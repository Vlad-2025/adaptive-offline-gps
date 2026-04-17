using OsmSharp.Streams;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using OsmSharp;

namespace adaptive_offline_gps.RoutingEngine.RoadGraphBuilding
{
    public class RoadGraphBuilder
    {
        private static readonly Dictionary<string, float> SpeedMap = new()
        {
            ["motorway"] = 130,
            ["motorway_link"] = 80,
            ["trunk"] = 110,
            ["trunk_link"] = 70,
            ["primary"] = 90,
            ["primary_link"] = 60,
            ["secondary"] = 70,
            ["secondary_link"] = 50,
            ["tertiary"] = 50,
            ["residential"] = 30,
            ["living_street"] = 10,
            ["service"] = 20,
        };

        public float[] AllNodeLat { get; private set; }
        public float[] AllNodeLon { get; private set; }
        public int[] AllNodeNearestJunction { get; private set; }

        public RoadGraph Build(string filePath)
        {
            //  Pass 1: scan ways, count node references to find junctions
            var nodeRefCount = new Dictionary<long, int>();
            var wayEndpoints = new HashSet<long>();

            using var stream = File.OpenRead(filePath);
            var source = new PBFOsmStreamSource(stream);

            foreach (var elem in source)
            {
                if (elem.Type != OsmGeoType.Way) continue;
                var way = (Way)elem;
                if (way.Tags == null || !way.Tags.ContainsKey("highway")) continue;
                if (!SpeedMap.ContainsKey(way.Tags["highway"])) continue;

                for (int i = 0; i < way.Nodes.Length; i++)
                {
                    long nid = way.Nodes[i];
                    if (nodeRefCount.ContainsKey(nid))
                        nodeRefCount[nid]++;
                    else
                        nodeRefCount[nid] = 1;
                }

                if (way.Nodes.Length > 0)
                {
                    wayEndpoints.Add(way.Nodes[0]);
                    wayEndpoints.Add(way.Nodes[^1]);
                }
            }

            var junctionOsmIds = new HashSet<long>();
            foreach (var (nid, count) in nodeRefCount)
            {
                if (count > 1 || wayEndpoints.Contains(nid))
                    junctionOsmIds.Add(nid);
            }

            //  Keep all road node IDs before freeing nodeRefCount
            var allRoadNodeIds = new HashSet<long>(nodeRefCount.Keys);

            int totalRoadNodes = nodeRefCount.Count;
            Debug.WriteLine($"RoadGraph: {totalRoadNodes} road nodes, {junctionOsmIds.Count} junctions " +
                            $"({100.0 * junctionOsmIds.Count / totalRoadNodes:F1}% are junctions)");

            nodeRefCount = null;
            wayEndpoints = null;

            //  Pass 2: read coordinates
            //  Junction data stays in memory
            //  ALL node coordinates go to DiskNodeStore (replaces allNodeCoords dictionary).
            var junctionIndex = new Dictionary<long, int>();
            var junctionCoords = new List<(float lat, float lon)>();

            var allNodeCompactIndex = new Dictionary<long, int>();
            var allLat = new List<float>();
            var allLon = new List<float>();

            using var nodeStore = new DiskNodeStore();

            stream.Position = 0;
            source = new PBFOsmStreamSource(stream);

            foreach (var elem in source)
            {
                if (elem.Type != OsmGeoType.Node) continue;
                var node = (Node)elem;
                if (!node.Id.HasValue || !node.Latitude.HasValue || !node.Longitude.HasValue)
                    continue;

                long id = node.Id.Value;
                if (!allRoadNodeIds.Contains(id))
                    continue;

                float lat = (float)node.Latitude.Value;
                float lon = (float)node.Longitude.Value;

                //  Coordinates to disk (replaces allNodeCoords dictionary)
                nodeStore.Add(id, lat, lon);

                //  Compact index for NodeGridIndex (just ints + floats in lists)
                allNodeCompactIndex[id] = allLat.Count;
                allLat.Add(lat);
                allLon.Add(lon);

                //  Junction index for routing graph
                if (junctionOsmIds.Contains(id))
                {
                    junctionIndex[id] = junctionCoords.Count;
                    junctionCoords.Add((lat, lon));
                }
            }

            allRoadNodeIds = null;

            //  Sort the disk store for binary search lookups
            nodeStore.Build();

            Debug.WriteLine($"RoadGraph: {allLat.Count} total road nodes, {junctionCoords.Count} junction nodes");

            //  Pass 3: build edges between junctions with pillar geometry
            var adjList = new List<(int to, float weight, float[] geomLat, float[] geomLon)>[junctionCoords.Count];
            for (int i = 0; i < adjList.Length; i++)
                adjList[i] = new List<(int, float, float[], float[])>();

            var allNodeNearestJunc = new int[allLat.Count];
            foreach (var (osmId, jIdx) in junctionIndex)
            {
                if (allNodeCompactIndex.TryGetValue(osmId, out int allIdx))
                    allNodeNearestJunc[allIdx] = jIdx;
            }

            stream.Position = 0;
            source = new PBFOsmStreamSource(stream);

            foreach (var elem in source)
            {
                if (elem.Type != OsmGeoType.Way) continue;
                var way = (Way)elem;
                if (way.Tags == null || !SpeedMap.TryGetValue(way.Tags.GetValue("highway") ?? "", out float speed))
                    continue;

                bool isOneWay = way.Tags.GetValue("oneway") == "yes";

                var segmentOsmIds = new List<long>();

                foreach (long nid in way.Nodes)
                {
                    segmentOsmIds.Add(nid);

                    if (junctionOsmIds.Contains(nid) && segmentOsmIds.Count >= 2)
                    {
                        EmitSegment(segmentOsmIds, speed, isOneWay,
                            nodeStore, junctionIndex, junctionCoords,
                            allNodeCompactIndex, allNodeNearestJunc,
                            adjList);

                        segmentOsmIds.Clear();
                        segmentOsmIds.Add(nid);
                    }
                }

                if (segmentOsmIds.Count >= 2)
                {
                    EmitSegment(segmentOsmIds, speed, isOneWay,
                        nodeStore, junctionIndex, junctionCoords,
                        allNodeCompactIndex, allNodeNearestJunc,
                        adjList);
                }
            }

            AllNodeLat = allLat.ToArray();
            AllNodeLon = allLon.ToArray();
            AllNodeNearestJunction = allNodeNearestJunc;

            return CompressToCSR(junctionCoords, adjList);
        }

