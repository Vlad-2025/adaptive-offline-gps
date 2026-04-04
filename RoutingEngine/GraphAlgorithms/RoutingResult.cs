using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace adaptive_offline_gps.RoutingEngine.GraphAlgorithms
{
    public record RoutingResult
    {
        public static readonly RoutingResult NoPath = new()
        {
            Path            = System.Array.Empty<int>(),
            TotalCost       = float.MaxValue,
            NodesVisited    = 0,
            EdgeRelaxations = 0,
            ElapsedMs       = 0,
            PathFound       = false,
        };

        /// <summary>
        /// Node indices from start to end (inclusive)
        /// </summary>
        public int[] Path { get; init; } = System.Array.Empty<int>();
        
        /// <summary>
        /// Total travel cost in seconds
        /// </summary>
        public float TotalCost { get; init; }

        /// <summary>
        /// How many nodes were popped from the p.queue
        /// </summary>
        public int NodesVisited { get; init; }

        /// <summary>
        /// How many edge relaxations were performed
        /// </summary>
        public int EdgeRelaxations { get; init; }

        /// <summary>
        /// Real elapsed time
        /// </summary>
        public long ElapsedMs { get; init; }

        public bool PathFound { get; init; } = true;

        /// <summary>
        /// Approximate path length in meters, calculated from graph coords
        /// </summary>
        public double DistanceMetres { get; init; }
    }
}
