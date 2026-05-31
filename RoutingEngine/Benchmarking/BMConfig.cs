using System.Collections.Generic;

namespace adaptive_offline_gps.RoutingEngine.Benchmarking
{
    /// <summary>
    /// A straight-line (haversine) distance range used to bucket queries
    /// MinKm inclusive, MaxKm exclusive
    /// Labels are used for output / CSV
    /// </summary>
    public record DistanceBucket(string Label, double MinKm, double MaxKm)
    {
        public static DistanceBucket Short  => new("1-10km", 1, 10);
        public static DistanceBucket Medium => new("10-100km", 10, 100);
        public static DistanceBucket Long   => new("100-1000km", 100, 1000);

        public static IReadOnlyList<DistanceBucket> DefaultSet => new[] { Short, Medium, Long };
    }

    /// <summary>
    /// One generated query pair
    /// 
    /// Straight-line distance is computed once at generation time and stored
    /// 
    /// It's the x-axis for performance plots
    /// </summary>
    public record QueryPair(
        int Id,
        int StartNode,
        int EndNode,
        double StraightLineKm,
        string BucketLabel);

    public enum BenchmarkMode
    {
        SinglePair,    //  use the user's pinned start/end
        MultiQuery,    //  generate from distance buckets
    }

    /// <summary>
    /// Everything the runner needs to know
    /// 
    /// Built by the UI from form values, or hand-constructed for tests / CLI use
    /// </summary>
    public class BenchmarkConfig
    {
        public BenchmarkMode Mode { get; init; } = BenchmarkMode.MultiQuery;

        //  Multi-query parameters
        public IReadOnlyList<DistanceBucket> Buckets { get; init; } = DistanceBucket.DefaultSet;
        public int PairsPerBucket { get; init; } = 5;
        public int QuerySeed { get; init; } = 42;

        //  Single-pair parameters (used when Mode == SinglePair)
        public int SingleStart { get; init; } = -1;
        public int SingleEnd { get; init; } = -1;

        //  Algorithms to run
        //
        //  The runner maps these to concrete algorithm instances
        public IReadOnlyList<string> AlgorithmNames { get; init; } = new[] { "Dijkstra", "A*", "CH" };

        //  Scenario identifier, written into output for traceability
        public string ScenarioName { get; init; } = "free_flow";

        //  Run multiplicity
        public int WarmupRuns { get; init; } = 1;
        public int MeasuredRuns { get; init; } = 3;

        //  Cap on attempts to find a node pair within a bucket before giving up on a particular slot
        //  Large enough that failures only happen if the bucket is genuinely infeasible
        public int MaxPairAttempts { get; init; } = 200;
    }
}