        private void EmitSegment(
            List<long> segmentOsmIds,
            float speed,
            bool isOneWay,
            DiskNodeStore nodeStore,
            Dictionary<long, int> junctionIndex,
            List<(float lat, float lon)> junctionCoords,
            Dictionary<long, int> allNodeCompactIndex,
            int[] allNodeNearestJunc,
            List<(int to, float weight, float[] geomLat, float[] geomLon)>[] adjList)
        {
            long firstOsm = segmentOsmIds[0];
            long lastOsm = segmentOsmIds[^1];

            if (!junctionIndex.TryGetValue(firstOsm, out int juncA)) return;
            if (!junctionIndex.TryGetValue(lastOsm, out int juncB)) return;
            if (juncA == juncB) return;

            float totalDist = 0;
            var pillarLat = new List<float>();
            var pillarLon = new List<float>();

            float prevLat = 0, prevLon = 0;
            bool hasPrev = false;

            for (int i = 0; i < segmentOsmIds.Count; i++)
            {
                long nid = segmentOsmIds[i];

                if (!nodeStore.TryGet(nid, out float lat, out float lon))
                    continue;

                if (hasPrev)
                    totalDist += Haversine((prevLat, prevLon), (lat, lon));

                prevLat = lat;
                prevLon = lon;
                hasPrev = true;

                if (i > 0 && i < segmentOsmIds.Count - 1)
                {
                    pillarLat.Add(lat);
                    pillarLon.Add(lon);

                    if (allNodeCompactIndex.TryGetValue(nid, out int allIdx))
                        allNodeNearestJunc[allIdx] = (i < segmentOsmIds.Count / 2) ? juncA : juncB;
                }
            }

            float cost = totalDist / (speed / 3.6f);

            var gLat = pillarLat.ToArray();
            var gLon = pillarLon.ToArray();

            adjList[juncA].Add((juncB, cost, gLat, gLon));

            if (!isOneWay)
            {
                var revLat = gLat.Reverse().ToArray();
                var revLon = gLon.Reverse().ToArray();
                adjList[juncB].Add((juncA, cost, revLat, revLon));
            }
        }

        private RoadGraph CompressToCSR(
            List<(float lat, float lon)> coords,
            List<(int to, float weight, float[] geomLat, float[] geomLon)>[] adj)
        {
            int n = coords.Count;
            int totalEdges = adj.Sum(x => x.Count);

            var g = new RoadGraph
            {
                NodeLat = new float[n],
                NodeLon = new float[n],
                EdgeHead = new int[n + 1],
                EdgeTarget = new int[totalEdges],
                EdgeWeight = new float[totalEdges],
                EdgeGeomLat = new float[totalEdges][],
                EdgeGeomLon = new float[totalEdges][]
            };

            for (int i = 0; i < n; i++)
            {
                g.NodeLat[i] = coords[i].lat;
                g.NodeLon[i] = coords[i].lon;
            }

            int cursor = 0;

            for (int i = 0; i < n; i++)
            {
                g.EdgeHead[i] = cursor;

                foreach (var (to, w, gLat, gLon) in adj[i])
                {
                    g.EdgeTarget[cursor] = to;
                    g.EdgeWeight[cursor] = w;
                    g.EdgeGeomLat[cursor] = gLat;
                    g.EdgeGeomLon[cursor] = gLon;
                    cursor++;
                }
            }

            g.EdgeHead[n] = cursor;

            return g;
        }

