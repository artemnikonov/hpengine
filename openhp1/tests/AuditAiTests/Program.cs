// Comparison tests of Core AI/hearing against shipped HP1 1.53, no game files needed.
// Input is the same Core functions the VM calls:
// native 512 MakeNoise -> UELevelRuntime.MakeNoiseNative -> AActor::CheckNoiseHearing,
// and APawn::CanHear (UELevelRuntime.PawnCanHear — called by the listener walk).
//
// Expectations are computed HERE, independently of HP1.Core.X87Ext: exact rational
// arithmetic (BigInteger) and round-to-nearest-even at 64 bits (x87 register
// under FPCW PC64) and at 24 bits (`fstp dword`). Operation order follows the disasm of
// Engine.dll 1.53:
//   CanHear          0x103dacc9..0x103dae7d
//   CheckNoiseHearing 0x103db01b..0x103db29a
// Each check additionally requires that a naive float step-by-step formula
// gives a DIFFERENT answer — otherwise the case does not distinguish the fix.
// Red mutations — redtest.sh.
using System.Numerics;
using HP1.UEScript;

// Exact rational number and x87 rounding.
readonly struct Q
{
    public readonly BigInteger N, D;   // D > 0
    public Q(BigInteger n, BigInteger d)
    {
        if (d.Sign < 0) { n = -n; d = -d; }
        BigInteger g = BigInteger.GreatestCommonDivisor(BigInteger.Abs(n), d);
        if (g.IsZero) g = BigInteger.One;
        N = n / g; D = d / g;
    }
    public static Q Of(float f) => Of((double)f);
    public static Q Of(double v)
    {
        long bits = BitConverter.DoubleToInt64Bits(v);
        int exp = (int)((bits >> 52) & 0x7ff);
        long man = bits & 0xFFFFFFFFFFFFFL;
        if (exp == 0) { if (man == 0) return new Q(0, 1); exp = 1; } else man |= 1L << 52;
        BigInteger m = bits < 0 ? -man : man;
        int e = exp - 1075;
        return e >= 0 ? new Q(m << e, 1) : new Q(m, BigInteger.One << -e);
    }
    public static Q operator +(Q a, Q b) => new(a.N * b.D + b.N * a.D, a.D * b.D);
    public static Q operator -(Q a, Q b) => new(a.N * b.D - b.N * a.D, a.D * b.D);
    public static Q operator *(Q a, Q b) => new(a.N * b.N, a.D * b.D);
    public static Q operator /(Q a, Q b) => new(a.N * b.D, a.D * b.N);
    public int Sign => N.Sign;
    public int Cmp(Q o) => (this - o).Sign;

    // Round to nearest even at `bits` significant bits.
    public Q Round(int bits)
    {
        if (N.IsZero) return this;
        BigInteger an = BigInteger.Abs(N);
        // magnitude: 2^e <= |x| < 2^(e+1)
        int e = (int)(an.GetBitLength() - D.GetBitLength());
        if (Cmp2(an, D, e) < 0) e--;
        int shift = bits - 1 - e;                    // |x|·2^shift in [2^(bits-1), 2^bits)
        BigInteger num = shift >= 0 ? an << shift : an;
        BigInteger den = shift >= 0 ? D : D << -shift;
        BigInteger q = BigInteger.DivRem(num, den, out BigInteger r);
        int c = (r * 2).CompareTo(den);
        if (c > 0 || (c == 0 && !q.IsEven)) q += 1;
        if (N.Sign < 0) q = -q;
        return shift >= 0 ? new Q(q, BigInteger.One << shift) : new Q(q << -shift, 1);
    }
    static int Cmp2(BigInteger an, BigInteger d, int e)
        => e >= 0 ? an.CompareTo(d << e) : (an << -e).CompareTo(d);
    public Q R64 => Round(64);

    // fsqrt under PC64: exact root rounded to 64 bits (sticky bit from the remainder).
    public Q Sqrt64()
    {
        // x = N/D; sqrt(x) = sqrt(N·D·4^k)/(D·2^k)
        const int k = 140;
        BigInteger m = (N * D) << (2 * k);
        BigInteger s = ISqrt(m);
        bool exact = s * s == m;
        // s is the lower bound; add "half an ulp" as stickiness
        Q lo = new Q(exact ? s * 2 : s * 2 + 1, (D << k) * 2);
        return lo.Round(64);
    }
    static BigInteger ISqrt(BigInteger n)
    {
        if (n.IsZero) return n;
        BigInteger x = BigInteger.One << (int)((n.GetBitLength() + 1) / 2);
        while (true) { BigInteger y = (x + n / x) >> 1; if (y >= x) return x; x = y; }
    }
    public double ToDouble() { Q r = Round(53); return (double)r.N / (double)r.D; }
    public float ToFloat()
    {
        Q r = Round(24);
        return (float)((double)r.N / (double)r.D);   // r is a float, both conversions are exact
    }
}

