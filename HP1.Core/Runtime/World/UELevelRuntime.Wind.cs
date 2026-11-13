// WIND — port of AWind::GetWind / AWind::GetTotalWind (Engine.dll.c:46203-46325,
// disasm GetTotalWind 0x10432180) and its application to particles.
//
// WHO READS THE WIND. Only particles (three GetTotalWind sites, argument disasm):
//   * the emitter, once per update, when `Damping * WindModifier > 0`
//     (0x103c1f0e, 0x103c264a) — at the emitter Location;
//   * each particle with bWindPerParticle (0x103bfd3d) — at its own position.
// Both feed ONLY the damped branch of UParticle::Update (Engine.dll.c:93676-93693):
// terminal = Accel/Damping + WindModifier·Wind. The native Wind.GetWind (425) is not
// called by campaign bytecode (native census).
//
// Carriers in the campaign: Lev3_Dungeon/DungeonB — TriggerWind
// (250, directional) + TorchFire02 with Damping=1, WindModifier=1; Lev5_fluffy —
// FluteWind (−50, point) + FluteSleepFX (bWindPerParticle); quidditch — Wind
// 300 with WindFluctuation=51, of which only Lev3_Quid2 has a responsive emitter.
//
// FIELDS (offset oracle): +0xfc Location, +0x108 Rotation, +0x25c WindSpeed,
// +0x260 WindRadius, +0x261 WindRadiusInner, +0x262 WindFluctuation, +0x264
// WindSource, +0x268 bit0 bPermeating, +0x26c Fluc.
//
// OMITTED (D06 — only particles read the wind):
//  * FLUCTUATION (WindFluctuation != 0): shipped adds lattice noise
//    FUN_104315d0 (32-unit cell, permutation table 0x105f7c18 that the first
//    call shuffles with 256 appRand). Table indices are a signed remainder:
//    for a negative cell coordinate the code reads memory BEFORE the table (process
//    BSS, Engine statics) — impossible to reproduce without a live process image
//    (same nature as gesture frame 426). Measured: of 200 random points within
//    ±6000 units only 46 stay entirely inside the table. The noise structure was fitted
//    to the oracle (16 of 46 bit-exact, the rest is x87 addition order) and is NOT
//    closed. The port prints STOP once and omits the noise term — like the
//    MultiLineCheck tie, the world does not stop.
//  * Shipped's wind list is global and filled by the AWind CONSTRUCTOR
//    (Engine.dll.c:49003-49037), so it also holds class default objects
//    (WindSpeed 256, radius 32² = 1024 units around the origin, Level = NULL
//    — within that radius shipped would dereference a null pointer). The port
//    walks the level's live actors in Actors order; class defaults do not take part.
//
// Checked against the real GetWind under unicorn.
using System;
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        bool _windFluctuationReported;
        public int WindFluctuationSkipped;

        // AWind::GetWind for one source. `lineClear(from, to)` — the answer of
        // UModel::FastLineCheck (true = line of sight exists).
        public static Vector3 WindAt(Vector3 location, UERotator rotation, float windSpeed, byte radius,
            byte radiusInner, byte fluctuation, byte source, bool permeating, Vector3 fluc,
            Vector3 point, Func<Vector3, Vector3, bool> lineClear, out bool fluctuationSkipped)
        {
            fluctuationSkipped = false;
            // Order and precision follow the x87 disasm (0x1043106d-0x10431462):
            //  * coordinate differences are rounded to float (FVector arguments);
            //  * distSq = (dz² + dy²) + dx² — IN THIS order, stored to float;
            //  * dist = (float)sqrt((double)distSq);
            //  * for a point source the differences are taken AGAIN in extended precision and
            //    multiplied by inv = (float)(1/dist); each product is stored to float;
            //  * dir + Fluc: X and Z stay unrounded on the x87 stack (fst), Y
            //    is rounded (fstp) — so the final multiply uses the exact sum for X/Z
            //    and its float rounding for Y;
            //  * falloff and the WindSpeed multiply — without intermediate rounding.
            float dx = point.x - location.x, dy = point.y - location.y, dz = point.z - location.z;
            float distSq = (float)(((double)dz * dz + (double)dy * dy) + (double)dx * dx);
            float reach = (float)(radius * radius);
            if (windSpeed == 0f || (double)reach * reach < distSq) return Vector3.zero;
            if (!permeating && !lineClear(location, point)) return Vector3.zero;
            float dist = (float)Math.Sqrt((double)distSq);
            float dirX, dirY, dirZ;
            if (source == 0 && dist > 0f)
            {
                float inv = (float)(1.0 / dist);
                dirX = (float)(((double)point.x - location.x) * inv);
                dirY = (float)(((double)point.y - location.y) * inv);
                dirZ = (float)(((double)point.z - location.z) * inv);
            }
            else if (source == 0 || source == 1)
            {
                // XAxis of (UnitCoords / Rotation) — the rotation's "forward" vector.
                Vector3 fwd = UEValue.RotateVector(new Vector3(1f, 0f, 0f), rotation);
                dirX = fwd.x; dirY = fwd.y; dirZ = fwd.z;
            }
            else { dirX = 0f; dirY = 0f; dirZ = 0f; }
            // The tail runs in x87 registers (64-bit mantissa, round to even
            // after EVERY operation): double here is off in the last bit
            // (measured: 2 cases of 184), so the arithmetic is exact — Ext80.
            Ext80 sumX = Ext80.Add(dirX, fluc.x), sumZ = Ext80.Add(dirZ, fluc.z);
            float sumY = Ext80.Add(dirY, fluc.y).ToFloat();
            if (fluctuation != 0) fluctuationSkipped = true;
            Ext80 one = Ext80.From(1.0);
            Ext80 num = Ext80.Sub(one, Ext80.Div(Ext80.From(dist), Ext80.From(reach)));
            Ext80 den = Ext80.Sub(one, Ext80.Mul(Ext80.From((double)radiusInner), Ext80.From(0.00390625)));
            Ext80 falloff = Ext80.Div(num, den);
            if (falloff.CompareTo(one) > 0) falloff = one;
            falloff = Ext80.Mul(falloff, Ext80.From(windSpeed));
            // Sign of zero: x87 gives −0 for zero times a negative factor
            // ("other" source with dir = 0 and negative falloff·speed).
            bool negative = falloff.CompareTo(Ext80.From(0.0)) < 0;
            float Z(float v, double factorSign) => v == 0f && (negative != (factorSign < 0 || (factorSign == 0 && double.IsNegative(factorSign)))) ? -0f : v;
            return new Vector3(Z(Ext80.Mul(falloff, sumX).ToFloat(), sumX.SignOrZero), Z(Ext80.Mul(Ext80.From(sumY), falloff).ToFloat(), sumY),
                               Z(Ext80.Mul(sumZ, falloff).ToFloat(), sumZ.SignOrZero));
        }

        // An x87 register value: m · 2^e, |m| < 2^64, round to nearest even
        // after every operation (shipped control word — PC64/RC_NEAR). Exact
        // rational arithmetic on BigInteger; denormals and overflows never occur
        // here (wind magnitudes are units..thousands).
        readonly struct Ext80
        {
            readonly System.Numerics.BigInteger _m; readonly int _e;
            Ext80(System.Numerics.BigInteger m, int e) { _m = m; _e = e; }
            public static Ext80 From(double v)
            {
                if (v == 0.0) return new Ext80(0, 0);
                long bits = BitConverter.DoubleToInt64Bits(v);
                int exp = (int)((bits >> 52) & 0x7ff); long frac = bits & 0xfffffffffffffL;
                long mant = exp == 0 ? frac : frac | (1L << 52);
                int e = (exp == 0 ? 1 : exp) - 1075;
                return new Ext80(bits < 0 ? -mant : mant, e);
            }
            // Own bit length (BigInteger.GetBitLength is missing in .NET Standard 2.1);
            // the argument here is always non-negative.
            static long BitLen(System.Numerics.BigInteger v)
            {
                if (v.IsZero) return 0;
                byte[] b = v.ToByteArray();                 // little-endian, two's complement
                int top = b.Length - 1;
                while (top > 0 && b[top] == 0) top--;
                int bits = 0; for (int t = b[top]; t != 0; t >>= 1) bits++;
                return (long)top * 8 + bits;
            }
            static Ext80 Round(System.Numerics.BigInteger num, System.Numerics.BigInteger den, int e)
            {
                if (num.IsZero) return new Ext80(0, 0);
                bool neg = (num.Sign < 0) != (den.Sign < 0);
                num = System.Numerics.BigInteger.Abs(num); den = System.Numerics.BigInteger.Abs(den);
                // shift so the quotient has >= 66 bits, then round to 64
                int shift = 0;
                long nb = BitLen(num), db = BitLen(den);
                if (nb - db < 66) { shift = (int)(66 - (nb - db)); num <<= shift; }
                var q = System.Numerics.BigInteger.DivRem(num, den, out var rem);
                int extra = (int)BitLen(q) - 64;
                var half = System.Numerics.BigInteger.One << (extra - 1);
                var low = q & ((System.Numerics.BigInteger.One << extra) - 1);
                q >>= extra;
                bool sticky = !rem.IsZero;
                if (low > half || (low == half && (sticky || !q.IsEven))) q += 1;
                else if (low == half && !sticky && q.IsEven) { }
                return new Ext80(neg ? -q : q, e - shift + extra);
            }
            public static Ext80 Mul(Ext80 a, Ext80 b) => Round(a._m * b._m, 1, a._e + b._e);
            public static Ext80 Div(Ext80 a, Ext80 b) => Round(a._m, b._m, a._e - b._e);
            public static Ext80 Sub(Ext80 a, Ext80 b) => Add(a, new Ext80(-b._m, b._e));
            public static Ext80 Add(float a, float b) => Add(From(a), From(b));
            public static Ext80 Add(Ext80 a, Ext80 b)
            {
                if (a._m.IsZero) return b; if (b._m.IsZero) return a;
                int e = Math.Min(a._e, b._e);
                return Round((a._m << (a._e - e)) + (b._m << (b._e - e)), 1, e);
            }
            public int CompareTo(Ext80 o) => Sub(this, o)._m.Sign;
            public double SignOrZero => _m.Sign;
            public float ToFloat()
            {
                if (_m.IsZero) return 0f;
                // a 64-bit exact value cannot go through double without a second rounding,
                // so round to 24 bits directly
                var m = System.Numerics.BigInteger.Abs(_m); int e = _e;
                int extra = (int)BitLen(m) - 24;
                if (extra > 0)
                {
                    var half = System.Numerics.BigInteger.One << (extra - 1);
                    var low = m & ((System.Numerics.BigInteger.One << extra) - 1);
                    m >>= extra; e += extra;
                    if (low > half || (low == half && !m.IsEven)) m += 1;
                }
                float r = (float)((double)m * Math.Pow(2.0, e));
                return _m.Sign < 0 ? -r : r;
            }
        }

        // AWind::GetTotalWind: sum over all winds (shipped does not read the ULevel argument).
        public Vector3 TotalWind(Vector3 point)
        {
            float x = 0f, y = 0f, z = 0f;
            foreach (UEObject w in Actors)
            {
                if (w is null || w.Destroyed || !UEInterpreter.ClassIsA(w.Class, "Wind")) continue;
                Vector3 v = WindAt(UEInterpreter.AsVector3(w.GetProp(PK.Location)), UEInterpreter.AsRotator(w.GetProp(PK.Rotation)),
                    UEInterpreter.AsFloat(w.GetProp(PK.WindSpeed)), (byte)UEInterpreter.ToInt(w.GetProp(PK.WindRadius)),
                    (byte)UEInterpreter.ToInt(w.GetProp(PK.WindRadiusInner)), (byte)UEInterpreter.ToInt(w.GetProp(PK.WindFluctuation)),
                    (byte)UEInterpreter.ToInt(w.GetProp(PK.WindSource)), UEInterpreter.ToBool(w.GetProp(PK.bPermeating)),
                    UEInterpreter.AsVector3(w.GetProp(PK.Fluc)), point,
                    (from, to) => Bsp is null
                        ? throw new InvalidOperationException("wind: the level model has no BSP")
                        : Bsp.FastLineCheckClear(to, from),
                    out bool skipped);
                if (skipped)
                {
                    WindFluctuationSkipped++;
                    if (!_windFluctuationReported)
                    {
                        _windFluctuationReported = true;
                        // Only particles read the wind, the noise is
                        // presentation (D06).
                        HP1.Core.UETrace.Warn("wind §4a: '" + w.GetProp(PK.Name) + "' WindFluctuation="
                            + w.GetProp(PK.WindFluctuation) + " — fluctuation noise (Engine.dll FUN_104315d0, reads process memory "
                            + "before the table) omitted: wind affects only particles (presentation); "
                            + "constant component is applied. See UELevelRuntime.Wind.cs.");
                    }
                }
                x += v.x; y += v.y; z += v.z;
            }
            return new Vector3(x, y, z);
        }
    }
}
