using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using SeedLab.Cli.Analysis;
using SeedLab.Cli.Infra;
using SeedLab.LocationOracle;
using SeedLab.Render;
using SeedLab.Runtime.Execution;
using SeedLab.Search.Evaluation;
using SeedLab.Search.Locations;
using SeedLab.Search.Metrics;
using SeedLab.WorldGen;
using SeedLab.WorldGen.Diagnostics;

namespace SeedLab.Cli.Commands
{
    /// <summary>
    /// <c>vseed profile</c> - where one seed's time goes, phase by phase, measured now on this machine.
    ///
    /// <para><b>What it runs.</b> Its own loop over the same library calls a search makes - the
    /// generator, <see cref="SeedSampler"/>, <see cref="WorldMeasurement"/> and the location oracle -
    /// the way <c>vseed bench</c> does, rather than going through the search evaluator, so profiling
    /// needs no change to the search and cannot alter one. Each worker thread sets
    /// <see cref="PhaseSink.Current"/> to its own sink; the generator's phase boundaries record into
    /// it, and the command reads the sink between seeds. Nothing the generator computes can depend on
    /// the sink (it is write-only from generator code, and the tests prove it on 64 worlds).</para>
    ///
    /// <para><b>Hygiene.</b> Timings from a busy machine are not measurements. The command watches the
    /// machine for a baseline before it starts and samples every process's CPU throughout
    /// (<see cref="QuietMachineProbe"/>), and a run another vseed, the game or a build overlapped is
    /// marked TAINTED, with what tainted it, rather than trusted. Every worker discards its first seeds
    /// (tiered JIT and PGO are still settling), and the JIT and GC activity inside the measured window
    /// is reported next to the numbers.</para>
    /// </summary>
    public static class ProfileCommand
    {
        public const string Help = @"vseed profile [options]

