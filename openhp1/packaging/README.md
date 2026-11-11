# Building OpenHP1 for Windows / Linux / macOS

Author decisions: the package is installed OVER the user's game install, or into an empty folder where HP1Setup copies
the game data into `game/`; it contains no original files; supported platforms are Windows + macOS (and Linux /
Steam Deck).

| file | what it is |
|---|---|
| `publish.sh` | self-contained `dotnet publish` for `win-x64`, `linux-x64`, `osx-arm64` + content check + archives |
| `InstallRoot.cs` | game install lookup: `--root`, else the executable's directory, else its `game/` subfolder (HP1Setup), else a loud error (compiled into the engine; called from `Main` in `OpenHP1Program.cs`) |
| `../../.github/workflows/openhp1.yml` | CI: packaging tests, three RIDs, `--selftest-gpu` of the unpacked linux package on lavapipe |
| `../tests/PackagingTests/` | tests (`dotnet run`) and red mutations (`redtest.sh`) |
| `make_app.sh` | wraps a published `osx-arm64` directory into a plain `HP1.app` (game folder `HARRY_POTTER_US_` next to it, or `$HP1_GAME_ROOT`) — the local working build (`HP1_build/HP1.app`) (`publish.sh osx-arm64` + `make_app.sh`), not a release asset |
| `make_launcher.sh` | release build: the packages above + `HP1Setup` in each, and the macOS `OpenHP1 Launcher.app` / `OpenHP1-Launcher-macOS.zip` (section "Launcher and HP1Setup") |
| `../../launcher/` | `HP1Setup` (C#, checks and copies the player's game data, unpacks installers), `third_party.sh` (innoextract for the package), `Launcher.swift` (macOS app), `update_sign.swift` (update signing key and signatures), `us153.sha1`, `test_setup.sh`, `test_update.sh` (+ `*_redtest.sh`), `VERSION` |

## Game install marker — where it comes from

`InstallRoot` treats a directory as an install if it has the **`System`** and **`Maps`** subdirectories. That is
what the engine opens unconditionally when loading a world:

- `HP1.Core/Runtime/World/UEGameEngine.cs:40` — `Path.Combine(gameRoot, "System")`: `*.u` packages, ini;
- `HP1.Core/Runtime/World/UEGameEngine.cs:60` — `Path.Combine(gameRoot, "Maps", map + ".unr")`;
- `HP1.Core/Runtime/World/UELevelRuntime.Bootstrap.cs:94` — `System/0/Default.ini` (`File.ReadAllBytes`, unchecked).

Core reads `Textures`, `Sounds`, `Music` only if present (`UETextureSource.cs:62`, `UESoundCatalog.cs:74`,
`UEMusicCatalog.cs:50`), so they are not install markers. `System/0/Default.ini` and a specific map are NOT markers
either: the engine already fails loudly without them (`FileNotFoundException` with the path).

**Name case.** Core opens exactly `System` and `Maps`. `InstallRoot` asks the file system with the same path
(`Directory.Exists`), so its answer matches what the engine sees:

- Linux / Steam Deck (case-sensitive): a `system/` or `MAPS/` directory does NOT work — neither for the engine nor
  for `InstallRoot`; the error names the variant found. The same applies to file names inside (`Lev_Tut1.unr`,
  `Engine.u`): on Linux the install must be copied preserving the original case.
- Windows, macOS (APFS is case-insensitive by default): any case works — as for the engine.

The directory case of a particular user install is unknown here; the author's is `HARRY_POTTER_US_/System`, `/Maps`.

## Installing over the game

The archive is unpacked **into the game install root** — where `System/` and `Maps/` are. Files in the archive have
no wrapper directory, so `OpenHP1` ends up next to `System/`. The package contains no directory or file named like
anything in the install (`publish.sh` checks this), so game files are never overwritten; ~200 .NET and SDL3 files
are added to the root. To keep the root clean, unpack anywhere and run with `--root <game dir>`. HP1Setup is not
used on this path: started from a game folder without `--dest` it refuses (`already_installed`), and it never writes
into a folder holding game data it did not install (`dest_not_ours`).

- **Windows** (`OpenHP1-win-x64.zip`): unpack into the game directory, run `OpenHP1.exe`. It is a console
  application (subsystem `console`): a console window with the log opens alongside.
- **Linux / Steam Deck** (`OpenHP1-linux-x64.tar.gz`):
  `tar -C <game dir> -xzf OpenHP1-linux-x64.tar.gz && <game dir>/OpenHP1`.
  Requires a Vulkan driver (SDL_GPU). tar keeps the exec bit; zip would lose it, hence tar.gz for Linux/macOS.
  Steam Deck = linux-x64; not tested in Steam game mode.
- **macOS, Apple Silicon** (`OpenHP1-osx-arm64.tar.gz`): needs the Windows game data (`System/`, `Maps/` …) copied
  into a folder; unpack there with `tar -C <game folder> -xzf OpenHP1-osx-arm64.tar.gz`, run `./OpenHP1` from
  Terminal. A browser-downloaded file gets the Gatekeeper quarantine; remove it with
  `xattr -dr com.apple.quarantine <game folder>` (NOT verified, see below).

Without an install next to it or in its `game/` subfolder, and without `--root`, the engine prints:

```
game install not found: --root not set, next to the executable (<directory>) no directory System/; no directory Maps/, and in <directory>/game the directory does not exist.
Hint: run HP1Setup next to OpenHP1 with your copy of the game, or unpack OpenHP1 into the game install directory (where System/ and Maps/ are), or name it explicitly: OpenHP1 --root <game directory>.
```

and returns code 4.

## Build

```
sh openhp1/packaging/publish.sh                      # three RIDs -> .artifacts/openhp1-packages/
sh openhp1/packaging/publish.sh --out DIR linux-x64  # selected RIDs into DIR
sh openhp1/packaging/publish.sh --check DIR RID      # only check a finished directory
```

Requires .NET SDK 9 (or newer: tested with SDK 10.0.112 targeting net9.0 — the 9 runtime pack comes from NuGet).
All three RIDs are cross-published on one Linux machine.

Content check (fails the build with a named reason): `OpenHP1.dll` present, the RID's executable (`OpenHP1.exe` /
`OpenHP1` with the exec bit), the RID's native SDL3 (`SDL3.dll` / `libSDL3.so` / `libSDL3.dylib`, non-empty —
comes from the `ppy.SDL3-CS` package), a self-contained `runtimeconfig` (`includedFrameworks`); NO `System`, `Maps`,
`Textures`, `Sounds`, `Music`, `Save`, `port_src`, `tests` directories (case-insensitive), NO game files
(`*.u *.unr *.utx *.uax *.umx *.usx *.ukx *.int *.ini *.sav`), decompiles or sources
(`*.dll.c *.cs *.csproj *.hlsl *.md *.sh *.tsv *.yml`), NO debug symbols (`*.pdb`) and no personal data of the build
machine in any file: home-directory paths (except the CI users of the .NET runtime pack and SDL3's Ubuntu Touch
path), the author's user name, the private repository name, e-mail addresses (except the upstream authors' in
innoextract and its libraries). The build passes `-p:DebugType=none -p:DebugSymbols=false -p:PathMap=<repo>=/_/`
(also for HP1Setup, `make_launcher.sh`), so the assemblies carry no CodeView/SourceLink paths; `publish.sh --scan <dir>`
runs the personal-data part alone (`make_launcher.sh`: the Launcher `.app` before it is zipped).

