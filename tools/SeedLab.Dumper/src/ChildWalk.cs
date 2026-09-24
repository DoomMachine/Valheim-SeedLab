using System;
using System.Collections.Generic;
using System.Globalization;
using SeedLab.Contracts.Dump;
using UnityEngine;

namespace SeedLab.Dumper
{
    /// <summary>
    /// Everything one interior walk needs to know, gathered so the per-entry readers take one argument
    /// instead of eight. Created per prefab, thrown away with it; it holds no state between prefabs.
    /// </summary>
    internal sealed class WalkContext
    {
        /// <summary>The asset's root transform. Paths and the <c>IsEnabledInheirarcy</c> tests are
        /// relative to this.</summary>
        public Transform Root;

        public GameObject RootGo;

        /// <summary>The transform the SPAWN ROUTINE measures child positions from: the asset root for a
        /// location (<c>SpawnLocation</c> zeroes it and reads <c>child.position</c>), the <c>Room</c>
        /// component's transform for a room (<c>PlaceRoom</c> computes
        /// <c>Inverse(room.rotation) * (child.position - room.position)</c>). Normally the same object.</summary>
        public Vector3 AnchorPos;

        public Quaternion InvAnchorRot;

        public readonly Dictionary<Transform, int> SpawnIndex = new Dictionary<Transform, int>();
        public readonly HashSet<Transform> OffObjects = new HashSet<Transform>();
        public List<RandomSpawnDef> SpawnDefs;
        public List<string> Warnings;

        /// <summary>Null when nothing is being looked for.</summary>
        public SearchIndex Search;

        public string HostKind;      // "location" | "room"
        public string HostName;
        public string HostRoomList;  // rooms only
        public int HostTheme;        // rooms only
        public bool HostEnabled;     // rooms only

        /// <summary><c>Location.m_generator.transform</c>, but ONLY when
        /// <c>SpawnLocation</c>'s <c>flag</c> would be true. Null otherwise, including for every
        /// room.</summary>
        public Transform GeneratorTransform;

        /// <summary><c>Location.m_interiorTransform</c>, on the same condition.</summary>
        public Transform InteriorTransform;

        /// <summary>How many entries were flagged
        /// <c>prefabPositionIsInstanceDependent</c>.</summary>
        public int InstanceDependentPositions;
    }

    /// <summary>
    /// The prefab-child walk: what is INSIDE a prefab that spends a seeded RNG stream on its own
    /// children. Two callers, one body:
    /// <list type="bullet">
    /// <item><see cref="ModeAssets"/> walks the LOCATION prefabs, from the same
    /// <c>SoftReference.Load()</c> that fills <c>locationprefabs.json</c>;</item>
    /// <item><see cref="RoomWalk"/> walks the dungeon/camp ROOM prefabs out of <c>DungeonDB</c>.</item>
    /// </list>
    ///
    /// <b>Everything here is a pure read.</b> The objects being inspected are SHARED PREFAB ASSETS that
    /// <c>ZoneSystem.SpawnLocation</c> and <c>DungeonGenerator.PlaceRoom</c> use for every instance in
    /// the world, so a single <c>SetActive</c> or a single <c>Prepare()</c> would change how the game
    /// spawns them for the rest of the session. In particular:
    /// <list type="bullet">
    /// <item><c>RandomSpawn.Prepare()</c> is NOT called - its <c>m_childNetViews</c> query is
    /// reproduced read-only in <see cref="ActivatedNetViewNames"/>;</item>
    /// <item>the asset's root transform is NOT zeroed the way <c>SpawnLocation</c> zeroes it - the
    /// position the game would read is computed instead, in <see cref="PrefabPosition"/>;</item>
    /// <item>nothing calls <c>Randomize</c>, which would both draw from <c>UnityEngine.Random</c> and
    /// write <c>Location.m_biome</c> on the shared asset;</item>
    /// <item><c>DungeonDB.RoomData.RoomInPrefab</c> is NOT used - it caches into the private
    /// <c>m_loadedRoom</c> field and logs an error when the asset is not resident. The walk calls
    /// <c>asset.GetComponent&lt;Room&gt;()</c> on the asset it loaded itself.</item>
    /// </list>
    ///
    /// <b>Order is the data.</b> The <c>RandomSpawn</c> and <c>RandomObject</c> arrays come from the
    /// very same <c>Utils.GetEnabledComponentsInChildren&lt;T&gt;(asset)</c> call the game makes, so
    /// their order is identical to the game's by construction, not by assumption. Entry N consumes
    /// draw N of the prefab's seeded stream.
    ///
    /// <b>Names are normalised the game's way.</b> Every index and every search matches on
    /// <c>Utils.GetPrefabName(name)</c> - truncated at the first <c>'('</c> or <c>' '</c> - because that
    /// is the game's own identity for a GameObject, and a child authored as <c>piece_maypole (1)</c>
    /// would otherwise be a false negative on an exact-match query. The raw spelling is kept beside it
    /// everywhere, so the normalisation hides nothing. See <see cref="Names"/>.
    ///
    /// <b>Nothing here throws.</b> Both entry points wrap the whole walk, and both leave every array
    /// non-null and set <c>error</c> on the entry when something failed - with <c>loaded</c> still
    /// reporting whether the ASSET loaded, so this file and <c>locationprefabs.json</c> can never
    /// disagree about that. Every section inside is additionally wrapped on its own, so one malformed
    /// subtree costs only itself.
    /// </summary>
    internal static class ChildWalk
    {
        /// <summary>
        /// How many distinct names a single RandomSpawn / RandomObject entry may list for its subtree
        /// and its off-object, and how many raw spellings one normalised name may list. Those are the
        /// one part of this file with no natural bound - a RandomSpawn gating a whole building repeats
        /// that building's names, once per RandomSpawn, across 186 prefabs - and the writer builds the
        /// whole JSON document in memory before it writes it, so an unbounded worst case is an
        /// allocation spike inside a frame of the user's game.
        ///
        /// Every capped list has a sibling <c>...Count</c> carrying the true number, so truncation is
        /// visible and never silent. Nothing load-bearing is capped: the whole-prefab
        /// <c>childNames</c> / <c>prefabNames</c> indices and each entry's
        /// <c>activatedNetViewNames</c> are complete.
        /// </summary>
        public const int MaxNamesPerEntry = 64;

        // ---- entry point: a LOCATION prefab ----------------------------------------------------------

        /// <summary>
        /// Reads one loaded location prefab. <paramref name="asset"/> is what
        /// <c>SoftReference&lt;GameObject&gt;.Asset</c> returned; <paramref name="location"/> is the
        /// ROOT <c>Location</c> component <see cref="ModeAssets"/> already found, or null - the root one
        /// specifically, because that is the object <c>SpawnLocation</c> hands to every
        /// <c>Randomize</c>.
        /// </summary>
        public static LocationChildrenDef Read(GameObject asset, Location location,
                                               string prefabName, AssetIdDef assetId,
                                               SearchIndex search)
        {
            var d = new LocationChildrenDef
            {
                prefabName = prefabName,
                assetId = assetId,
                loaded = true,
                hasLocationComponent = location != null,
            };
            Blank(d);
            BlankNames(d);

            var warnings = new List<string>();
            try
            {
                d.locationBiome = location != null ? (int)location.m_biome : 0;

                Transform root = asset.transform;
                var ctx = new WalkContext
                {
                    Root = root,
                    RootGo = root.gameObject,
                    Warnings = warnings,
                    Search = search,
                    HostKind = "location",
                    HostName = prefabName,
                    // Only enabled locations are walked (WalkPrefabs filters on m_enable), so a
                    // location hit's hostEnabled is true by construction. Left false it would read as
                    // "this location is disabled", which is the opposite of the truth.
                    HostEnabled = true,
                };

                // ---- the custom interior transform, before anything reads a position ----------------
                // SpawnLocation's own `flag`: all three conditions, exactly as the game spells them.
                if (location != null)
                {
                    d.useCustomInteriorTransform = location.m_useCustomInteriorTransform;
                    Transform interior = location.m_interiorTransform;
                    DungeonGenerator gen = location.m_generator;
                    Transform genT = gen != null ? gen.transform : null;

                    d.interiorTransformPath = interior != null ? PathOf(root, interior) : null;
                    d.generatorTransformPath = genT != null ? PathOf(root, genT) : null;

                    d.customInteriorTransformActive =
                        location.m_useCustomInteriorTransform && interior != null && genT != null;

                    if (d.customInteriorTransformActive)
                    {
                        ctx.InteriorTransform = interior;
                        ctx.GeneratorTransform = genT;
                    }
                    else if (location.m_useCustomInteriorTransform)
                    {
                        warnings.Add("Location.m_useCustomInteriorTransform is set but " +
                                     (interior == null ? "m_interiorTransform" : "m_generator") +
                                     " is null, so SpawnLocation's `flag` is FALSE and the pre-Randomize " +
                                     "transform mutation does not happen. Every prefabPosition in this " +
                                     "entry is the value the game reads.");
                    }
                }

                SetAnchor(ctx, root);
                d.rootPosition = Snapshot.Vec3(root.position);
                d.rootRotation = Quat(root.rotation);
                d.rootLocalScale = Snapshot.Vec3(root.localScale);
                d.rootAtIdentity = AtIdentity(root.position, root.rotation);

                ReadInterior(d, asset, ctx);

                d.instanceDependentPositionCount = ctx.InstanceDependentPositions;
                if (ctx.InstanceDependentPositions > 0)
                {
                    warnings.Add(ctx.InstanceDependentPositions + " entr(ies) in this prefab sit under " +
                                 "Location.m_generator or Location.m_interiorTransform, and this prefab " +
                                 "has m_useCustomInteriorTransform ACTIVE. SpawnLocation moves both of " +
                                 "those transforms between Random.InitState and the Randomize loop - the " +
                                 "generator to Vector3.zero, the interior to a position AND rotation " +
                                 "derived from the zone centre, the instance position and the instance " +
                                 "rotation. Their 'prefabPosition' here is therefore NOT the value the " +
                                 "game reads, and for the interior one no dump can state it: it is " +
                                 "instance-dependent. Draw order and draw count are unaffected; only " +
                                 "the elevation and lava gates of those entries are unknowable offline. " +
                                 "Each affected entry carries prefabPositionIsInstanceDependent.");
                }
            }
            catch (Exception e)
            {
                // The asset DID load - locationprefabs.json says so and so does this entry. What failed
                // is the walk, and that is what `error` reports. Conflating the two is how the two files
                // came to disagree before 2026-09-23.
                d.error = "the child walk failed: " + Short(e);
                warnings.Add("This entry is INCOMPLETE: the walk threw before it finished. Whatever is " +
                             "missing from it is missing because of that, not because the prefab does " +
                             "not contain it.");
                Discard(d);
            }

            d.warnings = warnings.ToArray();
            return d;
        }

        // ---- entry point: a ROOM prefab --------------------------------------------------------------

