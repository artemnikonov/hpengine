# Contributing to HPEngine

How to build and run the port, which checks to run for a change, and the rules a pull request is reviewed against.
Read [Architecture](architecture.md) first for the map of the code.

## Build and run

Requirements: the .NET SDK 9 or newer (projects target `net9.0`). SDL3 comes from the `ppy.SDL3-CS` NuGet package;
the compiled shader blobs are committed, so no shader compiler is needed unless you edit a shader.

    dotnet build hpengine/HPEngine.csproj -c Release
    dotnet run --project hpengine/HPEngine.csproj -c Release -- --root <game folder>

The game folder is the one that contains `System/` and `Maps/` (see the main README for installing the game). Without
`--root` the engine looks in its own folder, then in the parent folder, then in a `game/` subfolder
(`hpengine/packaging/InstallRoot.cs`).
Useful options (all parsed in `hpengine/HPEngineProgram.cs`):

| option | effect |
|---|---|
| `--map Lev_Tut1` | start on a map instead of the shipped start map |
| `--shot out.png` | no window: boot, tick, render one frame to a PNG with timings |
| `--perf-title` | frame rate and Core / render times in the window title |
| `--debug-cam` | `F` toggles a free camera (the game gets no input meanwhile) |
| `--selftest-gpu out.png` | GPU self-test on synthetic scenes, no game files needed (see `hpengine/README.md`) |

Logs of every run go to `<game folder>/HPEngineData/logs/`.

Release packages (self-contained; `win-x64`, `linux-x64`, `osx-arm64`, `osx-x64`; all can be built on one machine):

    sh hpengine/packaging/publish.sh                     # all of them
    sh hpengine/packaging/publish.sh --out DIR osx-arm64 # one, into DIR
    sh hpengine/packaging/make_launcher.sh               # release archives: win/linux = Launcher + HPEngine/ (engine, HP1Setup); macOS Launcher.app (needs macOS)

`publish.sh` fails if a package would contain a file of the original game. The macOS Launcher is
`launcher/Launcher.swift`, the Windows/Linux Launcher is the C# project `launcher/Desktop/HPEngineLauncher.csproj`; the
installer is the C# project `launcher/HP1Setup/HP1Setup.csproj`.

Shaders: after editing `hpengine/shaders/classic.hlsl` or `hpengine/Hud/shaders/hud.hlsl`, rebuild the blobs with
`sh hpengine/shaders/build.sh` (or `sh hpengine/Hud/shaders/build.sh`) and check them with `--check`. The DXC and
SPIRV-Cross versions that reproduce the committed blobs byte for byte are listed in `hpengine/README.md`.

## Checks

### What you can run from the public repository

| check | needs game files | what it covers |
|---|---|---|
| `dotnet build hpengine/HPEngine.csproj -c Release` | no | everything compiles (Core, frontend) |
| `sh hpengine/tests/run_all.sh` | no | every test project present in `hpengine/tests/` (in the public repository: the ones that need no game files, e.g. `CoreNativesTests`, `HudTests`, `PacingTests`, `SpritesTests`); exit 0 when all pass |
| `--selftest-gpu out.png` (with `SDL_VIDEO_DRIVER=offscreen` on a headless machine) | no | the main shader, pipelines and blending against values computed from the formulas; needs a GPU or lavapipe; exit 0 pass, 1 fail, 2 no GPU, 3 refused |
| `sh hpengine/shaders/build.sh --check` | no | committed blobs match the HLSL source (needs DXC and SPIRV-Cross) |
| `sh launcher/test_update.sh` | no | the macOS Launcher's update verification and command-line checks (macOS only; exit 3 elsewhere) |
| `sh launcher/test_desktop.sh` | no | the Windows/Linux Launcher's logic and a headless window (no window opens) |
| playing the affected map with `--root` | yes | what the change does in the game |

The CI workflow (`public/workflows/build.yml`, exported as `.github/workflows/build.yml`) runs the build, `run_all.sh`
and the package build on every push and pull request. A typical change to Core or the frontend should pass the first
two rows locally, plus a play of the place the change affects.

Some test projects use exit code 3 (`REFUSED`, `NOVERDICT`) when the branch they compare did not execute. That is not
a pass: find out why the branch did not run.

