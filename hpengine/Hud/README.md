# Hud: the HPEngine 2D frame (HUD, menu, loading screen, flash, gamma)

The module turns ready Core canvas commands (`WorldFrame.Hud`) and frame screen states into quad batches (`HudQuad`:
screen rectangle, UV window, vertex colour, blend mode, texture path). It does not call the GPU: the backend (SDL_GPU,
`SdlHudPass`) draws the batches. Shader: `shaders/hud.hlsl`; SPIR-V/DXIL/MSL blobs are built and stored next to it.

All Canvas semantics (pen, line wrap, glyph layout, clipping, PolyFlags) live in Core and are not repeated here.

## What the module does

| Code | What |
|---|---|
| `HudBatcher.cs` `Rasterize` | tile: refusal when XL/YL ≤ 0, before the texture; text: Core glyphs, `null` = native font (not drawn), page by index; tint `Color/255`, alpha 1 |
| `HudBatcher.DrawWithFlags` | scale `×Scale`; edges snapped to integer pixels, each separately (`Round` to even); a collapsed quad is not drawn; mode: `PF_Translucent(4)` → additive with tint, else `PF_Modulated(0x40)` → modulation with white + alpha test under `PF_Masked(2)`, else CanvasTile (under `PF_Masked` SrcAlpha/OneMinusSrcAlpha, else opaque); window `[U,U+UL)×[V,V+VL)` as is, in fractions of the logical size |
| `HudBatcher.FillBlack` | black opaque quad over a white texture |
| `HudBatcher.Scale` | `WorldFrame.CanvasScale`: the scale Core built the frame's canvas with (`UEWorldFrameProducer.SetScreen`, `UEConfig.UIScaleFor`) |
| `HudBatcher.ConsoleRegionWidth` | 4:3 menu region (Core input) |
| `HudBatcher.Build` | order: [coronas are not here] → black fill when `!DrawWorld` → HUD → sides `ConsoleRegionOffsetX·Scale` |
| `HudBatcher.Font` | `""`/`"None"` = no font; key is the leaf after the last dot; case-insensitive cache, misses cached |
| `HudBatcher.BuildLevelInfoScreen` | black screen + `Console.DrawLevelInfo` commands |
| `ScreenEffects.ViewFlash/FlashQuad` | `scale = clamp01(W·0.5)`, rgb = clamp01, `alpha = min(2·scale, 1)`; skipped when `!Valid` and on identity (rgb = 0 and alpha ≥ 1 AFTER the clamp); the quad is UNDER the HUD, over world and coronas (`ScreenEffects.Overlay`, UGameEngine::Draw: EndFlash before PostRender) |
| `HudBlend.Flash` + `hud.hlsl` branch 0 | `Blend One SrcAlpha`, output = quad colour |
| `ScreenEffects.GammaExponent/GammaActive/GammaRamp16` | `1/(2.5·B)`, `B ≤ 0` → 1; pass only when `|e−1| ≥ 1e-6` (retail 0.4 → no pass); table `ROUND(pow(i/255,e)·65535)` |
| `hud.hlsl` branch 0 (CanvasTile) | `saturate(t·c)`, alpha `t.a·c.a` |
| `hud.hlsl` branch 1, `HudBlend.Translucent` (HUD PF_Translucent path) | `(t·c, 1)`, `One / OneMinusSrcColor` |
| `hud.hlsl` branch 2, `HudBlend.Modulated` (HUD PF_Modulated path) | `clip(t.a−0.5)` under mask, `DstColor / SrcColor` |
| `hud.hlsl` branch 3 (GammaRamp) | `round(pow(saturate(c), e)·65535)/65535` |

The target is gamma space (UNORM without sRGB, like `classic.hlsl`):
- CanvasTile is `saturate(t_byte·c)` in bytes.
- SpriteModulate: in a gamma target `DstColor/SrcColor` itself yields `2·s·d`, so no extra multiplier (adding one back is
  the red mutation `gpu-mod-linear`).
- No V flip: in SDL_GPU UV (0,0) is the first uploaded row (`SdlRenderer.UploadLevels` uploads UE row 0 first);
  the texel window is unchanged.

## NOT here, and why

