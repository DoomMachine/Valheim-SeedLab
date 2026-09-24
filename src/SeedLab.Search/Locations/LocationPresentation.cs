using System;
using System.Collections.Generic;
using SeedLab.Render;
using SeedLab.WorldGen;

namespace SeedLab.Search.Locations
{
    /// <summary>
    /// Everything a surface needs to SHOW one location type: what to call it, where the name came
    /// from, which group it belongs in and where it sorts inside that group.
    ///
    /// <para><b>Why the presentation is computed in C# and shipped as data.</b> The dropdown, the map
    /// card, the CLI listing and the search vocabulary must agree about the name and the order of the
    /// same 183 rows. Computing the order here and emitting it as an integer means the page sorts by
    /// <c>(GroupOrder, SortIndex)</c> and never compares names itself - there is no second collation
    /// to drift away from the first. See <see cref="LocationDisplayOrder"/> for why that matters.</para>
    ///
    /// <para><b>Why it lives in <c>SeedLab.Search</c>.</b> Search already references
    /// <c>SeedLab.Render</c> (for the grid), so <c>MapPalette.Name</c> and
    /// <c>MapPalette.LegendOrder</c> are in scope - the biome group headings are therefore the SAME
    /// strings and the SAME order as the map legend, the <c>vseed seed</c> biome-share table and
    /// <c>/api/meta</c>'s palette. Search references neither <c>SeedLab.Data</c> nor
    /// <c>SeedLab.Locations</c>, so this type holds only plain strings; the assembly that owns both
    /// halves (<c>SeedLab.LocationOracle</c>) is what fills it in.</para>
    ///
    /// <para><b>Source is a string here, not the enum.</b> <c>DisplayNameSource</c> lives in
    /// <c>SeedLab.Data</c>, which this assembly cannot see, and it is a string by the time it reaches
    /// JSON anyway.</para>
    /// </summary>
    public sealed class LocationPresentation
    {
        public LocationPresentation(string prefab, string? displayName, string? displayNameSource,
                                    string? nameToken, IReadOnlyList<string> aliases, string groupKey,
                                    string groupHeading, int groupOrder, int sortIndex)
        {
            Prefab = prefab;
            DisplayName = displayName;
            DisplayNameSource = displayNameSource;
            NameToken = nameToken;
            Aliases = aliases;
            GroupKey = groupKey;
            GroupHeading = groupHeading;
            GroupOrder = groupOrder;
            SortIndex = sortIndex;
        }

        public string Prefab { get; }

        /// <summary>Null when the dump does not name this place; then every surface shows
        /// <see cref="Prefab"/>. It is never the prefab spelled out as if it were a name.</summary>
        public string? DisplayName { get; }

        /// <summary><c>SeedLab.Data.DisplayNameSource</c> as its name, or null when unnamed.</summary>
        public string? DisplayNameSource { get; }

        public string? NameToken { get; }

        /// <summary>Prefab first, then the display name, then the other names the game supplies.</summary>
        public IReadOnlyList<string> Aliases { get; }

        /// <summary>
        /// <c>boss</c> | <c>trader</c> | <c>biome:&lt;Biome enum name&gt;</c> | <c>multi</c>. It is a
        /// key, not a caption - <see cref="GroupHeading"/> is what a human reads.
        /// </summary>
        public string GroupKey { get; }

        public string GroupHeading { get; }

        /// <summary>See <see cref="LocationGroupTaxonomy"/>: 0 bosses, 1 traders, 2-10 the biomes in
        /// legend order, 11 the several-biome residue.</summary>
        public int GroupOrder { get; }

        /// <summary>Position inside the group under <see cref="LocationDisplayOrder"/>. Only its
        /// ORDER is meaningful: a consumer that filters the list (the dropdown shows placeable types
        /// only) still gets the right sequence, because dropping rows cannot reorder the rest.</summary>
        public int SortIndex { get; }

        public override string ToString()
            => GroupHeading + " / " + (DisplayName ?? Prefab) + " (" + Prefab + ")";
    }

    /// <summary>One group in the ordered listing: its key, its heading and where it sits.</summary>
    public sealed class LocationGroupHeading
    {
        public LocationGroupHeading(string key, string heading, int order)
        {
            Key = key;
            Heading = heading;
            Order = order;
        }

        public string Key { get; }
        public string Heading { get; }
        public int Order { get; }
    }

