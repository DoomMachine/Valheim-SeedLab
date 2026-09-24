using System;
using System.Collections.Generic;
using System.Globalization;
using SeedLab.Search.Criteria;
using SeedLab.Search.Evaluation;
using SeedLab.Search.Locations;
using SeedLab.Search.Output;

namespace SeedLab.Search.Execution
{
    /// <summary>A sink that keeps the passing SEEDS and nothing else - the whole point of stage one.</summary>
    public sealed class SurvivorSink : IResultSink
    {
        private readonly List<int> _seeds = new List<int>();

        /// <summary>The surviving seeds, in the order stage one visited them.</summary>
        public IReadOnlyList<int> Seeds => _seeds;

        public string Path => "(survivors, in memory)";

        /// <summary>
        /// Stage one holds four bytes per survivor and nothing else, so a run that would blow memory
        /// up is one that would also have written an unusable survivor list. The cap is checked here
        /// too rather than only at write time, so the failure happens before the memory is taken.
        /// </summary>
        public long MaxSeeds = 512L * 1024 * 1024 / 4;

        public void Add(SeedResult r)
        {
            if (_seeds.Count >= MaxSeeds)
            {
                throw new InvalidOperationException(
                    "stage one has kept " + _seeds.Count.ToString("N0", CultureInfo.InvariantCulture)
                    + " survivors, which is more than a funnel can pay for. Its cheap goals are not "
                    + "filtering; tighten one, or run --strategy sample instead.");
            }

            _seeds.Add(r.Seed);
        }

        public void Flush()
        {
        }

        public void Finish()
        {
        }

        /// <summary>-1: nothing is appended to a file, so there is no resume offset.</summary>
        public long Length => -1;

        /// <summary>Zero: stage one writes no results file. The survivor list is written afterwards,
        /// by the caller, once the count is final and the ceiling has been checked against it.</summary>
        public long FileBytes => 0;

        public long Count => _seeds.Count;

        /// <summary>Every survivor is kept - stage one exists to keep them all, and a bounded stage
        /// one would silently shrink the search.</summary>
        public long TotalMatches => _seeds.Count;

        public long Kept => _seeds.Count;

        public long Dropped => 0;

        public bool IsBounded => false;

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// The measured gate between a funnel's two stages: what stage one found, and what stage two will
    /// therefore cost.
    ///
    /// <para>Every number here is <b>measured</b>, not projected - that is the entire reason the
    /// staged funnel exists when the per-seed ladder already skips placement for a settled seed. The
    /// per-seed placement cost is the one input that is still a measurement of a sample, and it is
    /// labelled as such wherever it is printed.</para>
    /// </summary>
    public sealed class FunnelGate
    {
        public long Scanned;
        public long Survivors;

        /// <summary>
        /// Stage one's wall time, or <b>-1</b> when this process did not run it - a resumed run reuses
        /// an earlier survivor list, and reporting 0.0 s for work that happened yesterday would be a
        /// fabricated number rather than a missing one.
        /// </summary>
        public double StageOneSeconds = -1;

        /// <summary>Measured seconds per seed for the FULL query, from the calibration sample.</summary>
        public double SecondsPerSurvivor;

        /// <summary>
        /// Stage two's block size, decided ONCE (<see cref="BlockSizing.Decide"/> over the survivor
        /// count and the thread count) and given both to this gate and to stage two's plan, so the
        /// idle count printed here is the one the run has. It used to be <c>q.Search.BlockSize</c> read
        /// separately by the gate and by the plan, and the stage whose few survivors left workers idle
        /// never said why, or that anything could be done about it. A resumed stage two's decision
        /// carries its checkpoint's resume point, and everything below counts only what is left.
        /// </summary>
        public BlockSizeDecision Decision = BlockSizing.Decide(null, 0, 1);

        /// <summary>The worker count stage two runs on.</summary>
        public int Threads => Decision.Workers;

