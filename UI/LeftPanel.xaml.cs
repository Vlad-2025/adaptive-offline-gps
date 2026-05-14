using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace adaptive_offline_gps.UI
{
    public partial class LeftPanel : UserControl
    {
        public LeftPanel() => InitializeComponent();

        public event Action? BenchmarkRequested;

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

        public bool RushHour    => ChkRushHour.IsChecked == true;
        public bool RoadWorks   => ChkRoadWorks.IsChecked == true;
        public bool Snow        => ChkSnow.IsChecked == true;
        public bool Rain        => ChkRain.IsChecked == true;

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
