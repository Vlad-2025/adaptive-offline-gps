using adaptive_offline_gps.RoutingEngine;
using adaptive_offline_gps.RoutingEngine.Bookmarks;
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
using System.Collections.Generic;
using System.Linq;
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
        private readonly BookmarkPopup _bookmarkPopup;
        private readonly Canvas _overlayCanvas;
        private readonly Rectangle _dismissOverlay;
        private readonly RouteInfoPanel _routeInfoPanel;
        private readonly BookmarkStore _bookmarks;

        private readonly MemoryLayer _startLayer    = new() { Name = "StartPin" };
        private readonly MemoryLayer _endLayer      = new() { Name = "EndPin" };
        private readonly MemoryLayer _bookmarkLayer = new() { Name = "Bookmarks" };

        public int StartNodeIndex { get; private set; } = -1;
        public int EndNodeIndex { get; private set; } = -1;

        public event Action<int, int>? RouteReadyToRun;

        private static readonly Mapsui.Styles.Color PinStart = new(0, 220, 100);
        private static readonly Mapsui.Styles.Color PinEnd = new(255, 60, 80);
        private static readonly Mapsui.Styles.Color PinBookmark = new(68, 136, 255);
        private static readonly Mapsui.Styles.Color PinBorder = new(13, 13, 15);

        //  Click anywhere within this many pixels of a bookmark counts as a
        //  click on that bookmark (not on the road below).
        private const double BookmarkHitRadiusPixels = 16.0;

        public MapRoutingController(
            MapControl mapControl,
            NodeGridIndex nodeIndex,
            RoadGraph graph,
            MapClickPopup popup,
            BookmarkPopup bookmarkPopup,
            Canvas overlayCanvas,
            Rectangle dismissOverlay,
            RouteInfoPanel routeInfoPanel,
            BookmarkStore bookmarks)
        {
            _mapControl = mapControl;
            _nodeIndex = nodeIndex;
            _graph = graph;
            _popup = popup;
            _bookmarkPopup = bookmarkPopup;
            _overlayCanvas = overlayCanvas;
            _dismissOverlay = dismissOverlay;
            _routeInfoPanel = routeInfoPanel;
            _bookmarks = bookmarks;

            mapControl.Map.Layers.Add(_startLayer);
            mapControl.Map.Layers.Add(_endLayer);
            mapControl.Map.Layers.Add(_bookmarkLayer);

            mapControl.Info += OnMapInfo;

            //  Click-on-empty-map popup
            _popup.SetStartRequested += OnSetStart;
            _popup.SetEndRequested += OnSetEnd;
            _popup.BookmarkRequested += OnBookmarkAtClick;
            _popup.PopupOpened += OnAnyPopupOpened;
            _popup.PopupClosed += OnAnyPopupClosed;

            //  Click-on-existing-bookmark popup
            _bookmarkPopup.SetStartRequested += OnSetStartFromBookmark;
            _bookmarkPopup.SetEndRequested += OnSetEndFromBookmark;
            _bookmarkPopup.RenameRequested += OnRenameBookmark;
            _bookmarkPopup.DeleteRequested += OnDeleteBookmark;
            _bookmarkPopup.PopupOpened += OnAnyPopupOpened;
            _bookmarkPopup.PopupClosed += OnAnyPopupClosed;

            _dismissOverlay.MouseDown += OnDismissOverlayClicked;

            //  Render any pre-existing bookmarks (loaded from disk)
            RefreshBookmarkLayer();
        }

        public void Detach()
        {
            _mapControl.Info -= OnMapInfo;

            _popup.SetStartRequested -= OnSetStart;
            _popup.SetEndRequested -= OnSetEnd;
            _popup.BookmarkRequested -= OnBookmarkAtClick;
            _popup.PopupOpened -= OnAnyPopupOpened;
            _popup.PopupClosed -= OnAnyPopupClosed;

            _bookmarkPopup.SetStartRequested -= OnSetStartFromBookmark;
            _bookmarkPopup.SetEndRequested -= OnSetEndFromBookmark;
            _bookmarkPopup.RenameRequested -= OnRenameBookmark;
            _bookmarkPopup.DeleteRequested -= OnDeleteBookmark;
            _bookmarkPopup.PopupOpened -= OnAnyPopupOpened;
            _bookmarkPopup.PopupClosed -= OnAnyPopupClosed;

            _dismissOverlay.MouseDown -= OnDismissOverlayClicked;

            _mapControl.Map.Layers.Remove(_startLayer);
            _mapControl.Map.Layers.Remove(_endLayer);
            _mapControl.Map.Layers.Remove(_bookmarkLayer);
        }

        //  Dismiss overlay =============================================

        private void OnDismissOverlayClicked(object sender, EventArgs e)
        {
            _popup.Hide();
            _bookmarkPopup.Hide();
        }

        private void OnAnyPopupOpened()
        {
            _dismissOverlay.Width = _overlayCanvas.ActualWidth;
            _dismissOverlay.Height = _overlayCanvas.ActualHeight;
            _dismissOverlay.Visibility = Visibility.Visible;
        }

        private void OnAnyPopupClosed()
        {
            //  Only hide the overlay if no popup is still open
            if (_popup.Visibility != Visibility.Visible
                && _bookmarkPopup.Visibility != Visibility.Visible)
            {
                _dismissOverlay.Visibility = Visibility.Collapsed;
            }
        }

        //  Map click =============================================

        private void OnMapInfo(object sender, MapInfoEventArgs e)
        {
            _popup.Hide();
            _bookmarkPopup.Hide();

            if (e.WorldPosition == null) 
                return;

            var world = e.WorldPosition;
            var screenPos = _mapControl.Map.Navigator.Viewport.WorldToScreen(world.X, world.Y);

            //  Did we click an existing bookmark?
            var hit = HitTestBookmark(screenPos.X, screenPos.Y);
            if (hit != null)
            {
                _bookmarkPopup.Show(screenPos.X, screenPos.Y, hit);
                return;
            }

            //  Fall through to nearest-junction popup
            var (lon, lat) = SphericalMercator.ToLonLat(world.X, world.Y);
            int nodeIdx = _nodeIndex.FindNearestNode(lat, lon);

            if (nodeIdx < 0) 
                return;

            _popup.Show(screenPos.X, screenPos.Y, nodeIdx, lat, lon);
        }

        /// <summary>
        /// Returns the closest bookmark within BookmarkHitRadiusPixels of the
        /// given screen coordinates, or null.
        /// </summary>
        private Bookmark? HitTestBookmark(double screenX, double screenY)
        {
            Bookmark best = null;
            double bestDistSq = BookmarkHitRadiusPixels * BookmarkHitRadiusPixels;

            foreach (var b in _bookmarks.All)
            {
                var (mx, my) = SphericalMercator.FromLonLat(b.Lon, b.Lat);
                var sp = _mapControl.Map.Navigator.Viewport.WorldToScreen(mx, my);

                double dx = sp.X - screenX;
                double dy = sp.Y - screenY;
                double d2 = dx * dx + dy * dy;

                if (d2 <= bestDistSq)
                {
                    bestDistSq = d2;
                    best = b;
                }
            }

            return best;
        }

        //  Popup actions: start/end =============================================

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

        //  Popup actions: bookmarks =============================================

        private void OnBookmarkAtClick()
        {
            //  Capture the click context before hiding (the dialog steals focus)
            double lat = _popup.ClickLat;
            double lon = _popup.ClickLon;

            _popup.Hide();

            var dlg = new BookmarkNameDialog
            {
                Owner = Window.GetWindow(_mapControl),
            };
            dlg.ConfigureForNew(lat, lon);

            if (dlg.ShowDialog() == true)
            {
                var added = _bookmarks.Add(dlg.ResultName, lat, lon);

                RefreshBookmarkLayer();
            }
        }

        private void OnSetStartFromBookmark()
        {
            var b = _bookmarkPopup.Current;

            if (b == null) 
                return;

            //  Resolve nearest junction lazily
            //  Bookmark stores lat/lon, not node index, so it survives graph rebuilds
            //  The popup is already hidden by the time this runs
            int nodeIdx = _nodeIndex.FindNearestNode(b.Lat, b.Lon);

            if (nodeIdx < 0) 
                return;

            StartNodeIndex = nodeIdx;

            PlacePin(_startLayer, StartNodeIndex, PinStart, "S");

            _routeInfoPanel.SetStart(b.Lat, b.Lon);

            CheckReady();
        }

        private void OnSetEndFromBookmark()
        {
            var b = _bookmarkPopup.Current;

            if (b == null) 
                return;

            int nodeIdx = _nodeIndex.FindNearestNode(b.Lat, b.Lon);

            if (nodeIdx < 0) 
                return;

            EndNodeIndex = nodeIdx;

            PlacePin(_endLayer, EndNodeIndex, PinEnd, "E");

            _routeInfoPanel.SetEnd(b.Lat, b.Lon);

            CheckReady();
        }

        private void OnRenameBookmark()
        {
            var b = _bookmarkPopup.Current;

            if (b == null) 
                return;

            var dlg = new BookmarkNameDialog
            {
                Owner = Window.GetWindow(_mapControl),
            };
            dlg.ConfigureForRename(b.Name);

            if (dlg.ShowDialog() == true)
            {
                _bookmarks.Rename(b, dlg.ResultName);
                //  Name lives only in the popup header, so no need to redraw the marker layer (markers dont show names)
                //  But the popup is still showing the old name on screen
                //  Refresh by closing it
                _bookmarkPopup.Hide();
            }
            else
            {
                //  User cancelled, leave the popup open for further actions
            }
        }

        private void OnDeleteBookmark()
        {
            var b = _bookmarkPopup.Current;

            if (b == null) 
                return;

            var owner = Window.GetWindow(_mapControl);
            var result = MessageBox.Show(
                owner,
                $"delete bookmark '{b.Name}'?",
                "delete bookmark",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.OK)
            {
                _bookmarks.Remove(b);
                _bookmarkPopup.Hide();
                RefreshBookmarkLayer();
            }
        }

        //  Wiring helpers =============================================

        private void CheckReady()
        {
            if (StartNodeIndex >= 0 && EndNodeIndex >= 0)
                RouteReadyToRun?.Invoke(StartNodeIndex, EndNodeIndex);
        }

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

        /// <summary>
        /// Rebuild the bookmark layer's features from the store. Called after
        /// any add/delete operation. Markers are diamond shapes in bookmark
        /// blue to distinguish from circular start/end pins.
        /// </summary>
        private void RefreshBookmarkLayer()
        {
            var features = new List<GeometryFeature>(_bookmarks.All.Count);

            foreach (var b in _bookmarks.All)
            {
                var (mx, my) = SphericalMercator.FromLonLat(b.Lon, b.Lat);
                var feat = new GeometryFeature { Geometry = new Point(mx, my) };

                //  Square marker, smaller than start/end ellipses, blue
                //  Name is intentionally not a label here, names only appear in the popup on click
                feat.Styles.Add(new SymbolStyle
                {
                    Fill = new Brush(PinBookmark),
                    Outline = new Pen(PinBorder, 2),
                    SymbolScale = 0.45,
                    SymbolType = SymbolType.Rectangle,
                });

                features.Add(feat);
            }

            _bookmarkLayer.Features = features;
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
