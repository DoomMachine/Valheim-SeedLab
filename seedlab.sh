#!/bin/sh
# SeedLab for macOS and Linux: install, run, stop and remove SeedLab.
#
#   sh seedlab.sh                       a numbered menu
#   sh seedlab.sh install               check the .NET 10 SDK, build SeedLab, make "vseed" a command
#   sh seedlab.sh web [vseed options]   start SeedLab's web server (or open the page if it runs)
#   sh seedlab.sh stop                  stop the web server
#   sh seedlab.sh status                what is installed and what is running
#   sh seedlab.sh shell                 a shell in the SeedLab folder in which vseed works
#   sh seedlab.sh uninstall             remove what SeedLab put outside its folder; keeps the build
#   sh seedlab.sh remove-build          uninstall, then delete the build inside the SeedLab folder
#   sh seedlab.sh help
#
#   --no-pause   never wait for Enter (for scripts and tests)
#
# On a Mac, double-clicking SeedLab.command in Finder runs this script in Terminal.
# docs/scripts.md explains every action in plain words.
#
# NOT TESTED ON macOS: no Mac was available. Linux is to be tested in WSL. Until then, treat this
# script as a careful first version; docs/scripts.md lists the manual steps it automates.
#
# Rules this script keeps:
#   - Every action first does whatever earlier step has not happened yet, and says so.
#   - It refuses to run as root, whatever the action, and never runs sudo. The only step that can
#     need administrator rights is installing Microsoft's .NET SDK system-wide, which you do yourself
#     if you choose it.
#   - It asks before it removes anything or installs anything, and before it stops a running search
#     or any program other than SeedLab's web server. "stop" stops the web server at once when no
#     search is running in it - stopping it is what was asked for - and asks first when one is. When
#     it cannot ask (no keyboard) it takes the safe answer, which is always "no".
#   - It only ever stops programs started from THIS SeedLab folder's build.
#
# POSIX sh only (it must run under dash, Debian's and Ubuntu's /bin/sh, and macOS's /bin/sh): no
# bash features. Keep this file LF and ASCII.
#
# SeedLab's web server is stopped the way vseed itself provides (vseed serve --stop, since
# 2026-09-24): a running search is then stopped with its checkpoint saved. Sending SIGTERM to a
# process is kept for what vseed cannot reach: an older build, or a server no cache folder here
# knows about.
#
# Test hooks (environment variables, never needed in normal use). When any SEEDLAB_SCRIPT_TEST_*
# variable is set the script runs in test mode and refuses to start unless SEEDLAB_SCRIPT_TEST_HOME
# equals $HOME (so a test cannot touch a real home folder) and SEEDLAB_SCRIPT_TEST_CACHE is set.
# It also sets SEEDLAB_CACHE_DIR to SEEDLAB_SCRIPT_TEST_CACHE when a test has not set it, so that
# every vseed it starts keeps its cache, its logs and its server file in the scratch folder too.
#   SEEDLAB_SCRIPT_TEST_HOME         must equal $HOME: a scratch folder standing in for it
#   SEEDLAB_SCRIPT_TEST_CACHE        folder used instead of the usual cache folder
#   SEEDLAB_SCRIPT_TEST_ANSWERS      answers to the questions, in order, separated by |
#   SEEDLAB_SCRIPT_TEST_OS           linux or macos, instead of asking uname
#   SEEDLAB_SCRIPT_TEST_UID          a user id instead of `id -u` (0 = behave as root)
#   SEEDLAB_SCRIPT_TEST_DOTNET_DIRS  folders (separated by :) searched for dotnet instead of the
#                                    usual places
#   SEEDLAB_SCRIPT_TEST_PS_FILE      a file of "pid|exe|arguments" lines used instead of
#                                    the list of running programs
#   SEEDLAB_SCRIPT_TEST_NO_WINDOWS   1 = print addresses instead of opening a browser (vseed serve
#                                    is given --no-browser), and do not start an interactive shell

# Fields of the process lists are separated by |, not a tab: a tab is IFS white space, so an empty
# field (a program whose path could not be told) would silently vanish when the line is read.

# ------------------------------------------------------------------------------------------------
# Output and questions
# ------------------------------------------------------------------------------------------------

say() { printf '%s\n' "$*"; }
title() {
    printf '\n%s\n' "$1"
    printf '%s\n' "$1" | sed 's/./-/g'
}

NO_PAUSE=
ANSWERS_LEFT=${SEEDLAB_SCRIPT_TEST_ANSWERS-}
ANSWERS_DONE=
if [ -z "${SEEDLAB_SCRIPT_TEST_ANSWERS+x}" ]; then ANSWERS_DONE=none; fi

# Reads one answer into ANSWER. Returns 1 when there is nobody to ask: callers then take the safe
# answer.
read_answer() {
    if [ "$ANSWERS_DONE" != none ]; then
        if [ "$ANSWERS_DONE" = yes ]; then
            printf '%s(no test answer left: taking the safe answer)\n' "$1"
            return 1
        fi
        case $ANSWERS_LEFT in
            *'|'*) ANSWER=${ANSWERS_LEFT%%|*}; ANSWERS_LEFT=${ANSWERS_LEFT#*|} ;;
            *) ANSWER=$ANSWERS_LEFT; ANSWERS_DONE=yes ;;
        esac
        printf '%s%s   (test answer)\n' "$1" "$ANSWER"
        return 0
    fi
    printf '%s' "$1"
    if IFS= read -r ANSWER; then
        if [ ! -t 0 ]; then printf '%s\n' "$ANSWER"; fi
        return 0
    fi
    ANSWER=
    printf '(no keyboard input: taking the safe answer)\n'
    return 1
}

# ask_yes_no "question" y|n  -> status 0 for yes. The second argument is what Enter means.
ask_yes_no() {
    ayn_hint=' [y/N] '
    if [ "$2" = y ]; then ayn_hint=' [Y/n] '; fi
    ayn_i=0
    while [ $ayn_i -lt 3 ]; do
        ayn_i=$((ayn_i + 1))
        read_answer "$1$ayn_hint" || return 1
        case $ANSWER in
            '') [ "$2" = y ]; return ;;
            y|Y|yes|YES|Yes) return 0 ;;
            n|N|no|NO|No) return 1 ;;
        esac
        say 'Please answer y (yes) or n (no).'
    done
    return 1
}

# ask_choice "question" "P B C" -> CHOICE set to one of the letters (upper case), status 1 if none.
ask_choice() {
    ac_i=0
    while [ $ac_i -lt 3 ]; do
        ac_i=$((ac_i + 1))
        read_answer "$1 [$(printf '%s' "$2" | sed 's# #/#g')] " || return 1
        CHOICE=$(printf '%s' "$ANSWER" | tr '[:lower:]' '[:upper:]' | sed 's/^ *//; s/ *$//')
        for ac_l in $2; do
            if [ "$CHOICE" = "$ac_l" ]; then return 0; fi
        done
        say "Please type one of: $2."
    done
    return 1
}

format_kb() {
    awk -v k="$1" 'BEGIN { if (k >= 1048576) printf "%.1f GB", k / 1048576; else if (k >= 1024) printf "%.1f MB", k / 1024; else printf "%d KB", k }'
}

size_of() {
    sz=$(du -sk "$1" 2>/dev/null | awk '{ print $1 }')
    format_kb "${sz:-0}"
}

# Quotes a string for a POSIX shell script: 'it'"'"'s'
shell_quote() {
    printf "'%s'" "$(printf '%s' "$1" | sed "s/'/'\"'\"'/g")"
}

# ------------------------------------------------------------------------------------------------
# Where things are
# ------------------------------------------------------------------------------------------------

