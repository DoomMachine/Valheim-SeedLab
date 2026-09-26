<#
.SYNOPSIS
  Runs the game's rounding helpers on the game's own Mono runtime, with and without -O=-float32.

.DESCRIPTION
  The oracle behind tests\SeedLab.Tests -- rounding. It
    1. builds MonoHost (a 64-bit .NET program that loads the GAME's MonoBleedingEdge\EmbedRuntime\
       mono-2.0-bdwgc.dll in place and runs a .NET Framework program on it);
    2. compiles RoundingProbe.cs with the .NET Framework's own C# compiler (C# 5; no game assembly is
       referenced - the probe reads the IL of Utils.FloorToInt, Utils.RoundToInt and
       AltBiomeWorldData.WorldSpaceToMapSpace out of the game's DLLs by reflection at run time and
       re-emits it byte for byte);
    3. runs the probe twice on the game's runtime: with -O=-float32 (the option string UnityPlayer.dll
       passes to mono_jit_parse_options) and with the runtime's defaults.
  The first run's values are the "game" column the test asserts; the second reproduces what .NET computes.

  It reads the game folder and writes only under -OutDir (default %TEMP%\seedlab-monoprobe). Nothing is
  written to the game folder, and nothing of the game's is copied.

.EXAMPLE
  .\tools\SeedLab.MonoProbe\run-rounding-probe.ps1
  .\tools\SeedLab.MonoProbe\run-rounding-probe.ps1 -ValheimDir "D:\Steam\steamapps\common\Valheim"
#>
param(
    [string]$ValheimDir = "",
    [string]$OutDir = ""
)
$ErrorActionPreference = "Stop"

function Test-GameDir([string]$d) {
    return ($d -ne "") -and (Test-Path (Join-Path $d "valheim_Data\Managed\assembly_valheim.dll"))
}

if ($ValheimDir -eq "" -and $env:SEEDLAB_VALHEIM_DIR -and (Test-GameDir $env:SEEDLAB_VALHEIM_DIR)) { $ValheimDir = $env:SEEDLAB_VALHEIM_DIR }
if ($ValheimDir -eq "") {
    # SeedLab may live inside the game folder: walk up from here.
    $d = $PSScriptRoot
    for ($i = 0; $i -lt 12 -and $d; $i++) {
        if (Test-GameDir $d) { $ValheimDir = $d; break }
        $d = Split-Path $d -Parent
    }
}
if (-not (Test-GameDir $ValheimDir)) { throw "Valheim not found: pass -ValheimDir or set SEEDLAB_VALHEIM_DIR." }
if ($OutDir -eq "") { $OutDir = Join-Path $env:TEMP "seedlab-monoprobe" }

$mono = Join-Path $ValheimDir "MonoBleedingEdge\EmbedRuntime\mono-2.0-bdwgc.dll"
$managed = Join-Path $ValheimDir "valheim_Data\Managed"
$etc = Join-Path $ValheimDir "MonoBleedingEdge\etc"
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $mono)) { throw "The game's Mono runtime is not at $mono." }
if (-not (Test-Path $csc)) { throw "The .NET Framework C# compiler is not at $csc." }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$artifacts = Join-Path $OutDir "artifacts"
& dotnet build (Join-Path $PSScriptRoot "MonoHost\MonoHost.csproj") -c Release --artifacts-path $artifacts -nodeReuse:false -p:UseSharedCompilation=false -v quiet | Out-Null
if ($LASTEXITCODE -ne 0) { throw "MonoHost did not build." }
$h = Get-ChildItem (Join-Path $artifacts "bin") -Recurse -Filter MonoHost.exe | Select-Object -First 1 -ExpandProperty FullName
if (-not $h) { throw "MonoHost.exe was not found under $artifacts." }
$probe = Join-Path $OutDir "RoundingProbe.exe"
& $csc /nologo /optimize+ "/out:$probe" (Join-Path $PSScriptRoot "RoundingProbe.cs")
if ($LASTEXITCODE -ne 0) { throw "RoundingProbe.cs did not compile." }

"# the game's Mono runtime: $mono (SHA-256 $((Get-FileHash $mono -Algorithm SHA256).Hash))"
"## WITH -O=-float32 (the option UnityPlayer.dll passes)"
& cmd /c "`"$h`" `"$mono`" `"$managed`" `"$etc`" `"$probe`" -O=-float32 2>&1"
"exit=$LASTEXITCODE"
"## WITHOUT it (the runtime's defaults)"
& cmd /c "`"$h`" `"$mono`" `"$managed`" `"$etc`" `"$probe`" 2>&1"
"exit=$LASTEXITCODE"
