using System;
using System.Globalization;
using SeedLab.Search.Criteria;

namespace SeedLab.Search.Execution
{
    /// <summary>Where a run's block size came from. Every one of them is said out loud except <see cref="Default"/>.</summary>
    public enum BlockSizeSource
    {
        /// <summary>
        /// Automatic, and the ceiling already gives every worker at least four blocks - or there is
        /// only one worker, which no block size can help. Nothing to say beyond the plan's own
        /// "N seeds in B blocks of S".
        /// </summary>
        Default,

        /// <summary>Automatic, and shrunk below the ceiling so that every worker gets blocks.</summary>
        Automatic,

        /// <summary>Given by the user: <c>--block-size</c>, <c>search.block_size</c> or the web's Block size box.</summary>
        Explicit,

        /// <summary>Taken from the checkpoint this run resumes, whose boundaries cannot move.</summary>
        Adopted,
    }

    /// <summary>
    /// Where a VERIFIED checkpoint says the run stands: its block size and the first block that is not
    /// finished. Built only from a checkpoint that matches the run on everything but the block size
    /// (<see cref="Checkpoint.MatchesExceptBlockSize"/>), so it never carries another run's boundaries
    /// into this one.
    /// </summary>
    public sealed class ResumePoint
    {
        /// <summary>Seeds per block in the checkpoint. A value below 1 is never honoured.</summary>
        public int BlockSize;

        /// <summary>Every block below this one is finished and on disk.</summary>
        public long NextBlock;

        /// <summary>The checkpoint file, for the sentence that tells the user what to delete; may be null.</summary>
        public string? Path;

        public static ResumePoint From(Checkpoint c) =>
            new ResumePoint { BlockSize = c.BlockSize, NextBlock = c.NextBlock, Path = c.LoadedFrom };
    }

    /// <summary>
    /// The block size a run uses, where it came from, what it does to the workers, and the sentences
    /// that say so. Produced only by <see cref="BlockSizing.Decide"/>, so the CLI's plan block, the web
    /// page's plan and verdict list, and the funnel gate on both all print the same words for the same
    /// numbers.
    /// </summary>
    public sealed class BlockSizeDecision
    {
        /// <summary>The label every plan line carries, so the size lines up with "threads" and "checkpoint".</summary>
        public const string Label = "block size   ";

        /// <summary>Seeds per block - the size the plan is built with.</summary>
        public int Size;

        public BlockSizeSource Source;

        /// <summary>The size the user gave, or null for automatic. Kept so the decision can be re-derived.</summary>
        public int? Requested;

        /// <summary>The largest size the automatic rule picks.</summary>
        public int Ceiling;

        /// <summary>The honoured resume point, or null for a fresh run (or a checkpoint that was not adopted).</summary>
        public ResumePoint? Resume;

        /// <summary>Seeds the run visits in total (the plan's clamped limit, not the raw budget).</summary>
        public long Limit;

        /// <summary>The worker count the decision was made for.</summary>
        public int Workers;

        /// <summary>Blocks in the whole run at <see cref="Size"/>.</summary>
        public long Blocks;

        /// <summary>The first block this leg computes: 0, or the resume point's.</summary>
        public long NextBlock;

        /// <summary>Blocks this leg computes: all of them, or those after the resume point.</summary>
        public long RemainingBlocks;

        /// <summary>Seeds this leg computes.</summary>
        public long RemainingSeeds;

        /// <summary>Workers with at least one block this leg: <c>min(workers, remaining blocks)</c>.</summary>
        public int BusyWorkers;

        /// <summary>Workers with nothing to do this leg.</summary>
        public int IdleWorkers => Math.Max(0, Workers - BusyWorkers);

        /// <summary>Seeds the busiest worker computes this leg - what its wall clock actually is.</summary>
        public long BusiestWorkerSeeds;

        /// <summary>What the automatic rule would pick for a FRESH run of the same size on the same workers.</summary>
        public int AutomaticSize;

        /// <summary>The sentence about the size, without its label, or null when there is nothing to say.</summary>
        public string? Note;

        /// <summary>A warning: a size the user gave leaves workers idle. Contains the literal <c>--block-size</c>.</summary>
        public string? Warning;

        /// <summary>
        /// A refusal: a size the user gave differs from the checkpoint this run resumes. It names the
        /// checkpoint file (<see cref="ResumePoint.Path"/>) for the user to delete, and a front end that
        /// re-flows it to a terminal keeps that path on one line: it can hold a space ("C:\Users\First
        /// Last\..."), and split at it the path could not be copied (review of 2026-09-24).
        /// </summary>
        public string? Refusal;

        /// <summary><see cref="Note"/> under the plan's label, or null.</summary>
        public string? PlanLine => Note == null ? null : Label + Note;
    }

