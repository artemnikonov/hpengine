// The VM's value semantics — coercion, UE1 truthiness/equality and the
// leading-number parses. Coercions of the port's untyped storage
// (ToInt/ToBool have no shipped counterpart). Typed bytecode conversions are
// the cast-token handlers (checked against Core.dll); comparisons follow
// execSwitch (appStricmp/appMemcmp) and property Identical.
using System;
using Vector3 = HP1.UEScript.UEVector;
using X87Ext = HP1.Core.X87Ext;

namespace HP1.UEScript
{
    public static class UEValue
    {
        // UStructProperty::CopySingleValue / UArrayProperty::CopySingleValue / CopyCompleteValue:
        // structs and arrays are VALUES; the copy is recursive (struct members, static array members, array<>).
        public static object CopyValue(object v) => v switch
        {
            UEObject u when u.IsStruct => u.CloneStruct(),
            System.Collections.Generic.List<object> l => l.ConvertAll(CopyValue),
            _ => v,
        };

        // execStringConst (Core 0x10133cf0) -> appFromAnsi (0x10155600, Core.dll.c:63250): at most 0x3ff = 1023
        // characters into a static 1024 buffer. Encoding of bytes 0x80–0xFF (Latin-1 in shipped) is as
        // UELib returns it, not verified.
        public static string AnsiStringConst(string s) => s is { Length: > 1023 } ? s.Substring(0, 1023) : s;

        public static bool StructEquals(object a, object b)
        {
            if (ReferenceEquals(a, b)) return true;
            var sa = a as UEObject; var sb = b as UEObject;
            if ((sa != null && sa.IsStruct) || (sb != null && sb.IsStruct))
            {
                var keys = new System.Collections.Generic.HashSet<string>(UEPropNameComparer.Instance);
                if (sa != null) foreach (var k in sa.Props.Keys) keys.Add(k);
                if (sb != null) foreach (var k in sb.Props.Keys) keys.Add(k);
                foreach (var k in keys)
                {
                    object ma = sa?.GetProp(k), mb = sb?.GetProp(k);
                    // String member: UStrProperty::Identical — appStricmp (Core
                    // 0x1016f0a0..0x1016f0ce), i.e. CASE-INSENSITIVE, unlike
                    // the == operator on strings (ValueEquals).
                    if (!MemberIdentical(ma, mb)) return false;
                }
                return true;
            }
            // Vector / Plane members are UFloatProperty: Identical per component.
            if (a is UEVector va && b is UEVector vb)
                return FloatIdentical(va.x, vb.x) && FloatIdentical(va.y, vb.y) && FloatIdentical(va.z, vb.z);
            if (a is UEPlane pa && b is UEPlane pb)
                return FloatIdentical(pa.X, pb.X) && FloatIdentical(pa.Y, pb.Y) && FloatIdentical(pa.Z, pb.Z) && FloatIdentical(pa.W, pb.W);
            return ValueEquals(a, b);   // UERotator / Color value-structs + fallback
        }

        // UFloatProperty::Identical (Core 0x1016e0e0): fcomp; test ah,0x40 -> C3 is set for equal AND for unordered, so a
        // NaN on either side counts as identical.
        static bool FloatIdentical(float a, float b) => a == b || float.IsNaN(a) || float.IsNaN(b);

        // execVectorToBool (Core 0x1013a67a..0x1013a6ab): each component fcomp 0.0; test ah,0x40 — C3 is set for equal AND
        // unordered, so a NaN component counts as zero; true when any component is ordered and non-zero.
        public static bool VectorToBool(UEVector v)
        {
            static bool NonZero(float c) => !(c == 0f || float.IsNaN(c));
            return NonZero(v.x) || NonZero(v.y) || NonZero(v.z);
        }

        // UProperty::Identical of a member (UStruct::StructCompare): string — UStrProperty::Identical
        // (appStricmp, Core 0x1016f0a0..0x1016f0ce, case-insensitive); nested struct — recursion;
        // array (static or array<>) — element-wise by the same rule when lengths match (UArrayProperty::Identical);
        // anything else — ValueEquals.
        static bool MemberIdentical(object ma, object mb)
        {
            if (ma is string ssa && mb is string ssb) return UENativesCore.AsciiStricmpEquals(ssa, ssb);
            if ((ma is UEObject ua && ua.IsStruct) || (mb is UEObject ub && ub.IsStruct)) return StructEquals(ma, mb);
            if (ma is float fa && mb is float fb) return FloatIdentical(fa, fb);
            if (ma is UEVector || ma is UEPlane) return StructEquals(ma, mb);
            if (ma is System.Collections.IList la && mb is System.Collections.IList lb)
            {
                if (la.Count != lb.Count) return false;
                for (int i = 0; i < la.Count; i++) if (!MemberIdentical(la[i], lb[i])) return false;
                return true;
            }
            return ValueEquals(ma, mb);
        }

