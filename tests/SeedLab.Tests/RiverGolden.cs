using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using SeedLab.LocationOracle;
using SeedLab.WorldGen;
using SeedLab.WorldGen.Unity;

namespace SeedLabTests
{
    /// <summary>
    /// The river-points golden: everything the lake/river/stream pre-generation produces, recorded bit
    /// for bit for the first N seeds of the profile's fixed seed order, so a change to how that code
    /// uses memory can be proven to change no value - including the ORDER the river-point dictionary
    /// lists its cells in and the order of the points inside each cell.
    ///
    /// <code>
    /// dotnet run -c Release --project tests\SeedLab.Tests -- river-golden --write &lt;file&gt; [--seeds 64] [--threads T] [--force]
    /// dotnet run -c Release --project tests\SeedLab.Tests -- river-golden --check &lt;file&gt; [--threads T]
    /// dotnet run -c Release --project tests\SeedLab.Tests -- river-golden --self-test
    /// </code>
    ///
    /// <para><b>What a seed's record holds</b>, every float as its raw bits: the constructor's values;
    /// the lakes, rivers and streams in list order; the river-point dictionary in enumeration order (each
    /// cell's key and point count, then every point's position, width and squared width); a digest of the
    /// same content sorted by cell, so a pure change of cell order is told apart from a change of content;
    /// the single-entry river cache as pre-generation left it; and river weights and heights at the cache
    /// cell's centre, at the first point of every k-th cell and on a 21 x 21 lattice, asked of five
    /// handles that reach the river points by different paths: the eager generator (a), a fork that
    /// inherits its cache (d), a cold fork (e), a deferred generator whose first river query triggers
    /// the pre-generation lazily (b), and a fork of a deferred generator, which pre-generates the parent
    /// (c). The paths must agree with each other (checked every time, as invariants) and with the golden.
    /// Each record carries its own SHA-256.</para>
    ///
    /// <para><b>Size and time.</b> About 11 MB of raw bits per seed; stored as gzip with each value kept
    /// as its xor with the same field of the element before, it came to 2.1 MiB per seed (136 MiB for
    /// 64 seeds, written in 17 s on 8 threads, checked in 25 s; 2026-09-25, Ryzen 7 9800X3D, a busy
    /// machine). It is a before/after file for scratch space, not something to commit.</para>
    ///
    /// <para><b>--check</b> recomputes every seed on this build and reports each seed that differs with
    /// its first difference: the block, and for the points the cell (its place in enumeration order and
    /// its key), the point's index in the cell and the field; for a probe, where it was asked. Exit 0 when
    /// every seed and every invariant agrees, 1 when anything differs, 2 on a usage or file error.</para>
    /// </summary>
    public static class RiverGolden
    {
        private const string Magic = "SLRIVGLD";
        private const int FormatVersion = 1;
        private const int WorldGenVersion = 2;

