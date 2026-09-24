using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using SeedLab.Contracts.Dump;
using SeedLab.Data;
using SeedLab.Locations;
using SeedLab.Search.Locations;
using SeedLab.WorldGen;
using SeedLab.WorldGen.Unity;

namespace SeedLab.LocationOracle
{
    /// <summary>
    /// One world, reduced to five SHA-256 digests - one per layer of what the generator computes - so
    /// that "did this change move anything?" is a comparison of short strings over many seeds rather
    /// than a judgement.
    ///
    /// <list type="bullet">
    /// <item><b>L1</b>, the lattice: <c>GetBiome</c> and the <c>GetBaseHeight</c> float bits on a
    /// 1024 x 1024 lattice at 24 m (x = (j - 512) * 24 + 12), on a handle whose pre-generation is
    /// deferred and must still be pending afterwards - biomes and base heights never read rivers.</item>
    /// <item><b>L2</b>, pre-generation: the constructor's offsets and seeds, the lakes, rivers and
    /// streams in order with every field's bits, the whole river-point grid (cells in (x, y) order,
    /// each cell's points in STORED order, which the float sums depend on) and the single-entry river
    /// cache pre-generation leaves behind (cell, staleness, contents). Computed on an eager handle and
    /// again on a deferred one after <c>ForcePregeneration</c>; the two must agree.</item>
    /// <item><b>L3</b>, the location oracle's point grid: <c>BiomeGrid.Build</c>'s 4,194,304 biome bytes
    /// and height bits.</item>
    /// <item><b>L4</b>, placement of all 183 ordered entries: every instance's prefab, x/y/z bits, zone,
    /// ordered index and attempt, in registration order.</item>
    /// <item><b>L5</b>, the location oracle itself (<see cref="DumpedLocationOracle"/>, prefix 67 - every
    /// shipped preset's types): its hits, spawn and prefix, in order. L4 drives the engine directly;
    /// L5 goes through the oracle's own worker, which is the path a search takes.</item>
    /// </list>
    ///
    /// <para>Every layer is a function of the seed and nothing else. Any change that is meant to be
    /// value-neutral - an instrumented build, a new vector path, another CPU level - must reproduce
    /// the recorded digests exactly; the acceptance suite's profile-neutrality check does that for
    /// profiling off, phases on and counters on.</para>
    /// </summary>
    public sealed class WorldFingerprint
    {
        /// <summary>The schema name of a recorded reference file.</summary>
        public const string Schema = "seedlab-world-fingerprint/1";

        /// <summary>The layer names, in order.</summary>
        public static IReadOnlyList<string> LayerNames { get; } = new[] { "L1", "L2", "L3", "L4", "L5" };

        internal WorldFingerprint(int seed, string[] layers, bool riverCacheStale, bool deferredMatchesEager,
                                  int instances, int oracleHits)
        {
            Seed = seed;
            _layers = layers;
            RiverCacheStale = riverCacheStale;
            DeferredMatchesEager = deferredMatchesEager;
            Instances = instances;
            OracleHits = oracleHits;
        }

        private readonly string[] _layers;

        /// <summary>
        /// A fingerprint read back from a recording or another process, for comparison. It is data, not a
        /// computation: nothing checks that the digests belong to the seed.
        /// </summary>
        public static WorldFingerprint FromRecorded(int seed, string[] layers, bool riverCacheStale,
                                                    bool deferredMatchesEager, int instances, int oracleHits)
        {
            if (layers == null || layers.Length != 5) throw new ArgumentException("a fingerprint has five layers.", nameof(layers));
            return new WorldFingerprint(seed, (string[])layers.Clone(), riverCacheStale, deferredMatchesEager,
                                        instances, oracleHits);
        }

        public int Seed { get; }

        /// <summary>The digest of layer <paramref name="index"/> (0 = L1), lower-case hex.</summary>
        public string Layer(int index) => _layers[index];

        public string L1 => _layers[0];
        public string L2 => _layers[1];
        public string L3 => _layers[2];
        public string L4 => _layers[3];
        public string L5 => _layers[4];

