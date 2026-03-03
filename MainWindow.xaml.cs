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

        private STRtree<GeometryFeature> _majorLowIndex = new();
        private STRtree<GeometryFeature> _majorFullIndex = new();

        private STRtree<GeometryFeature> _mediumLowIndex = new();
        private STRtree<GeometryFeature> _mediumFullIndex = new();

        private STRtree<GeometryFeature> _minorIndex = new();

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

            /*
             * We create different memory layers to store different kinds of information about the nodes
             * 
             * This helps in creating LODs for faster rendering in exchange for detail and in managing 
             * different kinds of node structures
             */
            _roadLayer = new MemoryLayer
            {
                Name = "Roads"
            };

            map.Layers.Add(_roadLayer);
            mapControl.Map = map;   //  Set the map created earlier as the 'canvas'

            mapControl.Map.Navigator.ViewportChanged += ViewportChanged;

            await BuildRoadIndexesAsync(osmFilePath);

            _isReady = true;
        }

        private Task BuildRoadIndexesAsync(string filePath)
        {
            return Task.Run(() =>
            {
                var nodeDict = new Dictionary<long, Coordinate>();

                using var stream = File.OpenRead(filePath);
                var source = new PBFOsmStreamSource(stream);

                // First pass: collect nodes
                foreach (var element in source)
                {
                    if (element.Type == OsmGeoType.Node)
                    {
                        var node = (Node)element;   //  OsmSharp node cast
                        if (node.Id.HasValue &&
                            node.Latitude.HasValue &&
                            node.Longitude.HasValue)
                        {
                            var mercator = SphericalMercator.FromLonLat(
                                node.Longitude.Value,
                                node.Latitude.Value);

                            nodeDict[node.Id.Value] = new Coordinate(mercator.x, mercator.y);
                        }
                    }
                }

                // Reset stream for second pass
                stream.Position = 0;
                source = new PBFOsmStreamSource(stream);

                // Second pass: build ways
                foreach (var element in source)
                {
                    if (element.Type == OsmGeoType.Way)
                    {
                        var way = (Way)element;

                        if (way.Tags == null || !way.Tags.ContainsKey("highway"))
                            continue;

                        var highwayType = way.Tags["highway"];

                        var coords = new List<Coordinate>();

                        foreach (var nodeId in way.Nodes)
                        {
                            if (nodeDict.TryGetValue(nodeId, out var coord))
                                coords.Add(coord);
                        }

                        if (coords.Count < 2) continue;

                        var line = new LineString(coords.ToArray());

                        //  Heavy simplification
                        var simplifiedLow = NetTopologySuite.Simplify
                        .TopologyPreservingSimplifier
                        .Simplify(line, 300);


                        //  Medium simplification
                        var simplifiedMedium = NetTopologySuite.Simplify
                        .TopologyPreservingSimplifier
                        .Simplify(line, 80);

                        var fullFeature = new GeometryFeature { Geometry = line };
                        var lowFeature = new GeometryFeature { Geometry = simplifiedLow };
                        var mediumFeature = new GeometryFeature { Geometry = simplifiedMedium };

                        //  Categorize by importance
                        if (highwayType == "motorway" || highwayType == "trunk")
                        {
                            _majorFullIndex.Insert(line.EnvelopeInternal, fullFeature);
                            _majorLowIndex.Insert(simplifiedLow.EnvelopeInternal, lowFeature);
                        }
                        else if (highwayType == "primary" || highwayType == "secondary")
                        {
                            _mediumFullIndex.Insert(line.EnvelopeInternal, fullFeature);
                            _mediumLowIndex.Insert(simplifiedMedium.EnvelopeInternal, mediumFeature);
                        }
                        else
                        {
                            _minorIndex.Insert(line.EnvelopeInternal, fullFeature);
                        }
                    }
                }

                _majorFullIndex.Build();
                _majorLowIndex.Build();

                _mediumFullIndex.Build();
                _mediumLowIndex.Build();

                _minorIndex.Build();
            });
        }

        private void ViewportChanged(object sender, System.EventArgs e)
        {
            if (!_isReady) return;
            UpdateVisibleRoads();
        }

        private void UpdateVisibleRoads()
        {
            var viewport = mapControl.Map.Navigator.Viewport;
            var resolution = viewport.Resolution;

            Dispatcher.Invoke(() =>
            {
                Title = $"Resolution: {resolution:F2}";
            });

            var extent = viewport.ToExtent();
            if (extent == null) return;

            var envelope = new Envelope(
                extent.MinX,
                extent.MaxX,
                extent.MinY,
                extent.MaxY);

            var visible = new List<GeometryFeature>();

            //  Zoom-based LOD
            if (resolution > 150)
            {
                //  Load heavily simplified major roads at far zoom
                visible.AddRange(_majorLowIndex.Query(envelope));
            }
            else if (resolution > 60)
            {
                //  Load full major roads and simplified mediums
                visible.AddRange(_majorFullIndex.Query(envelope));
                visible.AddRange(_mediumLowIndex.Query(envelope));
            }
            else if (resolution > 20)
            {
                //  Load full major and full mediums
                visible.AddRange(_majorFullIndex.Query(envelope));
                visible.AddRange(_mediumFullIndex.Query(envelope));
            }
            else
            {
                //  Load all
                visible.AddRange(_majorFullIndex.Query(envelope));
                visible.AddRange(_mediumFullIndex.Query(envelope));
                visible.AddRange(_minorIndex.Query(envelope));
            }

            _roadLayer.Features = visible;
            mapControl.RefreshGraphics();
        }
    }
}