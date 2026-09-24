using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using SeedLab.Contracts.Dump;
using SeedLab.Data;
using SeedLab.LocationOracle;
using SeedLab.Search.Criteria;
using SeedLab.Search.Evaluation;
using SeedLab.Search.Locations;

namespace SeedLab.SearchTests
{
    /// <summary>
    /// The guards on the naming, grouping and world-feature layer.
    ///
    /// <para>Four of these checks exist because a plausible wrong answer would otherwise ship
    /// silently. (1) The boss table is complete only because the Mistlands queen's offering bowl is
    /// joined in from a ROOM prefab; a derivation that reads location prefabs alone reports seven
    /// bosses and looks fine. (2) That room join is a recorded constant at run time - the file it came
    /// from is 71 MB - so the constant is re-derived from the real file here and the two tables are
    /// compared row for row. (3) A runestone's own name token would caption places "Runestone",
    /// "Sacrificial Stone" and "A mysterious text"; the negatives make re-adopting it a test failure
    /// rather than a discovery. (4) The display order folds case with <c>ToLowerInvariant</c> + ordinal
    /// rather than <c>OrdinalIgnoreCase</c>, and no pair among the 183 placed types happens to trip
    /// the difference - only the golden listing would catch a regression.</para>
    ///
    /// <para>(5) The entrance-door rule (2026-09-24) names seventeen dungeons from
    /// <c>Teleport.m_enterText</c>, and three of its captions are shared by several prefabs. Its
    /// checks pin the names, the source label, the two boss places where the door must NOT win, that
    /// a shared caption resolves to none of its prefabs without a note, and - by placing the named
    /// types against the game's own fresh-world goldens - that naming moved no instance.</para>
    /// </summary>
    public static class NameChecks
    {
        /// <summary>
        /// A verbatim copy of <c>SeedLab.Cli.Analysis.LocationCatalog.BossAltars</c>. It is copied
        /// rather than referenced because <c>LocationCatalog</c> lives in <c>SeedLab.Cli</c>, which
        /// this suite does not reference and should not start referencing to reach two string arrays.
        /// The whole point of the check is that the DERIVED set equals the CURATED one, after which
        /// the curated one can be deleted.
        /// </summary>
        private static readonly string[] CuratedBossAltars =
        {
            "Eikthyrnir", "GDKing", "Bonemass", "Dragonqueen", "GoblinKing",
            "Mistlands_DvergrBossEntrance1", "FaderLocation", "DN_Bossroom",
        };

        /// <summary>A verbatim copy of <c>LocationCatalog.Traders</c>; see above.</summary>
        private static readonly string[] CuratedTraders =
            { "Vendor_BlackForest", "Hildir_camp", "BogWitch_Camp" };

        /// <summary>The eight bosses and their <c>m_bossOrder</c>, measured 2026-09-24.</summary>
        private static readonly (string Prefab, string Name, int Order)[] BossGolden =
        {
            ("DN_Bossroom", "Kall Fimbulbringer", 0),
            ("Eikthyrnir", "Eikthyr", 1),
            ("GDKing", "The Elder", 2),
            ("Bonemass", "Bonemass", 3),
            ("Dragonqueen", "Moder", 4),
            ("GoblinKing", "Yagluth", 5),
            ("Mistlands_DvergrBossEntrance1", "The Queen", 6),
            ("FaderLocation", "Fader", 7),
        };

        /// <summary>Names a rejected rule would have produced. None may ever be a display name.</summary>
        private static readonly string[] ForbiddenNames =
            { "Runestone", "Sacrificial Stone", "A mysterious text" };

        /// <summary>
        /// Every place the entrance-door rule names, measured from the dump that first carried the
        /// doors (dumper run 6, 2026-09-24): the <c>m_enterText</c> token on each prefab's one usable
        /// captioned <c>Teleport</c> and what it resolves to in the dumped English table. Checked
        /// against the Valheim wiki by prefab id the same day - the wiki agrees where it has a page
        /// (Sunken Crypts = SunkenCrypt4, Infested Mine = both Dvergr town entrances, Smouldering Tomb
        /// = Hildir_crypt, Tomb of Lord Reto = PlaceofMystery3, Putrid Hole = MorgenHole1/2/3,
        /// Frost Caves = MountainCave02 through its point-of-interest table, Burial Chambers =
        /// DG_ForestCrypt, the generator all three Crypt prefabs carry), and has no page at all for
        /// the three Deep North captions or Bear Cave. The rule reads the dump, never this table.
        /// </summary>
        private static readonly (string Prefab, string Name, string Token)[] DoorGolden =
        {
            ("Crypt2", "Burial Chambers", "$location_forestcrypt"),
            ("Crypt3", "Burial Chambers", "$location_forestcrypt"),
            ("Crypt4", "Burial Chambers", "$location_forestcrypt"),
            ("TrollCave02", "Troll Cave", "$location_forestcave"),
            ("SunkenCrypt4", "Sunken Crypts", "$location_sunkencrypt"),
            ("MountainCave02", "Frost Caves", "$location_mountaincave"),
            ("Mistlands_DvergrTownEntrance1", "Infested Mine", "$location_dvergrtown"),
            ("Mistlands_DvergrTownEntrance2", "Infested Mine", "$location_dvergrtown"),
            ("Hildir_cave", "Howling Cavern", "$hud_pin_hildir2"),
            ("Hildir_crypt", "Smouldering Tomb", "$hud_pin_hildir1"),
            ("PlaceofMystery3", "Tomb of Lord Reto", "$location_mausoleum"),
            ("MorgenHole1", "Putrid Hole", "$location_morgenhole"),
            ("MorgenHole2", "Putrid Hole", "$location_morgenhole"),
            ("MorgenHole3", "Putrid Hole", "$location_morgenhole"),
            ("TheHole01", "Winding tunnels", "$location_thehole"),
            ("MorkBorg", "Mörkhalla", "$location_morkhalla"),
            ("BearCave", "Bear Cave", "$location_bearcave"),
        };

