// x87 TRANSCENDENTALS OF THE SHIPPED CODE (D-71).
//
// What shipped executes (Core.dll disasm): appSin 0x101035df -> 0x101260a0 `fld qword; fsin; ret`, appCos 0x101260b0
// `fcos`, appAtan2 0x10102ab8 -> 0x101260f0 `fld qword y; fld qword x; fpatan; ret`, appSqrt 0x10126100 `fsqrt`;
// appAcos 0x10101361 -> 0x101260c0 `fld qword; jmp [MSVCRT!_CIacos]` (import 0x1025050c). The DLLs ship no msvcrt:
// _CIacos is the system MSVCRT.dll; the reference is Microsoft msvcrt 6.00.8337.0 (author decision 2026-10-30).
//
// MODEL (author decision 2026-10-30): fsin, fcos and fpatan return the exact function value CORRECTLY ROUNDED to the
// 64-bit mantissa (nearest even), whatever the precision control (the hardware table shows PC does not round them).
// fsin / fcos / fptan first reduce with the x87's own π/2: P66 = 0xC90FDAA22168C234C·2^-67, π/2 to 66 significant
// bits (Intel SDM vol. 1 §8.3.10): r = x − k·P66 EXACTLY, k nearest, then the correctly rounded sin / cos / tan of r
// in quadrant k. Measured 2026-11-01 on the Deck (Zen 2; 168386 inputs incl. negatives, k·π/2 neighbours, the closest
// double to a multiple of π/2 in every binade up to 2^63): every row matches this model to <= 1 ulp of 80 bits; the
// true-π/2 reduction misses 2 ulps already at x ≈ −π and 2^-13 relative at 2^55.
// Evaluated here in arbitrary precision with a rigorous error bound and a Ziv loop: the value is computed to P bits
// with an interval [v − err, v + err]; when both ends round to the same 64-bit value that value is returned, otherwise
// P doubles. sin/cos/atan of a nonzero rational are transcendental (Lindemann), never a rounding midpoint, so the loop
// ends; it refuses loudly past 2^16 bits. x87 operand domain: |x| ≥ 2^63 sets C2 and leaves st0 unchanged (the shipped
// appSin/appCos test no C2, so the caller gets the operand back).
//
// _CIacos is the instruction sequence of msvcrt 6.00.8337.0 (0x78028905..) on top of that model — see CIacos.
//
// Judge: HeadlessSmoke --x87ref <table> (tools/x87_reference, a Steam Deck / Zen 2 run with the genuine msvcrt).
using System;
using System.Collections.Concurrent;
using System.Numerics;

namespace HP1.Core
{
    public static class UEX87Transcendental
    {
        // SelfTest: false compares the fixed-point fast path with the arbitrary-precision path on a corpus.
        [ThreadStatic] internal static bool NoFastPath;
        // How often the fast path left a fsin / fcos / fpatan to the arbitrary-precision path (differential test only).
        [ThreadStatic] internal static long SlowCount;

        // SelfTest seam (headless only): every finite nonzero fsin / fcos / fpatan result moves by this many units of its
        // 64th mantissa bit — the class by which x87 hardware may differ from the model (Zen 2: 1 ulp of 80 bits on 950 of
        // 64054 table rows). SelfTest st0_consumers_ignore_one_ulp re-runs the consumer judges with ±1.
        [ThreadStatic] internal static int UlpNudge;

        static X87Ext Nudge(X87Ext v)
        {
            if (UlpNudge == 0 || !v.IsFinite || v.Sign == 0) return v;
            var (m, e) = v.Scaled;
            int shift = 64 - (int)BigInteger.Abs(m).GetBitLength();
            m <<= shift;
            return X87Ext.FromScaled(m + m.Sign * UlpNudge, e - shift);
        }

        public static X87Ext Fsin(X87Ext x) => Nudge(SinCos(x, cos: false));

        public static X87Ext Fcos(X87Ext x) => Nudge(SinCos(x, cos: true));

        // fpatan: st1 = y, st0 = x -> atan2(y, x) in st0 (the two pops of fpatan leave one value). The sign bits of zero
        // operands decide (X87Ext carries them): (±0, −0 or x < 0) -> ±π, (±0, +0 or x > 0) -> ±0, (y ≠ 0, ±0) -> ±π/2.
        public static X87Ext Fpatan(X87Ext y, X87Ext x) => Nudge(Atan2(y, x));

