# SeedLab for Windows: install, run, stop and remove SeedLab.
#
# SeedLab.bat and the numbered "SeedLab N - ....bat" files in the SeedLab folder run this script;
# docs\scripts.md explains every action in plain words.
#
#   SeedLab.bat                       a numbered menu
#   SeedLab.bat install               check the .NET 10 SDK, build SeedLab, register the vseed command
#   SeedLab.bat web [vseed options]   open the web page, starting SeedLab's web server if needed
#   SeedLab.bat stop                  stop the web server
#   SeedLab.bat status                what is installed and what is running
#   SeedLab.bat shell                 a PowerShell window in which vseed works
#   SeedLab.bat uninstall             remove what SeedLab put outside its folder; keeps the build
#   SeedLab.bat remove-build          uninstall, then delete the build output inside the SeedLab folder
#   SeedLab.bat help
#
#   --no-pause   never wait for Enter at the end. For scripts and tests: a double-clicked window
#                waits, so that it does not vanish before it can be read.
#
# Rules this script keeps:
#   - Every action first does whatever earlier step has not happened yet, and says so.
#   - It refuses to run as administrator (see Assert-NotElevated), whatever the action. The only step
#     that needs administrator rights is installing Microsoft's .NET SDK, and Windows asks for that itself.
#   - It asks before it removes anything or installs anything, and before it stops a running search or
#     any program other than SeedLab's web server. "stop" stops the web server at once when no search
#     is running in it - stopping it is what was asked for - and asks first when one is. When it cannot
#     ask (no keyboard, input redirected) it takes the safe answer, which is always "no".
#   - It only ever stops programs started from THIS SeedLab folder's build.
#   - SeedLab's web server is stopped the way vseed itself provides (vseed serve --stop, since
#     2026-09-24): a running search is then stopped with its checkpoint saved. Ending a process
#     directly is kept for what vseed cannot reach: an older build, a server no cache folder here
#     knows about, a search running in a terminal.
#
# Compatible with Windows PowerShell 5.1. Keep this file ASCII only: PowerShell 5.1 reads a script
# without a byte order mark in the ANSI code page, so any other character would be mangled.
#
# Test hooks (environment variables, never needed in normal use). When any SEEDLAB_SCRIPT_TEST_*
# variable is set the script runs in test mode: it refuses to start unless SEEDLAB_SCRIPT_TEST_ENVKEY
# and SEEDLAB_SCRIPT_TEST_CACHE are both set, looks for Valheim only in SEEDLAB_VALHEIM_DIR, never
# broadcasts an environment change and never offers to stop a program from another folder. It also
# sets SEEDLAB_CACHE_DIR to SEEDLAB_SCRIPT_TEST_CACHE when a test has not set it, so that every vseed
# it starts keeps its cache, its logs and its server file in the scratch folder too.
#   SEEDLAB_SCRIPT_TEST_ENVKEY       registry key used instead of HKCU\Environment (under HKCU:\)
#   SEEDLAB_SCRIPT_TEST_CACHE        folder used instead of %LOCALAPPDATA%\SeedLab
#   SEEDLAB_SCRIPT_TEST_ANSWERS      the answers to the script's questions, in order, separated by |
#   SEEDLAB_SCRIPT_TEST_ELEVATED     1 = behave as if this window ran as administrator
#   SEEDLAB_SCRIPT_TEST_DOTNET_DIRS  folders (separated by ;) searched for dotnet.exe instead of
#                                    the usual places
#   SEEDLAB_SCRIPT_TEST_DUMPER_OUT   folder used instead of %USERPROFILE%\AppData\valheim-dumper
#   SEEDLAB_SCRIPT_TEST_NO_WINDOWS   1 = print addresses instead of opening a browser (vseed serve
#                                    is given --no-browser), and run the command window's commands
#                                    here instead of in a new window

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$ScriptArgs = @($args)
$script:NoPause = $false          # --no-pause was given
$script:SkipFinalPause = $false   # the action already handed over to another window

# -------------------------------------------------------------------------------------------------
# Output and questions
# -------------------------------------------------------------------------------------------------

function Say([string]$text = '') { Write-Host $text }
function Say-Title([string]$text) {
    Write-Host ''
    Write-Host $text -ForegroundColor Cyan
    Write-Host ('-' * $text.Length) -ForegroundColor Cyan
}
function Say-Warn([string]$text) { Write-Host $text -ForegroundColor Yellow }
function Say-Bad([string]$text) { Write-Host $text -ForegroundColor Red }
function Say-Good([string]$text) { Write-Host $text -ForegroundColor Green }

function TestVar([string]$name) {
    $v = [Environment]::GetEnvironmentVariable($name, 'Process')
    if ([string]::IsNullOrEmpty($v)) { return $null }
    return $v
}

$TestMode = @(Get-ChildItem Env: | Where-Object { $_.Name -like 'SEEDLAB_SCRIPT_TEST_*' }).Count -gt 0
if ($TestMode -and (TestVar 'SEEDLAB_CACHE_DIR') -eq $null -and (TestVar 'SEEDLAB_SCRIPT_TEST_CACHE') -ne $null) {
    # Every vseed this script starts inherits it: a test never reaches the real %LOCALAPPDATA%\SeedLab.
    [Environment]::SetEnvironmentVariable('SEEDLAB_CACHE_DIR', (TestVar 'SEEDLAB_SCRIPT_TEST_CACHE'), 'Process')
}

$script:Answers = $null
if ((TestVar 'SEEDLAB_SCRIPT_TEST_ANSWERS') -ne $null) {
    $script:Answers = New-Object System.Collections.Queue
    foreach ($x in (TestVar 'SEEDLAB_SCRIPT_TEST_ANSWERS').Split('|')) { $script:Answers.Enqueue($x) }
}

# One line of input, or $null when there is nobody to ask (then every caller takes the safe answer).
function Read-Answer([string]$prompt) {
    if ($script:Answers -ne $null) {
        if ($script:Answers.Count -eq 0) {
            Write-Host ($prompt + '(no test answer left: taking the safe answer)')
            return $null
        }
        $a = [string]$script:Answers.Dequeue()
        Write-Host ($prompt + $a + '   (test answer)')
        return $a
    }
    Write-Host $prompt -NoNewline
    if ([Console]::IsInputRedirected) {
        $line = [Console]::In.ReadLine()
        if ($line -eq $null) {
            Write-Host '(no keyboard input: taking the safe answer)'
            return $null
        }
        Write-Host $line
        return $line
    }
    return (Read-Host)
}

function Ask-YesNo([string]$question, [bool]$defaultYes) {
    $hint = ' [y/N] '
    if ($defaultYes) { $hint = ' [Y/n] ' }
    for ($i = 0; $i -lt 3; $i++) {
        $a = Read-Answer ($question + $hint)
        if ($a -eq $null) { return $false }
        $a = $a.Trim().ToLowerInvariant()
        if ($a -eq '') { return $defaultYes }
        if ($a -eq 'y' -or $a -eq 'yes') { return $true }
        if ($a -eq 'n' -or $a -eq 'no') { return $false }
        Say 'Please answer y (yes) or n (no).'
    }
    return $false
}

# Returns one of $letters (upper case), or $null when there is no answer.
function Ask-Choice([string]$question, [string[]]$letters) {
    for ($i = 0; $i -lt 3; $i++) {
        $a = Read-Answer ($question + ' [' + ($letters -join '/') + '] ')
        if ($a -eq $null) { return $null }
        $a = $a.Trim().ToUpperInvariant()
        foreach ($l in $letters) { if ($a -eq $l) { return $l } }
        Say ('Please type one of: ' + ($letters -join ', ') + '.')
    }
    return $null
}

function Format-Size([double]$bytes) {
    if ($bytes -ge 1GB) { return ('{0:0.0} GB' -f ($bytes / 1GB)) }
    if ($bytes -ge 1MB) { return ('{0:0.0} MB' -f ($bytes / 1MB)) }
    if ($bytes -ge 1KB) { return ('{0:0} KB' -f ($bytes / 1KB)) }
    return ('{0:0} bytes' -f $bytes)
}

function Get-FolderSize([string]$path) {
    $sum = 0.0
    foreach ($f in @(Get-ChildItem -LiteralPath $path -Recurse -File -Force -ErrorAction SilentlyContinue)) {
        $sum += $f.Length
    }
    return $sum
}

# -------------------------------------------------------------------------------------------------
# A little native code: the environment-change broadcast, long path names, Ctrl+C, the Recycle Bin.
# Compiled only when an action needs it (it takes a second or two).
# -------------------------------------------------------------------------------------------------

$NativeSource = @'
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace SeedLabScript
{
    public static class Native
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam,
                                                        uint flags, uint timeoutMs, out UIntPtr result);

        // WM_SETTINGCHANGE with "Environment" to every top-level window, so that Explorer - and so
        // every window opened from now on - reads the changed user environment. SMTO_ABORTIFHUNG.
        public static bool BroadcastEnvironmentChange()
        {
            UIntPtr result;
            return SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, UIntPtr.Zero, "Environment", 0x0002, 5000,
                                      out result) != IntPtr.Zero;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetLongPathName(string shortPath, StringBuilder longPath, uint size);

        public static string LongPath(string path)
        {
            StringBuilder sb = new StringBuilder(32768);
            uint n = GetLongPathName(path, sb, (uint)sb.Capacity);
            return (n == 0 || n >= sb.Capacity) ? path : sb.ToString();
        }

        public delegate bool ConsoleCtrlHandler(uint ctrlType);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandler handler, bool add);

        private static readonly ConsoleCtrlHandler Swallow = SwallowCtrlC;
        private static bool SwallowCtrlC(uint ctrlType) { return ctrlType == 0 || ctrlType == 1; }

        // While vseed runs in the foreground, Ctrl+C must stop vseed and NOT this script, so that the
        // script can still say what happened and keep the window open. Every process attached to the
        // console gets its own Ctrl+C; this handler only makes THIS process ignore its copy. (It is a
        // handler routine, not the NULL "ignore" flag, because that flag is inherited by children.)
        public static void IgnoreCtrlC(bool on) { SetConsoleCtrlHandler(Swallow, on); }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT64
        {
            public IntPtr hwnd; public uint wFunc; public string pFrom; public string pTo; public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings; public string lpszProgressTitle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
        private struct SHFILEOPSTRUCT32
        {
            public IntPtr hwnd; public uint wFunc; public string pFrom; public string pTo; public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings; public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
        private static extern int SHFileOperation64(ref SHFILEOPSTRUCT64 op);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
        private static extern int SHFileOperation32(ref SHFILEOPSTRUCT32 op);

        // Moves a file or folder to the Recycle Bin. FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT |
        // FOF_WANTNUKEWARNING: no second "are you sure" (the script already asked), no progress window,
        // but if Windows would have to delete it PERMANENTLY instead - a folder too big for the Recycle
        // Bin, or a drive that has none - Windows itself warns and lets the user say no.
        // (Microsoft.VisualBasic's SendToRecycleBin with OnlyErrorDialogs passes FOF_NOCONFIRMATION
        // without FOF_WANTNUKEWARNING, which deletes such a folder permanently without a word.)
        public static int Recycle(string fullPath, out bool aborted)
        {
            const ushort flags = 0x0040 | 0x0010 | 0x0004 | 0x4000;
            string from = fullPath + "\0";
            int rc;
            if (IntPtr.Size == 8)
            {
                SHFILEOPSTRUCT64 op = new SHFILEOPSTRUCT64();
                op.wFunc = 3; op.pFrom = from; op.fFlags = flags;
                rc = SHFileOperation64(ref op);
                aborted = op.fAnyOperationsAborted;
            }
            else
            {
                SHFILEOPSTRUCT32 op = new SHFILEOPSTRUCT32();
                op.wFunc = 3; op.pFrom = from; op.fFlags = flags;
                rc = SHFileOperation32(ref op);
                aborted = op.fAnyOperationsAborted;
            }
            return rc;
        }
    }
}
'@

$script:NativeLoaded = $false
function Use-Native {
    if ($script:NativeLoaded) { return }
    if (-not ('SeedLabScript.Native' -as [type])) {
        Add-Type -TypeDefinition $NativeSource -Language CSharp
    }
    $script:NativeLoaded = $true
}

# -------------------------------------------------------------------------------------------------
# Where things are
# -------------------------------------------------------------------------------------------------

