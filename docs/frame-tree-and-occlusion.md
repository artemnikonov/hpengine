# Frame tree and occlusion

The original renderer does not draw "the level" once. `URender::DrawWorld` builds a tree of frames: a master frame for the player's camera, and child frames for every sky window, mirror and warp-zone portal the BSP walk meets. Each frame has its own camera, near clip plane, span buffers and list of visible sprites. The decision of what is visible lives in `URender::OccludeBsp` (Render.dll 1.53, 0x10b22480..0x10b24f30) and `URender::CreateChildFrame` (0x10b20790). HPEngine ports both into Core and hands the frontend the finished tree, so the frontend only draws it.

## Why occlusion is game logic, not just drawing

- OccludeBsp decides which actors enter a frame's sprite list (`Frame->Sprite`, +0xa4). Only those get `GetFrame`/`ApplyAnim` and an emitter `Update(0)` in `DrawFrame`. Those calls write game state, so culling is part of the simulation (Render.dll 0x10b22c79, 0x10b22d8e, 0x10b2354a, 0x10b24ae6, 0x10b227ff, 0x10b2468d).
- Concrete case: the third-person weapon branch of `DrawActorSprite` writes the weapon's Rotation/Mesh/DrawScale and the pawn's WeaponLoc/WeaponRot, but only for actors drawn after occlusion (0x10b32fd1..0x10b330b4). A simplified culling produced a different `baseWand0.Rotation` on Lev5_Snare (D-56).
- OccludeBsp also writes node flag bits 0x10/0x08 and each zone's `LastRenderTime` (0x10b227ff, 0x10b2468d).
- Port: [`HP1.Core/Runtime/Render/UEOcclusion.cs`](../HP1.Core/Runtime/Render/UEOcclusion.cs) (OccludeBsp, sprites, CreateChildFrame), [`HP1.Core/Runtime/VM/UEWorldFrameProducer.Views.cs`](../HP1.Core/Runtime/VM/UEWorldFrameProducer.Views.cs) (drives it per tick, `OccludeWorld`).

## The walk

