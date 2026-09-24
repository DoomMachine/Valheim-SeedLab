using System;
using System.Collections.Generic;
using SeedLab.Contracts.Dump;
using SeedLab.Data;
using SeedLab.Locations;

namespace SeedLab.Cli.Analysis
{
    /// <summary>What kind of place a location prefab is, for the <c>--type</c> filter.</summary>
    [Flags]
    public enum LocationKind
    {
        None = 0,

        /// <summary>A boss altar. Curated - see <see cref="LocationCatalog.BossAltars"/>.</summary>
        Boss = 1,

        /// <summary>An NPC trader camp. Curated - see <see cref="LocationCatalog.Traders"/>.</summary>
        Trader = 2,

        /// <summary>
        /// The location prefab carries at least one <c>DungeonGenerator</c>. Read out of
        /// <c>locationprefabs.json</c>, not curated.
        /// </summary>
        Dungeon = 4,

        /// <summary><c>ZoneLocation.m_unique</c>: the game keeps ONE instance and deletes the rest.</summary>
        Unique = 8,
    }

    /// <summary>
    /// The shipped location table plus the little that classifies it, kept in one place so
    /// <c>vseed seed</c> and <c>vseed locations</c> cannot disagree.
    ///
    /// <para><b>Where each classification comes from.</b> "Dungeon" is derived from the dumped
    /// <c>locationprefabs.json</c>: a prefab with at least one <c>DungeonGenerator</c> component. "Boss"
    /// and "trader" are NOT in the dumped data - nothing on <c>ZoneLocation</c> or <c>Location</c> marks
    /// an altar or a vendor - so they are the two curated lists below, checked against the loaded table
    /// at startup: a name that is no longer in the table is reported rather than silently dropped.</para>
    /// </summary>
    public sealed class LocationCatalog
    {
        /// <summary>
        /// The boss altars of Valheim 1.0.15, by <c>m_prefab.Name</c>. CURATED, not derived - see the
        /// class remarks. Every one of these is <c>m_prioritized</c> and has
        /// <c>m_quantity</c> between 3 and 20, so a world holds SEVERAL altars per boss, not one.
        /// </summary>
        public static readonly string[] BossAltars =
        {
            "Eikthyrnir",                      // Eikthyr, Meadows
            "GDKing",                          // The Elder, Black Forest
            "Bonemass",                        // Bonemass, Swamp
            "Dragonqueen",                     // Moder, Mountain
            "GoblinKing",                      // Yagluth, Plains
            "Mistlands_DvergrBossEntrance1",   // The Queen, Mistlands (an entrance, with a dungeon)
            "FaderLocation",                   // Fader, Ash Lands
            "DN_Bossroom",                     // Deep North boss room
        };

        /// <summary>The NPC trader camps. CURATED. All three are <c>m_unique</c>.</summary>
        public static readonly string[] Traders =
        {
            "Vendor_BlackForest",  // Haldor
            "Hildir_camp",         // Hildir
            "BogWitch_Camp",       // the Bog Witch
        };

        private readonly Dictionary<string, LocationKind> _kind = new Dictionary<string, LocationKind>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _generators = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _label = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly List<LocationDisplayNote> _notes = new List<LocationDisplayNote>();

        private LocationCatalog(GameData data, LocationTable table, IReadOnlyList<AltBiomeRuntime> altBiomes)
        {
            Data = data;
            Table = table;
            AltBiomes = altBiomes;

            foreach (LocationPrefabDef p in data.LocationPrefabs)
            {
                if (p.prefabName == null) continue;
                int g = p.generators?.Length ?? 0;
                _generators[p.prefabName] = g;
                if (g > 0) Or(p.prefabName, LocationKind.Dungeon);
                if (!string.IsNullOrEmpty(p.discoverLabel)) _label[p.prefabName] = p.discoverLabel!;
            }

            foreach (ZoneLocationEntry e in table.Ordered)
                if (e.Unique) Or(e.PrefabName, LocationKind.Unique);

            Mark(BossAltars, LocationKind.Boss, "boss altar");
            Mark(Traders, LocationKind.Trader, "trader camp");

            // ---- the curated lists, checked against the derived ones ------------------------------
            //
            // The two arrays above are hand-written; Names derives the same two sets from the dump, by
            // the OfferingBowl and Trader components themselves. While both exist they must agree, and
            // the assertion is here rather than in SeedLab.Data because that assembly cannot see this
            // file. A difference is a NOTE, not a throw: the placements are still right, and refusing
            // to answer "where are the bosses" because a name rule stopped matching would be a worse
            // failure than saying so and carrying on. When the arrays are retired a release from now,
            // this block and they go together.
            Compare(BossAltars, Names.BossAltarPrefabs, "boss altar");
            Compare(Traders, Names.TraderPrefabs, "trader camp");

            // Everything the derivation itself wants said - the Bog Witch's hand-made join, a rule
            // that matched nothing, a token with no entry. It is deliberately permanent, not a
            // one-off startup check: the caller prints these beside the answer, so the provenance
            // travels with the names rather than being recorded once and forgotten.
            foreach (LocationDisplayNote note in Names.NoteDetails) _notes.Add(note);
        }

