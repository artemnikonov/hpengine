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
    a="$1/HPEngine Launcher.app"; mkdir -p "$a/Contents/MacOS" "$a/Contents/Resources"
    cp "$L" "$a/Contents/MacOS/Launcher"; echo data > "$a/Contents/Resources/data"
    cat > "$a/Contents/Info.plist" <<PL
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
	<key>CFBundleExecutable</key><string>Launcher</string>
	<key>CFBundleIdentifier</key><string>$2</string>
	<key>CFBundlePackageType</key><string>APPL</string>
	<key>HPEngineVersion</key><string>$3</string>
</dict></plist>
PL
    codesign --force --deep -s - "$a" > /dev/null 2>&1 || { echo "codesign failed"; exit 2; }
}
zipit() { (cd "$1" && ditto -c -k --keepParent "HPEngine Launcher.app" "$1.zip"); }
# verify <name> <zip> <sig> <key> <version> <expected: OK or a FAIL text>
verify() {
    cp "$2" "$T/try.zip"   # verifiedApp moves the zip it checks
    got=$("$L" --verify-update "$T/try.zip" "$3" "$4" "$5" 2>&1); out=$got
    case "$got" in OK*) got=OK; app=${out#OK } ;; esac
    case "$got" in "$6"*) ok "$1: $got" ;; *) bad "$1: want '$6', got '$got'" ;; esac
}
mkdir -p "$T/good"; mkapp "$T/good" io.github.hpengine.launcher 9.9.9
zipit "$T/good"; xattr -w com.apple.quarantine "0081;00000000;test;" "$T/good.zip"   # as a browser would
"$S" sign "$T/other" "$T/good.zip" && mv "$T/good.zip.sig" "$T/good.other.sig"
"$S" sign "$T/key" "$T/good.zip"
mkdir -p "$T/plain"; ditto -x -k "$T/good.zip" "$T/plain"   # the branch: unpacking passes the zip's flag to the app
xattr -p com.apple.quarantine "$T/plain/HPEngine Launcher.app" > /dev/null 2>&1 && q=1 || q=0
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
verify foreign_bundle_id "$T/foreign.zip" "$T/foreign.zip.sig" "$KEY" 9.9.9 "FAIL The downloaded file is not an HPEngine Launcher"
mkdir -p "$T/broken"; mkapp "$T/broken" io.github.hpengine.launcher 9.9.9
echo changed > "$T/broken/HPEngine Launcher.app/Contents/Resources/data"   # after codesign
zipit "$T/broken"; "$S" sign "$T/key" "$T/broken.zip"
verify broken_codesign "$T/broken.zip" "$T/broken.zip.sig" "$KEY" 9.9.9 "FAIL The downloaded Launcher's code signature is broken"

# pick: newest release a build may take (drafts never; pre-releases only for a pre-release build), if newer.
cat > "$T/feed.json" <<'J'
[{"tag_name":"v0.4.0","draft":true,"prerelease":false,"html_url":"https://example.invalid/4","assets":[]},
 {"tag_name":"v0.3.0-beta","draft":false,"prerelease":true,"html_url":"https://example.invalid/3","assets":[
   {"name":"HPEngine-win-x64.zip","browser_download_url":"https://example.invalid/w.zip"},
   {"name":"HPEngine-Launcher-macOS.zip","browser_download_url":"https://example.invalid/HPEngine-Launcher-macOS.zip"},
   {"name":"HPEngine-Launcher-macOS.zip.sig","browser_download_url":"https://example.invalid/HPEngine-Launcher-macOS.zip.sig"}]},
 {"tag_name":"v0.2.0","draft":false,"prerelease":false,"html_url":"https://example.invalid/2","assets":[
   {"name":"HPEngine-Launcher-macOS.zip","browser_download_url":"https://example.invalid/HPEngine-Launcher-macOS.zip"}]}]
J
echo '[{"tag_name":"v0.2.0","draft":false,"prerelease":false,"html_url":"https://example.invalid/2","assets":[]}]' > "$T/final.json"
pick() {   # <name> <feed> <current> <expected>
    got=$("$L" --pick-release "$2" "$3" 2>&1)
    [ "$got" = "$4" ] && ok "$1: $got" || bad "$1: want '$4', got '$got'"
}
pick final_build_skips_prerelease "$T/feed.json" 0.1.0 "0.2.0 zip=HPEngine-Launcher-macOS.zip sig=-"
pick prerelease_build_takes_prerelease "$T/feed.json" 0.1.0-beta "0.3.0-beta zip=HPEngine-Launcher-macOS.zip sig=HPEngine-Launcher-macOS.zip.sig"
pick up_to_date "$T/feed.json" 0.2.0 none
pick final_after_beta "$T/final.json" 0.2.0-beta "0.2.0 zip=- sig=-"
# Pre-release precedence (semver.org, item 11): 0.1.0-beta < 0.1.0-beta.2 < 0.1.0-beta.10 < 0.1.0-rc1.
rel() { echo "[{\"tag_name\":\"v$1\",\"draft\":false,\"prerelease\":true,\"html_url\":\"https://example.invalid/p\",\"assets\":[]}]" > "$T/pre.json"; }
rel 0.1.0-beta.2; pick beta_dot_two_after_beta "$T/pre.json" 0.1.0-beta "0.1.0-beta.2 zip=- sig=-"
rel 0.1.0-beta.10; pick numeric_identifier_as_number "$T/pre.json" 0.1.0-beta.2 "0.1.0-beta.10 zip=- sig=-"
rel 0.1.0-rc1; pick rc_after_beta "$T/pre.json" 0.1.0-beta.10 "0.1.0-rc1 zip=- sig=-"
rel 0.1.0-beta.2; pick beta_not_after_rc "$T/pre.json" 0.1.0-rc1 none

