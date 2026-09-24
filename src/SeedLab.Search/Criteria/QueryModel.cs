using System;
using System.Collections.Generic;

namespace SeedLab.Search.Criteria
{
    /// <summary>What kind of thing a goal is about.</summary>
    public enum TargetKind
    {
        /// <summary>One of the nine <c>Heightmap.Biome</c> values.</summary>
        Biome,

        /// <summary>A shape-of-the-world number that is not tied to one biome (land, islands, peaks, rivers).</summary>
        World,

        /// <summary>One vanilla location prefab, by its prefab name (needs the dumped location table).</summary>
        Location,

        /// <summary>A named set of location prefabs - "traders", "burial_chambers" (needs the dumped table).</summary>
        Group,
    }

    /// <summary>
    /// The comparison a goal makes. <c>near</c>/<c>far</c> are the distance-flavoured spellings of
    /// <c>at_most</c>/<c>at_least</c>; they differ only in how the nice-to-have sub-score is shaped
    /// (07-features.md section 3.6), never in whether the goal passes.
    /// </summary>
    public enum GoalTest
    {
        /// <summary>measured &lt;= value. Sub-score <c>clamp01(1 - d/value)</c>.</summary>
        Near,

        /// <summary>measured &gt;= value. Sub-score <c>clamp01(d/value)</c>.</summary>
        Far,

        /// <summary>measured &gt;= value. Sub-score <c>min(1, v/value)</c>.</summary>
        AtLeast,

        /// <summary>measured &lt;= value. Sub-score <c>clamp01(1 - v/value)</c>.</summary>
        AtMost,

        /// <summary>min &lt;= measured &lt;= max. 1 inside, falling linearly to 0 over a pad.</summary>
        Between,
    }

    public enum Importance
    {
        /// <summary>A hard filter. A seed that fails it is rejected.</summary>
        Must,

        /// <summary>Contributes a weighted sub-score in [0,1]; never rejects.</summary>
        Nice,
    }

    /// <summary>Where a distance is measured from.</summary>
    public enum DistanceOrigin
    {
        /// <summary>The world centre (0,0) - what both reference sites use.</summary>
        Center,

        /// <summary>
        /// The <c>StartTemple</c> point the player actually spawns on. Needs the dumped location table,
        /// so a goal using it fails closed until the dump exists.
        /// </summary>
        Spawn,
    }

    /// <summary>The thing a goal measures.</summary>
    public sealed class GoalTarget
    {
        public GoalTarget(TargetKind kind, string name)
        {
            Kind = kind;
            Name = name;
        }

        public TargetKind Kind { get; }

        /// <summary>The biome name, the metric name (for <see cref="TargetKind.World"/>), or the prefab/group.</summary>
        public string Name { get; }

        public override string ToString() => Kind.ToString().ToLowerInvariant() + ":" + Name;
    }

    /// <summary>One declarative goal. This is the unit the user writes and the unit the report explains.</summary>
    public sealed class Goal
    {
        public string Id = "";
        public GoalTarget Target = new GoalTarget(TargetKind.World, "land_area");

        /// <summary>The metric name, e.g. <c>nearest_distance</c>. See <see cref="MetricCatalog"/>.</summary>
        public string Metric = "";

        public GoalTest Test = GoalTest.AtLeast;

        /// <summary>The threshold. For <see cref="GoalTest.Between"/> this is the low end.</summary>
        public double Value;

        /// <summary>The high end of a <see cref="GoalTest.Between"/> range.</summary>
        public double Max;

        /// <summary>The radius a <c>*_within</c> metric counts inside, metres.</summary>
        public double Radius;

        /// <summary>The height an <c>area_above_height</c> metric counts above, metres.</summary>
        public double Height;

        /// <summary>The minimum component area an island count uses, m^2. Default 10,000 (1 ha).</summary>
        public double MinArea = 10_000.0;

        public DistanceOrigin From = DistanceOrigin.Center;
        public Importance Importance = Importance.Must;
        public double Weight = 1.0;

        /// <summary>
        /// The pad a <see cref="GoalTest.Between"/> sub-score falls to zero over, as a fraction of the
        /// span. 07-features.md section 3.6 defaults it to 25 %.
        /// </summary>
        public double Pad = 0.25;

        public override string ToString() => Id + " (" + Target + "." + Metric + ")";
    }

