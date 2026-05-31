using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using adaptive_offline_gps.RoutingEngine.RoadGraphBuilding;

namespace adaptive_offline_gps.UI
{
    /// <summary>
    /// Three-state panel for managing scenario preprocessing.
    ///
    /// Lifecycle:
    ///   - MainWindow creates one and calls Initialize(...) once after the base
    ///     graph is loaded.
    ///   - LeftPanel.ScenariosRequested -> MainWindow shows this panel.
    ///   - User checks scenarios, clicks PREPROCESS SELECTED.
    ///   - Panel transitions to running, runs the pipeline on a background task,
    ///     reports progress via IProgress&lt;ScenarioProgress&gt;.
    ///   - On done or cancel, transitions to DONE view; user clicks BACK to return.
    ///   - ScenariosChanged fires whenever at least one .chgr was produced or
    ///     invalidated; MainWindow refreshes the LeftPanel + Benchmark dropdowns.
    /// </summary>
    public partial class ScenarioPanelView : UserControl
    {
        public event Action? BackRequested;

        /// <summary>Fires when one or more .chgr files were written (or attempted).</summary>
        public event Action? ScenariosChanged;

        private RoadGraph? _baseGraph;
        private string? _baseRgphPath;
        private string? _scenariosDir;

        private readonly ObservableCollection<ScenarioRowVM> _rows = new();
        private CancellationTokenSource? _cts;
        private DateTime _runStartUtc;

        public ScenarioPanelView()
        {
            InitializeComponent();
            ScenariosList.ItemsSource = _rows;

            //  Recompute the estimate / button-enabled state whenever any row's
            //  IsSelected changes.
            _rows.CollectionChanged += (_, __) => HookRows();
        }

        //  Public API ===========================================================

        public void Initialize(RoadGraph baseGraph, string baseRgphPath)
        {
            _baseGraph = baseGraph;
            _baseRgphPath = baseRgphPath;
            _scenariosDir = ScenarioPipeline.ScenariosDirFor(baseRgphPath);

            TxtScenariosDir.Text = $"scenarios directory: {_scenariosDir}";

            ShowSelection();
            RefreshRows();
        }

        /// <summary>
        /// Re-scan the scenarios directory and refresh the row statuses.
        /// Called by MainWindow when this panel is shown.
        /// </summary>
        public void Refresh()
        {
            if (_baseRgphPath == null) return;
            RefreshRows();
            ShowSelection();
        }

        //  Row population ======================================================

        private void RefreshRows()
        {
            _rows.Clear();
            if (_baseRgphPath == null || _scenariosDir == null) return;

            var available = ScenarioPipeline.ListAvailableScenarios(_scenariosDir);
            if (available.Count == 0)
            {
                TxtEmptyHint.Visibility = Visibility.Visible;
                TxtEstimate.Text = "no scenarios available";
                BtnPreprocess.IsEnabled = false;
                return;
            }
            TxtEmptyHint.Visibility = Visibility.Collapsed;

            var statuses = ScenarioPipeline.GetScenarioStatuses(
                _baseRgphPath, _scenariosDir, available);

            foreach (var name in available)
            {
                var status = statuses.TryGetValue(name, out var s) ? s : ScenarioStatus.NotPreprocessed;
                var row = new ScenarioRowVM(name, status)
                {
                    //  Pre-tick anything that isn't already ready
                    IsSelected = status != ScenarioStatus.Ready,
                };
                row.PropertyChanged += Row_PropertyChanged;
                _rows.Add(row);
            }

            UpdateEstimate();
        }

        private void HookRows()
        {
            foreach (var r in _rows)
            {
                r.PropertyChanged -= Row_PropertyChanged;
                r.PropertyChanged += Row_PropertyChanged;
            }
        }

