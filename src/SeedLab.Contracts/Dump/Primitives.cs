namespace SeedLab.Contracts.Dump
{
    // The public fields below are deliberately camelCase: they ARE the JSON keys. SeedLab.Contracts
    // multi-targets netstandard2.0, where System.Text.Json (and therefore [JsonPropertyName]) is not
    // available without a NuGet package, and this project takes no packages. Naming the field exactly
    // as the wire key removes the whole class of "the attribute and the field disagree" bugs; the
    // dumper's reflection writer emits the field name verbatim and the net10.0 reader matches it
    // case-insensitively.

    /// <summary>A <c>UnityEngine.Vector2</c>.</summary>
    public sealed class Vec2Def
    {
        public float x;
        public float y;
    }

    /// <summary>A <c>UnityEngine.Vector3</c>.</summary>
    public sealed class Vec3Def
    {
        public float x;
        public float y;
        public float z;
    }

    /// <summary>A <c>Vector2s</c> / <c>Vector2i</c> zone or grid coordinate (integers, no bits object).</summary>
    public sealed class Vec2IntDef
    {
        public int x;
        public int y;
    }

    /// <summary>A <c>UnityEngine.Color</c> in linear 0..1 floats, exactly as the prefab stores it.</summary>
    public sealed class ColorDef
    {
        public float r;
        public float g;
        public float b;
        public float a;

        /// <summary>The value after Unity's <c>Color -&gt; Color32</c> conversion
        /// (round-half-to-even, spec 05 section 1.3), as RGBA hex - what the minimap cache stores.</summary>
        public string? rgba32;
    }

    /// <summary>
    /// <c>SoftReferenceableAssets.AssetID</c>: four uints, declared v3, v2, v1, v0 in that order
    /// (<c>AssetID</c>, SoftReferenceableAssets.dll). It is the identity the game uses for
    /// "same location prefab" in <c>ZoneSystem.m_locationIDCache</c> / <c>HaveLocationInRange</c>.
    /// </summary>
    public sealed class AssetIdDef
    {
        public uint v3;
        public uint v2;
        public uint v1;
        public uint v0;

        /// <summary>32 lowercase hex digits, <c>AssetID.ToString()</c>: v3, v2, v1, v0 each "%08x".</summary>
        public string? hex;

        public bool isValid;
    }

    /// <summary>
    /// Where a <c>ZoneLocation</c> / <c>ZoneVegetation</c> entry came from, so a game update that
    /// reorders the lists is diagnosable. <c>ZoneSystem.SetupLocations</c> concatenates in this order:
    /// the ZoneSystem prefab's own list, then each <c>LocationList</c> sorted by <c>m_sortOrder</c>
    /// with an UNSTABLE <c>List.Sort</c>, then each <c>AltBiome.m_addLocations</c>.
    /// </summary>
    public sealed class EntrySourceDef
    {
        /// <summary>"ZoneSystemPrefab", "LocationList" or "AltBiome".</summary>
        public string? kind;

        /// <summary>The <c>LocationList</c> GameObject name, or the <c>AltBiome.m_name</c>.</summary>
        public string? name;

        /// <summary><c>LocationList.m_sortOrder</c>, or -1 when it does not apply.</summary>
        public int sortOrder;
    }
}
