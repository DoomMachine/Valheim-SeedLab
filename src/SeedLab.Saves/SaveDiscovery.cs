using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace SeedLab.Saves
{
    /// <summary>Where a save root came from, so a UI can say which copy of a world it is showing.</summary>
    public enum SaveRootKind
    {
        /// <summary>Supplied by the caller or by an environment variable.</summary>
        Explicit,

        /// <summary>
        /// The game's own data directory: <c>worlds_local</c>, <c>characters_local</c>, and the
        /// <b>minimap cache</b>, which always lives here even for a Steam Cloud world.
        /// </summary>
        GameData,

        /// <summary>A Steam Cloud <c>userdata/&lt;id&gt;/892970/remote</c> directory.</summary>
        SteamCloud,
    }

    /// <summary>A directory that contains <c>worlds</c> / <c>worlds_local</c> / <c>characters</c> subfolders.</summary>
    public sealed class ValheimSaveRoot
    {
        public ValheimSaveRoot(string path, SaveRootKind kind)
        {
            Path = path;
            Kind = kind;
        }

        public string Path { get; }
        public SaveRootKind Kind { get; }

        public string? WorldsDirectory => ExistingSub("worlds");
        public string? WorldsLocalDirectory => ExistingSub("worlds_local");
        public string? CharactersDirectory => ExistingSub("characters");
        public string? CharactersLocalDirectory => ExistingSub("characters_local");

        private string? ExistingSub(string name)
        {
            string p = System.IO.Path.Combine(Path, name);
            return Directory.Exists(p) ? p : null;
        }

        /// <summary>True when at least one of the four known subfolders is present.</summary>
        public bool LooksLikeSaveRoot =>
            WorldsDirectory != null || WorldsLocalDirectory != null
            || CharactersDirectory != null || CharactersLocalDirectory != null;

        public override string ToString() => Kind + ": " + Path;
    }

    /// <summary>
    /// One <c>_main.&lt;N&gt;.*</c> group inside a world folder.
    /// </summary>
    public sealed class WorldSaveSet
    {
        public WorldSaveSet(string worldName, string directory, int saveNumber,
                            string? fwlPath, string? dbPath, string? chunksPath, string? okPath,
                            IReadOnlyList<string> allFiles)
        {
            WorldName = worldName;
            Directory = directory;
            SaveNumber = saveNumber;
            FwlPath = fwlPath;
            DbPath = dbPath;
            ChunksPath = chunksPath;
            OkPath = okPath;
            AllFiles = allFiles;
        }

        /// <summary>The folder name, which is the game's <c>m_worldName</c>.</summary>
        public string WorldName { get; }

        public string Directory { get; }

        /// <summary>The N in <c>_main.N.fwl2</c>.</summary>
        public int SaveNumber { get; }

        public string? FwlPath { get; }
        public string? DbPath { get; }
        public string? ChunksPath { get; }
        public string? OkPath { get; }

        /// <summary>Every <c>_main.N.*</c> file in this group, including ones with unknown extensions.</summary>
        public IReadOnlyList<string> AllFiles { get; }

        /// <summary>
        /// All four members present - what <c>SaveCollection.KeepOnlyNewest</c> requires of the group
        /// it keeps (<c>num3 - num2 == 3</c> over a descending list, i.e. four files).
        /// </summary>
        public bool IsComplete => FwlPath != null && DbPath != null && ChunksPath != null && OkPath != null;

        public override string ToString() =>
            WorldName + " _main." + SaveNumber + (IsComplete ? " (complete)" : " (incomplete)");
    }

    /// <summary>
    /// Finds Valheim saves without touching the registry, WMI or any Windows-only API, and resolves
    /// which <c>_main.&lt;N&gt;.*</c> group to read. <b>Read-only</b>; see <see cref="IValheimReader"/>.
    /// <para>
    /// <b>The save number moves.</b> During the session that produced the ground truth,
    /// <c>asdasdasd</c> went from <c>_main.2.*</c> to <c>_main.3.*</c> because the user was playing,
    /// and <c>SaveCollection.KeepOnlyNewest</c> deletes every group it did not choose - including
    /// higher-numbered incomplete ones. So: re-resolve on every open and never cache a path
    /// (05-validation.md section 5.0).
    /// </para>
    /// </summary>
    public static class SaveDiscovery
    {
        /// <summary>Valheim's Steam app id, used for the <c>userdata/&lt;id&gt;/892970/remote</c> path.</summary>
        public const string SteamAppId = "892970";

        /// <summary>Overrides the save root entirely. Highest priority after an explicit argument.</summary>
        public const string SaveDirEnvironmentVariable = "SEEDLAB_SAVES_DIR";

        /// <summary>Points straight at a <c>userdata/&lt;id&gt;/892970/remote</c> directory.</summary>
        public const string SteamRemoteEnvironmentVariable = "SEEDLAB_STEAM_REMOTE";

        /// <summary>Points at a Steam installation root (the folder holding <c>userdata</c>).</summary>
        public const string SteamRootEnvironmentVariable = "SEEDLAB_STEAM_ROOT";

        /// <summary>
        /// Every save root worth looking in, most specific first.
        /// <list type="bullet">
        /// <item><paramref name="explicitRoot"/>, then <see cref="SaveDirEnvironmentVariable"/>.</item>
        /// <item>The game data directory. Windows: <c>%USERPROFILE%\AppData\LocalLow\IronGate\Valheim</c>
        /// - verified on this machine. Linux: <c>~/.config/unity3d/IronGate/Valheim</c>; macOS:
        /// <c>~/Library/Application Support/IronGate/Valheim</c> - the usual Unity conventions, but
        /// <b>Unverified:</b> neither was checked against a real install
        /// (08-architecture.md section 1.5).</item>
        /// <item>Steam Cloud: <c>&lt;steam&gt;/userdata/&lt;id&gt;/892970/remote</c> for every Steam
        /// root and every numeric user folder found.</item>
        /// </list>
        /// <para>
        /// <b>Steam's own location cannot be discovered reliably without the registry</b>, which this
        /// assembly will not use: the probe list below covers the default installs only. A Steam
        /// installed outside the default folders is <i>not</i> found by
        /// probing - which is exactly why <see cref="SteamRootEnvironmentVariable"/> and the explicit
        /// argument exist, and why a CLI must offer <c>--saves-dir</c>.
        /// </para>
        /// </summary>
        public static IReadOnlyList<ValheimSaveRoot> FindSaveRoots(string? explicitRoot = null)
        {
            List<ValheimSaveRoot> roots = new List<ValheimSaveRoot>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string? path, SaveRootKind kind)
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                string full;
                try { full = Path.GetFullPath(path); }
                catch (Exception) { return; }
                if (!Directory.Exists(full)) return;
                if (!seen.Add(full)) return;
                ValheimSaveRoot root = new ValheimSaveRoot(full, kind);
                if (root.LooksLikeSaveRoot || kind == SaveRootKind.Explicit) roots.Add(root);
            }

            Add(explicitRoot, SaveRootKind.Explicit);
            Add(Environment.GetEnvironmentVariable(SaveDirEnvironmentVariable), SaveRootKind.Explicit);
            Add(Environment.GetEnvironmentVariable(SteamRemoteEnvironmentVariable), SaveRootKind.SteamCloud);

            foreach (string path in GameDataDirectoryCandidates()) Add(path, SaveRootKind.GameData);
            foreach (string path in SteamRemoteDirectoryCandidates()) Add(path, SaveRootKind.SteamCloud);

            return roots;
        }

        /// <summary>
        /// The game's own data directory candidates for this OS. The Windows one is verified
        /// (<c>Utils.GetSaveDataPath</c> / <c>app.info</c>); the other two are the usual Unity
        /// conventions and are <b>Unverified</b>.
        /// </summary>
        public static IReadOnlyList<string> GameDataDirectoryCandidates()
        {
            List<string> result = new List<string>();
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) return result;

            if (OperatingSystem.IsWindows())
            {
                result.Add(Path.Combine(home, "AppData", "LocalLow", "IronGate", "Valheim"));
            }
            else if (OperatingSystem.IsMacOS())
            {
                result.Add(Path.Combine(home, "Library", "Application Support", "IronGate", "Valheim"));
            }
            else
            {
                result.Add(Path.Combine(home, ".config", "unity3d", "IronGate", "Valheim"));
                // Proton/Steam Deck: the Windows build's LocalLow inside the prefix.
                result.Add(Path.Combine(home, ".steam", "steam", "steamapps", "compatdata", SteamAppId,
                                        "pfx", "drive_c", "users", "steamuser", "AppData", "LocalLow",
                                        "IronGate", "Valheim"));
            }

            return result;
        }

        /// <summary>
        /// Steam installation roots to probe - the folder that holds <c>userdata</c>. Default
        /// locations only; see the remarks on <see cref="FindSaveRoots"/>.
        /// </summary>
        public static IReadOnlyList<string> SteamRootCandidates()
        {
            List<string> result = new List<string>();
            string? env = Environment.GetEnvironmentVariable(SteamRootEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(env)) result.Add(env);

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (OperatingSystem.IsWindows())
            {
                string? pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
                string? pf = Environment.GetEnvironmentVariable("ProgramFiles");
                if (!string.IsNullOrEmpty(pf86)) result.Add(Path.Combine(pf86, "Steam"));
                if (!string.IsNullOrEmpty(pf)) result.Add(Path.Combine(pf, "Steam"));
            }
            else if (OperatingSystem.IsMacOS())
            {
                if (!string.IsNullOrEmpty(home)) result.Add(Path.Combine(home, "Library", "Application Support", "Steam"));
            }
            else if (!string.IsNullOrEmpty(home))
            {
                result.Add(Path.Combine(home, ".steam", "steam"));
                result.Add(Path.Combine(home, ".steam", "root"));
                result.Add(Path.Combine(home, ".local", "share", "Steam"));
                result.Add(Path.Combine(home, "Steam"));
            }

            return result;
        }

        /// <summary>
        /// <c>&lt;steamRoot&gt;/userdata/&lt;accountId&gt;/892970/remote</c> for every Steam root and
        /// account folder that exists. The account id is not known in advance, so every numeric
        /// folder is tried.
        /// </summary>
        public static IReadOnlyList<string> SteamRemoteDirectoryCandidates()
        {
            List<string> result = new List<string>();
            foreach (string steamRoot in SteamRootCandidates())
            {
                string userdata = Path.Combine(steamRoot, "userdata");
                if (!Directory.Exists(userdata)) continue;

                string[] accounts;
                try { accounts = Directory.GetDirectories(userdata); }
                catch (Exception) { continue; }

                foreach (string account in accounts)
                {
                    string remote = Path.Combine(account, SteamAppId, "remote");
                    if (Directory.Exists(remote)) result.Add(remote);
                }
            }
            return result;
        }

        /// <summary>
        /// Every world folder under a root: both <c>worlds</c> (Steam Cloud) and <c>worlds_local</c>.
        /// A world folder's name is the game's <c>m_worldName</c>.
        /// </summary>
        public static IReadOnlyList<string> WorldDirectories(ValheimSaveRoot root)
        {
            List<string> result = new List<string>();
            foreach (string? parent in new[] { root.WorldsDirectory, root.WorldsLocalDirectory })
            {
                if (parent == null) continue;
                try
                {
                    foreach (string dir in Directory.GetDirectories(parent)) result.Add(dir);
                }
                catch (Exception)
                {
                    // An unreadable folder is not a reason to fail the whole scan.
                }
            }
            return result;
        }

        /// <summary>Every <c>*.fch</c> under a root's character folders.</summary>
        public static IReadOnlyList<string> CharacterFiles(ValheimSaveRoot root)
        {
            List<string> result = new List<string>();
            foreach (string? parent in new[] { root.CharactersDirectory, root.CharactersLocalDirectory })
            {
                if (parent == null) continue;
                try
                {
                    foreach (string f in Directory.GetFiles(parent, "*.fch")) result.Add(f);
                }
                catch (Exception)
                {
                }
            }
            return result;
        }

        /// <summary>
        /// The minimap cache directory for a world, if one exists.
        /// <para>
        /// <c>Minimap.Start</c> (line 571) builds it from
        /// <c>ZNet.World.GetSaveDirectory(FileHelpers.FileSource.Local)</c> - <b>always Local</b>, so
        /// the cache for a Steam Cloud world still lives under <c>worlds_local/&lt;worldName&gt;/</c>
        /// in the game data directory, not beside the save (05-validation.md section 1.1).
        /// </para>
        /// </summary>
        public static string? FindMinimapCacheDirectory(string worldName, IEnumerable<ValheimSaveRoot> roots)
        {
            foreach (ValheimSaveRoot root in roots)
            {
                foreach (string? parent in new[] { root.WorldsLocalDirectory, root.WorldsDirectory })
                {
                    if (parent == null) continue;
                    string dir = Path.Combine(parent, worldName);
                    if (MinimapCacheReader.Exists(dir)) return dir;
                }
            }
            return null;
        }

        /// <summary>
        /// Every <c>_main.&lt;N&gt;.*</c> group in a world folder, newest first. Files whose number
        /// does not parse are reported through <paramref name="warnings"/> and skipped; the game logs
        /// "Failed to parse files for save: &lt;dir&gt;" and drops the whole world in that case, which
        /// would be the wrong thing for a read-only inspector to do.
        /// </summary>
        public static IReadOnlyList<WorldSaveSet> EnumerateSaveSets(string worldDirectory, List<string>? warnings = null)
        {
            string worldName = new DirectoryInfo(worldDirectory).Name;
            Dictionary<int, List<string>> groups = new Dictionary<int, List<string>>();

            string[] files;
            try { files = Directory.GetFiles(worldDirectory, "_main.*"); }
            catch (Exception ex)
            {
                warnings?.Add(worldDirectory + ": " + ex.Message);
                return Array.Empty<WorldSaveSet>();
            }

            foreach (string file in files)
            {
                int? number = TryParseSaveNumber(file);
                if (number == null)
                {
                    warnings?.Add("Could not parse a save number from " + file +
                                  "; the game would drop this whole world folder from its list.");
                    continue;
                }
                if (!groups.TryGetValue(number.Value, out List<string>? list))
                {
                    list = new List<string>();
                    groups[number.Value] = list;
                }
                list.Add(file);
            }

            List<int> numbers = new List<int>(groups.Keys);
            numbers.Sort();
            numbers.Reverse();

            List<WorldSaveSet> result = new List<WorldSaveSet>(numbers.Count);
            foreach (int n in numbers)
            {
                List<string> list = groups[n];
                string? fwl = null, db = null, chunks = null, ok = null;
                foreach (string f in list)
                {
                    string ext = Path.GetExtension(f);
                    if (ext.Equals(".fwl2", StringComparison.OrdinalIgnoreCase)) fwl = f;
                    else if (ext.Equals(".db2", StringComparison.OrdinalIgnoreCase)) db = f;
                    else if (ext.Equals(".chunks", StringComparison.OrdinalIgnoreCase)) chunks = f;
                    else if (ext.Equals(".ok", StringComparison.OrdinalIgnoreCase)) ok = f;
                }
                result.Add(new WorldSaveSet(worldName, worldDirectory, n, fwl, db, chunks, ok, list));
            }

            return result;
        }

        /// <summary>
        /// The group to read: the highest complete one, or - failing that - the highest one that at
        /// least has a <c>.fwl2</c>.
        /// <para>
        /// <c>SaveCollection.KeepOnlyNewest</c> walks the groups in descending order and keeps the
        /// first with four members, with one special case: a lone <c>_main.0.fwl2</c> and nothing else
        /// (a world created but never saved). This mirrors that choice, but falls back to an
        /// incomplete group rather than returning nothing - a copied-out fixture may be missing the
        /// <c>.ok</c> file, and refusing to read it would be unhelpful. Check
        /// <see cref="WorldSaveSet.IsComplete"/> if that matters.
        /// </para>
        /// </summary>
        public static WorldSaveSet? ResolveNewestSaveSet(string worldDirectory, List<string>? warnings = null)
        {
            IReadOnlyList<WorldSaveSet> sets = EnumerateSaveSets(worldDirectory, warnings);

            foreach (WorldSaveSet set in sets)
                if (set.IsComplete) return set;

            // The game's other accepted shape: a lone _main.0.fwl2.
            foreach (WorldSaveSet set in sets)
                if (set.SaveNumber == 0 && set.AllFiles.Count == 1 && set.FwlPath != null) return set;

            foreach (WorldSaveSet set in sets)
            {
                if (set.FwlPath != null)
                {
                    warnings?.Add(worldDirectory + ": no complete _main.N group; falling back to the " +
                                  "highest with a .fwl2 (_main." + set.SaveNumber + ").");
                    return set;
                }
            }

            return null;
        }

        /// <summary>
        /// The N in <c>_main.N.ext</c>, using the game's own expression:
        /// <c>int.Parse(Path.GetExtension(Path.GetFileNameWithoutExtension(f)).Substring(1))</c>
        /// (<c>SaveCollection.KeepOnlyNewest</c>). Returns null instead of throwing.
        /// </summary>
        public static int? TryParseSaveNumber(string path)
        {
            string withoutExt = Path.GetFileNameWithoutExtension(path);
            string numberPart = Path.GetExtension(withoutExt);
            if (numberPart.Length < 2) return null;
            if (int.TryParse(numberPart.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                return n;
            return null;
        }
    }
}
