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

namespace adaptive_offline_gps
{
    public partial class MainWindow : Window
    {
        private MapEngine _mapEngine;

        private readonly string osmFilePath = @"YOUR_PATH_TO_FILE\malta-roads.osm.pbf";

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _mapEngine = await MapEngine.CreateAsync(mapControl, osmFilePath);
        }
    }
}