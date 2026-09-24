using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using SeedLab.Runtime.Execution;

namespace SeedLab.RuntimeTests
{
    /// <summary>
    /// The quiet-machine probe: the verdict rule on made-up processes (exact), then the live probe on
    /// this machine - it must never throw, must leave itself out, and must see a process it was told to
    /// watch while that process runs. A made-up whole-machine reader drives the parts that depend on
    /// load the probe cannot attribute to a readable process.
    /// </summary>
    public static class QuietProbeChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            DateTime t0 = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
            DateTime t1 = t0.AddSeconds(5);
            QuietThresholds th = QuietThresholds.Default;

            // ---- the rule --------------------------------------------------------------------------
            ProbeTick quiet = QuietMachineProbe.Evaluate(t0, t1, new[]
            {
                new ProcessCpu("explorer", 10, 0.2, WatchedKind.None),
                new ProcessCpu("dotnet", 11, 0.05, WatchedKind.DevTool),
            }, 2, 0, 0.2, th);
            check(!quiet.Tainted, "an idle desktop and an idle dotnet are quiet",
                  quiet.Tainted ? string.Join("; ", quiet.Reasons) : "0.25 core-s over 5 s");

            ProbeTick vseed = QuietMachineProbe.Evaluate(t0, t1, new[]
            {
                new ProcessCpu("vseed", 12, 0.0, WatchedKind.SeedLab),
            }, 1, 0, 0, th);
            check(vseed.Tainted && vseed.Reasons[0].Contains("another SeedLab program"),
                  "another vseed taints by being there, even idle", string.Join("; ", vseed.Reasons));

            ProbeTick game = QuietMachineProbe.Evaluate(t0, t1, new[]
            {
                new ProcessCpu("valheim", 13, 0.0, WatchedKind.Game),
            }, 1, 0, 0, th);
            check(game.Tainted && game.Reasons[0].Contains("Valheim"), "a running Valheim taints", string.Join("; ", game.Reasons));

            ProbeTick build = QuietMachineProbe.Evaluate(t0, t1, new[]
            {
                new ProcessCpu("VBCSCompiler", 14, 1.0, WatchedKind.DevTool),
            }, 1, 0, 0, th);
            check(build.Tainted && build.Reasons[0].Contains("VBCSCompiler"),
                  "a build tool above a tenth of a core taints (0.2 cores here)", string.Join("; ", build.Reasons));

            ProbeTick load = QuietMachineProbe.Evaluate(t0, t1, new[]
            {
                new ProcessCpu("chrome", 15, 4.0, WatchedKind.None),
                new ProcessCpu("game-launcher", 16, 3.0, WatchedKind.None),
            }, 2, 0, 0.2, th);
            check(load.Tainted && Math.Abs(load.ForeignCores - 1.4) < 1e-9,
                  "1.4 foreign cores is over the 1-core limit", string.Join("; ", load.Reasons));

            ProbeTick noisyDesk = QuietMachineProbe.Evaluate(t0, t1, new[]
            {
                new ProcessCpu("chrome", 15, 7.0, WatchedKind.None),
            }, 1, 0, 0.6, th);
            check(!noisyDesk.Tainted, "the limit rises to 3x a quiet recorded baseline (1.4 cores < 3 x 0.6)",
                  noisyDesk.Tainted ? string.Join("; ", noisyDesk.Reasons) : "quiet against its own baseline");

            ProbeTick twice = QuietMachineProbe.Evaluate(t0, t1, new[]
            {
                new ProcessCpu("vseed", 12, 0.5, WatchedKind.SeedLab),
            }, 1, 0, 0, th);
            check(twice.Reasons[0] == vseed.Reasons[0],
                  "a reason is worded without the sample's numbers, so one cause is one line in a report", twice.Reasons[0]);

            check(QuietMachineProbe.Classify("VSEED") == WatchedKind.SeedLab
                  && QuietMachineProbe.Classify("Valheim") == WatchedKind.Game
                  && QuietMachineProbe.Classify("MSBuild") == WatchedKind.DevTool
                  && QuietMachineProbe.Classify("notepad") == WatchedKind.None,
                  "process names are matched case-insensitively", "vseed, valheim, dotnet, VBCSCompiler, MSBuild");

