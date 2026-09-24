using System;
using System.Collections.Generic;
using HarmonyLib;
using SeedLab.Contracts.Dump;
using SoftReferenceableAssets;
using UnityEngine;

namespace SeedLab.Dumper
{
    /// <summary>
    /// Turns live game objects into plain DTOs. Every read happens here and nowhere else, so there is
    /// one place to check against the decompiled declarations.
    ///
    /// The tables are read on demand rather than captured by a Harmony hook during the user's world
    /// load. They are safe to read late: <c>ZoneSystem.SetupLocations</c> (ZoneSystem.cs:869-940) is
    /// the last thing that touches them - it concatenates the lists, tags the alt-biome entries with
    /// <c>AltBiomeParent</c> and overwrites <c>m_prefabName</c> from <c>m_prefab.Name</c> - and nothing
    /// mutates a <c>ZoneLocation</c> afterwards. The entries in <c>ZoneSystem.m_locations</c> are the
    /// SAME OBJECTS as in each <c>LocationList.m_locations</c> and <c>AltBiome.m_addLocations</c>
    /// (<c>AddRange</c> copies references), which is what makes provenance recoverable by reference
    /// identity instead of by a hook.
    /// </summary>
    internal static class Snapshot
    {
        // ---- locations -------------------------------------------------------------------------

        public static LocationTableFile Locations(ZoneSystem zs, string stamp,
                                                  out List<ZoneSystem.ZoneLocation> ordered)
        {
            List<ZoneSystem.ZoneLocation> all = zs.m_locations;
            Dictionary<ZoneSystem.ZoneLocation, EntrySourceDef> sources = LocationSources();

            // ZoneLocation does not override Equals/GetHashCode, so a Dictionary keyed by it is
            // reference-keyed, which is exactly what identity-based provenance needs.
            ordered = OrderedForPlacement(all);
            var orderIndex = new Dictionary<ZoneSystem.ZoneLocation, int>();
            for (int i = 0; i < ordered.Count; i++) orderIndex[ordered[i]] = i;

            var seenHashes = new Dictionary<int, string>();
            var duplicates = new List<string>();

            var defs = new LocationDef[all.Count];
            int enabled = 0;
            for (int i = 0; i < all.Count; i++)
            {
                ZoneSystem.ZoneLocation l = all[i];
                EntrySourceDef src;
                if (!sources.TryGetValue(l, out src)) src = ZoneSystemPrefabSource();

                int oi;
                if (!orderIndex.TryGetValue(l, out oi)) oi = -1;

                LocationDef d = Location(l, i, oi, src);
                defs[i] = d;
                if (l.m_enable && l.m_quantity != 0) enabled++;

                // SetupLocations registers by ZoneLocation.Hash and logs an error on a collision,
                // keeping the FIRST entry. Its guard is `(m_enable || m_prefab.IsValid) &&
                // Application.isPlaying`, so an entry that fails it is never registered and can never
                // collide - matching that guard here keeps this list from reporting phantom duplicates.
                bool registered = l.m_enable || (d.assetId != null && d.assetId.isValid);
                if (registered)
                {
                    if (seenHashes.ContainsKey(d.nameHash)) duplicates.Add(d.prefabName);
                    else seenHashes[d.nameHash] = d.prefabName;
                }
            }

            return new LocationTableFile
            {
                stamp = stamp,
                schema = DumpFormat.Schema,
                count = defs.Length,
                enabledCount = enabled,
                duplicateHashPrefabNames = duplicates.ToArray(),
                locations = defs,
            };
        }

        /// <summary>
        /// Reproduces the list <c>ZoneSystem.GenerateLocationsTimeSliced()</c> actually walks
        /// (ZoneSystem.cs:1715-1727): <c>m_locations.OrderByDescending(a =&gt; a.m_prioritized)</c> -
        /// a STABLE ordering - and only THEN a reverse sweep removing every entry with
        /// <c>!m_enable || m_quantity == 0</c>. Written out rather than LINQ'd so the stability is
        /// visible: prioritized entries keep their original relative order, then the rest do.
        /// </summary>
        public static List<ZoneSystem.ZoneLocation> OrderedForPlacement(List<ZoneSystem.ZoneLocation> all)
        {
            var result = new List<ZoneSystem.ZoneLocation>(all.Count);
            for (int i = 0; i < all.Count; i++) if (all[i].m_prioritized) result.Add(all[i]);
            for (int i = 0; i < all.Count; i++) if (!all[i].m_prioritized) result.Add(all[i]);
            for (int i = result.Count - 1; i >= 0; i--)
            {
                if (!result[i].m_enable || result[i].m_quantity == 0) result.RemoveAt(i);
            }
            return result;
        }

