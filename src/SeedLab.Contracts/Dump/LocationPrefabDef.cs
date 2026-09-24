namespace SeedLab.Contracts.Dump
{
    /// <summary>
    /// Per-location-prefab data: the <c>Location</c> component's radii and the
    /// <c>DungeonGenerator</c>'s local transform. These are the only fields that require touching the
    /// location prefabs themselves, which are <c>SoftReference</c> assets loaded on demand.
    ///
    /// Why the generator's local offset matters: <c>DungeonGenerator.GetSeed</c> is
    /// <code>
    /// seed + zone.x * 4271 + zone.y * -7187
    ///      + (int)position.x * -4271 + (int)position.y * 9187 + (int)position.z * -2134
    /// </code>
    /// using the GENERATOR's world position, which is
    /// <c>locationPos + locationRot * generatorLocalPos</c>. With a non-zero XZ offset the unseeded
    /// location rotation feeds the dungeon seed. <c>(int)</c> is a truncation toward zero, not a floor.
    /// </summary>
    public sealed class LocationPrefabsFile
    {
        public string? stamp;
        public int schema;
        public int count;
        public int loadedCount;
        public int failedCount;
        public LocationPrefabDef[]? prefabs;
    }

    public sealed class LocationPrefabDef
    {
        public string? prefabName;
        public AssetIdDef? assetId;
        public bool loaded;
        public string? error;

        public bool hasLocationComponent;

        /// <summary>The instantiated location's radius, used by <c>Location.IsInsideLocation</c> - NOT
        /// the <c>ZoneLocation.m_exteriorRadius</c> that placement uses. They are different fields.</summary>
        public float exteriorRadius;

        public float interiorRadius;
        public bool hasInterior;
        public bool noBuild;
        public float noBuildRadiusOverride;
        public bool clearArea;
        public string? discoverLabel;

        /// <summary><c>Location.m_useCustomInteriorTransform</c>. Its own tooltip says it "must be used
        /// together with DungeonGenerator.m_useCustomInteriorTransform to make sure seeds are
        /// deterministic".</summary>
        public bool useCustomInteriorTransform;

        /// <summary><c>Location.m_biome</c>, for the in-game discover label.</summary>
        public int biome;

        public DungeonGeneratorDef[]? generators;
    }

    public sealed class DungeonGeneratorDef
    {
        /// <summary>Transform path from the location prefab root, so the reader can see where it sits.</summary>
        public string? path;

        /// <summary>Position relative to the location prefab root. A non-zero x or z means the unseeded
        /// location rotation feeds this dungeon's seed.</summary>
        public Vec3Def? localPosition;

        public Vec3Def? localEulerAngles;

        /// <summary>
        /// <c>generator.transform.localPosition</c> - relative to the generator's own PARENT, which is
        /// not the same quantity as <see cref="localPosition"/> (that one is
        /// <c>root.InverseTransformPoint(generator.position)</c>, measured from the asset root with
        /// scale divided out). 9 of the 21 generators in 1.0.15 hang under an <c>Interior/</c> parent,
        /// so the two differ in general.
        ///
        /// <para>It matters because <c>ZoneSystem.SpawnLocation</c> reads exactly this value and
        /// assigns it to the instantiated generator's <c>m_originalPosition</c> - but only when
        /// <c>Location.m_useCustomInteriorTransform</c> is set AND both <c>m_interiorTransform</c> and
        /// <c>m_generator</c> exist (18 location prefabs in 1.0.15). <c>Generate</c> then computes
        /// <c>m_zoneCenter.y = transform.position.y - m_originalPosition.y</c>, and that y decides the
        /// bounds every <c>IsInsideDungeon</c> corner test runs against. For those locations the
        /// AUTHORED <see cref="originalPosition"/> is dead and this is the live value.</para>
        /// </summary>
        public Vec3Def? parentLocalPosition;

        /// <summary>True when <see cref="localPosition"/>.x or .z is non-zero.</summary>
        public bool hasNonZeroXZOffset;

        public bool useCustomInteriorTransform;
        public int algorithm;
        public int minRooms;
        public int maxRooms;

        /// <summary><c>m_minRequiredRooms</c>: how many of <see cref="requiredRoomNames"/> must be
        /// placed for <c>CheckRequiredRooms</c> to accept the layout.</summary>
        public int minRequiredRooms;

        /// <summary><c>m_requiredRooms</c>, verbatim. These are matched against a placed room's
        /// GameObject name, which <c>PlaceRoom</c> sets to <c>roomData.m_prefab.Name</c>.</summary>
        public string[]? requiredRoomNames;

        /// <summary><c>m_themes</c> as the raw <c>Room.Theme</c> bitmask. This is the value
        /// <c>SetupAvailableRooms</c> tests against each <c>RoomData.m_theme</c>, so it is the link
        /// from this location to the entries in <c>roomchildren.json</c> it can contain.</summary>
        public int themes;

        /// <summary><c>m_addBaseSeedToRandomSpawn</c>. When true, <c>PlaceRoom</c> adds this
        /// generator's <c>GetSeed()</c> to each room's own RandomSpawn seed, so two identical rooms at
        /// the same local position in two different dungeons roll differently. When false they roll
        /// the same.</summary>
        public bool addBaseSeedToRandomSpawn;

        public float campRadiusMin;
        public float campRadiusMax;

        // ---- captured from 2026-09-23 onward ---------------------------------------------------------

        /// <summary>
        /// False on every file written before 2026-09-23, where the fields below this line do not exist
        /// and JSON deserialisation leaves them at zero. Zero is not a safe default for any of them -
        /// <c>maxTilt</c> 0 rejects every slope, <c>zoneSize</c> 0 puts every room outside the dungeon
        /// bounds - so a reader that needs them must refuse when this is false rather than proceed.
        /// The dump's schema number is deliberately NOT bumped for this: the change is additive and
        /// <see cref="DumpFormat.Schema"/> is documented to move only when an existing field changes
        /// meaning. This flag is the narrower, per-record statement of the same thing.
        /// </summary>
        public bool fullFieldsCaptured;

        /// <summary><c>m_maxTilt</c>, degrees. Both camp algorithms reject a candidate whose ground
        /// normal fails <c>normal.y &lt; Mathf.Cos(PI/180 * m_maxTilt)</c>, so it is a hard gate on
        /// every room placement, not a cosmetic limit.</summary>
        public float maxTilt;

        /// <summary><c>m_tileWidth</c> - CampGrid only.</summary>
        public float tileWidth;

        /// <summary><c>m_gridSize</c> - CampGrid only. The grid is <c>gridSize x gridSize</c> tiles.</summary>
        public int gridSize;

        /// <summary><c>m_spawnChance</c> - CampGrid only: <c>Random.value &gt; m_spawnChance</c> skips a
        /// tile, and the draw happens whether or not it is skipped.</summary>
        public float spawnChance;

        /// <summary><c>m_minAltitude</c>. <c>GenerateCampRadial</c> and <c>PlaceWall</c> reject a
        /// candidate when <c>p.y - 30f &lt; m_minAltitude</c>, where <c>p.y</c> is the ground height
        /// <c>ZoneSystem.GetGroundData</c> wrote back - so the test is against height above sea level.
        /// <c>GenerateCampGrid</c> does NOT test it: its only gate is the tilt one.</summary>
        public float minAltitude;

        /// <summary><c>m_perimeterSections</c>: how many perimeter rooms <c>PlaceWall</c> places after
        /// the main loop, or 0 for no wall pass. Non-zero means the RNG stream continues past the last
        /// room.</summary>
        public int perimeterSections;

        /// <summary><c>m_perimeterBuffer</c>. CampRadial draws each candidate's distance as
        /// <c>Random.Range(0f, radius - m_perimeterBuffer)</c>, so this changes the value of EVERY
        /// radial draw, not just the ones near the edge.</summary>
        public float perimeterBuffer;

        /// <summary><c>m_alternativeFunctionality</c>: in the Dungeon algorithm it switches ROOM
        /// selection from uniform (<c>GetRandomRoom</c>) to weighted (<c>GetRandomWeightedRoom</c>).
        /// For END CAPS it is not a switch but an addition - <c>PlaceEndCaps</c> prepends up to five
        /// <c>GetWeightedRoom</c> attempts and the <c>OrderByDescending(m_endCapPrio)</c> fallback loop
        /// still runs after them. Either way it changes both the choice and the number of
        /// draws.</summary>
        public bool alternativeFunctionality;

        /// <summary><c>m_doorChance</c>, used for a door type whose own <c>m_chance</c> is 0.</summary>
        public float doorChance;

        /// <summary><c>m_doorTypes</c> in list order - <c>FindDoorType</c> collects every entry whose
        /// <c>m_connectionType</c> matches and picks one with <c>Random.Range(0, list.Count)</c>.</summary>
        public DoorDefDef[]? doorTypes;

        /// <summary><c>m_zoneCenter</c> as authored. At generation time <c>Generate</c> overwrites it
        /// with the zone's centre and sets <c>y = transform.position.y - m_originalPosition.y</c>, so
        /// the authored value matters only when there is no ZoneSystem.</summary>
        public Vec3Def? zoneCenter;

        /// <summary><c>m_zoneSize</c>. With <see cref="zoneCenter"/> this is the <c>Bounds</c> that
        /// <c>IsInsideDungeon</c> tests all eight corners of a candidate room against, and
        /// <c>TestCollision</c> calls it first - so it gates <c>GenerateCampRadial</c> as well as the
        /// Dungeon algorithm. <c>GenerateCampGrid</c> calls neither: it places on the grid without a
        /// collision or bounds test at all.</summary>
        public Vec3Def? zoneSize;

        /// <summary>
        /// <c>m_originalPosition</c> as AUTHORED on the prefab. <c>[HideInInspector]</c> but
        /// serialised, and <c>Generate</c> subtracts its <c>y</c> from the generator's world <c>y</c>
        /// to place the dungeon bounds vertically.
        ///
        /// <para><b>It is not always the value the game reads.</b> For a location with
        /// <c>m_useCustomInteriorTransform</c> (and an interior transform and a generator),
        /// <c>ZoneSystem.SpawnLocation</c> overwrites it on the instance with
        /// <see cref="parentLocalPosition"/> before calling <c>Generate</c>. Use this field only when
        /// that does not apply.</para>
        /// </summary>
        public Vec3Def? originalPosition;
    }

    /// <summary>One <c>DungeonGenerator.DoorDef</c>.</summary>
    public sealed class DoorDefDef
    {
        /// <summary>Index in <c>m_doorTypes</c>; the list order is what
        /// <c>FindDoorType</c>'s <c>Random.Range(0, list.Count)</c> indexes into.</summary>
        public int index;

        /// <summary><c>m_connectionType</c>, matched against <c>RoomConnection.m_type</c> by exact
        /// string equality.</summary>
        public string? connectionType;

        /// <summary><c>m_chance</c>, 0..1. Zero means "use the generator's <c>m_doorChance</c>" - the
        /// game tests <c>m_chance &gt; 0f</c> to decide which of the two applies.</summary>
        public float chance;

        /// <summary><c>Utils.GetPrefabName(m_prefab)</c>, or null when the entry has no prefab.</summary>
        public string? prefabName;

        public bool hasPrefab;
    }
}
