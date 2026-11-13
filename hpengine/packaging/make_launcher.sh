#!/bin/sh
# Release build of the easy-install flow (README.md here, "Launcher and HP1Setup").
#
#   sh hpengine/packaging/make_launcher.sh [--out <dir>] [--reuse] [RID ...]
#
# Default RIDs: osx-arm64 osx-x64 win-x64 linux-x64. For each: HPEngine through publish.sh (--reuse keeps an already published
# <out>/HPEngine-<RID>), HP1Setup (launcher/HP1Setup: self-contained single file, trimmed) next to HPEngine with the
# installer unpacker in tools/ (launcher/third_party.sh: innoextract, pinned by SHA-256; macOS: built from source once
# and cached), the content check again (publish.sh --check), then
#   osx-arm64 + osx-x64 (both required, after the loop): <out>/HPEngine Launcher.app, universal (launcher/Launcher.swift
#              built for arm64 and x86_64 and joined with lipo; HPEngine with HP1Setup per architecture in
#              Contents/Resources/HPEngine (arm64) and Contents/Resources/HPEngine-x64; each slice of the Launcher must
#              name the runtime of its own architecture, checked through Launcher --runtime-path under arch -arm64 and
#              arch -x86_64, so the builder needs Rosetta) and <out>/HPEngine-Launcher-macOS.zip — the release asset the Launcher updates itself from — with
#              <out>/HPEngine-Launcher-macOS.zip.sig. HPENGINE_UPDATE_KEY = the release Ed25519 private key file
#              (launcher/update_sign.swift keygen; kept outside the repository): its public key is pinned into the app
#              and the zip is signed. Without it the app cannot self-update (it opens the release page) — a warning;
#   win-x64:   <out>/HPEngine-win-x64.zip;  linux-x64: <out>/HPEngine-linux-x64.tar.gz (keeps the exec bits). Layout of both:
#              the Launcher at the root ("HPEngine Launcher.exe" / "HPEngine-Launcher") + README.txt + HPEngine/ (the engine
#              folder: HPEngine, HP1Setup, tools/, .NET and SDL3 files). These are the RELEASE archives; publish.sh alone
#              (CI) makes the flat engine-only ones under the same names, so do not share an --out between the two.
# Version: launcher/VERSION, the release tag without "v" (a "-" suffix marks a pre-release); Info.plist HPEngineVersion,
# compared with the GitHub release tags; CFBundleShortVersionString is its numeric part.
# Needs .NET SDK 9 and network access for the pinned downloads (or a filled HPENGINE_TOOLS_CACHE); osx-* also needs
# an Apple Silicon Mac with Rosetta, Xcode Command Line Tools (swiftc, lipo, codesign, ditto) and cmake.
# Nothing of the game is packed: publish.sh --check refuses game files, and HP1Setup carries only SHA-1 hashes.
set -eu
HERE=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$HERE/../.." && pwd)
fail() { echo "FAILED: $*" >&2; exit 1; }
# As publish.sh RELEASE_PROPS: no debug symbols, source paths mapped (publish.sh --check refuses both in the package).
RELEASE_PROPS="-p:DebugType=none -p:DebugSymbols=false -p:PathMap=$REPO=/_/"

OUT="$REPO/.artifacts/hpengine-packages"; REUSE=0
while [ $# -gt 0 ]; do
    case "$1" in
        --out) [ $# -ge 2 ] || fail "--out without a directory"; OUT=$2; shift 2 ;;
        --reuse) REUSE=1; shift ;;
        *) break ;;
    esac
done
[ $# -gt 0 ] || set -- osx-arm64 osx-x64 win-x64 linux-x64
mkdir -p "$OUT"; OUT=$(cd "$OUT" && pwd)
V=$(cat "$REPO/launcher/VERSION")
sign() { xcrun swift "$REPO/launcher/update_sign.swift" "$@"; }
PUBKEY=""
case " $* " in *" osx-"*)
    case " $* " in *" osx-arm64 "*) ;; *) fail "the macOS Launcher is universal: osx-arm64 and osx-x64 go together" ;; esac
    case " $* " in *" osx-x64 "*) ;; *) fail "the macOS Launcher is universal: osx-arm64 and osx-x64 go together" ;; esac
    if [ -n "${HPENGINE_UPDATE_KEY:-}" ]; then PUBKEY=$(sign pub "$HPENGINE_UPDATE_KEY") || fail "HPENGINE_UPDATE_KEY: not a key"
    else echo "WARNING: HPENGINE_UPDATE_KEY not set — this Launcher cannot update itself and its zip is not signed" >&2; fi ;;