### What the maintainer runs (private)

These need the original game at `HARRY_POTTER_US_/` in the repository root, the original DLLs, their decompile and
disassembly, or captures of the running original, so they are not in the public repository. They are listed so code
comments and review remarks that name them make sense.

| tier | command | time | what |
|---|---|---|---|
| per change | `runtime_poc/targeted.sh <changed files>` | 5-10 min | build, harness self-test, `runtime_poc/pins.sh`, and the judges of the systems the changed files touch (chosen by `runtime_poc/impact_map.py`) |
| after a Core edit | `runtime_poc/pins.sh` | minutes | five deterministic harness journals against their pinned SHA-256 |
| before a merge | `audit/merge_check.sh` | about 1 h | every judge against the original; each step ends `MATCH`, `FAIL` or `REFUSED` |
| mutation sample | `audit/stage_check.sh`, `audit/completeness_redtest.sh <id>`, `*_redtest.sh` | varies | checks still catch the errors they were written for |
| stage acceptance | `audit/accept.sh` | hours | tiers 2 and 3 plus a recorded code review and the author's playthrough |
| launcher installer | `launcher/test_setup.sh` | minutes | HP1Setup against private copies of several game editions |
| test suites needing the game | `hpengine/tests/BoundaryTests/`, `MeshLodTests`, `Mode3Tests`, `AudioTests`, `InputTests`, `PackagingTests`, `SettingsTests` | minutes | frontend behaviour on real maps |

If your change touches something only these can judge (a native body, physics, what is drawn), say so in the pull
request; the maintainer runs the matching judge.

## Rules for a pull request

**Exactness.** Everything the player sees as world behaviour must match the original US 1.53: game logic, physics,
collision, movement, animation and poses, timing, sound, saves, and what is drawn (which surfaces and actors, with
which blending, colour and placement). What the player cannot see (draw scheduling, caches, which emitter is processed
first) may be simpler.

**Improvements are marked.** A deliberate difference from the original is allowed only when it is visibly better for
the player and the maintainer agrees. It is marked "not original" in the code and listed in
[Differences from the original](differences.md). Logic, physics and progress are never "improved". Script functions
replaced by port code go in `HP1.Core/Runtime/VM/UEPortScriptOverrides.cs` only.

**One owner.** Game state, visibility, poses, the frame tree, coronas and script-visible sound channels belong to Core.
The frontend draws, mixes and passes input; it never writes Core state (no property writes, no Core caches, no calls
with side effects), and render code never draws from the game's random stream. A frontend change that needs a new value
gets it from a new field of the Core frame (`WorldFrame`, listed in `WorldContract.cs`), not by reaching into Core.

**Sources.** Port from the original binaries: the disassembly of the shipped DLLs, with a decompiler as a reading aid,
then the UnrealScript bytecode and the shipped data. SurrealEngine, the Unreal Tournament source and other
reimplementations are not sources. Cite what you ported (address, function name; see
[Architecture](architecture.md#how-a-body-cites-its-source)). A claim that the original does *not* do something needs
the disassembly, because decompilers drop calls and arguments. No numbers chosen by eye, no multipliers without a
source, no special cases by asset or class name. Name lookups (animations, packages) are exact and case-insensitive,
like UE1 `FName`.

**Unknown is said out loud.** If the correct behaviour cannot be established from the original, do not guess a
plausible default: describe what is unknown in the pull request.

**A new check must be able to fail.** Show that it turns red when you introduce the error it is meant to catch, and
make it report (exit 3) instead of passing when the compared code path did not execute.

**Nothing of the game in the repository.** No game files, extracted data, screenshots of the original, decompiled or
disassembled listings, or original binaries. `tools/publish_public.sh` (private) refuses an export on such file names,
on any binary not explicitly allowed, on files over 1 MiB and on pasted decompiler output; please do not get close.

**Language.** Code, identifiers, comments, logs and documentation are in English.

## Reporting bugs

Open an issue on GitHub; the template asks for what helps most (edition and platform, the map and what happened, the
run log from `HPEngineData/logs/`, a save near the problem). See
[Reporting a bug](../README.md#reporting-a-bug) in the main README. If you can compare with the original game, say how
it behaves there; a difference from the original is a bug unless it is listed in [Differences](differences.md).
