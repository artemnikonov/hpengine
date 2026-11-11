#!/bin/sh
# Release build of the easy-install flow (README.md here, "Launcher and HP1Setup").
#
#   sh openhp1/packaging/make_launcher.sh [--out <dir>] [--reuse] [RID ...]
#
# Default RIDs: osx-arm64 win-x64 linux-x64. For each: OpenHP1 through publish.sh (--reuse keeps an already published
# <out>/OpenHP1-<RID>), HP1Setup (launcher/HP1Setup: self-contained single file, trimmed) next to OpenHP1 with the
# installer unpacker in tools/ (launcher/third_party.sh: innoextract, pinned by SHA-256; macOS: built from source once
# and cached), the content check again (publish.sh --check), then
#   osx-arm64: <out>/OpenHP1 Launcher.app (launcher/Launcher.swift; OpenHP1 with HP1Setup in Contents/Resources/OpenHP1)
#              and <out>/OpenHP1-Launcher-macOS.zip — the release asset the Launcher updates itself from — with
#              <out>/OpenHP1-Launcher-macOS.zip.sig. OPENHP1_UPDATE_KEY = the release Ed25519 private key file
#              (launcher/update_sign.swift keygen; kept outside the repository): its public key is pinned into the app
#              and the zip is signed. Without it the app cannot self-update (it opens the release page) — a warning;
#   win-x64:   <out>/OpenHP1-win-x64.zip;  linux-x64: <out>/OpenHP1-linux-x64.tar.gz (keeps the exec bits).
# Version: launcher/VERSION, the release tag without "v" (a "-" suffix marks a pre-release); Info.plist OpenHP1Version,
# compared with the GitHub release tags; CFBundleShortVersionString is its numeric part.
# Needs .NET SDK 9 and network access for the pinned downloads (or a filled OPENHP1_TOOLS_CACHE); osx-arm64 also needs
# a Mac with Xcode Command Line Tools (swiftc, codesign, ditto) and cmake.
# Nothing of the game is packed: publish.sh --check refuses game files, and HP1Setup carries only SHA-1 hashes.
set -eu
HERE=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$HERE/../.." && pwd)
fail() { echo "FAILED: $*" >&2; exit 1; }
# As publish.sh RELEASE_PROPS: no debug symbols, source paths mapped (publish.sh --check refuses both in the package).
RELEASE_PROPS="-p:DebugType=none -p:DebugSymbols=false -p:PathMap=$REPO=/_/"

OUT="$REPO/.artifacts/openhp1-packages"; REUSE=0
while [ $# -gt 0 ]; do
    case "$1" in
        --out) [ $# -ge 2 ] || fail "--out without a directory"; OUT=$2; shift 2 ;;
        --reuse) REUSE=1; shift ;;
        *) break ;;
    esac
done
[ $# -gt 0 ] || set -- osx-arm64 win-x64 linux-x64
mkdir -p "$OUT"; OUT=$(cd "$OUT" && pwd)
V=$(cat "$REPO/launcher/VERSION")
sign() { xcrun swift "$REPO/launcher/update_sign.swift" "$@"; }
PUBKEY=""
case " $* " in *" osx-"*)
    if [ -n "${OPENHP1_UPDATE_KEY:-}" ]; then PUBKEY=$(sign pub "$OPENHP1_UPDATE_KEY") || fail "OPENHP1_UPDATE_KEY: not a key"
    else echo "WARNING: OPENHP1_UPDATE_KEY not set — this Launcher cannot update itself and its zip is not signed" >&2; fi ;;
esac

