using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace adaptive_offline_gps.RoutingEngine.RoadGraphBuilding
{
    /// <summary>
    /// Status of one scenario relative to the current base graph.
    /// </summary>
    public enum ScenarioStatus
    {
        /// <summary>
        /// No .chgr file at all - needs full preprocessing.
        /// </summary>
        NotPreprocessed,

        /// <summary>
        /// .chgr exists but is older than its CSV or the base .rgph - re-preprocess.
        /// </summary>
        Stale,

        /// <summary>
        /// .chgr exists and is up to date.
        /// </summary>
        Ready,
    }

    /// <summary>
    /// Progress payload emitted by the pipeline. The UI consumes this via
    /// IProgress&lt;ScenarioProgress&gt; and updates labels / bar accordingly.
    /// </summary>
    public sealed record ScenarioProgress(
        string ScenarioName,
        int CurrentIndex,     //  1-based: "winter [4/6]"
        int TotalCount,
        string Stage,         //  "Applying CSV..." / "Building CH..." / "Saving..."
        double ChFraction     //  0..1 fraction of CH preprocessing done (0 outside CH stage)
    );

    /// <summary>
    /// Result of preprocessing one scenario.
    /// </summary>
    public sealed class ScenarioPreprocessResult
    {
        public string ScenarioName;
        public string ChPath;
        public int CsvRows;
        public int MatchedRows;
        public double Coverage;
        public TimeSpan ApplyTime;
        public TimeSpan PreprocessTime;
        public TimeSpan SaveTime;
        public bool Cancelled;
        public Exception Error;   //  null on success/cancel
    }


    /// <summary>
    /// End-to-end orchestration for the scenario popup flow.
    ///
    /// Pipeline per scenario:
    ///   base RoadGraph + scenario_X.csv
    ///       -> ScenarioWeightLoader.Apply   (multiply weights)
    ///       -> CHPreprocessor.Preprocess    (build CH, ~20 min)
    ///       -> CHGraphSerializer.Save       (write country.scenario_X.chgr)
    ///
    /// Naming convention used throughout:
    ///   base graph: &lt;country&gt;.rgph
    ///   per scenario: &lt;country&gt;.scenario_&lt;name&gt;.chgr
    ///   csv input:  &lt;scenariosDir&gt;/scenario_&lt;name&gt;.csv
    ///
    /// scenariosDir is next to the .rgph: scenarios_&lt;basename&gt;/
    /// (".osm" suffix stripped to match the Python generator's default layout).
    /// </summary>
    public static class ScenarioPipeline
    {
        //  Filename helpers ----------------------------------------------------

        /// <summary>
        /// Path to the .chgr for a given country + scenario.
        /// </summary>
        public static string ChPathFor(string baseRgphPath, string scenarioName)
        {
            string dir = Path.GetDirectoryName(baseRgphPath) ?? ".";
            string country = Path.GetFileNameWithoutExtension(baseRgphPath);
            return Path.Combine(dir, $"{country}.scenario_{scenarioName}.chgr");
        }

        /// <summary>
        /// Path to a scenario CSV from a scenarios directory.
        /// </summary>
        public static string CsvPathFor(string scenariosDir, string scenarioName)
            => Path.Combine(scenariosDir, $"scenario_{scenarioName}.csv");

        /// <summary>
        /// The directory where scenario CSVs are expected to live, derived from
        /// the base .rgph path. Matches the Python generator's default layout
        /// (".osm" suffix stripped from the basename).
        /// </summary>
        public static string ScenariosDirFor(string baseRgphPath)
        {
            string dir = Path.GetDirectoryName(baseRgphPath) ?? ".";
            string name = Path.GetFileNameWithoutExtension(baseRgphPath);
            //  romania-latest.osm.rgph -> "romania-latest.osm" -> "romania-latest"
            if (name.EndsWith(".osm", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);

            return Path.Combine(dir, $"scenarios_{name}");
        }

        /// <summary>
        /// Lists scenario names present as CSV files in a directory.
        /// </summary>
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

            //  Skip the diagnostic summary CSV from the Python generator
            result.Remove("summary");
            result.Sort(StringComparer.OrdinalIgnoreCase);

            return result;
        }

        /// <summary>
        /// Compute the status of every scenario in <paramref name="scenarioNames"/>
        /// against the current base .rgph and CSV mtimes. One disk-stat per file -
        /// safe to call from the UI thread on app startup.
        /// </summary>
        public static Dictionary<string, ScenarioStatus> GetScenarioStatuses(
            string baseRgphPath, string scenariosDir, IEnumerable<string> scenarioNames)
        {
            var result = new Dictionary<string, ScenarioStatus>(StringComparer.OrdinalIgnoreCase);
            DateTime baseTs = File.Exists(baseRgphPath)
                ? File.GetLastWriteTimeUtc(baseRgphPath)
                : DateTime.MinValue;

            foreach (var name in scenarioNames)
            {
                string chPath = ChPathFor(baseRgphPath, name);
                string csvPath = CsvPathFor(scenariosDir, name);

                if (!File.Exists(chPath))
                {
                    result[name] = ScenarioStatus.NotPreprocessed;
                    continue;
                }

                DateTime chTs = File.GetLastWriteTimeUtc(chPath);
                DateTime csvTs = File.Exists(csvPath)
                    ? File.GetLastWriteTimeUtc(csvPath)
                    : DateTime.MinValue;

                result[name] = (chTs < baseTs || chTs < csvTs)
                    ? ScenarioStatus.Stale
                    : ScenarioStatus.Ready;
            }

            return result;
        }

        /// <summary>
        /// Returns scenario names whose .chgr is missing or stale. Convenience
        /// wrapper around GetScenarioStatuses for the "pre-tick these" UI logic.
        /// </summary>
        public static List<string> ListStaleScenarios(
            string baseRgphPath, string scenariosDir, IEnumerable<string> scenarioNames)
        {
            var statuses = GetScenarioStatuses(baseRgphPath, scenariosDir, scenarioNames);
            var stale = new List<string>();
            foreach (var (name, s) in statuses)
                if (s != ScenarioStatus.Ready) 
                    stale.Add(name);

            return stale;
        }

        //  Preprocessing ======================================================

        /// <summary>
        /// Preprocess one scenario: apply CSV, run CH, save .chgr. Progress is
        /// reported with index=1, total=1; for multi-scenario runs use the
        /// plural overload instead so indexing is correct.
        /// </summary>
        public static ScenarioPreprocessResult PreprocessScenario(
            RoadGraph baseGraph, string baseRgphPath,
            string scenariosDir, string scenarioName,
            IProgress<ScenarioProgress> progress = null,
            CancellationToken cancel = default,
            Action<string> log = null)
        {
            return PreprocessOne(
                baseGraph, baseRgphPath, scenariosDir, scenarioName,
                index1Based: 1, total: 1,
                progress: progress, cancel: cancel, log: log);
        }

        /// <summary>
        /// Preprocess multiple scenarios sequentially. Cancellation between
        /// scenarios is checked at each boundary; cancellation during a scenario
        /// is honored by CHPreprocessor and unwinds out. Returns whatever finished
        /// (successful or cancelled-mid-flight results both appear in the list,
        /// the latter with .Cancelled = true).
        /// </summary>
        public static List<ScenarioPreprocessResult> PreprocessScenarios(
            RoadGraph baseGraph, string baseRgphPath,
            string scenariosDir, IReadOnlyList<string> scenarioNames,
            IProgress<ScenarioProgress> progress = null,
            CancellationToken cancel = default,
            Action<string> log = null)
        {
            var results = new List<ScenarioPreprocessResult>();
            int total = scenarioNames.Count;

            for (int i = 0; i < scenarioNames.Count; i++)
            {
                if (cancel.IsCancellationRequested) 
                    break;

                var r = PreprocessOne(
                    baseGraph, baseRgphPath, scenariosDir, scenarioNames[i],
                    index1Based: i + 1, total: total,
                    progress: progress, cancel: cancel, log: log);

                results.Add(r);

                if (r.Cancelled) 
                    break;
            }
            return results;
        }

        /// <summary>
        /// Internal worker. Catches OperationCanceledException and bakes it
        /// into the result rather than rethrowing - callers see a uniform
        /// list of results and can decide what to do based on .Cancelled.
        /// </summary>
        private static ScenarioPreprocessResult PreprocessOne(
            RoadGraph baseGraph, string baseRgphPath,
            string scenariosDir, string scenarioName,
            int index1Based, int total,
            IProgress<ScenarioProgress> progress,
            CancellationToken cancel,
            Action<string> log)
        {
            log ??= (s => Debug.WriteLine(s));

            string csvPath = CsvPathFor(scenariosDir, scenarioName);
            string chPath = ChPathFor(baseRgphPath, scenarioName);

            var result = new ScenarioPreprocessResult
            {
                ScenarioName = scenarioName,
                ChPath = chPath,
            };

            if (!File.Exists(csvPath))
            {
                result.Error = new FileNotFoundException(
                    $"Scenario CSV not found: {csvPath}", csvPath);

                return result;
            }

            try
            {
                //  1. Apply scenario multipliers 
                progress?.Report(new ScenarioProgress(
                    scenarioName, index1Based, total, "Applying CSV multipliers...", 0.0));

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

                cancel.ThrowIfCancellationRequested();

                //  2. CH preprocessing 
                progress?.Report(new ScenarioProgress(
                    scenarioName, index1Based, total, "Building contraction hierarchy...", 0.0));

                log($"[scenario:{scenarioName}] CH preprocessing (this takes ~20 min)...");

                //  Inner-progress adapter: rebroadcast the CH fraction with the
                //  outer scenario indexing intact.
                var chProgress = progress == null
                    ? null
                    : new Progress<double>(f => progress.Report(new ScenarioProgress(
                        scenarioName, index1Based, total,
                        "Building contraction hierarchy...", f)));

                var swCh = Stopwatch.StartNew();
                var ch = new CHPreprocessor().Preprocess(applied.Graph, chProgress, cancel);

                swCh.Stop();

                result.PreprocessTime = swCh.Elapsed;

                log($"[scenario:{scenarioName}] CH built in {swCh.Elapsed.TotalMinutes:F1} min");

                cancel.ThrowIfCancellationRequested();

                //  3. Save .chgr 
                progress?.Report(new ScenarioProgress(
                    scenarioName, index1Based, total, "Saving .chgr...", 1.0));

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
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                log($"[scenario:{scenarioName}] CANCELLED");
                //  Best-effort cleanup of any partially-written .chgr.
                //  In practice the cancel point is before CHGraphSerializer.Save
                //  so the file won't exist; this is a safety net only.
                try 
                { 
                    if (File.Exists(chPath)) File.Delete(chPath); 
                } catch 
                { 
                }

                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex;

                log($"[scenario:{scenarioName}] ERROR: {ex.Message}");
                
                return result;
            }
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