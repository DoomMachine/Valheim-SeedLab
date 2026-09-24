using System;
using System.Collections.Generic;

namespace SeedLab.Web.Search
{
    /// <summary>
    /// One goal, in the criteria language of <c>SeedLab.Search</c> and nothing else.
    ///
    /// <para><b>There is deliberately no second vocabulary here.</b> The panel used to speak its own
    /// dialect - <c>biome_nearest</c>, <c>land_share</c>, <c>spawn_island</c> - which the placeholder
    /// engine evaluated against the seed panel's figures. That was a second definition of "nearest"
    /// and "island" that only happened to agree with the engine's. These fields are the ones in
    /// <c>schema.md</c>, spelled the same way, and the server turns them straight into the query text
    /// that <c>vseed search</c> reads - which is what lets the page and the terminal be compared on
    /// the same query and give the same seeds.</para>
    /// </summary>
    public sealed class SearchGoal
    {
        /// <summary>Unique within the query. Filled in from the target and metric when left empty.</summary>
        public string Id { get; set; } = "";

        /// <summary><c>biome:Swamp</c> | <c>world:land_area</c> | <c>location:&lt;Prefab&gt;</c> | <c>group:&lt;name&gt;</c>.</summary>
        public string Target { get; set; } = "";

        /// <summary>The metric name, e.g. <c>nearest_distance</c>. See <c>MetricCatalog</c>.</summary>
        public string Metric { get; set; } = "";

        /// <summary>near | far | at_least | at_most | between</summary>
        public string Test { get; set; } = "at_least";

        public double Value { get; set; }

        /// <summary>The high end of a <c>between</c> range.</summary>
        public double Max { get; set; }

        /// <summary>Metres, for a <c>*_within</c> metric.</summary>
        public double Radius { get; set; }

        /// <summary>Metres, for <c>area_above_height</c>.</summary>
        public double Height { get; set; }

        /// <summary>m^2, for <c>island_count</c>. Default 10,000 (1 ha).</summary>
        public double MinArea { get; set; } = 10_000.0;

        /// <summary>center | spawn. <c>spawn</c> needs the dumped location table.</summary>
        public string From { get; set; } = "center";

        /// <summary>must | nice</summary>
        public string Importance { get; set; } = "must";

        public double Weight { get; set; } = 1.0;

        public double Pad { get; set; } = 0.25;

        /// <summary>
        /// Decision 9's "when matches are equally good" preference, and the one field on this object
        /// that is NOT a key of the query language.
        ///
        /// <para>The criteria language has no tie-break key: a goal is either a filter
        /// (<c>importance: must</c>) or a weighted sub-score (<c>importance: nice</c>). So a preference
        /// on a must-have is written into the query file as what it actually is - a SECOND goal over
        /// the same target and metric with <c>importance: nice</c>, whose test spelling carries the
        /// direction. <c>prefer closer</c> becomes <c>near</c>, <c>prefer farther</c> becomes
        /// <c>far</c>, <c>prefer smaller</c> <c>at_most</c>, <c>prefer larger</c> <c>at_least</c>;
        /// <c>recommended</c> picks whichever of those the metric asks for, by a fixed rule. The page says so on the goal, because the exported file then has one more goal in it
        /// than the editor shows rows - and a surprise in an exported file is the one thing parity
        /// cannot survive.</para>
        ///
        /// <para>Values: <c>none</c> (the default), <c>recommended</c>, <c>closer</c>, <c>farther</c>,
        /// <c>smaller</c>, <c>larger</c>. Ignored on a goal that is already a nice-to-have: it is its
        /// own preference.</para>
        /// </summary>
        public string Preference { get; set; } = "none";
    }

    /// <summary>A whole search: what to look for, where in the space, and for how long.</summary>
    public sealed class SearchQuery
    {
        public string? Name { get; set; }

        public List<SearchGoal> Goals { get; set; } = new List<SearchGoal>();