case $0 in
    */*) SCRIPT_DIR=${0%/*} ;;
    *) SCRIPT_DIR=. ;;
esac
REPO=$(cd "$SCRIPT_DIR" 2>/dev/null && pwd -P)
if [ -z "$REPO" ] || [ ! -f "$REPO/src/SeedLab.Cli/SeedLab.Cli.csproj" ]; then
    say 'This script must stay where it came with SeedLab: in the SeedLab folder itself, next to src/.'
    exit 2
fi
CLI_PROJECT=$REPO/src/SeedLab.Cli/SeedLab.Cli.csproj
BUILD_DIR=$REPO/src/SeedLab.Cli/bin/Release/net10.0
STAMP_FILE=$BUILD_DIR/seedlab-build-unix.stamp
SDK_PAGE=https://dotnet.microsoft.com/download/dotnet/10.0
INSTALL_SCRIPT_URL=https://dot.net/v1/dotnet-install.sh
LAUNCHER_DIR=$HOME/.local/bin
LAUNCHER=$LAUNCHER_DIR/vseed
LAUNCHER_MARK='# SeedLab: the vseed command.'
BLOCK_BEGIN='# >>> SeedLab >>>'
BLOCK_END='# <<< SeedLab <<<'

OS=${SEEDLAB_SCRIPT_TEST_OS-}
if [ -z "$OS" ]; then
    case $(uname -s 2>/dev/null) in
        Darwin) OS=macos ;;
        Linux) OS=linux ;;
        *) OS=other ;;
    esac
fi

TEST_MODE=
if env | grep '^SEEDLAB_SCRIPT_TEST_' >/dev/null 2>&1; then TEST_MODE=1; fi
CACHE_DIR_SET_BY_SCRIPT=
if [ -n "$TEST_MODE" ] && [ -z "${SEEDLAB_CACHE_DIR-}" ] && [ -n "${SEEDLAB_SCRIPT_TEST_CACHE-}" ]; then
    # Every vseed this script starts inherits it: a test never reaches the real cache folder.
    SEEDLAB_CACHE_DIR=$SEEDLAB_SCRIPT_TEST_CACHE
    export SEEDLAB_CACHE_DIR
    CACHE_DIR_SET_BY_SCRIPT=1
fi

default_cache_root() {
    if [ -n "${SEEDLAB_SCRIPT_TEST_CACHE-}" ]; then printf '%s' "$SEEDLAB_SCRIPT_TEST_CACHE"; return; fi
    if [ "$OS" = macos ]; then printf '%s' "$HOME/Library/Caches/SeedLab"; return; fi
    printf '%s' "${XDG_CACHE_HOME:-$HOME/.cache}/seedlab"
}

# The cache folder a vseed started by this script uses when it is given no --cache-dir: vseed's own
# rule, SEEDLAB_CACHE_DIR if it is set, otherwise the usual one. (In test mode the script has set
# SEEDLAB_CACHE_DIR to the scratch folder.)
vseed_default_root() {
    if [ -n "${SEEDLAB_CACHE_DIR-}" ]; then printf '%s' "${SEEDLAB_CACHE_DIR%/}"; else default_cache_root; fi
}

# The vseed program in this folder's build ($BUILD_DIR/vseed), or nothing.
vseed_exe() {
    if [ -f "$BUILD_DIR/vseed" ]; then printf '%s' "$BUILD_DIR/vseed"
    elif [ -f "$BUILD_DIR/vseed.exe" ]; then printf '%s' "$BUILD_DIR/vseed.exe"   # only when tested under Windows
    fi
}

is_arm() {
    case $(uname -m 2>/dev/null) in
        arm64|aarch64|arm*) return 0 ;;
    esac
    return 1
}

platform_note() {
    if is_arm; then
        say ''
        say 'This computer has an ARM processor (an Apple Silicon Mac, or ARM Linux). SeedLab has only'
        say 'ever been proven on x64 processors. On any other kind, vseed can only prove its arithmetic'
        say 'with the ground-truth recordings (groundtruth/natives), which are not published, so it'
        say 'refuses to answer unless you add --accept-unverified-platform - and then its answers may'
        say 'differ from the game'"'"'s.'
    fi
}

untested_note() {
    if [ "$OS" = macos ]; then
        say 'Note: this script has NOT been tested on macOS - no Mac was available. If a step here fails,'
        say 'docs/scripts.md lists the manual steps it automates.'
    else
        say 'Note: this script has not yet been tested on a real Linux system (that is planned, in WSL).'
        say 'If a step here fails, docs/scripts.md lists the manual steps it automates.'
    fi
}

refuse_root() {
    ur_uid=${SEEDLAB_SCRIPT_TEST_UID-}
    if [ -z "$ur_uid" ]; then ur_uid=$(id -u); fi
    if [ "$ur_uid" != 0 ]; then return 0; fi
    say ''
    say 'Stop: this is running as root (the superuser), and SeedLab refuses to run that way.'
    say ''
    say 'As root, the vseed command and SeedLab'"'"'s files would go into root'"'"'s home folder instead of'
    say 'yours, and files made by root in the SeedLab folder would later get in your way. Nothing'
    say 'SeedLab does needs root. The only step that can need administrator rights is installing'
    say 'Microsoft'"'"'s .NET SDK for the whole system - and that you do yourself, if you choose it; this'
    say 'script never runs sudo.'
    say ''
    say 'Run it again as yourself, without sudo:  sh seedlab.sh'
    return 1
}

# ------------------------------------------------------------------------------------------------
# Running programs
# ------------------------------------------------------------------------------------------------

# Every running vseed as "pid|exe|arguments". exe is the program's real path when the system
# says (Linux: /proc/<pid>/exe; macOS: ps comm), else empty.
vseed_procs() {
    if [ -n "${SEEDLAB_SCRIPT_TEST_PS_FILE-}" ]; then
        cat "$SEEDLAB_SCRIPT_TEST_PS_FILE"
        return 0
    fi
    ps -A -o pid= -o args= 2>/dev/null | while read -r vp_pid vp_args; do
        case $vp_args in
            "$BUILD_DIR/vseed"|"$BUILD_DIR/vseed "*) vp_rest=${vp_args#"$BUILD_DIR/vseed"} ;;
            */vseed|*/vseed\ *|vseed|vseed\ *) vp_rest=${vp_args#*vseed} ;;
            *) continue ;;
        esac
        vp_exe=
        if [ -e "/proc/$vp_pid/exe" ]; then vp_exe=$(readlink "/proc/$vp_pid/exe" 2>/dev/null); fi
        if [ -z "$vp_exe" ] && [ "$OS" = macos ]; then vp_exe=$(ps -p "$vp_pid" -o comm= 2>/dev/null); fi
        if [ -z "$vp_exe" ]; then
            case $vp_args in /*) vp_exe=${vp_args%"$vp_rest"} ;; esac
        fi
        printf '%s|%s|%s\n' "$vp_pid" "$vp_exe" "${vp_rest# }"
    done
}

# Filters vseed_procs output. $1: servers|others; $2: ours|foreign|any
filter_procs() {
    while IFS='|' read -r fp_pid fp_exe fp_args; do
        [ -n "$fp_pid" ] || continue
        fp_server=no
        case " $fp_args " in
            *' serve '*)
                case " $fp_args " in
                    *' --selftest'*|*' --status'*|*' --stop'*|*' --help'*|*' -h '*) ;;
                    *) fp_server=yes ;;
                esac ;;
        esac
        fp_ours=no
        if [ "$fp_exe" = "$BUILD_DIR/vseed" ] || [ "$fp_exe" = "$BUILD_DIR/vseed.exe" ]; then fp_ours=yes; fi
        case $1 in
            servers) [ $fp_server = yes ] || continue ;;
            others) [ $fp_server = no ] || continue ;;
        esac
        case $2 in
            ours) [ $fp_ours = yes ] || continue ;;
            foreign) [ $fp_ours = no ] || continue ;;
        esac
        printf '%s|%s|%s\n' "$fp_pid" "$fp_exe" "$fp_args"
    done
}

show_procs() {
    while IFS='|' read -r sp_pid sp_exe sp_args; do
        [ -n "$sp_pid" ] || continue
        say "    process $sp_pid: vseed $sp_args"
        if [ -n "$sp_exe" ]; then say "      from ${sp_exe%/*}"; fi
    done
}

# Stops the processes listed on stdin ("pid|..."): SIGTERM, which vseed answers by shutting down
# cleanly, then up to 15 seconds' wait.
stop_procs() {
    stp_ok=0
    stp_pids=$(cut -d'|' -f1)
    for stp_pid in $stp_pids; do
        if kill -TERM "$stp_pid" 2>/dev/null; then
            stp_n=0
            while kill -0 "$stp_pid" 2>/dev/null && [ $stp_n -lt 15 ]; do sleep 1; stp_n=$((stp_n + 1)); done
            if kill -0 "$stp_pid" 2>/dev/null; then
                say "  process $stp_pid did not stop within 15 seconds."
                stp_ok=1
            else
                say "  stopped process $stp_pid"
            fi
        else
            if kill -0 "$stp_pid" 2>/dev/null; then say "  could not stop process $stp_pid"; stp_ok=1
            else say "  process $stp_pid had already stopped"; fi
        fi
    done
    return $stp_ok
}

# Every running program whose executable lives inside this SeedLab folder.
repo_procs() {
    if [ -n "${SEEDLAB_SCRIPT_TEST_PS_FILE-}" ]; then
        while IFS='|' read -r rp_pid rp_exe rp_args; do
            case $rp_exe in "$REPO"/*) printf '%s|%s|%s\n' "$rp_pid" "$rp_exe" "$rp_args" ;; esac
        done < "$SEEDLAB_SCRIPT_TEST_PS_FILE"
        return 0
    fi
    if [ "$OS" = linux ]; then
        for rp_d in /proc/[0-9]*; do
            rp_exe=$(readlink "$rp_d/exe" 2>/dev/null) || continue
            case $rp_exe in "$REPO"/*) printf '%s|%s|\n' "${rp_d#/proc/}" "$rp_exe" ;; esac
        done
    else
        ps -A -o pid= -o comm= 2>/dev/null | while read -r rp_pid rp_comm; do
            case $rp_comm in "$REPO"/*) printf '%s|%s|\n' "$rp_pid" "$rp_comm" ;; esac
        done
    fi
}

# Whether this build's 'vseed serve' knows an option (from 'vseed serve --help', which prints its text
# and exits before vseed starts any real work).
SERVE_HELP=
SERVE_HELP_READ=
serve_has() {
    sh_exe=$(vseed_exe)
    [ -n "$sh_exe" ] || return 1
    if [ -z "$SERVE_HELP_READ" ]; then
        SERVE_HELP=$(cd "$REPO" && run_vseed serve --help 2>&1 </dev/null)
        SERVE_HELP_READ=1
    fi
    printf '%s\n' "$SERVE_HELP" | grep -e "$1 " -e "$1=" -e "$1\$" >/dev/null 2>&1
}

# Asks a local port whether SeedLab's page is there (proxy bypassed).
seedlab_at() {
    [ "$1" -gt 0 ] 2>/dev/null || return 1
    if command -v curl >/dev/null 2>&1; then
        curl -s --noproxy '*' --max-time 3 "http://127.0.0.1:$1/" 2>/dev/null | grep '<title>SeedLab</title>' >/dev/null
        return
    fi
    if command -v wget >/dev/null 2>&1; then
        wget -q -O - --no-proxy -T 3 "http://127.0.0.1:$1/" 2>/dev/null | grep '<title>SeedLab</title>' >/dev/null
        return
    fi
    return 1
}

# Only two kinds of address are ever opened: SeedLab's own page on this computer, and Microsoft's pages
# for the SDK. open and xdg-open open whatever they are given, and a server's address comes from vseed
# reading a file in the cache folder (review of 2026-09-25).
open_url() {
    case $1 in
        http://127.0.0.1:[0-9]|http://127.0.0.1:[0-9][0-9]|http://127.0.0.1:[0-9][0-9][0-9]|http://127.0.0.1:[0-9][0-9][0-9][0-9]|http://127.0.0.1:[0-9][0-9][0-9][0-9][0-9]) ;;
        https://dotnet.microsoft.com/*|https://learn.microsoft.com/*) ;;
        *) say "Not opened, because it is not an address of SeedLab's page: $1"; return 1 ;;
    esac
    if [ "${SEEDLAB_SCRIPT_TEST_NO_WINDOWS-}" = 1 ]; then say "  (test mode: would open $1 in the browser)"; return; fi
    if [ "$OS" = macos ] && command -v open >/dev/null 2>&1; then open "$1" && return; fi
    if command -v xdg-open >/dev/null 2>&1; then xdg-open "$1" >/dev/null 2>&1 && return; fi
    say "Open this address in your browser: $1"
}

# The address of SeedLab's page served by one of these processes ("pid|exe|arguments" lines), found
# by asking the port each one was given. For servers vseed cannot report on.
page_of() {
    po_ports=$(printf '%s\n' "$1" | sed -n 's/.*--port[= ]\([0-9][0-9]*\).*/\1/p')
    if printf '%s\n' "$1" | grep . | grep -v -e '--port' >/dev/null; then po_ports="$po_ports 8731"; fi
    for po_p in $po_ports; do
        if seedlab_at "$po_p"; then printf '%s' "http://127.0.0.1:$po_p"; return 0; fi
    done
    return 1
}

# ------------------------------------------------------------------------------------------------
# SeedLab's web server, as vseed reports it (vseed serve --status and --stop, since 2026-09-24)
#
# A running server registers itself in the cache folder it was started with, so "is it running" is
# asked of each cache folder a server of this account can be in: the one vseed uses by itself, the
# usual one, and SEEDLAB_CACHE_DIR's. --status and --stop start nothing and create nothing - not even
# the cache folder - so they are safe to ask at any time, including right after an uninstall.
# ------------------------------------------------------------------------------------------------

serve_roots() {
    {
        vseed_default_root; printf '\n'
        default_cache_root; printf '\n'
        if [ -n "${SEEDLAB_CACHE_DIR-}" ]; then printf '%s\n' "${SEEDLAB_CACHE_DIR%/}"; fi
    } | awk 'NF && !seen[$0]++'
}

# vseed wraps a long line and continues it with two spaces; this joins such lines back into one.
unwrap_lines() {
    awk 'NR > 1 && /^  / { sub(/^ +/, " "); line = line $0; next } { if (NR > 1) print line; line = $0 } END { if (NR > 0) print line }'
}

# Asks every cache folder above. Sets
#   SRV_REG       one line per server: root|pid|url|port|search (yes or no)|vseed's own line about it
#   SRV_UNREG     the vseed web servers none of them knows about ("pid|exe|arguments", process list)
#   SRV_PROBLEMS  the folders vseed could not answer for
#   SRV_LEFTOVER  what vseed said about registry files that name no running server (review of
#                 2026-09-25): its own paragraph and the paths, to be shown as they are
serve_report() {
    SRV_REG=; SRV_UNREG=; SRV_PROBLEMS=; SRV_LEFTOVER=
    sr_seen=
    sr_roots=$(serve_roots)
    while IFS= read -r sr_root; do
        [ -n "$sr_root" ] || continue
        sr_out=$(cd "$REPO" && run_vseed serve --status --cache-dir "$sr_root" 2>&1 </dev/null)
        sr_code=$?
        if [ $sr_code -eq 0 ] || [ $sr_code -eq 1 ]; then
            sr_left=$(printf '%s\n' "$sr_out" | awk '/serve folder name/ { p = 1 } p && /^$/ { exit } p { print }')
            if [ -n "$sr_left" ]; then SRV_LEFTOVER="$SRV_LEFTOVER$sr_left
"; fi
        fi
        if [ $sr_code -eq 1 ]; then continue; fi
        if [ $sr_code -ne 0 ]; then
            SRV_PROBLEMS="$SRV_PROBLEMS$sr_root: vseed serve --status ended with exit code $sr_code
"
            continue
        fi
        sr_lines=$(printf '%s\n' "$sr_out" | unwrap_lines | grep "^SeedLab's web server is running at ")
        while IFS= read -r sr_l; do
            [ -n "$sr_l" ] || continue
            sr_url=$(printf '%s\n' "$sr_l" | sed -n 's#^SeedLab'"'"'s web server is running at \(http://127\.0\.0\.1:[0-9][0-9]*\).*#\1#p')
            sr_pid=$(printf '%s\n' "$sr_l" | sed -n 's/.*(pid \([0-9][0-9]*\)).*/\1/p')
            case $sr_l in
                *'; a search is running:'*|*'searches are running:'*) sr_search=yes ;;
                *) sr_search=no ;;
            esac
            # One server is listed once, even if two spellings of a folder both lead to it.
            case " $sr_seen " in *" $sr_pid "*) continue ;; esac
            sr_seen="$sr_seen $sr_pid"
            SRV_REG="$SRV_REG$sr_root|$sr_pid|$sr_url|${sr_url##*:}|$sr_search|$sr_l
