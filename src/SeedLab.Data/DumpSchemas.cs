using System;

namespace SeedLab.Data
{
    /// <summary>One field's shape in a <see cref="DumpSchema"/>.</summary>
    internal enum FieldKind
    {
        Scalar = 0,
        F32 = 1,
        F64 = 2,
        Object = 3,
        ObjectArray = 4,
    }

    internal readonly struct SchemaField
    {
        internal readonly string Name;
        internal readonly FieldKind Kind;
        internal readonly DumpSchema? Child;

        internal SchemaField(string name, FieldKind kind, DumpSchema? child)
        {
            Name = name; Kind = kind; Child = child;
        }
    }

    internal sealed class DumpSchema
    {
        internal readonly string TypeName;
        internal readonly SchemaField[] Fields;

        /// <summary>Names of the fields that must appear in this object's <c>"bits"</c> sibling.</summary>
        internal readonly string[] BitsFields;

        internal DumpSchema(string typeName, SchemaField[] fields)
        {
            TypeName = typeName;
            Fields = fields;

            int n = 0;
            foreach (SchemaField f in fields)
            {
                if (f.Kind == FieldKind.F32 || f.Kind == FieldKind.F64) n++;
            }

            BitsFields = new string[n];
            n = 0;
            foreach (SchemaField f in fields)
            {
                if (f.Kind == FieldKind.F32 || f.Kind == FieldKind.F64) BitsFields[n++] = f.Name;
            }
        }

        internal bool TryField(ReadOnlySpan<char> name, out SchemaField field)
        {
            for (int i = 0; i < Fields.Length; i++)
            {
                if (name.SequenceEqual(Fields[i].Name.AsSpan()))
                {
                    field = Fields[i];
                    return true;
                }
            }

            field = default;
            return false;
        }
    }

    /// <summary>
    /// The shape every dumped JSON object must have, one schema per DTO in
    /// <c>SeedLab.Contracts.Dump</c>. It exists so that a MISSING field is an error instead of a
    /// silently defaulted zero: <see cref="StrictJson"/> walks the file in lockstep with the schema
    /// and names the file and the field the moment one is absent.
    ///
    /// <para><b>ORDER OF OPERATIONS, and it is a trap.</b> Regenerate this file only AFTER the dump
    /// that carries the new fields exists, never before. On 2026-09-23 <c>DungeonGeneratorDef</c>
    /// gained fourteen fields and <c>RoomChildrenDef</c> two; regenerating first would have made the
    /// loader demand <c>fullFieldsCaptured</c> of a shipped <c>locationprefabs.json</c> that predates
    /// it, and every location answer in the tool would have failed closed on a file that is perfectly
    /// good. <see cref="StrictJson"/> ignores UNKNOWN members by design, so a schema lagging behind
    /// the DTO is harmless in the meantime - the new fields simply are not validated until the dump
    /// that carries them lands. Dump first, regenerate second.</para>
    ///
    /// <para>The field lists are a mechanical transcription of the DTO declarations - same names,
    /// same order, every public field - produced by <c>src\SeedLab.Data\gen_schema.py</c> from
    /// <c>src\SeedLab.Contracts\Dump\*.cs</c> and pasted here. They are hard-coded rather than
    /// reflected so that the loader stays trim- and AOT-safe. If a DTO gains a field, re-run the
    /// generator; the loader will then demand the new field, which is the intended behaviour - a dump
    /// written before the field existed is NOT usable data for code that reads it.</para>
    ///
    /// <para><see cref="FieldKind.F32"/>/<see cref="FieldKind.F64"/> additionally require the sibling
    /// <c>"bits"</c> entry the dump format promises (DumpFormat, "JSON conventions"), and the decimal
    /// is checked against it. Every float in the shipped dump agrees with its bits, so the
    /// source-generated deserializer's values are exact; the check is what keeps that true.</para>
    /// </summary>
    internal static class DumpSchemas
    {
        private static SchemaField Scalar(string n) => new SchemaField(n, FieldKind.Scalar, null);
        private static SchemaField F32(string n) => new SchemaField(n, FieldKind.F32, null);
        private static SchemaField F64(string n) => new SchemaField(n, FieldKind.F64, null);
        private static SchemaField Obj(string n, DumpSchema c) => new SchemaField(n, FieldKind.Object, c);
        private static SchemaField Arr(string n, DumpSchema c) => new SchemaField(n, FieldKind.ObjectArray, c);

