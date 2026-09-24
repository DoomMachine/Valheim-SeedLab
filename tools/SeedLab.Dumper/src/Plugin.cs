using System;
using System.Collections;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SeedLab.Dumper
{
    /// <summary>What the trigger file asked for. Nothing runs without one.</summary>
    internal enum DumpMode
    {
        /// <summary>No trigger file, or an unrecognised word: the plugin is inert.</summary>
        Idle = 0,

        /// <summary>The asset tables: locations, vegetation, alt biomes, prefab constants, the current
        /// world's WorldGenerator state, the placement results.</summary>
        Assets = 1,

        /// <summary>The native-function evidence only (PerlinNoise, Random, FloatToHalf, libm, hashes).
        /// Needs no world.</summary>
        Natives = 2,

        /// <summary>The main-menu, multi-seed WorldGenerator dump.</summary>
        WorldGen = 3,

        /// <summary>All three, each still on its own explicit trigger.</summary>
        All = 4,
    }

    /// <summary>
    /// SeedLab's game-data dumper. It captures the ~50 serialized values that exist only at runtime -
    /// the location and vegetation tables, the alt-biome list, a handful of prefab constants - plus the
    /// native-function evidence that settles how <c>Mathf.PerlinNoise</c>, <c>UnityEngine.Random</c>
    /// and <c>Mathf.FloatToHalf</c> actually behave on this machine.
    ///
    /// <b>It is inert unless deliberately armed.</b> Defence in depth, each layer enough on its own:
    /// <list type="number">
    /// <item>A file named <c>dumper.enable</c> must sit next to the plugin DLL. Without it
    /// <c>Awake</c> logs one line and returns before applying any Harmony patch - patches that are
    /// never applied cannot interact with anything.</item>
    /// <item>That file must contain one recognised word (<c>assets</c>, <c>natives</c>,
    /// <c>worldgen</c>, or <c>all</c>).</item>
    /// <item>Every dump re-checks, at the moment of action, that this is a solo session we host with
    /// no connected peers (<see cref="Safety"/>) - and keeps re-checking about once a second while it
    /// runs (<see cref="Watch"/>), because a dump is minutes of frames and this coroutine survives a
    /// scene change: BepInEx's manager object is <c>DontDestroyOnLoad</c>.</item>
    /// <item>Nothing happens without a human action: a key press or a console command. There is no
    /// automatic behaviour on world load.</item>
    /// </list>
    ///
    /// Everything that can touch <c>UnityEngine.Random</c> runs inside a <see cref="RandomGuard"/>, and
    /// the guard is verified rather than assumed - the first item in the native dump is a state
    /// round-trip proof.
    /// </summary>
    [BepInPlugin(GUID, NAME, VERSION)]
    [BepInProcess("valheim.exe")]   // client only; the dedicated server is valheim_server.exe
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "DoomMachine.SeedLabDumper";
        public const string NAME = "SeedLab.Dumper";
        public const string VERSION = "1.0.0";

        /// <summary>The file that arms the plugin, looked for next to the plugin DLL.</summary>
        public const string TriggerFileName = "dumper.enable";

        public static ManualLogSource Log;
        public static Plugin Instance;

        internal static DumpMode Mode = DumpMode.Idle;

        /// <summary>Set after a refusal or an unexpected failure. Once true the plugin does nothing at
        /// all for the rest of the session - a dumper that keeps retrying in someone's multiplayer
        /// session is worse than one that gave up.</summary>
        internal static bool Disabled;

        internal static ConfigEntry<KeyCode> DumpKey;
        internal static ConfigEntry<string> OutputDir;
        internal static ConfigEntry<bool> WalkLocationPrefabs;
        internal static ConfigEntry<bool> WalkPrefabChildren;
        internal static ConfigEntry<bool> WalkRoomPrefabs;
        internal static ConfigEntry<string> SoughtPrefabNames;
        internal static ConfigEntry<int> PrefabsPerFrame;

        private Harmony _harmony;
        private bool _running;
        private int _updateErrors;

        private void Awake()
        {
            Log = Logger;
            Instance = this;

            // BepInEx drops a plugin that throws in Awake, so every stage is guarded on its own.
            try
            {
                Mode = ReadTrigger();
            }
            catch (Exception e)
            {
                Log.LogError("Could not read the trigger file, staying idle: " + e);
                Mode = DumpMode.Idle;
                return;
            }

            if (Mode == DumpMode.Idle)
            {
                Log.LogInfo(NAME + " " + VERSION + ": no " + TriggerFileName + " next to the plugin, idle. " +
                            "No patches applied, nothing will run.");
                return;
            }

            try
            {
                // F4 is a function key vanilla does not use: vanilla holds F1 (mouse capture), F2
                // (connect panel), F3 (HUD), F5 (console), F9 (gamepad layout), F11 (screenshot). Other
                // plugins bind keys too, so check the ones installed beside this before changing it;
                // tools\decompile.ps1 reads vanilla's side (e.g. -Type ZInput -Assembly assembly_utils)
                // and takes any plugin DLL as -Assembly.
                DumpKey = Config.Bind("General", "DumpKey", KeyCode.F4,
                    "Runs the asset dump (mode 'assets'). Only does anything while the plugin is armed " +
                    "by a dumper.enable file, and only in a solo session you host.");

                OutputDir = Config.Bind("General", "OutputDir", "",
                    "Where dumps are written. Empty means %USERPROFILE%\\AppData\\valheim-dumper. " +
                    "The plugin refuses to write into the game install, the save folders, worlds, " +
                    "worlds_local, characters, cache or any Steam Cloud 'remote' folder.");

                WalkLocationPrefabs = Config.Bind("Assets", "WalkLocationPrefabs", true,
                    "Load every enabled location prefab to read its Location/DungeonGenerator fields. " +
                    "Spread over frames; 186 prefabs took about 7 seconds in the 2026-09-22 run.");

                WalkPrefabChildren = Config.Bind("Assets", "WalkPrefabChildren", true,
                    "While the prefab walk has each location prefab loaded, also record what is " +
                    "inside it: the ordered RandomSpawn and RandomObject arrays that spend the " +
                    "location's seeded RNG stream, every Container's default drop table, and an " +
                    "index of the distinct child names. Writes locationchildren.json, the largest " +
                    "file in the dump. Needs WalkLocationPrefabs; it is the same walk, and turning " +
                    "it on forces one prefab per frame regardless of PrefabsPerFrame.");

                WalkRoomPrefabs = Config.Bind("Assets", "WalkRoomPrefabs", true,
                    "Also walk every dungeon/camp ROOM prefab in DungeonDB - the crypt, cave, " +
                    "village and fort pieces that a location prefab never contains, because the " +
                    "generator instantiates them at world generation time. Writes " +
                    "roomchildren.json. Without this the dump can only answer 'which LOCATION " +
                    "contains X', and an interior piece answers that with silence. One room per " +
                    "frame, the same as the location child walk.");

                SoughtPrefabNames = Config.Bind("Assets", "SoughtPrefabNames",
                    "piece_maypole,TreasureChest_meadows_01,TreasureChest_meadows_02",
                    "Comma-separated prefab names to resolve across everything the dump can reach - " +
                    "location prefabs and their children, room prefabs and their children, the " +
                    "RandomObject options, the container drop tables, and the location and " +
                    "vegetation tables. Writes search.json, which says FOUND (with every host, path " +
                    "and gate) or NOT FOUND in as many words, plus what was and was not searched. " +
                    "Names are matched the way the game matches them, through " +
                    "Utils.GetPrefabName, so 'piece_maypole' also finds a child named " +
                    "'piece_maypole (1)'. Set this BEFORE the run: the walk releases every prefab it " +
                    "loads, so a name chosen afterwards cannot be searched without dumping again.");

                PrefabsPerFrame = Config.Bind("Assets", "PrefabsPerFrame", 2,
                    "How many location prefabs to load per frame during the prefab walk. Lower is " +
                    "gentler on the frame rate. Ignored while WalkPrefabChildren is on, which " +
                    "always uses one.");
            }
            catch (Exception e)
            {
                Log.LogError("Configuration failed to bind, staying idle: " + e);
                Mode = DumpMode.Idle;
                return;
            }

            // A bad OutputDir must not reach here as an exception: BepInEx drops a plugin that throws
            // in Awake, and it would be dropped AFTER the patches were applied.
            string outputRoot;
            try { outputRoot = ResolveOutputRoot(); }
            catch (Exception e)
            {
                Log.LogError("OutputDir is not a folder this plugin may write to (" + e.Message +
                             "). Fix it in the config; the dumper stays idle.");
                Mode = DumpMode.Idle;
                Disabled = true;
                return;
            }

            _harmony = new Harmony(GUID);
            ApplyPatches();

            Log.LogWarning(NAME + " " + VERSION + " is ARMED in mode '" + Mode.ToString().ToLowerInvariant() +
                           "'. Remove " + TriggerFileName + " to make it inert again.");
            Log.LogInfo("Output folder: " + outputRoot);
            Log.LogInfo("Commands: seedlab_dump | seedlab_natives | seedlab_worldgen | seedlab_status. " +
                        "Hotkey for the asset dump: " + DumpKey.Value + ".");
        }

        /// <summary>
        /// One <c>PatchAll</c> per patch class in its own try/catch: <c>PatchAll(assembly)</c> aborts on
        /// the first target it cannot resolve, so one method renamed by a game update would silently
        /// disable every patch.
        ///
        /// There are only two, and neither is on a hot path. Everything else this plugin reads -
        /// <c>ZoneSystem.m_locations</c>, <c>m_vegetation</c>, <c>AltBiomeList.m_altBiomes</c>, the
        /// Minimap and zone-prefab constants, <c>m_locationInstances</c> - is public (or reachable by
        /// reflection) and stable after load, so it is read on demand at dump time instead of being
        /// captured by a hook during the user's world load.
        /// </summary>
        private void ApplyPatches()
        {
            Type[] patchClasses = Mode == DumpMode.Natives
                ? new[] { typeof(Terminal_InitTerminal_Patch) }
                : new[] { typeof(Terminal_InitTerminal_Patch), typeof(GenerateAltBiomes_Patch) };

            int applied = 0;
            foreach (Type t in patchClasses)
            {
                try { _harmony.PatchAll(t); applied++; }
                catch (Exception e)
                {
                    Log.LogError("Patch " + t.Name + " failed, that feature is off: " + e.Message);
                }
            }
            Log.LogInfo(string.Format("Applied {0} of {1} patches.", applied, patchClasses.Length));
        }

        private void Update()
        {
            // Runs every frame: nothing may escape, and a repeating error must not flood the log.
            if (Mode == DumpMode.Idle || Disabled || _running) return;
            try
            {
                if (DumpKey == null || DumpKey.Value == KeyCode.None) return;
                if (!Allows(DumpMode.Assets)) return;
                if (IsTypingElsewhere()) return;
                if (!ZInput.GetKeyDown(DumpKey.Value, false)) return;   // never UnityEngine.Input

                string reason;
                if (Safety.MultiplayerBreach(out reason)) { Refuse(null, "the asset dump", reason); return; }
                if (!Safety.SoloHostWithWorld(out reason))
                {
                    // A mistimed keypress (at the menu, or mid-load) must not disable the plugin.
                    Say(null, NAME + ": not now - " + reason);
                    return;
                }
                // The hotkey never asks for a grid: it is the one-key "just dump it" path, and a grid
                // can take minutes. Use the console command for that.
                RunAssets(null, "none");
            }
            catch (Exception e)
            {
                _updateErrors++;
                if (_updateErrors <= 3) Log.LogError("Update failed: " + e);
                else if (_updateErrors == 4) Log.LogError("Update: repeating, further occurrences not logged.");
            }
        }

        /// <summary>True while a game text field owns the keyboard, so hotkeys must stand down.</summary>
        public static bool IsTypingElsewhere()
        {
            try
            {
                if (TextInput.IsVisible()) return true;
                if (Chat.instance != null && Chat.instance.HasFocus()) return true;
                if (Minimap.InTextInput()) return true;
                if (Console.IsVisible()) return true;      // Valheim's Console, not System.Console
            }
            catch { }
            return false;
        }

        internal static bool Allows(DumpMode wanted)
        {
            return Mode == DumpMode.All || Mode == wanted;
        }

        internal string ResolveOutputRoot()
        {
            string configured = OutputDir != null ? OutputDir.Value : null;
            string root = string.IsNullOrEmpty(configured) ? DumpWriter.DefaultRoot() : configured.Trim();
            DumpWriter.AssertWritable(root);
            return root;
        }

        // ---- entry points, shared by the hotkey and the console commands ------------------------

        internal void RunAssets(Terminal term, string gridId)
        {
            StartJob("asset dump", term, (w, t) => ModeAssets.Run(w, t, gridId));
        }

        internal void RunNatives(Terminal term)
        {
            StartJob("native-function dump", term, ModeNatives.Run);
        }

        internal void RunWorldGen(Terminal term, string[] seedTexts, string gridId)
        {
            StartJob("world-generator dump", term, (w, t) => ModeWorldGen.Run(w, t, seedTexts, gridId));
        }

        private void StartJob(string what, Terminal term, Func<DumpWriter, Terminal, IEnumerator> body)
        {
            if (Disabled) { Say(term, NAME + " is disabled for this session."); return; }
            if (_running) { Say(term, NAME + ": a dump is already running."); return; }
            try
            {
                string root = Path.Combine(ResolveOutputRoot(),
                    DumpWriter.BuildFolderName(GameInfo.Read().version, GameInfo.Read().assemblyValheimSha256));
                var writer = new DumpWriter(root);
                _running = true;
                StartCoroutine(Drive(what, term, writer, body));
            }
            catch (Exception e)
            {
                _running = false;
                Log.LogError(what + " could not start: " + e);
                Say(term, NAME + ": " + what + " could not start - " + e.Message);
            }
        }

        private IEnumerator Drive(string what, Terminal term, DumpWriter writer,
                                  Func<DumpWriter, Terminal, IEnumerator> body)
        {
            Say(term, NAME + ": " + what + " started. The game will hitch; do not quit until it says DONE.");
            Log.LogWarning(NAME + ": " + what + " started -> " + writer.Root);

            IEnumerator inner = body(writer, term);
            string failure = null;

            // A `yield return` may not sit inside a try that has a catch clause (CS1626), so the step
            // is taken inside the try and the yield happens after it. MoveNext is the only place the
            // job's own code runs, so every failure it can produce is caught here: a dump that dies
            // must never take the user's session with it.
            while (true)
            {
                bool more;
                object current = null;
                try
                {
                    more = inner.MoveNext();
                    if (more) current = inner.Current;
                }
                catch (Exception e)
                {
                    more = false;
                    failure = e.ToString();
                }
                if (!more) break;
                yield return current;
            }

            _running = false;
            if (failure != null)
            {
                Log.LogError(what + " FAILED: " + failure);
                Say(term, NAME + ": " + what + " FAILED - see BepInEx\\LogOutput.log. Partial files may " +
                          "exist in " + writer.Root + "; treat the dump as unusable.");
            }
            else if (Disabled)
            {
                // A mid-run refusal (a peer connected, the session left the world) ends the job by
                // returning, not by throwing - so without this the run would end on the word "DONE"
                // right after refusing itself. Refuse() is the only thing that sets Disabled, and
                // StartJob refuses to start at all when it is already set, so reaching here means
                // this run was the one that was stopped.
                Log.LogError(what + " STOPPED part way through: the dumper refused mid-run, see the " +
                             "refusal above.");
                Say(term, NAME + ": " + what + " STOPPED, not finished - see the refusal above. Partial " +
                          "files exist in " + writer.Root + "; treat the dump as unusable and run it " +
                          "again in a solo session.");
            }
            else
            {
                Log.LogWarning(NAME + ": " + what + " DONE -> " + writer.Root);
                Say(term, NAME + ": " + what + " DONE. Files are in " + writer.Root);
            }
        }

        /// <summary>Feedback the user can actually see: the console if it is open, the top-left message
        /// queue in-world, and always the BepInEx log.</summary>
        internal static void Say(Terminal term, string message)
        {
            try { if (term != null) term.AddString(message); } catch { }
            try
            {
                if (term == null && MessageHud.instance != null && Player.m_localPlayer != null)
                {
                    MessageHud.instance.ShowMessage(MessageHud.MessageType.TopLeft, message, 0, null, false, false);
                }
            }
            catch { }
            Log.LogInfo(message);
        }

        internal static void Refuse(Terminal term, string what, string reason)
        {
            Disabled = true;
            string msg = NAME + ": REFUSED " + what + " - " + reason + " The dumper is now disabled for " +
                         "this session; restart the game to try again.";
            Log.LogError(msg);
            Say(term, msg);
        }

        private static DumpMode ReadTrigger()
        {
            string dir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
            if (string.IsNullOrEmpty(dir)) return DumpMode.Idle;
            string path = Path.Combine(dir, TriggerFileName);
            if (!File.Exists(path)) return DumpMode.Idle;

            string word = (File.ReadAllText(path) ?? string.Empty).Trim().ToLowerInvariant();
            switch (word)
            {
                case "assets": return DumpMode.Assets;
                case "natives": return DumpMode.Natives;
                case "worldgen": return DumpMode.WorldGen;
                case "all":
                case "groundtruth":   // the word spec 04 section 3.2 uses for the ground-truth mode
                    return DumpMode.All;
                default:
                    Log.LogWarning(TriggerFileName + " contains '" + word + "', which is not one of " +
                                   "assets / natives / worldgen / all. Staying idle.");
                    return DumpMode.Idle;
            }
        }

        private void OnDestroy()
        {
            if (_harmony != null) _harmony.UnpatchSelf();
        }
    }
}