        /// <summary>
        /// The run's wall budget, which stage two gets whole (as stage one did), or zero for none. Set,
        /// it adds stage two's own bound under the gate: the plan's budget line was worked out for
        /// stage one's blocks, and a stage two cut into bigger blocks - fewer survivors on fewer
        /// threads, or a checkpoint written on fewer - can run further past the budget than that
        /// line said (review of 2026-09-24).
        /// </summary>
        public TimeSpan Wall;

        /// <summary>Seeds per block - the unit ONE worker takes. Needed for an honest estimate.</summary>
        public int BlockSize => Decision.Size;

        public double Ratio => Scanned > 0 ? Survivors / (double)Scanned : 0.0;

        /// <summary>Blocks stage two is cut into.</summary>
        public long Blocks => Math.Max(1, (Survivors + Math.Max(1, BlockSize) - 1) / Math.Max(1, BlockSize));

        /// <summary>True when an earlier leg already finished some of stage two's blocks.</summary>
        public bool Resumed => Decision.NextBlock > 0;

        /// <summary>Survivors this leg places: all of them, or those after the resume point.</summary>
        public long ToPlace => Decision.Limit > 0 ? Decision.RemainingSeeds : Survivors;

        /// <summary>
        /// Workers that will actually have something to do.
        ///
        /// <para><b>Not the thread count.</b> One worker computes a whole block, so a stage two with
        /// fewer blocks than threads runs at the parallelism of its BLOCK COUNT and no more.</para>
        /// </summary>
        public int EffectiveWorkers => Math.Max(1, Decision.BusyWorkers);

        /// <summary>
        /// Seeds the BUSIEST worker takes - which is what stage two's wall clock actually is.
        ///
        /// <para>Two corrections live in the formula, both found by measurement rather than by
        /// reading. Dividing by the thread count gave 2.1 s for a run that took 16.7 s, because 15
        /// survivors at a block size of 256 is one block and one worker. Dividing by the effective
        /// worker count then gave 16.9 s for a run that took 33.6 s, because blocks are not equal: 26
        /// survivors at a block size of 25 is a block of 25 and a block of 1, and the worker holding
        /// the 25 decides when the stage ends. An average over unequal blocks is not a wall clock.</para>
        ///
        /// <para>So the blocks are dealt round-robin - which is how they are claimed - and the
        /// heaviest worker's load is the answer. The formula moved to
        /// <see cref="BlockSizing.BusiestWorkerSeeds"/> on 2026-09-24 so the plan line, the idle
        /// warning and this estimate cannot disagree.</para>
        /// </summary>
        public long BusiestWorkerSeeds => Decision.Limit > 0
            ? Decision.BusiestWorkerSeeds
            : BlockSizing.BusiestWorkerSeeds(Survivors, BlockSize, Threads);

        /// <summary>Wall-clock estimate for stage two: the busiest worker, not the average.</summary>
        public double StageTwoSeconds => BusiestWorkerSeeds * SecondsPerSurvivor;

        /// <summary>Bytes the survivor list occupies on disk.</summary>
        public long SurvivorBytes => SurvivorList.BytesFor(Survivors);

        /// <summary>
        /// True when the funnel did not earn its keep: stage one rejected so little that stage two is
        /// nearly the whole range anyway. Reported rather than silently tolerated, because the user
        /// asked for a strategy and deserves to know it did not apply.
        /// </summary>
        public bool Pointless => Ratio > 0.10;

