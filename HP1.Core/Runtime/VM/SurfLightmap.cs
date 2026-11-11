using System;
using System.Collections.Generic;
using System.Threading;

namespace HP1.UEScript
{
    // SURFACE LIGHTMAP FILL BY ONE LIGHT — shipped FLightManager,
    // slot 3 of SetupForSurf (Render.dll.c:19726-20700) and its fillers
    // FUN_10b037c0 (:17298-17439, LightRadiusInner = 0) / FUN_10b0ca70
    // (:21639-21766, otherwise). Proven texel-for-texel against shipped under unicorn
    // (replica se_harness/bsp_diff/lm_filler_replica.py). Single owner of the formula (C7):
    // the level atlas bake and the per-frame rebuild of mode-3 mover lightmaps
    // (`bDynamicLightMover`) both call here.
    //
    // ALL float32/double ROUNDING POINTS follow the decompile; do not change the
    // shape of the expressions without re-verifying against shipped.
    // PRECISION: in game slot 3 runs inside the PreRender..PostRender window, where
    // appEnableFastMath(1) keeps x87 at PC24 (Render.dll.c:15787/28920,
    // Engine.dll.c:117945-117957). The radial body and basis below are modelled
    // and proven at PC64 (double); measured on the same cases at PC24 —
    // 0 differences (validate_mover_mode3_fill, validate_lightmap_effects
    // --pc64), i.e. the sampling is precision-insensitive. Frame effects
    // (FillWave, Merge, LightBrightness) are ported at PC24 (float).
    //
    // Coordinates are UE (as in package data). Accumulator — W×H lightmap texels
    // in its own coordinates (i — column, j — row), each channel 0..127
    // (float, integer values); seed — zone ambient (AmbientSeed), merged per
    // light IN LIST ORDER (saturation at 127 / for bDarkLight a packed
    // subtraction with borrow between bytes and floor 0, Render.dll.c:
    // 17260-17280, disasm 0x10b0365f-0x10b03689 — see Merge).
    public static class SurfLightmap
    {
        public struct Surface { public UEVector B, U, V, N; }
        public struct Map { public float PanX, PanY, UScale, VScale; public int Width, Height; }
        public struct Light
        {
            public string Name;
            public UEVector Pos;
            public float DrawScale;
            public int Pitch, Yaw, Roll;
            public byte Type, Effect, Brightness, Hue, Saturation, Radius, RadiusInner, Cone;
            public bool DarkLight;
            public int Source;          // KnowWonder ELightSource: 0 LD_Point, 1 LD_Plane, 2 LD_Ambient
            // Brightness type-function multiplier (LightState.Scale, computed by
            // WorldFrameProjector.LightAnim — table 0x10b38288 by LightType).
            // Shipped GlobalLighting (FUN_10b06810, R:19076-19105) multiplies
            // brightness B·(1/255) by it BEFORE the [0,1] clamp; see LightColour. 1 — Steady/
            // static bake (multiplying by 1 is exact).
            public float BrightnessScale;
            // LightPeriod (+0x1e7) and LightPhase (+0x1e8): read by the own
            // fillers SlowWave/Interference (0x10b04388/0x10b043cd).
            public byte Period, Phase;
        }

        // FRAME STATE THAT SHIPPED READS INSIDE SetupForSurf. The lightmap
        // is built during URender::DrawWorld, i.e. AFTER Render->PreRender
        // of this frame (Engine.dll.c:117945 PreRender -> :117957 DrawWorld) —
        // the FGlobalRandoms tables have already ticked this frame.
        //   RandomBase / RandomRand — FGlobalRandoms Base 0x10b6696c / Rand
        //     0x10b66d6c (UEGlobalRandoms.Base / .Rand), 256 each;
        //   TimeSeconds — Level->Actors(0).TimeSeconds: SetupForSurf stores
        //     Actors(0) in DAT_10b4c820 (R:19842), fillers read +0x3b4;
        //   LevelBrightness — the light's Actor.Level(+0x7c)->Brightness(+0x44c)
        //     (R:19276).
        public sealed class FrameFx
        {
            public float[] RandomBase, RandomRand;
            public float TimeSeconds;
            public float LevelBrightness;
            // UClient.NoDynamicLights ([WinDrv.WindowsClient], UEViewportStartup.ClientNoDynamicLights) — read by the
            // categoriser through Frame->Viewport->Outer (MoverMode3.Categorize).
            public bool NoDynamicLights;
        }
        // Lightmap basis: start and steps in world space (FCoords::Inverse, Core.dll.c:3441-3492;
        // rounding points per Render.dll.c:19312-19369).
        public struct Basis
        {
            public bool Degenerate;
            public float StartX, StartY, StartZ, UsX, UsY, UsZ, VsX, VsY, VsZ;
        }

        // Shadow mask when ABSENT (the mode-3 mover branch does not supply
        // ELEM_SHADOW): shipped fills the mask buffer with byte 0x7f
        // (FUN_10b02970, Render.dll.c:16773 `appMemset(param_3, 0x7f, …)`), i.e.
        // nominal illumination 127 versus 255 for a fully unshadowed texel of a
        // baked shadow (3×3 expander: Σ=320 → ·255/320).
        public const int NoShadowMask = 0x7f;

        const float EffectRadiusScale = 1.0499999523162842f;   // 0x10b38698: 1.05·R², 5 % margin

