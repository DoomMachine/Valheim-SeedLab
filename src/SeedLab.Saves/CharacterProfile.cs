using System;
using System.Collections.Generic;

namespace SeedLab.Saves
{
    /// <summary>
    /// One category of <c>PlayerProfile.m_playerStats</c>. Ten of these are stored from profile
    /// version 46 (<c>Version.Player.DeepNorth</c>) - see <c>PlayerProfile.LoadPlayerFromDisk</c>.
    /// <para>
    /// SeedLab does not use any of this; it is parsed because it sits between the file header and the
    /// per-world data, and skipping it by a fixed size is impossible - every list is length-prefixed.
    /// </para>
    /// </summary>
    public sealed class PlayerStatBlock
    {
        public PlayerStatBlock(float[] stats,
                               IReadOnlyList<KeyValuePair<string, float>> knownWorlds,
                               IReadOnlyList<KeyValuePair<string, float>> knownWorldKeys,
                               IReadOnlyList<KeyValuePair<string, float>> knownCommands,
                               IReadOnlyList<IReadOnlyList<KeyValuePair<string, float>>> enemyStats,
                               IReadOnlyList<KeyValuePair<string, float>> itemPickupStats,
                               IReadOnlyList<KeyValuePair<string, float>> itemCraftStats,
                               IReadOnlyList<KeyValuePair<string, float>> pickableStats,
                               IReadOnlyList<KeyValuePair<string, float>> foodEatenStats,
                               IReadOnlyList<KeyValuePair<string, float>> piecesPlacedStats)
        {
            Stats = stats;
            KnownWorlds = knownWorlds;
            KnownWorldKeys = knownWorldKeys;
            KnownCommands = knownCommands;
            EnemyStats = enemyStats;
            ItemPickupStats = itemPickupStats;
            ItemCraftStats = itemCraftStats;
            PickableStats = pickableStats;
            FoodEatenStats = foodEatenStats;
            PiecesPlacedStats = piecesPlacedStats;
        }

        /// <summary><c>m_stats[PlayerStatType]</c> - 205 floats on this build, count read from the file.</summary>
        public float[] Stats { get; }

        public IReadOnlyList<KeyValuePair<string, float>> KnownWorlds { get; }
        public IReadOnlyList<KeyValuePair<string, float>> KnownWorldKeys { get; }
        public IReadOnlyList<KeyValuePair<string, float>> KnownCommands { get; }

        /// <summary>
        /// <c>m_enemyStats[k]</c>. The count is written as a literal <c>5</c> and must be read, not
        /// assumed (05-validation.md section 5.4).
        /// </summary>
        public IReadOnlyList<IReadOnlyList<KeyValuePair<string, float>>> EnemyStats { get; }

        public IReadOnlyList<KeyValuePair<string, float>> ItemPickupStats { get; }
        public IReadOnlyList<KeyValuePair<string, float>> ItemCraftStats { get; }
        public IReadOnlyList<KeyValuePair<string, float>> PickableStats { get; }
        public IReadOnlyList<KeyValuePair<string, float>> FoodEatenStats { get; }
        public IReadOnlyList<KeyValuePair<string, float>> PiecesPlacedStats { get; }
    }

    /// <summary>
    /// What a character knows about one world, keyed by the world's <b>UID</b>.
    /// <para>
    /// The UID is random per world creation (<c>name.GetStableHashCode() + Utils.GenerateUID()</c>),
    /// not derived from the name or the seed, so matching a character entry to a world means reading
    /// <see cref="WorldMeta.Uid"/> from that world's <c>.fwl2</c> (05-validation.md section 5.4).
    /// </para>
    /// </summary>
    public sealed class WorldPlayerData
    {
        public WorldPlayerData(long worldUid, bool haveCustomSpawnPoint, Vec3f spawnPoint,
                               bool haveLogoutPoint, Vec3f logoutPoint,
                               bool haveDeathPoint, Vec3f deathPoint,
                               Vec3f homePoint, byte[]? mapDataBlob, PlayerMapData? map)
        {
            WorldUid = worldUid;
            HaveCustomSpawnPoint = haveCustomSpawnPoint;
            SpawnPoint = spawnPoint;
            HaveLogoutPoint = haveLogoutPoint;
            LogoutPoint = logoutPoint;
            HaveDeathPoint = haveDeathPoint;
            DeathPoint = deathPoint;
            HomePoint = homePoint;
            MapDataBlob = mapDataBlob;
            Map = map;
        }

