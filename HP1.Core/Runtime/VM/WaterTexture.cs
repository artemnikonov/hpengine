using System;

namespace HP1.UEScript
{
    // UWaterTexture / UWetTexture (Fire.dll, UnFractal.cpp) — water simulation
    // over a source texture. 1:1 port of the Fire.dll.c decompile; oracle:
    // se_harness/bsp_diff/wet_oracle.py (real bodies under unicorn), compared
    // byte-for-byte on every tick (validate_wet_texture.py).
    //
    // State — UWaterTexture fields at the decompile offsets: +0x30 UBits,
    // +0x31 VBits, +0x34 USize, +0x38 VSize, +0xc0 UMask, +0xc4 VMask,
    // +0xd0 GlobalPhase, +0xd4 DrawPhase, +0xd8.. DropType, WaveAmp,
    // FX_Frequency, FX_Phase, FX_Amplitude, FX_Speed, FX_Radius, FX_Size,
    // FX_Depth, FX_Time, +0xe4 NumDrops, +0xe8 Drops[256]{Type,Depth,X,Y,
    // ByteA..ByteD}, +0x8e8 SourceFields (USize/2 × VSize: two fields in interleaved
    // rows), +0x8ec RenderTable[0x404], +0xcf0 WaterTable[0x600],
    // +0x12f0 WaterParity, +0x12f4 OldWaveAmp; UWetTexture: +0x12f8 SourceTexture,
    // +0x1300 LocalSourceBitmap. Names and layout from the shipped script System/Fire.u (FractalTexture/WaterTexture/WetTexture)
    // (WaterTexture: DropType..OldWaveAmp; WetTexture: SourceTexture,
    // OldSourceTex, LocalSourceBitmap); Init defaults — Fire.dll.c:4936-4947.
    public sealed class WaterTexture
    {
        // ── UTexture (Engine): animation ──
        public float MinFrameRate, MaxFrameRate, Accumulator;   // +0x98, +0x9c, +0xa0
        public byte PrimeCount, PrimeCurrent;                    // +0x94, +0x95
        public int USize, VSize; public byte UBits, VBits;
        public byte[] Mip0;                                      // Mips[0].DataPtr — output (source palette indices)

        // ── UFractalTexture ──
        public int UMask, VMask, GlobalPhase; public byte DrawPhase, AuxPhase;

        // ── UWaterTexture ──
        public byte DropType = 1, WaveAmp = 0x80, FX_Frequency = 8, FX_Phase = 8, FX_Amplitude = 0xff,
                    FX_Speed = 0xff, FX_Radius = 0x80, FX_Size = 0x1e, FX_Depth = 0xff, FX_Time = 0x1e;
        public int NumDrops;
        public readonly byte[] Drops = new byte[256 * 8];
        public byte[] SourceFields;
        public readonly byte[] RenderTable = new byte[0x404];
        public readonly byte[] WaterTable = new byte[0x600];
        public byte WaterParity; public int OldWaveAmp = -1;

        // ── UWetTexture ──
        public byte[] SourceMip0; public byte SourceUBits, SourceVBits;   // SourceTexture (null — no source)
        public byte[] LocalSourceBitmap;                                   // resampled copy when sizes differ

        // Sine table DAT_1051731c (FUN_10501050, Fire.dll.c:24-33):
        // fild i; fmul 1/256f; fmul 2πf; fstp qword; appSin; fmul 127.5; fadd 127.45;
        // fstp dword; fistp. Computed the same way; the margin to rounding boundaries is ≥ 1.2e-4
        // (measured in the oracle), so any correct sin gives the same bytes.
        public static readonly byte[] Sine = BuildSine();
        static byte[] BuildSine()
        {
            var t = new byte[256];
            const float c1 = 1f / 256f, c2 = 6.2831854820251465f;
            for (int i = 0; i < 256; i++)
            {
                double arg = (double)((float)i * c1) * c2;          // x87 extended, exact for these operands
                float v = (float)(HP1.Core.UEX87Transcendental.Fsin(HP1.Core.X87Ext.From(arg)).ToDouble() * 127.5 + 127.45);   // appSin: fsin (D-71)
                t[i] = unchecked((byte)(int)Math.Round(v, MidpointRounding.ToEven));
            }
            return t;
        }