        // Built in the type initializer: lightmap bodies run on more than one thread (UELevelRuntime.RunEngineFrame).
        static readonly float[] _tableH = new float[4096], _tableHV = new float[4096];
        static readonly bool _falloffBuilt = BuildFalloffTables();
        static bool BuildFalloffTables()
        {
            float[] h = _tableH, hv = _tableHV;
            for (int i = 0; i < 4096; i++)
            {
                double v = Math.Sqrt((double)((float)(i + 1) * (1f / 4096f)));
                double hh = ((v * v * v) + (v * v * v)) - v * v * 3.0 + 1.0;
                h[i] = (float)hh; hv[i] = (float)(hh / v);
            }
            return true;
        }

        public static UEVector FGetHSV(int h, int s, int b)
        {
            float x = b * 0.005490196f;                       // 1.4/255
            float lum = x > 0f ? Clamp01(0.7f * x / ((float)Math.Sqrt(x) + 0.01f)) : 0f;
            float wr, wg, wb;
            if (h < 0x56)      { wb = 0f; wr = (85 - h) * 0.011764706f; wg = h * 0.011764706f; }
            else if (h < 0xab) { wr = 0f; wg = (170 - h) * 0.011764706f; wb = (h - 85) * 0.011764706f; }
            else               { wg = 0f; wr = (h - 170) * 0.011764706f; wb = (255 - h) * 0.011904762f; }
            float sat = s * 0.003921569f;
            return new UEVector(((1f - wr) * sat + wr) * lum,
                                ((1f - wg) * sat + wg) * lum,
                                ((1f - wb) * sat + wb) * lum);
        }
        static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        // Ambient seed: ROUND(c·64 − 0.5), floored at 0 per channel.
        public static UEVector AmbientSeed(int h, int s, int b)
        {
            UEVector raw = FGetHSV(h, s, b);
            return new UEVector(
                MathF.Max(RoundEven(raw.x * 64f - 0.5f), 0f),
                MathF.Max(RoundEven(raw.y * 64f - 0.5f), 0f),
                MathF.Max(RoundEven(raw.z * 64f - 0.5f), 0f));
        }

        public static float RoundEven(float v) => (float)Math.Round((double)v, MidpointRounding.ToEven);
        public static int RoundHalfEven(float v) => (int)Math.Round((double)v, MidpointRounding.ToEven);
        public static byte EncodeChan(float color)
        {
            int r = RoundHalfEven(color);
            return (byte)(r < 0 ? 0 : (r > 127 ? 127 : r));
        }

        public static Basis ComputeBasis(in Surface s, in Map m)
        {
            var r = new Basis();
            double ux = s.U.x, uy = s.U.y, uz = s.U.z;
            double vx = s.V.x, vy = s.V.y, vz = s.V.z;
            double nx = s.N.x, ny = s.N.y, nz = s.N.z;
            double basisDet = (vy * nz - ny * vz) * ux + (ny * vx - vy * nx) * uz + (vz * nx - vx * nz) * uy;
            r.Degenerate = basisDet == 0.0;
            float rdet = r.Degenerate ? 0f : (float)(1.0 / basisDet);
            float Xx = (float)((nz * vy - vz * ny) * rdet), Xy = (float)((vz * nx - nz * vx) * rdet), Xz = (float)((ny * vx - vy * nx) * rdet);
            float Yx = (float)((uz * ny - nz * uy) * rdet), Yy = (float)((nz * ux - uz * nx) * rdet), Yz = (float)((nx * uy - ux * ny) * rdet);
            float panX = m.PanX, panY = m.PanY;
            float s0x = (float)((double)panX * Xx + s.B.x);
            float s0y = (float)((double)(float)((double)panX * Xy) + s.B.y);
            float s0z = (float)((double)panX * Xz + s.B.z);
            r.StartX = (float)((double)s0x + (float)((double)panY * Yx));
            r.StartY = (float)((double)s0y + (float)((double)panY * Yy));
            r.StartZ = (float)((double)s0z + (float)((double)panY * Yz));
            r.UsX = (float)((double)m.UScale * Xx); r.UsY = (float)((double)m.UScale * Xy); r.UsZ = (float)((double)m.UScale * Xz);
            r.VsX = (float)((double)m.VScale * Yx); r.VsY = (float)((double)m.VScale * Yy); r.VsZ = (float)((double)m.VScale * Yz);
            return r;
        }

        // Light radius in world space: AActor::WorldLightRadius (one body, WorldFrameProjector.WorldLightRadius).
        public static float WorldRadius(in Light L) => WorldFrameProjector.WorldLightRadius(L.DrawScale, L.Radius);

        // FILLER BY LightEffect — table 0x10b38110 (3 dwords per
        // effect, read from the image; 0x10b01xxx thunks resolved
        // by the disassembler):
        //   0,1,2,3,7,11,15,19 -> 0x10b037c0 (radial; when inner != 0 —
        //     0x10b0ca70), 8,12 -> 0x10b05710 (Spot), 13 -> 0x10b05dc0
        //     (NonIncidence), 17 -> 0x10b05ae0 (Cylinder), 5 -> 0x10b04360
        //     (SlowWave), 16 -> 0x10b05330 (Interference);
        //   NOT PORTED (loud refusal): 4 -> 0x10b03bc0, 6 -> 0x10b04700,
        //     9 -> 0x10b04aa0, 10 -> 0x10b04ea0, 18 -> 0x10b03f80, 14 -> 0x10b06090
        //     (Shell). Census (41 maps, --dump-lightworld + defaults of
        //     363 placed classes + .uc defaults): NO carriers of 4/6/9/10/14/18;
        //     5 — 42 lights (Lev2_fire1/Fire2, Lev3_Dungeon, Lev3_Quid2,
        //     Lev4_Sneak2), 16 — 3 (Lev2_HogFront*).
        // Effects 1/2/3 use the radial body but are modulated per texel
        // during merge (Merge, FUN_10b03430).
        enum Filler { Radial, Spot, NonIncidence, Cylinder, SlowWave, Interference, Unported }

