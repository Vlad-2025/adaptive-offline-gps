using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace adaptive_offline_gps.RoutingEngine.RoadGraphBuilding
{
    public class RoadGraphSerializer
    {
        private const uint Magic = 0x52475048; // RGPH
        private const int Version = 1;

        public static void Save(RoadGraph g, string path)
        {
            using var bw = new BinaryWriter(File.OpenWrite(path));

            bw.Write(Magic);
            bw.Write(Version);
            bw.Write(g.NodeCount);
            bw.Write(g.EdgeCount);

            //  Bulk write arrays as raw bytes
            WriteFloatArray(bw, g.NodeLat);
            WriteFloatArray(bw, g.NodeLon);
            WriteIntArray(bw, g.EdgeHead);
            WriteIntArray(bw, g.EdgeTarget);
            WriteFloatArray(bw, g.EdgeWeight);
        }

        public static RoadGraph Load(string path)
        {
            using var br = new BinaryReader(File.OpenRead(path));

            if (br.ReadUInt32() != Magic)
                throw new Exception("Not a RoadGraph file!");

            if (br.ReadInt32() != Version)
                throw new Exception("Version mismatch");

            int nodeCount = br.ReadInt32();
            int edgeCount = br.ReadInt32();

            return new RoadGraph
            {
                NodeLat = ReadFloatArray(br, nodeCount),
                NodeLon = ReadFloatArray(br, nodeCount),
                EdgeHead = ReadIntArray(br, nodeCount + 1),
                EdgeTarget = ReadIntArray(br, edgeCount),
                EdgeWeight = ReadFloatArray(br, edgeCount),
            };
        }

        public static bool TryLoadCached(string pbfPath, out RoadGraph g)
        {
            var cachePath = Path.ChangeExtension(pbfPath, ".rgph");

            if (File.Exists(cachePath) && File.GetLastWriteTimeUtc(cachePath) > File.GetLastWriteTimeUtc(pbfPath))
            {
                g = Load(cachePath);
                return true;
            }
            g = null;
            return false;
        }

        // Unsafe bulk copy - allocates once and memcpy's
        private static unsafe void WriteFloatArray(BinaryWriter bw, float[] arr)
        {
            var bytes = new byte[arr.Length * 4];
            Buffer.BlockCopy(arr, 0, bytes, 0, bytes.Length);
            bw.Write(bytes);
        }
        private static unsafe void WriteIntArray(BinaryWriter bw, int[] arr)
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
