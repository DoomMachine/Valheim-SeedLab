using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using SeedLab.Search.Evaluation;
using SeedLab.Search.Locations;
using SeedLab.Search.Output;

namespace SeedLab.Search.Execution
{
    /// <summary>Live numbers for the progress line. Every field is measured, none is predicted.</summary>
    public struct Progress
    {
        public long Evaluated;
        public long Passed;
        public long Limit;
        public double Seconds;
        public double SeedsPerSecond;

        /// <summary>Seconds left for the requested range, at the rate measured so far. NaN before the first block.</summary>
        public double EtaSeconds;

        /// <summary>The fraction of all 4,294,967,296 worlds this run will have covered when it finishes.</summary>
        public double FractionOfSpace;

        /// <summary>The same, but for what has actually been evaluated so far.</summary>
        public double FractionCovered;

        public long ProbeAccepts;
        public long EarlyExits;

        /// <summary>Records kept on disk so far (a bounded run keeps at most <c>keep</c> of them).</summary>
        public long Kept;

        /// <summary>Bytes the results file holds, or -1 when the sink does not append.</summary>
        public long ResultBytes;

        /// <summary>Blocks finished and waiting for the collector. The memory bound's own number.</summary>
        public int PendingBlocks;
    }

    /// <summary>Everything the run measured, for the report at the end.</summary>
    public sealed class SearchOutcome
    {
        public long Evaluated;

        /// <summary>Every seed that matched, whether or not it was written. The honest headline.</summary>
        public long Passed;

        /// <summary>Wall-clock of this process's share of the run (a resumed run adds the earlier legs).</summary>
        public double Seconds;

        public double SeedsPerSecond;
        public long Blocks;
        public long BlocksDone;
        public bool Complete;

        /// <summary>The wall budget cut the run short. Never true of a run that is <see cref="Complete"/>.</summary>
        public bool StoppedByWall;

        /// <summary>A Stop (Ctrl-C, the page's button) cut the run short. Never true of a run that is <see cref="Complete"/>.</summary>
        public bool StoppedByUser;

        /// <summary>The run stopped because the result ceiling was reached and <c>--on-limit stop</c> was in force.</summary>
        public bool StoppedByLimit;

        public long ProbeAccepts;
        public long EarlyExits;

        /// <summary>Seeds whose location placement stopped part-way through the ordered prefix.</summary>
        public long LocationAborts;

        /// <summary>Seeds that never started a placement because a cheaper must-goal had already failed.</summary>
        public long LocationSkips;

        /// <summary>Seeds that ran a location placement.</summary>
        public long LocationsPlaced;

        /// <summary>Summed over workers: time inside the location oracle.</summary>
        public double LocationSeconds;

        /// <summary>Summed over workers: time inside the <c>WorldGeneratorPort</c> constructor.</summary>
        public double ConstructSeconds;

        /// <summary>Summed over workers: time sampling the grid.</summary>
        public double SampleSeconds;

        /// <summary>Summed over workers: time in the deferred lake/river/stream pre-generation.</summary>
        public double PregenSeconds;

        /// <summary>Seeds that reached a tier which needed the river data and so paid for pre-generation.</summary>
        public long Pregenerated;

        public int Threads;

        /// <summary>
        /// Workers that claimed at least one block in this leg - counted at the claim, not predicted.
        ///
        /// <para><b>Why the rate needs it.</b> One worker computes a whole block, so a leg with fewer
        /// blocks than workers - a short run at a block size that was given, the tail of a resumed
        /// run, a run the budget stopped before every worker had claimed - measures the rate of the
        /// workers that had blocks, not of the machine. Reported as "on 8 threads" it was a starved
        /// number wearing the machine's name: the gentle-start run captured in finding-a-seed.md was
        /// 400 seeds in 2 blocks of 256 at 2.0 seeds/s, where the same preset measured 7.3 with every
        /// worker fed. Every front end labels the rate with this whenever it is below
        /// <see cref="Threads"/> (2026-09-24), and the web page does not keep such a rate as the
        /// machine's.</para>
        /// </summary>
        public int BusyWorkers;

        /// <summary>
        /// The label a rate needs when not every worker had work - "(2 of 8 workers had work)" - or
        /// null when every one did. One sentence for the CLI's report, its no-match line, and the web
        /// page, so the three cannot word it differently.
        /// </summary>
        public string? RateNote => BusyWorkers < Threads
            ? "(" + BusyWorkers.ToString(System.Globalization.CultureInfo.InvariantCulture) + " of "
              + Threads.ToString(System.Globalization.CultureInfo.InvariantCulture) + " workers had work)"
            : null;

