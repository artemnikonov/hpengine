// BODIES OF Core.dll ARITHMETIC exec NATIVES WHOSE INTERMEDIATES LIVE IN THE x87 REGISTER.
//
// The shipped process runs with PC = 64 (appEnableFastMath(0)): the product of two
// floats is exact, sum/difference rounds to a 64-bit mantissa, the result is
// one fstp dword rounding. Plain C# float arithmetic rounds EVERY step to
// float and differs by an ulp (VSize 63, Dot 167, Cross 252 corpus cases),
// and at boundaries also in script branches. X87Ext reproduces exactly these
// roundings (double rounding 64 -> 24, as the CPU does).
//
// RENDER WINDOW. Script that runs between appEnableFastMath(1) in URender::PreRender and appEnableFastMath(0) in
// PostRender (HUD, console: UEWorldFrameProducer.FastMathWindow) calls the same bodies with the FPU at PC24: every
// fmul/fadd/fsub/fdiv/fsqrt rounds the register to a 24-bit mantissa, the exponent stays the register's (15 bits).
// Each body below follows the window: the PC24 chain inside it (same instructions, same stores), the PC64 chain
// outside. R24 models one PC24 operation: the exact result of float-grid operands is computed in double and rounded
// once to 24 bits keeping the double's exponent. Double holds the exact product of two floats; for + - / sqrt of
// 24-bit operands the double rounding 53 -> 24 is innocuous (53 >= 2*24 + 2); every intermediate of these chains on
// float inputs lies in [2^-330, 2^260], inside the normal double range, so the register's wide exponent is kept.
// +-0 and +-inf propagate through double as through the x87 (IEEE); a NaN does not (.NET makes its CPU's default NaN
// where x87 makes the indefinite 0xFFC00000, and picks another operand of two NaNs), so every body with a non-finite
// input runs its chain in X87Ext (RW: the window's rounding), which carries NaN sign and payload as the FPU does.
//
// Oracle for all bodies: the real exec natives under unicorn,
// compared against the port registered as natives, also with FPCW 0x007F against the port with the
// window open (PC24). Addresses are given at each body.
using System;
using HP1.Core;
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    internal static class X87
    {
        static X87Ext E(float f) => X87Ext.From(f);
        static X87Ext R(X87Ext v) => X87Ext.Round64(v);

        static bool Window => UEWorldFrameProducer.FastMathWindow;

        static double R24(double d) => X87Ext.Round24(d);

        static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
        static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);
        // One register operation in the caller's window.
        static X87Ext RW(X87Ext v) => Window ? X87Ext.Round24(v) : X87Ext.Round64(v);

        // One x87 operation on two floats and one fstp dword (execAdd/Subtract/Multiply/Divide_FloatFloat, their
        // *Equal forms, the component loops of the vector natives): the C# float result is that value in both windows
        // (a single + - * / of floats rounds alike at 24, 53 or 64 bits then 24), except a NaN, whose sign and payload
        // follow the x87 rules (X87Ext). Checked against the real natives under unicorn, special operands crossed.
        public static float Add(float a, float b) { float r = a + b; return float.IsNaN(r) ? (E(a) + E(b)).ToFloat() : r; }
        public static float Sub(float a, float b) { float r = a - b; return float.IsNaN(r) ? (E(a) - E(b)).ToFloat() : r; }
        public static float Mul(float a, float b) { float r = a * b; return float.IsNaN(r) ? (E(a) * E(b)).ToFloat() : r; }
        public static float Div(float a, float b) { float r = a / b; return float.IsNaN(r) ? X87Ext.Div64(E(a), E(b)).ToFloat() : r; }
        public static Vector3 Add(Vector3 a, Vector3 b) => new Vector3(Add(a.x, b.x), Add(a.y, b.y), Add(a.z, b.z));
        public static Vector3 Sub(Vector3 a, Vector3 b) => new Vector3(Sub(a.x, b.x), Sub(a.y, b.y), Sub(a.z, b.z));
        public static Vector3 Mul(Vector3 a, float b) => new Vector3(Mul(a.x, b), Mul(a.y, b), Mul(a.z, b));
        public static Vector3 Mul(Vector3 a, Vector3 b) => new Vector3(Mul(a.x, b.x), Mul(a.y, b.y), Mul(a.z, b.z));
        // execSqrt 0x10139a00: appSqrt (bare fsqrt) of the argument, fstp dword; a NaN as the FPU makes it.
        public static float Sqrt(float a) { float r = (float)Math.Sqrt(a); return float.IsNaN(r) ? X87Ext.Sqrt64(E(a)).ToFloat() : r; }

        // execVSize 0x1013c763: fld Y; fld Z; Z·Z + Y·Y (faddp) + X·X; fstp qword;
        // appSqrt 0x10126100 (fld qword; fsqrt); fstp dword.
        // PC24: the same chain with every operation rounded to 24 bits = UEVector.Size24 with the operands in
        // (Z, Y, X) order ((Z*Z + Y*Y) + X*X); fstp qword of a 24-bit value is exact.
        public static float VSize(Vector3 v)
        {
            if (Window && Finite(v)) return Vector3.Size24(v.z, v.y, v.x);
            double sum = RW(RW(RW(E(v.z) * E(v.z)) + RW(E(v.y) * E(v.y))) + RW(E(v.x) * E(v.x))).ToDouble();
            return RW(X87Ext.Sqrt64(X87Ext.From(sum))).ToFloat();
        }

        // execDot_VectorVector 0x1013c09e: (Az·Bz + Ay·By) + Ax·Bx; fstp dword.
        public static float Dot(Vector3 a, Vector3 b)
        {
            if (!Finite(a) || !Finite(b))
                return RW(RW(RW(E(a.z) * E(b.z)) + RW(E(a.y) * E(b.y))) + RW(E(a.x) * E(b.x))).ToFloat();
            if (Window)
                return (float)R24(R24(R24((double)a.z * b.z) + R24((double)a.y * b.y)) + R24((double)a.x * b.x));
            var s = R(R(E(a.z) * E(b.z) + E(a.y) * E(b.y)) + E(a.x) * E(b.x));
            return s.ToFloat();
        }

        // execCross_VectorVector 0x1013c16e: X = Ay·Bz − Az·By, Y = Az·Bx − Ax·Bz,
        // Z = Ax·By − Ay·Bx; each is fsubp of two exact products, fstp dword.
        public static Vector3 Cross(Vector3 a, Vector3 b) => new Vector3(
            Diff(a.y, b.z, a.z, b.y), Diff(a.z, b.x, a.x, b.z), Diff(a.x, b.y, a.y, b.x));

        static float Diff(float p, float q, float r, float t)
        {
            if (!Finite(p) || !Finite(q) || !Finite(r) || !Finite(t)) return RW(RW(E(p) * E(q)) - RW(E(r) * E(t))).ToFloat();
            if (Window) return (float)R24(R24((double)p * q) - R24((double)r * t));
            return R(E(p) * E(q) - E(r) * E(t)).ToFloat();
        }

        // execLerp 0x10139f41: fld B; fsub A; fmul Alpha; fadd A; fstp dword.
        public static float Lerp(float alpha, float a, float b)
        {
            if (!Finite(alpha) || !Finite(a) || !Finite(b)) return RW(RW(RW(E(b) - E(a)) * E(alpha)) + E(a)).ToFloat();
            if (Window) return (float)R24(R24(R24((double)b - a) * alpha) + a);
            var d = R(E(b) - E(a));
            var m = R(d * E(alpha));
            return R(m + E(a)).ToFloat();
        }

        // FVector::SafeNormal (Core 0x1014f4e0), as exec natives call it: one owner, UEVector.normalized, which follows
        // the window (SafeNormal64 / SafeNormal24).
        public static Vector3 SafeNormal(Vector3 v) => v.normalized;

        // execDivide_VectorFloat 0x1013b526 / execDivideEqual_VectorFloat 0x1013c49f: `fld B; fdivr 1.0f`, the
        // reciprocal stays in the register; X*rv, Y*rv, Z*rv each fstp dword. PC64 keeps the port's float division
        // (UEVector operator /, judged by the same oracle); PC24 rounds the reciprocal and each product to 24 bits.
        public static Vector3 DivVectorFloat(Vector3 a, float b)
        {
            if (!Finite(a) || !Finite(b) || b == 0f)
            {
                X87Ext r87 = Window ? X87Ext.Div24(E(1f), E(b)) : X87Ext.Div64(E(1f), E(b));
                return new Vector3(RW(E(a.x) * r87).ToFloat(), RW(E(a.y) * r87).ToFloat(), RW(E(a.z) * r87).ToFloat());
            }
            if (!Window) return a / b;
            double rv = R24(1.0 / b);
            return new Vector3((float)R24(a.x * rv), (float)R24(a.y * rv), (float)R24(a.z * rv));
        }

        // execMirrorVectorByNormal 0x1013cd2e: N = FVector::SafeNormal(Normal)
        // (SafeNormal above, checked by the oracle against native Normal);
        // 2Ny, 2Nz — fstp dword, 2Nx stays in the register;
        // d = fstp dword((Ay·Ny + Az·Nz) + Ax·Nx);
        // X = fstp dword(Ax − 2Nx·d)   (product in the register),
        // Y = fstp dword(Ay − fstp dword(2Ny·d)), Z like Y.
        // PC24: every product and difference rounds to 24 bits; the same values are stored (d, 2Ny*d, 2Nz*d) and
        // 2Nx stays in the register (doubling is exact).
        public static Vector3 Mirror(Vector3 a, Vector3 normal)
        {
            Vector3 n = SafeNormal(normal);
            if (!Finite(a) || !Finite(n))
            {
                float dd = RW(RW(RW(E(a.y) * E(n.y)) + RW(E(a.z) * E(n.z))) + RW(E(a.x) * E(n.x))).ToFloat();
                float tyd = RW(E(2f * n.y) * E(dd)).ToFloat(), tzd = RW(E(2f * n.z) * E(dd)).ToFloat();
                return new Vector3(RW(E(a.x) - RW(E(2f * n.x) * E(dd))).ToFloat(), RW(E(a.y) - E(tyd)).ToFloat(),
                    RW(E(a.z) - E(tzd)).ToFloat());
            }
            if (Window)
            {
                float d24 = (float)R24(R24(R24((double)a.y * n.y) + R24((double)a.z * n.z)) + R24((double)a.x * n.x));
                float ty24 = (float)R24(2.0 * n.y * d24), tz24 = (float)R24(2.0 * n.z * d24);
                return new Vector3((float)R24(a.x - R24(2.0 * n.x * d24)), (float)R24((double)a.y - ty24),
                    (float)R24((double)a.z - tz24));
            }
            float d = R(R(E(a.y) * E(n.y) + E(a.z) * E(n.z)) + E(a.x) * E(n.x)).ToFloat();
            float ty = 2f * n.y * d, tz = 2f * n.z * d;      // doubling is exact, the product is one rounding
            float x = R(E(a.x) - E(2f * n.x) * E(d)).ToFloat();
            float y = R(E(a.y) - E(ty)).ToFloat();
            float z = R(E(a.z) - E(tz)).ToFloat();
            return new Vector3(x, y, z);
        }

        // UInput::Exec, AXIS branches (Engine, disasm 0x103a547b..0x103a54bd):
        // fld Delta; fmul A; fmul B; fadd [Value]; fstp [Value] — products and
        // sum in the register, one rounding on store. Hold calls it with B = 1
        // (multiplying by 1 is exact).
        public static float MulMulAdd(float a, float b, float c, float add)
        {
            return R(R(R(E(a) * E(b)) * E(c)) + E(add)).ToFloat();
        }

        // execMultiplyEqual_IntFloat 0x10137ba5: fild A; fmul B; _ftol.
        // |A| < 2^31, B is float: the product (<= 55 bits) is exact in a 64-bit
        // mantissa. _ftol: truncation; NaN/outside int64 -> 0x8000000000000000 (low
        // dword 0), otherwise the low dword.
        // PC24: fmul rounds the product to 24 bits before _ftol (X87Ext: the int*float product can have 55 bits, more
        // than double holds).
        public static int MulIntFloatFtol(int a, float b)
        {
            if (!Finite(b)) return 0;                        // inf·a and NaN are indefinite
            X87Ext prod = X87Ext.From(a) * E(b);
            return Ftol(Window ? X87Ext.Round24(prod) : prod);
        }

        // execDivideEqual_IntFloat 0x10137c90..0x10137cc7: `fcomp 0.0f; test ah,0x40` (C3: B == 0 or NaN) ->
        // _ftol(0.0) = 0; otherwise `fild A; fdiv B; _ftol`: the quotient rounded to 64 bits (PC64; a double quotient
        // changed the low dword of large quotients, e.g. -223 / -5.9e-17) or to 24 bits (PC24). Div64 then Round24 is
        // the one PC24 rounding: a false tie needs |A/B - M| < 2^-64*M for a 25-bit midpoint M, but A - M*B is a
        // nonzero multiple of min(1, ulp(M)*ulp(B)), i.e. at least 2^-49*M*B. B = +-inf gives 0 (Div64).
        public static int DivIntFloatFtol(int a, float b)
        {
            if (b == 0f || float.IsNaN(b)) return 0;
            X87Ext q = X87Ext.Div64(X87Ext.From(a), E(b));
            return Ftol(Window ? X87Ext.Round24(q) : q);
        }

        // ── v030 area B: Core math natives with no shipped call site (bodies from the disassembly) ──
        // Every operation runs in X87Ext at the window's precision (RW); NaN/inf flow as on the FPU.

        // execComplementEqual_FloatFloat 0x101390a0 (native 210, `~=`): fld A; fsub B; fcom 0.0f; C0 -> fchs;
        // fcomp qword 0.0001 (0x1017efe8); C0 -> 1 else 0. Unordered sets C0 in both compares: NaN ~= x is true.
        public static bool ComplementEqual(float a, float b)
        {
            X87Ext d = RW(E(a) - E(b));
            if ((X87Ext.Fcom(d, X87Ext.From(0f)) & 1) != 0) d = -d;
            return (X87Ext.Fcom(d, X87Ext.From(0.0001)) & 1) != 0;
        }

        // execSmerp 0x10139fd0 (native 248): a2 = α·α; h = a2·3.0f (0x1017eff4) − (a2·α + a2·α); fstp dword(A + (B − A)·h).
        public static float Smerp(float alpha, float a, float b)
        {
            X87Ext a2 = RW(E(alpha) * E(alpha));
            X87Ext t = RW(a2 * E(3f));
            X87Ext a3 = RW(a2 * E(alpha));
            X87Ext h = RW(t - RW(a3 + a3));
            return RW(RW(RW(E(b) - E(a)) * h) + E(a)).ToFloat();
        }

        // execInvert 0x1013c8d0 (native 227): FCoords(0, X, Y, Z).Inverse() (0x1014e700), the axes written back.
        // Inverse: RDet = 1.0f / ((Y.z·Z.x − Y.x·Z.z)·X.y + (Z.y·Y.x − Y.y·Z.x)·X.z + (Y.y·Z.z − Z.y·Y.z)·X.x) (fdivr,
        // kept in the register). Each result axis is a row (a, b, c)·RDet, a from the register, b and c stored as dwords
        // first; X' = row3, Y' = row2, Z' = row1 (stores 0x1014e74e..0x1014e842).
        public static (Vector3 X, Vector3 Y, Vector3 Z) Invert(Vector3 x, Vector3 y, Vector3 z)
        {
            X87Ext P(float p, float q) => RW(E(p) * E(q));
            X87Ext D(float p, float q, float r, float s) => RW(P(p, q) - P(r, s));
            X87Ext det = RW(RW(RW(D(y.z, z.x, y.x, z.z) * E(x.y)) + RW(D(z.y, y.x, y.y, z.x) * E(x.z)))
                + RW(D(y.y, z.z, z.y, y.z) * E(x.x)));
            X87Ext rdet = Window ? X87Ext.Div24(E(1f), det) : X87Ext.Div64(E(1f), det);
            Vector3 Row(X87Ext a, X87Ext b, X87Ext c) => new Vector3(RW(a * rdet).ToFloat(),
                RW(E(b.ToFloat()) * rdet).ToFloat(), RW(E(c.ToFloat()) * rdet).ToFloat());
            Vector3 r1 = Row(D(z.y, y.x, y.y, z.x), D(z.x, x.y, x.x, z.y), D(x.x, y.y, y.x, x.y));
            Vector3 r2 = Row(D(y.z, z.x, z.z, y.x), D(z.z, x.x, x.z, z.x), D(x.z, y.x, y.z, x.x));
            Vector3 r3 = Row(D(z.z, y.y, y.z, z.y), D(x.z, z.y, z.z, x.y), D(y.z, x.y, x.z, y.y));
            return (r3, r2, r1);
        }

        // FCoords::OrthoRotation 0x1014ed10 (execOrthoRotation 0x1013e1b0, FCoords(0, X, Y, Z)). Its own scale
        // C = qword [0x1017fa50] = 10430.37806021874 (NOT FVector::Rotation's 0x1017fa40). Yaw = _ftol(appAtan2(X.y, X.x)·C);
        // Pitch = _ftol(appAtan2(X.z, appSqrt(X.y·X.y + X.x·X.x))·C), the sum and the root stored as qwords; Y' = YAxis of
        // GMath.UnitCoords / (Pitch, Yaw, 0) (0x10103369, the GetAxes body); Roll = _ftol(appAtan2(Y'·Z, Y'·Y)·C), each
        // dot in (z, y, x) order stored as a qword. appAtan2 = fpatan (0x101260f0), _ftol 0x10176f72. Every register
        // operation at the window's precision.
        static readonly X87Ext OrthoScale = X87Ext.From(BitConverter.Int64BitsToDouble(0x40C45F306446F9B4));
        public static UERotator OrthoRotation(Vector3 x, Vector3 y, Vector3 z)
        {
            static int FtolAny(X87Ext v) => v.IsNaN || v.IsInfinity ? 0 : Ftol(v);
            static X87Ext Q(X87Ext v) => v.IsNaN || v.IsInfinity ? v : X87Ext.From(v.ToDouble());   // fstp qword
            int yaw = FtolAny(RW(UEX87Transcendental.Fpatan(E(x.y), E(x.x)) * OrthoScale));
            X87Ext xy = Q(RW(RW(E(x.y) * E(x.y)) + RW(E(x.x) * E(x.x))));
            X87Ext d = Q(RW(X87Ext.Sqrt64(xy)));
            int pitch = FtolAny(RW(UEX87Transcendental.Fpatan(E(x.z), d) * OrthoScale));
            Vector3 c = UEValue.GetAxes(new UERotator(pitch, yaw, 0)).Y;
            X87Ext Dot(Vector3 a) => Q(RW(RW(RW(E(c.z) * E(a.z)) + RW(E(c.y) * E(a.y))) + RW(E(c.x) * E(a.x))));
            X87Ext dy = Dot(y), dz = Dot(z);
            return new UERotator(pitch, yaw, FtolAny(RW(UEX87Transcendental.Fpatan(dz, dy) * OrthoScale)));
        }

        // _ftol of a finite register value: truncation; outside int64 -> 0x8000000000000000 (low dword 0), otherwise
        // the low dword.
        static int Ftol(X87Ext v)
        {
            System.Numerics.BigInteger t = v.TruncateToInteger();
            if (t >= System.Numerics.BigInteger.One << 63 || t < -(System.Numerics.BigInteger.One << 63)) return 0;
            return unchecked((int)(long)t);
        }
    }
}
