using System;
using System.Collections.Generic;
using SeedLab.Contracts.Dump;
using UnityEngine;

namespace SeedLab.Dumper
{
    /// <summary>
    /// Resolves a configured list of prefab names across everything this plugin can reach, and writes
    /// <c>search.json</c>.
    ///
    /// <b>This class exists to make an absence loud.</b> Every other output of the dump is a table, and
    /// a table cannot distinguish "X is not in the game" from "X is somewhere the walk never went" from
    /// "X was in a prefab that failed to load". After a user has spent a game launch on a dump, being
    /// left to guess between those three is the failure this whole revision is about. So the rules here
    /// are:
    /// <list type="number">
    /// <item>every sought name gets an entry, found or not - nothing is omitted;</item>
    /// <item>a NOT FOUND publishes exactly what WAS searched and what was NOT;</item>
    /// <item>a NOT FOUND is marked <c>inconclusive</c> whenever anything the name could have hidden
    /// behind did not complete - a failed prefab load, a switched-off walk, a null
    /// <c>DungeonDB.instance</c>, or <b>a walk that ran and loaded nothing</b> - and the verdict
    /// sentence says so. Coverage is measured in COUNTS, never in "did the walk run" flags: see
    /// <see cref="Coverage"/>;</item>
    /// <item>the verdict is printed to the console as well as written to the file, because the user is
    /// sitting in front of a running game and will read the console line first.</item>
    /// </list>
    ///
    /// <b>Matching is on the normalised name</b> (<see cref="Names.Normalize"/>), including for the
    /// sought names themselves, so <c>piece_maypole (1)</c> matches <c>piece_maypole</c> and a config
    /// entry typed as <c>piece_maypole (1)</c> still searches for the right thing.
    ///
    /// It is a pure accumulator: it draws nothing, constructs nothing, and never touches a game object
    /// it was not handed. Its only game call is the optional <c>ZNetScene.GetPrefab(name)</c> existence
    /// probe, a dictionary lookup on <c>m_namedPrefabs</c>.
    /// </summary>
    internal sealed class SearchIndex
    {
        private sealed class Term
        {
            public string Requested;
            public string Normalized;
            public int Hash;
            public readonly List<SearchHitDef> Hits = new List<SearchHitDef>();
            public bool ZNetChecked;
            public bool ZNetFound;
        }

        /// <summary>A hard ceiling on the hits recorded for one name. A name like <c>Beech1</c> would
        /// otherwise produce tens of thousands of entries and turn a diagnostic file into the largest
        /// thing in the dump. The count is still exact - <see cref="Term.Hits"/> is capped, the
        /// per-term <c>hitCount</c> is not - and the cap is reported in the term's note.</summary>
        public const int MaxHitsPerTerm = 500;

        private readonly List<Term> _terms = new List<Term>();
        private readonly Dictionary<string, Term> _byName = new Dictionary<string, Term>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _counts = new Dictionary<string, int>(StringComparer.Ordinal);

        // ---- coverage, filled by the modes as they go -------------------------------------------------
        public bool LocationChildWalkRan;
        public bool RoomWalkRan;
        public string RoomWalkSkipped;
        public int LocationPrefabsWalked, LocationPrefabsLoaded, LocationPrefabsFailed;
        public int RoomPrefabsWalked, RoomPrefabsLoaded, RoomPrefabsFailed;
        public int InteriorWalksWithErrors;
        public int LocationTableEntries, VegetationTableEntries;

        /// <summary>RandomObject weighted options whose own prefab subtree was opened and read.</summary>
        public int RandomObjectOptionsResolved;

        /// <summary>Containers found inside those options, each with its drop table.</summary>
        public int RandomObjectOptionContainersRead;

        /// <summary>Option scans that threw. Each one is a place a sought name could have been.</summary>
        public int RandomObjectOptionScanErrors;

        /// <summary>RandomObjects nested INSIDE a RandomObject option. Their own options are one level
        /// deeper and are not followed - a real, countable gap, not a standing limitation.</summary>
        public int NestedRandomObjectsInOptions;

        /// <summary>True when at least one name is being looked for; the walks skip their matching work
        /// entirely when this is false.</summary>
        public bool Active { get { return _terms.Count > 0; } }