### What goes into the package besides the engine

Of the project's assemblies, `dotnet publish -r linux-x64 --self-contained` outputs only `OpenHP1.dll` and
`HP1.Core.dll` (plus `Eliot.UELib.dll`, `SDL3-CS.dll`, the .NET runtime). No `*.pdb` (see the content check).

## Launcher and HP1Setup (easy install)

`HP1Setup` (`launcher/HP1Setup/Program.cs`) is the one owner of "is this copy usable, and put it where OpenHP1
looks". It finds the game in what the player gives it, checks it, and copies the data into an install root:

- **Accepted sources:** the installed game folder; the game CD (drive or mount point) or a folder copy of it; a
  `.zip` of either; on macOS also a disc image (`.iso`, `.bin`+`.cue`, `.mdf`+`.mds`; raw 2352-byte sectors are
  converted, then `hdiutil attach -readonly`); a game **installer**: Inno Setup (`Setup.exe` + `Setup-1.bin`, unpacked
  by the bundled `tools/innoextract`) or InstallShield cabinets (`data1.hdr` + `data1.cab`..., read by
  `InstallShield.cs`, a port of unshield's reader; layout `<group>/<directory>/<file>` as `unshield x`); `.7z`/`.rar`
  through the system's bsdtar (macOS `/usr/bin/tar`, Windows 10+ `tar.exe`, Linux `bsdtar` if installed; otherwise
  it asks to unpack). A folder with `System/` and `Maps/` up to 3 levels down is found; without one, archives and
  installers up to 3 levels down are unpacked into temporary folders (removed at the end), nested up to 3 deep, in
  name order. The US CD keeps the whole game unpacked at its root (`setup/` holds only the installer's own files).
  An installer that unpacks without a game folder is named (`installer_layout`); a missing innoextract too
  (`unpack_tool_missing`). Not accepted: images on Windows/Linux (asks to mount: Windows — double-click; Steam Deck —
  Dolphin, right-click, Mount). Nothing from the source is executed or written. After unpacking the copy goes through
  the same check.
- **Tested installers** (`launcher/test_setup.sh`): Inno Setup 5.4.2 (a re-release with modified scripts, also inside
  a `.zip`: named and installed with `WARNING modified`); InstallShield 6 — only the US CD's `setup/data1.hdr` (installer support
  files, compared with unshield file by file); `.7z` written by bsdtar. **Untested for lack of a sample:** an
  InstallShield cabinet holding the game itself (multi-volume, split files, obfuscation, version 5), `.rar`, Inno
  Setup 6.1 and later (innoextract 1.9 reads up to 6.0.5), bsdtar on Windows/Linux.