static class Program
{
    static int _fail, _pass;
    static void Check(string name, bool ok, string detail)
    {
        if (ok) _pass++; else { _fail++; Console.WriteLine($"FAIL {name}: {detail}"); }
    }
    static uint Bits(float f) => (uint)BitConverter.SingleToInt32Bits(f);

    static Q DistSqShipped(UEVector a, UEVector b)
    {
        Q dx = (Q.Of(a.x) - Q.Of(b.x)).R64, dy = (Q.Of(a.y) - Q.Of(b.y)).R64,
          dz = (Q.Of(a.z) - Q.Of(b.z)).R64;
        return (((dx * dx).R64 + (dy * dy).R64).R64 + (dz * dz).R64).R64;
    }
    static float DistSqFloatSteps(UEVector a, UEVector b)
    {
        float dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
        float xx = dx * dx, yy = dy * dy, zz = dz * dz;
        float s = xx + yy; return s + zz;
    }

    // ULevel clock = clockFixed (FTime 32.32); TimeSeconds is its float, as published by ULevel::Tick.
    static (UEInterpreter, UELevelRuntime, UEObject level) WorldAt(long clockFixed)
    {
        var (interp, rt, level) = World((float)(clockFixed / 4294967296.0));
        rt.RestoreLevelClock(level, clockFixed);
        return (interp, rt, level);
    }

    static (UEInterpreter, UELevelRuntime, UEObject level) World(float now)
    {
        var interp = new UEInterpreter();
        var level = new UEObject();
        level.Props["TimeSeconds"] = now;
        level.Props["NetMode"] = (byte)0;
        interp.RestoreWorldLevelInfo(level);
        var rt = new UELevelRuntime();
        // CheckNoiseHearing reads the ULevel clock (FTime 32.32), not TimeSeconds (D-11); float ×2^32 is an integer.
        rt.RestoreLevelClock(level, (long)((double)now * 4294967296.0));
        return (interp, rt, level);
    }

    static UEObject NoisePawn(UEVector s1, float t1, float l1, UEVector s2, float t2, float l2)
    {
        var p = new UEObject();
        p.Props["noise1spot"] = s1; p.Props["noise1time"] = t1; p.Props["noise1loudness"] = l1;
        p.Props["noise2spot"] = s2; p.Props["noise2time"] = t2; p.Props["noise2loudness"] = l2;
        p.Props["bIsPlayer"] = false;
        return p;
    }

    static void MakeNoise(UEInterpreter interp, UELevelRuntime rt, UEObject source, float loudness)
        => rt.MakeNoiseNative(interp, new UEFrame { Object = source, Context = source },
            new object[] { loudness });

    static UEObject Source(UEVector at, UEObject instigator)
    {
        var s = new UEObject();
        s.Props["Location"] = at;
        s.Props["Instigator"] = instigator;
        return s;
    }