# Remove game (--remove-game <root> <keep|all> [fail-after-marker]): keep-mode keeps Save/HPEngineData/Mods, all-mode removes the
# root, the marker goes first (simulated failure right after it), links are deleted as links (their target survives).
mkgame() {
    g="$T/$1"; mkdir -p "$g/System" "$g/Maps" "$g/Save" "$g/HPEngineData/logs" "$g/Mods/Alpha"
    for f in .hp1-installed System/a.u Maps/b.unr Save/slot1.sav HPEngineData/Default.ini HPEngineData/logs/l.txt Mods/Alpha/mod.txt; do echo "x:$f" > "$g/$f"; done
}
mkgame rm_keep; got=$("$L" --remove-game "$T/rm_keep" keep 2>&1)
if [ "$got" = OK ] && [ ! -e "$T/rm_keep/.hp1-installed" ] && [ ! -e "$T/rm_keep/System" ] && [ ! -e "$T/rm_keep/Maps" ] \
    && [ "$(cat "$T/rm_keep/Save/slot1.sav")" = "x:Save/slot1.sav" ] && [ "$(cat "$T/rm_keep/HPEngineData/logs/l.txt")" = "x:HPEngineData/logs/l.txt" ] \
    && [ "$(cat "$T/rm_keep/Mods/Alpha/mod.txt")" = "x:Mods/Alpha/mod.txt" ]; then ok "remove_keep_keeps_saves_settings_mods"
else bad "remove_keep_keeps_saves_settings_mods: $got"; fi
mkgame rm_all; got=$("$L" --remove-game "$T/rm_all" all 2>&1)
{ [ "$got" = OK ] && [ ! -e "$T/rm_all" ]; } && ok "remove_all_removes_everything" || bad "remove_all_removes_everything: $got"
mkgame rm_fail; got=$("$L" --remove-game "$T/rm_fail" all fail-after-marker 2>&1)
{ [ "${got%% *}" = FAIL ] && [ ! -e "$T/rm_fail/.hp1-installed" ] && [ -e "$T/rm_fail/System/a.u" ] && [ -e "$T/rm_fail/Save/slot1.sav" ]; } \
    && ok "remove_marker_first" || bad "remove_marker_first: $got"
mkgame rm_link; mkdir -p "$T/outside"; echo target > "$T/outside/keep.txt"
ln -s "$T/outside" "$T/rm_link/Maps/lnk"; ln -s "$T/outside" "$T/rm_link/Textures";mkdir -p "$T/rm_link/HPEngineData/modroot"; ln -s "$T/outside" "$T/rm_link/HPEngineData/modroot/Alpha"
"$L" --remove-game "$T/rm_link" keep > /dev/null 2>&1; k=$(cat "$T/outside/keep.txt" 2>/dev/null)
"$L" --remove-game "$T/rm_link" all > /dev/null 2>&1; a2=$(cat "$T/outside/keep.txt" 2>/dev/null)
{ [ "$k" = target ] && [ "$a2" = target ] && [ ! -e "$T/rm_link" ]; } && ok "remove_never_follows_links" || bad "remove_never_follows_links: keep='$k' all='$a2'"

# Mods.ini (--mods-ini <file> [names...]); the cases of launcher/Desktop.Tests (Logic.cs ModsIni) that both implementations share.
ini_src='[Other]\nK=1\n\n[Mods]\n; c\nEnabled=Beta\nEnabled=Alpha\n\n[Tail]\nX=2\n'
printf "$ini_src" > "$T/m.ini"; printf "$ini_src" | sed 's/$/\r/' > "$T/m_crlf.ini"
got=$("$L" --mods-ini "$T/m.ini"); [ "$got" = "Beta|Alpha" ] && ok "mods_ini_read_order" || bad "mods_ini_read_order: '$got'"
"$L" --mods-ini "$T/m.ini" Alpha Gamma > "$T/m.out"; printf '[Other]\nK=1\n\n[Mods]\n; c\nEnabled=Alpha\nEnabled=Gamma\n\n[Tail]\nX=2\n' > "$T/m.want"
cmp -s "$T/m.out" "$T/m.want" && ok "mods_ini_rewrite_keeps_comments_and_sections" || bad "mods_ini_rewrite_keeps_comments_and_sections: $(cat "$T/m.out")"
"$L" --mods-ini "$T/m_crlf.ini" Beta Alpha > "$T/m.out"
cmp -s "$T/m.out" "$T/m_crlf.ini" && ok "mods_ini_crlf_roundtrip" || bad "mods_ini_crlf_roundtrip"
printf '[Other]\nEnabled=Zed\n' > "$T/o.ini"; got=$("$L" --mods-ini "$T/o.ini"); [ -z "$got" ] && ok "mods_ini_enabled_outside_mods_ignored" || bad "mods_ini_enabled_outside_mods_ignored: '$got'"
printf '[Other]\nK=1\n' > "$T/n.ini"; "$L" --mods-ini "$T/n.ini" A > "$T/m.out"; printf '[Other]\nK=1\n\n[Mods]\nEnabled=A\n' > "$T/m.want"
cmp -s "$T/m.out" "$T/m.want" && ok "mods_ini_missing_section_created" || bad "mods_ini_missing_section_created: $(cat "$T/m.out")"

