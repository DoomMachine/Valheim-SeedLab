using System;
using System.Collections.Generic;
using SeedLab.Runtime.Execution;
using SeedLab.Runtime.Hardware;
using SeedLab.Search.Criteria;
using SeedLab.Search.Evaluation;
using SeedLab.Search.Execution;
using SeedLab.Search.Locations;
using SeedLab.Search.Output;

namespace SeedLab.SearchTests
{
    /// <summary>
    /// A block for every worker: the block-size rule, and the session that applies it.
    ///
    /// <para><b>What went wrong, and why these checks exist.</b> Until 2026-09-24 the block size was a
    /// plain 256 unless the user gave one, and one worker computes a whole block - so
    /// <c>vseed search &lt;query&gt; --seeds 512</c> on 8 workers was 2 blocks and 6 workers did
    /// nothing (measured: <c>axe-heads</c> at 64 seeds took 1.6 min as one block of 64 and 16.4 s as
    /// eight blocks of 8, with byte-identical results). The fix is a rule
    /// (<see cref="BlockSizing.Decide"/>) that shrinks the block when a run would give a worker fewer
    /// than four, warns instead when the user gave the size, and on a resume keeps the checkpoint's
    /// size - a resume point is a block number - and refuses a different one before anything runs.
    /// These checks pin the rule's arithmetic and every sentence's counts, and that the session
    /// decides for the FINAL worker count and hash (after a grid raise), adopts only from a checkpoint
    /// that is this run, and never writes the decided size back into the query.</para>
    ///
    /// <para>No dumped location table and no seed evaluation: every session here uses the
    /// unavailable oracle and a biome query, and nothing touches the disk.</para>
    /// </summary>
    public static class BlockChecks
    {
        private const string Engine = "tests";

        private const string Meadows =
            @"{""id"":""meadows"",""target"":""biome:Meadows"",""metric"":""area_within"",""radius"":2000,""test"":""at_least"",""value"":1000000,""importance"":""must""},
              {""id"":""swamp"",""target"":""biome:Swamp"",""metric"":""area_within"",""radius"":2000,""test"":""at_least"",""value"":100000,""importance"":""nice""}";

        public static void Run(Action<bool, string, string> check)
        {
            Rule(check);
            Sentences(check);
            Resume(check);
            BusiestWorker(check);
            SessionDecides(check);
            SessionFollowsTheRaise(check);
            SessionAdoptsOnlyItsOwnCheckpoint(check);
            CheckpointMatching(check);
            PreflightDerivesTheCounts(check);
            Gate(check);
        }

        // =========================================================================================
        // The rule: which size, from which source.

