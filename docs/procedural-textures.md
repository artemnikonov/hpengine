# Procedural textures (Fire.dll)

Torch flames on walls, water surfaces and the spell-book background are not stored as pixels. The package holds parameters, and `Fire.dll` runs a small simulation each time the texture is drawn: `UFireTexture` (rising or plain fire with sparks), `UWetTexture` (a water refraction table driven by drops over a source texture) and `UIceTexture` (a panning glass over a source). The kernels are self-modifying assembly that a decompiler cannot read, so the port derives closed forms from probing the real code and compares them byte for byte with the original's machine code run in an emulator. The point to take away: these textures advance in steps tied to drawing, not to game time.

## Stepping: `UTexture::Tick`

- One body, `UTexture::Tick` (Engine `0x104217c0`), serves every texture class. It first runs the prime steps (`PrimeCount`). With `MaxFrameRate == 0` it makes one step per call, with no time involved. Otherwise `Min` and `Max` frame rates are clamped to [0.1, 100], the delta is added to an accumulator, a step happens when the sum reaches `1/Max`, and the accumulator is reset (below `1/Min`) or reduced by `1/Min` and capped. The sum is kept in double and compared before it is stored to float; storing first flips a step on some inputs. (port: `Tick` in [`FireTexture.cs`](../HP1.Core/Runtime/VM/FireTexture.cs), [`WaterTexture.cs`](../HP1.Core/Runtime/VM/WaterTexture.cs); texture animation chains in [`UETextureAnim.cs`](../HP1.Core/Runtime/World/UETextureAnim.cs))
- The delta is the wall clock, not level time. `UTexture::Update` (Engine `0x10421070`) takes `dt = Time - LastUpdateTime`, with `Time` the viewport's clock; it is called by `UTexture::Lock` (`0x10421720`) only when `Time != LastUpdateTime`. So a texture's delta is the time since its own last draw, and neither pause nor `TimeDilation` affects it. `LastUpdateTime` starts at 0 while `Time` is seconds since machine start, so the first delta is huge and the accumulator clips it.
- Water carriers in the campaign all have `MaxFrameRate = 0`: one step per drawn frame.

## 60 Hz step (port difference)

In the original the step rate follows the frame rate (one step per drawn frame). The port draws at the monitor rate, so steps are metered by wall clock to 60 per second, with a quarter-period tolerance for jitter; a longer lag is not caught up with a queue of steps. **Not original (author decision D-35):** it keeps fire and water looking as at the original's usual 60 fps on any monitor. (port: [`RenderCadence.cs`](../HP1.Core/Runtime/VM/RenderCadence.cs), used by `UETextureAnim.CurrentFrame` and by [`hpengine/ProcTex/ProcTextures.cs`](../hpengine/ProcTex/ProcTextures.cs).) Only textures drawn this or the previous frame are stepped.

## Fire (`UFireTexture`)

- `ConstantTimeTick` is: `RedrawSparks` (Fire.dll `0x105025e0`, 44 spark types; `DrawFlashRamp` `0x10502320`), the fire kernel, then `PostDrawSparks`. A spark is 8 bytes: type, heat, X, Y, 4 parameter bytes. Bodies are ported as plain code and were compared with the original's code for all 44 spark types and the campaign's carriers.
- Fire kernel (four shipped variants in `.data`, `0x10515780`, `0x105159a4` and two Pentium Pro versions): rising fire reads the row below plus one more (`out[y][x] = T[p[y+1][x-1] + p[y+1][x] + p[y+1][x+1] + p[y+2][x]]`); the non-rising kind reads the same row. Rows and columns wrap; the original field is read. Checked on 48 random fields across all four variants.
- `RenderTable` (built in `PostLoad`, `0x10507ffa..0x1050807b`): `T[i] = trunc(clamp(i*0.25 + 1.0 - (255 - RenderHeat)*0.0625, 0, 255))`.
- `PostLoad` clamps the spark array only when it exceeds its limit (`0x10508089..0x1050812b`): then to [4, 0x2000], dropping sparks of type 0x1f and above from the end.
- Sparks sit right after the mip array in the package (`UFireTexture::Serialize` calls `UTexture::Serialize` first); data loader: [`UEProceduralTextureData.cs`](../HP1.Core/Runtime/World/UEProceduralTextureData.cs).

