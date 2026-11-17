// BrushCoords: the FCoords port for brush actors (ABrush::ToWorld/ToLocal and the coords operators).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UELib;
using UELib.Core;
using UELib.Types;
// Script vectors are the Core value type (HP1.UEScript.UEVector).
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    // FCoords port for brush actors (ABrush::ToWorld, Engine.dll.c:60413, and the
    // Core.dll coords operators it composes). Floats spill exactly where the shipped code stores;
    // intermediates ride in double like the rest of the collision ports. The
    // rotator trig comes from the SAME 16384-entry float32 sine table the shipped
    // FGlobalMath ctor builds (tab[i] = sin(float(i) * step)).
    public struct BrushCoords
    {
        public Vector3 Origin, X, Y, Z;

        private const float TableStep = 0.0003834952076431364f; // Core.dll _DAT_1017fa38
        private static float[] _sinTab;
        private static float[] SinTab
        {
            get
            {
                if (_sinTab is null)
                {
                    // Ctor loop machine code (Core.dll 0x1014e26e): fild i;
                    // fmul dword [step]; fstp qword → the product reaches appSin
                    // UNROUNDED (exact in double: ≤38 mantissa bits); only the
                    // sin result is stored as float32. appSin = bare fsin: UEX87Transcendental.
                    var t = new float[16384];
                    for (int i = 0; i < t.Length; i++)
                        t[i] = HP1.Core.UEX87Transcendental.Fsin(HP1.Core.X87Ext.From(i * (double)TableStep)).ToFloat();
                    _sinTab = t;
                }
                return _sinTab;
            }
        }
        public static float SinAngle(int a) => SinTab[(a >> 2) & 0x3fff];
        public static float CosAngle(int a) => SinTab[((a + 0x4000) >> 2) & 0x3fff];

        public static BrushCoords Identity => new()
        { Origin = Vector3.zero, X = new Vector3(1, 0, 0), Y = new Vector3(0, 1, 0), Z = new Vector3(0, 0, 1) };

        // FVector::TransformVectorBy (Core.dll.c:34554): dots with the axes.
        // Sum order per disasm 0x1011c70a-0x1011c75e (same code inlined in
        // FCoords::operator*=(FCoords) 0x1011d1c0): (v.x·A.x + v.y·A.y) + v.z·A.z,
        // `faddp` in a register, one store to float. A (z + y) + x order diverges under cancellation
        // (selftest brushcoords_transformvector_sum_order).
        // The body sets no control word: PC64 in game logic, PC24 inside the render window
        // (UEWorldFrameProducer.FastMathWindow: HUD/console script `>>`, `<<`, GetAxes, Vector(Rotator)), where every
        // fmul/fadd rounds the register to 24 bits (X87Ext.Round24 of the exact double) before the fstp dword.
        // Original: Core.dll ?TransformVectorBy@FVector@@QBE?AV1@ABVFCoords@@@Z [HP1 0x1011c6f0]
        public Vector3 TransformVector(Vector3 v) => UEWorldFrameProducer.FastMathWindow ? TransformVector24(v) : new(
            VectorRow64(v, X), VectorRow64(v, Y), VectorRow64(v, Z));

        // f32 of the PC64 register row (v.x·A.x + v.y·A.y) + v.z·A.z. A double chain is not that register: under
        // cancellation its 53-bit sums leave the float one bit off (a brush hit's near-zero world normal component; the
        // LineCheck tail 0x10429eed..0x10429f41 is this row inline).
        static float VectorRow64(Vector3 v, Vector3 a)
            => Decides((double)v.x * a.x, (double)v.y * a.y, (double)v.z * a.z, out float f) ? f
                : Dot87(v.x, a.x, v.y, a.y, v.z, a.z).ToFloat();

        // TransformPointBy row: the differences v − Origin stay in the register (0x1011c63a..0x1011c64d), then the row.
        static float PointRow64(Vector3 v, Vector3 o, Vector3 a)
        {
            double dx = (double)v.x - o.x, dy = (double)v.y - o.y, dz = (double)v.z - o.z;
            if (Decides(dx * a.x, dy * a.y, dz * a.z, out float f)) return f;
            static HP1.Core.X87Ext F(float s) => HP1.Core.X87Ext.From(s);
            static HP1.Core.X87Ext R(HP1.Core.X87Ext s) => HP1.Core.X87Ext.Round64(s);
            return R(R(R(R(F(v.x) - F(o.x)) * F(a.x)) + R(R(F(v.y) - F(o.y)) * F(a.y))) + R(R(F(v.z) - F(o.z)) * F(a.z)))
                .ToFloat();
        }

        // The double chain (p0 + p1) + p2 decides the stored float when its error (< 1e-15 of the summed magnitudes,
        // product roundings included; the register's is far below) cannot reach a float rounding boundary: rounding is
        // monotone. A zero (its sign) or non-finite value is left to the exact register chain.
        static bool Decides(double p0, double p1, double p2, out float f)
        {
            double d = (p0 + p1) + p2, e = (Math.Abs(p0) + Math.Abs(p1) + Math.Abs(p2)) * 1e-15;
            f = (float)(d - e);
            return f == (float)(d + e) && f != 0f;
        }

        static float Dot24(double x, double y, double z, Vector3 a)
        {
            static double R(double d) => HP1.Core.X87Ext.Round24(d);
            return (float)R(R(R(x * a.x) + R(y * a.y)) + R(z * a.z));
        }
        Vector3 TransformVector24(Vector3 v) => TransformVector24(v.x, v.y, v.z);
        Vector3 TransformVector24(double x, double y, double z) => new(Dot24(x, y, z, X), Dot24(x, y, z, Y), Dot24(x, y, z, Z));

        // Inverse of TransformVector: sum of the axes weighted by the components:
        // X·v.x + Y·v.y + Z·v.z (USkeletalMesh::GetRootMovement, Engine.dll.c:48421-48423;
        // x87 keeps the sum in extended precision, one store to float).
        public Vector3 UntransformVector(Vector3 v) => new(
            (float)((double)X.x * v.x + (double)Y.x * v.y + (double)Z.x * v.z),
            (float)((double)X.y * v.x + (double)Y.y * v.y + (double)Z.y * v.z),
            (float)((double)X.z * v.x + (double)Y.z * v.y + (double)Z.z * v.z));

        // FVector::TransformPointBy (Core.dll.c:22957): origin-relative dots.
        // The v − Origin differences stay in a register (disasm 0x1011c63a-0x1011c64d: fsub without
        // a store to float), then the TransformVectorBy sum.
        // Like TransformVectorBy the body sets no control word: inside the render window each difference rounds to 24 bits in
        // the register, then the PC24 dot (checked against the real FCoords::operator*= at both FPCWs).
        // Original: Core.dll ?TransformPointBy@FVector@@QBE?AV1@ABVFCoords@@@Z [HP1 0x1011c620]
        public Vector3 TransformPoint(Vector3 v)
        {
            if (UEWorldFrameProducer.FastMathWindow)
                return TransformVector24(HP1.Core.X87Ext.Round24((double)v.x - Origin.x),
                    HP1.Core.X87Ext.Round24((double)v.y - Origin.y), HP1.Core.X87Ext.Round24((double)v.z - Origin.z));
            return new Vector3(PointRow64(v, Origin, X), PointRow64(v, Origin, Y), PointRow64(v, Origin, Z));
        }

        // FPlane::TransformPlaneByOrtho, from the machine code (Core.dll
        // 0x1011c8f0 — the Ghidra decompile misorders the sums): n' components
        // sum as ((A.y*ny + A.z*nz) + A.x*nx), each spilled to float; the
        // transformed origin is an INLINE matvec associating ((x + y) + z),
        // spilled to float per component; W' = W - ((to.y*ty + to.x*tx) +
        // to.z*tz) on the x87 stack, one final float store.
        public (float X, float Y, float Z, float W) TransformPlane(float nx, float ny, float nz, float w)
        {
            if (UEWorldFrameProducer.FastMathWindow)   // PC24: every step rounds to float, same order (plane_forms_oracle ortho)
            {
                float ax = (X.y * ny + X.z * nz) + X.x * nx, ay = (Y.y * ny + Y.z * nz) + Y.x * nx, az = (Z.y * ny + Z.z * nz) + Z.x * nx;
                float ox = (Origin.x * X.x + Origin.y * X.y) + Origin.z * X.z, oy = (Origin.x * Y.x + Origin.y * Y.y) + Origin.z * Y.z,
                      oz = (Origin.x * Z.x + Origin.y * Z.y) + Origin.z * Z.z;
                return (ax, ay, az, w - ((oy * ay + ox * ax) + oz * az));
            }
            // PC64: the same order, every sum in the 64-bit register (Dot87), one fstp dword each (checked against the original;
            // the cancellation rows tell the register from a double).
            float tx = Dot87(X.y, ny, X.z, nz, X.x, nx).ToFloat(), ty = Dot87(Y.y, ny, Y.z, nz, Y.x, nx).ToFloat();
            float tz = Dot87(Z.y, ny, Z.z, nz, Z.x, nx).ToFloat();
            float tox = Dot87(Origin.x, X.x, Origin.y, X.y, Origin.z, X.z).ToFloat();
            float toy = Dot87(Origin.x, Y.x, Origin.y, Y.y, Origin.z, Y.z).ToFloat();
            float toz = Dot87(Origin.x, Z.x, Origin.y, Z.y, Origin.z, Z.z).ToFloat();
            return (tx, ty, tz, HP1.Core.X87Ext.Round64(HP1.Core.X87Ext.From(w) - Dot87(toy, ty, tox, tx, toz, tz)).ToFloat());
        }

        // Extent sweep worker node transform (0x1042a4bc): three FVector::operator| dots (Core 0x10119190: (b.z·a.z + b.y·a.y)
        // + b.x·a.x in the 64-bit register, fstp dword each), origin via the Core TransformVectorBy (float stores), W via
        // operator| kept in the register into fsubr, fstp dword. Checked against the original at PC64.
        public (float X, float Y, float Z, float W) TransformNodePlaneSweep(float nx, float ny, float nz, float w)
        {
            if (UEWorldFrameProducer.FastMathWindow) return NodePlaneByOperatorDot24(nx, ny, nz, w);
            var (tx, ty, tz, wx) = TransformNodePlaneSweepExt(nx, ny, nz, w);
            return (tx, ty, tz, wx.ToFloat());
        }

        // The PC64 sweep form with W' as the register holds it before its fstp dword.
        public (float X, float Y, float Z, HP1.Core.X87Ext W) TransformNodePlaneSweepExt(float nx, float ny, float nz, float w)
        {
            float tz = Dot87(Z.z, nz, Z.y, ny, Z.x, nx).ToFloat(), ty = Dot87(Y.z, nz, Y.y, ny, Y.x, nx).ToFloat();
            float tx = Dot87(X.z, nz, X.y, ny, X.x, nx).ToFloat();
            Vector3 o = TransformVector(Origin);
            return (tx, ty, tz, W87x(w, tz, ty, tx, o));
        }

        // HP2's zero-extent line worker and PointCheck walk subtract W' = W − (o | N') from the side dot while it is still
        // in the register (no FPlane is built): the side is f32(((N.z·p.z + N.y·p.y) + N.x·p.x) − W') at 64 bits, where
        // HP1 stores W' as a float first (the FPlane copy constructor). PC64 only: at PC24 the register value is the float.
        // Original: Engine.dll not exported: the UModel::LineCheck zero-extent worker [HP1 0x104294c0] [HP2 0x10437c60]:
        //   near side [HP1 0x10429574..0x104295be] [HP2 0x10437cf5..0x10437d22], far side [HP1 0x1042962c..0x1042968c]
        //   [HP2 0x10437d79..0x10437dab]
        // Original: Engine.dll ?PointCheck@UModel@@UAEHAAUFCheckResult@@PAVAActor@@VFVector@@2K@Z [HP1 0x104271d0]
        //   [HP2 0x10435af0]: the zero-extent walk's side [HP1 0x104273c4..0x104273f6] [HP2 0x10435cd8..0x10435cf3]
        public static bool KeepsPlaneW => UEGameProfile.Current.Game == UEEdition.Game.HP2 && !UEWorldFrameProducer.FastMathWindow;

        // ((a1·b1 + a2·b2) + a3·b3) − W' in the PC64 register (float products exact): HP2's side before its store / compare.
        public static HP1.Core.X87Ext SideKeepW(float a1, float b1, float a2, float b2, float a3, float b3, HP1.Core.X87Ext w)
            => HP1.Core.X87Ext.Round64(Dot87(a1, b1, a2, b2, a3, b3) - w);

        // (a1·b1 + a2·b2) + a3·b3 in the PC64 register (float products are exact).
        static HP1.Core.X87Ext Dot87(float a1, float b1, float a2, float b2, float a3, float b3)
        {
            static HP1.Core.X87Ext F(float v) => HP1.Core.X87Ext.From(v);
            return HP1.Core.X87Ext.Round64(HP1.Core.X87Ext.Round64(F(a1) * F(b1) + F(a2) * F(b2)) + F(a3) * F(b3));
        }

        // W' = W − operator|(o, t) = W − ((t.z·o.z + t.y·o.y) + t.x·o.x) kept in the register into `fsubr W`, fstp dword.
        static float W87(float w, float tz, float ty, float tx, Vector3 o) => W87x(w, tz, ty, tx, o).ToFloat();
        static HP1.Core.X87Ext W87x(float w, float tz, float ty, float tx, Vector3 o)
            => HP1.Core.X87Ext.Round64(HP1.Core.X87Ext.From(w) - Dot87(tz, o.z, ty, o.y, tx, o.x));

        // The render window (PC24) form of the sweep worker (0x1042a4bc) and of both planes of the zero-extent LineCheck worker
        // (0x104294fc / 0x104295a8): FVector::operator| (Core 0x10119190) is (b.z·a.z + b.y·a.y) + b.x·a.x in the register,
        // so each axis dot is (A.z·n.z + A.y·n.y) + A.x·n.x and the W dot (t.z·o.z + t.y·o.y) + t.x·o.x with o =
        // Origin.TransformVectorBy (window-following); fsubr W; one store each. Checked against the original.
        (float X, float Y, float Z, float W) NodePlaneByOperatorDot24(float nx, float ny, float nz, float w)
        {
            float tz = (Z.z * nz + Z.y * ny) + Z.x * nx, ty = (Y.z * nz + Y.y * ny) + Y.x * nx, tx = (X.z * nz + X.y * ny) + X.x * nx;
            Vector3 o = TransformVector(Origin);
            return (tx, ty, tz, w - ((tz * o.z + ty * o.y) + tx * o.x));
        }

        // Zero-extent PointCheck node transform (dispatcher inline, 0x1042735e..0x104273a0):
        // n' components (A.x·n.x + A.z·n.z) + A.y·n.y in the register, fstp dword; the
        // W dot goes through operator| kept in the register into fsubr (fst keeps the
        // dot on the x87 stack), fstp dword — the sweep-worker W semantics.
        public (float X, float Y, float Z, float W) TransformNodePlanePoint(float nx, float ny, float nz, float w)
        {
            if (UEWorldFrameProducer.FastMathWindow)   // PC24 (0x10427341..0x104273dd)
            {
                float cz = (Z.x * nx + Z.z * nz) + Z.y * ny, cy = (Y.x * nx + Y.z * nz) + Y.y * ny, cx = (X.x * nx + X.z * nz) + X.y * ny;
                Vector3 o = TransformVector(Origin);
                return (cx, cy, cz, w - ((cz * o.z + cy * o.y) + cx * o.x));
            }
            var (tx, ty, tz, wx) = TransformNodePlanePointExt(nx, ny, nz, w);
            return (tx, ty, tz, wx.ToFloat());
        }

        // The PC64 point form with W' as the register holds it before its fstp dword.
        public (float X, float Y, float Z, HP1.Core.X87Ext W) TransformNodePlanePointExt(float nx, float ny, float nz, float w)
        {
            float tz = Dot87(Z.x, nx, Z.z, nz, Z.y, ny).ToFloat(), ty = Dot87(Y.x, nx, Y.z, nz, Y.y, ny).ToFloat();
            float tx = Dot87(X.x, nx, X.z, nz, X.y, ny).ToFloat();
            return (tx, ty, tz, W87x(w, tz, ty, tx, TransformVector(Origin)));
        }

        // Zero-extent worker node transform (FUN_104294c0, Engine.dll.c:325974-326004; disasm 0x104294fc..0x10429592 near
        // plane, ..0x10429656 far plane): the operator| construction of the sweep worker; the origin dot stays in the
        // register into `fsubr W` (0x1042955e -> 0x10429574: no store between — the decompile's float local misleads), so
        // W' is the sweep form's. The near plane's inline Z dot and the far plane's operator| round alike in both windows:
        // one form (checked against the original, both planes, PC24 and PC64).
        public (float X, float Y, float Z, float W) TransformNodePlaneLine(float nx, float ny, float nz, float w)
            => TransformNodePlaneSweep(nx, ny, nz, w);

        // The zero-extent LineCheck tail for a brush hit (0x10429ec7): the raw model-space normal goes through the
        // TransformVectorBy row inline (0x10429eed..0x10429f41, Core 0x1011c70a's instructions) with ToWorld's axes, in
        // the caller's window.
        public Vector3 NormalToWorld(Vector3 n) => TransformVector(n);

        // FCoords::operator*=(FCoords) (Core.dll.c:44460).
        public void Mul(in BrushCoords b)
        {
            Origin = b.TransformPoint(Origin);
            X = b.TransformVector(X);
            Y = b.TransformVector(Y);
            Z = b.TransformVector(Z);
        }

        // FCoords::operator*=(FRotator): yaw, then pitch, then roll elementary
        // coords, each applied to all four rows. Matrix entries taken from the
        // machine code (Core.dll 0x1011d780): yaw X=(c,s,0) Y=(-s,c,0) Z=(0,0,1);
        // pitch X=(c,0,s) Y=(0,1,0) Z=(-s,0,c); roll X=(1,0,0) Y=(0,c,-s)
        // Z=(0,s,c) — the decompile's reused stack slots hid these signs.
        public void MulRotator(int pitch, int yaw, int roll)
        {
            float sy = SinAngle(yaw), cy = CosAngle(yaw);
            Mul(new BrushCoords
            {
                Origin = Vector3.zero,
                X = new Vector3(cy, sy, 0f),
                Y = new Vector3(-sy, cy, 0f),
                Z = new Vector3(0f, 0f, 1f),
            });
            float sp = SinAngle(pitch), cp = CosAngle(pitch);
            Mul(new BrushCoords
            {
                Origin = Vector3.zero,
                X = new Vector3(cp, 0f, sp),
                Y = new Vector3(0f, 1f, 0f),
                Z = new Vector3(-sp, 0f, cp),
            });
            float sr = SinAngle(roll), cr = CosAngle(roll);
            Mul(new BrushCoords
            {
                Origin = Vector3.zero,
                X = new Vector3(1f, 0f, 0f),
                Y = new Vector3(0f, cr, -sr),
                Z = new Vector3(0f, sr, cr),
            });
        }

        // FCoords::operator/=(FRotator), from the machine code (Core.dll
        // 0x1011e190): the INVERSE composition — roll, then pitch, then yaw, each
        // elementary matrix the transpose of the operator*= one. physicsRotation
        // banking builds its frame with this (Engine.dll.c:64155).
        public void DivRotator(int pitch, int yaw, int roll)
        {
            float sr = SinAngle(roll), cr = CosAngle(roll);
            Mul(new BrushCoords
            {
                Origin = Vector3.zero,
                X = new Vector3(1f, 0f, 0f),
                Y = new Vector3(0f, cr, sr),
                Z = new Vector3(0f, -sr, cr),
            });
            float sp = SinAngle(pitch), cp = CosAngle(pitch);
            Mul(new BrushCoords
            {
                Origin = Vector3.zero,
                X = new Vector3(cp, 0f, -sp),
                Y = new Vector3(0f, 1f, 0f),
                Z = new Vector3(sp, 0f, cp),
            });
            float sy = SinAngle(yaw), cy = CosAngle(yaw);
            Mul(new BrushCoords
            {
                Origin = Vector3.zero,
                X = new Vector3(cy, -sy, 0f),
                Y = new Vector3(sy, cy, 0f),
                Z = new Vector3(0f, 0f, 1f),
            });
        }

        // FCoords::operator/=(FScale) (Core.dll.c:51222, disasm 0x1011f04f..; ABrush::ToLocal's PostScale inline, Engine
        // 0x1032a4b6..0x1032a5e0, is the same): axes /= Scale, Origin *= Scale, then *= the unit coords with the NEGATED
        // snapped sheer in the SheerAxis slot (no transpose: the slot is the *= one). The multiply runs at zero snap too.
        public void DivScale(Vector3 scale, float sheerRate, int sheerAxis)
        {
            X = new Vector3(X.x / scale.x, X.y / scale.y, X.z / scale.z);
            Y = new Vector3(Y.x / scale.x, Y.y / scale.y, Y.z / scale.z);
            Z = new Vector3(Z.x / scale.x, Z.y / scale.y, Z.z / scale.z);
            Origin = new Vector3(Origin.x * scale.x, Origin.y * scale.y, Origin.z * scale.z);
            Mul(SheerCoords(-SnapSheer(sheerRate), sheerAxis));
        }

        // GMath.UnitCoords with one axis entry set to the snapped sheer: ESheerAxis 1 XY -> X.y, 2 XZ -> X.z, 3 YX -> Y.x,
        // 4 YZ -> Y.z, 5 ZX -> Z.x, 6 ZY -> Z.y (jump table Core 0x1011ef1c: stores to ebp-0x34/-0x30/-0x2c/-0x24/-0x20/-0x1c
        // of the copy at ebp-0x44); any other value leaves the unit coords.
        public static BrushCoords SheerCoords(float sheer, int sheerAxis)
        {
            BrushCoords t = Identity;
            switch (sheerAxis)
            {
                case 1: t.X.y = sheer; break;
                case 2: t.X.z = sheer; break;
                case 3: t.Y.x = sheer; break;
                case 4: t.Y.z = sheer; break;
                case 5: t.Z.x = sheer; break;
                case 6: t.Z.y = sheer; break;
            }
            return t;
        }

        // Sheer snap shared by FCoords::operator*=(FScale) and the ToWorld inline
        // (constants Core.dll 0x1017d740..): |v|<=0.05→0, then ∓0.05, knees at
        // 0.55 (→±0.5) and 0.65 (→v∓0.15).
        public static float SnapSheer(float v)
        {
            if (v < -0.65f) return v + 0.15f;
            if (v > 0.65f) return v - 0.15f;
            if (v < -0.55f) return -0.5f;
            if (v > 0.55f) return 0.5f;
            if (v < -0.05f) return v + 0.05f;
            if (v > 0.05f) return v - 0.05f;
            return 0f;
        }

        // FCoords::operator*=(FScale) (Core.dll.c:39995, disasm 0x1011eba0..; ABrush::ToWorld's MainScale inline, Engine
        // 0x1032a710.., is the same): *= the unit coords with the snapped sheer in the SheerAxis slot (also at zero snap),
        // then axes *= Scale component-wise, Origin /= Scale.
        public void MulScale(Vector3 scale, float sheerRate, int sheerAxis)
        {
            Mul(SheerCoords(SnapSheer(sheerRate), sheerAxis));
            X = new Vector3(X.x * scale.x, X.y * scale.y, X.z * scale.z);
            Y = new Vector3(Y.x * scale.x, Y.y * scale.y, Y.z * scale.z);
            Z = new Vector3(Z.x * scale.x, Z.y * scale.y, Z.z * scale.z);
            Origin = new Vector3(Origin.x / scale.x, Origin.y / scale.y, Origin.z / scale.z);
        }

        // ABrush::ToWorld (Engine.dll.c:60413): identity, Origin -= Location,
        // *= PostScale, *= Rotation, *= MainScale (inline), Origin += PrePivot.
        public static BrushCoords ToWorld(Vector3 location, int pitch, int yaw, int roll, Vector3 prePivot,
            Vector3 mainScale, float mainSheer, int mainSheerAxis, Vector3 postScale, float postSheer, int postSheerAxis)
        {
            BrushCoords c = Identity;
            c.Origin -= location;
            c.MulScale(postScale, postSheer, postSheerAxis);
            c.MulRotator(pitch, yaw, roll);
            c.MulScale(mainScale, mainSheer, mainSheerAxis);
            c.Origin += prePivot;
            return c;
        }

        // ABrush::ToLocal (Engine.dll.c:141428), inverse chain of ToWorld. The
        // returned coords are exactly what FCheckResult::BspNode obtains from the
        // hit Mover's vtable +0x98 before transforming Location and Normal into
        // the brush model's local space.
        public static BrushCoords ToLocal(Vector3 location, int pitch, int yaw, int roll, Vector3 prePivot,
            Vector3 mainScale, float mainSheer, int mainSheerAxis, Vector3 postScale, float postSheer, int postSheerAxis)
        {
            BrushCoords c = Identity;
            c.Origin -= prePivot;
            c.DivScale(mainScale, mainSheer, mainSheerAxis);
            c.DivRotator(pitch, yaw, roll);
            c.DivScale(postScale, postSheer, postSheerAxis);
            c.Origin += location;
            return c;
        }
    }
}
