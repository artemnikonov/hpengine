#!/bin/sh
# Wraps a published osx-arm64 HPEngine directory into a macOS .app (no game files inside).
#   sh hpengine/packaging/make_app.sh <published HPEngine dir> <out dir>
# The launcher passes --root: the folder HARRY_POTTER_US_ next to HP1.app, or $HP1_GAME_ROOT.
set -e
SRC=$1; OUT=$2; [ -x "$SRC/HPEngine" ] || { echo "make_app: no HPEngine in $SRC" >&2; exit 2; }
A=$OUT/HP1.app; rm -rf "$A"; mkdir -p "$A/Contents/MacOS" "$A/Contents/Resources"
cp -R "$SRC" "$A/Contents/Resources/HPEngine"
cat > "$A/Contents/MacOS/HP1" <<'E'
#!/bin/sh
APP="$(cd "$(dirname "$0")/../.." && pwd)"
ROOT="${HP1_GAME_ROOT:-$(dirname "$APP")/HARRY_POTTER_US_}"
exec "$APP/Contents/Resources/HPEngine/HPEngine" --root "$ROOT" "$@"
E
chmod +x "$A/Contents/MacOS/HP1"
VER=$(cut -d' ' -f1 "$SRC/BUILD_COMMIT" 2>/dev/null || echo dev)
cat > "$A/Contents/Info.plist" <<E
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleName</key><string>HP1</string>
<key>CFBundleDisplayName</key><string>Harry Potter (HPEngine)</string>
<key>CFBundleIdentifier</key><string>local.hpengine</string>
<key>CFBundleExecutable</key><string>HP1</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleShortVersionString</key><string>$VER</string>
<key>LSMinimumSystemVersion</key><string>12.0</string>
<key>NSHighResolutionCapable</key><true/>
</dict></plist>
E
echo "make_app: $A"
