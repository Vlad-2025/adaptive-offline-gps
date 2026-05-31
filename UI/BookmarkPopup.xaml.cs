using System;
using System.Windows;
using System.Windows.Controls;
using adaptive_offline_gps.RoutingEngine.Bookmarks;

namespace adaptive_offline_gps.UI
{
    /// <summary>
    /// Click-on-existing-bookmark popup. Mirrors MapClickPopup's positioning
    /// idiom (Canvas-positioned, shown/hidden imperatively) but exposes
    /// bookmark-specific actions: set as start/end, rename, delete.
    /// </summary>
    public partial class BookmarkPopup : UserControl
    {
        public event Action? SetStartRequested;
        public event Action? SetEndRequested;
        public event Action? RenameRequested;
        public event Action? DeleteRequested;
        public event Action? PopupOpened;
        public event Action? PopupClosed;

        /// <summary>
        /// The bookmark this popup is currently displaying, or null when hidden.
        /// </summary>
        public Bookmark? Current { get; private set; }

        public BookmarkPopup() => InitializeComponent();

        public void Show(double canvasX, double canvasY, Bookmark bookmark)
        {
            Current = bookmark;
            TxtName.Text = bookmark.Name;
            TxtCoords.Text = $"{bookmark.Lat:F4}, {bookmark.Lon:F4}";
            Visibility = Visibility.Visible;

            Canvas.SetLeft(this, canvasX + 12);
            Canvas.SetTop(this, canvasY - 10);

            PopupOpened?.Invoke();
        }

        public void Hide()
        {
            Visibility = Visibility.Collapsed;
            Current = null;
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

        private void BtnRename_Click(object sender, RoutedEventArgs e)
        {
            RenameRequested?.Invoke();
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            DeleteRequested?.Invoke();
        }

        private void BtnDismiss_Click(object sender, RoutedEventArgs e) => Hide();
    }
}
