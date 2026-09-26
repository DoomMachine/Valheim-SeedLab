using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace SeedLab.Runtime.Execution
{
    /// <summary>One measurement of an earlier profile, as a replay needs it.</summary>
    public sealed class PlannedSection
    {
        public string Tier { get; init; } = "";

        /// <summary>Sampling grid in metres for t2/t3, 0 otherwise.</summary>
        public int Grid { get; init; }

        /// <summary>The location prefix that was ASKED for (t5), 0 otherwise - not the de-duplicated run length.</summary>
        public int PrefixRequested { get; init; }

        public int Workers { get; init; }
        public int Seeds { get; init; }

        /// <summary>The recorded SHA-256 of the section's seed list (always present in a plan).</summary>
        public string SeedListSha256 { get; init; } = "";

        /// <summary>How long the section's measured window took when it was recorded, when the document says.</summary>
        public double? WallSeconds { get; init; }
    }

    /// <summary>
    /// A pilot an earlier profile ran before its measurements (a <c>--saturate</c> pilot, or a replay's
    /// re-run of one): the section it belongs to and the seed count of each of its runs, in order. A
    /// replay runs them again, uncounted, so both profiles start their measurements from the same
    /// history of work.
    /// </summary>
    public sealed class PlannedPilot
    {
        /// <summary>The plan section (index into <see cref="ProfilePlan.Sections"/>) whose tier and worker count the pilot ran at.</summary>
        public int SectionIndex { get; init; }

        public int Workers { get; init; }

        /// <summary>Each pilot run's seed count, in the order they ran.</summary>
        public IReadOnlyList<int> Runs { get; init; } = Array.Empty<int>();

        /// <summary>The pilot's recorded time, all its runs together, when the document says.</summary>
        public double? Seconds { get; init; }
    }

    /// <summary>
    /// A <c>seedlab-profile/2</c> document read back as a plan: the seed order (key and first index),
    /// the warm-up, each section's tier, grid or prefix, worker count, seed count and seed-list
    /// digest, in document order, and the pilots that ran before them. Replaying it measures exactly
    /// the same seeds after the same pilots, which is what makes a before/after comparison fair. It
    /// also carries what the earlier run was measured WITH (counters, build, garbage collector,
    /// machine), so a replay can say what differs. Only /2 documents are plans: a /1 document records
    /// the location prefix as the run length, from which the asked-for prefix cannot be recovered.
    /// </summary>
    public sealed class ProfilePlan
    {
        public const string Schema = "seedlab-profile/2";

        public ulong Key { get; init; }
        public long From { get; init; }
        public int Warmup { get; init; }
        public IReadOnlyList<PlannedSection> Sections { get; init; } = Array.Empty<PlannedSection>();
        public IReadOnlyList<PlannedPilot> Pilots { get; init; } = Array.Empty<PlannedPilot>();

        /// <summary>Whether the recorded run counted per-point events (<c>--counters</c>); null when the document does not say.</summary>
        public bool? Counters { get; init; }

        /// <summary>The recorded run's <c>run.tier</c> (the battery it was, e.g. "t4" or "all").</summary>
        public string? TierText { get; init; }

        public string? VseedSha256 { get; init; }
        public string? WorldGenSha256 { get; init; }
        public string? LocationsSha256 { get; init; }

        /// <summary>The game-data snapshot the recorded run's t5 sections placed locations from (<c>build.data</c>).</summary>
        public string? DataProvenance { get; init; }

        /// <summary><c>machine.gc</c>: "server" or "workstation".</summary>
        public string? GcMode { get; init; }

        /// <summary><c>machine.gc_config.GCDynamicAdaptationMode</c> (1 = DATAS on), as text.</summary>
        public string? GcDynamicAdaptation { get; init; }

        public int? LogicalCores { get; init; }
        public string? CpuBrand { get; init; }

        /// <summary>
        /// How long the recorded run's measurements and pilots took, in seconds, when every section
        /// recorded its time (warm-ups and the watch before measuring not included); null otherwise.
        /// </summary>
        public double? RecordedSeconds
        {
            get
            {
                double s = 0;
                foreach (PlannedSection p in Sections)
                {
                    if (!p.WallSeconds.HasValue) return null;
                    s += p.WallSeconds.Value;
                }

                foreach (PlannedPilot p in Pilots) s += p.Seconds ?? 0;
                return s;
            }
        }

        /// <summary>Reads a plan, or throws <see cref="FormatException"/> with a plain-words reason naming the field.</summary>
        public static ProfilePlan Parse(string json)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new FormatException("it is not a JSON document (" + ex.Message + ")");
            }

            using (doc)
            {
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new FormatException("it is not a profile document");
                string schema = root.TryGetProperty("schema", out JsonElement s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "";
                if (schema != Schema)
                {
                    throw new FormatException(schema.Length == 0
                        ? "it is not a vseed profile (no \"schema\")"
                        : "it is a " + schema + " document; only " + Schema + " records what a replay needs (the asked-for location prefix and each section's seed list)");
                }

                if (!root.TryGetProperty("run", out JsonElement run) || run.ValueKind != JsonValueKind.Object)
                    throw new FormatException("it has no \"run\" block");
                string keyText = Str(run, "key");
                string k = keyText.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? keyText.Substring(2) : keyText;
                if (!ulong.TryParse(k, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong key))
                    throw new FormatException("run.key '" + keyText + "' is not a hex key");
                long from = Long(run, "from_index");
                if (from < 0 || from > uint.MaxValue)
                    throw new FormatException("run.from_index " + from.ToString(CultureInfo.InvariantCulture) + " is outside the seed order (0 to 4294967295)");
                long warmup = Long(run, "warmup_per_worker");
                if (warmup < 0 || warmup > 1000)
                    throw new FormatException("run.warmup_per_worker " + warmup.ToString(CultureInfo.InvariantCulture) + " is outside 0 to 1000");

                if (!root.TryGetProperty("sections", out JsonElement secs) || secs.ValueKind != JsonValueKind.Array || secs.GetArrayLength() == 0)
                    throw new FormatException("it has no measured sections (an --overhead run is not a plan)");
                List<PlannedSection> list = new List<PlannedSection>();
                int i = 0;
                foreach (JsonElement e in secs.EnumerateArray())
                {
                    string where = "sections[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                    if (e.ValueKind != JsonValueKind.Object) throw new FormatException(where + " is not an object");
                    string tier = Str(e, "tier", where);
                    if (tier != "t2" && tier != "t3" && tier != "t4" && tier != "t5") throw new FormatException(where + ".tier '" + tier + "' is not t2, t3, t4 or t5");
                    int grid = OptInt(e, "grid_m", where) ?? 0;
                    int prefix = OptInt(e, "prefix_requested", where) ?? 0;
                    if (tier == "t5" && prefix < 1) throw new FormatException(where + " is a t5 section without prefix_requested");
                    if ((tier == "t2" || tier == "t3") && grid < 1) throw new FormatException(where + " is a " + tier + " section without grid_m");
                    long workers = Long(e, "workers", where);
                    long seeds = Long(e, "seeds", where);
                    if (workers < 1 || workers > 4096) throw new FormatException(where + ".workers " + workers.ToString(CultureInfo.InvariantCulture) + " is not a worker count");
                    if (seeds < 1 || seeds > 1_000_000) throw new FormatException(where + ".seeds " + seeds.ToString(CultureInfo.InvariantCulture) + " is outside 1 to 1,000,000");
                    string digest = OptStr(e, "seed_list_sha256") ?? "";
                    if (!IsSha256(digest))
                    {
                        throw new FormatException(where + ".seed_list_sha256 is missing or not a SHA-256: without it a replay cannot prove it "
                                                  + "measures the plan's seeds");
                    }

                    list.Add(new PlannedSection
                    {
                        Tier = tier,
                        Grid = tier == "t2" || tier == "t3" ? grid : 0,
                        PrefixRequested = tier == "t5" ? prefix : 0,
                        Workers = (int)workers,
                        Seeds = (int)seeds,
                        SeedListSha256 = digest.ToLowerInvariant(),
                        WallSeconds = OptDouble(e, "wall_s"),
                    });
                    i++;
                }

                List<PlannedPilot> pilots = ReadPilots(run, list);
                JsonElement build = root.TryGetProperty("build", out JsonElement b) && b.ValueKind == JsonValueKind.Object ? b : default;
                JsonElement machine = root.TryGetProperty("machine", out JsonElement m) && m.ValueKind == JsonValueKind.Object ? m : default;
                JsonElement gcConfig = machine.ValueKind == JsonValueKind.Object && machine.TryGetProperty("gc_config", out JsonElement gcc)
                                       && gcc.ValueKind == JsonValueKind.Object ? gcc : default;
                JsonElement cpu = machine.ValueKind == JsonValueKind.Object && machine.TryGetProperty("cpu", out JsonElement c)
                                  && c.ValueKind == JsonValueKind.Object ? c : default;
                int? cores = null;
                if (machine.ValueKind == JsonValueKind.Object && machine.TryGetProperty("logical_cores", out JsonElement lc)
                    && lc.ValueKind == JsonValueKind.Number && lc.TryGetInt32(out int lcv))
                {
                    cores = lcv;
                }

                return new ProfilePlan
                {
                    Key = key,
                    From = from,
                    Warmup = (int)warmup,
                    Sections = list,
                    Pilots = pilots,
                    Counters = run.TryGetProperty("counters", out JsonElement cn) && (cn.ValueKind == JsonValueKind.True || cn.ValueKind == JsonValueKind.False)
                        ? cn.GetBoolean() : null,
                    TierText = OptStr(run, "tier"),
                    VseedSha256 = OptStr(build, "vseed_sha256"),
                    WorldGenSha256 = OptStr(build, "worldgen_sha256"),
                    LocationsSha256 = OptStr(build, "locations_sha256"),
                    DataProvenance = OptStr(build, "data"),
                    GcMode = OptStr(machine, "gc"),
                    GcDynamicAdaptation = OptText(gcConfig, "GCDynamicAdaptationMode"),
                    LogicalCores = cores,
                    CpuBrand = OptStr(cpu, "brand"),
                };
            }
        }

        /// <summary>
        /// The pilots to run again: a replay's own record (<c>run.plan.pilots</c>, so a replay of a
        /// replay has the same history) or else the <c>--saturate</c> pilots. Each belongs to one section
        /// and must name its worker count.
        /// </summary>
        private static List<PlannedPilot> ReadPilots(JsonElement run, List<PlannedSection> sections)
        {
            List<PlannedPilot> r = new List<PlannedPilot>();
            JsonElement arr = default;
            string where = "";
            if (run.TryGetProperty("plan", out JsonElement plan) && plan.ValueKind == JsonValueKind.Object
                && plan.TryGetProperty("pilots", out JsonElement pp) && pp.ValueKind == JsonValueKind.Array)
            {
                arr = pp;
                where = "run.plan.pilots";
            }
            else if (run.TryGetProperty("saturate", out JsonElement sat) && sat.ValueKind == JsonValueKind.Object
                     && sat.TryGetProperty("pilots", out JsonElement sp) && sp.ValueKind == JsonValueKind.Array)
            {
                arr = sp;
                where = "run.saturate.pilots";
            }

            if (arr.ValueKind != JsonValueKind.Array) return r;
            int i = 0;
            foreach (JsonElement p in arr.EnumerateArray())
            {
                string at = where + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                if (p.ValueKind != JsonValueKind.Object) throw new FormatException(at + " is not an object");
                int section = OptInt(p, "section_index", at) ?? i;
                if (section < 0 || section >= sections.Count) throw new FormatException(at + " belongs to no section of the plan");
                long workers = Long(p, "workers", at);
                if (workers != sections[section].Workers)
                {
                    throw new FormatException(at + " ran at " + workers.ToString(CultureInfo.InvariantCulture) + " worker(s), its section at "
                                              + sections[section].Workers.ToString(CultureInfo.InvariantCulture));
                }

                List<int> runs = new List<int>();
                if (p.TryGetProperty("pilot_runs", out JsonElement pr) && pr.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement x in pr.EnumerateArray())
                    {
                        if (x.ValueKind != JsonValueKind.Number || !x.TryGetInt32(out int v) || v < 1 || v > SaturationPlanner.PilotCap)
                            throw new FormatException(at + ".pilot_runs holds something that is not a seed count");
                        runs.Add(v);
                    }
                }
                else
                {
                    int? one = OptInt(p, "pilot_seeds", at);
                    if (one.HasValue)
                    {
                        if (one.Value < 1 || one.Value > SaturationPlanner.PilotCap) throw new FormatException(at + ".pilot_seeds is not a seed count");
                        runs.Add(one.Value);
                    }
                }

                if (runs.Count == 0) throw new FormatException(at + " records no pilot run");
                r.Add(new PlannedPilot { SectionIndex = section, Workers = (int)workers, Runs = runs, Seconds = OptDouble(p, "pilot_s") });
                i++;
            }

            return r;
        }

        /// <summary>
        /// Null when the command line agrees with the plan; otherwise a plain-words reason. A value
        /// left off the command line always agrees - the plan's is used.
        /// </summary>
        public string? Conflict(ulong? key, long? from, int? warmup)
        {
            if (key.HasValue && key.Value != Key)
                return "--key 0x" + key.Value.ToString("X16", CultureInfo.InvariantCulture) + " differs from the plan's 0x" + Key.ToString("X16", CultureInfo.InvariantCulture);
            if (from.HasValue && from.Value != From)
                return "--from " + from.Value.ToString(CultureInfo.InvariantCulture) + " differs from the plan's " + From.ToString(CultureInfo.InvariantCulture);
            if (warmup.HasValue && warmup.Value != Warmup)
                return "--warmup " + warmup.Value.ToString(CultureInfo.InvariantCulture) + " differs from the plan's " + Warmup.ToString(CultureInfo.InvariantCulture)
                       + " (the warm-up seeds are part of what is replayed)";
            return null;
        }

        /// <summary>
        /// Null when this process counts per-point events the way the plan's run did (or the plan does
        /// not say); otherwise what to do. Counting is fixed when vseed starts, so a replay cannot switch
        /// it itself, and timings with and without it are not comparable.
        /// </summary>
        public string? CountersConflict(bool countersOn)
        {
            if (!Counters.HasValue || Counters.Value == countersOn) return null;
            return Counters.Value
                ? "the plan was measured with --counters, so its timings include the counting; add --counters to measure the same way"
                : "the plan was measured without --counters; leave --counters out (with it, every timing would include the counting)";
        }

        private static bool IsSha256(string s)
        {
            if (s.Length != 64) return false;
            foreach (char ch in s)
            {
                if (!Uri.IsHexDigit(ch)) return false;
            }

            return true;
        }

        private static string Str(JsonElement e, string name, string where = "run")
        {
            if (!e.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.String) throw new FormatException(where + "." + name + " is missing");
            return v.GetString()!;
        }

        private static long Long(JsonElement e, string name, string where = "run")
        {
            if (!e.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out long x))
                throw new FormatException(where + "." + name + " is missing or not a whole number");
            return x;
        }

        /// <summary>An optional whole number: absent or null gives null, anything but a whole number is refused.</summary>
        private static int? OptInt(JsonElement e, string name, string where)
        {
            if (!e.TryGetProperty(name, out JsonElement v) || v.ValueKind == JsonValueKind.Null) return null;
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int x))
                throw new FormatException(where + "." + name + " is not a whole number");
            return x;
        }

        /// <summary>An optional, informational number: anything but a number reads as absent.</summary>
        private static double? OptDouble(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.Number) return null;
            return v.TryGetDouble(out double d) && !double.IsNaN(d) && !double.IsInfinity(d) ? d : null;
        }

        /// <summary>An optional, informational string: anything but a string reads as absent.</summary>
        private static string? OptStr(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.String) return null;
            return v.GetString();
        }

        /// <summary>An optional value of any simple kind, as text (the GC reports numbers and strings alike).</summary>
        private static string? OptText(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out JsonElement v)) return null;
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            };
        }
    }
}
