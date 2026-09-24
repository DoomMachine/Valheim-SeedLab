namespace SeedLab.Contracts.Dump
{
    /// <summary>
    /// One <c>AltBiome</c> (AltBiome.cs), in <c>AltBiomeList.m_altBiomes</c> order - the static list
    /// that <c>AltBiomeList.Awake</c> fills from the one shipped <c>AltBiomeList</c> prefab.
    ///
    /// Two RNG facts the field list alone hides, both required to reproduce the assignment
    /// (<c>AltBiomeWorldData.GenerateAltBiomes</c>):
    /// (a) it opens with <c>UnityEngine.Random.InitState(WorldGenerator.instance.GetSeed() + 920)</c>;
    /// (b) after each per-pair <c>InitState((int)(biome.Key + m_name.GetStableHashCode() + worldSeed))</c>
    /// it calls <c>biome.Value.Sectors.Shuffle()</c>, permuting the per-biome sector list IN PLACE,
    /// and then draws one <c>Random.Range(0f, 1f)</c> per candidate sector.
    /// So <see cref="name"/> is load-bearing twice: stream seed, and the string compared by
    /// <c>m_blockLocationNames</c> / <c>m_incompatibleAltBiomes</c>.
    /// </summary>
    public sealed class AltBiomeDef
    {
        /// <summary>Index in <c>AltBiomeList.m_altBiomes</c>.</summary>
        public int index;

        /// <summary>The per-pair stream seed component and the blocking-test key.</summary>
        public string? name;

        /// <summary><c>name.GetStableHashCode()</c>, precomputed.</summary>
        public int nameHash;

        public bool enabled;

        /// <summary><c>Heightmap.Biome</c> bitmask, matched with <c>HasFlag</c> in
        /// <c>AltBiomeList.GetValidAltBiomes</c>.</summary>
        public int biome;

        // ---- name composition (BiomeSector.GetName); not used by placement

        public string? namePrefix;
        public string? nameSuffix;
        public string? nameOverride;

        /// <summary>Multiplied together by <c>BiomeSector.GetLevelUpChanceMultiplier</c>.</summary>
        public float levelUpChanceMultiplier;

        // ---- quota / chance

        public float minDistanceFromCenter;
        public int minAmountSpawned;
        public int maxAmountSpawned;

        /// <summary>Compared against the per-sector <c>Random.Range(0f, 1f)</c>, but only once
        /// <c>Sectors.Count &gt;= m_minAmountSpawned</c> - below the minimum the sector is taken
        /// unconditionally (subject to <c>CanAddModifier</c>).</summary>
        public float chance;

        // ---- BiomeSector.CanAddModifier filters

        /// <summary>A bitmask, but the loop tests it with <c>((BiomeIndex)i).ToBiome()</c> and then
        /// compares the neighbour with <c>(Heightmap.Biome)i</c> - so the bit and the neighbour value
        /// are from different enums and do not line up. Reproduce the bug, do not fix it.</summary>
        public int requireNeighbor;

        /// <summary>Same index/flag mismatch as <see cref="requireNeighbor"/>.</summary>
        public int notNeighbor;

        public string[]? incompatibleAltBiomes;
        public int minEdgeSize;
        public int maxEdgeSize;
        public float minAvgHeight;
        public float maxAvgHeight;
        public float belowWorldX;
        public float aboveWorldX;
        public float belowWorldY;
        public float aboveWorldY;

        // ---- content

        /// <summary>Appended to <c>ZoneSystem.m_locations</c> by SetupLocations, each tagged with
        /// <c>AltBiomeParent = m_name</c>. Dumped inline here as well as in locations.json.</summary>
        public LocationDef[]? addLocations;

        public string[]? blockLocationNames;

        public VegetationDef[]? addVegetation;

        public string[]? blockVegetationNames;

        // ---- dumped for completeness; not read by placement

        public string? forceMusic;
        public string? forceEnvironment;
        public string[]? blockEnvironments;
        public string[]? blockSpawnNames;
        public int addEnvironmentsCount;
        public int spawnCount;
        public int terrainTextureOverride;
    }

    /// <summary>
    /// One <c>BiomeSector</c> from <c>AltBiomeWorldData.Sectors</c>, identified by its index in THAT
    /// list. Never identify a sector by its position in a <c>BiomeTypeInfo.Sectors</c> list:
    /// <c>GenerateAltBiomes</c> shuffles those in place once per (biome, altBiome) pair, so two reads
    /// give two orders with no error. <c>AltBiomeWorldData.Sectors</c> is append-only and stable.
    /// </summary>
    public sealed class SectorDef
    {
        /// <summary>Index in <c>AltBiomeWorldData.Sectors</c>. The stable identity.</summary>
        public int index;

        /// <summary><c>Heightmap.Biome</c> value (bitmask form).</summary>
        public int biome;

        public int edgeCount;
        public Vec2Def? center;
        public Vec2Def? min;
        public Vec2Def? max;

        /// <summary><c>GenerateSectors</c> sets <c>MaxZone</c> from <c>Min</c>, not <c>Max</c>, and
        /// passes a <c>Vector3(x, y)</c> whose z is 0 while <c>GetZone</c> reads z. So MinZone always
        /// equals MaxZone and <c>ZoneCount</c> is always 1. A port must reproduce this.</summary>
        public Vec2IntDef? minZone;

        public Vec2IntDef? maxZone;
        public float heightMin;
        public float heightMax;
        public float heightAvg;
        public float distanceFromCenter;
        public bool isDiscovered;

        /// <summary>Biome values of <c>Neighbors</c>, in list order.</summary>
        public int[]? neighborBiomes;

        /// <summary><c>AltBiome.m_name</c> of every modifier added to this sector, in the order
        /// <c>AddModifier</c> appended them.</summary>
        public string[]? altBiomeNames;
    }

    /// <summary>One entry of the <c>AltBiomeWorldData.Biomes</c> dictionary, in enumeration order.</summary>
    public sealed class BiomeKeyDef
    {
        /// <summary>Position in the <c>foreach (KeyValuePair...)</c>. The order is a
        /// <c>Dictionary&lt;TKey,TValue&gt;</c> implementation detail, not a contract - which is exactly
        /// why it is recorded rather than re-derived. In practice it is insertion order, i.e.
        /// <c>Enum.GetValues(typeof(Heightmap.Biome))</c> order.</summary>
        public int order;

        /// <summary>The key. There are TWELVE, not nine: the enum also declares None = 0,
        /// Land = 0x27F and All = 0x37F, and <c>HasFlag(None)</c> is true for every value, so every
        /// enabled AltBiome gets an extra InitState + Shuffle + per-sector draw under the None key.</summary>
        public int biome;

        /// <summary><c>BiomeTypeInfo.Sectors.Count</c> for this key.</summary>
        public int sectorCount;

        /// <summary>Indices into <c>AltBiomeWorldData.Sectors</c> of this key's sectors, as observed
        /// AFTER <c>GenerateAltBiomes</c> (i.e. already shuffled). Recorded to make the permutation
        /// visible, not to be replayed.</summary>
        public int[]? sectorIndices;

        public int allPointsCount;
        public int allPointsAboveSeaLevelCount;
    }

    /// <summary>The result of <c>GenerateAltBiomes</c> for one AltBiome.</summary>
    public sealed class AltBiomeAssignmentDef
    {
        public string? altBiomeName;

        /// <summary><c>Sectors.Count</c> BEFORE the call. Non-zero here means the contamination bug of
        /// spec 04 section 3.7 Invariant 2 has occurred (GenerateAltBiomes never clears
        /// <c>AltBiome.Sectors</c>), and the dump must be discarded.</summary>
        public int sectorsBefore;

        public int sectorsAfter;
        public int validPlacementSectors;
        public int validPlacementSectorCombos;

        /// <summary>Indices into <c>AltBiomeWorldData.Sectors</c>, in <c>AddModifier</c> order.</summary>
        public int[]? sectorIndices;
    }
}
