using Mapsui.Layers;
using Mapsui.UI.Wpf;
using Mapsui;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using adaptive_offline_gps.Data;
using Mapsui.Extensions;
using NetTopologySuite.Geometries;
using System.Windows.Threading;
using System.Windows;
using adaptive_offline_gps.RoutingEngine;
using System.IO;
using Mapsui.Projections;

namespace adaptive_offline_gps.EngineMap
{
    public delegate void GraphReadyCallback(int nodeCount, int edgeCount, bool fromCache);
    public delegate void ViewportChangedCallback(double resolution, double zoom, double centerLon,  double centerLat, int featureCount);

    public class MapEngine
    {
        private readonly MapControl _mapControl;
        private readonly MemoryLayer _roadLayer;
        private readonly RoadLodSelector _lodSelector;
        private readonly ViewportChangedCallback _onViewportChanged;

        private Envelope? _lastEnvelope;
        private double _lastResolution;

        private MapEngine(MapControl mapControl, RoadLodSelector lodSelector, ViewportChangedCallback? onViewportChanged)
        {
            _mapControl = mapControl;
            _lodSelector = lodSelector;
            _onViewportChanged = onViewportChanged;

            var map = new Map();

            map.Layers.Add(Mapsui.Tiling.OpenStreetMap.CreateTileLayer());

            _roadLayer = new MemoryLayer { Name = "Roads" };
            map.Layers.Add(_roadLayer);

            _mapControl.Map = map;
            map.Navigator.ViewportChanged += OnViewportChanged;
        }

        public static async Task<MapEngine> CreateAsync(
            MapControl mapControl, string osmFilePath, bool forceRebuild = false, 
            GraphReadyCallback? onGraphReady = null, 
            ViewportChangedCallback? onViewportChanged = null)
        {
            RoadGraph graph;

            var indexBuilder = new RoadIndexBuilder();

            var index = await indexBuilder.BuildAsync(osmFilePath);

            var lodSelector = new RoadLodSelector(index);

            bool fromCache = false;

            if (!forceRebuild && RoadGraphSerializer.TryLoadCached(osmFilePath, out graph))
            {
                fromCache = true;
            }
            else
            { 
                var graphBuilder = new RoadGraphBuilder();

                graph = graphBuilder.Build(osmFilePath);

                RoadGraphSerializer.Save(graph, Path.ChangeExtension(osmFilePath, ".rgph"));
            }

            onGraphReady?.Invoke(graph.NodeCount, graph.EdgeCount, fromCache);

            return new MapEngine(mapControl, lodSelector, onViewportChanged);
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
    }
}
