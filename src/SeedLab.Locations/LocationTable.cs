using System;
using System.Collections.Generic;
using SeedLab.Contracts.Dump;
using SeedLab.Seeds;
using SeedLab.WorldGen;

namespace SeedLab.Locations
{
    /// <summary>
    /// <c>SoftReferenceableAssets.AssetID</c> - four uints in declaration order v3, v2, v1, v0. It is
    /// the identity the game uses for "the same location prefab" in <c>m_locationIDCache</c>, and
    /// therefore in the min/max distance-from-similar filters.
    ///
    /// <para>When a table carries no AssetID (a synthetic table, or a dump written before the field
    /// existed) use <see cref="FromPrefabNameFallback"/>. That is EXACT as long as no two entries share
    /// a prefab name; if two do, the game would put them in one bucket and the fallback keeps them in
    /// one bucket too, because it hashes the name. The one case it cannot reproduce is two entries with
    /// DIFFERENT names pointing at the SAME prefab asset - <see cref="LocationTable.AssetIdsAreReal"/>
    /// tells a caller whether that risk is live.</para>
    /// </summary>
    public readonly struct AssetId : IEquatable<AssetId>
    {
        public readonly uint V3, V2, V1, V0;

        public AssetId(uint v3, uint v2, uint v1, uint v0) { V3 = v3; V2 = v2; V1 = v1; V0 = v0; }

        public bool IsValid => (V3 | V2 | V1 | V0) != 0u;

        /// <summary>
        /// A stand-in AssetID derived from the prefab name. Distinct names give distinct ids (the two
        /// djb2-xor lanes are kept separately, so this is a 64-bit spread, not a 32-bit hash), equal
        /// names give equal ids.
        /// </summary>
        public static AssetId FromPrefabNameFallback(string prefabName)
        {
            (uint even, uint odd) = StableHash.ComputeLanes(prefabName.AsSpan());
            return new AssetId(0x5EED1AB0u, 0u, odd, even);   // "SEEDLAB" marker, so a fallback id is obvious
        }

        public bool Equals(AssetId other) => V3 == other.V3 && V2 == other.V2 && V1 == other.V1 && V0 == other.V0;
        public override bool Equals(object? obj) => obj is AssetId a && Equals(a);
        public override int GetHashCode() => unchecked((int)(V3 * 2654435761u ^ V2 * 2246822519u ^ V1 * 3266489917u ^ V0 * 668265263u));
        public override string ToString() => V3.ToString("x8") + V2.ToString("x8") + V1.ToString("x8") + V0.ToString("x8");
    }

    /// <summary>
    /// One <c>ZoneSystem.ZoneLocation</c> as the placement engine needs it. Every field here is read by
    /// <c>GenerateLocationsTimeSliced</c>; none of it is hard-coded anywhere in this assembly - the
    /// engine is pure machinery and the table is data.
    /// </summary>
    public sealed class ZoneLocationEntry
    {
        /// <summary>Index in <c>ZoneSystem.m_locations</c>. Provenance only.</summary>
        public int Index;

        /// <summary>
        /// <c>ZoneLocation.m_name</c>. Not a label: the alt-biome block filter matches
        /// <c>AltBiome.m_blockLocationNames.Contains(location.m_name)</c> against THIS string, not
        /// against the prefab name.
        /// </summary>
        public string Name = "";

        public bool Enable = true;

        /// <summary>
        /// <c>m_prefab.Name</c> - the RNG stream key (<c>seed + Name.GetStableHashCode()</c>), the
        /// identity <c>CountNrOfLocation</c> compares, and the string whose hash the .db2 stores.
        /// </summary>
        public string PrefabName = "";

        /// <summary><c>m_prefab.Name.GetStableHashCode()</c>.</summary>
        public int NameHash;

        public AssetId AssetId;

        public Biome Biome;
        public BiomeArea BiomeArea = BiomeArea.Everything;
        public int Quantity;
        public bool Prioritized;
        public bool CenterFirst;
        public bool Unique;

        public string Group = "";
        public float MinDistanceFromSimilar;
        public string GroupMax = "";
        public float MaxDistanceFromSimilar;

        public float InteriorRadius;
        public float ExteriorRadius;

        public float MinTerrainDelta;
        public float MaxTerrainDelta = 2f;

