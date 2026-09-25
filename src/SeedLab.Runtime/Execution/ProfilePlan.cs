using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace SeedLab.Runtime.Execution
{
    /// <summary>One measurement of an earlier profile, as a replay needs it.</summary>
    public sealed class PlannedSection
    {
        public string Tier { get; init; } = "";

        /// <summary>Sampling grid in metres for t2/t3, 0 otherwise.</summary>
        public int Grid { get; init; }

        /// <summary>The location prefix that was ASKED for (t5), 0 otherwise - not the de-duplicated run length.</summary>
        public int PrefixRequested { get; init; }

        public int Workers { get; init; }
        public int Seeds { get; init; }

        /// <summary>The recorded SHA-256 of the section's seed list, or null when the document has none.</summary>
        public string? SeedListSha256 { get; init; }
    }

    /// <summary>
    /// A <c>seedlab-profile/2</c> document read back as a plan: the seed order (key and first index),
    /// the warm-up, and each section's tier, grid or prefix, worker count and seed count, in document
    /// order. Replaying it measures exactly the same seeds, which is what makes a before/after
    /// comparison fair. Only /2 documents are plans: a /1 document records the location prefix as the
    /// run length, from which the asked-for prefix cannot be recovered.
    /// </summary>
    public sealed class ProfilePlan
    {
        public const string Schema = "seedlab-profile/2";

        public ulong Key { get; init; }
        public long From { get; init; }
        public int Warmup { get; init; }
        public IReadOnlyList<PlannedSection> Sections { get; init; } = Array.Empty<PlannedSection>();

        /// <summary>Reads a plan, or throws <see cref="FormatException"/> with a plain-words reason.</summary>
        public static ProfilePlan Parse(string json)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new FormatException("it is not a JSON document (" + ex.Message + ")");
            }

            using (doc)
            {
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new FormatException("it is not a profile document");
                string schema = root.TryGetProperty("schema", out JsonElement s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "";
                if (schema != Schema)
                {
                    throw new FormatException(schema.Length == 0
                        ? "it is not a vseed profile (no \"schema\")"
                        : "it is a " + schema + " document; only " + Schema + " records what a replay needs (the asked-for location prefix and each section's seed list)");
                }

                if (!root.TryGetProperty("run", out JsonElement run) || run.ValueKind != JsonValueKind.Object)
                    throw new FormatException("it has no \"run\" block");
                string keyText = Str(run, "key");
                string k = keyText.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? keyText.Substring(2) : keyText;
                if (!ulong.TryParse(k, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong key))
                    throw new FormatException("run.key '" + keyText + "' is not a hex key");
                long from = Long(run, "from_index");
                int warmup = (int)Long(run, "warmup_per_worker");

                if (!root.TryGetProperty("sections", out JsonElement secs) || secs.ValueKind != JsonValueKind.Array || secs.GetArrayLength() == 0)
                    throw new FormatException("it has no measured sections (an --overhead run is not a plan)");
                List<PlannedSection> list = new List<PlannedSection>();
                int i = 0;
                foreach (JsonElement e in secs.EnumerateArray())
                {
                    string where = "sections[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                    string tier = Str(e, "tier", where);
                    if (tier != "t2" && tier != "t3" && tier != "t4" && tier != "t5") throw new FormatException(where + ".tier '" + tier + "' is not t2, t3, t4 or t5");
                    int grid = e.TryGetProperty("grid_m", out JsonElement g) ? g.GetInt32() : 0;
                    int prefix = e.TryGetProperty("prefix_requested", out JsonElement p) ? p.GetInt32() : 0;
                    if (tier == "t5" && prefix < 1) throw new FormatException(where + " is a t5 section without prefix_requested");
                    if ((tier == "t2" || tier == "t3") && grid < 1) throw new FormatException(where + " is a " + tier + " section without grid_m");
                    int workers = (int)Long(e, "workers", where);
                    int seeds = (int)Long(e, "seeds", where);
                    if (workers < 1 || seeds < 1) throw new FormatException(where + " has no workers or no seeds");
                    list.Add(new PlannedSection
                    {
                        Tier = tier,
                        Grid = tier == "t2" || tier == "t3" ? grid : 0,
                        PrefixRequested = tier == "t5" ? prefix : 0,
                        Workers = workers,
                        Seeds = seeds,
                        SeedListSha256 = e.TryGetProperty("seed_list_sha256", out JsonElement h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null,
                    });
                    i++;
                }

                return new ProfilePlan { Key = key, From = from, Warmup = warmup, Sections = list };
            }
        }

        /// <summary>
        /// Null when the command line agrees with the plan; otherwise a plain-words reason. A value
        /// left off the command line always agrees - the plan's is used.
        /// </summary>
        public string? Conflict(ulong? key, long? from, int? warmup)
        {
            if (key.HasValue && key.Value != Key)
                return "--key 0x" + key.Value.ToString("X16", CultureInfo.InvariantCulture) + " differs from the plan's 0x" + Key.ToString("X16", CultureInfo.InvariantCulture);
            if (from.HasValue && from.Value != From)
                return "--from " + from.Value.ToString(CultureInfo.InvariantCulture) + " differs from the plan's " + From.ToString(CultureInfo.InvariantCulture);
            if (warmup.HasValue && warmup.Value != Warmup)
                return "--warmup " + warmup.Value.ToString(CultureInfo.InvariantCulture) + " differs from the plan's " + Warmup.ToString(CultureInfo.InvariantCulture)
                       + " (the warm-up seeds are part of what is replayed)";
            return null;
        }

        private static string Str(JsonElement e, string name, string where = "run")
        {
            if (!e.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.String) throw new FormatException(where + "." + name + " is missing");
            return v.GetString()!;
        }

        private static long Long(JsonElement e, string name, string where = "run")
        {
            if (!e.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out long x))
                throw new FormatException(where + "." + name + " is missing or not a whole number");
            return x;
        }
    }
}
