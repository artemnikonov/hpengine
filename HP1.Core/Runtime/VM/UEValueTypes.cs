// Value types of the VM: UnrealScript name, package resource and model references, and the UE1 vector, rotator and plane
// structs with the shipped Core.dll semantics.
using UELib.Core;

namespace HP1.UEScript
{
    // Distinguishes UnrealScript 'name' from 'string' at runtime.
    public class UEName
    {
        public string Value;

        public UEName(string value) { Value = value; }
        public UEName(UName name) { Value = name?.ToString() ?? "None"; }

        public override string ToString() => Value;
    }

    // Reference to an original UE render/audio resource. Frontends resolve the
    // exact package/group path; the VM only treats it as a non-None object ref.
    public class UEResourceRef
    {
        // Readonly: Hp1SkeletalMeshCatalog memoises its resolution on the reference (MeshMemo/AnimMemo), which is
        // exact only while these strings never change after construction.
        public readonly string Class;
        public readonly string Name;
        public readonly string ObjectPath;
        // EXACT IDENTITY WHEN THE PATH IS NOT ENOUGH.
        //
        // `ObjectPath` is the readable form; for a texture EMBEDDED in the map
        // it degenerates to `export:Leaf`: the local export index is lost and
        // a package may hold any number of identical leaf names. A reference
        // that shipped returns as a pointer to a concrete object
        // (ADecal::execAttachDecal) cannot be expressed by leaf name or path,
        // so a source that has the identity puts it here
        // (SurfMaterialRef.IdentityKey). null — the source did not provide one.
        public readonly string IdentityKey;
        // Catalog resolution memo (Hp1SkeletalMeshCatalog.Get/GetAnimation by reference): an immutable
        // (catalog, epoch, value) record, published whole so a reader on another thread sees all or nothing.
        internal object MeshMemo, AnimMemo;

        public UEResourceRef(string resourceClass, string name, string objectPath = null,
            string identityKey = null)
        {
            Class = resourceClass;
            Name = name;
            ObjectPath = objectPath;
            IdentityKey = identityKey;
        }

        public override string ToString() => $"{Class}'{ObjectPath ?? Name}'";
    }

    // Reference to a non-actor Model export (a mover's Brush). Carries the
    // export index so collision code can look up the model's serialized
    // bounds and the frontend proxy can bind the imported brush geometry
    // (the frame projects this identity). Plain class, not a positional
    // record; ToString keeps the record rendering byte-for-byte.
    public sealed class UEModelRef
    {
        public string Name { get; }
        public int ExportIndex { get; }
        public UEModelRef(string name, int exportIndex) { Name = name; ExportIndex = exportIndex; }
        public override string ToString() => $"UEModelRef {{ Name = {Name}, ExportIndex = {ExportIndex} }}";
    }

    // UE1 Vector intrinsic — the engine-independent vector type for
    // gameplay code (mirrors UERotator's role for rotators). The SEMANTICS
    // are the shipped FVector's (Core.dll), including == and normalized:
    //   ==          — EXACT per-component float equality
    //                 (execEqualEqual_VectorVector, Core.dll.c:12346: three
    //                 exact fcomp compares, no epsilon).
    //   normalized  — FVector::SafeNormal (Core 0x1014f4e0), in the caller's
    //                 x87 window (SafeNormal64 / SafeNormal24).
    // ToString stays "F2" (journal format only, no shipped analog).
    public struct UEVector : System.IEquatable<UEVector>
    {
        public float x;
        public float y;
        public float z;

        public UEVector(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }

        public static readonly UEVector zero = default;
        public static readonly UEVector one = new UEVector(1f, 1f, 1f);
        public static readonly UEVector up = new UEVector(0f, 1f, 0f);
        public static readonly UEVector down = new UEVector(0f, -1f, 0f);
        public static readonly UEVector right = new UEVector(1f, 0f, 0f);
        public static readonly UEVector forward = new UEVector(0f, 0f, 1f);

