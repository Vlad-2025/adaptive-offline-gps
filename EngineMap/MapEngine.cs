using adaptive_offline_gps.Data;
using adaptive_offline_gps.RoutingEngine;
using adaptive_offline_gps.RoutingEngine.GraphAlgorithms;
using adaptive_offline_gps.RoutingEngine.RoadGraphBuilding;
using BruTile.Predefined;
using BruTile.Web;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Layers;
using Mapsui.Limiting;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.Tiling.Layers;
using Mapsui.UI.Wpf;
using NetTopologySuite.Geometries;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace adaptive_offline_gps.EngineMap
{
    public delegate void GraphReadyCallback(int nodeCount, int edgeCount, bool fromCache);
    public delegate void ViewportChangedCallback(double resolution, double zoom, double centerLon,  double centerLat, int featureCount);

    public class MapEngine : IDisposable
    {
        private readonly MapControl _mapControl;
        private readonly MemoryLayer _roadLayer, _visitedLayer, _pathLayer;
        private readonly RoadLodSelector _lodSelector;
        private readonly ViewportChangedCallback? _onViewportChanged;
        public MapVisualizer? _mapVisualizer;

        private Envelope? _lastEnvelope;
        private double _lastResolution;

        public  NodeGridIndex? NodeIndex {  get; }

        //  Road Graph
        private RoadGraph _graph;
        public  RoadGraph Graph => _graph;

        //  Contraction Hierarchies graf
        private CHGraph _chGraph;
        public  CHGraph ChGraph => _chGraph;


        public  MapVisualizer Visualizer { get; }

        public double CurrentResolution => _mapControl.Map?.Navigator?.Viewport.Resolution ?? double.MaxValue;

        private static TileLayer CreateOsmTileLayer()
        {
            var tileSource = new HttpTileSource(
                new GlobalSphericalMercator(),
                "https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png",
                new[] { "a", "b", "c" },
                name: "OpenStreetMap",
                configureHttpRequestMessage: msg =>
                    msg.Headers.TryAddWithoutValidation("User-Agent",
                        "OSMRoutingBenchmark/1.0 (your@email.com)"));

            return new TileLayer(tileSource) { Name = "OSM" };
        }

        private MapEngine
            (
            MapControl mapControl,
            RoadLodSelector lodSelector,
            ViewportChangedCallback? onViewportChanged,
            NodeGridIndex nodeIndex,
            RoadGraph graph,
            CHGraph chGraph
            )
        {
            _mapControl = mapControl;
            _lodSelector = lodSelector;
            _onViewportChanged = onViewportChanged;
            NodeIndex = nodeIndex;
            _graph = graph;
            _chGraph = chGraph;

            var map = new Map();

            map.Layers.Add(CreateOsmTileLayer());

            _roadLayer      = new MemoryLayer { 
                Name = "Roads"
            };

            map.Layers.Add(_roadLayer);

            _visitedLayer   = new MemoryLayer { 
                Name = "Visited",
                Style = new SymbolStyle
                {
                    Fill = new Brush(new Mapsui.Styles.Color(68, 136, 255, 180)), // alpha=180
                    Outline = new Pen(new Mapsui.Styles.Color(0, 0, 0, 0), 0),
                    SymbolScale = 0.12,
                    SymbolType = SymbolType.Ellipse,
                }
            };
            _pathLayer      = new MemoryLayer { 
                Name = "Path",
                Style = null
            };

            map.Layers.Add(_visitedLayer);
            map.Layers.Add(_pathLayer);

            Visualizer = new MapVisualizer(
                _mapControl, 
                _graph, 
                _visitedLayer, 
                _pathLayer
            );


            //  needs some work, maybe not
            map.Navigator.OverridePanBounds = new MRect(
                -20037508.34, -20037508.34,
                 20037508.34, 20037508.34);

            map.BackColor = new Mapsui.Styles.Color(13,13,15);  //  same color as the control background

            _mapControl.Map = map;
            map.Navigator.ViewportChanged += OnViewportChanged;
        }

        public static async Task<MapEngine> CreateAsync(
                MapControl mapControl, 
                string osmFilePath, 
                bool forceRebuild = false, 
                GraphReadyCallback? onGraphReady = null, 
                ViewportChangedCallback? onViewportChanged = null,
                Action<string>? onStatusUpdate = null
            )
        {
            var indexProgress = new Progress<string>(msg => onStatusUpdate?.Invoke(msg));

            var indexBuilder = new RoadIndexBuilder();

            var index = await indexBuilder.BuildAsync(osmFilePath);

            var lodSelector = new RoadLodSelector(index);
            
            RoadGraph graph;
            bool fromCache = false;

            if (!forceRebuild && RoadGraphSerializer.TryLoadCached(osmFilePath, out graph))
            {
                fromCache = true;
                onStatusUpdate?.Invoke("road graph loaded from cache");
            }
            else
            {
                onStatusUpdate?.Invoke("parsing road graph...");

                graph = await Task.Run(() =>
                {
                    var g = new RoadGraphBuilder().Build(osmFilePath);
                    return g;
                });

                onStatusUpdate?.Invoke("saving road graph...");

                await Task.Run(() =>
                {
                    RoadGraphSerializer.Save(graph, Path.ChangeExtension(osmFilePath, ".rgph"));
                });
            }

            NodeGridIndex nodeIndex;

            if(graph is RoadGraph && false)
            {
                nodeIndex = null!;
            }

            onStatusUpdate?.Invoke("building spatial index...");

            nodeIndex = await Task.Run(() => new NodeGridIndex(graph));

            CHGraph chGraph;

            if (!forceRebuild && CHGraphSerializer.TryLoadCached(osmFilePath, graph, out chGraph))
            {
                onStatusUpdate?.Invoke("CH loaded from cache");
            }
            else
            {
                //onGraphReady?.Invoke(graph.NodeCount, graph.EdgeCount, fromCache);

                onStatusUpdate?.Invoke("building contraction hierarchy...");

                chGraph = await Task.Run(() => new CHPreprocessor().Preprocess(graph));

                onStatusUpdate?.Invoke("saving CH cache");

                await Task.Run(()=> CHGraphSerializer.Save(chGraph, Path.ChangeExtension(osmFilePath, ".chgr")));
                
                int shortcuts = chGraph.ForwardUp.Sum(a => a.Count(e => e.ShortcutMid != -1));
                Debug.WriteLine($"CH has {shortcuts} shortcuts");
            }

            onGraphReady?.Invoke(graph.NodeCount, graph.EdgeCount, fromCache);

            return new MapEngine(mapControl, lodSelector, onViewportChanged, nodeIndex, graph, chGraph);
        }

        private void OnViewportChanged(object sender, EventArgs e)
        {
            var viewport = _mapControl.Map.Navigator.Viewport;
            var extent = viewport.ToExtent();

            if (extent == null)
                return;

            var resolution = viewport.Resolution;

            var envelope = new Envelope(
                extent.MinX, extent.MaxX,
                extent.MinY, extent.MaxY);

            if (_lastEnvelope != null
                && Math.Abs(resolution - _lastResolution) < resolution * 0.05
                && _lastEnvelope.Contains(envelope))
                return;

            _lastEnvelope = new Envelope(envelope);
            _lastEnvelope.ExpandBy(envelope.Width * 0.2);
            _lastResolution = resolution;

            var visible = _lodSelector.GetVisible(envelope, resolution);

            _roadLayer.Features = visible;
            _mapControl.Refresh();

            double cx = viewport.CenterX, cy = viewport.CenterY;
            
            var (lon, lat) = SphericalMercatorToLonLat(cx, cy);

            double zoom = Math.Log(156543.0 / resolution, 2);

            _onViewportChanged?.Invoke(resolution, zoom, lon, lat, visible.Count);
        }

        private static (double lon, double lat) SphericalMercatorToLonLat(double x, double y)
        {
            const double R = 6_378_137.0;

            double lon = (x / R) * (180.0 / Math.PI);
            double lat = (2 * Math.Atan(Math.Exp(y / R)) - Math.PI / 2) * (180.0 / Math.PI);
        
            return (lon, lat);
        }

        public void Dispose()
        {
            _mapControl.Map.Navigator.ViewportChanged -= OnViewportChanged;
            _roadLayer.Features     = new List<IFeature>();
            _mapControl.Map         = new Map();
            _visitedLayer.Features  = Array.Empty<IFeature>();
            _pathLayer.Features     = Array.Empty<IFeature>();
        }
    }
}
