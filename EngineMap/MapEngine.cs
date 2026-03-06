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

namespace adaptive_offline_gps.EngineMap
{
    public class MapEngine
    {
        private readonly MapControl _mapControl;
        private readonly MemoryLayer _roadLayer;
        private readonly RoadLodSelector _lodSelector;

        private MapEngine(MapControl mapControl, RoadLodSelector lodSelector)
        {
            _mapControl = mapControl;
            _lodSelector = lodSelector;

            var map = new Map();

            map.Layers.Add(Mapsui.Tiling.OpenStreetMap.CreateTileLayer());

            _roadLayer = new MemoryLayer { Name = "Roads" };
            map.Layers.Add(_roadLayer);

            _mapControl.Map = map;
            map.Navigator.ViewportChanged += OnViewportChanged;
        }

        public static async Task<MapEngine> CreateAsync(MapControl mapControl, string osmFilePath)
        {
            RoadGraph graph;

            var indexBuilder = new RoadIndexBuilder();
            var index = await indexBuilder.BuildAsync(osmFilePath);

            var lodSelector = new RoadLodSelector(index);

            if (!RoadGraphSerializer.TryLoadCached(osmFilePath, out graph))
            {
                var graphBuilder = new RoadGraphBuilder();

                graph = graphBuilder.Build(osmFilePath);

                RoadGraphSerializer.Save(graph, Path.ChangeExtension(osmFilePath, ".rgph"));
            }

            return new MapEngine(mapControl, lodSelector);
        }

        private void OnViewportChanged(object sender, EventArgs e)
        {
            var viewport = _mapControl.Map.Navigator.Viewport;
            var extent = viewport.ToExtent();

            if (extent == null)
                return;

            var envelope = new Envelope(
                extent.MinX, extent.MaxX,
                extent.MinY, extent.MaxY);

            var visible = _lodSelector.GetVisible(
                envelope,
                viewport.Resolution);

            _roadLayer.Features = visible;
            _mapControl.Refresh();

            if (_mapControl.Parent is Window window)
            {
                window.Title = $"Resolution: {viewport.Resolution:F2} | Features: {visible.Count}";
            }

        }
    }
}
