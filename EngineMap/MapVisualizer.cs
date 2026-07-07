using adaptive_offline_gps.RoutingEngine;
using adaptive_offline_gps.RoutingEngine.GraphAlgorithms;
using adaptive_offline_gps.RoutingEngine.RoadGraphBuilding;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Rendering;
using Mapsui.Rendering.Skia;
using Mapsui.Rendering.Skia.SkiaStyles;
using Mapsui.Styles;
using Mapsui.UI.Wpf;
using NetTopologySuite.Geometries;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace adaptive_offline_gps.EngineMap
{
    /// <summary>
    /// Style that *carries* the visited-node point cloud in world (Mercator) coordinates.
    /// The paired VisitedDotsRenderer draws the entire cloud in one Skia call,
    /// so there is exactly ONE feature and ONE style no matter how many nodes are visited.
    ///
    /// Thread-safety: the search thread appends, the render thread snapshots.
    /// Appends only grow the count; a resize swaps in a new array, but a snapshot
    /// taken under the lock always sees a consistent (array, count) pair.
    /// </summary>
    public sealed class VisitedDotsStyle : BaseStyle
    {
        private readonly object _sync = new();
        private double[] _xs = new double[4096];
        private double[] _ys = new double[4096];
        private int _count;

        public SKColor DotColor { get; set; } = new SKColor(68, 136, 255);
        public float DotSize { get; set; } = 5f;

        public int Count { get { lock (_sync) return _count; } }

        public void Add(double mercX, double mercY)
        {
            lock (_sync)
            {
                if (_count == _xs.Length)
                {
                    Array.Resize(ref _xs, _xs.Length * 2);
                    Array.Resize(ref _ys, _ys.Length * 2);
                }
                _xs[_count] = mercX;
                _ys[_count] = mercY;
                _count++;
            }
        }

        public void Clear()
        {
            lock (_sync) _count = 0;
        }

        /// <summary>Consistent snapshot for the render thread. Do not mutate the arrays.</summary>
        public (double[] xs, double[] ys, int count) Snapshot()
        {
            lock (_sync) 
                return (_xs, _ys, _count);
        }
    }

    /// <summary>
    /// Draws every visited node in a single canvas.DrawPoints call.
    /// World -> screen is recomputed each frame, so pan/zoom stays perfectly aligned
    /// (this is what naive "draw once to a bitmap" approaches get wrong).
    ///
    /// NOTE: written against Mapsui 5.x (ISkiaStyleRenderer lives in
    /// Mapsui.Rendering.Skia.SkiaStyles, and Draw takes a RenderService).
    /// </summary>
    public sealed class VisitedDotsRenderer : ISkiaStyleRenderer
    {
        public bool Draw(SKCanvas canvas, Viewport viewport, ILayer layer, IFeature feature,
                         IStyle style, RenderService renderService, long iteration)
        {
            if (style is not VisitedDotsStyle dots)
                return false;

            var (xs, ys, count) = dots.Snapshot();

            if (count == 0)
                return true;

            //  Cull to viewport (with margin) and transform world -> screen.
            //  Per-frame allocation of the culled array is fine: gen0 garbage,
            //  and culling usually keeps it far smaller than the full cloud.
            float w = (float)viewport.Width;
            float h = (float)viewport.Height;

            const float margin = 20f;

            var screen = new List<SKPoint>(Math.Min(count, 65536));
            for (int i = 0; i < count; i++)
            {
                var (px, py) = viewport.WorldToScreenXY(xs[i], ys[i]);
                float sx = (float)px, sy = (float)py;

                if (sx < -margin || sx > w + margin || sy < -margin || sy > h + margin)
                    continue;

                screen.Add(new SKPoint(sx, sy));
            }

            if (screen.Count == 0)
                return true;

            using var paint = new SKPaint
            {
                Color = dots.DotColor,
                StrokeWidth = dots.DotSize,
                StrokeCap = SKStrokeCap.Round,   //  round -> dots instead of squares
                IsAntialias = false,             //  big win at high counts; flip to true if you prefer
                Style = SKPaintStyle.Stroke,
            };

            canvas.DrawPoints(SKPointMode.Points, screen.ToArray(), paint);

            return true;
        }
    }

    /// <summary>
    /// Implements IRoutingVisualizer by drawing onto Mapsui layers.
    /// Visited nodes are rendered as ONE feature via a custom Skia renderer
    /// (single DrawPoints call per frame), the final path as a green line,
    /// alternatives in warm tones beneath the primary.
    ///
    /// IMPORTANT: call MapVisualizer.RegisterRenderer() once at startup,
    /// otherwise the visited layer renders nothing.
    /// </summary>
    public class MapVisualizer : IRoutingVisualizer
    {
        private readonly MapControl _mapControl;
        private readonly RoadGraph _graph;
        private readonly MemoryLayer _visitedLayer;
        private readonly MemoryLayer _pathLayer;
        private RoutingOptions _options;

        private readonly VisitedDotsStyle _dots = new();
        private int _visitCount;

        //  Time-based repaint throttle (~30 fps). Count-based throttling ties the
        //  refresh rate to expansion speed; time-based keeps it constant.
        private long _lastRefreshMs;
        private const long RefreshIntervalMs = 33;

        private static readonly Mapsui.Styles.Color ColPath = new(0, 220, 100);      //  green (primary)

        //  Alternatives rendered in warm tones, distinct from the green primary
        //  Cycles through these if more alternatives exist
        private static readonly Mapsui.Styles.Color[] ColAltPaths = new[]
        {
            new Mapsui.Styles.Color(255, 165, 0),   //  orange
            new Mapsui.Styles.Color(255, 215, 0),   //  yellow
            new Mapsui.Styles.Color(220, 130, 200), //  pink-ish
        };

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

            //  One feature
            //  Its geometry only exists to give the layer a non-cullable extent
            //  The custom renderer ignores it and draws the point cloud held by the style

            var carrier = new GeometryFeature { Geometry = BuildGraphBoundsGeometry() };
            _visitedLayer.Style = _dots;                    // layer style = our custom style, nothing else
            _visitedLayer.Features = new[] { carrier };     // carrier has no styles of its own
        }

        /// <summary>
        /// Registers the custom point-cloud renderer. Call once at startup
        /// (before the first search), e.g. in MainWindow's constructor:
        ///     MapVisualizer.RegisterRenderer();
        /// Registration is global/static in Mapsui 5 — no MapControl needed.
        /// </summary>
        public static void RegisterRenderer()
        {
            MapRenderer.RegisterStyleRenderer(typeof(VisitedDotsStyle), new VisitedDotsRenderer());
        }

        //  IRoutingVisualizer ===========================================================

        public async Task OnNodeVisited(int nodeIndex)
        {
            if (_options.BenchmarkMode) 
                return;

            if (_visitedLayer == null || _mapControl == null) 
                return;

            var (mx, my) = NodeToMercator(nodeIndex);
            _dots.Add(mx, my);
            _visitCount++;

            //  Repaint at most every RefreshIntervalMs
            //  RefreshGraphics (not Refresh) only invalidates the canvas
            //  Refresh also re-triggers data fetch on every layer, including the tile layer
            long now = Environment.TickCount64;

            if (now - _lastRefreshMs >= RefreshIntervalMs)
            {
                _lastRefreshMs = now;
                _mapControl.RefreshGraphics();
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
            _pathLayer.Features = new[] { feature };

            _mapControl.RefreshGraphics();

            return Task.CompletedTask;
        }

        public Task OnNoPathFound()
        {
            _mapControl.RefreshGraphics();
            return Task.CompletedTask;
        }

        //  Alternatives ===========================================================

        /// <summary>
        /// Render the optimal route plus zero or more alternatives.
        /// Optimal is drawn in primary color on top; alternatives are drawn beneath
        /// in distinct warm tones. If alternatives is empty or null this behaves
        /// like OnPathFound(optimal).
        /// </summary>
        public Task OnAlternativesFound(int[] optimal, int[][] alternatives)
        {
            if (optimal == null || optimal.Length < 2)
                return Task.CompletedTask;

            var features = new List<GeometryFeature>();

            //  Add alternatives first so they render under the primary route
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
            _mapControl.RefreshGraphics();
            return Task.CompletedTask;
        }

        public void SetOptions(RoutingOptions options) => _options = options;

        public void Reset()
        {
            _dots.Clear();
            _visitCount = 0;
            _lastRefreshMs = 0;
            _pathLayer.Features = Array.Empty<GeometryFeature>();
            _mapControl.RefreshGraphics();
        }

        //  Helpers ===============================================

        //  A diagonal across the graph's bounding box
        private LineString BuildGraphBoundsGeometry()
        {
            float minLat = float.MaxValue, maxLat = float.MinValue;
            float minLon = float.MaxValue, maxLon = float.MinValue;

            for (int i = 0; i < _graph.NodeLat.Length; i++)
            {
                if (_graph.NodeLat[i] < minLat) 
                    minLat = _graph.NodeLat[i];

                if (_graph.NodeLat[i] > maxLat) 
                    maxLat = _graph.NodeLat[i];

                if (_graph.NodeLon[i] < minLon) 
                    minLon = _graph.NodeLon[i];

                if (_graph.NodeLon[i] > maxLon) 
                    maxLon = _graph.NodeLon[i];
            }

            var (x0, y0) = SphericalMercator.FromLonLat(minLon, minLat);
            var (x1, y1) = SphericalMercator.FromLonLat(maxLon, maxLat);

            return new LineString(new[] { new Coordinate(x0, y0), new Coordinate(x1, y1) });
        }

        //  Build a styled LineString feature for a path
        //  Reconstructs full geometry including pillar nodes by scanning the base graph's CSR for each consecutive pair
        //  Falls back to straight lines for missing edges (shouldnt happen but you never know)
        private GeometryFeature BuildPathFeature(int[] pathNodes, Mapsui.Styles.Color color, double width)
        {
            var coords = BuildPathCoordinates(pathNodes);
            var line = new LineString(coords.ToArray());
            var feature = new GeometryFeature { Geometry = line };

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

            //  Only log on missing-edges to reduce noise
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

        private (double mx, double my) NodeToMercator(int nodeIndex)
        {
            float lat = _graph.NodeLat[nodeIndex];
            float lon = _graph.NodeLon[nodeIndex];
            return SphericalMercator.FromLonLat(lon, lat);
        }
    }
}