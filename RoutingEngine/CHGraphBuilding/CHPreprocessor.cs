using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace adaptive_offline_gps.RoutingEngine.RoadGraphBuilding
{
    public class CHPreprocessor
    {
        private List<CHEdge>[] _adj;
        private List<CHEdge>[] _revAdj;
        private bool[] _contracted;
        private int[] _level;
        private int[] _depth;       // max shortcut nesting depth per node
        private int _nodeCount;

        public CHGraph Preprocess(RoadGraph baseGraph)
        {
            var sw = Stopwatch.StartNew();

            _nodeCount = baseGraph.NodeCount;
            _contracted = new bool[_nodeCount];
            _level = new int[_nodeCount];
            _depth = new int[_nodeCount];

            //  Build mutable forward + reverse adjacency from CSR
            _adj = new List<CHEdge>[_nodeCount];
            _revAdj = new List<CHEdge>[_nodeCount];

            for (int i = 0; i < _nodeCount; i++)
            {
                _adj[i] = new List<CHEdge>();
                _revAdj[i] = new List<CHEdge>();
            }

            for (int u = 0; u < _nodeCount; u++)
            {
                for (int e = baseGraph.EdgeHead[u]; e < baseGraph.EdgeHead[u + 1]; e++)
                {
                    int v = baseGraph.EdgeTarget[e];
                    float w = baseGraph.EdgeWeight[e];
                    _adj[u].Add(new CHEdge(v, w, -1));
                    _revAdj[v].Add(new CHEdge(u, w, -1));
                }
            }

            Debug.WriteLine($"CH: {_nodeCount} nodes, {baseGraph.EdgeCount} edges");

            //  Initial priorities
            var pq = new PriorityQueue<int, int>();
            for (int i = 0; i < _nodeCount; i++)
                pq.Enqueue(i, ComputePriority(i));

            int contractionOrder = 0;

            while (pq.Count > 0)
            {
                int node = pq.Dequeue();
                if (_contracted[node]) continue;

                //  Lazy update
                int fresh = ComputePriority(node);
                if (pq.Count > 0)
                {
                    pq.TryPeek(out _, out int nextP);
                    if (fresh > nextP)
                    {
                        pq.Enqueue(node, fresh);
                        continue;
                    }
                }

                ContractNode(node);
                _contracted[node] = true;
                _level[node] = contractionOrder++;

                if (contractionOrder % 100000 == 0)
                    Debug.WriteLine($"CH: {contractionOrder}/{_nodeCount} ({100.0 * contractionOrder / _nodeCount:F1}%)");

                //  Re-enqueue affected neighbors
                foreach (var e in _adj[node])
                    if (!_contracted[e.Target])
                        pq.Enqueue(e.Target, ComputePriority(e.Target));

                foreach (var e in _revAdj[node])
                    if (!_contracted[e.Target])
                        pq.Enqueue(e.Target, ComputePriority(e.Target));
            }

            sw.Stop();
            Debug.WriteLine($"CH: done in {sw.ElapsedMilliseconds}ms");

            return BuildCHGraph(baseGraph);
        }

        //  PRIORITY

        private int ComputePriority(int node)
        {
            //  Collect live (uncontracted) in/out neighbors
            GetLiveNeighbors(node, out var inEdges, out var outEdges);

            int edgesRemoved = inEdges.Count + outEdges.Count;

            //  Simulate contraction: count shortcuts needed
            int shortcutsAdded = CountShortcuts(node, inEdges, outEdges);

            int edgeDiff = shortcutsAdded - edgesRemoved;

            //  Contracted neighbors
            int contractedNeighbors = 0;
            foreach (var e in _adj[node])
                if (_contracted[e.Target]) contractedNeighbors++;
            foreach (var e in _revAdj[node])
                if (_contracted[e.Target]) contractedNeighbors++;

            int depth = _depth[node];

            //  Geisberger "economical" variant weights
            return 2 * edgeDiff + 5 * contractedNeighbors + 2 * depth;
        }

        private void GetLiveNeighbors(int node,
            out List<(int id, float weight)> inEdges,
            out List<(int id, float weight)> outEdges)
        {
            inEdges = new List<(int, float)>();
            outEdges = new List<(int, float)>();

            foreach (var e in _revAdj[node])
                if (!_contracted[e.Target])
                    inEdges.Add((e.Target, e.Weight));

            foreach (var e in _adj[node])
                if (!_contracted[e.Target])
                    outEdges.Add((e.Target, e.Weight));
        }

        /// <summary>
        /// Count how many shortcuts would be needed if we contract this node.
        /// Uses batched witness search: one search per in-neighbor covers all out-neighbors.
        /// </summary>
        private int CountShortcuts(int node,
            List<(int id, float weight)> inEdges,
            List<(int id, float weight)> outEdges)
        {
            if (inEdges.Count == 0 || outEdges.Count == 0)
                return 0;

            int count = 0;

            foreach (var (u, wIn) in inEdges)
            {
                //  Max possible shortcut weight from u through node
                float maxW = 0;
                foreach (var (w, wOut) in outEdges)
                {
                    if (w == u) continue;
                    float sw = wIn + wOut;
                    if (sw > maxW) maxW = sw;
                }
                if (maxW == 0) continue;

                //  One witness search from u, limited to maxW
                var witnesses = WitnessSearch(u, node, maxW);

                foreach (var (w, wOut) in outEdges)
                {
                    if (w == u) continue;
                    float needed = wIn + wOut;
                    if (!witnesses.TryGetValue(w, out float found) || found > needed)
                        count++;
                }
            }

            return count;
        }

        //  CONTRACTION

        private void ContractNode(int node)
        {
            GetLiveNeighbors(node, out var inEdges, out var outEdges);

            foreach (var (u, wIn) in inEdges)
            {
                float maxW = 0;
                foreach (var (w, wOut) in outEdges)
                {
                    if (w == u) continue;
                    float sw = wIn + wOut;
                    if (sw > maxW) maxW = sw;
                }
                if (maxW == 0) continue;

                var witnesses = WitnessSearch(u, node, maxW);

                foreach (var (w, wOut) in outEdges)
                {
                    if (w == u) continue;
                    float shortcutW = wIn + wOut;

                    if (!witnesses.TryGetValue(w, out float witDist) || witDist > shortcutW)
                    {
                        _adj[u].Add(new CHEdge(w, shortcutW, node));
                        _revAdj[w].Add(new CHEdge(u, shortcutW, node));

                        // Update depth
                        int d = Math.Max(_depth[u], _depth[w]) + 1;
                        if (d > _depth[u]) _depth[u] = d;
                        if (d > _depth[w]) _depth[w] = d;
                    }
                }
            }
        }

        //  WITNESS SEARCH

        /// <summary>
        /// Batched local Dijkstra from source, avoiding excludeNode,
        /// bounded by maxWeight. Returns shortest distances to all
        /// reachable nodes within the limit.
        /// 
        /// Uses proper settled-set tracking to avoid processing nodes
        /// multiple times.
        /// </summary>
        private Dictionary<int, float> WitnessSearch(int source, int excludeNode, float maxWeight)
        {
            //  Hop limit scales with graph density, road networks are sparse
            //  so 500 settled nodes covers a large local area
            const int MaxSettled = 500;

            var dist = new Dictionary<int, float>(128) { [source] = 0 };
            var settled = new HashSet<int>(128);
            var pq = new PriorityQueue<int, float>();
            pq.Enqueue(source, 0);

            while (pq.Count > 0 && settled.Count < MaxSettled)
            {
                int u = pq.Dequeue();

                //  Skip already settled
                if (settled.Contains(u))
                    continue;

                //  Get current best distance
                if (!dist.TryGetValue(u, out float d))
                    continue;

                //  Beyond our weight limit - stop
                if (d > maxWeight)
                    break;

                settled.Add(u);

                foreach (var e in _adj[u])
                {
                    if (e.Target == excludeNode || _contracted[e.Target])
                        continue;

                    float nd = d + e.Weight;
                    if (nd > maxWeight)
                        continue;

                    if (!dist.TryGetValue(e.Target, out float old) || nd < old)
                    {
                        dist[e.Target] = nd;
                        pq.Enqueue(e.Target, nd);
                    }
                }
            }

            return dist;
        }
        //  BUILD FINAL CH GRAPH

        private CHGraph BuildCHGraph(RoadGraph baseGraph)
        {
            int n = _nodeCount;

            var upAdj = new List<CHEdge>[n];
            var downAdj = new List<CHEdge>[n];

            for (int i = 0; i < n; i++)
            {
                upAdj[i] = new List<CHEdge>();
                downAdj[i] = new List<CHEdge>();
            }

            int upCount = 0, downCount = 0;

            for (int u = 0; u < n; u++)
            {
                foreach (var e in _adj[u])
                {
                    if (_level[e.Target] > _level[u])
                    {
                        upAdj[u].Add(e);
                        upCount++;

                        downAdj[e.Target].Add(new CHEdge(u, e.Weight, e.ShortcutMid));
                        downCount++;
                    }
                    else if (_level[u] > _level[e.Target])
                    {
                        downAdj[e.Target].Add(new CHEdge(u, e.Weight, e.ShortcutMid));
                        downCount++;
                    }
                }
            }

            Debug.WriteLine($"CH: {upCount} up-edges, {downCount} down-edges");

            for (int i = 0; i < n; i++)
            {
                upAdj[i].TrimExcess();
                downAdj[i].TrimExcess();
            }

            //  Free preprocessing data
            _adj = null;
            _revAdj = null;
            _contracted = null;
            _depth = null;

            return new CHGraph
            {
                BaseGraph = baseGraph,
                Level = _level,
                UpAdj = upAdj,
                DownAdj = downAdj
            };
        }
    }
}