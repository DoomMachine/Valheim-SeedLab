using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ExampleMod
{
    /// <summary>
    /// Starter plugin. Every default here exists because the opposite went wrong in a real mod; the
    /// reasons are in the valheim-modding skill (references/pitfalls.md).
    /// </summary>
    [BepInPlugin(GUID, NAME, VERSION)]
    [BepInProcess("valheim.exe")]          // client only; the dedicated server is valheim_server.exe
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "DoomMachine.ExampleMod";   // Author.Mod - also names the .cfg file
        public const string NAME = "ExampleMod";
        public const string VERSION = "1.0.0";

        public static ManualLogSource Log;
        public static ConfigEntry<KeyCode> ToggleKey;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            // BepInEx drops a plugin that throws in Awake, so each stage is guarded on its own.
            try
            {
                // Vanilla does not use F4, but other mods may (SeedLab's dumper holds it while armed).
                // Check your own install with scripts\find-key-usage.ps1 -Plugins before choosing a default.
                ToggleKey = Config.Bind("General", "ToggleKey", KeyCode.F4, "What this key does.");
            }
            catch (Exception e)
            {
                Log.LogError("Configuration failed to bind: " + e);
                return;
            }

            _harmony = new Harmony(GUID);
            ApplyPatches();
            Log.LogInfo(NAME + " " + VERSION + " loaded.");
        }

        /// <summary>
        /// One PatchAll per patch class, not PatchAll(assembly): PatchAll aborts on the first target it
        /// cannot resolve, so a single method renamed by a game update would disable every patch.
        /// </summary>
        private void ApplyPatches()
        {
            Type[] patchClasses = new Type[]
            {
                typeof(ExamplePatch),
            };
            int applied = 0;
            foreach (Type t in patchClasses)
            {
                try { _harmony.PatchAll(t); applied++; }
                catch (Exception e) { Log.LogError("Patch " + t.Name + " failed, that feature is off: " + e.Message); }
            }
            Log.LogInfo(string.Format("Applied {0} of {1} patches.", applied, patchClasses.Length));
        }

        private void Update()
        {
            // Runs every frame: nothing may escape, and a repeating error must not flood the log.
            try
            {
                if (!IsTypingElsewhere() && ZInput.GetKeyDown(ToggleKey.Value, false))   // never UnityEngine.Input
                {
                    // ...
                }
            }
            catch (Exception e)
            {
                LogThrottled(ref _updateErrors, "Update failed", e);
            }
        }

        /// <summary>
        /// Set this while your own window is open, if you add one. A window that takes the keyboard does so
        /// by postfixing TextInput.IsVisible to return true (see pitfalls.md) - and then the check below
        /// would block the very key that closes the window: it could open but never close.
        /// </summary>
        public static bool OwnWindowOpen;

        /// <summary>True while a game text field owns the keyboard, so hotkeys must stand down.</summary>
        public static bool IsTypingElsewhere()
        {
            if (OwnWindowOpen) return false;   // we are the ones making TextInput.IsVisible true
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

        private static int _updateErrors;
        private static void LogThrottled(ref int counter, string context, Exception e)
        {
            counter++;
            if (counter <= 3) Log.LogError(context + ": " + e);
            else if (counter == 4) Log.LogError(context + ": repeating, further occurrences not logged.");
        }

        private void OnDestroy()
        {
            if (_harmony != null) _harmony.UnpatchSelf();
        }
    }

    /// <summary>
    /// Example patch. Harmony reaches private methods by name, so accessibility does not matter here;
    /// a prefix that returns false skips the original AND every later prefix from other mods - only do
    /// it conditionally. Check who else patches the target with scripts\scan-mod-patches.ps1.
    /// </summary>
    [HarmonyPatch(typeof(Hud), "Update")]
    public static class ExamplePatch
    {
        private static void Postfix()
        {
            // ...
        }
    }
}
