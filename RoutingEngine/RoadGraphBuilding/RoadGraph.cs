using System;

namespace adaptive_offline_gps.RoutingEngine.RoadGraphBuilding
{
    public class RoadGraph
    {
        //  Junction (tower) node coordinates
        public float[] NodeLat;
        public float[] NodeLon;

        //  CSR adjacency (junctions only)
        public int[] EdgeHead;      //  first edge index for that node
        public int[] EdgeTarget;    //  target node index
        public float[] EdgeWeight;

        /// <summary>
        /// Per-edge pillar geometry: the intermediate coordinates between
        /// the two junction endpoints (excluding the endpoints themselves).
        /// EdgeGeomLat[edgeIdx] = float[] of pillar latitudes (may be empty).
        /// Used to reconstruct the full road shape when rendering a path.
        /// </summary>
        public float[][] EdgeGeomLat;
        public float[][] EdgeGeomLon;

        public int NodeCount => NodeLat.Length;
        public int EdgeCount => EdgeTarget.Length;

        /// <summary>
        /// Returns the full coordinate sequence for an edge, including
        /// the source and target junction nodes and all pillar nodes between.
        /// </summary>
        public (float lat, float lon)[] GetEdgeGeometry(int fromNode, int edgeIdx)
        {
            int toNode = EdgeTarget[edgeIdx];
            var pillarLat = EdgeGeomLat[edgeIdx];
            var pillarLon = EdgeGeomLon[edgeIdx];

            int pillarCount = pillarLat?.Length ?? 0;
            var result = new (float, float)[pillarCount + 2];

            result[0] = (NodeLat[fromNode], NodeLon[fromNode]);

            for (int i = 0; i < pillarCount; i++)
                result[i + 1] = (pillarLat[i], pillarLon[i]);

            result[pillarCount + 1] = (NodeLat[toNode], NodeLon[toNode]);

            return result;
        }
    }
}