  Where one seed's time goes, phase by phase, measured on this machine now: the generator's
  constructor, the lake/river/stream pre-generation and its nine steps, the biome and height passes
  per sampling grid, the structure counts, and the location world build (grid, sectors, alt
  biomes, placement). Timestamps are taken only at phase boundaries; no value a seed produces
  changes when it is profiled. Seeds come from one fixed order (key 0xA17A25EED10C5117, the
  count study's), so profiles from different runs and machines cover the same worlds.

  A timing from a busy machine is not a measurement. The run watches the machine before and
  during the work, and marks itself TAINTED - naming the other vseed, the game, the build or
  the load it saw - instead of pretending. It writes nothing unless --out is given.

what
  --tier t2|t3|t4|t5|all   default all, the fixed battery: t2 G384 x500 seeds; t3 G384 x500 (with
                           the structure counts); t3 G12 x100; t5 prefix 2, 22, 67 x200; t5 prefix
                           183 x100. t2 = biome pass (+ largest patches), t3 = biomes, pre-generation,
                           structure counts and the height pass, t4 = pre-generation and the
                           structure counts, t5 = the location oracle's whole world build
  --grid <G>[,<G>...]      t2/t3 sampling grid(s): G384, G192, G96, G24, G12 (default G384)
  --prefix <n>[,<n>...]    t5: ordered location entries to place (default 22; 183 = all of them)
  --seeds <n>              seeds per section (default 64; overrides the battery's counts)
  --key <0x...>            the seed order's key (default 0xA17A25EED10C5117)
  --from <index>           first index into the order (default 0)
  --threads <n>[,<n>...]   worker counts to measure at, e.g. 1,8,16 (default 1)
  --warmup <n>             seeds each worker runs and discards first (default 3)

how
  --counters               also count per-point events (base heights, world angles, river
                           lookups, stream tries). Fixed at process start; the timings then
                           include the counting
  --overhead               measure what the profiler itself costs instead (default t2 G384,
                           200 seeds, one worker): sink off/on in this process and counters
                           off/on in child processes, interleaved ABAB x 5, medians
  --baseline <vseed.exe>   with --overhead: also time this build (profiler off) against another
                           vseed build on the same one-worker T2 G384 search
  --quiet-baseline <s>     watch the machine for s seconds before measuring (default 30; 0 skips)

output
  --out <file.json>        write the profile, schema seedlab-profile/1 (not inside a Valheim save
                           folder, Steam's userdata or the game's own install folders)
  --per-seed               with --out: also <file>-seeds.csv, one row per measured seed
  --json                   the document on stdout

The profile's own --threads is a list; the mode's worker share does not apply. Every requested
count is still checked against the memory guard.";

        /// <summary>The seeds each section runs when --seeds is not given.</summary>
        private const int DefaultSeeds = 64;

        // ---------------------------------------------------------------------------------------------
        // Main's first statement
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// Called as the FIRST statement of <c>Main</c>: when the command is <c>profile</c> and
        /// <c>--counters</c> is on the line, sets <see cref="PhaseClock.EnvironmentVariable"/> before any
        /// generator type exists. The counter switch is a static readonly field read once in its type
        /// initialiser (that is what makes it free when off), so this is the only moment it can be set.
        /// The command checks afterwards that it took effect.
        /// </summary>
        public static void ApplyCountersSwitch(string[] rawArgs)
        {
            if (Array.IndexOf(rawArgs, "--counters") < 0) return;
            if (!string.Equals(CommandOf(rawArgs), "profile", StringComparison.Ordinal)) return;
            Environment.SetEnvironmentVariable(PhaseClock.EnvironmentVariable, "1");
        }

        /// <summary>The command name the way Main finds it: the first token that is not a global option or its value.</summary>
        private static string? CommandOf(string[] rawArgs)
        {
            for (int i = 0; i < rawArgs.Length; i++)
            {
                string t = rawArgs[i];
                if (t == "--debug") continue;
                if (Args.IsGlobalOption(t))
                {
                    if (Args.GlobalOptionTakesValue(t)) i++;
                    continue;
                }

                return t;
            }

            return null;
        }

        /// <summary>
        /// The profile's <c>--threads</c> is a LIST of worker counts, where every other command's is the
        /// runtime's one count. Main moves global options next to the command, so the list is renamed
        /// here, before the runtime reads <c>--threads</c> and refuses "1,8,16".
        /// </summary>
        public static List<string> RenameThreadsOption(List<string> rest)
        {
            List<string> r = new List<string>(rest.Count);
            foreach (string t in rest)
            {
                if (t == "--threads") r.Add("--profile-threads");
                else if (t.StartsWith("--threads=", StringComparison.Ordinal)) r.Add("--profile-threads=" + t.Substring("--threads=".Length));
                else r.Add(t);
            }

            return r;
        }

        // ---------------------------------------------------------------------------------------------
        // Options
        // ---------------------------------------------------------------------------------------------

        internal sealed class Options
        {
            public List<ProfileSection> Sections = new List<ProfileSection>();
            public List<int> WorkerCounts = new List<int> { 1 };
            public ulong Key = PilotSeedOrder.Key;
            public long From;
            public int Warmup = 3;
            public bool CountersRequested;
            public bool Overhead;
            public string? Baseline;
            public int QuietSeconds = 30;
            public string? Out;
            public bool PerSeed;
            public string TierText = "all";
        }

        public static int Run(Args a, Out o, CliRuntime rt)
        {
            // Hidden: one timing leg of --overhead, in a child process.
            if (a.Flag("overhead-leg")) return OverheadLeg(a);

            Options opt = Parse(a);
            a.RejectUnknown();

            // Assert the EFFECT of --counters, never the name of the variable.
            if (opt.CountersRequested && !PhaseClock.CountersOn)
            {
                throw new CliException("--counters did not take effect: the counter switch was already read as off "
                                       + "before the command started.", ExitCodes.Internal);
            }

            PilotSeedOrder.Verify();
            string? outPath = opt.Out == null ? null : ResolveOut(opt.Out);

            DumpedLocationOracle? oracle = null;
            bool needsOracle = opt.Sections.Exists(s => s.Tier == "t5");
            if (needsOracle)
            {
                // Fails closed like every location answer: a table from another build places other worlds.
                oracle = DumpedLocationOracle.Open();
            }

            try
            {
                CheckMemory(rt, opt);
                if (!o.Json)
                {
                    rt.PrintStartup("Profile");
                    Console.Error.WriteLine("  profiling " + opt.Sections.Count + " section(s) at " + string.Join(", ", opt.WorkerCounts)
                                            + " worker(s); counters " + (PhaseClock.CountersOn ? "ON" : "off"));
                }

                ProfileRun run = new ProfileRun
                {
                    CreatedUtc = DateTime.UtcNow,
                    Key = opt.Key,
                    From = opt.From,
                    Warmup = opt.Warmup,
                    WorkerCounts = opt.WorkerCounts,
                    CountersOn = PhaseClock.CountersOn,
                    TierText = opt.TierText,
                    Machine = ProfileReport.Machine(rt),
                    Build = ProfileReport.Build(oracle),
                };

                // ---- hygiene: a baseline, then a probe for the whole run -----------------------------
                if (opt.QuietSeconds > 0)
                {
                    if (!o.Json) Console.Error.WriteLine("  watching the machine for " + opt.QuietSeconds + " s before measuring...");
                    run.Baseline = QuietMachineProbe.TakeBaseline(TimeSpan.FromSeconds(opt.QuietSeconds));
                }

                using QuietMachineProbe probe = QuietMachineProbe.Start(baseline: run.Baseline);
                run.Probe = probe;

                if (opt.Overhead)
                {
                    run.Overhead = MeasureOverhead(opt, rt, probe, o.Json);
                }
                else
                {
                    foreach (int workers in opt.WorkerCounts)
                    {
                        foreach (ProfileSection spec in opt.Sections)
                        {
                            if (!o.Json) Console.Error.WriteLine("  " + spec.Label + " x" + spec.Seeds + " at " + workers + " worker(s)...");
                            run.Sections.Add(RunSection(spec, workers, opt.Warmup, opt.Key, opt.From, oracle,
                                                        attachSink: true, recordRows: true));
                        }
                    }
                }

                probe.Stop();
                run.EndedUtc = DateTime.UtcNow;
                ProfileReport report = new ProfileReport(run);

                if (outPath != null)
                {
                    report.WriteJson(outPath);
                    if (opt.PerSeed) report.WritePerSeedCsv(PerSeedPath(outPath));
                }

                if (o.Json)
                {
                    report.WriteTo(o.J);
                }
                else
                {
                    report.Print(o);
                    if (outPath != null)
                    {
                        o.Line();
                        o.Field("written", outPath + (opt.PerSeed ? " and " + PerSeedPath(outPath) : ""));
                    }
                }

                return ExitCodes.Ok;
            }
            finally
            {
                oracle?.Dispose();
            }
        }

        private static Options Parse(Args a)
        {
            Options opt = new Options();
            string tier = (a.Get("tier") ?? "all").Trim().ToLowerInvariant();
            opt.TierText = tier;
            List<int>? grids = a.Get("grid") is string g ? ParseList(g, "grid", ParseGrid) : null;
            List<int>? prefixes = a.Get("prefix") is string p ? ParseList(p, "prefix", ParsePositive) : null;
            int? seeds = a.Get("seeds") is string s ? ParsePositive(s, "seeds") : null;
            if (seeds.HasValue && seeds.Value > 1_000_000) throw new CliException("--seeds is capped at 1,000,000 per section.");

            if (a.Get("key") is string k) opt.Key = ParseKey(k);
            if (a.Get("from") is string f)
            {
                if (!long.TryParse(f, NumberStyles.Integer, CultureInfo.InvariantCulture, out long from) || from < 0 || from > uint.MaxValue)
                {
                    throw new CliException("--from takes an index from 0 to 4294967295, not '" + f + "'.");
                }

                opt.From = from;
            }

            if (a.Get("profile-threads") is string t) opt.WorkerCounts = ParseList(t, "threads", ParsePositive);
            foreach (int w in opt.WorkerCounts)
            {
                if (w > Args.MaxThreads) throw new CliException("--threads is capped at " + Args.MaxThreads + " here, not " + w + ".");
            }

            opt.Warmup = a.Int("warmup", 3);
            if (opt.Warmup < 0 || opt.Warmup > 1000) throw new CliException("--warmup must be between 0 and 1000.");
            opt.CountersRequested = a.Flag("counters");
            opt.Overhead = a.Flag("overhead");
            opt.Baseline = a.Get("baseline");
            opt.QuietSeconds = a.Int("quiet-baseline", 30);
            if (opt.QuietSeconds < 0 || opt.QuietSeconds > 600) throw new CliException("--quiet-baseline takes 0 to 600 seconds.");
            opt.Out = a.Get("out");
            opt.PerSeed = a.Flag("per-seed");
            if (opt.PerSeed && opt.Out == null) throw new CliException("--per-seed writes beside --out; give --out <file.json> as well.");
            if (opt.Baseline != null && !opt.Overhead) throw new CliException("--baseline only means something with --overhead.");
            if (opt.Baseline != null && !File.Exists(opt.Baseline)) throw new CliException("--baseline: no file at " + opt.Baseline, ExitCodes.NotFound);

            if (opt.Overhead)
            {
                if (opt.WorkerCounts.Count != 1 || opt.WorkerCounts[0] != 1)
                {
                    throw new CliException("--overhead measures on one worker; leave --threads out.");
                }

                string ot = tier == "all" ? "t2" : tier;
                if (ot == "t5") throw new CliException("--overhead times t2, t3 or t4 - a t5 leg is minutes per repetition.");
                int grid = grids != null && grids.Count > 0 ? grids[0] : 384;
                opt.Sections.Add(new ProfileSection(ot, ot == "t4" ? 0 : grid, 0, seeds ?? 200));
                opt.TierText = ot;
                return opt;
            }

            switch (tier)
            {
                case "all":
                    if (grids == null && prefixes == null && seeds == null)
                    {
                        // The fixed battery (the pilot's P0): comparable run to run.
                        opt.Sections.Add(new ProfileSection("t2", 384, 0, 500));
                        opt.Sections.Add(new ProfileSection("t3", 384, 0, 500));
                        opt.Sections.Add(new ProfileSection("t3", 12, 0, 100));
                        opt.Sections.Add(new ProfileSection("t5", 0, 2, 200));
                        opt.Sections.Add(new ProfileSection("t5", 0, 22, 200));
                        opt.Sections.Add(new ProfileSection("t5", 0, 67, 200));
                        opt.Sections.Add(new ProfileSection("t5", 0, 183, 100));
                    }
                    else
                    {
                        int n = seeds ?? DefaultSeeds;
                        foreach (int gr in grids ?? new List<int> { 384 }) opt.Sections.Add(new ProfileSection("t2", gr, 0, n));
                        foreach (int gr in grids ?? new List<int> { 384 }) opt.Sections.Add(new ProfileSection("t3", gr, 0, n));
                        foreach (int pr in prefixes ?? new List<int> { 22 }) opt.Sections.Add(new ProfileSection("t5", 0, pr, n));
                    }

                    break;
                case "t2":
                case "t3":
                    foreach (int gr in grids ?? new List<int> { 384 }) opt.Sections.Add(new ProfileSection(tier, gr, 0, seeds ?? DefaultSeeds));
                    if (prefixes != null) throw new CliException("--prefix belongs to --tier t5.");
                    break;
                case "t4":
                    if (grids != null || prefixes != null) throw new CliException("--tier t4 takes neither --grid nor --prefix.");
                    opt.Sections.Add(new ProfileSection("t4", 0, 0, seeds ?? DefaultSeeds));
                    break;
                case "t5":
                    if (grids != null) throw new CliException("--grid belongs to --tier t2 or t3; t5 always builds the game's own 12 m grid.");
                    foreach (int pr in prefixes ?? new List<int> { 22 })
                    {
                        if (pr > 183) throw new CliException("--prefix: the ordered location list has 183 entries, not " + pr + ".");
                        opt.Sections.Add(new ProfileSection("t5", 0, pr, seeds ?? DefaultSeeds));
                    }

                    break;
                default:
                    throw new CliException("--tier takes t2, t3, t4, t5 or all, not '" + tier + "'.");
            }

            return opt;
        }

        private static List<int> ParseList(string text, string name, Func<string, string, int> one)
        {
            List<int> r = new List<int>();
            foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int v = one(part, name);
                if (!r.Contains(v)) r.Add(v);
            }

            if (r.Count == 0) throw new CliException("--" + name + " needs at least one value.");
            return r;
        }

        private static int ParsePositive(string text, string name)
        {
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) || v < 1)
            {
                throw new CliException("--" + name + " takes a whole number of at least 1, not '" + text + "'.");
            }