        private static void Rule(Action<bool, string, string> check)
        {
            BlockSizeDecision longRun = BlockSizing.Decide(null, 20_000, 8);
            check(longRun.Size == 256 && longRun.Source == BlockSizeSource.Default && longRun.Blocks == 79
                  && longRun.Note == null && longRun.Warning == null,
                  "a long run keeps 256 and says nothing new (79 blocks for 8 workers is already 4 each)",
                  Show(longRun));

            BlockSizeDecision space = BlockSizing.Decide(null, 4294967296L, 64);
            check(space.Size == 256 && space.Blocks == 16_777_216 && space.BusiestWorkerSeeds == 262_144 * 256L,
                  "the whole space on 64 workers keeps 256, in long arithmetic", Show(space));

            BlockSizeDecision shrink = BlockSizing.Decide(null, 512, 8);
            check(shrink.Size == 16 && shrink.Source == BlockSizeSource.Automatic && shrink.Blocks == 32
                  && shrink.BusyWorkers == 8 && shrink.BusiestWorkerSeeds == 64,
                  "512 seeds on 8 workers: 16 per block, 32 blocks, the busiest computing 64", Show(shrink));

            BlockSizeDecision one = BlockSizing.Decide(null, 512, 1);
            check(one.Size == 256 && one.Source == BlockSizeSource.Default && one.Note == null,
                  "one worker keeps the ceiling and says nothing: no size helps a single worker", Show(one));

            BlockSizeDecision none = BlockSizing.Decide(null, 0, 8);
            check(none.Size == 1 && none.Blocks == 0 && none.Note == null && none.Warning == null && none.Refusal == null,
                  "a limit of 0 (a funnel gate with no survivor) is size 1 and no sentence at all", Show(none));

            BlockSizeDecision few = BlockSizing.Decide(null, 3, 8);
            check(few.Size == 1 && few.Blocks == 3 && few.BusyWorkers == 3
                  && (few.Note ?? "").Contains("3 seeds cannot occupy more than 3 of the 8 workers", StringComparison.Ordinal),
                  "3 seeds on 8 workers: one seed per block, and the note blames the seed count, not the size",
                  few.Note ?? "(no note)");

            BlockSizeDecision given = BlockSizing.Decide(256, 512, 8);
            check(given.Size == 256 && given.Source == BlockSizeSource.Explicit && given.Blocks == 2
                  && given.BusyWorkers == 2 && given.IdleWorkers == 6 && given.AutomaticSize == 16,
                  "a size that was given is kept, even when it leaves 6 of 8 workers idle", Show(given));

            BlockSizeDecision capped = BlockSizing.Decide(null, 20_000, 8, ceiling: 64);
            check(capped.Size == 64 && capped.Source == BlockSizeSource.Default,
                  "the ceiling is a parameter (the rule never exceeds it)", Show(capped));

            bool threw = false;
            try
            {
                BlockSizing.Decide(0, 512, 8);
            }
            catch (ArgumentOutOfRangeException)
            {
                threw = true;
            }

            check(threw, "a requested size of 0 is an error, not a silent 1", "");

            // The floor: between 4W and 8W-1 blocks whenever the rule shrinks above 1, never more than
            // the ceiling, and the ceiling only when it already gives 4W.
            string bad = "";
            int tried = 0;
            foreach (int w in new[] { 2, 3, 5, 8, 16, 24, 64 })
            {
                for (long limit = 1; limit <= 70_000; limit = limit * 5 / 4 + 1)
                {
                    tried++;
                    BlockSizeDecision d = BlockSizing.Decide(null, limit, w);
                    bool ok = d.Size >= 1 && d.Size <= 256
                              && (d.Size == 256 ? d.Blocks >= 4L * w
                                  : d.Size == 1 ? d.Blocks == limit
                                  : d.Blocks >= 4L * w && d.Blocks <= 8L * w - 1);
                    if (!ok && bad.Length == 0) bad = "W=" + w + " limit=" + limit + ": " + Show(d);
                }
            }

            check(bad.Length == 0, "automatic sizes give 4W..8W-1 blocks, or 256 with at least 4W, or 1 seed per block",
                  bad.Length == 0 ? tried + " worker-count and limit pairs" : bad);
        }

        // =========================================================================================
        // The sentences: counts, never multipliers, and the variant that is true.

        private static void Sentences(Action<bool, string, string> check)
        {
            string s512 = BlockSizing.Decide(null, 512, 8).PlanLine ?? "";
            check(s512.StartsWith("block size   16, sized automatically", StringComparison.Ordinal)
                  && s512.Contains("the default 256 would cut these 512 seeds into 2 blocks", StringComparison.Ordinal)
                  && s512.Contains("6 of the 8 workers would have nothing to do", StringComparison.Ordinal)
                  && s512.Contains("32 blocks", StringComparison.Ordinal) && !s512.Contains("x as long", StringComparison.Ordinal),
                  "the automatic line states the counts at the default and at the size chosen", s512);

            string s2560 = BlockSizing.Decide(null, 2_560, 8).Note ?? "";
            check(s2560.Contains("10 blocks for 8 workers", StringComparison.Ordinal)
                  && s2560.Contains("the busiest would compute 512 seeds against an even share of 320", StringComparison.Ordinal)
                  && s2560.Contains("at 80 per block it is 32 blocks", StringComparison.Ordinal)
                  && !s2560.Contains("nothing to do", StringComparison.Ordinal),
                  "10 blocks for 8 workers gets the round-robin reason, not 'leaving 0 of 8 idle'", s2560);

            string s4096 = BlockSizing.Decide(null, 4_096, 8).Note ?? "";
            check(s4096.Contains("16 blocks for 8 workers, fewer than 4 each", StringComparison.Ordinal)
                  && s4096.Contains("not equal in time", StringComparison.Ordinal)
                  && s4096.Contains("at 128 per block it is 32 blocks", StringComparison.Ordinal),
                  "an even 2 blocks each gets the unequal-blocks reason", s4096);

            string s2000 = BlockSizing.Decide(null, 2_000, 8).Note ?? "";
            check(s2000.Contains("not equal in time", StringComparison.Ordinal)
                  && !s2000.Contains("round-robin", StringComparison.Ordinal)
                  && s2000.Contains("at 62 per block it is 33 blocks and the busiest worker computes 264 seeds", StringComparison.Ordinal),
                  "2,000 seeds on 8 (256 would be busiest 256, 62 is busiest 264): not the round-robin reason, which is false there",
                  s2000);

            BlockSizeDecision given = BlockSizing.Decide(256, 512, 8);
            string w = given.Warning ?? "";
            check(w.Contains("--block-size", StringComparison.Ordinal)
                  && w.Contains("search.block_size in the query", StringComparison.Ordinal)
                  && w.Contains("cuts these 512 seeds into 2 blocks", StringComparison.Ordinal)
                  && w.Contains("6 of the 8 workers have nothing to do", StringComparison.Ordinal)
                  && w.Contains("the busiest computes 256 seeds on its own", StringComparison.Ordinal)
                  && w.Contains("(16 per block, the busiest computing 64)", StringComparison.Ordinal)
                  && !w.Contains("as long", StringComparison.Ordinal),
                  "the idle warning names the knob (every place it can be set), the idle count and the automatic alternative",
                  w);

            BlockSizeDecision busy = BlockSizing.Decide(64, 20_000, 8);
            check(busy.Warning == null && (busy.Note ?? "").StartsWith("64, as given", StringComparison.Ordinal),
                  "a given size that keeps every worker busy is stated, not warned about", busy.Note ?? "(none)");

            BlockSizeDecision tiny = BlockSizing.Decide(2, 3, 8);
            check((tiny.Warning ?? "").Contains("cannot occupy more than 3 of the 8 workers whatever the size", StringComparison.Ordinal),
                  "a given size on fewer seeds than workers says no size would do better", tiny.Warning ?? "(none)");

            SearchPreflight pf = new SearchPreflight();
            pf.Warnings.Add(given.Warning!);
            check(pf.WarningsForOneSeed().Count == 0,
                  "explain leaves the idle warning out (it names --block-size, and explain scans nothing)", "");
        }

