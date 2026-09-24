using System;
using System.Collections.Generic;
using System.IO;
using SeedLab.Search.Criteria;
using SeedLab.Search.Evaluation;
using SeedLab.Search.Execution;
using SeedLab.Search.Locations;

namespace SeedLab.SearchTests
{
    /// <summary>
    /// The funnel strategy's substrate: the survivor list's identity checks, and the plan that decides
    /// whether a funnel is worth running.
    ///
    /// <para>The survivor list is the one artefact a funnel adds, and every way it can be wrong is a
    /// way to get a plausible answer to a question nobody asked - the wrong query's survivors, another
    /// game build's survivors, a truncated list that reads as a smaller search. Each of those is
    /// checked here by constructing it, not by trusting the code that writes it.</para>
    /// </summary>
    public static class FunnelChecks
    {
        public static void Run(Action<bool, string, string> check, ILocationOracle oracle)
        {
            string dir = Path.Combine(Path.GetTempPath(), "seedlab-funnel-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                RoundTrip(check, dir);
                Rejections(check, dir);
                Ceiling(check, dir);
                Planning(check, oracle);
                ExplicitPlan(check);
                SameSeeds(check, oracle);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        private static SurvivorList.Header H(long scanned, string hash = "QHASH", string stamp = "STAMP")
            => new SurvivorList.Header
            {
                QueryHash = hash,
                Stamp = stamp,
                From = int.MinValue,
                To = int.MaxValue,
                Scanned = scanned,
            };

        private static void RoundTrip(Action<bool, string, string> check, string dir)
        {
            string p = Path.Combine(dir, "a.svr");
            int[] seeds = { int.MinValue, -1, 0, 1, 12345, int.MaxValue };
            SurvivorList.Write(p, H(1000), seeds, SurvivorList.DefaultMaxBytes);
            int[] back = SurvivorList.Read(p, "QHASH", "STAMP", int.MinValue, int.MaxValue, 1000,
                                           out SurvivorList.Header h);

            bool same = back.Length == seeds.Length;
            for (int i = 0; same && i < seeds.Length; i++) same = back[i] == seeds[i];
            check(same, "the survivor list round-trips exactly, including the extreme seeds",
                  seeds.Length + " seeds, int.MinValue and int.MaxValue among them");
            check(h.Scanned == 1000 && h.Survivors == 6 && Math.Abs(h.Ratio - 0.006) < 1e-12,
                  "the header carries the ratio stage two has to report",
                  "scanned 1,000, kept 6, ratio " + h.Ratio.ToString("0.###%"));
            check(SurvivorList.BytesFor(6) == new FileInfo(p).Length,
                  "the size arithmetic matches the file actually written",
                  SurvivorList.BytesFor(6) + " B predicted, " + new FileInfo(p).Length + " B on disk");
        }

        private static void Rejections(Action<bool, string, string> check, string dir)
        {
            string p = Path.Combine(dir, "b.svr");
            SurvivorList.Write(p, H(500), new[] { 7, 8, 9 }, SurvivorList.DefaultMaxBytes);

            check(Throws(() => SurvivorList.Read(p, "OTHERQUERY", "STAMP", int.MinValue, int.MaxValue, 500, out _)),
                  "a survivor list from another QUERY is refused",
                  "stage two against them would answer a question nobody asked");
            check(Throws(() => SurvivorList.Read(p, "QHASH", "OTHERBUILD", int.MinValue, int.MaxValue, 500, out _)),
                  "a survivor list from another game BUILD is refused",
                  "placement differs between builds, so they are not this build's survivors");

            // The range and the budget are NOT part of the query hash, so one survivor path is shared
            // by every range and budget of the same query. Before these two checks existed, a 200-seed
            // stage one served a --seeds 400 request and a --from/--to run wrote 26 of 26 records with
            // seeds outside the range it had just printed.
            check(Throws(() => SurvivorList.Read(p, "QHASH", "STAMP", 0, 1000000, 500, out _)),
                  "a survivor list from another RANGE is refused",
                  "reusing it would answer about seeds this run never asked to see");
            check(Throws(() => SurvivorList.Read(p, "QHASH", "STAMP", int.MinValue, int.MaxValue, 400, out _)),
                  "a survivor list from another BUDGET is refused",
                  "200 scanned against a 400-seed request would silently shrink the search");

            // Truncation: lop the last seed off and confirm it is not read as a shorter search.
            string t = Path.Combine(dir, "c.svr");
            byte[] whole = File.ReadAllBytes(p);
            File.WriteAllBytes(t, Trim(whole, 4));
            check(Throws(() => SurvivorList.Read(t, "QHASH", "STAMP", int.MinValue, int.MaxValue, 500, out _)),
                  "a TRUNCATED survivor list is refused rather than read as a smaller search",
                  "4 bytes removed from a 3-seed list");

            string bad = Path.Combine(dir, "d.svr");
            File.WriteAllBytes(bad, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            check(Throws(() => SurvivorList.Read(bad, "QHASH", "STAMP", int.MinValue, int.MaxValue, 500, out _)),
                  "a file that is not a survivor list at all is refused",
                  "bad magic");
        }

        private static void Ceiling(Action<bool, string, string> check, string dir)
        {
            string p = Path.Combine(dir, "e.svr");
            int[] many = new int[1000];
            check(Throws(() => SurvivorList.Write(p, H(2000), many, 64)),
                  "the survivor list refuses to exceed its ceiling instead of truncating",
                  "1,000 seeds against a 64 B ceiling");
            check(!File.Exists(p),
                  "and it writes no file when it refuses",
                  "a partial survivor list is a silently smaller search");
        }

        private static void Planning(Action<bool, string, string> check, ILocationOracle oracle)
        {
            // A query with a cheap must-have AND a location must-have: the case a funnel is for.
            Query mixed = new Query { Name = "mixed" };
            mixed.Goals.Add(new Goal
            {
                Id = "cheap", Target = new GoalTarget(TargetKind.Biome, "Meadows"), Metric = "area",
                Test = GoalTest.AtLeast, Value = 10_000_000, Importance = Importance.Must,
            });
            mixed.Goals.Add(new Goal
            {
                Id = "expensive", Target = new GoalTarget(TargetKind.Location, "Eikthyrnir"),
                Metric = "nearest_distance", Test = GoalTest.Near, Value = 2000,
                Importance = Importance.Must,
            });

            FunnelPlan p1 = FunnelPlan.For(mixed, CompiledQuery.Compile(mixed, oracle, false));
            check(p1.Usable && p1.StageOneGoals.Contains("cheap") && p1.DeferredGoals.Contains("expensive"),
                  "a query with a cheap must-have and a location must-have is funnel-able",
                  "stage 1: " + string.Join(",", p1.StageOneGoals)
                  + " | deferred: " + string.Join(",", p1.DeferredGoals));
            check(p1.StageOneQuery != null && p1.StageOneQuery!.Goals.Count == 1,
                  "the stage-one query carries ONLY the cheap must-haves",
                  (p1.StageOneQuery?.Goals.Count ?? -1) + " goal(s)");

            // Cheap goals only: nothing expensive to defer, so a funnel buys nothing.
            Query cheapOnly = new Query { Name = "cheap-only" };
            cheapOnly.Goals.Add(mixed.Goals[0]);
            FunnelPlan p2 = FunnelPlan.For(cheapOnly, CompiledQuery.Compile(cheapOnly, oracle, false));
            check(!p2.Usable, "a query with nothing to defer is NOT funnelled", p2.Reason);

            // Location must-have only: stage one would admit every seed.
            Query locOnly = new Query { Name = "loc-only" };
            locOnly.Goals.Add(mixed.Goals[1]);
            FunnelPlan p3 = FunnelPlan.For(locOnly, CompiledQuery.Compile(locOnly, oracle, false));
            check(!p3.Usable, "a query whose only must-have needs placement is NOT funnelled", p3.Reason);

            // A nice-to-have never filters, so it cannot justify a stage one.
            Query niceCheap = new Query { Name = "nice" };
            Goal nice = new Goal
            {
                Id = "rank", Target = new GoalTarget(TargetKind.Biome, "Meadows"), Metric = "area",
                Test = GoalTest.AtLeast, Value = 10_000_000, Importance = Importance.Nice,
            };
            niceCheap.Goals.Add(nice);
            niceCheap.Goals.Add(mixed.Goals[1]);
            FunnelPlan p4 = FunnelPlan.For(niceCheap, CompiledQuery.Compile(niceCheap, oracle, false));
            check(!p4.Usable,
                  "a cheap NICE-to-have cannot justify a stage one - it ranks, it never rejects",
                  p4.Reason);

            check(FunnelPlan.Decide(p1, out string why1) == SearchStrategy.Funnel && why1.StartsWith("funnel:"),
                  "the automatic choice states its reason either way", why1);
            check(FunnelPlan.Decide(p3, out string why3) == SearchStrategy.Sample && why3.StartsWith("sample:"),
                  "and falls back to sample with the reason", why3);
        }

        private static void ExplicitPlan(Action<bool, string, string> check)
        {
            int[] survivors = { -5, 0, 77, 12345 };
            ScanPlan p = ScanPlan.OverSeeds(survivors, 2);
            bool ok = p.IsExplicit && p.Limit == 4 && p.Blocks == 2;
            for (long i = 0; ok && i < 4; i++) ok = p.SeedAt(i) == survivors[i];
            check(ok, "stage two walks the survivor list through the ordinary block machinery",
                  "4 seeds, 2 blocks, index -> seed is the list itself");

            ScanPlan other = ScanPlan.OverSeeds(new[] { -5, 0, 77, 12346 }, 2);
            check(p.Key != other.Key,
                  "a different survivor set is a different run identity, so a checkpoint cannot cross over",
                  "keys " + p.Key.ToString("X16") + " vs " + other.Key.ToString("X16"));
        }

        /// <summary>
        /// Stage one must walk the SAME seeds, in the same order, as the run it stands in for.
        ///
        /// <para>This is the check that caught the worst bug in the feature. <c>ScanPlan</c> derives
        /// its Feistel key from the query hash when <c>search.key</c> is null, and the stage-one query
        /// is a different query with a different hash - so a stage one left to build its own plan
        /// sampled a DIFFERENT part of the space. Measured: a 600-seed funnel and a 600-seed ordinary
        /// run of the same query shared not one seed, and the funnel's 14 matches were 14 real matches
        /// to a question nobody had asked. The result set was not subtly wrong; it was an answer about
        /// the wrong seeds, which is the harder kind to notice.</para>
        /// </summary>
        private static void SameSeeds(Action<bool, string, string> check, ILocationOracle oracle)
        {
            Query full = new Query { Name = "same-seeds" };
            full.Goals.Add(new Goal
            {
                Id = "cheap", Target = new GoalTarget(TargetKind.Biome, "Meadows"), Metric = "area",
                Test = GoalTest.AtLeast, Value = 13_000_000, Importance = Importance.Must,
            });
            full.Goals.Add(new Goal
            {
                Id = "expensive", Target = new GoalTarget(TargetKind.Location, "Eikthyrnir"),
                Metric = "nearest_distance", Test = GoalTest.Near, Value = 600,
                Importance = Importance.Must,
            });

            FunnelPlan fp = FunnelPlan.For(full, CompiledQuery.Compile(full, oracle, false));
            if (!fp.Usable || fp.StageOneQuery == null)
            {
                check(false, "the same-seeds fixture is funnel-able", fp.Reason);
                return;
            }

            SearchSession fullSession = SearchSession.Create(full, oracle, "test", 600, 1);
            SearchSession free = SearchSession.Create(fp.StageOneQuery!, oracle, "test", 600, 1,
                                                      null, false, true, true);
            SearchSession tied = SearchSession.Create(fp.StageOneQuery!, oracle, "test", 600, 1,
                                                     null, false, true, true, fullSession.Plan);

            bool freeDiffers = false;
            for (long i = 0; i < 64 && !freeDiffers; i++)
            {
                if (free.Plan.SeedAt(i) != fullSession.Plan.SeedAt(i)) freeDiffers = true;
            }

            bool tiedSame = true;
            for (long i = 0; i < 600 && tiedSame; i++)
            {
                if (tied.Plan.SeedAt(i) != fullSession.Plan.SeedAt(i)) tiedSame = false;
            }

            check(freeDiffers,
                  "THE HAZARD: a stage one left to build its own plan samples different seeds",
                  "the stage-one query hashes differently, so its Feistel key differs");
            check(tiedSame,
                  "and passing the full run's plan makes stage one walk exactly the run's own seeds",
                  "600 of 600 indices agree");
        }

        private static byte[] Trim(byte[] b, int off)
        {
            byte[] t = new byte[b.Length - off];
            Array.Copy(b, t, t.Length);
            return t;
        }

        private static bool Throws(Action a)
        {
            try { a(); return false; }
            catch (Exception) { return true; }
        }
    }
}
