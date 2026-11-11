// IceTexture — UE1 "ice" procedural texture (Fire.dll UIceTexture) in Core. Input: P8 source, P8 glass
// map (palette indices used as offsets), export parameters; output: the frame's P8 (Mip0, SOURCE
// palette indices) and, on request, RGBA.
//
// Sources (Fire.dll/Engine.dll decompile):
//   * MoveIcePosition — UIceTexture::MoveIcePosition, Fire.dll.c:2636 (constants 120 / 0.0012 / 0.0011 / 0.5 —
//     .rdata 0x10510d04/0d00/0cfc/0cf8); arithmetic per disasm 0x10505b40 (x87, see the method);
//   * ConstantTimeTick — Fire.dll.c:6560: RenderIce(1/120);
//   * Update(Time) — UTexture::Lock :97545 -> UTexture::Update 0x10421070
//     -> UIceTexture::Tick Fire.dll.c:6671: TimeMethod != 0 — MoveIcePosition(dt), else UTexture::Tick;
//   * blit — RenderIce Fire.dll.c:6785 picks by MoveIce: BlitIceTex :3136 / BlitTexIce :2734; texel
//     address wraps with & mask, computed on the CPU;
//   * redraw only when the ROUNDED offsets change (RenderIce +0x104/+0x108).
//
// UTexture::Tick accumulator: the shipped body (Engine.dll.c:70100, disasm 0x104217c0) is shared by all
// UTexture and already ported in WaterTexture.Tick / FireTexture.Tick — sum in double, the sum compared
// before the store to float; derived from disasm and verified byte-for-byte by the water oracle.
// See openhp1/ProcTex/README.md.
//
// Prime: UTexture::Tick calls Prime() (as Water/FireTexture.Prime) — PrimeCount ConstantTimeTick steps
// once; les_spellbackgrnd has PrimeCount = 0. Gate Client && !NoFractalAnim — animation is always on
// (no such ini key in the shipped data).
//
// No RNG is consumed: the ice body never touches the FractalRandom ring.
//
// NOT HERE: reading parameters from the package — UEProceduralTextureData.IceTextures (data input).
//
// LocalSource (+0x114): UIceTexture::PostLoad (disasm 0x10509fca-0x1050a0bb), when the source has a
// different size OR there is no usable glass, writes a resampled source into the ice's OWN mip 0 and
// sets +0x114 = its address; both blits exit immediately when +0x114 != 0
// (BlitTexIce 0x10505ebc-0x10505ec4, BlitIceTex 0x1050624b-0x10506256 -> epilogue). The ice then
// shows a STATIC source: localSource = true, Mip0 = source, no blits.
using System;
using HP1.Core;   // X87Ext — shipped x87 arithmetic (MoveIcePosition)

namespace HP1.UEScript
{
    public sealed class IceTexture
    {
        // ── inputs ──
        public readonly int USize, VSize;
        readonly int _uMask, _vMask;
        readonly byte[] _source;     // SourceTexture mip0, P8, USize × VSize
        readonly byte[] _glass;      // GlassTexture mip0, P8 (index = offset), USize × VSize; null when localSource
        public readonly bool LocalSource;                // +0x114 != 0: shipped blits do not run (see header)

        // ── export parameters ──
        public readonly int PanningStyle, Frequency, Amplitude, HorizPanSpeed, VertPanSpeed;
        public readonly bool MoveIce;
        public readonly int TimeMethod;                  // 0 TIME_FrameRateSync, otherwise TIME_RealTimeScroll

        // ── UTexture: cadence ──
        public float MinFrameRate, MaxFrameRate, Accumulator;   // +0x98, +0x9c, +0xa0
        public byte PrimeCount, PrimeCurrent;                    // +0x94, +0x95
        public double LastUpdateTime;                            // UBitmap.LastUpdateTime, zero at creation

        // ── UIceTexture: state ──
        public float MasterCount, UPosition, VPosition;          // +0xec/+0xf0/+0xf4
        public float UDisplace, VDisplace;                       // +0xf8/+0xfc

        // ── output ──
        public readonly byte[] Mip0;                             // frame P8 (source palette indices)
        public int RoundedU => _ud;                              // RenderIce +0x104
        public int RoundedV => _vd;                              // RenderIce +0x108

        // telemetry
        public int ConstantTicks, RealTicks, DrawnFrames, Updates, Blits;

