using OsmSharp;
using OsmSharp.Streams;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace adaptive_offline_gps.WebDownloader
{
    public class OsmRoadExtractor
    {
        private static readonly HashSet<string> RoadTypes = new()
        {
            "motorway", "trunk", "primary", "secondary", "tertiary",
            "residential", "living_street", "service", "unclassified",
            "motorway_link", "trunk_link", "primary_link"
        };
        
        public async Task ExtractRoadsAsync(
            string inputPbf,
            string outputPbf,
            IProgress<double>? progress = null)
        {
            await Task.Run(() =>
            {

                //  Pass 1: find nodes that are part of ways
                var usedNodeIds = new HashSet<long>();

                using(var stream = File.OpenRead(inputPbf))
                {
                    var source1 = new PBFOsmStreamSource(stream);

                    foreach(var element in source1)
                    {
                        if (element.Type != OsmGeoType.Way)
                            continue;

                        var way = (Way) element;

                        if (way.Tags == null || !way.Tags.ContainsKey("highway"))
                            continue;

                        if (!RoadTypes.Contains(way.Tags["highway"]))
                            continue;

                       foreach(var nid in way.Nodes)
                            usedNodeIds.Add(nid);
                    }
                }
                progress?.Report(0.5);

                using var inStream = File.OpenRead(inputPbf);
                using var outStream = File.OpenWrite(outputPbf);

                var source2 = new PBFOsmStreamSource(inStream);
                var target = new PBFOsmStreamTarget(outStream);

                target.Initialize();

                foreach (var element in source2)
                {
                    switch (element.Type)
                    {
                        case OsmGeoType.Node:
                            var node = (Node)element;

                            if (node.Id.HasValue && usedNodeIds.Contains(node.Id.Value))
                                target.AddNode(node);

                            break;

                        case OsmGeoType.Way:
                            var way = (Way)element;

                            if (way.Tags?.ContainsKey("highway") == true && RoadTypes.Contains(way.Tags["highway"]))
                                target.AddWay(way);

                            break;
                    }
                }

                target.Flush();
                target.Close();

                progress?.Report(1.0);
            });
        }
    }
}
