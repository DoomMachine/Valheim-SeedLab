using System;
using System.IO;
using System.Text;

namespace SeedLab.Runtime.Storage
{
    /// <summary>
    /// Temp-and-rename for every durable write in SeedLab: checkpoints, manifests, the self-test stamp,
    /// result files, rendered maps.
    ///
    /// <para>The audit found a hard kill leaving a torn file and an orphaned <c>.tmp</c>. The rule here
    /// is: write the whole thing to a sibling temp file in the SAME directory (so the rename cannot
    /// cross a volume and stop being atomic), flush it to the device with <c>Flush(true)</c>, then
    /// <see cref="File.Move(string,string,bool)"/> over the target. A reader then sees either the old
    /// file or the new one, never half of either.</para>
    ///
    /// <para>The temp name carries our pid so a crash leaves something the reaper can recognise as ours
    /// (<see cref="CleanOrphans"/>). Directory entries themselves are not fsynced - the BCL exposes no
    /// way to - so on a power cut the rename may be lost; the file content never is.</para>
    /// </summary>
    public static class DurableWrite
    {
        public const string TempPrefix = ".seedlab-tmp-";

        /// <summary>UTF-8 without a BOM, the project's file convention.</summary>
        public static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public static void Text(string path, string text) =>
            Stream(path, s =>
            {
                byte[] bytes = Utf8NoBom.GetBytes(text ?? "");
                s.Write(bytes, 0, bytes.Length);
            });

        public static void Bytes(string path, byte[] data) =>
            Stream(path, s => s.Write(data ?? Array.Empty<byte>(), 0, data?.Length ?? 0));

        /// <summary>
        /// Runs <paramref name="write"/> against a temp file and renames it over <paramref name="path"/>.
        /// If <paramref name="write"/> throws, the temp file is removed and the original is untouched.
        /// </summary>
        public static void Stream(string path, Action<Stream> write)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A path is required.", nameof(path));
            if (write == null) throw new ArgumentNullException(nameof(write));

            string full = Path.GetFullPath(path);
            string dir = Path.GetDirectoryName(full) ?? ".";
            Directory.CreateDirectory(dir);

            string temp = Path.Combine(dir,
                TempPrefix + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "-" + Path.GetFileName(full));

            try
            {
                using (FileStream fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                                      bufferSize: 1 << 16, FileOptions.SequentialScan))
                {
                    write(fs);
                    fs.Flush(flushToDisk: true);
                }
                File.Move(temp, full, overwrite: true);
            }
            catch (Exception)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
                throw;
            }
        }

        /// <summary>
        /// Deletes SeedLab temp files left in a directory by a process that is no longer alive.
        /// Called on launch, because "clean up on exit" is exactly what a killed process cannot do.
        /// Returns how many were removed.
        /// </summary>
        public static int CleanOrphans(string directory, Func<int, bool>? isAlive = null)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return 0;
            isAlive ??= ProcessLiveness.IsAlive;

            int removed = 0;
            string[] files;
            try { files = Directory.GetFiles(directory, TempPrefix + "*"); }
            catch (Exception) { return 0; }

            foreach (string f in files)
            {
                int pid = PidFromTempName(Path.GetFileName(f));
                if (pid > 0 && pid != Environment.ProcessId && isAlive(pid)) continue;
                if (pid == Environment.ProcessId) continue;
                try { File.Delete(f); removed++; }
                catch (Exception) { /* in use by someone else; leave it */ }
            }
            return removed;
        }

        internal static int PidFromTempName(string name)
        {
            if (!name.StartsWith(TempPrefix, StringComparison.Ordinal)) return -1;
            int i = TempPrefix.Length;
            int j = i;
            while (j < name.Length && name[j] >= '0' && name[j] <= '9') j++;
            if (j == i) return -1;
            return int.TryParse(name.Substring(i, j - i), out int pid) ? pid : -1;
        }
    }
}
