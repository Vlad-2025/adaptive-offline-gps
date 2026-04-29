using System;
using System.Collections.Generic;
using System.IO;

namespace adaptive_offline_gps.RoutingEngine.RoadGraphBuilding
{
    /// <summary>
    /// Binary serializer for CHGraph. Version bumped to 2 — old .chgr caches
    /// are invalidated automatically on load and will be rebuilt.
    /// </summary>
    public static class CHGraphSerializer
    {
        private const uint Magic = 0x43484752; // "CHGR"
        private const int Version = 2;

        public static void Save(CHGraph ch, string path)
        {
            int n = ch.Level.Length;

            using var bw = new BinaryWriter(File.Create(path));
            bw.Write(Magic);
            bw.Write(Version);
            bw.Write(n);

            var levelBytes = new byte[n * 4];
            Buffer.BlockCopy(ch.Level, 0, levelBytes, 0, levelBytes.Length);
            bw.Write(levelBytes);

            WriteAdjacency(bw, ch.ForwardUp, n);
            WriteAdjacency(bw, ch.BackwardUp, n);
        }

        public static CHGraph Load(string path, RoadGraph baseGraph)
        {
            using var br = new BinaryReader(File.OpenRead(path));

            uint magic = br.ReadUInt32();
            if (magic != Magic)
                throw new InvalidDataException($"Not a CHGraph file (got 0x{magic:X8})");

            int version = br.ReadInt32();
            if (version != Version)
                throw new InvalidDataException($"CHGraph version mismatch (expected {Version}, got {version})");

            int n = br.ReadInt32();
            if (n != baseGraph.NodeCount)
                throw new InvalidDataException(
                    $"Node count mismatch: CH has {n}, RoadGraph has {baseGraph.NodeCount}. Rebuild the CH cache.");

            var levelBytes = br.ReadBytes(n * 4);
            var level = new int[n];
            Buffer.BlockCopy(levelBytes, 0, level, 0, levelBytes.Length);

            var forwardUp = ReadAdjacency(br, n);
            var backwardUp = ReadAdjacency(br, n);

            return new CHGraph
            {
                BaseGraph = baseGraph,
                Level = level,
                ForwardUp = forwardUp,
                BackwardUp = backwardUp
            };
        }

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
                catch { }
            }
            chGraph = null;
            return false;
        }

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