- **Third-party tool:** `launcher/third_party.sh <RID> <package dir>` puts `tools/innoextract` (+ `tools/licenses/`)
  into the package: the official 1.9 binaries for win-x64/linux-x64, a source build (innoextract 1.9 + Boost 1.86 +
  xz 5.6.3, static, macOS 13) for osx-arm64; every download pinned by SHA-256 (those of the files whose upstream GPG
  signatures verified). Licenses: `public/THIRD_PARTY_NOTICES.md`.
- **Check:** the edition comes from Core's `UEEdition` (`HP1.Core/Runtime/World/UEEdition.cs`, linked into HP1Setup:
  SHA-1 of `System/Engine.dll`, `Render.dll`, `HPBase.u`; the exe only names the No-CD variant), so HP1Setup and Core
  cannot disagree. Supported editions and tiers: US 1.53 — exact (`launcher/us153.sha1`, 254 files; equal to three
  separate US 1.53 copies); UK 1.1 — supported (`launcher/uk11.sha1`, 253 files: no `Textures/HPFonts.utx`); RU EA —
  supported (`launcher/ru_ea.sha1`, 254 files; menu text in the substitute font Liberation Sans, not original). Every
  file of the edition's manifest (top-level `System/*.u *.dll`, `Maps/*.unr`, `Textures/*.utx`, `Sounds/*.uax`,
  `Music/*.umx`) must be present with that size and SHA-1, plus `System/DefUser.ini` and `Default.ini` (in `System/` or
  `System/0/`). The game exe is not checked or copied in any edition. HP1Setup prints `EDITION <UEEdition id> <edition:
  tier>` (the tier is `UEEdition.Support`); the marker keeps `data=` and `edition=`, which the launcher shows. Known
  releases with modified scripts (an unofficial re-release, and the 2002 "City" disc — US 1.53 with English or Russian
  text in the `pol` language slot, not a Polish translation), a known edition with files that differ, and a known
  Engine.dll + Render.dll build with another `HPBase.u` (the unofficial 1.55) are **modified** (author decision
  2026-11-10): installed as found (no `*.exe`) with `WARNING modified <text>`; any other key-file set is **unverified** (author decision 2026-11-09): installed without a manifest (the
  same folders, no `*.exe`), with `WARNING unverified <text>` naming the SHA-1 of `System/HP.exe`, `Engine.dll`,
  `Render.dll`, `HPBase.u` and the issues link (the launcher shows it; OpenHP1 prints the same `UEEdition` warning at
  start, into its run log).
- **Destination:** `--dest`, default `game/` next to HP1Setup (`InstallRoot.cs` step 3). A destination holding game
  data (`System Maps Textures Sounds Music Help`, any case) without HP1Setup's marker (`.hp1-installed`, or
  `.hp1-installing` of an interrupted run) is refused before anything is read (`dest_not_ours`): an original install
  is never changed. Without `--dest`, next to a game (`System/` and `Maps/` beside HP1Setup) it refuses
  (`already_installed`): OpenHP1 already runs that game.
