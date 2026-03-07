using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace adaptive_offline_gps.UI
{
    public partial class RouteInfoPanel : UserControl
    {
        public RouteInfoPanel() => InitializeComponent();

        public void Update(double distanceKm, double timeSeconds, int nodesVisited)
        {
            TxtDistance.Text = $"{distanceKm:F1} km";
            TxtTime.Text = FormatTime(timeSeconds);
        }

        public void Clear()
        {
            TxtDistance.Text    = "-";
            TxtTime.Text        = "-";
            TxtNodes.Text       = "-";
        }

        private static string FormatTime(double timeSeconds)
        {
            if (timeSeconds < 60)
                return $"{timeSeconds:F0}s";

            if (timeSeconds < 3600)
                return $"{timeSeconds / 60:F0}m";

            return $"{timeSeconds / 3600:F1}h";
        }
    }
}
