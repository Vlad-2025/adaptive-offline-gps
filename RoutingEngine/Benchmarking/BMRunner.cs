using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using adaptive_offline_gps.RoutingEngine.GraphAlgorithms;
using adaptive_offline_gps.RoutingEngine.GraphAlgorithms.Algorithms;
using adaptive_offline_gps.RoutingEngine.RoadGraphBuilding;

namespace adaptive_offline_gps.RoutingEngine.Benchmarking
{
    /// <summary>
    /// Progress callback. Called from the runner thread, callers should
    /// marshal to the UI thread themselves.
    /// </summary>
    public delegate void BenchmarkProgressCallback(BenchmarkProgress progress);

    public record BenchmarkProgress(
        int CompletedRuns,
        int TotalRuns,
        string CurrentAlgorithm,
        int CurrentQueryId,
        string Stage // "warmup" | "measure"
    );

    /// <summary>
    /// Orchestrates a benchmark: generates queries (if needed), runs each algorithm
    /// against each query (warmup + measured runs), computes per-query consensus,
    /// and produces the final BenchmarkReport.
    /// 
    /// All algorithm runs use RoutingOptions.Benchmark so the visualizer is bypassed.
    /// </summary>
    public class BenchmarkRunner
    {
        private readonly RoadGraph _graph;
        private readonly CHGraph _chGraph;
        private readonly string _graphFile;

        public BenchmarkRunner(RoadGraph graph, CHGraph chGraph, string graphFile)
        {
            _graph = graph;
            _chGraph = chGraph;
            _graphFile = graphFile;
        }

        public async Task<BenchmarkReport> RunAsync(
            BenchmarkConfig config,
            IEdgeWeightProvider weights,
            BenchmarkProgressCallback? onProgress = null,
            CancellationToken cancel = default)
        {
            var startUtc = DateTime.UtcNow;

            //  1. Generate queries
            var generator = new QueryGenerator(_graph, config.QuerySeed, config.MaxPairAttempts);
            List<QueryPair> queries;
            List<(DistanceBucket bucket, int generated, int requested)> failedBuckets;

            if (config.Mode == BenchmarkMode.SinglePair)
            {
                if (config.SingleStart < 0 || config.SingleEnd < 0)
                    throw new InvalidOperationException(
                        "SinglePair mode requires SingleStart and SingleEnd to be set.");

                queries = new List<QueryPair> { generator.Single(config.SingleStart, config.SingleEnd) };
                failedBuckets = new List<(DistanceBucket, int, int)>();
            }
            else
            {
                queries = generator.Generate(config.Buckets, config.PairsPerBucket, out failedBuckets);
            }

            if (queries.Count == 0)
                throw new InvalidOperationException("No queries generated; cannot benchmark.");

            //  2. Resolve algorithm runners. We use small functors so the runner
            //  doesn't care that CH ignores the weight provider while Dijkstra/A*
            //  use it. The functor knows what to pass.
            var runners = ResolveAlgorithms(config.AlgorithmNames);

            //  3. Run all (algorithm × query) combinations
            int totalRuns = runners.Count * queries.Count * (config.WarmupRuns + config.MeasuredRuns);
            int completedRuns = 0;

            var perRunByKey = new Dictionary<(string algo, int queryId), BenchmarkRunResult>();

            //  Each query is run for every algorithm before moving to the next
            //  query. This means measurements for a given query happen close
            //  together in time - fairer if the OS scheduler / thermal state
            //  drifts during the run.
            foreach (var query in queries)
            {
                foreach (var runner in runners)
                {
                    cancel.ThrowIfCancellationRequested();

                    //  Warmup
                    for (int w = 0; w < config.WarmupRuns; w++)
                    {
                        onProgress?.Invoke(new BenchmarkProgress(
                            completedRuns, totalRuns,
                            runner.Name, query.Id, "warmup"));

                        await runner.Run(query, weights, cancel);
                        completedRuns++;
                    }

                    //  Measured
                    var elapsed = new double[config.MeasuredRuns];
                    RoutingResult? last = null;

                    for (int m = 0; m < config.MeasuredRuns; m++)
                    {
                        cancel.ThrowIfCancellationRequested();

                        onProgress?.Invoke(new BenchmarkProgress(
                            completedRuns, totalRuns,
                            runner.Name, query.Id, "measure"));

                        var r = await runner.Run(query, weights, cancel);
                        elapsed[m] = r.ElapsedMs;
                        last = r;
                        completedRuns++;
                    }

                    //  Aggregate this (algo, query) cell
                    Array.Sort(elapsed);
                    double median = elapsed[elapsed.Length / 2];

                    perRunByKey[(runner.Name, query.Id)] = new BenchmarkRunResult
                    {
                        AlgorithmName = runner.Name,
                        Query = query,
                        PathFound = last?.PathFound ?? false,
                        PathDistanceKm = (last?.DistanceMetres ?? 0) / 1000.0,
                        TotalCost = last?.TotalCost ?? float.MaxValue,
                        NodesVisited = last?.NodesVisited ?? 0,
                        EdgeRelaxations = last?.EdgeRelaxations ?? 0,
                        MedianElapsedMs = median,
                        MinElapsedMs = elapsed[0],
                        MaxElapsedMs = elapsed[^1],
                        AllElapsedMs = elapsed.ToArray(),
                    };
                }
            }

            //  4. Consensus check per query - flag any disagreement
            CheckCostAgreement(perRunByKey, queries);

            //  5. Build flat result list + aggregates
            var resultsList = perRunByKey.Values
                .OrderBy(r => r.Query.Id)
                .ThenBy(r => r.AlgorithmName)
                .ToList();

            var aggregates = BenchmarkReport.ComputeAggregates(resultsList);

            //  Recount disagreements per-algorithm after consensus pass
            aggregates = aggregates.Select(a => new AlgorithmAggregate
            {
                AlgorithmName = a.AlgorithmName,
                QueryCount = a.QueryCount,
                MeanMs = a.MeanMs,
                MedianMs = a.MedianMs,
                P95Ms = a.P95Ms,
                P99Ms = a.P99Ms,
                MinMs = a.MinMs,
                MaxMs = a.MaxMs,
                MeanNodesVisited = a.MeanNodesVisited,
                MeanEdgeRelaxations = a.MeanEdgeRelaxations,
                SpeedupVsBaseline = a.SpeedupVsBaseline,
                CostDisagreements = resultsList
                    .Where(r => r.AlgorithmName == a.AlgorithmName && !r.CostAgreesWithOthers)
                    .Count(),
            }).ToList();

            int chShortcuts = _chGraph.ForwardUp.Sum(a => a.Count(e => e.ShortcutMid != -1));

            return new BenchmarkReport
            {
                Config = config,
                StartedUtc = startUtc,
                FinishedUtc = DateTime.UtcNow,
                Queries = queries,
                Results = resultsList,
                Aggregates = aggregates,
                FailedBuckets = failedBuckets,
                GraphFile = _graphFile,
                GraphNodes = _graph.NodeCount,
                GraphEdges = _graph.EdgeCount,
                ChShortcuts = chShortcuts,
            };
        }

