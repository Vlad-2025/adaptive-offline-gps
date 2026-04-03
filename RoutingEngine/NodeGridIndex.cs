using System;
using System.Collections.Generic;

namespace adaptive_offline_gps.RoutingEngine
{
    public class NodeGridIndex
    {
        private readonly RoadGraph _graph;
        private readonly int _gridSize;
        private readonly int[][] _cells;      // _cells[cellIndex] = array of node indices

        //  Bounding box of all nodes
        private readonly float _minLat, _minLon;
        private readonly float _latRange, _lonRange;

        public NodeGridIndex(RoadGraph graph, int gridSize = 512)
        {
            _graph = graph;
            _gridSize = gridSize;

            //  1) Find bounding box
            _minLat         = float.MaxValue; _minLon   = float.MaxValue;

            float maxLat    = float.MinValue, maxLon    = float.MinValue;

            for (int i = 0; i < graph.NodeCount; i++)
            {
                float lat = graph.NodeLat[i];
                float lon = graph.NodeLon[i];

                if (lat < _minLat) 
                    _minLat = lat;

                if (lat > maxLat) 
                    maxLat = lat;

                if (lon < _minLon) 
                    _minLon = lon;

                if (lon > maxLon) 
                    maxLon = lon;
            }

            //  Small padding so nodes exactly on the edge land inside the grid
            _minLat -= 0.0001f; _minLon -= 0.0001f;
            maxLat  += 0.0001f; maxLon  += 0.0001f;

            _latRange = maxLat - _minLat;
            _lonRange = maxLon - _minLon;

            //  2) Count nodes per cell (two-pass avoids realloc)
            var counts = new int[gridSize * gridSize];

            for (int i = 0; i < graph.NodeCount; i++)
            {
                int idx = CellIndex(graph.NodeLat[i], graph.NodeLon[i]);
                counts[idx]++;
            }

            //  3) Allocate buckets exactly sized
            _cells = new int[gridSize * gridSize][];
            for (int c = 0; c < _cells.Length; c++)
                if (counts[c] > 0)
                    _cells[c] = new int[counts[c]];

            //  4) Fill buckets 
            var cursors = new int[gridSize * gridSize];

            for (int i = 0; i < graph.NodeCount; i++)
            {
                int idx = CellIndex(graph.NodeLat[i], graph.NodeLon[i]);
                _cells[idx][cursors[idx]++] = i;
            }
        }

        //  Public API 

        /// <summary>
        /// Returns the index of the graph node closest to (lat, lon).
        /// Expands search ring outward until at least one candidate is found.
        /// </summary>
        public int FindNearestNode(double lat, double lon)
        {
            int targetRow = LatToRow((float)lat);
            int targetCol = LonToCol((float)lon);

            int bestNode = -1;
            float bestDist = float.MaxValue;

            //  Expand search ring: ring 0 = target cell only, ring 1 = 3×3, etc
            for (int ring = 0; ring <= _gridSize; ring++)
            {
                //  Once we've found a candidate and the ring is wider than the
                //  best distance, no closer node can exist in outer rings
                if (bestNode >= 0 && RingIsDefinitelyFarther(ring, targetRow, targetCol, bestDist))
                    break;

                SearchRing(ring, targetRow, targetCol,
                    (float)lat, (float)lon, ref bestNode, ref bestDist);

                //  Found something and checked at least one full ring beyond — done
                if (bestNode >= 0 && ring > 0)
                    break;
            }

            return bestNode;
        }

        //  Private helpers 

        private void SearchRing(int ring, int centerRow, int centerCol,
            float lat, float lon, ref int bestNode, ref float bestDist)
        {
            if (ring == 0)
            {
                SearchCell(centerRow, centerCol, lat, lon, ref bestNode, ref bestDist);
                return;
            }

            //  Walk the perimeter of the ring square
            int rMin = centerRow - ring, rMax = centerRow + ring;
            int cMin = centerCol - ring, cMax = centerCol + ring;

            for (int r = rMin; r <= rMax; r++)
            {
                for (int c = cMin; c <= cMax; c++)
                {
                    //  Only visit cells ON the perimeter (not interior -> already searched)
                    if (r != rMin && r != rMax && c != cMin && c != cMax) 
                        continue;

                    SearchCell(r, c, lat, lon, ref bestNode, ref bestDist);
                }
            }
        }

        private void SearchCell(int row, int col,
            float lat, float lon, ref int bestNode, ref float bestDist)
        {
            //  Clamp to grid bounds
            if (row < 0 || row >= _gridSize || col < 0 || col >= _gridSize) 
                return;

            var bucket = _cells[row * _gridSize + col];

            if (bucket == null) 
                return;

            foreach (int nodeIdx in bucket)
            {
                float dLat = _graph.NodeLat[nodeIdx] - lat;
                float dLon = _graph.NodeLon[nodeIdx] - lon;
                //  Approximate squared distance — good enough for nearest-node
                //  No need for Haversine since we're comparing relative distances
                float dist = dLat * dLat + dLon * dLon;

                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestNode = nodeIdx;
                }
            }
        }

        //  A ring is definitely farther when its closest possible edge
        //  is farther than the best distance found so far
        private bool RingIsDefinitelyFarther(int ring, int centerRow, int centerCol, float bestDist)
        {
            //  Minimum angular distance from center to ring edge in lat/lon degrees
            float minLatDelta = (ring - 1) * (_latRange / _gridSize);
            float minLonDelta = (ring - 1) * (_lonRange / _gridSize);

            float minDist = minLatDelta * minLatDelta + minLonDelta * minLonDelta;

            return minDist > bestDist;
        }

        private int CellIndex(float lat, float lon)
            => LatToRow(lat) * _gridSize + LonToCol(lon);

        private int LatToRow(float lat)
        {
            int row = (int)(((lat - _minLat) / _latRange) * _gridSize);

            return Math.Clamp(row, 0, _gridSize - 1);
        }

        private int LonToCol(float lon)
        {
            int col = (int)(((lon - _minLon) / _lonRange) * _gridSize);

            return Math.Clamp(col, 0, _gridSize - 1);
        }
    }
}