using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SeedLab.Cli.Commands;
using SeedLab.Cli.Infra;
using SeedLab.LocationOracle;
using SeedLab.Runtime.Execution;
using SeedLab.WorldGen.Diagnostics;
using SeedLab.WorldGen.Unity;

namespace SeedLab.Cli.Analysis
{
    /// <summary>
    /// Turns a <c>vseed profile</c> run into numbers a person can act on: per phase, the median, p10,
    /// p90 and mean milliseconds per seed, its share of the seed, its allocation and the time its
    /// children do not explain (<c>other = parent - sum(children)</c>, so nothing is silently dropped);
    /// JIT and GC inside the measured window; the machine it ran on; whether the machine was quiet;
    /// and a verdict, with the numbers behind it, on each hypothesis the profile was built to test.
    ///
    /// <para>The document is <c>seedlab-profile/2</c> JSON, UTF-8 without a BOM. A measurement that was
    /// not taken is left out, never written as 0. Only this layer reads a <see cref="PhaseSink"/>.
    /// /2 keeps every /1 field as it was and adds, per section, what the section used (<c>cpu</c>,
    /// <c>steady_state</c>, <c>memory</c> with the profiler's own table and the exact window peaks,
    /// <c>io</c>, <c>gc.last_gc</c> and <c>gc.heap_count_max</c>), how its seed count was chosen
    /// (<c>sizing</c>), and what a replay needs (<c>prefix_requested</c>, <c>first_index</c>,
    /// <c>seed_list_sha256</c>); per run, the <c>--saturate</c> pilots (every pilot run's seed count) or
    /// the <c>--plan</c> replayed (the pilots it ran again, and what differed from the plan's run); and
    /// the garbage collector's configuration in the machine block. A replay keeps the plan's
    /// <c>run.tier</c>; <c>run.plan</c> says it was a replay.</para>
    /// </summary>
    internal sealed class ProfileReport
    {
        public const string Schema = "seedlab-profile/2";

        private readonly ProfileRun _run;
        private readonly List<SectionView> _sections = new List<SectionView>();
        private readonly List<Hypothesis> _hypotheses = new List<Hypothesis>();
        private readonly bool _tainted;
        private readonly List<string> _taintReasons = new List<string>();

        public ProfileReport(ProfileRun run)
        {
            _run = run;
            if (run.Baseline != null && run.Baseline.Tainted)
            {
                foreach (string r in run.Baseline.Tick.Reasons) _taintReasons.Add("before the run: " + r);
            }

            // From the moment the probe started: the baseline before it is judged on its own, above.
            DateTime watchedFrom = run.Probe != null && run.Probe.StartedUtc > run.CreatedUtc ? run.Probe.StartedUtc : run.CreatedUtc;
            if (run.Probe != null && run.Probe.TaintedBetween(watchedFrom, run.EndedUtc, out IReadOnlyList<string> reasons))
            {
                _taintReasons.AddRange(reasons);
            }

            _tainted = _taintReasons.Count > 0;
            long kept = 0;
            foreach (SectionResult s in run.Sections)
            {
                // Every earlier section's table, as it was kept, lived under this one.
                _sections.Add(new SectionView(s, run, kept));
                kept += s.Table.BytesKept;
            }
            if (run.Overhead == null) BuildHypotheses();
        }

        // =============================================================================================
        // Aggregation
        // =============================================================================================

        private readonly struct Stat
        {
            public Stat(double median, double p10, double p90, double mean)
            {
                Median = median; P10 = p10; P90 = p90; Mean = mean;
            }

            public double Median { get; }
            public double P10 { get; }
            public double P90 { get; }
            public double Mean { get; }

            public static Stat Of(double[] v)
            {
                if (v.Length == 0) return new Stat(double.NaN, double.NaN, double.NaN, double.NaN);
                double[] s = (double[])v.Clone();
                Array.Sort(s);
                double sum = 0;
                foreach (double x in s) sum += x;
                return new Stat(Q(s, 0.5), Q(s, 0.1), Q(s, 0.9), sum / s.Length);
            }

            /// <summary>Linear interpolation between closest ranks.</summary>
            private static double Q(double[] sorted, double q)
            {
                double pos = q * (sorted.Length - 1);
                int lo = (int)Math.Floor(pos);
                int hi = Math.Min(sorted.Length - 1, lo + 1);
                return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
            }
        }

        private sealed class PhaseView
        {
            public Phase Phase;
            public Phase Parent;
            public double EntriesPerSeed;
            public Stat Ms;
            public double Share;
            public double AllocMean;
            public double? OtherMs;
            public double[] PerSeedMs = Array.Empty<double>();
        }

        private sealed class SectionView
        {
            public SectionView(SectionResult r, ProfileRun run, long keptBefore)
            {
                Result = r;
                KeptBeforeBytes = keptBefore;
                SeedTable t = r.Table;
                int n = t.Count;
                double f = Stopwatch.Frequency;
                SeedMs = new double[n];
                for (int i = 0; i < n; i++) SeedMs[i] = t.WallTicks[i] * 1000.0 / f;
                Seed = Stat.Of(SeedMs);
                double alloc = 0;
                for (int i = 0; i < n; i++) alloc += t.AllocBytes[i];
                AllocMean = n > 0 ? alloc / n : 0;

                bool recorded = n > 0 && t.HasPhases;
                if (recorded)
                {
                    foreach (Phase p in PhaseMap.All)
                    {
                        int id = (int)p;
                        long entries = t.EntriesSum[id];
                        double allocP = t.AllocSum[id];
                        if (entries == 0) continue;
                        double[] ms = new double[n];
                        for (int i = 0; i < n; i++) ms[i] = t.Tick(i, p) * 1000.0 / f;
                        Stat st = Stat.Of(ms);
                        Phases.Add(new PhaseView
                        {
                            Phase = p,
                            Parent = ParentIn(r.Spec.Tier, p),
                            EntriesPerSeed = entries / (double)n,
                            Ms = st,
                            Share = Seed.Mean > 0 ? st.Mean / Seed.Mean : 0,
                            AllocMean = allocP / n,
                            PerSeedMs = ms,
                        });
                    }

                    foreach (PhaseView pv in Phases)
                    {
                        double children = 0;
                        bool any = false;
                        foreach (PhaseView c in Phases)
                        {
                            if (c.Parent != pv.Phase) continue;
                            children += c.Ms.Mean;
                            any = true;
                        }

                        if (any) pv.OtherMs = pv.Ms.Mean - children;
                    }

                    double top = 0;
                    foreach (PhaseView pv in Phases)
                    {
                        if (pv.Parent == Phase.None) top += pv.Ms.Mean;
                    }

                    OtherMs = Seed.Mean - top;
                }

                if (run.CountersOn && n > 0 && t.HasCounters)
                {
                    Counters = new double[PhaseClock.Capacity];
                    for (int i = 0; i < n; i++)
                    {
                        for (int c = 0; c < PhaseClock.Capacity; c++) Counters[c] += t.Counter(i, c);
                    }

                    for (int c = 0; c < PhaseClock.Capacity; c++) Counters[c] /= n;
                }

                // A busy baseline taints every section, not only the run: the numbers of a section are what
                // a consumer reads, and a section of a tainted run must never read "tainted: false".
                if (run.Baseline != null && run.Baseline.Tainted)
                {
                    foreach (string b in run.Baseline.Tick.Reasons) TaintReasons.Add("before the run: " + b);
                }

                if (run.Probe != null && run.Probe.TaintedBetween(r.StartUtc, r.EndUtc, out IReadOnlyList<string> reasons))
                {
                    TaintReasons.AddRange(reasons);
                }

                // The steady state: every seed's start and finish, on the clock of the window's edges.
                if (n > 0 && r.EndTicks > r.StartTicks)
                {
                    long[] ends = new long[n];
                    for (int i = 0; i < n; i++) ends[i] = t.StartTicks[i] + t.WallTicks[i];
                    Steady = SteadyState.Compute(r.StartTicks, r.EndTicks, t.StartTicks, ends, t.Worker, r.WorkerCount, Stopwatch.Frequency);
                }

                // Worker CPU, measured on each worker's own thread over its share.
                bool allRead = r.Workers.Count > 0;
                double cpu = 0, share = 0;
                foreach (WorkerStat w in r.Workers)
                {
                    if (!w.Finished || !w.ThreadCpuSeconds.HasValue)
                    {
                        allRead = false;
                        break;
                    }

                    cpu += w.ThreadCpuSeconds.Value;
                    share += (w.ShareEndTicks - w.ShareStartTicks) / f;
                }

                if (allRead)
                {
                    WorkerCpuSeconds = cpu;
                    WorkerShareSeconds = share;
                }
            }

            public SteadyStateResult? Steady { get; }

            /// <summary>The profiler's tables of every earlier section, as kept, alive while this one measured.</summary>
            public long KeptBeforeBytes { get; }

            /// <summary>The workers' own thread CPU summed, when every worker's was read.</summary>
            public double? WorkerCpuSeconds { get; }

            /// <summary>The workers' shares (release to running out of work) summed, wall-clock.</summary>
            public double? WorkerShareSeconds { get; }

            public SectionResult Result { get; }
            public double[] SeedMs { get; }
            public Stat Seed { get; }
            public double AllocMean { get; }
            public List<PhaseView> Phases { get; } = new List<PhaseView>();
            public double? OtherMs { get; }
            public double[]? Counters { get; }
            public List<string> TaintReasons { get; } = new List<string>();
            public bool Tainted => TaintReasons.Count > 0;

            public PhaseView? Get(Phase p) => Phases.Find(v => v.Phase == p);

            public double SeedsPerSecond => Result.WallSeconds > 0 ? Result.Table.Count / Result.WallSeconds : 0;

            public double CpuMsPerSeed => Result.Table.Count > 0 ? Result.CpuSeconds * 1000.0 / Result.Table.Count : 0;

            public double PauseShare => Result.WallSeconds > 0 ? Result.PauseMs / (Result.WallSeconds * 1000.0) : 0;

            public bool JitFlagged => Result.JitMs > 0.01 * Result.WallSeconds * 1000.0;

            /// <summary>Per-seed ratio of the summed ticks of <paramref name="num"/> to those of <paramref name="den"/> (or the seed).</summary>
            public double[] Shares(Phase[] num, Phase? den)
            {
                SeedTable t = Result.Table;
                double[] s = new double[t.Count];
                for (int i = 0; i < s.Length; i++)
                {
                    double a = 0;
                    foreach (Phase p in num) a += t.Tick(i, p);
                    double b = den.HasValue ? t.Tick(i, den.Value) : t.WallTicks[i];
                    s[i] = b > 0 ? a / b : 0;
                }

                return s;
            }
        }

