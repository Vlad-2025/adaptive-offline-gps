using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace adaptive_offline_gps.RoutingEngine.RoadGraphBuilding
{
    /// <summary>
    /// Result of applying a scenario CSV: the weighted graph plus diagnostics.
    /// </summary>
    public sealed class ScenarioApplyResult
    {
        public RoadGraph Graph;          //  weighted graph (shares base arrays, fresh EdgeWeight)
        public string ScenarioName;

        public int CsvRows;              //  rows read from the CSV
        public int Matched;              //  rows mapped to a real edge
        public int Unmatched;            //  rows whose (from,to) pair had no edge
        public int EdgesMultiplied;      //  distinct edges that received a non-1.0 factor

        /// <summary>Fraction of CSV rows that matched an edge, 0..1.</summary>
        public double Coverage => CsvRows == 0 ? 1.0 : (double)Matched / CsvRows;
    }

    /// <summary>
    /// Loads a per-scenario multiplier CSV (from generate_scenarios.py) and applies
    /// it to a base RoadGraph, producing a new graph whose EdgeWeight is the base
    /// weight times the scenario multiplier.
    ///
    /// CSV format (one file per scenario, e.g. scenario_winter.csv):
    ///     from_osm_id,to_osm_id,multiplier
    ///     244533748,265929626,1.0660
    ///     ...
    /// Edges absent from the CSV keep their base weight (multiplier 1.0) - the
    /// Python writer omits unity-multiplier rows to keep files compact.
    ///
    /// The weighted graph is meant to be handed to CH preprocessing exactly like
    /// the base graph. CH stays unaware that scenarios exist.
    /// </summary>
    public static class ScenarioWeightLoader
    {
        /// <summary>
        /// Apply a scenario CSV to a base graph.
        /// </summary>
        /// <param name="baseGraph">The base RoadGraph (default weights). Not mutated.</param>
        /// <param name="csvPath">Path to a scenario_*.csv file.</param>
        /// <param name="minCoverage">
        /// If matched-row fraction falls below this, throws - a low value almost
        /// always means the Python osm_parser and C# RoadGraphBuilder have drifted
        /// out of sync. Set to 0 to disable the check.
        /// </param>
        public static ScenarioApplyResult Apply(
            RoadGraph baseGraph, string csvPath, double minCoverage = 0.90)
        {
            if (baseGraph.NodeOsmId == null)
                throw new InvalidOperationException(
                    "Base graph has no NodeOsmId - it was built/loaded with an " +
                    "older serializer. Rebuild the .rgph cache (v3).");

            var edgeIndex = BuildEdgeIndex(baseGraph);

            //  Start from the base weights; every edge defaults to x1.0.
            var newWeight = (float[])baseGraph.EdgeWeight.Clone();

            var result = new ScenarioApplyResult
            {
                ScenarioName = ScenarioNameFromPath(csvPath),
            };

            using (var sr = new StreamReader(csvPath))
            {
                string line = sr.ReadLine();   //  header: from_osm_id,to_osm_id,multiplier
                if (line == null)
                    throw new IOException($"Scenario CSV is empty: {csvPath}");

                while ((line = sr.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;

                    if (!TryParseRow(line, out long fromId, out long toId, out float mult))
                        continue;   //  skip malformed line

                    result.CsvRows++;

                    if (edgeIndex.TryGetValue((fromId, toId), out int e))
                    {
                        newWeight[e] = baseGraph.EdgeWeight[e] * mult;
                        result.Matched++;
                        result.EdgesMultiplied++;
                    }
                    else
                    {
                        result.Unmatched++;
                    }
                }
            }

            //  Weighted graph: share every immutable base array, swap EdgeWeight.
            result.Graph = new RoadGraph
            {
                NodeLat     = baseGraph.NodeLat,
                NodeLon     = baseGraph.NodeLon,
                NodeOsmId   = baseGraph.NodeOsmId,
                EdgeHead    = baseGraph.EdgeHead,
                EdgeTarget  = baseGraph.EdgeTarget,
                EdgeGeomLat = baseGraph.EdgeGeomLat,
                EdgeGeomLon = baseGraph.EdgeGeomLon,
                EdgeWeight  = newWeight,
            };

            Debug.WriteLine(
                $"[ScenarioWeightLoader] {result.ScenarioName}: " +
                $"{result.CsvRows:N0} rows, {result.Matched:N0} matched, " +
                $"{result.Unmatched:N0} unmatched ({result.Coverage:P1} coverage)");

            if (minCoverage > 0 && result.CsvRows > 0 && result.Coverage < minCoverage)
                throw new InvalidOperationException(
                    $"Scenario '{result.ScenarioName}' coverage {result.Coverage:P1} " +
                    $"is below the {minCoverage:P0} threshold. The Python osm_parser " +
                    $"and C# RoadGraphBuilder edge sets have likely diverged " +
                    $"(check SpeedMap and one-way rules on both sides).");

            return result;
        }

        /// <summary>
        /// Build the (fromOsmId, toOsmId) -> edgeIndex lookup from the CSR.
        /// For each edge, the source node comes from EdgeHead ranges and the
        /// target from EdgeTarget; NodeOsmId converts both to OSM IDs.
        /// </summary>
        private static Dictionary<(long, long), int> BuildEdgeIndex(RoadGraph g)
        {
            var map = new Dictionary<(long, long), int>(g.EdgeCount);

            for (int node = 0; node < g.NodeCount; node++)
            {
                long fromOsm = g.NodeOsmId[node];
                int start = g.EdgeHead[node];
                int end = g.EdgeHead[node + 1];

                for (int e = start; e < end; e++)
                {
                    long toOsm = g.NodeOsmId[g.EdgeTarget[e]];
                    //  Parallel edges between the same junction pair are rare but
                    //  possible (e.g. dual carriageways collapsed to one junction
                    //  pair). Last-write-wins; the multiplier is identical anyway
                    //  since Python keys on the same pair.
                    map[(fromOsm, toOsm)] = e;
                }
            }

            return map;
        }

        private static bool TryParseRow(string line, out long from, out long to, out float mult)
        {
            from = to = 0;
            mult = 1.0f;

            int c1 = line.IndexOf(',');
            if (c1 < 0) 
                return false;

            int c2 = line.IndexOf(',', c1 + 1);
            if (c2 < 0) 
                return false;

            var sFrom = line.AsSpan(0, c1);
            var sTo   = line.AsSpan(c1 + 1, c2 - c1 - 1);
            var sMult = line.AsSpan(c2 + 1);

            return long.TryParse(sFrom, NumberStyles.Integer, CultureInfo.InvariantCulture, out from)
                && long.TryParse(sTo,   NumberStyles.Integer, CultureInfo.InvariantCulture, out to)
                && float.TryParse(sMult, NumberStyles.Float, CultureInfo.InvariantCulture, out mult);
        }

        /// <summary>
        /// "…/scenario_winter.csv" -> "winter". Falls back to the bare filename.
        /// </summary>
        public static string ScenarioNameFromPath(string csvPath)
        {
            string name = Path.GetFileNameWithoutExtension(csvPath);
            const string prefix = "scenario_";
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                name = name.Substring(prefix.Length);
            return name;
        }
    }
}
