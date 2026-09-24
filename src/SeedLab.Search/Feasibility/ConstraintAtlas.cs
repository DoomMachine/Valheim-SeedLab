using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace SeedLab.Search.Feasibility
{
    /// <summary>One location type's row in the shipped atlas. Hard fields only, plus a labelled sample.</summary>
    public sealed class AtlasType
    {
        public string Prefab = "";
        public int OrderedIndex = -1;
        public int Quantity;
        public bool Unique;
        public int Attempts;
        public int BiomeArea = -1;
        public double RadialLo;
        public double RadialHi;
        public string[] RadialLoSources = Array.Empty<string>();
        public string[] RadialHiSources = Array.Empty<string>();

        /// <summary>
        /// Non-null when the type can never be placed in any seed. The only such type in 1.0.15 is
        /// <c>GoblinCamp2_1</c>: <c>m_biomeArea</c> is 0 while <c>GetBiomeArea</c> only ever returns
        /// <c>Edge = 1</c> or <c>Median = 2</c>, so <c>PlaceLocations</c>'s
        /// <c>(location.m_biomeArea &amp; biomeArea) == 0</c> is true for every zone in every seed.
        /// This is a decompiled branch condition against a dumped asset field - hard evidence.
        /// </summary>
        public string? Impossible;

        public string? ImpossibleWhy;

        /// <summary>The 240-seed distance sample, or null. SAMPLE: warnings only.</summary>
        public AtlasSample? Sample;
    }

    /// <summary>A measured distance summary. Never sufficient for a refusal.</summary>
    public sealed class AtlasSample
    {
        public int N;
        public double Min, P5, Median, P95, Max;
        public int SeedsWithShortfall = -1;
    }

    /// <summary>One measured or proved absence row (rule A1).</summary>
    public sealed class AtlasAbsence
    {
        public string Prefab = "";

        /// <summary>"proof" or "measured". A "proof" row may refuse; a "measured" row may only warn.</summary>
        public string Kind = "measured";

        public int N;
        public int SeedsShortOfQuantity = -1;
        public int SeedsWithNone = -1;
        public string Note = "";
        public string Why = "";
    }

    /// <summary>
    /// The shipped constraint atlas: the per-type hard geometry the checker reasons about, the
    /// one-per-zone caps, and the labelled samples it is allowed to warn from.
    ///
    /// <para><b>It is a cache and a document, not the authority.</b> Every hard field is recomputable
    /// from <c>locations.json</c> plus the decompiled bands, and the checker recomputes
    /// <c>radialLo</c>/<c>radialHi</c> from the live <c>LocationTypeInfo</c> and compares
    /// (<see cref="CheckerSelfTest"/> T4). On a disagreement the live value wins and the mismatch is
    /// a loud warning: a stale atlas is precisely how a false refusal would ship.</para>
    ///
    /// <para><b>It carries the DATA-STAMP.</b> When the atlas stamp and the game data's stamp
    /// disagree, or the atlas is missing, <b>no REFUSE is issued at all</b> - every would-be refusal
    /// becomes a warning naming the mismatch. A game update is exactly the case where a hard bound
    /// quietly stops being hard, so the tool becomes less helpful rather than wrong.</para>
    ///
    /// <para>It is found the same way <c>SeedLab.Data.GameData</c> finds its dump folder - walking up
    /// from the working directory and from the binary looking for
    /// <c>data\&lt;version&gt;-&lt;hash&gt;\constraint-atlas.json</c> - so that this assembly keeps
    /// its property of referencing neither <c>SeedLab.Data</c> nor <c>SeedLab.Locations</c>.</para>
    /// </summary>
    public sealed class ConstraintAtlas
    {
        public const string FileName = "constraint-atlas.json";

        /// <summary>Same override the rest of the tool uses, so one variable moves all the data.</summary>
        public const string DirectoryEnvironmentVariable = "SEEDLAB_DATA_DIR";

        private readonly Dictionary<string, AtlasType> _byPrefab =
            new Dictionary<string, AtlasType>(StringComparer.Ordinal);

        private readonly Dictionary<string, AtlasAbsence> _absence =
            new Dictionary<string, AtlasAbsence>(StringComparer.Ordinal);

        private ConstraintAtlas()
        {
        }

        /// <summary>The DATA-STAMP line the atlas was built from. Empty when the atlas is missing.</summary>
        public string Stamp { get; private set; } = "";

        public int AtlasVersion { get; private set; }

        public bool Available { get; private set; }

        /// <summary>Why it is not available, in one sentence. Empty when it is.</summary>
        public string UnavailableReason { get; private set; } = "";

        public string Path { get; private set; } = "";

        public int TypesCovered { get; private set; }

        public int InstancesValidated { get; private set; }

        public double WaterEdge { get; private set; } = 10500.0;

        public double ZoneSize { get; private set; } = 64.0;

        /// <summary>Total <c>m_quantity</c> over every running type - the whole-world count ceiling.</summary>
        public int TotalQuantity { get; private set; }

        /// <summary>
        /// Radius (m) to the number of 64 m zones whose square INTERSECTS the disc. That is the
        /// over-approximating choice on purpose: a zone-centre test gives 767 at R = 1,000 where the
        /// intersection test gives 837, and the smaller number would refuse legal queries.
        /// </summary>
        public IReadOnlyDictionary<double, int> ZonesWithin { get; private set; }
            = new Dictionary<double, int>();

        public string OnePerZoneEvidence { get; private set; } = "";

        public IReadOnlyCollection<AtlasType> Types => _byPrefab.Values;

        public AtlasType? TypeOf(string prefab)
            => _byPrefab.TryGetValue(prefab, out AtlasType? t) ? t : null;

        public AtlasAbsence? AbsenceOf(string prefab)
            => _absence.TryGetValue(prefab, out AtlasAbsence? a) ? a : null;

        /// <summary>
        /// The number of zones that intersect a disc of <paramref name="radius"/>, interpolated
        /// UPWARDS from the shipped table so the bound stays an over-approximation between rows, and
        /// computed directly when the radius is outside it.
        /// </summary>
        public int ZonesIntersecting(double radius)
        {
            if (radius <= 0) return 0;
            foreach (KeyValuePair<double, int> kv in ZonesWithin)
            {
                if (Math.Abs(kv.Key - radius) < 1e-9) return kv.Value;
            }

            // The direct count, same rule as the table: a zone counts when its 64 m square meets the
            // disc. Cheap (the whole world is 329 x 329 zones) and never smaller than the truth.
            double z = ZoneSize;
            int span = (int)Math.Ceiling(radius / z) + 2;
            int n = 0;
            for (int zx = -span; zx <= span; zx++)
            {
                for (int zy = -span; zy <= span; zy++)
                {
                    double cx = zx * z, cy = zy * z;
                    double dx = Math.Max(0.0, Math.Abs(cx) - z / 2.0);
                    double dy = Math.Max(0.0, Math.Abs(cy) - z / 2.0);
                    if (dx * dx + dy * dy <= radius * radius) n++;
                }
            }

            return n;
        }

        /// <summary>The 8-character build tag of the stamp, e.g. <c>1.0.15 / 59f53fb5</c>.</summary>
        public string BuildTag => BuildTagOf(Stamp);

        /// <summary>
        /// The build tag inside a DATA-STAMP line, or empty when it holds neither key. The
        /// <c>ILocationOracle.Provenance</c> string is written in exactly this shape, which is how
        /// the two stamps are compared without this assembly taking a dependency on
        /// <c>SeedLab.Data</c>.
        /// </summary>
        public static string BuildTagOf(string stamp)
        {
            if (string.IsNullOrEmpty(stamp)) return "";
            string version = Token(stamp, "game-version="), sha = Token(stamp, "assembly_valheim-sha256=");
            if (version.Length == 0 || sha.Length < 8) return "";
            return version + " / " + sha.Substring(0, 8);
        }

        private static string Token(string s, string key)
        {
            int i = s.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return "";
            int j = i + key.Length, e = j;
            while (e < s.Length && s[e] != ' ') e++;
            return s.Substring(j, e - j);
        }

        private static ConstraintAtlas? _cached;
        private static readonly object Gate = new object();

        /// <summary>Loads the atlas once per process, and never throws: a missing atlas is a state.</summary>
        public static ConstraintAtlas Load()
        {
            lock (Gate)
            {
                if (_cached != null) return _cached;
                _cached = LoadUncached(FindPath());
                return _cached;
            }
        }

        /// <summary>For the self-test: load a named file without touching the process-wide cache.</summary>
        public static ConstraintAtlas LoadFrom(string path) => LoadUncached(path);

        private static ConstraintAtlas LoadUncached(string? path)
        {
            ConstraintAtlas a = new ConstraintAtlas();
            if (path == null || !File.Exists(path))
            {
                a.UnavailableReason =
                    "the constraint atlas (" + FileName + ") was not found beside the dumped game "
                    + "data. Without it the checker cannot refuse anything that rests on a per-type "
                    + "asset field, so every such refusal is downgraded to a warning. It ships in "
                    + "data\\<version>-<hash>\\; set " + DirectoryEnvironmentVariable + " or run from "
                    + "inside the SeedLab tree";
                return a;
            }

            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                JsonElement root = doc.RootElement;
                a.Path = path;
                a.Stamp = Str(root, "stamp");
                a.AtlasVersion = Int(root, "atlasVersion");
                a.TypesCovered = Int(root, "typesCovered");
                a.InstancesValidated = Int(root, "instancesValidated");

                if (root.TryGetProperty("globalBounds", out JsonElement gb))
                {
                    a.WaterEdge = Dbl(gb, "waterEdge", 10500.0);
                    a.ZoneSize = Dbl(gb, "zoneSize", 64.0);
                    a.TotalQuantity = Int(gb, "totalQuantity");
                    if (gb.TryGetProperty("onePerZone", out JsonElement opz))
                    {
                        a.OnePerZoneEvidence = Str(opz, "evidence");
                        Dictionary<double, int> zones = new Dictionary<double, int>();
                        if (opz.TryGetProperty("maxWithin", out JsonElement mw))
                        {
                            foreach (JsonProperty p in mw.EnumerateObject())
                            {
                                if (double.TryParse(p.Name, NumberStyles.Float, CultureInfo.InvariantCulture,
                                                    out double r))
                                {
                                    zones[r] = p.Value.GetInt32();
                                }
                            }
                        }

                        a.ZonesWithin = zones;
                    }
                }

                if (root.TryGetProperty("locations", out JsonElement locs))
                {
                    foreach (JsonElement e in locs.EnumerateArray())
                    {
                        AtlasType t = new AtlasType
                        {
                            Prefab = Str(e, "prefabName"),
                            OrderedIndex = Int(e, "orderedIndex", -1),
                            Quantity = Int(e, "quantity"),
                            Unique = Bool(e, "unique"),
                            Attempts = Int(e, "attempts"),
                            BiomeArea = Int(e, "biomeArea", -1),
                            RadialLo = Dbl(e, "radialLo", 0.0),
                            RadialHi = Dbl(e, "radialHi", 10500.0),
                            RadialLoSources = Strings(e, "radialLoSources"),
                            RadialHiSources = Strings(e, "radialHiSources"),
                        };

                        string imp = Str(e, "impossible");
                        if (imp.Length > 0)
                        {
                            t.Impossible = imp;
                            t.ImpossibleWhy = Str(e, "impossibleWhy");
                        }

                        if (e.TryGetProperty("observed", out JsonElement o) && Int(o, "n") > 0)
                        {
                            t.Sample = new AtlasSample
                            {
                                N = Int(o, "n"),
                                Min = Dbl(o, "minD", double.NaN),
                                P5 = Dbl(o, "p5", double.NaN),
                                Median = Dbl(o, "median", double.NaN),
                                P95 = Dbl(o, "p95", double.NaN),
                                Max = Dbl(o, "maxD", double.NaN),
                            };
                        }

                        if (t.Prefab.Length > 0) a._byPrefab[t.Prefab] = t;
                    }
                }

                // The per-seed distance sample is a better warning source than the three-world one.
                if (root.TryGetProperty("sampling", out JsonElement sm)
                    && sm.TryGetProperty("byType", out JsonElement bt))
                {
                    foreach (JsonProperty p in bt.EnumerateObject())
                    {
                        if (!a._byPrefab.TryGetValue(p.Name, out AtlasType? t)) continue;
                        t.Sample = new AtlasSample
                        {
                            N = Int(p.Value, "n"),
                            Min = Dbl(p.Value, "min", double.NaN),
                            P5 = Dbl(p.Value, "p5", double.NaN),
                            Median = Dbl(p.Value, "median", double.NaN),
                            P95 = Dbl(p.Value, "p95", double.NaN),
                            Max = Dbl(p.Value, "max", double.NaN),
                            SeedsWithShortfall = Int(p.Value, "seedsWithShortfall", -1),
                        };
                    }
                }

                if (root.TryGetProperty("absence", out JsonElement ab))
                {
                    ReadAbsence(a, ab, "proved", "proof");
                    ReadAbsence(a, ab, "measured", "measured");
                }

                a.Available = a._byPrefab.Count > 0 && a.Stamp.Length > 0;
                if (!a.Available)
                {
                    a.UnavailableReason = path + " parsed but holds no stamped location rows";
                }

                return a;
            }
            catch (Exception ex) when (ex is JsonException || ex is IOException
                                       || ex is UnauthorizedAccessException || ex is FormatException)
            {
                a.UnavailableReason = "the constraint atlas at " + path + " could not be read ("
                                      + ex.Message + "), so no refusal that rests on it will be issued";
                return a;
            }
        }

        private static void ReadAbsence(ConstraintAtlas a, JsonElement ab, string array, string kind)
        {
            if (!ab.TryGetProperty(array, out JsonElement rows)) return;
            foreach (JsonElement e in rows.EnumerateArray())
            {
                AtlasAbsence row = new AtlasAbsence
                {
                    Prefab = Str(e, "prefab"),
                    Kind = kind,
                    N = Int(e, "n"),
                    SeedsShortOfQuantity = Int(e, "seedsShortOfQuantity", -1),
                    SeedsWithNone = Int(e, "seedsWithNone", -1),
                    Note = Str(e, "note"),
                    Why = Str(e, "why"),
                };
                if (row.Prefab.Length > 0) a._absence[row.Prefab] = row;
            }
        }

        private static string? FindPath()
        {
            string? env = Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable);
            if (!string.IsNullOrEmpty(env))
            {
                string direct = System.IO.Path.Combine(env, FileName);
                if (File.Exists(direct)) return direct;
                string? inside = PickFrom(env);
                if (inside != null) return inside;
            }

            foreach (string start in new[] { SafeCurrentDirectory(), AppContext.BaseDirectory })
            {
                if (start.Length == 0) continue;
                DirectoryInfo? d = new DirectoryInfo(start);
                for (int i = 0; i < 12 && d != null; i++, d = d.Parent)
                {
                    string? found = PickFrom(System.IO.Path.Combine(d.FullName, "data"));
                    if (found != null) return found;
                }
            }

            return null;
        }

        private static string? PickFrom(string dataDirectory)
        {
            try
            {
                if (!Directory.Exists(dataDirectory)) return null;
                foreach (string d in Directory.GetDirectories(dataDirectory))
                {
                    string cand = System.IO.Path.Combine(d, FileName);
                    if (File.Exists(cand)) return cand;
                }
            }
            catch (Exception)
            {
                return null;
            }

            return null;
        }

        private static string SafeCurrentDirectory()
        {
            try { return Directory.GetCurrentDirectory(); }
            catch (Exception) { return ""; }
        }

        private static string Str(JsonElement e, string name)
            => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? "" : "";

        private static int Int(JsonElement e, string name, int fallback = 0)
            => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number
                ? v.GetInt32() : fallback;

        private static double Dbl(JsonElement e, string name, double fallback = 0.0)
            => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number
                ? v.GetDouble() : fallback;

        private static bool Bool(JsonElement e, string name)
            => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;

        private static string[] Strings(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            List<string> all = new List<string>();
            foreach (JsonElement i in v.EnumerateArray())
            {
                if (i.ValueKind == JsonValueKind.String) all.Add(i.GetString() ?? "");
            }

            return all.ToArray();
        }
    }
}
