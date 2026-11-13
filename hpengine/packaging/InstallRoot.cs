// Locates the game install for HPEngine instead of a hard-coded path in Main.
//
// Order:
//   1. `--root <dir>` — if given, it and only it. A wrong --root is a loud error, NOT a silent fallback
//      to the executable's directory: a typo in an explicit path would otherwise pick up another install
//      unnoticed.
//   2. The executable's directory (the package is unpacked over the game install).
//   2b. The PARENT of the executable's directory: the release archive keeps the engine in `HPEngine/` beside the Launcher,
//      so unpacking it into the game folder gives `<game>/HPEngine/HPEngine` and the game is the parent.
//   3. `<executable's directory>/game` — where HP1Setup installs the data by default,
//      so a package unpacked into an empty folder runs after HP1Setup without --root. Checked after 2: a package
//      unpacked over the game keeps using that game.
//   4. Otherwise InstallRootException listing the checked places, with a hint.
//
// An install is recognised by the `System` and `Maps` subdirectories — exactly what the engine opens
// unconditionally when loading a world:
//   - HP1.Core/Runtime/World/UEGameEngine.cs  Path.Combine(gameRoot, "System") — *.u packages, ini;
//   - HP1.Core/Runtime/World/UEGameEngine.cs  Path.Combine(gameRoot, "Maps", mapName + ".unr");
//   - HP1.Core/Runtime/World/UELevelRuntime.Bootstrap.cs  System/0/Default.ini (File.ReadAllBytes).
// Core reads `Textures`, `Sounds`, `Music` only if present (UETextureSource,
// UESoundCatalog, UEMusicCatalog), so they are not install markers.
//
// NAME CASE. Core opens exactly "System" and "Maps" (Path.Combine with a literal). The check here does
// THE SAME (Directory.Exists on the same path), so its answer matches what Core sees on this file system:
// on a case-sensitive one (Linux, Steam Deck) a `system` directory works for neither Core nor us; on a
// case-insensitive one (Windows, default APFS) it works for both. If the exact name is missing but a
// different-case variant exists, the error names it explicitly instead of accepting it silently.
using System;
using System.Collections.Generic;
using System.IO;

namespace HPEngine.Packaging
{
    public sealed class InstallRootException : Exception
    {
        public InstallRootException(string message) : base(message) { }
    }

    public static class InstallRoot
    {
        // Main exit code when no install is found (HPEngineProgram.cs uses 2 and 3).
        public const int ExitCode = 4;

        // Subdirectories the engine opens unconditionally (references in the file header).
        public static readonly string[] RequiredDirs = { "System", "Maps" };

        // HP1Setup's default destination inside the executable's directory (step 3).
        public const string SetupDir = "game";

        // The player's settings overlay and run logs: <install>/HPEngineData. Not "HPEngine": the Linux/macOS package
        // unpacked over the game (step 2) puts the executable "HPEngine" into the same folder, and a file blocks a
        // directory of the same name (the game crashed at start on the Steam Deck).
        public const string DataDirName = "HPEngineData";

        // Creates <root>/HPEngineData and returns it.
        public static string DataDir(string root)
        {
            string dir = Path.Combine(root, DataDirName);
            Directory.CreateDirectory(dir);
            return dir;
        }

        const string Hint =
            "\nHint: the easy way is the HPEngine Launcher: it installs your copy of the game and starts it. Or run HP1Setup "
            + "next to HPEngine with your copy of the game, or unpack HPEngine into the game install directory (where System/ "
            + "and Maps/ are), or name it explicitly: HPEngine --root <game directory>.";

        // rootArg — the --root value (null if absent); exeDir — the executable's directory
        // (in Main: AppContext.BaseDirectory). Returns the full install path.
        public static string Resolve(string rootArg, string exeDir)
        {
            if (rootArg is not null)
            {
                string full = Path.GetFullPath(rootArg);
                string why = Missing(full);
                if (why is not null)
                    throw new InstallRootException($"--root {rootArg}: this is not a game install ({full}: {why})." + Hint);
                return full;
            }
            if (string.IsNullOrEmpty(exeDir)) throw new ArgumentNullException(nameof(exeDir));
            string dir = Path.GetFullPath(exeDir);
            string whyExe = Missing(dir);
            if (whyExe is null) return dir;
            string parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(dir));   // null at the file system root
            string whyParent = parent is null ? "there is no parent directory" : Missing(parent);
            if (whyParent is null) return parent;
            string setup = Path.Combine(dir, SetupDir);
            string whySetup = Missing(setup);
            if (whySetup is null) return setup;
            throw new InstallRootException(
                $"game install not found: --root not set, next to the executable ({dir}) {whyExe}, in its parent {parent} {whyParent}, and in {setup} {whySetup}." + Hint);
        }

        // null — the directory is a valid install; otherwise why not (all missing markers).
        public static string Missing(string dir)
        {
            if (!Directory.Exists(dir)) return "the directory does not exist";
            var problems = new List<string>();
            foreach (string sub in RequiredDirs)
            {
                if (Directory.Exists(Path.Combine(dir, sub))) continue;
                string variant = CaseVariant(dir, sub);
                problems.Add(variant is null
                    ? $"no directory {sub}/"
                    : $"no directory {sub}/ (there is \"{variant}/\", but the engine opens the name \"{sub}\" with exact case, and the file system is case-sensitive)");
            }
            return problems.Count == 0 ? null : string.Join("; ", problems);
        }

        static string CaseVariant(string dir, string sub)
        {
            foreach (string d in Directory.EnumerateDirectories(dir))
            {
                string name = Path.GetFileName(d);
                if (string.Equals(name, sub, StringComparison.OrdinalIgnoreCase)) return name;
            }
            return null;
        }
    }
}
