// SHIPPED OBJECT LAYOUT BY THE UStruct::Link RULES (Core.dll.c).
//
// The world mirror needs the byte offsets of every class property — the ones the translated Render.dll bodies
// use to read actors. Rules (Core.dll.c line numbers):
//   UStruct::Link (17929): level size = super size, prev is reset on
//     every level (17993); properties in declaration order; the total
//     is aligned to 4.
//   UByteProperty (56669) 1/1; Int (51075), Float (52516), Name (51193),
//   Object (58184), Class (inherits Object) 4/4; Str (3650), Array (25163) 12/4;
//   Bool (4633): 4, packed into the word of the previous bool of the same level while
//     (prev.BitMask & 0x7fffffff) != 0, otherwise a new word with mask 1;
//   Struct (34695): size = the struct's PropertiesSize, alignment 2 for
//     size 2, 1 for size < 4, otherwise 4.
// An unknown property type throws instead of guessing a size.
//
// Checked against an independent dump of the shipped layout over all campaign classes.
using System;
using System.Collections.Generic;
using UELib;
using UELib.Core;

namespace HP1.UEScript.Render
{
    public sealed class ShippedField
    {
        public string Level, Name, Type;
        public int Offset, ElementSize, ArrayDim;
        public uint BitMask;
        public UProperty Property;
        public UStruct Struct;           // for UStructProperty: the resolved struct
    }

    public sealed class ShippedStructLayout
    {
        public readonly List<ShippedField> Fields = new();
        public int Size;
    }

    public static class ShippedLayout
    {
        public const int ObjectSize = 0x28;   // sizeof(UObject), Core.dll.c:4213

