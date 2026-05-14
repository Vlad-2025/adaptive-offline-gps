using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace adaptive_offline_gps.RoutingEngine.Benchmarking
{
    /// <summary>
    /// Writes a BenchmarkReport to disk as CSV with a comment-prefixed metadata
    /// header. Long format (one row per algorithm-query pair) for pandas/R/Excel
    /// pivot friendliness.
    /// </summary>
    public static class BenchmarkCsvWriter
    {
        //  Use invariant culture so decimal separator is "." even on locales
        //  that default to "," (your debug output showed Romanian formatting).
        //  CSV readers expect "." for floats.
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>
        /// Write report to `path`. Overwrites if it exists.
        /// </summary>
        public static void Write(BenchmarkReport report, string path)
        {
            using var w = new StreamWriter(path, append: false, Encoding.UTF8);
            WriteMetadata(w, report);
            WriteRows(w, report);
        }

        /// <summary>
        /// Generate a standardized filename for a report. Caller picks the directory.
        /// Format: bench_{scenario}_{yyyy-MM-dd_HH-mm}.csv
        /// </summary>
        public static string DefaultFileName(BenchmarkReport report)
        {
            //  Sanitize scenario name - no path separators, no spaces
            string scen = report.Config.ScenarioName
                .Replace(' ', '_')
                .Replace('/', '_')
                .Replace('\\', '_');
            string stamp = report.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd_HH-mm", Inv);
            return $"bench_{scen}_{stamp}.csv";
        }

        private static void WriteMetadata(StreamWriter w, BenchmarkReport r)
        {
            void M(string line) => w.WriteLine("# " + line);

            M("adaptive-offline-gps benchmark");
            M($"scenario:        {r.Config.ScenarioName}");
            M($"graph_file:      {r.GraphFile}");
            M($"graph_nodes:     {r.GraphNodes}");
            M($"graph_edges:     {r.GraphEdges}");
            M($"ch_shortcuts:    {r.ChShortcuts}");
            M($"mode:            {r.Config.Mode}");
            M($"query_seed:      {r.Config.QuerySeed}");
            if (r.Config.Mode == BenchmarkMode.MultiQuery)
            {
                M($"buckets:         {string.Join(",", r.Config.Buckets.Select(b => b.Label))}");
                M($"pairs_per_bucket: {r.Config.PairsPerBucket}");
            }
            M($"warmup_runs:     {r.Config.WarmupRuns}");
            M($"measured_runs:   {r.Config.MeasuredRuns}");
            M($"algorithms:      {string.Join(",", r.Config.AlgorithmNames)}");
            M($"run_started:     {r.StartedUtc.ToString("o", Inv)}");
            M($"run_finished:    {r.FinishedUtc.ToString("o", Inv)}");
            M($"total_queries:   {r.TotalQueries}");
            M($"all_costs_agree: {r.AllCostsAgree}");

            foreach (var (bucket, gen, req) in r.FailedBuckets)
                M($"bucket_short:    {bucket.Label} produced {gen}/{req} pairs");

            //  Header row
            w.WriteLine(string.Join(",", new[]
            {
                "scenario", "query_id", "bucket", "algorithm",
                "start_node", "end_node",
                "straight_line_km", "path_distance_km", "total_cost",
                "nodes_visited", "edge_relaxations",
                "median_elapsed_ms", "min_elapsed_ms", "max_elapsed_ms",
                "all_elapsed_ms",
                "path_found", "cost_agrees_with_others",
            }));
        }

        private static void WriteRows(StreamWriter w, BenchmarkReport r)
        {
            string scen = r.Config.ScenarioName;
            foreach (var row in r.Results)
            {
                //  all_elapsed_ms is space-separated inside a quoted CSV cell -
                //  no embedded commas, but reading tools can split it on space.
                string allElapsed = string.Join(" ",
                    row.AllElapsedMs.Select(x => x.ToString("F3", Inv)));

                w.WriteLine(string.Join(",", new[]
                {
                    scen,
                    row.Query.Id.ToString(Inv),
                    row.Query.BucketLabel,
                    row.AlgorithmName,
                    row.Query.StartNode.ToString(Inv),
                    row.Query.EndNode.ToString(Inv),
                    row.Query.StraightLineKm.ToString("F3", Inv),
                    row.PathDistanceKm.ToString("F3", Inv),
                    row.TotalCost.ToString("F3", Inv),
                    row.NodesVisited.ToString(Inv),
                    row.EdgeRelaxations.ToString(Inv),
                    row.MedianElapsedMs.ToString("F3", Inv),
                    row.MinElapsedMs.ToString("F3", Inv),
                    row.MaxElapsedMs.ToString("F3", Inv),
                    $"\"{allElapsed}\"",
                    row.PathFound ? "true" : "false",
                    row.CostAgreesWithOthers ? "true" : "false",
                }));
            }
        }
    }
}