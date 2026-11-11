// FireTexture — UE1 procedural fire/spark texture (Fire.dll UFireTexture) in Core.
//
// UFireTexture::ConstantTimeTick step (Fire.dll.c:4579-4610):
//     RedrawSparks -> fire kernel -> PostDrawSparks
// * RedrawSparks (Fire.dll.c:966-2506, 0x105025e0) and DrawFlashRamp (:811-963,
//   0x10502320) are ported from the decompile as plain code; PostDrawSparks (:2513-2549).
//   Verified byte-for-byte against the real bodies under unicorn (fire_oracle.py,
//   validate_fire_texture.py): all 44 spark types, campaign carriers and synthetic cases.
// * The fire kernel is self-modifying code in .data (0x10515780 rising / 0x105159a4,
//   plus two PentiumPro variants). The closed form below was derived by a
//   sensitivity probe and verified byte-for-byte against ALL four shipped kernels
//   on random fields (48 cases, sizes 8x8..128x32..32x256):
//     rising : out[y][x] = T[ p[y+1][x-1] + p[y+1][x] + p[y+1][x+1] + p[y+2][x] ]
//     else   : out[y][x] = T[ p[y  ][x-1] + p[y  ][x] + p[y  ][x+1] + p[y+1][x] ]
//   rows and columns wrap; the ORIGINAL field is read (shipped keeps
//   the first two rows in buffer 0x10515500).
// * RenderTable (PostLoad, disasm 0x10507ffa-0x1050807b):
//     T[i] = trunc(clamp(i*0.25 + 1.0 - (255 - RenderHeat)*0.0625, 0, 255)).
// * Cadence — UTexture::Tick/Prime, as for WaterTexture (viewport wall clock).
//
// Object fields (shipped offsets): +0x30 UBits +0x34 USize +0x38 VSize +0xc0 UMask
// +0xc4 VMask +0xd0 GlobalPhase +0xd4 DrawPhase +0xd5 AuxPhase +0xd9 RenderHeat
// +0xdc bit0 bRising +0xe4 FX_Frequency +0xec SparksLimit +0xf0 NumSparks +0xf4 Sparks
// +0x100 OldRenderHeat +0x104 RenderTable[0x400] +0x504..0x50b tail (+0x508 — the
// PostDrawSparks flag: constructor 0x7970 sets 1, AddSpark of type 0x16 sets 1).
// A spark is 8 bytes: [0] type, [1] heat, [2] X, [3] Y, [4..7] type parameters.
//
// Oracle: se_harness/bsp_diff/fire_oracle.py (real Fire.dll bodies under unicorn),
// validator validate_fire_texture.py, red test audit/fire_texture_redtest.sh.
using System;
using System.Buffers.Binary;

namespace HP1.UEScript
{
    public sealed class FireTexture
    {
        readonly byte[] _sparks;
        readonly byte[] _table = new byte[0x400];
        readonly byte[] _phases = new byte[8];          // +0xd0..0xd7
        readonly byte[] _tail = new byte[8];            // +0x504..0x50b
        readonly int _uBits, _uMask, _vMask;
        readonly byte _fxFrequency;
        byte[] _scratch;
        FractalRandom _rng;
        int _numSparks;

        public readonly int USize, VSize;
        public readonly byte[] Mip0;
        public readonly bool Rising;
        public readonly int SparksLimit;
        public float MinFrameRate, MaxFrameRate, Accumulator;
        public byte PrimeCount, PrimeCurrent;

        static readonly byte[] Sine = WaterTexture.Sine;                              // DAT_1051731c
        static readonly byte[] SineP20 = Build(v => (byte)Math.Min(255, v + 0x20));   // DAT_10518348
        static readonly byte[] SineP80 = Build(v => unchecked((byte)(v + 0x80)));      // DAT_1051908c
        static byte[] Build(Func<int, byte> f)
        {
            var t = new byte[256];
            for (int i = 0; i < 256; i++) t[i] = f(Sine[i]);
            return t;
        }

        public int NumSparks => _numSparks;
        public ReadOnlySpan<byte> Sparks => _sparks.AsSpan(0, Math.Max(0, Math.Min(_numSparks, SparksLimit)) * 8);
        public ReadOnlySpan<byte> RenderTable => _table;
        public ReadOnlySpan<byte> Phases => _phases;
        public ReadOnlySpan<byte> Tail => _tail;

