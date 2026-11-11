# OpenHP1 — our own engine (2026-10-04)

OpenHP1, the project's own engine frontend: .NET 9 + SDL3 (ppy.SDL3-CS) + SDL_GPU: Metal (Mac), Vulkan (Linux, Steam
Deck), D3D12 (Windows). Core brings up and ticks the world the same way as headless: `UEGameEngine` in Core
(`HP1.Core/Runtime/World/UEGameEngine.cs`, see `UEGameEngine.md` next to it); the engine draws the Core frame
(`WorldFrame`). The engine does not reference `runtime_poc`.

    cd openhp1
    dotnet run -c Release -- --map Lev_Tut1 --shot out.png   # no window: frame to PNG + timing
    dotnet run -c Release -- --map Lev_Tut1                  # window (F: free camera with --debug-cam, Esc: game key)
    dotnet run -c Release -- --map Lev_Tut1 --perf-title     # fps and Core/frame timings in the window title

Shader debugging: HP1_DBG=red|tex|lm.

Drawn from the Core frame: static BSP (Core coplanar occlusion, Core texture metrics), the Core lightmap atlas (tiles by
(lightmap, zone)), Classic formula saturate(t·lm/63.75) (D3DDrv MODULATE2X over RGBA7·2); skeletal and vertex-mesh
actors (Core poses), slot textures, styles, actor light, vertex fog and Opacity; sprites, particles, decals, coronas;
movers; procedural textures; sky, zone fog, mirrors and warp portals (Core child frames); HUD/menu; audio; input; level
travel.

## Shaders and backends

One source for the main shader, `shaders/classic.hlsl`, and one for the HUD, `Hud/shaders/hud.hlsl`. The scripts
`shaders/build.sh` and `Hud/shaders/build.sh` (DXC + SPIRV-Cross) build blobs that live in the tree and are embedded in
the assembly (`OpenHP1.shaders.<file>`):

| file | backend | produced by |
|---|---|---|
| `*.{vert,frag}.spv` | Vulkan | `dxc -spirv -fspv-target-env=vulkan1.0` |
| `*.{vert,frag}.dxil` | D3D12 | `dxc -T vs_6_0/ps_6_0` (signed by libdxil) |
| `*.{vert,frag}.msl` | Metal | `spirv-cross --msl --msl-version 20100 --msl-decoration-binding` from SPIR-V |

Rebuild after editing a source (DXC with libdxil: the DirectXShaderCompiler release for your OS; spirv-cross:
`apt install spirv-cross` / `brew install spirv-cross`). Byte-identical to the blobs in the tree: DXC v1.8.2505.1
(Linux, `1.9(dev;4950-b106a961)`, libdxil 1.9) and spirv-cross 2021.01.15+1.3.239.0 (apt Ubuntu 24.04); brew
spirv-cross 1.4.363.0 writes `powr` instead of `pow` in MSL, and DXC 1.9.26xx produces different DXIL:

    sh shaders/build.sh            # write blobs
    sh shaders/build.sh --check    # do the blobs in the tree match the source? (otherwise STALE, rc=1)
    sh Hud/shaders/build.sh --check
    DXC=/opt/dxc/bin/dxc sh shaders/build.sh   # dxc path if it is not on PATH

`Renderer.Create`: the device is created with `MSL | SPIRV | DXIL`, the blob format chosen by
`SDL_GetGPUShaderFormats`; it prints `GPU: <driver>, shaders <format>`. No blob for the device is a loud refusal. The
main shader and the HUD (`SdlHudPass.EnsureHud`) load blobs of the same format (`Shaders.Load("classic"|"hud", …)`).
The target has no sRGB (offscreen R8G8B8A8_UNORM, window: SDR swapchain).

### classic.hlsl

