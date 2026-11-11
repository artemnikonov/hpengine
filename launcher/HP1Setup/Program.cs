// HP1Setup — installs the game data for OpenHP1 from the player's own copy of Harry Potter (2001, PC).
//
//   HP1Setup [--dest <dir>] <source> [<source> ...]
//   HP1Setup                        (in a terminal: asks for the source)
//   HP1Setup --unpack-cab <data1.hdr> <dir>   (test: only unpacks an InstallShield cabinet)
//   HP1Setup --check-manifests               (test: the manifests agree with UEEdition)
//
// <source>: the installed game folder, the game CD (its drive or mount point), a folder copy of the CD, a .zip of
// those, (macOS only) a disc image .iso / .bin+.cue / .mdf+.mds, or a game installer: Inno Setup (Setup.exe with its
// Setup-1.bin, unpacked by the bundled innoextract), InstallShield cabinets (data1.hdr + data1.cab ..., read by
// InstallShield.cs), or a .7z / .rar (by the system's bsdtar where there is one: macOS, Windows 10+). A folder with
// System/ and Maps/ up to 3 levels down is found; without one, archives and installers up to 3 levels down are
// unpacked (into temporary folders, removed at the end), also inside each other up to 3 deep. Nothing from the source
// is ever executed and the source is never written.
// <dest>: the install root OpenHP1 runs with. Default: `game/` in this executable's directory, i.e. next to OpenHP1
// (openhp1/packaging/InstallRoot.cs, step 3), so after setup `OpenHP1` starts without arguments. With no --dest and a
// game already next to this executable (the package unpacked into the game folder) nothing is needed: refused.
//
// SAFETY: a <dest> that holds game data (System, Maps, Textures, Sounds, Music or Help, any case) not installed by
// HP1Setup (no .hp1-installed or .hp1-installing) is refused before anything is read: an original game install is
// never changed. <dest>/Save and <dest>/OpenHP1Data (settings, logs; OpenHP1 before 2026-11-08) are never deleted or overwritten.
//
// Check: the edition is Core's UEEdition (linked from HP1.Core: the same SHA-1 keys, System/Engine.dll + Render.dll +
// HPBase.u, so HP1Setup and Core never disagree), and so is its support tier (UEEdition.Support). A supported edition
// (US 1.53, UK 1.1, RU EA) must then match its manifest fully — every file of us153.sha1 / uk11.sha1 / ru_ea.sha1 present with that size and SHA-1 (the top-level
// packages, maps, textures, sounds, music and DLLs the port loads; the CD and an installed copy share them). The game
// exe is neither checked nor copied in any edition (the port never runs it); it only names the No-CD variant. A release
// with modified game scripts (a listed one, or a listed DLL build with another HPBase.u), or a supported edition whose
// files differ from its manifest, is installed with WARNING modified (author decision 2026-11-10); an unknown one
// (Unverified, author decision 2026-11-09) is installed without a manifest, with a WARNING naming the hashes to report.
// The two ini files Core opens unconditionally must exist.
// Copy: System, Maps, Textures, Sounds, Music, Help as found, without *.exe; checked files and the ini files get their
// canonical name case (Linux: Core opens System/DefUser.ini by exact name; the rest it finds in any case through
// UEInstallPath). .hp1-installing is written first. Each directory is staged as <Dir>.new; then the old one is moved
// to <Dir>.old and <Dir>.new takes its place; .hp1-installed is (re)written after all swaps, then .hp1-installing and
// the <Dir>.old backups are removed. An interrupted run is resumed by the next one (a lone <Dir>.old is moved back).
// Save/ is merged: files already there are kept.
//
// Output, one per line (read by launcher/Launcher.swift): STEP <find|unpack|check|copy>, PROGRESS <0..100>,
// EDITION <UEEdition id> <edition: support tier>, WARNING <key> <text for the player>, ERROR <key> <text for the player>, DONE <dest>. Exit code: 0 done, 2 the source was rejected, 1 other failure.
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using HP1.UEScript;

