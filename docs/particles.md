# Particle emitters (AParticleFX)

Emitters (torch fire, spell sparks, smoke, snail trails, broom trails) are `AParticleFX` actors. Their game-tick half only ages the emitter; the particles themselves are born and moved inside the renderer, one emitter at a time, and only for emitters that are actually drawn. That split decides what a recording of the original can and cannot show: an emitter that is off screen does not simulate. The port reproduces the split and the order of random draws, with one deliberate difference: render random numbers come from their own stream.

## Two halves

- **Game tick: `AParticleFX::Tick` prelude** (Engine.dll). Runs before the plain `AActor::Tick` body. It adds the tick delta to the emitter's `Age`, and consumes no random numbers. It counts as active if the emitter's own particle container holds at least one particle, otherwise if `ParticlesPerSec.Base + max(Rand, 0) > 0`. (port: `TickParticleFx` in [`UEWorldFrameProducer.cs`](../HP1.Core/Runtime/VM/UEWorldFrameProducer.cs))
- **Self-destroy by budget (game tick).** When `ParticlesMax > 0` and `ParticlesPerSec.Base > 0`, the emitter destroys itself once `Age` exceeds `ParticlesMax / pps.Base + Lifetime.Base + max(Lifetime.Rand, 0)`. The sum is formed in the x87 register (not rounded to float) and compared with the stored float `Age` (Engine `0x103c3d43..0x103c3d59`). Same file.
- **Render: `AParticleFX::Update(0)`**, called per drawn emitter by `URender::DrawParticleSystem` (Render.dll) via `DrawActorSprite`. It computes `dt = Age - ElapsedTime` (the renderer catches up to the game tick), adds it to `ElapsedTime` unconditionally, then runs `UpdateParticles` first and `EmitParticles` after. (port: `RenderStepFx` / `RenderStepFxBody`, same file)

## Only visible emitters are simulated

Update runs from the draw of the emitter's sprite, in `DrawFrame` order, once per actor per frame; a repeat in the same frame tree has `dt = 0` and returns at once. An emitter culled by occlusion is not updated and keeps its particles frozen. The one exception is an emitter that moved: `AParticleFX::GetRenderBoundingBox` (Engine `0x103c116c`) also calls `Update(0)` from `FDynamicSprite::Setup`, so a moving invisible emitter is still stepped; a stationary invisible one is not.

Consequence for the port: whether an emitter's sprite passes the occlusion test can depend on its particle positions, hence on the render random stream. This is recorded as an open pin for the Lev3_Dungeon emitter PotFire08 (owner TorchFire38), whose particles at the view edge differ between original and port (open, decision of 2026-11-03).

## Update(0) details

- **Plain emitter with `dt <= 0`:** returns without simulating, so particles freeze on paused frames. Only `bShellOnly` forces the simulation quantum to 0.1.
- **First update:** when `ElapsedTime == 0`, `Location` is copied into `LastUpdateLocation` and `LastEmitLocation`, and `Rotation` into `LastUpdateRotation`.
- **Steady-state catch-up clamp:** for `bSteadyState` emitters whose `Location` equals `LastUpdateLocation` and `dt > 1/15`, `dt` is limited by the particle lifetime and by `(target - alive) / (pps.Base + min(pps.Rand, 0))`, floored at 1/15. The division is unguarded in the original (`0x103c4003..0x103c401b`): a zero rate gives infinity or NaN, and the compare replaces anything below or unordered with 1/15.
- **Budget gate:** if `ParticlesMax == 0` or fewer than `ParticlesMax` have been emitted, `bEmit` decides between `EmitParticles` and merely tracking `LastEmitLocation`. Otherwise, with no particle alive, the render-side self-destroy runs and Update returns false.
- **After simulation** the container bounds are rebuilt (UParticleList slot `+0x94`, Engine `0x103c7630`): each particle adds `Location -/+ half`, `half = 0.5 * max(Length, Width)`. These bounds are what `GetRenderBoundingBox` reads for emitter visibility. (port: `UpdateListBounds`)

## Emitter construction