        public WaterTexture(int uSize, int vSize)
        {
            USize = uSize; VSize = vSize;
            UBits = (byte)Log2(uSize); VBits = (byte)Log2(vSize);
            Mip0 = new byte[uSize * vSize];
            // UWaterTexture constructor (Fire.dll.c:4874-4887): WaterTable
            for (int i = 0; i < 0x600; i++)
            {
                int v = i / 2 - 0x100 + (i < 0x300 ? 1 : 0);
                WaterTable[i] = (byte)(v < 0 ? 0 : v > 0xfe ? 0xff : v);
            }
        }
        static int Log2(int v) { int b = 0; while ((1 << b) < v) b++; if ((1 << b) != v) throw new ArgumentException("size is not a power of two: " + v); return b; }

        // UWaterTexture::PostLoad (Fire.dll.c:5015-5027) + UWetTexture::PostLoad (:5910-5990):
        // masks, 0x80 fields, local copy of the source, refraction table.
        public void PostLoad()
        {
            UMask = USize - 1; VMask = VSize - 1;
            if (SourceFields == null)
            {
                SourceFields = new byte[VSize * USize / 2];
                Array.Fill(SourceFields, (byte)0x80);
            }
            if (SourceMip0 != null)
            {
                if (UBits == SourceUBits && VBits == SourceVBits) LocalSourceBitmap = null;
                else
                {
                    int du = UBits - SourceUBits, dv = VBits - SourceVBits;
                    if (du < 0 || dv < 0) SourceMip0 = null;                    // source is larger — no water
                    else
                    {
                        LocalSourceBitmap = new byte[VSize * USize];
                        for (int y = 0; y < VSize; y++)
                            for (int x = 0; x < USize; x++)
                                LocalSourceBitmap[USize * y + x] = SourceMip0[(x >> du) + ((y >> dv) << SourceUBits)];
                    }
                }
                if (WaveAmp != OldWaveAmp) SetRefractionTable();
                OldWaveAmp = WaveAmp;
            }
        }

        // UWetTexture::SetRefractionTable (disasm 0x10509d10): k = −511..512,
        // r = ftol((k) · (WaveAmp · 1/512)) clamped to [−128, 127].
        void SetRefractionTable()
        {
            for (int i = 0; i < 0x400; i++)
            {
                int v = (int)((double)(i - 511) * ((double)WaveAmp * 0.001953125f));   // ftol: truncation toward zero
                RenderTable[i] = unchecked((byte)(v < -0x80 ? -0x80 : v > 0x7e ? 0x7f : v));
            }
        }

        // UTexture::Tick (Engine.dll.c:70100, disasm 0x104217c0): Prime, then a step driven by
        // the accumulator; MaxFrameRate==0 — a step every tick.
        public void Tick(float deltaSeconds, FractalRandom rng)
        {
            Prime(rng);
            if (MaxFrameRate == 0f) { ConstantTimeTick(rng); return; }
            float max = MaxFrameRate < 0.1f ? 0.1f : MaxFrameRate >= 100f ? 100f : MaxFrameRate;
            float min = MinFrameRate < 0.1f ? 0.1f : MinFrameRate >= 100f ? 100f : MinFrameRate;
            float minTime = (float)(1.0 / max), maxTime = (float)(1.0 / min);
            double sum = (double)deltaSeconds + Accumulator;
            Accumulator = (float)sum;
            if (sum < minTime) return;
            if (sum < maxTime) { ConstantTimeTick(rng); Accumulator = 0f; return; }
            ConstantTimeTick(rng);
            double rest = (double)Accumulator - maxTime;
            Accumulator = (float)rest;
            if (rest > maxTime) Accumulator = maxTime;
        }

        // UFractalTexture::Prime (Fire.dll.c:4031-4053) with NoFractalAnim=0 →
        // UTexture::Prime (Engine.dll.c): catches up PrimeCount steps.
        void Prime(FractalRandom rng)
        {
            while (PrimeCurrent < PrimeCount) { PrimeCurrent++; ConstantTimeTick(rng); }
        }