    public enum ScanOrder
    {
        /// <summary>index i -> seed i + int.MinValue.</summary>
        Sequential,

        /// <summary>index i -> Feistel_k(i) + int.MinValue. A bijection: no repeats, no gaps.</summary>
        Shuffled,
    }

    public sealed class WorldSpec
    {
        /// <summary>World.m_worldGenVersion: 0 | 1 | 2. Every world made by 1.0.15 is 2.</summary>
        public int GenVersion = 2;
    }

    public sealed class SearchSpec
    {
        public ScanOrder Order = ScanOrder.Shuffled;

        /// <summary>The Feistel key. Null means "derive it from the query hash", which is still deterministic.</summary>
        public ulong? Key;

        /// <summary>Inclusive int32 seed range for <see cref="ScanOrder.Sequential"/>; the index range otherwise.</summary>
        public long From = int.MinValue;

        public long To = int.MaxValue;

        /// <summary>Stop after this many seeds. 0 or negative means "the whole range".</summary>
        public long Seeds;

        /// <summary>
        /// Stop after this long. Zero means no wall-clock limit. <c>budget.wall</c> in a query file,
        /// the CLI's <c>--budget</c> (since 2026-09-24, so the preflight's budget line sees it) and the
        /// web page's time budget all land here. Not part of the canonical JSON: a budget changes when
        /// a run stops, never which seeds it visits in what order.
        /// </summary>
        public TimeSpan Wall = TimeSpan.Zero;

        /// <summary>
        /// <b>A real cap on the results file</b>, not only on an in-memory table.
        ///
        /// <para>This changed in this build, and it is the headline fix of the disk audit. It used to
        /// size a display list while every passing seed was streamed to disk, so
        /// <c>vseed search custom --all --out results.json</c> would have written a measured
        /// <b>7.36 TB</b> - <c>custom</c> has no must-have goal, so it matches 100 % of seeds. Now the
        /// file holds the best <see cref="Keep"/> records and nothing else, the disk cost is
        /// <c>keep x record size</c> whatever the scan finds, and the run reports the TRUE match count
        /// beside it ("top 1000 of 131,076").</para>
        ///
        /// <para>Set <see cref="KeepAll"/> to stream every match instead; that mode wants rotation
        /// (<c>output.rotate</c>).</para>
        /// </summary>
        public int Keep = 1000;

        /// <summary>
        /// <c>keep: all</c> - stream every match rather than the best <see cref="Keep"/>. Unbounded
        /// output: pair it with <c>output.rotate</c> unless the match count is known to be small.
        /// </summary>
        public bool KeepAll;

        /// <summary>
        /// Measure only inside this disc, metres; 0 means "whatever the goals themselves need".
        ///
        /// <para><b>Exact and free.</b> A metric bounded by a disc cannot be changed by a cell outside
        /// it, so restricting the region is not an approximation - and it is the largest lever in the
        /// tool. Measured on this machine: a 1 km goal costs <b>8.16 ms/seed</b> at the game's own
        /// 12 m grid against <b>1,324 ms</b> for the same goal over the whole world - <b>162x</b>,
        /// with no loss of any kind. It is refused rather than silently applied when a goal in the
        /// query is defined over the whole world (a whole-world area, an island count), because there
        /// the disc would change the goal's meaning instead of only its cost.</para>
        /// </summary>
        public double Region;

        /// <summary>
        /// The coarse grid a screen-then-verify run screens on, metres; 0 for the automatic choice.
        /// The verification grid is always <see cref="Grid"/> - every record written is measured
        /// there, so the screen changes the cost and never the answer's definition.
        /// </summary>
        public double ScreenGrid;

        /// <summary>
        /// Screen-then-verify: off, on, or automatic (the default - <see cref="Evaluation.GridPolicy"/>
        /// decides from the goals present).
        /// </summary>
        public ScreenMode Screen = ScreenMode.Auto;

        /// <summary>The definitional sampling grid, metres. 12 is the game's own.</summary>
        public double Grid = 12.0;

        /// <summary>Enable HEURISTIC prefilters. Taints every output record with <c>approx: true</c>.</summary>
        public bool Approx;

        /// <summary>Worker threads. 0 means every logical core.</summary>
        public int Threads;

