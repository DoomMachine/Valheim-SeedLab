using System;
using System.Collections.Generic;
using SeedLab.Seeds;

namespace SeedLab.LocationLab
{
    /// <summary>
    /// Validates the one piece of the location machinery that ground truth already covers without the
    /// asset table: the per-location RNG stream seed.
    ///
    /// <para><c>groundtruth\location-names.csv</c> holds 47 prefab names recovered from the game's own
    /// world-generation log together with the hash the <c>.db2</c> stores for them. That hash is
    /// <c>m_prefab.Name.GetStableHashCode()</c> - the same function, on the same string, that
    /// <c>GenerateLocationsTimeSliced</c> adds to the world seed to open the type's stream
    /// (<c>int seed = WorldGenerator.instance.GetSeed() + location.m_prefab.Name.GetStableHashCode()</c>).
    /// So matching every hash proves the stream seed for every one of those 47 types, for any world.
    /// </para>
    /// </summary>
    public static class StreamCheck
    {
        public static int Run(string[] args)
        {
            List<GroundTruthPaths.NameRow> rows = GroundTruthPaths.ReadNames();
            int bad = 0;
            foreach (GroundTruthPaths.NameRow r in rows)
            {
                int h = StableHash.Compute(r.Name);
                if (h != r.Hash)
                {
                    bad++;
                    Console.WriteLine("  MISMATCH " + r.Name + ": csv " + r.Hash + ", computed " + h);
                }
            }
            Console.WriteLine("stream seeds");
            Console.WriteLine("  " + (rows.Count - bad) + " / " + rows.Count
                              + " prefab-name hashes in location-names.csv reproduced by StableHash.Compute");

            // Cross-check against the hashes the .db2 actually stores, via the per-world CSV.
            foreach (WorldRef w in WorldRef.All)
            {
                // The CSV leaves the name blank for every hash whose prefab name has not been recovered
                // from a log yet - about five instances in six. Those rows carry no claim to check.
                Dictionary<string, int> seen = new Dictionary<string, int>(StringComparer.Ordinal);
                HashSet<int> unnamedHashes = new HashSet<int>();
                int total = 0, named = 0, rowBad = 0;
                foreach (GroundTruthPaths.CsvInstance c in GroundTruthPaths.ReadLocationsCsv(w))
                {
                    total++;
                    if (c.Name.Length == 0) { unnamedHashes.Add(c.Hash); continue; }
                    named++;
                    if (StableHash.Compute(c.Name) != c.Hash) rowBad++;
                    seen.TryGetValue(c.Name, out int n);
                    seen[c.Name] = n + 1;
                }
                Console.WriteLine("  " + w.Name + ": " + (named - rowBad) + " / " + named
                                  + " NAMED saved instances have hash == GetStableHashCode(name), "
                                  + seen.Count + " distinct prefabs; " + (total - named)
                                  + " further instances carry " + unnamedHashes.Count
                                  + " hashes whose prefab name is not yet known (the dumper supplies them)");
                if (rowBad != 0) bad += rowBad;
            }

            if (args.Length > 0 && Args.Has(args, "--verbose"))
            {
                Console.WriteLine("  stream seeds for the two ground-truth worlds (seed + nameHash, unchecked):");
                foreach (GroundTruthPaths.NameRow r in rows)
                    Console.WriteLine("    " + r.Name.PadRight(28)
                                      + " hash " + r.Hash.ToString().PadLeft(12)
                                      + "  asdasdasd " + unchecked(WorldRef.Development.Seed + r.Hash).ToString().PadLeft(12)
                                      + "  testworldclaude " + unchecked(WorldRef.HoldOut.Seed + r.Hash).ToString().PadLeft(12));
            }

            Console.WriteLine("  verdict     " + (bad == 0 ? "PASS" : "FAIL"));
            return bad == 0 ? 0 : 1;
        }
    }
}
