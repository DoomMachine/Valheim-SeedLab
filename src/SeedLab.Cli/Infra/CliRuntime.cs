using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SeedLab.Runtime;
using SeedLab.Runtime.Execution;
using SeedLab.Runtime.SelfTest;

namespace SeedLab.Cli.Infra
{
    /// <summary>
    /// The one <see cref="RuntimeContext"/> a command runs inside, plus the two CLI-side things the
    /// runtime layer cannot do for itself: registering the generator-level golden suite, and turning
    /// its exceptions into <see cref="CliException"/>s with a fix in them.
    ///
    /// <para><b>What this fixes.</b> Every resource decision used to be
    /// <c>Environment.ProcessorCount</c> at the point of use: <c>vseed search</c>, <c>bench</c>,
    /// <c>selftest</c> and <c>serve</c> each sized themselves independently, none of them looked at
    /// free memory, none of them noticed the game running, and there was no <c>--mode</c> at all. The
    /// worker count now comes from one place - mode share, capped by the memory guard, overridden by
    /// an explicit <c>--threads</c> - and the command prints the arithmetic it used.</para>
    ///
    /// <para><b>Self-test.</b> <see cref="RuntimeContext.Start"/> verifies before a caller can
    /// register anything, so its outcome only ever knows about the built-in numeric suite. This class
    /// registers <see cref="NativesGoldenSuite"/> and verifies AGAIN; that second outcome is the one
    /// <see cref="RequireVerified"/> enforces. Registering changes the fingerprint, so the second
    /// verification really runs rather than reading the first one's stamp, and its own stamp then
    /// makes every later command a few milliseconds.</para>
    /// </summary>
    public sealed class CliRuntime : IDisposable
    {
        private readonly List<string> _extra = new List<string>();
        private bool _disposed;

        private CliRuntime(RuntimeContext ctx)
        {
            Context = ctx;
        }

        public RuntimeContext Context { get; }

        /// <summary>The self-test outcome AFTER the natives suite was registered, when it could be.</summary>
        public SelfTestOutcome? SelfTest { get; private set; }

        /// <summary>Where the natives goldens were found, or null.</summary>
        public string? NativesDirectory { get; private set; }

        public SeedLab.Runtime.Storage.CacheRoot Cache => Context.Cache;

        public ResourceMode Mode => Context.EffectiveMode;

        /// <summary>
        /// Reads the global runtime options off the command line and starts the context. Every option
        /// is READ here even when the command will not use it, so that <see cref="Args.RejectUnknown"/>
        /// does not call a documented global flag a typo.
        /// </summary>
        public static CliRuntime Start(Args a, string command)
        {
            string? modeText = a.Get("mode");
            if (!ResourceModes.TryParse(modeText, out ResourceMode mode, out string modeError))
            {
                throw new CliException("--mode: " + modeError, ExitCodes.Usage,
                    "background is ~25 % of the cores at BelowNormal, balanced (the default) ~50 %, full all of them");
            }

            RuntimeOptions options = new RuntimeOptions
            {
                Mode = mode,
                Threads = a.ThreadsRequest(),
                CacheDirectory = a.Get("cache-dir"),
                AutoThrottle = !a.Flag("ignore-running-game"),
                SelfTest = !a.Flag("skip-self-test"),
                AcceptUnverifiedPlatform = a.Flag("accept-unverified-platform"),
                // One line, on stderr, so a --json stdout stays parseable. This is the channel the
                // auto-throttle announcement arrives on.
                Log = line => { if (!string.IsNullOrEmpty(line)) Console.Error.WriteLine("vseed: " + line); },
            };

            RuntimeContext ctx;
            try
            {
                ctx = RuntimeContext.Start(options);
            }
            catch (IOException ex)
            {
                // A --cache-dir that cannot be created is a user-fixable condition, not "this is a bug".
                throw new CliException(ex.Message, ExitCodes.NotFound,
                    "pass --cache-dir <folder> or set SEEDLAB_CACHE_DIR to somewhere writable");
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new CliException("the cache directory could not be opened: " + ex.Message, ExitCodes.NotFound,
                    "pass --cache-dir <folder> or set SEEDLAB_CACHE_DIR to somewhere writable");
            }

            CliRuntime rt = new CliRuntime(ctx);
            rt.RegisterSuitesAndVerify(command);

            // A run whose arithmetic has not been proved must SAY SO on every command, not only on
            // the ones that happen to print the startup block. --skip-self-test's own message ends
            // "which says so on every result"; before this it said so on none of them, because
            // 'vseed seed' never prints StartupLines.
            if (rt.SelfTest != null && rt.SelfTest.Status == SelfTestStatus.Skipped)
            {
                Out.Warn(rt.SelfTest.Message);
            }

            return rt;
        }

