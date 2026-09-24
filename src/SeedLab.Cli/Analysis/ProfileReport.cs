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
    /// <para>The document is <c>seedlab-profile/1</c> JSON, UTF-8 without a BOM. A measurement that was
    /// not taken is left out, never written as 0. Only this layer reads a <see cref="PhaseSink"/>.</para>
    /// </summary>
    internal sealed class ProfileReport
    {
        public const string Schema = "seedlab-profile/1";

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

            if (run.Probe != null && run.Probe.TaintedBetween(run.CreatedUtc, run.EndedUtc, out IReadOnlyList<string> reasons))
            {
                _taintReasons.AddRange(reasons);
            }

            _tainted = _taintReasons.Count > 0;
            foreach (SectionResult s in run.Sections) _sections.Add(new SectionView(s, run));
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
            public SectionView(SectionResult r, ProfileRun run)
            {
                Result = r;
                int n = r.Rows.Count;
                double f = Stopwatch.Frequency;
                SeedMs = new double[n];
                for (int i = 0; i < n; i++) SeedMs[i] = r.Rows[i].WallTicks * 1000.0 / f;
                Seed = Stat.Of(SeedMs);
                double alloc = 0;
                foreach (SeedRow row in r.Rows) alloc += row.AllocBytes;
                AllocMean = n > 0 ? alloc / n : 0;

                bool recorded = n > 0 && r.Rows[0].Phase != null;
                if (recorded)
                {
                    foreach (Phase p in PhaseMap.All)
                    {
                        int id = (int)p;
                        long entries = 0;
                        double allocP = 0;
                        double[] ms = new double[n];
                        for (int i = 0; i < n; i++)
                        {
                            long[] ph = r.Rows[i].Phase!;
                            entries += ph[PhaseSink.EntriesOffset + id];
                            allocP += ph[PhaseSink.AllocOffset + id];
                            ms[i] = ph[id] * 1000.0 / f;
                        }

                        if (entries == 0) continue;
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

                if (run.CountersOn && n > 0 && r.Rows[0].Counters != null)
                {
                    Counters = new double[PhaseClock.Capacity];
                    foreach (SeedRow row in r.Rows)
                    {
                        for (int c = 0; c < PhaseClock.Capacity; c++) Counters[c] += row.Counters![c];
                    }

                    for (int c = 0; c < PhaseClock.Capacity; c++) Counters[c] /= n;
                }

                if (run.Probe != null && run.Probe.TaintedBetween(r.StartUtc, r.EndUtc, out IReadOnlyList<string> reasons))
                {
                    TaintReasons.AddRange(reasons);
                }
            }

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

            public double SeedsPerSecond => Result.WallSeconds > 0 ? Result.Rows.Count / Result.WallSeconds : 0;

            public double CpuMsPerSeed => Result.Rows.Count > 0 ? Result.CpuSeconds * 1000.0 / Result.Rows.Count : 0;

            public double PauseShare => Result.WallSeconds > 0 ? Result.PauseMs / (Result.WallSeconds * 1000.0) : 0;

            public bool JitFlagged => Result.JitMs > 0.01 * Result.WallSeconds * 1000.0;

            /// <summary>Per-seed ratio of the summed ticks of <paramref name="num"/> to those of <paramref name="den"/> (or the seed).</summary>
            public double[] Shares(Phase[] num, Phase? den)
            {
                double[] s = new double[Result.Rows.Count];
                for (int i = 0; i < s.Length; i++)
                {
                    long[] ph = Result.Rows[i].Phase!;
                    double a = 0;
                    foreach (Phase p in num) a += ph[(int)p];
                    double b = den.HasValue ? ph[(int)den.Value] : Result.Rows[i].WallTicks;
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
                                         "DOTNET_gcServer", "DOTNET_GCgen0size", PhaseClock.EnvironmentVariable,
                                         SeedLab.WorldGen.Simd.SimdDispatch.EnvironmentVariable })
            {
                string? v = Environment.GetEnvironmentVariable(k);
                if (v != null) knobs[k] = v;
            }

            m["knobs_set"] = knobs;
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
            d["not_measured"] = new List<object?>
            {
                "isolated per-call kernel timings (the attribution of phase time to Perlin, libm and river lookups)",
                "per-location-type placement costs inside t5.place",
                "the search evaluator's own phases (collect, seedtext); vseed profile runs its own loop",
            };
            return d;
        }

        private Dictionary<string, object?> Hygiene()
        {
            Dictionary<string, object?> h = new Dictionary<string, object?>
            {
                ["tainted"] = _tainted,
                ["reasons"] = _taintReasons.ConvertAll(r => (object?)r),
                ["method"] = "per-process TotalProcessorTime deltas, every " + (_run.Probe?.Interval.TotalSeconds ?? 5).ToString(CultureInfo.InvariantCulture)
                             + " s, excluding this process, Idle and its own timing legs",
            };
            if (_run.Baseline != null)
            {
                QuietBaseline b = _run.Baseline;
                h["baseline"] = new Dictionary<string, object?>
                {
                    ["seconds"] = Math.Round(b.Seconds, 1),
                    ["foreign_core_seconds"] = Math.Round(b.ForeignCoreSeconds, 2),
                    ["foreign_cores"] = Math.Round(b.ForeignCores, 3),
                    ["processes"] = b.Tick.Processes,
                    ["unreadable"] = b.Tick.Unreadable,
                    ["tainted"] = b.Tainted,
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
                };
                IReadOnlyList<ProbeTick> ticks = _run.Probe.Ticks;
                int tainted = 0;
                double foreign = 0, seconds = 0, max = 0;
                foreach (ProbeTick k in ticks)
                {
                    if (k.Tainted) tainted++;
                    foreign += k.ForeignCoreSeconds;
                    seconds += k.Seconds;
                    max = Math.Max(max, k.ForeignCores);
                }

                h["samples"] = ticks.Count;
                h["samples_tainted"] = tainted;
                h["foreign_cores_mean"] = seconds > 0 ? Math.Round(foreign / seconds, 3) : null;
                h["foreign_cores_max_sample"] = ticks.Count > 0 ? Math.Round(max, 3) : null;
                h["watched_seen"] = new List<string>(_run.Probe.WatchedSeen()).ConvertAll(s => (object?)s);
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
                ["seeds"] = r.Rows.Count,
                ["warmup_per_worker"] = r.Warmup,
                ["wall_s"] = Math.Round(r.WallSeconds, 4),
                ["seeds_per_second"] = Math.Round(s.SeedsPerSecond, 3),
                ["cpu_ms_per_seed"] = Math.Round(s.CpuMsPerSeed, 3),
                ["seed_ms"] = StatDoc(s.Seed),
                ["alloc_bytes_mean"] = Math.Round(s.AllocMean),
                ["tainted"] = s.Tainted,
                ["taint_reasons"] = s.Tainted ? s.TaintReasons.ConvertAll(x => (object?)x) : null,
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

            d["gc"] = new Dictionary<string, object?>
            {
                ["gen0"] = r.Gen0,
                ["gen1"] = r.Gen1,
                ["gen2"] = r.Gen2,
                ["pause_ms"] = Math.Round(r.PauseMs, 3),
                ["pause_share"] = Math.Round(s.PauseShare, 5),
            };
            d["jit"] = new Dictionary<string, object?>
            {
                ["methods_in_window"] = r.JitMethods,
                ["compile_ms_in_window"] = Math.Round(r.JitMs, 3),
                ["over_1_percent"] = s.JitFlagged,
            };
            List<object?> ws = new List<object?>();
            foreach (WorkerStat w in r.Workers)
            {
                ws.Add(new Dictionary<string, object?>
                {
                    ["id"] = w.Id,
                    ["seeds"] = w.Seeds,
                    ["busy_s"] = Math.Round(w.BusyTicks / (double)Stopwatch.Frequency, 4),
                });
            }

            d["worker_stats"] = ws;
            return d;
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
        /// One row per measured seed: <c># seedlab-profile-seeds/1</c>, then a header, then integers
        /// only, LF line ends. Every phase any section entered gets a microseconds column.
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
            sb.Append("# seedlab-profile-seeds/1\n");
            sb.Append("section,workers,seed,worker,wall_us,alloc_kb");
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
                foreach (SeedRow r in s.Result.Rows)
                {
                    if (++rows > 1_000_000) break;
                    sb.Append(label).Append(',').Append(s.Result.WorkerCount.ToString(CultureInfo.InvariantCulture))
                      .Append(',').Append(r.Seed.ToString(CultureInfo.InvariantCulture))
                      .Append(',').Append(r.Worker.ToString(CultureInfo.InvariantCulture))
                      .Append(',').Append(((long)Math.Round(r.WallTicks * 1e6 / f)).ToString(CultureInfo.InvariantCulture))
                      .Append(',').Append((r.AllocBytes / 1024).ToString(CultureInfo.InvariantCulture));
                    foreach (Phase p in cols)
                    {
                        long t = r.Phase == null ? 0 : r.Phase[(int)p];
                        sb.Append(',').Append(((long)Math.Round(t * 1e6 / f)).ToString(CultureInfo.InvariantCulture));
                    }

                    if (_run.CountersOn)
                    {
                        foreach (Counter c in PhaseClock.All)
                        {
                            sb.Append(',').Append((r.Counters?[(int)c] ?? 0).ToString(CultureInfo.InvariantCulture));
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
            if (_run.Baseline != null)
            {
                o.Field("baseline", Out.F(_run.Baseline.Seconds, 0) + " s: other processes used "
                                    + Out.F(_run.Baseline.ForeignCoreSeconds, 2) + " core-s (" + Out.F(_run.Baseline.ForeignCores, 2) + " cores)");
            }

            if (_tainted)
            {
                o.Line();
                o.Note("TAINTED - what else was running:");
                foreach (string r in _taintReasons) o.Note("  " + r);
            }
            else
            {
                o.Field("hygiene", "quiet: no other vseed, no Valheim, no busy build tool, foreign load under the limit");
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
                     + ", " + r.WorkerCount + " worker(s), " + r.Rows.Count + " seeds (+" + r.Warmup + " warm-up each)"
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
