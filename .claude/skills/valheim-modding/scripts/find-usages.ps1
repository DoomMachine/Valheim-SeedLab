<#
.SYNOPSIS
  Finds every method whose IL references something: a method, field, type or string literal.

.DESCRIPTION
  The fastest way to answer "who calls X", "who reads field Y", "where is this key/string used".
  -Needle is a case-insensitive substring matched against each instruction's operand; separate several
  with commas. Operands look like "System.Void Minimap::RemovePin(Minimap/PinData)" or a plain string.

.EXAMPLE
  .\find-usages.ps1 -Needle "TextInput::IsVisible"
  .\find-usages.ps1 -Needle "PinData::m_save"
  .\find-usages.ps1 -Needle "Minimap::OnMapLeftClick" -Plugins      # also scan installed mods
  .\find-usages.ps1 -Needle "hud_mapday"                             # a string literal
#>
param(
    [Parameter(Mandatory = $true)] [string]$Needle,
    [string]$Assembly = "",        # name or path; default = all four game-code assemblies
    [switch]$Plugins,              # also scan every DLL under BepInEx\plugins
    [string]$ValheimDir = ""
)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "_common.ps1")

$paths = @()
if ($Assembly -ne "") { $paths += (Get-GameAssemblyPath $Assembly) } else { $paths = @(Get-GameCodeAssemblies) }
if ($Plugins) {
    $paths += (Get-ChildItem (Join-Path $ValheimDir "BepInEx\plugins") -Recurse -Filter *.dll | ForEach-Object { $_.FullName })
}
$needles = $Needle -split "," | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne "" }
$count = 0

foreach ($path in $paths) {
    try { $md = Read-Module $path } catch { continue }
    $label = [IO.Path]::GetFileName($path)
    foreach ($t in $md.GetTypes()) {
        foreach ($m in $t.Methods) {
            if (-not $m.HasBody) { continue }
            foreach ($i in $m.Body.Instructions) {
                if ($null -eq $i.Operand) { continue }
                $s = $i.Operand.ToString()
                foreach ($n in $needles) {
                    if ($s.IndexOf($n, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                        Write-Output ("[{0}] {1}::{2}  {3}  {4}" -f $label, $t.FullName, $m.Name, $i.OpCode.Name, $s)
                        $count++
                    }
                }
            }
        }
    }
}
Write-Output ("-- {0} reference(s)" -f $count)