        public static bool ValueEquals(object a, object b)
        {
            if (ReferenceEquals(a, b)) return true;
            // VM convention (see LoadDefaults): a property absent from defaults is
            // stored null and READ as its UE1 type-zero (byte/enum/int/float→0,
            // bool→false, name→'None'). switch(expr) must honour the same coercion —
            // else switch(CameraType) on an unset enum (null) matches no numeric
            // `case 0` (BaseCam.SetCamera never GotoState'd → camera stuck in CutState).
            if (a == null && b == null) return true;
            if (a == null) return IsTypeZero(b);
            if (b == null) return IsTypeZero(a);
            // int vs int — exact (UIntProperty::Identical compares the dword; via float, distinct integers
            // above 2^24 would compare "equal").
            if (a is int ia && b is int ib) return ia == ib;
            // Numeric coercion across int/byte/float
            if ((a is int || a is byte || a is float)
             && (b is int || b is byte || b is float))
                return AsFloat(a) == AsFloat(b);
            if (a is bool ab && b is bool bb) return ab == bb;
            if (a is string sa && b is string sb) return sa == sb;   // UE1 strings ARE case-sensitive
            // UE1 FName comparison is CASE-INSENSITIVE (names intern to a shared table
            // by case-folded key — NameString.h:24 / FName::operator==). switch(name) and
            // EX_StructCmpEq on names must match UE1: 'Idle'=='idle' (UE1
            // never distinguishes names by case).
            if (a is UEName na && b is UEName nb)
                return string.Equals(na.Value, nb.Value, StringComparison.OrdinalIgnoreCase);
            return a.Equals(b);
        }

        // execSwitch (Core.dll.c:44870): a string (size 0 in the token) is compared
        // with appStricmp (_wcsicmp in the "C" locale — case only for 'A'..'Z', see
        // UENativesCore.AsciiStricmpEquals); everything else — ValueEquals. In the campaign
        // all 94 string cases get their input via Caps(), so the difference
        // is visible only on a direct call (fixture core_string_state_natives).
        // A non-string is compared by appMemcmp over the value size: for float these are
        // BITS (−0.0 != 0.0, identical NaNs are equal), not numeric ==.
        // A vector is appMemcmp too (component bits); a name is a single FName index, and the name table folds
        // case only for A..Z (appStricmp) — not Unicode-aware.
        public static bool SwitchCaseMatches(object condVal, object caseVal) => (condVal, caseVal) switch
        {
            (string cs, string ks) => UENativesCore.AsciiStricmpEquals(cs, ks),
            (float cf, float kf) => BitConverter.SingleToInt32Bits(cf) == BitConverter.SingleToInt32Bits(kf),
            (UEVector cv, UEVector kv) => BitConverter.SingleToInt32Bits(cv.x) == BitConverter.SingleToInt32Bits(kv.x)
                && BitConverter.SingleToInt32Bits(cv.y) == BitConverter.SingleToInt32Bits(kv.y)
                && BitConverter.SingleToInt32Bits(cv.z) == BitConverter.SingleToInt32Bits(kv.z),
            (UEName cn, UEName kn) => UENativesCore.AsciiStricmpEquals(cn.Value, kn.Value),
            _ => ValueEquals(condVal, caseVal),
        };

        // True if v equals its UE1 type-zero — the value an absent/null property
        // reads as. Lets switch() on an unset (null) property match the zero case.
        static bool IsTypeZero(object v)
        {
            switch (v)
            {
                case int i:    return i == 0;
                case byte b:   return b == 0;
                case float f:  return f == 0f;
                case bool bo:  return !bo;
                case string s: return s.Length == 0;
                case UEName n: return string.Equals(n.Value, "None", StringComparison.OrdinalIgnoreCase);
                default:       return false; // object/struct: only null equals null
            }
        }

        public static byte AsByte(object v)
        {
            if (v == null) return 0;
            switch (v) {
                case byte b: return b;
                case int i: return (byte)(i & 0xFF);
                case float f: return (byte)((int)f & 0xFF);
                case bool bo: return bo ? (byte)1 : (byte)0;
            }
            return 0;
        }

