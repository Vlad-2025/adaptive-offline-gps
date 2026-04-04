using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace adaptive_offline_gps.RoutingEngine.GraphAlgorithms
{
    public class RoutingOptions
    {
        /// <summary>
        /// <para>Delay between each node visit during visualization</para>
        /// <para>Set to 0 for benchmarking</para>
        /// </summary>
        public int StepDelayMs { get; init; } = 20;

        /// <summary>
        /// <para>How often to yield to the UI thread</para>
        /// <para>Prevents the UI from freezing at low step delays</para>
        /// </summary>
        public int YieldEveryN {  get; init; } = 1;

        /// <summary>
        /// Token to cancel a running algorithm
        /// </summary>
        public CancellationToken CancellationToken { get; init; } = CancellationToken.None;

        /// <summary>
        /// If true, the visualizer callbacks are skipped
        /// </summary>
        public bool BenchmarkMode { get; init; } = false;

        public static RoutingOptions Default => new();

        public static RoutingOptions Instant => new()
        {
            StepDelayMs     = 0,
            YieldEveryN     = 500,
            BenchmarkMode   = false,
        };

        public static RoutingOptions Benchmark => new()
        {
            StepDelayMs     = 0,
            YieldEveryN     = 0,
            BenchmarkMode   = true,
        };
    }
}
