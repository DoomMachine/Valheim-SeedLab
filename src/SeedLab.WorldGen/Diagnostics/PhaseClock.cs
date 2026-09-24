using System;
using System.Runtime.CompilerServices;

namespace SeedLab.WorldGen.Diagnostics
{
    /// <summary>
    /// A per-point event the profiler can count. Ids are stable in the same way as <see cref="Phase"/>:
    /// appended, never renumbered.
    /// </summary>
    public enum Counter : ushort
    {
        /// <summary><c>GetBaseHeight</c> on a non-menu world, every call.</summary>
        BaseHeight = 0,

        /// <summary>... of which the one-entry memo answered (the same point asked twice in a row).</summary>
        BaseHeightMemoHit = 1,

        /// <summary>... of which were computed by the 8-wide Perlin batch.</summary>
        BaseHeightSimd = 2,

        /// <summary><c>GetBiome</c> on a non-menu world.</summary>
        GetBiome = 3,

        /// <summary><c>WorldAngle</c> - each one is one <c>Math.Atan2</c> and one <c>Math.Sin</c>.</summary>
        WorldAngle = 4,

        /// <summary><c>CreateAshlandsGap</c> and <c>CreateDeepNorthGap</c> together; each calls <c>WorldAngle</c> once.</summary>
        GapCalls = 5,

        /// <summary><c>GetBiomeHeight</c> for a final height (both gap multipliers evaluated).</summary>
        HeightFinal = 6,

        /// <summary><c>GetBiomeHeight</c> during pre-generation (no gaps).</summary>
        HeightPregen = 7,

        /// <summary><c>GetRiverWeight</c>, every call.</summary>
        RiverWeight = 8,

        /// <summary>... answered from the single-entry river cache.</summary>
        RiverCacheHit = 9,

        /// <summary>... that found the cell in the river-point dictionary.</summary>
        RiverDictHit = 10,

        /// <summary>... whose cell holds no river point.</summary>
        RiverDictMiss = 11,

        /// <summary>River points scanned by <c>GetWeight</c>, summed over calls.</summary>
        RiverPointsScanned = 12,

        /// <summary>Tries of <c>FindStreamStartPoint</c> (two draws and one pre-generation height each).</summary>
        StreamStartTries = 13,

        /// <summary>Tries of <c>FindStreamEndPoint</c> (one draw, a sine, a cosine and one pre-generation height each).</summary>
        StreamEndTries = 14,

        /// <summary>Point steps of <c>RenderRivers</c> (one draw and three <c>Math.Sin</c> each).</summary>
        RenderSteps = 15,

        /// <summary>Cells tested by <c>AddRiverPoint</c>.</summary>
        RenderCellTests = 16,

        /// <summary>Base-height samples taken by <c>IsRiverAllowed</c>.</summary>
        RiverAllowedSamples = 17,

        /// <summary>Candidate lakes looked at by <c>FindRandomRiverEnd</c>.</summary>
        RiverEndScans = 18,
    }

    /// <summary>
    /// The per-point counters behind <c>SEEDLAB_PROFILE_COUNTERS</c>.
    ///
    /// <para><b>Free when off, and that is by construction, not by care.</b> <see cref="CountersOn"/> is a
    /// <c>static readonly bool</c> read once, in the type initialiser, from the environment. The JIT
    /// compiles an optimised method only after the type is initialised and then treats a static
    /// readonly field as the constant it is, so in the code that actually runs,
    /// <c>if (CountersOn)</c> with a false value is removed together with everything under it. A
    /// counter site therefore costs nothing in a normal run - which is why the variable cannot be
    /// switched on part-way: a process decides once. <c>vseed profile --counters</c> sets the variable
    /// as the first statement of <c>Main</c>, before any generator type is touched, and checks
    /// afterwards that it took effect.</para>
    ///
    /// <para><b>Write-only from generator code</b>, exactly like <see cref="PhaseSink"/>: sites call
    /// <see cref="Count"/> and <see cref="Add"/>; only a recorder calls <see cref="SnapshotCounters"/>
    /// or <see cref="ResetCounters"/>. Counts are per thread, so a worker's counts are its own
    /// seeds'.</para>
    /// </summary>
    public static class PhaseClock
    {
        /// <summary>The environment variable; "1" switches counting on for the whole process.</summary>
        public const string EnvironmentVariable = "SEEDLAB_PROFILE_COUNTERS";

        /// <summary>Length of the per-thread counter array, larger than the last <see cref="Counter"/> id.</summary>
        public const int Capacity = 32;

        /// <summary>True when this process counts per-point events. Read once; never changes.</summary>
        public static readonly bool CountersOn;

        [ThreadStatic]
        private static long[]? t_counts;

        // An explicit type initialiser, so the moment the variable is read is the first touch of this
        // type and nothing earlier: a caller that sets the variable first is guaranteed to be seen.
        static PhaseClock()
        {
            CountersOn = string.Equals(Environment.GetEnvironmentVariable(EnvironmentVariable), "1",
                                       StringComparison.Ordinal);
        }

        /// <summary>Counts one <paramref name="c"/> on this thread, when counters are on.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Count(Counter c)
        {
            if (CountersOn) Bump(c, 1);
        }

        /// <summary>Adds <paramref name="n"/> to <paramref name="c"/> on this thread, when counters are on.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Add(Counter c, long n)
        {
            if (CountersOn) Bump(c, n);
        }

        // Kept out of line so an inlined site is a test and a call, never the thread-static access.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Bump(Counter c, long n)
        {
            (t_counts ??= new long[Capacity])[(int)c] += n;
        }

        /// <summary>Copies this thread's counts into <paramref name="into"/> (<see cref="Capacity"/> longs). Recorders only.</summary>
        public static void SnapshotCounters(Span<long> into)
        {
            if (into.Length < Capacity) throw new ArgumentException("the snapshot needs " + Capacity + " longs.", nameof(into));
            if (t_counts == null) into.Slice(0, Capacity).Clear();
            else t_counts.AsSpan().CopyTo(into);
        }

        /// <summary>Zeroes this thread's counts. Recorders only.</summary>
        public static void ResetCounters()
        {
            if (t_counts != null) Array.Clear(t_counts);
        }

        /// <summary>Every defined counter, in id order.</summary>
        public static Counter[] All { get; } = (Counter[])Enum.GetValues(typeof(Counter));

        /// <summary>The snake_case name a report uses: "base_height_memo_hit".</summary>
        public static string Name(Counter c) => c switch
        {
            Counter.BaseHeight => "base_height",
            Counter.BaseHeightMemoHit => "base_height_memo_hit",
            Counter.BaseHeightSimd => "base_height_simd",
            Counter.GetBiome => "get_biome",
            Counter.WorldAngle => "world_angle",
            Counter.GapCalls => "gap_calls",
            Counter.HeightFinal => "height_final",
            Counter.HeightPregen => "height_pregen",
            Counter.RiverWeight => "river_weight",
            Counter.RiverCacheHit => "river_cache_hit",
            Counter.RiverDictHit => "river_dict_hit",
            Counter.RiverDictMiss => "river_dict_miss",
            Counter.RiverPointsScanned => "river_points_scanned",
            Counter.StreamStartTries => "stream_start_tries",
            Counter.StreamEndTries => "stream_end_tries",
            Counter.RenderSteps => "render_steps",
            Counter.RenderCellTests => "render_cell_tests",
            Counter.RiverAllowedSamples => "river_allowed_samples",
            Counter.RiverEndScans => "river_end_scans",
            _ => "counter" + ((int)c).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }
}