        /// <summary>
        /// Parses the configured list. Blank entries are dropped, duplicates (after normalisation) are
        /// merged, and each name is normalised the same way the walk normalises a GameObject name - so
        /// the two cannot disagree.
        /// </summary>
        public static SearchIndex FromConfig(string configured)
        {
            var idx = new SearchIndex();
            if (string.IsNullOrEmpty(configured)) return idx;

            string[] parts = configured.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string raw in parts)
            {
                string requested = raw.Trim();
                if (requested.Length == 0) continue;
                string normalized = Names.Normalize(requested);
                if (string.IsNullOrEmpty(normalized)) continue;
                if (idx._byName.ContainsKey(normalized)) continue;

                var t = new Term
                {
                    Requested = requested,
                    Normalized = normalized,
                    Hash = Names.Hash(normalized),
                };
                idx._terms.Add(t);
                idx._byName[normalized] = t;
            }
            return idx;
        }

        /// <summary>
        /// One prefab loaded but its interior walk did not complete, so part of its subtree was never
        /// indexed. Counted rather than ignored: it is one of the things that turns a NOT FOUND into an
        /// INCONCLUSIVE, which is the whole reason this class exists.
        /// </summary>
        public void NoteInteriorWalkError()
        {
            InteriorWalksWithErrors++;
        }

        /// <summary>Is this normalised name one of the ones being looked for? A dictionary probe, called
        /// once per transform of every prefab, so it must stay this cheap.</summary>
        public bool Wants(string normalizedName)
        {
            return normalizedName != null && _byName.ContainsKey(normalizedName);
        }

        /// <summary>Records one occurrence. Never throws: a search that dies would take the dump with
        /// it, and the dump is worth more than the search.</summary>
        public void Add(string normalizedName, SearchHitDef hit)
        {
            try
            {
                Term t;
                if (normalizedName == null || !_byName.TryGetValue(normalizedName, out t)) return;
                int n;
                _counts.TryGetValue(normalizedName, out n);
                _counts[normalizedName] = n + 1;
                if (t.Hits.Count < MaxHitsPerTerm) t.Hits.Add(hit);
            }
            catch { }
        }

        /// <summary>
        /// Asks <c>ZNetScene</c> whether each sought name is a registered network prefab at all. Pure
        /// read: <c>GetPrefab(string)</c> is <c>m_namedPrefabs.TryGetValue(name.GetStableHashCode())</c>
        /// and nothing else (verified from IL, 2026-09-23).
        ///
        /// It is what separates the three meanings of a NOT FOUND: a name that is not in ZNetScene
        /// either is probably a typo or a renamed piece; a name that IS in ZNetScene but in no prefab
        /// exists in the build and is simply not placed by world generation - a player-built piece, for
        /// instance.
        /// </summary>
        public void ProbeZNetScene()
        {
            ZNetScene scene = null;
            try { scene = ZNetScene.instance; } catch { }
            if (scene == null) return;

            foreach (Term t in _terms)
            {
                try
                {
                    t.ZNetChecked = true;
                    t.ZNetFound = scene.GetPrefab(t.Normalized) != null;
                }
                catch
                {
                    t.ZNetChecked = false;
                }
            }
        }

        /// <summary>Matches a plain name from one of the tables (a location's or a vegetation entry's
        /// prefab name) rather than a GameObject in a prefab.</summary>
        public void AddTableName(string rawName, string kind, string note)
        {
            if (string.IsNullOrEmpty(rawName)) return;
            string normalized = Names.Normalize(rawName);
            if (!Wants(normalized)) return;
            Add(normalized, new SearchHitDef
            {
                kind = kind,
                hostName = null,
                rawName = rawName,
                gatedByRandomSpawnIndex = -1,
                randomObjectIndex = -1,
                randomObjectEntryIndex = -1,
                note = note,
            });
        }