        /// <summary>Inclusive int32 range to scan. Defaults to the whole space.</summary>
        public long RangeStart { get; set; } = int.MinValue;

        public long RangeEnd { get; set; } = int.MaxValue;

        /// <summary>Stop after this many seeds. 0 means "until the range is exhausted".</summary>
        public long BudgetSeeds { get; set; } = 20_000;

        /// <summary>Stop after this many seconds. 0 means no wall-clock limit.</summary>
        public double BudgetSeconds { get; set; }

        /// <summary>How many of the best results the run holds in memory.</summary>
        public int Keep { get; set; } = 200;

        /// <summary>The definitional grid for every metric in this query, in metres.</summary>
        public double GridSpacingM { get; set; } = 96.0;

        /// <summary>0 means the server's default, which leaves a couple of cores for the map.</summary>
        public int Threads { get; set; }

        /// <summary>shuffled | sequential</summary>
        public string Order { get; set; } = "shuffled";

        /// <summary>Permutation key. Omit and it is derived from the query hash, which is still repeatable.</summary>
        public ulong? Key { get; set; }

        /// <summary>World.m_worldGenVersion: 0 | 1 | 2.</summary>
        public int GenVersion { get; set; } = 2;

        /// <summary>
        /// Seeds per work block, or null (the page's empty Block size box) to have it sized
        /// automatically. Checkpoints, streamed results and Stop land on block boundaries.
        ///
        /// <para><b>Automatic, like the terminal's.</b> It used to be a plain 64 the page always sent,
        /// which the terminal could not express as "automatic" and which left workers idle on a short
        /// run just as the terminal's fixed 256 did. Null goes through the same rule
        /// (<c>BlockSizing.Decide</c>): 256, or smaller so every worker gets at least four blocks.
        /// The user chose one ceiling, 256, for both front ends (2026-09-24), so a long location-tier
        /// run's block can be minutes of one worker's time; a number in the box pins it, is kept, and
        /// is warned about when it leaves workers idle.</para>
        /// </summary>
        public int? BlockSize { get; set; }

        /// <summary>
        /// Drop nice-to-have goals this build cannot measure instead of refusing the run. A
        /// <b>must</b> goal it cannot measure always refuses: see <c>EngineSearchEngine</c>.
        /// </summary>
        public bool SkipUnavailable { get; set; }

        // ---- decision 1: the bound ---------------------------------------------------------------

        /// <summary><c>--keep all</c>: stream every match instead of the best <see cref="Keep"/>.</summary>
        public bool KeepAll { get; set; }

        // ---- decision 6: the sampling ladder ------------------------------------------------------

        /// <summary>auto | off | on - <c>search.screen</c>. Auto is the default and is what auto-pick means.</summary>
        public string Screen { get; set; } = "auto";

        /// <summary>
        /// <c>auto | funnel | sample</c> - the same choice the terminal's <c>--strategy</c> makes, and
        /// decided by the same code, so the browser and the terminal cannot pick differently for the
        /// same query.
        ///
        /// <para><b>funnel</b> measures the cheap must-have goals over the whole range first and places
        /// locations only for the seeds that survived, reporting the survivor count and the measured
        /// cost of that second stage BEFORE it starts. <b>sample</b> evaluates the seeds straight
        /// through. <b>auto</b> picks per query and says why.</para>
        /// </summary>
        public string Strategy { get; set; } = "auto";

        /// <summary>The coarse screening grid in metres; 0 for the policy's own choice.</summary>
        public double ScreenGridM { get; set; }

        /// <summary>
        /// <c>search.region</c>: measure only inside this disc, metres. 0 means "whatever the goals
        /// need". The largest free lever in the tool and the one the CLI's plan block shouts about.
        /// </summary>
        public double RegionM { get; set; }

        // ---- decisions 2 and 5: what reaches the disk ----------------------------------------------

