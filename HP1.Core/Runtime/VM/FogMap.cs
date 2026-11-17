using System;
using System.Collections.Generic;

namespace HP1.UEScript
{
    // ZONE FOG: surface fog-map generator — port of the texel loop of
    // FLightManager::SetupForSurf (Render.dll.c:20006-20260), the light record fields of
    // ComputeFromActor (:479-500), the colour ramp (:435-470), the blend table
    // (:16430-16436) and the table FastSqrt (:16500-16509 ==
    // FGlobalMath +0x1008c, Core.dll.c:47370-47380). Density, Build and VertexFog are judged against the real
    // texel and vertex loops under unicorn (se_harness/bsp_diff/validate_fogmap.py).
    //
    // NOT decided here: the light set (LeafVolumetricLighting +
    // SetupDynamics — Core, LevelFogMaps),
    // the texel grid (same as the lightmap — the caller computes start and steps)
    // and the D3DDrv composite (out = 2·rgb7/255 + dest·(1 − 2·a7/255) — shader).
    //
    // Arithmetic is float32: the fog bodies run inside the render window, where the x87 control word is PC24
    // (URender::PreRender -> appEnableFastMath(1)), i.e. float precision as here.
    public static class FogMap
    {
        // FastSqrt: 2048 entries indexed by the top 10 mantissa bits + the low
        // exponent bit; result = table + (exp & 0x7f000000) >> 1, in bits.
        // Table 0x10b4ca44, built by FLightManager::Init (URender::Init, PC64 — outside the
        // PreRender..PostRender PC24 window). Disasm 0x10b025c8-0x10b025d7: sqrt(2.0) is stored as a
        // QWORD and multiplied as one (`fmul qword [ebp-0x1c]`); the decompile's `(float)local_20`
        // drops that. A float32 sqrt2 is the PC24 build and differs in 248 of 2048 entries
        // (checked by running both shipped builders under unicorn).
        static readonly int[] _sqrt = BuildSqrtTable();

        static int[] BuildSqrtTable()
        {
            var t = new int[2048];
            double sqrt2 = Math.Sqrt(2.0);
            for (int k = 0; k < 1024; k++)
            {
                float f = BitConverter.Int32BitsToSingle(0x3f800000 + k * 0x2000);
                float s = (float)Math.Sqrt(f);
                float l = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(s) - 0x20000000);
                t[k] = BitConverter.SingleToInt32Bits((float)(l * sqrt2));
                t[1024 + k] = BitConverter.SingleToInt32Bits(l + l);
            }
            return t;
        }

        public static float FastSqrt(float x)
        {
            int b = BitConverter.SingleToInt32Bits(x);
            return BitConverter.Int32BitsToSingle(_sqrt[(b >> 13) & 0x7ff] + ((b & 0x7f000000) >> 1));
        }

        // DAT_10b47358[a·128 + x] = (127 − a)·x / 127 (integer, toward zero).
        static readonly byte[] _blend = BuildBlendTable();

        static byte[] BuildBlendTable()
        {
            var t = new byte[128 * 128];
            for (int a = 0; a < 128; a++)
                for (int x = 0; x < 128; x++)
                    t[a * 128 + x] = (byte)(((0x7f - a) * x) / 0x7f);
            return t;
        }

        static int RoundEven(float x) => (int)Math.Round((double)x, MidpointRounding.ToEven);

        // Volumetric light record in camera coordinates.
        public sealed class Light
        {
            public float Lx, Ly, Lz;          // light position relative to the camera (+8..+0x10)
            public float R, R2, InvR, InvR2;  // (VolumeRadius+1)·25 and derived values (+0x64..+0x78)
            public float L2;                  // |L|² (+0x70)
            public float K;                   // VolumeBrightness·(LightBrightness/255)·(1/64) (+0x6c)
            public bool Inside;               // |L|² < r² (+0x7c)
            public int Source;                // LightSource (actor +0x1f4): 2 = by depth
            // Raw record fields for actor vertex fog (LightAndFog,
            // Render.dll.c:17086-17110): [0x27..0x29] — light colour (copy of
            // +0x8c..+0x94), [0x2a] — VolumeFog·(1/255) (+0xa8). The ramp above already
            // holds them in 16.16, but the ramp is for surface texels; the vertex
            // is computed in float, as in shipped.
            public float ColR, ColG, ColB, FogA;
            public readonly byte[] Ramp = new byte[256 * 4];   // (B,G,R,A) × 256 (+0xc0)
        }

