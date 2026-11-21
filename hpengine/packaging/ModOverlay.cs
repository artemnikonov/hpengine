// MODS (NOT ORIGINAL): existing community mods, not an HPEngine mod API. A mod is a folder
// <install>/Mods/<name>/ with the mod's files as it ships them: either laid out like the game (System/, Textures/ ...)
// or flat, the way HP1 mods usually come ("copy my packages into the System folder": AdamJD's Movement and Camera mod
// is eight .u files and a readme). A flat file goes where the game itself looks for its extension: the install's
// Default.ini [Core.System] Paths (../System/*.u, ../Maps/*.unr, ../Textures/*.utx, ../Sounds/*.uax, ../Music/*.umx);
// localization *.int goes to System, where Localize reads it (appBaseDir). Other flat files (readme ...) are not game
// data: listed in Result.Ignored and logged. A file in a subfolder keeps its relative path; when the install has no
// game directory with that top-level name (a mod unpacked one level too deep: Mods/X/X-v1/System/...), the game never
// opens it: listed in Result.Misplaced and logged, never re-placed by guess. A mod's files replace the game's files with the same relative path or
// are added next to them. <data>/Mods.ini turns mods on:
//
//     [Mods]
//     Enabled=<name>      ; one line per mod, in order: a later mod wins over an earlier one
//
// With no mod or data language selected the engine runs on the install itself, exactly as before. Otherwise a working
// copy <data>/modroot is rebuilt at every start: every game directory of the install as a real directory of links to its
// files, then GameLanguage's selected files, then the enabled mods' files linked over them. Paths are matched case-insensitively, as on the shipped
// Windows file system (a mod's "system/engine.u" replaces "System/Engine.u"). Core boots from the working copy and
// gets only the enabled names (UEGameEngine.Boot mods), a label for its saves: a save records them with the GUIDs of the
// script packages it depends on, and a save that does not fit the current scripts is refused before the old level is
// torn down (UELevelRuntime.SaveScriptMismatch). Saves are written and read in <install>/Save (Boot saveDir), settings and
// logs stay in <data>. Save/ is still linked into the working copy like game data: the slot pictures the installer put
// there ("..\Save\SGS <level>.bmp", DefaultGameSnap.bmp) are opened relative to System/ (native 321, UETextureImport).
// Only the windowed game uses mods: snapshots, probes and judges always run on the install (HPEngineProgram).
// Links: symbolic on macOS and Linux; hard links on Windows, with independent writable copies for read-only files
// or files on a volume that cannot be hard-linked. The working copy holds only real directories and files, so
// deleting it never reaches a target. Attributes on an original file or hard link are never changed.
// Problems are loud (ModOverlayException, exit code 5): a missing or empty enabled mod is not skipped silently.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace HPEngine.Packaging
{
    public sealed class ModOverlayException : Exception
    {
        public ModOverlayException(string message) : base(message) { }
    }

    public static class ModOverlay
    {
        public const int ExitCode = 5;
        public const string ModsDirName = "Mods";
        public const string ConfigName = "Mods.ini";
        public const string WorkDirName = "modroot";

        // Install directories that are not game data: never linked into the working copy.
        static readonly string[] NotGameData = ModInstall.NotGameData;

        public sealed record Result(string GameRoot, IReadOnlyList<string> Active, int Replaced, int Added,
            IReadOnlyList<string> Ignored, IReadOnlyList<string> Misplaced, IReadOnlyList<string> Newer = null);

        // The enabled mod names from <data>/Mods.ini, in order; none when the file is missing.
        public static List<string> Enabled(string dataDir)
        {
            var names = new List<string>();
            string path = Path.Combine(dataDir, ConfigName);
            if (!File.Exists(path)) return names;
            bool inMods = false;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Split(';')[0].Trim();
                if (line.StartsWith('[')) { inMods = line.Equals("[Mods]", StringComparison.OrdinalIgnoreCase); continue; }
                if (!inMods) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0 || !line[..eq].Trim().Equals("Enabled", StringComparison.OrdinalIgnoreCase)) continue;
                string name = line[(eq + 1)..].Trim();
                if (name.Length > 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
            }
            return names;
        }

        // The game root for Core: the install itself with no mod enabled, else the rebuilt working copy.
        public static Result Prepare(string install, string dataDir)
        {
            List<string> enabled = Enabled(dataDir);
            var language = GameLanguage.Files(install);
            string work = Path.Combine(dataDir, WorkDirName);
            if (Directory.Exists(work)) Directory.Delete(work, true);   // links and real directories only (see header)
            if (enabled.Count == 0 && language.Count == 0) return new Result(install, enabled, 0, 0, Array.Empty<string>(), Array.Empty<string>());

            var ignored = new List<string>();
            var misplaced = new List<string>();
            var mods = Collect(install, dataDir, enabled, ignored, misplaced);
            mods.Insert(0, language);   // language data first; enabled community mods still win, and keep their save identity

            // relative path (lower case, '/') -> path in the working copy, so a mod lands on the install's spelling
            var placed = new Dictionary<string, string>(StringComparer.Ordinal);
            Directory.CreateDirectory(work);
            foreach (string file in GameFiles(install))
            {
                string rel = Path.GetRelativePath(install, file);
                string dest = Path.Combine(work, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                Link(dest, file);
                placed[Key(rel)] = dest;
            }
            int replaced = 0, added = 0;
            for (int m = 0; m < mods.Count; m++)
                foreach (var (file, rel) in mods[m])
                {
                    if (placed.TryGetValue(Key(rel), out string dest))
                    {
                        File.Delete(dest);   // the link, not the install's file
                        if (m > 0) replaced++;   // the language layer is not a community mod
                    }
                    else
                    {
                        dest = Path.Combine(DirIn(work, Path.GetDirectoryName(rel)), Path.GetFileName(rel));
                        placed[Key(rel)] = dest;
                        if (m > 0) added++;
                    }
                    Link(dest, file);
                }
            return new Result(work, enabled, replaced, added, ignored, misplaced, Newer(install, mods.Skip(1)));
        }

        // The enabled mods' Unreal packages saved with a newer package version than the install's System/Engine.u (e.g. HP2
        // maps made with a later community engine: versions 93..110 against the retail 79), as "<rel> (<version>)". Only
        // named in the log (HPEngineProgram): some such files load (HP1 mods carry 79 and 126 against 76), so nothing is refused.
        static List<string> Newer(string install, IEnumerable<List<(string Source, string Rel)>> mods)
        {
            static int Version(string f) => PackageVersion(f);
            string engine = FindDir(install, "System") is string sys ? FindFile(sys, "Engine.u") : null;
            int game = engine is null ? -1 : Version(engine);
            if (game < 0) return new List<string>();
            return mods.SelectMany(m => m).Select(f => (f.Rel, V: Version(f.Source))).Where(f => f.V > game)
                .Select(f => $"{f.Rel.Replace('\\', '/')} ({f.V} > {game})").ToList();
        }

        // The maps the enabled mods ADD: Maps/<name>.unr (where Core's Boot opens a map) by Prepare's placement, with no
        // file of that relative path in the install. A replaced shipped map is not one. By name; a map two mods add is
        // the later mod's, as in the working copy. Reads only (the Launchers' "Start map" list; mirrored in
        // launcher/Launcher.swift addedMaps).
        public static List<(string Mod, string Map)> AddedMaps(string install, string dataDir)
        {
            List<string> enabled = Enabled(dataDir);
            var maps = new Dictionary<string, (string Mod, string Map)>(StringComparer.Ordinal);
            if (enabled.Count == 0) return maps.Values.ToList();
            var mods = Collect(install, dataDir, enabled, new List<string>(), new List<string>());
            var shipped = GameFiles(install).Select(f => Key(Path.GetRelativePath(install, f))).ToHashSet(StringComparer.Ordinal);
            for (int i = 0; i < mods.Count; i++)
                foreach (var (_, rel) in mods[i])
                {
                    string key = Key(rel);
                    if (key.StartsWith("maps/") && key.EndsWith(".unr") && key.IndexOf('/', 5) < 0 && !shipped.Contains(key))
                        maps[key] = (enabled[i], Path.GetFileNameWithoutExtension(rel));
                }
            return maps.Values.OrderBy(m => m.Map, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // Files that two enabled mods both place at the same relative path (Prepare's placement and its case-insensitive
        // Key): the later mod's file is the one in the working copy. One entry per (earlier, later) pair, in mod order,
        // with the paths sorted without case; a file three mods carry is reported for each step (A
        // then B, B then C). Reads only (the Launchers' Mods card; mirrored in launcher/Launcher.swift modConflicts).
        public static List<(string Earlier, string Later, List<string> Files)> Conflicts(string install, string dataDir)
        {
            List<string> enabled = Enabled(dataDir);
            var pairs = new List<(string Earlier, string Later, List<string> Files)>();
            if (enabled.Count < 2) return pairs;
            var mods = Collect(install, dataDir, enabled, new List<string>(), new List<string>());
            var owner = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < mods.Count; i++)
                foreach (var (_, rel) in mods[i])
                {
                    string key = Key(rel);   // without case, as Prepare places it
                    if (owner.TryGetValue(key, out int j) && j != i)
                    {
                        int at = pairs.FindIndex(p => p.Earlier == enabled[j] && p.Later == enabled[i]);
                        if (at < 0) { pairs.Add((enabled[j], enabled[i], new List<string>())); at = pairs.Count - 1; }
                        pairs[at].Files.Add(rel.Replace('\\', '/'));
                    }
                    owner[key] = i;
                }
            foreach (var p in pairs) p.Files.Sort(StringComparer.OrdinalIgnoreCase);
            return pairs;
        }

        // Every file of the install's game directories.
        static IEnumerable<string> GameFiles(string install) =>
            Directory.GetDirectories(install).Where(d => !NotGameData.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
                .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories));

        // The placement rule (header): per enabled mod, in order, its game files as (file, relative path in the game).
        static List<List<(string Source, string Rel)>> Collect(string install, string dataDir, List<string> enabled,
            List<string> ignored, List<string> misplaced)
        {
            var mods = new List<List<(string Source, string Rel)>>();
            string modsDir = Path.Combine(install, ModsDirName);
            var gameDirs = Directory.GetDirectories(install).Select(Path.GetFileName)
                .Where(d => !NotGameData.Contains(d, StringComparer.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> extDirs = null;
            foreach (string name in enabled)
            {
                string dir = FindDir(modsDir, name)
                    ?? throw new ModOverlayException($"mod \"{name}\" is enabled in {Path.Combine(dataDir, ConfigName)} " +
                        $"but there is no folder {Path.Combine(install, ModsDirName, name)}");
                var files = Directory.GetDirectories(dir)
                    .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
                    .Where(f => !Path.GetFileName(f).StartsWith('.'))
                    .Select(f => (f, Path.GetRelativePath(dir, f))).ToList();
                misplaced.AddRange(files.Where(f => !gameDirs.Contains(f.Item2.Split(Path.DirectorySeparatorChar)[0]))
                    .Select(f => Path.GetRelativePath(modsDir, f.f)));
                foreach (string f in Directory.GetFiles(dir).Where(f => !Path.GetFileName(f).StartsWith('.')))
                {
                    extDirs ??= ExtDirs(install);
                    if (extDirs.TryGetValue(Path.GetExtension(f), out string gameDir)) files.Add((f, Path.Combine(gameDir, Path.GetFileName(f))));
                    else ignored.Add(Path.GetRelativePath(modsDir, f));
                }
                if (files.Count == 0) throw new ModOverlayException($"mod \"{name}\": {dir} has no game files");
                mods.Add(files);
            }
            return mods;
        }

        // Extension -> game directory from the install's Default.ini [Core.System] Paths=../<Dir>/*.<ext> (the first line
        // for an extension wins, as in the search order), plus .int -> System. Same file as Core's DefaultIniPath:
        // System/Default.ini, else System/0/Default.ini.
        static Dictionary<string, string> ExtDirs(string install)
        {
            string system = FindDir(install, "System");
            string ini = system is null ? null
                : FindFile(system, "Default.ini") ?? (FindDir(system, "0") is string zero ? FindFile(zero, "Default.ini") : null);
            if (ini is null) throw new ModOverlayException($"cannot place a mod's loose files: no Default.ini in {install}/System");
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [".int"] = "System" };
            bool inSystem = false;
            foreach (string raw in File.ReadAllLines(ini))
            {
                string line = raw.Trim();
                if (line.StartsWith('[')) { inSystem = line.Equals("[Core.System]", StringComparison.OrdinalIgnoreCase); continue; }
                if (!inSystem || !line.StartsWith("Paths=", StringComparison.OrdinalIgnoreCase)) continue;
                string[] parts = line[6..].Replace('\\', '/').Split('/');   // ../System/*.u
                if (parts.Length == 3 && parts[0] == ".." && parts[2].StartsWith("*.") && !map.ContainsKey(parts[2][1..])
                    && !parts[1].Equals("Save", StringComparison.OrdinalIgnoreCase))
                    map[parts[2][1..]] = parts[1];
            }
            return map;
        }

        // HP2 (coordinator 2026-11-21, DECISIONS): a mod with a package newer than HP2's retail format does not run. The
        // version is Core's UEGameProfile.HP2.MaxPackageVersion (MIRROR; selftest game_profile_selected_from_install checks
        // the two agree); the Launchers and HPEngineProgram refuse such a mod by name before Core opens anything.
        public const int Hp2PackageVersion = 79;
        public static string NewerHp2Engine(string mod) => $"{mod} was made for a newer HP2 engine and does not run in HPEngine";

        // The first Unreal package (by its header) among the mod folder's files — every non-hidden file at any depth,
        // whether or not the game would open it — with a version above max, as "<path in the mod> (version <n>)"; null:
        // none. Same rule in launcher/Launcher.swift newerPackage.
        public static string NewerPackage(string modDir, int max)
        {
            foreach (string f in Directory.EnumerateFiles(modDir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            {
                if (Path.GetRelativePath(modDir, f).Split(Path.DirectorySeparatorChar).Any(p => p.StartsWith('.'))) continue;
                if (PackageVersion(f) is int v && v > max) return $"{Path.GetRelativePath(modDir, f).Replace('\\', '/')} (version {v})";
            }
            return null;
        }

        // The package version of an Unreal package file; -1: not a package (or unreadable).
        static int PackageVersion(string f)
        {
            try
            {
                using var s = File.OpenRead(f); var b = new byte[6];
                return s.Read(b, 0, 6) == 6 && BitConverter.ToUInt32(b, 0) == 0x9E2A83C1u ? BitConverter.ToUInt16(b, 4) : -1;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return -1; }
        }

        internal static string FindFile(string dir, string name) =>
            Directory.GetFiles(dir).FirstOrDefault(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase));

        static string Key(string rel) => rel.Replace('\\', '/').ToLowerInvariant();

        internal static string FindDir(string parent, string name) =>
            Directory.Exists(parent)
                ? Directory.GetDirectories(parent).FirstOrDefault(d => Path.GetFileName(d).Equals(name, StringComparison.OrdinalIgnoreCase))
                : null;

        // The directory for <rel> inside the working copy, reusing existing directories whatever their case.
        static string DirIn(string work, string rel)
        {
            string at = work;
            if (string.IsNullOrEmpty(rel)) return at;
            foreach (string part in rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                at = FindDir(at, part) ?? Directory.CreateDirectory(Path.Combine(at, part)).FullName;
            return at;
        }

        static void Link(string path, string target)
        {
            if (!OperatingSystem.IsWindows()) { File.CreateSymbolicLink(path, target); return; }
            // CD distributions mark their files read-only. Windows rejects their hard links (error 5), and a
            // read-only working file could not be replaced by a language/mod or deleted on the next start.
            if ((File.GetAttributes(target) & FileAttributes.ReadOnly) == 0)
            {
                if (CreateHardLinkW(path, target, IntPtr.Zero)) return;
                int error = Marshal.GetLastWin32Error();
                if (error is not (1 or 5 or 17 or 50))
                    throw new ModOverlayException($"cannot link {path} -> {target} (Windows error {error})");
            }
            File.Copy(target, path);
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);   // an independent copy only
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateHardLinkW(string newFileName, string existingFileName, IntPtr securityAttributes);
    }
}
