using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace SeedLab.Runtime.Execution
{
    /// <summary>Why a watched process matters to a measurement.</summary>
    public enum WatchedKind
    {
        /// <summary>Not watched: counted only in the foreign total.</summary>
        None = 0,

        /// <summary>Another vseed - two SeedLab runs share the cores and the memory bandwidth. Its presence taints.</summary>
        SeedLab = 1,

        /// <summary>Valheim itself. Its presence taints.</summary>
        Game = 2,

        /// <summary>A build or SDK process (dotnet, the Roslyn server, MSBuild). It taints when it burns CPU.</summary>
        DevTool = 3,
    }

    /// <summary>What decides that a stretch of time was not quiet enough to measure in.</summary>
    public sealed class QuietThresholds
    {
        /// <summary>All foreign processes together, averaged over a sample, in cores: above this the sample is tainted.</summary>
        public double ForeignCoresMax { get; init; } = 1.0;

        /// <summary>... unless the recorded baseline times this is larger - an idle desktop is not a fault.</summary>
        public double BaselineMultiple { get; init; } = 3.0;

        /// <summary>A watched build or SDK process using more than this many cores taints the sample.</summary>
        public double DevToolCoresMax { get; init; } = 0.10;

        public static QuietThresholds Default => new QuietThresholds();
    }

    /// <summary>One process's CPU over one interval.</summary>
    public sealed class ProcessCpu
    {
        public ProcessCpu(string name, int pid, double coreSeconds, WatchedKind watched)
        {
            Name = name;
            Pid = pid;
            CoreSeconds = coreSeconds;
            Watched = watched;
        }

        public string Name { get; }
        public int Pid { get; }

        /// <summary><c>TotalProcessorTime</c> delta over the interval: core-seconds, summed over its threads.</summary>
        public double CoreSeconds { get; }

        public WatchedKind Watched { get; }

        public override string ToString()
            => Name + " (pid " + Pid.ToString(CultureInfo.InvariantCulture) + ", "
               + CoreSeconds.ToString("F2", CultureInfo.InvariantCulture) + " core-s)";
    }

    /// <summary>One sampling interval and its verdict.</summary>
    public sealed class ProbeTick
    {
        internal ProbeTick(DateTime startUtc, DateTime endUtc, double foreignCoreSeconds, int processes,
                           int unreadable, IReadOnlyList<ProcessCpu> top, IReadOnlyList<ProcessCpu> watched,
                           IReadOnlyList<string> reasons)
        {
            StartUtc = startUtc;
            EndUtc = endUtc;
            ForeignCoreSeconds = foreignCoreSeconds;
            Processes = processes;
            Unreadable = unreadable;
            Top = top;
            Watched = watched;
            Reasons = reasons;
        }

        public DateTime StartUtc { get; }
        public DateTime EndUtc { get; }
        public double Seconds => (EndUtc - StartUtc).TotalSeconds;

        /// <summary>Every process but this one, Idle and the excluded children, summed.</summary>
        public double ForeignCoreSeconds { get; }

        /// <summary>Foreign cores on average over the interval.</summary>
        public double ForeignCores => Seconds > 0 ? ForeignCoreSeconds / Seconds : 0;

        public int Processes { get; }

        /// <summary>Processes whose CPU time Windows would not tell this account (protected services).</summary>
        public int Unreadable { get; }

        /// <summary>The busiest foreign processes, up to five.</summary>
        public IReadOnlyList<ProcessCpu> Top { get; }

        /// <summary>Every watched process present, whatever it used.</summary>
        public IReadOnlyList<ProcessCpu> Watched { get; }

        /// <summary>Why the interval is tainted; empty when it was quiet.</summary>
        public IReadOnlyList<string> Reasons { get; }

        public bool Tainted => Reasons.Count > 0;
    }

    /// <summary>A timed look at what the machine does when SeedLab is not measuring.</summary>
    public sealed class QuietBaseline
    {
        internal QuietBaseline(ProbeTick tick) { Tick = tick; }

        public ProbeTick Tick { get; }
        public double Seconds => Tick.Seconds;

        /// <summary>Foreign core-seconds over the whole baseline.</summary>
        public double ForeignCoreSeconds => Tick.ForeignCoreSeconds;

        public double ForeignCores => Tick.ForeignCores;

        /// <summary>The baseline was itself tainted (a foreign vseed, the game, a build).</summary>
        public bool Tainted => Tick.Tainted;
    }

    /// <summary>
    /// Is the machine quiet enough for a timing to mean anything? <c>docs\measurements.md</c>'s method,
    /// as a class: per-process <see cref="Process.TotalProcessorTime"/> deltas over a fixed interval,
    /// summed over everything except this process and Idle - NOT the processor-total performance
    /// counter, which that file shows reading a steady 50 % while the real foreign load was one other
    /// vseed.
    ///
    /// <para><b>What it does with the answer.</b> Nothing but say it. A run it watched is marked
    /// tainted, with the processes and the numbers that tainted it, rather than stopped or silently
    /// trusted: the caller decides whether to discard and repeat (the measurement rules do). A foreign
    /// vseed or a running Valheim taints by being there at all; a build tool (dotnet, the Roslyn
    /// server, MSBuild) taints when it uses more than a tenth of a core; anything else taints only in
    /// total, above <see cref="QuietThresholds.ForeignCoresMax"/> or three times the recorded
    /// baseline.</para>
    ///
    /// <para><b>Its own cost.</b> One pass over the process list every interval (5 s by default) on a
    /// background thread - tens of milliseconds, well under 1 % of one core. Processes Windows will not
    /// report on are counted as unreadable rather than guessed at. BCL only, no P/Invoke.</para>
    ///
    /// <para><b>Children.</b> A process this one starts (a timing leg of its own) would look like a
    /// foreign vseed; <see cref="Exclude"/> it as soon as it has started. A sample taken in the
    /// microseconds between the start and the exclusion can still see it - that is a false taint, never
    /// a false "quiet".</para>
    /// </summary>
    public sealed class QuietMachineProbe : IDisposable
    {
        /// <summary>The default sampling interval.</summary>
        public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

        /// <summary>The process names watched by default (without ".exe"), matched case-insensitively.</summary>
        public static IReadOnlyList<(string Name, WatchedKind Kind)> DefaultWatched { get; } = new[]
        {
            ("vseed", WatchedKind.SeedLab),
            ("valheim", WatchedKind.Game),
            ("dotnet", WatchedKind.DevTool),
            ("VBCSCompiler", WatchedKind.DevTool),
            ("MSBuild", WatchedKind.DevTool),
        };

        private readonly TimeSpan _interval;
        private readonly QuietThresholds _thresholds;
        private readonly IReadOnlyList<(string Name, WatchedKind Kind)> _watched;
        private readonly double _baselineCores;
        private readonly HashSet<int> _excluded = new HashSet<int>();
        private readonly List<ProbeTick> _ticks = new List<ProbeTick>();
        private readonly object _gate = new object();
        private readonly ManualResetEventSlim _stop = new ManualResetEventSlim(false);
        private readonly Thread _thread;
        private Dictionary<int, (string Name, TimeSpan Cpu)> _last;
        private DateTime _lastUtc;
        private bool _stopped;

        private QuietMachineProbe(TimeSpan interval, QuietBaseline? baseline, QuietThresholds? thresholds,
                                  IReadOnlyList<(string, WatchedKind)>? watched)
        {
            _interval = interval <= TimeSpan.Zero ? DefaultInterval : interval;
            _thresholds = thresholds ?? QuietThresholds.Default;
            _watched = watched ?? DefaultWatched;
            _baselineCores = baseline?.ForeignCores ?? 0;
            Baseline = baseline;
            _last = Snapshot(out _);
            _lastUtc = DateTime.UtcNow;
            _thread = new Thread(Loop) { IsBackground = true, Name = "quiet-machine-probe", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        /// <summary>The baseline the probe compares against, if one was taken.</summary>
        public QuietBaseline? Baseline { get; }

        public QuietThresholds Thresholds => _thresholds;

        public TimeSpan Interval => _interval;

        /// <summary>
        /// Starts sampling now, every <paramref name="interval"/>, until <see cref="Stop"/> or
        /// <see cref="Dispose"/>.
        /// </summary>
        public static QuietMachineProbe Start(TimeSpan? interval = null, QuietBaseline? baseline = null,
                                              QuietThresholds? thresholds = null,
                                              IReadOnlyList<(string, WatchedKind)>? watched = null)
            => new QuietMachineProbe(interval ?? DefaultInterval, baseline, thresholds, watched);

        /// <summary>
        /// Watches the machine for <paramref name="duration"/> (blocking) and reports what it did. The
        /// measurement rules take 30 s before every timed step; a smoke run may take less and must say so.
        /// </summary>
        public static QuietBaseline TakeBaseline(TimeSpan duration, QuietThresholds? thresholds = null,
                                                 IReadOnlyList<(string, WatchedKind)>? watched = null)
        {
            IReadOnlyList<(string Name, WatchedKind Kind)> w = watched ?? DefaultWatched;
            DateTime t0 = DateTime.UtcNow;
            Dictionary<int, (string Name, TimeSpan Cpu)> a = Snapshot(out _);
            if (duration > TimeSpan.Zero) Thread.Sleep(duration);
            Dictionary<int, (string Name, TimeSpan Cpu)> b = Snapshot(out int unreadable);
            DateTime t1 = DateTime.UtcNow;
            ProbeTick tick = Evaluate(t0, t1, Deltas(a, b, new HashSet<int>(), w), b.Count + unreadable, unreadable,
                                      0, thresholds ?? QuietThresholds.Default);
            return new QuietBaseline(tick);
        }

        /// <summary>Leaves process <paramref name="pid"/> out of every later sample (a child this process started).</summary>
        public void Exclude(int pid)
        {
            lock (_gate) _excluded.Add(pid);
        }

        /// <summary>Every sample so far, oldest first.</summary>
        public IReadOnlyList<ProbeTick> Ticks
        {
            get { lock (_gate) return _ticks.ToArray(); }
        }

        /// <summary>
        /// Whether any sample overlapping [<paramref name="t0Utc"/>, <paramref name="t1Utc"/>] was
        /// tainted, and every distinct reason. Call <see cref="Stop"/> first to include the time since the
        /// last sample.
        /// </summary>
        public bool TaintedBetween(DateTime t0Utc, DateTime t1Utc, out IReadOnlyList<string> reasons)
        {
            List<string> r = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ProbeTick t in Ticks)
            {
                if (t.EndUtc < t0Utc || t.StartUtc > t1Utc) continue;
                foreach (string s in t.Reasons)
                {
                    if (seen.Add(s)) r.Add(s);
                }
            }

            reasons = r;
            return r.Count > 0;
        }

        /// <summary>Every watched process seen during the probe, once each: "vseed (pid 1234)".</summary>
        public IReadOnlyList<string> WatchedSeen()
        {
            List<string> r = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ProbeTick t in Ticks)
            {
                foreach (ProcessCpu p in t.Watched)
                {
                    string s = p.Name + " (pid " + p.Pid.ToString(CultureInfo.InvariantCulture) + ")";
                    if (seen.Add(s)) r.Add(s);
                }
            }

            return r;
        }

        /// <summary>Takes a last sample covering the time since the previous one, and stops.</summary>
        public void Stop()
        {
            lock (_gate)
            {
                if (_stopped) return;
                _stopped = true;
            }

            _stop.Set();
            _thread.Join();
            Sample();
        }

        public void Dispose()
        {
            Stop();
            _stop.Dispose();
        }

        private void Loop()
        {
            while (!_stop.Wait(_interval))
            {
                Sample();
            }
        }

        private void Sample()
        {
            try
            {
                Dictionary<int, (string Name, TimeSpan Cpu)> now = Snapshot(out int unreadable);
                DateTime nowUtc = DateTime.UtcNow;
                HashSet<int> excluded;
                lock (_gate) excluded = new HashSet<int>(_excluded);
                ProbeTick tick = Evaluate(_lastUtc, nowUtc, Deltas(_last, now, excluded, _watched),
                                          now.Count + unreadable, unreadable, _baselineCores, _thresholds);
                lock (_gate) _ticks.Add(tick);
                _last = now;
                _lastUtc = nowUtc;
            }
            catch (Exception)
            {
                // A probe that throws would take the measurement down with it; a missed sample is only a
                // gap, and a gap is reported as such by the caller (no tick covers it).
            }
        }

        /// <summary>
        /// Every readable process's CPU time right now, by pid, without this process and Idle.
        /// </summary>
        private static Dictionary<int, (string Name, TimeSpan Cpu)> Snapshot(out int unreadable)
        {
            unreadable = 0;
            int self = Environment.ProcessId;
            Dictionary<int, (string, TimeSpan)> d = new Dictionary<int, (string, TimeSpan)>();
            Process[] all;
            try
            {
                all = Process.GetProcesses();
            }
            catch (Exception)
            {
                return d;
            }

            foreach (Process p in all)
            {
                try
                {
                    if (p.Id == 0 || p.Id == self) continue;
                    d[p.Id] = (p.ProcessName, p.TotalProcessorTime);
                }
                catch (Exception)
                {
                    // Access denied (a protected service) or the process exited between the listing and
                    // the read. Counted, never guessed at.
                    unreadable++;
                }
                finally
                {
                    p.Dispose();
                }
            }

            return d;
        }

        private static List<ProcessCpu> Deltas(Dictionary<int, (string Name, TimeSpan Cpu)> before,
                                               Dictionary<int, (string Name, TimeSpan Cpu)> after,
                                               HashSet<int> excluded,
                                               IReadOnlyList<(string Name, WatchedKind Kind)> watched)
        {
            List<ProcessCpu> list = new List<ProcessCpu>(after.Count);
            foreach (KeyValuePair<int, (string Name, TimeSpan Cpu)> kv in after)
            {
                if (excluded.Contains(kv.Key)) continue;
                // A process first seen in this interval started inside it (or became readable): all of its
                // time is counted, which can only over-count the foreign load, never hide it.
                TimeSpan start = before.TryGetValue(kv.Key, out (string Name, TimeSpan Cpu) b)
                                 && string.Equals(b.Name, kv.Value.Name, StringComparison.Ordinal)
                    ? b.Cpu
                    : TimeSpan.Zero;
                double cs = Math.Max(0, (kv.Value.Cpu - start).TotalSeconds);
                list.Add(new ProcessCpu(kv.Value.Name, kv.Key, cs, Classify(kv.Value.Name, watched)));
            }

            return list;
        }

        /// <summary>The watched kind of a process name (no ".exe"), case-insensitive.</summary>
        public static WatchedKind Classify(string processName, IReadOnlyList<(string Name, WatchedKind Kind)>? watched = null)
        {
            foreach ((string name, WatchedKind kind) in watched ?? DefaultWatched)
            {
                if (string.Equals(processName, name, StringComparison.OrdinalIgnoreCase)) return kind;
            }

            return WatchedKind.None;
        }

        /// <summary>
        /// The verdict on one interval. Pure - the tests drive it with made-up processes.
        /// </summary>
        public static ProbeTick Evaluate(DateTime startUtc, DateTime endUtc, IReadOnlyList<ProcessCpu> processes,
                                         int processCount, int unreadable, double baselineCores,
                                         QuietThresholds thresholds)
        {
            double seconds = Math.Max(1e-6, (endUtc - startUtc).TotalSeconds);
            double total = 0;
            List<ProcessCpu> watched = new List<ProcessCpu>();
            List<string> reasons = new List<string>();
            foreach (ProcessCpu p in processes)
            {
                total += p.CoreSeconds;
                if (p.Watched == WatchedKind.None) continue;
                watched.Add(p);
                double cores = p.CoreSeconds / seconds;
                // Reasons are worded without the sample's own numbers, so the same cause seen in many
                // samples is one line in a report; the numbers stay on the tick (Top, Watched, ForeignCores).
                string who = p.Name + " (pid " + p.Pid.ToString(CultureInfo.InvariantCulture) + ")";
                switch (p.Watched)
                {
                    case WatchedKind.SeedLab:
                        reasons.Add("another vseed was running: " + who);
                        break;
                    case WatchedKind.Game:
                        reasons.Add("Valheim was running: " + who);
                        break;
                    case WatchedKind.DevTool when cores > thresholds.DevToolCoresMax:
                        reasons.Add("a build tool was busy: " + who + " used more than "
                                    + thresholds.DevToolCoresMax.ToString("0.##", CultureInfo.InvariantCulture) + " cores");
                        break;
                }
            }

            double limit = Math.Max(thresholds.ForeignCoresMax, thresholds.BaselineMultiple * baselineCores);
            if (total / seconds > limit)
            {
                reasons.Add("other processes together used more than " + limit.ToString("0.##", CultureInfo.InvariantCulture)
                            + " cores on average");
            }

            List<ProcessCpu> top = new List<ProcessCpu>(processes);
            top.Sort((a, b) => b.CoreSeconds.CompareTo(a.CoreSeconds));
            if (top.Count > 5) top.RemoveRange(5, top.Count - 5);
            return new ProbeTick(startUtc, endUtc, total, processCount, unreadable, top, watched, reasons);
        }
    }
}