        static Filler FillerFor(byte effect)
        {
            switch (effect)
            {
                case 0: case 1: case 2: case 3: case 7: case 11: case 15: case 19: return Filler.Radial;
                case 8: case 12: return Filler.Spot;
                case 13: return Filler.NonIncidence;
                case 17: return Filler.Cylinder;
                case 5: return Filler.SlowWave;
                case 16: return Filler.Interference;
                default: return Filler.Unported;
            }
        }

        static string UnportedFiller(byte effect)
        {
            switch (effect)
            {
                case 4: return "0x10b03bc0 (Searchlight)";
                case 6: return "0x10b04700 (FastWave)";
                case 9: return "0x10b04aa0 (Shock)";
                case 10: return "0x10b04ea0 (Disco)";
                case 14: return "0x10b06090 (Shell)";
                case 18: return "0x10b03f80 (Rotor)";
                default: return "outside table 0x10b38110 (shipped -> writes 0, R:19262; not present in the data)";
            }
        }

        // Whether the LightEffect has a ported filler. Effects that need
        // frame state (NeedsFrame) are also "supported", but FillLight
        // refuses without FrameFx.
        public static bool EffectSupported(byte effect, out bool nonIncidence, out bool spot, out bool cylinder)
        {
            Filler f = FillerFor(effect);
            nonIncidence = f == Filler.NonIncidence;
            spot = f == Filler.Spot;
            cylinder = f == Filler.Cylinder;
            return f != Filler.Unported;
        }

        // The effect reads frame state: per-texel modulation 1/2/3
        // (FGlobalRandoms tables) or time/phase of fillers 5/16.
        public static bool NeedsFrame(byte effect) => effect == 1 || effect == 2 || effect == 3 || effect == 5 || effect == 16;

        // INVERSE SQRT VIA GMath TABLES — as built by the static
        // FGlobalMath constructor (Core.dll.c:47319, tail of FUN_1014e350(-0.5)):
        // exponent — 512 entries pow(|float(i<<23)|, -0.5), mantissa — 1024
        // entries pow(1.m, -0.5) in steps of 0x2000 bits; NaN -> 0. Lookup —
        // Mant[(bits>>13)&0x3ff]·Exp[bits>>23] (Render.dll FUN_10b05710).
        // Tables are built once, before the first call (also called from parallel actor lighting,
        // UEActorLighting.PointNormals — Render.dll builds the same tables, FUN_10b027d0(-0.5)).
        static readonly float[] _rsqrtExp = BuildRsqrtExp(), _rsqrtMant = BuildRsqrtMant();
        static float[] BuildRsqrtExp()
        {
            var e = new float[512];
            for (int i = 0; i < 512; i++)
            {
                float f = BitConverter.Int32BitsToSingle(unchecked((int)((uint)i << 23)));
                if (f < 0f) f = -f;
                float v = (float)Math.Pow(f, -0.5);
                e[i] = float.IsNaN(v) ? 0f : v;
            }
            return e;
        }
        static float[] BuildRsqrtMant()
        {
            var m = new float[1024];
            for (int i = 0; i < 1024; i++)
            {
                float f = BitConverter.Int32BitsToSingle(0x3f800000 + i * 0x2000);
                float v = (float)Math.Pow(f, -0.5);
                m[i] = float.IsNaN(v) ? 0f : v;
            }
            return m;
        }
        internal static float GMathRsqrt(float x)
        {
            uint b = unchecked((uint)BitConverter.SingleToInt32Bits(x));
            return _rsqrtMant[(b >> 13) & 0x3ff] * _rsqrtExp[b >> 23];
        }
        // Light rectangle in the lightmap (FLightInfo::ComputeFromActor, disasm 0x10b0719e-0x10b073df, PC24 = per-op
        // float). Surface record [0x10b5f688]: +0 Base, +0xc X (U), +0x18 Y (V), +0x24 Z (Normal) — read RAW, no
        // normalisation. Every dot product is summed (y + z) + x (0x10b071a1-0x10b071c3, 0x10b07258-0x10b072a1).
        //   perp  = (L − B)·N                                              -> [ebp+8]
        //   t     = R·R·1.05 − perp·perp; t < 0 (or unordered) -> 0         (0x10b071ce-0x10b071f4)
        //   m     = FastSqrt(t) — the table 0x10b4ca44, ALWAYS (0 gives the table's first word, not 0)
        //   P     = (L − perp·N) − B; cu = P·U − PanX, cv = P·V − PanY
        //   hu    = FastSqrt((Ux·Ux + Uy·Uy) + Uz·Uz)·m, hv the same with V  (0x10b072aa-0x10b07344)
        //   col0 = max(fistp((cu − hu)/UScale), 0), row0 = max(fistp((cv − hv)/VScale), 0),
        //   col1 = min(fistp((hu + cu)/UScale), Width), row1 = min(fistp((hv + cv)/VScale), Height).
        public static bool Rect(in Surface s, in Map m, in Light L, float radius,
                                out int col0, out int col1, out int row0, out int row1)
        {
            UEVector n = s.N, u = s.U, v = s.V;
            float rx = L.Pos.x - s.B.x, ry = L.Pos.y - s.B.y, rz = L.Pos.z - s.B.z;
            float perp = (ry * n.y + rz * n.z) + rx * n.x;
            float rectT = radius * radius * EffectRadiusScale - perp * perp;
            float rectM = FogMap.FastSqrt(rectT < 0f || float.IsNaN(rectT) ? 0f : rectT);
            float px = L.Pos.x - perp * n.x - s.B.x, py = L.Pos.y - perp * n.y - s.B.y, pz = L.Pos.z - perp * n.z - s.B.z;
            float rectCu = ((py * u.y + pz * u.z) + px * u.x) - m.PanX;
            float rectCv = ((py * v.y + pz * v.z) + px * v.x) - m.PanY;
            float hu = FogMap.FastSqrt((u.x * u.x + u.y * u.y) + u.z * u.z) * rectM;
            float hv = FogMap.FastSqrt((v.x * v.x + v.y * v.y) + v.z * v.z) * rectM;
            col0 = Math.Max(RoundHalfEven((rectCu - hu) / m.UScale), 0);
            col1 = Math.Min(RoundHalfEven((hu + rectCu) / m.UScale), m.Width);
            row0 = Math.Max(RoundHalfEven((rectCv - hv) / m.VScale), 0);
            row1 = Math.Min(RoundHalfEven((hv + rectCv) / m.VScale), m.Height);
            return col0 < col1 && row0 < row1;
        }

