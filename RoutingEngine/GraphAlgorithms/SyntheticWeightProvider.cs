using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace adaptive_offline_gps.RoutingEngine.GraphAlgorithms
{
    /// <summary>
    /// Applies synthetic condition multipliers to edge weights.
    /// </summary>
    public class SyntheticWeightProvider : IEdgeWeightProvider
    {
        public bool RushHour { get; set; }
        public bool Snow { get; set; }
        public bool Rain { get; set; }

        //  Road works: set of blocked edge indices - treated as impassable
        public HashSet<int> BlockedEdges { get; } = new();

        //  Rush hour: major roads 2x slower, others 1.5x
        //  Stored per edge-type range - for now a flat multiplier;
        //  extend with EdgeType array on RoadGraph when needed

        private const float RushHourMultiplier = 2.0f;
        private const float SnowMultiplier = 1.8f;
        private const float RainMultiplier = 1.3f;

        public float GetWeight(int edgeIndex, float baseWeight)
        {
            //  Blocked edges are impassable - return MaxValue so no algorithm uses them
            if (BlockedEdges.Contains(edgeIndex))
                return float.MaxValue;

            float weight = baseWeight;

            if (RushHour) 
                weight *= RushHourMultiplier;

            if (Snow) 
                weight *= SnowMultiplier;

            if (Rain) 
                weight *= RainMultiplier;

            return weight;
        }

        /// <summary>
        /// True if any condition is active.
        /// </summary>
        public bool AnyActive => RushHour || Snow || Rain || BlockedEdges.Count > 0;
    }
}