# Mods notice (--mods-notice <game dir>, --slot-mods <file>): which slots (Save/save0..5.hp1sav = Slot 1..6) were saved with
# another mod set than Mods.ini enables; the record is read from the save head (format 5: "HP1SAV", int32 version, int32
# count, count strings, BinaryWriter encoding). Same cases as launcher/Desktop.Tests.
G="$T/notice"; mkdir -p "$G/HPEngineData" "$G/Save"
inis() { printf '[Mods]\n'; for n in "$@"; do printf 'Enabled=%s\n' "$n"; done; }
# head <file> <version> [mods...]: a save body head as the engine writes it (7-bit lengths; names here are < 128 bytes)
head5() {
    f=$1 v=$2; shift 2
    python3 -I -c 'import struct,sys
def s(x): b=x.encode(); return bytes([len(b)])+b
v=int(sys.argv[2]); m=sys.argv[3:]
o=s("HP1SAV")+struct.pack("<i",v)+((struct.pack("<i",len(m))+b"".join(s(x) for x in m)) if v>=5 else b"")+s("Lev_Tut1")+bytes(8)
open(sys.argv[1],"wb").write(o)' "$f" "$v" "$@"
}
slot() {   # <name> <expected> <version> [mods...]
    n=$1 want=$2; shift 2; head5 "$T/one.hp1sav" "$@"
    got=$("$L" --slot-mods "$T/one.hp1sav"); [ "$got" = "$want" ] && ok "slot_mods_$n: $got" || bad "slot_mods_$n: want '$want', got '$got'"
}
slot v5_mods_in_order "B|A" 5 B A
slot v5_no_mods "-" 5
slot v4_legacy legacy 4
slot v6_mods "A" 6 A
slot v7_unknown unknown 7
printf 'garbage file' > "$T/one.hp1sav"; got=$("$L" --slot-mods "$T/one.hp1sav"); [ "$got" = unknown ] && ok "slot_mods_garbage_unknown" || bad "slot_mods_garbage_unknown: '$got'"
head5 "$T/one.hp1sav" 5 Alpha; head -c 12 "$T/one.hp1sav" > "$T/cut.hp1sav"
got=$("$L" --slot-mods "$T/cut.hp1sav"); [ "$got" = unknown ] && ok "slot_mods_truncated_unknown" || bad "slot_mods_truncated_unknown: '$got'"
notice() {   # <name> <expected text, lines joined by '/'>
    got=$("$L" --mods-notice "$G" | paste -sd/ -); [ "$got" = "$2" ] && ok "mods_notice_$1" || bad "mods_notice_$1: want '$2', got '$got'"
}
R1="was saved with other mods: loading it restarts its level with the progress you had when you entered it."
RN="were saved with other mods: loading them restarts their level with the progress you had when you entered it."
inis A > "$G/HPEngineData/Mods.ini"; notice no_slots ""
head5 "$G/Save/save0.hp1sav" 5 a; head5 "$G/Save/save99.hp1sav" 5; head5 "$G/Save/save6.hp1sav" 5
notice same_mods_autosave_and_save6_ignored ""
head5 "$G/Save/save2.hp1sav" 5; notice one_slot "Slot 3 $R1"
head5 "$G/Save/save1.hp1sav" 5 B; notice two_slots "Slots 2 and 3 $RN"
head5 "$G/Save/save4.hp1sav" 5 A B; notice three_slots "Slots 2, 3 and 5 $RN"
head5 "$G/Save/save3.hp1sav" 4; printf 'xx' > "$G/Save/save5.hp1sav"
notice legacy_with_mods_damaged_skipped "Slots 2, 3 and 5 $RN/Slot 4 was saved by an older version: it does not load while mods are on."
inis > "$G/HPEngineData/Mods.ini"; notice no_mods_legacy_fine "Slots 1, 2 and 5 $RN"
G="$T/notice2"; mkdir -p "$G/HPEngineData" "$G/Save"; inis A B > "$G/HPEngineData/Mods.ini"
head5 "$G/Save/save0.hp1sav" 5 B A; head5 "$G/Save/save1.hp1sav" 5 a b; notice reordered "Slot 1 $R1"

# Mod problems (--mods-check <game dir>): an enabled mod without its folder blocks Play; files of a mod unpacked one level
# too deep are counted (not in a game folder of the install). Same cases and texts as launcher/Desktop.Tests.
G="$T/check"; mkdir -p "$G/System" "$G/Maps" "$G/HPEngineData" "$G/Mods/Alpha/system" "$G/Mods/Deep/Deep-v1/System" "$G/Mods/Deep/Maps" "$G/Mods/Off/Off-v1"
for f in Alpha/system/A.u Alpha/A.u Deep/Deep-v1/System/D.u Deep/Deep-v1/D2.u Deep/Deep-v1/.DS_Store Deep/Maps/D.unr Off/Off-v1/O.u; do echo x > "$G/Mods/$f"; done
check() { got=$("$L" --mods-check "$G" | paste -sd/ -); [ "$got" = "$2" ] && ok "mods_check_$1" || bad "mods_check_$1: want '$2', got '$got'"; }
DEEP="2 files of Deep are not in a game folder (System, Maps, ...): unpack the mod so these folders are directly inside Mods/Deep"
inis alpha Ghost Deep > "$G/HPEngineData/Mods.ini"
check missing_blocks_and_misplaced "The mod Ghost is on, but its folder is gone. Turn off Ghost or put its folder back into Mods./$DEEP"
inis Ghost Gone > "$G/HPEngineData/Mods.ini"
check missing_several "The mods Ghost and Gone are on, but their folders are gone. Turn off Ghost and Gone or put their folders back into Mods."
inis Alpha Off > "$G/HPEngineData/Mods.ini"
check one_file "1 file of Off is not in a game folder (System, Maps, ...): unpack the mod so these folders are directly inside Mods/Off"
inis Alpha > "$G/HPEngineData/Mods.ini"; check nothing ""