        public static int Run(string[] args)
        {
            try
            {
                if (Array.IndexOf(args, "--self-test") >= 0) return SelfTest();
                string? write = Value(args, "--write"), check = Value(args, "--check");
                int threads = Math.Max(1, int.Parse(Value(args, "--threads") ?? Math.Max(1, Math.Min(8, Environment.ProcessorCount / 2)).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
                if ((write == null) == (check == null)) return Usage("give exactly one of --write <file>, --check <file> or --self-test");
                if (write != null)
                {
                    int seeds = int.Parse(Value(args, "--seeds") ?? "64", CultureInfo.InvariantCulture);
                    if (seeds < 1 || seeds > 100_000) return Usage("--seeds takes 1 to 100000");
                    return Write(Path.GetFullPath(write), seeds, threads, Array.IndexOf(args, "--force") >= 0);
                }

                return Check(Path.GetFullPath(check!), threads);
            }
            catch (FormatException ex)
            {
                return Usage(ex.Message);
            }
        }

        private static int Usage(string why)
        {
            Console.Error.WriteLine("river-golden: " + why);
            Console.Error.WriteLine("  river-golden --write <file> [--seeds 64] [--threads T] [--force]");
            Console.Error.WriteLine("  river-golden --check <file> [--threads T]");
            Console.Error.WriteLine("  river-golden --self-test");
            return 2;
        }

        private static string? Value(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        // =============================================================================================
        // The record
        // =============================================================================================

        /// <summary>A named array of raw 32-bit values, <see cref="Fields"/>.Length per element.</summary>
        internal sealed class Block
        {
            public Block(string name, string[] fields, uint[] values)
            {
                if (fields.Length == 0 || values.Length % fields.Length != 0) throw new ArgumentException("block " + name + ": " + values.Length + " values do not fill " + fields.Length + " fields");
                Name = name;
                Fields = fields;
                Values = values;
            }

            public string Name { get; }
            public string[] Fields { get; }
            public uint[] Values { get; set; }
            public int Count => Values.Length / Fields.Length;
        }

        internal sealed class SeedRecord
        {
            public long Index;
            public int Seed;
            public List<Block> Blocks = new List<Block>();
            public byte[] Sha256 = Array.Empty<byte>();

            public Block? Get(string name) => Blocks.Find(b => b.Name == name);

            public void Seal() => Sha256 = Digest(Blocks);
        }

        private static readonly string[] CtorFields =
        {
            "seed", "version", "offset0", "offset1", "offset2", "offset3", "offset4", "riverSeed", "streamSeed",
            "minMountainDistance", "minDarklandNoise", "maxMarshDistance",
        };

        private static readonly string[] RiverFields =
        {
            "p0.x", "p0.y", "p1.x", "p1.y", "center.x", "center.y", "widthMin", "widthMax", "curveWidth", "curveWavelength",
        };

        private static readonly string[] PointFields = { "p.x", "p.y", "w", "w2" };
        private static readonly string[] ProbeFields = { "weight", "width", "height" };
        private static readonly string[] DigestFields = { "h0", "h1", "h2", "h3", "h4", "h5", "h6", "h7" };

        private static uint F(float v) => BitConverter.SingleToUInt32Bits(v);

        private static float AsFloat(uint v) => BitConverter.UInt32BitsToSingle(v);

        /// <summary>Computes one seed's record on this build. Every handle stays on the calling thread.</summary>
        internal static SeedRecord Build(long index, int seed)
        {
            SeedRecord r = new SeedRecord { Index = index, Seed = seed };
            List<Block> b = r.Blocks;

            // (a) eager. Its cache is read before anything asks it a question, and both of its forks are
            // taken before that too: (d) inherits the cache as pre-generation left it, (e) starts cold.
            WorldGeneratorPort a = new WorldGeneratorPort(seed, WorldGenVersion);
            Vec2i cell = a.RiverCacheCell;
            b.Add(CacheBlock("a.cache", a));
            b.Add(CachePoints("a.cache.points", a));
            WorldGeneratorPort d = a.Fork(inheritRiverCache: true);
            WorldGeneratorPort e = a.Fork();
            b.Add(CacheBlock("d.cache", d));
            b.Add(CachePoints("d.cache.points", d));
            List<Block> data = DataBlocks(a);
            b.InsertRange(0, data);

            List<(float X, float Y)> probes = ProbePoints(a, cell);
            uint[] at = new uint[probes.Count * 2];
            for (int i = 0; i < probes.Count; i++)
            {
                at[2 * i] = F(probes[i].X);
                at[2 * i + 1] = F(probes[i].Y);
            }

            b.Add(new Block("probe.at", new[] { "x", "y" }, at));
            b.Add(Probe("a.probe", a, probes));
            b.Add(Probe("d.probe", d, probes));
            b.Add(Probe("e.probe", e, probes));

            // (b) deferred: nothing about biomes or base heights may start the pre-generation; the first
            // river query does, lazily, and must then answer exactly as (a).
            WorldGeneratorPort bg = new WorldGeneratorPort(seed, WorldGenVersion, menu: false, deferPregeneration: true);
            uint pending0 = bg.PregenerationPending ? 1u : 0u;
            uint[] biomes = new uint[21 * 21 * 2];
            int k = 0;
            for (int j = -10; j <= 10; j++)
            {
                for (int i = -10; i <= 10; i++)
                {
                    biomes[k++] = (uint)bg.GetBiome(i * 1000f, j * 1000f);
                    biomes[k++] = F(bg.GetBaseHeightPublic(i * 1000f, j * 1000f));
                }
            }

            uint pending1 = bg.PregenerationPending ? 1u : 0u;
            b.Add(new Block("b.pending", new[] { "at_construction", "after_biome_pass" }, new[] { pending0, pending1 }));
            b.Add(new Block("b.biomes", new[] { "biome", "base_height" }, biomes));
            b.Add(Probe("b.probe", bg, probes));
            b.Add(new Block("b.digest", DigestFields, DigestValues(DataBlocks(bg))));

            // (c) a fork of a deferred generator: Fork pre-generates the parent; the fork shares its data
            // and starts with a cold cache, so it must answer as (e).
            WorldGeneratorPort cp = new WorldGeneratorPort(seed, WorldGenVersion, menu: false, deferPregeneration: true);
            WorldGeneratorPort c = cp.Fork();
            b.Add(CacheBlock("c.parent.cache", cp));
            b.Add(CachePoints("c.parent.cache.points", cp));
            b.Add(new Block("c.digest", DigestFields, DigestValues(DataBlocks(c))));
            b.Add(Probe("c.probe", c, probes));

            r.Seal();
            return r;
        }

        /// <summary>The constructor's values, the lakes, rivers and streams, and the river points in enumeration order, then their sorted digest.</summary>
        private static List<Block> DataBlocks(WorldGeneratorPort g)
        {
            List<Block> b = new List<Block>();
            b.Add(new Block("ctor", CtorFields, new[]
            {
                (uint)g.GetSeed(), (uint)g.WorldGenVersion, F(g.Offset0), F(g.Offset1), F(g.Offset2), F(g.Offset3), F(g.Offset4),
                (uint)g.RiverSeed, (uint)g.StreamSeed, F(g.MinMountainDistance), F(g.MinDarklandNoise), F(g.MaxMarshDistance),
            }));

            IReadOnlyList<Vec2>? lakes = g.GetLakes();
            uint[] lv = new uint[(lakes?.Count ?? 0) * 2];
            for (int i = 0; lakes != null && i < lakes.Count; i++)
            {
                lv[2 * i] = F(lakes[i].x);
                lv[2 * i + 1] = F(lakes[i].y);
            }

            b.Add(new Block("lakes", new[] { "x", "y" }, lv));
            b.Add(new Block("rivers", RiverFields, Rivers(g.GetRivers())));
            b.Add(new Block("streams", RiverFields, Rivers(g.GetStreams())));

            IReadOnlyDictionary<Vec2i, WorldGeneratorPort.RiverPoint[]> pts = g.GetRiverPoints();
            List<uint> cells = new List<uint>(pts.Count * 3);
            int total = 0;
            foreach (KeyValuePair<Vec2i, WorldGeneratorPort.RiverPoint[]> kv in pts) total += kv.Value.Length;
            uint[] pv = new uint[total * 4];
            int o = 0;
            foreach (KeyValuePair<Vec2i, WorldGeneratorPort.RiverPoint[]> kv in pts)
            {
                cells.Add((uint)kv.Key.x);
                cells.Add((uint)kv.Key.y);
                cells.Add((uint)kv.Value.Length);
                foreach (WorldGeneratorPort.RiverPoint p in kv.Value)
                {
                    pv[o++] = F(p.p.x);
                    pv[o++] = F(p.p.y);
                    pv[o++] = F(p.w);
                    pv[o++] = F(p.w2);
                }
            }

            b.Add(new Block("cells", new[] { "x", "y", "count" }, cells.ToArray()));
            b.Add(new Block("points", PointFields, pv));
            b.Add(new Block("sorted", DigestFields, SortedDigest(b[b.Count - 2], b[b.Count - 1])));
            return b;
        }

        private static uint[] Rivers(IReadOnlyList<WorldGeneratorPort.River> list)
        {
            uint[] v = new uint[list.Count * RiverFields.Length];
            int o = 0;
            foreach (WorldGeneratorPort.River r in list)
            {
                v[o++] = F(r.p0.x); v[o++] = F(r.p0.y); v[o++] = F(r.p1.x); v[o++] = F(r.p1.y);
                v[o++] = F(r.center.x); v[o++] = F(r.center.y); v[o++] = F(r.widthMin); v[o++] = F(r.widthMax);
                v[o++] = F(r.curveWidth); v[o++] = F(r.curveWavelength);
            }

            return v;
        }

        /// <summary>The cells' content (key, count, points) hashed in (x, then y) order: blind to enumeration order only.</summary>
        internal static uint[] SortedDigest(Block cells, Block points)
        {
            int n = cells.Count;
            int[] start = new int[n];
            int s = 0;
            for (int i = 0; i < n; i++)
            {
                start[i] = s;
                s += (int)cells.Values[3 * i + 2];
            }

            int[] order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            Array.Sort(order, (p, q) =>
            {
                int px = (int)cells.Values[3 * p], qx = (int)cells.Values[3 * q];
                if (px != qx) return px.CompareTo(qx);
                return ((int)cells.Values[3 * p + 1]).CompareTo((int)cells.Values[3 * q + 1]);
            });
            using IncrementalHash h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buf = new byte[4];
            foreach (int i in order)
            {
                for (int f = 0; f < 3; f++) Put(h, buf, cells.Values[3 * i + f]);
                int c = (int)cells.Values[3 * i + 2];
                for (int p = 0; p < 4 * c && 4 * start[i] + p < points.Values.Length; p++) Put(h, buf, points.Values[4 * start[i] + p]);
            }

            return Words(h.GetHashAndReset());
        }

        private static Block CacheBlock(string name, WorldGeneratorPort g)
        {
            Vec2i c = g.RiverCacheCell;
            return new Block(name, new[] { "cell.x", "cell.y", "stale" }, new[] { (uint)c.x, (uint)c.y, g.RiverCacheIsStale ? 1u : 0u });
        }

        private static Block CachePoints(string name, WorldGeneratorPort g)
        {
            WorldGeneratorPort.RiverPoint[]? pts = g.CopyRiverCachePoints();
            uint[] v = new uint[(pts?.Length ?? 0) * 4];
            for (int i = 0; pts != null && i < pts.Length; i++)
            {
                v[4 * i] = F(pts[i].p.x);
                v[4 * i + 1] = F(pts[i].p.y);
                v[4 * i + 2] = F(pts[i].w);
                v[4 * i + 3] = F(pts[i].w2);
            }

            return new Block(name, PointFields, v);
        }

        /// <summary>
        /// Where the handles are asked: the cache cell's centre first (the one query that can meet a stale
        /// cache), then the first point of every k-th cell (inside a channel, where the order of the
        /// points in a cell changes the sum), then a 21 x 21 lattice 1 km apart.
        /// </summary>
        private static List<(float X, float Y)> ProbePoints(WorldGeneratorPort a, Vec2i cell)
        {
            List<(float, float)> p = new List<(float, float)> { (cell.x * 64f, cell.y * 64f) };
            IReadOnlyDictionary<Vec2i, WorldGeneratorPort.RiverPoint[]> pts = a.GetRiverPoints();
            int every = Math.Max(1, pts.Count / 48), i = 0;
            foreach (KeyValuePair<Vec2i, WorldGeneratorPort.RiverPoint[]> kv in pts)
            {
                if (i++ % every == 0 && kv.Value.Length > 0) p.Add((kv.Value[0].p.x, kv.Value[0].p.y));
            }

            for (int y = -10; y <= 10; y++)
            {
                for (int x = -10; x <= 10; x++) p.Add((x * 1000f, y * 1000f));
            }

            return p;
        }

        private static Block Probe(string name, WorldGeneratorPort g, List<(float X, float Y)> probes)
        {
            uint[] v = new uint[probes.Count * 3];
            for (int i = 0; i < probes.Count; i++)
            {
                g.GetRiverWeightPublic(probes[i].X, probes[i].Y, out float weight, out float width);
                v[3 * i] = F(weight);
                v[3 * i + 1] = F(width);
                v[3 * i + 2] = F(g.GetHeight(probes[i].X, probes[i].Y));
            }

            return new Block(name, ProbeFields, v);
        }

        // =============================================================================================
        // Digests
        // =============================================================================================

        private static void Put(IncrementalHash h, byte[] buf, uint v)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buf, v);
            h.AppendData(buf, 0, 4);
        }

        private static uint[] Words(byte[] hash)
        {
            uint[] w = new uint[8];
            for (int i = 0; i < 8; i++) w[i] = BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(4 * i));
            return w;
        }

