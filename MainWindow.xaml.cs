using adaptive_offline_gps.Data;
using BruTile;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.UI.Wpf;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using OsmSharp;
using OsmSharp.Streams;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using adaptive_offline_gps.EngineMap;
using Microsoft.Extensions.Configuration;
using adaptive_offline_gps.UI;
using System.Diagnostics;

namespace adaptive_offline_gps
{
    public partial class MainWindow : Window
    {
        private MapEngine? _mapEngine;
        private string? _osmFilePath;

        private static readonly double SquaredUntilResolution = 156543.0 / Math.Pow(2, 3);  //  cannot be const because pow is runtime

        public MainWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            SizeChanged += (_, _) => UpdateMapSquare();
        }

        //  =================== Start-Up
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            TopBar.LoadFileRequested += OnLoadFileRequested;
            TopBar.BuildGraphRequested += OnBuildGraphRequested;
            TopBar.ClearRouteRequested += OnClearRouteRequested;

            LeftPanel.Log("system ready");
            LeftPanel.Log("select a .osm.pbf file to begin");

            UpdateMapSquare();
        }

        //  =================== TopBar event handlers
        private async void OnLoadFileRequested(string filePath)
        {
            _osmFilePath = filePath;
            LeftPanel.SetFilePath(filePath);
            
            await LoadMapAsync(filePath);
        }

        private async void OnBuildGraphRequested()
        {
            if(_osmFilePath == null)
            {
                LeftPanel.Log("no file loaded -> use [ LOAD FILE ] first", DebugConsole.LogLevel.Warning);

                return;
            }

            LeftPanel.Log("rebuilding graph (forced)...");

            await LoadMapAsync(_osmFilePath, forceRebuild: true);
        }

        private void OnClearRouteRequested()
        {
            RouteInfoPanel.Clear();
            LeftPanel.Log("route cleared");

            //  TODO: _mapEngine?.ClearRouteLayer();
        }

        //  =================== Map Loading
        private async Task LoadMapAsync(string filePath, bool forceRebuild = false)
        {
            TopBar.SetStatus("LOADING...", "#FFB830");
            TopBar.SetButtonsEnabled(false);

            //  measure time needed to load map
            var sw = Stopwatch.StartNew();
            try
            {
                LeftPanel.Log($"loading {Path.GetFileName(filePath)}");

                _mapEngine = await MapEngine.CreateAsync(
                    mapControl,
                    filePath,
                    forceRebuild,
                    onGraphReady: (nodeCount, edgeCount, fromCache) =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            LeftPanel.SetGraphStats(nodeCount, edgeCount);

                            var src = fromCache ? "cache" : "parsed";

                            LeftPanel.Log($"graph ready: [{src}]", DebugConsole.LogLevel.Ok);
                        });
                    },
                    onViewportChanged: (resolution, zoom, lon, lat, featureCount) =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            BottomBar.Update(resolution, zoom, lon, lat, featureCount);
                            UpdateMapSquare();
                        });
                    });

                sw.Stop();

                BottomBar.SetLoadTime(sw.ElapsedMilliseconds);

                LeftPanel.Log($"map ready   ({sw.ElapsedMilliseconds}ms)", DebugConsole.LogLevel.Ok);

                TopBar.SetStatus("MAP LOADED", "#00FF88");
            }
            catch (Exception ex)
            {
                sw.Stop();

                LeftPanel.Log($"error: {ex.Message}", DebugConsole.LogLevel.Error);

                TopBar.SetStatus("ERROR", "#FF4455");
            }
            finally
            {
                TopBar.SetButtonsEnabled(true);
            }
        }

        //  =================== Map Sizing Constraints
        private void MapContainer_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateMapSquare();

        private void UpdateMapSquare()
        {
            double availableW = MapContainer.ActualWidth    - 40;
            double availableH = MapContainer.ActualHeight   - 40;

            if (availableW <= 0 || availableH <= 0)
                return;

            var resolution = _mapEngine?.CurrentResolution ?? double.MaxValue;

            if(resolution > SquaredUntilResolution)
            {
                double side = Math.Max(200, Math.Min(availableH, availableW));

                MapBorder.Width = side;
                MapBorder.Height = side;
            }
            else
            {
                MapBorder.Width = Math.Max(200, availableW);
                MapBorder.Height = Math.Max(200, availableH);
            }
        }
    }
}