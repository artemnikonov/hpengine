using System;
using System.Collections.Generic;
using System.Globalization;
using HP1.Core;
using UELib.Core;
using UELib.Types;

namespace HP1.UEScript
{
    // UObject::SaveConfig / UObject::LoadConfig — an object's config() properties in ini.
    //
    // SOURCES (Core.dll, decompile + disasm):
    //   SaveConfig  Core.dll.c:63491, body 0x1015b150..0x1015b5f0 (export 0x3404)
    //   LoadConfig  Core.dll.c:37239 (export 0x252c)
    //   execSaveConfig Core.dll.c:5999 → SaveConfig(this, 0x4000, NULL)
    //   UClass::Serialize Core.dll.c:4240 → LoadConfig(Defaults, 0) when loading
    //     EVERY class (after package defaults, before LoadLocalized)
    //   StaticConstructObject Core.dll.c:57882 → LoadConfig(obj, 0) for an instance
    //     of a class with CLASS_PerObjectConfig
    //
    // SHARED BY BOTH (UClass/UProperty layout per decompile offsets):
    //   ClassFlags  Class+0x488: 0x4 CLASS_Config, 0x400 CLASS_PerObjectConfig
    //   ConfigName  Class+0x4a4 (FName; "System" / "User")
    //   PropertyFlags Prop+0x3c: 0x4000 CPF_Config, 0x40000 CPF_GlobalConfig
    //   bPerObject = (this->Class->ClassFlags & 0x400) && this->Index != -1
    //     (a class default object has Index = -1, an instance does not);
    //   FILE = bPerObject && Outer != GObjTransientPkg ? Outer->Name
    //                                                   : this->Class->ConfigName;
    //   property walk — TFieldIterator (FUN_1012e910: along Next, and at the end of the
    //     list to GetInheritanceSuper, slot +0x6c), i.e. ALL properties, inherited ones
    //     included, from the most derived class upward;
    //   SECTION = bPerObject ? this->Name
    //          : (flag 0x40000 ? GetPathName(Prop->GetOwnerClass()) : GetPathName(Class));
    //   KEY = ArrayDim == 1 ? Name : sprintf("%s[%i]", Name, i)   (0x101bfb38/0x101bfab0).
    //
    // In SaveConfig the section "Class" is this->Class; in LoadConfig it is the class of the
    // CURRENT recursion step (param_2), which with propagate=0 is this->Class.
    //
    // FILE -> UEConfig SCOPE by FConfigCacheIni::Find's name rule (UEConfig.ScopeForFile): "System"/"User" are the
    // process inis, any other name (config(Name), a PerObjectConfig package) is the file "<Name>.ini" of System.
    public static class UEObjectConfig
    {
        public const uint CLASS_Config = 0x4;
        public const uint CLASS_PerObjectConfig = 0x400;
        public const ulong CPF_Config = 0x4000;
        public const ulong CPF_GlobalConfig = 0x40000;

        static UEClassRegistry Reg => UEClassRegistry.Shared;

        // A class import stub carries neither flags nor ConfigName — the real export
        // of the same FName answers.
        static UClass Canon(UClass c)
        {
            if (c is null || !UEClassRegistry.IsImportStub(c)) return c;
            UClass real = Reg?.Find(c.Name);
            return real is not null && !UEClassRegistry.IsImportStub(real) ? real : c;
        }

        public static uint ClassFlags(UClass c) => Canon(c) is { } k ? (uint)k.ClassFlags : 0u;

        // GetPathName(Class): a class is a top-level object of its package.
        public static string ClassPath(UClass c)
        {
            UClass k = Canon(c);
            return k.Package.PackageName + "." + k.Name;
        }

        static string ScopeFor(string file) => UEConfig.ScopeForFile(file);

        // The object's file. `fileClass` is the OBJECT's class (this->Class): for a
        // class default object it is the class itself.
        // Original: UObject::SaveConfig (Core.dll.c:63537-63549) / LoadConfig (Core.dll.c:37308-37320):
        //   bPerObject && Outer != GObjTransientPkg ? Outer->Name : Class->ConfigName  (a null FName -> "None").
        // The outer is a VM object (Props Outer) or a package named by UEObject.OuterPath ("Transient" for `new`
        // without Outer, the map package for an actor): Outer->Name is that path's last segment.
        static string ConfigFile(UEObject obj, UClass fileClass, bool perObject)
        {
            if (perObject)
            {
                if (obj?.GetProp(PK.Outer) is UEObject outer)
                    return outer.GetProp(PK.Name)?.ToString() ?? "None";
                string path = obj?.OuterPath
                    ?? throw new NotSupportedException("config file of PerObjectConfig " + obj?.Class?.Name
                        + ": the object's outer is not modelled — STOP, §4");
                if (!string.Equals(path, "Transient", StringComparison.OrdinalIgnoreCase))
                    return path.Substring(path.LastIndexOf('.') + 1);
            }
            string cfg = Canon(fileClass)?.ConfigName?.ToString();
            return string.IsNullOrEmpty(cfg) ? "None" : cfg;
        }

        // TFieldIterator<UProperty>(cls): own fields, then the ancestor's fields.
        static IEnumerable<UProperty> AllProperties(UClass cls)
        {
            for (UClass c = Canon(cls); c is not null; c = Reg.CanonicalSuper(c))
                foreach (UProperty p in c.EnumerateFields<UProperty>())
                    if (p is not null) yield return p;
        }

        // The same walk filtered by a property flag (CPF_Config / CPF_Localized), once per
        // class: LoadDefaults calls this on every spawn, and a class's property set does not
        // change after packages are loaded. Walk order is preserved.
        static readonly Dictionary<(UClass, ulong), UProperty[]> _flagged =
            new Dictionary<(UClass, ulong), UProperty[]>();