        public static Vector3 AsVector3(object v) =>
            v is Vector3 vec ? vec
            // A FOREIGN vector type in prop storage (any type merely NAMED
            // Vector3 — i.e. a frontend engine vector) means a bridge writer
            // skipped the ToUE() boundary — loud, never silently coerced.
            // Checked structurally: the Core must not name frontend types.
            : v != null && v.GetType().Name == nameof(Vector3)
                       && v.GetType() != typeof(Vector3)
                ? throw new InvalidOperationException(
                    $"foreign vector type '{v.GetType().FullName}' leaked into prop storage: {v}")
            : Vector3.zero;

        public static UERotator AsRotator(object v) => v is UERotator r ? r : default;

        // Plane as a value: a real UEPlane — as-is; a vector (a local whose
        // W was not written yet) — with W=0; a missing property — the zero
        // plane (UE1 type-zero); a struct bag from package data (generic
        // decoder) — by members X/Y/Z/W.
        public static UEPlane AsPlane(object v)
        {
            switch (v)
            {
                case UEPlane p: return p;
                case Vector3 vec: return new UEPlane(vec, 0f);
                case UEObject bag when bag.IsStruct:
                    return new UEPlane(new Vector3(AsFloat(bag.GetProp(PK.X)), AsFloat(bag.GetProp(PK.Y)),
                        AsFloat(bag.GetProp(PK.Z))), AsFloat(bag.GetProp(PK.W)));
            }
            return new UEPlane(Vector3.zero, 0f);
        }

        public static float AsFloat(object v)
        {
            if (v == null) return 0f;
            switch (v) {
                case float f: return f;
                case int i: return i;
                case byte b: return b;
                case bool bo: return bo ? 1f : 0f;
            }
            if (v is string s && float.TryParse(s,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed)) return parsed;
            return 0f;
        }

        // appAtoi (Core 0x101264b0) — MSVCRT _wtoi. The CRT body is not shipped; body of
        // Microsoft MSVCRT 5.00.7303 (UT, 2000): _wtoi 0x7801c812 =
        // WideCharToMultiByte(CP_ACP, 20-byte buffer) + atol 0x78005d30: skips
        // "C" isspace (_pctype&8: 0x09..0x0D, 0x20), sign '-'/'+', digits _pctype&4
        // ('0'..'9'), accumulates 10*t + c - '0' unchecked — wraps mod 2^32.
        // The port's only appAtoi body (VM casts, config, console, Exec chain).
        // Not established: strings of 20+ characters (W2MB buffer without terminator) and best-fit
        // CP_ACP for characters > 0x7F — here they are neither digits nor spaces.
        public static int LeadingAtoi(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int i = 0, n = s.Length;
            while (i < n && (s[i] == ' ' || (s[i] >= '\t' && s[i] <= '\r'))) i++;
            char sign = i < n ? s[i] : '\0';
            if (sign == '-' || sign == '+') i++;
            uint t = 0;
            while (i < n && s[i] >= '0' && s[i] <= '9') t = unchecked(t * 10 + (uint)(s[i++] - '0'));
            return unchecked(sign == '-' ? -(int)t : (int)t);
        }

        // UE1 appAtof: parse the LEADING float (sign, digits, '.', digits, optional exponent),
        // stopping at the first char that can't extend the number; no leading number → 0.
        // The port's only appAtof body (VM casts, input Parse(float&),
        // config ImportText and the Exec chain).
        public static float LeadingAtof(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0f;
            // appToAnsi (Core.dll.c:39341): at most 0x3ff characters up to the first
            // NUL, characters > 0xFF -> 0x7F — for atof neither space nor digit. atof uses
            // ANSI isspace in the "C" locale (no shipped module imports setlocale):
            // 0x09..0x0D and 0x20; digits only '0'..'9'.
            int n = Math.Min(s.Length, 0x3ff);
            int nul = s.IndexOf('\0', 0, n);
            if (nul >= 0) n = nul;
            static bool Sp(char c) => c == ' ' || (c >= '\t' && c <= '\r');
            static bool Dg(char c) => c >= '0' && c <= '9';
            int i = 0;
            while (i < n && Sp(s[i])) i++;
            int start = i;
            if (i < n && (s[i] == '+' || s[i] == '-')) i++;
            int digits = 0;
            while (i < n && Dg(s[i])) { i++; digits++; }
            if (i < n && s[i] == '.') { i++; while (i < n && Dg(s[i])) { i++; digits++; } }
            if (digits == 0) return 0f;                  // no mantissa digits
            if (i < n && (s[i] == 'e' || s[i] == 'E'))
            {
                int e = i + 1;
                if (e < n && (s[e] == '+' || s[e] == '-')) e++;
                int ed = e;
                while (e < n && Dg(s[e])) e++;
                if (e > ed) i = e;                        // valid exponent → include it
            }
            // appAtof (Core 0x101264c0): appToAnsi + MSVCRT atof -> double in st0,
            // the caller's fstp rounds to float — string -> double -> float, two
            // roundings (parsing straight to float differs at float midpoints).
            return double.TryParse(s.Substring(start, i - start), System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out double r) ? (float)r : 0f;
        }

