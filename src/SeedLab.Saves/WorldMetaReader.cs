using System;
using System.Collections.Generic;
using System.IO;

namespace SeedLab.Saves
{
    /// <summary>
    /// Reads <c>_main.&lt;N&gt;.fwl2</c>. <b>Read-only</b>; see <see cref="IValheimReader"/>.
    /// <para>
    /// Layout, from <c>World.SaveWorldFWLData</c> (lines 218-247) and <c>World.LoadWorld</c>
    /// (lines 249-360), decompiled:
    /// <code>
    /// int32  byteLength
    /// byte[byteLength]  ZPackage:
    ///     int32  version            // Version.World; 41 = DeepNorth
    ///     string m_name
    ///     string m_seedName
    ///     int32  m_seed
    ///     int64  m_uid
    ///     int32  m_worldGenVersion  // version &gt;= 26 (WorldGenVersion)
    ///     bool   m_needsDB          // version &gt;= 30 (NeedsDB)
    ///     int32  nKeys ; string x nKeys      // version &gt;= 32 (GlobalKeys)
    ///     int32  nUsers ; {string x 4} x n   // version &gt;= 41 (DeepNorth)
    /// </code>
    /// </para>
    /// <para>
    /// Note <c>LoadWorld</c> writes the needsDB gate as
    /// <c>m_needsDB = world &gt;= Version.World.NeedsDB &amp;&amp; zPackage.ReadBool();</c> - C# short-circuits,
    /// so below version 30 <b>no byte is consumed</b>. Reproduced literally below; getting that wrong
    /// shifts everything after it.
    /// </para>
    /// <para>
    /// Verified byte-exact on both ground-truth worlds: <c>asdasdasd/_main.3.fwl2</c> is 224 bytes on
    /// disk with a 220-byte inner package and <b>0 bytes left over</b>;
    /// <c>testworldclaude/_main.1.fwl2</c> is 125/121/0.
    /// </para>
    /// </summary>
    public sealed class WorldMetaReader : IValheimReader
    {
        /// <summary><c>Version.World.OldestForwardCompatible</c> = 9.</summary>
        public const int OldestSupportedVersion = 9;

        /// <summary><c>Version.World.DeepNorth</c> = 41, the current world version on 1.0.15.</summary>
        public const int CurrentVersion = 41;

        // Version gates, from Version.World (decompiled).
        private const int VersionWorldGenVersion = 26;
        private const int VersionNeedsDb = 30;
        private const int VersionGlobalKeys = 32;
        private const int VersionDeepNorth = 41;

        public static WorldMeta Read(string path)
        {
            byte[] bytes = SaveFileAccess.ReadAllBytes(path);
            return Parse(bytes, path);
        }

        public static WorldMeta Parse(byte[] fileBytes, string? path = null)
        {
            PackageReader file = new PackageReader(fileBytes);

            // World.SaveWorldFWLData: fileWriter.m_binary.Write(array.Length); Write(array);
            PackageReader p = file.ReadPackage();
            if (file.Remaining != 0)
                throw new InvalidDataException(
                    (path ?? ".fwl2") + ": " + file.Remaining + " byte(s) after the package. Expected none.");

            int version = p.ReadInt();

            // Version.IsWorldVersionCompatible: version <= DeepNorth && version >= OldestForwardCompatible.
            // Reject a newer file loudly instead of guessing at a layout we have not seen.
            if (version > CurrentVersion)
                throw new InvalidDataException(
                    (path ?? ".fwl2") + ": world version " + version + " is newer than " + CurrentVersion +
                    " (Version.World.DeepNorth, the version this build of SeedLab was written against). " +
                    "Refusing to guess at the layout.");
            if (version < OldestSupportedVersion)
                throw new InvalidDataException(
                    (path ?? ".fwl2") + ": world version " + version + " is below " + OldestSupportedVersion +
                    " (Version.World.OldestForwardCompatible); the game itself would refuse it.");

            string name = p.ReadString();
            string seedName = p.ReadString();
            int seed = p.ReadInt();
            long uid = p.ReadLong();

            int worldGenVersion = 0;
            if (version >= VersionWorldGenVersion) worldGenVersion = p.ReadInt();

            // Literal port of `world >= NeedsDB && ReadBool()`: the read is short-circuited away below 30.
            bool needsDb = version >= VersionNeedsDb && p.ReadBool();

            List<string> keys = new List<string>();
            if (version >= VersionGlobalKeys)
            {
                int n = ReadCount(p, "starting global keys", path);
                for (int i = 0; i < n; i++) keys.Add(p.ReadString());
            }

            List<CrossNetworkUser> history = new List<CrossNetworkUser>();
            if (version >= VersionDeepNorth)
            {
                int n = ReadCount(p, "player history", path);
                for (int i = 0; i < n; i++)
                {
                    // ZNet.CrossNetworkUserInfo.Read: four strings in this order.
                    string platformUserId = p.ReadString();
                    string displayName = p.ReadString();
                    string serverAssignedName = p.ReadString();
                    string playFabId = p.ReadString();
                    history.Add(new CrossNetworkUser(platformUserId, displayName, serverAssignedName, playFabId));
                }
            }

            // T9(a): a correct walk consumes the package exactly. Both ground-truth files do.
            p.ExpectEnd(path ?? ".fwl2");

            return new WorldMeta(version, name, seedName, seed, uid, worldGenVersion, needsDb,
                                 keys, history, path);
        }

        private static int ReadCount(PackageReader p, string what, string? path)
        {
            int n = p.ReadInt();
            if (n < 0 || n > p.Remaining)
                throw new InvalidDataException(
                    (path ?? ".fwl2") + ": implausible " + what + " count " + n +
                    " with only " + p.Remaining + " bytes left.");
            return n;
        }
    }
}