        public GameData Data { get; }

        /// <summary>
        /// The derived prefab-to-player-facing-name table: what to CALL each place, where the name
        /// came from, and everything else it answers to.
        ///
        /// <para>It is a passthrough to <see cref="GameData.DisplayNames"/>, which builds once per
        /// process (about 177 ms cold on this machine, 2026-09-24: 4 ms for <c>localization.json</c>,
        /// 170 ms for the occupant slice of <c>locationchildren.json</c> and 3 ms for the derivation
        /// itself; 0 ms on every touch after the first). The 71 MB <c>roomchildren.json</c> is NOT on
        /// this path - see <c>LocationDisplayNames</c> for the join that keeps it off.</para>
        /// </summary>
        public LocationDisplayNames Names => Data.DisplayNames;

        public LocationTable Table { get; }

        public IReadOnlyList<AltBiomeRuntime> AltBiomes { get; }

        /// <summary>
        /// Anything the caller should print: a curated name that the table no longer has, plus
        /// everything the name derivation wants said. Unchanged in content and order - every existing
        /// consumer (<c>vseed seed</c>, <c>vseed serve</c>, <c>vseed data</c>) gets exactly what it got
        /// before.
        /// </summary>
        public IReadOnlyList<string> Notes
        {
            get
            {
                List<string> flat = new List<string>(_notes.Count);
                foreach (LocationDisplayNote note in _notes) flat.Add(note.Text);
                return flat;
            }
        }

        /// <summary>
        /// The table-wide notes, plus the per-prefab notes for the places in <paramref name="prefabs"/>.
        ///
        /// <para>For a command that shows one SELECTION rather than the whole table. Printing every
        /// note on every run is how the channel that carries the <c>m_unique</c> caveats stops being
        /// read - see <see cref="LocationDisplayNames.NotesFor"/>. Nothing is suppressed: a note about
        /// a place appears whenever that place does.</para>
        ///
        /// <para><paramref name="includeTableWide"/> exists so a caller can print the table-wide notes
        /// EARLY - before it knows the selection, and therefore before any refusal - and the per-prefab
        /// ones later without repeating them. "the localization table could not be read" has to reach a
        /// user whose <c>--name</c> then fails to resolve, or the failure looks arbitrary.</para>
        ///
        /// </summary>
        public IReadOnlyList<string> NotesFor(IEnumerable<string>? prefabs, bool includeTableWide = true)
        {
            HashSet<string> want = new HashSet<string>(StringComparer.Ordinal);
            if (prefabs != null)
            {
                foreach (string p in prefabs) want.Add(p);
            }

            List<string> outp = new List<string>();
            foreach (LocationDisplayNote note in _notes)
            {
                bool wanted = note.Prefab == null ? includeTableWide : want.Contains(note.Prefab);
                if (wanted) outp.Add(note.Text);
            }
            return outp;
        }

        public static LocationCatalog Load()
        {
            GameData data = GameData.Load();
            data.RequireUsableForAssetData("where the locations of a seed are");
            LocationTable table = LocationTable.FromDump(data.Locations);
            List<AltBiomeRuntime> alts = new List<AltBiomeRuntime>(data.AltBiomes.Count);
            foreach (AltBiomeDef d in data.AltBiomes) alts.Add(AltBiomeRuntime.FromDump(d));
            return new LocationCatalog(data, table, alts);
        }

