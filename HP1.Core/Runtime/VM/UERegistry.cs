// The .int object registry: UObject::CacheDrivers -> GObjDrivers / GObjPreferences and UObject::GetRegistryObjects.
// Readers: AActor::execGetNextInt / execGetNextIntDesc (UEGameEngineNatives). No shipped script calls them (mods do).
using System;
using System.Collections.Generic;
using System.IO;

namespace HP1.UEScript
{
    public static class UERegistry
    {
        // FRegistryObjectInfo (0x3c): Object, Class, MetaClass, Description, Autodetect (FStrings at +0, +0xc, ...).
        public sealed record ObjectInfo(string Object, string Class, string MetaClass, string Description, string Autodetect);
        // FPreferencesInfo (0x2c): Caption, ParentCaption, Class, Category (FName), Immediate.
        public sealed record PreferencesInfo(string Caption, string ParentCaption, string Class, string Category, bool Immediate);

        public static readonly List<ObjectInfo> Drivers = new();             // GObjDrivers
        public static readonly List<PreferencesInfo> Preferences = new();    // GObjPreferences
        static string _cachedLanguage = "";                                  // GObjCachedLanguage (bss: empty)
        public static int Generation { get; private set; }                   // bumped when CacheDrivers rebuilds the lists

        // UObject::CacheDrivers (Core.dll.c:61805; disasm 0x101629f0..0x101632c0; strings read from the image).
        //  - ForceRefresh==0 and appStricmp(GObjCachedLanguage, GetLanguage())==0 -> nothing. Else both lists are emptied.
        //  - For each GSys->Paths(i) in order (0x10162aaf): Filename = appBaseDir() + Paths(i); Tmp = strstr("*."),
        //    none -> next path. Tmp = "*.int"; FindFiles(Filename, Files=1, Dirs=0). For each file j:
        //    Tmp = appBaseDir() + Files(j) (0x10162bfd, "%s%s") and GetSection is given Tmp (0x10162c34 push ebx), so the
        //    file read is ALWAYS <appBaseDir = System>/<name>, whichever path directory listed it. The last 4 chars
        //    become "." + GetLanguage() -> GetSection("Public", 0x7fff); 0 -> ".int" -> GetSection again; 0 -> skip.
        //    Each "Key=Value" of the section: Value = after the first '='; a leading '(' and a trailing ')' are cut.
        //    Key "Object" (appStricmp) -> Parse Name=/Class=/MetaClass=/Description=/Autodetect= (Parse FString, 0x1000);
        //    "Preferences" -> Caption=/Parent=/Class= (FString), Category= (FName, 0x40), ParseUBOOL Immediate=.
        //  - Then FindFiles("Core.*") relative to the working directory (0x10162ee9; HP.exe runs in System): per file,
        //    Ext = "." + after the LAST '.' (InStr right, 0x1016300d); no '.' or Ext in {.dll,.u,.ilk} (appStricmp)
        //    -> skip; else a driver {Object = after the FIRST '.' (appStrstr, 0x10163250), Class = "Class",
        //    MetaClass = ULanguage::StaticClass()->GetPathName() = "Core.Language"}.
        // FindFiles order is the file system's: NTFS lists a directory by its upcased names — OrdinalIgnoreCase here
        // (same rule as UEInstallPath.PackageFiles).
        public static void CacheDrivers(bool forceRefresh)
        {
            string lang = UELocalization.Language;
            if (!forceRefresh && string.Equals(_cachedLanguage, lang, StringComparison.OrdinalIgnoreCase)) return;
            string baseDir = UELocalization.SystemDir
                ?? throw new InvalidOperationException("CacheDrivers before appInit: no System directory (appBaseDir)");
            _cachedLanguage = lang;
            Generation++;
            Preferences.Clear();
            Drivers.Clear();
            string root = Path.GetDirectoryName(Path.GetFullPath(baseDir).TrimEnd('/', '\\'));   // appBaseDir + "../"
            foreach (string path in UEInstallPath.PackagePaths)
            {
                string dir = UEInstallPath.Dir(Path.Combine(root, path.Substring(0, path.IndexOf('/'))));
                foreach (string name in FindFiles(dir, f => f.EndsWith(".int", StringComparison.OrdinalIgnoreCase)))
                {
                    string stem = name.Substring(0, name.Length - 4);
                    var section = GetSection("Public", Path.Combine(baseDir, stem + "." + lang))
                               ?? GetSection("Public", Path.Combine(baseDir, stem + ".int"));
                    if (section is null) continue;
                    foreach (string line in section)
                    {
                        int eq = line.IndexOf('=');
                        if (eq < 0) continue;
                        string key = line.Substring(0, eq), value = line.Substring(eq + 1);
                        if (value.Length > 0 && value[0] == '(') value = value.Substring(1);
                        if (value.Length > 0 && value[value.Length - 1] == ')') value = value.Substring(0, value.Length - 1);
                        if (Stricmp(key, "Object"))
                            Drivers.Add(new ObjectInfo(Parse(value, "Name=", 0x1000), Parse(value, "Class=", 0x1000),
                                Parse(value, "MetaClass=", 0x1000), Parse(value, "Description=", 0x1000), Parse(value, "Autodetect=", 0x1000)));
                        else if (Stricmp(key, "Preferences"))
                        {
                            string imm = UEValue.ParseString(value, "Immediate=", 0x10, out string t) ? t : null;
                            string cat = Parse(value, "Category=", 0x40);
                            Preferences.Add(new PreferencesInfo(Parse(value, "Caption=", 0x1000), Parse(value, "Parent=", 0x1000),
                                Parse(value, "Class=", 0x1000), cat.Length == 0 ? "None" : cat,
                                imm is not null && (Stricmp(imm, "On") || Stricmp(imm, "True") || UELocalization.Eq(imm, UELocalization.GTrue) || Stricmp(imm, "1"))));
                        }
                    }
                }
            }
            foreach (string name in FindFiles(baseDir, f => f.StartsWith("Core.", StringComparison.OrdinalIgnoreCase) || Stricmp(f, "Core")))
            {
                int last = name.LastIndexOf('.');
                if (last < 0) continue;
                string ext = name.Substring(last);
                if (Stricmp(ext, ".dll") || Stricmp(ext, ".u") || Stricmp(ext, ".ilk")) continue;
                Drivers.Add(new ObjectInfo(name.Substring(name.IndexOf('.') + 1), "Class", "Core.Language", "", ""));
            }
        }

