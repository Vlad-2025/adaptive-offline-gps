using System;
using System.Windows;
using System.Windows.Controls;

namespace adaptive_offline_gps.UI
{
    public partial class MapClickPopup : UserControl
    {
        public event Action? SetStartRequested;
        public event Action? SetEndRequested;
        public event Action? PopupOpened;
        public event Action? PopupClosed;

        public int NearestNodeIndex { get; private set; } = -1;

        // Store coords so controller can read them after selection
        public double ClickLat { get; private set; }
        public double ClickLon { get; private set; }

        public MapClickPopup() => InitializeComponent();

        public void Show(double canvasX, double canvasY, int nodeIndex, double lat, double lon)
        {
            NearestNodeIndex = nodeIndex;
            ClickLat = lat;
            ClickLon = lon;
            TxtCoords.Text = $"{lat:F4}, {lon:F4}";
            Visibility = Visibility.Visible;

            Canvas.SetLeft(this, canvasX + 12);
            Canvas.SetTop(this, canvasY - 10);

            PopupOpened?.Invoke();
        }

        public void Hide()
        {
            Visibility = Visibility.Collapsed;
            PopupClosed?.Invoke();
        }

        private void BtnSetStart_Click(object sender, RoutedEventArgs e)
        {
            SetStartRequested?.Invoke();
            Hide();
        }

        private void BtnSetEnd_Click(object sender, RoutedEventArgs e)
        {
            SetEndRequested?.Invoke();
            Hide();
        }

        private void BtnDismiss_Click(object sender, RoutedEventArgs e) => Hide();
    }
}