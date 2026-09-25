using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using SeedLab.Runtime.Execution;

namespace SeedLab.RuntimeTests
{
    /// <summary>
    /// The saturating profile's arithmetic, exact: how many seeds a measurement gets, the steady-state
    /// window, which indices a replayed plan reads, the seed-list digest, the plan reader, the memory
    /// sampler on made-up readers, and the Linux /proc parsers on made-up text. Nothing here touches the
    /// machine, so the answers are the same everywhere.
    /// </summary>
    public static class ProfileMathChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            Saturation(check);
            Steady(check);
            Indices(check);
            Plans(check);
            Sampler(check);
            Proc(check);
        }

        // ---- --saturate ------------------------------------------------------------------------------

        private static void Saturation(Action<bool, string, string> check)
        {
            int a = SaturationPlanner.Seeds(7.0985, 5, 1, out bool c1);
            check(a == 36 && !c1, "saturate: 7.1 seeds/s x 5 s at 1 worker = 36 (the rate beats the 32 floor)", a.ToString());

            int b = SaturationPlanner.Seeds(23.6116, 5, 4, out bool c2);
            check(b == 128 && !c2, "saturate: 23.6 seeds/s x 5 s at 4 workers = 128 (the 32-per-worker floor beats 119)", b.ToString());

            int c = SaturationPlanner.Seeds(1001, 10, 16, out _);
            check(c == 10016, "saturate: 10,010 seeds rounds UP to a multiple of 16 (10,016)", c.ToString());

            int d = SaturationPlanner.Seeds(0, 10, 3, out _);
            check(d == 96, "saturate: an unknown rate (0) still gets the floor, 3 x 32", d.ToString());

            int e = SaturationPlanner.Seeds(double.NaN, 10, 5, out _);
            check(e == 160, "saturate: a NaN rate is treated as unknown", e.ToString());

            int f = SaturationPlanner.Seeds(1e9, 10, 7, out bool c3);
            check(f == 999_999 && c3, "saturate: the 1,000,000 cap rounds DOWN to a multiple of 7 and says it was capped", f + (c3 ? " capped" : ""));

            int g = SaturationPlanner.Seeds(50, 1, 3, out _, minPerWorker: 1, cap: 2);
            check(g == 3, "saturate: a cap below one seed per worker still gives each worker one", g.ToString());

            bool allOk = true;
            string bad = "";
            Random rng = new Random(20260925);
            for (int i = 0; i < 2000; i++)
            {
                int w = rng.Next(1, 65);
                double rate = rng.NextDouble() * 5000;
                double secs = 1 + rng.NextDouble() * 120;
                int n = SaturationPlanner.Seeds(rate, secs, w, out bool capped);
                bool ok = n % w == 0 && n >= w * SaturationPlanner.MinPerWorker && n >= Math.Ceiling(rate * secs) - (capped ? 1e12 : 0)
                          && n - Math.Max(w * SaturationPlanner.MinPerWorker, Math.Ceiling(rate * secs)) < w;
                if (!ok)
                {
                    allOk = false;
                    bad = "w=" + w + " rate=" + rate + " s=" + secs + " -> " + n;
                    break;
                }
            }

            check(allOk, "saturate: 2,000 random cases are multiples of the worker count, meet the floor and the time, and overshoot by less than one round",
                  allOk ? "" : bad);

            double r = SaturationPlanner.RateFromMeanSeedMs(4, 169.4079);
            check(Math.Abs(r - 23.6116) < 0.001, "saturate: the rate is workers x 1000 / mean seed ms", r.ToString("F4"));
            check(SaturationPlanner.RateFromMeanSeedMs(4, double.NaN) == 0 && SaturationPlanner.RateFromMeanSeedMs(4, 0) == 0,
                  "saturate: no mean seed time, no rate", "");
            check(SaturationPlanner.PilotSeeds(1) == 4 && SaturationPlanner.PilotSeeds(16) == 32,
                  "saturate: the pilot is two seeds per worker, never fewer than four", SaturationPlanner.PilotSeeds(1) + ", " + SaturationPlanner.PilotSeeds(16));
        }

        // ---- the steady state ------------------------------------------------------------------------

        private static void Steady(Action<bool, string, string> check)
        {
            // 1,000 ticks per second. Worker 0: nine 100-tick seeds from 0 to 900. Worker 1: four
            // 200-tick seeds from 50 to 850. The section runs 0..1000.
            List<long> s = new List<long>(), e = new List<long>();
            List<int> w = new List<int>();
            for (int i = 0; i < 9; i++) { s.Add(i * 100); e.Add(i * 100 + 100); w.Add(0); }
            for (int i = 0; i < 4; i++) { s.Add(50 + i * 200); e.Add(250 + i * 200); w.Add(1); }
            SteadyStateResult r = SteadyState.Compute(0, 1000, s.ToArray(), e.ToArray(), w.ToArray(), 2, 1000);
            // Window 50..850 = 0.8 s. Worker 0: half of [0,100), all of seven, half of [800,900) = 8;
            // worker 1: all four. 12 seeds in 0.8 s = 15/s; the section did 13 in 1 s.
            check(r.Defined && Near(r.WindowSeconds, 0.8) && Near(r.HeadSeconds, 0.05) && Near(r.TailSeconds, 0.15),
                  "steady: the window runs from the latest first start to the earliest last finish",
                  "head " + r.HeadSeconds + ", window " + r.WindowSeconds + ", tail " + r.TailSeconds);
            check(Near(r.SeedsInWindow, 12) && Near(r.WindowSeedsPerSecond, 15) && Near(r.SectionSeedsPerSecond, 13),
                  "steady: seeds straddling an edge count by the share inside it", r.SeedsInWindow + " seeds, " + r.WindowSeedsPerSecond + "/s");
            check(Near(r.TailLoss, 1 - 13.0 / 15.0), "steady: tail loss = 1 - section rate / window rate", r.TailLoss.ToString("F4"));

            SteadyStateResult idle = SteadyState.Compute(0, 1000, new long[] { 0 }, new long[] { 100 }, new[] { 0 }, 2, 1000);
            check(!idle.Defined && idle.Why != null && idle.Why.Contains("1 of 2"), "steady: a worker with no seed means no window, and says so", idle.Why ?? "");

            SteadyStateResult apart = SteadyState.Compute(0, 1000, new long[] { 0, 600 }, new long[] { 500, 900 }, new[] { 0, 1 }, 2, 1000);
            check(!apart.Defined, "steady: one worker done before the other started means no window", apart.Why ?? "");

            bool threw = false;
            try { SteadyState.Compute(0, 10, new long[] { 0 }, new long[] { 1 }, new[] { 3 }, 2, 1000); }
            catch (ArgumentOutOfRangeException) { threw = true; }
            check(threw, "steady: a seed from a worker that does not exist is an error, not a silent drop", "");
        }

        private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;

        // ---- indices and the seed-list digest --------------------------------------------------------

        private static void Indices(Action<bool, string, string> check)
        {
            check(ProfileIndices.Measured(4294967295L, 1) == 0, "indices: the order wraps at 2^32", "");
            List<long> warm = new List<long>();
            for (int wk = 0; wk < 2; wk++)
            {
                for (int k = 0; k < 3; k++) warm.Add(ProfileIndices.Warmup(10, 5, 3, wk, k));
            }

            check(string.Join(",", warm) == "15,16,17,18,19,20",
                  "indices: warm-up seeds follow the measured ones, worker by worker, and never overlap them", string.Join(",", warm));

            // A replay rebuilds the same (from, n, warm-up), so it reads exactly these indices again.
            bool same = true;
            for (int i = 0; i < 100; i++) same &= ProfileIndices.Measured(123, i) == ProfileIndices.Measured(123, i) && ProfileIndices.Measured(123, i) == 123 + i;
            check(same, "indices: a replay with the plan's first index reads the same measured indices", "");

            byte[] raw = { 1, 0, 0, 0, 2, 0, 0, 0 };
            string want = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();
            string got = SeedListDigest.Of(new[] { 1, 2 });
            check(got == want, "digest: the seed list is hashed as little-endian int32s in index order", got.Substring(0, 16));
            check(SeedListDigest.Of(new[] { 2, 1 }) != got, "digest: the order of the seeds is part of it", "");
            check(SeedListDigest.Of(Array.Empty<int>()) == "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                  "digest: an empty list is SHA-256 of nothing", "");
        }

        // ---- the plan reader -------------------------------------------------------------------------

        private const string PlanDoc = "{ \"schema\": \"seedlab-profile/2\", \"run\": { \"key\": \"0xA17A25EED10C5117\", \"from_index\": 7, \"warmup_per_worker\": 2 },\n"
                                       + "  \"sections\": [ { \"tier\": \"t4\", \"workers\": 4, \"seeds\": 128, \"seed_list_sha256\": \"ab\" },\n"
                                       + "                { \"tier\": \"t2\", \"grid_m\": 384, \"workers\": 1, \"seeds\": 32 },\n"
                                       + "                { \"tier\": \"t5\", \"prefix\": 21, \"prefix_requested\": 22, \"workers\": 2, \"seeds\": 64 } ] }";

        private static void Plans(Action<bool, string, string> check)
        {
            ProfilePlan p = ProfilePlan.Parse(PlanDoc);
            check(p.Key == 0xA17A25EED10C5117UL && p.From == 7 && p.Warmup == 2 && p.Sections.Count == 3,
                  "plan: the seed order, the first index and the warm-up are read", p.Sections.Count + " sections");
            check(p.Sections[0].Tier == "t4" && p.Sections[0].Workers == 4 && p.Sections[0].Seeds == 128 && p.Sections[0].SeedListSha256 == "ab"
                  && p.Sections[1].Grid == 384 && p.Sections[2].PrefixRequested == 22,
                  "plan: each section's tier, grid, ASKED-FOR prefix (not the run length), workers, seeds and digest, in order", "");

            check(p.Conflict(null, null, null) == null && p.Conflict(0xA17A25EED10C5117UL, 7, 2) == null,
                  "plan: options left out, or equal to the plan's, agree", "");
            check((p.Conflict(1, null, null) ?? "").Contains("--key") && (p.Conflict(null, 8, null) ?? "").Contains("--from")
                  && (p.Conflict(null, null, 3) ?? "").Contains("--warmup"),
                  "plan: a different key, first index or warm-up is named as the conflict", p.Conflict(null, null, 3) ?? "");

            check(Refused(PlanDoc.Replace("seedlab-profile/2", "seedlab-profile/1"), "only seedlab-profile/2"),
                  "plan: a /1 document is refused in plain words (it lacks the asked-for prefix)", "");
            check(Refused(PlanDoc.Replace("\"prefix_requested\": 22, ", ""), "prefix_requested"),
                  "plan: a t5 section without its asked-for prefix is refused", "");
            check(Refused("{ \"schema\": \"seedlab-profile/2\", \"run\": { \"key\": \"0x1\", \"from_index\": 0, \"warmup_per_worker\": 3 } }", "no measured sections"),
                  "plan: an --overhead profile (no sections) is not a plan", "");
            check(Refused("not json", "not a JSON"), "plan: something that is not JSON says so", "");
        }

        private static bool Refused(string json, string words)
        {
            try
            {
                ProfilePlan.Parse(json);
                return false;
            }
            catch (FormatException ex)
            {
                return ex.Message.Contains(words, StringComparison.Ordinal);
            }
        }

        // ---- the memory sampler ----------------------------------------------------------------------

        private static void Sampler(Action<bool, string, string> check)
        {
            SeriesStat? st = SeriesStat.Of(new long[] { 5, -1, 7, 3, 99 }, 4);
            check(st != null && st.Count == 3 && st.Peak == 7 && Near(st.Mean, 5) && st.First == 5 && st.Last == 3,
                  "sampler: statistics skip the samples that were not read, and stop at the count", st == null ? "null" : st.Count + " " + st.Peak + " " + st.Mean);
            check(SeriesStat.Of(new long[] { -1, -1 }, 2) == null, "sampler: a figure never read is absent, not 0", "");

            long next = 0;
            int cpuCalls = 0;
            ResourceReaders fake = new ResourceReaders
            {
                WorkingSet = () => Interlocked.Increment(ref next) * 100,
                PrivateBytes = () => null,
                GcHeap = () => throw new InvalidOperationException("a reader that throws"),
                GcCommitted = () => 42,
                ThreadCpu = () => TimeSpan.FromMilliseconds(10 * Interlocked.Increment(ref cpuCalls)),
            };
            ResourceSampler s = ResourceSampler.Start(fake, TimeSpan.FromMilliseconds(20), capacity: 4);
            Thread.Sleep(300);
            ResourceSummary z = s.Stop();
            s.Dispose();
            check(z.Samples >= 3 && z.WorkingSet != null && z.WorkingSet.Count == z.Samples && z.WorkingSet.Peak == z.Samples * 100L,
                  "sampler: samples at its start, on its interval and at its stop, past its first capacity", z.Samples + " samples, peak " + z.WorkingSet?.Peak);
            check(z.PrivateBytes == null && z.GcHeap == null, "sampler: a reader returning null or throwing leaves its figure absent", "");
            check(z.GcCommitted != null && z.GcCommitted.Peak == 42 && Near(z.GcCommitted.Mean, 42), "sampler: a steady figure's peak and mean are that figure", "");
            check(z.SamplerCpu.HasValue && Near(z.SamplerCpu.Value.TotalMilliseconds, 10), "sampler: its own thread CPU is read at its start and its end", z.SamplerCpu?.ToString() ?? "null");
            check(ReferenceEquals(z, s.Stop()), "sampler: stopping twice returns the same summary", "");

            ResourceSampler real = ResourceSampler.Start(ResourceReaders.Bcl(), TimeSpan.FromMilliseconds(50));
            Thread.Sleep(120);
            ResourceSummary rz = real.Stop();
            real.Dispose();
            check(rz.WorkingSet != null && rz.WorkingSet.Peak > 0 && rz.GcHeap != null && rz.GcHeap.Peak > 0 && rz.GcCommitted != null && rz.PrivateBytes == null,
                  "sampler: the BCL readers read this process (private bytes needs the host)", rz.WorkingSet?.Peak + " B working set");
        }

        // ---- Linux's /proc files ---------------------------------------------------------------------

        private static void Proc(Action<bool, string, string> check)
        {
            ProcessIoReading? io = ProcFiles.ParseIo("rchar: 1000\nwchar: 2000\nsyscr: 3\nsyscw: 4\nread_bytes: 4096\nwrite_bytes: 8192\ncancelled_write_bytes: 0\n");
            check(io != null && io.ReadBytes == 1000 && io.WriteBytes == 2000 && io.ReadOps == 3 && io.WriteOps == 4
                  && io.StorageReadBytes == 4096 && io.StorageWriteBytes == 8192,
                  "/proc/self/io: every call's bytes, the calls, and what reached storage", "");
            check(ProcFiles.ParseIo("nothing: here\n") == null, "/proc/self/io: text without rchar/wchar is no reading", "");

            ProcessMemoryReading? m = ProcFiles.ParseStatus("Name:\tvseed\nVmHWM:\t  2048 kB\nVmRSS:\t  1024 kB\nRssAnon:\t   512 kB\nVmSwap:\t     8 kB\n");
            check(m != null && m.WorkingSet == 1024 * 1024 && m.PeakWorkingSet == 2048 * 1024 && m.PrivateBytes == (512 + 8) * 1024,
                  "/proc/self/status: kB turned into bytes; private = anonymous resident + swapped", "");

            TimeSpan? t = ProcFiles.ParseSchedstat("123456789 55 7\n");
            check(t.HasValue && t.Value.Ticks == 1234567, "schedstat: the first field is nanoseconds on a processor", t?.Ticks.ToString() ?? "null");
            check(ProcFiles.ParseSchedstat("x") == null && ProcFiles.ParseSchedstat("-5 0 0") == null, "schedstat: garbage is no reading", "");

            ProcessIoReading dlt = ProcessIoReading.Delta(new ProcessIoReading { ReadBytes = 10, WriteBytes = 20, OtherBytes = 5 },
                                                          new ProcessIoReading { ReadBytes = 15, WriteBytes = 50, StorageReadBytes = 3 });
            check(dlt.ReadBytes == 5 && dlt.WriteBytes == 30 && dlt.OtherBytes == null && dlt.StorageReadBytes == null,
                  "io delta: field by field, and a field either side lacks stays absent", "");
        }
    }
}
