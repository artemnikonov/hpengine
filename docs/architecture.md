# HPEngine architecture

A map of the code for a developer who wants to find their way around, fix something or port a missing native function.
Paths are relative to the repository root. Where a directory is **private** (not in the public repository, see
[What is not in this repository](building.md#what-is-not-in-this-repository)) it is marked so: you will see it named in
code comments, but you cannot run it without the original game's binaries and the project's validation data.

## 1. What the program is

HPEngine runs the 2001 PC game *Harry Potter and the Philosopher's Stone* (KnowWonder, Unreal Engine 1, US build 1.53)
on new code. It consists of two halves with a strict boundary:

- **Core** (`HP1.Core`, a .NET library) is the game. It loads the original packages (`.u` script packages, `.unr`
  maps) from the player's install, executes the original UnrealScript **bytecode** on its own virtual machine, and
  carries C# ports of the engine's **native functions** (the C++ code of `Core.dll`, `Engine.dll`, `Render.dll`,
  `Galaxy.dll`, ...). Game state, physics, animation and poses, timing, saves, sound channels visible to scripts, and
  the decision of what is visible in a frame all live here.
- **The frontend** (`hpengine/`, the `HPEngine` executable) is the device layer. It opens the window (SDL3 + SDL_GPU on
  Metal, Vulkan or D3D12), draws the frame Core produced, mixes and plays sound, and passes keyboard, mouse and gamepad
  input to Core.

The boundary rule: **the frontend never writes Core state.** It receives finished frame data from Core and only
executes it. It sets no properties, owns no caches Core reads, and calls no Core code with side effects except the
input entry points and the tick. Rendering takes random numbers from its own stream and never draws from the game's
random stream (`UENativesCore` keeps two `MsvcRandom` streams for exactly this reason).

Core does not reference the frontend. The scripts are not decompiled or rewritten: the bytecode is parsed by a patched
build of UELib (`HP1.Core/ThirdParty/Eliot.UELib.dll`, see `HP1.Core/ThirdParty/README.md`) and run as shipped. A
handful of script functions are replaced by port code by explicit decision; they are listed in one place,
`HP1.Core/Runtime/VM/UEPortScriptOverrides.cs`, and each is marked "not original".

## 2. Module map

### Core (`HP1.Core/Runtime/`)

| directory | owns |
|---|---|
| `HP1.Core/Runtime/VM/` | the UnrealScript interpreter, execution frames, native table, the Core frame producer and frame contract, procedural textures and lightmaps |
| `HP1.Core/Runtime/ObjectModel/` | script objects, property storage, class registry, object lifecycle |
| `HP1.Core/Runtime/World/` | the level runtime: actors, tick, physics and collision, animation, saves, travel, config, input, audio channels, install lookup, boot |
| `HP1.Core/Runtime/Render/` | render-side decisions that affect what is visible: occlusion, sprite sort, coronas, actor lighting, mesh LOD |
| `HP1.Core/Runtime/Native/` | bit-exact helpers for the original's x87 floating point and MSVC runtime |
| `HP1.Core/Runtime/Diagnostics/` | traces and logs (read-only: they never feed back into the simulation) |

**VM.** `UEInterpreter.cs` is the bytecode interpreter: token handlers checked against the `exec*` bodies of the
original `Core.dll`, function calls, states and latent functions, the native dispatch (a native function with an index
is looked up by index, otherwise by name; an unknown native fails loudly through `OnUnknownNative`). `UEFrame.cs` is the
execution frame (the counterpart of the original `FFrame`). `UENativesCore.cs` holds the native-table machinery
(`Pair`, `GetByIndex`, `GetByName`) and the pure native groups: bool/int/byte/float/string/vector/rotator operators,
math, Canvas drawing natives, `Sleep` and other state natives. `UEValue.cs` and `UEValueTypes.cs` are the script value
types (`UEVector`, `UERotator`, ...). `UELocalization.cs` reads `.int` localisation.
`UEInterpreter` is a partial class: `UEInterpreter.cs` keeps the fields, the constructor and the call machinery
(`Invoke`, `Run`, `EvalNext`); `.TokenHandlers` holds the token handler table, `.Eval` the evaluation helpers
(arguments, l-values, switch, conversions), `.States` states and labels, `.Objects` texture proxies, class defaults
and `NewObject`, `.Iterators` the native `ForEach` iterators.

**Frame contract.** `WorldFrame.cs` defines what Core hands the frontend: `WorldFrame` (per tick), `WorldStatic` (per
level), and the snapshot structs (`ActorSnapshot`, `CameraSnapshot`, `AudioCommand`, `HudCommand`, ...).
`UEWorldFrameProducer.cs` builds a `WorldFrame`; it ports `UGameEngine::Draw` (the view actor's `PreRender`/`PostRender`,
particle updates, the HUD Canvas, the Galaxy audio update). It is a partial class: `UEWorldFrameProducer.cs` keeps the
fields, audio and the `Build`/`BuildFrame` entry points; `.Views` the 3D frame stages (view projection, render boxes,
`DirectRenderStep`, `OccludeWorld`), `.Particles` and `.ParticleStep` the particle emitters, `.Canvas` the HUD and
console canvas, `.Text` fonts and text drawing. `WorldContract.cs` is the field manifest of the contract:
for every field, who produces it and whether it is gameplay or presentation, checked by reflection.
Despite the topic, these three files live in `VM/`, not in `Render/`.

**Object model.** `UEObject.cs` is a script object: class, properties (`UEProps.cs`, slots of the class layout),
state. `UEClassRegistry.cs` loads packages and resolves classes. `UELifecycle.cs` covers creation and destruction.
`UEPropNameComparer.cs` gives the case-insensitive name comparison of UE1 `FName`.

**World.** `UELevelRuntime.cs` is the one owner of the actor and world lifecycle: actor list, spawn order, level clock,
the per-actor tick (events, state code, timers, life span, physics), spawn and destroy, owner and base, zones. It is a
partial class split over `UELevelRuntime.*.cs` by subject (`.Animation`, `.SaveGame`, `.ReachNatives`, `.Jump`,
`.LoadMapScreens`, ...). `UELevelRuntime.cs` itself keeps every field, the actor list, the engine frame and the level
clock; the tick pass is in `.Tick`, spawn/destroy/owner/base/zones in `.ActorLifecycle`, physics in `.Physics`
(dispatch and the other modes), `.PhysWalking` and `.PhysFalling`, moving actors and encroachment in `.MoveActor`, line
checks in `.LineChecks`, the collision hash in `.CollisionHash`, level loading in `.LevelLoad`, auxiliary animation
channels in `.AnimChannels`. Next to it:

| file | owns |
|---|---|
| `HP1.Core/Runtime/World/UEGameEngine.cs` | the boot sequence (`UEGameEngine.Boot`) and the tick entry point used by every host |
| `HP1.Core/Runtime/World/UEGameEngineNatives.cs` | the engine's native table (`UEGameEngineNatives`, derives from `UENativesCore`) and the world tick loop `TickWorld` |
| `HP1.Core/Runtime/World/UEBspCollision.cs` | the map's BSP model (`HeadlessBspModel`, partial): fields and construction; the reader is in `.Loading`, the BSP queries (line and point checks, `FindRegion`) in `.Traces` and `.BoxSweep`, `BrushCoords` in `.BrushCoords`, the actor collision primitives in `.ActorPrimitives`, value types in `.Types` |
| `HP1.Core/Runtime/World/UEPawnMovement.cs` | latent movement (`MoveTo`, `TurnTo`, ...) and walking physics formulas |
| `HP1.Core/Runtime/World/UEGalaxyAudio.cs` | the 16-channel sound table and music logic of the original Galaxy audio subsystem |
| `HP1.Core/Runtime/World/UEInput.cs` | `UInput`: key events, bindings, exec functions behind buttons |
| `HP1.Core/Runtime/World/UEConfig.cs` | the live `.ini` configuration state |
| `HP1.Core/Runtime/World/UEInstallPath.cs` | case-insensitive file lookup in the install (Linux file systems) |
| `HP1.Core/Runtime/World/UEEdition.cs` | which release of the game the install holds (SHA-1 of its files) |

**Render-side Core.** `HP1.Core/Runtime/Render/` holds the parts of the original renderer whose results the player
sees as world behaviour: `UEOcclusion.cs` (which actors, sprites and child frames such as mirrors, sky and warp
portals enter a frame), `UESpanBuffer.cs`, `UEBspRaster.cs`, `UESpriteSort.cs` (draw order), `UECoronas.cs`,
`UEActorLighting.cs` and `UEActorLights.cs`, `UEMeshLod.cs`, `MeshTriangleOrder.cs`. `ShippedLayout.cs` reproduces the
original object memory layout (used by the VM judge, section 4).

**Exactness helpers.** `HP1.Core/Runtime/Native/` reproduces the original's arithmetic where C# would differ:
`X87Ext.cs` (an x87 register with a 64-bit mantissa and the precision-control rounding), `UEX87Transcendental.cs`
(`fsin`, `fpatan`, `acos` as the CPU and MSVC 6.0 runtime compute them), `MsvcRandom.cs` (the MSVC `rand()` stream
behind `appRand`/`appFrand`), `MsvcQsort.cs` (MSVC `qsort`, whose order of equal elements is visible). See
[x87 precision](x87-precision.md).

### Frontend (`hpengine/`)

| path | owns |
|---|---|
| `hpengine/HPEngineProgram.cs` | `Program.Main`: command line, boot, the window loop, snapshot and benchmark modes |
| `hpengine/FramePacing.cs` | `FramePacer` (logic at 60 Hz, drawing at display rate) and `Interp` (interpolation between two ticks) |
| `hpengine/CoreThread.cs` | the Core tick on its own thread, calls back to the window thread marshalled (`--single-thread` keeps the tick on the window thread) |
| `hpengine/SdlRenderer.cs` | `Renderer`: GPU device, pipelines, passes (partial class continued in `SdlHudPass.cs`, `SdlProcTex.cs`, `SdlSelfTest.cs`) |
| `hpengine/SdlWorldMesh.cs`, `hpengine/SdlActors.cs`, `hpengine/SdlMovers.cs`, `hpengine/SdlSky.cs`, `hpengine/SdlFog.cs` | geometry built from the Core frame: BSP and lightmap atlas, actor meshes, movers, sky, fog |
| `hpengine/Sprites/` | sprites, particles, decals, coronas as quads |
| `hpengine/Hud/` | the 2D layer (HUD, menus, Canvas output) |
| `hpengine/Audio/` | `AudioSystem`, `AudioMixer`, the Galaxy mixing kernel, zone reverb, mp2 music stream |
| `hpengine/Input/` | `InputStage` (SDL events in OS order), `CoreInputSink` (hands them to Core), gamepad, key map |
| `hpengine/SdlViewport.cs` | `SdlViewportDevice`: resolutions and window modes Core asks about |
| `hpengine/DrawLog.cs` | the port side of the draw-call log (`--drawlog`, section 4) |
| `hpengine/packaging/` | `InstallRoot.cs` (finding the game folder), `ModOverlay.cs` (mods linked over a working copy of the install), `FatalDialog.cs` (when a start-up failure is shown in a dialog), `publish.sh`, `make_launcher.sh`, `make_app.sh` |
| `hpengine/shaders/`, `hpengine/Hud/shaders/` | HLSL sources and the committed compiled blobs (SPIR-V, DXIL, MSL) |

Each subdirectory of `hpengine/` has its own `README.md` with details; `hpengine/README.md` covers shaders and the GPU
self-test.

### Launchers (`launcher/`)

`launcher/Launcher.swift` is the macOS app (adds the game, starts it, updates itself; `launcher/update_sign.swift`
signs updates). `launcher/Desktop/` is the Windows/Linux Launcher (C#, Avalonia; the same steps, no self-update: it
opens the release page). `launcher/HP1Setup/` is the C# installer used on all platforms: it identifies the edition by SHA-1
(`launcher/us153.sha1`, `launcher/uk11.sha1`, `launcher/ru_ea.sha1`), unpacks installers (`InstallShield.cs`; Inno Setup
through the bundled innoextract from `launcher/third_party.sh`) and copies the game data into `game/`.

### Checks and tools (mostly private)

| path | what | public |
|---|---|---|
| `hpengine/tests/` | one project per subject; `hpengine/tests/run_all.sh` runs them all | only the projects that need no game files |
| `runtime_poc/` | the headless harness (`HP1.Headless.Smoke.csproj`): boots maps without a window, writes deterministic journals, feeds judges; `runtime_poc/targeted.sh`, `runtime_poc/pins.sh` | no |
| `audit/` | `audit/merge_check.sh` (every judge), `audit/stage_check.sh`, `audit/accept.sh`, `*_redtest.sh` mutation scripts, captures of the original | no |
| `tools/` | judges and probes: `tools/swarc_oracle/`, `tools/origprobe/`, `tools/render_ledger.py`, `tools/source_index.py`, `tools/publish_public.sh` | no |
| `se_harness/bsp_diff/` | unicorn runs of original DLL bodies (`*_oracle.py`) | no |

## 3. The life of one frame

### Boot

`Program.Main` (`hpengine/HPEngineProgram.cs`) finds the game folder (`InstallRoot`: `--root`, else the executable's
folder, else its parent, else its `game/` subfolder), applies enabled mods (`ModOverlay`), then calls `UEGameEngine.Boot(root, map, ...)`.
`Boot` is the single boot sequence; the headless harness runs the same one and only stops it at declared points
(`UEGameEngine.BootStops`):

1. class registry and interpreter (`UEClassRegistry`, `UEInterpreter`), `UELevelRuntime.AppInit` (ini files);
2. the script packages (`UEGameEngine.CorePackages`: `Core`, `Engine`, `HPBase`, `HarryPotter`, ...);
3. the native table (`UEGameEngineNatives`, registering every native group);
4. the map's BSP model and reach specs (`HeadlessBspModel.Load`), the placed-actor graph (`UEActorGraphLoader`);
5. level clock, mover brushes, collision hash;
6. the resident `Entry` level (`EnsureEntryWorld`), then `BootstrapPlayWorld`: actor `PreBeginPlay`, `BeginPlay`,
   `PostBeginPlay`, `SetInitialState` in the original order.

The returned `UEGameEngine` exposes `Tick(count, dt)`, `Runtime` (the `UELevelRuntime`) and `Interp`.

### Tick

The window loop in `hpengine/HPEngineProgram.cs` runs logic at a fixed 60 Hz (`FramePacer.LogicDue`) and draws at the
display rate. The step runs on the Core thread (`hpengine/CoreThread.cs`) while the window thread draws from the
previous tick's frame; with `--single-thread` it runs on the window thread. One logic step:

1. **Input.** SDL events were queued by `InputStage.Feed` in OS order; `InputStage.RunFrame` hands them to
   `CoreInputSink`, which calls Core's `UEInput` (console key events first, then bindings) just before the tick, as the
   original's message pump did.
2. **World tick.** `UEGameEngine.Tick` calls `UEGameEngineNatives.TickWorld`: advance the level clock
   (`AdvanceLevelClock`, with the original's delta clamp), tick every actor (`UELevelRuntime.TickActorsPass`: events,
   state code, latent functions, timers, physics), tick the `Entry` level, then handle a pending level change or save
   load (`TryTakeLevelTravel`, `LoadMap` fade, `Travel`, `LoadSavedWorldAndBootstrap`).
3. **Engine frame.** `UELevelRuntime.RunEngineFrame` calls `UEWorldFrameProducer.Build`. This is the port of
   `UGameEngine::Draw` and it runs script code: the view actor's `PreRender` and `PostRender` (the HUD draws through
   Canvas natives into `HudCommand`s), particle updates for emitters that are actually drawn (decided by
   `UEOcclusion`), the Galaxy audio update (`UEGalaxyAudio`). The result is published as
   `UELevelRuntime.LatestFrame`; the level geometry it belongs to is `LatestFrameWorld` (a `WorldStatic`). A tick that
   changed level publishes the loading screen instead (`HoldLoadMapScreen`).

