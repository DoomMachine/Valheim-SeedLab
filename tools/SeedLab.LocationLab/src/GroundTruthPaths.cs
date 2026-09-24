using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace SeedLab.LocationLab
{
    /// <summary>One of the two worlds the game itself generated.</summary>
    public sealed class WorldRef
    {
        public WorldRef(string name, string seedText, int seed, bool holdOut)
        { Name = name; SeedText = seedText; Seed = seed; IsHoldOut = holdOut; }

        public string Name { get; }
        public string SeedText { get; }
        public int Seed { get; }
        public bool IsHoldOut { get; }

        public static readonly WorldRef Development = new WorldRef("asdasdasd", "MWd8eV6svz", -1772362158, false);
        public static readonly WorldRef HoldOut = new WorldRef("testworldclaude", "hnBd9gJf2G", 319486907, true);
        public static readonly WorldRef[] All = { Development, HoldOut };

        public static WorldRef ByName(string name)
        {
            foreach (WorldRef w in All)
                if (string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase)) return w;
            throw new ArgumentException("Unknown world '" + name + "'. Known: asdasdasd, testworldclaude.");
        }
    }

    /// <summary>Locates <c>groundtruth\</c> by walking up from the cwd and from the binary directory.</summary>
    public static class GroundTruthPaths
    {
        private static string? s_root;

        public static string Root
        {
            get
            {
                if (s_root != null) return s_root;
                foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
                {
                    DirectoryInfo? d = new DirectoryInfo(start);
                    for (int i = 0; i < 12 && d != null; i++, d = d.Parent)
                    {
                        string cand = Path.Combine(d.FullName, "groundtruth");
                        if (Directory.Exists(Path.Combine(cand, "decoded"))) { s_root = cand; return s_root; }
                    }
                }
                throw new DirectoryNotFoundException("groundtruth\\decoded not found from "
                    + Directory.GetCurrentDirectory() + " or " + AppContext.BaseDirectory);
            }
        }

        public static string BiomeOracle(WorldRef w) => Path.Combine(Root, "decoded", w.Name + ".biome.u8");
        public static string HeightOracle(WorldRef w) => Path.Combine(Root, "decoded", w.Name + ".height.f32");
        public static string LocationsCsv(WorldRef w) => Path.Combine(Root, w.Name + "-locations.csv");
        public static string WorldDirectory(WorldRef w) => Path.Combine(Root, "worlds", w.Name);
        public static string NamesCsv => Path.Combine(Root, "location-names.csv");

        /// <summary>One row of <c>&lt;world&gt;-locations.csv</c>: hash,name,x,z,placed.</summary>
        public readonly struct CsvInstance
        {
            public CsvInstance(int hash, string name, float x, float z, bool placed)
            { Hash = hash; Name = name; X = x; Z = z; Placed = placed; }
            public int Hash { get; }
            public string Name { get; }
            public float X { get; }
            public float Z { get; }
            public bool Placed { get; }
        }

        public static List<CsvInstance> ReadLocationsCsv(WorldRef w)
        {
            List<CsvInstance> rows = new List<CsvInstance>();
            using StreamReader r = new StreamReader(LocationsCsv(w));
            string? line = r.ReadLine();                 // header
            while ((line = r.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                string[] f = line.Split(',');
                if (f.Length < 5) continue;
                rows.Add(new CsvInstance(
                    int.Parse(f[0], CultureInfo.InvariantCulture),
                    f[1],
                    float.Parse(f[2], CultureInfo.InvariantCulture),
                    float.Parse(f[3], CultureInfo.InvariantCulture),
                    bool.Parse(f[4])));
            }
            return rows;
        }

        /// <summary>
        /// <c>location-names.csv</c>: the prefab names recovered from the game's own worldgen log, with
        /// the hashes the .db2 stores. The only ground truth that exists today for the stream seeds.
        /// </summary>
        public readonly struct NameRow
        {
            public NameRow(string name, int hash, int? placedInLog, int? quantityInLog)
            { Name = name; Hash = hash; PlacedInLog = placedInLog; QuantityInLog = quantityInLog; }
            public string Name { get; }
            public int Hash { get; }
            public int? PlacedInLog { get; }
            public int? QuantityInLog { get; }
        }

        public static List<NameRow> ReadNames()
        {
            List<NameRow> rows = new List<NameRow>();
            using StreamReader r = new StreamReader(NamesCsv);
            string? line = r.ReadLine();
            while ((line = r.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                string[] f = line.Split(',');
                if (f.Length < 2) continue;
                int? placed = f.Length > 2 && f[2].Length > 0 ? int.Parse(f[2], CultureInfo.InvariantCulture) : (int?)null;
                int? qty = f.Length > 3 && f[3].Length > 0 ? int.Parse(f[3], CultureInfo.InvariantCulture) : (int?)null;
                rows.Add(new NameRow(f[0], int.Parse(f[1], CultureInfo.InvariantCulture), placed, qty));
            }
            return rows;
        }
    }
}
