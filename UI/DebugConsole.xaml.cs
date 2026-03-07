using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace adaptive_offline_gps.UI
{
    public partial class DebugConsole : UserControl
    {
        public enum LogLevel { Info, Ok, Warning, Error }

        public DebugConsole() => InitializeComponent();

        private void BtnClear_Click(object sender, RoutedEventArgs e) => Clear();

        public void Clear() => LogPanel.Children.Clear();

        //  Can be called from background tasks
        public void Log(string message, LogLevel level = LogLevel.Info)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => Log(message, level));
                return;
            }

            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");

            var prefix = level switch
            {
                LogLevel.Ok => "✓",
                LogLevel.Warning => "!",
                LogLevel.Error => "X",
                _ => ".",
            };

            var msgColor = level switch
            {
                LogLevel.Ok => "#00CC66",
                LogLevel.Warning => "#FFB830",
                LogLevel.Error => "#FF4455",
                _ => "#4466AA",
            };

            var line = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Padding = new Thickness(0, 1, 0, 1)
            };

            //  Dim timestamp, bright message
            line.Inlines.Add(new Run($"{timestamp} {prefix} ")
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x5C))
            });

            LogPanel.Children.Add(line);

            //  Cap at 200 lines
            while (LogPanel.Children.Count > 200)
                LogPanel.Children.RemoveAt(0);

            LogPanel.UpdateLayout();
            Scroller.ScrollToBottom();
        }
    }
}