- **Copy:** `System Maps Textures Sounds Music Help` as found, without `*.exe`; checked files and the ini files renamed
  to their canonical case (Core opens `System/DefUser.ini` by exact name; other files it finds in any case through
  `UEInstallPath`, `*.int` included). `.hp1-installing` first; each directory staged as `<Dir>.new`, then the old one
  moved to `<Dir>.old` and the new one into place; `.hp1-installed` rewritten after all swaps (the old marker stays
  until then), then `.hp1-installing` and the backups removed. A failed run leaves the previous install and marker;
  the next run resumes. `Save/` merged (existing files kept); `<root>/OpenHP1/` (settings overlay, logs) never touched.
- **Protocol** (one per line): `STEP find|unpack|check|copy`, `PROGRESS 0..100`, `ERROR <key> <text>`, `DONE <root>`;
  exit 0 / 2 (source rejected) / 1. Run with no arguments in a terminal, it asks for the source.

**macOS — `OpenHP1 Launcher.app`** (`launcher/Launcher.swift`). OpenHP1 and HP1Setup are inside the app
(`Contents/Resources/OpenHP1`). The player drops the game (folder, CD, image, installer, .zip); the Launcher runs HP1Setup with
`--dest <home>/Library/Application Support/OpenHP1/game` and shows its steps and errors. **Play** starts
`OpenHP1 --root <that folder>` (console output to `<home>/Library/Logs/OpenHP1.log`). **More:** open the saved games
folder (`game/Save`), open logs (`game/OpenHP1Data/logs`), reset settings (moves `game/OpenHP1Data/Default.ini` and
`DefUser.ini` to the Trash), check for updates, about.

**Updates.** Feed: the GitHub releases of `artemnikonov/openhp1` (`OPENHP1_RELEASES_URL` overrides it; `file://`
works), newest first; drafts never; pre-releases only when this build is one (`launcher/VERSION` with a `-` suffix,
Info.plist `OpenHP1Version`). The newest such release, if newer (`0.2.0-beta` < `0.2.0`), is offered; its asset whose
name contains `Launcher` and ends in `.zip` is used with `<that name>.sig`. Integrity — chosen: an **Ed25519
signature with the public key pinned into the app at build time** (`OPENHP1_UPDATE_KEY`, `launcher/update_sign.swift`;
CryptoKit). A `.sha256` file in the same release only catches a broken download: whoever can change the release (a
leaked GitHub token) changes both files, while the signing key never leaves the author's machine. The app is ad-hoc
signed, so `codesign` alone proves nothing about who built it. Before the zip is opened its signature is checked;
then the app inside must have the bundle id `io.github.openhp1.launcher`, `OpenHP1Version` equal to the release tag
(an old signed zip under a new tag is refused) and pass `codesign --verify --deep --strict`. Only then it replaces
this app (`replaceItemAt`) and restarts. Quarantine: the verified copy is installed without the quarantine flag
(cleared on purpose: a quarantined app is translocated and cannot run its bundled tools). A build without the key,
or a release without the `.sig`, is not installed automatically: the release page opens. Updating is disabled while
the game or a setup runs (both run from inside the app). OpenHP1 is inside the app, so a Launcher update is also the
game update; game data, saves and settings stay in Application Support. The quarantine flag of a downloaded app is
cleared on the bundle before HP1Setup/OpenHP1 run; from a translocated copy the Launcher asks to be moved to
Applications first. Ad-hoc signed: the first start needs right-click → Open.

**Windows, Linux / Steam Deck — HP1Setup next to OpenHP1.** Unpack `OpenHP1-win-x64.zip` /
`OpenHP1-linux-x64.tar.gz` into an empty folder (not into the game folder: there OpenHP1 needs no setup), then:

- Windows: double-click `HP1Setup.exe`, drag the game folder or the CD drive into the window, Enter. Then run
  `OpenHP1.exe`.
- Steam Deck (desktop mode) / Linux: `./HP1Setup /run/media/deck/<CD or USB>/...` or `./HP1Setup <folder>/HP1.zip`
  (a CD image: mount it first, Dolphin → right-click → Mount, or `udisksctl loop-setup -r -f HP1.iso`). Then
  `./OpenHP1`. For game mode: Steam → Add a Non-Steam Game → `OpenHP1` in that folder.

HP1Setup copies the data into `game/` next to itself, which OpenHP1 checks third (`InstallRoot.cs`), so no `--root`
is needed. Unpacking the package over an existing install (section above) keeps working and needs no HP1Setup.

