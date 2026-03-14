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

        //  =================== Navigation stack - lets user go back up the hierarchy
        private readonly Stack<(string Title, string ParentId)> _navStack = new();
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
            await LoadLevelAsync("continents", "");
        }

        //  =================== Load a level of the hierarchy 
        private async Task LoadLevelAsync(string title, string parentId)
        {
            CboRegion.IsEnabled = false;
            CboRegion.ItemsSource = new[] { "loading…" };
            CboRegion.SelectedIndex = 0;
            BtnBack.Visibility = _navStack.Count > 0
                ? Visibility.Visible : Visibility.Collapsed;
            TxtLevel.Text = $"// {title.ToUpper()}";

            ResetSelection();

            _currentRegions = string.IsNullOrEmpty(parentId)
                ? await _scraper.GetContinentsAsync()
                : await _scraper.GetChildrenAsync(parentId);

            CboRegion.ItemsSource = _currentRegions.ConvertAll(r =>
                r.HasChildren ? $"{r.Name}  ▸" : r.Name);
            CboRegion.SelectedIndex = -1;
            CboRegion.IsEnabled = true;
        }

        //  =================== Region selected 
        private void CboRegion_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CboRegion.SelectedIndex < 0) return;
            if (CboRegion.SelectedIndex >= _currentRegions.Count) return;

            _selectedRegion = _currentRegions[CboRegion.SelectedIndex];

            //  Show file info if this region has a direct download
            if (_selectedRegion.PbfUrl != null)
            {
                TxtFileName.Text = System.IO.Path.GetFileName(_selectedRegion.PbfUrl);
                TxtFileSize.Text = _selectedRegion.SizeBytes > 0
                    ? $"{_selectedRegion.SizeBytes / 1024.0 / 1024.0:F0} MB"
                    : "size unknown";
                FileInfoRow.Visibility = Visibility.Visible;
                BtnDownload.IsEnabled = true;
            }
            else
            {
                FileInfoRow.Visibility = Visibility.Collapsed;
                BtnDownload.IsEnabled = false;
            }

            //  Show drill-in button if subregions exist
            BtnDrillIn.Visibility = _selectedRegion.HasChildren
                ? Visibility.Visible : Visibility.Collapsed;
        }

        //  =================== Drill into subregions 
        private async void BtnDrillIn_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedRegion == null) return;
            _navStack.Push(($"back to {TxtLevel.Text}", _selectedRegion.Parent));
            await LoadLevelAsync(_selectedRegion.Name, _selectedRegion.Id);
        }

        //  =================== Back 
        private async void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            if (_navStack.Count == 0) return;
            var (title, parentId) = _navStack.Pop();
            await LoadLevelAsync(title, parentId);
        }

        //  =================== Browse existing file
        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select OSM PBF file",
                Filter = "OSM PBF files (*.osm.pbf)|*.osm.pbf|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog() != true) return;
            FileReady?.Invoke(dlg.FileName);
        }

        //  =================== Download
        private async void BtnDownload_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedRegion?.PbfUrl == null) return;

            //  Strip .osm.pbf before passing to SaveFileDialog
            var baseName = System.IO.Path.GetFileName(_selectedRegion.PbfUrl)
                               .Replace("-latest.osm.pbf", "")
                               .Replace(".osm.pbf", "");

            var dlg = new SaveFileDialog
            {
                Title = "Save PBF file",
                FileName = baseName,
                Filter = "OSM PBF files (*.osm.pbf)|*.osm.pbf",
                DefaultExt = "osm.pbf"
            };
            if (dlg.ShowDialog() != true) return;

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

                System.IO.File.Delete(destPath);
                FileReady?.Invoke(roadsPath);
            }
            catch (OperationCanceledException)
            {
                TxtProgressLabel.Text = "cancelled";
                if (System.IO.File.Exists(destPath))
                    System.IO.File.Delete(destPath);
            }
            catch (Exception ex)
            {
                TxtProgressLabel.Text = $"error: {ex.Message}";
                BtnDownload.IsEnabled = true;
            }
            finally
            {
                ProgressBar.IsIndeterminate = false;
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