Because `Build` runs script and consumes the game's random stream, it is part of the simulation, not of drawing: it
runs once per tick whether or not the frontend draws.

### The Core-to-frontend frame contract

The frontend reads only these:

| what | type | content |
|---|---|---|
| per level | `WorldStatic` | BSP geometry, surfaces, zones, lightmap and fog data, built once when the map loads |
| per tick | `WorldFrame` | `Actors` (`ActorSnapshot`: transform, mesh, pose, style, lighting), `Camera`, `Views` (child frames: sky, mirrors, warp), `SpriteActors` and `DrawOrder`, `MeshPoses`, `Particles`, `Decals`, `Coronas`, `Hud`, `Audio`, `AudioChannels`, `Music`, `Reverb`, `ViewFlash`, `DrawWorld`, `Paused` |

`WorldFrame` objects are immutable to the frontend. The actor list storage is reused by the producer after three
frames, so a frontend that keeps data longer copies the structs. `WorldContract.cs` lists every field with its
producer and consumer.

### Drawing

After the tick the loop calls `Interp.Frame(previous, current, alpha, dt)` to interpolate between the last two frames
when the display runs faster than 60 Hz (an improvement over the original, see
[Differences](differences.md)), then `FrameDraws` turns the frame into GPU batches: `WorldMesh` and `LightmapAtlas` for
BSP, `ActorBuilder` for meshes in Core's `DrawOrder`, `MoverScene`, `Sky`, sprite builders, `HudBatcher`.
`Renderer.RenderToWindow` submits them. Nothing in this path decides visibility, order or pose: those come from Core.

