using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using adaptive_offline_gps.RoutingEngine.GraphAlgorithms;

namespace adaptive_offline_gps.RoutingEngine.RoadGraphBuilding
{
    public class CHQuery
    {
        private readonly CHGraph _ch;

        //  Lookup: (from, to) -> CHEdge for ALL edges in the CH (up and down directions)
        //  Built once, used for every query's unpacking step.
        private readonly Dictionary<(int, int), CHEdge> _edgeLookup;

        public CHQuery(CHGraph ch)
        {
            _ch = ch;
            _edgeLookup = BuildEdgeLookup(ch);
        }

        /// <summary>
        /// Builds a lookup of all directed edges in the CH.
        /// For every upward edge u -> v, we also store the reverse v -> u
        /// with the same shortcutMid, so unpacking works in both directions.
        /// </summary>
        private static Dictionary<(int, int), CHEdge> BuildEdgeLookup(CHGraph ch)
        {
            var lookup = new Dictionary<(int, int), CHEdge>();

            //  Index all UpAdj edges + reverses
            for (int u = 0; u < ch.UpAdj.Length; u++)
            {
                foreach (var e in ch.UpAdj[u])
                {
                    var key = (u, e.Target);
                    if (!lookup.TryGetValue(key, out var existing) || e.Weight < existing.Weight)
                        lookup[key] = e;

                    var revKey = (e.Target, u);
                    if (!lookup.TryGetValue(revKey, out var revExisting) || e.Weight < revExisting.Weight)
                        lookup[revKey] = new CHEdge(u, e.Weight, e.ShortcutMid);
                }
            }

            //  Index all DownAdj edges + reverses
            //  These include edges from the else-if branch not present in UpAdj
            for (int u = 0; u < ch.DownAdj.Length; u++)
            {
                foreach (var e in ch.DownAdj[u])
                {
                    var key = (u, e.Target);
                    if (!lookup.TryGetValue(key, out var existing) || e.Weight < existing.Weight)
                        lookup[key] = e;

                    var revKey = (e.Target, u);
                    if (!lookup.TryGetValue(revKey, out var revExisting) || e.Weight < revExisting.Weight)
                        lookup[revKey] = new CHEdge(u, e.Weight, e.ShortcutMid);
                }
            }

            return lookup;
        }