        // =========================================================================================
        // A resume point wins, counts only what is left, and refuses a different size.

        private static void Resume(Action<bool, string, string> check)
        {
            ResumePoint rp64 = new ResumePoint { BlockSize = 64, NextBlock = 2, Path = "C:\\x\\run.ckpt" };

            BlockSizeDecision adopted = BlockSizing.Decide(null, 512, 8, resume: rp64);
            check(adopted.Size == 64 && adopted.Source == BlockSizeSource.Adopted && adopted.Blocks == 8
                  && adopted.RemainingBlocks == 6 && adopted.BusyWorkers == 6 && adopted.IdleWorkers == 2
                  && adopted.Warning == null && adopted.Refusal == null
                  && (adopted.Note ?? "").Contains("from the checkpoint being resumed", StringComparison.Ordinal)
                  && (adopted.Note ?? "").Contains("block 2 of 8 is next, so this leg has 6 blocks (384 seeds) left", StringComparison.Ordinal)
                  && (adopted.Note ?? "").Contains("2 of the 8 workers have nothing to do", StringComparison.Ordinal)
                  && (adopted.Note ?? "").Contains("A fresh run would size it automatically at 16", StringComparison.Ordinal),
                  "left out on a resume: the checkpoint's 64 is adopted, and the idle count is for the 6 blocks left",
                  adopted.Note ?? "(no note)");

            BlockSizeDecision same = BlockSizing.Decide(64, 512, 8, resume: rp64);
            string sw = same.Warning ?? "";
            check(same.Size == 64 && same.Source == BlockSizeSource.Explicit && same.Refusal == null
                  && (same.Note ?? "").Contains("this resumed run keeps 64, the checkpoint's boundaries", StringComparison.Ordinal)
                  && sw.Contains("--block-size", StringComparison.Ordinal)
                  && sw.Contains("2 of the 8 workers have nothing to do", StringComparison.Ordinal)
                  && sw.Contains("can only continue at 64", StringComparison.Ordinal)
                  && !sw.Contains("per block, the busiest computing", StringComparison.Ordinal),
                  "given and equal to the checkpoint's: kept, and no other size is offered for this leg", sw);

            ResumePoint rp256 = new ResumePoint { BlockSize = 256, NextBlock = 1, Path = "C:\\x\\run.ckpt" };
            BlockSizeDecision differs = BlockSizing.Decide(16, 512, 8, resume: rp256);
            string r = differs.Refusal ?? "";
            check(differs.Size == 256 && differs.Note == null && differs.Warning == null
                  && r.Contains("this checkpoint's blocks are 256 seeds", StringComparison.Ordinal)
                  && r.Contains("block 1 of 2", StringComparison.Ordinal)
                  && r.Contains("a block size of 16 was asked for", StringComparison.Ordinal)
                  && r.Contains("--block-size 256 (or with the block size left out)", StringComparison.Ordinal)
                  && r.Contains("delete C:\\x\\run.ckpt to start again", StringComparison.Ordinal),
                  "given and different from the checkpoint's: a refusal with both sizes, the remedy and the file", r);

            BlockSizeDecision tail = BlockSizing.Decide(null, 512, 8, resume: rp256);
            check(tail.RemainingBlocks == 1 && tail.BusyWorkers == 1 && tail.IdleWorkers == 7 && tail.BusiestWorkerSeeds == 256,
                  "the last of 2 blocks left: 7 of 8 idle, not 6 - counted against the blocks remaining", Show(tail));

            BlockSizeDecision shortTail = BlockSizing.Decide(null, 520, 8,
                resume: new ResumePoint { BlockSize = 256, NextBlock = 2 });
            check(shortTail.Blocks == 3 && shortTail.RemainingSeeds == 8 && shortTail.BusiestWorkerSeeds == 8,
                  "a resumed short last block counts its own 8 seeds", Show(shortTail));

            BlockSizeDecision edited = BlockSizing.Decide(null, 512, 8,
                resume: new ResumePoint { BlockSize = 0, NextBlock = 5 });
            check(edited.Resume == null && edited.Source == BlockSizeSource.Automatic && edited.Size == 16,
                  "a hand-edited block_size of 0 is not adopted", Show(edited));
        }