        public FireTexture(int uSize, int vSize, byte renderHeat, bool rising, int sparksLimit,
                           ReadOnlySpan<byte> sparks, byte fxFrequency)
        {
            if (uSize <= 0 || vSize <= 0 || (uSize & (uSize - 1)) != 0 || (vSize & (vSize - 1)) != 0)
                throw new ArgumentException($"FireTexture: dimensions must be powers of two, got {uSize}x{vSize}");
            USize = uSize; VSize = vSize; Rising = rising;
            if (sparks.Length % 8 != 0)
                throw new ArgumentException($"FireTexture: spark array of {sparks.Length} bytes is not a multiple of 8");
            // UFireTexture::Serialize grows the spark array to the tag's SparksLimit (Fire.dll.c:4818-4826), then
            // UFireTexture::PostLoad (disasm): array length == SparksLimit -> exit
            // BEFORE the clamp (0x10508089-0x10508097) — the tag limit stays as is (including < 4 and > 0x2000);
            // otherwise (more sparks in the array than the limit) SparksLimit is clamped to [4, 0x2000] (0x1050809d-0x105080bf)
            // and, while NumSparks > SparksLimit, sparks of type >= 0x1f are removed from the end (the last one takes
            // their place, 0x105080d3-0x10508117), then NumSparks = min(SparksLimit, NumSparks) (0x10508119-0x1050812b).
            int n = sparks.Length / 8;
            byte[] s0 = sparks.ToArray();
            if (n <= sparksLimit) SparksLimit = sparksLimit;
            else
            {
                SparksLimit = sparksLimit < 4 ? 4 : sparksLimit > 0x2000 ? 0x2000 : sparksLimit;
                for (int i = n; SparksLimit < n;)
                {
                    do { i--; } while (i >= 0 && s0[i * 8] < 0x1f);
                    if (i < 0) break;
                    n--;
                    Buffer.BlockCopy(s0, n * 8, s0, i * 8, 8);
                }
                if (n > SparksLimit) n = SparksLimit;
            }
            Mip0 = new byte[uSize * vSize];
            _sparks = new byte[Math.Max(SparksLimit, n) * 8];
            Buffer.BlockCopy(s0, 0, _sparks, 0, n * 8);
            _numSparks = n;
            while ((1 << _uBits) < uSize) _uBits++;
            _uMask = uSize - 1; _vMask = vSize - 1;
            _fxFrequency = fxFrequency;
            _tail[4] = 1;                                     // constructor: +0x508 = 1
            for (int i = 0; i < 0x400; i++)
            {
                double v = i * 0.25 + 1.0 - (255 - renderHeat) * 0.0625;
                _table[i] = (byte)(int)(v < 0.0 ? 0.0 : v > 255.0 ? 255.0 : v);   // ftol: truncation
            }
        }

