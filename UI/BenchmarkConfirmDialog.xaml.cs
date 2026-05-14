using System.Windows;

namespace adaptive_offline_gps.UI
{
    public partial class BenchmarkConfirmDialog : Window
    {
        public string EstimateText
        {
            get => TxtEstimateLine.Text;
            set => TxtEstimateLine.Text = value;
        }

        public BenchmarkConfirmDialog()
        {
            InitializeComponent();
        }

        private void BtnConfirm_Click(object sender, RoutedEventArgs e)
        {
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