        public float MinimumVegetation;
        public float MaximumVegetation = 1f;

        public bool SurroundCheckVegetation;
        public float SurroundCheckDistance = 20f;
        public int SurroundCheckLayers = 2;
        public float SurroundBetterThanAverage;

        public bool InForest;
        public float ForestTresholdMin;
        public float ForestTresholdMax = 1f;

        public float MinDistanceFromCenter;
        public float MaxDistanceFromCenter;
        public float MinDistance;
        public float MaxDistance;
        public float MinAltitude = -1000f;
        public float MaxAltitude = 1000f;

        /// <summary>Non-null only for entries injected by an <c>AltBiome.m_addLocations</c>.</summary>
        public string? AltBiomeParent;

        // ---- fields the engine does not read, kept so a caller can report them ------------------

        public bool IconAlways;
        public bool IconPlaced;
        public bool RandomRotation = true;
        public bool SlopeRotation;
        public bool SnapToWater;
        public bool ClearArea;

        /// <summary><c>Mathf.Max(m_exteriorRadius, m_interiorRadius)</c> - the inset of the point draw.</summary>
        public float MaxRadius => ZoneMath.MathfMax(ExteriorRadius, InteriorRadius);

        /// <summary>60 000 when prioritized, 12 000 otherwise.</summary>
        public int Attempts => Prioritized ? 60000 : 12000;

        /// <summary>
        /// True when the point draw can land outside its own candidate zone, which makes
        /// <c>RegisterLocation</c> use a zone the biome-area filter never checked - and can silently
        /// drop the instance while <c>placed</c> still counts it.
        ///
        /// <para>The threshold is <b>64</b>, not the 32 spec 02 section 6.1 states: with
        /// <c>r &gt; 32</c> the <c>Range(-32+r, 32-r)</c> bounds invert and the offset spans
        /// <c>+/-(r-32)</c>, which only exceeds the 32 m half-zone once <c>r &gt; 64</c>. See
        /// <see cref="ZoneMath.GetRandomPointInZone"/>.</para>
        /// </summary>
        public bool CanEscapeZone => MaxRadius > ZoneMath.ZoneSize;

        /// <summary>
        /// True when <c>MaxRadius &gt; 32</c> inverts the point-draw bounds. Harmless on its own - the
        /// point stays in the zone, in a band of half-width <c>MaxRadius - 32</c> - but it means the
        /// entry samples a narrower strip the bigger its radius gets, which is the opposite of the
        /// intent and is worth reporting.
        /// </summary>
        public bool PointDrawBoundsInverted => MaxRadius > ZoneMath.ZoneHalf;

        public override string ToString() => PrefabName + " x" + Quantity + (Prioritized ? " (prioritized)" : "");
    }

    /// <summary>
    /// <c>ZoneSystem.m_locations</c> plus the order the placement run walks
    /// (<c>GenerateLocationsTimeSliced</c>).
    ///
    /// <para><b>The table is never built in code.</b> <c>SetupLocations</c> concatenates serialized
    /// Unity asset data, sorts it with an UNSTABLE <c>List.Sort</c> and appends every
    /// <c>AltBiome.m_addLocations</c> whether the alt-biome is enabled or not. Spec 02 section 3 is
    /// explicit that a port must dump the final list rather than reimplement that - so this type only
    /// consumes one.</para>
    /// </summary>
    public sealed class LocationTable
    {
        private readonly Dictionary<string, int> m_orderedIndexByPrefab;

        private LocationTable(IReadOnlyList<ZoneLocationEntry> all, IReadOnlyList<ZoneLocationEntry> ordered,
                              bool assetIdsAreReal, IReadOnlyList<string> notes)
        {
            All = all;
            Ordered = ordered;
            AssetIdsAreReal = assetIdsAreReal;
            Notes = notes;
            m_orderedIndexByPrefab = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < ordered.Count; i++)
                if (!m_orderedIndexByPrefab.ContainsKey(ordered[i].PrefabName))
                    m_orderedIndexByPrefab[ordered[i].PrefabName] = i;
        }

        /// <summary><c>ZoneSystem.m_locations</c> order.</summary>
        public IReadOnlyList<ZoneLocationEntry> All { get; }

