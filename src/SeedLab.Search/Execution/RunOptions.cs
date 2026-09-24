using System;
using SeedLab.Search.Output;

namespace SeedLab.Search.Execution
{
    /// <summary>
    /// Everything a <see cref="SearchRun"/> needs beyond the plan itself: where results go, when the
    /// checkpoint is written, how much memory the scan is allowed to hold, and what happens at a
    /// ceiling.
    ///
    /// <para>The defaults are the safe ones. A caller that passes nothing gets a bounded run that
    /// checkpoints on time, cannot grow its queue without bound, and stops at its ceiling rather than
    /// evicting - which is the decided behaviour (docs\studies\decisions-1.md sections 1, 2, 11).</para>
    /// </summary>
    public sealed class RunOptions
    {
        /// <summary>Where passing seeds go. Null keeps nothing on disk (the web UI streams instead).</summary>
        public IResultSink? Sink;

        /// <summary>The resume point, or null for a run that cannot be resumed.</summary>
        public Checkpoint? Checkpoint;

        public CheckpointPolicy Checkpoints = new CheckpointPolicy();

        /// <summary>Stop after this long. Zero means no wall-clock limit.</summary>
        public TimeSpan Wall = TimeSpan.Zero;

        /// <summary>Progress callback, at most twice a second. Null for a silent run.</summary>
        public Action<Progress>? OnProgress;

        /// <summary>
        /// Called on the collector thread with each warning as it happens - a checkpoint save that
        /// failed and the run went on, the save that worked again, a finished run's checkpoint that
        /// could not be deleted - so a front end can show it at once rather than at the end. Every
        /// one is also kept in <see cref="SearchOutcome.Warnings"/>. Failures in a row are not
        /// repeated: the first, then every tenth, then the recovery (2026-09-24).
        /// </summary>
        public Action<string>? OnWarning;

        /// <summary>
        /// The most finished-but-not-yet-written blocks the scan may hold in memory.
        ///
        /// <para><b>This bound is the fix for a measured 7.6 GB blow-up.</b> Blocks are claimed by 16
        /// threads and emitted in order by one collector; when the collector is the slower of the two
        /// - which it is whenever most seeds pass, because it alone formats and writes - the finished
        /// blocks pile up with nothing to stop them. Measured on this machine before the bound:
        /// <c>vseed search custom --all --block-size 64 --threads 16</c> reached 7,599 MB of working
        /// set after 120 s and was still climbing by about 60 MB/s. With the bound, a worker that
        /// finishes a block waits for the collector to catch up.</para>
        ///
        /// <para><b>It is a soft bound.</b> A worker checks it before CLAIMING a block, and always
        /// stores the block it has already finished, so the queue can reach
        /// <c>(MaxPendingBlocks + threads) x block size</c> results. Measured at 16 threads with the
        /// default bound of 32: a peak of 47 queued blocks against the 48 the arithmetic allows. The
        /// point is that it is bounded at all - the alternative, measured, was 7.6 GB and climbing.</para>
        /// </summary>
        public int MaxPendingBlocks;

        /// <summary>A hard ceiling on result bytes; 0 for none. <see cref="OnLimit"/> decides what happens.</summary>
        public long MaxResultBytes;

        /// <summary>
        /// At the ceiling: stop cleanly (the default) or keep going and drop the worst records. Evict
        /// is only meaningful for a bounded sink, which drops by construction.
        /// </summary>
        public OnLimit OnLimit = OnLimit.Stop;

        /// <summary>
        /// Fed one slice of real work at every checkpoint, so the run's own projection is
        /// re-calibrated as it goes. Null for a run that does not report estimates.
        /// </summary>
        public RunEstimator? Estimator;

        /// <summary>Seeds already evaluated by earlier legs of a resumed run.</summary>
        public long ResumedEvaluated;

        public long ResumedPassed;

        public double ResumedSeconds;

        /// <summary>The default queue bound for a thread count: enough to keep workers fed, not enough to hurt.</summary>
        public static int DefaultPendingBlocks(int threads) => Math.Max(8, threads * 2);
    }
}