        //  Cost agreement check: for each query, take the median cost across
        //  algorithms that found a path. Any algorithm differing by more than
        //  tolerance is flagged. Tolerance is relative (0.1%) to absorb float drift.
        private static void CheckCostAgreement(
            Dictionary<(string, int), BenchmarkRunResult> byKey,
            List<QueryPair> queries)
        {
            const double RelTol = 0.001; // 0.1%

            foreach (var query in queries)
            {
                var resultsForQuery = byKey
                    .Where(kv => kv.Key.Item2 == query.Id && kv.Value.PathFound)
                    .Select(kv => kv.Value)
                    .ToList();

                if (resultsForQuery.Count < 2) continue;

                //  Use median as consensus - robust to one outlier (e.g. a buggy
                //  algorithm). With 3+ algos disagreeing badly, all get flagged
                //  which is fine - surfaces the problem.
                var sortedCosts = resultsForQuery.Select(r => r.TotalCost).OrderBy(c => c).ToArray();
                double consensus = sortedCosts[sortedCosts.Length / 2];

                foreach (var r in resultsForQuery)
                {
                    double diff = Math.Abs(r.TotalCost - consensus);
                    double tol = Math.Max(RelTol * Math.Abs(consensus), 0.01);
                    if (diff > tol)
                    {
                        r.CostAgreesWithOthers = false;
                        Debug.WriteLine(
                            $"Cost disagreement: query {query.Id} algo {r.AlgorithmName} " +
                            $"reported {r.TotalCost:F2} vs consensus {consensus:F2}");
                    }
                }
            }
        }

        //  Map config algorithm-names to little functor objects that know how to
        //  invoke each algorithm. Keeps the runner agnostic of CH-vs-others quirks.
        private List<AlgoRunner> ResolveAlgorithms(IReadOnlyList<string> names)
        {
            var algorithms = new Dictionary<string, IRoutingAlgorithm>
            {
                ["Dijkstra"] = new DijkstraAlgorithm(),
                ["A*"] = new AStarAlgorithm()
            };

            var list = new List<AlgoRunner>();
            foreach (var name in names)
            {
                if(name == "CH")
                {
                    list.Add(
                        new AlgoRunner("CH", (q, _, c) => 
                            new CHAlgorithmWrapper(_chGraph).FindPathAsync(
                                _graph, 
                                q.StartNode,
                                q.EndNode, 
                                BaseWeightProvider.Instance, 
                                BenchOptions(c), 
                                NullVisualizer.Instance
                            )));
                    
                    continue;
                }

                if (!algorithms.TryGetValue(name, out var algo))
                    throw new ArgumentException($"Unknown algorithm: {name}");

                list.Add(new AlgoRunner(
                    algo.Name,
                    (q, w, c) =>
                        algo.FindPathAsync(
                            _graph,
                            q.StartNode,
                            q.EndNode,
                            w,
                            BenchOptions(c),
                            NullVisualizer.Instance
                        )));
            }
            return list;
        }

        private static RoutingOptions BenchOptions(CancellationToken cancel) => new()
        {
            StepDelayMs = 0,
            YieldEveryN = 0,
            BenchmarkMode = true,
            CancellationToken = cancel,
        };

        //  Internal: tagged delegate so the runner can call any algorithm uniformly.
        private record AlgoRunner(
            string Name,
            Func<QueryPair, IEdgeWeightProvider, CancellationToken, Task<RoutingResult>> Run);
    }

    /// <summary>
    /// IRoutingVisualizer that does nothing. Used for benchmark runs to bypass
    /// rendering entirely - significant speedup vs even a no-op real visualizer
    /// because the per-node await dispatches still cost something.
    /// </summary>
    internal class NullVisualizer : IRoutingVisualizer
    {
        public static readonly NullVisualizer Instance = new();
        public Task OnNodeVisited(int nodeIndex) => Task.CompletedTask;
        public Task OnEdgeRelaxed(int fromNode, int toNode) => Task.CompletedTask;
        public Task OnPathFound(int[] pathNodes) => Task.CompletedTask;
        public Task OnNoPathFound() => Task.CompletedTask;
    }
}