Bindings per the SDL_GPU convention: SPIR-V set 1 (vertex uniform `VU`), set 2 (combined image sampler: base 0,
lightmap 1, fog 2), set 3 (`FU` binding 0, `DL` binding 1); DXIL b0 space1, t/s 0..2 space2, b0/b1 space3, inputs
TEXCOORD0..6; MSL buffer(0/1), texture/sampler(0..2). Vertex `SdlWorldMesh.Vertex`: pos (0), uv (12), uv2 (20), light
float3 (28), uv3 (40), fog float3 (48, actor vertex fog = specular byte/255), alpha float (60, Opacity), 64 bytes,
location n = TEXCOORDn. `FU` is 64 bytes (`mode, uvoff, clip, screen`), `DL` 400 bytes (`count, pos[8], col[8],
meta[8]`) — dead, always pushed as zeros (dynamic BSP lights are in Core's lightmap); it goes with the next blob rebuild. Samplers/uniforms: vertex 0/1, fragment 3/2, as in `Renderer`; the depth-only pipeline (#5) and actor
AlphaBlend (#6: SRC_ALPHA/ONE_MINUS_SRC_ALPHA, no Z write) use the same shaders. `FU.uvoff.z` of an actor mesh batch is
the vertex fog admission (1 for Opaque/Masked), `uvoff.w` is 1 for AlphaBlend (alpha output `t.a·alpha`, otherwise 1).

ActorFx: lit mesh `saturate(c·light + fog)`, unlit `saturate(lin2srgb(srgb2lin(c)·light) + fog)`; fog only on
Opaque/Masked slots (D3DDrv clears PF_RenderFog under Translucent/Modulated, and Opacity < 1 carries Translucent; see
`ActorFx/README.md`). With fog 0 and non-AlphaBlend the frame is byte-identical to the one without ActorFx.

Formulas: clip plane, mode 4 (mirror: sampling at `pos.xy · screen.xy`), `HP1_DBG` debugging, `uvoff.xy` shift, mask 0.5,
mip bias −0.5, actor modes 2/3, mode 0: fog `lit·(1−a)+rgb` for a non-zero texel, without lights `c·l/63.75`, with
lights `lmCol = l/127`, bSpecialLit pair, NonIncidence `1−√ds`, otherwise `(1+2v³−3v²)·|cos|·R/max(R−inner,1)`, caps
of 1, result `c·lmCol·127/64`.

The clip matrix (`Camera.Rows`) is the same on Vulkan and Metal under SDL_GPU (z 0..1, +Y is the frame top); verified by
execution (mutations clip-x/y are red).

### Self-test without game files: `--selftest-gpu <out.png>`

Synthetic scenes are drawn by the same `Draw` (mirror passes, pipelines, samplers, matrix, FU/DL, backend blob);
expectations are computed from the formulas, not from the frame.

- **Scene 1** (`out.png`, 384×128): world: lit quad `t·L/63.75` (strict point StrictLm with no tolerance) (per channel
  ≈×0.99/×0.5/×2.5, saturation), mask (alpha 129 stays, 127 is cut), unlit, mip quad (LOD ≈ 0.75: with bias −0.5 level
  0, without it 1), background farther in depth drawn last; actors via `SetActors` (modes 2/3); fog with alpha over a
  lit surface; UV shift (0.5, 0). The `HP1_DBG=red|tex|lm` modes are checked too. Also the HUD pipeline is created from
  the device-format blob.
- **Scene 2** (`out.mirror.png`): mirror X = 300; a quad behind the camera is visible only in the reflected pass (2×2
  quarters, no flip); a quad behind the mirror is cut by the clip plane in the reflected pass and hidden by the mirror
  in the main one. Exactly one mirror pass is required.
- **Scene 3** — removed: dynamic lights on BSP are Core's (the lightmap's +0x48 records, `LevelSurfLightmap`); the
  shader `DL` block is dead (all-zero) until the blobs are rebuilt with dxc.
- **Scene 4** (`out.actorfx.png`, 640×128), ActorFx via `SetActors`: fog on a lit Opaque mesh (K1) and Masked (K2,
  saturating); on unlit (U1, U2 saturating; fog in gamma, not linear); fog present in vertices but NOT applied to
  Translucent (NT), Modulated (NM), AlphaBlend (NA) over the world background; Opacity 0.5 over an opaque actor
  inserted LAST (order "blended after opaque"): palette alpha 255 (O1) and 128 (O2, `t.a·A`), index 0 of a masked slot
  (O3, `(0,0,0,0)`) and without mask (O4); a bMasked texture in an unmasked slot (O5); AlphaBlend in front of
  Translucent inserted later and lying farther (ZA: no Z write). AlphaBlend textures are P8 via `Renderer.ExpandP8` by
  variant keys, as in game.

**Tolerance is 1 byte**: float→UNORM8 rounding and float32 shader arithmetic vs double expectations (pow, fog, light).
Filtering contributes nothing by construction: point sampling of uniform world texels, 1×1 textures for linearly
sampled ones, lightmap/fog at the centre of a uniform 4×4 block or at a clamped corner, the mirror at the target texel
centre, checked pixels ≥3 px from boundaries. A region without checked pixels or an indistinguishable bSpecialLit pair
is a refusal (`REFUSED`, rc=3). Exit codes: 0 PASS, 1 FAIL, 2 no GPU, 3 REFUSED.

    cd openhp1
    dotnet build -c Release
    SDL_VIDEO_DRIVER=offscreen ./bin/Release/net9.0/OpenHP1 --selftest-gpu out.png
    sh selftest_redtest.sh         # red mutations + base in all HP1_DBG modes

Result (Ubuntu 24.04, lavapipe: llvmpipe LLVM 20.1.2 / Vulkan 1.4.318, libSDL3 from ppy.SDL3-CS 2026.1002.1; .NET 10
SDK with `DOTNET_ROLL_FORWARD=Major`):

    GPU: vulkan, shaders SPIR-V
    selftest-gpu hud: pipeline from blob created
    selftest-gpu scene1 vulkan HP1_DBG=-: A=1600, B-cut=800, B-keep=800, C=1600, clear=14048, D=2116,
      E1=2116, E2=2116, F1=2116, F2=2116, G=312, H=1600, U=1600; mismatches 0, max deviation 0
    selftest-gpu mirror vulkan: clear=9660, mirror-empty=2964, mirror-Q=676; mismatches 0, deviation 0
    selftest-gpu dyn vulkan: dyn-normal=13456, dyn-special=13456; mismatches 0, deviation 0
    selftest-gpu actorfx vulkan: bg2=4381, bg4=7406, clear=19312, K1=2116, K2=2116, NA=2116, NM=2116,
      NT=2116, O1=2116, O2=2116, O3=2116, O4=2116, O5=2116, OB=1356, U1=2116, U2=2116, ZA=2116;
      mismatches 0, max deviation 1 (tolerance 1)
    selftest-gpu PASS
    (HP1_DBG=red, tex, lm: scene 1 PASS as well, deviation 0)

`selftest_redtest.sh` (≈50 min on lavapipe: 51 mutations, each rebuilds the blobs and runs `dotnet build`); each
mutation changes one line; `sed -i.bak` + removing `.bak` works the same with GNU and BSD (macOS) sed:

    RED no-mip-bias  lm-divisor-32  mask-threshold-0.4  swap-tex-bindings
    RED dbg-red-color  dbg-tex-no-bias  dbg-lm-gain
    RED actor-lit-gain  actor-unlit-gamma  vertex-light-lost
    RED fog-alpha-ignored  fog-uv-lost  uvoff-lost  mirror-not-screen  clip-plane-off
    RED dyn-nonincidence  dyn-falloff  dyn-inner-scale  dyn-angle  dyn-special-pair
    RED dyn-gain-127-64  dyn-lm-127  hud-blob-msl
    RED clip-y-flipped  clip-x-flipped  no-depth-test
    RED fx-lit-fog-lost  fx-unlit-fog-linear  fx-unlit-fog-lost  fx-fog-vertex-lost  fx-fog-shader-ungated
    RED fx-fog-gate-transl  fx-fog-gate-modul  fx-fog-gate-alpha  fx-fog-gate-no-masked
    RED fx-alpha-texel-lost  fx-alpha-vertex-lost  fx-alpha-vs-lost  fx-alpha-flag-lost  fx-alpha-src-one
    RED fx-alpha-zwrite  fx-alpha-unsorted  fx-alpha-mode-transl
    RED fx-opacity-tex-normal  fx-opacity-slot-mask  fx-opacity-bmasked
    RED fx-layout-fog-offset  fx-layout-attr-count
    RED refuse-unchecked (rc=3)  refuse-pair-blind (rc=3)  stale-blobs (--check)
    BASE HP1_DBG=- / red / tex / lm PASS
    selftest_redtest: all mutations red, base PASS

`fx-layout-attr-count` holds the vertex attribute count of the pipeline (with too few attributes the vertex fog and
alpha arrive as zeros; fixed, held by `fx-layout-attr-count`). Mutation
`actor-lit-gain` targets the light ×0.5 shader line.

### NOT checked / not provable by this scene

- **D3D12 / Windows**: not executed. DXIL is built and signed by DXC 1.9; first step on Windows:
  `OpenHP1.exe --selftest-gpu out.png`.
- **Metal**: not executed here; MSL is generated by SPIRV-Cross from the same HLSL. DXC folds division by a constant into
  multiplication by a rounded reciprocal: `/64` and `127/64` are exact (powers of two).
- **Indistinguishable mutations** (form taken from the source, no proof here): the inner cap `min(col·k, 1)`: with
  `lmCol ≥ 0`, `min(a + min(x,1), 1) = min(a + x, 1)`, removing it changes no value; the `any(fg != 0)` fog condition: a
  zero texel gives the same `lit·1 + 0`.
- **`discard` before `ddx/ddy`**: DXC (vulkan1.0) turns `discard` into OpKill; on a masked surface with spawned light the
  neighbours of a discarded pixel take the `wpos` derivative from a terminated invocation (Metal/D3D: demote). Only
  `|cos|` of a flat surface normal is used; not checked by the scene.
- The sky pass, the depth-only pipeline (#5, sky windows), the `SurfacePan` formula (the scene sets `uvoff` directly),
  movers (atlas #2), procedural textures, HUD output via `SdlHudPass` (HUD formulas are checked separately:
  `tests/HudTests -- --gpu`), the window on Vulkan: not covered by the self-test.
- Game frames on Vulkan: not captured (no game files).
- **ActorFx**: only compositing, fog admission by mode, the AlphaBlend pipeline and texture variants are checked,
  synthetically on lavapipe. NOT checked: Metal and D3D12, game frames, the reflected pass with AlphaBlend, procedural
  textures with Opacity < 1 (no alpha variant). Actor batches are drawn in insertion order (no sort); a mesh Core sorts
  (MeshTriangleOrder: Translucent style or Opacity < 1) is drawn as its own runs in Core's triangle order.