        public static void Run(Action<bool, string, string> check)
        {
            ILocationOracle oracle = DumpedLocationOracle.Create(out string? problem);
            if (!oracle.Available)
            {
                check(false, "the dumped oracle is available", problem ?? oracle.UnavailableReason);
                return;
            }

            DumpedLocationOracle dumped = (DumpedLocationOracle)oracle;
            LocationDisplayNames? names = dumped.DisplayNames;
            if (names == null)
            {
                check(false, "the display-name table was built", string.Join(" | ", dumped.NameNotes));
                return;
            }

            CheckBossTable(check, names);
            CheckTraders(check, names);
            CheckRoomJoin(check, names);
            CheckNegatives(check, names);
            CheckNotes(check, dumped);
            CheckNoteOwnership(check, names);
            CheckComparer(check);
            CheckResolution(check, dumped);
            CheckWorldFeatures(check);
            CheckDropdownGolden(check, dumped);
            CheckDoorNames(check, names);
            CheckSharedDoorNames(check, dumped);
            CheckDoorPlacement(check, dumped);
        }

        private static void CheckBossTable(Action<bool, string, string> check, LocationDisplayNames names)
        {
            List<string> derived = new List<string>(names.BossAltarPrefabs);
            check(derived.Count == BossGolden.Length, "the derivation finds 8 boss altars",
                  derived.Count + " found: " + string.Join(", ", derived));

            List<int> orders = new List<int>();
            bool allNamed = true;
            foreach ((string prefab, string name, int order) in BossGolden)
            {
                LocationDisplayName n = names.For(prefab);
                bool ok = n.Source == DisplayNameSource.BossAltar
                          && string.Equals(n.DisplayName, name, StringComparison.Ordinal)
                          && n.BossOrder == order;
                if (!ok) allNamed = false;
                check(ok, "boss altar " + prefab + " is '" + name + "'",
                      (n.DisplayName ?? "(unnamed)") + ", source " + n.Source + ", bossOrder "
                      + n.BossOrder);
                orders.Add(n.BossOrder);
            }

            orders.Sort();
            bool contiguous = orders.Count == 8;
            for (int i = 0; contiguous && i < orders.Count; i++) contiguous = orders[i] == i;
            check(contiguous, "m_bossOrder is contiguous 0-7 over the derived set",
                  string.Join(", ", orders));

            check(allNamed && SetEquals(derived, CuratedBossAltars),
                  "the derived boss set equals the curated LocationCatalog.BossAltars",
                  string.Join(", ", derived));

            // NorthMemorialPlace has an OfferingBowl whose bossFlag is false. It is a memorial, not an
            // altar, and a rule that tested "has a bowl" rather than "has a boss" would name it.
            check(names.For("NorthMemorialPlace").Source == DisplayNameSource.Prefab,
                  "NorthMemorialPlace is not a boss altar (its bowl has bossFlag false)",
                  names.For("NorthMemorialPlace").DisplayName ?? "(unnamed)");
        }

        private static void CheckTraders(Action<bool, string, string> check, LocationDisplayNames names)
        {
            check(SetEquals(new List<string>(names.TraderPrefabs), CuratedTraders),
                  "the derived trader set equals the curated LocationCatalog.Traders",
                  string.Join(", ", names.TraderPrefabs));

            check(string.Equals(names.For("Vendor_BlackForest").DisplayName, "Haldor", StringComparison.Ordinal)
                  && names.For("Vendor_BlackForest").Source == DisplayNameSource.Trader,
                  "Vendor_BlackForest is Haldor, from its Trader component",
                  names.For("Vendor_BlackForest").DisplayName ?? "(unnamed)");

            // DECISION 1: the name is DISPLAYED, and its source still records that a human made the
            // join because Trader.m_name is empty in this build.
            LocationDisplayName bog = names.For("BogWitch_Camp");
            check(string.Equals(bog.DisplayName, "The Bog Witch", StringComparison.Ordinal)
                  && bog.Source == DisplayNameSource.TraderNpcTokenConvention
                  && bog.IsDerivedByConvention,
                  "BogWitch_Camp displays 'The Bog Witch' and keeps its convention source",
                  (bog.DisplayName ?? "(unnamed)") + ", source " + bog.Source);

            bool unverified = false;
            foreach (string n in names.Notes)
            {
                if (n.StartsWith("Unverified:", StringComparison.Ordinal) && n.Contains("BogWitch_Camp"))
                    unverified = true;
            }

            check(unverified, "the Bog Witch note is printed and still says Unverified:",
                  unverified ? "present" : "missing");
        }

