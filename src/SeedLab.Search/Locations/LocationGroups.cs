using System;
using System.Collections.Generic;

namespace SeedLab.Search.Locations
{
    /// <summary>One curated group: what a player calls it, and the vanilla prefabs it expands to.</summary>
    public sealed class LocationGroup
    {
        public LocationGroup(string name, string help, params string[] prefabs)
        {
            Name = name;
            Help = help;
            Prefabs = prefabs;
        }

        /// <summary>The name a query writes after <c>group:</c>.</summary>
        public string Name { get; }

        /// <summary>One line for <c>vseed search --groups</c>.</summary>
        public string Help { get; }

        public IReadOnlyList<string> Prefabs { get; }
    }

    /// <summary>
    /// The curated groups the criteria language understands, expressed purely as prefab-name strings so
    /// that this table lives in <c>SeedLab.Search</c> and needs no dumped asset data to be parsed.
    /// Whether the prefabs actually exist in the installed game is the oracle's business, and
    /// <see cref="SeedLab.Search.Evaluation.CompiledQuery"/> checks it against the oracle when one is
    /// available.
    ///
    /// <para><b>Cost is part of the definition.</b> A group's prefabs decide how much of
    /// <c>ZoneSystem.m_locations</c> a seed has to run (the ordered-prefix property - see
    /// <c>LocationTable.PrefixLengthForTarget</c>), and one late prefab can triple the cost of a query.
    /// That is why <c>tar_pits</c> and <c>fuling_villages</c> stop at the entries the reference sites
    /// show, and the alt-biome variants - which place in roughly one seed in four, a few instances, far
    /// out - live in separate <c>*_with_alt</c> groups whose extra cost is stated in the help line.</para>
    /// </summary>
    public static class LocationGroups
    {
        // ---- the pieces, so the composite groups cannot drift out of step with their parts ----------
        private static readonly string[] BossAltars =
        {
            "Eikthyrnir",                     // Eikthyr,   ordered 1
            "GoblinKing",                     // Yagluth,   ordered 2
            "GDKing",                         // the Elder, ordered 3
            "Bonemass",                       // Bonemass,  ordered 4
            "Dragonqueen",                    // Moder,     ordered 7
            "Mistlands_DvergrBossEntrance1",  // the Queen, ordered 11
            "FaderLocation",                  // Fader,     ordered 16
        };

        /// <summary>
        /// The five altars a normal playthrough visits in order. They stop at Yagluth (ordered 2),
        /// which makes the group's prefix 8 instead of the full set's 17 - and, far more importantly,
        /// leaves out the two whose rings dominate every "all bosses near" goal: the Queen is never
        /// closer than about 6 km and Fader, being AshLands-only, never closer than 7,900 m.
        /// </summary>
        private static readonly string[] BossAltarsClassic =
            { "Eikthyrnir", "GoblinKing", "GDKing", "Bonemass", "Dragonqueen" };

        private static readonly string[] Traders = { "Vendor_BlackForest", "Hildir_camp", "BogWitch_Camp" };
        private static readonly string[] BurialChambers = { "Crypt2", "Crypt3", "Crypt4" };
        private static readonly string[] SunkenCrypts = { "SunkenCrypt4" };
        private static readonly string[] TrollCaves = { "TrollCave02" };
        private static readonly string[] FrostCaves = { "MountainCave02" };
        private static readonly string[] InfestedMines = { "Mistlands_DvergrTownEntrance1", "Mistlands_DvergrTownEntrance2" };
        private static readonly string[] TarPits = { "TarPit1", "TarPit2", "TarPit3" };
        private static readonly string[] TarPitsAlt = { "TarPit1_1", "TarPit2_1", "TarPit3_1" };
        private static readonly string[] FulingVillages = { "GoblinCamp2" };
        private static readonly string[] FulingVillagesAlt = { "GoblinCamp2_1" };

        private static string[] Join(params string[][] parts)
        {
            List<string> all = new List<string>();
            foreach (string[] p in parts)
            {
                foreach (string s in p)
                {
                    if (!all.Contains(s)) all.Add(s);
                }
            }

            return all.ToArray();
        }

