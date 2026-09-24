using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace SeedLab.WorldGen.Diagnostics
{
    /// <summary>
    /// One thread's phase clock: the ticks, entries and allocated bytes of every <see cref="Phase"/>
    /// that thread has entered since the last <see cref="Reset"/>.
    ///
    /// <para><b>How the generator reaches it.</b> Through <see cref="BeginCurrent"/> and
    /// <see cref="EndCurrent"/>, which write into <see cref="Current"/>, a thread-static that is null
    /// unless a profiler set it on this thread. Nothing is passed down through a constructor or an
    /// interface, so switching profiling on changes no signature and no call path; a generator handle a
    /// worker built on another thread (every <c>Fork</c> handed to a <c>Parallel.For</c>) sees that
    /// thread's null and records nothing, which is right - its time is not this thread's time. When
    /// profiling is off, a phase boundary costs one thread-static read and one null test, and there are
    /// a few dozen boundaries per seed.</para>
    ///
    /// <para><b>Write-only from generator code.</b> The world-building code calls the two static
    /// methods <see cref="BeginCurrent"/> and <see cref="EndCurrent"/> and nothing else - not even
    /// <see cref="Current"/>'s getter, so it cannot branch on whether anything is recording. The numbers
    /// are private and leave only through <see cref="Snapshot"/>, which only a recorder calls (a
    /// profiler, a test); an IL tripwire in the test suite fails if any generator assembly references
    /// any other member. So no value the generator computes can depend on how long anything took, or on
    /// whether it was being timed - which is what makes "profiling on" and "profiling off" the same
    /// world, bit for bit, and the tests prove that on real seeds as well.</para>
    ///
    /// <para><b>Timestamps only at boundaries.</b> <see cref="Stopwatch.GetTimestamp"/> costs tens of
    /// nanoseconds, more than one base height; timing per point would measure the clock. Per-point work is
    /// counted instead, by <see cref="PhaseClock"/>, and only when counters are switched on.</para>
    ///
    /// <para>BCL only, no I/O: this assembly stays free of <c>System.IO</c>. One sink belongs to one
    /// thread and is not safe to share.</para>
    /// </summary>
    public sealed class PhaseSink
    {
        [ThreadStatic]
        private static PhaseSink? t_current;

        private const int C = PhaseMap.Capacity;

        private readonly long[] _ticks = new long[C];
        private readonly long[] _entries = new long[C];
        private readonly long[] _allocBytes = new long[C];
        private readonly long[] _openTicks = new long[C];
        private readonly long[] _openAlloc = new long[C];

        /// <summary>
        /// This thread's sink, or null (the default on every thread) when nothing is recording. A profiler
        /// sets it on its own worker threads before the first seed and clears it after the last. For
        /// recorders only: generator code reaches the sink through <see cref="BeginCurrent"/> and
        /// <see cref="EndCurrent"/>, never through this getter.
        /// </summary>
        public static PhaseSink? Current
        {
            get => t_current;
            set => t_current = value;
        }

        /// <summary>
        /// A phase boundary as generator code writes it: opens <paramref name="phase"/> in this thread's
        /// sink when one is set, and is one thread-static read and one null test when none is. Static,
        /// and returning nothing, so the code that calls it learns nothing - not even whether it was
        /// recorded.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void BeginCurrent(Phase phase) => t_current?.Begin(phase);

        /// <summary>The closing boundary: <see cref="End"/> on this thread's sink when one is set.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void EndCurrent(Phase phase) => t_current?.End(phase);

        /// <summary>
        /// Length of the span <see cref="Snapshot"/> fills: ticks, then entries, then allocated bytes, each
        /// <see cref="PhaseMap.Capacity"/> long and indexed by the phase's numeric id.
        /// </summary>
        public const int SnapshotLength = 3 * C;

        /// <summary>Offset of the entry counts inside a snapshot.</summary>
        public const int EntriesOffset = C;

        /// <summary>Offset of the allocated bytes inside a snapshot.</summary>
        public const int AllocOffset = 2 * C;

        /// <summary>
        /// Opens <paramref name="phase"/>. The allocation counter is read before the clock, and
        /// <see cref="End"/> reads them the other way round, so neither reading's own cost lands inside
        /// the phase it measures. A phase never runs inside itself, so one open slot per phase is enough.
        /// </summary>
        public void Begin(Phase phase)
        {
            int i = (int)phase;
            _openAlloc[i] = GC.GetAllocatedBytesForCurrentThread();
            _openTicks[i] = Stopwatch.GetTimestamp();
        }

        /// <summary>Closes <paramref name="phase"/>, adding its ticks, one entry and its allocation.</summary>
        public void End(Phase phase)
        {
            long now = Stopwatch.GetTimestamp();
            int i = (int)phase;
            _ticks[i] += now - _openTicks[i];
            _entries[i]++;
            _allocBytes[i] += GC.GetAllocatedBytesForCurrentThread() - _openAlloc[i];
        }

        /// <summary>Zeroes everything - for a recorder starting a new leg.</summary>
        public void Reset()
        {
            Array.Clear(_ticks);
            Array.Clear(_entries);
            Array.Clear(_allocBytes);
            Array.Clear(_openTicks);
            Array.Clear(_openAlloc);
        }

        /// <summary>
        /// Copies the running totals into <paramref name="into"/> (<see cref="SnapshotLength"/> longs):
        /// ticks at [0, C), entries at [C, 2C), allocated bytes at [2C, 3C). For recorders only - never
        /// called from generator code (the tripwire test enforces it). Ticks are
        /// <see cref="Stopwatch.Frequency"/> units.
        /// </summary>
        public void Snapshot(Span<long> into)
        {
            if (into.Length < SnapshotLength)
            {
                throw new ArgumentException("the snapshot needs " + SnapshotLength + " longs.", nameof(into));
            }

            _ticks.AsSpan().CopyTo(into);
            _entries.AsSpan().CopyTo(into.Slice(EntriesOffset));
            _allocBytes.AsSpan().CopyTo(into.Slice(AllocOffset));
        }
    }
}
