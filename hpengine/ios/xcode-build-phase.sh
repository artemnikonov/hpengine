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
# The .NET Info.plist becomes the target's INFOPLIST_FILE; Xcode processes it into the bundle.
mkdir -p "$DERIVED_FILE_DIR"
cp "$built_dir/HPEngine.app/Info.plist" "$DERIVED_FILE_DIR/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleIdentifier $PRODUCT_BUNDLE_IDENTIFIER" "$DERIVED_FILE_DIR/Info.plist"

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
