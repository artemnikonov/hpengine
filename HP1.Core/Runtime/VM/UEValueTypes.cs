// Value types of the VM: UnrealScript name, package resource and model references, and the UE1 vector, rotator and plane
// structs with the shipped Core.dll semantics.
using UELib.Core;

namespace HP1.UEScript
{
    // Distinguishes UnrealScript 'name' from 'string' at runtime. The text is the spelling of the process's name table
    // (UENameTable): an FName prints as it was first registered, whatever package the reference came from.
    public class UEName
    {
        public string Value;

        public UEName(string value) { Value = UENameTable.Spell(value); }
        public UEName(UName name) { Value = name is null ? "None" : UENameTable.Spell(name.ToString()); }

        public override string ToString() => Value;
    }

    // THE NAME TABLE (FName), one per process as in UE1. FName::FName(Text, FNAME_Add) [HP1 Core 0x1010307b]
    // [HP2 0x101030d0] finds the entry with appStricmp and keeps the FIRST registered spelling (it replaces the text only
    // in mode 3, which no game loader passes). A package's names are registered when its linker is created: the
    // ULinkerLoad constructor adds every name-map entry whose flags meet the linker's context, FName(Entry, FNAME_Add)
    // [HP1 0x10148e22] [HP2 0x1014a6b2] (any other entry is NAME_None in that package), the context of the game being
    // GIsClient 0x10000 | GIsServer 0x20000 (ULinker::ULinker, Core.dll.c:15607..15622), and then verifies its imports in
    // table order — each import's root package gets its linker, and so its names, at that point (Verify inlined in the
    // constructor: its guard 0x10149489; VerifyImport -> GetPackageLinker). The first linker is Engine.u's
    // (StaticLoadClass "ini:Engine.Engine.GameEngine", audit/hp2_plan_stage0_1_2026-11-15.md S-6), maps follow as
    // LoadMap loads them. So a name spelled two ways prints the spelling of the package registered first in the session:
    // HP2's Entry.unr spells Harry0's Tag 'Harry', Engine.u 'harry' — the live HP2 EN original prints 'harry'.
    public static class UENameTable
    {
        static readonly object _lock = new();
        static readonly System.Collections.Generic.Dictionary<string, string> _spelling = new(System.StringComparer.Ordinal);
        static readonly System.Collections.Generic.HashSet<string> _linked = new(System.StringComparer.OrdinalIgnoreCase);
        sealed class Memo { public readonly string S, V; public Memo(string s, string v) { S = s; V = v; } }
        static readonly Memo[] _memo = new Memo[4096];     // text reference -> spelling (a spelling never changes)

        // FName(Text, FNAME_Add): the registered spelling of the name, this text registered if the name is new.
        public static string Spell(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            int slot = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(text) & (4096 - 1);
            Memo m = System.Threading.Volatile.Read(ref _memo[slot]);
            if (m is not null && ReferenceEquals(m.S, text)) return m.V;
            string v;
            lock (_lock) { string k = Fold(text); if (!_spelling.TryGetValue(k, out v)) _spelling[k] = v = text; }
            System.Threading.Volatile.Write(ref _memo[slot], new Memo(text, v));
            return v;
        }

        // appStricmp in the C locale folds A..Z only.
        static string Fold(string s)
        {
            int i = 0;
            while (i < s.Length && (s[i] < 'A' || s[i] > 'Z')) i++;
            if (i == s.Length) return s;
            var c = s.ToCharArray();
            for (; i < c.Length; i++) if (c[i] >= 'A' && c[i] <= 'Z') c[i] = (char)(c[i] + 0x20);
            return new string(c);
        }

        // A new process (appInit with no GConfig yet): an empty table.
        internal static void Reset()
        {
            lock (_lock) { _spelling.Clear(); _linked.Clear(); for (int i = 0; i < _memo.Length; i++) _memo[i] = null; }
        }

        // UObject::GetPackageLinker created the linker of this package file: its context names in table order, then the
        // root package of each import in import order (resolved as the game resolves a package name). A package whose
        // linker was created once adds nothing later (its names stay registered). A file that is not there is skipped
        // (no linker, no names).
        public static void Linker(string packageFile)
        {
            if (packageFile is null || !System.IO.File.Exists(packageFile)) return;
            string full = System.IO.Path.GetFullPath(packageFile);
            lock (_lock) { if (!_linked.Add(full)) return; }
            string root = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(full));
            var (names, context, imports) = ReadTables(full);
            for (int i = 0; i < names.Length; i++) if (context[i]) Spell(names[i]);
            foreach ((int outer, string name) in imports)
            {
                // VerifyImport(i): the import's outer chain up to its top-level package
                int o = outer, guard = 0;
                string top = name;
                while (o < 0 && -o - 1 < imports.Count && guard++ < imports.Count) { top = imports[-o - 1].Name; o = imports[-o - 1].Outer; }
                if (o == 0 && top is not null) Linker(UEInstallPath.PackageFile(root, top));
            }
        }

        // The summary's name and import tables: each name and whether its flags meet the game context (else NAME_None in
        // this package's map), each import as (PackageIndex, ObjectName).
        static (string[] Names, bool[] Context, System.Collections.Generic.List<(int Outer, string Name)> Imports) ReadTables(string path)
        {
            using var f = new System.IO.BinaryReader(System.IO.File.OpenRead(path));
            if (f.ReadUInt32() != 0x9e2a83c1) throw new System.IO.InvalidDataException("not an Unreal package: " + path);
            int version = f.ReadUInt16(); f.ReadUInt16(); f.ReadUInt32();
            int nameCount = f.ReadInt32(), nameOffset = f.ReadInt32();
            f.ReadInt32(); f.ReadInt32();
            int importCount = f.ReadInt32(), importOffset = f.ReadInt32();
            var names = new string[nameCount];
            var context = new bool[nameCount];
            f.BaseStream.Position = nameOffset;
            for (int i = 0; i < nameCount; i++)
            {
                string text;
                if (version < 64) { var b = new System.Collections.Generic.List<byte>(); for (byte c; (c = f.ReadByte()) != 0;) b.Add(c); text = System.Text.Encoding.Latin1.GetString(b.ToArray()); }
                else { int len = Compact(f); text = System.Text.Encoding.Latin1.GetString(f.ReadBytes(len)).TrimEnd('\0'); }
                names[i] = text;
                context[i] = (f.ReadUInt32() & 0x30000) != 0;
            }
            var imports = new System.Collections.Generic.List<(int, string)>(importCount);
            f.BaseStream.Position = importOffset;
            for (int i = 0; i < importCount; i++)
            {
                Compact(f); Compact(f);                       // class package, class name
                int outer = f.ReadInt32();
                int n = Compact(f);
                imports.Add((outer, n >= 0 && n < nameCount ? names[n] : null));
            }
            return (names, context, imports);
        }

        static int Compact(System.IO.BinaryReader f)
        {
            byte b = f.ReadByte();
            int v = b & 0x3f;
            if ((b & 0x40) != 0)
                for (int sh = 6; ; sh += 7) { byte c = f.ReadByte(); v |= (c & 0x7f) << sh; if ((c & 0x80) == 0) break; }
            return (b & 0x80) != 0 ? -v : v;
        }
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