        int _lastUpdateFrame = -1;     // frame in which Update already ran
        int _ud, _vd;                  // rounded offsets (start at zero)
        int _blitU, _blitV;            // offsets Mip0 was filled with
        bool _blitted;                 // Mip0 has never been filled yet

        public IceTexture(int uSize, int vSize, byte[] sourceP8, byte[] glassP8,
                          int panningStyle, int frequency, int amplitude, int horizPanSpeed, int vertPanSpeed,
                          float uPosition, float vPosition, float masterCount, bool moveIce, int timeMethod,
                          float minFrameRate, float maxFrameRate, byte primeCount = 0, bool localSource = false)
        {
            if (uSize <= 0 || vSize <= 0 || (uSize & (uSize - 1)) != 0 || (vSize & (vSize - 1)) != 0)
                throw new ArgumentException($"IceTexture: dimensions must be powers of two (masks &UMask/&VMask), got {uSize}x{vSize}");
            // Source and glass are sampled with the same UV, so different-sized source/glass behaviour
            // in shipped is unknown. STOP.
            if (sourceP8 == null || sourceP8.Length != uSize * vSize)
                throw new ArgumentException($"IceTexture: source {sourceP8?.Length.ToString() ?? "null"} bytes != {uSize}x{vSize} — a source of a different size is not supported (shipped behavior unknown)");
            if (!localSource && (glassP8 == null || glassP8.Length != uSize * vSize))
                throw new ArgumentException($"IceTexture: glass {glassP8?.Length.ToString() ?? "null"} bytes != {uSize}x{vSize} — glass of a different size is not supported (shipped behavior unknown)");
            // EPanningType: five values 0..4 (MoveIcePosition branches). Any other value cannot occur
            // in the data; the default branch is unproven for it — loud refusal.
            if (panningStyle < 0 || panningStyle > 4)
                throw new ArgumentException($"IceTexture: PanningStyle={panningStyle} outside 0..4 — MoveIcePosition branch unknown");
            USize = uSize; VSize = vSize; _uMask = uSize - 1; _vMask = vSize - 1;
            _source = sourceP8; _glass = glassP8;
            PanningStyle = panningStyle; Frequency = frequency; Amplitude = amplitude;
            HorizPanSpeed = horizPanSpeed; VertPanSpeed = vertPanSpeed;
            MoveIce = moveIce; TimeMethod = timeMethod;
            MinFrameRate = minFrameRate; MaxFrameRate = maxFrameRate; PrimeCount = primeCount;
            // initial state
            MasterCount = masterCount; UPosition = uPosition; VPosition = vPosition;
            UDisplace = UPosition; VDisplace = VPosition;
            Mip0 = new byte[uSize * vSize];
            LocalSource = localSource;
            if (localSource) { Buffer.BlockCopy(sourceP8, 0, Mip0, 0, Mip0.Length); _blitted = true; }
        }