        public static LocationDef Location(ZoneSystem.ZoneLocation l, int index, int orderedIndex,
                                           EntrySourceDef source)
        {
            string softRefName = null;
            AssetIdDef assetId = null;
            try
            {
                assetId = AssetId(l.m_prefab.m_assetID);
                // SoftReference<T>.Name is the filename without extension of the SoftRef manifest path,
                // memoised on first use. It is the RNG stream key, so it is read verbatim.
                softRefName = l.m_prefab.Name;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Location " + l.m_name + ": SoftReference unreadable (" + e.Message + ")");
            }

            string hashSource = softRefName ?? l.m_prefabName;

            return new LocationDef
            {
                index = index,
                orderedIndex = orderedIndex,
                source = source,

                name = l.m_name,
                enable = l.m_enable,
                prefabName = l.m_prefabName,
                assetId = assetId,
                softRefName = softRefName,
                nameHash = string.IsNullOrEmpty(hashSource) ? 0 : hashSource.GetStableHashCode(),

                biome = (int)l.m_biome,
                biomeArea = (int)l.m_biomeArea,
                quantity = l.m_quantity,
                prioritized = l.m_prioritized,
                centerFirst = l.m_centerFirst,
                unique = l.m_unique,
                group = l.m_group,
                minDistanceFromSimilar = l.m_minDistanceFromSimilar,
                groupMax = l.m_groupMax,
                maxDistanceFromSimilar = l.m_maxDistanceFromSimilar,
                iconAlways = l.m_iconAlways,
                iconPlaced = l.m_iconPlaced,
                randomRotation = l.m_randomRotation,
                slopeRotation = l.m_slopeRotation,
                snapToWater = l.m_snapToWater,
                interiorRadius = l.m_interiorRadius,
                exteriorRadius = l.m_exteriorRadius,
                clearArea = l.m_clearArea,
                minTerrainDelta = l.m_minTerrainDelta,
                maxTerrainDelta = l.m_maxTerrainDelta,
                minimumVegetation = l.m_minimumVegetation,
                maximumVegetation = l.m_maximumVegetation,
                surroundCheckVegetation = l.m_surroundCheckVegetation,
                surroundCheckDistance = l.m_surroundCheckDistance,
                surroundCheckLayers = l.m_surroundCheckLayers,
                surroundBetterThanAverage = l.m_surroundBetterThanAverage,
                inForest = l.m_inForest,
                forestTresholdMin = l.m_forestTresholdMin,
                forestTresholdMax = l.m_forestTresholdMax,
                minDistanceFromCenter = l.m_minDistanceFromCenter,
                maxDistanceFromCenter = l.m_maxDistanceFromCenter,
                minDistance = l.m_minDistance,
                maxDistance = l.m_maxDistance,
                minAltitude = l.m_minAltitude,
                maxAltitude = l.m_maxAltitude,
                foldout = l.m_foldout,

                altBiomeParent = l.AltBiomeParent,
            };
        }

        // ---- vegetation ------------------------------------------------------------------------

        public static VegetationTableFile Vegetation(ZoneSystem zs, string stamp)
        {
            List<ZoneSystem.ZoneVegetation> all = zs.m_vegetation;
            Dictionary<ZoneSystem.ZoneVegetation, EntrySourceDef> sources = VegetationSources();

            var defs = new VegetationDef[all.Count];
            int enabled = 0;
            for (int i = 0; i < all.Count; i++)
            {
                ZoneSystem.ZoneVegetation v = all[i];
                EntrySourceDef src;
                if (!sources.TryGetValue(v, out src)) src = ZoneSystemPrefabSource();
                defs[i] = Vegetation(v, i, src);
                if (v.m_enable) enabled++;
            }

            return new VegetationTableFile
            {
                stamp = stamp,
                schema = DumpFormat.Schema,
                count = defs.Length,
                enabledCount = enabled,
                vegetation = defs,
            };
        }

