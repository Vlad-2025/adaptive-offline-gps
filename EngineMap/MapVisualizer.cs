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
using System.Threading.Tasks;
using System.Windows;
using Point = NetTopologySuite.Geometries.Point;

namespace adaptive_offline_gps.EngineMap
{
    /// <summary>
    /// Implements IRoutingVisualizer by drawing onto Mapsui layers.
    /// Visited nodes appear as small blue dots.
    /// The final path appears as a green line.
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

        private static readonly Mapsui.Styles.Color ColPath = new(0, 220, 100);     // green
        private static readonly Mapsui.Styles.Color ColPathOut = new(13, 13, 15);  // border

        private static readonly Mapsui.Styles.Color ColVisitedBright = new(68, 136, 255);   //  bright blue
        private static readonly Mapsui.Styles.Color ColVisitedDim = new(20, 40, 80);        //  dim blue


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

        //  IRoutingVisualizer

        public async Task OnNodeVisited(int nodeIndex)
        {
            if (_options.BenchmarkMode) return;
            if (_visitedLayer == null || _mapControl == null) return;

            var (mx, my) = NodeToMercator(nodeIndex);

            var feature = new GeometryFeature { Geometry = new Point(mx, my) };
            feature.Styles.Add(new SymbolStyle
            {
                Fill = new Brush(ColVisitedBright),
                Outline = new Pen(new Mapsui.Styles.Color(0, 0, 0, 0), 0),  // fully transparent
                SymbolScale = 0.15,
                SymbolType = SymbolType.Ellipse,
            });

            _visitedFeatures.Add(feature);
            _visitCount++;

            //  Batch redraws to avoid hammering the renderer
            if (_visitCount % GetRedrawInterval() == 0)
            {
                //  Only render the last N visited nodes - older ones stay as permanent dim dots
                //  or are dropped entirely. The path at the end shows where the algo went.
                var toRender = _visitedFeatures.Count <= MaxRenderedVisited
                    ? _visitedFeatures
                    : _visitedFeatures.GetRange(
                        _visitedFeatures.Count - MaxRenderedVisited, MaxRenderedVisited);

                _visitedLayer.Features = new List<GeometryFeature>(toRender);
                _mapControl.Refresh();
            }

            if (_options.StepDelayMs > 0)
                await Task.Delay(_options.StepDelayMs, _options.CancellationToken);

            else if (_options.YieldEveryN > 0 && _visitCount % _options.YieldEveryN == 0)
                await Task.Yield();
        }

        public Task OnEdgeRelaxed(int fromNode, int toNode)
        {
            //  Not visualized by default - too noisy at high node counts
            //  Implement if you want to show relaxation arrows later
            return Task.CompletedTask;
        }

