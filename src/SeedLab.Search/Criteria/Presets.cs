using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace SeedLab.Search.Criteria
{
    /// <summary>
    /// The shipped queries. They are plain query files embedded in the assembly, so
    /// <c>vseed presets show &lt;name&gt; &gt; mine.json</c> gives the user a real, editable starting
    /// point rather than a black box - which is the point of having a criteria language at all.
    ///
    /// <para>Each preset says in its own comments whether it works today or needs the dumped location
    /// table, and the loader repeats that in <c>vseed presets list</c>, because a preset that silently
    /// returns nothing is worse than one that refuses.</para>
    ///
    /// <para><b>The thresholds are starting points, not measurements.</b> 07-features.md section 5.3
    /// asks for them to be calibrated so that roughly 0.1-2 % of seeds pass; where this build has
    /// measured a pass rate it is quoted in <see cref="Describe"/>, and where it has not, the preset
    /// says so.</para>
    /// </summary>
    public static class Presets
    {
        private const string Prefix = "SeedLab.Search.Presets.";

        public static IReadOnlyList<string> Names
        {
            get
            {
                List<string> names = new List<string>();
                foreach (string r in typeof(Presets).Assembly.GetManifestResourceNames())
                {
                    if (r.StartsWith(Prefix, StringComparison.Ordinal) && r.EndsWith(".json", StringComparison.Ordinal))
                    {
                        names.Add(r.Substring(Prefix.Length, r.Length - Prefix.Length - 5));
                    }
                }

                names.Sort(StringComparer.Ordinal);
                return names;
            }
        }

        public static bool Exists(string name) => Read(name) != null;

        /// <summary>The preset's source text, exactly as shipped - comments and all.</summary>
        public static string? Read(string name)
        {
            using Stream? s = typeof(Presets).Assembly.GetManifestResourceStream(Prefix + name + ".json");
            if (s == null) return null;
            using StreamReader r = new StreamReader(s, Encoding.UTF8);
            return r.ReadToEnd();
        }

        public static Query Load(string name)
        {
            string? text = Read(name)
                           ?? throw new QueryException("", "no preset called '" + name + "'",
                                  "try: " + string.Join(", ", Names));
            return QueryReader.Parse(text, "preset:" + name);
        }

        /// <summary>The documentation file printed by <c>vseed search --schema</c>.</summary>
        public static string Schema()
        {
            using Stream? s = typeof(Presets).Assembly.GetManifestResourceStream("SeedLab.Search.Criteria.schema.md");
            if (s == null) return "(the schema document was not embedded in this build)";
            using StreamReader r = new StreamReader(s, Encoding.UTF8);
            return r.ReadToEnd();
        }

        /// <summary>One line per preset: its description and whether it can run in this build.</summary>
        public static (string Description, bool NeedsLocations) Describe(string name)
        {
            Query q = Load(name);
            bool needs = false;
            foreach (Goal g in q.Goals)
            {
                if (g.Target.Kind == TargetKind.Location || g.Target.Kind == TargetKind.Group) needs = true;
                if (g.From == DistanceOrigin.Spawn) needs = true;
            }

            return (q.Description ?? "", needs);
        }
    }
}
