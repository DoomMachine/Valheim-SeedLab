using System;
using System.IO;
using System.Threading;

namespace SeedLab.Saves
{
    /// <summary>
    /// Marker interface for the readers in this assembly.
    /// <para>
    /// <b>SeedLab.Saves is read-only by construction. There is no writing API anywhere in this
    /// assembly, and there never must be.</b> The user's world and character saves are synced by
    /// Steam Cloud and are irreplaceable, and the game actively deletes files it does not expect:
    /// <c>SaveCollection.KeepOnlyNewest</c> (decompiled, assembly_valheim) removes every
    /// <c>_main.*</c> file in a world folder that is not part of the chosen complete save group -
    /// including higher-numbered incomplete groups. A tool that wrote so much as a scratch file into
    /// a world folder could therefore cause the game to delete a real save.
    /// </para>
    /// <para>
    /// Every file this assembly opens goes through <see cref="SaveFileAccess"/>, which only ever
    /// asks for <see cref="FileAccess.Read"/>. 05-validation.md sections 3.3 / T9(b) and
    /// 08-architecture.md R10.
    /// </para>
    /// </summary>
    public interface IValheimReader
    {
    }

    /// <summary>
    /// The one door onto the disk. Opens files read-only and shared, because the game may hold the
    /// file open while playing and Steam Cloud may replace it mid-read.
    /// <para>
    /// 05-validation.md section 5.0: "Open with
    /// <c>new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)</c>
    /// - the game may hold the file open, and Steam Cloud may replace it mid-read; treat an
    /// <c>IOException</c> as 'retry once, then report', never as corruption."
    /// </para>
    /// </summary>
    public static class SaveFileAccess
    {
        /// <summary>Sharing mode: the game writes these files while running, so never take a lock.</summary>
        public const FileShare Share = FileShare.ReadWrite | FileShare.Delete;

        /// <summary>Milliseconds to wait before the single permitted retry.</summary>
        public const int RetryDelayMs = 75;

        /// <summary>Opens a save file for reading. The returned stream is read-only.</summary>
        public static FileStream OpenRead(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            return new FileStream(path, FileMode.Open, FileAccess.Read, Share, 1 << 16, FileOptions.SequentialScan);
        }

        /// <summary>
        /// Reads a whole save file. Retries exactly once on <see cref="IOException"/>, per section 5.0:
        /// a transient sharing failure while the game or Steam Cloud writes is not corruption.
        /// </summary>
        public static byte[] ReadAllBytes(string path)
        {
            try
            {
                return ReadOnce(path);
            }
            catch (IOException)
            {
                Thread.Sleep(RetryDelayMs);
                return ReadOnce(path);
            }
        }

        private static byte[] ReadOnce(string path)
        {
            using (FileStream fs = OpenRead(path))
            {
                long length = fs.Length;
                if (length > int.MaxValue)
                    throw new InvalidDataException("Save file is larger than 2 GiB: " + path);

                byte[] buffer = new byte[(int)length];
                int read = 0;
                while (read < buffer.Length)
                {
                    int n = fs.Read(buffer, read, buffer.Length - read);
                    if (n <= 0) break;
                    read += n;
                }

                if (read != buffer.Length)
                {
                    // The file shrank under us (Steam Cloud replacement). Return what is really there.
                    byte[] shorter = new byte[read];
                    Buffer.BlockCopy(buffer, 0, shorter, 0, read);
                    return shorter;
                }

                return buffer;
            }
        }
    }
}