        public List<SeedResult> Top = new List<SeedResult>();
        public string? ResultsPath;

        /// <summary>Records actually on disk. For a bounded run this is at most <c>keep</c>.</summary>
        public long ResultsWritten;

        /// <summary>Matches found and deliberately not written (bounded or reduced output).</summary>
        public long ResultsDropped;

        /// <summary>The score of the worst kept record, i.e. the cut line. NaN when nothing was dropped.</summary>
        public double CutScore = double.NaN;

        /// <summary>The cut line at the moment the first record was dropped. NaN when nothing was dropped.</summary>
        public double FirstDropScore = double.NaN;

        /// <summary>True when the sink capped the file rather than streaming every match.</summary>
        public bool Bounded;

        /// <summary>The checkpoint this run left behind, or null when it completed and retired it.</summary>
        public string? CheckpointPath;

        /// <summary>The most blocks that were ever queued for the collector. The memory bound, measured.</summary>
        public int PeakPendingBlocks;
    }

    /// <summary>
    /// The scan: claim a block, evaluate its seeds, hand the block's hits to one collector that writes
    /// them in block order.
    ///
    /// <para><b>One seed per thread, never a seed split across threads.</b> Every tier is embarrassingly
    /// parallel at seed granularity, and the per-seed state (grid buffers, flood-fill scratch) is large
    /// and reusable, so each worker allocates its buffers once and keeps them forever.</para>
    ///
    /// <para><b>Determinism.</b> Results are emitted in block order by a single thread, areas are summed
    /// as integer cell counts, and the top-N list is ordered by <c>(score desc, seed asc)</c>. A run is
    /// therefore byte-reproducible from <c>{query, defs, key, range, block size}</c> whatever the thread
    /// count - which is what makes the resume test meaningful.</para>
    ///
    /// <para><b>Bounded memory.</b> The queue of finished-but-unwritten blocks is capped
    /// (<see cref="RunOptions.MaxPendingBlocks"/>). Without the cap a run whose seeds mostly pass
    /// outruns its own collector and grows without limit - measured at 7,599 MB after 120 s before
    /// this bound existed.</para>
    ///
    /// <para><b>Bounded time to durability.</b> The checkpoint and the flush are on a clock, not on
    /// "a block happened to be emitted just now": a kill must never cost more than the checkpoint
    /// interval, which is what <c>--checkpoint-every</c> has always promised.</para>
    /// </summary>
    public sealed class SearchRun
    {
        private readonly CompiledQuery _q;
        private readonly ScanPlan _plan;
        private readonly ILocationOracle _oracle;
        private readonly int _threads;
        private readonly object _statLock = new object();

        private long _nextClaim;
        private long _blocksFinished;
        private volatile bool _stop;
        private volatile bool _userStop;
        private volatile bool _wallHit;
        private volatile bool _limitHit;
        private long _startBlock;
        private int _peakPending;
        private int _busyWorkers;

        private double _constructSeconds;
        private double _sampleSeconds;
        private double _pregenSeconds;
        private long _pregenerated;
        private long _probeAccepts;
        private long _earlyExits;
        private long _locationAborts;
        private long _locationSkips;
        private long _locationsPlaced;
        private double _locationSeconds;

        public SearchRun(CompiledQuery q, ScanPlan plan, ILocationOracle oracle, int threads)
        {
            _q = q;
            _plan = plan;
            _oracle = oracle;
            _threads = threads > 0 ? threads : Math.Max(1, Environment.ProcessorCount);
        }

        public int Threads => _threads;

        /// <summary>
        /// Called with every passing seed at the moment it is emitted, on the collector thread, in the
        /// same block order and with the same records the sink receives.
        ///
        /// <para>This is the seam a caller that must not touch the disk uses - the local web UI streams
        /// hits to the browser through it while the scan is still running. It is deliberately the same
        /// emit point as the sink's rather than a second one, so a streamed result set and a written
        /// one cannot diverge. Set it before <see cref="Run"/>; it is not read again afterwards.</para>
        /// </summary>
        public Action<SeedResult>? OnResult;

