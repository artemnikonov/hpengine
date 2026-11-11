# OpenHP1

<p align="center">
  <a href="../../releases"><img alt="Latest release" src="https://img.shields.io/github/v/release/artemnikonov/openhp1?include_prereleases&label=release"></a>
  <a href="LICENSE"><img alt="License: GPL-3.0" src="https://img.shields.io/badge/license-GPL--3.0-blue"></a>
  <img alt="Platforms: macOS, Windows, Linux" src="https://img.shields.io/badge/platforms-macOS%20%7C%20Windows%20%7C%20Linux-lightgrey">
</p>

<p align="center">
  <b>A native engine for the 2001 PC game Harry Potter and the Sorcerer's Stone.<br>
  macOS (Apple Silicon), Windows and Linux. The original game, checked against the original.</b>
</p>

<p align="center">
  <img src="images/norbert-charlie-moon.jpg" alt="Norbert is handed over to Charlie's friends on the Astronomy Tower" width="100%">
</p>

OpenHP1 is an unofficial, open-source replacement for the engine behind **Harry Potter and the Philosopher's Stone /
Sorcerer's Stone** for PC (KnowWonder / Electronic Arts, 2001; Unreal Engine 1). It contains nothing of the game: it
reads the maps, UnrealScript code, textures, sounds and music from a copy you own and runs them on new code.