        public float sqrMagnitude => x * x + y * y + z * z;
        // FVector::Size as shipped compiles it (calcVelocity 0x103eb8d9..0x103eb916, MultiLineCheck 0x103ac728..0x103ac741):
        // (X·X + Y·Y) + Z·Z in the register, fstp qword, appSqrt (fld qword; fsqrt), fstp dword. The code sets no control
        // word: PC64 in game logic, PC24 inside the render window (UEWorldFrameProducer.FastMathWindow), where every
        // operation rounds to 24 bits.
        public float magnitude => UEWorldFrameProducer.FastMathWindow ? Size24(x, y, z) : Size64(x, y, z);

        // (X·X + Y·Y) + Z·Z in the register at PC64 (the float products are exact).
        public static HP1.Core.X87Ext SizeSquared64(float x, float y, float z)
        {
            var X = HP1.Core.X87Ext.From(x); var Y = HP1.Core.X87Ext.From(y); var Z = HP1.Core.X87Ext.From(z);
            return HP1.Core.X87Ext.Round64(HP1.Core.X87Ext.Round64(X * X + Y * Y) + Z * Z);
        }

        // appSqrt of the register sum: fstp qword, fsqrt at PC64 (result kept in the register).
        public static HP1.Core.X87Ext Size64Ext(float x, float y, float z)
            => HP1.Core.X87Ext.Sqrt64(HP1.Core.X87Ext.From(SizeSquared64(x, y, z).ToDouble()));

        // NaN and ±inf propagate through X87Ext as through the FPU; an overflowing or subnormal result is stored as
        // fstp dword stores it.
        public static float Size64(float x, float y, float z) => Size64Ext(x, y, z).ToFloat();

        // The same chain at PC24: every product, sum and the root round to 24 bits, the exponent stays the register's
        // (15 bits). While every component is 0 or of magnitude in [2^-63, 2^62) no product leaves the float range and
        // the float steps are the same numbers; otherwise the exact model rounds (subnormal and overflowing sizes).
        public static float Size24(float x, float y, float z)
        {
            static bool InRange(float v) { float a = System.Math.Abs(v); return a == 0f || (a >= 1.0842021724855044e-19f && a < 4.611686018427388e18f); }
            if (InRange(x) && InRange(y) && InRange(z)) return (float)System.Math.Sqrt((x * x + y * y) + z * z);
            var X = HP1.Core.X87Ext.From(x); var Y = HP1.Core.X87Ext.From(y); var Z = HP1.Core.X87Ext.From(z);
            static HP1.Core.X87Ext R(HP1.Core.X87Ext v) => HP1.Core.X87Ext.Round24(v);
            var sum = R(R(R(X * X) + R(Y * Y)) + R(Z * Z));
            // fsqrt rounded at 64 then 24 bits equals one rounding to 24 bits (64 >= 2·24 + 2).
            return R(HP1.Core.X87Ext.Sqrt64(sum)).ToFloat();
        }

        // FVector::SafeNormal (Core 0x1014f4e0..0x1014f58c). The body sets no control word: PC64 in game logic, PC24
        // inside the render window (UEWorldFrameProducer.FastMathWindow), as FVector::Size.
        public UEVector normalized => UEWorldFrameProducer.FastMathWindow ? SafeNormal24(x, y, z) : SafeNormal64(x, y, z);