- **Coronas**: the sprite subsystem; in frame order they come BEFORE `Build`.
- **Travel hold state machine** (capture of the last frame, black gate until the first Core frame, release on the first
  frame of the new world with a camera): lives in `hpengine/Travel`; only the loading-screen frame itself is here
  (`BuildLevelInfoScreen`).
- **Menu input** (mouse, keys, ESC, wheel): `hpengine/Input`. Only the 4:3 region formula is used here.
- **Texture store** (P8 → RGBA, mask `(0,0,0,0)`, mip choice ≤256, LOD, font pages "white + alpha = max(rgb)"): the
  texture subsystem. The module takes only the logical size.
- **Font metrics** (LineHeight, SpaceWidth, Kerning): not needed by the rasterizer, Core lays out the glyphs.
- **Text without glyphs** (`Glyphs == null`) is a contract violation: Core always provides glyphs — for OS fonts of
  CreateNativeFont (RU edition) from its bundled substitute font (`UENativeFonts`, NOT ORIGINAL); the batcher refuses loudly.
- **D3DDrv branch `0x10000000`** (palette with alpha in entry 128 — `cmp byte [eax+0x203], -1` at 0x100060ef — without
  PF_Translucent → One/InvSrcAlpha): not ported (in shipped data only fonts and `*.Default`/editor textures have it);
  a device decision by palette, the store does not carry the palette.

## Known discrepancies and unknowns (loud)

1. **Flash identity is checked after the clamp**, while Core `ViewFlashState.IsIdentity` checks raw `R,G,B == 0 && W ≥ 1`.
   With a negative FlashFog (`R = −0.1, W = 1`) the quad is skipped here, Core would call it non-identity. The visible
   result is the same (rgb → 0, alpha 1: the screen does not change). Test `flash-identity-after-clamp`, mutation
   `flash-noclamp-rgb`.
2. **Gamma ramp: "high byte" vs rounding.** The DAC may take the high byte of the 16-bit entry (`ramp >> 8`), while the
   shader writes `ramp/65535` into an 8-bit target, i.e. `round(ramp·255/65535)`. These differ (e.g. `ramp = 65280`:
   `>>8` = 255, rounding = 254). Which one the device uses is not established. `GammaRamp16` returns the 16-bit ramp
   itself; the conversion to a byte is unresolved.
3. **Ramp ROUND at a half**: computed with fistp (round to even, see `ScreenEffects`). Tests use only indices whose
   fraction is far from 0.5.
4. **Blending happens in the gamma target**, as D3D7 blended frame bytes.
5. **Additive text with "white" font pages.** Font pages are RGB = 255, A = max(rgb), and the additive path
   (PF_Translucent) does not read alpha. Translucent-style text would add the tint over the WHOLE glyph cell. Whether the
   game uses such text cannot be checked without the game.
6. **HUD texture loading:** the mask must give `(0,0,0,0)` and unmasked index 0 must have alpha 0; otherwise an
   additive masked tile adds the colour of entry 0.
7. **Constants without a disassembly reference in the code are not checked against the DLL here.**

## Tests

```
cd hpengine/tests/HudTests
DOTNET_ROLL_FORWARD=Major dotnet run              # port (CPU)
DOTNET_ROLL_FORWARD=Major dotnet run -- --gpu     # + execution of hud.hlsl on SDL_GPU (lavapipe)
sh hpengine/tests/HudTests/redtest.sh               # from the repo root: red mutations
DXC=/opt/dxc/bin/dxc sh hpengine/Hud/shaders/build.sh --check   # blobs match hud.hlsl
dotnet build hpengine/HPEngine.csproj -c Release     # the module compiles into the engine
```

`dotnet run`: 135 checks on synthetic `HudCommand`/`ViewFlashState`; expectations computed by hand from the rules (see
comments in `Program.cs`), gamma ramps separately in Python:

- scale: 1080p → 2, 720p → 1, 960 → 2, 1440 → 3, 479 and 0 → 1; 4:3 region (including exactly 4:3);
- tile at 1080p and 720p: rectangle after scale and snapping (20.5 → 20, 81.5 → 82; 21.5 → 22, 22.5 → 22, to even),
  UV window top-left, tint, mode;
