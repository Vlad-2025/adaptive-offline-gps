using OsmSharp.Streams;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OsmSharp;
using Mapsui.Utilities;

namespace adaptive_offline_gps.RoutingEngine.RoadGraphBuilding
{
    public class RoadGraphBuilder
    {
        private static readonly Dictionary<string, float> SpeedMap = new()
        {
            //  need to add
            ["motorway"] = 130,
            ["trunk"] = 110,
            ["primary"] = 90,
            ["secondary"] = 70,
            ["tertiary"] = 50,
            ["residential"] = 30,
            ["living_street"] = 10,
            ["service"] = 20
        };

        public RoadGraph Build(string filePath)
        {
            var nodeIdToIndex = new Dictionary<long, int>();
            var nodeCoords = new List<(float lat, float lon)>();

            //  adjacency list before CSR
            var adjList = new List<(int to, float weight)>[0];  //  resize after node pass

            using var stream = File.OpenRead(filePath);
            var source = new PBFOsmStreamSource(stream);

            var usedNodeIds = new HashSet<long>();

            //  Pass 1: add nodes from highways
            foreach(var elem in source)
            {
                if (elem.Type != OsmGeoType.Way)
                    continue;

                var way = (Way)elem;

                if (way.Tags == null || !way.Tags.ContainsKey("highway"))
                    continue;

                if (!SpeedMap.ContainsKey(way.Tags["highway"]))
                    continue;

                foreach(var nid in way.Nodes)
                    usedNodeIds.Add(nid);
            }

            stream.Position = 0;
            source = new PBFOsmStreamSource(stream);

            //  Pass 2: assign compact indeces to used nodes
            foreach(var elem in source)
            {
                if(elem.Type != OsmGeoType.Node) 
                    continue;

                var node = (Node)elem;

                if (!node.Id.HasValue || !usedNodeIds.Contains(node.Id.Value))
                    continue;

                nodeIdToIndex[node.Id.Value] = nodeCoords.Count;

                nodeCoords.Add(((float)node.Latitude!.Value, (float)node.Longitude!.Value));
            }

            adjList = new List<(int, float)>[nodeCoords.Count];
            for (int i = 0; i < adjList.Length; i++)
                adjList[i] = new List<(int, float)>();

            stream.Position = 0;
            source = new PBFOsmStreamSource(stream);

            //  Pass 3: build edges
            foreach(var elem in source)
            {
                if (elem.Type != OsmGeoType.Way)
                    continue;

                var way = (Way)elem;

                if (way.Tags == null || !SpeedMap.TryGetValue(way.Tags.GetValue("highway") ?? "", out float speed))
                    continue;

                bool isOneWay = way.Tags.GetValue("oneway") == "yes";

                for(int i=0; i<way.Nodes.Length-1; i++)
                {
                    if (!nodeIdToIndex.TryGetValue(way.Nodes[i], out int a))
                        continue;
                    if (!nodeIdToIndex.TryGetValue(way.Nodes[i + 1], out int b))
                        continue;

                    float dist = Haversine(nodeCoords[a], nodeCoords[b]);
                    float cost = dist / (speed / 3.6f); //  m/s

                    adjList[a].Add((b, cost));

                    if(!isOneWay)
                        adjList[b].Add((a, cost));
                }
            }

            return CompressToCSR(nodeCoords, adjList);
        }

        private RoadGraph CompressToCSR(List<(float lat, float lon)> coords, List<(int to, float w)>[] adj)
        {
            int n = coords.Count;
            int totalEdges = adj.Sum(x => x.Count);

            var g = new RoadGraph
            {
                NodeLat     = new float[n],
                NodeLon     = new float[n],
                EdgeHead    = new int[n+1],
                EdgeTarget  = new int[totalEdges],
                EdgeWeight  = new float[totalEdges]
            };

            for(int i=0; i<n; i++)
            {
                g.NodeLat[i] = coords[i].lat;
                g.NodeLon[i] = coords[i].lon;
            }

            int cursor = 0;

            for(int i=0; i<n; i++)
            {
                g.EdgeHead[i] = cursor;

                foreach(var (to, w) in adj[i])
                {
                    g.EdgeTarget[cursor] = to;
                    g.EdgeWeight[cursor] = w;

                    cursor++;
                }
            }

            g.EdgeHead[n] = cursor;

            return g;
        }

        public static float Haversine((float lat, float lon) a, (float lat, float lon) b)
        {
            const float R = 6_378_100;  //  meters

            float dLat = (b.lat - a.lat) * MathF.PI / 180f;
            float dLon = (b.lon - a.lon) * MathF.PI / 180f;

            float sa = MathF.Sin(dLat / 2);
            float so = MathF.Sin(dLon / 2);

            float x = 
                sa * sa 
                + MathF.Cos(a.lat * MathF.PI / 180f) 
                * MathF.Cos(b.lat * MathF.PI / 180f) 
                * so * so;

            return R * 2f * MathF.Atan2(MathF.Sqrt(x), MathF.Sqrt(1 - x));
        }
    }
}