        private void RegisterSuitesAndVerify(string command)
        {
            SelfTest = Context.SelfTestOutcome;
            if (!Context.Options.SelfTest) return;

            NativesGoldenSuite? suite = NativesGoldenSuite.TryCreate();
            if (suite == null)
            {
                NativesDirectory = null;
                // Only worth saying on a platform where it is the difference between running and not.
                if (SelfTest != null && SelfTest.Status == SelfTestStatus.Unproven)
                {
                    _extra.Add("groundtruth\\natives was not found beside this build, so the generator-level "
                               + "goldens could not be replayed - copy groundtruth\\ next to vseed and re-run");
                }

                return;
            }

            NativesDirectory = suite.Directory_;
            Context.SelfTest.Register(suite);
            SelfTest = Context.SelfTest.Verify(Context.Hardware);
        }

        /// <summary>
        /// Fail closed before anything a user would act on. Anything but a pass (or an explicitly
        /// skipped gate) stops the command with the message that names the fix.
        /// </summary>
        public void RequireVerified()
        {
            if (SelfTest == null) return;
            try
            {
                SelfTest.EnsureUsable();
            }
            catch (SelfTestFailedException ex)
            {
                throw new CliException(ex.Message, ExitCodes.CheckFailed,
                    ex.Outcome.Status == SelfTestStatus.Unproven && NativesDirectory == null
                        ? "the generator goldens (groundtruth\\natives) were not found beside this build; "
                          + "copy groundtruth\\ next to vseed so the self-test can prove this platform"
                        : null);
            }
        }

        /// <summary>
        /// Workers for a tier and grid, at the mode in force right now. The returned plan carries the
        /// arithmetic - mode share, memory budget, what each allowed - which every command prints
        /// rather than only using.
        /// </summary>
        public WorkerPlan Plan(WorkTier tier, double gridSpacingMetres, long reservedBytes = 0) =>
            Context.PlanWorkers(tier, WorkerFootprints.CellsForSpacing(gridSpacingMetres), reservedBytes);

        public WorkerPlan Plan(WorkTier tier, long gridCells, long reservedBytes = 0) =>
            Context.PlanWorkers(tier, gridCells, reservedBytes);

        /// <summary>The worker count alone, for a caller that only needs the number.</summary>
        public int Workers(WorkTier tier, double gridSpacingMetres) => Plan(tier, gridSpacingMetres).Workers;

        /// <summary>
        /// The machine, the cache, the mode, the self-test and anything the startup reaped - printed
        /// before a run that is going to take time, and never for a one-line answer.
        /// </summary>
        public IReadOnlyList<string> StartupLines()
        {
            List<string> l = new List<string>(Context.StartupLines());
            if (SelfTest != null && SelfTest.Status != SelfTestStatus.PassedCached
                && SelfTest.Status != SelfTestStatus.Passed)
            {
                l.Add(SelfTest.Message);
            }
            else if (SelfTest != null && SelfTest.Status == SelfTestStatus.Passed)
            {
                l.Add(SelfTest.Message);
            }

            l.AddRange(_extra);
            return l;
        }

        /// <summary>Everything in <see cref="StartupLines"/>, on stderr, indented under a heading.</summary>
        public void PrintStartup(string heading = "Machine")
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine(heading);
            foreach (string line in StartupLines()) Console.Error.WriteLine("  " + line);
        }

        /// <summary>The worker plan's own lines, on stderr, so a --json stdout stays parseable.</summary>
        public static void PrintPlan(WorkerPlan plan)
        {
            foreach (string line in plan.Lines()) Console.Error.WriteLine("  " + line);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Context.Dispose();
        }

        /// <summary>Bytes, the way the runtime layer prints them, so the two never disagree.</summary>
        public static string Bytes(long b) => SeedLab.Runtime.Hardware.Bytes.Human(b);

        public static string Count(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
    }
}
