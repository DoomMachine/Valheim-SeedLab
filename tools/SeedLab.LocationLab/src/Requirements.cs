using System;

namespace SeedLab.LocationLab
{
    /// <summary>
    /// Exactly what the BepInEx dumper has to produce before <c>reconstruct</c> can run, and what each
    /// item is for. Printed rather than filed so it stays next to the code that consumes it.
    /// </summary>
    public static class Requirements
    {
        public static int Run(string[] args)
        {
            Console.WriteLine(Text);
            return 0;
        }

        public const string Text = @"
What the dumper must provide for `reconstruct` to run
=====================================================

REQUIRED - locations.json (SeedLab.Contracts.Dump.LocationTableFile)
--------------------------------------------------------------------
One entry per element of ZoneSystem.m_locations, IN LIST ORDER, captured in a postfix hook on
ZoneSystem.SetupLocations (after it has run, so m_prefabName has been overwritten with m_prefab.Name
and AltBiomeParent has been set). Per entry:

  index            position in m_locations. The order is semantic; do not sort the array.
  softRefName      m_prefab.Name.               <- THE RNG STREAM KEY. Without it nothing works.
  prefabName       m_prefabName (runtime value). Dump both, even though they are equal today.
  nameHash         m_prefab.Name.GetStableHashCode(). Checked against this port's hash, not trusted.
  assetId          m_prefab.m_assetID, all four uints (v3, v2, v1, v0), in declaration order.
                   Without it two entries that share one prefab asset cannot be put in one
                   m_locationIDCache bucket, and filters 7/8 differ.
  name             m_name. Read by filter 10b (m_blockLocationNames matches m_name, NOT the prefab).
  enable, quantity                 decide membership of the ordered list.
  prioritized                      the ordering key AND the attempt budget (60000 vs 12000).
  biome, biomeArea                 ints (Heightmap.Biome bitmask; Edge 1 / Median 2 / Everything 3).
  centerFirst, minDistance         select GetRandomZone and its growing range.
  minAltitude                      ALSO selects the zone draw: < 0 uses GetRandomPointByBiomes,
                                   >= 0 uses ...AboveSeaLevel. A sign error changes every draw.
  maxAltitude, maxDistance, minDistanceFromCenter, maxDistanceFromCenter
  unique, group, groupMax, minDistanceFromSimilar, maxDistanceFromSimilar
  interiorRadius, exteriorRadius   maxRadius insets the point draw; exteriorRadius is the
                                   GetTerrainDelta sample radius.
  minTerrainDelta, maxTerrainDelta
  minimumVegetation, maximumVegetation
  surroundCheckVegetation, surroundCheckDistance, surroundCheckLayers, surroundBetterThanAverage
  inForest, forestTresholdMin, forestTresholdMax
  altBiomeParent                   null for everything that did not come from an AltBiome.

Also at file level: count, enabledCount (the length of the ordered list the game actually walked -
this port cross-checks its own ordering against it) and the loaded BepInEx plugin list. The 183
figure in the log is from a MODDED install; a table is only valid for the build + plugin set that
produced it.

STRONGLY WANTED - altbiomes.json (AltBiomeTableFile)
----------------------------------------------------
All 28 AltBiome records with every field CanAddModifier reads: name, enabled, biome,
minDistanceFromCenter, minAmountSpawned, maxAmountSpawned, chance, requireNeighbor, notNeighbor,
incompatibleAltBiomes, minEdgeSize, maxEdgeSize, minAvgHeight, maxAvgHeight, the four world-bounds,
blockLocationNames, and the addLocations list.

Without it the engine runs with empty AltBiomes on every sector. Consequences, stated exactly:
filter 10b then always passes, and filter 10a rejects EVERY point of every entry that carries an
altBiomeParent - which is what the game does too when no sector carries that alt-biome, but it is an
assumption until the table exists. The entries the log shows placing 0 of N (TarPit1_1, TarPit2_1,
TarPit3_1, GoblinCamp2_1, StoneTowerRuins05_leet, StoneTowerRuins10_sunk) are the signature of
exactly that.

USEFUL, NOT REQUIRED
--------------------
  locationinstances-<seedHex>.json   m_locationInstances after genloc on a FRESH world: (zone,
                                     prefabName, x, y, z, placed). A fresh dump removes the two
                                     sources of expected disagreement that the played .db2 files carry
                                     (generated zones, unique losers already deleted).
  AllPoints / AllPointsAboveSeaLevel counts per biome, plus the first and last 100 entries of each.
                                     That alone catches the flood-fill ordering and the missing
                                     seed-point quirk without a full comparison.
  m_locationVersion                  only matters for predicting re-rolls on an existing world.
  natives-random.json                state deltas for Range(int,int) with min == max,
                                     Range(float,float) with min > max, and insideUnitCircle. This
                                     port already assumes: min == max consumes nothing; Range(float)
                                     always consumes one; insideUnitCircle consumes two and is
                                     cos/sin of a 0..2pi draw times sqrt of a second. `synthetic`
                                     asserts those assumptions so a dump can contradict them loudly.

HOW TO RUN IT, ONCE THE DUMP EXISTS
-----------------------------------
  dotnet run -c Release --project tools\SeedLab.LocationLab -- reconstruct ^
      --table <dump>\locations.json [--altbiomes <dump>\altbiomes.json]

It reconstructs both ground-truth worlds and prints per-prefab agreement against their .db2 files.
";
    }
}
