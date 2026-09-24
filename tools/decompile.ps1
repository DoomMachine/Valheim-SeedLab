<#
.SYNOPSIS
  Decompiles one type of the installed Valheim to C#, with the ILSpy decompiler engine.

.DESCRIPTION
  It finds the game the same way tools\check-game-version.ps1 does, decompiles the named type with
  C# 10 settings, prints the C# and caches it under
      %LOCALAPPDATA%\SeedLab\decompile\<assembly>-<first 8 hex of its SHA-256>-<language version>-<backend>\
  so asking again for the same type of the same game build is instant, and a game update can never
  be answered from a stale cache. It reads the game's files and writes only inside
  %LOCALAPPDATA%\SeedLab\decompile.

  Two ways to decompile, tried in this order:
    1. An installed ILSpy desktop app (https://github.com/icsharpcode/ILSpy). ILSpy ships
       ICSharpCode.Decompiler.dll, its decompiler engine; the first run builds the small program in
       tools\decompiler against it (needs the .NET SDK) and later runs reuse it. ILSpy is looked for at
       %LOCALAPPDATA%\Programs\ILSpy, or pass -ILSpyDir. This is the engine the docs' citations came
       from.
    2. ilspycmd, the command-line ILSpy (a .NET tool), found on PATH, at %USERPROFILE%\.dotnet\tools,
       or passed with -ILSpyCmd. Install it once with:  dotnet tool install --global ilspycmd
  -Backend ilspy or -Backend ilspycmd forces one of them.

  -Type takes the type's full name. Most of Valheim's own types are in the global namespace, so the
  plain name works: WorldGenerator, ZoneSystem, BiomeHelpers. -List <text> prints the type names that
  contain <text>, to find the exact spelling of anything else.

  The docs cite decompiled code as decomp\<Type>.cs followed by a line number. Those numbers come from
  a C# 10 decompile made with ILSpy's decompiler engine; a different ILSpy version can lay the same
  code out differently, so search for the quoted code rather than trusting the line number alone.

  Exit codes: 0 done; 1 no decompiler was found, or it failed (for example, no such type); 3 the game
  or the assembly was not found.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\decompile.ps1 -Type WorldGenerator

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\decompile.ps1 -Type BiomeHelpers -PathOnly

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\decompile.ps1 -List Zone

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\decompile.ps1 -Type ZInput -Assembly assembly_utils
#>
param(
    [string]$Type = "",
    [string]$List = "",
    # A name, looked up in valheim_Data\Managed and then BepInEx\core, or a path to any .dll.
    [string]$Assembly = "assembly_valheim",
    [string]$ValheimDir = "",
    [string]$LanguageVersion = "CSharp10_0",
    # "auto" (default): an installed ILSpy if found, else ilspycmd. "ilspy" or "ilspycmd" forces one.
    [ValidateSet("auto", "ilspy", "ilspycmd")] [string]$Backend = "auto",
    [string]$ILSpyDir = "",
    [string]$ILSpyCmd = "",
    # Print only the path of the cached .cs file instead of its contents.
    [switch]$PathOnly,
    # Decompile again even when the cache already holds the type.
    [switch]$Force
)
$ErrorActionPreference = "Stop"

if (($Type -eq "") -eq ($List -eq "")) {
    Write-Output "Pass exactly one of -Type <full type name> or -List <text>."
    exit 1
}

$AssemblyRelative = "valheim_Data\Managed\assembly_valheim.dll"

function Test-GameDir([string]$dir) {
    if (-not $dir) { return $false }
    try { return [IO.File]::Exists((Join-Path $dir $AssemblyRelative)) } catch { return $false }
}

function Get-SteamCandidates {
    $roots = New-Object System.Collections.Generic.List[string]
    $roots.Add("C:\Program Files (x86)\Steam")
    $roots.Add("C:\Program Files\Steam")
    if ($HOME) {
        $roots.Add((Join-Path $HOME ".steam\steam"))
        $roots.Add((Join-Path $HOME ".local\share\Steam"))
        $roots.Add((Join-Path $HOME "Library\Application Support\Steam"))
    }
    foreach ($code in 68..90) {
        $drive = [string][char]$code
        $roots.Add($drive + ":\SteamLibrary")
        $roots.Add($drive + ":\Steam")
    }
    return $roots
}

# The game folder: -ValheimDir, then SEEDLAB_VALHEIM_DIR (both authoritative), then walking up from
# the current directory and from this script, then the usual Steam library folders. Returns $null
# and sets $script:WhyNot when nothing is found.
function Find-Game {
    if ($ValheimDir -ne "") {
        if (Test-GameDir $ValheimDir) { return $ValheimDir }
        $script:WhyNot = "-ValheimDir '$ValheimDir' has no $AssemblyRelative."
        return $null
    }
    if ($env:SEEDLAB_VALHEIM_DIR) {
        if (Test-GameDir $env:SEEDLAB_VALHEIM_DIR) { return $env:SEEDLAB_VALHEIM_DIR }
        $script:WhyNot = "SEEDLAB_VALHEIM_DIR is set to '$($env:SEEDLAB_VALHEIM_DIR)', which has no $AssemblyRelative."
        return $null
    }
    $starts = New-Object System.Collections.Generic.List[string]
    try { $starts.Add((Get-Location).ProviderPath) } catch { }
    if ($PSScriptRoot) { $starts.Add($PSScriptRoot) }
    foreach ($start in $starts) {
        $d = $start
        for ($i = 0; $i -lt 12 -and $d; $i++) {
            if (Test-GameDir $d) { return $d }
            $d = Split-Path $d -Parent
        }
    }
    foreach ($root in (Get-SteamCandidates)) {
        $cand = Join-Path $root "steamapps\common\Valheim"
        if (Test-GameDir $cand) { return $cand }
    }
    $script:WhyNot = "no $AssemblyRelative was found above the current directory or this script, or in " +
                     "the usual Steam library folders. Pass -ValheimDir or set SEEDLAB_VALHEIM_DIR."
    return $null
}

function Find-ILSpyCmd {
    if ($ILSpyCmd -ne "") {
        if ([IO.File]::Exists($ILSpyCmd)) { return $ILSpyCmd }
        return $null
    }
    $cmd = Get-Command ilspycmd -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmd) { return $cmd.Source }
    if ($env:USERPROFILE) {
        $tool = Join-Path $env:USERPROFILE ".dotnet\tools\ilspycmd.exe"
        if ([IO.File]::Exists($tool)) { return $tool }
    }
    if ($HOME) {
        $tool = Join-Path $HOME ".dotnet/tools/ilspycmd"
        if ([IO.File]::Exists($tool)) { return $tool }
    }
    return $null
}