        internal static readonly DumpSchema EntrySourceDef;
        internal static readonly DumpSchema AssetIdDef;
        internal static readonly DumpSchema LocationDef;
        internal static readonly DumpSchema VegetationDef;
        internal static readonly DumpSchema AltBiomeDef;
        internal static readonly DumpSchema Vec2Def;
        internal static readonly DumpSchema Vec2IntDef;
        internal static readonly DumpSchema SectorDef;
        internal static readonly DumpSchema BiomeKeyDef;
        internal static readonly DumpSchema AltBiomeAssignmentDef;
        internal static readonly DumpSchema GameInfoDef;
        internal static readonly DumpSchema DumperInfoDef;
        internal static readonly DumpSchema WorldInfoDef;
        internal static readonly DumpSchema CountsDef;
        internal static readonly DumpSchema FileEntryDef;
        internal static readonly DumpSchema DumpManifest;
        internal static readonly DumpSchema LocalizationFile;
        internal static readonly DumpSchema CharacterDef;
        internal static readonly DumpSchema OfferingBowlDef;
        internal static readonly DumpSchema RuneStoneDef;
        internal static readonly DumpSchema TraderDef;
        internal static readonly DumpSchema TeleportDef;
        internal static readonly DumpSchema VegvisirLocationDef;
        internal static readonly DumpSchema VegvisirDef;
        internal static readonly DumpSchema LocationChildrenDef;
        internal static readonly DumpSchema LocationChildrenFile;
        internal static readonly DumpSchema Vec3Def;
        internal static readonly DumpSchema RandomSpawnDef;
        internal static readonly DumpSchema DropDataDef;
        internal static readonly DumpSchema DropTableDef;
        internal static readonly DumpSchema ContainerDef;
        internal static readonly DumpSchema RandomObjectEntryDef;
        internal static readonly DumpSchema RandomObjectDef;
        internal static readonly DumpSchema ChildNameDef;
        internal static readonly DumpSchema PrefabNameDef;
        internal static readonly DumpSchema QuatDef;
        internal static readonly DumpSchema LocationOccupantsDef;
        internal static readonly DumpSchema LocationOccupantsFile;
        internal static readonly DumpSchema RoomOccupantsDef;
        internal static readonly DumpSchema RoomOccupantsFile;
        internal static readonly DumpSchema DoorDefDef;
        internal static readonly DumpSchema DungeonGeneratorDef;
        internal static readonly DumpSchema LocationPrefabDef;
        internal static readonly DumpSchema LocationPrefabsFile;
        internal static readonly DumpSchema RandomStateRoundTripDef;
        internal static readonly DumpSchema RandomInitStateDef;
        internal static readonly DumpSchema RandomDrawDef;
        internal static readonly DumpSchema RandomTraceDef;
        internal static readonly DumpSchema NativesRandomFile;
        internal static readonly DumpSchema PerlinBlockDef;
        internal static readonly DumpSchema NativesPerlinIndexFile;
        internal static readonly DumpSchema LibmSampleDef;
        internal static readonly DumpSchema WorldAngleSampleDef;
        internal static readonly DumpSchema NativesLibmFile;
        internal static readonly DumpSchema HalfSampleDef;
        internal static readonly DumpSchema NativesHalfFile;
        internal static readonly DumpSchema HashSampleDef;
        internal static readonly DumpSchema NativesHashFile;
        internal static readonly DumpSchema LocationListDef;
        internal static readonly DumpSchema ZoneSystemConstantsDef;
        internal static readonly DumpSchema ColorDef;
        internal static readonly DumpSchema MinimapConstantsDef;
        internal static readonly DumpSchema HeightmapConstantsDef;
        internal static readonly DumpSchema PrefabConstantsFile;
        internal static readonly DumpSchema RoomListDef;
        internal static readonly DumpSchema Vec3IntDef;
        internal static readonly DumpSchema RoomConnectionDef;
        internal static readonly DumpSchema RoomChildrenDef;
        internal static readonly DumpSchema RoomChildrenFile;
        internal static readonly DumpSchema SearchHitDef;
        internal static readonly DumpSchema SearchCoverageDef;
        internal static readonly DumpSchema SearchTermDef;
        internal static readonly DumpSchema SearchFile;
        internal static readonly DumpSchema SeedInputFieldDef;
        internal static readonly DumpSchema SeedInputFile;
        internal static readonly DumpSchema LocationTableFile;
        internal static readonly DumpSchema VegetationTableFile;
        internal static readonly DumpSchema AltBiomeTableFile;
        internal static readonly DumpSchema VersionConstantsFile;
        internal static readonly DumpSchema RiverDef;
        internal static readonly DumpSchema WorldGenDumpFile;
        internal static readonly DumpSchema LocationInstanceDef;
        internal static readonly DumpSchema LocationInstancesFile;
        internal static readonly DumpSchema AltBiomeAssignmentFile;

