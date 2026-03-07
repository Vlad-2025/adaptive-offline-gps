using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace adaptive_offline_gps.UI
{
    public partial class TopBar : UserControl
    {
        public event Action<string>? LoadFileRequested; //  carries chosen file path
        public event Action? BuildGraphRequested;
        public event Action? ClearRouteRequested;

        public TopBar() => InitializeComponent();

        //  =================== Button handlers
        private void BtnLoadFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select .osm.pbf file",
                Filter = "OSM PBF files (*.osm.pbf)|*.osm.pbf|All files(*.*)|*.*"
            };

            if (dlg.ShowDialog() != true)
                return;

            LoadFileRequested?.Invoke(dlg.FileName);
        }

        private void BtnBuildGraph_Click(object sender, RoutedEventArgs e) => BuildGraphRequested?.Invoke();

        private void BtnClearRoute_Click(object sender, RoutedEventArgs e) => ClearRouteRequested?.Invoke();

        //  =================== Public API called by MainWindow
        public void SetStatus(string text, string hexColor)
        {
            TxtStatus.Text = text;
            TxtStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hexColor));

            var anim = new ColorAnimation(
                (Color)ColorConverter.ConvertFromString(hexColor),
                TimeSpan.FromMilliseconds(300));

            StatusDotColor.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }

        public void SetButtons(bool enabled)
        {
            BtnLoadFile.IsEnabled   = enabled;
            BtnBuildGraph.IsEnabled = enabled;
            BtnClearRoute.IsEnabled = enabled;
        }
    }
}
