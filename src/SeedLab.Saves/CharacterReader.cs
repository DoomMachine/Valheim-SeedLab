using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace SeedLab.Saves
{
    /// <summary>Knobs for <see cref="CharacterReader"/>.</summary>
    public sealed class CharacterReaderOptions
    {
        /// <summary>
        /// Decode each world's <c>mapData</c> blob (two N*N byte bitmaps plus the pins). Costs about
        /// 8 MiB per world entry. Turn it off to list a character's worlds cheaply; the raw bytes are
        /// kept either way in <see cref="WorldPlayerData.MapDataBlob"/>.
        /// </summary>
        public bool DecodeMapData { get; set; } = true;

        /// <summary>Verify the trailing SHA-512 over the data package. Cheap; default on.</summary>
        public bool VerifyHash { get; set; } = true;
    }

    /// <summary>
    /// Reads <c>&lt;name&gt;.fch</c>. <b>Read-only</b>; see <see cref="IValheimReader"/>.
    /// <para>
    /// Layout, from <c>PlayerProfile.SavePlayerToDisk</c> / <c>LoadPlayerFromDisk</c> (decompiled)
    /// and 05-validation.md section 5.4:
    /// <code>
    /// int32  dataLength
    /// byte[dataLength]  ZPackage:
    ///     int32 46          // Version.Player.DeepNorth
    ///     int32 205         // stat count
    ///     int32 10          // stat categories
    ///     repeat 10: float x 205, then knownWorlds / knownWorldKeys / knownCommands,
    ///                int32 5 + that many enemy-stat tables,
    ///                itemPickup / itemCraft / pickable / foodEaten / piecesPlaced
    ///     bool  m_firstSpawn
    ///     int32 worldCount
    ///     repeat: int64 uid; bool+Vector3 spawn; bool+Vector3 logout; bool+Vector3 death;
    ///             Vector3 home; bool hasMap; if (hasMap) { int32 len; byte[len] mapData }
    ///     string m_playerName ; int64 m_playerID ; string m_startSeed
    ///     bool usedCheats ; int64 dateCreated ; bool hasPlayerData ; if so { int32 len; byte[len] }
    /// int32  hashLength   // 64
    /// byte[64]  SHA-512 of the data package   // read and DISCARDED by the game
    /// </code>
    /// Verified on <c>asda.fch</c>: 27 092 bytes, a 27 020-byte package, profile v46, 205 stats, 10
    /// categories, 2 world entries, <b>0 bytes left over</b>, and the stored SHA-512 reproduces.
    /// </para>
    /// <para>
    /// <b>What this reader is for.</b> Per world UID it yields the exploration bitmaps, the saved
    /// pins and the logout / home / death points - everything an "overlay my own map" feature needs
    /// and everything that exists for it. The <c>Player.Save</c> blob at the end is exposed as raw
    /// bytes because its layout was not decompiled: better an opaque array than a guess.
    /// </para>
    /// </summary>
    public sealed class CharacterReader : IValheimReader
    {
        // Version.Player (decompiled).
        private const int VersionOldestForwardCompatible = 27;
        private const int VersionStats = 28;
        private const int VersionMapData = 29;
        private const int VersionDeathPoint = 30;
        private const int VersionStats2 = 38;
        private const int VersionFirstSpawn = 40;
        private const int VersionCallToArms = 42;
        private const int VersionAbandonedDn = 44;
        private const int VersionDeepNorth = 46;

        // Version.Map (decompiled).
        private const int MapVersionPins = 2;
        private const int MapVersionPinsChecked = 3;
        private const int MapVersionVisibleOnMap = 4;
        private const int MapVersionNewExplore = 5;
        private const int MapVersionPinsOwnerId = 6;
        private const int MapVersionCompressed = 7;
        private const int MapVersionPinsAuthor = 8;

        public static CharacterProfile Read(string path, CharacterReaderOptions? options = null)
        {
            byte[] bytes = SaveFileAccess.ReadAllBytes(path);
            return Parse(bytes, options, path);
        }

        public static CharacterProfile Parse(byte[] fileBytes, CharacterReaderOptions? options = null, string? path = null)
        {
            options ??= new CharacterReaderOptions();
            string what = path ?? ".fch";

            PackageReader file = new PackageReader(fileBytes);
            int dataLength = file.ReadInt();
            if (dataLength < 0 || dataLength > file.Remaining)
                throw new InvalidDataException(what + ": implausible data length " + dataLength + ".");
            byte[] dataBytes = file.ReadByteArray(dataLength);

            bool hashVerified = false;
            if (file.Remaining > 0)
            {
                int hashLength = file.ReadInt();
                if (hashLength >= 0 && hashLength <= file.Remaining)
                {
                    byte[] storedHash = file.ReadByteArray(hashLength);
                    if (options.VerifyHash && hashLength == 64)
                    {
                        // ZPackage.GenerateHash() == SHA512.Create().ComputeHash(GetArray()).
                        byte[] actual = SHA512.HashData(dataBytes);
                        hashVerified = CryptographicOperations.FixedTimeEquals(actual, storedHash);
                    }
                }
            }

            PackageReader p = new PackageReader(dataBytes);
            int version = p.ReadInt();
            if (version > VersionDeepNorth)
                throw new InvalidDataException(
                    what + ": player profile version " + version + " is newer than " + VersionDeepNorth +
                    " (Version.Player.DeepNorth). Refusing to guess at the layout.");
            if (version < VersionOldestForwardCompatible)
                throw new InvalidDataException(
                    what + ": player profile version " + version + " is below " +
                    VersionOldestForwardCompatible + "; the game itself would refuse it.");

            List<PlayerStatBlock> stats = ReadStats(p, version, what);

            bool firstSpawn = false;
            if (version >= VersionFirstSpawn) firstSpawn = p.ReadBool();

            int worldCount = p.ReadInt();
            if (worldCount < 0 || worldCount > p.Remaining)
                throw new InvalidDataException(what + ": implausible world count " + worldCount + ".");

            List<WorldPlayerData> worlds = new List<WorldPlayerData>(worldCount);
            for (int i = 0; i < worldCount; i++)
            {
                long uid = p.ReadLong();
                bool haveSpawn = p.ReadBool();
                Vec3f spawn = p.ReadVector3();
                bool haveLogout = p.ReadBool();
                Vec3f logout = p.ReadVector3();

                bool haveDeath = false;
                Vec3f death = default;
                if (version >= VersionDeathPoint)
                {
                    haveDeath = p.ReadBool();
                    death = p.ReadVector3();
                }

                Vec3f home = p.ReadVector3();

                byte[]? mapBlob = null;
                // Literal port of `if (player >= Version.Player.MapData && zPackage.ReadBool())`:
                // below version 29 no byte is consumed at all.
                if (version >= VersionMapData && p.ReadBool()) mapBlob = p.ReadByteArray();

                PlayerMapData? map = null;
                if (mapBlob != null && options.DecodeMapData) map = ParseMapData(mapBlob, what);

                worlds.Add(new WorldPlayerData(uid, haveSpawn, spawn, haveLogout, logout,
                                               haveDeath, death, home, mapBlob, map));
            }

            string playerName = p.ReadString();
            long playerId = p.ReadLong();
            string startSeed = p.ReadString();

            bool usedCheats = false;
            DateTimeOffset dateCreated = new DateTimeOffset(new DateTime(2021, 2, 2, 0, 0, 0, DateTimeKind.Utc));
            if (version >= VersionStats2)
            {
                usedCheats = p.ReadBool();
                dateCreated = DateTimeOffset.FromUnixTimeSeconds(p.ReadLong());

                // Unverified: no pre-46 profile exists on this machine to test this branch against.
                // Ported from LoadPlayerFromDisk so that an older file is at least not mis-parsed silently.
                if (version < VersionDeepNorth && version != VersionAbandonedDn)
                {
                    ReadStringFloatList(p);   // m_knownWorlds
                    ReadStringFloatList(p);   // m_knownWorldKeys
                    ReadStringFloatList(p);   // m_knownCommands
                    if (version >= VersionCallToArms)
                    {
                        ReadStringFloatList(p);   // m_enemyStats[0]
                        ReadStringFloatList(p);   // m_itemPickupStats
                        ReadStringFloatList(p);   // m_itemCraftStats
                    }
                }
            }

            byte[]? playerData = null;
            if (p.ReadBool()) playerData = p.ReadByteArray();

            p.ExpectEnd(what + " player package");

            return new CharacterProfile(version, firstSpawn, stats, worlds, playerName, playerId,
                                        startSeed, usedCheats, dateCreated, playerData, hashVerified, path);
        }

        /// <summary>
        /// The stat section. Three shapes, exactly as <c>LoadPlayerFromDisk</c> branches: the
        /// ten-category form at version 46 (and at 44, <c>AbandonedDN</c>), a single flat float list
        /// from version 38, and four ints from version 28. The counts are read from the file - the
        /// literal <c>5</c> before the enemy tables included - so nothing can be skipped by size.
        /// </summary>
        private static List<PlayerStatBlock> ReadStats(PackageReader p, int version, string what)
        {
            List<PlayerStatBlock> result = new List<PlayerStatBlock>();

            if (version >= VersionDeepNorth || version == VersionAbandonedDn)
            {
                int statCount = p.ReadInt();
                int categoryCount = p.ReadInt();
                if (statCount < 0 || categoryCount < 0 || (long)statCount * 4 * categoryCount > p.Remaining)
                    throw new InvalidDataException(
                        what + ": implausible stat table " + categoryCount + " x " + statCount + ".");

                for (int i = 0; i < categoryCount; i++)
                {
                    float[] values = new float[statCount];
                    for (int j = 0; j < statCount; j++) values[j] = p.ReadSingle();

                    var knownWorlds = ReadStringFloatList(p);
                    var knownWorldKeys = ReadStringFloatList(p);
                    var knownCommands = ReadStringFloatList(p);

                    int enemyTableCount = p.ReadInt();
                    if (enemyTableCount < 0 || enemyTableCount > p.Remaining)
                        throw new InvalidDataException(what + ": implausible enemy-stat table count " + enemyTableCount + ".");
                    var enemy = new List<IReadOnlyList<KeyValuePair<string, float>>>(enemyTableCount);
                    for (int k = 0; k < enemyTableCount; k++) enemy.Add(ReadStringFloatList(p));

                    var itemPickup = ReadStringFloatList(p);
                    var itemCraft = ReadStringFloatList(p);
                    var pickable = ReadStringFloatList(p);
                    var foodEaten = ReadStringFloatList(p);
                    var piecesPlaced = ReadStringFloatList(p);

                    result.Add(new PlayerStatBlock(values, knownWorlds, knownWorldKeys, knownCommands,
                                                   enemy, itemPickup, itemCraft, pickable, foodEaten, piecesPlaced));
                }
            }
            else if (version >= VersionStats2)
            {
                // Unverified: no such profile on this machine.
                int n = p.ReadInt();
                if (n < 0 || (long)n * 4 > p.Remaining)
                    throw new InvalidDataException(what + ": implausible stat count " + n + ".");
                float[] values = new float[n];
                for (int i = 0; i < n; i++) values[i] = p.ReadSingle();
                result.Add(EmptyBlock(values));
            }
            else if (version >= VersionStats)
            {
                // Unverified: four ints, PlayerStatType EnemyKills / Deaths / CraftsOrUpgrades / Builds.
                float[] values = new float[4];
                for (int i = 0; i < 4; i++) values[i] = p.ReadInt();
                result.Add(EmptyBlock(values));
            }

            return result;
        }

        private static PlayerStatBlock EmptyBlock(float[] values)
        {
            var none = new List<KeyValuePair<string, float>>();
            return new PlayerStatBlock(values, none, none, none,
                                       new List<IReadOnlyList<KeyValuePair<string, float>>>(),
                                       none, none, none, none, none);
        }

        private static List<KeyValuePair<string, float>> ReadStringFloatList(PackageReader p)
        {
            int n = p.ReadInt();
            if (n < 0 || (long)n > p.Remaining)
                throw new InvalidDataException("Implausible name/value list count " + n + ".");
            var list = new List<KeyValuePair<string, float>>(n);
            for (int i = 0; i < n; i++)
            {
                string key = p.ReadString();
                float value = p.ReadSingle();
                list.Add(new KeyValuePair<string, float>(key, value));
            }
            return list;
        }

        /// <summary>
        /// Decodes one world entry's <c>mapData</c> blob, mirroring <c>Minimap.SetMapData</c>.
        /// <para>
        /// One deliberate difference from the game: <c>SetMapData</c> sizes the two byte arrays from
        /// its own <c>m_explored.Length</c> and throws "Error: minimap mismatch" if the blob's
        /// <c>textureSize</c> disagrees with <c>m_textureSize</c>. Offline there is no live Minimap,
        /// so the size is taken from the blob - which the game's own check guarantees is the same
        /// number it would have used.
        /// </para>
        /// </summary>
        public static PlayerMapData ParseMapData(byte[] blob, string? what = null)
        {
            string where = (what ?? ".fch") + " mapData";
            PackageReader outer = new PackageReader(blob);
            int mapVersion = outer.ReadInt();

            PackageReader p = mapVersion >= MapVersionCompressed ? outer.ReadCompressedPackage() : outer;

            int textureSize = p.ReadInt();
            if (textureSize <= 0 || (long)textureSize * textureSize > int.MaxValue)
                throw new InvalidDataException(where + ": implausible texture size " + textureSize + ".");
            long pixels = (long)textureSize * textureSize;

            byte[] explored;
            byte[] exploredOthers;
            if (mapVersion >= MapVersionNewExplore)
            {
                if (pixels * 2 > p.Remaining)
                    throw new InvalidDataException(
                        where + ": needs " + (pixels * 2) + " bytes of explore data but only " +
                        p.Remaining + " remain.");
                explored = p.ReadByteArray((int)pixels);
                exploredOthers = p.ReadByteArray((int)pixels);
            }
            else
            {
                // Unverified: no pre-version-5 map blob on this machine. The old format is one bool
                // per pixel for the local layer only, and no "others" layer exists.
                explored = new byte[pixels];
                for (long i = 0; i < pixels; i++) explored[i] = p.ReadBool() ? (byte)1 : (byte)0;
                exploredOthers = new byte[pixels];
            }

            List<MapPin> pins = new List<MapPin>();
            if (mapVersion >= MapVersionPins)
            {
                int pinCount = p.ReadInt();
                if (pinCount < 0 || pinCount > p.Remaining)
                    throw new InvalidDataException(where + ": implausible pin count " + pinCount + ".");
                for (int i = 0; i < pinCount; i++)
                {
                    string name = p.ReadString();
                    Vec3f pos = p.ReadVector3();
                    PinType type = (PinType)p.ReadInt();
                    bool isChecked = mapVersion >= MapVersionPinsChecked && p.ReadBool();
                    long ownerId = mapVersion >= MapVersionPinsOwnerId ? p.ReadLong() : 0L;
                    string author = mapVersion >= MapVersionPinsAuthor ? p.ReadString() : "";
                    pins.Add(new MapPin(name, pos, type, isChecked, ownerId, author));
                }
            }

            bool publicReference = false;
            if (mapVersion >= MapVersionVisibleOnMap) publicReference = p.ReadBool();

            p.ExpectEnd(where);
            if (!ReferenceEquals(p, outer)) outer.ExpectEnd(where + " envelope");

            return new PlayerMapData(mapVersion, textureSize, explored, exploredOthers, pins, publicReference);
        }
    }
}
