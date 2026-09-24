using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using SeedLab.Search.Criteria;

namespace SeedLab.Web.Search
{
    /// <summary>
    /// Turns the panel's query into the criteria language - as <b>text</b>, which is then read back by
    /// <see cref="QueryReader"/>.
    ///
    /// <para><b>Why go through text.</b> Building a <c>Query</c> object directly would bypass every
    /// check the reader makes: unknown keys, ranges, durations, duplicate ids, the version and defs
    /// gates. Going through the reader means the page is held to exactly the rules a query file is
    /// held to, and the by-product - <see cref="Translation.QueryJson"/> - is a file the user can save
    /// and hand to <c>vseed search</c> to get the same run in the terminal. That is what makes the
    /// page's results checkable against the CLI's rather than merely similar.</para>
    ///
    /// <para><b>Everything the CLI has, decision 12.</b> The whole <c>search</c> and <c>output</c>
    /// surface is written here: the bound (<c>keep N</c> / <c>keep all</c>), the region, the sampling
    /// ladder (<c>screen</c>, <c>screen_grid</c>), rotation, compression, the per-segment reduce, the
    /// ceiling and <c>on_limit</c>. A flag that exists in the terminal and not here would make the two
    /// different tools wearing one name.</para>
    /// </summary>
    public static class QueryTranslator
    {
        public sealed class Translation
        {
            public Query Query = null!;

            /// <summary>The query file this run is, pretty-printed. Runnable as-is by <c>vseed search</c>.</summary>
            public string QueryJson = "";

            /// <summary>Lines the page shows about what the translation DID, e.g. an added preference goal.</summary>
            public List<string> Notes = new List<string>();
        }

        /// <summary>
        /// The results extensions a browser is allowed to name. <c>.json</c> is here because the reader
        /// accepts it; <see cref="SeedLab.Search.Output.OutputPolicy.Validate"/> is what refuses it in
        /// combination with rotation, and it says why.
        /// </summary>
        public static readonly string[] AllowedResultExtensions = { ".jsonl", ".csv", ".json" };

        /// <summary>Win32 device names, reserved whatever extension follows them.</summary>
        private static readonly string[] ReservedDeviceNames =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        public static Translation Translate(SearchQuery q) => Translate(q, null, null);

        public static Translation Translate(SearchQuery q, string? resolvedOutPath)
            => Translate(q, resolvedOutPath, null);

