#!/bin/sh
# SeedLab for macOS: double-click this file in Finder to open SeedLab's menu in Terminal.
# It runs seedlab.sh, which sits next to it; docs/scripts.md explains everything.
# (Not tested on a Mac: none was available.)
cd "$(dirname "$0")" || exit 1
exec /bin/sh ./seedlab.sh "$@"
