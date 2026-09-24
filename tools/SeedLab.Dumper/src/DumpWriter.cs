using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SeedLab.Contracts.Dump;

namespace SeedLab.Dumper
{
    /// <summary>
    /// The one place this plugin writes a file. Everything goes through <see cref="Resolve"/>, which
    /// refuses any path the game manages.
    ///
    /// Prohibited by spec 04 section 3.2 and by the project's own rule that the game's save folders and
    /// the Steam Cloud folders are READ-ONLY, always: <c>worlds</c>, <c>worlds_local</c>,
    /// <c>characters</c>, the Steam Cloud <c>remote</c> directory, the biome-data <c>cache</c> folder,
    /// anything under <c>&lt;LocalLow&gt;\IronGate</c>, and the game install itself. The check is on
    /// whole path segments, not substrings, so a user folder called "myworlds" is not caught by
    /// accident while <c>...\worlds\...</c> is.
    /// </summary>
    internal sealed class DumpWriter
    {
        private readonly string _root;
        private readonly List<FileEntryDef> _files = new List<FileEntryDef>();

        public string Root { get { return _root; } }
        public IList<FileEntryDef> Files { get { return _files; } }

        public DumpWriter(string root)
        {
            _root = root;
            Directory.CreateDirectory(_root);
        }

