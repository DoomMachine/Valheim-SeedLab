using System.Collections.Generic;

namespace SeedLab.Saves
{
    /// <summary><c>Minimap.PinType</c> (decompiled, lines 24-44).</summary>
    public enum PinType
    {
        Icon0 = 0,
        Icon1 = 1,
        Icon2 = 2,
        Icon3 = 3,
        Death = 4,
        Bed = 5,
        Icon4 = 6,
        Shout = 7,
        None = 8,
        Boss = 9,
        Player = 10,
        RandomEvent = 11,
        Ping = 12,
        EventArea = 13,
        Hildir1 = 14,
        Hildir2 = 15,
        Hildir3 = 16,
        Memorial = 17,
    }

    /// <summary>
    /// One saved map pin. Only pins with <c>m_save == true</c> reach the file
    /// (<c>Minimap.GetMapData</c>), so a mod's temporary markers are absent by design.
    /// </summary>
    public sealed class MapPin
    {
        public MapPin(string name, Vec3f position, PinType type, bool isChecked, long ownerId, string author)
        {
            Name = name;
            Position = position;
            Type = type;
            IsChecked = isChecked;
            OwnerId = ownerId;
            Author = author;
        }

        /// <summary>May be a localisation token such as <c>$enemy_eikthyr</c>.</summary>
        public string Name { get; }

        public Vec3f Position { get; }
        public PinType Type { get; }

        /// <summary>Present from map version 3 (<c>PinsChecked</c>); false on older saves.</summary>
        public bool IsChecked { get; }

        /// <summary>Present from map version 6 (<c>PinsOwnerID</c>); 0 on older saves and for local pins.</summary>
        public long OwnerId { get; }

        /// <summary>A <c>PlatformUserID</c> string; present from map version 8 (<c>PinsAuthor</c>), otherwise "".</summary>
        public string Author { get; }

        public override string ToString() => Type + " \"" + Name + "\" at " + Position;
    }

    /// <summary>
    /// The decoded <c>mapData</c> blob of one world entry in a character save: that character's own
    /// exploration bitmaps and saved pins for that world.
    /// <para>
    /// <c>Minimap.GetMapData</c> / <c>SetMapData</c> (decompiled):
    /// <code>
    /// int32  8                        // Version.Map.PinsAuthor
    /// int32  gzipLength ; gzip -&gt;     // WriteCompressed, from Version.Map.Compressed (7)
    ///     int32 textureSize           // 2048 - the game throws "Error: minimap mismatch" on any other value
    ///     byte[textureSize^2] explored          // 1 byte per pixel, 0/1
    ///     byte[textureSize^2] exploredOthers    // 1 byte per pixel, 0/1
    ///     int32 nPins ; { string name; Vector3 pos; int32 pinType; bool checked; int64 ownerID; string author }
    ///     bool publicReferencePosition
    /// </code>
    /// </para>
    /// <para>
    /// <b>The exploration grid is the same grid as the minimap cache</b>: pixel
    /// <c>k = row*N + col</c> with <c>col = Utils.RoundToInt(x/12f + 1024f)</c> and
    /// <c>row = Utils.RoundToInt(z/12f + 1024f)</c> - the <c>(int)(f + 64000.5f) - 64000</c> form, not
    /// <c>Math.Round</c> (<c>Minimap.WorldToPixel</c>, and <c>Minimap.Explore(int,int)</c> indexing
    /// <c>m_explored[y * m_textureSize + x]</c>, line 1859). So an "overlay my exploration" feature is
    /// a direct per-pixel AND with the cache, with no resampling
    /// (05-validation.md section 5.4). Use <see cref="Geometry"/> for the mapping.
    /// </para>
    /// <para>
    /// <b>One caveat the spec does not mention.</b> The two buffers are indexed identically, but the
    /// world point behind index k is not quite the same for both: <c>GenerateWorldMap</c> samples
    /// pixel k at <c>(j - N/2)*P + P/2</c> while <c>WorldToPixel</c> places it at <c>(j - N/2)*P</c>,
    /// a half-pixel (6 m) offset - see <see cref="MinimapGeometry.WorldToPixel"/>. Overlaying
    /// index-for-index reproduces exactly what the player saw, because the game overlays the same two
    /// textures the same way; only a caller converting an explored <i>pixel</i> back to a world
    /// <i>position</i> needs to care, and it should use <see cref="MinimapGeometry.AnchorWorldX"/>
    /// rather than the sample point.
    /// </para>
    /// <para>
    /// Shared (cartography-table) exploration is <b>not</b> here - it lives in a world ZDO, whose
    /// layout is Unverified.
    /// </para>
    /// </summary>
    public sealed class PlayerMapData
    {
        public PlayerMapData(int mapVersion, int textureSize, byte[] explored, byte[] exploredOthers,
                             IReadOnlyList<MapPin> pins, bool publicReferencePosition)
        {
            MapVersion = mapVersion;
            TextureSize = textureSize;
            Explored = explored;
            ExploredOthers = exploredOthers;
            Pins = pins;
            PublicReferencePosition = publicReferencePosition;
        }

        /// <summary><c>Version.Map</c>; 8 = <c>PinsAuthor</c> on this build.</summary>
        public int MapVersion { get; }

        /// <summary>Read from the blob itself, not assumed. 2048 in both observed entries.</summary>
        public int TextureSize { get; }

        /// <summary>One byte per pixel, 0 or 1: what this character explored personally.</summary>
        public byte[] Explored { get; }

        /// <summary>One byte per pixel: what was explored by others and shared to this character.</summary>
        public byte[] ExploredOthers { get; }

        public IReadOnlyList<MapPin> Pins { get; }

        /// <summary>Present from map version 4 (<c>VisibleOnMap</c>).</summary>
        public bool PublicReferencePosition { get; }

        /// <summary>
        /// The pixel grid these bitmaps use. The pixel size is not stored in the blob any more than it
        /// is in the cache; it is <see cref="MinimapGeometry.MeasuredPixelSize"/>, which the cache
        /// reader independently re-derives from the world-edge discontinuity.
        /// </summary>
        public MinimapGeometry Geometry => new MinimapGeometry(TextureSize, MinimapGeometry.MeasuredPixelSize);

        /// <summary>How many pixels this character has explored personally.</summary>
        public int ExploredPixelCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < Explored.Length; i++) if (Explored[i] != 0) n++;
                return n;
            }
        }
    }
}
