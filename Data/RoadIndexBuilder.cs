using Mapsui.Nts;
using Mapsui.Projections;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.Operation.Linemerge;
using NetTopologySuite.Operation.Union;
using NetTopologySuite.Simplify;
using OsmSharp;
using OsmSharp.Streams;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace adaptive_offline_gps.Data
{
    public class RoadIndexBuilder
    {
        public async Task<RoadSpatialIndex> BuildAsync
        (
            string filePath,
            IProgress<string>? progress = null
        )
        {
            return await Task.Run(() =>
            {
                var index = new RoadSpatialIndex();
                var nodeDict = new Dictionary<long, Coordinate>();

                using var stream = File.OpenRead(filePath);
                var source = new PBFOsmStreamSource(stream);

                //  First pass: nodes
                progress?.Report("reading nodes...");
                foreach (var element in source)
                {
                    if (element.Type == OsmGeoType.Node)
                    {
                        var node = (Node)element;

                        if (node.Id.HasValue
                            && node.Longitude.HasValue
                            && node.Latitude.HasValue)
                        {
                            var mercator = SphericalMercator.FromLonLat(
                                node.Longitude.Value,
                                node.Latitude.Value);

                            nodeDict[node.Id.Value] = new Coordinate(mercator.x, mercator.y);
                        }
                    }
                }
                stream.Position = 0;
                source = new PBFOsmStreamSource(stream);

                var majorWays  = new List<Geometry>();
                var mediumWays = new List<Geometry>();
                var minorWays  = new List<Geometry>();

                //  Second pass: ways
                progress?.Report("reading ways...");
                foreach (var element in source)
                {
                    if (element.Type != OsmGeoType.Way)
                        continue;

                    var way = (Way)element;

                    if (way.Tags == null || !way.Tags.ContainsKey("highway"))
                        continue;

                    var coords = new List<Coordinate>();

                    foreach (var nodeId in way.Nodes)
                    {
                        if (nodeDict.TryGetValue(nodeId, out var coord))
                            coords.Add(coord);
                    }

                    if (coords.Count < 2)
                        continue;

                    var line = new LineString(coords.ToArray());

                    var highwayType = way.Tags["highway"];

                    if (highwayType is "motorway" or "trunk")
                    {
                        majorWays.Add(line);
                    }
                    else if (highwayType is "primary" or "secondary")
                    {
                        mediumWays.Add(line);
                    }
                    else
                    {
                        minorWays.Add(line);
                    }
                }

                //  I tried using parallel threads, not much changed

                progress?.Report("merging geometries...");

                InsertMerged(index.MajorFull,   index.MajorLow,     majorWays, 300);
                InsertMerged(index.MediumFull,  index.MediumLow,    mediumWays, 80);

                var minorMerger = new LineMerger();

                foreach(var g in minorWays)
                    minorMerger.Add(g);

                foreach(LineString merged in minorMerger.GetMergedLineStrings().Cast<LineString>())
                {
                    var f = new GeometryFeature { Geometry = merged };

                    index.Minor.Insert(merged.EnvelopeInternal, f);
                }

                Debug.WriteLine($"major:  {majorWays.Count} ways -> {index.MajorFull.Count} merged");
                Debug.WriteLine($"medium: {mediumWays.Count} ways -> {index.MediumFull.Count} merged");
                Debug.WriteLine($"minor:  {minorWays.Count} ways -> {index.Minor.Count} merged");

                index.BuildAll();

                return index;
            });
    }
    private void InsertMerged(
        STRtree<GeometryFeature> fullTree,
        STRtree<GeometryFeature> lowTree,
        List<Geometry> ways,
        double lowTolerance
        )
        {
            var merger = new LineMerger();

            foreach (var w in ways)
                merger.Add(w);

            foreach (LineString merged in merger.GetMergedLineStrings().Cast<LineString>())
            {
                var simplified = DouglasPeuckerSimplifier.Simplify(merged, lowTolerance);

                fullTree.Insert(merged.EnvelopeInternal, new GeometryFeature { Geometry = merged });

                lowTree.Insert(simplified.EnvelopeInternal, new GeometryFeature { Geometry = simplified });
            }
        }
    }
}
