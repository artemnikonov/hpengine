# Changelog

## 0.3.0-beta — 2026-11-15

New:

- UK edition: the British release ("Philosopher's Stone") installs and plays with its own logo and British voice-over;
  the launchers show the game's title by edition.
- Large mod packs: Restored + Free Play starts its New Game route; 169 of its 170 maps load (`Entry` has no player
  start, and the original stops on it too). Mods that bring their own classes inside a map or carry lists in placed
  actors now load (were refused before).
- Launcher self-update on Windows and Linux (signed, as on macOS). The Linux launcher offers to add HPEngine to Steam
  (only while Steam is closed, with a backup).
- An unrecognised release of the game still plays; the launcher now offers a one-click **Report** with the file hashes
  so the release can be checked and added.
- Any key skips the start-up splash screens, and Space turns a story-book page from the second page on once it has
  been shown for 2 seconds; the first page and one-page stories turn by themselves (not original: in the original only
  the hidden debug mode allows both). "Reset to defaults" of the keys also restores the gamepad's default buttons.
- Travel to another map at a named spot (`Map#Portal`, used by some mods) works.
- Swimming (not used by the original maps; for mods with water) is ported from the original engine.
- All 20 light effects of the original renderer are ported (Searchlight, FastWave, Shock, Disco, Shell, Rotor were
  missing; used by mods), each checked against the original code.
- More script functions of the original engine are available to mods (colour arithmetic, actor iterators, property
  text get/set, enum names, wind, warnings/errors).
- Game language: the Windows/Linux launcher lists the languages in your game files (multilingual discs such as the
  Nordic CD) and starts the game in the chosen one; the Nordic set is labelled as multilingual data of the UK 1.1 engine
  family. Contributed by Rasmus Kemiläinen (ranpekemi).

Fixed:

- A save-slot picture in `Save/` that is not a valid BMP no longer crashes Start Game: the slot shows without
  its picture, as in the original (issue #4).
- Mouse capture returns after switching window mode or focus (macOS with several monitors, Steam Deck windowed).
- Gamepad in Options: no double stop on a setting in the left column; a mouse click no longer flips the Controls page to
  the keyboard view. The two gamepad hint lines on the Controls page are removed.
- A missing texture or sound is logged and skipped like in the original instead of stopping the game; an unknown console
  command answers like the original.
- A thin crack of light along coplanar walls (Christmas Night mod; the same spot in Lev2_Inc_B) is gone.

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