"
        done <<EOF
$sr_lines
EOF
    done <<EOF
$sr_roots
EOF
    SRV_UNREG=$(vseed_procs | filter_procs servers any | while IFS='|' read -r su_pid su_rest; do
        case " $sr_seen " in *" $su_pid "*) continue ;; esac
        printf '%s|%s\n' "$su_pid" "$su_rest"
    done)
}

show_leftover() {
    [ -n "$SRV_LEFTOVER" ] || return 0
    say ''
    printf '%s' "$SRV_LEFTOVER"
}

# $1 = also: a registered server was just shown, so this one is "also" running; it: the line above is
# about this very server; otherwise the lines above said none is running, and this is the exception
# to that ("But").
show_unregistered() {
    [ -n "$SRV_UNREG" ] || return 0
    say ''
    case ${1-} in
        also) say 'A vseed web server is also running that no SeedLab cache folder here knows about:' ;;
        it) say 'It is a vseed web server that no SeedLab cache folder here knows about:' ;;
        *) say 'But a vseed web server IS running that no SeedLab cache folder here knows about:' ;;
    esac
    printf '%s\n' "$SRV_UNREG" | show_procs
    say 'It was started with another cache folder - then "sh seedlab.sh stop --cache-dir <that folder>"'
    say 'stops it - or by an older SeedLab. Otherwise stop it in its own terminal: press Ctrl+C twice'
    say 'there.'
}

# Stops the servers in these SRV_REG lines through vseed, one cache folder at a time. vseed stops a
# running search at once with its checkpoint saved; when one is running it asks first - unless $2 is
# "yes" (the user has already said yes here, so it gets --yes). Returns vseed's exit status.
stop_registered() {
    sreg_result=0
    sreg_roots=$(printf '%s' "$1" | cut -d'|' -f1 | awk 'NF && !seen[$0]++')
    while IFS= read -r sreg_root; do
        [ -n "$sreg_root" ] || continue
        if [ "$2" = yes ]; then
            (cd "$REPO" && run_vseed serve --stop --cache-dir "$sreg_root" --yes)
        else
            (cd "$REPO" && run_vseed serve --stop --cache-dir "$sreg_root")
        fi
        sreg_code=$?
        if [ $sreg_code -ne 0 ]; then sreg_result=$sreg_code; fi
    done <<EOF
$sreg_roots
EOF
    return $sreg_result
}

# The value of an option in the arguments that follow the name ("--port 0" or "--port=0").
opt_value() {
    ov_name=$1
    shift
    while [ $# -gt 0 ]; do
        case $1 in
            "$ov_name") if [ $# -gt 1 ]; then printf '%s' "$2"; return 0; fi ;;
            "$ov_name"=*) printf '%s' "${1#*=}"; return 0 ;;
        esac
        shift
    done
    return 1
}

# Is a web server running? Sets SRV_STATE (running|stopped|unknown), SRV_URL, SRV_LIST (the processes
# of servers vseed cannot report on), and through serve_report SRV_REG, SRV_UNREG and SRV_PROBLEMS.
server_state() {
    SRV_STATE=stopped; SRV_URL=; SRV_LIST=; SRV_REG=; SRV_UNREG=; SRV_PROBLEMS=; SRV_LEFTOVER=
    if serve_has --status; then
        serve_report
        if [ -n "$SRV_REG" ]; then
            SRV_STATE=running
            SRV_URL=$(printf '%s' "$SRV_REG" | head -n 1 | cut -d'|' -f3)
            return 0
        fi
        SRV_LIST=$SRV_UNREG
        if [ -n "$SRV_UNREG" ]; then
            SRV_STATE=running
            SRV_URL=$(page_of "$SRV_UNREG")
            return 0
        fi
        if [ -n "$SRV_PROBLEMS" ]; then SRV_STATE=unknown; fi
        return 0
    fi
    # A build from before vseed serve --status: the list of running programs.
    SRV_LIST=$(vseed_procs | filter_procs servers any)
    [ -n "$SRV_LIST" ] || return 0
    SRV_STATE=running
    SRV_URL=$(page_of "$SRV_LIST")
}

# ------------------------------------------------------------------------------------------------
# Step 1: the .NET 10 SDK
# ------------------------------------------------------------------------------------------------

dotnet_candidates() {
    if [ -n "${SEEDLAB_SCRIPT_TEST_DOTNET_DIRS+x}" ]; then
        printf '%s\n' "$SEEDLAB_SCRIPT_TEST_DOTNET_DIRS" | tr ':' '\n' | while IFS= read -r dc_d; do
            [ -n "$dc_d" ] || continue
            printf '%s\n' "$dc_d/dotnet" "$dc_d/dotnet.exe"
        done
        return 0
    fi
    command -v dotnet 2>/dev/null
    if [ -n "${DOTNET_ROOT-}" ]; then printf '%s\n' "$DOTNET_ROOT/dotnet"; fi
    printf '%s\n' "$HOME/.dotnet/dotnet" /usr/local/share/dotnet/dotnet /usr/local/share/dotnet/x64/dotnet \
        /opt/homebrew/bin/dotnet /usr/local/bin/dotnet /usr/share/dotnet/dotnet /usr/lib/dotnet/dotnet \
        /usr/lib64/dotnet/dotnet /snap/bin/dotnet
}

# Sets DOTNET (the program) and SDK_VERSION when a 10.x SDK is found; OTHER_SDKS lists the rest.
find_sdk() {
    DOTNET=; SDK_VERSION=; OTHER_SDKS=; ASPNET10=
    fs_list=$(dotnet_candidates)
    while IFS= read -r fs_c; do
        [ -n "$fs_c" ] && [ -f "$fs_c" ] || continue
        fs_out=$("$fs_c" --list-sdks 2>/dev/null | tr -d '\r')
        fs_v=$(printf '%s\n' "$fs_out" | sed -n 's/^\(10\.[0-9]*\.[^ ]*\) .*/\1/p' | tail -n 1)
        if [ -n "$fs_v" ]; then
            DOTNET=$fs_c; SDK_VERSION=$fs_v
            if "$fs_c" --list-runtimes 2>/dev/null | grep 'Microsoft\.AspNetCore\.App 10\.' >/dev/null; then ASPNET10=1; fi
            return 0
        fi
        fs_o=$(printf '%s\n' "$fs_out" | sed -n "s#^\([0-9][0-9.]*[^ ]*\) .*#\1  ($fs_c)#p")
        if [ -n "$fs_o" ]; then OTHER_SDKS="$OTHER_SDKS$fs_o
"; fi
    done <<EOF
$fs_list
EOF
    return 1
}

# The folder the found dotnet lives in, exported as DOTNET_ROOT for vseed when it is not a place the
# .NET host finds by itself (a per-user install in ~/.dotnet above all).
dotnet_root_for_vseed() {
    [ -n "$DOTNET" ] || return 0
    case $DOTNET in
        "$HOME/.dotnet/dotnet") printf '%s' "$HOME/.dotnet" ;;
    esac
}

run_vseed() {
    rv_exe=$(vseed_exe)
    rv_root=$(dotnet_root_for_vseed)
    if [ -z "$rv_root" ] && [ -z "$DOTNET" ] && [ -x "$HOME/.dotnet/dotnet" ]; then rv_root=$HOME/.dotnet; fi
    if [ -n "$rv_root" ]; then
        DOTNET_ROOT=$rv_root "$rv_exe" "$@"
    else
        "$rv_exe" "$@"
    fi
}

install_sdk_per_user() {
    if ! command -v bash >/dev/null 2>&1; then
        say 'Microsoft'"'"'s installer script needs bash, and bash was not found. Install the SDK another way.'
        return 1
    fi
    isu_tmp=${TMPDIR:-/tmp}/dotnet-install.$$.sh
    say "Downloading $INSTALL_SCRIPT_URL ..."
    if command -v curl >/dev/null 2>&1; then
        curl -fsSL "$INSTALL_SCRIPT_URL" -o "$isu_tmp" || { rm -f "$isu_tmp"; say 'The download failed.'; return 1; }
    elif command -v wget >/dev/null 2>&1; then
        wget -q -O "$isu_tmp" "$INSTALL_SCRIPT_URL" || { rm -f "$isu_tmp"; say 'The download failed.'; return 1; }
    else
        say 'Neither curl nor wget was found, so the installer script cannot be downloaded.'
        return 1
    fi
    say "Running it: bash dotnet-install.sh --channel 10.0 --install-dir $HOME/.dotnet"
    bash "$isu_tmp" --channel 10.0 --install-dir "$HOME/.dotnet"
    isu_code=$?
    rm -f "$isu_tmp"
    if [ $isu_code -ne 0 ]; then say "The installer script failed (exit code $isu_code)."; return 1; fi
    return 0
}

ensure_sdk() {
    title 'Step 1 of 4: the .NET 10 SDK'
    if find_sdk; then
        say "Found the .NET SDK $SDK_VERSION ($DOTNET)."
        if [ -z "$ASPNET10" ]; then
            say 'Its ASP.NET Core 10 runtime was not found. vseed needs it for EVERY command, not only the'
            say 'web page, so the check at the end of this install will fail without it. The full .NET 10 SDK'
            say 'from Microsoft includes it; on Linux, a distribution package named aspnetcore-runtime-10.0'
            say 'adds it to an SDK installed that way.'
        fi
        return 0
    fi
    say 'SeedLab needs the .NET 10 SDK and it was not found on this computer.'
    say ''
    say 'The .NET SDK is Microsoft'"'"'s free toolkit for building .NET programs. SeedLab is built from its'
    say 'source code, here on your computer, and the SDK also brings the runtimes vseed needs to run.'
    if [ -n "$OTHER_SDKS" ]; then
        say 'Other versions are installed, but SeedLab needs a 10.x one:'
        printf '%s' "$OTHER_SDKS" | sed 's/^/  /'
    fi
    say ''
    say 'You can:'
    say '  P  install it for your account only, with Microsoft'"'"'s installer script. No administrator'
    say '     password, no sudo. Before you choose this, know that it:'
    say "     - downloads $INSTALL_SCRIPT_URL and runs it with bash;"
    say '     - that script downloads the SDK from Microsoft: about 200 MB;'
    say '     - installs it for your account only, into this folder:'
    say "         $HOME/.dotnet"
    say "       SeedLab's scripts find it there; it is not added to your PATH. Deleting that folder"
    say '       removes it again.'
    if [ "$OS" = linux ]; then
        say '     If vseed later fails to start with a message about ICU or libssl, your distribution'
        say '     needs its libicu and libssl packages (installing those needs sudo).'
    fi
    if [ "$OS" = macos ]; then
        say '  B  open Microsoft'"'"'s download page and use the official installer (.pkg) yourself:'
        say "       $SDK_PAGE"
        say '     Choose the SDK for macOS: Arm64 for an Apple processor (M1, M2...), x64 for Intel.'
        say '     The installer asks for your administrator password. (Homebrew can also install .NET;'
        say '     Microsoft'"'"'s own instructions do not cover it.) Then run this again.'
    else
        say '  D  use your distribution'"'"'s own package instead. You run that yourself; it needs sudo,'
        say '     which this script never runs. For example, on Ubuntu 24.04 and later:'
        say '       sudo apt-get update && sudo apt-get install -y dotnet-sdk-10.0'
        say '     (Ubuntu 22.04 first needs: sudo add-apt-repository ppa:dotnet/backports)'
        say '     Other distributions: https://learn.microsoft.com/dotnet/core/install/linux'
        say '     Then run this again.'
    fi
    say '  C  cancel. Nothing has been changed.'
    say ''
    es_letters='P B C'
    if [ "$OS" != macos ]; then es_letters='P D C'; fi
    if ! ask_choice 'Your choice' "$es_letters"; then CHOICE=C; fi
    case $CHOICE in
        P)
            install_sdk_per_user || return 1
            if find_sdk; then say "The .NET SDK $SDK_VERSION is installed ($DOTNET)."; return 0; fi
            say 'The installer finished, but the .NET 10 SDK still cannot be found.'
            return 1 ;;
        B)
            open_url "$SDK_PAGE"
            say 'When the SDK is installed, run this again.'
            return 1 ;;
        D)
            say 'When the package is installed, run this again.'
            return 1 ;;
    esac
    say 'Cancelled. Nothing has been changed.'
    return 1
}