        // =========================================================================================
        // The busiest worker: the closed form is the round-robin deal FunnelGate used to loop over.

        private static void BusiestWorker(Action<bool, string, string> check)
        {
            string bad = "";
            int cases = 0;
            for (long seeds = 0; seeds <= 300; seeds += 7)
            {
                for (int bs = 1; bs <= 40; bs += 3)
                {
                    for (int w = 1; w <= 12; w++)
                    {
                        cases++;
                        long want = Dealt(seeds, bs, w);
                        long got = BlockSizing.BusiestWorkerSeeds(seeds, bs, w);
                        if (want != got && bad.Length == 0)
                        {
                            bad = "seeds " + seeds + ", block " + bs + ", workers " + w + ": " + got + " vs " + want;
                        }
                    }
                }
            }

            check(bad.Length == 0, "the busiest worker's seeds in closed form equal the block-by-block deal",
                  bad.Length == 0 ? cases + " cases" : bad);
            check(BlockSizing.BusiestWorkerSeeds(15, 256, 16) == 15 && BlockSizing.BusiestWorkerSeeds(26, 25, 16) == 25,
                  "the gate's two measured cases: 15 survivors at 256 is one worker, 26 at 25 is a block of 25 (GUARD)", "");
        }

        /// <summary>The loop FunnelGate.BusiestWorkerSeeds ran until 2026-09-24, kept as the oracle.</summary>
        private static long Dealt(long seeds, int blockSize, int threads)
        {
            if (seeds <= 0) return 0;
            long blocks = (seeds + blockSize - 1) / blockSize;
            int workers = (int)Math.Max(1, Math.Min(threads, blocks));
            long[] load = new long[workers];
            long left = seeds;
            for (int b = 0; left > 0; b++)
            {
                long take = Math.Min(blockSize, left);
                load[b % workers] += take;
                left -= take;
            }

            long max = 0;
            foreach (long l in load) max = Math.Max(max, l);
            return max;
        }

        // =========================================================================================
        // The session decides, says so in the plan, and never writes the size back into the query.

        private static void SessionDecides(Action<bool, string, string> check)
        {
            Query q = Query("auto", "\"grid\":384,\"screen\":\"off\"", Meadows);
            SearchSession s = Session(q, 512, 8);
            check(s.Plan.BlockSize == 16 && s.Plan.Blocks == 32 && s.BlockDecision.Source == BlockSizeSource.Automatic
                  && PlanLine(s, "block size   16, sized automatically") != null,
                  "a 512-seed session on 8 workers is cut into 32 blocks of 16, and the plan says why",
                  PlanLine(s, "block size") ?? "(no block-size line)");
            check(q.Search.BlockSize == null && s.Query.Search.BlockSize == null,
                  "the decided size is never written back into the query (stage one and the raise share it)", "");

            SearchSession given = Session(Query("given", "\"grid\":384,\"screen\":\"off\",\"block_size\":256", Meadows), 512, 8);
            check(given.Plan.BlockSize == 256 && given.Plan.Blocks == 2
                  && Warning(given, "6 of the 8 workers have nothing to do") != null
                  && PlanLine(given, "block size   256, as given") != null,
                  "search.block_size in a query file is kept, and the idle warning is in the preflight",
                  Warning(given, "--block-size") ?? "(no warning)");

            SearchSession single = SearchSession.Create(Query("explain", "\"grid\":384", Meadows),
                                                        UnavailableLocationOracle.Instance, Engine, 1, 1,
                                                        outPath: null, noPrefilter: true, acceptScanOrder: true);
            check(single.Plan.BlockSize == 256 && single.Plan.Blocks == 1 && PlanLine(single, BlockSizeDecision.Label) == null,
                  "explain's one seed on one worker: the default, and no block-size line (GUARD)", "");
        }