# The engine's own error line in the banner (--engine-error <code> <output file>). Same cases as launcher/Desktop.Tests.
banner() { got=$("$L" --engine-error "$2" "$T/said.txt"); [ "$got" = "$3" ] && ok "engine_error_$1" || bad "engine_error_$1: want '$3', got '$got'"; }
M='[Mods] mod "Ghost" is enabled in /g/HPEngineData/Mods.ini but there is no folder /g/Mods/Ghost'
printf '%s\n' '[Mods] none' "$M" > "$T/said.txt"
banner mods 5 "The game stopped with an error (code 5): $M See the log: L."
banner no_line 4 "The game stopped with an error (code 4). See the log: L."
printf '%s\n' '--root /g: this is not a game install (/g: no directory Maps/).' 'Hint: unpack HPEngine into the game install directory.' > "$T/said.txt"
banner root_not_hint 4 "The game stopped with an error (code 4): --root /g: this is not a game install (/g: no directory Maps/). See the log: L."
printf '%s\n' '[Mods] none' '[Crash] unhandled exception: System.InvalidDataException: bad package' '   at X()' 'Unhandled exception. System.InvalidDataException: bad package' '   at X()' > "$T/said.txt"
banner crash 134 "The game stopped with an error (code 134): [Crash] unhandled exception: System.InvalidDataException: bad package See the log: L."

printf '%s\n' '[Mods] active: Xmas' '[Map] there is no map "nosuch": no Maps/nosuch.unr in the game or in an enabled mod' > "$T/said.txt"
banner no_map 6 'The game stopped with an error (code 6): [Map] there is no map "nosuch": no Maps/nosuch.unr in the game or in an enabled mod See the log: L.'

# "Start map" (--start-maps <game dir> [chosen map]): the maps the enabled mods add, and Play's engine arguments. Same
# cases as launcher/Desktop.Tests (StartMaps), where the rule's owner (ModOverlay.AddedMaps) is checked.
G="$T/startmap"; mkdir -p "$G/System" "$G/Maps" "$G/HPEngineData" "$G/Mods/Replace/Maps" "$G/Mods/Xmas/maps" "$G/Mods/Flat/Maps/sub" "$G/Mods/Flat/System" "$G/Mods/Off/Maps" "$G/Mods/Total/Maps" "$G/Mods/Empty"
printf '[Core.System]\nPaths=../System/*.u\nPaths=../Maps/*.unr\n' > "$G/System/Default.ini"
for f in Maps/Lev_Tut1.unr Maps/Startup.unr Mods/Replace/Maps/lev_tut1.unr Mods/Xmas/maps/acniabwc.unr Mods/Xmas/ReadMe.txt Mods/Flat/Early_Potions.unr \
    Mods/Flat/STARTUP.UNR Mods/Flat/Maps/sub/deep.unr Mods/Flat/System/Flat.u Mods/Off/Maps/Zzz.unr Mods/Total/Maps/b.unr Mods/Total/Maps/A.unr \
    Mods/Total/Maps/Zed.unr Mods/Total/Maps/acniabwc.unr Mods/Empty/readme.txt; do echo x > "$G/$f"; done
PLAY="--root /g --launched-by-launcher"
smap() { sn=$1 want=$2 chosen=$3; shift 3; inis "$@" > "$G/HPEngineData/Mods.ini"
    got=$("$L" --start-maps "$G" $chosen | paste -sd/ -); [ "$got" = "$want" ] && ok "start_maps_$sn" || bad "start_maps_$sn: want '$want', got '$got'"; }
smap no_mod_on "/$PLAY" ""
smap replaced_only "/$PLAY" "" Replace
smap laid_out "acniabwc@Xmas=acniabwc/$PLAY" "" Xmas
smap flat "Early_Potions@Flat=Early_Potions/$PLAY --map Early_Potions" Early_Potions Flat
smap two_mods "acniabwc@Xmas=acniabwc  (Xmas)|Early_Potions@Flat=Early_Potions  (Flat)/$PLAY --map acniabwc" acniabwc Flat Xmas Replace
smap sorted "A@Total=A|acniabwc@Total=acniabwc|b@Total=b|Zed@Total=Zed/$PLAY" "" Total
smap later_mod_owns "A@Total=A|acniabwc@Total=acniabwc|b@Total=b|Zed@Total=Zed/$PLAY --map b" b Xmas Total
smap chosen_map_gone "acniabwc@Xmas=acniabwc/$PLAY" Early_Potions Xmas
smap missing_folder "/$PLAY" acniabwc Xmas Ghost
smap mod_without_game_files "/$PLAY" acniabwc Xmas Empty
rm "$G/System/Default.ini"
smap flat_without_default_ini "/$PLAY" "" Xmas
mkdir "$G/System/0"; printf '[Core.System]\r\nPaths=..\\Maps\\*.unr\r\n' > "$G/System/0/Default.ini"
smap default_ini_in_system_0 "acniabwc@Xmas=acniabwc  (Xmas)|Early_Potions@Flat=Early_Potions  (Flat)/$PLAY" "" Xmas Flat

