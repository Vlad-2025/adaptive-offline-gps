using adaptive_offline_gps.Data;
using adaptive_offline_gps.EngineMap;
using adaptive_offline_gps.RoutingEngine.GraphAlgorithms;
using adaptive_offline_gps.RoutingEngine.GraphAlgorithms.Algorithms;
using adaptive_offline_gps.UI;
using BruTile;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.UI.Wpf;
using Microsoft.Extensions.Configuration;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using OsmSharp;
using OsmSharp.Streams;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace adaptive_offline_gps
{
    public partial class MainWindow : Window
    {
        private MapEngine? _mapEngine;
        private string? _osmFilePath;
        private MapRoutingController? _routingController;
        private MapVisualizer? _mapVisualizer;

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
            TopBar.LoadFileRequested    += OnLoadFileRequested;
            TopBar.BuildGraphRequested  += OnBuildGraphRequested;
            TopBar.ClearRouteRequested  += OnClearRouteRequested;

            DownloadPanel.FileReady     += DownloadPanel_FileReady;

            LeftPanel.Log("system ready");
            LeftPanel.Log("select a .osm.pbf file to begin");

            LeftPanel.RunRequested      += OnRunRequested;
            LeftPanel.CancelRequested   += OnCancelRequested;

            UpdateMapSquare();
        }

        //  =================== TopBar event handlers
        private async void OnLoadFileRequested(string filePath)
        {
            ResetToStartup();

            //  Switch panels
            DownloadPanel.Visibility = Visibility.Collapsed;
            MapContainer.Visibility = Visibility.Visible;

            _osmFilePath = filePath;
            LeftPanel.SetFilePath(filePath);

            await LoadMapAsync(filePath);
        }
        private void DownloadPanel_FileReady(string filePath)
        {
            DownloadPanel.Visibility = Visibility.Collapsed;
            MapContainer.Visibility = Visibility.Visible;

            _osmFilePath = filePath;
            LeftPanel.SetFilePath(filePath);
            _ = LoadMapAsync(filePath);
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
            _routingController?.ClearPins();
            _mapVisualizer?.Reset();
            MapPopup.Hide();

            RouteInfoPanel.Clear();
            LeftPanel.SetRunEnabled(false);
            LeftPanel.Log("route cleared");
        }

        //  =================== LeftPanel event handlers

        private CancellationTokenSource? _routingCts;

        private async void OnRunRequested()
        {
            if (_mapEngine == null || _routingController == null || _mapVisualizer == null)
            {
                LeftPanel.Log("engine not ready", DebugConsole.LogLevel.Warning);
                return;
            }

            _routingCts = new CancellationTokenSource();

            LeftPanel.SetRunning(true);

            //  Read settings from left panel
            var options = new RoutingOptions
            {
                StepDelayMs = LeftPanel.StepDelayMs,
                CancellationToken = _routingCts.Token,
                BenchmarkMode = false,
            };

            //  Build weight provider from active conditions
            IEdgeWeightProvider weights;
            if (LeftPanel.RushHour || LeftPanel.Snow || LeftPanel.Rain)
            {
                weights = new SyntheticWeightProvider
                {
                    RushHour = LeftPanel.RushHour,
                    Snow = LeftPanel.Snow,
                    Rain = LeftPanel.Rain,
                };
            }
            else
            {
                weights = BaseWeightProvider.Instance;
            }

            LeftPanel.Log($"algorithm: '{LeftPanel.SelectedAlgorithm}'");
            // Pick algorithm from combo
            IRoutingAlgorithm algo = LeftPanel.SelectedAlgorithm switch
            {
                "Dijkstra"                  => new DijkstraAlgorithm(),
                "A*"                        => new AStarAlgorithm(),
                //"Bidirectional A*"        => new BidirectionalAStarAlgorithm(),
                //"Genetic"                 => new GeneticAlgorithm(),
                "CH"   => new CHAlgorithmWrapper(_mapEngine.ChGraph),
                _                           => new DijkstraAlgorithm(),
            };

            //  Reset visualization layers
            _mapVisualizer?.SetOptions(options);
            _mapVisualizer?.Reset();

            if (algo is CHAlgorithmWrapper)
            {
                CHDebugVerifier.Verify(
                    _mapEngine.Graph,
                    _mapEngine.ChGraph,
                    _routingController.StartNodeIndex,
                    _routingController.EndNodeIndex);
            }

            try
            {
                LeftPanel.Log($"running {algo.Name}...");

                var result = await algo.FindPathAsync(
                    _mapEngine.Graph,
                    _routingController.StartNodeIndex,
                    _routingController.EndNodeIndex,
                    weights,
                    options,
                    _mapVisualizer);

                if (result.PathFound)
                {
                    RouteInfoPanel.Update(
                        result.DistanceMetres / 1000.0,
                        result.TotalCost,
                        result.NodesVisited);

                    LeftPanel.Log(
                        $"{algo.Name}  {result.DistanceMetres / 1000.0:F1}km  " +
                        $"{result.NodesVisited:N0} nodes  {result.ElapsedMs}ms",
                        DebugConsole.LogLevel.Ok);
                }
                else
                {
                    LeftPanel.Log("no path found", DebugConsole.LogLevel.Warning);
                }
            }
            catch (OperationCanceledException)
            {
                LeftPanel.Log("cancelled", DebugConsole.LogLevel.Warning);
                _mapVisualizer?.SetOptions(options);
                _mapVisualizer?.Reset();
            }
            catch (Exception ex)
            {
                LeftPanel.Log($"routing error: {ex.Message}", DebugConsole.LogLevel.Error);
            }
            finally
            {
                LeftPanel.SetRunning(false);
            }
        }

        private void OnCancelRequested()
        {
            _routingCts?.Cancel();
        }

        //  =================== Download Panel
        private void ResetToStartup()
        {
            if(_mapEngine != null)
            {
                _mapEngine.Dispose();
                _mapEngine = null;
            }

            //  Switch panels
            MapContainer.Visibility     = Visibility.Collapsed;
            DownloadPanel.Visibility    = Visibility.Visible;

            TopBar.SetStatus("NO MAP LOADED", "#3A3A5C");
            TopBar.SetButtonsEnabled(true);
            LeftPanel.SetGraphStats(0, 0);
            LeftPanel.Log("map unloaded");
        }

        //  =================== Map Loading
        private async Task LoadMapAsync(string filePath, bool forceRebuild = false)
        {
            TopBar.SetStatus("LOADING...", "#FFB830");
            TopBar.SetButtonsEnabled(false);
            LoadingOverlay.Visibility = Visibility.Visible;

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
                    },
                    onStatusUpdate: msg => Dispatcher.Invoke(() => 
                    {
                        TxtLoadingStatus.Text = msg;
                        LeftPanel.Log(msg, DebugConsole.LogLevel.Info);
                    }));

                sw.Stop();

                _mapVisualizer = _mapEngine.Visualizer;

                BottomBar.SetLoadTime(sw.ElapsedMilliseconds);

                LeftPanel.Log($"map ready   ({sw.ElapsedMilliseconds}ms)", DebugConsole.LogLevel.Ok);

                TopBar.SetStatus("MAP LOADED", "#00FF88");

                _routingController?.Detach();

                _routingController = new MapRoutingController(
                    mapControl,
                    _mapEngine.NodeIndex!,
                    _mapEngine.Graph,
                    MapPopup,
                    MapOverlayCanvas,
                    PopupDismissOverlay,
                    RouteInfoPanel
                    );

                _routingController.RouteReadyToRun += (startIdx, endIdx) =>
                {
                    LeftPanel.Log($"route {startIdx} → {endIdx}", DebugConsole.LogLevel.Info);
                    LeftPanel.SetRunEnabled(true);
                };
            }
            catch (Exception ex)
            {
                sw.Stop();

                LeftPanel.Log($"error: {ex.Message}", DebugConsole.LogLevel.Error);

                TopBar.SetStatus("ERROR", "#FF4455");
            }
            finally
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                TopBar.SetButtonsEnabled(true);
            }
        }

        //  =================== Map Sizing Constraints

        private DispatcherTimer? _resizeDebounce;

        //  Adds a delay for the animation, waiting for the user to stop resizing 
        private void MapContainer_SizeChanged(object sender, SizeChangedEventArgs e)// => UpdateMapSquare();
        {
            _resizeDebounce?.Stop();
            _resizeDebounce = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _resizeDebounce.Tick += (_, _) =>
            {
                _resizeDebounce.Stop();
                UpdateMapSquare();
            };

            _resizeDebounce.Start();
        }
        private void UpdateMapSquare()
        {
            double availableW = MapContainer.ActualWidth    - 40;
            double availableH = MapContainer.ActualHeight   - 40;

            if (availableW <= 0 || availableH <= 0)
                return;

            var resolution = _mapEngine?.CurrentResolution ?? double.MaxValue;

            double targetW, targetH;

            if(resolution > SquaredUntilResolution)
            {
                double side = Math.Max(200, Math.Min(availableH, availableW));

                targetW = side;
                targetH = side;
            }
            else
            {
                targetW = Math.Max(200, availableW);
                targetH = Math.Max(200, availableH);
            }

            //  Animation
            var duration = TimeSpan.FromMilliseconds(300);
            var easing = new CubicEase
            {
                EasingMode = EasingMode.EaseOut
            };

            MapBorder.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(MapBorder.ActualWidth, targetW, duration){
                EasingFunction = easing
            });

            MapBorder.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(MapBorder.ActualHeight, targetH, duration)
            {
                EasingFunction = easing
            });
        }
    }
}