        /// <summary>
        /// Reads one loaded dungeon/camp room prefab. <paramref name="room"/> is
        /// <c>asset.GetComponent&lt;Room&gt;()</c> - read off the asset the caller loaded, never through
        /// <c>RoomData.RoomInPrefab</c>, which caches into the RoomData's private field.
        ///
        /// The anchor is the <c>Room</c> component's transform, because that is what
        /// <c>DungeonGenerator.PlaceRoom</c> measures from. When the component sits on the asset root -
        /// which it normally does - the two are the same and <c>prefabPosition</c> means exactly what it
        /// means for a location.
        /// </summary>
        public static RoomChildrenDef ReadRoom(GameObject asset, Room room, string prefabName,
                                               AssetIdDef assetId, SearchIndex search,
                                               string roomListName, int roomDataTheme, bool roomDataEnabled)
        {
            var d = new RoomChildrenDef
            {
                prefabName = prefabName,
                assetId = assetId,
                loaded = true,
                hasRoomComponent = room != null,
                roomListName = roomListName,
                roomDataTheme = roomDataTheme,
                roomDataEnabled = roomDataEnabled,
                roomListIndex = -1,
                duplicateOfIndex = -1,
            };
            BlankRoom(d);
            BlankNames(d);

            var warnings = new List<string>();
            try
            {
                Transform root = asset.transform;
                Transform anchor = room != null ? room.transform : root;
                d.roomComponentOnRoot = room == null || room.transform == root;
                d.roomComponentPath = (room != null && room.transform != root)
                    ? PathOf(root, room.transform) : null;

                if (room == null)
                {
                    warnings.Add("This room prefab has no Room component. DungeonGenerator.PlaceRoom " +
                                 "dereferences asset.GetComponent<Room>() without a null check, so the " +
                                 "game would throw if it ever picked this room. Positions below are " +
                                 "anchored on the asset root instead.");
                }
                else if (!d.roomComponentOnRoot)
                {
                    warnings.Add("The Room component is not on the asset root (" + d.roomComponentPath +
                                 "). PlaceRoom measures every child from the ROOM's transform, so " +
                                 "prefabPosition here is anchored there and not on the root.");
                }

                var ctx = new WalkContext
                {
                    Root = root,
                    RootGo = root.gameObject,
                    Warnings = warnings,
                    Search = search,
                    HostKind = "room",
                    HostName = prefabName,
                    HostRoomList = roomListName,
                    HostTheme = roomDataTheme,
                    HostEnabled = roomDataEnabled,
                    // A room is never subject to the custom interior transform: PlaceRoom's own
                    // m_useCustomInteriorTransform changes the room's SEED, not any transform on the
                    // shared asset.
                    GeneratorTransform = null,
                    InteriorTransform = null,
                };
                SetAnchor(ctx, anchor);

                d.rootPosition = Snapshot.Vec3(anchor.position);
                d.rootRotation = Quat(anchor.rotation);
                d.rootLocalScale = Snapshot.Vec3(root.localScale);
                d.rootAtIdentity = AtIdentity(anchor.position, anchor.rotation);

                if (room != null)
                {
                    d.size = new Vec3IntDef { x = room.m_size.x, y = room.m_size.y, z = room.m_size.z };
                    d.roomTheme = (int)room.m_theme;
                    d.roomEnabled = room.m_enabled;
                    d.entrance = room.m_entrance;
                    d.endCap = room.m_endCap;
                    d.divider = room.m_divider;
                    d.endCapPrio = room.m_endCapPrio;
                    d.minPlaceOrder = room.m_minPlaceOrder;
                    d.weight = room.m_weight;
                    d.faceCenter = room.m_faceCenter;
                    d.perimeter = room.m_perimeter;
                    d.placeOrder = room.m_placeOrder;
                    d.seed = room.m_seed;

                    // SetupAvailableRooms filters on RoomData, not on the component. A disagreement is
                    // harmless to the game and confusing to a reader, so it is stated rather than hidden.
                    if ((int)room.m_theme != roomDataTheme)
                    {
                        warnings.Add("Room.m_theme (" + (int)room.m_theme + ") differs from " +
                                     "RoomData.m_theme (" + roomDataTheme + "). DungeonGenerator." +
                                     "SetupAvailableRooms filters on the RoomData one, so that is the " +
                                     "value that decides which dungeon can contain this room.");
                    }
                    if (room.m_enabled != roomDataEnabled)
                    {
                        warnings.Add("Room.m_enabled (" + room.m_enabled + ") differs from " +
                                     "RoomData.m_enabled (" + roomDataEnabled + "). The generator " +
                                     "filters on the RoomData one.");
                    }

                    ReadConnections(d, room, root, warnings);
                }

                ReadInterior(d, asset, ctx);
            }
            catch (Exception e)
            {
                d.error = "the room child walk failed: " + Short(e);
                warnings.Add("This entry is INCOMPLETE: the walk threw before it finished.");
                Discard(d);
                // Same rule as Discard's: a half-built entry must not be readable as a complete one.
                // Connections read before the throw are dropped rather than left beside an error.
                d.connectionsCaptured = false;
                d.connections = Empty<RoomConnectionDef>();
            }

            d.warnings = warnings.ToArray();
            return d;
        }

        /// <summary>A prefab whose walk produced nothing. Keeps the arrays aligned with the sibling
        /// file instead of silently dropping the entry. <paramref name="loaded"/> is whether the ASSET
        /// loaded - pass the caller's own answer, so the two files agree.</summary>
        public static LocationChildrenDef Failed(string prefabName, AssetIdDef assetId, string error,
                                                 bool loaded)
        {
            var d = new LocationChildrenDef
            {
                prefabName = prefabName,
                assetId = assetId,
                loaded = loaded,
                error = error,
            };
            Blank(d);
            BlankNames(d);
            d.warnings = new string[0];
            return d;
        }

        /// <summary>The room-side equivalent of <see cref="Failed"/>.</summary>
        public static RoomChildrenDef FailedRoom(string prefabName, AssetIdDef assetId, string error,
                                                 bool loaded)
        {
            var d = new RoomChildrenDef
            {
                prefabName = prefabName,
                assetId = assetId,
                loaded = loaded,
                error = error,
                roomListIndex = -1,
                duplicateOfIndex = -1,
            };
            BlankRoom(d);
            BlankNames(d);
            d.warnings = new string[0];
            return d;
        }

        /// <summary>
        /// <see cref="Blank"/> plus the room-only members. It exists because <see cref="Blank"/> is
        /// typed on <see cref="InteriorDef"/> and <c>connections</c> is declared on
        /// <see cref="RoomChildrenDef"/>, so <see cref="Blank"/> structurally CANNOT reach it - and a
        /// <c>connections</c> left null would be written as JSON <c>null</c> beside every other array
        /// written as <c>[]</c>, which is the one shape this dump's own convention promises never to
        /// emit.
        /// </summary>
        private static void BlankRoom(RoomChildrenDef d)
        {
            Blank(d);
            d.connectionsCaptured = false;
            d.connections = Empty<RoomConnectionDef>();
        }

        /// <summary>Every array on the DTO set to an empty array, never null. Called before the walk, so
        /// a throw at any point still leaves a document a reader can parse.</summary>
        private static void Blank(InteriorDef d)
        {
            d.randomSpawns = Empty<RandomSpawnDef>();
            d.randomObjects = Empty<RandomObjectDef>();
            d.containers = Empty<ContainerDef>();
            d.childNames = Empty<ChildNameDef>();
            d.prefabNames = Empty<PrefabNameDef>();
            if (d.warnings == null) d.warnings = Empty<string>();
        }

        /// <summary>
        /// The name-bearing arrays emptied AND their capture flags cleared, together - for an entry
        /// that has captured nothing yet: the start of <see cref="Read"/> and <see cref="ReadRoom"/>,
        /// and <see cref="Failed"/> / <see cref="FailedRoom"/>. Before 2026-09-24 an entry whose asset
        /// never loaded wrote these arrays as JSON null, the shape this dump promises never to emit.
        ///
        /// <para><b>Deliberately NOT part of <see cref="Blank"/>.</b> <see cref="Discard"/> calls
        /// <see cref="Blank"/> after a throw LATER in the walk, when <see cref="ReadOccupants"/> and
        /// <see cref="ReadWaymarks"/> have already finished and set their flags. Emptying the arrays
        /// there left <c>occupantsCaptured: true</c> beside an empty <c>offeringBowls</c> - "captured,
        /// and this altar has no boss" - which is exactly the wrong answer the flag exists to prevent
        /// (caught in review before the run). A flag and the array it vouches for are reset in one
        /// place or not at all.</para>
        /// </summary>
        private static void BlankNames(InteriorDef d)
        {
            d.occupantsCaptured = false;
            d.characters = Empty<CharacterDef>();
            d.traders = Empty<TraderDef>();
            d.offeringBowls = Empty<OfferingBowlDef>();
            d.runeStones = Empty<RuneStoneDef>();
            d.waymarksCaptured = false;
            d.teleports = Empty<TeleportDef>();
            d.vegvisirs = Empty<VegvisirDef>();
        }

        /// <summary>After a throw: empty every array AND zero every count, so a half-built entry cannot
        /// be read as a complete one. A count of 8 beside an array of 3 is exactly the kind of quiet
        /// inconsistency that gets believed. <c>error</c> and a warning say what happened; the numbers
        /// say nothing at all, which is the honest thing for them to say.</summary>
        private static void Discard(InteriorDef d)
        {
            Blank(d);
            d.randomSpawnCount = 0;
            d.randomSpawnCountAll = 0;
            d.randomObjectCount = 0;
            d.randomObjectCountAll = 0;
            d.seededDrawCount = 0;
            d.netViewCount = 0;
            d.containerCount = 0;
            d.childTransformCount = 0;
        }

        private static void SetAnchor(WalkContext ctx, Transform anchor)
        {
            ctx.AnchorPos = anchor.position;
            ctx.InvAnchorRot = Quaternion.Inverse(anchor.rotation);
        }

        private static bool AtIdentity(Vector3 p, Quaternion q)
        {
            return p.x == 0f && p.y == 0f && p.z == 0f
                && q.x == 0f && q.y == 0f && q.z == 0f && q.w == 1f;
        }

        // ---- the shared body --------------------------------------------------------------------------

