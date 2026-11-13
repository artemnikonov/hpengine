#!/bin/sh
# Builds HPEngine packages to be installed over the user's game.
#
#   sh hpengine/packaging/publish.sh [--out <dir>] [RID ...]
#       Default RIDs: win-x64 linux-x64 osx-arm64 osx-x64. For each:
#       self-contained dotnet publish -> <out>/HPEngine-<RID>/, content check,
#       archive <out>/HPEngine-<RID>.zip (win-*) or .tar.gz (others; keeps the exec bit).
#       Default <out>: .artifacts/hpengine-packages at the repo root (not in git, and outside the
#       project directory so its output is not caught by HPEngine.csproj globs).
#
#   sh hpengine/packaging/publish.sh --check <dir> <RID>
#       only checks the content of a finished directory (also used by PackagingTests).
#
#   sh hpengine/packaging/publish.sh --scan <dir>
#       only the personal-data part of the check, for any directory (make_launcher.sh: the macOS Launcher .app).
#
# The package has NO game files and never may (author decision): the check fails the build if the
# output contains original packages/maps/ini, install directories (System, Maps, ... — unpacked over
# the game they would merge with its directories), tests or sources.
#
# The archive is unpacked INTO the game install root: files have no wrapper directory, so the
# executable lands next to System/ and Maps/ (InstallRoot.cs, step 2).
#
# Exit codes: 0 — everything built and checked; 1 — failure (reason named).
set -u

HERE=$(cd "$(dirname "$0")" && pwd)
PROJ="$HERE/../HPEngine.csproj"
REPO=$(cd "$HERE/../.." && pwd)
RELEASE_PROPS="-p:DebugType=none -p:DebugSymbols=false -p:PathMap=$REPO=/_/"

fail() { echo "FAILED: $*" >&2; exit 1; }

# check_output <dir> <RID> — content of a published directory.
check_output()
{
    dir=$1 rid=$2
    [ -d "$dir" ] || fail "$rid: directory $dir missing"
    case "$rid" in
        win-*)   exe=HPEngine.exe; sdl=SDL3.dll ;;
        linux-*) exe=HPEngine;     sdl=libSDL3.so ;;
        osx-*)   exe=HPEngine;     sdl=libSDL3.dylib ;;
        *)       fail "$rid: unknown RID family (expected win-*, linux-*, osx-*)" ;;
    esac
    [ -f "$dir/HPEngine.dll" ] || fail "$rid: missing HPEngine.dll"
    [ -f "$dir/$exe" ] || fail "$rid: missing executable $exe"
    case "$rid" in win-*) ;; *) [ -x "$dir/$exe" ] || fail "$rid: $exe without the execute bit" ;; esac
    [ -s "$dir/$sdl" ] || fail "$rid: missing native SDL3 ($sdl) — the ppy.SDL3-CS package gave no library for this RID"
    [ -f "$dir/HPEngine.runtimeconfig.json" ] || fail "$rid: missing HPEngine.runtimeconfig.json"
    grep -q '"includedFrameworks"' "$dir/HPEngine.runtimeconfig.json" \
        || fail "$rid: the build is not self-contained (runtimeconfig has no includedFrameworks)"
    # Game install and source-tree directories, case-insensitive (Windows/macOS ignore case).
    bad=$(find "$dir" -mindepth 1 -type d | while IFS= read -r d; do
        n=$(basename "$d" | tr 'A-Z' 'a-z')
        case "$n" in
            (system|maps|textures|sounds|music|save|tests) echo "$d" ;;
        esac
    done)
    [ -z "$bad" ] || fail "$rid: the output has game install/source directories: $bad"
    # Original files (packages, maps, ini, saves) and tree sources/data.
    bad=$(find "$dir" -type f | while IFS= read -r f; do
        n=$(basename "$f" | tr 'A-Z' 'a-z')
        case "$n" in
            (*.u|*.unr|*.utx|*.uax|*.umx|*.usx|*.ukx|*.int|*.ini|*.sav|*.dll.c) echo "$f" ;;
            (*.cs|*.csproj|*.hlsl|*.md|*.sh|*.tsv|*.yml) echo "$f" ;;
        esac
    done)
    [ -z "$bad" ] || fail "$rid: the output has game files/sources: $bad"
    scan_private "$dir" "$rid"
}

