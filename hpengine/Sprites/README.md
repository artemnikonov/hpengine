# Sprites — sprites, particles, coronas, shadows (HPEngine)

Geometry for everything in the Core frame that is NOT drawn as a mesh. Input: `WorldFrame` (plus texture /
visibility / trace delegates); output: vertex lists in UE world space (position, UV, RGBA colour) with a texture
path and a blend mode. No GPU calls.

The behaviour reference was the former Unity frontend (`HP1SpriteBridge.cs`, `HP1ParticleBridge.cs`,
`HP1CoronaBridge.cs`, `HP1ActorShadow.cs`; no longer in the repo — line refs below point into it). Unity coordinates
(×0.02, swapped axes) are NOT ported: everything is in UE world space, view axes come from
`UEValue.GetAxes(Camera.Rotation)` (X forward, Y right, Z up).

## Ported

| reference (Unity Scripts) | port | what |
|---|---|---|
| HP1SpriteBridge.cs:98-102 | SpriteBuilder.cs `Build` | DT_Sprite(1)/DT_SpriteAnimOnce(7), then the shared visibility gate (delegate) |
| HP1SpriteBridge.cs:108-109 | SpriteBuilder.cs `Build` | texture path from the frame; empty/`None` is not drawn |
| HP1SpriteBridge.cs:110-126 | SpriteBuilder.cs `AnimOnceFrame` | AnimNext chain frame: fraction = clamp(1 − LifeSpan/Default.LifeSpan), NaN → 0, `round_even(n·fraction − 0.5)`, clamp [0, n−1] |
| HP1SpriteBridge.cs:131, :179, :306-327 | SpriteBuilder.cs `BlendOf` | Style 4 → Modulated, 3 → Translucent, else bMasked → Masked / Opaque; IceTexture only Style 3/4, otherwise refused |
| HP1SpriteBridge.cs:166-174 | SpriteBuilder.cs `Build` | quad USize·DrawScale × VSize·DrawScale along the frame view axes (Y, Z of GetAxes), centred on Location |
| HP1SpriteBridge.cs:176 | SpriteBuilder.cs `Build` | vertex colour = clamp01(ScaleGlow), alpha 1 |
| HP1ParticleBridge.cs:148-191, :249-252 | ParticleBuilder.cs `Build`, `PrimitivePorted` | Style/Opacity/RenderPrimitive from the emitter snapshot; Style 4 → Modulated, else Translucent; Style 2 → Translucent with a report; RenderPrimitive ∉ {0,1,2,3} → emitter not drawn |
| HP1ParticleBridge.cs:299-310 | ParticleBuilder.cs `VertexColor` | FUN_10b16ab0: a = Opacity·alpha; for a < 1 colour ×a, Style 4 adds (1−a)·0.5; clamp01 |
| Render.dll FUN_10b184d0 (Render.dll.c:25995-26200) | ParticleBuilder.cs `Billboard` | Width×Length billboard in the view plane, rotated by SpinPos; corner UVs as shipped (quad top V = VSize−1, edge USize−1) |
| HP1ParticleBridge.cs:312-314, :321-457 (constants :362-366, body :414) | ParticleBuilder.cs `Liquid` | PPRIM_Liquid: hanging (DripTimer > 0) and falling drop, Render.dll constants, fan (0,1,2),(0,2,3), then the same billboard |
| Render.dll 0x10b26742..0x10b26e0f, FUN_10b26f10 | HP1.Core UECoronas.cs `Step` | 32-slot table, fading, leaf candidates, ray, colour — decided by Core, emitted as WorldFrame.Coronas |
| Render.dll 0x10b26b03..0x10b26d71 | CoronaBuilder.cs `Build`, `Project` | frame projection (Z > 1, Proj.Z / Z + FX2/FY2), tile Frame->X·DrawScale·0.8 centred, Translucent without depth test |
| HP1ActorShadow.cs:97-128 | DecalBuilder.cs `Build` | frame decal pieces → triangle fan, world points, U/V as fractions |
| HP1ActorShadow.cs:143-170, :89-92 | DecalBuilder.cs `Build` | texture/style from the decal actor snapshot; unresolved or Style ≠ 4 → not drawn; vertex colour white |
| HP1Sprite*/HP1Particle* shaders | SpriteTypes.cs `SpriteBlend` | mode names match actor meshes (`ActorBlend`) |