        static uint Fistp(double x)
        {
            double r = Math.Round(x, MidpointRounding.ToEven);
            if (r >= 2147483648.0 || r < -2147483648.0 || double.IsNaN(r)) return 0x80000000u;
            return unchecked((uint)(int)r);
        }

        // ROUND(m·a·b) as a single x87 product: exact integer mantissa
        // arithmetic (double float rounding would give ±1 otherwise).
        static int RoundProduct3(int m, float a, float b)
        {
            if (m == 0 || a == 0f || b == 0f) return 0;
            int ba = BitConverter.SingleToInt32Bits(a);
            int bb = BitConverter.SingleToInt32Bits(b);
            int ea = ((ba >> 23) & 0xff), eb = ((bb >> 23) & 0xff);
            if (ea == 0 || eb == 0 || ea == 0xff || eb == 0xff)
                throw new InvalidOperationException($"RoundProduct3: denormal/inf in a factor ({a}, {b})");
            long ma = (ba & 0x7fffff) | 0x800000, mb = (bb & 0x7fffff) | 0x800000;
            long prod = (long)m * ma * mb;
            int e = (ea - 150) + (eb - 150);
            bool neg = ((ba ^ bb) & unchecked((int)0x80000000)) != 0;
            long q;
            if (e >= 0)
            {
                if (e > 62 - 56) throw new InvalidOperationException("RoundProduct3: overflow");
                q = prod << e;
            }
            else
            {
                int sh = -e;
                if (sh >= 63) q = 0;
                else
                {
                    q = prod >> sh;
                    long rem = prod & ((1L << sh) - 1), half = 1L << (sh - 1);
                    if (rem > half || (rem == half && (q & 1) == 1)) q++;
                }
            }
            if (q > int.MaxValue) throw new InvalidOperationException("RoundProduct3: result outside int32");
            return neg ? -(int)q : (int)q;
        }

        // LIGHT COLOUR — FLightInfo::ComputeFromActor (FUN_10b06920) with
        // GlobalLighting (FUN_10b06810, via GRender->vtbl[0x8c]):
        //   br = (float)LightBrightness · (1/255)          R:19258, 0x10b3869c
        //   type function by LightType (table 0x10b38288, disasm
        //     0x10b06370..0x10b06805): 0 -> br = 0; 1, 6 and >= 10 -> unchanged;
        //     2/3/4/5/7 -> br·factor (factor = BrightnessScale,
        //     computed by WorldFrameProjector.LightAnim); 8/9 (TexturePalette)
        //     change the COLOUR from the texture palette — NOT ported, refusal;
        //   clamp: !(br >= 0) -> 0, !(br < 1) -> 1       disasm 0x10b068a6-0x10b068ce
        //   f = Level.Brightness · br; colour = f · FGetHSV(Hue, Sat, 0xff)   R:19276
        // A zero call flag (ShowFlags bit 11, R:19275) forces type 1;
        // the port has only the game viewport, where it is set (ShowFlags = 0x480c,
        // UELevelRuntime.Bootstrap), so the "flag = 0" (editor) branch is not
        // ported.
        //
        // BAKED PATH WITHOUT A FRAME (fx == null — the atlas bake, which bakes
        // ONLY category 0, see LevelSurfLightmap; the runtime — mode-3 movers
        // and per-frame level lightmaps — passes the Core frame
        // UEWorldFrameProducer.LightmapFx): there is no source for Level.Brightness,
        // so the multiplier is NOT applied. This is a declared assumption, not a derivation:
        // census — LevelInfo.Brightness = 1.0 in all 41 maps, and no
        // .uc script writes Level.Brightness. Effects that need the frame
        // (NeedsFrame) are not drawn at all without fx — FillLight refuses.
        const float InvByte255 = 0.003921568859368563f;   // 0x10b3869c (bits 0x3b808081)

        // Brightness after GlobalLighting (FUN_10b06810): B·(1/255), type function,
        // clamp [0,1] — the value shipped stores in FLightInfo+0x3c.
        public static float LightBrightness(in Light L)
        {
            float br = (float)L.Brightness * InvByte255;
            if (L.Type == 0) br = 0f;
            else if (L.Type == 8 || L.Type == 9)
                throw new InvalidOperationException($"{L.Name}: LightType {L.Type} (TexturePalette, 0x10b06590/0x10b066d0) " +
                    "takes its color from the texture palette — not ported (no carriers in the campaign, census 2026-09-25)");
            else br = L.BrightnessScale * br;
            if (!(br >= 0f)) br = 0f;
            else if (!(br < 1f)) br = 1f;
            return br;
        }

        public static UEVector LightColour(in Light L, FrameFx fx)
        {
            float br = LightBrightness(in L);
            float f = fx != null ? fx.LevelBrightness * br : br;
            UEVector hsv = FGetHSV(L.Hue, L.Saturation, 255);
            return new UEVector(f * hsv.x, f * hsv.y, f * hsv.z);
        }

