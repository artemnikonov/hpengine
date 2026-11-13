# Building HPEngine for Windows / Linux / macOS

Author decisions: the package is installed OVER the user's game install, or into an empty folder where HP1Setup copies
the game data into `game/`; it contains no original files; supported platforms are Windows + macOS (and Linux /
Steam Deck).

| file | what it is |
|---|---|
| `ModOverlay.cs` | mods: the working copy of the install with the enabled mods (`<data>/Mods.ini`) linked over it; header has the rules |
| `publish.sh` | self-contained `dotnet publish` for `win-x64`, `linux-x64`, `osx-arm64`, `osx-x64` + content check + archives |
| `InstallRoot.cs` | game install lookup: `--root`, else the executable's directory, else its parent (the release archive keeps the engine in `HPEngine/`), else its `game/` subfolder (HP1Setup), else a loud error (compiled into the engine; called from `Main` in `HPEngineProgram.cs`) |
| `../../.github/workflows/hpengine.yml` | CI: packaging tests, four RIDs, `--selftest-gpu` of the unpacked linux package on lavapipe |
| `../tests/PackagingTests/` | tests (`dotnet run`) and red mutations (`redtest.sh`) |
| `make_app.sh` | wraps a published `osx-arm64` directory into a plain `HP1.app` (game folder `HARRY_POTTER_US_` next to it, or `$HP1_GAME_ROOT`) — the local working build (`HP1_build/HP1.app`) (`publish.sh osx-arm64` + `make_app.sh`), not a release asset |
| `make_launcher.sh` | release build: the packages above + `HP1Setup` in each, the Windows/Linux archives with the Launcher (`HPEngine-win-x64.zip`, `HPEngine-linux-x64.tar.gz`), and the macOS `HPEngine Launcher.app` / `HPEngine-Launcher-macOS.zip` (section "Launcher and HP1Setup") |
| `../../launcher/` | `HP1Setup` (C#, checks and copies the player's game data, unpacks installers), `third_party.sh` (innoextract for the package), `Launcher.swift` (macOS app), `Desktop/` (Windows/Linux Launcher, Avalonia; `Desktop.Tests/`, `test_desktop.sh`), `icon.swift` (the Launcher's shield icon), `update_sign.swift` (update signing key and signatures), `us153.sha1`, `uk11.sha1`, `ru_ea.sha1`, `test_setup.sh`, `test_update.sh` (+ `*_redtest.sh`), `VERSION` |

## Game install marker — where it comes from

`InstallRoot` treats a directory as an install if it has the **`System`** and **`Maps`** subdirectories. That is
what the engine opens unconditionally when loading a world:

- `HP1.Core/Runtime/World/UEGameEngine.cs` — `Path.Combine(gameRoot, "System")`: `*.u` packages, ini;
- `HP1.Core/Runtime/World/UEGameEngine.cs` — `Path.Combine(gameRoot, "Maps", mapName + ".unr")`;
- `HP1.Core/Runtime/World/UELevelRuntime.Bootstrap.cs` — `System/0/Default.ini` (`File.ReadAllBytes`, unchecked).

Core reads `Textures`, `Sounds`, `Music` only if present (`UETextureSource.cs`, `UESoundCatalog.cs`,
`UEMusicCatalog.cs`), so they are not install markers. `System/0/Default.ini` and a specific map are NOT markers
either: the engine already fails loudly without them (`FileNotFoundException` with the path).

**Name case.** Core opens exactly `System` and `Maps`. `InstallRoot` asks the file system with the same path
(`Directory.Exists`), so its answer matches what the engine sees:

- Linux / Steam Deck (case-sensitive): a `system/` or `MAPS/` directory does NOT work — neither for the engine nor
  for `InstallRoot`; the error names the variant found. The same applies to file names inside (`Lev_Tut1.unr`,
  `Engine.u`): on Linux the install must be copied preserving the original case.
- Windows, macOS (APFS is case-insensitive by default): any case works — as for the engine.

The directory case of a particular user install is unknown here; the author's is `HARRY_POTTER_US_/System`, `/Maps`.

## Installing over the game

The archive is unpacked **into the game install root** — where `System/` and `Maps/` are. The osx archive has
no wrapper directory, so `HPEngine` ends up next to `System/`; the win/linux release archive adds the Launcher, `README.txt`
and the folder `HPEngine/` (the engine is then `HPEngine/HPEngine`; step 2b of the lookup). The package contains no directory or file named like
anything in the install (`publish.sh` checks this), so game files are never overwritten; the osx archive adds
~200 .NET and SDL3 files to the root. To keep the root clean, unpack anywhere and run with `--root <game dir>`. HP1Setup is not
used on this path: started from a game folder without `--dest` it refuses (`already_installed`), and it never writes
into a folder holding game data it did not install (`dest_not_ours`).

- **Windows** (`HPEngine-win-x64.zip`): unpack into the game directory, run `HPEngine Launcher.exe` or
  `HPEngine\HPEngine.exe`. The engine is a windowed application (`WinExe`): no console window; the log is in
  `HPEngineData\logs`, a start-up failure is shown in a dialog (`FatalDialog.cs`).
- **Linux / Steam Deck** (`HPEngine-linux-x64.tar.gz`):
  `tar -C <game dir> -xzf HPEngine-linux-x64.tar.gz && <game dir>/HPEngine/HPEngine`.
  Requires a Vulkan driver (SDL_GPU). tar keeps the exec bit; zip would lose it, hence tar.gz for Linux/macOS.
  Steam Deck = linux-x64; not tested in Steam game mode.
- **macOS, Apple Silicon** (`HPEngine-osx-arm64.tar.gz`): needs the Windows game data (`System/`, `Maps/` …) copied
  into a folder; unpack there with `tar -C <game folder> -xzf HPEngine-osx-arm64.tar.gz`, run `./HPEngine` from
  Terminal. A browser-downloaded file gets the Gatekeeper quarantine; remove it with
  `xattr -dr com.apple.quarantine <game folder>` (NOT verified, see below).

Without an install next to it, in its parent or in its `game/` subfolder, and without `--root`, the engine prints:

```
game install not found: --root not set, next to the executable (<directory>) no directory System/; no directory Maps/, in its parent <parent> no directory System/; no directory Maps/, and in <directory>/game the directory does not exist.
Hint: the easy way is the HPEngine Launcher: it installs your copy of the game and starts it. Or run HP1Setup next to HPEngine with your copy of the game, or unpack HPEngine into the game install directory (where System/ and Maps/ are), or name it explicitly: HPEngine --root <game directory>.
```

and returns code 4.

A start map that does not exist (`--map <name>` with no `Maps/<name>.unr` in the game or in an enabled mod) stops the
windowed game with the line `[Map] there is no map "<name>": ...` and code 6.

## Build

```
sh hpengine/packaging/publish.sh                      # four RIDs -> .artifacts/hpengine-packages/
sh hpengine/packaging/publish.sh --out DIR linux-x64  # selected RIDs into DIR
sh hpengine/packaging/publish.sh --check DIR RID      # only check a finished directory
```

Requires .NET SDK 9 (or newer: tested with SDK 10.0.112 targeting net9.0 — the 9 runtime pack comes from NuGet).
All four RIDs are cross-published on one Linux machine.

Content check (fails the build with a named reason): `HPEngine.dll` present, the RID's executable (`HPEngine.exe` /
`HPEngine` with the exec bit), the RID's native SDL3 (`SDL3.dll` / `libSDL3.so` / `libSDL3.dylib`, non-empty —
comes from the `ppy.SDL3-CS` package), a self-contained `runtimeconfig` (`includedFrameworks`); NO `System`, `Maps`,
`Textures`, `Sounds`, `Music`, `Save`, `tests` directories (case-insensitive), NO game files
(`*.u *.unr *.utx *.uax *.umx *.usx *.ukx *.int *.ini *.sav`), decompiles or sources
(`*.dll.c *.cs *.csproj *.hlsl *.md *.sh *.tsv *.yml`), NO debug symbols (`*.pdb`) and no personal data of the build
machine in any file: home-directory paths (except the CI users of the .NET runtime pack and SDL3's Ubuntu Touch
path), the author's user name, the private repository name, e-mail addresses (except the upstream authors' in
innoextract and its libraries). The build passes `-p:DebugType=none -p:DebugSymbols=false -p:PathMap=<repo>=/_/`
(also for HP1Setup, `make_launcher.sh`), so the assemblies carry no CodeView/SourceLink paths; `publish.sh --scan <dir>`
runs the personal-data part alone (`make_launcher.sh`: the Launcher `.app` before it is zipped).

