<#
.SYNOPSIS
  Decompiles a game type, or one member of it, to readable C#.

.DESCRIPTION
  The single most useful tool for this game. Read what vanilla actually does before patching it:
  signatures, filters, orderings, constants. Answers from decompiled code have repeatedly overturned
  reasonable-sounding assumptions (for example: Minimap.GetClosestPin skips every pin with m_save
  false; OnMapLeftClick toggles a pin's checkmark rather than placing a pin).

  First run builds the tool from .\decompiler into %LOCALAPPDATA%\valheim-modding-tools\decompiler
  (needs the .NET SDK and an installed ILSpy). Later runs reuse it and only rebuild if the source changed.

.EXAMPLE
  .\decompile.ps1 -Type Minimap -Member AddPin
  .\decompile.ps1 -Type WorldGenerator -Member GetBiome
  .\decompile.ps1 -Type ZInput -Assembly assembly_utils
  .\decompile.ps1 -Type BepInEx.Bootstrap.Chainloader -Member Start -Assembly BepInEx
  .\decompile.ps1 -Type Plugin -Assembly "<Valheim>\BepInEx\plugins\SomeMod\SomeMod.dll"
#>
param(
    [Parameter(Mandatory = $true)] [string]$Type,
    [string]$Member = "",
    [string]$Assembly = "assembly_valheim",   # name (searched in Managed and BepInEx\core) or full path
    [string]$ILSpyDir = "",
    [string]$ValheimDir = ""
)
$ErrorActionPreference = "Stop"
$SkipCecil = $true   # ILSpy's engine reads the assembly; Mono.Cecil is not needed here
. (Join-Path $PSScriptRoot "_common.ps1")

if ($ILSpyDir -eq "") { $ILSpyDir = Join-Path $env:LOCALAPPDATA "Programs\ILSpy" }
if (-not (Test-Path (Join-Path $ILSpyDir "ICSharpCode.Decompiler.dll"))) {
    throw "ILSpy not found at $ILSpyDir (need ICSharpCode.Decompiler.dll). Install ILSpy or pass -ILSpyDir."
}

$src = Join-Path $PSScriptRoot "decompiler"
$cache = Join-Path $env:LOCALAPPDATA "valheim-modding-tools\decompiler"
$exe = Join-Path $cache "out\decomp.dll"
$stamp = Join-Path $cache "source.hash"

# Rebuild only when the bundled source changed (or was never built).
$hash = (Get-FileHash (Join-Path $src "Program.cs")).Hash + (Get-FileHash (Join-Path $src "decomp.csproj")).Hash
$built = (Test-Path $exe) -and (Test-Path $stamp) -and ((Get-Content $stamp -Raw).Trim() -eq $hash)
if (-not $built) {
    New-Item -ItemType Directory -Force -Path $cache | Out-Null
    Copy-Item (Join-Path $src "Program.cs") $cache -Force
    Copy-Item (Join-Path $src "decomp.csproj") $cache -Force
    Write-Host "Building decompiler into $cache ..." -ForegroundColor DarkGray
    & dotnet build (Join-Path $cache "decomp.csproj") -c Release -o (Join-Path $cache "out") -v quiet "-p:ILSpyDir=$ILSpyDir" | Out-Null
    if (-not (Test-Path $exe)) { throw "Decompiler build failed - run: dotnet build `"$cache\decomp.csproj`"" }
    Set-Content -Path $stamp -Value $hash
}

$asmPath = Get-GameAssemblyPath $Assembly
if ($Member -ne "") { & dotnet $exe $asmPath $Type $Member } else { & dotnet $exe $asmPath $Type }
