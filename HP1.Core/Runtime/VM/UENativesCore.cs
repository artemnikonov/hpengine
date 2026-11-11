// The pure core of the VM's native-function table (plan §2, natives slice).
// Machinery (registration + dispatch + the shipped MSVCRT random stream) and
// the proven-pure intrinsic groups — no UnityEngine, no frontend types. The
// Unity frontend (UENatives) derives from this and adds the bridge-backed
// groups; the headless smoke (HeadlessNatives) derives directly and
// re-registers only these pure groups plus its own shipped-ported handlers.
// Source citations inside each member are unchanged from the original file.

using System;
using System.Collections.Generic;
using UELib.Core;
using HP1.Core;      // UETrace + UEClassRegistry + MsvcRandom
// Script vectors are the Core value type (see UEInterpreter.cs).
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    public class UENativesCore : IUENativeRegistry
    {
        readonly Dictionary<int, UENativeHandler> _byIndex = new();
        readonly Dictionary<string, UENativeHandler> _byName =
            new(UEPropNameComparer.Instance);
        readonly MsvcRandom _random;
        // Presentation stream (rendering never draws from the game's random numbers): particles, FGlobalRandoms,
        // ported render bodies. Separate from the game stream so that what reaches
        // the frame never changes random game outcomes.
        readonly MsvcRandom _renderRandom;
        // Fixture seam: how many rand() draws each stream consumed.
        internal long GameRandomDrawsForTest => _random.Draws;
        internal long RenderRandomDrawsForTest => _renderRandom.Draws;
        internal HP1.Core.MsvcRandom RenderRandomForTest => _renderRandom;

        // Registers NOTHING: every group is registered by the derived
        // frontend (Unity UENatives in its full order; HeadlessNatives only
        // the pure groups, with its provenance-journaling Pair override).
        // x87 COMPARISONS (Core.dll: fld A; fcomp B; fnstsw; test ah, M). With
        // unordered operands (NaN) fcomp sets C0, C2 and C3 at once,
        // so in shipped Less (test ah,1; je), LessEqual (0x41) and Equal (0x40)
        // give true and NotEqual gives false; Greater/GreaterEqual match C#.
        // Addresses: 0x10138bac, 0x10138d6c, 0x10138f2c, 0x1013900c, vectors
        // 0x1013be9e/0x1013bf9e.
        static bool X87Less(float a, float b) => a < b || float.IsNaN(a) || float.IsNaN(b);
        static bool X87LessEqual(float a, float b) => a <= b || float.IsNaN(a) || float.IsNaN(b);
        static bool X87Equal(float a, float b) => a == b || float.IsNaN(a) || float.IsNaN(b);

        protected UENativesCore(uint randomSeed)
        {
            _random = new MsvcRandom(randomSeed);
            _renderRandom = new MsvcRandom(randomSeed);
        }

        protected int NextRandomInt15() => _random.NextInt15();
        protected float NextRandomFloat01() => _random.NextFloat01();
        // execVRand body (see handler 252): static so the --crtrandbatch check
        // can run the same code against the real Core.
        internal static Vector3 VRandBody(HP1.Core.MsvcRandom rng)
        {
            // Core 0x1013cac0. Components are fstp dword of
            // 2f−1, where f is the UNROUNDED st0 of appFrand;
            // the sum of squares (z²+y²)+x² is in a 64-bit mantissa; loop while
            // 1 < sum. Then the same sum -> fstp qword -> appSqrt (Core
            // 0x10126100: fld qword; fsqrt) -> fdivr 1.0 -> fmul component ->
            // fstp dword. All in 64 bits: a float sum (Vector3.Dot) differed
            // from shipped by 1-4 ulp (reference: crt_rand_oracle.py).
            // LOOP SUBTLETY (0x1013caf9): the third component is stored with `fst`
            // (no pop), and `fmul dword [esp+8]` multiplies the UNROUNDED
            // 2f-1 by its float store; after the loop the sum is recomputed from
            // the three floats.
            // The body sets no control word: PC64 in game logic, PC24 inside the render window
            // (UEWorldFrameProducer.FastMathWindow), where appFrand's fmul and every fadd/fsub/fmul/fsqrt/fdivr round
            // the register to 24 bits (at PC64 a float·float product is exact, so R of it is a no-op there).
            bool pc24 = UEWorldFrameProducer.FastMathWindow;
            HP1.Core.X87Ext R(HP1.Core.X87Ext v) => pc24 ? HP1.Core.X87Ext.Round24(v) : HP1.Core.X87Ext.Round64(v);
            float x, y, z;
            HP1.Core.X87Ext loopSum;
            do {
                x = VRandComponent(rng, pc24).ToFloat();
                y = VRandComponent(rng, pc24).ToFloat();
                var zExt = VRandComponent(rng, pc24);
                z = zExt.ToFloat();
                var X = HP1.Core.X87Ext.From(x); var Y = HP1.Core.X87Ext.From(y);
                loopSum = R(R(R(zExt * HP1.Core.X87Ext.From(z)) + R(Y * Y)) + R(X * X));
            } while (loopSum.CompareTo(HP1.Core.X87Ext.From(1f)) > 0);
            var Xf = HP1.Core.X87Ext.From(x); var Yf = HP1.Core.X87Ext.From(y); var Zf = HP1.Core.X87Ext.From(z);
            var sum = R(R(R(Zf * Zf) + R(Yf * Yf)) + R(Xf * Xf));          // 0x1013cb22..0x1013cb3f
            var root = R(HP1.Core.X87Ext.Sqrt64(HP1.Core.X87Ext.From(sum.ToDouble())));
            var inv = pc24 ? HP1.Core.X87Ext.Div24(HP1.Core.X87Ext.From(1.0), root) : HP1.Core.X87Ext.Div64(HP1.Core.X87Ext.From(1.0), root);
            return new Vector3(R(inv * Xf).ToFloat(), R(inv * Yf).ToFloat(), R(inv * Zf).ToFloat());
        }

        // Node names of the state node's SuperField chain (see IsInState).
        static IEnumerable<string> StateNodeChain(UEInterpreter interp, UEObject self)
        {
            string cur = self.CurrentStateName;
            if (string.IsNullOrEmpty(cur))
            {
                for (UClass c = self.Class; c != null; c = UEInterpreter.CanonicalSuper(c))
                    yield return c.Name.ToString();
                yield break;
            }
            UState st = interp?.FindState(self.Class, cur);
            if (st is null) { yield return cur; yield break; }
            for (UStruct n = st; n != null; n = n.Super as UStruct)
                yield return n.Name.ToString();
        }

        // FString::Caps (Core.dll.c:64283).
        internal static string ShippedCaps(string s)
        {
            var c = s.ToCharArray();
            for (int i = 0; i < c.Length; i++)
                if (c[i] > 0x60 && c[i] < 0x7b) c[i] = (char)(c[i] - 0x20);
            return new string(c);
        }

        // FString::Mid (Core.dll.c:35057), unsigned comparisons.
        internal static string ShippedMid(string s, int start, int count)
        {
            uint len = (uint)s.Length;
            uint st = (uint)start < len ? (uint)start : len;
            uint end = unchecked((uint)(start + count));
            uint en = end >= st ? Math.Min(end, len) : st;
            return s.Substring((int)st, (int)(en - st));
        }

        // _wcsicmp in the "C" locale == 0: 'A'..'Z' fold to 'a'..'z',
        // everything else compares by character code.
        internal static bool AsciiStricmpEquals(string a, string b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                char x = a[i], y = b[i];
                if (x >= 'A' && x <= 'Z') x = (char)(x + 0x20);
                if (y >= 'A' && y <= 'Z') y = (char)(y + 0x20);
                if (x != y) return false;
            }
            return true;
        }

        // fadd st0,st0; fsub 1.0 over appFrand's st0 (Core 0x1013cacf): 2f−1 at the caller's precision.
        static HP1.Core.X87Ext VRandComponent(HP1.Core.MsvcRandom rng, bool pc24)
        {
            var f = rng.NextFrandSt0(pc24);
            HP1.Core.X87Ext d = f + f - HP1.Core.X87Ext.From(1f);                    // doubling is exact
            return pc24 ? HP1.Core.X87Ext.Round24(d) : HP1.Core.X87Ext.Round64(d);
        }
        protected HP1.Core.X87Ext NextRandomFrandSt0() => _random.NextFrandSt0(UEWorldFrameProducer.FastMathWindow);
        protected float NextRandomRange(float min, float max) => _random.NextRandRange(min, max, UEWorldFrameProducer.FastMathWindow);
        protected void SeedRandom(uint seed) => _random.Seed(seed);

        protected static string Str(object v) => v?.ToString();

        protected static UERotator RotAdd(UERotator a, UERotator b, int sign) =>
            new UERotator(a.Pitch + sign * b.Pitch, a.Yaw + sign * b.Yaw, a.Roll + sign * b.Roll);

        // Core.dll 0x1013d020 (287) / 0x1013d0c0 (288) / 0x1013d320 (290):
        // `fild comp; fmul B; fstp dword; fld; fistp` — the product
        // rounds to float, then fistp in the default mode (round to
        // nearest even), not truncation. The product is computed in double:
        // exact for |comp| < 2^29 (x87 holds it exactly in a 64-bit mantissa).
        protected static UERotator RotScale(UERotator a, float s) =>
            new UERotator(Fistp((float)((double)a.Pitch * s)),
                          Fistp((float)((double)a.Yaw * s)),
                          Fistp((float)((double)a.Roll * s)));

        // 0x1013d220 (289) / 0x1013d450 (291): `fld 1.0; fdiv B; fstp dword` —
        // division is multiplication by the float reciprocal, then as in RotScale.
        protected static UERotator RotDiv(UERotator a, float s) => RotScale(a, 1f / s);

        // x87 fistp with RC=00: round to nearest even; NaN and outside int32 —
        // "integer indefinite" 0x80000000.
        protected static int Fistp(float f)
        {
            double d = Math.Round((double)f, MidpointRounding.ToEven);
            return double.IsNaN(d) || d < int.MinValue || d > int.MaxValue ? int.MinValue : (int)d;
        }

        // Logical operators: bodies of UObject::exec*_BoolBool in Core.dll (references
        // at each registration).
        // Note: AndAnd / OrOr are not dispatched through these eager callbacks.
        // UEInterpreter.NativeFunctionToken intercepts indices 130/132 and
        // evaluates the RHS lazily through EX_Skip, like execAndAnd_BoolBool /
        // execOrOr_BoolBool (Core.dll.c:67281 / 34421).
        protected void RegisterBoolOps()
        {
            // execNot_PreBool (Core.dll.c:11557 / 0x101360e0) — `!A`
            Pair(129, "Not_PreBool", (interp, frame, args) =>
                !UEInterpreter.ToBool(args.Length > 0 ? args[0] : null));

            // execAndAnd_BoolBool (Core.dll.c:67281 / 0x10136350) — `A && B`
            // (the registration is not called by the dispatcher — see above)
            Pair(130, "AndAnd_BoolBool", (interp, frame, args) =>
                UEInterpreter.ToBool(args.Length > 0 ? args[0] : null)
                && UEInterpreter.ToBool(args.Length > 1 ? args[1] : null));

            // execXorXor_BoolBool (Core.dll.c:45976 / 0x10136450) — `A ^^ B`
            Pair(131, "XorXor_BoolBool", (interp, frame, args) =>
                UEInterpreter.ToBool(args.Length > 0 ? args[0] : null)
                ^ UEInterpreter.ToBool(args.Length > 1 ? args[1] : null));

            // execOrOr_BoolBool (Core.dll.c:34421 / 0x10136530) — `A || B`
            // (the registration is not called by the dispatcher — see above)
            Pair(132, "OrOr_BoolBool", (interp, frame, args) =>
                UEInterpreter.ToBool(args.Length > 0 ? args[0] : null)
                || UEInterpreter.ToBool(args.Length > 1 ? args[1] : null));

            // execEqualEqual_BoolBool (Core.dll.c:64018 / 0x10136190) — `A == B`
            Pair(242, "EqualEqual_BoolBool", (interp, frame, args) =>
                UEInterpreter.ToBool(args.Length > 0 ? args[0] : null)
                == UEInterpreter.ToBool(args.Length > 1 ? args[1] : null));

            // execNotEqual_BoolBool (Core.dll.c:9212 / 0x10136270) — `A != B`
            Pair(243, "NotEqual_BoolBool", (interp, frame, args) =>
                UEInterpreter.ToBool(args.Length > 0 ? args[0] : null)
                != UEInterpreter.ToBool(args.Length > 1 ? args[1] : null));
        }

        // Integer operators — bodies of UObject::exec*_Int* in Core.dll: int arithmetic, bitwise,
        // comparison, and in-place operators. The in-place variants
        // (AddAdd_PreInt, AddEqual_IntInt, etc.) take their first parameter
        // by reference; UELib exposes that as PropertyFlag.OutParm on the
        // UFunction declaration. Our CollectArgsWithOuts + WriteLValue
        // round-trip (UEInterpreter.cs) handles the writeback transparently —
        // the native just mutates args[0] before returning.
        protected void RegisterIntOps()
        {
            // ── Arithmetic ─────────────────────────────────────────────────
            // execAdd_IntInt (Core.dll.c:60616 / 0x101370b0) — `A + B`
            Pair(146, "Add_IntInt", (interp, frame, args) =>
                UEInterpreter.ToInt(args[0]) + UEInterpreter.ToInt(args[1]));
            // execSubtract_IntInt (Core.dll.c:64908 / 0x10137180) — `A - B`
            Pair(147, "Subtract_IntInt", (interp, frame, args) =>
                UEInterpreter.ToInt(args[0]) - UEInterpreter.ToInt(args[1]));
            // execMultiply_IntInt (Core.dll.c:59611) — `A * B`
            Pair(144, "Multiply_IntInt", (interp, frame, args) =>
                UEInterpreter.ToInt(args[0]) * UEInterpreter.ToInt(args[1]));
            // Core.dll 0x10137022: `cmp B,0; je` — for B == 0 the result is 0, no
            // exception; otherwise idiv.
            Pair(145, "Divide_IntInt", (interp, frame, args) => {
                int b = UEInterpreter.ToInt(args[1]);
                return b == 0 ? 0 : UEInterpreter.ToInt(args[0]) / b;
            });
            // execSubtract_PreInt (Core.dll.c:65723 / 0x10136e50) — `-A` (unary minus)
            Pair(143, "Subtract_PreInt", (interp, frame, args) =>
                -UEInterpreter.ToInt(args[0]));
            // execComplement_PreInt (Core.dll.c:48758 / 0x10136cd0) — `~A` (bitwise NOT)
            Pair(141, "Complement_PreInt", (interp, frame, args) =>
                ~UEInterpreter.ToInt(args[0]));

            // ── Bitwise ────────────────────────────────────────────────────
            // execAnd_IntInt (Core.dll.c:64580 / 0x101378d0) — `A & B`
            Pair(156, "And_IntInt", (interp, frame, args) =>
                UEInterpreter.ToInt(args[0]) & UEInterpreter.ToInt(args[1]));
            // execOr_IntInt (Core.dll.c:38796 / 0x10137a70) — `A | B`
            Pair(158, "Or_IntInt", (interp, frame, args) =>
                UEInterpreter.ToInt(args[0]) | UEInterpreter.ToInt(args[1]));
            // execXor_IntInt (Core.dll.c:38423 / 0x101379a0) — `A ^ B`
            Pair(157, "Xor_IntInt", (interp, frame, args) =>
                UEInterpreter.ToInt(args[0]) ^ UEInterpreter.ToInt(args[1]));
            // execLessLess_IntInt (Core.dll.c:32474 / 0x10137250) — `A << B`
            Pair(148, "LessLess_IntInt", (interp, frame, args) =>
                UEInterpreter.ToInt(args[0]) << UEInterpreter.ToInt(args[1]));
            // execGreaterGreater_IntInt (Core.dll.c:61521 / 0x10137320) — `A >> B` (arithmetic shift)
            Pair(149, "GreaterGreater_IntInt", (interp, frame, args) =>
                UEInterpreter.ToInt(args[0]) >> UEInterpreter.ToInt(args[1]));
            // execGreaterGreaterGreater_IntInt (Core.dll.c:33902 / 0x10136d80) — `A >>> B` (logical
            // shift); C# integer right-shift is arithmetic for int, so cast
            // through uint to get unsigned-shift behavior.
            Pair(196, "GreaterGreaterGreater_IntInt", (interp, frame, args) =>
                (int)((uint)UEInterpreter.ToInt(args[0]) >> UEInterpreter.ToInt(args[1])));

            // ── Comparison (return bool) ───────────────────────────────────
            // execLess_IntInt (Core.dll.c:12381 / 0x101373f0) — `A < B`
            Pair(150, "Less_IntInt", (interp, frame, args) =>
                UEInterpreter.ToInt(args[0]) < UEInterpreter.ToInt(args[1]));
            // execGreater_IntInt (Core.dll.c:9189 / 0x101374c0) — `A > B`
            Pair(151, "Greater_IntInt", (interp, frame, args) =>
                UEInterpreter.ToInt(args[0]) > UEInterpreter.ToInt(args[1]));
            // execLessEqual_IntInt (Core.dll.c:57543 / 0x10137590) — `A <= B`
            Pair(152, "LessEqual_IntInt", (interp, frame, args) =>
                UEInterpreter.ToInt(args[0]) <= UEInterpreter.ToInt(args[1]));
            // execGreaterEqual_IntInt (Core.dll.c:53449 / 0x10137660) — `A >= B`
            Pair(153, "GreaterEqual_IntInt", (interp, frame, args) =>
                UEInterpreter.ToInt(args[0]) >= UEInterpreter.ToInt(args[1]));
            // execEqualEqual_IntInt (Core.dll.c:51351 / 0x10137730) — `A == B`
            Pair(154, "EqualEqual_IntInt", (interp, frame, args) =>
                UEInterpreter.ToInt(args[0]) == UEInterpreter.ToInt(args[1]));
            // execNotEqual_IntInt (Core.dll.c:25788 / 0x10137800) — `A != B`
            Pair(155, "NotEqual_IntInt", (interp, frame, args) =>
                UEInterpreter.ToInt(args[0]) != UEInterpreter.ToInt(args[1]));

            // ── In-place (arg 0 is OutParm; mutate args[0]) ───────────────
            // execAddAdd_PreInt (Core.dll.c:5914 / 0x10137f20) — `++A`
            Pair(163, "AddAdd_PreInt", (interp, frame, args) => {
                int v = UEInterpreter.ToInt(args[0]) + 1;
                args[0] = v;
                return v;
            });
            // execSubtractSubtract_PreInt (Core.dll.c:10013 / 0x10137fe0) — `--A`
            Pair(164, "SubtractSubtract_PreInt", (interp, frame, args) => {
                int v = UEInterpreter.ToInt(args[0]) - 1;
                args[0] = v;
                return v;
            });
            // execAddAdd_Int (Core.dll.c:55279 / 0x101380a0) — `A++` (post, returns old)
            Pair(165, "AddAdd_Int", (interp, frame, args) => {
                int v = UEInterpreter.ToInt(args[0]);
                args[0] = v + 1;
                return v;
            });
            // execSubtractSubtract_Int (Core.dll.c:47504 / 0x10138160) — `A--` (post)
            Pair(166, "SubtractSubtract_Int", (interp, frame, args) => {
                int v = UEInterpreter.ToInt(args[0]);
                args[0] = v - 1;
                return v;
            });
            // execAddEqual_IntInt (Core.dll.c:16217 / 0x10137d40) — `A += B`
            Pair(161, "AddEqual_IntInt", (interp, frame, args) => {
                int v = UEInterpreter.ToInt(args[0]) + UEInterpreter.ToInt(args[1]);
                args[0] = v;
                return v;
            });
            // execSubtractEqual_IntInt (Core.dll.c:1384) — `A -= B`
            Pair(162, "SubtractEqual_IntInt", (interp, frame, args) => {
                int v = UEInterpreter.ToInt(args[0]) - UEInterpreter.ToInt(args[1]);
                args[0] = v;
                return v;
            });
            // Core.dll 0x10137ba5: `fild A; fmul B; _ftol` — product in a
            // 64-bit mantissa (exact for int32×float), _ftol truncation
            // (outside int64 and NaN — 0, otherwise the low dword). Rounding A and the
            // product to float would make `100 *= 0.01` give 1
            // instead of shipped 0 (0.01f < 0.01). Oracle: validate_core_math_natives.py.
            Pair(159, "MultiplyEqual_IntFloat", (interp, frame, args) => {
                int v = X87.MulIntFloatFtol(UEInterpreter.ToInt(args[0]), UEInterpreter.AsFloat(args[1]));
                args[0] = v;
                return v;
            });
            // Core.dll 0x10137c97: B == 0.0 → A = _ftol(0.0) = 0; otherwise
            // `fild A; fdiv B; _ftol` (MSVCRT, truncation, outside int64 — 0) — the dividend
            // never passes through float. The quotient here is double (x87 — extended).
            Pair(160, "DivideEqual_IntFloat", (interp, frame, args) => {
                int v = X87.DivIntFloatFtol(UEInterpreter.ToInt(args[0]), UEInterpreter.AsFloat(args[1]));
                args[0] = v;
                return v;
            });
        }

        // Byte operators — bodies of UObject::exec*_Byte* in Core.dll: byte in-place / increment operators
        // (class Object). uint8 wraparound via the (byte) cast. Same out-param
        // contract as int ops (arg 0 mutated, written back). Byte comparisons reuse
        // the Int handlers (by name the Core.dll decompile has only
        // exec{Add,Subtract,Multiply,Divide}Equal_ByteByte — no byte comparisons
        // among them). Previously absent →
        // `byte i; i++` silently no-op'd ("Unknown native idx=139").
        protected void RegisterByteOps()
        {
            // post: ReturnValue = A++ / A-- ; pre: ++A / --A — execAddAdd_Byte /
            // execSubtractSubtract_Byte / execAddAdd_PreByte / execSubtractSubtract_PreByte
            // (Core.dll.c:16726 / 64143 / 47830 / 13124)
            Pair(139, "AddAdd_Byte", (interp, frame, args) => {
                byte v = (byte)UEInterpreter.ToInt(args[0]); args[0] = (byte)(v + 1); return v; });
            Pair(140, "SubtractSubtract_Byte", (interp, frame, args) => {
                byte v = (byte)UEInterpreter.ToInt(args[0]); args[0] = (byte)(v - 1); return v; });
            Pair(137, "AddAdd_PreByte", (interp, frame, args) => {
                byte v = (byte)(UEInterpreter.ToInt(args[0]) + 1); args[0] = v; return v; });
            Pair(138, "SubtractSubtract_PreByte", (interp, frame, args) => {
                byte v = (byte)(UEInterpreter.ToInt(args[0]) - 1); args[0] = v; return v; });
            // in-place A op= B — execAddEqual_ByteByte / execSubtractEqual_ByteByte /
            // execMultiplyEqual_ByteByte / execDivideEqual_ByteByte (Core.dll.c:3616 /
            // 12145 / 13157 / 19521)
            Pair(135, "AddEqual_ByteByte", (interp, frame, args) => {
                byte v = (byte)(UEInterpreter.ToInt(args[0]) + UEInterpreter.ToInt(args[1])); args[0] = v; return v; });
            Pair(136, "SubtractEqual_ByteByte", (interp, frame, args) => {
                byte v = (byte)(UEInterpreter.ToInt(args[0]) - UEInterpreter.ToInt(args[1])); args[0] = v; return v; });
            Pair(133, "MultiplyEqual_ByteByte", (interp, frame, args) => {
                byte v = (byte)(UEInterpreter.ToInt(args[0]) * UEInterpreter.ToInt(args[1])); args[0] = v; return v; });
            // Core.dll 0x10136779: for B == 0 A is NOT changed, the result is 0.
            Pair(134, "DivideEqual_ByteByte", (interp, frame, args) => {
                int b = UEInterpreter.ToInt(args[1]);
                if (b == 0) return (byte)0;
                byte v = (byte)(UEInterpreter.ToInt(args[0]) / b); args[0] = v; return v; });
        }

        // Float operators — bodies of UObject::exec*_Float* in Core.dll: float arithmetic, comparison,
        // and in-place operator family. Same out-param contract as int ops
        // for the *Equal variants (arg 0 mutated, writes back via
        // CollectArgsWithOuts/WriteLValue).
        protected void RegisterFloatOps()
        {
            // ── Unary / power / arithmetic ────────────────────────────────
            // execSubtract_PreFloat (Core.dll.c:9304 / 0x101385b0) — `-A`
            Pair(169, "Subtract_PreFloat", (interp, frame, args) =>
                -UEInterpreter.AsFloat(args[0]));
            // execMultiplyMultiply_FloatFloat (Core.dll.c:21912 / 0x10138660) — `A ** B`
            // (power): (float)appPow(double, double) → MSVCRT _CIpow; bit-exactness is not
            // established
            Pair(170, "MultiplyMultiply_FloatFloat", (interp, frame, args) =>
                (float)System.Math.Pow(
                    UEInterpreter.AsFloat(args[0]),
                    UEInterpreter.AsFloat(args[1])));
            // execMultiply_FloatFloat (Core.dll.c:28351 / 0x10138740) — `A * B`
            Pair(171, "Multiply_FloatFloat", (interp, frame, args) =>
                X87.Mul(UEInterpreter.AsFloat(args[0]), UEInterpreter.AsFloat(args[1])));
            // execDivide_FloatFloat (Core.dll.c:51871 / 0x10138810) — `A / B`
            Pair(172, "Divide_FloatFloat", (interp, frame, args) =>
                X87.Div(UEInterpreter.AsFloat(args[0]), UEInterpreter.AsFloat(args[1])));
            // execPercent_FloatFloat (Core.dll.c:63768 / 0x101388e0) — `A % B`
            Pair(173, "Percent_FloatFloat", (interp, frame, args) =>
                UEInterpreter.AsFloat(args[0]) % UEInterpreter.AsFloat(args[1]));
            // execAdd_FloatFloat (Core.dll.c:32180 / 0x101389c0) — `A + B`
            Pair(174, "Add_FloatFloat", (interp, frame, args) =>
                X87.Add(UEInterpreter.AsFloat(args[0]), UEInterpreter.AsFloat(args[1])));
            // execSubtract_FloatFloat (Core.dll.c:41677 / 0x10138a90) — `A - B`
            Pair(175, "Subtract_FloatFloat", (interp, frame, args) =>
                X87.Sub(UEInterpreter.AsFloat(args[0]), UEInterpreter.AsFloat(args[1])));

            // ── Comparison (return bool) ───────────────────────────────────
            // Less_FloatFloat — x87 fcomp (X87Less above): `A < B` or a NaN operand -> true.
            Pair(176, "Less_FloatFloat", (interp, frame, args) =>
                X87Less(UEInterpreter.AsFloat(args[0]), UEInterpreter.AsFloat(args[1])));
            // execGreater_FloatFloat (Core.dll.c:67258 / 0x10138c40) — `A > B`
            Pair(177, "Greater_FloatFloat", (interp, frame, args) =>
                UEInterpreter.AsFloat(args[0]) > UEInterpreter.AsFloat(args[1]));
            // LessEqual_FloatFloat — x87 fcomp (X87LessEqual): `A <= B` or a NaN operand -> true.
            Pair(178, "LessEqual_FloatFloat", (interp, frame, args) =>
                X87LessEqual(UEInterpreter.AsFloat(args[0]), UEInterpreter.AsFloat(args[1])));
            // execGreaterEqual_FloatFloat (Core.dll.c:58163 / 0x10138e00) — `A >= B`
            Pair(179, "GreaterEqual_FloatFloat", (interp, frame, args) =>
                UEInterpreter.AsFloat(args[0]) >= UEInterpreter.AsFloat(args[1]));
            // EqualEqual_FloatFloat — x87 fcomp (X87Equal): `A == B` or a NaN operand -> true.
            Pair(180, "EqualEqual_FloatFloat", (interp, frame, args) =>
                X87Equal(UEInterpreter.AsFloat(args[0]), UEInterpreter.AsFloat(args[1])));
            // NotEqual_FloatFloat — negation of X87Equal: false with a NaN operand.
            Pair(181, "NotEqual_FloatFloat", (interp, frame, args) =>
                !X87Equal(UEInterpreter.AsFloat(args[0]), UEInterpreter.AsFloat(args[1])));

            // ── In-place (arg 0 OutParm) ──────────────────────────────────
            // execMultiplyEqual_FloatFloat (Core.dll.c:33946 / 0x101391a0) — `A *= B`
            Pair(182, "MultiplyEqual_FloatFloat", (interp, frame, args) => {
                float v = X87.Mul(UEInterpreter.AsFloat(args[0]), UEInterpreter.AsFloat(args[1]));
                args[0] = v;
                return v;
            });
            // execDivideEqual_FloatFloat (Core.dll.c:1298 / 0x10139280) — `A /= B`
            Pair(183, "DivideEqual_FloatFloat", (interp, frame, args) => {
                float v = X87.Div(UEInterpreter.AsFloat(args[0]), UEInterpreter.AsFloat(args[1]));
                args[0] = v;
                return v;
            });
            // execAddEqual_FloatFloat (Core.dll.c:39089 / 0x10139360) — `A += B`
            Pair(184, "AddEqual_FloatFloat", (interp, frame, args) => {
                float v = X87.Add(UEInterpreter.AsFloat(args[0]), UEInterpreter.AsFloat(args[1]));
                args[0] = v;
                return v;
            });
            // execSubtractEqual_FloatFloat (Core.dll.c:12081 / 0x10139440) — `A -= B`
            Pair(185, "SubtractEqual_FloatFloat", (interp, frame, args) => {
                float v = X87.Sub(UEInterpreter.AsFloat(args[0]), UEInterpreter.AsFloat(args[1]));
                args[0] = v;
                return v;
            });
        }

        // String functions and operators — bodies of UObject::exec* in Core.dll
        // for UE1 v220 (the secondary registrations at idx 197+ are for
        // newer engine versions, not HP1).
        // Pure string intrinsics (no Unity, no game bridge) — safe for the
        // headless core. Localize is Core-owned (RegisterLocalizationOps).
        protected void RegisterStringOpsPure()
        {
            // ── Constructors / accessors ──────────────────────────────────
            // execConcat_StringString (Core.dll.c:16447 / 0x1013e5b0) — `A $ B` (string concatenation)
            Pair(112, "Concat_StrStr", (interp, frame, args) =>
                (Str(args[0]) ?? "") + (Str(args[1]) ?? ""));
            // execAt_StringString (Core.dll.c:26971 / 0x1013e7a0) — `A @ B` (concat with single space)
            Pair(168, "At_StrStr", (interp, frame, args) =>
                (Str(args[0]) ?? "") + " " + (Str(args[1]) ?? ""));
            // execLen (Core.dll.c:16821) — string length
            Pair(125, "Len", (interp, frame, args) =>
                (Str(args[0]) ?? "").Length);
            // FString::Caps (Core.dll.c:64283): only 'a'..'z' (0x60 < c < 0x7b)
            // are reduced by 0x20; other characters (incl. non-ASCII) stay as-is.
            Pair(235, "Caps", (interp, frame, args) => ShippedCaps(Str(args[0]) ?? ""));
            // Locs (238) is NOT registered: HP1 has no such native — no execLocs
            // in Core.dll, no Locs name in Core.u (only FString::Locs,
            // Core.dll.c:7393). A call to native 238 goes loudly to OnUnknownNative.
            // ── Substring family ──────────────────────────────────────────
            // execLeft (Core.dll.c:22754 / 0x1013fdb0) — substring(0, clamp(i, 0, len))
            Pair(128, "Left", (interp, frame, args) => {
                var s = Str(args[0]) ?? "";
                int i = System.Math.Max(0, System.Math.Min(UEInterpreter.ToInt(args[1]), s.Length));
                return s.Substring(0, i);
            });
            // execRight (Core.dll.c:28435) — last `count` chars
            Pair(234, "Right", (interp, frame, args) => {
                var s = Str(args[0]) ?? "";
                int count = System.Math.Max(0, System.Math.Min(UEInterpreter.ToInt(args[1]), s.Length));
                return s.Substring(s.Length - count, count);
            });
            // execMid -> FString::Mid (Core.dll.c:35057): Count defaults to 0xffff;
            // Start and End = Start + Count are compared UNSIGNED:
            //   Start' = (uint)Start < Len ? Start : Len;
            //   End'   = (uint)End >= Start' ? min((uint)End, Len) : Start'.
            // A negative Start gives an empty string.
            Pair(127, "Mid", (interp, frame, args) => {
                var s = Str(args[0]) ?? "";
                int start = UEInterpreter.ToInt(args[1]);
                int count = (args.Length > 2 && args[2] != null) ? UEInterpreter.ToInt(args[2]) : 0xffff;
                return ShippedMid(s, start, count);
            });

            // ── Search / character ─────────────────────────────────────────
            // execInStr (Core.dll.c:65471) — index of substring, -1 if not found.
            Pair(126, "InStr", (interp, frame, args) =>
                (Str(args[0]) ?? "").IndexOf(Str(args[1]) ?? "", StringComparison.Ordinal));
            // execAsc (Core.dll.c:24245): `(ushort)*Str` — the whole first character
            // (16 bits), 0 for an empty string.
            Pair(237, "Asc", (interp, frame, args) => {
                var s = Str(args[0]) ?? "";
                return s.Length > 0 ? (int)s[0] : 0;
            });
            // execChr (Core.dll.c:43715): character `A & 0xffff`; for a zero
            // character the string is EMPTY (length by appStrlen), not "\0".
            Pair(236, "Chr", (interp, frame, args) => {
                int c = UEInterpreter.ToInt(args[0]) & 0xffff;
                return c == 0 ? "" : new string((char)c, 1);
            });

            // ── Comparison (return bool) ───────────────────────────────────
            // execLess_StringString (Core.dll.c:10051 / 0x1013e990) — lexicographic `A < B`
            Pair(115, "Less_StrStr", (interp, frame, args) =>
                string.CompareOrdinal(Str(args[0]) ?? "", Str(args[1]) ?? "") < 0);
            // execGreater_StringString (Core.dll.c:42173 / 0x1013eba0) — `A > B`
            Pair(116, "Greater_StrStr", (interp, frame, args) =>
                string.CompareOrdinal(Str(args[0]) ?? "", Str(args[1]) ?? "") > 0);
            // execLessEqual_StringString (Core.dll.c:51122 / 0x1013edb0) — `A <= B`
            Pair(120, "LessEqual_StrStr", (interp, frame, args) =>
                string.CompareOrdinal(Str(args[0]) ?? "", Str(args[1]) ?? "") <= 0);
            // execGreaterEqual_StringString (Core.dll.c:61185 / 0x1013efc0) — `A >= B`
            Pair(121, "GreaterEqual_StrStr", (interp, frame, args) =>
                string.CompareOrdinal(Str(args[0]) ?? "", Str(args[1]) ?? "") >= 0);
            // execEqualEqual_StringString (Core.dll.c:22595 / 0x1013f1d0) — `A == B`
            Pair(122, "EqualEqual_StrStr", (interp, frame, args) =>
                (Str(args[0]) ?? "") == (Str(args[1]) ?? ""));
            // execNotEqual_StringString (Core.dll.c:60642) — `A != B`
            Pair(123, "NotEqual_StrStr", (interp, frame, args) =>
                (Str(args[0]) ?? "") != (Str(args[1]) ?? ""));
            // execComplementEqual_StringString (Core.dll.c:28895) — `A ~= B`, case-insensitive
            // equality. CutScene.handleCast
            // uses it to match command keywords (Command ~= "MOVETO" etc.).
            // execComplementEqual_StringString (Core.dll.c:28895): appStricmp ==
            // msvcrt _wcsicmp. No shipped module imports setlocale
            // (import tables of Core/Engine/.../HP.exe), so the CRT stays in the
            // "C" locale: case folds only for 'A'..'Z'.
            Pair(124, "ComplementEqual_StrStr", (interp, frame, args) =>
                AsciiStricmpEquals(Str(args[0]) ?? "", Str(args[1]) ?? ""));
        }

        // Math functions — bodies of UObject::exec* in Core.dll. All pure (no
        // Unity bridge). Float trig/exp/sqrt map to System.Math; Rand/FRand use
        // the shared shipped MSVCRT stream in HP1.Core.
        protected void RegisterMathOps()
        {
            // Core.dll execAbs (0x10139549): `fcomp 0.0; test ah,1; jne -> fchs`
            // — the sign flips only when A < 0 (and for NaN); Abs(-0.0) = -0.0.
            Pair(186, "Abs", (interp, frame, args) => {
                float a = UEInterpreter.AsFloat(args[0]);
                return a < 0f || float.IsNaN(a) ? -a : a;
            });
            // Sin/Cos/Tan/Atan — radians. execSin/Cos/Tan/Atan call appSin/appCos/appTan/appAtan (Core 0x101260a0
            // fsin, 0x101260b0 fcos, 0x101260d0 fptan; fstp st(0), 0x101260e0 fld1; fpatan) on the qword argument;
            // the result goes out by fstp dword. UEX87Transcendental: correctly rounded 64-bit results (D-71); finite
            // |x| >= 2^63: fsin/fcos leave x (C2), appTan reads the popped register (NaN); ±inf -> the indefinite NaN.
            // ±0 gives ±0 for sin, tan, atan (X87Ext keeps the sign of zero).
            Pair(187, "Sin", (interp, frame, args) =>
                UEX87Transcendental.Fsin(X87Ext.From(UEInterpreter.AsFloat(args[0]))).ToFloat());
            Pair(188, "Cos", (interp, frame, args) =>
                UEX87Transcendental.Fcos(X87Ext.From(UEInterpreter.AsFloat(args[0]))).ToFloat());
            Pair(189, "Tan", (interp, frame, args) =>
                UEX87Transcendental.AppTan(X87Ext.From(UEInterpreter.AsFloat(args[0]))).ToFloat());
            Pair(190, "Atan", (interp, frame, args) =>
                UEX87Transcendental.AppAtan(X87Ext.From(UEInterpreter.AsFloat(args[0]))).ToFloat());
            // Exp/Loge — Core.dll appExp/appLoge (x87), not std::exp/std::log.
            // appExp (Core 0x10126060): fldl2e·x, frndint, f2xm1, fscale — the body is modelled (UEX87Transcendental.AppExp,
            // judged by se_harness/bsp_diff/app_exp_oracle.py; D-74), but no shipped script calls Exp — proven by the
            // campaign-wide bytecode scan (HeadlessSmoke --nativescan, SelfTest exp_has_no_shipped_caller), so the native's
            // precision window was never established. appLoge (execLoge -> 0x10102d15): fldln2, fyl2x — NOT MODELLED (D-74):
            // fyl2x has no reference and no shipped script calls Loge (same scan). A call means that proof no longer holds:
            // refuse loudly.
            Pair(191, "Exp", (interp, frame, args) =>
                throw new NotSupportedException("Exp (native 191, appExp: f2xm1) is not modelled: no shipped script calls it (D-74)"));
            Pair(192, "Loge", (interp, frame, args) =>
                throw new NotSupportedException("Loge (native 192, appLoge: fyl2x) is not modelled: no shipped script calls it (D-74)"));
            // execSqrt (Core.dll.c:43915 / 0x10139a00) / execSquare (:7049 / 0x10139ab0)
            Pair(193, "Sqrt", (interp, frame, args) =>
                X87.Sqrt(UEInterpreter.AsFloat(args[0])));
            Pair(194, "Square", (interp, frame, args) => {
                float a = UEInterpreter.AsFloat(args[0]);
                return a * a;
            });
            // execMin (Core.dll.c:17627 / 0x101382e0) / execMax (:11531 / 0x101383c0) /
            // execClamp — int
            Pair(249, "Min", (interp, frame, args) =>
                System.Math.Min(UEInterpreter.ToInt(args[0]), UEInterpreter.ToInt(args[1])));
            Pair(250, "Max", (interp, frame, args) =>
                System.Math.Max(UEInterpreter.ToInt(args[0]), UEInterpreter.ToInt(args[1])));
            // execClamp (Core.dll.c:24065) — `clamp(V, A, B)`
            Pair(251, "Clamp", (interp, frame, args) => {
                int v = UEInterpreter.ToInt(args[0]);
                int lo = UEInterpreter.ToInt(args[1]);
                int hi = UEInterpreter.ToInt(args[2]);
                return v < lo ? lo : (v > hi ? hi : v);
            });
            // Core.dll execFMax (0x10139d13): `fcomp; test ah,1; jne` — A when
            // A >= B (including equal ±0), otherwise and for NaN — B.
            // System.Math.Max would pick +0 and NaN.
            Pair(245, "FMax", (interp, frame, args) => {
                float a = UEInterpreter.AsFloat(args[0]), b = UEInterpreter.AsFloat(args[1]);
                return a >= b ? a : b;
            });
            // Core.dll execFMin (0x10139c33): `test ah,0x41; je` — B only when
            // A > B; when equal or unordered — A.
            Pair(244, "FMin", (interp, frame, args) => {
                float a = UEInterpreter.AsFloat(args[0]), b = UEInterpreter.AsFloat(args[1]);
                return a > b ? b : a;
            });
            // execFClamp (Core.dll.c:46203; disasm 0x10139e11..0x10139e5f) — float `clamp(V, A, B)`: `fld V; fcomp A;
            // test ah, 1` (V < A or unordered) -> A; else `fld V; fcomp B; test ah, 1` (V < B or unordered) -> V; else B.
            Pair(246, "FClamp", (interp, frame, args) => {
                float v = UEInterpreter.AsFloat(args[0]);
                float lo = UEInterpreter.AsFloat(args[1]);
                float hi = UEInterpreter.AsFloat(args[2]);
                return !(v >= lo) ? lo : !(v >= hi) ? v : hi;
            });
            // Core.dll execLerp (0x10139f41): `fld B; fsub A; fmul Alpha; fadd A;
            // fstp dword` — the whole chain in a 64-bit mantissa, one rounding
            // to float at the end.
            Pair(247, "Lerp", (interp, frame, args) =>
                X87.Lerp(UEInterpreter.AsFloat(args[0]), UEInterpreter.AsFloat(args[1]),
                         UEInterpreter.AsFloat(args[2])));
            // Core.dll execRand (0x10138255): `A > 0 ? appRand() % A : 0` (idiv,
            // A unbounded; for A <= 0 rand() is not called). Clamping to
            // Min(A, 32767) would give 0 instead of 32767 when A > 32767 and rand() = 32767.
            Pair(167, "Rand", (interp, frame, args) => {
                int max = UEInterpreter.ToInt(args[0]);
                return max > 0 ? NextRandomInt15() % max : 0;
            });
            // execFRand (Core.dll.c:66816) — random float in [0, 1)
            Pair(195, "FRand", (interp, frame, args) =>
                NextRandomFloat01());
            // execRandRange (Core.dll.c:6905) -> appRandRange(float,float)
            // (0x101261a0) at the caller's precision (PC64 game tick, PC24 render window).
            Pair(1033, "RandRange", (interp, frame, args) =>
                NextRandomRange(UEInterpreter.AsFloat(args[0]), UEInterpreter.AsFloat(args[1])));
        }

        // Vector/Rotator operators — bodies of UObject::exec* in Core.dll: Vector (HP1.UEScript.UEVector) and
        // Rotator (UERotator) intrinsic math. Pure arithmetic in UE1 space;
        // no Unity-world conversion here (that lives in the SetLocation/
        // SetRotation bridge). The coordinate-transform operators
        // (>>/<< VectorRotator, idx 275/276) and Vector<->Rotator casts are
        // deferred to the transform-bridge commit where UE1's Coords::Rotation
        // convention is established.
        protected void RegisterVectorOps()
        {
            // ── Vector arithmetic ─────────────────────────────────────────
            Pair(211, "Subtract_PreVector", (interp, frame, args) =>      // -A
                -UEInterpreter.AsVector3(args[0]));
            Pair(212, "Multiply_VectorFloat", (interp, frame, args) =>    // A*B
                X87.Mul(UEInterpreter.AsVector3(args[0]), UEInterpreter.AsFloat(args[1])));
            Pair(213, "Multiply_FloatVector", (interp, frame, args) =>    // A*B
                X87.Mul(UEInterpreter.AsVector3(args[1]), UEInterpreter.AsFloat(args[0])));
            Pair(214, "Divide_VectorFloat", (interp, frame, args) =>      // A/B, see X87.DivVectorFloat
                X87.DivVectorFloat(UEInterpreter.AsVector3(args[0]), UEInterpreter.AsFloat(args[1])));
            Pair(215, "Add_VectorVector", (interp, frame, args) =>        // A+B
                X87.Add(UEInterpreter.AsVector3(args[0]), UEInterpreter.AsVector3(args[1])));
            Pair(216, "Subtract_VectorVector", (interp, frame, args) =>   // A-B
                X87.Sub(UEInterpreter.AsVector3(args[0]), UEInterpreter.AsVector3(args[1])));
            // execEqualEqual_VectorVector / execNotEqual_VectorVector (Core.dll.c:12350 /
            // 36660) — component-wise EXACT float comparison via x87 fcomp
            // (X87Equal: a NaN component compares equal; 0x1013be60). Unity's
            // Vector3 operators are approximate (~1e-5 epsilon),
            // which turned sub-milliunit residuals (Harry.uc:910 `Acceleration ==
            // vect(0,0,0)`) into false equality. Same fix as VectorToBool above.
            Pair(217, "EqualEqual_VectorVector", (interp, frame, args) => { // A==B
                var a = UEInterpreter.AsVector3(args[0]); var b = UEInterpreter.AsVector3(args[1]);
                return X87Equal(a.x, b.x) && X87Equal(a.y, b.y) && X87Equal(a.z, b.z);
            });
            Pair(218, "NotEqual_VectorVector", (interp, frame, args) => {   // A!=B
                var a = UEInterpreter.AsVector3(args[0]); var b = UEInterpreter.AsVector3(args[1]);
                return !(X87Equal(a.x, b.x) && X87Equal(a.y, b.y) && X87Equal(a.z, b.z));
            });
            // Core.dll execDot_VectorVector (0x1013c09e): (Az·Bz + Ay·By) + Ax·Bx
            // in a 64-bit mantissa, fstp dword.
            Pair(219, "Dot_VectorVector", (interp, frame, args) =>        // dot
                X87.Dot(UEInterpreter.AsVector3(args[0]), UEInterpreter.AsVector3(args[1])));
            // execGreaterGreater_VectorRotator / execLessLess_VectorRotator
            // (Core.dll.c:24784 / 18270) — pure vector⊗rotator transforms (moved here
            // from the Unity-backed transform group so the headless core gets
            // them too; UEValue.Rotate/UnRotate is a 1:1 Coords::Rotation port).
            // `V >> R` transforms V from R's local frame to world; `V << R` the
            // inverse (world → local). Used by camera-target placement.
            Pair(276, "GreaterGreater_VectorRotator", (interp, frame, args) =>  // V >> R
                UEValue.RotateVector(UEInterpreter.AsVector3(args[0]), UEInterpreter.AsRotator(args[1])));
            Pair(275, "LessLess_VectorRotator", (interp, frame, args) =>        // V << R
                UEValue.UnRotateVector(UEInterpreter.AsVector3(args[0]), UEInterpreter.AsRotator(args[1])));
            // Core.dll execCross_VectorVector (0x1013c16e): each component is the
            // difference of two exact products in a 64-bit mantissa, fstp dword.
            Pair(220, "Cross_VectorVector", (interp, frame, args) =>      // cross
                X87.Cross(UEInterpreter.AsVector3(args[0]), UEInterpreter.AsVector3(args[1])));
            // execMultiply_VectorVector (Core.dll.c:59951) — COMPONENT-WISE A*B (vec3*vec3
            // is component-wise, NOT the dot product). Unity has no operator* for two
            // Vector3, so Vector3.Scale.
            Pair(296, "Multiply_VectorVector", (interp, frame, args) =>   // A*B (per-component)
                X87.Mul(UEInterpreter.AsVector3(args[0]), UEInterpreter.AsVector3(args[1])));
            // Core.dll execVSize (0x1013c763): (Z²+Y²)+X² in a 64-bit mantissa,
            // fstp qword, appSqrt (fsqrt, PC64), fstp dword.
            Pair(225, "VSize", (interp, frame, args) =>
                X87.VSize(UEInterpreter.AsVector3(args[0])));
            // Normal (226, execNormal 0x1013c810: one call of FVector::SafeNormal) — X87.SafeNormal, PC64 or PC24 by
            // the render window; checked by validate_core_math_natives.py in both precisions.
            Pair(226, "Normal", (interp, frame, args) => X87.SafeNormal(UEInterpreter.AsVector3(args[0])));
            // UObject::execNormalize (Core.dll 0x1013e320, from disasm): each
            // rotator component (Pitch, Yaw, Roll) is `& 0xffff`, and values above 0x7fff
            // are reduced by 0x10000, i.e. wrapped into [−32768, 32767]. Called
            // by name (HPBase.spellVoldemortTracking.Tick, the Hub5 final fight).
            Pair(0, "Normalize", (interp, frame, args) => {
                UERotator r = UEInterpreter.AsRotator(args[0]);
                static int Wrap(int c) { int w = c & 0xffff; return w > 0x7fff ? w - 0x10000 : w; }
                return new UERotator(Wrap(r.Pitch), Wrap(r.Yaw), Wrap(r.Roll));
            });
            // Core.dll.c:63839 UObject::execVRand — reject-sample a cube, `while (1.0 <
            // x²+y²+z²)` keeps points INSIDE the unit ball, then normalize → uniform on the
            // sphere. Components are raw UE space (just a direction).
            Pair(252, "VRand", (interp, frame, args) => VRandBody(_random));
            // VSize2D (native 271) — no execVSize2D body found by name in the Core/Engine
            // decompiles; not verified. Length of the horizontal (XY) components.
            // Our Vector3 holds raw UE (X,Y,Z); UE's 2D plane is XY (Z is up).
            Pair(271, "VSize2D", (interp, frame, args) => {
                var v = UEInterpreter.AsVector3(args[0]);
                return (float)Math.Sqrt(v.x * v.x + v.y * v.y);
            });
            // Core.dll execMirrorVectorByNormal (0x1013cd2e): N = SafeNormal(Normal)
            // (not assumed to be unit length), see X87.Mirror.
            Pair(300, "MirrorVectorByNormal", (interp, frame, args) =>
                X87.Mirror(UEInterpreter.AsVector3(args[0]), UEInterpreter.AsVector3(args[1])));

            // execGetAxes (Core.dll.c:53242 / 0x1013d940): GetAxes(Rotation, out X, out Y, out Z) — the rotation
            // frame's 3 world-space axis vectors (Coords::Rotation(R).GetAxes); X is
            // forward. UEValue.GetAxes is the 1:1 Coords::Rotation port. X/Y/Z are
            // `out` params (Object.uc) → flow back via the native out-arg writeback.
            Pair(229, "GetAxes", (interp, frame, args) => {
                var (X, Y, Z) = UEValue.GetAxes(UEInterpreter.AsRotator(args[0]));
                if (args.Length > 1) args[1] = X;
                if (args.Length > 2) args[2] = Y;
                if (args.Length > 3) args[3] = Z;
                return null;
            });

            // ── Vector in-place (arg 0 OutParm) ───────────────────────────
            Pair(221, "MultiplyEqual_VectorFloat", (interp, frame, args) => {
                var v = X87.Mul(UEInterpreter.AsVector3(args[0]), UEInterpreter.AsFloat(args[1]));
                args[0] = v; return v;
            });
            Pair(222, "DivideEqual_VectorFloat", (interp, frame, args) => {
                var v = X87.DivVectorFloat(UEInterpreter.AsVector3(args[0]), UEInterpreter.AsFloat(args[1]));
                args[0] = v; return v;
            });
            Pair(223, "AddEqual_VectorVector", (interp, frame, args) => {
                var v = UEInterpreter.AsVector3(args[0]) + UEInterpreter.AsVector3(args[1]);
                args[0] = v; return v;
            });
            Pair(224, "SubtractEqual_VectorVector", (interp, frame, args) => {
                var v = UEInterpreter.AsVector3(args[0]) - UEInterpreter.AsVector3(args[1]);
                args[0] = v; return v;
            });
            // execMultiplyEqual_VectorVector (Core.dll.c:38213 / 0x1013c340) — COMPONENT-WISE `A *= B`
            // (Hadamard, in-place). Tut1: Harry.uc:1555 `Acceleration *= vect(0,0,1)`
            // in state Hit.Begin zeroes the knockback X/Y accel, keeps Z (live on a
            // Peeves hit). Was native idx=297 unimplemented → Accel X/Y not cleared.
            Pair(297, "MultiplyEqual_VectorVector", (interp, frame, args) => {
                var v = Vector3.Scale(UEInterpreter.AsVector3(args[0]), UEInterpreter.AsVector3(args[1]));
                args[0] = v; return v;
            });

            // ── Rotator arithmetic (component-wise int math) ──────────────
            Pair(316, "Add_RotatorRotator", (interp, frame, args) =>
                RotAdd(UEInterpreter.AsRotator(args[0]), UEInterpreter.AsRotator(args[1]), 1));
            Pair(317, "Subtract_RotatorRotator", (interp, frame, args) =>
                RotAdd(UEInterpreter.AsRotator(args[0]), UEInterpreter.AsRotator(args[1]), -1));
            // execMultiply_RotatorFloat (Core.dll.c:29004 / 0x1013d020) /
            // execMultiply_FloatRotator (:23487)
            // / :289 Divide_RotatorFloat — scale each component.
            Pair(287, "Multiply_RotatorFloat", (interp, frame, args) =>
                RotScale(UEInterpreter.AsRotator(args[0]), UEInterpreter.AsFloat(args[1])));
            Pair(288, "Multiply_FloatRotator", (interp, frame, args) =>
                RotScale(UEInterpreter.AsRotator(args[1]), UEInterpreter.AsFloat(args[0])));
            // Shipped 0x1013d220 — multiplication by the float reciprocal with fistp to nearest
            // (see RotDiv); direct division with truncation (SE rotator.h) differs from
            // shipped. B==0 → ±inf/NaN → 0x80000000.
            Pair(289, "Divide_RotatorFloat", (interp, frame, args) =>
                RotDiv(UEInterpreter.AsRotator(args[0]), UEInterpreter.AsFloat(args[1])));
            // Shipped Core.dll compares rotator components as RAW 32-bit ints — disasm of
            // execEqualEqual_RotatorRotator (body 0x1013ce40, export thunk RVA 0x2ced):
            // three plain `cmpl` on Pitch/Yaw/Roll, no movzx/and-0xFFFF/cmpw anywhere;
            // execNotEqual (0x1013cf30) identical inverted. Same as UT-public FRotator.
            // SE's rotator.h:88-89 uint16 casts (0 == 65536) are SE's own deviation —
            // native wins by source priority (the shipped engine says 0 != 65536).
            Pair(142, "EqualEqual_RotatorRotator", (interp, frame, args) => {
                var a = UEInterpreter.AsRotator(args[0]); var b = UEInterpreter.AsRotator(args[1]);
                return a.Pitch == b.Pitch && a.Yaw == b.Yaw && a.Roll == b.Roll;
            });
            Pair(203, "NotEqual_RotatorRotator", (interp, frame, args) => {
                var a = UEInterpreter.AsRotator(args[0]); var b = UEInterpreter.AsRotator(args[1]);
                return a.Pitch != b.Pitch || a.Yaw != b.Yaw || a.Roll != b.Roll;
            });
            // ── Rotator in-place (arg 0 OutParm) ──────────────────────────
            Pair(318, "AddEqual_RotatorRotator", (interp, frame, args) => {
                var v = RotAdd(UEInterpreter.AsRotator(args[0]), UEInterpreter.AsRotator(args[1]), 1);
                args[0] = v; return v;
            });
            Pair(319, "SubtractEqual_RotatorRotator", (interp, frame, args) => {
                var v = RotAdd(UEInterpreter.AsRotator(args[0]), UEInterpreter.AsRotator(args[1]), -1);
                args[0] = v; return v;
            });
            Pair(290, "MultiplyEqual_RotatorFloat", (interp, frame, args) => {
                var v = RotScale(UEInterpreter.AsRotator(args[0]), UEInterpreter.AsFloat(args[1]));
                args[0] = v; return v;
            });
            Pair(291, "DivideEqual_RotatorFloat", (interp, frame, args) => {
                var v = RotDiv(UEInterpreter.AsRotator(args[0]), UEInterpreter.AsFloat(args[1]));
                args[0] = v; return v;
            });
            // Shipped Core.dll.c:57626-57634 — yaw consumes the first appRand,
            // pitch the second, and roll the optional third. Each component is
            // `(rand()<<1) % 0xffff`, hence the exact even range [0,65534].
            Pair(320, "RotRand", (interp, frame, args) => {
                bool bRoll = args.Length > 0 && args[0] != null && UEInterpreter.ToBool(args[0]);
                int yaw   = (NextRandomInt15() << 1) % 0xffff;
                int pitch = (NextRandomInt15() << 1) % 0xffff;
                int roll  = bRoll ? (NextRandomInt15() << 1) % 0xffff : 0;
                return new UERotator(pitch, yaw, roll);
            });
        }

        // Object and name operators — bodies of UObject::exec* in Core.dll: object identity / name equality /
        // class hierarchy queries / state-membership tests. No state-mutating
        // natives here (GotoState/Enable/Disable/Spawn/Destroy land with the
        // state-machine commit alongside their bridge requirements).
        // Pure VM type/state intrinsics (reference equality, IsA, state name) —
        // safe for the headless core. SaveConfig is Core-owned
        // (RegisterLocalizationOps); DynamicLoadObject stays per frontend.
        protected void RegisterTypeOpsPure()
        {
            // execEqualEqual_ObjectObject (Core.dll.c:36331 / 0x101407a0) — `A == B` (reference)
            Pair(114, "EqualEqual_ObjectObject", (interp, frame, args) =>
                ObjectRefEquals(args[0], args[1]));
            // execNotEqual_ObjectObject (Core.dll.c:36516 / 0x10140870) — `A != B`
            Pair(119, "NotEqual_ObjectObject", (interp, frame, args) =>
                !ObjectRefEquals(args[0], args[1]));

            // execEqualEqual_NameName (Core.dll.c:9281 / 0x10140600) — `A == B`. UEName wraps
            // UELib's UName; compare by Value (case-insensitive per UE1).
            Pair(254, "EqualEqual_NameName", (interp, frame, args) =>
                NameEquals(args[0], args[1]));
            // execNotEqual_NameName (Core.dll.c:26831 / 0x101406d0) — `A != B`
            Pair(255, "NotEqual_NameName", (interp, frame, args) =>
                !NameEquals(args[0], args[1]));

            // execIsA (Core.dll.c:44651) — `Self->IsA(ClassName)`. Delegates to the
            // shared name-based walk (which stitches cross-package import
            // stubs via CanonicalSuper).
            Pair(303, "IsA", (interp, frame, args) => {
                string nm = NameOf(args[0]);
                if (string.IsNullOrEmpty(nm)) return false;
                var self = frame.Context ?? frame.Object;
                return UEInterpreter.ClassIsA(self?.Class, nm);
            });

            // AActor::execIsOSVer2kOrXP (Engine.dll.c:16482) — a wrapper with no
            // logic of its own: it calls IsOSVer2kOrXP (Core.dll.c:14279) and
            // returns the bool. The body is ONE predicate over the HOST OS version:
            //     GetVersionEx(&vi);
            //     return vi.dwPlatformId == 2 && vi.dwMajorVersion > 4;
            // where 2 = VER_PLATFORM_WIN32_NT, i.e. "NT family, version
            // 5.0 and up" — Windows 2000/XP VERSUS Windows 9x/ME and NT4.
            //
            // The only caller is FEOptionsPage.IsSupportedResolution
            // (:340): when true the resolution list loses 512x384. The answer is
            // visible to the player, so the native must be registered.
            //
            // WHAT IS ASSUMED AND WHAT IS FROM SOURCE. The predicate is ported
            // verbatim and evaluated against the LIVE OS, as in shipped. On a non-Windows
            // host dwPlatformId has no equivalent: there the answer true is a DECLARED
            // assumption. Reason: this predicate gives false EXACTLY on
            // Win9x/ME/NT4, and no such system runs this port; on every host where the
            // port runs at all (including Windows 10/11 — platformId=2, major=10)
            // shipped would return true.
            Pair(327, "IsOSVer2kOrXP", (interp, frame, args) =>
                IsOSVer2kOrXPFor(Environment.OSVersion.Platform,
                                 Environment.OSVersion.Version.Major));

            // execClassIsChildOf (Core 0x10141b50..0x10141bc6): NULL -> false, then
            // a walk of the test class's SuperField comparing POINTERS
            // (0x10141bb4 cmp). Import stubs resolve to the real class of
            // THE SAME package (the linker binds them before the call); matching by
            // name would confuse same-named classes of different packages (HPModels/HProps).
            Pair(258, "ClassIsChildOf", (interp, frame, args) => {
                return UEClassRegistry.Shared.IsChildOfClass(AsClass(args[0]), AsClass(args[1]));
            });

            // execIsInState (Core.dll 0x10142cd0..0x10142d3c): the frame's state
            // node (StateFrame+0x1c) and on up SuperField (+0x28) until the name
            // matches. The node of an object without a state is its CLASS (GotoState sets
            // Class when not found/None), so the chain walks class
            // names; a state node walks ancestor states (`extends` and
            // same-named states of parent classes).
            Pair(281, "IsInState", (interp, frame, args) => {
                var self = frame.Context ?? frame.Object;
                string target = NameOf(args[0]);
                if (self?.Class is null || string.IsNullOrEmpty(target)) return false;
                foreach (string n in StateNodeChain(interp, self))
                    if (string.Equals(n, target, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            });

            // execGetStateName (Core.dll 0x10142da0..0x10142de3): name of the frame's
            // state node; for an object without a state the node is the class, so
            // the answer is the CLASS NAME (not 'None').
            Pair(284, "GetStateName", (interp, frame, args) => {
                var self = frame.Context ?? frame.Object;
                if (self?.Class is null) return new UEName("None");
                string s = self.CurrentStateName;
                return new UEName(string.IsNullOrEmpty(s) ? self.Class.Name.ToString() : s);
            });

        }

        // State machine natives — bodies of UObject::exec* in Core.dll (execGotoState
        // Core.dll.c:33347, execDisable :31010, execEnable :54226). These
        // operate on the *current Context* object (the implicit Self for the
        // call): in shipped this is `this`, the object the native runs on.
        // Pure VM state intrinsics (GotoState/Enable/Disable) — safe for the
        // headless core. The decal natives live in RegisterStateOpsUnity.
        // Shipped UObject::execLocalize (Core.dll.c:10463 → worker :11150) —
        // reads the original .int files through the Core UELocalization port
        // (semantics + literals documented there). ONE body for BOTH
        // frontends: they register this same group.
        //
        // Also here — the other config/localisation natives whose body is entirely
        // Core-side:
        //   GetLanguage (UObject::execGetLanguage, Core.dll.c:49839) — a copy of
        //     GLanguage (UELocalization.Language), which appInit takes from
        //     `[Engine.Engine] Language`, not a literal;
        //   SaveConfig (536, UObject::execSaveConfig, Core.dll.c:5999) —
        //     SaveConfig(this, CPF_Config, NULL) over the process GConfig;
        //   SaveGameSaveInfo/LoadGameSaveInfo (325/326, Engine.dll.c:138213 /
        //     121710) — the file format is Core-side; the frontend only supplies the DIRECTORY
        //     (GSys->SavePath — the platform boundary).
        protected void RegisterLocalizationOps(Func<string> saveDir)
        {
            Pair(0, "Localize", (interp, frame, args) =>
                UELocalization.Localize(
                    Str(args.Length > 0 ? args[0] : null),
                    Str(args.Length > 1 ? args[1] : null),
                    Str(args.Length > 2 ? args[2] : null)));
            Pair(0, "GetLanguage", (interp, frame, args) => UELocalization.Language);
            Pair(536, "SaveConfig", (interp, frame, args) =>
            {
                UEObjectConfig.SaveConfig(UEConfig.Global, frame.Context ?? frame.Object, interp);
                return null;
            });
            Pair(325, "SaveGameSaveInfo", (interp, frame, args) =>
                UELevelRuntime.WriteGameSaveInfo(
                    UELevelRuntime.GameSaveInfoPath(saveDir(), Str(args.Length > 0 ? args[0] : null)),
                    args.Length > 1 ? args[1] as UEObject : null));
            Pair(326, "LoadGameSaveInfo", (interp, frame, args) =>
                UELevelRuntime.LoadGameSaveInfo(saveDir(), Str(args.Length > 0 ? args[0] : null),
                    args.Length > 1 ? args[1] as UEObject : null));
            // Slot thumbnails: AActor::execCreateTextureFromBMP (Engine.dll
            // 0x10413630) -> StaticImportObject -> UEngineTextureFactory. The body is
            // Core (UETextureImport), pixels go to the UERuntimeTextures registry. The file
            // name is relative to shipped's working directory — the install's System,
            // the same one appInit loaded GConfig from.
            Pair(321, "CreateTextureFromBMP", (interp, frame, args) =>
                UETextureImport.CreateTextureFromBMP(interp,
                    Str(args.Length > 0 ? args[0] : null), Str(args.Length > 1 ? args[1] : null),
                    UEConfig.Global?.InstallSystemDir
                        ?? throw new InvalidOperationException(
                            "CreateTextureFromBMP: process GConfig not initialized (appInit) — the System working directory is unknown"),
                    HP1.Core.UETrace.Info));
        }

        // ── GetEntryLevel — THE ENTRY LEVEL, NOT THE CURRENT ONE ──────────
        //
        // Shipped APlayerPawn::execGetEntryLevel (Engine.dll.c:122381) reads the
        // CHAIN `XLevel->Engine->EntryLevel->Actors(0)` (UGameEngine+0x74) and
        // asserts that Actors(0) is an ALevelInfo. That is the LevelInfo of the SEPARATE,
        // permanently alive Entry level — loaded by UGameEngine::Init
        // (Engine.dll.c:38221: FURL(L"Entry")), not `[URL] LocalMap`.
        // It has its own TimeSeconds (window timeouts use it,
        // HPMessageBox.uc:77,81).
        //
        // The Entry world is loaded in Core (UELevelRuntime.Entry.cs); the frontend only
        // supplies a reference to the session's main runtime. No Entry is a shipped
        // assert, and it stays loud here.
        //
        // The native is declared WITHOUT an index (measured: FunctionFlags.Native, NativeToken
        // 0), so it is dispatched by name — one registration for both
        // frontends, as with Localize.
        protected void RegisterEntryLevelOps(Func<UELevelRuntime> sessionRuntime)
        {
            Pair(0, "GetEntryLevel", (interp, frame, args) =>
            {
                UELevelRuntime entry = sessionRuntime?.Invoke()?.EntryWorld
                    ?? throw new InvalidOperationException(
                        "GetEntryLevel: ((UGameEngine*)XLevel->Engine)->EntryLevel == NULL "
                        + "(Engine.dll.c:122408) — the Entry world is not up in this session");
                UEObject a0 = entry.Actors.Count > 0 ? entry.Actors[0] : null;
                if (a0 is null || !UEInterpreter.ClassIsA(a0.Class, "LevelInfo"))
                    throw new InvalidOperationException(
                        "GetEntryLevel: Actors(0)->IsA(ALevelInfo) (UnLevel.h:0xf8) violated for the Entry world");
                return a0;
            });
        }

        // Timer, Sleep and the level pawn list — Core-owned (one body for all frontends).
        // shipped APawn::execRemovePawn (Engine.dll.c:40035, disasm 0x103d6d40..
        // 0x103d6e30) writes only PawnList/the chain link; the removed pawn's nextPawn is kept.
        protected void RegisterActorTimerAndPawnListOps()
        {
            // Sleep (256, latent): the counter is TickStateFrame (LatentRunState.Sleep).
            Pair(256, "Sleep", (interp, frame, args) =>
            {
                frame.LatentState = LatentRunState.Sleep;
                frame.SleepTimeLeft = UEInterpreter.AsFloat(args.Length > 0 ? args[0] : null);
                return null;
            });
            // SetTimer (280): AActor::execSetTimer (disasm 0x1040cc70..0x1040ccee) —
            // TimerCounter (+0x48) = 0, TimerRate (+0x44) = the argument AS-IS (no
            // clamp: rate <= 0 does not arm the timer because the tick checks
            // 0 < TimerRate), bTimerLoop bit 0x4000 = bLoop & 1.
            Pair(280, "SetTimer", (interp, frame, args) =>
            {
                UEObject self = frame.Context ?? frame.Object;
                if (self is null) return null;
                float rate = UEInterpreter.AsFloat(args.Length > 0 ? args[0] : null);
                self.TimerRate = rate;
                self.TimerCounter = 0f;
                self.TimerLoop = args.Length > 1 && UEInterpreter.ToBool(args[1]);
                return null;
            });
            // AddPawn (529): execAddPawn (Engine.dll.c:46972) — nextPawn =
            // LevelInfo.PawnList; LevelInfo.PawnList = Self.
            Pair(529, "AddPawn", (interp, frame, args) =>
            {
                UEObject pawn = frame.Context ?? frame.Object;
                if (pawn is null) return null;
                UEObject level = interp.GetLevelInfo();
                pawn.Props["nextPawn"] = level.GetProp("PawnList");
                level.Props["PawnList"] = pawn;
                return null;
            });
            // RemovePawn (530): execRemovePawn (Engine.dll.c:40035) — unlink Self
            // from the nextPawn chain; Self's own nextPawn is not touched.
            Pair(530, "RemovePawn", (interp, frame, args) =>
            {
                UEObject self = frame.Context ?? frame.Object;
                if (self is null) return null;
                UEObject level = interp.GetLevelInfo();
                if (ReferenceEquals(level.GetProp("PawnList") as UEObject, self))
                {
                    level.Props["PawnList"] = self.GetProp("nextPawn");
                    return null;
                }
                for (UEObject p = level.GetProp("PawnList") as UEObject; p is not null; p = p.GetProp("nextPawn") as UEObject)
                    if (ReferenceEquals(p.GetProp("nextPawn") as UEObject, self))
                    {
                        p.Props["nextPawn"] = self.GetProp("nextPawn");
                        return null;
                    }
                return null;
            });
        }

        protected void RegisterStateOpsPure()
        {
            // execGotoState (Core.dll 0x10141cd0; UT UnScript.cpp form): the state name
            // defaults to the current one; UObject::GotoState is called
            // ONLY for a different name (otherwise no End/BeginState and no mask
            // recompute — Disable() survives GotoState into the same state); on
            // success — GotoLabel(L == None ? Begin : L).
            Pair(113, "GotoState", (interp, frame, args) => {
                var self = frame.Context ?? frame.Object;
                string current = self?.CurrentStateName;
                string state = NameOf(args.Length > 0 ? args[0] : null);
                string label = NameOf(args.Length > 1 ? args[1] : null);
                if (string.IsNullOrEmpty(state) || string.Equals(state, "None", StringComparison.OrdinalIgnoreCase))
                    state = args.Length > 0 && args[0] is not null ? null : current;
                var result = UEInterpreter.GotoStateResult.Success;
                if (!string.Equals(state ?? "", current ?? "", StringComparison.OrdinalIgnoreCase))
                    result = interp.GotoStateCore(self, state, label);
                if (result == UEInterpreter.GotoStateResult.Success)
                    interp.GotoLabel(self, label);
                return null;
            });

            // Pawn.StopWaiting — BREAKS A LATENT SLEEP.
            //
            // Shipped APawn::execStopWaiting (Engine.dll.c:50959):
            //     if (StateFrame->LatentAction == 0x180) LatentFloat = -1.0f;
            // Code 0x180 (384) is the latent action `Sleep`: set by
            // AActor::execSleep (Engine.dll.c:124854), which also puts the seconds into
            // LatentFloat (+0xcc, verified by the offset oracle). So the native
            // means "if the pawn is asleep now, finish sleeping immediately"; for
            // any other latent (FinishAnim, MoveTo, TurnTo…) it does
            // nothing.
            //
            // Mapping to the port model: the remaining sleep lives in
            // UEFrame.SleepTimeLeft, the poll (UELevelRuntime.StateFrame) is
            // execPollSleep with a 0.5·dt threshold, so breaking writes the same -1.0.
            //
            // ONE body for both frontends: in Tut2 (BroomHarry) this native is live.
            Pair(0, "StopWaiting", (interp, frame, args) => {
                var self = frame.Context ?? frame.Object;
                UEFrame sf = self?.StateFrame;
                if (sf is not null && sf.LatentState == LatentRunState.Sleep)
                    sf.SleepTimeLeft = -1f;
                return null;
            });

            // Enable/Disable change ONE BIT of the state frame's probe mask —
            // exactly like shipped execEnable/execDisable (Core.dll.c). For a name
            // OUTSIDE the probe range both natives do NOTHING: the check against
            // 0xc4/0x105 precedes any mask work.
            //
            // The mask is the only store (D-20); a separate per-name disable table is
            // blocked by verify_architecture.py ("dual store").

            // execEnable: the bit is restored ONLY if the class or state actually
            // defines it and it is not in ignores — the mask is recomputed with the
            // same formula as in GotoState.
            Pair(117, "Enable", (interp, frame, args) => {
                var self = frame.Context ?? frame.Object;
                string probe = NameOf(args.Length > 0 ? args[0] : null);
                if (self?.StateFrame is UEFrame sf && UEProbe.TryBit(probe, out int onBit))
                    sf.ProbeMask |= (1UL << onBit)
                        & UEProbe.MaskFor(self.Class, sf.Func as UELib.Core.UState);
                return null;
            });

            // execDisable: the bit is simply cleared.
            Pair(118, "Disable", (interp, frame, args) => {
                var self = frame.Context ?? frame.Object;
                string probe = NameOf(args.Length > 0 ? args[0] : null);
                if (self?.StateFrame is UEFrame sf && UEProbe.TryBit(probe, out int offBit))
                    sf.ProbeMask &= ~(1UL << offBit);
                return null;
            });

        }

        // DynamicLoadObject RESOLUTION — ONE BODY FOR BOTH FRONTENDS.
        //
        // Shipped execDynamicLoadObject (Core.dll.c:63931) is
        // StaticLoadObject (Core.dll.c:48076): the path is parsed by ResolveName
        // (groups are CREATED, Create=1), and the object itself comes from ULinkerLoad::Create
        // -> FindExportIndex(class name, class package, NAME, OuterIndex=-1):
        // the path's group is NOT COMPARED (disasm 0x1014a1cb..0x1014a1d8). Only
        // if the linker finds nothing — StaticFindObject by exact outer. Measured on the
        // live original 1.53: BroomHoopStage4.PreBeginPlay asks for
        // "HP_BroomTraining.TrainingHoop.BTStage4" (there is no TrainingHoop group),
        // shipped returns HP_BroomTraining.HoopTextures.BTStage4.
        //
        // Three outcomes; the third is an admitted boundary, not an invention:
        //   1. found and IsA          → typed reference;
        //   2. package KNOWN to the registry, no object → None (as shipped);
        //   3. package found NEITHER by the registry NOR on disk → marker reference and
        //      ONE loud log (a resource package on disk without the object — None
        //      and shipped's "Failed to load" line, as outcome 2). The script registry loads only .u, but the campaign
        //      also asks for resource packages (Lev_Tut2: FireTexture from
        //      HP_BroomTraining.utx). Silently returning None here would
        //      erase a resource the original loads, and silently
        //      returning a marker would lie that the check passed.
        public static object ResolveDynamicLoad(string path, string requestedClass,
            Action<string> warnOnce)
        {
            if (string.IsNullOrEmpty(path)) return null;
            int dot = path.LastIndexOf('.');
            string leaf = dot >= 0 ? path.Substring(dot + 1) : path;
            UELib.Core.UObject found = UEClassRegistry.Shared?.FindExport(path);
            if (found is not null)
            {
                // UELib does not fill Class for a CLASS export (its class is
                // Core.Class itself), so a naive check rejects every class as "of the wrong
                // class": DynamicLoadObject(..., Class'Core.Class') would always give None.
                string foundClass = found is UELib.Core.UClass ? "Class" : found.Class?.Name?.ToString();
                bool isA = foundClass is not null && (requestedClass is null
                    || string.Equals(foundClass, requestedClass, StringComparison.OrdinalIgnoreCase)
                    || UEInterpreter.ClassIsA(UEClassRegistry.Shared.Find(foundClass), requestedClass));
                if (!isA) return null;
                // A CLASS object is returned ITSELF (StaticLoadObject returns a UClass*):
                // `new (none) Class<UWindowRootWindow>(DynamicLoadObject(RootWindow,
                // Class'Core.Class'))` (WindowConsole.CreateRootWindow) metacasts it
                // and instantiates it.
                if (found is UELib.Core.UClass foundType) return foundType;
                return new UEResourceRef(foundClass, leaf, path);
            }
            string pkg = dot >= 0 ? path.Substring(0, path.IndexOf('.')) : path;
            if (UEClassRegistry.Shared is not null && UEClassRegistry.Shared.KnowsPackage(pkg))
            {
                // FindExportIndex(..., -1): name + EXACT class, any group.
                var hits = requestedClass is null ? new System.Collections.Generic.List<UELib.Core.UObject>()
                    : UEClassRegistry.Shared.FindExportsByNameAndClass(pkg, leaf, requestedClass);
                // Several matches: FindExportIndex returns the first in the hash
                // chain, and the chain is built by inserting at the head in ascending
                // index order (Core.dll.c:29644-29688; walk 17718-17755) — i.e. the
                // export with the LARGEST index. The list is in table order.
                if (hits.Count > 0)
                {
                    var hit = hits[hits.Count - 1];
                    string hitPath = pkg + "." + hit.GetPath();
                    return new UEResourceRef(requestedClass, leaf, hitPath);
                }
                return null;                       // package exists, object does not — None
            }
            // A resource package outside the script registry — the same linker rule over its
            // export table from disk.
            var resHits = requestedClass is null ? new System.Collections.Generic.List<string>()
                : UEClassRegistry.Shared?.FindResourceExportPaths(pkg, leaf, requestedClass)
                  ?? new System.Collections.Generic.List<string>();
            if (resHits.Count > 0) return new UEResourceRef(requestedClass, leaf, resHits[resHits.Count - 1]);
            // The package is on disk but its table lacks the object: shipped
            // StaticLoadObject (Core.dll.c:48076) finds the export neither by the linker
            // nor by StaticFindObject and throws ObjectNotFound (appThrowf, :48140);
            // the caller gets None, the log gets a line with the OUTERMOST
            // package path (loop to outermost, :48119). Live original 1.53, story
            // book: "Failed to load 'Texture StoryBookTest.CommonRoom001'" —
            // FEStoryBookPage asks for textures absent from StoryBookTest.utx.
            if (requestedClass is not null && UEClassRegistry.Shared is not null
                && UEClassRegistry.Shared.HasResourcePackage(pkg))
            {
                string shown = $"{requestedClass} {pkg}.{leaf}";
                UETrace.Info($"Failed to load '{shown}': Failed to find object '{shown}'");
                return null;
            }
            // The package is neither in the registry nor on disk (the port's search covers all Paths directories:
            // System, Maps, Textures, Sounds, Music): shipped StaticLoadObject -> LoadPackage does not find the file
            // -> None. (A marker reference would be true under `!= None`.)
            warnOnce?.Invoke(pkg);
            return null;
        }

        protected static bool NameEquals(object a, object b)
        {
            // UE1 names: the empty/unset name IS 'None' (name table index 0) — there
            // is no distinct empty name. So normalise null/"" → "None" before compare.
            // Without this, a Dispatcher OutEvent fed as "" passes its `!= 'None'`
            // guard and AllActors("") (no tag filter) Triggers EVERY actor — which
            // made migrated knights wobble perpetually.
            string sa = NameOf(a); if (string.IsNullOrEmpty(sa)) sa = "None";
            string sb = NameOf(b); if (string.IsNullOrEmpty(sb)) sb = "None";
            return string.Equals(sa, sb, StringComparison.OrdinalIgnoreCase);
        }

        protected static string NameOf(object v)
        {
            if (v == null) return null;
            if (v is UEName n) return n.Value;
            return v.ToString();
        }

        protected static UClass AsClass(object v)
        {
            if (v is UClass c) return c;
            string nm = NameOf(v);
            return string.IsNullOrEmpty(nm) ? null : UEClassRegistry.Shared.Find(nm);
        }

        // `A == B` for object refs. In UE1 a cross-package class literal
        // (Class'HPBase.spellFlip' compiled into Tut1.u) RESOLVES at package
        // load to the same UClass object the spawned actor's .Class points at —
        // our loader keeps the import stub and the canonical export as separate
        // instances, so plain ReferenceEquals breaks every
        // `X.Class == Class'Pkg.Y'` comparison across packages (found via
        // tut1Peeves.TakeSpellEffect never matching spellFlip). Class names are
        // globally unique in UE1, so name equality IS class identity.
        protected static bool ObjectRefEquals(object a, object b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a is UClass ca && b is UClass cb)
                return string.Equals(ca.Name?.ToString(), cb.Name?.ToString(),
                                     StringComparison.OrdinalIgnoreCase);
            // Resource: one UE1 object = one full path (Outer chain + FName,
            // case-insensitive), while port references are distinct instances (the default and
            // the literal `Mesh == SkeletalMesh'HPModels.skdevilronMesh'`,
            // Hog2.SausageRollKidOnAStick). A texture embedded in the map has no path
            // ("export:Leaf", UEBspCollision) — only IdentityKey decides it.
            if (a is UEResourceRef ra && b is UEResourceRef rb)
                return ra.IdentityKey is not null && rb.IdentityKey is not null
                    ? ra.IdentityKey == rb.IdentityKey
                    : ra.ObjectPath is not null && !ra.ObjectPath.StartsWith("export:", StringComparison.Ordinal)
                      && string.Equals(ra.ObjectPath, rb.ObjectPath, StringComparison.OrdinalIgnoreCase);
            return false;
        }

        // The IsOSVer2kOrXP predicate (Core.dll.c:14279), split out so that a
        // fixture can drive it with a TABLE of inputs rather than the single value of
        // the host machine: `platformId == 2 && major > 4`.
        //
        // The Win32-to-PlatformID constant mapping is exact: VER_PLATFORM_WIN32_NT
        // (2) = Win32NT, VER_PLATFORM_WIN32_WINDOWS (1) = Win32Windows,
        // VER_PLATFORM_WIN32s (0) = Win32S. On all three the answer follows the
        // shipped predicate exactly. The `default` branch is the only place where
        // shipped has no equivalent (a non-Windows host), and it holds the
        // DECLARED assumption discussed at Pair(327).
        public static bool IsOSVer2kOrXPFor(PlatformID platform, int major)
        {
            switch (platform)
            {
                case PlatformID.Win32NT: return major > 4;
                case PlatformID.Win32Windows:
                case PlatformID.Win32S:
                case PlatformID.WinCE: return false;
                default: return true;
            }
        }

        protected virtual void Pair(int idx, string name, UENativeHandler handler)
        {
            if (idx > 0) _byIndex[idx] = handler;
            if (!string.IsNullOrEmpty(name)) _byName[name] = handler;
            if (idx > 0 && !string.IsNullOrEmpty(name)) _nameByIndex[idx] = name;
        }

        // EXACT REGISTRATION SNAPSHOT AND EXACT RESTORE.
        //
        // Restoring through the ordinary Pair is wrong: headless Pair WRAPS the handler
        // with journaling, so the restored entry would no longer be the same one (a second
        // wrapper, a rewritten provider, a lost _nameByIndex). Here EXACTLY what was
        // captured is written straight into the tables.
        protected readonly struct NativeRegistration
        {
            public readonly bool HadIndex, HadName, HadNameOfIndex;
            public readonly UENativeHandler ByIndex, ByName;
            public readonly string NameOfIndex;
            public NativeRegistration(bool hadIndex, UENativeHandler byIndex,
                bool hadName, UENativeHandler byName,
                bool hadNameOfIndex, string nameOfIndex)
            {
                HadIndex = hadIndex; ByIndex = byIndex;
                HadName = hadName; ByName = byName;
                HadNameOfIndex = hadNameOfIndex; NameOfIndex = nameOfIndex;
            }
        }

        protected NativeRegistration SnapshotRegistration(int idx, string name)
        {
            UENativeHandler byIndex = null, byName = null;
            string nameOfIndex = null;
            bool hadIndex = false, hadName = false, hadNameOfIndex = false;
            if (idx > 0)
            {
                hadIndex = _byIndex.TryGetValue(idx, out byIndex);
                hadNameOfIndex = _nameByIndex.TryGetValue(idx, out nameOfIndex);
            }
            if (!string.IsNullOrEmpty(name))
                hadName = _byName.TryGetValue(name, out byName);
            return new NativeRegistration(hadIndex, byIndex, hadName, byName,
                hadNameOfIndex, nameOfIndex);
        }

        protected void RestoreRegistration(int idx, string name, in NativeRegistration snap)
        {
            if (idx > 0)
            {
                if (snap.HadIndex) _byIndex[idx] = snap.ByIndex; else _byIndex.Remove(idx);
                if (snap.HadNameOfIndex) _nameByIndex[idx] = snap.NameOfIndex;
                else _nameByIndex.Remove(idx);
            }
            if (!string.IsNullOrEmpty(name))
            {
                if (snap.HadName) _byName[name] = snap.ByName; else _byName.Remove(name);
            }
        }

        public UENativeHandler GetByIndex(int idx)
            => _byIndex.TryGetValue(idx, out var h) ? h : null;

        public UENativeHandler GetByName(string name)
            => !string.IsNullOrEmpty(name) && _byName.TryGetValue(name, out var h) ? h : null;

        // ── UNKNOWN NATIVE IS FATAL, AND THE DECISION IS ONE FOR BOTH FRONTENDS ──
        //
        // There used to be `UETrace.Error(...)` here with the comment «the Unity runtime
        // logs and continues (legacy behavior); the headless core overrides
        // this to THROW». A skipped native returns no value and the bytecode then computes
        // from garbage — that would be faking a working game, forbidden by the STOP rule.
        // So an unknown native throws in every frontend; the only legitimate override is
        // headless survey mode, which calls the base.
        public virtual void OnUnknownNative(int idx, string name, string context)
            => throw new NotSupportedException(
                $"native idx={idx} name='{name}' reached in {context} but has no handler; "
                + "implement it from source (PLAN_core: unknowns are loud, never silent no-ops)");

        // Diagnostics (HP1BridgeValidator coverage): registered name for a native
        // index (bytecode-side intrinsics only expose "__NFUN_<idx>__" stubs), and
        // the full registered-name set for the called-vs-registered session diff.
        readonly Dictionary<int, string> _nameByIndex = new();
        public string NameOfIndex(int idx)
            => _nameByIndex.TryGetValue(idx, out var n) ? n : null;

        // ── The shared WorldFrame producer + Canvas exec group ──────────────
        // (review #11 item 1): frame building is owned by ONE Core object;
        // every frontend sets FrameProducer at bootstrap and calls
        // RegisterCanvasOps so the UCanvas execs (Engine.u Canvas.uc natives
        // 464-470/480) dispatch into it. SetPos/SetOrigin/DrawIcon/DrawPattern
        // /DrawRect are SCRIPT in Canvas.uc and run as plain bytecode.
        public UEWorldFrameProducer FrameProducer;

        // One-call frontend wiring: sets the producer, binds the render-side
        // MSVCRT stream (shipped appFrand = rand()*C, C = 1/32767 — Core 0x1017ddb0; appRandRange =
        // a + rand() % (b-a) for b>a, else a — Core.dll.c:3578, modulo by the
        // DIFFERENCE, an off-by-one shipped quirk preserved exactly), and
        // registers the Canvas exec group.
        public void BindFrameProducer(UEWorldFrameProducer producer)
        {
            FrameProducer = producer;
            // SEPARATE PRESENTATION STREAM (rendering has its own random numbers). In shipped the
            // renderer pulls rand() from the shared MSVCRT stream: FGlobalRandoms::Tick
            // (256/512 appFrand per frame) and particle emission in DrawParticleSystem,
            // so which emitters reached the frame shifted game outcomes
            // (spawn variants, timers): turn the camera — a different roll.
            // Bit-exact stream matching is invisible to the player, while camera-dependent
            // gameplay is visible. The renderer draws from its own stream; formulas and
            // call counts in every body are unchanged.
            producer.Frand = _renderRandom.NextFloat01;
            producer.RandRange = (a, b) => b - a > 0 ? a + _renderRandom.NextInt15() % (b - a) : a;
            // appFrand(float,float) (Core 0x10126170; Engine.dll.c:23527 —
            // particle emission in DrawParticleSystem, 24-bit render precision):
            // rand()·C·(max−min) + min, one rand per call, always.
            producer.FrandRange = _renderRandom.NextFrandRangePc24;
            producer.RandInt = _renderRandom.NextInt15;
            RegisterCanvasOps();
        }

        protected virtual void RegisterCanvasOps()
        {
            Pair(466, "DrawTile", (interp, frame, args) => { FrameProducer.DrawTile(frame, args, clipped: false); return null; });
            Pair(468, "DrawTileClipped", (interp, frame, args) => { FrameProducer.DrawTile(frame, args, clipped: true); return null; });
            Pair(465, "DrawText", (interp, frame, args) =>
            {
                UEObject canvas = frame.Context ?? frame.Object;
                string text = args is { Length: > 0 } ? args[0]?.ToString() ?? "" : "";
                // Shipped execDrawText pre-sets the optional CR local to TRUE
                // between the two arg evals (same pattern as StopSound's slot).
                bool cr = args is { Length: > 1 } && args[1] is not null
                    ? UEValue.ToBool(args[1]) : true;
                FrameProducer.DrawText(canvas, text, cr);
                return null;
            });
            // execDrawTextClipped 0x1038a2f0: the optional bCheckHotKey is
            // pre-filled with 0 (0x1038a348 mov [ebp-0x14], 0).
            Pair(469, "DrawTextClipped", (interp, frame, args) =>
            {
                UEObject canvas = frame.Context ?? frame.Object;
                string text = args is { Length: > 0 } ? args[0]?.ToString() ?? "" : "";
                bool hotkey = args is { Length: > 1 } && args[1] is not null && UEValue.ToBool(args[1]);
                FrameProducer.DrawTextClipped(canvas, text, hotkey);
                return null;
            });
            Pair(470, "TextSize", (interp, frame, args) =>
            {
                UEObject canvas = frame.Context ?? frame.Object;
                string text = args is { Length: > 0 } ? args[0]?.ToString() ?? "" : "";
                // execTextSize 0x1038a640: ClipX=32767.0 for the duration of WrappedPrint.
                // Without a font the out parameters are NOT written (0x1038a6f5-0x1038a730):
                // the VM write-back restores their previous values.
                if (!FrameProducer.TextSize(canvas, text, out float xl, out float yl)) return null;
                if (args.Length > 1) args[1] = xl;
                if (args.Length > 2) args[2] = yl;
                return null;
            });
            Pair(464, "StrLen", (interp, frame, args) =>
            {
                UEObject canvas = frame.Context ?? frame.Object;
                string text = args is { Length: > 0 } ? args[0]?.ToString() ?? "" : "";
                // execStrLen 0x10389050: wrapping by the LIVE ClipX.
                (float xl, float yl) = FrameProducer.StrLen(canvas, text);
                if (args.Length > 1) args[1] = xl;
                if (args.Length > 2) args[2] = yl;
                return null;
            });
            // ── 467/471/480: LOUD BOUNDARIES, not stubs ──
            //
            // 471 DrawClippedActor and 480 DrawPortal: a bytecode census of all shipped .u
            // (audit/campaign_coverage.json) — 480 has NO calls, 471 has a
            // single one, UWindowWindow.DrawClippedActor, which itself has no
            // caller (neither final nor virtual). Unreachable.
            //
            // 467 DrawActor: callers are Inventory.RenderOverlays and
            // Weapon.RenderOverlays (Engine.u, decompile of 146 classes: Weapon.uc:209,
            // Inventory.uc:103 — also the search control; HPMenu.u mentions
            // DrawActor(MouseParticle) only in commented-out "test code").
            // They are called by PlayerPawn/Pawn.RenderOverlays, and the event from the tail of
            // DrawWorld goes to `bBehindView ? None : (ViewTarget ?? Actor)`.
            // UNREACHABILITY — by listing the writers:
            //  * bBehindView = false is written only by PlayerPawn.ViewPlayerNum/
            //    ViewPlayer/ViewSelf/BehindView (exec), ViewClass (on a miss),
            //    PlayerSpectating.AltFire, Dying.EndState and Spectator; game scripts never
            //    write bBehindView or ViewTarget = None;
            //  * the execs are unreachable: the HPConsole console opens only with
            //    bDebugMode (globalconfig, absent from shipped ini — false; the cheat
            //    HarryDebugModeOn and the settings key are cut — no developer console,
            //    author decision 2026-10-30, D-77), Tilde
            //    is unbound in DefUser.ini, no key calls
            //    View*/BehindView, the game's ConsoleCommand sends no such commands;
            //  * the game never enters PlayerSpectating (no GotoState in Engine.u or
            //    in scripts); Dying.EndState clears ViewTarget only for Carcass —
            //    the recipient stays the BaseCam camera (a Pawn without a Weapon);
            //  * ViewClass(BaseCam, true) from Harry.PostBeginPlay misses
            //    only without a camera on the map: measured by audit/frame_census.sh (38 maps,
            //    tick 300, view line) — everywhere bBehindView=True, ViewTarget is
            //    PotCam/BaseCam.
            // If the boundary is reached anyway: execDrawActor body (Engine.dll.c:
            // 125588) — WireFrame -> RendMap = 1 for the call; clear bHidden;
            // ClearZ; Render->DrawActor(Frame, A) = FDynamicSprite + FUN_10b30c70
            // + DrawActorSprite (Render.dll.c:3339-3372); bHidden = 1; restore
            // RendMap. The mesh branch of DrawActorSprite exists in Core
            // (RenderLevelPass.DrawMeshSprite), but URender::DrawActor is not ported,
            // and the port closes the frame (FinishMasterFrame) BEFORE RenderOverlays —
            // it has no frame for DrawActor at that point.
            Pair(467, "DrawActor", (interp, frame, args) =>
                throw new NotSupportedException("Canvas.DrawActor reached — not ported: execDrawActor (Engine.dll.c:125588) needs URender::DrawActor (not translated) on a live frame, while the port finishes the master frame before RenderOverlays; campaign census shows bBehindView=1 with a camera ViewTarget on all 38 maps, and every bBehindView=false writer is an exec command, PlayerSpectating or Dying.EndState"));
            Pair(471, "DrawClippedActor", (interp, frame, args) =>
                throw new NotSupportedException("Canvas.DrawClippedActor reached — not ported; the campaign census shows its only caller UWindowWindow.DrawClippedActor has no callers"));
            Pair(480, "DrawPortal", (interp, frame, args) =>
                throw new NotSupportedException("Canvas.DrawPortal reached — not ported; the campaign census shows no call site"));
            // UConsole.CreateNativeFont (Engine 0x33a0 execCreateNativeFont -> UViewport::CreateNativeFont,
            // WinDrv UWindowsViewport::CreateNativeFont): an OS font (CreateFont(-Height, …, 400, …)) that GDI DrawText
            // measures and draws (WinDrv UWindowsViewport::DrawString). Called only by HPConsole.SetupLanguage for
            // languages without bitmap fonts (RU edition: 'MS Sans Serif' 24/16/10/12 from System/SAPFont.rus).
            // NOT ORIGINAL: served by the bundled Liberation Sans (UENativeFonts, author decision 2026-11-03); other
            // faces (Asian SAPFont tables) refuse loudly.
            Pair(0, "CreateNativeFont", (interp, frame, args) =>
            {
                string name = args is { Length: > 0 } ? args[0]?.ToString() ?? "" : "";
                int height = args is { Length: > 1 } ? UEValue.ToInt(args[1]) : 0;
                return UENativeFonts.Create(name, height);
            });
        }
    }
}