esac

for rid in "$@"; do
    dir="$OUT/HPEngine-$rid"
    if [ $REUSE = 1 ] && [ -d "$dir" ]; then sh "$HERE/publish.sh" --check "$dir" "$rid"
    else sh "$HERE/publish.sh" --out "$OUT" "$rid"; fi
    echo "== $rid: HP1Setup $V"
    dotnet publish "$REPO/launcher/HP1Setup/HP1Setup.csproj" -c Release -r "$rid" --self-contained true \
        -p:PublishSingleFile=true -p:PublishTrimmed=true -p:Version="$V" $RELEASE_PROPS -o "$OUT/HP1Setup-$rid" -nologo -v q \
        > "$OUT/hp1setup-$rid.log" 2>&1 || { tail -30 "$OUT/hp1setup-$rid.log"; fail "$rid: HP1Setup publish"; }
    case "$rid" in win-*) exe=HP1Setup.exe ;; *) exe=HP1Setup ;; esac
    cp "$OUT/HP1Setup-$rid/$exe" "$dir/$exe"
    sh "$REPO/launcher/third_party.sh" "$rid" "$dir" || fail "$rid: third-party tools"
    sh "$HERE/publish.sh" --check "$dir" "$rid"   # the engine folder: HPEngine + HP1Setup + tools, flat as publish.sh makes it
    case "$rid" in
        osx-*) continue ;;   # both osx packages go into one universal app after the loop
    esac
    # win/linux archive, laid out like the macOS app (everything inside, one thing to click):
    #   <Launcher>  +  HPEngine/ (the engine folder above)  [+ README.txt]
    # The desktop Launcher (Avalonia, one self-contained file) finds HPEngine/ beside itself; unpacked into the game folder the
    # engine finds the game in its parent (InstallRoot.cs, step 2b).
    echo "== $rid: Launcher $V"
    # Windows: the shield as the .exe icon in Explorer (the window icon is drawn in code, launcher/Desktop/ShieldIcon.cs).
    # The same drawing as the macOS app icon (launcher/icon.swift), so it needs a Mac to build; elsewhere a warning.
    ICON_PROP=""
    case "$rid" in win-*)
        if command -v xcrun > /dev/null 2>&1; then
            rm -rf "$OUT/.building/win.iconset"; mkdir -p "$OUT/.building"
            xcrun swift "$REPO/launcher/icon.swift" "$OUT/.building/win.iconset" || fail "icon.swift"
            # ICO with one PNG entry (256x256 is written as 0 in the directory).
            python3 -c 'import struct,sys; d=open(sys.argv[1],"rb").read(); open(sys.argv[2],"wb").write(struct.pack("<HHHBBBBHHII",0,1,1,0,0,0,0,1,32,len(d),22)+d)' \
                "$OUT/.building/win.iconset/icon_256x256.png" "$OUT/.building/launcher.ico" || fail "launcher.ico"
            rm -rf "$OUT/.building/win.iconset"
            ICON_PROP="-p:ApplicationIcon=$OUT/.building/launcher.ico"
        else
            echo "   WARNING: no xcrun here: 'HPEngine Launcher.exe' gets no icon in Explorer"
        fi ;;
    esac
    dotnet publish "$REPO/launcher/Desktop/HPEngineLauncher.csproj" -c Release -r "$rid" --self-contained true \
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
        $ICON_PROP $RELEASE_PROPS -o "$OUT/Launcher-$rid" -nologo -v q > "$OUT/launcher-$rid.log" 2>&1 \
        || { tail -30 "$OUT/launcher-$rid.log"; fail "$rid: Launcher publish"; }
    stage="$OUT/archive-$rid"; rm -rf "$stage"; mkdir -p "$stage"
    cp -R "$dir" "$stage/HPEngine" || fail "$rid: staging"
    case "$rid" in
        win-*) cp "$OUT/Launcher-$rid/HPEngineLauncher.exe" "$stage/HPEngine Launcher.exe"; first="HPEngine Launcher.exe" ;;
        *)     cp "$OUT/Launcher-$rid/HPEngineLauncher" "$stage/HPEngine-Launcher"; chmod +x "$stage/HPEngine-Launcher"; first=HPEngine-Launcher ;;
    esac
    cat > "$stage/README.txt" <<TXT
HPEngine $V

Run "$first". It installs the game from your own copy (folder, CD, installer or .zip) and starts it.
Keep the HPEngine folder next to the launcher; you can put both anywhere (no admin rights needed).