        private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ScenarioRowVM.IsSelected))
                UpdateEstimate();
        }

        private void UpdateEstimate()
        {
            int selected = _rows.Count(r => r.IsSelected);
            BtnPreprocess.IsEnabled = selected > 0;

            if (selected == 0)
            {
                TxtEstimate.Text = "select scenarios to preprocess";
            }
            else
            {
                //  Rough estimate: ~20 min per scenario
                int minutes = selected * 20;
                TxtEstimate.Text =
                    $"{selected} scenario(s) selected   |   estimated time: ~{minutes} min";
            }
        }

        //  State transitions ===================================================

        private void ShowSelection()
        {
            SelectionView.Visibility = Visibility.Visible;
            RunningView.Visibility = Visibility.Collapsed;
            DoneView.Visibility = Visibility.Collapsed;
            TxtHeaderStatus.Text = "";
        }

        private void ShowRunning()
        {
            SelectionView.Visibility = Visibility.Collapsed;
            RunningView.Visibility = Visibility.Visible;
            DoneView.Visibility = Visibility.Collapsed;
            TxtHeaderStatus.Text = "running";
            ProgressBar.Value = 0;
            TxtProgressPercent.Text = "0%";
            TxtStage.Text = "starting...";
            TxtElapsed.Text = "";
        }

        private void ShowDone()
        {
            SelectionView.Visibility = Visibility.Collapsed;
            RunningView.Visibility = Visibility.Collapsed;
            DoneView.Visibility = Visibility.Visible;
            TxtHeaderStatus.Text = "done";
        }

        //  Run handler =========================================================

        private async void BtnPreprocess_Click(object sender, RoutedEventArgs e)
        {
            if (_baseGraph == null || _baseRgphPath == null || _scenariosDir == null)
            {
                MessageBox.Show(Window.GetWindow(this), "scenario panel not initialized",
                    "error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var selected = _rows.Where(r => r.IsSelected).Select(r => r.Name).ToList();
            if (selected.Count == 0) return;

            _cts = new CancellationTokenSource();
            _runStartUtc = DateTime.UtcNow;
            ShowRunning();

            //  IProgress<T> captures the UI SynchronizationContext here, so
            //  background reports are dispatched onto the UI thread automatically.
            var progress = new Progress<ScenarioProgress>(OnProgress);

            List<ScenarioPreprocessResult>? results = null;
            Exception? fatal = null;

            try
            {
                results = await Task.Run(() => ScenarioPipeline.PreprocessScenarios(
                    _baseGraph, _baseRgphPath, _scenariosDir,
                    selected,
                    progress: progress,
                    cancel: _cts.Token));
            }
            catch (Exception ex)
            {
                fatal = ex;
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
            }

            //  Always emit a change notification - even on cancel/error, some
            //  scenarios may have finished before cancellation took effect.
            ScenariosChanged?.Invoke();

            PopulateDone(results, selected.Count, fatal);
            ShowDone();
        }

        private void OnProgress(ScenarioProgress p)
        {
            TxtScenarioCounter.Text = $"{p.ScenarioName} [{p.CurrentIndex}/{p.TotalCount}]";
            TxtStage.Text = p.Stage;

            //  Outer fraction: completed scenarios + this scenario's CH fraction.
            //  The CH stage dominates the time; treat apply/save as ~0% of inner.
            double done = (p.CurrentIndex - 1) + p.ChFraction;
            double pct = p.TotalCount > 0 ? 100.0 * done / p.TotalCount : 0;
            ProgressBar.Value = pct;
            TxtProgressPercent.Text = $"{pct:F0}%";

            var elapsed = DateTime.UtcNow - _runStartUtc;
            TxtElapsed.Text = $"elapsed {elapsed:hh\\:mm\\:ss}";
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            TxtStage.Text = "cancelling...";
        }

        //  Done view ===========================================================

        private void PopulateDone(List<ScenarioPreprocessResult>? results, int requestedCount, Exception? fatal)
        {
            DoneList.Children.Clear();

            int ok = 0, cancelled = 0, errored = 0;
            if (results != null)
            {
                foreach (var r in results)
                {
                    bool isOk = !r.Cancelled && r.Error == null;
                    if (isOk) ok++;
                    else if (r.Cancelled) cancelled++;
                    else errored++;

                    string status;
                    string color;
                    if (isOk)
                    {
                        status = $"OK   apply {r.ApplyTime.TotalSeconds:F1}s   ch {r.PreprocessTime.TotalMinutes:F1}min   {r.MatchedRows:N0}/{r.CsvRows:N0} matched";
                        color = "#00CC66";
                    }
                    else if (r.Cancelled)
                    {
                        status = "cancelled";
                        color = "#FFB830";
                    }
                    else
                    {
                        status = $"ERROR: {r.Error?.Message ?? "?"}";
                        color = "#FF4455";
                    }

                    var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    var nameTb = new TextBlock
                    {
                        Text = r.ScenarioName,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0xBB)),
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 11,
                    };
                    Grid.SetColumn(nameTb, 0);
                    row.Children.Add(nameTb);

                    var statusTb = new TextBlock
                    {
                        Text = status,
                        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 10,
                        TextWrapping = TextWrapping.Wrap,
                    };
                    Grid.SetColumn(statusTb, 1);
                    row.Children.Add(statusTb);

                    DoneList.Children.Add(row);
                }
            }

            if (fatal != null)
            {
                TxtDoneHeader.Text = "preprocessing failed";
                TxtDoneHeader.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x44, 0x55));
                TxtDoneSummary.Text = fatal.Message;
            }
            else if (cancelled > 0 && ok == 0)
            {
                TxtDoneHeader.Text = "cancelled";
                TxtDoneHeader.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB8, 0x30));
                TxtDoneSummary.Text = $"no scenarios completed. {requestedCount} were requested.";
            }
            else
            {
                TxtDoneHeader.Text = "preprocessing complete";
                TxtDoneHeader.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xFF, 0x88));
                TxtDoneSummary.Text =
                    $"{ok} of {requestedCount} scenarios ready"
                    + (cancelled > 0 ? $"   |   {cancelled} cancelled" : "")
                    + (errored > 0 ? $"   |   {errored} errored" : "");
            }
        }

        //  Back handlers =======================================================

        private void BtnDoneBack_Click(object sender, RoutedEventArgs e)
        {
            //  Return to selection so the user can see refreshed statuses.
            Refresh();
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            //  If running, cancel before leaving the panel - otherwise it would
            //  continue in the background while the user moves on.
            _cts?.Cancel();
            BackRequested?.Invoke();
        }
    }

    /// <summary>
    /// View-model for one scenario row in the selection list.
    /// </summary>
    public class ScenarioRowVM : INotifyPropertyChanged
    {
        public string Name { get; }
        public ScenarioStatus Status { get; }

        public string StatusText => Status switch
        {
            ScenarioStatus.Ready => "ready",
            ScenarioStatus.Stale => "stale",
            ScenarioStatus.NotPreprocessed => "not preprocessed",
            _ => "?",
        };

        public Brush StatusBrush => new SolidColorBrush(Status switch
        {
            ScenarioStatus.Ready => Color.FromRgb(0x00, 0xCC, 0x66),
            ScenarioStatus.Stale => Color.FromRgb(0xFF, 0xB8, 0x30),
            ScenarioStatus.NotPreprocessed => Color.FromRgb(0x3A, 0x3A, 0x5C),
            _ => Color.FromRgb(0x88, 0x88, 0xBB),
        });

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public ScenarioRowVM(string name, ScenarioStatus status)
        {
            Name = name;
            Status = status;
        }
    }
}