# ------------------------------------------------------------------------------------------------
# Step 2: the build
# ------------------------------------------------------------------------------------------------

# A fingerprint of every source file of the tool (names and contents), kept beside the build, so a
# later action can tell that the source changed (git pull, a new ZIP).
source_fingerprint() {
    (
        cd "$REPO" || exit 1
        {
            find src -type f ! -path '*/bin/*' ! -path '*/obj/*' | LC_ALL=C sort | while IFS= read -r sf_f; do
                printf '%s\n' "$sf_f"
                cat "$sf_f"
            done
            if [ -f Directory.Build.props ]; then cat Directory.Build.props; fi
        } | cksum | awk '{ print $1 "-" $2 }'
    )
}

# missing | current | stale | unknown (built some other way, so the script cannot tell)
build_state() {
    if [ -z "$(vseed_exe)" ]; then printf 'missing'; return; fi
    if [ ! -f "$STAMP_FILE" ]; then printf 'unknown'; return; fi
    if [ "$(cat "$STAMP_FILE")" = "$(source_fingerprint)" ]; then printf 'current'; else printf 'stale'; fi
}

TELEMETRY_NOTE_SHOWN=
run_build() {
    title 'Step 2 of 4: build SeedLab'
    rb_running=$(vseed_procs | filter_procs any ours)
    if [ -n "$rb_running" ]; then
        say 'Note: vseed from this build is running. It keeps running the old version until you stop'
        say 'and start it again:'
        printf '%s\n' "$rb_running" | show_procs
    fi
    say 'Building SeedLab from the source in this folder:'
    say '  dotnet build src/SeedLab.Cli/SeedLab.Cli.csproj -c Release'
    say 'The first build takes a minute or two; later ones are quicker.'
    if [ -z "$TELEMETRY_NOTE_SHOWN" ]; then
        say ''
        say 'Note: the .NET SDK sends usage data ("telemetry") to Microsoft by default. SeedLab'"'"'s scripts'
        say 'turn that off for the builds they run (DOTNET_CLI_TELEMETRY_OPTOUT=1). Other uses of dotnet'
        say 'on this computer are not changed.'
        TELEMETRY_NOTE_SHOWN=1
    fi
    say ''
    (
        cd "$REPO" || exit 1
        # --disable-build-servers: nothing is left running in the background after the build.
        DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 "$DOTNET" build "$CLI_PROJECT" -c Release --nologo --disable-build-servers
    )
    rb_code=$?
    if [ $rb_code -ne 0 ] || [ -z "$(vseed_exe)" ]; then
        say ''
        say "The build failed (exit code $rb_code). The lines above say why."
        return 1
    fi
    source_fingerprint > "$STAMP_FILE" 2>/dev/null
    say ''
    say 'Built.'
    return 0
}

# ------------------------------------------------------------------------------------------------
# Step 3: the vseed command
# ------------------------------------------------------------------------------------------------

# The startup file a new terminal reads: zsh -> ~/.zshrc; bash -> ~/.bash_profile on macOS (Terminal
# opens login shells) or ~/.bashrc on Linux; anything else -> ~/.profile.
startup_file() {
    case ${SHELL-} in
        */zsh) printf '%s' "$HOME/.zshrc" ;;
        */bash) if [ "$OS" = macos ]; then printf '%s' "$HOME/.bash_profile"; else printf '%s' "$HOME/.bashrc"; fi ;;
        *) if [ "$OS" = macos ]; then printf '%s' "$HOME/.zshrc"; else printf '%s' "$HOME/.profile"; fi ;;
    esac
}

# Startup files that hold SeedLab's marked block.
files_with_block() {
    for fwb in "$HOME/.zshrc" "$HOME/.bashrc" "$HOME/.bash_profile" "$HOME/.profile" "$HOME/.zprofile"; do
        if [ -f "$fwb" ] && grep -x -F -e "$BLOCK_BEGIN" "$fwb" >/dev/null 2>&1; then printf '%s\n' "$fwb"; fi
    done
}

# True when every SeedLab BEGIN line in the file is followed by its END line, before the next BEGIN.
block_is_whole() {
    awk -v b="$BLOCK_BEGIN" -v e="$BLOCK_END" '
        $0 == b { if (open) bad = 1; open = 1; next }
        $0 == e { if (!open) bad = 1; open = 0; next }
        END { exit (open || bad) ? 1 : 0 }' "$1"
}

# Takes the marked block out of a startup file, with the blank line install put before it, and
# leaves every other line as it was. The file is rewritten in place (cat >), so its permissions,
# owner and any symlink to it are kept.
#
# ONLY when the block is whole (review of 2026-09-25): the awk below drops everything from a BEGIN line
# to its END line, so a block whose END line was edited or deleted would take every line after it - a
# conda setup, the user's own aliases - with it. Such a file is left exactly as it is, and the user is
# told which lines to take out by hand.
remove_block() {
    if ! block_is_whole "$1"; then
        say "  $1 was left exactly as it is: SeedLab's lines in it no longer end with the line"
        say "    $BLOCK_END"
        say '  (it was changed or removed), so this script cannot tell where they stop. Open the file in a'
        say '  text editor and delete, by hand, the line'
        say "    $BLOCK_BEGIN"
        say '  and the SeedLab lines under it (the ones that mention vseed or .local/bin) - nothing else.'
        return 1
    fi
    rbk_tmp=$1.seedlab-tmp.$$
    awk -v b="$BLOCK_BEGIN" -v e="$BLOCK_END" '
        $0 == b { inblock = 1; pending = ""; next }
        inblock { if ($0 == e) inblock = 0; next }
        $0 == "" { pending = pending "\n"; next }
        { printf "%s", pending; pending = ""; print }
        END { printf "%s", pending }' "$1" > "$rbk_tmp" && cat "$rbk_tmp" > "$1"
    rm -f "$rbk_tmp"
}

# What the launcher at ~/.local/bin/vseed is: ours | other-seedlab | dangling | foreign | none
launcher_state() {
    if [ ! -e "$LAUNCHER" ] && [ ! -L "$LAUNCHER" ]; then printf 'none'; return; fi
    if ! grep -F -e "$LAUNCHER_MARK" "$LAUNCHER" >/dev/null 2>&1; then printf 'foreign'; return; fi
    ls_target=$(sed -n 's/^# target: //p' "$LAUNCHER" | head -n 1)
    if [ "$ls_target" = "$BUILD_DIR" ]; then printf 'ours'; return; fi
    if [ -d "$ls_target" ]; then printf 'other-seedlab'; else printf 'dangling'; fi
}

write_launcher() {
    mkdir -p "$LAUNCHER_DIR" || return 1
    launcher_text > "$LAUNCHER" && chmod +x "$LAUNCHER"
}

# The launcher as it should be now. Regenerated on every install, so an update of this script also
# updates it.
launcher_text() {
    wl_root=$(dotnet_root_for_vseed)
    {
        printf '#!/bin/sh\n'
        printf '%s Made by seedlab.sh; "sh seedlab.sh uninstall" removes it.\n' "$LAUNCHER_MARK"
        printf '# target: %s\n' "$BUILD_DIR"
        if [ -n "$wl_root" ]; then
            printf '# The .NET SDK was installed for this account only, where .NET does not look by itself.\n'
            printf 'DOTNET_ROOT=%s\nexport DOTNET_ROOT\n' "$(shell_quote "$wl_root")"
        fi
        printf 'if [ ! -x %s ]; then\n' "$(shell_quote "$BUILD_DIR/vseed")"
        printf '    echo %s >&2\n' "$(shell_quote "vseed: the SeedLab build it points at is gone: $BUILD_DIR")"
        printf '    echo %s >&2\n' "$(shell_quote "       run:  sh seedlab.sh install   in your SeedLab folder.")"
        printf '    exit 3\n'
        printf 'fi\n'
        printf 'exec %s "$@"\n' "$(shell_quote "$BUILD_DIR/vseed")"
    }
}

register_command() {
    title 'Step 3 of 4: make "vseed" a command'
    rc_state=$(launcher_state)
    case $rc_state in
        foreign)
            say "There is already a file at $LAUNCHER that SeedLab did not make, so it was left alone."
            say 'Rename or remove it, then run this again, if you want "vseed" to be SeedLab.'
            return 0 ;;
        other-seedlab)
            say "$LAUNCHER belongs to another SeedLab folder:"
            say "  $(sed -n 's/^# target: //p' "$LAUNCHER" | head -n 1)"
            if ! ask_yes_no 'Point it at this folder instead?' n; then say 'Left as it is.'; return 0; fi ;;
        dangling)
            say "$LAUNCHER points at a SeedLab folder that no longer exists; it is replaced." ;;
    esac
    if [ "$rc_state" = ours ] && [ "$(cat "$LAUNCHER")" = "$(launcher_text)" ]; then
        say "The vseed command is already set up for your account: $LAUNCHER"
    else
        write_launcher || { say "Could not write $LAUNCHER."; return 1; }
        say "The vseed command is set up for your account: $LAUNCHER"
        say "(a small script that runs $BUILD_DIR/vseed)"
    fi
    case ":$PATH:" in
        *":$LAUNCHER_DIR:"*) return 0 ;;
    esac
    rc_file=$(startup_file)
    if files_with_block | grep . >/dev/null; then
        say "$LAUNCHER_DIR is not on the PATH of this terminal yet, but SeedLab's lines are already in:"
        files_with_block | sed 's/^/  /'
        say 'New terminals will find vseed.'
        return 0
    fi
    say ''
    say "$LAUNCHER_DIR is not on your PATH, so terminals will not find vseed there yet."
    say "SeedLab can add these lines to the end of $rc_file (the file your terminal reads at start):"
    say "  $BLOCK_BEGIN"
    say '  case ":$PATH:" in *":$HOME/.local/bin:"*) ;; *) PATH="$HOME/.local/bin:$PATH"; export PATH ;; esac'
    say "  $BLOCK_END"
    say 'Uninstall takes exactly those lines out again.'
    if ask_yes_no 'Add them?' y; then
        {
            printf '\n%s\n' "$BLOCK_BEGIN"
            printf '# Added by SeedLab'"'"'s seedlab.sh so that the vseed command is found; "sh seedlab.sh uninstall" removes it.\n'
            printf '%s\n' 'case ":$PATH:" in *":$HOME/.local/bin:"*) ;; *) PATH="$HOME/.local/bin:$PATH"; export PATH ;; esac'
            printf '%s\n' "$BLOCK_END"
        } >> "$rc_file" || { say "Could not write $rc_file."; return 1; }
        say "Added to $rc_file. Terminals that are already open do not see it; new ones do (or run: . $rc_file)."
    else
        say "Not added. Run vseed as $LAUNCHER, or use \"sh seedlab.sh shell\"."
    fi
    return 0
}