        public static VegetationDef Vegetation(ZoneSystem.ZoneVegetation v, int index, EntrySourceDef source)
        {
            // m_prefab is a plain GameObject reference, not a SoftReference: the RNG key is the
            // object's own name, which is why it must be read at runtime and cannot come from the
            // SoftRef manifest.
            string prefabName = null;
            try { if (v.m_prefab != null) prefabName = v.m_prefab.name; }
            catch (Exception e) { Plugin.Log.LogWarning("Vegetation " + v.m_name + ": " + e.Message); }

            return new VegetationDef
            {
                index = index,
                source = source,
                name = v.m_name,
                prefabName = prefabName,
                nameHash = string.IsNullOrEmpty(prefabName) ? 0 : prefabName.GetStableHashCode(),
                enable = v.m_enable,
                min = v.m_min,
                max = v.m_max,
                forcePlacement = v.m_forcePlacement,
                scaleMin = v.m_scaleMin,
                scaleMax = v.m_scaleMax,
                randTilt = v.m_randTilt,
                chanceToUseGroundTilt = v.m_chanceToUseGroundTilt,
                biome = (int)v.m_biome,
                biomeArea = (int)v.m_biomeArea,
                blockCheck = v.m_blockCheck,
                snapToStaticSolid = v.m_snapToStaticSolid,
                minAltitude = v.m_minAltitude,
                maxAltitude = v.m_maxAltitude,
                minVegetation = v.m_minVegetation,
                maxVegetation = v.m_maxVegetation,
                surroundCheckVegetation = v.m_surroundCheckVegetation,
                surroundCheckDistance = v.m_surroundCheckDistance,
                surroundCheckLayers = v.m_surroundCheckLayers,
                surroundBetterThanAverage = v.m_surroundBetterThanAverage,
                minOceanDepth = v.m_minOceanDepth,
                maxOceanDepth = v.m_maxOceanDepth,
                minTilt = v.m_minTilt,
                maxTilt = v.m_maxTilt,
                terrainDeltaRadius = v.m_terrainDeltaRadius,
                maxTerrainDelta = v.m_maxTerrainDelta,
                minTerrainDelta = v.m_minTerrainDelta,
                snapToWater = v.m_snapToWater,
                groundOffset = v.m_groundOffset,
                groupSizeMin = v.m_groupSizeMin,
                groupSizeMax = v.m_groupSizeMax,
                groupRadius = v.m_groupRadius,
                minDistanceFromCenter = v.m_minDistanceFromCenter,
                maxDistanceFromCenter = v.m_maxDistanceFromCenter,
                inForest = v.m_inForest,
                forestTresholdMin = v.m_forestTresholdMin,
                forestTresholdMax = v.m_forestTresholdMax,
                foldout = v.m_foldout,
                altBiomeParent = v.AltBiomeParent,
            };
        }

        // ---- alt biomes ------------------------------------------------------------------------

        public static AltBiomeTableFile AltBiomes(string stamp)
        {
            List<AltBiome> all = AltBiomeList.m_altBiomes;
            var defs = new AltBiomeDef[all.Count];
            int enabled = 0;
            for (int i = 0; i < all.Count; i++)
            {
                defs[i] = AltBiome(all[i], i);
                if (all[i].m_enabled) enabled++;
            }
            return new AltBiomeTableFile
            {
                stamp = stamp,
                schema = DumpFormat.Schema,
                count = defs.Length,
                enabledCount = enabled,
                altBiomes = defs,
            };
        }