- The C++ constructor sets class defaults: `LOD (+0x300) = 1.0`, `bUpdate (+0x2f8 bit 0) = true` (without it alpha, colour and size ramps never move), and zeroes `Age`, `ElapsedTime` and the container. Nothing in Engine or Render ever writes `LOD` afterwards, as far as the repo's scan of the disassembly shows. (port: `UEObject.LoadDefaults`, `ParticleFxInitExecution`)
- `AParticleFX::InitExecution` forces `bSteadyState = (ParticlesMax == 0 && Distribution != 1)`. If that holds and `bPrime` is set or `Level.TimeSeconds == 0`, it raises `Age` to a full lifetime (`Lifetime.Base + max(Lifetime.Rand, 0)`). The first render Update then back-fills a whole lifetime window of aged particles: the "prime burst" of torches that look already burning when the level opens.
- InitExecution is called from the engine's loaded-level sweep and from `SpawnActor`, not lazily from the first render; otherwise a run without rendering and a run with it would start from different world states. (port: `RunInitExecutionPass` in [`UELevelRuntime.cs`](../HP1.Core/Runtime/World/UELevelRuntime.cs))
- Priorities: `EmitParticles` deals each born particle one of ten draw priorities in turn, from a table in Engine `.rdata` `0x104783e8` (9, 3, 1, 7, 5, 0, 6, 2, 4, 8). The renderer's density fade reads it.
- `EmitParticles` (Engine, `AParticleFX::EmitParticles`): the particles-per-second sample is an unconditional `appFrand`. A range parameter (`GetParams`) consumes a random number only when its `Rand` is non-zero; with `Rand == 0` it returns `Base` and makes no call. Colour parameters use one conditional draw for the whole colour, tested on the whole `FColor` dword including alpha (`0x103c554c`, `0x103c5704`).
- `ParentBlend != 0` samples the parent class defaults as a second parameter source and spends a second `appFrand` even if the blend is outside [0, 1].

## Random numbers

- `appFrand()` is `rand() * 1/32767` (float constant at Core `0x1017ddb0`, bits `0x38000100`), not `/32768`; `rand() = 0x7fff` gives exactly 1.0. `appFrand(min, max)` (Core `0x10126170`) is exactly one `rand()` per call, even when min equals max. (port: `FrandScale`, `NextFrandRangePc24` in [`MsvcRandom.cs`](../HP1.Core/Runtime/Native/MsvcRandom.cs)) See [x87-precision.md](x87-precision.md) for the arithmetic rounding.
- In the original, the renderer pulls from the same CRT `rand()` stream as the game. Particle emission and `FGlobalRandoms::Tick` (256 or 512 `appFrand` per frame, Render.dll `0x10b1d020`) therefore shift gameplay rolls depending on what the camera sees. **Not original:** the port gives rendering its own stream; call counts and formulas in every body are unchanged. (port: `UENativesCore.BindFrameProducer`, [`HP1.Core/Runtime/VM/UENativesCore.cs`](../HP1.Core/Runtime/VM/UENativesCore.cs).)
- The render stream shapes how the frame looks, not world logic, so only the player-visible result has to match, not the bit-exact sequence.

## Stack residue in the first particle (D-73)

The first particle of an `EmitParticles` call reads `ChaosTimer` and `SpinPos` from uninitialised stack slots that the constructor and `EmitParticles` do not write. The values depend on the caller: on the `GetRenderBoundingBox` path the slot holds two floats left by an earlier `URender::BoundVisible` call; on the render path it holds the last value written by an earlier particle emit or by `UParticle::Update` in its `bSystemRelative` branch. The port chains these writes (matched bit-exactly for the first particles of several levels). Driver-side overwrites between calls (the Direct3D layer) are platform code and are not emulated; session start uses zeros while the original holds a loader residue. (port: `EntrySlot`, `_slotKind` in `UEWorldFrameProducer.cs`; record: D-73.)

## Destroyed emitter in a child frame

A frame tree (mirror or portal child frames) can draw the same emitter again after it was destroyed earlier in the same tree. The renderer does not test `bDeleteMe` before the draw (`DrawFrame` `0x10b254f0`, `DrawActorSprite` `0x10b32850`); `AParticleFX::Destroy` frees the particle list (`+0x378 = 0`, Engine `0x103c1cdd`), and Update returns false on a null list before writing anything. The emitter draws nothing. What a mesh or sprite draw would read from a destroyed non-emitter actor is not established: the port stops loudly there.

## The start-up flash of torches (D-54, improvement, Enhanced mode only)

In the original, a particle born inside a long catch-up window (an emitter coming back into view, or the prime burst) is caught up with one `UParticle::Update` step when the emitter has no `Attraction` and no `Elasticity` (the 1/15 substeps exist only with them), and its alpha and colour ramps are evaluated against the old `Age = 0`. Particles seeded over a long window are therefore drawn as newborn, and the torch flashes. **Not original (author decision, improvement):** the port runs the shipped step as is (position, velocity, chaos, size, age) but evaluates only the alpha and colour ramps in 0.06667 s substeps; this touches neither the bounding box, the random stream nor the game. Only in Enhanced mode; `VisualMode=Original` runs the shipped step. Running the whole particle update in substeps was tried and rejected: it shifted the chaos draws and made some emitters self-destruct earlier. (port: `AdvanceNewborn` in `UEWorldFrameProducer.cs`; record D-54.)

## Open

- Not ported (the port stops loudly instead): colour palette ramps (`ColorPalette`) and fractional `ParentBlend` between 0 and 1. The campaign data uses only 0 and 1.
- Shipped draws particle 0 twice per frame (the Render.dll particle walk `FUN_10b15090` has the same First/Next quirk as Engine's `UParticleList` First/Next, `0x103c7330/0x103c7360`; additive, so one spark is brighter); the port draws it once. The update side of the quirk (particle 0 updated twice per substep) is ported. Open, author to decide.