        private static uint[] DigestValues(List<Block> blocks) => Words(Digest(blocks));

        /// <summary>SHA-256 of blocks as written: each name, its field names and its values, in order.</summary>
        internal static byte[] Digest(List<Block> blocks)
        {
            using IncrementalHash h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buf = new byte[4];
            foreach (Block b in blocks)
            {
                byte[] name = Encoding.UTF8.GetBytes(b.Name + "(" + string.Join(",", b.Fields) + ")");
                Put(h, buf, (uint)name.Length);
                h.AppendData(name);
                Put(h, buf, (uint)b.Values.Length);
                byte[] vals = new byte[b.Values.Length * 4];
                for (int i = 0; i < b.Values.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(vals.AsSpan(4 * i), b.Values[i]);
                h.AppendData(vals);
            }

            return h.GetHashAndReset();
        }

        private static string Hex(byte[] h, int chars = 64) => Convert.ToHexString(h).ToLowerInvariant().Substring(0, chars);

        // =============================================================================================
        // Invariants: the five paths agree
        // =============================================================================================

        /// <summary>The ways into the river points that must agree on this build, whatever the golden says.</summary>
        internal static List<string> Invariants(SeedRecord r)
        {
            List<string> bad = new List<string>();
            uint[] own = DigestValues(r.Blocks.GetRange(0, 7));
            void Same(string x, string y, string what)
            {
                Block? bx = r.Get(x), by = r.Get(y);
                if (bx == null || by == null || !Equal(bx.Values, by.Values)) bad.Add(what + " (" + x + " against " + y + ")");
            }

            if (!Equal(r.Get("b.digest")?.Values, own)) bad.Add("the deferred generator's river data differs from the eager one's (b.digest)");
            if (!Equal(r.Get("c.digest")?.Values, own)) bad.Add("the deferred generator's fork holds other river data than the eager one (c.digest)");
            Same("b.probe", "a.probe", "a lazily pre-generated handle answers differently from the eager one");
            Same("d.probe", "a.probe", "a fork that inherits the cache answers differently from its parent");
            Same("c.probe", "e.probe", "the deferred generator's cold fork answers differently from the eager one's cold fork");
            Same("d.cache", "a.cache", "the inherited cache is not the parent's");
            Same("d.cache.points", "a.cache.points", "the inherited cache holds other points");
            Same("c.parent.cache", "a.cache", "pre-generation through Fork leaves another cache than the eager constructor");
            Same("c.parent.cache.points", "a.cache.points", "pre-generation through Fork leaves other cached points");
            Block? p = r.Get("b.pending");
            if (p == null || p.Values[0] != 1 || p.Values[1] != 1) bad.Add("the deferred generator started its pre-generation before a river query (b.pending)");
            return bad;
        }

        private static bool Equal(uint[]? a, uint[]? b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            return a.AsSpan().SequenceEqual(b);
        }

        // =============================================================================================
        // The first difference
        // =============================================================================================

        internal sealed class Difference
        {
            public string Block = "";
            public int Element = -1;
            public string Field = "";
            public int Cell = -1;
            public int PointInCell = -1;
            public bool OrderOnly;
            public string Text = "";
        }

        /// <summary>The first difference between a golden record and this build's, or null when they are the same.</summary>
        internal static Difference? Compare(SeedRecord golden, SeedRecord now)
        {
            if (golden.Seed != now.Seed) return new Difference { Block = "seed", Text = "the golden is for seed " + golden.Seed + ", this is " + now.Seed };
            // "Order only" is decided from the cells and points themselves, sorted by cell on both sides -
            // never from a stored digest, which a damaged record could carry unchanged.
            bool orderOnly = false;
            Block? gc = golden.Get("cells"), gp = golden.Get("points"), nc = now.Get("cells"), np = now.Get("points");
            if (gc != null && gp != null && nc != null && np != null) orderOnly = Equal(SortedDigest(gc, gp), SortedDigest(nc, np));

            foreach (Block g in golden.Blocks)
            {
                Block? n = now.Get(g.Name);
                if (n == null) return new Difference { Block = g.Name, Text = "block " + g.Name + " is missing on this build" };
                if (string.Join(",", g.Fields) != string.Join(",", n.Fields))
                    return new Difference { Block = g.Name, Text = "block " + g.Name + " has fields (" + string.Join(",", n.Fields) + "), the golden (" + string.Join(",", g.Fields) + ")" };
                int len = Math.Min(g.Values.Length, n.Values.Length);
                int first = -1;
                for (int i = 0; i < len; i++)
                {
                    if (g.Values[i] != n.Values[i])
                    {
                        first = i;
                        break;
                    }
                }

                if (first < 0 && g.Values.Length == n.Values.Length) continue;
                if (first < 0) first = len;
                bool inOrder = orderOnly && (g.Name == "cells" || g.Name == "points");
                Difference d = Describe(g.Name, g, n, first, golden, now);
                d.OrderOnly = inOrder;
                if (inOrder) d.Text += " - the cells' CONTENT is identical (same keys, counts and points sorted by cell): only the order the dictionary lists them in changed";
                return d;
            }

            foreach (Block n in now.Blocks)
            {
                if (golden.Get(n.Name) == null) return new Difference { Block = n.Name, Text = "block " + n.Name + " is new on this build (the golden has none)" };
            }

            return null;
        }

        private static Difference Describe(string name, Block g, Block n, int i, SeedRecord golden, SeedRecord now)
        {
            int stride = g.Fields.Length;
            int element = i / stride;
            string field = g.Fields[i % stride];
            Difference d = new Difference { Block = name, Element = element, Field = field };
            string where;
            if (i >= g.Values.Length || i >= n.Values.Length)
            {
                d.Text = name + ": " + n.Count + " elements, the golden has " + g.Count + " (they agree up to element " + element + ")";
                return d;
            }

            if (name == "points")
            {
                // The point's cell, from the counts both records agree on (the cells block compared equal first).
                Block? cells = golden.Get("cells");
                int cell = -1, inCell = element, cnt = 0;
                for (int c = 0; cells != null && c < cells.Count; c++)
                {
                    cnt = (int)cells.Values[3 * c + 2];
                    if (inCell < cnt)
                    {
                        cell = c;
                        break;
                    }

                    inCell -= cnt;
                }

                d.Cell = cell;
                d.PointInCell = inCell;
                where = cell >= 0 && cells != null
                    ? "cell #" + cell + " in enumeration order (key " + (int)cells.Values[3 * cell] + ", " + (int)cells.Values[3 * cell + 1] + "), point " + inCell + " of " + cnt
                    : "point " + element;
            }
            else if (name == "cells")
            {
                d.Cell = element;
                where = "cell #" + element + " in enumeration order";
            }
            else if (name.EndsWith(".probe", StringComparison.Ordinal))
            {
                Block? at = golden.Get("probe.at");
                string kind = element == 0 ? "the cache cell's centre" : "probe";
                where = at != null && element < at.Count
                    ? kind + " " + element + " at (" + Num(AsFloat(at.Values[2 * element])) + ", " + Num(AsFloat(at.Values[2 * element + 1])) + ")"
                    : "probe " + element;
            }
            else
            {
                where = g.Count > 1 ? "element " + element : "";
            }

            bool isFloat = !(name == "cells" || name == "ctor" && (field is "seed" or "version" or "riverSeed" or "streamSeed")
                             || name.EndsWith(".cache", StringComparison.Ordinal) || name == "b.pending" || name.EndsWith("digest", StringComparison.Ordinal)
                             || name == "sorted" || name == "b.biomes" && field == "biome");
            d.Text = name + (where.Length > 0 ? ", " + where : "") + ", field " + field + ": golden " + Val(g.Values[i], isFloat) + ", now " + Val(n.Values[i], isFloat);
            return d;
        }

        private static string Val(uint v, bool isFloat) => isFloat
            ? "0x" + v.ToString("X8", CultureInfo.InvariantCulture) + " (" + Num(AsFloat(v)) + ")"
            : ((int)v).ToString(CultureInfo.InvariantCulture);

        private static string Num(float f) => f.ToString("R", CultureInfo.InvariantCulture);

        // =============================================================================================
        // The file
        // =============================================================================================

        private sealed class Header
        {
            public ulong Key;
            public long From;
            public int Seeds;
            public string Text = "";
        }

        private static void WriteHeader(BinaryWriter w, Header h)
        {
            w.Write(Encoding.ASCII.GetBytes(Magic));
            w.Write(FormatVersion);
            w.Write(h.Key);
            w.Write(h.From);
            w.Write(h.Seeds);
            w.Write(h.Text);
        }

        private static Header ReadHeader(BinaryReader r)
        {
            string magic = Encoding.ASCII.GetString(r.ReadBytes(8));
            if (magic != Magic) throw new InvalidDataException("not a river golden (no " + Magic + " header)");
            int v = r.ReadInt32();
            if (v != FormatVersion) throw new InvalidDataException("a river golden of format " + v + "; this build reads format " + FormatVersion);
            return new Header { Key = r.ReadUInt64(), From = r.ReadInt64(), Seeds = r.ReadInt32(), Text = r.ReadString() };
        }

        /// <summary>
        /// One record: index, seed, SHA-256, then each block's name, field names and values. Values are
        /// stored as the xor with the same field of the element before, which the reader undoes; that
        /// makes neighbouring river points small numbers that compress well.
        /// </summary>
        internal static void WriteRecord(BinaryWriter w, SeedRecord r)
        {
            w.Write(r.Index);
            w.Write(r.Seed);
            w.Write(r.Sha256);
            w.Write(r.Blocks.Count);
            foreach (Block b in r.Blocks)
            {
                w.Write(b.Name);
                w.Write((byte)b.Fields.Length);
                foreach (string f in b.Fields) w.Write(f);
                w.Write(b.Values.Length);
                int s = b.Fields.Length;
                byte[] buf = new byte[b.Values.Length * 4];
                for (int i = 0; i < b.Values.Length; i++)
                {
                    uint v = i >= s ? b.Values[i] ^ b.Values[i - s] : b.Values[i];
                    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4 * i), v);
                }

                w.Write(buf);
            }
        }