## NOT ported, and why

1. **Particle `SetupOnce` read textures NOT from the frame** (HP1ParticleBridge.cs:92-135). Through the sync window
   (`HP1CoreGate.Post`) it read `UEObject.Textures` of the emitter VM object, took the FIRST resolvable one, and if
   the path did not resolve, fell back to the procedural `HP1FireTexture.GetTexture(name leaf)`. `ApplyFrameTexture`
   (:478-498) also tried the path, then the name LEAF. Here only the frame's `ParticleInstance.TexturePath` is used;
   resolving it is the job of the `size` delegate. What the frame lacks: the path lists are there
   (`ParticleState.TexturePaths`, `ParticleInstance.TexturePath`), but not the **texture class** (static /
   `FireTexture` / `IceTexture`) by which the reference picked the pixel source. Two options, author's choice:
   (a) take the class from the static texture table (`UETextureMetrics.Row.Class` — a property of the texture,
   not the frame; nothing added to the frame);
   (b) add `ParticleState.TextureClasses` to the frame alongside `TexturePaths`.
   Separately: the fallback by name LEAF is name matching, which the port does not do (lookups are exact); it must not be ported — an
   unresolved path is an export defect, not a reason to search by leaf. Also, "first resolvable of Textures"
   (reference) vs "Textures[0]" (Core, `ParticleInstance.TexturePath`) can differ when the first texture is not
   exported; such a particle currently gets the refusal `texture_unresolved:<path>`.
2. **Class `Default.LifeSpan`** (for DT_SpriteAnimOnce) is not in the frame; it comes through the
   `defaultLifeSpan(className)` delegate. Proposal: `ActorSnapshot.DefaultLifeSpan` or a class-defaults table in Core.
3. **IceTexture**: glass map, `MoveIcePosition` wobble, `HP1SpriteIce*` shaders belong to the procedural-texture
   subsystem (CLOUD_TASK_PROCTEX); here only the mode choice and the refusal for Style ≠ 3/4. The "no glass map →
   do not draw" check (HP1SpriteBridge.cs:145-146, :160) belongs there too. For IceTexture the reference
   takes the quad size from SourceTexture (:141, :169) — the `size` delegate must return exactly that. Whether IceTexture and
   its source have equal USize is NOT VERIFIED.
4. **Particle billboard** — Render.dll FUN_10b184d0 (drawer vtable 0x10b388f8, chosen in FUN_10b15090 when bit 0 of
   `AParticleFX+0x3c8` = bShellOnly is clear, disasm 0x10b159d2). Corners and UVs as shipped:
   (−w,−h) → (0, VSize−1), (+w,−h) → (USize−1, VSize−1), (+w,+h) → (USize−1, 0), (−w,+h) → (0, 0) in view axes
   (y down), i.e. the quad TOP carries V = VSize−1. Checked against the live original (hp1drawlog detail=1,
   Lev_Tut1, PotFire08 64×64: corners (0, 0.984375) … (0.984375, 0)). Rotation R(θ) matches shipped (angle from
   the GMath sine table by SpinPos·65536/2π; here MathF of the same angle).
   **Not ported:** the bShellOnly branch — FUN_10b16c70 (screen quad with ftol half-size in pixels, no rotation,
   V not flipped); a bShellOnly emitter is drawn by the FUN_10b184d0 branch.
5. **Unity linear space** — `pow(ScaleGlow, 2.2)` (HP1SpriteBridge.cs:177), the `×2.2974` of modulated shaders,
   corona `c.linear` (:176) are deliberately NOT ported: they were corrections for Unity's linear project to get
   UE1's gamma result. Colours here are UE gamma values; the HPEngine renderer works in gamma
   (`saturate(texel · light)`, SdlShaders.cs:47).
6. **Sprite bNoSmooth** — the reference only warned once (:186-194); filtering was not ported there either; here
   neither (it is a renderer sampler matter).