        [ThreadStatic] static byte[] _illum;
        // Executed-branch counters for fixtures (V8): texels that went through
        // merge per-texel modulation, and texels of own fillers
        // 5/16, and lit texels of the spatial fillers Spot (8/12), NonIncidence (13), Cylinder (17).
        // Not game state; not accessible to the frontend (internal). Each body counts locally and adds once
        // (Interlocked): lightmap bodies run on more than one thread (UELevelRuntime.RunEngineFrame).
        internal static long ModulatedTexels, WaveTexels, SpotTexels, NonIncidenceTexels, CylinderTexels;

        // Contribution of ONE light to accumulator acc (W×H, channels 0..127).
        // mask — expanded W×H shadow mask (byte 0..255 as float) or null:
        // then every texel sees NoShadowMask (see above).
        // fx — frame state (FrameFx); required for NeedsFrame effects.
        // Returns false if the light does not touch the lightmap (empty rectangle).
        //
        // Order as in shipped: the filler writes an illumination byte into the light
        // rectangle buffer (0 where there is no contribution), then merge
        // FUN_10b03430 adds the buffer into the accumulator (Merge).
        public static bool FillLight(UEVector[] acc, in Surface s, in Map m, in Basis bs, in Light L, float[] mask,
                                     FrameFx fx = null)
        {
            if (bs.Degenerate) return false;
            Filler kind = FillerFor(L.Effect);
            if (kind == Filler.Unported)
                throw new InvalidOperationException($"{L.Name}: LightEffect {L.Effect} — its own filler " +
                    $"{UnportedFiller(L.Effect)} is not ported (census 2026-09-25: no carriers in the campaign)");
            if (NeedsFrame(L.Effect) && fx == null)
                throw new InvalidOperationException($"{L.Name}: LightEffect {L.Effect} reads frame state " +
                    "(FGlobalRandoms / TimeSeconds / LightPhase), but FrameFx not passed — a static bake of this " +
                    "light would be a substitute for the per-frame lightmap");
            bool nonIncidence = kind == Filler.NonIncidence, spot = kind == Filler.Spot, cylinder = kind == Filler.Cylinder;
            float radius = WorldRadius(in L);
            float inner = L.RadiusInner * (1f / 256f) * radius;
            UEVector lightColor = LightColour(in L, fx);
            int stepR = RoundHalfEven(lightColor.x * 65536f - 0.5f);
            int stepG = RoundHalfEven(lightColor.y * 65536f - 0.5f);
            int stepB = RoundHalfEven(lightColor.z * 65536f - 0.5f);
            if (!Rect(in s, in m, in L, radius, out int col0, out int col1, out int row0, out int row1)) return false;

            float invR = 1f / radius;
            float field44; float[] table;
            if (L.Source == 0)
            {
                float dLx = (float)((double)L.Pos.x - s.B.x);
                float dLy = (float)((double)L.Pos.y - s.B.y);
                float dLz = (float)((double)L.Pos.z - s.B.z);
                double dn = (double)dLx * s.N.x + (double)dLy * s.N.y + (double)dLz * s.N.z;
                field44 = (float)(Math.Abs(dn) * invR);
                table = _tableHV;
            }
            else if (L.Source == 1)
            {
                var f = UEPawnMovement.GMathForward(new UERotator(L.Pitch, L.Yaw, L.Roll));
                field44 = MathF.Max(-(f.x * s.N.x + f.y * s.N.y + f.z * s.N.z), 0f);
                table = _tableH;
            }
            else if (L.Source == 2) { field44 = 1f; table = _tableH; }
            else throw new InvalidOperationException($"{L.Name}: LightSource {L.Source} — shipped has only three branches (0/1/2)");

            int W = m.Width;
            int rw = col1 - col0, rh = row1 - row0;
            byte[] illum = _illum;
            if (illum == null || illum.Length < rw * rh) _illum = illum = new byte[Math.Max(rw * rh, 1024)];
            Array.Clear(illum, 0, rw * rh);

            if (kind == Filler.SlowWave || kind == Filler.Interference)
            {
                FillWave(illum, rw, W, in bs, in L, radius, inner, field44, table, mask,
                         col0, col1, row0, row1, fx, kind == Filler.Interference);
                Merge(acc, W, illum, col0, col1, row0, row1, stepR, stepG, stepB, L.DarkLight, L.Effect, fx);
                return true;
            }

            float local14 = (float)(((double)field44 * radius) / ((double)radius - inner));

            float startX = bs.StartX, startY = bs.StartY, startZ = bs.StartZ;
            float usx = bs.UsX, usy = bs.UsY, usz = bs.UsZ, vsx = bs.VsX, vsy = bs.VsY, vsz = bs.VsZ;
            float L0x = (float)((double)startX - L.Pos.x), L0y = (float)((double)startY - L.Pos.y), L0z = (float)((double)startZ - L.Pos.z);
            float c0 = col0, r0 = row0;
            float L1x = (float)((double)L0x + (float)((double)usx * c0)), L1y = (float)((double)L0y + (float)((double)usy * c0)), L1z = (float)((double)L0z + (float)((double)usz * c0));
            float Dx = (float)((double)L1x + (float)((double)vsx * r0)), Dy = (float)((double)L1y + (float)((double)vsy * r0)), Dz = (float)((double)L1z + (float)((double)vsz * r0));
            float sAcc = (float)((((double)invR * 4095.0) * invR) * 4095.0);
            uint bb0 = Fistp(((double)Dx * Dx + (double)Dy * Dy + (double)Dz * Dz) * sAcc);
            uint bb4 = Fistp(((double)Dx * usx + (double)usy * Dy + (double)usz * Dz) * sAcc);
            uint bb8 = Fistp(((double)Dx * vsx + (double)vsy * Dy + (double)vsz * Dz) * sAcc);
            uint bbc = Fistp(((double)usx * usx + (double)usy * usy + (double)usz * usz) * sAcc);
            uint bc0 = Fistp(((double)vsx * vsx + (double)vsy * vsy + (double)vsz * vsz) * sAcc);
            uint bd0 = Fistp(((double)usx * vsx + (double)vsy * usy + (double)vsz * usz) * sAcc);
            uint bc8 = unchecked(bc0 + 2u * bb8), bcc = unchecked(2u * bc0);
            uint bd8 = unchecked(bbc + 2u * bb4), bd4 = unchecked(2u * bbc), bdc = unchecked(2u * bd0);
            uint bc4 = bb0;
            UEVector dir = default; float coneC = 0f, coneK = 0f;
            long nNonInc = 0, nCyl = 0, nSpot = 0;
            if (spot)
            {
                dir = UEPawnMovement.GMathForward(new UERotator(L.Pitch, L.Yaw, L.Roll));
                coneC = 1f - L.Cone * (1f / 256f);
                coneK = 1f / (1f - coneC);
            }
            for (int j = row0; j < row1; j++)
            {
                uint be0 = bc4, be4 = bd8;
                for (int i = col0; i < col1; i++)
                {
                    int mv = mask == null ? NoShadowMask : (int)mask[j * W + i];
                    if (be0 < 0x1000000u && mv != 0)
                    {
                        int val;
                        if (nonIncidence || spot || cylinder)
                        {
                            float Dx2 = (float)((double)Dx + (float)((double)usx * (i - col0)) + (float)((double)vsx * (j - row0)));
                            float Dy2 = (float)((double)Dy + (float)((double)usy * (i - col0)) + (float)((double)vsy * (j - row0)));
                            float Dz2 = (float)((double)Dz + (float)((double)usz * (i - col0)) + (float)((double)vsz * (j - row0)));
                            float dd = (float)((double)Dx2 * Dx2 + (double)Dy2 * Dy2 + (double)Dz2 * Dz2);
                            int idx = RoundHalfEven((float)((double)dd * (4093f / (radius * radius))));
                            if (idx >= 0x1000) val = 0;
                            else if (nonIncidence)
                            {
                                nNonInc++;
                                float f = (float)(((double)radius / ((double)radius - inner)) * _tableH[idx]);
                                if (!(f < 1f)) f = 1f;
                                val = (int)Math.Round((double)mv * f, MidpointRounding.ToEven);
                            }
                            else if (cylinder)
                            {
                                nCyl++;
                                float v = (float)(1.0 - ((double)Dx2 * Dx2 + (double)Dy2 * Dy2) * ((double)invR * invR));
                                if (v <= 0f) v = 0f;
                                val = (int)Math.Round((double)mv * v, MidpointRounding.ToEven);
                            }
                            else
                            {
                                float dv = (float)((double)Dx2 * dir.x + (double)Dy2 * dir.y + (double)Dz2 * dir.z);
                                if (dv <= 0f || dv * dv <= dd * (coneC * coneC)) val = 0;
                                else
                                {
                                    nSpot++;
                                    float f = (float)((double)local14 * table[idx]);
                                    if (!(f < 1f)) f = 1f;
                                    // rsqrt — GMath tables (FUN_10b05710: GMath[m+0x1208c]·GMath[e+0x1408c]),
                                    // not an exact square root.
                                    float g = (float)((double)GMathRsqrt(dd) * coneK * dv - (double)(coneK * coneC));
                                    val = (int)Math.Round((double)mv * f * g * g, MidpointRounding.ToEven);
                                }
                            }
                        }
                        else
                        {
                            float tv = table[(int)(be0 >> 12)];
                            if (inner != 0f)
                            {
                                float f = (float)((double)local14 * tv);
                                if (!(f < 1f)) f = 1f;
                                val = (int)Math.Round((double)mv * f, MidpointRounding.ToEven);
                            }
                            else val = RoundProduct3(mv, tv, local14);
                        }
                        illum[(j - row0) * rw + (i - col0)] = (byte)(val & 0xff);
                    }
                    be0 = unchecked(be0 + be4); be4 = unchecked(be4 + bd4);
                }
                bc4 = unchecked(bc4 + bc8); bc8 = unchecked(bc8 + bcc); bd8 = unchecked(bd8 + bdc);
            }
            if (nNonInc != 0) Interlocked.Add(ref NonIncidenceTexels, nNonInc);
            if (nCyl != 0) Interlocked.Add(ref CylinderTexels, nCyl);
            if (nSpot != 0) Interlocked.Add(ref SpotTexels, nSpot);
            Merge(acc, W, illum, col0, col1, row0, row1, stepR, stepG, stepB, L.DarkLight, L.Effect, fx);
            return true;
        }

