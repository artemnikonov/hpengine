// WHICH FILE IS PACKAGE X: the package half of UEInstallPath (HP1.Core only; the launcher links UEInstallPath.cs).
using System;
using System.Collections.Generic;
using System.IO;

namespace HP1.UEScript
{
    public static partial class UEInstallPath
    {
        // WHICH FILE IS PACKAGE <Name> — THE ONE RULE OF THE INSTALL. appFindPackageFile (Core 0x10153f40) walks GSys->Paths
        // in the [Core.System] order of Default.ini (path index 0 up, 0x10154055..0x101542a1) and returns at the first
        // existing <path><Name>.<ext> (0x1015424e); UObject::GetPackageLinker (Core.dll.c:10652) keeps one linker per
        // package name. So a name present in two directories (community mods: System/HPSounds.u + Sounds/HPSounds.uax,
        // System/HPMenu.u + Textures/HPMenu.utx) is the FIRST file and the other one is never opened by the game.
        // LOCALIZED FILE FIRST, PER PATH ENTRY (disasm 0x10153fef..0x1015424c): bLocalized = appStricmp(GetLanguage(),
        // "int") != 0 (0x10153ffa, 0x101b3b7c = L"int"); for each path, k = 0..bLocalized: the path is cut at its '*'
        // (0x10154117), <dir><Name> is tried bare (0x101541db; package names carry no extension, no install file is
        // extensionless), then <dir><Name> + "." + (k != bLocalized ? GetLanguage() + "_" : "") + <ext without its dot>
        // (0x101541f3..0x1015423e). So with Language=eng the UK build's Textures/MenuArt.eng_utx is package MenuArt
        // and Textures/MenuArt.utx is not opened; with "int" (any case) only the plain name is tried. GetLanguage is
        // GLanguage = [Engine.Engine] Language (appInit, UELocalization.Language). The same bytes are in the UK and RU
        // Core.dll. The case-insensitive directory-listing pass after it (0x101542b2..) tries no language name.
        // Every reader that resolves a package name or lists the packages of a directory goes through here
        // (lock: runtime_poc/verify_architecture.py "package file owner").
        public static readonly string[] PackagePaths =
        {
            "System/{0}.u", "Maps/{0}.unr", "Textures/{0}.utx",
            "Sounds/{0}.uax", "Music/{0}.umx", "save/{0}.usa",
        };

        public static string PackageFile(string root, string name) => Resolve(root, name, out _);

        // The script package <Name>: its file when the package's file is on the System/{0}.u path entry, else null (the
        // name is a texture/sound/... package, or absent). Class sources are loaded only from these.
        public static string ScriptPackageFile(string root, string name) =>
            Resolve(root, name, out int index) is string file && index == 0 ? file : null;

        // The map package <Name>: its file when the package's file is on the Maps/{0}.unr path entry, else null.
        public static string MapPackageFile(string root, string name) =>
            Resolve(root, name, out int index) is string file && index == 1 ? file : null;

        static string Resolve(string root, string name, out int index)
        {
            string lang = Language;
            for (index = 0; index < PackagePaths.Length; index++)
            {
                string plain = string.Format(PackagePaths[index], name);
                if (lang is not null && File(Path.Combine(root, Localized(plain, lang))) is string own) return own;
                if (File(Path.Combine(root, plain)) is string hit) return hit;
            }
            index = -1;
            return null;
        }

        // GLanguage when it is not "int" (the language name is tried), else null.
        static string Language =>
            string.Equals(UELocalization.Language, "int", StringComparison.OrdinalIgnoreCase) ? null : UELocalization.Language;

        // <dir>/<Name>.<ext> -> <dir>/<Name>.<lang>_<ext>
        static string Localized(string file, string lang) =>
            Path.ChangeExtension(file, lang + "_" + Path.GetExtension(file).Substring(1));

