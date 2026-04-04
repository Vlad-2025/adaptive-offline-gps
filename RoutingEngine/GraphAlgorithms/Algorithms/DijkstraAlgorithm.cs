using adaptive_offline_gps.RoutingEngine.RoadGraphBuilding;
using Mapsui.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace adaptive_offline_gps.RoutingEngine.GraphAlgorithms.Algorithms
{
    public class DijkstraAlgorithm : IRoutingAlgorithm
    {
        public string Name => "Dijkstra";

        async Task<RoutingResult> IRoutingAlgorithm.FindPathAsync(
            RoadGraph graph, 
            int startNode, 
            int endNode, 
            IEdgeWeightProvider weights, 
            RoutingOptions options, 
            IRoutingVisualizer visualizer)
        {

            var sw = Stopwatch.StartNew();

            var dist = new float[graph.NodeCount];  Array.Fill(dist, float.MaxValue);
            var prev = new int[graph.NodeCount];    Array.Fill(prev, -1);

            //  (cost, nodeIndex)
            var pqueue = new PriorityQueue<int, float>();

            dist[startNode] = 0f;

            pqueue.Enqueue(startNode, 0);

            int visited = 0;
            int relaxed = 0;

            //  Stops the algorithm from re-processing duplicate entries
            var settled = new bool[graph.NodeCount];

            while(pqueue.Count > 0)
            {
                options.CancellationToken.ThrowIfCancellationRequested();

                int node = pqueue.Dequeue();

                //  Duplicate -> skip
                if (settled[node])
                    continue;

                settled[node] = true;

                if (!options.BenchmarkMode)
                    await visualizer.OnNodeVisited(node);   //  fires only for unsettled nodes

                visited++;

                if (node == endNode)
                    break;

                for(int e = graph.EdgeHead[node]; e < graph.EdgeHead[node + 1]; e++)
                {
                    int neighbour = graph.EdgeTarget[e];
                    float edgeCost = weights.GetWeight(e, graph.EdgeWeight[e]);

                    //  Skip road works to avoid float overflow
                    if (edgeCost >= float.MaxValue)
                        continue;

                    float cost = dist[node] + edgeCost;

                    if(cost < dist[neighbour])
                    {
                        dist[neighbour] = cost;
                        prev[neighbour] = node;

                        pqueue.Enqueue(neighbour, cost);

                        await visualizer.OnEdgeRelaxed(node, neighbour);

                        relaxed++;
                    }
                }
            }

            sw.Stop();

            //  Check if destination was reached
            if (dist[endNode] >= float.MaxValue)
            {
                await visualizer.OnNoPathFound();

                return RoutingResult.NoPath;
            }

            //  Reconstruct path by walking prev backwards
            //  -> start at endNode, follow prev until we reach startNode
            var pathList = new List<int>();
            int current = endNode;

            while(current != -1)
            {
                pathList.Add(current);
                current = prev[current];
            }

            //  Path is end->start, reverse it
            pathList.Reverse();
            int[] path = pathList.ToArray();

            //  Compute path distance
            double distanceMeters = ComputePathDistance(graph, path);

            await visualizer.OnPathFound(path);

            return new RoutingResult
            {
                Path                = path,
                TotalCost           = dist[endNode],
                NodesVisited        = visited,
                EdgeRelaxations     = relaxed,
                ElapsedMs           = sw.ElapsedMilliseconds,
                DistanceMetres      = distanceMeters,
                PathFound           = true
            };
        }

        private static double ComputePathDistance(RoadGraph graph, int[] path)
        {
            double total = 0;
            for (int i = 0; i < path.Length - 1; i++)
            {
                total += RoadGraphBuilder.Haversine(
                        (graph.NodeLat[path[i]],        graph.NodeLon[path[i]]),
                        (graph.NodeLat[path[i + 1]],    graph.NodeLon[path[i + 1]])
                    );
            }

            return total;
        }
    }
}