        /// <summary>
        /// Builds one evaluator per worker. The default is a single-pass <see cref="SeedEvaluator"/>
        /// over the compiled query; a screen-then-verify run hands in a
        /// <see cref="TwoStageEvaluator"/> instead, so the coarse screen and the exact re-measurement
        /// are one worker's business and the scan machinery does not have to know about either.
        /// </summary>
        public Func<ISeedEvaluator>? EvaluatorFactory;

        /// <summary>Ask the run to stop cleanly at the next block boundary and checkpoint.</summary>
        public void RequestStop()
        {
            _userStop = true;
            _stop = true;
        }

        /// <summary>
        /// The original entry point, kept source-compatible for callers that stream to a
        /// <see cref="ResultWriter"/> (the web engine passes null, <c>vseed serve</c> passes null).
        /// </summary>
        public SearchOutcome Run(long startBlock, ResultWriter? writer, Checkpoint? checkpoint,
                                 string? checkpointPath, TimeSpan checkpointInterval, TimeSpan wallBudget,
                                 Action<Progress>? onProgress, long resumedEvaluated = 0, long resumedPassed = 0,
                                 double resumedSeconds = 0)
        {
            // The bound is applied HERE, not left to the caller, because this is the entry point
            // 'vseed search' uses and the headline defect the audit found was that the caller's
            // --keep capped a display list while the file took every hit: a whole-space run of a
            // query with no must-have goal would have written 7.36 TB. A caller that genuinely wants
            // every match sets keep: "all" in the query (or builds its own sink through
            // SearchSession), which is a thing that now has to be asked for out loud.
            IResultSink? sink = writer;
            if (writer != null && !_q.Query.Search.KeepAll)
            {
                sink = new BoundedResultSink(writer, _q.Query.Search.Keep);
            }

            // NOT screened here, on purpose. Screen-then-verify returns the same seeds with the same
            // records (verified: 0 false negatives, 0 false positives, 0 differing records over 3,000
            // seeds) and cost 2.75-3.3x less on a selective query - but it is a second pass, and a
            // front end that cannot say it happened cannot be honest about what the run did. It is
            // therefore wired by SearchSession, which prints the grid plan, the margin and the
            // measurement behind it before the scan starts. A caller on this legacy entry point gets
            // exactly the single pass its own plan printed.

            return Run(startBlock, new RunOptions
            {
                Sink = sink,
                Checkpoint = checkpoint,
                Checkpoints = new CheckpointPolicy
                {
                    Path = checkpointPath,
                    Interval = checkpointInterval,
                    // No grace period before the first checkpoint: its whole job is that a kill costs
                    // at most one interval, and a delay is exactly that much work thrown away. The
                    // litter the audit found - two orphaned checkpoints sitting in the repository,
                    // left by runs that had FINISHED - is prevented by DeleteOnComplete below
                    // (decisions 11.4 and 11.5), not by refusing to write the file in the first place.
                    MinRunTime = TimeSpan.Zero,
                    SnapshotPath = checkpointPath != null
                        ? CheckpointStore.SnapshotPathFor(checkpointPath) : null,
                    DeleteOnComplete = true,
                },
                Wall = wallBudget,
                OnProgress = onProgress,
                ResumedEvaluated = resumedEvaluated,
                ResumedPassed = resumedPassed,
                ResumedSeconds = resumedSeconds,
            });
        }