    static float F(UEObject o, string p) => UEInterpreter.AsFloat(o.GetProp(p));

    // ── CheckNoiseHearing ───────────────────────────────────────────────────
    static void NoiseCases()
    {
        var here = new UEVector(100f, 200f, 300f);
        var far = new UEVector(5000f, 5000f, 5000f);

        // 1. The constant 0.9 is a double (`fmul qword [0x10478e78]`, 0x103db08c):
        //    previous 0.9f vs new L = 1: 0.9f < 0.9, so NOT suppressed.
        {
            float now = 100f, L = 1f, loud = 0.9f;
            Check("noise09-premise", (Q.Of(L) * Q.Of(0.9)).R64.Cmp(Q.Of(loud)) > 0
                                     && loud >= L * 0.9f,
                "case does not distinguish a double from a float constant");
            var (interp, rt, _) = World(now);
            var pawn = NoisePawn(here, now, loud, here, now, loud);
            MakeNoise(interp, rt, Source(here, pawn), L);
            // Both slots fresh (0.18 each), near, loud1 = 0.9f ≤ L -> written to slot 1.
            Check("noise09-not-suppressed", F(pawn, "noise1loudness") == L && F(pawn, "noise1time") == now,
                $"shipped writes slot 1 (L·0.9 > 0.9f), port: noise1loudness={F(pawn, "noise1loudness"):R}");
        }

        // 2. Freshness by 0.2: (now − 0.2f) on the stack without rounding.
        {
            float now = 100f;
            Q exact = Q.Of(now) - Q.Of(0.2f);
            float t1 = exact.ToFloat();                  // nearest float to now − 0.2f
            Check("fresh02-premise", exact.Cmp(Q.Of(t1)) < 0 && !(t1 > now - 0.2f),
                "case does not distinguish an exact from a float difference");
            var (interp, rt, _) = World(now);
            // slot 1 fresh per shipped, near and not quieter than 0.9·L -> suppressed; slot 2 far
            var pawn = NoisePawn(here, t1, 1f, far, 0f, 0f);
            MakeNoise(interp, rt, Source(here, pawn), 1f);
            Check("fresh02-suppressed", F(pawn, "noise1time") == t1 && F(pawn, "noise2time") == 0f,
                $"shipped suppresses the noise (now−0.2f < t1), port: noise1time={F(pawn, "noise1time"):R} noise2time={F(pawn, "noise2time"):R}");
        }

        // 3. Expiry by 0.18: slot 1 expired by the exact difference although the float difference equals t1.
        {
            float now = 100f;
            Q exact = Q.Of(now) - Q.Of(0.18f);
            float t1 = exact.ToFloat();
            Check("stale018-premise", exact.Cmp(Q.Of(t1)) > 0 && !(t1 < now - 0.18f),
                "case does not distinguish an exact from a float difference");
            var (interp, rt, _) = World(now);
            // slot 1 fresh by 0.2 but far (does not suppress); slot 2 expired
            var pawn = NoisePawn(far, t1, 0f, far, 0f, 0f);
            MakeNoise(interp, rt, Source(here, pawn), 1f);
            Check("stale018-slot1", F(pawn, "noise1time") == now && F(pawn, "noise2time") == 0f,
                $"shipped writes slot 1 (stale by the exact difference), port: noise1time={F(pawn, "noise1time"):R} noise2time={F(pawn, "noise2time"):R}");
        }

        // 4. dist² < 2500 as one chain: exact sum 2499.99978…, float step-by-step gives 2500.
        {
            var spot = new UEVector(482.0180358886719f, -98.61296844482422f, 737.8206176757812f);
            var at = new UEVector(509.36444091796875f, -134.99827575683594f, 717.1256103515625f);
            Check("dist2500-premise", DistSqShipped(spot, at).Cmp(Q.Of(2500f)) < 0
                                      && !(DistSqFloatSteps(spot, at) < 2500f),
                "case does not distinguish a single chain from step-by-step float");
            float now = 100f;
            var (interp, rt, _) = World(now);
            var pawn = NoisePawn(spot, now, 1f, far, 0f, 0f);
            MakeNoise(interp, rt, Source(at, pawn), 1f);
            Check("dist2500-suppressed", F(pawn, "noise1time") == now && F(pawn, "noise1loudness") == 1f
                                         && UEInterpreter.AsVector3(pawn.GetProp("noise1spot")) == spot
                                         && F(pawn, "noise2time") == 0f,
                "shipped suppresses (dist² < 2500), the port wrote the slot");
        }
        // 6. Clock is ULevel+0xdc EXACTLY (`fild qword; fmul 2^-32`, 0x103db01b), not float TimeSeconds (D-11).
        //    Construction: S = T + 0.2f exactly; C = S·2^32 + 1 — by the exact clock slot 1 is no longer fresh
        //    (C·2^-32 − 0.2f > T) and the noise is written, while float(C·2^-32) is below S — "fresh", the noise would be suppressed.
        {
            float T = 99.8f; long C = 0; float f = 0f;
            for (int k = 0; k < 4096; k++, T = MathF.BitIncrement(T))
            {
                double S = (double)T + (double)0.2f;           // exact: 33 significant bits
                C = (long)(S * 4294967296.0) + 1;
                f = (float)(C / 4294967296.0);
                if ((double)f < S) break;
            }
            Check("clock-premise", (double)C / 4294967296.0 - (double)0.2f > T && (double)f - (double)0.2f < T,
                "case does not distinguish exact from rounded clock");
            var (interp, rt, _) = WorldAt(C);
            var pawn = NoisePawn(here, T, 1f, far, 0f, 0f);
            MakeNoise(interp, rt, Source(here, pawn), 1f);
            Check("clock-exact-records", F(pawn, "noise1time") == f && F(pawn, "noise1loudness") == 1f,
                $"shipped writes slot 1 (not fresh by the exact clock), port: noise1time={F(pawn, "noise1time"):R}, expected {f:R}");
        }
        // 5. Unordered loudness comparison: `fld slot; fcomp L; test ah,0x41` —
        //    C0|C3 are set on NaN too, so with L = NaN the slot is "not louder" and the write happens.
        //    Both slots fresh and far (no suppression) -> fourth branch -> slot 1.
        {
            float now = 100f;
            var (interp, rt, _) = World(now);
            var pawn = NoisePawn(far, now, 0.5f, far, now, 0.5f);
            MakeNoise(interp, rt, Source(here, pawn), float.NaN);
            Check("nan-loudness-records", float.IsNaN(F(pawn, "noise1loudness")) && F(pawn, "noise1time") == now,
                $"shipped writes slot 1 (NaN: \"not louder\"), port: noise1loudness={F(pawn, "noise1loudness"):R}");
        }
    }