        /// <summary><c>WorldGeneratorPort.RiverCacheIsStale</c> right after pre-generation.</summary>
        public bool RiverCacheStale { get; }

        /// <summary>
        /// True when the deferred handle's pre-generation state (L2) equals the eager handle's. False is a
        /// defect: the two are documented to be the same world.
        /// </summary>
        public bool DeferredMatchesEager { get; }

        /// <summary>Instances L4 registered (all 183 entries).</summary>
        public int Instances { get; }

        /// <summary>Hits L5's oracle returned (prefix 67).</summary>
        public int OracleHits { get; }

        /// <summary>One sentence per layer, for a reference file's header.</summary>
        public static string Describe(string layer) => layer switch
        {
            "L1" => "GetBiome and GetBaseHeight bits on the 1024^2 lattice at 24 m, deferred handle",
            "L2" => "pre-generation: offsets, seeds, lakes, rivers, streams, river-point grid in stored order, river cache",
            "L3" => "BiomeGrid.Build: 2048^2 biome bytes and height bits",
            "L4" => "LocationPlacementEngine.Run, all 183 entries: every instance in order",
            "L5" => "DumpedLocationOracle.Run, prefix 67: hits, spawn and prefix",
            _ => "",
        };
    }

    /// <summary>
    /// L1-L3 of a <see cref="WorldFingerprint"/>: the layers a machine without game data can compute
    /// and compare (<see cref="WorldFingerprinter.ComputeTerrain"/>).
    /// </summary>
    public sealed class TerrainFingerprint
    {
        internal TerrainFingerprint(int seed, string[] layers, bool riverCacheStale, bool deferredMatchesEager)
        {
            Seed = seed;
            _layers = layers;
            RiverCacheStale = riverCacheStale;
            DeferredMatchesEager = deferredMatchesEager;
        }

        private readonly string[] _layers;

        public int Seed { get; }

        /// <summary>The digest of layer <paramref name="index"/> (0 = L1, 2 = L3), lower-case hex.</summary>
        public string Layer(int index) => _layers[index];

        public bool RiverCacheStale { get; }
        public bool DeferredMatchesEager { get; }
    }

    /// <summary>
    /// Computes <see cref="WorldFingerprint"/>s. Opens the shipped game data once (the location table
    /// and the alt-biome definitions, failing closed like the oracle does); <see cref="Compute"/> is
    /// safe to call from several threads at once - each thread keeps its own 20 MB of grid buffers and
    /// its own alt-biome runtime objects, exactly as the oracle's workers do.
    /// </summary>
    public sealed class WorldFingerprinter : IDisposable
    {
        /// <summary>The ordered prefix L5 asks the oracle for: every shipped preset's types fall inside it.</summary>
        public const int OraclePrefix = 67;

        /// <summary>The world-generation version every fingerprint is taken at.</summary>
        public const int WorldGenVersion = 2;

        private readonly LocationTable _table;
        private readonly List<AltBiomeDef> _altDefs;
        private readonly DumpedLocationOracle _oracle;
        private readonly LocationPlan _plan;
        private readonly ThreadLocal<Worker> _workers;

        private WorldFingerprinter(GameData data, DumpedLocationOracle oracle)
        {
            _oracle = oracle;
            _table = oracle.Table;
            _altDefs = new List<AltBiomeDef>(data.AltBiomes);
            DataStamp = data.Stamp.FolderName;

            List<string> prefix = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < OraclePrefix && i < _table.Ordered.Count; i++)
            {
                string p = _table.Ordered[i].PrefabName;
                if (seen.Add(p)) prefix.Add(p);
            }

            _plan = oracle.Plan(prefix, needSpawn: true);
            _workers = new ThreadLocal<Worker>(() => new Worker(_table, _altDefs), trackAllValues: false);
        }

        /// <summary>The data folder the table came from, e.g. "1.0.15-59f53fb5".</summary>
        public string DataStamp { get; }

