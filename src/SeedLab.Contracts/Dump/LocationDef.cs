namespace SeedLab.Contracts.Dump
{
    /// <summary>
    /// One <c>ZoneSystem.ZoneLocation</c> as it exists after <c>ZoneSystem.SetupLocations</c>
    /// (ZoneSystem.cs:148-262 for the declaration, :869-940 for SetupLocations).
    /// All 40 serialized fields plus the runtime-only extras, in declaration order - which is also
    /// Unity's serialization order (spec 04 section 1.1).
    ///
    /// Nothing here is a label: every field is read by
    /// <c>ZoneSystem.GenerateLocationsTimeSliced(ZoneLocation, ...)</c>. See spec 04 section 1.1 for the
    /// per-field reason and spec 02 for the algorithm.
    /// </summary>
    public sealed class LocationDef
    {
        // ---- identity and provenance (not serialized fields; recorded so the port never re-derives them)

        /// <summary>Index in <c>ZoneSystem.m_locations</c> after SetupLocations. Order is semantic.</summary>
        public int index;

        /// <summary>Index in the list the placement run actually walks:
        /// <c>m_locations.Where(enable &amp;&amp; quantity != 0).OrderByDescending(m_prioritized)</c>,
        /// a STABLE LINQ ordering (ZoneSystem.cs:1862-1876). -1 when the entry is filtered out.</summary>
        public int orderedIndex;

        public EntrySourceDef? source;

        // ---- the 40 serialized fields, in declaration order

        /// <summary>1. Read by a placement filter, not just a label: the alt-biome block test is
        /// <c>biomeSector.AltBiomes.Any(x =&gt; x.m_blockLocationNames.Contains(location.m_name))</c>
        /// (ZoneSystem.cs:2039). Dump verbatim.</summary>
        public string? name;

        /// <summary>2. <c>m_enable</c>. Outer filter.</summary>
        public bool enable;

        /// <summary>3. <c>m_prefabName</c>, the RUNTIME value: SetupLocations overwrites the serialized
        /// one with <c>m_prefab.Name</c>. It is what <c>ZoneSystem.Save</c> hashes into the .db2.</summary>
        public string? prefabName;

        /// <summary>4a. <c>m_prefab.m_assetID</c> - the "same prefab" identity in <c>m_locationIDCache</c>.</summary>
        public AssetIdDef? assetId;

        /// <summary>4b. <c>m_prefab.Name</c>. THE RNG STREAM KEY:
        /// <c>seed = WorldGenerator.instance.GetSeed() + location.m_prefab.Name.GetStableHashCode()</c>
        /// (ZoneSystem.cs:1880). Equal to <see cref="prefabName"/> after SetupLocations, dumped
        /// separately so a future divergence is visible rather than silent.</summary>
        public string? softRefName;

        /// <summary><c>m_prefab.Name.GetStableHashCode()</c> = <c>ZoneLocation.Hash</c>, precomputed so
        /// the port never has to trust its own hash on these exact strings.</summary>
        public int nameHash;

        /// <summary>5. <c>Heightmap.Biome</c> bitmask (Meadows 1, Swamp 2, Mountain 4, BlackForest 8,
        /// Plains 16, AshLands 32, DeepNorth 64, Ocean 256, Mistlands 512).</summary>
        public int biome;

        /// <summary>6. <c>Heightmap.BiomeArea</c>: Edge 1, Median 2, Everything 3.</summary>
        public int biomeArea;

        /// <summary>7. Loop bound.</summary>
        public int quantity;

        /// <summary>8. Ordering key AND attempt budget (60000 prioritized vs 12000).</summary>
        public bool prioritized;

        /// <summary>9. Selects <c>GetRandomZone(maxRange)</c> instead of a biome-point draw - a different
        /// RNG consumption pattern entirely.</summary>
        public bool centerFirst;

        /// <summary>10.</summary>
        public bool unique;

        /// <summary>11.</summary>
        public string? group;

        /// <summary>12.</summary>
        public float minDistanceFromSimilar;

        /// <summary>13.</summary>
        public string? groupMax;

        /// <summary>14.</summary>
        public float maxDistanceFromSimilar;

        /// <summary>15. Drawn on every player's map from the start - but only if the prefab name also
        /// appears in <c>Minimap.m_locationIcons</c>, which has exactly 5 entries.</summary>
        public bool iconAlways;

        /// <summary>16.</summary>
        public bool iconPlaced;

        /// <summary>17. Rotation is <c>Random.Range(0,16) * 22.5</c> from the AMBIENT stream, so it is
        /// not seed-determined - and it shifts a dungeon generator's world position, hence its seed.</summary>
        public bool randomRotation;

        /// <summary>18. Uses <c>ZoneSystem.GetTerrainDelta</c> (:2701, physics raycasts) at spawn -
        /// a DIFFERENT method from the <c>WorldGenerator.GetTerrainDelta</c> of rows 23/24.</summary>
        public bool slopeRotation;

        /// <summary>19. Sets y = 30 at spawn, which changes the dungeon seed's <c>(int)position.y</c> term.</summary>
        public bool snapToWater;

        /// <summary>20. Part of <c>maxRadius = Max(exteriorRadius, interiorRadius)</c>, the inset in
        /// <c>GetRandomPointInZone</c> - so it changes the drawn coordinate itself.</summary>
        public float interiorRadius;

        /// <summary>21. Same maxRadius, the radius passed to GetTerrainDelta, and the ClearArea half-size.</summary>
        public float exteriorRadius;

        /// <summary>22. ClearArea is an axis-aligned SQUARE of half-size <see cref="exteriorRadius"/>.</summary>
        public bool clearArea;

        /// <summary>23. Filter via <c>WorldGenerator.GetTerrainDelta</c> (WorldGenerator.cs:1418, called
        /// unconditionally at ZoneSystem.cs:2001) - 10 <c>Random.insideUnitCircle</c> draws EVERY time,
        /// whatever the bounds are. It is on the RNG-consumption path, not just the accept path.</summary>
        public float minTerrainDelta;

        /// <summary>24. Same call, same 10 draws.</summary>
        public float maxTerrainDelta;

        /// <summary>25.</summary>
        public float minimumVegetation;

        /// <summary>26.</summary>
        public float maximumVegetation;

        /// <summary>27.</summary>
        public bool surroundCheckVegetation;

        /// <summary>28.</summary>
        public float surroundCheckDistance;

        /// <summary>29. <c>layers * 6</c> samples.</summary>
        public int surroundCheckLayers;

        /// <summary>30.</summary>
        public float surroundBetterThanAverage;

        /// <summary>31. Enables the <c>WorldGenerator.GetForestFactor</c> filter (static, seedless).</summary>
        public bool inForest;

        /// <summary>32. Spelt "Treshold" in the game; kept verbatim.</summary>
        public float forestTresholdMin;

        /// <summary>33.</summary>
        public float forestTresholdMax;

        /// <summary>34. Second, redundant distance pair, measured with <c>Utils.LengthXZ</c>.</summary>
        public float minDistanceFromCenter;

        /// <summary>35.</summary>
        public float maxDistanceFromCenter;

        /// <summary>36. First distance pair (<c>point.magnitude</c>, y = 0), and the initial maxRange
        /// when <see cref="centerFirst"/>.</summary>
        public float minDistance;

        /// <summary>37.</summary>
        public float maxDistance;

        /// <summary>38. ALSO SELECTS THE ZONE-DRAW FUNCTION: <c>m_minAltitude &lt; 0</c> uses
        /// <c>GetRandomPointByBiomes</c>, otherwise <c>GetRandomPointByBiomesAboveSeaLevel</c>
        /// (ZoneSystem.cs:1919). A sign error here changes every subsequent draw of the stream.</summary>
        public float minAltitude;

        /// <summary>39.</summary>
        public float maxAltitude;

        /// <summary>40. Editor foldout state. Dumped only because it occupies a serialization slot.</summary>
        public bool foldout;

        // ---- runtime-only

        /// <summary>Private <c>m_altBiomeParent</c>, set by SetupLocations for entries that came from an
        /// <c>AltBiome.m_addLocations</c>. Drives the alt-biome gate in the point filter. Null otherwise.</summary>
        public string? altBiomeParent;
    }
}
