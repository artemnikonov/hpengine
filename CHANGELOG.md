# Changelog

## 0.2.0-beta — 2026-11-13

**The project is renamed: OpenHP1 is now HPEngine.** Everything of 0.1.0 keeps working where it is, but 0.2.0 uses
new names for its launcher, folders and settings, and does not move anything by itself.

Coming from 0.1.0:

- macOS: the old "OpenHP1 Launcher" does not update to HPEngine. Download `HPEngine-Launcher-macOS.zip`, add your game
  again, then delete the old app.
- Your saves: copy the files of the old `Save` folder into the new one, after installing the game in the new launcher.
  - macOS: from `~/Library/Application Support/OpenHP1/game/Save` to `~/Library/Application Support/HPEngine/game/Save`
  - Windows: from `game\Save` in the folder where you unpacked 0.1.0 (next to `HP1Setup.exe`) to
    `%LOCALAPPDATA%\HPEngine\game\Save`
  - Linux / Steam Deck: from `game/Save` in the folder where you unpacked 0.1.0 to `~/.local/share/HPEngine/game/Save`
  - installed by hand into the game folder: the saves are in `<game folder>/Save` and stay there.
  Saves of 0.1.0 load in 0.2.0 (with mods off). Settings start fresh (they were in `OpenHP1Data`, now `HPEngineData`).
  After that the old `OpenHP1` folder (macOS: `~/Library/Application Support/OpenHP1`) can be deleted.

New:

- Launcher for Windows and Linux / Steam Deck, with the same steps as on macOS: add your game, install, play; mods,
  saved games, logs, reset settings, remove game, update notice. Unpacked into a game folder it works with that game.
- Intel Macs: the macOS Launcher is universal (Apple Silicon and Intel), and there is an `HPEngine-osx-x64.tar.gz`
  package. Tested under Rosetta on Apple Silicon, not yet on an Intel Mac.
- Mods: existing HP1 mods can be switched on and off without touching the game copy. Put a mod's files (as it ships
  them, e.g. loose `.u` packages) into `Mods/<name>/` of the game folder and enable it in the launcher or in
  `HPEngineData/Mods.ini`. Maps a mod adds are started from **Start map** in the launcher. A save made with a
  different set of script mods restarts its level with the progress you entered it with, and the game says so.
  Beta: not every mod works; large conversions (Restored + Free Play) are not supported yet.
- Gamepad (not original): one model for every controller. Left stick moves (analog), right stick looks, the buttons
  have default bindings; **Options → Controls** shows the gamepad's buttons under the names of your controller type
  and rebinds them; the d-pad and A/B work in the menus; **Reset Gamepad Bindings** in the launcher. Layout:
  docs/settings.md.
- Performance: the game logic runs on its own thread (Steam Deck OLED: about 60 → 88–90 frames per second at 90 Hz).
  `--single-thread` keeps the old loop.
- Interface Size offers only the sizes that differ on your screen.
- Errors are shown: the launchers show the engine's own error line, and the engine started by double-click shows a
  dialog when it cannot find the game, a mod is broken or the start map does not exist.
- Linux: X11 (XWayland) is preferred to native Wayland (a fullscreen hang on Steam Deck desktop mode);
  `SDL_VIDEO_DRIVER=wayland` chooses Wayland.
- An open combo list in Options no longer stays on the screen over the next page.
- Options: the resolution list is also filled when a mod's menu scripts lack the original's resolution filter (the
  Widescreen Mod, whose list is empty in the original).
- Windows and Linux archives: the launcher and `README.txt` at the top, the engine in the folder `HPEngine/`.

## 0.1.0-beta — 2026-11-11 (as OpenHP1)

First public beta.

- The whole campaign has been played through on macOS (Apple Silicon) on this build.
- Licensed under the GNU General Public License, version 3 or later.
- The whole game runs from your own copy of Harry Potter 1 (PC): the original UnrealScript bytecode on HPEngine's VM,
  the engine's native code ported to C#. Editions: US 1.53 — exact (the reference the port is checked against);
  UK 1.1 and the Russian EA release — supported (they play through, not yet proven exact). The edition is detected
  from your files and shown by HP1Setup and the launcher; a release HP1Setup does not know is installed and run as
  unverified, with a warning and the hashes to report (European English and other languages: please send yours).
  Modified copies (the fan 1.55, modded re-releases, changed game files) are installed and run with a warning.
- Russian release: its menu text is drawn with Liberation Sans in place of Windows' MS Sans Serif (**not original**:
  the shapes and line breaks of that text differ from the original game).
- New SDL3 frontend: SDL_GPU renderer (Metal, D3D12, Vulkan), 44.1 kHz mixer, keyboard and mouse.
- Packages for macOS Apple Silicon, Windows x64 and Linux x64 / Steam Deck; no game file is changed. HP1Setup checks
  your copy and copies the game data into a folder of its own; the macOS launcher does this and updates itself with
  signed updates. Windows and Linux are not verified on hardware yet.
- The improvements listed in README.md (Enhanced picture always on; Options → Video → Interface Size in place of Object Detail).
- Own save format, kept loadable across HPEngine versions; the original game's saves are not read.

Known issues: see README.md.

Downloads: `OpenHP1-osx-arm64.tar.gz`, `OpenHP1-win-x64.zip`, `OpenHP1-linux-x64.tar.gz` (both with HP1Setup), and
the macOS launcher `OpenHP1-Launcher-macOS.zip` (with its signature `.sig`).
