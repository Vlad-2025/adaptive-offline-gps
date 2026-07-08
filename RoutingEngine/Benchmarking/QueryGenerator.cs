using System;
using System.Collections.Generic;
using System.Diagnostics;
using adaptive_offline_gps.RoutingEngine.RoadGraphBuilding;

namespace adaptive_offline_gps.RoutingEngine.Benchmarking
{
    /// <summary>
    /// Generates query pairs (start, end node) for benchmarking.
    /// 
    /// Strategy: pick start uniformly at random from all nodes, then pick end
    /// uniformly at random and check if the haversine distance falls in the
    /// target bucket. If not, retry. Bounded retries per slot via MaxPairAttempts.
    /// 
    /// Deterministic given the seed - same seed always produces the same pairs.
    /// </summary>
    public class QueryGenerator
    {
        private readonly RoadGraph _graph;
        private readonly int _seed;
        private readonly int _maxAttemptsPerSlot;

        public QueryGenerator(RoadGraph graph, int seed, int maxAttemptsPerSlot = 200)
        {
            _graph = graph;
            _seed = seed;
            _maxAttemptsPerSlot = maxAttemptsPerSlot;
        }

        /// <summary>
        /// Generate `pairsPerBucket` query pairs for each bucket. Returns a flat
        /// list; the bucket label is stored on each pair for grouping later.
        /// </summary>
        /// <param name="failedBuckets">
        /// Buckets where we could not fill all requested slots. Empty if everything
        /// succeeded. Useful to surface in the UI ("could only generate 2/5 long
        /// queries - graph may be too small").
        /// </param>
        public List<QueryPair> Generate(
            IReadOnlyList<DistanceBucket> buckets,
            int pairsPerBucket,
            out List<(DistanceBucket bucket, int generated, int requested)> failedBuckets)
        {
            var rng = new Random(_seed);
            var pairs = new List<QueryPair>();
            failedBuckets = new List<(DistanceBucket, int, int)>();

            int nextId = 1;
            int n = _graph.NodeCount;

            foreach (var bucket in buckets)
            {
                int generated = 0;
                int attempts = 0;

                while (generated < pairsPerBucket && attempts < _maxAttemptsPerSlot * pairsPerBucket)
                {
                    attempts++;

                    int s = rng.Next(n);
                    int e = rng.Next(n);

                    if (s == e) 
                        continue;

                    double km = HaversineKm(
                        _graph.NodeLat[s], _graph.NodeLon[s],
                        _graph.NodeLat[e], _graph.NodeLon[e]);

                    if (km < bucket.MinKm || km >= bucket.MaxKm) 
                        continue;

                    pairs.Add(new QueryPair(
                        Id: nextId++,
                        StartNode: s,
                        EndNode: e,
                        StraightLineKm: km,
                        BucketLabel: bucket.Label));

                    generated++;
                }

                if (generated < pairsPerBucket)
                {
                    failedBuckets.Add((bucket, generated, pairsPerBucket));
                    Debug.WriteLine(
                        $"QueryGenerator: bucket {bucket.Label} produced only {generated}/{pairsPerBucket} pairs " +
                        $"after {attempts} attempts (graph may be too small for this bucket)");
                }
            }

            return pairs;
        }

        /// <summary>
        /// Build a single-pair "generated" list for SinglePair mode
        /// </summary>
        public QueryPair Single(int start, int end)
        {
            double km = HaversineKm(
                _graph.NodeLat[start], _graph.NodeLon[start],
                _graph.NodeLat[end], _graph.NodeLon[end]);

            string label = km < 10 ? DistanceBucket.Short.Label
                         : km < 100 ? DistanceBucket.Medium.Label
                         : DistanceBucket.Long.Label;

            return new QueryPair(1, start, end, km, label);
        }

        //  Haversine in kilometres
        //  Same primitive RoadGraphBuilder uses, repeated here so the benchmark module doesn't reach across to RoadGraphBuilder
        //  (and to return km not m, which matches bucket units)
        private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371.0; // km
            double dLat = (lat2 - lat1) * Math.PI / 180.0;
            double dLon = (lon2 - lon1) * Math.PI / 180.0;
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) *
                       Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

            return R * c;
        }
    }
}