        // OWN FILLERS SlowWave (effect 5, FUN_10b04360) and
        // Interference (16, FUN_10b05330) — ported from the DISASSEMBLY
        // 0x10b04360..0x10b046f9 and 0x10b05330..0x10b05703. The bodies match in everything
        // except the texel value formula.
        //
        // PRECISION. SetupForSurf runs inside URender::DrawWorld, i.e.
        // between PreRender (appEnableFastMath(1) -> _controlfp(_PC_24), R:15787) and
        // PostRender (FUN_10b1ef80 -> appEnableFastMath(0), R:28920): every
        // x87 operation rounds the mantissa to 24 bits. Hence float
        // arithmetic here (one rounding per operation) in disasm operation ORDER.
        // (Other branches of this file model 64-bit precision — a separate,
        // explicitly reported question.)
        //
        //   local70 = (field44·R) / (R − inner)                  0x10b04372-0x10b04390
        //   t = Period ? (TimeSeconds·1048576)/Period : 0.0      0x10b04393-0x10b043b0
        //   phaseT = _ftol(t) + (LightPhase << 8)                0x10b043b6-0x10b043da
        //   D0 = (USX·c0) + ((VSX·r0) + Start) − Location        0x10b043bb-0x10b04500
        //   k = 4093 / (R·R)                                     0x10b04506-0x10b04548
        //   per texel: ss = (x² + y²) + z² (FVector::SizeSquared, Core 0x10119700)
        //     idx = fistp(ss·k); idx >= 0x1000 (signed) or mask 0 -> 0
        //     f = local70·table[idx]; bits of f >= 0x3f800000 (unsigned) -> 1.0
        //     SlowWave:     q = fistp(FastSqrt(ss)·1024); a = sin[((q − phaseT) >> 2) & 0x3fff]
        //                   v = ((a·0.3 + 0.7)·mask)·f               0x10b045e1-0x10b04659
        //     Interference: tf = (float)phaseT·9.58738e-5 (0x10b38100)
        //                   q = fistp((atan2(FastSqrt(y² + x²), z)·11 + tf)·65536·0.15915494), atan2 unrounded
        //                   v = (((cos[q] + 1)·0.5)·mask)·f          0x10b055bd-0x10b05663
        //     byte = low byte of float(v + 2^23)                     fadd 0x10b385e0
        //   D += US per column, row += VS                            0x10b04675-0x10b046ea
        static readonly float PhaseToRad = BitConverter.Int32BitsToSingle(0x38c90fdb); // 0x10b38100 = 2π/65536
        static HP1.Core.X87Ext R24(HP1.Core.X87Ext v) => HP1.Core.X87Ext.Round24(v);
        static readonly float InvTwoPi = BitConverter.Int32BitsToSingle(0x3e22f983);   // 0x10b385f0 = 1/(2π)

