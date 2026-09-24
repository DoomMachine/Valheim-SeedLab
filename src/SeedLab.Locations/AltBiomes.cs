using System;
using System.Collections.Generic;
using SeedLab.Contracts.Dump;
using SeedLab.Seeds;
using SeedLab.WorldGen;
using SeedLab.WorldGen.Unity;

namespace SeedLab.Locations
{
    /// <summary>
    /// One <c>AltBiome</c> (decomp/AltBiome.cs). Field defaults below are the CODE defaults; the
    /// shipped asset overrides most of them, and the dumper is what settles the real values - nothing
    /// in this assembly hard-codes a particular alt-biome.
    ///
    /// <para><b>Mutable per world.</b> <c>GenerateAltBiomes</c> never clears <c>AltBiome.Sectors</c>:
    /// in the game that is masked because a scene load re-instantiates the whole list. A sweep that
    /// reuses one set of objects across seeds would starve every run after the first AND leak the
    /// previous world's 2048^2 grid through the stale sector references. <see cref="ResetForWorld"/>
    /// exists to make that impossible; <see cref="AltBiomeAssignment.Generate"/> calls it.</para>
    /// </summary>
    public sealed class AltBiomeRuntime
    {
        public string Name = "";
        public int NameHash;
        public bool Enabled = true;
        public Biome Biome;

        public float MinDistanceFromCenter = 1000f;
        public int MinAmountSpawned = 1;
        public int MaxAmountSpawned = 10;
        public float Chance = 0.1f;

        public Biome RequireNeighbor = Biome.None;
        public Biome NotNeighbor = Biome.None;
        public string[] IncompatibleAltBiomes = Array.Empty<string>();

        public int MinEdgeSize = 50;
        public int MaxEdgeSize = 1500;
        public float MinAvgHeight = 30f;
        public float MaxAvgHeight = 10000f;

        public float BelowWorldX, AboveWorldX, BelowWorldY, AboveWorldY;

        /// <summary>Matched against <c>ZoneLocation.m_name</c> by placement filter 10b.</summary>
        public string[] BlockLocationNames = Array.Empty<string>();

        // ---- per-world state --------------------------------------------------------------------

        /// <summary>Indices into <see cref="BiomeField.Sectors"/>, in <c>AddModifier</c> order.</summary>
        public readonly List<int> Sectors = new List<int>();

        public int ValidPlacementSectors;
        public int ValidPlacementSectorCombos;

        /// <summary>Clears everything <c>GenerateAltBiomes</c> forgets to clear, plus what it does.</summary>
        public void ResetForWorld()
        {
            Sectors.Clear();
            ValidPlacementSectors = 0;
            ValidPlacementSectorCombos = 0;
        }

        public static AltBiomeRuntime FromDump(AltBiomeDef d)
        {
            if (d == null) throw new ArgumentNullException(nameof(d));
            string name = d.name ?? "";
            int hash = StableHash.Compute(name);
            if (d.nameHash != 0 && d.nameHash != hash)
                throw new InvalidOperationException(
                    "altbiomes.json entry '" + name + "' carries nameHash " + d.nameHash
                    + " but GetStableHashCode is " + hash + ".");
            return new AltBiomeRuntime
            {
                Name = name,
                NameHash = hash,
                Enabled = d.enabled,
                Biome = (Biome)d.biome,
                MinDistanceFromCenter = d.minDistanceFromCenter,
                MinAmountSpawned = d.minAmountSpawned,
                MaxAmountSpawned = d.maxAmountSpawned,
                Chance = d.chance,
                RequireNeighbor = (Biome)d.requireNeighbor,
                NotNeighbor = (Biome)d.notNeighbor,
                IncompatibleAltBiomes = d.incompatibleAltBiomes ?? Array.Empty<string>(),
                MinEdgeSize = d.minEdgeSize,
                MaxEdgeSize = d.maxEdgeSize,
                MinAvgHeight = d.minAvgHeight,
                MaxAvgHeight = d.maxAvgHeight,
                BelowWorldX = d.belowWorldX,
                AboveWorldX = d.aboveWorldX,
                BelowWorldY = d.belowWorldY,
                AboveWorldY = d.aboveWorldY,
                BlockLocationNames = d.blockLocationNames ?? Array.Empty<string>(),
            };
        }

        public override string ToString() => Name + " [" + Biome + "] " + Sectors.Count + " sectors";
    }