        static readonly Dictionary<string, ShippedStructLayout> _structs = new(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<UClass, ShippedStructLayout> _classes = new(UERefComparer<UClass>.Instance);

        static int Align(int x, int a) => (x + a - 1) & ~(a - 1);

        public static bool HasFields(UStruct u)
        {
            foreach (UProperty _ in u.EnumerateFields<UProperty>()) return true;
            return false;
        }

        // Full UELib object path: an import's GetPath() already includes the package; for an
        // export the package is prepended if missing.
        public static string FullPath(UObject o)
        {
            if (o is null) return null;
            string p = o.GetPath();
            string pkg = o.ImportTable != null ? null : o.Package?.PackageName;
            return pkg != null && !p.StartsWith(pkg + ".", StringComparison.OrdinalIgnoreCase) ? $"{pkg}.{p}" : p;
        }

        // An imported struct stub is replaced ONLY by an export with the same
        // full path; no exact match or several matches → Mark.
        public static (UStruct Struct, string Mark) ResolveStruct(UStruct s)
        {
            if (s is null) return (null, "!NOSTRUCT");
            if (HasFields(s)) return (s, null);
            string want = FullPath(s);
            var cands = new List<UStruct>();
            var shown = new List<string>();
            foreach (UnrealPackage pkg in HP1.Core.UEClassRegistry.Shared.Packages)
                foreach (var exp in pkg.Exports)
                {
                    if (!string.Equals(exp.ObjectName.ToString(), s.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.Equals(exp.Class?.ObjectName.ToString(), "Struct", StringComparison.Ordinal)) continue;
                    if (exp.Object is not UStruct cand || !HasFields(cand)) continue;
                    shown.Add(FullPath(cand));
                    if (string.Equals(FullPath(cand), want, StringComparison.OrdinalIgnoreCase)) cands.Add(cand);
                }
            if (cands.Count == 1) return (cands[0], null);
            return (s, $"!NOEXACT({cands.Count})[wanted {want}; have {string.Join(";", shown)}]");
        }

        static (int Size, int AlignTo) Scalar(string type) => type switch
        {
            "UByteProperty" => (1, 1),
            "UIntProperty" or "UFloatProperty" or "UNameProperty" or "UObjectProperty" or "UClassProperty" => (4, 4),
            "UStrProperty" or "UArrayProperty" => (12, 4),
            _ => (0, 0),
        };

        // Levels from the chain root down, each with its own properties.
        static ShippedStructLayout Link(IEnumerable<(string Level, IEnumerable<UProperty> Props)> levels, int baseSize)
        {
            var result = new ShippedStructLayout();
            int size = baseSize;
            bool first = true;
            foreach (var (level, props) in levels)
            {
                if (!first) size = Align(size, 4);
                first = false;
                ShippedField prev = null;
                foreach (UProperty pr in props)
                {
                    var f = new ShippedField { Level = level, Name = pr.Name, Type = pr.GetType().Name, ArrayDim = pr.ArrayDim, Property = pr };
                    if (f.Type == "UBoolProperty")
                    {
                        f.ElementSize = 4;
                        if (prev is not null && prev.Type == "UBoolProperty" && (prev.BitMask & 0x7fffffffu) != 0)
                        { f.Offset = prev.Offset; f.BitMask = prev.BitMask * 2; }
                        else { f.Offset = Align(size, 4); f.BitMask = 1; }
                    }
                    else if (pr is UStructProperty sp)
                    {
                        var (st, mark) = ResolveStruct(sp.Struct);
                        if (mark != null)
                            throw new InvalidOperationException($"ShippedLayout: field struct {level}.{pr.Name}: {mark}");
                        f.Struct = st;
                        f.ElementSize = ForStruct(st).Size;
                        int a = f.ElementSize == 2 ? 2 : f.ElementSize < 4 ? 1 : 4;
                        f.Offset = Align(size, a);
                    }
                    else
                    {
                        var (sz, al) = Scalar(f.Type);
                        if (sz == 0)
                            throw new InvalidOperationException(
                                $"ShippedLayout: layout rule for {f.Type} not read from Core.dll.c (field {level}.{pr.Name})");
                        f.ElementSize = sz; f.Offset = Align(size, al);
                    }
                    size = f.Offset + f.ElementSize * f.ArrayDim;
                    prev = f;
                    result.Fields.Add(f);
                }
            }
            result.Size = Align(size, 4);
            return result;
        }

        public static ShippedStructLayout ForStruct(UStruct s)
        {
            string key = FullPath(s);
            if (_structs.TryGetValue(key, out var done)) return done;
            var chain = new List<UStruct>();
            for (UStruct u = s; u != null;)
            {
                chain.Add(u);
                if (u.Super is null) break;
                var (sup, mark) = ResolveStruct(u.Super as UStruct);
                if (mark != null) throw new InvalidOperationException($"ShippedLayout: struct super {key}: {mark}");
                u = sup;
            }
            chain.Reverse();
            var levels = new List<(string, IEnumerable<UProperty>)>();
            foreach (UStruct u in chain) levels.Add((FullPath(u), u.EnumerateFields<UProperty>()));
            var l = Link(levels, 0);
            _structs[key] = l;
            return l;
        }

        // Class: chain from Object down; the Object header is laid out from zero
        // and must yield exactly sizeof(UObject) (checked, not added).
        public static ShippedStructLayout ForClass(UClass cls)
        {
            if (_classes.TryGetValue(cls, out var done)) return done;
            var reg = HP1.Core.UEClassRegistry.Shared;
            var chain = new List<UClass>();
            for (UClass c = cls; c != null; c = reg.CanonicalSuper(c)) chain.Add(c);
            chain.Reverse();
            var levels = new List<(string, IEnumerable<UProperty>)>();
            foreach (UClass c in chain) levels.Add((c.Name, c.EnumerateFields<UProperty>()));
            bool header = chain.Count > 0 && chain[0].Name == "Object";
            var l = Link(levels, header ? 0 : ObjectSize);
            if (header)
                foreach (var f in l.Fields)
                    if (f.Level != "Object")
                    {
                        if (f.Offset != ObjectSize)
                            throw new InvalidOperationException(
                                $"ShippedLayout: the Object header laid out to 0x{f.Offset:x}, but sizeof(UObject) = 0x{ObjectSize:x}");
                        break;
                    }
            _classes[cls] = l;
            return l;
        }
    }
}
