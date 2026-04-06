using OsmSharp.Streams;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using OsmSharp;

namespace adaptive_offline_gps.RoutingEngine.RoadGraphBuilding
{
    public class RoadGraphBuilder
    {
        private static readonly Dictionary<string, float> SpeedMap = new()
        {
            ["motorway"] = 130,
            ["trunk"] = 110,
            ["primary"] = 90,
            ["secondary"] = 70,
            ["tertiary"] = 50,
            ["residential"] = 30,
            ["living_street"] = 10,
            ["service"] = 20
        };

        /// <summary>
        /// All road node coordinates, indexed by their compact "all-node" index.
        /// Stored separately so NodeGridIndex can index every road node for click snapping.
        /// </summary>
        public float[] AllNodeLat { get; private set; }
        public float[] AllNodeLon { get; private set; }

        /// <summary>
        /// Maps each all-node index to the edge it sits on: (junctionA, junctionB, fractionAlongEdge).
        /// For junction nodes, maps to themselves. Used by NodeGridIndex to snap clicks to edges.
        /// </summary>
        public int[] AllNodeNearestJunction { get; private set; }

        public RoadGraph Build(string filePath)
        {
            // ── Pass 1: scan ways, count node references to find junctions ──
            var nodeRefCount = new Dictionary<long, int>();
            var wayEndpoints = new HashSet<long>();

            using var stream = File.OpenRead(filePath);
            var source = new PBFOsmStreamSource(stream);

            foreach (var elem in source)
            {
                if (elem.Type != OsmGeoType.Way) continue;
                var way = (Way)elem;
                if (way.Tags == null || !way.Tags.ContainsKey("highway")) continue;
                if (!SpeedMap.ContainsKey(way.Tags["highway"])) continue;

                for (int i = 0; i < way.Nodes.Length; i++)
                {
                    long nid = way.Nodes[i];
                    if (nodeRefCount.ContainsKey(nid))
                        nodeRefCount[nid]++;
                    else
                        nodeRefCount[nid] = 1;
                }

                if (way.Nodes.Length > 0)
                {
                    wayEndpoints.Add(way.Nodes[0]);
                    wayEndpoints.Add(way.Nodes[^1]);
                }
            }

            // A node is a junction (tower node) if:
            //   - referenced by 2+ ways (intersection), OR
            //   - start/end of any way (dead end, way boundary)
            var junctionOsmIds = new HashSet<long>();
            foreach (var (nid, count) in nodeRefCount)
            {
                if (count > 1 || wayEndpoints.Contains(nid))
                    junctionOsmIds.Add(nid);
            }

            int totalRoadNodes = nodeRefCount.Count;
            Debug.WriteLine($"RoadGraph: {totalRoadNodes} road nodes, {junctionOsmIds.Count} junctions " +
                            $"({100.0 * junctionOsmIds.Count / totalRoadNodes:F1}% are junctions)");

            nodeRefCount = null;
            wayEndpoints = null;

            // ── Pass 2: read coordinates for ALL road nodes ──
            // Junction nodes get compact junction IDs.
            // All nodes get compact all-node IDs (for NodeGridIndex).
            var junctionIndex = new Dictionary<long, int>();    // osmId → junction compact ID
            var junctionCoords = new List<(float lat, float lon)>();

            var allNodeIndex = new Dictionary<long, int>();     // osmId → all-node compact ID  
            var allLat = new List<float>();
            var allLon = new List<float>();

            // We also need osmId → (lat, lon) for pillar nodes during pass 3
            var allNodeCoords = new Dictionary<long, (float lat, float lon)>();

            stream.Position = 0;
            source = new PBFOsmStreamSource(stream);

            foreach (var elem in source)
            {
                if (elem.Type != OsmGeoType.Node) continue;
                var node = (Node)elem;
                if (!node.Id.HasValue || !node.Latitude.HasValue || !node.Longitude.HasValue)
                    continue;

                long id = node.Id.Value;

                // Skip nodes not on any road
                if (!junctionOsmIds.Contains(id))
                {
                    // Check if it's a road node at all (was in nodeRefCount)
                    // We can't check nodeRefCount anymore, so use allNodeCoords as gate:
                    // Actually we need a set of all road node IDs. Let's use a different approach.
                    // We'll just store all nodes we encounter and filter later.
                    // For now, store in allNodeCoords unconditionally — pass 1 already told us
                    // which nodes are on roads. But we cleared nodeRefCount...
                    // 
                    // Fix: we need the set of all road node IDs. Let's keep it.
                    continue;
                }

                float lat = (float)node.Latitude.Value;
                float lon = (float)node.Longitude.Value;

                allNodeCoords[id] = (lat, lon);

                // All-node index (for grid)
                allNodeIndex[id] = allLat.Count;
                allLat.Add(lat);
                allLon.Add(lon);

                // Junction index (for routing graph)
                if (junctionOsmIds.Contains(id))
                {
                    junctionIndex[id] = junctionCoords.Count;
                    junctionCoords.Add((lat, lon));
                }
            }

            // Hmm, we skipped non-junction road nodes above. We need them for:
            // 1. allNodeCoords (pillar geometry in pass 3)
            // 2. AllNodeLat/Lon (NodeGridIndex for click snapping)
            // Let's re-do pass 2 properly.

            // Actually, let me restructure. We need ALL road node IDs from pass 1.
            // Let me redo this cleanly.

            return BuildInternal(filePath, junctionOsmIds);
        }

