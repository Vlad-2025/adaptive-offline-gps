using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace adaptive_offline_gps.RoutingEngine.GraphAlgorithms
{
    /// <summary>
    /// <para>Returns the travel cost for an edge</para>
    /// <para>Implementations can modify the base weight based on active conditions 
    /// (rush hour, snow, road works, rain) without touching the graph itself</para>
    /// </summary>
    public interface IEdgeWeightProvider
    {
        float GetWeight(int edgeIndex, float baseWeight);
    }
}