    /// <summary>
    /// How big a block is: the one rule, shared by <c>SearchSession.Create</c>, the funnel's second
    /// stage and every front end.
    ///
    /// <para><b>Why a rule at all.</b> One worker computes a whole block, so a run cut into fewer blocks
    /// than workers leaves the rest idle, and the block count - not the thread count - is its
    /// parallelism. At the old fixed 256, <c>vseed search &lt;query&gt; --seeds 512</c> on 8 workers was
    /// 2 blocks and 6 workers did nothing; measured 2026-09-24, <c>axe-heads</c> at 64 seeds took
    /// 1.6 min as one block of 64 and 16.4 s as eight blocks of 8, with byte-identical results. A
    /// completed run's results file does not depend on the block size (measured in jsonl, json and csv,
    /// keep N and keep all, evict limits and screen-then-verify), so the size is free to choose for
    /// speed and resume granularity.</para>
    ///
    /// <para><b>Why four blocks per worker, not one.</b> Blocks are not equal in time - a cheap tier or
    /// a location gate settles some seeds early (FunnelGate records a measured 33.6 s stage whose even
    /// split said 16.9 s) - and blocks are dealt as workers free up, so 10 blocks on 8 workers is a
    /// second round of 2 that the other 6 wait for (62.5 % efficient). Four per worker bounds that
    /// tail; <c>floor</c> rather than <c>ceil</c> keeps the count between 4W and 8W-1. The overhead of
    /// more, smaller blocks was measured as nothing on 4,000 seeds at 125 against 256, and only a size
    /// of 1 (4,000 blocks) was slower.</para>
    ///
    /// <para><b>Why a resume point wins.</b> A checkpoint records a block NUMBER, so the boundaries of
    /// a run cannot move between its legs. The worker count may (the auto-throttle, free memory,
    /// <c>--threads</c>), which is why the size of a resumed run comes from its checkpoint and never
    /// from the rule, and why a size the user gives that differs is refused: for the main run in the
    /// preflight, before the prompt and before anything is scanned; for a funnel's second stage at its
    /// gate, before anything is placed - stage one runs first (or its survivor list is reused), because
    /// a stage-two checkpoint is identified by that list and cannot be recognised before it exists.</para>
    ///
    /// <para>Pure: the same inputs always give the same decision and the same sentences.</para>
    /// </summary>
    public static class BlockSizing
    {
        /// <summary>Blocks per worker the automatic rule aims for.</summary>
        public const int BlocksPerWorker = 4;

        /// <summary>What names the knob in a sentence. Contains the literal <c>--block-size</c>, which
        /// <see cref="SearchPreflight.WarningsForOneSeed"/> relies on to leave scan-only advice out of explain.</summary>
        private const string Knob = "--block-size, or search.block_size in the query, or the web's Block size box";

        public static BlockSizeDecision Decide(int? requested, long limit, int workers,
                                               int ceiling = SearchSpec.DefaultBlockSize,
                                               ResumePoint? resume = null)
        {
            if (ceiling < 1) throw new ArgumentOutOfRangeException(nameof(ceiling), "a block holds at least one seed");
            if (requested != null && requested.Value < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(requested), "a block holds at least one seed");
            }

            if (workers < 1) workers = 1;
            if (limit < 0) limit = 0;

            // A hand-edited checkpoint's 0 is not a size: ScanPlan would clamp it to 1 and the refusal
            // below would then tell the user to pass --block-size 0, which the CLI rejects. It is not
            // honoured here, and Checkpoint.MustMatch names the checkpoint invalid.
            if (resume != null && resume.BlockSize < 1) resume = null;

            int automatic = Automatic(limit, workers, ceiling);
            BlockSizeDecision d = new BlockSizeDecision
            {
                Requested = requested,
                Ceiling = ceiling,
                Resume = resume,
                Limit = limit,
                Workers = workers,
                AutomaticSize = automatic,
            };

            if (resume != null)
            {
                d.Size = resume.BlockSize;
                d.Source = requested == null ? BlockSizeSource.Adopted : BlockSizeSource.Explicit;
            }
            else if (requested != null)
            {
                d.Size = requested.Value;
                d.Source = BlockSizeSource.Explicit;
            }
            else
            {
                d.Size = automatic;
                d.Source = automatic == ceiling ? BlockSizeSource.Default : BlockSizeSource.Automatic;
            }

            d.Blocks = limit <= 0 ? 0 : (limit + d.Size - 1) / d.Size;
            d.NextBlock = resume == null ? 0 : Math.Min(Math.Max(0, resume.NextBlock), d.Blocks);
            d.RemainingBlocks = d.Blocks - d.NextBlock;
            d.RemainingSeeds = Math.Max(0, limit - d.NextBlock * d.Size);
            d.BusyWorkers = (int)Math.Min(workers, d.RemainingBlocks);
            d.BusiestWorkerSeeds = BusiestWorkerSeeds(d.RemainingSeeds, d.Size, workers);

