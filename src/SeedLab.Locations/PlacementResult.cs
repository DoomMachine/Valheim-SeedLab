using System;
using System.Collections.Generic;
using SeedLab.WorldGen;

namespace SeedLab.Locations
{
    /// <summary>Why a candidate zone or point was thrown away. One value per <c>continue</c> in the game.</summary>
    public enum RejectReason
    {
        None = 0,

        /// <summary>The candidate zone already holds an instance (<c>errorLocationInZone</c>). One location per zone, globally.</summary>
        ZoneOccupied,

        /// <summary>The zone is already generated. Impossible on a fresh world; no counter in the game either.</summary>
        ZoneAlreadyGenerated,

        /// <summary><c>errorBiomeArea</c> - <c>(m_biomeArea &amp; GetBiomeArea(zoneCentre)) == 0</c>.</summary>
        BiomeArea,

        /// <summary>Filter 1, <c>errorCenterDistance</c> - point.magnitude against m_min/maxDistance.</summary>
        CenterDistance,

        /// <summary>Filter 2, <c>errorBiome</c>.</summary>
        Biome,

        /// <summary>Filter 3, <c>errorAltitude</c> - <c>(height - 30)</c> against m_min/maxAltitude.</summary>
        Altitude,

        /// <summary>Filter 4, <c>errorForest</c>.</summary>
        Forest,

        /// <summary>Filter 5 - the second, redundant distance pair. <b>The game keeps no counter for it.</b></summary>
        DistanceFromCenter,

        /// <summary>Filter 6, <c>errorTerrainDelta</c>. The 10 insideUnitCircle draws are already spent.</summary>
        TerrainDelta,

        /// <summary>Filter 7, <c>errorSimilar</c> - too close to a same-prefab or same-group instance.</summary>
        Similar,

        /// <summary>Filter 8, <c>errorNotSimilar</c> - not close enough to a same-prefab or same-groupMax instance.</summary>
        NotSimilar,

        /// <summary>Filter 9, <c>errorVegetation</c> - the height mask's alpha.</summary>
        Vegetation,

        /// <summary>Filter 10a, <c>errorAltBiomeMissing</c> - the sector does not carry this entry's AltBiomeParent.</summary>
        AltBiomeMissing,

        /// <summary>Filter 10b, <c>errorAltBiomeBlock</c> - an alt-biome on the sector blocks this m_name.</summary>
        AltBiomeBlock,

        /// <summary>Filter 11, first phase: fewer than 10 surround scores collected yet, so this point is dropped whatever it scored.</summary>
        SurroundBaseline,

        /// <summary>Filter 11, second phase: score below <c>average + (max - average) * m_surroundBetterThanAverage</c>.</summary>
        SurroundBelowCutoff,

        /// <summary>Accepted - <c>RegisterLocation</c> was called.</summary>
        Accepted,
    }

    /// <summary>One registered <c>ZoneSystem.LocationInstance</c>, exactly as the game would save it.</summary>
    public sealed class LocationInstanceResult
    {
        public LocationInstanceResult(ZoneLocationEntry location, float x, float y, float z, Vec2s zone,
                                      int orderedIndex, int attempt)
        {
            Location = location;
            X = x; Y = y; Z = z;
            Zone = zone;
            OrderedIndex = orderedIndex;
            Attempt = attempt;
        }

        public ZoneLocationEntry Location { get; }

        /// <summary><c>ZoneLocation.Hash</c> - what the .db2 stores for this instance.</summary>
        public int PrefabHash => Location.NameHash;

        public string PrefabName => Location.PrefabName;

        public float X { get; }

        /// <summary>
        /// <c>WorldGenerator.GetHeight(x, z)</c>, set by filter 3 and never touched again -
        /// <c>PlaceLocations</c> modifies a local copy, so this IS the saved value.
        /// </summary>
        public float Y { get; }

        public float Z { get; }