        /// <summary>
        /// Where dumps go: <c>%USERPROFILE%\AppData\valheim-dumper</c> - deliberately outside every path
        /// the game manages (spec 04 section 3.2 writes it as
        /// <c>&lt;LocalLow&gt;\IronGate\Valheim\..\..\..\valheim-dumper</c>, which resolves to the same
        /// folder). Overridable from the plugin config; the override is checked by the same guard.
        /// </summary>
        public static string DefaultRoot()
        {
            string profile = Environment.GetEnvironmentVariable("USERPROFILE");
            if (string.IsNullOrEmpty(profile))
            {
                profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            if (string.IsNullOrEmpty(profile))
            {
                profile = Path.GetTempPath();
            }
            return Path.Combine(Path.Combine(profile, "AppData"), "valheim-dumper");
        }

        /// <summary>The per-build subfolder: <c>&lt;version&gt;-&lt;first 8 hex of assembly_valheim sha256&gt;</c>.
        /// A game update therefore lands in a new folder and can never overwrite older evidence.</summary>
        public static string BuildFolderName(string gameVersion, string assemblySha256)
        {
            string shortSha = string.IsNullOrEmpty(assemblySha256)
                ? "unknown"
                : assemblySha256.Substring(0, Math.Min(8, assemblySha256.Length));
            string safeVersion = Sanitize(string.IsNullOrEmpty(gameVersion) ? "unknown" : gameVersion);
            return safeVersion + "-" + shortSha;
        }

        public static string Sanitize(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                sb.Append(char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_' ? c : '_');
            }
            return sb.ToString();
        }

        /// <summary>Throws rather than writing anywhere the game owns. A refusal is a bug in the
        /// caller, not a condition to recover from. The policy itself lives in <see cref="PathPolicy"/>,
        /// which has no game dependencies so that it can be tested.</summary>
        public static void AssertWritable(string fullPath)
        {
            string reason = PathPolicy.Reject(fullPath, GameRoot(), SaveDataRoot());
            if (reason != null)
            {
                throw new InvalidOperationException(
                    "SeedLab.Dumper refuses to write to '" + fullPath + "': " + reason + ".");
            }
        }

        private static string GameRoot()
        {
            try { return BepInEx.Paths.GameRootPath; } catch { return null; }
        }

        /// <summary>
        /// <c>&lt;LocalLow&gt;\IronGate</c> - the parent of <c>Valheim\worlds_local</c>,
        /// <c>Valheim\characters</c> and <c>Valheim\cache</c>. Blocking the whole IronGate tree is
        /// broader than the individual folders on purpose.
        /// </summary>
        private static string SaveDataRoot()
        {
            string profile = Environment.GetEnvironmentVariable("USERPROFILE");
            if (string.IsNullOrEmpty(profile))
            {
                profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            if (string.IsNullOrEmpty(profile)) return null;
            return Path.Combine(Path.Combine(Path.Combine(profile, "AppData"), "LocalLow"), "IronGate");
        }

        private string Resolve(string relativePath)
        {
            string full = Path.GetFullPath(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            AssertWritable(full);
            if (!PathPolicy.IsUnderOrEqual(full, _root))
            {
                throw new InvalidOperationException("Refusing to write outside the dump folder: " + full);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            return full;
        }

        /// <summary>Writes UTF-8 WITHOUT a BOM - the project convention, and what every reader here
        /// expects.</summary>
        public void WriteJson(string relativePath, object dto)
        {
            string full = Resolve(relativePath);
            File.WriteAllText(full, Json.Serialize(dto), new UTF8Encoding(false));
            Record(relativePath, full);
        }

        public void WriteText(string relativePath, string text)
        {
            string full = Resolve(relativePath);
            File.WriteAllText(full, text, new UTF8Encoding(false));
            Record(relativePath, full);
        }

        /// <summary>Streams a binary file. Little-endian by construction: <c>BinaryWriter</c> is
        /// little-endian on every runtime the tool supports, and <see cref="AssertLittleEndian"/>
        /// makes the assumption fail loudly instead of silently producing byte-swapped floats.</summary>
        public void WriteBinary(string relativePath, Action<BinaryWriter> body)
        {
            AssertLittleEndian();
            string full = Resolve(relativePath);
            using (var fs = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            using (var bw = new BinaryWriter(fs, new UTF8Encoding(false)))
            {
                body(bw);
                bw.Flush();
            }
            Record(relativePath, full);
        }

        /// <summary>
        /// Streams a binary file whose producer needs to yield - a 2048x2048 grid is 4.2 million
        /// samples and must not be written inside one frame. The file handle is held across the
        /// yields inside a try/finally, which an iterator may do (only <c>yield return</c> inside a
        /// try that has a CATCH clause is illegal). If <paramref name="body"/> throws, the handle is
        /// still closed and the exception reaches the job driver, which reports a failed dump.
        /// </summary>
        public IEnumerator WriteBinaryStreaming(string relativePath, Func<BinaryWriter, IEnumerator> body)
        {
            AssertLittleEndian();
            string full = Resolve(relativePath);
            var fs = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            var bw = new BinaryWriter(fs, new UTF8Encoding(false));
            try
            {
                IEnumerator inner = body(bw);
                while (inner.MoveNext()) yield return inner.Current;
                bw.Flush();
            }
            finally
            {
                bw.Dispose();
                fs.Dispose();
            }
            Record(relativePath, full);
        }

        public static void AssertLittleEndian()
        {
            if (!BitConverter.IsLittleEndian)
            {
                throw new PlatformNotSupportedException(
                    "The dump format is little-endian; this machine is big-endian.");
            }
        }

        private void Record(string relativePath, string fullPath)
        {
            var info = new FileInfo(fullPath);
            _files.Add(new FileEntryDef
            {
                path = relativePath.Replace(Path.DirectorySeparatorChar, '/'),
                bytes = info.Length,
                sha256 = Sha256File(fullPath),
            });
        }

        /// <summary>
        /// Re-hashes every file currently in the dump folder, whichever run wrote it. Used for the
        /// manifest: the folder, not the run, is the unit a reader validates.
        /// </summary>
        public IEnumerable<FileEntryDef> ScanAllFiles(params string[] excludeRelative)
        {
            var result = new List<FileEntryDef>();
            string rootFull = Path.GetFullPath(_root);
            foreach (string full in Directory.GetFiles(rootFull, "*", SearchOption.AllDirectories))
            {
                string rel = full.Substring(rootFull.Length).TrimStart(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                rel = rel.Replace(Path.DirectorySeparatorChar, '/');

                bool skip = false;
                if (excludeRelative != null)
                {
                    foreach (string ex in excludeRelative)
                    {
                        if (string.Equals(rel, ex, StringComparison.OrdinalIgnoreCase)) { skip = true; break; }
                    }
                }
                if (skip) continue;

                result.Add(new FileEntryDef
                {
                    path = rel,
                    bytes = new FileInfo(full).Length,
                    sha256 = Sha256File(full),
                });
            }
            result.Sort((a, b) => string.CompareOrdinal(a.path, b.path));
            return result;
        }

        public static string Sha256File(string path)
        {
            try
            {
                using (var sha = SHA256.Create())
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20))
                {
                    return ToHex(sha.ComputeHash(fs));
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Could not hash " + path + ": " + e.Message);
                return null;
            }
        }

        public static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }
}
