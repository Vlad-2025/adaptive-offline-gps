using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Collections.Generic;
using adaptive_offline_gps.RoutingEngine.RoadGraphBuilding;

namespace adaptive_offline_gps.UI
{
    public partial class LeftPanel : UserControl
    {
        public LeftPanel() => InitializeComponent();

        public event Action? BenchmarkRequested;
        public event Action? ScenariosRequested;
        public event Action? ScenarioChanged;

        private const string DefaultScenarioLabel = "(default)";

        public string? SelectedScenarioName
        {
            get
            {
                var item = CboScenario.SelectedItem as string;
                return (item == null || item == DefaultScenarioLabel) ? null : item;
            }
        }

        /// <summary>
        /// True if a real scenario is picked (anything other than "(default)").
        /// When true, the four synthetic condition checkboxes are inert.
        /// </summary>
        public bool HasRealScenario => SelectedScenarioName != null;

        public void SetScenariosButtonEnabled(bool enabled)
            => BtnScenarios.IsEnabled = enabled;

        /// <summary>
        /// Populate the dropdown from the scenarios directory. Pass an empty list
        /// to show only "(default)". Tries to preserve the current selection by name.
        /// </summary>
        public void RefreshScenarios(IEnumerable<string> scenarioNames)
        {
            string? preserve = SelectedScenarioName;

            CboScenario.SelectionChanged -= CboScenario_SelectionChanged;
            CboScenario.Items.Clear();
            CboScenario.Items.Add(DefaultScenarioLabel);
            foreach (var name in scenarioNames)
                CboScenario.Items.Add(name);

            //  Restore selection if still present, else default
            int idx = 0;
            if (preserve != null)
            {
                for (int i = 0; i < CboScenario.Items.Count; i++)
                    if ((string)CboScenario.Items[i] == preserve) { idx = i; break; }
            }
            CboScenario.SelectedIndex = idx;
            CboScenario.SelectionChanged += CboScenario_SelectionChanged;
        }

        private void CboScenario_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ScenarioChanged?.Invoke();
        }

        private void BtnScenarios_Click(object sender, RoutedEventArgs e)
            => ScenariosRequested?.Invoke();

        public void SetBenchmarkEnabled(bool enabled)
            => BtnBenchmark.IsEnabled = enabled;

        private void BtnBenchmark_Click(object sender, RoutedEventArgs e)
            => BenchmarkRequested?.Invoke();

        //  Public API called by MainWindow
        public void SetFilePath(string filePath)
        {
            TxtFilePath.Text = Path.GetFileName(filePath);
            TxtFilePath.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0xBB));
        }

        public void SetGraphStats(int nodeCount, int edgeCount)
        {
            TxtNodeCount.Text = nodeCount.ToString("N0");
            TxtEdgeCount.Text = edgeCount.ToString("N0");
        }

        public event Action? RunRequested;
        public event Action? CancelRequested;

        public void SetRunEnabled(bool enabled)
            => BtnRun.IsEnabled = enabled;

        public void SetRunning(bool running)
        {
            BtnRun.IsEnabled = !running;
            BtnCancel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        }

        //  Forwarded to the DebugConsole
        public void Log(string message, DebugConsole.LogLevel level = DebugConsole.LogLevel.Info) => Console.Log(message, level);

        //  Properties read by MainWindow -> user by RoutingEngine
        public string SelectedAlgorithm => ((ComboBoxItem)CboAlgorithm.SelectedItem)?.Content?.ToString() ?? "Dijkstra";

        public int StepDelayMs => (int)SliderDelay.Value;

        //  Internal handlers
        private void SliderDelay_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtDelayValue != null)
                TxtDelayValue.Text = $"{(int)e.NewValue}ms";
        }

        private void BtnRun_Click(object sender, RoutedEventArgs e)
            => RunRequested?.Invoke();

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
            => CancelRequested?.Invoke();
    }
}
