using adaptive_offline_gps.Data;
using BruTile;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.UI.Wpf;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using OsmSharp;
using OsmSharp.Streams;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using adaptive_offline_gps.EngineMap;
using Microsoft.Extensions.Configuration;

namespace adaptive_offline_gps
{
    public partial class MainWindow : Window
    {
        private MapEngine? _mapEngine;

        private readonly string? osmFilePath;

        public MainWindow()
        {
            InitializeComponent();

            var config = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            osmFilePath = config["AppSettings:DataPath"];


            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _mapEngine = await MapEngine.CreateAsync(mapControl, osmFilePath);
        }


    }
}