    // ── CanHear: Stimulus ───────────────────────────────────────────────────
    static float StimulusShipped(UEVector listener, UEVector noise, float L, float alertness)
    {
        Q dist = Q.Of(DistSqShipped(listener, noise).ToFloat());
        Q q = (Q.Of(1200000f) / dist).R64;
        Q scale, alert;
        if (q.Cmp(Q.Of(2f)) > 0) { scale = Q.Of(2f); alert = Q.Of(0.5f); }
        else
        {
            float sf = q.ToFloat();
            scale = Q.Of(sf);
            alert = 0.5f <= sf ? Q.Of(0.5f) : Q.Of(sf);
        }
        return ((alert * Q.Of(alertness)).R64 + (scale * Q.Of(L)).R64).R64.ToFloat();
    }
    static float StimulusFloatSteps(UEVector listener, UEVector noise, float L, float alertness)
    {
        float d = DistSqFloatSteps(listener, noise);
        float scale = 1200000f / d, alert;
        if (scale > 2f) { scale = 2f; alert = 0.5f; } else alert = scale >= 0.5f ? 0.5f : scale;
        float a = scale * L, b = alert * alertness;
        return a + b;
    }

    static void HearCases()
    {
        // All listeners are within 2000·L (otherwise refused before Stimulus).
        var cases = new (UEVector l, UEVector n, float L, float A)[]
        {
            (new(1762.5899658203125f, -578.1400146484375f, 443.67999267578125f),
             new(1577.4300537109375f, 197.69000244140625f, 876.25f), 2f, 0f),
            (new(-277.9100036621094f, 200.8800048828125f, 825.5900268554688f),
             new(290.32000732421875f, 991.77001953125f, -2f), 0.7f, 1f),
            (new(1898.06005859375f, -1677.8499755859375f, -1591.3699951171875f),
             new(1574.6400146484375f, -1641.9599609375f, -281.55999755859375f), 2f, 0.5f),
            (new(1197.4300537109375f, 905.47998046875f, -1588.9100341796875f),
             new(1621.43994140625f, 2076.429931640625f, -186.1699981689453f), 1f, 1f),
            (new(-55.439998626708984f, -1900.6600341796875f, -1985.6400146484375f),
             new(-284.42999267578125f, -3461.360107421875f, -2253.8701171875f), 2f, 1f),
        };
        int i = 0;
        foreach (var (l, n, L, A) in cases)
        {
            i++;
            float want = StimulusShipped(l, n, L, A);
            Check($"hear{i}-premise", Bits(want) != Bits(StimulusFloatSteps(l, n, L, A)),
                "case does not distinguish a single chain from step-by-step float");
            var (interp, rt, _) = World(0f);
            var listener = new UEObject();
            listener.Props["Location"] = l;
            listener.Props["bIsPlayer"] = false;
            listener.Props["Alertness"] = A;
            listener.Props["HearingThreshold"] = 1e30f;   // refused BEFORE the trace: no BSP needed
            listener.Props["Stimulus"] = -1f;
            bool heard;
            try { heard = rt.PawnCanHear(interp, listener, n, L, null); }
            catch (InvalidOperationException e) { heard = true; Console.WriteLine("  (went to the trace: " + e.Message + ")"); }
            float got = F(listener, "Stimulus");
            Check($"hear{i}-stimulus", !heard && Bits(got) == Bits(want),
                $"expected Stimulus 0x{Bits(want):x8} ({want:R}), got 0x{Bits(got):x8} ({got:R}), heard={heard}");
        }

        // The threshold is compared with the UNrounded sum (fst Stimulus; fcomp threshold).
        // Threshold = float copy of Stimulus: shipped rejects only if the sum is LESS than the copy.
        {
            var (l, n, L, A) = cases[4];   // the sum on the stack is less than its float copy
            float stored = StimulusShipped(l, n, L, A);
            Q dist = Q.Of(DistSqShipped(l, n).ToFloat());
            Q q = (Q.Of(1200000f) / dist).R64;
            float sf = q.ToFloat();
            Q sc = q.Cmp(Q.Of(2f)) > 0 ? Q.Of(2f) : Q.Of(sf);
            Q al = q.Cmp(Q.Of(2f)) > 0 ? Q.Of(0.5f) : (0.5f <= sf ? Q.Of(0.5f) : Q.Of(sf));
            Q wide = ((al * Q.Of(A)).R64 + (sc * Q.Of(L)).R64).R64;
            bool wantReject = wide.Cmp(Q.Of(stored)) < 0;
            var (interp, rt, _) = World(0f);
            var listener = new UEObject();
            listener.Props["Location"] = l;
            listener.Props["bIsPlayer"] = false;
            listener.Props["Alertness"] = A;
            listener.Props["HearingThreshold"] = stored;
            Check("hear-threshold-premise", wantReject, "case 5 must have a sum smaller than its float copy");
            bool heard;
            try { heard = rt.PawnCanHear(interp, listener, n, L, null); }
            catch (InvalidOperationException e) { heard = true; Console.WriteLine("  (went to the trace: " + e.Message + ")"); }
            Check("hear-threshold-wide", !heard, "shipped rejects: the sum on the stack is below the float threshold");
        }

        // L = NaN: `fcomp dist; test ah,1` sets C0 on unordered — refused BEFORE Stimulus.
        {
            var (interp, rt, _) = World(0f);
            var listener = new UEObject();
            listener.Props["Location"] = new UEVector(0f, 0f, 0f);
            listener.Props["bIsPlayer"] = false;
            listener.Props["Alertness"] = 0f;
            listener.Props["HearingThreshold"] = 0f;
            listener.Props["Stimulus"] = -1f;
            bool heard;
            try { heard = rt.PawnCanHear(interp, listener, new UEVector(10f, 0f, 0f), float.NaN, null); }
            catch (InvalidOperationException e) { heard = true; Console.WriteLine("  (went to the trace: " + e.Message + ")"); }
            Check("hear-nan-rejected", !heard && F(listener, "Stimulus") == -1f,
                $"shipped rejects a NaN loudness before Stimulus; heard={heard} Stimulus={F(listener, "Stimulus"):R}");
        }
    }

