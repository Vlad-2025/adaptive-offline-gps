using HtmlAgilityPack;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace adaptive_offline_gps.WebDownloader
{
    public record GeofabrikRegion
    {
        public string Name { get; init; } = "";
        public string PageUrl { get; init; } = "";   // .html page for drilling in
        public string? PbfUrl { get; init; }         // direct .osm.pbf download, null if continent
        public long SizeBytes { get; init; } = -1;
        public bool HasChildren { get; init; }
    }

    public class GeofabrikScraper
    {
        private static readonly HttpClient _http = new();
        private const string BaseUrl = "https://download.geofabrik.de/";

        //  Scrape any Geofabrik page and return its subregion rows
        public async Task<List<GeofabrikRegion>> GetRegionsAsync(string pageUrl)
        {
            var html = await FetchHtmlAsync(pageUrl);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var regions = new List<GeofabrikRegion>();

            var rows = doc.DocumentNode
                .SelectNodes("//table[@id='subregions']//tr");

            if (rows == null) return regions;

            foreach (var row in rows)
            {
                var cells = row.SelectNodes(".//td");
                if (cells == null || cells.Count < 2) 
                    continue;

                //  Cell 0: region name + link to its .html page
                var nameCell = cells[0];
                var pageLink = nameCell.SelectSingleNode(".//a[@href]");
                if (pageLink == null) 
                    continue;

                var name = pageLink.InnerText.Trim();
                var pageHref = pageLink.GetAttributeValue("href", "");
                var fullPageUrl = MakeAbsolute(pageHref, pageUrl);

                //  Cell 1: .osm.pbf download link (may not exist for continents)
                string? pbfUrl = null;
                var pbfLink = cells[1].SelectSingleNode(".//a[@href]");
                if (pbfLink != null)
                {
                    var pbfHref = pbfLink.GetAttributeValue("href", "");
                    if (pbfHref.EndsWith(".osm.pbf"))
                        pbfUrl = MakeAbsolute(pbfHref, pageUrl);
                }

                //  Cell 2: size like "(817&nbsp;MB)" - may be missing
                long sizeBytes = -1;

                if (cells.Count > 2)
                {
                    var sizeText = System.Net.WebUtility.HtmlDecode(cells[2].InnerText)
                        .Trim('(', ')', ' ', '\u00a0');

                    sizeBytes = ParseSize(sizeText);
                }

                /*  A region has children if its page link goes to an .html page
                    We determine this by checking if the page itself has a subregions table
                    - but that would require an extra request per row, so instead we
                    use a heuristic: if pbfUrl is null OR the name is a known large region,
                    mark as potentially having children. We confirm lazily when user drills in.
                */

                bool hasChildren = fullPageUrl.EndsWith(".html") && pbfUrl != null;
                //  For continents (no pbf at this level), always has children
                if (pbfUrl == null) 
                    hasChildren = true;

                regions.Add(new GeofabrikRegion
                {
                    Name = name,
                    PageUrl = fullPageUrl,
                    PbfUrl = pbfUrl,
                    SizeBytes = sizeBytes,
                    HasChildren = hasChildren,
                });
            }

            return regions;
        }

        //  Check if a region page actually has subregions
        //  Called lazily when user tries to drill in, avoids N+1 requests on load
        public async Task<bool> HasSubregionsAsync(string pageUrl)
        {
            var html = await FetchHtmlAsync(pageUrl);
            var doc = new HtmlDocument();

            doc.LoadHtml(html);

            var table = doc.DocumentNode.SelectSingleNode("//table[@id='subregions']");

            return table != null;
        }

        //  Parse the "File size: 31.8 GB" from a region's own page
        //  Used as a fallback when the size isn't in the parent table cell
        public async Task<long> GetPageFileSizeAsync(string pageUrl)
        {
            var html = await FetchHtmlAsync(pageUrl);

            //  "File size: 31.8 GB" or "File size: 732 MB"
            var match = Regex.Match(html,
                @"File size:\s*([\d.]+)\s*(GB|MB|KB)",
                RegexOptions.IgnoreCase);

            if (!match.Success) 
                return -1;

            if (!double.TryParse(
                match.Groups[1].Value,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out double value)) 

                return -1;

            return match.Groups[2].Value.ToUpperInvariant() switch
            {
                "GB" => (long)(value * 1024 * 1024 * 1024),
                "MB" => (long)(value * 1024 * 1024),
                "KB" => (long)(value * 1024),
                _ => -1
            };
        }

        //  Helpers
        private static async Task<string> FetchHtmlAsync(string url)
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();

            using var reader = new StreamReader(stream);

            return await reader.ReadToEndAsync();
        }

        private static string MakeAbsolute(string href, string contextPageUrl)
        {
            if (href.StartsWith("http")) 
                return href;

            //  href is relative to the context page's directory
            //  e.g. context = "https://download.geofabrik.de/europe.html"
            //      href    = "europe/romania.html"
            //      result  = "https://download.geofabrik.de/europe/romania.html"

            var baseUri     = new Uri(contextPageUrl);
            var baseDir     = new Uri(baseUri, ".");
            var resolved    = new Uri(baseDir, href);

            return resolved.ToString();
        }

        private static long ParseSize(string text)
        {
            //  "817 MB", "38.0 MB", "1.5 GB", "732 MB"
            var parts = text.Split(new[] { ' ', '\u00a0' },
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return -1;

            if (!double.TryParse(parts[0],
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out double value)) return -1;

            return parts[1].ToUpperInvariant() switch
            {
                "GB" => (long)(value * 1024 * 1024 * 1024),
                "MB" => (long)(value * 1024 * 1024),
                "KB" => (long)(value * 1024),
                _ => -1
            };
        }
    }
}