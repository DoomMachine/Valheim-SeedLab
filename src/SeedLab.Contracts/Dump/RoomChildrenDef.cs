namespace SeedLab.Contracts.Dump
{
    /// <summary>
    /// <c>roomchildren.json</c> - what lives INSIDE each dungeon/camp ROOM prefab.
    ///
    /// <b>Why this file exists.</b> A location prefab is not the whole world. Everything inside a crypt,
    /// a cave, a Fuling camp or a Meadows village is built from ROOM prefabs, which live in
    /// <c>DungeonDB</c> and never appear as children of a location prefab. Until 2026-09-23 the dump
    /// walked location prefabs only, so "which location contains <c>piece_maypole</c>" could come back
    /// empty for a piece that is authored in a room - and an empty answer reads as "this world has no
    /// maypole", the worst wrong answer this dump can give. <see cref="SearchFile"/> now resolves a
    /// sought name across both, and says NOT FOUND in as many words when it is in neither.
    ///
    /// <b>How the game gets here.</b> <c>DungeonDB.Start</c> runs <c>SetupRooms</c>, which instantiates
    /// every prefab in <c>m_roomLists</c> and concatenates each resulting <c>RoomList.m_rooms</c> into
    /// the private <c>m_rooms</c>; <c>GenerateHashList</c> then keys them by
    /// <c>m_prefab.Name.GetStableHashCode()</c>, <b>logging an error and dropping any later duplicate</b>.
    /// <c>DungeonGenerator.SetupAvailableRooms</c> filters that list with
    /// <c>(room.m_theme &amp; m_themes) != None &amp;&amp; room.m_enabled</c> - note that it reads the
    /// <c>RoomData</c>'s theme and enabled flag, <b>not</b> the <c>Room</c> component's
    /// (verified from IL, 2026-09-23). Both pairs are recorded here so a disagreement is visible.
    ///
    /// <b>The stream.</b> <c>DungeonGenerator.PlaceRoom(RoomData, pos, rot, fromConnection, mode)</c>
    /// does exactly what <c>SpawnLocation</c> does to the three component arrays, with its own seed:
    /// <code>
    /// Vector3 v = m_useCustomInteriorTransform ? pos - generator.transform.position : pos;
    /// int seed  = (int)v.x * 4271 + (int)v.y * 9187 + (int)v.z * 2134;
    /// if (m_addBaseSeedToRandomSpawn) seed += GetSeed();
    /// Random.State saved = Random.state;      // the ambient stream is restored afterwards
    /// Random.InitState(seed);
    /// foreach (RandomSpawn s in randSpawns)  s.Randomize(pos + rot * prefabPosition, null, this);
    /// foreach (RandomObject o in randObjs)   o.Randomize(pos + rot * prefabPosition, null, this);
    /// </code>
    /// So the draw budget is the same shape as a location's - one <c>Random.Range(0f,100f)</c> per
    /// RandomSpawn in array order, then one weighted draw per RandomObject - but the seed is derived
    /// from the room's placement position, and <c>pos</c> comes out of the dungeon layout, which is
    /// itself rolled once from <c>DungeonGenerator.GetSeed()</c> and then SAVED to the generator's ZDO.
    /// A layout is never re-rolled, so an offline reader that has not reproduced the layout cannot say
    /// where a room landed - and therefore cannot say what its RandomSpawns rolled. <b>This file states
    /// what a room CAN contain, and under which gate; it does not predict a particular dungeon.</b>
    ///
    /// <b>Anchor.</b> <c>PlaceRoom</c> measures children from the <c>Room</c> COMPONENT's transform,
    /// not from the asset root: <c>Inverse(room.rotation) * (child.position - room.position)</c>.
    /// <see cref="roomComponentOnRoot"/> says whether the two are the same object, which they normally
    /// are. <see cref="InteriorDef.rootPosition"/> / <see cref="InteriorDef.rootRotation"/> hold the
    /// anchor actually used.
    /// </summary>
    public sealed class RoomChildrenFile
    {
        public string? stamp;
        public int schema;

        /// <summary>Number of entries in <see cref="rooms"/>: the length of <c>DungeonDB.GetRooms()</c>
        /// at dump time, in that list's own order.</summary>
        public int count;

        public int loadedCount;
        public int failedCount;

        /// <summary>How many entries have <c>RoomData.m_enabled</c> true - the ones
        /// <c>SetupAvailableRooms</c> can pick.</summary>
        public int enabledCount;

        public int totalRandomSpawns;
        public int totalRandomObjects;
        public int totalContainers;

        /// <summary>See <see cref="LocationChildrenFile.maxNamesPerEntry"/>.</summary>
        public int maxNamesPerEntry;

        /// <summary><c>DungeonDB.m_roomLists</c>: the RoomList PREFABS the DB instantiates in
        /// <c>SetupRooms</c>, by name, in list order. Recorded because it is the authored input; the
        /// instantiated result is <see cref="roomLists"/>.</summary>
        public string[]? roomListPrefabNames;

        /// <summary><c>DungeonDB.m_roomScenes</c>, verbatim. Empty in 1.0.15 as shipped, recorded so a
        /// future build that fills it is noticed.</summary>
        public string[]? roomScenes;

        /// <summary>The instantiated <c>RoomList</c>s, from <c>RoomList.GetAllRoomLists()</c>, in that
        /// static list's order. Each room in <see cref="rooms"/> names the list it came from.</summary>
        public RoomListDef[]? roomLists;

        /// <summary><c>DungeonDB.m_roomByHash.Count</c>, read by reflection. Smaller than
        /// <see cref="count"/> means <c>GenerateHashList</c> dropped duplicates - see
        /// <see cref="RoomChildrenDef.duplicateOfIndex"/>.</summary>
        public int roomByHashCount;

        /// <summary>Non-null when the room walk could not run at all (no <c>DungeonDB.instance</c>, or
        /// the config switch was off). When this is set, <see cref="rooms"/> is empty and every
        /// "not found" verdict that depended on rooms is INCONCLUSIVE - <see cref="SearchFile"/> says
        /// so per sought name.</summary>
        public string? skipped;

        public RoomChildrenDef[]? rooms;
    }

    /// <summary>One instantiated <c>RoomList</c> - the grouping that says which family a room belongs
    /// to (one list per dungeon/camp kind in the shipped data).</summary>
    public sealed class RoomListDef
    {
        /// <summary>Index in <c>RoomList.GetAllRoomLists()</c>.</summary>
        public int index;

        /// <summary>The GameObject's name VERBATIM. <c>SetupRooms</c> <c>Instantiate</c>s the list
        /// prefabs, so expect a trailing <c>(Clone)</c>.</summary>
        public string? name;

        /// <summary><c>Utils.GetPrefabName(name)</c> - the authored list name with <c>(Clone)</c>
        /// removed.</summary>
        public string? normalizedName;

        /// <summary><c>m_rooms.Count</c>.</summary>
        public int roomCount;

        /// <summary>The bitwise OR of every member room's <c>RoomData.m_theme</c>: which themes, and so
        /// which <c>DungeonGenerator</c>s, this list can serve.</summary>
        public int themeMask;
    }

    /// <summary>One room prefab's interior, plus the metadata that says which dungeon or camp can
    /// contain it.</summary>
    public sealed class RoomChildrenDef : InteriorDef
    {
        /// <summary>Index in <c>DungeonDB.GetRooms()</c>.</summary>
        public int index;

        /// <summary><c>RoomData.Hash</c> = <c>m_prefab.Name.GetStableHashCode()</c>, the key
        /// <c>DungeonDB.GetRoom(int)</c> and the dungeon save data use.</summary>
        public int hash;

        /// <summary>Index of the EARLIER entry that already owns <see cref="hash"/>, or -1.
        /// <c>GenerateHashList</c> keeps the first and logs an error for the rest, so a room with a
        /// non-negative value here is unreachable through <c>GetRoom(hash)</c> - though
        /// <c>SetupAvailableRooms</c>, which walks the list rather than the dictionary, would still pick
        /// it. Its interior is walked anyway.</summary>
        public int duplicateOfIndex;

        // ---- which dungeon or camp can contain it ---------------------------------------------------

        /// <summary>Index in <see cref="RoomChildrenFile.roomLists"/> of the <c>RoomList</c> this room
        /// came from, or -1 when it could not be attributed.</summary>
        public int roomListIndex;

        /// <summary>That list's normalised name, or null.</summary>
        public string? roomListName;

        /// <summary><c>RoomData.m_theme</c> as the raw <c>Room.Theme</c> bitmask. <b>This is the field
        /// the generator filters on</b>: <c>SetupAvailableRooms</c> tests
        /// <c>(room.m_theme &amp; m_themes) != None</c>.</summary>
        public int roomDataTheme;

        /// <summary><c>RoomData.m_enabled</c>. <b>This is the flag the generator filters on</b>, not
        /// <see cref="roomEnabled"/>.</summary>
        public bool roomDataEnabled;

        // ---- the Room component's own fields ---------------------------------------------------------

        /// <summary>True when the prefab has a <c>Room</c> component at all. False is a data problem:
        /// <c>PlaceRoom</c> dereferences <c>asset.GetComponent&lt;Room&gt;()</c> unguarded.</summary>
        public bool hasRoomComponent;

        /// <summary>True when the <c>Room</c> component sits on the asset ROOT, so the anchor
        /// <c>PlaceRoom</c> measures from is the root transform and
        /// <see cref="RandomSpawnDef.prefabPosition"/> means the same thing it does for a location.</summary>
        public bool roomComponentOnRoot;

        /// <summary>Path of the <c>Room</c> component from the asset root, or null when it is the root
        /// (or absent).</summary>
        public string? roomComponentPath;

        /// <summary><c>Room.m_size</c>, the room's grid footprint. <c>PlaceRoom</c> skips the collision
        /// test entirely when <c>m_size.x == 0 || m_size.z == 0</c>.</summary>
        public Vec3IntDef? size;

        /// <summary><c>Room.m_theme</c> on the component. Normally equal to
        /// <see cref="roomDataTheme"/>; when it is not, the generator still uses the RoomData one.</summary>
        public int roomTheme;

        /// <summary><c>Room.m_enabled</c> on the component. Normally equal to
        /// <see cref="roomDataEnabled"/>; the generator uses the RoomData one.</summary>
        public bool roomEnabled;

        /// <summary><c>Room.m_entrance</c>: a candidate for <c>FindStartRoom</c>.</summary>
        public bool entrance;

        /// <summary><c>Room.m_endCap</c>: used to seal an open connection rather than extend the
        /// layout.</summary>
        public bool endCap;

        public bool divider;
        public int endCapPrio;
        public int minPlaceOrder;

        /// <summary><c>Room.m_weight</c>: the weight in <c>GetWeightedRoom</c>'s draw.</summary>
        public float weight;

        public bool faceCenter;

        /// <summary><c>Room.m_perimeter</c>: used by the camp algorithms' wall pass.</summary>
        public bool perimeter;

        /// <summary><c>Room.m_placeOrder</c> as AUTHORED on the shared asset. <c>PlaceRoom</c> writes
        /// this field on the INSTANCE it creates (<c>fromConnection.m_placeOrder + 1</c>), never on the
        /// asset, so the value here is the prefab's default and not a placement result.</summary>
        public int placeOrder;

        /// <summary><c>Room.m_seed</c> as AUTHORED. Like <see cref="placeOrder"/>, <c>PlaceRoom</c>
        /// writes the real per-placement seed on the instance, not here.</summary>
        public int seed;

        // ---- connections, captured from 2026-09-23 onward --------------------------------------------

        /// <summary>
        /// False on every file written before 2026-09-23, where <see cref="connections"/> does not
        /// exist and deserialises to null. An empty array and "not captured" are different facts - a
        /// room with no connections cannot be placed by the Dungeon algorithm at all - so a reader that
        /// needs connections must refuse on false rather than read the empty array as an answer.
        /// </summary>
        public bool connectionsCaptured;

        /// <summary>
        /// The <c>RoomConnection</c> components under the <c>Room</c>, in
        /// <c>GetComponentsInChildren&lt;RoomConnection&gt;(includeInactive: false)</c> order - the same
        /// call, on the same object, that <c>Room.GetConnections()</c> makes, so index 0 here is the
        /// index 0 the game sees. Order is semantic three times over: <c>GetEntrance</c> returns the
        /// FIRST entrance, <c>GetConnection</c> collects the same-type ones in this order and picks with
        /// <c>Random.Range(0, count)</c>, and <c>PlaceEndCaps</c> walks <c>m_openConnections</c> in the
        /// order <c>AddOpenConnections</c> appended them.
        ///
        /// <b>Not called through <c>Room.GetConnections()</c></b>, which caches its result into the
        /// component's private <c>m_roomConnections</c> on a shared asset that the game reuses for the
        /// rest of the session.
        ///
        /// <b>Where the index parity holds, and where it does not.</b> It holds for every lookup the
        /// game makes on the ASSET - <c>GetConnection</c>, <c>HaveConnection</c>, <c>GetEntrance</c>.
        /// It is not guaranteed for <c>m_openConnections</c>: <c>AddOpenConnections</c> walks the
        /// CLONE's connections, and by then <c>PlaceRoom</c> has deactivated every
        /// <c>ZNetView</c>-bearing object on the asset and <c>Randomize</c> has deactivated the
        /// subtrees of every RandomSpawn/RandomObject that did not spawn. A connection parented under
        /// one of those would be absent from that walk. Each entry's <see cref="RoomConnectionDef.path"/>
        /// is recorded so the question can be settled against <c>childNames</c> from the dump itself.
        /// </summary>
        public RoomConnectionDef[]? connections;
    }

    /// <summary>
    /// One <c>RoomConnection</c>: where a room can attach to another, and on what terms.
    ///
    /// <b>The placement arithmetic.</b> <c>DungeonGenerator.CalculateRoomPosRot</c> is
    /// <code>
    /// rot = exitRot * Quaternion.Inverse(roomCon.transform.localRotation);
    /// pos = exitPos - rot * roomCon.transform.localPosition;
    /// </code>
    /// It reads <b>local</b> position and rotation - relative to the connection's PARENT, which is
    /// normally the room root but is not guaranteed to be (the game logs a warning and carries on).
    /// <see cref="localPosition"/> / <see cref="localRotation"/> are therefore the values the game
    /// actually uses, and <see cref="roomPosition"/> / <see cref="roomRotation"/> are the same point
    /// measured from the <c>Room</c> anchor, which is what a reader wants when the two differ.
    /// </summary>
    public sealed class RoomConnectionDef
    {
        /// <summary>Index in the room's connection array.</summary>
        public int index;

        /// <summary>Transform path from the asset root.</summary>
        public string? path;

        /// <summary>The GameObject's name verbatim.</summary>
        public string? name;

        /// <summary><c>transform.localPosition</c> - relative to the PARENT, which is what
        /// <c>CalculateRoomPosRot</c> uses.</summary>
        public Vec3Def? localPosition;

        /// <summary><c>transform.localRotation</c> - relative to the PARENT.</summary>
        public QuatDef? localRotation;

        /// <summary>The connection's position expressed in the <c>Room</c> anchor's frame:
        /// <c>Inverse(room.rotation) * (connection.position - room.position)</c>. Equal to
        /// <see cref="localPosition"/> whenever the connection is a direct child of a room that sits at
        /// the asset root, which is the normal case.</summary>
        public Vec3Def? roomPosition;

        /// <summary>The connection's rotation in the <c>Room</c> anchor's frame.</summary>
        public QuatDef? roomRotation;

        /// <summary>False when the connection is NOT a direct child of the <c>Room</c>'s GameObject.
        /// The game itself warns about this in <c>PlaceRoom</c> ("is not placed as a direct child of
        /// room!") and then uses the parent-relative transform anyway, so the warning marks a case
        /// where <see cref="localPosition"/> and <see cref="roomPosition"/> disagree and the game
        /// follows the former.</summary>
        public bool directChildOfRoom;

        /// <summary><c>m_type</c>. Matched by exact string equality in <c>GetConnection</c>,
        /// <c>HaveConnection</c> and <c>FindDoorType</c>.</summary>
        public string? type;

        /// <summary><c>m_entrance</c>: <c>GetEntrance</c> returns the first of these, and
        /// <c>AddOpenConnections</c> never re-opens one.</summary>
        public bool entrance;

        /// <summary><c>m_allowDoor</c>, default true.</summary>
        public bool allowDoor;

        /// <summary><c>m_doorOnlyIfOtherAlsoAllowsDoor</c>.</summary>
        public bool doorOnlyIfOtherAlsoAllowsDoor;

        /// <summary><c>gameObject.activeSelf</c>. The game's own lookup passes
        /// <c>includeInactive: false</c>, so an inactive connection is invisible to it; such a
        /// connection is not written here at all, and this field exists to make that visible if the
        /// filter ever changes.</summary>
        public bool activeSelf;
    }

    /// <summary>A <c>UnityEngine.Vector3Int</c>.</summary>
    public sealed class Vec3IntDef
    {
        public int x;
        public int y;
        public int z;
    }
}