    // ── PickTarget: aim vs bestAim unrounded ─────────────────────────────────
    static void PickCases()
    {
        var at = new UEVector(-1166.699951171875f, 451.51007080078125f, -2139.760009765625f);
        var proj = new UEVector(-1886.3599853515625f, 1236.56005859375f, -1743.77001953125f);
        var dir = new UEVector(0.49701130390167236f, -0.6206036806106567f, -0.6064905524253845f);
        // shipped: aim_f / dist_f on the stack
        Q dx = (Q.Of(at.x) - Q.Of(proj.x)).R64, dy = (Q.Of(at.y) - Q.Of(proj.y)).R64,
          dz = (Q.Of(at.z) - Q.Of(proj.z)).R64;
        Q aim = (((Q.Of(dir.z) * dz).R64 + (Q.Of(dir.y) * dy).R64).R64 + (Q.Of(dir.x) * dx).R64).R64;
        float aimF = aim.ToFloat();
        Q dist2 = (((dz * dz).R64 + (dy * dy).R64).R64 + (dx * dx).R64).R64;
        float dist = Q.Of(dist2.ToDouble()).Sqrt64().ToFloat();
        Q quot = (Q.Of(aimF) / Q.Of(dist)).R64;
        // smallest float >= the quotient: shipped rejects (aim ≤ bestAim)
        float best = quot.ToFloat();
        if (Q.Of(best).Cmp(quot) < 0) best = MathF.BitIncrement(best);
        // naive float step-by-step: aim/dist > best -> would go to the trace
        float pdx = at.x - proj.x, pdy = at.y - proj.y, pdz = at.z - proj.z;
        float paim = dir.x * pdx + dir.y * pdy + dir.z * pdz;
        float pd2 = pdx * pdx + pdy * pdy + pdz * pdz;
        float pq = paim / (float)Math.Sqrt(pd2);
        Check("pick-premise", Q.Of(best).Cmp(quot) >= 0 && pq > best,
            "case does not distinguish the quotient on the stack from step-by-step float");

        var (interp, rt, level) = World(0f);
        var self = new UEObject();
        self.Props["Location"] = proj;
        self.Props["bIsPlayer"] = false;
        var cand = new UEObject();
        cand.Props["Location"] = at;
        cand.Props["Health"] = 10;
        cand.Props["bProjTarget"] = true;
        level.Props["PawnList"] = cand;
        var args = new object[] { best, 0f, dir, proj };
        object picked;
        try { picked = rt.PickTargetNative(interp, new UEFrame { Object = self, Context = self }, args); }
        catch (InvalidOperationException e) { picked = "trace"; Console.WriteLine("  (went to the trace: " + e.Message + ")"); }
        Check("pick-tie-rejected", picked is null && UEInterpreter.AsFloat(args[0]) == best,
            $"shipped rejects the candidate (quotient ≤ bestAim) without a trace; port: {picked ?? "None"}");
    }