        // =========================================================================================
        // A raised grid: the planner is asked at the RAISED grid, the peek gets the RAISED hash.

        private static void SessionFollowsTheRaise(Action<bool, string, string> check)
        {
            HardwareInfo machine = HardwareProbe.Probe(new HardwareProbeOptions
            {
                LogicalCoresOverride = 16,
                AvailableMemoryOverride = 64L * 1024 * 1024 * 1024,
                ReadEnvironment = false,
            });

            List<double> grids = new List<double>();
            List<string> hashes = new List<string>();
            Func<double, WorkerPlan> planner = g =>
            {
                grids.Add(g);
                return WorkerPlanner.Plan(ResourceMode.Full, WorkerFootprints.For(WorkTier.BiomeGrid, 1000), machine,
                                          new WorkerPlanOptions { RequestedWorkers = g <= 12.0 ? 3 : 8 });
            };

            Query q = Query("raise",
                            "\"grid\":96",
                            @"{""id"":""home"",""target"":""world:spawn_island_area"",""metric"":""spawn_island_area"",""test"":""at_least"",""value"":1000000,""importance"":""must""}");
            SearchSession s = SearchSession.Create(q, UnavailableLocationOracle.Instance, Engine, 512, 0,
                                                   outPath: null, acceptScanOrder: true, allowVacuous: true,
                                                   plannerAtGrid: planner,
                                                   resumeCheckpoint: h => { hashes.Add(h); return null; });

            check(s.Grid.RaisedFrom == 96.0 && grids.Count == 2 && grids[0] == 96.0 && grids[1] == 12.0,
                  "the planner is asked at the query's grid, then at the raised G12",
                  "grids asked: " + string.Join(", ", grids));
            check(s.WorkerPlan != null && s.WorkerPlan.Workers == 3 && s.Threads == 3 && s.BlockDecision.Workers == 3
                  && s.Plan.BlockSize == 42 && PlanLine(s, "threads      3") != null,
                  "the raised session runs, prints and sizes its blocks for the raised grid's 3 workers (512 / 12 = 42)",
                  "threads " + s.Threads + ", block " + s.Plan.BlockSize);
            check(hashes.Count == 2 && hashes[1] == s.QueryHash && hashes[0] != s.QueryHash,
                  "the checkpoint peek gets the RAISED hash last - the one Start will open",
                  hashes.Count + " calls");
        }

        // =========================================================================================
        // Adoption: the same run at another thread count resumes; nothing else is adopted.

