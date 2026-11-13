# Bugs and quirks of the original game

Faults found in the 2001 game while porting it, each with a documented cause. For each: the symptom, the cause in the
original, and what HPEngine does. "Fixed" always means an author decision recorded as "not original"; where the port keeps
the original behaviour it says so. Game logic, physics and progress are otherwise exact, so most of these are visible only on
modern hardware or in rare spots. The decision numbers (D-xx) are entries of the project's debt register.

## Bertie Bott's beans fly away at a steady frame rate (D-30)

- **Symptom.** Picking up a jelly bean makes it fly millions of units away instead of to Harry, and the pickup sound is lost
  (the sound's position follows the bean).
- **Cause.** `Jellybean.killbean.Tick` (script, `Jellybean.uc` lines 110-111) subtracts the frame delta from
  `fPickupFlyTime` and then moves the bean by `(target - Location) / (fPickupFlyTime / Delta)`. The remainder lags by a
  tick, and on the tick where it crosses zero the divisor is almost 0. At a steady rate where `0.25 / dt` is an integer
  (60, 120, 144 Hz) the float remainder is about 1.1e-8, so the move is enormous. On the project's reference machine the original runs at
  roughly 20 fps with uneven deltas, where the remainder is effectively random and the fault is hidden; a running original with the delta
  forced to exactly 1/60 flies off the same way.
- **Port.** Fixed, not original: `UEPortScriptOverrides` wraps `killbean.Tick` and replaces a divisor of magnitude below 1 by
  1 on the zero-crossing tick, so the last step lands on the target. The first tick (divisor -1, `fPickupFlyTime` still 0
  because `Begin` runs after the first `Tick`) runs the original bytecode, so a bean still bounces away from the target on
  that tick as in the original. Port: `../HP1.Core/Runtime/VM/UEPortScriptOverrides.cs`.

## Frame-rate dependent logic (D-31)

- **Symptom.** On a fast machine the game runs ahead of the clock, and fire stops animating.
- **Cause.** `HP.exe`'s main loop (0x10914a60) has no rate limit in single player: `UGameEngine::GetMaxTickRate`
  (0x103a06d0) returns 0, and the pace is whatever D3D vsync gives. `ULevel::Tick` (0x103b7062) clamps the delta to at least
  0.005 s, so above about 200 ticks per second the game outruns the clock. Particle `Age` is a float: the Tut1 torches have
  `Age` near 199836 s, an increment under 1/128 s is lost to rounding, so above 128 ticks per second the flames freeze.
  By the project's estimate (the places are not listed), about 45 places in the shipped bytecode count a step per tick
  (cameras, aiming, levitation, frogs) and behave differently at other rates.
- **Port.** Kept rate, not behaviour: game logic always ticks at 60 Hz (not original, author decision), and frames between
  ticks are interpolated for display, capped at 120 Hz (`../hpengine/FramePacing.cs`). The 0.005 s clamp itself is ported.

## Mesh detail collapses with distance and low frame rate (D-62)

- **Symptom.** Dumbledore in the final level is drawn with about 65 of his 568 triangles at that distance.
- **Cause.** `URender::DrawLodMesh` (Render.dll 0x10b0ffcd-0x10b1011d) picks a vertex count from the sprite's screen area,
  the mesh's `LODMinVerts` and a LOD level `Owner.LODBias / (GlobalShapeLODAdjust * GlobalShapeLOD * LODStrength)`. In addition
  `URender::PostRender` (0x10b1ef80) rewrites `GlobalShapeLODAdjust` every frame from the frame time against
  `MinDesiredFrameRate` (30 in `HP.ini`): below that rate it grows in 0.1 steps (never below 1.0), so below 30 fps meshes collapse
  further. The user-visible result is a clearly lower-detail model at moderate distances.
- **Port.** The LOD collapse is ported and drawn in `VisualMode=Original` (`UEMeshLod`); `Enhanced`, the default, draws the
  full mesh. The frame-rate adaptation is not ported in either mode: `GlobalShapeLODAdjust` stays 1.0 (improvements, not
  original, 2026-10-15 and 2026-10-30). On the
  UK build the same adaptation happens in `DrawWorld`, and the port keeps 1.0 there too.

## Dark band over glasses and green rectangles on trees (UsePrecache)

- **Symptom.** A dark band across Harry's and Dumbledore's glasses, and solid green or magenta rectangles on tree and bush
  billboards in Tut1 and the Hogwarts exterior, on some machines but not others.
- **Cause.** With `UsePrecache=True` (the shipped `Default.ini` value), `URender::Precache` (`FUN_10b1d900`) uploads every
  texture with the texture's own poly flags (0 for mesh skins, no masked flag from a surface). Palette-8 skins such as
  `HPModels.skdevilharryTex2` and `skdumbledoreTex3` are later reused by cache id for faces drawn masked, so palette index 0
  stays opaque. With `UsePrecache=False` the first upload carries the masked flag (poly flags 4002 in the draw log) and the texel is cut.
  The data scan predicts 59 mesh skins affected; 28 were drawn in short windows, none outside the prediction. The result
  therefore depends on the machine's precache setting.
- **Port.** Fixed, not original (author 2026-11-03): masked textures cut palette colour 0 everywhere, expanded per use.
  Walls and movers are not affected, since precache ORs the surface's masked flag in.

## Fire flashes when an emitter comes back into view (D-54)

- **Symptom.** A torch flashes bright for a moment when the player looks back at it after it was out of view.
- **Cause.** A particle born inside a long catch-up window is advanced by **one** `UParticle::Update` step if the emitter has
  no Attraction and Elasticity (the 0.06667-second substeps exist only with them), and its alpha and
  colour ramps are evaluated against the old `Age = 0`. Particles seeded over a long window are therefore drawn as newborn.
- **Port.** Fixed under `Enhanced` only, not original: the shipped step runs unchanged (position, velocity, chaos, `Age`, size,
  so the bounding box, visibility and the random stream stay exact) and only the alpha and colour ramps are evaluated in
  0.06667-second substeps. Running the whole `Update` in substeps was tried and wrong: it shifted the presentation random
  stream and made a smoke emitter self-destruct about 12 ticks earlier. `Original` runs the shipped step.

## Light flares

- **Behaviour.** Coronas are drawn as depth-less screen tiles. `FUN_10b26f10` (Render.dll, called from the corona block of
  `DrawFrame`, 0x10b26742-0x10b26e0f) traces camera to lamp with trace flags 6 (movers and level) only, so a character
  standing between camera and lamp does not hide the flare: it shines through Harry. Candidate lamps come only from the
  viewer leaf's light list, so flares have a distance cut-off.
- **Fade clock.** The fade reads the wall clock (`appSeconds`), and the table lives in the memory cache, so a cache flush
  (for example on every save) empties it. The port uses the game's own frame clock and keeps the table for the process
  lifetime (not original, author 2026-11-03). The port has the same distance cut-off as the original (6 corona tiles drawn at
  60-150 uu, none from 300 uu on a Tut1 pair); a reported "always visible at any distance" is not reproduced and is open.
- **Port.** Under `Enhanced` the ray also fails on a visible, opaque pawn it crosses, so a flare hides behind characters.
  Pawns that are see-through (Harry under the cloak at Opacity below 1, translucent or modulated ghosts) or not drawn never
  block (author 2026-11-08, 2026-11-09). Decorations still let flares through, as in the original. Port:
  `../HP1.Core/Runtime/Render/UECoronas.cs`.

## Music cut at its end and restarted after Alt-Tab (D-72, D-51)

- **Symptom.** A song that reaches its end loses its last 0.74-0.98 s; after focus loss it restarts from the beginning.
- **Cause.** The feeder thread's short final read sets "stream exhausted" before playback gets there, and the next `Update`
  stops the song (D-72). `SetViewport(NULL)` frees the channel and unregisters the music, and the restart on return starts the
  song at its beginning (D-51). Details in `music-and-galaxy.md`.
- **Port.** The script-visible timing is exact; the buffered tail plays out, and the song resumes at its position (both not
  original).

## Data that contradicts itself or is empty

- **Missing save caption.** `baseConsole.DrawLevelAction` looks up the Localize key `nearly_nick_40` while saving; the key is
  absent from the shipped `.int`, so the original draws the Localize placeholder. The port reproduces this (not changed).
- **Empty recording.** `HPSounds.critters_sfx.critters.pig_squeal1` has no audio data; `FSoundData::GetPeriod` returns 0.0
  for it instead of failing, and the port does the same. The only mp2 with `iEndPosn` 0, `HPSounds.menu_sfx.s_menu_enter`,
  has `bIsMP2 = 0`, so it never reaches the stream path with a zero length (open: the zero-length branch is refused loudly).
- **A trigger outside the world.** `Trigger20` in `Lev_Tut1` sits at (3763, -4099, 439), in no BSP leaf (`Zone=LevelInfo0`,
  `iLeaf=-1`); the original draws nothing from a camera there, and neither does the port.
- **Overlapping exports.** The Russian `HPSounds.u` has script exports with serial offsets one byte apart (16617..16620) and
  the US sizes: leftovers of the localisation. The game never loads them (see `editions.md`).
- **Old package versions.** Shipped `Ambient.uax` and `Critters.uax` are package version 69, which carries no `+0x4c` byte
  counter; `USound::Serialize` computes it with the mp2 decoder at load time.
- **Cheat/debug code.** The shipped key-string cheat matcher in `baseHarry.KeyDownEvent` and the developer console are removed
  in the port (not original, D-77).
