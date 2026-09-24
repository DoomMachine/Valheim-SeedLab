using System;
using System.Collections.Generic;
using System.IO;
using SeedLab.Saves;

namespace SeedLab.Cli.Infra
{
    /// <summary>
    /// Finds the Steam installation root - the folder that holds <c>userdata</c>, and therefore the
    /// Steam Cloud copies of the worlds under
    /// <c>userdata\&lt;accountId&gt;\892970\remote\worlds\</c>.
    ///
    /// <para><c>SaveDiscovery</c> probes only the default install locations plus the
    /// <c>SEEDLAB_STEAM_ROOT</c> environment variable. A Steam installed outside the default folders
    /// is neither, and the game's own save folder holds only the minimap
    /// caches - so without this probe <c>vseed worlds</c> lists world names with no seeds. Rather than
    /// reach into another project, the CLI finds the root and hands it over through the environment
    /// variable that project already documents.</para>
    ///
    /// <para>The probe is a bounded list of plausible paths on fixed drives, never a filesystem walk.
    /// It reads nothing outside a <c>userdata</c> folder, and it is skipped entirely when
    /// <c>SEEDLAB_STEAM_ROOT</c> is already set.</para>
    /// </summary>
    public static class SteamProbe
    {
        private static bool s_done;

        /// <summary>Sets SEEDLAB_STEAM_ROOT for this process if it is unset and a root can be found.</summary>
        public static void EnsureSteamRoot()
        {
            if (s_done) return;
            s_done = true;

            string? already = Environment.GetEnvironmentVariable(SaveDiscovery.SteamRootEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(already)) return;

            foreach (string cand in Candidates())
            {
                try
                {
                    if (!Directory.Exists(Path.Combine(cand, "userdata"))) continue;
                }
                catch (Exception)
                {
                    continue;
                }

                Environment.SetEnvironmentVariable(SaveDiscovery.SteamRootEnvironmentVariable, cand);
                return;
            }
        }

        private static IEnumerable<string> Candidates()
        {
            if (OperatingSystem.IsWindows())
            {
                string?[] fixedRoots =
                {
                    Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                    Environment.GetEnvironmentVariable("ProgramFiles"),
                };

                foreach (string? r in fixedRoots)
                {
                    if (!string.IsNullOrEmpty(r)) yield return Path.Combine(r, "Steam");
                }

                string[] suffixes = { "Steam", @"Games\Steam", @"Program Files (x86)\Steam", @"Program Files\Steam", @"SteamLibrary\Steam" };
                foreach (DriveInfo d in SafeDrives())
                {
                    foreach (string s in suffixes) yield return Path.Combine(d.Name, s);
                }
            }
            else
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrEmpty(home)) yield break;
                yield return Path.Combine(home, ".steam", "steam");
                yield return Path.Combine(home, ".steam", "root");
                yield return Path.Combine(home, ".local", "share", "Steam");
                yield return Path.Combine(home, "Library", "Application Support", "Steam");
                yield return Path.Combine(home, "Steam");
            }
        }

        private static IEnumerable<DriveInfo> SafeDrives()
        {
            DriveInfo[] all;
            try { all = DriveInfo.GetDrives(); }
            catch (Exception) { yield break; }

            foreach (DriveInfo d in all)
            {
                bool usable;
                try { usable = d.DriveType == DriveType.Fixed && d.IsReady; }
                catch (Exception) { usable = false; }

                if (usable) yield return d;
            }
        }
    }
}
