using System;
using System.Collections.Generic;
using HarmonyLib;

namespace SeedLab.Dumper
{
    /// <summary>
    /// What <c>AltBiomeWorldData.GenerateAltBiomes</c> looked like before and after it ran, captured
    /// while it runs because one of the two numbers cannot be recovered afterwards.
    ///
    /// The number that cannot: <c>Sectors.Count</c> BEFORE the call. <c>GenerateAltBiomes</c> resets
    /// only <c>ValidPlacementSectors</c> and <c>ValidPlacementSectorCombos</c>; it never clears
    /// <c>AltBiome.Sectors</c>, and its quota tests are <c>Sectors.Count &lt; m_maxAmountSpawned</c> and
    /// <c>&lt; m_minAmountSpawned</c>. In normal play that is harmless because a new <c>main</c> scene
    /// load re-instantiates the <c>AltBiomeList</c> prefab and produces fresh <c>AltBiome</c> objects.
    /// Run it twice in one process - <c>genloc alt</c>, or a multi-seed dumper - and the second run
    /// sees every AltBiome already at its maximum and assigns almost nothing, with no error anywhere.
    /// A non-zero "before" count means the dump is not a clean first run and must be discarded.
    /// </summary>
    internal static class AltBiomeCapture
    {
        public sealed class Entry
        {
            public string Name;
            public int SectorsBefore;
            public int SectorsAfter;
            public int ValidPlacementSectors;
            public int ValidPlacementSectorCombos;
        }

        /// <summary>Number of times <c>GenerateAltBiomes</c> has run this session. More than one means
        /// the later runs were contaminated by the earlier ones.</summary>
        public static int Runs;

        public static int WorldSeed;
        public static bool Contaminated;
        public static List<Entry> Entries = new List<Entry>();

        /// <summary>The <c>AltBiomeWorldData</c> the last run operated on, so the dump can read its
        /// <c>Sectors</c> list (the stable sector identity) and its <c>Biomes</c> key order.</summary>
        public static AltBiomeWorldData LastWorldData;

        private static readonly Dictionary<AltBiome, int> Before = new Dictionary<AltBiome, int>();

        public static void Prefix(AltBiomeWorldData world)
        {
            Before.Clear();
            Contaminated = false;
            foreach (AltBiome a in AltBiomeList.m_altBiomes)
            {
                int n = a.Sectors != null ? a.Sectors.Count : 0;
                Before[a] = n;
                if (n != 0) Contaminated = true;
            }
            LastWorldData = world;
            try { WorldSeed = WorldGenerator.instance != null ? WorldGenerator.instance.GetSeed() : 0; }
            catch { WorldSeed = 0; }
        }

        public static void Postfix(AltBiomeWorldData world)
        {
            Runs++;
            LastWorldData = world;
            var list = new List<Entry>(AltBiomeList.m_altBiomes.Count);
            foreach (AltBiome a in AltBiomeList.m_altBiomes)
            {
                int before;
                if (!Before.TryGetValue(a, out before)) before = -1;
                list.Add(new Entry
                {
                    Name = a.m_name,
                    SectorsBefore = before,
                    SectorsAfter = a.Sectors != null ? a.Sectors.Count : 0,
                    ValidPlacementSectors = a.ValidPlacementSectors,
                    ValidPlacementSectorCombos = a.ValidPlacementSectorCombos,
                });
            }
            Entries = list;
            if (Contaminated || Runs > 1)
            {
                Plugin.Log.LogWarning("GenerateAltBiomes has now run " + Runs + " time(s) this session" +
                    (Contaminated ? " and some AltBiomes already had sectors" : "") +
                    ". Only the FIRST run is clean ground truth.");
            }
        }
    }

    /// <summary>
    /// The only patch on game logic. It records counts and touches nothing: no RNG, no state, no
    /// control flow. Both halves swallow their own exceptions so a failure here can never break world
    /// loading.
    /// </summary>
    [HarmonyPatch(typeof(AltBiomeWorldData), "GenerateAltBiomes")]
    public static class GenerateAltBiomes_Patch
    {
        [HarmonyPrefix]
        private static void Prefix(AltBiomeWorldData __instance)
        {
            try { AltBiomeCapture.Prefix(__instance); }
            catch (Exception e) { Plugin.Log.LogError("AltBiome prefix capture failed: " + e.Message); }
        }

        [HarmonyPostfix]
        private static void Postfix(AltBiomeWorldData __instance)
        {
            try { AltBiomeCapture.Postfix(__instance); }
            catch (Exception e) { Plugin.Log.LogError("AltBiome postfix capture failed: " + e.Message); }
        }
    }

    /// <summary>
    /// Registers the console commands. <c>Terminal.InitTerminal</c> is where Valheim builds its command
    /// table and six installed mods already postfix it, each only adding commands - the conventional,
    /// conflict-free place. Constructing a <c>Terminal.ConsoleCommand</c> registers it; nothing else
    /// is needed.
    /// </summary>
    [HarmonyPatch(typeof(Terminal), "InitTerminal")]
    public static class Terminal_InitTerminal_Patch
    {
        private static bool _registered;

        [HarmonyPostfix]
        private static void Postfix()
        {
            if (_registered) return;
            _registered = true;
            try
            {
                Register("seedlab_status",
                    "SeedLab dumper: print the arming mode, the output folder and whether a dump may run now.",
                    Commands.Status);

                Register("seedlab_dump",
                    "SeedLab dumper: dump the asset tables and this world's generator state. Solo host " +
                    "only. seedlab_dump [grid=none|coarse128|findlakes|edges|full12]",
                    Commands.Assets);

                Register("seedlab_natives",
                    "SeedLab dumper: dump the Mathf.PerlinNoise / UnityEngine.Random / FloatToHalf evidence. " +
                    "Works at the main menu.",
                    Commands.Natives);

                Register("seedlab_worldgen",
                    "SeedLab dumper: MAIN MENU ONLY. seedlab_worldgen <seedText> [<seedText> ...] " +
                    "[grid=none|coarse128|findlakes|full12] - per-seed WorldGenerator offsets, rivers and grids.",
                    Commands.WorldGen);

                Plugin.Log.LogInfo("Console commands registered: seedlab_status, seedlab_dump, " +
                                   "seedlab_natives, seedlab_worldgen.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not register console commands: " + e);
            }
        }

        private static void Register(string name, string description, Terminal.ConsoleEvent handler)
        {
            new Terminal.ConsoleCommand(
                name,
                description,
                handler,
                false,  // isCheat - these read game data and write outside the game folder
                false,  // isNetwork
                false,  // onlyServer
                false,  // isSecret
                false,  // allowInDevBuild
                false,  // hideBehindDevCommands
                null,   // optionsFetcher
                false,  // alwaysRefreshTabOptions
                false,  // remoteCommand - never runnable from another machine
                false); // onlyAdmin
        }
    }
}
