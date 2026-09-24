# The five gates at every vector level this machine can reach (design 12.5, ST8).
#
#   powershell -ExecutionPolicy Bypass -File tests\level-matrix.ps1 [-Levels K0,K3,K6,K7,K8] [-Gates ...] [-OutDir <folder>]
#
# Build first (dotnet build -c Release for src\SeedLab.Cli, tests\SeedLab.Acceptance.Tests,
# tests\SeedLab.Tests, tools\SeedLab.GoldenCheck and tools\SeedLab.LocationLab). Every gate is the BUILT
# executable, started directly under one runtime knob or request - never 'dotnet run', so no build tool
# runs under a knob and nothing is rebuilt between levels:
#
#   K0  no knob                        the default level
#   K3  DOTNET_EnableAVX512=0          AVX-512 off (the knob is EnableAVX512; EnableAVX512F does not exist)
#   K6  DOTNET_EnableAVX2=0            AVX2 off: the scalar Perlin path, and AVX-512 goes with it
#   K7  DOTNET_EnableHWIntrinsic=0     every intrinsic off, all vector acceleration off
#   K8  SEEDLAB_SIMD=scalar            every ISA on, the scalar path by request
#
# Each process also gets SEEDLAB_SIMD_EXPECT, so the WorldGen module initialiser refuses to load unless
# the knob had the effect the level needs: a gate that passes at a level has proved, in its own process,
# that it ran there. A level whose knob can have no effect on this CPU is skipped and said so.
#
# The gates need groundtruth\ and data\ where they look for them (above the working directory, or
# SEEDLAB_DATA_DIR for data\). Output of every run goes to -OutDir; the table at the end is the result.
# The C runtime's libm is not switched by any knob: a CPU without FMA3 is not simulated here.