        // appStrfind (Core.dll.c:42097): case-insensitive search (only 'a'..'z'
        // fold; the tail is _wcsnicmp in the "C" locale); a match counts only
        // at the START of a word — the preceding character is not [A-Za-z0-9].
        // Index of the first match or -1.
        public static int AppStrfind(string s, string find)
        {
            if (string.IsNullOrEmpty(s) || string.IsNullOrEmpty(find)) return -1;
            static char Up(char c) => c > 0x60 && c < 0x7b ? (char)(c - 0x20) : c;
            char first = Up(find[0]);
            bool inWord = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = Up(s[i]);
                if (!inWord && c == first && i + find.Length <= s.Length)
                {
                    int k = 1;
                    while (k < find.Length && Up(s[i + k]) == Up(find[k])) k++;
                    if (k == find.Length) return i;
                }
                inWord = (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
            }
            return -1;
        }

        // Parse(Stream, Match, TCHAR* Value, MaxLen) (Core 0x10151b70, unwind "ParseString"):
        // appStrfind; if '"' follows the key — appStrncpy(MaxLen) of the tail after the quote, cut
        // at the next '"'; otherwise appStrncpy(MaxLen) and cut at the first of ' ', '\r',
        // '\n', '\t', ',' (strings 0x101b394c..0x101b395c). appStrncpy keeps at most
        // MaxLen-1 characters. No key — false, Value untouched.
        public static bool ParseString(string stream, string match, int maxLen, out string value)
        {
            value = null;
            int at = AppStrfind(stream, match);
            if (at < 0) return false;
            string s = stream.Substring(at + match.Length);
            bool quoted = s.Length > 0 && s[0] == '"';
            if (quoted) s = s.Substring(1);
            if (s.Length > maxLen - 1) s = s.Substring(0, maxLen - 1);
            int cut = quoted ? s.IndexOf('"') : s.IndexOfAny(new[] { ' ', '\r', '\n', '\t', ',' });
            value = cut < 0 ? s : s.Substring(0, cut);
            return true;
        }

        // Coerce a runtime value to int as the shipped casts do (execIntToByte 0x101348e0, execByteToInt 0x101344e0);
        // ExpressionValue::ToInt for the cases we actually emit.
        public static int ToInt(object v)
        {
            if (v == null) return 0;
            switch (v)
            {
                case int i:    return i;
                case byte b:   return b;
                case float f:  return (int)f;
                case bool bo:  return bo ? 1 : 0;
            }
            if (v is string s && int.TryParse(s, out var parsed)) return parsed;
            return 0;
        }

        // UE1 truthiness: execIntToBool 0x10134980, execObjectToBool 0x101353d0, execFloatToBool 0x10135100.
        // null/false/0/empty string → false; everything else (incl. live object
        // refs and non-empty strings) → true.
        public static bool ToBool(object v)
        {
            if (v == null) return false;
            switch (v)
            {
                case bool b:   return b;
                case int i:    return i != 0;
                case byte by:  return by != 0;
                // execFloatToBool (Core 0x10135100): fcomp 0.0; test ah,0x40 -> false; unordered sets C3, so NaN is false.
                case float f:  return !(f == 0f || float.IsNaN(f));
                case string s: return !string.IsNullOrEmpty(s);
            }
            return true;
        }

