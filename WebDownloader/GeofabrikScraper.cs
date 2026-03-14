using HtmlAgilityPack;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace adaptive_offline_gps.WebDownloader
{
    public record GeofabrikRegion
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Parent { get; init; } = "";   //  parent region id
        public string? PbfUrl { get; init; }
        public long SizeBytes { get; init; }        //  -1 if unknown
        public bool HasChildren { get; init; }      //   true if subregions exist
    }

    public class GeofabrikScraper
    {
        private static readonly HttpClient _http = new();
        private const string IndexUrl = "https://download.geofabrik.de/index-v1.json";

        private List<GeofabrikRegion>? _allRegions;

        //  =================== Load the full index once
        public async Task<List<GeofabrikRegion>> LoadIndexAsync()
        {
            if (_allRegions != null) return _allRegions;

            using var response = await _http.GetAsync(IndexUrl,
                HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();
            var doc = await JsonDocument.ParseAsync(stream);

            var regions = new List<GeofabrikRegion>();
            var features = doc.RootElement
                .GetProperty("features");

            foreach (var feature in features.EnumerateArray())
            {
                var props = feature.GetProperty("properties");

                var id = props.GetProperty("id").GetString() ?? "";
                var name = props.GetProperty("name").GetString() ?? "";
                var parent = props.TryGetProperty("parent", out var p)
                    ? p.GetString() ?? "" : "";

                string? pbfUrl = null;
                if (props.TryGetProperty("urls", out var urls)
                    && urls.TryGetProperty("pbf", out var pbfProp))
                    pbfUrl = pbfProp.GetString();

                long sizeBytes = -1;
                if (props.TryGetProperty("pbf_size_bytes", out var sizeProp))
                    sizeBytes = sizeProp.GetInt64();

                regions.Add(new GeofabrikRegion
                {
                    Id = id,
                    Name = name,
                    Parent = parent,
                    PbfUrl = pbfUrl,
                    SizeBytes = sizeBytes,
                    HasChildren = false // set below
                });
            }

            //  Mark regions that have children (so UI can show " -> subregions" option)
            var parentIds = regions.Select(r => r.Parent).ToHashSet();
            _allRegions = regions
                .Select(r => r with { HasChildren = parentIds.Contains(r.Id) })
                .ToList();

            return _allRegions;
        }

        //  =================== Hierarchy helpers

        // Top-level continents (parent is empty or "")
        public async Task<List<GeofabrikRegion>> GetContinentsAsync()
        {
            var all = await LoadIndexAsync();
            return all
                .Where(r => string.IsNullOrEmpty(r.Parent))
                .OrderBy(r => r.Name)
                .ToList();
        }

        //  Direct children of a region
        public async Task<List<GeofabrikRegion>> GetChildrenAsync(string parentId)
        {
            var all = await LoadIndexAsync();
            return all
                .Where(r => r.Parent == parentId)
                .OrderBy(r => r.Name)
                .ToList();
        }
    }
}
