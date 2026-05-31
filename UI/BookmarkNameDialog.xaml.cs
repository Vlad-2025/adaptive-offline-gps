using System.IO;
using System.Windows;
using System.Xml.Linq;

namespace adaptive_offline_gps.UI
{
    /// <summary>
    /// Small modal for entering / editing a bookmark name. Used for both
    /// "create new" (with coordinates shown) and "rename" (no coords).
    /// </summary>
    public partial class BookmarkNameDialog : Window
    {
        public string ResultName => TxtName.Text.Trim();

        public BookmarkNameDialog()
        {
            InitializeComponent();
            Loaded += (_, _) =>
            {
                TxtName.Focus();
                TxtName.SelectAll();
            };
        }

        /// <summary>
        /// Configure as "new bookmark" prompt with coordinates display.
        /// </summary>
        public void ConfigureForNew(double lat, double lon, string suggestedName = "")
        {
            TxtHeader.Text = "// NEW BOOKMARK";
            TxtCoords.Text = $"location: {lat:F4}, {lon:F4}";
            TxtName.Text = suggestedName;
        }

        /// <summary>
        /// Configure as "rename" prompt with the current name pre-filled.
        /// </summary>
        public void ConfigureForRename(string currentName)
        {
            TxtHeader.Text = "// RENAME BOOKMARK";
            TxtCoords.Text = "";
            TxtCoords.Visibility = Visibility.Collapsed;
            TxtName.Text = currentName;
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtName.Text))
            {
                MessageBox.Show(this, "name cannot be empty", "bookmark",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
