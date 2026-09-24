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
    /// watch while that process runs.
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
            check(vseed.Tainted && vseed.Reasons[0].Contains("another vseed"),
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
            check(!noisyDesk.Tainted, "the limit rises to 3x a recorded baseline (1.4 cores < 3 x 0.6)",
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

            // ---- live ------------------------------------------------------------------------------
            QuietBaseline b = QuietMachineProbe.TakeBaseline(TimeSpan.FromMilliseconds(500));
            check(b.Tick.Processes > 0 && b.ForeignCoreSeconds >= 0,
                  "a live baseline lists this machine's processes and never throws",
                  b.Tick.Processes + " processes (" + b.Tick.Unreadable + " unreadable), "
                  + b.ForeignCoreSeconds.ToString("F2") + " foreign core-s in " + b.Seconds.ToString("F2") + " s");
            bool self = false;
            foreach (ProcessCpu p in b.Tick.Top) if (p.Pid == Environment.ProcessId) self = true;
            check(!self, "the probe leaves its own process out", "pid " + Environment.ProcessId);

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
                check(tainted && reasons.Count > 0 && reasons[0].Contains("ping", StringComparison.OrdinalIgnoreCase),
                      "a watched process running during the probe marks that time tainted, by name",
                      tainted ? reasons[0] : "not seen in " + probe.Ticks.Count + " samples");
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
                }

                probe.Stop();
                bool tainted = probe.TaintedBetween(a, DateTime.UtcNow, out IReadOnlyList<string> reasons);
                bool pingSeen = false;
                foreach (string r in reasons) if (r.Contains("ping", StringComparison.OrdinalIgnoreCase)) pingSeen = true;
                check(!pingSeen, "a child the caller excluded does not taint the run (its own timing legs)",
                      pingSeen ? string.Join("; ", reasons) : "excluded");
            }
        }

        private static Process? StartPing()
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "ping.exe" : "ping",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(OperatingSystem.IsWindows() ? "-n" : "-c");
            psi.ArgumentList.Add("5");
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