        static X87Ext Atan2(X87Ext y, X87Ext x)
        {
            if (y.IsNaN || x.IsNaN) return X87Ext.PickNaN(y, x);
            y = X87Ext.Round64(y); x = X87Ext.Round64(x);
            bool yNeg = y.IsNegative, xNeg = x.IsNegative;
            // FPATAN special operands (Intel SDM table 3-49, exceptions masked).
            int quarters = -1;                                  // result = ±quarters·π/4
            if (y.IsInfinity) quarters = x.IsInfinity ? (xNeg ? 3 : 1) : 2;
            else if (x.IsInfinity || y.Sign == 0) quarters = xNeg ? 4 : 0;
            else if (x.Sign == 0) quarters = 2;
            if (quarters == 0) return yNeg ? X87Ext.NegativeZero : X87Ext.From(0f);
            if (quarters > 0) return AtanQuarters(quarters, yNeg);

            if (!NoFastPath && Fast.Atan(y, x, xNeg, yNeg, out X87Ext fast)) return fast;
            if (!NoFastPath) SlowCount++;
            var (my, ey) = y.Scaled; var (mx, ex) = x.Scaled;
            int e0 = Math.Min(ey, ex);
            BigInteger Y = BigInteger.Abs(my) << (ey - e0), X = BigInteger.Abs(mx) << (ex - e0);
            bool swap = Y > X;
            BigInteger p = swap ? X : Y, q = swap ? Y : X;      // 0 < p/q <= 1
            // atan(p/q) = atan(i/16) + atan((16p − iq)/(16q + ip)), i = round(16p/q): the second argument is exact and
            // at most 1/32 in magnitude.
            int i = (int)((32 * p + q) / (2 * q));
            BigInteger dn = 16 * p - i * q, nn = 16 * q + i * p;
            return AtanSlow(p, q, i, dn, nn, swap, xNeg, yNeg);
        }

        // The Ziv evaluations live in their own methods: a lambda capturing Atan2's / SinCos's locals made the compiler
        // allocate its closure on EVERY call, fast path included (allocation only).
        static X87Ext AtanQuarters(int quarters, bool yNeg)
            => Ziv(p => { Approx v = Pi(p + 8); v = new Approx(v.V * quarters, v.Err * quarters, v.Exp - 2); return yNeg ? -v : v; });

        static X87Ext AtanSlow(BigInteger p, BigInteger q, int i, BigInteger dn, BigInteger nn, bool swap, bool xNeg, bool yNeg)
        {
            return Ziv(prec =>
            {
                int g = prec + 10;
                Approx a = i == 0
                    ? EulerAtan(p, q, g + (int)(q.GetBitLength() - p.GetBitLength()) + 1)   // relative: p/q may be tiny
                    : AtanSixteenth(i, g) + (dn.Sign < 0 ? -EulerAtan(-dn, nn, g) : EulerAtan(dn, nn, g));
                if (swap) a = Pi(g).Half - a;
                if (xNeg) a = Pi(g) - a;
                return yNeg ? -a : a;
            });
        }

        // ── _CIacos, msvcrt 6.00.8337.0 ──────────────────────────────────────────────────────────────────────────
        // 0x78028905 sub esp,8; fst qword [esp]; call 0x7802c33d (eax = hi dword & 0x7ff00000, or the whole hi dword
        //   with ZF=1 when the exponent is 0x7ff); call 0x78028922:
        // 0x78028922 push edx; fnstcw [esp]; je 0x780289e3 (NaN/inf); cw != 0x27f -> call 0x7802c2ca: fldcw
        //   (cw & 0x300) | 0x7f — the caller's PRECISION CONTROL kept, RC = nearest, every exception masked;
        //   eax >= 0x3ff00000 (|x| >= 1) -> 0x7802899a;
        // 0x78028941 fld1; fadd st,st(1) (1 + x); fld1; fsub st,st(2) (1 − x); fmulp st(1); fsqrt; fxch st(1); fpatan
        //   -> atan2(sqrt((1 − x)(1 + x)), x), each arithmetic step rounded at the caller's PC, fpatan by the model;
        // 0x78028951 cmp [0x7803b718],0 (read-only in the image: no write to it in msvcrt's code) -> 0; edx = 0xd,
        //   ecx = "acos"; saved cw == 0x27f -> return st0 as is; (cw & 0x20) == 0 -> fldcw, return; fnstsw & 0x20 (the
        //   STICKY precision flag) == 0 -> fldcw, return; else eax = 8, call 0x7802c2aa: fstp qword [ebp−8] (THE RESULT
        //   IS ROUNDED TO DOUBLE), _87except 0x7802d303 (type 8 = inexact, masked in the caller's cw: retval untouched),
        //   fld qword [ebp−8], fldcw the saved cw.
        //   The shipped control words are 0x037F (logic) and 0x007F (render window): PM set, not 0x27f; fpatan of these
        //   operands is always inexact, so PE is set and the result is the double.
        // 0x7802899a |x| >= 2 or 1 < |x| < 2 -> fstp; fld tbyte [0x7803ae20] (the indefinite NaN), error path type 1;
        //   |x| == 1 -> fstp; fldz (x = +1) / fldpi (x = −1), then 0x78028951.
        // 0x780289e3 NaN -> NaN (a signalling one quieted by fadd qword [0x78030018]); ±inf -> the indefinite NaN.
        // The error paths (_87except, _matherr, errno) only change the value through a user _matherr: none modelled,
        // the value is NaN.
        public static X87Ext CIacos(X87Ext x, bool pc24)
        {
            if (x.IsNaN) return x;                              // fld quieted it; fadd of a number keeps it
            if (x.IsInfinity) return X87Ext.Indefinite;
            double d = x.ToDouble();                            // fst qword [esp]: the classification reads the double
            X87Ext one = X87Ext.From(1f);
            if (Math.Abs(d) >= 1.0)
            {
                if (Math.Abs(d) != 1.0) return X87Ext.Indefinite;   // fld tbyte [0x7803ae20]
                if (d > 0) return X87Ext.From(0f);              // fldz: exact, rounding to double changes nothing
                // fldpi sets no precision flag: the result is the 80-bit π or the double π depending on the caller's
                // sticky PE, which the port does not model. No shipped caller passes −1 (SlerpQuat: |cos| < 1).
                throw new NotSupportedException("_CIacos(-1): result depends on the caller's sticky x87 precision flag (not modelled)");
            }
            X87Ext R(X87Ext v) => pc24 ? X87Ext.Round24(v) : X87Ext.Round64(v);
            X87Ext a = R(one + x);
            X87Ext b = R(one - x);
            X87Ext s = R(X87Ext.Sqrt64(R(b * a)));
            X87Ext r = Fpatan(s, x);
            return X87Ext.From(r.ToDouble());                   // 0x7802c277 fstp qword; 0x7802c29a fld qword
        }

