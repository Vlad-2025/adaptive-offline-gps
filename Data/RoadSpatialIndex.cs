using Mapsui.Nts;
using NetTopologySuite.Index.Strtree;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace adaptive_offline_gps.Data
{
    public class RoadSpatialIndex
    {
        public STRtree<GeometryFeature> MajorLow { get; } = new();
        public STRtree<GeometryFeature> MajorFull { get; } = new();

        public STRtree<GeometryFeature> MediumLow { get; } = new();
        public STRtree<GeometryFeature> MediumFull { get; } = new();

        public STRtree<GeometryFeature> Minor { get; } = new();

        public void BuildAll()
        {
            MajorFull.Build();
            MajorLow.Build();

            MediumFull.Build();
            MediumLow.Build();

            Minor.Build();
        }
    }
}