7. **Lesson-board telemetry** (:214-287) — Unity diagnostics, not behaviour.
8. Differences of a pure function from a Unity object (all are loud reasons in `Report`):
   * sprite without a frame camera — refusal `no_camera` (the reference kept the previous quad rotation, :173);
   * particles without a camera — refusal `no_camera`;
   * corona without a frame camera — refusal `no_camera`, no brightness step;
   * decal without its actor snapshot — refusal `no_snapshot` (the reference took `Texture` from VM Props or
     `ShadowT`, :65-70; unavailable without the VM);
   * emitter texture — the path of the emitter's LAST particle (the reference set one material per emitter from
     each particle's path, so the last one remained at draw time);
   * emitter order — frame order; coronas — WorldFrame.Coronas slot order.
9. **Particle visibility gate** — the same gate as for meshes: shipped draws a particle system from
   `DrawActorSprite` of an accepted actor (HP1.Reference/Render/RenderDrawOrder.cs:126-149). For shadows the gate
   is explicit (HP1ActorShadow.cs:73-77).
10. **Sprite position** — `Location` (quad at the actor transform origin, :297); whether the proxy transform
    applied `PrePivot` is NOT VERIFIED.

## Divergences found (not fixed — files outside this subsystem)

* **`hpengine/SdlActors.cs:50`: `DT_SpriteAnimOnce = 4`.** In the game enum 4 is `DT_RopeSprite`, and
  `DT_SpriteAnimOnce` = 7 (`EDrawType` names: se_harness/bsp_diff/BspDiffProgram.cs:2794-2798). The engine's
  `Stats.Sprite` counter counts the wrong actors. Here it is 7, test `anim.dt4_not_sprite`.
* **`HP1.Core/Runtime/VM/WorldFrame.cs:536`**: the comment "EStyle: 3=Masked 4=Translucent" contradicts the
  numbering used by the engine (2=Masked, 3=Translucent, 4=Modulated — SdlActors.cs:52).

## Tests

```
cd hpengine/tests/SpritesTests && DOTNET_ROLL_FORWARD=Major dotnet run -c Release
...
SpritesTests: pass=101 fail=0
OK
```

Synthetic frames through the builders' `Build()`; expectations computed by hand (numbers in `Program.cs`
comments): quad corners at yaw 90° and pitch 45°, UV, modes, ScaleGlow; the AnimOnce frame table (including
round-to-even at 0.5 and 2.5, NaN, clamping); particle colour (Translucent/Modulated/clamp), billboard with SpinPos
0 and π/2; hanging and falling drop (vertices, UV (U−1)/U), drop behind the camera; HueSat (three sectors, divisor
84, saturation), corona projection rectangle, fade-in/fade-out by steps, occlusion, leaf culling, lamp behind the
camera, no Skin; decal fan, UV, white colour, refusals by style/texture/snapshot/pieces.

## Red mutations

`sh hpengine/tests/SpritesTests/redtest.sh` — 38 mutations; each changes EXACTLY one ported line (checked), builds,
and the tests must fail with FAIL on the expected check; the file is restored byte-for-byte (`cmp`, plus `trap` on
interrupt); at the end the base must be green. The reverse is checked too: a harmless mutation (a comment) prints
`GREEN!` and fails the script. Output:

