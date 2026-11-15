#!/bin/sh
# Checks of the Windows/Linux Launcher (launcher/Desktop): HP1Setup protocol parsing, Mods.ini round-trip, data directory
# per OS, release choice, self-update (signature, swap, rollback, helper), Add to Steam (binary VDF), title by edition, and a
# headless Avalonia window in both states (no window opens). Needs .NET SDK 9 (+ NuGet).
#   sh launcher/test_desktop.sh
# On macOS with swiftc the signature of the release signer (launcher/update_sign.swift) is checked against the C# verifier too.
# Red mutations: sh launcher/test_desktop_redtest.sh. Exit: 0 all passed, 1 a check failed, 2 build, 3 NOVERDICT.
HERE=$(cd "$(dirname "$0")" && pwd)
if [ "$(uname)" = Darwin ] && command -v xcrun > /dev/null 2>&1; then
    X=$(mktemp -d); trap 'rm -rf "$X"' EXIT
    if xcrun swiftc -O "$HERE/update_sign.swift" -o "$X/sign" > /dev/null 2>&1; then
        PUB=$("$X/sign" keygen "$X/key") && head -c 3000 /dev/urandom > "$X/file" && "$X/sign" sign "$X/key" "$X/file" \
            && HP1_XSIGN="$PUB|$X/file|$X/file.sig" && export HP1_XSIGN
    fi
fi
dotnet run --project "$HERE/Desktop.Tests" -c Release
