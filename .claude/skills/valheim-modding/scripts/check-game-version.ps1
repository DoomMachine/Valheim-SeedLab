<#
.SYNOPSIS
  Says whether Valheim has changed since the knowledge base was last verified.

.DESCRIPTION
  Every fact in the valheim-* skills was verified against one specific game build. That build is
  recorded as a KB-STAMP line in references/environment.md. This compares it with what is installed now:
    - the game version the game itself logs ("Valheim version: 1.0.15 (network version 40)")
    - the Steam build id from appmanifest_892970.acf
    - the SHA-256 of assembly_valheim.dll (the one that really matters: same hash = same code)
  Exit code 0 = unchanged, 2 = the game changed: re-run the project preflight, re-verify any fact you
  are about to rely on, then update the stamp with -UpdateStamp once the knowledge base is re-checked.

.EXAMPLE
  .\check-game-version.ps1
  .\check-game-version.ps1 -UpdateStamp      # only after re-verifying the knowledge base
#>
param(
    [switch]$UpdateStamp,
    [string]$ValheimDir = ""
)
$ErrorActionPreference = "Stop"
$SkipCecil = $true   # only hashes and reads text files; works without BepInEx
. (Join-Path $PSScriptRoot "_common.ps1")

$envDoc = Join-Path $PSScriptRoot "..\references\environment.md"

# --- what is installed now
$asm = Join-Path $GameManaged "assembly_valheim.dll"
$hash = (Get-FileHash $asm -Algorithm SHA256).Hash.ToLowerInvariant()

$version = "unknown"; $network = "unknown"
$log = Join-Path $ValheimDir "BepInEx\LogOutput.log"
if (Test-Path $log) {
    $m = Select-String -Path $log -Pattern 'Valheim version: (\S+) \(network version (\d+)\)' | Select-Object -Last 1
    if ($m) { $version = $m.Matches[0].Groups[1].Value; $network = $m.Matches[0].Groups[2].Value }
}

$build = "unknown"
$manifest = Join-Path (Split-Path (Split-Path $ValheimDir -Parent) -Parent) "appmanifest_892970.acf"
if (Test-Path $manifest) {
    $b = Select-String -Path $manifest -Pattern '"buildid"\s+"(\d+)"' | Select-Object -First 1
    if ($b) { $build = $b.Matches[0].Groups[1].Value }
}

$current = "KB-STAMP game-version=$version network=$network steam-build=$build assembly_valheim-sha256=$hash"

# --- what the knowledge base was verified against
$stampLine = ""
if (Test-Path $envDoc) {
    $s = Select-String -Path $envDoc -Pattern '^KB-STAMP ' | Select-Object -First 1
    if ($s) { $stampLine = $s.Line.Trim() }
}
$stampHash = ""
if ($stampLine -match 'assembly_valheim-sha256=([0-9a-f]+)') { $stampHash = $Matches[1] }

Write-Output ("installed : Valheim {0} (network {1}), Steam build {2}" -f $version, $network, $build)
Write-Output ("            assembly_valheim.dll sha256 {0}" -f $hash)
Write-Output ("verified  : {0}" -f $(if ($stampLine) { $stampLine } else { "(no KB-STAMP found in $envDoc)" }))

if ($UpdateStamp) {
    # Explicit UTF-8 both ways. Windows PowerShell 5.1's Get-Content reads a BOM-less file as ANSI
    # (cp1252), and Set-Content -Encoding UTF8 adds a BOM - together they turn every em dash in the
    # document into "â€”". That happened to this very file once.
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $path = (Resolve-Path $envDoc).Path
    $text = [System.IO.File]::ReadAllText($path, $utf8)
    if ($text -match '(?m)^KB-STAMP .*$') {
        $text = [regex]::Replace($text, '(?m)^KB-STAMP .*$', $current)
    } else {
        $text = $text.TrimEnd() + "`n`n" + $current + "`n"
    }
    [System.IO.File]::WriteAllText($path, $text, $utf8)
    Write-Output "stamp updated."
    exit 0
}

if ($stampHash -eq $hash) {
    Write-Output "OK - the game code is the build the knowledge base was verified against."
    exit 0
}
Write-Output ""
Write-Output "GAME CHANGED since the knowledge base was verified. Facts about game code may be stale:"
Write-Output "  1. run each mod's preflight.ps1 (Harmony targets and reflected members are what break first)"
Write-Output "  2. re-verify, with decompile.ps1, any fact you are about to rely on"
Write-Output "  3. record what changed in references/environment.md, then run with -UpdateStamp"
exit 2