        /// <summary>
        /// The recorded room join, re-derived from <c>roomchildren.json</c> itself. This is the only
        /// place that pays the 71 MB read; if the constant in <c>LocationDisplayNames</c> ever stops
        /// matching the dump, this is what says so.
        /// </summary>
        private static void CheckRoomJoin(Action<bool, string, string> check, LocationDisplayNames runtime)
        {
            GameData data = GameData.Load();

            int bowls = 0;
            string room = "", roomList = "";
            int theme = -1, dataTheme = -1, order = -1;
            string bossName = "", bossPrefab = "";
            bool flag = false;

            foreach (SeedLab.Contracts.Dump.RoomOccupantsDef r in data.RoomOccupants)
            {
                if (r.offeringBowls == null) continue;
                foreach (SeedLab.Contracts.Dump.OfferingBowlDef b in r.offeringBowls)
                {
                    bowls++;
                    room = r.prefabName ?? "";
                    roomList = r.roomListName ?? "";
                    theme = r.roomTheme;
                    dataTheme = r.roomDataTheme;
                    bossName = b.bossLocalizedName ?? "";
                    bossPrefab = b.bossPrefabName ?? "";
                    flag = b.bossFlag;
                    order = b.bossOrder;
                }
            }

            check(bowls == 1, "exactly one OfferingBowl exists among all 358 room prefabs",
                  bowls + " found");
            check(string.Equals(room, "dvergr_new_bossroom_ENTRANCE02", StringComparison.Ordinal),
                  "that bowl is in dvergr_new_bossroom_ENTRANCE02", room + " (" + roomList + ")");
            check(theme == 256 && dataTheme == 256,
                  "its Room.m_theme and RoomData.m_theme are both 256",
                  "roomTheme " + theme + ", roomDataTheme " + dataTheme);
            check(flag && order == 6 && string.Equals(bossName, "The Queen", StringComparison.Ordinal),
                  "it summons The Queen, bossFlag true, bossOrder 6",
                  bossPrefab + " / " + bossName + " / bossOrder " + order);

            // And the whole table: the cheap path and the 71 MB path must agree row for row, or the
            // recorded constant has drifted away from the dump it was read out of.
            LocationDisplayNames viaRooms = LocationDisplayNames.BuildWithRoomJoin(data);
            int differences = 0;
            string firstDifference = "";
            foreach (LocationDisplayName a in runtime.All)
            {
                LocationDisplayName b = viaRooms.For(a.Prefab);
                if (string.Equals(a.DisplayName, b.DisplayName, StringComparison.Ordinal)
                    && a.Source == b.Source && a.BossOrder == b.BossOrder) continue;

                differences++;
                if (firstDifference.Length == 0)
                {
                    firstDifference = a.Prefab + ": '" + (a.DisplayName ?? "(none)") + "' vs '"
                                      + (b.DisplayName ?? "(none)") + "'";
                }
            }

            check(differences == 0,
                  "the recorded room join gives the same table as reading roomchildren.json",
                  differences == 0 ? "identical over " + CountOf(runtime) + " rows"
                                   : differences + " differ, first: " + firstDifference);
        }

        private static void CheckNegatives(Action<bool, string, string> check, LocationDisplayNames names)
        {
            string offender = "";
            foreach (LocationDisplayName n in names.All)
            {
                if (n.DisplayName == null) continue;
                foreach (string bad in ForbiddenNames)
                {
                    if (string.Equals(n.DisplayName, bad, StringComparison.Ordinal))
                        offender = n.Prefab + " = '" + n.DisplayName + "'";
                }

                if (n.DisplayName.StartsWith("Lore: ", StringComparison.Ordinal))
                    offender = n.Prefab + " = '" + n.DisplayName + "'";
            }

            check(offender.Length == 0,
                  "no display name is a runestone's own name token or a journal entry",
                  offender.Length == 0 ? "none of Runestone / Sacrificial Stone / A mysterious text / "
                                         + "Lore: ... appears" : offender);

            // The four discoverLabel names, which ARE legitimate and must survive the same pass.
            check(string.Equals(names.For("AncientUpgradeStation").DisplayName, "Forge of Potential",
                                StringComparison.Ordinal)
                  && string.Equals(names.For("CharredFortress").DisplayName, "Charred Fortress",
                                   StringComparison.Ordinal)
                  && string.Equals(names.For("Hildir_plainsfortress").DisplayName, "Sealed Tower",
                                   StringComparison.Ordinal),
                  "the three discoverLabel names resolve",
                  names.For("AncientUpgradeStation").DisplayName + " / "
                  + names.For("CharredFortress").DisplayName + " / "
                  + names.For("Hildir_plainsfortress").DisplayName);

            // DECISION 2: DN_Bossroom is captioned by its boss; "Aesir Passage" survives as an alias.
            bool aesir = false;
            foreach (string a in names.For("DN_Bossroom").Aliases)
            {
                if (string.Equals(a, "Aesir Passage", StringComparison.Ordinal)) aesir = true;
            }

            check(aesir && string.Equals(names.For("DN_Bossroom").DisplayName, "Kall Fimbulbringer",
                                         StringComparison.Ordinal),
                  "DN_Bossroom is 'Kall Fimbulbringer' with 'Aesir Passage' as an alias",
                  string.Join(", ", names.For("DN_Bossroom").Aliases));

            // The runestone pin survives as an alias only, never as the caption.
            bool emerald = false;
            foreach (string a in names.For("FaderLocation").Aliases)
            {
                if (string.Equals(a, "The Emerald Flame", StringComparison.Ordinal)) emerald = true;
            }

            check(emerald && string.Equals(names.For("FaderLocation").DisplayName, "Fader",
                                           StringComparison.Ordinal),
                  "FaderLocation is 'Fader' with 'The Emerald Flame' as an alias",
                  string.Join(", ", names.For("FaderLocation").Aliases));

            // A stone inside StartTemple names five OTHER altars. None may become StartTemple's name.
            check(names.For("StartTemple").Source == DisplayNameSource.Prefab
                  && names.For("StartTemple").Aliases.Count == 1,
                  "StartTemple takes no name from the five BossStone_* it contains",
                  string.Join(", ", names.For("StartTemple").Aliases));
        }