### What goes into the package besides the engine

Of the project's assemblies, `dotnet publish -r linux-x64 --self-contained` outputs only `HPEngine.dll` and
`HP1.Core.dll` (plus `Eliot.UELib.dll`, `SDL3-CS.dll`, the .NET runtime). No `*.pdb` (see the content check).

## Launcher and HP1Setup (easy install)

`HP1Setup` (`launcher/HP1Setup/Program.cs`) is the one owner of "is this copy usable, and put it where HPEngine
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
  xz 5.6.3, static, macOS 13) for osx-arm64 and osx-x64; every download pinned by SHA-256 (those of the files whose upstream GPG
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
  `Render.dll`, `HPBase.u` and the issues link (the launcher shows it; HPEngine prints the same `UEEdition` warning at
  start, into its run log).
- **Destination:** `--dest`, default `game/` next to HP1Setup (`InstallRoot.cs` step 3). A destination holding game
  data (`System Maps Textures Sounds Music Help`, any case) without HP1Setup's marker (`.hp1-installed`, or
  `.hp1-installing` of an interrupted run) is refused before anything is read (`dest_not_ours`): an original install
  is never changed. Without `--dest`, next to a game (`System/` and `Maps/` beside HP1Setup) it refuses
  (`already_installed`): HPEngine already runs that game.
