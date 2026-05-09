using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using adaptive_offline_gps.RoutingEngine.GraphAlgorithms;
using adaptive_offline_gps.RoutingEngine.RoadGraphBuilding;

namespace adaptive_offline_gps.RoutingEngine.GraphAlgorithms.Algorithms
{
    public class CHQuery
    {
        private readonly CHGraph _ch;

        public CHQuery(CHGraph ch)
        {
            _ch = ch;
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

            //  For each settled node, remember the EDGE that reached it:
            //  predecessor node and the shortcut-mid of that edge (or -1 for original edges)
            var prevNodeF = new int[n]; Array.Fill(prevNodeF, -1);
            var prevMidF  = new int[n]; Array.Fill(prevMidF, -1);
            var prevNodeB = new int[n]; Array.Fill(prevNodeB, -1);
            var prevMidB  = new int[n]; Array.Fill(prevMidB, -1);

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

                float minF = pqF.Count > 0 ? PeekPriority(pqF) : float.MaxValue;
                float minB = pqB.Count > 0 ? PeekPriority(pqB) : float.MaxValue;

                if (minF >= bestDist && minB >= bestDist) 
                    break;

                bool stepForward = minF <= minB;

                if (stepForward && pqF.Count > 0)
                {
                    int u = pqF.Dequeue();
                    if (settledF[u]) 
                        continue;

                    if (distF[u] >= bestDist) 
                        continue;

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

                    foreach (var e in _ch.ForwardUp[u])
                    {
                        float nd = distF[u] + e.Weight;

                        if (nd < distF[e.Target])
                        {
                            distF[e.Target] = nd;
                            prevNodeF[e.Target] = u;
                            prevMidF[e.Target] = e.ShortcutMid;
                            pqF.Enqueue(e.Target, nd);
                        }
                    }
                }
                else if (pqB.Count > 0)
                {
                    int u = pqB.Dequeue();
                    if (settledB[u]) 
                        continue;

                    if (distB[u] >= bestDist) 
                        continue;

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

                    //  Each entry in BackwardUp[u] represents an original edge (e.Target -> u)
                    //  Backward search relaxes this edge in reverse, updating dist for e.Target
                    foreach (var e in _ch.BackwardUp[u])
                    {
                        float nd = distB[u] + e.Weight;

                        if (nd < distB[e.Target])
                        {
                            distB[e.Target] = nd;
                            prevNodeB[e.Target] = u;
                            prevMidB[e.Target] = e.ShortcutMid;
                            pqB.Enqueue(e.Target, nd);
                        }
                    }
                }
            }

            sw.Stop();

            if (meetNode == -1 || bestDist >= float.MaxValue)
            {
                await visualizer.OnNoPathFound();
                return RoutingResult.NoPath;
            }

            //  Reconstruct unpacked path directly
            //  Recursively unpack each edge as we walk the predecessor chains
            var unpacked = ReconstructPath(start, end, meetNode, prevNodeF, prevMidF, prevNodeB, prevMidB);

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

        //  Alternatives ===================

