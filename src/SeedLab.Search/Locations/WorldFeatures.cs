using System;
using System.Collections.Generic;

namespace SeedLab.Search.Locations
{
    /// <summary>
    /// How much of a feature the world seed actually decides. <b>Never merged into one number</b> -
    /// two uncertainties of different kinds multiplied together produce a percentage that reads like a
    /// forecast and is not one.
    /// </summary>
    public enum FeatureDeterminism
    {
        /// <summary>Entirely a function of the seed: where the location itself lands. SeedLab computes
        /// this exactly.</summary>
        Placement = 0,

        /// <summary>A <c>RandomSpawn</c> inside a location prefab. Seeded from
        /// <c>worldSeed + zone.x * 4271 + zone.y * 9187</c>, so it IS a function of the seed and is
        /// computable offline - this build does not replay it yet.</summary>
        LocationSpawn = 1,

        /// <summary>A <c>RandomSpawn</c> inside a generated dungeon room. Same kind of stream, one
        /// level further in.</summary>
        RoomSpawn = 2,

        /// <summary><c>ZoneVegetation</c>, not a location at all - ore deposits and the like.</summary>
        Vegetation = 3,

        /// <summary>
        /// A container's contents. <c>Container.Awake</c> -&gt; <c>AddDefaultItems</c> -&gt;
        /// <c>DropTable.GetDropListItems</c> draws on the AMBIENT <c>UnityEngine.Random</c> stream the
        /// first time a zone is loaded and writes the result to the ZDO. <b>Not a function of the
        /// seed at all</b>: two players on the same seed get different chests. No offline tool can
        /// predict it, and one that claims to is wrong.
        /// </summary>
        ContainerRoll = 4,

        /// <summary><c>m_unique</c>: the game keeps one candidate and deletes the rest, and which one
        /// survives is exploration order, not seed.</summary>
        UniqueSurvivor = 5,
    }

    /// <summary>
    /// One curated "thing a player goes looking for" that is not a single location type: a small set
    /// of prefabs plus the one honest sentence about what the seed does and does not decide there.
    /// </summary>
    public sealed class WorldFeature
    {
        public WorldFeature(string id, string name, IReadOnlyList<string> prefabs,
                            FeatureDeterminism determinism, string what, string note, string evidence)
        {
            Id = id;
            Name = name;
            Prefabs = prefabs;
            Determinism = determinism;
            What = what;
            Note = note;
            Evidence = evidence;
        }

        /// <summary>The name a query writes after <c>group:</c> - so the criteria language needs no
        /// new parser and no new metric for this to work.</summary>
        public string Id { get; }

        /// <summary>What the player calls it: "Axe-head houses". This is what the dropdown filter
        /// matches on, and it is why typing "axe" finds the two houses at all.</summary>
        public string Name { get; }

        public IReadOnlyList<string> Prefabs { get; }

        public FeatureDeterminism Determinism { get; }

        /// <summary>One line: what a player gets here.</summary>
        public string What { get; }

        /// <summary>THE honesty sentence. Printed verbatim by the CLI, the web card and the search
        /// explanation - one wording, so the three cannot drift into three different promises.</summary>
        public string Note { get; }

        /// <summary>The dump file and path that proves it, so the claim can be re-checked.</summary>
        public string Evidence { get; }

        public override string ToString() => Id + " (" + Prefabs.Count + " prefabs)";
    }