        public IEnumerable<string> Lines()
        {
            yield return "stage 1 scanned      " + N(Scanned) + " seeds"
                         + (StageOneSeconds >= 0 ? " in " + Dur(StageOneSeconds)
                                                 : "  (reused from an earlier stage 1, so its wall "
                                                   + "time is not this run's to report)");
            yield return "stage 1 kept         " + N(Survivors) + "  (" + Pct(Ratio) + " of them)";
            yield return "survivor list        " + Bytes(SurvivorBytes);
            yield return "stage 2 will place   " + N(ToPlace) + " worlds at "
                         + F(SecondsPerSurvivor, 2) + " s each, measured"
                         + (Resumed ? "  (the other " + N(Survivors - ToPlace) + " were placed by an earlier leg)" : "");
            yield return "stage 2 estimate     " + Dur(StageTwoSeconds) + " on "
                         + EffectiveWorkers.ToString(CultureInfo.InvariantCulture) + " worker"
                         + (EffectiveWorkers == 1 ? "" : "s");

            // Nothing to place is nothing to spread: at zero survivors this said "0 survivors at a
            // block size of 1 is 1 block over 1 of 8 threads" (review of 2026-09-24).
            long blocks = Resumed ? Decision.RemainingBlocks : Blocks;
            if (ToPlace > 0 && (EffectiveWorkers < Threads || BusiestWorkerSeeds * EffectiveWorkers != ToPlace))
            {
                yield return "                     (" + N(ToPlace) + (Resumed ? " remaining" : "")
                             + " survivors at a block size of "
                             + BlockSize.ToString(CultureInfo.InvariantCulture) + " is " + N(blocks)
                             + " block" + (blocks == 1 ? "" : "s") + " over "
                             + EffectiveWorkers.ToString(CultureInfo.InvariantCulture) + " of "
                             + Threads.ToString(CultureInfo.InvariantCulture) + " thread"
                             + (Threads == 1 ? "" : "s") + "; one worker takes a whole block, and the "
                             + "busiest ends up with " + N(BusiestWorkerSeeds) + " seeds - that worker "
                             + "is what the stage waits for)";
            }

            // The decision's own sentences - automatic, given or adopted, and the warning when a size
            // that was given leaves workers idle - under the gate's label, so both front ends show
            // stage two's block size the way the plan block shows the main run's. A refusal (a size
            // asked for that is not a resumed stage two's own) is not a gate line: only a resume can
            // raise it, and the front end that resumes stops on it with its own refusal block.
            if (Decision.Note != null) yield return "stage 2 block size   " + Decision.Note;

            // Stage two's own budget bound, in the plan's words: the plan's budget line counts stage
            // one's blocks, and stage two's are sized here over the survivors (or kept from its
            // checkpoint), so its overrun is one of THESE blocks.
            if (Wall > TimeSpan.Zero && ToPlace > 0)
            {
                yield return "stage 2 budget       " + SearchPreflightCheck.BudgetBound(Wall, Decision, "stage");
            }

            if (Decision.Warning != null)
            {
                yield return "";
                yield return "WARNING: " + Decision.Warning;
            }

            if (Pointless)
            {
                yield return "";
                yield return "NOTE: stage 1 rejected only " + Pct(1.0 - Ratio) + " of the seeds it saw, "
                             + "so the funnel is saving very little here - almost every seed still "
                             + "needs placing. A cheap must-have goal that actually discriminates "
                             + "would change that; --strategy sample is the honest alternative.";
            }
        }

        private static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

        private static string F(double v, int d)
            => v.ToString("F" + d.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

        private static string Pct(double f)
            => (f * 100).ToString(f < 0.0001 ? "0.######" : "0.###", CultureInfo.InvariantCulture) + " %";

        private static string Bytes(long b)
        {
            if (b >= 1L << 30) return F(b / (double)(1L << 30), 2) + " GiB";
            if (b >= 1L << 20) return F(b / (double)(1L << 20), 2) + " MiB";
            if (b >= 1L << 10) return F(b / (double)(1L << 10), 2) + " KiB";
            return N(b) + " B";
        }

        private static string Dur(double s)
        {
            if (s < 90) return F(s, 1) + " s";
            if (s < 5400) return F(s / 60, 1) + " min";
            if (s < 172800) return F(s / 3600, 1) + " h";
            return F(s / 86400, 1) + " days";
        }
    }
}