        // ── fast path: 127-bit fixed point ───────────────────────────────────────────────────────────────────────
        // The same correctly rounded value by the same decision: fixed-point numbers in UInt128 with an error bound in
        // units of the last place (every truncation and constant counted, as in the comments); a result is returned only
        // when both ends of its interval round to the same 64-bit value. Otherwise — or outside the fast range (|x| >= 2^59
        // for sin/cos) — the arbitrary-precision path answers. Accuracy ~2^−120, so the fallback is a ~2^−55 event.
        static class Fast
        {
            const int Q = 127, NS = 16, NA = 13;
            // Horner terms: sin/cos tails 1/35!, 1/34! < 2^−127 (u < 1); atan tail v^14/29 < 2^−127 (v <= 2^−10).
            static readonly UInt128[] SinC = Coeffs(NS, n => Fact(2 * n + 1)), CosC = Coeffs(NS, n => Fact(2 * n));
            static readonly UInt128[] AtanC = Coeffs(NA, n => 2 * n + 1);
            // Terms by the size of the Horner argument: [L] = the fewest N whose first dropped term c_{N+1}·u^{N+1} is
            // < 1 unit for every u < 2^(L−127) (L = bit length of u in Q127; the series alternate with decreasing terms,
            // so the dropped tail is below that term). [128] = NS / NA, the u < 1 bound above.
            static readonly int[] SinN = Terms(NS, n => Fact(2 * n + 1)), CosN = Terms(NS, n => Fact(2 * n)), AtanN = Terms(NA, n => 2 * n + 1);
            // Constants floor-rounded from 300-bit values: error <= 2 units each.
            static readonly UInt128 HalfPi125 = Const(Pi(300).Half, 125), Pi125 = Const(Pi(300), 125);
            static readonly UInt128[] T16 = Sixteenths();

            static BigInteger Fact(int n) { BigInteger f = 1; for (int k = 2; k <= n; k++) f *= k; return f; }
            static int[] Terms(int nMax, Func<int, BigInteger> den)
            {
                var t = new int[129];
                for (int l = 0; l <= 128; l++)
                {
                    int n = 0;
                    // 2^127·(2^(l−127))^(n+1) / den(n+1) < 1
                    while (n < nMax && 127 + (long)(l - 127) * (n + 1) >= 0 && BigInteger.One << (127 + (l - 127) * (n + 1)) >= den(n + 1)) n++;
                    t[l] = n;
                }
                return t;
            }
            static UInt128[] Coeffs(int n, Func<int, BigInteger> den)
            {
                var c = new UInt128[n + 1];
                for (int k = 0; k <= n; k++) c[k] = (UInt128)((BigInteger.One << Q) / den(k));   // error < 1
                return c;
            }
            static UInt128 Const(Approx a, int q)
            {
                Approx c = a.At(-q);
                if (c.Err > 2) throw new InvalidOperationException("UEX87Transcendental: constant error bound");
                return (UInt128)c.V;
            }
            static UInt128[] Sixteenths()
            {
                var t = new UInt128[17];
                for (int i = 1; i <= 16; i++) t[i] = Const(EulerAtan(i, 16, 300), Q);
                return t;
            }

            static UInt128 Mul64(ulong a, ulong b) { ulong hi = Math.BigMul(a, b, out ulong lo); return new UInt128(hi, lo); }