- **Copy:** `System Maps Textures Sounds Music Help` as found, without `*.exe`; checked files and the ini files renamed
  to their canonical case (Core opens `System/DefUser.ini` by exact name; other files it finds in any case through
  `UEInstallPath`, `*.int` included). `.hp1-installing` first; each directory staged as `<Dir>.new`, then the old one
  moved to `<Dir>.old` and the new one into place; `.hp1-installed` rewritten after all swaps (the old marker stays
  until then), then `.hp1-installing` and the backups removed. A failed run leaves the previous install and marker;
  the next run resumes. `Save/` merged (existing files kept); `<root>/HPEngineData/` (settings overlay, logs) never touched.
- **Protocol** (one per line): `STEP find|unpack|check|copy`, `PROGRESS 0..100`, `EDITION <id> <edition: tier>`,
  `WARNING <key> <text>`, `ERROR <key> <text>`, `DONE <root>`;
  exit 0 / 2 (source rejected) / 1. Run with no arguments in a terminal, it asks for the source.

**macOS — `HPEngine Launcher.app`** (`launcher/Launcher.swift`), universal: the Launcher binary has arm64 and x86_64
slices (lipo), and HPEngine with HP1Setup is inside the app once per architecture (`Contents/Resources/HPEngine` — arm64,
`Contents/Resources/HPEngine-x64` — x86_64); each slice starts the runtime of its own architecture (`make_launcher.sh`
checks it through `Launcher --runtime-path` under `arch -arm64` / `arch -x86_64`). The player drops the game (folder, CD, image, installer, .zip); the Launcher runs HP1Setup with
`--dest <home>/Library/Application Support/HPEngine/game` and shows its steps and errors. **Play** starts
`HPEngine --root <that folder> --launched-by-launcher` (console output to `<home>/Library/Logs/HPEngine.log`; with a
**Start map** chosen also `--map <name>`: the list is shown while an enabled mod adds maps). **More:** open the saved games
folder (`game/Save`), open logs (`game/HPEngineData/logs`), reset settings (moves `game/HPEngineData/Default.ini` and
`DefUser.ini` to the Trash), reset gamepad bindings, remove game, check for updates, about.

