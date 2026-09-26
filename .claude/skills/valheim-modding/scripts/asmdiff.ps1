<#
.SYNOPSIS
  Compares one assembly between two builds: its types, every member's surface and access, and every method's IL.

.DESCRIPTION
  Written 2026-09-26 to compare the client's and the dedicated server's assembly_valheim.dll (multiplayer.md
  section 1.4 is its result). It works for any two folders that hold the same assembly, e.g. a kept copy of an
  older build's Managed folder against the installed one after a game update.

  Output (UTF-8, no BOM), in this order: types only in A, types only in B; member differences for types in both
  (<A>-ONLY, <B>-ONLY, ACCESS-DIFF); then every method present in both whose IL differs. Branch targets are
  compared by instruction index, so a method whose code merely moved does not count.
  -Constants adds the const fields whose values differ.
  -ShowIL "Type::Method/argCount,..." adds the IL lines that differ in the named methods ("<=" only in A,
  "=>" only in B; the first 30 per method). Nested types are written Outer/Inner.

  Defaults: A = this game's valheim_Data\Managed, B = the dedicated server installed beside it
  (..\Valheim dedicated server\valheim_server_Data\Managed). References resolve from each assembly's own folder.
  Reads with Mono.Cecil from BepInEx\core; writes only -Out. Merged 2026-09-26 from three scripts written
  for that investigation.

.EXAMPLE
  .\asmdiff.ps1 -Out .\diff_valheim.txt
  .\asmdiff.ps1 -Name assembly_utils -Out .\diff_utils.txt
  .\asmdiff.ps1 -Out .\d.txt -Constants -ShowIL "Game::FixedUpdate/0,Terminal::AddString/1"
  .\asmdiff.ps1 -A "<old Managed copy>" -LabelA OLD -LabelB NEW -B "<game>\valheim_Data\Managed" -Out .\update.txt
#>
param(
    [string]$Name = "assembly_valheim",
    [Parameter(Mandatory = $true)] [string]$Out,
    [string]$A = "",
    [string]$B = "",
    [string]$LabelA = "CLIENT",
    [string]$LabelB = "SERVER",
    [switch]$Constants,
    [string]$ShowIL = "",
    [string]$ValheimDir = ""
)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "_common.ps1")

if ($A -eq "") { $A = $GameManaged }
if ($B -eq "") { $B = Join-Path (Split-Path $ValheimDir -Parent) "Valheim dedicated server\valheim_server_Data\Managed" }
if ($Name.EndsWith(".dll")) { $Name = $Name.Substring(0, $Name.Length - 4) }
foreach ($dir in @($A, $B)) {
    $p = Join-Path $dir ($Name + ".dll")
    if (-not (Test-Path $p -PathType Leaf)) { throw "Not found: $p (pass -A / -B folders that hold $Name.dll)" }
}

function Open-Module($dir, $n) {
    $r = New-Object Mono.Cecil.DefaultAssemblyResolver
    $r.AddSearchDirectory($dir)
    $rp = New-Object Mono.Cecil.ReaderParameters
    $rp.AssemblyResolver = $r
    return [Mono.Cecil.ModuleDefinition]::ReadModule((Join-Path $dir ($n + ".dll")), $rp)
}
function Vis($m) {
    if ($m.IsPublic) { "public" } elseif ($m.IsFamily) { "protected" } elseif ($m.IsAssembly) { "internal" }
    elseif ($m.IsFamilyOrAssembly) { "protinternal" } else { "private" }
}
function Sig($t) {
    $h = @{}
    foreach ($f in $t.Fields) { $h["F " + $f.FieldType.FullName + " " + $f.Name] = (Vis $f) + $(if ($f.IsStatic) { " static" } else { "" }) }
    foreach ($m in $t.Methods) {
        $ps = ($m.Parameters | ForEach-Object { $_.ParameterType.FullName }) -join ","
        $gp = if ($m.HasGenericParameters) { "``" + $m.GenericParameters.Count } else { "" }
        $h["M " + $m.ReturnType.FullName + " " + $m.Name + $gp + "(" + $ps + ")"] = (Vis $m) +
            $(if ($m.IsStatic) { " static" } else { "" }) + $(if ($m.IsVirtual) { " virtual" } else { "" })
    }
    foreach ($p in $t.Properties) { $h["P " + $p.PropertyType.FullName + " " + $p.Name] = "" }
    foreach ($e in $t.Events) { $h["E " + $e.EventType.FullName + " " + $e.Name] = "" }
    return $h
}
function BodyKey($m) {
    if (-not $m.HasBody) { return "" }
    $sb = New-Object System.Text.StringBuilder
    foreach ($i in $m.Body.Instructions) {
        $o = $i.Operand
        $os = if ($o -is [Mono.Cecil.Cil.Instruction]) { "->" + $m.Body.Instructions.IndexOf($o) }
              elseif ($o -is [Mono.Cecil.Cil.Instruction[]]) { "sw" + $o.Count }
              elseif ($o -is [Mono.Cecil.Cil.VariableDefinition]) { "v" + $o.Index }
              elseif ($o -is [Mono.Cecil.ParameterDefinition]) { "p" + $o.Index }
              else { "$o" }
        [void]$sb.Append($i.OpCode.Name).Append(" ").Append($os).Append(";")
    }
    return $sb.ToString()
}
function IL-Lines($module, $typeName, $methodName, $argc) {
    $t = $module.GetType($typeName)
    if ($null -eq $t) { return ,@("(type $typeName not found)") }
    $me = $t.Methods | Where-Object { $_.Name -eq $methodName -and $_.Parameters.Count -eq $argc } | Select-Object -First 1
    if ($null -eq $me) { return ,@("(method $methodName/$argc not found)") }
    if (-not $me.HasBody) { return ,@("(no body)") }
    return @($me.Body.Instructions | ForEach-Object {
        $o = $_.Operand
        if ($o -is [Mono.Cecil.Cil.Instruction]) { $o = "->" } elseif ($o -is [Mono.Cecil.Cil.VariableDefinition]) { $o = "V" }
        "$($_.OpCode.Name) $o"
    })
}

