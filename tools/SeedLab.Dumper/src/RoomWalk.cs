using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SeedLab.Contracts.Dump;
using SoftReferenceableAssets;
using UnityEngine;

namespace SeedLab.Dumper
{
    /// <summary>
    /// The ROOM prefab walk: <c>roomchildren.json</c>.
    ///
    /// <b>Why it exists.</b> The location prefab walk answers "what is inside a location", and for a
    /// long time that was assumed to be the whole question. It is not. A location that has an interior -
    /// a crypt, a cave, a Fuling camp, a Meadows village - contains a <c>DungeonGenerator</c> and
    /// nothing else; the actual contents are ROOM prefabs that live in <c>DungeonDB</c> and are
    /// instantiated at generation time. Nothing in a location prefab's child tree names them. So a
    /// question like "which prefab contains <c>piece_maypole</c>" could come back empty from a complete,
    /// correct location walk - and an empty answer reads as "this game has no maypole", which is the
    /// worst wrong answer this dump can give. This walk closes that gap, and
    /// <see cref="SearchIndex"/> states the verdict either way.
    ///
    /// <b>How the rooms are reached.</b> <c>DungeonDB.Start</c> instantiates every prefab in
    /// <c>m_roomLists</c> and concatenates each resulting <c>RoomList.m_rooms</c> into its private
    /// <c>m_rooms</c>. The walk reads what is ACTUALLY there rather than assuming which field is
    /// populated: <c>DungeonDB.GetRooms()</c> (public static, returns that private list) for the rooms,
    /// <c>RoomList.GetAllRoomLists()</c> for the grouping, <c>m_roomByHash</c> by reflection for the
    /// hash dictionary's size, and <c>m_roomLists</c> / <c>m_roomScenes</c> for the authored inputs.
    /// All four are written to the file, so a reader can see which of them the build actually used.
    ///
    /// <b>What is deliberately not touched.</b> <c>DungeonDB.RoomData.RoomInPrefab</c> is never read:
    /// it caches into the RoomData's private <c>m_loadedRoom</c> field and logs an error when the asset
    /// is not resident. <c>Room.GetConnections()</c> / <c>GetEntrance()</c> are never called either -
    /// they populate the component's private connection array on a shared asset. The walk loads the
    /// asset itself, calls <c>GetComponent&lt;Room&gt;()</c>, and balances every <c>Load</c> with a
    /// <c>Release</c>.
    ///
    /// <b>Safety.</b> One room per frame inside a <see cref="RandomGuard"/>, with the solo/no-peers
    /// re-check between every two rooms - the same discipline as the location child walk, and for the
    /// same reason: a guard must never span a <c>yield</c>, so one room's walk is the finest
    /// interruptible unit available.
    /// </summary>
    internal static class RoomWalk
    {
        public static IEnumerator Run(RoomChildrenFile file, SearchIndex search, Terminal term,
                                      AbortFlag abort)
        {
            file.maxNamesPerEntry = ChildWalk.MaxNamesPerEntry;
            file.roomListPrefabNames = new string[0];
            file.roomScenes = new string[0];
            file.roomLists = new RoomListDef[0];
            file.rooms = new RoomChildrenDef[0];

            DungeonDB db = null;
            try { db = DungeonDB.instance; } catch { }
            if (db == null)
            {
                // GetRooms() is static and dereferences m_instance without a null check, so this test
                // must come first - and it is a real case: DungeonDB lives in the 'main' scene and its
                // Start() has to have run.
                file.skipped = "DungeonDB.instance was null, so no room prefab could be reached. It " +
                               "exists only in the 'main' scene and only after its Start() has run - " +
                               "load a world and let it finish loading before dumping.";
                if (search != null) search.RoomWalkSkipped = "DungeonDB.instance was null";
                Plugin.Say(term, "rooms: SKIPPED - DungeonDB.instance is null. Any 'not found' verdict " +
                                 "in search.json is INCONCLUSIVE.");
                yield break;
            }

            List<DungeonDB.RoomData> rooms = null;
            try { rooms = DungeonDB.GetRooms(); } catch (Exception e)
            {
                file.skipped = "DungeonDB.GetRooms() failed: " + e.GetType().Name + ": " + e.Message;
            }
            if (rooms == null)
            {
                if (file.skipped == null) file.skipped = "DungeonDB.GetRooms() returned null.";
                if (search != null) search.RoomWalkSkipped = file.skipped;
                Plugin.Say(term, "rooms: SKIPPED - " + file.skipped);
                yield break;
            }

            // ---- the authored inputs, recorded before anything is loaded -------------------------
            RandomStateSafe.Trace("rooms: the authored inputs, recorded before anything is loaded");
            file.roomListPrefabNames = NamesOf(db.m_roomLists);
            file.roomScenes = db.m_roomScenes != null ? db.m_roomScenes.ToArray() : new string[0];
            file.roomByHashCount = RoomByHashCount(db);

            // ---- the instantiated RoomLists, and which list each room came from --------------------
            RandomStateSafe.Trace("rooms: the instantiated RoomLists, and which list each room came from");
            var listOf = new Dictionary<DungeonDB.RoomData, int>();
            var listDefs = new List<RoomListDef>();
            try
            {
                List<RoomList> all = RoomList.GetAllRoomLists();
                if (all != null)
                {
                    for (int i = 0; i < all.Count; i++)
                    {
                        RoomList rl = all[i];
                        if (rl == null) continue;
                        string raw = rl.gameObject != null ? rl.gameObject.name : null;
                        int mask = 0;
                        int n = 0;
                        if (rl.m_rooms != null)
                        {
                            n = rl.m_rooms.Count;
                            foreach (DungeonDB.RoomData rd in rl.m_rooms)
                            {
                                if (rd == null) continue;
                                mask |= (int)rd.m_theme;
                                // First list wins if a RoomData were somehow shared; recorded rather
                                // than overwritten so the attribution is stable.
                                if (!listOf.ContainsKey(rd)) listOf[rd] = i;
                            }
                        }
                        listDefs.Add(new RoomListDef
                        {
                            index = i,
                            name = raw,
                            // SetupRooms Instantiates the list prefabs, so the raw name ends in
                            // "(Clone)". The normalised one is the authored list name.
                            normalizedName = Names.Normalize(raw),
                            roomCount = n,
                            themeMask = mask,
                        });
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("RoomList.GetAllRoomLists() failed (" + e.Message + "); rooms " +
                                      "will not name the list they came from.");
            }
            file.roomLists = listDefs.ToArray();

            // ---- the walk -----------------------------------------------------------------------
            RandomStateSafe.Trace("rooms: the walk");
            var defs = new List<RoomChildrenDef>(rooms.Count);
            var firstByHash = new Dictionary<int, int>();
            var watch = new Watch();
            int loaded = 0, failed = 0, enabled = 0;
            int spawnTotal = 0, objectTotal = 0, containerTotal = 0;

            for (int i = 0; i < rooms.Count; i++)
            {
                RoomChildrenDef d;
                using (RandomGuard.Capture("rooms: room walk"))
                {
                    d = ReadRoom(rooms[i], i, listOf, listDefs, firstByHash, search);
                }
                defs.Add(d);
                if (d.loaded) loaded++; else failed++;
                if (d.roomDataEnabled) enabled++;
                spawnTotal += d.randomSpawnCount;
                objectTotal += d.randomObjectCount;
                containerTotal += d.containerCount;

                if (defs.Count % 20 == 0 || defs.Count == rooms.Count)
                {
                    Plugin.Say(term, "room walk: " + defs.Count + "/" + rooms.Count);
                }
                yield return null;

                if (ModeAssets.Aborted(term, ref watch))
                {
                    abort.Requested = true;
                    Plugin.Log.LogWarning("The room walk stopped after " + defs.Count + " of " +
                                          rooms.Count + " rooms; " + DumpFormat.RoomChildrenFile +
                                          " was not written.");
                    yield break;
                }
            }

            file.count = defs.Count;
            file.loadedCount = loaded;
            file.failedCount = failed;
            file.enabledCount = enabled;
            file.totalRandomSpawns = spawnTotal;
            file.totalRandomObjects = objectTotal;
            file.totalContainers = containerTotal;
            file.rooms = defs.ToArray();

            if (search != null)
            {
                search.RoomWalkRan = true;
                search.RoomPrefabsWalked = defs.Count;
                search.RoomPrefabsLoaded = loaded;
                search.RoomPrefabsFailed = failed;
            }

            Plugin.Say(term, "room walk done: " + loaded + " loaded, " + failed + " failed, " +
                             enabled + " enabled, " + spawnTotal + " RandomSpawns, " + objectTotal +
                             " RandomObjects, " + containerTotal + " containers.");
        }

        /// <summary>
        /// One room: load, walk, release. The <c>loadCalled</c> discipline is the same as the location
        /// walk's - a <c>Release</c> that was never matched by a <c>Load</c> would decrement a
        /// reference count this plugin never took and corrupt the asset loader's bookkeeping for the
        /// rest of the session.
        /// </summary>
        private static RoomChildrenDef ReadRoom(DungeonDB.RoomData rd, int index,
                                                Dictionary<DungeonDB.RoomData, int> listOf,
                                                List<RoomListDef> listDefs,
                                                Dictionary<int, int> firstByHash,
                                                SearchIndex search)
        {
            if (rd == null)
            {
                RoomChildrenDef nullDef = ChildWalk.FailedRoom(null, null,
                    "DungeonDB.GetRooms() contained a null entry at index " + index + ".", false);
                nullDef.index = index;
                return nullDef;
            }

            string prefabName = null;
            AssetIdDef assetId = null;
            SoftReference<GameObject> reference = rd.m_prefab;
            try
            {
                prefabName = reference.Name;
                assetId = Snapshot.AssetId(reference.m_assetID);
            }
            catch (Exception e)
            {
                RoomChildrenDef bad = ChildWalk.FailedRoom(prefabName, assetId,
                    "SoftReference unreadable: " + e.Message, false);
                Describe(bad, rd, index, listOf, listDefs, firstByHash);
                return bad;
            }

            int hash = 0;
            try { hash = string.IsNullOrEmpty(prefabName) ? 0 : prefabName.GetStableHashCode(); } catch { }

            RoomChildrenDef d = null;
            bool loadCalled = false;
            try
            {
                reference.Load();
                loadCalled = true;
                GameObject asset = reference.Asset;
                if (asset == null)
                {
                    d = ChildWalk.FailedRoom(prefabName, assetId, "Load() returned no asset.", false);
                }
                else
                {
                    // GetComponent on the asset we loaded ourselves - never RoomData.RoomInPrefab,
                    // which caches into the RoomData's private m_loadedRoom on a shared object.
                    Room room = asset.GetComponent<Room>();
                    string listName = null;
                    int li;
                    if (listOf.TryGetValue(rd, out li) && li >= 0 && li < listDefs.Count)
                    {
                        listName = listDefs[li].normalizedName;
                    }
                    d = ChildWalk.ReadRoom(asset, room, prefabName, assetId, search,
                                           listName, (int)rd.m_theme, rd.m_enabled);
                }
            }
            catch (Exception e)
            {
                d = ChildWalk.FailedRoom(prefabName, assetId,
                                         e.GetType().Name + ": " + e.Message, loadCalled);
            }
            finally
            {
                if (loadCalled)
                {
                    // Skipping this would hold every room prefab resident for the rest of the session.
                    try { reference.Release(); }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning("Release failed for room " + prefabName + ": " + e.Message);
                    }
                }
            }

            if (d == null)
            {
                d = ChildWalk.FailedRoom(prefabName, assetId,
                                         "the room walk produced no entry for this prefab", loadCalled);
            }
            d.hash = hash;
            Describe(d, rd, index, listOf, listDefs, firstByHash);
            if (d.error != null && d.loaded && search != null) search.NoteInteriorWalkError();
            return d;
        }

        /// <summary>The metadata that says which dungeon or camp can contain this room, filled whether
        /// or not the asset loaded - so even a failed entry still names its list, theme and enabled
        /// flag.</summary>
        private static void Describe(RoomChildrenDef d, DungeonDB.RoomData rd, int index,
                                     Dictionary<DungeonDB.RoomData, int> listOf,
                                     List<RoomListDef> listDefs,
                                     Dictionary<int, int> firstByHash)
        {
            d.index = index;
            if (rd != null)
            {
                d.roomDataTheme = (int)rd.m_theme;
                d.roomDataEnabled = rd.m_enabled;
                int li;
                if (listOf.TryGetValue(rd, out li))
                {
                    d.roomListIndex = li;
                    if (li >= 0 && li < listDefs.Count) d.roomListName = listDefs[li].normalizedName;
                }
            }

            // DungeonDB.GenerateHashList keeps the FIRST room for a hash and logs an error for the
            // rest, so a later duplicate is unreachable through GetRoom(hash) - though
            // SetupAvailableRooms walks the list, not the dictionary, and would still pick it. Both
            // facts matter to a reader, so the link is recorded rather than the entry dropped.
            d.duplicateOfIndex = -1;
            if (d.hash != 0)
            {
                int first;
                if (firstByHash.TryGetValue(d.hash, out first)) d.duplicateOfIndex = first;
                else firstByHash[d.hash] = index;
            }
        }

        private static string[] NamesOf(List<GameObject> list)
        {
            if (list == null) return new string[0];
            var names = new List<string>(list.Count);
            foreach (GameObject g in list) names.Add(g != null ? g.name : null);
            return names.ToArray();
        }

        /// <summary><c>DungeonDB.m_roomByHash.Count</c>. Private, so reflected - and a failure is
        /// reported as -1 rather than as 0, which would look like an empty dictionary.</summary>
        private static int RoomByHashCount(DungeonDB db)
        {
            try
            {
                FieldInfo f = AccessTools.Field(typeof(DungeonDB), "m_roomByHash");
                if (f == null) return -1;
                var dict = f.GetValue(db) as ICollection;
                return dict != null ? dict.Count : -1;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("DungeonDB.m_roomByHash could not be read (" + e.Message + ").");
                return -1;
            }
        }
    }
}
