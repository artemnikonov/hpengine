# Changelog

## 0.5.0-beta — 2026-11-20

Updating from 0.4.0: the launcher updates itself; your game, saves, settings and mods stay where they are.

New — Harry Potter and the Chamber of Secrets (first beta):

- The launcher's **Harry Potter 2** tab is open: drop your copy of the second game (the installed game folder or the
  English retail CD) onto the window and click **Install**, then **New Game** or **Load Game** from the tab. Tested on
  the English retail release and the Russian EA "Hit-Parade" edition; re-releases with changed game scripts install
  and run with a warning. See the README's [Requirements](README.md#harry-potter-2-tested-editions) and [Install](README.md#install).
- It runs on the same engine as the first game: widescreen without stretching, smooth motion at high frame rates,
  gamepad support (Back opens the map, B skips a cut-scene, the d-pad picks the duel spell), and Difficulty shown on
  the options input page for every player (not original). See [differences](docs/differences.md).
- It is installed as its own copy with its own saves and settings (`HP2Data`, see the README); your original
  installation is not changed. Saves are in HPEngine's own format: start a new game (the original's `.usa` saves are
  not read).
- Widescreen: the loading screen is a centred 4:3 picture like the menus at every resolution; the shop (vendor) bar
  keeps the world full-width (not original). Alt+Enter switches fullscreen; the options page shows the mode in use and
  the choice is kept for the next start.

Known limits of Chamber of Secrets in this beta:

- The campaign has not yet been played through to the end on HPEngine; please report anything that stops you.
- Mods are not tested yet, and the launcher cannot switch them on and off yet.
- Rebinding keys or buttons on its Controls page is not available yet.