        private void Or(string prefab, LocationKind k)
        {
            _kind.TryGetValue(prefab, out LocationKind cur);
            _kind[prefab] = cur | k;
        }

        private void Mark(string[] names, LocationKind k, string what)
        {
            foreach (string n in names)
            {
                if (Table.OrderedIndexOf(n) < 0)
                {
                    _notes.Add(new LocationDisplayNote(n,
                        "the curated " + what + " '" + n + "' is not in this build's placement list "
                        + "(missing, disabled, or m_quantity 0), so it is not reported"));
                    continue;
                }

                Or(n, k);
            }
        }

        /// <summary>
        /// Set-compares a curated list against the one the dump derives, in both directions, and
        /// records each difference by name. Order is not compared: the arrays are written in the game's
        /// progression order and the derivation emits the occupant file's order, and neither is a fact
        /// about the world.
        /// </summary>
        private void Compare(string[] curated, IReadOnlyList<string> derived, string what)
        {
            HashSet<string> c = new HashSet<string>(curated, StringComparer.Ordinal);
            HashSet<string> d = new HashSet<string>(derived, StringComparer.Ordinal);

            foreach (string n in curated)
            {
                if (d.Contains(n)) continue;
                _notes.Add(new LocationDisplayNote(n,
                    "the curated " + what + " '" + n + "' is NOT one the dump's own components "
                    + "name, so the two lists have drifted apart - the curated list is what this "
                    + "command's --type filter uses, and the derived one is what the names come "
                    + "from"));
            }

            foreach (string n in derived)
            {
                if (c.Contains(n)) continue;
                _notes.Add(new LocationDisplayNote(n,
                    "'" + n + "' IS named as a " + what + " by the dump's own components but is "
                    + "not in the curated list, so --type will not select it"));
            }
        }

        public LocationKind KindOf(string prefab) => _kind.TryGetValue(prefab, out LocationKind k) ? k : LocationKind.None;

        /// <summary>
        /// What a player calls this place, or null when the dump does not name it. Never the prefab
        /// spelled out as if it were a name - <c>Bonemass</c> is a real name that happens to equal its
        /// prefab, and only a genuine null tells the two apart.
        /// </summary>
        public string? DisplayNameOf(string prefab) => Names.For(prefab).DisplayName;

        public int GeneratorsOf(string prefab) => _generators.TryGetValue(prefab, out int g) ? g : 0;

        public string? LabelOf(string prefab) => _label.TryGetValue(prefab, out string? l) ? l : null;

        /// <summary>The ordered entries matching a kind mask; an empty mask means all of them.</summary>
        public List<ZoneLocationEntry> Select(LocationKind mask)
        {
            List<ZoneLocationEntry> outp = new List<ZoneLocationEntry>();
            foreach (ZoneLocationEntry e in Table.Ordered)
                if (mask == LocationKind.None || (KindOf(e.PrefabName) & mask) != 0) outp.Add(e);
            return outp;
        }

        /// <summary>
        /// How much of the ordered list has to run for every one of <paramref name="wanted"/> to be
        /// right: the largest ordered index plus one. Nothing shorter is safe - a type's own RNG stream
        /// is independent, but every earlier type can take its zones, its AssetID / group buckets and
        /// its <c>CountNrOfLocation</c> start value (see <see cref="LocationTable.PrefixLengthForTarget"/>).
        /// </summary>
        public int PrefixFor(IEnumerable<ZoneLocationEntry> wanted)
        {
            int last = -1;
            foreach (ZoneLocationEntry e in wanted)
            {
                int i = Table.OrderedIndexOf(e.PrefabName);
                if (i > last) last = i;
            }

            return last + 1;
        }

        public static LocationKind ParseKind(string s) => s switch
        {
            "boss" or "bosses" => LocationKind.Boss,
            "trader" or "traders" => LocationKind.Trader,
            "dungeon" or "dungeons" => LocationKind.Dungeon,
            "unique" => LocationKind.Unique,
            "all" => LocationKind.None,
            _ => throw new ArgumentException(
                "unknown --type '" + s + "'. Use boss, trader, dungeon, unique or all."),
        };
    }
}