        private static readonly LocationGroup[] Curated =
        {
            new LocationGroup("bosses",
                "the seven boss altars: Eikthyr, the Elder, Bonemass, Moder, Yagluth, the Queen and Fader",
                BossAltars),
            new LocationGroup("bosses_classic",
                "the five altars a playthrough visits in order: Eikthyr, the Elder, Bonemass, Moder "
                + "and Yagluth - no Queen, no Fader, so 'all of them near the centre' is a goal seeds "
                + "can actually meet",
                BossAltarsClassic),
            new LocationGroup("traders",
                "Haldor, Hildir's camp and the Bog Witch's camp - all three are m_unique, so their "
                + "instances are CANDIDATES (see 'unique semantics')",
                Traders),
            new LocationGroup("burial_chambers", "Black Forest burial chambers (Crypt2/3/4)", BurialChambers),
            new LocationGroup("sunken_crypts", "Swamp sunken crypts", SunkenCrypts),
            new LocationGroup("troll_caves", "Black Forest troll caves", TrollCaves),
            new LocationGroup("frost_caves", "Mountain frost caves", FrostCaves),
            new LocationGroup("infested_mines", "Mistlands infested mines (both Dvergr town entrances)", InfestedMines),
            new LocationGroup("dungeons",
                "every enterable dungeon: burial chambers, sunken crypts, troll caves, frost caves and "
                + "infested mines",
                Join(BurialChambers, SunkenCrypts, TrollCaves, FrostCaves, InfestedMines)),
            new LocationGroup("fuling_villages",
                "Fuling villages (GoblinCamp2), which is what the reference sites plot",
                FulingVillages),
            new LocationGroup("fuling_villages_with_alt",
                "Fuling villages plus the 'Goblin Plains' alt-biome variant GoblinCamp2_1, which is "
                + "ordered 176 of 183 - it makes a seed cost about 4x a plain fuling_villages query",
                Join(FulingVillages, FulingVillagesAlt)),
            new LocationGroup("tar_pits", "tar pits (TarPit1/2/3)", TarPits),
            new LocationGroup("tar_pits_with_alt",
                "tar pits plus the 'Death Plains' alt-biome variants, which are the last three entries "
                + "of the ordered list - this group forces the FULL 183-entry placement on every seed",
                Join(TarPits, TarPitsAlt)),
            new LocationGroup("surtling_geysers", "Swamp fire geysers (FireHole)", "FireHole"),
            new LocationGroup("charred_fortresses", "Ashlands charred fortresses", "CharredFortress"),
            new LocationGroup("places_of_mystery",
                "the three Places of Mystery - all m_unique with m_quantity 1, so their single "
                + "candidate IS the position",
                "PlaceofMystery1", "PlaceofMystery2", "PlaceofMystery3"),
            new LocationGroup("hildir_quests",
                "Hildir's three quest locations: the cave, the crypt and the plains fortress",
                "Hildir_cave", "Hildir_crypt", "Hildir_plainsfortress"),
            new LocationGroup("boss_rooms",
                "the seven boss altars plus the Deep North boss room (DN_Bossroom)",
                Join(BossAltars, new[] { "DN_Bossroom" })),
            new LocationGroup("runestones",
                "every biome's lore runestone",
                "Runestone_Meadows", "Runestone_BlackForest", "Runestone_Swamps", "Runestone_Mountains",
                "Runestone_Plains", "Runestone_Mistlands", "Runestone_Ashlands", "Runestone_DeepNorth",
                "Runestone_Greydwarfs", "Runestone_Draugr", "Runestone_Boars"),
        };

        /// <summary>Anything a caller should print about the group vocabulary itself. Empty in
        /// this build; see <see cref="All"/> for what would put something in it.</summary>
        public static IReadOnlyList<string> Problems => ProblemList;

        private static readonly List<string> ProblemList = new List<string>();

        /// <summary>
        /// Every group the criteria language understands: the curated ones above, then one per entry
        /// of <see cref="WorldFeatures.All"/>.
        ///
        /// <para>The features are merged in here rather than kept in a second vocabulary, so that
        /// <c>vseed search --groups</c>, the "groups: ..." line of an unknown-group error and the web
        /// goal bounds all list them without each having to learn about features separately. A
        /// feature's group is built from the feature's own prefab list
        /// (<see cref="WorldFeatures.Groups"/>), so the two cannot drift apart.</para>
        ///
        /// <para>A feature whose id is already a curated group name would silently change what an
        /// existing saved query means, so it is DROPPED and recorded in <see cref="Problems"/> rather
        /// than overwriting or being overwritten. There are no collisions in this build, and the test
        /// suite asserts that.</para>
        /// </summary>
        public static readonly IReadOnlyList<LocationGroup> All = BuildAll();

        private static IReadOnlyList<LocationGroup> BuildAll()
        {
            List<LocationGroup> all = new List<LocationGroup>(Curated);

            HashSet<string> taken = new HashSet<string>(StringComparer.Ordinal);
            foreach (LocationGroup g in Curated) taken.Add(g.Name);

            foreach (string clash in WorldFeatures.IdCollisionsWith(taken))
            {
                ProblemList.Add("the world feature '" + clash + "' has the same name as a curated "
                                + "location group, so it was NOT added to the group vocabulary - a "
                                + "query that says group:" + clash + " still means the curated one. "
                                + "Rename the feature.");
            }

            foreach (LocationGroup g in WorldFeatures.Groups)
            {
                // taken grows as features are accepted, so two FEATURES sharing an id are caught here
                // too. Without that, both would be added and ByName's dictionary would throw from a
                // static initialiser - surfacing as a TypeInitializationException on whatever line
                // happened to touch this class first, which is the least useful place to read it.
                if (!taken.Add(g.Name))
                {
                    ProblemList.Add("two location groups are both called '" + g.Name + "', so only "
                                    + "the first is in the vocabulary. A world feature id doubles as "
                                    + "a group name and must be unique.");
                    continue;
                }

                all.Add(g);
            }

            return all;
        }

        private static readonly Dictionary<string, LocationGroup> ByName = Build();

        private static Dictionary<string, LocationGroup> Build()
        {
            Dictionary<string, LocationGroup> d = new Dictionary<string, LocationGroup>(StringComparer.Ordinal);
            foreach (LocationGroup g in All) d.Add(g.Name, g);
            return d;
        }

        public static IEnumerable<string> Names
        {
            get
            {
                foreach (LocationGroup g in All) yield return g.Name;
            }
        }

        public static LocationGroup? Find(string name)
            => ByName.TryGetValue(name, out LocationGroup? g) ? g : null;

        public static IReadOnlyList<string>? Expand(string name)
            => ByName.TryGetValue(name, out LocationGroup? g) ? g.Prefabs : null;
    }
}