        /// <param name="resolvedOutPath">
        /// The absolute path the SERVER will write to, when the query names an output file. The exported
        /// text still carries the bare name, so the same file run by <c>vseed search</c> writes beside
        /// the user's terminal rather than into the server's results directory.
        /// </param>
        /// <param name="canonicaliseTarget">
        /// Resolves a goal target a human typed to the spelling the run will actually use -
        /// <c>location:The Elder</c> to <c>location:GDKing</c>. It affects ONLY the generated goal id,
        /// which becomes a CSV column header; the target text itself is canonicalised later and
        /// independently by <c>SearchSession</c>, so passing null here changes no result, only a label.
        /// Null when the caller has no naming table (the CLI path, or a server with no dumped data).
        /// </param>
        public static Translation Translate(SearchQuery q, string? resolvedOutPath,
                                            Func<string, string>? canonicaliseTarget)
        {
            if (q.Goals == null || q.Goals.Count == 0) throw new ArgumentException("a search needs at least one goal.");
            if (q.Goals.Count > 32) throw new ArgumentException("at most 32 goals.");
            if (q.RangeStart < int.MinValue || q.RangeEnd > int.MaxValue)
            {
                throw new ArgumentException("the seed space is int32: -2147483648 .. 2147483647.");
            }

            if (q.RangeEnd < q.RangeStart) throw new ArgumentException("the seed range runs backwards.");
            if (q.BudgetSeeds < 0) throw new ArgumentException("the seed budget cannot be negative.");
            if (!double.IsFinite(q.BudgetSeconds) || q.BudgetSeconds < 0 || q.BudgetSeconds > 604_800)
            {
                throw new ArgumentException("the wall-clock budget must be between 0 and 604,800 s (a week).");
            }

            if (!q.KeepAll && (q.Keep < 1 || q.Keep > 1_000_000))
            {
                throw new ArgumentException("keep must be between 1 and 1,000,000, or 'all'.");
            }

            if (!double.IsFinite(q.GridSpacingM) || q.GridSpacingM < 12 || q.GridSpacingM > 1024)
            {
                throw new ArgumentException("the grid must be between 12 and 1,024 m.");
            }

            if (!double.IsFinite(q.ScreenGridM) || q.ScreenGridM < 0 || q.ScreenGridM > 1024)
            {
                throw new ArgumentException("the screening grid must be between 0 (automatic) and 1,024 m.");
            }

            if (!double.IsFinite(q.RegionM) || q.RegionM < 0 || q.RegionM > 10_500)
            {
                throw new ArgumentException("the region must be between 0 (whatever the goals need) and 10,500 m.");
            }

            if (q.Threads < 0 || q.Threads > 256) throw new ArgumentException("threads must be between 0 and 256.");
            if (q.BlockSize < 1 || q.BlockSize > 65_536) throw new ArgumentException("the block size must be between 1 and 65,536.");
            if (q.GenVersion < 0 || q.GenVersion > 2) throw new ArgumentException("gen_version must be 0, 1 or 2.");
            if (q.RotateBytes < 0) throw new ArgumentException("the rotation size cannot be negative.");
            if (q.MaxBytes < 0) throw new ArgumentException("the output ceiling cannot be negative.");

            Translation t = new Translation();

            StringBuilder sb = new StringBuilder(4096);
            sb.Append("{\n");
            sb.Append("  \"version\": 1,\n");
            sb.Append("  \"defs\": 1,\n");
            if (!string.IsNullOrWhiteSpace(q.Name))
            {
                sb.Append("  \"name\": ").Append(JsonSerializer.Serialize(q.Name)).Append(",\n");
            }

            sb.Append("  \"description\": \"built in the SeedLab web UI\",\n");
            sb.Append("  \"world\": { \"gen_version\": ").Append(q.GenVersion).Append(" },\n");
            sb.Append("  \"search\": {\n");
            sb.Append("    \"order\": ").Append(JsonSerializer.Serialize(Order(q.Order))).Append(",\n");
            if (q.Key.HasValue)
            {
                sb.Append("    \"key\": \"0x").Append(q.Key.Value.ToString("X16", CultureInfo.InvariantCulture)).Append("\",\n");
            }

            sb.Append("    \"range\": [").Append(q.RangeStart.ToString(CultureInfo.InvariantCulture))
              .Append(", ").Append(q.RangeEnd.ToString(CultureInfo.InvariantCulture)).Append("],\n");
            sb.Append("    \"budget\": {");
            bool anyBudget = false;
            if (q.BudgetSeeds > 0)
            {
                sb.Append(" \"seeds\": ").Append(q.BudgetSeeds.ToString(CultureInfo.InvariantCulture));
                anyBudget = true;
            }

            if (q.BudgetSeconds > 0)
            {
                if (anyBudget) sb.Append(',');
                sb.Append(" \"wall\": \"").Append(Num(q.BudgetSeconds)).Append("s\"");
                anyBudget = true;
            }

            sb.Append(anyBudget ? " },\n" : "},\n");

            // keep: a number or the string "all". This is decision 1, and it is a real cap on the FILE
            // now, not only on a display table - which is why it is worth spelling out in the export.
            sb.Append("    \"keep\": ").Append(q.KeepAll
                ? "\"all\""
                : q.Keep.ToString(CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("    \"grid\": ").Append(Num(q.GridSpacingM)).Append(",\n");
            sb.Append("    \"screen\": ").Append(JsonSerializer.Serialize(Screen(q.Screen))).Append(",\n");
            if (q.ScreenGridM > 0) sb.Append("    \"screen_grid\": ").Append(Num(q.ScreenGridM)).Append(",\n");
            if (q.RegionM > 0) sb.Append("    \"region\": ").Append(Num(q.RegionM)).Append(",\n");
            sb.Append("    \"threads\": ").Append(q.Threads.ToString(CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("    \"block_size\": ").Append(q.BlockSize.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("  },\n");

            // The output block. It used to be omitted with a comment saying the server writes nothing;
            // that stopped being true when decision 12 made rotation, compression, the ceiling and
            // on-limit reachable from the page. What is still true - and is enforced in WebServer - is
            // that the browser names a FILE, never a path, and the server resolves it inside one
            // directory it owns.
            string? outName = Clean(q.OutName);
            if (outName != null)
            {
                sb.Append("  \"output\": {\n");
                sb.Append("    \"path\": ").Append(JsonSerializer.Serialize(outName)).Append(",\n");
                if (q.RotateBytes > 0) sb.Append("    \"rotate\": ").Append(q.RotateBytes.ToString(CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("    \"compress\": ").Append(q.Compress ? "true" : "false").Append(",\n");
                sb.Append("    \"reduce\": ").Append(JsonSerializer.Serialize(string.IsNullOrWhiteSpace(q.Reduce) ? "none" : q.Reduce.Trim())).Append(",\n");
                if (q.MaxBytes > 0) sb.Append("    \"max_bytes\": ").Append(q.MaxBytes.ToString(CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("    \"on_limit\": ").Append(JsonSerializer.Serialize(
                    string.Equals(q.OnLimit, "evict", StringComparison.OrdinalIgnoreCase) ? "evict" : "stop")).Append('\n');
                sb.Append("  },\n");
            }

            sb.Append("  \"goals\": [\n");
            List<string> rows = new List<string>();
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < q.Goals.Count; i++)
            {
                SearchGoal g = q.Goals[i];
                string id = string.IsNullOrWhiteSpace(g.Id) ? DefaultId(g, i, canonicaliseTarget) : g.Id.Trim();
                if (!ids.Add(id)) id = id + "-" + i.ToString(CultureInfo.InvariantCulture);
                if (!double.IsFinite(g.Value) || !double.IsFinite(g.Max) || !double.IsFinite(g.Radius)
                    || !double.IsFinite(g.Height) || !double.IsFinite(g.MinArea) || !double.IsFinite(g.Weight)
                    || !double.IsFinite(g.Pad))
                {
                    throw new ArgumentException("goal '" + id + "' has a value that is not a finite number.");
                }

                rows.Add(GoalRow(g, id, g.Importance ?? "must", g.Test ?? "", g.Value));

                // The preference, written as what the query language can actually express: a second
                // goal, nice-to-have, same target and metric, whose test spelling is the direction.
                string? pref = PreferenceTest(g);
                if (pref != null)
                {
                    string pid = id + "-pref";
                    int n = 2;
                    while (!ids.Add(pid)) pid = id + "-pref" + (n++).ToString(CultureInfo.InvariantCulture);
                    rows.Add(GoalRow(g, pid, "nice", pref, PreferenceValue(g, pref)));
                    t.Notes.Add("goal '" + id + "' has a ranking preference, so the query file carries a second, "
                                + "nice-to-have goal '" + pid + "' (" + pref.Replace('_', ' ')
                                + ") over the same measurement. The criteria language has no tie-break key: "
                                + "a goal either filters or scores, so a preference IS a scoring goal.");
                }
            }

            for (int i = 0; i < rows.Count; i++)
            {
                sb.Append(rows[i]).Append(i + 1 < rows.Count ? ",\n" : "\n");
            }

            sb.Append("  ]\n}\n");

            string json = sb.ToString();
            Query parsed;
            try
            {
                parsed = QueryReader.Parse(json, "the search panel");
            }
            catch (QueryException ex)
            {
                // The reader's message already names the offending path ("goals.swamp-near: ...") and
                // usually carries a hint that lists what WAS allowed. Both are worth more to the user
                // than "bad query", so both go back up.
                throw new ArgumentException(ex.Message + (string.IsNullOrEmpty(ex.Hint) ? "" : " - " + ex.Hint));
            }

            // The server writes into its own results directory; the exported TEXT keeps the bare name so
            // the same file is runnable in a terminal. Both describe the same run: nothing the engine
            // compares is in the path.
            if (resolvedOutPath != null && parsed.Output.Path != null) parsed.Output.Path = resolvedOutPath;

            t.Query = parsed;
            t.QueryJson = json;
            return t;
        }

        /// <summary>
        /// The one place that turns a bare results NAME into a path, and refuses everything else.
        /// Returns null when the name is empty; throws with the reason when it is not a bare name.
        /// </summary>
        public static string? ResolveOutPath(string? name, string resultsDirectory)
        {
            string? clean = Clean(name);
            if (clean == null) return null;
            if (string.IsNullOrWhiteSpace(resultsDirectory))
            {
                throw new ArgumentException("this server was started without a results directory, so it cannot "
                                            + "write a results file. Run the search without an output file, or "
                                            + "start 'vseed serve' with a results directory.");
            }

            return System.IO.Path.Combine(System.IO.Path.GetFullPath(resultsDirectory), clean);
        }

        /// <summary>
        /// A bare file name or nothing. Separators, drive letters, <c>..</c>, device names and any
        /// extension outside <see cref="AllowedResultExtensions"/> are refused rather than sanitised:
        /// silently rewriting what a user typed is how a path check becomes a path bug.
        /// </summary>
        public static string? Clean(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            string s = name.Trim();

            if (s.IndexOf('/') >= 0 || s.IndexOf('\\') >= 0 || s.IndexOf(':') >= 0)
            {
                throw new ArgumentException("the results file is a NAME, not a path: '" + s + "' contains a "
                                            + "directory separator or a drive. The server writes it into its "
                                            + "own results directory, which the panel shows.");
            }

            if (s == "." || s == ".." || s.StartsWith("..", StringComparison.Ordinal))
            {
                throw new ArgumentException("'" + s + "' is not a file name.");
            }

            if (s.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ArgumentException("'" + s + "' contains a character a file name cannot hold.");
            }

            string ext = System.IO.Path.GetExtension(s).ToLowerInvariant();
            bool ok = false;
            foreach (string a in AllowedResultExtensions) if (ext == a) ok = true;
            if (!ok)
            {
                throw new ArgumentException("the results file must end in .jsonl, .csv or .json - '" + s
                                            + "' does not, and the extension is what picks the format.");
            }

            if (System.IO.Path.GetFileNameWithoutExtension(s).Length == 0)
            {
                throw new ArgumentException("'" + s + "' has no name, only an extension.");
            }

            // Windows device names. CON, NUL, COM1 and the rest are reserved with ANY extension, so
            // "CON.jsonl" opens the console device rather than a file - GetInvalidFileNameChars says
            // nothing about it, and a probe of this endpoint found the run starting happily and
            // writing its results into a device. Refused by name, on every platform, because a query
            // file written here should mean the same thing when it is run on another one.
            string stem = System.IO.Path.GetFileNameWithoutExtension(s).ToUpperInvariant();
            foreach (string device in ReservedDeviceNames)
            {
                if (stem == device)
                {
                    throw new ArgumentException("'" + s + "' is a reserved device name on Windows ("
                                                + device + " is the console, a port or the null device, "
                                                + "whatever extension follows it), so it cannot be a "
                                                + "results file. Pick another name.");
                }
            }

            // A trailing space or dot is silently stripped by Win32, so the file that appears is not
            // the file that was asked for.
            if (s.EndsWith(" ", StringComparison.Ordinal) || s.EndsWith(".", StringComparison.Ordinal))
            {
                throw new ArgumentException("'" + s + "' ends in a space or a dot, which Windows strips - "
                                            + "the file that appeared would not be the one you named.");
            }

            return s;
        }

        // -----------------------------------------------------------------------------------------
        private static string GoalRow(SearchGoal g, string id, string importance, string test, double value)
        {
            StringBuilder sb = new StringBuilder(256);
            sb.Append("    { \"id\": ").Append(JsonSerializer.Serialize(id));
            sb.Append(", \"target\": ").Append(JsonSerializer.Serialize(g.Target ?? ""));
            sb.Append(", \"metric\": ").Append(JsonSerializer.Serialize(g.Metric ?? ""));
            sb.Append(", \"test\": ").Append(JsonSerializer.Serialize(test));
            sb.Append(", \"value\": ").Append(Num(value));
            if (string.Equals(test, "between", StringComparison.Ordinal)) sb.Append(", \"max\": ").Append(Num(g.Max));
            if (g.Radius > 0) sb.Append(", \"radius\": ").Append(Num(g.Radius));
            if (g.Height != 0) sb.Append(", \"height\": ").Append(Num(g.Height));
            if (g.MinArea > 0 && Math.Abs(g.MinArea - 10_000.0) > 1e-9) sb.Append(", \"min_area\": ").Append(Num(g.MinArea));
            if (!string.Equals(g.From, "center", StringComparison.Ordinal)) sb.Append(", \"from\": ").Append(JsonSerializer.Serialize(g.From ?? "center"));
            sb.Append(", \"importance\": ").Append(JsonSerializer.Serialize(importance));
            if (string.Equals(importance, "nice", StringComparison.Ordinal))
            {
                sb.Append(", \"weight\": ").Append(Num(g.Weight <= 0 ? 1.0 : g.Weight));
                if (string.Equals(test, "between", StringComparison.Ordinal)) sb.Append(", \"pad\": ").Append(Num(g.Pad));
            }

            sb.Append(" }");
            return sb.ToString();
        }

        /// <summary>
        /// The preference's test spelling, or null when there is none to write. A goal that is already
        /// a nice-to-have IS its own preference and never gets a companion.
        /// </summary>
        public static string? PreferenceTest(SearchGoal g)
        {
            if (string.Equals(g.Importance, "nice", StringComparison.Ordinal)) return null;
            string p = (g.Preference ?? "none").Trim().ToLowerInvariant();
            switch (p)
            {
                case "":
                case "none": return null;
                case "closer": return "near";
                case "farther":
                case "further": return "far";
                case "smaller": return "at_most";
                case "larger": return "at_least";
                case "recommended": return Recommended(g);
                default:
                    throw new ArgumentException("'" + g.Preference + "' is not a preference - use none, recommended, "
                                                + "closer, farther, smaller or larger.");
            }
        }

        /// <summary>
        /// The recommended direction for a metric, as a FIXED rule rather than a guess, so an exported
        /// file is legible: a distance is better when it is shorter unless the goal asked for distance
        /// (<c>far</c>), and everything else - an area, a count, a share - is better when there is more
        /// of it unless the goal asked for less (<c>at_most</c>/<c>near</c>).
        /// </summary>
        public static string Recommended(SearchGoal g)
        {
            string metric = g.Metric ?? "";
            string test = g.Test ?? "";
            bool distance = metric.Contains("distance", StringComparison.Ordinal);
            if (distance) return string.Equals(test, "far", StringComparison.Ordinal) ? "far" : "near";
            if (string.Equals(test, "at_most", StringComparison.Ordinal) || string.Equals(test, "near", StringComparison.Ordinal)) return "at_most";
            return "at_least";
        }

        /// <summary>
        /// The companion's threshold. A sub-score is shaped against its own value, so the preference
        /// must be anchored somewhere sensible: the goal's own threshold, or the far end of a
        /// <c>between</c> range when the preference points that way.
        /// </summary>
        private static double PreferenceValue(SearchGoal g, string prefTest)
        {
            bool between = string.Equals(g.Test, "between", StringComparison.Ordinal);
            if (!between) return g.Value;
            return prefTest is "far" or "at_least" ? g.Max : g.Value;
        }

        private static string Order(string? order)
            => string.Equals(order, "sequential", StringComparison.Ordinal) ? "sequential" : "shuffled";

        private static string Screen(string? screen)
        {
            string s = (screen ?? "auto").Trim().ToLowerInvariant();
            return s is "off" or "on" or "auto" ? s : "auto";
        }

        /// <summary>
        /// The goal id, which becomes a CSV column header and a JSON key, so it has to be well formed
        /// whatever the target says.
        ///
        /// <para><b>Two things went wrong here and both are fixed (2026-09-24).</b> The id was built
        /// from the RAW target, so a user who typed the display name got
        /// <c>location-The Elder-nearest-distance</c> - a column header with a space in it - even
        /// though the run went on to resolve the target to <c>GDKing</c>. The caller now passes
        /// <c>canonicaliseTarget</c>, so the id names the prefab the run actually used. And because
        /// that resolution is best-effort by design - an oracle with no dumped data cannot answer -
        /// the id is ALSO sanitised: anything outside <c>[A-Za-z0-9]</c> becomes a hyphen and runs
        /// collapse. Belt and braces, because a malformed header is silent corruption in a
        /// spreadsheet rather than an error anybody sees.</para>
        /// </summary>
        private static string DefaultId(SearchGoal g, int i, Func<string, string>? canonicaliseTarget)
        {
            string target = g.Target ?? "";
            if (canonicaliseTarget != null && target.Length > 0)
            {
                // A naming table that cannot answer must never fail a search.
                try { target = canonicaliseTarget(target) ?? target; }
                catch (Exception) { target = g.Target ?? ""; }
            }

            string id = (Sanitise(target) + "-" + Sanitise(g.Metric ?? "")).Trim('-');
            return id.Length > 0 ? id : "goal-" + i.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>One pass, no regex, no culture: keep letters and digits, everything else is a
        /// separator, and runs of separators collapse to one hyphen.</summary>
        private static string Sanitise(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            bool lastWasDash = false;
            foreach (char c in s)
            {
                bool keep = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
                if (keep) { sb.Append(c); lastWasDash = false; }
                else if (!lastWasDash) { sb.Append('-'); lastWasDash = true; }
            }
            return sb.ToString().Trim('-');
        }

        /// <summary>
        /// Round-trip-exact JSON for a double. "R" is what <see cref="QueryReader.Canonicalise"/> uses,
        /// so a threshold cannot drift by a bit between what the page sent and what the run compared.
        /// </summary>
        private static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    }
}
