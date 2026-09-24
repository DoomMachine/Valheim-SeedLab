using System;
using System.Collections.Generic;
using System.IO;

namespace SeedLab.Saves
{
    /// <summary>
    /// Reads <c>_main.&lt;N&gt;.db2</c>. <b>Read-only</b>; see <see cref="IValheimReader"/>.
    /// <para>
    /// Layout (05-validation.md section 5.2), from <c>ZNet.SaveWorldThread</c>,
    /// <c>ZoneSystem.Save</c> (lines 987-1025), <c>RandEventSystem.Save</c> and
    /// <c>PersistentEventSystem.Save</c>, decompiled:
    /// <code>
    /// int32   version        // 41
    /// double  m_netTime
    /// --- ZoneSystem block ---
    /// int32   gzipLength
    /// byte[gzipLength]  gzip -&gt; ZPackage:
    ///     int32 nZones ; { int16 x; int16 y } x nZones
    ///     int32 locationVersion
    ///     int32 nKeys  ; string x nKeys
    ///     bool  locationsGenerated
    ///     int32 nLoc   ; { int32 prefabHash; float x; float y; float z; bool placed } x nLoc
    /// --- RandEventSystem block ---
    /// float  eventTimer ; string eventName ; float eventTime ; float posX, posY, posZ
    /// --- PersistentEventSystem block ---
    /// int32  compressedLength ; byte[compressedLength]
    /// </code>
    /// The top level is a bare <c>BinaryWriter</c>, not a ZPackage - the primitive encodings are
    /// identical, so <see cref="PackageReader"/> applies unchanged.
    /// </para>
    /// <para>
    /// The two trailing blocks are parsed <b>best effort</b>: nothing in world generation needs them,
    /// their inner 15-byte payload has an unverified framing, and a future game version could change
    /// them. If they do not parse, the bytes are kept verbatim in
    /// <see cref="WorldDb.UndecodedTail"/> and the ZoneSystem data - which is what this tool is
    /// actually after - is still returned. The ZoneSystem block itself is parsed strictly and must
    /// consume its blob exactly.
    /// </para>
    /// </summary>
    public sealed class WorldDbReader : IValheimReader
    {
        /// <summary><c>Version.World.DeepNorth</c> = 41.</summary>
        public const int CurrentVersion = 41;

        /// <summary><c>Version.World.OldestForwardCompatible</c> = 9.</summary>
        public const int OldestSupportedVersion = 9;

        public static WorldDb Read(string path)
        {
            byte[] bytes = SaveFileAccess.ReadAllBytes(path);
            return Parse(bytes, path);
        }

        public static WorldDb Parse(byte[] fileBytes, string? path = null)
        {
            string what = path ?? ".db2";
            PackageReader file = new PackageReader(fileBytes);

            int version = file.ReadInt();
            if (version > CurrentVersion)
                throw new InvalidDataException(
                    what + ": world version " + version + " is newer than " + CurrentVersion +
                    ". Refusing to guess at the layout.");
            if (version < OldestSupportedVersion)
                throw new InvalidDataException(
                    what + ": world version " + version + " is below " + OldestSupportedVersion + ".");

            double netTime = file.ReadDouble();

            // ZoneSystem.Save line 1016: byte[] c = zPackage.GetCompressed(); w.Write(c.Length); w.Write(c);
            PackageReader zone = file.ReadCompressedPackage();
            ZoneSystemData zoneData = ParseZoneSystem(zone, what);

            RandomEventData? randomEvent = null;
            byte[]? persistent = null;
            byte[] tail = Array.Empty<byte>();

            int tailStart = file.Position;
            try
            {
                float eventTimer = file.ReadSingle();
                string eventName = file.ReadString();
                float eventTime = file.ReadSingle();
                // Three separate float writes, not a Vector3 helper (RandEventSystem.Save).
                float px = file.ReadSingle();
                float py = file.ReadSingle();
                float pz = file.ReadSingle();
                randomEvent = new RandomEventData(eventTimer, eventName, eventTime, new Vec3f(px, py, pz));

                // PersistentEventSystem.Save: w.Write(compressedData.Length); w.Write(compressedData);
                int persistLength = file.ReadInt();
                if (persistLength < 0 || persistLength > file.Remaining)
                    throw new InvalidDataException("implausible persistent-event length " + persistLength);
                persistent = file.ReadByteArray(persistLength);

                if (file.Remaining != 0) tail = file.ReadRemaining();
            }
            catch (Exception ex) when (ex is EndOfStreamException || ex is InvalidDataException)
            {
                // The ZoneSystem data is still good; record the rest verbatim rather than inventing it.
                randomEvent = null;
                persistent = null;
                tail = new PackageReader(fileBytes, tailStart, fileBytes.Length - tailStart).ReadRemaining();
            }

            return new WorldDb(version, netTime, zoneData, randomEvent, persistent, tail, path);
        }

        private static ZoneSystemData ParseZoneSystem(PackageReader z, string what)
        {
            int zoneCount = ReadCount(z, "generated zone", what, 4);
            List<(short X, short Y)> zones = new List<(short, short)>(zoneCount);
            for (int i = 0; i < zoneCount; i++) zones.Add(z.ReadVector2s());

            int locationVersion = z.ReadInt();

            int keyCount = ReadCount(z, "global key", what, 1);
            List<string> keys = new List<string>(keyCount);
            for (int i = 0; i < keyCount; i++) keys.Add(z.ReadString());

            bool locationsGenerated = z.ReadBool();

            // 17 bytes per instance: int32 hash + 3 floats + bool.
            int locCount = ReadCount(z, "location instance", what, 17);
            List<LocationInstance> locations = new List<LocationInstance>(locCount);
            for (int i = 0; i < locCount; i++)
            {
                int hash = z.ReadInt();
                float x = z.ReadSingle();
                float y = z.ReadSingle();
                float zz = z.ReadSingle();
                bool placed = z.ReadBool();
                locations.Add(new LocationInstance(hash, x, y, zz, placed));
            }

            // T9(a): the ZoneSystem blob must be consumed exactly. Both ground-truth worlds do.
            z.ExpectEnd(what + " ZoneSystem block");

            return new ZoneSystemData(zones, locationVersion, keys, locationsGenerated, locations);
        }

        private static int ReadCount(PackageReader p, string what, string file, int bytesPerItem)
        {
            int n = p.ReadInt();
            if (n < 0 || (long)n * bytesPerItem > p.Remaining)
                throw new InvalidDataException(
                    file + ": implausible " + what + " count " + n + " with only " + p.Remaining +
                    " bytes left in the block.");
            return n;
        }
    }
}
