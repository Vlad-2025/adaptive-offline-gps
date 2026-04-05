using System.Threading.Tasks;
using adaptive_offline_gps.RoutingEngine.GraphAlgorithms;
using adaptive_offline_gps.RoutingEngine.RoadGraphBuilding;

namespace adaptive_offline_gps.RoutingEngine.GraphAlgorithms.Algorithms
{
    public class CHAlgorithmWrapper : IRoutingAlgorithm
    {
        public string Name => "Contraction Hierarchies";

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
    }
}