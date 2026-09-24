namespace SeedLab.Contracts.Dump
{
    /// <summary>
    /// One <c>ZoneSystem.ZoneVegetation</c> (ZoneSystem.cs:34-145). All 40 serialized fields in
    /// declaration order - spec 04 section 1.2 (corrected there from "38" to 40; the count includes
    /// <c>m_prefab</c>).
    ///
    /// Vegetation acceptance uses physics raycasts (<c>ZoneSystem.PlaceVegetation</c>), so it is
    /// seed-driven but NOT reproducible bit-for-bit offline. Ore deposits and berry bushes are
    /// vegetation, not locations, and this table is the only reliable source of their prefab names -
    /// the vegetation prefab is a plain <c>GameObject</c> reference, not a <c>SoftReference</c>, so
    /// it does not appear in the SoftRef manifest.
    /// </summary>
    public sealed class VegetationDef
    {
        /// <summary>Index in <c>ZoneSystem.m_vegetation</c> after SetupLocations.</summary>
        public int index;

        public EntrySourceDef? source;

        /// <summary>1. Matched by <c>AltBiome.m_blockVegetationNames</c> (ZoneSystem.cs:1417).</summary>
        public string? name;

        /// <summary>2. <c>m_prefab.name</c> - the GameObject's name. THE RNG KEY for this entry
        /// (<c>veg.m_prefab.name.GetStableHashCode()</c>); it is not recoverable offline.</summary>
        public string? prefabName;

        /// <summary><c>prefabName.GetStableHashCode()</c>, precomputed.</summary>
        public int nameHash;

        /// <summary>3.</summary>
        public bool enable;

        /// <summary>4.</summary>
        public float min;

        /// <summary>5. <c>m_max &lt; 1</c> means "chance of exactly one", not "at most one".</summary>
        public float max;

        /// <summary>6. Tries = count * 50.</summary>
        public bool forcePlacement;

        /// <summary>7.</summary>
        public float scaleMin;

        /// <summary>8.</summary>
        public float scaleMax;

        /// <summary>9.</summary>
        public float randTilt;

        /// <summary>10.</summary>
        public float chanceToUseGroundTilt;

        /// <summary>11. <c>Heightmap.Biome</c> bitmask.</summary>
        public int biome;

        /// <summary>12. <c>Heightmap.BiomeArea</c>.</summary>
        public int biomeArea;

        /// <summary>13.</summary>
        public bool blockCheck;

        /// <summary>14.</summary>
        public bool snapToStaticSolid;

        /// <summary>15.</summary>
        public float minAltitude;

        /// <summary>16.</summary>
        public float maxAltitude;

        /// <summary>17. The vegetation-mask filter applies only when min != max.</summary>
        public float minVegetation;

        /// <summary>18.</summary>
        public float maxVegetation;

        /// <summary>19.</summary>
        public bool surroundCheckVegetation;

        /// <summary>20.</summary>
        public float surroundCheckDistance;

        /// <summary>21.</summary>
        public int surroundCheckLayers;

        /// <summary>22.</summary>
        public float surroundBetterThanAverage;

        /// <summary>23. Ocean-depth filter applies only when min != max.</summary>
        public float minOceanDepth;

        /// <summary>24.</summary>
        public float maxOceanDepth;

        /// <summary>25.</summary>
        public float minTilt;

        /// <summary>26.</summary>
        public float maxTilt;

        /// <summary>27. The terrain-delta filter applies only when this is &gt; 0.</summary>
        public float terrainDeltaRadius;

        /// <summary>28.</summary>
        public float maxTerrainDelta;

        /// <summary>29.</summary>
        public float minTerrainDelta;

        /// <summary>30.</summary>
        public bool snapToWater;

        /// <summary>31.</summary>
        public float groundOffset;

        /// <summary>32.</summary>
        public int groupSizeMin;

        /// <summary>33.</summary>
        public int groupSizeMax;

        /// <summary>34. Also the in-zone inset: the draw range is +/-(32 - groupRadius).</summary>
        public float groupRadius;

        /// <summary>35.</summary>
        public float minDistanceFromCenter;

        /// <summary>36.</summary>
        public float maxDistanceFromCenter;

        /// <summary>37.</summary>
        public bool inForest;

        /// <summary>38.</summary>
        public float forestTresholdMin;

        /// <summary>39.</summary>
        public float forestTresholdMax;

        /// <summary>40. Editor foldout state.</summary>
        public bool foldout;

        /// <summary>Runtime-only <c>AltBiomeParent</c>, set by SetupLocations for entries from an
        /// <c>AltBiome.m_addVegetation</c>.</summary>
        public string? altBiomeParent;
    }
}