# "Reset Gamepad Bindings" (--pad-reset <game dir> [reset]): only the Joy* lines of [Engine.Input] in DefUser.ini and the
# GamepadBindingsSaved line of [HPEngine] in Default.ini go. Same cases as launcher/Desktop.Tests (PadResetRule).
G="$T/pad"; mkdir -p "$G/HPEngineData"; U="$G/HPEngineData/DefUser.ini"; S="$G/HPEngineData/Default.ini"
padn() { got=$("$L" --pad-reset "$G" ${3:-}); [ "$got" = "$2" ] && ok "pad_reset_$1" || bad "pad_reset_$1: want '$2', got '$got'"; }
padn no_files 0
printf '[Engine.Input]\nScrollLock=Walking\nUp=MoveForward\n\n[Other]\nJoy1=keep\n' > "$U"
printf '[HPEngine]\nUIScaleStep=1\nGamepadLookSpeed=6\n\n[Engine.Input]\nJoy1=not the user file\n' > "$S"
cp "$U" "$T/u0"; cp "$S" "$T/s0"
padn keyboard_only 0; padn keyboard_only_reset 0 reset
cmp -s "$U" "$T/u0" && cmp -s "$S" "$T/s0" && ok pad_reset_keyboard_only_untouched || bad "pad_reset_keyboard_only_untouched"
printf '[Engine.Input]\r\nScrollLock=Walking\r\nJoy1=\r\nJoy13=JUMP\r\njoypovup = Button bBroomBrake\r\nJoyful\r\nUp=MoveForward | Joy\r\n\r\n[Other]\r\nJoy1=keep\r\nName=caf\351\r\n' > "$U"
printf '[HPEngine]\nUIScaleStep=1\ngamepadbindingssaved=True\nGamepadLookSpeed=6\n\n[Engine.Input]\nJoy1=not the user file\nGamepadBindingsSaved=other section\n' > "$S"
cp "$U" "$T/u1"
padn count 4
cmp -s "$U" "$T/u1" && ok pad_reset_count_writes_nothing || bad "pad_reset_count_writes_nothing"
padn removed 4 reset
printf '[Engine.Input]\r\nScrollLock=Walking\r\nJoyful\r\nUp=MoveForward | Joy\r\n\r\n[Other]\r\nJoy1=keep\r\nName=caf\351\r\n' > "$T/uw"
printf '[HPEngine]\nUIScaleStep=1\nGamepadLookSpeed=6\n\n[Engine.Input]\nJoy1=not the user file\nGamepadBindingsSaved=other section\n' > "$T/sw"
cmp -s "$U" "$T/uw" && ok pad_reset_user_file || bad "pad_reset_user_file"
cmp -s "$S" "$T/sw" && ok pad_reset_system_file || bad "pad_reset_system_file"
padn nothing_left 0

# Title by edition (--game-title [edition text]); same cases as launcher/Desktop.Tests (TitleCases).
title() { got=$("$L" --game-title "$@"); [ "$got" = "$want" ] && ok "title_$name" || bad "title_$name: want '$want', got '$got'"; }
name=us want="Harry Potter and the Sorcerer's Stone" title "US 1.53: exact"
name=us_modified want="Harry Potter and the Sorcerer's Stone" title "US 1.53: modified"
name=uk want="Harry Potter and the Philosopher's Stone" title "UK 1.1: supported"
name=ru_ea want="Harry Potter and the Philosopher's Stone" title "RU EA: supported"
name=city want="Harry Potter and the Sorcerer's Stone" title "City re-release: modified"
name=modded want="Harry Potter and the Sorcerer's Stone" title "Modded re-release: modified"
name=modified_release want="Harry Potter" title "Modified release: modified"
name=unknown want="Harry Potter" title "Unknown release: unverified"
name=none want="Harry Potter" title

# The owner of the mod-placement and language rules is C# (hpengine/packaging ModOverlay.cs, GameLanguage.cs); the
# Launcher mirrors them. launcher/Desktop.Tests runs the owner's code on the same folders for the comparisons below.
dotnet build "$HERE/Desktop.Tests" -c Release > "$T/dt.log" 2>&1 || { tail -20 "$T/dt.log"; echo "build of launcher/Desktop.Tests failed"; exit 2; }
DT() { dotnet run --no-build --project "$HERE/Desktop.Tests" -c Release -- "$@"; }

# Mod conflicts and the apply-order numbers (--mods-conflicts, --mods-order): the cases and texts of launcher/Desktop.Tests
# (ModConflicts), and each case compared with the owner's lines (ModOverlay.Conflicts through Desktop.Tests).
G="$T/conflict"
for f in System/Shared.u Textures/A.utx HPEngineData/x Mods/A/Textures/A.utx Mods/A/System/Shared.u Mods/B/textures/a.utx Mods/B/Shared.u \
    Mods/B/readme.txt Mods/C/Textures/A.UTX Mods/C/Textures/Own.utx Mods/D/Textures/A.utx Mods/E/System/E.u Mods/E/E.u; do
    mkdir -p "$(dirname "$G/$f")"; echo x > "$G/$f"; done
printf '[Core.System]\nPaths=../System/*.u\nPaths=../Textures/*.utx\n' > "$G/System/Default.ini"
conf() { cn=$1 want=$2; shift 2; inis "$@" > "$G/HPEngineData/Mods.ini"
    got=$("$L" --mods-conflicts "$G" | paste -sd/ -); [ "$got" = "$want" ] && ok "mods_conflicts_$cn" || bad "mods_conflicts_$cn: want '$want', got '$got'"
    own=$(DT --mods-conflicts "$G" | paste -sd/ -); [ "$got" = "$own" ] && ok "mods_conflicts_${cn}_as_owner" || bad "mods_conflicts_${cn}_as_owner: launcher '$got', owner '$own'"; }
