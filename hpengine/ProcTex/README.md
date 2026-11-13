# ProcTex: procedural textures (ice in Core, fire and water in the engine)

Checked with synthetic data only (the tests need no game files or decompiles). Wired into the frame by
`hpengine/SdlProcTex.cs`.

## Files

| file | what |
|---|---|
| `HP1.Core/Runtime/VM/IceTexture.cs` | Core: UE1 ice as a plain class |
| `hpengine/ProcTex/ProcTextures.cs` | engine fire/water manager over Core `FireTexture`/`WaterTexture` |
| `hpengine/tests/ProcTexTests/` | console tests (`Program.cs`) and red mutations (`redtest.sh`) |

## Contents

### Ice: `IceTexture.cs`

| function | code | note |
|---|---|---|
| MoveIcePosition (Fire.dll.c:2636) | `IceTexture.MoveIcePosition` | x87 form, see item 13 |
| ConstantTimeTick (Fire.dll.c:6560, RenderIce(1/120)) | `ConstantTimeTick` | |
| UTexture::Tick (Engine.dll.c:70098) | `TextureTick` + `Prime` | **Core form**, see "Discrepancies" item 1 |
| Update(Time): frame/Time == LastUpdateTime, TimeMethod branch, rounding of shifts | `Update(nowSeconds, frame)` | returns a redraw flag |
| UObject state (MasterCount/UPos/VPos/UD/VD/Accumulator/LastUpdateTime=0) | `IceTexture` fields | |
| BlitIceTex (Fire.dll.c:3136) / BlitTexIce (:2734) by MoveIce | `Blit` | texel address with `& UMask/VMask` (repeat) |
| RenderIce redraws only when ROUND(UD)/ROUND(VD) change (+0x104/+0x108) | `Update` -> `Blit` | |
| alpha 1.0 | `WriteRgba` (alpha 255) | RGB = source palette[index] |

### Fire: `ProcTextures.cs`

| function | code |
|---|---|
| palette -> RGBA, alpha = max(r,g,b) | `Fire(...)` (builds `PaletteRgba`) |
| bank: full path -> record, leaf -> candidates | `ProcTextures` constructor |
| SameContent | `SameContent` |
| Resolve: full path, else an unambiguous leaf, else STOP | `ResolveFire` |
| GetTexture: Prime + first `Tick(0)`, `_lastTime = now`, used = frame | `Fire(refName, frame, now)` |
| MarkUsed | `MarkFireUsed` |
| TickAll: once per frame; only textures drawn this/previous frame; dt from their last tick | `TickAll` / `TickOne` |
| Step (`FireTexture.Tick(dt, FractalRandom.Shared)`) / Upload (palette) | `Step` / `Upload` |

### Water: `ProcTextures.cs`

| function | code |
|---|---|
| bank (case-insensitive; leaf indexes only records with a source) | constructor |
| IsCarrier, CarrierByLeaf (exactly one gives the path, else STOP), Size | `IsWetCarrier`, `WetCarrierByLeaf`, `WetSize` |
| GetTexture: STOP without a source; WaterTexture from the record, drops, PostLoad; STOP on an oversized source; STOP on an unseeded ring; palette with alpha 0 at index 0; first `Tick(0)` | `Wet(uePath, frame, now)` |
| MarkUsed, TickAll | `MarkWetUsed`, `TickAll` |
| Journal | `Journal` (+ fire count) |
| live texture substituted by material path, MarkUsed at draw time | `Wet(...)` at frame build + `MarkWetUsed` |

Fire/water simulation is Core's (`FireTexture`, `WaterTexture`), data is `UEProceduralTextureData.Fire/Wet` (Core), the
ring is `FractalRandom.Shared` by default. RNG: the ring is separate Fire.dll state, seeded once; fire/water steps do
not consume the game `rand()` stream (the stream owner is unchanged). Ice consumes no RNG at all.

## NOT here, and why

1. **Ice parameters from the package** are loaded by `UEProceduralTextureData.IceTextures` (Core); ice is wired in
   `hpengine/SdlProcTex.cs`. Only the question of tag defaults remains open ("Discrepancies" item 2).
2. **Ice state registry by full path**: one instance per UObject (`SdlProcTex`).
3. **Ice sprite blending** (Modulate `Blend DstColor SrcColor`; Translucent `One OneMinusSrcColor`) is sprite style, the
   sprite renderer's job.