        // col — light colour (FGetHSV(H,S,255)·(Level.Brightness·b3c), as +0x8c..+0x94 after :190-199),
        // b3c — FLightInfo+0x3c: LightBrightness·(1/255), light-type function, clamp [0,1] (SurfLightmap.LightBrightness).
        // AActor::WorldVolumetricRadius (Engine.dll 0x1031b9e0): (VolumeRadius+1)*25, no DrawScale factor.
        public static float WorldVolumetricRadius(int volumeRadius) => (float)(volumeRadius + 1) * 25f;

        public static Light MakeLight(float lx, float ly, float lz, int volumeRadius,
            int volumeBrightness, float b3c, int volumeFog,
            float colR, float colG, float colB, int source)
        {
            var L = new Light { Lx = lx, Ly = ly, Lz = lz, Source = source,
                                ColR = colR, ColG = colG, ColB = colB, FogA = (float)volumeFog * (1f / 255f) };
            float r = WorldVolumetricRadius(volumeRadius);
            L.R = r; L.R2 = r * r; L.InvR = 1f / r; L.InvR2 = L.InvR * L.InvR;
            L.L2 = (lz * lz + ly * ly) + lx * lx;
            // fild VolumeBrightness; fmul [+0x3c]; fmul [0x10b3868c]; fstp [+0x6c] (disasm 0x10b07758..0x10b07764, PC24).
            L.K = ((float)volumeBrightness * b3c) * 0.015625f;
            L.Inside = L.L2 < L.R2;
            BuildRamp(L.Ramp, colR, colG, colB, (float)volumeFog * (1f / 255f));
            return L;
        }

        // The fog record of a frame light (ComputeFromActor thunk_FUN_10b06920), shared by the surface fog maps
        // (LevelFogMaps) and actor vertex fog (ActorFogFrame). The type function scales +0x3c = B·(1/255), never the
        // byte (FUN_10b06810, then the clamp 0x10b068a6..0x10b068ce); colour = TypeColour·(Level.Brightness·+0x3c)
        // (0x10b06b0c..0x10b06b2d); K reads the same +0x3c (MakeLight).
        public static Light FromSnapshot(in ActorSnapshot a, float lx, float ly, float lz, float levelBrightness)
        {
            LightState L = a.Light;
            SurfLightmap.Light SL = MoverMode3.FromSnapshot(in a).L;
            float b3c = SurfLightmap.LightBrightness(in SL);
            float f = levelBrightness * b3c;
            UEVector hsv = SurfLightmap.TypeColour(in SL);
            return MakeLight(lx, ly, lz, L.VolumeRadius, L.VolumeBrightness, b3c, L.VolumeFog,
                             f * hsv.x, f * hsv.y, f * hsv.z, L.Source);
        }

        // :435-470 — 16.16 fixed point: channel i = min(127, (i·step)>>16),
        // step = ROUND(c·65536 − 0.5). Bytes [0]=B (from col B), [1]=G, [2]=R, [3]=A.
        public static void BuildRamp(byte[] ramp, float colR, float colG, float colB, float fog)
        {
            int sr = RoundEven(colR * 65536f - 0.5f), sg = RoundEven(colG * 65536f - 0.5f);
            int sb = RoundEven(colB * 65536f - 0.5f), sa = RoundEven(fog * 65536f - 0.5f);
            int r = 0, g = 0, b = 0, a = 0;
            for (int i = 0; i < 256; i++)
            {
                ramp[i * 4 + 2] = (byte)Math.Min(0x7f, r >> 16);
                ramp[i * 4 + 1] = (byte)Math.Min(0x7f, g >> 16);
                ramp[i * 4 + 0] = (byte)Math.Min(0x7f, b >> 16);
                ramp[i * 4 + 3] = (byte)Math.Min(0x7f, a >> 16);
                r += sr; g += sg; b += sb; a += sa;
            }
        }