conf pairs "B and A both have System/Shared.u and 1 more file: the game uses B's (later in the order)/C and B both have Textures/A.UTX: the game uses C's (later in the order)" A B C
conf other_order "A and C both have Textures/A.utx: the game uses A's (later in the order)" C A
conf one_mod "" A
conf same_mod "" E D
conf unplaceable "" A Ghost
order() { on=$1 want=$2; shift 2; inis "$@" > "$G/HPEngineData/Mods.ini"
    got=$("$L" --mods-order "$G"); [ "$got" = "$want" ] && ok "mods_order_$on" || bad "mods_order_$on: want '$want', got '$got'"; }
order numbered "A=1|B=2|C=3|D=-|E=-" A B C
order moved "C=1|A=2|B=-|D=-|E=-" C A

# Game language (--language-list, --language-select, --edition-label): the Launcher's mirror against GameLanguage.cs on
# two identical folders; every listing, every stdout and the written HPEngineData/Default.ini byte for byte.
mklang() {
    g=$1; mkdir -p "$g/System/0" "$g/System/7" "$g/System/12" "$g/System/3" "$g/System/4" "$g/System/5" "$g/System/6" "$g/System/not-a-language" "$g/HPEngineData"
    printf '[Engine.Engine]\nLanguage=eng\n[Core.System]\nPaths=../System/*.u\n' > "$g/System/0/Default.ini"
    printf '[Engine.Engine]\nLanguage=fin\n' > "$g/System/7/default.INI"
    printf '[Engine.Engine]\nLanguage=fin\n' > "$g/System/12/Default.ini"   # ordinal folder order: 12 before 7, its fin wins
    printf '[Engine.Engine]\r\nLanguage=dan\r\nLanguage="swe"\r\n' > "$g/System/3/Default.ini"   # last duplicate, quotes stripped
    { printf '\377\376'; printf '[Engine.Engine]\r\nLanguage=nor\r\n' | iconv -f UTF-8 -t UTF-16LE; } > "$g/System/4/Default.ini"
    printf '[Engine.Engine]\n Language=xx\n' > "$g/System/5/Default.ini"   # no trimming: not the key
    printf '[Engine.Engine]\nLanguage=ABC\n' > "$g/System/6/Default.ini"
    printf '[Engine.Engine]\nLanguage=../bad\n' > "$g/System/not-a-language/Default.ini"
    printf '; settings\r\n[Engine.Engine]\r\nLanguage=eng\r\nLanguage=eng\r\n[Other]\r\nKeep=42\r\nOwner=H\344\r\n' > "$g/HPEngineData/Default.ini"
}
G1="$T/lang_launcher"; G2="$T/lang_owner"; mklang "$G1"; mklang "$G2"
langcmp() {   # <name>: the listing and the settings file of both folders
    a=$("$L" --language-list "$G1" | paste -sd/ -); b=$(DT --language-list "$G2" | paste -sd/ -)
    [ "$a" = "$b" ] && ok "language_list_$1_as_owner" || bad "language_list_$1_as_owner: launcher '$a', owner '$b'"
    cmp -s "$G1/HPEngineData/Default.ini" "$G2/HPEngineData/Default.ini" && ok "language_file_$1_as_owner" || bad "language_file_$1_as_owner: the settings files differ"
}
got=$("$L" --language-list "$G1" | paste -sd/ -)
want="abc|abc|6/eng|English|0/fin|Finnish|12/nor|Norwegian|4/swe|Swedish|3/selected=-"
[ "$got" = "$want" ] && ok "language_list" || bad "language_list: want '$want', got '$got'"
langcmp initial
for c in fin swe abc default nor; do
    a=$("$L" --language-select "$G1" $c); b=$(DT --language-select "$G2" $c)
    [ "$a" = "$b" ] && [ "$a" = OK ] && ok "language_select_$c" || bad "language_select_$c: launcher '$a', owner '$b'"
    langcmp "$c"
done
grep -q 'CurLanguageName=NORWEGIAN' "$G1/HPEngineData/Default.ini" && LC_ALL=C grep -q "Owner=H$(printf '\344')" "$G1/HPEngineData/Default.ini" \
    && ok "language_file_narrator_and_latin1" || bad "language_file_narrator_and_latin1"
rm -rf "$G1/System/4" "$G2/System/4"
a=$("$L" --language-select "$G1" nor); b=$(DT --language-select "$G2" nor)
[ "$a" = "$b" ] && [ "$a" = "FAIL The selected language is no longer installed: nor" ] && ok "language_select_removed" || bad "language_select_removed: launcher '$a', owner '$b'"
langcmp removed
G3="$T/nordic"; for x in 0:eng 1:dan 2:fin 3:swe 4:nor; do mkdir -p "$G3/System/${x%%:*}"; printf '[Engine.Engine]\nLanguage=%s\n' "${x#*:}" > "$G3/System/${x%%:*}/Default.ini"; done
for e in "UK 1.1: modified" "US 1.53: exact"; do for g in "$G3" "$G1"; do
    a=$("$L" --edition-label "$e" "$g"); b=$(DT --edition-label "$e" "$g")
    [ "$a" = "$b" ] && ok "edition_label_as_owner: $a" || bad "edition_label_as_owner: launcher '$a', owner '$b'"; done; done
got=$("$L" --edition-label "UK 1.1: modified" "$G3")
[ "$got" = "Nordic multilingual data|different data|Engine family: UK 1.1. Languages: Danish, English, Finnish, Norwegian, Swedish." ] && ok "edition_label_nordic" || bad "edition_label_nordic: '$got'"


