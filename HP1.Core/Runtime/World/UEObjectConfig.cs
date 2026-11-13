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
    // FILE -> UEConfig AREA. GConfig maps the names "System" and "User" to the system and user
    // ini. Any other file (PerObjectConfig with a package name, config(FileName)) is not
    // supported by the port — a loud STOP, not a write "somewhere".
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

        static string ScopeFor(string file)
        {
            if (string.Equals(file, "System", StringComparison.OrdinalIgnoreCase)) return UEConfig.ScopeSystem;
            if (string.Equals(file, "User", StringComparison.OrdinalIgnoreCase)) return UEConfig.ScopeUser;
            throw new NotSupportedException(
                "config file '" + file + "': the port's GConfig holds only System and User "
                + "(PerObjectConfig with a package file / config(Name) are not ported) — STOP, §8");
        }

        // The object's file. `fileClass` is the OBJECT's class (this->Class): for a
        // class default object it is the class itself.
        static string ConfigFile(UEObject obj, UClass fileClass, bool perObject)
        {
            if (perObject && obj?.GetProp(PK.Outer) is UEObject outer)
                return outer.GetProp(PK.Name)?.ToString() ?? "None";
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

        static void RefuseContainer(UProperty p, string op)
        {
            if (p.Type == PropertyType.ArrayProperty || p.Type == PropertyType.MapProperty)
                throw new NotSupportedException(
                    op + ": config property " + OwnerName(p) + "." + p.Name + " of type " + p.Type
                    + " — dynamic array/map (GetSectionPrivate + key removal) not "
                    + "ported; the loaded classes in the campaign have no such config properties — STOP");
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
        public static void SaveConfig(UEConfig config, UEObject obj, UEInterpreter interp)
        {
            if (config is null)
                throw new InvalidOperationException("SaveConfig without the process GConfig (UELevelRuntime.AppInit was not called)");
            if (obj?.Class is null) throw new ArgumentNullException(nameof(obj));
            UClass cls = Canon(obj.Class);
            bool perObject = (ClassFlags(cls) & CLASS_PerObjectConfig) != 0;
            string scope = ScopeFor(ConfigFile(obj, cls, perObject));
            foreach (UProperty p in PropertiesWith(cls, CPF_Config))
            {
                RefuseContainer(p, "SaveConfig");
                string section = Section(obj, cls, p, perObject);
                object slot = obj.GetProp(UEObject.PropKey(obj.Class, OwnerName(p), p.Name.ToString()));
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
                RefuseContainer(p, "LoadConfig");
                scope ??= ScopeFor(file);
                string section = Section(obj, c, p, perObject);
                string key = UEObject.PropKey(obj.Class, OwnerName(p), p.Name.ToString());
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
        internal static void ResetReloadedForTest() { lock (_lock) _reloadedBases.Clear(); }

        // ── ExportTextItem(PortFlags = 0) ───────────────────────────────────
        //   Bool  Core.dll.c:18767 — (flags&1)==0 → literal "True"/"False"
        //         (0x101c2a1c/0x101c2a28), format "%s";
        //   Int   Core.dll.c:55308 — "%i" (0x101c2980);
        //   Float Core.dll.c:63221 — "%.7g" (0x101c2ad4) of (double)float;
        //   Byte  Core.dll.c:13206 — with Enum: value name ("%s"), else "%i";
        //   Str   Core.dll.c:36535 — (flags&2)==0 → string as is;
        //   Name  Core.dll.c:39684 — contains ' ' or ',' → "\"%s\"", else as is.
        public static string ExportTextItem(UProperty p, object v)
        {
            switch (p.Type)
            {
                case PropertyType.BoolProperty: return UEValue.ToBool(v) ? "True" : "False";
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
                case PropertyType.StrProperty: return v?.ToString() ?? "";
                case PropertyType.NameProperty:
                {
                    string n = v is UEName un ? un.Value : v?.ToString();
                    if (string.IsNullOrEmpty(n)) n = "None";
                    return n.IndexOf(' ') >= 0 || n.IndexOf(',') >= 0 ? "\"" + n + "\"" : n;
                }
                default:
                    throw new NotSupportedException(
                        "ExportTextItem: config property " + OwnerName(p) + "." + p.Name + " of type "
                        + p.Type + " is not ported (object/class/struct) — STOP, §8");
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
        public static string FormatG7(float f)
        {
            double d = f;
            if (double.IsNaN(d) || double.IsInfinity(d))
                throw new NotSupportedException("ExportText float " + d + ": the MSVCRT form of NaN/Inf is not ported");
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
        // GTrue/GFalse for the int language are "True"/"False"; for other languages the port's
        // SetLanguage passes only words differing in case (UELocalization.SetLanguage).
        public static bool ImportText(UProperty p, string text, ref object value)
        {
            text ??= "";
            switch (p.Type)
            {
                case PropertyType.BoolProperty:
                {
                    string tok = ReadToken(text);
                    if (tok is null) return false;
                    if (Eq(tok, "1") || Eq(tok, "True")) { value = true; return true; }
                    if (Eq(tok, "0") || Eq(tok, "False")) { value = false; return true; }
                    return false;
                }
                case PropertyType.IntProperty:
                    if (text.Length == 0 || !(text[0] == '-' || IsDigit(text[0]))) return false;
                    value = AppAtoi(text);
                    return true;
                case PropertyType.FloatProperty:
                    value = UEValue.LeadingAtof(text);   // appAtof (Core 0x101264c0)
                    return true;
                case PropertyType.ByteProperty:
                {
                    // With Enum, digits are checked AFTER the token (shipped takes the pointer
                    // ReadToken returned): "3" on an enum byte is eaten by the token and the
                    // value does NOT change.
                    IReadOnlyList<string> names = EnumNamesOf(p);
                    string rest = text;
                    if (names is not null)
                    {
                        string tok = ReadToken(text, out int after);
                        if (tok is null) return false;
                        if (tok.Length > 0 && !Eq(tok, "None"))
                            for (int i = 0; i < names.Count; i++)
                                if (Eq(names[i], tok)) { value = (byte)i; return true; }
                        rest = text.Substring(after);
                    }
                    if (rest.Length == 0 || !IsDigit(rest[0])) return false;
                    value = unchecked((byte)AppAtoi(rest));
                    return true;
                }
                case PropertyType.StrProperty:
                    value = text;
                    return true;
                case PropertyType.NameProperty:
                {
                    string tok = ReadToken(text);
                    if (tok is null) return false;
                    value = new UEName(tok.Length == 0 ? "None" : tok);
                    return true;
                }
                default:
                    throw new NotSupportedException(
                        "ImportText: config property " + OwnerName(p) + "." + p.Name + " of type "
                        + p.Type + " is not ported (object/class/struct) — STOP, §8");
            }
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
        internal static string ReadToken(string s, out int end)
        {
            const int max = 0x400 - 1;
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
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || IsDigit(c) || c == '_' || c == '-';
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