Fixed (Philosopher's Stone):

- Loading a save made with a mod (for example Restored) while that mod is switched off restarts the level as before, but
  Harry no longer dies at once: the restart now finishes the level start the game itself does (Harry's health is
  topped up to half, as at every level start). Your save slots are still not overwritten by such a restart (not
  original: the original has no mod switch).
- If the save the game reloads after Harry dies (the level-start save) cannot be loaded — for example it was made on a
  map of a mod that is now off — the current level restarts from its beginning instead of retrying forever with Harry
  lying on the floor (not original: the original keeps retrying).
- Harry's shadow moves smoothly with Harry at high frame rates instead of stepping at 60 Hz.
- Glows drawn on moving actors (the Bludger's and the Snitch's halos), particles carried by a moving emitter, and HUD
  items (the cut-scene bars, sliding text) move smoothly at high frame rates instead of stepping at 60 Hz.
- Launchers: a game error is shown as plain text in a banner below the page; **Open log** opens the log of that run and
  × dismisses the banner. On Windows and Linux the banner looks as on macOS.
- The game's blue "Saving…" frame is shown while a game is saved (it was never drawn: the game went on at once).
- The first time the pause menu opens no longer stutters (about 0.1 s).

Fixed (both games):

- macOS: a game started from the launcher keeps the keyboard focus (before, it often stayed behind until a click).
- Launchers: the Harry Potter 2 pages (main menu, new game, load game) are in English like the rest of the launcher,
  whatever the game's language (they were partly Russian with the Russian edition).
- Russian editions of both games: menu, book and on-screen text is drawn with the book-style font Philosopher (with
  Cyrillic) instead of the plain Liberation Sans (**not original**: the original draws Windows' MS Sans Serif); mods that
  ask for another font get it too.
- macOS: the game started from the launcher gets keyboard and mouse at once, without a click (the system cursor stayed
  visible after the splash screen).
- Gamepad: the left stick works exactly like the arrow keys (no slow walk at a partial tilt); the right stick turns and
  aims fast by default (Settings: `GamepadLookSpeed`), and the Sensitivity slider applies to it.
- Steam Deck: the on-screen keyboard no longer opens when the game starts.
- Game folders copied from a Mac (with hidden `._` files next to every file) install and start; such a copy could fail
  to start, for example on the Steam Deck.
- No more occasional single-frame freezes (the game asked the system for the display's refresh rate every frame).
- After the splash the game's first moments are shown, not played behind a blank window.
- Menus and HUD are placed to the sub-pixel as in the original instead of rounded to whole pixels.
- Surfaces lying almost on top of each other (candle bases on tables, plates on walls) no longer flicker: depth is
  computed as precisely as the original's Direct3D renderer does.

## 0.4.0-beta — 2026-11-17

Updating from 0.3.0:

- A 0.3.0 launcher updates itself (macOS, Windows, Linux). Your game, saves, settings and mods stay where they are.
- Saves of 0.3.0 load in 0.4.0. Saves made by 0.4.0 use a newer save format and do not load in 0.3.0 (keep a copy of
  the `Save` folder if you want to go back).

New:

- Redesigned launchers. macOS and Windows/Linux now have the same features: the window in the selected game's colours,
  one main button per screen, rarely used actions in **Menu** (**This game**: open the saved games or logs folder,
  install again, reset settings, reset gamepad bindings, and **Remove game** last, in red; **Launcher**: check for
  updates, Add to Steam on Linux, About), the Mods card opening a Mods page, the **Language** row only when your game
  files have more than one language (now on macOS too), and the **Start map** list for mods that add maps. The Menu,
  Language and Start map lists open at their button and scroll inside the window.
- The launchers work with a gamepad (both launchers, the same layout): A selects, B goes back, LB/RB switch the game,
  Start opens the Menu, Y plays, the d-pad or stick moves between buttons; the button hints show only while a pad is
  connected.
- The launchers keep errors and logs per game: an error is shown on its game's tab, and **Open logs folder** opens that
  game's logs.
- The launcher has a Harry Potter 2 tab, shown as "Coming soon" and not selectable yet.
- Launchers: enabled mods show their apply order (a higher number wins a file both mods have), and two enabled mods
  with the same file are named on the Mods page.
- Space now also turns the first page of a story, so one-page stories can be skipped too, still only once the page has
  been shown for 2 seconds (not original).
- The log's "[Overrides] WARNING: ... differs" lines compare what a function does (its code with references resolved,
  flags, parameters), not its file bytes: a mod package recompiled without touching a function no longer reports it
  (the Unofficial Widescreen Patch: 2 lines instead of 14; Movement & Camera: none instead of 2).

Fixed:

- Two-sided faces of characters (Harry under the Invisibility Cloak in Lev4_Sneak) get the original's small depth
  offset. Wider than 800 pixels the offset stays at its 800-wide size, so Harry's robe does not show through his neck
  at high resolutions (not original: the original's offset grows with the screen width, and at 4K/5K the robe came
  through his neck in the first level's Dumbledore cutscene). At 800 wide and below the picture is the original's.
- Russian edition: menu and book text (drawn with the bundled Liberation Sans in place of MS Sans Serif) is now smooth
  (antialiased) and drawn at the interface scale instead of jagged, pixel-doubled glyphs whose thin strokes could
  vanish (not original: the original draws small aliased pixel text). Line breaks and text layout are unchanged.
- A mod that asks for a native font other than MS Sans Serif (for example 25 Years HD) no longer stops the game: every
  face is drawn with the same bundled Liberation Sans (not original: on Windows the original takes whatever substitute
  font Windows picks).
- macOS: the game started from the launcher gets the keyboard and mouse focus on its first launch.
- Lighting on moving doors and platforms is computed from the surface as it is drawn, as the original does (checked
  against the running original on 69 movers); a light-colour rounding order is corrected (it changes the lightmaps of
  Lev2_Fire2); lights that take their colour from a palette (LightType 8/9, not used by the shipped maps) are lit as in the
  original instead of being refused.
- Particles near wind (the torches in the dungeons and in Fluffy's room, Fluffy's flute effect) and the cauldron and
  lock effects drawn after them start their spin as in the original.
- Russian edition: the wand under the Invisibility Cloak (Lev4_Sneak) draws its faces in the original's order.
- Collision of moving brushes, the collision boxes of animated characters, several BSP surface tests and the reference
  pose of five meshes are now computed with the original's exact floating-point order.
- A mod's tracker song (an `.umx` module such as IT) is silent as in the original with the game's default
  `UseDigitalMusic=False`, without an error line; with `UseDigitalMusic=True` it fails to load and is logged as in the
  original, whose tracker player cannot load any known module either (a module the original could load still stops
  with a clear message).
- Music requested at a song section other than 0 (a mod's `ClientSetMusic` with a section) plays like section 0, as in
  the original, instead of stopping the game; an `OutputRate` in `Default.ini` that the original does not recognise
  (e.g. `OutputRate=22050`) now means 8000 Hz as in the original instead of an error; an mp2 sound whose frames change
  sample rate mid-stream decodes as in the original (a change of channel count still stops with a clear message).
- A script texture import (`CreateTextureFromBMP`) of an 8-bit `.pcx` file works as in the original instead of
  stopping the game, and textures named `<name>_A00`, `_A01`, ... are linked into an animation chain as the original
  does (24-bit images still stop with a clear message).
- More of the original engine is available to mods: config arrays and per-object config files, object/class/struct
  values in property text and console commands, console `SET <class> <property> <value>`, fonts with stored
  properties, the original's localized True/False words in other languages.

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
