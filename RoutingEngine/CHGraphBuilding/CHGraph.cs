using System.Collections.Generic;

namespace adaptive_offline_gps.RoutingEngine.RoadGraphBuilding
{
    public class CHGraph
    {
        /// <summary>
        /// The original road graph — still needed for node coordinates,
        /// path distance calculation, visualization, pin placement, etc.
        /// </summary>
        public RoadGraph BaseGraph;

        /// <summary>
        /// Contraction order for each node. Lower = contracted earlier = less important.
        /// During query, edges are only followed "uphill" (toward higher levels).
        /// </summary>
        public int[] Level;

        /// <summary>
        /// Upward adjacency: edges from u where Level[target] > Level[u].
        /// Used by the forward search.
        /// </summary>
        public List<CHEdge>[] ForwardUp;

        /// <summary>
        /// Downward adjacency (reversed upward edges): 
        /// Used by the backward search.
        /// </summary>
        public List<CHEdge>[] BackwardUp;
    }
}