        // Light fog density along the camera→texel ray T (camera coordinates). outside — global DAT_10b658d8 (the
        // "previous texel outside the sphere" hysteresis switches the test form). vertex — LightAndFog's point, else a
        // SetupForSurf texel; blend — a texel of a later (blending) record. Sums in the disassembly's order: dot (Z·Lz + Y·Ly) + X·Lx (SetupForSurf inline
        // 0x10b081bf / 0x10b08563; LightAndFog Core FVector::operator| [HP1 Core 0x10119190]); |T|² of a texel
        // (X² + Z²) + Y² (0x10b081f8 / 0x10b0859f), of the by-depth record and of a vertex Core FVector::SizeSquared
        // [HP1 Core 0x10119700] (X² + Y²) + Z²; HP2's blend loop alone sums a texel's |T|² as (Z² + Y²) + X²
        // [HP2 0x10b08114..0x10b0812e] (its overwrite loop, LightAndFog and Core's two bodies are HP1's). The clamp to 1
        // compares the bits unsigned (0x10b083bd): −0 gives 1.
        // Judge: se_harness/bsp_diff/validate_fogmap.py (both bodies under unicorn at PC24).
        // Original: Render.dll not exported: FLightManager::SetupForSurf FUN_10b077f0 [HP1 0x10b077f0] [HP2 0x10b07380] (FLightManagerBase vtable 0x10b386d4 slot 3; texel loops 0x10b08115..0x10b0886b)
        // Original: Render.dll not exported: FLightManager LightAndFog FUN_10b02b10 [HP1 0x10b02b10] [HP2 0x10b02b10] (vtable 0x10b386d4 slot 6; fog loop 0x10b0303e..0x10b03407)
        public static float Density(Light L, float tx, float ty, float tz, ref int outside, bool vertex = false,
            bool blend = false)
        {
            float size2 = (tx * tx + ty * ty) + tz * tz;
            if (L.Source == 2)
            {
                float v = (L.InvR * L.K) * FastSqrt(size2);
                return (uint)BitConverter.SingleToInt32Bits(v) > 0x3f7fffffu ? 1f : v;
            }
            float dot = (tz * L.Lz + ty * L.Ly) + tx * L.Lx;
            if (!L.Inside && (uint)BitConverter.SingleToInt32Bits(dot) >= 0x80000000u) return 0f;
            float t2 = vertex ? size2
                : blend && UEGameProfile.Current.Game == UEEdition.Game.HP2 ? (tz * tz + ty * ty) + tx * tx
                : (tx * tx + tz * tz) + ty * ty;
            if (outside != 0 && !(t2 * L.L2 - dot * dot <= t2 * L.R2)) return 0f;
            float d2 = L.L2 - (dot * dot) / t2;
            if (!(d2 <= L.R2)) { outside = 1; return 0f; }
            outside = 0;
            float depth = FastSqrt(t2);
            float tc = dot / depth;
            float h = FastSqrt(L.R2 - d2);
            float tcd = tc - depth;
            float lo, hi; bool full = false;
            if (tc <= h) { lo = -h; hi = tc; if (-h <= tcd) lo = tcd; }
            else if (-h <= tcd) { lo = tcd; hi = h; }
            else { full = true; lo = -h; hi = h; }
            if (!(lo < hi)) return 0f;
            // b, F and v2 stay in the PC24 register up to v2's sign test and store (0x10b08336..0x10b083ad): 24-bit
            // mantissa, wide exponent. On the sphere's edge (d2 == R2: h = FastSqrt(0), F = 0) v2 = −2·b³·K is about
            // 1e-63, negative in the register (no fog) where a float chain underflows to −0 (which the clamp makes 1).
            static double R(double x) => HP1.Core.X87Ext.Round24(x);
            double b = R((double)hi * L.InvR);
            double F = R(3.0 - R((double)(d2 * L.InvR2) * 3.0));
            double v2;
            if (full) { v2 = R(R(R(F - R(b * b)) * b) * L.K); v2 = R(v2 + v2); }
            else
            {
                double a = R((double)lo * L.InvR);
                v2 = R(R(R(R(F - R(b * b)) * b) - R(R(F - R(a * a)) * a)) * L.K);
            }
            if (!(0.0 <= v2)) return 0f;
            float stored = (float)v2;
            return (uint)BitConverter.SingleToInt32Bits(stored) > 0x3f7fffffu ? 1f : stored;
        }