        /// <summary>
        /// The list the run walks: <c>m_locations.OrderByDescending(a =&gt; a.m_prioritized)</c> (a
        /// STABLE LINQ ordering) with the <c>!m_enable || m_quantity == 0</c> entries removed afterwards
        /// by a backwards pass, which preserves relative order.
        /// </summary>
        public IReadOnlyList<ZoneLocationEntry> Ordered { get; }

        /// <summary>
        /// False when the AssetIDs were synthesised from prefab names. Then two entries that share one
        /// prefab ASSET but not its name would be split into two <c>m_locationIDCache</c> buckets,
        /// which the game would not do. The dumper settles it (spec 02 section 6.1).
        /// </summary>
        public bool AssetIdsAreReal { get; }

        public IReadOnlyList<string> Notes { get; }

        public static LocationTable FromEntries(IEnumerable<ZoneLocationEntry> entries, bool assetIdsAreReal = true,
                                                IReadOnlyList<string>? notes = null)
        {
            List<ZoneLocationEntry> all = new List<ZoneLocationEntry>(entries);
            List<string> n = notes != null ? new List<string>(notes) : new List<string>();

            // OrderByDescending on a bool is a stable partition: prioritized first, original order kept
            // inside each group.
            List<ZoneLocationEntry> ordered = new List<ZoneLocationEntry>(all.Count);
            foreach (ZoneLocationEntry e in all) if (e.Prioritized) ordered.Add(e);
            foreach (ZoneLocationEntry e in all) if (!e.Prioritized) ordered.Add(e);

            // ... then the removal pass, backwards, AFTER the sort.
            for (int i = ordered.Count - 1; i >= 0; i--)
                if (!ordered[i].Enable || ordered[i].Quantity == 0) ordered.RemoveAt(i);

            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ZoneLocationEntry e in ordered)
                if (!seen.Add(e.PrefabName))
                    n.Add("Two ordered entries share the prefab name '" + e.PrefabName
                          + "': they share an RNG stream seed AND CountNrOfLocation starts the second "
                          + "one at the first one's count. That is the game's behaviour, not a bug here.");