```
RED     sprite.yaw90  (7 failures)  [SpriteBuilder.cs: hh = uSize * ds * 0.5f;]
RED     sprite.yaw90  (7 failures)  [SpriteBuilder.cs: var (right, _, up) = UEValue.GetAxes]
RED     anim.life=1.5  (3 failures)  [SpriteBuilder.cs: MidpointRounding.AwayFromZero]
RED     anim.life=1.25  (4 failures)  [SpriteBuilder.cs: n * clamped]
RED     anim.nan  (2 failures)  [SpriteBuilder.cs: float clamped = float.IsNaN(fraction) ? 1f : 0f;]
RED     anim.life  (11 failures)  [SpriteBuilder.cs: DT_SpriteAnimOnce = 4;]
RED     sprite.blend  (2 failures)  [SpriteBuilder.cs: : style == STY_Translucent ? SpriteBlend.Modulated]
RED     sprite.blend  (2 failures)  [SpriteBuilder.cs: : SpriteBlend.Opaque;]
RED     sprite.glow.clamp  (2 failures)  [SpriteBuilder.cs: float glow = a.ScaleGlow;]
RED     sprite.visibility_gate  (3 failures)  [SpriteBuilder.cs: if (false) continue;]
RED     sprite.reject  (3 failures)  [SpriteBuilder.cs: if (false && style != STY_Modulated]
RED     particle.color.modulated  (2 failures)  [ParticleBuilder.cs: float gray = (1f - a) * 0.25f;]
RED     particle.color.translucent  (3 failures)  [ParticleBuilder.cs: if (a < 0f)]
RED     particle.color.clamp  (2 failures)  [ParticleBuilder.cs: return (r, ]
RED     particle.billboard.spin  (2 failures)  [ParticleBuilder.cs: float rx = x * cs - y * sn;]
RED     particle.prim4_refused  (2 failures)  [ParticleBuilder.cs: PrimitivePorted(int prim) => prim >= 0;]
RED     particle.visibility_gate  (2 failures)  [ParticleBuilder.cs: if (false) continue;]
RED     liquid.hang.v1  (3 failures)  [ParticleBuilder.cs: const float LiquidSide = 1.0f;]
RED     liquid.fly.v0  (4 failures)  [ParticleBuilder.cs: const float LiquidThree = 2.0f;]
RED     liquid.fly.v3  (2 failures)  [ParticleBuilder.cs: float len = rad > sp ?]
RED     liquid.hang.uv  (6 failures)  [ParticleBuilder.cs: float u1 = 1f, v1 = 1f;]
RED     liquid.behind  (2 failures)  [ParticleBuilder.cs: if (hz <= -1e30f) return;]
RED     liquid.hang.v1  (6 failures)  [ParticleBuilder.cs: Liquid(d, in pi, frame.Camera.Location, fwd, right, up,]
RED     decal.rejects  (3 failures)  [DecalBuilder.cs: if (false)]
RED     decal.fan  (2 failures)  [DecalBuilder.cs: d.Indices.Add(b0 + i);]
RED     decal.uv  (2 failures)  [DecalBuilder.cs: pc.V[i], pc.U[i], 1f, 1f, 1f, 1f]
RED     decal.rejects  (2 failures)  [DecalBuilder.cs: if (false) continue;]
RED     decal.tex_missing  (3 failures)  [DecalBuilder.cs: if (false)]
--- base (no mutations)
SpritesTests: pass=101 fail=0
OK
redtest: caught 38, not caught/broken 0
```

(The block above is verbatim script output.) The billboard UV orientation (item 4) was checked against the live
original (hp1drawlog detail=1); mutations `particle.billboard.uvTL/uvBR` hold it in the fixture.

## Status

Verification is synthetic builder fixtures (`targeted` level): the claim ceiling is below `CLOSED`. The reference
was the Unity frontend, not shipped, so no oracle proof is claimed: expectations are computed by hand from the
reference rules.

| Native/system | State (C1) | Owner | Headless | Unity | Activated | Oracle | Dependencies |
|---|---|---|---|---|---|---|---|
| SpriteBuilder (DT_Sprite/AnimOnce) | SOURCE_PORTED (from the Unity reference) | HPEngine presentation | — | reference | no (synthetic fixture only, not wired to the engine) | no (hand-computed expectations) | engine visibility gate; UETextureMetrics; UETextureAnim; Default.LifeSpan (not in frame); IceTexture — PROCTEX |
| ParticleBuilder (billboard, colour, PPRIM_Liquid) | SOURCE_PORTED (billboard — FUN_10b184d0; UVs checked against the live original) | HPEngine presentation | — | reference | no | no | texture resolution / FireTexture class (SetupOnce, item 1); bShellOnly branch (FUN_10b16c70) not ported |
| CoronaBuilder | PORTED (projection and tile; decisions in Core UECoronas) | HPEngine presentation | — | — | no | no | WorldFrame.Coronas (Core UECoronas, SelfTest coronas_core_drawframe) |
| DecalBuilder | SOURCE_PORTED | HPEngine presentation | — | reference | no | no | Core: AttachDecal/ClipDecal (BonePos 257 debt per WorldFrame.cs:664-669) |
