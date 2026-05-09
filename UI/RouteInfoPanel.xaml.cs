using BruTile;
using System.Windows.Controls;

namespace adaptive_offline_gps.UI
{
    public partial class RouteInfoPanel : UserControl
    {
        public RouteInfoPanel() => InitializeComponent();

        public void SetStart(double lat, double lon)
            => TxtStart.Text = $"{lat:F4}, {lon:F4}";

        public void SetEnd(double lat, double lon)
            => TxtEnd.Text = $"{lat:F4}, {lon:F4}";

        public void Update(double distanceKm, double timeSeconds, int nodesVisited)
        {
            TxtDistance.Text = $"{distanceKm:F1} km";
            TxtTime.Text = FormatTime(timeSeconds);
            TxtNodes.Text = nodesVisited.ToString("N0");
        }

        public void Clear()
        {
            TxtStart.Text = "-";
            TxtEnd.Text = "-";
            TxtDistance.Text = "-";
            TxtTime.Text = "-";
            TxtNodes.Text = "-";
        }

        private static string FormatTime(double seconds)
        {
            if (seconds < 60) return $"{seconds:F0}s";
            if (seconds < 3600) return $"{seconds / 60:F0}m";
            return $"{seconds / 3600:F1}h";
        }
    }
}