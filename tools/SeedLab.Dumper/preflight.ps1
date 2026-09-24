# Preflight check for the SeedLab dumper plugin.
#
# It reads the BUILT DLL and the shipped game assemblies with Mono.Cecil. It does not launch the
# game, so it can only prove things that are visible in metadata and IL. What it does prove:
#
#   1. the plugin identifies itself as expected and is gated to valheim.exe
#   2. every [HarmonyPatch] target still exists in the shipped game assemblies
#   3. every game member the plugin reads - by name, by reflection, or directly - still exists,
#      which is the check that turns a Valheim rename into a failure here instead of into a
#      silently wrong dump
#   4. no method ANYWHERE in the assembly - including compiler-generated iterator state machines
#      and lambda closures, which are nested types and were NOT scanned before 2026-09-23 - calls
#      one of the 18 destructive file APIs, and every one of the 20 file-writing APIs is called
#      only from inside SeedLab.Dumper.DumpWriter (both lists are in this script, widened 2026-09-23)
#   5. every DumpWriter write path goes through Resolve -> AssertWritable -> PathPolicy.Reject, and
#      PathPolicy itself refuses every save-folder path when it is run against a table of them
#   6. the world-generator dump's restore of WorldGenerator.instance is inside a finally, and the
#      whole RandomGuard contract holds (added 2026-09-23 after the "aaaaaaaaaa" bug): the guard
#      is a reference type so an argumentless 'new' cannot compile, no 'initobj RandomGuard' is
#      emitted anywhere, RandomStateSafe.Restore is the assembly's ONLY writer of
#      UnityEngine.Random.state and it refuses an all-zero state, NoDrawCheck.Around restores
#      through it inside a finally, no iterator state machine hoists a guard or writes the state
#      (the "never span a yield" rule, mechanised), and every dump entry point refuses to start
#      on an already-dead generator
#   7. every assembly the plugin references resolves from the game folder
#   8. every public instance field of the generation classes (DungeonGenerator, DoorDef, Room,
#      RoomConnection, RandomSpawn, RandomObject, ObjectEntry) is LOADED somewhere in the plugin's IL
#      or waived here with a reason - a tripwire against a field nobody listed. It proves the IL
#      mentions the field, NOT that the value reaches the dump.
#
# What it does NOT prove: that a dump is CORRECT, that the plugin behaves at runtime, or that
# reflection by a name this script does not list still resolves. It is a gate against renames and
# against unsafe file code, nothing more.
#
#   powershell -ExecutionPolicy Bypass -File preflight.ps1
#   powershell -ExecutionPolicy Bypass -File preflight.ps1 -SelfTest    # prove the tripwires fire
#
# The game folder is -ValheimDir, else SEEDLAB_VALHEIM_DIR, else found by walking up from the
# current directory and from this script (the repository kept inside the game folder), else in the
# usual Steam library folders - the same search tools\check-game-version.ps1 makes. The game must
# have BepInEx installed: Mono.Cecil is loaded from its BepInEx\core.

param(
    [string]$ValheimDir = "",
    [string]$Plugin = "",
    [switch]$SelfTest
)

$ErrorActionPreference = "Stop"

function Test-GameDir([string]$dir) {
    if (-not $dir) { return $false }
    try { return [IO.File]::Exists((Join-Path $dir "valheim_Data\Managed\assembly_valheim.dll")) } catch { return $false }
}

if ($ValheimDir -eq "" -and $env:SEEDLAB_VALHEIM_DIR) { $ValheimDir = $env:SEEDLAB_VALHEIM_DIR }
if ($ValheimDir -eq "") {
    $starts = @()
    try { $starts += (Get-Location).ProviderPath } catch { }
    if ($PSScriptRoot) { $starts += $PSScriptRoot }
    foreach ($start in $starts) {
        $d = $start
        for ($i = 0; $i -lt 12 -and $d -and $ValheimDir -eq ""; $i++) {
            if (Test-GameDir $d) { $ValheimDir = $d }
            $d = Split-Path $d -Parent
        }
        if ($ValheimDir -ne "") { break }
    }
}
if ($ValheimDir -eq "") {
    $roots = @("C:\Program Files (x86)\Steam", "C:\Program Files\Steam")
    foreach ($code in 68..90) { $roots += ([string][char]$code + ":\SteamLibrary"); $roots += ([string][char]$code + ":\Steam") }
    foreach ($root in $roots) {
        $cand = Join-Path $root "steamapps\common\Valheim"
        if (Test-GameDir $cand) { $ValheimDir = $cand; break }
    }
}
if (-not (Test-GameDir $ValheimDir)) {
    Write-Output "FAIL  Valheim not found (valheim_Data\Managed\assembly_valheim.dll)."
    Write-Output "      pass -ValheimDir <game folder> or set SEEDLAB_VALHEIM_DIR"
    exit 1
}
if (-not [IO.File]::Exists((Join-Path $ValheimDir "BepInEx\core\Mono.Cecil.dll"))) {
    Write-Output "FAIL  $ValheimDir has no BepInEx\core\Mono.Cecil.dll - the preflight needs BepInEx installed in the game."
    exit 1
}

$managed = Join-Path $ValheimDir "valheim_Data\Managed"
$core = Join-Path $ValheimDir "BepInEx\core"
if ($Plugin -eq "") { $Plugin = Join-Path $PSScriptRoot "build\SeedLab.Dumper.dll" }

Add-Type -Path (Join-Path $core "Mono.Cecil.dll")

if (-not (Test-Path $Plugin)) {
    Write-Output "FAIL  plugin not found: $Plugin"
    Write-Output "      build it first:  dotnet build tools\SeedLab.Dumper\SeedLab.Dumper.csproj"
    exit 1
}
Write-Output "Checking $Plugin"
Write-Output ""

$modules = @{}
foreach ($f in (Get-ChildItem $managed -Filter *.dll) + (Get-ChildItem $core -Filter *.dll)) {
    try { $modules[$f.FullName] = [Mono.Cecil.ModuleDefinition]::ReadModule($f.FullName) } catch { }
}

# Every type in the assembly, INCLUDING nested ones. C# hides a lot of code in nested types: each
# iterator method becomes a <Name>d__N state machine and each lambda that captures becomes a
# <>c__DisplayClass, both nested inside the declaring type. ModuleDefinition.Types lists only
# top-level types, so a scan built on it sees neither - as built on 2026-09-23 that is 34 of the
# assembly's 61 types, among them the bodies of ModeAssets.Run, ModeNatives.Run, ModeWorldGen.Run,
# Plugin.Drive and DumpWriter's streaming writer. Anything that walks code must walk THIS list.
# The counts move with every build, so this script PRINTS the ones it actually found rather than
# asking anyone to trust the number in this comment.
function Get-AllTypes($type) {
    $out = @($type)
    foreach ($n in $type.NestedTypes) { $out += Get-AllTypes $n }
    return $out
}

# The outermost declaring type: SeedLab.Dumper.DumpWriter/<WriteBinaryStreaming>d__17 is DumpWriter's
# own code, so ownership questions ("is this write inside DumpWriter?") must be asked of the root,
# not of the nested type, or the recursion would flag DumpWriter's own streaming writer as a bypass.
function Get-RootType($type) {
    $t = $type
    while ($t.DeclaringType) { $t = $t.DeclaringType }
    return $t
}

function Find-GameType([string]$name) {
    foreach ($m in $modules.Values) {
        $t = $m.GetType($name)
        if ($t) { return $t }
    }
    return $null
}

$failures = 0
$checks = 0

function Check-Field([string]$typeName, [string]$fieldName) {
    $script:checks++
    $t = Find-GameType $typeName
    if (-not $t) {
        Write-Output ("FAIL  type not found: {0}" -f $typeName)
        $script:failures++
        return
    }
    $f = $t.Fields | Where-Object { $_.Name -eq $fieldName }
    if (-not $f) {
        Write-Output ("FAIL  {0}.{1} no longer exists" -f $typeName, $fieldName)
        $script:failures++
    }
}

function Check-Member([string]$typeName, [string]$memberName) {
    $script:checks++
    $t = Find-GameType $typeName
    if (-not $t) {
        Write-Output ("FAIL  type not found: {0}" -f $typeName)
        $script:failures++
        return
    }
    $hit = $t.Fields | Where-Object { $_.Name -eq $memberName }
    if (-not $hit) { $hit = $t.Methods | Where-Object { $_.Name -eq $memberName } }
    if (-not $hit) { $hit = $t.Properties | Where-Object { $_.Name -eq $memberName } }
    if (-not $hit) {
        Write-Output ("FAIL  {0}.{1} no longer exists" -f $typeName, $memberName)
        $script:failures++
    }
}

function Check-Method([string]$typeName, [string]$methodName, [int]$paramCount) {
    $script:checks++
    $t = Find-GameType $typeName
    if (-not $t) {
        Write-Output ("FAIL  type not found: {0}" -f $typeName)
        $script:failures++
        return
    }
    $m = $t.Methods | Where-Object { $_.Name -eq $methodName -and $_.Parameters.Count -eq $paramCount }
    if (-not $m) {
        Write-Output ("FAIL  {0}.{1}({2} args) no longer exists" -f $typeName, $methodName, $paramCount)
        $script:failures++
    }
}

# How many times one shipped game method calls another. Used for the arithmetic claims that member
# names alone cannot protect: "one Random draw per RandomSpawn", "GetWeightedObject runs before the
# gates". $needle is a wildcard pattern matched against "Namespace.Type::Method".
# $paramCount disambiguates an OVERLOADED method. Without it this picks whichever overload Cecil
# happens to list first, and DungeonGenerator.PlaceRoom has two - a 3-argument one that only computes
# a position and a 5-argument one that owns the whole room RNG stream. Counting the wrong one would
# make every arithmetic claim about rooms a claim about a different method. -1 means "don't care",
# which is right for the methods that have exactly one overload.
function Count-Calls([string]$typeName, [string]$methodName, [string]$needle, [int]$paramCount = -1,
                     [string]$firstParam = "") {
    $t = Find-GameType $typeName
    if (-not $t) { return -1 }
    $cands = @($t.Methods | Where-Object { $_.Name -eq $methodName -and $_.HasBody })
    if ($paramCount -ge 0) { $cands = @($cands | Where-Object { $_.Parameters.Count -eq $paramCount }) }
    # Argument COUNT is not always enough: Utils.GetPrefabName has two one-argument overloads, and the
    # GameObject one is a single forwarding call to the string one. Picking it would make the
    # truncation assertions below pass against a method that does no truncating.
    if ($firstParam -ne "") {
        $cands = @($cands | Where-Object {
            $_.Parameters.Count -gt 0 -and $_.Parameters[0].ParameterType.FullName -eq $firstParam })
    }
    $m = $cands | Select-Object -First 1
    if (-not $m) { return -1 }
    $n = 0
    foreach ($i in $m.Body.Instructions) {
        $op = $i.Operand
        if (-not $op) { continue }
        if (-not ($op -is [Mono.Cecil.MethodReference])) { continue }
        $sig = "{0}::{1}" -f $op.DeclaringType.FullName, $op.Name
        if ($sig -like $needle) { $n++ }
    }
    return $n
}

function Check-CallCount([string]$typeName, [string]$methodName, [string]$needle, [int]$expected,
                        [string]$why, [int]$paramCount = -1, [string]$firstParam = "") {
    $script:checks++
    $n = Count-Calls $typeName $methodName $needle $paramCount $firstParam
    if ($n -lt 0) {
        Write-Output ("FAIL  {0}.{1}{2} not found, so its call count could not be checked" -f
                      $typeName, $methodName, $(if ($paramCount -ge 0) { "($paramCount args)" } else { "" }))
        $script:failures++
        return
    }
    if ($n -ne $expected) {
        Write-Output ("FAIL  {0}.{1} makes {2} call(s) to {3}, expected {4}. {5}" -f
                      $typeName, $methodName, $n, $needle, $expected, $why)
        $script:failures++
    }
}