        // The packages the game can open from these install directories ("System", "Textures" ...): each directory's
        // files of ITS extension on the path list (<Name>.<ext>, and <Name>.<lang>_<ext> under a non-"int" language)
        // that are the file of their name, so neither one an earlier path shadows nor a plain file its localized one
        // replaces. Directory order as given, then by file name (OrdinalIgnoreCase); a missing directory holds nothing.
        // Each path entry's directory is listed once per call and shadowing is decided from those listings with the
        // answer Resolve gives (exact spelling first, else the first entry matching OrdinalIgnoreCase, as File does).
        public static List<string> PackageFiles(string root, params string[] dirs)
        {
            var files = new List<string>();
            string lang = Language;
            var listings = new Listing[PackagePaths.Length];
            Listing At(int i) => listings[i] ??= new Listing(root, PackagePaths[i]);
            foreach (string sub in dirs)
                for (int i = 0; i < PackagePaths.Length; i++)
                {
                    if (!PackagePaths[i].StartsWith(sub + "/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (At(i).Files is not { } here) continue;
                    string ext = Path.GetExtension(PackagePaths[i]);
                    string own = lang is null ? null : "." + lang + "_" + ext.Substring(1);
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var mine = new List<string>();
                    foreach (string f in here)
                    {
                        string e = Path.GetExtension(f);
                        if (!e.Equals(ext, StringComparison.OrdinalIgnoreCase) && !e.Equals(own, StringComparison.OrdinalIgnoreCase)) continue;
                        string name = Path.GetFileNameWithoutExtension(f);
                        if (!names.Add(name)) continue;
                        for (int j = 0; j < PackagePaths.Length; j++)
                        {
                            string plain = string.Format(PackagePaths[j], name);
                            string file = (lang is null ? null : At(j).Find(root, Localized(plain, lang))) ?? At(j).Find(root, plain);
                            if (file is null) continue;
                            if (j == i) mine.Add(file);
                            break;
                        }
                    }
                    mine.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(Path.GetFileName(a), Path.GetFileName(b)));
                    files.AddRange(mine);
                }
            return files;
        }

        // Original: Engine.dll.c:122755 AActor::execGetMapName (export 0x3ffd, body 0x103d4d90).
        // Wildcard = "*." + FURL::DefaultMapExt; for each GSys->Paths(i) (Default.ini's [Core.System] Paths, which is
        // "../" + PackagePaths[i] with "*") that contains Wildcard (appStrstr, case-sensitive): FindFiles(<Paths(i) cut at
        // Wildcard> + Prefix + Wildcard, Files=1, Dirs=0) relative to System, and each name not yet in the list
        // (appStricmp) is appended. The CD pass (GCdPath + "System" + Paths(i)) never runs here: appInit leaves GCdPath
        // empty when ..\Textures\Palettes.utx exists (Core.dll.c:56325-56333), and the port has no CDPATH= command line.
        // Every name is cut after its last '\\' (InStr/Right loop). The answer is the name Dir steps from MapName
        // (appStrcmp, exact case): before the first -> the last, past the last -> the first; MapName not listed -> the
        // first name, no names -> "".
        public static string GetMapName(string root, string prefix, string mapName, int dir)
        {
            string wildcard = "*." + (UEUrl.DefaultMapExt ?? throw new InvalidOperationException("GetMapName: FURL::StaticInit has not run (DefaultMapExt)"));
            var names = new List<string>();
            foreach (string entry in PackagePaths)
            {
                string path = "../" + string.Format(entry, "*");
                int at = path.IndexOf(wildcard, StringComparison.Ordinal);
                if (at < 0) continue;
                foreach (string f in FindFiles(Path.Combine(root, "System"), path.Substring(0, at) + prefix + wildcard))
                    if (!names.Exists(n => string.Equals(n, f, StringComparison.OrdinalIgnoreCase))) names.Add(f);
            }
            for (int i = 0; i < names.Count; i++)
                while (names[i].IndexOf('\\') >= 0) names[i] = names[i].Substring(names[i].IndexOf('\\') + 1);
            for (int i = 0; i < names.Count; i++)
                if (string.Equals(names[i], mapName, StringComparison.Ordinal))
                {
                    int k = i + dir;
                    return names[k < 0 ? names.Count - 1 : k >= names.Count ? 0 : k];
                }
            return names.Count > 0 ? names[0] : "";
        }

        // FFileManagerWindows::FindFiles(Filename, Files=1, Dirs=0) relative to the working directory (HP.exe runs in
        // System): the file names (no directories) of Filename's directory that match its last component ('*' any run,
        // '?' one character, case-insensitive, as FindFirstFile on long names). Platform boundary: NTFS lists a directory
        // by its upcased names — OrdinalIgnoreCase here, as UERegistry and PackageFiles. Not modelled: FindFirstFile's
        // 8.3 short-name matches ("*.unr" also hitting "x.unrbak" through X~1.UNR where short names exist).
        public static List<string> FindFiles(string workingDir, string filename)
        {
            string f = filename.Replace('\\', '/');
            int cut = f.LastIndexOf('/');
            string dir = Dir(Path.GetFullPath(Path.Combine(workingDir, cut < 0 ? "." : f.Substring(0, cut + 1))));
            string pattern = f.Substring(cut + 1);
            var names = new List<string>();
            if (dir is not null)
                foreach (string e in Directory.EnumerateFiles(dir))
                    if (WildMatch(pattern, 0, Path.GetFileName(e), 0)) names.Add(Path.GetFileName(e));
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        static bool WildMatch(string p, int i, string n, int j)
        {
            if (i == p.Length) return j == n.Length;
            if (p[i] == '*') return WildMatch(p, i + 1, n, j) || (j < n.Length && WildMatch(p, i, n, j + 1));
            return j < n.Length && (p[i] == '?' || char.ToUpperInvariant(p[i]) == char.ToUpperInvariant(n[j])) && WildMatch(p, i + 1, n, j + 1);
        }

        // One path entry's directory, listed once: what File(<root>/<entry dir>/<file>) answers, without a stat or a
        // directory scan per name.
        sealed class Listing
        {
            public readonly List<string> Files;   // enumeration order; null: no such directory
            readonly HashSet<string> _exact = new(StringComparer.Ordinal);
            readonly Dictionary<string, string> _first = new(StringComparer.OrdinalIgnoreCase);
            readonly bool _spelledDir;            // the directory exists under the spelling Path.Combine(root, ...) gives

            public Listing(string root, string entry)
            {
                string spelled = Path.Combine(root, entry.Substring(0, entry.IndexOf('/')));
                string dir = Dir(spelled);
                if (dir is null) return;
                _spelledDir = dir == spelled;
                Files = new List<string>();
                foreach (string f in Directory.EnumerateFileSystemEntries(dir))
                {
                    string leaf = Path.GetFileName(f);
                    bool isFile = System.IO.File.Exists(f);
                    if (isFile) { Files.Add(f); _exact.Add(leaf); }
                    _first.TryAdd(leaf, isFile ? f : null);   // File: the first matching entry, null when it is a directory
                }
            }

            // File(Path.Combine(root, relative)) for a relative path into this directory.
            public string Find(string root, string relative)
            {
                if (Files is null) return null;
                string leaf = Path.GetFileName(relative);
                if (!_first.TryGetValue(leaf, out string first)) return null;
                string spelled = Path.Combine(root, relative);
                // The exact spelling exists (on a case-insensitive file system also in another case): that string.
                if ((_spelledDir && _exact.Contains(leaf)) || System.IO.File.Exists(spelled)) return spelled;
                return first;
            }
        }
    }
}
