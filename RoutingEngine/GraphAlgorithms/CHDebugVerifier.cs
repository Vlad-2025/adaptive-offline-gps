using System;
using System.Collections.Generic;
using System.Diagnostics;
using adaptive_offline_gps.RoutingEngine.RoadGraphBuilding;

namespace adaptive_offline_gps.RoutingEngine.GraphAlgorithms
{
    /// <summary>
    /// Debug helper: runs plain Dijkstra on the base graph and the CH query
    /// on the same (start,end), comparing results.
    /// Call this from MainWindow or anywhere to diagnose CH issues.
    /// </summary>
    public static class CHDebugVerifier
    {
        public static void Verify(RoadGraph graph, CHGraph ch, int start, int end)
        {
            Debug.WriteLine($"=== CH VERIFY: {start} -> {end} ===");

            //  1. Plain Dijkstra on base graph
            float dijkDist = RunDijkstra(graph, start, end, out int dijkVisited, out bool dijkFound);

            Debug.WriteLine($"Dijkstra: found={dijkFound}, dist={dijkDist:F2}, visited={dijkVisited}");

            //  2. Check connectivity
            Debug.WriteLine($"Start node {start}: degree={graph.EdgeHead[start + 1] - graph.EdgeHead[start]}");

            Debug.WriteLine($"End node {end}: degree={graph.EdgeHead[end + 1] - graph.EdgeHead[end]}");

            //  3. Check CH structure
            Debug.WriteLine($"Start ForwardUp count: {ch.ForwardUp[start].Count}, BackwardUp count: {ch.BackwardUp[start].Count}");

            Debug.WriteLine($"End ForwardUp count: {ch.ForwardUp[end].Count}, BackwardUp count: {ch.BackwardUp[end].Count}");

            Debug.WriteLine($"Start level: {ch.Level[start]}, End level: {ch.Level[end]}");

            //  4. Manual CH search with logging
            float chDist = RunCHSearch(ch, start, end, out int chVisited, out bool chFound, out int chMeetNode);

            Debug.WriteLine($"CH: found={chFound}, dist={chDist:F2}, visited={chVisited}, meet={chMeetNode}");

            if (dijkFound && chFound)
            {
                float ratio = chDist / dijkDist;

                Debug.WriteLine($"Ratio CH/Dijkstra: {ratio:F4} (should be ~1.0)");

                if (Math.Abs(ratio - 1.0f) > 0.01f)
                    Debug.WriteLine("WARNING: CH distance differs from Dijkstra!");
            }
            else if (dijkFound && !chFound)
            {
                Debug.WriteLine("BUG: Dijkstra found a path but CH did not!");
            }
            else if (!dijkFound)
            {
                Debug.WriteLine("No path exists in base graph either — nodes may be disconnected.");
            }

            Debug.WriteLine("=== END VERIFY ===");
        }

        private static float RunDijkstra(RoadGraph graph, int start, int end, out int visited, out bool found)
        {
            int n = graph.NodeCount;
            var dist = new float[n];
            Array.Fill(dist, float.MaxValue);
            dist[start] = 0;

            var settled = new bool[n];
            var pq = new PriorityQueue<int, float>();
            pq.Enqueue(start, 0);
            visited = 0;

            while (pq.Count > 0)
            {
                int u = pq.Dequeue();
                if (settled[u]) continue;
                settled[u] = true;
                visited++;

                if (u == end) break;

                for (int e = graph.EdgeHead[u]; e < graph.EdgeHead[u + 1]; e++)
                {
                    int v = graph.EdgeTarget[e];
                    float w = graph.EdgeWeight[e];
                    float nd = dist[u] + w;
                    if (nd < dist[v])
                    {
                        dist[v] = nd;
                        pq.Enqueue(v, nd);
                    }
                }
            }

            found = dist[end] < float.MaxValue;
            return dist[end];
        }

        private static float RunCHSearch(CHGraph ch, int start, int end,
            out int visited, out bool found, out int meetNode)
        {
            int n = ch.BaseGraph.NodeCount;

            var distF = new float[n]; Array.Fill(distF, float.MaxValue);
            var distB = new float[n]; Array.Fill(distB, float.MaxValue);
            var settledF = new bool[n];
            var settledB = new bool[n];

            var pqF = new PriorityQueue<int, float>();
            var pqB = new PriorityQueue<int, float>();

            distF[start] = 0;
            distB[end] = 0;
            pqF.Enqueue(start, 0);
            pqB.Enqueue(end, 0);

            float bestDist = float.MaxValue;
            meetNode = -1;
            visited = 0;

            int fwdSettled = 0, bwdSettled = 0;

            //  Mirrors CHQuery: step the side whose pq top is smaller, terminate when
            //  both tops are >= bestDist

            //  No level checks — every edge in ForwardUp / BackwardUp is up-directed by construction

            while (pqF.Count > 0 || pqB.Count > 0)
            {
                float minF = pqF.Count > 0 ? PeekP(pqF) : float.MaxValue;
                float minB = pqB.Count > 0 ? PeekP(pqB) : float.MaxValue;

                if (minF >= bestDist && minB >= bestDist) break;

                bool stepForward = minF <= minB;

                if (stepForward && pqF.Count > 0)
                {
                    int u = pqF.Dequeue();
                    if (settledF[u]) continue;
                    if (distF[u] >= bestDist) continue;

                    settledF[u] = true;
                    visited++;
                    fwdSettled++;

                    if (distB[u] < float.MaxValue)
                    {
                        float total = distF[u] + distB[u];
                        if (total < bestDist) { bestDist = total; meetNode = u; }
                    }

                    foreach (var e in ch.ForwardUp[u])
                    {
                        float nd = distF[u] + e.Weight;
                        if (nd < distF[e.Target])
                        {
                            distF[e.Target] = nd;
                            pqF.Enqueue(e.Target, nd);
                        }
                    }
                }
                else if (pqB.Count > 0)
                {
                    int u = pqB.Dequeue();
                    if (settledB[u]) continue;
                    if (distB[u] >= bestDist) continue;

                    settledB[u] = true;
                    visited++;
                    bwdSettled++;

                    if (distF[u] < float.MaxValue)
                    {
                        float total = distF[u] + distB[u];
                        if (total < bestDist) { bestDist = total; meetNode = u; }
                    }

                    foreach (var e in ch.BackwardUp[u])
                    {
                        float nd = distB[u] + e.Weight;
                        if (nd < distB[e.Target])
                        {
                            distB[e.Target] = nd;
                            pqB.Enqueue(e.Target, nd);
                        }
                    }
                }
            }

            Debug.WriteLine($"  CH search: fwdSettled={fwdSettled}, bwdSettled={bwdSettled}");

            found = meetNode != -1 && bestDist < float.MaxValue;

            return bestDist;
        }

        private static float PeekP(PriorityQueue<int, float> pq)
        {
            pq.TryPeek(out _, out float p);
            return p;
        }
    }
}