        public long WorldUid { get; }

        /// <summary>A bed the player set in this world.</summary>
        public bool HaveCustomSpawnPoint { get; }
        public Vec3f SpawnPoint { get; }

        public bool HaveLogoutPoint { get; }
        public Vec3f LogoutPoint { get; }

        /// <summary>Present from profile version 30 (<c>Version.Player.DeathPoint</c>).</summary>
        public bool HaveDeathPoint { get; }
        public Vec3f DeathPoint { get; }

        /// <summary>Always written; the last bed/home position.</summary>
        public Vec3f HomePoint { get; }

        /// <summary>
        /// The raw <c>mapData</c> bytes, kept even when <see cref="Map"/> decoded, so a caller can
        /// re-examine them without re-reading the file.
        /// </summary>
        public byte[]? MapDataBlob { get; }

        /// <summary>The decoded exploration bitmaps and pins, or null when there is no map data.</summary>
        public PlayerMapData? Map { get; }
    }

    /// <summary>A parsed <c>&lt;name&gt;.fch</c> character profile.</summary>
    public sealed class CharacterProfile
    {
        public CharacterProfile(int profileVersion, bool firstSpawn, IReadOnlyList<PlayerStatBlock> stats,
                                IReadOnlyList<WorldPlayerData> worlds, string playerName, long playerId,
                                string startSeed, bool usedCheats, DateTimeOffset dateCreated,
                                byte[]? playerDataBlob, bool hashVerified, string? sourcePath)
        {
            ProfileVersion = profileVersion;
            FirstSpawn = firstSpawn;
            Stats = stats;
            Worlds = worlds;
            PlayerName = playerName;
            PlayerId = playerId;
            StartSeed = startSeed;
            UsedCheats = usedCheats;
            DateCreated = dateCreated;
            PlayerDataBlob = playerDataBlob;
            HashVerified = hashVerified;
            SourcePath = sourcePath;
        }

        /// <summary><c>Version.Player</c>; 46 = <c>DeepNorth</c> on 1.0.15.</summary>
        public int ProfileVersion { get; }

        /// <summary>Present from profile version 40 (<c>FirstSpawn</c>).</summary>
        public bool FirstSpawn { get; }

        public IReadOnlyList<PlayerStatBlock> Stats { get; }

        /// <summary>Per-world data, keyed in the file by world UID. Two entries in <c>asda.fch</c>.</summary>
        public IReadOnlyList<WorldPlayerData> Worlds { get; }

        public string PlayerName { get; }
        public long PlayerId { get; }

        /// <summary><c>m_startSeed</c>. Empty in the observed profile.</summary>
        public string StartSeed { get; }

        public bool UsedCheats { get; }

        /// <summary>
        /// <c>DateTimeOffset.FromUnixTimeSeconds(...)</c>. On profiles below version 38
        /// (<c>Stats2</c>) the game substitutes 2021-02-02 and reads nothing.
        /// </summary>
        public DateTimeOffset DateCreated { get; }

        /// <summary>
        /// The <c>Player.Save</c> blob (<c>Version.PlayerData.ChunkedNorth</c> = 33), kept as
        /// <b>raw bytes</b>. Its layout was not decompiled and nothing in SeedLab needs it, so it is
        /// exposed rather than guessed at.
        /// </summary>
        public byte[]? PlayerDataBlob { get; }

        /// <summary>
        /// Whether the trailing 64-byte SHA-512 matches the data package.
        /// <para>
        /// The game reads and <i>discards</i> this hash (<c>PlayerProfile.LoadPlayerFromDisk</c>), but
        /// it is real - <c>ZPackage.GenerateHash()</c> is
        /// <c>SHA512.Create().ComputeHash(GetArray())</c> - and it reproduces exactly for
        /// <c>asda.fch</c>, so it is a genuine integrity check on a file Steam Cloud may have
        /// replaced mid-read (05-validation.md section 5.4).
        /// </para>
        /// </summary>
        public bool HashVerified { get; }

        public string? SourcePath { get; }

        /// <summary>The entry for a world UID, or null when this character has never been there.</summary>
        public WorldPlayerData? ForWorld(long worldUid)
        {
            for (int i = 0; i < Worlds.Count; i++)
                if (Worlds[i].WorldUid == worldUid) return Worlds[i];
            return null;
        }

        public override string ToString() =>
            PlayerName + " (id " + PlayerId + ", profile v" + ProfileVersion + ", " + Worlds.Count + " world(s))";
    }
}