# HP2 tab: the Launcher's mirror of Hp2FrontEnd (launcher/Desktop/Logic.cs, judged against HP2 Game.exe by
# tools/hp2/frontend_judge.sh) answers the judge's own cases byte for byte as the owner does (--hp2-frontend), and runs
# the menu on real folders as the owner does (--hp2-menu: save dir, page texts, scan, New Game / Load clicks, files left).
python3 "$HERE/../tools/hp2/frontend_judge.py" --cases "$T/h2all" || { echo "NOVERDICT: frontend_judge.py --cases"; exit 3; }
# The launchers offer no HP2 Options (author 2026-11-16: Sound / Video live in the game's own menu): the judge's Sound
# page cases (soundinit / soundok) have no launcher answer and are left out.
grep -v -E '^sound(init|ok)	' "$T/h2all" > "$T/h2cases"
"$L" --hp2-frontend "$T/h2cases" "$T/h2swift" && DT --hp2-frontend "$T/h2cases" "$T/h2owner" > /dev/null
n=$(wc -l < "$T/h2cases" | tr -d ' ')
cmp -s "$T/h2swift" "$T/h2owner" && [ "$n" -gt 80 ] && ok "hp2_frontend_cases_as_owner ($n judge cases)" || { bad "hp2_frontend_cases_as_owner"; diff "$T/h2swift" "$T/h2owner" | head -5; }
mkh2() {   # a fresh HP2 root and HP1 data dir at the same paths for each run
    rm -rf "$T/h2"; mkdir -p "$T/h2/root/System" "$T/h2/data/game" "$T/h2/data/HP2Data/Save/Slot2" "$T/h2/data/HP2Data/Save/Slot4"
    : > "$T/h2/root/System/Engine.dll"
    printf '[IDDIALOG_FrontEndPageMainMenu]\r\nIDC_Title=Main Menu\r\nIDC_NewGame=New Game\r\n IDC_LoadGame = Load\r\n[IDDIALOG_FrontEndPageNewGame]\r\nIDC_Title=New\r\nIDC_SlotEmpty=Empty\r\nIDC_SlotUsed=Used\r\n[IDDIALOG_FrontEndPageLoadGame]\r\nIDC_SlotEmpty=Leer\r\nIDC_SlotUsed=Caf\351\r\n' > "$T/h2/root/System/Startup.int"
    printf '[IDDIALOG_FrontEndPageMainMenu]\nIDC_Options=Optionen\n' > "$T/h2/root/System/Startup.det"
    printf '[Engine.Engine]\nLanguage=det\n' > "$T/h2/root/System/default.ini"
    { printf '\377\376'; printf '[All]\r\nSelect_Game_0001=Replace?\r\nSelect_Game_0002=Caption\r\n' | iconv -f UTF-8 -t UTF-16LE; } > "$T/h2/root/System/HPMenu.int"
    s2="$T/h2/data/HP2Data/Save/Slot2"; s4="$T/h2/data/HP2Data/Save/Slot4"; mkdir -p "$s2/cache" "$s4/cache"
    printf 'save' > "$s2/save0.hp1sav"; printf 'bmp' > "$s2/Save0.bmp"; printf 'pa' > "$s2/Entry_pa.usa"; printf 'c' > "$s2/cache/c.bin"
    printf 'x' > "$s4/notes.txt"; : > "$s4/zero.txt"; : > "$s4/save0.hp1sav"; printf 'old' > "$s4/Save0.usa"; printf 'c' > "$s4/cache/c.bin"
}
h2menu() {   # <name> [new|load <slot> <0|1>]
    hn=$1; shift
    mkh2; a=$("$L" --hp2-menu "$T/h2/data/game" "$T/h2/root" "$@")
    mkh2; b=$(DT --hp2-menu "$T/h2/data/game" "$T/h2/root" "$@")
    [ "$a" = "$b" ] && ok "hp2_menu_${hn}_as_owner" || { bad "hp2_menu_${hn}_as_owner"; printf '%s\n----\n%s\n' "$a" "$b" | head -20; }
}
h2menu scan; scan_out=$a
h2menu new_unused new 1 1
h2menu new_used_no new 2 0
h2menu new_used_yes new 2 1; new_out=$a
h2menu load_unused load 4 1
h2menu load_used load 2 1
case "$scan_out" in *"slots=1:0:-;2:1:Slot2\\Save0.bmp;3:0:-;4:0:-;5:0:-;6:0:-"*"files=Slot2/Entry_pa.usa,Slot2/Save0.bmp,Slot2/cache/c.bin,Slot2/save0.hp1sav,Slot4/save0.hp1sav,Slot4/zero.txt"*) ok "hp2_menu_scan_branches" ;;
    *) bad "hp2_menu_scan_branches: $scan_out" ;; esac
case "$new_out" in *"go=1 --root|$T/h2/root|--launched-by-launcher|--hp2-savedir|$T/h2/data/HP2Data/Save|--hp2-cmdline|PrivetDr.unr  -SAVESLOT=2"*"files=Slot4/save0.hp1sav,Slot4/zero.txt") ok "hp2_menu_new_game_branch" ;;
    *) bad "hp2_menu_new_game_branch: $new_out" ;; esac

# Gamepad (author 2026-11-16: both launchers the same): the Launcher's PadMap mirrors launcher/Desktop/Logic.cs PadMap, the
# whole table (every button x popup x tab, the stick around half way, presses between polls) byte for byte (--pad-map);
# then the pad on the window's view offscreen (--pad-check): focus starts on the primary, the d-pad walks the targets,
# A opens the Mods page, B leaves it, Start opens the Menu, B closes it, HP2 Load Game slots. No controller is needed.
"$L" --pad-map > "$T/padswift"; DT --pad-map > "$T/padowner"
n=$(grep -c '^button' "$T/padswift")
cmp -s "$T/padswift" "$T/padowner" && [ "$n" = 64 ] && ok "pad_map_as_owner ($n button rows)" || { bad "pad_map_as_owner"; diff "$T/padswift" "$T/padowner" | head -5; }
grep -qx 'button 3 popup=0 hp2=1 None' "$T/padswift" && grep -qx 'button 10 popup=0 hp2=0 Hp2' "$T/padswift" && grep -qx 'stick 16001 0 16384' "$T/padswift" \
    && ok pad_map_y_rb_stick || bad pad_map_y_rb_stick
