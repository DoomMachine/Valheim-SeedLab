using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using SeedLab.Contracts.Dump;
using SoftReferenceableAssets;
using UnityEngine;

namespace SeedLab.Dumper
{
    /// <summary>
    /// Mode A: the asset tables and this world's generator state. Pure reads - it constructs nothing,
    /// swaps no static, and must draw nothing at all from <c>UnityEngine.Random</c>.
    ///
    /// <b>A <see cref="RandomGuard"/> must never span a <c>yield</c>.</b> The guard restores the state
    /// it saved, so one held across frames would silently undo every draw the game itself made in
    /// between - weather, effects, anything. Every guard here wraps a synchronous block inside one
    /// frame, and the blocks that are supposed to draw nothing are checked with
    /// <see cref="NoDrawCheck"/> instead.
    ///
    /// <b>The solo rule is re-checked WHILE it runs</b> (2026-09-23). The first live run took about 8
    /// seconds end to end (LogOutput.log, 2026-09-22 01:49:54 -> 01:50:01, the 186-prefab walk
    /// included), and <c>grid=full12</c> turns that into minutes. Seconds are enough: a peer can
    /// connect inside them, and the coroutine outlives a scene change because BepInEx's manager object
    /// is <c>DontDestroyOnLoad</c>. A <see cref="Watch"/> re-asks
    /// <see cref="Safety.MultiplayerBreach"/> and <see cref="Safety.SoloHostWithWorld"/> at every
    /// section boundary, inside the prefab walk and inside the grid job; a session that stops being a
    /// solo hosted world - a peer connecting, a quit to the menu - stops the dump on the spot.
    /// </summary>
    internal static class ModeAssets
    {
        public static IEnumerator Run(DumpWriter w, Terminal term, string gridId)
        {
            string stamp = GameInfo.Stamp("assets");
            var notes = new List<string>();

            // Re-checked here, not only in the command handler: a peer can connect between the two,
            // and by this point the conditions were already confirmed once - so anything wrong now is
            // a change of conditions and earns the hard refusal.
            string reason;
            if (Safety.MultiplayerBreach(out reason) || !Safety.SoloHostWithWorld(out reason))
            {
                Plugin.Refuse(term, "the asset dump", reason);
                yield break;
            }

            // The generator must be ALIVE before a single byte is dumped (2026-09-23). Four zero words
            // is a fixed point of Unity's xorshift128: it means something has already killed the global
            // stream, every ambient draw in this session is returning 0, and anything this dump reads
            // that was produced by a random draw is garbage. Refuse rather than record it.
            if (RandomStateSafe.CurrentIsZero())
            {
                Plugin.Refuse(term, "the asset dump",
                    "UnityEngine.Random reads as all zeros, which is a fixed point of its xorshift128 " +
                    "generator - every random draw in this session is dead (a new world would offer the " +
                    "seed \"aaaaaaaaaa\"). Something has already broken the generator, so this dump would " +
                    "be unverifiable. Restart the game and dump again.");
                yield break;
            }
            RandomStateSafe.Trace("assets: START");

            // ... and kept re-checked from here on, at every section boundary below. See Aborted().
            var watch = new Watch();

            // The sought-name search is set up BEFORE anything is loaded, because every prefab the walk
            // touches is Released again immediately afterwards: a name that is decided on later cannot
            // be looked for at all without a second pass over the whole asset set. It accumulates hits
            // as the walks go and writes search.json at the end, found or not found.
            SearchIndex search = null;
            try { search = SearchIndex.FromConfig(Plugin.SoughtPrefabNames.Value); }
            catch (Exception e)
            {
                notes.Add("SoughtPrefabNames could not be parsed (" + e.Message + "); " +
                          DumpFormat.SearchFile + " was not written.");
            }

            ZoneSystem zs = ZoneSystem.instance;
            if (zs == null)
            {
                throw new InvalidOperationException(
                    "ZoneSystem.instance is null. It exists only in the 'main' scene, so a world must " +
                    "be loaded - the main menu is not enough.");
            }
            World world = ZNet.World;

            // ---- locations -------------------------------------------------------------------
            RandomStateSafe.Trace("assets: locations");
            LocationTableFile locTable = null;
            List<ZoneSystem.ZoneLocation> ordered = null;
            List<ZoneSystem.ZoneLocation> orderedCapture = null;
            NoDrawCheck.Around("locations snapshot", () =>
            {
                locTable = Snapshot.Locations(zs, stamp, out orderedCapture);
            });
            ordered = orderedCapture;
            w.WriteJson(DumpFormat.LocationsFile, locTable);
            if (search != null) NoteLocationTable(search, locTable);
            Plugin.Say(term, "locations: " + locTable.count + " entries, " + locTable.enabledCount + " enabled.");
            if (locTable.duplicateHashPrefabNames != null && locTable.duplicateHashPrefabNames.Length > 0)
            {
                notes.Add("Duplicate ZoneLocation.Hash values; SetupLocations keeps the first and " +
                          "ignores the rest: " + string.Join(", ", locTable.duplicateHashPrefabNames));
            }
            yield return null;
            if (Aborted(term, ref watch)) yield break;

            // ---- vegetation ------------------------------------------------------------------
            RandomStateSafe.Trace("assets: vegetation");
            VegetationTableFile vegTable = null;
            NoDrawCheck.Around("vegetation snapshot", () => { vegTable = Snapshot.Vegetation(zs, stamp); });
            w.WriteJson(DumpFormat.VegetationFile, vegTable);
            if (search != null) NoteVegetationTable(search, vegTable);
            Plugin.Say(term, "vegetation: " + vegTable.count + " entries.");
            yield return null;
            if (Aborted(term, ref watch)) yield break;

            // ---- alt biomes ------------------------------------------------------------------
            RandomStateSafe.Trace("assets: alt biomes");
            AltBiomeTableFile altTable = null;
            NoDrawCheck.Around("altbiome snapshot", () => { altTable = Snapshot.AltBiomes(stamp); });
            w.WriteJson(DumpFormat.AltBiomesFile, altTable);
            Plugin.Say(term, "alt biomes: " + altTable.count + " (" + altTable.enabledCount + " enabled).");
            yield return null;
            if (Aborted(term, ref watch)) yield break;

            // ---- prefab and version constants -------------------------------------------------
            RandomStateSafe.Trace("assets: prefab and version constants");
            PrefabConstantsFile constants = null;
            bool sortOrderTies = false;
            bool tiesCapture = false;
            NoDrawCheck.Around("prefab constants", () =>
            {
                constants = Snapshot.PrefabConstants(zs, stamp, out tiesCapture);
            });
            sortOrderTies = tiesCapture;
            w.WriteJson(DumpFormat.PrefabConstantsFile, constants);
            w.WriteJson(DumpFormat.VersionConstantsFile, GameInfo.VersionConstants(stamp));
            if (sortOrderTies)
            {
                notes.Add("Two or more LocationLists share an m_sortOrder. SetupLocations sorts them " +
                          "with List.Sort, which is UNSTABLE, so the concatenation order of m_locations " +
                          "is not guaranteed to repeat. Treat the table order as one observation.");
            }
            if (constants != null && constants.minimap == null)
            {
                notes.Add("Minimap.instance was null; prefab-constants.json has no minimap section.");
            }
            yield return null;
            if (Aborted(term, ref watch)) yield break;

            // ---- alt-biome assignment for this world ------------------------------------------
            RandomStateSafe.Trace("assets: alt-biome assignment for this world");
            int seed = world != null ? world.m_seed : 0;
            string seedHex = WorldGenReader.SeedHex(seed);
            AltBiomeAssignmentFile assignment = null;
            NoDrawCheck.Around("altbiome assignment", () => { assignment = Assignment(world, stamp); });
            if (assignment != null)
            {
                w.WriteJson(DumpFormat.GoldensDir + "/altbiomes-assignment-" + seedHex + ".json", assignment);
                if (assignment.contaminated)
                {
                    notes.Add("AltBiome.Sectors was already non-empty when GenerateAltBiomes ran " +
                              "(it never clears them). This assignment is NOT a clean first run.");
                }
                if (AltBiomeCapture.Runs > 1)
                {
                    notes.Add("GenerateAltBiomes ran " + AltBiomeCapture.Runs + " times this session; " +
                              "only the first is clean.");
                }
            }
            else
            {
                notes.Add("No AltBiomeWorldData was available (World.m_biomeData null); the alt-biome " +
                          "assignment was not dumped. It exists only on the host, after VerifyBiomeData.");
            }
            yield return null;
            if (Aborted(term, ref watch)) yield break;

            // ---- the game's own placement output ----------------------------------------------
            RandomStateSafe.Trace("assets: the game's own placement output");
            LocationInstancesFile instances = null;
            NoDrawCheck.Around("location instances", () =>
            {
                instances = Instances(zs, world, ordered, stamp);
            });
            w.WriteJson(DumpFormat.GoldensDir + "/locationinstances-" + seedHex + ".json", instances);
            Plugin.Say(term, "location instances: " + instances.count +
                             " (" + CountPlaced(instances) + " placed).");
            if (instances.count == 0)
            {
                notes.Add("m_locationInstances is empty: location generation had not finished when the " +
                          "dump ran, or this is not the host. Re-run once the world has finished loading.");
            }
            yield return null;
            if (Aborted(term, ref watch)) yield break;

            // ---- this world's WorldGenerator ---------------------------------------------------
            RandomStateSafe.Trace("assets: this world's WorldGenerator");
            WorldGenDumpFile wgDump = null;
            WorldGenerator wg = WorldGenerator.instance;
            if (wg != null)
            {
                // ReplayConstructorDraws inside Read() does draw - it has its own guard and is fully
                // synchronous, so NoDrawCheck is deliberately NOT applied around this call.
                wgDump = WorldGenReader.Read(wg, world, stamp);
                // DumpFormat.SourceWorld: this is the generator the LOADED world is running. The
                // world-generator dump writes ...-menu.json for the same seed, and both use
                // FileMode.Create - without the source token whichever ran second would silently
                // replace the other's file while the manifest still listed one entry for it.
                WorldGenReader.WriteRiverPoints(w, wg, wgDump,
                    DumpFormat.RiverPointsFile(seedHex, DumpFormat.SourceWorld));
                w.WriteJson(DumpFormat.WorldGenFile(seedHex, DumpFormat.SourceWorld), wgDump);
                Plugin.Say(term, "worldgen: offsets " + wgDump.offset0 + "/" + wgDump.offset1 + "/" +
                                 wgDump.offset2 + "/" + wgDump.offset3 + "/" + wgDump.offset4 +
                                 ", rivers " + (wgDump.rivers != null ? wgDump.rivers.Length : 0) +
                                 ", streams " + (wgDump.streams != null ? wgDump.streams.Length : 0) + ".");
                CheckCtorTrace(wgDump, notes);

                // Sampling the loaded world's generator is safe: GetBiome / GetBiomeHeight /
                // GetBaseHeight are pure, WorldGenerator.instance is NOT replaced, and nothing here
                // touches UnityEngine.Random. Off by default because full12 is 4.2M samples.
                if (!string.IsNullOrEmpty(gridId) && gridId != "none")
                {
                    var gridFiles = new List<string>();
                    IEnumerator grid = Grids.Write(w, wg, wgDump, gridId, DumpFormat.SourceWorld, gridFiles);
                    while (grid.MoveNext())
                    {
                        yield return grid.Current;
                        // full12 is 4.2M samples and runs for minutes: the longest single stretch in
                        // this mode, and the one most likely to outlive the session it started in.
                        if (Aborted(term, ref watch)) yield break;
                    }
                    wgDump.grids = gridFiles.ToArray();
                    // Rewritten so the grid list lands in the json rather than only on disk.
                    w.WriteJson(DumpFormat.WorldGenFile(seedHex, DumpFormat.SourceWorld), wgDump);
                }
            }
            else
            {
                notes.Add("WorldGenerator.instance was null; no per-seed generator state was dumped.");
            }
            yield return null;
            if (Aborted(term, ref watch)) yield break;

            // ---- the hash corpus ---------------------------------------------------------------
            RandomStateSafe.Trace("assets: the hash corpus");
            NativesHashFile hashes = null;
            NoDrawCheck.Around("hash corpus", () => { hashes = Hashes(locTable, vegTable, altTable, world, stamp); });
            w.WriteJson(DumpFormat.GoldensDir + "/" + DumpFormat.NativesHashFile, hashes);
            yield return null;
            if (Aborted(term, ref watch)) yield break;

            // ---- the prefab walk ----------------------------------------------------------------
            // NOTE ON ORDER: this block must stay AHEAD of every walk. Names.Translations()
            // caches on first call, so whichever code asks for a name first decides the table
            // for the whole dump - and a walk that ran before Localization was ready would
            // latch an empty table while this file still reported a healthy one.
            // ---- the game's own names ---------------------------------------------------------------
            // Written BEFORE the walks, because every name they resolve comes from this table. A dump
            // whose localization failed still produces its walks; the names in them are simply null,
            // which is the difference between "this has no name" and "this is called '$enemy_gdking'".
            RandomStateSafe.Trace("assets: the localization table");
            {
                var loc = new LocalizationFile { stamp = stamp, schema = DumpFormat.Schema };
                try
                {
                    Localization inst = Localization.instance;
                    if (inst == null)
                    {
                        loc.skipped = "Localization.instance was null at dump time, so no display name "
                                      + "in this dump could be resolved.";
                        loc.translations = new System.Collections.Generic.Dictionary<string, string>();
                        loc.languages = new string[0];
                    }
                    else
                    {
                        loc.language = inst.GetSelectedLanguage();
                        var langs = inst.GetLanguages();
                        loc.languages = langs != null ? langs.ToArray() : new string[0];
                        loc.translations = Names.Translations();
                        loc.count = loc.translations.Count;

                        // The table is cached for the session; GetSelectedLanguage() is read fresh. A
                        // language change between two F4 presses would therefore label an English table
                        // as Swedish, and every localizedName in the dump would stay English. Compare
                        // and say so rather than ship a mislabelled file.
                        string cachedIn = Names.TranslationsLanguage();
                        if (!string.IsNullOrEmpty(cachedIn) && loc.language != null
                            && !string.Equals(cachedIn, loc.language, StringComparison.Ordinal))
                        {
                            loc.skipped = "the language was " + cachedIn + " when this session first "
                                          + "resolved a name and is " + loc.language + " now; the table "
                                          + "below is the " + cachedIn + " one and every localizedName "
                                          + "in this dump is " + cachedIn + ". Restart the game and dump "
                                          + "again to get " + loc.language + ".";
                            loc.language = cachedIn;
                        }
                        if (loc.count == 0)
                        {
                            loc.skipped = "Localization.m_translations read as empty; every name in "
                                          + "this dump is a raw token.";
                        }
                    }
                }
                catch (Exception e)
                {
                    loc.skipped = "the localization table could not be read: " + e.GetType().Name + ": " + e.Message;
                    loc.translations = new System.Collections.Generic.Dictionary<string, string>();
                    loc.languages = new string[0];
                }

                w.WriteJson(DumpFormat.LocalizationFile, loc);

                // READ IT BACK. The first version of this reported "6258 strings in English" into the
                // log and wrote "translations": {} - the writer reflects over public FIELDS and a
                // Dictionary has none, so it silently produced an empty object beside a healthy count.
                // A count is a claim about a file; this checks the file. The writer now refuses a type
                // it cannot serialise, and this is the second line of defence behind that.
                try
                {
                    string back = System.IO.File.ReadAllText(
                        System.IO.Path.Combine(w.Root, DumpFormat.LocalizationFile));
                    // Counted by BRACE DEPTH, not by stopping at the first '}'. The first version
                    // stopped there, which would have declared a perfectly good file broken the day any
                    // key or value contained a brace. (It could not fire on 1.0.15 - none of the 5,740
                    // keys or their English values holds one - but "cannot fire because of today's data"
                    // is luck, and this check exists precisely to catch what nobody predicted.)
                    int written = 0;
                    int at = back.IndexOf("\"translations\"", StringComparison.Ordinal);
                    if (at >= 0)
                    {
                        int open = back.IndexOf('{', at);
                        if (open >= 0)
                        {
                            int depth = 0;
                            bool inString = false, escaped = false;
                            for (int i = open; i < back.Length; i++)
                            {
                                char ch = back[i];
                                if (inString)
                                {
                                    if (escaped) escaped = false;
                                    else if (ch == '\\') escaped = true;
                                    else if (ch == '"') inString = false;
                                    continue;
                                }

                                if (ch == '"') { inString = true; continue; }
                                if (ch == '{') depth++;
                                else if (ch == '}') { depth--; if (depth == 0) break; }
                                else if (ch == ':' && depth == 1) written++;
                            }
                        }
                    }

                    if (loc.count > 0 && written < loc.count)
                    {
                        loc.skipped = "the localization table did not survive serialisation: " + loc.count
                                      + " strings were read from the game but the file carries about "
                                      + written + ". Every display name in this dump is a raw token.";
                        w.WriteJson(DumpFormat.LocalizationFile, loc);
                        Plugin.Log.LogError("SeedLab.Dumper: " + loc.skipped);
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("localization.json could not be read back to check it (" +
                                          e.Message + ").");
                }

                Plugin.Say(term, "localization: " + (loc.skipped ?? (loc.count + " strings in " + loc.language)));
                Plugin.Log.LogInfo("localization: " + (loc.skipped ?? (loc.count + " strings, language " + loc.language)));
            }

            yield return null;
            if (Aborted(term, ref watch)) yield break;

            RandomStateSafe.Trace("assets: the prefab walk");
            if (Plugin.WalkLocationPrefabs.Value)
            {
                var prefabs = new LocationPrefabsFile
                {
                    stamp = stamp,
                    schema = DumpFormat.Schema,
                };
                // Filled by the SAME pass, from the SAME SoftReference.Load(), when the child walk is
                // on. Two files, one load: loading 186 location prefabs twice would double the longest
                // stretch of the dump for nothing.
                LocationChildrenFile children = Plugin.WalkPrefabChildren.Value
                    ? new LocationChildrenFile
                      {
                          stamp = stamp,
                          schema = DumpFormat.Schema,
                          // Recorded in the file rather than left as a constant only the writer knows:
                          // a reader has to be able to tell a 64-name list that was cut from one that
                          // happened to end there.
                          maxNamesPerEntry = ChildWalk.MaxNamesPerEntry,
                      }
                    : null;

                // The longest stretch of the dump apart from a grid (about 7 of the 8 seconds the
                // first live run took), so the walk carries the abort flag and checks the rules
                // itself, a couple of prefabs at a time.
                var abort = new AbortFlag();
                IEnumerator walk = WalkPrefabs(zs, prefabs, children, term, abort, search);
                while (walk.MoveNext()) yield return walk.Current;
                if (abort.Requested) yield break;
                w.WriteJson(DumpFormat.LocationPrefabsFile, prefabs);
                if (prefabs.failedCount > 0)
                {
                    notes.Add(prefabs.failedCount + " location prefab(s) could not be loaded; see " +
                              "their 'error' field in locationprefabs.json.");
                }

                if (children != null)
                {
                    // The largest single write in the dump, serialized by a reflection walker in one
                    // frame. Say so and give the game a frame first, so the message is on screen
                    // during the hitch rather than after it.
                    Plugin.Say(term, "writing " + DumpFormat.LocationChildrenFile +
                                     " - the biggest file in the dump; the game will hitch once.");
                    yield return null;
                    if (Aborted(term, ref watch)) yield break;

                    w.WriteJson(DumpFormat.LocationChildrenFile, children);
                    // Measured, not estimated: this file is the largest in the dump and the user was
                    // told it would be reported.
                    Plugin.Say(term, "location children: " + children.totalRandomSpawns +
                                     " RandomSpawns, " + children.totalRandomObjects +
                                     " RandomObjects, " + children.totalContainers + " containers, " +
                                     DumpFormat.LocationChildrenFile + " is " +
                                     Mib(w, DumpFormat.LocationChildrenFile) + ".");
                    int warned = 0;
                    if (children.locations != null)
                    {
                        foreach (LocationChildrenDef c in children.locations)
                        {
                            if (c != null && c.warnings != null && c.warnings.Length > 0) warned++;
                        }
                    }
                    if (warned > 0)
                    {
                        notes.Add(warned + " location prefab(s) in " + DumpFormat.LocationChildrenFile +
                                  " carry a 'warnings' entry - most often an enabled-vs-present " +
                                  "component count mismatch, which means the shared prefab asset had " +
                                  "already been through a SpawnLocation this session. Read them " +
                                  "before replaying the RNG stream from this file.");
                    }
                }
                else
                {
                    notes.Add("WalkPrefabChildren was off: " + DumpFormat.LocationChildrenFile +
                              " was not written, so the RandomSpawn/RandomObject draw order and the " +
                              "Container drop tables are missing from this dump.");
                }
            }
            else
            {
                notes.Add("WalkLocationPrefabs was off: locationprefabs.json was not written, so the " +
                          "Location radii and DungeonGenerator offsets are missing from this dump. " +
                          DumpFormat.LocationChildrenFile + " was not written either - it is filled " +
                          "by the same walk.");
            }
            yield return null;
            if (Aborted(term, ref watch)) yield break;

            // ---- the dungeon/camp room prefabs -----------------------------------------------------
            RandomStateSafe.Trace("assets: the dungeon/camp room prefabs");
            // A separate walk because rooms come from DungeonDB, not from a location's child tree -
            // nothing inside a location prefab names them. Without this the dump can only ever answer
            // "which LOCATION contains X", and an interior piece answers that question with silence.
            if (Plugin.WalkRoomPrefabs.Value)
            {
                var roomFile = new RoomChildrenFile { stamp = stamp, schema = DumpFormat.Schema };
                var roomAbort = new AbortFlag();
                IEnumerator roomWalk = RoomWalk.Run(roomFile, search, term, roomAbort);
                while (roomWalk.MoveNext()) yield return roomWalk.Current;
                if (roomAbort.Requested) yield break;

                if (roomFile.skipped == null)
                {
                    // Same courtesy as locationchildren.json: the serializer builds the whole document
                    // in memory in one frame, and the user is looking at a live game. Say it before the
                    // hitch, not after it.
                    Plugin.Say(term, "writing " + DumpFormat.RoomChildrenFile +
                                     " - another large file; the game will hitch once.");
                    yield return null;
                    if (Aborted(term, ref watch)) yield break;
                }
                w.WriteJson(DumpFormat.RoomChildrenFile, roomFile);
                if (roomFile.skipped != null)
                {
                    notes.Add(DumpFormat.RoomChildrenFile + " is EMPTY: " + roomFile.skipped +
                              " Any 'not found' in " + DumpFormat.SearchFile + " is inconclusive.");
                }
                else
                {
                    Plugin.Say(term, "rooms: " + roomFile.count + " (" + roomFile.enabledCount +
                                     " enabled) in " + (roomFile.roomLists != null ? roomFile.roomLists.Length : 0) +
                                     " room list(s), " + DumpFormat.RoomChildrenFile + " is " +
                                     Mib(w, DumpFormat.RoomChildrenFile) + ".");
                    if (roomFile.failedCount > 0)
                    {
                        notes.Add(roomFile.failedCount + " room prefab(s) could not be loaded; see " +
                                  "their 'error' field in " + DumpFormat.RoomChildrenFile + ".");
                    }
                    if (roomFile.roomByHashCount >= 0 && roomFile.roomByHashCount != roomFile.count)
                    {
                        notes.Add("DungeonDB has " + roomFile.count + " rooms but only " +
                                  roomFile.roomByHashCount + " distinct hashes: GenerateHashList " +
                                  "dropped the duplicates. See duplicateOfIndex in " +
                                  DumpFormat.RoomChildrenFile + ".");
                    }
                }
            }
            else if (search != null)
            {
                search.RoomWalkSkipped = "the WalkRoomPrefabs config switch was off";
                notes.Add("WalkRoomPrefabs was off: " + DumpFormat.RoomChildrenFile + " was not " +
                          "written, so nothing inside a dungeon, cave or camp was captured - and any " +
                          "'not found' in " + DumpFormat.SearchFile + " is inconclusive.");
            }
            yield return null;
            if (Aborted(term, ref watch)) yield break;

            // ---- the answer ------------------------------------------------------------------------
            RandomStateSafe.Trace("assets: the answer");
            // Written LAST, because it is the one file whose job is to summarise what every other file
            // did and did not cover. It is written even when nothing was found - especially then.
            if (search != null)
            {
                NoDrawCheck.Around("ZNetScene existence probe", () => search.ProbeZNetScene());
                SearchFile searchFile = search.Build(stamp);
                w.WriteJson(DumpFormat.SearchFile, searchFile);
                if (searchFile.verdictLines != null)
                {
                    foreach (string line in searchFile.verdictLines)
                    {
                        Plugin.Say(term, line);
                        notes.Add(line);
                    }
                    if (searchFile.verdictLines.Length == 0)
                    {
                        notes.Add("SoughtPrefabNames was empty, so " + DumpFormat.SearchFile +
                                  " has no verdicts. Set it in the config before the run - the walk " +
                                  "Releases every prefab it loads, so a name chosen afterwards cannot " +
                                  "be searched for without dumping again.");
                    }
                }
            }
            yield return null;
            if (Aborted(term, ref watch)) yield break;

            // ---- manifest -----------------------------------------------------------------------
            RandomStateSafe.Trace("assets: manifest");
            // A zero UnityEngine.Random state was seen or refused while this dump ran, so the
            // generator was dead for at least part of it. Anything here that came from a random draw
            // is suspect; say so in the dump itself, not only in the log.
            if (RandomStateSafe.Poisoned)
            {
                notes.Add("UnityEngine.Random was found in an all-zero state during this dump (see the " +
                          "BepInEx log for the call site). Zero is a fixed point of Unity's xorshift128, " +
                          "so every draw returned 0 until it was re-seeded. Treat this dump as UNVERIFIED " +
                          "and take it again from a freshly started game.");
            }

            var manifest = new DumpManifest
            {
                stamp = stamp,
                schema = DumpFormat.Schema,
                game = GameInfo.Read(),
                dumper = new DumperInfoDef
                {
                    version = Plugin.VERSION,
                    mode = "assets",
                    utc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    guid = Plugin.GUID,
                },
                world = new WorldInfoDef
                {
                    name = world != null ? world.m_name : null,
                    seedText = world != null ? world.m_seedName : null,
                    seed = seed,
                    worldGenVersion = world != null ? world.m_worldGenVersion : 0,
                    worldVersion = world != null ? (int)world.m_worldVersion : 0,
                    locationVersion = zs.m_locationVersion,
                    menu = world != null && world.m_menu,
                },
                counts = new CountsDef
                {
                    locations = locTable.count,
                    locationsEnabled = locTable.enabledCount,
                    vegetation = vegTable.count,
                    vegetationEnabled = vegTable.enabledCount,
                    altBiomes = altTable.count,
                    altBiomesEnabled = altTable.enabledCount,
                    locationLists = LocationList.GetAllLocationLists().Count,
                    altBiomeLists = zs.m_altBiomeLists != null ? zs.m_altBiomeLists.Count : 0,
                    distinctLocationHashes = locTable.count -
                        (locTable.duplicateHashPrefabNames != null ? locTable.duplicateHashPrefabNames.Length : 0),
                    locationInstances = instances.count,
                    locationInstancesPlaced = CountPlaced(instances),
                },
                sortOrderTies = sortOrderTies,
                notes = notes.ToArray(),
            };
            Manifest.Write(w, manifest, "assets");
            RandomStateSafe.Trace("assets: END");
        }

        /// <summary>
        /// The mid-run safety re-check, throttled by <paramref name="watch"/>.
        ///
        /// The conditions were already true once, at the moment the dump was asked for, so anything
        /// false now is a CHANGE of conditions while the dumper was running - a peer connecting, the
        /// user quitting to the menu, a hand-off to someone else's server. That is exactly the case
        /// <see cref="Plugin.Refuse"/> exists for: loud, and the plugin is done for the session.
        ///
        /// The partial dump on disk is left where it is rather than cleaned up: this plugin is not
        /// allowed to delete files (the preflight enforces it), and the manifest for the run is never
        /// written when the run stops here, so the folder is visibly incomplete.
        /// </summary>
        internal static bool Aborted(Terminal term, ref Watch watch)
        {
            if (!watch.Due()) return false;

            string reason;
            if (!Safety.MultiplayerBreach(out reason) && Safety.SoloHostWithWorld(out reason)) return false;

            Plugin.Refuse(term, "the asset dump part way through",
                          reason + " The dump stopped where it was; no manifest was written and the " +
                          "folder is incomplete - treat it as unusable and run it again.");
            return true;
        }

        private static void CheckCtorTrace(WorldGenDumpFile d, List<string> notes)
        {
            RandomTraceDef t = d.constructorTrace;
            if (t == null || t.draws == null || t.draws.Length != 7) return;
            bool ok = t.draws[0].resultInt == (int)d.offset0
                   && t.draws[1].resultInt == (int)d.offset1
                   && t.draws[2].resultInt == (int)d.offset2
                   && t.draws[3].resultInt == (int)d.offset3
                   && t.draws[4].resultInt == d.riverSeed
                   && t.draws[5].resultInt == d.streamSeed
                   && t.draws[6].resultInt == (int)d.offset4;
            if (!ok)
            {
                notes.Add("The replayed WorldGenerator constructor draws do NOT match the generator's " +
                          "own fields. Either UnityEngine.Random behaves differently than the replay " +
                          "assumes, or something drew from the stream inside the constructor. This " +
                          "invalidates every RNG assumption in the port - investigate before using it.");
                Plugin.Log.LogError("Constructor draw replay MISMATCH for seed " + d.seed + ".");
            }
        }

        /// <summary>
        /// Offers every location's prefab name to the search. A sought name can BE a location rather
        /// than sit inside one, and a reader who is told "not found" while the name is sitting in
        /// locations.json would rightly stop trusting the file.
        /// </summary>
        private static void NoteLocationTable(SearchIndex search, LocationTableFile t)
        {
            try
            {
                search.LocationTableEntries = t != null ? t.count : 0;
                if (t == null || t.locations == null) return;
                foreach (LocationDef d in t.locations)
                {
                    if (d == null) continue;
                    search.AddTableName(d.prefabName, "locationTable",
                        "This name IS a location in ZoneSystem.m_locations, not something inside one. " +
                        "See locations.json for its placement rules.");
                    if (d.softRefName != null && d.softRefName != d.prefabName)
                    {
                        search.AddTableName(d.softRefName, "locationTable",
                            "This name is a location's SoftReference name in ZoneSystem.m_locations.");
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("The location table could not be offered to the search (" +
                                      e.Message + ").");
            }
        }

        /// <summary>The same for the vegetation table: a sought name may be a world-scattered prop
        /// rather than a prefab child.</summary>
        private static void NoteVegetationTable(SearchIndex search, VegetationTableFile t)
        {
            try
            {
                search.VegetationTableEntries = t != null ? t.count : 0;
                if (t == null || t.vegetation == null) return;
                foreach (VegetationDef d in t.vegetation)
                {
                    if (d == null) continue;
                    search.AddTableName(d.prefabName, "vegetationTable",
                        "This name IS a vegetation entry in ZoneSystem.m_vegetation - scattered per " +
                        "zone by PlaceVegetation, not placed inside any prefab. See vegetation.json.");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("The vegetation table could not be offered to the search (" +
                                      e.Message + ").");
            }
        }

        private static int CountPlaced(LocationInstancesFile f)
        {
            if (f == null || f.instances == null) return 0;
            int n = 0;
            foreach (LocationInstanceDef i in f.instances) if (i.placed) n++;
            return n;
        }

        // ---- location instances -----------------------------------------------------------------

        private static LocationInstancesFile Instances(ZoneSystem zs, World world,
                                                       List<ZoneSystem.ZoneLocation> ordered, string stamp)
        {
            var list = new List<LocationInstanceDef>(zs.m_locationInstances != null
                ? zs.m_locationInstances.Count : 0);

            if (zs.m_locationInstances != null)
            {
                foreach (KeyValuePair<Vector2s, ZoneSystem.LocationInstance> kv in zs.m_locationInstances)
                {
                    ZoneSystem.LocationInstance li = kv.Value;
                    string prefabName = li.m_location != null ? li.m_location.m_prefabName : null;
                    list.Add(new LocationInstanceDef
                    {
                        zoneX = kv.Key.x,
                        zoneY = kv.Key.y,
                        prefabName = prefabName,
                        // ZoneSystem.Save writes m_prefabName.GetStableHashCode() (ZoneSystem.cs:1010),
                        // which is the key a .db2 is read back with.
                        hash = string.IsNullOrEmpty(prefabName) ? 0 : prefabName.GetStableHashCode(),
                        x = li.m_position.x,
                        y = li.m_position.y,
                        z = li.m_position.z,
                        placed = li.m_placed,
                    });
                }
            }

            var orderedNames = new string[ordered != null ? ordered.Count : 0];
            for (int i = 0; i < orderedNames.Length; i++) orderedNames[i] = ordered[i].m_prefabName;

            return new LocationInstancesFile
            {
                stamp = stamp,
                schema = DumpFormat.Schema,
                worldName = world != null ? world.m_name : null,
                seed = world != null ? world.m_seed : 0,
                locationVersion = zs.m_locationVersion,
                count = list.Count,
                orderedPrefabNames = orderedNames,
                instances = list.ToArray(),
            };
        }

        // ---- alt-biome assignment ------------------------------------------------------------

        private static AltBiomeAssignmentFile Assignment(World world, string stamp)
        {
            AltBiomeWorldData data = world != null ? world.m_biomeData : null;
            if (data == null) data = AltBiomeCapture.LastWorldData;
            if (data == null) return null;

            var f = new AltBiomeAssignmentFile
            {
                stamp = stamp,
                schema = DumpFormat.Schema,
                worldName = world != null ? world.m_name : null,
                seed = world != null ? world.m_seed : 0,
                // GenerateAltBiomes opens with InitState(GetSeed() + 920); the literal is 920.
                openingInitState = unchecked((world != null ? world.m_seed : 0) + 920),
                contaminated = AltBiomeCapture.Contaminated,
            };

            // AltBiomeWorldData.Sectors is append-only (GenerateSectors only ever Adds), so an index
            // into it is a stable identity. The per-biome BiomeTypeInfo.Sectors lists are NOT:
            // GenerateAltBiomes calls Shuffle() on them once per (biome, altBiome) pair.
            var sectorIndex = new Dictionary<BiomeSector, int>();
            var sectorDefs = new List<SectorDef>(data.Sectors != null ? data.Sectors.Count : 0);
            if (data.Sectors != null)
            {
                for (int i = 0; i < data.Sectors.Count; i++)
                {
                    BiomeSector s = data.Sectors[i];
                    sectorIndex[s] = i;
                    sectorDefs.Add(Sector(s, i));
                }
            }
            f.sectors = sectorDefs.ToArray();

            var keys = new List<BiomeKeyDef>();
            int order = 0;
            if (data.Biomes != null)
            {
                foreach (KeyValuePair<Heightmap.Biome, BiomeTypeInfo> kv in data.Biomes)
                {
                    var key = new BiomeKeyDef
                    {
                        order = order++,
                        biome = (int)kv.Key,
                        sectorCount = kv.Value != null && kv.Value.Sectors != null ? kv.Value.Sectors.Count : 0,
                        allPointsCount = kv.Value != null && kv.Value.AllPoints != null ? kv.Value.AllPoints.Count : 0,
                        allPointsAboveSeaLevelCount = kv.Value != null && kv.Value.AllPointsAboveSeaLevel != null
                            ? kv.Value.AllPointsAboveSeaLevel.Count : 0,
                    };
                    key.sectorIndices = IndicesOf(kv.Value != null ? kv.Value.Sectors : null, sectorIndex);
                    keys.Add(key);
                }
            }
            f.biomeKeys = keys.ToArray();

            var assignments = new List<AltBiomeAssignmentDef>();
            foreach (AltBiome a in AltBiomeList.m_altBiomes)
            {
                int before = -1;
                foreach (AltBiomeCapture.Entry e in AltBiomeCapture.Entries)
                {
                    if (e.Name == a.m_name) { before = e.SectorsBefore; break; }
                }
                assignments.Add(new AltBiomeAssignmentDef
                {
                    altBiomeName = a.m_name,
                    sectorsBefore = before,
                    sectorsAfter = a.Sectors != null ? a.Sectors.Count : 0,
                    validPlacementSectors = a.ValidPlacementSectors,
                    validPlacementSectorCombos = a.ValidPlacementSectorCombos,
                    sectorIndices = IndicesOf(a.Sectors, sectorIndex),
                });
            }
            f.assignments = assignments.ToArray();
            return f;
        }

        private static int[] IndicesOf(List<BiomeSector> list, Dictionary<BiomeSector, int> index)
        {
            if (list == null) return new int[0];
            var result = new int[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                int idx;
                result[i] = index.TryGetValue(list[i], out idx) ? idx : -1;
            }
            return result;
        }

        private static SectorDef Sector(BiomeSector s, int index)
        {
            var neighbours = new int[s.Neighbors != null ? s.Neighbors.Count : 0];
            for (int i = 0; i < neighbours.Length; i++) neighbours[i] = (int)s.Neighbors[i].Biome;

            var alts = new string[s.AltBiomes != null ? s.AltBiomes.Count : 0];
            for (int i = 0; i < alts.Length; i++) alts[i] = s.AltBiomes[i].m_name;

            return new SectorDef
            {
                index = index,
                biome = (int)s.Biome,
                edgeCount = s.EdgeCount,
                center = Snapshot.Vec2(s.Center),
                min = Snapshot.Vec2(s.Min),
                max = Snapshot.Vec2(s.Max),
                // GenerateSectors sets MaxZone from Min, not Max, so these are always equal and
                // ZoneCount is always 1. Recorded as observed, not as intended.
                minZone = new Vec2IntDef { x = s.MinZone.x, y = s.MinZone.y },
                maxZone = new Vec2IntDef { x = s.MaxZone.x, y = s.MaxZone.y },
                heightMin = s.HeightMin,
                heightMax = s.HeightMax,
                heightAvg = s.HeightAvg,
                distanceFromCenter = s.DistanceFromCenter,
                isDiscovered = s.IsDiscovered,
                neighborBiomes = neighbours,
                altBiomeNames = alts,
            };
        }

        // ---- hash corpus -------------------------------------------------------------------------

        private static NativesHashFile Hashes(LocationTableFile loc, VegetationTableFile veg,
                                              AltBiomeTableFile alt, World world, string stamp)
        {
            var samples = new List<HashSampleDef>();
            var seen = new HashSet<string>();

            // The separator is U+0000 because it cannot occur in a prefab or seed name, so
            // kind + sep + name is unambiguous. Write it as the ESCAPE "\0", never as a literal NUL
            // byte in this file: a raw NUL makes the source a binary file to grep and git, and some
            // editors strip it on save - which would silently merge the keys of two kinds.
            Action<string, string> add = (s, kind) =>
            {
                if (string.IsNullOrEmpty(s) || !seen.Add(kind + "\0" + s)) return;
                samples.Add(new HashSampleDef { s = s, hash = s.GetStableHashCode(), kind = kind });
            };

            if (loc.locations != null) foreach (LocationDef d in loc.locations) add(d.softRefName ?? d.prefabName, "location");
            if (veg.vegetation != null) foreach (VegetationDef d in veg.vegetation) add(d.prefabName, "vegetation");
            if (alt.altBiomes != null) foreach (AltBiomeDef d in alt.altBiomes) add(d.name, "altbiome");
            if (world != null) add(world.m_seedName, "seedtext");

            return new NativesHashFile
            {
                stamp = stamp,
                schema = DumpFormat.Schema,
                samples = samples.ToArray(),
            };
        }

        // ---- prefab walk -------------------------------------------------------------------------

        /// <summary>
        /// Loads each enabled location prefab to read its <c>Location</c> radii and its
        /// <c>DungeonGenerator</c>'s local transform - and, when <paramref name="children"/> is not
        /// null, everything inside it too (<see cref="ChildWalk"/>). One load fills both files.
        ///
        /// Spread over frames: <c>SoftReference&lt;T&gt;.Load()</c> is synchronous, and doing 183 of
        /// them in one frame stalls the game for many seconds. When
        /// <c>Settings.AssetMemoryUsagePolicy</c> has <c>KeepAsynchronousLoadedBit</c>,
        /// <c>SetupLocations</c> has already loaded and held them all, so this costs almost nothing.
        /// Each frame's batch is wrapped in a <see cref="RandomGuard"/> in case an asset's own
        /// initialisation draws.
        ///
        /// <b>The child walk forces one prefab per frame</b>, whatever <c>PrefabsPerFrame</c> says. It
        /// visits every transform of every prefab - thousands each for the big camps and villages -
        /// which is the one part of this dump that could plausibly turn a 7-second walk into a much
        /// longer one. Yielding per prefab puts a frame AND a solo/no-peers re-check between every two
        /// prefabs, which is the finest granularity available: a <see cref="RandomGuard"/> must never
        /// span a yield, so a single prefab's child walk cannot be interrupted part way.
        /// </summary>
        private static IEnumerator WalkPrefabs(ZoneSystem zs, LocationPrefabsFile file,
                                               LocationChildrenFile children, Terminal term,
                                               AbortFlag abort, SearchIndex search)
        {
            var watch = new Watch();
            var todo = new List<ZoneSystem.ZoneLocation>();
            var seen = new HashSet<string>();
            foreach (ZoneSystem.ZoneLocation l in zs.m_locations)
            {
                if (!l.m_enable) continue;
                string key;
                try { key = l.m_prefab.Name; } catch { continue; }
                if (string.IsNullOrEmpty(key) || !seen.Add(key)) continue;
                todo.Add(l);
            }

            var defs = new List<LocationPrefabDef>(todo.Count);
            var childDefs = children != null ? new List<LocationChildrenDef>(todo.Count) : null;
            int perFrame = children != null ? 1 : Math.Max(1, Plugin.PrefabsPerFrame.Value);
            int loaded = 0, failed = 0;
            int spawnTotal = 0, objectTotal = 0, containerTotal = 0;

            for (int i = 0; i < todo.Count; i += perFrame)
            {
                int end = Math.Min(todo.Count, i + perFrame);
                using (RandomGuard.Capture("assets: prefab walk batch"))
                {
                    for (int j = i; j < end; j++)
                    {
                        LocationChildrenDef c;
                        LocationPrefabDef d = ReadPrefab(todo[j], children != null, search, out c);
                        defs.Add(d);
                        if (d.loaded) loaded++; else failed++;
                        if (childDefs != null)
                        {
                            // The index alignment between the two files is load-bearing; a null would
                            // shift every later entry. ReadPrefab is supposed to make this impossible.
                            if (c == null)
                            {
                                c = ChildWalk.Failed(d.prefabName, d.assetId,
                                                     "the child walk produced no entry for this prefab",
                                                     d.loaded);
                            }
                            // 'loaded' must mean the same thing in both files. It is the caller's
                            // answer, not the walk's, precisely so the two can never disagree.
                            if (c.loaded != d.loaded) c.loaded = d.loaded;
                            if (c.error != null && c.loaded && search != null)
                            {
                                search.NoteInteriorWalkError();
                            }
                            childDefs.Add(c);
                            spawnTotal += c.randomSpawnCount;
                            objectTotal += c.randomObjectCount;
                            containerTotal += c.containerCount;
                        }
                    }
                }
                // Every 20 prefabs rather than every 40: with the child walk on a frame is one prefab,
                // and the user must never be looking at a game that seems to have stopped.
                if (defs.Count % 20 == 0 || defs.Count == todo.Count)
                {
                    Plugin.Say(term, "prefab walk: " + defs.Count + "/" + todo.Count);
                }
                yield return null;

                // The mid-run re-check that matters most: this loop is the stretch during which a
                // peer can connect, or the user can quit to the menu, without anything else noticing.
                // With the child walk on it runs between every two prefabs.
                if (Aborted(term, ref watch))
                {
                    abort.Requested = true;
                    Plugin.Log.LogWarning("The prefab walk stopped after " + defs.Count + " of " +
                                          todo.Count + " prefabs; neither locationprefabs.json nor " +
                                          DumpFormat.LocationChildrenFile + " was written.");
                    yield break;
                }
            }

            file.count = defs.Count;
            file.loadedCount = loaded;
            file.failedCount = failed;
            file.prefabs = defs.ToArray();

            if (children != null)
            {
                children.count = childDefs.Count;
                children.loadedCount = loaded;
                children.failedCount = failed;
                children.totalRandomSpawns = spawnTotal;
                children.totalRandomObjects = objectTotal;
                children.totalContainers = containerTotal;
                children.locations = childDefs.ToArray();
            }

            if (search != null)
            {
                // Coverage, whether or not the child walk ran: a location prefab that was loaded but
                // not walked is a place the sought name could have been hiding.
                search.LocationChildWalkRan = children != null;
                search.LocationPrefabsWalked = defs.Count;
                search.LocationPrefabsLoaded = loaded;
                search.LocationPrefabsFailed = failed;
            }

            Plugin.Say(term, "prefab walk done: " + loaded + " loaded, " + failed + " failed.");
        }

        /// <summary>The size <see cref="DumpWriter"/> recorded for a file it has just written. The
        /// child walk's output is the biggest thing in the dump, and a measured number in the log beats
        /// an estimate in a README.</summary>
        private static string Mib(DumpWriter w, string relativePath)
        {
            long bytes = 0;
            foreach (FileEntryDef f in w.Files)
            {
                if (f != null && f.path == relativePath) bytes = f.bytes;
            }
            if (bytes <= 0) return "an unrecorded size";
            return bytes >= 1024L * 1024L
                ? (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MiB"
                : (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KiB";
        }

        /// <summary>
        /// One prefab, one <c>Load()</c>, both files. <paramref name="withChildren"/> decides whether
        /// the child walk runs; <paramref name="childDef"/> is always non-null when it does, even for a
        /// prefab that failed to load, so the two files stay index-aligned.
        /// </summary>
        private static LocationPrefabDef ReadPrefab(ZoneSystem.ZoneLocation l, bool withChildren,
                                                    SearchIndex search,
                                                    out LocationChildrenDef childDef)
        {
            var d = new LocationPrefabDef();
            childDef = null;
            SoftReference<GameObject> reference = l.m_prefab;
            try
            {
                d.prefabName = reference.Name;
                d.assetId = Snapshot.AssetId(reference.m_assetID);
            }
            catch (Exception e)
            {
                d.error = "SoftReference unreadable: " + e.Message;
                if (withChildren) childDef = ChildWalk.Failed(d.prefabName, d.assetId, d.error, false);
                return d;
            }

            // Release must balance Load exactly. If Load() throws, nothing was acquired, and releasing
            // anyway would decrement a reference count this plugin never took - corrupting the asset
            // loader's bookkeeping for the rest of the session.
            bool loadCalled = false;
            try
            {
                reference.Load();
                loadCalled = true;
                GameObject asset = reference.Asset;
                if (asset == null)
                {
                    d.error = "Load() returned no asset.";
                    // This return is INSIDE the try, so the fallback after the finally never sees it.
                    // Without this line childDef would stay null and WalkPrefabs would dereference it
                    // inside a RandomGuard - an uncaught throw on a reachable path.
                    if (withChildren) childDef = ChildWalk.Failed(d.prefabName, d.assetId, d.error, false);
                    return d;
                }
                d.loaded = true;

                // SpawnLocation does `location.m_prefab.Asset.GetComponent<Location>()` - ROOT ONLY -
                // and hands that (possibly null) reference to every Randomize, where a null `loc`
                // skips the biome gate outright. locationprefabs.json keeps its child fallback so the
                // radii are still reported, but the child walk must only ever see the root one, or
                // locationchildren.json would describe a component the game never consults.
                Location rootLocation = asset.GetComponent<Location>();
                Location loc = rootLocation;
                if (loc == null) loc = asset.GetComponentInChildren<Location>(true);
                if (loc != null)
                {
                    d.hasLocationComponent = true;
                    d.exteriorRadius = loc.m_exteriorRadius;
                    d.interiorRadius = loc.m_interiorRadius;
                    d.hasInterior = loc.m_hasInterior;
                    d.noBuild = loc.m_noBuild;
                    d.noBuildRadiusOverride = loc.m_noBuildRadiusOverride;
                    d.clearArea = loc.m_clearArea;
                    d.discoverLabel = loc.m_discoverLabel;
                    d.useCustomInteriorTransform = loc.m_useCustomInteriorTransform;
                    d.biome = (int)loc.m_biome;
                }

                var gens = new List<DungeonGeneratorDef>();
                foreach (DungeonGenerator g in asset.GetComponentsInChildren<DungeonGenerator>(true))
                {
                    if (g == null) continue;
                    Vector3 local = asset.transform.InverseTransformPoint(g.transform.position);
                    gens.Add(new DungeonGeneratorDef
                    {
                        path = ChildWalk.PathOf(asset.transform, g.transform),
                        localPosition = Snapshot.Vec3(local),
                        localEulerAngles = Snapshot.Vec3(g.transform.localEulerAngles),
                        // NOT the same quantity as localPosition above: that one divides scale out and
                        // measures from the asset ROOT, this one is the raw parent-relative value -
                        // and 9 of the 21 generators sit under an Interior/ parent. ZoneSystem.
                        // SpawnLocation assigns exactly this to the instance's m_originalPosition when
                        // the location uses a custom interior transform, so for those 18 prefabs it is
                        // the live value and the authored originalPosition below is dead.
                        parentLocalPosition = Snapshot.Vec3(g.transform.localPosition),
                        // DungeonGenerator.GetSeed uses the GENERATOR's world position, which is
                        // locationPos + locationRot * this offset - so a non-zero x or z lets the
                        // unseeded location rotation change the dungeon seed.
                        hasNonZeroXZOffset = local.x != 0f || local.z != 0f,
                        useCustomInteriorTransform = g.m_useCustomInteriorTransform,
                        algorithm = (int)g.m_algorithm,
                        minRooms = g.m_minRooms,
                        maxRooms = g.m_maxRooms,
                        minRequiredRooms = g.m_minRequiredRooms,
                        requiredRoomNames = g.m_requiredRooms != null
                            ? g.m_requiredRooms.ToArray() : new string[0],
                        // The link from this location to roomchildren.json: SetupAvailableRooms keeps
                        // every RoomData whose m_theme intersects this mask and whose m_enabled is set.
                        themes = (int)g.m_themes,
                        // PlaceRoom adds this generator's GetSeed() to each room's own RandomSpawn seed
                        // only when this is set; without it two identical rooms at the same local
                        // position in two different dungeons roll identically.
                        addBaseSeedToRandomSpawn = g.m_addBaseSeedToRandomSpawn,
                        campRadiusMin = g.m_campRadiusMin,
                        campRadiusMax = g.m_campRadiusMax,

                        // Everything below was added on 2026-09-23. Each of these is READ by
                        // GenerateRooms on some path, and every one of them defaults to a value that
                        // is not zero - so a reader that assumed zero for a missing field would be
                        // reproducing a generator the game never runs. fullFieldsCaptured says they
                        // are here; see DungeonGeneratorDef for why that flag exists rather than a
                        // schema bump.
                        fullFieldsCaptured = true,
                        maxTilt = g.m_maxTilt,
                        tileWidth = g.m_tileWidth,
                        gridSize = g.m_gridSize,
                        spawnChance = g.m_spawnChance,
                        minAltitude = g.m_minAltitude,
                        perimeterSections = g.m_perimeterSections,
                        perimeterBuffer = g.m_perimeterBuffer,
                        alternativeFunctionality = g.m_alternativeFunctionality,
                        doorChance = g.m_doorChance,
                        doorTypes = DoorTypes(g),
                        zoneCenter = Snapshot.Vec3(g.m_zoneCenter),
                        zoneSize = Snapshot.Vec3(g.m_zoneSize),
                        originalPosition = Snapshot.Vec3(g.m_originalPosition),
                    });
                }
                d.generators = gens.ToArray();

                // Inside the SAME try/finally that balances Load() with Release(): the child walk
                // needs the asset resident, and it must not be the reason a Release is skipped.
                // ChildWalk.Read never throws - it records a warning on the entry instead - so a
                // malformed prefab cannot lose this prefab's Location radii as well.
                if (withChildren)
                {
                    childDef = ChildWalk.Read(asset, rootLocation, d.prefabName, d.assetId, search);
                }
            }
            catch (Exception e)
            {
                d.error = e.GetType().Name + ": " + e.Message;
            }
            finally
            {
                if (loadCalled)
                {
                    // Skipping this would hold every location prefab resident for the rest of the
                    // session.
                    try { reference.Release(); }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning("Release failed for " + d.prefabName + ": " + e.Message);
                    }
                }
            }

            // The two files are read side by side by index, so a prefab that failed anywhere above
            // still gets a placeholder rather than shifting every later entry by one - and it carries
            // THIS method's 'loaded', so locationprefabs.json and locationchildren.json state the same
            // thing about the same prefab. Before 2026-09-23 the placeholder hard-coded loaded=false,
            // so a prefab whose asset loaded and whose walk then threw appeared as loaded in one file
            // and not loaded in the other.
            if (withChildren && childDef == null)
            {
                childDef = ChildWalk.Failed(d.prefabName, d.assetId,
                                            d.error ?? "the child walk did not run for this prefab",
                                            d.loaded);
            }
            return d;
        }

        /// <summary>
        /// <c>DungeonGenerator.m_doorTypes</c> in list order. <c>FindDoorType</c> collects every entry
        /// whose <c>m_connectionType</c> equals the connection's type and picks one with
        /// <c>Random.Range(0, list.Count)</c>, so the order and the count are both semantic; a null
        /// entry is written as a placeholder rather than skipped, because skipping it would shift every
        /// later index.
        /// </summary>
        private static DoorDefDef[] DoorTypes(DungeonGenerator g)
        {
            if (g == null || g.m_doorTypes == null) return new DoorDefDef[0];
            var list = new List<DoorDefDef>(g.m_doorTypes.Count);
            for (int i = 0; i < g.m_doorTypes.Count; i++)
            {
                DungeonGenerator.DoorDef dd = g.m_doorTypes[i];
                if (dd == null)
                {
                    list.Add(new DoorDefDef { index = i, connectionType = null, hasPrefab = false });
                    continue;
                }
                list.Add(new DoorDefDef
                {
                    index = i,
                    connectionType = dd.m_connectionType,
                    chance = dd.m_chance,
                    // The door's identity is the normalised prefab name, the same way every other name
                    // in this dump is spelled - see Names.
                    prefabName = Names.Normalize(dd.m_prefab),
                    hasPrefab = dd.m_prefab != null,
                });
            }
            return list.ToArray();
        }
    }

    /// <summary>
    /// How a walk iterator tells its caller that it stopped early. An iterator cannot have an
    /// <c>out</c> parameter, and the files these walks fill are wire DTOs that must not grow fields for
    /// the dumper's own control flow. Shared by <see cref="ModeAssets.WalkPrefabs"/> and
    /// <see cref="RoomWalk.Run"/>.
    /// </summary>
    internal sealed class AbortFlag
    {
        public bool Requested;
    }

    /// <summary>
    /// Runs a synchronous block that is supposed to draw nothing from <c>UnityEngine.Random</c>, and
    /// makes sure the stream is where it started either way. Mode A is supposed to be invisible to the
    /// stream; this is what turns that from an intention into a checked fact.
    ///
    /// It both CHECKS and RESTORES. Detecting a moved stream and leaving it moved would satisfy the
    /// letter of "the dumper notices its own bugs" and break the rule that actually matters to the
    /// user - save and restore around everything the plugin does - in the exact session where the bug
    /// bit. So the saved <c>Random.state</c> is put back unconditionally (the same struct assignment
    /// <see cref="RandomGuard"/> makes, and a no-op when nothing moved), and a block that DID move it
    /// is still reported as the dumper bug it is.
    ///
    /// The restore is in a <c>finally</c>: a block that throws half way through is exactly the case
    /// that would otherwise leave the user's stream displaced.
    ///
    /// <b>Restore first, compare second</b> (2026-09-23). The comparison needs the four state words,
    /// and those are only reachable by REFLECTION (<see cref="RandomStateUtil"/>), which can throw -
    /// a Unity update that renames the fields, a security policy that refuses the private read. When
    /// that read happened before the assignment, a throw inside it skipped the restore entirely and
    /// the check meant to protect the stream was the thing that displaced it. Now the <c>finally</c>
    /// copies the current <c>Random.State</c> struct (a plain property getter over native state: it
    /// draws nothing and does no reflection), puts the saved state back immediately, and only then
    /// does the reflective comparison, inside its own try/catch. The restore cannot be skipped by
    /// anything the diagnostic does.
    ///
    /// This only works within a single frame - across a yield the game itself will have drawn, and
    /// putting the state back would rewind the game's own stream. Never span a yield.
    /// </summary>
    internal static class NoDrawCheck
    {
        public static void Around(string what, Action body)
        {
            // Saved as the struct, not as the four words: the words are read by reflection and can be
            // unavailable, but the struct round trip is what the restore needs and it always works.
            UnityEngine.Random.State saved = UnityEngine.Random.state;
            try
            {
                body();
            }
            finally
            {
                // 1. Capture, 2. RESTORE, 3. only then look at what happened. Nothing between the
                // capture and the assignment may be able to throw.
                UnityEngine.Random.State after = UnityEngine.Random.state;
                // Through the single writer (2026-09-23): a restore that would write four zeros is
                // always wrong, whatever produced the value, so RandomStateSafe re-seeds instead. See
                // RandomStateSafe for why the zero test cannot throw and cannot skip the write.
                RandomStateSafe.Restore(saved, "NoDrawCheck.Around: " + what);

                try
                {
                    int[] beforeWords = RandomStateUtil.Read(saved);
                    int[] afterWords = RandomStateUtil.Read(after);
                    if (beforeWords == null || afterWords == null)
                    {
                        Plugin.Log.LogWarning("SeedLab.Dumper: could not read Random.State's words, so '" +
                                              what + "' could not be checked for draws. The state was " +
                                              "restored anyway.");
                    }
                    else if (!RandomStateUtil.Equal(beforeWords, afterWords))
                    {
                        Plugin.Log.LogError("SeedLab.Dumper: '" + what + "' perturbed UnityEngine.Random. " +
                                            "That is a bug in the dumper: wrap it in a RandomGuard. The " +
                                            "state has been restored, so this session is unharmed - but " +
                                            "the dump may have consumed draws it should not have.");
                    }
                }
                catch (Exception e)
                {
                    // The restore already happened above, so this is only a lost diagnostic.
                    Plugin.Log.LogWarning("SeedLab.Dumper: the no-draw check for '" + what + "' could " +
                                          "not compare the Random state (" + e.Message + "). The state " +
                                          "was restored before this was attempted.");
                }
            }
        }
    }
}
