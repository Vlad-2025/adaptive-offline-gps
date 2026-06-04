using adaptive_offline_gps.EngineMap;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;

namespace adaptive_offline_gps.UI
{
    public partial class BottomBar : UserControl
    {
        public BottomBar() => InitializeComponent();

        //  Called from the viewport callback in MainWindow
        public void Update(double resolution, double zoom, double lon, double lat, int featureCount)
        {
            TxtResolution.Text  = $"{resolution:F1}";
            TxtZoom.Text        = $"{zoom:F1}";
            TxtCenter.Text      = $"{lat:F4}, {lon:F4}";
            TxtFeatures.Text    = $"{featureCount}";
            TxtMemoryUsage.Text = $"{Process.GetCurrentProcess().PrivateMemorySize64 / 1024 / 1024}mb";
        }

        public void SetLoadTime(long ms) => TxtLoadTime.Text = $"{ms}ms";

        public void SetTileSource(CompositeAvailabilityTileSource.SourceState state)
        {
            if (state == CompositeAvailabilityTileSource.SourceState.Primary)
            {
                TxtTileSource.Text = "ONLINE";
                TxtTileSource.Foreground = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#00FF88"));
            }
            else
            {
                TxtTileSource.Text = "OFFLINE";
                TxtTileSource.Foreground = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#FFB830"));
            }
        }
    }
}
