using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SeedLab.Search.Criteria;
using SeedLab.Search.Evaluation;

namespace SeedLab.Search.Output
{
    /// <summary>
    /// Turns one <see cref="SeedResult"/> into the exact text a results file holds, for every format.
    ///
    /// <para><b>Why this is its own class.</b> There are now four things that write results - the
    /// streaming <see cref="ResultWriter"/>, the bounded best-N set (<see cref="BoundedResultSet"/>),
    /// the rotating segment writer and the reducer - and a record that differed between them by one
    /// byte would make a bounded run and an unbounded run incomparable. They all format through here,
    /// so they cannot diverge. The code is the code that used to live inside
    /// <see cref="ResultWriter"/>, moved unchanged: the bytes a JSONL/CSV/JSON run writes today are
    /// the bytes it wrote before.</para>
    ///
    /// <para><b>Measured record sizes</b> (disk audit, 2026-09-23, this build): JSONL 1,709.6 B and
    /// CSV 383.0 B for the audit's 11-goal query, with the laws <c>≈54 + 150.5 x goals</c> (JSONL) and
    /// <c>≈75 + 28 x goals</c> (CSV). <see cref="EstimatedBytesPerRecord"/> is those laws, and it is
    /// what the pre-run disk estimate uses - never a guess.</para>
    /// </summary>
    public sealed class RecordFormatter
    {
        private readonly Query _query;
        private readonly string _engine;
        private readonly List<string> _goalIds = new List<string>();

        public RecordFormatter(Query query, CompiledQuery compiled, string engineVersion)
        {
            _query = query;
            _engine = engineVersion;
            foreach (CompiledGoal g in compiled.Goals) _goalIds.Add(g.Goal.Id);
        }

        /// <summary>The goal ids, in the order the CSV columns use them.</summary>
        public IReadOnlyList<string> GoalIds => _goalIds;

        /// <summary>
        /// The bytes one record costs in a given format, from the measured law.
        ///
        /// <para>The law was fitted on the disk audit's eleven-goal query, so it is an EXTRAPOLATION
        /// at other goal counts and it under-reads at small ones: measured here, a one-goal JSONL
        /// record is 320 B where the law predicts 205. It is therefore used only before the run has
        /// written its first record; from that point the estimate divides the real file size by the
        /// real record count and uses that instead (<see cref="Execution.RunEstimator.BytesPerRecord"/>).</para>
        /// </summary>
        public static double EstimatedBytesPerRecord(ResultFormat format, int goals) => format switch
        {
            ResultFormat.Csv => 75.0 + 28.0 * goals,
            ResultFormat.Json => 55.0 + 150.5 * goals,     // the array form adds ",\n  " per record
            _ => 54.0 + 150.5 * goals,
        };

        /// <summary>
        /// The bytes a record costs in the WORST case, by formatting one with this formatter.
        ///
        /// <para><b>Why the fitted law is not enough.</b> The law above was fitted on one eleven-goal
        /// query and is an extrapolation everywhere else, which the comment on it already admitted.
        /// QA measured what that costs: the plan block multiplied the law by <c>keep</c>, printed the
        /// product as "a REAL cap on the file: at most 167 KB whatever the scan finds", and the run
        /// then wrote 398,214 B - 2.33x the stated ceiling. The same number is the free-disk refusal's
        /// threshold, so a refusal that said "it WILL write about 818 GB" was describing 1.47 TB.</para>
        ///
        /// <para>This builds a record with THIS query's goal ids, targets and metrics and the widest
        /// values each field can hold, runs it through the real writer, and returns the length. It is
        /// an upper bound for every record the run can write, with one stated exception: a goal the
        /// build cannot measure carries a free-text <c>unavailable</c> message, which has no bound -
        /// and such a query is refused before it runs unless the goal is a nice-to-have that
        /// <c>--skip-unavailable</c> dropped.</para>
        /// </summary>
        public int WorstCaseBytesPerRecord(ResultFormat format)
        {
            SeedResult r = new SeedResult
            {
                Seed = int.MinValue,
                // Longer than any seed text the inverter emits (the new-world field takes 10), so the
                // bound holds even if that ever grows.
                SeedText = new string('W', 16),
                Score = -1.0 / 3.0,
                Pass = true,
                TierReached = Tier.T5,
                LandAreaM2 = 1.0 / 3.0,
                LargestIslandM2 = 1.0 / 3.0,
                OceanShare = 1.0 / 3.0,
                HighestPeak = 1.0 / 3.0,
            };

            var goals = new List<GoalOutcome>(_goalIds.Count);
            foreach (string id in _goalIds)
            {
                goals.Add(new GoalOutcome
                {
                    Id = id,
                    Target = id,
                    Metric = id,
                    Value = -1.0 / 3.0,
                    Threshold = -1.0 / 3.0,
                    ThresholdMax = -1.0 / 3.0,
                    Weight = 1.0 / 3.0,
                    Score = 1.0 / 3.0,
                    Margin = -1.0 / 3.0,
                    BoundRadius = 1.0 / 3.0,
                    Bounded = true,
                    Pass = true,
                });
            }

            r.Goals = goals;

            try
            {
                int n = System.Text.Encoding.UTF8.GetByteCount(Text(format, r));
                // The JSON array form adds a separator per record that Text() does not carry.
                return format == ResultFormat.Json ? n + 4 : n;
            }
            catch (Exception)
            {
                // A bound that cannot be computed must not silently become a smaller one.
                return (int)Math.Ceiling(EstimatedBytesPerRecord(format, _goalIds.Count) * 2.5);
            }
        }

