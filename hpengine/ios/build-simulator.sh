#!/bin/sh
# Usage: build-simulator.sh [runtime identifier]   (default iossimulator-arm64; ios-arm64 for a device)
# HPENGINE_BUILD_ONLY=1 stops after the .NET build (the Xcode target copies and signs the app itself).
set -eu
rid=${1:-iossimulator-arm64}

repo=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
build_dir="$repo/.artifacts/ios-simulator"
mkdir -p "$build_dir"

dotnet="$build_dir/dotnet/dotnet"
if [ ! -d "$build_dir/dotnet/sdk/11.0.100" ]; then
    curl -fsSL --retry 2 https://dot.net/v1/dotnet-install.sh -o "$build_dir/dotnet-install.sh"
    bash "$build_dir/dotnet-install.sh" --version 11.0.100 --architecture arm64 --install-dir "$build_dir/dotnet"
fi

# Use the matching SDK even when the shell has a different .NET version on PATH.
printf '%s\n' '{"sdk":{"version":"11.0.100","rollForward":"disable"}}' > "$build_dir/global.json"
cd "$build_dir"
export DOTNET_CLI_HOME="$build_dir/cli" NUGET_PACKAGES="$build_dir/nuget"
"$dotnet" workload install ios --version 11.0.100.1

framework="$build_dir/SDL3.xcframework"
if [ ! -f "$framework/Info.plist" ]; then
    archive="$build_dir/SDL3-3.4.16.dmg"
    curl -fsSL --retry 2 https://github.com/libsdl-org/SDL/releases/download/release-3.4.16/SDL3-3.4.16.dmg -o "$archive"
    printf '%s  %s\n' 675660a9e457239af615f9e41f788612168d1639b9d2eda2957e8dace26687fd "$archive" | shasum -a 256 -c
    mount_dir=$(mktemp -d "$build_dir/sdl-mount.XXXXXX")
    trap 'hdiutil detach "$mount_dir" -quiet 2>/dev/null || :; rmdir "$mount_dir" 2>/dev/null || :' EXIT
    hdiutil attach "$archive" -mountpoint "$mount_dir" -nobrowse -readonly -quiet
    source_framework=$(find "$mount_dir" -name SDL3.xcframework -type d -print -quit)
    if [ -z "$source_framework" ]; then
        echo "SDL3.xcframework was missing from the release disk image." >&2
        exit 1
    fi
    ditto "$source_framework" "$framework"
fi

out="$build_dir/app"
[ "$rid" = iossimulator-arm64 ] || out="$build_dir/app-$rid"
"$dotnet" build "$repo/hpengine/HPEngine.csproj" -c Release -r "$rid" \
    -p:BuildForIos=true -p:SdlIosFramework="$framework" -p:EnableCodeSigning=false \
    -o "$out"
built_app="$out/HPEngine.app"
test -f "$built_app/Info.plist"
if [ -n "${HPENGINE_BUILD_ONLY:-}" ]; then
    exit 0
fi
# The simulator on Apple silicon refuses unsigned code; an ad-hoc signature needs no certificate. Sign a copy
# outside the repository, because a synced folder (iCloud Documents) re-adds Finder attributes codesign rejects.
app="$HOME/Library/Developer/HPEngine/ios-simulator/HPEngine.app"
rm -rf "$app"
mkdir -p "$(dirname "$app")"
ditto --noextattr --noqtn "$built_app" "$app"
xattr -cr "$app"
find "$app" \( -name '*.dylib' -o -name '*.framework' \) -prune -print | while IFS= read -r item; do
    codesign --force --sign - --timestamp=none "$item"
done
codesign --force --sign - --timestamp=none "$app"
ditto -c -k --keepParent "$app" "$build_dir/HPEngine-iOS-Simulator.zip"
printf 'Simulator app: %s\nSimulator ZIP: %s\n' "$app" "$build_dir/HPEngine-iOS-Simulator.zip"
