<#
.SYNOPSIS
  Says whether the installed Valheim is the build SeedLab was verified against.

.DESCRIPTION
  SeedLab reproduces one specific Valheim build. A build is identified by the SHA-256 of
  valheim_Data\Managed\assembly_valheim.dll: same hash, same game code. This script hashes the
  installed file and compares it with the verified one. It only reads files; it writes nothing and
  needs nothing but Windows PowerShell 5.1 or PowerShell 7 (no BepInEx, no build of SeedLab).

  The game is found in this order (vseed searches the same way, walking up from its own folder
  rather than from this script's):
    1. -ValheimDir
    2. the SEEDLAB_VALHEIM_DIR environment variable
    3. walking up from the current directory and from this script's folder, which finds the game
       when the repository is kept inside the game folder
    4. the usual Steam library folders: C:\Program Files (x86)\Steam, C:\Program Files\Steam and
       <drive>:\SteamLibrary / <drive>:\Steam for D: to Z:, each followed by steamapps\common\Valheim
  1 and 2 are authoritative: when either names a folder that does not hold the game, nothing else is
  searched, because checking a different install than the one named would answer the wrong question.

  The verified hash is taken from, in order:
    1. -Expected <sha256>
    2. src\SeedLab.Cli\Infra\Verified.cs in this repository (the AssemblyValheimSha256 constant)
    3. data\<version>-<hash>\manifest.json (game.assemblyValheimSha256), when a data folder exists;
       if there are several, matching any one of them counts as a match
  When the verified hash comes from 1 or 2 and data folders exist, each one's stamp is reported as
  well, because the game data vseed uses has to match the installed game too.

  Also printed, when they can be found: the game version and network version the game logged in
  BepInEx\LogOutput.log, and the Steam build id from steamapps\appmanifest_892970.acf. Both are
  informational; only the hash decides.

  Exit codes. These are this script's own; vseed uses 2 for a bad command line.
    0  the installed game is the verified build
    2  the game changed: the installed assembly_valheim.dll is not the verified one
    3  the game was not found
    1  no check could be made: nothing to compare against, a malformed -Expected, or an
       unexpected error

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\check-game-version.ps1

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\check-game-version.ps1 -ValheimDir "X:\MySteam\steamapps\common\Valheim"

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\check-game-version.ps1 -Expected 59f53fb55d99d22a33e8ed094eec8d21e9f133543bce92bc3d80dce44033adb1
#>
param(
    [string]$ValheimDir = "",
    [string]$Expected = ""
)
$ErrorActionPreference = "Stop"

$AssemblyRelative = "valheim_Data\Managed\assembly_valheim.dll"

function Test-GameDir([string]$dir) {
    if (-not $dir) { return $false }
    try { return [IO.File]::Exists((Join-Path $dir $AssemblyRelative)) } catch { return $false }
}

# Reads a file that another process may hold open (the game keeps LogOutput.log open while it runs).
function Read-SharedText([string]$path) {
    $share = [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete
    $fs = New-Object IO.FileStream($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, $share)
    try {
        $reader = New-Object IO.StreamReader($fs, (New-Object Text.UTF8Encoding($false)), $true)
        return $reader.ReadToEnd()
    } finally {
        $fs.Dispose()
    }
}

function Get-SteamCandidates {
    $roots = New-Object System.Collections.Generic.List[string]
    $roots.Add("C:\Program Files (x86)\Steam")
    $roots.Add("C:\Program Files\Steam")
    if ($HOME) {
        $roots.Add((Join-Path $HOME ".steam\steam"))
        $roots.Add((Join-Path $HOME ".local\share\Steam"))
        $roots.Add((Join-Path $HOME "Library\Application Support\Steam"))
    }
    foreach ($code in 68..90) {
        $drive = [string][char]$code
        $roots.Add($drive + ":\SteamLibrary")
        $roots.Add($drive + ":\Steam")
    }
    return $roots
}

# --- 1. find the game -----------------------------------------------------------------------------
$gameDir = $null
$foundBy = ""
$whyNot = ""

if ($ValheimDir -ne "") {
    if (Test-GameDir $ValheimDir) { $gameDir = $ValheimDir; $foundBy = "-ValheimDir" }
    else { $whyNot = "-ValheimDir '$ValheimDir' has no $AssemblyRelative." }
} elseif ($env:SEEDLAB_VALHEIM_DIR) {
    if (Test-GameDir $env:SEEDLAB_VALHEIM_DIR) { $gameDir = $env:SEEDLAB_VALHEIM_DIR; $foundBy = "SEEDLAB_VALHEIM_DIR" }
    else {
        $whyNot = "SEEDLAB_VALHEIM_DIR is set to '$($env:SEEDLAB_VALHEIM_DIR)', which has no " +
                  "$AssemblyRelative. Nothing else was searched."
    }
} else {
    $starts = New-Object System.Collections.Generic.List[string]
    try { $starts.Add((Get-Location).ProviderPath) } catch { }
    if ($PSScriptRoot) { $starts.Add($PSScriptRoot) }
    foreach ($start in $starts) {
        $d = $start
        for ($i = 0; $i -lt 12 -and $d; $i++) {
            if (Test-GameDir $d) { $gameDir = $d; $foundBy = "walking up from $start"; break }
            $d = Split-Path $d -Parent
        }
        if ($gameDir) { break }
    }
    if (-not $gameDir) {
        foreach ($root in (Get-SteamCandidates)) {
            $cand = Join-Path $root "steamapps\common\Valheim"
            if (Test-GameDir $cand) { $gameDir = $cand; $foundBy = "the Steam library at $root"; break }
        }
    }
    if (-not $gameDir) {
        $whyNot = "no $AssemblyRelative was found above the current directory or this script, or in " +
                  "the usual Steam library folders. Pass -ValheimDir or set SEEDLAB_VALHEIM_DIR."
    }
}

if (-not $gameDir) {
    Write-Output "Valheim not found: $whyNot"
    exit 3
}

# --- 2. what is installed --------------------------------------------------------------------------
$asm = Join-Path $gameDir $AssemblyRelative
$hash = (Get-FileHash -LiteralPath $asm -Algorithm SHA256).Hash.ToLowerInvariant()

$version = "unknown"; $network = "unknown"
$log = Join-Path $gameDir "BepInEx\LogOutput.log"
if ([IO.File]::Exists($log)) {
    try {
        $ms = [regex]::Matches((Read-SharedText $log), 'Valheim version: (\S+) \(network version (\d+)\)')
        if ($ms.Count -gt 0) {
            $last = $ms[$ms.Count - 1]
            $version = $last.Groups[1].Value
            $network = $last.Groups[2].Value
        }
    } catch { }
}

$build = "unknown"
$steamapps = Split-Path (Split-Path $gameDir -Parent) -Parent
if ($steamapps) {
    $acf = Join-Path $steamapps "appmanifest_892970.acf"
    if ([IO.File]::Exists($acf)) {
        try {
            $b = [regex]::Match((Read-SharedText $acf), '"buildid"\s+"(\d+)"')
            if ($b.Success) { $build = $b.Groups[1].Value }
        } catch { }
    }
}

# --- 3. what SeedLab was verified against ----------------------------------------------------------
$repo = ""
if ($PSScriptRoot) { $repo = Split-Path $PSScriptRoot -Parent }

$dataStamps = @()
if ($repo) {
    $dataRoot = Join-Path $repo "data"
    if ([IO.Directory]::Exists($dataRoot)) {
        foreach ($dir in (Get-ChildItem -LiteralPath $dataRoot -Directory | Sort-Object Name)) {
            $manifest = Join-Path $dir.FullName "manifest.json"
            if (-not [IO.File]::Exists($manifest)) { continue }
            try {
                $m = [IO.File]::ReadAllText($manifest) | ConvertFrom-Json
                $sha = [string]$m.game.assemblyValheimSha256
                if ($sha -match '^[0-9a-fA-F]{64}$') {
                    $dataStamps += New-Object PSObject -Property @{
                        Folder  = "data\" + $dir.Name
                        Sha     = $sha.ToLowerInvariant()
                        Version = [string]$m.game.version
                    }
                }
            } catch { }
        }
    }
}

$expectedHash = ""; $expectedFrom = ""; $expectedDesc = ""
if ($Expected -ne "") {
    if ($Expected -notmatch '^[0-9a-fA-F]{64}$') {
        Write-Output "-Expected must be a SHA-256: 64 hexadecimal digits."
        exit 1
    }
    $expectedHash = $Expected.ToLowerInvariant()
    $expectedFrom = "-Expected"
    $expectedDesc = "assembly_valheim-sha256=$expectedHash"
} else {
    $verifiedCs = ""
    if ($repo) { $verifiedCs = Join-Path $repo "src\SeedLab.Cli\Infra\Verified.cs" }
    if ($verifiedCs -and [IO.File]::Exists($verifiedCs)) {
        $src = [IO.File]::ReadAllText($verifiedCs)
        $h = [regex]::Match($src, 'AssemblyValheimSha256\s*=\s*"([0-9a-fA-F]{64})"')
        if ($h.Success) {
            $expectedHash = $h.Groups[1].Value.ToLowerInvariant()
            $expectedFrom = "src\SeedLab.Cli\Infra\Verified.cs"
            $gv = [regex]::Match($src, 'GameVersion\s*=\s*"([^"]+)"')
            $nv = [regex]::Match($src, 'NetworkVersion\s*=\s*(\d+)')
            $sb = [regex]::Match($src, 'SteamBuildId\s*=\s*"(\d+)"')
            $expectedDesc = "game-version=" + $(if ($gv.Success) { $gv.Groups[1].Value } else { "unknown" }) +
                            " network=" + $(if ($nv.Success) { $nv.Groups[1].Value } else { "unknown" }) +
                            " steam-build=" + $(if ($sb.Success) { $sb.Groups[1].Value } else { "unknown" }) +
                            " assembly_valheim-sha256=$expectedHash"
        }
    }
}

if ($expectedHash -eq "" -and $dataStamps.Count -eq 0) {
    Write-Output "Nothing to compare against: no -Expected, no src\SeedLab.Cli\Infra\Verified.cs next to"
    Write-Output "this script, and no data\<version>-<hash>\manifest.json. Pass -Expected <sha256>."
    Write-Output ("installed assembly_valheim.dll sha256 is {0}" -f $hash)
    exit 1
}

# --- 4. report --------------------------------------------------------------------------------------
Write-Output ("installed : {0}" -f $gameDir)
Write-Output ("            found via {0}" -f $foundBy)
Write-Output ("            game-version={0} network={1} steam-build={2} assembly_valheim-sha256={3}" -f $version, $network, $build, $hash)

$match = $false
if ($expectedHash -ne "") {
    Write-Output ("verified  : {0}" -f $expectedDesc)
    Write-Output ("            from {0}" -f $expectedFrom)
    $match = ($expectedHash -eq $hash)
    foreach ($ds in $dataStamps) {
        $verdict = $(if ($ds.Sha -eq $hash) { "matches the installed game" } else { "does NOT match the installed game" })
        Write-Output ("data      : {0} (Valheim {1}) assembly_valheim-sha256={2} - {3}" -f $ds.Folder, $ds.Version, $ds.Sha, $verdict)
    }
} else {
    foreach ($ds in $dataStamps) {
        $verdict = $(if ($ds.Sha -eq $hash) { "matches the installed game" } else { "does NOT match the installed game" })
        Write-Output ("verified  : {0} (Valheim {1}) assembly_valheim-sha256={2} - {3}" -f $ds.Folder, $ds.Version, $ds.Sha, $verdict)
        if ($ds.Sha -eq $hash) { $match = $true }
    }
}

if ($match) {
    Write-Output "OK - the installed game is the build SeedLab was verified against."
    exit 0
}

Write-Output ""
Write-Output "GAME CHANGED - the installed assembly_valheim.dll is not the build SeedLab was verified"
Write-Output "against. Until the new build is re-verified:"
Write-Output "  1. run tools\SeedLab.Dumper\preflight.ps1 - do the dumper's patch targets and the game"
Write-Output "     members it reads still exist?"
Write-Output "  2. re-dump the game data into a NEW folder data\<version>-<first 8 hex of the hash>\"
Write-Output "     (docs\dumper.md, tools\SeedLab.Dumper\README.md); vseed refuses location answers while"
Write-Output "     its data does not match the installed game"
Write-Output "  3. if you have the ground truth (groundtruth\), run vseed selftest"
exit 2