        /// <summary>
        /// Where a phase sits in a section's tree. Pre-generation's steps sit under pre-generation; the
        /// generator's construction and pre-generation sit under the oracle's construction in a t5
        /// section (the oracle builds its own generator) and directly under the seed elsewhere.
        /// </summary>
        private static Phase ParentIn(string tier, Phase p)
        {
            Phase structural = PhaseMap.Parent(p);
            if (structural != Phase.None) return structural;
            if (tier == "t5" && (p == Phase.Construct || p == Phase.Pregen)) return Phase.T5Construct;
            return Phase.None;
        }

        // =============================================================================================
        // Hypotheses
        // =============================================================================================

        private sealed class Hypothesis
        {
            public string Id = "";
            public string Prediction = "";
            public string Verdict = "";
            public string Evidence = "";
        }

        private const double Spread = 5.0;   // percentage points either side of the median

        /// <summary>
        /// Confirmed when the median per-seed share lies in [lo, hi] and the p10-p90 spread is under +-5
        /// points; refuted when it lies outside with that spread; inconclusive when the spread is wider.
        /// </summary>
        private static (string Verdict, string Numbers) Judge(double[] shares, double lo, double hi)
        {
            Stat s = Stat.Of(shares);
            string numbers = Pct(s.Median) + " (p10 " + Pct(s.P10) + ", p90 " + Pct(s.P90) + ", " + shares.Length + " seeds)";
            if ((s.P90 - s.P10) * 100.0 / 2.0 > Spread) return ("inconclusive", numbers);
            bool inside = s.Median * 100.0 >= lo && s.Median * 100.0 <= hi;
            return (inside ? "confirmed" : "refuted", numbers);
        }

        private static string Combine(IEnumerable<string> verdicts)
        {
            bool anyRefuted = false, allConfirmed = true;
            foreach (string v in verdicts)
            {
                if (v == "refuted") anyRefuted = true;
                if (v != "confirmed") allConfirmed = false;
            }

            return anyRefuted ? "refuted" : allConfirmed ? "confirmed" : "inconclusive";
        }

        private SectionView? Pick(Func<SectionView, bool> where)
        {
            SectionView? best = null;
            foreach (SectionView s in _sections)
            {
                if (s.Phases.Count == 0 || !where(s)) continue;
                if (best == null || s.Result.WorkerCount < best.Result.WorkerCount) best = s;
            }

            return best;
        }

        private void BuildHypotheses()
        {
            const string kernels = "not measured: needs the isolated per-call kernel timings, which are not built yet";

            // H1 -------------------------------------------------------------------------------------
            {
                Hypothesis h = new Hypothesis { Id = "H1", Prediction = "t5.grid >= 75 % of a T5 seed for prefix <= 22; t5.place dominates only near prefix 183" };
                SectionView? s = Pick(v => v.Result.Spec.Tier == "t5" && v.Result.PrefixRun <= 22);
                if (s == null)
                {
                    h.Verdict = "not measured";
                    h.Evidence = "needs a t5 section with a prefix of 22 or less";
                }
                else
                {
                    (h.Verdict, string numbers) = Judge(s.Shares(new[] { Phase.T5Grid }, null), 75, 100);
                    h.Evidence = "t5.grid " + numbers + " at prefix " + s.Result.PrefixRun + ", " + s.Result.WorkerCount + " worker(s)";
                    foreach (SectionView p in _sections)
                    {
                        if (p.Result.Spec.Tier != "t5" || p.Phases.Count == 0) continue;
                        PhaseView? place = p.Get(Phase.T5Place);
                        if (place != null)
                        {
                            h.Evidence += "; t5.place " + Pct(place.Share) + " at prefix " + p.Result.PrefixRun
                                          + " (" + p.Result.WorkerCount + "w)";
                        }
                    }
                }

                _hypotheses.Add(h);
            }

            // H2 -------------------------------------------------------------------------------------
            {
                Hypothesis h = new Hypothesis
                {
                    Id = "H2",
                    Prediction = "of pre-generation: stream search 45-60 %, rendering 20-35 %, river search <= 15 %, lakes <= 5 %",
                };
                SectionView? s = Pick(v => v.Result.Spec.Tier is "t3" or "t4" && v.Get(Phase.Pregen) != null);
                if (s == null)
                {
                    h.Verdict = "not measured";
                    h.Evidence = "needs a t3 or t4 section";
                }
                else
                {
                    (string v1, string n1) = Judge(s.Shares(new[] { Phase.PregenStreams1Search, Phase.PregenStreams2Search }, Phase.Pregen), 45, 60);
                    (string v2, string n2) = Judge(s.Shares(new[] { Phase.PregenRiversRender, Phase.PregenStreams1Render, Phase.PregenStreams2Render }, Phase.Pregen), 20, 35);
                    (string v3, string n3) = Judge(s.Shares(new[] { Phase.PregenRiversSearch }, Phase.Pregen), 0, 15);
                    (string v4, string n4) = Judge(s.Shares(new[] { Phase.PregenLakesScan, Phase.PregenLakesMerge }, Phase.Pregen), 0, 5);
                    h.Verdict = Combine(new[] { v1, v2, v3, v4 });
                    h.Evidence = "stream search " + n1 + " " + v1 + "; rendering " + n2 + " " + v2 + "; river search " + n3 + " " + v3
                                 + "; lakes " + n4 + " " + v4 + " (" + s.Result.Spec.Label + ", " + s.Result.WorkerCount + "w)";
                }

                _hypotheses.Add(h);
            }

            // H3 and H8: allocation per seed, then GC pause at 16 workers ----------------------------------
            AllocAndGc("H3", "pre-generation allocates >= 50 MB/seed; at 16 workers GC pause >= 5 % of wall for T3/T4",
                       v => v.Result.Spec.Tier is "t3" or "t4", Phase.Pregen, 50);
            AllocAndGc("H8", "BiomeField allocates >= 40 MB/seed; GC >= 5 % of T5 wall at 16 workers",
                       v => v.Result.Spec.Tier == "t5", Phase.T5Field, 40);

            _hypotheses.Add(new Hypothesis { Id = "H4", Prediction = "libm (Atan2 + Sin) >= 20 % of GetBiome + final GetBiomeHeight", Verdict = "not measured", Evidence = kernels + CounterNote(Counter.WorldAngle) });

            // H5 -------------------------------------------------------------------------------------
            {
                Hypothesis h = new Hypothesis { Id = "H5", Prediction = "T3's second GetBaseHeight per cell (a memo miss) is ~12 % of T3 at G12", Verdict = "not measured" };
                SectionView? s = Pick(v => v.Result.Spec.Tier == "t3" && v.Result.Spec.Grid == 12 && v.Counters != null);
                h.Evidence = kernels;
                if (s != null)
                {
                    double bh = s.Counters![(int)Counter.BaseHeight], hit = s.Counters[(int)Counter.BaseHeightMemoHit];
                    h.Evidence += "; counted at t3 G12: " + Out.F(bh, 0) + " base heights per seed, "
                                  + Pct(bh > 0 ? hit / bh : 0) + " answered by the memo";
                }

                _hypotheses.Add(h);
            }

            _hypotheses.Add(new Hypothesis { Id = "H6", Prediction = "Perlin >= 35 % of the grid path after the exact short-cuts", Verdict = "not measured", Evidence = kernels });
            _hypotheses.Add(new Hypothesis
            {
                Id = "H7",
                Prediction = "queries with T3/T4 and T5 goals pay pre-generation twice",
                Verdict = "not measured",
                Evidence = "vseed profile does not run the search evaluator, where the second pre-generation would happen",
            });
            _hypotheses.Add(new Hypothesis { Id = "H9", Prediction = "on this CPU a VBMI Noise16 beats two Noise8", Verdict = "not measured", Evidence = kernels });

            // H10 ------------------------------------------------------------------------------------
            {
                Hypothesis h = new Hypothesis { Id = "H10", Prediction = "16-worker CPU ms/seed exceeds 1-worker by ~30 % (SMT), not by contention", Verdict = "not measured" };
                foreach (SectionView one in _sections)
                {
                    if (one.Result.WorkerCount != 1) continue;
                    SectionView? sixteen = _sections.Find(v => v.Result.WorkerCount == 16 && v.Result.Spec.Label == one.Result.Spec.Label);
                    if (sixteen == null || one.CpuMsPerSeed <= 0) continue;
                    double ratio = sixteen.CpuMsPerSeed / one.CpuMsPerSeed;
                    h.Verdict = ratio >= 1.15 && ratio <= 1.45 ? "confirmed" : "refuted";
                    h.Evidence = one.Result.Spec.Label + ": " + Out.F(one.CpuMsPerSeed, 1) + " CPU ms/seed at 1 worker, "
                                 + Out.F(sixteen.CpuMsPerSeed, 1) + " at 16 (x" + Out.F(ratio, 2) + "; predicted x1.15-1.45)";
                    SectionView? eight = _sections.Find(v => v.Result.WorkerCount == 8 && v.Result.Spec.Label == one.Result.Spec.Label);
                    if (eight != null) h.Evidence += ", " + Out.F(eight.CpuMsPerSeed, 1) + " at 8";

                    // Sized per worker count (--saturate), the sections cover different numbers of the same
                    // seed order, and seed costs vary: say so rather than imply like for like.
                    int n1 = one.Result.Table.Count, n16 = sixteen.Result.Table.Count;
                    if (n1 != n16 || (eight != null && eight.Result.Table.Count != n1))
                    {
                        h.Evidence += " - over different seed counts (1 worker: " + n1 + " seeds; "
                                      + (eight != null ? "8 workers: " + eight.Result.Table.Count + "; " : "")
                                      + "16 workers: " + n16 + "; the first " + Math.Min(n1, Math.Min(n16, eight?.Result.Table.Count ?? n16))
                                      + " in common), so part of the difference can be the seeds";
                    }

                    break;
                }

                if (h.Verdict == "not measured") h.Evidence = "needs the same section at 1 and 16 workers (--threads 1,16)";
                _hypotheses.Add(h);
            }

            _hypotheses.Sort((a, b) => int.Parse(a.Id.Substring(1), CultureInfo.InvariantCulture)
                                           .CompareTo(int.Parse(b.Id.Substring(1), CultureInfo.InvariantCulture)));
        }

        private string CounterNote(Counter c)
        {
            foreach (SectionView s in _sections)
            {
                if (s.Counters == null) continue;
                return "; counted: " + Out.F(s.Counters[(int)c], 0) + " " + PhaseClock.Name(c) + " per seed in " + s.Result.Spec.Label;
            }

            return "";
        }

