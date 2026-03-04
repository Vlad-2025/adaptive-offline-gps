using adaptive_offline_gps.Data;
using BruTile.Wmts.Generated;
using Mapsui.Nts;
using NetTopologySuite.Geometries;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace adaptive_offline_gps
{
    public class RoadLodSelector
    {
        private readonly RoadSpatialIndex _index;

        public RoadLodSelector(RoadSpatialIndex index)
        {
            _index = index;
        }

        public List<GeometryFeature> GetVisible(Envelope envelope, double resolution)
        {
            var visible = new List<GeometryFeature>();

            if (resolution > 150)
            {
                //visible.AddRange(_index.MajorLow.Query(envelope));
                visible.Add(_index.MergedMajorLow);
            }
            else if (resolution > 60)
            {
                visible.AddRange(_index.MajorFull.Query(envelope));
                visible.AddRange(_index.MediumLow.Query(envelope));
            }
            else if (resolution > 5)
            {
                visible.AddRange(_index.MajorFull.Query(envelope));
                visible.AddRange(_index.MediumFull.Query(envelope));
            }
            else
            {
                visible.AddRange(_index.MajorFull.Query(envelope));
                visible.AddRange(_index.MediumFull.Query(envelope));
                visible.AddRange(_index.Minor.Query(envelope));
            }

            return visible;
        }
    }
}