$ma = Open-Module $A $Name
$mb = Open-Module $B $Name
$ta = @{}; foreach ($t in $ma.GetTypes()) { $ta[$t.FullName] = $t }
$tb = @{}; foreach ($t in $mb.GetTypes()) { $tb[$t.FullName] = $t }

$o = New-Object System.Collections.Generic.List[string]
$o.Add("== $Name : types $LabelA=$($ta.Count) $LabelB=$($tb.Count)")
$o.Add("   $LabelA = $A")
$o.Add("   $LabelB = $B")
$o.Add("-- types only in ${LabelA}:"); foreach ($k in ($ta.Keys | Where-Object { -not $tb.ContainsKey($_) } | Sort-Object)) { $o.Add("   " + $k) }
$o.Add("-- types only in ${LabelB}:"); foreach ($k in ($tb.Keys | Where-Object { -not $ta.ContainsKey($_) } | Sort-Object)) { $o.Add("   " + $k) }
$o.Add("-- member differences (types in both):")
$bodyDiff = New-Object System.Collections.Generic.List[string]
foreach ($k in ($ta.Keys | Where-Object { $tb.ContainsKey($_) } | Sort-Object)) {
    $sa = Sig $ta[$k]; $sbb = Sig $tb[$k]
    foreach ($m in ($sa.Keys | Sort-Object)) {
        if (-not $sbb.ContainsKey($m)) { $o.Add("   $LabelA-ONLY  " + $k + " :: " + $m + "  [" + $sa[$m] + "]") }
        elseif ($sa[$m] -ne $sbb[$m]) { $o.Add("   ACCESS-DIFF  " + $k + " :: " + $m + "  $LabelA[" + $sa[$m] + "] $LabelB[" + $sbb[$m] + "]") }
    }
    foreach ($m in ($sbb.Keys | Sort-Object)) { if (-not $sa.ContainsKey($m)) { $o.Add("   $LabelB-ONLY  " + $k + " :: " + $m + "  [" + $sbb[$m] + "]") } }
    $mbs = @{}; foreach ($m in $tb[$k].Methods) { $mbs[$m.FullName] = $m }
    foreach ($m in $ta[$k].Methods) {
        if (-not $mbs.ContainsKey($m.FullName)) { continue }
        if ((BodyKey $m) -ne (BodyKey $mbs[$m.FullName])) {
            $na = if ($m.HasBody) { $m.Body.Instructions.Count } else { 0 }
            $nb = if ($mbs[$m.FullName].HasBody) { $mbs[$m.FullName].Body.Instructions.Count } else { 0 }
            $bodyDiff.Add("   BODY-DIFF  " + $m.FullName + "   ($LabelA $na ins, $LabelB $nb ins)")
        }
    }
}
$o.Add("-- methods whose IL differs: " + $bodyDiff.Count)
$o.AddRange($bodyDiff)

if ($Constants) {
    $cb = @{}
    foreach ($t in $mb.GetTypes()) { foreach ($f in $t.Fields) { if ($f.HasConstant) { $cb[$t.FullName + "::" + $f.Name] = "$($f.Constant)" } } }
    $n = 0; $diffs = New-Object System.Collections.Generic.List[string]
    foreach ($t in $ma.GetTypes()) {
        foreach ($f in $t.Fields) {
            if (-not $f.HasConstant) { continue }
            $key = $t.FullName + "::" + $f.Name; $n++
            if ($cb.ContainsKey($key) -and $cb[$key] -ne "$($f.Constant)") { $diffs.Add("   CONST-DIFF  $key  $LabelA=$($f.Constant) $LabelB=$($cb[$key])") }
        }
    }
    $o.Add("-- const fields compared: $n, differing: $($diffs.Count)")
    $o.AddRange($diffs)
}

if ($ShowIL -ne "") {
    foreach ($spec in ($ShowIL -split ",")) {
        $spec = $spec.Trim()
        if ($spec -notmatch '^(.+)::(.+)/(\d+)$') { throw "Bad -ShowIL entry '$spec' (want Type::Method/argCount)" }
        $o.Add("-- IL of $spec  (<= only in $LabelA, => only in $LabelB)")
        $la = IL-Lines $ma $Matches[1] $Matches[2] ([int]$Matches[3])
        $lb = IL-Lines $mb $Matches[1] $Matches[2] ([int]$Matches[3])
        if ($la.Count -eq 1 -and $la[0].StartsWith("(")) { $o.Add("   ${LabelA}: " + $la[0]) }
        if ($lb.Count -eq 1 -and $lb[0].StartsWith("(")) { $o.Add("   ${LabelB}: " + $lb[0]) }
        $cmp = Compare-Object $la $lb | Select-Object -First 30
        if ($null -eq $cmp) { $o.Add("   (no instruction differs)") }
        else { foreach ($c in $cmp) { $o.Add(("   {0} {1}" -f $c.SideIndicator, $c.InputObject)) } }
    }
}

[IO.File]::WriteAllLines($Out, $o, (New-Object System.Text.UTF8Encoding($false)))
"wrote $Out ($($o.Count) lines; $($bodyDiff.Count) methods with different IL)"