        // PC64: sum and reciprocal wider than float. This double form equals the PC64 chain: 3.1M random inputs against
        // the x87 chain, 0 differences.
        public static UEVector SafeNormal64(float x, float y, float z)
        {
            double sum = ((double)x * x + (double)y * y) + (double)z * z;
            // `fcom 1e-8f; test ah,1; je` (0x1014f504..0x1014f50f): C0 — below OR unordered — returns zero, so a
            // NaN component gives the zero vector.
            if (!(sum >= 9.99999993922529e-09f)) return zero;
            double scale = 1.0 / System.Math.Sqrt(sum);
            // An overflowing sum (an infinite component) gives scale +0: inf·0 is the x87 indefinite, not .NET's NaN.
            if (double.IsInfinity(sum))
            {
                var s0 = HP1.Core.X87Ext.From(scale);
                return new UEVector((HP1.Core.X87Ext.From(x) * s0).ToFloat(), (HP1.Core.X87Ext.From(y) * s0).ToFloat(),
                    (HP1.Core.X87Ext.From(z) * s0).ToFloat());
            }
            return new UEVector((float)(x * scale), (float)(y * scale), (float)(z * scale));
        }

        // PC24, the disasm chain: (x·x + y·y) + z·z in the register (0x1014f4ee..0x1014f4fc), fcom 9.99999994e-9f
        // (0x1017d72c), fstp qword, appSqrt (fsqrt), fdivr 1.0f (0x1017d714), then fld st(0); fmul component; fstp dword
        // per component. Every operation rounds to 24 bits with the register's 15-bit exponent; inside the range of
        // Size24 the float steps are the same numbers, otherwise the exact model rounds.
        public static UEVector SafeNormal24(float x, float y, float z)
        {
            static bool InRange(float v) { float a = System.Math.Abs(v); return a == 0f || (a >= 1.0842021724855044e-19f && a < 4.611686018427388e18f); }
            if (InRange(x) && InRange(y) && InRange(z))
            {
                float sq = (x * x + y * y) + z * z;
                if (sq < 9.99999993922529e-09f) return zero;
                float inv = 1f / (float)System.Math.Sqrt(sq);
                return new UEVector(x * inv, y * inv, z * inv);
            }
            static HP1.Core.X87Ext R(HP1.Core.X87Ext v) => HP1.Core.X87Ext.Round24(v);
            var X = HP1.Core.X87Ext.From(x); var Y = HP1.Core.X87Ext.From(y); var Z = HP1.Core.X87Ext.From(z);
            var sum = R(R(R(X * X) + R(Y * Y)) + R(Z * Z));
            if ((HP1.Core.X87Ext.Fcom(sum, HP1.Core.X87Ext.From(9.99999993922529e-09f)) & HP1.Core.X87Ext.C0) != 0) return zero;
            // fstp qword then fld qword: the double store keeps a 24-bit mantissa; an exponent beyond double does not occur
            // for float inputs (|sum| <= 3·2^256).
            var root = R(HP1.Core.X87Ext.Sqrt64(HP1.Core.X87Ext.From(sum.ToDouble())));
            var inv24 = R(HP1.Core.X87Ext.Div64(HP1.Core.X87Ext.From(1f), root));
            return new UEVector(R(inv24 * X).ToFloat(), R(inv24 * Y).ToFloat(), R(inv24 * Z).ToFloat());
        }

        public static float Dot(UEVector a, UEVector b) => a.x * b.x + a.y * b.y + a.z * b.z;

        public static UEVector Cross(UEVector a, UEVector b) => new UEVector(
            a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);

        public static UEVector Scale(UEVector a, UEVector b)
            => new UEVector(a.x * b.x, a.y * b.y, a.z * b.z);

