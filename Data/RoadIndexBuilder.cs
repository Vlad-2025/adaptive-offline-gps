using Mapsui.Nts;
using Mapsui.Projections;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Union;
using NetTopologySuite.Simplify;
using OsmSharp;
using OsmSharp.Streams;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace adaptive_offline_gps.Data
{
    public class RoadIndexBuilder
    {
        public async Task<RoadSpatialIndex> BuildAsync(string filePath)
        {
            return await Task.Run(() =>
            {
                var index = new RoadSpatialIndex();
                var nodeDict = new Dictionary<long, Coordinate>();

                using var stream = File.OpenRead(filePath);
                var source = new PBFOsmStreamSource(stream);

                //  First pass: nodes
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

                var allMajorLowGeometries = new List<Geometry>();

                //  Second pass: ways
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

                    var simplifiedLow       = DouglasPeuckerSimplifier.Simplify(line, 300);

                    var simplifiedMedium    = DouglasPeuckerSimplifier.Simplify(line, 80);

                    var fullFeature     = new GeometryFeature { Geometry = line };
                    var lowFeature      = new GeometryFeature { Geometry = simplifiedLow };
                    var mediumFeature   = new GeometryFeature { Geometry = simplifiedMedium };

                    var highwayType = way.Tags["highway"];

                    if (highwayType is "motorway" or "trunk")
                    {
                        index.MajorFull.Insert(line.EnvelopeInternal, fullFeature);
                        index.MajorLow.Insert(simplifiedLow.EnvelopeInternal, lowFeature);

                        allMajorLowGeometries.Add(simplifiedLow);
                    }
                    else if (highwayType is "primary" or "secondary")
                    {
                        index.MediumFull.Insert(line.EnvelopeInternal, fullFeature);
                        index.MediumLow.Insert(simplifiedMedium.EnvelopeInternal, mediumFeature);
                    }
                    else
                    {
                        index.Minor.Insert(line.EnvelopeInternal, fullFeature);
                    }
                }

                //  This gives a 5-10fps increase at far zooms
                var factory = new GeometryFactory();

                var multiLine = factory.CreateMultiLineString(allMajorLowGeometries
                                                                .OfType<LineString>()
                                                                .ToArray());

                index.MergedMajorLow = new GeometryFeature
                {
                    Geometry = multiLine
                };

                index.BuildAll();
                return index;
            });
        }
    }
}