$Repo = $null
try { $Repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).ProviderPath } catch { $Repo = $null }
if (-not $Repo -or -not (Test-Path -LiteralPath (Join-Path $Repo 'src\SeedLab.Cli\SeedLab.Cli.csproj'))) {
    Say-Bad 'This script must stay where it came with SeedLab: in scripts\windows inside the SeedLab folder.'
    Say-Bad 'The SeedLab folder (with src\SeedLab.Cli in it) was not found two folders above it.'
    exit 2
}
if ($Repo.Contains('~')) {
    # An old 8.3 short name (C:\PROGRA~1\...) would register a Path entry that does not look like the
    # folder the user knows, and would not compare equal to the long spelling.
    Use-Native
    $Repo = [SeedLabScript.Native]::LongPath($Repo)
}
$Repo = $Repo.TrimEnd('\')
$CliProject = Join-Path $Repo 'src\SeedLab.Cli\SeedLab.Cli.csproj'
$BuildDir = Join-Path $Repo 'src\SeedLab.Cli\bin\Release\net10.0'
$VseedExe = Join-Path $BuildDir 'vseed.exe'
$StampFile = Join-Path $BuildDir 'seedlab-build-windows.stamp'
$SdkPage = 'https://dotnet.microsoft.com/download/dotnet/10.0'
# The winget package id of the .NET 10 SDK, as Microsoft documents it: "Install .NET on Windows",
# section "Install with Windows Package Manager (WinGet)", https://learn.microsoft.com/dotnet/core/install/windows
# ("winget install Microsoft.DotNet.SDK.10"; the page also says those installs are system-wide).
$WingetSdkId = 'Microsoft.DotNet.SDK.10'

function Normalize-Dir([string]$p) {
    if ($p -eq $null) { return '' }
    $x = $p.Trim().Trim('"').Trim()
    if ($x -eq '') { return '' }
    $x = [Environment]::ExpandEnvironmentVariables($x)
    return $x.TrimEnd('\').ToLowerInvariant()
}

function Get-DefaultCacheRoot {
    $t = TestVar 'SEEDLAB_SCRIPT_TEST_CACHE'
    # GetFullPath, as for SEEDLAB_CACHE_DIR below, so that two spellings of one folder (an 8.3 short
    # name such as C:\Users\ABCDEF~1) compare equal.
    if ($t -ne $null) { try { return [IO.Path]::GetFullPath($t).TrimEnd('\') } catch { return $t.TrimEnd('\') } }
    return (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'SeedLab')
}

function Get-DumperOutputDir {
    $t = TestVar 'SEEDLAB_SCRIPT_TEST_DUMPER_OUT'
    if ($t -ne $null) { return $t.TrimEnd('\') }
    return (Join-Path $env:USERPROFILE 'AppData\valheim-dumper')
}

# The cache folder a vseed started by this script uses when it is given no --cache-dir: vseed's own
# rule, SEEDLAB_CACHE_DIR if it is set, otherwise %LOCALAPPDATA%\SeedLab. (In test mode the script
# has set SEEDLAB_CACHE_DIR to the scratch folder.)
function Get-VseedDefaultRoot {
    $v = TestVar 'SEEDLAB_CACHE_DIR'
    if ($v -ne $null) {
        try { return [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($v)).TrimEnd('\') } catch { }
    }
    return (Get-DefaultCacheRoot)
}

# -------------------------------------------------------------------------------------------------
# The user environment (HKCU\Environment, or the test key)
# -------------------------------------------------------------------------------------------------

function Get-EnvKeyName {
    $t = TestVar 'SEEDLAB_SCRIPT_TEST_ENVKEY'
    if ($t -eq $null) { return 'Environment' }
    if ($t -match '^(?i)(HKCU:\\|HKEY_CURRENT_USER\\|Registry::HKEY_CURRENT_USER\\)(.+)$') {
        return $Matches[2].TrimEnd('\')
    }
    throw ("SEEDLAB_SCRIPT_TEST_ENVKEY must name a key under HKCU:\ - it is '" + $t + "'.")
}

function Open-EnvKey([bool]$writable) {
    $name = Get-EnvKeyName
    if ($writable) { return [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($name) }
    return [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($name, $false)
}

# The user Path exactly as stored: %VARIABLES% not expanded, and its registry kind.
function Get-UserPath {
    $o = New-Object psobject -Property @{
        Name = 'Path'; Raw = ''; Exists = $false
        Kind = [Microsoft.Win32.RegistryValueKind]::ExpandString
    }
    $k = Open-EnvKey $false
    if ($k -eq $null) { return $o }
    try {
        foreach ($n in $k.GetValueNames()) {
            if ($n -ieq 'Path') {
                $o.Name = $n
                $o.Raw = [string]$k.GetValue($n, '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                $o.Kind = $k.GetValueKind($n)
                $o.Exists = $true
            }
        }
    } finally { $k.Close() }
    return $o
}

function Set-UserPath($info, [string]$raw) {
    $k = Open-EnvKey $true
    try {
        if ($raw -eq '') {
            if ($info.Exists) { $k.DeleteValue($info.Name, $false) }
        } else {
            # Same kind as before: a REG_EXPAND_SZ Path stays REG_EXPAND_SZ, so the %VARIABLES% in
            # other entries keep working. (setx would also cut the value at 1024 characters and mix
            # the machine Path into it.)
            $k.SetValue($info.Name, $raw, $info.Kind)
        }
    } finally { $k.Close() }
}

function Get-UserEnvValue([string]$name) {
    $k = Open-EnvKey $false
    if ($k -eq $null) { return $null }
    try {
        $v = $k.GetValue($name, $null)
        if ($v -eq $null) { return $null }
        return [string]$v
    } finally { $k.Close() }
}

function Get-UserSeedLabVars {
    $k = Open-EnvKey $false
    if ($k -eq $null) { return }
    try {
        foreach ($n in $k.GetValueNames()) {
            if ($n -like 'SEEDLAB_*') {
                New-Object psobject -Property @{ Name = $n; Value = [string]$k.GetValue($n, '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) }
            }
        }
    } finally { $k.Close() }
}

function Remove-UserEnvValue([string]$name) {
    $k = Open-EnvKey $true
    try { $k.DeleteValue($name, $false) } finally { $k.Close() }
}

function Send-EnvironmentChange {
    if ($TestMode) { Say '  (test mode: the environment-change broadcast was skipped)'; return }
    Use-Native
    [void][SeedLabScript.Native]::BroadcastEnvironmentChange()
}

function Split-PathList([string]$raw) {
    if ([string]::IsNullOrEmpty($raw)) { return }
    foreach ($e in $raw.Split(';')) { $e }
}

function Remove-PathEntries([string]$raw, [string[]]$remove) {
    $drop = @{}
    foreach ($r in $remove) { $drop[(Normalize-Dir $r)] = $true }
    $keep = New-Object System.Collections.Generic.List[string]
    foreach ($e in @(Split-PathList $raw)) {
        $n = Normalize-Dir $e
        if ($n -ne '' -and $drop.ContainsKey($n)) { continue }
        $keep.Add($e)
    }
    $out = ($keep -join ';')
    if ($out.Trim(';').Trim() -eq '') { return '' }
    return $out
}

# Classifies the SeedLab entries of the user Path: this build's, ones whose folder is gone, others.
function Get-PathReport {
    $info = Get-UserPath
    $mine = Normalize-Dir $BuildDir
    $r = New-Object psobject -Property @{ Info = $info; Ours = @(); Dangling = @(); Others = @() }
    foreach ($e in @(Split-PathList $info.Raw)) {
        $n = Normalize-Dir $e
        if ($n -eq '') { continue }
        if ($n -eq $mine) { $r.Ours += $e; continue }
        if ($n -like '*\src\seedlab.cli\bin\release\net10.0') {
            $exists = $false
            try { $exists = Test-Path -LiteralPath ([Environment]::ExpandEnvironmentVariables($e.Trim().Trim('"'))) } catch { $exists = $false }
            if ($exists) { $r.Others += $e } else { $r.Dangling += $e }
        }
    }
    return $r
}

# The vseed a NEW window would run: Windows builds its Path as the machine Path, then the user Path.
function Get-FirstVseedOnNewPath {
    $machine = [string][Environment]::GetEnvironmentVariable('Path', 'Machine')
    $user = [Environment]::ExpandEnvironmentVariables((Get-UserPath).Raw)
    foreach ($d in (($machine + ';' + $user).Split(';'))) {
        $d2 = $d.Trim().Trim('"')
        if ($d2 -eq '') { continue }
        foreach ($name in @('vseed.exe', 'vseed.cmd', 'vseed.bat', 'vseed.com')) {
            try {
                $f = Join-Path $d2 $name
                if (Test-Path -LiteralPath $f -PathType Leaf) { return $f }
            } catch { }
        }
    }
    return $null
}

# -------------------------------------------------------------------------------------------------
# Running programs
# -------------------------------------------------------------------------------------------------

function Get-ArgsAfterProgram([string]$cl) {
    if ($cl -match '^\s*"[^"]*"\s*(.*)$') { return $Matches[1] }
    if ($cl -match '^\s*\S+\s*(.*)$') { return $Matches[1] }
    return ''
}

# Every running vseed.exe, with what it is doing and whether it was started from THIS folder's build.
function Get-VseedProcesses {
    $procs = @()
    try { $procs = @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'vseed.exe'" -ErrorAction Stop) } catch { $procs = @() }
    $mine = Normalize-Dir $BuildDir
    foreach ($p in $procs) {
        $exe = [string]$p.ExecutablePath
        $rest = Get-ArgsAfterProgram ([string]$p.CommandLine)
        $isServer = ($rest -match '(?i)(^|\s)serve(\s|$)') -and
                    ($rest -notmatch '(?i)--(selftest|status|stop|help)\b') -and
                    ($rest -notmatch '(^|\s)-h(\s|$)')
        $port = 8731
        if ($rest -match '(?i)--port(=|\s+)(\d+)') { $port = [int]$Matches[2] }
        $dir = ''
        if ($exe -ne '') { $dir = Normalize-Dir (Split-Path -Parent $exe) }
        $started = ''
        try { if ($p.CreationDate) { $started = $p.CreationDate.ToString('yyyy-MM-dd HH:mm') } } catch { }
        New-Object psobject -Property @{
            Id = [int]$p.ProcessId; Exe = $exe; Args = $rest; IsServer = $isServer; Port = $port
            Ours = ($dir -ne '' -and $dir -eq $mine); Started = $started
        }
    }
}

# Every running program whose .exe lives inside this SeedLab folder (vseed, a test program, a tool).
function Get-RepoProcesses {
    $root = (Normalize-Dir $Repo) + '\'
    $all = @()
    try { $all = @(Get-CimInstance -ClassName Win32_Process -ErrorAction Stop) } catch { $all = @() }
    foreach ($p in $all) {
        $exe = [string]$p.ExecutablePath
        if ($exe -eq '') { continue }
        if ((Normalize-Dir $exe).StartsWith($root)) {
            New-Object psobject -Property @{ Id = [int]$p.ProcessId; Exe = $exe; Args = (Get-ArgsAfterProgram ([string]$p.CommandLine)); Started = '' }
        }
    }
}

function Show-Processes($list) {
    foreach ($p in $list) {
        $line = '    process ' + $p.Id
        if ($p.Started) { $line += ', started ' + $p.Started }
        $leaf = 'vseed.exe'
        if ($p.Exe) { $leaf = Split-Path -Leaf $p.Exe }
        $line += ': ' + $leaf + ' ' + $p.Args
        Say $line
        if ($p.Exe) { Say ('      from ' + (Split-Path -Parent $p.Exe)) }
    }
}

function Stop-Processes($list) {
    $ok = $true
    foreach ($p in $list) {
        try {
            Stop-Process -Id $p.Id -Force -ErrorAction Stop
            try { Wait-Process -Id $p.Id -Timeout 15 -ErrorAction SilentlyContinue } catch { }
            Say ('  stopped process ' + $p.Id)
        } catch {
            if (Get-Process -Id $p.Id -ErrorAction SilentlyContinue) {
                Say-Bad ('  could not stop process ' + $p.Id + ': ' + $_.Exception.Message)
                $ok = $false
            } else {
                Say ('  process ' + $p.Id + ' had already stopped')
            }
        }
    }
    return $ok
}

function Quote-Arg([string]$a) {
    if ($a -eq '') { return '""' }
    if ($a -notmatch '[\s"]') { return $a }
    return '"' + (($a -replace '(\\*)"', '$1$1\"') -replace '(\\+)$', '$1$1') + '"'
}

# Runs a program with its output captured (not shown). Its input is closed, so it never waits for a key.
function Invoke-Captured([string]$exe, [string[]]$argList, [int]$timeoutSec = 120) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = ((@($argList) | ForEach-Object { Quote-Arg $_ }) -join ' ')
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.RedirectStandardInput = $true
    $psi.CreateNoWindow = $true
    $psi.WorkingDirectory = $Repo
    $p = [System.Diagnostics.Process]::Start($psi)
    $p.StandardInput.Close()
    $so = $p.StandardOutput.ReadToEndAsync()
    $se = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit($timeoutSec * 1000)) {
        try { $p.Kill() } catch { }
        return New-Object psobject -Property @{ Code = -1; Out = ''; Err = 'timed out'; TimedOut = $true }
    }
    $p.WaitForExit()
    return New-Object psobject -Property @{ Code = $p.ExitCode; Out = [string]$so.Result; Err = [string]$se.Result; TimedOut = $false }
}

# Runs a program in this window: its output goes straight to the screen and it can ask questions.
# (Calling it with & inside a function would capture its output into the function's return value.)
# Only that process is waited for, not programs it starts - such as the browser vseed serve opens.
function Invoke-Live([string]$exe, [string[]]$argList) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = ((@($argList) | ForEach-Object { Quote-Arg $_ }) -join ' ')
    $psi.UseShellExecute = $false
    $psi.WorkingDirectory = $Repo
    $p = [System.Diagnostics.Process]::Start($psi)
    $p.WaitForExit()
    return $p.ExitCode
}

# Whether this build's 'vseed serve' knows an option. Read from 'vseed serve --help', which prints
# its text and exits before vseed starts any real work.
$script:ServeHelp = $null
function Test-ServeOption([string]$opt) {
    if (-not (Test-Path -LiteralPath $VseedExe)) { return $false }
    if ($script:ServeHelp -eq $null) {
        $r = Invoke-Captured $VseedExe @('serve', '--help') 60
        $script:ServeHelp = $r.Out + $r.Err
    }
    return ($script:ServeHelp -match ([regex]::Escape($opt) + '\b'))
}

# Asks a local port whether SeedLab's page is there. Loopback only; the proxy is bypassed.
function Test-SeedLabAt([int]$port) {
    if ($port -le 0) { return $false }
    try {
        $req = [System.Net.HttpWebRequest]::Create('http://127.0.0.1:' + $port + '/')
        $req.Proxy = $null
        $req.Timeout = 3000
        $resp = $req.GetResponse()
        try {
            $reader = New-Object System.IO.StreamReader($resp.GetResponseStream())
            $body = $reader.ReadToEnd()
        } finally { $resp.Close() }
        return ($body -match '<title>SeedLab</title>')
    } catch { return $false }
}

# Only two kinds of address are ever opened: SeedLab's own page on this computer, and Microsoft's SDK
# download page. Start-Process runs whatever it is given, and a server's address comes from a file in
# the cache folder (review of 2026-09-25).
function Open-Url([string]$url) {
    if (-not ($url -match '^http://127\.0\.0\.1:\d{1,5}/?$') -and $url -ne $SdkPage) {
        Say-Warn ('Not opened, because it is not an address of SeedLab''s page: ' + $url)
        return
    }
    if ((TestVar 'SEEDLAB_SCRIPT_TEST_NO_WINDOWS') -eq '1') { Say ('  (test mode: would open ' + $url + ' in the browser)'); return }
    try { Start-Process $url } catch { Say-Warn ('Could not open the browser. Open this address yourself: ' + $url) }
}

# The address of SeedLab's page served by one of these processes, found by asking the port each one
# was given - or, for --port 0 (the system picks the port), the ports the process is listening on.
# For servers vseed cannot report on: an older build, or one no cache folder here knows about.
function Find-PageOf($procs) {
    foreach ($s in $procs) {
        $ports = New-Object System.Collections.Generic.List[int]
        if ($s.Port -gt 0) { $ports.Add($s.Port) }
        try {
            foreach ($c in @(Get-NetTCPConnection -OwningProcess $s.Id -State Listen -ErrorAction Stop)) {
                if (-not $ports.Contains([int]$c.LocalPort)) { $ports.Add([int]$c.LocalPort) }
            }
        } catch { }
        foreach ($port in $ports) {
            if (Test-SeedLabAt $port) { return ('http://127.0.0.1:' + $port) }
        }
    }
    return $null
}

# -------------------------------------------------------------------------------------------------
# SeedLab's web server, as vseed reports it (vseed serve --status and --stop, since 2026-09-24)
#
# A running server registers itself in the cache folder it was started with. So "is it running" is
# asked of each cache folder a server of this account can be in: the one vseed uses by itself, the
# usual one, and the one SEEDLAB_CACHE_DIR chooses for the account. --status and --stop start
# nothing and create nothing - not even the cache folder - so they are safe to ask at any time,
# including right after an uninstall has removed that folder.
# -------------------------------------------------------------------------------------------------

function Get-ServerRoots {
    $seen = @{}
    foreach ($r in @((Get-VseedDefaultRoot), (Get-DefaultCacheRoot), (Get-ChosenCacheDir))) {
        if ([string]::IsNullOrEmpty($r)) { continue }
        $k = Normalize-Dir $r
        if ($seen.ContainsKey($k)) { continue }
        $seen[$k] = $true
        $r
    }
}

# A property of an object ConvertFrom-Json made, or $null when it is absent (strict mode would throw).
function Get-JsonProp($o, [string]$name) {
    if ($o -eq $null) { return $null }
    $p = $o.PSObject.Properties[$name]
    if ($p -eq $null) { return $null }
    return $p.Value
}

function Format-LocalTime([string]$utc) {
    try {
        $t = [DateTime]::Parse($utc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind)
        return $t.ToLocalTime().ToString('HH:mm', [Globalization.CultureInfo]::InvariantCulture)
    } catch { return '?' }
}

# One cache folder's servers, from 'vseed serve --status --json'. Problem is set when vseed could not
# answer (then nothing is known about that folder).
function Get-ServeStatus([string]$root) {
    $o = New-Object psobject -Property @{ Root = $root; Servers = @(); Unregistered = @(); Problem = ''; LeftOver = @() }
    $r = Invoke-Captured $VseedExe @('serve', '--status', '--json', '--cache-dir', $root) 60
    if ($r.Code -ne 0 -and $r.Code -ne 1) {
        $o.Problem = 'vseed serve --status ended with exit code ' + $r.Code + ': ' + (($r.Out + ' ' + $r.Err).Trim())
        return $o
    }
    $j = $null
    try { $j = $r.Out | ConvertFrom-Json } catch { $j = $null }
    if ($j -eq $null) { $o.Problem = 'vseed serve --status gave an answer this script cannot read'; return $o }
    $inv = [Globalization.CultureInfo]::InvariantCulture
    foreach ($s in @(Get-JsonProp $j 'servers')) {
        if ($s -eq $null) { continue }
        $searches = @()
        foreach ($q in @(Get-JsonProp $s 'searches')) {
            if ($q -eq $null) { continue }
            $resume = Get-JsonProp $q 'resume_command'
            $text = '"' + [string](Get-JsonProp $q 'name') + '", ' +
                    ([double](Get-JsonProp $q 'percent')).ToString('0.0', $inv) + ' % done (' +
                    ([long](Get-JsonProp $q 'scanned')).ToString('N0', $inv) + ' of ' +
                    ([long](Get-JsonProp $q 'limit')).ToString('N0', $inv) + ' seeds)'
            $searches += New-Object psobject -Property @{ Text = $text; Resume = $resume }
        }
        $o.Servers += New-Object psobject -Property @{
            Root = $root; Id = [int](Get-JsonProp $s 'pid'); Url = [string](Get-JsonProp $s 'url')
            Port = [int](Get-JsonProp $s 'port'); Since = (Format-LocalTime ([string](Get-JsonProp $s 'started_utc')))
            Answering = [bool](Get-JsonProp $s 'answering'); Stopping = [bool](Get-JsonProp $s 'stopping')
            Searches = $searches
        }
    }
    # Files in serve\ that name no running server (review of 2026-09-25): said, so a person can see them.
    foreach ($f in @(Get-JsonProp $j 'left_over_files')) { if ($f) { $o.LeftOver += [string]$f } }
    foreach ($u in @(Get-JsonProp $j 'unregistered')) {
        if ($u -eq $null) { continue }
        $o.Unregistered += New-Object psobject -Property @{ Id = [int](Get-JsonProp $u 'pid'); CommandLine = [string](Get-JsonProp $u 'command_line') }
    }
    return $o
}

# Every registered server of every cache folder above (Servers), the vseed web servers none of them
# knows about (Unregistered: from the process list, with whether they belong to this folder's build),
# and the folders vseed could not answer for (Problems).
function Get-ServeReport {
    $rep = New-Object psobject -Property @{ Servers = @(); Unregistered = @(); Problems = @(); LeftOver = @() }
    foreach ($root in @(Get-ServerRoots)) {
        $st = Get-ServeStatus $root
        if ($st.Problem) { $rep.Problems += ($root + ': ' + $st.Problem); continue }
        $rep.LeftOver += @($st.LeftOver)
        foreach ($s in $st.Servers) {
            if (@($rep.Servers | Where-Object { $_.Id -eq $s.Id }).Count -eq 0) { $rep.Servers += $s }
        }
    }
    $ids = @($rep.Servers | ForEach-Object { $_.Id })
    $rep.Unregistered = @(Get-VseedProcesses | Where-Object { $_.IsServer -and ($ids -notcontains $_.Id) })
    return $rep
}

function Show-RegisteredServer($s) {
    $line = 'SeedLab''s web server is running at ' + $s.Url + ' since ' + $s.Since + ' (process ' + $s.Id + ')'
    if (-not $s.Answering) { $line += ', but it did not answer just now - it may be busy starting or stopping.' }
    elseif ($s.Stopping) { $line += '; it is stopping.' }
    elseif (@($s.Searches).Count -eq 0) { $line += '; no search is running.' }
    else { $line += '; a search is running in it:' }
    Say $line
    foreach ($q in @($s.Searches)) {
        Say ('    ' + $q.Text)
        if (-not $q.Resume) { Say '    It has no resume point yet (it is in its first stage): stopping it now loses its work so far.' }
    }
}

# $how: 'also' - a registered server was just shown, so this one is also running; 'it' - the line above
# is about this very server; 'but' - the lines above said none is running, and this is the exception.
# Registry files that name no running SeedLab web server: left by one that was ended without stopping
# (Task Manager, a power cut), or not SeedLab's. vseed ignores them and the next server removes them;
# they are named so that nothing is hidden (review of 2026-09-25).
function Show-LeftOver($files) {
    if (@($files).Count -eq 0) { return }
    Say ''
    Say 'A file in SeedLab''s cache folder says a web server is running, but none is - it was left behind'
    Say 'by one that ended without stopping (a crash, a power cut, Task Manager). It is ignored, the next'
    Say 'web server removes it, and deleting it by hand is safe:'
    foreach ($f in @($files)) { Say ('  ' + $f) }
}

function Show-Unregistered($list, [string]$how = 'but') {
    if (@($list).Count -eq 0) { return }
    Say ''
    if ($how -eq 'also') { Say 'A vseed web server is also running that no SeedLab cache folder here knows about:' }
    elseif ($how -eq 'it') { Say 'It is a vseed web server that no SeedLab cache folder here knows about:' }
    else { Say 'But a vseed web server IS running that no SeedLab cache folder here knows about:' }
    Show-Processes $list
    Say 'It was started with another cache folder - then "SeedLab.bat stop --cache-dir <that folder>"'
    Say 'stops it - or by an older SeedLab. Otherwise stop it in its own window: press Ctrl+C twice there,'
    Say 'or close that window.'
}

# Stops these registered servers through vseed, one cache folder at a time. vseed stops a running
# search at once with its checkpoint saved; when one is running it asks first - unless $searchesAgreed
# (the user has already said yes here, so it gets --yes). Returns vseed's exit code: 0 stopped.
function Stop-Registered($servers, [bool]$searchesAgreed) {
    $result = 0
    $roots = @{}
    foreach ($s in @($servers)) { $roots[(Normalize-Dir $s.Root)] = $s.Root }
    foreach ($k in @($roots.Keys)) {
        $a = @('serve', '--stop', '--cache-dir', $roots[$k])
        if ($searchesAgreed) { $a += '--yes' }
        $code = Invoke-Live $VseedExe $a
        if ($code -ne 0) { $result = $code }
    }
    return $result
}

# Is a SeedLab web server running, and where?
#   Known      - the answer is certain; Running - one is running; Url - its address, when known
#   Registered - the servers vseed reported (with their cache folder); Servers - processes (fallback)
function Get-ServerState {
    $st = New-Object psobject -Property @{
        Known = $true; Running = $false; Url = $null; Servers = @(); Registered = @(); Unregistered = @()
        Problems = @(); Method = ''; LeftOver = @()
    }
    if (Test-ServeOption '--status') {
        $rep = Get-ServeReport
        $st.Method = 'vseed serve --status'
        $st.Registered = $rep.Servers
        $st.Unregistered = $rep.Unregistered
        $st.Servers = $rep.Unregistered
        $st.Problems = $rep.Problems
        $st.LeftOver = $rep.LeftOver
        if ($rep.Servers.Count -gt 0) {
            $st.Running = $true
            $st.Url = $rep.Servers[0].Url
            return $st
        }
        if ($rep.Unregistered.Count -gt 0) {
            $st.Running = $true
            $st.Url = Find-PageOf $rep.Unregistered
            return $st
        }
        if ($rep.Problems.Count -gt 0) { $st.Known = $false; $st.Method = 'vseed serve --status failed' }
        return $st
    }

    # A build from before vseed serve --status: the list of running programs.
    $servers = @(Get-VseedProcesses | Where-Object { $_.IsServer })
    $st.Servers = $servers
    $st.Method = 'the process list (this vseed cannot report on its server: it has no "serve --status")'
    if ($servers.Count -eq 0) { return $st }
    $st.Running = $true
    $st.Url = Find-PageOf $servers
    return $st
}

# -------------------------------------------------------------------------------------------------
# Administrator check
# -------------------------------------------------------------------------------------------------

function Test-Elevated {
    if ((TestVar 'SEEDLAB_SCRIPT_TEST_ELEVATED') -eq '1') { return $true }
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-UacOff {
    try {
        $v = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name EnableLUA -ErrorAction Stop).EnableLUA
        return ($v -eq 0)
    } catch { return $false }
}

function Assert-NotElevated {
    if (-not (Test-Elevated)) { return $true }
    if (-not $TestMode -and (Test-UacOff)) {
        Say-Warn 'Note: User Account Control is turned off on this PC, so every program runs with administrator'
        Say-Warn 'rights and this window cannot be anything else. Continuing.'
        return $true
    }
    Say ''
    Say-Bad 'Stop: this window is running as administrator, and SeedLab refuses to run that way.'
    Say ''
    Say '"Run as administrator" can run this script as a DIFFERENT Windows account - the one whose'
    Say 'password was typed into the prompt. The vseed command would then be registered for that'
    Say 'account instead of yours, and SeedLab''s files would go into that account''s folders.'
    Say 'Nothing SeedLab does needs administrator rights, except installing Microsoft''s .NET SDK,'
    Say 'and for that one step Windows asks you itself, with its own prompt.'
    Say ''
    Say 'Close this window, then double-click the SeedLab file normally (not "Run as administrator").'
    return $false
}

function Show-PlatformNote {
    $arch = [string]$env:PROCESSOR_ARCHITECTURE
    $arch2 = [string][Environment]::GetEnvironmentVariable('PROCESSOR_ARCHITEW6432', 'Process')
    if ($arch -eq 'ARM64' -or $arch2 -eq 'ARM64') {
        Say ''
        Say-Warn 'This PC has an ARM processor. SeedLab has only ever been proven on x64 processors. On any'
        Say-Warn 'other kind, vseed can only prove its arithmetic with the ground-truth recordings'
        Say-Warn '(groundtruth\natives), which are not published, so it refuses to answer unless you add'
        Say-Warn '--accept-unverified-platform - and then its answers may differ from the game''s.'
    }
}

# -------------------------------------------------------------------------------------------------
# Step 1: the .NET 10 SDK
# -------------------------------------------------------------------------------------------------

function Get-DotnetCandidates {
    $list = New-Object System.Collections.Generic.List[string]
    $t = TestVar 'SEEDLAB_SCRIPT_TEST_DOTNET_DIRS'
    if ($t -ne $null) {
        foreach ($d in $t.Split(';')) { if ($d.Trim() -ne '') { $list.Add((Join-Path $d.Trim() 'dotnet.exe')) } }
    } else {
        foreach ($c in @(Get-Command -Name 'dotnet.exe' -CommandType Application -ErrorAction SilentlyContinue)) { $list.Add([string]$c.Source) }
        # Right after an install this window's Path does not have dotnet yet, so look where the
        # installer puts it. InstallLocation is written to the 32-bit view of the registry.
        $arch = 'x64'
        if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { $arch = 'arm64' }
        foreach ($key in @("HKLM:\SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\$arch",
                           "HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\$arch")) {
            try {
                $loc = (Get-ItemProperty -Path $key -Name InstallLocation -ErrorAction Stop).InstallLocation
                if ($loc) { $list.Add((Join-Path $loc 'dotnet.exe')) }
            } catch { }
        }
        if ($env:ProgramFiles) { $list.Add((Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')) }
        $list.Add((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Microsoft\dotnet\dotnet.exe'))
        if ($env:USERPROFILE) { $list.Add((Join-Path $env:USERPROFILE '.dotnet\dotnet.exe')) }
    }
    $seen = @{}
    foreach ($c in $list) {
        $k = Normalize-Dir $c
        if ($seen.ContainsKey($k)) { continue }
        $seen[$k] = $true
        if (Test-Path -LiteralPath $c -PathType Leaf) { $c }
    }
}

# The first dotnet.exe that has a 10.x SDK, or $null. $script:OtherSdks lists what else was seen.
$script:OtherSdks = @()
function Find-Sdk {
    $script:OtherSdks = @()
    foreach ($d in @(Get-DotnetCandidates)) {
        $r = Invoke-Captured $d @('--list-sdks') 60
        $best = $null
        foreach ($line in ($r.Out -split "`r?`n")) {
            if ($line -match '^\s*(\d+)\.(\d+)\.(\S+)\s+\[') {
                if ($Matches[1] -eq '10') { $best = ($Matches[1] + '.' + $Matches[2] + '.' + $Matches[3]) }
                else { $script:OtherSdks += ($line.Trim() + '   (' + $d + ')') }
            }
        }
        if ($best -ne $null) {
            $rt = Invoke-Captured $d @('--list-runtimes') 60
            return New-Object psobject -Property @{
                Dotnet = $d; Version = $best
                AspNet10 = ($rt.Out -match 'Microsoft\.AspNetCore\.App 10\.')
            }
        }
    }
    return $null
}

function Ensure-Sdk {
    Say-Title 'Step 1 of 4: the .NET 10 SDK'
    $sdk = Find-Sdk
    if ($sdk -ne $null) {
        Say ('Found the .NET SDK ' + $sdk.Version + ' (' + $sdk.Dotnet + ').')
        if (-not $sdk.AspNet10) {
            Say-Warn 'Its ASP.NET Core 10 runtime was not found. vseed needs it for EVERY command, not only the'
            Say-Warn 'web page, so the check at the end of this install will fail without it. Installing the'
            Say-Warn 'full .NET 10 SDK from Microsoft (the "B" choice when it is missing) includes it:'
            Say-Warn ('  ' + $SdkPage)
        }
        return $sdk
    }

    Say 'SeedLab needs the .NET 10 SDK and it was not found on this PC.'
    Say ''
    Say 'The .NET SDK is Microsoft''s free toolkit for building .NET programs. SeedLab is built from its'
    Say 'source code, here on your PC, and the SDK also brings the runtimes vseed needs to run.'
    if ($script:OtherSdks.Count -gt 0) {
        Say 'Other versions are installed, but SeedLab needs a 10.x one:'
        foreach ($o in $script:OtherSdks) { Say ('  ' + $o) }
    }
    $winget = Get-Command -Name 'winget.exe' -CommandType Application -ErrorAction SilentlyContinue
    Say ''
    Say 'You can:'
    if ($winget) {
        Say ('  W  install it now with winget, Windows'' own package manager:')
        Say ('       winget install --id ' + $WingetSdkId + ' --exact --source winget')
        Say  '     Before you choose this, know that:'
        Say  '     - it downloads the SDK from Microsoft: about 200 MB;'
        Say  '     - Windows WILL show an administrator (User Account Control) prompt, because the SDK'
        Say  '       is installed for every account on this PC, in C:\Program Files\dotnet. Answer Yes'
        Say  '       to continue. This is the only step of SeedLab that needs administrator rights;'
        Say  '     - winget may first ask you to accept its source agreements. That question is'
        Say  '       winget''s own, not SeedLab''s.'
    } else {
        Say  '  W  (not available: winget, Windows'' package manager, was not found on this PC. It comes'
        Say  '     with "App Installer" from the Microsoft Store.)'
    }
    Say ('  B  open Microsoft''s download page in your browser and install it yourself:')
    Say ('       ' + $SdkPage)
    Say  '     Choose the SDK for Windows, x64 (or Arm64 on an ARM PC). Its installer also asks for'
    Say  '     administrator rights. Then run "SeedLab 1 - Install or update" again.'
    Say  '  C  cancel. Nothing has been changed.'
    Say ''
    $letters = @('B', 'C')
    if ($winget) { $letters = @('W', 'B', 'C') }
    $c = Ask-Choice 'Your choice' $letters
    if ($c -eq 'W') {
        Say ''
        Say 'Running winget. Answer Windows'' administrator prompt when it appears (it may be flashing on'
        Say 'the taskbar rather than in front).'
        $code = Invoke-Live $winget.Source @('install', '--id', $WingetSdkId, '--exact', '--source', 'winget')
        $sdk = Find-Sdk
        if ($sdk -ne $null) {
            Say-Good ('The .NET SDK ' + $sdk.Version + ' is installed (' + $sdk.Dotnet + ').')
            return $sdk
        }
        Say-Bad ('winget finished (exit code ' + $code + ') but the .NET 10 SDK still cannot be found.')
        Say 'If the installer asked to restart Windows, restart and run "SeedLab 1 - Install or update"'
        Say ('again. Otherwise install it from ' + $SdkPage)
        return $null
    }
    if ($c -eq 'B') {
        Open-Url $SdkPage
        Say 'When the SDK is installed, run "SeedLab 1 - Install or update" again.'
        return $null
    }
    Say 'Cancelled. Nothing has been changed.'
    return $null
}

# -------------------------------------------------------------------------------------------------
# Step 2: the build
# -------------------------------------------------------------------------------------------------

# A fingerprint of every source file of the tool (names and contents), written beside the build by a
# successful build, so a later action can tell that the source changed (git pull, a new ZIP).
function Get-SourceFingerprint {
    $rels = New-Object System.Collections.Generic.List[string]
    $src = Join-Path $Repo 'src'
    foreach ($f in @(Get-ChildItem -LiteralPath $src -Recurse -File -Force -ErrorAction SilentlyContinue)) {
        $rel = $f.FullName.Substring($Repo.Length + 1)
        if ($rel -match '\\(bin|obj)\\') { continue }
        $rels.Add($rel)
    }
    if (Test-Path -LiteralPath (Join-Path $Repo 'Directory.Build.props')) { $rels.Add('Directory.Build.props') }
    $sorted = $rels.ToArray()
    [Array]::Sort($sorted, [StringComparer]::OrdinalIgnoreCase)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        foreach ($rel in $sorted) {
            $name = [Text.Encoding]::UTF8.GetBytes($rel.ToLowerInvariant() + [char]0)
            [void]$sha.TransformBlock($name, 0, $name.Length, $null, 0)
            $bytes = [IO.File]::ReadAllBytes((Join-Path $Repo $rel))
            [void]$sha.TransformBlock($bytes, 0, $bytes.Length, $null, 0)
        }
        [void]$sha.TransformFinalBlock((New-Object byte[] 0), 0, 0)
        return ([BitConverter]::ToString($sha.Hash) -replace '-', '')
    } finally { $sha.Dispose() }
}

# missing | current | stale | unknown (built some other way, so the script cannot tell)
function Get-BuildState {
    if (-not (Test-Path -LiteralPath $VseedExe)) { return 'missing' }
    if (-not (Test-Path -LiteralPath $StampFile)) { return 'unknown' }
    $s = ([IO.File]::ReadAllText($StampFile)).Trim()
    if ($s -eq (Get-SourceFingerprint)) { return 'current' }
    return 'stale'
}

$script:TelemetryNoteShown = $false
function Invoke-Build($sdk) {
    Say-Title 'Step 2 of 4: build SeedLab'

    # Built by these scripts from exactly this source: there is nothing to build, and nothing to stop for
    # it (review of 2026-09-25). This used to ask to stop a running web server even then, and "no" ended
    # the install before the vseed command and the check - on a run that had nothing to do.
    if ((Get-BuildState) -eq 'current') {
        Say 'SeedLab is already built from exactly this source - nothing to build.'
        return $true
    }

    # Windows locks a running program's files, and the build would fail with MSB3027 / MSB3021.
    $running = @(Get-VseedProcesses | Where-Object { $_.Ours })
    if ($running.Count -gt 0) {
        Say-Warn 'vseed from this build is running, and Windows will not let the build replace a running'
        Say-Warn 'program''s files (the build would fail with error MSB3027):'
        Show-Processes $running
        # A web server this build can ask to stop is stopped that way: a search running in it is
        # stopped with its checkpoint saved. Anything else is ended at once.
        $ids = @($running | ForEach-Object { $_.Id })
        $servers = @()
        if (Test-ServeOption '--stop') { $servers = @((Get-ServeReport).Servers | Where-Object { $ids -contains $_.Id }) }
        foreach ($s in $servers) { Show-RegisteredServer $s }
        if ($servers.Count -gt 0) {
            Say 'SeedLab''s web server is asked to stop the normal way: a search running in it is stopped with'
            Say 'its checkpoint saved, and vseed prints the command that continues it.'
        }
        if ($running.Count -gt $servers.Count) {
            Say 'The rest is ended at once: a search running there loses what its last checkpoint did not hold.'
        }
        if (-not (Ask-YesNo 'Stop it now so the build can go ahead?' $false)) {
            Say 'Left running. The build was not started; stop vseed and run this again.'
            return $false
        }
        if ($servers.Count -gt 0) { [void](Stop-Registered $servers $true) }
        $left = @(Get-VseedProcesses | Where-Object { $_.Ours })
        if ($left.Count -gt 0 -and -not (Stop-Processes $left)) { return $false }
    }

    Say 'Building SeedLab from the source in this folder:'
    Say ('  dotnet build src\SeedLab.Cli\SeedLab.Cli.csproj -c Release')
    Say 'The first build takes a minute or two; later ones are quicker.'
    if (-not $script:TelemetryNoteShown) {
        Say ''
        Say 'Note: the .NET SDK sends usage data ("telemetry") to Microsoft by default. SeedLab''s scripts'
        Say 'turn that off for the builds they run (DOTNET_CLI_TELEMETRY_OPTOUT=1). Other uses of dotnet'
        Say 'on this PC are not changed.'
        $script:TelemetryNoteShown = $true
    }
    Say ''

    $saved = @{}
    foreach ($n in @('DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_NOLOGO')) {
        $saved[$n] = [Environment]::GetEnvironmentVariable($n, 'Process')
        [Environment]::SetEnvironmentVariable($n, '1', 'Process')
    }
    $code = 1
    try {
        # --disable-build-servers: nothing is left running in the background after the build.
        $code = Invoke-Live $sdk.Dotnet @('build', $CliProject, '-c', 'Release', '--nologo', '--disable-build-servers')
    } finally {
        foreach ($n in @($saved.Keys)) { [Environment]::SetEnvironmentVariable($n, $saved[$n], 'Process') }
    }
    if ($code -ne 0 -or -not (Test-Path -LiteralPath $VseedExe)) {
        Say ''
        Say-Bad ('The build failed (exit code ' + $code + '). The lines above say why.')
        Say 'If they mention MSB3027 or MSB3021, a vseed is still running: stop it and try again.'
        return $false
    }
    try { [IO.File]::WriteAllText($StampFile, (Get-SourceFingerprint), (New-Object Text.UTF8Encoding($false))) } catch { }
    # A new build may know options the old one did not: read 'vseed serve --help' again when asked.
    $script:ServeHelp = $null
    Say ''
    Say-Good 'Built.'
    return $true
}

# -------------------------------------------------------------------------------------------------
# Step 3: the vseed command
# -------------------------------------------------------------------------------------------------

function Register-Command {
    Say-Title 'Step 3 of 4: make "vseed" a command'
    $rep = Get-PathReport
    $raw = $rep.Info.Raw
    $changed = $false

    if ($rep.Dangling.Count -gt 0) {
        Say 'Your Path also lists a SeedLab build folder that no longer exists (that SeedLab folder was'
        Say 'moved or deleted):'
        foreach ($d in $rep.Dangling) { Say ('  ' + $d) }
        if (Ask-YesNo 'Remove that entry from your Path?' $true) {
            $raw = Remove-PathEntries $raw $rep.Dangling
            $changed = $true
        }
    }

    if ($rep.Ours.Count -gt 0) {
        Say 'The vseed command is already registered for your Windows account; this folder is on your'
        Say 'user Path:'
        Say ('  ' + $BuildDir)
    } else {
        Say 'Adding this folder to your user Path (HKCU\Environment), so that typing "vseed" works in'
        Say 'every new Command Prompt or PowerShell window:'
        Say ('  ' + $BuildDir)
        Say 'This changes your own Windows account only. Uninstall takes it off again.'
        if ($raw -eq '') { $raw = $BuildDir }
        elseif ($raw.EndsWith(';')) { $raw = $raw + $BuildDir }
        else { $raw = $raw + ';' + $BuildDir }
        $changed = $true
    }

    if ($changed) {
        Set-UserPath $rep.Info $raw
        Send-EnvironmentChange
        Say-Good 'Your Path is updated.'
        Say 'Windows that are already open do not see the change; windows you open from now on do.'
    }
    if ($rep.Others.Count -gt 0) {
        Say ''
        Say-Warn 'Another SeedLab folder is registered on your Path as well:'
        foreach ($o in $rep.Others) { Say-Warn ('  ' + $o) }
        Say-Warn 'A new window runs whichever comes first. Uninstall from that folder to remove its entry.'
    }
}

# -------------------------------------------------------------------------------------------------
# Step 4: check
# -------------------------------------------------------------------------------------------------

function Test-Install {
    Say-Title 'Step 4 of 4: check'
    $r = Invoke-Captured $VseedExe @('--version') 60
    if ($r.Code -ne 0) {
        Say-Bad ('vseed --version failed (exit code ' + $r.Code + '):')
        Say (($r.Out + $r.Err).Trim())
        return $false
    }
    Say ('  ' + $r.Out.Trim())
    $first = Get-FirstVseedOnNewPath
    if ($first -eq $null) {
        Say-Warn 'A new window will not find vseed yet. Sign out of Windows and back in, or use'
        Say-Warn '"SeedLab 4 - Command window", which always knows it.'
    } elseif ((Normalize-Dir (Split-Path -Parent $first)) -ne (Normalize-Dir $BuildDir)) {
        Say-Warn 'A new window will run a DIFFERENT vseed, which comes earlier on the Path:'
        Say-Warn ('  ' + $first)
        Say-Warn 'Remove that one (or its folder from your Path) if you want this build to answer "vseed".'
    } else {
        Say-Good 'A new window will run this build when you type vseed.'
    }
    Show-PlatformNote
    return $true
}

function Do-Install {
    if (-not (Assert-NotElevated)) { return 3 }
    Say-Title 'SeedLab: install or update'
    Say 'This checks for the .NET 10 SDK, builds SeedLab from the source in this folder, and makes'
    Say '"vseed" a command for your Windows account. It is safe to run again at any time - run it after'
    Say 'every update of SeedLab (git pull, or a new ZIP unpacked over this folder).'
    $sdk = Ensure-Sdk
    if ($sdk -eq $null) { return 1 }
    if (-not (Invoke-Build $sdk)) { return 1 }
    Register-Command
    if (-not (Test-Install)) { return 1 }
    Say ''
    Say-Good 'SeedLab is installed.'
    Say 'Next:'
    Say '  - "SeedLab 2 - Open web page" opens the map and the search in your browser;'
    Say '  - "SeedLab 4 - Command window" opens a window where vseed works, in the SeedLab folder;'
    Say '  - or open any NEW Command Prompt or PowerShell window and type:  vseed --help'
    return 0
}

# For the actions that need a build: builds when there is none, and offers to rebuild when the source
# changed since the last build.
function Ensure-Built([string]$purpose) {
    $state = Get-BuildState
    if ($state -eq 'missing') {
        Say ('SeedLab has not been built yet, and ' + $purpose + ' needs the build.')
        Say 'Installing it first does the same as "SeedLab 1 - Install or update".'
        if (-not (Ask-YesNo 'Install SeedLab now?' $true)) { Say 'Nothing was done.'; return $false }
        return ((Do-Install) -eq 0)
    }
    if ($state -eq 'stale') {
        Say 'The SeedLab source has changed since it was last built (an update?).'
        if (Ask-YesNo 'Build it again first (the same as "SeedLab 1 - Install or update")?' $true) {
            return ((Do-Install) -eq 0)
        }
        Say 'Using the existing build.'
    }
    return $true
}

# -------------------------------------------------------------------------------------------------
# web, stop, status, shell
# -------------------------------------------------------------------------------------------------

# The value of a vseed option in a list of arguments ("--port 0" or "--port=0"), or $null.
function Get-OptionValue([string[]]$list, [string]$name) {
    $l = @($list)
    for ($i = 0; $i -lt $l.Count; $i++) {
        $x = [string]$l[$i]
        if ($x -ieq $name -and $i + 1 -lt $l.Count) { return [string]$l[$i + 1] }
        if ($x.StartsWith($name + '=', [StringComparison]::OrdinalIgnoreCase)) { return $x.Substring($name.Length + 1) }
    }
    return $null
}

# The running server that 'vseed serve' with these options would open instead of starting another -
# vseed's own rule: the same cache folder, and with --port N the one on port N (--port 0 always
# starts a new one). $null when there is none.
function Get-SameServer($registered, [string[]]$extra) {
    $root = Get-OptionValue $extra '--cache-dir'
    if ($root -ne $null) {
        # vseed runs from the SeedLab folder, so a relative folder is relative to that.
        if (-not [IO.Path]::IsPathRooted($root)) { $root = Join-Path $Repo $root }
        try { $root = [IO.Path]::GetFullPath($root) } catch { return $null }
    } else {
        $root = Get-VseedDefaultRoot
    }
    $port = Get-OptionValue $extra '--port'
    if ($port -eq '0') { return $null }
    foreach ($s in @($registered)) {
        if ((Normalize-Dir $s.Root) -ne (Normalize-Dir $root)) { continue }
        if ($port -eq $null -or [string]$s.Port -eq $port) { return $s }
    }
    return $null
}

function Do-Web([string[]]$extra) {
    if (-not (Assert-NotElevated)) { return 3 }
    if (-not (Ensure-Built 'the web page')) { return 1 }
    $extra = @($extra)
    if ((TestVar 'SEEDLAB_SCRIPT_TEST_NO_WINDOWS') -eq '1' -and ($extra -notcontains '--no-browser')) { $extra += '--no-browser' }
    $st = Get-ServerState
    if ($st.Running) {
        $same = $null
        if ($st.Registered.Count -gt 0) { $same = Get-SameServer $st.Registered $extra }
        if ($same -ne $null) {
            # vseed's own answer (2026-09-24): a second 'vseed serve' opens the running server's page,
            # says where that server's window is, and exits 0 without starting anything.
            return (Invoke-Live $VseedExe (@('serve') + $extra))
        }
        if ((Get-OptionValue $extra '--port') -eq $null) {
            if ($st.Url) {
                Say ('SeedLab''s web page is already running at ' + $st.Url + ' - opening it in your browser.')
                if ($st.Registered.Count -eq 0) { Show-Unregistered $st.Unregistered 'it' }
                Open-Url $st.Url
                return 0
            }
            Say-Warn 'A SeedLab web server is already running, but its address could not be found out:'
            Show-Processes $st.Servers
            Say 'Look for its window (titled "SeedLab web server"), or stop it with "SeedLab 3 - Stop web page"'
            Say 'and start it again.'
            return 1
        }
        # An explicit --port that the running server is not on: another server, as asked.
    }
    if (-not $st.Known) { Say-Warn ('Could not tell whether a web server is already running (' + $st.Method + '); starting one.') }
    Show-PlatformNote

    # A double-clicked .bat runs this script under cmd.exe, and cmd.exe answers a Ctrl+C by asking
    # "Terminate batch job (Y/N)?" once the server has stopped (seen on Windows 10). So the server
    # gets a window of its own with no cmd.exe in it, and this window is free again (the menu stays
    # usable). With --no-pause (scripts, tests) it runs right here instead.
    #
    # A vseed that knows 'serve --stop' (2026-09-24) owns its window: it titles it "SeedLab web
    # server", prints first what the window is and how to stop it, takes Ctrl+C (twice) and the
    # window's close button itself, and saves a running search before it ends. So vseed.exe is
    # started as the window's ONLY program - nothing else in it to catch a key or keep the window
    # open. An older vseed gets the PowerShell window below, which says those things for it.
    if (-not $script:NoPause -and (Test-ServeOption '--stop')) {
        $argLine = 'serve'
        foreach ($x in $extra) { $argLine += ' ' + (Quote-Arg $x) }
        try {
            Start-Process -FilePath $VseedExe -ArgumentList $argLine -WorkingDirectory $Repo
            Say 'SeedLab''s web server is starting in a window of its own, titled "SeedLab web server".'
            if ($extra -contains '--no-browser') { Say 'Open the address that window prints in your browser.' }
            else { Say 'Your browser opens the page by itself in a few seconds.' }
            Say 'Keep that window open (minimised is fine) while you use the page. To stop it: Stop SeedLab'
            Say 'on the page, "SeedLab 3 - Stop web page", or Ctrl+C twice in its window.'
            $script:SkipFinalPause = $true
            return 0
        } catch {
            Say-Warn ('Could not open a new window (' + $_.Exception.Message + '); starting the server here.')
            return (Start-ServerHere $extra)
        }
    }
    if (-not $script:NoPause) {
        $ps = Join-Path $PSHOME 'powershell.exe'
        $argLine = '-NoProfile -ExecutionPolicy Bypass -File ' + (Quote-Arg $PSCommandPath) + ' serve-window'
        foreach ($x in $extra) { $argLine += ' ' + (Quote-Arg $x) }
        try {
            Start-Process -FilePath $ps -ArgumentList $argLine -WorkingDirectory $Repo
            Say 'SeedLab''s web server is starting in a window of its own, titled "SeedLab web server".'
            Say 'Your browser opens the page by itself in a few seconds. Keep that window open (minimised is'
            Say 'fine) while you use the page; closing it stops the server.'
            # Nothing more to read here: this window may close by itself.
            $script:SkipFinalPause = $true
            return 0
        } catch {
            Say-Warn ('Could not open a new window (' + $_.Exception.Message + '); starting the server here.')
        }
    }
    return (Start-ServerHere $extra)
}

# Runs 'vseed serve' in this window until it stops, then says how it ended.
function Start-ServerHere([string[]]$extra) {
    # A vseed that knows 'serve --stop' titles the window and prints its own banner (Ctrl+C twice, the
    # page's Stop SeedLab); saying it here as well would say it twice, and differently.
    $hasStop = Test-ServeOption '--stop'
    if (-not $hasStop) {
        try { $Host.UI.RawUI.WindowTitle = 'SeedLab web server' } catch { }
        Say ''
        Say '================================================================================'
        Say ' This window IS SeedLab''s web server.'
        Say ' Minimise it, do not close it, while you use the page.'
        Say ' To stop it: "SeedLab 3 - Stop web page", or press Ctrl+C in this window.'
        Say '================================================================================'
        Say ''
    }

    Use-Native
    $code = 0
    [SeedLabScript.Native]::IgnoreCtrlC($true)
    try {
        # Run from the SeedLab folder: vseed finds data\ there, and the page's results files go to
        # its seedlab-results folder.
        $code = Invoke-Live $VseedExe (@('serve') + @($extra))
    } finally {
        [SeedLabScript.Native]::IgnoreCtrlC($false)
    }
    Say ''
    if ($code -eq 0) {
        # A vseed that knows --stop has just said so itself ("SeedLab's web server has stopped").
        if (-not $hasStop) { Say 'The SeedLab web server has stopped.' }
    } elseif ($code -eq -1) {
        Say 'The SeedLab web server was stopped from outside (its process was ended).'
    } else {
        Say-Warn ('The SeedLab web server stopped with exit code ' + $code + '. The lines above say why.')
    }
    return $code
}

# For what vseed serve --stop cannot reach: a build from before it existed, or a server no cache
# folder here knows about. The process is ended directly - abruptly: a search running in it loses
# what its last checkpoint did not hold.
# $confirmed: the user already agreed to stop this folder's server (uninstall's "Go ahead?"), so it
# is not asked again. A server from another folder is always asked about on its own.
function Stop-ServersByProcess($servers, [bool]$confirmed = $false) {
    $ours = @($servers | Where-Object { $_.Ours })
    $foreign = @($servers | Where-Object { -not $_.Ours })
    $result = 0
    if ($foreign.Count -gt 0) {
        Say 'A SeedLab web server from ANOTHER SeedLab folder is running:'
        Show-Processes $foreign
        if ($TestMode) {
            Say '(test mode: servers from other folders are never offered)'
        } elseif (Ask-YesNo 'Stop that one as well?' $false) {
            if (-not (Stop-Processes $foreign)) { $result = 1 }
        } else {
            Say 'Left running.'
        }
    }
    if ($ours.Count -eq 0) { return $result }
    Say 'SeedLab''s web server, started from this folder, is running:'
    Show-Processes $ours
    Say 'It cannot be asked to stop, so its process is ended - abruptly: a search running in the page'
    Say 'stops too, and loses what its last checkpoint did not hold.'
    if (-not $confirmed -and -not (Ask-YesNo 'Stop it now?' $false)) { Say 'Left running.'; return 1 }
    if (-not (Stop-Processes $ours)) { return 1 }
    Say-Good 'The web server has stopped.'
    return $result
}

function Do-Stop([string[]]$extra) {
    if (-not (Assert-NotElevated)) { return 3 }
    if (Test-ServeOption '--stop') {
        if (@($extra).Count -gt 0) {
            # Options were given (SeedLab.bat stop --yes, --force, --cache-dir <folder>): they are for
            # vseed serve --stop, which knows what to do with them.
            return (Invoke-Live $VseedExe (@('serve', '--stop') + @($extra)))
        }
        $rep = Get-ServeReport
        foreach ($p in $rep.Problems) { Say-Warn ('Could not ask vseed about ' + $p) }
        if ($rep.Servers.Count -eq 0) {
            Say 'SeedLab''s web server is not running - nothing to stop.'
            Show-Unregistered $rep.Unregistered 'but'
            Show-LeftOver $rep.LeftOver
            if ($rep.Problems.Count -gt 0) { return 1 }
            return 0
        }
        # vseed stops a running search with its checkpoint saved, and asks first when one is running
        # ("Stop anyway? [y/N]", with the command that continues it). It also lists, itself, any vseed
        # web server its cache folder does not know about, so that is not repeated here.
        return (Stop-Registered $rep.Servers $false)
    }
    $servers = @(Get-VseedProcesses | Where-Object { $_.IsServer })
    if ($servers.Count -eq 0) {
        Say 'No SeedLab web server is running. Nothing to stop.'
        return 0
    }
    if (Test-Path -LiteralPath $VseedExe) {
        Say 'This build of vseed cannot stop its web server by itself yet (it has no "serve --stop"), so'
        Say 'this script stops the server''s process instead.'
    } else {
        Say 'This SeedLab folder has no build (vseed.exe is missing), so this script looks for the web'
        Say 'server''s process instead.'
    }
    return (Stop-ServersByProcess $servers)
}

function Do-Status {
    if (-not (Assert-NotElevated)) { return 3 }
    Say-Title 'SeedLab status'
    Say ('Folder         ' + $Repo)
    $sdk = Find-Sdk
    if ($sdk -ne $null) { Say ('.NET 10 SDK    found: ' + $sdk.Version + ' (' + $sdk.Dotnet + ')') }
    else { Say '.NET 10 SDK    not found ("SeedLab 1 - Install or update" helps you install it)' }
    $state = Get-BuildState
    switch ($state) {
        'missing' { Say 'Build          not built yet' }
        'current' { Say 'Build          built, up to date with the source' }
        'stale'   { Say 'Build          built, but the source has changed since: run "SeedLab 1 - Install or update"' }
        default   { Say 'Build          built (not by these scripts, so they cannot tell whether it is up to date)' }
    }
    $rep = Get-PathReport
    if ($rep.Ours.Count -gt 0) { Say 'Command        "vseed" is registered for your account (on your user Path)' }
    else { Say 'Command        "vseed" is not registered for your account' }
    foreach ($d in $rep.Dangling) { Say ('               a Path entry points at a SeedLab folder that is gone: ' + $d) }
    $st = Get-ServerState
    if ($st.Method -eq 'vseed serve --status' -or $st.Method -eq 'vseed serve --status failed') {
        # What vseed serve --status reports, for every cache folder a server of this account can be in.
        if ($st.Registered.Count -eq 0 -and $st.Unregistered.Count -eq 0 -and $st.Known) { Say 'Web server     not running' }
        elseif ($st.Registered.Count -eq 0 -and $st.Unregistered.Count -eq 0) { Say 'Web server     cannot tell' }
        elseif ($st.Registered.Count -eq 0) { Say 'Web server     not running from any SeedLab cache folder here (see below)' }
        else { Say 'Web server' }
        foreach ($s in $st.Registered) { Say ''; Show-RegisteredServer $s }
        foreach ($p in $st.Problems) { Say-Warn ('               could not ask vseed about ' + $p) }
        if ($st.Registered.Count -gt 0) { Show-Unregistered $st.Unregistered 'also' } else { Show-Unregistered $st.Unregistered 'but' }
        Show-LeftOver $st.LeftOver
        if ($st.Registered.Count -gt 0 -or $st.Unregistered.Count -gt 0) { Say '' }
    }
    elseif ($st.Running -and $st.Url) { Say ('Web server     running at ' + $st.Url + '   (found by ' + $st.Method + ')') }
    elseif ($st.Running) {
        Say ('Web server     running, address unknown   (found by ' + $st.Method + ')')
        Show-Processes $st.Servers
    }
    else { Say 'Web server     not running' }
    # The two session logs (2026-09-24): this session's, or the last one's, and the one before it.
    foreach ($root in @(Get-ServerRoots)) {
        $logs = Join-Path $root 'logs'
        $names = @()
        foreach ($n in @('vseed.log', 'vseed-prev.log')) { if (Test-Path -LiteralPath (Join-Path $logs $n)) { $names += $n } }
        if ($names.Count -gt 0) { Say ('Session logs   ' + $logs + '  (' + ($names -join ', ') + ')') }
    }
    $cache = Get-DefaultCacheRoot
    if (Test-Path -LiteralPath $cache) { Say ('Cache folder   ' + $cache + '  (' + (Format-Size (Get-FolderSize $cache)) + ')') }
    else { Say ('Cache folder   none yet (' + $cache + ')') }
    $chosen = Get-ChosenCacheDir
    if ($chosen -ne $null -and (Normalize-Dir $chosen) -ne (Normalize-Dir $cache)) {
        $exists = 'does not exist yet'
        if (Test-Path -LiteralPath $chosen) { $exists = Format-Size (Get-FolderSize $chosen) }
        Say ('               SEEDLAB_CACHE_DIR chooses ' + $chosen + '  (' + $exists + ')')
    }
    return 0
}

function Do-Shell {
    if (-not (Assert-NotElevated)) { return 3 }
    if (-not (Ensure-Built 'the command window')) { return 1 }
    $exe = $VseedExe.Replace("'", "''")
    $dir = $Repo.Replace("'", "''")
    $cmd = @"
Set-Location -LiteralPath '$dir'
Set-Alias -Name vseed -Value '$exe'
try { `$Host.UI.RawUI.WindowTitle = 'SeedLab command window' } catch { }
Write-Host 'This window knows the vseed command, and it is in the SeedLab folder, where vseed finds its data.' -ForegroundColor Cyan
Write-Host 'Try:  vseed seed 12345    vseed map 12345    vseed search --help    (type exit to close the window)' -ForegroundColor Cyan
Write-Host ''
vseed --help
"@
    $enc = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($cmd))
    $ps = Join-Path $PSHOME 'powershell.exe'
    if ((TestVar 'SEEDLAB_SCRIPT_TEST_NO_WINDOWS') -eq '1') {
        Say '(test mode: running the command window''s commands here, without -NoExit)'
        return (Invoke-Live $ps @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-OutputFormat', 'Text', '-EncodedCommand', $enc))
    }
    Start-Process -FilePath $ps -WorkingDirectory $Repo -ArgumentList ('-NoExit -NoProfile -ExecutionPolicy Bypass -EncodedCommand ' + $enc)
    Say 'A new PowerShell window has opened in the SeedLab folder. "vseed" works in it.'
    return 0
}

# -------------------------------------------------------------------------------------------------
# uninstall
# -------------------------------------------------------------------------------------------------

function Get-ChosenCacheDir {
    $v = Get-UserEnvValue 'SEEDLAB_CACHE_DIR'
    if ([string]::IsNullOrEmpty($v)) { $v = [Environment]::GetEnvironmentVariable('SEEDLAB_CACHE_DIR', 'Process') }
    if ([string]::IsNullOrEmpty($v)) { return $null }
    try { return [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($v)).TrimEnd('\') } catch { return $null }
}

# What SeedLab puts in a cache folder: vseed's category folders and a web server's registry folder,
# and in each only files of the kinds SeedLab writes there. A folder in a SEEDLAB_CACHE_DIR folder counts
# as SeedLab's only when BOTH its name and everything in it fit (review of 2026-09-25): a name alone -
# "logs", "maps" - or any "*.log" also matched a person's own files, which uninstall then offered to
# remove as "SeedLab's own folders". Nothing at the top of the folder but these folders is SeedLab's.
#   <name> = the file-name patterns allowed directly inside it
$CacheLayout = @{
    'checkpoints' = @('*.ckpt', '*.ckpt.top', '*.ckpt.top2', '*.ckpt.query.json', '*.ckpt.tmp', '*.ckpt.top.tmp',
                      '*.ckpt.top2.tmp', '*.survivors', '*.survivors.tmp', '.seedlab-tmp-*')
    'runs'        = @('.seedlab-tmp-*')
    'maps'        = @('map-*.png', '.seedlab-tmp-*')
    'selftest'    = @('passed-*.txt', '.seedlab-tmp-*')
    'logs'        = @('vseed.log', 'vseed-prev.log', 'vseed.log.1', 'vseed.log.2', 'vseed.log.3', 'vseed.log.4')
    'serve'       = @('server-*.json', '.seedlab-tmp-*')
    'tiles'       = @()   # only v<N>\*.png (a tile cache per generator version)
    'scratch'     = @()   # only <run>\... in a run folder that holds SeedLab's owner.txt
}
function Test-SeedLabCacheEntry($item) {
    if (-not $item.PSIsContainer) { return $false }
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return $false }
    $name = $item.Name.ToLowerInvariant()
    if (-not $CacheLayout.ContainsKey($name)) { return $false }
    $base = $item.FullName.TrimEnd('\')
    foreach ($f in @(Get-ChildItem -LiteralPath $base -Recurse -Force -ErrorAction SilentlyContinue)) {
        if (($f.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return $false }
        if ($f.PSIsContainer) { continue }
        $rel = $f.FullName.Substring($base.Length + 1)
        $parts = $rel.Split('\')
        if ($name -eq 'tiles') {
            if ($parts.Count -ne 2 -or $parts[0] -notmatch '^v\d+$' -or -not ($parts[1] -like '*.png' -or $parts[1] -like '.seedlab-tmp-*')) { return $false }
            continue
        }
        if ($name -eq 'scratch') {
            if ($parts.Count -lt 2 -or -not (Test-Path -LiteralPath (Join-Path (Join-Path $base $parts[0]) 'owner.txt'))) { return $false }
            continue
        }
        if ($parts.Count -ne 1) { return $false }
        $fits = $false
        foreach ($pat in $CacheLayout[$name]) { if ($f.Name -like $pat) { $fits = $true; break } }
        if (-not $fits) { return $false }
    }
    return $true
}

function Move-ToRecycleBin([string]$path) {
    Use-Native
    $full = [IO.Path]::GetFullPath($path)
    $aborted = $false
    $rc = [SeedLabScript.Native]::Recycle($full, [ref]$aborted)
    if ($rc -eq 0 -and -not $aborted -and -not (Test-Path -LiteralPath $full)) {
        Say ('  moved to the Recycle Bin: ' + $full)
        return $true
    }
    if ($aborted) { Say-Warn ('  left in place (you said no in Windows'' prompt): ' + $full) }
    else { Say-Bad ('  could not move it to the Recycle Bin (error ' + $rc + '), so it was left in place: ' + $full); Say-Bad '  Something may still be using it: close SeedLab windows and run this again.' }
    return $false
}

function Get-ValheimDirs {
    $list = New-Object System.Collections.Generic.List[string]
    $v = [Environment]::GetEnvironmentVariable('SEEDLAB_VALHEIM_DIR', 'Process')
    if ($v) { $list.Add($v) }
    $v2 = Get-UserEnvValue 'SEEDLAB_VALHEIM_DIR'
    if ($v2) { $list.Add([Environment]::ExpandEnvironmentVariables($v2)) }
    if (-not $TestMode) {
        # A folder above this one (SeedLab kept inside the game folder).
        $d = Split-Path -Parent $Repo
        while ($d) {
            if (Test-Path -LiteralPath (Join-Path $d 'valheim.exe')) { $list.Add($d); break }
            $d = Split-Path -Parent $d
        }
        # Steam and its libraries.
        $roots = New-Object System.Collections.Generic.List[string]
        try { $sp = (Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam' -Name SteamPath -ErrorAction Stop).SteamPath; if ($sp) { $roots.Add($sp.Replace('/', '\')) } } catch { }
        if (${env:ProgramFiles(x86)}) { $roots.Add((Join-Path ${env:ProgramFiles(x86)} 'Steam')) }
        if ($env:ProgramFiles) { $roots.Add((Join-Path $env:ProgramFiles 'Steam')) }
        foreach ($root in $roots) {
            $list.Add((Join-Path $root 'steamapps\common\Valheim'))
            $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
            if (Test-Path -LiteralPath $vdf) {
                foreach ($line in [IO.File]::ReadAllLines($vdf)) {
                    if ($line -match '^\s*"path"\s+"([^"]+)"') {
                        $list.Add((Join-Path ($Matches[1].Replace('\\', '\')) 'steamapps\common\Valheim'))
                    }
                }
            }
        }
    }
    $seen = @{}
    foreach ($d in $list) {
        $k = Normalize-Dir $d
        if ($k -eq '' -or $seen.ContainsKey($k)) { continue }
        $seen[$k] = $true
        if (Test-Path -LiteralPath (Join-Path $d 'BepInEx')) { $d.TrimEnd('\') }
    }
}

function Get-DumperItems {
    foreach ($v in @(Get-ValheimDirs)) {
        $plugins = Join-Path $v 'BepInEx\plugins'
        if (Test-Path -LiteralPath $plugins) {
            foreach ($i in @(Get-ChildItem -LiteralPath $plugins -Filter '*SeedLabDumper*' -Force -ErrorAction SilentlyContinue)) { $i.FullName }
        }
        $cfg = Join-Path $v 'BepInEx\config\DoomMachine.SeedLabDumper.cfg'
        if (Test-Path -LiteralPath $cfg) { $cfg }
    }
}

# The folder a vseed process was given with --cache-dir, from its command line, or $null.
function Get-ArgCacheDir([string]$a) {
    if ($a -match '(?i)(^|\s)--cache-dir(=|\s+)("([^"]*)"|(\S+))') {
        if ($Matches[4]) { return $Matches[4] }
        return $Matches[5]
    }
    return $null
}

# Whether a running vseed uses one of the cache folders uninstall removes. One started with
# --cache-dir naming another folder does not (a test, a second setup), so it neither blocks the
# uninstall nor is stopped by it. Without --cache-dir, or when the folder cannot be told, it is
# taken to use them: that is the safe side.
function Test-UsesOurCache($p) {
    $d = Get-ArgCacheDir $p.Args
    if ($d -eq $null -or -not [IO.Path]::IsPathRooted($d)) { return $true }
    try { $n = Normalize-Dir ([IO.Path]::GetFullPath($d)) } catch { return $true }
    foreach ($r in @(Get-ServerRoots)) { if ((Normalize-Dir $r) -eq $n) { return $true } }
    return $false
}

# What uninstall would find to do. The "core" part is what "remove-build" requires to be done first.
function Get-UninstallWork {
    $w = New-Object psobject -Property @{
        Registered = @(); Servers = @(); Searches = @(); ForeignSearches = @(); Cache = $null; CacheSize = 0.0
        Chosen = $null; ChosenWhole = $false; ChosenParts = @(); Path = $null; Vars = @()
        Dumper = @(); DumperOut = $null
    }
    $all = @(Get-VseedProcesses | Where-Object { Test-UsesOurCache $_ })
    # Registered: the servers vseed serve --stop can stop. Servers: the ones it cannot (an older build,
    # or a server no cache folder here knows about), whose process is ended instead.
    $w.Servers = @($all | Where-Object { $_.IsServer })
    if (Test-ServeOption '--stop') {
        $rep = Get-ServeReport
        $w.Registered = @($rep.Servers)
        $w.Servers = @($rep.Unregistered | Where-Object { Test-UsesOurCache $_ })
    }
    $w.Searches = @($all | Where-Object { (-not $_.IsServer) -and $_.Ours })
    $w.ForeignSearches = @($all | Where-Object { (-not $_.IsServer) -and (-not $_.Ours) })
    $cache = Get-DefaultCacheRoot
    if (Test-Path -LiteralPath $cache) { $w.Cache = $cache; $w.CacheSize = Get-FolderSize $cache }
    $chosen = Get-ChosenCacheDir
    if ($chosen -ne $null -and (Normalize-Dir $chosen) -ne (Normalize-Dir $cache) -and (Test-Path -LiteralPath $chosen)) {
        $entries = @(Get-ChildItem -LiteralPath $chosen -Force -ErrorAction SilentlyContinue)
        $unknown = @($entries | Where-Object { -not (Test-SeedLabCacheEntry $_) })
        $known = @($entries | Where-Object { Test-SeedLabCacheEntry $_ })
        if ($unknown.Count -eq 0) { $w.Chosen = $chosen; $w.ChosenWhole = $true }
        elseif ($known.Count -gt 0) { $w.Chosen = $chosen; $w.ChosenWhole = $false; $w.ChosenParts = @($known | ForEach-Object { $_.FullName }) }
    }
    $w.Path = Get-PathReport
    $w.Vars = @(Get-UserSeedLabVars)
    $w.Dumper = @(Get-DumperItems)
    $out = Get-DumperOutputDir
    if (Test-Path -LiteralPath $out) { $w.DumperOut = $out }
    return $w
}

function Test-CoreUninstallPending($w) {
    return ($w.Cache -ne $null) -or ($w.Path.Ours.Count -gt 0) -or ($w.Path.Dangling.Count -gt 0) -or
           ($w.Registered.Count -gt 0) -or (@($w.Servers | Where-Object { $_.Ours }).Count -gt 0)
}

function Show-Kept {
    Say 'Kept on purpose:'
    Say '  - this SeedLab folder and its build ("SeedLab 6 - Remove the build" deletes the build);'
    Say '  - data\ (the game data) and your results: seedlab-results folders and any file you named'
    Say '    with --out;'
    Say '  - the .NET SDK, because other programs may use it. To remove it: Windows Settings > Apps,'
    Say '    "Microsoft .NET SDK 10...".'
}

function Do-Uninstall {
    if (-not (Assert-NotElevated)) { return 3 }
    Say-Title 'SeedLab: uninstall (everything except the SeedLab folder and its build)'
    $w = Get-UninstallWork
    $core = Test-CoreUninstallPending $w
    $extras = ($w.Chosen -ne $null) -or ($w.Vars.Count -gt 0) -or ($w.Dumper.Count -gt 0) -or
              ($w.DumperOut -ne $null) -or ($w.Searches.Count -gt 0) -or
              ((-not $TestMode) -and $w.ForeignSearches.Count -gt 0) -or ($w.Servers.Count -gt 0)
    if (-not $core -and -not $extras) {
        Say 'Nothing to uninstall: the vseed command is not registered for your account, there is no'
        Say ('SeedLab cache folder (' + (Get-DefaultCacheRoot) + '), and nothing of SeedLab''s is running.')
        Say ''
        Show-Kept
        return 0
    }

    if ($core) {
        Say 'This will:'
        $n = 1
        foreach ($s in $w.Registered) {
            Say ('  ' + $n + '. stop SeedLab''s web server at ' + $s.Url + ' (running since ' + $s.Since + ');'); $n++
            foreach ($q in @($s.Searches)) { Say ('     A search is running in it, ' + $q.Text + ': you are asked about that separately.') }
        }
        $oursServers = @($w.Servers | Where-Object { $_.Ours })
        if ($oursServers.Count -gt 0) {
            Say ('  ' + $n + '. end the process of SeedLab''s web server started from this folder that no cache folder'); $n++
            Say  '     here knows about (an older SeedLab, or one started with another cache folder): a search'
            Say  '     running in it loses what its last checkpoint did not hold;'
        }
        if ($w.Cache -ne $null) {
            Say ('  ' + $n + '. move SeedLab''s cache folder to the Recycle Bin (' + (Format-Size $w.CacheSize) + '):'); $n++
            Say ('       ' + $w.Cache)
            Say  '     It holds rendered maps, map tiles, search checkpoints (the resume points of searches'
            Say  '     you have not finished), vseed''s two session logs (logs\vseed.log and vseed-prev.log)'
            Say  '     and the self-test stamp. All of it is rebuilt when needed, except that an unfinished'
            Say  '     search can no longer be resumed, and the logs of the last two sessions are gone.'
        }
        if ($w.Path.Ours.Count -gt 0) {
            Say ('  ' + $n + '. remove the vseed command from your account: take this entry off your user Path:'); $n++
            Say ('       ' + $BuildDir)
        }
        if ($w.Path.Dangling.Count -gt 0) {
            Say ('  ' + $n + '. take off your Path the entries for SeedLab folders that no longer exist:'); $n++
            foreach ($d in $w.Path.Dangling) { Say ('       ' + $d) }
        }
        $later = @()
        if ($w.Searches.Count -gt 0) { $later += 'vseed programs running from this folder (a search): stop them?' }
        if ($w.Chosen -ne $null) { $later += ('the cache folder you chose with SEEDLAB_CACHE_DIR: ' + $w.Chosen) }
        if ($w.Vars.Count -gt 0) { $later += ('your SeedLab settings (' + (($w.Vars | ForEach-Object { $_.Name }) -join ', ') + ')') }
        if ($w.Dumper.Count -gt 0) { $later += 'SeedLab''s dumper plugin in Valheim' }
        if ($w.DumperOut -ne $null) { $later += ('the dumper''s output folder: ' + $w.DumperOut) }
        if ($later.Count -gt 0) {
            Say 'Then it asks you separately about:'
            foreach ($l in $later) { Say ('  - ' + $l) }
        }
        Say ''
        if (-not (Ask-YesNo 'Go ahead?' $false)) {
            Say 'Cancelled. Nothing has been changed.'
            return 1
        }
    } else {
        Say 'The vseed command is not registered and there is no SeedLab cache folder. What is left is'
        Say 'optional, and each thing is asked about on its own:'
    }

    # 1. Web servers. The ones vseed knows about are asked to stop through vseed serve --stop (a running
    #    search is stopped with its checkpoint saved); "Go ahead?" covers that, except for a running
    #    search, which is asked about here - its checkpoint goes to the Recycle Bin with the cache
    #    folder. The others have their process ended (Stop-ServersByProcess).
    $blocked = $false
    if ($w.Registered.Count -gt 0) {
        Say ''
        Say 'Stopping the web server:'
        $withSearch = @($w.Registered | Where-Object { @($_.Searches).Count -gt 0 })
        $go = $true
        if ($withSearch.Count -gt 0) {
            foreach ($s in $withSearch) { Show-RegisteredServer $s }
            Say 'Stopping SeedLab stops that search at once, with its checkpoint saved - but the checkpoint is'
            Say 'in the cache folder this uninstall moves to the Recycle Bin, so the search could not be'
            Say 'continued afterwards (unless you restore that folder). Answer n to leave it running: the'
            Say 'uninstall then leaves the cache folder where it is.'
            $go = Ask-YesNo 'Stop it anyway?' $false
            if (-not $go) { Say 'Left running.'; $blocked = $true }
        }
        if ($go) {
            $code = Stop-Registered $w.Registered ($withSearch.Count -gt 0)
            if ($code -ne 0) {
                $blocked = $true
                Say-Warn 'SeedLab''s web server is still running. Stop it with "SeedLab 3 - Stop web page" (it asks'
                Say-Warn 'about a running search), or in its own window, then run the uninstall again.'
            } elseif ((Get-ServeReport).Servers.Count -gt 0) {
                $blocked = $true
            }
        }
    }
    if ($w.Servers.Count -gt 0) {
        Say ''
        if ((Stop-ServersByProcess $w.Servers $true) -ne 0) { $blocked = $true }
        if (@(Get-VseedProcesses | Where-Object { $_.IsServer -and $_.Ours -and (Test-UsesOurCache $_) }).Count -gt 0) { $blocked = $true }
    }

    # 2. Other vseed programs (a search in a terminal).
    if ($w.Searches.Count -gt 0) {
        Say ''
        Say 'vseed is also running from this folder (a search, or another command):'
        Show-Processes $w.Searches
        Say 'Stopping it ends its process at once: a search loses what its last checkpoint did not hold -'
        Say 'and the checkpoints are in the cache folder this uninstall removes.'
        if (Ask-YesNo 'Stop it?' $false) {
            if (-not (Stop-Processes $w.Searches)) { $blocked = $true }
        } else { $blocked = $true }
    }
    if ($w.ForeignSearches.Count -gt 0) {
        Say ''
        Say 'vseed from ANOTHER SeedLab folder is running (it uses the same cache folder):'
        Show-Processes $w.ForeignSearches
        if ($TestMode) { Say '(test mode: programs from other folders are never offered)'; $blocked = $true }
        elseif (Ask-YesNo 'Stop it as well?' $false) {
            if (-not (Stop-Processes $w.ForeignSearches)) { $blocked = $true }
        } else { $blocked = $true }
    }

    # 3. The cache folder(s).
    if ($w.Cache -ne $null) {
        Say ''
        if ($blocked) {
            Say-Warn 'The cache folder was left in place, because vseed is still running and using it:'
            Say-Warn ('  ' + $w.Cache)
            Say-Warn 'Run the uninstall again once it has stopped.'
        } else {
            Say 'Removing the cache folder:'
            [void](Move-ToRecycleBin $w.Cache)
        }
    }
    if ($w.Chosen -ne $null) {
        Say ''
        Say 'SEEDLAB_CACHE_DIR tells SeedLab to keep its cache in a folder you chose:'
        Say ('  ' + $w.Chosen)
        if ($blocked) {
            Say-Warn 'Left in place, because vseed is still running.'
        } elseif ($w.ChosenWhole) {
            Say 'Everything in it is a folder with one of SeedLab''s names, holding only the kind of files SeedLab'
            Say 'writes there (session logs, checkpoints, map tiles and the like) - check it is not yours:'
            foreach ($e in @(Get-ChildItem -LiteralPath $w.Chosen -Force -ErrorAction SilentlyContinue)) { Say ('    ' + $e.FullName) }
            if (Ask-YesNo 'Move that whole folder to the Recycle Bin?' $false) { [void](Move-ToRecycleBin $w.Chosen) }
            else { Say 'Left in place.' }
        } else {
            Say 'It also holds things SeedLab did not make, so only these folders in it are offered. They have'
            Say 'SeedLab''s names and hold only the kind of files SeedLab writes there - check they are not yours:'
            foreach ($p in $w.ChosenParts) { Say ('    ' + $p) }
            Say 'Everything else in that folder is left alone.'
            if (Ask-YesNo 'Move those folders to the Recycle Bin?' $false) { foreach ($p in $w.ChosenParts) { [void](Move-ToRecycleBin $p) } }
            else { Say 'Left in place.' }
        }
    }

    # 4. The Path.
    $rep = Get-PathReport
    $drop = @($rep.Ours) + @($rep.Dangling)
    if ($drop.Count -gt 0) {
        Say ''
        Say 'Removing the vseed command from your account:'
        Set-UserPath $rep.Info (Remove-PathEntries $rep.Info.Raw $drop)
        foreach ($d in $drop) { Say ('  taken off your user Path: ' + $d) }
        Send-EnvironmentChange
        Say 'Windows that are already open still know vseed until they are closed.'
    }

    # 5. SEEDLAB_* settings.
    $vars = @(Get-UserSeedLabVars)
    if ($vars.Count -gt 0) {
        Say ''
        Say 'These SeedLab settings are set for your Windows account:'
        foreach ($v in $vars) { Say ('  ' + $v.Name + ' = ' + $v.Value) }
        if (Ask-YesNo 'Remove them?' $false) {
            foreach ($v in $vars) { Remove-UserEnvValue $v.Name; Say ('  removed ' + $v.Name) }
            Send-EnvironmentChange
        } else { Say 'Left as they are.' }
    }
    $machineVars = @()
    foreach ($k in ([Environment]::GetEnvironmentVariables('Machine')).Keys) { if ([string]$k -like 'SEEDLAB_*') { $machineVars += [string]$k } }
    if ($machineVars.Count -gt 0) {
        Say ''
        Say ('Set for ALL accounts on this PC: ' + ($machineVars -join ', '))
        Say 'This script does not change those; an administrator can, in System Properties >'
        Say 'Environment Variables.'
    }

    # 6. The dumper plugin in Valheim.
    $dumper = @(Get-DumperItems)
    if ($dumper.Count -gt 0) {
        Say ''
        Say 'SeedLab''s dumper plugin is installed in Valheim (it is only needed to read the game''s data'
        Say 'again after a Valheim update):'
        foreach ($d in $dumper) { Say ('  ' + $d) }
        if (Get-Process -Name 'valheim' -ErrorAction SilentlyContinue) {
            Say-Warn 'Valheim is running, so it was left in place. Quit Valheim and run the uninstall again.'
        } elseif (Ask-YesNo 'Move it to the Recycle Bin?' $false) {
            foreach ($d in $dumper) { [void](Move-ToRecycleBin $d) }
        } else { Say 'Left in place.' }
    }
    Say ''
    Say 'Not searched: mod-manager profiles (r2modman, Thunderstore Mod Manager and the like keep their'
    Say 'own BepInEx folders). If you installed the dumper through one, remove it there.'

    # 7. The dumper's output.
    $out = Get-DumperOutputDir
    if (Test-Path -LiteralPath $out) {
        Say ''
        Say 'The dumper''s output folder - the raw game data it wrote - is still here:'
        Say ('  ' + $out + '  (' + (Format-Size (Get-FolderSize $out)) + ')')
        Say 'SeedLab''s data\ folder holds a copy of what you imported from it. If you have deleted data\,'
        Say 'or have not imported the last run yet, this may be the only copy.'
        if (Ask-YesNo 'Move it to the Recycle Bin?' $false) { [void](Move-ToRecycleBin $out) } else { Say 'Left in place.' }
    }

    Say ''
    Show-Kept
    Say ''
    # Blocked: something of SeedLab's is still running, so what it uses was left in place. That is not
    # "finished", and "SeedLab 6 - Remove the build" must not go on as if it were (review of 2026-09-25).
    if ($blocked) {
        Say-Warn 'The uninstall did NOT finish: vseed is still running (see above), so the folders it uses were'
        Say-Warn 'left in place. Stop it - "SeedLab 3 - Stop web page", or in its own window - and run the'
        Say-Warn 'uninstall again.'
        return 1
    }
    Say-Good 'Uninstall finished.'
    return 0
}

# -------------------------------------------------------------------------------------------------
# remove-build
# -------------------------------------------------------------------------------------------------

function Get-BuildOutputDirs {
    $seen = @{}
    foreach ($top in @('src', 'tests', 'tools')) {
        $t = Join-Path $Repo $top
        if (-not (Test-Path -LiteralPath $t)) { continue }
        foreach ($proj in @(Get-ChildItem -LiteralPath $t -Recurse -Filter '*.csproj' -File -Force -ErrorAction SilentlyContinue)) {
            $rel = $proj.FullName.Substring($Repo.Length)
            if ($rel -match '\\(bin|obj|build)\\') { continue }
            foreach ($n in @('bin', 'obj')) {
                $d = Join-Path $proj.DirectoryName $n
                if ((Test-Path -LiteralPath $d -PathType Container) -and -not $seen.ContainsKey($d.ToLowerInvariant())) {
                    $seen[$d.ToLowerInvariant()] = $true
                    $d
                }
            }
        }
    }
    $dumperBuild = Join-Path $Repo 'tools\SeedLab.Dumper\build'
    if ((Test-Path -LiteralPath $dumperBuild -PathType Container) -and -not $seen.ContainsKey($dumperBuild.ToLowerInvariant())) { $dumperBuild }
}

function Do-RemoveBuild {
    if (-not (Assert-NotElevated)) { return 3 }
    Say-Title 'SeedLab: remove the build'
    $w = Get-UninstallWork
    if (Test-CoreUninstallPending $w) {
        Say 'Removing the build is the last step, and the uninstall has not been done yet: the vseed'
        Say 'command is still registered, or the cache folder is still there, or the web server is'
        Say 'running. Removing the build first would leave a vseed command that points at nothing.'
        if (-not (Ask-YesNo 'Run the uninstall first (it lists what it does and asks)?' $true)) {
            Say 'Nothing was removed.'
            return 1
        }
        $u = Do-Uninstall
        if ($u -ne 0 -or (Test-CoreUninstallPending (Get-UninstallWork))) {
            Say ''
            Say 'The uninstall did not finish, so the build was not removed.'
            return 1
        }
        Say-Title 'SeedLab: remove the build'
    }

    $running = @(Get-RepoProcesses)
    if ($running.Count -gt 0) {
        Say 'These programs are running from this SeedLab folder, and Windows will not delete the files'
        Say 'of a running program:'
        Show-Processes $running
        if (-not (Ask-YesNo 'Stop them?' $false)) { Say 'Nothing was removed.'; return 1 }
        if (-not (Stop-Processes $running)) { Say 'Nothing was removed.'; return 1 }
    }

    $dirs = @(Get-BuildOutputDirs)
    if ($dirs.Count -eq 0) {
        Say 'There is no build output in this folder. Nothing to remove.'
    } else {
        Say 'These build folders will be DELETED (not moved to the Recycle Bin: they are rebuilt exactly'
        Say 'by "SeedLab 1 - Install or update"):'
        $total = 0.0
        foreach ($d in $dirs) {
            $s = Get-FolderSize $d
            $total += $s
            Say ('  ' + $d.Substring($Repo.Length + 1) + '   ' + (Format-Size $s))
        }
        Say ('  total ' + (Format-Size $total))
        Say 'The dumper''s build (tools\SeedLab.Dumper\build, if listed) can only be rebuilt with Valheim'
        Say 'installed, because it is built against the game''s own files.'
        if (-not (Ask-YesNo 'Delete them?' $false)) { Say 'Nothing was removed.'; return 1 }
        $failed = 0
        foreach ($d in $dirs) {
            try { Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction Stop; Say ('  deleted ' + $d.Substring($Repo.Length + 1)) }
            catch { $failed++; Say-Bad ('  could not delete ' + $d + ': ' + $_.Exception.Message) }
        }
        if ($failed -gt 0) { Say-Bad 'Some folders could not be deleted (see above).'; return 1 }
        Say-Good 'The build is removed.'
    }
    Say ''
    Say 'To remove SeedLab completely, delete the SeedLab folder itself: close this window, then in'
    Say 'File Explorer right-click the folder and choose Delete. (A script cannot delete the folder it'
    Say 'runs from.) If you ran searches from it, save its seedlab-results folder first.'
    Say ('  ' + $Repo)
    return 0
}

# -------------------------------------------------------------------------------------------------
# help and the menu
# -------------------------------------------------------------------------------------------------

function Do-Help {
    Say 'SeedLab for Windows. Double-click SeedLab.bat for a menu, or one of the numbered files:'
    Say ''
    Say '  SeedLab 1 - Install or update       check the .NET 10 SDK, build SeedLab, make "vseed" a command'
    Say '  SeedLab 2 - Open web page           the map and the search in your browser'
    Say '  SeedLab 3 - Stop web page           stop SeedLab''s web server'
    Say '  SeedLab 4 - Command window          a PowerShell window in which vseed works'
    Say '  SeedLab 5 - Uninstall (keeps the build)'
    Say '                                      remove what SeedLab put outside its folder'
    Say '  SeedLab 6 - Remove the build        uninstall, then delete the build inside the folder'
    Say ''
    Say 'From a Command Prompt:  SeedLab.bat [install|web|stop|status|shell|uninstall|remove-build|help]'
    Say '  --no-pause              do not wait for Enter at the end (for scripts)'
    Say '  web <options>           passed on to "vseed serve", e.g. SeedLab.bat web --port 0'
    Say '  stop <options>          passed on to "vseed serve --stop", e.g. SeedLab.bat stop --yes (stop'
    Say '                          it even though a search is running; the search keeps its checkpoint)'
    Say ''
    Say 'Each action first does any earlier step that has not happened yet, and says so. Nothing is'
    Say 'removed or installed without asking. "stop" stops the web server at once unless a search is'
    Say 'running in it, when it asks first. docs\scripts.md explains it all.'
    return 0
}

function Show-Menu {
    while ($true) {
        Say ''
        Say 'SeedLab - Valheim world generation, offline'
        Say '==========================================='
        Say '  1  Install or update     check .NET, build SeedLab, make "vseed" a command'
        Say '  2  Open web page         the map and the search in your browser'
        Say '  3  Stop web page'
        Say '  4  Command window        a PowerShell window in which vseed works'
        Say '  5  Uninstall             everything except the SeedLab folder and its build'
        Say '  6  Remove the build      after uninstalling'
        Say '  S  Status                what is installed and what is running'
        Say '  H  Help'
        Say '  Q  Quit'
        $c = Read-Answer 'Type a number or letter, then press Enter: '
        if ($c -eq $null) { return 0 }
        $c = $c.Trim().ToLowerInvariant()
        $code = $null
        switch ($c) {
            '1' { $code = Do-Install }
            '2' { $code = Do-Web @() }
            '3' { $code = Do-Stop @() }
            '4' { $code = Do-Shell }
            '5' { $code = Do-Uninstall }
            '6' { $code = Do-RemoveBuild }
            's' { $code = Do-Status }
            'h' { $code = Do-Help }
            'q' { return 0 }
            ''  { continue }
            default { Say 'That is not one of the choices.' }
        }
        if ($code -ne $null) {
            Say ''
            if ((Read-Answer 'Press Enter to go back to the menu. ') -eq $null) { return $code }
        }
    }
}

# -------------------------------------------------------------------------------------------------
# main
# -------------------------------------------------------------------------------------------------

function Main {
    $action = $null
    $noPause = $false
    $extra = New-Object System.Collections.Generic.List[string]
    foreach ($a in $ScriptArgs) {
        $s = [string]$a
        if ($s -ieq '--no-pause') { $noPause = $true; $script:NoPause = $true; continue }
        if ($action -eq $null -and ($s -ieq '--help' -or $s -ieq '-h' -or $s -eq '/?')) { $action = 'help'; continue }
        if ($action -eq $null -and -not $s.StartsWith('-')) { $action = $s.ToLowerInvariant(); continue }
        $extra.Add($s)
    }

    if ($TestMode) {
        Say ('[test mode: ' + ((@(Get-ChildItem Env: | Where-Object { $_.Name -like 'SEEDLAB_SCRIPT_TEST_*' }) | ForEach-Object { $_.Name }) -join ', ') + ']')
        if ((TestVar 'SEEDLAB_SCRIPT_TEST_ENVKEY') -eq $null -or (TestVar 'SEEDLAB_SCRIPT_TEST_CACHE') -eq $null) {
            Say-Bad 'Test mode needs both SEEDLAB_SCRIPT_TEST_ENVKEY and SEEDLAB_SCRIPT_TEST_CACHE; refusing to touch the real ones.'
            return 2
        }
        [void](Get-EnvKeyName)
    }

    $code = 0
    # serve-window is internal: the window 'web' opens for the server (see Do-Web).
    $known = @('install', 'update', 'web', 'stop', 'status', 'shell', 'uninstall', 'remove-build', 'help', 'menu', 'serve-window')
    if ($action -ne $null -and $known -notcontains $action) {
        Say-Bad ('Unknown action: ' + $action)
        [void](Do-Help)
        $code = 2
    } elseif ($extra.Count -gt 0 -and $action -ne 'web' -and $action -ne 'stop' -and $action -ne 'serve-window') {
        Say-Bad ('Unexpected: ' + ($extra -join ' ') + '   (only "web" and "stop" take options, which go to vseed)')
        $code = 2
    } elseif ($action -eq $null -or $action -eq 'menu') {
        # The menu has its own "Press Enter to go back to the menu" and its own Quit.
        if (-not (Assert-NotElevated)) { $code = 3 } else { $code = Show-Menu; $noPause = $true }
    } else {
        switch ($action) {
            'install'      { $code = Do-Install }
            'update'       { $code = Do-Install }
            'web'          { $code = Do-Web $extra.ToArray() }
            'serve-window' { $code = Start-ServerHere $extra.ToArray() }
            'stop'         { $code = Do-Stop $extra.ToArray() }
            'status'       { $code = Do-Status }
            'shell'        { $code = Do-Shell }
            'uninstall'    { $code = Do-Uninstall }
            'remove-build' { $code = Do-RemoveBuild }
            'help'         { $code = Do-Help }
        }
    }
    if (-not $noPause -and -not $script:SkipFinalPause) {
        Say ''
        [void](Read-Answer 'Press Enter to close this window. ')
    }
    return $code
}

try {
    $exitCode = Main
} catch {
    Say ''
    Say-Bad ('Something went wrong: ' + $_.Exception.Message)
    Say-Bad ('  at ' + $_.InvocationInfo.PositionMessage)
    Say 'Nothing after this point was done. Please report this, with the lines above.'
    if (@($ScriptArgs | Where-Object { [string]$_ -ieq '--no-pause' }).Count -eq 0) { [void](Read-Answer 'Press Enter to close this window. ') }
    $exitCode = 4
}
if ($exitCode -is [array]) { $exitCode = $exitCode[-1] }
if ($exitCode -eq $null) { $exitCode = 0 }
exit ([int]$exitCode)
