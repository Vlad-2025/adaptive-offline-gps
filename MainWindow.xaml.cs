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
using adaptive_offline_gps.UI;

namespace adaptive_offline_gps
{
    public partial class MainWindow : Window
    {
        private MapEngine? _mapEngine;
        private readonly string? _osmFilePath;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            SizeChanged += (_, _) => UpdateMapSquare();
        }

        //  =================== Start-Up
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            TopBar.LoadFileRequested += OnLoadFileRequested;
        }
    }
}