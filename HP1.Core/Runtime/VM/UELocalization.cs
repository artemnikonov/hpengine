// Shipped UObject::execLocalize / ::Localize port — reads the ORIGINAL .int
// localization files (review #5 P0: Localize must live in the Core, not the
// Unity dialog-bank bridge, so headless subtitles/warnings resolve from data).
//
// Sources (shipped Core.dll, decompile + literals read from the image):
//   execLocalize  Core.dll.c:10463 — three FString args (section, key,
//                 package; null → ""), then Localize(section,key,package,0,0).
//   Localize      Core.dll.c:11150 —
//                 if (!GIsStarted || !GConfig) return key;
//                 lang = GetLanguage();               // &GLanguage global
//                 loop: file = sprintf("%s.%s", package, lang)   @0x101b3a44
//                       GConfig->GetString(section, key, out, 1024, file)
//                       hit → return value;
//                       lang == "int" → break; else lang = "int"; // @0x101b3a50/58
//                 optional → return "";  (unused by execLocalize: passes 0)
//                 Logf(localization warning);
//                 return sprintf("<?%s?%s.%s.%s?>", lang, package, section, key);  @0x101b3aa0
//
// Language: GLanguage is config-loaded (appInit reads [Engine.Engine]
// Language, see UELevelRuntime.AppInit); the worker's retry loop re-queries
// with "int" and gives up once lang was already "int". This body is the ONE
// Localize of both frontends (C7): headless and Unity register the same
// Core group (UENativesCore.RegisterLocalizationOps).
//
// .int parsing — FConfigFile::Read (Engine 0x103cfad0..0x103cfec0, disasm):
// no whitespace trimming, no comments, the parser itself strips quotes;
// duplicate key — the LAST wins (TMultiMap::Add at the chain head,
// GetString takes the first pair). See ParseIni.
using System;
using System.Collections.Generic;
using System.IO;

namespace HP1.UEScript
{
    public static class UELocalization
    {
        // The original game's System directory (where the .int files live).
        // Set by the frontend bootstrap (headless smoke). Null = the shipped
        // "GIsStarted==0 || GConfig==0" branch: Localize returns the key.
        public static string SystemDir;

        // GLanguage (Core.dll: ?GLanguage@UObject@@0PAGA, 0x101bf174). The initial
        // image of the global in .data is L"int" (read from the binary itself); appInit
        // then asks GConfig for `[Engine.Engine] Language` and, if the key exists,
        // calls SetLanguage (Core.dll.c:56313-56321). GetLanguage
        // (0x101035c1 → &GLanguage) returns this global; it is read by execLocalize,
        // execGetLanguage (Core.dll.c:49839) and UCanvas::WrappedPrint.
        public static string Language { get; private set; } = "int";

        // UObject::SetLanguage (Core.dll.c:50370, export 0x2d06; disasm 0x10164d60..0x10164f25):
        //   if (appStricmp(lang, GLanguage) != 0) {
        //       appStrcpy(GLanguage, lang);
        //       GNone/GTrue/GFalse/GYes/GNo = LocalizeGeneral(<word>, "Core");
        //       for EVERY object in GObjObjects (IsA UObject::PrivateStaticClass 0x10246358)
        //           vtable+0x50 = UObject::LanguageChange -> LoadLocalized(this, 0, 0)
        //   }
        // Case-insensitive comparison: `INT` in the ini does NOT change the global.
        //
        // Object walk. The only caller is appInit (Core.dll.c:56317), and it runs BEFORE
        // UObject::StaticInit (56320): no objects exist yet, the walk is empty. LanguageChange is
        // overridden by nobody in the shipped DLLs (slot +0x50 of every exported U-class is the base
        // thunk, vtable scan). A call AFTER packages are loaded would need LoadLocalized on all
        // objects, which the port does not have: loud STOP.
        //
        // The five global words. The port keeps them as literals "None/True/False/Yes/No" (ExportText
        // writes the literal, ImportText compares case-insensitively). Whether GIsStarted is already
        // set at appInit time (otherwise LocalizeGeneral returns the key and the words stay) is not
        // established statically: HP.exe is packed. So a language whose Core.<lang> [General] gives
        // words differing by more than case is a STOP; for RU (TRUE/FALSE) only the case differs.
        static readonly string[] GlobalWords = { "None", "True", "False", "Yes", "No" };