        public Task OnPathFound(int[] pathNodes)
        {
            if (pathNodes.Length < 2) return Task.CompletedTask;

            //  Build a LineString from the path node coordinates
            var coordList = new List<Coordinate>();

            for (int i = 0; i < pathNodes.Length; i++)
            {
                if (i > 0)
                {
                    int from = pathNodes[i - 1];
                    int to = pathNodes[i];

                    //  Try forward direction: from -> to
                    bool found = false;
                    for (int e = _graph.EdgeHead[from]; e < _graph.EdgeHead[from + 1]; e++)
                    {
                        if (_graph.EdgeTarget[e] == to)
                        {
                            var pillarLat = _graph.EdgeGeomLat[e];
                            var pillarLon = _graph.EdgeGeomLon[e];

                            if (pillarLat != null)
                            {
                                for (int p = 0; p < pillarLat.Length; p++)
                                {
                                    var (pmx, pmy) = SphericalMercator.FromLonLat(pillarLon[p], pillarLat[p]);
                                    coordList.Add(new Coordinate(pmx, pmy));
                                }
                            }
                            found = true;
                            break;
                        }
                    }

                    // Try reverse direction: to -> from (for undirected roads,
                    // the edge might only be stored in one direction)
                    if (!found)
                    {
                        for (int e = _graph.EdgeHead[to]; e < _graph.EdgeHead[to + 1]; e++)
                        {
                            if (_graph.EdgeTarget[e] == from)
                            {
                                var pillarLat = _graph.EdgeGeomLat[e];
                                var pillarLon = _graph.EdgeGeomLon[e];

                                if (pillarLat != null && pillarLat.Length > 0)
                                {
                                    // Insert in reverse order since we're traversing the edge backwards
                                    for (int p = pillarLat.Length - 1; p >= 0; p--)
                                    {
                                        var (pmx, pmy) = SphericalMercator.FromLonLat(pillarLon[p], pillarLat[p]);
                                        coordList.Add(new Coordinate(pmx, pmy));
                                    }
                                }
                                break;
                            }
                        }
                    }
                }

                //  Add the junction node itself
                var (mx, my) = NodeToMercator(pathNodes[i]);
                coordList.Add(new Coordinate(mx, my));
            }

            // Debug: count how many edges found vs missing
            int foundCount = 0, missingCount = 0;

            for (int i = 0; i < pathNodes.Length - 1; i++)
            {
                int from = pathNodes[i];
                int to = pathNodes[i + 1];

                bool found = false;
                for (int e = _graph.EdgeHead[from]; e < _graph.EdgeHead[from + 1]; e++)
                    if (_graph.EdgeTarget[e] == to) { found = true; break; }

                if (!found)
                {
                    for (int e = _graph.EdgeHead[to]; e < _graph.EdgeHead[to + 1]; e++)
                        if (_graph.EdgeTarget[e] == from) { found = true; break; }
                }

                if (found) foundCount++;
                else missingCount++;
            }

            Debug.WriteLine($"Path geometry: {foundCount} edges found, {missingCount} missing " +
                            $"({pathNodes.Length} nodes, {pathNodes.Length - 1} edges)");

            var line = new LineString(coordList.ToArray());

            var feature = new GeometryFeature { Geometry = line };

            feature.Styles.Add(new VectorStyle
            {
                Line = new Pen(ColPath, 4),
            });

            _pathLayer.Features = new[] { feature };

            //  Only keep last 2000 visited dots — clear the rest
            var lastVisited = _visitedFeatures.Count <= MaxRenderedVisited
                ? new List<GeometryFeature>(_visitedFeatures)
                : _visitedFeatures.GetRange(
                    _visitedFeatures.Count - MaxRenderedVisited, MaxRenderedVisited);

            _visitedLayer.Features = lastVisited;

            // Free the full list — path line is now the visual record
            _visitedFeatures.Clear();

            _mapControl.Refresh();
            return Task.CompletedTask;
        }

        public Task OnNoPathFound()
        {
            var lastVisited = _visitedFeatures.Count <= MaxRenderedVisited
                ? new List<GeometryFeature>(_visitedFeatures)
                : _visitedFeatures.GetRange(_visitedFeatures.Count - MaxRenderedVisited, MaxRenderedVisited);

            _visitedLayer.Features = lastVisited;
            _visitedFeatures.Clear();
            _mapControl.Refresh();
            return Task.CompletedTask;
        }

        public void SetOptions(RoutingOptions options)
            => _options = options;

        /// <summary>
        /// Clear both layers and reset state. Call before each new run.
        /// </summary>
        public void Reset()
        {
            _visitedFeatures.Clear();
            _visitCount = 0;
            _visitedLayer.Features = Array.Empty<GeometryFeature>();
            _pathLayer.Features = Array.Empty<GeometryFeature>();
            _mapControl.Refresh();
        }

        //  Helpers
        private (double mx, double my) NodeToMercator(int nodeIndex)
        {
            float lat = _graph.NodeLat[nodeIndex];
            float lon = _graph.NodeLon[nodeIndex];
            return SphericalMercator.FromLonLat(lon, lat);
        }

        private void FadeOlderNodes()
        {
            int count = _visitedFeatures.Count;
            if (count == 0)
                return;

            //  Nodes older than the last 200 get progressively dimmer
            const int fadeWindow = 200;
            int fadeStart = Math.Max(0, count - fadeWindow - RedrawEvery);
            int fadeEnd = Math.Max(0, count - RedrawEvery);

            for (int i = fadeStart; i < fadeEnd; i++)
            {
                //  0 = bright, 1 = dim
                float t = 1f - ((float)(fadeEnd - i) / fadeWindow);
                t = Math.Clamp(t, 0f, 1f);

                var color = LerpColor(ColVisitedBright, ColVisitedDim, t);

                var style = _visitedFeatures[i].Styles.OfType<SymbolStyle>().FirstOrDefault();

                if (style != null)
                {
                    style.Fill = new Brush(color);
                    style.Outline = new Pen(color, 0);
                }
            }
        }

        private int GetRedrawInterval()
        {
            // Refresh less often when there are many features already
            if (_visitCount < 500) return 20;
            if (_visitCount < 2000) return 50;
            return 150;
        }

        private static Mapsui.Styles.Color LerpColor(
            Mapsui.Styles.Color a,
            Mapsui.Styles.Color b,
            float t)
        {
            return new Mapsui.Styles.Color(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }
    }
}