# ------------------------------------------------------------------------------------------------
# Step 4: check, and install as a whole
# ------------------------------------------------------------------------------------------------

check_install() {
    title 'Step 4 of 4: check'
    ci_out=$(run_vseed --version 2>&1 </dev/null)
    ci_code=$?
    if [ $ci_code -ne 0 ]; then
        say "vseed --version failed (exit code $ci_code):"
        say "$ci_out"
        return 1
    fi
    say "  $ci_out"
    platform_note
    return 0
}

do_install() {
    refuse_root || return 3
    title 'SeedLab: install or update'
    say 'This checks for the .NET 10 SDK, builds SeedLab from the source in this folder, and makes'
    say '"vseed" a command for your account. It is safe to run again at any time - run it after every'
    say 'update of SeedLab (git pull, or a new ZIP unpacked over this folder).'
    untested_note
    ensure_sdk || return 1
    run_build || return 1
    register_command || return 1
    check_install || return 1
    say ''
    say 'SeedLab is installed.'
    say 'Next:'
    say '  - "sh seedlab.sh web" opens the map and the search in your browser;'
    say '  - "sh seedlab.sh shell" opens a shell in the SeedLab folder where vseed works;'
    say '  - or open a NEW terminal and type:  vseed --help'
    return 0
}

# For the actions that need a build: builds when there is none, and offers to rebuild when the source
# changed since the last build.
ensure_built() {
    eb_state=$(build_state)
    if [ "$eb_state" = missing ]; then
        say "SeedLab has not been built yet, and $1 needs the build."
        say 'Installing it first does the same as "sh seedlab.sh install".'
        if ! ask_yes_no 'Install SeedLab now?' y; then say 'Nothing was done.'; return 1; fi
        do_install
        return
    fi
    if [ "$eb_state" = stale ]; then
        say 'The SeedLab source has changed since it was last built (an update?).'
        if ask_yes_no 'Build it again first (the same as "sh seedlab.sh install")?' y; then
            do_install
            return
        fi
        say 'Using the existing build.'
    fi
    if [ -z "$DOTNET" ]; then find_sdk >/dev/null 2>&1; fi
    return 0
}

# ------------------------------------------------------------------------------------------------
# web, stop, status, shell
# ------------------------------------------------------------------------------------------------

# The running server (an SRV_REG line) that 'vseed serve' with these options would open instead of
# starting another - vseed's own rule: the same cache folder, and with --port N the one on port N
# (--port 0 always starts a new one). Prints nothing and fails when there is none.
same_server() {
    ss_root=$(opt_value --cache-dir "$@") || ss_root=$(vseed_default_root)
    case $ss_root in /*) ;; *) ss_root=$REPO/$ss_root ;; esac   # vseed runs from the SeedLab folder
    ss_root=${ss_root%/}
    ss_port=$(opt_value --port "$@") || ss_port=
    [ "$ss_port" != 0 ] || return 1
    while IFS='|' read -r ss_r ss_pid ss_url ss_p ss_rest; do
        [ -n "$ss_pid" ] || continue
        case $ss_r in /*) ;; *) ss_r=$REPO/$ss_r ;; esac
        [ "${ss_r%/}" = "$ss_root" ] || continue
        if [ -z "$ss_port" ] || [ "$ss_p" = "$ss_port" ]; then printf '%s' "$ss_url"; return 0; fi
    done <<EOF
$SRV_REG
EOF
    return 1
}

do_web() {
    refuse_root || return 3
    ensure_built 'the web page' || return 1
    if [ "${SEEDLAB_SCRIPT_TEST_NO_WINDOWS-}" = 1 ]; then
        case " $* " in *' --no-browser '*) ;; *) set -- "$@" --no-browser ;; esac
    fi
    server_state
    if [ "$SRV_STATE" = running ]; then
        if [ -n "$SRV_REG" ] && same_server "$@" >/dev/null; then
            # vseed's own answer (2026-09-24): a second 'vseed serve' opens the running server's page,
            # says where that server's terminal is, and exits 0 without starting anything.
            (cd "$REPO" && run_vseed serve "$@")
            return $?
        fi
        if ! opt_value --port "$@" >/dev/null; then
            if [ -n "$SRV_URL" ]; then
                say "SeedLab's web page is already running at $SRV_URL - opening it in your browser."
                if [ -z "$SRV_REG" ]; then show_unregistered it; fi
                open_url "$SRV_URL"
                return 0
            fi
            say 'A SeedLab web server is already running, but its address could not be found out:'
            printf '%s\n' "$SRV_LIST" | show_procs
            say 'Look for the terminal it runs in, or stop it with "sh seedlab.sh stop" and start it again.'
            return 1
        fi
        # An explicit --port that the running server is not on: another server, as asked.
    fi
    platform_note
    # A vseed that knows 'serve --stop' (2026-09-24) prints its own banner first - what this terminal
    # is and how to stop it (Stop SeedLab on the page, "sh seedlab.sh stop", Ctrl+C twice) - and
    # titles the terminal. An older one gets this script's.
    if ! serve_has --stop; then
        say ''
        say '================================================================================'
        say ' This terminal IS SeedLab'"'"'s web server.'
        say ' Leave it open (minimised is fine) while you use the page.'
        say ' To stop it: press Ctrl+C here, or in another terminal:'
        say "   sh $(shell_quote "$REPO/seedlab.sh") stop"
        say '================================================================================'
        say ''
    fi
    # Ctrl+C must stop vseed, not this script, so that it can still say how the server ended. A trap
    # that runs a command (not an ignored signal) is reset in vseed, which gets its Ctrl+C as usual.
    # vseed runs as a direct child of this shell - not in a subshell, which would die of the
    # Ctrl+C at once - from the SeedLab folder: it finds data/ there, and the page's results go to
    # its seedlab-results folder.
    cd "$REPO" || return 1
    trap 'WEB_INTERRUPTED=1' INT
    run_vseed serve "$@"
    dw_code=$?
    trap - INT
    say ''
    case $dw_code in
        # A vseed that knows --stop has just said so itself ("SeedLab's web server has stopped").
        0) if ! serve_has --stop; then say 'The SeedLab web server has stopped.'; fi ;;
        130) say 'The SeedLab web server has stopped.' ;;
        143) say 'The SeedLab web server was stopped from outside (SIGTERM).' ;;
        *) say "The SeedLab web server stopped with exit code $dw_code. The lines above say why." ;;
    esac
    return $dw_code
}

# For what vseed serve --stop cannot reach: a build from before it existed, or a server no cache
# folder here knows about. Each process is sent SIGTERM.
stop_servers_by_process() {
    sbp_foreign=$(printf '%s\n' "$1" | filter_procs any foreign)
    sbp_ours=$(printf '%s\n' "$1" | filter_procs any ours)
    sbp_result=0
    if [ -n "$sbp_foreign" ]; then
        say 'A SeedLab web server that does not belong to this folder (or whose folder could not be'
        say 'told) is running:'
        printf '%s\n' "$sbp_foreign" | show_procs
        if [ -n "$TEST_MODE" ]; then
            say '(test mode: servers from other folders are never offered)'
        elif ask_yes_no 'Stop that one as well?' n; then
            printf '%s\n' "$sbp_foreign" | stop_procs || sbp_result=1
        else
            say 'Left running.'
        fi
    fi
    [ -n "$sbp_ours" ] || return $sbp_result
    say 'SeedLab'"'"'s web server, started from this folder, is running:'
    printf '%s\n' "$sbp_ours" | show_procs
    say 'It cannot be asked through vseed, so it is sent SIGTERM. A vseed from 2026-09-24 or later then'
    say 'stops a running search with its checkpoint saved; an older one stops it too, and the search'
    say 'loses what its last checkpoint did not hold.'
    # STOP_CONFIRMED: uninstall's "Go ahead?" already covered this folder's server.
    if [ -z "${STOP_CONFIRMED-}" ] && ! ask_yes_no 'Stop it now?' n; then say 'Left running.'; return 1; fi
    printf '%s\n' "$sbp_ours" | stop_procs || return 1
    say 'The web server has stopped.'
    return $sbp_result
}

do_stop() {
    refuse_root || return 3
    if serve_has --stop; then
        if [ $# -gt 0 ]; then
            # Options were given (sh seedlab.sh stop --yes, --force, --cache-dir <folder>): they are
            # for vseed serve --stop, which knows what to do with them.
            (cd "$REPO" && run_vseed serve --stop "$@")
            return $?
        fi
        serve_report
        if [ -n "$SRV_PROBLEMS" ]; then printf '%s' "$SRV_PROBLEMS" | sed 's/^/Could not ask vseed about /'; fi
        if [ -z "$SRV_REG" ]; then
            say "SeedLab's web server is not running - nothing to stop."
            show_unregistered
            show_leftover
            [ -z "$SRV_PROBLEMS" ]
            return
        fi
        # vseed stops a running search with its checkpoint saved, and asks first when one is running
        # ("Stop anyway? [y/N]", with the command that continues it).
        stop_registered "$SRV_REG" ''
        ds_code=$?
        show_unregistered also
        return $ds_code
    fi
    ds_servers=$(vseed_procs | filter_procs servers any)
    if [ -z "$ds_servers" ]; then
        say 'No SeedLab web server is running. Nothing to stop.'
        return 0
    fi
    if [ -n "$(vseed_exe)" ]; then
        say 'This build of vseed cannot stop its web server by itself yet (it has no "serve --stop"), so'
        say 'this script stops the server'"'"'s process instead.'
    else
        say 'This SeedLab folder has no build, so this script looks for the web server'"'"'s process instead.'
    fi
    stop_servers_by_process "$ds_servers"
}

do_status() {
    refuse_root || return 3
    title 'SeedLab status'
    say "Folder         $REPO"
    if find_sdk; then say ".NET 10 SDK    found: $SDK_VERSION ($DOTNET)"
    else say '.NET 10 SDK    not found ("sh seedlab.sh install" helps you install it)'; fi
    case $(build_state) in
        missing) say 'Build          not built yet' ;;
        current) say 'Build          built, up to date with the source' ;;
        stale) say 'Build          built, but the source has changed since: run "sh seedlab.sh install"' ;;
        *) say 'Build          built (not by this script, so it cannot tell whether it is up to date)' ;;
    esac
    case $(launcher_state) in
        ours) say "Command        \"vseed\" is set up for your account ($LAUNCHER)" ;;
        other-seedlab) say "Command        $LAUNCHER belongs to another SeedLab folder" ;;
        dangling) say "Command        $LAUNCHER points at a SeedLab folder that is gone" ;;
        foreign) say "Command        $LAUNCHER exists but is not SeedLab's" ;;
        *) say 'Command        "vseed" is not set up for your account' ;;
    esac
    fb=$(files_with_block)
    if [ -n "$fb" ]; then say "               SeedLab's PATH lines are in: $(printf '%s' "$fb" | tr '\n' ' ')"; fi
    server_state
    if [ -n "$SRV_REG" ]; then
        # What vseed serve --status says, for every cache folder a server of this account can be in.
        say 'Web server'
        printf '%s' "$SRV_REG" | cut -d'|' -f6- | sed 's/^/  /'
        show_unregistered also
    elif [ "$SRV_STATE" = running ]; then
        if [ -n "$SRV_URL" ]; then say "Web server     running at $SRV_URL"; else say 'Web server     running, address unknown'; fi
        if [ -n "$SRV_UNREG" ]; then show_unregistered it; else printf '%s\n' "$SRV_LIST" | show_procs; fi
    elif [ "$SRV_STATE" = unknown ]; then say 'Web server     cannot tell:'; printf '%s' "$SRV_PROBLEMS" | sed 's/^/               /'
    else say 'Web server     not running'; fi
    show_leftover
    # The two session logs (2026-09-24): this session's, or the last one's, and the one before it.
    serve_roots | while IFS= read -r st_root; do
        st_names=
        for st_n in vseed.log vseed-prev.log; do
            if [ -f "$st_root/logs/$st_n" ]; then st_names="$st_names${st_names:+, }$st_n"; fi
        done
        if [ -n "$st_names" ]; then say "Session logs   $st_root/logs  ($st_names)"; fi
    done
    st_cache=$(default_cache_root)
    if [ -d "$st_cache" ]; then say "Cache folder   $st_cache  ($(size_of "$st_cache"))"
    else say "Cache folder   none yet ($st_cache)"; fi
    if [ -n "${SEEDLAB_CACHE_DIR-}" ] && [ "$SEEDLAB_CACHE_DIR" != "$st_cache" ]; then
        say "               SEEDLAB_CACHE_DIR chooses $SEEDLAB_CACHE_DIR"
    fi
    return 0
}

do_shell() {
    refuse_root || return 3
    ensure_built 'the SeedLab shell' || return 1
    say 'This shell knows the vseed command, and it is in the SeedLab folder, where vseed finds its data.'
    say 'Try:  vseed seed 12345    vseed map 12345    vseed search --help    (type exit to leave)'
    say ''
    (cd "$REPO" && run_vseed --help)
    if [ -n "$NO_PAUSE" ] || [ "${SEEDLAB_SCRIPT_TEST_NO_WINDOWS-}" = 1 ] || [ ! -t 0 ]; then return 0; fi
    dsh_root=$(dotnet_root_for_vseed)
    (
        cd "$REPO" || exit 1
        PATH=$BUILD_DIR:$PATH
        export PATH
        if [ -n "$dsh_root" ]; then DOTNET_ROOT=$dsh_root; export DOTNET_ROOT; fi
        "${SHELL:-/bin/sh}"
    )
    return 0
}

# ------------------------------------------------------------------------------------------------
# uninstall
# ------------------------------------------------------------------------------------------------

# What SeedLab puts in a cache folder: vseed's category folders and a web server's registry folder,
# and in each only files of the kinds SeedLab writes there. A folder in a SEEDLAB_CACHE_DIR folder
# counts as SeedLab's only when BOTH its name and everything in it fit (review of 2026-09-25): a name
# alone - "logs", "maps" - or any "*.log" also matched a person's own files, which uninstall then
# offered to remove. Nothing at the top of the folder but these folders is SeedLab's.
# $1 = the folder (a path), with no trailing slash.
is_seedlab_cache_entry() {
    [ -d "$1" ] && [ ! -L "$1" ] || return 1
    isc_name=${1##*/}
    case $isc_name in checkpoints|runs|maps|tiles|scratch|selftest|logs|serve) ;; *) return 1 ;; esac
    isc_bad=$(find "$1" ! -type d 2>/dev/null | while IFS= read -r isc_f; do
        if [ -L "$isc_f" ] || [ ! -f "$isc_f" ]; then echo x; break; fi
        isc_rel=${isc_f#"$1"/}
        isc_base=${isc_f##*/}
        case $isc_name in
            tiles)
                case $isc_rel in
                    v[0-9]*/*/*) echo x; break ;;
                    v[0-9]*/*.png|v[0-9]*/.seedlab-tmp-*) ;;
                    *) echo x; break ;;
                esac ;;
            scratch)
                case $isc_rel in
                    */*) [ -f "$1/${isc_rel%%/*}/owner.txt" ] || { echo x; break; } ;;
                    *) echo x; break ;;
                esac ;;
            *)
                case $isc_rel in */*) echo x; break ;; esac
                case $isc_name/$isc_base in
                    checkpoints/*.ckpt|checkpoints/*.ckpt.top|checkpoints/*.ckpt.top2|checkpoints/*.ckpt.query.json) ;;
                    checkpoints/*.ckpt.tmp|checkpoints/*.ckpt.top.tmp|checkpoints/*.ckpt.top2.tmp) ;;
                    checkpoints/*.survivors|checkpoints/*.survivors.tmp|checkpoints/.seedlab-tmp-*) ;;
                    runs/.seedlab-tmp-*) ;;
                    maps/map-*.png|maps/.seedlab-tmp-*) ;;
                    selftest/passed-*.txt|selftest/.seedlab-tmp-*) ;;
                    logs/vseed.log|logs/vseed-prev.log|logs/vseed.log.[1-4]) ;;
                    serve/server-*.json|serve/.seedlab-tmp-*) ;;
                    *) echo x; break ;;
                esac ;;
        esac
    done)
    [ -z "$isc_bad" ]
}

# Moves a file or folder to the Trash when the system has one; otherwise deletes it for good.
# The caller has said which it will be (trash_kind) before asking.
trash_kind() {
    if [ "$OS" = macos ] && [ -d "$HOME/.Trash" ]; then printf 'Trash'; return; fi
    if command -v gio >/dev/null 2>&1; then printf 'Trash'; return; fi
    if command -v trash-put >/dev/null 2>&1; then printf 'Trash'; return; fi
    printf 'permanent'
}

remove_path() {
    if [ "$OS" = macos ] && [ -d "$HOME/.Trash" ]; then
        rp_name=${1##*/}
        rp_dest=$HOME/.Trash/$rp_name
        if [ -e "$rp_dest" ]; then rp_dest=$HOME/.Trash/$rp_name-$(date +%Y%m%d-%H%M%S); fi
        mv "$1" "$rp_dest" && { say "  moved to the Trash: $1"; return 0; }
    elif command -v gio >/dev/null 2>&1; then
        gio trash "$1" && { say "  moved to the Trash: $1"; return 0; }
    elif command -v trash-put >/dev/null 2>&1; then
        trash-put "$1" && { say "  moved to the Trash: $1"; return 0; }
    else
        rm -rf "$1" && { say "  deleted: $1"; return 0; }
    fi
    say "  could not remove it, so it was left in place: $1"
    return 1
}

