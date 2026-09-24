using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace SeedLab.Runtime.Execution
{
    /// <summary>Why a watched process matters to a measurement.</summary>
    public enum WatchedKind
    {
        /// <summary>Not watched: counted only in the foreign total.</summary>
        None = 0,

        /// <summary>
        /// Another SeedLab program - vseed, or one of the repository's gate and test executables, which
        /// run the same world generation. Two SeedLab runs share the cores and the memory bandwidth. Its
        /// presence taints.
        /// </summary>
        SeedLab = 1,

        /// <summary>Valheim itself. Its presence taints.</summary>
        Game = 2,

        /// <summary>A build or SDK process (dotnet, the Roslyn server, MSBuild). It taints when it burns CPU.</summary>
        DevTool = 3,
    }

    /// <summary>
    /// The machine's cumulative busy processor time since it started, summed over every logical
    /// processor, or null when it cannot be read. The Windows reading needs a system call this layer
    /// may not make (no P/Invoke in the runtime layer), so the host supplies it; see
    /// <see cref="MachineCpuTime"/> for the part that needs none.
    /// </summary>
    public delegate TimeSpan? MachineCpuReader();

    /// <summary>What decides that a stretch of time was not quiet enough to measure in.</summary>
    public sealed class QuietThresholds
    {
        /// <summary>All foreign processes together, averaged over a sample, in cores: above this the sample is tainted.</summary>
        public double ForeignCoresMax { get; init; } = 1.0;

        /// <summary>
        /// ... unless a recorded baseline times this is larger - an idle desktop is not a fault. Only a
        /// baseline that was itself quiet raises the limit; a busy one would raise it to hide its own load.
        /// </summary>
        public double BaselineMultiple { get; init; } = 3.0;

        /// <summary>A watched build or SDK process using more than this many cores taints the sample.</summary>
        public double DevToolCoresMax { get; init; } = 0.10;

        /// <summary>
        /// The shortest interval the per-second rules are applied to on their own. Windows charges a
        /// process's CPU time in clock-tick steps of about 15.6 ms, so one idle dotnet charged one step
        /// inside a 0.1 s interval reads as 0.16 cores. The last sample a probe takes when it stops can be
        /// that short, and is judged together with the sample before it instead.
        /// </summary>
        public double MinTickSeconds { get; init; } = 1.0;

        public static QuietThresholds Default => new QuietThresholds();
    }

    /// <summary>One process's CPU over one interval.</summary>
    public sealed class ProcessCpu
    {
        public ProcessCpu(string name, int pid, double coreSeconds, WatchedKind watched, bool measured = true)
        {
            Name = name;
            Pid = pid;
            CoreSeconds = measured ? coreSeconds : 0;
            Watched = watched;
            Measured = measured;
        }

        public string Name { get; }
        public int Pid { get; }

        /// <summary><c>TotalProcessorTime</c> delta over the interval: core-seconds, summed over its threads. 0 when not measured.</summary>
        public double CoreSeconds { get; }

        public WatchedKind Watched { get; }

        /// <summary>
        /// False when the operating system would not give this account the process's CPU time (a
        /// protected service, an elevated process). Its name is still known, so a watched one still taints
        /// by being there; its load is only in the whole-machine figure.
        /// </summary>
        public bool Measured { get; }

        public override string ToString()
            => Name + " (pid " + Pid.ToString(CultureInfo.InvariantCulture) + ", "
               + (Measured ? CoreSeconds.ToString("F2", CultureInfo.InvariantCulture) + " core-s)" : "CPU time not readable)");
    }

    /// <summary>One sampling interval and its verdict.</summary>
    public sealed class ProbeTick
    {
        internal ProbeTick(DateTime startUtc, DateTime endUtc, double processCoreSeconds, double? machineCoreSeconds,
                           int processes, int unreadable, IReadOnlyList<ProcessCpu> top, IReadOnlyList<ProcessCpu> watched,
                           IReadOnlyList<string> reasons, double limitCores)
        {
            StartUtc = startUtc;
            EndUtc = endUtc;
            ProcessCoreSeconds = processCoreSeconds;
            MachineCoreSeconds = machineCoreSeconds;
            Processes = processes;
            Unreadable = unreadable;
            Top = top;
            Watched = watched;
            Reasons = reasons;
            LimitCores = limitCores;
        }

        public DateTime StartUtc { get; }
        public DateTime EndUtc { get; }
        public double Seconds => (EndUtc - StartUtc).TotalSeconds;

        /// <summary>Every process whose CPU time could be read, but this one, Idle and the excluded children, summed.</summary>
        public double ProcessCoreSeconds { get; }

        /// <summary>
        /// The whole machine's busy time over the interval less this process's and its excluded
        /// children's, when the machine's total could be read: it includes the processes whose own time
        /// could not be. Null when it could not be read.
        /// </summary>
        public double? MachineCoreSeconds { get; }

        /// <summary>
        /// The foreign load the verdict uses: the larger of the two figures above, so neither a protected
        /// process nor a process that came and went between two samples can make the machine look idle.
        /// </summary>
        public double ForeignCoreSeconds => MachineCoreSeconds.HasValue ? Math.Max(ProcessCoreSeconds, MachineCoreSeconds.Value) : ProcessCoreSeconds;

        /// <summary>Foreign cores on average over the interval.</summary>
        public double ForeignCores => Seconds > 0 ? ForeignCoreSeconds / Seconds : 0;

        public int Processes { get; }

        /// <summary>Processes whose CPU time the operating system would not tell this account (protected services).</summary>
        public int Unreadable { get; }

        /// <summary>The busiest foreign processes whose time could be read, up to five.</summary>
        public IReadOnlyList<ProcessCpu> Top { get; }

        /// <summary>Every watched process present, whatever it used, whether or not its time could be read.</summary>
        public IReadOnlyList<ProcessCpu> Watched { get; }

        /// <summary>Why the interval is tainted; empty when it was quiet.</summary>
        public IReadOnlyList<string> Reasons { get; }

        /// <summary>The foreign-load limit this interval was judged against, in cores.</summary>
        public double LimitCores { get; }

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

        /// <summary>The baseline was itself tainted (another SeedLab program, the game, a build, load, or no view of it).</summary>
        public bool Tainted => Tick.Tainted;
    }

    /// <summary>
    /// Is the machine quiet enough for a timing to mean anything? <c>docs\measurements.md</c>'s method,
    /// as a class: per-process <see cref="Process.TotalProcessorTime"/> deltas over a fixed interval,
    /// summed over everything except this process and Idle - NOT the processor-total performance
    /// counter, which that file shows reading a steady 50 % while the real foreign load was one other
    /// vseed.
    ///
    /// <para><b>What the per-process view cannot see.</b> A process started by another account or with
    /// higher rights will not tell this one its CPU time: on a normal desktop that is about a third of
    /// the list, and it includes exactly the bursty background users - the antivirus scanning a file
    /// just built, the search indexer, Windows Update, memory compression. Such a process is kept by
    /// name, so a watched one still taints by being there, and its load is caught by a second figure:
    /// the whole machine's busy time (from the host's <see cref="MachineCpuReader"/>) less this process
    /// and its excluded children. The verdict uses the larger of the two. Where no whole-machine figure
    /// exists and some process could not be measured, the load is unknown, and that taints too.</para>
    ///
    /// <para><b>What it does with the answer.</b> Nothing but say it. A run it watched is marked
    /// tainted, with the processes and the numbers that tainted it, rather than stopped or silently
    /// trusted: the caller decides whether to discard and repeat (the measurement rules do). Another
    /// SeedLab program or a running Valheim taints by being there at all; a build tool (dotnet, the
    /// Roslyn server, MSBuild) taints when it uses more than a tenth of a core; anything else taints
    /// only in total, above <see cref="QuietThresholds.ForeignCoresMax"/> or three times a quiet
    /// recorded baseline. A sample that could not be taken is a gap, and a stretch of time no sample
    /// covers is reported as not observed - a false taint, never a false quiet.</para>
    ///
    /// <para><b>Its own cost.</b> One pass over the process list every interval (5 s by default) on a
    /// background thread - tens of milliseconds, well under 1 % of one core. BCL only, no P/Invoke: the
    /// whole-machine figure comes from the host.</para>
    ///
    /// <para><b>Children.</b> A process this one starts (a timing leg of its own) would look like
    /// another SeedLab program; <see cref="Exclude"/> it as soon as it has started, and report its final
    /// CPU time with <see cref="ChildExited"/> when it ends, so the whole-machine figure can take its
    /// time out exactly. A sample taken in the microseconds between the start and the exclusion can
    /// still see it - that is a false taint, never a false "quiet".</para>
    /// </summary>
    public sealed class QuietMachineProbe : IDisposable
    {
        /// <summary>The default sampling interval.</summary>
        public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

        /// <summary>
        /// The process names watched by default (without ".exe"), matched case-insensitively; a name
        /// ending in '*' matches every name that starts with what comes before it. "SeedLab.*" is the
        /// repository's own gate and test executables (SeedLab.Acceptance.Tests, SeedLab.GoldenCheck,
        /// SeedLab.LocationLab, ...), which build worlds the way vseed does.
        /// </summary>
        public static IReadOnlyList<(string Name, WatchedKind Kind)> DefaultWatched { get; } = new[]
        {
            ("vseed", WatchedKind.SeedLab),
            ("SeedLab.*", WatchedKind.SeedLab),
            ("valheim", WatchedKind.Game),
            ("dotnet", WatchedKind.DevTool),
            ("VBCSCompiler", WatchedKind.DevTool),
            ("MSBuild", WatchedKind.DevTool),
        };

        private readonly TimeSpan _interval;
        private readonly QuietThresholds _thresholds;
        private readonly IReadOnlyList<(string Name, WatchedKind Kind)> _watched;
        private readonly MachineCpuReader? _machine;
        private readonly double _baselineCores;
        private readonly HashSet<int> _excluded = new HashSet<int>();
        private readonly Dictionary<int, TimeSpan> _exitedCpu = new Dictionary<int, TimeSpan>();
        private readonly List<ProbeTick> _ticks = new List<ProbeTick>();
        private readonly object _gate = new object();
        private readonly ManualResetEventSlim _stop = new ManualResetEventSlim(false);
        private readonly Thread _thread;

        // Only the sampling thread touches these (and Stop, after that thread has ended).
        private Reading _last;
        private Reading? _beforeLast;
        private bool _stopped;

        private QuietMachineProbe(TimeSpan interval, QuietBaseline? baseline, QuietThresholds? thresholds,
                                  IReadOnlyList<(string, WatchedKind)>? watched, MachineCpuReader? machine)
        {
            _interval = interval <= TimeSpan.Zero ? DefaultInterval : interval;
            _thresholds = thresholds ?? QuietThresholds.Default;
            _watched = watched ?? DefaultWatched;
            _machine = machine ?? MachineCpuTime.Default;
            // A baseline that was itself busy must not raise the limit it would then pass under.
            _baselineCores = baseline != null && !baseline.Tainted ? baseline.ForeignCores : 0;
            Baseline = baseline;
            // A first reading that fails leaves an empty one: every process then counts its whole life in
            // the first sample, which can only taint.
            _last = Read(_machine, NoPids, NoTimes, null) ?? new Reading(DateTime.UtcNow, null, null, TimeSpan.Zero, null);
            StartedUtc = _last.Utc;
            _thread = new Thread(Loop) { IsBackground = true, Name = "quiet-machine-probe", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        /// <summary>The baseline the probe compares against, if one was taken.</summary>
        public QuietBaseline? Baseline { get; }

        /// <summary>Whether <see cref="Baseline"/> raised the limit (only a quiet one does).</summary>
        public bool BaselineRaisesLimit => Baseline != null && !Baseline.Tainted;

        /// <summary>The foreign-load limit in force, in cores.</summary>
        public double LimitCores => Limit(_baselineCores, _thresholds);

        public QuietThresholds Thresholds => _thresholds;

        public TimeSpan Interval => _interval;

        /// <summary>When the probe started watching. Time before it is not covered by any sample.</summary>
        public DateTime StartedUtc { get; }

        /// <summary>Whether a whole-machine CPU figure was available when the probe started.</summary>
        public bool SeesWholeMachine => _last.MachineBusy.HasValue;

        /// <summary>
        /// Starts sampling now, every <paramref name="interval"/>, until <see cref="Stop"/> or
        /// <see cref="Dispose"/>. <paramref name="machine"/> reads the whole machine's CPU time; without
        /// one, <see cref="MachineCpuTime.Default"/> is used (Linux only).
        /// </summary>
        public static QuietMachineProbe Start(TimeSpan? interval = null, QuietBaseline? baseline = null,
                                              QuietThresholds? thresholds = null,
                                              IReadOnlyList<(string, WatchedKind)>? watched = null,
                                              MachineCpuReader? machine = null)
            => new QuietMachineProbe(interval ?? DefaultInterval, baseline, thresholds, watched, machine);

        /// <summary>
        /// Watches the machine for <paramref name="duration"/> (blocking) and reports what it did. The
        /// measurement rules take 30 s before every timed step; a smoke run may take less and must say so.
        /// A baseline whose process list could not be read at either end is tainted: nothing was seen.
        /// </summary>
        public static QuietBaseline TakeBaseline(TimeSpan duration, QuietThresholds? thresholds = null,
                                                 IReadOnlyList<(string, WatchedKind)>? watched = null,
                                                 MachineCpuReader? machine = null)
        {
            IReadOnlyList<(string Name, WatchedKind Kind)> w = watched ?? DefaultWatched;
            QuietThresholds th = thresholds ?? QuietThresholds.Default;
            MachineCpuReader? m = machine ?? MachineCpuTime.Default;
            DateTime t0 = DateTime.UtcNow;
            Reading? a = Read(m, NoPids, NoTimes, null);
            if (duration > TimeSpan.Zero) Thread.Sleep(duration);
            Reading? b = Read(m, NoPids, NoTimes, a);
            DateTime t1 = DateTime.UtcNow;
            if (a == null || b == null)
            {
                return new QuietBaseline(new ProbeTick(t0, t1, 0, null, 0, 0, Array.Empty<ProcessCpu>(), Array.Empty<ProcessCpu>(),
                                                       new[] { NotObserved }, Limit(0, th)));
            }

            return new QuietBaseline(Judge(a, b, NoPids, w, 0, th));
        }

        private static readonly HashSet<int> NoPids = new HashSet<int>();
        private static readonly Dictionary<int, TimeSpan> NoTimes = new Dictionary<int, TimeSpan>();

        /// <summary>Leaves process <paramref name="pid"/> out of every later sample (a child this process started).</summary>
        public void Exclude(int pid)
        {
            lock (_gate) _excluded.Add(pid);
        }

        /// <summary>
        /// An excluded child has ended, having used <paramref name="totalCpu"/> in all. The next reading
        /// records that total as the child's time, so the interval it ended in takes out exactly what it
        /// used there and no later interval takes it out again; once the total is in a reading and the
        /// pid is gone from two listings in a row, the pid is forgotten, so a later process that is given
        /// the same pid is watched again.
        /// </summary>
        public void ChildExited(int pid, TimeSpan totalCpu)
        {
            lock (_gate)
            {
                if (_excluded.Contains(pid)) _exitedCpu[pid] = totalCpu;
            }
        }

        /// <summary>Every sample so far, oldest first.</summary>
        public IReadOnlyList<ProbeTick> Ticks
        {
            get { lock (_gate) return _ticks.ToArray(); }
        }

        /// <summary>
        /// Whether any sample overlapping [<paramref name="t0Utc"/>, <paramref name="t1Utc"/>] was
        /// tainted, and every distinct reason. Time in that window that no sample covers (before the
        /// probe started, or a sample that could not be taken) is itself a reason: nothing was seen
        /// then. Call <see cref="Stop"/> first to include the time since the last sample.
        /// </summary>
        public bool TaintedBetween(DateTime t0Utc, DateTime t1Utc, out IReadOnlyList<string> reasons)
        {
            List<string> r = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            IReadOnlyList<ProbeTick> ticks = Ticks;
            DateTime covered = t0Utc;
            bool gap = false;
            foreach (ProbeTick t in ticks)
            {
                if (t.EndUtc < t0Utc || t.StartUtc > t1Utc) continue;
                if (t.StartUtc - covered > CoverageSlack) gap = true;
                if (t.EndUtc > covered) covered = t.EndUtc;
                foreach (string s in t.Reasons)
                {
                    if (seen.Add(s)) r.Add(s);
                }
            }

            if (t1Utc - covered > CoverageSlack) gap = true;
            if (gap && seen.Add(NotObserved)) r.Add(NotObserved);
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

        /// <summary>
        /// Takes a last sample covering the time since the previous one, and stops. A last interval
        /// shorter than <see cref="QuietThresholds.MinTickSeconds"/> is judged together with the one
        /// before it (whose own verdict is kept as well): CPU time is charged in steps too coarse for a
        /// per-second rate over a few milliseconds.
        /// </summary>
        public void Stop()
        {
            lock (_gate)
            {
                if (_stopped) return;
                _stopped = true;
            }

            _stop.Set();
            _thread.Join();
            Sample(final: true);
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
                Sample(final: false);
            }
        }

        private void Sample(bool final)
        {
            try
            {
                HashSet<int> excluded;
                Dictionary<int, TimeSpan> exited;
                lock (_gate)
                {
                    excluded = new HashSet<int>(_excluded);
                    exited = new Dictionary<int, TimeSpan>(_exitedCpu);
                }

                Reading? now = Read(_machine, excluded, exited, _last);
                // The process list could not be read: a gap, not a quiet sample. The previous reading
                // stays, so the next sample that works covers the gap and judges all of it.
                if (now == null) return;

                ProbeTick tick;
                lock (_gate)
                {
                    ProbeTick? previous = _ticks.Count > 0 ? _ticks[_ticks.Count - 1] : null;
                    bool shortTail = final && _beforeLast != null && previous != null
                                     && (now.Utc - _last.Utc).TotalSeconds < _thresholds.MinTickSeconds;
                    if (shortTail)
                    {
                        // Judged over [the previous sample's start, now]; the previous sample stays too,
                        // so nothing it found is lost. The merged sample replaces only the short tail.
                        ProbeTick merged = Judge(_beforeLast!, now, excluded, _watched, _baselineCores, _thresholds);
                        ProbeTick own = Judge(_last, now, excluded, _watched, _baselineCores, _thresholds);
                        tick = new ProbeTick(_last.Utc, now.Utc, own.ProcessCoreSeconds, own.MachineCoreSeconds, own.Processes,
                                             own.Unreadable, own.Top, own.Watched, WithoutRates(own.Reasons, merged.Reasons), own.LimitCores);
                    }
                    else
                    {
                        tick = Judge(_last, now, excluded, _watched, _baselineCores, _thresholds);
                    }

                    _ticks.Add(tick);

                    // A child whose end was reported is forgotten once its total is in a reading and it is
                    // gone from this listing and the one before: every window judged from here on either
                    // starts after its end or already has its total at both ends.
                    foreach (KeyValuePair<int, TimeSpan> kv in exited)
                    {
                        if (now.Processes.ContainsKey(kv.Key) || _last.Processes.ContainsKey(kv.Key)) continue;
                        if (!now.Children.TryGetValue(kv.Key, out TimeSpan recorded) || recorded != kv.Value) continue;
                        _excluded.Remove(kv.Key);
                        _exitedCpu.Remove(kv.Key);
                    }
                }

                _beforeLast = _last;
                _last = now;
            }
            catch (Exception)
            {
                // A probe that throws would take the measurement down with it; a missed sample is only a
                // gap, and TaintedBetween reports time no sample covers as not observed.
            }
        }

        /// <summary>
        /// A short tail's own reasons, less the ones that come from a per-second rate (those are taken
        /// from the merged, longer interval instead), plus the merged interval's reasons.
        /// </summary>
        private static IReadOnlyList<string> WithoutRates(IReadOnlyList<string> own, IReadOnlyList<string> merged)
        {
            List<string> r = new List<string>();
            foreach (string s in own)
            {
                if (s.StartsWith(RateReasonPrefixBuild, StringComparison.Ordinal)
                    || s.StartsWith(RateReasonPrefixLoad, StringComparison.Ordinal)
                    || s.StartsWith(RateReasonPrefixMachine, StringComparison.Ordinal)) continue;
                r.Add(s);
            }

            foreach (string s in merged) if (!r.Contains(s)) r.Add(s);
            return r;
        }

        private static readonly TimeSpan CoverageSlack = TimeSpan.FromMilliseconds(250);

        /// <summary>The reason given for time no sample covers.</summary>
        public const string NotObserved = "the machine was not observed for part of this time (the process list could not be read)";

        private const string RateReasonPrefixBuild = "a build tool was busy: ";
        private const string RateReasonPrefixLoad = "other processes together used more than ";
        private const string RateReasonPrefixMachine = "the machine as a whole was busier than ";

        // ---- readings -------------------------------------------------------------------------------

        /// <summary>
        /// One look at the machine: every process by pid, the machine's busy time, this process's CPU, and
        /// each excluded child's CPU as known at this moment.
        /// </summary>
        private sealed class Reading
        {
            public Reading(DateTime utc, Dictionary<int, (string Name, TimeSpan? Cpu)>? processes, TimeSpan? machineBusy, TimeSpan selfCpu,
                           Dictionary<int, TimeSpan>? children)
            {
                Utc = utc;
                Processes = processes ?? new Dictionary<int, (string, TimeSpan?)>();
                MachineBusy = machineBusy;
                SelfCpu = selfCpu;
                Children = children ?? new Dictionary<int, TimeSpan>();
            }

            /// <summary>
            /// Each excluded child's cumulative CPU time as known when this reading was taken: from the list
            /// while it runs, its reported total once it has ended, and otherwise the value the reading
            /// before had (no news is no progress, so its load then reads as foreign - a false taint, never
            /// a false quiet). A window's children are the difference between its two readings, so no
            /// child's time is ever taken out twice.
            /// </summary>
            public Dictionary<int, TimeSpan> Children { get; }

            public DateTime Utc { get; }

            /// <summary>Every process but this one and Idle; the CPU time is null where it could not be read.</summary>
            public Dictionary<int, (string Name, TimeSpan? Cpu)> Processes { get; }

            public TimeSpan? MachineBusy { get; }
            public TimeSpan SelfCpu { get; }
        }

        /// <summary>A reading now, or null when the process list itself could not be read.</summary>
        private static Reading? Read(MachineCpuReader? machine, HashSet<int> excluded, Dictionary<int, TimeSpan> exited, Reading? previous)
        {
            Dictionary<int, (string Name, TimeSpan? Cpu)>? processes = Snapshot();
            if (processes == null) return null;
            Dictionary<int, TimeSpan> children = new Dictionary<int, TimeSpan>();
            foreach (int pid in excluded)
            {
                if (processes.TryGetValue(pid, out (string Name, TimeSpan? Cpu) listed) && listed.Cpu.HasValue) children[pid] = listed.Cpu.Value;
                else if (exited.TryGetValue(pid, out TimeSpan total)) children[pid] = total;
                else if (previous != null && previous.Children.TryGetValue(pid, out TimeSpan known)) children[pid] = known;
            }

            TimeSpan? busy = null;
            try { busy = machine?.Invoke(); }
            catch (Exception) { busy = null; }

            TimeSpan self;
            try { self = Environment.CpuUsage.TotalTime; }
            catch (Exception)
            {
                // Without this process's own time the machine figure cannot be split; drop it.
                self = TimeSpan.Zero;
                busy = null;
            }

            return new Reading(DateTime.UtcNow, processes, busy, self, children);
        }

        /// <summary>
        /// Every process right now, by pid, without this process and Idle: its name, and its CPU time where
        /// the operating system will tell this account (null where it will not). Null when the list itself
        /// could not be read.
        /// </summary>
        private static Dictionary<int, (string Name, TimeSpan? Cpu)>? Snapshot()
        {
            int self = Environment.ProcessId;
            Dictionary<int, (string, TimeSpan?)> d = new Dictionary<int, (string, TimeSpan?)>();
            Process[] all;
            try
            {
                all = Process.GetProcesses();
            }
            catch (Exception)
            {
                return null;
            }

            foreach (Process p in all)
            {
                try
                {
                    int pid = p.Id;
                    if (pid == 0 || pid == self) continue;
                    string name;
                    try { name = p.ProcessName; }
                    catch (Exception) { continue; }   // gone between the listing and the read

                    TimeSpan? cpu;
                    try { cpu = p.TotalProcessorTime; }
                    catch (Exception)
                    {
                        // Access denied (a protected service, an elevated process) or it exited just now.
                        // The name is kept - a watched one still counts - and the time is never guessed at.
                        cpu = null;
                    }

                    d[pid] = (name, cpu);
                }
                catch (Exception)
                {
                    // An id that cannot be read belongs to a process that is gone.
                }
                finally
                {
                    p.Dispose();
                }
            }

            return d;
        }

        /// <summary>The verdict on the interval between two readings.</summary>
        private static ProbeTick Judge(Reading before, Reading after, HashSet<int> excluded,
                                       IReadOnlyList<(string Name, WatchedKind Kind)> watched, double baselineCores,
                                       QuietThresholds thresholds)
        {
            List<ProcessCpu> list = new List<ProcessCpu>(after.Processes.Count);
            int unreadable = 0;
            foreach (KeyValuePair<int, (string Name, TimeSpan? Cpu)> kv in after.Processes)
            {
                if (excluded.Contains(kv.Key)) continue;
                WatchedKind kind = Classify(kv.Value.Name, watched);
                if (!kv.Value.Cpu.HasValue)
                {
                    unreadable++;
                    list.Add(new ProcessCpu(kv.Value.Name, kv.Key, 0, kind, measured: false));
                    continue;
                }

                // A process first seen in this interval started inside it (or became readable): all of its
                // time is counted, which can only over-count the foreign load, never hide it.
                TimeSpan start = before.Processes.TryGetValue(kv.Key, out (string Name, TimeSpan? Cpu) b)
                                 && string.Equals(b.Name, kv.Value.Name, StringComparison.Ordinal) && b.Cpu.HasValue
                    ? b.Cpu.Value
                    : TimeSpan.Zero;
                double cs = Math.Max(0, (kv.Value.Cpu.Value - start).TotalSeconds);
                list.Add(new ProcessCpu(kv.Value.Name, kv.Key, cs, kind));
            }

            double? machine = null;
            if (before.MachineBusy.HasValue && after.MachineBusy.HasValue)
            {
                // The children's time over the interval: what the later reading knew of each, less what
                // the earlier one did (nothing, for a child that started inside the interval). A child the
                // later reading no longer carries has been forgotten after its end was in both - it used
                // nothing here.
                double children = 0;
                foreach (KeyValuePair<int, TimeSpan> kv in after.Children)
                {
                    TimeSpan then = before.Children.TryGetValue(kv.Key, out TimeSpan t) ? t : TimeSpan.Zero;
                    children += Math.Max(0, (kv.Value - then).TotalSeconds);
                }

                double busy = (after.MachineBusy.Value - before.MachineBusy.Value).TotalSeconds;
                double self = (after.SelfCpu - before.SelfCpu).TotalSeconds;
                machine = Math.Max(0, busy - self - children);
            }

            return Evaluate(before.Utc, after.Utc, list, after.Processes.Count, unreadable, baselineCores, thresholds, machine);
        }

        /// <summary>
        /// The watched kind of a process name (no ".exe"), case-insensitive. A watched name ending in
        /// '*' is a prefix.
        /// </summary>
        public static WatchedKind Classify(string processName, IReadOnlyList<(string Name, WatchedKind Kind)>? watched = null)
        {
            foreach ((string name, WatchedKind kind) in watched ?? DefaultWatched)
            {
                if (name.EndsWith('*'))
                {
                    string prefix = name.Substring(0, name.Length - 1);
                    if (processName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return kind;
                }
                else if (string.Equals(processName, name, StringComparison.OrdinalIgnoreCase))
                {
                    return kind;
                }
            }

            return WatchedKind.None;
        }

        private static double Limit(double baselineCores, QuietThresholds thresholds)
            => Math.Max(thresholds.ForeignCoresMax, thresholds.BaselineMultiple * baselineCores);

        /// <summary>
        /// The verdict on one interval. Pure - the tests drive it with made-up processes.
        /// <paramref name="machineForeignCoreSeconds"/> is the whole machine's busy time less this
        /// process and its excluded children, or null when it could not be read;
        /// <paramref name="baselineCores"/> is a QUIET baseline's foreign cores (0 when there is none or
        /// it was not quiet).
        /// </summary>
        public static ProbeTick Evaluate(DateTime startUtc, DateTime endUtc, IReadOnlyList<ProcessCpu> processes,
                                         int processCount, int unreadable, double baselineCores,
                                         QuietThresholds thresholds, double? machineForeignCoreSeconds = null)
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
                        reasons.Add("another SeedLab program was running: " + who);
                        break;
                    case WatchedKind.Game:
                        reasons.Add("Valheim was running: " + who);
                        break;
                    case WatchedKind.DevTool when p.Measured && cores > thresholds.DevToolCoresMax:
                        reasons.Add(RateReasonPrefixBuild + who + " used more than "
                                    + thresholds.DevToolCoresMax.ToString("0.##", CultureInfo.InvariantCulture) + " cores");
                        break;
                }
            }

            double limit = Limit(baselineCores, thresholds);
            string limitText = limit.ToString("0.##", CultureInfo.InvariantCulture);
            if (total / seconds > limit)
            {
                reasons.Add(RateReasonPrefixLoad + limitText + " cores on average");
            }
            else if (machineForeignCoreSeconds.HasValue && machineForeignCoreSeconds.Value / seconds > limit)
            {
                reasons.Add(RateReasonPrefixMachine + limitText + " cores beyond this run, counting the processes "
                            + "whose CPU time this account cannot read (protected services such as an antivirus scan, "
                            + "a search indexer or an update)");
            }

            if (unreadable > 0 && !machineForeignCoreSeconds.HasValue)
            {
                reasons.Add("some processes could not be measured and the machine's total CPU time could not be read, "
                            + "so their load is unknown");
            }

            List<ProcessCpu> top = new List<ProcessCpu>();
            foreach (ProcessCpu p in processes) if (p.Measured) top.Add(p);
            top.Sort((a, b) => b.CoreSeconds.CompareTo(a.CoreSeconds));
            if (top.Count > 5) top.RemoveRange(5, top.Count - 5);
            return new ProbeTick(startUtc, endUtc, total, machineForeignCoreSeconds, processCount, unreadable, top, watched,
                                 reasons, limit);
        }
    }

    /// <summary>
    /// The machine's total CPU time where the runtime layer can read it without a system call of its
    /// own: Linux's <c>/proc/stat</c>. On Windows the host supplies a <see cref="MachineCpuReader"/>
    /// (the CLI's reads <c>GetSystemTimes</c>).
    /// </summary>
    public static class MachineCpuTime
    {
        /// <summary>The reader used when the host gives none: <see cref="ReadProcStat"/> on Linux, nothing elsewhere.</summary>
        public static MachineCpuReader? Default => OperatingSystem.IsLinux() ? ReadProcStat : null;

        /// <summary>
        /// Busy time from the first ("cpu") line of <c>/proc/stat</c>: user, nice, system, irq, softirq
        /// and steal, in clock ticks of 10 ms (USER_HZ is 100 on every mainstream kernel). Idle and
        /// iowait are not busy. Null when the file cannot be read or parsed.
        /// </summary>
        public static TimeSpan? ReadProcStat()
        {
            try
            {
                const string path = "/proc/stat";
                if (!File.Exists(path)) return null;
                using StreamReader r = new StreamReader(path);
                string? line = r.ReadLine();
                if (line == null || !line.StartsWith("cpu ", StringComparison.Ordinal)) return null;
                string[] f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // cpu user nice system idle iowait irq softirq steal ...
                if (f.Length < 8) return null;
                long busy = 0;
                foreach (int i in new[] { 1, 2, 3, 6, 7 }) busy += long.Parse(f[i], CultureInfo.InvariantCulture);
                if (f.Length > 8) busy += long.Parse(f[8], CultureInfo.InvariantCulture);
                return TimeSpan.FromMilliseconds(busy * 10.0);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
