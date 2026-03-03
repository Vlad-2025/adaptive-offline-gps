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

namespace adaptive_offline_gps
{
    public partial class MainWindow : Window
    {
        private MemoryLayer _roadLayer;

        private RoadLodSelector _lodSelector;

        private bool _isReady = false;

        public MainWindow()
        {
            InitializeComponent();
            InitializeMap();
        }

        private async void InitializeMap()
        {
            //string osmFilePath = @"YOUR_PATH_TO_FILE\malta-roads.osm.pbf";
            string osmFilePath = @"YOUR_PATH_TO_FILE\romania-roads.osm.pbf";

            var map = new Map();    //  Initialize a new Map() object on which we render the nodes
            map.Layers.Add(Mapsui.Tiling.OpenStreetMap.CreateTileLayer());  //  Fetch 'visual' tiles from OpenStreetMap over HTTP

            _roadLayer = new MemoryLayer { Name = "Roads" };
            map.Layers.Add(_roadLayer);

            mapControl.Map = map;
            map.Navigator.ViewportChanged += ViewportChanged;

            var builder = new RoadIndexBuilder();
            var index = await builder.BuildAsync(osmFilePath);

            _lodSelector = new RoadLodSelector(index);

            _isReady = true;
        }

        private void ViewportChanged(object sender, System.EventArgs e)
        {
            if (!_isReady)
                return;

            var viewport = mapControl.Map.Navigator.Viewport;
            var extent = viewport.ToExtent();

            if (extent == null)
                return;

            var envelope = new Envelope(
                extent.MinX, extent.MaxX,
                extent.MinY, extent.MaxY);

            var visible = _lodSelector.GetVisible(
                envelope,
                viewport.Resolution);

            _roadLayer.Features = visible;
            mapControl.RefreshGraphics();
        }
    }
}