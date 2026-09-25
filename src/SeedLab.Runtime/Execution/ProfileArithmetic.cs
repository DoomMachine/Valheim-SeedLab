using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace SeedLab.Runtime.Execution
{
    /// <summary>
    /// How many seeds a saturating profile measures, from a short pilot's rate.
    ///
    /// <para>A measurement "saturates" the machine when every worker is busy for most of it. The rate
    /// comes from the pilot's mean seed time, not from the pilot's seeds over its wall time: a pilot of
    /// a few seeds per worker is mostly its own ramp-up and tail, and its wall-clock rate would size
    /// the real run too small. The count is at least <see cref="MinPerWorker"/> per worker, so the
    /// ramp-up and the tail stay a small part of it, and a multiple of the worker count, so every
    /// worker can be given the same share.</para>
    /// </summary>
    public static class SaturationPlanner
    {
        /// <summary>The fewest seeds each worker measures in a saturated run.</summary>
        public const int MinPerWorker = 32;

        /// <summary>The most seeds one measurement runs (the profile's own cap on <c>--seeds</c>).</summary>
        public const int MaxSeeds = 1_000_000;

        /// <summary>The pilot's size: two seeds per worker, never fewer than four.</summary>
        public static int PilotSeeds(int workers) => Math.Max(4, 2 * Math.Max(1, workers));

        /// <summary>
        /// Seeds per second when <paramref name="workers"/> each take <paramref name="meanSeedMs"/> per
        /// seed; 0 when the mean is not a positive number.
        /// </summary>
        public static double RateFromMeanSeedMs(int workers, double meanSeedMs)
        {
            if (!(meanSeedMs > 0) || double.IsInfinity(meanSeedMs)) return 0;
            return Math.Max(1, workers) * 1000.0 / meanSeedMs;
        }

        /// <summary>
        /// max(workers x minPerWorker, ceil(rate x seconds)), rounded UP to a multiple of the worker
        /// count, and never above <paramref name="cap"/> (which is rounded DOWN to a multiple, but kept at
        /// one seed per worker or more). <paramref name="capped"/> says whether the cap decided it.
        /// </summary>
        public static int Seeds(double rate, double seconds, int workers, out bool capped,
                                int minPerWorker = MinPerWorker, int cap = MaxSeeds)
        {
            if (workers < 1) throw new ArgumentOutOfRangeException(nameof(workers));
            if (minPerWorker < 1) throw new ArgumentOutOfRangeException(nameof(minPerWorker));
            double byTime = rate > 0 && seconds > 0 && !double.IsInfinity(rate) ? Math.Ceiling(rate * seconds) : 0;
            double want = Math.Max((double)workers * minPerWorker, byTime);
            double rounded = Math.Ceiling(want / workers) * workers;
            long capRounded = Math.Max(workers, (long)cap / workers * workers);
            capped = rounded > capRounded;
            return (int)(capped ? capRounded : (long)rounded);
        }
    }

    /// <summary>
    /// The part of a measured section in which every worker was busy, and what the rest cost.
    ///
    /// <para>The window runs from the LATEST worker's first seed start to the EARLIEST worker's last seed
    /// finish: before it some workers were still starting, after it some had run out of work (the
    /// tail). Seeds are counted into it by the fraction of their own time that falls inside, so a seed
    /// straddling an edge counts in part. When a worker measured no seed there is no such window and the
    /// result says so rather than inventing one.</para>
    /// </summary>
    public sealed class SteadyStateResult
    {
        public bool Defined { get; init; }

        /// <summary>Why there is no window, when <see cref="Defined"/> is false.</summary>
        public string? Why { get; init; }

        public double SectionSeconds { get; init; }
        public double HeadSeconds { get; init; }
        public double WindowSeconds { get; init; }
        public double TailSeconds { get; init; }
        public double SeedsInWindow { get; init; }
        public double WindowSeedsPerSecond { get; init; }
        public double SectionSeedsPerSecond { get; init; }

        /// <summary>1 - whole-section rate / in-window rate: the throughput the ramp-up and the tail cost.</summary>
        public double TailLoss { get; init; }
    }

    public static class SteadyState
    {
        /// <summary>
        /// The steady-state window of a section. <paramref name="starts"/> and <paramref name="ends"/>
        /// are each seed's start and finish, <paramref name="workerOf"/> the worker that ran it, all in
        /// ticks of <paramref name="frequency"/> per second on one clock with <paramref name="sectionStart"/>
        /// and <paramref name="sectionEnd"/>.
        /// </summary>
        public static SteadyStateResult Compute(long sectionStart, long sectionEnd, long[] starts, long[] ends, int[] workerOf,
                                                int workers, long frequency)
        {
            if (starts.Length != ends.Length || starts.Length != workerOf.Length)
                throw new ArgumentException("starts, ends and workerOf must be the same length");
            if (frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
            double f = frequency;
            double sectionS = Math.Max(0, sectionEnd - sectionStart) / f;
            double sectionRate = sectionS > 0 ? starts.Length / sectionS : 0;
            if (workers < 1) return Undefined("no workers", sectionS, sectionRate);

            long[] first = new long[workers];
            long[] last = new long[workers];
            bool[] any = new bool[workers];
            for (int i = 0; i < starts.Length; i++)
            {
                int w = workerOf[i];
                if (w < 0 || w >= workers) throw new ArgumentOutOfRangeException(nameof(workerOf), "worker " + w + " of " + workers);
                if (!any[w] || starts[i] < first[w]) first[w] = starts[i];
                if (!any[w] || ends[i] > last[w]) last[w] = ends[i];
                any[w] = true;
            }

            int idle = 0;
            for (int w = 0; w < workers; w++)
            {
                if (!any[w]) idle++;
            }

            if (idle > 0)
            {
                return Undefined(idle + " of " + workers + " worker(s) measured no seed, so there was no moment when all of them were busy",
                                 sectionS, sectionRate);
            }

            long ws = long.MinValue, we = long.MaxValue;
            for (int w = 0; w < workers; w++)
            {
                ws = Math.Max(ws, first[w]);
                we = Math.Min(we, last[w]);
            }

            if (we <= ws)
            {
                return Undefined("one worker finished before the last one started, so there was no moment when all of them were busy",
                                 sectionS, sectionRate);
            }

            double inWindow = 0;
            for (int i = 0; i < starts.Length; i++)
            {
                long a = Math.Max(starts[i], ws), b = Math.Min(ends[i], we);
                if (b <= a) continue;
                long len = ends[i] - starts[i];
                inWindow += len > 0 ? (double)(b - a) / len : 1.0;
            }

            double windowS = (we - ws) / f;
            double windowRate = windowS > 0 ? inWindow / windowS : 0;
            return new SteadyStateResult
            {
                Defined = true,
                SectionSeconds = sectionS,
                HeadSeconds = Math.Max(0, ws - sectionStart) / f,
                WindowSeconds = windowS,
                TailSeconds = Math.Max(0, sectionEnd - we) / f,
                SeedsInWindow = inWindow,
                WindowSeedsPerSecond = windowRate,
                SectionSeedsPerSecond = sectionRate,
                TailLoss = windowRate > 0 ? 1.0 - sectionRate / windowRate : 0,
            };
        }

        private static SteadyStateResult Undefined(string why, double sectionS, double sectionRate)
            => new SteadyStateResult { Defined = false, Why = why, SectionSeconds = sectionS, SectionSeedsPerSecond = sectionRate };
    }

    /// <summary>
    /// Which indices of the profile's fixed seed order a section reads. Measured seeds are the
    /// <c>n</c> indices from <c>from</c>; each worker's warm-up seeds come right after them, worker by
    /// worker. Both depend only on (from, n, warm-up, worker), which is why a replayed plan that gives
    /// the same four numbers runs the same seeds and the same warm-up.
    /// </summary>
    public static class ProfileIndices
    {
        /// <summary>The number of indices in the seed order (the whole int32 range).</summary>
        public const long OrderLength = 4294967296L;

        public static long Measured(long from, long i) => (from + i) % OrderLength;

        public static long Warmup(long from, long n, int warmup, int worker, int k) => (from + n + (long)worker * warmup + k) % OrderLength;
    }

    /// <summary>A section's seed list as one SHA-256: every seed as a little-endian int32, in index order.</summary>
    public static class SeedListDigest
    {
        public static string Of(IReadOnlyList<int> seeds)
        {
            using IncrementalHash h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Span<byte> b = stackalloc byte[4];
            for (int i = 0; i < seeds.Count; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(b, seeds[i]);
                h.AppendData(b);
            }

            return Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
        }
    }
}
