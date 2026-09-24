<#
.SYNOPSIS
  Lists a game type's fields, properties and methods WITH their accessibility.

.DESCRIPTION
  Accessibility is the thing training data gets wrong and the compiler punishes: a PRIVATE member can
  only be reached through HarmonyLib.AccessTools reflection (or patched by name), never called directly.
  Nested types use a slash: Minimap/PinData. Wildcards work: 'Minimap/*'. Several types: 'A,B'.

.EXAMPLE
  .\api-surface.ps1 -Type Minimap -Filter Pin
  .\api-surface.ps1 -Type 'Minimap/PinData,Minimap/PinType'
  .\api-surface.ps1 -Type ZInput -Assembly assembly_utils
#>
param(
    [Parameter(Mandatory = $true)] [string]$Type,
    [string]$Assembly = "",        # name or path; default = all four game-code assemblies
    [string]$Filter = "",          # regex applied to member lines
    [string]$ValheimDir = ""
)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "_common.ps1")

$paths = @()
if ($Assembly -ne "") { $paths += (Get-GameAssemblyPath $Assembly) } else { $paths = Get-GameCodeAssemblies }
$wanted = $Type -split ","
$found = $false

foreach ($path in $paths) {
    $md = Read-Module $path
    foreach ($t in $md.GetTypes()) {
        $hit = $false
        foreach ($w in $wanted) { if ($t.FullName -like $w.Trim()) { $hit = $true } }
        if (-not $hit) { continue }
        $found = $true

        $tv = if ($t.IsPublic -or $t.IsNestedPublic) { "public" } else { "NONPUBLIC" }
        $base = if ($t.BaseType) { $t.BaseType.FullName } else { "-" }
        Write-Output ""
        Write-Output ("=== {0} {1} : {2}   [{3}]" -f $tv, $t.FullName, $base, [IO.Path]::GetFileName($path))

        $lines = @()
        foreach ($f in $t.Fields) {
            $s = if ($f.IsStatic) { "static " } else { "" }
            $c = if ($f.HasConstant) { " = " + $f.Constant } else { "" }
            $lines += ("  F {0,-9} {1}{2} {3}{4}" -f (Get-Visibility $f), $s, $f.FieldType.Name, $f.Name, $c)
        }
        foreach ($p in $t.Properties) {
            $g = $p.GetMethod; $st = $p.SetMethod
            $acc = @()
            if ($g) { $acc += ("get:" + (Get-Visibility $g)) }
            if ($st) { $acc += ("set:" + (Get-Visibility $st)) }
            $lines += ("  P {0,-9} {1} {2}  ({3})" -f "", $p.PropertyType.Name, $p.Name, ($acc -join " "))
        }
        foreach ($m in $t.Methods) {
            $s = if ($m.IsStatic) { "static " } else { "" }
            $ps = @(); foreach ($pp in $m.Parameters) { $ps += ($pp.ParameterType.Name + " " + $pp.Name) }
            $lines += ("  M {0,-9} {1}{2} {3}({4})" -f (Get-Visibility $m), $s, $m.ReturnType.Name, $m.Name, ($ps -join ", "))
        }
        if ($Filter -ne "") { $lines = $lines | Where-Object { $_ -match $Filter } }
        $lines | ForEach-Object { Write-Output $_ }
    }
}

if (-not $found) { Write-Output "No type matched '$Type'. Nested types need a slash (Outer/Inner); try a wildcard ('*Pin*')." }
