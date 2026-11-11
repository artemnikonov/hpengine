// EXACT MODEL OF AN x87 REGISTER WITH A 64-BIT MANTISSA FOR SHORT CHAINS.
//
// A value is a dyadic rational m*2^e; operations are exact, Round64 rounds to nearest even at 64 significant
// bits (shipped control word after appEnableFastMath(0): PC = 64, RC = nearest), ToFloat rounds once more to
// nearest even at 24 bits (fstp dword). The double rounding here is NOT an assumption: it is what the CPU does.
//
// The mantissa is a 128-bit integer (sign separate); BigInteger only when the exact value is wider than 128 bits
// (addition with a large exponent gap, Div64, TruncateToInteger); keeping everything in BigInteger cost ~2 us and
// a kilobyte of garbage per VSize. Both models agree bitwise (random corpus, SelfTest
// x87ext_matches_bigint_model).
//
// Special values (review round 7): NaN and ±inf load (fld), propagate through + − × ÷ √ as the FPU does with every
// exception masked (x/0 -> ±inf), and store (fstp).
// Signed zero (2026-10-30): a zero carries its sign bit as the x87 register does (IEEE 754, round to nearest — the only
// rounding the shipped chains use): fld of ±0 keeps it, fild 0 is +0; an exact zero sum of nonzero operands is +0, (−0) +
// (−0) is −0, (+0) + (−0) is +0, x + (±0) = x; fchs/subtraction flips it; a product or quotient of a zero has the xor sign,
// x/(±0) is ±inf by the xor sign, x/(±inf) is ±0 likewise; √(−0) = −0; fstp dword/qword keep it. A stored −0 is observable
// (sign bit tests, 1/x, fpatan of ±0); compares do not see it (Sign and Fcom: −0 == +0).
// CompareTo and Sign refuse NaN loudly: a compare that can see NaN uses Fcom and tests the status-word bits exactly as
// the disassembly does.
// NaN keeps its sign and 64-bit significand (2026-10-30), because a stored NaN's sign bit is observable (FastLineCheck
// side = sign bit of the stored float). Rules, Intel SDM vol. 1 §4.8.3.5/§8.5.1.2 (x87 rows of table 4-7), all
// exceptions masked: an invalid operation (inf − inf, 0·inf, 0/0, inf/inf, √negative) gives the real indefinite
// (sign 1, significand 0xC000000000000000; fstp dword 0xFFC00000); fld of a signalling NaN quiets it (significand bit
// 62 set) and keeps sign and payload; an operation with one NaN operand returns it; with two, the one with the larger
// significand, and of two with the same significand the positive one (the CPU model of unicorn, QEMU softfloat
// float_2nan_prop_x87; judge se_harness/bsp_diff/nan_slope_oracle.py kind x87, quiet NaNs). fsub/fsubr do not flip a
// NaN's sign; fchs does. fstp dword/qword keep the sign and the top 23/52 significand bits after the integer bit. Not judged: a
// signalling NaN (unicorn keeps it signalling after fld, unlike the SDM; no SNaN reaches the ported chains). ToFloat rounds once to the float grid, subnormals
// included, and overflows to ±inf like fstp dword. ToDouble keeps the normal-double range check.
using System;
using System.Numerics;

