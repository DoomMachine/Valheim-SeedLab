using System;
using System.Collections.Generic;
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
                // Unbound until you choose: check a key against vanilla and every installed mod with
                // scripts\find-key-usage.ps1 -Plugins first. Never F4 - SeedLab's dumper holds it while armed.
                ToggleKey = Config.Bind("General", "ToggleKey", KeyCode.None, "What this key does.");
                // A new choice of key is tried afresh, even one that failed before (KeyPressed).
                ToggleKey.SettingChanged += (_, _) => _unreadableKeys.Clear();
            }
            catch (Exception e)
            {
                Log.LogError("Configuration failed to bind: " + e);
                enabled = false;   // stops Update, which would otherwise read the unbound entries every frame
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
            // Runs every frame: nothing may escape, and a repeating error must not flood the log. Keys are
            // handled in a try block of their own, so nothing going wrong with a key can stop the per-frame
            // work below.
            try
            {
                if (!IsTypingElsewhere() && KeyPressed(ToggleKey))
                {
                    // ...
                }
            }
            catch (Exception e)
            {
                LogThrottled(ref _keyErrors, "Key handling failed", e);
            }

            try
            {
                // ... per-frame work that must run whatever the keys do
            }
            catch (Exception e)
            {
                LogThrottled(ref _updateErrors, "Update failed", e);
            }
        }

        /// <summary>
        /// Reads a configurable key. Valheim 1.0.16's ZInput throws ArgumentOutOfRangeException on every read of
        /// 30 KeyCodes that BepInEx still offers as settings (Plus, Hash, Colon, F13-F15, WheelUp, WheelDown and
        /// others missing from its KeyCode-to-Key table - pitfalls.md section 5), so a player's choice of key must
        /// not be able to break the mod. The first failed read is logged once; after that the key reads as not
        /// pressed, like an unbound key, until the setting changes.
        /// </summary>
        public static bool KeyPressed(ConfigEntry<KeyCode> setting)
        {
            KeyCode key = setting.Value;
            if (key == KeyCode.None || _unreadableKeys.Contains((int)key)) return false;
            try
            {
                return ZInput.GetKeyDown(key, false);   // never UnityEngine.Input
            }
            catch (Exception e)
            {
                _unreadableKeys.Add((int)key);
                Log.LogWarning(setting.Definition.Key + " = " + key + ": Valheim cannot read this key ("
                               + e.GetType().Name + "), so it will do nothing. Choose another key.");
                return false;
            }
        }

        // ints, not KeyCodes: List<int>.Contains compares without boxing, and this runs every frame.
        private static readonly List<int> _unreadableKeys = new List<int>();

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

        private static int _keyErrors;
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
    /// a prefix that returns false skips the original for every mod, but never another mod's prefix
    /// (HarmonyX runs them all and ANDs the results) - only do it conditionally. Check who else patches
    /// the target with scripts\scan-mod-patches.ps1.
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
