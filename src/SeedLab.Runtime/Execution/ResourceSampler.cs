using System;
using System.Diagnostics;
using System.Threading;

namespace SeedLab.Runtime.Execution
{
    /// <summary>
    /// What <see cref="ResourceSampler"/> reads each time. Every reader is optional and may return null;
    /// a figure that was never read is reported as absent, never as 0. The host supplies what needs a
    /// system call (this layer makes none): private bytes, and the sampler thread's own CPU time.
    /// </summary>
    public sealed class ResourceReaders
    {
        public Func<long?>? WorkingSet { get; init; }
        public Func<long?>? PrivateBytes { get; init; }
        public Func<long?>? GcHeap { get; init; }
        public Func<long?>? GcCommitted { get; init; }

        /// <summary>The CALLING thread's processor time; the sampler reads it on its own thread at its start and end.</summary>
        public Func<TimeSpan?>? ThreadCpu { get; init; }

        /// <summary>
        /// The BCL readers: the working set (<see cref="Environment.WorkingSet"/>), the GC's live-heap
        /// estimate (<see cref="GC.GetTotalMemory(bool)"/> without collecting) and the memory the GC has
        /// committed as of its last collection; plus whatever the host passes for the rest.
        /// </summary>
        public static ResourceReaders Bcl(Func<long?>? privateBytes = null, Func<TimeSpan?>? threadCpu = null) => new ResourceReaders
        {
            WorkingSet = () => Environment.WorkingSet,
            GcHeap = () => GC.GetTotalMemory(false),
            GcCommitted = () => GC.GetGCMemoryInfo().TotalCommittedBytes,
            PrivateBytes = privateBytes,
            ThreadCpu = threadCpu,
        };
    }

    /// <summary>The peak, mean, first and last of one sampled figure, over the samples that read it.</summary>
    public sealed class SeriesStat
    {
        public int Count { get; init; }
        public long Peak { get; init; }
        public double Mean { get; init; }
        public long First { get; init; }
        public long Last { get; init; }

        /// <summary>The statistics of the first <paramref name="count"/> values, skipping the negative ones (not read); null when none was read.</summary>
        public static SeriesStat? Of(long[] values, int count)
        {
            int n = 0;
            long peak = long.MinValue, first = 0, last = 0;
            double sum = 0;
            for (int i = 0; i < count && i < values.Length; i++)
            {
                long v = values[i];
                if (v < 0) continue;
                if (n == 0) first = v;
                last = v;
                peak = Math.Max(peak, v);
                sum += v;
                n++;
            }

            return n == 0 ? null : new SeriesStat { Count = n, Peak = peak, Mean = sum / n, First = first, Last = last };
        }
    }

    /// <summary>What a <see cref="ResourceSampler"/> saw between its start and its stop.</summary>
    public sealed class ResourceSummary
    {
        public TimeSpan Interval { get; init; }
        public int Samples { get; init; }
        public double Seconds { get; init; }
        public SeriesStat? WorkingSet { get; init; }
        public SeriesStat? PrivateBytes { get; init; }
        public SeriesStat? GcHeap { get; init; }
        public SeriesStat? GcCommitted { get; init; }

        /// <summary>The sampler thread's own processor time, when the host gave a thread-CPU reader.</summary>
        public TimeSpan? SamplerCpu { get; init; }
    }

    /// <summary>
    /// Samples this process's memory on a background thread, every 200 ms by default, off the workers:
    /// the working set, private bytes, the GC's heap and the GC's committed memory. It samples once as
    /// it starts and once as it stops, so even a window shorter than one interval has two samples.
    ///
    /// <para><b>Its own cost.</b> The samples go into arrays allocated up front (they grow, rarely, by
    /// doubling), so sampling adds no garbage of its own beyond what the readers allocate - the GC
    /// memory reader allocates one small object per call. Its thread's processor time is measured and
    /// reported, so it can be taken out of the process's "other" CPU.</para>
    /// </summary>
    public sealed class ResourceSampler : IDisposable
    {
        public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(200);

        private readonly ResourceReaders _readers;
        private readonly TimeSpan _interval;
        private readonly ManualResetEventSlim _stop = new ManualResetEventSlim(false);
        private readonly Thread _thread;
        private long[] _ws, _priv, _heap, _commit;
        private int _n;
        private long _t0, _t1;
        private TimeSpan? _cpu0, _cpu1;
        private ResourceSummary? _summary;

        private ResourceSampler(ResourceReaders readers, TimeSpan interval, int capacity)
        {
            _readers = readers;
            _interval = interval <= TimeSpan.Zero ? DefaultInterval : interval;
            int c = Math.Max(4, capacity);
            _ws = new long[c];
            _priv = new long[c];
            _heap = new long[c];
            _commit = new long[c];
            _thread = new Thread(Loop) { IsBackground = true, Name = "resource-sampler" };
        }

        /// <summary>Starts sampling now. <paramref name="capacity"/> is the number of samples room is made for up front.</summary>
        public static ResourceSampler Start(ResourceReaders readers, TimeSpan? interval = null, int capacity = 1024)
        {
            ResourceSampler s = new ResourceSampler(readers, interval ?? DefaultInterval, capacity);
            s._thread.Start();
            return s;
        }

        /// <summary>Takes a last sample, stops, and summarises. Calling it again returns the same summary.</summary>
        public ResourceSummary Stop()
        {
            if (_summary != null) return _summary;
            _stop.Set();
            _thread.Join();
            _summary = new ResourceSummary
            {
                Interval = _interval,
                Samples = _n,
                Seconds = Math.Max(0, _t1 - _t0) / (double)Stopwatch.Frequency,
                WorkingSet = SeriesStat.Of(_ws, _n),
                PrivateBytes = SeriesStat.Of(_priv, _n),
                GcHeap = SeriesStat.Of(_heap, _n),
                GcCommitted = SeriesStat.Of(_commit, _n),
                SamplerCpu = _cpu0.HasValue && _cpu1.HasValue ? _cpu1.Value - _cpu0.Value : null,
            };
            return _summary;
        }

        public void Dispose()
        {
            Stop();
            _stop.Dispose();
        }

        private void Loop()
        {
            _cpu0 = Safe(_readers.ThreadCpu);
            _t0 = Stopwatch.GetTimestamp();
            Sample();
            while (!_stop.Wait(_interval)) Sample();
            Sample();
            _t1 = Stopwatch.GetTimestamp();
            _cpu1 = Safe(_readers.ThreadCpu);
        }

        private void Sample()
        {
            if (_n == _ws.Length)
            {
                Array.Resize(ref _ws, _n * 2);
                Array.Resize(ref _priv, _n * 2);
                Array.Resize(ref _heap, _n * 2);
                Array.Resize(ref _commit, _n * 2);
            }

            _ws[_n] = Read(_readers.WorkingSet);
            _priv[_n] = Read(_readers.PrivateBytes);
            _heap[_n] = Read(_readers.GcHeap);
            _commit[_n] = Read(_readers.GcCommitted);
            _n++;
        }

        /// <summary>A reading, or -1 when there is no reader, it returned null, or it threw: a missed figure, never a 0.</summary>
        private static long Read(Func<long?>? r)
        {
            if (r == null) return -1;
            try
            {
                long? v = r();
                return v.HasValue && v.Value >= 0 ? v.Value : -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static TimeSpan? Safe(Func<TimeSpan?>? r)
        {
            if (r == null) return null;
            try
            {
                return r();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
