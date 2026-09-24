using System;
using System.Collections.Generic;
using SeedLab.WorldGen;

namespace SeedLab.Locations
{
    /// <summary>
    /// The per-seed state location placement needs: the world generator, the 2048^2 biome point grid,
    /// its sector decomposition and (optionally) the alt-biome assignment. Building it is the expensive
    /// part of a seed - see <see cref="GridMilliseconds"/> and <see cref="SectorMilliseconds"/> - and it
    /// is reusable across as many placement runs as you like.
    /// </summary>
    public sealed class WorldLocations
    {
        private WorldLocations(WorldGeneratorPort gen, BiomeField field, IReadOnlyList<AltBiomeRuntime>? altBiomes,
                               double altMs)
        {
            Generator = gen;
            Field = field;
            AltBiomes = altBiomes;
            AltBiomeMilliseconds = altMs;
        }

        public WorldGeneratorPort Generator { get; }
        public BiomeField Field { get; }
        public BiomeGrid Grid => Field.Grid;

        /// <summary>Null when alt-biomes were not computed (no table supplied).</summary>
        public IReadOnlyList<AltBiomeRuntime>? AltBiomes { get; }

        public int Seed => Generator.GetSeed();
        public double GridMilliseconds => Grid.BuildMilliseconds;
        public double SectorMilliseconds => Field.BuildMilliseconds;
        public double AltBiomeMilliseconds { get; }
        public double TotalMilliseconds => GridMilliseconds + SectorMilliseconds + AltBiomeMilliseconds;

        /// <summary>
        /// Builds everything for one seed.
        ///
        /// <para><b>Cost.</b> The grid alone is 4 194 304 <c>GetBiome</c> + <c>GetBiomeHeight</c>
        /// evaluations. That number, not the placement run, is what a seed-search tier design has to
        /// budget for; <see cref="GridMilliseconds"/> measures it on the machine in front of you.</para>
        ///
        /// <para><paramref name="altBiomes"/> must be a set of objects this call may own: the
        /// assignment resets and refills their per-world <c>Sectors</c> lists. Sharing one set across
        /// two worlds at the same time is a bug the game itself hides behind a scene reload.</para>
        /// </summary>
        public static WorldLocations Build(int seed, int worldGenVersion = 2,
                                           IReadOnlyList<AltBiomeRuntime>? altBiomes = null,
                                           int maxDegreeOfParallelism = -1)
        {
            WorldGeneratorPort gen = new WorldGeneratorPort(seed, worldGenVersion, menu: false);
            return Build(gen, altBiomes, maxDegreeOfParallelism);
        }

        public static WorldLocations Build(WorldGeneratorPort gen, IReadOnlyList<AltBiomeRuntime>? altBiomes = null,
                                           int maxDegreeOfParallelism = -1)
        {
            BiomeGrid grid = BiomeGrid.Build(gen, maxDegreeOfParallelism);
            BiomeField field = BiomeField.Build(grid);
            double altMs = 0;
            if (altBiomes != null)
            {
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                AltBiomeAssignment.Generate(field, altBiomes, gen.GetSeed());
                sw.Stop();
                altMs = sw.Elapsed.TotalMilliseconds;
            }
            return new WorldLocations(gen, field, altBiomes, altMs);
        }

        /// <summary>
        /// The full instance list, exactly as the game would write it into a fresh <c>.db2</c>.
        /// </summary>
        public PlacementResult PlaceAll(LocationTable table, IPlacementTrace? trace = null)
            => LocationPlacementEngine.Run(Generator, Field, table, new PlacementOptions
            {
                Trace = trace,
                AltBiomesComputed = AltBiomes != null,
            });

        /// <summary>
        /// Only as much of the ordered list as the named target needs.
        ///
        /// <para>The prefix is <c>OrderedIndexOf(target) + 1</c> and nothing shorter is safe: a type's
        /// own stream is independent, but every earlier type can steal its zones (one location per zone,
        /// globally), share its AssetID / group buckets, or seed its <c>CountNrOfLocation</c>. So the
        /// saving is real only for entries near the front - which, if boss altars are
        /// <c>m_prioritized</c>, is exactly where the bosses are. The returned
        /// <see cref="PlacementResult.IsPartial"/> is true, and every type after the target is absent
        /// rather than "not placed".</para>
        /// </summary>
        public PlacementResult PlaceForTarget(LocationTable table, string prefabName, IPlacementTrace? trace = null)
        {
            int prefix = table.PrefixLengthForTarget(prefabName);
            return LocationPlacementEngine.Run(Generator, Field, table, new PlacementOptions
            {
                StopAfterOrderedIndex = prefix - 1,
                Trace = trace,
                AltBiomesComputed = AltBiomes != null,
            });
        }

        /// <summary>
        /// Every filter's verdict for one entry at one world point, for answering "why is there no
        /// crypt here". See <see cref="LocationProbe"/> for what it cannot decide.
        /// </summary>
        public IReadOnlyList<FilterVerdict> Explain(ZoneLocationEntry e, float x, float z, PlacementResult? afterRun = null)
            => LocationProbe.Explain(Generator, Field, e, x, z, afterRun);
    }
}