            // floor(a·b / 2^127); a·b < 2^255.
            static UInt128 MulHi(UInt128 a, UInt128 b)
            {
                ulong a0 = (ulong)a, a1 = (ulong)(a >> 64), b0 = (ulong)b, b1 = (ulong)(b >> 64);
                UInt128 p00 = Mul64(a0, b0), p01 = Mul64(a0, b1), p10 = Mul64(a1, b0), p11 = Mul64(a1, b1);
                UInt128 mid = p01 + p10;
                UInt128 midCarry = mid < p01 ? (UInt128)1 << 64 : 0;
                UInt128 low = p00 + (mid << 64);
                UInt128 high = p11 + (mid >> 64) + midCarry + (low < p00 ? 1u : 0u);
                return (high << 1) | (low >> 127);
            }

            // floor(a·b / 2^64).
            static UInt128 MulHi64(ulong a, UInt128 b) => Mul64(a, (ulong)(b >> 64)) + (Mul64(a, (ulong)b) >> 64);

            // Σ (−1)^n c_n u^n by Horner, u < 1 (Q127), the N = terms[bit length of u] first terms: |result − S(u)| <=
            // 2N + 3 units + |S'|·err(u) (N + 1 floored coefficients, N floored products, the dropped tail < 1).
            static UInt128 Horner(UInt128[] c, int[] terms, UInt128 u, out int n)
            {
                n = terms[128 - (int)UInt128.LeadingZeroCount(u)];
                UInt128 h = c[n];
                for (int k = n - 1; k >= 0; k--) h = c[k] - MulHi(u, h);
                return h;
            }

            static bool Decide(UInt128 v, UInt128 err, int exp, bool neg, out X87Ext res)
            {
                res = default;
                if (v <= err || v > UInt128.MaxValue - err) return false;
                X87Ext a = X87Ext.Round64(X87Ext.FromUInt128(v - err, neg, exp));
                X87Ext b = X87Ext.Round64(X87Ext.FromUInt128(v + err, neg, exp));
                if (X87Ext.Fcom(a, b) != X87Ext.C3) return false;
                res = a;
                return true;
            }

            public static bool SinCos(X87Ext x, bool cos, out X87Ext res)
            {
                res = default;
                if (!x.TryMantissa64(out ulong m, out int e, out bool neg)) return false;
                int len = 64 - BitOperations.LeadingZeroCount(m), top = len + e;
                if (top > 59) return false;                     // |x| >= 2^59
                UInt128 r; UInt128 eR; int quadrant = 0; bool rNeg = false;
                if (top <= -1)
                {
                    int s = e + Q;                              // |x| < 1/2: r = x, Q127 (only u needs it)
                    r = s >= 0 ? (UInt128)m << s : s > -64 ? (UInt128)(m >> -s) : 0;
                    eR = s >= 0 ? 0u : 1u;
                }
                else
                {
                    // k = floor((|x| + P66/2)/P66) (the nearest integer), in Q67: X = x·2^67 exact (|x| >= 1/2: e >= −64;
                    // |x| < 2^59: X < 2^126), P66 = P66Mantissa exact and even, so P/2 is an integer; r = X − k·P exact,
                    // |r| <= P/2 < 2^67, then Q127. k from a double estimate, made exact by its defining inequalities.
                    UInt128 X = (UInt128)m << (e + 67), P = P66Mantissa, T = X + P / 2;
                    ulong k = (ulong)((double)X / (double)P);
                    while (k > 0 && P * k > T) k--;
                    while (T - P * k >= P) k++;
                    UInt128 K = P * k;
                    if (X >= K) r = X - K; else { r = K - X; rNeg = true; }
                    r <<= 60; eR = 0;
                    quadrant = (int)(k & 3);
                }
                if (cos) quadrant = (quadrant + 1) & 3;         // cos x = sin(x + π/2)
                UInt128 u = MulHi(r, r), eU = 2 * eR + 2;
                bool vNeg = quadrant >= 2;
                UInt128 v, err; int exp = -Q;
                if ((quadrant & 1) != 0) { v = Horner(CosC, CosN, u, out int nc); err = 2 * (uint)nc + 3 + eU; }
                else
                {
                    UInt128 sv = Horner(SinC, SinN, u, out int ns), eS = 2 * (uint)ns + 3 + eU;
                    if (top <= -1)
                    {
                        // relative: the exact mantissa times S
                        ulong mn = m << (64 - len);
                        v = MulHi64(mn, sv); err = eS + 2; exp = e - (64 - len) - 63;
                    }
                    else { v = MulHi(r, sv); err = eR + eS + 2; }
                    if (rNeg) vNeg = !vNeg;
                }
                if (neg && !cos) vNeg = !vNeg;
                return Decide(v, err, exp, vNeg, out res);
            }

            static UInt128 ShlDiv(UInt128 a, int k, UInt128 b) => X87Ext.ShlDivRem(a, k, b).Q;

