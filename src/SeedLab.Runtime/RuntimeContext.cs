using System;
using System.Collections.Generic;
using SeedLab.Runtime.Estimation;
using SeedLab.Runtime.Execution;
using SeedLab.Runtime.Hardware;
using SeedLab.Runtime.SelfTest;
using SeedLab.Runtime.Storage;
using SeedLab.Runtime.Throttle;

namespace SeedLab.Runtime
{
    /// <summary>
    /// Everything a host has to decide before a run starts, in one options object so the CLI's flags and
    /// the web UI's form map onto the same thing.
    /// </summary>
    public sealed class RuntimeOptions
    {
        /// <summary><c>--mode</c>. Balanced unless the user says otherwise.</summary>
        public ResourceMode Mode { get; set; } = ResourceModes.Default;

        /// <summary><c>--threads N</c>: replaces the mode's share, still memory-capped.</summary>
        public int? Threads { get; set; }

        /// <summary><c>--cache-dir</c>.</summary>
        public string? CacheDirectory { get; set; }

        /// <summary><c>--ignore-running-game</c> sets this false.</summary>
        public bool AutoThrottle { get; set; } = true;

        /// <summary>Extra or replacement process names to watch for.</summary>
        public IReadOnlyList<string>? GameProcessNames { get; set; }

        public TimeSpan ThrottlePollInterval { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary><c>--skip-self-test</c> sets this false. The result then says the run is unverified.</summary>
        public bool SelfTest { get; set; } = true;

        /// <summary><c>--accept-unverified-platform</c>.</summary>
        public bool AcceptUnverifiedPlatform { get; set; }

        /// <summary>Re-run the self-test even if this machine has a valid stamp.</summary>
        public bool ForceSelfTest { get; set; }

        /// <summary>Reap abandoned scratch directories and orphaned temp files at start. Leave on.</summary>
        public bool ReapOnStart { get; set; } = true;

        /// <summary>Where the layer's one-line announcements go. Defaults to nowhere.</summary>
        public Action<string>? Log { get; set; }

        /// <summary>Probe overrides, for tests and for "plan as if on a smaller machine".</summary>
        public HardwareProbeOptions? Probe { get; set; }

        /// <summary>For tests: a process lister that does not look at the real machine.</summary>
        public IProcessLister? ProcessLister { get; set; }

        public EstimateThresholds? Thresholds { get; set; }
    }

    /// <summary>
    /// The one object the CLI and the web server both hold for the life of a command or a search:
    /// the hardware probe, the cache root, the scratch directory, the auto-throttle, the estimator and
    /// the self-test outcome.
    ///
    /// <para>Start it once, print <see cref="StartupLines"/>, call <see cref="RequireVerifiedMachine"/>
    /// where a wrong answer would matter, plan workers with <see cref="PlanWorkers"/>, and dispose it at
    /// the end so the scratch directory and the process priority go back.</para>
    /// </summary>
    public sealed class RuntimeContext : IDisposable
    {
        private readonly List<string> _startupLines = new List<string>();
        private PriorityScope? _priority;
        private ScratchDirectory? _scratch;
        private bool _disposed;

        private RuntimeContext(RuntimeOptions options, HardwareInfo hardware, CacheRoot cache,
                               AutoThrottle throttle, Estimator estimator, MachineSelfTest selfTest)
        {
            Options = options;
            Hardware = hardware;
            Cache = cache;
            Throttle = throttle;
            Estimator = estimator;
            SelfTest = selfTest;
        }

        public RuntimeOptions Options { get; }
        public HardwareInfo Hardware { get; }
        public CacheRoot Cache { get; }
        public AutoThrottle Throttle { get; }
        public Estimator Estimator { get; }
        public MachineSelfTest SelfTest { get; }

        /// <summary>The self-test result, or null when <see cref="RuntimeOptions.SelfTest"/> is off.</summary>
        public SelfTestOutcome? SelfTestOutcome { get; private set; }

        public ReapReport? Reaped { get; private set; }

        /// <summary>The mode actually in force, after the auto-throttle has had its say.</summary>
        public ResourceMode EffectiveMode => Throttle.EffectiveMode;

