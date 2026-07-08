using adaptive_offline_gps.RoutingEngine.RoadGraphBuilding;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace adaptive_offline_gps.RoutingEngine
{
    /// <summary>
    /// Result of preprocessing one scenario.
    /// </summary>
    public sealed class ScenarioPreprocessResult
    {
        public string ScenarioName;
        public string ChPath;             //  e.g. "romania.scenario_winter.chgr"
        public int CsvRows;
        public int MatchedRows;
        public double Coverage;
        public TimeSpan ApplyTime;        //  CSV apply
        public TimeSpan PreprocessTime;   //  CH preprocessing
        public TimeSpan SaveTime;         //  .chgr write
    }


    /// <summary>
    /// End-to-end orchestration for the scenario popup flow.
    ///
    /// Pipeline per scenario:
    ///   base RoadGraph + scenario_X.csv
    ///       -> ScenarioWeightLoader.Apply   (multiply weights)
    ///       -> CHPreprocessor.Preprocess    (build CH, ~20 min)
    ///       -> ChGraphSerializer.Save       (write country.scenario_X.chgr)
    ///
    /// Naming convention used throughout:
    ///   base graph: country.rgph
    ///   per scenario: country.scenario_name.chgr
    ///   csv input: scenariosDir/scenario_name.csv
    /// </summary>
    public static class ScenarioPipeline
    {
        //  Filename helpers ======================================================

        /// <summary>Path to the .chgr for a given country + scenario.</summary>
        public static string ChPathFor(string baseRgphPath, string scenarioName)
        {
            string dir = Path.GetDirectoryName(baseRgphPath) ?? ".";
            string country = Path.GetFileNameWithoutExtension(baseRgphPath);
            return Path.Combine(dir, $"{country}.scenario_{scenarioName}.chgr");
        }

        /// <summary>Path to a scenario CSV from a scenarios directory.</summary>
        public static string CsvPathFor(string scenariosDir, string scenarioName)
            => Path.Combine(scenariosDir, $"scenario_{scenarioName}.csv");

        /// <summary>Lists scenario names present as CSV files in a directory.</summary>
        public static List<string> ListAvailableScenarios(string scenariosDir)
        {
            var result = new List<string>();
            if (!Directory.Exists(scenariosDir)) return result;

            foreach (var file in Directory.EnumerateFiles(scenariosDir, "scenario_*.csv"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                const string prefix = "scenario_";
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    result.Add(name.Substring(prefix.Length));
            }
            result.Sort(StringComparer.OrdinalIgnoreCase);

            return result;
        }

        /// <summary>
        /// Returns scenario names from <paramref name="scenarioNames"/> that
        /// do NOT already have a fresh .chgr next to <paramref name="baseRgphPath"/>.
        /// "Fresh" = .chgr is newer than both the base .rgph and its source CSV.
        /// Use this to drive the popup: show all scenarios, pre-tick the stale ones.
        /// </summary>
        public static List<string> ListStaleScenarios(
            string baseRgphPath, string scenariosDir, IEnumerable<string> scenarioNames)
        {
            var stale = new List<string>();
            DateTime baseTs = File.Exists(baseRgphPath)
                ? File.GetLastWriteTimeUtc(baseRgphPath)
                : DateTime.MinValue;

            foreach (var name in scenarioNames)
            {
                string chPath = ChPathFor(baseRgphPath, name);
                string csvPath = CsvPathFor(scenariosDir, name);

                if (!File.Exists(chPath))
                {
                    stale.Add(name);
                    continue;
                }

                DateTime chTs = File.GetLastWriteTimeUtc(chPath);
                DateTime csvTs = File.Exists(csvPath)
                    ? File.GetLastWriteTimeUtc(csvPath)
                    : DateTime.MinValue;

                if (chTs < baseTs || chTs < csvTs)
                    stale.Add(name);
            }
            return stale;
        }

        //  Preprocessing ======================================================

        /// <summary>
        /// Preprocess one scenario: apply CSV, run CH, save .chgr.
        /// </summary>
        public static ScenarioPreprocessResult PreprocessScenario(
            RoadGraph baseGraph, string baseRgphPath,
            string scenariosDir, string scenarioName,
            Action<string> log = null)
        {
            log ??= (s => Debug.WriteLine(s));

            string csvPath = CsvPathFor(scenariosDir, scenarioName);
            string chPath = ChPathFor(baseRgphPath, scenarioName);

            if (!File.Exists(csvPath))
                throw new FileNotFoundException($"Scenario CSV not found: {csvPath}", csvPath);

            var result = new ScenarioPreprocessResult
            {
                ScenarioName = scenarioName,
                ChPath = chPath,
            };

            //  1. Apply scenario multipliers
            log($"[scenario:{scenarioName}] Applying CSV multipliers...");

            var swApply = Stopwatch.StartNew();
            var applied = ScenarioWeightLoader.Apply(baseGraph, csvPath);

            swApply.Stop();

            result.ApplyTime = swApply.Elapsed;
            result.CsvRows = applied.CsvRows;
            result.MatchedRows = applied.Matched;
            result.Coverage = applied.Coverage;

            log($"[scenario:{scenarioName}] CSV: {applied.Matched:N0}/{applied.CsvRows:N0} matched " +
                $"({applied.Coverage:P1}) in {swApply.ElapsedMilliseconds} ms");

            //  2. CH preprocessing
            log($"[scenario:{scenarioName}] CH preprocessing (this takes ~20 min)...");

            var swCh = Stopwatch.StartNew();
            var ch = new CHPreprocessor().Preprocess(applied.Graph);

            swCh.Stop();

            result.PreprocessTime = swCh.Elapsed;

            log($"[scenario:{scenarioName}] CH built in {swCh.Elapsed.TotalMinutes:F1} min");

            //  3. Save .chgr with scenario tag in the header
            log($"[scenario:{scenarioName}] Writing {chPath}");

            var swSave = Stopwatch.StartNew();
            CHGraphSerializer.Save(ch, chPath, scenarioName);

            swSave.Stop();

            result.SaveTime = swSave.Elapsed;

            log($"[scenario:{scenarioName}] Done. " +
                $"apply={result.ApplyTime.TotalSeconds:F1}s " +
                $"ch={result.PreprocessTime.TotalMinutes:F1}min " +
                $"save={result.SaveTime.TotalSeconds:F1}s");

            return result;
        }

        /// <summary>
        /// Preprocess multiple scenarios sequentially. Stops on first failure
        /// (caller can retry the remaining list).
        /// </summary>
        public static List<ScenarioPreprocessResult> PreprocessScenarios(
            RoadGraph baseGraph, string baseRgphPath,
            string scenariosDir, IEnumerable<string> scenarioNames,
            Action<string> log = null
            )
        {
            var results = new List<ScenarioPreprocessResult>();
            foreach (var name in scenarioNames)
            {
                var r = PreprocessScenario(baseGraph, baseRgphPath, scenariosDir, name, log);
                results.Add(r);
            }
            return results;
        }

        //  Runtime selection ======================================================

        /// <summary>
        /// Load a preprocessed scenario .chgr at runtime. Requires the base graph
        /// already to be loaded (the CH references it via BaseGraph).
        /// Returns null if the .chgr doesn't exist (caller should offer to preprocess).
        /// </summary>
        public static CHGraph LoadScenarioCh(
            RoadGraph baseGraph, string baseRgphPath, string scenarioName)
        {
            string chPath = ChPathFor(baseRgphPath, scenarioName);
            if (!File.Exists(chPath))
                return null;

            return CHGraphSerializer.Load(chPath, baseGraph, expectedScenario: scenarioName);
        }
    }
}