        private static void ReadInterior(InteriorDef d, GameObject asset, WalkContext ctx)
        {
            List<string> warnings = ctx.Warnings;

            ReadOccupants(d, asset, ctx);
            ReadWaymarks(d, asset, ctx);

            // ---- the two ordered arrays, from the game's own query -------------------------------
            RandomSpawn[] spawns = Empty<RandomSpawn>();
            RandomObject[] objects = Empty<RandomObject>();
            try
            {
                spawns = Utils.GetEnabledComponentsInChildren<RandomSpawn>(asset) ?? Empty<RandomSpawn>();
                objects = Utils.GetEnabledComponentsInChildren<RandomObject>(asset) ?? Empty<RandomObject>();
                d.netViewCount = Length(Utils.GetEnabledComponentsInChildren<ZNetView>(asset));
            }
            catch (Exception e)
            {
                warnings.Add("Utils.GetEnabledComponentsInChildren failed (" + Short(e) +
                             "); the RandomSpawn/RandomObject arrays in this entry are NOT the " +
                             "game's arrays and must not be used to replay the stream.");
                // Also an 'error', not only a warning: without this the entry would read as a
                // perfectly good prefab with zero RandomSpawns, i.e. "this prefab has no random
                // children" - the most dangerous wrong answer this file can give.
                d.error = "the game's component query failed: " + Short(e);
            }

            d.randomSpawnCount = spawns.Length;
            d.randomObjectCount = objects.Length;
            d.seededDrawCount = spawns.Length + objects.Length;
            d.randomSpawnCountAll = CountAll<RandomSpawn>(asset, ctx.Root, warnings);
            d.randomObjectCountAll = CountAll<RandomObject>(asset, ctx.Root, warnings);

            // Like with like. Utils.GetEnabledComponentsInChildren does TWO things: it drops anything
            // whose activeSelf chain to the root is broken, and it drops a component mounted on the ROOT
            // TRANSFORM itself (`componentsInChildren[i].transform == root.transform`, verified from IL
            // 2026-09-23). CountAll now applies the same root exclusion, so the only thing left for this
            // comparison to detect is the first of those - which is the thing worth warning about.
            //
            // Before that fix a perfectly clean prefab with a root-mounted RandomSpawn was reported as
            // session-drifted and the user was told to distrust a good dump. A warning that fires on
            // healthy data is worse than no warning: it trains the reader to skip warnings.
            if (d.randomSpawnCountAll != d.randomSpawnCount)
            {
                warnings.Add("RandomSpawn: " + d.randomSpawnCount + " enabled of " + d.randomSpawnCountAll +
                             " present (both counts exclude a component on the root transform, which " +
                             "the game's query also excludes). The disabled ones sit under a " +
                             "switched-off object - most likely another RandomSpawn's m_OffObject, " +
                             "which the spawn routine's Reset() leaves inactive on the shared asset " +
                             "after the first spawn of this session. The game saw the same array at " +
                             "that moment; a dump taken in a freshly created world, before anything " +
                             "spawned, sees the authored one.");
            }
            if (d.randomObjectCountAll != d.randomObjectCount)
            {
                warnings.Add("RandomObject: " + d.randomObjectCount + " enabled of " +
                             d.randomObjectCountAll + " present. See the RandomSpawn note.");
            }

            // ---- the maps the per-entry linkage needs ---------------------------------------------
            // Transform -> index in the FILTERED array, so "which RandomSpawn gates this" is a walk up
            // the parent chain rather than a guess from the path string.
            for (int i = 0; i < spawns.Length; i++)
            {
                if (spawns[i] == null) continue;
                Transform t = spawns[i].transform;
                if (!ctx.SpawnIndex.ContainsKey(t)) ctx.SpawnIndex[t] = i;
            }

            // Every m_OffObject in this prefab: the "not spawned" variants. Anything under one of them
            // appears only when its RandomSpawn does NOT spawn.
            foreach (RandomSpawn rs in spawns)
            {
                if (rs == null || rs.m_OffObject == null) continue;
                ctx.OffObjects.Add(rs.m_OffObject.transform);
            }

            // ---- RandomSpawns ----------------------------------------------------------------------
            var spawnDefs = new List<RandomSpawnDef>(spawns.Length);
            for (int i = 0; i < spawns.Length; i++)
            {
                RandomSpawnDef def;
                try { def = ReadSpawn(spawns[i], i, ctx); }
                catch (Exception e)
                {
                    warnings.Add("RandomSpawn[" + i + "] could not be read (" + Short(e) +
                                 "); it still consumes draw " + i + ".");
                    def = new RandomSpawnDef { index = i, parentRandomSpawnIndex = -1 };
                }
                spawnDefs.Add(def);
            }
            d.randomSpawns = spawnDefs.ToArray();
            ctx.SpawnDefs = spawnDefs;

            // ---- RandomObjects -----------------------------------------------------------------------
            var objectDefs = new List<RandomObjectDef>(objects.Length);
            for (int i = 0; i < objects.Length; i++)
            {
                RandomObjectDef def;
                try { def = ReadObject(objects[i], i, ctx); }
                catch (Exception e)
                {
                    warnings.Add("RandomObject[" + i + "] could not be read (" + Short(e) +
                                 "); it still consumes draw " + (spawns.Length + i) + ".");
                    def = new RandomObjectDef { index = i, parentRandomSpawnIndex = -1 };
                }
                objectDefs.Add(def);
            }
            d.randomObjects = objectDefs.ToArray();

            // ---- containers ----------------------------------------------------------------------------
            try
            {
                // includeInactive: a chest behind a switched-off RandomSpawn is still a chest that CAN
                // appear, and the whole point of the file is what CAN be there.
                Container[] containers = asset.GetComponentsInChildren<Container>(true);
                var cDefs = new List<ContainerDef>(containers != null ? containers.Length : 0);
                if (containers != null)
                {
                    foreach (Container c in containers)
                    {
                        if (c == null) continue;
                        try { cDefs.Add(ReadContainer(c, ctx)); }
                        catch (Exception e)
                        {
                            warnings.Add("A Container could not be read (" + Short(e) + ").");
                        }
                    }
                }
                d.containers = cDefs.ToArray();
                d.containerCount = cDefs.Count;
            }
            catch (Exception e)
            {
                warnings.Add("The Container scan failed (" + Short(e) + "); this entry lists none.");
            }

            // ---- the flat name indices, and the search ---------------------------------------------
            // The ROOT's own name first, and OUTSIDE the try below: it costs nothing, and it must
            // survive a child-name index that throws. childNames excludes the root by contract, so
            // without these three fields the prefab's own name would be in no index at all.
            try
            {
                d.rootName = asset.name;
                d.rootNormalizedName = Names.Normalize(d.rootName);
                d.rootNormalizedHash = Names.Hash(d.rootNormalizedName);
            }
            catch (Exception e)
            {
                warnings.Add("The asset root's own name could not be read (" + Short(e) + ").");
            }

            try
            {
                int transformCount;
                ChildNameDef[] raw;
                PrefabNameDef[] normalized;
                ChildNames(asset, ctx, out transformCount, out raw, out normalized);
                d.childTransformCount = transformCount;
                d.childNames = raw;
                d.prefabNames = normalized;
            }
            catch (Exception e)
            {
                warnings.Add("The child-name index failed (" + Short(e) + "); this entry lists none, " +
                             "and any sought name inside this prefab was NOT searched for.");
                // Set as an ERROR, not only a warning. The name pass is also the search pass, so its
                // failure is a place a sought name could have been hiding - and `error != null &&
                // loaded` is the single condition the two callers count incomplete walks with. There
                // is exactly one counter and exactly one place that increments it; a coverage number
                // that double-counts would undermine the one file whose job is honest numbers.
                if (d.error == null) d.error = "the child-name index failed: " + Short(e);
            }
        }

        // ---- one RandomSpawn --------------------------------------------------------------------------

        private static RandomSpawnDef ReadSpawn(RandomSpawn rs, int index, WalkContext ctx)
        {
            GameObject go = rs.gameObject;
            Transform t = rs.transform;
            string normalized = Names.Normalize(go.name);

            bool underGen = ctx.GeneratorTransform != null && IsUnder(t, ctx.GeneratorTransform, ctx.Root);
            bool underInterior = ctx.InteriorTransform != null && IsUnder(t, ctx.InteriorTransform, ctx.Root);
            if (underGen || underInterior) ctx.InstanceDependentPositions++;

            var def = new RandomSpawnDef
            {
                index = index,
                path = PathOf(ctx.Root, t),
                name = go.name,
                normalizedName = normalized,
                normalizedHash = Names.Hash(normalized),
                localPosition = Snapshot.Vec3(t.localPosition),
                prefabPosition = Snapshot.Vec3(PrefabPosition(t, ctx)),
                dumpWorldPosition = Snapshot.Vec3(t.position),
                underGeneratorTransform = underGen,
                underInteriorTransform = underInterior,
                prefabPositionIsInstanceDependent = underGen || underInterior,
                chanceToSpawn = rs.m_chanceToSpawn,
                requireBiome = (int)rs.m_requireBiome,
                minElevation = rs.m_minElevation,
                maxElevation = rs.m_maxElevation,
                notInLava = rs.m_notInLava,
                dungeonRequireTheme = (int)rs.m_dungeonRequireTheme,
                activeSelf = go.activeSelf,
                enabledInHierarchy = EnabledInHierarchy(go, ctx.RootGo),
                hasNetView = go.GetComponent<ZNetView>() != null,
                offObjectChildNames = Empty<string>(),
                activatedNetViewNames = Empty<string>(),
                subtreeChildNames = Empty<string>(),
                activatedNetViewCount = 0,
                parentRandomSpawnIndex = NearestSpawnAncestor(t.parent, ctx),
                underAnOffObject = UnderAnOffObject(t, ctx),
            };

            GameObject off = rs.m_OffObject;
            if (off != null)
            {
                def.offObjectName = off.name;
                def.offObjectPath = PathOf(ctx.Root, off.transform);
                def.offObjectActiveSelf = off.activeSelf;
                List<string> offNames = DistinctSubtreeNames(off.transform);
                def.offObjectChildNameCount = offNames.Count;
                def.offObjectChildNames = Cap(offNames);
            }

            int netViewCount;
            def.activatedNetViewNames = ActivatedNetViewNames(go, out netViewCount);
            def.activatedNetViewCount = netViewCount;

            List<string> subtree = DistinctSubtreeNames(t);
            def.subtreeChildNameCount = subtree.Count;
            def.subtreeChildNames = Cap(subtree);

            if (rs.m_chanceToSpawn < 0f || rs.m_chanceToSpawn > 100f)
            {
                ctx.Warnings.Add("RandomSpawn[" + index + "] '" + go.name + "' has m_chanceToSpawn " +
                                 Json.Hex(rs.m_chanceToSpawn) + ", outside 0..100. The comparison is " +
                                 "draw <= chance against Random.Range(0f,100f), so it is a constant.");
            }
            return def;
        }

        /// <summary>
        /// The names of the <c>ZNetView</c>s this RandomSpawn switches on, in the order
        /// <c>RandomSpawn.Prepare()</c> builds <c>m_childNetViews</c>:
        /// <c>GetComponentsInChildren&lt;ZNetView&gt;(includeInactive: true)</c> over the RandomSpawn's
        /// own subtree, filtered by <c>Utils.IsEnabledInheirarcy(child.gameObject, this.gameObject)</c>.
        ///
        /// <b>Reproduced, never invoked.</b> Calling <c>Prepare()</c> would write the component's
        /// private <c>m_nview</c> and <c>m_childNetViews</c> on a shared prefab asset.
        ///
        /// Returned NORMALISED, distinct and sorted ordinal, with the true list length in
        /// <paramref name="count"/>. Prepare's order carries no information - it builds the list only
        /// to <c>SetActive</c> every entry together, and nothing in it touches the RNG - while a
        /// building's 300-piece list written out verbatim for every RandomSpawn in the prefab is most
        /// of this file's size. Not capped.
        /// </summary>
        private static string[] ActivatedNetViewNames(GameObject go, out int count)
        {
            count = 0;
            try
            {
                ZNetView[] views = go.GetComponentsInChildren<ZNetView>(true);
                if (views == null) return Empty<string>();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (ZNetView v in views)
                {
                    if (v == null) continue;
                    if (!EnabledInHierarchy(v.gameObject, go)) continue;
                    count++;
                    string n = Names.Normalize(v.gameObject.name);
                    if (n != null) names.Add(n);
                }
                var list = new List<string>(names);
                list.Sort(StringComparer.Ordinal);
                return list.ToArray();
            }
            catch { return Empty<string>(); }
        }