        private void AllocAndGc(string id, string prediction, Func<SectionView, bool> where, Phase phase, double mb)
        {
            Hypothesis h = new Hypothesis { Id = id, Prediction = prediction };
            SectionView? s = Pick(v => where(v) && v.Get(phase) != null);
            if (s == null)
            {
                h.Verdict = "not measured";
                h.Evidence = "needs a section that enters " + PhaseMap.Name(phase);
                _hypotheses.Add(h);
                return;
            }

            PhaseView pv = s.Get(phase)!;
            double perEntry = pv.AllocMean / Math.Max(1e-9, pv.EntriesPerSeed) / 1e6;
            string a = perEntry >= mb ? "confirmed" : "refuted";
            h.Evidence = PhaseMap.Name(phase) + " allocates " + Out.F(perEntry, 1) + " MB (10^6 bytes) per entry (" + a + ")";
            SectionView? sixteen = _sections.Find(v => where(v) && v.Result.WorkerCount == 16 && v.Phases.Count > 0);
            if (sixteen == null)
            {
                h.Verdict = "inconclusive";
                h.Evidence += "; the GC half needs the same tier at 16 workers (--threads 1,16)";
            }
            else
            {
                string g = sixteen.PauseShare >= 0.05 ? "confirmed" : "refuted";
                h.Evidence += "; GC pause " + Pct(sixteen.PauseShare) + " of wall at 16 workers (" + g + ")";
                h.Verdict = Combine(new[] { a, g });
            }

            _hypotheses.Add(h);
        }

        // =============================================================================================
        // Machine and build
        // =============================================================================================

        /// <summary>
        /// The machine block: CPU identity, what the runtime lets this process use, the libm the process
        /// loaded, the GC and JIT settings, and the clock. Read, never assumed.
        /// </summary>
        public static Dictionary<string, object?> Machine(CliRuntime rt)
        {
            Dictionary<string, object?> m = new Dictionary<string, object?>();
            m["cpu"] = CpuIdentity(rt.Context.Hardware.Cpu);
            m["isa"] = new Dictionary<string, object?>
            {
                ["sse42"] = Sse42.IsSupported,
                ["avx"] = Avx.IsSupported,
                ["avx2"] = Avx2.IsSupported,
                ["fma"] = Fma.IsSupported,
                ["bmi2"] = Bmi2.IsSupported,
                ["avx512f"] = Avx512F.IsSupported,
                ["avx512bw"] = Avx512BW.IsSupported,
                ["avx512vbmi"] = Avx512Vbmi.IsSupported,
                ["avx10v1"] = Avx10v1.IsSupported,
                ["vector128_accelerated"] = Vector128.IsHardwareAccelerated,
                ["vector256_accelerated"] = Vector256.IsHardwareAccelerated,
                ["vector512_accelerated"] = Vector512.IsHardwareAccelerated,
                ["vector_t_bytes"] = System.Numerics.Vector<byte>.Count,
            };
            m["simd"] = new Dictionary<string, object?>
            {
                ["active"] = SeedLab.WorldGen.Simd.SimdDispatch.Name(SeedLab.WorldGen.Simd.SimdDispatch.Active),
                ["hardware"] = SeedLab.WorldGen.Simd.SimdDispatch.Name(SeedLab.WorldGen.Simd.SimdDispatch.Hardware),
                ["requested"] = SeedLab.WorldGen.Simd.SimdDispatch.Name(SeedLab.WorldGen.Simd.SimdDispatch.Requested),
                ["key"] = SeedLab.WorldGen.Simd.SimdDispatch.Key,
                ["reason"] = SeedLab.WorldGen.Simd.SimdDispatch.Reason,
                ["perlin_8wide"] = PerlinFast.Use8Wide,
            };
            Dictionary<string, object?> knobs = new Dictionary<string, object?>();
            foreach (string k in new[] { "DOTNET_EnableHWIntrinsic", "DOTNET_EnableAVX2", "DOTNET_EnableAVX512", "DOTNET_EnableAVX512v2",
                                         "DOTNET_EnableAVX512v3", "DOTNET_EnableAVX10v1", "DOTNET_PreferredVectorBitWidth",
                                         "DOTNET_TieredPGO", "DOTNET_TieredCompilation", "DOTNET_TC_QuickJitForLoops", "DOTNET_ReadyToRun",
                                         "DOTNET_gcServer", "DOTNET_GCgen0size", "DOTNET_gcConcurrent", "DOTNET_GCDynamicAdaptationMode",
                                         "DOTNET_GCHeapCount", "DOTNET_GCHeapAffinitizeMask", "DOTNET_GCConserveMemory",
                                         "DOTNET_GCHeapHardLimit", PhaseClock.EnvironmentVariable,
                                         SeedLab.WorldGen.Simd.SimdDispatch.EnvironmentVariable })
            {
                string? v = Environment.GetEnvironmentVariable(k);
                if (v != null) knobs[k] = v;
            }

            m["knobs_set"] = knobs;
            m["gc_config"] = GcConfig();
            m["ucrtbase"] = rt.Context.Hardware.UcrtVersion;

            m["logical_cores"] = Environment.ProcessorCount;
            m["memory_bytes"] = rt.Context.Hardware.TotalMemoryBytes;
            m["os"] = RuntimeInformation.OSDescription;
            m["runtime"] = RuntimeInformation.FrameworkDescription;
            m["arch"] = RuntimeInformation.ProcessArchitecture.ToString();
            m["gc"] = GCSettings.IsServerGC ? "server" : "workstation";
            m["gc_concurrent"] = AppContext.GetData("System.GC.Concurrent")?.ToString();
            m["tiered_compilation"] = AppContext.GetData("System.Runtime.TieredCompilation")?.ToString() ?? "default (on)";
            m["tiered_pgo"] = AppContext.GetData("System.Runtime.TieredPGO")?.ToString() ?? "default";
            m["qpc_frequency"] = Stopwatch.Frequency;
            m["timestamp_ns"] = Math.Round(TimestampNs(), 2);
            m["mode"] = ResourceModes.Name(rt.Mode);
            m["self_test"] = rt.SelfTest?.Status.ToString();
            return m;
        }

