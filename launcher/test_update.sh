#!/bin/sh
# Launcher self-update checks (macOS; no window: only the Launcher's command-line modes run). Builds launcher/Launcher.swift
# and launcher/update_sign.swift, makes a small ad-hoc-signed app with the Launcher's bundle id, zips and signs it with a
# throw-away key, then runs the same verifiedApp() the app runs before replacing itself, and the release choice pick().
#   sh launcher/test_update.sh
# Red mutations: sh launcher/test_update_redtest.sh. Exit: 0 all passed, 1 a case failed, 2 build, 3 NOVERDICT (not macOS).
set -u
HERE=$(cd "$(dirname "$0")" && pwd)
[ "$(uname)" = Darwin ] || { echo "NOVERDICT: needs macOS (swiftc, codesign, ditto)"; exit 3; }
T=$(cd "$(mktemp -d)" && pwd -P); trap 'rm -rf "$T"' EXIT
L="$T/Launcher"; S="$T/sign"
xcrun swiftc -O -parse-as-library -target arm64-apple-macos13.0 "$HERE/Launcher.swift" -o "$L" > "$T/build.log" 2>&1 \
    && xcrun swiftc -O "$HERE/update_sign.swift" -o "$S" >> "$T/build.log" 2>&1 || { tail -20 "$T/build.log"; echo "build failed"; exit 2; }
fails=0
ok() { echo "ok    $1"; }
bad() { echo "FAIL  $1"; fails=$((fails + 1)); }
KEY=$("$S" keygen "$T/key") && OTHER=$("$S" keygen "$T/other") || { echo "keygen failed"; exit 2; }
# mkapp <dir> <bundle id> <version>: an ad-hoc-signed .app, zipped as <dir>.zip
mkapp() {
    a="$1/OpenHP1 Launcher.app"; mkdir -p "$a/Contents/MacOS" "$a/Contents/Resources"
    cp "$L" "$a/Contents/MacOS/Launcher"; echo data > "$a/Contents/Resources/data"
    cat > "$a/Contents/Info.plist" <<PL
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
	<key>CFBundleExecutable</key><string>Launcher</string>
	<key>CFBundleIdentifier</key><string>$2</string>
	<key>CFBundlePackageType</key><string>APPL</string>
	<key>OpenHP1Version</key><string>$3</string>
</dict></plist>
PL
    codesign --force --deep -s - "$a" > /dev/null 2>&1 || { echo "codesign failed"; exit 2; }
}
zipit() { (cd "$1" && ditto -c -k --keepParent "OpenHP1 Launcher.app" "$1.zip"); }
# verify <name> <zip> <sig> <key> <version> <expected: OK or a FAIL text>
verify() {
    cp "$2" "$T/try.zip"   # verifiedApp moves the zip it checks
    got=$("$L" --verify-update "$T/try.zip" "$3" "$4" "$5" 2>&1); out=$got
    case "$got" in OK*) got=OK; app=${out#OK } ;; esac
    case "$got" in "$6"*) ok "$1: $got" ;; *) bad "$1: want '$6', got '$got'" ;; esac
}
mkdir -p "$T/good"; mkapp "$T/good" io.github.openhp1.launcher 9.9.9
zipit "$T/good"; xattr -w com.apple.quarantine "0081;00000000;test;" "$T/good.zip"   # as a browser would
"$S" sign "$T/other" "$T/good.zip" && mv "$T/good.zip.sig" "$T/good.other.sig"
"$S" sign "$T/key" "$T/good.zip"
mkdir -p "$T/plain"; ditto -x -k "$T/good.zip" "$T/plain"   # the branch: unpacking passes the zip's flag to the app
xattr -p com.apple.quarantine "$T/plain/OpenHP1 Launcher.app" > /dev/null 2>&1 && q=1 || q=0
app=""; verify signed_good "$T/good.zip" "$T/good.zip.sig" "$KEY" 9.9.9 OK
if [ $q = 1 ] && [ -n "$app" ] && [ -d "$app" ]; then
    xattr -p com.apple.quarantine "$app" > /dev/null 2>&1 && bad "quarantine_cleared: still set" || ok "quarantine_cleared"
    rm -rf "$(dirname "$app")"   # verifiedApp's temporary folder (failed checks remove their own)
