using adaptive_offline_gps.RoutingEngine.Benchmarking;
using adaptive_offline_gps.RoutingEngine.GraphAlgorithms;
using adaptive_offline_gps.RoutingEngine.RoadGraphBuilding;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace adaptive_offline_gps.UI
{
    /// <summary>
    /// Three-state benchmark UI: setup -> running -> results.
    ///
    /// Lifecycle:
    ///     - MainWindow creates a single BenchmarkPanel and calls Initialize(...) once
    ///         after the graph is loaded.
    ///         
    ///     - When the user clicks "RUN BENCHMARK" in LeftPanel, MainWindow calls
    ///         PrepareForRun(...) which shows the setup view with current pin context.
    ///         
    ///     - The panel handles its own state transitions internally from there.
    ///     
    ///     - When the user clicks "&lt; BACK", BackRequested fires; MainWindow switches
    ///         the visibility of MapContainer / BenchmarkPanel.
    /// </summary>
    public partial class BenchmarkPanelView : UserControl
    {
        //  Events for MainWindow to subscribe to
        public event Action? BackRequested;

        private RoadGraph? _graph;
        private CHGraph? _chGraph;
        private string? _graphFile;
        private string? _baseRgphPath;

        //  Single-pair context (only valid when both >= 0)
        private int _pinStart = -1;
        private int _pinEnd = -1;

        private CancellationTokenSource? _cts;
        private BenchmarkReport? _lastReport;

        public BenchmarkPanelView()
        {
            InitializeComponent();
        }

        //  Public API ===============================

        public void Initialize(RoadGraph graph, CHGraph chGraph, string graphFile, string baseRgphPath)
        {
            _graph = graph;
            _chGraph = chGraph;
            _graphFile = graphFile;
            _baseRgphPath = baseRgphPath;
            RefreshScenarios();
        }

        /// <summary>
        /// Repopulate the scenario dropdown. Called by MainWindow when scenarios
        /// change (after preprocessing or when the base graph reloads).
        /// </summary>
        public void RefreshScenarios()
        {
            if (_baseRgphPath == null) return;

            string? preserve = (CboScenario.SelectedItem as ComboBoxItem)?.Content?.ToString();

            var scenariosDir = ScenarioPipeline.ScenariosDirFor(_baseRgphPath);
            var available = ScenarioPipeline.ListAvailableScenarios(scenariosDir);

            CboScenario.Items.Clear();
            //  "free_flow" is the baseline (no scenario). Keep the existing label for
            //  CSV consistency with prior benchmark exports.
            var freeFlow = new ComboBoxItem { Content = "free_flow", IsSelected = true };
            CboScenario.Items.Add(freeFlow);

            foreach (var name in available)
                CboScenario.Items.Add(new ComboBoxItem { Content = name });

            //  Try to preserve previous selection
            if (preserve != null)
            {
                foreach (var raw in CboScenario.Items)
                {
                    if (raw is ComboBoxItem ci && (ci.Content as string) == preserve)
                    {
                        ci.IsSelected = true;
                        break;
                    }
                }
            }
        }

        //  Called by MainWindow each time the user navigates to the benchmark view,
        //  so the panel can reflect the current pin selection (single-pair option is enabled if start/end are pinned)
        public void PrepareForRun(int startNode, int endNode)
        {
            _pinStart = startNode;
            _pinEnd = endNode;

            bool hasPins = startNode >= 0 && endNode >= 0;

            //  Single-pair radio is enabled iff pins exist; hint text explains why
            RbSinglePair.IsEnabled = hasPins;
            TxtSinglePairHint.Text = hasPins
                ? $"will route {startNode} -> {endNode}"
                : "requires a start and end pin on the map";

            //  Reset to setup view in case we were showing results from a prior run
            ShowSetup();
            UpdateEstimate();
        }

        //  State transitions ==========================

        private void ShowSetup()
        {
            SetupView.Visibility   = Visibility.Visible;
            RunningView.Visibility = Visibility.Collapsed;
            ResultsView.Visibility = Visibility.Collapsed;
            TxtHeaderStatus.Text   = "";
        }

        private void ShowRunning()
        {
            SetupView.Visibility   = Visibility.Collapsed;
            RunningView.Visibility = Visibility.Visible;
            ResultsView.Visibility = Visibility.Collapsed;
            ProgressBar.Value = 0;
            TxtProgressPercent.Text = "0%";
            TxtProgressDetail.Text = "warming up...";
            TxtHeaderStatus.Text   = "running";
        }

        private void ShowResults()
        {
            SetupView.Visibility   = Visibility.Collapsed;
            RunningView.Visibility = Visibility.Collapsed;
            ResultsView.Visibility = Visibility.Visible;
            TxtHeaderStatus.Text   = "done";
        }

        //  Setup-view handlers =====================

        private void ModeChanged(object sender, RoutedEventArgs e)
        {
            //  Bucket section is only relevant in multi-query mode
            //  Guard for IsInitialized so this can be called from xaml init too
            if (!IsInitialized) 
                return;

            if (BucketsSection == null) 
                return;

            bool multi = RbMultiQuery.IsChecked == true;
            BucketsSection.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
            UpdateEstimate();
        }

        private void UpdateEstimate()
        {
            if (TxtEstimate == null) return;

            int algoCount = (ChkAlgoDijkstra.IsChecked == true ? 1 : 0)
                          + (ChkAlgoAStar.IsChecked    == true ? 1 : 0)
                          + (ChkAlgoCH.IsChecked       == true ? 1 : 0);

            int totalQueries;

            if (RbSinglePair.IsChecked == true)
            {
                totalQueries = 1;
            }
            else
            {
                int buckets = (ChkBucketShort.IsChecked  == true ? 1 : 0)
                            + (ChkBucketMedium.IsChecked == true ? 1 : 0)
                            + (ChkBucketLong.IsChecked   == true ? 1 : 0);

                int pairs = TryParse(TxtPairsPerBucket?.Text, 5);

                totalQueries = buckets * pairs;
            }

            int warmup = TryParse(TxtWarmup?.Text, 1);
            int measured = TryParse(TxtMeasured?.Text, 3);
            int totalRuns = algoCount * totalQueries * (warmup + measured);

            TxtEstimate.Text =
                $"estimated total runs: {totalRuns}   " +
                $"({algoCount} algos × {totalQueries} queries × {warmup + measured} runs each)";
        }

        private async void BtnRunBenchmark_Click(object sender, RoutedEventArgs e)
        {
            if (_graph == null || _chGraph == null || _graphFile == null)
            {
                MessageBox.Show("benchmark not initialized (graph missing)", "error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            //  Build config from form
            var cfg = BuildConfig();
            if (cfg == null) 
                return;  // validation failed, BuildConfig showed message

            //  Confirm dialog
            var confirm = new BenchmarkConfirmDialog
            {
                Owner = Window.GetWindow(this),
                EstimateText = TxtEstimate.Text,
            };
            if (confirm.ShowDialog() != true) return;

            //  Run
            ShowRunning();
            _cts = new CancellationTokenSource();

            try
            {
                var runner = new BenchmarkRunner(_graph, _chGraph, _graphFile);
                BenchmarkProgressCallback onProgress = p =>
                {
                    //  Marshal to UI thread
                    Dispatcher.Invoke(() => UpdateProgress(p));
                };

                //  Resolve weights + CH override based on the selected scenario.
                //  "free_flow" -> base CH, base weights.
                //  Real scenario -> scenario CH (if available), ScenarioWeightProvider for non-CH.
                IEdgeWeightProvider weights = RoutingEngine.GraphAlgorithms.BaseWeightProvider.Instance;
                CHGraph? chOverride = null;

                if (cfg.ScenarioName != "free_flow")
                {
                    var scenariosDir = ScenarioPipeline.ScenariosDirFor(_baseRgphPath!);
                    var CSVPath = ScenarioPipeline.CsvPathFor(scenariosDir, cfg.ScenarioName);

                    if (!File.Exists(CSVPath))
                    {
                        MessageBox.Show($"scenario CSV missing: {CSVPath}", "error",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                        ShowSetup();
                        return;
                    }

                    weights = ScenarioWeightProvider.FromCsv(_graph!, CSVPath);

                    //  CH override: load scenario .chgr if CH is one of the algorithms.
                    //  If CH is requested but its .chgr is missing, refuse to run rather
                    //  than silently falling back to base CH (which would be wrong weights).
                    if (cfg.AlgorithmNames.Contains("CH"))
                    {
                        chOverride = ScenarioPipeline.LoadScenarioCh(_graph!, _baseRgphPath!, cfg.ScenarioName);
                        if (chOverride == null)
                        {
                            MessageBox.Show(
                                $"scenario '{cfg.ScenarioName}' has no preprocessed CH (.chgr). " +
                                $"open SCENARIOS in the main panel and preprocess it first, " +
                                $"or unselect CH from the algorithms list.",
                                "missing CH",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                            ShowSetup();
                            return;
                        }
                    }
                }

                var report = await Task.Run(() => runner.RunAsync(
                    cfg,
                    weights,
                    onProgress,
                    _cts.Token,
                    chOverride));

                _lastReport = report;

                //  Write CSV next to the .pbf file
                var outDir = Path.GetDirectoryName(_graphFile) ?? ".";
                var csvPath = Path.Combine(outDir, BenchmarkCsvWriter.DefaultFileName(report));
                BenchmarkCsvWriter.Write(report, csvPath);

                PopulateResults(report, csvPath);
                ShowResults();
            }
            catch (OperationCanceledException)
            {
                ShowSetup();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"benchmark failed: {ex.Message}", "error", MessageBoxButton.OK, MessageBoxImage.Error);
                Debug.WriteLine(ex);
                ShowSetup();
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
            }
        }

        private BenchmarkConfig? BuildConfig()
        {
            var algos = new List<string>();
            if (ChkAlgoDijkstra.IsChecked == true) algos.Add("Dijkstra");
            if (ChkAlgoAStar.IsChecked    == true) algos.Add("A*");
            if (ChkAlgoCH.IsChecked       == true) algos.Add("CH");

            if (algos.Count == 0)
            {
                MessageBox.Show("select at least one algorithm", "config",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }

            var scenario = (CboScenario.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "free_flow";

            int warmup = TryParse(TxtWarmup.Text, 1, min: 0);
            int measured = TryParse(TxtMeasured.Text, 3, min: 1);

            if (RbSinglePair.IsChecked == true)
            {
                if (_pinStart < 0 || _pinEnd < 0)
                {
                    MessageBox.Show("single-pair mode requires pinned start and end", "config",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return null;
                }

                return new BenchmarkConfig
                {
                    Mode = BenchmarkMode.SinglePair,
                    SingleStart = _pinStart,
                    SingleEnd   = _pinEnd,
                    AlgorithmNames = algos,
                    ScenarioName = scenario,
                    WarmupRuns = warmup,
                    MeasuredRuns = measured,
                };
            }

            //  Multi-query
            var buckets = new List<DistanceBucket>();

            if (ChkBucketShort.IsChecked  == true) 
                buckets.Add(DistanceBucket.Short);

            if (ChkBucketMedium.IsChecked == true) 
                buckets.Add(DistanceBucket.Medium);

            if (ChkBucketLong.IsChecked   == true) 
                buckets.Add(DistanceBucket.Long);

            if (buckets.Count == 0)
            {
                MessageBox.Show("select at least one distance bucket", "config", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }

            int pairs = TryParse(TxtPairsPerBucket.Text, 5, min: 1);
            int seed  = TryParse(TxtSeed.Text, 42);

            return new BenchmarkConfig
            {
                Mode = BenchmarkMode.MultiQuery,
                Buckets = buckets,
                PairsPerBucket = pairs,
                QuerySeed = seed,
                AlgorithmNames = algos,
                ScenarioName = scenario,
                WarmupRuns = warmup,
                MeasuredRuns = measured,
            };
        }

        //  Running-view updates =====================

        private void UpdateProgress(BenchmarkProgress p)
        {
            double pct = p.TotalRuns > 0 ? 100.0 * p.CompletedRuns / p.TotalRuns : 0;

            ProgressBar.Value = pct;

            TxtProgressPercent.Text = $"{pct:F0}%";
            TxtProgressDetail.Text =
                $"query {p.CurrentQueryId} | {p.CurrentAlgorithm} | {p.Stage} | " +
                $"{p.CompletedRuns}/{p.TotalRuns} runs";
        }

        private void BtnCancelBenchmark_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
        }

        //  Results-view population ==================

        private void PopulateResults(BenchmarkReport report, string csvPath)
        {
            //  Banner
            string failedNote = "";
            if (report.FailedBuckets.Count > 0)
            {
                failedNote = "  ! " + string.Join("; ",
                    report.FailedBuckets.Select(f => $"bucket {f.bucket.Label} only {f.generated}/{f.requested}"));
            }

            TxtRunInfo.Text =
                $"scenario: {report.Config.ScenarioName}   |   " +
                $"queries: {report.TotalQueries}   |   " +
                $"algorithms: {string.Join(", ", report.Config.AlgorithmNames)}   |   " +
                $"warmup+measured: {report.Config.WarmupRuns}+{report.Config.MeasuredRuns}" +
                failedNote;

            TxtCsvPath.Text = $"-> {Path.GetFileName(csvPath)}";

            //  Summary grid: bind the aggregates directly
            SummaryGrid.ItemsSource = report.Aggregates;

            //  Per-query grid: flatten results into the row shape the DataGrid expects
            var rows = report.Results.Select(r => new PerQueryRow
            {
                QueryId         = r.Query.Id,
                Bucket          = r.Query.BucketLabel,
                AlgorithmName   = r.AlgorithmName,
                PathDistanceKm  = r.PathDistanceKm,
                TotalCost       = r.TotalCost,
                MedianElapsedMs = r.MedianElapsedMs,
                NodesVisited    = r.NodesVisited,
                CostAgrees      = r.CostAgreesWithOthers ? "yes" : "NO",
            }).ToList();
            PerQueryGrid.ItemsSource = rows;

            //  Agreement note
            if (report.AllCostsAgree)
            {
                TxtAgreement.Text = "all algorithms agreed on path cost for all queries";
                TxtAgreement.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xCC, 0x66));
            }
            else
            {
                int disagreements = report.Results.Count(r => !r.CostAgreesWithOthers);
                TxtAgreement.Text = $"! {disagreements} cost disagreement(s) - check the per-query table";
                TxtAgreement.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x44, 0x55));
            }

            //  Algorithms reference panel (static - just lists complexities of whatever ran)
            PopulateAlgoReference(report.Config.AlgorithmNames);
        }

        private void PopulateAlgoReference(IReadOnlyList<string> algos)
        {
            AlgoRefPanel.Children.Clear();
            foreach (var name in algos)
            {
                (string complexity, string note) = name switch
                {
                    "Dijkstra" => (
                        "O((V + E) log V) time, O(V) space",
                        "explores nodes in order of distance from source; no goal awareness. " +
                        "long queries settle a circle whose radius equals the route distance - " +
                        "scales poorly with country-scale graphs."),
                    "A*" => (
                        "O((V + E) log V) worst-case; typically 2-10× faster than Dijkstra on road networks",
                        "uses straight-line distance to the goal as a heuristic. " +
                        "explores a directional cone toward the destination rather than a full circle."),
                    "CH" => (
                        "preprocessing O(V log V · h); query is bidirectional and very fast",
                        "two-phase. offline preprocessing builds shortcut edges that skip over " +
                        "low-importance nodes; queries are bidirectional searches that only " +
                        "follow edges to higher-level nodes, terminating quickly."),
                    _ => ("", ""),
                };

                var header = new TextBlock
                {
                    Text = $"{name}",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x88, 0xFF)),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                    Margin = new Thickness(0, 4, 0, 2),
                };
                var compl = new TextBlock
                {
                    Text = "  " + complexity,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xFF, 0x88)),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 10,
                    Margin = new Thickness(0, 0, 0, 2),
                };
                var noteBlock = new TextBlock
                {
                    Text = "  " + note,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0xBB)),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 6),
                };
                AlgoRefPanel.Children.Add(header);
                AlgoRefPanel.Children.Add(compl);
                AlgoRefPanel.Children.Add(noteBlock);
            }
        }

        private void BtnRunAgain_Click(object sender, RoutedEventArgs e) => ShowSetup();

        //  Back =====

        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            //  If a run is in progress, cancel it first
            _cts?.Cancel();
            BackRequested?.Invoke();
        }

        //  Helpers ==============

        private static int TryParse(string? s, int fallback, int min = int.MinValue)
        {
            if (int.TryParse(s, out int v) && v >= min) return v;
            return fallback;
        }

        //  Row shape for the per-query DataGrid. Public properties for binding
        private class PerQueryRow
        {
            public int QueryId { get; set; }
            public string Bucket { get; set; } = "";
            public string AlgorithmName { get; set; } = "";
            public double PathDistanceKm { get; set; }
            public double TotalCost { get; set; }
            public double MedianElapsedMs { get; set; }
            public int NodesVisited { get; set; }
            public string CostAgrees { get; set; } = "yes";
        }
    }
}
