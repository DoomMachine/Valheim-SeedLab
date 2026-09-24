using System;
using System.Collections.Generic;
using System.Globalization;

namespace SeedLab.Data
{
    /// <summary>
    /// The DATA-STAMP: which Valheim build the shipped data was read out of.
    ///
    /// <para>It is the first property of every file in the dump, so a file lifted out of the folder is
    /// still self-identifying, and it is compared against the installed game before any
    /// location-dependent answer is given. Its authority is
    /// <see cref="AssemblyValheimSha256"/> - the same artifact and the same hash that
    /// <c>tools\check-game-version.ps1</c> reports, so the two stamps can be compared by eye.</para>
    ///
    /// <para>Format (one line, space separated <c>key=value</c>):
    /// <code>
    /// DATA-STAMP game-version=1.0.15 network=40 unity=6000.0.75f1
    ///            assembly_valheim-sha256=... unityplayer-sha256=...
    ///            dumped=2026-09-22 dumper=1.0.0 mode=assets schema=1
    /// </code></para>
    /// </summary>
    public sealed class DataStamp : IEquatable<DataStamp>
    {
        private DataStamp(string raw, IReadOnlyDictionary<string, string> parts)
        {
            Raw = raw;
            Parts = parts;
        }

        public const string Prefix = "DATA-STAMP";

        /// <summary>The stamp exactly as the dumper wrote it.</summary>
        public string Raw { get; }

        /// <summary>Every <c>key=value</c> in the stamp, so a key added later is still visible.</summary>
        public IReadOnlyDictionary<string, string> Parts { get; }

        public string GameVersion => Required("game-version");
        public int NetworkVersion => int.Parse(Required("network"), CultureInfo.InvariantCulture);
        public string UnityVersion => Required("unity");

        /// <summary>SHA-256 of <c>valheim_Data\Managed\assembly_valheim.dll</c>, lowercase hex.</summary>
        public string AssemblyValheimSha256 => Required("assembly_valheim-sha256");

        /// <summary>SHA-256 of <c>UnityPlayer.dll</c> - where PerlinNoise, Random and FloatToHalf live.</summary>
        public string UnityPlayerSha256 => Required("unityplayer-sha256");

        /// <summary>The date the dump was taken, as the dumper wrote it (UTC date, yyyy-MM-dd).</summary>
        public string Dumped => Required("dumped");

        public string DumperVersion => Required("dumper");

        /// <summary>"assets", "natives" or "worldgen" - the run that wrote the file this stamp came from.</summary>
        public string Mode => Required("mode");

        public int Schema => int.Parse(Required("schema"), CultureInfo.InvariantCulture);

        /// <summary>The folder name a dump of this build gets: <c>1.0.15-59f53fb5</c>.</summary>
        public string FolderName => GameVersion + "-" + AssemblyValheimSha256.Substring(0, 8);

        public static DataStamp Parse(string raw, string originFile)
        {
            if (raw == null) throw new GameDataException(originFile + ": the DATA-STAMP is missing.") { File = originFile };

            string trimmed = raw.Trim();
            if (!trimmed.StartsWith(Prefix, StringComparison.Ordinal))
            {
                throw new GameDataException(
                    originFile + ": the stamp does not start with " + Prefix + "; it reads '" + trimmed + "'.")
                { File = originFile };
            }

            Dictionary<string, string> parts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string token in trimmed.Substring(Prefix.Length)
                         .Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = token.IndexOf('=');
                if (eq <= 0)
                {
                    throw new GameDataException(
                        originFile + ": the stamp has a token without a value: '" + token + "'.")
                    { File = originFile };
                }

                parts[token.Substring(0, eq)] = token.Substring(eq + 1);
            }

            DataStamp stamp = new DataStamp(trimmed, parts);

            // Touch every member the tool relies on, so a stamp that is missing one fails here - at
            // load - rather than much later inside whichever answer happened to ask for it first.
            _ = stamp.GameVersion;
            _ = stamp.NetworkVersion;
            _ = stamp.UnityVersion;
            _ = stamp.AssemblyValheimSha256;
            _ = stamp.UnityPlayerSha256;
            _ = stamp.Dumped;
            _ = stamp.DumperVersion;
            _ = stamp.Mode;
            _ = stamp.Schema;

            if (stamp.AssemblyValheimSha256.Length != 64)
            {
                throw new GameDataException(
                    originFile + ": assembly_valheim-sha256 is '" + stamp.AssemblyValheimSha256
                    + "', which is not a 64-character SHA-256.")
                { File = originFile };
            }

            return stamp;
        }

        /// <summary>
        /// Two stamps identify the same game build when the two SHA-256s agree. The mode, the dump
        /// date and the dumper version differ between files of one dump and are not part of identity.
        /// </summary>
        public bool SameBuild(DataStamp other)
        {
            return other != null
                   && string.Equals(AssemblyValheimSha256, other.AssemblyValheimSha256, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(UnityPlayerSha256, other.UnityPlayerSha256, StringComparison.OrdinalIgnoreCase);
        }

        public bool Equals(DataStamp? other) => other != null && string.Equals(Raw, other.Raw, StringComparison.Ordinal);
        public override bool Equals(object? obj) => Equals(obj as DataStamp);
        public override int GetHashCode() => Raw.GetHashCode(StringComparison.Ordinal);
        public override string ToString() => Raw;

        private string Required(string key)
        {
            if (Parts.TryGetValue(key, out string? v) && v.Length != 0) return v;
            throw new GameDataException("the DATA-STAMP has no '" + key + "': " + Raw);
        }
    }
}