    /// <summary>
    /// The curated world features, as plain prefab-name strings.
    ///
    /// <para><b>Why the table is here.</b> <see cref="LocationGroups"/> already states the rule:
    /// curation lives in <c>SeedLab.Search</c> as strings so it needs no dumped data and cannot fail
    /// closed. Both the CLI and the web project already reference this assembly, so one table serves
    /// three surfaces with no new project reference and no new package. Nothing here reads
    /// <c>locationchildren.json</c> (28.5 MB) or <c>roomchildren.json</c> (71.0 MB) - these are
    /// constants, and the files that prove them are read by the test suite, not by a search.</para>
    ///
    /// <para><b>Why a feature is also a <c>group:</c>.</b> <see cref="WorldFeature.Id"/> doubles as the
    /// group name and <see cref="Groups"/> builds one <see cref="LocationGroup"/> per feature from the
    /// same rows, so the two cannot drift apart. <c>group:axe_head_houses</c> therefore works in the
    /// criteria language the day this table ships, with no change to the parser.</para>
    ///
    /// <para><b>One entry today.</b> A general item-to-location index was considered and rejected: 46
    /// of the 116 distinct container items in the dump live in ten or more host prefabs, so an index
    /// would not discriminate, and the honest sentence differs per item across four determinism
    /// classes. A curated table of the questions people actually ask, each with its own sentence, says
    /// more and promises less.</para>
    /// </summary>
    public static class WorldFeatures
    {
        /// <summary>
        /// The axe-head sentence, in one place because the CLI, the web card and
        /// <c>vseed explain</c> all print it and must not each paraphrase it.
        ///
        /// <para>The two probabilities are kept apart on purpose. 31/112 is the product of a
        /// seed-determined coin and an unseeded draw, and a reader who is given only the product
        /// cannot tell that half of it is knowable and half of it never will be. For any ONE seed
        /// WoodHouse2's chance is either 31/56 or nothing at all.</para>
        /// </summary>
        public const string AxeHeadNote =
            "These are CANDIDATE houses, never chests you are promised. Where the house is IS a "
            + "function of the seed and that is what SeedLab computes. What is inside it is not. "
            + "WoodHouse6 always contains TreasureChest_meadows_01, and a chest that exists yields "
            + "its axe head 31 times in 56 (55.4 %) - exact, derived from DropTable.GetDropListItems, "
            + "not modelled. WoodHouse2's TreasureChest_meadows_02 sits behind a RandomSpawn at 50 %: "
            + "that coin comes out of the zone-seeded stream (worldSeed + zone.x * 4271 + "
            + "zone.y * 9187), so the seed DOES decide it and an offline tool could compute it - this "
            + "build does not replay it yet. So WoodHouse2 is 31/112 (27.7 %) before the seed is "
            + "consulted, but for any ONE seed it is either 31/56 or zero; the two numbers are not "
            + "interchangeable and must never be multiplied into a single forecast. The draw for the "
            + "axe head itself is a different kind of unknown again: Container.Awake -> "
            + "AddDefaultItems -> DropTable.GetDropListItems runs on the AMBIENT UnityEngine.Random "
            + "stream the first time anyone loads that zone and is saved to the ZDO, so two players "
            + "on the same seed get different chests. SeedLab can tell you where every one of these "
            + "houses is; no offline tool can tell you which of them holds an axe head.";

        private static readonly string[] AxeHeadHousePrefabs = { "WoodHouse2", "WoodHouse6" };

        public static readonly IReadOnlyList<WorldFeature> All = new WorldFeature[]
        {
            new WorldFeature(
                id: "axe_head_houses",
                name: "Axe-head houses",
                prefabs: AxeHeadHousePrefabs,
                determinism: FeatureDeterminism.ContainerRoll,
                what: "the two abandoned Meadows houses whose chest can hold an axe head: WoodHouse6 "
                      + "(AxeHead1, \"Curious Axe Head\") and WoodHouse2 (AxeHead2, \"Mysterious Axe "
                      + "Head\")",
                note: AxeHeadNote,
                evidence: "locationchildren.json WoodHouse6.containers[0] = TreasureChest_meadows_01 "
                          + "(unconditional) and WoodHouse2.containers[0] = TreasureChest_meadows_02 "
                          + "behind randomSpawns[50] at m_chanceToSpawn 50; search.json carries both "
                          + "chest terms. 20 instances of each on seed bmbp74, and bobmitch.com agrees "
                          + "on all 40 coordinates (2026-09-24)."),
        };

        /// <summary>
        /// One <see cref="LocationGroup"/> per feature, built from the SAME rows, so a feature and its
        /// group can never name different prefabs. <see cref="LocationGroups.All"/> includes these, so
        /// <c>vseed search --groups</c> lists them and <c>group:</c> resolves them with no other
        /// change.
        /// </summary>
        public static readonly IReadOnlyList<LocationGroup> Groups = BuildGroups();