        /// <summary>
        /// The largest block the automatic rule picks, and the size a long run gets: 256 seeds.
        ///
        /// <para>Measured cost of smaller blocks: 32 against 256 cost 2.62 % on the fastest tier, and
        /// 125 against 256 nothing measurable (docs\measurements.md). A completed run's results file is
        /// byte-identical at every block size (measured 2026-09-24 in jsonl, json and csv, keep N and
        /// keep all, evict limits and screen-then-verify), so the size is a cost and resume-granularity
        /// knob, never part of the answer. One ceiling for the CLI and the web page, by the user's
        /// decision of 2026-09-24.</para>
        /// </summary>
        public const int DefaultBlockSize = 256;

        /// <summary>
        /// Seeds per work block, as the user GAVE it (<c>--block-size</c>, <c>search.block_size</c> or
        /// the web's Block size box), or null for "size it automatically"
        /// (<see cref="Execution.BlockSizing.Decide"/>).
        ///
        /// <para><b>Why nullable.</b> One worker computes a whole block, so a run with fewer blocks than
        /// workers leaves the rest idle: <c>--seeds 512</c> at the old fixed 256 was two blocks, and six
        /// of eight workers did nothing. The automatic rule shrinks the block for such a run, but it
        /// must not overrule a size the user asked for - it warns instead - and a plain int with the
        /// default baked in could not tell the two apart (the shipped custom.json and the web page
        /// both wrote 256 or 64 explicitly).</para>
        ///
        /// <para><b>Never write a decided size back here.</b> Funnel stage one shares this object by
        /// reference (<c>FunnelPlan.StageOne</c>) and the grid raise re-enters
        /// <c>SearchSession.Create</c> with the same query, so a write-back would turn "automatic" into
        /// "explicit" for both. The decided size lives on the plan and on
        /// <c>SearchSession.BlockDecision</c>. Not part of the canonical JSON, so neither the hash,
        /// the key nor the checkpoint path depends on it.</para>
        /// </summary>
        public int? BlockSize;
    }

    /// <summary>How a run chooses its sampling strategy. Auto-pick is the default (decision 6).</summary>
    public enum ScreenMode
    {
        /// <summary>Choose from the goals present, and say what was chosen and why.</summary>
        Auto,

        /// <summary>Never screen: measure once, at <c>search.grid</c>.</summary>
        Off,

        /// <summary>Always screen, at <c>search.screen_grid</c> or the policy's default.</summary>
        On,
    }

    public sealed class OutputSpec
    {
        /// <summary>Results file. The extension picks the format: .csv, .jsonl, .json.</summary>
        public string? Path;

        public bool Explain = true;

        /// <summary>
        /// Uncompressed bytes per segment for an unbounded run; 0 means "one flat file". The decided
        /// size is 1 GB (<c>"1GB"</c> in the query file).
        /// </summary>
        public long RotateBytes;

        /// <summary>gzip each closed segment. On by default for rotated output: JSONL compresses 8-15x.</summary>
        public bool Compress = true;

        /// <summary>
        /// The per-segment reduction, e.g. <c>"top:1000"</c>. Only reductions that merge exactly
        /// across segments are accepted, because the raw segment is deleted afterwards.
        /// </summary>
        public string Reduce = "none";

        /// <summary>At the result ceiling: stop (the default) or evict the worst records.</summary>
        public string OnLimit = "stop";

        /// <summary>A hard ceiling on result bytes; 0 for none.</summary>
        public long MaxBytes;
    }

    /// <summary>
    /// One search, entirely described by one file. The schema is in
    /// <c>src\SeedLab.Search\Criteria\schema.md</c> and is printed by <c>vseed search --schema</c>.
    /// </summary>
    public sealed class Query
    {
        /// <summary>Query-file format version.</summary>
        public int Version = 1;

        /// <summary>
        /// Metric-definition version (07-features.md section 2). A results file from a different
        /// <c>defs</c> is not comparable with this one, so it travels with every record.
        /// </summary>
        public int Defs = 1;

        public string? Name;
        public string? Description;

        public WorldSpec World = new WorldSpec();
        public SearchSpec Search = new SearchSpec();
        public OutputSpec Output = new OutputSpec();
        public List<Goal> Goals = new List<Goal>();

        /// <summary>The canonical JSON this query was read from (or serialises to); hashed into the manifest.</summary>
        public string CanonicalJson = "";
    }
}
