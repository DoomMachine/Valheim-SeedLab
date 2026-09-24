# Hard-kills a search at three points and resumes it, then compares the resumed results file with
# an uninterrupted run's, byte for byte. Run it twice - bare for the bounded sink (which resumes
# from the kept-set snapshot) and with -KeepAll for the streaming one (which resumes from the
# checkpoint's results_length and has its torn last record truncated away).
#
#   dotnet build tests\SeedLab.Search.Safety.Tests -c Release
#   powershell -File tests\SeedLab.Search.Safety.Tests\killtest.ps1
#   powershell -File tests\SeedLab.Search.Safety.Tests\killtest.ps1 -KeepAll
#
# The block size is automatic by default in vseed search (2026-09-24), and a resumed run keeps its
# checkpoint's size whatever thread count it resumes on. -AutoBlock leaves block_size out of the
# query (every leg, the reference too), and -ResumeThreads resumes on a different thread count than
# the killed leg ran on. For the automatic size to differ between the two thread counts the run must
# be short enough to be shrunk - under 1,024 seeds per worker - and still long enough to kill, which
# a 24 m grid over a 5 km disc gives:
#
#   powershell -File tests\SeedLab.Search.Safety.Tests\killtest.ps1 -AutoBlock -ResumeThreads 3 -Seeds 6000 -Grid 24 -Radius 5000
#
# Every "IDENTICAL to the uninterrupted run" line is the proof. A "DIFFERENT" line is a regression
# in the checkpoint, the truncation or the kept-set snapshot, and the run directories are left
# behind so it can be read.
param(
  [int]$Seeds = 400000,
  [int]$Threads = 8,
  [int]$ResumeThreads = 0,
  [double]$CkptEvery = 2,
  [double]$Grid = 96,
  [double]$Radius = 2000,
  [switch]$KeepAll,
  [switch]$AutoBlock,
  [string]$Work = (Join-Path $env:TEMP "seedlab-killtest"),
  [string]$Proof = (Join-Path $PSScriptRoot "bin\Release\net10.0\proof.exe")
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Proof)) {
  throw "proof.exe not found at $Proof - build tests\SeedLab.Search.Safety.Tests -c Release first, or pass -Proof"
}
New-Item -ItemType Directory -Force -Path $Work | Out-Null
$sw = $Work
$proof = $Proof
$mode = if ($KeepAll) { "--keep-all" } else { "" }
$tag  = if ($KeepAll) { "keepall" } else { "bounded" }
if ($AutoBlock) { $tag += "-auto" }
if ($ResumeThreads -le 0) { $ResumeThreads = $Threads }
if ($ResumeThreads -ne $Threads) { $tag += "-t$Threads-$ResumeThreads" }
$inv = [Globalization.CultureInfo]::InvariantCulture

function Run-Leg($dir, $extra, $threads) {
  $a = @("run","--dir",$dir,"--seeds","$Seeds","--threads","$threads",
         "--ckpt-every",$CkptEvery.ToString($inv),"--grid",$Grid.ToString($inv),"--radius",$Radius.ToString($inv))
  if ($mode) { $a += $mode }
  if ($AutoBlock) { $a += "--auto-block" }
  if ($extra) { $a += $extra }
  $p = Start-Process -FilePath $proof -ArgumentList $a -PassThru -NoNewWindow -RedirectStandardOutput "$dir\leg.out" -RedirectStandardError "$dir\leg.err"
  return $p
}