        private static void SessionAdoptsOnlyItsOwnCheckpoint(Action<bool, string, string> check)
        {
            const string search = "\"grid\":384,\"screen\":\"off\"";
            SearchSession first = Session(Query("resume", search, Meadows), 512, 8);
            Checkpoint ckpt = CheckpointOf(first, nextBlock: 3);
            ckpt.LoadedFrom = "C:\\cache\\resume.ckpt";

            int calls = 0;
            Func<string, Checkpoint?> peek = h =>
            {
                calls++;
                return h == ckpt.QueryHash ? ckpt : null;
            };

            SearchSession second = SearchSession.Create(Query("resume", search, Meadows), UnavailableLocationOracle.Instance,
                                                        Engine, 512, 16, outPath: null, acceptScanOrder: true,
                                                        allowVacuous: true, resumeCheckpoint: peek);
            bool matches = Throws(() => ckpt.MustMatch(second.Query, second.Plan, second.QueryHash)) == null;
            check(second.Plan.BlockSize == 16 && second.BlockDecision.Source == BlockSizeSource.Adopted
                  && second.Plan.Key == first.Plan.Key && second.Plan.Limit == first.Plan.Limit && matches
                  && PlanLine(second, "from the checkpoint being resumed") != null,
                  "resumed on 16 workers (which alone would pick 8), the checkpoint's 16 is adopted and MustMatch passes",
                  "block " + second.Plan.BlockSize + ", " + second.BlockDecision.Source);

            SearchSession asked = SearchSession.Create(Query("resume", search + ",\"block_size\":8", Meadows),
                                                       UnavailableLocationOracle.Instance, Engine, 512, 16, outPath: null,
                                                       acceptScanOrder: true, allowVacuous: true, resumeCheckpoint: peek);
            string? refusal = Refusal(asked, "this checkpoint's blocks are 16 seeds");
            check(calls == 2 && refusal != null && refusal.Contains("--block-size 16", StringComparison.Ordinal)
                  && refusal.Contains("a block size of 8 was asked for", StringComparison.Ordinal)
                  && refusal.Contains("delete C:\\cache\\resume.ckpt", StringComparison.Ordinal),
                  "a different size asked for on a resume is REFUSED in the preflight - the peek runs even when a size is given",
                  refusal ?? "(no refusal) after " + calls + " peeks");

            SearchSession fresh = Session(Query("resume", search, Meadows), 512, 16);
            string? why = Throws(() => ckpt.MustMatch(fresh.Query, fresh.Plan, fresh.QueryHash));
            check(fresh.Plan.BlockSize == 8 && why != null
                  && why.Contains("16 in the checkpoint, 8 now", StringComparison.Ordinal)
                  && why.Contains("Pass --block-size 16", StringComparison.Ordinal),
                  "without the peek, 16 workers pick 8 and the resume is refused with both sizes and the remedy",
                  why ?? "(not refused)");

            // A funnel's stage two checkpoints under the SAME hash: sequential over the survivors,
            // keyed by a hash of the list, with the survivor count as its limit.
            int[] survivors = { 5, 17, 99, 1234, -8 };
            ScanPlan stageTwo = ScanPlan.OverSeeds(survivors, 2);
            Checkpoint two = CheckpointOf(first, 1);
            two.Order = "sequential";
            two.Key = stageTwo.Key;
            two.From = stageTwo.From;
            two.To = stageTwo.To;
            two.Limit = stageTwo.Limit;
            two.BlockSize = 2;
            SearchSession notTwo = SearchSession.Create(Query("resume", search, Meadows), UnavailableLocationOracle.Instance,
                                                        Engine, 512, 8, outPath: null, acceptScanOrder: true,
                                                        allowVacuous: true, resumeCheckpoint: _ => two);
            check(notTwo.Plan.BlockSize == 16 && notTwo.BlockDecision.Source == BlockSizeSource.Automatic
                  && PlanLine(notTwo, "from the checkpoint being resumed") == null,
                  "a stage-two checkpoint under the same hash is NOT adopted into the full run's plan",
                  "block " + notTwo.Plan.BlockSize);

            Checkpoint otherBudget = CheckpointOf(first, 1);
            otherBudget.Limit = 1_000;
            otherBudget.BlockSize = 64;
            SearchSession notOther = SearchSession.Create(Query("resume", search, Meadows), UnavailableLocationOracle.Instance,
                                                          Engine, 512, 8, outPath: null, acceptScanOrder: true,
                                                          allowVacuous: true, resumeCheckpoint: _ => otherBudget);
            string? budget = Throws(() => otherBudget.MustMatch(notOther.Query, notOther.Plan, notOther.QueryHash));
            check(notOther.Plan.BlockSize == 16 && budget != null && budget.Contains("seed budget", StringComparison.Ordinal)
                  && !budget.Contains("block size", StringComparison.Ordinal),
                  "a checkpoint of another --seeds is not adopted, and the refusal names the budget, not the block size",
                  budget ?? "(not refused)");

            Checkpoint zero = CheckpointOf(first, 1);
            zero.BlockSize = 0;
            SearchSession notZero = SearchSession.Create(Query("resume", search, Meadows), UnavailableLocationOracle.Instance,
                                                         Engine, 512, 8, outPath: null, acceptScanOrder: true,
                                                         allowVacuous: true, resumeCheckpoint: _ => zero);
            string? invalid = Throws(() => zero.MustMatch(notZero.Query, notZero.Plan, notZero.QueryHash));
            check(notZero.BlockDecision.Source == BlockSizeSource.Automatic && invalid != null
                  && invalid.Contains("not valid", StringComparison.Ordinal),
                  "a hand-edited block_size of 0 is not adopted, and the checkpoint is named invalid", invalid ?? "(not refused)");

            SearchSession thrown = SearchSession.Create(Query("resume", search, Meadows), UnavailableLocationOracle.Instance,
                                                        Engine, 512, 8, outPath: null, acceptScanOrder: true,
                                                        allowVacuous: true,
                                                        resumeCheckpoint: _ => throw new System.IO.IOException("locked"));
            check(thrown.Plan.BlockSize == 16, "a peek that throws counts as no checkpoint (Start reports the file)", "");
        }

