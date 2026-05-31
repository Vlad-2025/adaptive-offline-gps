using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace adaptive_offline_gps.RoutingEngine.RoadGraphBuilding
{
    /// <summary>
    /// Persists a CHGraph to disk. One file format for both cases:
    ///
    ///   - Base graph CH:     scenario name = "" (empty string)
    ///                         filename = &lt;country&gt;.chgr
    ///   - Scenario CH:       scenario name = "winter" (etc.)
    ///                         filename = &lt;country&gt;.scenario_&lt;name&gt;.chgr
    ///
    /// BaseGraph is not serialized here, the .chgr references the base .rgph at load time (passed in by the caller)
    /// One .rgph, many .chgr files
    ///
    /// File format (.chgr):
    ///   magic        u32   'CHGR' (0x43484752)
    ///   version      i32   1
    ///   scenarioLen  i32   length of UTF-8 scenario name in bytes (0 for base)
    ///   scenarioName bytes UTF-8, no terminator
    ///   nodeCount    i32
    ///   level        i32[nodeCount]
    ///   
    ///   for each node u in 0..nodeCount-1:
    ///       fwdCount      i32
    ///       fwd[]         (target i32, weight f32, shortcutMid i32) * fwdCount
    ///       
    ///   for each node u in 0..nodeCount-1:
    ///       bwdCount      i32
    ///       bwd[]         (target i32, weight f32, shortcutMid i32) * bwdCount
    ///       
    /// </summary>
    public static class CHGraphSerializer
    {
        private const uint Magic = 0x43484752;   // 'CHGR'
        private const int Version = 1;

        //  Save / Load =========================================================

        /// <summary>
        /// Save a CH. scenarioName is "" for the base-graph CH, or the scenario
        /// name (e.g. "winter") for a scenario CH
        /// </summary>
        public static void Save(CHGraph ch, string path, string scenarioName = "")
        {
            using var bw = new BinaryWriter(File.Create(path));

            bw.Write(Magic);
            bw.Write(Version);

            var nameBytes = Encoding.UTF8.GetBytes(scenarioName ?? "");
            bw.Write(nameBytes.Length);
            bw.Write(nameBytes);

            int n = ch.Level.Length;
            bw.Write(n);

            //  Level array
            for (int i = 0; i < n; i++)
                bw.Write(ch.Level[i]);

            //  Forward up-edges
            for (int u = 0; u < n; u++)
            {
                var list = ch.ForwardUp[u];
                bw.Write(list.Count);
                for (int i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    bw.Write(e.Target);
                    bw.Write(e.Weight);
                    bw.Write(e.ShortcutMid);
                }
            }

            //  Backward up-edges
            for (int u = 0; u < n; u++)
            {
                var list = ch.BackwardUp[u];
                bw.Write(list.Count);
                for (int i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    bw.Write(e.Target);
                    bw.Write(e.Weight);
                    bw.Write(e.ShortcutMid);
                }
            }
        }

        /// <summary>
        /// Load a .chgr and attach it to the given base graph.
        /// If <paramref name="expectedScenario"/> is non-null, throws on mismatch
        /// (use "" to require a base CH, or "winter" for a specific scenario,
        /// or null to skip the check).
        /// </summary>
        public static CHGraph Load(string path, RoadGraph baseGraph, string expectedScenario = null)
        {
            using var br = new BinaryReader(File.OpenRead(path));

            uint magic = br.ReadUInt32();
            if (magic != Magic)
                throw new InvalidDataException($"Not a CHGraph file: {path}");

            int version = br.ReadInt32();
            if (version != Version)
                throw new InvalidDataException(
                    $"CHGraph version mismatch (expected {Version}, got {version}): {path}");

            int nameLen = br.ReadInt32();
            var nameBytes = br.ReadBytes(nameLen);
            string scenarioName = Encoding.UTF8.GetString(nameBytes);

            if (expectedScenario != null
                && !string.Equals(scenarioName, expectedScenario, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"CHGraph scenario mismatch: file says '{scenarioName}', " +
                    $"expected '{expectedScenario}' ({path})");
            }

            int n = br.ReadInt32();
            if (n != baseGraph.NodeCount)
                throw new InvalidDataException(
                    $"CHGraph node count {n} does not match base graph {baseGraph.NodeCount}. " +
                    $"The base .rgph was rebuilt after this .chgr - re-preprocess.");

            var level = new int[n];
            for (int i = 0; i < n; i++)
                level[i] = br.ReadInt32();

            var forwardUp = new List<CHEdge>[n];
            for (int u = 0; u < n; u++)
            {
                int count = br.ReadInt32();
                var list = new List<CHEdge>(count);
                for (int i = 0; i < count; i++)
                {
                    int target = br.ReadInt32();
                    float weight = br.ReadSingle();
                    int shortcutMid = br.ReadInt32();
                    list.Add(new CHEdge(target, weight, shortcutMid));
                }
                forwardUp[u] = list;
            }

            var backwardUp = new List<CHEdge>[n];
            for (int u = 0; u < n; u++)
            {
                int count = br.ReadInt32();
                var list = new List<CHEdge>(count);
                for (int i = 0; i < count; i++)
                {
                    int target = br.ReadInt32();
                    float weight = br.ReadSingle();
                    int shortcutMid = br.ReadInt32();
                    list.Add(new CHEdge(target, weight, shortcutMid));
                }
                backwardUp[u] = list;
            }

            return new CHGraph
            {
                BaseGraph = baseGraph,
                Level = level,
                ForwardUp = forwardUp,
                BackwardUp = backwardUp,
            };
        }

        //  Base-graph cache helper (matches RoadGraphSerializer.TryLoadCached) ==

        /// <summary>
        /// Try to load a cached base CH next to the .pbf. The cache is considered
        /// fresh iff the .chgr file exists and is newer than both the .pbf and
        /// the .rgph file. Mirrors RoadGraphSerializer.TryLoadCached's pattern.
        /// </summary>
        public static bool TryLoadCached(string pbfPath, RoadGraph baseGraph, out CHGraph chGraph)
        {
            var chPath = Path.ChangeExtension(pbfPath, ".chgr");
            var rgphPath = Path.ChangeExtension(pbfPath, ".rgph");

            if (File.Exists(chPath))
            {
                DateTime chTs = File.GetLastWriteTimeUtc(chPath);
                bool freshVsPbf = !File.Exists(pbfPath) || chTs > File.GetLastWriteTimeUtc(pbfPath);
                bool freshVsRgph = !File.Exists(rgphPath) || chTs > File.GetLastWriteTimeUtc(rgphPath);

                if (freshVsPbf && freshVsRgph)
                {
                    try
                    {
                        //  Base CH: scenarioName in the file should be "".
                        chGraph = Load(chPath, baseGraph, expectedScenario: "");
                        return true;
                    }
                    catch
                    {
                        //  Version mismatch, scenario mismatch, or node-count mismatch - rebuild.
                    }
                }
            }
            chGraph = null;
            return false;
        }

        /// <summary>
        /// Read just the scenario name from a .chgr without loading the whole file.
        /// Returns "" for base CHs, the scenario name otherwise, or null on error.
        /// </summary>
        public static string ReadScenarioName(string path)
        {
            try
            {
                using var br = new BinaryReader(File.OpenRead(path));

                if (br.ReadUInt32() != Magic) 
                    return null;

                if (br.ReadInt32() != Version) 
                    return null;

                int nameLen = br.ReadInt32();

                return Encoding.UTF8.GetString(br.ReadBytes(nameLen));
            }
            catch
            {
                return null;
            }
        }
    }
}