**Build:** `OPENHP1_UPDATE_KEY=<private key file> sh openhp1/packaging/make_launcher.sh [--out DIR] [--reuse] [RID ...]`
(default all three; the macOS app needs a Mac). One-time key: `xcrun swift launcher/update_sign.swift keygen <file>`,
kept outside the repository; every later release must be signed with the same key. Version: `launcher/VERSION`; the
release tag is `v<VERSION>`. Release assets for macOS: `OpenHP1-Launcher-macOS.zip` and `OpenHP1-Launcher-macOS.zip.sig`.
**Checks:** `sh launcher/test_setup.sh` — sources from environment variables only (`HP1_US` required, optional
other copies; listed in the script; unset -> NOVERDICT): the manifests against UEEdition, US folder, file inside it, a
US `.zip`, the UK and RU copies (accepted, with their EDITION id), other releases, missing map, one changed byte, one
changed byte in Engine.dll (unverified: installed, WARNING with the hashes, no exe), one changed byte in HPBase.u
(modified: installed, WARNING modified), no DefUser.ini, lower-case defuser.ini, a destination holding a game HP1Setup did
not install (refused, left byte-equal), the package inside a game folder, the default `game/`, reinstall (saves incl.
a colliding one and settings kept), a stale backup, a copy failing half way (install and marker kept). Red:
`sh launcher/test_setup_redtest.sh`. `sh launcher/test_update.sh` — the Launcher's update checks
(command-line mode, no window) on throw-away keys and ad-hoc-signed test apps: good, tampered zip, other key, bad
`.sig`, wrong version, foreign bundle id, broken code signature, quarantine cleared, the release choice; red:
`sh launcher/test_update_redtest.sh` (7 mutations). A Headless.Smoke run of Lev_Tut1 (300 ticks) on a root
installed by HP1Setup gave a journal identical to the source root.
**Not verified:** an installed US copy (all local US copies are CD layout; the top-level files checked are the
same in an installed layout of another release, so an installed US 1.53 is expected to pass); an EU (English) copy
— no EU hashes exist locally: it is refused as unknown with its hashes to report; the Launcher UI,
a real self-update from GitHub and the quarantine path of a browser-downloaded Launcher (need the author); HP1Setup
on Windows and Linux (built, not run).

## What was verified (Linux x64, .NET SDK 10.0.112)

Engine build: `dotnet build openhp1/OpenHP1.csproj -c Release` — `Build succeeded`, 0 errors.

`sh openhp1/packaging/publish.sh --out …` from a CLEAN clone of the branch:

```
== win-x64: dotnet publish
   win-x64: files 201, directory 85M, archive 37M -> …/OpenHP1-win-x64.zip
== linux-x64: dotnet publish
   linux-x64: files 201, directory 88M, archive 37M -> …/OpenHP1-linux-x64.tar.gz
== osx-arm64: dotnet publish
   osx-arm64: files 201, directory 91M, archive 34M -> …/OpenHP1-osx-arm64.tar.gz
publish: built and checked: win-x64 linux-x64 osx-arm64
```

Native libraries (`file`): `SDL3.dll` — PE32+ x86-64 DLL; `libSDL3.dylib` — Mach-O arm64; `libSDL3.so` — ELF
x86-64 (depends only on libc/libm). Executables: `OpenHP1.exe` — PE32+ x86-64 console; `OpenHP1` (osx) — Mach-O
arm64; it and `libSDL3.dylib` carry `LC_CODE_SIGNATURE` (SDK ad-hoc signature; checked by parsing load commands,
not `codesign`).

`--selftest-gpu` of the unpacked `OpenHP1-linux-x64.tar.gz` on a machine without the .NET 9 runtime (only 10 — so
the package really is self-contained), lavapipe, no window:

```
$ SDL_VIDEO_DRIVER=offscreen ./OpenHP1 --selftest-gpu st.png
GPU: vulkan, shaders SPIR-V
…
selftest-gpu PASS
rc=0
```

Tests: `DOTNET_ROLL_FORWARD=Major dotnet run --project openhp1/tests/PackagingTests -c Release`

```
PASS exe_dir_with_system_and_maps
PASS root_arg_wins_over_exe_dir
PASS root_arg_used_when_exe_dir_empty
PASS root_arg_relative_made_full
PASS bad_root_arg_no_fallback
PASS exe_dir_missing_maps
PASS exe_dir_missing_system
PASS exe_dir_missing_both
PASS root_arg_nonexistent
PASS system_is_file_not_dir
  branch: the file system is case-sensitive
PASS case_variant_matches_filesystem
PASS publish_check_accepts_each_rid
PASS publish_check_rejects_bad_outputs
TOTAL: passed 13, failed 0
```