# --------------------------------------------------------------------------------------------------
Write-Output "== plugin identity =="
$plug = [Mono.Cecil.ModuleDefinition]::ReadModule($Plugin)
$topTypes = @($plug.Types)
$allTypes = @()
foreach ($t in $topTypes) { $allTypes += Get-AllTypes $t }
Write-Output ("      {0} top-level types, {1} types in all (nested included)." -f $topTypes.Count, $allTypes.Count)
$pluginType = $plug.GetType("SeedLab.Dumper.Plugin")
$checks++
if (-not $pluginType) {
    Write-Output "FAIL  SeedLab.Dumper.Plugin not found in the assembly"
    $failures++
} else {
    $guid = $null; $name = $null; $version = $null; $process = $null
    foreach ($ca in $pluginType.CustomAttributes) {
        if ($ca.AttributeType.Name -eq "BepInPlugin") {
            $guid = $ca.ConstructorArguments[0].Value
            $name = $ca.ConstructorArguments[1].Value
            $version = $ca.ConstructorArguments[2].Value
        }
        if ($ca.AttributeType.Name -eq "BepInProcess") {
            $process = $ca.ConstructorArguments[0].Value
        }
    }
    Write-Output ("      GUID={0} name={1} version={2} process={3}" -f $guid, $name, $version, $process)
    if ($guid -ne "DoomMachine.SeedLabDumper") { Write-Output "FAIL  unexpected GUID"; $failures++ }
    if ($process -ne "valheim.exe") { Write-Output "FAIL  BepInProcess is not valheim.exe"; $failures++ }
}

# --------------------------------------------------------------------------------------------------
Write-Output ""
Write-Output "== Harmony patch targets =="
foreach ($t in $allTypes) {
    foreach ($ca in $t.CustomAttributes) {
        if ($ca.AttributeType.Name -ne "HarmonyPatch") { continue }
        if ($ca.ConstructorArguments.Count -lt 2) { continue }
        $targetType = $ca.ConstructorArguments[0].Value
        $targetMember = $ca.ConstructorArguments[1].Value
        if (-not $targetType) { continue }
        $tn = $targetType.FullName
        Write-Output ("      {0} -> {1}.{2}" -f $t.Name, $tn, $targetMember)
        Check-Member $tn $targetMember
    }
}

# --------------------------------------------------------------------------------------------------
Write-Output ""
Write-Output "== game members the dumper reads =="

# ZoneSystem.ZoneLocation - all 40 serialized fields, in declaration order, plus the runtime extras.
$zoneLocationFields = @(
    "m_name", "m_enable", "m_prefabName", "m_prefab", "m_biome", "m_biomeArea", "m_quantity",
    "m_prioritized", "m_centerFirst", "m_unique", "m_group", "m_minDistanceFromSimilar", "m_groupMax",
    "m_maxDistanceFromSimilar", "m_iconAlways", "m_iconPlaced", "m_randomRotation", "m_slopeRotation",
    "m_snapToWater", "m_interiorRadius", "m_exteriorRadius", "m_clearArea", "m_minTerrainDelta",
    "m_maxTerrainDelta", "m_minimumVegetation", "m_maximumVegetation", "m_surroundCheckVegetation",
    "m_surroundCheckDistance", "m_surroundCheckLayers", "m_surroundBetterThanAverage", "m_inForest",
    "m_forestTresholdMin", "m_forestTresholdMax", "m_minDistanceFromCenter", "m_maxDistanceFromCenter",
    "m_minDistance", "m_maxDistance", "m_minAltitude", "m_maxAltitude", "m_foldout"
)
if ($zoneLocationFields.Count -ne 40) {
    Write-Output ("FAIL  the ZoneLocation field list in this script has {0} entries, not 40" -f $zoneLocationFields.Count)
    $failures++
}
foreach ($f in $zoneLocationFields) { Check-Field "ZoneSystem/ZoneLocation" $f }
Check-Member "ZoneSystem/ZoneLocation" "AltBiomeParent"
Check-Member "ZoneSystem/ZoneLocation" "Hash"

# ZoneSystem.ZoneVegetation - all 40 serialized fields (the count is 40, not 38: m_prefab counts).
$zoneVegFields = @(
    "m_name", "m_prefab", "m_enable", "m_min", "m_max", "m_forcePlacement", "m_scaleMin", "m_scaleMax",
    "m_randTilt", "m_chanceToUseGroundTilt", "m_biome", "m_biomeArea", "m_blockCheck",
    "m_snapToStaticSolid", "m_minAltitude", "m_maxAltitude", "m_minVegetation", "m_maxVegetation",
    "m_surroundCheckVegetation", "m_surroundCheckDistance", "m_surroundCheckLayers",
    "m_surroundBetterThanAverage", "m_minOceanDepth", "m_maxOceanDepth", "m_minTilt", "m_maxTilt",
    "m_terrainDeltaRadius", "m_maxTerrainDelta", "m_minTerrainDelta", "m_snapToWater", "m_groundOffset",
    "m_groupSizeMin", "m_groupSizeMax", "m_groupRadius", "m_minDistanceFromCenter",
    "m_maxDistanceFromCenter", "m_inForest", "m_forestTresholdMin", "m_forestTresholdMax", "m_foldout"
)
if ($zoneVegFields.Count -ne 40) {
    Write-Output ("FAIL  the ZoneVegetation field list in this script has {0} entries, not 40" -f $zoneVegFields.Count)
    $failures++
}
foreach ($f in $zoneVegFields) { Check-Field "ZoneSystem/ZoneVegetation" $f }
Check-Member "ZoneSystem/ZoneVegetation" "AltBiomeParent"

# ZoneSystem scalars and lists.
foreach ($f in @("m_locationVersion", "m_waterLevel", "m_zoneSize", "m_zoneTTL", "m_zoneTTS",
                 "m_locationScenes", "m_locationLists", "m_altBiomeLists", "m_vegetation", "m_locations",
                 "m_locationInstances", "m_zonePrefab", "m_zoneCtrlPrefab", "m_locationProxyPrefab")) {
    Check-Field "ZoneSystem" $f
}

# AltBiome / AltBiomeList / BiomeSector / BiomeTypeInfo.
foreach ($f in @("m_name", "m_enabled", "m_biome", "m_namePrefix", "m_nameSuffix", "m_nameOverride",
                 "m_levelUpChanceMultiplier", "m_forceMusic", "m_forceEnvironment", "m_addEnvironments",
                 "m_blockEnvironments", "m_spawn", "m_blockSpawnNames", "m_addVegetation",
                 "m_blockVegetationNames", "m_addLocations", "m_blockLocationNames",
                 "m_terrainTextureOverride", "m_minDistanceFromCenter", "m_minAmountSpawned",
                 "m_maxAmountSpawned", "m_chance", "m_requireNeighbor", "m_notNeighbor",
                 "m_incompatibleAltBiomes", "m_minEdgeSize", "m_maxEdgeSize", "m_minAvgHeight",
                 "m_maxAvgHeight", "m_belowWorldX", "m_aboveWorldX", "m_belowWorldY", "m_aboveWorldY",
                 "Sectors", "ValidPlacementSectors", "ValidPlacementSectorCombos")) {
    Check-Field "AltBiome" $f
}
Check-Field "AltBiomeList" "m_altBiomes"
Check-Field "AltBiomeList" "m_alts"
foreach ($f in @("Biome", "EdgeCount", "Neighbors", "Center", "Min", "Max", "MinZone", "MaxZone",
                 "HeightMin", "HeightMax", "HeightAvg", "IsDiscovered", "DistanceFromCenter", "AltBiomes")) {
    Check-Field "BiomeSector" $f
}
foreach ($f in @("Biome", "Sectors", "AllPoints", "AllPointsAboveSeaLevel")) {
    Check-Field "BiomeTypeInfo" $f
}
foreach ($f in @("Biomes", "Sectors", "PointsGenerated", "SectorsCalculated", "Size")) {
    Check-Field "AltBiomeWorldData" $f
}
foreach ($f in @("c_textureSize", "c_pixelSize", "c_halfWidth", "c_halfPixel")) {
    Check-Field "AltBiomeWorldData" $f
}

# WorldGenerator: the private state the dump is made of, plus the private GetBaseHeight it samples.
foreach ($f in @("m_offset0", "m_offset1", "m_offset2", "m_offset3", "m_offset4", "m_riverSeed",
                 "m_streamSeed", "m_lakes", "m_rivers", "m_streams", "m_riverPoints", "m_version",
                 "m_world", "m_noiseGen", "m_minMountainDistance", "minDarklandNoise", "maxMarshDistance")) {
    Check-Field "WorldGenerator" $f
}
Check-Method "WorldGenerator" "GetBaseHeight" 3
Check-Method "WorldGenerator" "WorldAngle" 2
Check-Method "WorldGenerator" "Initialize" 1
Check-Method "WorldGenerator" "GetSeed" 0
Check-Member "WorldGenerator" "instance"
foreach ($f in @("p0", "p1", "center", "widthMin", "widthMax", "curveWidth", "curveWavelength")) {
    Check-Field "WorldGenerator/River" $f
}
foreach ($f in @("p", "w", "w2")) { Check-Field "WorldGenerator/RiverPoint" $f }
Check-Method "FastNoise" "GetSeed" 0

# World.
foreach ($f in @("m_name", "m_seedName", "m_seed", "m_uid", "m_worldGenVersion", "m_worldVersion",
                 "m_menu", "m_biomeData")) {
    Check-Field "World" $f
}
Check-Method "World" "GetMenuWorld" 0

# Minimap: the eight colour fields (m_mistlandsColor is private and reached by reflection) and geometry.
foreach ($f in @("m_textureSize", "m_pixelSize", "m_exploreRadius", "m_exploreInterval", "m_removeRadius",
                 "m_locationIcons", "m_meadowsColor", "m_ashlandsColor", "m_blackforestColor",
                 "m_deepnorthColor", "m_heathColor", "m_swampColor", "m_mountainColor", "m_mistlandsColor")) {
    Check-Field "Minimap" $f
}
Check-Field "Minimap/LocationSpriteData" "m_name"

# Heightmap, LocationList, Location, DungeonGenerator, Settings.
Check-Field "Heightmap" "m_width"
Check-Field "Heightmap" "m_scale"
Check-Member "Heightmap" "IsDistantLod"
foreach ($f in @("m_sortOrder", "m_locations", "m_vegetation")) { Check-Field "LocationList" $f }
Check-Method "LocationList" "GetAllLocationLists" 0
foreach ($f in @("m_exteriorRadius", "m_interiorRadius", "m_hasInterior", "m_noBuild",
                 "m_noBuildRadiusOverride", "m_clearArea", "m_discoverLabel",
                 "m_useCustomInteriorTransform", "m_biome")) {
    Check-Field "Location" $f
}
foreach ($f in @("m_useCustomInteriorTransform", "m_algorithm", "m_minRooms", "m_maxRooms", "m_themes",
                 "m_campRadiusMin", "m_campRadiusMax")) {
    Check-Field "DungeonGenerator" $f
}
Check-Field "Settings" "AssetMemoryUsagePolicy"