        private static void CheckpointMatching(Action<bool, string, string> check)
        {
            SearchSession s = Session(Query("match", "\"grid\":384,\"screen\":\"off\"", Meadows), 512, 8);
            Checkpoint c = CheckpointOf(s, 0);
            c.BlockSize = 99;
            bool all = c.MatchesExceptBlockSize(s.Query, s.Plan, s.QueryHash);

            var mutations = new List<(string, Action<Checkpoint>)>
            {
                ("hash", k => k.QueryHash = "0000"),
                ("defs", k => k.Defs = k.Defs + 1),
                ("grid", k => k.Grid = 96),
                ("order", k => k.Order = "sequential"),
                ("key", k => k.Key ^= 1),
                ("from", k => k.From = k.From + 1),
                ("to", k => k.To = k.To - 1),
                ("limit", k => k.Limit = k.Limit + 1),
            };

            List<string> leaked = new List<string>();
            foreach ((string name, Action<Checkpoint> mutate) in mutations)
            {
                Checkpoint m = CheckpointOf(s, 0);
                mutate(m);
                if (m.MatchesExceptBlockSize(s.Query, s.Plan, s.QueryHash)) leaked.Add(name);
            }

            check(all && leaked.Count == 0,
                  "MatchesExceptBlockSize ignores the block size and nothing else MustMatch checks",
                  leaked.Count == 0 ? "8 fields" : "matched despite: " + string.Join(", ", leaked));

            Checkpoint g = CheckpointOf(s, 0);
            g.Grid = 12.5;
            string? grid = Throws(() => g.MustMatch(s.Query, s.Plan, s.QueryHash));
            check(grid != null && grid.Contains("(12.5 m -> 384 m)", StringComparison.Ordinal),
                  "MustMatch's grid sentence is invariant ('12.5', never '12,5')", grid ?? "(not refused)");
        }

        // =========================================================================================
        // The preflight derives the counts itself and refuses a decision that is not its plan's.

        private static void PreflightDerivesTheCounts(Action<bool, string, string> check)
        {
            Query q = Query("direct", "\"grid\":384,\"screen\":\"off\"", Meadows);
            CompiledQuery cq = CompiledQuery.Compile(q, UnavailableLocationOracle.Instance);
            ScanPlan plan = new ScanPlan(q.Search.Order, q.Search.From, q.Search.To, 1, 64, 100);

            SearchPreflight direct = SearchPreflightCheck.Check(q, cq, plan, new OutputPolicy(), 8, acceptScanOrder: true);
            bool warned = false;
            foreach (string w in direct.Warnings)
            {
                if (w.Contains("--block-size", StringComparison.Ordinal) && w.Contains("6 of the 8 workers", StringComparison.Ordinal)) warned = true;
            }

            check(warned && direct.BlockDecision.Source == BlockSizeSource.Explicit,
                  "a direct call with no decision (the Safety tests') still gets the true idle count: 64 of 100 seeds on 8",
                  "");

            string? mismatch = Throws(() => SearchPreflightCheck.Check(q, cq, plan, new OutputPolicy(), 8,
                                                                         acceptScanOrder: true,
                                                                         blocks: BlockSizing.Decide(null, 100, 8)));
            check(mismatch != null && mismatch.Contains("does not describe the plan", StringComparison.Ordinal),
                  "a decision whose size is not the plan's throws instead of printing another run's counts",
                  mismatch ?? "(accepted)");

            // An adopted leg of a T3 query: the '--block-size 16' advice is for a fresh run.
            Query t3 = Query("t3", "\"grid\":384,\"screen\":\"off\"",
                @"{""id"":""land"",""target"":""world:land"",""metric"":""land_share"",""test"":""at_least"",""value"":0.2,""importance"":""nice""}");
            CompiledQuery c3 = CompiledQuery.Compile(t3, UnavailableLocationOracle.Instance);
            ScanPlan p3 = new ScanPlan(t3.Search.Order, t3.Search.From, t3.Search.To, 1, 256, 20_000);
            SearchPreflight adopted = SearchPreflightCheck.Check(t3, c3, p3, new OutputPolicy(), 8, acceptScanOrder: true,
                blocks: BlockSizing.Decide(null, 20_000, 8, resume: new ResumePoint { BlockSize = 256, NextBlock = 4 }));
            SearchPreflight freshT3 = SearchPreflightCheck.Check(t3, c3, p3, new OutputPolicy(), 8, acceptScanOrder: true,
                blocks: BlockSizing.Decide(null, 20_000, 8));
            string? a = null, f = null;
            foreach (string w in adopted.Warnings) if (w.Contains("--block-size 16", StringComparison.Ordinal)) a = w;
            foreach (string w in freshT3.Warnings) if (w.Contains("--block-size 16", StringComparison.Ordinal)) f = w;
            check(c3.MaxTier >= Tier.T3 && a != null && a.Contains("on a FRESH run", StringComparison.Ordinal)
                  && f != null && !f.Contains("FRESH", StringComparison.Ordinal),
                  "on a resumed leg the T3 '--block-size 16' warning is advice for a fresh run; on a fresh one it is not reworded",
                  a ?? "(no T3 warning)");
        }

