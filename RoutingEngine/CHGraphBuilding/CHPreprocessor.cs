using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace adaptive_offline_gps.RoutingEngine.RoadGraphBuilding
{
    /// <summary>
    /// Builds a contraction hierarchy from an existing RoadGraph.
    /// 
    /// Maintains both forward (adj) and reverse (revAdj) adjacency lists
    /// so that finding in-edges for a node is O(degree) instead of O(N).
    /// This makes the overall preprocessing O(N log N) for typical road networks.
    /// </summary>
    public class CHPreprocessor
    {
        private List<CHEdge>[] _adj;
        private List<CHEdge>[] _revAdj;
        private bool[] _contracted;
        private int[] _level;
        private int _nodeCount;

        public CHGraph Preprocess(RoadGraph baseGraph)
        {
            var sw = Stopwatch.StartNew();

            _nodeCount = baseGraph.NodeCount;
            _contracted = new bool[_nodeCount];
            _level = new int[_nodeCount];

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

            Debug.WriteLine($"CH: built adjacency for {_nodeCount} nodes, {baseGraph.EdgeCount} edges");

            //  Initial priority for all nodes
            var pq = new PriorityQueue<int, int>();
            var cachedPriority = new int[_nodeCount];

            for (int i = 0; i < _nodeCount; i++)
            {
                cachedPriority[i] = ComputePriority(i);
                pq.Enqueue(i, cachedPriority[i]);
            }

            //  Contract nodes in priority order (lazy updates)
            int contractionOrder = 0;

            while (pq.Count > 0)
            {
                int node = pq.Dequeue();

                if (_contracted[node])
                    continue;

                //  Lazy update: recompute priority and check if still best
                int freshPriority = ComputePriority(node);

                if (pq.Count > 0)
                {
                    pq.TryPeek(out _, out int nextPriority);

                    if (freshPriority > nextPriority)
                    {
                        //  Not the best anymore — re-enqueue with updated priority
                        pq.Enqueue(node, freshPriority);
                        continue;
                    }
                }

                //  Contract this node
                ContractNode(node);
                _contracted[node] = true;
                _level[node] = contractionOrder++;

                if (contractionOrder % 50000 == 0)
                    Debug.WriteLine($"CH: contracted {contractionOrder}/{_nodeCount} " +
                                    $"({100.0 * contractionOrder / _nodeCount:F1}%)");

                //  Update priorities of uncontracted neighbors
                //  (they may have gained/lost shortcuts)
                foreach (var e in _adj[node])
                {
                    if (!_contracted[e.Target])
                    {
                        cachedPriority[e.Target] = ComputePriority(e.Target);
                        pq.Enqueue(e.Target, cachedPriority[e.Target]);
                    }
                }

                foreach (var e in _revAdj[node])
                {
                    if (!_contracted[e.Target])
                    {
                        cachedPriority[e.Target] = ComputePriority(e.Target);
                        pq.Enqueue(e.Target, cachedPriority[e.Target]);
                    }
                }
            }

            sw.Stop();
            Debug.WriteLine($"CH: preprocessing done in {sw.ElapsedMilliseconds}ms");

            return BuildCHGraph(baseGraph);
        }

        /// <summary>
        /// Priority heuristic based on Geisberger et al.
        /// 
        /// Components (weighted):
        ///   1. Edge difference: shortcuts_added - edges_removed
        ///      Higher = more shortcuts = worse candidate → contract later
        ///   2. Contracted neighbors: how many neighbors are already contracted
        ///      Higher = contract sooner (to spread contraction evenly)
        ///   3. Shortcut cover: for how many neighbor pairs does contracting this
        ///      node require a shortcut? Penalizes high-degree junction nodes.
        ///   4. Node depth: maximum depth of shortcuts touching this node.
        ///      Prevents deep nesting of shortcuts which hurts unpacking.
        /// </summary>
        private int ComputePriority(int node)
        {
            //  In-edges from uncontracted neighbors
            var inNeighbors = new List<(int from, float weight)>();
            foreach (var e in _revAdj[node])
            {
                if (!_contracted[e.Target])
                    inNeighbors.Add((e.Target, e.Weight));
            }

            //  Out-edges to uncontracted neighbors
            var outNeighbors = new List<(int to, float weight)>();
            foreach (var e in _adj[node])
            {
                if (!_contracted[e.Target])
                    outNeighbors.Add((e.Target, e.Weight));
            }

            int edgesRemoved = inNeighbors.Count + outNeighbors.Count;

            //  Count shortcuts needed + track which neighbors are "covered"
            int shortcutsAdded = 0;
            var coveredNeighbors = new HashSet<int>();

            foreach (var (u, wIn) in inNeighbors)
            {
                foreach (var (w, wOut) in outNeighbors)
                {
                    if (u == w) continue;

                    float shortcutWeight = wIn + wOut;

                    if (!HasWitness(u, w, node, shortcutWeight))
                    {
                        shortcutsAdded++;
                        coveredNeighbors.Add(u);
                        coveredNeighbors.Add(w);
                    }
                }
            }

            int edgeDiff = shortcutsAdded - edgesRemoved;

            //  Contracted neighbors,  want to spread contraction evenly
            int contractedNeighbors = 0;
            foreach (var e in _adj[node])
                if (_contracted[e.Target])
                    contractedNeighbors++;

            foreach (var e in _revAdj[node])
                if (_contracted[e.Target])
                    contractedNeighbors++;

            //  Node depth: max shortcut depth among edges touching this node
            //  Prevents creating deeply nested shortcuts
            int depth = 0;
            foreach (var e in _adj[node])
                if (e.ShortcutMid != -1) depth++;
            foreach (var e in _revAdj[node])
                if (e.ShortcutMid != -1) depth++;

            //  Weighted combination - these weights are from the Geisberger paper
            //  Edge difference is the most important factor (weight 190)
            //  Contracted neighbors and depth help break ties and spread contraction
            return 190 * edgeDiff
                 + 120 * contractedNeighbors
                 + 1 * coveredNeighbors.Count
                 + 60 * depth;
        }

        /// <summary>
        /// Contract a node: for every pair (in-neighbor u, out-neighbor w),
        /// if no witness path u -> w exists that avoids this node,
        /// add a shortcut u -> w.
        /// </summary>
        private void ContractNode(int node)
        {
            var inEdges = new List<(int from, float weight)>();
            foreach (var e in _revAdj[node])
            {
                if (!_contracted[e.Target])
                    inEdges.Add((e.Target, e.Weight));
            }

            var outEdges = new List<(int to, float weight)>();
            foreach (var e in _adj[node])
            {
                if (!_contracted[e.Target])
                    outEdges.Add((e.Target, e.Weight));
            }

            foreach (var (u, wIn) in inEdges)
            {
                foreach (var (w, wOut) in outEdges)
                {
                    if (u == w) continue;

                    float shortcutWeight = wIn + wOut;

                    if (!HasWitness(u, w, node, shortcutWeight))
                    {
                        //  Add shortcut u -> w, recording that it goes through 'node'
                        var sc = new CHEdge(w, shortcutWeight, node);
                        _adj[u].Add(sc);

                        var scRev = new CHEdge(u, shortcutWeight, node);
                        _revAdj[w].Add(scRev);
                    }
                }
            }
        }

        /// <summary>
        /// Limited local Dijkstra from source to target, ignoring excludeNode.
        /// Returns true if a path ≤ maxWeight exists (= no shortcut needed).
        /// Settled node limit keeps this fast.
        /// </summary>
        private bool HasWitness(int source, int target, int excludeNode, float maxWeight)
        {
            const int MaxSettled = 300;

            //  Using a dictionary for the small local search 
            //  (much cheaper than allocating a full-size array per call)
            var dist = new Dictionary<int, float>(64) { [source] = 0 };
            var pq = new PriorityQueue<int, float>();
            pq.Enqueue(source, 0);

            int settled = 0;

            while (pq.Count > 0 && settled < MaxSettled)
            {
                int u = pq.Dequeue();

                if (!dist.TryGetValue(u, out float d))
                    continue;

                //  Check for stale entries
                if (d > maxWeight)
                    return false;

                if (u == target)
                    return true;

                settled++;

                //  Mark as processed by removing (prevents re-processing)
                foreach (var e in _adj[u])
                {
                    if (e.Target == excludeNode || _contracted[e.Target])
                        continue;

                    float nd = d + e.Weight;

                    if (nd > maxWeight)
                        continue;

                    if (!dist.TryGetValue(e.Target, out float oldDist) || nd < oldDist)
                    {
                        dist[e.Target] = nd;
                        pq.Enqueue(e.Target, nd);
                    }
                }
            }

            return false;
        }

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
                        //  Upward edge u -> v: forward search follows this
                        upAdj[u].Add(e);
                        upCount++;

                        //  Reversed for backward search: from v, reach u
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

            //  Trim excess capacity
            for (int i = 0; i < n; i++)
            {
                upAdj[i].TrimExcess();
                downAdj[i].TrimExcess();
            }

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