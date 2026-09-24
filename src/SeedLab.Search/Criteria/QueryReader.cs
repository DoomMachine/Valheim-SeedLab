using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SeedLab.Search.Criteria
{
    /// <summary>A problem in the query file, reported with the JSON path that caused it.</summary>
    public sealed class QueryException : Exception
    {
        public QueryException(string path, string message, string? hint = null)
            : base(path.Length == 0 ? message : path + ": " + message)
        {
            Path = path;
            Hint = hint;
        }

        public string Path { get; }
        public string? Hint { get; }
    }

    /// <summary>
    /// Reads a query file. <b>Unknown keys are errors, never warnings</b> - a typo in
    /// <c>"importance"</c> must not silently turn a must-have into nothing at all
    /// (07-features.md section 5.3).
    ///
    /// <para>JSON only. The spec offered YAML "as a superset"; a hand-rolled YAML parser is a bug farm
    /// and there is no package budget, so the file is JSON with a documented schema, which is readable
    /// and writable by hand and which every editor already validates. <c>//</c> and <c>/* */</c>
    /// comments and trailing commas are accepted, so a query file can be annotated.</para>
    /// </summary>
    public static class QueryReader
    {
        private static readonly JsonDocumentOptions Options = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        public static Query Parse(string json, string? originName = null)
        {
            using JsonDocument doc = Parse(json, originName, out _);
            return Build(doc.RootElement, json);
        }

        private static JsonDocument Parse(string json, string? originName, out string origin)
        {
            origin = originName ?? "<query>";
            try
            {
                return JsonDocument.Parse(json, Options);
            }
            catch (JsonException ex)
            {
                throw new QueryException("", "not valid JSON (" + ex.Message + ")",
                    "the query language is JSON; // and /* */ comments and trailing commas are allowed");
            }
        }

        private static Query Build(JsonElement root, string rawJson)
        {
            Expect(root, JsonValueKind.Object, "");
            Query q = new Query();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (JsonProperty p in root.EnumerateObject())
            {
                seen.Add(p.Name);
                switch (p.Name)
                {
                    case "version": q.Version = Int(p.Value, "version"); break;
                    case "defs": q.Defs = Int(p.Value, "defs"); break;
                    case "name": q.Name = Str(p.Value, "name"); break;
                    case "description": q.Description = Str(p.Value, "description"); break;
                    case "world": ReadWorld(p.Value, q.World); break;
                    case "search": ReadSearch(p.Value, q.Search); break;
                    case "output": ReadOutput(p.Value, q.Output); break;
                    case "goals": ReadGoals(p.Value, q.Goals); break;
                    default:
                        throw new QueryException(p.Name, "unknown key",
                            "the top level takes: version, defs, name, description, world, search, output, goals");
                }
            }

            if (q.Version != 1)
            {
                throw new QueryException("version", "this build reads query version 1, not " + q.Version);
            }

            if (q.Defs != 1)
            {
                throw new QueryException("defs", "this build implements metric definitions version 1, not " + q.Defs,
                    "a results file from a different 'defs' is not comparable with this one");
            }

            if (!seen.Contains("goals") || q.Goals.Count == 0)
            {
                throw new QueryException("goals", "a query needs at least one goal");
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (Goal g in q.Goals)
            {
                if (!ids.Add(g.Id)) throw new QueryException("goals", "two goals share the id '" + g.Id + "'");
            }

            q.CanonicalJson = Canonicalise(q);
            return q;
        }

        private static void ReadWorld(JsonElement e, WorldSpec w)
        {
            Expect(e, JsonValueKind.Object, "world");
            foreach (JsonProperty p in e.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "gen_version": w.GenVersion = Int(p.Value, "world.gen_version"); break;
                    default: throw new QueryException("world." + p.Name, "unknown key", "world takes: gen_version");
                }
            }

            if (w.GenVersion < 0 || w.GenVersion > 2)
            {
                throw new QueryException("world.gen_version", "must be 0, 1 or 2 (WorldGenerator.VersionSetup)");
            }
        }

        private static void ReadSearch(JsonElement e, SearchSpec s)
        {
            Expect(e, JsonValueKind.Object, "search");
            foreach (JsonProperty p in e.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "order":
                        s.Order = Str(p.Value, "search.order") switch
                        {
                            "shuffled" => ScanOrder.Shuffled,
                            "sequential" => ScanOrder.Sequential,
                            string other => throw new QueryException("search.order",
                                "'" + other + "' is not an order", "use 'shuffled' or 'sequential'"),
                        };
                        break;
                    case "key": s.Key = Key(p.Value); break;
                    case "range": ReadRange(p.Value, s); break;
                    case "budget": ReadBudget(p.Value, s); break;
                    case "keep": ReadKeep(p.Value, s); break;
                    case "region": s.Region = Num(p.Value, "search.region"); break;
                    case "screen_grid": s.ScreenGrid = Num(p.Value, "search.screen_grid"); break;
                    case "screen":
                        s.Screen = Str(p.Value, "search.screen") switch
                        {
                            "auto" => ScreenMode.Auto,
                            "off" => ScreenMode.Off,
                            "on" => ScreenMode.On,
                            string other => throw new QueryException("search.screen",
                                "'" + other + "' is not a screening mode", "use 'auto', 'on' or 'off'"),
                        };
                        break;
                    case "grid": s.Grid = Num(p.Value, "search.grid"); break;
                    case "approx": s.Approx = Bool(p.Value, "search.approx"); break;
                    case "threads": s.Threads = Int(p.Value, "search.threads"); break;
                    case "block_size": s.BlockSize = Int(p.Value, "search.block_size"); break;
                    default:
                        throw new QueryException("search." + p.Name, "unknown key",
                            "search takes: order, key, range, budget, keep, region, grid, screen, screen_grid, "
                            + "approx, threads, block_size");
                }
            }

            if (s.Keep < 1) throw new QueryException("search.keep", "must be at least 1, or \"all\"");
            if (s.Region < 0 || !double.IsFinite(s.Region))
            {
                throw new QueryException("search.region", "must be a radius in metres, 0 for 'whatever the goals need'");
            }

            if (s.ScreenGrid < 0 || !double.IsFinite(s.ScreenGrid))
            {
                throw new QueryException("search.screen_grid", "must be a positive spacing in metres, or 0 for automatic");
            }

            if (!(s.Grid > 0) || !double.IsFinite(s.Grid)) throw new QueryException("search.grid", "must be a positive spacing in metres");
            if (s.BlockSize < 1) throw new QueryException("search.block_size", "must be at least 1");
            if (s.From > s.To) throw new QueryException("search.range", "the range runs backwards");
        }

        /// <summary>
        /// <c>"keep": 1000</c> or <c>"keep": "all"</c>. The word is spelled out rather than encoded as
        /// 0 or -1, because this number is now a real cap on the file and "all" is the one value that
        /// can fill a disk: it should have to be written out.
        /// </summary>
        private static void ReadKeep(JsonElement e, SearchSpec s)
        {
            if (e.ValueKind == JsonValueKind.String)
            {
                string v = (e.GetString() ?? "").Trim().ToLowerInvariant();
                if (v != "all")
                {
                    throw new QueryException("search.keep", "'" + v + "' is not a keep setting",
                        "a number (the best N are written) or \"all\" (every match, which wants output.rotate)");
                }

                s.KeepAll = true;
                return;
            }

            s.KeepAll = false;
            s.Keep = Int(e, "search.keep");
        }

        private static void ReadRange(JsonElement e, SearchSpec s)
        {
            Expect(e, JsonValueKind.Array, "search.range");
            long[] v = new long[2];
            int i = 0;
            foreach (JsonElement x in e.EnumerateArray())
            {
                if (i >= 2) throw new QueryException("search.range", "takes exactly two numbers [from, to]");
                v[i++] = (long)Num(x, "search.range");
            }

            if (i != 2) throw new QueryException("search.range", "takes exactly two numbers [from, to]");
            if (v[0] < int.MinValue || v[1] > int.MaxValue)
            {
                throw new QueryException("search.range", "the seed space is int32: -2147483648 .. 2147483647");
            }

            s.From = v[0];
            s.To = v[1];
        }

        private static void ReadBudget(JsonElement e, SearchSpec s)
        {
            Expect(e, JsonValueKind.Object, "search.budget");
            foreach (JsonProperty p in e.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "seeds": s.Seeds = (long)Num(p.Value, "search.budget.seeds"); break;
                    case "wall": s.Wall = Duration(Str(p.Value, "search.budget.wall"), "search.budget.wall"); break;
                    default:
                        throw new QueryException("search.budget." + p.Name, "unknown key", "budget takes: seeds, wall");
                }
            }
        }

        /// <summary><c>"1GB"</c>, <c>"512MB"</c>, <c>"1.5 TB"</c>, or a plain byte count.</summary>
        public static long Size(JsonElement e, string path)
        {
            if (e.ValueKind == JsonValueKind.Number) return (long)Num(e, path);
            string t = Str(e, path).Trim().Replace(" ", "").ToUpperInvariant();
            if (t.Length == 0) throw new QueryException(path, "empty size");
            long mult = 1;
            if (t.EndsWith("KB", StringComparison.Ordinal)) { mult = 1L << 10; t = t.Substring(0, t.Length - 2); }
            else if (t.EndsWith("MB", StringComparison.Ordinal)) { mult = 1L << 20; t = t.Substring(0, t.Length - 2); }
            else if (t.EndsWith("GB", StringComparison.Ordinal)) { mult = 1L << 30; t = t.Substring(0, t.Length - 2); }
            else if (t.EndsWith("TB", StringComparison.Ordinal)) { mult = 1L << 40; t = t.Substring(0, t.Length - 2); }
            else if (t.EndsWith("B", StringComparison.Ordinal)) { t = t.Substring(0, t.Length - 1); }

            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                || !double.IsFinite(v) || v < 0)
            {
                throw new QueryException(path, "'" + t + "' is not a size", "try 1GB, 512MB or a byte count");
            }

            return (long)(v * mult);
        }

        private static bool ReadCompress(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.True || e.ValueKind == JsonValueKind.False) return e.GetBoolean();
            string v = Str(e, "output.compress").Trim().ToLowerInvariant();
            return v switch
            {
                "gz" or "gzip" or "on" or "true" => true,
                "none" or "off" or "false" => false,
                _ => throw new QueryException("output.compress", "'" + v + "' is not a compression setting",
                        "'gz' (the default for rotated output) or 'none'"),
            };
        }

        /// <summary>"90s", "45m", "8h", "2d", or plain seconds.</summary>
        public static TimeSpan Duration(string text, string path)
        {
            string t = text.Trim();
            if (t.Length == 0) throw new QueryException(path, "empty duration");
            char last = t[t.Length - 1];
            double mult = last switch { 's' => 1, 'm' => 60, 'h' => 3600, 'd' => 86400, _ => 0 };
            string num = mult == 0 ? t : t.Substring(0, t.Length - 1);
            if (mult == 0) mult = 1;
            if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                || !double.IsFinite(v) || v < 0)
            {
                throw new QueryException(path, "'" + text + "' is not a duration", "try 30s, 45m, 8h or 2d");
            }

            return TimeSpan.FromSeconds(v * mult);
        }

        private static void ReadOutput(JsonElement e, OutputSpec o)
        {
            Expect(e, JsonValueKind.Object, "output");
            foreach (JsonProperty p in e.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "path": o.Path = Str(p.Value, "output.path"); break;
                    case "explain": o.Explain = Bool(p.Value, "output.explain"); break;
                    case "rotate": o.RotateBytes = Size(p.Value, "output.rotate"); break;
                    case "compress": o.Compress = ReadCompress(p.Value); break;
                    case "reduce": o.Reduce = Str(p.Value, "output.reduce"); break;
                    case "max_bytes": o.MaxBytes = Size(p.Value, "output.max_bytes"); break;
                    case "on_limit":
                        o.OnLimit = Str(p.Value, "output.on_limit") switch
                        {
                            "stop" => "stop",
                            "evict" => "evict",
                            string other => throw new QueryException("output.on_limit",
                                "'" + other + "' is not a ceiling behaviour",
                                "'stop' (the default: stop cleanly and print the resume command) or 'evict' "
                                + "(keep running and drop the worst records; needs an explicit confirmation)"),
                        };
                        break;
                    default: throw new QueryException("output." + p.Name, "unknown key",
                        "output takes: path, explain, rotate, compress, reduce, on_limit, max_bytes");
                }
            }
        }

        private static void ReadGoals(JsonElement e, List<Goal> goals)
        {
            Expect(e, JsonValueKind.Array, "goals");
            int i = 0;
            foreach (JsonElement g in e.EnumerateArray())
            {
                goals.Add(ReadGoal(g, "goals[" + i + "]"));
                i++;
            }
        }

        private static Goal ReadGoal(JsonElement e, string path)
        {
            Expect(e, JsonValueKind.Object, path);
            Goal g = new Goal();
            bool haveValue = false, haveMax = false, haveTarget = false, haveMetric = false, haveTest = false;

            foreach (JsonProperty p in e.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "id": g.Id = Str(p.Value, path + ".id"); break;
                    case "target": g.Target = ReadTarget(p.Value, path + ".target"); haveTarget = true; break;
                    case "metric": g.Metric = Str(p.Value, path + ".metric"); haveMetric = true; break;
                    case "test": g.Test = ReadTest(p.Value, path + ".test"); haveTest = true; break;
                    case "value": g.Value = Num(p.Value, path + ".value"); haveValue = true; break;
                    case "max": g.Max = Num(p.Value, path + ".max"); haveMax = true; break;
                    case "radius": g.Radius = Num(p.Value, path + ".radius"); break;
                    case "height": g.Height = Num(p.Value, path + ".height"); break;
                    case "min_area": g.MinArea = Num(p.Value, path + ".min_area"); break;
                    case "from": g.From = ReadFrom(p.Value, path + ".from"); break;
                    case "importance": g.Importance = ReadImportance(p.Value, path + ".importance"); break;
                    case "weight": g.Weight = Num(p.Value, path + ".weight"); break;
                    case "pad": g.Pad = Num(p.Value, path + ".pad"); break;
                    default:
                        throw new QueryException(path + "." + p.Name, "unknown key",
                            "a goal takes: id, target, metric, test, value, max, radius, height, min_area, from, importance, weight, pad");
                }
            }

            if (g.Id.Length == 0) throw new QueryException(path + ".id", "every goal needs an id, so the report can name it");
            if (!haveTarget) throw new QueryException(path + ".target", "missing");
            if (!haveMetric) throw new QueryException(path + ".metric", "missing");
            if (!haveTest) throw new QueryException(path + ".test", "missing");
            if (!haveValue) throw new QueryException(path + ".value", "missing");
            if (g.Test == GoalTest.Between && !haveMax) throw new QueryException(path + ".max", "a 'between' test needs 'max'");
            if (g.Test == GoalTest.Between && g.Max < g.Value) throw new QueryException(path + ".max", "must be at least 'value'");
            if (g.Weight <= 0) throw new QueryException(path + ".weight", "must be positive");
            if (g.Pad < 0) throw new QueryException(path + ".pad", "must not be negative");
            if (g.MinArea < 0) throw new QueryException(path + ".min_area", "must not be negative");
            return g;
        }

        private static GoalTarget ReadTarget(JsonElement e, string path)
        {
            // Shorthand: "biome:Swamp", "world:land_area", "location:Vendor_BlackForest", "group:traders".
            if (e.ValueKind == JsonValueKind.String)
            {
                string s = e.GetString() ?? "";
                int c = s.IndexOf(':');
                if (c <= 0 || c == s.Length - 1)
                {
                    throw new QueryException(path, "'" + s + "' is not a target",
                        "use \"biome:Swamp\" or {\"kind\":\"biome\",\"name\":\"Swamp\"}");
                }

                return new GoalTarget(ParseKind(s.Substring(0, c), path), s.Substring(c + 1));
            }

            Expect(e, JsonValueKind.Object, path);
            string? kind = null, name = null;
            foreach (JsonProperty p in e.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "kind": kind = Str(p.Value, path + ".kind"); break;
                    case "name": name = Str(p.Value, path + ".name"); break;
                    default: throw new QueryException(path + "." + p.Name, "unknown key", "a target takes: kind, name");
                }
            }

            if (kind == null) throw new QueryException(path + ".kind", "missing");
            if (name == null) throw new QueryException(path + ".name", "missing");
            return new GoalTarget(ParseKind(kind, path), name);
        }

        private static TargetKind ParseKind(string s, string path) => s switch
        {
            "biome" => TargetKind.Biome,
            "world" or "world_shape" => TargetKind.World,
            "location" => TargetKind.Location,
            "group" => TargetKind.Group,
            _ => throw new QueryException(path + ".kind", "'" + s + "' is not a target kind",
                     "use biome, world, location or group"),
        };

        private static GoalTest ReadTest(JsonElement e, string path) => Str(e, path) switch
        {
            "near" => GoalTest.Near,
            "far" => GoalTest.Far,
            "at_least" or "count_near" or "area" => GoalTest.AtLeast,
            "at_most" => GoalTest.AtMost,
            "between" or "range" => GoalTest.Between,
            string other => throw new QueryException(path, "'" + other + "' is not a test",
                "use near, far, at_least, at_most or between"),
        };

        private static DistanceOrigin ReadFrom(JsonElement e, string path) => Str(e, path) switch
        {
            "center" or "centre" => DistanceOrigin.Center,
            "spawn" => DistanceOrigin.Spawn,
            string other => throw new QueryException(path, "'" + other + "' is not an origin", "use center or spawn"),
        };

        private static Importance ReadImportance(JsonElement e, string path) => Str(e, path) switch
        {
            "must" or "must-have" or "must_have" => Importance.Must,
            "nice" or "nice-to-have" or "nice_to_have" => Importance.Nice,
            string other => throw new QueryException(path, "'" + other + "' is not an importance",
                "use 'must' (a filter) or 'nice' (a weighted score)"),
        };

        // ---- primitives -----------------------------------------------------------------------------
        private static void Expect(JsonElement e, JsonValueKind kind, string path)
        {
            if (e.ValueKind != kind)
            {
                throw new QueryException(path, "expected " + (kind == JsonValueKind.Object ? "an object" : "an array")
                                               + ", found " + e.ValueKind.ToString().ToLowerInvariant());
            }
        }

        private static string Str(JsonElement e, string path)
            => e.ValueKind == JsonValueKind.String
                ? e.GetString()!
                : throw new QueryException(path, "expected a string, found " + e.ValueKind.ToString().ToLowerInvariant());

        private static bool Bool(JsonElement e, string path) => e.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new QueryException(path, "expected true or false"),
        };

        private static double Num(JsonElement e, string path)
        {
            if (e.ValueKind != JsonValueKind.Number)
            {
                throw new QueryException(path, "expected a number, found " + e.ValueKind.ToString().ToLowerInvariant());
            }

            double d = e.GetDouble();
            if (!double.IsFinite(d)) throw new QueryException(path, "must be finite");
            return d;
        }

        private static int Int(JsonElement e, string path)
        {
            double d = Num(e, path);
            if (d != Math.Floor(d) || d < int.MinValue || d > int.MaxValue)
            {
                throw new QueryException(path, "expected a whole number");
            }

            return (int)d;
        }

        /// <summary>Accepts a JSON number or a "0x..." string, because a 64-bit key does not survive a double.</summary>
        private static ulong Key(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.String)
            {
                string s = e.GetString()!.Trim();
                bool hex = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
                if (hex ? ulong.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong h)
                        : ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out h))
                {
                    return h;
                }

                throw new QueryException("search.key", "'" + s + "' is not a 64-bit key", "try \"0x5EEDF00D1234ABCD\"");
            }

            if (e.ValueKind == JsonValueKind.Number && e.TryGetUInt64(out ulong n)) return n;
            throw new QueryException("search.key", "expected a 64-bit unsigned number, or a \"0x...\" string");
        }

        // ---- canonical form -------------------------------------------------------------------------

        /// <summary>
        /// The query written back out with every field explicit and in a fixed order. This - not the
        /// user's file - is what the run manifest hashes, so reformatting, comments or a re-ordered
        /// object cannot change a run's identity, while a changed threshold always does.
        /// </summary>
        public static string Canonicalise(Query q)
        {
            StringBuilder sb = new StringBuilder(2048);
            sb.Append("{\"version\":").Append(q.Version);
            sb.Append(",\"defs\":").Append(q.Defs);
            if (q.Name != null) sb.Append(",\"name\":").Append(JsonSerializer.Serialize(q.Name));
            sb.Append(",\"world\":{\"gen_version\":").Append(q.World.GenVersion).Append('}');
            sb.Append(",\"search\":{\"order\":\"").Append(q.Search.Order.ToString().ToLowerInvariant()).Append('"');
            sb.Append(",\"grid\":").Append(D(q.Search.Grid));
            sb.Append(",\"approx\":").Append(q.Search.Approx ? "true" : "false");
            // Region and screening change WHAT IS MEASURED, so they belong in the identity a resume
            // and a comparison rest on. They are appended only when they are set, so every query
            // written before they existed still hashes to exactly what it hashed to before.
            if (q.Search.Region > 0) sb.Append(",\"region\":").Append(D(q.Search.Region));
            if (q.Search.Screen != ScreenMode.Auto)
            {
                sb.Append(",\"screen\":\"").Append(q.Search.Screen.ToString().ToLowerInvariant()).Append('"');
            }

            if (q.Search.ScreenGrid > 0) sb.Append(",\"screen_grid\":").Append(D(q.Search.ScreenGrid));
            sb.Append('}');
            sb.Append(",\"goals\":[");
            for (int i = 0; i < q.Goals.Count; i++)
            {
                Goal g = q.Goals[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(JsonSerializer.Serialize(g.Id));
                // Serialized, not concatenated: a target is a user-supplied string, and now that a
                // location target may be typed as a player-facing name it can carry an apostrophe
                // ("Vegvisir's stone") or a quote. Appended raw, either produced canonical JSON that
                // no reader could parse - and the canonical form is what the run hash, the checkpoint
                // and the manifest are built from. A target made only of ASCII letters, digits, '_'
                // and ':' serialises to exactly itself in quotes, so every query written before this
                // line still hashes to what it hashed to before; the test suite asserts that over
                // every prefab, biome, group and metric name this build knows.
                sb.Append(",\"target\":").Append(JsonSerializer.Serialize(g.Target.ToString()));
                sb.Append(",\"metric\":\"").Append(g.Metric).Append('"');
                sb.Append(",\"test\":\"").Append(g.Test.ToString().ToLowerInvariant()).Append('"');
                sb.Append(",\"value\":").Append(D(g.Value));
                sb.Append(",\"max\":").Append(D(g.Max));
                sb.Append(",\"radius\":").Append(D(g.Radius));
                sb.Append(",\"height\":").Append(D(g.Height));
                sb.Append(",\"min_area\":").Append(D(g.MinArea));
                sb.Append(",\"from\":\"").Append(g.From.ToString().ToLowerInvariant()).Append('"');
                sb.Append(",\"importance\":\"").Append(g.Importance.ToString().ToLowerInvariant()).Append('"');
                sb.Append(",\"weight\":").Append(D(g.Weight));
                sb.Append(",\"pad\":").Append(D(g.Pad));
                sb.Append('}');
            }

            sb.Append("]}");
            return sb.ToString();
        }

        private static string D(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        /// <summary>SHA-256 of the canonical form, lowercase hex. The run's identity.</summary>
        public static string Hash(Query q)
        {
            byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes(q.CanonicalJson.Length > 0 ? q.CanonicalJson : Canonicalise(q)));
            return Convert.ToHexString(h).ToLowerInvariant();
        }
    }
}