        // ── Vector <-> Rotator (pure UE space) ────────────────────────────
        // Cast Rotator(Vector) — UObject::execVectorToRotator (thunk 0x101034d6)
        // -> FVector::Rotation (Core.dll 0x1014e460..0x1014e4d8; Engine.dll imports the
        // same function for rotateToward, interpolation etc.):
        //   yaw   = _ftol(appAtan2((double)Y, (double)X) * C)
        //   pitch = _ftol(appAtan2((double)Z, appSqrt((double)(X*X + Y*Y))) * C)
        //   roll  = 0
        // C = qword [0x1017fa40] = 0x40C45F1C0516956D (10430.218905280077), NOT
        // 65536/2π. X*X+Y*Y — in
        // x87 PC64 registers (fstp qword), appSqrt 0x10126100 = fsqrt in PC64
        // (fstp qword), appAtan2 0x101260f0 = fpatan, product with C — in the
        // register, _ftol — truncation (low dword of int64).
        // Checked against the real body under unicorn (775 cases). fpatan = UEX87Transcendental.Fpatan (correctly
        // rounded to the 64-bit mantissa, D-71), the product with C one PC64 rounding.
        static readonly double RotationScale = BitConverter.Int64BitsToDouble(0x40C45F1C0516956D);

        public static UERotator RotatorFromVector(Vector3 v)
        {
            if (!IsFinite(v.x) || !IsFinite(v.y) || !IsFinite(v.z))
            {
                // Non-finite input: X87Ext does not hold it. fpatan/fsqrt on
                // infinities are exact in double too, _ftol(NaN) = low dword of
                // 0x8000000000000000 = 0 (UELevelRuntime.Ftol).
                double sq = Math.Sqrt((double)v.x * v.x + (double)v.y * v.y);
                return new UERotator(UELevelRuntime.Ftol(Math.Atan2(v.z, sq) * RotationScale),
                                     UELevelRuntime.Ftol(Math.Atan2(v.y, v.x) * RotationScale), 0);
            }
            // The body sets no control word: PC64 in game logic, PC24 inside the render window
            // (UEWorldFrameProducer.FastMathWindow). Disasm 0x1014e485..0x1014e4a5: fld Y; fld X; Y·Y (fmul st(2)),
            // X·X, faddp, fstp qword, appSqrt (fld qword; fsqrt), fstp qword; the fmul by C after each fpatan. Every
            // fmul/fadd/fsqrt rounds the register at the window's precision (fpatan is correctly rounded at any PC,
            // D-71); the qword stores of a 24-bit value are exact.
            bool pc24 = UEWorldFrameProducer.FastMathWindow;
            X87Ext R(X87Ext x) => pc24 ? X87Ext.Round24(x) : X87Ext.Round64(x);
            var c = X87Ext.From(RotationScale);
            int yaw = (int)R(HP1.Core.UEX87Transcendental.Fpatan(X87Ext.From(v.y), X87Ext.From(v.x)) * c).TruncateToInteger();
            X87Ext X = X87Ext.From(v.x), Y = X87Ext.From(v.y);
            double xy = (pc24 ? R(R(Y * Y) + R(X * X)) : R(X * X + Y * Y)).ToDouble();   // PC64: the products are exact
            double d = R(X87Ext.Sqrt64(X87Ext.From(xy))).ToDouble();
            int pitch = (int)R(HP1.Core.UEX87Transcendental.Fpatan(X87Ext.From(v.z), X87Ext.From(d)) * c).TruncateToInteger();
            return new UERotator(pitch, yaw, 0);
        }

        static bool IsFinite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);

        // execStringToVector (Core.dll.c:824) / execStringToRotator (:6422):
        // X = appAtof(s) (appAtoi for the rotator); after the FIRST ',' (appStrstr) —
        // Y; after the next ',' — Z; missing ones are 0: "5,6" -> (5,6,0).
        public static Vector3 StringToVector(string s)
        {
            var (p0, p1, p2) = SplitCommaParts(s);
            return new Vector3(LeadingAtof(p0), p1 is null ? 0f : LeadingAtof(p1), p2 is null ? 0f : LeadingAtof(p2));
        }

        public static UERotator StringToRotator(string s)
        {
            var (p0, p1, p2) = SplitCommaParts(s);
            return new UERotator(LeadingAtoi(p0), p1 is null ? 0 : LeadingAtoi(p1), p2 is null ? 0 : LeadingAtoi(p2));
        }

        static (string, string, string) SplitCommaParts(string s)
        {
            int c1 = s.IndexOf(',');
            if (c1 < 0) return (s, null, null);
            string t1 = s.Substring(c1 + 1);
            int c2 = t1.IndexOf(',');
            return (s, t1, c2 < 0 ? null : t1.Substring(c2 + 1));
        }