**A legally obtained copy of the game is required.** See [Requirements](#requirements).

Status: **beta (0.1.0)**. The full campaign has been completed on this build.

## Quick start

1. **Download** the package for your system from [Releases](../../releases):
   - macOS (Apple Silicon): `OpenHP1-Launcher-macOS.zip`
   - Windows: `OpenHP1-win-x64.zip`
   - Linux / Steam Deck: `OpenHP1-linux-x64.tar.gz`
2. **Add your game.** On macOS start OpenHP1 Launcher and add the game; on Windows and Linux run `HP1Setup` from the
   unpacked folder. Either accepts the CD, a disc image, the game's installer or an installed game folder, and never
   changes your files.
3. **Play.** Press **Play** in the launcher, or run `OpenHP1` from the same folder.

Step by step, with the first-launch warnings of macOS and Windows: [Install](#install).

## Contents

- [Highlights](#highlights) · [Improvements](#improvements-over-the-2001-release) · [Requirements](#requirements) · [Install](#install)
- [Known issues](#known-issues) · [Reporting a bug](#reporting-a-bug) · [FAQ](#faq) · [Contact](#contact) · [License](#license) · [Legal](#legal)
- More: [all differences from the original](docs/differences.md) · [settings](docs/settings.md) ·
  [building from source](docs/building.md) · [how the original engine works](docs/README.md)

## Highlights

- **Native, without a compatibility layer.** Built for Apple Silicon Macs, Windows x64 and Linux x64; no Wine, no
  virtual machine. The CD check and SafeDisc protection of the 2001 release are not involved, so modern Windows
  versions that refuse SafeDisc are no obstacle.
- **The game's own code, unchanged.** The shipped UnrealScript bytecode runs as it is in OpenHP1's virtual machine; the
  engine's native functions are ported from the original binaries. Game logic, physics, timing, animation and saves
  behave as in 2001.
- **Measured against the original.** Ported functions are run side by side with the original machine code in an
  emulator, frames are compared draw call by draw call with the running original game, and whole levels are played in
  both and their state compared. Every difference found so far has been fixed; please report any you notice.
- **Modern displays.** Any resolution and aspect ratio: on a wide screen you see more to the sides, the interface is
  scaled by whole pixels, and fast displays get up to 120 interpolated frames per second while the game logic keeps
  its native 60 Hz.
- **Easy setup.** Install from the CD, a disc image, the game's installer or an already installed folder; on macOS
  a launcher does this and keeps itself up to date.

<table>
  <tr>
    <td><img src="images/astronomy-tower-moon.jpg" alt="The Astronomy Tower at night" width="100%"></td>
    <td><img src="images/troll-doorway.jpg" alt="The troll in the doorway" width="100%"></td>
  </tr>
  <tr>
    <td><img src="images/common-room-fireplace.jpg" alt="The Gryffindor common room" width="100%"></td>
    <td><img src="images/library.jpg" alt="The library" width="100%"></td>
  </tr>
  <tr>
    <td><img src="images/great-staircase-hall.jpg" alt="The entrance hall and the grand staircase" width="100%"></td>
    <td><img src="images/wingardium-rooftops.jpg" alt="The rooftops of the Wingardium Leviosa challenge" width="100%"></td>
  </tr>
  <tr>
    <td><img src="images/hagrids-hut.jpg" alt="Outside Hagrid's hut" width="100%"></td>
    <td><img src="images/mirror-of-erised.jpg" alt="The Mirror of Erised" width="100%"></td>
  </tr>
</table>
<p align="center"><sub>OpenHP1 at 1920x1080 with the US 1.53 game files.</sub></p>

## Improvements over the 2001 release

The game itself is left as it was. Where OpenHP1 deliberately does something differently, it is listed here and in
detail, marked "not original", under [Differences from the original](docs/differences.md):

- **Picture:** wide-screen view without stretching (menus and 2D screens stay 4:3), sharp interface at every size,
  smooth motion up to 120 fps, 16x anisotropic and trilinear filtering, models at full detail at any distance, no dark
  band on glasses, light flares hidden behind Harry instead of shining through him.
- **Timing:** the game logic is locked to 60 Hz, as its scripts assume; it never runs fast on high-refresh displays,
  and torch flames do not stop above 128 fps as they do in the original.
- **Sound:** mixed at 44.1 kHz instead of 22 kHz; music continues from the same place after Alt-Tab or the pause menu
  and songs are no longer cut short; lowering the sound volume no longer silences the music.
- **Menus and input:** mouse-wheel scrolling in lists, every resolution offered in every window mode, readable key
  names, the mouse is captured in a window without an extra click.
- **Bugs of the original fixed:** some Bertie Bott's beans flew away from Harry at a steady frame rate; Mrs Norris
  could not climb the library bookcases above about 30 fps.
- **Saves:** OpenHP1's own save files stay loadable across OpenHP1 versions.

What we learned about how the original engine works, with addresses in the original binaries: [docs/](docs/README.md).

## Requirements

- Your copy of the PC game, one of:

| edition | support |
|---|---|
| US English 1.53 | **exact**: the reference the port is checked against |
| UK English 1.1 | supported: plays through, not yet proven exact |
| Russian (EA) | supported: plays through, not yet proven exact; menu text in a substitute font (Liberation Sans for MS Sans Serif, **not original**) |
| any other unmodified release (European English, French, German, Spanish, Italian, ...) | **unverified**: installed and run with a warning; it may not work correctly |
| the fan "1.55" patch, modded re-releases (such as the 2002 "City" disc), other modified copies | **modified**: installed and run with a warning; the port is not checked against changed game scripts |

  HP1Setup detects the edition from the files and shows it. For a release it does not recognise it shows a warning
  with the SHA-1 hashes of `System/HP.exe`, `Engine.dll`, `Render.dll` and `HPBase.u` (OpenHP1 prints the same warning
  at start): please [open an issue](https://github.com/artemnikonov/openhp1/issues) with them and say whether the
  game works.
- One of:

| platform | status |
|---|---|
| macOS, Apple Silicon (M1 or later) | developed and played on; the package is not signed or notarized |
| Windows x64 (D3D12) | installs, plays, saves and loads under CrossOver (D3D12 translated); **not verified on a Windows PC yet** |
| Linux x64 / Steam Deck (Vulkan) | runs natively on the Steam Deck (no Proton); a full playthrough there is still to do |

Intel Macs (`osx-x64`) can be built from source but are not tested.

## Install

Your game files are never changed: OpenHP1 only reads them, and HP1Setup (below) copies them into a folder of its
own and refuses to write into a folder that holds a game it did not install.

### macOS: OpenHP1 Launcher

Download `OpenHP1-Launcher-macOS.zip` from [Releases](../../releases), unpack it, move **OpenHP1 Launcher** to
Applications, start it and add your game (the installed game folder, the CD or its image, the game's installer, or a
`.zip` of the game folder). The launcher is not signed by Apple: on the first launch open **System Settings → Privacy & Security** and
click **Open Anyway** (or run `xattr -dr com.apple.quarantine "/Applications/OpenHP1 Launcher.app"`). It updates
itself, and only installs updates signed by OpenHP1.

### Windows, Linux / Steam Deck: HP1Setup

1. Download `OpenHP1-win-x64.zip` or `OpenHP1-linux-x64.tar.gz` from [Releases](../../releases) and unpack it into
   an **empty folder** (not into the game folder).
2. Run `HP1Setup` there (Windows: double-click `HP1Setup.exe`, drag the game folder or the CD drive into its window,
   Enter; Linux: `./HP1Setup <game folder, CD, installer or .zip>`; mount a disc image first). It checks the copy and
   copies the game data into `game/` in that folder. A game installer (Inno Setup `Setup.exe` with its `.bin` files,
   or InstallShield `data1.cab`) is unpacked, never run, and then checked the same way.
3. Run `OpenHP1` (`OpenHP1.exe`) from the same folder. The Windows files are not signed: if SmartScreen warns,
   choose **More info → Run anyway**.

### Manually, into the game folder

1. Install the game (or copy its installed folder from a Windows machine — on macOS and Linux you only need the files).
   The folder that contains `System/` and `Maps/` is the **game folder**. HP1Setup is not used on this path.
2. Download the archive for your platform from [Releases](../../releases):
   `OpenHP1-osx-arm64.tar.gz`, `OpenHP1-win-x64.zip` or `OpenHP1-linux-x64.tar.gz`.
3. Unpack it **into the game folder** (the archive has no wrapper directory; it adds files next to `System/` and never
   overwrites a game file), or anywhere else and pass `--root <game folder>`.
   - Windows: unpack, run `OpenHP1.exe`.
   - macOS: `tar -C <game folder> -xzf OpenHP1-osx-arm64.tar.gz`, then `xattr -dr com.apple.quarantine <game folder>`
     and run `./OpenHP1` from Terminal.
   - Linux / Steam Deck: `tar -C <game folder> -xzf OpenHP1-linux-x64.tar.gz && <game folder>/OpenHP1`. Needs a Vulkan
     driver. On a case-sensitive file system keep the original names exactly (`System`, `Maps`, `Lev_Tut1.unr`, ...).

If the game folder is not found, OpenHP1 says what is missing and exits with code 4.

Below, the **game folder** is the folder OpenHP1 runs with (with HP1Setup: its `game/`; with the launcher on macOS:
`Library/Application Support/OpenHP1/game` in your home folder). Settings changed in the game are stored in
`<game folder>/OpenHP1Data/` (with the run logs in `OpenHP1Data/logs/`); the shipped `.ini` files are never rewritten. Saves go to `<game folder>/Save/` in OpenHP1's
own format: they stay loadable across OpenHP1 versions, but saves of the
original game are not read.

## Controls and settings

Keyboard and mouse work as in the original (rebind keys in **Options → Controls**); **Alt+Enter** toggles fullscreen.
Interface size, the original-look mode and the other OpenHP1 settings: [docs/settings.md](docs/settings.md).

## Known issues

- The Windows package has been run under CrossOver only, not on a Windows PC yet. Reports are welcome.
- macOS and Windows builds are not signed: Gatekeeper and SmartScreen warn on first launch.
- UK 1.1 and Russian: supported, not yet proven exact against those builds. Russian menu text uses a substitute font
  (not original). Other releases (European English and the other languages) are unverified: they install and run with
  a warning, and nobody has checked them yet. Modified copies (patches, mods, modded re-releases) also install and run
  with a warning; please check a bug with the unmodified game before reporting it.
- No `.app` bundle in the manual archive on macOS; run from Terminal or use the launcher.
- Please report anything that differs from the original: see [Reporting a bug](#reporting-a-bug).

## Reporting a bug

Please [open an issue](../../issues/new/choose). What helps most:

- **Edition and platform:** which copy of the game (HP1Setup, the launcher and the first lines of the log name it) and
  which system and display you play on.
- **Where and what:** the map or the place in the story, what you did, what happened and what you expected. If you
  can compare with the original game, say how it behaves there.
- **The log** of that session: the newest `run_<date>_<time>.log` in `<game folder>/OpenHP1Data/logs/` (with the
  macOS launcher the game folder is `Library/Application Support/OpenHP1/game` in your home folder). It names files on your computer,
  so it may contain your user name; remove it if you prefer.
- **A save** close to the problem, if there is one: `save<N>.hp1sav` and `GameSaveInfo<N>` from `<game folder>/Save/`.

## FAQ

**Where do I get the game?** OpenHP1 needs the original PC release: the CD or a copy you installed from it. The game
is not sold digitally.

**Do my saves from the original game work?** No. OpenHP1 has its own save format, so start a new game; its saves stay
loadable in later OpenHP1 versions.

**Can I see the game exactly as it looked in 2001?** Set `VisualMode=Original` in the `[OpenHP1]` section of
`<game folder>/OpenHP1Data/Default.ini`; the 3D world is then drawn as the 2001 renderer drew it. See
[docs/settings.md](docs/settings.md).

**Does it run on Intel Macs?** It can be built from source for `osx-x64`, but this is not tested.

**Steam Deck?** The Linux package runs natively on the Deck, without Proton. A full playthrough there is still to do.

**Chamber of Secrets?** Not supported right now. It may become a separate project later.

**Is this legal?** OpenHP1 contains no code, data or assets of the original game; it runs a copy you own. See
[Legal](#legal).

## Contact

- Bugs: [Issues](../../issues) (see [Reporting a bug](#reporting-a-bug)).
- Questions, ideas, help with setup: [Discussions](../../discussions).

## Credits

- OpenHP1: Artem Nikonov.
- Original game: KnowWonder, published by Electronic Arts; Unreal Engine 1 by Epic Games.
- Third-party components (SDL3, SDL3-CS, UELib, the .NET runtime, innoextract, unshield, Liberation Sans):
  [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## License

The OpenHP1 source code is released under the GNU General Public License, version 3 or later (see `LICENSE`):
you may use, change and share it, and what you distribute must stay open under the same license.
Copyright (C) 2026 Artem Nikonov and OpenHP1 contributors. The license covers the port's code only and
grants no rights to the original game or its files. Third-party components keep their own licenses
(THIRD_PARTY_NOTICES.md).

## Legal

OpenHP1 is a fan project. It is not affiliated with, endorsed by or sponsored by Warner Bros. Entertainment, Wizarding
World Digital, Electronic Arts, KnowWonder, Epic Games or J.K. Rowling. Harry Potter names and marks are trademarks of
their owners. This repository contains no game assets, code or data of the original game; you need your own copy of
the game to use it. Do not open issues asking for game files.
