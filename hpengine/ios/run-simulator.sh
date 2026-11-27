#!/bin/sh
# Installs and launches the simulator build on the simulator selected in Xcode (or the given UDID).
set -eu

# Signed copy written by build-simulator.sh.
app="$HOME/Library/Developer/HPEngine/ios-simulator/HPEngine.app"
device=${1:-${TARGET_DEVICE_IDENTIFIER:-}}
if [ -z "$device" ]; then
    device=$(xcrun simctl list devices booted | sed -n 's/.*(\([0-9A-F-]\{36\}\)) (Booted).*/\1/p' | head -n 1)
fi
if [ -z "$device" ]; then
    echo "No simulator selected: pass a UDID or run from the Xcode scheme." >&2
    exit 1
fi
test -f "$app/Info.plist"
bundle_id=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$app/Info.plist")

xcrun simctl boot "$device" 2>/dev/null || :
open -b com.apple.iphonesimulator --args -CurrentDeviceUDID "$device" 2>/dev/null || :
xcrun simctl bootstatus "$device" -b >/dev/null
xcrun simctl install "$device" "$app"
xcrun simctl launch --terminate-running-process "$device" "$bundle_id"
