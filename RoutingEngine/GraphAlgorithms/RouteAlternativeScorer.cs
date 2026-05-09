using System.Collections.Generic;
using adaptive_offline_gps.RoutingEngine.RoadGraphBuilding;

namespace adaptive_offline_gps.RoutingEngine.GraphAlgorithms
{
    /// <summary>
    /// Scores and filters candidate alternative routes against an optimal route.
    ///
    /// Three criteria, all standard in the alternative-routes literature
    /// (Abraham, Delling, Goldberg, Werneck 2010):
    ///
    ///   1. STRETCH: cost(alt) ≤ (1 + ε) × cost(optimal)
    ///      Alternatives must not be much longer than the optimal.
    ///      ε = 0.25 means at most 25% longer.
    ///
    ///   2. SHARING vs optimal: shared length / alt length ≤ τ
    ///      Alternatives must be substantially different from the optimal.
    ///      τ = 0.80 means alt shares at most 80% of its length with optimal.
    ///
    ///   3. SHARING vs other accepted alternatives: same threshold.
    ///      Alternatives must also differ from each other, otherwise we'd
    ///      return three slightly-shifted copies of the same route.
    ///
    /// Sharing is measured by EDGE LENGTH (in metres), not edge count, because
    /// "70% of the route by distance is the same" matches user intuition better
    /// than "70 of 100 edges are shared" (a long highway segment dominates many
    /// short urban segments in user perception).
    /// </summary>
    public class RouteAlternativesScorer
    {
        public float MaxStretch { get; init; } = 0.25f;
        public float MaxSharing { get; init; } = 0.80f;

        private readonly RoadGraph _graph;

        public RouteAlternativesScorer(RoadGraph graph)
        {
            _graph = graph;
        }

        /// <summary>
        /// Filter candidate paths and return the accepted alternatives in order
        /// of increasing cost. Does NOT include the optimal in the output.
        /// </summary>
        /// <param name="optimalPath">The unpacked optimal node sequence.</param>
        /// <param name="optimalCost">CH cost of the optimal route (NOT distance in metres).</param>
        /// <param name="candidates">
        ///   Candidate alternatives as (path, cost) pairs, ideally pre-sorted by cost ascending.
        /// </param>
        /// <param name="maxAlternatives">Cap on how many to return.</param>
        public List<(int[] path, float cost)> Filter(
            int[] optimalPath,
            float optimalCost,
            IEnumerable<(int[] path, float cost)> candidates,
            int maxAlternatives)
        {
            float stretchLimit = optimalCost * (1.0f + MaxStretch);

            //  Edge sets are keyed by an order-independent edge identifier so
            //  routes that share the same physical road in opposite directions
            //  count as sharing. Encoded as long(min, max).
            var optimalEdges = new Dictionary<long, double>(optimalPath.Length); // edge -> length(m)
            double optimalLength = AddPathEdges(optimalPath, optimalEdges);

            var accepted = new List<(int[] path, float cost)>();
            var acceptedEdgeSets = new List<Dictionary<long, double>>();
            var acceptedLengths = new List<double>();

            foreach (var (path, cost) in candidates)
            {
                if (accepted.Count >= maxAlternatives) break;

                //  1. Stretch
                if (cost > stretchLimit) continue;

                //  Skip exact duplicates of optimal (cost == optimalCost path)
                if (PathsEqual(path, optimalPath)) continue;

                //  Compute this candidate's edge set and length once
                var altEdges = new Dictionary<long, double>(path.Length);
                double altLength = AddPathEdges(path, altEdges);
                if (altLength <= 0) continue;

                //  2. Sharing vs optimal
                double sharedWithOptimal = SharedLength(altEdges, optimalEdges);
                if (sharedWithOptimal / altLength > MaxSharing) continue;

                //  3. Sharing vs each already-accepted alternative
                bool tooSimilar = false;
                for (int i = 0; i < acceptedEdgeSets.Count; i++)
                {
                    double shared = SharedLength(altEdges, acceptedEdgeSets[i]);
                    //  Use min(altLength, otherLength) as the denominator so we
                    //  catch "this candidate is essentially a subset of an existing alt"
                    //  symmetrically.
                    double denom = altLength < acceptedLengths[i] ? altLength : acceptedLengths[i];
                    if (shared / denom > MaxSharing) { tooSimilar = true; break; }
                }
                if (tooSimilar) continue;

                accepted.Add((path, cost));
                acceptedEdgeSets.Add(altEdges);
                acceptedLengths.Add(altLength);
            }

            return accepted;
        }

        //  Order-independent edge key: encode the edge as (min<<32)|max so that
        //  forward and reverse traversals of the same physical road map to the
        //  same key. This is correct for "did these routes use the same road,"
        //  which is what users mean by "sharing."
        private static long EdgeKey(int a, int b)
        {
            int lo = a < b ? a : b;
            int hi = a < b ? b : a;
            return ((long)lo << 32) | (uint)hi;
        }

        //  Adds every (consecutive-pair) edge in `path` to `into`, with the
        //  edge's length in metres as the value. Returns total path length.
        //  Length comes from haversine over node coordinates - same primitive
        //  used elsewhere for consistency.
        private double AddPathEdges(int[] path, Dictionary<long, double> into)
        {
            double total = 0;
            for (int i = 0; i < path.Length - 1; i++)
            {
                long key = EdgeKey(path[i], path[i + 1]);
                double len = RoadGraphBuilder.Haversine(
                    (_graph.NodeLat[path[i]], _graph.NodeLon[path[i]]),
                    (_graph.NodeLat[path[i + 1]], _graph.NodeLon[path[i + 1]]));

                //  If the same edge appears twice in a path (shouldn't normally,
                //  but possible with weird unpackings), take the max length so we
                //  don't undercount sharing.
                if (!into.TryGetValue(key, out double existing) || len > existing)
                    into[key] = len;

                total += len;
            }
            return total;
        }

        //  Sum the lengths of edges present in BOTH dictionaries. We use the
        //  length from `a` (caller's "this route") since the question is
        //  "how much of THIS route overlaps with the other."
        private static double SharedLength(Dictionary<long, double> a, Dictionary<long, double> b)
        {
            //  Iterate the smaller dict for speed.
            var (small, big) = a.Count <= b.Count ? (a, b) : (b, a);
            double shared = 0;
            foreach (var kv in small)
                if (big.ContainsKey(kv.Key)) shared += kv.Value;
            return shared;
        }

        private static bool PathsEqual(int[] a, int[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }
    }
}