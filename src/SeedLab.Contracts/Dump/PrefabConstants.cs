namespace SeedLab.Contracts.Dump
{
    /// <summary>
    /// Serialized prefab scalars that do not exist in the IL. A code default is NOT the shipped value:
    /// <c>Minimap.m_textureSize</c> is 256 in code and 2048 in the prefab, <c>m_pixelSize</c> 64 vs 12,
    /// <c>ZoneSystem.m_locationVersion</c> 1 vs 32. Every number here is measured at runtime.
    /// </summary>
    public sealed class PrefabConstantsFile
    {
        public string? stamp;
        public int schema;
        public ZoneSystemConstantsDef? zoneSystem;
        public MinimapConstantsDef? minimap;
        public HeightmapConstantsDef? heightmap;
    }

    public sealed class ZoneSystemConstantsDef
    {
        /// <summary>Code default 1, shipped 32 (read from the user's _main.N.db2). <c>ZoneSystem.Load</c>
        /// forces regeneration when the saved value differs, so a tool must refuse to compare its
        /// prediction with a save whose stored version is not this.</summary>
        public int locationVersion;

        /// <summary>Sea level. Every altitude filter is <c>y - m_waterLevel</c>. Also <c>c_WaterLevel</c>.</summary>
        public float waterLevel;

        /// <summary>Also <c>c_ZoneSize</c>.</summary>
        public float zoneSize;

        public float zoneTTL;
        public float zoneTTS;

        /// <summary>Dead at runtime - the only reference is the <c>stfld</c> in <c>ZoneSystem..ctor</c>.
        /// Dumped once to prove it is empty.</summary>
        public string[]? locationScenes;

        public LocationListDef[]? locationLists;

        /// <summary>True when two <c>LocationList</c>s share an <c>m_sortOrder</c>. SetupLocations sorts
        /// them with <c>List.Sort</c>, which is UNSTABLE, so a tie means the concatenation order is not
        /// guaranteed to be the same between runs of the same build.</summary>
        public bool sortOrderTies;

        public string[]? altBiomeListNames;

        /// <summary><c>Settings.AssetMemoryUsagePolicy</c> as an integer. When it has
        /// <c>KeepAsynchronousLoadedBit</c>, SetupLocations has already Load()ed and held every enabled
        /// location prefab, so the prefab walk is nearly free.</summary>
        public int assetMemoryUsagePolicy;

        public int zoneCtrlPrefabPresent;
        public int locationProxyPrefabPresent;
    }

    /// <summary>One entry of <c>ZoneSystem.m_locationLists</c>, after <c>LocationList.Awake</c> registered it.</summary>
    public sealed class LocationListDef
    {
        public string? name;
        public int sortOrder;
        public int locationCount;
        public int vegetationCount;

        /// <summary>The half-open range [first, first + locationCount) this list occupies in
        /// <c>ZoneSystem.m_locations</c>, or -1 if it could not be matched.</summary>
        public int firstLocationIndex;

        public int firstVegetationIndex;
    }

    /// <summary>
    /// <c>Minimap</c> prefab data. Needed both to render a map that looks like the game's and to decode
    /// the ground-truth minimap cache.
    /// </summary>
    public sealed class MinimapConstantsDef
    {
        /// <summary>2048 in the shipped prefab. The cache files are textureSize^2 samples.</summary>
        public int textureSize;

        /// <summary>12.0 in the shipped prefab. Pixel centres are
        /// <c>wx = (j - textureSize/2) * pixelSize + pixelSize/2</c>, index <c>i*textureSize + j</c>.</summary>
        public float pixelSize;

        public float exploreRadius;
        public float exploreInterval;
        public float removeRadius;

        public ColorDef? meadowsColor;
        public ColorDef? ashlandsColor;
        public ColorDef? blackforestColor;
        public ColorDef? deepnorthColor;
        public ColorDef? heathColor;
        public ColorDef? swampColor;
        public ColorDef? mountainColor;

        /// <summary><c>private Color</c> with no <c>[SerializeField]</c> (Minimap.cs:274): Unity does not
        /// serialize it, the prefab cannot override it, so it is guaranteed to be the code default
        /// (0.2, 0.2, 0.2). Read by reflection.</summary>
        public ColorDef? mistlandsColor;

        /// <summary>Ocean has no field: <c>Minimap.GetPixelColor</c> returns <c>Color.white</c> for Ocean
        /// and its <c>default</c> arm is also white. Recorded so the collision is explicit: Ocean,
        /// Mountain and DeepNorth all render #ffffff, which makes the biome cache LOSSY.</summary>
        public ColorDef? oceanColorHardcoded;

        /// <summary><c>m_locationIcons</c> names in list order. A location with iconAlways/iconPlaced but
        /// no entry here draws no pin.</summary>
        public string[]? locationIcons;
    }

    /// <summary>
    /// <c>Heightmap.m_width</c> / <c>m_scale</c> on the zone prefab, forwarded verbatim to
    /// <c>HeightmapBuilder.RequestTerrainSync</c>. Nothing in the code ties them to the 64 m zone size,
    /// so they are genuinely prefab data. Needed only for the highest-fidelity ground-height model.
    /// </summary>
    public sealed class HeightmapConstantsDef
    {
        public int zoneWidth;
        public float zoneScale;
        public bool zoneIsDistantLod;

        /// <summary>Best effort: the first loaded <c>Heightmap</c> with <c>IsDistantLod</c> true, if any
        /// exists when the dump runs. -1 / NaN when none was found.</summary>
        public int distantLodWidth;

        public float distantLodScale;
        public bool distantLodFound;
    }
}