**Remove game** (More menu, both Launchers; disabled while a setup runs or the game plays): after a confirmation naming
`<data>/game`, the installed game copy goes, while `Save/`, `HPEngineData/` and `Mods/` are kept; the checkbox "Also delete
saved games, settings and mods" removes the whole folder. The `.hp1-installed` marker is deleted first (an interrupted
removal reads as not installed), links are never followed. macOS: Trash; Windows: Recycle Bin; Linux: permanent delete
(said in the dialog). A reinstall merges the kept `Save/` (HP1Setup keeps existing files). Logic: `GameRemoval` in
`launcher/Desktop/Logic.cs`, `Launcher.removeGame` (`Launcher --remove-game <root> <keep|all>`) in `Launcher.swift`.

**Updates.** Feed: the GitHub releases of `artemnikonov/hpengine` (`HPENGINE_RELEASES_URL` overrides it; `file://`
works), newest first; drafts never; pre-releases only when this build is one (`launcher/VERSION` with a `-` suffix,
Info.plist `HPEngineVersion`). The newest such release, if newer (`0.2.0-beta` < `0.2.0`), is offered; its asset whose
name contains `Launcher` and ends in `.zip` is used with `<that name>.sig`. Integrity — chosen: an **Ed25519
signature with the public key pinned into the app at build time** (`HPENGINE_UPDATE_KEY`, `launcher/update_sign.swift`;
CryptoKit). A `.sha256` file in the same release only catches a broken download: whoever can change the release (a
leaked GitHub token) changes both files, while the signing key never leaves the author's machine. The app is ad-hoc
signed, so `codesign` alone proves nothing about who built it. Before the zip is opened its signature is checked;
then the app inside must have the bundle id `io.github.hpengine.launcher`, `HPEngineVersion` equal to the release tag
(an old signed zip under a new tag is refused) and pass `codesign --verify --deep --strict`. Only then it replaces
this app (`replaceItemAt`) and restarts. Quarantine: the verified copy is installed without the quarantine flag
(cleared on purpose: a quarantined app is translocated and cannot run its bundled tools). A build without the key,
or a release without the `.sig`, is not installed automatically: the release page opens. Updating is disabled while
the game or a setup runs (both run from inside the app). HPEngine is inside the app, so a Launcher update is also the
game update; game data, saves and settings stay in Application Support. The quarantine flag of a downloaded app is
cleared on the bundle before HP1Setup/HPEngine run; from a translocated copy the Launcher asks to be moved to
Applications first. Ad-hoc signed: the first start needs right-click → Open.

**Windows, Linux / Steam Deck — `HPEngine Launcher.exe` / `HPEngine-Launcher`** (`launcher/Desktop/`, C# + Avalonia 11.3.23,
one self-contained single file, not trimmed: ~44 MB; WinExe, no console window). It finds `HPEngine` and `HP1Setup` in the
`HPEngine/` folder beside itself (the only layout `make_launcher.sh` builds; there is no dev layout) and does what the macOS Launcher does: drop or add the game (folder, CD folder, installer, .zip),
run `HP1Setup --dest <data>/game` and show its steps, progress, edition, warnings and errors; **Play** starts
`HPEngine --root <data>/game --launched-by-launcher` (and `--map <name>` from **Start map**; console output to `<data>/HPEngine.log`; on an error exit the banner
shows the engine's own error line; the flag keeps the engine's own error dialog off, `FatalDialog.cs`). Unpacked into the
player's game folder (the Launcher next to `System/` and `Maps/`, `InstallRoot.Missing`), that folder is the game
instead of `<data>/game`: nothing is installed, **Remove Game** is off, the log is `HPEngineData/HPEngine.log` there; **More:** open saved games (`game/Save`), open
logs (`game/HPEngineData/logs`), reset settings (`Default.ini` and `DefUser.ini` of `game/HPEngineData` are moved to
`game/HPEngineData/settings-backup/`, there is no trash on Linux), reset gamepad bindings, remove game, check for updates, about. The window icon is the
Launcher's own shield (`ShieldIcon.cs`, drawn in code; the game's `hp.ico` is on the game window). `<data>` (no admin rights): Windows `%LOCALAPPDATA%\HPEngine`,
Linux `$XDG_DATA_HOME/HPEngine` (absolute path only; default `~/.local/share/HPEngine`), macOS `~/Library/Application
Support/HPEngine`. Disc images are not unpacked here: mount them and add the mounted disc. **Mods:** a list of the folders
in `<game>/Mods` with a checkbox and up/down per enabled mod, "Open Mods Folder"; it writes `<game>/HPEngineData/Mods.ini`
(`[Mods]` + one `Enabled=<name>` per enabled mod, in order, a later mod wins; comments and other sections are kept).
**Updates (Windows/Linux, v1):** no self-update; "Check for Updates" (and a check at start) reads the same GitHub feed
with the same version rule as the macOS Launcher (pre-releases only for a pre-release build) and opens the release page.
Checks: `sh launcher/test_desktop.sh` (+ `test_desktop_redtest.sh`).