        public static void SetLanguage(string lang)
        {
            lang ??= "";
            if (string.Equals(lang, Language, StringComparison.OrdinalIgnoreCase)) return;
            if (HP1.Core.UEClassRegistry.Shared is { Packages.Count: > 0 })
                throw new NotSupportedException(
                    "UObject::SetLanguage('" + lang + "') with GLanguage='" + Language + "' after packages are loaded: "
                    + "shipped calls LanguageChange -> LoadLocalized on all objects (Core 0x10164e7e..0x10164ef4) — "
                    + "the port does not have this (STOP, §8); the only shipped caller is appInit before StaticInit");
            if (SystemDir != null)
            {
                var core = LoadFile("Core." + lang);
                if (core != null && core.TryGetValue("General", out var general))
                    foreach (string w in GlobalWords)
                        if (general.TryGetValue(w, out string v) && !string.Equals(v, w, StringComparison.OrdinalIgnoreCase))
                            throw new NotSupportedException(
                                "UObject::SetLanguage('" + lang + "'): Core." + lang + " [General] " + w + "=" + v
                                + " — the global word differs from '" + w + "' by more than case; the port keeps it as a literal, "
                                + "and whether shipped appInit (GIsStarted) localizes it is not established (STOP, §8)");
            }
            Language = lang;
        }

        // file name (lowercase) → section → key → value, all case-insensitive.
        static readonly Dictionary<string, Dictionary<string, Dictionary<string, string>>> _files =
            new(UEPropNameComparer.Instance);

        // Localize (Core.dll.c:11150; body 0x10153430, disasm checked):
        //   [ebp+8]=Section, [ebp+0xc]=Key, [ebp+0x10]=Package, [ebp+0x14]=Lang,
        //   [ebp+0x18]=Optional. execLocalize (Core.dll.c:10463) passes
        //   Lang=NULL and Optional=0.
        //   if (!GIsStarted || !GConfig) return appStrcpy(Key);        0x10153539
        //   lang = Lang ? Lang : GetLanguage();                         0x10153486
        //   loop: file = sprintf("%s.%s", Package, lang)                0x101b3a44
        //         GConfig->GetString(Section, Key, out, 0x400, file) → hit: out
        //         if (!appStricmp(lang, "int")) break;                   0x101b3a50
        //         lang = "int";                                          0x101b3a58
        //   if (Optional) return out;
        //   GLog->Logf(...);
        //   return sprintf("<?%s?%s.%s.%s?>", lang, Package, Section, Key);  0x101b3aa0
        // On a miss the marker carries the LAST queried language — i.e. "int"
        // if the original language was different.
        // `optional` is the fifth shipped argument (Optional): on a miss return
        // an empty buffer with no log and no marker. GetString starts with `*Value = 0`
        // (FConfigCacheIni::GetString — FUN_103d0220, Engine.dll.c:277417; name
        // from unwind :277568). Called by LoadLocalized.
        //
        // A FOUND VALUE IS TRUNCATED TO 1023 CHARACTERS. A GetString hit
        // copies the value with `appStrncpy(Value, found, Size)` (Engine.dll.c:277541),
        // Size = 0x400 from Localize (Core.dll.c:11186); appStrncpy (Core.dll.c:
        // 42661) is wcsncpy(Dest, Src, Max) plus `Dest[Max-1] = 0`. A value longer
        // than 1023 characters is cut to 1023, a shorter one is returned whole.
        public static string Localize(string section, string key, string package, bool optional = false)
        {
            section ??= ""; key ??= ""; package ??= "";
            if (SystemDir == null) return key;
            string lang = Language;
            while (true)
            {
                var file = LoadFile(package + "." + lang);
                if (file != null
                 && file.TryGetValue(section, out var sec)
                 && sec.TryGetValue(key, out var value))
                {
                    // quotes stripped by the parser (ParseIni); appStrncpy(…, 0x400)
                    return value.Length > 1023 ? value.Substring(0, 1023) : value;
                }
                if (string.Equals(lang, "int", StringComparison.OrdinalIgnoreCase)) break;
                lang = "int";
            }
            if (optional) return "";
            // Shipped miss path: a log line (GLog, not the journal) + marker.
            HP1.Core.UETrace.Warn($"Localization: no entry {package}.{section}.{key} ({lang})");
            return $"<?{lang}?{package}.{section}.{key}?>";
        }

        // appPackage (Core.dll.c:6979, export 0x13ac): the executable's base name — HP.exe -> "HP".
        public const string AppPackage = "HP";

