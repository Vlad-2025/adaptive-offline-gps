using System.Threading.Tasks;
using adaptive_offline_gps.RoutingEngine.GraphAlgorithms;
using adaptive_offline_gps.RoutingEngine.RoadGraphBuilding;

namespace adaptive_offline_gps.RoutingEngine.GraphAlgorithms.Algorithms
{
    /// <summary>
    /// Adapts CH to the IRoutingAlgorithm interface for the standard
    /// "single shortest path" call. For alternatives, callers can use
    /// FindAlternativesAsync directly, that's CH-specific and not part of
    /// IRoutingAlgorithm yet (Dijkstra/A* would be different)
    /// </summary>
    public class CHAlgorithmWrapper : IRoutingAlgorithm
    {
        public string Name => "CH";

        private readonly CHGraph _ch;

        public CHAlgorithmWrapper(CHGraph ch) => _ch = ch;

        public Task<RoutingResult> FindPathAsync(
            RoadGraph graph,
            int startNode,
            int endNode,
            IEdgeWeightProvider weights,    // CH ignores weights, uses precomputed
            RoutingOptions options,
            IRoutingVisualizer visualizer)
        {
            var query = new CHQuery(_ch);
            return query.FindPathAsync(startNode, endNode, options, visualizer);
        }

        /// <summary>
        /// Find the optimal route plus alternatives. Result[0] is optimal;
        /// result[1..n] are alternatives in cost order
        /// If no alternative passes filtering, the array has length 1
        /// </summary>
        public Task<RoutingResult[]> FindAlternativesAsync(
            int startNode,
            int endNode,
            int maxAlternatives,
            RoutingOptions options,
            IRoutingVisualizer visualizer,
            float searchRelaxation = 1.5f,
            float maxStretch = 0.25f,
            float maxSharing = 0.80f)
        {
            var query = new CHQuery(_ch);
            return query.FindAlternativePathsAsync(
                startNode, endNode, maxAlternatives,
                options, visualizer,
                searchRelaxation, maxStretch, maxSharing);
        }
    }
}