        // ACTOR VERTEX FOG — FLightManager::LightAndFog, PF_RenderFog branch
        // (Render.dll.c:16990-17110). For vertex T (camera coordinates), over the
        // actor's volumetric lights IN ITS ORDER: density by the same formula as
        // the texel (:16997-17075 repeat :20035-20120 verbatim, including the
        // DAT_10b658d8 hysteresis), then
        //   f      = f + f                                  (:17077)
        //   a      = f·[0x2a]  (VolumeFog/255), capped at 1 (:17083-17086)
        //   rgb    = min(1, (1−a)·rgb + f·[0x27..0x29])     (:17087-17104)
        //   A      = min(1, A + a)                          (:17106-17110)
        // Shipped compares against one unsigned on the float bits
        // ((uint)x < 0x3f800000) — kept as is.
        public static void VertexFog(IReadOnlyList<Light> lights, float tx, float ty, float tz,
            ref int outside, ref float r, ref float g, ref float b, ref float a)
        {
            for (int i = 0; i < lights.Count; i++)
            {
                Light L = lights[i];
                float f = Density(L, tx, ty, tz, ref outside, vertex: true);
                f = f + f;
                if (f == 0f) continue;
                float fa = f * L.FogA;
                if (!((uint)BitConverter.SingleToInt32Bits(fa) < 0x3f800000u)) fa = 1f;
                float ia = 1f - fa;
                float v = ia * r + f * L.ColR; r = (uint)BitConverter.SingleToInt32Bits(v) < 0x3f800000u ? v : 1f;
                v = ia * g + f * L.ColG; g = (uint)BitConverter.SingleToInt32Bits(v) < 0x3f800000u ? v : 1f;
                v = ia * b + f * L.ColB; b = (uint)BitConverter.SingleToInt32Bits(v) < 0x3f800000u ? v : 1f;
                v = fa + a; a = (uint)BitConverter.SingleToInt32Bits(v) > 0x3f7fffffu ? 1f : v;
            }
        }

        // Full pass over the width×height grid: start and steps in camera
        // coordinates (shipped transforms start/steps, not every texel:
        // :20020-20036), accumulated by addition along row/column (:20130-20137).
        // lights — already sorted by camera Z DESCENDING (0x10b077c0).
        // dest — BGRA7, 4 bytes per texel, stride = width.
        public static void Build(IReadOnlyList<Light> lights, int width, int height,
            float sx, float sy, float sz, float dux, float duy, float duz,
            float dvx, float dvy, float dvz, byte[] dest, ref int outside)
        {
            bool first = true;
            for (int li = 0; li < lights.Count; li++)
            {
                Light L = lights[li];
                float rx = sx, ry = sy, rz = sz;
                for (int y = 0; y < height; y++)
                {
                    float tx = rx, ty = ry, tz = rz;
                    for (int x = 0; x < width; x++)
                    {
                        float f = Density(L, tx, ty, tz, ref outside, blend: !first);
                        int o = (y * width + x) * 4;
                        if (first)
                        {
                            int i = RoundEven(f * 255f) * 4;
                            dest[o] = L.Ramp[i]; dest[o + 1] = L.Ramp[i + 1];
                            dest[o + 2] = L.Ramp[i + 2]; dest[o + 3] = L.Ramp[i + 3];
                        }
                        else if (f != 0f)
                        {
                            int i = RoundEven(f * 255f) * 4;
                            int a = L.Ramp[i + 3];
                            dest[o]     = (byte)Math.Min(0x7f, _blend[a * 128 + dest[o]]     + L.Ramp[i]);
                            dest[o + 1] = (byte)Math.Min(0x7f, _blend[a * 128 + dest[o + 1]] + L.Ramp[i + 1]);
                            dest[o + 2] = (byte)Math.Min(0x7f, _blend[a * 128 + dest[o + 2]] + L.Ramp[i + 2]);
                            dest[o + 3] = (byte)Math.Min(0x7f, dest[o + 3] + a);
                        }
                        tx += dux; ty += duy; tz += duz;
                    }
                    rx += dvx; ry += dvy; rz += dvz;
                }
                first = false;
            }
            if (lights.Count == 0) Array.Clear(dest, 0, width * height * 4);
        }

        // Sort records by camera Z descending — appQsort (msvcrt qsort, MsvcQsort) with comparator 0x10b077c0:
        // fcomp a.z, b.z; 1 when a.z < b.z, otherwise −1 (never 0), so equal Z move as msvcrt moves them. The input
        // order is the port's record order (frame actors), not shipped's record array: it only matters for an
        // exactly equal camera z.
        public static void SortFarFirst(List<Light> lights)
            => HP1.Core.MsvcQsort.Sort(lights, (a, b) => a.Lz < b.Lz ? 1 : -1);
    }
}