        private static IReadOnlyList<LocationGroup> BuildGroups()
        {
            List<LocationGroup> groups = new List<LocationGroup>(All.Count);
            foreach (WorldFeature f in All)
            {
                string[] prefabs = new string[f.Prefabs.Count];
                for (int i = 0; i < f.Prefabs.Count; i++) prefabs[i] = f.Prefabs[i];
                groups.Add(new LocationGroup(f.Id, HelpFor(f), prefabs));
            }

            return groups;
        }

        /// <summary>
        /// The <c>--groups</c> line. It states which of the reference sites' two questions this
        /// answers and which one is REFUSED, because a group that silently answers a different
        /// question than the one asked is worse than one that is missing.
        /// </summary>
        private static string HelpFor(WorldFeature f)
        {
            string help = f.What + ". 'nearest_distance ... from: spawn' answers "
                          + "\"how far is the nearest one\" exactly";

            if (string.Equals(f.Id, "axe_head_houses", StringComparison.Ordinal))
            {
                help += " (bobmitch.com's axeChestNearest), and count_within answers how many are "
                        + "inside a STRAIGHT-LINE radius. bobmitch's other axe-head question, "
                        + "axeChestsAfoot, is REFUSED: 'afoot' is reachable-on-foot, count_within "
                        + "is a Euclidean disc that counts houses across open water and on other "
                        + "islands, and SeedLab has no walkable flood fill to answer it with";
            }

            return help + ". CONTENTS ARE NOT PREDICTED - see the note this group prints.";
        }

        public static WorldFeature? Find(string id)
        {
            if (id == null) return null;
            foreach (WorldFeature f in All)
            {
                if (string.Equals(f.Id, id, StringComparison.Ordinal)) return f;
            }

            return null;
        }

        /// <summary>The prefabs a feature id expands to, or null when it is not a feature. Mirrors
        /// <see cref="LocationGroups.Expand"/> so a caller can try both.</summary>
        public static IReadOnlyList<string>? Expand(string id) => Find(id)?.Prefabs;

        /// <summary>Every feature that names <paramref name="prefab"/>. Keyed by PREFAB, not by group
        /// name, so a bare <c>location:WoodHouse6</c> carries the same warning a
        /// <c>group:axe_head_houses</c> query does.</summary>
        public static IReadOnlyList<WorldFeature> ForPrefab(string prefab)
        {
            List<WorldFeature> hits = new List<WorldFeature>();
            if (prefab == null) return hits;
            foreach (WorldFeature f in All)
            {
                foreach (string p in f.Prefabs)
                {
                    if (!string.Equals(p, prefab, StringComparison.Ordinal)) continue;
                    hits.Add(f);
                    break;
                }
            }

            return hits;
        }

        /// <summary>Every feature any of <paramref name="prefabs"/> belongs to, de-duplicated and in
        /// table order.</summary>
        public static IReadOnlyList<WorldFeature> ForPrefabs(IEnumerable<string> prefabs)
        {
            List<WorldFeature> hits = new List<WorldFeature>();
            if (prefabs == null) return hits;

            HashSet<string> wanted = new HashSet<string>(prefabs, StringComparer.Ordinal);
            foreach (WorldFeature f in All)
            {
                foreach (string p in f.Prefabs)
                {
                    if (!wanted.Contains(p)) continue;
                    hits.Add(f);
                    break;
                }
            }

            return hits;
        }

        /// <summary>
        /// Feature ids that collide with a group name already in use. Putting a feature into the
        /// <c>group:</c> vocabulary changes what an existing query means if the name is taken, so the
        /// clash is detected rather than resolved: <see cref="LocationGroups"/> drops the colliding
        /// feature group and says so, and the test suite asserts this list is empty.
        ///
        /// <para>It is a method and not a static constructor check on purpose - a throwing static
        /// initialiser surfaces as <c>TypeInitializationException</c> from whatever unrelated line
        /// happened to touch the type first, which is the least useful place to read it.</para>
        /// </summary>
        public static IReadOnlyList<string> IdCollisionsWith(IEnumerable<string> existingGroupNames)
        {
            List<string> clashes = new List<string>();
            if (existingGroupNames == null) return clashes;

            HashSet<string> taken = new HashSet<string>(existingGroupNames, StringComparer.Ordinal);
            foreach (WorldFeature f in All)
            {
                if (taken.Contains(f.Id)) clashes.Add(f.Id);
            }

            return clashes;
        }
    }
}
