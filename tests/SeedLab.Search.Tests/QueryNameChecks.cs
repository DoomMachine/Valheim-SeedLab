using System;
using System.Collections.Generic;
using System.Text.Json;
using SeedLab.Search.Criteria;
using SeedLab.Search.Evaluation;
using SeedLab.Search.Execution;
using SeedLab.Search.Locations;

namespace SeedLab.SearchTests
{
    /// <summary>
    /// Names go IN, prefabs come OUT - and nothing about a query that was already spelled in prefabs
    /// changes by one byte.
    ///
    /// <para><b>What is actually at risk.</b> A run's identity is the SHA-256 of its canonical JSON
    /// (<see cref="QueryReader.Hash"/>), and that identity is what a checkpoint's <c>MustMatch</c>, a
    /// resume and a results-file comparison all rest on. Accepting <c>location:"The Elder"</c> means
    /// rewriting a goal's target, and a rewrite that ran at the wrong moment - or that changed the
    /// canonical form of a query nobody rewrote - would silently make every checkpoint and every
    /// results file written before this feature incompatible with the ones written after it. Nothing
    /// would throw; the run would just start again from zero and the user would never know why.</para>
    ///
    /// <para>So the two properties below are asserted directly rather than inferred: the rewrite is
    /// IDEMPOTENT (a prefab resolves to itself, so a second pass is a no-op), and a prefab-spelled
    /// query's canonical JSON is byte-identical to what the previous build produced.</para>
    /// </summary>
    public static class QueryNameChecks
    {
        private const string Engine = "tests";

        public static void Run(Action<bool, string, string> check)
        {
            ILocationOracle oracle = SeedLab.LocationOracle.DumpedLocationOracle.Create(out string? problem);
            if (!oracle.Available)
            {
                check(false, "the dumped location table is available for the name checks", problem ?? "");
                return;
            }

            CanonicalFormIsUnchanged(check, oracle);
            RewriteIsIdempotent(check, oracle);
            NameAndPrefabHashTheSame(check, oracle);
            ContentsNoteReachesBothSpellings(check, oracle);
            SuggestOffersDisplayNames(check, oracle);
        }

        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// Every target string this build can produce must serialise to itself in quotes.
        ///
        /// <para>Canonicalise now writes the target through <c>JsonSerializer.Serialize</c> instead of
        /// concatenating it, because a typed name can carry an apostrophe or a quote and a raw append
        /// produced canonical JSON no reader could parse. System.Text.Json's default encoder escapes
        /// <c>' + &lt; &gt; &amp;</c> and every non-ASCII character, so the change is only free if no
        /// name a query can hold contains one of those - which is what this measures, over the real
        /// table rather than over a hand-picked example.</para>
        /// </summary>
        private static void CanonicalFormIsUnchanged(Action<bool, string, string> check, ILocationOracle oracle)
        {
            List<string> targets = new List<string>();
            foreach (string p in oracle.PrefabNames) targets.Add("location:" + p);
            foreach (string g in LocationGroups.Names) targets.Add("group:" + g);
            foreach (string b in MetricCatalog.BiomeNames) targets.Add("biome:" + b);
            foreach (string m in MetricCatalog.NamesFor(TargetKind.World)) targets.Add("world:" + m);

            List<string> escaped = new List<string>();
            foreach (string t in targets)
            {
                if (JsonSerializer.Serialize(t) != "\"" + t + "\"") escaped.Add(t);
            }

            check(escaped.Count == 0,
                  "every prefab, group, biome and world target serialises to itself, so no old query's hash moved",
                  targets.Count + " targets checked, " + escaped.Count + " would change"
                  + (escaped.Count > 0 ? ": " + string.Join(", ", escaped.ToArray()) : ""));

            // The end-to-end version of the same claim: a query written in prefabs canonicalises to
            // exactly the string a re-parse of it canonicalises to, after a session has had its hands
            // on it. This is the byte-identity a checkpoint compares.
            Query parsed = Parse("location:GDKing");
            string before = parsed.CanonicalJson;
            SearchSession s = Session(parsed, oracle);
            check(s.Query.CanonicalJson == before && s.QueryHash == QueryReader.Hash(Parse("location:GDKing")),
                  "a prefab-spelled query's canonical JSON and run hash are untouched by the name pass",
                  s.QueryHash.Substring(0, 16) + (s.Query.CanonicalJson == before ? " (canonical form identical)" : " CANONICAL FORM CHANGED"));

            check(s.NameNotes.Count == 0,
                  "a prefab-spelled query produces no substitution note",
                  s.NameNotes.Count == 0 ? "none" : string.Join(" | ", s.NameNotes.ToArray()));
        }

        /// <summary>
        /// Running the rewrite twice changes nothing the second time. <c>SearchSession.Create</c>
        /// re-enters itself on a grid raise, so this is not a theoretical property.
        /// </summary>
        private static void RewriteIsIdempotent(Action<bool, string, string> check, ILocationOracle oracle)
        {
            Query q = Parse("location:The Elder");

            SearchSession first = Session(q, oracle);
            string hash1 = first.QueryHash;
            string canonical1 = q.CanonicalJson;
            int notes1 = first.NameNotes.Count;

            // The SAME query object, already rewritten in place, back through the same code path.
            SearchSession second = Session(q, oracle);

            check(second.QueryHash == hash1 && q.CanonicalJson == canonical1,
                  "the target rewrite is idempotent: a second pass moves neither the canonical form nor the hash",
                  hash1.Substring(0, 16) + " -> " + second.QueryHash.Substring(0, 16));

            check(notes1 == 1 && second.NameNotes.Count == 0,
                  "the substitution is announced once, on the pass that made it",
                  "first pass " + notes1 + " note(s), second pass " + second.NameNotes.Count
                  + (notes1 > 0 ? ": " + first.NameNotes[0] : ""));

            check(q.Goals[0].Target.Name == "GDKing",
                  "the rewritten goal holds the prefab, which is what every record and column header will say",
                  q.Goals[0].Target.ToString());
        }