- adjacent tiles share a boundary (right edge `Round(x+xl)`, not `x0+Round(xl)`);
- refusals: XL ≤ 0 before the texture (counters), a quad collapsed by snapping, missing texture (named);
- modes for PolyFlags `0, 2, 4, 6, 0x40, 0x42, 0x44, 0x1000802`: mode, alpha test, vertex colour;
- order: fill → tile → glyph → tile → sides `[0..240]`, `[1680..1920]` at 1920×1080 with `ConsoleRegionOffsetX = 120`;
  no fill when `DrawWorld`; without a frame only the sides;
- glyphs: page, window, font key `BigFont`, case-insensitive cache, native/empty/`None`/unknown font, page out of range,
  modulated text;
- loading screen: black, then the commands at scale;
- flash: invalid, identity W=1/W=2/after clamp, fade W=0.5 → α 0.5, flash with rgb and α clamp, W<0 → α 0, α cap,
  W=0.8 → α 0.8, full-screen quad;
- gamma: exponent 0.5 → 0.8, 0.2 → 2, ≤0 → 1; 0.4 without a pass; ramp 0.4 = `i·257`; 13 ramp values at B = 0.5/0.2/0.8;
- the table of blend factors and shader branches.

`--gpu`: 25 checks: `HudBatcher`/`FlashQuad` quads are drawn by the SPIR-V blob on an SDL_GPU device (Vulkan lavapipe)
into a 64×64 UNORM target; bytes are compared with the formulas, tolerance 1 byte: CanvasTile with tint, alpha blending
(α 255/0/128), additive, modulated with mask, EndFlash `rgb + d·α`, gamma ramp (branch 3); distinct 2×2 texels and cells
in the top row catch a V flip and the clip Y sign; background outside the quads. No device: `HudGpu REFUSED`, exit 2.

Output:

```
$ dotnet run -- --gpu
HudTests: 135 checks passed, 0 failed
GPU: driver vulkan
HudGpu: 25 checks passed, 0 failed
```

### Red mutations (`redtest.sh`)

Each mutation is a `sed` of ONE line (verified by `diff`: exactly one changed line); the tests must fail, the file is restored byte-exactly (`trap`
on interruption), and at the end the base is green. `hud.hlsl` mutations rebuild the blobs and run `--gpu`; the blobs
are restored and verified by `build.sh --check`. Without dxc/spirv-cross or a device: a `SKIP` line and a list in the
summary, not "PASS".

Output of `sh hpengine/tests/HudTests/redtest.sh` (lavapipe; ~2.5 min):