        /// <summary>
        /// The results file NAME - never a path.
        ///
        /// <para>A browser must not be able to name a file anywhere on the machine, so this is a bare
        /// name that the server resolves inside its own results directory (see
        /// <c>WebServerOptions.ResultsDirectory</c>) and refuses if it contains a separator, a drive,
        /// a <c>..</c> or an extension that is not <c>.jsonl</c>, <c>.csv</c> or <c>.json</c>. Empty
        /// means "keep nothing on disk", which is what the panel did before this existed.</para>
        /// </summary>
        public string? OutName { get; set; }

        /// <summary>Uncompressed bytes per segment for a <c>keep: all</c> run; 0 for one flat file.</summary>
        public long RotateBytes { get; set; }

        /// <summary>gzip each closed segment. On by default for rotated output.</summary>
        public bool Compress { get; set; } = true;

        /// <summary><c>none</c> or <c>top:N</c> - the per-segment reduce-and-delete step.</summary>
        public string Reduce { get; set; } = "none";

        /// <summary>stop (the default) | evict - what happens at the result ceiling.</summary>
        public string OnLimit { get; set; } = "stop";

        /// <summary>A hard ceiling on result bytes; 0 for the policy's own.</summary>
        public long MaxBytes { get; set; }

        // ---- decision 7: the resource modes --------------------------------------------------------

        /// <summary>background | balanced | full. Balanced everywhere unless the user says otherwise.</summary>
        public string Mode { get; set; } = "balanced";

        /// <summary><c>--ignore-running-game</c>: do not drop to background because Valheim is running.</summary>
        public bool IgnoreRunningGame { get; set; }

        // ---- decisions 9 and 10: the refusals and the confirmations --------------------------------

        /// <summary>
        /// The browser's <c>--yes</c>. The server runs the SAME preflight the CLI runs and will not
        /// start a run that carries confirmations until this says the user saw them - which is what
        /// stops a hand-written POST from skipping a dialog the page would have shown.
        /// </summary>
        public bool Confirmed { get; set; }

        /// <summary><c>--accept-scan-order</c>: accept an arbitrary first-N and waive the refusal rule.</summary>
        public bool AcceptScanOrder { get; set; }

        /// <summary>
        /// A shallow copy, so a server-side rewrite (dropping unmeasurable nice-to-haves) never
        /// mutates the object the request was deserialised into.
        /// </summary>
        public SearchQuery MemberwiseCloneShim() => (SearchQuery)MemberwiseClone();
    }

    /// <summary>One goal's measurement on one seed.</summary>
    public sealed class GoalResult
    {
        public string Id { get; set; } = "";

        public string Target { get; set; } = "";

        public string Metric { get; set; } = "";

        public string Test { get; set; } = "";

        public string Importance { get; set; } = "";

        public double Threshold { get; set; }

        /// <summary>The measured value in the metric's own unit; null when it could not be measured.</summary>
        public double? Value { get; set; }

        public string Unit { get; set; } = "";

        public bool Pass { get; set; }

        /// <summary>0..1 quality within the goal. Meaningful for a nice-to-have only.</summary>
        public double Score { get; set; }

        /// <summary>
        /// True when the region restriction stopped short of the true value, so the number is a proven
        /// bound rather than a measurement. The VERDICT is still exact - that is the only restriction
        /// the engine takes - and the page says "&gt; n" rather than printing the bound as a figure.
        /// </summary>
        public bool Bounded { get; set; }

        public double BoundRadius { get; set; }

        /// <summary>The tier that produced the value.</summary>
        public string Tier { get; set; } = "";

        public string? Unavailable { get; set; }
    }

    /// <summary>One passing seed, exactly as the engine emitted it.</summary>
    public sealed class SearchHit
    {
        public int Seed { get; set; }

        public string Text { get; set; } = "";

        public double Score { get; set; }

        public string Tier { get; set; } = "";

        /// <summary>The disc the side numbers below were measured inside, metres.</summary>
        public double RegionRadiusM { get; set; }

        public List<GoalResult> Goals { get; set; } = new List<GoalResult>();

