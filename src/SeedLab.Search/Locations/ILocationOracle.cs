using System;
using System.Collections.Generic;
using SeedLab.WorldGen;

namespace SeedLab.Search.Locations
{
    /// <summary>One placed or candidate location instance, reduced to what a search goal asks about.</summary>
    public readonly struct LocationHit
    {
        public LocationHit(string prefab, float x, float z, bool candidate)
        {
            Prefab = prefab;
            X = x;
            Z = z;
            Candidate = candidate;
        }

        public string Prefab { get; }
        public float X { get; }
        public float Z { get; }

        /// <summary>
        /// True when this is one of an <c>m_unique</c> type's candidates rather than a settled position
        /// - 07-features.md section 0.6.2. Every candidate is written into the .db2; the first one whose
        /// zone a player generates wins and <c>RemoveUnplacedLocations</c> deletes the rest, which is
        /// exploration order, not seed. A report must carry the distinction rather than quietly counting
        /// candidates as if they were placements.
        /// </summary>
        public bool Candidate { get; }

        /// <summary>
        /// Distance from the world centre, in double. The game's own filter-1 test is
        /// <c>Vector3(x, 0, z).magnitude</c> in float32; the two differ by at most one float ULP
        /// (~1.2 mm at 10 km), which is why every static bound in
        /// <see cref="SeedLab.Search.Feasibility.LocationFeasibility"/> carries a metre of slack.
        /// </summary>
        public double Distance => Math.Sqrt((double)X * X + (double)Z * Z);

        public double DistanceFrom(double ox, double oz)
        {
            double dx = X - ox, dz = Z - oz;
            return Math.Sqrt(dx * dx + dz * dz);
        }
    }

    /// <summary>
    /// The seed-independent facts about one location prefab, straight off the dumped
    /// <c>ZoneSystem.ZoneLocation</c>. These are what T0 reasons about, before any seed is touched.
    /// </summary>
    public sealed class LocationTypeInfo
    {
        public LocationTypeInfo(string prefab, string name, bool placeable, int orderedIndex, int quantity,
                               bool unique, bool prioritized, Biome biome, float minDistance, float maxDistance,
                               float minDistanceFromCenter, float maxDistanceFromCenter, string? altBiomeParent,
                               int biomeArea = -1, LocationPresentation? presentation = null)
        {
            BiomeArea = biomeArea;
            Presentation = presentation;
            Prefab = prefab;
            Name = name;
            Placeable = placeable;
            OrderedIndex = orderedIndex;
            Quantity = quantity;
            Unique = unique;
            Prioritized = prioritized;
            Biome = biome;
            MinDistance = minDistance;
            MaxDistance = maxDistance;
            MinDistanceFromCenter = minDistanceFromCenter;
            MaxDistanceFromCenter = maxDistanceFromCenter;
            AltBiomeParent = altBiomeParent;
        }

        /// <summary>
        /// <c>m_biomeArea</c> from the dumped table, or -1 when the oracle did not supply it.
        ///
        /// <para><b>It is here so the tool's hardest verdict rests on hashed data.</b> "This type can
        /// never be placed in any seed" used to be read out of <c>constraint-atlas.json</c>, which is
        /// derived, is not in <c>manifest.json</c> and is hash-checked by nothing - so an edited atlas
        /// made the checker refuse a perfectly legal search while <c>vseed data --verify</c> still
        /// passed 8/8 (measured 2026-09-23). The dumped location table IS hash-checked, and
        /// <c>GetBiomeArea</c> returns only <c>Edge = 1</c> or <c>Median = 2</c>, so
        /// <c>m_biomeArea == 0</c> is the whole proof.</para>
        /// </summary>
        public int BiomeArea { get; }

        /// <summary><c>m_prefab.Name</c> - the RNG stream key and the name a query writes.</summary>
        public string Prefab { get; }

        /// <summary><c>m_name</c>, which is what an alt-biome's <c>m_blockLocationNames</c> matches.</summary>
        public string Name { get; }

        /// <summary>
        /// True when the entry is in the list the placement run walks (<c>m_enable</c> and
        /// <c>m_quantity != 0</c>). A prefab that is known but not placeable has exactly zero instances
        /// in every seed - which is a measurement, not a failure.
        /// </summary>
        public bool Placeable { get; }