**Archive layout (win-x64, linux-x64), like the macOS app — everything inside, one thing to click:**

```
HPEngine-win-x64.zip:       HPEngine Launcher.exe   README.txt   HPEngine\   (engine: HPEngine.exe, HP1Setup.exe, tools\, .NET + SDL3 files)
HPEngine-linux-x64.tar.gz:  HPEngine-Launcher       README.txt   HPEngine/   (engine: HPEngine, HP1Setup, tools/, .NET + SDL3 files)
```

`make_launcher.sh` makes these RELEASE archives; `publish.sh` alone (CI) still makes the flat engine-only
`HPEngine-<rid>` folders and archives (the content check and `--selftest-gpu` run on those), under the same file names, so
the two must not share an `--out`. `publish.sh --check` runs on the engine folder before it is staged as `HPEngine/`.
Unpacked INTO the game folder (the manual way), the archive adds three entries next to `System/` and the engine finds the
game in the parent of its folder (`InstallRoot.cs` step 2b). The folder `HPEngine/` there is the engine's:
settings and logs go to `HPEngineData/` (`InstallRoot.DataDir`) and `ModOverlay` never links it into the working copy.

**Windows, Linux / Steam Deck — HP1Setup without the Launcher.** Unpack `HPEngine-win-x64.zip` /
`HPEngine-linux-x64.tar.gz` into an empty folder (not into the game folder: there HPEngine needs no setup), then in `HPEngine/`:

- Windows: double-click `HP1Setup.exe`, drag the game folder or the CD drive into the window, Enter. Then run
  `HPEngine.exe`.
- Steam Deck (desktop mode) / Linux: `./HP1Setup /run/media/deck/<CD or USB>/...` or `./HP1Setup <folder>/HP1.zip`
  (a CD image: mount it first, Dolphin → right-click → Mount, or `udisksctl loop-setup -r -f HP1.iso`). Then
  `./HPEngine`. For game mode: Steam → Add a Non-Steam Game → `HPEngine` in that folder.

HP1Setup copies the data into `game/` next to itself (`HPEngine/game/`), which HPEngine checks last (`InstallRoot.cs`), so no `--root`
is needed. Unpacking the package over an existing install (section above) keeps working and needs no HP1Setup.

**Build:** `HPENGINE_UPDATE_KEY=<private key file> sh hpengine/packaging/make_launcher.sh [--out DIR] [--reuse] [RID ...]`
(default all four RIDs; the macOS app and the Windows `.exe` icon need a Mac). One-time key: `xcrun swift launcher/update_sign.swift keygen <file>`,
kept outside the repository; every later release must be signed with the same key. Version: `launcher/VERSION`; the
release tag is `v<VERSION>`. Release assets for macOS: `HPEngine-Launcher-macOS.zip` and `HPEngine-Launcher-macOS.zip.sig`.
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
— no EU hashes exist locally: it is installed as unverified, with its hashes to report; the Launcher UI,
a real self-update from GitHub and the quarantine path of a browser-downloaded Launcher (need the author); HP1Setup
on a Windows PC (the Windows package has been run under CrossOver only).