function Quote-Arg([string]$a) {
    if ($a -notmatch '[\s"]') { return $a }
    return '"' + $a.TrimEnd('\').Replace('"', '\"') + '"'
}

# Runs ilspycmd with stdout and stderr captured separately. Windows PowerShell 5.1 turns a native
# program's stderr lines into errors, which "Stop" would make fatal, so the process is started directly.
function Invoke-ILSpy([string]$exe, [string[]]$arguments) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = (($arguments | ForEach-Object { Quote-Arg $_ }) -join " ")
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $errTask = $p.StandardError.ReadToEndAsync()
    $out = $p.StandardOutput.ReadToEnd()
    $p.WaitForExit()
    return New-Object PSObject -Property @{ ExitCode = $p.ExitCode; Out = $out; Err = $errTask.Result }
}

# --- the assembly ---------------------------------------------------------------------------------
$script:WhyNot = ""
$gameDir = $null
$asmPath = $null
if ([IO.File]::Exists($Assembly)) {
    $asmPath = (Resolve-Path -LiteralPath $Assembly).ProviderPath
    $gameDir = Find-Game   # optional here: only used to resolve the assembly's references
} else {
    $gameDir = Find-Game
    if (-not $gameDir) {
        Write-Output "Valheim not found: $script:WhyNot"
        exit 3
    }
    $name = $Assembly
    if (-not $name.EndsWith(".dll", [StringComparison]::OrdinalIgnoreCase)) { $name = $name + ".dll" }
    foreach ($dir in @((Join-Path $gameDir "valheim_Data\Managed"), (Join-Path $gameDir "BepInEx\core"))) {
        $cand = Join-Path $dir $name
        if ([IO.File]::Exists($cand)) { $asmPath = $cand; break }
    }
    if (-not $asmPath) {
        Write-Output "Assembly not found: $name (looked in valheim_Data\Managed and BepInEx\core under $gameDir)."
        exit 3
    }
}