        /// <summary>Position in the ordered placement list, or -1 when <see cref="Placeable"/> is false.</summary>
        public int OrderedIndex { get; }

        /// <summary>
        /// <c>m_quantity</c> - a hard cap. The placement loop is
        /// <c>while (i &lt; attempts &amp;&amp; placed &lt; m_quantity)</c>, and registrations only ever
        /// add, so a world can never hold more than this many instances of the type.
        /// </summary>
        public int Quantity { get; }

        /// <summary><c>m_unique</c>: the instances are candidates, one of which survives exploration.</summary>
        public bool Unique { get; }

        public bool Prioritized { get; }

        /// <summary><c>m_biome</c>, a bitmask. Filter 2 tests the RAW biome at the point against it.</summary>
        public Biome Biome { get; }

        /// <summary>Filter 1's <c>m_minDistance</c> / <c>m_maxDistance</c>; 0 means the test is off.</summary>
        public float MinDistance { get; }

        public float MaxDistance { get; }

        /// <summary>Filter 5's pair; a value of 0 or less means the test is off.</summary>
        public float MinDistanceFromCenter { get; }

        public float MaxDistanceFromCenter { get; }

        /// <summary>Non-null for an entry injected by an <c>AltBiome.m_addLocations</c>.</summary>
        public string? AltBiomeParent { get; }

        /// <summary>
        /// What to CALL this type and where to list it, or null when the oracle has no dumped name
        /// table. It is presentation hung on the seed-independent facts, not a fact of its own: the
        /// search engine never reads it, and an oracle that cannot name anything still answers every
        /// question this class exists to answer.
        /// </summary>
        public LocationPresentation? Presentation { get; }

        /// <summary>How much of the ordered list has to run before this type's instances are final.</summary>
        public int PrefixLength => OrderedIndex + 1;

        public override string ToString()
            => Prefab + " x" + Quantity + (Unique ? " (unique)" : "") + (Placeable ? " @" + OrderedIndex : " (not placed)");
    }

    /// <summary>
    /// What a typed location name turned out to mean. The seam type for complaint 2 - "the CLI must
    /// accept both the prefab name and the player-facing name" - alongside
    /// <see cref="LocationTypeInfo"/>: Search-side data, filled in from the dump by the oracle.
    /// </summary>
    public sealed class LocationNameMatch
    {
        public LocationNameMatch(string prefab, string asTyped, string? displayName, string how,
                                 string? note = null)
        {
            Prefab = prefab;
            AsTyped = asTyped;
            DisplayName = displayName;
            How = how;
            Note = note;
        }

        /// <summary>The prefab the typed string means - the identity every other API takes.</summary>
        public string Prefab { get; }

        /// <summary>Exactly what the user typed, kept so a report can echo it back unedited.</summary>
        public string AsTyped { get; }

        /// <summary>The player-facing name of that prefab, or null when the dump does not name it.</summary>
        public string? DisplayName { get; }

        /// <summary>Which pass matched, in words a user can read: "prefab name", "display name",
        /// "display name (article dropped)". It is shown, not logged - a user who typed "elder" and
        /// got GDKing is entitled to know why.</summary>
        public string How { get; }

        /// <summary>A caveat about THIS match, e.g. that the name was joined by hand rather than by
        /// the dump. Null when there is nothing to say.</summary>
        public string? Note { get; }

        public override string ToString()
            => AsTyped + " -> " + Prefab + " (" + How + ")";
    }

    /// <summary>
    /// One query's location work, computed once and shared by every worker thread.
    ///
    /// <para><b>The ordered-prefix property</b> (spec 02 section 7, <c>LocationTable.PrefixLengthForTarget</c>):
    /// each type draws from its own stream, seeded <c>worldSeed + m_prefab.Name.GetStableHashCode()</c>,
    /// so nothing AFTER a type can change it; but everything BEFORE it can, through zone occupancy
    /// (global, one location per zone), the AssetID bucket, the group / groupMax buckets and
    /// <c>CountNrOfLocation</c>. The correct prefix is therefore exactly
    /// <c>max(OrderedIndex) + 1</c> over the query's own prefabs, and nothing shorter is sound.</para>
    /// </summary>
    public sealed class LocationPlan
    {
        public LocationPlan(IReadOnlyList<string> prefabs, int prefixLength, int orderedCount, bool needSpawn,
                            IReadOnlyList<LocationTypeInfo> types)
        {
            Prefabs = prefabs;
            PrefixLength = prefixLength;
            OrderedCount = orderedCount;
            NeedSpawn = needSpawn;
            Types = types;
        }