        private static void CheckNotes(Action<bool, string, string> check, DumpedLocationOracle oracle)
        {
            string collision = "";
            foreach (string n in oracle.NameNotes)
            {
                // The second phrase is the note for a real collision on a key the game already shares
                // between several prefabs ("Burial Chambers"); the sharing itself says nothing.
                if (n.Contains("no longer resolves to either") || n.Contains("now also means")) collision = n;
            }

            check(collision.Length == 0, "no name folds onto another prefab's key",
                  collision.Length == 0 ? "all fold indexes are injective" : collision);

            check(LocationGroups.Problems.Count == 0,
                  "no world feature id collides with a curated group name",
                  LocationGroups.Problems.Count == 0 ? "none"
                                                     : string.Join(" | ", LocationGroups.Problems));
        }

        /// <summary>
        /// Every note carries the place it is about, so a command that shows ONE selection can print
        /// the ones that belong beside it and leave the rest.
        ///
        /// <para>The check that matters is the last one. Before 2026-09-24 the Bog Witch's
        /// <c>Unverified:</c> note printed on every <c>vseed locations</c> run, including
        /// <c>--type boss</c>, where that trader is not in the selection at all. It shares
        /// <c>Out.Warn</c> with the <c>m_unique</c> caveats and the seed-ambiguity note, and a caveat
        /// repeated where it does not apply is how a reader learns to skip the channel. Filtering it
        /// by string surgery on the sentence would break the moment the sentence is reworded, which is
        /// why the prefab is carried and why this asserts on the carried value.</para>
        /// </summary>
        private static void CheckNoteOwnership(Action<bool, string, string> check, LocationDisplayNames names)
        {
            // 1. The flat list and the structured list are the same notes in the same order.
            IReadOnlyList<string> flat = names.Notes;
            IReadOnlyList<LocationDisplayNote> detailed = names.NoteDetails;
            bool sameOrder = flat.Count == detailed.Count;
            for (int i = 0; sameOrder && i < flat.Count; i++)
            {
                if (!string.Equals(flat[i], detailed[i].Text, StringComparison.Ordinal)) sameOrder = false;
            }

            check(sameOrder, "Notes and NoteDetails carry the same sentences in the same order",
                  flat.Count.ToString() + " note(s); "
                  + (sameOrder ? "identical" : "THEY DIFFER, so one of them is lying about the other"));

            // 2. Every note that names a prefab names one the table knows. A typo here would silently
            //    hide the note from every selection, which is worse than printing it too often.
            // For() never returns null - an unheard-of prefab comes back as an unnamed row - so the
            // membership test has to be against the table's own list rather than against For().
            HashSet<string> known = new HashSet<string>(StringComparer.Ordinal);
            foreach (LocationDisplayName n in names.All) known.Add(n.Prefab);

            List<string> unknown = new List<string>();
            foreach (LocationDisplayNote n in detailed)
            {
                if (n.Prefab != null && !known.Contains(n.Prefab)) unknown.Add(n.Prefab);
            }

            check(unknown.Count == 0, "every per-prefab note names a prefab the table has",
                  unknown.Count == 0 ? "all owned notes resolve"
                                     : "unknown: " + string.Join(", ", unknown));

            // 3. Asking for every prefab gives back exactly the flat list - nothing is lost by
            //    filtering, only deferred.
            List<string> everyPrefab = new List<string>();
            foreach (LocationDisplayName n in names.All) everyPrefab.Add(n.Prefab);
            IReadOnlyList<string> all = names.NotesFor(everyPrefab);
            bool lossless = all.Count == flat.Count;
            for (int i = 0; lossless && i < all.Count; i++)
            {
                if (!string.Equals(all[i], flat[i], StringComparison.Ordinal)) lossless = false;
            }

            check(lossless, "NotesFor(every prefab) == Notes",
                  lossless ? all.Count.ToString() + " note(s), unchanged"
                           : "filtering LOSES notes: " + all.Count + " vs " + flat.Count);

            // 4. The one this was written for.
            const string BogWitch = "BogWitch_Camp";
            IReadOnlyList<string> withoutTrader = names.NotesFor(new[] { "GDKing", "Eikthyrnir" });
            IReadOnlyList<string> withTrader = names.NotesFor(new[] { BogWitch });
            bool mentioned = false;
            foreach (string n in withoutTrader)
            {
                if (n.Contains(BogWitch, StringComparison.Ordinal)) mentioned = true;
            }

            bool shown = false;
            foreach (string n in withTrader)
            {
                if (n.Contains(BogWitch, StringComparison.Ordinal)) shown = true;
            }

            // The note only exists while the Bog Witch is named by convention; if a later dump gives
            // that Trader a real m_name the note goes away and both halves below are vacuously true,
            // which is the right behaviour rather than a failure.
            bool noteExists = false;
            foreach (LocationDisplayNote n in detailed)
            {
                if (string.Equals(n.Prefab, BogWitch, StringComparison.Ordinal)) noteExists = true;
            }

            check(!mentioned, "a selection without " + BogWitch + " is not told about it",
                  mentioned ? "its note printed anyway" : "silent, as it should be");

            check(!noteExists || shown, "a selection containing " + BogWitch + " IS told about it",
                  !noteExists ? "no note about it in this dump (nothing to say)"
                              : shown ? "its note printed" : "SUPPRESSED - the note exists and was hidden");
        }