static class Program
{
    sealed class Reject(string key, string text) : Exception(text) { public readonly string Key = key; }

    sealed record Entry(string Sha1, long Size, string Path);   // Path: canonical case, '/'-separated

    static readonly string[] DataDirs = { "System", "Maps", "Textures", "Sounds", "Music", "Help" };
    const string Marker = ".hp1-installed", Installing = ".hp1-installing";
    // Files Core opens by exact name (HP1.Core/Runtime/World/UELevelRuntime.Bootstrap.cs InstallInis,
    // UEGameEngineNatives.cs: System/DefUser.ini) or that the Check names; copied with this case.
    static readonly string[] CanonIni = { "System/DefUser.ini", "System/Default.ini", "System/0/Default.ini" };

    // Manifest and name of every data set UEEdition rates Exact or Supported (the tier is UEEdition's).
    static readonly Dictionary<UEEdition.DataSet, (string manifest, string name)> Editions = new()
    {
        [UEEdition.DataSet.US153] = ("us153.sha1", "US 1.53"),
        [UEEdition.DataSet.UK11] = ("uk11.sha1", "UK 1.1"),
        [UEEdition.DataSet.RU_EA] = ("ru_ea.sha1", "RU EA"),
    };

    const string NothingFound = "No Harry Potter game files found here. Add the installed game folder, the game CD or its image "
        + "(.iso, .bin/.cue, .mdf), its installer (Setup.exe with its .bin files, or the setup folder with data1.cab), "
        + "or a copy of the CD (a folder or .zip with System and Maps inside).";

    static readonly List<string> Mounts = new(), Temps = new();
    static Process _child;   // a running unpacker, stopped on SIGTERM before its output folder is removed
    const int MaxNest = 3;   // archives or installers inside each other
    static Reject _hint;   // why a source gave nothing (archive, image), reported if no source is usable