        /// <summary>
        /// Find the optimal route plus up to (maxAlternatives) alternative routes.
        /// Result[0] is always the optimal. Result[1..n] are the accepted alternatives
        /// in order of increasing cost. If no alternative passes the filters, the
        /// returned array has length 1.
        ///
        /// Strategy: extend the bidirectional CH search past the first meeting until
        /// no candidate via-node within stretch range can still appear, then collect
        /// every node settled by both searches as a candidate alternative meeting
        /// point. Reconstruct paths through the most promising candidates and filter
        /// by stretch / sharing using RouteAlternativesScorer.
        /// </summary>
        public async Task<RoutingResult[]> FindAlternativePathsAsync(
            int start, int end,
            int maxAlternatives,
            RoutingOptions options,
            IRoutingVisualizer visualizer,
            float searchRelaxation = 1.5f,
            float maxStretch = 0.25f,
            float maxSharing = 0.80f)
        {
            var sw = Stopwatch.StartNew();
            int n = _ch.BaseGraph.NodeCount;

            var distF = new float[n]; Array.Fill(distF, float.MaxValue);
            var distB = new float[n]; Array.Fill(distB, float.MaxValue);

            var prevNodeF = new int[n]; Array.Fill(prevNodeF, -1);
            var prevMidF  = new int[n]; Array.Fill(prevMidF, -1);
            var prevNodeB = new int[n]; Array.Fill(prevNodeB, -1);
            var prevMidB  = new int[n]; Array.Fill(prevMidB, -1);

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

            //  Track every node settled by BOTH searches
            //  These are valid via-points for a complete start->via->end path
            //  distF[v] + distB[v] is the cost
            //  Using a HashSet avoids duplicates as nodes are settled by either side
            var bothSettled = new HashSet<int>();

            //  Search loop
            //  Same as FindPathAsync but with a relaxed termination:
            //      continue until no via-candidate within (searchRelaxation × bestDist) can still be discovered

            //  The bound minF + minB > limit captures the fact that any new via-candidate v has cost ≥ minF + minB
            while (pqF.Count > 0 || pqB.Count > 0)
            {
                options.CancellationToken.ThrowIfCancellationRequested();

                float minF = pqF.Count > 0 ? PeekPriority(pqF) : float.MaxValue;
                float minB = pqB.Count > 0 ? PeekPriority(pqB) : float.MaxValue;

                //  Relaxed termination: don't stop at first meeting, keep going
                //  until no candidate within stretch range is reachable
                float candidateLimit = bestDist < float.MaxValue
                    ? bestDist * searchRelaxation
                    : float.MaxValue;

                if (minF >= candidateLimit && minB >= candidateLimit) 
                    break;

                bool stepForward = minF <= minB;

                if (stepForward && pqF.Count > 0)
                {
                    int u = pqF.Dequeue();
                    if (settledF[u]) 
                        continue;

                    if (distF[u] >= candidateLimit) 
                        continue;

                    settledF[u] = true;
                    visited++;

                    if (!options.BenchmarkMode) 
                        await visualizer.OnNodeVisited(u);

                    if (settledB[u]) 
                        bothSettled.Add(u);

                    if (distB[u] < float.MaxValue)
                    {
                        float total = distF[u] + distB[u];
                        if (total < bestDist) 
                        { 
                            bestDist = total; 
                            meetNode = u; 
                        }
                    }

                    foreach (var e in _ch.ForwardUp[u])
                    {
                        float nd = distF[u] + e.Weight;

                        if (nd < distF[e.Target])
                        {
                            distF[e.Target] = nd;
                            prevNodeF[e.Target] = u;
                            prevMidF[e.Target] = e.ShortcutMid;
                            pqF.Enqueue(e.Target, nd);
                        }
                    }
                }
                else if (pqB.Count > 0)
                {
                    int u = pqB.Dequeue();
                    if (settledB[u]) 
                        continue;

                    if (distB[u] >= candidateLimit) 
                        continue;

                    settledB[u] = true;
                    visited++;

                    if (!options.BenchmarkMode) 
                        await visualizer.OnNodeVisited(u);

                    if (settledF[u]) 
                        bothSettled.Add(u);

                    if (distF[u] < float.MaxValue)
                    {
                        float total = distF[u] + distB[u];
                        if (total < bestDist) 
                        { 
                            bestDist = total; 
                            meetNode = u; 
                        }
                    }

                    foreach (var e in _ch.BackwardUp[u])
                    {
                        float nd = distB[u] + e.Weight;
                        if (nd < distB[e.Target])
                        {
                            distB[e.Target] = nd;
                            prevNodeB[e.Target] = u;
                            prevMidB[e.Target] = e.ShortcutMid;
                            pqB.Enqueue(e.Target, nd);
                        }
                    }
                }
            }

            if (meetNode == -1 || bestDist >= float.MaxValue)
            {
                await visualizer.OnNoPathFound();
                sw.Stop();

                return new[] 
                { 
                    RoutingResult.NoPath 
                };
            }

            //  Reconstruct optimal route
            int[] optimalPath = ReconstructPath(start, end, meetNode, prevNodeF, prevMidF, prevNodeB, prevMidB);
            double optimalDistance = ComputePathDistance(optimalPath);

            var optimalResult = new RoutingResult
            {
                Path = optimalPath,
                TotalCost = bestDist,
                NodesVisited = visited,
                EdgeRelaxations = 0,
                ElapsedMs = sw.ElapsedMilliseconds,
                DistanceMetres = optimalDistance,
                PathFound = true
            };

            //  Build candidate list: every bi-settled node within stretch range, excluding the meet node itself
            //  Sort by via-cost ascending so the filter sees the most promising candidates first
            float stretchLimit = bestDist * (1.0f + maxStretch);
            var candidates = new List<(int via, float cost)>();

            foreach (int v in bothSettled)
            {
                if (v == meetNode) 
                    continue;
                float c = distF[v] + distB[v];

                if (c <= stretchLimit) 
                    candidates.Add((v, c));
            }
            candidates.Sort((a, b) => a.cost.CompareTo(b.cost));

            Debug.WriteLine($"Alternatives: bothSettled={bothSettled.Count}, " +
                            $"within-stretch candidates={candidates.Count}, " +
                            $"optimalCost={bestDist:F2}, stretchLimit={stretchLimit:F2}");

            //  Reconstruct each candidate path, then filter
            //  Reconstructing all of them upfront is fine, its just array walks plus shortcut unpacking
            //  For 50-200 candidates this is milliseconds
            var reconstructed = new List<(int[] path, float cost)>(candidates.Count);
            foreach (var (via, cost) in candidates)
            {
                int[] path = ReconstructPath(start, end, via,
                                             prevNodeF, prevMidF, prevNodeB, prevMidB);
                reconstructed.Add((path, cost));
            }

            var scorer = new RouteAlternativesScorer(_ch.BaseGraph)
            {
                MaxStretch = maxStretch,
                MaxSharing = maxSharing
            };
            var accepted = scorer.Filter(optimalPath, bestDist, reconstructed, maxAlternatives);

            sw.Stop();

            Debug.WriteLine($"Alternatives: {accepted.Count} accepted out of {reconstructed.Count} reconstructed " +
                            $"(elapsed {sw.ElapsedMilliseconds}ms)");

            //  Build result array: optimal at index 0, alternatives after
            var results = new RoutingResult[1 + accepted.Count];
            results[0] = optimalResult;
            for (int i = 0; i < accepted.Count; i++)
            {
                int[] altPath = accepted[i].path;
                results[i + 1] = new RoutingResult
                {
                    Path = altPath,
                    TotalCost = accepted[i].cost,
                    NodesVisited = visited,            // shared search; not meaningful per-alt
                    EdgeRelaxations = 0,
                    ElapsedMs = sw.ElapsedMilliseconds, // total elapsed
                    DistanceMetres = ComputePathDistance(altPath),
                    PathFound = true
                };
            }

            //  Notify visualizer with the optimal path
            //  Alternatives are typically rendered separately by the caller using the returned array;
            //  the visualizer interface only knows about a single primary path.
            await visualizer.OnPathFound(optimalPath);

            return results;
        }