        internal static SeedRecord ReadRecord(BinaryReader r)
        {
            SeedRecord rec = new SeedRecord { Index = r.ReadInt64(), Seed = r.ReadInt32(), Sha256 = r.ReadBytes(32) };
            int blocks = r.ReadInt32();
            for (int b = 0; b < blocks; b++)
            {
                string name = r.ReadString();
                string[] fields = new string[r.ReadByte()];
                for (int f = 0; f < fields.Length; f++) fields[f] = r.ReadString();
                int n = r.ReadInt32();
                byte[] buf = r.ReadBytes(n * 4);
                if (buf.Length != n * 4) throw new EndOfStreamException("the golden ends inside block " + name);
                uint[] v = new uint[n];
                int s = fields.Length;
                for (int i = 0; i < n; i++)
                {
                    uint x = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4 * i));
                    v[i] = i >= s ? x ^ v[i - s] : x;
                }

                rec.Blocks.Add(new Block(name, fields, v));
            }

            return rec;
        }

        private static string BuildIdentity()
        {
            string dll = typeof(WorldGeneratorPort).Assembly.Location;
            string sha;
            try
            {
                using FileStream fs = File.OpenRead(dll);
                sha = Hex(SHA256.HashData(fs), 16);
            }
            catch (Exception)
            {
                sha = "unknown";
            }

            return "SeedLab.WorldGen.dll " + sha + ", " + SeedLab.WorldGen.Simd.SimdDispatch.Summary + ", " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        }

        private static string? Refused(string full)
        {
            // data\ and groundtruth\ are the repository's evidence (and may be links to another checkout's): never written.
            DirectoryInfo? d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null)
            {
                foreach (string name in new[] { "data", "groundtruth" })
                {
                    string root = Path.Combine(d.FullName, name) + Path.DirectorySeparatorChar;
                    if (Directory.Exists(root) && full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return "inside " + root;
                }

                d = d.Parent;
            }

            return null;
        }

        // =============================================================================================
        // --write and --check
        // =============================================================================================

        private static int Write(string path, int seeds, int threads, bool force)
        {
            if (Refused(path) is string why) return Usage("--write: refused, " + path + " is " + why + " (the repository's evidence is never written)");
            if (File.Exists(path) && !force) return Usage("--write: " + path + " exists; give --force to replace it");
            string? dir = Path.GetDirectoryName(path);
            if (dir != null && !Directory.Exists(dir)) return Usage("--write: the folder " + dir + " does not exist");

            PilotSeedOrder.Verify();
            Console.WriteLine("river golden: writing " + seeds + " seeds of the pilot order (key " + PilotSeedOrder.KeyText + ", from index 0) on " + threads + " thread(s)");
            Console.WriteLine("  build   " + BuildIdentity());
            Stopwatch sw = Stopwatch.StartNew();
            string tmp = path + ".tmp";
            int invariantFailures = 0;
            List<string> stale = new List<string>();
            using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (GZipStream gz = new GZipStream(fs, CompressionLevel.Optimal))
            using (BinaryWriter w = new BinaryWriter(gz, Encoding.UTF8))
            {
                WriteHeader(w, new Header
                {
                    Key = PilotSeedOrder.Key,
                    From = 0,
                    Seeds = seeds,
                    Text = "written " + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) + " by " + BuildIdentity(),
                });
                for (int start = 0; start < seeds; start += threads)
                {
                    int n = Math.Min(threads, seeds - start);
                    SeedRecord[] batch = new SeedRecord[n];
                    Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = threads }, i => batch[i] = Build(start + i, PilotSeedOrder.At(start + i)));
                    foreach (SeedRecord r in batch)
                    {
                        foreach (string bad in Invariants(r))
                        {
                            invariantFailures++;
                            Console.WriteLine("  INVARIANT FAILS  seed #" + r.Index + " (" + r.Seed + "): " + bad);
                        }

                        if (r.Get("a.cache")!.Values[2] == 1)
                        {
                            // The case the cache probes exist for: say that it occurred, and whether it showed.
                            bool shows = !Equal(r.Get("a.probe")!.Values.AsSpan(0, 3).ToArray(), r.Get("e.probe")!.Values.AsSpan(0, 3).ToArray());
                            stale.Add("#" + r.Index + (shows ? " (its first probe reads the stale points)" : ""));
                        }

                        WriteRecord(w, r);
                    }

                    Console.WriteLine("  " + (start + n) + " / " + seeds + " seeds, " + sw.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + " s");
                }
            }

            File.Move(tmp, path, overwrite: true);
            long bytes = new FileInfo(path).Length;
            Console.WriteLine("  wrote   " + path + " (" + (bytes / 1048576.0).ToString("F1", CultureInfo.InvariantCulture) + " MiB, "
                              + (bytes / 1048576.0 / seeds).ToString("F2", CultureInfo.InvariantCulture) + " MiB per seed) in "
                              + sw.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + " s");
            Console.WriteLine("  stale   " + (stale.Count == 0 ? "no seed ends its pre-generation with a stale river cache"
                                                                : "the river cache is stale after pre-generation in seed " + string.Join(", ", stale)));
            if (invariantFailures > 0)
            {
                Console.WriteLine("FAIL  written, but " + invariantFailures + " invariant(s) failed on this build: the handles disagree with each other");
                return 1;
            }

            Console.WriteLine("PASS  written; the five ways into the river points agree on every seed");
            return 0;
        }

        private static int Check(string path, int threads)
        {
            if (!File.Exists(path)) return Usage("--check: no file at " + path);
            Stopwatch sw = Stopwatch.StartNew();
            using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using GZipStream gz = new GZipStream(fs, CompressionMode.Decompress);
            using BinaryReader rd = new BinaryReader(gz, Encoding.UTF8);
            Header h;
            try
            {
                h = ReadHeader(rd);
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
            {
                return Usage("--check: " + path + ": " + ex.Message);
            }

            Console.WriteLine("river golden: checking " + path);
            Console.WriteLine("  golden  " + h.Seeds + " seeds from index " + h.From + " of key 0x" + h.Key.ToString("X16", CultureInfo.InvariantCulture) + "; " + h.Text);
            Console.WriteLine("  now     " + BuildIdentity());
            int differ = 0, corrupt = 0, invariants = 0, same = 0;
            string? first = null;
            for (int start = 0; start < h.Seeds; start += threads)
            {
                int n = Math.Min(threads, h.Seeds - start);
                SeedRecord[] gold = new SeedRecord[n];
                for (int i = 0; i < n; i++) gold[i] = ReadRecord(rd);
                SeedRecord[] now = new SeedRecord[n];
                Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = threads }, i =>
                    now[i] = Build(gold[i].Index, SeedAt(h.Key, gold[i].Index)));
                for (int i = 0; i < n; i++)
                {
                    SeedRecord g = gold[i], c = now[i];
                    string label = "seed #" + g.Index + " (" + g.Seed + ")";
                    if (!Digest(g.Blocks).AsSpan().SequenceEqual(g.Sha256))
                    {
                        corrupt++;
                        Console.WriteLine("  CORRUPT  " + label + ": the golden's own record does not match its SHA-256");
                        continue;
                    }

                    foreach (string bad in Invariants(c))
                    {
                        invariants++;
                        Console.WriteLine("  INVARIANT FAILS  " + label + ": " + bad);
                    }

                    Difference? d = Compare(g, c);
                    if (d == null)
                    {
                        same++;
                        continue;
                    }

                    differ++;
                    string line = label + ": " + d.Text + " (sha256 golden " + Hex(g.Sha256, 16) + ", now " + Hex(c.Sha256, 16) + ")";
                    first ??= line;
                    Console.WriteLine("  DIFFERENT" + (d.OrderOnly ? " (order only)" : "") + "  " + line);
                }

                Console.WriteLine("  " + (start + n) + " / " + h.Seeds + " seeds, " + sw.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + " s");
            }

            if (differ == 0 && corrupt == 0 && invariants == 0)
            {
                Console.WriteLine("PASS  all " + same + " seeds are bit-identical to the golden, cell order included, and the five paths agree");
                return 0;
            }

            Console.WriteLine("FAIL  " + differ + " of " + h.Seeds + " seed(s) differ" + (corrupt > 0 ? ", " + corrupt + " golden record(s) corrupt" : "")
                              + (invariants > 0 ? ", " + invariants + " invariant(s) fail" : "") + (first != null ? "; first: " + first : ""));
            return 1;
        }

        private static int SeedAt(ulong key, long index) => PilotSeedOrder.Plan(key).SeedAt(index % 4294967296L);

        // =============================================================================================
        // --self-test: the round trip, and a planted change reported where it was planted
        // =============================================================================================

        private static int _pass, _fail;

        private static void Ok(bool ok, string name, string detail = "")
        {
            if (ok) _pass++;
            else _fail++;
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (detail.Length > 0 ? " - " + detail : ""));
        }

        private static int SelfTest()
        {
            Console.WriteLine("river golden self-test: two seeds, written, read back, then changed in memory (never in the generator)");
            SeedRecord[] recs = new SeedRecord[2];
            Parallel.For(0, 2, i => recs[i] = Build(i, PilotSeedOrder.At(i)));
            foreach (SeedRecord r in recs)
            {
                List<string> bad = Invariants(r);
                Ok(bad.Count == 0, "seed #" + r.Index + " (" + r.Seed + "): the five ways into the river points agree", bad.Count > 0 ? bad[0] : r.Get("cells")!.Count + " cells, " + r.Get("points")!.Count + " points");
            }

            SeedRecord again = Build(0, PilotSeedOrder.At(0));
            Ok(again.Sha256.AsSpan().SequenceEqual(recs[0].Sha256), "the same seed built twice gives the same record", Hex(again.Sha256, 16));

            string tmp = Path.Combine(Path.GetTempPath(), "seedlab-rivergolden-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".bin");
            try
            {
                using (FileStream fs = new FileStream(tmp, FileMode.Create))
                using (GZipStream gz = new GZipStream(fs, CompressionLevel.Fastest))
                using (BinaryWriter w = new BinaryWriter(gz, Encoding.UTF8))
                {
                    WriteHeader(w, new Header { Key = PilotSeedOrder.Key, Seeds = 2, Text = "self-test" });
                    foreach (SeedRecord r in recs) WriteRecord(w, r);
                }

                SeedRecord[] back = new SeedRecord[2];
                using (FileStream fs = new FileStream(tmp, FileMode.Open))
                using (GZipStream gz = new GZipStream(fs, CompressionMode.Decompress))
                using (BinaryReader rd = new BinaryReader(gz, Encoding.UTF8))
                {
                    Header h = ReadHeader(rd);
                    Ok(h.Seeds == 2 && h.Key == PilotSeedOrder.Key, "the header reads back");
                    for (int i = 0; i < 2; i++) back[i] = ReadRecord(rd);
                }

                for (int i = 0; i < 2; i++)
                {
                    Ok(Compare(back[i], recs[i]) == null && Digest(back[i].Blocks).AsSpan().SequenceEqual(recs[i].Sha256) && back[i].Sha256.AsSpan().SequenceEqual(recs[i].Sha256),
                       "seed #" + i + " round-trips through the file bit for bit, SHA-256 included");
                }
            }
            finally
            {
                try { File.Delete(tmp); }
                catch (Exception) { /* a temporary file */ }
            }

            SeedRecord golden = recs[1];

            // 1. One bit of one point's width, in cell #10, point 3.
            SeedRecord p1 = Copy(golden);
            Block cells = p1.Get("cells")!;
            int before = 0;
            for (int c = 0; c < 10; c++) before += (int)cells.Values[3 * c + 2];
            int count10 = (int)cells.Values[3 * 10 + 2];
            int at = 4 * (before + Math.Min(3, count10 - 1)) + 2;
            p1.Get("points")!.Values[at] ^= 1u;
            p1.Get("sorted")!.Values = SortedDigest(p1.Get("cells")!, p1.Get("points")!);
            p1.Seal();
            Difference? d1 = Compare(golden, p1);
            Ok(d1 != null && d1.Block == "points" && d1.Cell == 10 && d1.PointInCell == Math.Min(3, count10 - 1) && d1.Field == "w" && !d1.OrderOnly,
               "a flipped bit in one point's width is reported as its seed, cell, index and field", d1?.Text ?? "not found");
            Ok(!p1.Sha256.AsSpan().SequenceEqual(golden.Sha256), "... and the seed's SHA-256 changes with it");

            // 2. Cells #5 and #6 swapped in enumeration order, content kept.
            SeedRecord p2 = SwapCells(golden, 5, 6);
            Difference? d2 = Compare(golden, p2);
            Ok(d2 != null && d2.Block == "cells" && d2.Cell == 5 && d2.OrderOnly,
               "two cells listed in the other order are reported as a change of order only", d2?.Text ?? "not found");

            // 3. One lattice height asked of the cold fork.
            SeedRecord p3 = Copy(golden);
            Block probe = p3.Get("e.probe")!;
            int lattice = probe.Count - 1;
            probe.Values[3 * lattice + 2] = F(AsFloat(probe.Values[3 * lattice + 2]) + 1f);
            p3.Seal();
            Difference? d3 = Compare(golden, p3);
            Ok(d3 != null && d3.Block == "e.probe" && d3.Element == lattice && d3.Field == "height" && d3.Text.Contains("(10000, 10000)", StringComparison.Ordinal),
               "a changed height is reported with the handle, the probe and where it was asked", d3?.Text ?? "not found");

            // 4. One cell loses its last point: its count changes before any point is compared.
            SeedRecord p4 = Copy(golden);
            p4.Get("cells")!.Values[3 * 7 + 2] -= 1;
            Difference? d4 = Compare(golden, p4);
            Ok(d4 != null && d4.Block == "cells" && d4.Cell == 7 && d4.Field == "count" && !d4.OrderOnly,
               "a cell with one point fewer is reported as that cell's count, and not as a change of order", d4?.Text ?? "not found");

            // 5. One river's widest width.
            SeedRecord p5 = Copy(golden);
            Block rivers = p5.Get("rivers")!;
            int river = Math.Min(3, rivers.Count - 1);
            rivers.Values[river * RiverFields.Length + 7] ^= 0x10u;
            Difference? d5 = Compare(golden, p5);
            Ok(d5 != null && d5.Block == "rivers" && d5.Element == river && d5.Field == "widthMax", "a river's widthMax is reported as that river and field", d5?.Text ?? "not found");

            // 6. The invariants catch a path that disagrees, whatever the golden says.
            SeedRecord p6 = Copy(golden);
            p6.Get("b.probe")!.Values[0] ^= 1u;
            List<string> inv = Invariants(p6);
            Ok(inv.Count == 1 && inv[0].Contains("lazily", StringComparison.Ordinal), "a lazily pre-generated handle that answers differently breaks an invariant", inv.Count > 0 ? inv[0] : "none");

            Console.WriteLine((_fail == 0 ? "PASS" : "FAIL") + "  river golden self-test: " + _pass + " passed, " + _fail + " failed");
            return _fail == 0 ? 0 : 1;
        }

        private static SeedRecord Copy(SeedRecord r)
        {
            SeedRecord c = new SeedRecord { Index = r.Index, Seed = r.Seed, Sha256 = (byte[])r.Sha256.Clone() };
            foreach (Block b in r.Blocks) c.Blocks.Add(new Block(b.Name, b.Fields, (uint[])b.Values.Clone()));
            return c;
        }

        private static SeedRecord SwapCells(SeedRecord r, int x, int y)
        {
            SeedRecord c = Copy(r);
            Block cells = c.Get("cells")!, points = c.Get("points")!;
            int[] start = new int[cells.Count + 1];
            for (int i = 0; i < cells.Count; i++) start[i + 1] = start[i] + (int)cells.Values[3 * i + 2];
            List<uint> np = new List<uint>(points.Values.Length);
            List<uint> nc = new List<uint>(cells.Values.Length);
            for (int i = 0; i < cells.Count; i++)
            {
                int src = i == x ? y : i == y ? x : i;
                for (int f = 0; f < 3; f++) nc.Add(cells.Values[3 * src + f]);
                for (int p = 4 * start[src]; p < 4 * start[src + 1]; p++) np.Add(points.Values[p]);
            }

            cells.Values = nc.ToArray();
            points.Values = np.ToArray();
            c.Get("sorted")!.Values = SortedDigest(cells, points);
            c.Seal();
            return c;
        }
    }
}