valheim_dirs() {
    {
        if [ -n "${SEEDLAB_VALHEIM_DIR-}" ]; then printf '%s\n' "$SEEDLAB_VALHEIM_DIR"; fi
        if [ -z "$TEST_MODE" ]; then
            vd_d=${REPO%/*}
            while [ -n "$vd_d" ] && [ "$vd_d" != / ]; do
                if [ -d "$vd_d/BepInEx" ] && { [ -e "$vd_d/valheim.x86_64" ] || [ -e "$vd_d/valheim.exe" ]; }; then printf '%s\n' "$vd_d"; break; fi
                vd_d=${vd_d%/*}
            done
            for vd_root in "$HOME/.steam/steam" "$HOME/.local/share/Steam" \
                           "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam" \
                           "$HOME/Library/Application Support/Steam"; do
                [ -d "$vd_root" ] || continue
                printf '%s\n' "$vd_root/steamapps/common/Valheim"
                if [ -f "$vd_root/steamapps/libraryfolders.vdf" ]; then
                    sed -n 's/^[[:space:]]*"path"[[:space:]]*"\([^"]*\)".*/\1/p' "$vd_root/steamapps/libraryfolders.vdf" |
                        while IFS= read -r vd_lib; do printf '%s\n' "$vd_lib/steamapps/common/Valheim"; done
                fi
            done
        fi
    } | awk '!seen[$0]++' | while IFS= read -r vd_v; do
        if [ -d "$vd_v/BepInEx" ]; then printf '%s\n' "$vd_v"; fi
    done
}

dumper_items() {
    valheim_dirs | while IFS= read -r di_v; do
        for di_i in "$di_v"/BepInEx/plugins/*SeedLabDumper*; do
            if [ -e "$di_i" ]; then printf '%s\n' "$di_i"; fi
        done
        if [ -f "$di_v/BepInEx/config/DoomMachine.SeedLabDumper.cfg" ]; then printf '%s\n' "$di_v/BepInEx/config/DoomMachine.SeedLabDumper.cfg"; fi
    done
}

dumper_output_dir() {
    printf '%s' "$HOME/AppData/valheim-dumper"
}

# Keeps the "pid|exe|arguments" lines of vseed processes that use one of the cache folders uninstall
# removes. One started with --cache-dir naming another folder (a test, a second setup) does not, so
# it neither blocks the uninstall nor is stopped by it. Without --cache-dir, or with a folder that
# cannot be told, it is taken to use them: that is the safe side.
filter_our_cache() {
    foc_roots=$(serve_roots)
    while IFS='|' read -r foc_pid foc_exe foc_args; do
        [ -n "$foc_pid" ] || continue
        foc_dir=$(printf '%s\n' " $foc_args" | sed -n 's/.* --cache-dir[= ]\([^ ]*\).*/\1/p')
        foc_ours=yes
        case $foc_dir in
            /*|[A-Za-z]:*)
                foc_ours=no
                # ps shows the arguments without quotes, so a folder with a space in it is cut at the
                # space: a cache folder that starts with what is left counts as the same one.
                while IFS= read -r foc_r; do
                    case $foc_r in "$foc_dir"*) foc_ours=yes ;; esac
                done <<EOF
$foc_roots
EOF
                ;;
        esac
        if [ $foc_ours = yes ]; then printf '%s|%s|%s\n' "$foc_pid" "$foc_exe" "$foc_args"; fi
    done
}

core_uninstall_pending() {
    [ -d "$(default_cache_root)" ] && return 0
    case $(launcher_state) in ours|dangling) return 0 ;; esac
    files_with_block | grep . >/dev/null && return 0
    if serve_has --stop; then
        serve_report
        [ -n "$SRV_REG" ] && return 0
        [ -n "$(printf '%s\n' "$SRV_UNREG" | filter_procs any ours | filter_our_cache)" ] && return 0
        return 1
    fi
    [ -n "$(vseed_procs | filter_procs servers ours)" ] && return 0
    return 1
}

show_kept() {
    say 'Kept on purpose:'
    say '  - this SeedLab folder and its build ("sh seedlab.sh remove-build" deletes the build);'
    say '  - data/ (the game data) and your results: seedlab-results folders and any file you named'
    say '    with --out;'
    say '  - the .NET SDK, because other programs may use it. If this script installed it for your'
    say "    account, it is the folder $HOME/.dotnet; otherwise remove it the way it was installed."
}

do_uninstall() {
    refuse_root || return 3
    title 'SeedLab: uninstall (everything except the SeedLab folder and its build)'
    du_cache=$(default_cache_root)
    du_launcher=$(launcher_state)
    du_blocks=$(files_with_block)
    # du_reg: the servers vseed serve --stop can stop (SRV_REG lines). du_servers: the ones it cannot
    # (an older build, or a server no cache folder here knows about), which are sent SIGTERM instead.
    du_reg=
    if serve_has --stop; then
        serve_report
        du_reg=$SRV_REG
        du_servers=$(printf '%s\n' "$SRV_UNREG" | filter_our_cache)
    else
        du_servers=$(vseed_procs | filter_procs servers any | filter_our_cache)
    fi
    du_servers_ours=$(printf '%s\n' "$du_servers" | filter_procs any ours)
    du_searches=$(vseed_procs | filter_procs others ours | filter_our_cache)
    du_foreign_searches=$(vseed_procs | filter_procs others foreign | filter_our_cache)
    du_chosen=
    du_chosen_whole=
    if [ -n "${SEEDLAB_CACHE_DIR-}" ] && [ "$SEEDLAB_CACHE_DIR" != "$du_cache" ] && [ -d "$SEEDLAB_CACHE_DIR" ]; then
        du_chosen=${SEEDLAB_CACHE_DIR%/}
        du_chosen_whole=yes
        for du_e in "$du_chosen"/* "$du_chosen"/.[!.]*; do
            [ -e "$du_e" ] || [ -L "$du_e" ] || continue
            is_seedlab_cache_entry "$du_e" || du_chosen_whole=no
        done
    fi
    du_vars=$(env | grep '^SEEDLAB_' | grep -v '^SEEDLAB_SCRIPT_TEST_' | cut -d= -f1)
    if [ -n "$CACHE_DIR_SET_BY_SCRIPT" ]; then du_vars=$(printf '%s\n' "$du_vars" | grep -v '^SEEDLAB_CACHE_DIR$'); fi
    du_dumper=$(dumper_items)
    du_dumper_out=
    if [ -d "$(dumper_output_dir)" ]; then du_dumper_out=$(dumper_output_dir); fi
    du_kind=$(trash_kind)

    du_core=
    if [ -d "$du_cache" ] || [ "$du_launcher" = ours ] || [ "$du_launcher" = dangling ] || [ -n "$du_blocks" ] || [ -n "$du_reg" ] || [ -n "$du_servers_ours" ]; then du_core=1; fi
    if [ -z "$du_core" ] && [ -z "$du_chosen" ] && [ -z "$du_searches" ] && [ -z "$du_dumper" ] && [ -z "$du_dumper_out" ] && [ -z "$du_servers" ]; then
        say 'Nothing to uninstall: the vseed command is not set up for your account, there is no SeedLab'
        say "cache folder ($du_cache), and nothing of SeedLab's is running."
        if [ -n "$du_vars" ]; then
            say "(SeedLab settings are set in this terminal: $(printf '%s' "$du_vars" | tr '\n' ' ')- this script does not change those.)"
        fi
        say ''
        show_kept
        return 0
    fi

    if [ -n "$du_core" ]; then
        say 'This will:'
        du_n=1
        while IFS='|' read -r du_r du_pid du_url du_port du_search du_text; do
            [ -n "$du_pid" ] || continue
            say "  $du_n. stop SeedLab's web server at $du_url (process $du_pid);"; du_n=$((du_n + 1))
            if [ "$du_search" = yes ]; then say '     A search is running in it: you are asked about that separately.'; fi
        done <<EOF
$du_reg
EOF
        if [ -n "$du_servers_ours" ]; then
            say "  $du_n. send SIGTERM to SeedLab's web server started from this folder that no cache folder here"; du_n=$((du_n + 1))
            say '     knows about (an older SeedLab, or one started with another cache folder);'
        fi
        if [ -d "$du_cache" ]; then
            if [ "$du_kind" = Trash ]; then say "  $du_n. move SeedLab's cache folder to the Trash ($(size_of "$du_cache")):"
            else say "  $du_n. DELETE SeedLab's cache folder for good - this system has no Trash that this script can use ($(size_of "$du_cache")):"; fi
            du_n=$((du_n + 1))
            say "       $du_cache"
            say '     It holds rendered maps, map tiles, search checkpoints (the resume points of searches'
            say "     you have not finished), vseed's two session logs (logs/vseed.log and vseed-prev.log)"
            say '     and the self-test stamp. All of it is rebuilt when needed, except that an unfinished'
            say '     search can no longer be resumed, and the logs of the last two sessions are gone.'
        fi
        if [ "$du_launcher" = ours ] || [ "$du_launcher" = dangling ]; then
            say "  $du_n. remove the vseed command: delete $LAUNCHER (the small script SeedLab made);"; du_n=$((du_n + 1))
        fi
        if [ -n "$du_blocks" ]; then
            say "  $du_n. take SeedLab's marked lines ($BLOCK_BEGIN ... $BLOCK_END) out of:"; du_n=$((du_n + 1))
            printf '%s\n' "$du_blocks" | sed 's/^/       /'
        fi
        du_later=
        if [ -n "$du_searches" ]; then du_later="$du_later
  - vseed programs running from this folder (a search): stop them?"; fi
        if [ -n "$du_chosen" ]; then du_later="$du_later
  - the cache folder you chose with SEEDLAB_CACHE_DIR: $du_chosen"; fi
        if [ -n "$du_dumper" ]; then du_later="$du_later
  - SeedLab's dumper plugin in Valheim"; fi
        if [ -n "$du_dumper_out" ]; then du_later="$du_later
  - the dumper's output folder: $du_dumper_out"; fi
        if [ -n "$du_later" ]; then say "Then it asks you separately about:$du_later"; fi
        say ''
        if ! ask_yes_no 'Go ahead?' n; then say 'Cancelled. Nothing has been changed.'; return 1; fi
    else
        say 'The vseed command is not set up and there is no SeedLab cache folder. What is left is'
        say 'optional, and each thing is asked about on its own:'
    fi

    # The web servers vseed knows about are asked to stop through vseed serve --stop (a running search
    # is stopped with its checkpoint saved); "Go ahead?" covers that, except for a running search,
    # which is asked about here - its checkpoint goes with the cache folder. The others get SIGTERM.
    du_blocked=
    if [ -n "$du_reg" ]; then
        say ''
        say 'Stopping the web server:'
        du_go=yes
        du_yes=
        if printf '%s' "$du_reg" | cut -d'|' -f5 | grep -x yes >/dev/null; then
            printf '%s' "$du_reg" | cut -d'|' -f5- | grep '^yes|' | cut -d'|' -f2- | sed 's/^/  /'
            if [ "$du_kind" = Trash ]; then du_where='moves to the Trash'; else du_where='deletes'; fi
            say 'Stopping SeedLab stops that search at once, with its checkpoint saved - but the checkpoint is'
            say "in the cache folder this uninstall $du_where, so the search could not be continued afterwards."
            say 'Answer n to leave it running: the uninstall then leaves the cache folder where it is.'
            if ask_yes_no 'Stop it anyway?' n; then du_yes=yes; else du_go=; say 'Left running.'; du_blocked=1; fi
        fi
        if [ -n "$du_go" ]; then
            if ! stop_registered "$du_reg" "$du_yes"; then
                du_blocked=1
                say "SeedLab's web server is still running. Stop it with \"sh seedlab.sh stop\" (it asks about a"
                say 'running search), or in its own terminal, then run the uninstall again.'
            else
                serve_report
                if [ -n "$SRV_REG" ]; then du_blocked=1; fi
            fi
        fi
    fi
    if [ -n "$du_servers" ]; then
        say ''
        STOP_CONFIRMED=1
        stop_servers_by_process "$du_servers" || du_blocked=1
        STOP_CONFIRMED=
        if [ -n "$(vseed_procs | filter_procs servers ours | filter_our_cache)" ]; then du_blocked=1; fi
    fi
    if [ -n "$du_searches" ]; then
        say ''
        say 'vseed is also running from this folder (a search, or another command):'
        printf '%s\n' "$du_searches" | show_procs
        say 'It is sent SIGTERM, which ends it: a search loses what its last checkpoint did not hold - and'
        say 'the checkpoints are in the cache folder this uninstall removes.'
        if ask_yes_no 'Stop it?' n; then printf '%s\n' "$du_searches" | stop_procs || du_blocked=1; else du_blocked=1; fi
    fi
    if [ -n "$du_foreign_searches" ]; then
        say ''
        say 'vseed from ANOTHER SeedLab folder (or one whose folder could not be told) is running; it'
        say 'uses the same cache folder:'
        printf '%s\n' "$du_foreign_searches" | show_procs
        say 'It was left running. Stop it yourself if you want the cache folder removed cleanly.'
        du_blocked=1
    fi

    if [ -d "$du_cache" ]; then
        say ''
        if [ -n "$du_blocked" ]; then
            say 'The cache folder was left in place, because vseed is still running and using it:'
            say "  $du_cache"
            say 'Run the uninstall again once it has stopped.'
        else
            say 'Removing the cache folder:'
            remove_path "$du_cache"
        fi
    fi
    if [ -n "$du_chosen" ]; then
        say ''
        say 'SEEDLAB_CACHE_DIR tells SeedLab to keep its cache in a folder you chose:'
        say "  $du_chosen"
        if [ -n "$du_blocked" ]; then
            say 'Left in place, because vseed is still running.'
        elif [ "$du_chosen_whole" = yes ]; then
            say 'Everything in it is a folder with one of SeedLab'"'"'s names, holding only the kind of files SeedLab'
            say 'writes there (session logs, checkpoints, map tiles and the like) - check it is not yours:'
            for du_e in "$du_chosen"/* "$du_chosen"/.[!.]*; do
                [ -e "$du_e" ] || continue
                say "    $du_e"
            done
            if [ "$du_kind" = Trash ]; then du_q='Move that whole folder to the Trash?'; else du_q='DELETE that whole folder for good (no Trash here)?'; fi
            if ask_yes_no "$du_q" n; then remove_path "$du_chosen"; else say 'Left in place.'; fi
        else
            du_parts=
            for du_e in "$du_chosen"/*; do
                [ -e "$du_e" ] || continue
                if is_seedlab_cache_entry "$du_e"; then du_parts=1; fi
            done
            if [ -z "$du_parts" ]; then
                say 'It holds nothing that is certainly SeedLab'"'"'s (only things SeedLab did not make, or folders with'
                say 'SeedLab'"'"'s names that hold other files too), so nothing in it is offered. Left in place.'
            else
                say 'It also holds things SeedLab did not make, so only these folders in it are offered. They have'
                say 'SeedLab'"'"'s names and hold only the kind of files SeedLab writes there - check they are not yours:'
                for du_e in "$du_chosen"/*; do
                    [ -e "$du_e" ] || continue
                    if is_seedlab_cache_entry "$du_e"; then say "    $du_e"; fi
                done
                say 'Everything else in that folder is left alone.'
                if [ "$du_kind" = Trash ]; then du_q='Move those folders to the Trash?'; else du_q='DELETE those folders for good (no Trash here)?'; fi
                if ask_yes_no "$du_q" n; then
                    for du_e in "$du_chosen"/*; do
                        [ -e "$du_e" ] || continue
                        if is_seedlab_cache_entry "$du_e"; then remove_path "$du_e"; fi
                    done
                else say 'Left in place.'; fi
            fi
        fi
    fi

    if [ "$du_launcher" = ours ] || [ "$du_launcher" = dangling ]; then
        say ''
        say 'Removing the vseed command:'
        rm -f "$LAUNCHER" && say "  deleted $LAUNCHER"
    elif [ "$du_launcher" = other-seedlab ]; then
        say ''
        say "$LAUNCHER belongs to another SeedLab folder; it was left alone. Uninstall from that folder."
    fi
    if [ -n "$du_blocks" ]; then
        printf '%s\n' "$du_blocks" | while IFS= read -r du_f; do
            remove_block "$du_f" && say "  took SeedLab's lines out of $du_f"
        done
        if [ -n "$(files_with_block)" ]; then du_blocked=1; fi
        say 'Terminals that are already open keep their PATH until they are closed.'
    fi

    if [ -n "$du_vars" ]; then
        say ''
        say "SeedLab settings are set in this terminal: $(printf '%s' "$du_vars" | tr '\n' ' ')"
        du_where=
        for du_f in "$HOME/.zshrc" "$HOME/.bashrc" "$HOME/.bash_profile" "$HOME/.profile" "$HOME/.zprofile" "$HOME/.config/fish/config.fish"; do
            if [ -f "$du_f" ] && grep 'SEEDLAB_' "$du_f" >/dev/null 2>&1; then du_where="$du_where $du_f"; fi
        done
        if [ -n "$du_where" ]; then
            say "They are mentioned in:$du_where"
            say 'This script does not edit lines you wrote yourself. Remove them from those files by hand.'
        else
            say 'They are not in your usual startup files, so they were set in this terminal only, or by a'
            say 'file this script does not look at. It does not change them.'
        fi
    fi

    if [ -n "$du_dumper" ]; then
        say ''
        say 'SeedLab'"'"'s dumper plugin is installed in Valheim (it is only needed to read the game'"'"'s data'
        say 'again after a Valheim update):'
        printf '%s\n' "$du_dumper" | sed 's/^/  /'
        if [ -z "$TEST_MODE" ] && ps -A -o comm= 2>/dev/null | grep -i 'valheim' >/dev/null; then
            say 'Valheim is running, so it was left in place. Quit Valheim and run the uninstall again.'
        else
            if [ "$du_kind" = Trash ]; then du_q='Move it to the Trash?'; else du_q='DELETE it for good (no Trash here)?'; fi
            if ask_yes_no "$du_q" n; then
                printf '%s\n' "$du_dumper" | while IFS= read -r du_i; do remove_path "$du_i"; done
            else say 'Left in place.'; fi
        fi
    fi
    say ''
    say 'Not searched: mod-manager profiles (r2modman and the like keep their own BepInEx folders),'
    say 'and Proton prefixes (steamapps/compatdata/892970/...). If the dumper is in one, remove it there.'

    if [ -n "$du_dumper_out" ]; then
        say ''
        say 'The dumper'"'"'s output folder - the raw game data it wrote - is still here:'
        say "  $du_dumper_out  ($(size_of "$du_dumper_out"))"
        say 'SeedLab'"'"'s data/ folder holds a copy of what you imported from it. If you have deleted data/,'
        say 'or have not imported the last run yet, this may be the only copy.'
        if [ "$du_kind" = Trash ]; then du_q='Move it to the Trash?'; else du_q='DELETE it for good (no Trash here)?'; fi
        if ask_yes_no "$du_q" n; then remove_path "$du_dumper_out"; else say 'Left in place.'; fi
    fi

    say ''
    show_kept
    say ''
    # Blocked: something of SeedLab's is still running, or a startup file could not be changed safely, so
    # something was left in place. That is not "finished", and remove-build must not go on as if it were
    # (review of 2026-09-25).
    if [ -n "$du_blocked" ]; then
        say 'The uninstall did NOT finish: something above was left in place (a vseed that is still running,'
        say 'or a startup file this script would not change). Deal with it as said above, then run the'
        say 'uninstall again.'
        return 1
    fi
    say 'Uninstall finished.'
    return 0
}

# ------------------------------------------------------------------------------------------------
# remove-build
# ------------------------------------------------------------------------------------------------

build_output_dirs() {
    (
        cd "$REPO" || exit 1
        for rbd_top in src tests tools; do
            [ -d "$rbd_top" ] || continue
            find "$rbd_top" -name '*.csproj' -type f ! -path '*/bin/*' ! -path '*/obj/*' ! -path '*/build/*' | while IFS= read -r rbd_p; do
                rbd_dir=${rbd_p%/*}
                for rbd_n in bin obj; do
                    if [ -d "$rbd_dir/$rbd_n" ]; then printf '%s\n' "$rbd_dir/$rbd_n"; fi
                done
            done
        done
        if [ -d tools/SeedLab.Dumper/build ]; then printf '%s\n' tools/SeedLab.Dumper/build; fi
    ) | awk '!seen[$0]++'
}

do_remove_build() {
    refuse_root || return 3
    title 'SeedLab: remove the build'
    if core_uninstall_pending; then
        say 'Removing the build is the last step, and the uninstall has not been done yet: the vseed'
        say 'command is still set up, or the cache folder is still there, or the web server is running.'
        say 'Removing the build first would leave a vseed command that points at nothing.'
        if ! ask_yes_no 'Run the uninstall first (it lists what it does and asks)?' y; then say 'Nothing was removed.'; return 1; fi
        do_uninstall
        if [ $? -ne 0 ] || core_uninstall_pending; then
            say ''
            say 'The uninstall did not finish, so the build was not removed.'
            return 1
        fi
        title 'SeedLab: remove the build'
    fi
    drb_running=$(repo_procs)
    if [ -n "$drb_running" ]; then
        say 'These programs are running from this SeedLab folder:'
        printf '%s\n' "$drb_running" | show_procs
        if ! ask_yes_no 'Stop them?' n; then say 'Nothing was removed.'; return 1; fi
        printf '%s\n' "$drb_running" | stop_procs || { say 'Nothing was removed.'; return 1; }
    fi
    drb_dirs=$(build_output_dirs)
    if [ -z "$drb_dirs" ]; then
        say 'There is no build output in this folder. Nothing to remove.'
    else
        say 'These build folders will be DELETED for good (they are rebuilt exactly by "sh seedlab.sh install"):'
        drb_total=0
        while IFS= read -r drb_d; do
            drb_k=$(du -sk "$REPO/$drb_d" 2>/dev/null | awk '{ print $1 }')
            drb_total=$((drb_total + ${drb_k:-0}))
            say "  $drb_d   $(format_kb "${drb_k:-0}")"
        done <<EOF
$drb_dirs
EOF
        say "  total $(format_kb "$drb_total")"
        say 'The dumper'"'"'s build (tools/SeedLab.Dumper/build, if listed) can only be rebuilt with Valheim'
        say 'installed, because it is built against the game'"'"'s own files.'
        if ! ask_yes_no 'Delete them?' n; then say 'Nothing was removed.'; return 1; fi
        drb_failed=0
        while IFS= read -r drb_d; do
            if rm -rf "${REPO:?}/$drb_d"; then say "  deleted $drb_d"; else say "  could not delete $drb_d"; drb_failed=1; fi
        done <<EOF
$drb_dirs
EOF
        if [ $drb_failed -ne 0 ]; then say 'Some folders could not be deleted (see above).'; return 1; fi
        say 'The build is removed.'
    fi
    say ''
    say 'To remove SeedLab completely, delete the SeedLab folder itself, for example:'
    say "  rm -rf $(shell_quote "$REPO")"
    say '(A script cannot sensibly delete the folder it runs from.) If you ran searches from it, save'
    say 'its seedlab-results folder first.'
    return 0
}

# ------------------------------------------------------------------------------------------------
# help and the menu
# ------------------------------------------------------------------------------------------------

do_help() {
    say 'SeedLab for macOS and Linux:  sh seedlab.sh [action]   (no action: a menu)'
    say ''
    say '  install        check the .NET 10 SDK, build SeedLab, make "vseed" a command'
    say '  web [options]  the map and the search in your browser (options go to "vseed serve")'
    say '  stop [options] stop SeedLab'"'"'s web server (options go to "vseed serve --stop", e.g. --yes'
    say '                 to stop it even though a search is running; the search keeps its checkpoint)'
    say '  status         what is installed and what is running'
    say '  shell          a shell in the SeedLab folder in which vseed works'
    say '  uninstall      remove what SeedLab put outside its folder (keeps the build)'
    say '  remove-build   uninstall, then delete the build inside the folder'
    say '  --no-pause     never wait for Enter (for scripts)'
    say ''
    say 'On a Mac, double-click SeedLab.command in Finder for the menu.'
    say 'Each action first does any earlier step that has not happened yet, and says so. Nothing is'
    say 'removed or installed without asking. "stop" stops the web server at once unless a search is'
    say 'running in it, when it asks first. docs/scripts.md explains it all.'
    say 'This script has NOT been tested on macOS (no Mac was available); Linux testing is planned (WSL).'
    return 0
}

show_menu() {
    while :; do
        say ''
        say 'SeedLab - Valheim world generation, offline'
        say '==========================================='
        say '  1  Install or update     check .NET, build SeedLab, make "vseed" a command'
        say '  2  Open web page         the map and the search in your browser'
        say '  3  Stop web page'
        say '  4  SeedLab shell         a shell in which vseed works'
        say '  5  Uninstall             everything except the SeedLab folder and its build'
        say '  6  Remove the build      after uninstalling'
        say '  S  Status                what is installed and what is running'
        say '  H  Help'
        say '  Q  Quit'
        read_answer 'Type a number or letter, then press Enter: ' || return 0
        sm_code=
        case $ANSWER in
            1) do_install; sm_code=$? ;;
            2) do_web; sm_code=$? ;;
            3) do_stop; sm_code=$? ;;
            4) do_shell; sm_code=$? ;;
            5) do_uninstall; sm_code=$? ;;
            6) do_remove_build; sm_code=$? ;;
            s|S) do_status; sm_code=$? ;;
            h|H) do_help; sm_code=$? ;;
            q|Q) return 0 ;;
            '') continue ;;
            *) say 'That is not one of the choices.' ;;
        esac
        if [ -n "$sm_code" ] && [ -z "$NO_PAUSE" ]; then
            say ''
            read_answer 'Press Enter to go back to the menu. ' || return "$sm_code"
        fi
    done
}

# ------------------------------------------------------------------------------------------------
# main
# ------------------------------------------------------------------------------------------------

if [ "$OS" = other ]; then
    say 'This script is for macOS and Linux. On Windows, use SeedLab.bat (or the numbered'
    say '"SeedLab N - ....bat" files) in the same folder.'
    exit 2
fi

if [ -n "$TEST_MODE" ]; then
    say "[test mode: $(env | grep '^SEEDLAB_SCRIPT_TEST_' | cut -d= -f1 | LC_ALL=C sort | tr '\n' ' ')]"
    if [ -z "${SEEDLAB_SCRIPT_TEST_HOME-}" ] || [ "${SEEDLAB_SCRIPT_TEST_HOME-}" != "$HOME" ] || [ -z "${SEEDLAB_SCRIPT_TEST_CACHE-}" ]; then
        say 'Test mode needs SEEDLAB_SCRIPT_TEST_HOME (equal to HOME) and SEEDLAB_SCRIPT_TEST_CACHE; refusing to run.'
        exit 2
    fi
fi

ACTION=
for main_a in "$@"; do
    case $main_a in
        --no-pause) NO_PAUSE=1 ;;
        --help|-h) if [ -z "$ACTION" ]; then ACTION=help; fi ;;
        -*) ;;
        *) if [ -z "$ACTION" ]; then ACTION=$main_a; fi ;;
    esac
done
# Rebuild the argument list without the action and --no-pause, for "web" and "stop".
main_seen_action=
main_n=$#
while [ $main_n -gt 0 ]; do
    main_a=$1
    shift
    main_n=$((main_n - 1))
    case $main_a in
        --no-pause) continue ;;
    esac
    if [ -z "$main_seen_action" ] && [ "$main_a" = "$ACTION" ]; then main_seen_action=1; continue; fi
    if [ "$ACTION" = help ] && [ -z "$main_seen_action" ]; then case $main_a in --help|-h) main_seen_action=1; continue ;; esac; fi
    set -- "$@" "$main_a"
done

case ${ACTION:-menu} in
    web|stop) ;;
    *) if [ $# -gt 0 ]; then
           say "Unexpected: $*   (only \"web\" and \"stop\" take options, which go to vseed)"
           exit 2
       fi ;;
esac

DOTNET=
main_code=0
case ${ACTION:-menu} in
    menu) refuse_root || exit 3; show_menu; main_code=$? ;;
    install|update) do_install; main_code=$? ;;
    web) do_web "$@"; main_code=$? ;;
    stop) do_stop "$@"; main_code=$? ;;
    status) do_status; main_code=$? ;;
    shell) do_shell; main_code=$? ;;
    uninstall) do_uninstall; main_code=$? ;;
    remove-build) do_remove_build; main_code=$? ;;
    help) do_help; main_code=$? ;;
    *) say "Unknown action: $ACTION"; do_help; main_code=2 ;;
esac
exit $main_code