        // UIceTexture::MoveIcePosition(dt) — disasm 0x10505b40-0x10505e46. ARITHMETIC as in shipped x87 at PC24 (round
        // to nearest): the texture ticks from UTexture::Update (Engine 0x10421070, vtable +0x7c), and every caller of
        // Update draws — URender::DrawFrame (Render 0x10b25898), DrawActorSprite via UTexture::Get, UCanvas::DrawTile
        // (Engine 0x103877e8), UTexture::Lock (0x10421551, called by the render device and Fire's Wet/Ice Lock) — inside
        // the PreRender..PostRender window. The one exception is URender::Precache (UGameEngine::Draw 0x1039fb5f,
        // before PreRender): its Update is the first one of the texture, whose dt is the viewport clock since machine
        // start (LastUpdateTime starts at 0) — an input the port does not have either, so every port tick is PC24.
        // The register chain rounds to 24 bits (register exponent), to float only at fstp dword.
        //   MasterCount: fld dt; fmul 120f; fadd [M]; fstp            (0x10505b5b-0x10505b85)
        //   UPosition:   fild s; fmul dt; fadd st,st; fsubr [U]; fstp (0x10505b91-0x10505bb1), s = (char)(b-0x80)
        //   VPosition:   fild s; fmul dt; fadd st,st; fadd [V]; fstp  (0x10505bb4-0x10505bcb)
        //   offset:      fild F; fmul [M]; fmul c; fstp qword -> appSin/appCos -> fstp qword;
        //                fild A; [fmul 0.5f;] fmul qword; fstp dword; fld; fistp (to even);
        //                fild; fadd [U|V]; fstp dword                  (0x10505c14-0x10505e46)
        // A float-per-step form with (float)sin differs from shipped in the low bit on ~30% of steps
        // and in the rounded offset on ~0.7%. Style outside 0..4: shipped does not write UD/VD
        // (0x10505be2 ja -> exit) — rejected by the constructor.
        // Not from the listing: .rdata values 0x10510d04/0d00/0cfc/0cf8 = 120 / 0.0012 / 0.0011 / 0.5;
        // appSin/appCos — bare fsin/fcos (AppSin/AppCos below, UEX87Transcendental).
        public void MoveIcePosition(float dt)
        {
            X87Ext edt = X87Ext.From(dt);
            MasterCount = R(R(edt * X87Ext.From(120f)) + X87Ext.From(MasterCount)).ToFloat();
            X87Ext du = R(X87Ext.From(HorizPanSpeed - 128) * edt), dv = R(X87Ext.From(VertPanSpeed - 128) * edt);
            UPosition = R(X87Ext.From(UPosition) - R(du + du)).ToFloat();
            VPosition = R(R(dv + dv) + X87Ext.From(VPosition)).ToFloat();
            int A = Amplitude + 1, F = Frequency + 1;
            switch (PanningStyle)
            {
                case 1:
                    UDisplace = Shift(A, false, AppSin(Arg(F, 0.0012f)), UPosition);
                    VDisplace = Shift(A, false, AppCos(Arg(F, 0.0012f)), VPosition);
                    break;
                case 2:
                    UDisplace = Shift(A, false, AppSin(Arg(F, 0.0012f)), UPosition);
                    VDisplace = Shift(A, false, AppCos(Arg(F, 0.0011f)), VPosition);
                    break;
                case 3:
                    UDisplace = Shift(A, true, AppSin(Arg(F, 0.0012f)), UPosition);
                    VDisplace = VPosition;
                    break;
                case 4:
                    UDisplace = UPosition;
                    VDisplace = Shift(A, true, AppCos(Arg(F, 0.0012f)), VPosition);
                    break;
                default:   // 0 (others rejected by the constructor)
                    UDisplace = UPosition; VDisplace = VPosition;
                    break;
            }
        }

        static X87Ext R(X87Ext v) => X87Ext.Round24(v);
        // fild F; fmul [M]; fmul c; fstp qword — appSin/appCos argument (double).
        double Arg(int f, float c) => R(R(X87Ext.From(f) * X87Ext.From(MasterCount)) * X87Ext.From(c)).ToDouble();
        // appSin / appCos (bare fsin / fcos, UEX87Transcendental, D-71); the caller stores the result as a qword.
        static double AppSin(double x) => x == 0 ? x : UEX87Transcendental.Fsin(X87Ext.From(x)).ToDouble();
        static double AppCos(double x) => UEX87Transcendental.Fcos(X87Ext.From(x)).ToDouble();
        // fild A; [fmul 0.5f;] fmul qword trig; fstp dword; fld; fistp (to even); fild; fadd [pos]; fstp dword.
        static float Shift(int a, bool half, double trig, float pos)
        {
            X87Ext k = X87Ext.From(a);
            if (half) k = R(k * X87Ext.From(0.5f));
            float s = R(k * X87Ext.From(trig)).ToFloat();
            int n = (int)Math.Round(s, MidpointRounding.ToEven);
            return R(X87Ext.From(n) + X87Ext.From(pos)).ToFloat();
        }

        // UIceTexture::ConstantTimeTick (Fire.dll.c:6560): RenderIce(0.008333334f).
        void ConstantTimeTick()
        {
            MoveIcePosition(1f / 120f);
            ConstantTicks++;
        }

        // UTexture::Prime: PrimeCount steps once (same form as WaterTexture.Prime / FireTexture.Tick).
        void Prime()
        {
            while (PrimeCurrent < PrimeCount) { PrimeCurrent++; ConstantTimeTick(); }
        }