        /// <summary>Every prefab the query asks about, de-duplicated, in ordered-index order.</summary>
        public IReadOnlyList<string> Prefabs { get; }

        /// <summary>Ordered entries this query must run. Never less than 1 when anything is asked for.</summary>
        public int PrefixLength { get; }

        /// <summary>The whole ordered list's length (183 for vanilla 1.0.15), for the cost report.</summary>
        public int OrderedCount { get; }

        /// <summary>True when some goal measures <c>from: spawn</c>, which needs <c>StartTemple</c>.</summary>
        public bool NeedSpawn { get; }

        public IReadOnlyList<LocationTypeInfo> Types { get; }

        /// <summary>The fraction of a full placement this plan runs. Not a time ratio - later entries cost more.</summary>
        public double PrefixFraction => OrderedCount > 0 ? (double)PrefixLength / OrderedCount : 1.0;
    }

    /// <summary>
    /// Asked after the ordered entry for <paramref name="prefab"/> has finished, with every hit the run
    /// has produced so far. Returning false aborts the run.
    ///
    /// <para><b>Why aborting here is EXACT and not a heuristic.</b> An instance is only ever appended,
    /// by the loop of its own entry, and no later entry touches an earlier entry's instances. So the
    /// moment an entry's loop returns, that type's instance set for this seed is FINAL. A must-goal all
    /// of whose prefabs have finished is therefore decided, and if it is false the seed fails whatever
    /// the remaining entries do. The caller is only allowed to return false in that case; see
    /// <c>SeedEvaluator.BuildLocationGate</c>.</para>
    /// </summary>
    public delegate bool LocationTypeGate(string prefab, IReadOnlyList<LocationHit> hitsSoFar);

    /// <summary>One seed's location answer.</summary>
    public sealed class LocationWorld
    {
        public LocationWorld(IReadOnlyList<LocationHit> hits, bool hasSpawn, float spawnX, float spawnZ,
                             bool aborted, string? abortedAfter, int lastOrderedIndexRun)
        {
            Hits = hits;
            HasSpawn = hasSpawn;
            SpawnX = spawnX;
            SpawnZ = spawnZ;
            Aborted = aborted;
            AbortedAfter = abortedAfter;
            LastOrderedIndexRun = lastOrderedIndexRun;
        }

        /// <summary>
        /// The ordered index of the last entry whose loop finished. Every type at or below it has its
        /// final instance set in <see cref="Hits"/>; nothing above it was run at all.
        /// </summary>
        public int LastOrderedIndexRun { get; }

        /// <summary>Every instance of the plan's prefabs, in registration order.</summary>
        public IReadOnlyList<LocationHit> Hits { get; }

        public bool HasSpawn { get; }
        public float SpawnX { get; }
        public float SpawnZ { get; }

        /// <summary>True when a gate stopped the run: types after <see cref="AbortedAfter"/> were not run.</summary>
        public bool Aborted { get; }

        public string? AbortedAfter { get; }
    }

    /// <summary>
    /// The one seam between the search engine and location placement.
    ///
    /// <para><c>SeedLab.Search</c> does not reference <c>SeedLab.Locations</c> or
    /// <c>SeedLab.Data</c>: location goals are expressible, validated and costed through this
    /// interface, and the implementation that actually places them lives in
    /// <c>SeedLab.LocationOracle</c>, which owns both of those dependencies. A build with no dumped
    /// asset table still compiles, still parses every boss query, and refuses it with a reason instead
    /// of returning seeds it never tested.</para>
    /// </summary>
    public interface ILocationOracle
    {
        /// <summary>False when there is no usable dumped table. Everything below may throw when false.</summary>
        bool Available { get; }

        /// <summary>Why it is not available, in one sentence the user can act on.</summary>
        string UnavailableReason { get; }

        /// <summary>Which game build the table came from, for the run manifest. Empty when unavailable.</summary>
        string Provenance { get; }

        /// <summary>True when this oracle knows the prefab name at all; used by the query validator.</summary>
        bool KnowsPrefab(string prefabName);

