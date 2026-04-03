using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace adaptive_offline_gps.RoutingEngine.RoadGraphBuilding
{
    public class RoadGraph
    {
        public float[] NodeLat;
        public float[] NodeLon;

        //  CSR adjacency
        public int[] EdgeHead;      //  first edge index for that node
        public int[] EdgeTarget;    //  target node index
        public float[] EdgeWeight;

        public int NodeCount => NodeLat.Length;
        public int EdgeCount => EdgeTarget.Length;
    }
}