got=$("$L" --pad-check | grep -v '^gamecontroller' | sed 's/ ring=[01]//' | paste -sd/ -)
want="hp2_off: tab2_target=0 hp2Tab=0/start: focus=play mods=0 popup=0 page=Main/up: focus=menu mods=0 popup=0 page=Main/down: focus=play mods=0 popup=0 page=Main/down: focus=mods mods=0 popup=0 page=Main/a_on_mods: focus=back mods=1 popup=0 page=Main/right: focus=mod:Alpha mods=1 popup=0 page=Main/b: focus=play mods=0 popup=0 page=Main/start: focus=pop1 mods=0 popup=1 page=Main/down: focus=pop2 mods=0 popup=1 page=Main/b: focus=play mods=0 popup=0 page=Main/hp2: focus=loadgame mods=0 popup=0 page=Main/y_on_hp2: focus=loadgame mods=0 popup=0 page=Main/a_on_load: focus=slot1-true mods=0 popup=0 page=LoadGame/right: focus=slot2-true mods=0 popup=0 page=LoadGame/b: focus=loadgame mods=0 popup=0 page=Main"
[ "$got" = "$want" ] && ok "pad_window_offscreen" || bad "pad_window_offscreen: got '$got'"
"$L" --pad-check | grep -q '^start: focus=play ring=0' && "$L" --pad-check | grep -q '^up: focus=menu ring=1' && ok pad_ring_after_first_press || bad pad_ring_after_first_press
# A long in-window list (author 2026-11-16, both launchers): 30 start maps open at the choice and scroll; End reaches the
# last map and scrolls it into view, Home / Page Down / type-ahead / the d-pad move through it, its indicator drags it (middle, end, top; and a real mouse drag on its track; the indicator stands where the list is after every step), A picks (--popup-check).
"$L" --popup-check > "$T/popup"
grep -q '^end: focus=Map30 popup=1 scroll=[1-9][0-9]*+[0-9]* last_in_view=1 ' "$T/popup" && grep -q '^home: focus=Game (normal start) .* last_in_view=0 ' "$T/popup" \
    && grep -q '^page_down: focus=Map06 ' "$T/popup" && grep -q '^type_m: focus=Map07 ' "$T/popup" \
    && grep -q '^pad_down_15: focus=Map22 popup=1 scroll=[1-9]' "$T/popup" && grep -qx 'chosen=Map22' "$T/popup" \
    && grep -q '^drag_mid: .* scroll=[45][0-9][0-9]+[0-9]* last_in_view=0 ' "$T/popup" && grep -q '^drag_end: .* last_in_view=1 ' "$T/popup" \
    && grep -q '^drag_top: .* scroll=[0-4]+' "$T/popup" \
    && grep -q '^mouse_drag_down: .* last_in_view=1 ' "$T/popup" \
    && ! grep -q ' ind=0' "$T/popup" \
    && ok popup_long_list_scrolls || { bad popup_long_list_scrolls; cat "$T/popup"; }

# Per game (author 2026-11-16): the Launcher's GameScope mirrors Logic.cs GameScope (--game-scope): the error banner of each
# tab (HP2's never on HP1, HP1's never on HP2, the launcher's own on both), each game's data and logs folder, the console capture.
mkdir -p "$T/scope/game"; "$L" --game-scope "$T/scope/game" > "$T/scopeswift"; DT --game-scope "$T/scope/game" > "$T/scopeowner"
cmp -s "$T/scopeswift" "$T/scopeowner" && [ "$(grep -c '^error' "$T/scopeswift")" = 16 ] && ok game_scope_as_owner || { bad game_scope_as_owner; diff "$T/scopeswift" "$T/scopeowner" | head -5; }
grep -qx 'error hp2Tab=0 hp1=- hp2=e2 launcher=- -> -' "$T/scopeswift" && grep -q "^game hp2=1 data=$T/scope/HP2Data/HPEngineData logs=$T/scope/HP2Data/HPEngineData/logs console=/d/HPEngine-HP2.log$" "$T/scopeswift" \
    && ok game_scope_hp2_apart || bad game_scope_hp2_apart
# Architecture lock (source text, CLAUDE.md §5: not proof of activation; macOS activation cannot be checked without a
# window): every start of the game process hands it the focus (Launcher.handFocus, activate(from:) on macOS 14+).
# Without it the first launch kept the game inactive: "capture on ... focus False", OS cursor visible.
python3 - "$HERE/Launcher.swift" <<'PY' && ok "game_launch_hands_focus" || bad "game_launch_hands_focus"
import re, sys
s = open(sys.argv[1], encoding='utf-8').read()
starts = s.count('runtimeURL.appendingPathComponent("HPEngine")')
hands = len(re.findall(r'try p\.run\(\); playing = true;[^\n]*\n    Launcher\.handFocus\(to: p\.processIdentifier\)', s))
sys.exit(0 if starts >= 2 and hands == starts and 'app.activate(from: .current)' in s else 1)
PY

[ $fails -eq 0 ] || exit 1
echo "all passed"