- A near/far node stack walks the BSP front to back. Each node surface is rasterised and tested against its zone's span buffer; opaque polygons subtract their spans from the buffer, so what is behind them is never reached (`CopyFromRaster`, `CopyFromRasterUpdate`).
- Node bounds are tested with bit 0x10; a node without it re-tests its bound only when `(iNode ^ DrawWorld counter) & 0xf == 0` (counter at DAT_10b719b4, incremented at `URender::DrawWorld` entry 0x10b27ba0). The port keeps the counter growing across level loads as the original does.
- The walk ends early when all active zones are emptied; zones are entered through portals using `MergeWith` on span buffers.
- Coplanar nodes: a node is followed by its chain of coplanar nodes via `FBspNode.iPlane` (+0x28). The earlier polygon occludes the later one only when it is opaque by the test `(Flags & 0x47) == 0 || (Flags & 0x4000001) == 0x4000001 || (Flags & 0x8000000)`. A node with no visible spans is not drawn at all. Shipped data has such pairs (Lev3_Troll nodes 6608/6609: the second polygon has a lightmap without sources and would show as a black patch by the troll's door). Port: [`HP1.Core/Runtime/Render/CoplanarOcclusion.cs`](../HP1.Core/Runtime/Render/CoplanarOcclusion.cs) computes the visible convex pieces of later nodes once per world (not a per-frame emulation: nodes of one chain lie in one plane and are visited in one order, so the result does not depend on the viewpoint). Open: cases it does not resolve are counted and reported loudly (two-sided later node occluded differently from behind; an earlier PF_Invisible polygon over a visible one, where ShowFlags decide).

## Span buffer

- `FSpanBuffer` (0x20 bytes): StartY, EndY, ValidLines (number of spans, not rows), row-head index; `FSpan` is Start, End, Next, a sorted list of still-free spans of a row. Bodies: `AllocIndex` 0x10b2e3a0, `CopyFromRasterUpdate` 0x10b2e830..0x10b2eb44, `BoxIsVisible` 0x10b2e7c0..0x10b2e829, `MergeWith`, `CopyIndexFrom`.
- Span nodes are shared between buffers, as in shared memory in the original: copying a buffer copies only row heads, so a change through one buffer is seen by every buffer sharing the nodes. The port keeps this on purpose.
- Port: [`HP1.Core/Runtime/Render/UESpanBuffer.cs`](../HP1.Core/Runtime/Render/UESpanBuffer.cs). Memory management (the original's `FMemStack`, mark/release) is replaced by pooled arenas; this is allocation only and has no visible effect.
- The surface raster itself (ClipBspSurf and polygon rows) is in [`HP1.Core/Runtime/Render/UEBspRaster.cs`](../HP1.Core/Runtime/Render/UEBspRaster.cs).

## Sprites in the walk

- `FDynamicSprite` constructor 0x10b30790 computes the sprite's screen rectangle at its depth, maps the corners back to world through the frame's Uncoords and puts a filter item at the head of node 0's list.
- The filter item (vtable 0x10b395e4, `Filter` 0x10b31160) is split down the tree by node planes with a 16.16 fixed-point row split; a final item (vtable 0x10b39600, `PreRender` 0x10b319a0) reaches a leaf and does `CopyFromRaster` against the zone span buffer. The frame list is kept sorted by descending key.
- Known difference: the original does not write the filter item's sort key (+8/+0xc), and the final item insertion reads old GDynMem memory. The port compares only against final items. Only the PreRender order of sprites with equal keys inside one leaf can differ.
- Light items (vtable 0x10b395c8, `FilterLight` 0x10b31c70) are walked too, in both halves: the surface half (`URender::SurfLights`, per surface, newest first, at most 0x800 surfaces) and the leaf half (`URender::LeafLights`, volumetric flag at +0x14). A sprite takes the list of its actor's leaf after OccludeBsp.

## Child frames

- `CreateChildFrame` merges a new child with an existing one when Level, iSurf, parent, NearClip and zone are the same (span buffers merge, projection grows by screen bounds); otherwise it copies the parent, computes new Coords/Uncoords and `ComputeRenderSize` (Engine.dll 0x10383390, in the x87 24-bit window), and links the child at the head of the children list.
- Sky (0x10b236a6..0x10b239c0): coordinates come from the sky zone's actor Location and Rotation; the sky zone is that of the window surface's zone, not the viewer's. The old frontend used the viewer's zone and was wrong; this was removed (D-61).
- Mirror (0x10b23abd..0x10b23d44): reflection about the node plane (`MirrorByVector` Core 0x1011c7b0, `MirrorByPlane` 0x1011c850), with a near clip made from the node plane. A translucent mirror on a device without ShinySurfaces (RenDev +0x5c == 0, 0x10b23a8e..0x10b23ab8) creates no child; the surface is drawn opaque instead.
- Warp portal (0x10b23eb8..0x10b24436): the child camera is the portal's WarpCoords chained with the other side's. If `OtherSideLevel` (+0x3fc) or `OtherSideActor` (+0x3f8) is None, the renderer calls the script event `Generate` (0x10b23e17..0x10b23eb2). Both fields are transient, so after loading a save the renderer is the first thing that refills them on seeing the portal. Port: [`HP1.Core/Runtime/World/UELevelRuntime.ChildFrames.cs`](../HP1.Core/Runtime/World/UELevelRuntime.ChildFrames.cs).
- If the camera itself is in a warp zone with an other side, the whole frame is a child through the warp (0x10b227f0..0x10b22ad5).
- Chains work: on Lev5_Final the Mirror of Erised is a warp, then a mirror, then a warp again (surfaces 2080 -> 2864 -> 2094), matching the running original's camera and near clip values.
- Open: merging child frames of different branches (a sky and a mirror with the same surface index, near clip and zone) is not encountered in the campaign and stops loudly. A brush shared by two movers with a mirror on it is also a loud refusal.

## What the frontend receives

- `WorldFrame.Views` carries each child as an affine "frame world to master world" map plus its near clip, the branch kind and the source surfaces ([`HP1.Core/Runtime/VM/UEWarpView.cs`](../HP1.Core/Runtime/VM/UEWarpView.cs)). The frontend computes no cameras itself (D-61).
- Draw order: children's `DrawFrame` run before the parent's. A mesh runs `DrawActorSprite` once per frame whose list holds it, because `GetFrame` is not idempotent within a tick; an emitter or shadow updates at most once per tick ([`HP1.Core/Runtime/VM/UEWorldFrameProducer.Views.cs`](../HP1.Core/Runtime/VM/UEWorldFrameProducer.Views.cs), `DirectRenderStep`).

## Verification status

- Frame tree, node bits 0x18 and the `LastRenderTime` of 64 zones were compared with a reference run of the original Render.dll path over 39 maps x 150 ticks with no divergences (D-56); the mirror and warp child cameras were also checked against logs of the running original. Sprite lists of meshes match; emitters differ because the render random stream is consumed differently (a simplification the project allows).
- Open: with the D3D driver the original's own pass does not run in the comparison setup, so a translucent mirror with ShinySurfaces=True (portal WarpZoneInfo3 on Lev5_Final) has no reference (D-60).