        /// <summary>The finished file. Also produces the one-line-per-term verdicts, which the caller
        /// prints.</summary>
        public SearchFile Build(string stamp)
        {
            var terms = new List<SearchTermDef>(_terms.Count);
            var lines = new List<string>(_terms.Count);
            var requested = new List<string>(_terms.Count);

            SearchCoverageDef coverage = Coverage();
            bool complete = coverage.notSearched == null || coverage.notSearched.Length == 0;

            foreach (Term t in _terms)
            {
                requested.Add(t.Requested);

                int total;
                _counts.TryGetValue(t.Normalized, out total);
                bool found = total > 0;
                bool inconclusive = !found && !complete;

                string verdict = Verdict(t, total, found, inconclusive, coverage);
                lines.Add(verdict);

                if (found && total > t.Hits.Count)
                {
                    // Truncation must never be silent: every listed hit says how many there really
                    // were, so a reader who sees exactly MaxHitsPerTerm entries cannot mistake the cap
                    // for the count.
                    string cap = " [" + total + " occurrences in all; only the first " +
                                 MaxHitsPerTerm + " are listed.]";
                    foreach (SearchHitDef h in t.Hits)
                    {
                        if (h == null) continue;
                        h.note = h.note == null ? cap.Trim() : h.note + cap;
                    }
                }

                terms.Add(new SearchTermDef
                {
                    requested = t.Requested,
                    normalized = t.Normalized,
                    hash = t.Hash,
                    found = found,
                    inconclusive = inconclusive,
                    verdict = verdict,
                    existsInZNetScene = t.ZNetFound,
                    zNetSceneChecked = t.ZNetChecked,
                    hitCount = total,
                    hits = t.Hits.ToArray(),
                    // A fresh copy per term: the DTO is written per term and a shared instance would
                    // make a reader think the coverage was measured separately for each.
                    coverage = Coverage(),
                });
            }

            return new SearchFile
            {
                stamp = stamp,
                schema = DumpFormat.Schema,
                requestedNames = requested.ToArray(),
                terms = terms.ToArray(),
                verdictLines = lines.ToArray(),
            };
        }

        private string Verdict(Term t, int total, bool found, bool inconclusive, SearchCoverageDef c)
        {
            if (found)
            {
                // The user reads this line off a running game's console before they read any file, so
                // it has to carry the answer itself - which prefab, and for a room, which dungeon
                // family - not just a count and a pointer to a file they have not opened yet.
                var kinds = new List<string>();
                var hosts = new List<string>();
                var seenHost = new HashSet<string>(StringComparer.Ordinal);
                foreach (SearchHitDef h in t.Hits)
                {
                    if (h == null) continue;
                    if (h.kind != null && !kinds.Contains(h.kind)) kinds.Add(h.kind);
                    if (h.hostName == null) continue;
                    string label = h.hostRoomList != null
                        ? h.hostName + " [" + h.hostRoomList + "]"
                        : h.hostName;
                    if (seenHost.Add(label)) hosts.Add(label);
                }

                string where;
                if (hosts.Count == 0)
                {
                    where = "the tables";
                }
                else
                {
                    int shown = hosts.Count < 3 ? hosts.Count : 3;
                    string names = string.Join(", ", hosts.GetRange(0, shown).ToArray());
                    where = hosts.Count + " host prefab(s) - " + names +
                            (hosts.Count > shown ? ", and " + (hosts.Count - shown) + " more" : "");
                }
                return t.Normalized + ": FOUND - " + total + " occurrence(s) in " + where +
                       " (" + string.Join(", ", kinds.ToArray()) + "). See search.json for every host, " +
                       "path and gate.";
            }

            string head = inconclusive
                ? t.Normalized + ": NOT FOUND, but the search was INCONCLUSIVE - "
                : t.Normalized + ": NOT FOUND anywhere this dump can reach - ";

            // LOADED, not walked: a prefab the walk visited but could not load was searched in no
            // sense at all, and quoting the attempted count here is how a search that covered nothing
            // came to sound thorough.
            string scope = "searched " + c.locationPrefabsLoaded + " of " + c.locationPrefabsWalked +
                           " location prefab(s) and " + c.roomPrefabsLoaded + " of " +
                           c.roomPrefabsWalked + " room prefab(s), their children, their root names, " +
                           c.randomObjectOptionsResolved + " RandomObject option prefab(s), the " +
                           c.locationTableEntries + "-entry location table and the " +
                           c.vegetationTableEntries + "-entry vegetation table.";

            string tail;
            if (inconclusive)
            {
                tail = " Do NOT read this as 'the game has no " + t.Normalized + "': " +
                       string.Join(" ", c.notSearched ?? new string[0]) +
                       " Fix that and run the dump again.";
            }
            else if (t.ZNetChecked && t.ZNetFound)
            {
                tail = " ZNetScene DOES have a prefab by this name, so it exists in this build but " +
                       "nothing in world generation places it - a player-built piece behaves exactly " +
                       "like this.";
            }
            else if (t.ZNetChecked)
            {
                tail = " ZNetScene has no prefab by this name either, so the name is probably wrong " +
                       "or the piece was renamed in this build.";
            }
            else
            {
                tail = " ZNetScene was not available to confirm whether a prefab by this name exists " +
                       "at all.";
            }

            return head + scope + tail;
        }