    /// <summary>
    /// <c>AltBiomeWorldData.GenerateAltBiomes</c> (decomp 262-300) and
    /// <c>BiomeSector.CanAddModifier</c> (decomp/BiomeSector.cs 96-180), ported with their bugs.
    /// </summary>
    public static class AltBiomeAssignment
    {
        /// <summary><c>UnityEngine.Random.InitState(seed + 920)</c> at the top of GenerateAltBiomes.</summary>
        public const int GlobalSeedOffset = 920;

        /// <summary>
        /// Assigns alt-biomes to sectors. Reproduces, in order:
        /// the pointless <c>InitState(seed + 920)</c>; the counter reset; a walk over the TWELVE
        /// <c>Biomes</c> keys including <c>None</c>, <c>Land</c> and <c>All</c>; per (biome, altBiome)
        /// pair an <c>InitState((int)(biomeKey + nameHash + seed))</c> followed by an in-place
        /// Fisher-Yates shuffle of that biome's sector list; and one <c>Range(0f, 1f)</c> draw per
        /// sector examined while the alt-biome is still below its max.
        ///
        /// <para>Three details that decide the outcome: the shuffle mutates the list the NEXT
        /// alt-biome will walk, so order is load-bearing; the float draw happens even for sectors that
        /// then fail <c>CanAddModifier</c>; and <c>HasFlag(None)</c> is true for every mask, so every
        /// enabled alt-biome gets one extra InitState + shuffle under the <c>None</c> key (harmless,
        /// its sector list is empty, but it must not be skipped if you ever share a stream).</para>
        ///
        /// <para><b>Proven, 2026-09-23</b>, against the game's own
        /// goldens/altbiomes-assignment-0480A34C.json (seed 75539276, 32 alt-biomes, all enabled): all
        /// 32 sector lists identical IN AddModifier ORDER, 99 of 99 sector slots, and
        /// ValidPlacementSectors / ValidPlacementSectorCombos equal for every one; the per-sector
        /// AltBiomes lists match in order for all 938 sectors. Independently on seed 319486907, the
        /// game's worldgen log warned about exactly one alt-biome that finished under its
        /// m_minAmountSpawned ("Fortress Mountain", 0/1-2, valid sectors 0, combos 2) and the engine
        /// produces that same one with the same counters - and the alt-biome-gated location variants of
        /// that world (SwampHut1_1 33/50, SwampHut2_1 2/50, GoblinCamp2_1 0/5, TarPit1_1 0/50) come out
        /// at the counts the same log printed. Skipping the <c>Biome.None</c> key breaks
        /// ValidPlacementSectorCombos on all 32.</para>
        /// </summary>
        /// <param name="ambient">
        /// Optional: the shared "ambient" generator, so a caller that models the game's global stream
        /// sees the same state afterwards. Placement never reads it - every location type re-seeds - so
        /// null is the normal case.
        /// </param>
        public static void Generate(BiomeField field, IReadOnlyList<AltBiomeRuntime> altBiomes, int worldSeed,
                                    UnityRandom? ambient = null)
        {
            if (field == null) throw new ArgumentNullException(nameof(field));
            if (altBiomes == null) throw new ArgumentNullException(nameof(altBiomes));

            UnityRandom rnd = ambient ?? new UnityRandom();
            rnd.InitState(unchecked(worldSeed + GlobalSeedOffset));

            foreach (AltBiomeRuntime a in altBiomes) a.ResetForWorld();
            foreach (BiomeSectorData s in field.Sectors) s.AltBiomes.Clear();

            List<AltBiomeRuntime> valid = new List<AltBiomeRuntime>();
            foreach (BiomeTypeInfo info in field.BiomesInKeyOrder)
            {
                // AltBiomeList.GetValidAltBiomes: m_enabled && m_biome.HasFlag(key).
                valid.Clear();
                foreach (AltBiomeRuntime a in altBiomes)
                    if (a.Enabled && HasFlag(a.Biome, info.Biome)) valid.Add(a);

                foreach (AltBiomeRuntime a in valid) a.ValidPlacementSectorCombos++;

                foreach (AltBiomeRuntime a in valid)
                {
                    rnd.InitState(unchecked((int)info.Biome + a.NameHash + worldSeed));
                    Shuffle(info.Sectors, rnd);
                    foreach (int si in info.Sectors)
                    {
                        if (a.Sectors.Count >= a.MaxAmountSpawned) continue;   // no draw once full
                        float r = rnd.Range(0f, 1f);                            // one draw per sector examined
                        BiomeSectorData s = field.Sectors[si];
                        if ((a.Sectors.Count < a.MinAmountSpawned || a.Chance >= r) && CanAddModifier(field, s, a))
                        {
                            a.ValidPlacementSectors++;
                            a.Sectors.Add(si);       // AddModifier: modifier.Sectors.Add(this)
                            s.AltBiomes.Add(a);      //              AltBiomes.Add(modifier)
                        }
                    }
                }
            }
        }

