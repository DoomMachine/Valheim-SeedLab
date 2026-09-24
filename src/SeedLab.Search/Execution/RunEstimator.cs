using System;
using System.Collections.Generic;
using System.Globalization;
using SeedLab.Search.Output;

namespace SeedLab.Search.Execution
{
    /// <summary>A projection, always as a range, always with the evidence it rests on.</summary>
    public sealed class RunEstimate
    {
        /// <summary>Seeds this run still has to evaluate.</summary>
        public long SeedsRemaining;

        /// <summary>The measured rate, seeds/s, from real work already done by THIS run.</summary>
        public double RateLow, RateHigh;

        public double WallSecondsLow, WallSecondsHigh;

        /// <summary>Matches projected for the whole run, from the measured hit rate.</summary>
        public long MatchesLow, MatchesHigh;

        /// <summary>Result bytes projected for the whole run, from the measured bytes per record.</summary>
        public long BytesLow, BytesHigh;

        public long FreeBytes = -1;

        /// <summary>Seeds the projection is based on. Zero means nothing has been measured yet.</summary>
        public long BasedOnSeeds;

        /// <summary>Slices measured so far. One slice is a point; the range widens as they accumulate.</summary>
        public int Slices;

        /// <summary>True when the projected output fits in the free space with a 5 % margin.</summary>
        public bool FitsOnDisk => FreeBytes < 0 || BytesHigh <= FreeBytes * 0.95;

        /// <summary>The sentence that says what this is built on. Never absent.</summary>
        public string Basis = "nothing measured yet";

        public string WallText => Range(WallSecondsLow, WallSecondsHigh, Duration);

        public string BytesText => Range(BytesLow, BytesHigh, b => CheckpointStore.Bytes((long)b));

        public string MatchesText => Range(MatchesLow, MatchesHigh,
                                           v => ((long)v).ToString("N0", CultureInfo.InvariantCulture));

        private static string Range(double lo, double hi, Func<double, string> f)
            => lo <= 0 && hi <= 0 ? "-" : f(lo) + " .. " + f(hi);

        public static string Duration(double s)
        {
            if (double.IsNaN(s) || double.IsInfinity(s)) return "unknown";
            if (s < 90) return s.ToString("0.#", CultureInfo.InvariantCulture) + " s";
            if (s < 5400) return (s / 60).ToString("0.#", CultureInfo.InvariantCulture) + " min";
            if (s < 172800) return (s / 3600).ToString("0.#", CultureInfo.InvariantCulture) + " h";
            if (s < 63072000) return (s / 86400).ToString("0.#", CultureInfo.InvariantCulture) + " days";
            return (s / 31557600).ToString("0.#", CultureInfo.InvariantCulture) + " years";
        }
    }

    /// <summary>
    /// Projects a run's time and disk from <b>work this run has actually done</b>, and re-projects
    /// after every slice.
    ///
    /// <para><b>Why the old estimate was wrong by 35x.</b> <c>--dry-run</c> timed eight seeds on one
    /// thread after a warm-up and scaled the result by the thread count. Measured on this machine, it
    /// predicted <b>62,080 seeds/s</b> where the real run managed <b>1,749</b>. Three things it could
    /// not see: eight seeds are not a sample of a 4.29e9-seed range (the cheap tiers reject most seeds
    /// early, and which ones is seed-dependent); per-thread scaling is not linear (the biome tier
    /// scales to 84 % at 16 threads, the height and location tiers to 54-58 %, with per-seed CPU up 44
    /// to 72 %); and the collector, the writer and the checkpoint cost nothing in a dry run and
    /// something in a real one.</para>
    ///
    /// <para>So this class does not model any of that. It runs the first slice <i>for real</i>, with
    /// every thread, every write and every checkpoint in place, and projects from the rate that slice
    /// actually achieved. After each further slice it re-projects, and the range it quotes spans the
    /// slowest and fastest slice seen - so a projection is never a single number that the next minute
    /// can contradict.</para>
    ///
    /// <para><b>Overlap to resolve.</b> <c>SeedLab.Runtime.Estimation.Calibration</c> has the same
    /// slice-observation design, arrived at independently on the same day. This one exists inside the
    /// engine because <see cref="SearchRun"/> feeds it from its own collector loop, where the slice
    /// boundary and the checkpoint are the same moment; the Runtime one is the general version with no
    /// dependency on the search. A front end should wire exactly ONE of them - and the cleaner end
    /// state is for <c>SeedLab.Search</c> to reference <c>SeedLab.Runtime</c> and for this class to
    /// become the adapter that hands slices to <c>Calibration.ObserveSlice</c>.</para>
    /// </summary>
    public sealed class RunEstimator
    {
        private readonly List<double> _sliceRates = new List<double>();
        private long _seeds;
        private double _seconds;
        private long _matches;
        private long _bytes;
        private long _records;

