# Changelog

## 0.1.0-beta — unreleased

First public beta.

- The whole campaign has been played through on macOS (Apple Silicon) on this build.
- Licensed under the GNU General Public License, version 3 or later.
- The whole game runs from your own copy of Harry Potter 1 (PC): the original UnrealScript bytecode on OpenHP1's VM,
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
- Own save format, kept loadable across OpenHP1 versions; the original game's saves are not read.

Known issues: see README.md.

Downloads: `OpenHP1-osx-arm64.tar.gz`, `OpenHP1-win-x64.zip`, `OpenHP1-linux-x64.tar.gz` (both with HP1Setup), and
the macOS launcher `OpenHP1-Launcher-macOS.zip` (with its signature `.sig`).