        static UProperty[] PropertiesWith(UClass cls, ulong flag)
        {
            lock (_lock)
            {
                if (_flagged.TryGetValue((cls, flag), out UProperty[] hit)) return hit;
                var list = new List<UProperty>();
                foreach (UProperty p in AllProperties(cls))
                    if (((ulong)p.PropertyFlags & flag) != 0) list.Add(p);
                return _flagged[(cls, flag)] = list.ToArray();
            }
        }

        static string OwnerName(UProperty p) => (p.Outer as UClass)?.Name ?? p.Outer?.Name.ToString();

        // Config keys the port does not read (NOT ORIGINAL (D-77): no developer console):
        // globalconfig baseConsole.bDebugMode would turn HPConsole's developer mode on from the settings file. The
        // property keeps its default (false, as with the shipped ini, which has no such key); SaveConfig still writes it.
        static bool IsNotHonoured(UProperty p)
            => string.Equals(OwnerName(p), "baseConsole", StringComparison.OrdinalIgnoreCase)
               && string.Equals(p.Name.ToString(), "bDebugMode", StringComparison.OrdinalIgnoreCase);

        static string Section(UEObject obj, UClass sectionClass, UProperty p, bool perObject)
        {
            if (perObject) return obj.GetProp(PK.Name)?.ToString() ?? "None";
            if (((ulong)p.PropertyFlags & CPF_GlobalConfig) != 0)
                return ClassPath(p.Outer as UClass);
            return ClassPath(sectionClass);
        }

        static string KeyFor(UProperty p, int i)
            => p.ArrayDim == 1 ? p.Name.ToString()
             : string.Format(CultureInfo.InvariantCulture, "{0}[{1}]", p.Name, i);

        // A map property never reaches here: the script compiler (Editor.dll) never constructs a UMapProperty, so no
        // package, shipped or mod-compiled, carries one. Its SaveConfig/LoadConfig branches stay unported, loudly.
        static void RefuseMap(UProperty p, string op)
        {
            if (p.Type == PropertyType.MapProperty)
                throw new NotSupportedException(
                    op + ": config property " + OwnerName(p) + "." + p.Name + " of type MapProperty — the compiler makes "
                    + "no UMapProperty, so the branch is unreachable and not ported — STOP");
        }

        // ── SaveConfig(0x4000, NULL) ────────────────────────────────────────
        //
        // Shipped order: for each property with (flags & 0x4000) == 0x4000 —
        // ExportText(Index, Value, this, this, 0) (slot +0x8c: Data == Delta, so Identical
        // is not consulted, always ExportTextItem with PortFlags=0, disasm
        // 0x1015b4d0..0x1015b4e3) and GConfig->SetString(Section, Key, Value, File)
        // (slot +0x30). Then GConfig->Flush(0) (slot +0x34).
        // UEConfig.Set writes into storage immediately — that is the Flush.
        //
        // TAIL (0x1015b519..): BaseClass = the topmost class from this->Class whose
        // ancestor does NOT carry CLASS_Config (or has no ancestor); for EVERY loaded class
        // with IsChildOf(BaseClass): LoadConfig(Defaults, 1).
        // Live instances are untouched — class defaults change.
        // `isInstance` — this->Index != -1: false for a class default object (InitClassDefaultObject, Core.dll.c:4576,
        // stores Index = -1), which therefore writes the class section of Class->ConfigName even under PerObjectConfig.
        // Original: Core.dll ?SaveConfig@UObject@@QAEXKPBG@Z [HP1 0x1015b110]
        public static void SaveConfig(UEConfig config, UEObject obj, UEInterpreter interp, bool isInstance = true)
        {
            if (config is null)
                throw new InvalidOperationException("SaveConfig without the process GConfig (UELevelRuntime.AppInit was not called)");
            if (obj?.Class is null) throw new ArgumentNullException(nameof(obj));
            UClass cls = Canon(obj.Class);
            bool perObject = isInstance && (ClassFlags(cls) & CLASS_PerObjectConfig) != 0;
            string scope = ScopeFor(ConfigFile(obj, cls, perObject));
            foreach (UProperty p in PropertiesWith(cls, CPF_Config))
            {
                RefuseMap(p, "SaveConfig");
                string section = Section(obj, cls, p, perObject);
                object slot = obj.GetProp(UEObject.PropKey(obj.Class, OwnerName(p), p.Name.ToString()));
                if (p is UArrayProperty ap)
                {
                    // Inner->ExportTextItem(Buf, Elem, Elem, 0) per element (disasm 0x1015b391..0x1015b3af), ArrayDim unused.
                    var texts = new List<string>();
                    if (slot is System.Collections.IList elems)
                        foreach (object e in elems) texts.Add(ExportTextItem(ap.InnerProperty, e));
                    config.SetSectionValues(scope, section, p.Name.ToString(), texts);
                    continue;
                }
                for (int i = 0; i < p.ArrayDim; i++)
                {
                    object v = p.ArrayDim == 1 ? slot
                             : slot is System.Collections.IList list && i < list.Count ? list[i] : null;
                    config.Set(scope, section, KeyFor(p, i), ExportTextItem(p, v));
                }
            }

            UClass baseClass = cls;
            for (UClass sup = Reg.CanonicalSuper(baseClass);
                 sup is not null && (ClassFlags(sup) & CLASS_Config) != 0;
                 sup = Reg.CanonicalSuper(baseClass))
                baseClass = sup;
            MarkReloaded(baseClass);
            interp?.ReloadConfigOfClassDefaults(baseClass, config);
        }