        // The window caption: UWindowsViewport sets it from LocalizeGeneral("Product", appPackage()) (WinDrv.dll.c:1706-1708,
        // 11383-11385) — HP.<lang> [General] Product ("Harry Potter" in HP.int), falling back to int like any Localize.
        public static string ViewportCaption() => Localize("General", "Product", AppPackage);

        static Dictionary<string, Dictionary<string, string>> LoadFile(string name)
        {
            if (_files.TryGetValue(name, out var cached)) return cached;
            Dictionary<string, Dictionary<string, string>> parsed = null;
            if (UEInstallPath.File(Path.Combine(SystemDir, name)) is { } path)   // case-insensitive, like Windows
                parsed = ParseIni(LoadFileToString(path));
            _files[name] = parsed;   // cache misses (null) too
            return parsed;
        }

        // appLoadFileToString (Core 0x3472, Core.dll.c:64410-64500) — shipped reads
        // both .int and .ini with it: an even-length file with BOM FF FE is UTF-16LE,
        // with FE FF UTF-16BE (the BOM is not part of the string); otherwise each byte is
        // the character of the same code (ISO-8859-1). The EA Russian localisation (.rus)
        // is UTF-16LE with BOM.
        internal static string LoadFileToString(string path) => DecodeFileBytes(File.ReadAllBytes(path));

        internal static string DecodeFileBytes(byte[] b)
        {
            int n = b.Length;
            if (n > 1 && (n & 1) == 0 && ((b[0] == 0xff && b[1] == 0xfe) || (b[0] == 0xfe && b[1] == 0xff)))
            {
                // code units copied as-is (no surrogate check), as in the shipped body
                bool le = b[0] == 0xff;
                var c = new char[(n - 2) / 2];
                for (int i = 0; i < c.Length; i++)
                    c[i] = le ? (char)(b[2 * i + 2] | b[2 * i + 3] << 8) : (char)(b[2 * i + 2] << 8 | b[2 * i + 3]);
                return new string(c);
            }
            // ISO-8859-1 (codepage 28591); Encoding.Latin1 is .NET5+ only
            // and absent from Unity's netstandard2.1 profile.
            return System.Text.Encoding.GetEncoding(28591).GetString(b);
        }

        // FConfigFile::Read (Engine 0x103cfad0..0x103cfec0; the only characters the
        // body compares against are CR, LF, '[', ']', '"'; '=' is found by appStrstr):
        //   - lines split on CR/LF, empty ones skipped; NO whitespace
        //     trimming and NO comments (`;`, `//` are ordinary characters);
        //   - "[...]" (first '[' and last ']') is a section; a section with the
        //     same name is extended (Find, otherwise Set);
        //   - otherwise, within the current section, the line splits at the FIRST '=':
        //     key = everything to the left as-is, value = everything to the right; a value
        //     starting and ending with '"' loses both quotes;
        //   - Add into the TMultiMap: a new pair goes to the head of the hash chain, GetString
        //     (Engine.dll.c:277353) takes the first pair of the chain — on a duplicate
        //     key the LAST line of the file wins (Pickup [all]
        //     hit_malfoy_text: CONTROL, then JUMP — the player sees JUMP).
        internal static Dictionary<string, Dictionary<string, string>> ParseIni(string text)
        {
            var parsed = new Dictionary<string, Dictionary<string, string>>(UEPropNameComparer.Instance);
            Dictionary<string, string> section = null;
            int pos = 0;
            while (pos < text.Length)
            {
                while (pos < text.Length && (text[pos] == '\r' || text[pos] == '\n')) pos++;
                int start = pos;
                while (pos < text.Length && text[pos] != '\r' && text[pos] != '\n') pos++;
                string line = text.Substring(start, pos - start);
                if (line.Length == 0) continue;
                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    string sn = line.Length >= 2 ? line.Substring(1, line.Length - 2) : "";
                    if (!parsed.TryGetValue(sn, out section))
                        parsed[sn] = section = new Dictionary<string, string>(UEPropNameComparer.Instance);
                    continue;
                }
                if (section == null) continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string value = line.Substring(eq + 1);
                if (value.Length > 0 && value[0] == '"' && value[value.Length - 1] == '"')
                    value = value.Length >= 2 ? value.Substring(1, value.Length - 2) : "";
                section[line.Substring(0, eq)] = value;   // last one wins
            }
            return parsed;
        }
    }
}