        // ---- one RandomObject --------------------------------------------------------------------------

        private static RandomObjectDef ReadObject(RandomObject ro, int index, WalkContext ctx)
        {
            GameObject go = ro.gameObject;
            Transform t = ro.transform;
            string normalized = Names.Normalize(go.name);

            bool underGen = ctx.GeneratorTransform != null && IsUnder(t, ctx.GeneratorTransform, ctx.Root);
            bool underInterior = ctx.InteriorTransform != null && IsUnder(t, ctx.InteriorTransform, ctx.Root);
            if (underGen || underInterior) ctx.InstanceDependentPositions++;

            var def = new RandomObjectDef
            {
                index = index,
                path = PathOf(ctx.Root, t),
                name = go.name,
                normalizedName = normalized,
                normalizedHash = Names.Hash(normalized),
                localPosition = Snapshot.Vec3(t.localPosition),
                prefabPosition = Snapshot.Vec3(PrefabPosition(t, ctx)),
                dumpWorldPosition = Snapshot.Vec3(t.position),
                underGeneratorTransform = underGen,
                underInteriorTransform = underInterior,
                prefabPositionIsInstanceDependent = underGen || underInterior,
                requireBiome = (int)ro.m_requireBiome,
                minElevation = ro.m_minElevation,
                maxElevation = ro.m_maxElevation,
                notInLava = ro.m_notInLava,
                dungeonRequireTheme = (int)ro.m_dungeonRequireTheme,
                activeSelf = go.activeSelf,
                enabledInHierarchy = EnabledInHierarchy(go, ctx.RootGo),
                hasNetView = go.GetComponent<ZNetView>() != null,
                objects = Empty<RandomObjectEntryDef>(),
                parentRandomSpawnIndex = NearestSpawnAncestor(t.parent, ctx),
                underAnOffObject = UnderAnOffObject(t, ctx),
            };

            List<string> subtree = DistinctSubtreeNames(t);
            def.subtreeChildNameCount = subtree.Count;
            def.subtreeChildNames = Cap(subtree);

            // The weighted pick: total = sum of every m_weight INCLUDING null entries, then
            // Random.Range(0f, total) and a running sum walked in list order with draw <= sum.
            List<RandomObject.ObjectEntry> entries = ro.m_objects;
            float total = 0f;
            var entryDefs = new List<RandomObjectEntryDef>(entries != null ? entries.Count : 0);
            if (entries != null)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    RandomObject.ObjectEntry e = entries[i];
                    float weight = e != null ? e.m_weight : 0f;
                    total += weight;
                    GameObject obj = e != null ? e.m_object : null;
                    string rawName = obj != null ? obj.name : null;
                    string norm = Names.Normalize(rawName);
                    entryDefs.Add(new RandomObjectEntryDef
                    {
                        index = i,
                        name = rawName,
                        hash = rawName != null ? rawName.GetStableHashCode() : 0,
                        normalizedName = norm,
                        normalizedHash = Names.Hash(norm),
                        weight = weight,
                        activeSelf = obj != null && obj.activeSelf,
                        containers = Empty<ContainerDef>(),
                    });
                    if (obj == null)
                    {
                        ctx.Warnings.Add("RandomObject[" + index + "] '" + go.name + "' entry " + i +
                                         " has a null m_object. Its weight still counts toward the " +
                                         "total and it can still win the draw, in which case nothing " +
                                         "spawns.");
                    }
                }
            }
            def.objects = entryDefs.ToArray();
            def.totalWeight = total;

            // ---- what is INSIDE each weighted option -------------------------------------------------
            // Until 2026-09-23 an option was matched by NAME and nothing else: the prefab it points at
            // was never opened, so its Containers - and therefore their m_defaultItems drop tables -
            // were invisible to the whole dump. A chest that is a weighted option rather than a direct
            // child simply had no drop table anywhere in the output, silently.
            //
            // It is resolved rather than merely reported because it costs nothing to resolve:
            // RandomObject/ObjectEntry.m_object is a plain GameObject field (verified against the
            // shipped assembly_valheim on 2026-09-23 - NOT a SoftReference), i.e. an ordinary Unity
            // serialised reference that is already resident whenever the host prefab is. The code
            // directly above already dereferences it for obj.name and obj.activeSelf. So this is a
            // pure component query on a loaded asset: no Load(), no Release(), no asset-loader
            // reference count that could be left unbalanced, and no new failure mode beyond a throw,
            // which is caught and recorded per entry.
            //
            // Run as a second pass so every hit can carry the FINAL totalWeight, which is only known
            // once every entry's m_weight has been summed.
            if (entries != null)
            {
                for (int i = 0; i < entries.Count && i < entryDefs.Count; i++)
                {
                    RandomObject.ObjectEntry e = entries[i];
                    GameObject obj = e != null ? e.m_object : null;
                    if (obj == null) continue;
                    try { ReadOptionContents(entryDefs[i], obj, def, index, total, ctx); }
                    catch (Exception ex)
                    {
                        entryDefs[i].scanError = "the option scan failed: " + Short(ex);
                        if (ctx.Search != null) ctx.Search.RandomObjectOptionScanErrors++;
                    }
                }
            }

