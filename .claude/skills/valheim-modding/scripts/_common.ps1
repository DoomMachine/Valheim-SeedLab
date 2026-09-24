# Shared setup for the Valheim inspection scripts. Dot-source it:  . (Join-Path $PSScriptRoot "_common.ps1")
#
# Provides:
#   $ValheimDir            the game folder, found in this order: -ValheimDir; $env:SEEDLAB_VALHEIM_DIR;
#                          the folders above this script (these skills may sit inside the game folder);
#                          the Steam libraries (registry SteamPath, the default Steam folders, and every
#                          library listed in their steamapps\libraryfolders.vdf)
#   $GameManaged           <game>\valheim_Data\Managed
#   $BepInExCore           <game>\BepInEx\core
#   Get-GameAssemblyPath   resolves "assembly_valheim" / "assembly_valheim.dll" / a full path
#   Get-GameCodeAssemblies the four assemblies that hold Valheim's own code
#   Read-Module            opens an assembly with Mono.Cecil, resolving references from the game folder
#
# Mono.Cecil ships with BepInEx (BepInEx\core\Mono.Cecil.dll), so nothing needs installing. A script that
# never reads a module sets $SkipCecil = $true before dot-sourcing this file (check-game-version.ps1
# does), so it also runs on an install without BepInEx.
# Written for Windows PowerShell 5.1: no ?:, no ??, no ?. operators.

function Test-ValheimDir([string]$dir) {
    if (-not $dir) { return $false }
    return (Test-Path (Join-Path $dir "valheim_Data\Managed\assembly_valheim.dll"))
}

function Find-ValheimDir {
    # An explicitly set SEEDLAB_VALHEIM_DIR is authoritative even when wrong (as in vseed): stop there
    # rather than quietly inspecting a different copy of the game than the one named.
    if ($env:SEEDLAB_VALHEIM_DIR) {
        if (Test-ValheimDir $env:SEEDLAB_VALHEIM_DIR) { return (Resolve-Path $env:SEEDLAB_VALHEIM_DIR).Path }
        throw "SEEDLAB_VALHEIM_DIR is set but is not a Valheim folder (no valheim_Data\Managed\assembly_valheim.dll): $env:SEEDLAB_VALHEIM_DIR"
    }

    # Walk up from this script: finds the game when the skills sit anywhere inside the game folder.
    $d = $PSScriptRoot
    while ($d) {
        if (Test-ValheimDir $d) { return $d }
        $parent = Split-Path $d -Parent
        if ($parent -eq $d) { break }
        $d = $parent
    }

    # Steam: the registry's SteamPath and the default folders, then every library they list.
    $steamRoots = @()
    try {
        $reg = Get-ItemProperty -Path "HKCU:\Software\Valve\Steam" -Name SteamPath -ErrorAction Stop
        if ($reg.SteamPath) { $steamRoots += $reg.SteamPath }
    } catch { }
    if (${env:ProgramFiles(x86)}) { $steamRoots += (Join-Path ${env:ProgramFiles(x86)} "Steam") }
    if ($env:ProgramFiles) { $steamRoots += (Join-Path $env:ProgramFiles "Steam") }

    $libraries = @()
    foreach ($root in $steamRoots) {
        if (-not (Test-Path $root)) { continue }
        $libraries += $root
        $vdf = Join-Path $root "steamapps\libraryfolders.vdf"
        if (Test-Path $vdf) {
            foreach ($m in (Select-String -Path $vdf -Pattern '"path"\s+"([^"]+)"' -AllMatches)) {
                foreach ($mm in $m.Matches) { $libraries += ($mm.Groups[1].Value -replace '\\\\', '\') }
            }
        }
    }
    foreach ($lib in ($libraries | Select-Object -Unique)) {
        $candidate = Join-Path $lib "steamapps\common\Valheim"
        if (Test-ValheimDir $candidate) { return (Resolve-Path $candidate).Path }
    }
    return $null
}

if (-not $ValheimDir -or $ValheimDir -eq "") {
    $ValheimDir = Find-ValheimDir
    if (-not $ValheimDir) {
        throw ("Cannot find the Valheim folder. Pass -ValheimDir '<the folder holding valheim_Data>' " +
               "or set SEEDLAB_VALHEIM_DIR to it.")
    }
} elseif (-not (Test-ValheimDir $ValheimDir)) {
    throw "Not a Valheim folder (no valheim_Data\Managed\assembly_valheim.dll): $ValheimDir"
}

$GameManaged = Join-Path $ValheimDir "valheim_Data\Managed"
$BepInExCore = Join-Path $ValheimDir "BepInEx\core"

function Initialize-Cecil {
    if ("Mono.Cecil.ModuleDefinition" -as [type]) { return }
    $cecil = Join-Path $BepInExCore "Mono.Cecil.dll"
    if (-not (Test-Path $cecil)) {
        throw "Mono.Cecil not found at $cecil - these scripts use the copy that ships with BepInEx."
    }
    Add-Type -Path $cecil
}
if (-not $SkipCecil) { Initialize-Cecil }

function Get-GameCodeAssemblies {
    # Where Valheim's own code lives. Assembly-CSharp.dll is only a stub in this build.
    return @("assembly_valheim", "assembly_utils", "assembly_guiutils", "gui_framework") |
        ForEach-Object { Join-Path $GameManaged ($_ + ".dll") }
}

function Get-GameAssemblyPath([string]$name) {
    if ($name -eq "") { return $null }
    if (Test-Path $name) { return (Resolve-Path $name).Path }
    $n = $name
    if (-not $n.EndsWith(".dll")) { $n = $n + ".dll" }
    foreach ($dir in @($GameManaged, $BepInExCore)) {
        $p = Join-Path $dir $n
        if (Test-Path $p) { return $p }
    }
    throw "Assembly not found: $name (looked in $GameManaged and $BepInExCore)"
}

function Read-Module([string]$path) {
    Initialize-Cecil
    $resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
    $resolver.AddSearchDirectory($GameManaged)
    $resolver.AddSearchDirectory($BepInExCore)
    $rp = New-Object Mono.Cecil.ReaderParameters
    $rp.AssemblyResolver = $resolver
    return [Mono.Cecil.ModuleDefinition]::ReadModule($path, $rp)
}

function Get-Visibility($member) {
    if ($member.IsPublic) { return "public" }
    if ($member.IsFamily) { return "protected" }
    if ($member.IsAssembly) { return "internal" }
    return "PRIVATE"
}