namespace HP1.Core
{
    public readonly struct X87Ext
    {
        // Small form: |m| = _hi*2^64 + _lo, sign _neg. Large form: _big.
        readonly ulong _hi, _lo;
        readonly bool _neg;        // sign bit: also of a zero (−0) and of ±inf / NaN
        readonly int _e;
        readonly object _bigBox;   // BigInteger (boxed): value wider than 128 bits
        readonly byte _sp;         // 0 finite, 1 ±inf (sign _neg), 2 NaN (sign _neg, significand _lo)

        X87Ext(ulong hi, ulong lo, bool neg, int e) { _hi = hi; _lo = lo; _neg = neg && (hi | lo) != 0; _e = e; _bigBox = null; _sp = 0; }
        X87Ext(byte sp, bool neg) { _hi = _lo = 0; _e = 0; _bigBox = null; _sp = sp; _neg = neg; }
        X87Ext(bool neg, ulong significand) { _hi = 0; _lo = significand | QuietBits; _e = 0; _bigBox = null; _sp = 2; _neg = neg; }
        const ulong QuietBits = 0xC000000000000000UL;   // integer bit 63 and quiet bit 62
        static X87Ext Inf(bool neg) => new X87Ext(1, neg);
        // ±0 with an explicit sign bit (the 4-argument constructor makes every zero +0, the sign of an exact cancellation).
        X87Ext(bool neg) { _hi = _lo = 0; _e = 0; _bigBox = null; _sp = 0; _neg = neg; }
        static X87Ext Zero(bool neg) => new X87Ext(neg);
        public static readonly X87Ext NegativeZero = new X87Ext(true);
        // The real indefinite: the result of every masked invalid operation.
        public static readonly X87Ext Indefinite = new X87Ext(true, QuietBits);
        // Two NaN operands: the larger significand, on a tie the positive one; one NaN operand: that one.
        internal static X87Ext PickNaN(X87Ext a, X87Ext b)
        {
            if (a._sp != 2) return b;
            if (b._sp != 2) return a;
            if (a._lo != b._lo) return a._lo > b._lo ? a : b;
            return a._neg ? b : a;
        }
        public bool IsNaN => _sp == 2;
        public bool IsInfinity => _sp == 1;
        public bool IsFinite => _sp == 0;
        X87Ext(BigInteger m, int e)
        {
            _e = e; _sp = 0;
            BigInteger mag = BigInteger.Abs(m);
            if (mag.IsZero) { _hi = _lo = 0; _neg = false; _bigBox = null; return; }
            if (BitLength(mag) <= 128)
            {
                _lo = (ulong)(mag & ulong.MaxValue);
                _hi = (ulong)(mag >> 64);
                _neg = m.Sign < 0;
                _bigBox = null;
            }
            else { _hi = _lo = 0; _neg = false; _bigBox = m; }
        }

        bool IsBig => _bigBox is not null;
        BigInteger BigBox => (BigInteger)_bigBox;
        bool IsZero => _sp == 0 && !IsBig && (_hi | _lo) == 0;
        BigInteger Big
        {
            get
            {
                if (_bigBox is not null) return (BigInteger)_bigBox;
                var mag = ((BigInteger)_hi << 64) | _lo;
                return _neg ? -mag : mag;
            }
        }

        public static X87Ext From(int v) => new X87Ext(0, (ulong)Math.Abs((long)v), v < 0, 0);

        // fild qword: a 64-bit integer loads exactly (x87 mantissa is 64 bits).
        public static X87Ext From(long v)
            => v == long.MinValue ? new X87Ext(0, 1UL << 63, true, 0) : new X87Ext(0, (ulong)Math.Abs(v), v < 0, 0);

        public static X87Ext From(float v)
        {
            int bits = BitConverter.SingleToInt32Bits(v);
            if (float.IsNaN(v)) return new X87Ext(bits < 0, (ulong)(bits & 0x7fffff) << 40);
            if (float.IsInfinity(v)) return Inf(v < 0);
            int exp = (bits >> 23) & 0xff;
            int man = bits & 0x7fffff;
            if (exp == 0) { if (man == 0) return Zero(bits < 0); exp = 1; }
            else man |= 0x800000;
            return new X87Ext(0, (ulong)man, bits < 0, exp - 150);
        }

        // fld qword: a double loads exactly.
        public static X87Ext From(double v)
        {
            long bits = BitConverter.DoubleToInt64Bits(v);
            if (double.IsNaN(v)) return new X87Ext(bits < 0, (ulong)(bits & 0xFFFFFFFFFFFFFL) << 11);
            if (double.IsInfinity(v)) return Inf(v < 0);
            int exp = (int)((bits >> 52) & 0x7ff);
            long man = bits & 0xFFFFFFFFFFFFFL;
            if (exp == 0) { if (man == 0) return Zero(bits < 0); exp = 1; }
            else man |= 1L << 52;
            return new X87Ext(0, (ulong)man, bits < 0, exp - 1075);
        }

        // Exact value m·2^e (UEX87Transcendental's arbitrary-precision evaluation). Finite values only.
        internal static X87Ext FromScaled(BigInteger m, int e) => new X87Ext(m, e);
        internal (BigInteger M, int E) Scaled => _sp != 0 ? throw new ArithmeticException("X87Ext.Scaled: non-finite") : (Big, _e);
        // The sign bit (fxam C1): set for −0 and −inf too. NaN has a sign bit as well but callers test IsNaN first.
        public bool IsNegative => _sp != 0 ? _neg : IsBig ? BigBox.Sign < 0 : _neg;
        internal static X87Ext FromUInt128(UInt128 m, bool neg, int e) => new X87Ext((ulong)(m >> 64), (ulong)m, neg, e);
        // Finite value whose mantissa fits 64 bits: |v| = m·2^e.
        internal bool TryMantissa64(out ulong m, out int e, out bool neg)
        {
            m = _lo; e = _e; neg = _neg;
            return _sp == 0 && _bigBox is null && _hi == 0;
        }

        public int Sign => _sp == 2 ? throw new ArithmeticException("X87Ext.Sign: NaN")
            : _sp == 1 ? (_neg ? -1 : 1) : IsBig ? BigBox.Sign : (_hi | _lo) == 0 ? 0 : _neg ? -1 : 1;

        // fcom: sign of the difference, exact. Ordered values only (NaN is a loud failure: use Fcom).
        public int CompareTo(X87Ext other)
        {
            if (_sp == 2 || other._sp == 2) throw new ArithmeticException("X87Ext.CompareTo: NaN (unordered), use Fcom");
            if (_sp == 1 && other._sp == 1) return _neg == other._neg ? 0 : _neg ? -1 : 1;
            if (_sp == 1) return _neg ? -1 : 1;
            if (other._sp == 1) return other._neg ? 1 : -1;
            return (this - other).Sign;
        }

        // C0 / C2 / C3 bits of AH after `fcom src; fnstsw ax` with st0 = a, src = b: a < b -> 0x01, a == b -> 0x40,
        // a > b -> 0, unordered (NaN) -> 0x45. Call sites test the mask the disassembly tests (`test ah, 0x41`, ...).
        public const int C0 = 0x01, C2 = 0x04, C3 = 0x40;
        public static int Fcom(X87Ext a, X87Ext b)
        {
            if (a._sp == 2 || b._sp == 2) return C0 | C2 | C3;
            int c = a.CompareTo(b);
            return c < 0 ? C0 : c == 0 ? C3 : 0;
        }

        // fsqrt in PC64: square root rounded to nearest even at 64 bits. The mantissa is shifted into
        // [2^126, 2^128) with an even exponent; the integer root s0 is exactly 64 bits, the remainder M - s0^2 comes
        // from the same digit-by-digit extraction. An integer root never has a midpoint
        // ((s0+1/2)^2 = s0^2+s0+1/4), so round up exactly when remainder > s0.
        public static X87Ext Sqrt64(X87Ext v)
        {
            if (v._sp == 2) return v;
            if (v.Sign < 0) return Indefinite;                // invalid operation, masked
            if (v.Sign == 0 || v._sp == 1) return v;
            if (v.IsBig) return SqrtBig(v.Big, v._e);
            ulong hi = v._hi, lo = v._lo;
            int e = v._e;
            int k = 128 - BitLen(hi, lo);
            if (((e - k) & 1) != 0) k--;            // even exponent: M' in [2^126, 2^128)
            if (k >= 0) Shl(ref hi, ref lo, k);
            else { if ((lo & 1) != 0) return SqrtBig(v.Big, v._e); Shr(ref hi, ref lo, -k); }
            e -= k;
            // Integer root s0 = floor(sqrt(M')) in [2^63, 2^64): double estimate
            // (error up to ~2^12) and exact correction via 128-bit squares.
            double est = Math.Sqrt(hi * 18446744073709551616.0 + lo);
            ulong s0 = est >= 18446744073709551615.0 ? ulong.MaxValue : (ulong)est;
            // r = M' - s0^2 (signed); s0 += r / (2*s0): one Newton correction in double.
            Mul64(s0, s0, out ulong qh, out ulong ql);
            bool below = qh < hi || (qh == hi && ql <= lo);       // s0² <= M'
            ulong dh = below ? hi - qh - (lo < ql ? 1UL : 0UL) : qh - hi - (ql < lo ? 1UL : 0UL);
            ulong dl = below ? lo - ql : ql - lo;
            double corr = (dh * 18446744073709551616.0 + dl) / (2.0 * s0);
            s0 = below ? s0 + (ulong)corr : s0 - (ulong)corr;
            // Exact refinement: s0^2 <= M' < (s0+1)^2.
            while (true)
            {
                Mul64(s0, s0, out qh, out ql);
                if (qh > hi || (qh == hi && ql > lo)) { s0--; continue; }
                if (s0 == ulong.MaxValue) break;
                ulong n1 = s0 + 1;
                Mul64(n1, n1, out ulong nh, out ulong nl);
                if (nh < hi || (nh == hi && nl <= lo)) { s0 = n1; continue; }
                break;
            }
            // Remainder M' - s0^2; round up exactly when remainder > s0.
            Mul64(s0, s0, out qh, out ql);
            ulong remLo = lo - ql, remHi = hi - qh - (lo < ql ? 1UL : 0UL);
            ulong resHi = 0, resLo = s0;
            if (remHi != 0 || remLo > s0)
            {
                resLo++;
                if (resLo == 0) resHi = 1;
            }
            return new X87Ext(resHi, resLo, false, e / 2);
        }

        static X87Ext SqrtBig(BigInteger m, int e)
        {
            int k = Math.Max(0, 140 - BitLength(m));
            if (((e - k) & 1) != 0) k++;
            m <<= k; e -= k;
            BigInteger s = ISqrt(m);
            bool exact = s * s == m;
            BigInteger r = exact ? s << 1 : (s << 1) | BigInteger.One;
            return RoundBits(new X87Ext(r, e / 2 - 1), 64);
        }

        // fdiv in PC64: quotient rounded to nearest even at 64 bits
        // (with a sticky bit).
        public static X87Ext Div64(X87Ext a, X87Ext b) => Div(a, b, 64);
        // fdiv in PC24: the exact quotient rounded ONCE to 24 bits (the sticky bit keeps it a single rounding).
        public static X87Ext Div24(X87Ext a, X87Ext b) => Div(a, b, 24);

        static X87Ext Div(X87Ext a, X87Ext b, int bits)
        {
            if (a._sp == 2 || b._sp == 2) return PickNaN(a, b);
            if (a._sp == 1) return b._sp == 1 ? Indefinite : Inf(a._neg != b.IsNegative);
            if (b._sp == 1) return Zero(a.IsNegative != b._neg);
            if (b.Sign == 0) return a.Sign == 0 ? Indefinite : Inf(a.IsNegative != b._neg);   // zero divide, masked
            if (a.Sign == 0) return Zero(a._neg != b.IsNegative);
            if (!a.IsBig && !b.IsBig)
            {
                // The same integer steps in UInt128 (no BigInteger garbage): q < 2^72, and q, rem and the sticky bit are
                // the same integers, so the rounded result is the same; past 127 bits by long division.
                int la = BitLen(a._hi, a._lo), lb = BitLen(b._hi, b._lo);
                int ks = Math.Max(0, 70 + lb - la);
                (UInt128 q128, UInt128 rem128) = la + ks <= 127
                    ? UInt128.DivRem(new UInt128(a._hi, a._lo) << ks, new UInt128(b._hi, b._lo))
                    : ShlDivRem(new UInt128(a._hi, a._lo), ks, new UInt128(b._hi, b._lo));
                UInt128 r128 = rem128 == UInt128.Zero ? q128 << 1 : (q128 << 1) | UInt128.One;
                return RoundBits(FromUInt128(r128, a._neg != b._neg, a._e - ks - b._e - 1), bits);
            }
            return DivBig(a, b, bits);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static X87Ext DivBig(X87Ext a, X87Ext b, int bits)
        {
            BigInteger am = BigInteger.Abs(a.Big), bm = BigInteger.Abs(b.Big);
            int k = Math.Max(0, 70 + BitLength(bm) - BitLength(am));
            BigInteger q = BigInteger.DivRem(am << k, bm, out BigInteger rem);
            BigInteger r = rem.IsZero ? q << 1 : (q << 1) | BigInteger.One;
            if (a.Sign * b.Sign < 0) r = -r;
            return RoundBits(new X87Ext(r, a._e - k - b._e - 1), bits);
        }

        // floor(a·2^k / b) and its remainder, b > 0, the quotient < 2^128: long division, the remainder shifted by as
        // many bits as keep it inside 128. A 128-bit b (b >= 2^127) leaves no free bit to shift into (step 0): the
        // same integers by BigInteger.
        internal static (UInt128 Q, UInt128 R) ShlDivRem(UInt128 a, int k, UInt128 b)
        {
            if (k > 0 && b >= (UInt128.One << 127)) return ShlDivRemBig(a, k, b);
            (UInt128 q, UInt128 r) = UInt128.DivRem(a, b);
            int step = 127 - (int)UInt128.Log2(b);              // r < b < 2^(log2 b + 1)
            while (k > 0)
            {
                int sh = Math.Min(k, step);
                (UInt128 d, r) = UInt128.DivRem(r << sh, b);
                q = (q << sh) + d;
                k -= sh;
            }
            return (q, r);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static (UInt128 Q, UInt128 R) ShlDivRemBig(UInt128 a, int k, UInt128 b)
        {
            BigInteger q = BigInteger.DivRem((BigInteger)a << k, (BigInteger)b, out BigInteger r);
            return ((UInt128)q, (UInt128)r);
        }

        static BigInteger ISqrt(BigInteger n)
        {
            BigInteger x = BigInteger.One << ((BitLength(n) + 1) / 2);
            while (true)
            {
                BigInteger y = (x + n / x) >> 1;
                if (y >= x) return x;
                x = y;
            }
        }

        // fstp qword: round to nearest even at 53 bits.
        public double ToDouble()
        {
            if (_sp == 2) return BitConverter.Int64BitsToDouble((_neg ? long.MinValue : 0L) | 0x7FF0000000000000L | (long)((_lo >> 11) & 0xFFFFFFFFFFFFFUL));
            if (_sp == 1) return _neg ? double.NegativeInfinity : double.PositiveInfinity;
            X87Ext r = RoundBits(this, 53);
            if (r.Sign == 0) return _neg ? -0.0 : 0.0;
            int len = r.IsBig ? BitLength(BigInteger.Abs(r.BigBox)) : BitLen(r._hi, r._lo);
            int top = r._e + len - 1;
            if (top < -1022 || top > 1023)
                throw new ArithmeticException($"X87Ext.ToDouble: exponent 2^{top} outside normal double range");
            double value = r.IsBig ? (double)BigInteger.Abs(r.BigBox) : (double)r._lo;   // <= 2^53: exact
            int e = r._e;
            while (e > 0) { int step = Math.Min(e, 1000); value *= BitConverter.Int64BitsToDouble((long)(step + 1023) << 52); e -= step; }
            while (e < 0) { int step = Math.Min(-e, 1000); value *= BitConverter.Int64BitsToDouble((long)(1023 - step) << 52); e += step; }
            return r.Sign < 0 ? -value : value;
        }

        public static X87Ext operator +(X87Ext a, X87Ext b)
        {
            if ((a._sp | b._sp) != 0)
            {
                if (a._sp == 2 || b._sp == 2) return PickNaN(a, b);
                if (a._sp == 1 && b._sp == 1) return a._neg == b._neg ? a : Indefinite;
                return a._sp == 1 ? a : b;
            }
            if (a.Sign == 0) return b.Sign == 0 ? Zero(a._neg && b._neg) : b;
            if (b.Sign == 0) return a;
            int e = Math.Min(a._e, b._e);
            if (!a.IsBig && !b.IsBig)
            {
                int sa = a._e - e, sb = b._e - e;
                if (BitLen(a._hi, a._lo) + sa <= 127 && BitLen(b._hi, b._lo) + sb <= 127)
                {
                    ulong ah = a._hi, al = a._lo, bh = b._hi, bl = b._lo;
                    Shl(ref ah, ref al, sa); Shl(ref bh, ref bl, sb);
                    if (a._neg == b._neg)
                    {
                        ulong l = al + bl;
                        return new X87Ext(ah + bh + (l < al ? 1UL : 0UL), l, a._neg, e);
                    }
                    bool aBigger = ah > bh || (ah == bh && al >= bl);
                    if (!aBigger) { (ah, bh) = (bh, ah); (al, bl) = (bl, al); }
                    ulong dl = al - bl;
                    ulong dh = ah - bh - (al < bl ? 1UL : 0UL);
                    return new X87Ext(dh, dl, aBigger ? a._neg : b._neg, e);
                }
            }
            return AddBig(a, b, e);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static X87Ext AddBig(X87Ext a, X87Ext b, int e) => new X87Ext((a.Big << (a._e - e)) + (b.Big << (b._e - e)), e);

        // fsub/fsubr: a NaN operand comes back with its own sign (the NaN rules see the operands as loaded).
        public static X87Ext operator -(X87Ext a, X87Ext b) => a._sp == 2 || b._sp == 2 ? PickNaN(a, b) : a + b.Negate();

        X87Ext Negate() => _sp == 2 ? new X87Ext(!_neg, _lo) : _sp == 1 ? Inf(!_neg) : IsBig ? new X87Ext(-BigBox, _e)
            : IsZero ? Zero(!_neg) : new X87Ext(_hi, _lo, !_neg, _e);
        // fchs: flips the sign bit (of a zero and of a NaN too; judge nan_slope_oracle x87 chs).
        public static X87Ext operator -(X87Ext a) => a.Negate();

        public static X87Ext operator *(X87Ext a, X87Ext b)
        {
            if ((a._sp | b._sp) != 0)
                return a._sp == 2 || b._sp == 2 ? PickNaN(a, b)
                    : a.Sign == 0 || b.Sign == 0 ? Indefinite : Inf((a.Sign < 0) != (b.Sign < 0));
            if (a.Sign == 0 || b.Sign == 0) return Zero(a.IsNegative != b.IsNegative);
            if (!a.IsBig && !b.IsBig && BitLen(a._hi, a._lo) + BitLen(b._hi, b._lo) <= 128)
            {
                // The product fits in 128 bits, so the high words are not both nonzero.
                Mul64(a._lo, b._lo, out ulong ph, out ulong pl);
                ph += a._hi * b._lo + a._lo * b._hi;
                return new X87Ext(ph, pl, a._neg != b._neg, a._e + b._e);
            }
            return MulBig(a, b);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static X87Ext MulBig(X87Ext a, X87Ext b) => new X87Ext(a.Big * b.Big, a._e + b._e);

        public static X87Ext Round64(X87Ext v) => RoundBits(v, 64);
        // PC24 (render window): the register rounds the mantissa to 24 bits, the exponent stays 15 bits wide.
        public static X87Ext Round24(X87Ext v) => RoundBits(v, 24);

        // One PC24 register operation on a double that holds its exact result (a product of two floats, or a sum /
        // difference / quotient / root of 24-bit operands, which double rounds innocuously: 53 >= 2·24 + 2): round to
        // nearest even at 24 mantissa bits, keep the double's exponent (the register's is wider, and every intermediate
        // of float chains lies inside the normal double range, |v| in [2^-330, 2^260]). ±0, ±inf and NaN pass unchanged.
        public static double Round24(double d)
        {
            long b = BitConverter.DoubleToInt64Bits(d);
            if ((b & 0x7FF0000000000000L) == 0x7FF0000000000000L) return d;     // ±inf, NaN
            const long Drop = (1L << 29) - 1, Half = 1L << 28;
            long low = b & Drop;
            b &= ~Drop;
            if (low > Half || (low == Half && (b & (1L << 29)) != 0)) b += 1L << 29;   // carry may bump the exponent
            return BitConverter.Int64BitsToDouble(b);
        }

        // fistp with RC = truncate (msvcrt _ftol): integer part toward zero, exact.
        public BigInteger TruncateToInteger() =>
            _sp != 0 ? throw new ArithmeticException("X87Ext.TruncateToInteger: non-finite")
            : _e >= 0 ? Big << _e : BigInteger.Divide(Big, BigInteger.One << -_e);

        static X87Ext RoundBits(X87Ext v, int bits)
        {
            if (v._sp != 0 || v.Sign == 0) return v;
            if (v.IsBig) return RoundBig(v, bits);
            if (v._hi == 0)
            {
                // The same integers in one word (the common case: a product or sum of 64-bit and 24-bit mantissas).
                ulong m = v._lo;
                int l = BitLen64(m);
                if (l <= bits) return v;
                int sh = l - bits;
                ulong q = m >> sh, rem = m & ((1UL << sh) - 1), half = 1UL << (sh - 1);
                if (rem > half || (rem == half && (q & 1) != 0)) q++;
                return new X87Ext(0, q, v._neg, v._e + sh);
            }
            int len = BitLen(v._hi, v._lo);
            if (len <= bits) return v;
            int shift = len - bits;
            ulong qh = v._hi, ql = v._lo;
            Shr(ref qh, ref ql, shift);
            // Remainder is the low shift bits; the half is bit shift-1.
            ulong rh = v._hi, rl = v._lo;
            Mask(ref rh, ref rl, shift);
            ulong hh = 0, hl = 1;
            Shl(ref hh, ref hl, shift - 1);
            bool up = rh > hh || (rh == hh && rl > hl) || (rh == hh && rl == hl && (ql & 1) != 0);
            if (up) { ql++; if (ql == 0) qh++; }
            return new X87Ext(qh, ql, v._neg, v._e + shift);
        }

        // RoundBits of a value wider than 128 bits (kept out of the small path: BigInteger locals cost every call).
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static X87Ext RoundBig(X87Ext v, int bits)
        {
            BigInteger mag = BigInteger.Abs(v.BigBox);
            int blen = BitLength(mag);
            if (blen <= bits) return v;
            int bshift = blen - bits;
            BigInteger bq = mag >> bshift;
            BigInteger brem = mag - (bq << bshift);
            BigInteger bhalf = BigInteger.One << (bshift - 1);
            if (brem > bhalf || (brem == bhalf && !bq.IsEven)) bq += 1;
            return new X87Ext(v.BigBox.Sign < 0 ? -bq : bq, v._e + bshift);
        }

        static int BitLength(BigInteger mag)
        {
            int n;
            byte[] b = mag.ToByteArray();
            int top = b.Length - 1;
            while (top > 0 && b[top] == 0) top--;
            n = top * 8;
            for (int x = b[top]; x != 0; x >>= 1) n++;
            return n;
        }

        static int BitLen64(ulong x) => 64 - System.Numerics.BitOperations.LeadingZeroCount(x);
        static int BitLen(ulong hi, ulong lo) => hi != 0 ? 64 + BitLen64(hi) : BitLen64(lo);

        static void Shl(ref ulong hi, ref ulong lo, int k)
        {
            if (k <= 0) return;
            if (k >= 128) { hi = lo = 0; return; }
            if (k >= 64) { hi = lo << (k - 64); lo = 0; return; }
            hi = (hi << k) | (lo >> (64 - k)); lo <<= k;
        }
        static void Shr(ref ulong hi, ref ulong lo, int k)
        {
            if (k <= 0) return;
            if (k >= 128) { hi = lo = 0; return; }
            if (k >= 64) { lo = hi >> (k - 64); hi = 0; return; }
            lo = (lo >> k) | (hi << (64 - k)); hi >>= k;
        }
        // Keep the low k bits.
        static void Mask(ref ulong hi, ref ulong lo, int k)
        {
            if (k >= 128) return;
            if (k >= 64) { hi &= k == 64 ? 0UL : (1UL << (k - 64)) - 1; return; }
            hi = 0; lo &= (1UL << k) - 1;
        }
        static void Mul64(ulong a, ulong b, out ulong hi, out ulong lo)
        {
            ulong a0 = (uint)a, a1 = a >> 32, b0 = (uint)b, b1 = b >> 32;
            ulong p00 = a0 * b0, p01 = a0 * b1, p10 = a1 * b0, p11 = a1 * b1;
            ulong mid = (p00 >> 32) + (uint)p01 + (uint)p10;
            lo = (mid << 32) | (uint)p00;
            hi = p11 + (p01 >> 32) + (p10 >> 32) + (mid >> 32);
        }

        // fstp dword: ONE rounding (nearest even) of the exact value to the float grid — 24 bits for a normal result,
        // the fixed 2^-149 grid below 2^-126 (a subnormal or a signed zero) — and ±inf past the largest float.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        bool IsPowerOfTwoMagnitude() => BigInteger.Abs(Big).IsPowerOfTwo;

        public float ToFloat()
        {
            if (_sp == 2) return BitConverter.Int32BitsToSingle((_neg ? int.MinValue : 0) | 0x7F800000 | (int)((_lo >> 40) & 0x7FFFFF));
            if (_sp == 1) return _neg ? float.NegativeInfinity : float.PositiveInfinity;
            if (Sign == 0) return _neg ? -0f : 0f;
            bool neg = Sign < 0;
            int top0 = _e + (IsBig ? BitLength(BigInteger.Abs(BigBox)) : BitLen(_hi, _lo)) - 1;   // top bit, exact value
            X87Ext r;
            if (top0 >= -126) r = RoundBits(this, 24);
            else if (top0 >= -149) r = RoundBits(this, top0 + 150);     // bits down to 2^-149
            else
            {
                // Under half the smallest subnormal rounds to zero; (2^-150, 2^-149) rounds up; exactly 2^-150 is a tie
                // that goes to the even zero.
                bool up = top0 == -150 && !IsPowerOfTwoMagnitude();
                return up ? (neg ? -float.Epsilon : float.Epsilon) : (neg ? -0f : 0f);
            }
            int len = r.IsBig ? BitLength(BigInteger.Abs(r.BigBox)) : BitLen(r._hi, r._lo);
            int top = r._e + len - 1;           // exponent of the top bit
            if (top > 127) return neg ? float.NegativeInfinity : float.PositiveInfinity;
            // mag <= 2^24, power of two assembled bitwise: the product is exact (a subnormal too: e >= -149).
            double mag = r.IsBig ? (double)BigInteger.Abs(r.BigBox) : (double)r._lo;
            double value = mag * BitConverter.Int64BitsToDouble((long)(r._e + 1023) << 52);
            return (float)(r.Sign < 0 ? -value : value);
        }
    }
}