        /// <summary><c>GetZone(position)</c> - recomputed from the point, not the candidate zone.</summary>
        public Vec2s Zone { get; }

        public int OrderedIndex { get; }

        /// <summary>Which outer attempt of this entry's stream produced it. Diagnostics only.</summary>
        public int Attempt { get; }

        /// <summary>
        /// Always false. <c>m_placed</c> only becomes true when a player generates the zone, which is
        /// not a function of the seed. A freshly generated world saves every instance with placed=false.
        /// </summary>
        public bool Placed => false;

        public override string ToString()
            => PrefabName + " (" + X.ToString("F1") + ", " + Y.ToString("F1") + ", " + Z.ToString("F1") + ") zone " + Zone;
    }

    /// <summary>Per location type: what its stream did.</summary>
    public sealed class LocationTypeResult
    {
        public ZoneLocationEntry Location = null!;
        public int OrderedIndex;

        /// <summary><c>worldSeed + m_prefab.Name.GetStableHashCode()</c>, unchecked.</summary>
        public int StreamSeed;

        /// <summary>True when <c>m_unique</c> and the type was already present - the whole type is skipped.</summary>
        public bool SkippedAsUnique;

        public int Quantity;

        /// <summary>The game's <c>placed</c> counter. See <see cref="RegisterCollisions"/>: it can exceed the instance count.</summary>
        public int Placed;

        /// <summary>Instances actually added to the world. <c>Placed - RegisterCollisions</c>.</summary>
        public int Registered;

        /// <summary>
        /// Times <c>RegisterLocation</c> hit "Location already exist in zone" and returned without
        /// adding, while the caller still incremented <c>placed</c>. Only reachable when the entry's
        /// <c>MaxRadius &gt; 32</c> lets a point escape its candidate zone.
        /// </summary>
        public int RegisterCollisions;

        /// <summary>Instances whose registered zone differs from the candidate zone the filters vetted.</summary>
        public int PointsOutsideCandidateZone;

        /// <summary>Outer attempts consumed (the game's <c>i</c>).</summary>
        public int Attempts;

        /// <summary>Point tries (the game's <c>iterations</c>).</summary>
        public int Iterations;

        public double Milliseconds;

        /// <summary>Counts per <see cref="RejectReason"/>, indexable by <c>(int)reason</c>.</summary>
        public readonly int[] Rejections = new int[Enum.GetValues(typeof(RejectReason)).Length];

        public bool Shortfall => !SkippedAsUnique && Placed < Quantity;

        public int this[RejectReason r] => Rejections[(int)r];

        public override string ToString()
            => Location.PrefabName + " " + Placed + "/" + Quantity + " in " + Attempts + " attempts";
    }

    /// <summary>
    /// A <c>m_unique</c> entry with more than one surviving candidate. <b>The engine must never present
    /// one of these as "the" position.</b> Every candidate is registered and saved; the first one whose
    /// zone a player (or a peer's ghost zone) generates wins, and <c>RemoveUnplacedLocations</c> deletes
    /// the rest. That is exploration order, not seed.
    /// </summary>
    public sealed class UniqueCandidateSet
    {
        public UniqueCandidateSet(ZoneLocationEntry location, IReadOnlyList<LocationInstanceResult> candidates)
        {
            Location = location;
            Candidates = candidates;
        }

        public ZoneLocationEntry Location { get; }
        public IReadOnlyList<LocationInstanceResult> Candidates { get; }

        public bool WinnerIsPredictable => Candidates.Count <= 1;
    }

