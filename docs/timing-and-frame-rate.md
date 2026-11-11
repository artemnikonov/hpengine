# Timing and frame rate

The original game has no frame limiter in single player and its scripts behave as designed at about 60 fps (the author's decision for the port; not established from the original). At other rates several things go wrong: scripts that count steps per tick behave differently, the level clock runs ahead of real time above about 200 fps, and fire flames freeze above 128 fps because a particle's age is a float. A slow first second after loading a level also matters, because the game data depends on how much level time has accumulated by the first cutscene steps. OpenHP1 therefore runs game logic at a fixed 60 Hz and draws at the monitor rate by interpolation. Both the fixed rate and the interpolation are deliberate differences, not original behaviour.

## How the original paces itself

- **No limiter.** `HP.exe` MainLoop (`0x10914a60`) computes `dt` from a 32.32 fixed-point clock converted to float and has no clamp of its own. It sleeps only when `GetMaxTickRate` is above 0; in single player `UGameEngine::GetMaxTickRate` (`0x103a06d0`) returns 0, so pace is set only by the display driver's vertical sync. The engine tick draws inside it. (D-31)
- **Level clock clamp.** `ULevel::Tick` clamps the real delta to the range 0.005 to 0.1 seconds (compares at `0x103b7062..0x103b70dc`, constants `0x10477bcc` and `0x104737fc`; less-than or unordered yields 0.005), multiplies by `TimeDilation`, stores the float delta and adds `_ftol(delta * 2^32)` to the 32.32 level clock. `TimeSeconds` is the float conversion of that clock. (port: `AdvanceLevelClock` in [`UELevelRuntime.cs`](../HP1.Core/Runtime/World/UELevelRuntime.cs)) Above 200 ticks per second the lower clamp makes the clock run faster than real time, so the game speeds up.
- **Texture steps follow the wall clock** of the viewport, not level time: see [procedural-textures.md](procedural-textures.md).

## Frame-rate-dependent behaviour

- **Shards hang above 100 Hz.** In `FlipendoVaseBronzeShard`, the `fall` state's Tick enables gravity only for a step larger than 0.1 unreal units per tick. At high rates the step per tick is smaller, so shards hang in the air; cutscene timing drifts the same way. At a fixed 60 Hz these scripts behave as designed (author decision 2026-11-08, D-35).
- **Many scripts count steps per tick.** The code comment of the pacing loop says about 45 places in the bytecode (cameras, aiming, levitation, frogs) count a step per tick and behaved differently at 120 Hz ([`openhp1/OpenHP1Program.cs`](../openhp1/OpenHP1Program.cs)). The count is the author's estimate; the repo does not list the places.
- **Fire and torch flames freeze above 128 Hz.** `AParticleFX::Tick` accumulates `Age` in a float (see [particles.md](particles.md)). The Tut1 torches have `Age` of roughly 199836 seconds. A float near that value cannot represent a step smaller than half its spacing, so an increment `dt < 1/128` is lost and the flame stops advancing. The conclusion from the repo is: above 128 ticks per second the flame freezes ([`openhp1/FramePacing.cs`](../openhp1/FramePacing.cs), [`openhp1/OpenHP1Program.cs`](../openhp1/OpenHP1Program.cs)).
- **Speed-up above 200 fps.** From the 0.005 second lower clamp described above.
- **Sound and music start-up depend on pace** (next section).

## Slow first frames after a load

After `LoadMap` the original's first frames are slow (textures are uploaded on first display, a probe of the original ran at about 18 fps), so by the first cutscene steps the level clock has already accumulated a few tenths of a second. Game data depends on it: in Lev_Tut1b and Lev_Tut3, Harry is placed inside `Trigger16` (`Event=MU1`, `ReTriggerDelay` 0.25) and his first step is about tick 7. `Trigger.Touch` (Trigger.uc:265) rejects the touch if `Level.TimeSeconds` is below 0.25. At 60 to 120 fps only about 0.18 s have accumulated by that tick, and the start music is lost.

- **Port rule (not original code, an emulation of the original's pace by author decision 2026-10-01).** For the first 3 ticks after world bring-up the delta is at least 0.1 (the upper clamp), giving 0.3 s at any frame rate. It is enabled by a host with real frame pacing (`EmulateLoadStutter`, switched on by `SetRealFramePacing`) and armed on level entry and on loading a save; fixed-step headless runs enable it only when emulating the player. (port: `LoadStutterTicks`, `ArmLoadStutter`, `AdvanceLevelClock` in `UELevelRuntime.cs`; `UEGameEngine.SetRealFramePacing` in [`UEGameEngine.cs`](../HP1.Core/Runtime/World/UEGameEngine.cs))
- The windowed game enables it; snapshots do not, to stay deterministic. Music after a load: see [music-and-galaxy.md](music-and-galaxy.md).

## What the port does

- **Logic always at 60 Hz** (`FramePacer`, [`openhp1/FramePacing.cs`](../openhp1/FramePacing.cs); D-35, D-31(b)), with the raw measured delta. A command-line option can override the rate for tests. Input is delivered right before the tick (message pump, then update; the original's loop order is taken from a comment, not re-verified, see [gestures.md](gestures.md)).
- **Drawing at the display rate, capped at 120** (`FramePacer.MaxDrawHz`). Frames between ticks show a linear interpolation of the previous and the current tick: camera and level clock, skeleton and wand poses, movers, and particles (shifted back along their velocity). Sprites, decals, static meshes and vertex meshes run at the logic rate. Display latency is at most one tick. Interpolation is on by default and can be switched off in the ini key `SmoothMotion`. It is presentation only and writes nothing back to Core.
- **Procedural textures and texture animation use a 60 Hz cadence** (`RenderCadence`, [`RenderCadence.cs`](../HP1.Core/Runtime/VM/RenderCadence.cs)): steps are metered by wall clock, 60 per second, with a quarter-period tolerance; a longer lag is not caught up with extra steps.
- **Particle age, emission and texture steps** are as shipped at 60 Hz; the particle simulation itself runs per drawn emitter exactly as the original ([particles.md](particles.md)).
- Public summary: [Differences from the original](differences.md): no speed-up at very high rates and no frozen torches above 128 fps.

## Open

- The author reconfirmed the fixed 60 Hz rate on 2026-11-08 after an attempt at variable-rate logic was removed. The record lists a presentation remainder (sprites, decals and vertex meshes at logic rate; light between ticks taken from the tick) judged by eye, and a live smoothness check is still open (D-05).
- Engine field `UEngine+0x6c` (1/dt) is not written by the port; static and dynamic analysis of the original found no reader, so it is not observable (D-31).
