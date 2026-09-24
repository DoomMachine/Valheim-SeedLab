using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace SeedLab.Data
{
    /// <summary>What a stamp comparison against the installed game concluded.</summary>
    public enum StampMatch
    {
        /// <summary>The installed <c>assembly_valheim.dll</c> hashes to the stamp's value.</summary>
        Match = 0,

        /// <summary>A game is installed and it is a DIFFERENT build. The asset tables are stale.</summary>
        Mismatch = 1,

        /// <summary>No Valheim install was found, so nothing contradicts the data and nothing confirms it.</summary>
        GameNotFound = 2,

        /// <summary>A game directory was found but its assembly could not be read (locked, permissions).</summary>
        Unreadable = 3,
    }

    /// <summary>The result of comparing the shipped DATA-STAMP with the installed game.</summary>
    public sealed class StampCheck
    {
        internal StampCheck(StampMatch result, DataStamp stamp, string? gameDirectory,
                            string? installedSha256, string message)
        {
            Result = result;
            Stamp = stamp;
            GameDirectory = gameDirectory;
            InstalledSha256 = installedSha256;
            Message = message;
        }

        public StampMatch Result { get; }
        public DataStamp Stamp { get; }

        /// <summary>The Valheim install that was compared against, or null when none was found.</summary>
        public string? GameDirectory { get; }

        /// <summary>SHA-256 of that install's <c>assembly_valheim.dll</c>, or null.</summary>
        public string? InstalledSha256 { get; }

        /// <summary>One line, suitable for printing as-is.</summary>
        public string Message { get; }

        public bool IsMatch => Result == StampMatch.Match;
    }

    /// <summary>
    /// Finds the Valheim install and hashes <c>valheim_Data\Managed\assembly_valheim.dll</c> - the
    /// same artifact, and the same comparison, as
    /// <c>tools\check-game-version.ps1</c>. Nothing here writes, and
    /// nothing here reads a save folder.
    /// </summary>
    public static class GameInstall
    {
        /// <summary>Points the tool at a specific install; <c>tools\check-game-version.ps1</c> honours it too.</summary>
        public const string DirectoryEnvironmentVariable = "SEEDLAB_VALHEIM_DIR";

        /// <summary>
        /// Set to 1/true to turn <see cref="StampMatch.GameNotFound"/> into a refusal as well. Off by
        /// default so the tool still answers on a machine with no Valheim installed; on, for a machine
        /// where a silently un-verified answer would be worse than no answer.
        /// </summary>
        public const string RequireMatchEnvironmentVariable = "SEEDLAB_REQUIRE_GAME_MATCH";

        public static string AssemblyPath(string gameDirectory)
            => Path.Combine(gameDirectory, "valheim_Data", "Managed", "assembly_valheim.dll");

        /// <summary>
        /// The install directory: <c>SEEDLAB_VALHEIM_DIR</c>, then by walking up from the working
        /// directory and from the binary (SeedLab lives inside the game folder on this machine), then
        /// the usual Steam roots. Returns null when nothing is found.
        /// </summary>
        public static string? FindDirectory() => FindDirectory(out _);

        /// <summary>
        /// The same search, with the reason it came up empty.
        ///
        /// <para>An explicitly set <see cref="DirectoryEnvironmentVariable"/> is authoritative even
        /// when it is wrong: if it names something that is not a Valheim install, the search STOPS
        /// there rather than quietly checking against a different install than the one the user
        /// named. Silently comparing against another copy of the game is how a stale-data check
        /// passes for the wrong reason.</para>
        /// </summary>
        public static string? FindDirectory(out string? whyNot)
        {
            whyNot = null;
            string? env = Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable);
            if (!string.IsNullOrEmpty(env))
            {
                if (HasAssembly(env)) return env;

                whyNot = DirectoryEnvironmentVariable + " is set to '" + env + "', which has no "
                         + @"valheim_Data\Managed\assembly_valheim.dll. Nothing else was searched, "
                         + "because checking a different install than the one you named would answer "
                         + "the wrong question.";
                return null;
            }

            foreach (string start in new[] { SafeCurrentDirectory(), AppContext.BaseDirectory })
            {
                if (start.Length == 0) continue;
                DirectoryInfo? d = new DirectoryInfo(start);
                for (int i = 0; i < 12 && d != null; i++, d = d.Parent)
                {
                    if (HasAssembly(d.FullName)) return d.FullName;
                }
            }

            foreach (string root in SteamCandidates())
            {
                string cand = Path.Combine(root, "steamapps", "common", "Valheim");
                if (HasAssembly(cand)) return cand;
            }

            whyNot = @"no valheim_Data\Managed\assembly_valheim.dll was found by walking up from the "
                     + "working directory or the binary, or in the usual Steam library folders. Set "
                     + DirectoryEnvironmentVariable + " to the install.";
            return null;
        }

        /// <summary>Compares the shipped stamp against the installed game.</summary>
        public static StampCheck Check(DataStamp stamp)
        {
            if (stamp == null) throw new ArgumentNullException(nameof(stamp));

            string? dir = FindDirectory(out string? whyNot);
            if (dir == null)
            {
                return new StampCheck(
                    StampMatch.GameNotFound, stamp, null, null,
                    "the data could not be checked against an installed game: "
                    + (whyNot ?? "no Valheim install was found.")
                    + " The data itself says Valheim " + stamp.GameVersion + ", dumped " + stamp.Dumped + ".");
            }

            string sha;
            try
            {
                sha = Sha256File(AssemblyPath(dir));
            }
            catch (Exception ex)
            {
                return new StampCheck(
                    StampMatch.Unreadable, stamp, dir, null,
                    "the installed assembly_valheim.dll at " + AssemblyPath(dir) + " could not be read ("
                    + ex.GetType().Name + "), so the data could not be checked against the game.");
            }

            if (string.Equals(sha, stamp.AssemblyValheimSha256, StringComparison.OrdinalIgnoreCase))
            {
                return new StampCheck(
                    StampMatch.Match, stamp, dir, sha,
                    "the installed game is the build this data was dumped from (Valheim "
                    + stamp.GameVersion + ", assembly_valheim " + sha.Substring(0, 8) + ").");
            }

            return new StampCheck(
                StampMatch.Mismatch, stamp, dir, sha,
                "the installed game is NOT the build this data was dumped from. Installed "
                + "assembly_valheim.dll is " + sha + "; the data was dumped from "
                + stamp.AssemblyValheimSha256 + " (Valheim " + stamp.GameVersion + ", "
                + stamp.Dumped + ").");
        }

        public static string Sha256File(string path)
        {
            using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }

        private static bool HasAssembly(string dir)
        {
            try { return File.Exists(AssemblyPath(dir)); }
            catch { return false; }
        }

        private static string SafeCurrentDirectory()
        {
            try { return Directory.GetCurrentDirectory(); }
            catch { return ""; }
        }

        private static IEnumerable<string> SteamCandidates()
        {
            yield return @"C:\Program Files (x86)\Steam";
            yield return @"C:\Program Files\Steam";

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                yield return Path.Combine(home, ".steam", "steam");
                yield return Path.Combine(home, ".local", "share", "Steam");
                yield return Path.Combine(home, "Library", "Application Support", "Steam");
            }

            for (char drive = 'D'; drive <= 'Z'; drive++)
            {
                yield return drive + @":\SteamLibrary";
                yield return drive + @":\Steam";
            }
        }
    }
}