        /// <summary>The seed-independent facts about a prefab, or null when it is unknown.</summary>
        LocationTypeInfo? TypeOf(string prefabName);

        /// <summary>Every prefab name this oracle knows, for a "did you mean" message.</summary>
        IEnumerable<string> PrefabNames { get; }

        /// <summary>The prefab names a group name expands to, or null when the group is unknown.</summary>
        IReadOnlyList<string>? ExpandGroup(string groupName);

        /// <summary>
        /// The prefab a typed string means, whether it was spelled as a prefab (<c>GDKing</c>) or as
        /// the name a player knows (<c>The Elder</c>), or null.
        ///
        /// <para><b>Null is not "unknown name".</b> An oracle with no dumped table answers null to
        /// everything, because it genuinely cannot say; a caller must then leave the typed string
        /// alone so that the existing "needs the dumped location table" refusal is what the user
        /// sees, rather than a "no such place" that would be a different and false claim. The two are
        /// told apart by <see cref="Available"/>, never by the return value: when this oracle IS
        /// available, null means no known prefab, display name or alias matches.</para>
        /// </summary>
        LocationNameMatch? ResolveLocationName(string typed);

        /// <summary>
        /// Every prefab this oracle knows with the name a player uses for it, or null where the dump
        /// does not name it. For a "did you mean", for <c>--groups</c>-style vocabulary listings and
        /// for the web dropdown.
        /// </summary>
        IEnumerable<(string Prefab, string? Display)> LocationNames { get; }

        /// <summary>
        /// Plans one query's location work: the union of its prefabs and the shortest correct prefix.
        /// Called once per query, never per seed.
        /// </summary>
        LocationPlan Plan(IReadOnlyList<string> prefabs, bool needSpawn);

        /// <summary>
        /// Places one seed. Called at most once per seed, from many threads at once; an implementation
        /// must keep no per-seed state that outlives the call.
        /// </summary>
        LocationWorld Run(LocationPlan plan, int seed, int worldGenVersion, LocationTypeGate? gate);
    }

    /// <summary>
    /// The fallback when no dumped location table is usable: it answers "no" to everything and says
    /// exactly why.
    ///
    /// <para>It is deliberately not a stub that returns empty lists. An empty list would make
    /// "no Fuling village within 3 km" <i>pass</i> for every seed in the world, which is worse than
    /// failing - the user would get a results file full of seeds that were never tested.</para>
    /// </summary>
    public sealed class UnavailableLocationOracle : ILocationOracle
    {
        public static readonly UnavailableLocationOracle Instance = new UnavailableLocationOracle();

        private UnavailableLocationOracle() { }

        public bool Available => false;

        public string UnavailableReason =>
            "needs the dumped location table: the vanilla ZoneLocation parameters are asset data, so they "
            + "have to be captured from one game launch with tools\\SeedLab.Dumper before any boss, trader, "
            + "dungeon or village goal can be measured";

        public string Provenance => "";

        public bool KnowsPrefab(string prefabName) => false;

        public LocationTypeInfo? TypeOf(string prefabName) => null;

        public IEnumerable<string> PrefabNames => Array.Empty<string>();

        public IReadOnlyList<string>? ExpandGroup(string groupName) => LocationGroups.Expand(groupName);

        /// <summary>Null, and null here means "this oracle cannot say" - see the interface.</summary>
        public LocationNameMatch? ResolveLocationName(string typed) => null;

        /// <summary>Empty, for the same reason: with no dumped table there are no names to offer, and
        /// an empty "did you mean" is honest where a guessed one would not be.</summary>
        public IEnumerable<(string Prefab, string? Display)> LocationNames
            => Array.Empty<(string, string?)>();

        public LocationPlan Plan(IReadOnlyList<string> prefabs, bool needSpawn)
            => throw new InvalidOperationException(UnavailableReason);

        public LocationWorld Run(LocationPlan plan, int seed, int worldGenVersion, LocationTypeGate? gate)
            => throw new InvalidOperationException(UnavailableReason);

        /// <summary>
        /// Kept so that a caller with no oracle at all can still print the group vocabulary. The table
        /// itself lives in <see cref="LocationGroups"/>, which needs no game data to be read.
        /// </summary>
        public static IEnumerable<string> KnownGroupNames => LocationGroups.Names;
    }
}
