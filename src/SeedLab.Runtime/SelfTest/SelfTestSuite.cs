using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace SeedLab.Runtime.SelfTest
{
    /// <summary>
    /// One body of recorded golden vectors that must reproduce bit for bit on this machine.
    ///
    /// <para>The runtime layer ships one suite of its own (libm and float evaluation, embedded in the
    /// assembly so it works with no data folder). The generator-level suites - Perlin, the game's
    /// Random, the world hashes, the location placement - live with the code that can compute them and
    /// are REGISTERED here by the CLI or the web host. That is what keeps this project free of any
    /// SeedLab dependency while still being the thing that decides whether a machine may run a
    /// search.</para>
    /// </summary>
    public interface ISelfTestSuite
    {
        /// <summary>Short stable name; it goes into the self-test stamp, so changing it forces a re-check.</summary>
        string Name { get; }

        /// <summary>One line: what a divergence here would mean for a result.</summary>
        string Describes { get; }

        SelfTestSuiteResult Run(CancellationToken cancel);
    }

    public sealed class SelfTestSuiteResult
    {
        public SelfTestSuiteResult(string name, int checks, int failures, string firstFailure, TimeSpan elapsed,
                                   string? shareableFailure = null)
        {
            Name = name;
            Checks = checks;
            Failures = failures;
            FirstFailure = firstFailure ?? "";
            ShareableFailure = shareableFailure ?? FirstFailure;
            Elapsed = elapsed;
        }

        /// <summary>
        /// A suite that threw: the full message for this machine's own output, and for anything meant to
        /// be sent elsewhere only the exception's type and, when it names one, the file's name - an I/O
        /// exception's message carries the full path, which can name the user.
        /// </summary>
        public static SelfTestSuiteResult Threw(string name, Exception ex, string what = "the suite threw")
        {
            string file = ex is FileNotFoundException fnf && !string.IsNullOrEmpty(fnf.FileName)
                ? " (" + Path.GetFileName(fnf.FileName) + ")"
                : "";
            return new SelfTestSuiteResult(name, 1, 1, what + " " + ex.GetType().Name + ": " + ex.Message, TimeSpan.Zero,
                                           what + " " + ex.GetType().Name + file);
        }

        public string Name { get; }
        public int Checks { get; }
        public int Failures { get; }

        /// <summary>The first mismatch, with both bit patterns - the only detail worth printing.</summary>
        public string FirstFailure { get; }

        /// <summary>
        /// <see cref="FirstFailure"/> without anything that could name this machine or its user (no
        /// exception message, no path): what a machine report prints. For a golden that did not
        /// reproduce it is the same text - numbers and bit patterns only.
        /// </summary>
        public string ShareableFailure { get; }

        public TimeSpan Elapsed { get; }
        public bool Passed => Failures == 0 && Checks > 0;

        public override string ToString() =>
            Name + ": " + (Checks - Failures) + "/" + Checks + " exact"
            + (Passed ? "" : "  FIRST FAILURE: " + FirstFailure);
    }

    /// <summary>Thrown by <see cref="SelfTestOutcome.EnsureUsable"/>. Failing closed means throwing.</summary>
    public sealed class SelfTestFailedException : Exception
    {
        public SelfTestFailedException(SelfTestOutcome outcome)
            : base(outcome.Message)
        {
            Outcome = outcome;
        }

        public SelfTestOutcome Outcome { get; }
    }

    public enum SelfTestStatus
    {
        /// <summary>Every registered suite reproduced its goldens, here and now.</summary>
        Passed = 0,

        /// <summary>This exact machine, runtime and vector set passed earlier and the stamp is still valid.</summary>
        PassedCached = 1,

        /// <summary>
        /// Nothing diverged, but nothing proved the generator either: an architecture SeedLab has not
        /// verified, with no generator-level suite registered. Fails closed by default.
        /// </summary>
        Unproven = 2,

        /// <summary>A golden did not reproduce. The bit-exactness claim is false on this machine.</summary>
        Failed = 3,

        /// <summary>The caller turned the self-test off.</summary>
        Skipped = 4
    }

    public sealed class SelfTestOutcome
    {
        public SelfTestOutcome(SelfTestStatus status, string fingerprint, string platform,
                               IReadOnlyList<SelfTestSuiteResult> results, string message, TimeSpan elapsed)
        {
            Status = status;
            Fingerprint = fingerprint;
            Platform = platform;
            Results = results;
            Message = message;
            Elapsed = elapsed;
        }

        public SelfTestStatus Status { get; }

        /// <summary>The machine+runtime+vectors identity the stamp is filed under.</summary>
        public string Fingerprint { get; }

        public string Platform { get; }
        public IReadOnlyList<SelfTestSuiteResult> Results { get; }

        /// <summary>What to show the user - and, when it failed, what to do about it.</summary>
        public string Message { get; }

        public TimeSpan Elapsed { get; }

        public bool Ok => Status == SelfTestStatus.Passed || Status == SelfTestStatus.PassedCached;

        /// <summary>
        /// Fail closed. Anything other than a pass (or an explicitly skipped test) throws, because a
        /// seed list from a machine whose arithmetic has not been proved is worse than no seed list.
        /// </summary>
        public void EnsureUsable()
        {
            if (Ok || Status == SelfTestStatus.Skipped) return;
            throw new SelfTestFailedException(this);
        }
    }
}
