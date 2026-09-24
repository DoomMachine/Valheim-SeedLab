using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SeedLab.Contracts.Dump;

namespace SeedLab.Data
{
    /// <summary>One <c>goldens\worldgen-&lt;seedHex&gt;-&lt;source&gt;.json</c>, identified.</summary>
    public readonly struct WorldGenGoldenId : IEquatable<WorldGenGoldenId>
    {
        public WorldGenGoldenId(string seedHex, string source)
        {
            SeedHex = seedHex;
            Source = source;
        }

        /// <summary>The seed as 8 uppercase hex digits, e.g. <c>965BE652</c>.</summary>
        public string SeedHex { get; }

        /// <summary><c>world</c> (the generator the loaded world was running) or <c>menu</c>.</summary>
        public string Source { get; }

        /// <summary>The seed as the int32 the generator actually used.</summary>
        public int Seed => unchecked((int)uint.Parse(SeedHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));

        public bool Equals(WorldGenGoldenId other)
            => string.Equals(SeedHex, other.SeedHex, StringComparison.OrdinalIgnoreCase)
               && string.Equals(Source, other.Source, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is WorldGenGoldenId o && Equals(o);
        public override int GetHashCode() => HashCode.Combine(SeedHex.ToUpperInvariant(), Source);
        public override string ToString() => SeedHex + "-" + Source;
    }

    /// <summary>One <c>Mathf.PerlinNoise</c> sample, as the bit patterns the game produced.</summary>
    public readonly struct PerlinSample
    {
        public PerlinSample(uint xBits, uint yBits, uint resultBits)
        {
            XBits = xBits; YBits = yBits; ResultBits = resultBits;
        }

        public uint XBits { get; }
        public uint YBits { get; }
        public uint ResultBits { get; }

        public float X => BitConverter.Int32BitsToSingle(unchecked((int)XBits));
        public float Y => BitConverter.Int32BitsToSingle(unchecked((int)YBits));
        public float Result => BitConverter.Int32BitsToSingle(unchecked((int)ResultBits));
    }

    /// <summary>One <c>WorldGenerator.m_riverPoints</c> point: position, width, half width.</summary>
    public readonly struct RiverPoint
    {
        public RiverPoint(float x, float y, float w, float w2) { X = x; Y = y; W = w; W2 = w2; }

        public float X { get; }
        public float Y { get; }
        public float W { get; }
        public float W2 { get; }
    }

    /// <summary>One cell of the river-point dictionary, keyed by its grid coordinate.</summary>
    public sealed class RiverPointCell
    {
        internal RiverPointCell(int gx, int gy, RiverPoint[] points) { Gx = gx; Gy = gy; Points = points; }

        public int Gx { get; }
        public int Gy { get; }
        public RiverPoint[] Points { get; }
    }

    /// <summary>The header of a river-point sidecar, plus its cells.</summary>
    public sealed class RiverPointsGolden
    {
        internal RiverPointsGolden(int seed, float gridSize, RiverPointCell[] cells, int pointCount)
        {
            Seed = seed; GridSize = gridSize; Cells = cells; PointCount = pointCount;
        }

        public int Seed { get; }
        public float GridSize { get; }
        public RiverPointCell[] Cells { get; }
        public int PointCount { get; }
    }

    /// <summary>
    /// <c>Mathf.FloatToHalf</c> evidence with the exact bit patterns. The decimal in the JSON cannot
    /// carry a NaN payload - the game's <c>HalfToFloat(FloatToHalf(NaN))</c> comes back as
    /// <c>0xFFE00000</c>, not the <c>0xFFC00000</c> that went in - so a port that compares half codes
    /// must compare these, not the decimals.
    /// </summary>
    public readonly struct HalfSampleBits
    {
        public HalfSampleBits(string? note, uint valueBits, int half, uint backBits)
        {
            Note = note; ValueBits = valueBits; Half = half; BackBits = backBits;
        }

        public string? Note { get; }
        public uint ValueBits { get; }

        /// <summary><c>Mathf.FloatToHalf(value)</c> as an unsigned 16-bit value widened to int.</summary>
        public int Half { get; }

        public uint BackBits { get; }

        public float Value => BitConverter.Int32BitsToSingle(unchecked((int)ValueBits));
        public float Back => BitConverter.Int32BitsToSingle(unchecked((int)BackBits));
    }

    /// <summary>
    /// The recorded outputs of the running game: what the port is checked against.
    ///
    /// <para>Everything here is loaded on demand and then cached, because the set is 48 MB and no
    /// single command needs all of it. The two binary sidecars
    /// (<c>natives-perlin.bin</c>, 262,780 samples; <c>worldgen-*-riverpoints.bin</c>, 764,577 points
    /// for the world dump) are raw float32, never decimal text: one ulp in a Perlin argument moves a
    /// biome boundary, and a decimal round trip is not bit-exact in every reader.</para>
    /// </summary>
    public sealed class Goldens
    {
        private readonly GameData _data;
        private readonly object _gate = new object();
        private readonly Dictionary<string, object> _cache = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        private IReadOnlyList<WorldGenGoldenId>? _worldGenIds;

        internal Goldens(GameData data) { _data = data; }

        // ---- natives ------------------------------------------------------------------------------

        /// <summary><c>UnityEngine.Random</c>: 268 InitState seeds and 276 ordered call traces.</summary>
        public NativesRandomFile Random() => Json(
            DumpFormat.GoldensDir + "/" + DumpFormat.NativesRandomFile,
            DumpSchemas.NativesRandomFile, DumpJsonContext.Default.NativesRandomFile, f => f.stamp);

        /// <summary>The index of <c>natives-perlin.bin</c>. The samples themselves come from
        /// <see cref="ReadPerlin"/>.</summary>
        public NativesPerlinIndexFile PerlinIndex() => Json(
            DumpFormat.GoldensDir + "/" + DumpFormat.NativesPerlinIndexFile,
            DumpSchemas.NativesPerlinIndexFile, DumpJsonContext.Default.NativesPerlinIndexFile, f => f.stamp);

        /// <summary>Mono's <c>Math.Sin/Cos/Atan2/Pow</c>, and <c>WorldGenerator.WorldAngle</c> itself.</summary>
        public NativesLibmFile Libm() => Json(
            DumpFormat.GoldensDir + "/" + DumpFormat.NativesLibmFile,
            DumpSchemas.NativesLibmFile, DumpJsonContext.Default.NativesLibmFile, f => f.stamp);

        /// <summary><c>Mathf.FloatToHalf</c> over an adversarial float set.</summary>
        public NativesHalfFile Half() => Json(
            DumpFormat.GoldensDir + "/" + DumpFormat.NativesHalfFile,
            DumpSchemas.NativesHalfFile, DumpJsonContext.Default.NativesHalfFile, f => f.stamp);

        /// <summary><c>GetStableHashCode</c> over every prefab name, alt-biome name and seed text.</summary>
        public NativesHashFile Hash() => Json(
            DumpFormat.GoldensDir + "/" + DumpFormat.NativesHashFile,
            DumpSchemas.NativesHashFile, DumpJsonContext.Default.NativesHashFile, f => f.stamp);

        /// <summary>
        /// The same half samples as <see cref="Half"/>, but carrying the raw <c>value</c> and
        /// <c>back</c> bit patterns from the file's <c>"bits"</c> objects.
        /// </summary>
        public IReadOnlyList<HalfSampleBits> HalfBits()
        {
            const string rel = DumpFormat.GoldensDir + "/" + DumpFormat.NativesHalfFile;
            lock (_gate)
            {
                if (_cache.TryGetValue("half-bits", out object? hit)) return (IReadOnlyList<HalfSampleBits>)hit;

                string path = _data.PathOf(rel);
                byte[] bytes = ReadVerified(rel);
                StrictJson.Validate(bytes, DumpSchemas.NativesHalfFile, path);

                List<HalfSampleBits> list = new List<HalfSampleBits>();
                using (JsonDocument doc = JsonDocument.Parse(bytes))
                {
                    JsonElement samples = doc.RootElement.GetProperty("samples");
                    foreach (JsonElement s in samples.EnumerateArray())
                    {
                        JsonElement bits = s.GetProperty("bits");
                        list.Add(new HalfSampleBits(
                            s.GetProperty("note").GetString(),
                            ParseHex32(bits.GetProperty("value").GetString(), path, "value"),
                            s.GetProperty("half").GetInt32(),
                            ParseHex32(bits.GetProperty("back").GetString(), path, "back")));
                    }
                }

                _cache["half-bits"] = list;
                return list;
            }
        }

        /// <summary>
        /// The samples of one block of <c>natives-perlin.bin</c>, as raw bit patterns.
        /// The .bin has no per-block header: a block is located only by the
        /// <c>byteOffset</c>/<c>sampleCount</c> in <see cref="PerlinIndex"/>.
        /// </summary>
        public PerlinSample[] ReadPerlin(PerlinBlockDef block)
        {
            if (block == null) throw new ArgumentNullException(nameof(block));

            NativesPerlinIndexFile index = PerlinIndex();
            string binName = index.binFile ?? DumpFormat.NativesPerlinBinFile;
            string rel = DumpFormat.GoldensDir + "/" + binName;
            string path = _data.PathOf(rel);
            byte[] bytes = ReadVerified(rel);

            if (bytes.Length < 12
                || Encoding.ASCII.GetString(bytes, 0, 4) != DumpFormat.PerlinMagic)
            {
                throw new GameDataException(
                    path + ": the file does not start with the magic " + DumpFormat.PerlinMagic + ". "
                    + StrictJson.ReDumpHint)
                { File = path };
            }

            if (index.binSha256 != null)
            {
                string actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (!string.Equals(actual, index.binSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new GameDataException(
                        path + ": SHA-256 " + actual + " does not match the " + binName
                        + " hash recorded in the index. " + StrictJson.ReDumpHint)
                    { File = path };
                }
            }

            long start = block.byteOffset;
            long need = (long)block.sampleCount * 12L;
            if (start < 12 || start + need > bytes.Length)
            {
                throw new GameDataException(
                    path + ": block '" + (block.id ?? "?") + "' wants " + need.ToString(CultureInfo.InvariantCulture)
                    + " bytes at offset " + start.ToString(CultureInfo.InvariantCulture) + " and the file is "
                    + bytes.Length.ToString(CultureInfo.InvariantCulture) + " bytes. " + StrictJson.ReDumpHint)
                { File = path };
            }

            PerlinSample[] samples = new PerlinSample[block.sampleCount];
            ReadOnlySpan<byte> span = bytes;
            for (int i = 0; i < samples.Length; i++)
            {
                int o = (int)start + i * 12;
                samples[i] = new PerlinSample(
                    BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(o, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(o + 4, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(o + 8, 4)));
            }

            return samples;
        }

        // ---- per-seed goldens ----------------------------------------------------------------------

        /// <summary>Every <c>worldgen-&lt;seedHex&gt;-&lt;source&gt;.json</c> in this dump.</summary>
        public IReadOnlyList<WorldGenGoldenId> WorldGenIds()
        {
            if (_worldGenIds != null) return _worldGenIds;
            lock (_gate)
            {
                if (_worldGenIds != null) return _worldGenIds;

                List<WorldGenGoldenId> ids = new List<WorldGenGoldenId>();
                if (_data.Manifest.files != null)
                {
                    foreach (FileEntryDef f in _data.Manifest.files)
                    {
                        string? p = f.path;
                        if (p == null) continue;

                        const string prefix = DumpFormat.GoldensDir + "/worldgen-";
                        if (!p.StartsWith(prefix, StringComparison.Ordinal)) continue;
                        if (!p.EndsWith(".json", StringComparison.Ordinal)) continue;

                        string stem = p.Substring(prefix.Length, p.Length - prefix.Length - 5);
                        int dash = stem.IndexOf('-');
                        if (dash <= 0) continue;

                        ids.Add(new WorldGenGoldenId(stem.Substring(0, dash), stem.Substring(dash + 1)));
                    }
                }

                ids.Sort((a, b) =>
                {
                    int c = string.CompareOrdinal(a.SeedHex, b.SeedHex);
                    return c != 0 ? c : string.CompareOrdinal(a.Source, b.Source);
                });

                return _worldGenIds = ids;
            }
        }

        /// <summary>The private state of one <c>WorldGenerator</c>: the five offsets, the two river
        /// seeds, the lakes, rivers and streams.</summary>
        public WorldGenDumpFile WorldGen(WorldGenGoldenId id) => Json(
            DumpFormat.WorldGenFile(id.SeedHex, id.Source),
            DumpSchemas.WorldGenDumpFile, DumpJsonContext.Default.WorldGenDumpFile, f => f.stamp);

        /// <summary>The rendered river point set for one seed.</summary>
        public RiverPointsGolden RiverPoints(WorldGenGoldenId id)
        {
            string rel = DumpFormat.RiverPointsFile(id.SeedHex, id.Source);
            string path = _data.PathOf(rel);

            lock (_gate)
            {
                if (_cache.TryGetValue(rel, out object? hit)) return (RiverPointsGolden)hit;

                byte[] bytes = ReadVerified(rel);
                if (bytes.Length < 20 || Encoding.ASCII.GetString(bytes, 0, 4) != DumpFormat.RiverPointsMagic)
                {
                    throw new GameDataException(
                        path + ": the file does not start with the magic " + DumpFormat.RiverPointsMagic + ". "
                        + StrictJson.ReDumpHint)
                    { File = path };
                }

                ReadOnlySpan<byte> s = bytes;
                int seed = BinaryPrimitives.ReadInt32LittleEndian(s.Slice(8, 4));
                float gridSize = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(s.Slice(12, 4)));
                int cellCount = BinaryPrimitives.ReadInt32LittleEndian(s.Slice(16, 4));

                if (cellCount < 0)
                {
                    throw new GameDataException(path + ": the cell count is negative. " + StrictJson.ReDumpHint)
                    { File = path };
                }

                RiverPointCell[] cells = new RiverPointCell[cellCount];
                int total = 0;
                int o = 20;
                for (int c = 0; c < cellCount; c++)
                {
                    if (o + 12 > bytes.Length) throw Truncated(path);

                    int gx = BinaryPrimitives.ReadInt32LittleEndian(s.Slice(o, 4));
                    int gy = BinaryPrimitives.ReadInt32LittleEndian(s.Slice(o + 4, 4));
                    int n = BinaryPrimitives.ReadInt32LittleEndian(s.Slice(o + 8, 4));
                    o += 12;

                    if (n < 0 || o + (long)n * 16L > bytes.Length) throw Truncated(path);

                    RiverPoint[] points = new RiverPoint[n];
                    for (int i = 0; i < n; i++)
                    {
                        int b = o + i * 16;
                        points[i] = new RiverPoint(
                            F(s, b), F(s, b + 4), F(s, b + 8), F(s, b + 12));
                    }

                    o += n * 16;
                    total += n;
                    cells[c] = new RiverPointCell(gx, gy, points);
                }

                if (o != bytes.Length)
                {
                    throw new GameDataException(
                        path + ": " + (bytes.Length - o).ToString(CultureInfo.InvariantCulture)
                        + " bytes are left over after " + cellCount.ToString(CultureInfo.InvariantCulture)
                        + " cells, so the file is not the format this reader expects. " + StrictJson.ReDumpHint)
                    { File = path };
                }

                RiverPointsGolden golden = new RiverPointsGolden(seed, gridSize, cells, total);
                _cache[rel] = golden;
                return golden;
            }
        }

        /// <summary>
        /// The 12,228 location instances the game's own placement run produced for the fresh world
        /// <c>0480A34C</c> - candidates included, before <c>RemoveUnplacedLocations</c> pruned them.
        /// </summary>
        public LocationInstancesFile LocationInstances(string seedHex) => Json(
            DumpFormat.GoldensDir + "/locationinstances-" + seedHex + ".json",
            DumpSchemas.LocationInstancesFile, DumpJsonContext.Default.LocationInstancesFile, f => f.stamp);

        /// <summary>Which sectors each alt biome was assigned, and the sector grid it chose from.</summary>
        public AltBiomeAssignmentFile AltBiomeAssignment(string seedHex) => Json(
            DumpFormat.GoldensDir + "/altbiomes-assignment-" + seedHex + ".json",
            DumpSchemas.AltBiomeAssignmentFile, DumpJsonContext.Default.AltBiomeAssignmentFile, f => f.stamp);

        /// <summary>True when this dump carries that golden.</summary>
        public bool Has(string relativeToDump) => File.Exists(_data.PathOf(relativeToDump));

        // ---- plumbing ------------------------------------------------------------------------------

        private T Json<T>(string rel, DumpSchema schema,
                          System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info,
                          Func<T, string?> stampOf) where T : class
        {
            lock (_gate)
            {
                if (_cache.TryGetValue(rel, out object? hit)) return (T)hit;

                T loaded = _data.LoadFile(rel, schema, info);
                _data.CheckStamp(stampOf(loaded), rel);
                _cache[rel] = loaded;
                return loaded;
            }
        }

        private byte[] ReadVerified(string rel)
        {
            string path = _data.PathOf(rel);
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                throw new GameDataException(
                    path + ": could not be read (" + ex.GetType().Name + ": " + ex.Message + "). "
                    + StrictJson.ReDumpHint, ex)
                { File = path };
            }

            string? expected = _data.ShaOf(rel);
            if (expected != null)
            {
                string actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new GameDataException(
                        path + ": SHA-256 " + actual + " does not match the manifest's " + expected
                        + ". " + StrictJson.ReDumpHint)
                    { File = path };
                }

                GameData.NoteVerified(path);
            }

            return bytes;
        }

        private static float F(ReadOnlySpan<byte> s, int offset)
            => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(s.Slice(offset, 4)));

        private static uint ParseHex32(string? hex, string path, string what)
        {
            ReadOnlySpan<char> d = (hex ?? "").AsSpan();
            if (d.StartsWith("0x".AsSpan(), StringComparison.OrdinalIgnoreCase)) d = d.Slice(2);

            if (!uint.TryParse(d, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v))
            {
                throw new GameDataException(
                    path + ": bits." + what + " is '" + (hex ?? "null") + "', not a 32-bit hex pattern.")
                { File = path, Field = "bits." + what };
            }

            return v;
        }

        private static GameDataException Truncated(string path)
            => new GameDataException(path + ": the file ends in the middle of a cell. " + StrictJson.ReDumpHint)
            { File = path };
    }
}
