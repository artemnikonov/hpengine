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
    public Vector3 TransformVector(Vector3 v) => UEWorldFrameProducer.FastMathWindow ? TransformVector24(v) : new(
        (float)(((double)v.x * X.x + (double)v.y * X.y) + (double)v.z * X.z),
        (float)(((double)v.x * Y.x + (double)v.y * Y.y) + (double)v.z * Y.z),
        (float)(((double)v.x * Z.x + (double)v.y * Z.y) + (double)v.z * Z.z));

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
    public Vector3 TransformPoint(Vector3 v)
    {
        if (UEWorldFrameProducer.FastMathWindow)
            return TransformVector24(HP1.Core.X87Ext.Round24((double)v.x - Origin.x),
                HP1.Core.X87Ext.Round24((double)v.y - Origin.y), HP1.Core.X87Ext.Round24((double)v.z - Origin.z));
        double dx = (double)v.x - Origin.x, dy = (double)v.y - Origin.y, dz = (double)v.z - Origin.z;
        return new Vector3((float)((dx * X.x + dy * X.y) + dz * X.z),
                           (float)((dx * Y.x + dy * Y.y) + dz * Y.z),
                           (float)((dx * Z.x + dy * Z.y) + dz * Z.z));
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
        float tz = Dot87(Z.z, nz, Z.y, ny, Z.x, nx).ToFloat(), ty = Dot87(Y.z, nz, Y.y, ny, Y.x, nx).ToFloat();
        float tx = Dot87(X.z, nz, X.y, ny, X.x, nx).ToFloat();
        Vector3 o = TransformVector(Origin);
        return (tx, ty, tz, W87(w, tz, ty, tx, o));
    }

    // (a1·b1 + a2·b2) + a3·b3 in the PC64 register (float products are exact).
    static HP1.Core.X87Ext Dot87(float a1, float b1, float a2, float b2, float a3, float b3)
    {
        static HP1.Core.X87Ext F(float v) => HP1.Core.X87Ext.From(v);
        return HP1.Core.X87Ext.Round64(HP1.Core.X87Ext.Round64(F(a1) * F(b1) + F(a2) * F(b2)) + F(a3) * F(b3));
    }

    // W' = W − operator|(o, t) = W − ((t.z·o.z + t.y·o.y) + t.x·o.x) kept in the register into `fsubr W`, fstp dword.
    static float W87(float w, float tz, float ty, float tx, Vector3 o)
        => HP1.Core.X87Ext.Round64(HP1.Core.X87Ext.From(w) - Dot87(tz, o.z, ty, o.y, tx, o.x)).ToFloat();

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
        float tz = Dot87(Z.x, nx, Z.z, nz, Z.y, ny).ToFloat(), ty = Dot87(Y.x, nx, Y.z, nz, Y.y, ny).ToFloat();
        float tx = Dot87(X.x, nx, X.z, nz, X.y, ny).ToFloat();
        return (tx, ty, tz, W87(w, tz, ty, tx, TransformVector(Origin)));
    }

    // Zero-extent worker node transform (FUN_104294c0, Engine.dll.c:325974-326004; disasm 0x104294fc..0x10429592 near
    // plane, ..0x10429656 far plane): the operator| construction of the sweep worker; the origin dot stays in the
    // register into `fsubr W` (0x1042955e -> 0x10429574: no store between — the decompile's float local misleads), so
    // W' is the sweep form's. The near plane's inline Z dot and the far plane's operator| round alike in both windows:
    // one form (checked against the original, both planes, PC24 and PC64).
    public (float X, float Y, float Z, float W) TransformNodePlaneLine(float nx, float ny, float nz, float w)
        => TransformNodePlaneSweep(nx, ny, nz, w);

    // The zero-extent LineCheck tail for a brush hit (0x10429ec7): the raw
    // model-space normal transforms covariantly — dots with the axes, the
    // same mapping TransformPlaneByOrtho applies to hull planes (proven by
    // a diff against the original: shipped normal == (n·X, n·Y, n·Z) oriented).
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

    // FCoords::operator/=(FScale), no-sheer branch used by ABrush::ToLocal.
    // HP1 Tut1 brush models have zero snapped sheer; retain the STOP rule for
    // any authored model that does not, rather than applying an approximate
    // inverse matrix.
    public void DivScale(Vector3 scale, float sheerRate, string who)
    {
        if (SnapSheer(sheerRate) != 0f)
            throw new NotSupportedException($"headless brush sheer unsupported ({who})");
        X = new Vector3(X.x / scale.x, X.y / scale.y, X.z / scale.z);
        Y = new Vector3(Y.x / scale.x, Y.y / scale.y, Y.z / scale.z);
        Z = new Vector3(Z.x / scale.x, Z.y / scale.y, Z.z / scale.z);
        Origin = new Vector3(Origin.x * scale.x, Origin.y * scale.y, Origin.z * scale.z);
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

    // FCoords::operator*=(FScale) (Core.dll.c:39995) with the sheer left loud
    // (no Tut1 brush shears; the snap zeroes |rate|<=0.05).
    public void MulScale(Vector3 scale, float sheerRate, string who)
    {
        if (SnapSheer(sheerRate) != 0f)
            throw new NotSupportedException($"headless brush sheer unsupported ({who})");
        X = new Vector3(X.x * scale.x, X.y * scale.y, X.z * scale.z);
        Y = new Vector3(Y.x * scale.x, Y.y * scale.y, Y.z * scale.z);
        Z = new Vector3(Z.x * scale.x, Z.y * scale.y, Z.z * scale.z);
        Origin = new Vector3(Origin.x / scale.x, Origin.y / scale.y, Origin.z / scale.z);
    }

    // ABrush::ToWorld (Engine.dll.c:60413): identity, Origin -= Location,
    // *= PostScale, *= Rotation, *= MainScale (inline), Origin += PrePivot.
    public static BrushCoords ToWorld(Vector3 location, int pitch, int yaw, int roll, Vector3 prePivot,
        Vector3 mainScale, float mainSheer, Vector3 postScale, float postSheer)
    {
        BrushCoords c = Identity;
        c.Origin -= location;
        c.MulScale(postScale, postSheer, "PostScale");
        c.MulRotator(pitch, yaw, roll);
        c.MulScale(mainScale, mainSheer, "MainScale");
        c.Origin += prePivot;
        return c;
    }

    // ABrush::ToLocal (Engine.dll.c:141428), inverse chain of ToWorld. The
    // returned coords are exactly what FCheckResult::BspNode obtains from the
    // hit Mover's vtable +0x98 before transforming Location and Normal into
    // the brush model's local space.
    public static BrushCoords ToLocal(Vector3 location, int pitch, int yaw, int roll, Vector3 prePivot,
        Vector3 mainScale, float mainSheer, Vector3 postScale, float postSheer)
    {
        BrushCoords c = Identity;
        c.Origin -= prePivot;
        c.DivScale(mainScale, mainSheer, "MainScale");
        c.DivRotator(pitch, yaw, roll);
        c.DivScale(postScale, postSheer, "PostScale");
        c.Origin += location;
        return c;
    }
}
}