param(
    [string[]]$Levels = @('K0', 'K3', 'K6', 'K7', 'K8'),
    [string[]]$Gates = @('acceptance', 'location', 'natives', 'goldencheck', 'selftest'),
    [string]$OutDir = (Join-Path ([IO.Path]::GetTempPath()) 'seedlab-level-matrix')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ($Levels.Count -eq 1 -and $Levels[0].Contains(',')) { $Levels = $Levels[0].Split(',') }
if ($Gates.Count -eq 1 -and $Gates[0].Contains(',')) { $Gates = $Gates[0].Split(',') }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$cache = Join-Path $OutDir 'cache'
New-Item -ItemType Directory -Force $cache | Out-Null

$vseed = Join-Path $root 'src\SeedLab.Cli\bin\Release\net10.0\vseed.exe'
$gateCommands = [ordered]@{
    'acceptance'  = @((Join-Path $root 'tests\SeedLab.Acceptance.Tests\bin\Release\net10.0\SeedLab.Acceptance.Tests.exe'))
    'location'    = @((Join-Path $root 'tools\SeedLab.LocationLab\bin\Release\net10.0\SeedLab.LocationLab.exe'), 'gate')
    'natives'     = @((Join-Path $root 'tests\SeedLab.Tests\bin\Release\net10.0\SeedLab.Tests.exe'), 'natives')
    'goldencheck' = @((Join-Path $root 'tools\SeedLab.GoldenCheck\bin\Release\net10.0\SeedLab.GoldenCheck.exe'))
    'selftest'    = @($vseed, 'selftest', '--cache-dir', $cache)
}

$knobNames = @('DOTNET_EnableAVX512', 'DOTNET_EnableAVX2', 'DOTNET_EnableHWIntrinsic', 'SEEDLAB_SIMD', 'SEEDLAB_SIMD_EXPECT')
function Clear-Knobs { foreach ($k in $knobNames) { Remove-Item "Env:$k" -ErrorAction SilentlyContinue } }

# The default level's facts decide which knobs can show an effect here.
Clear-Knobs
if (-not (Test-Path $vseed)) { Write-Host "FAIL  $vseed is not built"; exit 1 }
$isa = (& $vseed selftest --isa-json --cache-dir $cache | Out-String | ConvertFrom-Json).facts
Write-Host ("default level: hardware {0}, active {1}" -f $isa.hardware, $isa.active)

$defs = @{
    'K0' = @{ Knob = $null; Value = $null; Applicable = $true; Expect = "requested=auto,active=$($isa.active),avx2=$($isa.avx2),avx512f=$($isa.avx512f)" }
    'K3' = @{ Knob = 'DOTNET_EnableAVX512'; Value = '0'; Applicable = ($isa.avx512f -eq '1'); Expect = "avx512f=0,avx512bw=0,avx2=$($isa.avx2)" }
    'K6' = @{ Knob = 'DOTNET_EnableAVX2'; Value = '0'; Applicable = ($isa.avx2 -eq '1'); Expect = 'avx2=0,avx512f=0,active=scalar' }
    'K7' = @{ Knob = 'DOTNET_EnableHWIntrinsic'; Value = '0'; Applicable = ($isa.x86base -eq '1'); Expect = 'x86base=0,sse2=0,avx2=0,v128acc=0,active=scalar' }
    'K8' = @{ Knob = 'SEEDLAB_SIMD'; Value = 'scalar'; Applicable = ($isa.avx2 -eq '1'); Expect = 'requested=scalar,active=scalar' }
}

$rows = @()
$failed = 0
Push-Location $root
try {
    foreach ($level in $Levels) {
        $d = $defs[$level]
        if ($null -eq $d) { Write-Host "unknown level $level"; $failed++; continue }
        if (-not $d.Applicable) {
            Write-Host "n/a   $level - this CPU lacks what $($d.Knob) turns off"
            continue
        }

        foreach ($gate in $Gates) {
            $cmd = $gateCommands[$gate]
            if ($null -eq $cmd) { Write-Host "unknown gate $gate"; $failed++; continue }
            Clear-Knobs
            if ($d.Knob) { Set-Item "Env:$($d.Knob)" $d.Value }
            $env:SEEDLAB_SIMD_EXPECT = $d.Expect
            $log = Join-Path $OutDir ("{0}-{1}.txt" -f $level, $gate)
            $sw = [Diagnostics.Stopwatch]::StartNew()
            $exe = $cmd[0]
            $rest = @()
            if ($cmd.Count -gt 1) { $rest = $cmd[1..($cmd.Count - 1)] }
            $ErrorActionPreference = 'Continue'
            & $exe @rest *> $log
            $code = $LASTEXITCODE
            $ErrorActionPreference = 'Stop'
            $sw.Stop()
            Clear-Knobs
            if ($code -ne 0) { $failed++ }
            # The gate's own verdict line; the last non-empty line when it printed none.
            $last = (Get-Content $log | Where-Object { $_ -match 'VERDICT|GATE:|PASSED|verdict|FAIL' } | Select-Object -Last 1)
            if ($null -eq $last) { $last = (Get-Content $log | Where-Object { $_ -match '\S' } | Select-Object -Last 1) }
            if ($null -eq $last) { $last = '' }
            $rows += [pscustomobject]@{
                Level = $level; Gate = $gate; Exit = $code; Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1)
                Last = $last.Trim().Substring(0, [math]::Min(90, $last.Trim().Length))
            }
            Write-Host ("{0,-4} {1,-12} exit {2}  ({3} s, under whatever load the machine had)  {4}" -f $level, $gate, $code, [math]::Round($sw.Elapsed.TotalSeconds, 1), $log)
        }
    }
}
finally {
    Clear-Knobs
    Pop-Location
}

Write-Host ''
$rows | Format-Table -AutoSize Level, Gate, Exit, Last | Out-String -Width 200 | Write-Host
if ($failed -eq 0) { Write-Host "PASS  every gate at every level run"; exit 0 }
Write-Host "FAIL  $failed gate run(s) failed - see $OutDir"
exit 1