        // Casts float -> int / byte: UObject::execFloatToInt (Core.dll thunk
        // 0x10102ee1) and execFloatToByte (0x10101a82) = `_ftol` (truncation; NaN and
        // outside int64 — "integer indefinite", low dword 0; outside
        // int32 — low dword of int64), byte — low byte. A plain `(int)f`
        // saturates (.NET 9) or gives MinValue (Mono), so frontends would
        // differ. Checked against the original bodies.
        public static int FloatToInt(float f) => UELevelRuntime.Ftol(f);
        public static byte FloatToByte(float f) => (byte)UELevelRuntime.Ftol(f);

        // Cast Vector(Rotator) — UObject::execRotatorToVector (Core.dll 0x1013ab10)
        // = (GMath.UnitCoords / R).XAxis via the SinTab table. The real body
        // run under unicorn: 702 cases bit-equal to this form. Continuous cos/sin
        // would differ the same way as for `>>`/`<<`.
        public static Vector3 RotatorToVector(UERotator r) => GetAxes(r).X;

        // ── Vector ⊗ Rotator operators (`<<` / `>>`) and GetAxes — FROM THE SHIPPED BODIES ──
        //
        // Building the axes via Math.Sin/Cos of a CONTINUOUS angle would differ
        // from shipped Core.dll on 559 of 602 corpus cases (checked by running the real
        // exec natives 276/275 under unicorn): shipped QUANTISES the angle by the table
        // FGlobalMath.SinTab[(a>>2)&0x3fff] (angles 1..3 give zero), and cos/sin are
        // float32 table entries (69 ULP in X).
        //
        // Bodies (Core.dll.c:24784 execGreaterGreater_VectorRotator, 18270
        // execLessLess_VectorRotator, 53242 execGetAxes, 39424 execGetUnAxes):
        //   coords = GMath.UnitCoords;  coords *= FCoords(0, X, Y, Z) one
        //   rotation per step (FCoords::operator*=(FCoords): each axis =
        //   axis.TransformVectorBy(C) = (axis·C.X, axis·C.Y, axis·C.Z), float32
        //   after each step); result of `>>`/`<<` = V.TransformVectorBy(coords).
        //   `>>` : yaw  X=(c, s,0) Y=(-s,c,0) Z=(0,0,1);  pitch X=(c,0, s) Y=(0,1,0)
        //          Z=(-s,0,c);  roll X=(1,0,0) Y=(0,c,-s) Z=(0,s,c);  order yaw,pitch,roll.
        //   `<<` : transposed matrices, order roll, pitch, yaw (= UnitCoords / R).
        //   GetAxes = axes of `UnitCoords / R`; GetUnAxes = axes of `UnitCoords * R`.
        // Shipped accumulates TransformVectorBy in x87 extended (FPCW PC=64)
        // with one store to float32; here — double with one rounding: three
        // float32·float32 products are exact in double, a difference is possible only
        // through double rounding of the sum — measured against the original.
        // The table has one owner: BrushCoords.SinAngle/CosAngle
        // (UEBspCollision.cs, transcription of the FGlobalMath ctor with a float32 step).
        // UnitCoords * R / UnitCoords / R: ONE body, BrushCoords.MulRotator/DivRotator (Core.dll 0x1011d780 /
        // 0x1011e190, the same left-to-right double dot sums of FVector::TransformVectorBy).
        static BrushCoords CoordsMul(UERotator r)
        {
            BrushCoords c = BrushCoords.Identity;
            c.MulRotator(r.Pitch, r.Yaw, r.Roll);
            return c;
        }

        static BrushCoords CoordsDiv(UERotator r)
        {
            BrushCoords c = BrushCoords.Identity;
            c.DivRotator(r.Pitch, r.Yaw, r.Roll);
            return c;
        }

        // V << R (LessLess_VectorRotator, native 275) = V.TransformVectorBy(UnitCoords / R).
        public static Vector3 UnRotateVector(Vector3 v, UERotator r)
        {
            return CoordsDiv(r).TransformVector(v);
        }

        // V >> R (GreaterGreater_VectorRotator, native 276) = V.TransformVectorBy(UnitCoords * R).
        public static Vector3 RotateVector(Vector3 v, UERotator r)
        {
            return CoordsMul(r).TransformVector(v);
        }

