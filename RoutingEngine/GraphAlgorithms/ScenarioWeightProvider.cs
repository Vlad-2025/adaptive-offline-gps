using System;
using System.IO;
using System.Collections.Generic;
using System.Globalization;

namespace adaptive_offline_gps.RoutingEngine.GraphAlgorithms
{
    /// <summary>
    /// Applies a scenario's per-edge multipliers as a live weight provider.
    ///
    /// IMPORTANT - use this only with the plain (Dijkstra/A*) router.
    /// A contraction hierarchy bakes weights in at preprocessing time, so a CH
    /// built from base weights is INVALID under a runtime multiplier. For CH,
    /// apply the scenario *before* preprocessing via ScenarioWeightLoader and
    /// build a dedicated CH per scenario. This provider exists for the non-CH
    /// fallback path where weights are read live during search.
    ///
    /// The multiplier array is indexed by CSR edge index. Edges with no entry
    /// default to 1.0 (the Python writer omits unity-multiplier rows).
    /// </summary>
    public sealed class ScenarioWeightProvider : IEdgeWeightProvider
    {
        private readonly float[] _multiplier;   //  indexed by edge index; 1.0 = unchanged

        public string ScenarioName { get; }

        private ScenarioWeightProvider(string scenarioName, float[] multiplier)
        {
            ScenarioName = scenarioName;
            _multiplier = multiplier;
        }

        public float GetWeight(int edgeIndex, float baseWeight)
            => baseWeight * _multiplier[edgeIndex];

        /// <summary>
        /// Build a provider from a scenario CSV against a base graph.
        ///
        /// Mirrors ScenarioWeightLoader's edge-matching, but stores the
        /// multipliers rather than producing a new graph. The graph is only
        /// read here (its CSR + NodeOsmId), never modified.
        /// </summary>
        public static ScenarioWeightProvider FromCsv(
            RoutingEngine.RoadGraphBuilding.RoadGraph baseGraph, string csvPath)
        {
            if (baseGraph.NodeOsmId == null)
                throw new InvalidOperationException("Base graph has no NodeOsmId - rebuild the .rgph cache (v3).");

            //  (fromOsmId, toOsmId) -> edgeIndex
            var edgeIndex = new Dictionary<(long, long), int>(baseGraph.EdgeCount);
            for (int node = 0; node < baseGraph.NodeCount; node++)
            {
                long fromOsm = baseGraph.NodeOsmId[node];
                int start = baseGraph.EdgeHead[node];
                int end = baseGraph.EdgeHead[node + 1];
                for (int e = start; e < end; e++)
                    edgeIndex[(fromOsm, baseGraph.NodeOsmId[baseGraph.EdgeTarget[e]])] = e;
            }

            var mult = new float[baseGraph.EdgeCount];
            for (int i = 0; i < mult.Length; i++)
                mult[i] = 1.0f;

            using (var sr = new StreamReader(csvPath))
            {
                if (sr.ReadLine() == null)   //  header
                    throw new IOException($"Scenario CSV is empty: {csvPath}");

                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.Length == 0) 
                        continue;

                    int c1 = line.IndexOf(',');
                    if (c1 < 0) 
                        continue;

                    int c2 = line.IndexOf(',', c1 + 1);
                    if (c2 < 0) 
                        continue;

                    if (!long.TryParse(line.AsSpan(0, c1), NumberStyles.Integer, CultureInfo.InvariantCulture, out long fromId))
                        continue;

                    if (!long.TryParse(line.AsSpan(c1 + 1, c2 - c1 - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out long toId))
                        continue;

                    if (!float.TryParse(line.AsSpan(c2 + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float m))
                        continue;

                    if (edgeIndex.TryGetValue((fromId, toId), out int e))
                        mult[e] = m;
                }
            }

            return new ScenarioWeightProvider(ScenarioNameFromPath(csvPath), mult);
        }

        private static string ScenarioNameFromPath(string csvPath)
        {
            string name = Path.GetFileNameWithoutExtension(csvPath);
            const string prefix = "scenario_";

            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                name = name.Substring(prefix.Length);

            return name;
        }
    }
}