        private static void CheckComparer(Action<bool, string, string> check)
        {
            check(Less("WoodHouse9", "WoodHouse10"), "digit runs compare as numbers", "WoodHouse9 < WoodHouse10");
            check(Less("Crypt2", "Crypt3") && Less("Crypt3", "Crypt4"), "Crypt2 < Crypt3 < Crypt4", "");
            check(Less("Mistlands_Statue1", "Mistlands_StatueGroup1"),
                  "a prefix sorts before its extension", "Mistlands_Statue1 < Mistlands_StatueGroup1");
            check(Less("SwampHut1", "SwampHut1_1"), "SwampHut1 < SwampHut1_1", "");
            check(Less("Bonemass", "Eikthyr") && Less("Moder", "The Elder") && Less("The Queen", "Yagluth"),
                  "the boss group reads Bonemass .. The Elder, The Queen, Yagluth (no article stripping)",
                  "");
            check(Less("Runestone_Boars", "Runestone_Meadows"),
                  "the underscore sorts before letters, as ToLowerInvariant + Ordinal requires", "");
        }

        private static bool Less(string a, string b) => LocationDisplayOrder.Compare(a, a, b, b) < 0;

        private static void CheckResolution(Action<bool, string, string> check, DumpedLocationOracle oracle)
        {
            Resolves(check, oracle, "GDKing", "GDKing", "prefab name");
            Resolves(check, oracle, "The Elder", "GDKing", "display name");
            Resolves(check, oracle, "the elder", "GDKing", "display name (case and spacing folded)");
            Resolves(check, oracle, "elder", "GDKing", "display name (leading 'the' dropped)");
            Resolves(check, oracle, "gdking", "GDKing", "prefab name (case and spacing folded)");
            Resolves(check, oracle, "Eikthyr", "Eikthyrnir", "display name");
            Resolves(check, oracle, "Haldor", "Vendor_BlackForest", "display name");
            Resolves(check, oracle, "The Bog Witch", "BogWitch_Camp", "display name");

            // "Bonemass" is BOTH a prefab and a display name for the same row. Pass 1 must win, so the
            // echo says "prefab name" and nothing that works today changes meaning.
            Resolves(check, oracle, "Bonemass", "Bonemass", "prefab name");

            // 'queen' must never reach Dragonqueen, which is MODER's altar in the Mountains.
            Resolves(check, oracle, "queen", "Mistlands_DvergrBossEntrance1",
                     "display name (leading 'the' dropped)");

            check(oracle.ResolveLocationName("Eikthyrnirr") == null,
                  "a genuine non-name resolves to nothing", "Eikthyrnirr");
            check(oracle.ResolveLocationName("Hildir")?.Prefab == "Hildir_camp",
                  "'Hildir' is the trader's camp, not one of the four Hildir_* prefixes",
                  oracle.ResolveLocationName("Hildir")?.Prefab ?? "(null)");
            check(UnavailableLocationOracle.Instance.ResolveLocationName("The Elder") == null,
                  "an oracle with no dumped table answers null to every name", "");

            LocationNameMatch? bog = oracle.ResolveLocationName("Bog Witch");
            check(bog != null && bog.Note != null && bog.Note.StartsWith("Unverified:", StringComparison.Ordinal),
                  "resolving the Bog Witch carries its Unverified: caveat", bog?.Note ?? "(null)");
        }

        private static void Resolves(Action<bool, string, string> check, ILocationOracle oracle,
                                     string typed, string prefab, string how)
        {
            LocationNameMatch? m = oracle.ResolveLocationName(typed);
            bool ok = m != null && string.Equals(m.Prefab, prefab, StringComparison.Ordinal)
                      && string.Equals(m.How, how, StringComparison.Ordinal);
            check(ok, "'" + typed + "' resolves to " + prefab + " by " + how,
                  m == null ? "(no match)" : m.Prefab + " by " + m.How);
        }