# ---- the prefab-child walk (locationchildren.json) -------------------------------------------------
# Everything below is read by SeedLab.Dumper.ChildWalk off a loaded location prefab. A rename in a
# future Valheim would otherwise produce a file full of zeros that LOOKS like data: a RandomSpawn with
# chanceToSpawn 0 reads as "never spawns", and an empty drop table reads as "this chest holds nothing".
# Those are the two worst possible silent failures for what this file is used for, so every member
# named in ChildWalk.cs is listed here.
foreach ($f in @("m_OffObject", "m_chanceToSpawn", "m_dungeonRequireTheme", "m_requireBiome",
                 "m_notInLava", "m_minElevation", "m_maxElevation")) {
    Check-Field "RandomSpawn" $f
}
# Randomize's signature is load-bearing, not decorative: SpawnLocation calls Randomize(pos, location)
# with the DungeonGenerator argument defaulted, which is why m_dungeonRequireTheme never gates a
# location's own RandomSpawns. Prepare is listed because ChildWalk REPRODUCES its query read-only and
# must be re-read if it changes.
Check-Method "RandomSpawn" "Randomize" 3
Check-Method "RandomSpawn" "Prepare" 0
Check-Method "RandomSpawn" "Reset" 0

foreach ($f in @("m_objects", "m_dungeonRequireTheme", "m_requireBiome", "m_notInLava",
                 "m_minElevation", "m_maxElevation")) {
    Check-Field "RandomObject" $f
}
Check-Method "RandomObject" "Randomize" 3
Check-Method "RandomObject" "GetWeightedObject" 0
foreach ($f in @("m_object", "m_weight")) { Check-Field "RandomObject/ObjectEntry" $f }

foreach ($f in @("m_name", "m_width", "m_height", "m_autoDestroyEmpty", "m_defaultItems",
                 "m_privacy", "m_checkGuardStone")) {
    Check-Field "Container" $f
}
Check-Member "Container/PrivacySetting" "Public"

foreach ($f in @("m_drops", "m_dropMin", "m_dropMax", "m_dropChance", "m_oneOfEach")) {
    Check-Field "DropTable" $f
}
foreach ($f in @("m_item", "m_stackMin", "m_stackMax", "m_weight", "m_dontScale")) {
    Check-Field "DropTable/DropData" $f
}

# The two Utils helpers ChildWalk calls rather than reimplements, so the ORDER of the RandomSpawn and
# RandomObject arrays is the game's own by construction.
Check-Method "Utils" "GetEnabledComponentsInChildren" 1
Check-Method "Utils" "IsEnabledInheirarcy" 2

# Utils.GetPrefabName IS the game's identity for a GameObject, and SeedLab.Dumper.Names calls it for
# every index and every search in the dump. Both overloads are listed: the plugin calls the string one,
# and the GameObject one is what the game itself calls, so a divergence between them would matter.
# extraCharacters is the truncation set - private, never touched by the plugin, listed because Names.cs
# carries a fallback copy of it and a change there must fail the build rather than quietly produce a
# different answer than the game's.
Check-Method "Utils" "GetPrefabName" 1
Check-Field "Utils" "extraCharacters"

# ---- the room walk (roomchildren.json) -------------------------------------------------------------
# Everything below is read by SeedLab.Dumper.RoomWalk and ChildWalk.ReadRoom. A rename here would
# produce a roomchildren.json full of zeros, and - worse - a search.json that says NOT FOUND for a
# piece that is sitting in a crypt.
foreach ($f in @("m_roomScenes", "m_roomLists", "m_rooms", "m_roomByHash")) {
    Check-Field "DungeonDB" $f
}
Check-Member "DungeonDB" "instance"
Check-Method "DungeonDB" "GetRooms" 0
Check-Method "DungeonDB" "GetRoom" 1
# SetupRooms and GenerateHashList are not called by the plugin. They are checked because
# roomchildren.json's own documentation states what they do - concatenate the room lists, and drop a
# duplicate hash - and a signature change is the signal to re-read them.
Check-Method "DungeonDB" "SetupRooms" 0
Check-Method "DungeonDB" "GenerateHashList" 0
foreach ($f in @("m_prefab", "m_enabled", "m_theme")) { Check-Field "DungeonDB/RoomData" $f }
Check-Member "DungeonDB/RoomData" "Hash"
# RoomInPrefab is listed so that a future edit to RoomWalk cannot reach for it without noticing it
# exists: it caches into the RoomData's private m_loadedRoom on a SHARED object, which is exactly what
# this walk must not do.
Check-Member "DungeonDB/RoomData" "RoomInPrefab"

Check-Field "RoomList" "m_rooms"
Check-Method "RoomList" "GetAllRoomLists" 0

# All 13 serialized Room fields, in declaration order. roomchildren.json writes every one of them.
$roomFields = @("m_size", "m_theme", "m_enabled", "m_entrance", "m_endCap", "m_divider",
                "m_endCapPrio", "m_minPlaceOrder", "m_weight", "m_faceCenter", "m_perimeter",
                "m_placeOrder", "m_seed")
if ($roomFields.Count -ne 13) {
    Write-Output ("FAIL  the Room field list in this script has {0} entries, not 13" -f $roomFields.Count)
    $failures++
}
foreach ($f in $roomFields) { Check-Field "Room" $f }

# The generator side of the room story: which rooms a dungeon can pick, and whether its base seed is
# folded into each room's own RandomSpawn stream.
foreach ($f in @("m_minRequiredRooms", "m_requiredRooms", "m_addBaseSeedToRandomSpawn",
                 "m_availableRooms", "m_loadedRooms")) {
    Check-Field "DungeonGenerator" $f
}
Check-Method "DungeonGenerator" "SetupAvailableRooms" 0
Check-Method "DungeonGenerator" "GenerateRooms" 1
Check-Method "DungeonGenerator" "GetSeed" 0
Check-Method "DungeonGenerator" "LoadRoomPrefabsAsync" 0
Check-Method "DungeonGenerator" "GetCampRoomRotation" 2

Write-Output ""
Write-Output "== field coverage: every public instance field of the generation classes is read or waived =="

# WHY THIS GATE EXISTS (2026-09-23). The dump's DungeonGenerator record was built by asking "what does
# the feature we are writing need?" and it answered with 14 fields. The generator actually reads 23:
# m_perimeterBuffer changes the value of every radial draw, m_maxTilt and m_minAltitude reject
# candidates outright, and m_zoneSize bounds them - and each one's C# default is NON-ZERO, so a reader
# that filled the gaps with zeros would have reproduced a generator the game never runs, silently and
# plausibly. Room.m_connections was missing outright, which is the whole Dungeon algorithm.
#
# The rule this encodes: field coverage is a DIFF against the type's serialized fields, never against
# what a feature happened to need. Every public instance field of these classes must be read somewhere
# in the plugin, or be waived HERE with a reason. A game update that adds a field fails this gate
# instead of quietly widening the blind spot.
#
# WHAT THIS GATE PROVES, AND WHAT IT DOES NOT. It proves the plugin's IL contains an instance-field
# LOAD of the field somewhere. It does NOT prove that load is reachable, nor that the value reaches a
# DTO - a read in dead code would satisfy it. That is worth knowing rather than glossing: the gate is
# a tripwire against a field nobody listed, not a proof of capture. Only ldfld/ldflda count; stfld and
# the static opcodes are not reads, and a FieldReference operand alone would have accepted them.
#
# The set is PUBLIC INSTANCE fields, which is deliberately wider than "serialized": Room.m_placeOrder
# and Room.m_seed are [NonSerialized] and the dump records them anyway (as the AUTHORED values, which
# is a real fact about the prefab), so excluding them would drop two fields the dump does carry.
# [NonSerialized] is a field FLAG (FieldAttributes.NotSerialized), never a custom attribute - a scan
# that looks in CustomAttributes for it finds nothing and silently reports zero.
$fieldReadOpcodes = @('ldfld', 'ldflda')
$fieldReads = @{}
foreach ($t in $allTypes) {
    foreach ($m in $t.Methods) {
        if (-not $m.HasBody) { continue }
        foreach ($i in $m.Body.Instructions) {
            $op = $i.Operand
            if (($op -is [Mono.Cecil.FieldReference]) -and ($fieldReadOpcodes -contains $i.OpCode.Name)) {
                $fieldReads[("{0}::{1}" -f $op.DeclaringType.FullName, $op.Name)] = $true
            }
        }
    }
}

# Waived, with the reason. A waiver says "read deliberately omitted", never "not needed yet".
$fieldWaivers = @{
    "DungeonGenerator::m_generatedSeed" =
        "[HideInInspector] runtime output of GetSeed(), not an input; the offline port recomputes it"
    "Room::m_musicPrefab" =
        "audio only - Awake instantiates a MusicVolume; it draws nothing and places nothing"
    "RoomConnection::m_placeOrder" =
        "[NonSerialized] - AddOpenConnections writes it on the placed instance at generation time"
    "Teleport::m_hoverOffset" =
        "hover-UI placement only (GetHoverOffset); it names nothing and moves nothing"
    "Vegvisir::m_hoverOffset" =
        "hover-UI placement only (GetHoverOffset); it names nothing and reveals nothing"
}