        // =========================================================================================
        // The funnel gate takes the decision and prints its sentences.

        private static void Gate(Action<bool, string, string> check)
        {
            FunnelGate auto = new FunnelGate
            {
                Scanned = 6_000,
                Survivors = 37,
                Decision = BlockSizing.Decide(null, 37, 8),
                SecondsPerSurvivor = 1.0,
            };
            List<string> lines = new List<string>(auto.Lines());
            check(auto.BlockSize == 1 && auto.Threads == 8 && auto.EffectiveWorkers == 8 && auto.BusiestWorkerSeeds == 5
                  && lines.Exists(l => l.StartsWith("stage 2 block size   1, sized automatically", StringComparison.Ordinal)),
                  "stage two over 37 survivors on 8 workers: sized automatically, all 8 busy, and the gate says so",
                  string.Join(" | ", lines.FindAll(l => l.Contains("block", StringComparison.Ordinal))));

            FunnelGate given = new FunnelGate
            {
                Scanned = 6_000,
                Survivors = 37,
                Decision = BlockSizing.Decide(256, 37, 8),
                SecondsPerSurvivor = 1.0,
            };
            List<string> gl = new List<string>(given.Lines());
            check(given.EffectiveWorkers == 1 && given.BusiestWorkerSeeds == 37
                  && gl.Exists(l => l.StartsWith("WARNING: ", StringComparison.Ordinal)
                                    && l.Contains("--block-size", StringComparison.Ordinal)
                                    && l.Contains("7 of the 8 workers have nothing to do", StringComparison.Ordinal)),
                  "a given 256 over 37 survivors: one worker, and the gate carries the idle warning",
                  string.Join(" | ", gl.FindAll(l => l.StartsWith("WARNING", StringComparison.Ordinal))));

            FunnelGate empty = new FunnelGate { Scanned = 6_000, Survivors = 0, Decision = BlockSizing.Decide(null, 0, 8) };
            List<string> el = new List<string>(empty.Lines());
            check(empty.BlockSize == 1 && empty.BusiestWorkerSeeds == 0
                  && !el.Exists(l => l.StartsWith("stage 2 block size", StringComparison.Ordinal)),
                  "a gate with no survivor has no block-size sentence", "");
        }

        // =========================================================================================
        // helpers

        private static Query Query(string name, string search, string goals)
            => QueryReader.Parse("{\"version\":1,\"defs\":1,\"name\":\"" + name + "\",\"search\":{" + search
                                 + "},\"goals\":[" + goals + "]}", name);

        private static SearchSession Session(Query q, long seeds, int threads)
            => SearchSession.Create(q, UnavailableLocationOracle.Instance, Engine, seeds, threads, outPath: null,
                                    acceptScanOrder: true, allowVacuous: true);

        /// <summary>A checkpoint of <paramref name="s"/>, built the way <c>SearchSession.Start</c> builds one.</summary>
        private static Checkpoint CheckpointOf(SearchSession s, long nextBlock) => new Checkpoint
        {
            Engine = Engine,
            QueryHash = s.QueryHash,
            Defs = s.Query.Defs,
            Grid = s.Query.Search.Grid,
            Order = s.Plan.Order.ToString().ToLowerInvariant(),
            Key = s.Plan.Key,
            From = s.Plan.From,
            To = s.Plan.To,
            BlockSize = s.Plan.BlockSize,
            Limit = s.Plan.Limit,
            NextBlock = nextBlock,
        };

        private static string? Throws(Action a)
        {
            try
            {
                a();
                return null;
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message;
            }
        }

        private static string? PlanLine(SearchSession s, string needle)
        {
            foreach (string l in s.Preflight.Plan)
            {
                if (l.Contains(needle, StringComparison.Ordinal)) return l;
            }

            return null;
        }

        private static string? Warning(SearchSession s, string needle)
        {
            foreach (string w in s.Preflight.Warnings)
            {
                if (w.Contains(needle, StringComparison.Ordinal)) return w;
            }

            return null;
        }

        private static string? Refusal(SearchSession s, string needle)
        {
            foreach (string r in s.Preflight.Refusals)
            {
                if (r.Contains(needle, StringComparison.Ordinal)) return r;
            }

            return null;
        }

        private static string Show(BlockSizeDecision d)
            => d.Size + " x " + d.Blocks + " blocks, " + d.Source + ", busy " + d.BusyWorkers + "/" + d.Workers
               + ", busiest " + d.BusiestWorkerSeeds;
    }
}