        public static float Haversine((float lat, float lon) a, (float lat, float lon) b)
        {
            const float R = 6_378_100;

            float dLat = (b.lat - a.lat) * MathF.PI / 180f;
            float dLon = (b.lon - a.lon) * MathF.PI / 180f;

            float sa = MathF.Sin(dLat / 2);
            float so = MathF.Sin(dLon / 2);

            float x =
                sa * sa
                + MathF.Cos(a.lat * MathF.PI / 180f)
                * MathF.Cos(b.lat * MathF.PI / 180f)
                * so * so;

            return R * 2f * MathF.Atan2(MathF.Sqrt(x), MathF.Sqrt(1 - x));
        }
    }

    /// <summary>
    /// Disk-backed node coordinate store. Writes to a temp file during the node pass,
    /// then sorts and memory-maps for binary search lookups during the way pass.
    /// 
    /// Memory usage: ~8 bytes per node (just the sorted ID index).
    /// Coordinates stay on disk, paged in by the OS as needed.
    /// 
    /// For 50M nodes: ~400MB RAM (ID index) + ~800MB disk file.
    /// Compare to Dictionary: ~2-4GB RAM.
    /// </summary>
    public class DiskNodeStore : IDisposable
    {
        private const int RecordSize = 16; // 8 (long id) + 4 (float lat) + 4 (float lon)

        private readonly string _filePath;
        private BinaryWriter _writer;
        private long _count;

        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _accessor;
        private long[] _sortedIds;

        public DiskNodeStore(string tempDir = null)
        {
            _filePath = Path.Combine(
                tempDir ?? Path.GetTempPath(),
                $"nodes_{Guid.NewGuid():N}.bin");
            _writer = new BinaryWriter(File.Create(_filePath));
            _count = 0;
        }

        public void Add(long osmId, float lat, float lon)
        {
            _writer.Write(osmId);
            _writer.Write(lat);
            _writer.Write(lon);
            _count++;
        }

        public void Build()
        {
            _writer.Flush();
            _writer.Dispose();
            _writer = null;

            Debug.WriteLine($"DiskNodeStore: sorting {_count} nodes " +
                            $"({_count * RecordSize / 1024 / 1024}MB on disk)");

            _mmf = MemoryMappedFile.CreateFromFile(
                _filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            _accessor = _mmf.CreateViewAccessor(
                0, _count * RecordSize, MemoryMappedFileAccess.Read);

            _sortedIds = new long[_count];
            var positions = new int[_count];

            for (int i = 0; i < _count; i++)
            {
                _sortedIds[i] = _accessor.ReadInt64((long)i * RecordSize);
                positions[i] = i;
            }

            Array.Sort(_sortedIds, positions);

            //  Rewrite file in sorted order
            var sortedPath = _filePath + ".sorted";
            using (var bw = new BinaryWriter(File.Create(sortedPath)))
            {
                for (int i = 0; i < _count; i++)
                {
                    long offset = (long)positions[i] * RecordSize;
                    long id = _accessor.ReadInt64(offset);
                    float lat = _accessor.ReadSingle(offset + 8);
                    float lon = _accessor.ReadSingle(offset + 12);
                    bw.Write(id);
                    bw.Write(lat);
                    bw.Write(lon);
                }
            }

            _accessor.Dispose();
            _mmf.Dispose();
            File.Delete(_filePath);
            File.Move(sortedPath, _filePath);

            _mmf = MemoryMappedFile.CreateFromFile(
                _filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            
            _accessor = _mmf.CreateViewAccessor(
                0, _count * RecordSize, MemoryMappedFileAccess.Read);

            //  Re-read sorted IDs for binary search
            for (int i = 0; i < _count; i++)
                _sortedIds[i] = _accessor.ReadInt64((long)i * RecordSize);

            Debug.WriteLine("DiskNodeStore: ready");
        }

        public bool TryGet(long osmId, out float lat, out float lon)
        {
            int idx = Array.BinarySearch(_sortedIds, osmId);
            if (idx < 0)
            {
                lat = lon = 0;
                return false;
            }

            long offset = (long)idx * RecordSize;
            lat = _accessor.ReadSingle(offset + 8);
            lon = _accessor.ReadSingle(offset + 12);
            return true;
        }

        public void Dispose()
        {
            _accessor?.Dispose();
            _mmf?.Dispose();
            _writer?.Dispose();
            try { File.Delete(_filePath); } catch { }
            try { File.Delete(_filePath + ".sorted"); } catch { }
        }
    }
}