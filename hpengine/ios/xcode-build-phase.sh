#!/bin/sh
# Run Script phase of the HPEngine Xcode app target. Builds the .NET app for the selected destination and
# copies it into Xcode's product bundle. Xcode then signs that bundle with the team and certificate chosen
# under Signing & Capabilities, and Run installs and launches it.
set -eu

case "$PLATFORM_NAME" in
    iphoneos) rid=ios-arm64 ;;
    iphonesimulator) rid=iossimulator-arm64 ;;
    *) echo "error: unsupported platform $PLATFORM_NAME" >&2; exit 1 ;;
esac

# Xcode exports every build setting (PRODUCT_NAME, TARGET_NAME, ...); MSBuild would read them as properties.
env -i HOME="$HOME" PATH="/usr/bin:/bin:/usr/sbin:/sbin" TMPDIR="${TMPDIR:-/tmp}" DEVELOPER_DIR="$DEVELOPER_DIR" \
    HPENGINE_BUILD_ONLY=1 /bin/sh "$SRCROOT/hpengine/ios/build-simulator.sh" "$rid"

built_dir="$SRCROOT/.artifacts/ios-simulator/app"
[ "$rid" = iossimulator-arm64 ] || built_dir="$SRCROOT/.artifacts/ios-simulator/app-$rid"
app="$TARGET_BUILD_DIR/$WRAPPER_NAME"
# Sync instead of replacing: Xcode has already put the provisioning profile (and Info.plist) into the bundle.
mkdir -p "$app"
rsync -a --delete --exclude=/embedded.mobileprovision --exclude=/Info.plist --exclude=/content-server.json \
    "$built_dir/HPEngine.app/" "$app/"
xattr -cr "$app"
# Development download server on this Mac (content-server.sh setup): the app fetches a missing game from it.
server_config="$HOME/Library/Application Support/HPEngine Content Server/app-config.json"
if [ -f "$server_config" ]; then
    cp "$server_config" "$app/content-server.json"
else
    echo "warning: no content server configured (hpengine/ios/content-server.sh setup); the app will not download games"
fi
# The .NET Info.plist becomes the target's INFOPLIST_FILE; Xcode processes it into the bundle.
mkdir -p "$DERIVED_FILE_DIR"
cp "$built_dir/HPEngine.app/Info.plist" "$DERIVED_FILE_DIR/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleIdentifier $PRODUCT_BUNDLE_IDENTIFIER" "$DERIVED_FILE_DIR/Info.plist"

# App icon: the launcher's original artwork (launcher/icon.swift --ios; no game file is used), drawn again only when
# icon.swift changes, compiled by actool into the bundle (Assets.car and the PNGs it writes; the sync above removes them, so
# every build compiles them), its icon keys merged into the Info.plist Xcode processes.
icons="$DERIVED_FILE_DIR/AppIcon.xcassets"
icon_png="$icons/AppIcon.appiconset/icon_1024.png"
if [ ! -f "$icon_png" ] || [ "$SRCROOT/launcher/icon.swift" -nt "$icon_png" ]; then
    mkdir -p "$icons/AppIcon.appiconset"
    env -i HOME="$HOME" PATH="/usr/bin:/bin:/usr/sbin:/sbin" TMPDIR="${TMPDIR:-/tmp}" DEVELOPER_DIR="$DEVELOPER_DIR"         xcrun swift "$SRCROOT/launcher/icon.swift" --ios "$icon_png"
    printf '%s\n' '{ "images": [ { "idiom": "universal", "platform": "ios", "size": "1024x1024", "filename": "icon_1024.png" } ],' \
        '  "info": { "author": "xcode", "version": 1 } }' > "$icons/AppIcon.appiconset/Contents.json"
    printf '%s\n' '{ "info": { "author": "xcode", "version": 1 } }' > "$icons/Contents.json"
fi
xcrun actool "$icons" --compile "$app" --app-icon AppIcon --platform "$PLATFORM_NAME" \
    --minimum-deployment-target "$IPHONEOS_DEPLOYMENT_TARGET" --target-device iphone --target-device ipad \
    --output-partial-info-plist "$DERIVED_FILE_DIR/icon-info.plist" --errors --warnings > "$DERIVED_FILE_DIR/actool.log"
/usr/libexec/PlistBuddy -c "Merge $DERIVED_FILE_DIR/icon-info.plist" "$DERIVED_FILE_DIR/Info.plist"

# Xcode signs the app bundle after this phase; nested code must be signed first with the same identity.
if [ "${CODE_SIGNING_ALLOWED:-YES}" = YES ]; then
    identity=${EXPANDED_CODE_SIGN_IDENTITY:-}
    [ -n "$identity" ] || identity=-
    find "$app" \( -name '*.dylib' -o -name '*.framework' \) -prune -print | while IFS= read -r item; do
        codesign --force --sign "$identity" --timestamp=none "$item"   # identifier: the framework's CFBundleIdentifier
    done
    # Xcode skips its own CodeSign step when it thinks nothing changed, but the sync above may have replaced the
    # signed executable. Sign the bundle here the way Xcode does (same identity and entitlements); Xcode's CodeSign,
    # when it runs, signs again. On the first build there is no processed Info.plist yet and Xcode's step always runs.
    xcent="$TARGET_TEMP_DIR/$WRAPPER_NAME.xcent"
    [ -f "$xcent" ] || xcent="$TARGET_TEMP_DIR/$WRAPPER_NAME-Simulated.xcent"
    if [ -f "$app/Info.plist" ] && [ -f "$xcent" ]; then
        codesign --force --sign "$identity" --entitlements "$xcent" --timestamp=none --generate-entitlement-der "$app"
    fi
fi
