using System;
using System.Collections.Generic;
using System.IO;

namespace adaptive_offline_gps.RoutingEngine.RoadGraphBuilding
{
    /// <summary>
    /// Binary serializer for CHGraph.
    /// 
    /// File format (.chgr):
    ///   [4 bytes] magic  0x43484752 ("CHGR")
    ///   [4 bytes] version
    ///   [4 bytes] nodeCount
    ///   [nodeCount × 4 bytes] Level array
    ///   
    ///   -- UpAdj --
    ///   For each node:
    ///     [4 bytes] edge count for this node
    ///     For each edge:
    ///       [4 bytes] target
    ///       [4 bytes] weight (float)
    ///       [4 bytes] shortcutMid
    ///   
    ///   -- DownAdj --
    ///   Same format as UpAdj
    ///   
    /// The RoadGraph (BaseGraph) is NOT included — it must be loaded
    /// separately and passed in to Load().
    /// </summary>
    public static class CHGraphSerializer
    {
        private const uint Magic = 0x43484752; // "CHGR"
        private const int Version = 1;

        public static void Save(CHGraph ch, string path)
        {
            int n = ch.Level.Length;

            using var bw = new BinaryWriter(File.Create(path));

            bw.Write(Magic);
            bw.Write(Version);
            bw.Write(n);

            // Level array
            var levelBytes = new byte[n * 4];
            Buffer.BlockCopy(ch.Level, 0, levelBytes, 0, levelBytes.Length);
            bw.Write(levelBytes);

            // UpAdj
            WriteAdjacency(bw, ch.UpAdj, n);

            // DownAdj
            WriteAdjacency(bw, ch.DownAdj, n);
        }

        /// <summary>
        /// Load a CHGraph from disk. Requires the base RoadGraph to be loaded first.
        /// </summary>
        public static CHGraph Load(string path, RoadGraph baseGraph)
        {
            using var br = new BinaryReader(File.OpenRead(path));

            uint magic = br.ReadUInt32();
            if (magic != Magic)
                throw new InvalidDataException(
                    $"Not a CHGraph file (expected 0x{Magic:X8}, got 0x{magic:X8})");

            int version = br.ReadInt32();
            if (version != Version)
                throw new InvalidDataException(
                    $"CHGraph version mismatch (expected {Version}, got {version})");

            int n = br.ReadInt32();

            if (n != baseGraph.NodeCount)
                throw new InvalidDataException(
                    $"Node count mismatch: CH has {n}, RoadGraph has {baseGraph.NodeCount}. " +
                    "Rebuild the CH cache.");

            // Level array
            var levelBytes = br.ReadBytes(n * 4);
            var level = new int[n];
            Buffer.BlockCopy(levelBytes, 0, level, 0, levelBytes.Length);

            // UpAdj
            var upAdj = ReadAdjacency(br, n);

            // DownAdj
            var downAdj = ReadAdjacency(br, n);

            return new CHGraph
            {
                BaseGraph = baseGraph,
                Level = level,
                UpAdj = upAdj,
                DownAdj = downAdj
            };
        }

        /// <summary>
        /// Try to load a cached CH file. Returns false if no cache exists
        /// or if it's older than the road graph cache.
        /// </summary>
        public static bool TryLoadCached(string pbfPath, RoadGraph baseGraph, out CHGraph chGraph)
        {
            var chPath = Path.ChangeExtension(pbfPath, ".chgr");
            var rgphPath = Path.ChangeExtension(pbfPath, ".rgph");

            if (File.Exists(chPath)
                && File.Exists(rgphPath)
                && File.GetLastWriteTimeUtc(chPath) > File.GetLastWriteTimeUtc(rgphPath))
            {
                try
                {
                    chGraph = Load(chPath, baseGraph);
                    return true;
                }
                catch
                {
                    // Corrupted or version mismatch — rebuild
                }
            }

            chGraph = null;
            return false;
        }

        // ── Helpers ──

        private static void WriteAdjacency(BinaryWriter bw, List<CHEdge>[] adj, int nodeCount)
        {
            for (int i = 0; i < nodeCount; i++)
            {
                var edges = adj[i];
                bw.Write(edges.Count);

                foreach (var e in edges)
                {
                    bw.Write(e.Target);
                    bw.Write(e.Weight);
                    bw.Write(e.ShortcutMid);
                }
            }
        }

        private static List<CHEdge>[] ReadAdjacency(BinaryReader br, int nodeCount)
        {
            var adj = new List<CHEdge>[nodeCount];

            for (int i = 0; i < nodeCount; i++)
            {
                int count = br.ReadInt32();
                adj[i] = new List<CHEdge>(count);

                for (int j = 0; j < count; j++)
                {
                    int target = br.ReadInt32();
                    float weight = br.ReadSingle();
                    int mid = br.ReadInt32();

                    adj[i].Add(new CHEdge(target, weight, mid));
                }
            }

            return adj;
        }
    }
}