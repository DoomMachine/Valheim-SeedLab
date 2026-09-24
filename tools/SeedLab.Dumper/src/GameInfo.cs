using System;
using System.Globalization;
using System.IO;
using SeedLab.Contracts.Dump;

namespace SeedLab.Dumper
{
    /// <summary>
    /// Identifies the exact build the dump came from, so the offline tool can refuse stale data after
    /// a game update instead of producing plausible nonsense.
    ///
    /// Two hashes, not one. <c>assembly_valheim.dll</c> covers the game code - it is the same artifact
    /// <c>check-game-version.ps1</c> hashes, so the stamps can be compared by eye. <c>UnityPlayer.dll</c>
    /// covers the natives: <c>Mathf.PerlinNoise</c>, <c>UnityEngine.Random</c> and
    /// <c>Mathf.FloatToHalf</c> all live there, and a Unity upgrade could change them while the game
    /// code is untouched.
    /// </summary>
    internal static class GameInfo
    {
        private static GameInfoDef _cached;

        public static GameInfoDef Read()
        {
            if (_cached != null) return _cached;

            var info = new GameInfoDef();
            try { info.version = global::Version.GetVersionString(); }
            catch (Exception e) { Plugin.Log.LogWarning("Version.GetVersionString failed: " + e.Message); }

            info.networkVersion = unchecked((int)global::Version.c_networkVersion);

            try { info.unityVersion = UnityEngine.Application.unityVersion; } catch { }

            string root = null;
            try { root = BepInEx.Paths.GameRootPath; } catch { }

            if (!string.IsNullOrEmpty(root))
            {
                string asm = Path.Combine(Path.Combine(Path.Combine(root, "valheim_Data"), "Managed"),
                                          "assembly_valheim.dll");
                if (File.Exists(asm))
                {
                    info.assemblyValheimSha256 = DumpWriter.Sha256File(asm);
                    info.assemblyValheimBytes = new FileInfo(asm).Length;
                }

                string player = Path.Combine(root, "UnityPlayer.dll");
                if (File.Exists(player))
                {
                    info.unityPlayerSha256 = DumpWriter.Sha256File(player);
                    info.unityPlayerBytes = new FileInfo(player).Length;
                }

                // steam_appid.txt holds the app id, not the build id; the build id lives in Steam's own
                // appmanifest outside the game folder. Recorded as null rather than guessed - the two
                // SHA-256s are the authority.
                info.steamBuild = null;
            }

            _cached = info;
            return info;
        }

        /// <summary>
        /// The DATA-STAMP line, in exactly the shape <c>check-game-version.ps1</c> prints, so the dump
        /// and the knowledge base can be compared without tooling. Repeated as the first property of
        /// every file in the dump: a file lifted out of the folder is still self-identifying.
        /// </summary>
        public static string Stamp(string mode)
        {
            GameInfoDef g = Read();
            return string.Format(CultureInfo.InvariantCulture,
                "DATA-STAMP game-version={0} network={1} unity={2} assembly_valheim-sha256={3} " +
                "unityplayer-sha256={4} dumped={5} dumper={6} mode={7} schema={8}",
                g.version ?? "unknown",
                g.networkVersion,
                g.unityVersion ?? "unknown",
                g.assemblyValheimSha256 ?? "unknown",
                g.unityPlayerSha256 ?? "unknown",
                DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Plugin.VERSION,
                mode,
                DumpFormat.Schema);
        }

        public static VersionConstantsFile VersionConstants(string stamp)
        {
            return new VersionConstantsFile
            {
                stamp = stamp,
                schema = DumpFormat.Schema,
                gameVersion = Read().version,
                networkVersion = unchecked((int)global::Version.c_networkVersion),
                worldVersion = (int)global::Version.c_WorldVersion,
                worldGenVersion = global::Version.c_WorldGenVersion,
                cachedMinimapVersion = (int)global::Version.c_CachedMinimapVersion,
                playerVersion = (int)global::Version.c_PlayerVersion,

                // Minimap.SaveMapTextureDataToDisk emits BitConverter.GetBytes(1) - a hard-coded literal -
                // while TryLoadMinimapTextureData compares the field against Version.CachedMinimap.Original.
                // They agree today. A tool that derived the written value from the enum would diverge
                // silently if Iron Gate bumped the enum and forgot the literal, so both are recorded.
                cachedMinimapWrittenLiteral = 1,

                biomeGridTextureSize = AltBiomeWorldData.c_textureSize,
                biomeGridPixelSize = AltBiomeWorldData.c_pixelSize,
                biomeGridHalfWidth = AltBiomeWorldData.c_halfWidth,
                biomeGridHalfPixel = AltBiomeWorldData.c_halfPixel,
            };
        }
    }
}
