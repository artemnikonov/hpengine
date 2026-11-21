// MODS (NOT ORIGINAL): adding a community mod to <install>/Mods/<name>/ (ModOverlay.cs reads it), the one rule both
// Launchers use: launcher/Desktop calls it directly, launcher/Launcher.swift through HP1Setup --add-mod. Both games.
// The source is a folder (copied; links inside it are skipped, never followed), one loose game file (an Unreal package
// such as a lone Maps .unr, or a .int: Mods/<its name>/<file>, placed like any flat file), or an archive: .zip (read here), .7z /
// .rar (the system's bsdtar: macOS, Windows 10+, Linux where installed; it refuses absolute paths and ".."). Nothing of
// a mod is ever run: an installer (.exe ...) is refused. The name is the folder's / the archive's file name without its
// extension; a mod of that name already there (without case, as ModOverlay.FindDir) is refused, never merged.
// One wrapper folder is dropped: while the mod holds exactly one folder and nothing else (hidden files and __MACOSX
// aside) and that folder is not a game directory of the install (System, Maps, ...), its contents are the mod — the
// archive "Mod-v1.zip/Mod-v1/System/..." installs as Mods/Mod-v1/System/... . No other reshaping: files the game would
// not open stay where the mod put them, and the Mods page names them (ModOverlay.Result.Misplaced).
// The mod is unpacked into Mods/.adding-<id> and moved into place at the end: an interrupted add leaves no half mod.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace HPEngine.Packaging
{
    public static class ModInstall
    {
        // Install directories that are not game data (ModOverlay never links them into the working copy).
        public static readonly string[] NotGameData = { "Mods", InstallRoot.DataDirName, "HPEngine" };

        // Adds the mod and returns its name. InvalidDataException: refused (the message says why, for the Launchers).
        public static string Add(string install, string source)
        {
            source = Path.GetFullPath(Path.TrimEndingDirectorySeparator(source));
            bool dir = Directory.Exists(source);
            if (!dir && !File.Exists(source)) throw new InvalidDataException($"There is no file or folder {source}.");
            string ext = dir ? "" : Path.GetExtension(source).ToLowerInvariant();
            bool single = !dir && (ext == ".int" || IsPackage(source));   // one loose game file (a map, a package): a mod of its own
            if (!dir && !single && ext is not (".zip" or ".7z" or ".rar"))
                throw new InvalidDataException($"{Path.GetFileName(source)} is not a mod folder, a game package or a .zip, .7z or .rar archive. "
                    + "HPEngine never runs a mod's installer: unpack the mod first, then add its folder.");
            string name = dir ? Path.GetFileName(source) : Path.GetFileNameWithoutExtension(source);
            string mods = Path.Combine(install, "Mods");
            Directory.CreateDirectory(mods);
            if (FindDir(mods, name) is string there)
                throw new InvalidDataException($"There is already a mod named {Path.GetFileName(there)}. Remove it first to add this one.");
            string temp = Path.Combine(mods, ".adding-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(temp);
                if (dir) Copy(source, temp);
                else if (single) File.Copy(source, Path.Combine(temp, Path.GetFileName(source)));
                else if (ext == ".zip")
                {
                    try { ZipFile.ExtractToDirectory(source, temp); }
                    catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
                    { throw new InvalidDataException($"Could not read {Path.GetFileName(source)}: {e.Message}"); }
                }
                else Untar(source, temp);
                var gameDirs = Directory.GetDirectories(install).Select(Path.GetFileName)
                    .Where(d => !NotGameData.Contains(d, StringComparer.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                string top = temp;
                while (Visible(top) is [string only] && Directory.Exists(only) && !IsLink(only) && !gameDirs.Contains(Path.GetFileName(only))) top = only;
                if (!Directory.EnumerateFiles(top, "*", SearchOption.AllDirectories).Any())
                    throw new InvalidDataException($"{Path.GetFileName(source)} holds no files.");
                Directory.Move(top, Path.Combine(mods, name));
                return name;
            }
            finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); }   // what is left of the unpack (links deleted as links)
        }

        // Deletes Mods/<name> (links inside deleted as links, never followed). Turning it off in Mods.ini is the Launcher's.
        public static void Remove(string install, string name)
        {
            if (FindDir(Path.Combine(install, "Mods"), name) is string dir) Directory.Delete(dir, true);
        }

        static string FindDir(string parent, string name) =>
            Directory.Exists(parent)
                ? Directory.GetDirectories(parent).FirstOrDefault(d => Path.GetFileName(d).Equals(name, StringComparison.OrdinalIgnoreCase))
                : null;

        static bool IsPackage(string f)
        {
            try { using var s = File.OpenRead(f); var b = new byte[4]; return s.Read(b, 0, 4) == 4 && BitConverter.ToUInt32(b, 0) == 0x9E2A83C1u; }
            catch (IOException) { return false; }
        }

        static bool IsLink(string p) => (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0;

        static string[] Visible(string dir) => Directory.GetFileSystemEntries(dir)
            .Where(e => !Path.GetFileName(e).StartsWith('.') && !Path.GetFileName(e).Equals("__MACOSX", StringComparison.OrdinalIgnoreCase)).ToArray();

        static void Copy(string from, string to)
        {
            foreach (string e in Directory.GetFileSystemEntries(from))
            {
                if (IsLink(e)) continue;
                string dest = Path.Combine(to, Path.GetFileName(e));
                if (Directory.Exists(e)) { Directory.CreateDirectory(dest); Copy(e, dest); }
                else File.Copy(e, dest);
            }
        }

        static void Untar(string archive, string to)
        {
            string exe = OperatingSystem.IsWindows() ? ".exe" : "";
            string tool = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(d => d.Length > 0)
                .SelectMany(d => new[] { Path.Combine(d, "bsdtar" + exe), OperatingSystem.IsLinux() ? null : Path.Combine(d, "tar" + exe) })
                .FirstOrDefault(p => p != null && File.Exists(p));
            if (tool != null)
            {
                var psi = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (string a in new[] { "-x", "-f", archive, "-C", to }) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi);
                var err = p.StandardError.ReadToEndAsync(); p.StandardOutput.ReadToEnd(); err.Wait(); p.WaitForExit();
                if (p.ExitCode == 0) return;
            }
            throw new InvalidDataException($"This archive can't be opened here: {Path.GetFileName(archive)}. Unpack it first, then add the unpacked folder.");
        }
    }
}