            // ---- SeedLab's own gates and tests are SeedLab programs -------------------------------------
            bool gates = true;
            foreach (string n in new[] { "SeedLab.Acceptance.Tests", "SeedLab.Tests", "SeedLab.Runtime.Tests", "SeedLab.Search.Tests",
                                         "SeedLab.Search.Safety.Tests", "SeedLab.GoldenCheck", "SeedLab.LocationLab", "seedlab.goldencheck" })
            {
                if (QuietMachineProbe.Classify(n) != WatchedKind.SeedLab) gates = false;
            }

            check(gates && QuietMachineProbe.Classify("SeedLabX") == WatchedKind.None && QuietMachineProbe.Classify("MySeedLab.Tests") == WatchedKind.None,
                  "the repository's gate and test executables (SeedLab.*) are watched like vseed; a name that only resembles them is not",
                  "SeedLab.Search.Tests, SeedLab.GoldenCheck, SeedLab.LocationLab, ...");
            ProbeTick gateTick = QuietMachineProbe.Evaluate(t0, t1, new[]
            {
                new ProcessCpu("SeedLab.Search.Tests", 3048, 0.9 * 5, QuietMachineProbe.Classify("SeedLab.Search.Tests")),
            }, 1, 0, 0, th);
            check(gateTick.Tainted && gateTick.Reasons[0].Contains("SeedLab.Search.Tests"),
                  "a single-threaded SeedLab test at 0.9 cores taints by being there, although it is under the 1-core limit",
                  string.Join("; ", gateTick.Reasons));

            // ---- processes whose CPU time cannot be read ------------------------------------------------
            ProbeTick hiddenGame = QuietMachineProbe.Evaluate(t0, t1, new[]
            {
                new ProcessCpu("valheim", 20, 0, WatchedKind.Game, measured: false),
            }, 1, 1, 0, th, machineForeignCoreSeconds: 0.1);
            check(hiddenGame.Tainted && hiddenGame.Reasons[0].Contains("Valheim"),
                  "a watched process whose CPU time is not readable still taints by being there (its name is kept)",
                  string.Join("; ", hiddenGame.Reasons));

            ProbeTick unknown = QuietMachineProbe.Evaluate(t0, t1, new[]
            {
                new ProcessCpu("explorer", 10, 0.2, WatchedKind.None),
                new ProcessCpu("MsMpEng", 21, 0, WatchedKind.None, measured: false),
            }, 2, 1, 0, th, machineForeignCoreSeconds: null);
            check(unknown.Tainted && unknown.Reasons[0].Contains("could not be measured"),
                  "unreadable processes with no whole-machine figure: the load is unknown, which taints - never 'quiet'",
                  string.Join("; ", unknown.Reasons));

            ProbeTick protectedBusy = QuietMachineProbe.Evaluate(t0, t1, new[]
            {
                new ProcessCpu("explorer", 10, 0.2, WatchedKind.None),
                new ProcessCpu("MsMpEng", 21, 0, WatchedKind.None, measured: false),
            }, 2, 1, 0, th, machineForeignCoreSeconds: 6.0);
            check(protectedBusy.Tainted && protectedBusy.Reasons[0].StartsWith("the machine as a whole", StringComparison.Ordinal)
                  && Math.Abs(protectedBusy.ForeignCores - 1.2) < 1e-9,
                  "an antivirus scan the process list cannot read (1.2 cores of whole-machine load, 0.04 readable) taints",
                  string.Join("; ", protectedBusy.Reasons));

            ProbeTick protectedIdle = QuietMachineProbe.Evaluate(t0, t1, new[]
            {
                new ProcessCpu("explorer", 10, 0.2, WatchedKind.None),
                new ProcessCpu("MsMpEng", 21, 0, WatchedKind.None, measured: false),
            }, 2, 1, 0, th, machineForeignCoreSeconds: 0.6);
            check(!protectedIdle.Tainted, "unreadable processes are no fault when the whole machine was under the limit",
                  protectedIdle.Tainted ? string.Join("; ", protectedIdle.Reasons) : "0.12 cores, whole machine");