        /// <summary>
        /// What this run actually covered.
        ///
        /// <b>Every line below is decided by a COUNT, never by a "did it run" flag.</b> That is the
        /// whole point of this method. A flag says the walk was entered; only a count says it
        /// contributed anything. The two came apart in exactly the way that matters: a room walk that
        /// reached an empty <c>DungeonDB.GetRooms()</c> falls straight through its loop and still sets
        /// <c>RoomWalkRan = true</c> at the end, and a location walk whose candidate list is empty does
        /// the same - so a run that searched NOTHING published FULL coverage, <c>inconclusive</c>
        /// stayed false, and the user was handed a confident NOT FOUND from a search that never
        /// happened. A source that contributed zero items is listed in <c>notSearched</c> here
        /// whatever its flag says, and a NOT FOUND from an empty walk is loudly inconclusive.
        /// </summary>
        private SearchCoverageDef Coverage()
        {
            var searched = new List<string>();
            var notSearched = new List<string>();

            // ---- location prefabs ----------------------------------------------------------------
            if (LocationChildWalkRan && LocationPrefabsLoaded > 0)
            {
                searched.Add("every child transform of " + LocationPrefabsLoaded + " of " +
                             LocationPrefabsWalked + " location prefab(s), including inactive ones, " +
                             "plus each one's own root name, its RandomObject options and the " +
                             "Container drop tables of both");
            }
            if (!LocationChildWalkRan)
            {
                notSearched.Add("The location prefab child walk did not run (WalkLocationPrefabs / " +
                                "WalkPrefabChildren), so nothing inside a location prefab was searched.");
            }
            else if (LocationPrefabsLoaded == 0)
            {
                notSearched.Add("The location prefab child walk RAN BUT SEARCHED NOTHING: 0 of " +
                                LocationPrefabsWalked + " location prefab(s) were loaded, so no " +
                                "location's contents were searched at all. An empty walk is not " +
                                "coverage - check that ZoneSystem.m_locations is populated and that " +
                                "its entries' prefabs load.");
            }
            if (LocationPrefabsFailed > 0)
            {
                notSearched.Add(LocationPrefabsFailed + " location prefab(s) failed to load and their " +
                                "contents were not searched.");
            }

            // ---- room prefabs ----------------------------------------------------------------------
            if (RoomWalkRan && RoomPrefabsLoaded > 0)
            {
                searched.Add("every child transform of " + RoomPrefabsLoaded + " of " +
                             RoomPrefabsWalked + " dungeon/camp room prefab(s) from DungeonDB, " +
                             "including inactive ones, plus each one's own root name, its " +
                             "RandomObject options and the Container drop tables of both");
            }
            if (!RoomWalkRan)
            {
                notSearched.Add("The room walk did not run" +
                                (RoomWalkSkipped != null ? " (" + RoomWalkSkipped + ")" : "") +
                                ", so nothing inside a dungeon, cave or camp room was searched - which " +
                                "is where most interior pieces live.");
            }
            else if (RoomPrefabsLoaded == 0)
            {
                notSearched.Add("The room walk RAN BUT SEARCHED NOTHING: 0 of " + RoomPrefabsWalked +
                                " room prefab(s) were loaded" +
                                (RoomPrefabsWalked == 0
                                    ? ", because DungeonDB.GetRooms() returned an EMPTY list - " +
                                      "DungeonDB.SetupRooms has most likely not run yet, so load a " +
                                      "world and let it finish loading before dumping"
                                    : ", so no room's contents were searched") +
                                ". Nothing inside any dungeon, cave or camp room was searched, which " +
                                "is where most interior pieces live.");
            }
            if (RoomPrefabsFailed > 0)
            {
                notSearched.Add(RoomPrefabsFailed + " room prefab(s) failed to load and their contents " +
                                "were not searched.");
            }
            if (InteriorWalksWithErrors > 0)
            {
                notSearched.Add(InteriorWalksWithErrors + " prefab(s) loaded but their child walk " +
                                "recorded an error, so part of their subtree was not indexed.");
            }

            // ---- RandomObject weighted options -----------------------------------------------------
            if (RandomObjectOptionsResolved > 0)
            {
                searched.Add(RandomObjectOptionsResolved + " RandomObject weighted option(s) were " +
                             "RESOLVED - the option prefab each one points at was opened and its own " +
                             "children and Containers read, yielding " +
                             RandomObjectOptionContainersRead + " container(s) with their drop tables. " +
                             "An option is a prefab reference rather than a child, so none of this is " +
                             "reachable through any childNames index");
            }
            if (RandomObjectOptionScanErrors > 0)
            {
                notSearched.Add(RandomObjectOptionScanErrors + " RandomObject option scan(s) threw, so " +
                                "part of what those options contain - their chests' drop tables " +
                                "included - was not searched. See the entry's 'scanError'.");
            }
            // ---- the two tables ---------------------------------------------------------------------
            if (LocationTableEntries > 0 || VegetationTableEntries > 0)
            {
                searched.Add("the " + LocationTableEntries + "-entry location table and the " +
                             VegetationTableEntries + "-entry vegetation table, by prefab name");
            }
            if (LocationTableEntries == 0)
            {
                notSearched.Add("The location table contributed NO entries, so a sought name that IS a " +
                                "location was not matched against it. ZoneSystem.m_locations was " +
                                "empty or could not be read.");
            }
            if (VegetationTableEntries == 0)
            {
                notSearched.Add("The vegetation table contributed NO entries, so a sought name that IS " +
                                "a vegetation prop was not matched against it. ZoneSystem.m_vegetation " +
                                "was empty or could not be read.");
            }

            // ---- what no run of this plugin reaches, however well it goes ---------------------------
            var standing = new List<string>
            {
                "Nothing outside the running game's loaded asset set is reachable at all: a prefab " +
                "that is neither a location, a location's child, a DungeonDB room, a room's child, " +
                "a vegetation entry nor a ZNetScene registration cannot be seen by this plugin.",
                "A hit says a piece is AUTHORED inside a prefab. Whether it appears in a given " +
                "world depends on the RandomSpawn draw, the gates, and - for a room - on the " +
                "dungeon layout, which is rolled once per generator and saved to its ZDO.",
            };
            if (NestedRandomObjectsInOptions > 0)
            {
                // A STANDING limitation, deliberately not a notSearched line. RandomObject options are
                // resolved exactly one level deep, so a village whose house options carry their own
                // RandomObjects produces a non-zero count in essentially every run - and a limitation
                // that is present every time does not distinguish one run from another. Putting it in
                // notSearched would mark every verdict in every dump inconclusive and leave the word
                // meaning nothing, which is the same disservice as a confident false NOT FOUND.
                standing.Add(NestedRandomObjectsInOptions + " RandomObject(s) with options of their " +
                             "own sit INSIDE a RandomObject option. Options are resolved ONE level " +
                             "deep, so anything that exists only behind such a nested option - a chest " +
                             "and its drop table included - is not in this dump. Per option entry the " +
                             "count is 'nestedRandomObjectCount'; a non-zero total here does NOT by " +
                             "itself make a verdict inconclusive.");
            }

            return new SearchCoverageDef
            {
                searched = searched.ToArray(),
                notSearched = notSearched.ToArray(),
                standingLimitations = standing.ToArray(),
                locationPrefabsWalked = LocationPrefabsWalked,
                locationPrefabsLoaded = LocationPrefabsLoaded,
                locationPrefabsFailed = LocationPrefabsFailed,
                roomPrefabsWalked = RoomPrefabsWalked,
                roomPrefabsLoaded = RoomPrefabsLoaded,
                roomPrefabsFailed = RoomPrefabsFailed,
                interiorWalksWithErrors = InteriorWalksWithErrors,
                locationTableEntries = LocationTableEntries,
                vegetationTableEntries = VegetationTableEntries,
                randomObjectOptionsResolved = RandomObjectOptionsResolved,
                randomObjectOptionContainersRead = RandomObjectOptionContainersRead,
                randomObjectOptionScanErrors = RandomObjectOptionScanErrors,
                nestedRandomObjectsInOptions = NestedRandomObjectsInOptions,
                // The flags are REPORTED, never used to decide coverage. Kept because "the walk was
                // entered and contributed nothing" and "the walk was never entered" are different
                // facts and a reader wants both; every notSearched line above comes from a count.
                locationChildWalkRan = LocationChildWalkRan,
                roomWalkRan = RoomWalkRan,
                roomWalkSkipped = RoomWalkSkipped,
            };
        }
    }
}