# ---- the reference: one uninterrupted run -----------------------------------------------------
$ref = "$sw\k-$tag-ref"
Remove-Item $ref -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $ref | Out-Null
$t0 = Get-Date
$p = Run-Leg $ref $null $Threads
$p.WaitForExit()
$refSeconds = ((Get-Date) - $t0).TotalSeconds
$refHash = (Get-FileHash "$ref\run.jsonl" -Algorithm SHA256).Hash
$refLen  = (Get-Item "$ref\run.jsonl").Length
$refLines = (Get-Content "$ref\run.jsonl" | Measure-Object -Line).Lines
Write-Output "reference run : $([math]::Round($refSeconds,1)) s, $refLen B, $refLines records, sha $($refHash.Substring(0,16))"
if ((Get-Content "$ref\leg.out" -Raw) -match "threads=\d+\s+block_size=\S+ \(\w+\)") { Write-Output "  $($Matches[0])" }
# The kept-set snapshot alternates between run.ckpt.top and run.ckpt.top2 (2026-09-24); either one
# left behind by a finished run is litter.
Write-Output ("  checkpoint left behind: " + (Test-Path "$ref\run.ckpt") + "  snapshot left behind: " + ((Test-Path "$ref\run.ckpt.top") -or (Test-Path "$ref\run.ckpt.top2")))
Write-Output ""

# ---- killed runs, at several points -----------------------------------------------------------
foreach ($killAt in @(3, 6, 10)) {
  $d = "$sw\k-$tag-$killAt"
  Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue
  New-Item -ItemType Directory -Path $d | Out-Null

  $p = Run-Leg $d $null $Threads
  Start-Sleep -Seconds $killAt
  if ($p.HasExited) { Write-Output "kill at $killAt s: the run had already finished - shorten the kill point"; continue }
  Stop-Process -Id $p.Id -Force
  Start-Sleep -Milliseconds 400

  $afterKillLen = if (Test-Path "$d\run.jsonl") { (Get-Item "$d\run.jsonl").Length } else { 0 }
  $ck = $null
  if (Test-Path "$d\run.ckpt") { $ck = Get-Content "$d\run.ckpt" -Raw | ConvertFrom-Json }
  $ckLen = if ($ck) { $ck.results_length } else { "(no checkpoint)" }
  $ckSeeds = if ($ck) { $ck.seeds_evaluated } else { 0 }
  $ckBlock = if ($ck) { $ck.block_size } else { "n/a" }
  $torn = if ($ck -and $ck.results_length -ge 0) { $afterKillLen - $ck.results_length } else { "n/a" }
  Write-Output "kill at $killAt s : file $afterKillLen B, checkpoint says $ckLen B complete, $ckSeeds seeds done, block size $ckBlock, beyond-checkpoint $torn B"

  # resume, as many legs as it takes - on -ResumeThreads, which may differ from the killed leg's
  $legs = 0
  while ($true) {
    $legs++
    $p2 = Run-Leg $d "--resume" $ResumeThreads
    $p2.WaitForExit()
    $out = Get-Content "$d\leg.out" -Raw
    if ($legs -eq 1 -and $out -match "threads=\d+\s+block_size=\S+ \(\w+\)") { Write-Output "  first resumed leg: $($Matches[0])" }
    if ($out -match "complete=True") { break }
    if ($legs -gt 12) {
      Write-Output "  gave up after $legs legs"
      $err = Get-Content "$d\leg.err" -Raw
      if ($err) { Write-Output ("  last leg's error: " + ($err -split "`n")[0]) }
      break
    }
  }

  $h = (Get-FileHash "$d\run.jsonl" -Algorithm SHA256).Hash
  $l = (Get-Item "$d\run.jsonl").Length
  $n = (Get-Content "$d\run.jsonl" | Measure-Object -Line).Lines
  $same = if ($h -eq $refHash) { "IDENTICAL to the uninterrupted run" } else { "DIFFERENT" }
  Write-Output "  resumed in $legs leg(s): $l B, $n records, sha $($h.Substring(0,16)) -> $same"
  $repaired = (Get-Content "$d\leg.out" -Raw)
  if ($repaired -match "repaired torn tail\s+([\d,]+) B") { Write-Output "  $($Matches[0])" }
  Write-Output ("  checkpoint retired on completion: " + (-not (Test-Path "$d\run.ckpt")) + "  snapshot retired: " + (-not ((Test-Path "$d\run.ckpt.top") -or (Test-Path "$d\run.ckpt.top2"))))
  Write-Output ""
}