### Audio

Script calls (`PlaySound`, `StopSound`, `ModifySound`, music changes) execute in Core at call time, in
`UEGalaxyAudio`, which owns the 16-channel table, priorities and `bSongFinished`. The frame carries the resulting
channel snapshot (`WorldFrame.AudioChannels`) and music state. The frontend's `GameAudio` (`hpengine/SdlAudio.cs`) and
`AudioSystem` (`hpengine/Audio/`) only resample, mix with the Galaxy gain chain (`GalaxyMixGain`, `GalaxyMixKernel`),
apply zone reverb and feed the SDL audio device. See [Music and Galaxy audio](music-and-galaxy.md).

## 4. Porting a native function

### Where natives are registered

A native is a C# delegate `UENativeHandler(UEInterpreter interp, UEFrame frame, object[] args)` registered with
`Pair(index, name, handler)`:

- `index` is the native index the bytecode uses (e.g. `Pair(129, "Not_PreBool", ...)`); `0` registers by name only,
  for native functions that have no index in the script package (e.g. `Pair(0, "DynamicLoadObject", ...)`).
- `HP1.Core/Runtime/VM/UENativesCore.cs` defines `Pair` and the pure groups (`RegisterBoolOps`, `RegisterIntOps`,
  `RegisterFloatOps`, `RegisterMathOps`, `RegisterVectorOps`, Canvas natives, ...).