    // Opposite direction: the quotient on the stack is SLIGHTLY GREATER than bestAim — shipped
    // accepts the candidate and calls LineOfSightTo (without BSP this shows as a trace refusal).
    // q: the quotient would round down to bestAim; d: float step-by-step dist would give ≤ bestAim.
    static void PickTraceCase(string name, UEVector at, UEVector proj, UEVector dir, bool viaDist)
    {
        Q dx = (Q.Of(at.x) - Q.Of(proj.x)).R64, dy = (Q.Of(at.y) - Q.Of(proj.y)).R64,
          dz = (Q.Of(at.z) - Q.Of(proj.z)).R64;
        Q aim = (((Q.Of(dir.z) * dz).R64 + (Q.Of(dir.y) * dy).R64).R64 + (Q.Of(dir.x) * dx).R64).R64;
        float aimF = aim.ToFloat();
        Q dist2 = (((dz * dz).R64 + (dy * dy).R64).R64 + (dx * dx).R64).R64;
        float dist = Q.Of(dist2.ToDouble()).Sqrt64().ToFloat();
        Q quot = (Q.Of(aimF) / Q.Of(dist)).R64;
        float best = quot.ToFloat();
        if (Q.Of(best).Cmp(quot) >= 0) best = MathF.BitDecrement(best);   // largest float < the quotient
        bool premise;
        if (!viaDist) premise = Q.Of(quot.ToFloat()).Cmp(Q.Of(best)) <= 0;
        else
        {
            float pdx = at.x - proj.x, pdy = at.y - proj.y, pdz = at.z - proj.z;
            float pd = (float)Math.Sqrt(pdx * pdx + pdy * pdy + pdz * pdz);
            premise = (Q.Of(aimF) / Q.Of(pd)).R64.Cmp(Q.Of(best)) <= 0;
        }
        Check(name + "-premise", Q.Of(best).Cmp(quot) < 0 && premise, "case does not distinguish the edit");
        var (interp, rt, level) = World(0f);
        var self = new UEObject();
        self.Props["Location"] = proj;
        self.Props["bIsPlayer"] = false;
        var cand = new UEObject();
        cand.Props["Location"] = at;
        cand.Props["Health"] = 10;
        cand.Props["bProjTarget"] = true;
        level.Props["PawnList"] = cand;
        bool traced = false;
        try { rt.PickTargetNative(interp, new UEFrame { Object = self, Context = self },
                  new object[] { best, 0f, dir, proj }); }
        catch (InvalidOperationException) { traced = true; }
        Check(name, traced, "shipped accepts the candidate (quotient > bestAim) and goes to LineOfSightTo; the port rejected without a trace");
    }

    static int Main()
    {
        NoiseCases();
        HearCases();
        PickCases();
        PickTraceCase("pick-trace-quotient",
            new UEVector(521.6700439453125f, 995.3699951171875f, -1442.1500244140625f),
            new UEVector(595.9000244140625f, 1603.5999755859375f, -1547.1800537109375f),
            new UEVector(0.13014928996562958f, -0.8570032715797424f, -0.49860456585884094f), false);
        PickTraceCase("pick-trace-dist",
            new UEVector(-237.19998168945312f, 1040.4200439453125f, 566.0599975585938f),
            new UEVector(-997.22998046875f, 686.5700073242188f, -148.24000549316406f),
            new UEVector(-0.4799404442310333f, 0.45453423261642456f, 0.750370442867279f), true);
        Console.WriteLine($"AuditAiTests: passed {_pass}, failed {_fail}");
        return _fail == 0 ? 0 : 1;
    }
}