        /// <summary>The scan. See <see cref="RunOptions"/> for what the caller controls.</summary>
        public SearchOutcome Run(long startBlock, RunOptions options)
        {
            IResultSink? sink = options.Sink;
            Checkpoint? checkpoint = options.Checkpoint;
            CheckpointPolicy ckp = options.Checkpoints;
            string? checkpointPath = ckp.Path;
            int maxPending = options.MaxPendingBlocks > 0
                ? options.MaxPendingBlocks
                : RunOptions.DefaultPendingBlocks(_threads);

            _nextClaim = startBlock;
            _startBlock = startBlock;
            _peakPending = 0;
            _busyWorkers = 0;

            // A resumed bounded run has to continue the SAME best-N, or its file would hold the best
            // of the tail of the scan under the name "top N". The kept set travels with the
            // checkpoint, written in the same atomic step; restore it here so that even a caller that
            // only knows about the checkpoint (the legacy entry point above) resumes correctly.
            if (sink is BoundedResultSink restore && startBlock > 0 && restore.Set.TotalMatches == 0)
            {
                bool have = checkpoint?.KeptSnapshot != null && System.IO.File.Exists(checkpoint.KeptSnapshot);
                if (have)
                {
                    restore.Restore(BoundedResultSet.LoadSnapshot(checkpoint!.KeptSnapshot!, restore.Set.Keep));
                }
                else if ((checkpoint?.SeedsPassed ?? 0) > 0)
                {
                    // Refuse rather than quietly produce "the best N of the tail". A checkpoint from a
                    // build before the kept set was snapshotted looks exactly like this.
                    throw new InvalidOperationException(
                        "cannot resume this bounded run: the checkpoint records "
                        + checkpoint!.SeedsPassed.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
                        + " matches from earlier legs but has no kept-results snapshot beside it, so the best "
                        + "records found before the interruption cannot be recovered and this leg would report "
                        + "a top-N built only from the rest of the scan. Delete the checkpoint to run the query "
                        + "from the beginning, or use keep: \"all\", whose results file resumes from its own bytes.");
                }
            }

            ConcurrentDictionary<long, List<SeedResult>> done = new ConcurrentDictionary<long, List<SeedResult>>();
            ManualResetEventSlim blockReady = new ManualResetEventSlim(false);
            ManualResetEventSlim roomReady = new ManualResetEventSlim(false);
            Stopwatch sw = Stopwatch.StartNew();
            double wallSeconds = options.Wall > TimeSpan.Zero ? options.Wall.TotalSeconds : double.PositiveInfinity;

            Thread[] workers = new Thread[_threads];
            for (int t = 0; t < _threads; t++)
            {
                workers[t] = new Thread(() => Worker(done, blockReady, roomReady, sw, wallSeconds, maxPending))
                {
                    IsBackground = true,
                    Name = "vseed-search-" + t,
                };
                workers[t].Start();
            }

            List<SeedResult> top = new List<SeedResult>();
            long emit = startBlock;
            long emittedPassed = options.ResumedPassed;
            DateTime lastCheckpoint = DateTime.UtcNow;
            DateTime lastProgress = DateTime.MinValue;
            DateTime runStarted = DateTime.UtcNow;
            bool wroteCheckpoint = false;
            long sliceSeeds = Math.Min(_plan.Limit, _plan.BlockStart(startBlock));
            long sliceMatches = options.ResumedPassed;
            double sliceSeconds = 0;

            while (emit < _plan.Blocks)
            {
                if (done.TryRemove(emit, out List<SeedResult>? hits))
                {
                    foreach (SeedResult r in hits)
                    {
                        sink?.Add(r);
                        OnResult?.Invoke(r);
                        Keep(top, r, _q.Query.Search.Keep);
                        emittedPassed++;
                    }

                    emit++;
                    roomReady.Set();

                    // The ceiling. Checked here, at a block boundary, so the run stops on a boundary
                    // the checkpoint can express and the resume command it prints is exact.
                    if (options.MaxResultBytes > 0 && options.OnLimit == OnLimit.Stop && sink != null
                        && sink.FileBytes >= options.MaxResultBytes)
                    {
                        _limitHit = true;
                        _stop = true;
                    }
                }

                // Progress is on the same clock as everything else, and outside the "we had to wait"
                // branch: a run whose collector always has the next block ready would otherwise never
                // report anything at all.
                if (options.OnProgress != null && (DateTime.UtcNow - lastProgress).TotalMilliseconds >= 500)
                {
                    lastProgress = DateTime.UtcNow;
                    options.OnProgress(Snapshot(sw, emit, emittedPassed, options.ResumedSeconds, sink, done.Count));
                }

                // ---- durability on a CLOCK, not on "a block happened just now" ----------------------
                //
                // The audit measured the old behaviour: a hard kill 300 s into a 16-thread run left a
                // 0-byte results file and no checkpoint, because both were inside the block-emitted
                // branch and the emitting had stalled. Anything that is time-based must be time-based
                // from outside that branch, or it is a promise the tool does not keep.
                if (checkpointPath != null && checkpoint != null
                    && (DateTime.UtcNow - lastCheckpoint) >= ckp.Interval
                    && (DateTime.UtcNow - runStarted) >= ckp.MinRunTime)
                {
                    sink?.Flush();
                    SaveCheckpoint(checkpoint, checkpointPath, emit, emittedPassed,
                                   sw.Elapsed.TotalSeconds + options.ResumedSeconds, sink, ckp);
                    lastCheckpoint = DateTime.UtcNow;
                    wroteCheckpoint = true;
                    ObserveSlice(options, sw, emit, emittedPassed, sink, ref sliceSeeds, ref sliceMatches,
                                 ref sliceSeconds);
                }

                if (done.ContainsKey(emit)) continue;
                if (AllDone(workers)) break;

                blockReady.Wait(50);
                blockReady.Reset();
            }

            _stop = true;                 // every block below _plan.Blocks is claimed; let the workers go
            roomReady.Set();
            blockReady.Set();
            foreach (Thread th in workers) th.Join();

            // Blocks that finished after the first gap are discarded: the checkpoint's promise is
            // "every block below next_block is on disk", and a gap would break it. They are pure
            // functions of their seeds, so re-running them on resume costs time and nothing else.
            while (done.TryRemove(emit, out List<SeedResult>? tail))
            {
                foreach (SeedResult r in tail)
                {
                    sink?.Add(r);
                    OnResult?.Invoke(r);
                    Keep(top, r, _q.Query.Search.Keep);
                    emittedPassed++;
                }

                emit++;
            }

            ObserveSlice(options, sw, emit, emittedPassed, sink, ref sliceSeeds, ref sliceMatches,
                         ref sliceSeconds);

            sw.Stop();
            double legSeconds = sw.Elapsed.TotalSeconds;
            double seconds = legSeconds + options.ResumedSeconds;
            long emittedSeeds = Math.Min(_plan.Limit, emit * (long)_plan.BlockSize);
            bool complete = emit >= _plan.Blocks;

            sink?.Flush();

            if (checkpointPath != null && checkpoint != null)
            {
                bool worthIt = wroteCheckpoint || !complete
                               || (DateTime.UtcNow - runStarted) >= ckp.MinRunTime;
                if (complete && ckp.DeleteOnComplete)
                {
                    // A finished run's checkpoint is litter: the audit found two of them sitting in
                    // the repository, left by runs that had completed months of work ago. The kept-set
                    // snapshot goes with it - and the sink has to be told to stop writing it, or the
                    // caller's own Finish() would put it straight back.
                    if (sink is BoundedResultSink done2) done2.SnapshotPath = null;
                    CheckpointStore.Retire(checkpointPath);
                    checkpointPath = null;
                }
                else if (worthIt)
                {
                    SaveCheckpoint(checkpoint, checkpointPath, emit, emittedPassed, seconds, sink, ckp);
                }
                else
                {
                    checkpointPath = null;
                }
            }

            top.Sort(CompareResults);
            if (top.Count > _q.Query.Search.Keep) top.RemoveRange(_q.Query.Search.Keep, top.Count - _q.Query.Search.Keep);

            BoundedResultSink? bounded = sink as BoundedResultSink;
            return new SearchOutcome
            {
                Evaluated = emittedSeeds,
                Passed = emittedPassed,
                Seconds = seconds,
                SeedsPerSecond = legSeconds > 0 ? (emittedSeeds - options.ResumedEvaluated) / legSeconds : 0,
                Blocks = _plan.Blocks,
                BlocksDone = emit,
                Complete = complete,

                // Only a run the wall actually cut short. A worker that sees the wall after the last
                // block has already been claimed still sets the flag, so a run that FINISHED used to
                // report complete and stopped_by_wall together (and the web page, which tests the wall
                // first, said "the wall-clock budget ran out" on a finished run). Guarding the flag at
                // the claim races instead: another worker can take the last block between the check
                // and the flag. `complete` is decided after every worker has joined, so this cannot
                // lose a true stop - blocks left unclaimed means incomplete (review of 2026-09-24).
                StoppedByWall = _wallHit && !complete,

                // The same rule for a Stop, for the same reason. A Stop pressed after the last block was
                // claimed stops nothing - every block is finished - and a funnel's stage one reported so
                // was thrown away as "stopped after 400 of 400 seeds ... not of the range asked for", on
                // both front ends, which test StoppedByUser || StoppedByWall (review of 2026-09-24).
                StoppedByUser = _userStop && !complete,
                StoppedByLimit = _limitHit,
                ProbeAccepts = Interlocked.Read(ref _probeAccepts),
                EarlyExits = Interlocked.Read(ref _earlyExits),
                LocationAborts = Interlocked.Read(ref _locationAborts),
                LocationSkips = Interlocked.Read(ref _locationSkips),
                LocationsPlaced = Interlocked.Read(ref _locationsPlaced),
                LocationSeconds = _locationSeconds,
                ConstructSeconds = _constructSeconds,
                SampleSeconds = _sampleSeconds,
                PregenSeconds = _pregenSeconds,
                Pregenerated = Interlocked.Read(ref _pregenerated),
                Threads = _threads,
                BusyWorkers = Volatile.Read(ref _busyWorkers),
                Top = top,
                ResultsPath = sink?.Path,
                ResultsWritten = sink?.Kept ?? 0,
                ResultsDropped = sink?.Dropped ?? 0,
                CutScore = bounded?.Set.CutScore ?? double.NaN,
                FirstDropScore = bounded?.Set.FirstDropScore ?? double.NaN,
                Bounded = sink?.IsBounded ?? false,
                CheckpointPath = checkpointPath,
                PeakPendingBlocks = _peakPending,
            };
        }

