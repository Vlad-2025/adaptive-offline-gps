using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace adaptive_offline_gps.RoutingEngine.GraphAlgorithms
{
    /// <summary>
    /// Returns base graph weights unchanged
    /// </summary>
    public class BaseWeightProvider : IEdgeWeightProvider
    {
        public static readonly BaseWeightProvider Instance = new();

        public float GetWeight(int edgeIndex, float baseWeight) => baseWeight;
    }
}
