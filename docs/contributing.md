# Contributing to HPEngine

This guide is for people and for AI coding agents. Agents: read [AGENTS.md](../AGENTS.md) too; it states the same rules
as a checklist. For the map of the code, read [Architecture](architecture.md).

## What HPEngine is

HPEngine runs the 2001 PC game Harry Potter and the Philosopher's Stone / Sorcerer's Stone (Unreal Engine 1) on new
code. Core (`HP1.Core/`) executes the game's original UnrealScript bytecode and C# ports of the original engine's
native functions. The frontend (`hpengine/`) draws, plays sound and passes input. The launcher (`launcher/`) installs
and starts the game. The repository contains nothing of the game: it reads a copy the player owns.

**The one principle: behaviour matches the original game, proven against the original binaries.** A deliberate
difference is allowed only when it is better for the player, agreed with the maintainer and marked "not original".

## Quick start

Requirements: the .NET SDK 9 or newer (projects target `net9.0`). SDL3 comes from the `ppy.SDL3-CS` NuGet package;
the compiled shader blobs are committed, so no shader compiler is needed unless you edit a shader.

    dotnet build hpengine/HPEngine.csproj -c Release
    dotnet run --project hpengine/HPEngine.csproj -c Release -- --root <game folder>

The game folder is an installed copy of any supported edition (see [Game editions](editions.md); US 1.53 is the
reference, UK 1.1 and the Russian EA release are supported). It is the folder that contains `System/` and `Maps/`; the
main README explains how to install the game. Without `--root` the engine looks in its own folder, then in the parent
folder, then in a `game/` subfolder (`hpengine/packaging/InstallRoot.cs`). Logs of every run go to
`<game folder>/HPEngineData/logs/`.

Useful options (all parsed in `hpengine/HPEngineProgram.cs`):

| option | effect |
|---|---|
| `--map Lev_Tut1` | start on a map instead of the shipped start map |
| `--shot out.png` | no window: boot, tick, render one frame to a PNG with timings |
| `--perf-title` | frame rate and Core / render times in the window title |
| `--debug-cam` | `F` toggles a free camera (the game gets no input meanwhile) |
| `--selftest-gpu out.png` | GPU self-test on synthetic scenes, no game files needed (see `hpengine/README.md`) |

Release packages (self-contained; `win-x64`, `linux-x64`, `osx-arm64`, `osx-x64`; all can be built on one machine):

    sh hpengine/packaging/publish.sh                     # all of them
    sh hpengine/packaging/publish.sh --out DIR osx-arm64 # one, into DIR
    sh hpengine/packaging/make_launcher.sh               # release archives (the macOS Launcher.app needs macOS)

`publish.sh` fails if a package would contain a file of the original game. The macOS Launcher is
`launcher/Launcher.swift`, the Windows/Linux Launcher is `launcher/Desktop/HPEngineLauncher.csproj`, the installer is
`launcher/HP1Setup/HP1Setup.csproj`.

Shaders: after editing `hpengine/shaders/classic.hlsl` or `hpengine/Hud/shaders/hud.hlsl`, rebuild the blobs with
`sh hpengine/shaders/build.sh` (or `sh hpengine/Hud/shaders/build.sh`) and check them with `--check`. The DXC and
SPIRV-Cross versions that reproduce the committed blobs byte for byte are listed in `hpengine/README.md`.

## Making a change

### Checks you can run

| check | needs game files | what it covers |
|---|---|---|
| `dotnet build hpengine/HPEngine.csproj -c Release` | no | everything compiles (Core, frontend) |
| `sh hpengine/tests/run_all.sh` | no | every test project in `hpengine/tests/` (in the public repository: the ones that need no game files, e.g. `CoreNativesTests`, `HudTests`, `PacingTests`, `SpritesTests`); exit 0 when all pass |
| `sh hpengine/tests/style_check.sh <base commit>` | no | C# files you changed get no more whitespace findings than before (`.editorconfig`) |
| `--selftest-gpu out.png` (with `SDL_VIDEO_DRIVER=offscreen` on a headless machine) | no | the main shader, pipelines and blending against values computed from the formulas; needs a GPU or lavapipe; exit 0 pass, 1 fail, 2 no GPU, 3 refused |
| `sh hpengine/shaders/build.sh --check` | no | committed blobs match the HLSL source (needs DXC and SPIRV-Cross) |
| `sh launcher/test_update.sh` | no | the macOS Launcher's update verification and command-line checks (macOS only; exit 3 elsewhere) |
| `sh launcher/test_desktop.sh` | no | the Windows/Linux Launcher's logic and a headless window (no window opens) |
| playing the affected place with `--root` | yes | what the change does in the game |

CI (`.github/workflows/build.yml`) runs the style check, the build, `run_all.sh` and the package build on every push
and pull request. A typical change should pass the build and `run_all.sh` locally, plus a play of the place it affects.