            // ---- live ------------------------------------------------------------------------------
            QuietBaseline b = QuietMachineProbe.TakeBaseline(TimeSpan.FromMilliseconds(500));
            check(b.Tick.Processes > 0 && b.ForeignCoreSeconds >= 0,
                  "a live baseline lists this machine's processes and never throws",
                  b.Tick.Processes + " processes (" + b.Tick.Unreadable + " unreadable), "
                  + b.ForeignCoreSeconds.ToString("F2") + " foreign core-s in " + b.Seconds.ToString("F2") + " s");
            bool self = false;
            foreach (ProcessCpu p in b.Tick.Top) if (p.Pid == Environment.ProcessId) self = true;
            foreach (ProcessCpu p in b.Tick.Watched) if (p.Pid == Environment.ProcessId) self = true;
            check(!self, "the probe leaves its own process out (this test is itself a SeedLab.* program)", "pid " + Environment.ProcessId);

            // A process it is told to watch, started while it runs: 'ping' stands in for a foreign vseed.
            IReadOnlyList<(string, WatchedKind)> watch = new[] { ("PING", WatchedKind.SeedLab) };
            using (QuietMachineProbe probe = QuietMachineProbe.Start(TimeSpan.FromMilliseconds(200), watched: watch))
            {
                DateTime a = DateTime.UtcNow;
                using (Process? ping = StartPing())
                {
                    Thread.Sleep(900);
                    if (ping != null && !ping.HasExited) ping.Kill();
                }

                probe.Stop();
                bool tainted = probe.TaintedBetween(a, DateTime.UtcNow, out IReadOnlyList<string> reasons);
                bool pingSeen = false;
                foreach (string r in reasons) if (r.Contains("ping", StringComparison.OrdinalIgnoreCase)) pingSeen = true;
                check(tainted && pingSeen,
                      "a watched process running during the probe marks that time tainted, by name",
                      tainted ? string.Join("; ", reasons) : "not seen in " + probe.Ticks.Count + " samples");
                check(probe.Ticks.Count >= 3, "the probe samples on its interval and once more on Stop",
                      probe.Ticks.Count + " samples in ~1 s at 200 ms");
            }

            using (QuietMachineProbe probe = QuietMachineProbe.Start(TimeSpan.FromMilliseconds(200), watched: watch))
            {
                DateTime a = DateTime.UtcNow;
                using (Process? ping = StartPing())
                {
                    if (ping != null) probe.Exclude(ping.Id);
                    Thread.Sleep(900);
                    if (ping != null && !ping.HasExited) ping.Kill();
                    if (ping != null)
                    {
                        ping.WaitForExit();
                        probe.ChildExited(ping.Id, ping.TotalProcessorTime);
                    }
                }

                probe.Stop();
                bool tainted = probe.TaintedBetween(a, DateTime.UtcNow, out IReadOnlyList<string> reasons);
                bool pingSeen = false;
                foreach (string r in reasons) if (r.Contains("ping", StringComparison.OrdinalIgnoreCase)) pingSeen = true;
                check(!pingSeen, "a child the caller excluded does not taint the run (its own timing legs)",
                      pingSeen ? string.Join("; ", reasons) : "excluded");
            }

            // ---- an ended timing leg comes out of the whole-machine figure once, never twice -------------
            // A made-up machine that is 200 core-seconds busier at every reading, and a leg reported as
            // having used 100 core-seconds in all. Exactly one interval - the one it ended in - must read
            // ~100 foreign core-seconds and every other ~200: taking the leg out again later would make a
            // busy interval read 100, a false quiet. Once for a leg the listing never saw (a pid no process
            // has), once for a real child the listing saw while it ran ('ping', its end reported with a
            // stand-in total of 100 core-seconds).
            check(LegOnce(null, out string ghostSeen), "an ended timing leg the process list never saw is taken out of the whole machine once",
                  ghostSeen);
            check(LegOnce(StartPing(2), out string realSeen), "an ended timing leg the process list saw while it ran is taken out once too, "
                  + "in the interval it ended in, and never again", realSeen);

