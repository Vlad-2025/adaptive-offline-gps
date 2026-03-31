using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using adaptive_offline_gps.WebDownloader;

namespace adaptive_offline_gps.UI
{
    public partial class DownloadPanel : UserControl
    {
        public event Action<string>? FileReady;

        private readonly GeofabrikScraper _scraper = new();
        private readonly MapDownloader _downloader = new();

        private readonly Stack<(string Title, string PageUrl)> _navStack = new();
        private List<GeofabrikRegion> _currentRegions = new();
        private GeofabrikRegion? _selectedRegion;
        private CancellationTokenSource? _cts;

        public DownloadPanel()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await LoadLevelAsync("world", "https://download.geofabrik.de/index.html");
            }
            catch (Exception ex)
            {
                TxtLevel.Text = "// ERROR LOADING";
                CboRegion.IsEnabled = false;
                TxtOfflineMessage.Text = $"could not reach geofabrik: {ex.Message}";
                TxtOfflineMessage.Visibility = Visibility.Visible;
            }
        }

        //  Load a level
        //  Track the current page URL so we can push it on drill-in
        private string _currentPageUrl = "https://download.geofabrik.de/index.html";

        private async Task LoadLevelAsync(string title, string pageUrl)
        {
            CboRegion.SelectionChanged -= CboRegion_SelectionChanged;

            CboRegion.IsEnabled = false;
            CboRegion.ItemsSource = new[] { "loading…" };
            CboRegion.SelectedIndex = 0;

            //  Store raw title, no prefix here
            TxtLevel.Text = $"// {title.ToUpper()}";

            BtnBack.Visibility = _navStack.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            ResetSelection();

            _currentRegions = await _scraper.GetRegionsAsync(pageUrl);
            _currentPageUrl = pageUrl;  //  track where we are now

            CboRegion.ItemsSource = _currentRegions.ConvertAll(r =>
                r.HasChildren ? $"{r.Name}  ▸" : r.Name);

            CboRegion.SelectedIndex = -1;
            CboRegion.IsEnabled = true;

            CboRegion.SelectionChanged += CboRegion_SelectionChanged;
        }

        //  Region selected
        private async void CboRegion_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CboRegion.SelectedIndex < 0) 
                return;

            if (CboRegion.SelectedIndex >= _currentRegions.Count) 
                return;

            _selectedRegion = _currentRegions[CboRegion.SelectedIndex];

            if (_selectedRegion.PbfUrl != null)
            {
                TxtFileName.Text = Path.GetFileName(_selectedRegion.PbfUrl);

                //  Use size from table cell; fall back to scraping the region's own page
                long size = _selectedRegion.SizeBytes;
                if (size <= 0)
                    size = await _scraper.GetPageFileSizeAsync(_selectedRegion.PageUrl);

                TxtFileSize.Text = size > 0
                    ? $"{size / 1024.0 / 1024.0:F0} MB"
                    : "size unknown";

                FileInfoRow.Visibility = Visibility.Visible;

                BtnDownload.IsEnabled = true;
            }
            else
            {
                FileInfoRow.Visibility = Visibility.Collapsed;
                BtnDownload.IsEnabled = false;
            }

            //  Check for subregions lazily; only if flagged as potentially having children
            if (_selectedRegion.HasChildren)
            {
                bool confirmed = await _scraper.HasSubregionsAsync(_selectedRegion.PageUrl);
                BtnDrillIn.Visibility = confirmed
                    ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                BtnDrillIn.Visibility = Visibility.Collapsed;
            }
        }

        //  Drill into subregions
        private async void BtnDrillIn_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedRegion == null) 
                return;

            //  Push where we are now so 'Back' can return here
            //  Strip the "// " prefix before storing so LoadLevelAsync doesn't double it
            var currentTitle = TxtLevel.Text.TrimStart('/', ' ');
            _navStack.Push((currentTitle, _currentPageUrl));

            await LoadLevelAsync(_selectedRegion.Name, _selectedRegion.PageUrl);
        }

        private async void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            if (_navStack.Count == 0) 
                return;

            var (title, pageUrl) = _navStack.Pop();

            await LoadLevelAsync(title, pageUrl);
        }

        //  Browse existing file
        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select OSM PBF file",
                Filter = "OSM PBF files (*.osm.pbf)|*.osm.pbf|All files (*.*)|*.*"
            };

            if (dlg.ShowDialog() != true) 
                return;

            FileReady?.Invoke(dlg.FileName);
        }

        //  Download
        private async void BtnDownload_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedRegion?.PbfUrl == null) 
                return;

            var baseName = Path.GetFileName(_selectedRegion.PbfUrl)
                .Replace("-latest.osm.pbf", "")
                .Replace(".osm.pbf", "");

            var dlg = new SaveFileDialog
            {
                Title = "Save PBF file",
                FileName = baseName,
                Filter = "OSM PBF files (*.osm.pbf)|*.osm.pbf",
                DefaultExt = "osm.pbf"
            };

            if (dlg.ShowDialog() != true) 
                return;

            var destPath = dlg.FileName;

            _cts = new CancellationTokenSource();
            ProgressArea.Visibility = Visibility.Visible;
            BtnDownload.IsEnabled = false;
            BtnDrillIn.IsEnabled = false;

            var downloadProgress = new Progress<(double percent, string speed, long total)>(p =>
            {
                ProgressBar.Value = p.percent;
                TxtProgressLabel.Text = $"downloading…  {p.percent * 100:F0}%";
                TxtProgressDetail.Text = p.speed;
            });

            try
            {
                await _downloader.DownloadAsync(
                    _selectedRegion.PbfUrl, destPath, downloadProgress, _cts.Token);

                TxtProgressLabel.Text = "extracting roads…";
                ProgressBar.IsIndeterminate = true;
                ProgressBar.Value = 0;

                var roadsPath = destPath.Replace(".osm.pbf", "") + "-roads.osm.pbf";
                var extractor = new OsmRoadExtractor();

                var extractProgress = new Progress<double>(p =>
                    TxtProgressDetail.Text = $"{p * 100:F0}%");

                await extractor.ExtractRoadsAsync(destPath, roadsPath, extractProgress);

                File.Delete(destPath);
                FileReady?.Invoke(roadsPath);
            }
            catch (OperationCanceledException)
            {
                TxtProgressLabel.Text = "cancelled";
                if (File.Exists(destPath)) File.Delete(destPath);
            }
            catch (Exception ex)
            {
                TxtProgressLabel.Text = $"error: {ex.Message}";
                BtnDownload.IsEnabled = true;
            }
            finally
            {
                ProgressBar.IsIndeterminate = false;
                BtnDrillIn.IsEnabled = true;
            }
        }

        private void ResetSelection()
        {
            _selectedRegion = null;
            FileInfoRow.Visibility = Visibility.Collapsed;
            BtnDownload.IsEnabled = false;
            BtnDrillIn.Visibility = Visibility.Collapsed;
        }
    }
}