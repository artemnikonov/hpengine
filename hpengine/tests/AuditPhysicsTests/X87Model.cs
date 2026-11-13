// INDEPENDENT EXACT x87 MODEL FOR TEST EXPECTATIONS (does not use port code).
//
// A value is an exact rational (BigInteger/BigInteger). R64 — round to
// nearest even at 64 significant bits (shipped FPCW after appEnableFastMath(0):
// PC = 64, RC = nearest); D53 — a `fstp qword` store; F32 — a `fstp dword` store.
// Square root — exact rounding to 64 bits from an integer root with an exactness flag.
using System.Numerics;

readonly struct Q
{
    public readonly BigInteger N, D;   // D > 0
    Q(BigInteger n, BigInteger d)
    {
        if (d.Sign < 0) { n = -n; d = -d; }
        BigInteger g = BigInteger.GreatestCommonDivisor(n, d);
        if (!g.IsZero && !g.IsOne) { n /= g; d /= g; }
        N = n; D = n.IsZero ? BigInteger.One : d;
    }
    public static Q Of(BigInteger n, BigInteger d) => new Q(n, d);
    public static Q F(float f)
    {
        if (!float.IsFinite(f)) throw new System.ArgumentException("not finite");
        int bits = System.BitConverter.SingleToInt32Bits(f);
        int exp = (bits >> 23) & 0xff, man = bits & 0x7fffff;
        if (exp == 0) { if (man == 0) return new Q(0, 1); exp = 1; } else man |= 0x800000;
        BigInteger m = bits < 0 ? -(BigInteger)man : man;
        int e = exp - 150;
        return e >= 0 ? new Q(m << e, 1) : new Q(m, BigInteger.One << -e);
    }
    public static Q I(long v) => new Q(v, 1);
    public int Sign => N.Sign;
    public static Q operator +(Q a, Q b) => new Q(a.N * b.D + b.N * a.D, a.D * b.D);
    public static Q operator -(Q a, Q b) => new Q(a.N * b.D - b.N * a.D, a.D * b.D);
    public static Q operator *(Q a, Q b) => new Q(a.N * b.N, a.D * b.D);
    public static Q operator /(Q a, Q b) => new Q(a.N * b.D, a.D * b.N);
    public static bool operator <(Q a, Q b) => (a - b).Sign < 0;
    public static bool operator >(Q a, Q b) => (a - b).Sign > 0;
    public static bool operator <=(Q a, Q b) => (a - b).Sign <= 0;
    public static bool operator >=(Q a, Q b) => (a - b).Sign >= 0;

    static int Log2Floor(BigInteger n, BigInteger d)   // floor(log2(n/d)), n,d > 0
    {
        int e = (int)(n.GetBitLength() - d.GetBitLength());
        // 2^e <= n/d < 2^(e+1)?
        if (e >= 0 ? n < (d << e) : (n << -e) < d) e--;
        return e;
    }

    // Round to nearest even at `bits` significant bits.
    public static Q Round(Q x, int bits)
    {
        if (x.Sign == 0) return x;
        BigInteger n = BigInteger.Abs(x.N), d = x.D;
        int e = Log2Floor(n, d) - (bits - 1);      // unit in the last place = 2^e
        BigInteger sn = e >= 0 ? n : n << -e, sd = e >= 0 ? d << e : d;
        BigInteger q = BigInteger.DivRem(sn, sd, out BigInteger r);
        BigInteger twice = r * 2;
        if (twice > sd || (twice == sd && !q.IsEven)) q += 1;
        if (x.Sign < 0) q = -q;
        return e >= 0 ? new Q(q << e, 1) : new Q(q, BigInteger.One << -e);
    }
    public static Q R64(Q x) => Round(x, 64);
    public static Q D53(Q x) => Round(x, 53);

    // fsqrt PC64: root rounded to nearest even at 64 bits.
    public static Q Sqrt64(Q x)
    {
        if (x.Sign < 0) throw new System.ArgumentException("square root of a negative");
        if (x.Sign == 0) return x;
        // x = n/d; sqrt = sqrt(n·d)/d. Scaling by 2^(2k) gives an integer root wider than 70 bits.
        BigInteger nd = x.N * x.D;
        int k = System.Math.Max(0, (140 - (int)nd.GetBitLength()) / 2 + 1);
        BigInteger m = nd << (2 * k);
        BigInteger s = ISqrt(m);
        bool exact = s * s == m;
        // The exact root lies in [s, s+1); for rounding "s + ½·(inexact)" is enough.
        Q lo = new Q(exact ? s : s * 2 + 1, (exact ? BigInteger.One : 2) * (BigInteger.One << k) * x.D);
        return R64(lo);   // s ≥ 2^70: at least 6 bits are dropped, the ½ shift gives the correct sticky bit
    }

    static BigInteger ISqrt(BigInteger n)
    {
        if (n.IsZero) return 0;
        BigInteger x = BigInteger.One << (int)((n.GetBitLength() + 1) / 2);
        while (true)
        {
            BigInteger y = (x + n / x) >> 1;
            if (y >= x) return x;
            x = y;
        }
    }

    public float F32()
    {
        Q r = Round(this, 24);
        if (r.Sign == 0) return 0f;
        double v = (double)r.N / (double)r.D;   // ≤ 24 significant bits, exact division by a power of two
        float f = (float)v;
        if (!float.IsFinite(f) || (f != 0f && System.Math.Abs(f) < 1.17549435e-38f))
            throw new System.ArithmeticException("model: result outside normal floats");
        return f;
    }
}