        /// <summary>
        /// Opens the shipped data. Throws what <c>GameData</c> throws when the data is missing or does not
        /// describe the installed game - a fingerprint of placements from another build would be a
        /// fingerprint of the wrong thing.
        /// </summary>
        public static WorldFingerprinter Open()
        {
            GameData data = GameData.Load();
            DumpedLocationOracle oracle = DumpedLocationOracle.Open();
            return new WorldFingerprinter(data, oracle);
        }

        /// <summary>
        /// The 64 seeds the recorded reference covers: the three GoldenCheck seeds, the two ground-truth
        /// worlds, and the first 59 seeds of <see cref="PilotSeedOrder"/>.
        /// </summary>
        public static int[] ReferenceSeeds()
        {
            PilotSeedOrder.Verify();
            List<int> seeds = new List<int> { 75539276, 92653657, 164429806, -1772362158, 319486907 };
            for (int i = 0; seeds.Count < 64; i++) seeds.Add(PilotSeedOrder.At(i));
            return seeds.ToArray();
        }

        /// <summary>Where a reference seed comes from, for a reference file.</summary>
        public static string SourceOf(int index) => index switch
        {
            < 3 => "goldencheck",
            < 5 => "ground-truth world",
            _ => "pilot order index " + (index - 5),
        };

        /// <summary>All five layers of one world. Several threads may call this at once.</summary>
        public WorldFingerprint Compute(int seed) => _workers.Value!.Compute(seed, _table, _oracle, _plan);

        [ThreadStatic] private static byte[]? t_biomes;
        [ThreadStatic] private static float[]? t_heights;

        /// <summary>
        /// L1-L3 of one world - the layers that need only the seed, so no game data is opened. This is
        /// what a machine without <c>data\</c> (a clone of the public repository, a tester on another
        /// CPU) can still compare with the reference: the same code computes these three layers inside
        /// <see cref="Compute"/>, so the digests are the same by construction, not by copy. Several
        /// threads may call it at once; each keeps its own 20 MB of grid buffers.
        /// </summary>
        public static TerrainFingerprint ComputeTerrain(int seed)
        {
            byte[] biomes = t_biomes ??= new byte[BiomeGrid.PointCount];
            float[] heights = t_heights ??= new float[BiomeGrid.PointCount];
            Terrain t = TerrainLayers(seed, biomes, heights);
            return new TerrainFingerprint(seed, new[] { t.L1, t.L2, t.L3 }, t.Stale, t.Same);
        }

        /// <summary>L1-L3 and what L4 and L5 go on to use: the eager generator and its point grid.</summary>
        private readonly struct Terrain
        {
            public Terrain(string l1, string l2, string l3, bool stale, bool same, WorldGeneratorPort gen, BiomeGrid grid)
            {
                L1 = l1; L2 = l2; L3 = l3; Stale = stale; Same = same; Gen = gen; Grid = grid;
            }

            public string L1 { get; }
            public string L2 { get; }
            public string L3 { get; }
            public bool Stale { get; }
            public bool Same { get; }
            public WorldGeneratorPort Gen { get; }
            public BiomeGrid Grid { get; }
        }

        private static Terrain TerrainLayers(int seed, byte[] biomes, float[] heights)
        {
            string l1, l2, l3;

            // ---- L1: biome and base height on a handle that never pre-generates ----------------------
            WorldGeneratorPort lattice = new WorldGeneratorPort(seed, WorldGenVersion, menu: false,
                                                                deferPregeneration: true);
            using (Digest d = new Digest())
            {
                for (int i = 0; i < 1024; i++)
                {
                    float z = (float)((i - 512) * 24 + 12);
                    for (int j = 0; j < 1024; j++)
                    {
                        float x = (float)((j - 512) * 24 + 12);
                        d.U16((ushort)lattice.GetBiome(x, z));
                        d.F32(lattice.GetBaseHeightPublic(x, z));
                    }
                }

                d.Bool(lattice.PregenerationPending);
                l1 = d.Hex();
            }

            // ---- L2: pre-generation, eager and deferred ----------------------------------------------
            WorldGeneratorPort gen = new WorldGeneratorPort(seed, WorldGenVersion, menu: false);
            bool stale = gen.RiverCacheIsStale;
            l2 = Worker.PregenDigest(gen);

            WorldGeneratorPort deferred = new WorldGeneratorPort(seed, WorldGenVersion, menu: false,
                                                                 deferPregeneration: true);
            deferred.ForcePregeneration();
            bool same = string.Equals(Worker.PregenDigest(deferred), l2, StringComparison.Ordinal);

            // ---- L3: the oracle's point grid --------------------------------------------------------
            BiomeGrid grid = BiomeGrid.Build(gen, 1, biomes, heights);
            using (Digest d = new Digest())
            {
                d.Bytes(grid.PointBiomes);
                d.Bytes(MemoryMarshal.AsBytes(grid.PointHeights.AsSpan()));
                d.I64(grid.CutoffRingPoints);
                l3 = d.Hex();
            }

            return new Terrain(l1, l2, l3, stale, same, gen, grid);
        }