for rid in "$@"; do
    dir="$OUT/OpenHP1-$rid"
    if [ $REUSE = 1 ] && [ -d "$dir" ]; then sh "$HERE/publish.sh" --check "$dir" "$rid"
    else sh "$HERE/publish.sh" --out "$OUT" "$rid"; fi
    echo "== $rid: HP1Setup $V"
    dotnet publish "$REPO/launcher/HP1Setup/HP1Setup.csproj" -c Release -r "$rid" --self-contained true \
        -p:PublishSingleFile=true -p:PublishTrimmed=true -p:Version="$V" $RELEASE_PROPS -o "$OUT/HP1Setup-$rid" -nologo -v q \
        > "$OUT/hp1setup-$rid.log" 2>&1 || { tail -30 "$OUT/hp1setup-$rid.log"; fail "$rid: HP1Setup publish"; }
    case "$rid" in win-*) exe=HP1Setup.exe ;; *) exe=HP1Setup ;; esac
    cp "$OUT/HP1Setup-$rid/$exe" "$dir/$exe"
    sh "$REPO/launcher/third_party.sh" "$rid" "$dir" || fail "$rid: third-party tools"
    sh "$HERE/publish.sh" --check "$dir" "$rid"
    case "$rid" in
        osx-*)
            NAME="OpenHP1 Launcher"; FINAL="$OUT/$NAME.app"; APP="$OUT/.building/$NAME.app"
            rm -rf "$OUT/.building"; mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
            xcrun swiftc -O -parse-as-library -target arm64-apple-macos13.0 "$REPO/launcher/Launcher.swift" \
                -o "$APP/Contents/MacOS/Launcher" || fail "swiftc"
            cp -R "$dir" "$APP/Contents/Resources/OpenHP1"
            xcrun swift "$REPO/launcher/icon.swift" "$OUT/.building/AppIcon.iconset" || fail "icon.swift"
            iconutil -c icns "$OUT/.building/AppIcon.iconset" -o "$APP/Contents/Resources/AppIcon.icns" || fail "iconutil"
            rm -rf "$OUT/.building/AppIcon.iconset"
            cat > "$APP/Contents/Info.plist" <<PL
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
	<key>CFBundleExecutable</key><string>Launcher</string>
	<key>CFBundleIconFile</key><string>AppIcon</string>
	<key>CFBundleIdentifier</key><string>io.github.openhp1.launcher</string>
	<key>CFBundleName</key><string>$NAME</string>
	<key>CFBundlePackageType</key><string>APPL</string>
	<key>CFBundleShortVersionString</key><string>${V%%-*}</string>
	<key>OpenHP1Version</key><string>$V</string>${PUBKEY:+
	<key>OpenHP1UpdateKey</key><string>$PUBKEY</string>}
	<key>LSMinimumSystemVersion</key><string>13.0</string>
	<key>LSApplicationCategoryType</key><string>public.app-category.games</string>
	<key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PL
            # Every Mach-O inside Resources is signed on its own first (codesign --deep only walks standard places).
            find "$APP/Contents/Resources/OpenHP1" -type f \( -perm -u+x -o -name '*.dylib' \) \
                -exec codesign --force -s - {} \; > /dev/null 2>&1
            codesign --force --deep -s - "$APP" || fail "codesign"
            sh "$HERE/publish.sh" --scan "$APP"
            rm -rf "$FINAL"; mv "$APP" "$FINAL"; rmdir "$OUT/.building"
            arc="$OUT/OpenHP1-Launcher-macOS.zip"; rm -f "$arc"
            (cd "$OUT" && ditto -c -k --keepParent "$NAME.app" "$arc") || fail "ditto"
            rm -f "$arc.sig"
            if [ -n "$PUBKEY" ]; then sign sign "$OPENHP1_UPDATE_KEY" "$arc" || fail "signing the zip"; fi ;;
        win-*)
            arc="$OUT/OpenHP1-$rid.zip"; rm -f "$arc"
            (cd "$dir" && zip -qr -X "$arc" .) || fail "$rid: zip" ;;
        *)
            arc="$OUT/OpenHP1-$rid.tar.gz"; rm -f "$arc"
            tar -C "$dir" -czf "$arc" . || fail "$rid: tar" ;;
    esac
    echo "   $rid: $(du -h "$arc" | cut -f1) -> $arc"
done
echo "make_launcher: built $*"
