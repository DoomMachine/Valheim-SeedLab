<#
.SYNOPSIS
  Lists what every installed mod patches - to spot conflicts before choosing a patch target.

.DESCRIPTION
  Reads each DLL under BepInEx\plugins and reports:
    - [HarmonyPatch(typeof(X), "Method")] targets on patch classes and methods
    - manual Harmony.Patch(...) calls (target not visible statically - inspect with decompile.ps1)
    - the BepInPlugin identity (GUID, name, version) of each plugin
  Filter with -Target to see only mods touching, say, "Minimap" or "TakeInput".
  Several mods patching the same method is normal; what matters is a PREFIX that returns false
  unconditionally: it skips the original for everyone. It does NOT skip other prefixes - HarmonyX 2.9
  runs every prefix and ANDs their results (HarmonyManipulator.WritePrefixes), so you cannot stop a
  co-patcher's prefix with your own (corrected 2026-09-24).
  Not listed: targets supplied by a TargetMethods() method (e.g. ConfigurationManager's UI_WindowInput
  patches UIInputHandler.OnPointerDown/OnPointerClick that way) - search the DLL's ldstr operands.

.EXAMPLE
  .\scan-mod-patches.ps1
  .\scan-mod-patches.ps1 -Target "TakeInput"
  .\scan-mod-patches.ps1 -Target "Minimap,Terminal"
#>
param(
    [string]$Target = "",
    [string]$ValheimDir = ""
)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "_common.ps1")

$filters = $Target -split "," | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne "" }
function Matches-Filter([string]$s) {
    if ($filters.Count -eq 0) { return $true }
    foreach ($f in $filters) { if ($s -like "*$f*") { return $true } }
    return $false
}
function Arg-Text($value) {
    # Array arguments (e.g. the parameter-type list of an overload) arrive as CustomAttributeArgument[].
    if ($value -is [array]) { return "(" + ((@($value) | ForEach-Object { Arg-Text $_.Value }) -join ", ") + ")" }
    return "$value"
}
function Attr-Args($ca) { return (@($ca.ConstructorArguments) | ForEach-Object { Arg-Text $_.Value }) -join " . " }

$dlls = Get-ChildItem (Join-Path $ValheimDir "BepInEx\plugins") -Recurse -Filter *.dll
foreach ($dll in $dlls) {
    try { $md = Read-Module $dll.FullName } catch { continue }
    $out = @()
    $identity = ""
    foreach ($t in $md.GetTypes()) {
        foreach ($ca in $t.CustomAttributes) {
            $n = $ca.AttributeType.Name
            if ($n -eq "BepInPlugin") { $identity = Attr-Args $ca }
            if ($n -eq "HarmonyPatch") {
                $a = Attr-Args $ca
                if ($a -ne "" -and (Matches-Filter $a)) { $out += ("  patch  {0,-40} <- {1}" -f $a, $t.Name) }
            }
        }
        foreach ($m in $t.Methods) {
            foreach ($ca in $m.CustomAttributes) {
                if ($ca.AttributeType.Name -eq "HarmonyPatch") {
                    $a = Attr-Args $ca
                    if ($a -ne "" -and (Matches-Filter $a)) { $out += ("  patch  {0,-40} <- {1}.{2}" -f $a, $t.Name, $m.Name) }
                }
            }
            if (-not $m.HasBody) { continue }
            foreach ($i in $m.Body.Instructions) {
                if ($i.OpCode.Name -like "call*" -and "$($i.Operand)" -like "*HarmonyLib.Harmony::Patch(*" -and $filters.Count -eq 0) {
                    $out += ("  manual Harmony.Patch call in {0}.{1}" -f $t.Name, $m.Name)
                }
            }
        }
    }
    if ($out.Count -gt 0) {
        $rel = $dll.FullName.Substring((Join-Path $ValheimDir "BepInEx\plugins").Length + 1)
        Write-Output ("{0}   {1}" -f $rel, $(if ($identity -ne "") { "[" + $identity + "]" } else { "" }))
        $out | Sort-Object -Unique | ForEach-Object { Write-Output $_ }
    }
}