else bad "quarantine_cleared: branch not executed (zip quarantined $q, app '$app')"; fi
cp "$T/good.zip" "$T/tampered.zip"; printf x >> "$T/tampered.zip"
verify tampered_zip "$T/tampered.zip" "$T/good.zip.sig" "$KEY" 9.9.9 "FAIL The download is not signed"
verify other_key "$T/good.zip" "$T/good.other.sig" "$KEY" 9.9.9 "FAIL The download is not signed"
echo garbage > "$T/garbage.sig"
verify garbage_sig "$T/good.zip" "$T/garbage.sig" "$KEY" 9.9.9 "FAIL The download is not signed"
verify wrong_version "$T/good.zip" "$T/good.zip.sig" "$KEY" 9.9.8 "FAIL The downloaded Launcher is not version"
mkdir -p "$T/foreign"; mkapp "$T/foreign" com.example.other 9.9.9; zipit "$T/foreign"; "$S" sign "$T/key" "$T/foreign.zip"
verify foreign_bundle_id "$T/foreign.zip" "$T/foreign.zip.sig" "$KEY" 9.9.9 "FAIL The downloaded file is not an OpenHP1 Launcher"
mkdir -p "$T/broken"; mkapp "$T/broken" io.github.openhp1.launcher 9.9.9
echo changed > "$T/broken/OpenHP1 Launcher.app/Contents/Resources/data"   # after codesign
zipit "$T/broken"; "$S" sign "$T/key" "$T/broken.zip"
verify broken_codesign "$T/broken.zip" "$T/broken.zip.sig" "$KEY" 9.9.9 "FAIL The downloaded Launcher's code signature is broken"

# pick: newest release a build may take (drafts never; pre-releases only for a pre-release build), if newer.
cat > "$T/feed.json" <<'J'
[{"tag_name":"v0.4.0","draft":true,"prerelease":false,"html_url":"https://example.invalid/4","assets":[]},
 {"tag_name":"v0.3.0-beta","draft":false,"prerelease":true,"html_url":"https://example.invalid/3","assets":[
   {"name":"OpenHP1-win-x64.zip","browser_download_url":"https://example.invalid/w.zip"},
   {"name":"OpenHP1-Launcher-macOS.zip","browser_download_url":"https://example.invalid/OpenHP1-Launcher-macOS.zip"},
   {"name":"OpenHP1-Launcher-macOS.zip.sig","browser_download_url":"https://example.invalid/OpenHP1-Launcher-macOS.zip.sig"}]},
 {"tag_name":"v0.2.0","draft":false,"prerelease":false,"html_url":"https://example.invalid/2","assets":[
   {"name":"OpenHP1-Launcher-macOS.zip","browser_download_url":"https://example.invalid/OpenHP1-Launcher-macOS.zip"}]}]
J
echo '[{"tag_name":"v0.2.0","draft":false,"prerelease":false,"html_url":"https://example.invalid/2","assets":[]}]' > "$T/final.json"
pick() {   # <name> <feed> <current> <expected>
    got=$("$L" --pick-release "$2" "$3" 2>&1)
    [ "$got" = "$4" ] && ok "$1: $got" || bad "$1: want '$4', got '$got'"
}
pick final_build_skips_prerelease "$T/feed.json" 0.1.0 "0.2.0 zip=OpenHP1-Launcher-macOS.zip sig=-"
pick prerelease_build_takes_prerelease "$T/feed.json" 0.1.0-beta "0.3.0-beta zip=OpenHP1-Launcher-macOS.zip sig=OpenHP1-Launcher-macOS.zip.sig"
pick up_to_date "$T/feed.json" 0.2.0 none
pick final_after_beta "$T/final.json" 0.2.0-beta "0.2.0 zip=- sig=-"
# Pre-release precedence (semver.org, item 11): 0.1.0-beta < 0.1.0-beta.2 < 0.1.0-beta.10 < 0.1.0-rc1.
rel() { echo "[{\"tag_name\":\"v$1\",\"draft\":false,\"prerelease\":true,\"html_url\":\"https://example.invalid/p\",\"assets\":[]}]" > "$T/pre.json"; }
rel 0.1.0-beta.2; pick beta_dot_two_after_beta "$T/pre.json" 0.1.0-beta "0.1.0-beta.2 zip=- sig=-"
rel 0.1.0-beta.10; pick numeric_identifier_as_number "$T/pre.json" 0.1.0-beta.2 "0.1.0-beta.10 zip=- sig=-"
rel 0.1.0-rc1; pick rc_after_beta "$T/pre.json" 0.1.0-beta.10 "0.1.0-rc1 zip=- sig=-"
rel 0.1.0-beta.2; pick beta_not_after_rc "$T/pre.json" 0.1.0-rc1 none

[ $fails -eq 0 ] || exit 1
echo "all passed"