            // Nothing to plan: a funnel whose first stage kept no seed builds its gate before it
            // returns, and "0 seeds cannot occupy more than 0 of 8 workers" is not a sentence.
            if (limit <= 0) return d;

            if (resume != null && requested != null && requested.Value != resume.BlockSize)
            {
                d.Refusal = "this checkpoint's blocks are " + N(resume.BlockSize) + " seeds and its resume point is "
                            + "block " + N(d.NextBlock) + " of " + N(d.Blocks) + " - a block number, so the block "
                            + "boundaries cannot move mid-run - and a block size of " + N(requested.Value)
                            + " was asked for (" + Knob + "). It can only continue at --block-size "
                            + N(resume.BlockSize) + " (or with the block size left out). Pass that, or delete "
                            + (resume.Path ?? "the checkpoint") + " to start again";
                return d;
            }

            switch (d.Source)
            {
                case BlockSizeSource.Adopted:
                    d.Note = N(d.Size) + ", from the checkpoint being resumed (a resume point is a block number, so it "
                             + "cannot change mid-run); " + Leg(d)
                             + (automatic != d.Size ? ". A fresh run would size it automatically at " + N(automatic) : "");
                    break;

                case BlockSizeSource.Explicit when resume != null:
                    d.Note = N(d.Size) + ", as given - and the checkpoint's own, so this resumed run keeps "
                             + N(d.Size) + ", the checkpoint's boundaries; " + Leg(d);
                    if (d.IdleWorkers > 0)
                    {
                        d.Warning = "a block size of " + N(d.Size) + " (" + Knob + ") leaves this resumed leg "
                                    + Count(d.RemainingBlocks, "block") + " (" + Count(d.RemainingSeeds, "seed")
                                    + "), and one worker computes a whole block: " + Idle(d)
                                    + ". This checkpoint can only continue at " + N(d.Size)
                                    + " (a resume point is a block number); a fresh run can leave the block size "
                                    + "out to have it sized automatically";
                    }

                    break;

                case BlockSizeSource.Explicit:
                    d.Note = N(d.Size) + ", as given: " + Count(d.Blocks, "block") + " for "
                             + Count(workers, "worker");
                    if (d.IdleWorkers > 0)
                    {
                        long autoBlocks = (limit + automatic - 1) / automatic;
                        d.Warning = "a block size of " + N(d.Size) + " (" + Knob + ") cuts these "
                                    + Count(limit, "seed") + " into " + Count(d.Blocks, "block")
                                    + ", and one worker computes a whole block: " + Idle(d)
                                    + "; left out, it would be sized automatically (" + N(automatic)
                                    + " per block, the busiest computing "
                                    + N(BusiestWorkerSeeds(limit, automatic, workers))
                                    + (autoBlocks < workers
                                        ? "; " + Count(limit, "seed") + " cannot occupy more than " + N(limit)
                                          + " of the " + N(workers) + " workers whatever the size"
                                        : "")
                                    + ")";
                    }

                    break;

                case BlockSizeSource.Automatic:
                    d.Note = N(d.Size) + ", sized automatically for this run: " + WhyNotTheCeiling(d);
                    break;
            }

            return d;
        }

        /// <summary>
        /// The automatic size: the ceiling when it already gives every worker at least
        /// <see cref="BlocksPerWorker"/> blocks (or there is one worker, which no size can help), else
        /// <c>floor(limit / (4 x workers))</c>, never below 1. Computed in long arithmetic: a whole-space
        /// run's limit is 2^32 and 4 x workers is at most 4 x max(64, 4 x cores).
        /// </summary>
        public static int Automatic(long limit, int workers, int ceiling = SearchSpec.DefaultBlockSize)
        {
            if (ceiling < 1) throw new ArgumentOutOfRangeException(nameof(ceiling), "a block holds at least one seed");
            if (limit <= 0) return 1;
            if (workers <= 1) return ceiling;
            long target = (long)BlocksPerWorker * workers;
            long atCeiling = (limit + ceiling - 1) / ceiling;
            if (atCeiling >= target) return ceiling;
            long size = limit / target;
            return (int)Math.Max(1, Math.Min(ceiling, size));
        }