        public async Task<RoutingResult> FindPathAsync(
            int start, int end,
            RoutingOptions options,
            IRoutingVisualizer visualizer)
        {
            var sw = Stopwatch.StartNew();

            int n = _ch.BaseGraph.NodeCount;

            var distF = new float[n]; Array.Fill(distF, float.MaxValue);
            var distB = new float[n]; Array.Fill(distB, float.MaxValue);
            var prevF = new int[n]; Array.Fill(prevF, -1);
            var prevB = new int[n]; Array.Fill(prevB, -1);

            var settledF = new bool[n];
            var settledB = new bool[n];

            var pqF = new PriorityQueue<int, float>();
            var pqB = new PriorityQueue<int, float>();

            distF[start] = 0;
            distB[end] = 0;
            pqF.Enqueue(start, 0);
            pqB.Enqueue(end, 0);

            float bestDist = float.MaxValue;
            int meetNode = -1;
            int visited = 0;

            while (pqF.Count > 0 || pqB.Count > 0)
            {
                options.CancellationToken.ThrowIfCancellationRequested();

                //  Forward step: UpAdj (edges to higher-level nodes)
                if (pqF.Count > 0)
                {
                    int u = pqF.Dequeue();

                    if (!settledF[u] && distF[u] < bestDist)
                    {
                        settledF[u] = true;
                        visited++;

                        if (!options.BenchmarkMode)
                            await visualizer.OnNodeVisited(u);

                        if (distB[u] < float.MaxValue)
                        {
                            float total = distF[u] + distB[u];
                            if (total < bestDist)
                            {
                                bestDist = total;
                                meetNode = u;
                            }
                        }

                        foreach (var e in _ch.UpAdj[u])
                        {
                            float nd = distF[u] + e.Weight;
                            if (nd < distF[e.Target])
                            {
                                distF[e.Target] = nd;
                                prevF[e.Target] = u;
                                pqF.Enqueue(e.Target, nd);
                            }
                        }
                    }
                }
                if (pqB.Count > 0)
                {
                    int u = pqB.Dequeue();

                    if (!settledB[u] && distB[u] < bestDist)
                    {
                        settledB[u] = true;
                        visited++;

                        if (!options.BenchmarkMode)
                            await visualizer.OnNodeVisited(u);

                        if (distF[u] < float.MaxValue)
                        {
                            float total = distF[u] + distB[u];
                            if (total < bestDist)
                            {
                                bestDist = total;
                                meetNode = u;
                            }
                        }

                        foreach (var e in _ch.DownAdj[u])
                        {
                            //  Only follow edges going upward (to higher-level nodes)
                            if (_ch.Level[e.Target] <= _ch.Level[u])
                                continue;

                            float nd = distB[u] + e.Weight;
                            if (nd < distB[e.Target])
                            {
                                distB[e.Target] = nd;
                                prevB[e.Target] = u;
                                pqB.Enqueue(e.Target, nd);
                            }
                        }
                    }
                }

                //  Termination
                float minF = pqF.Count > 0 ? PeekPriority(pqF) : float.MaxValue;
                float minB = pqB.Count > 0 ? PeekPriority(pqB) : float.MaxValue;

                if (minF >= bestDist && minB >= bestDist)
                    break;
            }

            sw.Stop();

            if (meetNode == -1 || bestDist >= float.MaxValue)
            {
                await visualizer.OnNoPathFound();
                return RoutingResult.NoPath;
            }

            //  Build packed path
            //  Forward: start -> meetNode
            var forwardPacked = new List<int>();
            int c = meetNode;
            while (c != -1) { forwardPacked.Add(c); c = prevF[c]; }
            forwardPacked.Reverse();

            //  Backward: meetNode -> end
            var backwardPacked = new List<int>();
            c = prevB[meetNode];
            while (c != -1) { backwardPacked.Add(c); c = prevB[c]; }

            //  Full packed path
            var packedPath = new List<int>(forwardPacked);
            packedPath.AddRange(backwardPacked);

            //  Unpack
            var unpacked = UnpackPath(packedPath.ToArray());

            double distanceMeters = ComputePathDistance(unpacked);
            await visualizer.OnPathFound(unpacked);

            return new RoutingResult
            {
                Path = unpacked,
                TotalCost = bestDist,
                NodesVisited = visited,
                EdgeRelaxations = 0,
                ElapsedMs = sw.ElapsedMilliseconds,
                DistanceMetres = distanceMeters,
                PathFound = true
            };
        }

        /// <summary>
        /// Takes consecutive node pairs and recursively unpacks shortcuts.
        /// </summary>
        private int[] UnpackPath(int[] packedPath)
        {
            if (packedPath.Length < 2)
                return packedPath;

            var result = new List<int> { packedPath[0] };

            for (int i = 0; i < packedPath.Length - 1; i++)
                UnpackEdge(packedPath[i], packedPath[i + 1], result);

            return result.ToArray();
        }

        /// <summary>
        /// Recursively unpack edge from u to v.
        /// Uses the edge lookup which contains both directions.
        /// Appends all nodes after u (u is already in result).
        /// </summary>
        private void UnpackEdge(int u, int v, List<int> result)
        {
            if (!_edgeLookup.TryGetValue((u, v), out var edge))
            {
                result.Add(v);
                return;
            }

            if (edge.ShortcutMid == -1)
            {
                //  Original edge
                result.Add(v);
                return;
            }

            int mid = edge.ShortcutMid;

            //  Shortcut u -> v goes through mid.
            //  Recursively unpack u -> mid, then mid -> v.
            UnpackEdge(u, mid, result);
            UnpackEdge(mid, v, result);
        }

        private static float PeekPriority(PriorityQueue<int, float> pq)
        {
            pq.TryPeek(out _, out float p);
            return p;
        }

        private double ComputePathDistance(int[] path)
        {
            var g = _ch.BaseGraph;
            double total = 0;

            for (int i = 0; i < path.Length - 1; i++)
            {
                total += RoadGraphBuilder.Haversine(
                    (g.NodeLat[path[i]], g.NodeLon[path[i]]),
                    (g.NodeLat[path[i + 1]], g.NodeLon[path[i + 1]]));
            }

            return total;
        }
    }
}