        /// <summary>A small fixed set of world numbers, null where the run never needed to measure them.</summary>
        public double? LandKm2 { get; set; }

        public double? LargestIslandKm2 { get; set; }

        public int? IslandCount1Ha { get; set; }

        public double? OceanShare { get; set; }

        public double? PeakM { get; set; }
    }

    /// <summary>The live state of a run. Every field is measured; none is predicted except the ETA.</summary>
    public sealed class SearchProgress
    {
        public long Scanned { get; set; }

        public long Passed { get; set; }

        /// <summary>Seeds this run will visit: the budget, capped by the range.</summary>
        public long Limit { get; set; }

        public double ElapsedS { get; set; }

        public double SeedsPerSecond { get; set; }

        /// <summary>Seconds left at the measured rate. NaN before the first block lands.</summary>
        public double EtaSeconds { get; set; } = double.NaN;

        /// <summary>The share of all 4,294,967,296 worlds this run will have covered when it finishes.</summary>
        public double FractionOfSpace { get; set; }

        /// <summary>The same, for what has actually been evaluated so far.</summary>
        public double FractionCovered { get; set; }

        public long ProbeAccepts { get; set; }

        public long EarlyExits { get; set; }

        /// <summary>running | done | cancelled | failed</summary>
        public string Status { get; set; } = "running";

        public string? Message { get; set; }

        /// <summary>Records the sink is keeping. A bounded run never exceeds <c>keep</c>.</summary>
        public long Kept { get; set; }

        /// <summary>Bytes the results file holds right now, or -1 when this run keeps nothing.</summary>
        public long ResultBytes { get; set; } = -1;

        /// <summary>Free bytes on the results volume, or -1 when nothing is being written.</summary>
        public long FreeBytes { get; set; } = -1;
    }

    /// <summary>
    /// A query the pre-run checks will not start, and the whole report that says why.
    ///
    /// <para>Two kinds, in <see cref="Kind"/>: <c>refused</c> - it must not run, and the report names
    /// the fix - and <c>confirm</c> - it may run, but the user has to see the numbers first and send
    /// the query back with <c>confirmed: true</c>. The distinction is decision 10's, and it is the
    /// reason this is not simply an <see cref="ArgumentException"/>: the page has to be able to tell a
    /// dialog from a dead end.</para>
    /// </summary>
    public sealed class SearchRefusedException : Exception
    {
        public SearchRefusedException(PreflightReport report, string kind, string message) : base(message)
        {
            Report = report;
            Kind = kind;
        }

        public PreflightReport Report { get; }

        /// <summary>refused | confirm</summary>
        public string Kind { get; }
    }

    /// <summary>
    /// The answer to "Retry saving" (<c>POST /api/search/{id}/retry-save</c>): whether the last
    /// checkpoint of a run that stopped early is on disk now, and if not, why - in the same words the
    /// run's <c>done</c> event used.
    /// </summary>
    public sealed class SearchRetryResult
    {
        /// <summary>True when the checkpoint is on disk now - or when there was nothing left to save.</summary>
        public bool Saved { get; set; }

        /// <summary>The run has not ended yet; nothing was tried.</summary>
        public bool Running { get; set; }

        /// <summary>What happened, as a sentence for the page.</summary>
        public string Message { get; set; } = "";

        /// <summary>The checkpoint a resume would start from, or null when there is none on disk.</summary>
        public string? CheckpointPath { get; set; }

        public string? ResumeCommand { get; set; }

        /// <summary>The save that failed again, when it did (<c>message</c>, <c>path</c>, <c>onDiskBlock</c>, ...).</summary>
        public object? CheckpointError { get; set; }
    }

    /// <summary>One line on the wire. <c>Type</c> is the SSE event name.</summary>
    public sealed class SearchEvent
    {
        public SearchEvent(string type, object payload)
        {
            Type = type;
            Payload = payload;
        }

        public string Type { get; }

        public object Payload { get; }
    }

    /// <summary>What a UI needs to know before it offers the user a query builder.</summary>
    public sealed class SearchEngineInfo
    {
        public string Name { get; set; } = "";