# scan_private <dir> <label> — debug symbols and personal data of the build machine in any file under <dir>. Home paths
# of the users "runner" (the .NET runtime pack is built by Microsoft CI agents) and "phablet" (SDL3's Ubuntu Touch
# path) are allowed. So are the upstream authors'
# addresses compiled into innoextract and its libraries (innoextract, bzip2, zlib; tools/, launcher/third_party.sh).
# The [s]/[o]/[-] classes keep these patterns from being personal paths / the private repository name in this
# (exported) file.
scan_private()
{
    bad=$(find "$1" -type f -iname '*.pdb')
    [ -z "$bad" ] || fail "$2: the output has debug symbols: $bad"
    home='(/U[s]ers/|/h[o]me/|[A-Za-z]:[\\/]U[s]ers[\\/])'
    upstream='daniel@constexpr\.org|bzip2-devel@sourceware\.org|jseward@bzip\.org|jloup@gzip\.org|madler@alumni\.caltech\.edu'
    # .NET string literals are UTF-16 in the assembly: their ASCII runs (both byte alignments) go to a side folder that
    # the same search reads.
    u16=$(mktemp -d) || fail "$2: mktemp"
    python3 - "$1" "$u16" <<'PY' || { rm -rf "$u16"; fail "$2: UTF-16 string scan failed"; }
import os, re, sys
src, out = sys.argv[1], sys.argv[2]
run = re.compile(rb'(?:[\x20-\x7e]\x00){4,}')
for root, _, files in os.walk(src):
    for f in files:
        p = os.path.join(root, f)
        try: b = open(p, 'rb').read()
        except OSError: continue
        found = [m.group().decode('utf-16le') for k in (0, 1) for m in run.finditer(b, k)]
        if found:
            q = os.path.join(out, os.path.relpath(p, src) + '.utf16')
            os.makedirs(os.path.dirname(q), exist_ok=True)
            open(q, 'w', encoding='ascii').write('\n'.join(found) + '\n')
PY
    # E-mail: at least 3 characters before "@" and 2 in the first domain label. Shorter shapes occur by chance in the
    # composite ReadyToRun native code (2026-11-12, osx-x64 HPEngine.r2r.dll: one letter, "@", one letter, ".gj" between non-printable
    # ones) and would fail a release at random; a real address that short is not a builder's.
    bad=$(LC_ALL=C grep -r -o -a -E \
        "$home[A-Za-z0-9._-]+|a\\.nikonov|hp1[-]core|[A-Za-z0-9._%+-]{3,}@[A-Za-z0-9-]{2,}(\\.[A-Za-z0-9-]+)*\\.[a-z]{2,}" \
        "$1" "$u16" | grep -v -E ":(${home}(runner|phablet)|$upstream)\$" | sort -u)
    rm -rf "$u16"
    [ -z "$bad" ] || fail "$2: the output has personal paths/names/e-mail of the build machine: $bad"
    return 0
}

# Linux/macOS archives without file-system metadata of the build machine: macOS tar (bsdtar) otherwise stores its
# extended attributes (com.apple.provenance, code-signature xattrs) as pax headers, and GNU tar on Linux / Steam Deck
# prints "Ignoring unknown extended header keyword" for each of them (v0.1.0-beta's linux archive had 207).
tgz() {   # tgz <dir> <archive>
    _m=; [ "$(uname)" = Darwin ] && _m=--no-mac-metadata
    tar --no-xattrs $_m -C "$1" -czf "$2" . || return 1
    check_archive "$2"
}
check_archive() {   # check_archive <archive>: no pax header naming an extended attribute
    python3 - "$1" <<'PY' || fail "$1: the archive carries file-system extended attributes of the build machine"
import sys, tarfile
bad = [m.name for m in tarfile.open(sys.argv[1]) if any('xattr' in k for k in (m.pax_headers or {}))]
if bad: print(f"{len(bad)} members with xattr pax headers, e.g. {bad[0]}"); sys.exit(1)
PY
}

