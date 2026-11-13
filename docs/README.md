# How the original works: reverse-engineering notes

What HPEngine learned about **Harry Potter and the Philosopher's Stone** for PC (KnowWonder, 2001, Unreal Engine 1,
US 1.53) while porting it. The notes describe the original engine and game, not the port's design.

- **Addresses** are virtual addresses in the shipped US 1.53 binaries (`Engine.dll`, `Render.dll`, `Fire.dll`,
  `Galaxy.dll`, `Core.dll`, `HP.exe`), as cited in the port's code comments. Unnamed functions are written as
  `FUN_<address>`.
- **Sources.** Each claim names the original function and the port file that carries it (`../HP1.Core/...`,
  `../hpengine/...`). Natives were checked against the original's machine code run in an emulator, frames against draw
  logs of the running original; those tools and the decompiled code stay private (see
  [What is not in this repository](building.md#what-is-not-in-this-repository)).
- **D-nn** numbers are entries of the project's internal decision and debt register (not published); they are given so
  issues and code comments can refer to the same item.
- **Open** marks what is not established from the original. Behaviour where the port deliberately differs is marked
  **not original**; the player-facing list is in [Differences from the original](differences.md).

## Rendering

- [Frame tree and occlusion](frame-tree-and-occlusion.md) — mirrors, sky zone, warp zones, child frames, span-buffer occlusion.
- [Sprites and draw order](sprites-and-draw-order.md) — actor sprite sorting, mesh triangle order.
- [Render styles](render-styles.md) — EStyle (2 Masked, 3 Translucent, 4 Modulated), fatness, environment mapping, fog.
- [Lighting and coronas](lighting-and-coronas.md) — actor lighting, lightmaps, mover lightmaps, light flares.
- [Decals and actor shadows](decals-and-shadows.md) — decal lists drawn after their surface, clipping, shadows.
- [Meshes: LOD and skinning](meshes-lod-and-skinning.md) — LOD collapse, weighted-sum skinning, poses, animation lookup.
- [Particles](particles.md) — AParticleFX update on the render side, `appFrand`, self-destroy.
- [Procedural textures](procedural-textures.md) — FireTexture, WetTexture/WaterTexture, IceTexture.

## Engine and game

- [Timing and frame rate](timing-and-frame-rate.md) — frame-rate-dependent behaviour, why logic runs at 60 Hz.
- [x87 precision](x87-precision.md) — precision control, `fsin`/`fpatan`/`acos`, `_ftol`, MSVC `rand` and `qsort`.
- [Spell gestures and input](gestures.md) — gesture natives 426/427, the input path.
- [Music and Galaxy audio](music-and-galaxy.md) — streaming music, song end, music after a load, sound channels.
- [Saving, loading and map changes](saves-and-loading.md) — save/load flow, LoadMap screens, travel.
- [Editions](editions.md) — known releases and how they are identified.
- [Bugs and quirks of the original](original-bugs.md) — faults found while porting, their causes, what the port does.