- `HP1.Core/Runtime/World/UEGameEngineNatives.cs` calls those groups and registers the world natives (`PlayAnim`,
  `Trace`, `Spawn`, `MoveTo`, `PlaySound`, `ConsoleCommand`, ...). Most world natives are thin argument parsers that
  call a body on `UELevelRuntime` (e.g. `Pair(514, "LineOfSightTo", ...)` calls `Runtime.LineOfSightToNative`, whose
  body is in `UELevelRuntime.AINatives.cs`).

The interpreter resolves a native by index first, then by name; a native that is not registered throws
(`UENativesCore.OnUnknownNative`, a `NotSupportedException` naming it) instead of returning a default. When you add a native, add it to the group whose subject it belongs to,
put the body next to the state it touches (world state belongs in `UELevelRuntime`), and keep script-visible side
effects in the original order (property writes, events, random-number draws).

### How a body cites its source

Bodies are ported from the original binaries of US 1.53, never from SurrealEngine, the Unreal Tournament source or
other reimplementations. Every non-trivial body names what it ports, in one of these forms:

- `Core.dll 0x1013d020` or `FUN_10388530`: the virtual address of the original function or instruction;
- `Engine.dll.c:130427`: a line in the project's (private) decompile of that DLL;
- the original function name, e.g. `APawn::LineOfSightTo`, `UGameEngine::Draw`.