    /// <summary>The result of one placement run.</summary>
    public sealed class PlacementResult
    {
        internal PlacementResult(int seed, int worldGenVersion, IReadOnlyList<LocationInstanceResult> instances,
                                 IReadOnlyDictionary<Vec2s, LocationInstanceResult> byZone,
                                 IReadOnlyList<LocationTypeResult> types,
                                 IReadOnlyList<UniqueCandidateSet> uniqueCandidates,
                                 int orderedCountRun, int orderedCountTotal, double milliseconds,
                                 IReadOnlyList<string> warnings)
        {
            Seed = seed;
            WorldGenVersion = worldGenVersion;
            Instances = instances;
            ByZone = byZone;
            Types = types;
            UniqueCandidates = uniqueCandidates;
            OrderedCountRun = orderedCountRun;
            OrderedCountTotal = orderedCountTotal;
            Milliseconds = milliseconds;
            Warnings = warnings;
        }

        public int Seed { get; }
        public int WorldGenVersion { get; }

        /// <summary>Every registered instance, in registration order.</summary>
        public IReadOnlyList<LocationInstanceResult> Instances { get; }

        /// <summary><c>ZoneSystem.m_locationInstances</c>.</summary>
        public IReadOnlyDictionary<Vec2s, LocationInstanceResult> ByZone { get; }

        public IReadOnlyList<LocationTypeResult> Types { get; }

        /// <summary>Unique entries that finished with more than one candidate. See <see cref="UniqueCandidateSet"/>.</summary>
        public IReadOnlyList<UniqueCandidateSet> UniqueCandidates { get; }

        /// <summary>How many ordered entries were actually run (a target prefix runs fewer).</summary>
        public int OrderedCountRun { get; }

        public int OrderedCountTotal { get; }

        /// <summary>True when the run stopped early, so later types are absent, not "not placed".</summary>
        public bool IsPartial => OrderedCountRun < OrderedCountTotal;

        /// <summary>
        /// The entry a <see cref="PlacementOptions.ContinueAfterType"/> callback stopped the run after,
        /// or null when the run went as far as it was asked to. Every type at or below
        /// <see cref="LastOrderedIndexRun"/> has its final instance set here; nothing above it ran.
        /// </summary>
        public ZoneLocationEntry? StoppedEarly { get; init; }

        /// <summary>The ordered index of the last entry whose loop finished, or -1 when none did.</summary>
        public int LastOrderedIndexRun { get; init; } = -1;

        public double Milliseconds { get; }

        public IReadOnlyList<string> Warnings { get; }

        /// <summary>
        /// The things this engine deliberately does NOT return, because they are not functions of the
        /// seed. Print it next to any result shown to a user.
        /// </summary>
        public static readonly string[] NotPredictable =
        {
            "Y-rotation of every instance: Quaternion.Euler(0, Random.Range(0,16)*22.5, 0) is drawn from "
            + "the AMBIENT UnityEngine.Random stream at zone-spawn time, which nothing seeds.",
            "Which candidate of a m_unique entry with quantity > 1 survives: the first zone generated "
            + "wins and RemoveUnplacedLocations deletes the others. That is exploration order.",
            "Dungeon interior layout for any location whose DungeonGenerator has a non-zero local XZ "
            + "offset: the unseeded rotation moves the generator, and its position feeds its seed.",
            "The ground Y a location finally sits at in-world: that comes from the built heightmap and "
            + "terrain edits, not from GetHeight. The value SAVED in the .db2 is the GetHeight one, "
            + "which is what this engine returns.",
            "Anything on a world that has already been played: generated zones are excluded, placed "
            + "instances are kept, and a console genloc re-run is not even deterministic in the game.",
            "What is inside a container at any of these positions: Container.Awake -> AddDefaultItems "
            + "-> DropTable.GetDropListItems draws on the AMBIENT UnityEngine.Random stream the first "
            + "time anyone loads that zone and writes the result to the ZDO, so two players on the same "
            + "seed open different chests. Where the building is IS a function of the seed; what it "
            + "holds is not.",
        };

        public IEnumerable<LocationInstanceResult> OfPrefab(string prefabName)
        {
            foreach (LocationInstanceResult i in Instances)
                if (string.Equals(i.PrefabName, prefabName, StringComparison.Ordinal)) yield return i;
        }
    }
}