            // atan(p/q) of the Round64'd operands without BigInteger: p = the smaller, q = the larger magnitude, i =
            // round(16p/q), dn/nn = the reduced argument — the same integers (up to a common power of two, which the
            // quotients below do not see) and the same fixed-point values as the arbitrary-precision path; then the
            // quadrant. Mantissas normalised to bit 63: p/q = (mp/mq)·2^−d, d >= 0.
            public static bool Atan(X87Ext y, X87Ext x, bool xNeg, bool yNeg, out X87Ext res)
            {
                res = default;
                if (!y.TryMantissa64(out ulong my, out int ey, out _) || !x.TryMantissa64(out ulong mx, out int ex, out _)) return false;
                int ly = BitOperations.LeadingZeroCount(my), lx = BitOperations.LeadingZeroCount(mx);
                my <<= ly; ey -= ly; mx <<= lx; ex -= lx;
                bool swap = ey > ex || (ey == ex && my > mx);
                ulong mp = swap ? mx : my, mq = swap ? my : mx;
                long dl = (long)(swap ? ey : ex) - (swap ? ex : ey);
                // d >= 6: p/q < 2^−5 -> 32p < q, i = 0. Otherwise P = mp, Q = mq·2^d < 2^70: i, dn, nn exact in UInt128.
                int i = 0; UInt128 P = mp, Qv = 0;
                if (dl < 6) { Qv = (UInt128)mq << (int)dl; i = (int)((32 * P + Qv) / (2 * Qv)); }
                UInt128 v, err; int exp;
                if (i == 0)
                {
                    // relative: s = 125 + bitlen(q) − bitlen(p) = 125 + d; z = floor(p·2^s/q) = floor(mp·2^125/mq).
                    if (dl > 100000 - 125) return false;
                    int s = 125 + (int)dl;
                    if (s < 128) return false;
                    UInt128 z = ShlDiv(mp, 125, mq);
                    int sh = 2 * s - 254;
                    UInt128 w = sh >= 128 ? 0 : MulHi(z, z) >> sh;
                    UInt128 a = Horner(AtanC, AtanN, w, out int na), eA = 2 * (uint)na + 3 + 3;
                    v = MulHi(z, a); err = eA + 3; exp = -s;
                }
                else
                {
                    UInt128 ip = (uint)i * P, iq = (uint)i * Qv, p16 = 16 * P;
                    bool dnNeg = p16 < iq;
                    UInt128 dn = dnNeg ? iq - p16 : p16 - iq, nn = 16 * Qv + ip;
                    UInt128 z = ShlDiv(dn, Q, nn);              // |z| <= 1/32, error < 1
                    UInt128 w = MulHi(z, z);
                    UInt128 a = Horner(AtanC, AtanN, w, out int na), eA = 2 * (uint)na + 3 + 2;
                    UInt128 t = MulHi(z, a);
                    v = dnNeg ? T16[i] - t : T16[i] + t; err = eA + 3 + 2; exp = -Q;
                }
                if (swap || xNeg)
                {
                    int shift = -125 - exp;                     // to Q125 (results up to π)
                    if (shift >= 128) { v = 0; err = 2; } else { v >>= shift; err = (err >> shift) + 2; }
                    if (swap) { v = HalfPi125 - v; err += 2; }
                    if (xNeg) { v = Pi125 - v; err += 2; }
                    exp = -125;
                }
                return Decide(v, err, exp, yNeg, out res);
            }
        }

        // ── evaluation ───────────────────────────────────────────────────────────────────────────────────────────

        // A value known to lie in [(V − Err)·2^Exp, (V + Err)·2^Exp].
        readonly struct Approx
        {
            public readonly BigInteger V, Err;
            public readonly int Exp;
            public Approx(BigInteger v, BigInteger err, int exp) { V = v; Err = err; Exp = exp; }
            public Approx At(int exp)
            {
                if (exp <= Exp) { int s = Exp - exp; return new Approx(V << s, Err << s, exp); }
                int k = exp - Exp;                              // floor: < 1 new unit; Err rounded up: +1
                return new Approx(V >> k, (Err >> k) + 2, exp);
            }
            public Approx Half => new Approx(V, Err, Exp - 1);
            public static Approx operator +(Approx a, Approx b)
            {
                int e = Math.Min(a.Exp, b.Exp); a = a.At(e); b = b.At(e);
                return new Approx(a.V + b.V, a.Err + b.Err, e);
            }
            public static Approx operator -(Approx a) => new Approx(-a.V, a.Err, a.Exp);
            public static Approx operator -(Approx a, Approx b) => a + -b;
            public static Approx operator *(Approx a, Approx b) =>
                new Approx(a.V * b.V, BigInteger.Abs(a.V) * b.Err + BigInteger.Abs(b.V) * a.Err + a.Err * b.Err, a.Exp + b.Exp);
        }

