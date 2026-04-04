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
            if (_visitCount % RedrawEvery == 0)
            {
                FadeOlderNodes();
                _visitedLayer.Features = new List<GeometryFeature>(_visitedFeatures);
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
            var coords = new Coordinate[pathNodes.Length];

            for (int i = 0; i < pathNodes.Length; i++)
            {
                var (mx, my) = NodeToMercator(pathNodes[i]);
                coords[i] = new Coordinate(mx, my);
            }

            var line = new LineString(coords);
            var feature = new GeometryFeature { Geometry = line };

            feature.Styles.Add(new VectorStyle
            {
                Line = new Pen(ColPath, 4),
            });

            _pathLayer.Features = new[] { feature };

            //  Final flush of visited dots
            _visitedLayer.Features = new List<GeometryFeature>(_visitedFeatures);
            _mapControl.Refresh();

            return Task.CompletedTask;
        }

        public Task OnNoPathFound()
        {
            _visitedLayer.Features = new List<GeometryFeature>(_visitedFeatures);
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
            int fadeStart   = Math.Max(0, count - fadeWindow - RedrawEvery);
            int fadeEnd     = Math.Max(0, count - RedrawEvery);

            for (int i = fadeStart; i < fadeEnd; i++)
            {
                //  0 = bright, 1 = dim
                float t = 1f - ((float)(fadeEnd - i) /  fadeWindow);
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