        private RoadGraph BuildInternal(string filePath, HashSet<long> junctionOsmIds)
        {
            // ── Pass 1 (repeat): collect all road node IDs ──
            var allRoadNodeIds = new HashSet<long>();

            using var stream = File.OpenRead(filePath);
            var source = new PBFOsmStreamSource(stream);

            foreach (var elem in source)
            {
                if (elem.Type != OsmGeoType.Way) continue;
                var way = (Way)elem;
                if (way.Tags == null || !way.Tags.ContainsKey("highway")) continue;
                if (!SpeedMap.ContainsKey(way.Tags["highway"])) continue;

                foreach (var nid in way.Nodes)
                    allRoadNodeIds.Add(nid);
            }

            // ── Pass 2: read coordinates ──
            var junctionIndex = new Dictionary<long, int>();
            var junctionCoords = new List<(float lat, float lon)>();

            var allNodeCoords = new Dictionary<long, (float lat, float lon)>();
            var allNodeCompactIndex = new Dictionary<long, int>();
            var allLat = new List<float>();
            var allLon = new List<float>();

            stream.Position = 0;
            source = new PBFOsmStreamSource(stream);

            foreach (var elem in source)
            {
                if (elem.Type != OsmGeoType.Node) continue;
                var node = (Node)elem;
                if (!node.Id.HasValue || !node.Latitude.HasValue || !node.Longitude.HasValue)
                    continue;

                long id = node.Id.Value;
                if (!allRoadNodeIds.Contains(id))
                    continue;

                float lat = (float)node.Latitude.Value;
                float lon = (float)node.Longitude.Value;

                allNodeCoords[id] = (lat, lon);
                allNodeCompactIndex[id] = allLat.Count;
                allLat.Add(lat);
                allLon.Add(lon);

                if (junctionOsmIds.Contains(id))
                {
                    junctionIndex[id] = junctionCoords.Count;
                    junctionCoords.Add((lat, lon));
                }
            }

            allRoadNodeIds = null; // free

            Debug.WriteLine($"RoadGraph: {allLat.Count} total road nodes, {junctionCoords.Count} junction nodes");

            // ── Pass 3: build edges between junctions with pillar geometry ──
            // Each OSM way is split at junction nodes into segments.
            // Each segment becomes one edge in the graph.

            var adjList = new List<(int to, float weight, float[] geomLat, float[] geomLon)>[junctionCoords.Count];
            for (int i = 0; i < adjList.Length; i++)
                adjList[i] = new List<(int, float, float[], float[])>();

            // Also track which junction each all-node is nearest to
            var allNodeNearestJunc = new int[allLat.Count];
            // Initialize: junction nodes map to themselves
            foreach (var (osmId, jIdx) in junctionIndex)
            {
                if (allNodeCompactIndex.TryGetValue(osmId, out int allIdx))
                    allNodeNearestJunc[allIdx] = jIdx;
            }

            stream.Position = 0;
            source = new PBFOsmStreamSource(stream);

            foreach (var elem in source)
            {
                if (elem.Type != OsmGeoType.Way) continue;
                var way = (Way)elem;
                if (way.Tags == null || !SpeedMap.TryGetValue(way.Tags.GetValue("highway") ?? "", out float speed))
                    continue;

                bool isOneWay = way.Tags.GetValue("oneway") == "yes";

                // Walk the way's nodes, splitting at junctions
                var segmentOsmIds = new List<long>();

                foreach (long nid in way.Nodes)
                {
                    segmentOsmIds.Add(nid);

                    // When we hit a junction and have accumulated a segment, emit it
                    if (junctionOsmIds.Contains(nid) && segmentOsmIds.Count >= 2)
                    {
                        EmitSegment(segmentOsmIds, speed, isOneWay,
                            allNodeCoords, junctionIndex, junctionCoords,
                            allNodeCompactIndex, allNodeNearestJunc,
                            adjList);

                        // Start new segment from this junction
                        segmentOsmIds.Clear();
                        segmentOsmIds.Add(nid);
                    }
                }

                // Handle leftover (way ended without hitting a second junction)
                if (segmentOsmIds.Count >= 2)
                {
                    EmitSegment(segmentOsmIds, speed, isOneWay,
                        allNodeCoords, junctionIndex, junctionCoords,
                        allNodeCompactIndex, allNodeNearestJunc,
                        adjList);
                }
            }

            // Store all-node arrays for NodeGridIndex
            AllNodeLat = allLat.ToArray();
            AllNodeLon = allLon.ToArray();
            AllNodeNearestJunction = allNodeNearestJunc;

            return CompressToCSR(junctionCoords, adjList);
        }