        public static AltBiomeDef AltBiome(AltBiome a, int index)
        {
            var addLoc = new LocationDef[a.m_addLocations != null ? a.m_addLocations.Count : 0];
            for (int i = 0; i < addLoc.Length; i++)
            {
                addLoc[i] = Location(a.m_addLocations[i], -1, -1, AltBiomeSource(a.m_name));
            }
            var addVeg = new VegetationDef[a.m_addVegetation != null ? a.m_addVegetation.Count : 0];
            for (int i = 0; i < addVeg.Length; i++)
            {
                addVeg[i] = Vegetation(a.m_addVegetation[i], -1, AltBiomeSource(a.m_name));
            }

            return new AltBiomeDef
            {
                index = index,
                name = a.m_name,
                nameHash = string.IsNullOrEmpty(a.m_name) ? 0 : a.m_name.GetStableHashCode(),
                enabled = a.m_enabled,
                biome = (int)a.m_biome,
                namePrefix = a.m_namePrefix,
                nameSuffix = a.m_nameSuffix,
                nameOverride = a.m_nameOverride,
                levelUpChanceMultiplier = a.m_levelUpChanceMultiplier,
                minDistanceFromCenter = a.m_minDistanceFromCenter,
                minAmountSpawned = a.m_minAmountSpawned,
                maxAmountSpawned = a.m_maxAmountSpawned,
                chance = a.m_chance,
                requireNeighbor = (int)a.m_requireNeighbor,
                notNeighbor = (int)a.m_notNeighbor,
                incompatibleAltBiomes = Strings(a.m_incompatibleAltBiomes),
                minEdgeSize = a.m_minEdgeSize,
                maxEdgeSize = a.m_maxEdgeSize,
                minAvgHeight = a.m_minAvgHeight,
                maxAvgHeight = a.m_maxAvgHeight,
                belowWorldX = a.m_belowWorldX,
                aboveWorldX = a.m_aboveWorldX,
                belowWorldY = a.m_belowWorldY,
                aboveWorldY = a.m_aboveWorldY,
                addLocations = addLoc,
                blockLocationNames = Strings(a.m_blockLocationNames),
                addVegetation = addVeg,
                blockVegetationNames = Strings(a.m_blockVegetationNames),
                forceMusic = a.m_forceMusic,
                forceEnvironment = a.m_forceEnvironment,
                blockEnvironments = Strings(a.m_blockEnvironments),
                blockSpawnNames = Strings(a.m_blockSpawnNames),
                addEnvironmentsCount = a.m_addEnvironments != null ? a.m_addEnvironments.Count : 0,
                spawnCount = a.m_spawn != null ? a.m_spawn.Count : 0,
                terrainTextureOverride = (int)a.m_terrainTextureOverride,
            };
        }

        // ---- prefab constants ------------------------------------------------------------------

        public static PrefabConstantsFile PrefabConstants(ZoneSystem zs, string stamp, out bool sortOrderTies)
        {
            return new PrefabConstantsFile
            {
                stamp = stamp,
                schema = DumpFormat.Schema,
                zoneSystem = ZoneSystemConstants(zs, out sortOrderTies),
                minimap = MinimapConstants(),
                heightmap = HeightmapConstants(zs),
            };
        }

        private static ZoneSystemConstantsDef ZoneSystemConstants(ZoneSystem zs, out bool sortOrderTies)
        {
            List<LocationList> lists = LocationList.GetAllLocationLists();
            var defs = new LocationListDef[lists.Count];
            var seenOrders = new Dictionary<int, int>();
            sortOrderTies = false;

            for (int i = 0; i < lists.Count; i++)
            {
                LocationList ll = lists[i];
                defs[i] = new LocationListDef
                {
                    name = ll.gameObject != null ? ll.gameObject.name : null,
                    sortOrder = ll.m_sortOrder,
                    locationCount = ll.m_locations != null ? ll.m_locations.Count : 0,
                    vegetationCount = ll.m_vegetation != null ? ll.m_vegetation.Count : 0,
                    firstLocationIndex = FirstIndexOf(zs.m_locations, ll.m_locations),
                    firstVegetationIndex = FirstIndexOf(zs.m_vegetation, ll.m_vegetation),
                };
                int seen;
                if (seenOrders.TryGetValue(ll.m_sortOrder, out seen)) sortOrderTies = true;
                else seenOrders[ll.m_sortOrder] = i;
            }

            var altListNames = new List<string>();
            if (zs.m_altBiomeLists != null)
            {
                foreach (AltBiomeList abl in zs.m_altBiomeLists)
                {
                    altListNames.Add(abl != null && abl.gameObject != null ? abl.gameObject.name : "(null)");
                }
            }

            int policy = 0;
            try { policy = (int)Settings.AssetMemoryUsagePolicy; }
            catch (Exception e) { Plugin.Log.LogWarning("Settings.AssetMemoryUsagePolicy unreadable: " + e.Message); }

            return new ZoneSystemConstantsDef
            {
                locationVersion = zs.m_locationVersion,
                waterLevel = zs.m_waterLevel,
                zoneSize = zs.m_zoneSize,
                zoneTTL = zs.m_zoneTTL,
                zoneTTS = zs.m_zoneTTS,
                locationScenes = zs.m_locationScenes != null ? zs.m_locationScenes.ToArray() : new string[0],
                locationLists = defs,
                sortOrderTies = sortOrderTies,
                altBiomeListNames = altListNames.ToArray(),
                assetMemoryUsagePolicy = policy,
                zoneCtrlPrefabPresent = zs.m_zoneCtrlPrefab != null ? 1 : 0,
                locationProxyPrefabPresent = zs.m_locationProxyPrefab != null ? 1 : 0,
            };
        }

