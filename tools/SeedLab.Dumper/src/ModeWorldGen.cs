using System;
using System.Collections;
using System.Collections.Generic;
using SeedLab.Contracts.Dump;

namespace SeedLab.Dumper
{
    /// <summary>
    /// The multi-seed <c>WorldGenerator</c> dump: per seed, the private offsets and river seeds, the
    /// lake/river/stream lists, the river-point grid, and optionally a dense sample grid.
    ///
    /// <b>Main menu only</b>, and the reason is not caution for its own sake. <c>Initialize</c>
    /// replaces the static <c>WorldGenerator.instance</c>; the <c>HeightmapBuilder</c> thread calls
    /// <c>GetBiome</c> / <c>GetBiomeHeight</c> / <c>GetBiomeSector</c> on whatever generator its queued
    /// build captured, and <c>Heightmap.Regenerate</c> re-requests whenever
    /// <c>m_buildData.m_worldGen != WorldGenerator.instance</c> (Heightmap.cs:430). Swapping the static
    /// with a world loaded makes every loaded heightmap rebuild against the wrong generator, and the
    /// constructor also clears the static <c>s_cachedBiomeAreas</c> / <c>s_cachedBiomes</c> that the
    /// builder thread may be reading. At the menu there is no ZoneSystem, no peers and no terrain but
    /// the backdrop - which will flicker while this runs, and is restored at the end.
    ///
    /// Three invariants from spec 04 section 3.7 are honoured literally:
    /// <list type="bullet">
    /// <item><b>Invariant 0.</b> The saved generator object CANNOT be restored by assigning it back:
    /// <c>Initialize</c> runs <c>m_instance?.CleanCachedRiverData()</c> on the OUTGOING instance, so
    /// the first loop iteration guts it - rivers, streams and river points gone, unrecoverable because
    /// <c>Pregenerate()</c> only ever runs from the constructor. The saved <b>World</b> is kept instead
    /// and a fresh generator is built from it at the end - in a <c>finally</c>, so a failure anywhere
    /// in the loop cannot leave the menu backdrop generating from the dumper's throwaway world.</item>
    /// <item><b>Invariant 1.</b> <c>AltBiomeWorldData.VerifyBiomeData</c> is never called: it opens
    /// with <c>RemoveCache(world.m_name)</c>, which DELETES a file under the game's cache folder.</item>
    /// <item><b>Invariant 2.</b> No alt-biome generation happens here at all. At the menu
    /// <c>AltBiomeList.m_altBiomes</c> is empty (the list prefab is instantiated by
    /// <c>ZoneSystem.Awake</c> in the main scene), and running <c>GenerateAltBiomes</c> more than once
    /// per process starves every run after the first because <c>AltBiome.Sectors</c> is never
    /// cleared. Alt-biome ground truth comes from the asset dump of a real world instead.</item>
    /// </list>
    /// </summary>
    internal static class ModeWorldGen
    {
        public static IEnumerator Run(DumpWriter w, Terminal term, string[] seedTexts, string gridId)
        {
            string stamp = GameInfo.Stamp("worldgen");
            var notes = new List<string>();

            string reason;
            if (Safety.MultiplayerBreach(out reason))
            {
                Plugin.Refuse(term, "the world-generator dump", reason);
                yield break;
            }

            // See ModeAssets.Run: an all-zero UnityEngine.Random is a dead generator. This mode
            // constructs Worlds (Utils.GenerateUID draws) and re-points WorldGenerator.instance, so it
            // must not run on a stream that is already destroyed.
            if (RandomStateSafe.CurrentIsZero())
            {
                Plugin.Refuse(term, "the world-generator dump",
                    "UnityEngine.Random reads as all zeros - a fixed point of its xorshift128 generator, " +
                    "so every draw returns 0. Something has already broken the generator and this dump " +
                    "would be unverifiable. Restart the game and dump again.");
                yield break;
            }
            RandomStateSafe.Trace("worldgen: START");
            if (!Safety.MainMenuOnly(out reason))
            {
                Plugin.Say(term, "SeedLab.Dumper: " + reason);
                yield break;
            }
            if (!Grids.IsKnown(gridId))
            {
                Plugin.Say(term, "SeedLab.Dumper: unknown grid '" + gridId +
                                 "'. Use none, coarse128, findlakes, full12 or edges.");
                yield break;
            }

            // Invariant 0: keep the WORLD, never the generator object.
            World savedWorld = WorldGenerator.instance != null ? WorldGenerator.instance.m_world : null;
            if (savedWorld == null)
            {
                savedWorld = World.GetMenuWorld();
                notes.Add("WorldGenerator.instance had no World; the menu world was used for the restore.");
            }

            notes.Add("Menu mode: no ZoneSystem and no AltBiomeList exist, so no alt-biome assignment " +
                      "and no location placement is produced here. Use the asset dump for those.");

            int done = 0;
            var failures = new List<string>();

            // What this dump last LEFT in WorldGenerator.instance, by reference. The restore may only
            // touch the static while it still holds this object; anything else means the game has
            // installed its own generator since (ZNet.Awake does, when a world starts loading) and
            // writing over it would be the corruption this whole mode is careful about. `touched`
            // is set BEFORE Initialize so that a constructor that throws - after Initialize has
            // already run CleanCachedRiverData() on the outgoing instance - still gets the restore
            // it needs.
            WorldGenerator ours = WorldGenerator.instance;
            bool touched = false;

            // Invariant 0 restore, and it is a FINALLY on purpose. Only the World/Initialize/Read
            // block below is inside a try/catch; everything after it - WriteRiverPoints, the grid
            // job, WriteJson - can throw (a full disk, a refused path, a missing game member), and
            // that exception leaves Run() through MoveNext. Without this finally the restore would be
            // skipped and WorldGenerator.instance would stay pointed at the dumper's throwaway world:
            // the menu backdrop would keep generating from a seed the user never chose for the rest
            // of the session, and "Start World" would be built by FejdStartup from whatever generator
            // it finds. A yield may sit inside a try that has a finally (only try/CATCH forbids it,
            // CS1626), so the whole loop can be covered.
            try
            {
                foreach (string seedText in seedTexts)
                {
                    // Re-checked EVERY iteration, not once before the loop (fixed 2026-09-23).
                    // BepInEx's manager object is DontDestroyOnLoad, so this coroutine survives a
                    // scene change: the user can press "Start World" while a multi-seed run is still
                    // going, and the old code would have gone on replacing WorldGenerator.instance
                    // inside a loading world - the precise corruption MainMenuOnly exists to prevent.
                    // Stopping is a `break`, not a `yield break`: the finally restores either way, and
                    // the manifest still records how far the run got and why it stopped.
                    if (Safety.MultiplayerBreach(out reason))
                    {
                        Plugin.Refuse(term, "the world-generator dump part way through", reason);
                        notes.Add("Stopped after " + done + " of " + seedTexts.Length +
                                  " seed(s): " + reason);
                        break;
                    }
                    if (!Safety.MainMenuOnly(out reason))
                    {
                        Plugin.Say(term, "SeedLab.Dumper: the world-generator dump stopped after " + done +
                                         " of " + seedTexts.Length + " seed(s) - " + reason);
                        notes.Add("Stopped after " + done + " of " + seedTexts.Length +
                                  " seed(s) because the session left the main menu: " + reason);
                        break;
                    }

                    WorldGenDumpFile d = null;
                    string failure = null;
                    var gridFiles = new List<string>();

                    // One synchronous block per seed. The guard must not span the grid yields below, so it
                    // is closed before them - which is safe because sampling draws nothing.
                    using (RandomGuard.Capture("worldgen: one seed"))
                    {
                        try
                        {
                            // World..ctor calls Utils.GenerateUID, whose last statement is
                            // Random.Range(1, int.MaxValue) (Utils.cs:424) - so constructing a World
                            // consumes exactly one draw from whatever stream is current. Harmless here
                            // only because it happens BEFORE Initialize, whose constructor re-seeds with
                            // InitState(m_world.m_seed), and because this guard restores the ambient
                            // stream afterwards. Do not reorder these two lines.
                            var temp = new World("seedlab_dumper_" + DumpWriter.Sanitize(seedText), seedText);

                            // Runs Pregenerate(): FindLakes, PlaceRivers, PlaceStreams x2. Seconds, not
                            // milliseconds - the frame will visibly hitch.
                            // From this line on the static is this dump's responsibility, whether
                            // Initialize finishes or throws part way: its first act is
                            // m_instance?.CleanCachedRiverData(), which guts the outgoing generator.
                            touched = true;
                            WorldGenerator.Initialize(temp);
                            ours = WorldGenerator.instance;

                            d = WorldGenReader.Read(WorldGenerator.instance, temp, stamp);
                        }
                        catch (Exception e)
                        {
                            failure = e.Message;
                        }
                    }

                    if (failure != null || d == null)
                    {
                        failures.Add(seedText + ": " + (failure ?? "no data"));
                        Plugin.Log.LogError("worldgen dump failed for '" + seedText + "': " + failure);
                        yield return null;
                        continue;
                    }

                    string seedHex = WorldGenReader.SeedHex(d.seed);
                    WorldGenReader.WriteRiverPoints(w, WorldGenerator.instance, d,
                        DumpFormat.RiverPointsFile(seedHex, DumpFormat.SourceMenu));
                    yield return null;

                    bool stop = false;
                    if (gridId != "none")
                    {
                        // full12 is 4.2M samples for ONE seed and runs for minutes, so the per-seed
                        // re-check above is not enough on its own: the rules are re-asked inside this
                        // loop too, throttled to once a second.
                        var gridWatch = new Watch();
                        IEnumerator grid = Grids.Write(w, WorldGenerator.instance, d, gridId,
                                                       DumpFormat.SourceMenu, gridFiles);
                        while (grid.MoveNext())
                        {
                            yield return grid.Current;
                            if (!gridWatch.Due()) continue;
                            if (Safety.MultiplayerBreach(out reason))
                            {
                                Plugin.Refuse(term, "the world-generator dump part way through a grid", reason);
                                notes.Add("Stopped inside the grid for '" + seedText + "': " + reason);
                                stop = true;
                                break;
                            }
                            if (!Safety.MainMenuOnly(out reason))
                            {
                                Plugin.Say(term, "SeedLab.Dumper: the world-generator dump stopped inside " +
                                                 "the grid for '" + seedText + "' - " + reason);
                                notes.Add("Stopped inside the grid for '" + seedText + "' because the " +
                                          "session left the main menu: " + reason);
                                stop = true;
                                break;
                            }
                        }
                    }
                    if (stop) break;
                    d.grids = gridFiles.ToArray();

                    // SourceMenu: this generator was built HERE, at the menu, from the seed text.
                    // The asset dump writes ...-world.json for the loaded world's own generator, and
                    // with one name the two would overwrite each other (both FileMode.Create).
                    w.WriteJson(DumpFormat.WorldGenFile(seedHex, DumpFormat.SourceMenu), d);
                    done++;
                    Plugin.Say(term, "worldgen " + done + "/" + seedTexts.Length + ": '" + seedText +
                                     "' seed " + d.seed + ", lakes " + (d.lakes != null ? d.lakes.Length : 0) +
                                     ", rivers " + (d.rivers != null ? d.rivers.Length : 0) +
                                     ", streams " + (d.streams != null ? d.streams.Length : 0) + ".");
                    yield return null;
                }
            }
            finally
            {
                RestoreGenerator(savedWorld, touched, ours, notes);
            }

            if (failures.Count > 0) notes.Add("Failed seeds: " + string.Join("; ", failures.ToArray()));

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
                    mode = "worldgen",
                    utc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    guid = Plugin.GUID,
                },
                world = null,
                counts = null,
                notes = notes.ToArray(),
            };
            Manifest.Write(w, manifest, "worldgen");
            RandomStateSafe.Trace("worldgen: END");
        }

        /// <summary>
        /// Invariant 0's restore: build a FRESH generator from the world that was current when this
        /// dump started. The saved generator OBJECT cannot be used - <c>Initialize</c> calls
        /// <c>m_instance?.CleanCachedRiverData()</c> on the outgoing instance, so the first loop
        /// iteration already gutted it, and <c>Pregenerate()</c> only ever runs from the constructor.
        ///
        /// It restores ONLY while the static still holds what this dump left there
        /// (<paramref name="ours"/>, 2026-09-23). Two cases made that necessary once the loop learned
        /// to stop mid-run:
        /// <list type="bullet">
        /// <item>The run stopped before its first <c>Initialize</c> (<paramref name="touched"/> false),
        /// so nothing of ours was ever live. <c>WorldGenerator.instance</c> is untouched and assigning
        /// over it would be a change nobody asked for.</item>
        /// <item>The game has installed its own generator since - <c>ZNet.Awake</c> calls
        /// <c>Initialize(world)</c> when a world starts loading, and <c>FejdStartup.Awake</c> does it
        /// for the menu. Putting the saved MENU world back over the world the player just loaded is
        /// the exact corruption this restore exists to prevent, and it would be caused by the restore
        /// itself.</item>
        /// </list>
        /// Only when our throwaway generator is still the live one is anything replaced - and then the
        /// world to build from is the world the game is actually running (<c>ZNet.World</c>) if a world
        /// loaded while we held the static, and the saved menu world otherwise. That branch is an
        /// emergency, not a normal path, and it says so in the log.
        ///
        /// Called from a <c>finally</c>, which means it may run while an exception is on its way out.
        /// It therefore cannot throw: a failure here would replace the real error with this one, and
        /// the user would never see what actually went wrong. The re-<c>Initialize</c> draws the seven
        /// constructor values itself, so it stays inside a <see cref="RandomGuard"/>. For the menu
        /// world <c>m_menu</c> is true and <c>Pregenerate</c> is skipped, but this must not rely on
        /// that - it is the same path someone will reuse in-world.
        /// </summary>
        private static void RestoreGenerator(World savedWorld, bool touched, WorldGenerator ours,
                                             List<string> notes)
        {
            try
            {
                if (!touched)
                {
                    // Initialize was never reached (the run refused, or stopped on its first
                    // iteration). The static is exactly as the game left it.
                    return;
                }
                if (!ReferenceEquals(WorldGenerator.instance, ours))
                {
                    Plugin.Log.LogInfo("WorldGenerator.instance is no longer the dumper's (the game " +
                                       "replaced it, or it was cleared, while the dump was running). " +
                                       "Left alone, as it must be.");
                    if (notes != null)
                    {
                        notes.Add("The game replaced WorldGenerator.instance while the dump ran, so the " +
                                  "dumper's restore was skipped - the game's own generator is current.");
                    }
                    return;
                }

                // What we left is still live - the dumper's throwaway generator, or the original
                // one whose river data Initialize gutted before throwing. Either way it MUST be
                // replaced. Which world to build from depends on where the session is now.
                World target = savedWorld;
                bool emergency = false;
                try
                {
                    if (ZNet.instance != null && ZNet.World != null)
                    {
                        target = ZNet.World;
                        emergency = true;
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("Could not read ZNet.World while restoring the generator (" +
                                          e.Message + "); using the saved world.");
                }

                if (target == null)
                {
                    Plugin.Log.LogError("SeedLab.Dumper: no world is available to restore from, so " +
                                        "WorldGenerator.instance is still the dumper's throwaway world. " +
                                        "The menu backdrop will be wrong until you restart the game.");
                    if (notes != null) notes.Add("WorldGenerator was NOT restored: no world to restore from.");
                    return;
                }

                using (RandomGuard.Capture("worldgen: RestoreGenerator"))
                {
                    WorldGenerator.Initialize(target);
                }

                if (emergency)
                {
                    Plugin.Log.LogError("SeedLab.Dumper: a world ('" + target.m_name + "') loaded while the " +
                                        "dumper still held WorldGenerator.instance. It has been pointed at " +
                                        "that world, which is what the game itself would have installed - " +
                                        "but terrain built in the meantime may have used the dumper's " +
                                        "throwaway world. Leave the world and rejoin it, or restart the " +
                                        "game, before trusting what you see.");
                    if (notes != null)
                    {
                        notes.Add("A world loaded while the dumper held WorldGenerator.instance; the " +
                                  "generator was re-pointed at '" + target.m_name + "'. Terrain built in " +
                                  "that window is suspect.");
                    }
                }
                else
                {
                    Plugin.Log.LogInfo("WorldGenerator restored from world '" + target.m_name + "'.");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("SeedLab.Dumper: restoring WorldGenerator FAILED (" + e.Message +
                                    "). The main menu will keep generating its backdrop from the " +
                                    "dumper's throwaway world for the rest of this session - restart " +
                                    "the game before starting a real world.");
                if (notes != null) notes.Add("WorldGenerator restore failed: " + e.Message);
            }
        }
    }
}
