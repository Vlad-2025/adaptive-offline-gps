using adaptive_offline_gps.RoutingEngine;
using adaptive_offline_gps.RoutingEngine.GraphAlgorithms;
using adaptive_offline_gps.RoutingEngine.RoadGraphBuilding;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.UI.Wpf;
using NetTopologySuite.Geometries;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Point = NetTopologySuite.Geometries.Point;

namespace adaptive_offline_gps.EngineMap
{
    /// <summary>
    /// Implements IRoutingVisualizer by drawing onto Mapsui layers
    /// Visited nodes appear as small blue dots
    /// The final path appears as a green line
    /// Alternative paths (when present) appear as orange/yellow lines beneath the primary
    /// </summary>
    public class MapVisualizer : IRoutingVisualizer
    {
        private readonly MapControl _mapControl;
        private readonly RoadGraph _graph;
        private readonly MemoryLayer _visitedLayer;
        private readonly MemoryLayer _pathLayer;
        private RoutingOptions _options;

        //  Batch visited nodes -> redraw every N visits to avoid per-node refresh cost
        private readonly List<GeometryFeature> _visitedFeatures = new();
        private int _visitCount;
        private const int RedrawEvery = 50;
        private const int MaxRenderedVisited = 1000;

        private static readonly Mapsui.Styles.Color ColPath     = new(0, 220, 100);     //  green (primary)
        private static readonly Mapsui.Styles.Color ColPathOut  = new(13, 13, 15);      //  border

        //  Alternatives rendered in warm tones, distinct from the green primary
        //  Cycles through these if more alternatives exist
        private static readonly Mapsui.Styles.Color[] ColAltPaths = new[]
        {
            new Mapsui.Styles.Color(255, 165, 0),   //  orange
            new Mapsui.Styles.Color(255, 215, 0),   //  yellow
            new Mapsui.Styles.Color(220, 130, 200), //  pink-ish
        };

        private static readonly Mapsui.Styles.Color ColVisitedBright = new(68, 136, 255);
        private static readonly Mapsui.Styles.Color ColVisitedDim = new(20, 40, 80);


        public MapVisualizer(
                MapControl mapControl,
                RoadGraph graph,
                MemoryLayer visitedLayer,
                MemoryLayer pathLayer
            )
        {
            _mapControl = mapControl;
            _graph = graph;
            _visitedLayer = visitedLayer;
            _pathLayer = pathLayer;
            _options = RoutingOptions.Default;
        }

        //  IRoutingVisualizer =======================

        public async Task OnNodeVisited(int nodeIndex)
        {
            if (_options.BenchmarkMode) return;
            if (_visitedLayer == null || _mapControl == null) return;

            var (mx, my) = NodeToMercator(nodeIndex);

            var feature = new GeometryFeature { Geometry = new Point(mx, my) };
            feature.Styles.Add(new SymbolStyle
            {
                Fill = new Brush(ColVisitedBright),
                Outline = new Pen(new Mapsui.Styles.Color(0, 0, 0, 0), 0),
                SymbolScale = 0.15,
                SymbolType = SymbolType.Ellipse,
            });

            _visitedFeatures.Add(feature);
            _visitCount++;

            if (_visitCount % GetRedrawInterval() == 0)
            {
                var toRender = _visitedFeatures.Count <= MaxRenderedVisited
                    ? _visitedFeatures
                    : _visitedFeatures.GetRange(_visitedFeatures.Count - MaxRenderedVisited, MaxRenderedVisited);

                _visitedLayer.Features = new List<GeometryFeature>(toRender);
                _mapControl.Refresh();
            }

            if (_options.StepDelayMs > 0)
                await Task.Delay(_options.StepDelayMs, _options.CancellationToken);

            else if (_options.YieldEveryN > 0 && _visitCount % _options.YieldEveryN == 0)
                await Task.Yield();
        }

        public Task OnEdgeRelaxed(int fromNode, int toNode) => Task.CompletedTask;

        public Task OnPathFound(int[] pathNodes)
        {
            if (pathNodes.Length < 2) 
                return Task.CompletedTask;

            var feature = BuildPathFeature(pathNodes, ColPath, width: 4);
            _pathLayer.Features = new[] 
            { 
                feature 
            };

            FinalizeVisitedLayer();
            _mapControl.Refresh();

            return Task.CompletedTask;
        }

        public Task OnNoPathFound()
        {
            FinalizeVisitedLayer();
            _mapControl.Refresh();
            return Task.CompletedTask;
        }

        //  Alternatives ===========================================================

        /// <summary>
        /// Render the optimal route plus zero or more alternatives
        /// Optimal is drawn in primary color on top; alternatives are drawn beneath
        /// in distinct warm tones. If alternatives is empty or null this behaves
        /// like OnPathFound(optimal)
        /// </summary>
        public Task OnAlternativesFound(int[] optimal, int[][] alternatives)
        {
            if (optimal == null || optimal.Length < 2) 
                return Task.CompletedTask;

            var features = new List<GeometryFeature>();

            //  Add alternatives first so they render under the primary route
            //  Mapsui draws features in list order, so later entries appear on top
            if (alternatives != null)
            {
                for (int i = 0; i < alternatives.Length; i++)
                {
                    var alt = alternatives[i];

                    if (alt == null || alt.Length < 2) 
                        continue;

                    var color = ColAltPaths[i % ColAltPaths.Length];

                    //  Slightly thinner than primary so the primary visually dominates

                    features.Add(BuildPathFeature(alt, color, width: 3));
                }
            }

            //  Primary route last, drawn on top
            features.Add(BuildPathFeature(optimal, ColPath, width: 4));

            _pathLayer.Features = features;

            FinalizeVisitedLayer();
            _mapControl.Refresh();
            return Task.CompletedTask;
        }