        private void EmitSegment(
            List<long> segmentOsmIds,
            float speed,
            bool isOneWay,
            Dictionary<long, (float lat, float lon)> allNodeCoords,
            Dictionary<long, int> junctionIndex,
            List<(float lat, float lon)> junctionCoords,
            Dictionary<long, int> allNodeCompactIndex,
            int[] allNodeNearestJunc,
            List<(int to, float weight, float[] geomLat, float[] geomLon)>[] adjList)
        {
            long firstOsm = segmentOsmIds[0];
            long lastOsm = segmentOsmIds[^1];

            if (!junctionIndex.TryGetValue(firstOsm, out int juncA)) return;
            if (!junctionIndex.TryGetValue(lastOsm, out int juncB)) return;
            if (juncA == juncB) return; // self-loop

            // Compute total distance and collect pillar geometry
            float totalDist = 0;
            var pillarLat = new List<float>();
            var pillarLon = new List<float>();

            for (int i = 0; i < segmentOsmIds.Count; i++)
            {
                long nid = segmentOsmIds[i];

                if (!allNodeCoords.TryGetValue(nid, out var pos))
                    continue;

                // Distance accumulation
                if (i > 0)
                {
                    var prev = allNodeCoords[segmentOsmIds[i - 1]];
                    totalDist += Haversine(prev, pos);
                }

                // Pillar nodes = everything except first and last (which are junctions)
                if (i > 0 && i < segmentOsmIds.Count - 1)
                {
                    pillarLat.Add(pos.lat);
                    pillarLon.Add(pos.lon);

                    // Map this all-node to the nearest junction (pick closer of A or B)
                    if (allNodeCompactIndex.TryGetValue(nid, out int allIdx))
                    {
                        // Simple heuristic: assign to juncA for first half, juncB for second
                        allNodeNearestJunc[allIdx] = (i < segmentOsmIds.Count / 2) ? juncA : juncB;
                    }
                }
            }

            float cost = totalDist / (speed / 3.6f);

            var gLat = pillarLat.ToArray();
            var gLon = pillarLon.ToArray();

            adjList[juncA].Add((juncB, cost, gLat, gLon));

            if (!isOneWay)
            {
                // Reverse geometry for the reverse edge
                var revLat = gLat.Reverse().ToArray();
                var revLon = gLon.Reverse().ToArray();
                adjList[juncB].Add((juncA, cost, revLat, revLon));
            }
        }

        private RoadGraph CompressToCSR(
            List<(float lat, float lon)> coords,
            List<(int to, float weight, float[] geomLat, float[] geomLon)>[] adj)
        {
            int n = coords.Count;
            int totalEdges = adj.Sum(x => x.Count);

            var g = new RoadGraph
            {
                NodeLat = new float[n],
                NodeLon = new float[n],
                EdgeHead = new int[n + 1],
                EdgeTarget = new int[totalEdges],
                EdgeWeight = new float[totalEdges],
                EdgeGeomLat = new float[totalEdges][],
                EdgeGeomLon = new float[totalEdges][]
            };

            for (int i = 0; i < n; i++)
            {
                g.NodeLat[i] = coords[i].lat;
                g.NodeLon[i] = coords[i].lon;
            }

            int cursor = 0;

            for (int i = 0; i < n; i++)
            {
                g.EdgeHead[i] = cursor;

                foreach (var (to, w, gLat, gLon) in adj[i])
                {
                    g.EdgeTarget[cursor] = to;
                    g.EdgeWeight[cursor] = w;
                    g.EdgeGeomLat[cursor] = gLat;
                    g.EdgeGeomLon[cursor] = gLon;
                    cursor++;
                }
            }

            g.EdgeHead[n] = cursor;

            return g;
        }

        public static float Haversine((float lat, float lon) a, (float lat, float lon) b)
        {
            const float R = 6_378_100;

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