        // Interference angle, 0x10b05602..0x10b05626: fstp qword of FastSqrt(y² + x²) and of z, appAtan2 (fpatan,
        // D-71) leaves its 64-bit result in the register; fmul qword 11.0, fadd tf, fmul 65536f, fmul 1/2π each round at
        // PC24, then fstp dword (the fistp operand). Judge: x87_size_oracle.py kind lfxatan (SelfTest x87_size_corpus).
        internal static float InterferenceAngle(float sq, float pz, float tf)
        {
            HP1.Core.X87Ext at = HP1.Core.UEX87Transcendental.Fpatan(HP1.Core.X87Ext.From((double)sq), HP1.Core.X87Ext.From((double)pz));
            return R24(R24(R24(R24(at * HP1.Core.X87Ext.From(11.0)) + HP1.Core.X87Ext.From(tf)) * HP1.Core.X87Ext.From(65536f))
                * HP1.Core.X87Ext.From(InvTwoPi)).ToFloat();
        }

        static void FillWave(byte[] illum, int rw, int W, in Basis bs, in Light L, float radius, float inner,
                             float field44, float[] table, float[] mask,
                             int col0, int col1, int row0, int row1, FrameFx fx, bool interference)
        {
            float local70 = (field44 * radius) / (radius - inner);
            float t = L.Period != 0 ? (fx.TimeSeconds * 1048576f) / (float)L.Period : 0f;
            int phaseT = unchecked((int)UEGlobalRandoms.FtolLow(t) + (L.Phase << 8));
            float tf = interference ? MulIntFloat(phaseT, PhaseToRad) : 0f;   // fild; fmul [0x10b38100]
            float c0 = col0, r0 = row0;
            float ax = bs.UsX * c0, ay = bs.UsY * c0, az = bs.UsZ * c0;
            float bx = bs.VsX * r0, by = bs.VsY * r0, bz = bs.VsZ * r0;
            float cx = bx + bs.StartX, cy = bs.StartY + by, cz = bz + bs.StartZ;
            float dx = ax + cx, dy = ay + cy, dz = az + cz;
            float rx = dx - L.Pos.x, ry = dy - L.Pos.y, rz = dz - L.Pos.z;
            float k = 4093f / (radius * radius);
            long nWave = 0;
            for (int j = row0; j < row1; j++)
            {
                float px = rx, py = ry, pz = rz;
                for (int i = col0; i < col1; i++)
                {
                    int mv = mask == null ? NoShadowMask : (int)mask[j * W + i];
                    float ss = (px * px + py * py) + pz * pz;
                    int idx = unchecked((int)Fistp(ss * k));
                    byte o = 0;
                    if (idx < 0x1000 && mv != 0)
                    {
                        float f = local70 * table[idx];
                        if ((uint)BitConverter.SingleToInt32Bits(f) >= 0x3f800000u) f = 1f;
                        float w;
                        if (!interference)
                        {
                            int q = unchecked((int)Fistp(FogMap.FastSqrt(ss) * 1024f));
                            w = BrushCoords.SinAngle(unchecked(q - phaseT)) * 0.3f + 0.7f;
                        }
                        else
                        {
                            float xy = py * py + px * px;
                            int q = unchecked((int)Fistp(InterferenceAngle(FogMap.FastSqrt(xy), pz, tf)));
                            w = (BrushCoords.CosAngle(q) + 1f) * 0.5f;
                        }
                        nWave++;
                        float v = (w * mv) * f + 8388608f;
                        o = (byte)(BitConverter.SingleToInt32Bits(v) & 0xff);
                    }
                    illum[(j - row0) * rw + (i - col0)] = o;
                    px += bs.UsX; py += bs.UsY; pz += bs.UsZ;
                }
                rx += bs.VsX; ry += bs.VsY; rz += bs.VsZ;
            }
            if (nWave != 0) Interlocked.Add(ref WaveTexels, nWave);
        }