```
== base
HudTests: 135 checks passed, 0 failed
== mutations
RED    scale-480: FAIL scale-1080: got 1 (+8)
RED    region-ge: FAIL region-exact-4x3: 1440 (+0)
RED    scale-not-applied: FAIL 1080-rect: expected [20,41..82,64], got [10,20..41,32] (+2)
RED    round-away: FAIL 1080-rect: expected [20,41..82,64], got [21,41..82,64] (+0)
RED    right-edge-width: FAIL 1080-rect: expected [20,41..82,64], got [20,41..81,64] (+3)
RED    collapsed-drawn: FAIL degenerate-none-drawn: quads 1 (+0)
RED    xl-precheck: FAIL degenerate-counters: calls=4 tiles=4 hit=3 miss=1 (+0)
RED    v-flip-unity: FAIL 1080-uv: expected uv[0.125,0.125..0.375,0.375], got uv[0.125,0.625..0.375,0.875] (+1)
RED    uv-unscaled: FAIL 1080-uv: expected uv[0.125,0.125..0.375,0.375], got uv[8,0.125..24,0.375] (+1)
RED    translucent-prio: FAIL blend-0x44-mode: expected Translucent, got Modulated (+1)
RED    modulate-tint: FAIL blend-0x40-color: expected (1,1,1), got (0.2,0.4,0.8) (+2)
RED    modulate-noclip: FAIL blend-0x42-clip: expected True, got False (+1)
RED    masked-opaque: FAIL blend-0x2-mode: expected AlphaBlend, got Opaque (+2)
RED    black-when-world: FAIL 1080-count: quads 2 (+15)
RED    bars-unscaled: FAIL order-bar-left: expected [0,0..240,1080], got [0,0..120,1080] (+1)
RED    levelinfo-noblack: FAIL levelinfo-order: Tile (+0)
RED    tint-256: FAIL 1080-r: expected 1, got 0.99609375 (+6)
RED    native-uncounted: FAIL text-native-counted: 0 (+0)
RED    glyph-page0: FAIL glyph-count: quads 3 (+3)
RED    font-key-full: FAIL order-sequence: Fill#-1 Tile#0 Tile#2 Fill#-1 Fill#-1 (+6)
RED    font-cache-case: FAIL font-cache-ci: BigFont,BIGFONT (+1)
RED    flash-noclamp-rgb: FAIL flash-identity-after-clamp (+2)
RED    flash-alpha-1x: FAIL flash-identity-W1 (+6)
RED    flash-scale-W: FAIL fade-draw (+2)
RED    flash-identity-gt: FAIL flash-identity-W1 (+3)
RED    gamma-2.2: FAIL gamma-exp-0.5: expected 0.8, got 0.9090909 (+14)
RED    gamma-active-any: FAIL gamma-retail-0.4-inactive (+1)
RED    gamma-truncate: FAIL gamma-ramp-B0.2-i200: expected 40314, got 40313 (+2)
RED    factors-add-oneone: FAIL factors-Translucent: expected One/OneMinusSrcColor, got One/One (+0)
RED    shadermode-add: FAIL shadermode-Translucent: expected 1, got 0 (+0)
RED    shader-stale: build.sh --check -> STALE
== hud.hlsl mutations (GPU execution; base: HudGpu: 25 checks passed, 0 failed)
RED    gpu-v-flip: FAIL cell-T.Opaque-texel00: (4,4) expected (100,100,10), got (128,255,51) (+19)
RED    gpu-y-clip: FAIL cell-T.Opaque-texel00: (4,4) expected (100,100,10), got (60,120,200) (+15)
RED    gpu-canvas-notint: FAIL cell-T.Opaque-texel00: (4,4) expected (100,100,10), got (200,100,50) (+8)
RED    gpu-add-notint: FAIL cell-T.Add-texel00: (36,4) expected (136,133,243), got (136,146,243) (+2)
RED    gpu-mod-linear: FAIL cell-T.Mod-texel00: (52,4) expected (60,120,201), got (120,240,255) (+2)
RED    gpu-mod-noclip: FAIL cell-T.Mod-texel10: (60,4) expected (60,120,200), got (94,48,156) (+0)
RED    gpu-gamma-invexp: FAIL gamma-texel00: (16,16) expected (0,147,255), got (0,108,255) (+3)
== base after restore
HudTests: 135 checks passed, 0 failed
REDTEST PASS: 38 mutations red, base green
```

## Status

Checked by local subsystem tests (synthetic + shader execution), not the project gate. `ACTIVATED` is not reached:
a real Core frame with HUD (VM, game) has not been executed in these tests.

| System | Status | Owner | Engine (SDL) | Activation | Oracle | Dependencies |
|---|---|---|---|---|---|---|
| `HudBatcher.Build` (HUD/menu/sides) | PORTED | frontend presentation (commands from Core) | wired (`HPEngineProgram`: `Hud.Build` -> `ScreenEffects.Overlay`) | synthetic `HudCommand` only | none independent: expectations by hand from the rules | texture loader, font pages, `menuActive` (Input), coronas (sprites) |
| `HudBatcher.BuildLevelInfoScreen` | PORTED | frontend | wired (via `TravelFrameHold.ScreenQuads`) | synthetic | none | — |
| `ScreenEffects` flash | PORTED | frontend (state in Core) | wired (`ScreenEffects.FlashQuad`) | synthetic `ViewFlashState` | none | — |
| `ScreenEffects` gamma | PORTED; ramp-to-byte conversion NOT resolved (discrepancy 2) | frontend | wired (`SdlRenderer.GammaExponent`) | synthetic | none | `UEConfig.Brightness` |
| `hud.hlsl` + blobs | PORTED; SPIR-V executed on lavapipe (25 checks); DXIL/MSL only built | frontend | wired (`HPEngine.csproj`: hud.* blobs embedded) | `--gpu` (SPIR-V) | formulas in the test | — |

No row is CLOSED: no activation by a Core frame, and constants not checked against the decompile (item 7).