        // UTexture::Tick(dt) (Engine.dll.c:70100, disasm 0x104217c0) — same form as
        // WaterTexture.Tick/FireTexture.Tick in Core.
        void TextureTick(float deltaSeconds)
        {
            Prime();
            if (MaxFrameRate == 0f) { ConstantTimeTick(); return; }
            float max = MaxFrameRate < 0.1f ? 0.1f : MaxFrameRate >= 100f ? 100f : MaxFrameRate;
            float min = MinFrameRate < 0.1f ? 0.1f : MinFrameRate >= 100f ? 100f : MinFrameRate;
            float minTime = (float)(1.0 / max), maxTime = (float)(1.0 / min);
            double sum = (double)deltaSeconds + Accumulator;
            Accumulator = (float)sum;
            if (sum < minTime) return;
            if (sum < maxTime) { ConstantTimeTick(); Accumulator = 0f; return; }
            ConstantTimeTick();
            double rest = (double)Accumulator - maxTime;
            Accumulator = (float)rest;
            if (rest > maxTime) Accumulator = maxTime;
        }

        // ONE DRAWN FRAME: `nowSeconds` — viewport clock (one value per frame, wall time, not level
        // time), `frame` — render frame number. A repeated call in the same frame or with the same
        // Time does not call Update. Then RenderIce: Mip0 is redrawn only when the rounded offsets
        // changed (+0x104/+0x108). Returns true if Mip0 was redrawn ("changed" flag for upload).
        RenderCadence _cadence;

        public bool Update(double nowSeconds, int frame)
        {
            DrawnFrames++;
            if (frame != _lastUpdateFrame)
            {
                _lastUpdateFrame = frame;
                // Time-independent step (TimeMethod=0) — at 60 Hz regardless of draw rate (D-35);
                // a skipped frame leaves LastUpdateTime untouched, so time accumulates.
                if (nowSeconds != LastUpdateTime && (TimeMethod != 0 || _cadence.Due(nowSeconds)))
                {
                    float dt = (float)(nowSeconds - LastUpdateTime);
                    LastUpdateTime = nowSeconds;
                    Updates++;
                    if (TimeMethod != 0) { MoveIcePosition(dt); RealTicks++; }
                    else TextureTick(dt);
                    _ud = (int)Math.Round(UDisplace, MidpointRounding.ToEven);
                    _vd = (int)Math.Round(VDisplace, MidpointRounding.ToEven);
                }
            }
            if (LocalSource) return false;                          // +0x114: blit exits immediately (header)
            if (_blitted && _ud == _blitU && _vd == _blitV) return false;
            Blit(_ud, _vd);
            return true;
        }

        // RenderIce: branch by MoveIce (Fire.dll.c:6785).
        //   MoveIce=False -> BlitIceTex (:3136):  dest[y][x] = Src[(y+VD)&VM][(x+UD+Glass[y][x])&UM]
        //   MoveIce=True  -> BlitTexIce (:2734):  dest[y][x] = Src[y][(x+Glass[(y+VD)&VM][(x+UD)&UM])&UM]
        // (UE rows go top to bottom; +VD is one row lower.)
        void Blit(int ud, int vd)
        {
            int w = USize;
            for (int y = 0; y < VSize; y++)
            {
                int o = y * w;
                if (MoveIce)
                {
                    int gRow = ((y + vd) & _vMask) * w;
                    for (int x = 0; x < w; x++)
                        Mip0[o + x] = _source[o + ((x + _glass[gRow + ((x + ud) & _uMask)]) & _uMask)];
                }
                else
                {
                    int sRow = ((y + vd) & _vMask) * w;
                    for (int x = 0; x < w; x++)
                        Mip0[o + x] = _source[sRow + ((x + ud + _glass[o + x]) & _uMask)];
                }
            }
            _blitU = ud; _blitV = vd; _blitted = true; Blits++;
        }

        // Frame RGBA via the source palette (256 × RGB). Alpha 255: the texture is opaque;
        // blending is decided by the sprite style, not the texture.
        public void WriteRgba(ReadOnlySpan<byte> sourcePaletteRgb, Span<byte> rgba)
        {
            if (sourcePaletteRgb.Length < 768)
                throw new ArgumentException($"IceTexture: source palette {sourcePaletteRgb.Length} bytes < 768");
            if (rgba.Length < Mip0.Length * 4)
                throw new ArgumentException($"IceTexture: RGBA buffer {rgba.Length} < {Mip0.Length * 4}");
            for (int i = 0; i < Mip0.Length; i++)
            {
                int c = Mip0[i] * 3;
                rgba[i * 4] = sourcePaletteRgb[c]; rgba[i * 4 + 1] = sourcePaletteRgb[c + 1];
                rgba[i * 4 + 2] = sourcePaletteRgb[c + 2]; rgba[i * 4 + 3] = 255;
            }
        }
    }
}