        // Ziv loop: evaluate at increasing precision until the interval rounds to one 64-bit value.
        static X87Ext Ziv(Func<int, Approx> eval)
        {
            for (int prec = 96; prec <= 1 << 16; prec *= 2)
            {
                Approx a = eval(prec);
                BigInteger lo = a.V - a.Err, hi = a.V + a.Err;
                if (lo.Sign == 0 || lo.Sign != hi.Sign) continue;
                X87Ext rl = X87Ext.Round64(X87Ext.FromScaled(lo, a.Exp)), rh = X87Ext.Round64(X87Ext.FromScaled(hi, a.Exp));
                if (X87Ext.Fcom(rl, rh) == X87Ext.C3) return rl;
            }
            throw new ArithmeticException("UEX87Transcendental: correct rounding not decided at 2^16 bits");
        }

        // The x87 reduction constant π/2 ≈ P66Mantissa·2^-67 (68 bits, the last two zero: 66 significant bits).
        static readonly UInt128 P66Mantissa = (UInt128.One << 64) * 0xC + 0x90FDAA22168C234CUL;

        static X87Ext SinCos(X87Ext x, bool cos)
        {
            if (x.IsNaN) return x;
            if (x.IsInfinity) return X87Ext.Indefinite;         // invalid operation, masked
            x = X87Ext.Round64(x);
            if (x.Sign == 0) return cos ? X87Ext.From(1f) : x;
            if (!NoFastPath && Fast.SinCos(x, cos, out X87Ext fast)) return fast;
            if (!NoFastPath) SlowCount++;
            var (m, e) = x.Scaled;
            bool neg = m.Sign < 0;
            BigInteger mag = BigInteger.Abs(m);
            int top = (int)mag.GetBitLength() + e;              // 2^(top−1) <= |x| < 2^top
            if (top > 63) return x;                             // |x| >= 2^63: C2 = 1, st0 unchanged
            return SinCosSlow(mag, e, top, neg, cos);
        }

        static X87Ext SinCosSlow(BigInteger mag, int e, int top, bool neg, bool cos)
            => Ziv(prec => SinCosAt(mag, e, top, neg, cos, prec));

        static Approx SinCosAt(BigInteger mag, int e, int top, bool neg, bool cos, int prec)
        {
            int quadrant = 0;
            Approx r;
            if (top <= -1) r = new Approx(mag, 0, e);           // |x| < 1/2: no reduction, r exact
            else
            {
                // r = |x| − k·P66 exactly at F fraction bits (F >= 67), k nearest.
                int f = prec + 70 + top;
                BigInteger halfPi = (BigInteger)P66Mantissa << (f - 67);
                BigInteger xf = mag << (e + f);
                BigInteger k = (2 * xf + halfPi) / (2 * halfPi);
                r = new Approx(xf - k * halfPi, 0, -f);
                quadrant = (int)(k & 3);
                long len = BigInteger.Abs(r.V).GetBitLength();
                if (len > prec + 24) r = r.At(r.Exp + (int)(len - prec - 24));
            }
            if (cos) quadrant = (quadrant + 1) & 3;             // cos x = sin(x + π/2)
            int g = prec + 10;
            Approx u = (r * r).At(-g);                          // r² <= (π/4 + slop)² < 1
            Approx v = (quadrant & 1) != 0 ? Series(u, g, cos: true) : r * Series(u, g, cos: false);
            if (quadrant >= 2) v = -v;
            return neg && !cos ? -v : v;
        }

        // fptan: tan x correctly rounded (then fld1, which appTan pops: 0x101260d4 fptan; fstp st(0)). |x| >= 2^63:
        // C2 = 1 and NOTHING is pushed, so appTan's fstp st(0) pops the operand itself and the caller reads an empty
        // register: stack underflow, masked -> the indefinite NaN.
        public static X87Ext AppTan(X87Ext x)
        {
            if (x.IsNaN) return x;                              // NaN in st0, fld1 pushed and popped by appTan
            if (x.IsInfinity) return X87Ext.Indefinite;
            x = X87Ext.Round64(x);
            if (x.Sign == 0) return x;
            var (m, e) = x.Scaled;
            bool neg = m.Sign < 0;
            BigInteger mag = BigInteger.Abs(m);
            int top = (int)mag.GetBitLength() + e;
            if (top > 63) return X87Ext.Indefinite;
            // Memo (port, allocation/CPU only — a pure function of the rounded operand): the frame's ComputeRenderSize asks
            // the same FOV every DrawWorld. Cleared past 4096 entries, like ExpMemo.
            if (TanMemo.TryGetValue((m, e), out X87Ext hit)) return hit;
            if (TanMemo.Count > 4096) TanMemo.Clear();
            return TanMemo[(m, e)] = Ziv(prec => Div(SinCosAt(mag, e, top, neg, false, prec + 8), SinCosAt(mag, e, top, neg, true, prec + 8), prec));
        }
        static readonly ConcurrentDictionary<(BigInteger, int), X87Ext> TanMemo = new();

