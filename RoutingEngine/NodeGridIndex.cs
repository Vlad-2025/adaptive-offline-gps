using System;
using adaptive_offline_gps.RoutingEngine.RoadGraphBuilding;

namespace adaptive_offline_gps.RoutingEngine
{
    /// <summary>
    /// Spatial grid index for fast nearest-node lookup on map clicks.
    /// 
    /// Indexes ALL road nodes (junctions + pillar nodes) so users can click
    /// anywhere on a road. Each node maps to its nearest junction node
    /// for routing purposes.
    /// 
    /// Usage:
    ///   int junctionIdx = FindNearestJunction(lat, lon);
    ///   // use junctionIdx as start/end for routing
    /// </summary>
    public class NodeGridIndex
    {
        private readonly float[] _allLat;
        private readonly float[] _allLon;
        private readonly int[] _nearestJunction;   // allNodeIdx → junction graph index
        private readonly RoadGraph _graph;          // for junction coords (pin placement)

        private readonly int _gridSize;
        private readonly int[][] _cells;

        private readonly float _minLat, _minLon;
        private readonly float _latRange, _lonRange;

        /// <summary>
        /// Create index over all road nodes.
        /// </summary>
        /// <param name="graph">The junction-only road graph</param>
        /// <param name="allLat">Latitudes of ALL road nodes (junctions + pillars)</param>
        /// <param name="allLon">Longitudes of ALL road nodes</param>
        /// <param name="nearestJunction">Maps each all-node index to its nearest junction index in the graph</param>
        /// <param name="gridSize">Grid resolution</param>
        public NodeGridIndex(
            RoadGraph graph,
            float[] allLat, float[] allLon, int[] nearestJunction,
            int gridSize = 512)
        {
            _graph = graph;
            _allLat = allLat;
            _allLon = allLon;
            _nearestJunction = nearestJunction;
            _gridSize = gridSize;

            int totalNodes = allLat.Length;

            // Find bounding box
            _minLat = float.MaxValue; _minLon = float.MaxValue;
            float maxLat = float.MinValue, maxLon = float.MinValue;

            for (int i = 0; i < totalNodes; i++)
            {
                float lat = allLat[i], lon = allLon[i];
                if (lat < _minLat) _minLat = lat;
                if (lat > maxLat) maxLat = lat;
                if (lon < _minLon) _minLon = lon;
                if (lon > maxLon) maxLon = lon;
            }

            _minLat -= 0.0001f; _minLon -= 0.0001f;
            maxLat += 0.0001f; maxLon += 0.0001f;
            _latRange = maxLat - _minLat;
            _lonRange = maxLon - _minLon;

            // Count nodes per cell
            var counts = new int[gridSize * gridSize];
            for (int i = 0; i < totalNodes; i++)
                counts[CellIndex(allLat[i], allLon[i])]++;

            // Allocate
            _cells = new int[gridSize * gridSize][];
            for (int c = 0; c < _cells.Length; c++)
                if (counts[c] > 0)
                    _cells[c] = new int[counts[c]];

            // Fill
            var cursors = new int[gridSize * gridSize];
            for (int i = 0; i < totalNodes; i++)
            {
                int idx = CellIndex(allLat[i], allLon[i]);
                _cells[idx][cursors[idx]++] = i;
            }
        }

        /// <summary>
        /// Backwards-compatible constructor: indexes junction nodes only.
        /// Used when AllNodeLat/Lon aren't available (e.g. loaded from old cache).
        /// </summary>
        public NodeGridIndex(RoadGraph graph, int gridSize = 512)
            : this(graph, graph.NodeLat, graph.NodeLon,
                   CreateIdentityMapping(graph.NodeCount), gridSize)
        {
        }

        private static int[] CreateIdentityMapping(int count)
        {
            var map = new int[count];
            for (int i = 0; i < count; i++) map[i] = i;
            return map;
        }

        /// <summary>
        /// Find the nearest road node to (lat, lon) and return its junction index
        /// in the routing graph. This is what you use for start/end node selection.
        /// </summary>
        public int FindNearestNode(double lat, double lon)
        {
            int allNodeIdx = FindNearestAllNode(lat, lon);
            if (allNodeIdx < 0) return -1;

            return _nearestJunction[allNodeIdx];
        }

        /// <summary>
        /// Find the nearest road node (any node, not just junction).
        /// Returns the all-node compact index.
        /// </summary>
        private int FindNearestAllNode(double lat, double lon)
        {
            int targetRow = LatToRow((float)lat);
            int targetCol = LonToCol((float)lon);

            int bestNode = -1;
            float bestDist = float.MaxValue;

            for (int ring = 0; ring <= _gridSize; ring++)
            {
                if (bestNode >= 0 && RingIsDefinitelyFarther(ring, targetRow, targetCol, bestDist))
                    break;

                SearchRing(ring, targetRow, targetCol,
                    (float)lat, (float)lon, ref bestNode, ref bestDist);

                if (bestNode >= 0 && ring > 0)
                    break;
            }

            return bestNode;
        }

        private void SearchRing(int ring, int centerRow, int centerCol,
            float lat, float lon, ref int bestNode, ref float bestDist)
        {
            if (ring == 0)
            {
                SearchCell(centerRow, centerCol, lat, lon, ref bestNode, ref bestDist);
                return;
            }

            int rMin = centerRow - ring, rMax = centerRow + ring;
            int cMin = centerCol - ring, cMax = centerCol + ring;

            for (int r = rMin; r <= rMax; r++)
            {
                for (int c = cMin; c <= cMax; c++)
                {
                    if (r != rMin && r != rMax && c != cMin && c != cMax)
                        continue;
                    SearchCell(r, c, lat, lon, ref bestNode, ref bestDist);
                }
            }
        }

        private void SearchCell(int row, int col,
            float lat, float lon, ref int bestNode, ref float bestDist)
        {
            if (row < 0 || row >= _gridSize || col < 0 || col >= _gridSize)
                return;

            var bucket = _cells[row * _gridSize + col];
            if (bucket == null) return;

            foreach (int nodeIdx in bucket)
            {
                float dLat = _allLat[nodeIdx] - lat;
                float dLon = _allLon[nodeIdx] - lon;
                float dist = dLat * dLat + dLon * dLon;

                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestNode = nodeIdx;
                }
            }
        }

        private bool RingIsDefinitelyFarther(int ring, int centerRow, int centerCol, float bestDist)
        {
            float minLatDelta = (ring - 1) * (_latRange / _gridSize);
            float minLonDelta = (ring - 1) * (_lonRange / _gridSize);
            float minDist = minLatDelta * minLatDelta + minLonDelta * minLonDelta;
            return minDist > bestDist;
        }

        private int CellIndex(float lat, float lon)
            => LatToRow(lat) * _gridSize + LonToCol(lon);

        private int LatToRow(float lat)
            => Math.Clamp((int)(((lat - _minLat) / _latRange) * _gridSize), 0, _gridSize - 1);

        private int LonToCol(float lon)
            => Math.Clamp((int)(((lon - _minLon) / _lonRange) * _gridSize), 0, _gridSize - 1);
    }
}