# Finds ilspycmd (or exits 1 with the install hint) and fills $script:Common with the options every
# call shares. Only called when ilspycmd is actually needed, so a cached type needs no ilspycmd at all.
function Initialize-ILSpy {
    $exe = Find-ILSpyCmd
    if (-not $exe) {
        # Write-Host, not Write-Output: this function's output is its return value.
        if ($ILSpyCmd -ne "") { Write-Host "ilspycmd not found at $ILSpyCmd." }
        else { Write-Host "ilspycmd not found (looked on PATH and in %USERPROFILE%\.dotnet\tools)." }
        Write-Host "Install it once with:  dotnet tool install --global ilspycmd"
        Write-Host "(it needs the .NET SDK; see https://github.com/icsharpcode/ILSpy)"
        exit 1
    }
    $script:Common = New-Object System.Collections.Generic.List[string]
    if ($gameDir) {
        foreach ($dir in @((Join-Path $gameDir "valheim_Data\Managed"), (Join-Path $gameDir "BepInEx\core"))) {
            if ([IO.Directory]::Exists($dir)) { $script:Common.Add("-r"); $script:Common.Add($dir) }
        }
    }
    # A newer ilspycmd checks online for updates on every run and prints what it finds on stdout;
    # turn that off where the option exists, and leave older versions, which lack it, alone.
    $help = Invoke-ILSpy $exe @("--help")
    if (($help.Out + $help.Err) -match '--disable-updatecheck') { $script:Common.Add("--disable-updatecheck") }
    return $exe
}

# --- the ILSpy engine backend ---------------------------------------------------------------------
$localData = $env:LOCALAPPDATA
if (-not $localData) { $localData = [Environment]::GetFolderPath("LocalApplicationData") }
if ($ILSpyDir -eq "") { $ILSpyDir = Join-Path $localData "Programs\ILSpy" }

function Test-ILSpyEngine { return [IO.File]::Exists((Join-Path $ILSpyDir "ICSharpCode.Decompiler.dll")) }

# Builds tools\decompiler against the installed ILSpy the first time, and again only when its source
# changed. Returns the path of decomp.dll, or exits 1 when it cannot be built.
function Initialize-Engine {
    $src = Join-Path $PSScriptRoot "decompiler"
    $toolDir = Join-Path $localData "SeedLab\decompile\tool"
    $dll = Join-Path $toolDir "out\decomp.dll"
    $stamp = Join-Path $toolDir "source.hash"
    $hash = (Get-FileHash -LiteralPath (Join-Path $src "Program.cs")).Hash +
            (Get-FileHash -LiteralPath (Join-Path $src "decomp.csproj")).Hash +
            (Get-FileHash -LiteralPath (Join-Path $ILSpyDir "ICSharpCode.Decompiler.dll")).Hash
    $built = [IO.File]::Exists($dll) -and [IO.File]::Exists($stamp) -and
             ([IO.File]::ReadAllText($stamp).Trim() -eq $hash)
    if (-not $built) {
        [IO.Directory]::CreateDirectory($toolDir) | Out-Null
        Copy-Item -LiteralPath (Join-Path $src "Program.cs") -Destination $toolDir -Force
        Copy-Item -LiteralPath (Join-Path $src "decomp.csproj") -Destination $toolDir -Force
        Write-Host "Building the decompiler into $toolDir (first run only) ..." -ForegroundColor DarkGray
        $r = Invoke-ILSpy "dotnet" @("build", (Join-Path $toolDir "decomp.csproj"), "-c", "Release",
                                     "-o", (Join-Path $toolDir "out"), "-v", "quiet", "-nologo",
                                     ("-p:ILSpyDir=" + $ILSpyDir))
        if ($r.ExitCode -ne 0 -or -not [IO.File]::Exists($dll)) {
            Write-Host ("Building the decompiler failed (exit {0}); it needs the .NET SDK." -f $r.ExitCode)
            Write-Host ($r.Out + $r.Err).TrimEnd()
            exit 1
        }
        [IO.File]::WriteAllText($stamp, $hash, (New-Object Text.UTF8Encoding($false)))
    }
    return $dll
}

# Which backend runs. Only resolved when a decompile is actually needed, so a cached type needs neither.
function Resolve-Backend {
    if ($Backend -eq "ilspy" -or ($Backend -eq "auto" -and (Test-ILSpyEngine))) {
        if (-not (Test-ILSpyEngine)) {
            Write-Host "ILSpy not found at $ILSpyDir (need ICSharpCode.Decompiler.dll). Install ILSpy or pass -ILSpyDir."
            exit 1
        }
        return "ilspy"
    }
    if ($Backend -eq "auto" -and -not (Find-ILSpyCmd)) {
        Write-Host "No decompiler found. Either install the ILSpy desktop app (https://github.com/icsharpcode/ILSpy)"
        Write-Host "- looked for it at $ILSpyDir, or pass -ILSpyDir - or install ilspycmd once with:"
        Write-Host "    dotnet tool install --global ilspycmd"
        exit 1
    }
    return "ilspycmd"
}