## Water (`UWaterTexture` / `UWetTexture`)

- State: `Drops[256]` (type, depth, X, Y, 4 bytes), source fields (two half-width fields interleaved by rows), `RenderTable[0x404]`, `WaterTable[0x600]`, `WaterParity`. `Init` defaults: DropType 1, WaveAmp 128, FX_Frequency 8, FX_Phase 8, FX_Amplitude 255, FX_Speed 255, FX_Radius 128, FX_Size 30, FX_Depth 255, FX_Time 30. (port: [`WaterTexture.cs`](../HP1.Core/Runtime/VM/WaterTexture.cs))
- Kernel `CalculateWater` (Fire.dll `0x10516550` for even parity, `0x10516dc0` for odd): the field of rows with the current parity is updated from the other parity's rows and its own old value through `WaterTable`; the displayed mip is built from the field that is not updated, using differences of neighbours looked up in `RenderTable`. Checked on random fields at sizes 8 to 256, both parities, plus a full WetTexture tick. (port: [`WaterKernel.cs`](../HP1.Core/Runtime/VM/WaterKernel.cs))
- The sine table (`0x1051731c`) is `fild i; * 1/256 * 2pi; appSin; * 127.5 + 127.45` truncated; the margin to a rounding boundary is large enough that any correct sine gives the same bytes.
- Open: for 14 of 25 wet carriers the source is package `genfluid.utx`, which the game does not ship; those carriers are unused in the campaign and the port refuses them loudly.

## Random byte ring (`FractalRandom`)

All fractal textures share one ring of 0x200 bytes (Fire.dll `0x10518450`, index at `0x10518650`), seeded once per process by the first `UFractalTexture` constructor (`0x10501050`) with 0x200 values of `(BYTE)appRand()`, before any gameplay `FRand`. A step reads `ring32[(idx + 0x80) & 0xfc]`, advances `idx = (idx + 4) & 0xfc` and xors the value into the new slot. Because fire and water draw from the same ring, the order in which textures are stepped changes their bytes (look only, not gameplay). The port seeds its ring once from the render random stream, so ring contents differ from a given original run; ice never touches the ring. (port: [`FractalRandom.cs`](../HP1.Core/Runtime/VM/FractalRandom.cs).) Open: the original's order is the order of `Lock` calls at draw time; the port steps fire before water, in creation order (`hpengine/ProcTex/README.md`, discrepancy 5).

## Ice (`UIceTexture`)

- `ConstantTimeTick` is `RenderIce(1/120)`. With `TimeMethod != 0`, `Update` calls `MoveIcePosition` with the delta instead of `UTexture::Tick`. `MoveIcePosition` (Fire.dll `0x10505b40`, constants at `0x10510d04`, `0d00`, `0cfc`, `0cf8`) keeps its chains in an x87 register at 64-bit precision and rounds to float only on store; a float-per-step version differs in bits in 289 of 400 random cases. The texture is redrawn only when the rounded offsets change. (port: [`IceTexture.cs`](../HP1.Core/Runtime/VM/IceTexture.cs))
- When the source has a different size or there is no usable glass map, `PostLoad` (`0x10509fca..0x1050a0bb`) writes a resampled source into the ice's own mip and both blits exit at once: the ice is static.
- Open: `UIceTexture::Init` (`0x1050a160`) runs only on creation, not on load, so a missing tag in the original means the class value (zero by measurement) while the port loader substitutes the `Init` values; whether this matters on campaign data is checked only locally.

## Surface panning

Not a procedural texture but driven by the level clock like one: for each drawn BSP surface, `URender::DrawFrame` (Render.dll `0x10b258c4..0x10b25b56`) adds an automatic U or V pan from `Zone.TexU/VPanSpeed * Level.TimeSeconds * 8960` (rounded, masked to `0x3ffff`, scaled by 1/256) and, with the small-wavy flag, `8*sin(t) + 4*cos(2.3*t)` terms. The code runs inside the renderer's 24-bit precision window, so every multiply rounds to float; sine and cosine are correctly rounded (see [x87-precision.md](x87-precision.md)). The campaign has no small-wavy carriers. Bit `0x1000` is `PF_SpecialPoly` in this game, read only by `APawn::Mount`, and plays no part in the pan. (port: [`SurfacePan.cs`](../HP1.Core/Runtime/VM/SurfacePan.cs))