        private void Worker(ConcurrentDictionary<long, List<SeedResult>> done, ManualResetEventSlim blockReady,
                            ManualResetEventSlim roomReady, Stopwatch sw, double wallSeconds, int maxPending)
        {
            ISeedEvaluator ev = EvaluatorFactory != null ? EvaluatorFactory() : new SeedEvaluator(_q, _oracle);
            bool claimed = false;
            try
            {
                while (true)
                {
                    if (_stop) break;
                    if (sw.Elapsed.TotalSeconds >= wallSeconds) { _wallHit = true; break; }

                    // Back-pressure. Without it, 16 workers outrun one collector and the finished
                    // blocks pile up until the machine runs out of memory (measured: 7,599 MB after
                    // 120 s, climbing 60 MB/s). Waiting here costs throughput only when the collector
                    // is the bottleneck, which is exactly when the queue would otherwise grow.
                    while (!_stop && done.Count >= maxPending)
                    {
                        roomReady.Wait(20);
                        roomReady.Reset();
                        if (sw.Elapsed.TotalSeconds >= wallSeconds) { _wallHit = true; break; }
                    }

                    if (_stop || _wallHit) break;

                    long block = Interlocked.Increment(ref _nextClaim) - 1;
                    if (block >= _plan.Blocks) break;

                    // Counted at the claim, once per worker: the workers that had work, which is what
                    // the rate at the end was measured on (SearchOutcome.BusyWorkers).
                    if (!claimed)
                    {
                        claimed = true;
                        Interlocked.Increment(ref _busyWorkers);
                    }

                    long lo = _plan.BlockStart(block), hi = _plan.BlockEnd(block);
                    List<SeedResult> hits = new List<SeedResult>();
                    for (long i = lo; i < hi; i++)
                    {
                        SeedResult r = ev.Evaluate(_plan.SeedAt(i));
                        if (!r.Pass) continue;

                        // The typeable seed text is a pure function of the seed and it is not cheap.
                        // It used to be computed by the collector, one thread for all 16 workers'
                        // output, which made the collector the bottleneck and was half the reason the
                        // block queue grew without bound. It is per-seed work, so it belongs here.
                        r.SeedText = SeedLab.Seeds.SeedText.Invert(r.Seed, SeedLab.Seeds.SeedAlphabet.Alnum62);
                        hits.Add(r);
                    }

                    done[block] = hits;
                    int pending = done.Count;
                    int seen = Volatile.Read(ref _peakPending);
                    while (pending > seen)
                    {
                        int was = Interlocked.CompareExchange(ref _peakPending, pending, seen);
                        if (was == seen) break;
                        seen = was;
                    }

                    Interlocked.Increment(ref _blocksFinished);
                    blockReady.Set();
                }
            }
            finally
            {
                lock (_statLock)
                {
                    _constructSeconds += ev.ConstructSeconds;
                    _sampleSeconds += ev.SampleSeconds;
                    _pregenSeconds += ev.PregenSeconds;
                    _locationSeconds += ev.LocationSeconds;
                }

                Interlocked.Add(ref _pregenerated, ev.Pregenerated);

                Interlocked.Add(ref _probeAccepts, ev.ProbeAccepts);
                Interlocked.Add(ref _earlyExits, ev.EarlyExits);
                Interlocked.Add(ref _locationAborts, ev.LocationAborts);
                Interlocked.Add(ref _locationSkips, ev.LocationSkips);
                Interlocked.Add(ref _locationsPlaced, ev.Placed);
                blockReady.Set();
            }
        }

