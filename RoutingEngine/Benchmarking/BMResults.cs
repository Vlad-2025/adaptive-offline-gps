using System;
using System.Collections.Generic;
using System.Linq;

namespace adaptive_offline_gps.RoutingEngine.Benchmarking
{
    /// <summary>
    /// Result of one (algorithm, query) measurement, i.e. the median over
    /// MeasuredRuns repetitions
    /// Warmup runs are not included
    /// </summary>
    public class BenchmarkRunResult
    {
        public required string AlgorithmName { get; init; }
        public required QueryPair Query { get; init; }

        public bool PathFound { get; init; }
        public double PathDistanceKm { get; init; }
        public double TotalCost { get; init; }
        public int NodesVisited { get; init; }
        public int EdgeRelaxations { get; init; }

        /// <summary>
        /// Median elapsed time over the measured runs, in milliseconds
        /// </summary>
        public double MedianElapsedMs { get; init; }

        /// <summary>
        /// Min and max so the UI can show variance if it wants
        /// </summary>
        public double MinElapsedMs { get; init; }
        public double MaxElapsedMs { get; init; }

        /// <summary>
        /// Filled in by the runner after all algorithms have run for this query.
        /// 
        /// True if this algorithm's cost matches the cost of every other algorithm for the same query (within tolerance)
        /// False highlights a correctness bug.
        /// </summary>
        public bool CostAgreesWithOthers { get; set; } = true;

        /// <summary>
        /// Raw individual elapsed times, kept for the CSV
        /// </summary>
        public double[] AllElapsedMs { get; init; } = Array.Empty<double>();
    }

    /// <summary>
    /// Aggregate stats across all queries for one algorithm
    /// </summary>
    public class AlgorithmAggregate
    {
        public required string AlgorithmName { get; init; }
        public int QueryCount { get; init; }

        public double MeanMs { get; init; }
        public double MedianMs { get; init; }
        public double P95Ms { get; init; }
        public double P99Ms { get; init; }
        public double MinMs { get; init; }
        public double MaxMs { get; init; }

        public double MeanNodesVisited { get; init; }
        public double MeanEdgeRelaxations { get; init; }

        /// <summary>
        /// Speedup vs the baseline algorithm (typically Dijkstra)
        /// 1.0 if this is the baseline
        /// NaN if no baseline was set or denominator was zero
        /// </summary>
        public double SpeedupVsBaseline { get; init; } = 1.0;

        /// <summary>
        /// How many of this algorithm's runs disagreed with the consensus cost
        /// Should be zero. Non-zero is a correctness bug
        /// </summary>
        public int CostDisagreements { get; init; }
    }

    /// <summary>
    /// Complete benchmark output, what gets shown in the UI and serialized to CSV
    /// </summary>
    public class BenchmarkReport
    {
        public required BenchmarkConfig Config { get; init; }
        public required DateTime StartedUtc { get; init; }
        public required DateTime FinishedUtc { get; init; }

        public required IReadOnlyList<QueryPair> Queries { get; init; }
        public required IReadOnlyList<BenchmarkRunResult> Results { get; init; }
        public required IReadOnlyList<AlgorithmAggregate> Aggregates { get; init; }

        public required IReadOnlyList<(DistanceBucket bucket, int generated, int requested)> FailedBuckets { get; init; }

        //  Graph metadata, captured at run time, written into CSV header
        public required string GraphFile { get; init; }
        public required int GraphNodes { get; init; }
        public required int GraphEdges { get; init; }
        public required int ChShortcuts { get; init; }

        public bool AllCostsAgree => Results.All(r => r.CostAgreesWithOthers);
        public int TotalQueries => Queries.Count;

        /// <summary>
        /// Compute aggregate stats from results
        /// Called by the runner once results are in
        /// </summary>
        public static IReadOnlyList<AlgorithmAggregate> ComputeAggregates(
            IReadOnlyList<BenchmarkRunResult> results,
            string baselineAlgorithm = "Dijkstra")
        {
            var byAlgo = results.GroupBy(r => r.AlgorithmName).ToList();

            //  Baseline mean (for speedup ratios)
            //  If the baseline algorithm wasn't run, use the slowest algorithm's mean as the reference

            double baselineMean = double.NaN;
            var baselineGroup = byAlgo.FirstOrDefault(g => g.Key == baselineAlgorithm);

            if (baselineGroup != null)
            {
                var times = baselineGroup.Select(r => r.MedianElapsedMs).ToArray();

                if (times.Length > 0) 
                    baselineMean = times.Average();
            }

            var aggs = new List<AlgorithmAggregate>();
            foreach (var grp in byAlgo)
            {
                var sortedTimes = grp.Select(r => r.MedianElapsedMs).OrderBy(x => x).ToArray();

                if (sortedTimes.Length == 0) 
                    continue;

                double mean = sortedTimes.Average();

                aggs.Add(new AlgorithmAggregate
                {
                    AlgorithmName = grp.Key,
                    QueryCount = sortedTimes.Length,
                    MeanMs = mean,
                    MedianMs = Percentile(sortedTimes, 0.50),
                    P95Ms = Percentile(sortedTimes, 0.95),
                    P99Ms = Percentile(sortedTimes, 0.99),
                    MinMs = sortedTimes[0],
                    MaxMs = sortedTimes[^1],
                    MeanNodesVisited = grp.Average(r => (double)r.NodesVisited),
                    MeanEdgeRelaxations = grp.Average(r => (double)r.EdgeRelaxations),
                    SpeedupVsBaseline = !double.IsNaN(baselineMean) && mean > 0
                                            ? baselineMean / mean
                                            : double.NaN,
                    CostDisagreements = grp.Count(r => !r.CostAgreesWithOthers),
                });
            }

            //  Order: baseline first, then by descending speedup (fastest after it)

            return aggs
                .OrderBy(a => a.AlgorithmName == baselineAlgorithm ? 0 : 1)
                .ThenByDescending(a => a.SpeedupVsBaseline)
                .ToList();
        }

        //  Linear-interpolation percentile on a pre-sorted array
        //  p in [0,1]. p=0.5 = median
        private static double Percentile(double[] sorted, double p)
        {
            if (sorted.Length == 0) return double.NaN;
            if (sorted.Length == 1) return sorted[0];

            double idx = p * (sorted.Length - 1);
            int lo = (int)Math.Floor(idx);
            int hi = (int)Math.Ceiling(idx);
            if (lo == hi) return sorted[lo];

            double frac = idx - lo;
            return sorted[lo] * (1 - frac) + sorted[hi] * frac;
        }
    }
}