        // UTexture::Tick (Engine.dll.c:70100) — same body as WaterTexture.Tick.
        public void Tick(float deltaSeconds, FractalRandom rng)
        {
            while (PrimeCurrent < PrimeCount) { PrimeCurrent++; ConstantTimeTick(rng); }
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

        public bool ConstantTimeTick(FractalRandom rng)
        {
            if (USize <= 7 || VSize <= 7) return false;      // Fire.dll.c:4591
            _rng = rng ?? throw new ArgumentNullException(nameof(rng));
            RedrawSparks();
            Kernel();
            PostDrawSparks();
            _rng = null;
            return true;
        }

        void Kernel()
        {
            int w = USize, h = VSize, um = w - 1, vm = h - 1;
            _scratch ??= new byte[w * h];
            Buffer.BlockCopy(Mip0, 0, _scratch, 0, w * h);
            byte[] p = _scratch;
            for (int y = 0; y < h; y++)
            {
                int r0 = (Rising ? ((y + 1) & vm) : y) * w, r1 = (Rising ? ((y + 2) & vm) : ((y + 1) & vm)) * w, o = y * w;
                for (int x = 0; x < w; x++)
                    Mip0[o + x] = _table[p[r0 + ((x - 1) & um)] + p[r0 + x] + p[r0 + ((x + 1) & um)] + p[r1 + x]];
            }
        }

        // ── sparks ───────────────────────────────────────────────────────────
        byte R() => (byte)_rng.Next();
        byte UM => (byte)_uMask;
        byte VM => (byte)_vMask;
        void Plot(int x, int y, byte v) => Mip0[(y << _uBits) + x] = v;

        // Coordinate drift by signed parameter c: one RNG byte; when (r & 0x7f) < |c|,
        // step 1 toward the sign (Fire.dll.c, common tail of types 0x20-0x2b).
        void Drift(int s, int coord, sbyte c, byte mask)
        {
            int r = R() & 0x7f;
            if (c < 0) { if (r < -c) _sparks[s + coord] = (byte)((_sparks[s + coord] - 1) & mask); }
            else if (r < c) _sparks[s + coord] = (byte)((_sparks[s + coord] + 1) & mask);
        }
        void DriftXY(int s, sbyte cx, sbyte cy) { Drift(s, 2, cx, UM); Drift(s, 3, cy, VM); }

        // Parent random walk: X/Y += (r&7) − (r'&7) (types 0x0f, 0x14).
        void Jitter7(int s)
        {
            byte a = R(), b = R(); _sparks[s + 2] = (byte)(((a & 7) - (b & 7) + _sparks[s + 2]) & UM);
            a = R(); b = R(); _sparks[s + 3] = (byte)(((a & 7) - (b & 7) + _sparks[s + 3]) & VM);
        }
        // Parent random walk: with probability 1/2, X/Y += (r&0xf) − 7 (types 0x10, 0x12, 0x13, 0x1b).
        void Jitter15(int s)
        {
            if ((R() & 1) != 0) _sparks[s + 2] = (byte)(((R() & 0xf) + _sparks[s + 2] - 7) & UM);
            if ((R() & 1) != 0) _sparks[s + 3] = (byte)(((R() & 0xf) + _sparks[s + 3] - 7) & VM);
        }

        // New spark at the end of the array (bytes the type does not write keep earlier sparks' values).
        int Spawn(byte type) { int n = _numSparks++ * 8; _sparks[n] = type; return n; }
        // LAB_10505867: the last spark takes this spark's place; the index is not rolled back.
        void Kill(int s) { _numSparks--; Buffer.BlockCopy(_sparks, _numSparks * 8, _sparks, s, 8); }

        void RedrawSparks()
        {
            _phases[5] = (byte)(_phases[5] + _fxFrequency);                       // AuxPhase += FX_Frequency
            int phase = BinaryPrimitives.ReadInt32LittleEndian(_phases) + 1;      // GlobalPhase++
            BinaryPrimitives.WriteInt32LittleEndian(_phases, phase);
            byte[] S = _sparks;
            for (int i = 0; i < _numSparks; i++)
            {
                int s = i * 8;
                bool room = _numSparks < SparksLimit;
                switch (S[s])
                {
                    case 0x00: Plot(S[s + 2], S[s + 3], R()); break;
                    case 0x01:
                    {
                        byte r1 = R(), r2 = R();
                        int x = _uMask & (((S[s + 4] * r1) >> 8) + S[s + 2]);
                        int y = (((S[s + 5] * r2) >> 8) + S[s + 3]) & _vMask;
                        Plot(x, y, S[s + 1]);
                        break;
                    }
                    case 0x02: Plot(S[s + 2], S[s + 3], S[s + 1]); S[s + 1] += S[s + 7]; break;
                    case 0x03:
                        if (S[s + 6] < S[s + 1]) Plot(S[s + 2], S[s + 3], S[s + 1]);
                        S[s + 1] += S[s + 7];
                        if (S[s + 1] < S[s + 7]) S[s + 1] = R();
                        break;
                    case 0x04:
                        if (room && R() < 0x80)
                        {
                            int n = Spawn(0x20); S[n + 1] = S[s + 1]; S[n + 2] = S[s + 2]; S[n + 3] = S[s + 3];
                            S[n + 4] = R(); S[n + 5] = R(); S[n + 6] = S[s + 6]; S[n + 7] = S[s + 7];
                        }
                        break;
                    case 0x05:
                        if (room && R() < 0x80)
                        {
                            int n = Spawn(0x21); S[n + 1] = S[s + 1]; S[n + 2] = S[s + 2]; S[n + 3] = S[s + 3];
                            S[n + 4] = (byte)((R() & 0x7f) - 0x3f); S[n + 5] = 0x81; S[n + 7] = 2;
                        }
                        break;
                    case 0x06:
                        if (room && R() < 0x40)
                        {
                            int n = Spawn(0x22); S[n + 1] = S[s + 1]; S[n + 2] = S[s + 2]; S[n + 3] = S[s + 3];
                            S[n + 4] = (byte)((R() & 0x7f) - 0x3f); S[n + 5] = 0; S[n + 6] = 0x32;
                        }
                        break;
                    case 0x07:
                    case 0x08:
                        if (room && R() < 0x40)
                        {
                            int n = Spawn(0x22); S[n + 1] = S[s + 1]; S[n + 2] = S[s + 2]; S[n + 3] = S[s + 3];
                            S[n + 4] = (byte)((R() & 0x3f) + (S[s] == 0x07 ? 0x3f : 0x80));
                            S[n + 5] = 0xe3; S[n + 6] = S[s + 6];
                        }
                        break;
                    case 0x09:
                    case 0x0a:
                    {
                        byte a = S[s + 4]; int up = (a + 0x40) & 0xff;
                        if (S[s] == 0x09 || up < 0x80)
                        {
                            int v = Sine[up] + S[s + 1];
                            Plot((byte)(((Sine[a] * S[s + 5]) >> 8) + S[s + 2]) & _uMask, S[s + 3], (byte)(v > 0xff ? 0xff : v));
                        }
                        S[s + 4] += S[s + 7];
                        break;
                    }
                    case 0x0b:
                    {
                        byte a = S[s + 4], b = S[s + 5], h = S[s + 1];
                        int x = (byte)(((Sine[a] * h) >> 8) + S[s + 2]) & _uMask;
                        int y = (byte)(((Sine[b] * h) >> 8) + S[s + 3]) & _vMask;
                        Plot(x, y, SineP20[(byte)(a + 0x40)]);
                        S[s + 4] += S[s + 6]; S[s + 5] += S[s + 7];
                        break;
                    }
                    case 0x0c:
                    {
                        byte a = S[s + 4]; int v = Sine[(a + 0x40) & 0xff] + S[s + 1];
                        Plot(S[s + 2], (byte)(((Sine[a] * S[s + 5]) >> 8) + S[s + 3]) & _vMask, (byte)(v > 0xff ? 0xff : v));
                        S[s + 4] += S[s + 7];
                        break;
                    }
                    case 0x0d:
                    case 0x0e:
                        if (room && R() < 0x40)
                        {
                            int n = Spawn(S[s] == 0x0d ? (byte)0x21 : (byte)0x2a);
                            S[n + 1] = S[s + 1]; S[n + 2] = S[s + 2]; S[n + 3] = S[s + 3];
                            S[n + 4] = S[s + 4]; S[n + 5] = S[s + 5]; S[n + 7] = S[s + 7];
                        }
                        break;
                    case 0x0f:
                        if (room)
                        {
                            int n = Spawn(0x27);
                            S[n + 2] = (byte)(((R() & 0x1f) + S[s + 2]) & UM); S[n + 3] = (byte)(((R() & 0x1f) + S[s + 3]) & VM);
                            S[n + 5] = S[s + 4]; S[n + 4] = 0; S[n + 6] = S[s + 5]; S[n + 7] = S[s + 7]; S[n + 1] = S[s + 1];
                            S[s + 4] += S[s + 6];
                        }
                        Jitter7(s);
                        break;
                    case 0x10:
                    case 0x1b:
                        if (R() < 0x14 && room)
                        {
                            if (S[s] == 0x10)
                            {
                                int n = Spawn(0x26); S[n + 1] = S[s + 1];
                                S[n + 2] = (byte)(((R() & 0x1f) + S[s + 2]) & UM); S[n + 3] = (byte)(((R() & 0x1f) + S[s + 3]) & VM);
                                S[n + 4] = R(); S[n + 5] = R(); S[n + 6] = S[s + 6];
                            }
                            else
                            {
                                int n = Spawn(0x2b); S[n + 1] = S[s + 1];
                                S[n + 2] = (byte)(((R() & 0x1f) + S[s + 2]) & UM); S[n + 3] = (byte)(((R() & 0x1f) + S[s + 3]) & VM);
                                S[n + 4] = R(); S[n + 6] = S[s + 6]; S[n + 7] = R();
                            }
                        }
                        Jitter15(s);
                        break;
                    case 0x11:
                        if (room && R() < 0x80)
                        {
                            int n = Spawn(0x23);
                            S[n + 2] = (byte)((((R() * S[s + 6]) >> 8) + S[s + 2]) & UM);
                            S[n + 3] = (byte)((((R() * S[s + 6]) >> 8) + S[s + 3]) & VM);
                            S[n + 4] = (byte)(R() - 0x7f); S[n + 5] = 0x81; S[n + 6] = 0xff;
                        }
                        break;
                    case 0x12:
                    case 0x13:
                        if (room)
                        {
                            int n = Spawn(S[s] == 0x12 ? (byte)0x23 : (byte)0x24);
                            S[n + 2] = (byte)(((R() & 0x1f) + S[s + 2]) & UM); S[n + 3] = (byte)(((R() & 0x1f) + S[s + 3]) & VM);
                            if (S[s] == 0x12) { S[n + 4] = (byte)(R() - 0x7f); S[n + 6] = 0xff; }
                            else { S[n + 4] = (byte)((R() & 0x1f) - 0xf); S[n + 6] = 0; }
                            S[n + 5] = 0x81;
                        }
                        Jitter15(s);
                        break;
                    case 0x14:
                        if (room)
                        {
                            int n = Spawn(0x25);
                            S[n + 2] = (byte)(((R() & 0x1f) + S[s + 2]) & UM); S[n + 3] = (byte)(((R() & 0x1f) + S[s + 3]) & VM);
                            S[n + 4] = S[s + 4]; S[n + 5] = S[s + 5]; S[n + 6] = S[s + 7];
                        }
                        Jitter7(s);
                        break;
                    case 0x15:
                        if (room)
                        {
                            int n = Spawn(0x25);
                            S[n + 2] = (byte)((((R() * S[s + 6]) >> 8) + S[s + 2]) & UM);
                            S[n + 3] = (byte)((((R() * S[s + 6]) >> 8) + S[s + 3]) & VM);
                            S[n + 4] = S[s + 4]; S[n + 5] = S[s + 5]; S[n + 6] = S[s + 7];
                        }
                        break;
                    case 0x16: Plot(S[s + 2], S[s + 3], S[s + 5]); break;
                    case 0x17:
                    case 0x18:
                        if (S[s + 1] != 0)
                        {
                            if (S[s + 6] == 0)
                            {
                                if (S[s + 7] <= R()) S[s + 6] = (byte)((R() + 1) & 5);
                            }
                            else
                            {
                                S[s + 6]--;
                                byte h = S[s + 1];
                                DrawFlashRamp(S[s + 2], S[s + 3], S[s + 4], S[s + 5], h, S[s] == 0x17 ? h : (byte)(h >> 3));
                            }
                        }
                        break;
                    case 0x19:
                        if (S[s + 7] <= R())
                        {
                            byte r = R(), h = S[s + 1]; int sz = S[s + 6];
                            int a = ((Sine[r] * sz) >> 8) - (sz >> 1);
                            int b = ((Sine[(r + 0x40) & 0xff] * sz) >> 8) - (sz >> 1);
                            byte dx = a < 1 ? (byte)((byte)(-a) | 1) : (byte)(a & 0xfe);
                            byte dy = b < 1 ? (byte)((byte)(-b) | 1) : (byte)(b & 0xfe);
                            DrawFlashRamp(S[s + 2], S[s + 3], dx, dy, h, (byte)(h >> 2));
                        }
                        break;
                    case 0x1a:
                        if (room)
                        {
                            int n = Spawn(0x27);
                            S[n + 2] = S[s + 2]; S[n + 3] = S[s + 3]; S[n + 5] = S[s + 4]; S[n + 4] = 0;
                            S[n + 6] = S[s + 5]; S[n + 7] = S[s + 7]; S[n + 1] = S[s + 1];
                        }
                        S[s + 4] += S[s + 6];
                        break;
                    case 0x1c:
                        if (room)
                        {
                            int n = Spawn(0x28);
                            S[n + 2] = S[s + 2]; S[n + 3] = S[s + 3]; S[n + 1] = S[s + 1];
                            S[n + 4] = S[s + 4]; S[n + 5] = S[s + 5]; S[n + 6] = S[s + 6]; S[n + 7] = 2;
                        }
                        S[s + 4] += S[s + 7];
                        break;
                    case 0x1d:
                    {
                        byte a = S[s + 4];
                        Plot((byte)(((Sine[a] * S[s + 1]) >> 8) + S[s + 2]) & _uMask, S[s + 3], SineP20[(byte)(a + 0x40)]);
                        S[s + 4] += S[s + 6];
                        break;
                    }
                    case 0x1e:
                    {
                        byte b = S[s + 5];
                        Plot(S[s + 2], (byte)(((Sine[b] * S[s + 1]) >> 8) + S[s + 3]) & _vMask, SineP20[(byte)(b + 0x40)]);
                        S[s + 5] += S[s + 7];
                        break;
                    }
                    case 0x20:
                        S[s + 1] -= 5;
                        if (S[s + 1] >= 0xfb) { Kill(s); break; }
                        Plot(S[s + 2], S[s + 3], S[s + 1]); DriftXY(s, (sbyte)S[s + 4], (sbyte)S[s + 5]);
                        break;
                    case 0x21:
                        S[s + 1] -= S[s + 7];
                        if (S[s + 1] <= S[s + 7]) { Kill(s); break; }
                        Plot(S[s + 2], S[s + 3], S[s + 1]); DriftXY(s, (sbyte)S[s + 4], (sbyte)S[s + 5]);
                        break;
                    case 0x22:
                    case 0x2a:
                    {
                        if (S[s] == 0x22) { if (--S[s + 6] == 0) { Kill(s); break; } }
                        else { S[s + 1] -= S[s + 7]; if (S[s + 1] < 0x33) { Kill(s); break; } }
                        Plot(S[s + 2], S[s + 3], S[s + 1]);
                        sbyte cy = (sbyte)S[s + 5];
                        DriftXY(s, (sbyte)S[s + 4], cy);
                        if (S[s] == 0x22 ? cy < 0x7a : ((_phases[0] & 1) != 0 && cy < 0x7c)) S[s + 5] = (byte)(cy + 3);
                        break;
                    }
                    case 0x23:
                    case 0x24:
                        if (S[s] == 0x23) { S[s + 6] -= 3; if (S[s + 6] < 0xbf) { Kill(s); break; } }
                        else { S[s + 6] += 4; if (S[s + 6] > 0xf9) { Kill(s); break; } }
                        Plot(S[s + 2], S[s + 3], S[s + 6]);
                        Drift(s, 2, (sbyte)S[s + 4], UM);
                        S[s + 3] = (byte)((S[s + 3] - 2) & VM);
                        break;
                    case 0x25:
                        S[s + 6] += 4;
                        if (S[s + 6] > 0xf9) { Kill(s); break; }
                        Plot(S[s + 2], S[s + 3], S[s + 6]); DriftXY(s, (sbyte)S[s + 4], (sbyte)S[s + 5]);
                        break;
                    case 0x26:
                        if (--S[s + 6] == 0xff) { Kill(s); break; }
                        Plot(S[s + 2], S[s + 3], S[s + 1]); DriftXY(s, (sbyte)S[s + 4], (sbyte)S[s + 5]);
                        break;
                    case 0x27:
                    {
                        if (--S[s + 6] == 0xff) { Kill(s); break; }
                        Plot(S[s + 2], S[s + 3], S[s + 1]);
                        byte b = S[s + 5];
                        sbyte cy = (sbyte)SineP80[b], cx = (sbyte)SineP80[(b + 0x40) & 0xff];
                        ushort ab = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(S.AsSpan(s + 4)) + S[s + 7] * 0x10);
                        BinaryPrimitives.WriteUInt16LittleEndian(S.AsSpan(s + 4), ab);
                        DriftXY(s, cx, cy);
                        break;
                    }
                    case 0x28:
                    {
                        if (--S[s + 6] == 0xff) { Kill(s); break; }
                        Plot(S[s + 2], S[s + 3], S[s + 1]);
                        byte a = S[s + 4];
                        sbyte cx = (sbyte)(Sine[(a + 0x40) & 0xff] - 0x80), cy = (sbyte)S[s + 5];
                        S[s + 4] = (byte)(S[s + 7] + a);
                        DriftXY(s, cx, cy);
                        break;
                    }
                    case 0x29:
                        S[s + 1] -= S[s + 6];
                        if (S[s + 1] > 0xf9) { Kill(s); break; }
                        Plot(S[s + 2], S[s + 3], S[s + 1]); DriftXY(s, (sbyte)S[s + 4], (sbyte)S[s + 5]);
                        break;
                    case 0x2b:
                    {
                        if (--S[s + 6] == 0xff) { Kill(s); break; }
                        Plot(S[s + 2], S[s + 3], S[s + 1]);
                        S[s + 4] += 7;
                        int v = S[s + 4] & 0x7f;
                        if (v > 0x3f) v = 0x7f - v;
                        byte k = (byte)(S[s + 7] + v);
                        DriftXY(s, (sbyte)(Sine[k] - 0x7f), (sbyte)(Sine[(byte)(k + 0x40)] - 0x7f));
                        break;
                    }
                }
            }
        }