4. **`Client && !NoFractalAnim` gate**: animation is always on, as in Core (`WaterTexture.ConstantTimeTick`); the shipped
   ini files have no such key.
5. **Ring seeding** is not done in this directory: `HPEngineProgram` seeds `FractalRandom.Shared` once from its own
   presentation stream (render random numbers); the manager writes a loud STOP if it is unseeded.

## Discrepancies and unknowns (loud)

1. **UTexture::Tick accumulator.** Core carries the original body (`UTexture::Tick`, Engine.dll.c:70100, disassembly
   `0x104217c0`) in `WaterTexture.Tick`/`FireTexture.Tick`: the sum in double, comparison of the sum BEFORE storing to
   float, minimum `(float)(1.0 / max)`; the Core form is verified byte-exactly by the water oracle. The original has one
   body for all UTextures, so ice takes the Core form. A float accumulator (`Accumulator += dt; if (minTime <=
   Accumulator)`, minimum `1f / maxFr`) differs only at a rounding boundary: input `MaxFrameRate=60`, dt1 = `0.01f`, dt2 =
   `0x3bda740f`: the float form steps, the double form does not (test `ice-acc-forms-distinguishable` proves the
   difference is visible, `ice-acc-core-form` and mutation I11 hold the choice).
2. **Pan speed defaults.** `UIceTexture::Init` (0x1050a160) is called only when a texture is CREATED, not on the load
   path; the `UIceTexture` constructor (0x10509eb0) does not write HorizPanSpeed/VertPanSpeed/Frequency/Amplitude/MoveIce.
   So in the original a missing tag = the class value (zero by measurement), while the Core loader substitutes the Init
   values. Whether this matches on campaign data is checked locally (`HP1_PROCDBG=1`: "[Ice] class …", "no tag …").
3. **First tick of fire/water vs ice.** Fire/water call `Tick(0)` at creation and set `last = now`; ice follows the
   original: `LastUpdateTime = 0`, first delta = `CurrentTime`. With `MaxFrameRate > 0` this gives a different first
   frame (fire: Prime without a step; ice: a step). Which is right for UFireTexture/UWetTexture is unknown without the
   decompile of `UTexture::Lock/Update` for them. All campaign water carriers have `MaxFrameRate = 0`, so water is
   unaffected.
4. **No double tick in the creation frame.** The original `UTexture::Lock` rule applies: Update only when
   `Time != LastUpdateTime`, one tick per frame (`Entry.TickedFrame`, time check).
5. **Fire -> water order in `TickAll`.** In the original the order is the `Lock` order at draw time. It decides which
   ring bytes each texture gets (look only, not gameplay). Unknown; fire -> water is chosen, creation order within.
6. **RGBA rows** are in UE order (top to bottom) for both, as `SdlRenderer.LoadTexture`.
7. **A short fire palette -> STOP**, not a grey ramp: the Core record always carries 768 bytes, a ramp would be a silent
   substitute. Likewise a short water source palette -> STOP. A `FireTexture` constructor refusal (sizes not powers of
   two) -> STOP instead of an exception in the frame. More sparks than the limit is not a refusal: the
   `UFireTexture::PostLoad` branch is ported (disassembly 0x10508089-0x1050812b): the tag limit stays unclamped if the
   sparks do not exceed it; otherwise clamp to [4, 0x2000] and drop sparks of type >= 0x1f from the end (test
   `hpengine/tests/WaveA6Tests`).
8. **An unseeded ring** is flagged for fire too; the texture is still created. Loudness only, same behaviour.
9. **First ice blit.** Before the first Update the rounded shifts are (0,0); the first `Update` ALWAYS fills `Mip0`
   (otherwise the frame would be empty). If the first Update comes at `now == 0` (Time == LastUpdateTime), the blit uses
   shift (0,0). Earlier note, superseded by the next paragraph:
   The initial value of `+0x104/+0x108` in shipped is unknown.
   The initial value is known: constructor 0x10509eb0 sets +0x104/+0x108 = −1 and +0x118 = 1, so the original's first
   RenderIce always draws; the port draws on the first Update, the same. One difference remains: at `now == 0` the port
   draws with shift (0,0), while the original keeps the package mip until the first Tick.