        static DumpSchemas()
        {
        EntrySourceDef = new DumpSchema("EntrySourceDef", new[]
            {
                Scalar("kind"),
                Scalar("name"),
                Scalar("sortOrder"),
            });

        AssetIdDef = new DumpSchema("AssetIdDef", new[]
            {
                Scalar("v3"),
                Scalar("v2"),
                Scalar("v1"),
                Scalar("v0"),
                Scalar("hex"),
                Scalar("isValid"),
            });

        LocationDef = new DumpSchema("LocationDef", new[]
            {
                Scalar("index"),
                Scalar("orderedIndex"),
                Obj("source", EntrySourceDef),
                Scalar("name"),
                Scalar("enable"),
                Scalar("prefabName"),
                Obj("assetId", AssetIdDef),
                Scalar("softRefName"),
                Scalar("nameHash"),
                Scalar("biome"),
                Scalar("biomeArea"),
                Scalar("quantity"),
                Scalar("prioritized"),
                Scalar("centerFirst"),
                Scalar("unique"),
                Scalar("group"),
                F32("minDistanceFromSimilar"),
                Scalar("groupMax"),
                F32("maxDistanceFromSimilar"),
                Scalar("iconAlways"),
                Scalar("iconPlaced"),
                Scalar("randomRotation"),
                Scalar("slopeRotation"),
                Scalar("snapToWater"),
                F32("interiorRadius"),
                F32("exteriorRadius"),
                Scalar("clearArea"),
                F32("minTerrainDelta"),
                F32("maxTerrainDelta"),
                F32("minimumVegetation"),
                F32("maximumVegetation"),
                Scalar("surroundCheckVegetation"),
                F32("surroundCheckDistance"),
                Scalar("surroundCheckLayers"),
                F32("surroundBetterThanAverage"),
                Scalar("inForest"),
                F32("forestTresholdMin"),
                F32("forestTresholdMax"),
                F32("minDistanceFromCenter"),
                F32("maxDistanceFromCenter"),
                F32("minDistance"),
                F32("maxDistance"),
                F32("minAltitude"),
                F32("maxAltitude"),
                Scalar("foldout"),
                Scalar("altBiomeParent"),
            });

        VegetationDef = new DumpSchema("VegetationDef", new[]
            {
                Scalar("index"),
                Obj("source", EntrySourceDef),
                Scalar("name"),
                Scalar("prefabName"),
                Scalar("nameHash"),
                Scalar("enable"),
                F32("min"),
                F32("max"),
                Scalar("forcePlacement"),
                F32("scaleMin"),
                F32("scaleMax"),
                F32("randTilt"),
                F32("chanceToUseGroundTilt"),
                Scalar("biome"),
                Scalar("biomeArea"),
                Scalar("blockCheck"),
                Scalar("snapToStaticSolid"),
                F32("minAltitude"),
                F32("maxAltitude"),
                F32("minVegetation"),
                F32("maxVegetation"),
                Scalar("surroundCheckVegetation"),
                F32("surroundCheckDistance"),
                Scalar("surroundCheckLayers"),
                F32("surroundBetterThanAverage"),
                F32("minOceanDepth"),
                F32("maxOceanDepth"),
                F32("minTilt"),
                F32("maxTilt"),
                F32("terrainDeltaRadius"),
                F32("maxTerrainDelta"),
                F32("minTerrainDelta"),
                Scalar("snapToWater"),
                F32("groundOffset"),
                Scalar("groupSizeMin"),
                Scalar("groupSizeMax"),
                F32("groupRadius"),
                F32("minDistanceFromCenter"),
                F32("maxDistanceFromCenter"),
                Scalar("inForest"),
                F32("forestTresholdMin"),
                F32("forestTresholdMax"),
                Scalar("foldout"),
                Scalar("altBiomeParent"),
            });

        AltBiomeDef = new DumpSchema("AltBiomeDef", new[]
            {
                Scalar("index"),
                Scalar("name"),
                Scalar("nameHash"),
                Scalar("enabled"),
                Scalar("biome"),
                Scalar("namePrefix"),
                Scalar("nameSuffix"),
                Scalar("nameOverride"),
                F32("levelUpChanceMultiplier"),
                F32("minDistanceFromCenter"),
                Scalar("minAmountSpawned"),
                Scalar("maxAmountSpawned"),
                F32("chance"),
                Scalar("requireNeighbor"),
                Scalar("notNeighbor"),
                Scalar("incompatibleAltBiomes"),
                Scalar("minEdgeSize"),
                Scalar("maxEdgeSize"),
                F32("minAvgHeight"),
                F32("maxAvgHeight"),
                F32("belowWorldX"),
                F32("aboveWorldX"),
                F32("belowWorldY"),
                F32("aboveWorldY"),
                Arr("addLocations", LocationDef),
                Scalar("blockLocationNames"),
                Arr("addVegetation", VegetationDef),
                Scalar("blockVegetationNames"),
                Scalar("forceMusic"),
                Scalar("forceEnvironment"),
                Scalar("blockEnvironments"),
                Scalar("blockSpawnNames"),
                Scalar("addEnvironmentsCount"),
                Scalar("spawnCount"),
                Scalar("terrainTextureOverride"),
            });

        Vec2Def = new DumpSchema("Vec2Def", new[]
            {
                F32("x"),
                F32("y"),
            });

        Vec2IntDef = new DumpSchema("Vec2IntDef", new[]
            {
                Scalar("x"),
                Scalar("y"),
            });

        SectorDef = new DumpSchema("SectorDef", new[]
            {
                Scalar("index"),
                Scalar("biome"),
                Scalar("edgeCount"),
                Obj("center", Vec2Def),
                Obj("min", Vec2Def),
                Obj("max", Vec2Def),
                Obj("minZone", Vec2IntDef),
                Obj("maxZone", Vec2IntDef),
                F32("heightMin"),
                F32("heightMax"),
                F32("heightAvg"),
                F32("distanceFromCenter"),
                Scalar("isDiscovered"),
                Scalar("neighborBiomes"),
                Scalar("altBiomeNames"),
            });

        BiomeKeyDef = new DumpSchema("BiomeKeyDef", new[]
            {
                Scalar("order"),
                Scalar("biome"),
                Scalar("sectorCount"),
                Scalar("sectorIndices"),
                Scalar("allPointsCount"),
                Scalar("allPointsAboveSeaLevelCount"),
            });

        AltBiomeAssignmentDef = new DumpSchema("AltBiomeAssignmentDef", new[]
            {
                Scalar("altBiomeName"),
                Scalar("sectorsBefore"),
                Scalar("sectorsAfter"),
                Scalar("validPlacementSectors"),
                Scalar("validPlacementSectorCombos"),
                Scalar("sectorIndices"),
            });

        GameInfoDef = new DumpSchema("GameInfoDef", new[]
            {
                Scalar("version"),
                Scalar("networkVersion"),
                Scalar("unityVersion"),
                Scalar("assemblyValheimSha256"),
                Scalar("unityPlayerSha256"),
                Scalar("assemblyValheimBytes"),
                Scalar("unityPlayerBytes"),
                Scalar("steamBuild"),
            });

        DumperInfoDef = new DumpSchema("DumperInfoDef", new[]
            {
                Scalar("version"),
                Scalar("mode"),
                Scalar("utc"),
                Scalar("guid"),
            });

        WorldInfoDef = new DumpSchema("WorldInfoDef", new[]
            {
                Scalar("name"),
                Scalar("seedText"),
                Scalar("seed"),
                Scalar("worldGenVersion"),
                Scalar("worldVersion"),
                Scalar("locationVersion"),
                Scalar("menu"),
            });

        CountsDef = new DumpSchema("CountsDef", new[]
            {
                Scalar("locations"),
                Scalar("locationsEnabled"),
                Scalar("vegetation"),
                Scalar("vegetationEnabled"),
                Scalar("altBiomes"),
                Scalar("altBiomesEnabled"),
                Scalar("locationLists"),
                Scalar("altBiomeLists"),
                Scalar("distinctLocationHashes"),
                Scalar("locationInstances"),
                Scalar("locationInstancesPlaced"),
            });

        FileEntryDef = new DumpSchema("FileEntryDef", new[]
            {
                Scalar("path"),
                Scalar("bytes"),
                Scalar("sha256"),
            });

        DumpManifest = new DumpSchema("DumpManifest", new[]
            {
                Scalar("stamp"),
                Scalar("schema"),
                Obj("game", GameInfoDef),
                Obj("dumper", DumperInfoDef),
                Obj("world", WorldInfoDef),
                Obj("counts", CountsDef),
                Scalar("sortOrderTies"),
                Arr("files", FileEntryDef),
                Scalar("notes"),
            });

        LocalizationFile = new DumpSchema("LocalizationFile", new[]
            {
                Scalar("stamp"),
                Scalar("schema"),
                Scalar("language"),
                Scalar("languages"),
                Scalar("count"),
                Scalar("translations"),
                Scalar("skipped"),
            });

        CharacterDef = new DumpSchema("CharacterDef", new[]
            {
                Scalar("path"),
                Scalar("prefabName"),
                Scalar("nameToken"),
                Scalar("localizedName"),
                Scalar("boss"),
                Scalar("bossOrder"),
                Scalar("faction"),
                Scalar("group"),
                Scalar("enabledInHierarchy"),
                Scalar("activeSelf"),
            });

        OfferingBowlDef = new DumpSchema("OfferingBowlDef", new[]
            {
                Scalar("path"),
                Scalar("prefabName"),
                Scalar("nameToken"),
                Scalar("localizedName"),
                Scalar("bossPrefabName"),
                Scalar("bossNameToken"),
                Scalar("bossLocalizedName"),
                Scalar("bossFlag"),
                Scalar("bossOrder"),
                Scalar("offeringItemName"),
                Scalar("offeringItemToken"),
                Scalar("offeringItemLocalizedName"),
                Scalar("offeringItemCount"),
                Scalar("setGlobalKey"),
            });

        RuneStoneDef = new DumpSchema("RuneStoneDef", new[]
            {
                Scalar("path"),
                Scalar("prefabName"),
                Scalar("nameToken"),
                Scalar("localizedName"),
                Scalar("topicToken"),
                Scalar("topicLocalized"),
                Scalar("labelToken"),
                Scalar("labelLocalized"),
                Scalar("locationNameToken"),
                Scalar("locationNameLocalized"),
                Scalar("pinNameToken"),
                Scalar("pinNameLocalized"),
            });

        TraderDef = new DumpSchema("TraderDef", new[]
            {
                Scalar("path"),
                Scalar("prefabName"),
                Scalar("nameToken"),
                Scalar("localizedName"),
                Scalar("enabledInHierarchy"),
                Scalar("activeSelf"),
            });

        TeleportDef = new DumpSchema("TeleportDef", new[]
            {
                Scalar("path"),
                Scalar("prefabName"),
                Scalar("hoverTextToken"),
                Scalar("hoverTextLocalized"),
                Scalar("enterTextToken"),
                Scalar("enterTextLocalized"),
                Scalar("hasTarget"),
                Scalar("targetInPrefab"),
                Scalar("targetPath"),
                Scalar("enabledInHierarchy"),
                Scalar("activeSelf"),
            });

        VegvisirLocationDef = new DumpSchema("VegvisirLocationDef", new[]
            {
                Scalar("locationName"),
                Scalar("pinNameToken"),
                Scalar("pinNameLocalized"),
                Scalar("pinType"),
                Scalar("pinTypeName"),
                Scalar("discoverAll"),
                Scalar("showMap"),
            });

        VegvisirDef = new DumpSchema("VegvisirDef", new[]
            {
                Scalar("path"),
                Scalar("prefabName"),
                Scalar("nameToken"),
                Scalar("localizedName"),
                Scalar("hoverNameToken"),
                Scalar("hoverNameLocalized"),
                Scalar("useTextToken"),
                Scalar("useTextLocalized"),
                Scalar("setsGlobalKey"),
                Scalar("setsPlayerKey"),
                Arr("locations", VegvisirLocationDef),
                Scalar("enabledInHierarchy"),
                Scalar("activeSelf"),
            });

        LocationChildrenDef = new DumpSchema("LocationChildrenDef", new[]
            {
                Scalar("hasLocationComponent"),
                Scalar("locationBiome"),
                Scalar("customInteriorTransformActive"),
                Scalar("useCustomInteriorTransform"),
                Scalar("interiorTransformPath"),
                Scalar("generatorTransformPath"),
                Scalar("instanceDependentPositionCount"),
            });

        LocationChildrenFile = new DumpSchema("LocationChildrenFile", new[]
            {
                Scalar("stamp"),
                Scalar("schema"),
                Scalar("count"),
                Scalar("loadedCount"),
                Scalar("failedCount"),
                Scalar("totalRandomSpawns"),
                Scalar("totalRandomObjects"),
                Scalar("totalContainers"),
                Scalar("maxNamesPerEntry"),
                Arr("locations", LocationChildrenDef),
            });

        Vec3Def = new DumpSchema("Vec3Def", new[]
            {
                F32("x"),
                F32("y"),
                F32("z"),
            });

        RandomSpawnDef = new DumpSchema("RandomSpawnDef", new[]
            {
                Scalar("index"),
                Scalar("path"),
                Scalar("name"),
                Scalar("normalizedName"),
                Scalar("normalizedHash"),
                Obj("localPosition", Vec3Def),
                Obj("prefabPosition", Vec3Def),
                Obj("dumpWorldPosition", Vec3Def),
                Scalar("underGeneratorTransform"),
                Scalar("underInteriorTransform"),
                Scalar("prefabPositionIsInstanceDependent"),
                F32("chanceToSpawn"),
                Scalar("requireBiome"),
                Scalar("minElevation"),
                Scalar("maxElevation"),
                Scalar("notInLava"),
                Scalar("dungeonRequireTheme"),
                Scalar("activeSelf"),
                Scalar("enabledInHierarchy"),
                Scalar("hasNetView"),
                Scalar("offObjectName"),
                Scalar("offObjectPath"),
                Scalar("offObjectActiveSelf"),
                Scalar("offObjectChildNameCount"),
                Scalar("offObjectChildNames"),
                Scalar("activatedNetViewCount"),
                Scalar("activatedNetViewNames"),
                Scalar("subtreeChildNameCount"),
                Scalar("subtreeChildNames"),
                Scalar("parentRandomSpawnIndex"),
                Scalar("underAnOffObject"),
            });

        DropDataDef = new DumpSchema("DropDataDef", new[]
            {
                Scalar("index"),
                Scalar("itemPrefabName"),
                Scalar("itemPrefabHash"),
                Scalar("normalizedName"),
                Scalar("normalizedHash"),
                Scalar("itemNameToken"),
                Scalar("itemLocalizedName"),
                Scalar("stackMin"),
                Scalar("stackMax"),
                F32("weight"),
                Scalar("dontScale"),
            });

        DropTableDef = new DumpSchema("DropTableDef", new[]
            {
                Scalar("dropMin"),
                Scalar("dropMax"),
                F32("dropChance"),
                Scalar("oneOfEach"),
                Scalar("dropCount"),
                F32("totalWeight"),
                Arr("drops", DropDataDef),
            });

        ContainerDef = new DumpSchema("ContainerDef", new[]
            {
                Scalar("path"),
                Scalar("name"),
                Scalar("hash"),
                Scalar("normalizedName"),
                Scalar("normalizedHash"),
                Scalar("containerName"),
                Scalar("width"),
                Scalar("height"),
                Scalar("autoDestroyEmpty"),
                Scalar("privacy"),
                Scalar("checkGuardStone"),
                Scalar("activeSelf"),
                Scalar("enabledInHierarchy"),
                Scalar("gatedByRandomSpawnIndex"),
                Scalar("gatedByRandomSpawnPath"),
                Scalar("underAnOffObject"),
                Obj("defaultItems", DropTableDef),
            });

        RandomObjectEntryDef = new DumpSchema("RandomObjectEntryDef", new[]
            {
                Scalar("index"),
                Scalar("name"),
                Scalar("hash"),
                Scalar("normalizedName"),
                Scalar("normalizedHash"),
                F32("weight"),
                Scalar("activeSelf"),
                Arr("containers", ContainerDef),
                Scalar("containerCount"),
                Scalar("scanError"),
                Scalar("nestedRandomObjectCount"),
            });

        RandomObjectDef = new DumpSchema("RandomObjectDef", new[]
            {
                Scalar("index"),
                Scalar("path"),
                Scalar("name"),
                Scalar("normalizedName"),
                Scalar("normalizedHash"),
                Obj("localPosition", Vec3Def),
                Obj("prefabPosition", Vec3Def),
                Obj("dumpWorldPosition", Vec3Def),
                Scalar("underGeneratorTransform"),
                Scalar("underInteriorTransform"),
                Scalar("prefabPositionIsInstanceDependent"),
                F32("totalWeight"),
                Arr("objects", RandomObjectEntryDef),
                Scalar("requireBiome"),
                Scalar("minElevation"),
                Scalar("maxElevation"),
                Scalar("notInLava"),
                Scalar("dungeonRequireTheme"),
                Scalar("activeSelf"),
                Scalar("enabledInHierarchy"),
                Scalar("hasNetView"),
                Scalar("subtreeChildNameCount"),
                Scalar("subtreeChildNames"),
                Scalar("parentRandomSpawnIndex"),
                Scalar("underAnOffObject"),
            });

        ChildNameDef = new DumpSchema("ChildNameDef", new[]
            {
                Scalar("name"),
                Scalar("hash"),
                Scalar("normalizedName"),
                Scalar("count"),
                Scalar("netViewCount"),
                Scalar("enabledCount"),
            });

        PrefabNameDef = new DumpSchema("PrefabNameDef", new[]
            {
                Scalar("name"),
                Scalar("hash"),
                Scalar("count"),
                Scalar("netViewCount"),
                Scalar("enabledCount"),
                Scalar("rawNameCount"),
                Scalar("rawNames"),
            });

        QuatDef = new DumpSchema("QuatDef", new[]
            {
                F32("x"),
                F32("y"),
                F32("z"),
                F32("w"),
            });

        LocationOccupantsDef = new DumpSchema("LocationOccupantsDef", new[]
            {
                Scalar("prefabName"),
                Scalar("occupantsCaptured"),
                Arr("characters", CharacterDef),
                Arr("traders", TraderDef),
                Arr("offeringBowls", OfferingBowlDef),
                Arr("runeStones", RuneStoneDef),
                Arr("teleports", TeleportDef),
                Scalar("waymarksCaptured"),
            });

        LocationOccupantsFile = new DumpSchema("LocationOccupantsFile", new[]
            {
                Scalar("stamp"),
                Scalar("schema"),
                Scalar("count"),
                Arr("locations", LocationOccupantsDef),
            });

        RoomOccupantsDef = new DumpSchema("RoomOccupantsDef", new[]
            {
                Scalar("prefabName"),
                Scalar("occupantsCaptured"),
                Scalar("roomTheme"),
                Scalar("roomDataTheme"),
                Scalar("roomListName"),
                Arr("offeringBowls", OfferingBowlDef),
                Arr("characters", CharacterDef),
            });

        RoomOccupantsFile = new DumpSchema("RoomOccupantsFile", new[]
            {
                Scalar("stamp"),
                Scalar("schema"),
                Scalar("count"),
                Scalar("skipped"),
                Arr("rooms", RoomOccupantsDef),
            });

        DoorDefDef = new DumpSchema("DoorDefDef", new[]
            {
                Scalar("index"),
                Scalar("connectionType"),
                F32("chance"),
                Scalar("prefabName"),
                Scalar("hasPrefab"),
            });

        DungeonGeneratorDef = new DumpSchema("DungeonGeneratorDef", new[]
            {
                Scalar("path"),
                Obj("localPosition", Vec3Def),
                Obj("localEulerAngles", Vec3Def),
                Obj("parentLocalPosition", Vec3Def),
                Scalar("hasNonZeroXZOffset"),
                Scalar("useCustomInteriorTransform"),
                Scalar("algorithm"),
                Scalar("minRooms"),
                Scalar("maxRooms"),
                Scalar("minRequiredRooms"),
                Scalar("requiredRoomNames"),
                Scalar("themes"),
                Scalar("addBaseSeedToRandomSpawn"),
                F32("campRadiusMin"),
                F32("campRadiusMax"),
                Scalar("fullFieldsCaptured"),
                F32("maxTilt"),
                F32("tileWidth"),
                Scalar("gridSize"),
                F32("spawnChance"),
                F32("minAltitude"),
                Scalar("perimeterSections"),
                F32("perimeterBuffer"),
                Scalar("alternativeFunctionality"),
                F32("doorChance"),
                Arr("doorTypes", DoorDefDef),
                Obj("zoneCenter", Vec3Def),
                Obj("zoneSize", Vec3Def),
                Obj("originalPosition", Vec3Def),
            });

        LocationPrefabDef = new DumpSchema("LocationPrefabDef", new[]
            {
                Scalar("prefabName"),
                Obj("assetId", AssetIdDef),
                Scalar("loaded"),
                Scalar("error"),
                Scalar("hasLocationComponent"),
                F32("exteriorRadius"),
                F32("interiorRadius"),
                Scalar("hasInterior"),
                Scalar("noBuild"),
                F32("noBuildRadiusOverride"),
                Scalar("clearArea"),
                Scalar("discoverLabel"),
                Scalar("useCustomInteriorTransform"),
                Scalar("biome"),
                Arr("generators", DungeonGeneratorDef),
            });

        LocationPrefabsFile = new DumpSchema("LocationPrefabsFile", new[]
            {
                Scalar("stamp"),
                Scalar("schema"),
                Scalar("count"),
                Scalar("loadedCount"),
                Scalar("failedCount"),
                Arr("prefabs", LocationPrefabDef),
            });

        RandomStateRoundTripDef = new DumpSchema("RandomStateRoundTripDef", new[]
            {
                Scalar("saved"),
                Scalar("afterInitState"),
                Scalar("afterRestore"),
                Scalar("controlDraws"),
                Scalar("restoredDraws"),
                Scalar("statesEqual"),
                Scalar("drawsEqual"),
            });

        RandomInitStateDef = new DumpSchema("RandomInitStateDef", new[]
            {
                Scalar("seed"),
                Scalar("state"),
            });

        RandomDrawDef = new DumpSchema("RandomDrawDef", new[]
            {
                Scalar("call"),
                Scalar("kind"),
                Scalar("resultInt"),
                F32("resultFloat"),
                F32("resultFloat2"),
                Scalar("stateAfter"),
                Scalar("stateUnchanged"),
            });

        RandomTraceDef = new DumpSchema("RandomTraceDef", new[]
            {
                Scalar("id"),
                Scalar("note"),
                Scalar("initState"),
                Scalar("stateAfterInit"),
                Arr("draws", RandomDrawDef),
                Scalar("stateAtEnd"),
            });

        NativesRandomFile = new DumpSchema("NativesRandomFile", new[]
            {
                Scalar("stamp"),
                Scalar("schema"),
                Scalar("unityVersion"),
                Obj("stateRoundTrip", RandomStateRoundTripDef),
                Arr("initStates", RandomInitStateDef),
                Arr("traces", RandomTraceDef),
            });

        PerlinBlockDef = new DumpSchema("PerlinBlockDef", new[]
            {
                Scalar("id"),
                Scalar("note"),
                Scalar("kind"),
                Scalar("sampleCount"),
                Scalar("byteOffset"),
            });

        NativesPerlinIndexFile = new DumpSchema("NativesPerlinIndexFile", new[]
            {
                Scalar("stamp"),
                Scalar("schema"),
                Scalar("binFile"),
                Scalar("binBytes"),
                Scalar("binSha256"),
                Arr("blocks", PerlinBlockDef),
            });

        LibmSampleDef = new DumpSchema("LibmSampleDef", new[]
            {
                Scalar("fn"),
                F64("a"),
                F64("b"),
                F64("result"),
            });

        WorldAngleSampleDef = new DumpSchema("WorldAngleSampleDef", new[]
            {
                F32("wx"),
                F32("wy"),
                F32("result"),
            });

        NativesLibmFile = new DumpSchema("NativesLibmFile", new[]
            {
                Scalar("stamp"),
                Scalar("schema"),
                Arr("samples", LibmSampleDef),
                Arr("worldAngle", WorldAngleSampleDef),
            });

        HalfSampleDef = new DumpSchema("HalfSampleDef", new[]
            {
                Scalar("note"),
                F32("value"),
                Scalar("half"),
                Scalar("halfHex"),
                F32("back"),
            });

        NativesHalfFile = new DumpSchema("NativesHalfFile", new[]
            {
                Scalar("stamp"),
                Scalar("schema"),
                Arr("samples", HalfSampleDef),
            });

        HashSampleDef = new DumpSchema("HashSampleDef", new[]
            {
                Scalar("s"),
                Scalar("hash"),
                Scalar("kind"),
            });

        NativesHashFile = new DumpSchema("NativesHashFile", new[]
            {
                Scalar("stamp"),
                Scalar("schema"),
                Arr("samples", HashSampleDef),
            });

        LocationListDef = new DumpSchema("LocationListDef", new[]
            {
                Scalar("name"),
                Scalar("sortOrder"),
                Scalar("locationCount"),
                Scalar("vegetationCount"),
                Scalar("firstLocationIndex"),
                Scalar("firstVegetationIndex"),
            });

        ZoneSystemConstantsDef = new DumpSchema("ZoneSystemConstantsDef", new[]
            {
                Scalar("locationVersion"),
                F32("waterLevel"),
                F32("zoneSize"),
                F32("zoneTTL"),
                F32("zoneTTS"),
                Scalar("locationScenes"),
                Arr("locationLists", LocationListDef),
                Scalar("sortOrderTies"),
                Scalar("altBiomeListNames"),
                Scalar("assetMemoryUsagePolicy"),
                Scalar("zoneCtrlPrefabPresent"),
                Scalar("locationProxyPrefabPresent"),
            });

        ColorDef = new DumpSchema("ColorDef", new[]
            {
                F32("r"),
                F32("g"),
                F32("b"),
                F32("a"),
                Scalar("rgba32"),
            });

        MinimapConstantsDef = new DumpSchema("MinimapConstantsDef", new[]
            {
                Scalar("textureSize"),
                F32("pixelSize"),
                F32("exploreRadius"),
                F32("exploreInterval"),
                F32("removeRadius"),
                Obj("meadowsColor", ColorDef),
                Obj("ashlandsColor", ColorDef),
                Obj("blackforestColor", ColorDef),
                Obj("deepnorthColor", ColorDef),
                Obj("heathColor", ColorDef),
                Obj("swampColor", ColorDef),
                Obj("mountainColor", ColorDef),
                Obj("mistlandsColor", ColorDef),
                Obj("oceanColorHardcoded", ColorDef),
                Scalar("locationIcons"),
            });

        HeightmapConstantsDef = new DumpSchema("HeightmapConstantsDef", new[]
            {
                Scalar("zoneWidth"),
                F32("zoneScale"),
                Scalar("zoneIsDistantLod"),
                Scalar("distantLodWidth"),
                F32("distantLodScale"),
                Scalar("distantLodFound"),
            });

        PrefabConstantsFile = new DumpSchema("PrefabConstantsFile", new[]
            {
                Scalar("stamp"),
                Scalar("schema"),
                Obj("zoneSystem", ZoneSystemConstantsDef),
                Obj("minimap", MinimapConstantsDef),
                Obj("heightmap", HeightmapConstantsDef),
            });

        RoomListDef = new DumpSchema("RoomListDef", new[]
            {
                Scalar("index"),
                Scalar("name"),
                Scalar("normalizedName"),
                Scalar("roomCount"),
                Scalar("themeMask"),
            });

        Vec3IntDef = new DumpSchema("Vec3IntDef", new[]
            {
                Scalar("x"),
                Scalar("y"),
                Scalar("z"),
            });

        RoomConnectionDef = new DumpSchema("RoomConnectionDef", new[]
            {
                Scalar("index"),
                Scalar("path"),
                Scalar("name"),
                Obj("localPosition", Vec3Def),
                Obj("localRotation", QuatDef),
                Obj("roomPosition", Vec3Def),
                Obj("roomRotation", QuatDef),
                Scalar("directChildOfRoom"),
                Scalar("type"),
                Scalar("entrance"),
                Scalar("allowDoor"),
                Scalar("doorOnlyIfOtherAlsoAllowsDoor"),
                Scalar("activeSelf"),
            });

        RoomChildrenDef = new DumpSchema("RoomChildrenDef", new[]
            {
                Scalar("index"),
                Scalar("hash"),
                Scalar("duplicateOfIndex"),
                Scalar("roomListIndex"),
                Scalar("roomListName"),
                Scalar("roomDataTheme"),
                Scalar("roomDataEnabled"),
                Scalar("hasRoomComponent"),
                Scalar("roomComponentOnRoot"),
                Scalar("roomComponentPath"),
                Obj("size", Vec3IntDef),
                Scalar("roomTheme"),
                Scalar("roomEnabled"),
                Scalar("entrance"),
                Scalar("endCap"),
                Scalar("divider"),
                Scalar("endCapPrio"),
                Scalar("minPlaceOrder"),
                F32("weight"),
                Scalar("faceCenter"),
                Scalar("perimeter"),
                Scalar("placeOrder"),
                Scalar("seed"),
                Scalar("connectionsCaptured"),
                Arr("connections", RoomConnectionDef),
            });

        RoomChildrenFile = new DumpSchema("RoomChildrenFile", new[]
            {
                Scalar("stamp"),
                Scalar("schema"),
                Scalar("count"),
                Scalar("loadedCount"),
                Scalar("failedCount"),
                Scalar("enabledCount"),
                Scalar("totalRandomSpawns"),
                Scalar("totalRandomObjects"),
                Scalar("totalContainers"),
                Scalar("maxNamesPerEntry"),
                Scalar("roomListPrefabNames"),
                Scalar("roomScenes"),
                Arr("roomLists", RoomListDef),
                Scalar("roomByHashCount"),
                Scalar("skipped"),
                Arr("rooms", RoomChildrenDef),
            });

        SearchHitDef = new DumpSchema("SearchHitDef", new[]
            {
                Scalar("kind"),
                Scalar("hostName"),
                Scalar("hostRoomList"),
                Scalar("hostTheme"),
                Scalar("hostEnabled"),
                Scalar("path"),
                Scalar("rawName"),
                Scalar("hasNetView"),
                Scalar("enabledInHierarchy"),
                Scalar("underAnOffObject"),
                Scalar("gatedByRandomSpawnIndex"),
                Scalar("gatedByRandomSpawnPath"),
                F32("chanceToSpawn"),
                Scalar("requireBiome"),
                Scalar("minElevation"),
                Scalar("maxElevation"),
                Scalar("notInLava"),
                Scalar("dungeonRequireTheme"),
                Scalar("gatePositionIsInstanceDependent"),
                Scalar("randomObjectIndex"),
                Scalar("randomObjectEntryIndex"),
                F32("randomObjectEntryWeight"),
                F32("randomObjectTotalWeight"),
                Scalar("note"),
            });

        SearchCoverageDef = new DumpSchema("SearchCoverageDef", new[]
            {
                Scalar("searched"),
                Scalar("notSearched"),
                Scalar("standingLimitations"),
                Scalar("locationPrefabsWalked"),
                Scalar("locationPrefabsLoaded"),
                Scalar("locationPrefabsFailed"),
                Scalar("roomPrefabsWalked"),
                Scalar("roomPrefabsLoaded"),
                Scalar("roomPrefabsFailed"),
                Scalar("interiorWalksWithErrors"),
                Scalar("randomObjectOptionsResolved"),
                Scalar("randomObjectOptionContainersRead"),
                Scalar("randomObjectOptionScanErrors"),
                Scalar("nestedRandomObjectsInOptions"),
                Scalar("locationTableEntries"),
                Scalar("vegetationTableEntries"),
                Scalar("locationChildWalkRan"),
                Scalar("roomWalkRan"),
                Scalar("roomWalkSkipped"),
            });

        SearchTermDef = new DumpSchema("SearchTermDef", new[]
            {
                Scalar("requested"),
                Scalar("normalized"),
                Scalar("hash"),
                Scalar("found"),
                Scalar("inconclusive"),
                Scalar("verdict"),
                Scalar("existsInZNetScene"),
                Scalar("zNetSceneChecked"),
                Scalar("hitCount"),
                Arr("hits", SearchHitDef),
                Obj("coverage", SearchCoverageDef),
            });

        SearchFile = new DumpSchema("SearchFile", new[]
            {
                Scalar("stamp"),
                Scalar("schema"),
                Scalar("requestedNames"),
                Arr("terms", SearchTermDef),
                Scalar("verdictLines"),
            });

        SeedInputFieldDef = new DumpSchema("SeedInputFieldDef", new[]
            {
                Scalar("fieldName"),
                Scalar("componentType"),
                Scalar("characterLimit"),
                Scalar("characterValidation"),
                Scalar("contentType"),
                Scalar("inputType"),
                Scalar("lineType"),
                Scalar("readOnly"),
                Scalar("richText"),
                Scalar("currentText"),
                Scalar("currentTextLength"),
                Scalar("unreadProperties"),
            });

        SeedInputFile = new DumpSchema("SeedInputFile", new[]
            {
                Scalar("stamp"),
                Scalar("schema"),
                Obj("seedField", SeedInputFieldDef),
                Obj("nameField", SeedInputFieldDef),
                Scalar("notes"),
            });

        LocationTableFile = new DumpSchema("LocationTableFile", new[]
            {
                Scalar("stamp"),
                Scalar("schema"),
                Scalar("count"),
                Scalar("enabledCount"),
                Scalar("duplicateHashPrefabNames"),
                Arr("locations", LocationDef),
            });

        VegetationTableFile = new DumpSchema("VegetationTableFile", new[]
            {
                Scalar("stamp"),
                Scalar("schema"),
                Scalar("count"),
                Scalar("enabledCount"),
                Arr("vegetation", VegetationDef),
            });

        AltBiomeTableFile = new DumpSchema("AltBiomeTableFile", new[]
            {
                Scalar("stamp"),
                Scalar("schema"),
                Scalar("count"),
                Scalar("enabledCount"),
                Arr("altBiomes", AltBiomeDef),
            });

        VersionConstantsFile = new DumpSchema("VersionConstantsFile", new[]
            {
                Scalar("stamp"),
                Scalar("schema"),
                Scalar("gameVersion"),
                Scalar("networkVersion"),
                Scalar("worldVersion"),
                Scalar("worldGenVersion"),
                Scalar("cachedMinimapVersion"),
                Scalar("playerVersion"),
                Scalar("cachedMinimapWrittenLiteral"),
                Scalar("biomeGridTextureSize"),
                F32("biomeGridPixelSize"),
                Scalar("biomeGridHalfWidth"),
                F32("biomeGridHalfPixel"),
            });

        RiverDef = new DumpSchema("RiverDef", new[]
            {
                Obj("p0", Vec2Def),
                Obj("p1", Vec2Def),
                Obj("center", Vec2Def),
                F32("widthMin"),
                F32("widthMax"),
                F32("curveWidth"),
                F32("curveWavelength"),
            });

        WorldGenDumpFile = new DumpSchema("WorldGenDumpFile", new[]
            {
                Scalar("stamp"),
                Scalar("schema"),
                Scalar("worldName"),
                Scalar("seedText"),
                Scalar("seed"),
                Scalar("worldGenVersion"),
                Scalar("menu"),
                Scalar("version"),
                F32("offset0"),
                F32("offset1"),
                F32("offset2"),
                F32("offset3"),
                F32("offset4"),
                Scalar("riverSeed"),
                Scalar("streamSeed"),
                Scalar("noiseGenSeed"),
                F32("minMountainDistance"),
                F32("minDarklandNoise"),
                F32("maxMarshDistance"),
                Obj("constructorTrace", RandomTraceDef),
                Arr("lakes", Vec2Def),
                Arr("rivers", RiverDef),
                Arr("streams", RiverDef),
                Scalar("riverPointCellCount"),
                Scalar("riverPointTotal"),
                Scalar("riverPointsFile"),
                Scalar("grids"),
            });

        LocationInstanceDef = new DumpSchema("LocationInstanceDef", new[]
            {
                Scalar("zoneX"),
                Scalar("zoneY"),
                Scalar("prefabName"),
                Scalar("hash"),
                F32("x"),
                F32("y"),
                F32("z"),
                Scalar("placed"),
            });

        LocationInstancesFile = new DumpSchema("LocationInstancesFile", new[]
            {
                Scalar("stamp"),
                Scalar("schema"),
                Scalar("worldName"),
                Scalar("seed"),
                Scalar("locationVersion"),
                Scalar("count"),
                Scalar("orderedPrefabNames"),
                Arr("instances", LocationInstanceDef),
            });

        AltBiomeAssignmentFile = new DumpSchema("AltBiomeAssignmentFile", new[]
            {
                Scalar("stamp"),
                Scalar("schema"),
                Scalar("worldName"),
                Scalar("seed"),
                Scalar("openingInitState"),
                Arr("biomeKeys", BiomeKeyDef),
                Arr("sectors", SectorDef),
                Arr("assignments", AltBiomeAssignmentDef),
                Scalar("contaminated"),
            });
        }
    }
}
