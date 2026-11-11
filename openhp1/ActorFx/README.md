# ActorFx — actor vertex fog, Opacity < 1, Fatness

CPU computation for OpenHP1 actor meshes (UE world space). The original computation was verified only on
synthetic data (no game files were available when it was written).

## Engine wiring

| what | where |
|---|---|
| vertex 48 -> 64 bytes: `FogR, FogG, FogB` (offset 48, location/TEXCOORD 5), `A` (offset 60, location/TEXCOORD 6) | `SdlWorldMesh.Vertex`, `SdlRenderer.CreatePipelines`, `classic.hlsl` `VIn`/`VOut` |
| mesh fragment: lit `saturate(c·light + fog)`, unlit `saturate(lin2srgb(srgb2lin(c)·light) + fog)`, alpha `t.a·A` for AlphaBlend | `classic.hlsl` (`vfog = i.fog·uvoff.z`, `fa`) |
| fog only on Opaque/Masked slots (decided from the D3DDrv state journal) | `SdlRenderer.FogAllowed` -> `uvoff.z` |
| pipeline 6 AlphaBlend: SRC_ALPHA / ONE_MINUS_SRC_ALPHA, no Z write, after opaque geometry | `SdlRenderer.CreatePipelines`, `ModeOf`, sorting in `SetActors` |
| Opacity slot texture: `ActorOpacity.OpacityPalette(masked: slot ‖ bMasked)`, keys `|opacity`, `|opacity-masked` | `SdlRenderer.TexDraw`, `TexKey`, `ExpandP8`, `TexDrawOf` |
| slot mode — `ActorOpacity.Resolve` (instead of `ActorBuilder.BlendOf(style, flags)`), batch key with the palette mask | `SdlActors.Emit`, `DrawFor` |
| vertex fog: `ActorFogFrame.Apply` before Fatness, `SpecularByte/255` into the vertex; `Gather` also for unlit | `SdlActors.Prepare`, `ComputeLight`, `Emit` |
| Core `UEActorLights.Fog` (`ActorFogFrame`), `Begin` before `Build` | `OpenHP1Program.LoadScene`, `FrameDraws` |
| "in a fog zone" count — `IsFogZone(ZoneNumber)`; the "fog: …" line in `ActorStats`; Opacity<1 — among drawn actors | `SdlActors.ActorStats` |

GPU check — scene 4 of `--selftest-gpu` (`<out>.actorfx.png`) and 22 `fx-*` mutations in
`openhp1/selftest_redtest.sh`; output — `openhp1/README.md`. Mutation `fx-layout-attr-count` guards the vertex
attribute count (with `num_vertex_attributes = 5` fog and alpha arrive as zeros).

## Ported

Ported from the former reference port (no longer in the repository):