10. **Ice sizes.** The constructor requires source and glass sizes = ice size (powers of two), otherwise a named
    exception. `PanningStyle` outside 0..4 is an exception (EPanningType has five values). Original behaviour per the
    `UIceTexture::PostLoad` disassembly (0x10509fca-0x1050a0bb): a smaller source or no usable glass -> the source is
    stretched into the ice mip, +0x114 != 0, and both blits return at once (0x10505ec4, 0x10506256): the ice is STILL.
    Ported: `IceTexture(localSource)`, `UEProceduralTextureData.IceLocalSource`, `SdlProcTex`. The constructor refusal
    without glass remains for `localSource = false`.
11. **Tie rule inside MoveIcePosition** (round to even): mutation I07 proves rounding exists, but the tests build no
    input where `A·sin(...)` lands exactly on .5, so the tie rule there is unproven. The tie rule of the FINAL shift
    (round to even) is proven (I08/I09).
12. **Clock**: the caller's `nowSeconds` (double); dt = `(float)(now - last)`.
13. **MoveIcePosition arithmetic.** The original body (disassembly 0x10505b40-0x10505e46) keeps chains in an x87
    register (PC64) and rounds to float only on `fstp`; the sine argument is double. A per-step float computation with
    `(float)sin` differs in bits in 289 of 400 random cases, in the rounded shift (in the frame) in 36 of 400.
    Implemented via `X87Ext`; the oracle on listing bytes (`se_harness/wave_a6_ice/ice_oracle.py`) gives 0 differences.
    The formula tests (`ice-move-style*`) still pass: their inputs do not touch the rounding boundary; mutations I01-I07
    target the x87 form.

## Tests

    cd hpengine/tests/ProcTexTests
    DOTNET_ROLL_FORWARD=Major dotnet run -c Release          # 0 = success, 1 = with names of failures
    sh hpengine/tests/ProcTexTests/redtest.sh                 # from the repo root: red mutations + base

Expected values are computed INDEPENDENTLY in the test: ice formulas rewritten from scratch, blit texel address via
`((v % n) + n) % n` instead of masks; the accumulator in both forms (float and Core double); fire/water by a separate
Core `FireTexture`/`WaterTexture` instance on a copy of the ring, run directly by the test, the palette rule by its own
lines; cadence step counts both by formula and by hand (30 of 60, 20 of 60, one step per frame).

Output (`dotnet run`):

```
checks=78 failed=0
PROCTEX TESTS PASS
```

`redtest.sh` (≈30 s per mutation): 50 mutations, each exactly one line of `IceTexture.cs` (I01-I25) or
`ProcTextures.cs` (M01-M25); the script names the expected check and prints all failed checks. Result: all 50 red,
`BASE PASS: checks=78 failed=0`, `REDTEST PASS`, rc=0.

## Status

Checked by local subsystem tests (targeted level): `CLOSED` is not allowed.

| Native/system | Status | Owner | Activated | Oracle | Dependencies |
|---|---|---|---|---|---|
| UIceTexture (MoveIcePosition, Tick, RenderIce/Blit*): `IceTexture` | PORTED (MoveIcePosition proven by the x87 oracle) | Core | no: tests call the class directly, not through the frame | MoveIcePosition: `ice_oracle.py`; the rest checked against formulas | tag defaults (item 2) |
| FireTexture in the engine: `ProcTextures.Fire` | PORTED | engine (simulation is Core's, proven by `se_harness/bsp_diff/validate_fire_texture.py`) | no | simulation: Core oracle; boundary: synthetic against an independent Core instance | items 3, 5 |
| WetTexture in the engine: `ProcTextures.Wet` | PORTED | engine (simulation is Core's, `se_harness/bsp_diff/validate_wet_texture.py`) | no | as above | mover leaf-to-path resolution, item 5 |

No row is `ACTIVATED`: no tested live pass through the engine frame, no game files in the tests.

## Not checkable without the game

* the look of the ice `les_spellbackgrnd` (spell board) and the halos `Bludger_Halo/Snitch_Halo` in the frame;
* fire/water RGBA against original frames on real campaign records (175 fire, 25 water);
* ice oscillation speed at the real frame rate (TIME_FrameRateSync is tied to the frame count);
* behaviour with real ring seeding.
