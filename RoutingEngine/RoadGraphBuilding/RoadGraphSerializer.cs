using System;
using System.IO;

namespace adaptive_offline_gps.RoutingEngine.RoadGraphBuilding
{
    public class RoadGraphSerializer
    {
        private const uint Magic = 0x52475048; // RGPH
        private const int Version = 2;         // v2: includes edge geometry

        public static void Save(RoadGraph g, string path)
        {
            using var bw = new BinaryWriter(File.Create(path));

            bw.Write(Magic);
            bw.Write(Version);
            bw.Write(g.NodeCount);
            bw.Write(g.EdgeCount);

            //  Node coordinates
            WriteFloatArray(bw, g.NodeLat);
            WriteFloatArray(bw, g.NodeLon);

            //  CSR structure
            WriteIntArray(bw, g.EdgeHead);
            WriteIntArray(bw, g.EdgeTarget);
            WriteFloatArray(bw, g.EdgeWeight);

            //  Edge geometry (pillar nodes)
            for (int e = 0; e < g.EdgeCount; e++)
            {
                var lat = g.EdgeGeomLat[e];
                var lon = g.EdgeGeomLon[e];
                int count = lat?.Length ?? 0;

                bw.Write(count);

                if (count > 0)
                {
                    WriteFloatArray(bw, lat);
                    WriteFloatArray(bw, lon);
                }
            }
        }

        public static RoadGraph Load(string path)
        {
            using var br = new BinaryReader(File.OpenRead(path));

            uint magic = br.ReadUInt32();
            if (magic != Magic)
                throw new Exception("Not a RoadGraph file!");

            int version = br.ReadInt32();
            if (version != Version)
                throw new Exception($"Version mismatch (expected {Version}, got {version}). Rebuild the graph cache.");

            int nodeCount = br.ReadInt32();
            int edgeCount = br.ReadInt32();

            var g = new RoadGraph
            {
                NodeLat = ReadFloatArray(br, nodeCount),
                NodeLon = ReadFloatArray(br, nodeCount),
                EdgeHead = ReadIntArray(br, nodeCount + 1),
                EdgeTarget = ReadIntArray(br, edgeCount),
                EdgeWeight = ReadFloatArray(br, edgeCount),
                EdgeGeomLat = new float[edgeCount][],
                EdgeGeomLon = new float[edgeCount][]
            };

            //  Edge geometry
            for (int e = 0; e < edgeCount; e++)
            {
                int count = br.ReadInt32();

                if (count > 0)
                {
                    g.EdgeGeomLat[e] = ReadFloatArray(br, count);
                    g.EdgeGeomLon[e] = ReadFloatArray(br, count);
                }
                else
                {
                    g.EdgeGeomLat[e] = Array.Empty<float>();
                    g.EdgeGeomLon[e] = Array.Empty<float>();
                }
            }

            return g;
        }

        public static bool TryLoadCached(string pbfPath, out RoadGraph g)
        {
            var cachePath = Path.ChangeExtension(pbfPath, ".rgph");

            if (File.Exists(cachePath) && File.GetLastWriteTimeUtc(cachePath) > File.GetLastWriteTimeUtc(pbfPath))
            {
                try
                {
                    g = Load(cachePath);
                    return true;
                }
                catch
                {
                    // Version mismatch or corrupt — rebuild
                }
            }
            g = null;
            return false;
        }

        private static void WriteFloatArray(BinaryWriter bw, float[] arr)
        {
            var bytes = new byte[arr.Length * 4];
            Buffer.BlockCopy(arr, 0, bytes, 0, bytes.Length);
            bw.Write(bytes);
        }

        private static void WriteIntArray(BinaryWriter bw, int[] arr)
        {
            var bytes = new byte[arr.Length * 4];
            Buffer.BlockCopy(arr, 0, bytes, 0, bytes.Length);
            bw.Write(bytes);
        }

        private static float[] ReadFloatArray(BinaryReader br, int count)
        {
            var bytes = br.ReadBytes(count * 4);
            var arr = new float[count];
            Buffer.BlockCopy(bytes, 0, arr, 0, bytes.Length);
            return arr;
        }

        private static int[] ReadIntArray(BinaryReader br, int count)
        {
            var bytes = br.ReadBytes(count * 4);
            var arr = new int[count];
            Buffer.BlockCopy(bytes, 0, arr, 0, bytes.Length);
            return arr;
        }
    }
}