            return new LocationTable(all, ordered, assetIdsAreReal, n);
        }

        /// <summary>
        /// Builds a table from a dumped <c>locations.json</c>. Enum-valued fields arrive as ints; every
        /// float arrives verbatim. The precomputed <c>nameHash</c> is CHECKED against
        /// <see cref="StableHash"/> rather than trusted, because a disagreement there would silently
        /// shift every draw of that entry's stream.
        /// </summary>
        public static LocationTable FromDump(IReadOnlyList<LocationDef> defs)
        {
            if (defs == null) throw new ArgumentNullException(nameof(defs));
            List<ZoneLocationEntry> entries = new List<ZoneLocationEntry>(defs.Count);
            List<string> notes = new List<string>();
            bool realIds = true;
            int idLessDisabled = 0;

            foreach (LocationDef d in defs)
            {
                string prefab = d.softRefName ?? d.prefabName ?? d.name ?? "";
                if (prefab.Length == 0)
                    throw new InvalidOperationException("locations.json entry " + d.index + " has no prefab name.");

                int hash = StableHash.Compute(prefab);
                if (d.nameHash != 0 && d.nameHash != hash)
                    throw new InvalidOperationException(
                        "locations.json entry '" + prefab + "' carries nameHash " + d.nameHash
                        + " but GetStableHashCode(\"" + prefab + "\") is " + hash
                        + ". One of the two is wrong and the whole stream depends on it.");

                if (d.prefabName != null && d.softRefName != null && d.prefabName != d.softRefName)
                    notes.Add("Entry " + d.index + ": m_prefabName '" + d.prefabName
                              + "' differs from m_prefab.Name '" + d.softRefName
                              + "'. The STREAM uses m_prefab.Name; the .db2 hash uses m_prefabName.");

                AssetId id;
                if (d.assetId != null && (d.assetId.v3 | d.assetId.v2 | d.assetId.v1 | d.assetId.v0) != 0u)
                    id = new AssetId(d.assetId.v3, d.assetId.v2, d.assetId.v1, d.assetId.v0);
                else
                {
                    id = AssetId.FromPrefabNameFallback(prefab);
                    // Only an entry that actually RUNS can put a stand-in id into m_locationIDCache.
                    // Vanilla 1.0.15 has 19 id-less entries and every one of them is m_enable = false,
                    // so the fallback never reaches a placement run - measured 2026-09-23 against
                    // the shipped data folder's locations.json.
                    if (d.enable && d.quantity != 0) realIds = false;
                    else idLessDisabled++;
                }

                entries.Add(new ZoneLocationEntry
                {
                    Index = d.index,
                    Name = d.name ?? "",
                    Enable = d.enable,
                    PrefabName = prefab,
                    NameHash = hash,
                    AssetId = id,
                    Biome = (Biome)d.biome,
                    BiomeArea = (BiomeArea)d.biomeArea,
                    Quantity = d.quantity,
                    Prioritized = d.prioritized,
                    CenterFirst = d.centerFirst,
                    Unique = d.unique,
                    Group = d.group ?? "",
                    MinDistanceFromSimilar = d.minDistanceFromSimilar,
                    GroupMax = d.groupMax ?? "",
                    MaxDistanceFromSimilar = d.maxDistanceFromSimilar,
                    InteriorRadius = d.interiorRadius,
                    ExteriorRadius = d.exteriorRadius,
                    MinTerrainDelta = d.minTerrainDelta,
                    MaxTerrainDelta = d.maxTerrainDelta,
                    MinimumVegetation = d.minimumVegetation,
                    MaximumVegetation = d.maximumVegetation,
                    SurroundCheckVegetation = d.surroundCheckVegetation,
                    SurroundCheckDistance = d.surroundCheckDistance,
                    SurroundCheckLayers = d.surroundCheckLayers,
                    SurroundBetterThanAverage = d.surroundBetterThanAverage,
                    InForest = d.inForest,
                    ForestTresholdMin = d.forestTresholdMin,
                    ForestTresholdMax = d.forestTresholdMax,
                    MinDistanceFromCenter = d.minDistanceFromCenter,
                    MaxDistanceFromCenter = d.maxDistanceFromCenter,
                    MinDistance = d.minDistance,
                    MaxDistance = d.maxDistance,
                    MinAltitude = d.minAltitude,
                    MaxAltitude = d.maxAltitude,
                    AltBiomeParent = d.altBiomeParent,
                    IconAlways = d.iconAlways,
                    IconPlaced = d.iconPlaced,
                    RandomRotation = d.randomRotation,
                    SlopeRotation = d.slopeRotation,
                    SnapToWater = d.snapToWater,
                    ClearArea = d.clearArea,
                });
            }

            if (!realIds)
                notes.Add("At least one entry that RUNS had no m_prefab.m_assetID; a name-derived stand-in "
                          + "was used, so two entries sharing one prefab asset under different names would "
                          + "be split into two m_locationIDCache buckets. See AssetId.FromPrefabNameFallback.");
            else if (idLessDisabled > 0)
                notes.Add(idLessDisabled + " entries have no m_prefab.m_assetID, but all of them are "
                          + "disabled or have m_quantity 0, so none of them reaches a placement run and "
                          + "the stand-in ids cannot affect anything.");

            return FromEntries(entries, realIds, notes);
        }

        /// <summary>
        /// Position of <paramref name="prefabName"/> in <see cref="Ordered"/>, or -1.
        /// </summary>
        public int OrderedIndexOf(string prefabName)
            => m_orderedIndexByPrefab.TryGetValue(prefabName, out int i) ? i : -1;

        /// <summary>
        /// How much of <see cref="Ordered"/> has to run to get <paramref name="prefabName"/> right.
        ///
        /// <para>A location type's own RNG stream is independent of every other type, so nothing AFTER
        /// the target can change it (spec 02 section 7). But everything BEFORE it can, through four
        /// channels - zone occupancy (global, one location per zone), the AssetID bucket, the
        /// group/groupMax buckets, and <c>CountNrOfLocation</c> - and zone occupancy is global, so no
        /// predecessor can be skipped. The prefix is therefore exactly <c>OrderedIndexOf + 1</c>,
        /// and it is short only for prioritized entries near the front.</para>
        /// </summary>
        public int PrefixLengthForTarget(string prefabName)
        {
            int i = OrderedIndexOf(prefabName);
            if (i < 0) throw new KeyNotFoundException(
                "'" + prefabName + "' is not in the ordered list (missing, disabled, or quantity 0).");
            return i + 1;
        }
    }
}
