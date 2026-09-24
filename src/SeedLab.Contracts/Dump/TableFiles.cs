namespace SeedLab.Contracts.Dump
{
    /// <summary>
    /// <c>locations.json</c>. The array is in <c>ZoneSystem.m_locations</c> order and the order is
    /// semantic - it decides everything downstream - so it is never an object keyed by name.
    /// </summary>
    public sealed class LocationTableFile
    {
        public string? stamp;
        public int schema;
        public int count;

        /// <summary>Count of entries with <c>enable &amp;&amp; quantity != 0</c>, i.e. the length of the
        /// ordered list the placement run walks.</summary>
        public int enabledCount;

        /// <summary>Prefab names of entries whose <c>ZoneLocation.Hash</c> collided with an earlier
        /// entry. <c>SetupLocations</c> logs an error and ignores the later one, so the tool must too.</summary>
        public string[]? duplicateHashPrefabNames;

        public LocationDef[]? locations;
    }

    /// <summary><c>vegetation.json</c>, in <c>ZoneSystem.m_vegetation</c> order.</summary>
    public sealed class VegetationTableFile
    {
        public string? stamp;
        public int schema;
        public int count;
        public int enabledCount;
        public VegetationDef[]? vegetation;
    }

    /// <summary><c>altbiomes.json</c>, in <c>AltBiomeList.m_altBiomes</c> order.</summary>
    public sealed class AltBiomeTableFile
    {
        public string? stamp;
        public int schema;
        public int count;
        public int enabledCount;
        public AltBiomeDef[]? altBiomes;
    }

    /// <summary>
    /// <c>version-constants.json</c>. These are <c>const</c>/enum members in the IL, not serialized
    /// asset data - they are read here anyway so the dump is self-contained and a bump is visible.
    /// Two details the port must not smooth over: <c>Minimap.TryLoadMinimapTextureData</c> tests the
    /// world version with EXACT EQUALITY, and <c>Minimap.SaveMapTextureDataToDisk</c> writes a
    /// hard-coded literal <c>1</c> rather than the <c>Version.CachedMinimap</c> enum.
    /// </summary>
    public sealed class VersionConstantsFile
    {
        public string? stamp;
        public int schema;

        public string? gameVersion;
        public int networkVersion;

        /// <summary><c>Version.c_WorldVersion</c> = <c>Version.World.DeepNorth</c> = 41 for this build.</summary>
        public int worldVersion;

        /// <summary><c>Version.c_WorldGenVersion</c> = 2.</summary>
        public int worldGenVersion;

        /// <summary><c>Version.c_CachedMinimapVersion</c> = <c>Version.CachedMinimap.Original</c> = 1.</summary>
        public int cachedMinimapVersion;

        /// <summary><c>Version.c_PlayerVersion</c>.</summary>
        public int playerVersion;

        /// <summary>The literal <c>Minimap.SaveMapTextureDataToDisk</c> actually writes, as observed in
        /// the IL. Recorded separately from <see cref="cachedMinimapVersion"/> because a tool that
        /// derives the written value from the enum would silently diverge if the two ever disagree.</summary>
        public int cachedMinimapWrittenLiteral;

        /// <summary><c>AltBiomeWorldData</c> geometry constants: 2048, 12, 1024, 6.</summary>
        public int biomeGridTextureSize;

        public float biomeGridPixelSize;
        public int biomeGridHalfWidth;
        public float biomeGridHalfPixel;
    }
}