        // GetAxes (native 229, Core.dll.c:53242): axes of UnitCoords / R.
        public static (Vector3 X, Vector3 Y, Vector3 Z) GetAxes(UERotator r)
        {
            var c = CoordsDiv(r);
            return (c.X, c.Y, c.Z);
        }

        public static void WriteLValue(UEFrame frame, UELValueRef lref, object value)
        {
            if (UEVmPool.Verify) { if (frame.PoolReleased) UEVmPool.LateRead(frame, "WriteLValue"); UEVmPool.CheckRef(lref); UEVmPool.CheckValue(value); }
            switch (lref.Kind)
            {
                case UELValueKind.Local:
                    if (lref.SlotLayout != null && ReferenceEquals(lref.SlotLayout, frame.Variables.Layout))
                        frame.Variables.SetSlot(lref.Slot, value);
                    else if (!string.IsNullOrEmpty(lref.Name))
                        frame.Variables[lref.Name] = value;
                    return;
                case UELValueKind.Instance:
                    if (lref.Target == null) return;
                    if (lref.SlotLayout != null && ReferenceEquals(lref.SlotLayout, lref.Target.Props.Layout))
                        lref.Target.Props.SetSlot(lref.Slot, value);
                    else if (!string.IsNullOrEmpty(lref.Name))
                        lref.Target.Props[lref.Name] = value;
                    return;
                case UELValueKind.ArrayIndex:
                    if (lref.ListTarget != null
                     && lref.IntIndex >= 0
                     && lref.IntIndex < lref.ListTarget.Count)
                        lref.ListTarget[lref.IntIndex] = value;
                    return;
                case UELValueKind.StructMember:
                    // Read the parent struct, set the member, write the parent back
                    // (UE1 value-struct semantics). Parent is itself an lvalue.
                    if (lref.ParentRef is UELValueRef pref)
                    {
                        var parent = ReadLValue(frame, pref);
                        WriteLValue(frame, pref, SetStructMember(parent, lref.Name, value));
                    }
                    return;
                default:
                    HP1.Core.UETrace.Warn(
                        $"[VM] WriteLValue with invalid kind in " +
                        $"{frame.Object?.Class?.Name}.{frame.Func?.Name}");
                    return;
            }
        }

        // Mirror of WriteLValue for reads — used by out-param dispatch to
        // capture the current value before the call (since UnrealScript out
        // params are read-modify-write: the callee can read the inbound value
        // before overwriting it).
        public static object ReadLValue(UEFrame frame, UELValueRef lref)
        {
            if (UEVmPool.Verify) { if (frame.PoolReleased) UEVmPool.LateRead(frame, "ReadLValue"); UEVmPool.CheckRef(lref); }
            switch (lref.Kind)
            {
                case UELValueKind.Local:
                    if (lref.SlotLayout != null && ReferenceEquals(lref.SlotLayout, frame.Variables.Layout))
                        return frame.Variables.GetSlot(lref.Slot);
                    return frame.Variables.TryGetValue(lref.Name, out var lv) ? lv : null;
                case UELValueKind.Instance:
                    if (lref.Target == null) return null;
                    if (lref.SlotLayout != null && ReferenceEquals(lref.SlotLayout, lref.Target.Props.Layout))
                        return lref.Target.Props.GetSlot(lref.Slot);
                    return lref.Target.Props.TryGetValue(lref.Name, out var iv) ? iv : null;
                case UELValueKind.ArrayIndex:
                    if (lref.ListTarget == null
                     || lref.IntIndex < 0
                     || lref.IntIndex >= lref.ListTarget.Count) return null;
                    return lref.ListTarget[lref.IntIndex];
                case UELValueKind.StructMember:
                    if (lref.ParentRef is UELValueRef pref)
                        return GetStructMember(ReadLValue(frame, pref), lref.Name);
                    return null;
            }
            return null;
        }