        /// <summary>The estimate's own honesty check: times the projection was overtaken by reality.</summary>
        public int Violations { get; private set; }

        /// <summary>Widening factor applied after a violation, so the next projection cannot repeat it.</summary>
        public double Slack { get; private set; } = 1.0;

        /// <summary>Records one finished slice of real work.</summary>
        public void Observe(long seeds, double seconds, long matches, long resultBytes, long records)
        {
            if (seeds <= 0 || seconds <= 0) return;
            double rate = seeds / seconds;

            // Honesty check: did the last projection already claim a rate this slice has disproved?
            if (_sliceRates.Count > 0)
            {
                double lo = double.MaxValue, hi = 0;
                foreach (double r in _sliceRates)
                {
                    if (r < lo) lo = r;
                    if (r > hi) hi = r;
                }

                if (rate < lo / Slack || rate > hi * Slack)
                {
                    Violations++;
                    Slack = Math.Min(4.0, Slack * 1.5);
                }
            }

            _sliceRates.Add(rate);
            _seeds += seeds;
            _seconds += seconds;
            _matches += matches;
            if (resultBytes >= 0) _bytes = resultBytes;
            _records = records;
        }

        public long SeedsMeasured => _seeds;

        /// <summary>The measured hit rate so far, matches per seed.</summary>
        public double HitRate => _seeds > 0 ? (double)_matches / _seeds : 0;

        /// <summary>
        /// Bytes per record, MEASURED on this run's own file once it holds records, and from the
        /// measured law (<see cref="RecordFormatter.EstimatedBytesPerRecord"/>) before that.
        /// </summary>
        public double BytesPerRecord(ResultFormat format, int goals)
            => _records > 0 && _bytes > 0
                ? (double)_bytes / _records
                : RecordFormatter.EstimatedBytesPerRecord(format, goals);

        /// <summary>
        /// The projection for the rest of the run.
        ///
        /// <para><paramref name="seedsDone"/> is every seed this run has evaluated, which is not the
        /// same as the seeds the estimator has SEEN: the first slice only closes at the first
        /// checkpoint, so the seeds before it are real work that no slice reported. Counting only the
        /// observed ones made the projected match count fall short of the truth by exactly that gap -
        /// a projection the run itself then contradicted, which is the one thing an estimate here is
        /// not allowed to do.</para>
        /// </summary>
        public RunEstimate Project(long seedsRemaining, long seedsDone, OutputPolicy? output, int goals, string? path)
        {
            RunEstimate e = new RunEstimate
            {
                SeedsRemaining = Math.Max(0, seedsRemaining),
                BasedOnSeeds = _seeds,
                Slices = _sliceRates.Count,
                FreeBytes = path != null ? CheckpointStore.FreeBytes(path) : -1,
            };

            if (_sliceRates.Count == 0 || _seconds <= 0)
            {
                e.Basis = "nothing has been measured yet - no time or size is claimed";
                return e;
            }

            double lo = double.MaxValue, hi = 0;
            foreach (double r in _sliceRates)
            {
                if (r < lo) lo = r;
                if (r > hi) hi = r;
            }

            // One slice is a point, not a range. Widen it by a factor that says so rather than
            // pretending to a precision a single measurement cannot have.
            double widen = _sliceRates.Count == 1 ? 1.5 : 1.0;
            e.RateLow = lo / (widen * Slack);
            e.RateHigh = hi * widen * Slack;

            e.WallSecondsLow = e.SeedsRemaining / e.RateHigh;
            e.WallSecondsHigh = e.SeedsRemaining / e.RateLow;

            // The match count, with a Poisson-flavoured interval on the observed hits: with few hits
            // so far, the projection has to be wide, and saying so is the point.
            double p = HitRate;
            double n = Math.Max(1, _seeds);
            double se = Math.Sqrt(Math.Max(p * (1 - p) / n, 1.0 / (n * n)));
            double pLow = Math.Max(0, p - 2 * se), pHigh = Math.Min(1, p + 2 * se);
            long total = e.SeedsRemaining + Math.Max(seedsDone, _seeds);
            e.MatchesLow = (long)(pLow * total);
            e.MatchesHigh = (long)Math.Ceiling(pHigh * total);

            if (output != null)
            {
                double per = BytesPerRecord(output.Format, goals);
                long recLow = output.KeepAll ? e.MatchesLow : Math.Min(e.MatchesLow, output.Keep);
                long recHigh = output.KeepAll ? e.MatchesHigh : Math.Min(e.MatchesHigh, output.Keep);
                double c = output.IsRotating && output.Compress ? 1.0 / 10.0 : 1.0;
                e.BytesLow = (long)(recLow * per * c);
                e.BytesHigh = (long)(recHigh * per * c);
            }

            e.Basis = "measured on " + _seeds.ToString("N0", CultureInfo.InvariantCulture)
                      + " seeds of this run in " + RunEstimate.Duration(_seconds)
                      + " (" + _sliceRates.Count + (_sliceRates.Count == 1 ? " slice" : " slices")
                      + ", " + (_records > 0 ? BytesPerRecord(output?.Format ?? ResultFormat.Jsonl, goals)
                                                   .ToString("0", CultureInfo.InvariantCulture) + " B/record measured"
                                             : "record size from the measured law")
                      + (Violations > 0
                            ? ", widened by " + Slack.ToString("0.0", CultureInfo.InvariantCulture) + "x after "
                              + Violations + (Violations == 1 ? " slice fell" : " slices fell")
                              + " outside the range this estimate had claimed"
                            : "")
                      + ")";
            return e;
        }

