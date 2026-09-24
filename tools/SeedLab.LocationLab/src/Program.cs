using System;
using System.Globalization;

namespace SeedLab.LocationLab
{
    /// <summary>
    /// Harness for SeedLab.Locations.
    ///
    /// <code>
    /// dotnet run -c Release --project tools\SeedLab.LocationLab -- &lt;command&gt; [options]
    ///
    ///   grid          [--world NAME | --seed N] [--workers N] [--no-heights]
    ///                 Build the 2048^2 biome point grid and check it against the minimap oracle the
    ///                 game wrote. Reports the per-seed cost, which is what the search tiers budget for.
    ///
    ///   streams       [--verbose]
    ///                 Reproduce every prefab-name hash in groundtruth\location-names.csv and in both
    ///                 worlds' saved instances. That hash IS the per-location RNG stream seed.
    ///
    ///   synthetic     [--seed N] [--workers N]
    ///                 Everything provable without the asset table: RNG consumption per step, the
    ///                 RandomBiomeFromBiomes bugs, determinism, target-prefix equivalence, and the
    ///                 world invariants on an invented table.
    ///
    ///   fresh         [--seed-hex 0480A34C] [--workers N] [--no-alt] [--quiet]
    ///                 THE decisive test. Reproduces the world the dumper captured before anything was
    ///                 explored and compares the sector decomposition, the alt-biome assignment and
    ///                 every location instance's float32 bits against the game's own dump.
    ///
    ///   played        [--world NAME] [--workers N]
    ///                 Both played worlds against their .db2, plus - for testworldclaude - the per-type
    ///                 'placed N out of M' counters and the alt-biome warnings in the game's worldgen
    ///                 log of that world's creation.
    ///
    ///   gate          fresh + played, one verdict. The location engine's regression gate.
    ///
    ///   reconstruct   --table locations.json [--altbiomes altbiomes.json] [--world NAME]
    ///                 The older harness, for a table file outside the data folder.
    ///
    ///   requirements
    ///                 What the dumper has to provide for reconstruct to run.
    ///
    ///   all           grid + streams + synthetic.
    /// </code>
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            string cmd = args.Length > 0 ? args[0] : "all";
            try
            {
                switch (cmd)
                {
                    case "grid": return GridCheck.Run(args);
                    case "streams": return StreamCheck.Run(args);
                    case "synthetic": return SyntheticCheck.Run(args);
                    case "reconstruct": return Reconstruct.Run(args);
                    case "fresh": return FreshCheck.Run(args);
                    case "played": return PlayedCheck.Run(args);
                    case "gate":
                    {
                        int rcg = FreshCheck.Run(args);
                        Console.WriteLine();
                        rcg |= PlayedCheck.Run(args);
                        Console.WriteLine();
                        Console.WriteLine(rcg == 0 ? "GATE: PASS" : "GATE: FAIL");
                        return rcg;
                    }
                    case "requirements": return Requirements.Run(args);
                    case "all":
                    {
                        int rc = 0;
                        Console.WriteLine("### streams");
                        rc |= StreamCheck.Run(args);
                        Console.WriteLine();
                        Console.WriteLine("### grid");
                        rc |= GridCheck.Run(args);
                        Console.WriteLine();
                        Console.WriteLine("### synthetic");
                        rc |= SyntheticCheck.Run(args);
                        return rc;
                    }
                    default:
                        Console.WriteLine("Unknown command '" + cmd + "'.");
                        Console.WriteLine("Commands: grid, streams, synthetic, fresh, played, gate, reconstruct, requirements, all");
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine(ex.ToString());
                return 3;
            }
        }
    }
}