        // Read/write a member of a VALUE struct (Vector X/Y/Z, Rotator Pitch/Yaw/Roll
        // — UE component order; the VM keeps vectors in UE space, rotator comps int).
        // Returns the (possibly modified) struct by value.
        public static object GetStructMember(object s, string m)
        {
            if (s is Vector3 v)
                return string.Equals(m, "X", StringComparison.OrdinalIgnoreCase) ? (object)v.x
                     : string.Equals(m, "Y", StringComparison.OrdinalIgnoreCase) ? (object)v.y
                     : string.Equals(m, "Z", StringComparison.OrdinalIgnoreCase) ? (object)v.z : null;
            // Plane — a FOUR-COMPONENT VALUE STRUCT of the VM.
            //
            // Member access must work: all plane bytecode of Object.u (PlaneV/PlaneC/PlaneF and
            // operators +, -, *, +=, -= are ORDINARY script functions with 33..46-byte
            // bodies, not natives) depends on W, as does `FlashFog.W = 0.0` in
            // PlayerPawn.PostBeginPlay. Shipped bytecode runs as-is.
            if (s is UEPlane pl)
                return string.Equals(m, "X", StringComparison.OrdinalIgnoreCase) ? (object)pl.X
                     : string.Equals(m, "Y", StringComparison.OrdinalIgnoreCase) ? (object)pl.Y
                     : string.Equals(m, "Z", StringComparison.OrdinalIgnoreCase) ? (object)pl.Z
                     : string.Equals(m, "W", StringComparison.OrdinalIgnoreCase) ? (object)pl.W : null;
            if (s is UERotator r)
                return string.Equals(m, "Pitch", StringComparison.OrdinalIgnoreCase) ? (object)r.Pitch
                     : string.Equals(m, "Yaw",   StringComparison.OrdinalIgnoreCase) ? (object)r.Yaw
                     : string.Equals(m, "Roll",  StringComparison.OrdinalIgnoreCase) ? (object)r.Roll : null;
            return null;
        }
        public static object SetStructMember(object s, string m, object value)
        {
            // UE1 zero-fills locals — `local Vector V; V.X = X;` writes into a
            // zero struct. A C# null parent must materialize it (typed by the
            // member name; Vector/Rotator members are unambiguous) or the write
            // is lost (Object.Vec returned null forever).
            if (s == null)
            {
                if (string.Equals(m, "X", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(m, "Y", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(m, "Z", StringComparison.OrdinalIgnoreCase))
                    s = Vector3.zero;
                // `local Plane p; p.W = ...` as the first member — the zero plane.
                else if (string.Equals(m, "W", StringComparison.OrdinalIgnoreCase))
                    s = new UEPlane(Vector3.zero, 0f);
                else if (string.Equals(m, "Pitch", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(m, "Yaw", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(m, "Roll", StringComparison.OrdinalIgnoreCase))
                    s = new UERotator(0, 0, 0);
                else
                {
                    var bag = new UEObject { IsStruct = true };
                    bag.Props[m] = value;
                    return bag;
                }
            }
            if (s is Vector3 v)
            {
                if (string.Equals(m, "X", StringComparison.OrdinalIgnoreCase)) v.x = AsFloat(value);
                else if (string.Equals(m, "Y", StringComparison.OrdinalIgnoreCase)) v.y = AsFloat(value);
                else if (string.Equals(m, "Z", StringComparison.OrdinalIgnoreCase)) v.z = AsFloat(value);
                // PROMOTION TO A PLANE. UE1 zeroes a `local Plane p`, but
                // here it starts as null and on the first `p.X = ...` is
                // materialised as a vector (the type is guessed from the member name —
                // Vector has no W). Writing W is the moment it becomes known
                // to be a Plane: X/Y/Z carry over, W is set.
                else if (string.Equals(m, "W", StringComparison.OrdinalIgnoreCase))
                    return new UEPlane(v, AsFloat(value));
                return v;
            }
            if (s is UEPlane pl)
            {
                if (string.Equals(m, "X", StringComparison.OrdinalIgnoreCase)) pl.X = AsFloat(value);
                else if (string.Equals(m, "Y", StringComparison.OrdinalIgnoreCase)) pl.Y = AsFloat(value);
                else if (string.Equals(m, "Z", StringComparison.OrdinalIgnoreCase)) pl.Z = AsFloat(value);
                else if (string.Equals(m, "W", StringComparison.OrdinalIgnoreCase)) pl.W = AsFloat(value);
                return pl;
            }
            if (s is UERotator r)
            {
                if (string.Equals(m, "Pitch", StringComparison.OrdinalIgnoreCase)) r.Pitch = ToInt(value);
                else if (string.Equals(m, "Yaw", StringComparison.OrdinalIgnoreCase)) r.Yaw = ToInt(value);
                else if (string.Equals(m, "Roll", StringComparison.OrdinalIgnoreCase)) r.Roll = ToInt(value);
                return r;
            }
            return s;   // unknown struct type — leave unchanged
        }
    }
}