function Check-FieldCoverage([string]$typeName) {
    $t = Find-GameType $typeName
    $script:checks++
    if (-not $t) {
        Write-Output ("FAIL  type not found: {0}" -f $typeName)
        $script:failures++
        return
    }
    $missing = @()
    $waived = 0
    $read = 0
    foreach ($f in $t.Fields) {
        if ($f.IsStatic -or -not $f.IsPublic) { continue }
        $key = "{0}::{1}" -f $t.FullName, $f.Name
        if ($fieldWaivers.ContainsKey($key)) { $waived++; continue }
        if ($fieldReads.ContainsKey($key)) { $read++; continue }
        $missing += $f.Name
    }
    if ($missing.Count -gt 0) {
        Write-Output ("FAIL  {0}: the dump never reads {1}" -f $typeName, ($missing -join ", "))
        Write-Output ("      Add it to the dump, or waive it above with the reason it cannot matter.")
        $script:failures++
    } else {
        Write-Output ("      {0}: {1} field(s) read, {2} waived." -f $typeName, $read, $waived)
    }

    # A private field carrying [SerializeField] IS serialized and would sit outside the public filter
    # above - invisible to this gate while being exactly the kind of field it exists to catch. There
    # are none on these types in 1.0.15, which is why the two definitions look identical today.
    $script:checks++
    $hidden = @()
    foreach ($f in $t.Fields) {
        if ($f.IsStatic -or $f.IsPublic) { continue }
        foreach ($ca in $f.CustomAttributes) {
            if ($ca.AttributeType.FullName -eq 'UnityEngine.SerializeField') { $hidden += $f.Name }
        }
    }
    if ($hidden.Count -gt 0) {
        Write-Output ("FAIL  {0}: private [SerializeField] field(s) this gate does not cover: {1}" `
                      -f $typeName, ($hidden -join ", "))
        Write-Output ("      They are serialized, so widen the filter and account for them.")
        $script:failures++
    }
}

foreach ($tn in @("DungeonGenerator", "DungeonGenerator/DoorDef", "Room", "RoomConnection",
                  "RandomSpawn", "RandomObject", "RandomObject/ObjectEntry",
                  "Teleport", "Vegvisir", "Vegvisir/VegvisrLocation")) {
    Check-FieldCoverage $tn
}

# A waiver for a field that no longer exists is a stale waiver, and a stale waiver hides the next gap.
foreach ($key in $fieldWaivers.Keys) {
    $checks++
    $parts = $key -split "::"
    $t = Find-GameType $parts[0]
    if (-not $t -or -not ($t.Fields | Where-Object { $_.Name -eq $parts[1] })) {
        Write-Output ("FAIL  stale waiver: {0} no longer exists - remove it" -f $key)
        $failures++
    }
}

Write-Output ""
# The custom interior transform: the two references SpawnLocation moves before the Randomize loop.
# ChildWalk flags every entry underneath them, because their prefabPosition is NOT what the game reads.
Check-Field "Location" "m_interiorTransform"
Check-Field "Location" "m_generator"

# The waymark walk (2026-09-24): a dungeon's name is Teleport.m_enterText, which Teleport.Interact hands
# to MessageHud.ShowBiomeFoundMsg; a Vegvisir's entries are the pins it writes for the places it reveals.
# ChildWalk.ReadWaymarks reads every field below; the coverage gate above catches one added later.
foreach ($f in @("m_hoverText", "m_enterText", "m_targetPoint")) { Check-Field "Teleport" $f }
foreach ($f in @("m_name", "m_hoverName", "m_useText", "m_setsGlobalKey", "m_setsPlayerKey", "m_locations")) {
    Check-Field "Vegvisir" $f
}
foreach ($f in @("m_locationName", "m_pinName", "m_pinType", "m_discoverAll", "m_showMap")) {
    Check-Field "Vegvisir/VegvisrLocation" $f
}
Check-Method "Teleport" "Interact" 3
Check-Method "Vegvisir" "Interact" 3

# The existence probe that separates "the name is wrong" from "the piece exists but nothing places it".
Check-Member "ZNetScene" "instance"
Check-Method "ZNetScene" "GetPrefab" 1

# The methods the offline algorithm is derived from. They are not called by the plugin; they are
# checked because docs\studies\dumper-children-notes.md and locationchildren.json's own documentation
# quote them, and a signature change is the signal to re-read them.
Check-Method "ZoneSystem" "SpawnLocation" 7
Check-Method "ZoneSystem" "PlaceLocations" 7
Check-Method "ZoneSystem" "GetGroundData" 5
Check-Method "ZoneSystem" "IsLavaPreHeightmap" 2
Check-Member "Room/Theme" "None"
Check-Member "ZNetView" "GetZDO"

# ZNet - the refusal checks.
foreach ($m in @("IsServer", "IsDedicated", "GetPeers", "GetPeerConnections")) { Check-Member "ZNet" $m }
Check-Member "ZNet" "World"

# Version constants.
foreach ($f in @("c_networkVersion", "c_WorldGenVersion", "c_PlayerVersion", "c_WorldVersion",
                 "c_CachedMinimapVersion")) {
    Check-Field "Version" $f
}
Check-Method "Version" "GetVersionString" 1

# SoftReference / AssetID - the RNG stream key and the prefab identity.
foreach ($f in @("v3", "v2", "v1", "v0")) { Check-Field "SoftReferenceableAssets.AssetID" $f }

# UnityEngine natives. Random.State's four words are PRIVATE and read by reflection, so a rename here
# would silently blank every state trace in the dump.
foreach ($f in @("s0", "s1", "s2", "s3")) { Check-Field "UnityEngine.Random/State" $f }
Check-Method "UnityEngine.Random" "InitState" 1
Check-Member "UnityEngine.Random" "state"
Check-Member "UnityEngine.Random" "insideUnitCircle"
Check-Method "UnityEngine.Mathf" "PerlinNoise" 2
Check-Method "UnityEngine.Mathf" "PerlinNoise1D" 1
Check-Method "UnityEngine.Mathf" "FloatToHalf" 1
Check-Method "UnityEngine.Mathf" "HalfToFloat" 1
# The mid-run safety watchdog's clock (SeedLab.Dumper.Watch). realtimeSinceStartup rather than
# Time.time because it ignores timeScale: a paused game must not switch the watchdog off.
Check-Member "UnityEngine.Time" "realtimeSinceStartup"

# Terminal - the console commands.
Check-Method "Terminal" "InitTerminal" 0
Check-Method "Terminal" "AddString" 1
$checks++
$tc = Find-GameType "Terminal/ConsoleCommand"
if (-not $tc) { Write-Output "FAIL  Terminal/ConsoleCommand not found"; $failures++ }
else {
    $ctor = $tc.Methods | Where-Object { $_.IsConstructor -and $_.Parameters.Count -eq 13 }
    if (-not $ctor) {
        Write-Output "FAIL  Terminal.ConsoleCommand's 13-argument constructor no longer exists"
        $failures++
    }
}

if ($SelfTest) {
    Write-Output ""
    Write-Output "== self test: the tripwire must fire on a member or a draw count that is wrong =="
    $before = $failures
    Check-Field "WorldGenerator" "m_offset5_this_does_not_exist"
    Check-Method "UnityEngine.Mathf" "PerlinNoise" 7
    Check-Member "ZoneSystem" "NoSuchMemberAnywhere"
    Check-Field "RandomSpawn" "m_chanceToSpawn_this_does_not_exist"
    Check-Field "DropTable/DropData" "m_weight_this_does_not_exist"
    Check-CallCount "RandomSpawn" "Randomize" "UnityEngine.Random::Range" 99 `
        "(self test: the draw-count tripwire must fire on a wrong count)"
    # The room side of the walk, probed the same way. A member list that grew without its self test
    # growing too is a list nobody has proved anything about.
    Check-Field "Room" "m_size_this_does_not_exist"
    Check-Field "DungeonDB/RoomData" "m_theme_this_does_not_exist"
    Check-Method "DungeonDB" "GetRooms" 9
    # And the overload-aware counter: asking PlaceRoom's 5-argument overload for a count it does not
    # have must fail. If $paramCount were being ignored this would silently match the 3-argument
    # overload instead, which is the bug the parameter was added to prevent.
    Check-CallCount "DungeonGenerator" "PlaceRoom" "UnityEngine.Random::InitState" 99 `
        "(self test: the overload-aware draw-count tripwire must fire on a wrong count)" 5
    # ... and asking for an overload that does not exist at all must fail rather than fall back to one
    # that does.
    Check-CallCount "DungeonGenerator" "PlaceRoom" "UnityEngine.Random::InitState" 3 `
        "(self test: a non-existent overload must not silently resolve to another one)" 11
    if ($failures -eq $before + 11) {
        Write-Output "      the tripwire fired on all 11 deliberately wrong probes (3 names that do not"
        Write-Output "      exist, 2 renamed prefab-child fields, 1 wrong Random-draw count, 3 renamed"
        Write-Output "      room-walk members, 2 overload-aware call-count probes), as it must."
        $failures = $before
    } else {
        Write-Output "FAIL  the tripwire did NOT fire on all the deliberately wrong probes; this script"
        Write-Output "      proves nothing."
        $failures = $before + 1
    }

    # The same question for the IL scanner: does the matcher find a call it is told to look for, and
    # does it find one that is ONLY reachable through a nested type? Both signatures below are known
    # to be in this assembly - File::ReadAllText in Plugin.ReadTrigger (a top-level method) and
    # FileStream::.ctor in DumpWriter's <WriteBinaryStreaming> state machine (a nested one).
    Write-Output ""
    Write-Output "== self test: the IL scanner must find calls it is told to look for =="
    $probeSigs = @("System.IO.File::ReadAllText", "System.IO.FileStream::.ctor")
    $hitTop = 0
    $hitNested = 0
    foreach ($t in $allTypes) {
        foreach ($m in $t.Methods) {
            if (-not $m.HasBody) { continue }
            foreach ($i in $m.Body.Instructions) {
                $op = $i.Operand
                if (-not ($op -is [Mono.Cecil.MethodReference])) { continue }
                if ($probeSigs -contains ("{0}::{1}" -f $op.DeclaringType.FullName, $op.Name)) {
                    if ($t.DeclaringType) { $hitNested++ } else { $hitTop++ }
                }
            }
        }
    }
    Write-Output ("      probe hits: {0} in top-level types, {1} in nested types." -f $hitTop, $hitNested)
    $checks++
    if ($hitTop -eq 0 -or $hitNested -eq 0) {
        Write-Output "FAIL  the IL scanner missed a call it was told to find. It proves nothing about the"
        Write-Output "      calls it is supposed to REFUSE either."
        $failures++
    } else {
        Write-Output "      the scanner sees both top-level and nested code, as it must."
    }
}

# --------------------------------------------------------------------------------------------------
Write-Output ""
Write-Output "== the seeded stream still has the shape the offline replay assumes =="

# locationchildren.json is only useful because of an arithmetic claim: replaying the location's stream
# means taking ONE Random.Range draw per RandomSpawn, in array order, then ONE per RandomObject.
# Member names surviving a game update does not keep that true - a second draw added anywhere inside
# Randomize would shift every later entry and silently move which house has its maypole. So the
# preflight counts the calls in the shipped IL.
Check-CallCount "RandomSpawn" "Randomize" "UnityEngine.Random::Range" 1 `
    "One draw per RandomSpawn, in array order, is the whole basis of the offline replay."
Check-CallCount "RandomObject" "Randomize" "UnityEngine.Random::Range" 0 `
    "RandomObject.Randomize must draw only through GetWeightedObject."
Check-CallCount "RandomObject" "Randomize" "RandomObject::GetWeightedObject" 1 `
    "GetWeightedObject must still be called unconditionally, before the gates, or a gated-off RandomObject stops consuming its draw."
Check-CallCount "RandomObject" "GetWeightedObject" "UnityEngine.Random::Range" 1 `
    "One draw per RandomObject."
Check-CallCount "ZoneSystem" "SpawnLocation" "UnityEngine.Random::InitState" 3 `
    "SpawnLocation reseeds three times (once before the interior transform work, once in each spawn-mode branch); a change moves where the stream starts."
Check-CallCount "ZoneSystem" "SpawnLocation" "Utils::GetEnabledComponentsInChildren" 3 `
    "ZNetView, RandomObject and RandomSpawn - the three arrays, in that build order."
Check-CallCount "ZoneSystem" "PlaceLocations" "UnityEngine.Random::Range" 1 `
    "The location's rotation is the only ambient draw in PlaceLocations; a second one would change what is unreproducible offline."

# ---- the room stream has the same shape, with its own seed -----------------------------------------
# DungeonGenerator.PlaceRoom is OVERLOADED, so every check below names the 5-argument one explicitly:
# the 3-argument overload only computes a position and touches no RNG at all, and counting it would
# make these assertions true about the wrong method.
Check-CallCount "DungeonGenerator" "PlaceRoom" "Utils::GetEnabledComponentsInChildren" 3 `
    "ZNetView, RandomObject and RandomSpawn - a room's three arrays, built exactly the way SpawnLocation builds a location's." 5
Check-CallCount "DungeonGenerator" "PlaceRoom" "UnityEngine.Random::InitState" 3 `
    "PlaceRoom reseeds three times (once before the mode branch, once in each branch) from the room's placement position; a change moves where the room's stream starts." 5
Check-CallCount "DungeonGenerator" "PlaceRoom" "UnityEngine.Random::set_state" 1 `
    "PlaceRoom restores the AMBIENT stream after the room's own seeded run. If that ever stops happening, a dungeon's rooms would start perturbing everything generated after them." 5
Check-CallCount "DungeonGenerator" "PlaceRoom" "RandomSpawn::Randomize" 2 `
    "One Randomize loop per spawn-mode branch, and both pass the generator as the third argument - which is what makes m_dungeonRequireTheme LIVE inside a room and inert inside a location." 5
Check-CallCount "DungeonGenerator" "SetupAvailableRooms" "DungeonDB::GetRooms" 1 `
    "The available-room filter reads DungeonDB's room list; roomchildren.json is written from the same list, in the same order."
Check-CallCount "DungeonDB" "SetupRooms" "RoomList::GetAllRoomLists" 1 `
    "DungeonDB.m_rooms is the concatenation of every RoomList's m_rooms, which is why the walk attributes each room to a list through GetAllRoomLists."

# ---- the name normalisation is still the rule the dump assumes -------------------------------------
# Names.cs CALLS Utils.GetPrefabName rather than reimplementing it, but it carries a fallback copy of
# the rule for the case where the call throws, and locationchildren.json / roomchildren.json / search.json
# all DOCUMENT the rule as "truncate at the first '(' or ' '". These two counts pin the shape of the
# method: one IndexOfAny against the character set, one Remove at the index it found. A version that
# looked up a dictionary, or trimmed both ends, would still be called GetPrefabName and would still
# compile - and every "not found" in search.json would quietly become untrustworthy.
Check-CallCount "Utils" "GetPrefabName" "System.String::IndexOfAny" 1 `
    "GetPrefabName must still find the FIRST of a set of characters." 1 "System.String"
Check-CallCount "Utils" "GetPrefabName" "System.String::Remove" 1 `
    "GetPrefabName must still truncate AT that index and discard the rest." 1 "System.String"
# ... and the GameObject overload must still be nothing but a forward to the string one, or the game
# and the dumper would be normalising names by two different rules.
Check-CallCount "Utils" "GetPrefabName" "Utils::GetPrefabName" 1 `
    "GetPrefabName(GameObject) must still forward to GetPrefabName(string)." 1 "UnityEngine.GameObject"

# The root exclusion the enabled-vs-present comparison depends on. GetEnabledComponentsInChildren drops
# a component whose transform IS the root's (`componentsInChildren[i].transform == root.transform`),
# and ChildWalk.CountAll now drops the same one so the two counts compare like with like. That single
# op_Equality is the whole basis of the fix; if the game stopped doing it, CountAll would start
# under-counting and a real drift would be reported as clean.
Check-CallCount "Utils" "GetEnabledComponentsInChildren" "UnityEngine.Object::op_Equality" 1 `
    "The root-transform exclusion must still be there, or ChildWalk.CountAll's matching exclusion is wrong."
Check-CallCount "Utils" "GetEnabledComponentsInChildren" "Utils::IsEnabledInheirarcy" 1 `
    "The enabled filter is the OTHER half of that query, and the only difference the warning is meant to report."
Write-Output ""

Write-Output "== the plugin cannot delete, move or overwrite the game's files =="

# Nothing in the plugin may call a destructive file API at all, and every write must go through
# SeedLab.Dumper.DumpWriter, whose Resolve() runs the PathPolicy guard.
#
# Both lists were widened on 2026-09-23. They used to name six destructive calls and six write calls,
# which left File.Copy (overwrites its destination), Directory.CreateDirectory (creates folders
# anywhere), the whole FileInfo / DirectoryInfo object API and half of File's own write surface
# unchecked - while the README claimed the scan covered "every file write". A narrow list that reads
# as a wide one is worse than no list: it is a tripwire nobody steps on. The rule the lists encode:
#   destructive  - removes, renames or overwrites something that already exists, or changes a file's
#                  attributes. NOBODY may call these, DumpWriter included.
#   write        - creates or opens something for writing. Only SeedLab.Dumper.DumpWriter may call
#                  these, because only its Resolve() -> AssertWritable() -> PathPolicy.Reject path
#                  decides where a write is allowed to land.
# Read-only APIs (File.Exists, File.ReadAllText, Directory.GetFiles, a read-mode FileStream) are not
# listed: the plugin reads the trigger file and hashes the game's assemblies, and neither can damage
# anything. A read-mode FileStream is indistinguishable from a write-mode one in IL, so FileStream's
# constructor counts as a write and DumpWriter owns the only ones.
$destructive = @("System.IO.File::Delete", "System.IO.File::Move", "System.IO.File::Replace",
                 "System.IO.File::Copy", "System.IO.File::Encrypt", "System.IO.File::Decrypt",
                 "System.IO.File::SetAttributes",
                 "System.IO.Directory::Delete", "System.IO.Directory::Move",
                 "System.IO.FileInfo::Delete", "System.IO.FileInfo::MoveTo",
                 "System.IO.FileInfo::Replace", "System.IO.FileInfo::CopyTo",
                 "System.IO.FileInfo::Encrypt", "System.IO.FileInfo::Decrypt",
                 "System.IO.DirectoryInfo::Delete", "System.IO.DirectoryInfo::MoveTo",
                 "System.IO.FileSystemInfo::Delete")
$writeApis = @("System.IO.File::WriteAllText", "System.IO.File::WriteAllBytes",
               "System.IO.File::WriteAllLines", "System.IO.File::AppendAllText",
               "System.IO.File::AppendAllLines", "System.IO.File::AppendText",
               "System.IO.File::Create", "System.IO.File::CreateText",
               "System.IO.File::Open", "System.IO.File::OpenWrite",
               "System.IO.Directory::CreateDirectory",
               "System.IO.DirectoryInfo::Create", "System.IO.DirectoryInfo::CreateSubdirectory",
               "System.IO.FileInfo::Create", "System.IO.FileInfo::CreateText",
               "System.IO.FileInfo::Open", "System.IO.FileInfo::OpenWrite",
               "System.IO.FileInfo::AppendText",
               "System.IO.FileStream::.ctor", "System.IO.StreamWriter::.ctor")
$badDestructive = @()
$badWrites = @()
$goodWrites = 0
$goodWritesNested = 0
$methodsScanned = 0
$nestedMethodsScanned = 0
foreach ($t in $allTypes) {
    $root = Get-RootType $t
    $isNested = [bool]$t.DeclaringType
    foreach ($m in $t.Methods) {
        if (-not $m.HasBody) { continue }
        $methodsScanned++
        if ($isNested) { $nestedMethodsScanned++ }
        foreach ($i in $m.Body.Instructions) {
            $op = $i.Operand
            if (-not $op) { continue }
            if (-not ($op -is [Mono.Cecil.MethodReference])) { continue }
            $sig = "{0}::{1}" -f $op.DeclaringType.FullName, $op.Name
            if ($destructive -contains $sig) {
                $badDestructive += ("{0}.{1} -> {2}" -f $t.FullName, $m.Name, $sig)
            }
            if ($writeApis -contains $sig) {
                # Ownership is asked of the ROOT type: DumpWriter's streaming writer lives in a
                # nested state machine and is still DumpWriter's code.
                if ($root.FullName -eq "SeedLab.Dumper.DumpWriter") {
                    $goodWrites++
                    if ($isNested) { $goodWritesNested++ }
                }
                else { $badWrites += ("{0}.{1} -> {2}" -f $t.FullName, $m.Name, $sig) }
            }
        }
    }
}
$checks += 5
Write-Output ("      scanned {0} method bodies, {1} of them in nested types." -f $methodsScanned, $nestedMethodsScanned)
Write-Output ("      against {0} destructive APIs (forbidden everywhere) and {1} write APIs (DumpWriter only)." -f
              $destructive.Count, $writeApis.Count)

# The scan is blind unless it enters nested types, and "found nothing" is what blindness looks like.
# This assembly is known to have them (every Run() is an iterator), so their absence is a bug in
# this script, not a clean assembly.
if ($nestedMethodsScanned -eq 0) {
    Write-Output "FAIL  the scan entered no nested type at all. Iterator state machines and lambda"
    Write-Output "      closures are therefore unscanned and this section proves nothing."
    $failures++
}
# ... and specifically, DumpWriter's streaming writer must have been seen inside its state machine.
if ($goodWritesNested -eq 0) {
    Write-Output "FAIL  no file write was found inside a NESTED type, but DumpWriter.WriteBinaryStreaming"
    Write-Output "      is an iterator whose FileStream ctor lives in its state machine. The recursion"
    Write-Output "      into nested types is broken."
    $failures++
} else {
    Write-Output ("      {0} of the {1} writes are inside nested types (the streaming writer), so the" -f $goodWritesNested, $goodWrites)
    Write-Output "      recursion is demonstrably live."
}

# Without this, "no bad writes" would also be the answer when the signature strings are wrong and the
# scan matches nothing at all. DumpWriter is known to write files, so it must show up.
if ($goodWrites -eq 0) {
    Write-Output "FAIL  the IL scan found no file writes anywhere, including inside DumpWriter, so it is"
    Write-Output "      not actually checking anything. Fix the signature list in this script."
    $failures++
} else {
    Write-Output ("      the scan works: {0} write call(s) found inside DumpWriter." -f $goodWrites)
}
if ($badDestructive.Count -gt 0) {
    Write-Output "FAIL  the plugin calls a destructive file API:"
    foreach ($b in $badDestructive) { Write-Output ("        " + $b) }
    $failures++
} else {
    Write-Output ("      none of the {0} destructive APIs is called anywhere in the assembly." -f $destructive.Count)
}
if ($badWrites.Count -gt 0) {
    Write-Output "FAIL  a file write bypasses DumpWriter (and therefore the path guard):"
    foreach ($b in $badWrites) { Write-Output ("        " + $b) }
    $failures++
} else {
    Write-Output "      every file write goes through SeedLab.Dumper.DumpWriter."
}

if ($SelfTest) {
    # The lists above are only worth as much as their spelling. A signature string with a typo in it
    # matches nothing, and "no bad calls found" is exactly what that looks like. So: run the SAME
    # lists over the game's own assembly, which certainly deletes, moves, creates and writes files,
    # and require a good number of them to be seen. This is the widened lists' tripwire, the way the
    # planted-violation build is the scan's.
    Write-Output ""
    Write-Output "== self test: the destructive/write signature lists match real IL =="
    $seenD = @{}
    $seenW = @{}
    $gameAsm = Join-Path $managed "assembly_valheim.dll"
    try {
        $gm = [Mono.Cecil.ModuleDefinition]::ReadModule($gameAsm)
        $gameTypes = @()
        foreach ($t in $gm.Types) { $gameTypes += Get-AllTypes $t }
        foreach ($t in $gameTypes) {
            foreach ($m in $t.Methods) {
                if (-not $m.HasBody) { continue }
                foreach ($i in $m.Body.Instructions) {
                    $op = $i.Operand
                    if (-not ($op -is [Mono.Cecil.MethodReference])) { continue }
                    $sig = "{0}::{1}" -f $op.DeclaringType.FullName, $op.Name
                    if ($destructive -contains $sig) { $seenD[$sig] = $true }
                    if ($writeApis -contains $sig) { $seenW[$sig] = $true }
                }
            }
        }
    } catch {
        Write-Output ("      could not read {0}: {1}" -f $gameAsm, $_.Exception.Message)
    }
    $checks++
    Write-Output ("      of our lists, the game itself calls {0} destructive and {1} write API(s):" -f
                  $seenD.Count, $seenW.Count)
    foreach ($k in ($seenD.Keys | Sort-Object)) { Write-Output ("        destructive  " + $k) }
    foreach ($k in ($seenW.Keys | Sort-Object)) { Write-Output ("        write        " + $k) }
    if ($seenD.Count -lt 2 -or $seenW.Count -lt 3) {
        Write-Output "FAIL  the signature strings barely match anything even in code that is full of file"
        Write-Output "      I/O. They are probably misspelled, and the scan above proves nothing."
        $failures++
    }
}

$checks++
$policy = $plug.GetType("SeedLab.Dumper.PathPolicy")
if (-not $policy) {
    Write-Output "FAIL  SeedLab.Dumper.PathPolicy is missing - the write guard is not in this build."
    $failures++
}

# --------------------------------------------------------------------------------------------------
Write-Output ""
Write-Output "== the write guard is on every write path, and it refuses the save folders =="

function Find-PluginType([string]$full) {
    foreach ($t in $allTypes) { if ($t.FullName -eq $full) { return $t } }
    return $null
}

function Get-CallSigs($method) {
    $sigs = @()
    if (-not $method.HasBody) { return $sigs }
    foreach ($i in $method.Body.Instructions) {
        $op = $i.Operand
        if ($op -is [Mono.Cecil.MethodReference]) {
            $sigs += ("{0}::{1}" -f $op.DeclaringType.FullName, $op.Name)
        }
    }
    return $sigs
}

function Assert-Calls($type, [string]$methodName, [string]$calleeSig, [string]$label) {
    $script:checks++
    if (-not $type) { Write-Output ("FAIL  {0}: type missing" -f $label); $script:failures++; return }
    $m = $type.Methods | Where-Object { $_.Name -eq $methodName } | Select-Object -First 1
    if (-not $m) { Write-Output ("FAIL  {0}: {1} missing" -f $label, $methodName); $script:failures++; return }
    if ((Get-CallSigs $m) -notcontains $calleeSig) {
        Write-Output ("FAIL  {0}: {1} does not call {2}" -f $label, $methodName, $calleeSig)
        $script:failures++
    }
}

# Every public write entry point must funnel through Resolve, and Resolve through the policy. The
# streaming writer is an iterator, so its body is in the state machine's MoveNext, not in the method
# that bears its name - which is exactly the kind of code the old top-level-only scan could not see.
$dumpWriter = Find-PluginType "SeedLab.Dumper.DumpWriter"
$resolveSig = "SeedLab.Dumper.DumpWriter::Resolve"
Assert-Calls $dumpWriter "WriteJson"   $resolveSig "DumpWriter.WriteJson"
Assert-Calls $dumpWriter "WriteText"   $resolveSig "DumpWriter.WriteText"
Assert-Calls $dumpWriter "WriteBinary" $resolveSig "DumpWriter.WriteBinary"
$streamSm = $allTypes | Where-Object {
    $_.DeclaringType -and (Get-RootType $_).FullName -eq "SeedLab.Dumper.DumpWriter" -and
    $_.Name -like "*WriteBinaryStreaming*"
} | Select-Object -First 1
$checks++
if (-not $streamSm) {
    Write-Output "FAIL  DumpWriter.WriteBinaryStreaming's state machine was not found."
    $failures++
} else {
    Assert-Calls $streamSm "MoveNext" $resolveSig ("DumpWriter." + $streamSm.Name + ".MoveNext")
}
Assert-Calls $dumpWriter "Resolve" "SeedLab.Dumper.DumpWriter::AssertWritable" "DumpWriter.Resolve"
Assert-Calls $dumpWriter "Resolve" "SeedLab.Dumper.PathPolicy::IsUnderOrEqual" "DumpWriter.Resolve"
Assert-Calls $dumpWriter "AssertWritable" "SeedLab.Dumper.PathPolicy::Reject" "DumpWriter.AssertWritable"
Write-Output "      WriteJson / WriteText / WriteBinary / WriteBinaryStreaming -> Resolve -> AssertWritable -> PathPolicy.Reject."

# The forbidden-segment list, read out of the SHIPPED IL rather than trusted from the source.
$checks++
$expectedSegments = @("worlds", "worlds_local", "characters", "remote", "cache")
$cctor = $null
if ($policy) { $cctor = $policy.Methods | Where-Object { $_.Name -eq ".cctor" } | Select-Object -First 1 }
if (-not $cctor) {
    Write-Output "FAIL  PathPolicy has no static constructor, so ForbiddenSegments is not initialised."
    $failures++
} else {
    $lits = @()
    foreach ($i in $cctor.Body.Instructions) {
        if ($i.OpCode.Name -eq "ldstr") { $lits += [string]$i.Operand }
    }
    $missing = @($expectedSegments | Where-Object { $lits -notcontains $_ })
    $extra = @($lits | Where-Object { $expectedSegments -notcontains $_ })
    if ($missing.Count -gt 0 -or $extra.Count -gt 0) {
        Write-Output ("FAIL  PathPolicy.ForbiddenSegments in the built DLL is [{0}], expected [{1}]." -f
                      ($lits -join ", "), ($expectedSegments -join ", "))
        $failures++
    } else {
        Write-Output ("      PathPolicy.ForbiddenSegments in the DLL = {0}." -f ($lits -join ", "))
    }
}

# And the policy is RUN, not merely present. PathPolicy.cs has no Unity, BepInEx or game dependency
# precisely so it can be compiled here and driven against a table of real save paths - the rule
# "test the tripwire, not just the code". The source is compiled rather than the DLL loaded because a
# netstandard2.1 assembly cannot be loaded into Windows PowerShell 5.1's .NET Framework runtime; the
# ForbiddenSegments check above is what ties this source to the shipped binary.
$checks++
$policySrc = Join-Path $PSScriptRoot "src\PathPolicy.cs"
if (-not (Test-Path $policySrc)) {
    Write-Output ("FAIL  {0} not found; the path policy could not be executed." -f $policySrc)
    $failures++
} else {
    $src = [IO.File]::ReadAllText($policySrc)
    $src += @"

namespace SeedLab.Dumper
{
    public static class PathPolicyProbe
    {
        public static string Reject(string full, string gameRoot, string saveRoot)
        {
            return PathPolicy.Reject(full, gameRoot, saveRoot);
        }
    }
}
"@
    try { Add-Type -TypeDefinition $src -Language CSharp -ErrorAction Stop } catch { }

    $profileDir = $env:USERPROFILE
    $ironGate = Join-Path $profileDir "AppData\LocalLow\IronGate"
    $gameRoot = $ValheimDir

    $mustRefuse = @(
        (Join-Path $ironGate "Valheim\worlds_local\asdasdasd.fwl2"),
        (Join-Path $ironGate "Valheim\worlds_local\asdasdasd.db2"),
        (Join-Path $ironGate "Valheim\characters\Player.fch"),
        (Join-Path $ironGate "Valheim\cache\asdasdasd_biomedatacache.bin"),
        (Join-Path $ironGate "Valheim\anything-else.txt"),
        (Join-Path $gameRoot "BepInEx\plugins\dump.json"),
        (Join-Path $gameRoot "valheim_Data\Managed\x.bin"),
        $gameRoot,
        "C:\Program Files (x86)\Steam\userdata\12345678\892970\remote\worlds\asdasdasd.db",
        "C:\Program Files (x86)\Steam\userdata\12345678\892970\remote\x.bin",
        "D:\anywhere\worlds\x.json",
        "D:\anywhere\worlds_local\x.json",
        "D:\anywhere\characters\x.json",
        "D:\anywhere\cache\x.json",
        (Join-Path $profileDir "AppData\valheim-dumper\worlds\x.json"),
        ""
    )
    $mustAllow = @(
        (Join-Path $profileDir "AppData\valheim-dumper"),
        (Join-Path $profileDir "AppData\valheim-dumper\1.0.15-59f53fb5\manifest.json"),
        (Join-Path $profileDir "AppData\valheim-dumper\1.0.15-59f53fb5\goldens\natives-perlin.bin"),
        "D:\myworlds\x.json",
        "D:\worldscape\x.json"
    )

    $policyFails = 0
    foreach ($p in $mustRefuse) {
        $r = [SeedLab.Dumper.PathPolicyProbe]::Reject($p, $gameRoot, $ironGate)
        if (-not $r) {
            Write-Output ("FAIL  the path policy ACCEPTED a path it must refuse: '{0}'" -f $p)
            $policyFails++
        }
    }
    foreach ($p in $mustAllow) {
        $r = [SeedLab.Dumper.PathPolicyProbe]::Reject($p, $gameRoot, $ironGate)
        if ($r) {
            Write-Output ("FAIL  the path policy refused a legitimate output path: '{0}' ({1})" -f $p, $r)
            $policyFails++
        }
    }
    if ($policyFails -gt 0) { $failures++ }
    else {
        Write-Output ("      the policy refused all {0} save/install paths and allowed all {1} output paths." -f
                      $mustRefuse.Count, $mustAllow.Count)
    }
}

# --------------------------------------------------------------------------------------------------
Write-Output ""
Write-Output "== the two restores are unconditional =="

# ModeWorldGen replaces the static WorldGenerator.instance. If its restore were only on the success
# path, one throw would leave the main menu generating its backdrop from the dumper's throwaway world
# for the rest of the session. The C# compiler puts an iterator's finally body in <>m__FinallyN and
# calls it from MoveNext's fault handler and from Dispose - so BOTH must be present.
$checks++
$wgSm = $allTypes | Where-Object {
    $_.DeclaringType -and (Get-RootType $_).FullName -eq "SeedLab.Dumper.ModeWorldGen" -and $_.Name -like "*Run*"
} | Select-Object -First 1
if (-not $wgSm) {
    Write-Output "FAIL  ModeWorldGen.Run's state machine was not found."
    $failures++
} else {
    $restoreSig = "SeedLab.Dumper.ModeWorldGen::RestoreGenerator"
    $finallyM = $wgSm.Methods | Where-Object { (Get-CallSigs $_) -contains $restoreSig } | Select-Object -First 1
    $moveNext = $wgSm.Methods | Where-Object { $_.Name -eq "MoveNext" } | Select-Object -First 1
    if (-not $finallyM -or -not $moveNext) {
        Write-Output "FAIL  ModeWorldGen.Run never calls RestoreGenerator."
        $failures++
    } elseif ($finallyM.Name -notlike "*m__Finally*") {
        Write-Output ("FAIL  RestoreGenerator is called from {0}, not from a compiler-generated finally." -f $finallyM.Name)
        $failures++
    } else {
        # HandlerType is a Mono.Cecil enum; compared as a string because PowerShell 5.1 will not
        # resolve [Mono.Cecil.ExceptionHandlerType] from an Add-Type -Path assembly.
        $handlers = @($moveNext.Body.ExceptionHandlers | Where-Object {
            $_.HandlerType.ToString() -eq "Fault" -or $_.HandlerType.ToString() -eq "Finally" })
        $callsFinally = (Get-CallSigs $moveNext) -contains ("{0}::{1}" -f $wgSm.FullName, $finallyM.Name)
        if ($handlers.Count -eq 0 -or -not $callsFinally) {
            Write-Output "FAIL  ModeWorldGen.Run's MoveNext does not run the restore on the failure path."
            $failures++
        } else {
            Write-Output ("      ModeWorldGen.Run: RestoreGenerator runs from {0}, reached from MoveNext's fault/finally handler." -f $finallyM.Name)
        }
    }
}

# --------------------------------------------------------------------------------------------------
Write-Output ""
Write-Output "== the RandomGuard contract: nothing may write a Random state it did not capture =="
#
# WHY THIS SECTION EXISTS (2026-09-23). RandomGuard was `internal readonly struct RandomGuard` with a
# single constructor `RandomGuard(int? initState = null)`, and every argumentless site was written
# `using (new RandomGuard())`. For a STRUCT the implicit parameterless constructor is always a member
# and overload resolution prefers it over a candidate that needs a default-argument substitution, so
# Roslyn emitted `initobj` - the constructor never ran, `_saved` stayed {0,0,0,0}, and Dispose wrote
# four zeros into UnityEngine.Random. Zero is a FIXED POINT of Unity's xorshift128, so from the first
# batch of the first asset dump every ambient draw in the user's session returned 0 and the new-world
# dialog offered "aaaaaaaaaa" forever. Six sites were broken; the nine that passed an argument
# compiled to a real `call .ctor` and were fine. Reviewing the C# by eye could not see it - only the
# IL could, which is why this is a preflight gate and not a comment.
#
# The contract, enforced below:
#   G1  RandomGuard is a reference type, so `new RandomGuard()` is a COMPILE error (CS1729)
#   G2  no `initobj RandomGuard` anywhere in the assembly
#   G3  it is constructed only through the Capture/Seeded factories
#   G4  its constructor really reads UnityEngine.Random.state (it captures something)
#   G5  its constructor asks RandomStateSafe whether the LIVE state is already dead (capture check)
#   G6  Dispose restores through RandomStateSafe.Restore, not by assigning the state itself
#   G7  RandomStateSafe.Restore is the ONE writer of UnityEngine.Random.state in the whole assembly
#   G8  ...and it tests for the zero state before writing, and re-seeds instead
#   G9  NoDrawCheck.Around restores through RandomStateSafe.Restore, inside a finally
#   G10 no iterator/async state machine holds a RandomGuard field or writes the Random state
#   G11 every dump entry point refuses to run when the live state is already all zeros

$guardType = "SeedLab.Dumper.RandomGuard"
$safeType  = "SeedLab.Dumper.RandomStateSafe"
$restoreSig = "$safeType" + "::Restore"

$guard = Find-PluginType $guardType
$safe  = Find-PluginType $safeType

# G1 -------------------------------------------------------------------------------------------------
$checks++
if (-not $guard) {
    Write-Output "FAIL  $guardType is missing - the guard contract cannot be checked at all."
    $failures++
} elseif ($guard.IsValueType) {
    Write-Output "FAIL  $guardType is a VALUE TYPE. For a struct, 'new RandomGuard()' compiles whatever"
    Write-Output "      constructors are declared and Roslyn emits initobj - the constructor never runs,"
    Write-Output "      the saved state is {0,0,0,0} and Dispose kills UnityEngine.Random. That is the"
    Write-Output "      2026-09-23 'aaaaaaaaaa' bug. It must be a class."
    $failures++
} else {
    Write-Output "      RandomGuard is a reference type, so an argumentless 'new' cannot compile."
}

$checks++
if ($guard -and -not $guard.IsValueType) {
    $noArgCtors  = @($guard.Methods | Where-Object { $_.IsConstructor -and -not $_.IsStatic -and $_.Parameters.Count -eq 0 })
    $openCtors   = @($guard.Methods | Where-Object { $_.IsConstructor -and -not $_.IsStatic -and -not $_.IsPrivate })
    if ($noArgCtors.Count -gt 0) {
        Write-Output "FAIL  RandomGuard declares a parameterless constructor. The whole point of the fix is"
        Write-Output "      that an argumentless construction cannot be written."
        $failures++
    } elseif ($openCtors.Count -gt 0) {
        Write-Output "FAIL  RandomGuard has a non-private constructor; construction must go through the"
        Write-Output "      Capture/Seeded factories so the capture check cannot be bypassed."
        $failures++
    } else {
        Write-Output "      RandomGuard's constructors are all private; Capture/Seeded are the only way in."
    }
} else {
    Write-Output "FAIL  RandomGuard's constructors could not be inspected."
    $failures++
}

# G2, G3, G7, G10 - one walk over every method in the assembly, nested types included -------------------
$initobjHits   = @()
$newobjHits    = @()
$setStateHits  = @()
$initStateHits = @()
$restoreHits   = @()
$smGuardFields = @()
$smRngWrites   = @()
$stateMachines = @()

function Is-StateMachine($t) {
    if ($t.Name -match '^<.*>d__') { return $true }
    foreach ($i in $t.Interfaces) {
        $n = $i.InterfaceType.FullName
        if ($n -eq "System.Collections.IEnumerator" -or
            $n -eq "System.Runtime.CompilerServices.IAsyncStateMachine") {
            if ($t.Methods | Where-Object { $_.Name -eq "MoveNext" }) { return $true }
        }
    }
    return $false
}

foreach ($t in $allTypes) {
    $isSm = Is-StateMachine $t
    if ($isSm) {
        $stateMachines += $t
        foreach ($f in $t.Fields) {
            if ($f.FieldType.FullName -eq $guardType) {
                $smGuardFields += ("{0}::{1}  (field type {2})" -f $t.FullName, $f.Name, $f.FieldType.FullName)
            }
        }
    }
    foreach ($m in $t.Methods) {
        if (-not $m.HasBody) { continue }
        foreach ($i in $m.Body.Instructions) {
            $op = $i.Operand
            $where = "{0}::{1}  IL_{2}" -f $t.FullName, $m.Name, $i.Offset.ToString("x4")
            if ($i.OpCode.Name -eq "initobj" -and $op -is [Mono.Cecil.TypeReference] -and
                $op.FullName -eq $guardType) {
                $initobjHits += $where
            }
            if (-not ($op -is [Mono.Cecil.MethodReference])) { continue }
            $sig = "{0}::{1}" -f $op.DeclaringType.FullName, $op.Name
            if ($i.OpCode.Name -eq "newobj" -and $op.DeclaringType.FullName -eq $guardType) {
                $newobjHits += $where
            }
            if ($sig -eq "UnityEngine.Random::set_state") {
                $setStateHits += $where
                if ($isSm) { $smRngWrites += ("set_state                " + $where) }
            }
            if ($sig -eq "UnityEngine.Random::InitState") {
                $initStateHits += $where
                if ($isSm) { $smRngWrites += ("InitState                " + $where) }
            }
            if ($sig -eq $restoreSig) {
                $restoreHits += $where
                if ($isSm) { $smRngWrites += ("RandomStateSafe.Restore  " + $where) }
            }
        }
    }
}

Write-Output ("      scanned {0} types, {1} of them compiler-generated state machines." -f
              $allTypes.Count, $stateMachines.Count)

# G2
$checks++
if ($initobjHits.Count -gt 0) {
    Write-Output "FAIL  'initobj RandomGuard' appears in the built IL. That is the constructor NOT running:"
    Write-Output "      the guard saves {0,0,0,0} and Dispose writes four zeros into UnityEngine.Random."
    foreach ($h in $initobjHits) { Write-Output ("        " + $h) }
    $failures++
} else {
    Write-Output "      no 'initobj RandomGuard' anywhere - every guard is really constructed."
}

# G3
$checks++
$badNewobj = @($newobjHits | Where-Object {
    $_ -notlike ($guardType + "::Capture*") -and $_ -notlike ($guardType + "::Seeded*") })
if ($newobjHits.Count -eq 0) {
    Write-Output "FAIL  RandomGuard is never constructed at all; the factories are not in this build."
    $failures++
} elseif ($badNewobj.Count -gt 0) {
    Write-Output "FAIL  RandomGuard is constructed outside its Capture/Seeded factories:"
    foreach ($h in $badNewobj) { Write-Output ("        " + $h) }
    $failures++
} else {
    Write-Output ("      RandomGuard is constructed only inside Capture/Seeded ({0} site(s))." -f $newobjHits.Count)
}

# G4 + G5 - the constructor captures, and asks whether the live state is already dead -------------------
$guardCtor = $null
if ($guard) { $guardCtor = $guard.Methods | Where-Object { $_.IsConstructor -and -not $_.IsStatic } | Select-Object -First 1 }
$checks++
if (-not $guardCtor) {
    Write-Output "FAIL  RandomGuard has no instance constructor."
    $failures++
} else {
    $ctorSigs = Get-CallSigs $guardCtor
    if ($ctorSigs -notcontains "UnityEngine.Random::get_state") {
        Write-Output "FAIL  RandomGuard's constructor never reads UnityEngine.Random.state, so it restores a"
        Write-Output "      state it never captured."
        $failures++
    } elseif ($ctorSigs -notcontains ($safeType + "::EnsureLiveAtCapture")) {
        Write-Output "FAIL  RandomGuard's constructor does not check the live state at CAPTURE time. A guard"
        Write-Output "      that captures an already-dead generator re-inflicts it on Dispose."
        $failures++
    } else {
        Write-Output "      RandomGuard's constructor checks the live state, then captures it."
    }
}

# G6 ---------------------------------------------------------------------------------------------------
$checks++
$dispose = $null
if ($guard) { $dispose = $guard.Methods | Where-Object { $_.Name -eq "Dispose" } | Select-Object -First 1 }
if (-not $dispose) {
    Write-Output "FAIL  RandomGuard.Dispose is missing."
    $failures++
} elseif ((Get-CallSigs $dispose) -notcontains $restoreSig) {
    Write-Output "FAIL  RandomGuard.Dispose does not restore through $restoreSig, so it can write a state"
    Write-Output "      that nobody checked."
    $failures++
} else {
    Write-Output "      RandomGuard.Dispose restores through RandomStateSafe.Restore."
}

# G7 - the single-writer gate ----------------------------------------------------------------------------
$checks++
$badWriters = @($setStateHits | Where-Object { $_ -notlike ($safeType + "::Restore*") })
if ($setStateHits.Count -eq 0) {
    Write-Output "FAIL  nothing in the assembly ever assigns UnityEngine.Random.state; the guards restore"
    Write-Output "      nothing."
    $failures++
} elseif ($badWriters.Count -gt 0) {
    Write-Output "FAIL  UnityEngine.Random.state is written outside $restoreSig, so that write bypasses the"
    Write-Output "      zero check. Route it through RandomStateSafe.Restore:"
    foreach ($h in $badWriters) { Write-Output ("        " + $h) }
    $failures++
} else {
    Write-Output ("      UnityEngine.Random.state is written from exactly one place: {0}." -f $setStateHits[0])
}

# G8 -----------------------------------------------------------------------------------------------------
$checks++
$restoreM = $null
if ($safe) { $restoreM = $safe.Methods | Where-Object { $_.Name -eq "Restore" } | Select-Object -First 1 }
if (-not $restoreM) {
    Write-Output "FAIL  $restoreSig is missing - there is no zero check on the write path."
    $failures++
} else {
    $rs = Get-CallSigs $restoreM
    if (($rs -notcontains ($safeType + "::IsZero")) -and ($rs -notcontains ($safeType + "::CurrentIsZero"))) {
        Write-Output "FAIL  RandomStateSafe.Restore does not test the state for zero before writing it. An"
        Write-Output "      all-zero write kills UnityEngine.Random for the rest of the session."
        $failures++
    } elseif ($rs -notcontains ($safeType + "::Reseed") -and $rs -notcontains "UnityEngine.Random::InitState") {
        Write-Output "FAIL  RandomStateSafe.Restore has no re-seed path, so on a zero state it can only write"
        Write-Output "      the zeros or leave the generator dead."
        $failures++
    } else {
        Write-Output "      RandomStateSafe.Restore tests for the zero state and re-seeds instead of writing it."
    }
}

# G9 - NoDrawCheck.Around restores, in a finally, through the single writer -------------------------------
$checks++
$noDraw = Find-PluginType "SeedLab.Dumper.NoDrawCheck"
$around = $null
if ($noDraw) { $around = $noDraw.Methods | Where-Object { $_.Name -eq "Around" } | Select-Object -First 1 }
if (-not $around) {
    Write-Output "FAIL  SeedLab.Dumper.NoDrawCheck.Around is missing."
    $failures++
} else {
    $restoreCall = $null
    foreach ($i in $around.Body.Instructions) {
        $op = $i.Operand
        if ($op -is [Mono.Cecil.MethodReference] -and
            ("{0}::{1}" -f $op.DeclaringType.FullName, $op.Name) -eq $restoreSig) { $restoreCall = $i }
    }
    $inFinally = $false
    foreach ($h in $around.Body.ExceptionHandlers) {
        if ($h.HandlerType.ToString() -ne "Finally") { continue }
        if ($restoreCall -and $restoreCall.Offset -ge $h.HandlerStart.Offset -and
            ($h.HandlerEnd -eq $null -or $restoreCall.Offset -lt $h.HandlerEnd.Offset)) { $inFinally = $true }
    }
    if (-not $restoreCall) {
        Write-Output "FAIL  NoDrawCheck.Around never restores through $restoreSig - it either detects a moved"
        Write-Output "      stream and leaves it moved, or writes the state without the zero check."
        $failures++
    } elseif (-not $inFinally) {
        Write-Output "FAIL  NoDrawCheck.Around restores Random.state outside a finally, so a block that"
        Write-Output "      throws would leave the user's stream displaced."
        $failures++
    } else {
        Write-Output "      NoDrawCheck.Around restores through RandomStateSafe.Restore, inside a finally."
    }
}

# G10 - the "a RandomGuard must never span a yield" rule, enforced mechanically ---------------------------
#
# The rule the plugin's own doc comments claim. A guard held across a frame boundary would restore, on a
# later frame, a state captured on an earlier one - rewinding every draw the game made in between
# (EnvMan.UpdateEnvironment alone draws every single frame).
#
# What this check must NOT do is flag the correct code. Roslyn's iterator rewriter hoists EVERY local of
# an iterator into the state-machine type, whether or not it crosses a yield - a `using` on a reference
# type becomes a `<>s__N` FIELD even when its try/finally is wholly inside one MoveNext call. (The old
# struct guard stayed a plain local, so this only became visible when the guard became a class on
# 2026-09-23.) "A RandomGuard field exists" is therefore not the violation. These two are:
#
#   a) the guard's release is reachable from OUTSIDE MoveNext's straight-line flow - the iterator's own
#      Dispose(), or a `<>m__FinallyN` helper. Roslyn only lifts a finally into `<>m__FinallyN` and calls
#      it from Dispose() when the try region CONTAINS a yield, so a LOAD of a guard field anywhere but
#      MoveNext is precisely the signature of a guard that spans one. (Dispose() nulling the field for GC
#      is `ldnull; stfld` - a store, not a load - and is not a violation.)
#   b) the try region protected by the finally that disposes the guard contains a yield point, i.e. a
#      store to the state machine's `<>2__current` field. UNVERIFIED as a live detector: it is a
#      backstop, and the 2026-09-23 planted-violation build did not exercise it, because when a
#      `using` really does contain a yield Roslyn lifts its finally OUT of MoveNext into
#      `<>m__FinallyN` - which is what (a) catches. Keep (b) in case a future compiler leaves the
#      finally in place, but (a) is the check that has been proved to fire.
#
# Plus the blunt rule that makes the whole thing airtight: no state machine may WRITE the Random state at
# all. Every legitimate write lives in a plain synchronous method (RandomStateSafe, reached from the
# guard's Dispose, from NoDrawCheck.Around and from the natives D-tests). READING it -
# RandomStateSafe.Trace and CurrentIsZero, which the Run methods call - draws nothing and is unrestricted.

function Is-GuardField($op) {
    return ($op -is [Mono.Cecil.FieldReference]) -and ($op.FieldType.FullName -eq $guardType)
}

$spanViolations = @()
foreach ($t in $stateMachines) {
    $hasGuardField = $false
    foreach ($f in $t.Fields) { if ($f.FieldType.FullName -eq $guardType) { $hasGuardField = $true } }
    if (-not $hasGuardField) { continue }

    foreach ($m in $t.Methods) {
        if (-not $m.HasBody) { continue }

        if ($m.Name -ne "MoveNext") {
            # (a) the guard is RELEASED outside MoveNext: the method both loads a guard field and calls
            # Dispose. Clearing the field for GC - `ldnull; stfld` for a class, `ldflda; initobj` for a
            # struct - is not a release and must not be flagged.
            $loads = @()
            $disposes = $false
            foreach ($i in $m.Body.Instructions) {
                if (($i.OpCode.Name -eq "ldfld" -or $i.OpCode.Name -eq "ldflda") -and (Is-GuardField $i.Operand)) {
                    $loads += ("IL_{0} ({1})" -f $i.Offset.ToString("x4"), $i.Operand.Name)
                }
                $op = $i.Operand
                if ($op -is [Mono.Cecil.MethodReference] -and $op.Name -eq "Dispose" -and
                    ($op.DeclaringType.FullName -eq "System.IDisposable" -or
                     $op.DeclaringType.FullName -eq $guardType)) { $disposes = $true }
            }
            if ($loads.Count -gt 0 -and $disposes) {
                $spanViolations += ("{0}::{1}  disposes the guard field at {2} - its release is reachable from outside MoveNext, which Roslyn only produces when the 'using' contains a yield" -f
                                    $t.FullName, $m.Name, ($loads -join ", "))
            }
            continue
        }

        # (b) a yield inside the try region whose finally disposes the guard
        foreach ($h in $m.Body.ExceptionHandlers) {
            if ($h.HandlerType.ToString() -ne "Finally") { continue }
            $hEnd = if ($h.HandlerEnd -eq $null) { [int]::MaxValue } else { $h.HandlerEnd.Offset }
            $disposesGuard = $false
            foreach ($i in $m.Body.Instructions) {
                if ($i.Offset -lt $h.HandlerStart.Offset -or $i.Offset -ge $hEnd) { continue }
                if (($i.OpCode.Name -eq "ldfld" -or $i.OpCode.Name -eq "ldflda") -and (Is-GuardField $i.Operand)) {
                    $disposesGuard = $true
                }
            }
            if (-not $disposesGuard) { continue }

            foreach ($i in $m.Body.Instructions) {
                if ($i.Offset -lt $h.TryStart.Offset -or $i.Offset -ge $h.TryEnd.Offset) { continue }
                if ($i.OpCode.Name -eq "stfld" -and $i.Operand -is [Mono.Cecil.FieldReference] -and
                    $i.Operand.Name -like "*2__current*") {
                    $spanViolations += ("{0}::MoveNext  the 'using' guarded by the finally at IL_{1} contains a yield at IL_{2} (stfld {3}) - the guard spans a frame boundary" -f
                                        $t.FullName, $h.HandlerStart.Offset.ToString("x4"),
                                        $i.Offset.ToString("x4"), $i.Operand.Name)
                }
            }
        }
    }
}

$checks++
if ($stateMachines.Count -lt 5) {
    Write-Output ("FAIL  only {0} state machine(s) were recognised; the detector is broken and this check" -f $stateMachines.Count)
    Write-Output "      proves nothing."
    $failures++
} elseif ($spanViolations.Count -gt 0) {
    Write-Output "FAIL  a RandomGuard spans a yield. It would restore, on a later frame, a state captured on"
    Write-Output "      an earlier one - rewinding every draw the game made in between:"
    foreach ($h in $spanViolations) { Write-Output ("        " + $h) }
    $failures++
} elseif ($smRngWrites.Count -gt 0) {
    Write-Output "FAIL  a state machine writes UnityEngine.Random directly. Every legitimate write is in a"
    Write-Output "      plain synchronous method, so this is a raw write inside a coroutine:"
    foreach ($h in $smRngWrites) { Write-Output ("        " + $h) }
    $failures++
} else {
    Write-Output ("      no RandomGuard spans a yield, and none of the {0} state machines writes the Random" -f
                  $stateMachines.Count)
    Write-Output ("      state directly ({0} of them hold a hoisted guard field, which Roslyn emits for every" -f $smGuardFields.Count)
    Write-Output "      iterator local and is not by itself a violation)."
}

# G11 - every dump entry point refuses a generator that is already dead -----------------------------------
foreach ($mode in @("ModeAssets", "ModeNatives", "ModeWorldGen")) {
    $checks++
    $sm = $null
    foreach ($t in $allTypes) {
        if ($t.FullName -like ("SeedLab.Dumper." + $mode + "/<Run>d__*")) { $sm = $t; break }
    }
    if (-not $sm) {
        Write-Output "FAIL  SeedLab.Dumper.$mode.Run's state machine was not found; the dump-start check"
        Write-Output "      cannot be verified."
        $failures++
        continue
    }
    $mn = $sm.Methods | Where-Object { $_.Name -eq "MoveNext" } | Select-Object -First 1
    if (-not $mn -or ((Get-CallSigs $mn) -notcontains ($safeType + "::CurrentIsZero"))) {
        Write-Output "FAIL  $mode.Run does not check RandomStateSafe.CurrentIsZero before it dumps. A dump"
        Write-Output "      taken across a dead generator records zeros as ground truth."
        $failures++
    } else {
        Write-Output "      $mode.Run refuses to start when UnityEngine.Random already reads as all zeros."
    }
}

if ($SelfTest) {
    # The positive half. Every gate above is of the form "this must NOT appear", and a type-name string
    # with a typo in it makes all of them pass vacuously. So: assert the things that MUST be there.
    Write-Output ""
    Write-Output "== self test: the field-coverage gate detects a field that is genuinely unread =="

    # The honest probe for THIS gate is not a planted waiver - it is a field the plugin really does
    # not read. Every waived field is one: the waiver is the claim that nothing reads it, so if the
    # scan finds a read of one, either the waiver is a lie or the scan is matching the wrong key.
    # Both are failures worth catching, and this asserts the scan can tell the two states apart.
    $probeFound = 0
    foreach ($key in $fieldWaivers.Keys) {
        $checks++
        if ($fieldReads.ContainsKey($key)) {
            Write-Output ("FAIL  self test: {0} is waived as unread, but the IL scan finds a read of it" -f $key)
            $failures++
        } else {
            $probeFound++
        }
    }
    Write-Output ("      {0} waived field(s) confirmed absent from the read set, so the scan " `
                  -f $probeFound)
    Write-Output ("      distinguishes read from unread rather than matching everything.")

    # And the reverse direction: a key the scan DOES hold must be a field of the type it names.
    $checks++
    if (-not $fieldReads.ContainsKey('DungeonGenerator::m_maxTilt')) {
        Write-Output "FAIL  self test: the scan does not hold DungeonGenerator::m_maxTilt, which the dump reads"
        $failures++
    } else {
        Write-Output "      DungeonGenerator::m_maxTilt is in the read set, as the dump's own code requires."
    }

    Write-Output ""
    Write-Output "== self test: the RandomGuard gates are looking at real metadata =="
    $checks++
    $bad = @()
    if (-not $guard)                { $bad += "RandomGuard type not found by name" }
    if (-not $safe)                 { $bad += "RandomStateSafe type not found by name" }
    if ($newobjHits.Count -lt 1)    { $bad += "no 'newobj RandomGuard::.ctor' found - the newobj matcher is dead" }
    if ($setStateHits.Count -ne 1)  { $bad += ("expected exactly 1 set_state, found {0}" -f $setStateHits.Count) }
    if ($restoreHits.Count -lt 4)   { $bad += ("expected at least 4 calls to RandomStateSafe.Restore, found {0}" -f $restoreHits.Count) }
    if ($initStateHits.Count -lt 3) { $bad += ("expected at least 3 InitState calls, found {0}" -f $initStateHits.Count) }
    if ($stateMachines.Count -lt 5) { $bad += ("expected at least 5 state machines, found {0}" -f $stateMachines.Count) }
    Write-Output ("      RandomGuard newobj sites: {0};  set_state: {1};  Restore calls: {2};  InitState: {3};  state machines: {4}." -f
                  $newobjHits.Count, $setStateHits.Count, $restoreHits.Count, $initStateHits.Count, $stateMachines.Count)
    if ($bad.Count -gt 0) {
        Write-Output "FAIL  the RandomGuard gates are not seeing what they claim to see:"
        foreach ($b in $bad) { Write-Output ("        " + $b) }
        $failures++
    } else {
        Write-Output "      every positive probe hit, so the gates above are reading real metadata."
    }
}

# --------------------------------------------------------------------------------------------------
Write-Output ""
Write-Output "== referenced assemblies resolve =="
foreach ($r in $plug.AssemblyReferences) {
    $n = $r.Name
    if ($n -like "System*" -or $n -eq "netstandard" -or $n -eq "mscorlib") { continue }
    $checks++
    $p1 = Join-Path $managed ($n + ".dll")
    $p2 = Join-Path $core ($n + ".dll")
    $p3 = Join-Path (Split-Path $Plugin -Parent) ($n + ".dll")
    if ((Test-Path $p1) -or (Test-Path $p2) -or (Test-Path $p3)) {
        Write-Output ("      ok  {0}" -f $n)
    } else {
        Write-Output ("FAIL  {0}.dll cannot be resolved from the game folder or next to the plugin" -f $n)
        $failures++
    }
}

Write-Output ""
if ($failures -eq 0) {
    Write-Output ("PASS  {0} checks, 0 failures." -f $checks)
    exit 0
} else {
    Write-Output ("FAIL  {0} checks, {1} failure(s)." -f $checks, $failures)
    exit 1
}