        /// <summary>
        /// The garbage collector's own configuration as it reports it at the start of the run (heap
        /// count, dynamic adaptation, server or workstation, concurrency, limits) - read, never assumed.
        /// Null when this runtime does not report it.
        /// </summary>
        private static Dictionary<string, object?>? GcConfig()
        {
            try
            {
                Dictionary<string, object?> d = new Dictionary<string, object?>();
                List<string> keys = new List<string>(GC.GetConfigurationVariables().Keys);
                keys.Sort(StringComparer.Ordinal);
                IReadOnlyDictionary<string, object> v = GC.GetConfigurationVariables();
                foreach (string k in keys)
                {
                    object x = v[k];
                    d[k] = x switch
                    {
                        bool b => b,
                        int i => i,
                        long l => l,
                        ulong u when u <= long.MaxValue => (long)u,
                        _ => Convert.ToString(x, CultureInfo.InvariantCulture),
                    };
                }

                return d;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The runtime layer's CPUID reading, as the profile schema's fields. Null when none was possible.</summary>
        private static Dictionary<string, object?>? CpuIdentity(SeedLab.Runtime.Hardware.CpuIdentity cpu)
        {
            if (cpu.Source == "unavailable") return null;
            return new Dictionary<string, object?>
            {
                ["vendor"] = cpu.Vendor,
                ["family"] = cpu.Family,
                ["model"] = cpu.Model,
                ["stepping"] = cpu.Stepping,
                ["brand"] = cpu.Brand.Length > 0 ? cpu.Brand : null,
                ["hybrid"] = cpu.Hybrid,
                ["source"] = cpu.Source,
            };
        }

        /// <summary>What one <see cref="Stopwatch.GetTimestamp"/> costs here, in ns, over a warmed loop.</summary>
        public static double TimestampNs()
        {
            long sink = 0;
            for (int i = 0; i < 100_000; i++) sink += Stopwatch.GetTimestamp();
            const int n = 2_000_000;
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < n; i++) sink += Stopwatch.GetTimestamp();
            long t1 = Stopwatch.GetTimestamp();
            GC.KeepAlive(sink);
            return (t1 - t0) * 1e9 / Stopwatch.Frequency / n;
        }

        public static Dictionary<string, object?> Build(DumpedLocationOracle? oracle)
        {
            string dir = AppContext.BaseDirectory;
            Dictionary<string, object?> b = new Dictionary<string, object?>
            {
                ["engine"] = Verified.EngineVersion,
                ["vseed_sha256"] = Sha256OfFile(Path.Combine(dir, "vseed.dll")),
                ["worldgen_sha256"] = Sha256OfFile(Path.Combine(dir, "SeedLab.WorldGen.dll")),
                ["locations_sha256"] = Sha256OfFile(Path.Combine(dir, "SeedLab.Locations.dll")),
            };
            if (oracle != null) b["data"] = oracle.Provenance;
            return b;
        }

        public static string? Sha256OfFile(string path)
        {
            try
            {
                using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
            }
            catch (Exception)
            {
                return null;
            }
        }

        // =============================================================================================
        // The document
        // =============================================================================================

        private Dictionary<string, object?> Document()
        {
            Dictionary<string, object?> d = new Dictionary<string, object?>
            {
                ["schema"] = Schema,
                ["created_utc"] = Utc(_run.CreatedUtc),
                ["ended_utc"] = Utc(_run.EndedUtc),
                ["build"] = _run.Build,
                ["machine"] = _run.Machine,
            };

            List<object?> first = new List<object?>();
            for (int i = 0; i < 8; i++) first.Add(PilotSeedOrder.Plan(_run.Key).SeedAt((_run.From + i) % 4294967296L));
            d["run"] = new Dictionary<string, object?>
            {
                ["tier"] = _run.TierText,
                ["seed_list"] = "shuffled whole int32 range, key 0x" + _run.Key.ToString("X16", CultureInfo.InvariantCulture)
                                + ", from index " + _run.From.ToString(CultureInfo.InvariantCulture),
                ["key"] = "0x" + _run.Key.ToString("X16", CultureInfo.InvariantCulture),
                ["from_index"] = _run.From,
                ["first_seeds"] = first,
                ["worker_counts"] = _run.WorkerCounts.ConvertAll(w => (object?)w),
                ["warmup_per_worker"] = _run.Warmup,
                ["counters"] = _run.CountersOn,
                ["warmup_seeds_note"] = "each worker's warm-up seeds are drawn from the indices after the measured ones and never measured",
            };
            Dictionary<string, object?> runBlock = (Dictionary<string, object?>)d["run"]!;
            if (_run.SaturateSeconds.HasValue)
            {
                List<object?> pilots = new List<object?>();
                foreach (SaturationPilot p in _run.Pilots)
                {
                    pilots.Add(new Dictionary<string, object?>
                    {
                        ["section_index"] = p.SectionIndex,
                        ["section"] = p.Label,
                        ["workers"] = p.Workers,
                        ["pilot_runs"] = p.Sizing.PilotRuns?.ConvertAll(x => (object?)x),
                        ["pilot_seeds"] = p.Sizing.PilotSeeds,
                        ["pilot_s"] = p.Sizing.PilotSeconds.HasValue ? Math.Round(p.Sizing.PilotSeconds.Value, 3) : null,
                        ["pilot_mean_seed_ms"] = p.Sizing.PilotMeanSeedMs.HasValue ? R(p.Sizing.PilotMeanSeedMs.Value) : null,
                        ["rate_estimate_seeds_per_second"] = p.Sizing.RateEstimate.HasValue ? R(p.Sizing.RateEstimate.Value) : null,
                        ["chosen_seeds"] = p.Seeds,
                        ["capped"] = p.Sizing.Capped ? true : null,
                        ["min_per_worker_decided"] = p.Sizing.FloorDecided ? true : null,
                        ["estimated_s"] = p.Sizing.EstimatedSeconds.HasValue ? R(p.Sizing.EstimatedSeconds.Value) : null,
                    });
                }

                runBlock["saturate"] = new Dictionary<string, object?>
                {
                    ["target_s"] = _run.SaturateSeconds.Value,
                    ["min_seeds_per_worker"] = SaturationPlanner.MinPerWorker,
                    ["min_pilot_s"] = SaturationPlanner.MinPilotSeconds(_run.SaturateSeconds.Value),
                    ["rule"] = "seeds = max(workers x min_seeds_per_worker, ceil(rate x target_s)) rounded up to a multiple of the worker count; "
                               + "rate = workers x 1000 / the mean seed ms of the later half of the last pilot run; a pilot grows run by run "
                               + "(pilot_runs) until one has worked min_pilot_s; the pilots are not measurements",
                    ["estimated_total_s"] = _run.EstimatedSeconds.HasValue ? R(_run.EstimatedSeconds.Value) : null,
                    ["pilots"] = pilots,
                };
            }

            if (_run.PlanFile != null)
            {
                List<object?> replayed = new List<object?>();
                foreach (ReplayedPilot p in _run.ReplayedPilots)
                {
                    replayed.Add(new Dictionary<string, object?>
                    {
                        ["section_index"] = p.SectionIndex,
                        ["section"] = p.Label,
                        ["workers"] = p.Workers,
                        ["pilot_runs"] = p.Runs.ConvertAll(x => (object?)x),
                        ["pilot_s"] = Math.Round(p.Seconds, 3),
                    });
                }

                List<object?> differences = new List<object?>();
                foreach (PlanDifference x in _run.PlanDifferences)
                {
                    differences.Add(new Dictionary<string, object?> { ["what"] = x.What, ["plan"] = x.Plan, ["now"] = x.Now, ["note"] = x.Note });
                }

                runBlock["plan"] = new Dictionary<string, object?>
                {
                    ["file"] = _run.PlanFile,
                    ["sha256"] = _run.PlanSha256,
                    ["note"] = "the sections, worker counts, seed counts, seed order and warm-up were replayed from this profile, after "
                               + "its pilots were run again (uncounted), so the measurements started from the same history of work",
                    ["pilots"] = replayed,
                    ["differences"] = differences,
                };
            }

            d["hygiene"] = Hygiene();

            List<object?> sections = new List<object?>();
            foreach (SectionView s in _sections) sections.Add(SectionDoc(s));
            if (sections.Count > 0) d["sections"] = sections;

            if (_hypotheses.Count > 0)
            {
                List<object?> hs = new List<object?>();
                foreach (Hypothesis h in _hypotheses)
                {
                    hs.Add(new Dictionary<string, object?>
                    {
                        ["id"] = h.Id,
                        ["prediction"] = h.Prediction,
                        ["verdict"] = h.Verdict,
                        ["evidence"] = h.Evidence,
                        ["tainted"] = _tainted ? true : null,
                    });
                }

                d["hypotheses"] = hs;
            }

            if (_run.Overhead != null) d["overhead"] = OverheadDoc(_run.Overhead);
            List<object?> notMeasured = new List<object?>
            {
                "isolated per-call kernel timings (the attribution of phase time to Perlin, libm and river lookups)",
                "per-location-type placement costs inside t5.place",
                "the search evaluator's own phases (collect, seedtext); vseed profile runs its own loop",
            };
            if (_sections.Exists(s => s.Result.Resources && s.Result.Io == null))
                notMeasured.Add("the process's input and output: this operating system gave no reading");
            if (_sections.Exists(s => s.Result.Resources && s.WorkerCpuSeconds == null))
                notMeasured.Add("the workers' own thread CPU: this operating system gave no reading, so no split of the process's CPU");
            if (_sections.Exists(s => s.Result.Resources))
                notMeasured.Add("the number of garbage-collector heaps in use: with dynamic adaptation (DATAS) it moves between 1 and gc.heap_count_max, "
                                + "and only the maximum is read");
            d["not_measured"] = notMeasured;
            d["disk_note"] = "vseed profile writes nothing while it measures: --out (and --per-seed's CSV) is written once, after the last "
                             + "section, and vseed's own session log is kept in the cache root. Each section's io block counts every read "
                             + "and write call the process made in its window (on Windows: files, pipes and the console)";
            return d;
        }

        private Dictionary<string, object?> Hygiene()
        {
            Dictionary<string, object?> h = new Dictionary<string, object?>
            {
                ["tainted"] = _tainted,
                ["reasons"] = _taintReasons.ConvertAll(r => (object?)r),
                ["method"] = "per-process TotalProcessorTime deltas, every " + (_run.Probe?.Interval.TotalSeconds ?? 5).ToString(CultureInfo.InvariantCulture)
                             + " s, excluding this process, Idle and its own timing legs; and the whole machine's busy time less this "
                             + "process and its legs, which also counts the processes whose own time cannot be read; the larger decides",
            };
            if (_run.Baseline != null)
            {
                QuietBaseline b = _run.Baseline;
                h["baseline"] = new Dictionary<string, object?>
                {
                    ["seconds"] = Math.Round(b.Seconds, 1),
                    ["foreign_core_seconds"] = Math.Round(b.ForeignCoreSeconds, 2),
                    ["foreign_cores"] = Math.Round(b.ForeignCores, 3),
                    ["process_core_seconds"] = Math.Round(b.Tick.ProcessCoreSeconds, 2),
                    ["machine_core_seconds"] = b.Tick.MachineCoreSeconds.HasValue ? Math.Round(b.Tick.MachineCoreSeconds.Value, 2) : null,
                    ["processes"] = b.Tick.Processes,
                    ["unreadable"] = b.Tick.Unreadable,
                    ["tainted"] = b.Tainted,
                    ["reasons"] = new List<string>(b.Tick.Reasons).ConvertAll(r => (object?)r),
                    ["raises_limit"] = _run.Probe?.BaselineRaisesLimit,
                    ["top"] = new List<ProcessCpu>(b.Tick.Top).ConvertAll(p => (object?)p.ToString()),
                };
            }

            if (_run.Probe != null)
            {
                QuietThresholds t = _run.Probe.Thresholds;
                h["thresholds"] = new Dictionary<string, object?>
                {
                    ["foreign_cores_max"] = t.ForeignCoresMax,
                    ["baseline_multiple"] = t.BaselineMultiple,
                    ["dev_tool_cores_max"] = t.DevToolCoresMax,
                    ["min_sample_seconds"] = t.MinTickSeconds,
                    ["limit_cores"] = Math.Round(_run.Probe.LimitCores, 3),
                };
                IReadOnlyList<ProbeTick> ticks = _run.Probe.Ticks;
                int tainted = 0, unreadableMax = 0;
                double foreign = 0, seconds = 0, max = 0, machine = 0, machineSeconds = 0;
                List<object?> detail = new List<object?>();
                foreach (ProbeTick k in ticks)
                {
                    if (k.Tainted) tainted++;
                    foreign += k.ForeignCoreSeconds;
                    seconds += k.Seconds;
                    max = Math.Max(max, k.ForeignCores);
                    unreadableMax = Math.Max(unreadableMax, k.Unreadable);
                    if (k.MachineCoreSeconds.HasValue)
                    {
                        machine += k.MachineCoreSeconds.Value;
                        machineSeconds += k.Seconds;
                    }

                    detail.Add(new Dictionary<string, object?>
                    {
                        ["t_s"] = Math.Round((k.StartUtc - _run.Probe.StartedUtc).TotalSeconds, 3),
                        ["seconds"] = Math.Round(k.Seconds, 3),
                        ["foreign_cores"] = Math.Round(k.ForeignCores, 3),
                        ["process_cores"] = k.Seconds > 0 ? Math.Round(k.ProcessCoreSeconds / k.Seconds, 3) : null,
                        ["machine_cores"] = k.MachineCoreSeconds.HasValue && k.Seconds > 0 ? Math.Round(k.MachineCoreSeconds.Value / k.Seconds, 3) : null,
                        ["processes"] = k.Processes,
                        ["unreadable"] = k.Unreadable,
                        ["tainted"] = k.Tainted,
                    });
                }

                h["whole_machine"] = _run.Probe.SeesWholeMachine;
                h["samples"] = ticks.Count;
                h["samples_tainted"] = tainted;
                h["foreign_cores_mean"] = seconds > 0 ? Math.Round(foreign / seconds, 3) : null;
                h["foreign_cores_max_sample"] = ticks.Count > 0 ? Math.Round(max, 3) : null;
                h["machine_foreign_cores_mean"] = machineSeconds > 0 ? Math.Round(machine / machineSeconds, 3) : null;
                h["unreadable_max"] = unreadableMax;
                h["watched_seen"] = new List<string>(_run.Probe.WatchedSeen()).ConvertAll(s => (object?)s);
                h["samples_detail"] = detail;
            }

            return h;
        }

        private Dictionary<string, object?> SectionDoc(SectionView s)
        {
            SectionResult r = s.Result;
            Dictionary<string, object?> d = new Dictionary<string, object?>
            {
                ["tier"] = r.Spec.Tier,
                ["grid_m"] = r.Spec.Grid > 0 ? r.Spec.Grid : null,
                ["prefix"] = r.Spec.Tier == "t5" ? r.PrefixRun : null,
                ["work"] = ProfileCommand.DescribeWork(r.Spec.Tier),
                ["workers"] = r.WorkerCount,
                ["seeds"] = r.Table.Count,
                ["warmup_per_worker"] = r.Warmup,
                ["wall_s"] = Math.Round(r.WallSeconds, 4),
                ["seeds_per_second"] = Math.Round(s.SeedsPerSecond, 3),
                ["cpu_ms_per_seed"] = Math.Round(s.CpuMsPerSeed, 3),
                ["seed_ms"] = StatDoc(s.Seed),
                ["alloc_bytes_mean"] = Math.Round(s.AllocMean),
                ["tainted"] = s.Tainted,
                ["taint_reasons"] = s.Tainted ? s.TaintReasons.ConvertAll(x => (object?)x) : null,
                ["prefix_requested"] = r.Spec.Tier == "t5" ? r.Spec.Prefix : null,
                ["first_index"] = r.From,
                ["seed_list_sha256"] = SeedListDigest.Of(r.Seeds),
                ["sizing"] = SizingDoc(r.Sizing),
            };

            if (s.Phases.Count > 0)
            {
                List<object?> ph = new List<object?>();
                foreach (PhaseView p in Ordered(s))
                {
                    Dictionary<string, object?> pd = new Dictionary<string, object?>
                    {
                        ["id"] = (int)p.Phase,
                        ["name"] = PhaseMap.Name(p.Phase),
                        ["parent"] = PhaseMap.Name(p.Parent),
                        ["entries_per_seed"] = Math.Round(p.EntriesPerSeed, 4),
                        ["ms_median"] = R(p.Ms.Median),
                        ["ms_p10"] = R(p.Ms.P10),
                        ["ms_p90"] = R(p.Ms.P90),
                        ["ms_mean"] = R(p.Ms.Mean),
                        ["share_of_seed"] = Math.Round(p.Share, 5),
                        ["alloc_bytes_mean"] = Math.Round(p.AllocMean),
                        ["other_ms_mean"] = p.OtherMs.HasValue ? R(p.OtherMs.Value) : null,
                    };
                    ph.Add(pd);
                }

                d["phases"] = ph;
                d["other_ms_mean"] = s.OtherMs.HasValue ? R(s.OtherMs.Value) : null;
            }

            if (s.Counters != null)
            {
                Dictionary<string, object?> c = new Dictionary<string, object?>();
                foreach (Counter k in PhaseClock.All)
                {
                    c[PhaseClock.Name(k)] = new Dictionary<string, object?> { ["per_seed_mean"] = Math.Round(s.Counters[(int)k], 2) };
                }

                d["counters"] = c;
            }

            Dictionary<string, object?> gc = new Dictionary<string, object?>
            {
                ["gen0"] = r.Gen0,
                ["gen1"] = r.Gen1,
                ["gen2"] = r.Gen2,
                ["pause_ms"] = Math.Round(r.PauseMs, 3),
                ["pause_share"] = Math.Round(s.PauseShare, 5),
            };
            if (r.Resources)
            {
                gc["heap_count_max"] = r.GcHeapCountMax;
                gc["heap_count_note"] = r.GcHeapCountMax.HasValue
                    ? "the collector's configured maximum (GC.GetConfigurationVariables HeapCount), not the heaps in use: with dynamic "
                      + "adaptation (DATAS) that moves between 1 and this, and is not measured"
                    : null;
                gc["last_gc"] = LastGcDoc(r.LastGc, r.GcCountAtStart);
                if (r.LastFullGc.Index > 0)
                {
                    gc["last_full_gc"] = new Dictionary<string, object?>
                    {
                        ["index"] = r.LastFullGc.Index,
                        ["in_window"] = r.LastFullGc.Index > r.GcCountAtStart,
                        ["heap_size_bytes"] = r.LastFullGc.HeapSizeBytes,
                        ["promoted_bytes"] = r.LastFullGc.PromotedBytes,
                    };
                }
            }

            d["gc"] = gc;
            d["jit"] = new Dictionary<string, object?>
            {
                ["methods_in_window"] = r.JitMethods,
                ["compile_ms_in_window"] = Math.Round(r.JitMs, 3),
                ["over_1_percent"] = s.JitFlagged,
            };

            if (r.Resources)
            {
                d["cpu"] = CpuDoc(s);
                d["memory"] = MemoryDoc(s);
                d["io"] = IoDoc(r.Io);
            }

            d["steady_state"] = SteadyDoc(s.Steady);

            List<object?> ws = new List<object?>();
            double f = Stopwatch.Frequency;
            foreach (WorkerStat w in r.Workers)
            {
                double shareS = w.Finished ? (w.ShareEndTicks - w.ShareStartTicks) / f : double.NaN;
                ws.Add(new Dictionary<string, object?>
                {
                    ["id"] = w.Id,
                    ["seeds"] = w.Seeds,
                    ["busy_s"] = Math.Round(w.BusyTicks / (double)Stopwatch.Frequency, 4),
                    ["share_s"] = w.Finished ? Math.Round(shareS, 4) : null,
                    ["cpu_s"] = w.ThreadCpuSeconds.HasValue ? Math.Round(w.ThreadCpuSeconds.Value, 4) : null,
                    ["not_running_s"] = w.Finished && w.ThreadCpuSeconds.HasValue ? Math.Round(shareS - w.ThreadCpuSeconds.Value, 4) : null,
                    ["alloc_bytes"] = w.Finished && r.Resources ? w.ThreadAllocBytes : null,
                    ["first_start_s"] = w.Seeds > 0 ? Math.Round((w.FirstStartTicks - r.StartTicks) / f, 4) : null,
                    ["last_end_s"] = w.Seeds > 0 ? Math.Round((w.LastEndTicks - r.StartTicks) / f, 4) : null,
                });
            }

            d["worker_stats"] = ws;
            return d;
        }

        private static Dictionary<string, object?>? SizingDoc(SectionSizing? z)
        {
            if (z == null) return null;
            return new Dictionary<string, object?>
            {
                ["source"] = z.Source,
                ["plan_file"] = z.PlanFile,
                ["target_s"] = z.TargetSeconds,
                ["min_seeds_per_worker"] = z.MinPerWorker,
                ["pilot_runs"] = z.PilotRuns?.ConvertAll(x => (object?)x),
                ["pilot_seeds"] = z.PilotSeeds,
                ["pilot_s"] = z.PilotSeconds.HasValue ? Math.Round(z.PilotSeconds.Value, 3) : null,
                ["pilot_mean_seed_ms"] = z.PilotMeanSeedMs.HasValue ? R(z.PilotMeanSeedMs.Value) : null,
                ["rate_estimate_seeds_per_second"] = z.RateEstimate.HasValue ? R(z.RateEstimate.Value) : null,
                ["capped"] = z.Capped ? true : null,
                ["min_per_worker_decided"] = z.FloorDecided ? true : null,
                ["estimated_s"] = z.EstimatedSeconds.HasValue ? R(z.EstimatedSeconds.Value) : null,
                ["recorded_s"] = z.RecordedSeconds.HasValue ? R(z.RecordedSeconds.Value) : null,
            };
        }

        /// <summary>The process's CPU over the window, and how much of it the workers themselves used.</summary>
        private static Dictionary<string, object?> CpuDoc(SectionView s)
        {
            SectionResult r = s.Result;
            int cores = Math.Max(1, Environment.ProcessorCount);
            double process = r.CpuUserSeconds.HasValue && r.CpuKernelSeconds.HasValue ? r.CpuUserSeconds.Value + r.CpuKernelSeconds.Value : r.CpuSeconds;
            double? sampler = r.Memory?.SamplerCpu?.TotalSeconds;
            Dictionary<string, object?> c = new Dictionary<string, object?>
            {
                ["process_s"] = Math.Round(process, 4),
                ["user_s"] = r.CpuUserSeconds.HasValue ? Math.Round(r.CpuUserSeconds.Value, 4) : null,
                ["kernel_s"] = r.CpuKernelSeconds.HasValue ? Math.Round(r.CpuKernelSeconds.Value, 4) : null,
                ["logical_cores"] = cores,
                ["busy_cores"] = r.WallSeconds > 0 ? Math.Round(process / r.WallSeconds, 3) : null,
                ["utilisation"] = r.WallSeconds > 0 ? Math.Round(process / (r.WallSeconds * cores), 4) : null,
                ["workers_s"] = s.WorkerCpuSeconds.HasValue ? Math.Round(s.WorkerCpuSeconds.Value, 4) : null,
                ["workers_share_s"] = s.WorkerShareSeconds.HasValue ? Math.Round(s.WorkerShareSeconds.Value, 4) : null,
                ["workers_not_running_s"] = s.WorkerCpuSeconds.HasValue && s.WorkerShareSeconds.HasValue
                    ? Math.Round(s.WorkerShareSeconds.Value - s.WorkerCpuSeconds.Value, 4) : null,
                ["sampler_s"] = sampler.HasValue ? Math.Round(sampler.Value, 4) : null,
                ["other_s"] = s.WorkerCpuSeconds.HasValue ? Math.Round(process - s.WorkerCpuSeconds.Value - (sampler ?? 0), 4) : null,
                ["thread_cpu_source"] = ProcessResources.ThreadCpuSource,
                ["note"] = "other_s is the process's CPU less the workers' own and the memory sampler's: the garbage collector's threads under "
                           + "server GC, the JIT, the quiet-machine probe (every 5 s) and the runtime. Under workstation GC a collection runs on "
                           + "the worker that triggered it and lands in workers_s instead. workers_not_running_s is the time the workers' shares "
                           + "spent off a processor: waiting for a collection, preempted, or stalled on a page fault. Thread and process times "
                           + "advance in steps of about 15.6 ms on Windows, so a sampler_s of 0 means below that step, not free",
            };
            return c;
        }

        /// <summary>Memory: the sampler's peaks and means, the process's lifetime peaks, and what was allocated.</summary>
        private static Dictionary<string, object?> MemoryDoc(SectionView s)
        {
            SectionResult r = s.Result;
            double seedAlloc = 0;
            for (int i = 0; i < r.Table.Count; i++) seedAlloc += r.Table.AllocBytes[i];
            Dictionary<string, object?> m = new Dictionary<string, object?>
            {
                ["allocated_bytes"] = r.AllocatedBytes,
                ["allocated_bytes_per_seed"] = r.Table.Count > 0 ? Math.Round((double)r.AllocatedBytes / r.Table.Count) : null,
                ["seed_work_allocated_bytes"] = Math.Round(seedAlloc),
                ["allocation_note"] = "allocated_bytes is the whole process in the window; seed_work_allocated_bytes sums each measured seed's own; "
                                      + "the difference is the sampler, the probe and the runtime (the profiler's per-seed table is allocated before "
                                      + "the window)",
                ["profiler_table"] = new Dictionary<string, object?>
                {
                    ["while_measuring_bytes"] = r.Table.BytesWhileMeasured,
                    ["kept_bytes"] = r.Table.BytesKept,
                    ["earlier_sections_kept_bytes"] = s.KeptBeforeBytes,
                    ["note"] = "the profile's own per-seed figures: this section's table (no object per seed, allocated before the window) "
                               + "and every earlier section's table as kept after it ended, cut to the phases it entered. Both were alive while "
                               + "this section measured, so they are part of its memory figures",
                },
            };
            if (r.Memory != null)
            {
                ResourceSummary z = r.Memory;
                m["sampler"] = new Dictionary<string, object?>
                {
                    ["interval_ms"] = Math.Round(z.Interval.TotalMilliseconds),
                    ["samples"] = z.Samples,
                    ["seconds"] = Math.Round(z.Seconds, 3),
                    ["working_set_bytes"] = SeriesDoc(z.WorkingSet),
                    ["private_bytes"] = SeriesDoc(z.PrivateBytes),
                    ["gc_heap_bytes"] = SeriesDoc(z.GcHeap),
                    ["gc_committed_bytes"] = SeriesDoc(z.GcCommitted),
                    ["note"] = "gc_heap_bytes is the collector's live estimate without collecting; gc_committed_bytes is as of the last collection",
                };
            }

            if (r.MemoryEnd != null)
            {
                m["process_lifetime"] = new Dictionary<string, object?>
                {
                    ["peak_working_set_bytes"] = r.MemoryEnd.PeakWorkingSet,
                    ["peak_private_bytes"] = r.MemoryEnd.PeakPrivateBytes,
                    ["page_faults"] = r.MemoryEnd.PageFaults,
                    ["source"] = r.MemoryEnd.Source,
                    ["note"] = "the highest since vseed started, earlier sections included - not this section's own",
                };
            }

            WindowPeaks(r, out long? ws, out long? priv);
            if (r.MemoryStart != null && r.MemoryEnd != null)
            {
                m["window_peak"] = new Dictionary<string, object?>
                {
                    ["working_set_bytes"] = ws,
                    ["private_bytes"] = priv,
                    ["working_set_rose"] = ws.HasValue,
                    ["private_rose"] = priv.HasValue,
                    ["note"] = "exact, from the operating system's peak counters read at both edges of the window: they only ever rise, so "
                               + "a peak that rose in the window is the window's own. When it did not rise, the window stayed below the "
                               + "earlier peak and only the sampled peak is known",
                };
            }

            return m;
        }

        /// <summary>The window's exact peaks, where the lifetime peak rose inside it; null where it did not.</summary>
        private static void WindowPeaks(SectionResult r, out long? workingSet, out long? privateBytes)
        {
            workingSet = null;
            privateBytes = null;
            if (r.MemoryStart == null || r.MemoryEnd == null) return;
            if (r.MemoryEnd.PeakWorkingSet.HasValue && r.MemoryStart.PeakWorkingSet.HasValue && r.MemoryEnd.PeakWorkingSet > r.MemoryStart.PeakWorkingSet)
                workingSet = r.MemoryEnd.PeakWorkingSet;
            if (r.MemoryEnd.PeakPrivateBytes.HasValue && r.MemoryStart.PeakPrivateBytes.HasValue && r.MemoryEnd.PeakPrivateBytes > r.MemoryStart.PeakPrivateBytes)
                privateBytes = r.MemoryEnd.PeakPrivateBytes;
        }

        private static Dictionary<string, object?>? SeriesDoc(SeriesStat? s)
        {
            if (s == null) return null;
            return new Dictionary<string, object?>
            {
                ["peak"] = s.Peak,
                ["mean"] = Math.Round(s.Mean),
                ["first"] = s.First,
                ["last"] = s.Last,
                ["samples"] = s.Count,
            };
        }

        private static Dictionary<string, object?> LastGcDoc(GCMemoryInfo g, int gcCountAtStart)
        {
            Dictionary<string, object?> d = new Dictionary<string, object?>
            {
                ["index"] = g.Index,
                ["in_window"] = g.Index > gcCountAtStart,
                ["generation"] = g.Generation,
                ["compacted"] = g.Compacted,
                ["concurrent"] = g.Concurrent,
                ["heap_size_bytes"] = g.HeapSizeBytes,
                ["committed_bytes"] = g.TotalCommittedBytes,
                ["fragmented_bytes"] = g.FragmentedBytes,
                ["promoted_bytes"] = g.PromotedBytes,
                ["pinned_objects"] = g.PinnedObjectsCount,
                ["pause_time_percentage_process"] = Math.Round(g.PauseTimePercentage, 3),
                ["memory_load_bytes"] = g.MemoryLoadBytes,
                ["total_available_memory_bytes"] = g.TotalAvailableMemoryBytes,
                ["note"] = "the last collection before the section ended; pause_time_percentage_process covers the whole process life, "
                           + "the section's own pause is gc.pause_ms",
            };
            string[] names = { "gen0", "gen1", "gen2", "large_object_heap", "pinned_object_heap" };
            List<object?> gens = new List<object?>();
            ReadOnlySpan<GCGenerationInfo> info = g.GenerationInfo;
            for (int i = 0; i < info.Length && i < names.Length; i++)
            {
                gens.Add(new Dictionary<string, object?>
                {
                    ["name"] = names[i],
                    ["size_before_bytes"] = info[i].SizeBeforeBytes,
                    ["size_after_bytes"] = info[i].SizeAfterBytes,
                    ["fragmentation_after_bytes"] = info[i].FragmentationAfterBytes,
                });
            }

            d["generations"] = gens;
            return d;
        }

        private static Dictionary<string, object?>? IoDoc(ProcessIoReading? io)
        {
            if (io == null) return null;
            return new Dictionary<string, object?>
            {
                ["read_bytes"] = io.ReadBytes,
                ["write_bytes"] = io.WriteBytes,
                ["other_bytes"] = io.OtherBytes,
                ["read_ops"] = io.ReadOps,
                ["write_ops"] = io.WriteOps,
                ["other_ops"] = io.OtherOps,
                ["storage_read_bytes"] = io.StorageReadBytes,
                ["storage_write_bytes"] = io.StorageWriteBytes,
                ["source"] = io.Source,
            };
        }

        private static Dictionary<string, object?>? SteadyDoc(SteadyStateResult? st)
        {
            if (st == null) return null;
            if (!st.Defined)
            {
                return new Dictionary<string, object?>
                {
                    ["defined"] = false,
                    ["why"] = st.Why,
                    ["section_seeds_per_second"] = R(st.SectionSeedsPerSecond),
                };
            }

            return new Dictionary<string, object?>
            {
                ["defined"] = true,
                ["definition"] = "from the latest worker's first seed start to the earliest worker's last seed finish: the stretch in which "
                                 + "every worker was busy; seeds count by the share of their own time inside it",
                ["head_s"] = Math.Round(st.HeadSeconds, 4),
                ["window_s"] = Math.Round(st.WindowSeconds, 4),
                ["tail_s"] = Math.Round(st.TailSeconds, 4),
                ["window_share"] = st.SectionSeconds > 0 ? Math.Round(st.WindowSeconds / st.SectionSeconds, 4) : null,
                ["seeds_in_window"] = Math.Round(st.SeedsInWindow, 3),
                ["seeds_per_second"] = R(st.WindowSeedsPerSecond),
                ["section_seeds_per_second"] = R(st.SectionSeedsPerSecond),
                ["tail_loss"] = Math.Round(st.TailLoss, 4),
            };
        }

        private static Dictionary<string, object?> OverheadDoc(OverheadResult o)
        {
            double off = Median(o.SinkOffS), on = Median(o.SinkOnS);
            Dictionary<string, object?> d = new Dictionary<string, object?>
            {
                ["section"] = o.Section.Label + " x" + o.Section.Seeds + ", one worker",
                ["method"] = "interleaved ABAB x " + o.Reps + ", medians of whole-leg wall time",
                ["timestamp_ns"] = Math.Round(o.TimestampNs, 2),
                ["begin_end_pair_ns"] = Math.Round(o.PairNs, 2),
                ["boundaries_per_seed"] = Math.Round(o.BoundariesPerSeed, 2),
                ["predicted_pct"] = Math.Round(o.PredictedPct, 4),
                ["phases"] = new Dictionary<string, object?>
                {
                    ["sink_off_s"] = o.SinkOffS.ConvertAll(x => (object?)Math.Round(x, 5)),
                    ["sink_on_s"] = o.SinkOnS.ConvertAll(x => (object?)Math.Round(x, 5)),
                    ["measured_pct"] = Math.Round((on / off - 1) * 100.0, 3),
                },
                ["counters"] = new Dictionary<string, object?>
                {
                    ["off_s"] = o.CountersOffS.ConvertAll(x => (object?)Math.Round(x, 5)),
                    ["on_s"] = o.CountersOnS.ConvertAll(x => (object?)Math.Round(x, 5)),
                    ["measured_pct"] = Math.Round((Median(o.CountersOnS) / Median(o.CountersOffS) - 1) * 100.0, 3),
                    ["note"] = "child processes of this build, phases on in both; only the counter switch differs",
                },
            };
            if (o.BaselineExe != null)
            {
                d["profiler_off_vs_baseline"] = new Dictionary<string, object?>
                {
                    ["baseline_worldgen_sha256"] = o.BaselineSha256,
                    ["search_seeds"] = o.SearchSeeds,
                    ["baseline_seeds_per_second"] = o.BaselineSps.ConvertAll(x => (object?)Math.Round(x, 3)),
                    ["this_seeds_per_second"] = o.ThisSps.ConvertAll(x => (object?)Math.Round(x, 3)),
                    ["slowdown_pct"] = Math.Round((Median(o.BaselineSps) / Median(o.ThisSps) - 1) * 100.0, 3),
                    ["budget_pct"] = 1.0,
                    ["note"] = "one-worker T2 search, the same query on both builds, seeds_per_second as each run reports it",
                };
            }

            return d;
        }

        /// <summary>The section's phases in tree order: each parent followed by its children.</summary>
        private static List<PhaseView> Ordered(SectionView s)
        {
            List<PhaseView> r = new List<PhaseView>();
            void Walk(Phase parent)
            {
                foreach (PhaseView p in s.Phases)
                {
                    if (p.Parent != parent) continue;
                    r.Add(p);
                    Walk(p.Phase);
                }
            }

            Walk(Phase.None);
            return r;
        }

        private static int Depth(SectionView s, PhaseView p)
        {
            int d = 0;
            Phase cur = p.Parent;
            while (cur != Phase.None && d < 8)
            {
                d++;
                PhaseView? up = s.Get(cur);
                if (up == null) break;
                cur = up.Parent;
            }

            return d;
        }

        private static Dictionary<string, object?> StatDoc(Stat s) => new Dictionary<string, object?>
        {
            ["median"] = R(s.Median),
            ["p10"] = R(s.P10),
            ["p90"] = R(s.P90),
            ["mean"] = R(s.Mean),
        };

        private static double? R(double v) => double.IsFinite(v) ? Math.Round(v, 4) : null;

        private static double Median(List<double> v)
        {
            if (v.Count == 0) return double.NaN;
            List<double> s = new List<double>(v);
            s.Sort();
            return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2.0;
        }

        private static string Utc(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        private static string Pct(double fraction) => (fraction * 100.0).ToString("F1", CultureInfo.InvariantCulture) + " %";

        // ---- writing --------------------------------------------------------------------------------

        public void WriteTo(Utf8JsonWriter j) => WriteValue(j, Document());

        public void WriteJson(string path)
        {
            using MemoryStream ms = new MemoryStream();
            using (Utf8JsonWriter j = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                WriteValue(j, Document());
            }

            string text = Encoding.UTF8.GetString(ms.ToArray()).Replace("\r\n", "\n") + "\n";
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        private static void WriteValue(Utf8JsonWriter j, object? v)
        {
            switch (v)
            {
                case null: j.WriteNullValue(); break;
                case string s: j.WriteStringValue(s); break;
                case bool b: j.WriteBooleanValue(b); break;
                case int i: j.WriteNumberValue(i); break;
                case long l: j.WriteNumberValue(l); break;
                case double d: j.WriteNumberValue(d); break;
                case Dictionary<string, object?> map:
                    j.WriteStartObject();
                    foreach (KeyValuePair<string, object?> kv in map)
                    {
                        if (kv.Value == null) continue;   // absent, never written as 0 or null
                        j.WritePropertyName(kv.Key);
                        WriteValue(j, kv.Value);
                    }

                    j.WriteEndObject();
                    break;
                case List<object?> list:
                    j.WriteStartArray();
                    foreach (object? x in list) WriteValue(j, x);
                    j.WriteEndArray();
                    break;
                default:
                    j.WriteStringValue(Convert.ToString(v, CultureInfo.InvariantCulture));
                    break;
            }
        }

        /// <summary>
        /// One row per measured seed: <c># seedlab-profile-seeds/2</c>, then a header, then integers
        /// only, LF line ends. <c>start_us</c> is the seed's start from its section's start (new in /2;
        /// the other columns are /1's). Every phase any section entered gets a microseconds column.
        /// </summary>
        public void WritePerSeedCsv(string path)
        {
            List<Phase> cols = new List<Phase>();
            foreach (Phase p in PhaseMap.All)
            {
                foreach (SectionView s in _sections)
                {
                    if (s.Get(p) != null) { cols.Add(p); break; }
                }
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("# seedlab-profile-seeds/2\n");
            sb.Append("section,workers,seed,worker,start_us,wall_us,alloc_kb");
            foreach (Phase p in cols) sb.Append(',').Append(PhaseMap.Name(p).Replace('.', '_')).Append("_us");
            if (_run.CountersOn)
            {
                foreach (Counter c in PhaseClock.All) sb.Append(',').Append(PhaseClock.Name(c));
            }

            sb.Append('\n');
            double f = Stopwatch.Frequency;
            int rows = 0;
            foreach (SectionView s in _sections)
            {
                string label = s.Result.Spec.Label.Replace(' ', '_');
                SeedTable r = s.Result.Table;
                for (int i = 0; i < r.Count; i++)
                {
                    if (++rows > 1_000_000) break;
                    sb.Append(label).Append(',').Append(s.Result.WorkerCount.ToString(CultureInfo.InvariantCulture))
                      .Append(',').Append(r.Seed[i].ToString(CultureInfo.InvariantCulture))
                      .Append(',').Append(r.Worker[i].ToString(CultureInfo.InvariantCulture))
                      .Append(',').Append(((long)Math.Round((r.StartTicks[i] - s.Result.StartTicks) * 1e6 / f)).ToString(CultureInfo.InvariantCulture))
                      .Append(',').Append(((long)Math.Round(r.WallTicks[i] * 1e6 / f)).ToString(CultureInfo.InvariantCulture))
                      .Append(',').Append((r.AllocBytes[i] / 1024).ToString(CultureInfo.InvariantCulture));
                    foreach (Phase p in cols)
                    {
                        long t = r.Tick(i, p);
                        sb.Append(',').Append(((long)Math.Round(t * 1e6 / f)).ToString(CultureInfo.InvariantCulture));
                    }

                    if (_run.CountersOn)
                    {
                        foreach (Counter c in PhaseClock.All)
                        {
                            sb.Append(',').Append(r.Counter(i, (int)c).ToString(CultureInfo.InvariantCulture));
                        }
                    }

                    sb.Append('\n');
                }
            }

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        // ---- the terminal ---------------------------------------------------------------------------

        public void Print(Out o)
        {
            o.Header("Profile" + (_tainted ? "  -  TAINTED: the machine was not quiet, these timings are not measurements" : ""));
            if (_run.Machine.TryGetValue("cpu", out object? cpu) && cpu is Dictionary<string, object?> c)
            {
                o.Field("cpu", Convert.ToString(c["brand"], CultureInfo.InvariantCulture) + " (" + c["vendor"] + " family "
                               + c["family"] + " model " + c["model"] + " stepping " + c["stepping"] + ")");
            }

            o.Field("runtime", _run.Machine["runtime"] + ", " + _run.Machine["gc"] + " GC, "
                               + _run.Machine["logical_cores"] + " logical cores, timestamp "
                               + Out.F(Convert.ToDouble(_run.Machine["timestamp_ns"], CultureInfo.InvariantCulture), 1) + " ns");
            o.Field("seeds", "key 0x" + _run.Key.ToString("X16", CultureInfo.InvariantCulture) + " from index " + _run.From
                             + (_run.Key == PilotSeedOrder.Key && _run.From == 0 ? " (the pilot order; first seed " + PilotSeedOrder.DocumentedFirstEight[0] + ")" : ""));
            o.Field("counters", _run.CountersOn ? "ON - timings include the counting" : "off");
            if (_run.SaturateSeconds.HasValue)
            {
                o.Field("seed counts", "sized to keep every worker busy for about " + Out.F(_run.SaturateSeconds.Value, 0) + " s (at least "
                                       + SaturationPlanner.MinPerWorker + " seeds per worker), from an uncounted pilot each; estimated "
                                       + ProfileCommand.Minutes(_run.EstimatedSeconds ?? double.NaN) + " in all");
            }

            if (_run.PlanFile != null)
            {
                o.Field("seed counts", "replayed from " + _run.PlanFile
                                       + (_run.ReplayedPilots.Count > 0 ? ", after running its " + _run.ReplayedPilots.Count + " pilot(s) again, uncounted" : ""));
                foreach (PlanDifference d in _run.PlanDifferences) o.Field("differs", d.What + ": " + d.Plan + " then, " + d.Now + " now - " + d.Note);
            }
            if (_run.Baseline != null)
            {
                QuietBaseline b = _run.Baseline;
                o.Field("baseline", Out.F(b.Seconds, 0) + " s: other processes used "
                                    + Out.F(b.ForeignCoreSeconds, 2) + " core-s (" + Out.F(b.ForeignCores, 2) + " cores"
                                    + (b.Tick.MachineCoreSeconds.HasValue ? ", whole machine" : ", readable processes only")
                                    + "); the own time of " + b.Tick.Unreadable + " of " + b.Tick.Processes + " processes was not readable"
                                    + (b.Tainted ? "; not quiet, so it does not raise the limit" : ""));
            }

            if (_run.Probe != null)
            {
                o.Field("limit", Out.F(_run.Probe.LimitCores, 2) + " foreign cores"
                                 + (_run.Probe.SeesWholeMachine ? ", judged on the whole machine" : ", judged on the readable processes only"));
            }

            if (_tainted)
            {
                o.Line();
                o.Note("TAINTED - what else was running:");
                foreach (string r in _taintReasons) o.Note("  " + r);
            }
            else
            {
                o.Field("hygiene", "quiet: no other SeedLab program, no Valheim, no busy build tool, foreign load under the limit"
                                   + (_run.Probe != null && _run.Probe.SeesWholeMachine ? " (whole machine, protected processes included)" : ""));
            }

            foreach (SectionView s in _sections) PrintSection(o, s);

            if (_run.Overhead != null) PrintOverhead(o, _run.Overhead);

            if (_hypotheses.Count > 0)
            {
                o.Header("Hypotheses" + (_tainted ? " (from a tainted run: not evidence)" : ""));
                foreach (Hypothesis h in _hypotheses)
                {
                    o.Note(h.Id + "  " + h.Verdict.ToUpperInvariant() + "  " + h.Prediction);
                    o.Note("     " + h.Evidence);
                }
            }
        }

        private static void PrintSection(Out o, SectionView s)
        {
            SectionResult r = s.Result;
            o.Header(r.Spec.Label + (r.Spec.Tier == "t5" && r.PrefixRun != r.Spec.Prefix ? " (runs " + r.PrefixRun + ")" : "")
                     + ", " + r.WorkerCount + " worker(s), " + r.Table.Count + " seeds (+" + r.Warmup + " warm-up each)"
                     + (s.Tainted ? "  TAINTED" : ""));
            o.Field("seed", "median " + Out.F(s.Seed.Median, 2) + " ms, p10 " + Out.F(s.Seed.P10, 2) + ", p90 " + Out.F(s.Seed.P90, 2)
                            + ", mean " + Out.F(s.Seed.Mean, 2) + "; " + Out.F(s.SeedsPerSecond, 2) + " seeds/s, "
                            + Out.F(s.CpuMsPerSeed, 1) + " CPU ms/seed");
            if (s.Phases.Count > 0)
            {
                List<string[]> rows = new List<string[]>();
                foreach (PhaseView p in Ordered(s))
                {
                    string name = new string(' ', 2 * Depth(s, p)) + PhaseMap.Name(p.Phase);
                    rows.Add(new[]
                    {
                        name, Out.F(p.EntriesPerSeed, 2), Out.F(p.Ms.Median, 3), Out.F(p.Ms.P10, 3), Out.F(p.Ms.P90, 3),
                        Out.F(p.Ms.Mean, 3), Out.F(p.Share * 100, 1), Out.F(p.AllocMean / 1048576.0, 2),
                        p.OtherMs.HasValue ? Out.F(p.OtherMs.Value, 3) : "",
                    });
                }

                if (s.OtherMs.HasValue)
                {
                    rows.Add(new[] { "(other: seed - phases)", "", "", "", "", Out.F(s.OtherMs.Value, 3),
                                     Out.F(s.Seed.Mean > 0 ? s.OtherMs.Value / s.Seed.Mean * 100 : 0, 1), "", "" });
                }

                o.Table(new[] { "phase", "entries", "median ms", "p10", "p90", "mean", "% seed", "alloc MiB", "other ms" }, rows,
                        new[] { false, true, true, true, true, true, true, true, true });
            }

            o.Field("gc", "gen0 " + r.Gen0 + ", gen1 " + r.Gen1 + ", gen2 " + r.Gen2 + ", pause " + Out.F(r.PauseMs, 1) + " ms ("
                          + Out.F(s.PauseShare * 100, 2) + " % of wall)");
            o.Field("jit", r.JitMethods + " methods, " + Out.F(r.JitMs, 1) + " ms in the window" + (s.JitFlagged ? " - OVER 1 % of wall" : ""));
            PrintResources(o, s);
            if (s.Counters != null)
            {
                List<string> parts = new List<string>();
                foreach (Counter c in PhaseClock.All)
                {
                    if (s.Counters[(int)c] > 0) parts.Add(PhaseClock.Name(c) + " " + Out.F(s.Counters[(int)c], 0));
                }

                o.Field("counters/seed", string.Join(", ", parts));
            }
        }

        /// <summary>
        /// The compact resource block of one measurement: processor time and how it splits, the steady
        /// state, memory, disk and how the seed count was chosen - in plain units.
        /// </summary>
        private static void PrintResources(Out o, SectionView s)
        {
            SectionResult r = s.Result;
            if (r.Resources)
            {
                int cores = Math.Max(1, Environment.ProcessorCount);
                double process = r.CpuUserSeconds.HasValue && r.CpuKernelSeconds.HasValue ? r.CpuUserSeconds.Value + r.CpuKernelSeconds.Value : r.CpuSeconds;
                string cpu = Out.F(process, 1) + " s of processor time"
                             + (r.CpuKernelSeconds.HasValue ? " (" + Out.F(r.CpuKernelSeconds.Value, 1) + " s in the kernel)" : "")
                             + " over " + Out.F(r.WallSeconds, 1) + " s = " + Out.F(r.WallSeconds > 0 ? process / r.WallSeconds : 0, 1) + " of "
                             + cores + " cores busy (" + Out.F(r.WallSeconds > 0 ? 100.0 * process / (r.WallSeconds * cores) : 0, 1) + " %)";
                o.Field("cpu", cpu);
                if (s.WorkerCpuSeconds.HasValue && s.WorkerShareSeconds.HasValue)
                {
                    double sampler = r.Memory?.SamplerCpu?.TotalSeconds ?? 0;
                    double other = process - s.WorkerCpuSeconds.Value - sampler;
                    o.Field("cpu split", "workers " + Out.F(s.WorkerCpuSeconds.Value, 1) + " s, everything else " + Out.F(other, 1)
                                         + " s (the garbage collector's threads, the JIT compiling code, the runtime)");
                    o.Field("off cpu", "the workers were off a processor " + Out.F(s.WorkerShareSeconds.Value - s.WorkerCpuSeconds.Value, 1)
                                       + " s of their " + Out.F(s.WorkerShareSeconds.Value, 1) + " s (waiting for a collection, preempted, page faults)");
                }

                if (r.Memory != null)
                {
                    ResourceSummary z = r.Memory;
                    List<string> parts = new List<string>();
                    if (z.WorkingSet != null) parts.Add("working set peak " + Bytes(z.WorkingSet.Peak) + " (mean " + Bytes((long)z.WorkingSet.Mean) + ")");
                    if (z.PrivateBytes != null) parts.Add("private " + Bytes(z.PrivateBytes.Peak));
                    if (z.GcHeap != null) parts.Add("GC heap " + Bytes(z.GcHeap.Peak));
                    if (z.GcCommitted != null) parts.Add("GC committed " + Bytes(z.GcCommitted.Peak));
                    o.Field("ram", string.Join(", ", parts) + "; " + z.Samples + " samples every " + Out.F(z.Interval.TotalMilliseconds, 0) + " ms");
                }

                if (r.MemoryStart != null && r.MemoryEnd != null)
                {
                    WindowPeaks(r, out long? ws, out long? priv);
                    o.Field("ram peak", (ws.HasValue ? "working set " + Bytes(ws.Value) : "working set not above its earlier peak of " + Bytes(r.MemoryStart.PeakWorkingSet ?? 0))
                                        + ", " + (priv.HasValue ? "private " + Bytes(priv.Value) : "private not above its earlier peak of " + Bytes(r.MemoryStart.PeakPrivateBytes ?? 0))
                                        + " (exact, from Windows' own peak counters; the samples above can miss a short peak)");
                }

                GCMemoryInfo g = r.LastGc;
                if (g.Index > 0)
                {
                    ReadOnlySpan<GCGenerationInfo> gi = g.GenerationInfo;
                    string loh = gi.Length > 3 ? ", large objects " + Bytes(gi[3].SizeAfterBytes) : "";
                    o.Field("gc heap", "allocated " + Bytes(r.AllocatedBytes) + " (" + Bytes(r.Table.Count > 0 ? r.AllocatedBytes / r.Table.Count : 0)
                                       + " per seed); last collection #" + g.Index + " (gen" + g.Generation + (g.Index > r.GcCountAtStart ? "" : ", before this window")
                                       + "): heap " + Bytes(g.HeapSizeBytes) + ", fragmented " + Bytes(g.FragmentedBytes) + ", promoted "
                                       + Bytes(g.PromotedBytes) + loh
                                       + (r.GcHeapCountMax.HasValue ? "; up to " + r.GcHeapCountMax.Value + " collector heaps (the configured maximum; how many were in use is not measured)" : ""));
                }

                o.Field("disk", r.Io == null ? "not available on this system"
                                    : "read " + Bytes(r.Io.ReadBytes) + ", wrote " + Bytes(r.Io.WriteBytes) + " in the window (every read and write call; "
                                      + "the profile writes only --out, after the last section)");
            }

            if (s.Steady != null)
            {
                SteadyStateResult st = s.Steady;
                o.Field("steady", !st.Defined ? "no steady state: " + st.Why
                                      : "all " + r.WorkerCount + " worker(s) busy for " + Out.F(st.WindowSeconds, 2) + " s of " + Out.F(st.SectionSeconds, 2)
                                        + " s: " + Out.F(st.WindowSeedsPerSecond, 2) + " seeds/s there, " + Out.F(st.SectionSeedsPerSecond, 2)
                                        + " over the whole section (start " + Out.F(st.HeadSeconds, 2) + " s, tail " + Out.F(st.TailSeconds, 2)
                                        + " s, " + Out.F(st.TailLoss * 100, 1) + " % lost to them)");
            }

            if (r.Sizing != null && r.Sizing.Source == "saturate")
            {
                SectionSizing z = r.Sizing;
                string runs = z.PilotRuns != null && z.PilotRuns.Count > 0 ? string.Join(" -> ", z.PilotRuns) : Convert.ToString(z.PilotSeeds, CultureInfo.InvariantCulture) ?? "?";
                o.Field("sizing", "pilot " + runs + " seeds, " + Out.F(z.PilotMeanSeedMs ?? double.NaN, 2) + " ms each (its later half) -> "
                                  + Out.F(z.RateEstimate ?? 0, 1) + " seeds/s -> " + r.Table.Count + " seeds, estimated "
                                  + ProfileCommand.Minutes(z.EstimatedSeconds ?? double.NaN) + " (it took " + ProfileCommand.Minutes(r.WallSeconds) + ")"
                                  + (z.Capped ? "; capped at " + SaturationPlanner.MaxSeeds.ToString("N0", CultureInfo.InvariantCulture) + " seeds"
                                     : z.FloorDecided ? "; the " + SaturationPlanner.MinPerWorker + "-per-worker minimum decided, not the "
                                                        + Out.F(z.TargetSeconds ?? 0, 0) + " s asked" : ""));
            }
            else if (r.Sizing != null && r.Sizing.Source == "plan")
            {
                o.Field("sizing", "replayed from the plan" + (r.Sizing.RecordedSeconds.HasValue
                                      ? "; it took " + ProfileCommand.Minutes(r.Sizing.RecordedSeconds.Value) + " when recorded, "
                                        + ProfileCommand.Minutes(r.WallSeconds) + " now"
                                      : ""));
            }
        }

        private static string Bytes(long b)
        {
            double a = Math.Abs((double)b);
            if (a >= 1024.0 * 1024 * 1024) return Out.F(b / (1024.0 * 1024 * 1024), 2) + " GiB";
            if (a >= 1024.0 * 1024) return Out.F(b / (1024.0 * 1024), 1) + " MiB";
            if (a >= 1024.0) return Out.F(b / 1024.0, 1) + " KiB";
            return b.ToString(CultureInfo.InvariantCulture) + " B";
        }

        private static void PrintOverhead(Out o, OverheadResult v)
        {
            o.Header("Profiler overhead, " + v.Section.Label + " x" + v.Section.Seeds + ", one worker (ABAB x " + v.Reps + ")");
            o.Field("predicted", Out.F(v.BoundariesPerSeed, 1) + " boundaries/seed x " + Out.F(v.PairNs, 1) + " ns per Begin/End = "
                                 + Out.F(v.PredictedPct, 4) + " % of a " + Out.F(v.SeedMsProfiled, 3) + " ms seed");
            double off = Median(v.SinkOffS), on = Median(v.SinkOnS);
            o.Field("phases", "sink off " + Out.F(off, 4) + " s, on " + Out.F(on, 4) + " s: " + Out.F((on / off - 1) * 100, 2) + " %");
            double coff = Median(v.CountersOffS), con = Median(v.CountersOnS);
            o.Field("counters", "off " + Out.F(coff, 4) + " s, on " + Out.F(con, 4) + " s: " + Out.F((con / coff - 1) * 100, 2) + " % (child processes)");
            if (v.BaselineExe != null)
            {
                double b = Median(v.BaselineSps), t = Median(v.ThisSps);
                o.Field("profiler off", "baseline " + Out.F(b, 2) + " seeds/s, this build " + Out.F(t, 2) + " seeds/s: "
                                        + Out.F((b / t - 1) * 100, 2) + " % slower (budget 1 %)");
            }
        }
    }
}