        //  Reconstruction ===============================

        private int[] ReconstructPath(
            int start, int end, int meet,
            int[] prevNodeF, int[] prevMidF,
            int[] prevNodeB, int[] prevMidB)
        {
            //  Gather forward "edges" (each represented by its target node and the mid of the edge that settled it)
            //  Walk meet -> start, then reverse
            var fwdEdges = new List<(int from, int to, int mid)>();
            for (int c = meet; prevNodeF[c] != -1; c = prevNodeF[c])
                fwdEdges.Add((prevNodeF[c], c, prevMidF[c]));
            fwdEdges.Reverse();

            //  Backward relaxation at u finds entry e in BackwardUp[u] with e.Target = v,
            //  representing original edge v -> u. The relaxation sets prevNodeB[v] = u

            //  So prevNodeB[v] = u corresponds to original edge v -> u
            //
            //  Walking from meet outward: c, prevNodeB[c], prevNodeB[prevNodeB[c]], ..., end
            //  Each pair (c, prevNodeB[c]) corresponds to original edge c -> prevNodeB[c]

            //  (which is what we want for the meet -> end traversal in original-edge order)
            var bwdEdges = new List<(int from, int to, int mid)>();
            for (int c = meet; prevNodeB[c] != -1; c = prevNodeB[c])
                bwdEdges.Add((c, prevNodeB[c], prevMidB[c]));

            // Build the unpacked path.
            var result = new List<int> { start };

            foreach (var (from, to, mid) in fwdEdges)
                UnpackEdge(from, to, mid, result, isForward: true);

            foreach (var (from, to, mid) in bwdEdges)
                UnpackEdge(from, to, mid, result, isForward: false);

            Debug.WriteLine($"ReconstructPath: fwdEdges={fwdEdges.Count}, bwdEdges={bwdEdges.Count}, " +
                            $"unpacked nodes={result.Count}");

            return result.ToArray();
        }