        private static void CheckWorldFeatures(Action<bool, string, string> check)
        {
            WorldFeature? axe = WorldFeatures.Find("axe_head_houses");
            check(axe != null && axe.Prefabs.Count == 2, "the axe-head feature names two prefabs",
                  axe == null ? "(missing)" : string.Join(", ", axe.Prefabs));

            IReadOnlyList<string>? expanded = LocationGroups.Expand("axe_head_houses");
            check(expanded != null && expanded.Count == 2
                  && new List<string>(expanded).Contains("WoodHouse2")
                  && new List<string>(expanded).Contains("WoodHouse6"),
                  "group:axe_head_houses resolves through the ordinary group vocabulary",
                  expanded == null ? "(unknown group)" : string.Join(", ", expanded));

            check(WorldFeatures.ForPrefab("WoodHouse6").Count == 1
                  && WorldFeatures.ForPrefabs(new[] { "WoodHouse2" }).Count == 1
                  && WorldFeatures.ForPrefab("WoodHouse1").Count == 0,
                  "the feature is found BY PREFAB, so a bare location: query gets the note too", "");

            // The two uncertainties must both be stated and must not be presented as one number.
            check(axe != null && axe.Note.Contains("31 times in 56") && axe.Note.Contains("31/112")
                  && axe.Note.Contains("either 31/56 or zero"),
                  "the axe-head note keeps the two uncertainties apart (31/56 and 31/112)", "");
            check(axe != null && axe.Determinism == FeatureDeterminism.ContainerRoll,
                  "the axe head itself is a ContainerRoll: not a function of the seed at all", "");

            LocationGroup? g = LocationGroups.Find("axe_head_houses");
            check(g != null && g.Help.Contains("axeChestNearest") && g.Help.Contains("REFUSED"),
                  "the group help says which reference-site question it answers and which it refuses",
                  g?.Help ?? "(missing)");
        }

        /// <summary>
        /// The 183-row dropdown, regenerated through the real comparer and diffed against the golden.
        /// This is the ONLY guard on the <c>ToLowerInvariant</c> + <c>Ordinal</c> fold - no pair among
        /// the current rows exercises the difference from <c>OrdinalIgnoreCase</c> by itself.
        /// </summary>
        private static void CheckDropdownGolden(Action<bool, string, string> check, DumpedLocationOracle oracle)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "location-groups.golden.txt");
            if (!File.Exists(path))
            {
                check(false, "the dropdown golden fixture is on disk", path);
                return;
            }

            List<(string Heading, string Display, string Prefab)> expected = ParseGolden(path);

            List<LocationPresentation> rows = new List<LocationPresentation>();
            foreach (string prefab in oracle.PrefabNames)
            {
                LocationTypeInfo t = oracle.TypeOf(prefab)!;
                if (!t.Placeable || t.Presentation == null) continue;
                rows.Add(t.Presentation);
            }

            rows.Sort((a, b) => a.GroupOrder != b.GroupOrder
                ? a.GroupOrder.CompareTo(b.GroupOrder)
                : a.SortIndex.CompareTo(b.SortIndex));

            check(rows.Count == expected.Count, "the listing has 183 placed rows",
                  rows.Count + " built, " + expected.Count + " in the golden");

            // Every difference is printed, up to a dozen: one renamed row shifts everything after it
            // inside its group, so the FIRST difference on its own would not say what changed.
            int differences = 0;
            string detail = "";
            int n = Math.Min(rows.Count, expected.Count);
            for (int i = 0; i < n; i++)
            {
                LocationPresentation r = rows[i];
                (string heading, string display, string prefab) = expected[i];
                string got = r.GroupHeading + " / " + (r.DisplayName ?? r.Prefab) + " (" + r.Prefab + ")";
                string want = heading + " / " + display + " (" + prefab + ")";
                if (string.Equals(got, want, StringComparison.Ordinal)) continue;

                differences++;
                if (differences <= 12)
                {
                    detail += (detail.Length == 0 ? "" : " ;; ") + "row " + i + ": got " + got
                              + ", want " + want;
                }
            }

            check(differences == 0, "every row's group, name and prefab match the golden listing",
                  differences == 0 ? rows.Count + " rows identical" : differences + " differ: " + detail);

            // The Ocean group has an order but no members; a renderer must omit it rather than draw an
            // empty heading, so nothing here may claim it.
            bool ocean = false;
            foreach (LocationPresentation r in rows)
            {
                if (string.Equals(r.GroupKey, "biome:Ocean", StringComparison.Ordinal)) ocean = true;
            }