        // a/b to about prec + 30 bits: |a/b − ã/b̃| <= (ea·|b̃| + |ã|·eb) / (|b̃|·(|b̃| − eb)), plus 1 for the quotient.
        static Approx Div(Approx a, Approx b, int prec)
        {
            BigInteger bv = BigInteger.Abs(b.V);
            if (bv <= b.Err) return new Approx(0, BigInteger.One << 30, 0);   // undecided: the Ziv loop goes on
            int sh = Math.Max(0, prec + 30 + (int)(bv.GetBitLength() - BigInteger.Abs(a.V).GetBitLength()));
            BigInteger q = (a.V << sh) / b.V;
            BigInteger num = (a.Err * bv + BigInteger.Abs(a.V) * b.Err) << sh, den = bv * (bv - b.Err);
            return new Approx(q, num / den + 2, a.Exp - b.Exp - sh);
        }

        // appExp (Core 0x10126060, C5 2026-11-01): fldl2e; fmul qword [esp+4]; fld st0; frndint; fxch st1; fsub st,st1;
        // f2xm1; fld1; faddp st1,st; fscale; fstp st1. fmul, fsub and fadd round at the caller's precision control (PC24
        // inside the render window, PC64 outside); fldl2e (RC nearest: 0x3FFF:B8AA3B295C17F0BC), frndint (nearest even)
        // and fscale (exact) do not depend on PC; f2xm1 by the model (F2xm1). Operands that would make fscale overflow,
        // underflow or meet inf/NaN are refused: no shipped caller reaches them (particle damping, AWind fluctuation).
        static readonly X87Ext L2E = X87Ext.FromScaled(new BigInteger(0xB8AA3B295C17F0BCUL), -63);
        // Memo: the Ziv evaluation costs ~30 us; particle substeps repeat (step, Damping) pairs. Cleared past 4096 entries.
        static readonly ConcurrentDictionary<(double, bool), X87Ext> ExpMemo = new();
        public static X87Ext AppExp(double x, bool pc24)
        {
            if (ExpMemo.TryGetValue((x, pc24), out X87Ext hit)) return hit;
            if (ExpMemo.Count > 4096) ExpMemo.Clear();
            return ExpMemo[(x, pc24)] = AppExpBody(x, pc24);
        }

        static X87Ext AppExpBody(double x, bool pc24)
        {
            X87Ext R(X87Ext v) => pc24 ? X87Ext.Round24(v) : X87Ext.Round64(v);
            X87Ext t = R(L2E * X87Ext.From(x));
            if (!t.IsFinite) throw new NotSupportedException($"appExp({x}): non-finite operand (not modelled)");
            var (m, e) = t.Scaled;
            BigInteger n = m;
            if (e < 0)
            {
                BigInteger half = BigInteger.One << (-e - 1), q = BigInteger.DivRem(BigInteger.Abs(m) + half, half << 1, out BigInteger rem);
                if (rem.IsZero && !q.IsEven) q -= 1;            // a tie rounds to even
                n = m.Sign < 0 ? -q : q;
            }
            else n = m << e;
            if (BigInteger.Abs(n) > 16000) throw new NotSupportedException($"appExp({x}): fscale out of the 80-bit range (not modelled)");
            X87Ext f = R(t - X87Ext.FromScaled(n, 0));
            X87Ext s = R(X87Ext.From(1f) + F2xm1(f));
            var (sm, se) = s.Scaled;
            return X87Ext.FromScaled(sm, se + (int)n);
        }

        // f2xm1: 2^x − 1 CORRECTLY ROUNDED to the 64-bit mantissa (the author's rule for fsin/fpatan, 2026-10-30), PC not
        // applied; ±0 -> ±0. Only |x| <= 1/2 is modelled (frndint leaves appExp's operand there); beyond that a refusal.
        public static X87Ext F2xm1(X87Ext x)
        {
            if (x.IsNaN) return x;
            x = X87Ext.Round64(x);
            if (x.Sign == 0) return x;
            var (m, e) = x.Scaled;
            int top = (int)BigInteger.Abs(m).GetBitLength() + e;   // 2^(top−1) <= |x| < 2^top
            if (top > 0 || (top == 0 && BigInteger.Abs(m) != BigInteger.One << -e - 1))
                throw new NotSupportedException("f2xm1: |x| > 1/2 is not modelled");
            return Ziv(prec =>
            {
                int g = prec + 16 - top;                        // |2^x − 1| >= 2^(top−2): prec + 14 bits relative
                Approx z = (new Approx(m, 0, e) * Ln2(g + 8)).At(-g);   // x·ln2, |z| <= 0.35
                BigInteger tk = z.V, sum = tk;
                int k = 1;
                while (!tk.IsZero) { k++; tk = (tk * z.V >> g) / k; sum += tk; }
                // per term: <= 2 new units (floor of the product, truncation of the quotient) plus the carried error
                // times |z|/k < 1/2, so <= 4; z's own error passes through |d(e^z − 1)/dz| = e^z < 1.5.
                return new Approx(sum, 2 * z.Err + 4 * k + 8, -g);
            });
        }