            // A weighted option is a prefab REFERENCE, not a child, so it never appears in the
            // transform walk - and a sought piece that is one of a RandomObject's options would be a
            // silent miss. Matched here, with enough of the draw to say how likely it is.
            if (ctx.Search != null && ctx.Search.Active)
            {
                foreach (RandomObjectEntryDef e in entryDefs)
                {
                    if (!ctx.Search.Wants(e.normalizedName)) continue;
                    ctx.Search.Add(e.normalizedName, new SearchHitDef
                    {
                        kind = "randomObjectEntry",
                        hostName = ctx.HostName,
                        hostRoomList = ctx.HostRoomList,
                        hostTheme = ctx.HostTheme,
                        hostEnabled = ctx.HostEnabled,
                        path = def.path,
                        rawName = e.name,
                        hasNetView = def.hasNetView,
                        enabledInHierarchy = def.enabledInHierarchy,
                        underAnOffObject = def.underAnOffObject,
                        gatedByRandomSpawnIndex = def.parentRandomSpawnIndex,
                        gatedByRandomSpawnPath = SpawnPath(ctx, def.parentRandomSpawnIndex),
                        chanceToSpawn = SpawnChance(ctx, def.parentRandomSpawnIndex),
                        requireBiome = SpawnField(ctx, def.parentRandomSpawnIndex, 0),
                        minElevation = SpawnField(ctx, def.parentRandomSpawnIndex, 1),
                        maxElevation = SpawnField(ctx, def.parentRandomSpawnIndex, 2),
                        notInLava = SpawnNotInLava(ctx, def.parentRandomSpawnIndex),
                        dungeonRequireTheme = SpawnField(ctx, def.parentRandomSpawnIndex, 3),
                        gatePositionIsInstanceDependent = SpawnInstanceDependent(ctx, def.parentRandomSpawnIndex),
                        randomObjectIndex = index,
                        randomObjectEntryIndex = e.index,
                        randomObjectEntryWeight = e.weight,
                        randomObjectTotalWeight = total,
                        note = "One weighted option of RandomObject[" + index + "], which consumes " +
                               "draw randomSpawnCount+" + index + ". It wins only when that single " +
                               "Random.Range(0, totalWeight) draw lands in its slice - it is not a " +
                               "child of the prefab and never appears in childNames.",
                    });
                }
            }
            return def;
        }

        // ---- inside one RandomObject option ------------------------------------------------------------

        /// <summary>Where an option-side hit came from, so a Container or a name found inside a
        /// weighted option can name the draw it depends on instead of looking like a plain child.</summary>
        private sealed class OptionOrigin
        {
            public int RandomObjectIndex;
            public int EntryIndex;
            public float EntryWeight;
            public float TotalWeight;
            public string OptionName;
            public int GateIndex;
            public string GatePath;

            /// <summary>The prefix every option-side path carries, so a reader can never mistake it
            /// for a path inside the host prefab.</summary>
            public string Label(string pathInOption)
            {
                return "RandomObject[" + RandomObjectIndex + "].m_objects[" + EntryIndex + "] " +
                       (OptionName ?? "(null)") +
                       (string.IsNullOrEmpty(pathInOption) ? "" : "/" + pathInOption);
            }

            public string Note(string what)
            {
                // Invariant throughout: these numbers are also on the hit as real JSON floats, and a
                // comma decimal separator in the prose beside a dot in the field would read as a
                // disagreement between them.
                return what + " It exists only when option " + EntryIndex + " ('" +
                       (OptionName ?? "(null)") + "') wins RandomObject[" + RandomObjectIndex +
                       "]'s single Random.Range(0, " +
                       TotalWeight.ToString(CultureInfo.InvariantCulture) + ") draw with weight " +
                       EntryWeight.ToString(CultureInfo.InvariantCulture) +
                       " - the option is a prefab REFERENCE, not a child of the host, " +
                       "so it appears in no childNames index.";
            }
        }

        /// <summary>
        /// Reads one weighted option's own subtree: its Containers (with drop tables), the names
        /// inside it, and the count of the RandomObjects nested in it that are deliberately NOT
        /// followed. Every failure is recorded on the entry and counted in the search coverage rather
        /// than thrown: an option that could not be read is a place a sought name could have been.
        /// </summary>
        private static void ReadOptionContents(RandomObjectEntryDef entryDef, GameObject option,
                                               RandomObjectDef owner, int randomObjectIndex,
                                               float totalWeight, WalkContext ctx)
        {
            SearchIndex search = ctx.Search;
            if (search != null) search.RandomObjectOptionsResolved++;

            Transform optionRoot = option.transform;
            var origin = new OptionOrigin
            {
                RandomObjectIndex = randomObjectIndex,
                EntryIndex = entryDef.index,
                EntryWeight = entryDef.weight,
                TotalWeight = totalWeight,
                OptionName = entryDef.name,
                // The option inherits the RandomObject's own gate: whatever RandomSpawn switches the
                // RandomObject off switches every option off with it.
                GateIndex = owner != null ? owner.parentRandomSpawnIndex : -1,
                GatePath = owner != null ? SpawnPath(ctx, owner.parentRandomSpawnIndex) : null,
            };

            // ---- the chests, which is the question this was added for ------------------------------
            try
            {
                Container[] found = option.GetComponentsInChildren<Container>(true);
                var cDefs = new List<ContainerDef>(found != null ? found.Length : 0);
                if (found != null)
                {
                    foreach (Container c in found)
                    {
                        if (c == null) continue;
                        try { cDefs.Add(ReadOptionContainer(c, option, optionRoot, owner, origin, ctx)); }
                        catch (Exception e)
                        {
                            entryDef.scanError = "a Container inside the option could not be read: " +
                                                 Short(e);
                            if (search != null) search.RandomObjectOptionScanErrors++;
                        }
                    }
                }
                entryDef.containers = cDefs.ToArray();
                entryDef.containerCount = cDefs.Count;
                if (search != null) search.RandomObjectOptionContainersRead += cDefs.Count;
            }
            catch (Exception e)
            {
                entryDef.scanError = "the option's Container scan failed: " + Short(e);
                if (search != null) search.RandomObjectOptionScanErrors++;
            }

            // ---- the names inside the option -------------------------------------------------------
            // Opening the option for its chests and then not matching the names already in hand would
            // leave exactly the false negative this change is about, one level down.
            if (search != null && search.Active)
            {
                try
                {
                    Transform[] all = option.GetComponentsInChildren<Transform>(true);
                    if (all != null)
                    {
                        foreach (Transform t in all)
                        {
                            // The option's OWN name is matched by the randomObjectEntry pass in
                            // ReadObject; matching it again here would double-count it.
                            if (t == null || t == optionRoot) continue;
                            string norm = Names.Normalize(t.name);
                            if (!search.Wants(norm)) continue;
                            string inOption = PathOf(optionRoot, t);
                            search.Add(norm, OptionHit(ctx, owner, origin, t.name,
                                origin.Label(inOption),
                                origin.Note("An object INSIDE a RandomObject option's prefab.")));
                        }
                    }
                }
                catch (Exception e)
                {
                    entryDef.scanError = (entryDef.scanError == null ? "" : entryDef.scanError + " ") +
                                         "the option's name walk failed: " + Short(e);
                    search.RandomObjectOptionScanErrors++;
                }
            }

            // ---- the gap that is deliberately left, counted so it is never silent -------------------
            // Only a nested RandomObject that actually HAS an option is counted. One with an empty or
            // all-null m_objects list hides nothing - counting it would inflate a number the coverage
            // report is going to put in front of the user, and a gap figure nobody can act on is the
            // same kind of noise as a warning that fires on healthy data.
            try
            {
                RandomObject[] nested = option.GetComponentsInChildren<RandomObject>(true);
                int n = 0;
                if (nested != null)
                {
                    foreach (RandomObject r in nested)
                    {
                        if (r == null || r.m_objects == null) continue;
                        bool hasOption = false;
                        foreach (RandomObject.ObjectEntry ne in r.m_objects)
                        {
                            if (ne != null && ne.m_object != null) { hasOption = true; break; }
                        }
                        if (hasOption) n++;
                    }
                }
                entryDef.nestedRandomObjectCount = n;
                if (search != null) search.NestedRandomObjectsInOptions += n;
            }
            catch (Exception e)
            {
                ctx.Warnings.Add("RandomObject[" + randomObjectIndex + "] option '" +
                                 (entryDef.name ?? "(null)") + "': its nested RandomObjects could not " +
                                 "be counted (" + Short(e) + "), so the size of the unfollowed gap " +
                                 "below it is unknown.");
                if (search != null) search.RandomObjectOptionScanErrors++;
            }
        }

        /// <summary>A hit found inside a weighted option. The gate is the RandomObject's own, plus the
        /// weighted draw that picks this option.</summary>
        private static SearchHitDef OptionHit(WalkContext ctx, RandomObjectDef owner,
                                              OptionOrigin origin, string rawName, string path,
                                              string note)
        {
            return new SearchHitDef
            {
                kind = ctx.HostKind,
                hostName = ctx.HostName,
                hostRoomList = ctx.HostRoomList,
                hostTheme = ctx.HostTheme,
                hostEnabled = ctx.HostEnabled,
                path = path,
                rawName = rawName,
                underAnOffObject = owner != null && owner.underAnOffObject,
                gatedByRandomSpawnIndex = origin.GateIndex,
                gatedByRandomSpawnPath = origin.GatePath,
                chanceToSpawn = SpawnChance(ctx, origin.GateIndex),
                requireBiome = SpawnField(ctx, origin.GateIndex, 0),
                minElevation = SpawnField(ctx, origin.GateIndex, 1),
                maxElevation = SpawnField(ctx, origin.GateIndex, 2),
                notInLava = SpawnNotInLava(ctx, origin.GateIndex),
                dungeonRequireTheme = SpawnField(ctx, origin.GateIndex, 3),
                gatePositionIsInstanceDependent = SpawnInstanceDependent(ctx, origin.GateIndex),
                randomObjectIndex = origin.RandomObjectIndex,
                randomObjectEntryIndex = origin.EntryIndex,
                randomObjectEntryWeight = origin.EntryWeight,
                randomObjectTotalWeight = origin.TotalWeight,
                note = note,
            };
        }

        /// <summary>
        /// One Container inside a weighted option. Deliberately NOT <see cref="ReadContainer"/>: that
        /// one measures paths, enabled state and gates against <c>ctx.Root</c>, and the option prefab
        /// has a DIFFERENT root - it would produce a garbage path, an <c>enabledInHierarchy</c> of
        /// false from the caught throw, and a gate of -1 reading as "unconditional", which is the
        /// opposite of the truth here.
        /// </summary>
        private static ContainerDef ReadOptionContainer(Container c, GameObject optionGo,
                                                        Transform optionRoot, RandomObjectDef owner,
                                                        OptionOrigin origin, WalkContext ctx)
        {
            GameObject go = c.gameObject;
            Transform t = c.transform;
            string normalized = Names.Normalize(go.name);
            string inOption = t == optionRoot ? "" : PathOf(optionRoot, t);

            return new ContainerDef
            {
                path = origin.Label(inOption),
                name = go.name,
                hash = go.name.GetStableHashCode(),
                normalizedName = normalized,
                normalizedHash = Names.Hash(normalized),
                containerName = c.m_name,
                width = c.m_width,
                height = c.m_height,
                autoDestroyEmpty = c.m_autoDestroyEmpty,
                privacy = (int)c.m_privacy,
                checkGuardStone = c.m_checkGuardStone,
                activeSelf = go.activeSelf,
                enabledInHierarchy = EnabledInHierarchy(go, optionGo),
                gatedByRandomSpawnIndex = origin.GateIndex,
                gatedByRandomSpawnPath = origin.GatePath,
                underAnOffObject = owner != null && owner.underAnOffObject,
                defaultItems = DropTableOf(c.m_defaultItems, origin.Label(inOption), ctx, origin, owner),
            };
        }

        // ---- one Container ---------------------------------------------------------------------------------

        private static ContainerDef ReadContainer(Container c, WalkContext ctx)
        {
            GameObject go = c.gameObject;
            Transform t = c.transform;
            int gate = NearestSpawnAncestor(t, ctx);
            string normalized = Names.Normalize(go.name);

            var def = new ContainerDef
            {
                path = PathOf(ctx.Root, t),
                name = go.name,
                hash = go.name.GetStableHashCode(),
                normalizedName = normalized,
                normalizedHash = Names.Hash(normalized),
                containerName = c.m_name,
                width = c.m_width,
                height = c.m_height,
                autoDestroyEmpty = c.m_autoDestroyEmpty,
                privacy = (int)c.m_privacy,
                checkGuardStone = c.m_checkGuardStone,
                activeSelf = go.activeSelf,
                enabledInHierarchy = EnabledInHierarchy(go, ctx.RootGo),
                gatedByRandomSpawnIndex = gate,
                gatedByRandomSpawnPath = SpawnPath(ctx, gate),
                underAnOffObject = UnderAnOffObject(t, ctx),
                defaultItems = DropTableOf(c.m_defaultItems, go.name, ctx),
            };
            return def;
        }

        private static DropTableDef DropTableOf(DropTable table, string owner, WalkContext ctx)
        {
            return DropTableOf(table, owner, ctx, null, null);
        }

        /// <summary><paramref name="origin"/> is non-null when this table belongs to a container inside
        /// a RandomObject's weighted OPTION rather than to one of the host prefab's own children; the
        /// hits it produces then carry the draw that has to pick that option.</summary>
        private static DropTableDef DropTableOf(DropTable table, string owner, WalkContext ctx,
                                                OptionOrigin origin, RandomObjectDef optionOwner)
        {
            if (table == null) return null;

            List<DropTable.DropData> drops = table.m_drops;
            var defs = new List<DropDataDef>(drops != null ? drops.Count : 0);
            float total = 0f;
            if (drops != null)
            {
                for (int i = 0; i < drops.Count; i++)
                {
                    // DropData is a STRUCT, so the entry always exists even when m_item is null.
                    DropTable.DropData dd = drops[i];
                    total += dd.m_weight;
                    string itemName = dd.m_item != null ? dd.m_item.name : null;
                    string norm = Names.Normalize(itemName);
                    // The item's DISPLAY name, so a reader can say "a Curious Axe Head" rather than
                    // "AxeHead1". AxeHead1 and AxeHead2 are two different named items to a player and
                    // indistinguishable as prefab names.
                    string itemToken = Names.ItemToken(dd.m_item);
                    defs.Add(new DropDataDef
                    {
                        index = i,
                        itemPrefabName = itemName,
                        itemPrefabHash = itemName != null ? itemName.GetStableHashCode() : 0,
                        normalizedName = norm,
                        normalizedHash = Names.Hash(norm),
                        itemNameToken = itemToken,
                        itemLocalizedName = Names.Localize(itemToken),
                        stackMin = dd.m_stackMin,
                        stackMax = dd.m_stackMax,
                        weight = dd.m_weight,
                        dontScale = dd.m_dontScale,
                    });
                    if (itemName == null)
                    {
                        ctx.Warnings.Add("Container '" + owner + "' drop " + i + " has a null m_item; " +
                                         "its weight still counts toward the table's total.");
                    }
                    else if (ctx.Search != null && ctx.Search.Wants(norm))
                    {
                        string itemNote = "An ITEM in container '" + owner + "'s default drop table, " +
                                          "not a placed object. Whether it is rolled is decided at " +
                                          "spawn time by the ambient, unseeded stream, so no dump can " +
                                          "predict it.";
                        SearchHitDef hit;
                        if (origin != null)
                        {
                            hit = OptionHit(ctx, optionOwner, origin, itemName, owner,
                                            origin.Note(itemNote));
                        }
                        else
                        {
                            hit = new SearchHitDef
                            {
                                kind = "containerDrop",
                                hostName = ctx.HostName,
                                hostRoomList = ctx.HostRoomList,
                                hostTheme = ctx.HostTheme,
                                hostEnabled = ctx.HostEnabled,
                                path = owner,
                                rawName = itemName,
                                gatedByRandomSpawnIndex = -1,
                                randomObjectIndex = -1,
                                randomObjectEntryIndex = -1,
                                note = itemNote,
                            };
                        }
                        // A drop is an ITEM either way; only the route to the chest differs, and the
                        // randomObjectIndex on the hit is what says which route it was.
                        hit.kind = "containerDrop";
                        ctx.Search.Add(norm, hit);
                    }
                }
            }

            return new DropTableDef
            {
                dropMin = table.m_dropMin,
                dropMax = table.m_dropMax,
                dropChance = table.m_dropChance,
                oneOfEach = table.m_oneOfEach,
                dropCount = defs.Count,
                totalWeight = total,
                drops = defs.ToArray(),
            };
        }

        // ---- the flat child-name indices, and the search pass ---------------------------------------

        /// <summary>
        /// Every child GameObject name, inactive included, root excluded, in two indices: the RAW names
        /// (<see cref="ChildNameDef"/>, kept so nothing is hidden) and the NORMALISED ones
        /// (<see cref="PrefabNameDef"/>, which is the identity the game itself uses and therefore the
        /// one an "is X in here" question must be asked of).
        ///
        /// This is also where the sought-name search runs: the pass already visits every transform and
        /// already has the gate maps, so matching here costs one dictionary probe per transform rather
        /// than a second whole-prefab walk.
        ///
        /// The ZNetView set is gathered once and used as a lookup rather than calling
        /// <c>GetComponent</c> per transform: a location prefab can hold thousands of transforms and
        /// this runs for every prefab in the game.
        /// </summary>
        private static void ChildNames(GameObject asset, WalkContext ctx, out int transformCount,
                                       out ChildNameDef[] rawIndex, out PrefabNameDef[] normalizedIndex)
        {
            transformCount = 0;
            rawIndex = Empty<ChildNameDef>();
            normalizedIndex = Empty<PrefabNameDef>();

            var netViewObjects = new HashSet<Transform>();
            ZNetView[] views = asset.GetComponentsInChildren<ZNetView>(true);
            if (views != null)
            {
                foreach (ZNetView v in views) if (v != null) netViewObjects.Add(v.transform);
            }

            Transform[] all = asset.GetComponentsInChildren<Transform>(true);
            if (all == null) return;

            Transform root = ctx.Root;
            GameObject rootGo = ctx.RootGo;
            bool searching = ctx.Search != null && ctx.Search.Active;

            // ---- the ROOT's own name ----------------------------------------------------------------
            // The loop below skips the root on purpose: childNames / prefabNames are documented as
            // excluding it, childTransformCount counts on that, and the enabled-vs-present component
            // comparison uses the same exclusion. But the root still HAS a name, and a sought name can
            // BE a room or location prefab rather than something inside one - so skipping it here made
            // such a name a guaranteed false negative, which is exactly the wrong answer this whole
            // file exists to prevent. Locations were half-covered by the location table;
            // ROOM prefab names were reachable through nothing at all.
            //
            // The hit is recorded as the HOST ITSELF: null path, no gate. A root is unconditional by
            // construction - nothing inside the prefab can gate the prefab.
            if (searching)
            {
                try
                {
                    string rootRaw = rootGo != null ? rootGo.name : null;
                    MatchHostName(ctx, root, rootGo, netViewObjects, rootRaw,
                        "This IS the host prefab's own root GameObject, not something inside it. " +
                        "Where the prefab itself is placed is decided by the location table (for a " +
                        "location) or by the dungeon layout (for a room), not by anything in this file.");

                    // The name the GAME keys on is the SoftReference name, and the loaded asset's
                    // GameObject name can be spelled differently (a trailing " (1)", a "(Clone)"
                    // suffix). They normalise to the same thing in almost every case; when they do
                    // not, the second spelling is offered too rather than assumed equivalent.
                    string hostRaw = ctx.HostName;
                    if (hostRaw != null &&
                        !string.Equals(Names.Normalize(hostRaw), Names.Normalize(rootRaw),
                                       StringComparison.Ordinal))
                    {
                        MatchHostName(ctx, root, rootGo, netViewObjects, hostRaw,
                            "This IS the host prefab, matched on the name the game keys on " +
                            "(its SoftReference name), which is spelled differently from the loaded " +
                            "asset's own GameObject name '" + rootRaw + "'.");
                    }
                }
                catch { }
            }

            var byName = new Dictionary<string, ChildNameDef>(StringComparer.Ordinal);
            var byPrefab = new Dictionary<string, PrefabNameDef>(StringComparer.Ordinal);
            var rawSpellings = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            foreach (Transform t in all)
            {
                if (t == null || t == root) continue;
                transformCount++;

                GameObject go = t.gameObject;
                string name = go.name;
                string normalized = Names.Normalize(name);
                bool hasNetView = netViewObjects.Contains(t);
                bool enabled = EnabledInHierarchy(go, rootGo);

                ChildNameDef def;
                if (!byName.TryGetValue(name, out def))
                {
                    def = new ChildNameDef
                    {
                        name = name,
                        hash = name.GetStableHashCode(),
                        normalizedName = normalized,
                    };
                    byName[name] = def;
                }
                def.count++;
                if (hasNetView) def.netViewCount++;
                if (enabled) def.enabledCount++;

                if (normalized != null)
                {
                    PrefabNameDef pdef;
                    if (!byPrefab.TryGetValue(normalized, out pdef))
                    {
                        pdef = new PrefabNameDef
                        {
                            name = normalized,
                            hash = Names.Hash(normalized),
                            rawNames = Empty<string>(),
                        };
                        byPrefab[normalized] = pdef;
                        rawSpellings[normalized] = new List<string>();
                    }
                    pdef.count++;
                    if (hasNetView) pdef.netViewCount++;
                    if (enabled) pdef.enabledCount++;
                    List<string> spellings = rawSpellings[normalized];
                    if (!spellings.Contains(name)) spellings.Add(name);
                }

                if (searching && ctx.Search.Wants(normalized))
                {
                    int gate = NearestSpawnAncestor(t, ctx);
                    ctx.Search.Add(normalized, new SearchHitDef
                    {
                        kind = ctx.HostKind,
                        hostName = ctx.HostName,
                        hostRoomList = ctx.HostRoomList,
                        hostTheme = ctx.HostTheme,
                        hostEnabled = ctx.HostEnabled,
                        path = PathOf(root, t),
                        rawName = name,
                        hasNetView = hasNetView,
                        enabledInHierarchy = enabled,
                        underAnOffObject = UnderAnOffObject(t, ctx),
                        gatedByRandomSpawnIndex = gate,
                        gatedByRandomSpawnPath = SpawnPath(ctx, gate),
                        chanceToSpawn = SpawnChance(ctx, gate),
                        requireBiome = SpawnField(ctx, gate, 0),
                        minElevation = SpawnField(ctx, gate, 1),
                        maxElevation = SpawnField(ctx, gate, 2),
                        notInLava = SpawnNotInLava(ctx, gate),
                        dungeonRequireTheme = SpawnField(ctx, gate, 3),
                        gatePositionIsInstanceDependent = SpawnInstanceDependent(ctx, gate),
                        randomObjectIndex = -1,
                        randomObjectEntryIndex = -1,
                        note = gate < 0
                            ? "No RandomSpawn gates this object: it is unconditional in this prefab and " +
                              "appears in every instance of it."
                            : (ctx.HostKind == "room"
                                ? "Gated by RandomSpawn[" + gate + "], which consumes draw " + gate +
                                  " of the ROOM's stream. A room's stream is seeded from where the room " +
                                  "was placed, which comes out of the dungeon layout - rolled once per " +
                                  "generator and saved to its ZDO."
                                : "Gated by RandomSpawn[" + gate + "], which consumes draw " + gate +
                                  " of the location's stream (seed = worldSeed + zone.x*4271 + " +
                                  "zone.y*9187)."),
                    });
                }
            }

            var list = new List<ChildNameDef>(byName.Values);
            // Sorted ordinal: this is an INDEX, not a stream. Only the RandomSpawn/RandomObject arrays
            // carry semantic order, and a sorted index diffs cleanly across game builds.
            list.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            rawIndex = list.ToArray();

            var plist = new List<PrefabNameDef>(byPrefab.Values);
            foreach (PrefabNameDef p in plist)
            {
                List<string> spellings = rawSpellings[p.name];
                spellings.Sort(StringComparer.Ordinal);
                p.rawNameCount = spellings.Count;
                p.rawNames = Cap(spellings);
            }
            plist.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            normalizedIndex = plist.ToArray();
        }

        /// <summary>
        /// Offers ONE spelling of the host prefab's own name to the search, recording a match as the
        /// host itself rather than as a child: null <c>path</c>, <c>gatedByRandomSpawnIndex = -1</c>,
        /// <c>underAnOffObject = false</c>. Nothing inside a prefab can gate the prefab.
        /// Never throws: the search is worth less than the dump.
        /// </summary>
        private static void MatchHostName(WalkContext ctx, Transform root, GameObject rootGo,
                                          HashSet<Transform> netViewObjects, string rawName,
                                          string note)
        {
            string normalized = Names.Normalize(rawName);
            if (normalized == null || ctx.Search == null || !ctx.Search.Wants(normalized)) return;
            ctx.Search.Add(normalized, new SearchHitDef
            {
                kind = ctx.HostKind,
                hostName = ctx.HostName,
                hostRoomList = ctx.HostRoomList,
                hostTheme = ctx.HostTheme,
                hostEnabled = ctx.HostEnabled,
                path = null,
                rawName = rawName,
                hasNetView = root != null && netViewObjects != null && netViewObjects.Contains(root),
                enabledInHierarchy = rootGo != null && EnabledInHierarchy(rootGo, rootGo),
                underAnOffObject = false,
                gatedByRandomSpawnIndex = -1,
                gatedByRandomSpawnPath = null,
                randomObjectIndex = -1,
                randomObjectEntryIndex = -1,
                note = note,
            });
        }

        /// <summary>Distinct NORMALISED GameObject names in a subtree, itself included, inactive
        /// included, sorted ordinal. Returned as a list so the caller can record the true count before
        /// <see cref="Cap"/> truncates it.</summary>
        private static List<string> DistinctSubtreeNames(Transform t)
        {
            var list = new List<string>();
            try
            {
                Transform[] all = t.GetComponentsInChildren<Transform>(true);
                if (all == null) return list;
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (Transform c in all)
                {
                    if (c == null) continue;
                    string n = Names.Normalize(c.gameObject.name);
                    if (n != null) names.Add(n);
                }
                list.AddRange(names);
                list.Sort(StringComparer.Ordinal);
            }
            catch { list.Clear(); }
            return list;
        }

        /// <summary>The first <see cref="MaxNamesPerEntry"/> names. The caller always writes the
        /// untruncated count beside the result, so a reader can tell a short list from a cut one.</summary>
        private static string[] Cap(List<string> names)
        {
            if (names.Count <= MaxNamesPerEntry) return names.ToArray();
            var cut = new string[MaxNamesPerEntry];
            names.CopyTo(0, cut, 0, MaxNamesPerEntry);
            return cut;
        }

        // ---- geometry and hierarchy helpers ---------------------------------------------------------------------

        /// <summary>
        /// What the spawn routine reads as this child's position.
        ///
        /// <c>SpawnLocation</c> gets it by setting the asset root to <c>Vector3.zero</c> /
        /// <c>Quaternion.identity</c> and reading the child back; <c>PlaceRoom</c> computes
        /// <c>Inverse(room.rotation) * (child.position - room.position)</c> directly. Both are the same
        /// expression against their own anchor, and the root's <c>localScale</c> is left alone in both,
        /// so the scale stays baked into the value.
        ///
        /// The dumper must not mutate a shared asset, so it computes that quantity:
        /// <c>Inverse(anchorRotation) * (childWorld - anchorPosition)</c>. Deliberately NOT
        /// <c>anchor.InverseTransformPoint(child.position)</c>, which also divides by the anchor scale
        /// and would therefore disagree with the game on any prefab whose root is scaled.
        ///
        /// When the anchor is already at the identity this is exact: the subtraction is x-0 and the
        /// identity quaternion multiply reduces to 1*x + 0 + 0. <c>rootAtIdentity</c> on the entry says
        /// whether that was the case, and <c>prefabPositionIsInstanceDependent</c> says when the game
        /// moves the child before reading it at all.
        /// </summary>
        private static Vector3 PrefabPosition(Transform child, WalkContext ctx)
        {
            return ctx.InvAnchorRot * (child.position - ctx.AnchorPos);
        }

        /// <summary>The game's own enabled test, called rather than reimplemented. It walks
        /// <c>activeSelf</c> up to <paramref name="root"/>; a node outside <paramref name="root"/>'s
        /// subtree would dereference a null parent, so a throw is treated as "not enabled".</summary>
        private static bool EnabledInHierarchy(GameObject go, GameObject root)
        {
            try { return Utils.IsEnabledInheirarcy(go, root); }
            catch { return false; }
        }

        /// <summary>Index in the filtered RandomSpawn array of the nearest ancestor-or-self of
        /// <paramref name="from"/>, or -1. Pass <c>t.parent</c> to exclude the node itself.</summary>
        private static int NearestSpawnAncestor(Transform from, WalkContext ctx)
        {
            Transform t = from;
            while (t != null)
            {
                int i;
                if (ctx.SpawnIndex.TryGetValue(t, out i)) return i;
                if (t == ctx.Root) break;
                t = t.parent;
            }
            return -1;
        }

        private static bool UnderAnOffObject(Transform from, WalkContext ctx)
        {
            if (ctx.OffObjects.Count == 0) return false;
            Transform t = from;
            while (t != null)
            {
                if (ctx.OffObjects.Contains(t)) return true;
                if (t == ctx.Root) break;
                t = t.parent;
            }
            return false;
        }

        /// <summary>Is <paramref name="from"/> inside <paramref name="target"/>'s subtree (itself
        /// counting)? Stops at <paramref name="root"/> so a transform outside the prefab cannot walk
        /// off into the scene.</summary>
        private static bool IsUnder(Transform from, Transform target, Transform root)
        {
            if (target == null) return false;
            Transform t = from;
            while (t != null)
            {
                if (t == target) return true;
                if (t == root) break;
                t = t.parent;
            }
            return false;
        }

        // ---- gate lookups for the search hits --------------------------------------------------------

        private static RandomSpawnDef Gate(WalkContext ctx, int index)
        {
            if (ctx.SpawnDefs == null || index < 0 || index >= ctx.SpawnDefs.Count) return null;
            return ctx.SpawnDefs[index];
        }

        private static string SpawnPath(WalkContext ctx, int index)
        {
            RandomSpawnDef g = Gate(ctx, index);
            return g != null ? g.path : null;
        }

        private static float SpawnChance(WalkContext ctx, int index)
        {
            RandomSpawnDef g = Gate(ctx, index);
            return g != null ? g.chanceToSpawn : 0f;
        }

        private static bool SpawnNotInLava(WalkContext ctx, int index)
        {
            RandomSpawnDef g = Gate(ctx, index);
            return g != null && g.notInLava;
        }

        private static bool SpawnInstanceDependent(WalkContext ctx, int index)
        {
            RandomSpawnDef g = Gate(ctx, index);
            return g != null && g.prefabPositionIsInstanceDependent;
        }

        /// <summary>One of the gating RandomSpawn's integer gates: 0 requireBiome, 1 minElevation,
        /// 2 maxElevation, 3 dungeonRequireTheme. A switch rather than four near-identical helpers.</summary>
        private static int SpawnField(WalkContext ctx, int index, int which)
        {
            RandomSpawnDef g = Gate(ctx, index);
            if (g == null) return 0;
            switch (which)
            {
                case 0: return g.requireBiome;
                case 1: return g.minElevation;
                case 2: return g.maxElevation;
                case 3: return g.dungeonRequireTheme;
                default: return 0;
            }
        }

        /// <summary>Hierarchy path from the prefab root, '/' separated, the root itself excluded, RAW
        /// names throughout. Shared with <see cref="ModeAssets"/>'s DungeonGenerator paths so both files
        /// name the same node the same way.</summary>
        public static string PathOf(Transform root, Transform t)
        {
            string path = t.name;
            Transform p = t.parent;
            while (p != null && p != root)
            {
                path = p.name + "/" + path;
                p = p.parent;
            }
            return path;
        }

        // ---- small helpers -----------------------------------------------------------------------------------

        /// <summary>
        /// How many <typeparamref name="T"/> the prefab CONTAINS, inactive included, <b>excluding one
        /// mounted on the root transform</b>.
        ///
        /// That exclusion is the whole point. <c>Utils.GetEnabledComponentsInChildren</c> drops two
        /// kinds of component: one whose <c>activeSelf</c> chain to the root is broken, and one whose
        /// <c>transform == root.transform</c>. Counting the root one here and not there made every
        /// clean prefab with a root-mounted RandomSpawn look session-drifted, and the user was told to
        /// distrust a good dump. Comparing like with like leaves the comparison meaning exactly one
        /// thing: something was switched off.
        /// </summary>
        private static int CountAll<T>(GameObject asset, Transform root, List<string> warnings)
            where T : Component
        {
            try
            {
                T[] all = asset.GetComponentsInChildren<T>(true);
                if (all == null) return 0;
                int n = 0;
                foreach (T c in all)
                {
                    if (c == null) continue;
                    if (c.transform == root) continue;
                    n++;
                }
                return n;
            }
            catch (Exception e)
            {
                warnings.Add("Counting all " + typeof(T).Name + "s (inactive included) failed (" +
                             Short(e) + "); the count is 0 and cannot be compared.");
                return 0;
            }
        }

        private static int Length<T>(T[] a) { return a != null ? a.Length : 0; }

        private static T[] Empty<T>() { return new T[0]; }

        /// <summary>
        /// The <c>Character</c>s and <c>Trader</c>s inside a prefab, with their display-name tokens.
        ///
        /// <para><b>This is what makes "boss altar" and "trader camp" derived facts.</b> Until now both
        /// were hand-written arrays in the offline tool, with a comment admitting nothing in the dumped
        /// data marked either. <c>Character.m_boss</c> and <c>m_bossOrder</c> are serialized fields on
        /// the creature; reading them turns a curated list into a measurement, and a curated list is
        /// exactly the thing that drifts silently at the next game update.</para>
        ///
        /// <para><c>includeInactive: true</c> on purpose, but NOT for the reason first written here.
        /// The original comment claimed an altar's boss is "often inactive on the prefab"; it is not
        /// there at all. The five classic altars carry no <c>Character</c> whatsoever - the boss is
        /// <c>OfferingBowl.m_bossPrefab</c>, a reference to a different prefab - and where a boss IS a
        /// child (the Frozen King, the Seeker Queen) the game's own test says ENABLED. The false belief
        /// came from reading <c>activeInHierarchy</c>, which is meaningless on an asset that is not in a
        /// scene. The real reason to include inactive children is the one the rest of this walk uses:
        /// count everything present, and record its enabled state rather than letting a filter hide
        /// it.</para>
        /// </summary>
        private static void ReadOccupants(InteriorDef d, GameObject asset, WalkContext ctx)
        {
            d.occupantsCaptured = true;
            d.characters = Empty<CharacterDef>();
            d.traders = Empty<TraderDef>();
            d.offeringBowls = Empty<OfferingBowlDef>();
            d.runeStones = Empty<RuneStoneDef>();

            try
            {
                var chars = new List<CharacterDef>();
                foreach (Character c in asset.GetComponentsInChildren<Character>(true))
                {
                    if (c == null) continue;
                    chars.Add(new CharacterDef
                    {
                        path = PathOf(ctx.Root, c.transform),
                        prefabName = Names.Normalize(c.gameObject),
                        nameToken = c.m_name,
                        localizedName = Names.Localize(c.m_name),
                        boss = c.m_boss,
                        bossOrder = c.m_bossOrder,
                        faction = (int)c.m_faction,
                        group = c.m_group,
                        // NOT activeInHierarchy: a prefab asset loaded from a bundle is not in a
                        // scene, so Unity returns false for every node of it. The first version of this
                        // recorded false for all six occupants in the whole dump - Haldor, Hildir, the
                        // Bog Witch, Kvastur, the Frozen King, the Seeker Queen - while `childNames`,
                        // three fields away in the same file, reported them ENABLED via the game's own
                        // Utils.IsEnabledInheirarcy. A consumer filtering on the false value would have
                        // discarded every creature and both traders. Use the game's own test.
                        enabledInHierarchy = EnabledInHierarchy(c.gameObject, ctx.RootGo),
                        activeSelf = c.gameObject.activeSelf,
                    });
                }

                var traders = new List<TraderDef>();
                foreach (Trader t in asset.GetComponentsInChildren<Trader>(true))
                {
                    if (t == null) continue;
                    traders.Add(new TraderDef
                    {
                        path = PathOf(ctx.Root, t.transform),
                        prefabName = Names.Normalize(t.gameObject),
                        nameToken = t.m_name,
                        localizedName = Names.Localize(t.m_name),
                        enabledInHierarchy = EnabledInHierarchy(t.gameObject, ctx.RootGo),
                        activeSelf = t.gameObject.activeSelf,
                    });
                }

                // The altar-to-boss link. A classic altar holds no Character; it holds an
                // OfferingBowl whose m_bossPrefab IS the creature, and that prefab's Character.m_name
                // is the name a player knows. Eight location prefabs have one.
                var bowls = new List<OfferingBowlDef>();
                foreach (OfferingBowl b in asset.GetComponentsInChildren<OfferingBowl>(true))
                {
                    if (b == null) continue;
                    var def = new OfferingBowlDef
                    {
                        path = PathOf(ctx.Root, b.transform),
                        prefabName = Names.Normalize(b.gameObject),
                        nameToken = b.m_name,
                        localizedName = Names.Localize(b.m_name),
                        offeringItemCount = b.m_bossItems,
                        setGlobalKey = b.m_setGlobalKey,
                    };

                    if (b.m_bossPrefab != null)
                    {
                        def.bossPrefabName = Names.Normalize(b.m_bossPrefab);
                        Character bc = b.m_bossPrefab.GetComponent<Character>();
                        if (bc != null)
                        {
                            def.bossNameToken = bc.m_name;
                            def.bossLocalizedName = Names.Localize(bc.m_name);
                            def.bossFlag = bc.m_boss;
                            def.bossOrder = bc.m_bossOrder;
                        }
                        else
                        {
                            ctx.Warnings.Add("OfferingBowl '" + def.prefabName + "' summons '"
                                             + def.bossPrefabName + "', which carries no Character - "
                                             + "so this altar has no boss name to show.");
                        }
                    }

                    if (b.m_bossItem != null)
                    {
                        def.offeringItemName = Names.Normalize(b.m_bossItem.gameObject);
                        def.offeringItemToken = Names.ItemToken(b.m_bossItem.gameObject);
                        def.offeringItemLocalizedName = Names.Localize(def.offeringItemToken);
                    }

                    bowls.Add(def);
                }

                // The widest source of player-facing PLACE names the game has: 24 location prefabs
                // carry a rune stone, against 4 that carry a discoverLabel.
                var runes = new List<RuneStoneDef>();
                foreach (RuneStone rs in asset.GetComponentsInChildren<RuneStone>(true))
                {
                    if (rs == null) continue;
                    runes.Add(new RuneStoneDef
                    {
                        path = PathOf(ctx.Root, rs.transform),
                        prefabName = Names.Normalize(rs.gameObject),
                        nameToken = rs.m_name,
                        localizedName = Names.Localize(rs.m_name),
                        topicToken = rs.m_topic,
                        topicLocalized = Names.Localize(rs.m_topic),
                        labelToken = rs.m_label,
                        labelLocalized = Names.Localize(rs.m_label),
                        locationNameToken = rs.m_locationName,
                        locationNameLocalized = Names.Localize(rs.m_locationName),
                        pinNameToken = rs.m_pinName,
                        pinNameLocalized = Names.Localize(rs.m_pinName),
                    });
                }

                d.characters = chars.ToArray();
                d.traders = traders.ToArray();
                d.offeringBowls = bowls.ToArray();
                d.runeStones = runes.ToArray();
            }
            catch (Exception e)
            {
                d.occupantsCaptured = false;
                ctx.Warnings.Add("The occupant walk threw (" + Short(e) + "); characters, traders, " +
                                 "offering bowls and rune stones are NOT captured for this prefab, and " +
                                 "occupantsCaptured is false to say so rather than let an empty array " +
                                 "read as 'nobody lives here'.");
            }
        }

        /// <summary>
        /// The <c>Teleport</c>s and <c>Vegvisir</c>s inside a prefab (added 2026-09-24).
        ///
        /// <para><b>Teleport is here for one field:</b> <c>m_enterText</c>, the caption
        /// <c>Teleport.Interact</c> hands to <c>MessageHud.ShowBiomeFoundMsg</c> after a successful
        /// teleport - "Burial Chambers" and the like. It is the game's only name for a dungeon, and it
        /// lives on the prefab, not in code, so this walk is the only way to read it.</para>
        ///
        /// <para><b>Vegvisir</b> is read in the same pass because its <c>m_locations</c> entries are
        /// the pin captions the game writes for the places it reveals - evidence about OTHER prefabs'
        /// names, like a runestone's, and cheap to take while the asset is loaded.</para>
        ///
        /// <para>Separate from <see cref="ReadOccupants"/> with its own flag, so a throw here cannot
        /// unset <c>occupantsCaptured</c> on captures already verified against the game.
        /// <c>includeInactive: true</c> for the reason ReadOccupants gives: count everything present
        /// and record its enabled state rather than let a filter hide it.</para>
        /// </summary>
        private static void ReadWaymarks(InteriorDef d, GameObject asset, WalkContext ctx)
        {
            d.waymarksCaptured = true;
            d.teleports = Empty<TeleportDef>();
            d.vegvisirs = Empty<VegvisirDef>();

            try
            {
                var teleports = new List<TeleportDef>();
                foreach (Teleport tp in asset.GetComponentsInChildren<Teleport>(true))
                {
                    if (tp == null) continue;
                    var def = new TeleportDef
                    {
                        path = PathOf(ctx.Root, tp.transform),
                        prefabName = Names.Normalize(tp.gameObject),
                        hoverTextToken = tp.m_hoverText,
                        hoverTextLocalized = Names.Localize(tp.m_hoverText),
                        enterTextToken = tp.m_enterText,
                        enterTextLocalized = Names.Localize(tp.m_enterText),
                        hasTarget = tp.m_targetPoint != null,
                        enabledInHierarchy = EnabledInHierarchy(tp.gameObject, ctx.RootGo),
                        activeSelf = tp.gameObject.activeSelf,
                    };

                    if (def.hasTarget)
                    {
                        Transform target = tp.m_targetPoint.transform;
                        // IsChildOf is true for the root itself as well as its descendants. PathOf
                        // would walk a foreign target up to its own scene root and return a path that
                        // looks local, so it is only asked about a target that is.
                        def.targetInPrefab = target.IsChildOf(ctx.Root);
                        if (def.targetInPrefab) def.targetPath = PathOf(ctx.Root, target);
                    }

                    teleports.Add(def);
                }

                var vegvisirs = new List<VegvisirDef>();
                foreach (Vegvisir v in asset.GetComponentsInChildren<Vegvisir>(true))
                {
                    if (v == null) continue;
                    var entries = new List<VegvisirLocationDef>();
                    if (v.m_locations != null)
                    {
                        foreach (Vegvisir.VegvisrLocation l in v.m_locations)
                        {
                            if (l == null) continue;
                            entries.Add(new VegvisirLocationDef
                            {
                                locationName = l.m_locationName,
                                pinNameToken = l.m_pinName,
                                pinNameLocalized = Names.Localize(l.m_pinName),
                                pinType = (int)l.m_pinType,
                                pinTypeName = l.m_pinType.ToString(),
                                discoverAll = l.m_discoverAll,
                                showMap = l.m_showMap,
                            });
                        }
                    }
                    else
                    {
                        ctx.Warnings.Add("Vegvisir '" + Names.Normalize(v.gameObject) + "' has a null " +
                                         "m_locations; Interact would throw on it, and it is recorded " +
                                         "with no entries.");
                    }

                    vegvisirs.Add(new VegvisirDef
                    {
                        path = PathOf(ctx.Root, v.transform),
                        prefabName = Names.Normalize(v.gameObject),
                        nameToken = v.m_name,
                        localizedName = Names.Localize(v.m_name),
                        hoverNameToken = v.m_hoverName,
                        hoverNameLocalized = Names.Localize(v.m_hoverName),
                        useTextToken = v.m_useText,
                        useTextLocalized = Names.Localize(v.m_useText),
                        setsGlobalKey = v.m_setsGlobalKey,
                        setsPlayerKey = v.m_setsPlayerKey,
                        locations = entries.ToArray(),
                        enabledInHierarchy = EnabledInHierarchy(v.gameObject, ctx.RootGo),
                        activeSelf = v.gameObject.activeSelf,
                    });
                }

                d.teleports = teleports.ToArray();
                d.vegvisirs = vegvisirs.ToArray();
            }
            catch (Exception e)
            {
                d.waymarksCaptured = false;
                d.teleports = Empty<TeleportDef>();
                d.vegvisirs = Empty<VegvisirDef>();
                ctx.Warnings.Add("The waymark walk threw (" + Short(e) + "); teleports and vegvisirs " +
                                 "are NOT captured for this prefab, and waymarksCaptured is false to say " +
                                 "so rather than let an empty array read as 'this place has no door'.");
            }
        }

        private static QuatDef Quat(Quaternion q)
        {
            return new QuatDef { x = q.x, y = q.y, z = q.z, w = q.w };
        }

        /// <summary>
        /// The room's <c>RoomConnection</c>s, without going through <c>Room.GetConnections()</c>.
        ///
        /// <b>Why not GetConnections().</b> It caches its result into the component's private
        /// <c>m_roomConnections</c> field on a SHARED asset that the game keeps for the rest of the
        /// session; the walk's rule is that it leaves no state behind. The call made here is the one
        /// GetConnections() itself makes - <c>GetComponentsInChildren&lt;RoomConnection&gt;(false)</c>
        /// on the Room's own GameObject - so the array, and its order, are the game's.
        ///
        /// <b>The zero trap.</b> <c>includeInactive: false</c> filters on <c>activeInHierarchy</c>, so
        /// an inactive asset root would return an empty array that reads exactly like "this room has no
        /// connections" - which for the Dungeon algorithm means "this room can never be placed". The
        /// inactive-inclusive count is taken as well and a disagreement is written as a warning, so the
        /// two facts can never be confused for each other.
        /// </summary>
        private static void ReadConnections(RoomChildrenDef d, Room room, Transform root,
                                            List<string> warnings)
        {
            d.connectionsCaptured = true;
            d.connections = Empty<RoomConnectionDef>();

            RoomConnection[] active;
            RoomConnection[] all;
            try
            {
                active = room.GetComponentsInChildren<RoomConnection>(false);
                all = room.GetComponentsInChildren<RoomConnection>(true);
            }
            catch (Exception e)
            {
                d.connectionsCaptured = false;
                warnings.Add("The RoomConnection walk threw (" + Short(e) + "); connections for this " +
                             "room are NOT captured, and connectionsCaptured is false to say so.");
                return;
            }

            int activeCount = Length(active);
            int allCount = Length(all);
            if (activeCount != allCount)
            {
                warnings.Add("This room has " + allCount + " RoomConnection components but only " +
                             activeCount + " are active. The game's own lookup passes " +
                             "includeInactive:false, so the inactive ones do not exist as far as " +
                             "DungeonGenerator is concerned and they are not listed here.");
            }
            if (activeCount == 0 && allCount == 0 && room.m_size.x != 0 && room.m_size.z != 0)
            {
                warnings.Add("This room has no RoomConnection at all. The Dungeon algorithm can never " +
                             "place it (PlaceStartRoom, PlaceOneRoom and PlaceEndCaps all reach it " +
                             "through a connection); the camp algorithms can, because they place by " +
                             "position and never look at connections.");
            }

            Transform anchor = room.transform;
            Quaternion inv = Quaternion.Inverse(anchor.rotation);
            var defs = new List<RoomConnectionDef>(activeCount);
            for (int i = 0; i < activeCount; i++)
            {
                RoomConnection c = active[i];
                if (c == null)
                {
                    // A placeholder keeps the indices aligned with the game's array, but it must not
                    // be readable as a connection: every value on it would be a default, and
                    // m_allowDoor's default is TRUE, so writing false here states the opposite of the
                    // truth. The whole point of connectionsCaptured is that the file never claims a
                    // fact it does not have, so this clears it.
                    defs.Add(new RoomConnectionDef { index = i });
                    d.connectionsCaptured = false;
                    warnings.Add("RoomConnection[" + i + "] came back null (a destroyed or missing "
                                 + "component). Its record holds the index and nothing else - every "
                                 + "other value on it is a placeholder, NOT a reading, and "
                                 + "connectionsCaptured is false for this room to say so.");
                    continue;
                }
                Transform t = c.transform;
                defs.Add(new RoomConnectionDef
                {
                    index = i,
                    path = PathOf(root, t),
                    name = c.gameObject.name,
                    // Parent-relative: this pair is what CalculateRoomPosRot reads, whether or not the
                    // parent is the room.
                    localPosition = Snapshot.Vec3(t.localPosition),
                    localRotation = Quat(t.localRotation),
                    // Room-anchor-relative: equal to the pair above in the normal case, and the one a
                    // reader wants when they differ.
                    roomPosition = Snapshot.Vec3(inv * (t.position - anchor.position)),
                    roomRotation = Quat(inv * t.rotation),
                    directChildOfRoom = t.parent == anchor,
                    type = c.m_type,
                    entrance = c.m_entrance,
                    allowDoor = c.m_allowDoor,
                    doorOnlyIfOtherAlsoAllowsDoor = c.m_doorOnlyIfOtherAlsoAllowsDoor,
                    activeSelf = c.gameObject.activeSelf,
                });
                if (t.parent != anchor)
                {
                    warnings.Add("RoomConnection[" + i + "] '" + c.gameObject.name + "' is not a " +
                                 "direct child of the Room. PlaceRoom logs a warning about exactly " +
                                 "this and then uses the PARENT-relative transform anyway, so " +
                                 "localPosition/localRotation are the values the game applies, not " +
                                 "roomPosition/roomRotation.");
                }
            }
            d.connections = defs.ToArray();
        }

        private static string Short(Exception e) { return e.GetType().Name + ": " + e.Message; }
    }
}