        public string Description { get; set; } = "";

        /// <summary>
        /// False when this is a placeholder rather than the real tiered engine. The panel says so, in
        /// as many words, rather than presenting a stub's numbers as a search.
        /// </summary>
        public bool IsRealEngine { get; set; }

        /// <summary>Measured seeds per second on the last run, or null before anything has been measured.</summary>
        public double? MeasuredSeedsPerSecond { get; set; }

        /// <summary>The default thread count this server will use when a query asks for 0.</summary>
        public int DefaultThreads { get; set; }

        public int ProcessorCount { get; set; }

        /// <summary>Every metric the criteria language understands, with the tier that answers it.</summary>
        public List<MetricInfo> Metrics { get; set; } = new List<MetricInfo>();

        public List<string> Biomes { get; set; } = new List<string>();

        /// <summary>The location groups a goal may target, with the line that explains each.</summary>
        public List<GroupInfo> Groups { get; set; } = new List<GroupInfo>();

        public List<PresetInfo> Presets { get; set; } = new List<PresetInfo>();

        /// <summary>Why location goals cannot be measured, when they cannot. Null when they can.</summary>
        public string? LocationsUnavailable { get; set; }

        /// <summary>
        /// Where a results file the page names would be written. Null when this server was started
        /// without one, which the panel says rather than offering a control that cannot work.
        /// </summary>
        public string? ResultsDirectory { get; set; }

        /// <summary>
        /// The legal interval, the binding constraint and the evidence for every biome, prefab and
        /// group - so a goal's value control can refuse an impossible number before it is typed.
        /// </summary>
        public BoundsCatalog Bounds { get; set; } = new BoundsCatalog();
    }

    /// <summary>One entry of <c>MetricCatalog</c>, as the goal editor needs it.</summary>
    public sealed class MetricInfo
    {
        /// <summary>biome | world | location | group</summary>
        public string Kind { get; set; } = "";

        public string Name { get; set; } = "";

        public string Help { get; set; } = "";

        /// <summary>T0..T5 - the cheapest tier that answers this metric exactly.</summary>
        public string Tier { get; set; } = "";

        /// <summary>metres | square_metres | fraction | count</summary>
        public string Unit { get; set; } = "";

        /// <summary>The suffix to put after the input box - what the user actually types.</summary>
        public string UnitLabel { get; set; } = "";

        /// <summary>
        /// Multiply what the user typed by this to get the metric's own unit.
        ///
        /// <para>Nobody types 9000000 for nine square kilometres, and a share reads as a percentage
        /// everywhere else on the page, so the editor offers km² and % and converts. The conversion is
        /// declared here rather than guessed in the page, and the canonical query text the panel shows
        /// is always in the metric's own unit - the one a query file and <c>vseed search</c> use.</para>
        /// </summary>
        public double Scale { get; set; } = 1.0;

        public bool NeedsRadius { get; set; }

        public bool NeedsHeight { get; set; }

        public bool NeedsLocations { get; set; }

        /// <summary>The tests that read naturally for this metric, best first.</summary>
        public List<string> Tests { get; set; } = new List<string>();

        public double DefaultValue { get; set; }
    }

    /// <summary>One curated set of location prefabs a goal can target by name.</summary>
    public sealed class GroupInfo
    {
        public string Name { get; set; } = "";

        public string Help { get; set; } = "";

        public int Prefabs { get; set; }
    }

    /// <summary>A shipped query file, offered exactly as <c>vseed search &lt;preset&gt;</c> would run it.</summary>
    public sealed class PresetInfo
    {
        public string Name { get; set; } = "";

        public string Description { get; set; } = "";

        /// <summary>True when the preset cannot run in this build because it needs the location table.</summary>
        public bool NeedsLocations { get; set; }

        public List<SearchGoal> Goals { get; set; } = new List<SearchGoal>();

        public double GridSpacingM { get; set; }
    }
}