        /// <summary>
        /// The point of doing the rewrite before <c>Compile</c> and <c>Hash</c>: the two spellings are
        /// ONE run. A user who wrote "The Elder" and a user who wrote GDKing share a checkpoint, a
        /// results file and a resume.
        /// </summary>
        private static void NameAndPrefabHashTheSame(Action<bool, string, string> check, ILocationOracle oracle)
        {
            string byName = Session(Parse("location:The Elder"), oracle).QueryHash;
            string byPrefab = Session(Parse("location:GDKing"), oracle).QueryHash;
            check(byName == byPrefab,
                  "'The Elder' and 'GDKing' are the same run: same canonical form, same hash, same checkpoint",
                  byName.Substring(0, 16) + " vs " + byPrefab.Substring(0, 16));

            // Input tolerance, spelled out: case, spacing and a leading "the" are all dropped on the
            // way in, and they all arrive at the same run.
            string folded = Session(Parse("location:elder"), oracle).QueryHash;
            check(folded == byPrefab, "'elder' reaches the same run as 'GDKing'",
                  folded.Substring(0, 16));

            // ... and a name that is not one is still refused, with the prefab error, not silently
            // turned into something near it.
            bool refused = false;
            try { Session(Parse("location:The Elder Scrolls"), oracle); }
            catch (QueryException) { refused = true; }

            check(refused, "a string that is neither prefab nor name is still refused", "");
        }

        /// <summary>
        /// Complaint 5's caveat has to reach the query that names the prefab directly, not only the one
        /// that names the group - the axe-head houses are not <c>m_unique</c>, so before this there was
        /// no channel that carried it at all.
        /// </summary>
        private static void ContentsNoteReachesBothSpellings(Action<bool, string, string> check, ILocationOracle oracle)
        {
            CompiledGoal bare = CompiledQuery.Compile(Parse("location:WoodHouse6"), oracle).Goals[0];
            CompiledGoal grouped = CompiledQuery.Compile(Parse("group:axe_head_houses"), oracle).Goals[0];
            CompiledGoal other = CompiledQuery.Compile(Parse("location:GDKing"), oracle).Goals[0];

            check(bare.ContentsNote != null && bare.ContentsNote == grouped.ContentsNote,
                  "'location:WoodHouse6' carries the same contents caveat 'group:axe_head_houses' does",
                  bare.ContentsNote == null ? "(none on the bare location)"
                      : bare.ContentsNote.Substring(0, Math.Min(60, bare.ContentsNote.Length)) + "...");

            check(other.ContentsNote == null, "a goal with no world feature carries no contents caveat",
                  other.ContentsNote ?? "(none)");

            // The two uncertainties are kept apart in the shipped wording, and that is the whole
            // honesty claim of complaint 5: one of them the seed decides and one of it never will.
            string note = bare.ContentsNote ?? "";
            check(note.Contains("31/56") && note.Contains("31/112") && note.Contains("AMBIENT")
                  && !note.Contains("55%"),
                  "the axe-head note keeps the seed-determined coin and the unseeded draw separate",
                  note.Length + " chars");
        }

        /// <summary>
        /// "Did you mean" used to scan prefabs only, so a near miss on the name a player actually uses
        /// produced no suggestion at all - the very case the message exists for.
        /// </summary>
        private static void SuggestOffersDisplayNames(Action<bool, string, string> check, ILocationOracle oracle)
        {
            // "Eld" is a fragment of "The Elder" and of no prefab, so it does not resolve - and under
            // the old prefab-only scan it produced the bare fallback sentence and nothing else, which
            // is the defect. It is deliberately a SUBSTRING miss rather than a typo: Suggest matches
            // by containment, not by edit distance, and a test that assumed otherwise would be
            // asserting a feature this build does not have.
            string hint = "";
            try { CompiledQuery.Compile(Parse("location:Eld"), oracle); }
            catch (QueryException ex) { hint = ex.Hint ?? ""; }

            check(hint.Contains("GDKing") && hint.Contains("The Elder"),
                  "'did you mean' offers the prefab together with the name a player knows",
                  hint);
        }

        // -----------------------------------------------------------------------------------------

        private static Query Parse(string target)
            => QueryReader.Parse(
                "{\"version\":1,\"goals\":[{\"id\":\"g\",\"target\":" + JsonSerializer.Serialize(target)
                + ",\"metric\":\"count\",\"test\":\"at_least\",\"value\":1,\"importance\":\"must\"}]}",
                "name-checks");

        /// <summary>
        /// A session with no output path and no scan: <c>Create</c> is the method that owns the
        /// rewrite, so the checks go through it rather than through a private helper they would then
        /// be testing instead of the real path. Nothing is written and no seed is touched.
        /// </summary>
        private static SearchSession Session(Query q, ILocationOracle oracle)
            => SearchSession.Create(q, oracle, Engine, 1, 1, outPath: null, noPrefilter: true,
                                    acceptScanOrder: true);
    }
}