        /// <summary>The CSV header line, including its newline. Empty for the other formats.</summary>
        public string CsvHeader()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("seed,seed_text,score,pass,tier,defs,grid_m,approx");
            foreach (string id in _goalIds)
            {
                string c = Csv(id);
                sb.Append(',').Append(c).Append("_value");
                sb.Append(',').Append(c).Append("_pass");
                sb.Append(',').Append(c).Append("_score");
                sb.Append(',').Append(c).Append("_bounded");
            }

            sb.Append(",land_km2,largest_island_km2,ocean_share,highest_peak_m\n");
            return sb.ToString();
        }

        /// <summary>
        /// The text this record contributes, for the line-oriented formats: a JSONL line including its
        /// newline, or a CSV row including its newline. For <see cref="ResultFormat.Json"/> it is the
        /// bare object, which the array writer wraps in its own separators.
        /// </summary>
        public string Text(ResultFormat format, SeedResult r) => format switch
        {
            ResultFormat.Csv => CsvLine(r),
            ResultFormat.Json => Record(r),
            _ => Record(r) + "\n",
        };

        public string CsvLine(SeedResult r)
        {
            StringBuilder sb = new StringBuilder(256);
            sb.Append(r.Seed.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(Csv(r.SeedText ?? "")).Append(',');
            sb.Append(N(r.Score)).Append(',');
            sb.Append(r.Pass ? "true" : "false").Append(',');
            sb.Append(r.TierReached.ToString().ToLowerInvariant()).Append(',');
            sb.Append(_query.Defs).Append(',');
            sb.Append(N(_query.Search.Grid)).Append(',');
            sb.Append(_query.Search.Approx ? "true" : "false");

            Dictionary<string, GoalOutcome> byId = new Dictionary<string, GoalOutcome>(StringComparer.Ordinal);
            foreach (GoalOutcome g in r.Goals) byId[g.Id] = g;
            foreach (string id in _goalIds)
            {
                byId.TryGetValue(id, out GoalOutcome g);
                sb.Append(',').Append(N(g.Value));
                sb.Append(',').Append(g.Pass ? "true" : "false");
                sb.Append(',').Append(N(g.Score));
                sb.Append(',').Append(g.Bounded ? "true" : "false");
            }

            sb.Append(',').Append(r.LandAreaM2.HasValue ? N(r.LandAreaM2.Value / 1e6) : "");
            sb.Append(',').Append(r.LargestIslandM2.HasValue ? N(r.LargestIslandM2.Value / 1e6) : "");
            sb.Append(',').Append(r.OceanShare.HasValue ? N(r.OceanShare.Value) : "");
            sb.Append(',').Append(r.HighestPeak.HasValue ? N(r.HighestPeak.Value) : "");
            sb.Append('\n');
            return sb.ToString();
        }

        /// <summary>One result as a single-line JSON object. The same shape in JSONL and in the array form.</summary>
        public string Record(SeedResult r)
        {
            StringBuilder sb = new StringBuilder(512);
            sb.Append("{\"seed\":").Append(r.Seed.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"text\":").Append(Str(r.SeedText));
            sb.Append(",\"score\":").Append(N(r.Score));
            sb.Append(",\"pass\":").Append(r.Pass ? "true" : "false");
            sb.Append(",\"tier\":\"").Append(r.TierReached.ToString().ToLowerInvariant()).Append('"');
            sb.Append(",\"defs\":").Append(_query.Defs);
            sb.Append(",\"grid\":").Append(N(_query.Search.Grid));
            sb.Append(",\"engine\":\"").Append(_engine).Append('"');
            sb.Append(",\"approx\":").Append(_query.Search.Approx ? "true" : "false");
            sb.Append(",\"goals\":{");
            bool first = true;
            foreach (GoalOutcome g in r.Goals)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(Str(g.Id)).Append(":{");
                sb.Append("\"value\":").Append(N(g.Value));
                sb.Append(",\"unit\":\"").Append(UnitName(g.Unit)).Append('"');
                sb.Append(",\"test\":\"").Append(g.Test.ToString().ToLowerInvariant()).Append('"');
                sb.Append(",\"threshold\":").Append(N(g.Threshold));
                sb.Append(",\"pass\":").Append(g.Pass ? "true" : "false");
                sb.Append(",\"importance\":\"").Append(g.Importance.ToString().ToLowerInvariant()).Append('"');
                if (g.Importance == Importance.Nice)
                {
                    sb.Append(",\"s\":").Append(N(g.Score));
                    sb.Append(",\"weight\":").Append(N(g.Weight));
                }

                if (g.Bounded)
                {
                    sb.Append(",\"bounded\":true,\"bound_radius\":").Append(N(g.BoundRadius));
                }

                // Presentation, per goal rather than per record: a screen-then-verify run decides
                // different goals at different grids, and a metric that is not comparable across
                // grids has to say so where the number is, not in a header nobody reads.
                if (g.MeasuredAtGrid > 0) sb.Append(",\"measured_at_grid_m\":").Append(N(g.MeasuredAtGrid));
                if (!g.GridComparable)
                {
                    sb.Append(",\"grid_comparable\":false");
                    if (g.GridNote != null) sb.Append(",\"grid_note\":").Append(Str(g.GridNote));
                }

                if (g.Censored != null) sb.Append(",\"censored\":").Append(Str(g.Censored));
                if (double.IsFinite(g.MeasuredRelError) && g.MeasuredRelError > 0)
                {
                    sb.Append(",\"measured_median_rel_err_at_grid\":").Append(N(g.MeasuredRelError));
                    if (g.ErrorSource != null) sb.Append(",\"error_source\":").Append(Str(g.ErrorSource));
                }

                if (g.Unavailable != null) sb.Append(",\"unavailable\":").Append(Str(g.Unavailable));

                // A goal about a m_unique location type carries what its number actually claims about
                // the candidate set. A results file is read long after the search, often by someone
                // who did not write the query, and a bare "2464.1" next to "Vendor_BlackForest" would
                // read as "Haldor is 2,464 m away" - which the seed does not decide.
                if (g.UniqueSemantics != null) sb.Append(",\"unique_semantics\":").Append(Str(g.UniqueSemantics));

                // Same reasoning one field up, for the other kind of "this coordinate is not a
                // promise": a goal about the axe-head houses writes twenty positions the seed really
                // does decide, and nothing in the record would otherwise say that what is inside them
                // is drawn from the ambient stream at zone load and is not a function of the seed.
                if (g.ContentsNote != null) sb.Append(",\"contents_note\":").Append(Str(g.ContentsNote));
                sb.Append('}');
            }

            sb.Append('}');

            sb.Append(",\"metrics\":{");
            bool f2 = true;
            Metric(sb, ref f2, "land_km2", r.LandAreaM2.HasValue ? r.LandAreaM2.Value / 1e6 : (double?)null);
            Metric(sb, ref f2, "largest_island_km2", r.LargestIslandM2.HasValue ? r.LargestIslandM2.Value / 1e6 : (double?)null);
            Metric(sb, ref f2, "ocean_share", r.OceanShare);
            Metric(sb, ref f2, "highest_peak_m", r.HighestPeak);
            // These four are measured inside the query's region of interest, which the prefilter may
            // shrink below the 10,500 m water edge. Say so on the record instead of shipping a
            // whole-world name for a disc-limited number.
            if (r.RegionRadiusM < SeedLab.Search.Metrics.SeedSampler.WaterEdge)
            {
                Metric(sb, ref f2, "region_m", r.RegionRadiusM);
            }

            // Screen-then-verify: the record says which grid actually decided it. A survivor of a
            // coarse screen that was re-measured at the game's own grid is an exact result and must
            // not be confused with a coarse one, and a record that does not say so is a record that
            // will be compared with the wrong thing in a month.
            if (r.ScreenedAtGrid > 0)
            {
                Metric(sb, ref f2, "screened_at_grid_m", r.ScreenedAtGrid);
                Metric(sb, ref f2, "verified_at_grid_m", r.VerifiedAtGrid);
            }

            sb.Append('}');
            sb.Append('}');
            return sb.ToString();
        }

        private static void Metric(StringBuilder sb, ref bool first, string name, double? v)
        {
            if (!v.HasValue) return;
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(name).Append("\":").Append(N(v.Value));
        }

        public static string UnitName(Unit u) => u switch
        {
            Unit.Metres => "m",
            Unit.SquareMetres => "m2",
            Unit.Fraction => "fraction",
            _ => "count",
        };

        public static string N(double v)
        {
            if (double.IsNaN(v)) return "null";
            if (double.IsPositiveInfinity(v)) return "1e999";
            if (double.IsNegativeInfinity(v)) return "-1e999";
            return v.ToString("R", CultureInfo.InvariantCulture);
        }

        public static string Str(string? s)
            => s == null ? "null" : System.Text.Json.JsonSerializer.Serialize(s);

        public static string Csv(string s)
            => s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0 ? s : "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