See the header of `HP1.Core/Runtime/World/UELevelRuntime.AINatives.cs` for a typical citation block, or the x87
comments in `UENativesCore.VRandBody`. A claim that the original does *not* do something is taken from the disassembly,
not the decompile, because the decompiler loses calls and arguments.

### How a body is checked against the original

The checks below live in private directories (they need the original DLLs or captures of the running game). They are
listed so the comments that mention them make sense.

| judge | what it compares | where |
|---|---|---|
| unicorn real-body runs | the original x86 function executed in the unicorn emulator on the same inputs as the port | `se_harness/bsp_diff/*_oracle.py` (e.g. `engine_dll_oracle.py`, `crt_rand_oracle.py`), `runtime_poc/HeadlessNativeBatch.cs` for the port side |
| SwARc lifted DLLs | original `Core.dll` / `Engine.dll` / `Galaxy.dll` recompiled to C++ by SwARc (exact integer and 80-bit x87 model), run after their own `DllMain`, compared with the port on campaign corpora | `tools/swarc_oracle/` (`vm_judge.py`, `engine_judge.py`, `galaxy_judge.py`; `tools/swarc_oracle/README.md`) |
| draw log | per-frame draw calls of the running original (a DLL injected into `HP.exe` under CrossOver) against the port's `--drawlog` with the same camera, across the campaign | `tools/origprobe/hp1drawlog.c`, `tools/origprobe/drawlog_compare.py`, `tools/origprobe/drawlog_gate.sh`, `hpengine/DrawLog.cs` |
| live journals | state of the running original (actors, fog, lightmaps, particles) against the port | `tools/origprobe/` (`compare_*.py`, `datadump_*`) |
| self-contained tests | expectations computed independently from the disassembly, no game files | `hpengine/tests/CoreNativesTests/` and the other public test projects |