        // ln 2 at 2^−g = Σ_{k>=1} 1/(k·2^k): term floor(2^g/(k·2^k)) (error < 1), tail after the first zero term < 2.
        static Approx Ln2(int g)
        {
            BigInteger sum = 0, one = BigInteger.One << g;
            int k = 1;
            for (; ; k++) { BigInteger t = (one >> k) / k; if (t.IsZero) break; sum += t; }
            return new Approx(sum, k + 3, -g);
        }

        // appAtan 0x101260e0: fld qword x; fld1; fpatan -> atan2(x, 1).
        public static X87Ext AppAtan(X87Ext x) => Fpatan(x, X87Ext.From(1f));

        // Σ (−u)^n/(2n+1)! (sin r = r·S(r²)) or Σ (−u)^n/(2n)! (cos r) at 2^−g, 0 <= u < 1. Terms: t_n =
        // floor(floor(t_{n−1}·U / 2^g) / d_n) with d_n >= 2: the error of each term stays <= 3 units (e <= 2 + e/2),
        // the alternating tail after the first zero term is <= 3, and |dS/du|, |dC/du| <= 1/2 turn u's error into at
        // most u.Err units.
        static Approx Series(Approx u, int g, bool cos)
        {
            BigInteger U = u.V, t = BigInteger.One << g, sum = t;
            int n = 0;
            while (!t.IsZero)
            {
                n++;
                t = ((t * U) >> g) / (cos ? (2 * n - 1) * (2 * n) : (2 * n) * (2 * n + 1));
                sum += (n & 1) != 0 ? -t : t;
            }
            return new Approx(sum, 3 * n + 6 + u.Err, -g);
        }

        // atan(p/q), 0 < p <= q (or p = 0), at 2^−g, Euler's series: atan(z) = (pq/(p²+q²))·Σ c_k w^k,
        // w = p²/(p²+q²) <= 1/2, c_0 = 1, c_k = c_{k−1}·2k/(2k+1). t_0 = floor(2^g·pq/(p²+q²)) (error < 1),
        // W = floor(2^g·w) (error < 1 unit: |dΣ/dw| <= 4 over w <= 1/2, so <= 4·t_0/2^g + 1 units), t_k =
        // floor(floor(t_{k−1}·W/2^g)·2k/(2k+1)): per-term error <= 4 (e <= 2 + e/2); the positive tail after the first
        // zero term is <= 8.
        static Approx EulerAtan(BigInteger p, BigInteger q, int g)
        {
            if (p.IsZero) return new Approx(0, 0, -g);
            BigInteger p2 = p * p, d = p2 + q * q;
            BigInteger t = (p * q << g) / d, w = (p2 << g) / d, t0 = t, sum = t;
            int k = 0;
            while (!t.IsZero)
            {
                k++;
                t = ((t * w) >> g) * (2 * k) / (2 * k + 1);
                sum += t;
            }
            return new Approx(sum, 4 * k + 14 + (t0 >> (g - 2)), -g);
        }

        static readonly ConcurrentDictionary<int, Approx[]> SixteenthCache = new();
        static Approx AtanSixteenth(int i, int g) =>
            SixteenthCache.GetOrAdd(g, gg => { var a = new Approx[17]; for (int j = 1; j <= 16; j++) a[j] = EulerAtan(j, 16, gg); return a; })[i];

        // π at 2^−bits (error <= 2 units), Machin π = 16·atan(1/5) − 4·atan(1/239) with 32 guard bits, cached.
        static Approx _pi;
        static int _piBits;
        static readonly object PiLock = new();
        static Approx Pi(int bits)
        {
            lock (PiLock)
            {
                if (_piBits < bits)
                {
                    int nb = Math.Max(bits, 2 * _piBits), g = nb + 32;
                    Approx a5 = AtanInv(5, g), a239 = AtanInv(239, g);
                    _pi = new Approx(16 * a5.V - 4 * a239.V, 16 * a5.Err + 4 * a239.Err, -g);
                    _piBits = nb;
                }
                return _pi.At(-bits);
            }
        }

        // atan(1/n) at 2^−g: t_0 = floor(2^g/n), t_k = floor(t_{k−1}/n²) (error <= 2), term t_k/(2k+1) (<= 3).
        static Approx AtanInv(int n, int g)
        {
            BigInteger t = (BigInteger.One << g) / n, sum = 0, n2 = n * n;
            int k = 0;
            while (!t.IsZero)
            {
                BigInteger term = t / (2 * k + 1);
                sum += (k & 1) == 0 ? term : -term;
                t /= n2;
                k++;
            }
            return new Approx(sum, 3 * k + 3, -g);
        }
    }
}