        // (float)(n·c) with a single rounding, like `fild n; fmul c` at 24-bit
        // precision: the product of a 31-bit integer and a 24-bit mantissa fits
        // exactly in a long; round-to-nearest-even is done by hand
        // (long -> float conversion is not guaranteed to round once on every
        // platform).
        static float MulIntFloat(int n, float c)
        {
            if (n == 0 || c == 0f) return 0f * c;
            int cb = BitConverter.SingleToInt32Bits(c);
            int ce = (cb >> 23) & 0xff;
            if (ce == 0 || ce == 0xff) throw new InvalidOperationException("MulIntFloat: denormal/inf");
            long mant = ((long)(cb & 0x7fffff) | 0x800000) * Math.Abs((long)n);   // < 2^55
            int exp = ce - 150;                       // value = mant · 2^exp
            while (mant >= (1L << 24))
            {
                // shift out all excess bits at once, rounding to even
                int sh = 0; while ((mant >> sh) >= (1L << 24)) sh++;
                long q = mant >> sh, rem = mant & ((1L << sh) - 1), half = 1L << (sh - 1);
                if (rem > half || (rem == half && (q & 1) == 1)) q++;
                mant = q; exp += sh;                  // q may become 2^24 — the loop normalises
            }
            while (mant < (1L << 23)) { mant <<= 1; exp--; }
            int be = exp + 23 + 127;
            if (be <= 0 || be >= 0xff) throw new InvalidOperationException("MulIntFloat: outside the normal float range");
            int bits = (be << 23) | (int)(mant & 0x7fffff);
            if ((n < 0) != (cb < 0)) bits |= unchecked((int)0x80000000);
            return BitConverter.Int32BitsToSingle(bits);
        }

        // MERGE — FUN_10b03430 (disasm 0x10b03430..0x10b037bb). For LightEffect
        // 1/2/3 ([ebp-0x18] = 1, 0x10b03463-0x10b0347c) every illumination byte
        // of the row goes through modulation BEFORE merging:
        //   illum' = low byte of fistp(float(illum·(T[k & 0xff]·a + b)) − 0.5)
        //     1: T = Base 0x10b6696c, a = 0.05 (0x10b385dc), b = 0.95 (0x10b385d8)
        //     2: T = Base,             a = 0.2  (0x10b385d4), b = 0.8  (0x10b385d0)
        //     3: T = Rand 0x10b66d6c,  a = 0.4  (0x10b385cc), b = 0.6  (0x10b385c8)
        // k — third argument, a counter over ALL rectangle texels in row
        // order; both SetupForSurf calls pass 0 (push 0 at 0x10b08e64;
        // [ebp-0xe0] = ebx = 0 at 0x10b078de -> 0x10b09554). Then — packed
        // addition with saturation at 0x7f per channel (no carries between bytes:
        // 0x7f + 0x7f < 0x100), or for bDarkLight (actor+0x1f0 bit 0x10)
        // packed subtraction WITH BORROW between bytes and zeroing of
        // negatives.
        static void Merge(UEVector[] acc, int W, byte[] illum, int col0, int col1, int row0, int row1,
                          int stepR, int stepG, int stepB, bool dark, byte effect, FrameFx fx)
        {
            float[] tab = null; float a = 0f, b = 0f;
            if (effect == 1) { tab = fx.RandomBase; a = 0.05f; b = 0.95f; }
            else if (effect == 2) { tab = fx.RandomBase; a = 0.2f; b = 0.8f; }
            else if (effect == 3) { tab = fx.RandomRand; a = 0.4f; b = 0.6f; }
            int rw = col1 - col0;
            uint k = 0;
            for (int j = row0; j < row1; j++)
                for (int i = col0; i < col1; i++)
                {
                    int v = illum[(j - row0) * rw + (i - col0)];
                    if (tab != null)
                    {
                        float p = (tab[k & 0xff] * a + b) * v;
                        v = (int)(Fistp(p - 0.5f) & 0xff);
                        k++;
                    }
                    if (v == 0) continue;
                    int t = j * W + i;
                    int cr = Math.Min((v * stepR) >> 16, 127);
                    int cg = Math.Min((v * stepG) >> 16, 127);
                    int cb = Math.Min((v * stepB) >> 16, 127);
                    UEVector c = acc[t];
                    if (!dark)
                        acc[t] = new UEVector(MathF.Min(c.x + cr, 127f), MathF.Min(c.y + cg, 127f), MathF.Min(c.z + cb, 127f));
                    else
                    {
                        // Subtraction is ONE 32-bit `sub` over packed BGRA
                        // (0x10b0366d); the borrow from a lower byte goes to the higher one:
                        // B (byte 0) -> G (1) -> R (2). A negative byte
                        // (bit 7) is then zeroed (0x10b03674-0x10b03689), while
                        // the neighbour decreased by the borrow stays decreased.
                        int nb = (int)c.z - cb, borrow = nb < 0 ? 1 : 0;
                        int ng = (int)c.y - cg - borrow; borrow = ng < 0 ? 1 : 0;
                        int nr = (int)c.x - cr - borrow;
                        acc[t] = new UEVector(nr < 0 ? 0 : nr, ng < 0 ? 0 : ng, nb < 0 ? 0 : nb);
                    }
                }
            if (k != 0) Interlocked.Add(ref ModulatedTexels, k);   // k counts exactly the modulated texels
        }
    }
}
