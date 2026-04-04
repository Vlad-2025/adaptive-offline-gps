using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace adaptive_offline_gps.RoutingEngine.GraphAlgorithms
{
    /// <summary>
    /// <para>Receives events from a running algorithm so the UI can animate progress.</para>
    /// <para>All methods are async</para>
    /// </summary>
    public interface IRoutingVisualizer
    {
        /// <summary>
        /// Called each time the algorithm pops a node from the priority queue
        /// </summary>
        Task OnNodeVisited(int nodeIndex);

        /// <summary>
        /// Called each time an edge is relaxed
        /// </summary>
        Task OnEdgeRelaxed(int fromNode, int toNode);

        /// <summary>
        /// Called once when the final path is known
        /// </summary>
        Task OnPathFound(int[] pathNodes);

        /// <summary>
        /// Called if no path exists between start and end
        /// </summary>
        Task OnNoPathFound();
    }
}