        public void SetOptions(RoutingOptions options) => _options = options;

        public void Reset()
        {
            _visitedFeatures.Clear();
            _visitCount = 0;
            _visitedLayer.Features  = Array.Empty<GeometryFeature>();
            _pathLayer.Features     = Array.Empty<GeometryFeature>();
            _mapControl.Refresh();
        }

        //  Helpers ==================

        //  Build a styled LineString feature for a path
        //  Reconstructs full geometry including pillar nodes by scanning the base graph's CSR for each consecutive pair
        //  Falls back to straight lines for missing edges (shouldn't happen but you never know!)
        private GeometryFeature BuildPathFeature(int[] pathNodes, Mapsui.Styles.Color color, double width)
        {
            var coords  = BuildPathCoordinates(pathNodes);
            var line    = new LineString(coords.ToArray());
            var feature = new GeometryFeature 
            { 
                Geometry = line 
            };
            
            feature.Styles.Add(new VectorStyle 
            { 
                Line = new Pen(color, width) 
            });

            return feature;
        }

        private List<Coordinate> BuildPathCoordinates(int[] pathNodes)
        {
            var coords = new List<Coordinate>();
            int foundCount = 0, missingCount = 0;

            for (int i = 0; i < pathNodes.Length; i++)
            {
                if (i > 0)
                {
                    int from = pathNodes[i - 1];
                    int to = pathNodes[i];

                    bool found = false;

                    //  Forward direction
                    for (int e = _graph.EdgeHead[from]; e < _graph.EdgeHead[from + 1]; e++)
                    {
                        if (_graph.EdgeTarget[e] == to)
                        {
                            AppendPillars(_graph.EdgeGeomLat[e], _graph.EdgeGeomLon[e], reverse: false, coords);
                            found = true;
                            break;
                        }
                    }

                    //  Reverse direction (one-way roads stored only one way)
                    if (!found)
                    {
                        for (int e = _graph.EdgeHead[to]; e < _graph.EdgeHead[to + 1]; e++)
                        {
                            if (_graph.EdgeTarget[e] == from)
                            {
                                AppendPillars(_graph.EdgeGeomLat[e], _graph.EdgeGeomLon[e], reverse: true, coords);
                                found = true;
                                break;
                            }
                        }
                    }

                    if (found) foundCount++;
                    else missingCount++;
                }

                var (mx, my) = NodeToMercator(pathNodes[i]);
                coords.Add(new Coordinate(mx, my));
            }

            //  Only log on missing-edges to reduce noise;
            //  Alternatives can produce many rendered paths per query and we don't need the success line each time.
            if (missingCount > 0)
            {
                Debug.WriteLine($"Path geometry: {foundCount} edges found, {missingCount} missing " +
                                $"({pathNodes.Length} nodes, {pathNodes.Length - 1} edges)");
            }

            return coords;
        }

        private static void AppendPillars(float[] pillarLat, float[] pillarLon, bool reverse, List<Coordinate> coords)
        {
            if (pillarLat == null || pillarLat.Length == 0) 
                return;

            if (!reverse)
            {
                for (int p = 0; p < pillarLat.Length; p++)
                {
                    var (pmx, pmy) = SphericalMercator.FromLonLat(pillarLon[p], pillarLat[p]);
                    coords.Add(new Coordinate(pmx, pmy));
                }
            }
            else
            {
                for (int p = pillarLat.Length - 1; p >= 0; p--)
                {
                    var (pmx, pmy) = SphericalMercator.FromLonLat(pillarLon[p], pillarLat[p]);
                    coords.Add(new Coordinate(pmx, pmy));
                }
            }
        }

        //  Trim the visited-layer to its tail and clear the working list
        //  Called after any path/alternatives/no-path event finalizes the search
        private void FinalizeVisitedLayer()
        {
            var lastVisited = _visitedFeatures.Count <= MaxRenderedVisited
                ? new List<GeometryFeature>(_visitedFeatures)
                : _visitedFeatures.GetRange(_visitedFeatures.Count - MaxRenderedVisited, MaxRenderedVisited);

            _visitedLayer.Features = lastVisited;
            _visitedFeatures.Clear();
        }

        private (double mx, double my) NodeToMercator(int nodeIndex)
        {
            float lat = _graph.NodeLat[nodeIndex];
            float lon = _graph.NodeLon[nodeIndex];
            return SphericalMercator.FromLonLat(lon, lat);
        }

        private int GetRedrawInterval()
        {
            if (_visitCount < 500) return 20;
            if (_visitCount < 2000) return 50;
            return 150;
        }
    }
}