        // UWetTexture::ConstantTimeTick (Fire.dll.c:6085-6173). Condition Client &&
        // !NoFractalAnim — in the port animation is always on (client ini flag).
        public bool ConstantTimeTick(FractalRandom rng)
        {
            if (SourceMip0 == null || USize <= 7 || VSize <= 7) return false;
            WaterRedrawDrops(rng);
            CalculateWater();
            ApplyWetTexture();
            return true;
        }

        // UWaterTexture::CalculateWater (Fire.dll.c:2552-2568): parity++, kernel.
        void CalculateWater()
        {
            WaterParity = unchecked((byte)(WaterParity + 1));
            WaterKernel.Calculate(Mip0, SourceFields, RenderTable, WaterTable, USize, VSize, WaterParity);
        }

        // UWetTexture::ApplyWetTexture (Fire.dll.c:2576-2620): row by row
        // dst[x] = src[(disp[x] + x) & (USize−1)], where disp is the displacement map
        // written by the kernel into the same mip. In shipped each cell's displacement is
        // read before that cell is overwritten and no cell is read after being written,
        // so copying the row is equivalent (the oracle compares byte-for-byte).
        // The x counter is a BYTE (bVar7), as in shipped: widths > 256 are unsupported.
        readonly byte[] _row = new byte[512];
        void ApplyWetTexture()
        {
            byte[] src = LocalSourceBitmap ?? SourceMip0;
            int w = USize; byte mask = unchecked((byte)(w - 1));
            for (int y = 0; y < VSize; y++)
            {
                int row = y << UBits;
                Array.Copy(Mip0, row, _row, 0, w);
                uint x = 0;
                do
                {
                    Mip0[row + x] = src[row + ((_row[x] + x) & mask)];
                    Mip0[row + x + 1] = src[row + ((_row[x + 1] + x + 1) & mask)];
                    x = (byte)(x + 2);
                } while (x < (uint)(w - 2));
                Mip0[row + x] = src[row + ((_row[x] + x) & mask)];
                Mip0[row + x + 1] = src[row + ((_row[x + 1] + x + 1) & mask)];
            }
        }