| behaviour | port |
|---|---|
| `ApplyVertexFog` (skip order, record selection by the actor's light list, vertex to camera space, per-actor `outside`, quantisation) | Core `HP1.Core/Runtime/Render/ActorFogFrame.cs` `ActorFogFrame.Apply`, `SpecularByte` |
| static volumetric lights: chains of the leaves the frame's walk visited, culled | Core `LevelFogMaps.FrameStaticVolumetric` |
| `Bind`/`Unbind`: no fog surfaces — no layout | `ActorFogFrame.Bind` |
| per-frame setup: exit on an invalid camera, basis `x=right, y=up, z=fwd`, `ActorFogRecords` BEFORE frustum culling, static/dynamic set, colour `FGetHSV(H,S,255)·Brightness·Scale/255` | `ActorFogFrame.Begin` |
| `IsFogZone` (PF_RenderFog by the actor's zone) | `ActorFogFrame.IsFogZone` |
| fog body (LightAndFog, Render.dll.c:16990-17110) | NOT a copy: calls Core `FogMap.VertexFog` / `FogMap.MakeLight` |
| `Resolve` (render state: alpha blend above modulated/translucent/mask, SRCALPHA/INVSRCALPHA, no Z write, no alpha test, masked palette) | `ActorOpacity.cs` `ActorOpacity.Resolve` |
| vertex opacity `Opacity < 1 ? Opacity : 1`, pixel alpha `texel.a · opacity` | `ActorOpacity.VertexAlpha` |
| palette-alpha texture (`OpacityDraw`) | `ActorOpacity.OpacityPalette` |
| Fatness offset | Core `HP1.Core/Runtime/Render/ActorFatness.cs` `Offset`, `Apply` |

Fatness rule: offset of `Fatness/16 − 8` units along the vertex normal (Render.dll.c:9328, 14851). The
reference port's extra `0.02` factor converted to Unity metres and is not ported.

## Not ported / STOP

* **Fog under Translucent/Modulated/Opacity < 1 (R4)**: fog only on Opaque/Masked, per the D3DDrv journal
  (decompile + objdump disassembly of 1.53, `AdjustPolyFlags`/`SetBlending` 0x10009280/0x100092d0). The
  reference port added fog under any blending.
* **Procedural textures with Opacity < 1** — `LoadProc` returns the procedural texture as is; it has no
  palette-alpha variant (nor a masked one); carriers in campaign frames were not counted.
* **Vertex alpha quantisation** — D3DDrv writes the vertex alpha as a byte `Light.W·255`; the port keeps a float
  (output `t.a·A` in float, blending in the target's float). Byte quantisation is not added.
* **"No mesh" counter** (`FogSkipNoMesh`) — not applicable in OpenHP1.
* **`outside` hysteresis across actors** — not tested: per Core `FogMap.Density` it "switches the form of
  the test, not the result"; no distinguishing input was found (V9: "form from the source, no proof
  here").

## Divergences (found, NOT fixed — the fix is outside this subsystem or needs a source)

* **R1 (fixed: count by `IsFogZone(a.ZoneNumber)`).** `a.FogZone` is the actor's OWN `bFogZone` bit (always
  false for non-ZoneInfo, `WorldFrame.cs:453-457`), so counting by it gives 0 for ordinary actors.
  PF_RenderFog of an actor goes by the actor's zone (`IsFogZone(ZoneNumber)`).
* **R2 (fixed 2026-10-29).** Actor fog no longer depends on fog surfaces: shipped LightAndFog does not read
  surface fog maps; the `layoutBound` gate is removed.
* **R3. Unlit actors (per shipped).** `SetupForActor` exits before light selection when bUnlit
  (0x10b09b79, Render.dll) — no lights, fog of an unlit actor gets `SkipNoLights`. `Gather` exits on that
  gate (`UEActorLighting.Gather`, check `validate_actor_light`, target TorchFire0).
* **R4 (fog only Opaque/Masked).** See above.
* **R5. Hysteresis `DAT_10b658d8`**: global in shipped, per actor here (`int outside = 0`).
* **R6. Order of Fatness and fog.** Fog is computed on unshifted mesh vertices, lighting on shifted ones.
  The shipped order inside DrawLodMesh (offset :14851 vs LightAndFog :16990) is not verified against the
  decompile.
* **R7. An invalid camera** keeps the basis and records of the previous frame (setup exits before the
  reset) — ported, test `fog-stale-invalid-camera`.
* **R8. Camera basis.** Core `UEValue.GetAxes` (as in `SdlFog.cs`). Only orthonormality matters for actor
  fog: the light and the vertex go through one basis, and `FogMap.Density` depends only on dot products.
* **R9. Opacity palette.** The OpenHP1 texture store is palettized, so the palette is always available.

## Tests

```
cd openhp1/tests/ActorFxTests && DOTNET_ROLL_FORWARD=Major dotnet run -c Release
...
checks=44 failed=0
ACTORFX TESTS PASS
```

Expectations are independent, in the test: fog analytically (full chord through the sphere centre: `v = 4K`,
`K = VolB·(Brightness·Scale/255)/64`, `f = 2v`; LightSource 2: `v = K·|T|/R`), LightAndFog blending and
`FGetHSV` rewritten in the test with its own lines, tolerance 3e-3 relative (Core's table FastSqrt vs
`Math.Sqrt`); Opacity — the `Resolve` branch table by hand; Fatness — `Fatness/16 − 8` by hand.
Branches: no fog zone, no layout, no basis, no lights, no volumetric light, empty mesh, record set
(static/dynamic/Type/Brightness/Radius/Destroyed/ExportIndex 0/VolumeRadius 0), rotated and translated camera,
light order (AB vs BA), saturation, stale basis, record reset, ROUND to even; Opacity = 1 vs < 1, priority over
Modulated/Translucent/mask, palette.

## Red mutations

```
sh openhp1/tests/ActorFxTests/redtest.sh
```

Each mutation is a sed of EXACTLY one ported line (diff-checked), a test run, an expected failure of the named
check, restore with `cp` + `cmp`, and a base run at the end. Summary of the output:

```
[F01] RED  IsFogZone -> true                         failed: fog-no-zone fog-empty-mesh-not-counted fog-no-layout
[F02] RED  Bind without the layout condition         failed: fog-no-layout
[F03] RED  no lights not caught                      failed: fog-no-lights fog-no-volumetric
[F04] RED  no volumetric not caught                  failed: fog-no-volumetric fog-empty-mesh-not-counted
[F05] RED  invalid camera builds a basis             failed: fog-no-basis fog-stale-invalid-camera
[F06] RED  records not reset                         failed: fog-records-reset-per-frame
[F07] RED  empty mesh counted                        failed: fog-empty-mesh-not-counted
[F08] RED  output not zeroed                         failed: fog-no-zone
[F09] RED  dynamic without a fog zone                failed: fog-record-selection
[F10] RED  any export is static                      failed: fog-record-selection
[F11] RED  LightRadius 0 is a light                  failed: fog-record-selection
[F12] RED  Destroyed is a light                      failed: fog-record-selection
[F13] RED  VolumeRadius 0 is a record                failed: fog-record-selection
[F14] RED  brightness without Scale                  failed: fog-record-k-scale
[F15] RED  colour without Scale                      failed: fog-record-k-scale
[F16] RED  Hue/Saturation swapped                    failed: fog-order-AB fog-order-BA fog-saturation
[F17] RED  LightSource always 0                      failed: fog-ambient-source fog-order-AB fog-order-BA fog-saturation
[F18] RED  LeafVolumetric chain without zero         failed: fog-static-chains
[F19] RED  light without camera subtraction          failed: fog-rotated-rgba fog-record-camera-space
[F20] RED  vertex without camera subtraction (x)     failed: fog-rotated-rgba
[F21] RED  ty along the x axis                       failed: fog-ambient-source
[F22] RED  light order reversed                      failed: fog-order-AB fog-order-BA
[F23] RED  Fog.a = r                                 failed: fog-chord-rgba fog-rotated-rgba fog-ambient-source fog-order-AB fog-order-BA
[F24] RED  actor not counted                         failed: fog-chord-applied fog-rotated-applied fog-empty-mesh-not-counted
[F25] RED  ROUND away from zero                      failed: fog-byte-round-even
[F26] RED  camera at the origin                      failed: fog-rotated-rgba fog-record-camera-space
[O01] RED  Opacity = 1 -> alpha blend                failed: blend-opaque blend-style-masked ... (8)
[O02] RED  Opacity < 1 not blended                   failed: blend-opacity ... (5)
[O03] RED  Modulated above Opacity                   failed: blend-opacity-over-modulated
[O04] RED  no masked palette under Opacity           failed: blend-opacity-masked-palette
[O05] RED  SRC = ONE                                 failed: blend-opacity ... (5)
[O06] RED  vertex alpha without the cap at 1         failed: opacity-vertex-alpha
[O07] RED  palette without the mask                  failed: opacity-palette-masked
[O08] RED  Style 2/3 swapped                         failed: blend-style-masked blend-style-translucent blend-opacity-over-translucent blend-opacity-masked-palette
[O09] RED  no Modulated                              failed: blend-modulated-over-mask blend-modulated-over-translucent
[O10] RED  Z write under Opacity                     failed: blend-opacity ... (5)
[T01] RED  Fatness without −8                        failed: fat-offset fat-apply
[T02] RED  neutral 0 instead of 128                  failed: fat-offset
[T03] RED  offset against the normal                 failed: fat-apply
BASE PASS: checks=44 failed=0
mutations: 39, red: 39
REDTEST PASS
```

(Lines shortened: the script prints the exact form `[id] RED rc=1 expected <check> | failed: …`.)

## State (C1)

| Native/system | State (C1) | Owner | Headless | Activated | Oracle | Dependencies |
|---|---|---|---|---|---|---|
| Actor vertex fog (`ActorFogFrame`, CPU) | SOURCE_PORTED (wired into `ActorBuilder`; not executed in a game frame by the test) | OpenHP1 presentation | — | no | synthetic: chord/depth analytics, 44 checks, 39 mutations | R5–R8 |
| Vertex fog in the shader/renderer (composite, per-mode tolerance) | ACTIVATED (scene 4 of `--selftest-gpu`, lavapipe; Metal/D3D12 not executed) | OpenHP1 presentation | — | synthetic via `SetActors`/`Draw` | formulas in double, 9 mutations | game frames not captured |
| Opacity < 1 (`ActorOpacity` + pipeline 6 + `ExpandP8`) | ACTIVATED (scene 4, lavapipe; `Resolve` in `Emit` — SOURCE_PORTED, not executed in a game frame) | OpenHP1 presentation | — | synthetic | branch table by hand; GPU — blending formulas, 11 mutations (+2 vertex layout) | procedural textures without an alpha variant |
| Fatness (`ActorFatness`) | SOURCE_PORTED | OpenHP1 presentation | — | no | formula by hand | point normals of `ActorBuilder` (shipped rule not verified) |

Nothing above SOURCE_PORTED is claimed: no port has been executed in an engine frame, and there is no
comparison with the live original.
