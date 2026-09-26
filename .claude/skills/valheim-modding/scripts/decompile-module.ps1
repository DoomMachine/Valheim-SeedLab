<#
.SYNOPSIS
  Decompiles a WHOLE assembly (usually a plugin) to one C# file plus its full IL disassembly.

.DESCRIPTION
  decompile.ps1 answers "what does this one game type/member do"; this one is for reading a small
  assembly end to end - a third-party plugin you are reviewing, or your own build output. Every type is
  included, nested ones too (iterator bodies, lambdas' display classes). The IL file settles what the
  C# view hides: default arguments passed explicitly, the exact overload called, field accesses.

  References are resolved from the game's Managed folder, BepInEx\core, BepInEx\plugins and the
  assembly's own folder, so member names and overloads come out right.

  Output goes to -OutDir (default: the current directory's "decompiled" subfolder). Never write decompiled
  game code into a folder you might publish.

  First run builds the tool from .\decompiler-module into
  %LOCALAPPDATA%\valheim-modding-tools\decompiler-module (needs the .NET SDK and an installed ILSpy).

.EXAMPLE
  .\decompile-module.ps1 -Assembly "<Valheim>\BepInEx\plugins\SomeMod\SomeMod.dll" -OutDir .\decompiled-somemod
#>
param(
    [Parameter(Mandatory = $true)] [string]$Assembly,   # name (searched in Managed and BepInEx\core) or full path
    [string]$OutDir = "",
    [string]$ILSpyDir = "",
    [string]$ValheimDir = ""
)
$ErrorActionPreference = "Stop"
$SkipCecil = $true
. (Join-Path $PSScriptRoot "_common.ps1")

if ($ILSpyDir -eq "") { $ILSpyDir = Join-Path $env:LOCALAPPDATA "Programs\ILSpy" }
if (-not (Test-Path (Join-Path $ILSpyDir "ICSharpCode.Decompiler.dll"))) {
    throw "ILSpy not found at $ILSpyDir (need ICSharpCode.Decompiler.dll). Install ILSpy or pass -ILSpyDir."
}

$src = Join-Path $PSScriptRoot "decompiler-module"
$cache = Join-Path $env:LOCALAPPDATA "valheim-modding-tools\decompiler-module"
$exe = Join-Path $cache "out\decompmod.dll"
$stamp = Join-Path $cache "source.hash"

$hash = (Get-FileHash (Join-Path $src "Program.cs")).Hash + (Get-FileHash (Join-Path $src "decompmod.csproj")).Hash
$built = (Test-Path $exe) -and (Test-Path $stamp) -and ((Get-Content $stamp -Raw).Trim() -eq $hash)
if (-not $built) {
    New-Item -ItemType Directory -Force -Path $cache | Out-Null
    Copy-Item (Join-Path $src "Program.cs") $cache -Force
    Copy-Item (Join-Path $src "decompmod.csproj") $cache -Force
    Write-Host "Building module decompiler into $cache ..." -ForegroundColor DarkGray
    & dotnet build (Join-Path $cache "decompmod.csproj") -c Release -o (Join-Path $cache "out") -v quiet "-p:ILSpyDir=$ILSpyDir" | Out-Null
    if (-not (Test-Path $exe)) { throw "Module decompiler build failed - run: dotnet build `"$cache\decompmod.csproj`"" }
    Set-Content -Path $stamp -Value $hash
}

$asmPath = Get-GameAssemblyPath $Assembly
if ($OutDir -eq "") { $OutDir = Join-Path (Get-Location).Path "decompiled" }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$base = [IO.Path]::GetFileNameWithoutExtension($asmPath)
$cs = Join-Path $OutDir ($base + ".cs")
$il = Join-Path $OutDir ($base + ".il")

& dotnet $exe $asmPath $cs $il $GameManaged $BepInExCore (Join-Path $ValheimDir "BepInEx\plugins") (Split-Path $asmPath -Parent)
if ($LASTEXITCODE -ne 0) { throw "decompmod failed with exit code $LASTEXITCODE" }