            return v;
        }

        private static int ParseGrid(string text, string name)
        {
            string t = text.Trim().TrimStart('G', 'g');
            if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
                && PhaseMap.T2ForGrid(v) != Phase.None)
            {
                return v;
            }

            throw new CliException("--grid takes G384, G192, G96, G24 or G12 (the grids with a phase of their own), not '" + text + "'.");
        }

        private static ulong ParseKey(string text)
        {
            string t = text.Trim();
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t.Substring(2);
            if (!ulong.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong k))
            {
                throw new CliException("--key takes a 64-bit hex key such as " + PilotSeedOrder.KeyText + ", not '" + text + "'.");
            }

            return k;
        }

        // ---------------------------------------------------------------------------------------------
        // Where a profile may be written
        // ---------------------------------------------------------------------------------------------

        private static string ResolveOut(string path)
        {
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception ex)
            {
                throw new CliException("--out: '" + path + "' is not a usable path (" + ex.Message + ").");
            }

            if (Directory.Exists(full)) throw new CliException("--out names a folder; give a file, e.g. " + Path.Combine(full, "profile.json"));
            string? why = ProtectedReason(full);
            if (why != null)
            {
                throw new CliException("--out: refused - " + full + " is inside " + why
                                       + ". A profile is written somewhere of your own. Nothing was measured and nothing was written.");
            }

            string? dir = Path.GetDirectoryName(full);
            if (dir != null && !Directory.Exists(dir))
            {
                throw new CliException("--out: the folder " + dir + " does not exist.", ExitCodes.NotFound);
            }

            return full;
        }

        /// <summary>
        /// Why <paramref name="full"/> must not be written, or null. Valheim's save folder, Steam's
        /// userdata (where the cloud copies of the saves live) and the game's install folder are never
        /// written - except a <c>_ModSource</c> folder inside the install, which is where modding work
        /// that lives beside the game keeps its own files and is not the game's.
        /// </summary>
        internal static string? ProtectedReason(string full)
        {
            foreach (string d in SeedLab.Saves.SaveDiscovery.GameDataDirectoryCandidates())
            {
                if (IsUnder(full, d)) return "Valheim's save folder (" + d + ")";
            }

            foreach (string steam in SeedLab.Saves.SaveDiscovery.SteamRootCandidates())
            {
                string userdata = Path.Combine(steam, "userdata");
                if (IsUnder(full, userdata)) return "Steam's userdata, where the cloud copies of saves live (" + userdata + ")";
            }

            string? game = null;
            try
            {
                game = SeedLab.Data.GameInstall.FindDirectory();
            }
            catch (Exception)
            {
                // No install found: nothing to protect.
            }

            if (game != null && IsUnder(full, game) && !IsUnder(full, Path.Combine(game, "_ModSource")))
            {
                return "the game's install folder (" + game + ")";
            }

            return null;
        }

        private static bool IsUnder(string path, string folder)
        {
            try
            {
                string p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string f = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return p.Equals(f, StringComparison.OrdinalIgnoreCase)
                       || p.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string PerSeedPath(string outPath)
        {
            string dir = Path.GetDirectoryName(outPath) ?? ".";
            return Path.Combine(dir, Path.GetFileNameWithoutExtension(outPath) + "-seeds.csv");
        }

        /// <summary>Every requested worker count must fit the memory guard; the arithmetic is the plan's own.</summary>
        private static void CheckMemory(CliRuntime rt, Options opt)
        {
            foreach (ProfileSection s in opt.Sections)
            {
                WorkTier tier = s.Tier switch
                {
                    "t2" => WorkTier.BiomeGrid,
                    "t5" => s.Prefix > 22 ? WorkTier.LocationsAll : WorkTier.LocationsCore,
                    _ => WorkTier.HeightsRivers,
                };
                WorkerPlan plan = rt.Plan(tier, s.Tier == "t5" || s.Grid == 0 ? 12.0 : s.Grid);
                foreach (int w in opt.WorkerCounts)
                {
                    if (w > plan.MemoryWorkers)
                    {
                        throw new CliException(s.Label + " at " + w + " workers does not fit the memory guard: it allows "
                                               + plan.MemoryWorkers + ". Nothing was measured.", ExitCodes.Usage,
                                               string.Join(Environment.NewLine, plan.Lines()));
                    }
                }
            }
        }

        // ---------------------------------------------------------------------------------------------
        // One section
        // ---------------------------------------------------------------------------------------------

        /// <summary>One worker's reusable state: its sink, its sampling buffers and its plans.</summary>
        private sealed class WorkerState
        {
            public PhaseSink? Sink;
            public SeedSampler? Sampler;
            public WorldMeasurement? Measure;
            public WorldMeasurement? PatchMeasure;
            public long Checksum;
        }

        private static readonly MeasurementPlan s_biomePlan = new MeasurementPlan { NeedBiomes = true };

        private static readonly MeasurementPlan s_patchPlan = BuildPatchPlan();

        private static readonly MeasurementPlan s_heightPlan = BuildHeightPlan();

        private static MeasurementPlan BuildPatchPlan()
        {
            MeasurementPlan p = new MeasurementPlan { NeedBiomes = true };
            for (int i = 0; i < p.NeedLargestPatch.Length; i++) p.NeedLargestPatch[i] = true;
            return p;
        }

        /// <summary>
        /// The height pass the profile times: land and water, the peak, one "land within" and one "area
        /// above" accumulator, the island analysis at 1 km^2 and the 100 m shore band over the whole
        /// world - the shape of the terrain presets' height goals, so a t3 figure is a real query's.
        /// </summary>
        private static MeasurementPlan BuildHeightPlan()
        {
            MeasurementPlan p = new MeasurementPlan { NeedBiomes = true, NeedHeights = true };
            p.AddIslandMinArea(1_000_000.0);
            p.AddLandAreaWithin(SeedSampler.WaterEdge);
            p.AddAreaAbove(-1, 100.0);
            p.AddShoreWithin(SeedSampler.WaterEdge);
            return p;
        }

        internal static string DescribeWork(string tier) => tier switch
        {
            "t2" => "construct (deferred); SampleBiomes + MeasureBiomes (counts); patch = MeasureBiomes again with the largest-patch flood fill for all ten biomes",
            "t3" => "construct (deferred); SampleBiomes + MeasureBiomes; ForcePregeneration; MeasureStructures; SampleHeights + MeasureHeights (land, peak, land within 10.5 km, above 100 m, islands >= 1 km2, shore band)",
            "t4" => "construct (deferred); ForcePregeneration; MeasureStructures",
            "t5" => "DumpedLocationOracle.Run with no gate: its own eager generator, BiomeGrid, BiomeField, alt biomes, placement of the prefix, harvest",
            _ => "",
        };

        internal static SectionResult RunSection(ProfileSection spec, int workers, int warmup, ulong key, long from,
                                                 DumpedLocationOracle? oracle, bool attachSink, bool recordRows)
        {
            SectionResult res = new SectionResult(spec, workers, warmup);
            ScanPlanReader order = new ScanPlanReader(key);
            int n = spec.Seeds;
            int[] seeds = new int[n];
            for (int i = 0; i < n; i++) seeds[i] = order.At(from + i);
            res.Seeds = seeds;

            LocationPlan? plan = null;
            if (spec.Tier == "t5")
            {
                if (oracle == null) throw new InvalidOperationException("a t5 section needs the location oracle");
                List<string> prefabs = new List<string>();
                HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < spec.Prefix && i < oracle.Table.Ordered.Count; i++)
                {
                    string p = oracle.Table.Ordered[i].PrefabName;
                    if (seen.Add(p)) prefabs.Add(p);
                }

                plan = oracle.Plan(prefabs, needSpawn: false);
                res.PrefixRun = plan.PrefixLength;
            }

            FieldGrid? grid = spec.Grid > 0 ? SearchGrids.ForSpacing(spec.Grid) : null;
            Phase t2Phase = spec.Grid > 0 ? PhaseMap.T2ForGrid(spec.Grid) : Phase.None;
            Phase t3Phase = spec.Grid > 0 ? PhaseMap.T3ForGrid(spec.Grid) : Phase.None;
            Action<int, WorkerState> work = spec.Tier switch
            {
                "t2" => (seed, st) =>
                {
                    WorldGeneratorPort gen = new WorldGeneratorPort(seed, Verified.WorldGenVersion, menu: false,
                                                                    deferPregeneration: true);
                    st.Sink?.Begin(t2Phase);
                    st.Sampler!.SampleBiomes(gen, SeedSampler.WaterEdge);
                    st.Measure!.Reset(s_biomePlan);
                    st.Measure.MeasureBiomes(st.Sampler, s_biomePlan);
                    st.Sink?.End(t2Phase);
                    st.Sink?.Begin(Phase.Patch);
                    st.PatchMeasure!.Reset(s_patchPlan);
                    st.PatchMeasure.MeasureBiomes(st.Sampler, s_patchPlan);
                    st.Sink?.End(Phase.Patch);
                    st.Checksum += st.Measure.InWorldCells + st.PatchMeasure.LargestPatchCells[1];
                },
                "t3" => (seed, st) =>
                {
                    WorldGeneratorPort gen = new WorldGeneratorPort(seed, Verified.WorldGenVersion, menu: false,
                                                                    deferPregeneration: true);
                    st.Sink?.Begin(t2Phase);
                    st.Sampler!.SampleBiomes(gen, SeedSampler.WaterEdge);
                    st.Measure!.Reset(s_heightPlan);
                    st.Measure.MeasureBiomes(st.Sampler, s_heightPlan);
                    st.Sink?.End(t2Phase);
                    // Explicitly, as the search evaluator does, so pre-generation is its own phase and
                    // never hides inside the height pass that would otherwise trigger it.
                    gen.ForcePregeneration();
                    st.Sink?.Begin(Phase.T4Measure);
                    st.Measure.MeasureStructures(gen);
                    st.Sink?.End(Phase.T4Measure);
                    st.Sink?.Begin(t3Phase);
                    st.Sampler.SampleHeights(gen);
                    st.Measure.MeasureHeights(st.Sampler, s_heightPlan);
                    st.Sink?.End(t3Phase);
                    st.Checksum += st.Measure.LandCells + st.Measure.RiverCount;
                },
                "t4" => (seed, st) =>
                {
                    WorldGeneratorPort gen = new WorldGeneratorPort(seed, Verified.WorldGenVersion, menu: false,
                                                                    deferPregeneration: true);
                    gen.ForcePregeneration();
                    st.Sink?.Begin(Phase.T4Measure);
                    st.Measure!.MeasureStructures(gen);
                    st.Sink?.End(Phase.T4Measure);
                    st.Checksum += st.Measure.RiverCount + st.Measure.LakeCount + st.Measure.StreamCount;
                },
                "t5" => (seed, st) =>
                {
                    LocationWorld w = oracle!.Run(plan!, seed, Verified.WorldGenVersion, gate: null);
                    st.Checksum += w.Hits.Count;
                },
                _ => throw new InvalidOperationException("unknown tier " + spec.Tier),
            };

            SeedRow?[] rows = new SeedRow?[n];
            WorkerStat[] stats = new WorkerStat[workers];
            int next = -1;
            Exception? fault = null;
            object gate = new object();
            WindowMark start = default;

            // Two rendezvous: everyone finishes warming up, the window opens (JIT, GC and CPU counters
            // read once, with nobody working), everyone measures.
            using Barrier warm = new Barrier(workers + 1);
            Thread[] threads = new Thread[workers];
            for (int w = 0; w < workers; w++)
            {
                int wid = w;
                threads[w] = new Thread(() =>
                {
                    WorkerState st = new WorkerState { Sink = attachSink ? new PhaseSink() : null };
                    PhaseSink.Current = st.Sink;
                    PhaseClock.ResetCounters();
                    if (grid != null)
                    {
                        st.Sampler = new SeedSampler(grid);
                        st.Measure = new WorldMeasurement(grid);
                        st.PatchMeasure = new WorldMeasurement(grid);
                    }
                    else
                    {
                        // t4 reads only the structure counts, which ignore the grid; t5 uses none.
                        st.Measure = new WorldMeasurement(SearchGrids.ForSpacing(384));
                    }

                    long[] before = new long[PhaseSink.SnapshotLength];
                    long[] after = new long[PhaseSink.SnapshotLength];
                    long[] cBefore = new long[PhaseClock.Capacity];
                    long[] cAfter = new long[PhaseClock.Capacity];
                    WorkerStat stat = new WorkerStat { Id = wid };
                    try
                    {
                        try
                        {
                            for (int k = 0; k < warmup; k++) work(order.At(from + n + (long)wid * warmup + k), st);
                        }
                        finally
                        {
                            warm.SignalAndWait();
                            warm.SignalAndWait();
                        }

                        int i;
                        while ((i = Interlocked.Increment(ref next)) < n)
                        {
                            if (recordRows && st.Sink != null)
                            {
                                st.Sink.Snapshot(before);
                                if (PhaseClock.CountersOn) PhaseClock.SnapshotCounters(cBefore);
                            }

                            long a0 = GC.GetAllocatedBytesForCurrentThread();
                            long t0 = Stopwatch.GetTimestamp();
                            work(seeds[i], st);
                            long t1 = Stopwatch.GetTimestamp();
                            long a1 = GC.GetAllocatedBytesForCurrentThread();
                            stat.Seeds++;
                            stat.BusyTicks += t1 - t0;

                            SeedRow row = new SeedRow { Seed = seeds[i], Worker = wid, WallTicks = t1 - t0, AllocBytes = a1 - a0 };
                            if (recordRows && st.Sink != null)
                            {
                                st.Sink.Snapshot(after);
                                row.Phase = new long[PhaseSink.SnapshotLength];
                                for (int c = 0; c < PhaseSink.SnapshotLength; c++) row.Phase[c] = after[c] - before[c];
                                if (PhaseClock.CountersOn)
                                {
                                    PhaseClock.SnapshotCounters(cAfter);
                                    row.Counters = new long[PhaseClock.Capacity];
                                    for (int c = 0; c < PhaseClock.Capacity; c++) row.Counters[c] = cAfter[c] - cBefore[c];
                                }
                            }

                            rows[i] = row;
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (gate) fault ??= ex;
                    }
                    finally
                    {
                        stats[wid] = stat;
                        PhaseSink.Current = null;
                        GC.KeepAlive(st.Checksum);
                    }
                })
                {
                    IsBackground = true,
                    Name = "profile-" + w,
                };
                threads[w].Start();
            }

            warm.SignalAndWait();          // every worker has warmed up
            start = WindowMark.Now();
            res.StartUtc = DateTime.UtcNow;
            warm.SignalAndWait();          // release them into the measured window
            foreach (Thread t in threads) t.Join();
            WindowMark end = WindowMark.Now();
            res.EndUtc = DateTime.UtcNow;
            if (fault != null) throw new InvalidOperationException("a profile worker failed: " + fault.Message, fault);

            res.Rows = new List<SeedRow>(n);
            foreach (SeedRow? r in rows) res.Rows.Add(r!);
            res.Workers = new List<WorkerStat>(stats);
            res.WallSeconds = (end.Timestamp - start.Timestamp) / (double)Stopwatch.Frequency;
            res.CpuSeconds = (end.Cpu - start.Cpu).TotalSeconds;
            res.Gen0 = end.Gen0 - start.Gen0;
            res.Gen1 = end.Gen1 - start.Gen1;
            res.Gen2 = end.Gen2 - start.Gen2;
            res.PauseMs = (end.Pause - start.Pause).TotalMilliseconds;
            res.JitMethods = end.JitMethods - start.JitMethods;
            res.JitMs = (end.JitTime - start.JitTime).TotalMilliseconds;
            return res;
        }

        /// <summary>The seed order, read without allocating a plan per seed.</summary>
        private sealed class ScanPlanReader
        {
            private readonly SeedLab.Search.Execution.ScanPlan _plan;
            public ScanPlanReader(ulong key) { _plan = PilotSeedOrder.Plan(key); }
            public int At(long index) => _plan.SeedAt(index % 4294967296L);
        }

        /// <summary>Process-wide counters read at the edges of a measured window.</summary>
        private readonly struct WindowMark
        {
            public long Timestamp { get; init; }
            public TimeSpan Cpu { get; init; }
            public int Gen0 { get; init; }
            public int Gen1 { get; init; }
            public int Gen2 { get; init; }
            public TimeSpan Pause { get; init; }
            public long JitMethods { get; init; }
            public TimeSpan JitTime { get; init; }

            public static WindowMark Now()
            {
                using Process self = Process.GetCurrentProcess();
                return new WindowMark
                {
                    Timestamp = Stopwatch.GetTimestamp(),
                    Cpu = self.TotalProcessorTime,
                    Gen0 = GC.CollectionCount(0),
                    Gen1 = GC.CollectionCount(1),
                    Gen2 = GC.CollectionCount(2),
                    Pause = GC.GetTotalPauseDuration(),
                    JitMethods = System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: false),
                    JitTime = System.Runtime.JitInfo.GetCompilationTime(currentThread: false),
                };
            }
        }

        // ---------------------------------------------------------------------------------------------
        // --overhead
        // ---------------------------------------------------------------------------------------------

        private const int OverheadReps = 5;

        private static OverheadResult MeasureOverhead(Options opt, CliRuntime rt, QuietMachineProbe probe, bool quiet)
        {
            ProfileSection spec = opt.Sections[0];
            OverheadResult r = new OverheadResult { Section = spec, Reps = OverheadReps };

            // Predicted: boundaries per seed, times what one Begin/End pair costs here.
            r.PairNs = MeasurePairNs();
            r.TimestampNs = ProfileReport.TimestampNs();
            SectionResult probeRun = RunSection(spec, 1, opt.Warmup, opt.Key, opt.From, null, attachSink: true, recordRows: true);
            double entries = 0;
            foreach (SeedRow row in probeRun.Rows)
            {
                for (int p = 0; p < PhaseMap.Capacity; p++) entries += row.Phase![PhaseSink.EntriesOffset + p];
            }

            r.BoundariesPerSeed = entries / Math.Max(1, probeRun.Rows.Count);
            r.SeedMsProfiled = probeRun.WallSeconds * 1000.0 / Math.Max(1, probeRun.Rows.Count);
            r.PredictedPct = 100.0 * r.BoundariesPerSeed * r.PairNs / 1e6 / Math.Max(1e-9, r.SeedMsProfiled);

            // Measured, in this process: A = no sink on the thread, B = a sink recording every boundary.
            if (!quiet) Console.Error.WriteLine("  sink off/on, ABAB x " + OverheadReps + " in this process...");
            for (int i = 0; i < OverheadReps; i++)
            {
                r.SinkOffS.Add(RunSection(spec, 1, opt.Warmup, opt.Key, opt.From, null, attachSink: false, recordRows: false).WallSeconds);
                r.SinkOnS.Add(RunSection(spec, 1, opt.Warmup, opt.Key, opt.From, null, attachSink: true, recordRows: false).WallSeconds);
            }

            // Counters can only differ between processes: A = phases only, B = phases + counters.
            if (!quiet) Console.Error.WriteLine("  counters off/on, ABAB x " + OverheadReps + " in child processes...");
            string self = Environment.ProcessPath ?? throw new CliException("cannot find this vseed to start its timing legs.", ExitCodes.Internal);
            for (int i = 0; i < OverheadReps; i++)
            {
                r.CountersOffS.Add(ChildLeg(self, rt, probe, opt, counters: false));
                r.CountersOnS.Add(ChildLeg(self, rt, probe, opt, counters: true));
            }

            // Profiler off, against another build: the same one-worker search on both.
            if (opt.Baseline != null)
            {
                if (!quiet) Console.Error.WriteLine("  this build against " + opt.Baseline + ", ABAB x " + OverheadReps + "...");
                r.BaselineExe = Path.GetFullPath(opt.Baseline);
                r.BaselineSha256 = ProfileReport.Sha256OfFile(Path.Combine(Path.GetDirectoryName(r.BaselineExe) ?? ".", "SeedLab.WorldGen.dll"));
                string query = WriteOverheadQuery(rt, spec);
                int searchSeeds = Math.Max(2000, spec.Seeds * 10);
                r.SearchSeeds = searchSeeds;
                for (int i = 0; i < OverheadReps; i++)
                {
                    r.BaselineSps.Add(SearchLeg(r.BaselineExe, rt, probe, query, searchSeeds, i));
                    r.ThisSps.Add(SearchLeg(self, rt, probe, query, searchSeeds, i));
                }
            }

            return r;
        }

        private static double MeasurePairNs()
        {
            PhaseSink s = new PhaseSink();
            for (int i = 0; i < 200_000; i++) { s.Begin(Phase.AtlasRow); s.End(Phase.AtlasRow); }
            const int n = 2_000_000;
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < n; i++) { s.Begin(Phase.AtlasRow); s.End(Phase.AtlasRow); }
            long t1 = Stopwatch.GetTimestamp();
            return (t1 - t0) * 1e9 / Stopwatch.Frequency / n;
        }

        /// <summary>
        /// Hidden child mode for --overhead: one leg with the sink attached, printing
        /// <c>{"wall_s":..,"seeds":..,"counters_on":..}</c>.
        /// </summary>
        private static int OverheadLeg(Args a)
        {
            string tier = a.Get("tier") ?? "t2";
            int grid = a.Int("grid", 384);
            int seeds = a.Int("seeds", 200);
            int warmup = a.Int("warmup", 3);
            ulong key = a.Get("key") is string k ? ParseKey(k) : PilotSeedOrder.Key;
            long from = long.Parse(a.Get("from") ?? "0", CultureInfo.InvariantCulture);
            a.Flag("counters");
            a.RejectUnknown();
            SectionResult r = RunSection(new ProfileSection(tier, tier == "t4" ? 0 : grid, 0, seeds), 1, warmup, key, from,
                                         null, attachSink: true, recordRows: false);
            Console.Out.WriteLine("{\"wall_s\":" + r.WallSeconds.ToString("R", CultureInfo.InvariantCulture)
                                  + ",\"seeds\":" + seeds.ToString(CultureInfo.InvariantCulture)
                                  + ",\"counters_on\":" + (PhaseClock.CountersOn ? "true" : "false") + "}");
            return ExitCodes.Ok;
        }

        private static double ChildLeg(string exe, CliRuntime rt, QuietMachineProbe probe, Options opt, bool counters)
        {
            ProfileSection s = opt.Sections[0];
            List<string> args = new List<string>
            {
                "profile", "--overhead-leg", "--tier", s.Tier, "--grid", s.Grid.ToString(CultureInfo.InvariantCulture),
                "--seeds", s.Seeds.ToString(CultureInfo.InvariantCulture),
                "--warmup", opt.Warmup.ToString(CultureInfo.InvariantCulture),
                "--key", "0x" + opt.Key.ToString("X16", CultureInfo.InvariantCulture),
                "--from", opt.From.ToString(CultureInfo.InvariantCulture),
                "--cache-dir", rt.Cache.Path, "--ignore-running-game",
            };
            if (counters) args.Add("--counters");
            string stdout = RunChild(exe, args, probe, counters ? "1" : "0");
            using JsonDocument doc = JsonDocument.Parse(stdout);
            bool on = doc.RootElement.GetProperty("counters_on").GetBoolean();
            if (on != counters)
            {
                throw new CliException("a counters " + (counters ? "on" : "off") + " leg ran with counters "
                                       + (on ? "on" : "off") + " - the switch had no effect, so the comparison would be meaningless.",
                                       ExitCodes.CheckFailed);
            }

            return doc.RootElement.GetProperty("wall_s").GetDouble();
        }

        private static string WriteOverheadQuery(CliRuntime rt, ProfileSection s)
        {
            string path = rt.Context.Scratch("profile").File("overhead-query.json");
            string json = "{ \"version\": 1, \"defs\": 1, \"name\": \"profile-overhead\",\n"
                          + "  \"search\": { \"order\": \"shuffled\", \"key\": \"0x" + PilotSeedOrder.Key.ToString("X16", CultureInfo.InvariantCulture)
                          + "\", \"grid\": " + (s.Grid > 0 ? s.Grid : 384).ToString(CultureInfo.InvariantCulture)
                          + ", \"keep\": 10, \"screen\": \"off\" },\n"
                          + "  \"goals\": [ { \"id\": \"meadows\", \"target\": \"biome:Meadows\", \"metric\": \"area_within\", \"radius\": 10500,"
                          + " \"test\": \"at_least\", \"value\": 1, \"importance\": \"nice\" } ] }\n";
            File.WriteAllText(path, json, new UTF8Encoding(false));
            return path;
        }

        private static double SearchLeg(string exe, CliRuntime rt, QuietMachineProbe probe, string query, int seeds, int rep)
        {
            string outFile = rt.Context.Scratch("profile").File("overhead-" + rep.ToString(CultureInfo.InvariantCulture) + ".jsonl");
            List<string> args = new List<string>
            {
                "search", query, "--seeds", seeds.ToString(CultureInfo.InvariantCulture), "--threads", "1",
                "--screen", "off", "--keep", "10", "--out", outFile, "--yes", "--progress", "none", "--json",
                "--cache-dir", rt.Cache.Path, "--ignore-running-game",
            };
            string stdout = RunChild(exe, args, probe, "0");
            using JsonDocument doc = JsonDocument.Parse(stdout);
            return doc.RootElement.GetProperty("seeds_per_second").GetDouble();
        }

        private static string RunChild(string exe, List<string> args, QuietMachineProbe probe, string counters)
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = exe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
            };
            foreach (string s in args) psi.ArgumentList.Add(s);
            psi.Environment[PhaseClock.EnvironmentVariable] = counters;
            using Process p = Process.Start(psi) ?? throw new CliException("could not start " + exe, ExitCodes.Internal);
            probe.Exclude(p.Id);
            p.StandardInput.Close();
            System.Threading.Tasks.Task<string> err = p.StandardError.ReadToEndAsync();
            string stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0)
            {
                throw new CliException("a timing leg (" + Path.GetFileName(exe) + " " + string.Join(" ", args) + ") exited "
                                       + p.ExitCode + ": " + err.Result.Trim(), ExitCodes.CheckFailed);
            }

            return stdout;
        }
    }

    /// <summary>One thing to time: a tier, at a grid or a location prefix, over a number of seeds.</summary>
    internal sealed class ProfileSection
    {
        public ProfileSection(string tier, int grid, int prefix, int seeds)
        {
            Tier = tier;
            Grid = grid;
            Prefix = prefix;
            Seeds = seeds;
        }

        public string Tier { get; }

        /// <summary>Sampling grid in metres for t2/t3, 0 otherwise.</summary>
        public int Grid { get; }

        /// <summary>Ordered location entries for t5, 0 otherwise.</summary>
        public int Prefix { get; }

        public int Seeds { get; }

        public string Label => Tier + (Grid > 0 ? " G" + Grid.ToString(CultureInfo.InvariantCulture) : "")
                               + (Prefix > 0 ? " prefix " + Prefix.ToString(CultureInfo.InvariantCulture) : "");
    }

    /// <summary>One measured seed: its wall time and its sink's per-phase deltas.</summary>
    internal sealed class SeedRow
    {
        public int Seed;
        public int Worker;
        public long WallTicks;
        public long AllocBytes;

        /// <summary><see cref="PhaseSink.Snapshot"/> deltas over this seed, or null when not recorded.</summary>
        public long[]? Phase;

        /// <summary>Counter deltas over this seed, or null when counters are off.</summary>
        public long[]? Counters;
    }

    internal sealed class WorkerStat
    {
        public int Id;
        public long Seeds;
        public long BusyTicks;
    }

    internal sealed class SectionResult
    {
        public SectionResult(ProfileSection spec, int workers, int warmup)
        {
            Spec = spec;
            WorkerCount = workers;
            Warmup = warmup;
        }

        public ProfileSection Spec { get; }
        public int WorkerCount { get; }
        public int Warmup { get; }
        public int[] Seeds = Array.Empty<int>();
        public int PrefixRun;
        public List<SeedRow> Rows = new List<SeedRow>();
        public List<WorkerStat> Workers = new List<WorkerStat>();
        public double WallSeconds;
        public double CpuSeconds;
        public int Gen0, Gen1, Gen2;
        public double PauseMs;
        public long JitMethods;
        public double JitMs;
        public DateTime StartUtc, EndUtc;
    }

    internal sealed class OverheadResult
    {
        public ProfileSection Section = null!;
        public int Reps;
        public double PairNs;
        public double TimestampNs;
        public double BoundariesPerSeed;
        public double SeedMsProfiled;
        public double PredictedPct;
        public List<double> SinkOffS = new List<double>();
        public List<double> SinkOnS = new List<double>();
        public List<double> CountersOffS = new List<double>();
        public List<double> CountersOnS = new List<double>();
        public string? BaselineExe;
        public string? BaselineSha256;
        public int SearchSeeds;
        public List<double> BaselineSps = new List<double>();
        public List<double> ThisSps = new List<double>();
    }

    internal sealed class ProfileRun
    {
        public DateTime CreatedUtc;
        public DateTime EndedUtc;
        public ulong Key;
        public long From;
        public int Warmup;
        public List<int> WorkerCounts = new List<int>();
        public bool CountersOn;
        public string TierText = "";
        public Dictionary<string, object?> Machine = new Dictionary<string, object?>();
        public Dictionary<string, object?> Build = new Dictionary<string, object?>();
        public QuietBaseline? Baseline;
        public QuietMachineProbe? Probe;
        public List<SectionResult> Sections = new List<SectionResult>();
        public OverheadResult? Overhead;
    }
}