        /// <summary>
        /// Seeds the busiest worker computes when <paramref name="seeds"/> are cut into blocks of
        /// <paramref name="blockSize"/> and dealt round-robin to <paramref name="workers"/> - which is
        /// how blocks of equal cost are claimed. Every block is full except the last.
        ///
        /// <para>Lifted from <c>FunnelGate.BusiestWorkerSeeds</c> (2026-09-24) so the gate, the plan
        /// line, the warning and the estimate share one formula. That loop dealt the blocks one by one,
        /// which is fine for a survivor list and not for a whole-space plan of 16.7 million blocks, so
        /// this is the same answer in closed form (Search.Tests section 11 compares the two). Two
        /// measured corrections live in it: dividing by the thread count gave 2.1 s for a stage that
        /// took 16.7 s (15 survivors at 256 per block is one block and one worker), and dividing by
        /// the busy workers gave 16.9 s for one that took 33.6 s (26 survivors at 25 per block is a
        /// block of 25 and a block of 1).</para>
        /// </summary>
        public static long BusiestWorkerSeeds(long seeds, int blockSize, int workers)
        {
            if (seeds <= 0) return 0;
            long bs = Math.Max(1, blockSize);
            long w = Math.Max(1, workers);
            long blocks = (seeds + bs - 1) / bs;
            long last = seeds - (blocks - 1) * bs;
            long rounds = blocks / w;
            long extra = blocks % w;

            // Every worker has `rounds` blocks and the last worker holds the short one: any other
            // worker has `rounds` full ones. One worker has them all.
            if (extra == 0) return w == 1 ? seeds : rounds * bs;

            // Workers 0..extra-1 have one more block, and worker extra-1 holds the short one: with two
            // or more of them, worker 0's are all full; with one, it is the only one with the extra.
            return extra >= 2 ? (rounds + 1) * bs : rounds * bs + last;
        }

        // ---- the sentences -------------------------------------------------------------------------

        /// <summary>Why the automatic rule left the ceiling, in the case that applies.</summary>
        private static string WhyNotTheCeiling(BlockSizeDecision d)
        {
            long limit = d.Limit;
            int w = d.Workers;
            if (limit < w)
            {
                return Count(limit, "seed") + " cannot occupy more than " + N(limit) + " of the " + N(w)
                       + " workers, so a block is one seed and " + N(w - limit) + " of them have nothing to do "
                       + "whatever the size";
            }

            int c = d.Ceiling;
            long atCeiling = (limit + c - 1) / c;
            string why;
            if (atCeiling < w)
            {
                why = "the default " + N(c) + " would cut these " + Count(limit, "seed") + " into "
                      + Count(atCeiling, "block") + ", and one worker computes a whole block, so "
                      + N(w - atCeiling) + " of the " + N(w) + " workers would have nothing to do";
            }
            else
            {
                // The round-robin reason only where it is TRUE of the size chosen: 2,000 seeds on 8
                // workers is 8 blocks at 256 (busiest 256) and 33 at 62 (busiest 264), so for equal
                // blocks the smaller size is no better there, and the reason it is chosen is the other
                // one - blocks are not equal in time.
                long busiest = BusiestWorkerSeeds(limit, c, w);
                long even = (limit + w - 1) / w;
                why = busiest > even && busiest > d.BusiestWorkerSeeds
                    ? "the default " + N(c) + " would be " + N(atCeiling) + " blocks for " + N(w) + " workers, "
                      + "and one worker computes a whole block: dealt round-robin, the busiest would compute "
                      + N(busiest) + " seeds against an even share of " + N(even)
                    : "the default " + N(c) + " would be " + N(atCeiling) + " blocks for " + N(w) + " workers, "
                      + "fewer than " + N(BlocksPerWorker) + " each, and blocks are not equal in time (a cheap "
                      + "tier or a location gate settles some seeds early), so the worker that draws the slow "
                      + "ones decides when the run ends";
            }

            return why + "; at " + N(d.Size) + " per block it is " + Count(d.Blocks, "block")
                   + " and the busiest worker computes " + Count(d.BusiestWorkerSeeds, "seed");
        }

        /// <summary>What a resumed leg has left, and who is idle for it.</summary>
        private static string Leg(BlockSizeDecision d)
        {
            if (d.RemainingBlocks <= 0) return "all " + Count(d.Blocks, "block") + " are finished, none is left";
            string s = "block " + N(d.NextBlock) + " of " + N(d.Blocks) + " is next, so this leg has "
                       + Count(d.RemainingBlocks, "block") + " (" + Count(d.RemainingSeeds, "seed") + ") left";
            if (d.IdleWorkers > 0 && d.Source == BlockSizeSource.Adopted)
            {
                s += ", and one worker computes a whole block: " + Idle(d);
            }

            return s;
        }

        private static string Idle(BlockSizeDecision d)
            => N(d.IdleWorkers) + " of the " + N(d.Workers) + " workers have nothing to do, and the busiest "
               + "computes " + Count(d.BusiestWorkerSeeds, "seed") + " on its own";

        private static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

        private static string Count(long n, string noun) => N(n) + " " + noun + (n == 1 ? "" : "s");
    }
}