if [ "${1:-}" = "--check-archive" ]; then
    [ $# -eq 2 ] || fail "usage: publish.sh --check-archive <archive>"
    check_archive "$2"; echo "archive $2 — ok"; exit 0
fi
if [ "${1:-}" = "--check" ]; then
    [ $# -eq 3 ] || fail "usage: publish.sh --check <directory> <RID>"
    check_output "$2" "$3"
    echo "check $3: $2 — ok"
    exit 0
fi
if [ "${1:-}" = "--scan" ]; then
    [ $# -eq 2 ] || fail "usage: publish.sh --scan <directory>"
    scan_private "$2" "$2"
    exit 0
fi

OUT="$HERE/../../.artifacts/hpengine-packages"
if [ "${1:-}" = "--out" ]; then
    [ $# -ge 2 ] || fail "--out without a directory"
    OUT=$2; shift 2
fi
[ $# -gt 0 ] || set -- win-x64 linux-x64 osx-arm64 osx-x64
mkdir -p "$OUT" || fail "cannot create $OUT"
OUT=$(cd "$OUT" && pwd)

for rid in "$@"; do
    dir="$OUT/HPEngine-$rid"
    rm -rf "$dir"
    echo "== $rid: dotnet publish"
    dotnet publish "$PROJ" -c Release -r "$rid" --self-contained true $RELEASE_PROPS -o "$dir" -nologo -v q \
        > "$OUT/publish-$rid.log" 2>&1 || { tail -30 "$OUT/publish-$rid.log"; fail "$rid: dotnet publish"; }
    # License texts next to the program: HPEngine (GPL-3.0-or-later, with the source link) and the bundled third-party
    # components (SDL3, SDL3-CS, UELib, .NET, innoextract, ...).
    # The texts live in public/ here and at the root of the public repository.
    lic="$HERE/../../public/LICENSE"; [ -f "$lic" ] || lic="$HERE/../../LICENSE"
    notices="$HERE/../../public/THIRD_PARTY_NOTICES.md"; [ -f "$notices" ] || notices="$HERE/../../THIRD_PARTY_NOTICES.md"
    { cat <<'TXT'
HPEngine
Copyright (C) 2026 Artem Nikonov and HPEngine contributors

This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public
License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later
version. It is distributed WITHOUT ANY WARRANTY; see the license text below.

Source code: https://github.com/artemnikonov/hpengine

The license covers HPEngine's own code only and grants no rights to the original game or its files. Third-party
components keep their own licenses (THIRD_PARTY_NOTICES.txt).

TXT
      cat "$lic"; } > "$dir/LICENSE.txt" || fail "$rid: LICENSE"
    cp "$notices" "$dir/THIRD_PARTY_NOTICES.txt" || fail "$rid: notices"
    check_output "$dir" "$rid"
    case "$rid" in
        win-*)
            arc="$OUT/HPEngine-$rid.zip"; rm -f "$arc"
            (cd "$dir" && zip -qr -X "$arc" .) || fail "$rid: zip" ;;
        *)
            arc="$OUT/HPEngine-$rid.tar.gz"; rm -f "$arc"
            tgz "$dir" "$arc" || fail "$rid: tar" ;;
    esac
    files=$(find "$dir" -type f | wc -l | tr -d ' ')
    echo "   $rid: files $files, directory $(du -sh "$dir" | cut -f1), archive $(du -h "$arc" | cut -f1) -> $arc"
done
echo "publish: built and checked: $*"