        public static float Distance(UEVector a, UEVector b)
        {
            float dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
            return (float)System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        public static UEVector Min(UEVector a, UEVector b) => new UEVector(
            System.Math.Min(a.x, b.x), System.Math.Min(a.y, b.y), System.Math.Min(a.z, b.z));

        public static UEVector Max(UEVector a, UEVector b) => new UEVector(
            System.Math.Max(a.x, b.x), System.Math.Max(a.y, b.y), System.Math.Max(a.z, b.z));

        public static UEVector Reflect(UEVector inDirection, UEVector inNormal)
        {
            float num = -2f * Dot(inNormal, inDirection);
            return new UEVector(num * inNormal.x + inDirection.x,
                num * inNormal.y + inDirection.y, num * inNormal.z + inDirection.z);
        }

        public static UEVector Lerp(UEVector a, UEVector b, float t)
        {
            if (t < 0f) t = 0f;
            else if (t > 1f) t = 1f;
            return new UEVector(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t,
                a.z + (b.z - a.z) * t);
        }

        public static UEVector operator +(UEVector a, UEVector b)
            => new UEVector(a.x + b.x, a.y + b.y, a.z + b.z);
        public static UEVector operator -(UEVector a, UEVector b)
            => new UEVector(a.x - b.x, a.y - b.y, a.z - b.z);
        public static UEVector operator -(UEVector a) => new UEVector(-a.x, -a.y, -a.z);
        public static UEVector operator *(UEVector a, float d)
            => new UEVector(a.x * d, a.y * d, a.z * d);
        public static UEVector operator *(float d, UEVector a)
            => new UEVector(a.x * d, a.y * d, a.z * d);
        public static UEVector operator /(UEVector a, float d)
            => new UEVector(a.x / d, a.y / d, a.z / d);

        public static bool operator ==(UEVector a, UEVector b)
            => a.x == b.x && a.y == b.y && a.z == b.z;   // shipped: exact, no epsilon

        public static bool operator !=(UEVector a, UEVector b) => !(a == b);

        public bool Equals(UEVector other) => x == other.x && y == other.y && z == other.z;
        public override bool Equals(object other) => other is UEVector v && Equals(v);
        public override int GetHashCode()
            => x.GetHashCode() ^ (y.GetHashCode() << 2) ^ (z.GetHashCode() >> 2);

        public override string ToString() =>
            $"({x.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}, " +
            $"{y.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}, " +
            $"{z.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)})";

        public string ToString(string format) =>
            $"({x.ToString(format, System.Globalization.CultureInfo.InvariantCulture)}, " +
            $"{y.ToString(format, System.Globalization.CultureInfo.InvariantCulture)}, " +
            $"{z.ToString(format, System.Globalization.CultureInfo.InvariantCulture)})";
    }

    // UE1 Rotator intrinsic: 65536 raw units equal one full turn.
    public struct UERotator
    {
        public int Pitch;
        public int Yaw;
        public int Roll;

        public UERotator(int pitch, int yaw, int roll)
        {
            Pitch = pitch;
            Yaw = yaw;
            Roll = roll;
        }

        public override string ToString() => $"({Pitch},{Yaw},{Roll})";
    }

    // UE1 `Plane extends Vector { var float W; }` — the PlaneV intrinsic's
    // result value. TRANSPARENT TO THE VM: members X/Y/Z/W are read and
    // written through UEValue.Get/SetStructMember, so the plane bytecode of
    // Object.u (PlaneV/PlaneC/PlaneF, operators) and PlayerPawn.ViewFlash /
    // ClientFade* / PostBeginPlay run as-is. There is no PlaneV intercept;
    // the frontend reads WorldFrame.ViewFlash, not Props. ToString format is kept.
    public struct UEPlane : System.IEquatable<UEPlane>
    {
        public float X, Y, Z, W;

        public UEPlane(UEVector v, float w) { X = v.x; Y = v.y; Z = v.z; W = w; }
        public UEPlane(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }
        public UEVector XYZ => new UEVector(X, Y, Z);

        public bool Equals(UEPlane o) => X == o.X && Y == o.Y && Z == o.Z && W == o.W;
        public override bool Equals(object o) => o is UEPlane p && Equals(p);
        public override int GetHashCode() =>
            X.GetHashCode() ^ (Y.GetHashCode() << 2) ^ (Z.GetHashCode() >> 2) ^ (W.GetHashCode() >> 1);
        public override string ToString()
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            return $"({X.ToString("F2", ci)}, {Y.ToString("F2", ci)}, {Z.ToString("F2", ci)}, {W.ToString("F2", ci)})";
        }
    }
}