        /// <summary>
        /// Hands one slice of REAL work to the estimator, so the projection after it is built on what
        /// this run achieved rather than on a dry run's eight warmed-up seeds (which were measured
        /// 35x optimistic).
        /// </summary>
        private void ObserveSlice(RunOptions options, Stopwatch sw, long emit, long passed, IResultSink? sink,
                                  ref long sliceSeeds, ref long sliceMatches, ref double sliceSeconds)
        {
            if (options.Estimator == null) return;
            long seedsNow = Math.Min(_plan.Limit, emit * (long)_plan.BlockSize);
            double secondsNow = sw.Elapsed.TotalSeconds;
            long ds = seedsNow - sliceSeeds;
            double dt = secondsNow - sliceSeconds;
            if (ds <= 0 || dt <= 0) return;

            options.Estimator.Observe(ds, dt, passed - sliceMatches, sink?.FileBytes ?? -1, sink?.Count ?? 0);
            sliceSeeds = seedsNow;
            sliceMatches = passed;
            sliceSeconds = secondsNow;
        }

        private void SaveCheckpoint(Checkpoint c, string path, long nextBlock, long passed, double seconds,
                                    IResultSink? sink, CheckpointPolicy policy)
        {
            c.NextBlock = nextBlock;
            c.SeedsEvaluated = Math.Min(_plan.Limit, nextBlock * (long)_plan.BlockSize);
            c.SeedsPassed = passed;
            c.ElapsedSeconds = seconds;
            c.ResultsLength = sink?.Length ?? -1;
            c.ResultsPath = sink?.Path;
            c.ResultsKept = sink?.Kept ?? 0;
            c.ProbeAccepts = Interlocked.Read(ref _probeAccepts);
            c.EarlyExits = Interlocked.Read(ref _earlyExits);

            // A bounded run's resumable state is its kept set, not a byte offset: snapshot it in the
            // same step, so the checkpoint and the set on disk always describe the same moment.
            if (sink is BoundedResultSink b && policy.SnapshotPath != null)
            {
                b.SnapshotPath = policy.SnapshotPath;
                b.Set.SaveSnapshot(policy.SnapshotPath);
                c.KeptSnapshot = policy.SnapshotPath;
            }

            c.Save(path);
        }

