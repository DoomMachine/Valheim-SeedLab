using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace SeedLab.Cli.Infra
{
    /// <summary>
    /// The build this tool was verified against, and where its ground truth lives.
    /// Everything here is a fact about a specific Valheim install; if the install changes, the tool
    /// says so loudly rather than quietly producing numbers for a different game.
    /// </summary>
    public static class Verified
    {
        public const string EngineVersion = "0.1.0";

        /// <summary>The game build every SeedLab number was measured against.</summary>
        public const string GameVersion = "1.0.15";

        public const int NetworkVersion = 40;

        public const string SteamBuildId = "25390630";

        /// <summary>
        /// SHA-256 of <c>valheim_Data\Managed\assembly_valheim.dll</c> for that build - the stamp
        /// <c>tools\check-game-version.ps1</c> reads from this file and compares the installed game
        /// against, and the hash the DATA-STAMP of the dumped data records. Same hash means same code:
        /// a different one means every ported constant needs re-verifying before it is trusted.
        /// </summary>
        public const string AssemblyValheimSha256 =
            "59f53fb55d99d22a33e8ed094eec8d21e9f133543bce92bc3d80dce44033adb1";

        /// <summary>The world-gen version the ported generator reproduces.</summary>
        public const int WorldGenVersion = 2;

        /// <summary>A ground-truth world: what the game itself generated and wrote to disk.</summary>
        public sealed class Fixture
        {
            public Fixture(string name, string seedText, int seed, bool holdOut)
            {
                Name = name; SeedText = seedText; Seed = seed; HoldOut = holdOut;
            }

            public string Name { get; }
            public string SeedText { get; }
            public int Seed { get; }

            /// <summary>True for the hold-out world: never used while the biome and height code was ported.</summary>
            public bool HoldOut { get; }
        }

        public static readonly Fixture[] Fixtures =
        {
            new Fixture("asdasdasd", "MWd8eV6svz", -1772362158, holdOut: false),
            new Fixture("testworldclaude", "hnBd9gJf2G", 319486907, holdOut: true),
        };

        /// <summary>
        /// The <c>groundtruth\</c> folder, found by walking up from the working directory and from the
        /// binary, so the tool behaves the same whether it is run with <c>dotnet run</c> or as an exe.
        /// </summary>
        public static string? FindGroundTruth()
        {
            foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                DirectoryInfo? d = new DirectoryInfo(start);
                for (int i = 0; i < 12 && d != null; i++, d = d.Parent)
                {
                    string cand = Path.Combine(d.FullName, "groundtruth");
                    if (Directory.Exists(Path.Combine(cand, "decoded"))) return cand;
                }
            }

            return null;
        }

        /// <summary>
        /// The Valheim install directory: <c>SEEDLAB_VALHEIM_DIR</c> first, then by walking up from
        /// here (SeedLab lives inside the game folder on this machine), then the usual Steam paths.
        /// </summary>
        public static string? FindGameDirectory()
        {
            string? env = Environment.GetEnvironmentVariable("SEEDLAB_VALHEIM_DIR");
            if (!string.IsNullOrEmpty(env) && HasAssembly(env)) return env;

            foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
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

            return null;
        }

        private static bool HasAssembly(string dir)
        {
            try { return File.Exists(AssemblyPath(dir)); }
            catch { return false; }
        }

        public static string AssemblyPath(string gameDir)
            => Path.Combine(gameDir, "valheim_Data", "Managed", "assembly_valheim.dll");

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

        public static string Sha256File(string path)
        {
            using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            using SHA256 sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
        }
    }
}