# --- -List ----------------------------------------------------------------------------------------
if ($List -ne "" -and (Resolve-Backend) -eq "ilspy") {
    $dll = Initialize-Engine
    $r = Invoke-ILSpy "dotnet" @($dll, "--list", $asmPath, $List)
    if ($r.ExitCode -ne 0) {
        Write-Output ("the decompiler failed (exit {0}):" -f $r.ExitCode)
        Write-Output $r.Err.TrimEnd()
        exit 1
    }
    Write-Output $r.Out.TrimEnd()
    exit 0
}

if ($List -ne "") {
    $exe = Initialize-ILSpy
    $a = @("-l", "c", "-l", "i", "-l", "s", "-l", "d", "-l", "e") + $script:Common.ToArray() + @($asmPath)
    $r = Invoke-ILSpy $exe $a
    if ($r.ExitCode -ne 0) {
        Write-Output ("ilspycmd failed (exit {0}):" -f $r.ExitCode)
        Write-Output $r.Err.TrimEnd()
        exit 1
    }
    $needle = $List
    $r.Out -split "`r?`n" | Where-Object { $_ -ne "" -and $_.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0 }
    exit 0
}

# --- -Type ----------------------------------------------------------------------------------------
$sha8 = (Get-FileHash -LiteralPath $asmPath -Algorithm SHA256).Hash.Substring(0, 8).ToLowerInvariant()
# The two backends can lay the same code out differently, so each has its own cache.
$chosen = Resolve-Backend
$cacheDir = Join-Path $localData ("SeedLab\decompile\{0}-{1}-{2}-{3}" -f [IO.Path]::GetFileNameWithoutExtension($asmPath), $sha8, $LanguageVersion, $chosen)
$fileName = $Type
foreach ($c in [IO.Path]::GetInvalidFileNameChars()) { $fileName = $fileName.Replace([string]$c, "_") }
$cached = Join-Path $cacheDir ($fileName + ".cs")

if (($Force -or -not [IO.File]::Exists($cached)) -and $chosen -eq "ilspy") {
    $dll = Initialize-Engine
    [IO.Directory]::CreateDirectory($cacheDir) | Out-Null
    $r = Invoke-ILSpy "dotnet" @($dll, $asmPath, $Type, $LanguageVersion)
    if ($r.ExitCode -ne 0 -or $r.Out.Trim() -eq "") {
        Write-Output ("the decompiler could not decompile '{0}' from {1} (exit {2})." -f $Type, $asmPath, $r.ExitCode)
        if ($r.Err.Trim() -ne "") { Write-Output $r.Err.TrimEnd() }
        Write-Output "-Type needs the type's name; find it with:  tools\decompile.ps1 -List <part of the name>"
        exit 1
    }
    [IO.File]::WriteAllText($cached, $r.Out, (New-Object Text.UTF8Encoding($false)))
}

if ($Force -or -not [IO.File]::Exists($cached)) {
    $exe = Initialize-ILSpy
    [IO.Directory]::CreateDirectory($cacheDir) | Out-Null
    # ilspycmd writes the type to a file in the -o folder; a fresh folder per call means the one .cs
    # file in it is this type, whatever name this ilspycmd version gives it.
    $work = Join-Path $cacheDir ("work-" + [guid]::NewGuid().ToString("N"))
    [IO.Directory]::CreateDirectory($work) | Out-Null
    try {
        $a = @("-t", $Type, "-lv", $LanguageVersion) + $script:Common.ToArray() + @("-o", $work, $asmPath)
        $r = Invoke-ILSpy $exe $a
        $files = @(Get-ChildItem -LiteralPath $work -Filter *.cs -Recurse -File)
        if ($r.ExitCode -ne 0 -or $files.Count -ne 1 -or $files[0].Length -eq 0) {
            Write-Output ("ilspycmd could not decompile '{0}' from {1} (exit {2})." -f $Type, $asmPath, $r.ExitCode)
            if ($r.Err.Trim() -ne "") { Write-Output $r.Err.TrimEnd() }
            Write-Output "-Type needs the full type name; find it with:  tools\decompile.ps1 -List <part of the name>"
            exit 1
        }
        $text = [IO.File]::ReadAllText($files[0].FullName)
        [IO.File]::WriteAllText($cached, $text, (New-Object Text.UTF8Encoding($false)))
    } finally {
        if ([IO.Directory]::Exists($work)) { [IO.Directory]::Delete($work, $true) }
    }
}

if ($PathOnly) {
    Write-Output $cached
} else {
    Write-Output ([IO.File]::ReadAllText($cached))
    Write-Host ("(cached at {0})" -f $cached) -ForegroundColor DarkGray
}
exit 0
