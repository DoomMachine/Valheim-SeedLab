using System;
using System.IO;

namespace SeedLab.Dumper
{
    /// <summary>
    /// Which paths the dumper may write to. Deliberately dependency-free - no Unity, no BepInEx, no
    /// game types - so it can be linked into a plain test harness and the refusal can be PROVEN to
    /// fire rather than assumed: a tripwire nobody has seen fire is a suggestion, not a guard (the
    /// same rule as "Prove the tripwires" in this plugin's README.md).
    ///
    /// The rule this enforces: the game's save folders and the Steam Cloud folders are read-only,
    /// always. The user plays multiplayer with a Cartography Table and Steam Cloud syncs the world
    /// folder; a dumper that wrote there could corrupt a real save on another machine.
    /// </summary>
    internal static class PathPolicy
    {
        /// <summary>Whole path segments the dumper refuses to write under. Matched as complete
        /// segments, not substrings, so a user folder called "myworlds" is fine while
        /// <c>...\worlds\...</c> is not.
        ///
        /// <c>cache</c> is here because <c>AltBiomeWorldData.RemoveCache</c> keeps
        /// <c>&lt;LocalLow&gt;\IronGate\Valheim\cache\&lt;world&gt;_biomedatacache.bin</c> there;
        /// <c>remote</c> is the Steam Cloud directory
        /// (<c>...\userdata\&lt;id&gt;\892970\remote\worlds</c>).</summary>
        public static readonly string[] ForbiddenSegments =
        {
            "worlds", "worlds_local", "characters", "remote", "cache"
        };

        /// <summary>Returns null when the path may be written, otherwise the reason it may not.</summary>
        public static string Reject(string fullPath, string gameRoot, string saveDataRoot)
        {
            if (string.IsNullOrEmpty(fullPath)) return "the path is empty";

            string full;
            try { full = Path.GetFullPath(fullPath); }
            catch (Exception e) { return "the path cannot be resolved: " + e.Message; }

            if (!string.IsNullOrEmpty(gameRoot) && IsUnderOrEqual(full, gameRoot))
            {
                return "it is inside the game install (" + gameRoot + ")";
            }
            if (!string.IsNullOrEmpty(saveDataRoot) && IsUnderOrEqual(full, saveDataRoot))
            {
                return "it is inside the game's save data (" + saveDataRoot + ")";
            }

            foreach (string seg in full.Split('\\', '/'))
            {
                foreach (string bad in ForbiddenSegments)
                {
                    if (string.Equals(seg, bad, StringComparison.OrdinalIgnoreCase))
                    {
                        return "a path segment is '" + bad + "'";
                    }
                }
            }
            return null;
        }

        /// <summary>True when <paramref name="path"/> is <paramref name="parent"/> itself or below it.
        /// Compared segment-wise (the trailing separator is what stops "C:\Game2" matching
        /// "C:\Game"), case-insensitively, which is right for Windows and harmless elsewhere here.</summary>
        public static bool IsUnderOrEqual(string path, string parent)
        {
            try
            {
                string p = Path.GetFullPath(parent).TrimEnd('\\', '/');
                string c = Path.GetFullPath(path).TrimEnd('\\', '/');
                if (string.Equals(p, c, StringComparison.OrdinalIgnoreCase)) return true;
                return c.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || c.StartsWith(p + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // An unresolvable path is treated as "inside", i.e. refused. Failing closed is the
                // only safe direction for a write guard.
                return true;
            }
        }
    }
}