        // UWaterTexture::WaterRedrawDrops (Fire.dll.c:3237-3574): each drop writes
        // into BOTH fields (offsets 0 and USize/2 of the row pair). Types — EDropType
        // (Fire.u enum WDrop: FixedDepth=0, PhaseSpot, ShallowSpot, HalfAmpl, RandomMover,
        // FixedRandomSpot, WhirlyThing, BigWhirly, HorizontalLine, VerticalLine,
        // DiagonalLine1, DiagonalLine2, HorizontalOsc, VerticalOsc, DiagonalOsc1,
        // DiagonalOsc2, RainDrops, AreaClamp, LeakyTap, DrippyTap); 0x40/0x41 are
        // internal Whirly variants from AddDrop when FX_Frequency ≥ 0x80.
        void WaterRedrawDrops(FractalRandom rng)
        {
            uint xm = (uint)((UMask >> 1) & 0xff), ym = (uint)((VMask >> 1) & 0xff);
            byte[] F = SourceFields; int fB = USize / 2; int ub = UBits;
            GlobalPhase++;
            for (int i = 0; i < NumDrops; i++)
            {
                int d = i * 8;
                byte type = Drops[d];
                uint Y = Drops[d + 3], X = Drops[d + 2];
                int rowOff = (int)(Y << ub), idx = rowOff + (int)X;
                byte v; uint n, px, py, ph;
                switch (type)
                {
                    case 0x00:
                        v = Drops[d + 7]; F[idx] = v; F[idx + fB] = v; break;
                    case 0x01:
                        Drops[d + 1] = unchecked((byte)(Drops[d + 1] + Drops[d + 7]));
                        v = Sine[Drops[d + 1]]; F[idx] = v; F[idx + fB] = v; break;
                    case 0x02:
                        Drops[d + 1] = unchecked((byte)(Drops[d + 1] + Drops[d + 7]));
                        v = unchecked((byte)((Sine[Drops[d + 1]] >> 1) + 0x40)); F[idx] = v; F[idx + fB] = v; break;
                    case 0x03:
                        Drops[d + 1] = unchecked((byte)(Drops[d + 1] + Drops[d + 7]));
                        v = Sine[Drops[d + 1]]; if (v < 0x80) v = 0x80; F[idx] = v; F[idx + fB] = v; break;
                    case 0x04:
                    {
                        uint r1 = rng.Next(), r2 = rng.Next();
                        Drops[d + 2] = unchecked((byte)((byte)((r2 & 3) - (r1 & 3) + X) & (byte)(UMask >> 1)));
                        uint r3 = rng.Next(), r4 = rng.Next();
                        Drops[d + 3] = unchecked((byte)((byte)((r4 & 3) - (r3 & 3) + Y) & (byte)(VMask >> 1)));
                        F[idx] = 0xb9; F[idx + fB] = 0x47; break;
                    }
                    case 0x05:
                        F[idx] = unchecked((byte)rng.Next()); F[idx + fB] = unchecked((byte)rng.Next()); break;
                    case 0x06:
                        ph = (uint)(((Drops[d + 7] + Drops[d + 5]) * 0x100 + Drops[d + 6] + Drops[d + 4]) & 0xffff);
                        Drops[d + 4] = unchecked((byte)ph); Drops[d + 5] = unchecked((byte)(ph >> 8));
                        px = ((uint)(Sine[ph >> 8] >> 4) + X) & xm;
                        py = (uint)(Sine[((ph >> 8) + 0x40) & 0xff] >> 4);
                        goto whirl;
                    case 0x07:
                        ph = (uint)((Drops[d + 6] | (Drops[d + 7] << 8)) + (uint)(Drops[d + 4] | (Drops[d + 5] << 8) | (Drops[d + 6] << 16) | (Drops[d + 7] << 24))) & 0xffff;
                        goto whirl3;
                    case 0x40:
                        ph = (uint)((Drops[d + 5] * 0x100 - Drops[d + 7] * 0x100 - Drops[d + 6] + Drops[d + 4]) & 0xffff);
                        Drops[d + 4] = unchecked((byte)ph); Drops[d + 5] = unchecked((byte)(ph >> 8));
                        px = ((uint)(Sine[ph >> 8] >> 4) + X) & xm;
                        py = (uint)(Sine[((ph >> 8) + 0x40) & 0xff] >> 4);
                        goto whirl;
                    case 0x41:
                        ph = (uint)((Drops[d + 5] * 0x100 - Drops[d + 7] * 0x100 - Drops[d + 6]) + Drops[d + 4]) & 0xffff;
                        goto whirl3;
                    whirl3:
                        Drops[d + 5] = unchecked((byte)(ph >> 8)); Drops[d + 4] = unchecked((byte)ph);
                        px = ((uint)(Sine[Drops[d + 5]] >> 3) + X) & xm;
                        py = (uint)(Sine[(Drops[d + 5] + 0x40) & 0xff] >> 3);
                        goto whirl;
                    whirl:
                    {
                        int j = (int)px + (int)(((py + Y) & ym) << ub);
                        F[j] = Drops[d + 1]; F[j + fB] = Drops[d + 1]; break;
                    }
                    case 0x08:
                    {
                        v = Drops[d + 1]; n = (uint)(Drops[d + 7] >> 1) + 1; uint x = X;
                        do { uint c = xm & x; x++; F[rowOff + c] = v; F[rowOff + fB + c] = v; } while (--n != 0);
                        break;
                    }
                    case 0x09:
                    {
                        v = Drops[d + 1]; n = (uint)(Drops[d + 7] >> 1) + 1; uint y = Y;
                        do { int r = (int)((ym & y) << ub); y++; F[(int)X + r] = v; F[(int)X + fB + r] = v; } while (--n != 0);
                        break;
                    }
                    case 0x0a:
                    {
                        v = Drops[d + 1]; int k = 0; uint yy = Y;
                        do { int j = (int)(((ym & yy) << ub) + ((X - (uint)k) & xm)); F[j] = v; F[j + fB] = v; k++; yy++; }
                        while (k <= (Drops[d + 7] >> 1));
                        break;
                    }
                    case 0x0b:
                    {
                        v = Drops[d + 1]; n = (uint)(Drops[d + 7] >> 1) + 1; uint xx = X, y = Y;
                        do { int j = (int)((xm & xx) + ((ym & y) << ub)); y++; F[j] = v; F[j + fB] = v; xx++; } while (--n != 0);
                        break;
                    }
                    case 0x0c:
                    {
                        Drops[d + 1] = unchecked((byte)(Drops[d + 1] + Drops[d + 6])); v = Sine[Drops[d + 1]];
                        n = (uint)(Drops[d + 7] >> 1) + 1; uint x = X;
                        do { uint c = xm & x; x++; F[rowOff + c] = v; F[rowOff + fB + c] = v; } while (--n != 0);
                        break;
                    }
                    case 0x0d:
                    {
                        Drops[d + 1] = unchecked((byte)(Drops[d + 1] + Drops[d + 6])); v = Sine[Drops[d + 1]];
                        n = (uint)(Drops[d + 7] >> 1) + 1; uint y = Y;
                        do { int r = (int)((ym & y) << ub); y++; F[(int)X + r] = v; F[(int)X + fB + r] = v; } while (--n != 0);
                        break;
                    }
                    case 0x0e:
                    {
                        Drops[d + 1] = unchecked((byte)(Drops[d + 1] + Drops[d + 6])); v = Sine[Drops[d + 1]];
                        int k = 0; uint yy = Y;
                        do { int j = (int)(((ym & yy) << ub) + ((X - (uint)k) & xm)); F[j] = v; F[j + fB] = v; k++; yy++; }
                        while (k <= (Drops[d + 7] >> 1));
                        break;
                    }
                    case 0x0f:
                    {
                        Drops[d + 1] = unchecked((byte)(Drops[d + 1] + Drops[d + 6])); v = Sine[Drops[d + 1]];
                        n = (uint)(Drops[d + 7] >> 1) + 1; uint xx = X, y = Y;
                        do { int j = (int)((xm & xx) + ((ym & y) << ub)); y++; F[j] = v; F[j + fB] = v; xx++; } while (--n != 0);
                        break;
                    }
                    case 0x10:
                    {
                        uint r = rng.Next();
                        if ((r & 0xf) == 0)
                        {
                            v = Drops[d + 1]; int a = Drops[d + 7];
                            int r1 = (int)(rng.Next() & 0xff), r2 = (int)(rng.Next() & 0xff);
                            int j = (int)(((uint)((r1 * a) >> 8) + Y) & ym) << ub;
                            j += (int)(((uint)((r2 * a) >> 8) + X) & xm);
                            F[j] = v; F[j + fB] = unchecked((byte)~v);
                        }
                        break;
                    }
                    case 0x11:
                    {
                        v = Drops[d + 1]; uint yy = Y;
                        for (n = (uint)(Drops[d + 7] >> 1); n != 0; n--)
                        {
                            uint x = X;
                            for (uint m = (uint)(Drops[d + 7] >> 1); m != 0; m--)
                            { int j = (int)((xm & x) + ((ym & yy) << ub)); x++; F[j] = v; F[j + fB] = v; }
                            yy++;
                        }
                        break;
                    }
                    case 0x12:
                    {
                        byte old = Drops[d + 4]; byte nv = unchecked((byte)(old + Drops[d + 7])); Drops[d + 4] = nv;
                        if (nv <= Drops[d + 7]) { F[idx] = Drops[d + 1]; F[idx + fB] = unchecked((byte)~Drops[d + 1]); }
                        break;
                    }
                    case 0x13:
                    {
                        byte old = Drops[d + 4]; byte nv = unchecked((byte)(old + Drops[d + 7])); Drops[d + 4] = nv;
                        if (nv <= Drops[d + 7])
                        {
                            Drops[d + 4] = unchecked((byte)rng.Next());
                            F[idx] = Drops[d + 1]; F[idx + fB] = unchecked((byte)~Drops[d + 1]);
                        }
                        break;
                    }
                    default: break;
                }
            }
        }
    }
}
