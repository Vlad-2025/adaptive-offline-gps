using adaptive_offline_gps.RoutingEngine;
using adaptive_offline_gps.RoutingEngine.RoadGraphBuilding;
using adaptive_offline_gps.UI;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.UI.Wpf;
using NetTopologySuite.Geometries;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using Point = NetTopologySuite.Geometries.Point;

namespace adaptive_offline_gps.EngineMap
{
    public class MapRoutingController
    {
        private readonly MapControl _mapControl;
        private readonly NodeGridIndex _nodeIndex;
        private readonly RoadGraph _graph;
        private readonly MapClickPopup _popup;
        private readonly Canvas _overlayCanvas;
        private readonly Rectangle _dismissOverlay;
        private readonly RouteInfoPanel _routeInfoPanel;

        private readonly MemoryLayer _startLayer = new() { Name = "StartPin" };
        private readonly MemoryLayer _endLayer = new() { Name = "EndPin" };

        public int StartNodeIndex { get; private set; } = -1;
        public int EndNodeIndex { get; private set; } = -1;

        public event Action<int, int>? RouteReadyToRun;

        private static readonly Mapsui.Styles.Color PinStart = new(0, 220, 100);
        private static readonly Mapsui.Styles.Color PinEnd = new(255, 60, 80);
        private static readonly Mapsui.Styles.Color PinBorder = new(13, 13, 15);

        public MapRoutingController(
            MapControl mapControl,
            NodeGridIndex nodeIndex,
            RoadGraph graph,
            MapClickPopup popup,
            Canvas overlayCanvas,
            Rectangle dismissOverlay,
            RouteInfoPanel routeInfoPanel)
        {
            _mapControl = mapControl;
            _nodeIndex = nodeIndex;
            _graph = graph;
            _popup = popup;
            _overlayCanvas = overlayCanvas;
            _dismissOverlay = dismissOverlay;
            _routeInfoPanel = routeInfoPanel;

            mapControl.Map.Layers.Add(_startLayer);
            mapControl.Map.Layers.Add(_endLayer);

            mapControl.Info += OnMapInfo;
            _popup.SetStartRequested += OnSetStart;
            _popup.SetEndRequested += OnSetEnd;
            _popup.PopupOpened += OnPopupOpened;
            _popup.PopupClosed += OnPopupClosed;

            _dismissOverlay.MouseDown += (_, _) => _popup.Hide();
        }

        public void Detach()
        {
            _mapControl.Info -= OnMapInfo;
            _popup.SetStartRequested -= OnSetStart;
            _popup.SetEndRequested -= OnSetEnd;
            _popup.PopupOpened -= OnPopupOpened;
            _popup.PopupClosed -= OnPopupClosed;
            _mapControl.Map.Layers.Remove(_startLayer);
            _mapControl.Map.Layers.Remove(_endLayer);
        }

        //  Dismiss overlay
        private void OnPopupOpened()
        {
            _dismissOverlay.Width = _overlayCanvas.ActualWidth;
            _dismissOverlay.Height = _overlayCanvas.ActualHeight;
            _dismissOverlay.Visibility = Visibility.Visible;
        }

        private void OnPopupClosed()
            => _dismissOverlay.Visibility = Visibility.Collapsed;

        //  Map click
        private void OnMapInfo(object? sender, MapInfoEventArgs e)
        {
            _popup.Hide();

            if (e.WorldPosition == null) return;

            var world = e.WorldPosition;
            var (lon, lat) = SphericalMercator.ToLonLat(world.X, world.Y);

            int nodeIdx = _nodeIndex.FindNearestNode(lat, lon);
            if (nodeIdx < 0) return;

            var screenPos = _mapControl.Map.Navigator.Viewport
                .WorldToScreen(world.X, world.Y);

            _popup.Show(screenPos.X, screenPos.Y, nodeIdx, lat, lon);
        }

        //  Popup actions
        private void OnSetStart()
        {
            StartNodeIndex = _popup.NearestNodeIndex;
            PlacePin(_startLayer, StartNodeIndex, PinStart, "S");
            _routeInfoPanel.SetStart(_popup.ClickLat, _popup.ClickLon);
            CheckReady();
        }

        private void OnSetEnd()
        {
            EndNodeIndex = _popup.NearestNodeIndex;
            PlacePin(_endLayer, EndNodeIndex, PinEnd, "E");
            _routeInfoPanel.SetEnd(_popup.ClickLat, _popup.ClickLon);
            CheckReady();
        }

        private void CheckReady()
        {
            if (StartNodeIndex >= 0 && EndNodeIndex >= 0)
                RouteReadyToRun?.Invoke(StartNodeIndex, EndNodeIndex);
        }

        //  Pin placement
        private void PlacePin(MemoryLayer layer, int nodeIdx,
            Mapsui.Styles.Color color, string label)
        {
            float lat = _graph.NodeLat[nodeIdx];
            float lon = _graph.NodeLon[nodeIdx];

            var (mx, my) = SphericalMercator.FromLonLat(lon, lat);

            var point = new GeometryFeature { Geometry = new Point(mx, my) };

            point.Styles.Add(new SymbolStyle
            {
                Fill = new Brush(color),
                Outline = new Pen(PinBorder, 2),
                SymbolScale = 0.6,
                SymbolType = SymbolType.Ellipse,
            });

            point.Styles.Add(new LabelStyle
            {
                Text = label,
                ForeColor = PinBorder,
                Font = new Font { FontFamily = "Consolas", Size = 9 },
                HorizontalAlignment = LabelStyle.HorizontalAlignmentEnum.Center,
                VerticalAlignment = LabelStyle.VerticalAlignmentEnum.Center,
                Offset = new Offset(0, 0),
            });

            layer.Features = new[] { point };
            _mapControl.Refresh();
        }

        public void ClearPins()
        {
            StartNodeIndex = -1;
            EndNodeIndex = -1;
            _startLayer.Features = Array.Empty<GeometryFeature>();
            _endLayer.Features = Array.Empty<GeometryFeature>();
            _mapControl.Refresh();
        }
    }
}