            // ---- time no sample covers is not quiet ----------------------------------------------------
            using (QuietMachineProbe probe = QuietMachineProbe.Start(TimeSpan.FromMilliseconds(200), watched: Array.Empty<(string, WatchedKind)>(),
                                                                     thresholds: new QuietThresholds { ForeignCoresMax = 1e6 },
                                                                     machine: () => TimeSpan.Zero))
            {
                Thread.Sleep(450);
                probe.Stop();
                probe.TaintedBetween(probe.StartedUtc, DateTime.UtcNow, out IReadOnlyList<string> covered);
                probe.TaintedBetween(probe.StartedUtc.AddSeconds(-5), DateTime.UtcNow, out IReadOnlyList<string> before);
                check(!new List<string>(covered).Contains(QuietMachineProbe.NotObserved) && new List<string>(before).Contains(QuietMachineProbe.NotObserved),
                      "time before the probe started (or a sample that failed) is reported as not observed, never as quiet",
                      "watched span: " + (covered.Count == 0 ? "clean" : string.Join("; ", covered)) + "; with 5 s before: "
                      + string.Join("; ", before));
            }

            // ---- a busy baseline does not raise the limit ------------------------------------------------
            // A made-up whole-machine reader: 1,000 core-seconds of load the process list cannot see.
            long calls = 0;
            MachineCpuReader heavy = () => TimeSpan.FromSeconds(1000.0 * Interlocked.Increment(ref calls));
            QuietBaseline busy = QuietMachineProbe.TakeBaseline(TimeSpan.FromMilliseconds(300),
                                                                watched: Array.Empty<(string, WatchedKind)>(), machine: heavy);
            using (QuietMachineProbe probe = QuietMachineProbe.Start(TimeSpan.FromSeconds(60), baseline: busy,
                                                                     watched: Array.Empty<(string, WatchedKind)>(), machine: () => TimeSpan.Zero))
            {
                probe.Stop();
                check(busy.Tainted && !probe.BaselineRaisesLimit && probe.LimitCores == th.ForeignCoresMax,
                      "a baseline that was itself busy does not raise the in-run limit (it stays at 1 core, not 3x its load)",
                      "baseline " + busy.ForeignCores.ToString("F0") + " cores, tainted " + busy.Tainted + "; limit " + probe.LimitCores);
            }

            QuietBaseline calm = QuietMachineProbe.TakeBaseline(TimeSpan.FromMilliseconds(300), new QuietThresholds { ForeignCoresMax = 1e6 },
                                                                Array.Empty<(string, WatchedKind)>(), () => TimeSpan.Zero);
            using (QuietMachineProbe probe = QuietMachineProbe.Start(TimeSpan.FromSeconds(60), baseline: calm,
                                                                     watched: Array.Empty<(string, WatchedKind)>(), machine: () => TimeSpan.Zero))
            {
                probe.Stop();
                check(!calm.Tainted && probe.BaselineRaisesLimit
                      && Math.Abs(probe.LimitCores - Math.Max(th.ForeignCoresMax, th.BaselineMultiple * calm.ForeignCores)) < 1e-12,
                      "a quiet baseline raises the limit to 3x its load (never below 1 core)",
                      "baseline " + calm.ForeignCores.ToString("F3") + " cores; limit " + probe.LimitCores.ToString("F3"));
            }