    static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--unpack-cab")   // launcher/test_setup.sh: InstallShield.cs against unshield
        {
            Console.WriteLine($"{InstallShield.Extract(args[1], args[2])} files");
            return 0;
        }
        if (args.Length == 1 && args[0] == "--check-manifests") return CheckManifests();   // launcher/test_setup.sh
        if (args.Length == 1 && args[0] is "--help" or "-h" or "/?")
        {
            Console.WriteLine("HP1Setup installs the game data for OpenHP1 from your own copy of Harry Potter and the Philosopher's Stone (PC, 2001).");
            Console.WriteLine("Usage: HP1Setup [--dest <dir>] <source> [<source> ...]");
            Console.WriteLine("  <source>  the installed game folder, the game CD, a copy of the CD, the game's installer, or a .zip of those");
            Console.WriteLine("  --dest    where to install (default: game/ next to HP1Setup, where OpenHP1 finds it)");
            Console.WriteLine("Without arguments, in a terminal, it asks for the source. Your game files are only read, never changed.");
            return 0;
        }
        string dest = null;
        var sources = new List<string>();
        for (int i = 0; i < args.Length; i++)
            if (args[i] == "--dest" && i + 1 < args.Length) dest = args[++i];
            else sources.Add(args[i]);
        if (dest is null && IsRoot(AppContext.BaseDirectory))
        {
            Console.WriteLine("ERROR already_installed OpenHP1 is in a game folder already (System and Maps are next to it): "
                + "HP1Setup is not needed here. Start OpenHP1.");
            return 2;
        }
        dest ??= Path.Combine(AppContext.BaseDirectory, "game");   // InstallRoot.SetupDir
        bool interactive = sources.Count == 0 && !Console.IsInputRedirected;
        if (interactive)
        {
            Console.WriteLine("HP1Setup installs the game data for OpenHP1 from your own copy of Harry Potter and the "
                + "Philosopher's Stone (PC, 2001: US 1.53, UK 1.1 or the Russian EA release).\nDrag the installed game folder or the game CD "
                + "(or a copy of it, or a .zip of it) into this window and press Enter:");
            Console.Write("> ");
            string line = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(line)) sources.Add(line);
        }
        // The launcher stops a running setup with SIGTERM: detach images and remove temporary copies first.
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => Cleanup());
        int code;
        try { code = Run(Path.GetFullPath(dest), sources); }
        catch (Reject r) { Console.WriteLine($"ERROR {r.Key} {r.Message}"); code = 2; }
        catch (Exception e) { Console.WriteLine($"ERROR failed {e.Message}"); code = 1; }
        finally { Cleanup(); }
        if (interactive)
        {
            Console.WriteLine(code == 0 ? "Done. Start OpenHP1." : "Nothing was installed.");
            Console.WriteLine("Press Enter to close.");
            Console.ReadLine();
        }
        return code;
    }

    static int Run(string dest, List<string> sources)
    {
        RefuseForeign(dest);
        Console.WriteLine("STEP find");
        string root = null;
        List<Entry> manifest = null;
        Reject first = null;
        foreach (string s in sources)
        {
            foreach (string cand in Roots(Path.GetFullPath(Unquote(s))))
            {
                Console.WriteLine("STEP check");
                try { manifest = Check(cand); root = cand; break; }
                catch (Reject r) { first ??= r; }
            }
            if (root is not null) break;
        }
        if (root is null) throw first ?? _hint ?? new Reject("nothing_found", NothingFound);

        Console.WriteLine("STEP copy");
        bool same = string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(dest),
            StringComparison.OrdinalIgnoreCase);
        if (!same) Copy(root, dest, manifest);   // the source is this install itself (ours: RefuseForeign): checked
        Console.WriteLine("DONE " + dest);
        return 0;
    }

    static List<Entry> LoadManifest(string name)
    {
        using var s = typeof(Program).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(name + " is not embedded");
        var list = new List<Entry>();
        foreach (string line in new StreamReader(s).ReadToEnd().Split('\n'))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            string[] p = line.TrimEnd('\r').Split(' ', 3);
            list.Add(new Entry(p[0], long.Parse(p[1]), p[2]));
        }
        return list;
    }

    // Every manifest's key files classify, in UEEdition, to the edition the manifest is for, and every data set
    // UEEdition rates Exact or Supported has a manifest.
    static int CheckManifests()
    {
        int bad = 0;
        foreach (var d in Enum.GetValues<UEEdition.DataSet>())
            if (UEEdition.TierOf(d) is UEEdition.Support.Exact or UEEdition.Support.Supported && !Editions.ContainsKey(d))
            { Console.WriteLine($"{d}: {UEEdition.TierOf(d)} without a manifest"); bad++; }
        foreach (var (data, ed) in Editions)
        {
            var m = LoadManifest(ed.manifest).ToDictionary(e => e.Path, e => e.Sha1, StringComparer.OrdinalIgnoreCase);
            var got = UEEdition.DataOf(m.GetValueOrDefault("System/Engine.dll"), m.GetValueOrDefault("System/Render.dll"),
                m.GetValueOrDefault("System/HPBase.u"));
            Console.WriteLine($"{ed.manifest}: {got}{(got == data ? "" : $", want {data}")}");
            if (got != data) bad++;
        }
        return bad == 0 ? 0 : 1;
    }

    // ---- finding the game in a source ----

    static IEnumerable<string> Roots(string p, int nest = 0)
    {
        if (Directory.Exists(p))
        {
            var found = new List<string>();
            FindRoots(p, 3, found);
            if (found.Count > 0) { foreach (string f in found) yield return f; yield break; }
            if (nest >= MaxNest) yield break;
            var scan = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true };
            // In name order (the same on every system): the first reason a candidate gave nothing is the one reported.
            foreach (var dir in Directory.EnumerateFiles(p, "*", scan).Order(StringComparer.Ordinal).GroupBy(Path.GetDirectoryName))
            {
                var files = dir.ToList();
                bool sheet = files.Any(f => Ext(f) is ".cue" or ".mds" or ".iso");   // .bin/.mdf next to it is the same disc
                foreach (string f in files)
                    if (Ext(f) is ".zip" or ".iso" or ".cue" or ".mds" or ".7z" or ".rar" or ".exe" || IsCabHeader(f)
                        || (!sheet && Ext(f) is ".bin" or ".mdf" or ".img" && !IsInnoVolume(f)))
                        foreach (string r in Roots(f, nest + 1)) yield return r;
            }
            yield break;
        }
        if (!File.Exists(p)) { _hint ??= new Reject("not_found", "Not found: " + p); yield break; }
        string o = null;
        switch (Ext(p))
        {
            case ".zip": o = Extract(p); break;
            case ".iso": case ".bin": case ".img": case ".mdf": case ".cue": case ".mds": o = Mount(p); break;
            case ".rar": case ".7z": o = Untar(p); break;
            default:   // a file inside the copy (Setup.exe, AutoRun.exe, System/HP.exe): its folder or one above
                string d = Path.GetDirectoryName(p);
                for (int k = 0; k < 3 && d is not null; k++, d = Path.GetDirectoryName(d))
                    if (IsRoot(d)) { o = d; break; }
                if (o is null && Ext(p) == ".exe") o = Inno(p);
                else if (o is null && IsCabHeader(p)) o = Cab(p);
                break;
        }
        if (o is not null) foreach (string r in Roots(o, nest)) yield return r;
    }

    // <prefix>1.hdr, or <prefix>1.cab without a .hdr next to it (InstallShield keeps the header in the first cabinet
    // then); <prefix> has no digits (InstallShield.cs names the volumes <prefix>N).
    static bool IsCabHeader(string f)
    {
        string n = Path.GetFileName(f).ToLowerInvariant();
        if (n.Length < 6 || n[^5] != '1' || n[..^5].Any(char.IsAsciiDigit)) return false;
        return n.EndsWith(".hdr") || (n.EndsWith(".cab") && FileCI(Path.GetDirectoryName(f), n[..^4] + ".hdr") is null);
    }

    // Setup-1.bin next to Setup.exe: a volume of an Inno Setup installer, not a disc image.
    static bool IsInnoVolume(string f)
    {
        string n = Path.GetFileNameWithoutExtension(f);
        int dash = n.LastIndexOf('-');
        return dash > 0 && n[(dash + 1)..].All(char.IsAsciiDigit) && FileCI(Path.GetDirectoryName(f), n[..dash] + ".exe") is not null;
    }

    static void FindRoots(string dir, int depth, List<string> found)
    {
        if (IsRoot(dir)) { found.Add(dir); return; }
        if (depth == 0) return;
        IEnumerable<string> subs;
        try { subs = Directory.EnumerateDirectories(dir).ToList(); } catch (UnauthorizedAccessException) { return; }
        foreach (string s in subs) FindRoots(s, depth - 1, found);
    }

    static bool IsRoot(string d) => DirCI(d, "System") is not null && DirCI(d, "Maps") is not null;

    static string Ext(string f) => Path.GetExtension(f).ToLowerInvariant();

    static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[^1] == s[0]) s = s[1..^1];
        else if (!OperatingSystem.IsWindows()) s = s.Replace("\\ ", " ");   // a terminal's drag-and-drop escaping
        return s;
    }

    // Directory or file `name` in `parent`, exact name first, otherwise any case (a CD or a copy may differ).
    static string DirCI(string parent, string name) => EntryCI(parent, name, dir: true);
    static string FileCI(string parent, string name) => EntryCI(parent, name, dir: false);
    static string EntryCI(string parent, string name, bool dir)
    {
        if (parent is null || !Directory.Exists(parent)) return null;
        string exact = Path.Combine(parent, name);
        if (dir ? Directory.Exists(exact) : File.Exists(exact)) return exact;
        try
        {
            return (dir ? Directory.EnumerateDirectories(parent) : Directory.EnumerateFiles(parent))
                .FirstOrDefault(e => string.Equals(Path.GetFileName(e), name, StringComparison.OrdinalIgnoreCase));
        }
        catch (UnauthorizedAccessException) { return null; }
    }

    static string NewTemp()
    {
        string t = Path.Combine(Path.GetTempPath(), "hp1setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(t);
        Temps.Add(t);
        return t;
    }

    static string Extract(string zip)
    {
        Console.WriteLine("STEP unpack");
        string o = NewTemp();
        try { ZipFile.ExtractToDirectory(zip, o); return o; }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _hint ??= new Reject("unpack_failed", $"Could not read {Path.GetFileName(zip)}: {e.Message}");
            return null;
        }
    }

    // Inno Setup: innoextract (bundled in tools/ by launcher/third_party.sh; else on PATH) reads Setup.exe and its
    // Setup-N.bin; it never runs them. Its own temporary files are left out; two different files for one path are an error.
    static string Inno(string exe)
    {
        string tool = Tool("innoextract");
        if (tool is null)
        {
            _hint ??= new Reject("unpack_tool_missing", $"{Path.GetFileName(exe)} may be a game installer, but the unpacker "
                + "(innoextract) is missing next to HP1Setup. Reinstall OpenHP1, or install the game and add its folder.");
            return null;
        }
        Console.WriteLine("STEP unpack");
        string o = NewTemp();
        var (code, err) = Exec(tool, "--extract", "--silent", "--exclude-temp", "--collisions=error", "--progress=0",
            "--output-dir", o, exe);
        if (code == 0) return Unpacked(o, exe);
        if (!err.Contains("Not a supported Inno Setup installer"))   // any other program: not an installer, no hint
            _hint ??= new Reject("unpack_failed", $"Could not unpack the installer {Path.GetFileName(exe)}: "
                + string.Join(" ", err.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(2)));
        return null;
    }

    static string Cab(string header)
    {
        Console.WriteLine("STEP unpack");
        string o = NewTemp();
        try { InstallShield.Extract(header, o); return Unpacked(o, header); }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException
                                  or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            _hint ??= new Reject("unpack_failed", $"Could not read the InstallShield cabinet {Path.GetFileName(header)}: {e.Message}");
            return null;
        }
    }

    // An installer that unpacked into something without the game folder: said by name if nothing else is found.
    static string Unpacked(string o, string installer)
    {
        var found = new List<string>();
        FindRoots(o, 3, found);
        if (found.Count == 0)
            _hint ??= new Reject("installer_layout", $"The installer {Path.GetFileName(installer)} was unpacked, but it "
                + "holds no game folder (System and Maps). Install the game, then add the installed game folder.");
        return o;
    }

    // .7z / .rar: bsdtar (libarchive) of the system — /usr/bin/tar on macOS, tar.exe on Windows 10+, bsdtar on Linux if
    // installed. It refuses absolute paths and ".." on its own (no -P).
    static string Untar(string archive)
    {
        string tool = Tool("bsdtar") ?? (OperatingSystem.IsLinux() ? null : Tool("tar"));
        if (tool is not null)
        {
            Console.WriteLine("STEP unpack");
            string o = NewTemp();
            if (Exec(tool, "-x", "-f", archive, "-C", o).code == 0) return o;
        }
        _hint ??= new Reject("unpack_archive", $"This archive can't be opened here: {Path.GetFileName(archive)}. "
            + "Unpack it first, then add the unpacked folder.");
        return null;
    }

    static string Tool(string name)
    {
        string exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
        string bundled = Path.Combine(AppContext.BaseDirectory, "tools", exe);
        if (File.Exists(bundled)) return bundled;
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(d => d.Length > 0).Select(d => Path.Combine(d, exe)).FirstOrDefault(File.Exists);
    }

    static (int code, string err) Exec(string tool, params string[] args)
    {
        var psi = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (string a in args) psi.ArgumentList.Add(a);
        Process p;
        lock (Mounts) p = _child = Process.Start(psi)!;
        p.StandardInput.Close();
        var err = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        lock (Mounts) _child = null;
        return (p.ExitCode, err.Result);
    }

    // macOS: hdiutil, read-only. A raw 2352-byte-sector image (.bin/.mdf/.img) is first converted to plain 2048-byte
    // sectors (mode 1: data at byte 16; mode 2 form 1: at byte 24).
    static string Mount(string img)
    {
        string name = Path.GetFileName(img);
        if (!OperatingSystem.IsMacOS())
        {
            _hint ??= new Reject("mount_first", $"Disc images can't be opened here: {name}. Mount it first (Windows: "
                + "double-click it; Steam Deck: right-click it in Dolphin and choose Mount), then add the mounted disc.");
            return null;
        }
        if (Ext(img) == ".cue")
        {
            string bin = File.ReadLines(img).Select(l => l.Trim())
                .Where(l => l.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase) && l.Contains('"'))
                .Select(l => l.Split('"')[1]).FirstOrDefault();
            img = bin is null ? null : Path.Combine(Path.GetDirectoryName(img)!, bin);
        }
        else if (Ext(img) == ".mds") img = Path.ChangeExtension(img, ".mdf");
        if (img is null || !File.Exists(img)) { _hint ??= new Reject("unpack_failed", $"The image file for {name} is missing."); return null; }
        string mnt = NewTemp(), iso = mnt + ".iso";
        Temps.Add(iso);
        if (IsRaw(img)) ConvertRaw(img, iso);
        else File.CreateSymbolicLink(iso, img);   // hdiutil goes by the file name extension
        var p = Process.Start(new ProcessStartInfo("/usr/bin/hdiutil",
            new[] { "attach", "-readonly", "-nobrowse", "-noverify", "-mountpoint", mnt, iso })
            { RedirectStandardOutput = true, RedirectStandardError = true })!;
        p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit();
        if (p.ExitCode != 0) { _hint ??= new Reject("unpack_failed", $"Could not open the disc image {name}."); return null; }
        Mounts.Add(mnt);
        return mnt;
    }

    static readonly byte[] SyncPattern = { 0, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0 };

    static bool IsRaw(string img)
    {
        var head = new byte[12];
        using var f = File.OpenRead(img);
        return f.Read(head, 0, 12) == 12 && head.AsSpan().SequenceEqual(SyncPattern);
    }

    static void ConvertRaw(string img, string iso)
    {
        using var i = File.OpenRead(img);
        using var o = File.Create(iso);
        var sector = new byte[2352];
        while (i.ReadAtLeast(sector, 2352, throwOnEndOfStream: false) == 2352)
            o.Write(sector, sector[15] == 2 ? 24 : 16, 2048);
    }

    static void Cleanup()
    {
        lock (Mounts)
        {
            try { _child?.Kill(true); _child?.WaitForExit(); } catch (InvalidOperationException) { }
            var failed = new HashSet<string>();
            foreach (string m in Mounts)
            {
                var p = Process.Start(new ProcessStartInfo("/usr/bin/hdiutil", new[] { "detach", m, "-force", "-quiet" }));
                p!.WaitForExit();
                if (p.ExitCode != 0) failed.Add(m);   // still mounted: never delete through it
            }
            Mounts.Clear();
            foreach (string t in Temps)
                try
                {
                    if (failed.Contains(t)) continue;
                    if (File.Exists(t) || new FileInfo(t).LinkTarget is not null) File.Delete(t);
                    else if (Directory.Exists(t)) Directory.Delete(t, true);
                }
                catch (IOException) { }
            Temps.Clear();
        }
    }

    // ---- check ----

    // Top-level files of the checked directories, keyed by canonical relative path, case-insensitive.
    static Dictionary<string, string> Index(string root)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string dir in new[] { "System", "Maps", "Textures", "Sounds", "Music" })
            if (DirCI(root, dir) is string d)
                foreach (string f in Directory.EnumerateFiles(d)) files.TryAdd(dir + "/" + Path.GetFileName(f), f);
        return files;
    }

    static List<Entry> Check(string root)
    {
        var files = Index(root);
        string[] keys = { "System/Engine.dll", "System/Render.dll", "System/HPBase.u" };
        if (keys.FirstOrDefault(k => !files.ContainsKey(k)) is string absent)
            throw new Reject("missing_files", $"{absent} is missing. A full copy of the game is needed: the installed game folder or the game CD.");
        var id = UEEdition.Identify(root);
        // Releases with modified game scripts (the author's inventory, audit/versions_survey_2026-11-03.md, or a listed
        // DLL build with another HPBase.u) and unknown releases have no manifest: installed as found, with a WARNING.
        (string manifest, string name) ed = Editions.TryGetValue(id.Data, out var known) ? known : ("", id.Data switch
        {
            UEEdition.DataSet.CITY_PL => "City re-release",
            UEEdition.DataSet.MODDED_EN => "Modded re-release",
            _ => id.Tier == UEEdition.Support.Modified ? "Modified release" : "Unknown release",
        });
        var manifest = ed.manifest == "" ? new List<Entry>() : LoadManifest(ed.manifest);
        var missing = manifest.Where(e => !files.ContainsKey(e.Path)).Select(e => e.Path).ToList();
        if (missing.Count > 0)
            throw new Reject("missing_files", $"{missing.Count} game file(s) missing here (for example "
                + $"{string.Join(", ", missing.Take(3))}). A full copy of the game is needed: the installed game folder or the game CD.");
        long total = manifest.Sum(e => e.Size), done = 0;
        int shown = -1;
        var differ = new List<string>();
        foreach (var e in manifest)
        {
            string path = files[e.Path];
            if (Size(path) != e.Size || Sha1(path) != e.Sha1) differ.Add(e.Path);
            done += e.Size;
            Progress(done, total, ref shown);
        }

        // Opened unconditionally by Core: System/DefUser.ini, and Default.ini in System/ or (CD layout) System/0/.
        string sys = DirCI(root, "System");
        if (FileCI(sys, "DefUser.ini") is null)
            throw new Reject("missing_files", "System/DefUser.ini is missing. A full copy of the game is needed.");
        if (FileCI(sys, "Default.ini") is null && FileCI(DirCI(sys, "0"), "Default.ini") is null)
            throw new Reject("missing_files", "System/Default.ini is missing. A full copy of the game is needed.");
        // A supported edition with files that differ from its manifest: a patch, a mod or a damaged copy.
        var tier = differ.Count > 0 ? UEEdition.Support.Modified : id.Tier;
        _edition = $"{ed.name}: {tier.ToString().ToLowerInvariant()}";
        Console.WriteLine($"EDITION {id.Edition} {_edition}");
        if (differ.Count > 0)
            Console.WriteLine($"WARNING modified {differ.Count} game file(s) differ from {ed.name} (for example "
                + $"{string.Join(", ", differ.Take(3))}). " + UEEdition.ModifiedWarning);
        else if (id.Warning is string w) Console.WriteLine($"WARNING {tier.ToString().ToLowerInvariant()} " + w);
        _data = id.Data;
        return manifest;
    }

    static UEEdition.DataSet _data;   // of the checked root, written into the marker (the Launcher shows edition=)
    static string _edition;

    static string Sha1(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA1.HashData(s));
    }

    // Size of the file itself, through a symbolic link if it is one.
    static long Size(string path)
    {
        var fi = new FileInfo(path);
        return fi.LinkTarget is null ? fi.Length : ((FileInfo)fi.ResolveLinkTarget(true)!).Length;
    }

    static void Progress(long done, long total, ref int shown)
    {
        int p = total == 0 ? 100 : (int)(done * 100 / total);
        if (p != shown) { shown = p; Console.WriteLine("PROGRESS " + p); }
    }

    // ---- copy ----

    // A destination with game data HP1Setup did not install (an original install, the package unpacked into it) is
    // never written to: Copy would replace its System/ (HP.exe, HP.ini, User.ini, patches) and the rest.
    static void RefuseForeign(string dest)
    {
        if (!Directory.Exists(dest) || File.Exists(Path.Combine(dest, Marker)) || File.Exists(Path.Combine(dest, Installing))) return;
        string[] found = DataDirs.Where(d => DirCI(dest, d) is not null).ToArray();
        if (found.Length > 0)
            throw new Reject("dest_not_ours", $"{dest} already holds game files ({string.Join(", ", found)}) that HP1Setup did "
                + "not install. They are left as they are. Choose an empty folder for the game data.");
    }

    static void Copy(string root, string dest, List<Entry> manifest)
    {
        var canon = manifest.Select(e => e.Path).Concat(CanonIni).ToDictionary(p => p, p => p, StringComparer.OrdinalIgnoreCase);
        var plan = new List<(string src, string rel)>();
        foreach (string dir in DataDirs.Append("Save"))
        {
            if (DirCI(root, dir) is not string s) continue;
            foreach (string f in Directory.EnumerateFiles(s, "*", SearchOption.AllDirectories))
            {
                if (Ext(f) == ".exe") continue;
                string rel = dir + "/" + Path.GetRelativePath(s, f).Replace('\\', '/');
                plan.Add((f, canon.TryGetValue(rel, out string c) ? c : rel));
            }
        }
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, Installing), "");   // ours from here on, also if this run is interrupted
        foreach (string dir in DataDirs)   // a previous run stopped between its two moves: put its data back first
        {
            string staged = Path.Combine(dest, dir + ".new"), backup = Path.Combine(dest, dir + ".old");
            if (Directory.Exists(staged)) Directory.Delete(staged, true);
            if (Directory.Exists(backup))
                if (DirCI(dest, dir) is null) Directory.Move(backup, Path.Combine(dest, dir));
                else Directory.Delete(backup, true);
        }
        long total = plan.Sum(p => Size(p.src)), done = 0;
        int shown = -1;
        foreach (var (src, rel) in plan)
        {
            int slash = rel.IndexOf('/');
            string dir = rel[..slash], rest = rel[(slash + 1)..];
            string target = dir == "Save" ? Path.Combine(dest, "Save", rest) : Path.Combine(dest, dir + ".new", rest);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!(dir == "Save" && File.Exists(target))) File.Copy(src, target, overwrite: true);
            done += Size(src);
            Progress(done, total, ref shown);
        }
        var backups = new List<string>();
        foreach (string dir in DataDirs)
        {
            string staged = Path.Combine(dest, dir + ".new");
            if (!Directory.Exists(staged)) continue;
            if (DirCI(dest, dir) is string old)
            {
                string backup = Path.Combine(dest, dir + ".old");
                Directory.Move(old, backup);
                backups.Add(backup);
            }
            Directory.Move(staged, Path.Combine(dest, dir));
        }
        string version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        string tmp = Path.Combine(dest, Marker + ".tmp");
        File.WriteAllText(tmp, $"data={_data}\nedition={_edition}\nsetup={version}\n");
        File.Move(tmp, Path.Combine(dest, Marker), overwrite: true);   // the old marker stays until here
        File.Delete(Path.Combine(dest, Installing));
        foreach (string b in backups) Directory.Delete(b, true);
    }
}