        private static MinimapConstantsDef MinimapConstants()
        {
            // Minimap.instance is a raw ldsfld and can hand back a destroyed object: compare with
            // Unity's != null, never ReferenceEquals.
            Minimap m = Minimap.instance;
            if (m == null)
            {
                Plugin.Log.LogWarning("Minimap.instance is null; minimap constants omitted.");
                return null;
            }

            var def = new MinimapConstantsDef
            {
                textureSize = m.m_textureSize,
                pixelSize = m.m_pixelSize,
                exploreRadius = m.m_exploreRadius,
                exploreInterval = m.m_exploreInterval,
                removeRadius = m.m_removeRadius,
                meadowsColor = ToColorDef(m.m_meadowsColor),
                ashlandsColor = ToColorDef(m.m_ashlandsColor),
                blackforestColor = ToColorDef(m.m_blackforestColor),
                deepnorthColor = ToColorDef(m.m_deepnorthColor),
                heathColor = ToColorDef(m.m_heathColor),
                swampColor = ToColorDef(m.m_swampColor),
                mountainColor = ToColorDef(m.m_mountainColor),

                // Minimap.GetPixelColor returns Color.white for Ocean and its default arm is white too,
                // so Ocean, Mountain and DeepNorth all render #ffffff: the biome cache is LOSSY and a
                // white pixel must never be read as "Ocean".
                oceanColorHardcoded = ToColorDef(UnityEngine.Color.white),
            };

            // m_mistlandsColor is private with no [SerializeField] (Minimap.cs:274), so Unity does not
            // serialize it and the prefab cannot override it. Reflection is the only way to read it,
            // and its value is the code default by construction.
            try
            {
                var f = AccessTools.Field(typeof(Minimap), "m_mistlandsColor");
                if (f != null) def.mistlandsColor = ToColorDef((Color)f.GetValue(m));
            }
            catch (Exception e) { Plugin.Log.LogWarning("m_mistlandsColor unreadable: " + e.Message); }

            var icons = new List<string>();
            try
            {
                if (m.m_locationIcons != null)
                {
                    foreach (Minimap.LocationSpriteData d in m.m_locationIcons) icons.Add(d.m_name);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("m_locationIcons unreadable: " + e.Message); }
            def.locationIcons = icons.ToArray();

            return def;
        }

        private static HeightmapConstantsDef HeightmapConstants(ZoneSystem zs)
        {
            var def = new HeightmapConstantsDef
            {
                zoneWidth = -1,
                zoneScale = float.NaN,
                distantLodWidth = -1,
                distantLodScale = float.NaN,
                distantLodFound = false,
            };

            try
            {
                if (zs.m_zonePrefab != null)
                {
                    // The prefab is never instantiated by this read; GetComponentInChildren(true) also
                    // finds the component on an inactive child, which the zone prefab's is.
                    Heightmap hm = zs.m_zonePrefab.GetComponentInChildren<Heightmap>(true);
                    if (hm != null)
                    {
                        def.zoneWidth = hm.m_width;
                        def.zoneScale = hm.m_scale;
                        def.zoneIsDistantLod = hm.IsDistantLod;
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Zone Heightmap unreadable: " + e.Message); }

            // Best effort: the distant-LOD heightmap is not a ZoneSystem field, so the only way to see
            // it is to find a live one. It may legitimately not exist when the dump runs.
            try
            {
                foreach (Heightmap hm in UnityEngine.Object.FindObjectsByType<Heightmap>(
                             FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (hm != null && hm.IsDistantLod)
                    {
                        def.distantLodWidth = hm.m_width;
                        def.distantLodScale = hm.m_scale;
                        def.distantLodFound = true;
                        break;
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Distant-LOD Heightmap scan failed: " + e.Message); }

            return def;
        }

        // ---- helpers ---------------------------------------------------------------------------

        public static AssetIdDef AssetId(AssetID id)
        {
            return new AssetIdDef
            {
                v3 = id.v3,
                v2 = id.v2,
                v1 = id.v1,
                v0 = id.v0,
                hex = id.ToString(),
                isValid = id.IsValid,
            };
        }

        public static ColorDef ToColorDef(Color c)
        {
            // Color -> Color32 is round-to-nearest, HALF TO EVEN (UnityEngine.Color32.op_Implicit uses
            // Mathf.Round, which is System.Math.Round with MidpointRounding.ToEven). Doing it here means
            // the reader never has to guess which rounding produced the cache bytes.
            Color32 c32 = c;
            return new ColorDef
            {
                r = c.r,
                g = c.g,
                b = c.b,
                a = c.a,
                rgba32 = c32.r.ToString("x2") + c32.g.ToString("x2") + c32.b.ToString("x2") + c32.a.ToString("x2"),
            };
        }

        public static Vec2Def Vec2(Vector2 v) { return new Vec2Def { x = v.x, y = v.y }; }

        public static Vec3Def Vec3(Vector3 v) { return new Vec3Def { x = v.x, y = v.y, z = v.z }; }

        public static string[] Strings(List<string> list)
        {
            return list != null ? list.ToArray() : new string[0];
        }

        private static EntrySourceDef ZoneSystemPrefabSource()
        {
            return new EntrySourceDef { kind = "ZoneSystemPrefab", name = null, sortOrder = -1 };
        }

        private static EntrySourceDef AltBiomeSource(string name)
        {
            return new EntrySourceDef { kind = "AltBiome", name = name, sortOrder = -1 };
        }

        private static Dictionary<ZoneSystem.ZoneLocation, EntrySourceDef> LocationSources()
        {
            var map = new Dictionary<ZoneSystem.ZoneLocation, EntrySourceDef>();
            foreach (LocationList ll in LocationList.GetAllLocationLists())
            {
                if (ll == null || ll.m_locations == null) continue;
                var src = new EntrySourceDef
                {
                    kind = "LocationList",
                    name = ll.gameObject != null ? ll.gameObject.name : null,
                    sortOrder = ll.m_sortOrder,
                };
                foreach (ZoneSystem.ZoneLocation l in ll.m_locations)
                {
                    if (l != null && !map.ContainsKey(l)) map[l] = src;
                }
            }
            foreach (AltBiome a in AltBiomeList.m_altBiomes)
            {
                if (a == null || a.m_addLocations == null) continue;
                EntrySourceDef src = AltBiomeSource(a.m_name);
                foreach (ZoneSystem.ZoneLocation l in a.m_addLocations)
                {
                    if (l != null && !map.ContainsKey(l)) map[l] = src;
                }
            }
            return map;
        }

        private static Dictionary<ZoneSystem.ZoneVegetation, EntrySourceDef> VegetationSources()
        {
            var map = new Dictionary<ZoneSystem.ZoneVegetation, EntrySourceDef>();
            foreach (LocationList ll in LocationList.GetAllLocationLists())
            {
                if (ll == null || ll.m_vegetation == null) continue;
                var src = new EntrySourceDef
                {
                    kind = "LocationList",
                    name = ll.gameObject != null ? ll.gameObject.name : null,
                    sortOrder = ll.m_sortOrder,
                };
                foreach (ZoneSystem.ZoneVegetation v in ll.m_vegetation)
                {
                    if (v != null && !map.ContainsKey(v)) map[v] = src;
                }
            }
            foreach (AltBiome a in AltBiomeList.m_altBiomes)
            {
                if (a == null || a.m_addVegetation == null) continue;
                EntrySourceDef src = AltBiomeSource(a.m_name);
                foreach (ZoneSystem.ZoneVegetation v in a.m_addVegetation)
                {
                    if (v != null && !map.ContainsKey(v)) map[v] = src;
                }
            }
            return map;
        }

        private static int FirstIndexOf<T>(List<T> haystack, List<T> needleList) where T : class
        {
            if (haystack == null || needleList == null || needleList.Count == 0) return -1;
            T first = needleList[0];
            for (int i = 0; i < haystack.Count; i++)
            {
                if (ReferenceEquals(haystack[i], first)) return i;
            }
            return -1;
        }
    }
}