            // ---- the last, short sample is judged with the one before it ----------------------------------
            // Load appears only in the final reading: 1,000 core-seconds. Judged over its own 0.4 s or less
            // that is at least 2,500 cores; over the ~1.6 s of the sample before it plus itself, about 625.
            // The limit is set between the two, so the verdict says which window the rule used.
            QuietThresholds tail = new QuietThresholds { ForeignCoresMax = 1500 };
            ProbeTick? lastMerged = TailProbe(tail, out int samplesMerged);
            ProbeTick? lastAlone = TailProbe(new QuietThresholds { ForeignCoresMax = 1500, MinTickSeconds = 0.001 }, out _);
            bool mergedMachine = lastMerged != null && HasMachineReason(lastMerged);
            bool aloneMachine = lastAlone != null && HasMachineReason(lastAlone);
            check(lastMerged != null && lastAlone != null && !mergedMachine && aloneMachine,
                  "a final sample shorter than 1 s is judged together with the sample before it, so the ~15.6 ms steps "
                  + "CPU time is charged in cannot read as a burst (the same tail judged alone does)",
                  "merged: " + (lastMerged == null ? "no sample" : lastMerged.Seconds.ToString("F3") + " s, " + (mergedMachine ? "tainted" : "clean"))
                  + "; alone: " + (lastAlone == null ? "no sample" : lastAlone.Seconds.ToString("F3") + " s, " + (aloneMachine ? "tainted" : "clean"))
                  + "; " + samplesMerged + " samples");
        }

        private static bool HasMachineReason(ProbeTick t)
        {
            foreach (string r in t.Reasons) if (r.StartsWith("the machine as a whole", StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>
        /// A probe with one regular sample, then a stop at most ~0.4 s later, with a made-up machine
        /// reader that shows 1,000 core-seconds of load only in the final reading. Returns the final sample.
        /// </summary>
        private static ProbeTick? TailProbe(QuietThresholds thresholds, out int samples)
        {
            int jump = 0;
            MachineCpuReader reader = () => TimeSpan.FromSeconds(Volatile.Read(ref jump) == 1 ? 1000.0 : 0.0);
            using QuietMachineProbe probe = QuietMachineProbe.Start(TimeSpan.FromMilliseconds(1200), thresholds: thresholds,
                                                                    watched: Array.Empty<(string, WatchedKind)>(), machine: reader);
            // One regular sample at ~1.2 s (0.4 s to read ~400 processes on a busy machine), then stop at
            // ~1.6 s, before the loop's next sample at ~2.4 s.
            Thread.Sleep(1600);
            Volatile.Write(ref jump, 1);
            probe.Stop();
            IReadOnlyList<ProbeTick> ticks = probe.Ticks;
            samples = ticks.Count;
            return ticks.Count >= 2 ? ticks[ticks.Count - 1] : null;
        }

        /// <summary>
        /// Runs a probe with a made-up machine that is 200 core-seconds busier at every reading while a
        /// leg (<paramref name="child"/>, or a pid no process has when null) is excluded, then reported
        /// ended with a total of 100 core-seconds. True when exactly one sample took the 100 out.
        /// </summary>
        private static bool LegOnce(Process? child, out string seen)
        {
            long reads = 0;
            MachineCpuReader steady = () => TimeSpan.FromSeconds(200.0 * Interlocked.Increment(ref reads));
            List<string> values = new List<string>();
            int reduced = 0, full = 0, other = 0;
            double sum = 0;
            int count;
            using (QuietMachineProbe probe = QuietMachineProbe.Start(TimeSpan.FromMilliseconds(250), watched: Array.Empty<(string, WatchedKind)>(),
                                                                     machine: steady))
            {
                int pid = child?.Id ?? 0x7FFFFFF0;
                probe.Exclude(pid);
                if (child != null)
                {
                    child.WaitForExit();
                    child.Dispose();
                }

                probe.ChildExited(pid, TimeSpan.FromSeconds(100));
                Thread.Sleep(1300);
                probe.Stop();
                IReadOnlyList<ProbeTick> ticks = probe.Ticks;
                count = ticks.Count;
                foreach (ProbeTick t in ticks)
                {
                    double m = t.MachineCoreSeconds ?? double.NaN;
                    sum += m;
                    values.Add(m.ToString("F1"));
                    if (m > 95 && m <= 100.5) reduced++;
                    else if (m > 195 && m <= 200.5) full++;
                    else other++;
                }
            }

            seen = count + " samples, machine core-s per sample: " + string.Join(", ", values);
            return count >= 3 && reduced == 1 && other == 0 && Math.Abs(sum - (200.0 * count - 100)) < 2 * count;
        }

        private static Process? StartPing() => StartPing(5);

        private static Process? StartPing(int count)
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "ping.exe" : "ping",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(OperatingSystem.IsWindows() ? "-n" : "-c");
            psi.ArgumentList.Add(count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("127.0.0.1");
            try
            {
                return Process.Start(psi);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
