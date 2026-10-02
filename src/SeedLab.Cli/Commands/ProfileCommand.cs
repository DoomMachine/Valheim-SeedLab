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
    /// machine for a baseline before it starts and samples every process's CPU, and the whole machine's,
    /// throughout (<see cref="QuietMachineProbe"/>, with <see cref="MachineCpu"/> for the processes
    /// Windows will not report on), and a run another SeedLab program, the game, a build or background
    /// load overlapped is marked TAINTED, with what tainted it, rather than trusted. Every worker discards its first seeds
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
  the load it saw - instead of pretending. It writes no results unless --out is given
  (vseed's session log is always kept in the cache root).

what
  --tier t2|t3|t4|t5|all   default all, the fixed battery of 7 sections: t2 G384 x500 seeds; t3 G384
                           x500 (with the structure counts); t3 G12 x100; t5 prefix 2, 22, 67 x200;
                           t5 prefix 183 x100. t2 = biome pass (+ largest patches), t3 = biomes,
                           pre-generation, structure counts and the height pass, t4 = pre-generation
                           and the structure counts, t5 = the location oracle's whole world build.
                           A measurement is one section at one worker count, so the battery is 7
                           measurements per worker count
  --grid <G>[,<G>...]      t2/t3 sampling grid(s): G384, G192, G96, G24, G12 (default G384)
  --prefix <n>[,<n>...]    t5: ordered location entries to place (default 22; 183 = all of them)
  --seeds <n>              seeds per section (default 64; overrides the battery's counts). Each
                           worker count runs the section again with the same n seeds
  --key <0x...>            the seed order's key (default 0xA17A25EED10C5117)
  --from <index>           first index into the order (default 0)
  --threads <n>[,<n>...]   worker counts to measure at, e.g. 1,8,16 or 1,max (max = every logical
                           core this machine has; default 1)
  --warmup <n>             seeds each worker runs and discards first (default 3)

how many seeds
  --saturate <seconds>     size every measurement to keep all its workers busy for about this long:
                           an uncounted pilot per measurement runs until it has worked for 10 % of
                           that (0.5 to 3 s) and times the later half of its seeds; the count is then
                           max(32 per worker, rate x seconds), rounded up to a multiple of the worker
                           count. The time is therefore about seconds x sections x worker counts,
                           plus a pilot and a warm-up per measurement; at t5 the 32-per-worker
                           minimum usually decides (all 183 entries at 16 workers: about 5 min per
                           measurement whatever you ask). A lower bound is printed before the watch
                           of the machine, the full plan before the first measurement. Not with
                           --seeds, --plan or --overhead
  --plan <profile.json>    replay an earlier profile exactly: its sections, worker counts, seeds, key,
                           first index and warm-up, after running its pilots again (uncounted), so a
                           before/after comparison measures the same worlds from the same start.
                           Refused when the plan was measured with the other --counters setting, or
                           its t5 sections placed locations from other game data; any other
                           difference (the build, the garbage collector, the machine) is printed and
                           recorded. Only a seedlab-profile/2 document is a plan. Not with --tier,
                           --grid, --prefix, --seeds, --threads or --saturate

how
  --counters               also count per-point events (base heights, world angles, river
                           lookups, stream tries). Fixed at process start; the timings then
                           include the counting
  --overhead               measure what the profiler itself costs instead (default t2 G384,
                           200 seeds, one worker): sink off/on in this process and counters
                           off/on in child processes, interleaved ABAB x 5, medians. Not with
                           --counters, nor with SEEDLAB_PROFILE_COUNTERS=1 in the environment
  --baseline <vseed.exe>   with --overhead: also time this build (profiler off) against another
                           vseed build on the same one-worker T2 G384 search
  --quiet-baseline <s>     watch the machine for s seconds before measuring (default 30; 0 skips)

output
  --out <file.json>        write the profile, schema seedlab-profile/2 (not inside a Valheim save
                           folder, Steam's userdata or the game's own install folders)
  --per-seed               with --out: also <file>-seeds.csv, one row per measured seed
  --json                   the document on stdout

Besides the phases, every measurement reports what it used: processor time (the whole process,
the workers themselves measured on their own threads, and the rest - garbage collection, the JIT,
the runtime), how busy the machine's cores were, the steady state (the stretch in which every
worker was busy, its rate, and what the start and the tail cost), memory sampled every 200 ms
(working set, private bytes, the garbage collector's heap and what it has committed) with the
last collection's details, and the bytes the process read and wrote. The profile itself writes
nothing while it measures: --out is written once at the end; vseed's session log is kept in the
cache root.

A before/after comparison: 'vseed profile --saturate 30 --out before.json' on the old build, then
'vseed profile --plan before.json --out after.json' on the new one.

The profile's own --threads is a list; the mode's worker share does not apply (with the game
running, the background mode only lowers the priority). Every requested count is still checked
against the memory guard, the profile's own per-seed table included.";

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
            public double? Saturate;
            public string? PlanPath;
            public bool KeyGiven, FromGiven, WarmupGiven;
        }

        /// <summary>One measurement to make: a section at a worker count, with its own seed count.</summary>
        internal sealed class ProfileConfig
        {
            public ProfileConfig(ProfileSection spec, int workers, SectionSizing sizing)
            {
                Spec = spec;
                Workers = workers;
                Sizing = sizing;
            }

            public ProfileSection Spec { get; }
            public int Workers { get; }
            public SectionSizing Sizing { get; }
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

            // --overhead's in-process legs compare the sink off with the sink on; counting in both would
            // make that comparison something else, and the report would not say so.
            if (opt.Overhead && PhaseClock.CountersOn)
            {
                throw new CliException(PhaseClock.EnvironmentVariable + "=1 is set in the environment, so counters are on in this "
                                       + "process: --overhead's sink off/on legs would both count, and its figure would not be "
                                       + "the profiler's cost. Unset the variable; --overhead times the counters itself, in child "
                                       + "processes.", ExitCodes.Usage);
            }

            PilotSeedOrder.Verify();
            string? outPath = opt.Out == null ? null : ResolveOut(opt.Out);

            // What to measure: the battery or the options, or an earlier profile's exact sections.
            string? planSha = null;
            ProfilePlan? plan = null;
            List<ProfileConfig> configs = opt.PlanPath != null ? ConfigsFromPlan(opt, out planSha, out plan) : FixedConfigs(opt);

            // Counting is fixed when vseed starts: a replay cannot switch it, and timings with and
            // without it are not comparable, so the difference is refused before anything is spent.
            string? countersConflict = plan?.CountersConflict(PhaseClock.CountersOn);
            if (countersConflict != null)
            {
                throw new CliException("--plan: " + countersConflict + ". Nothing was measured.");
            }

            DumpedLocationOracle? oracle = null;
            bool needsOracle = configs.Exists(c => c.Spec.Tier == "t5");
            if (needsOracle)
            {
                // Fails closed like every location answer: a table from another build places other worlds.
                oracle = DumpedLocationOracle.Open();
            }

            try
            {
                // The profile's own per-seed table is part of what must fit - once the counts are known.
                CheckMemory(rt, configs, withTable: !opt.Saturate.HasValue && !opt.Overhead);
                Dictionary<string, object?> machine = ProfileReport.Machine(rt);
                Dictionary<string, object?> build = ProfileReport.Build(oracle);
                List<PlanDifference> differences = plan != null ? PlanDifferences(plan, configs, machine, build, oracle) : new List<PlanDifference>();
                if (!o.Json)
                {
                    rt.PrintStartup("Profile");
                    Console.Error.WriteLine("  profiling " + configs.Count + " measurement(s) at " + string.Join(", ", opt.WorkerCounts)
                                            + " worker(s); counters " + (PhaseClock.CountersOn ? "ON" : "off")
                                            + (opt.PlanPath != null ? "; replaying " + opt.PlanPath : ""));
                    if (rt.Mode == ResourceMode.Background)
                    {
                        Console.Error.WriteLine("  the profile keeps its own worker counts (--threads); the background mode only lowers its priority to "
                                                + "BelowNormal, so other programs come first and these timings are not measurements");
                    }

                    PrintTimeOutlook(configs, opt, plan);
                    PrintDifferences(differences);
                }

                ProfileRun run = new ProfileRun
                {
                    CreatedUtc = DateTime.UtcNow,
                    Key = opt.Key,
                    From = opt.From,
                    Warmup = opt.Warmup,
                    WorkerCounts = opt.WorkerCounts,
                    CountersOn = PhaseClock.CountersOn,
                    TierText = plan != null ? plan.TierText ?? "plan" : opt.TierText,
                    Machine = machine,
                    Build = build,
                    SaturateSeconds = opt.Saturate,
                    PlanFile = opt.PlanPath,
                    PlanSha256 = planSha,
                    PlanDifferences = differences,
                };

                // ---- hygiene: a baseline, then a probe for the whole run -----------------------------
                if (opt.QuietSeconds > 0)
                {
                    if (!o.Json) Console.Error.WriteLine("  watching the machine for " + opt.QuietSeconds + " s before measuring...");
                    run.Baseline = QuietMachineProbe.TakeBaseline(TimeSpan.FromSeconds(opt.QuietSeconds), machine: MachineCpu.Reader);
                }

                using QuietMachineProbe probe = QuietMachineProbe.Start(baseline: run.Baseline, machine: MachineCpu.Reader);
                run.Probe = probe;

                if (opt.Overhead)
                {
                    run.Overhead = MeasureOverhead(opt, rt, probe, o.Json);
                }
                else
                {
                    if (opt.Saturate.HasValue)
                    {
                        Saturate(configs, opt, oracle, o.Json, run);
                        CheckMemory(rt, configs, withTable: true);
                    }
                    else if (plan != null && plan.Pilots.Count > 0)
                    {
                        ReplayPilots(plan, configs, opt, oracle, o.Json, run);
                    }

                    for (int i = 0; i < configs.Count; i++)
                    {
                        ProfileConfig c = configs[i];
                        double? about = c.Sizing.EstimatedSeconds ?? c.Sizing.RecordedSeconds;
                        if (!o.Json)
                        {
                            Console.Error.WriteLine("  [" + (i + 1) + "/" + configs.Count + "] " + c.Spec.Label + " x" + c.Spec.Seeds + " at " + c.Workers + " worker(s)"
                                                    + (about.HasValue && double.IsFinite(about.Value) ? " (about " + Minutes(about.Value) + ")" : "") + "...");
                        }

                        SectionResult res = RunSection(c.Spec, c.Workers, opt.Warmup, opt.Key, opt.From, oracle,
                                                       attachSink: true, recordRows: true, resources: true);
                        res.Sizing = c.Sizing;
                        run.Sections.Add(res);
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

            if (a.Get("key") is string k)
            {
                opt.Key = ParseKey(k);
                opt.KeyGiven = true;
            }

            if (a.Get("from") is string f)
            {
                if (!long.TryParse(f, NumberStyles.Integer, CultureInfo.InvariantCulture, out long from) || from < 0 || from > uint.MaxValue)
                {
                    throw new CliException("--from takes an index from 0 to 4294967295, not '" + f + "'.");
                }

                opt.From = from;
                opt.FromGiven = true;
            }

            string? threadsText = a.Get("profile-threads");
            if (threadsText != null) opt.WorkerCounts = ParseList(threadsText, "threads", ParseWorkers);
            foreach (int w in opt.WorkerCounts)
            {
                if (w > Args.MaxThreads) throw new CliException("--threads is capped at " + Args.MaxThreads + " here, not " + w + ".");
            }

            opt.WarmupGiven = a.Get("warmup") != null;
            opt.Warmup = a.Int("warmup", 3);
            if (opt.Warmup < 0 || opt.Warmup > 1000) throw new CliException("--warmup must be between 0 and 1000.");

            if (a.Get("saturate") is string sat)
            {
                if (!double.TryParse(sat, NumberStyles.Float, CultureInfo.InvariantCulture, out double secs) || !(secs >= 1) || secs > 3600)
                {
                    throw new CliException("--saturate takes a number of seconds from 1 to 3600, not '" + sat + "'.");
                }

                if (seeds.HasValue) throw new CliException("--saturate chooses each measurement's seed count itself; leave --seeds out.");
                opt.Saturate = secs;
            }

            opt.PlanPath = a.Get("plan");
            if (opt.PlanPath != null)
            {
                List<string> clash = new List<string>();
                if (a.Get("tier") != null) clash.Add("--tier");
                if (grids != null) clash.Add("--grid");
                if (prefixes != null) clash.Add("--prefix");
                if (seeds.HasValue) clash.Add("--seeds");
                if (threadsText != null) clash.Add("--threads");
                if (opt.Saturate.HasValue) clash.Add("--saturate");
                if (clash.Count > 0)
                {
                    throw new CliException("--plan replays an earlier profile's sections, worker counts and seeds exactly; leave out "
                                           + string.Join(", ", clash) + ".");
                }

                if (!File.Exists(opt.PlanPath)) throw new CliException("--plan: no file at " + opt.PlanPath, ExitCodes.NotFound);
                opt.PlanPath = Path.GetFullPath(opt.PlanPath);
            }

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

            if (opt.Overhead && (opt.Saturate.HasValue || opt.PlanPath != null))
            {
                throw new CliException("--overhead sizes its own legs; leave out " + (opt.Saturate.HasValue ? "--saturate" : "--plan") + ".");
            }

            // The plan decides every section; nothing below applies to it.
            if (opt.PlanPath != null)
            {
                opt.TierText = "plan";
                return opt;
            }

            if (opt.Overhead)
            {
                if (opt.CountersRequested)
                {
                    throw new CliException("--overhead times the counters itself (off and on, in child processes); "
                                           + "leave --counters out.");
                }

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

        /// <summary>A worker count, or "max" for every logical core this machine has.</summary>
        private static int ParseWorkers(string text, string name)
        {
            if (string.Equals(text.Trim(), "max", StringComparison.OrdinalIgnoreCase)) return Math.Max(1, Environment.ProcessorCount);
            return ParsePositive(text, name);
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

        /// <summary>
        /// Every requested worker count must fit the memory guard; the arithmetic is the plan's own.
        /// With <paramref name="withTable"/> (once the seed counts are known), the profile's own per-seed
        /// table is taken out of the budget first: the section's table while it measures, plus every
        /// earlier section's table as it is kept afterwards (<see cref="SeedTable"/>).
        /// </summary>
        private static void CheckMemory(CliRuntime rt, List<ProfileConfig> configs, bool withTable)
        {
            long kept = 0;
            foreach (ProfileConfig c in configs)
            {
                ProfileSection s = c.Spec;
                WorkTier tier = s.Tier switch
                {
                    "t2" => WorkTier.BiomeGrid,
                    "t5" => s.Prefix > 22 ? WorkTier.LocationsAll : WorkTier.LocationsCore,
                    _ => WorkTier.HeightsRivers,
                };
                long reserved = withTable ? kept + SeedTable.BytesWhileMeasuring(s.Seeds, phases: true, PhaseClock.CountersOn) : 0;
                WorkerPlan plan = rt.Plan(tier, s.Tier == "t5" || s.Grid == 0 ? 12.0 : s.Grid, reserved);
                if (c.Workers > plan.MemoryWorkers)
                {
                    string table = withTable
                        ? " once the profile's own table of " + c.Spec.Seeds.ToString("N0", CultureInfo.InvariantCulture) + " seeds (about "
                          + Out.F(reserved / 1048576.0, 0) + " MiB with the earlier sections') is counted"
                        : "";
                    throw new CliException(s.Label + " at " + c.Workers + " workers does not fit the memory guard" + table + ": it allows "
                                           + plan.MemoryWorkers + ". Nothing was measured"
                                           + (withTable ? " - use a shorter --saturate, fewer worker counts or fewer sections." : "."),
                                           ExitCodes.Usage, string.Join(Environment.NewLine, plan.Lines()));
                }

                if (withTable) kept += SeedTable.BytesKeptEstimate(s.Seeds, PhaseClock.CountersOn);
            }
        }

        /// <summary>A duration in plain units: seconds under 90 s, minutes above.</summary>
        internal static string Minutes(double seconds) =>
            !double.IsFinite(seconds) ? "? s" : seconds < 90 ? Out.F(seconds, seconds < 10 ? 1 : 0) + " s" : Out.F(seconds / 60.0, 1) + " min";

        /// <summary>
        /// How long the run will take, as far as it is known before the watch of the machine: a
        /// saturating run's lower bound (the measurements alone; the pilots and warm-ups come on top),
        /// or what a replayed plan's measurements and pilots took when it was recorded.
        /// </summary>
        private static void PrintTimeOutlook(List<ProfileConfig> configs, Options opt, ProfilePlan? plan)
        {
            if (opt.Overhead) return;
            if (opt.Saturate.HasValue)
            {
                double lower = configs.Count * opt.Saturate.Value;
                Console.Error.WriteLine("  " + configs.Count + " measurement(s) of about " + Out.F(opt.Saturate.Value, 0) + " s each: at least "
                                        + Minutes(lower) + " of measuring, plus a pilot and a warm-up per measurement (the 32-seeds-per-worker "
                                        + "minimum can make a slow tier longer); the full plan follows after the pilots");
            }
            else if (plan != null)
            {
                double? rec = plan.RecordedSeconds;
                Console.Error.WriteLine("  " + configs.Count + " measurement(s) and " + plan.Pilots.Count + " pilot(s) replayed"
                                        + (rec.HasValue ? "; when recorded they took about " + Minutes(rec.Value) + " (warm-ups and the watch not included)" : ""));
            }
        }

        private static void PrintDifferences(List<PlanDifference> differences)
        {
            if (differences.Count == 0) return;
            Console.Error.WriteLine("  what differs from the plan's run (recorded in run.plan.differences):");
            foreach (PlanDifference d in differences)
            {
                Console.Error.WriteLine("    " + d.What + ": " + d.Plan + " then, " + d.Now + " now - " + d.Note);
            }
        }

        // ---------------------------------------------------------------------------------------------
        // What to measure: fixed counts, a replayed plan, or counts sized to saturate
        // ---------------------------------------------------------------------------------------------

        /// <summary>Every section at every worker count, worker counts outermost, with the section's own count.</summary>
        private static List<ProfileConfig> FixedConfigs(Options opt)
        {
            List<ProfileConfig> r = new List<ProfileConfig>();
            foreach (int workers in opt.WorkerCounts)
            {
                foreach (ProfileSection spec in opt.Sections)
                {
                    r.Add(new ProfileConfig(spec, workers, new SectionSizing { Source = "fixed" }));
                }
            }

            return r;
        }

        /// <summary>
        /// An earlier profile's measurements, in its order, with its seed order and warm-up. Each
        /// section's seed list is recomputed and must hash to what the plan recorded: if it does not,
        /// the seed order itself has changed and the replay would measure other worlds.
        /// </summary>
        private static List<ProfileConfig> ConfigsFromPlan(Options opt, out string planSha, out ProfilePlan plan)
        {
            string path = opt.PlanPath!;
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                throw new CliException("--plan: cannot read " + path + " (" + ex.Message + ").", ExitCodes.NotFound);
            }

            planSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
            try
            {
                plan = ProfilePlan.Parse(text);
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException)
            {
                throw new CliException("--plan: " + path + " is not a plan: " + ex.Message + ". Nothing was measured.");
            }

            string? conflict = plan.Conflict(opt.KeyGiven ? opt.Key : null, opt.FromGiven ? opt.From : null, opt.WarmupGiven ? opt.Warmup : null);
            if (conflict != null)
            {
                throw new CliException("--plan: " + conflict + ". A replay measures the plan's seeds exactly; leave the option out. Nothing was measured.");
            }

            opt.Key = plan.Key;
            opt.From = plan.From;
            opt.Warmup = plan.Warmup;
            ScanPlanReader order = new ScanPlanReader(plan.Key);
            List<ProfileConfig> configs = new List<ProfileConfig>();
            List<int> workerCounts = new List<int>();
            int i = 0;
            foreach (PlannedSection p in plan.Sections)
            {
                if (p.Seeds > 1_000_000) throw new CliException("--plan: section " + i + " asks for " + p.Seeds + " seeds; the cap is 1,000,000.");
                if (p.Workers > Args.MaxThreads) throw new CliException("--plan: section " + i + " asks for " + p.Workers + " workers; the cap here is " + Args.MaxThreads + ".");
                if (p.Tier is "t2" or "t3" && PhaseMap.T2ForGrid(p.Grid) == Phase.None)
                    throw new CliException("--plan: section " + i + " uses grid " + p.Grid + ", which has no phase of its own here.");
                if (p.Tier == "t5" && p.PrefixRequested > 183) throw new CliException("--plan: section " + i + " asks for prefix " + p.PrefixRequested + " of 183.");
                ProfileSection spec = new ProfileSection(p.Tier, p.Grid, p.PrefixRequested, p.Seeds);
                string now = SeedListDigest.Of(SeedsOf(order, plan.From, p.Seeds));
                if (!string.Equals(now, p.SeedListSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new CliException("--plan: section " + i + " (" + spec.Label + ") would measure other seeds than the plan recorded "
                                           + "(seed list SHA-256 " + now.Substring(0, 16) + "... against " + p.SeedListSha256.Substring(0, 16)
                                           + "...): the plan's seeds are not what this build's seed order gives at key 0x"
                                           + plan.Key.ToString("X16", CultureInfo.InvariantCulture) + " from index " + plan.From
                                           + " (the document was edited, or the seed order changed). Nothing was measured.", ExitCodes.CheckFailed);
                }

                configs.Add(new ProfileConfig(spec, p.Workers, new SectionSizing { Source = "plan", PlanFile = path, RecordedSeconds = p.WallSeconds }));
                if (!workerCounts.Contains(p.Workers)) workerCounts.Add(p.Workers);
                i++;
            }

            opt.WorkerCounts = workerCounts;
            return configs;
        }

        private static int[] SeedsOf(ScanPlanReader order, long from, int n)
        {
            int[] seeds = new int[n];
            for (int i = 0; i < n; i++) seeds[i] = order.At(ProfileIndices.Measured(from, i));
            return seeds;
        }

        /// <summary>
        /// Replaces every measurement's seed count with one that keeps all its workers busy for about
        /// <c>--saturate</c> seconds. A pilot per measurement (warm-up included, never reported as a
        /// section) runs until it has worked for <see cref="SaturationPlanner.MinPilotSeconds"/>, growing
        /// run by run; the later half of its last run's seeds gives the pace, and
        /// <see cref="SaturationPlanner"/> does the rest. Every pilot run is recorded, so a replay can run
        /// the same pilots again. The plan and its estimated time are printed before the first measured seed.
        /// </summary>
        private static void Saturate(List<ProfileConfig> configs, Options opt, DumpedLocationOracle? oracle, bool quiet, ProfileRun run)
        {
            double seconds = opt.Saturate!.Value;
            double minPilot = SaturationPlanner.MinPilotSeconds(seconds);
            if (!quiet)
            {
                Console.Error.WriteLine("  sizing every measurement to about " + Out.F(seconds, 0) + " s of work, at least "
                                        + SaturationPlanner.MinPerWorker + " seeds per worker: a pilot each (not counted) that runs at least "
                                        + Out.F(minPilot, 1) + " s...");
            }

            double total = 0;
            for (int i = 0; i < configs.Count; i++)
            {
                ProfileConfig c = configs[i];
                int pn = SaturationPlanner.PilotSeeds(c.Workers);
                List<int> runs = new List<int>();
                double pilotS = 0;
                SectionResult p;
                while (true)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    p = RunSection(c.Spec.WithSeeds(pn), c.Workers, opt.Warmup, opt.Key, opt.From, oracle,
                                   attachSink: true, recordRows: false, resources: false);
                    pilotS += (Stopwatch.GetTimestamp() - t0) / (double)Stopwatch.Frequency;
                    runs.Add(pn);
                    int next = SaturationPlanner.NextPilotSeeds(pn, p.WallSeconds, minPilot, c.Workers);
                    if (next == 0) break;
                    pn = next;
                }

                double meanMs = SaturationPlanner.SecondHalfMeanMs(p.Table.StartTicks, p.Table.WallTicks, p.Table.Count, Stopwatch.Frequency);
                double rate = SaturationPlanner.RateFromMeanSeedMs(c.Workers, meanMs);
                int n = SaturationPlanner.Seeds(rate, seconds, c.Workers, out bool capped);
                bool floor = !capped && !(rate > 0 && Math.Ceiling(rate * seconds) > (double)c.Workers * SaturationPlanner.MinPerWorker);
                double est = rate > 0 ? n / rate : double.NaN;
                SectionSizing sizing = new SectionSizing
                {
                    Source = "saturate",
                    TargetSeconds = seconds,
                    MinPerWorker = SaturationPlanner.MinPerWorker,
                    PilotSeeds = pn,
                    PilotRuns = runs,
                    MinPilotSeconds = minPilot,
                    PilotSeconds = pilotS,
                    PilotMeanSeedMs = meanMs,
                    RateEstimate = rate,
                    Capped = capped,
                    FloorDecided = floor,
                    EstimatedSeconds = est,
                };
                configs[i] = new ProfileConfig(c.Spec.WithSeeds(n), c.Workers, sizing);
                run.Pilots.Add(new SaturationPilot { SectionIndex = i, Label = c.Spec.Label, Workers = c.Workers, Sizing = sizing, Seeds = n });
                if (double.IsFinite(est)) total += est;
                if (!quiet)
                {
                    Console.Error.WriteLine("    " + c.Spec.Label + " at " + c.Workers + " worker(s): pilot " + string.Join(" -> ", runs) + " seeds in "
                                            + Out.F(pilotS, 1) + " s, " + Out.F(meanMs, 2) + " ms per seed (its later half) -> about "
                                            + Out.F(rate, 1) + " seeds/s -> " + n + " seeds (" + (n / c.Workers) + " per worker), about "
                                            + Minutes(est)
                                            + (capped ? " (capped at " + SaturationPlanner.MaxSeeds.ToString("N0", CultureInfo.InvariantCulture) + " seeds)"
                                               : floor ? " (the " + SaturationPlanner.MinPerWorker + "-per-worker minimum decided, not the " + Out.F(seconds, 0) + " s)" : ""));
                }
            }

            run.EstimatedSeconds = total;
            if (!quiet)
            {
                Console.Error.WriteLine("  estimated measuring time: about " + Minutes(total) + ", plus each measurement's warm-up");
            }
        }

        /// <summary>
        /// A replay runs the plan's pilots again, in their order, uncounted and never reported as a
        /// section - so its measurements start from the same history of work (the heap the pilots grew,
        /// the code the runtime optimised during them) as the plan's did.
        /// </summary>
        private static void ReplayPilots(ProfilePlan plan, List<ProfileConfig> configs, Options opt, DumpedLocationOracle? oracle, bool quiet, ProfileRun run)
        {
            if (!quiet)
            {
                Console.Error.WriteLine("  running the plan's " + plan.Pilots.Count + " pilot(s) again first, not counted, so the measurements start "
                                        + "from the same point as the plan's...");
            }

            foreach (PlannedPilot pp in plan.Pilots)
            {
                ProfileConfig c = configs[pp.SectionIndex];
                long t0 = Stopwatch.GetTimestamp();
                foreach (int k in pp.Runs)
                {
                    RunSection(c.Spec.WithSeeds(k), c.Workers, opt.Warmup, opt.Key, opt.From, oracle,
                               attachSink: true, recordRows: false, resources: false);
                }

                run.ReplayedPilots.Add(new ReplayedPilot
                {
                    SectionIndex = pp.SectionIndex,
                    Label = c.Spec.Label,
                    Workers = c.Workers,
                    Runs = new List<int>(pp.Runs),
                    Seconds = (Stopwatch.GetTimestamp() - t0) / (double)Stopwatch.Frequency,
                });
            }
        }

        /// <summary>
        /// What the plan's run was measured with that differs from this one. A different game-data
        /// snapshot under a t5 section is refused (it would place other locations); everything else - the
        /// build above all, which is the point of a before/after run - is returned to be printed and recorded.
        /// </summary>
        private static List<PlanDifference> PlanDifferences(ProfilePlan plan, List<ProfileConfig> configs, Dictionary<string, object?> machine,
                                                            Dictionary<string, object?> build, DumpedLocationOracle? oracle)
        {
            List<PlanDifference> d = new List<PlanDifference>();
            string? data = oracle?.Provenance;
            if (configs.Exists(c => c.Spec.Tier == "t5") && plan.DataProvenance != null && data != null && plan.DataProvenance != data)
            {
                throw new CliException("--plan: the plan's t5 sections placed locations from the game data " + plan.DataProvenance
                                       + "; this build reads " + data + ", so a replay would place other locations and time other work. "
                                       + "Nothing was measured.");
            }

            void Cmp(string what, string? then, string? now, string note)
            {
                if (then == null || now == null || string.Equals(then, now, StringComparison.Ordinal)) return;
                d.Add(new PlanDifference { What = what, Plan = Short(then), Now = Short(now), Note = note });
            }

            const string build_ = "a different build - expected in a before/after comparison";
            Cmp("vseed build", plan.VseedSha256, build.GetValueOrDefault("vseed_sha256") as string, build_);
            Cmp("generator build", plan.WorldGenSha256, build.GetValueOrDefault("worldgen_sha256") as string, build_);
            Cmp("location code build", plan.LocationsSha256, build.GetValueOrDefault("locations_sha256") as string, build_);
            Cmp("garbage collector", plan.GcMode, machine.GetValueOrDefault("gc") as string,
                "server and workstation collectors spend their time differently; the timings are not like for like");
            string? datas = machine.GetValueOrDefault("gc_config") is Dictionary<string, object?> gc && gc.TryGetValue("GCDynamicAdaptationMode", out object? v)
                ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
            Cmp("collector heap adaptation (GCDynamicAdaptationMode)", plan.GcDynamicAdaptation, datas,
                "the collector sizes its heaps differently; GC pauses are not like for like");
            Cmp("logical cores", plan.LogicalCores?.ToString(CultureInfo.InvariantCulture),
                Convert.ToString(machine.GetValueOrDefault("logical_cores"), CultureInfo.InvariantCulture),
                "another machine or affinity: the plan's worker counts are replayed as they were");
            string? brand = machine.GetValueOrDefault("cpu") is Dictionary<string, object?> cpu ? cpu.GetValueOrDefault("brand") as string : null;
            Cmp("processor", plan.CpuBrand, brand, "another processor: the timings are not like for like");
            return d;
        }

        private static string Short(string s) => s.Length == 64 && IsHex(s) ? s.Substring(0, 16) + "..." : s;

        private static bool IsHex(string s)
        {
            foreach (char ch in s)
            {
                if (!Uri.IsHexDigit(ch)) return false;
            }

            return true;
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
            "t2" => "construct (deferred); SampleBiomes + MeasureBiomes' counting pass; patch = its largest-patch flood fill for all ten biomes (the two together are one MeasureBiomes with every patch)",
            "t3" => "construct (deferred); SampleBiomes + MeasureBiomes; ForcePregeneration; MeasureStructures; SampleHeights + MeasureHeights (land, peak, land within 10.5 km, above 100 m, islands >= 1 km2, shore band)",
            "t4" => "construct (deferred); ForcePregeneration; MeasureStructures",
            "t5" => "DumpedLocationOracle.Run with no gate: its own eager generator, BiomeGrid, BiomeField, alt biomes, placement of the prefix, harvest",
            _ => "",
        };

        /// <summary>
        /// Measures one section at one worker count. With <paramref name="resources"/>, it also reads
        /// what the section used: each worker's own thread CPU at the start and the end of its share
        /// (two reads per worker, never per seed), the process's CPU split, input and output and
        /// allocation at the window's edges, the memory on a sampler thread every 200 ms and the
        /// operating system's peak counters at both edges, and the last garbage collection's details at
        /// the end. The per-seed figures go into a <see cref="SeedTable"/> allocated before the window
        /// opens, so the profiler allocates nothing of its own per seed. The <c>--overhead</c> legs pass
        /// false and take none of the resource readings; both of their legs run this same loop, so their
        /// comparison is like for like.
        /// </summary>
        internal static SectionResult RunSection(ProfileSection spec, int workers, int warmup, ulong key, long from,
                                                 DumpedLocationOracle? oracle, bool attachSink, bool recordRows, bool resources = false)
        {
            SectionResult res = new SectionResult(spec, workers, warmup);
            ScanPlanReader order = new ScanPlanReader(key);
            int n = spec.Seeds;
            int[] seeds = SeedsOf(order, from, n);
            res.Seeds = seeds;
            res.From = from;

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
                    // One biome pass with every biome's largest patch, as a query asking for all ten
                    // would run it: the counting pass under t2, then the flood fills alone under patch.
                    // MeasureBiomes(plan with patches) is exactly these two calls, so the seed's total is
                    // a real T2 seed's and patch is only the flood fill.
                    WorldGeneratorPort gen = new WorldGeneratorPort(seed, Verified.WorldGenVersion, menu: false,
                                                                    deferPregeneration: true);
                    st.Sink?.Begin(t2Phase);
                    st.Sampler!.SampleBiomes(gen, SeedSampler.WaterEdge);
                    st.Measure!.Reset(s_patchPlan);
                    st.Measure.MeasureBiomes(st.Sampler, s_biomePlan);
                    st.Sink?.End(t2Phase);
                    st.Sink?.Begin(Phase.Patch);
                    st.Measure.MeasurePatches(st.Sampler, s_patchPlan);
                    st.Sink?.End(Phase.Patch);
                    st.Checksum += st.Measure.InWorldCells + st.Measure.LargestPatchCells[1];
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

            // Every measured seed's figures, column by column, allocated here - before the window opens.
            bool phases = recordRows && attachSink;
            SeedTable table = new SeedTable(n, phases, phases && PhaseClock.CountersOn);
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
                    long[] shareSink0 = new long[PhaseSink.SnapshotLength];
                    long[] shareSink1 = new long[PhaseSink.SnapshotLength];
                    WorkerStat stat = new WorkerStat { Id = wid };
                    bool record = phases && st.Sink != null;
                    try
                    {
                        try
                        {
                            for (int k = 0; k < warmup; k++) work(order.At(ProfileIndices.Warmup(from, n, warmup, wid, k)), st);
                        }
                        finally
                        {
                            warm.SignalAndWait();
                            warm.SignalAndWait();
                        }

                        // This worker's share starts: its own thread's CPU, read on the thread itself, and
                        // its sink's totals (the share's entries and allocation per phase are their difference).
                        TimeSpan? cpu0 = resources ? ProcessResources.ThreadCpu() : null;
                        if (record) st.Sink!.Snapshot(shareSink0);
                        long share0 = Stopwatch.GetTimestamp();
                        long alloc0 = GC.GetAllocatedBytesForCurrentThread();

                        int i;
                        while ((i = Interlocked.Increment(ref next)) < n)
                        {
                            if (record)
                            {
                                st.Sink!.Snapshot(before);
                                if (PhaseClock.CountersOn) PhaseClock.SnapshotCounters(cBefore);
                            }

                            long a0 = GC.GetAllocatedBytesForCurrentThread();
                            long t0 = Stopwatch.GetTimestamp();
                            work(seeds[i], st);
                            long t1 = Stopwatch.GetTimestamp();
                            long a1 = GC.GetAllocatedBytesForCurrentThread();
                            if (stat.Seeds == 0) stat.FirstStartTicks = t0;
                            stat.LastEndTicks = t1;
                            stat.Seeds++;
                            stat.BusyTicks += t1 - t0;

                            table.Seed[i] = seeds[i];
                            table.Worker[i] = wid;
                            table.StartTicks[i] = t0;
                            table.WallTicks[i] = t1 - t0;
                            table.AllocBytes[i] = a1 - a0;
                            if (record)
                            {
                                st.Sink!.Snapshot(after);
                                table.SetPhaseTicks(i, before, after);
                                if (PhaseClock.CountersOn)
                                {
                                    PhaseClock.SnapshotCounters(cAfter);
                                    table.SetCounters(i, cBefore, cAfter);
                                }
                            }
                        }

                        // ... and ends here, inside the try: a worker that faulted has no share to report.
                        long alloc1 = GC.GetAllocatedBytesForCurrentThread();
                        long share1 = Stopwatch.GetTimestamp();
                        if (record) st.Sink!.Snapshot(shareSink1);
                        TimeSpan? cpu1 = resources ? ProcessResources.ThreadCpu() : null;
                        stat.ShareStartTicks = share0;
                        stat.ShareEndTicks = share1;
                        stat.ThreadCpuSeconds = cpu0.HasValue && cpu1.HasValue ? (cpu1.Value - cpu0.Value).TotalSeconds : null;
                        stat.ThreadAllocBytes = alloc1 - alloc0;
                        if (record)
                        {
                            stat.SinkAtStart = shareSink0;
                            stat.SinkAtEnd = shareSink1;
                        }

                        stat.Finished = true;
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
            // The operating system's peak counters only ever rise: read here and at the end, a peak that
            // rose in between is this window's exact peak, which the 200 ms samples can miss.
            ProcessMemoryReading? memoryStart = resources ? ProcessResources.Memory() : null;
            start = WindowMark.Now(resources);
            res.StartUtc = DateTime.UtcNow;
            // The memory sampler runs on its own thread, off the workers, from before they are released
            // until after the last has finished; its preallocated room covers the expected samples.
            ResourceSampler? sampler = resources ? ResourceSampler.Start(ProcessResources.SamplerReaders()) : null;
            warm.SignalAndWait();          // release them into the measured window
            foreach (Thread t in threads) t.Join();
            ResourceSummary? memory = sampler?.Stop();
            sampler?.Dispose();
            WindowMark end = WindowMark.Now(resources);
            res.EndUtc = DateTime.UtcNow;
            if (fault != null) throw new InvalidOperationException("a profile worker failed: " + fault.Message, fault);

            // The window has closed: the per-phase sums, then the table cut down to what is kept.
            foreach (WorkerStat w in stats)
            {
                if (w.SinkAtStart != null && w.SinkAtEnd != null) table.AddShare(w.SinkAtStart, w.SinkAtEnd);
                w.SinkAtStart = null;
                w.SinkAtEnd = null;
            }

            table.Compact();
            res.Table = table;
            res.Workers = new List<WorkerStat>(stats);
            res.StartTicks = start.Timestamp;
            res.EndTicks = end.Timestamp;
            res.WallSeconds = (end.Timestamp - start.Timestamp) / (double)Stopwatch.Frequency;
            res.CpuSeconds = (end.Cpu - start.Cpu).TotalSeconds;
            res.Gen0 = end.Gen0 - start.Gen0;
            res.Gen1 = end.Gen1 - start.Gen1;
            res.Gen2 = end.Gen2 - start.Gen2;
            res.PauseMs = (end.Pause - start.Pause).TotalMilliseconds;
            res.JitMethods = end.JitMethods - start.JitMethods;
            res.JitMs = (end.JitTime - start.JitTime).TotalMilliseconds;
            if (resources)
            {
                res.Resources = true;
                res.CpuUserSeconds = end.User.HasValue && start.User.HasValue ? (end.User.Value - start.User.Value).TotalSeconds : null;
                res.CpuKernelSeconds = end.Kernel.HasValue && start.Kernel.HasValue ? (end.Kernel.Value - start.Kernel.Value).TotalSeconds : null;
                res.Io = start.Io != null && end.Io != null ? ProcessIoReading.Delta(start.Io, end.Io) : null;
                res.AllocatedBytes = end.Allocated - start.Allocated;
                res.Memory = memory;
                res.MemoryStart = memoryStart;
                res.MemoryEnd = ProcessResources.Memory();
                res.GcCountAtStart = start.Gen0;
                res.LastGc = GC.GetGCMemoryInfo(GCKind.Any);
                res.LastFullGc = GC.GetGCMemoryInfo(GCKind.FullBlocking);
                res.GcHeapCountMax = GcHeapCountMax();
            }

            return res;
        }

        /// <summary>
        /// The GC's configured MAXIMUM heap count (<c>GC.GetConfigurationVariables()["HeapCount"]</c>), or
        /// null. It is not the number of heaps in use: with dynamic adaptation (DATAS) the collector runs
        /// anywhere from one heap up to this, and that live count is not read here (an independent probe on
        /// 2026-09-26 saw the variable stay at 16 while the GC's own events reported 1 to 15 heaps).
        /// </summary>
        private static long? GcHeapCountMax()
        {
            try
            {
                IReadOnlyDictionary<string, object> v = GC.GetConfigurationVariables();
                if (v.TryGetValue("HeapCount", out object? hc)) return Convert.ToInt64(hc, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                // Not reported by this runtime.
            }

            return null;
        }

        /// <summary>The seed order, read without allocating a plan per seed.</summary>
        private sealed class ScanPlanReader
        {
            private readonly SeedLab.Search.Execution.ScanPlan _plan;
            public ScanPlanReader(ulong key) { _plan = PilotSeedOrder.Plan(key); }
            public int At(long index) => _plan.SeedAt(index % ProfileIndices.OrderLength);
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
            public TimeSpan? User { get; init; }
            public TimeSpan? Kernel { get; init; }
            public ProcessIoReading? Io { get; init; }
            public long Allocated { get; init; }

            public static WindowMark Now(bool resources)
            {
                using Process self = Process.GetCurrentProcess();
                TimeSpan? user = null, kernel = null;
                if (resources)
                {
                    try
                    {
                        Environment.ProcessCpuUsage u = Environment.CpuUsage;
                        user = u.UserTime;
                        kernel = u.PrivilegedTime;
                    }
                    catch (Exception)
                    {
                        // Not available: the split is left out.
                    }
                }

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
                    User = user,
                    Kernel = kernel,
                    Io = resources ? ProcessResources.Io() : null,
                    Allocated = GC.GetTotalAllocatedBytes(false),
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
            for (int p = 0; p < PhaseMap.Capacity; p++) entries += probeRun.Table.EntriesSum[p];

            r.BoundariesPerSeed = entries / Math.Max(1, probeRun.Table.Count);
            r.SeedMsProfiled = probeRun.WallSeconds * 1000.0 / Math.Max(1, probeRun.Table.Count);
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
            // Its final CPU time lets the probe take the leg out of the whole-machine figure exactly.
            try { probe.ChildExited(p.Id, p.TotalProcessorTime); }
            catch (Exception) { /* unknown: the leg's time then reads as foreign - a false taint, never a false quiet */ }
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

        /// <summary>The same section over another number of seeds.</summary>
        public ProfileSection WithSeeds(int seeds) => new ProfileSection(Tier, Grid, Prefix, seeds);
    }

    /// <summary>How a measurement's seed count was chosen: fixed, replayed from a plan, or sized by a pilot to saturate.</summary>
    internal sealed class SectionSizing
    {
        public string Source = "fixed";
        public string? PlanFile;
        public double? TargetSeconds;
        public int? MinPerWorker;

        /// <summary>The last pilot run's seed count (the one the pace came from).</summary>
        public int? PilotSeeds;

        /// <summary>Every pilot run's seed count, in order.</summary>
        public List<int>? PilotRuns;
        public double? MinPilotSeconds;

        /// <summary>All the pilot's runs together, warm-ups included.</summary>
        public double? PilotSeconds;

        /// <summary>The mean seed time of the later half of the last pilot run's seeds.</summary>
        public double? PilotMeanSeedMs;
        public double? RateEstimate;
        public bool Capped;

        /// <summary>The 32-per-worker minimum decided the count, not the time asked for.</summary>
        public bool FloorDecided;
        public double? EstimatedSeconds;

        /// <summary>A replayed section's measured time when the plan was recorded.</summary>
        public double? RecordedSeconds;
    }

    /// <summary>One --saturate pilot, as the run's plan records it.</summary>
    internal sealed class SaturationPilot
    {
        public int SectionIndex;
        public string Label = "";
        public int Workers;
        public int Seeds;
        public SectionSizing Sizing = new SectionSizing();
    }

    /// <summary>A plan's pilot that a replay ran again before its measurements.</summary>
    internal sealed class ReplayedPilot
    {
        public int SectionIndex;
        public string Label = "";
        public int Workers;
        public List<int> Runs = new List<int>();
        public double Seconds;
    }

    /// <summary>Something a replay was measured with that differs from the plan's run.</summary>
    internal sealed class PlanDifference
    {
        public string What = "";
        public string Plan = "";
        public string Now = "";
        public string Note = "";
    }

    /// <summary>
    /// The measured seeds of one section, column by column, in arrays allocated before the window opens:
    /// no object per seed, nothing for the collector to trace, and no allocation of the profiler's own
    /// inside the window. While the section runs, each seed keeps its time in every phase (the phase
    /// table's <see cref="PhaseMap.Capacity"/> columns) and, with counters, every counter; once the
    /// section has ended, <see cref="Compact"/> keeps only the phases it entered and the counters that
    /// moved, so an earlier section's table costs the later ones little memory. Entries and allocation
    /// per phase are kept as the section's sums only - nothing reads them per seed.
    /// </summary>
    internal sealed class SeedTable
    {
        private long[]? _ticks;
        private int _tickWidth;
        private int[] _tickColumn;
        private long[]? _counters;
        private int _counterWidth;
        private int[] _counterColumn;

        public SeedTable(int n, bool phases, bool counters)
        {
            Count = n;
            Seed = new int[n];
            Worker = new int[n];
            StartTicks = new long[n];
            WallTicks = new long[n];
            AllocBytes = new long[n];
            _tickColumn = Identity(PhaseMap.Capacity, phases);
            _counterColumn = Identity(PhaseClock.Capacity, counters);
            if (phases)
            {
                _tickWidth = PhaseMap.Capacity;
                _ticks = new long[(long)n * _tickWidth];
            }

            if (counters)
            {
                _counterWidth = PhaseClock.Capacity;
                _counters = new long[(long)n * _counterWidth];
            }

            BytesWhileMeasured = Bytes();
        }

        public static readonly SeedTable Empty = new SeedTable(0, false, false);

        public int Count { get; }
        public readonly int[] Seed;
        public readonly int[] Worker;

        /// <summary>Each seed's start, a <see cref="Stopwatch"/> timestamp on the same clock as the section's edges.</summary>
        public readonly long[] StartTicks;
        public readonly long[] WallTicks;
        public readonly long[] AllocBytes;

        /// <summary>The section's phase entries and allocated bytes, per phase id, summed over its seeds.</summary>
        public readonly long[] EntriesSum = new long[PhaseMap.Capacity];
        public readonly long[] AllocSum = new long[PhaseMap.Capacity];

        public bool HasPhases => _ticks != null;
        public bool HasCounters => _counters != null;

        /// <summary>What the table held while the section measured.</summary>
        public long BytesWhileMeasured { get; }

        /// <summary>What it holds now (after <see cref="Compact"/>, what every later section lives with).</summary>
        public long BytesKept => Bytes();

        /// <summary>Seed <paramref name="i"/>'s phase times: the tick part of two <see cref="PhaseSink.Snapshot"/>s. Only before <see cref="Compact"/>.</summary>
        public void SetPhaseTicks(int i, long[] before, long[] after)
        {
            long o = (long)i * _tickWidth;
            for (int c = 0; c < PhaseMap.Capacity; c++) _ticks![o + c] = after[c] - before[c];
        }

        public void SetCounters(int i, long[] before, long[] after)
        {
            long o = (long)i * _counterWidth;
            for (int c = 0; c < PhaseClock.Capacity; c++) _counters![o + c] = after[c] - before[c];
        }

        /// <summary>Adds one worker's share: the difference of its sink's totals at the share's two ends.</summary>
        public void AddShare(long[] atStart, long[] atEnd)
        {
            for (int p = 0; p < PhaseMap.Capacity; p++)
            {
                EntriesSum[p] += atEnd[PhaseSink.EntriesOffset + p] - atStart[PhaseSink.EntriesOffset + p];
                AllocSum[p] += atEnd[PhaseSink.AllocOffset + p] - atStart[PhaseSink.AllocOffset + p];
            }
        }

        /// <summary>Seed <paramref name="i"/>'s ticks in <paramref name="p"/> (0 for a phase the section never entered).</summary>
        public long Tick(int i, Phase p)
        {
            int c = _tickColumn[(int)p];
            return c < 0 || _ticks == null ? 0 : _ticks[(long)i * _tickWidth + c];
        }

        public long Counter(int i, int counter)
        {
            int c = _counterColumn[counter];
            return c < 0 || _counters == null ? 0 : _counters[(long)i * _counterWidth + c];
        }

        /// <summary>Keeps only the phases the section entered and the counters that moved. After the window, never in it.</summary>
        public void Compact()
        {
            if (_ticks != null && _tickWidth == PhaseMap.Capacity)
            {
                bool[] keep = Moved(_ticks, PhaseMap.Capacity);
                for (int p = 0; p < PhaseMap.Capacity; p++) keep[p] |= EntriesSum[p] != 0;
                (_ticks, _tickWidth, _tickColumn) = Keep(_ticks, PhaseMap.Capacity, keep);
            }

            if (_counters != null && _counterWidth == PhaseClock.Capacity)
            {
                (_counters, _counterWidth, _counterColumn) = Keep(_counters, PhaseClock.Capacity, Moved(_counters, PhaseClock.Capacity));
            }
        }

        /// <summary>The columns in which any seed has a value other than 0.</summary>
        private bool[] Moved(long[] values, int width)
        {
            bool[] keep = new bool[width];
            for (int i = 0; i < Count; i++)
            {
                long o = (long)i * width;
                for (int c = 0; c < width; c++)
                {
                    if (values[o + c] != 0) keep[c] = true;
                }
            }

            return keep;
        }

        /// <summary>What a table of <paramref name="n"/> seeds holds while its section measures (the memory guard counts it).</summary>
        public static long BytesWhileMeasuring(int n, bool phases, bool counters) =>
            (long)n * (4 + 4 + 8 + 8 + 8 + (phases ? 8L * PhaseMap.Capacity : 0) + (counters ? 8L * PhaseClock.Capacity : 0)) + 1024;

        /// <summary>A generous estimate of what it keeps afterwards: 24 phases (a t3 section enters about 15) and, with counters, every counter.</summary>
        public static long BytesKeptEstimate(int n, bool counters) =>
            (long)n * (4 + 4 + 8 + 8 + 8 + 8L * 24 + (counters ? 8L * PhaseClock.Capacity : 0)) + 1024;

        private long Bytes()
        {
            const long header = 24;
            long b = 5 * header + 4L * Count * 2 + 8L * Count * 3 + 2 * (header + 8L * PhaseMap.Capacity);
            if (_ticks != null) b += header + 8L * _ticks.LongLength;
            if (_counters != null) b += header + 8L * _counters.LongLength;
            return b;
        }

        private (long[] Values, int Width, int[] Column) Keep(long[] values, int width, bool[] keep)
        {
            int[] column = new int[width];
            List<int> kept = new List<int>();
            for (int c = 0; c < width; c++)
            {
                column[c] = keep[c] ? kept.Count : -1;
                if (keep[c]) kept.Add(c);
            }

            long[] v = new long[(long)Count * kept.Count];
            for (int i = 0; i < Count; i++)
            {
                long src = (long)i * width, dst = (long)i * kept.Count;
                for (int k = 0; k < kept.Count; k++) v[dst + k] = values[src + kept[k]];
            }

            return (v, kept.Count, column);
        }

        private static int[] Identity(int width, bool on)
        {
            int[] c = new int[width];
            for (int i = 0; i < width; i++) c[i] = on ? i : -1;
            return c;
        }
    }

    internal sealed class WorkerStat
    {
        public int Id;
        public long Seeds;
        public long BusyTicks;

        /// <summary>The first measured seed's start and the last one's finish (timestamps; 0 when it measured none).</summary>
        public long FirstStartTicks, LastEndTicks;

        /// <summary>The worker's whole share: from its release into the window to its finding no more work.</summary>
        public long ShareStartTicks, ShareEndTicks;

        /// <summary>The worker thread's own CPU time over its share, read on the thread itself; null when not measured.</summary>
        public double? ThreadCpuSeconds;

        /// <summary>Everything the thread allocated over its share (the profiler's table is allocated before it, so not included).</summary>
        public long ThreadAllocBytes;

        /// <summary>The worker's sink totals at its share's two ends, until the section sums them.</summary>
        public long[]? SinkAtStart, SinkAtEnd;

        /// <summary>The share ended normally (a faulted worker's share is not reported).</summary>
        public bool Finished;
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

        /// <summary>The measured seeds' figures (<see cref="SeedTable"/>), compacted once the section ended.</summary>
        public SeedTable Table = SeedTable.Empty;
        public List<WorkerStat> Workers = new List<WorkerStat>();
        public double WallSeconds;
        public double CpuSeconds;
        public int Gen0, Gen1, Gen2;
        public double PauseMs;
        public long JitMethods;
        public double JitMs;
        public DateTime StartUtc, EndUtc;

        // ---- seedlab-profile/2 -------------------------------------------------------------------
        public long From;
        public SectionSizing? Sizing;

        /// <summary>The window's edges as <see cref="Stopwatch"/> timestamps, the clock the rows' starts use.</summary>
        public long StartTicks, EndTicks;

        /// <summary>Whether the resource figures below were read.</summary>
        public bool Resources;
        public double? CpuUserSeconds, CpuKernelSeconds;
        public ProcessIoReading? Io;
        public long AllocatedBytes;
        public ResourceSummary? Memory;
        public ProcessMemoryReading? MemoryStart;
        public ProcessMemoryReading? MemoryEnd;
        public int GcCountAtStart;
        public GCMemoryInfo LastGc;
        public GCMemoryInfo LastFullGc;

        /// <summary>The GC's configured maximum heap count - not the live count under DATAS, which is not measured.</summary>
        public long? GcHeapCountMax;
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

        // ---- seedlab-profile/2 -------------------------------------------------------------------
        public double? SaturateSeconds;
        public List<SaturationPilot> Pilots = new List<SaturationPilot>();
        public double? EstimatedSeconds;
        public string? PlanFile;
        public string? PlanSha256;
        public List<ReplayedPilot> ReplayedPilots = new List<ReplayedPilot>();
        public List<PlanDifference> PlanDifferences = new List<PlanDifference>();
    }
}