The `case_variant_matches_filesystem` branch for a case-insensitive FS (macOS/Windows) was NOT executed here — the
test prints which branch ran. On Windows the tests refuse (code 2): they need `sh`.

Red mutations: `bash openhp1/tests/PackagingTests/redtest.sh` (sed of exactly one line, restore with `cp` + `cmp`, base
before and after; output verbatim):

```
base before mutations: TOTAL: passed 13, failed 0
RED root_arg_ignored — FAIL root_arg_wins_over_exe_dir: --root must win …
RED bad_root_falls_back — FAIL bad_root_arg_no_fallback: expected InstallRootException …
RED root_not_made_full — FAIL root_arg_relative_made_full: path is not full …
RED exe_dir_ignored — FAIL exe_dir_with_system_and_maps: game install not found …
RED maps_not_required — FAIL bad_root_arg_no_fallback: expected InstallRootException …
RED nonexistent_unchecked — FAIL root_arg_nonexistent: Could not find a part of the path …
RED case_insensitive_accept — FAIL case_variant_matches_filesystem: expected InstallRootException …
RED case_variant_unnamed — FAIL case_variant_matches_filesystem: case variant not named …
RED hint_dropped — FAIL exe_dir_missing_maps: no hint …
RED sdl_not_checked — FAIL publish_check_rejects_bad_outputs: «no SDL3.dll» (win-x64) accepted …
RED osx_sdl_wrong_name — FAIL publish_check_accepts_each_rid: osx-arm64: a correct directory rejected …
RED exec_bit_not_checked — FAIL publish_check_rejects_bad_outputs: «no execute bit» …
RED self_contained_unchecked — FAIL publish_check_rejects_bad_outputs: «not self-contained» …
RED game_files_allowed — FAIL publish_check_rejects_bad_outputs: «game map» (linux-x64) accepted …
RED sources_allowed — FAIL publish_check_rejects_bad_outputs: «source file» (linux-x64) accepted …
RED install_dirs_allowed — FAIL publish_check_rejects_bad_outputs: «System directory» (win-x64) accepted …
RED dirs_case_sensitive — FAIL publish_check_rejects_bad_outputs: «System directory» (win-x64) accepted …
BASE GREEN — TOTAL: passed 13, failed 0
mutations: 17, defects: 0
```

### CI on GitHub

First run of `.github/workflows/openhp1.yml` (run 37206192287, commit `61dcf8a`, ubuntu-24.04, setup-dotnet 9.0.x: `conclusion: success`; all steps green —
packaging tests, publish of three RIDs (~1.5 min), `--selftest-gpu` of the unpacked linux package on lavapipe.
Artifacts: `OpenHP1-win-x64` (38.0 MB), `OpenHP1-linux-x64` (38.0 MB), `OpenHP1-osx-arm64` (34.8 MB),
`selftest-gpu-png`.

## What is NOT verified

- **Running the Windows package** and **running the macOS package** — on no machine; both are only cross-published
  on Linux and checked for content and file format. Whether SDL_GPU works on D3D12 (Windows) and Metal (macOS) with
  the `*.dxil` / `*.msl` blobs in the package is not verified.
- **macOS: signing, notarization, Gatekeeper.** The package is ad-hoc signed only (SDK); no Developer ID or
  notarization. Gatekeeper will likely block a downloaded archive until `xattr -dr com.apple.quarantine` — not
  verified. No `.app` bundle: run from Terminal.
- **Windows: SmartScreen**, Authenticode signing — absent and not verified.
- **Running with game files** (`InstallRoot` → `UEGameEngine.Boot`) from the CI environment, which has no game files;
  the wiring into `Main` is not done yet (`INTEGRATION.md`) — that note is stale: `Main` now calls
  `InstallRoot.Resolve` (`OpenHP1Program.cs`).
- Directory structure and case in real installs (US disc, re-releases, Mac version of the game) are unknown; the
  marker comes from the engine code, not from an install.
- Steam Deck (game mode; Proton is not needed — the package is native) — not verified.
- `osx-x64` (Intel Mac) is not built by default; `ppy.SDL3-CS` contains it, `publish.sh osx-x64` is possible — not
  verified.
- The impact map `runtime_poc/impact_map.py` does not know the `.github/workflows/openhp1.yml` path (`--what-if`
  prints a refusal for unclassified paths). No rule was added to `RULES`: that is a `gate-core` change (full level,
  its own red mutation).