        // UFireTexture::DrawFlashRamp (Fire.dll.c:816-963): segment (X, Y, DX, DY); the length sign
        // is the low bit (odd = negative); colour c1 -> c2 in 23-bit
        // fixed point; the cross coordinate jitters by one RNG byte per step.
        void DrawFlashRamp(byte x, byte y, byte dx, byte dy, byte c1, byte c2)
        {
            if (((dy & 1) != 0 && dy * 2 >= dx) || ((dx & 1) != 0 && dx > dy * 2))
            {
                // reversal: start moves to the end, length signs flip, colours swap
                x = (byte)(x + ((dx & 1) != 0 ? -dx : dx));
                y = (byte)(y + ((dy & 1) != 0 ? -dy : dy));
                dx ^= 1; dy ^= 1;
                (c1, c2) = (c2, c1);
            }
            int n = (dx < dy ? dy : dx) | 1;
            Span<byte> jit = stackalloc byte[256];
            int sum = 0;
            for (int i = 0; i < n; i++) { jit[i] = R(); sum += jit[i]; }
            int sy = (dy & 1) == 0 ? dy : -dy, sx = (dx & 1) == 0 ? dx : -dx;
            int colStep = (int)((uint)(c2 - c1) * 0x800000u) / n;
            int col = c1 << 23;
            if (dx < dy)
            {
                int step = (sx * 0x40 - sum) / n, fx = x << 6, ystep = (dy & 1) == 0 ? 1 : -1;
                for (int i = 0; i < dy; i++)
                {
                    fx += jit[i] + step; byte yy = y; y = (byte)(y + ystep); col += colStep;
                    Mip0[((yy & _vMask) << _uBits) + ((fx >> 6) & _uMask)] = (byte)(col >> 23);
                }
            }
            else
            {
                int step = (sy * 0x40 - sum) / n, fy = y << 6, xstep = (dx & 1) == 0 ? 1 : -1;
                for (int i = 0; i < dx; i++)
                {
                    byte xx = x; fy += jit[i] + step; x = (byte)(x + xstep); col += colStep;
                    Mip0[(((fy >> 6) & _vMask) << _uBits) + (xx & _uMask)] = (byte)(col >> 23);
                }
            }
        }

        // UFireTexture::PostDrawSparks (Fire.dll.c:2513-2549): while flag +0x508 is set,
        // type-0x16 sparks remember the pixel under them and draw [4] over it if that pixel is
        // darker than 0x26; with no such spark the flag is cleared.
        void PostDrawSparks()
        {
            if (_tail[4] == 0) return;
            bool any = false;
            for (int i = 0; i < _numSparks; i++)
            {
                int s = i * 8;
                if (_sparks[s] != 0x16) continue;
                any = true;
                int p = (_sparks[s + 3] << _uBits) + _sparks[s + 2];
                byte under = Mip0[p];
                _sparks[s + 5] = under;
                if (under < 0x26) Mip0[p] = _sparks[s + 4];
            }
            if (!any) _tail[4] = 0;
        }
    }
}
