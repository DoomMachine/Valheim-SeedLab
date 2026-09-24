using System;
using System.Collections.Generic;
using System.IO;
using SeedLab.Saves;
using SeedLab.Seeds;

namespace SeedLabAcceptanceTests
{
    /// <summary>
    /// Cross-checks SeedLab.Saves against the files the game wrote, and hands the sweep its oracles.
    ///
    /// The point of doing it here rather than trusting groundtruth\decoded is that the decoded files
    /// were produced by a throwaway Python probe; if the shipping C# reader and that probe disagree,
    /// one of them is wrong and the whole acceptance result is worthless. So the reader must reproduce
    /// the decoded files exactly, and only then are they used as the oracle.
    /// </summary>
    public static class SaveChecks
    {
        public sealed class WorldOracles
        {
            public byte[] BiomeIndices = Array.Empty<byte>();
            public ushort[] HeightHalf = Array.Empty<ushort>();
        }

        public static WorldOracles Run(WorldFixture w, Report rep)
        {
            WorldOracles o = new WorldOracles();
            string dir = GroundTruth.WorldDirectory(w);

            // ---- cacheMinimapMeta -------------------------------------------------------------------
            // 8 raw bytes: int32 seed, int32 Version.CachedMinimap (05-validation.md section 1.2).
            MinimapCacheMeta meta = MinimapCacheReader.ReadMeta(dir);
            rep.Add(w.Name + "/S1", "minimap cache meta",
                    meta.Seed == w.Seed && meta.CacheVersion == 1,
                    "seed " + meta.Seed + " (expected " + w.Seed + "), cache version " + meta.CacheVersion);

            MinimapCache cache = MinimapCacheReader.Read(dir, new MinimapCacheOptions { ExpectedSeed = w.Seed });

            // ---- the reader must reproduce the decoded oracle files byte for byte ---------------------
            byte[] fileBiome = File.ReadAllBytes(GroundTruth.DecodedBiome(w));
            byte[] readerBiome = cache.DecodeBiomeIndices();
            long biomeDiff = CountDiff(fileBiome, readerBiome);
            rep.Add(w.Name + "/S2", "MinimapCacheReader reproduces <world>.biome.u8",
                    biomeDiff == 0 && fileBiome.Length == 2048 * 2048,
                    fileBiome.Length.ToString("N0") + " bytes, " + biomeDiff.ToString("N0") + " differ");
            o.BiomeIndices = fileBiome;

            byte[] fileHeightBytes = File.ReadAllBytes(GroundTruth.DecodedHeight(w));
            float[] fileHeight = new float[fileHeightBytes.Length / 4];
            Buffer.BlockCopy(fileHeightBytes, 0, fileHeight, 0, fileHeightBytes.Length);
            float[] readerHeight = cache.DecodeHeights();
            long heightDiff = 0;
            for (int i = 0; i < fileHeight.Length; i++)
                if (BitConverter.SingleToInt32Bits(fileHeight[i]) != BitConverter.SingleToInt32Bits(readerHeight[i]))
                    heightDiff++;
            rep.Add(w.Name + "/S3", "MinimapCacheReader reproduces <world>.height.f32 bit for bit",
                    heightDiff == 0 && fileHeight.Length == 2048 * 2048,
                    fileHeight.Length.ToString("N0") + " floats, " + heightDiff.ToString("N0") + " differ");

            // The sweep compares 16-bit codes, not decoded floats: +0.0 (0x0000) and -0.0 (0x8000) decode
            // to floats that compare equal, and 05-validation.md T3 records one -0.0 pixel in asdasdasd
            // and two in testworldclaude. Take the raw codes from the reader.
            o.HeightHalf = new ushort[cache.PixelCount];
            for (int i = 0; i < o.HeightHalf.Length; i++) o.HeightHalf[i] = cache.GetHeightBits(i);

            // Guard the direction we did not check above: the raw codes must decode to the file's floats.
            long codeDiff = 0;
            for (int i = 0; i < o.HeightHalf.Length; i++)
                if (BitConverter.SingleToInt32Bits(HalfCodec.Decode(o.HeightHalf[i]))
                    != BitConverter.SingleToInt32Bits(fileHeight[i])) codeDiff++;
            int negZero = 0;
            for (int i = 0; i < o.HeightHalf.Length; i++) if (o.HeightHalf[i] == 0x8000) negZero++;
            rep.Add(w.Name + "/S4", "raw half codes decode to the oracle floats",
                    codeDiff == 0, codeDiff.ToString("N0") + " differ; " + negZero + " pixels hold -0.0 (code 0x8000)");

            // ---- _main.<N>.fwl2 ---------------------------------------------------------------------
            WorldMeta wm = WorldMetaReader.Read(GroundTruth.SaveFile(w, "fwl2"));
            bool fwlOk = wm.FileVersion == 41 && wm.Name == w.Name && wm.SeedName == w.SeedText
                         && wm.Seed == w.Seed && wm.WorldGenVersion == 2 && wm.SeedMatchesSeedName;
            rep.Add(w.Name + "/S5", ".fwl2 metadata", fwlOk,
                    "version " + wm.FileVersion + ", name '" + wm.Name + "', seedName '" + wm.SeedName
                    + "', seed " + wm.Seed + ", worldGenVersion " + wm.WorldGenVersion
                    + ", uid " + wm.Uid + ", seedName re-hashes to the stored seed: " + wm.SeedMatchesSeedName);

            // Independent of SeedLab.Saves' own private copy of the hash (SaveStableHash): recompute with
            // SeedLab.Seeds. If the two ever diverge this line catches it.
            int viaSeeds = StableHash.SeedFromText(wm.SeedName);
            rep.Add(w.Name + "/S6", ".fwl2 seed text re-hashes to the stored int (SeedLab.Seeds)",
                    viaSeeds == wm.Seed,
                    "StableHash.SeedFromText(\"" + wm.SeedName + "\") = " + viaSeeds + ", stored " + wm.Seed);

            // ---- _main.<N>.db2 ----------------------------------------------------------------------
            WorldDb db = WorldDbReader.Read(GroundTruth.SaveFile(w, "db2"));
            ZoneSystemData zs = db.ZoneSystem;
            int placed = 0;
            HashSet<int> hashes = new HashSet<int>();
            foreach (LocationInstance li in zs.Locations) { if (li.Placed) placed++; hashes.Add(li.PrefabHash); }
            rep.Add(w.Name + "/S7", ".db2 location instances",
                    zs.Locations.Count == w.LocationInstances && zs.LocationsGenerated && db.FileVersion == 41,
                    zs.Locations.Count.ToString("N0") + " instances (expected " + w.LocationInstances.ToString("N0")
                    + "), " + hashes.Count + " distinct prefab hashes, " + placed + " placed, locationVersion "
                    + zs.LocationVersion + ", " + zs.GeneratedZones.Count + " generated zones, file version "
                    + db.FileVersion);

            return o;
        }

        private static long CountDiff(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return Math.Max(a.Length, b.Length);
            long n = 0;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) n++;
            return n;
        }

        /// <summary>The .db2 location list, for the float32 GetHeight oracle (T5).</summary>
        public static IReadOnlyList<LocationInstance> Locations(WorldFixture w)
            => WorldDbReader.Read(GroundTruth.SaveFile(w, "db2")).ZoneSystem.Locations;
    }
}