        /// <summary>
        /// Probes the machine, opens the cache root, reaps what a killed process left behind, looks for a
        /// running game, and runs the self-test. Cheap: every part of it is milliseconds.
        /// </summary>
        public static RuntimeContext Start(RuntimeOptions? options = null)
        {
            RuntimeOptions o = options ?? new RuntimeOptions();
            Action<string> log = o.Log ?? (_ => { });

            HardwareInfo hw = HardwareProbe.Probe(o.Probe);
            CacheRoot cache = CacheRoot.Open(new CacheRootOptions { Override = o.CacheDirectory });

            GameWatchOptions watch = new GameWatchOptions
            {
                Enabled = o.AutoThrottle,
                PollInterval = o.ThrottlePollInterval
            };
            if (o.GameProcessNames != null && o.GameProcessNames.Count > 0)
            {
                string[] names = new string[o.GameProcessNames.Count];
                for (int i = 0; i < names.Length; i++) names[i] = o.GameProcessNames[i];
                watch.ProcessNames = names;
            }

            AutoThrottle throttle = new AutoThrottle(o.Mode, watch, log, o.ProcessLister);
            MachineSelfTest st = new MachineSelfTest(cache)
            {
                Enabled = o.SelfTest,
                AcceptUnverifiedPlatform = o.AcceptUnverifiedPlatform
            };

            RuntimeContext ctx = new RuntimeContext(o, hw, cache, throttle,
                new Estimator(o.Thresholds), st);

            if (o.ReapOnStart)
            {
                ctx.Reaped = cache.ReapAbandoned();
                string line = ctx.Reaped.Line();
                if (line.Length > 0) { ctx._startupLines.Add(line); log(line); }
            }

            ctx.SelfTestOutcome = st.Verify(hw, o.ForceSelfTest);
            if (ctx.SelfTestOutcome.Status != SelfTestStatus.PassedCached)
                ctx._startupLines.Add(ctx.SelfTestOutcome.Message);

            throttle.Start();
            ctx._priority = PriorityScope.Apply(ResourceModes.Priority(throttle.EffectiveMode));
            if (ctx._priority.Note.Length > 0) { ctx._startupLines.Add(ctx._priority.Note); log(ctx._priority.Note); }

            throttle.Changed += change =>
            {
                try
                {
                    ctx._priority?.Dispose();
                    ctx._priority = PriorityScope.Apply(ResourceModes.Priority(change.To));
                }
                catch (Exception) { }
            };

            return ctx;
        }

        /// <summary>The per-process scratch directory, created on first use and deleted on dispose.</summary>
        public ScratchDirectory Scratch(string purpose = "run") =>
            _scratch ??= Cache.CreateScratch(purpose);

        /// <summary>Fail closed. Call this before anything whose answer a user would act on.</summary>
        public void RequireVerifiedMachine() => SelfTestOutcome?.EnsureUsable();

        /// <summary>Workers and priority for a tier and grid, at the mode in force right now.</summary>
        public WorkerPlan PlanWorkers(WorkTier tier, long gridCells, long reservedBytes = 0) =>
            WorkerPlanner.Plan(EffectiveMode, WorkerFootprints.For(tier, gridCells), Hardware,
                new WorkerPlanOptions { RequestedWorkers = Options.Threads, ReservedBytes = reservedBytes });

        /// <summary>Workers for a footprint the engine measured itself.</summary>
        public WorkerPlan PlanWorkers(WorkerFootprint footprint, long reservedBytes = 0) =>
            WorkerPlanner.Plan(EffectiveMode, footprint, Hardware,
                new WorkerPlanOptions { RequestedWorkers = Options.Threads, ReservedBytes = reservedBytes });

        /// <summary>
        /// Fills in the machine-dependent half of an estimate (threads, free space, available RAM,
        /// footprint) so the caller only supplies the query's half.
        /// </summary>
        public EstimateInputs Inputs(EstimateInputs query, WorkerPlan plan, string outputPath)
        {
            EstimateInputs i = query.Clone();
            i.Threads = plan.Workers;
            i.Footprint = plan.Footprint;
            i.AvailableMemoryBytes = Hardware.AvailableMemoryBytes;
            i.FreeSpaceBytes = VolumeInfo.For(string.IsNullOrWhiteSpace(outputPath) ? Cache.Path : outputPath).FreeBytes;
            return i;
        }

        /// <summary>What to print at the top of a run: the machine, the cache, the self-test, the throttle.</summary>
        public IReadOnlyList<string> StartupLines()
        {
            List<string> l = new List<string>(Hardware.Lines());
            l.Add("cache       " + Cache.Path + " (" + Cache.SourceDetail + ")");
            l.Add("mode        " + ResourceModes.Describe(EffectiveMode)
                  + (Throttle.IsThrottled ? "  [auto-throttled by " + Throttle.ThrottledBy + "]" : ""));
            l.AddRange(_startupLines);
            return l;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Throttle.Dispose(); } catch (Exception) { }
            try { _scratch?.Dispose(); } catch (Exception) { }
            try { _priority?.Dispose(); } catch (Exception) { }
        }
    }
}