    /// <summary>
    /// The grouping the location dropdown and the CLI listing use: <b>bosses, then traders, then the
    /// rest by biome</b> - the user's own words for complaint 3.
    ///
    /// <para><b>Two axes, not one.</b> A Black Forest crypt is listed under Black Forest AND is still
    /// a dungeon: the group says where a place is, the existing category says what it is, and the
    /// dungeon toggle keeps working across every biome group. Only <c>boss</c> and <c>trader</c> are
    /// both at once, and there the two sets coincide exactly, which is why the heading and the
    /// toolbar toggle must use one string each rather than two.</para>
    ///
    /// <para><b>The biome order is the legend's order</b>
    /// (<see cref="MapPalette.LegendOrder"/>), not alphabetical, because that array is already the
    /// order of the map legend, the <c>vseed seed</c> biome-share table, <c>/api/meta</c>'s palette
    /// and the biome-mask string. A second biome order in the same window is exactly the kind of
    /// inconsistency this work is meant to remove.</para>
    ///
    /// <para><b>Ocean has an order but no members.</b> Of the 183 placed types none is Ocean-only -
    /// <c>Biome.Ocean</c> appears only inside the four shipwrecks' four-biome mask - so the Ocean
    /// group is empty and a renderer must OMIT it rather than draw a heading with nothing under it.
    /// Its slot (10) is kept so that the several-biome residue keeps order 11 whether or not a future
    /// build adds an Ocean-only type.</para>
    /// </summary>
    public static class LocationGroupTaxonomy
    {
        public const string BossKey = "boss";
        public const string TraderKey = "trader";

        /// <summary>The residue: types whose <c>m_biome</c> mask names more than one biome. Seven of
        /// 183 in this build, so listing them once at the end costs less than repeating each of them
        /// in two to four biome groups and making the counts not add up.</summary>
        public const string MultiKey = "multi";

        public const string BossHeading = "Bosses";
        public const string TraderHeading = "Traders";
        public const string MultiHeading = "Several biomes";

        /// <summary>Where the biome groups start. 0 is bosses and 1 is traders.</summary>
        private const int FirstBiomeOrder = 2;

        /// <summary>Every group, in the order a listing renders them. Includes the empty Ocean
        /// group; a renderer omits a group with no members rather than hard-coding which one that
        /// is.</summary>
        public static readonly IReadOnlyList<LocationGroupHeading> All = BuildAll();

        private static IReadOnlyList<LocationGroupHeading> BuildAll()
        {
            List<LocationGroupHeading> groups = new List<LocationGroupHeading>
            {
                new LocationGroupHeading(BossKey, BossHeading, 0),
                new LocationGroupHeading(TraderKey, TraderHeading, 1),
            };

            for (int i = 0; i < MapPalette.LegendOrder.Length; i++)
            {
                Biome b = MapPalette.LegendOrder[i];
                groups.Add(new LocationGroupHeading(BiomeKey(b), MapPalette.Name(b), FirstBiomeOrder + i));
            }

            groups.Add(new LocationGroupHeading(MultiKey, MultiHeading,
                                                FirstBiomeOrder + MapPalette.LegendOrder.Length));
            return groups;
        }

        public static string BiomeKey(Biome b) => "biome:" + b.ToString();

        /// <summary>
        /// Which group a type belongs to. <paramref name="isBoss"/> and <paramref name="isTrader"/>
        /// come from the derived name table (a place named by its OfferingBowl is a boss altar; one
        /// named by its Trader is a trader camp), so the grouping cannot disagree with the naming.
        /// Everything else is decided by the raw <c>ZoneLocation.m_biome</c> mask.
        /// </summary>
        public static LocationGroupHeading GroupFor(bool isBoss, bool isTrader, Biome biomeMask)
        {
            if (isBoss) return Find(BossKey);
            if (isTrader) return Find(TraderKey);

            Biome single = SingleBiome(biomeMask);
            return single == Biome.None ? Find(MultiKey) : Find(BiomeKey(single));
        }

        /// <summary>The one biome in the mask, or <see cref="Biome.None"/> when it names none or
        /// several. <c>m_biome</c> is a raw bitmask and the composite values (<c>All</c>,
        /// <c>Land</c>) are not group names.</summary>
        public static Biome SingleBiome(Biome mask)
        {
            Biome found = Biome.None;
            foreach (Biome b in MapPalette.LegendOrder)
            {
                if ((mask & b) == 0) continue;
                if (found != Biome.None) return Biome.None;
                found = b;
            }

            return found;
        }

        public static LocationGroupHeading Find(string key)
        {
            foreach (LocationGroupHeading g in All)
            {
                if (string.Equals(g.Key, key, StringComparison.Ordinal)) return g;
            }

            throw new ArgumentException("unknown location group key '" + key + "'", nameof(key));
        }
    }
}