        /// <summary><c>Enum.HasFlag</c> == <c>(value &amp; flag) == flag</c>; true for flag == 0.</summary>
        public static bool HasFlag(Biome value, Biome flag) => ((int)value & (int)flag) == (int)flag;

        /// <summary><c>Utils.Shuffle&lt;T&gt;(IList&lt;T&gt;)</c> - Fisher-Yates from the end, Count-1 int draws.</summary>
        public static void Shuffle(List<int> list, UnityRandom rnd)
        {
            for (int n = list.Count - 1; n > 0; n--)
            {
                int i = rnd.Range(0, n + 1);
                int t = list[n];
                list[n] = list[i];
                list[i] = t;
            }
        }

        /// <summary>
        /// <c>BiomeSector.CanAddModifier</c>, in the game's order.
        ///
        /// <para><b>Two neighbour bugs, both reproduced.</b>
        /// (1) <c>m_requireNeighbor != None</c> makes this return false ALWAYS: the loop runs
        /// <c>i = 0</c> first, <c>((BiomeIndex)0).ToBiome()</c> is <c>Biome.None == 0</c>,
        /// <c>HasFlag(0)</c> is true for every mask, so the iteration is never skipped and it demands a
        /// neighbour whose <c>Biome == Biome.None</c> - which no sector can ever have.
        /// (2) <c>m_notNeighbor</c> tests the mask bit for <c>BiomeIndex j</c> but compares the
        /// neighbour against <c>(Heightmap.Biome)j</c>, so the Mountain bit(index 3) is unreachable, the
        /// BlackForest bit (index 4) rejects a <b>Mountain(4)</b> neighbour and the Ocean bit (index 8)
        /// rejects a <b>BlackForest(8)</b> neighbour.
        ///
        /// <b>Neither bug is reachable in vanilla 1.0.15:</b> measured 2026-09-23, all 32 shipped
        /// alt-biomes have <c>m_requireNeighbor == None</c> and <c>m_notNeighbor == None</c>, so both
        /// loops are dead code. Starting the requireNeighbor loop at <c>i = 1</c> (the "fix") changes
        /// nothing measurable - which is a statement about the data, not a licence to change the
        /// code.</para>
        /// </summary>
        public static bool CanAddModifier(BiomeField field, BiomeSectorData sector, AltBiomeRuntime m)
        {
            if (sector.DistanceFromCenter < m.MinDistanceFromCenter) return false;
            if (sector.EdgeCount < m.MinEdgeSize) return false;
            if (sector.EdgeCount >= m.MaxEdgeSize) return false;
            if (sector.HeightAvg < m.MinAvgHeight || sector.HeightAvg >= m.MaxAvgHeight) return false;
            if ((m.AboveWorldX != 0f && sector.CenterX < m.AboveWorldX)
                || (m.BelowWorldX != 0f && sector.CenterX > m.BelowWorldX)
                || (m.AboveWorldY != 0f && sector.CenterY < m.AboveWorldY)
                || (m.BelowWorldY != 0f && sector.CenterY > m.BelowWorldY)) return false;

            foreach (AltBiomeRuntime present in sector.AltBiomes)
                foreach (string s in present.IncompatibleAltBiomes)
                    if (string.Equals(s, m.Name, StringComparison.Ordinal)) return false;

            foreach (string s in m.IncompatibleAltBiomes)
                foreach (AltBiomeRuntime present in sector.AltBiomes)
                    if (string.Equals(present.Name, s, StringComparison.Ordinal)) return false;

            if (m.RequireNeighbor != Biome.None)
            {
                for (int i = 0; i < 10; i++)
                {
                    if (!HasFlag(m.RequireNeighbor, ((BiomeIndex)i).ToBiome())) continue;
                    bool found = false;
                    foreach (int ni in sector.Neighbors)
                        if (field.Sectors[ni].Biome == (Biome)i) { found = true; break; }
                    if (!found) return false;
                }
            }

            if (m.NotNeighbor != Biome.None)
            {
                for (int j = 0; j < 10; j++)
                {
                    if (!HasFlag(m.NotNeighbor, ((BiomeIndex)j).ToBiome())) continue;
                    foreach (int ni in sector.Neighbors)
                        if (field.Sectors[ni].Biome == (Biome)j) return false;
                }
            }

            return true;
        }
    }
}
