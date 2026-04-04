using adaptive_offline_gps.RoutingEngine.RoadGraphBuilding;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace adaptive_offline_gps.RoutingEngine.GraphAlgorithms
{
    public interface IRoutingAlgorithm
    {
        string Name { get; }

        Task<RoutingResult> FindPathAsync(

            RoadGraph           graph,
            int                 startNode,
            int                 endNode,
            IEdgeWeightProvider weights,
            RoutingOptions      options,
            IRoutingVisualizer  visualizer
            );
    }
}
