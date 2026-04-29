using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace adaptive_offline_gps.RoutingEngine.RoadGraphBuilding
{
    public class CHPreprocessor
    {
        private List<CHEdge>[] _adj;       // forward: _adj[u] = edges u -> v
        private List<CHEdge>[] _revAdj;    // reverse: _revAdj[u] = edges v -> u, stored at u with .Target = v
        private bool[] _contracted;
        private int[] _level;
        private int[] _depth;
        private int _nodeCount;

        public CHGraph Preprocess(RoadGraph baseGraph)
        {
            var sw = Stopwatch.StartNew();

            _nodeCount = baseGraph.NodeCount;
            _contracted = new bool[_nodeCount];
            _level = new int[_nodeCount];
            _depth = new int[_nodeCount];

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

            var pq = new PriorityQueue<int, int>();
            for (int i = 0; i < _nodeCount; i++)
                pq.Enqueue(i, ComputePriority(i));

            int contractionOrder = 0;

            while (pq.Count > 0)
            {
                int node = pq.Dequeue();
                if (_contracted[node]) continue;

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

        // ===== Priority =====

        private int ComputePriority(int node)
        {
            GetLiveNeighbors(node, out var inEdges, out var outEdges);

            int edgesRemoved = inEdges.Count + outEdges.Count;
            int shortcutsAdded = CountShortcuts(node, inEdges, outEdges);
            int edgeDiff = shortcutsAdded - edgesRemoved;

            int contractedNeighbors = 0;
            foreach (var e in _adj[node])
                if (_contracted[e.Target]) contractedNeighbors++;
            foreach (var e in _revAdj[node])
                if (_contracted[e.Target]) contractedNeighbors++;

            return 2 * edgeDiff + 5 * contractedNeighbors + 2 * _depth[node];
        }

        private void GetLiveNeighbors(int node,
            out List<(int id, float weight)> inEdges,
            out List<(int id, float weight)> outEdges)
        {
            inEdges = new List<(int, float)>();
            outEdges = new List<(int, float)>();

            foreach (var e in _revAdj[node])
                if (!_contracted[e.Target]) inEdges.Add((e.Target, e.Weight));

            foreach (var e in _adj[node])
                if (!_contracted[e.Target]) outEdges.Add((e.Target, e.Weight));
        }

        private int CountShortcuts(int node,
            List<(int id, float weight)> inEdges,
            List<(int id, float weight)> outEdges)
        {
            if (inEdges.Count == 0 || outEdges.Count == 0) return 0;

            int count = 0;
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
                    float needed = wIn + wOut;
                    if (!witnesses.TryGetValue(w, out float found) || found > needed)
                        count++;
                }
            }
            return count;
        }

        // ===== Contraction =====

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
                        // If a shortcut u -> w already exists, keep the better one
                        // (avoids parallel-shortcut bloat that confuses query/unpacking).
                        ReplaceOrAddEdge(_adj[u], w, shortcutW, node);
                        ReplaceOrAddEdge(_revAdj[w], u, shortcutW, node);

                        int d = Math.Max(_depth[u], _depth[w]) + 1;
                        if (d > _depth[u]) _depth[u] = d;
                        if (d > _depth[w]) _depth[w] = d;
                    }
                }
            }
        }

        private static void ReplaceOrAddEdge(List<CHEdge> list, int target, float weight, int mid)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Target == target)
                {
                    if (weight < list[i].Weight)
                        list[i] = new CHEdge(target, weight, mid);
                    return;
                }
            }
            list.Add(new CHEdge(target, weight, mid));
        }

        // ===== Witness search =====

        private Dictionary<int, float> WitnessSearch(int source, int excludeNode, float maxWeight)
        {
            const int MaxSettled = 500;

            var dist = new Dictionary<int, float>(128) { [source] = 0 };
            var settled = new HashSet<int>(128);
            var pq = new PriorityQueue<int, float>();
            pq.Enqueue(source, 0);

            while (pq.Count > 0 && settled.Count < MaxSettled)
            {
                int u = pq.Dequeue();
                if (settled.Contains(u)) continue;
                if (!dist.TryGetValue(u, out float d)) continue;
                if (d > maxWeight) break;

                settled.Add(u);

                foreach (var e in _adj[u])
                {
                    if (e.Target == excludeNode || _contracted[e.Target]) continue;

                    float nd = d + e.Weight;
                    if (nd > maxWeight) continue;

                    if (!dist.TryGetValue(e.Target, out float old) || nd < old)
                    {
                        dist[e.Target] = nd;
                        pq.Enqueue(e.Target, nd);
                    }
                }
            }
            return dist;
        }

        // ===== assembly =====

        /// <summary>
        /// Walk every edge in the contracted graph exactly once and place it into
        /// ForwardUp / BackwardUp according to the up-edge invariant.
        /// 
        /// For each directed edge u -> v in _adj:
        ///   - if level[v] &gt; level[u]: it's an up-edge.
        ///       -> ForwardUp[u] gets (v, w, mid)         (forward search uses this)
        ///       -> BackwardUp[v] gets (u, w, mid)        (backward search at v can step to u)
        ///   - if level[u] &gt; level[v]: this is a DOWN-edge from u's perspective,
        ///     but viewed from v it's an INCOMING up-edge. By symmetry of the CH
        ///     contraction, the symmetric edge v -> u also exists in _adj (we built
        ///     _revAdj alongside _adj for exactly this reason — every shortcut was
        ///     added to both). So we'll see it as an up-edge when we process v's
        ///     out-list, and we don't add anything here.
        /// 
        /// This means we only act on up-edges and skip down-edges entirely,
        /// guaranteeing zero duplicates.
        /// </summary>
        private CHGraph BuildCHGraph(RoadGraph baseGraph)
        {
            int n = _nodeCount;
            var forwardUp = new List<CHEdge>[n];
            var backwardUp = new List<CHEdge>[n];

            for (int i = 0; i < n; i++)
            {
                forwardUp[i] = new List<CHEdge>();
                backwardUp[i] = new List<CHEdge>();
            }

            int fwdCount = 0, bwdCount = 0;

            for (int u = 0; u < n; u++)
            {
                foreach (var e in _adj[u])
                {
                    if (_level[e.Target] > _level[u])
                    {
                        forwardUp[u].Add(e);
                        fwdCount++;
                    }
                }

                foreach (var e in _revAdj[u])
                {
                    // _revAdj[u] holds entries (v, w) representing original edges v -> u.
                    // Backward search at u wants reverse-graph up-edges, i.e. those where
                    // level[v] > level[u]. Same edge data, different home.
                    if (_level[e.Target] > _level[u])
                    {
                        backwardUp[u].Add(e);
                        bwdCount++;
                    }
                }
            }

            Debug.WriteLine($"CH: {fwdCount} forward up-edges, {bwdCount} backward up-edges");

            // Note: fwdCount and bwdCount need NOT be equal. For a directed road graph
            // with antiparallel edges of different weights (typical OSM two-way streets
            // imported as two directed edges), an edge u->v and its companion v->u have
            // different weights and end up on different sides of the up/down split.
            // Specifically: edge u->v with level[v]>level[u] contributes to ForwardUp[u]
            // and BackwardUp[v]; the companion v->u is a down-edge in original direction
            // (filtered from ForwardUp) but is an up-edge in reverse from u's view, so
            // it contributes to BackwardUp[u] with no forward counterpart. Both lists
            // still contain only up-edges — the data structure is correct.

            for (int i = 0; i < n; i++)
            {
                forwardUp[i].TrimExcess();
                backwardUp[i].TrimExcess();
            }

            _adj = null;
            _revAdj = null;
            _contracted = null;
            _depth = null;

            return new CHGraph
            {
                BaseGraph = baseGraph,
                Level = _level,
                ForwardUp = forwardUp,
                BackwardUp = backwardUp
            };
        }
    }
}