Manual way: unpack this archive INTO the folder of your installed game (where System and Maps are) and run
"$first" there (it then works with that game), or HPEngine/HPEngine directly.

Coming from 0.1.0 (OpenHP1)? CHANGELOG.md on the project page tells how to move your saves.
TXT
    sh "$HERE/publish.sh" --scan "$stage"
    case "$rid" in
        win-*)
            arc="$OUT/HPEngine-$rid.zip"; rm -f "$arc"
            (cd "$stage" && zip -qr -X "$arc" .) || fail "$rid: zip" ;;
        *)
            arc="$OUT/HPEngine-$rid.tar.gz"; rm -f "$arc"
            if [ "$(uname)" = Darwin ]; then m=--no-mac-metadata; else m=; fi   # as publish.sh tgz
            tar --no-xattrs $m -C "$stage" -czf "$arc" . || fail "$rid: tar"
            sh "$HERE/publish.sh" --check-archive "$arc" ;;
    esac
    echo "   $rid: $(du -h "$arc" | cut -f1) -> $arc"
done
# The universal macOS Launcher: one .app, one zip (the asset name the installed Launchers update from stays the same).
case " $* " in *" osx-"*)
    NAME="HPEngine Launcher"; FINAL="$OUT/$NAME.app"; APP="$OUT/.building/$NAME.app"
    rm -rf "$OUT/.building"; mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
    for a in arm64 x86_64; do
        xcrun swiftc -O -parse-as-library -target $a-apple-macos13.0 "$REPO/launcher/Launcher.swift" \
            -o "$OUT/.building/Launcher-$a" || fail "swiftc $a"
    done
    lipo -create "$OUT/.building/Launcher-arm64" "$OUT/.building/Launcher-x86_64" -output "$APP/Contents/MacOS/Launcher" \
        || fail "lipo"
    rm -f "$OUT/.building/Launcher-arm64" "$OUT/.building/Launcher-x86_64"
    cp -R "$OUT/HPEngine-osx-arm64" "$APP/Contents/Resources/HPEngine"
    cp -R "$OUT/HPEngine-osx-x64" "$APP/Contents/Resources/HPEngine-x64"
    # Each slice runs the runtime of its own architecture, and that runtime is entirely of that architecture.
    for a in arm64 x86_64; do
        rt=$(arch -$a "$APP/Contents/MacOS/Launcher" --runtime-path) || fail "Launcher --runtime-path under arch -$a"
        for f in HPEngine HP1Setup tools/innoextract libSDL3.dylib; do
            got=$(lipo -archs "$rt/$f" 2> /dev/null) || fail "$a slice: no Mach-O $rt/$f"
            [ "$got" = "$a" ] || fail "$a slice runs $rt/$f built for $got"
        done
        echo "   Launcher $a slice -> Resources/$(basename "$rt") ($a)"
    done
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
	<key>CFBundleIdentifier</key><string>io.github.hpengine.launcher</string>
	<key>CFBundleName</key><string>$NAME</string>
	<key>CFBundlePackageType</key><string>APPL</string>
	<key>CFBundleShortVersionString</key><string>${V%%-*}</string>
	<key>HPEngineVersion</key><string>$V</string>${PUBKEY:+
	<key>HPEngineUpdateKey</key><string>$PUBKEY</string>}
	<key>LSMinimumSystemVersion</key><string>13.0</string>
	<key>LSApplicationCategoryType</key><string>public.app-category.games</string>
	<key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PL
    # Every Mach-O inside Resources (both runtimes) is signed on its own first (codesign --deep only walks standard places).
    find "$APP/Contents/Resources" -type f \( -perm -u+x -o -name '*.dylib' \) \
        -exec codesign --force -s - {} \; > /dev/null 2>&1
    codesign --force --deep -s - "$APP" || fail "codesign"
    sh "$HERE/publish.sh" --scan "$APP"
    rm -rf "$FINAL"; mv "$APP" "$FINAL"; rmdir "$OUT/.building"
    arc="$OUT/HPEngine-Launcher-macOS.zip"; rm -f "$arc"
    (cd "$OUT" && ditto -c -k --keepParent "$NAME.app" "$arc") || fail "ditto"
    rm -f "$arc.sig"
    if [ -n "$PUBKEY" ]; then sign sign "$HPENGINE_UPDATE_KEY" "$arc" || fail "signing the zip"; fi
    echo "   macOS: $(du -h "$arc" | cut -f1) -> $arc" ;;
esac
echo "make_launcher: built $*"