## What was verified (Linux x64, .NET SDK 10.0.112)

Engine build: `dotnet build hpengine/HPEngine.csproj -c Release` — `Build succeeded`, 0 errors.

`sh hpengine/packaging/publish.sh --out …` from a CLEAN clone of the branch:

```
== win-x64: dotnet publish
   win-x64: files 201, directory 85M, archive 37M -> …/HPEngine-win-x64.zip
== linux-x64: dotnet publish
   linux-x64: files 201, directory 88M, archive 37M -> …/HPEngine-linux-x64.tar.gz
== osx-arm64: dotnet publish
   osx-arm64: files 201, directory 91M, archive 34M -> …/HPEngine-osx-arm64.tar.gz
publish: built and checked: win-x64 linux-x64 osx-arm64
```

Native libraries (`file`): `SDL3.dll` — PE32+ x86-64 DLL; `libSDL3.dylib` — Mach-O arm64; `libSDL3.so` — ELF
x86-64 (depends only on libc/libm). Executables: `HPEngine.exe` — PE32+ x86-64 console; `HPEngine` (osx) — Mach-O
arm64; it and `libSDL3.dylib` carry `LC_CODE_SIGNATURE` (SDK ad-hoc signature; checked by parsing load commands,
not `codesign`).

`--selftest-gpu` of the unpacked `HPEngine-linux-x64.tar.gz` on a machine without the .NET 9 runtime (only 10 — so
the package really is self-contained), lavapipe, no window:

```
$ SDL_VIDEO_DRIVER=offscreen ./HPEngine --selftest-gpu st.png
GPU: vulkan, shaders SPIR-V
…
selftest-gpu PASS
rc=0
```

Tests: `DOTNET_ROLL_FORWARD=Major dotnet run --project hpengine/tests/PackagingTests -c Release`

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

Red mutations: `bash hpengine/tests/PackagingTests/redtest.sh` (sed of exactly one line, restore with `cp` + `cmp`, base
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

First run of `.github/workflows/hpengine.yml` (run 37206192287, commit `61dcf8a`, ubuntu-24.04, setup-dotnet 9.0.x: `conclusion: success`; all steps green —
packaging tests, publish of three RIDs (~1.5 min), `--selftest-gpu` of the unpacked linux package on lavapipe.
Artifacts: `HPEngine-win-x64` (38.0 MB), `HPEngine-linux-x64` (38.0 MB), `HPEngine-osx-arm64` (34.8 MB),
`selftest-gpu-png`.

## What is NOT verified

- **Running the Windows package on a Windows PC**: it has been run under CrossOver only (D3D12 translated). The
  macOS package is the one the game is developed and played on (Apple Silicon, Metal).
- **macOS: signing, notarization, Gatekeeper.** The package is ad-hoc signed only (SDK); no Developer ID or
  notarization. Gatekeeper will likely block a downloaded archive until `xattr -dr com.apple.quarantine` — not
  verified. No `.app` bundle: run from Terminal.
- **Windows: SmartScreen**, Authenticode signing — absent and not verified.
- **Running with game files** (`InstallRoot` → `UEGameEngine.Boot`) from the CI environment, which has no game files.
- Directory structure and case in real installs (US disc, re-releases, Mac version of the game) are unknown; the
  marker comes from the engine code, not from an install.
- Steam Deck (game mode; Proton is not needed — the package is native) — not verified.
- `osx-x64` (Intel Mac) is verified only under Rosetta on Apple Silicon (2026-11-12: `--selftest-gpu` 0 mismatches and
  PNG identical to osx-arm64; `--shot` Lev_Tut1 after 300 ticks byte-identical to osx-arm64 built from the same commit;
  the window reaches the main menu), not on Intel hardware.