        // Unpack a single edge (from -> to) with the given shortcut mid

        // For forward edges: the edge IS in ForwardUp[from] with .ShortcutMid = mid
        // (the relaxation that set it)

        // For backward edges: the edge represents original direction from -> to; the relaxation
        //   was on the entry e in BackwardUp[to] with e.Target = from and e.ShortcutMid = mid
        // 
        // To recurse, we need to find sub-edges (from -> mid) and (mid -> to)
        // Their mids come from the structures: we look them up in ForwardUp/BackwardUp
        private void UnpackEdge(int from, int to, int mid, List<int> result, bool isForward)
        {
            if (mid == -1)
            {
                result.Add(to);
                return;
            }

            //  Find the (from -> mid) edge and the (mid -> to) edge in the CH structure and recurse
            //  Each can be in ForwardUp or BackwardUp depending on level ordering
            var (m1, ok1) = FindEdgeMid(from, mid);
            if (!ok1)
            {
                Debug.WriteLine($"UnpackEdge: cannot find sub-edge ({from}->{mid}) of shortcut ({from}->{to} mid={mid})");
                result.Add(to);
                return;
            }

            var (m2, ok2) = FindEdgeMid(mid, to);
            if (!ok2)
            {
                Debug.WriteLine($"UnpackEdge: cannot find sub-edge ({mid}->{to}) of shortcut ({from}->{to} mid={mid})");
                result.Add(to);
                return;
            }

            UnpackEdge(from, mid, m1, result, isForward);
            UnpackEdge(mid, to, m2, result, isForward);
        }

        // Find the ShortcutMid for the directed edge (from -> to) by searching the CH adjacency lists
        // Returns (mid, true) if found, (default, false) otherwise
        //
        // The edge from -> to is stored as:
        //  - ForwardUp[from] entry with .Target == to,   IF level[to] > level[from]
        //  - BackwardUp[to]  entry with .Target == from, IF level[from] > level[to]

        // We pick the right list based on level comparison and scan it.
        private (int mid, bool ok) FindEdgeMid(int from, int to)
        {
            if (_ch.Level[to] > _ch.Level[from])
            {
                //  Up-edge: in ForwardUp[from]
                var list = _ch.ForwardUp[from];
                for (int i = 0; i < list.Count; i++)
                    if (list[i].Target == to) return (list[i].ShortcutMid, true);
            }
            else
            {
                //  Down-edge: in BackwardUp[to] (entries are original edges into to)
                var list = _ch.BackwardUp[to];
                for (int i = 0; i < list.Count; i++)
                    if (list[i].Target == from) return (list[i].ShortcutMid, true);
            }
            return (-1, false);
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