        /// <summary>
        /// A slice: a contiguous run of blocks sized to 30-120 s of work at the measured rate
        /// (decision 4). Before anything is measured it is one block, so the FIRST slice is real work
        /// rather than a guess, and everything after it is sized from what that slice achieved.
        /// </summary>
        public long SliceBlocks(int blockSize, long blocksRemaining, double targetSeconds = 60.0)
        {
            if (_sliceRates.Count == 0 || blockSize <= 0) return 1;
            double rate = 0;
            foreach (double r in _sliceRates) rate += r;
            rate /= _sliceRates.Count;
            if (rate <= 0) return 1;

            double seedsPerSlice = rate * targetSeconds;
            long blocks = (long)Math.Max(1, Math.Round(seedsPerSlice / blockSize));
            return Math.Min(blocks, Math.Max(1, blocksRemaining));
        }

        /// <summary>
        /// How many fewer cells a region restriction samples: the ratio of disc areas. It is EXACT as
        /// a statement about sampling, and it is an upper bound on the speed-up, never a promise of
        /// one.
        ///
        /// <para><b>Measured on this machine, one biome goal at the game's own 12 m grid</b> (proof
        /// harness, 8 seeds each, other agents' builds running):</para>
        /// <code>
        ///   radius  1,000 m    7.90 ms/seed     sampling ratio 110x, measured end to end 39.8x
        ///   radius  2,000 m   21.66 ms/seed     sampling ratio  27.6x
        ///   radius  5,000 m   79.35 ms/seed     sampling ratio   4.4x
        ///   radius 10,500 m  314.40 ms/seed     the whole world
        /// </code>
        ///
        /// <para>The 7.90 ms agrees with the 8.16 ms/seed the cost study measured for a 1 km goal.
        /// The end-to-end speed-up is smaller than the sampling ratio because roughly 5 ms of every
        /// seed is generator construction that no disc can remove - and for a query that needs
        /// heights, that fixed part is 570-617 ms of lake/river/stream pre-generation, so the
        /// restriction helps the sampling and not the floor. A caller that prints this number must
        /// therefore say "samples Nx fewer cells", not "runs Nx faster".</para>
        /// </summary>
        public static double RegionSpeedup(double radiusM)
        {
            double edge = SeedLab.Search.Metrics.SeedSampler.WaterEdge;
            if (!(radiusM > 0) || radiusM >= edge) return 1.0;
            double r = edge / radiusM;
            return r * r;
        }
    }
}