        // UObject::GetRegistryObjects (Core.dll.c:13379, export 0x177b): CacheDrivers(ForceRefresh), then every driver
        // with appStricmp(Class, Class->GetName())==0 and appStricmp(MetaClass, MetaClass ? GetPathName : "")==0, in order.
        public static List<ObjectInfo> GetRegistryObjects(string className, string metaClassPath, bool forceRefresh = false)
        {
            CacheDrivers(forceRefresh);
            var list = new List<ObjectInfo>();
            foreach (ObjectInfo d in Drivers)
                if (Stricmp(d.Class, className) && Stricmp(d.MetaClass, metaClassPath ?? "")) list.Add(d);
            return list;
        }

        // Original: Engine.dll.c:155656 AActor::execGetNextSkin (export 0x4ae3, body 0x103d42b0).
        // List = GetRegistryObjects(UTexture, MetaClass NULL). Over the entries whose Object starts with Prefix
        // (appStrnicmp, Prefix's length): the one after the entry equal to CurrentSkin (appStricmp) for any Dir but -1
        // and 0, wrapping to the first prefixed one; Dir == -1 the prefixed one before it (none before the first ->
        // ""), Dir == 0 the entry itself; CurrentSkin not among them -> the last prefixed one. Out of range -> "", "".
        // Answer: (Object, Description) of the chosen entry.
        public static (string Name, string Desc) NextSkin(string prefix, string current, int dir)
        {
            List<ObjectInfo> list = GetRegistryObjects("Texture", "");
            int first = -1, prev = -1, result = -1;
            bool found = false, done = false;
            for (int i = 0; i < list.Count && !done; i++)
            {
                if (!StartsWithI(list[i].Object, prefix)) continue;
                if (found) { result = i; done = true; break; }
                if (first == -1) first = i;
                if (Stricmp(list[i].Object, current))
                {
                    if (dir == -1) { result = prev; done = true; break; }
                    if (dir == 0) { result = i; done = true; break; }
                    found = true;
                }
                prev = i;
            }
            if (!done) result = found ? first : -1;
            if (result == -1) result = prev;
            return result < 0 || result >= list.Count ? ("", "") : (list[result].Object, list[result].Description);
        }

        // appStrnicmp(s, prefix, prefix.Length) == 0: a shorter s compares its terminator.
        static bool StartsWithI(string s, string prefix) => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

        // Judge seam (file_natives_judge.py): the driver list as given, CacheDrivers then sees the current language.
        internal static void SeedDriversForJudge(IEnumerable<ObjectInfo> drivers)
        {
            _cachedLanguage = UELocalization.Language;
            Drivers.Clear();
            Drivers.AddRange(drivers);
        }

        static List<string> FindFiles(string dir, Func<string, bool> match)
        {
            var names = new List<string>();
            if (dir is not null && Directory.Exists(dir))
                foreach (string f in Directory.EnumerateFiles(dir))
                    if (!UEInstallPath.IsMacMetadata(Path.GetFileName(f)) && match(Path.GetFileName(f))) names.Add(Path.GetFileName(f));
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        // FConfigCacheIni::GetSection (Engine.dll.c:277600): no file or no section -> 0 (null); else "Key=Value" per pair
        // in Pairs order, stopping when Size <= written + 1 + strlen(Key) (only the key is measured), Size = 0x7fff.
        static List<string> GetSection(string section, string file)
        {
            if (UEInstallPath.File(file) is not string path) return null;
            foreach (var (name, pairs) in UELocalization.ParseIniPairs(UELocalization.LoadFileToString(path)))
            {
                if (!Stricmp(name, section)) continue;
                var lines = new List<string>();
                int written = 0;
                foreach (var kv in pairs)
                {
                    if (0x7fff <= written + 1 + kv.Key.Length) break;
                    lines.Add(kv.Key + "=" + kv.Value);
                    written += kv.Key.Length + 1 + kv.Value.Length + 1;
                }
                return lines;
            }
            return null;
        }

        static string Parse(string stream, string match, int maxLen) => UEValue.ParseString(stream, match, maxLen, out string v) ? v : "";

        // appStricmp; the registry names are ASCII.
        static bool Stricmp(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