        public void Dispose()
        {
            _workers.Dispose();
            _oracle.Dispose();
        }

        private sealed class Worker
        {
            private readonly List<AltBiomeRuntime> _alts;
            private readonly byte[] _biomes = new byte[BiomeGrid.PointCount];
            private readonly float[] _heights = new float[BiomeGrid.PointCount];

            public Worker(LocationTable table, IReadOnlyList<AltBiomeDef> defs)
            {
                _alts = new List<AltBiomeRuntime>(defs.Count);
                foreach (AltBiomeDef d in defs) _alts.Add(AltBiomeRuntime.FromDump(d));
            }

            public WorldFingerprint Compute(int seed, LocationTable table, DumpedLocationOracle oracle, LocationPlan plan)
            {
                string[] layers = new string[5];

                // ---- L1-L3: the terrain layers, shared with ComputeTerrain ---------------------------
                Terrain t = TerrainLayers(seed, _biomes, _heights);
                layers[0] = t.L1;
                layers[1] = t.L2;
                layers[2] = t.L3;
                bool stale = t.Stale;
                bool same = t.Same;
                WorldGeneratorPort gen = t.Gen;
                BiomeGrid grid = t.Grid;

                // ---- L4: every ordered entry, straight through the engine ---------------------------
                BiomeField field = BiomeField.Build(grid);
                AltBiomeAssignment.Generate(field, _alts, seed);
                PlacementResult res = LocationPlacementEngine.Run(gen, field, table,
                                                                  new PlacementOptions { AltBiomesComputed = true });
                using (Digest d = new Digest())
                {
                    d.I32(res.Instances.Count);
                    foreach (LocationInstanceResult r in res.Instances)
                    {
                        d.Str(r.PrefabName);
                        d.F32(r.X);
                        d.F32(r.Y);
                        d.F32(r.Z);
                        d.I32(r.Zone.x);
                        d.I32(r.Zone.y);
                        d.I32(r.OrderedIndex);
                        d.I32(r.Attempt);
                    }

                    d.I32(res.LastOrderedIndexRun);
                    layers[3] = d.Hex();
                }

                // ---- L5: the oracle's own path, as a search runs it --------------------------------
                LocationWorld world = oracle.Run(plan, seed, WorldGenVersion, gate: null);
                using (Digest d = new Digest())
                {
                    d.I32(world.Hits.Count);
                    foreach (LocationHit h in world.Hits)
                    {
                        d.Str(h.Prefab);
                        d.F32(h.X);
                        d.F32(h.Z);
                        d.Bool(h.Candidate);
                    }

                    d.Bool(world.HasSpawn);
                    d.F32(world.SpawnX);
                    d.F32(world.SpawnZ);
                    d.Bool(world.Aborted);
                    d.I32(world.LastOrderedIndexRun);
                    layers[4] = d.Hex();
                }

                return new WorldFingerprint(seed, layers, stale, same, res.Instances.Count, world.Hits.Count);
            }