Exit code 3 (`REFUSED`, `NOVERDICT`) means the code path a test compares did not execute. That is not a pass: find out
why the path did not run.

### Checks only the maintainer can run

Proof against the original needs the original game files, the original DLLs with their disassembly, and recordings of
the running original game, none of which can be published. The maintainer therefore runs a private set of judges: the
original native bodies executed in an emulator and compared with the port, deterministic replays of whole maps, and
draw-call logs of the original renderer compared with the port's frame. They run against the reference edition,
**US 1.53**, because their recorded corpora were captured on it; the product still supports the other editions listed
in [Game editions](editions.md).

**In the pull request, say what the change touches that only these can judge** (a native body, physics, collision,
animation, timing, sound, saves, what is drawn), which original function you ported or changed, and what you checked
yourself. The maintainer runs the matching judge.

## Rules

**Exactness.** Everything the player sees as world behaviour matches the original: game logic, physics, collision,
movement, animation and poses, timing, sound, progress, saves, level data, and what is drawn (which surfaces and actors,
with which blending, colour and placement). What the player cannot see (draw scheduling, caches, which emitter is
processed first) may be simpler. A number that only sets how a pixel looks (a colour or alpha channel, a sub-pixel
screen position) may differ by ±1 in an 8-bit channel and by less than 1/16 pixel; anything discrete or read by game
logic stays exact.

**Improvements are marked.** A difference from the original is allowed only when it is visibly better for the player
and the maintainer agrees. It is marked "not original" in the code and listed in
[Differences from the original](differences.md). Logic, physics and progress are never "improved". Script functions
replaced by port code go in `HP1.Core/Runtime/VM/UEPortScriptOverrides.cs` only.

**One owner.** Game state, visibility, poses, the frame tree, coronas and script-visible sound channels belong to Core.
The frontend draws, mixes and passes input; it never writes Core state (no property writes, no Core caches, no calls
with side effects), and render code never draws from the game's random stream. A frontend change that needs a new value
gets it from a new field of the Core frame (`WorldFrame`, listed in `HP1.Core/Runtime/VM/WorldContract.cs`). Replacing
an owner is one change: new owner, callers moved, old code deleted.

**Sources.** Port from the original binaries: the disassembly of the shipped DLLs, with a decompiler as a reading aid,
then the UnrealScript bytecode and the shipped data. SurrealEngine, Epic's Unreal source and headers (and headers built
from them), and other reimplementations are not sources. A claim that the original does *not* do something needs the
disassembly, because decompilers drop calls and arguments. No numbers chosen by eye, no multipliers without a source, no
special cases by asset or class name. Name lookups (animations, packages) are exact and case-insensitive, like UE1
`FName`.

**Origin tag.** Directly above every function that ports an original body, one line per original function (see
[How a body cites its source](architecture.md#how-a-body-cites-its-source)):

    // Original: Engine.dll ?PlayAnim@AActor@@QAEHVFName@@_NMMMW4EAnimType@@0@Z [HP1 0x10408E20]

The decorated export name, then the game and the address of the real body (with incremental linking `?Foo@...` is a
`jmp` thunk; cite the body it jumps to). A non-exported function: `not exported: FUN_xxxxxxxx [HP1 0x...]` plus how to
find it again (an exported caller or callee, a string, a cross-reference).

**One core for every game the engine supports.** Code that more than one supported game runs is written once. A real
difference is a branch on the game profile at the line that differs, never a copied function; read both bodies before
branching, since many apparent differences are only build artifacts. Never mix one game's binaries or listings with
another's, and name the game of every address you cite.

**Unknown is said out loud.** If the correct behaviour cannot be established from the original, do not guess a
plausible default (a nearest value, a reference pose, an empty list): describe what is unknown in the pull request, with
the options if there are several.

**A check must be able to fail.** Show that a new check turns red when you introduce the error it is meant to catch,
and make it report (exit 3) instead of passing when the compared code path did not execute. A snapshot of the port's
own output only shows change, not correctness.

**Nothing of the game in the repository.** No game files, extracted data, screenshots of the original, decompiled or
disassembled listings, or original binaries. The public export refuses such file names, any binary not explicitly
allowed, files over 1 MiB and pasted decompiler output.

**Small diffs.** Every changed line belongs to the change; do not reformat or "improve" unrelated code.

**English.** Code, identifiers, comments, logs and documentation are in English.

## Reporting bugs

Open an issue on GitHub; the template asks for what helps most (edition and platform, the map and what happened, the
run log from `HPEngineData/logs/`, a save near the problem). See [Reporting a bug](../README.md#reporting-a-bug) in
the main README. If you can compare with the original game, say how it behaves there; a difference from the original is
a bug unless it is listed in [Differences](differences.md).
