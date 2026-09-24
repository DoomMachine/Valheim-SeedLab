<#
.SYNOPSIS
  Shows who already uses a key before you bind one: vanilla game code, installed mods' code, and every
  BepInEx config file.

.DESCRIPTION
  Checking mod configs alone is not enough - that mistake once shipped F9 as a default, which vanilla
  uses to cycle the gamepad layout. Vanilla binds keys two ways, and this scans for both:
    - UnityEngine.KeyCode constants passed to ZInput.GetKey/GetKeyDown (e.g. F11 -> screenshot)
    - UnityEngine.InputSystem.Key constants passed to ZInput's button table (e.g. F5 -> "Console")
  Enum values are read from the Unity assemblies, not hard-coded.
  A hit only shows that code reads the key - open the method with decompile.ps1 to see what it does,
  and whether a modifier (Ctrl+F3) is required.

.EXAMPLE
  .\find-key-usage.ps1                         # F1..F12
  .\find-key-usage.ps1 -Keys "F4,Insert,Home" -Plugins
#>
param(
    [string]$Keys = "F1,F2,F3,F4,F5,F6,F7,F8,F9,F10,F11,F12",
    [switch]$Plugins,             # also scan installed mods' code (their configs are always scanned)
    [string]$ValheimDir = ""
)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "_common.ps1")

$keyNames = $Keys -split "," | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne "" }

function Get-EnumValues([string]$asmName, [string]$typeName) {
    $md = Read-Module (Get-GameAssemblyPath $asmName)
    $t = $md.GetType($typeName)
    $map = @{}
    if ($t) { foreach ($f in $t.Fields) { if ($f.HasConstant) { $map[$f.Name] = [int]$f.Constant } } }
    return $map
}

# KeyCode lives in CoreModule; the Input System's Key in Unity.InputSystem.
$keyCode = Get-EnumValues "UnityEngine.CoreModule" "UnityEngine.KeyCode"
$isKey = Get-EnumValues "Unity.InputSystem" "UnityEngine.InputSystem.Key"

$targets = @{}   # value -> label, per enum
$kcTargets = @{}; $isTargets = @{}
foreach ($k in $keyNames) {
    if ($keyCode.ContainsKey($k)) { $kcTargets[$keyCode[$k]] = $k }
    if ($isKey.ContainsKey($k)) { $isTargets[$isKey[$k]] = $k }
}

function Scan-Code([string[]]$paths) {
    foreach ($path in $paths) {
        try { $md = Read-Module $path } catch { continue }
        $label = [IO.Path]::GetFileName($path)
        foreach ($t in $md.GetTypes()) { foreach ($m in $t.Methods) {
            if (-not $m.HasBody) { continue }
            $ins = @($m.Body.Instructions)
            for ($i = 0; $i -lt $ins.Count; $i++) {
                if ($ins[$i].OpCode.Name -notlike "ldc.i4*") { continue }
                $v = $ins[$i].Operand
                if ($null -eq $v) {
                    # ldc.i4.0 .. ldc.i4.8 carry the value in the opcode, never a function key
                    continue
                }
                $v = [int]$v
                $kc = $kcTargets.ContainsKey($v); $ik = $isTargets.ContainsKey($v)
                if (-not ($kc -or $ik)) { continue }
                # What consumes the constant? The callee's PARAMETER TYPE says which enum the number
                # belongs to. KeyCode and InputSystem.Key values overlap (KeyCode.Z is Key.F-something),
                # so guessing from the method name reports bindings that do not exist.
                for ($k = $i + 1; $k -lt [Math]::Min($i + 7, $ins.Count); $k++) {
                    if ($ins[$k].OpCode.Name -notlike "call*") { continue }
                    $callee = "$($ins[$k].Operand)"
                    $short = $callee -replace '^.*::', ''
                    if ($short -like "Add(*") { break }             # lookup tables, not bindings
                    if ($kc -and $callee -like "*(UnityEngine.KeyCode*") {
                        Write-Output ("  {0,-5} [{1}] {2}::{3}  -> {4}" -f $kcTargets[$v], $label, $t.FullName, $m.Name, $short)
                    } elseif ($ik -and $callee -like "*(UnityEngine.InputSystem.Key*") {
                        Write-Output ("  {0,-5} [{1}] {2}::{3}  -> {4}  (Input System key)" -f $isTargets[$v], $label, $t.FullName, $m.Name, $short)
                    }
                    break
                }
            }
        } }
    }
}

Write-Output "== vanilla game code =="
Scan-Code (Get-GameCodeAssemblies)
if ($Plugins) {
    Write-Output ""
    Write-Output "== installed mods' code =="
    Scan-Code (Get-ChildItem (Join-Path $ValheimDir "BepInEx\plugins") -Recurse -Filter *.dll | ForEach-Object { $_.FullName })
}

Write-Output ""
Write-Output "== BepInEx config files =="
$cfgDir = Join-Path $ValheimDir "BepInEx\config"
$files = Get-ChildItem $cfgDir -Recurse -Include *.cfg, *.yaml, *.yml -ErrorAction SilentlyContinue
foreach ($f in $files) {
    $n = 0
    foreach ($line in (Get-Content $f.FullName)) {
        $n++
        if ($line -match '^\s*#') { continue }
        foreach ($k in $keyNames) {
            if ($line -match ('(^|[^A-Za-z0-9])' + [regex]::Escape($k) + '($|[^A-Za-z0-9])') -and $line -match '[=:]') {
                Write-Output ("  {0,-5} {1}:{2}  {3}" -f $k, $f.Name, $n, $line.Trim())
            }
        }
    }
}