            internal static string PregenDigest(WorldGeneratorPort g)
            {
                using Digest d = new Digest();
                d.F32(g.Offset0);
                d.F32(g.Offset1);
                d.F32(g.Offset2);
                d.F32(g.Offset3);
                d.F32(g.Offset4);
                d.I32(g.RiverSeed);
                d.I32(g.StreamSeed);

                IReadOnlyList<Vec2>? lakes = g.GetLakes();
                d.I32(lakes?.Count ?? -1);
                if (lakes != null)
                {
                    foreach (Vec2 p in lakes) { d.F32(p.x); d.F32(p.y); }
                }

                Rivers(d, g.GetRivers());
                Rivers(d, g.GetStreams());

                IReadOnlyDictionary<Vec2i, WorldGeneratorPort.RiverPoint[]> points = g.GetRiverPoints();
                List<Vec2i> cells = new List<Vec2i>(points.Keys);
                cells.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
                d.I32(cells.Count);
                foreach (Vec2i c in cells)
                {
                    d.I32(c.x);
                    d.I32(c.y);
                    Points(d, points[c]);
                }

                Vec2i cached = g.RiverCacheCell;
                d.I32(cached.x);
                d.I32(cached.y);
                d.Bool(g.RiverCacheIsStale);
                WorldGeneratorPort.RiverPoint[]? cachedPoints = g.CopyRiverCachePoints();
                if (cachedPoints == null) d.I32(-1);
                else Points(d, cachedPoints);
                return d.Hex();
            }

            private static void Rivers(Digest d, IReadOnlyList<WorldGeneratorPort.River> rivers)
            {
                d.I32(rivers.Count);
                foreach (WorldGeneratorPort.River r in rivers)
                {
                    d.F32(r.p0.x); d.F32(r.p0.y);
                    d.F32(r.p1.x); d.F32(r.p1.y);
                    d.F32(r.center.x); d.F32(r.center.y);
                    d.F32(r.widthMin); d.F32(r.widthMax);
                    d.F32(r.curveWidth); d.F32(r.curveWavelength);
                }
            }

            private static void Points(Digest d, WorldGeneratorPort.RiverPoint[] pts)
            {
                d.I32(pts.Length);
                foreach (WorldGeneratorPort.RiverPoint rp in pts)
                {
                    d.F32(rp.p.x); d.F32(rp.p.y); d.F32(rp.w); d.F32(rp.w2);
                }
            }
        }

        /// <summary>
        /// A SHA-256 fed little-endian values through a 64 KB buffer. Floats go in as their bit patterns,
        /// so -0 and +0, and every NaN payload, are different inputs - which is the point.
        /// </summary>
        private sealed class Digest : IDisposable
        {
            private readonly IncrementalHash _h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            private readonly byte[] _buf = new byte[1 << 16];
            private int _n;

            private Span<byte> Take(int bytes)
            {
                if (_n + bytes > _buf.Length) Flush();
                Span<byte> s = _buf.AsSpan(_n, bytes);
                _n += bytes;
                return s;
            }

            private void Flush()
            {
                if (_n == 0) return;
                _h.AppendData(_buf, 0, _n);
                _n = 0;
            }

            public void I32(int v) => BinaryPrimitives.WriteInt32LittleEndian(Take(4), v);
            public void I64(long v) => BinaryPrimitives.WriteInt64LittleEndian(Take(8), v);
            public void U16(ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(Take(2), v);
            public void F32(float v) => BinaryPrimitives.WriteInt32LittleEndian(Take(4), BitConverter.SingleToInt32Bits(v));
            public void Bool(bool v) => Take(1)[0] = v ? (byte)1 : (byte)0;

            public void Str(string s)
            {
                byte[] b = Encoding.UTF8.GetBytes(s);
                I32(b.Length);
                Bytes(b);
            }

            public void Bytes(ReadOnlySpan<byte> b)
            {
                Flush();
                _h.AppendData(b);
            }

            public string Hex()
            {
                Flush();
                return Convert.ToHexString(_h.GetHashAndReset()).ToLowerInvariant();
            }

            public void Dispose() => _h.Dispose();
        }
    }
}