            check(!ocean, "no placed type is Ocean-only, so the Ocean group is empty and is omitted", "");
        }

        private static readonly Regex GoldenRow = new Regex(@"^\s{2}(.+?)\s+\((\S+)\)\s+q=", RegexOptions.Compiled);
        private static readonly Regex GoldenHead = new Regex(@"^--- (?:\[\d+\] )?(.+?)\s+\(", RegexOptions.Compiled);

        private static List<(string Heading, string Display, string Prefab)> ParseGolden(string path)
        {
            List<(string, string, string)> rows = new List<(string, string, string)>();
            string heading = "";
            foreach (string line in File.ReadAllLines(path))
            {
                Match h = GoldenHead.Match(line);
                if (h.Success)
                {
                    heading = h.Groups[1].Value;
                    continue;
                }

                Match r = GoldenRow.Match(line);
                if (r.Success) rows.Add((heading, r.Groups[1].Value.Trim(), r.Groups[2].Value));
            }

            return rows;
        }

        /// <summary>
        /// The entrance-door rule: the seventeen names, their source label and token, and the two
        /// places where the door is NOT the name - which is where a precedence mistake would show.
        /// </summary>
        private static void CheckDoorNames(Action<bool, string, string> check, LocationDisplayNames names)
        {
            bool allRight = true;
            foreach ((string prefab, string name, string token) in DoorGolden)
            {
                LocationDisplayName n = names.For(prefab);
                bool ok = n.Source == DisplayNameSource.TeleportEnterText
                          && string.Equals(n.DisplayName, name, StringComparison.Ordinal)
                          && string.Equals(n.NameToken, token, StringComparison.Ordinal)
                          && n.Provenance.Contains("Teleport.m_enterText", StringComparison.Ordinal)
                          && n.Aliases.Count >= 2
                          && string.Equals(n.Aliases[1], name, StringComparison.Ordinal);
                if (!ok) allRight = false;
                check(ok, prefab + " is '" + name + "' from its entrance door (" + token + ")",
                      (n.DisplayName ?? "(unnamed)") + ", source " + n.Source + ", token "
                      + (n.NameToken ?? "-"));
            }

            // Exactly these seventeen and no others: a door rule that also fired somewhere else would
            // be naming a place from a door this table has not looked at.
            List<string> doorNamed = new List<string>();
            foreach (LocationDisplayName n in names.All)
            {
                if (n.Source == DisplayNameSource.TeleportEnterText) doorNamed.Add(n.Prefab);
            }

            string[] expected = new string[DoorGolden.Length];
            for (int i = 0; i < DoorGolden.Length; i++) expected[i] = DoorGolden[i].Prefab;
            check(allRight && SetEquals(doorNamed, expected),
                  "the door rule names exactly the 17 prefabs measured from dumper run 6",
                  doorNamed.Count + " named by a door: " + string.Join(", ", doorNamed));

            // Precedence, case 1: The Queen's entrance has a captioned door ("Infested Citadel"), and
            // the boss rule must still win, or the boss group loses a member the moment doors exist.
            // The caption survives as an alias, so it can still be typed.
            LocationDisplayName queen = names.For("Mistlands_DvergrBossEntrance1");
            bool citadel = false;
            foreach (string a in queen.Aliases)
            {
                if (string.Equals(a, "Infested Citadel", StringComparison.Ordinal)) citadel = true;
            }

            check(queen.Source == DisplayNameSource.BossAltar
                  && string.Equals(queen.DisplayName, "The Queen", StringComparison.Ordinal) && citadel,
                  "Mistlands_DvergrBossEntrance1 stays 'The Queen' and answers to 'Infested Citadel'",
                  string.Join(", ", queen.Aliases));

            // Precedence, case 2: DN_Bossroom's only captioned door ("The Prison") is inactive in the
            // prefab. The game cannot show that caption unless something outside the dump enables the
            // door, so it is neither the name nor an alias - which also keeps the boss rule's answer.
            LocationDisplayName dn = names.For("DN_Bossroom");
            bool prison = false;
            foreach (string a in dn.Aliases)
            {
                if (string.Equals(a, "The Prison", StringComparison.Ordinal)) prison = true;
            }

            check(dn.Source == DisplayNameSource.BossAltar && !prison,
                  "DN_Bossroom's inactive door ('The Prison') is neither its name nor an alias",
                  string.Join(", ", dn.Aliases));

            // The discover label outranks a door. The only prefab carrying both in 1.0.15 is
            // DN_Bossroom (checked above), so this pins the label rule's three names rather than an
            // overlap.
            check(names.For("Hildir_plainsfortress").Source == DisplayNameSource.DiscoverLabel
                  && names.For("CharredFortress").Source == DisplayNameSource.DiscoverLabel
                  && names.For("AncientUpgradeStation").Source == DisplayNameSource.DiscoverLabel,
                  "the three discoverLabel names keep their source", "");

            // A name that works raises no note: none of the seventeen may print anything when shown.
            List<string> doorNotes = new List<string>(names.NotesFor(expected, includeTableWide: false));
            check(doorNotes.Count == 0, "no door-named place carries a note",
                  doorNotes.Count == 0 ? "none" : string.Join(" | ", doorNotes));

            // Corroboration from a second surface of the game: Hildir's map table is a Vegvisir whose
            // pins name her two dungeons with the very tokens their doors carry. Read from the file
            // itself, because the naming layer deliberately does not load the Vegvisirs.
            GameData data = GameData.Load();
            string pinCrypt = "", pinCave = "";
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(
                       Path.Combine(data.Directory, "locationchildren.json"))))
            {
                foreach (JsonElement loc in doc.RootElement.GetProperty("locations").EnumerateArray())
                {
                    if (!loc.TryGetProperty("vegvisirs", out JsonElement vegs)) continue;
                    foreach (JsonElement v in vegs.EnumerateArray())
                    {
                        foreach (JsonElement pin in v.GetProperty("locations").EnumerateArray())
                        {
                            string target = pin.GetProperty("locationName").GetString() ?? "";
                            string pinToken = pin.GetProperty("pinNameToken").GetString() ?? "";
                            if (target == "Hildir_crypt") pinCrypt = pinToken;
                            if (target == "Hildir_cave") pinCave = pinToken;
                        }
                    }
                }
            }

            check(pinCrypt == names.For("Hildir_crypt").NameToken && pinCave == names.For("Hildir_cave").NameToken,
                  "Hildir's map table pins her tomb and her cavern with the tokens their doors carry",
                  "Hildir_crypt " + pinCrypt + ", Hildir_cave " + pinCave);
        }

        /// <summary>
        /// Three captions are shared - the game names every variant of a dungeon alike - so typing one
        /// must pick NONE of its prefabs, say nothing on every run, and refuse with the prefabs listed.
        /// </summary>
        private static void CheckSharedDoorNames(Action<bool, string, string> check, DumpedLocationOracle oracle)
        {
            foreach (string shared in new[] { "Burial Chambers", "burial chambers", "Infested Mine", "Putrid Hole" })
            {
                LocationNameMatch? m = oracle.ResolveLocationName(shared);
                check(m == null, "'" + shared + "' is shared, so it resolves to no single prefab",
                      m == null ? "(no match)" : m.Prefab + " by " + m.How);
            }

            Resolves(check, oracle, "Sunken Crypts", "SunkenCrypt4", "display name");
            Resolves(check, oracle, "frost caves", "MountainCave02", "display name (case and spacing folded)");
            Resolves(check, oracle, "Infested Citadel", "Mistlands_DvergrBossEntrance1", "display name");
            Resolves(check, oracle, "Mörkhalla", "MorkBorg", "display name");

            // A query that names a shared caption is refused, and the hint puts the three prefabs in
            // front of the user - which is where the ambiguity has to be explained.
            string hint = "";
            bool refused = false;
            try
            {
                CompiledQuery.Compile(QueryReader.Parse(
                    "{\"version\":1,\"goals\":[{\"id\":\"g\",\"target\":\"location:Burial Chambers\","
                    + "\"metric\":\"count\",\"test\":\"at_least\",\"value\":1,\"importance\":\"must\"}]}",
                    "name-checks"), oracle);
            }
            catch (QueryException ex)
            {
                refused = true;
                hint = ex.Hint ?? "";
            }

            check(refused && hint.Contains("Crypt2") && hint.Contains("Crypt3") && hint.Contains("Crypt4"),
                  "location:Burial Chambers is refused and the hint lists Crypt2, Crypt3 and Crypt4", hint);
        }

        /// <summary>
        /// Naming must not move a single instance. For every fresh-world golden this data folder
        /// holds, the seventeen door-named types are placed through the oracle and compared, float bit
        /// for float bit and in order, with the instances the game itself registered.
        /// </summary>
        private static void CheckDoorPlacement(Action<bool, string, string> check, DumpedLocationOracle oracle)
        {
            GameData data = GameData.Load();
            List<string> prefabs = new List<string>();
            foreach ((string prefab, _, _) in DoorGolden) prefabs.Add(prefab);
            LocationPlan plan = oracle.Plan(prefabs, needSpawn: false);

            int worlds = 0;
            foreach (string seedHex in new[] { "0480A34C", "B83592B8" })
            {
                if (!data.Goldens.Has("goldens/locationinstances-" + seedHex + ".json")) continue;
                worlds++;

                LocationInstancesFile golden = data.Goldens.LocationInstances(seedHex);
                LocationWorld world = oracle.Run(plan, golden.seed, 2, null);

                int compared = 0, differing = 0;
                string first = "";
                foreach (string prefab in prefabs)
                {
                    List<string> want = new List<string>();
                    foreach (LocationInstanceDef i in golden.instances ?? Array.Empty<LocationInstanceDef>())
                    {
                        if (i.prefabName == prefab) want.Add(Bits(i.x, i.z));
                    }

                    List<string> got = new List<string>();
                    foreach (LocationHit h in world.Hits)
                    {
                        if (h.Prefab == prefab) got.Add(Bits(h.X, h.Z));
                    }

                    compared += want.Count;
                    bool same = want.Count == got.Count;
                    for (int k = 0; same && k < want.Count; k++) same = want[k] == got[k];
                    if (same) continue;

                    differing++;
                    if (first.Length == 0) first = prefab + ": " + got.Count + " placed vs " + want.Count + " in the golden";
                }

                check(differing == 0 && compared > 0,
                      "the door-named types place exactly as the game did in " + seedHex
                      + " (" + (golden.worldName ?? "?") + ")",
                      differing == 0 ? compared + " instances, bit-identical and in order"
                                     : differing + " type(s) differ, first " + first);
            }

            check(worlds > 0, "a fresh-world golden was available for the placement comparison",
                  worlds + " world(s) compared");
        }

        private static string Bits(float x, float z)
            => BitConverter.SingleToInt32Bits(x).ToString("X8") + "/" + BitConverter.SingleToInt32Bits(z).ToString("X8");

        private static int CountOf(LocationDisplayNames names)
        {
            int n = 0;
            foreach (LocationDisplayName unused in names.All) n++;
            return n;
        }

        private static bool SetEquals(List<string> a, string[] b)
        {
            if (a.Count != b.Length) return false;
            foreach (string s in b)
            {
                if (!a.Contains(s)) return false;
            }

            return true;
        }
    }
}