`audit/merge_check.sh` runs every judge; each step is classed `orig` (a judge against the original: the proof),
`self` (the port against its own pinned output: catches change, not correctness) or `arch` (an architecture lock).

## 5. Glossary

Terms you will meet in comments and commit messages. Meanings are taken from the repository; where one is uncertain it
says so.

| term | meaning |
|---|---|
| shipped | the original game as released (US 1.53 binaries, scripts, maps) |
| Core / frontend | `HP1.Core` (the game) / `hpengine` (window, GPU, sound device, input) |
| headless, harness | the windowless runner in `runtime_poc/` (`HP1.Headless.Smoke`), used for journals and judges |
| judge (older: oracle) | a check whose expectation comes from the original: a real DLL body under unicorn, a SwARc-lifted DLL, a capture of the live original, or an independent calculation from the disassembly |
| drawlog | text log of every draw call of one frame, from the live original or from the port; compared line by line |
| pins | SHA-256 of deterministic harness journals, stored in `audit/merge_check.sh` and checked by `runtime_poc/pins.sh`; they catch unintended change, they do not prove correctness |
| red mutation, redtest | a deliberate one-line error that a check must catch (turn red); `*_redtest.sh` scripts apply them temporarily. A check no mutation can turn red is considered blind |
| REFUSED / NOVERDICT, exit code 3 | the compared branch did not execute or an input is missing: no verdict, neither pass nor fail |
| tier 1 / 2 / 3, gate | `runtime_poc/targeted.sh` per change / `audit/merge_check.sh` before a merge / `audit/stage_check.sh` mutation sample; "gate" usually means the tier-2 run |
| acceptance | `audit/accept.sh`: tier 2 and 3, a recorded clean code review and the author's playthrough |
| render influence ledger | `tools/render_ledger.py`: every memory write of the original `Render.dll` classified by owner; rows of class `GAME` are render code that changes game state, so the port must reproduce them exactly |
| PORTED / ACTIVATED / PROVEN / CLOSED | status of a system: body written from the source / executed through the real route in a test / matched a judge / proven, dependencies closed and confirmed by a playthrough |
| D-nn | an entry in the project's private debt and decision register; quoted so code, issues and notes refer to the same item |
| not original | a deliberate, author-approved difference from the original (an improvement or a script override); listed in [Differences](differences.md) |
| game stream / render stream | the two `MsvcRandom` instances: game code (including UnrealScript HUD code) draws from the first, port-only render code from the second |
| PC24 / PC64, `FastMathWindow` | x87 precision control; the original renders with 24-bit precision and runs game logic with 64-bit; `UEWorldFrameProducer.FastMathWindow` marks the render window |
| Galaxy | the original's audio library (`Galaxy.dll`), ported in `UEGalaxyAudio` |
| Entry | the small resident level the original loads before the first map; its actors tick alongside the game level |
| `FUN_<address>` | an unnamed original function, by virtual address |
| `*.dll.c:<line>` | a line in the private decompile of that DLL |
| KnowWonder addition | engine code or data the HP1 developer added to UE1 (not in other UE1 games) |
| SurrealEngine, SE | an open UE1 reimplementation (for Unreal Tournament). Not a source; comments name it only where the port disagrees with it |
| Unity, `UENatives`, `HeadlessNatives` | history: until 2026-10-06 the frontend was a Unity project. Comments that mention Unity describe that earlier layout; today the native table is `UEGameEngineNatives` and the harness subclass in `runtime_poc/HeadlessSmoke.cs` |

## 6. Known gaps in this document

- Comments in `HP1.Core/Runtime/World/UEGameEngineNatives.cs` still mention the Unity-era layout in places. The code,
  not those comments, is current.
- The exact set of private judges changes between releases; `audit/merge_check.sh` is the list that counts.