        // ── ResetConfig(Class) (Core 0x1ac3, Core.dll.c:20086) ──────────────
        //   source file by Class->ConfigName (+0x4a4): System -> Default.ini, User -> DefUser.ini; the section
        //   GetPathName(Class) of that file replaces the live one (GConfig +0x18 / +0x30); then LoadConfig(1) of every
        //   loaded class default IsChildOf(Class) and of every object IsA(Class). The port's form: drop the saved
        //   overlay of that section (shipped files are never rewritten), reload the class defaults, and LoadConfig every
        //   live actor of the class. Live objects that are not actors are not enumerable in the port: loud.
        //   A ConfigName other than the FNames System / User (_Names +0x214 / +0x218) returns at once (Core.dll.c:20116-20124).
        public static void ResetConfig(UEConfig config, UClass cls, UEInterpreter interp, IEnumerable<UEObject> actors)
        {
            if (config is null) throw new InvalidOperationException("ResetConfig without the process GConfig");
            UClass c = Canon(cls) ?? throw new ArgumentNullException(nameof(cls));
            string cfgName = c.ConfigName?.ToString();
            if (!string.Equals(cfgName, "System", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(cfgName, "User", StringComparison.OrdinalIgnoreCase)) return;
            if (!Reg.IsChildOfClass(c, Reg.Find("Actor")))
                throw new NotSupportedException("ResetConfig(" + c.Name + "): live objects of a non-actor class are not "
                    + "enumerable in the port (the original walks GObjObjects) — STOP, §4");
            string scope = ScopeFor(cfgName);
            config.Reset(scope, ClassPath(c));
            interp?.ReloadConfigOfClassDefaults(c, config);
            foreach (UEObject a in actors)
                if (a is not null && !a.Destroyed && a.Class is not null && Reg.IsChildOfClass(a.Class, c))
                    LoadConfig(config, a, a.Class, a.Class, propagate: true, isInstance: true);
        }

        // ── LoadConfig(bPropagate, Class, NULL) ─────────────────────────────
        //
        //   if (!(Class->ClassFlags & CLASS_Config)) return;         (Class+0x488 & 4)
        //   if (bPropagate && Class->Super) LoadConfig(1, Super);
        //   for each property of TFieldIterator(Class) with flag 0x4000:
        //     for i < ArrayDim:
        //       if (GConfig->GetString(Section, Key, buf, 0x400, File))  (slot +0x10)
        //           Prop->ImportText(buf, element address, 0);         (slot +0x88)
        // No key — the value stays as it was.
        //
        // `fileClass` — the object's class (this->Class), deciding file and bPerObject;
        // `sectionClass` — the class of the recursion step.
        // Original: Core.dll ?LoadConfig@UObject@@QAEXHPAVUClass@@PBG@Z [HP1 0x1015a870]
        public static void LoadConfig(UEConfig config, UEObject obj, UClass fileClass,
            UClass sectionClass, bool propagate, bool isInstance)
        {
            if (config is null || obj is null) return;
            UClass c = Canon(sectionClass);
            if (c is null || (ClassFlags(c) & CLASS_Config) == 0) return;
            if (propagate && Reg.CanonicalSuper(c) is { } sup)
                LoadConfig(config, obj, fileClass, sup, true, isInstance);
            bool perObject = isInstance && (ClassFlags(fileClass) & CLASS_PerObjectConfig) != 0;
            string file = ConfigFile(obj, fileClass, perObject);
            string scope = null;
            foreach (UProperty p in PropertiesWith(c, CPF_Config))
            {
                if (IsNotHonoured(p)) continue;
                RefuseMap(p, "LoadConfig");
                scope ??= ScopeFor(file);
                string section = Section(obj, c, p, perObject);
                string key = UEObject.PropKey(obj.Class, OwnerName(p), p.Name.ToString());
                if (p is UArrayProperty ap)
                {
                    // No section: the array stays as it was. Otherwise DestroyValue (vtable +0x98) empties it, Add(Num)
                    // zero-fills Num elements, and Inner->ImportText(Value, Elem, 0) fills each (a refused text keeps the
                    // zero). Key = the property name, ArrayDim unused (disasm 0x1015abcf..0x1015ac71).
                    if (!config.TryGetSectionValues(scope, section, p.Name.ToString(), out List<string> values)) continue;
                    var elems = new List<object>(values.Count);
                    foreach (string text in values)
                    {
                        object cur = UEObject.ZeroValueForType(ap.InnerProperty.Type);
                        ImportText(ap.InnerProperty, text, ref cur);
                        elems.Add(cur);
                    }
                    obj.Props[key] = elems;
                    continue;
                }
                for (int i = 0; i < p.ArrayDim; i++)
                {
                    if (!config.TryGetIni(scope, section, KeyFor(p, i), out string text)) continue;
                    if (p.ArrayDim == 1)
                    {
                        object cur = obj.GetProp(key);
                        if (ImportText(p, text, ref cur)) obj.Props[key] = cur;
                    }
                    else
                    {
                        if (obj.GetProp(key) is not System.Collections.IList list)
                            throw new InvalidOperationException(
                                "LoadConfig: static array " + key + " not preallocated on "
                                + obj.Class?.Name);
                        while (list.Count <= i) list.Add(null);
                        object cur = list[i];
                        if (ImportText(p, text, ref cur)) list[i] = cur;
                    }
                }
            }
        }

        // ── LoadLocalized(bPropagate, Class) ────────────────────────────────
        //
        // Core.dll.c:60957 (export 0x3297). Callers: UClass::Serialize
        // (Core.dll.c:4246, defaults of EVERY class, right after LoadConfig),
        // AActor::PostLoad (Engine.dll.c:34396, a placed actor with CLASS_Localized),
        // StaticConstructObject (Core.dll.c:57883, a PerObjectConfig instance),
        // UObject::LanguageChange.
        //   if (!(Class->ClassFlags & 0x20) || GIsEditor) return;  (CLASS_Localized)
        //   if (bPropagate && Class->Super) LoadLocalized(1, Super);
        //   Package = Index == -1 ? Class->Outer->Name : this->Outer->Name;
        //   Section = Index == -1 ? Class->Name        : this->Name;
        //   for a property of TFieldIterator(Class) with flag 0x8000 (CPF_Localized):
        //     for i < ArrayDim: Key = Name / "%s[%i]" (0x101bfad4);
        //       s = Localize(Section, Key, Package, NULL, Optional=1);
        //       if (*s) Prop->ImportText(s, address, 0);
        // `outerName`/`objName` — only for an instance (Index != -1).
        public const uint CLASS_Localized = 0x20;
        public const ulong CPF_Localized = 0x8000;

        public static void LoadLocalized(UEObject obj, UClass cls, bool propagate,
            bool isInstance, string outerName = null, string objName = null)
        {
            if (obj is null || UELocalization.SystemDir is null) return;
            UClass c = Canon(cls);
            if (c is null || (ClassFlags(c) & CLASS_Localized) == 0) return;
            if (propagate && Reg.CanonicalSuper(c) is { } sup)
                LoadLocalized(obj, sup, true, isInstance, outerName, objName);
            string package = isInstance ? outerName ?? "None" : c.Package.PackageName;
            string section = isInstance ? objName ?? "None" : c.Name;
            foreach (UProperty p in PropertiesWith(c, CPF_Localized))
            {
                string key = UEObject.PropKey(obj.Class, OwnerName(p), p.Name.ToString());
                for (int i = 0; i < p.ArrayDim; i++)
                {
                    string text = UELocalization.Localize(section, KeyFor(p, i), package, optional: true);
                    if (text.Length == 0) continue;
                    if (p.ArrayDim == 1)
                    {
                        object cur = obj.GetProp(key);
                        if (ImportText(p, text, ref cur)) obj.Props[key] = cur;
                    }
                    else
                    {
                        if (obj.GetProp(key) is not System.Collections.IList list)
                            throw new InvalidOperationException(
                                "LoadLocalized: static array " + key + " not preallocated on "
                                + obj.Class?.Name);
                        while (list.Count <= i) list.Add(null);
                        object cur = list[i];
                        if (ImportText(p, text, ref cur)) list[i] = cur;
                    }
                }
            }
        }

        // ── DEFAULTS RE-READ BY THE SaveConfig TAIL ─────────────────────────
        //
        // After the SaveConfig tail a class default object is "package layers + LoadConfig(0)
        // at load" PLUS LoadConfig(1). The port rebuilds the defaults of every new object
        // (UEObject.LoadDefaults), so it remembers which hierarchies the tail has re-read and
        // repeats LoadConfig(1) for them at the end of the build. The difference from a single
        // LoadConfig(0) shows only where a subclass's defaultproperties overrode an inherited
        // config property — then propagate brings back the ancestor section's value.
        static readonly List<UClass> _reloadedBases = new List<UClass>();
        static readonly object _lock = new object();

        static void MarkReloaded(UClass baseClass)
        {
            lock (_lock) if (!_reloadedBases.Contains(baseClass)) _reloadedBases.Add(baseClass);
        }

        public static bool WasReloaded(UClass cls)
        {
            lock (_lock)
                foreach (UClass b in _reloadedBases)
                    if (UEInterpreter.ClassIsA(cls, b.Name)) return true;
            return false;
        }

        // For fixtures: the process starts with no re-read hierarchies.
        internal static void ResetReloadedForTest() { lock (_lock) { _reloadedBases.Clear(); _flagged.Clear(); } }

        // ── ExportTextItem(PortFlags = 0) ───────────────────────────────────
        //   Bool  Core.dll.c:18767 — (flags&1)==0 → literal "True"/"False"
        //         (0x101c2a1c/0x101c2a28), format "%s";
        //   Int   Core.dll.c:55308 — "%i" (0x101c2980);
        //   Float Core.dll.c:63221 — "%.7g" (0x101c2ad4) of (double)float;
        //   Byte  Core.dll.c:13206 — with Enum: value name ("%s"), else "%i";
        //   Str   Core.dll.c:36535 — (flags&2)==0 → string as is;
        //   Name  Core.dll.c:39684 — contains ' ' or ',' → "\"%s\"", else as is.
        // `portFlags` & 1 (PPF_Localized; StaticExec GET passes 1, disasm 0x1015cf3b): Bool prints GTrue / GFalse
        // (Core.dll.c:18780-18786); Byte/Int/Float/Name ignore the flags, Str quotes only with & 2 (not passed here).
        public static string ExportTextItem(UProperty p, object v, int portFlags = 0)
        {
            switch (p.Type)
            {
                case PropertyType.BoolProperty:
                    return (portFlags & 1) == 0 ? (UEValue.ToBool(v) ? "True" : "False")
                         : UEValue.ToBool(v) ? UELocalization.GTrue : UELocalization.GFalse;
                case PropertyType.IntProperty:
                    return UEValue.ToInt(v).ToString(CultureInfo.InvariantCulture);
                case PropertyType.FloatProperty: return FormatG7(UEValue.AsFloat(v));
                case PropertyType.ByteProperty:
                    {
                        int b = UEValue.ToInt(v) & 0xff;
                        IReadOnlyList<string> names = EnumNamesOf(p);
                        if (names is null) return b.ToString(CultureInfo.InvariantCulture);
                        if (b >= names.Count)
                            throw new InvalidOperationException(
                                "ExportText: byte " + b + " outside enum " + (p as UByteProperty)?.Enum?.Name
                                + " — shipped would read a name past the end of the table (STOP)");
                        return names[b];
                    }
                // PortFlags & 2 (struct fields): "\"%s\"" (0x101c2d1c), no escaping (Core 0x1016f250).
                case PropertyType.StrProperty: return (portFlags & 2) == 0 ? v?.ToString() ?? "" : "\"" + (v?.ToString() ?? "") + "\"";
                case PropertyType.NameProperty:
                    {
                        string n = v is UEName un ? un.Value : v?.ToString();
                        if (string.IsNullOrEmpty(n)) n = "None";
                        return n.IndexOf(' ') >= 0 || n.IndexOf(',') >= 0 ? "\"" + n + "\"" : n;
                    }
                case PropertyType.ObjectProperty:
                case PropertyType.ClassProperty:
                    return ExportObjectText(p, v);
                case PropertyType.StructProperty:
                    return ExportStructText((UStructProperty)p, v);
                default:
                    throw new NotSupportedException(
                        "ExportTextItem: config property " + OwnerName(p) + "." + p.Name + " of type "
                        + p.Type + " is not ported — STOP, §8");
            }
        }

        static IReadOnlyList<string> EnumNamesOf(UProperty p)
        {
            UEnum e = (p as UByteProperty)?.Enum;
            if (e is null) return null;
            return Reg?.EnumNames(e.Name) ?? throw new InvalidOperationException(
                "enum " + e.Name + " of property " + p.Name + " not found in the package catalog");
        }

        // printf("%.7g", (double)f) in MSVCRT: 7 significant digits, trailing zeros and the
        // point removed; exponent if it is < -4 or >= 7, and then MSVCRT prints it with THREE
        // digits ("1e+007"). Rounding uses the exact double value (for a float, 24-bit
        // mantissa, 7 digits resolve without CRT double rounding); CRT form, not bit-verified.
        // NaN / ±inf, the genuine msvcrt 6.00.8337.0 _snwprintf(L"%.7g") of the fld-dword-widened float
        // (tools/msvcrt_printf/printf_ref.c, 2026-11-15): ±inf "1.#INF" / "-1.#INF"; the indefinite (0xFFC00000)
        // "-1.#IND"; any other NaN "1.#QNAN" / "-1.#QNAN" by its sign (a signalling one is quieted by the fld).
        public static string FormatG7(float f)
        {
            double d = f;
            if (float.IsInfinity(f)) return f < 0 ? "-1.#INF" : "1.#INF";
            if (float.IsNaN(f))
            {
                uint bits = (uint)BitConverter.SingleToInt32Bits(f);
                return bits == 0xFFC00000u ? "-1.#IND" : (bits & 0x80000000u) != 0 ? "-1.#QNAN" : "1.#QNAN";
            }
            if (d == 0) return BitConverter.DoubleToInt64Bits(d) < 0 ? "-0" : "0";
            string e = d.ToString("E6", CultureInfo.InvariantCulture);   // -d.dddddde+XXX
            int ePos = e.IndexOf('E');
            int x = int.Parse(e.Substring(ePos + 1), CultureInfo.InvariantCulture);
            if (x < -4 || x >= 7)
            {
                string m = TrimZeros(e.Substring(0, ePos));
                return m + "e" + (x < 0 ? "-" : "+") + Math.Abs(x).ToString("000", CultureInfo.InvariantCulture);
            }
            return TrimZeros(d.ToString("F" + (6 - x), CultureInfo.InvariantCulture));
        }

        static string TrimZeros(string s)
        {
            if (s.IndexOf('.') < 0) return s;
            s = s.TrimEnd('0');
            return s.EndsWith(".", StringComparison.Ordinal) ? s.Substring(0, s.Length - 1) : s;
        }

        // ── ImportText(Buffer, Data, PortFlags = 0) ─────────────────────────
        //   Bool  Core.dll.c:33202 — ReadToken; "1"/"True"/GTrue → 1,
        //         "0"/"False"/GFalse → 0 (case-insensitive), otherwise unchanged;
        //   Int   Core.dll.c:4375 — only if the first char is '-' or a digit:
        //         appAtoi, otherwise unchanged;
        //   Float Core.dll.c:2075 — appAtof ALWAYS (non-number → 0.0);
        //   Byte  Core.dll.c:65128 — with Enum: ReadToken and name lookup; otherwise /
        //         not found — digit → appAtoi; otherwise unchanged;
        //   Str   Core.dll.c:42556 — (flags&2)==0 → the whole string;
        //   Name  Core.dll.c:35550 — ReadToken → FName.
        // GTrue/GFalse: Core.<lang> [General] True/False (UELocalization.SetLanguage, D03).
        public static bool ImportText(UProperty p, string text, ref object value)
        {
            ImportItem(p, text ?? "", 0, 0, ref value, out bool wrote);
            return wrote;
        }

        // The same bodies with the original's return pointer: the index after the consumed text, or -1 for NULL (a refusal;
        // `wrote` says whether Data was written before it). `flags` — PortFlags: & 2 (set for struct fields) makes a string
        // a ReadToken instead of the whole rest. Int stops after [-0-9]*, Float before , ) CR LF or the end; a Str without
        // & 2 does not advance.
        internal static int ImportItem(UProperty p, string text, int pos, int flags, ref object value, out bool wrote)
        {
            wrote = false;
            string rest = pos == 0 ? text : text.Substring(pos);
            switch (p.Type)
            {
                case PropertyType.BoolProperty:
                    {
                        string tok = ReadToken(rest, out int e);
                        if (tok is null || UELocalization.ImportBoolWord(tok) is not bool b) return -1;
                        value = b; wrote = true;
                        return pos + e;
                    }
                case PropertyType.IntProperty:
                    {
                        int e = 0;
                        if (rest.Length > 0 && (rest[0] == '-' || IsDigit(rest[0]))) { value = AppAtoi(rest); wrote = true; }
                        while (e < rest.Length && (rest[e] == '-' || IsDigit(rest[e]))) e++;
                        return pos + e;
                    }
                case PropertyType.FloatProperty:
                    {
                        value = UEValue.LeadingAtof(rest);   // appAtof (Core 0x101264c0)
                        wrote = true;
                        int e = 0;
                        while (e < rest.Length && rest[e] != ',' && rest[e] != ')' && rest[e] != '\r' && rest[e] != '\n') e++;
                        return pos + e;
                    }
                case PropertyType.ByteProperty:
                    {
                        // With Enum, digits are checked AFTER the token (shipped takes the pointer
                        // ReadToken returned): "3" on an enum byte is eaten by the token and the
                        // value does NOT change.
                        int e = ImportByte(EnumNamesOf(p), rest, out byte b, out bool ok);
                        if (ok) { value = b; wrote = true; }
                        return e < 0 ? -1 : pos + e;
                    }
                case PropertyType.StrProperty:
                    {
                        if ((flags & 2) == 0) { value = rest; wrote = true; return pos; }
                        string tok = ReadToken(rest, out int e, dots: false, max: 0x1000 - 1);
                        if (tok is null) return -1;
                        value = tok; wrote = true;
                        return pos + e;
                    }
                case PropertyType.NameProperty:
                    {
                        string tok = ReadToken(rest, out int e);
                        if (tok is null) return -1;
                        value = new UEName(tok.Length == 0 ? "None" : tok); wrote = true;
                        return pos + e;
                    }
                case PropertyType.ObjectProperty:
                case PropertyType.ClassProperty:
                    {
                        int e = ImportObjectText((UObjectProperty)p, rest, ref value, out wrote);
                        return e < 0 ? -1 : pos + e;
                    }
                case PropertyType.StructProperty:
                    return ImportStructText((UStructProperty)p, text, pos, ref value, out wrote);
                default:
                    throw new NotSupportedException(
                        "ImportText: config property " + OwnerName(p) + "." + p.Name + " of type "
                        + p.Type + " is not ported — STOP, §8");
            }
        }

        static UStruct StructOf(UStructProperty sp)
        {
            var (st, mark) = HP1.UEScript.Render.ShippedLayout.ResolveStruct(sp.Struct);
            return st ?? throw new InvalidOperationException("struct of " + OwnerName(sp) + "." + sp.Name + " not resolved (" + mark + ")");
        }

        static object StructMember(object s, string f) => s is UEObject bag ? bag.GetProp(f) : UEValue.GetStructMember(s, f);

        // Original: Core.dll ?ExportTextItem@UStructProperty@@UBEXPAGPAE1H@Z [HP1 0x101710c0]
        //   For every field of TFieldIterator(Struct) (own fields, then the super struct's) with Port() (vtable +0x9c) and
        //   every element i: UProperty::ExportText(i, buf, Data, Delta, 2) — every caller here passes Delta == Data, so
        //   Identical is never consulted (Core 0x1016cd80) and the element is written; "(" before the first, "," before
        //   the next, "%s=" (0x101c305c) or "%s[%i]=" (0x101c3064) for ArrayDim > 1, then the value; ")" if any was
        //   written, else the buffer stays "".
        static string ExportStructText(UStructProperty sp, object v)
        {
            var sb = new System.Text.StringBuilder();
            int n = 0;
            for (UStruct s = StructOf(sp); s is not null; s = s.Super as UStruct)
                foreach (UProperty f in s.EnumerateFields<UProperty>())
                {
                    if (f is null || !UELevelRuntime.Port(f)) continue;
                    object fv = StructMember(v, f.Name.ToString());
                    for (int i = 0; i < f.ArrayDim; i++)
                    {
                        object ev = f.ArrayDim == 1 ? fv : fv is System.Collections.IList l && i < l.Count ? l[i] : null;
                        sb.Append(++n == 1 ? '(' : ',');
                        sb.Append(f.ArrayDim == 1 ? f.Name.ToString() + "=" : string.Format(CultureInfo.InvariantCulture, "{0}[{1}]=", f.Name, i));
                        sb.Append(ExportTextItem(f, ev, 2));
                    }
                }
            if (n > 0) sb.Append(')');
            return sb.ToString();
        }

        // Original: Core.dll ?ImportText@UStructProperty@@UBEPBGPBGPAEH@Z [HP1 0x10171260] [HP2 0x101746d0]
        //   '(' required. Loop: ')' -> done (index after it). A name of at most 63 chars (HP2: 127 — its name buffer is 128
        //   TCHARs, cmp ecx, 0x7f; the body is HP1's otherwise, native_match consts) up to '=' / '[' / end; "[digits]"
        //   (no ']' -> NULL) gives the index (appAtoi); '=' required. Every struct field (own, then super) with that FName,
        //   0 <= index < ArrayDim and Port() takes Field->ImportText(text, element, PortFlags | 2); a NULL from it is NULL
        //   (fields already written stay written). No such field: the value is skipped up to CR / LF / end or, outside
        //   parentheses, ')' / ','; a '"' re-checks the quote it stands on and so changes nothing; an unclosed '(' -> NULL.
        //   Then ',' -> next field; ')' -> done; anything else -> NULL.
        // The port's struct values: VM value structs (Vector, Rotator, Plane) or IsStruct bags; a written field replaces
        // a copy of the value (the struct is the object's own memory, never a shared default).
        static int ImportStructText(UStructProperty sp, string text, int pos, ref object value, out bool wrote)
        {
            wrote = false;
            char At(int i) => i < text.Length ? text[i] : '\0';
            if (At(pos) != '(') return -1;
            UStruct st = StructOf(sp);
            object cur = value is null ? UEObject.ZeroStructValue(sp) : UEValue.CopyValue(value);
            int p = pos + 1, maxName = UEGameProfile.Current.Game == UEEdition.Game.HP2 ? 0x7f : 0x3f;
            while (true)
            {
                if (At(p) == ')') return p + 1;
                int nameStart = p;
                while (p - nameStart < maxName && At(p) != '\0' && At(p) != '=' && At(p) != '[') p++;
                string name = text.Substring(nameStart, p - nameStart);
                int index = 0;
                if (At(p) == '[')
                {
                    int d = p + 1, q = d;
                    while (IsDigit(At(q))) q++;
                    if (At(q) != ']') return -1;
                    index = AppAtoi(text.Substring(d, q - d));
                    p = q + 1;
                }
                if (At(p) != '=') return -1;
                p++;
                bool found = false;
                if (name.Length > 0 && !Eq(name, "None"))
                    for (UStruct s = st; s is not null; s = s.Super as UStruct)
                        foreach (UProperty f in s.EnumerateFields<UProperty>())
                        {
                            if (f is null || !Eq(f.Name.ToString(), name) || index < 0 || index >= f.ArrayDim
                                || !UELevelRuntime.Port(f)) continue;
                            string fn = f.Name.ToString();
                            object fv = StructMember(cur, fn);
                            object ev = f.ArrayDim == 1 ? fv : fv is System.Collections.IList l && index < l.Count ? l[index] : null;
                            int e = ImportItem(f, text, p, 2, ref ev, out bool w);
                            if (w)
                            {
                                if (f.ArrayDim == 1) cur = SetStructMember(cur, fn, ev);
                                else if (fv is System.Collections.IList list)
                                {
                                    while (list.Count <= index) list.Add(null);
                                    list[index] = ev;
                                }
                                else throw new InvalidOperationException("ImportText: static array " + fn + " of struct " + st.Name + " not preallocated");
                                value = cur; wrote = true;
                            }
                            if (e < 0) return -1;
                            p = e;
                            found = true;
                        }
                if (!found)
                {
                    int depth = 0;
                    for (; At(p) != '\0' && At(p) != '\n' && At(p) != '\r' && (depth > 0 || (At(p) != ')' && At(p) != ',')); p++)
                    {
                        if (At(p) == '(') depth++;
                        else if (At(p) == ')') depth--;
                    }
                    if (depth > 0) return -1;
                }
                if (At(p) == ',') { p++; continue; }
                if (At(p) != ')') return -1;
            }
        }

        static object SetStructMember(object s, string f, object v)
        {
            if (s is UEObject bag) { bag.Props[f] = v; return bag; }
            return UEValue.SetStructMember(s, f, v);
        }

        // Original: Core.dll ?ExportTextItem@UObjectProperty@@UBEXPAGPAE1H@Z [HP1 0x1016e6a0]
        //   NULL -> "None" (0x101c2b88); else appSprintf("%s'%s'" (0x101c2b78), Obj->Class->Name, Obj->GetPathName()).
        // UClassProperty has no ExportTextItem of its own. The port names a class object (Class'Pkg.Name'); any other
        // object's path (an actor in its level, a resource in its groups) is not modelled here: loud.
        static string ExportObjectText(UProperty p, object v)
        {
            if (v is null) return "None";
            if (v is UClass c) return "Class'" + ClassPath(c) + "'";
            throw new NotSupportedException("ExportTextItem: object property " + OwnerName(p) + "." + p.Name
                + " holds " + v.GetType().Name + " — its GetPathName is not modelled (only classes) — STOP, §4");
        }

        // Original: Core.dll ?ImportText@UObjectProperty@@UBEPBGPBGPAEH@Z [HP1 0x1016e790]
        // Original: Core.dll ?ImportText@UClassProperty@@UBEPBGPBGPAEH@Z [HP1 0x1016ea80]
        //   ReadToken(dots allowed) — refused: no write. appStricmp "None" (0x101c2ba8) -> NULL. Spaces skipped; a "'"
        //   makes the token a class name: ReadToken after the quote (dots), a closing "'" required, the class
        //   StaticFindObject(UClass, ANY_PACKAGE, token) — none: no write; the object StaticFindObject(that class,
        //   ANY_PACKAGE, path). Without a quote: StaticFindObject(PropertyClass, ANY_PACKAGE, token). The result is
        //   written even when NULL. UClassProperty then keeps it only when it is a UClass IsChildOf MetaClass, else NULL.
        // Returns whether Data was written. The port finds classes only (FindClassAnyPackage); an object of another class
        // is not modelled: loud. A class-name token absent from the script packages may be a native-only class: loud,
        // except Core's intrinsic `Class` itself.
        //   The returned pointer: after "None" — the token's end; after the quoted form — past the closing quote; after
        //   the bare form — ONE char past the token's end and spaces (puVar5 = puVar1 + 1), so a bare object inside a
        //   struct swallows the next ',' and the struct import fails. NULL (-1) on a lookup miss, after the NULL write.
        static int ImportObjectText(UObjectProperty p, string text, ref object value, out bool wrote)
        {
            wrote = false;
            string tok = ReadToken(text, out int end, dots: true);
            if (tok is null) return -1;
            if (Eq(tok, "None")) { value = null; wrote = true; return end; }
            int pos = end;
            while (pos < text.Length && text[pos] == ' ') pos++;
            int after = pos + 1;
            string path = tok;
            string kind = (p.Object as UClass)?.Name ?? "?";
            if (pos < text.Length && text[pos] == '\'')
            {
                string rest = text.Substring(pos + 1);
                path = ReadToken(rest, out int pathEnd, dots: true);
                if (path is null || pathEnd >= rest.Length || rest[pathEnd] != '\'') return -1;
                after = pos + 1 + pathEnd + 1;
                if (Eq(tok, "Class")) kind = "Class";
                else if (FindClassAnyPackage(tok) is { } named) kind = named.Name;
                else if (tok.Length == 0 || !char.IsAsciiLetter(tok[0]))
                    return -1;   // not a class name (one-character token, digits): StaticFindObject NULL, no write
                else
                    throw new NotSupportedException("ImportText: " + OwnerName(p) + "." + p.Name + " — class token '" + tok
                        + "' is in no script package; the native-only class set is not modelled — STOP, §4");
            }
            if (!Eq(kind, "Class"))
                throw new NotSupportedException("ImportText: " + OwnerName(p) + "." + p.Name + " — finding an object of class "
                    + kind + " by path is not modelled (only classes) — STOP, §4");
            UClass found = FindClassAnyPackage(path);
            value = found;
            wrote = true;
            if (found is null) return -1;
            if (p is UClassProperty cp && !(cp.MetaClass is { } meta && Reg.IsChildOfClass(found, Canon(meta))))
                value = null;
            return Math.Min(after, text.Length);
        }

        // Original: Core.dll ?StaticFindObject@UObject@@SAPAV1@PAVUClass@@PAV1@PBGH@Z [HP1 0x1015b990]
        //   (Class = UClass, Outer = ANY_PACKAGE): ResolveName(Outer NULL, Create 0) — each "X." prefix must name a package
        //   (StaticFindObject(UPackage, Outer, X)) or another object, else NULL; FName(leaf) None (empty or "None") -> NULL;
        //   the GObjHash chain of (leaf, resolved outer); on a miss the ANY_PACKAGE scan of every object named leaf whose
        //   class IsA UClass, lowest GObjObjects index first (a bare name always misses the hash: a class's outer is its
        //   package). The port keeps no object index: a leaf naming classes of several packages is loud (as GetNextInt).
        //   "ini:"/"usr:" names (ResolveName's config redirection) and group paths (two dots) are not modelled: loud.
        internal static UClass FindClassAnyPackage(string name)
        {
            name ??= "";
            if (name.StartsWith("ini:", StringComparison.OrdinalIgnoreCase) || name.StartsWith("usr:", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("StaticFindObject('" + name + "'): ResolveName's ini:/usr: form is not modelled here — STOP, §4");
            string leaf = name;
            string pkg = null;
            int dot = name.IndexOf('.');
            if (dot >= 0)
            {
                if (name.IndexOf('.', dot + 1) >= 0)
                    throw new NotSupportedException("StaticFindObject('" + name + "'): a group path is not modelled — STOP, §4");
                pkg = name.Substring(0, dot);
                leaf = name.Substring(dot + 1);
                bool known = false;
                foreach (var pk in Reg.Packages)
                    if (Eq(pk.PackageName, pkg)) known = true;
                if (!known) return null;
            }
            if (leaf.Length == 0 || Eq(leaf, "None")) return null;
            var hits = new List<UClass>();
            foreach (var pk in Reg.Packages)
                foreach (var ex in pk.Exports)
                    if (ex.Object is UClass c && !UEClassRegistry.IsImportStub(c) && Eq(c.Name, leaf) && !hits.Contains(c))
                    {
                        if (pkg is not null && Eq(pk.PackageName, pkg)) return c;   // the hash hit in the resolved package
                        hits.Add(c);
                    }
            if (hits.Count > 1)
                throw new NotSupportedException("StaticFindObject('" + name + "'): classes named " + leaf + " in several packages; "
                    + "the original takes the lowest GObjObjects index, which the port does not keep — STOP, §4");
            return hits.Count == 1 ? hits[0] : null;
        }

        // UByteProperty::ImportText (Core.dll.c:65128); names — the property's Enum (null: a plain byte).
        internal static bool ImportByte(IReadOnlyList<string> names, string text, out byte value)
        {
            ImportByte(names, text ?? "", out value, out bool ok);
            return ok;
        }

        // The end index (or -1 for NULL) and whether the byte was written.
        static int ImportByte(IReadOnlyList<string> names, string text, out byte value, out bool ok)
        {
            value = 0;
            ok = false;
            int at = 0;
            if (names is not null)
            {
                string tok = ReadToken(text, out int after);
                if (tok is null) return -1;
                if (tok.Length > 0 && !Eq(tok, "None"))
                    for (int i = 0; i < names.Count; i++)
                        if (Eq(names[i], tok)) { value = (byte)i; ok = true; return after; }
                at = after;
            }
            if (at >= text.Length || !IsDigit(text[at])) return -1;
            value = unchecked((byte)AppAtoi(text.Substring(at)));
            ok = true;
            while (at < text.Length && IsDigit(text[at])) at++;
            return at;
        }

        static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        static bool IsDigit(char c) => c >= '0' && c <= '9';

        // ReadToken (FUN_1016c5e0, Core.dll.c: body after ImportText):
        //   '"' — up to the closing quote/CR/LF/end, with `\\` and `\XX` (hex);
        //         no closing quote — refusal (null);
        //   letter/digit — while [A-Za-z0-9_-] (dot only with a flag, here 0);
        //   otherwise — a token of ONE character (empty at end of string included).
        //   Length >= 0x3ff — refusal (buffer 0x400).
        internal static string ReadToken(string s) => ReadToken(s, out _);

        // `end` — index of the first character AFTER the token (shipped returns a pointer).
        // For a single non-alphanumeric character the pointer does NOT advance (shipped
        // returns param_1 as is).
        internal static string ReadToken(string s, out int end) => ReadToken(s, out end, dots: false);

        // `dots` — the flag argument (UObjectProperty::ImportText passes 1): '.' continues a letter/digit token.
        // `max` — the buffer size - 1 (0x400 for most callers, 0x1000 for UStrProperty::ImportText with PortFlags & 2).
        internal static string ReadToken(string s, out int end, bool dots, int max = 0x400 - 1)
        {
            var sb = new System.Text.StringBuilder();
            end = 0;
            if (s.Length > 0 && s[0] == '"')
            {
                int i = 1;
                while (i < s.Length && s[i] != '"' && s[i] != '\r' && s[i] != '\n' && sb.Length < max)
                {
                    if (s[i] == '\\')
                    {
                        char n = i + 1 < s.Length ? s[i + 1] : '\0';
                        if (n == '\\') { sb.Append('\\'); i += 2; continue; }
                        int hi = Math.Max(0, Hex(n));
                        int lo = Hex(i + 2 < s.Length ? s[i + 2] : '\0');
                        sb.Append((char)(lo < 0 ? hi * 16 : hi * 16 + lo));
                        i += 3;
                        continue;
                    }
                    sb.Append(s[i++]);
                }
                if (sb.Length == max) return null;
                if (i >= s.Length || s[i] != '"') return null;
                end = i + 1;
                return sb.ToString();
            }
            char c0 = s.Length > 0 ? s[0] : '\0';
            bool alnum = (c0 >= 'a' && c0 <= 'z') || (c0 >= 'A' && c0 <= 'Z') || IsDigit(c0);
            if (!alnum) return c0 == '\0' ? "" : c0.ToString();
            int j = 0;
            while (j < s.Length && sb.Length < max)
            {
                char c = s[j];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || IsDigit(c) || c == '_' || c == '-'
                          || (dots && c == '.');
                if (!ok) break;
                sb.Append(c);
                j++;
            }
            if (sb.Length == max) return null;
            end = j;
            return sb.ToString();
        }

        // Hex digit of an escape; -1 — not a digit (shipped: the first non-digit gives 0,
        // the second — only the high digit contributes).
        static int Hex(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        static int AppAtoi(string s) => UEValue.LeadingAtoi(s);

    }
}
