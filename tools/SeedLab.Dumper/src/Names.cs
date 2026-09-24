using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SeedLab.Dumper
{
    /// <summary>
    /// The game's own identity for a GameObject.
    ///
    /// <b>A raw <c>GameObject.name</c> is not an identity.</b> Unity appends <c> (1)</c>, <c> (2)</c>
    /// to duplicated siblings and <c>(Clone)</c> to instantiated ones, and Valheim's authored prefabs
    /// are full of both. Everything in the game that asks "what prefab is this" first runs the name
    /// through <c>Utils.GetPrefabName</c>:
    /// <code>
    /// private static readonly char[] extraCharacters = new char[2] { '(', ' ' };
    /// public static string GetPrefabName(string name)
    /// {
    ///     int num = name.IndexOfAny(extraCharacters);
    ///     return num != -1 ? name.Remove(num) : name;
    /// }
    /// </code>
    /// (assembly_utils, 1.0.15, read from the shipped IL on 2026-09-23 - the truncation set is exactly
    /// <c>'('</c> and <c>' '</c>, the FIRST occurrence of either, and the rest of the string is
    /// discarded.)
    ///
    /// The dumper CALLS that method rather than reimplementing it, so a future change to the character
    /// set cannot leave this file quietly disagreeing with the game. It only falls back to its own copy
    /// of the rule if the call throws, and it says so in the log when it does.
    ///
    /// <b>Why this matters here.</b> A child authored as <c>piece_maypole (1)</c> is, to the game,
    /// <c>piece_maypole</c>. An exact-match query for <c>piece_maypole</c> against raw names would miss
    /// it and report "not in this prefab" - and a false "not found" is the single worst answer this
    /// dump can produce, because the user reads it as "this world has no maypole" and stops looking.
    /// </summary>
    internal static class Names
    {
        private static bool _warned;

        /// <summary><c>Utils.GetPrefabName(name)</c>. Null in, null out; never throws.</summary>
        public static string Normalize(string name)
        {
            if (name == null) return null;
            try
            {
                return Utils.GetPrefabName(name);
            }
            catch (Exception e)
            {
                if (!_warned)
                {
                    _warned = true;
                    Plugin.Log.LogWarning("Utils.GetPrefabName threw (" + e.GetType().Name + ": " +
                                          e.Message + "); the dumper is falling back to its own copy " +
                                          "of the rule (truncate at the first '(' or ' '). If the game " +
                                          "has changed that rule, every normalised name in this dump " +
                                          "is wrong - re-read Utils.GetPrefabName before using it.");
                }
                int i = name.IndexOfAny(Extra);
                return i >= 0 ? name.Substring(0, i) : name;
            }
        }

        /// <summary>The fallback's character set, kept identical to <c>Utils.extraCharacters</c>. The
        /// preflight asserts that field still exists and that <c>GetPrefabName</c> still has exactly one
        /// <c>IndexOfAny</c> and one <c>Remove</c>, so a change of shape is a build-time failure rather
        /// than a silently different answer.</summary>
        private static readonly char[] Extra = new char[] { '(', ' ' };

        /// <summary><c>Utils.GetPrefabName(go.name)</c>, guarding a destroyed or null GameObject.</summary>
        public static string Normalize(GameObject go)
        {
            if (go == null) return null;
            return Normalize(go.name);
        }

        /// <summary>The stable hash of the NORMALISED name - the identity a ZDO, the location table and
        /// <c>ZNetScene</c> all key on. 0 when the name is null.</summary>
        public static int Hash(string normalized)
        {
            if (string.IsNullOrEmpty(normalized)) return 0;
            try { return normalized.GetStableHashCode(); }
            catch { return 0; }
        }

        // ---- display names -------------------------------------------------------------------------

        private static Dictionary<string, string> _translations;
        private static bool _translationsTried;
        private static string _translationsLanguage;

        /// <summary>The language that was selected when the table was first read, or "".</summary>
        public static string TranslationsLanguage()
        {
            return _translationsLanguage ?? "";
        }

        /// <summary>
        /// The game's token-to-text table, read once from <c>Localization.m_translations</c> by
        /// reflection. Empty (never null) when it cannot be read - a missing table must make names
        /// absent, not make the dump fail.
        ///
        /// <para>The field is private and the public <c>Localize</c> would have to be asked one token
        /// at a time for names nobody has thought of yet, so the whole table is taken.</para>
        /// </summary>
        public static Dictionary<string, string> Translations()
        {
            if (_translationsTried) return _translations ?? Empty;
            _translationsTried = true;
            try
            {
                Localization inst = Localization.instance;
                if (inst == null) return Empty;
                FieldInfo f = AccessTools.Field(typeof(Localization), "m_translations");
                if (f == null) return Empty;
                var d = f.GetValue(inst) as Dictionary<string, string>;
                if (d == null) return Empty;
                _translations = new Dictionary<string, string>(d, StringComparer.Ordinal);
                try { _translationsLanguage = inst.GetSelectedLanguage(); } catch { }
                return _translations;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Localization.m_translations could not be read (" + e.Message +
                                      "); every name in this dump will be a raw token.");
                return Empty;
            }
        }

        private static readonly Dictionary<string, string> Empty = new Dictionary<string, string>();

        /// <summary>
        /// A <c>$token</c> resolved through <see cref="Translations"/>, or <b>null</b> when it has no
        /// entry. Null rather than the token: a consumer must be able to tell "this has no name" from
        /// "this is named '$enemy_gdking'", and <c>Localization.Localize</c> conflates the two by
        /// returning the decorated token.
        /// </summary>
        public static string Localize(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            string key = token[0] == '$' ? token.Substring(1) : token;
            string v;
            return Translations().TryGetValue(key, out v) ? v : null;
        }

        /// <summary>
        /// <c>ItemDrop.m_itemData.m_shared.m_name</c> off a drop-table entry's prefab, or null when the
        /// prefab carries no <c>ItemDrop</c> (a data bug worth seeing rather than hiding).
        /// </summary>
        public static string ItemToken(GameObject item)
        {
            if (item == null) return null;
            try
            {
                ItemDrop drop = item.GetComponent<ItemDrop>();
                if (drop == null || drop.m_itemData == null || drop.m_itemData.m_shared == null) return null;
                return drop.m_itemData.m_shared.m_name;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