        private Progress Snapshot(Stopwatch sw, long emit, long passed, double resumedSeconds, IResultSink? sink,
                                  int pending)
        {
            // Report what has been EMITTED, not what happens to have finished: the emitted count is the
            // one the checkpoint can promise, so it is the one the ETA should be built on.
            long ev = Math.Min(_plan.Limit, emit * (long)_plan.BlockSize);
            double leg = sw.Elapsed.TotalSeconds;
            long start = Math.Min(_plan.Limit, _plan.BlockStart(_startBlock));
            double rate = leg > 0 ? (ev - start) / leg : 0;
            return new Progress
            {
                Evaluated = ev,
                Passed = passed,
                Limit = _plan.Limit,
                Seconds = leg + resumedSeconds,
                SeedsPerSecond = rate,
                EtaSeconds = rate > 0 ? (_plan.Limit - ev) / rate : double.NaN,
                FractionOfSpace = _plan.FractionOfSpace,
                FractionCovered = ev / 4294967296.0,
                ProbeAccepts = Interlocked.Read(ref _probeAccepts),
                EarlyExits = Interlocked.Read(ref _earlyExits),
                Kept = sink?.Kept ?? 0,
                ResultBytes = sink?.Length ?? -1,
                PendingBlocks = pending,
            };
        }

        private static bool AllDone(Thread[] workers)
        {
            foreach (Thread t in workers)
            {
                if (t.IsAlive) return false;
            }

            return true;
        }

        /// <summary>Score descending, then seed ascending. An explicit total order, so ties never wobble.</summary>
        public static int CompareResults(SeedResult a, SeedResult b)
        {
            int c = b.Score.CompareTo(a.Score);
            return c != 0 ? c : a.Seed.CompareTo(b.Seed);
        }

        private static void Keep(List<SeedResult> top, SeedResult r, int keep)
        {
            top.Add(r);
            if (top.Count <= keep * 2L) return;
            top.Sort(CompareResults);
            top.RemoveRange(keep, top.Count - keep);
        }
    }
}
