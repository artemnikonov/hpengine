// Launcher logic without UI (checked by launcher/Desktop.Tests): HP1Setup's output protocol, Mods.ini, the data
// directory per OS, the GitHub release choice (the same rules as Launcher.older/pick in launcher/Launcher.swift).
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace HPEngine.Launcher;

// The installed edition's name and tier as the Game card shows them. A matching engine family does not identify a disc's
// region: the Nordic multilingual CD uses the same native/script hashes as the family called UK 1.1, and its language
// files differ from that reference manifest. The title stays GameTitle.For(edition) (by the DLL set). Mirrored by the
// macOS Launcher (Launcher.swift editionDisplay).
public static class EditionLabel
{
    public sealed record Display(string Name, string Tier, string Detail);

    public static Display Describe(string edition, IReadOnlyList<HPEngine.Packaging.LanguageChoice> languages)
    {
        var parts = (edition ?? "").Split(": ");
        string name = parts.Length > 1 ? string.Join(": ", parts[..^1]) : edition;
        string tier = parts.Length > 1 ? parts[^1] : "";
        if (languages.Count < 2) return new(name, tier, null);
        var codes = languages.Select(l => l.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool nordic = codes.SetEquals(new[] { "dan", "eng", "fin", "nor", "swe" });
        string detail = (string.IsNullOrEmpty(name) ? "" : "Engine family: " + name + ". ")
            + "Languages: " + string.Join(", ", languages.Select(l => l.Label)) + ".";
        // The engine's identity/support decisions stay intact. The UI reports the manifest difference without
        // implying that a genuine multilingual disc was patched. This does not authenticate an exact disc image.
        return new(nordic ? "Nordic multilingual data" : "Multilingual game data",
            nordic && name == "UK 1.1" && tier == "modified" ? "different data" : tier, detail);
    }

    // Author 2026-11-20: HPEngine runs every copy; the launcher names the edition only to warn (a modified or unknown
    // copy). A tested edition, and the genuine Nordic disc's manifest difference, show nothing.
    public static bool Quiet(string tier) => tier is "exact" or "supported" or "different data";
}

public enum SetupKind { Step, Progress, Error, Warning, Edition, Report, Done, Malformed }

// One line of HP1Setup's stdout (launcher/HP1Setup/Program.cs header). Key: ERROR/WARNING key, EDITION id, STEP key;
// Text: the player's text, DONE root. Malformed: Text holds the reason, Raw the line; the caller shows it, never drops it.
public sealed record SetupLine(SetupKind Kind, string Key, string Text, int Index, double Progress, string Raw);

public static class SetupProtocol
{
    public static readonly (string Key, string Text)[] Steps =
    {
        ("find", "Looking for the game"), ("unpack", "Unpacking"), ("check", "Checking the game files"), ("copy", "Copying game data"),
    };

    public static SetupLine Parse(string line)
    {
        SetupLine Bad(string why) => new(SetupKind.Malformed, "", why, -1, 0, line);
        var sp = line.IndexOf(' ');
        var cmd = sp < 0 ? line : line[..sp];
        var arg = sp < 0 ? "" : line[(sp + 1)..];
        switch (cmd)
        {
            case "STEP":
                var i = Array.FindIndex(Steps, s => s.Key == arg);
                return i < 0 ? Bad($"unknown step '{arg}'") : new(SetupKind.Step, arg, "", i, 0, line);
            case "PROGRESS":
                return double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) && p >= 0 && p <= 100
                    ? new(SetupKind.Progress, "", "", -1, p / 100, line) : Bad($"progress '{arg}' is not 0..100");
            case "ERROR": case "WARNING": case "EDITION":
                var k = arg.IndexOf(' ');   // "<key> <text>"
                if (k <= 0 || k == arg.Length - 1) return Bad($"{cmd} without '<key> <text>'");
                return new(cmd == "ERROR" ? SetupKind.Error : cmd == "WARNING" ? SetupKind.Warning : SetupKind.Edition,
                           arg[..k], arg[(k + 1)..], -1, 0, line);
            case "REPORT":   // a new-issue link for an unrecognised release (UEEdition.ReportUrl)
                return arg.StartsWith("https://", StringComparison.Ordinal) ? new(SetupKind.Report, "", arg, -1, 0, line) : Bad("REPORT without an https link");
            case "DONE":
                return arg.Length == 0 ? Bad("DONE without a root") : new(SetupKind.Done, "", arg, -1, 0, line);
            default:
                return Bad("unknown line");
        }
    }
}

// The game's title by installed edition (HP1Setup's EDITION text "<name>: <tier>", kept in the marker; names: HP1Setup Program.cs
// Editions and its unlisted-set names), by the DLL set the name stands for (UEEdition.Sets): US 1.53 and the two re-releases on
// its DLLs (City, Modded) are "Sorcerer's Stone", UK 1.1 "Philosopher's Stone"; the Russian EA release keeps the title the Launcher
// always showed (Philosopher's). A game not identified (nothing installed, a game folder without the Launcher's marker, a
// "Modified release" or "Unknown release", whose text does not say which DLL set it has): the neutral "Harry Potter".
// Same in Launcher.swift gameTitle.
public static class GameTitle
{
    public const string Neutral = "Harry Potter", Philosopher = "Harry Potter and the Philosopher's Stone", Sorcerer = "Harry Potter and the Sorcerer's Stone";
    public static string For(string edition) => edition == null ? Neutral : edition.Split(": ")[0] switch
    {
        "US 1.53" or "City re-release" or "Modded re-release" => Sorcerer,
        "UK 1.1" or "RU EA" => Philosopher,
        _ => Neutral,
    };
}

public sealed record ModEntry(string Name, bool Enabled, bool Present);

// <game>/HPEngineData/Mods.ini: "[Mods]" with one "Enabled=<name>" per enabled mod, in order (a later mod wins); ";"
// starts a comment. Everything else (comments, other keys, other sections, line endings) is written back unchanged.
public static class ModsIni
{
    // Under the list while a mod is on; the numbers are the apply order (Launcher.swift orderHint).
    public const string OrderHint = "Enabled mods apply in number order: a higher number wins";

    static bool IsEnabledLine(string l, bool inMods, out string name)
    {
        name = null;
        var t = l.Trim();
        if (!inMods || !t.StartsWith("Enabled", StringComparison.OrdinalIgnoreCase)) return false;
        var rest = t[7..].TrimStart();
        if (!rest.StartsWith('=')) return false;
        name = rest[1..].Trim();
        return name.Length > 0;
    }

    internal static bool Section(string l, out string name)
    {
        var t = l.Trim(); name = null;
        if (t.Length < 2 || t[0] != '[' || t[^1] != ']') return false;
        name = t[1..^1].Trim(); return true;
    }

    public static List<string> ReadEnabled(string text)
    {
        var r = new List<string>(); var inMods = false;
        foreach (var l in text.Split('\n'))
        {
            if (Section(l, out var s)) { inMods = s.Equals("Mods", StringComparison.OrdinalIgnoreCase); continue; }
            if (IsEnabledLine(l, inMods, out var n) && !r.Contains(n)) r.Add(n);
        }
        return r;
    }

    // The new text: the old Enabled lines of [Mods] removed, the new ones put where the first old one was (else at the end
    // of [Mods]; else a new [Mods] section at the end of the file).
    public static string WriteEnabled(string text, IReadOnlyList<string> enabled)
    {
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Length == 0 ? new List<string>() : text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);   // the final newline
        var outp = new List<string>(); bool inMods = false, placed = false, sawMods = false;
        void Place() { if (placed) return; placed = true; outp.AddRange(enabled.Select(n => "Enabled=" + n)); }
        foreach (var l in lines)
        {
            if (Section(l, out var s))
            {
                if (inMods) Place();   // leaving [Mods] without an Enabled line: the new ones close the section
                inMods = s.Equals("Mods", StringComparison.OrdinalIgnoreCase); sawMods |= inMods;
                outp.Add(l); continue;
            }
            if (IsEnabledLine(l, inMods, out _)) { Place(); continue; }
            outp.Add(l);
        }
        if (inMods) Place();
        if (!sawMods) { if (outp.Count > 0) outp.Add(""); outp.Add("[Mods]"); Place(); }
        return string.Join(nl, outp) + nl;
    }

    public static string IniPath(string gameDir) => Path.Combine(gameDir, "HPEngineData", "Mods.ini");

    // Enabled mods in file order (also those whose folder is gone: Present=false, kept), then the other folders by name.
    public static List<ModEntry> Load(string gameDir)
    {
        var ini = IniPath(gameDir);
        var enabled = File.Exists(ini) ? ReadEnabled(File.ReadAllText(ini)) : new List<string>();
        var modsDir = Path.Combine(gameDir, "Mods");
        var found = Directory.Exists(modsDir)
            ? Directory.GetDirectories(modsDir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();
        // Folder names without case, as the engine finds them (ModOverlay.FindDir).
        var r = enabled.Select(n => new ModEntry(n, true, found.Contains(n, StringComparer.OrdinalIgnoreCase))).ToList();
        r.AddRange(found.Where(n => !enabled.Contains(n, StringComparer.OrdinalIgnoreCase)).Select(n => new ModEntry(n, false, true)));
        return r;
    }

    // Why Play cannot start: enabled mods whose folder is gone (the engine would stop with exit code 5); null: none.
    public static string Missing(IEnumerable<ModEntry> mods)
    {
        var gone = mods.Where(m => m.Enabled && !m.Present).Select(m => m.Name).ToList();
        if (gone.Count == 0) return null;
        return gone.Count == 1
            ? $"The mod {gone[0]} is on, but its folder is gone. Turn off {gone[0]} or put its folder back into Mods."
            : $"The mods {And(gone)} are on, but their folders are gone. Turn off {And(gone)} or put their folders back into Mods.";
    }

    public static string And(IReadOnlyList<string> n) => n.Count == 1 ? n[0] : string.Join(", ", n.Take(n.Count - 1)) + " and " + n[^1];

    // One line per enabled mod with files the game never opens: files in a subfolder of the mod that is not a game
    // directory of the install (a mod unpacked one level too deep). Same rule as ModOverlay.Result.Misplaced.
    public static List<string> Misplaced(string gameDir, IEnumerable<ModEntry> mods)
    {
        var lines = new List<string>();
        if (!Directory.Exists(gameDir)) return lines;
        var notGame = new[] { "Mods", "HPEngineData", "HPEngine" };
        var gameDirs = Directory.GetDirectories(gameDir).Select(Path.GetFileName)
            .Where(d => !notGame.Contains(d, StringComparer.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var modDirs = Directory.Exists(Path.Combine(gameDir, "Mods")) ? Directory.GetDirectories(Path.Combine(gameDir, "Mods")) : Array.Empty<string>();
        foreach (var m in mods.Where(m => m.Enabled))
        {
            var dir = modDirs.FirstOrDefault(d => Path.GetFileName(d).Equals(m.Name, StringComparison.OrdinalIgnoreCase));
            if (dir == null) continue;
            int n = Directory.GetDirectories(dir).Where(d => !gameDirs.Contains(Path.GetFileName(d)))
                .Sum(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Count(f => !Path.GetFileName(f).StartsWith('.')));
            if (n > 0)
                lines.Add((n == 1 ? $"1 file of {m.Name} is" : $"{n} files of {m.Name} are")
                    + $" not in a game folder (System, Maps, ...): unpack the mod so these folders are directly inside Mods/{m.Name}");
        }
        return lines;
    }

    // One line per pair of enabled mods that both carry a file at the same game path (the engine's placement,
    // ModOverlay.Conflicts): the later mod's file is used. None while the mods cannot be placed (Play reports that).
    // Same texts in Launcher.swift conflictLines.
    public static List<string> Conflicts(string gameDir)
    {
        List<(string Earlier, string Later, List<string> Files)> pairs;
        try { pairs = HPEngine.Packaging.ModOverlay.Conflicts(gameDir, Path.Combine(gameDir, "HPEngineData")); }
        catch (Exception e) when (e is HPEngine.Packaging.ModOverlayException or IOException or UnauthorizedAccessException) { return new(); }
        return pairs.Select(p => $"{p.Later} and {p.Earlier} both have " + (p.Files.Count == 1 ? p.Files[0] : $"{p.Files[0]} and {p.Files.Count - 1} more file" + (p.Files.Count == 2 ? "" : "s"))
            + $": the game uses {p.Later}'s (later in the order)").ToList();
    }

    public static void Save(string gameDir, IEnumerable<ModEntry> mods)
    {
        var ini = IniPath(gameDir);
        Directory.CreateDirectory(Path.GetDirectoryName(ini));
        var old = File.Exists(ini) ? File.ReadAllText(ini) : "";
        File.WriteAllText(ini, WriteEnabled(old, mods.Where(m => m.Enabled).Select(m => m.Name).ToList()), new UTF8Encoding(false));
    }
}

// "Start map" next to Play: the maps the enabled mods add to the game (community map mods; the original starts them
// with `HP.exe <map>`). Which files those are is the engine's rule, not a second one here: ModOverlay.AddedMaps.
// Not saved between Launcher runs; with no such map the choice is not shown. Same in Launcher.swift.
public static class StartMap
{
    public const string Title = "Start map", Game = "Game (normal start)";

    // By map name; none also while the mods cannot be placed (a missing or empty mod folder: Play reports that).
    public static List<(string Mod, string Map)> Added(string gameDir)
    {
        try { return HPEngine.Packaging.ModOverlay.AddedMaps(gameDir, Path.Combine(gameDir, "HPEngineData")); }
        catch (Exception e) when (e is HPEngine.Packaging.ModOverlayException or IOException or UnauthorizedAccessException) { return new(); }
    }

    // The entries after "Game": the map name, with its mod when the maps come from several mods.
    public static List<string> Labels(IReadOnlyList<(string Mod, string Map)> maps)
    {
        bool several = maps.Select(m => m.Mod).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
        return maps.Select(m => several ? $"{m.Map}  ({m.Mod})" : m.Map).ToList();
    }

    // The engine's arguments for Play; map null: the normal start (no --map).
    // --launched-by-launcher: the engine shows no error dialog of its own, the Launcher does (hpengine/packaging/FatalDialog.cs).
    public static List<string> PlayArgs(string gameDir, string map)
    {
        var a = new List<string> { "--root", gameDir, "--launched-by-launcher" };
        if (map != null) { a.Add("--map"); a.Add(map); }
        return a;
    }
}

// What the engine itself said when it stopped with an error: the last line of its console output that names the cause.
// Exit code 5 (mods): its "[Mods] ..." line; 6 (no such start map): its "[Map] ..." line; 4 (no game folder): the InstallRoot message; else the crash line (the run
// log's "[Crash] ...", or the runtime's own "Unhandled exception. ..."). Same rules in Launcher.swift.
public static class EngineError
{
    public static string Pick(IEnumerable<string> lines, int code)
    {
        Func<string, bool> Is(string prefix) => l => l.StartsWith(prefix, StringComparison.Ordinal);
        if (code == 5) return lines.LastOrDefault(Is("[Mods] "));
        if (code == 6) return lines.LastOrDefault(Is("[Map] "));
        if (code == 4) return lines.LastOrDefault(l => l.Contains("not a game install") || l.StartsWith("game install not found", StringComparison.Ordinal));
        return lines.LastOrDefault(Is("[Crash] ")) ?? lines.LastOrDefault(Is("Unhandled exception. "));
    }

    // The banner (author 2026-11-17): what happened in plain words, then the engine's line on its own line when there is one.
    // No file path: "Open log" opens the right log (GameScope.LogToOpen).
    public static string Banner(IEnumerable<string> lines, int code) =>
        $"The game stopped with an error (code {code})." + (Pick(lines, code) is string l ? "\n" + l.Trim() : "");
}

// The launcher's gamepad (proposal §6), the ONE answer of both launchers: launcher/Launcher.swift PadMap mirrors it and
// launcher/test_update.sh compares the two tables (--pad-map). Buttons in SDL_GamepadButton numbers (Sdl.cs reads them;
// the macOS Launcher maps GameController's buttons onto the same numbers). popup: a menu, a drop-down list or a dialog is
// open and takes the pad; hp2: the HP2 tab is selected.
public enum PadAction { None, Activate, Escape, Back, Hp1, Hp2, Menu, Play, KeyUp, KeyDown, Previous, Next }
public static class PadMap
{
    public const int A = 0, B = 1, Y = 3, Start = 6, LB = 9, RB = 10, Up = 11, Down = 12, Left = 13, Right = 14;

    public static PadAction For(int button, bool popup, bool hp2) => button switch
    {
        A => PadAction.Activate,
        B => popup ? PadAction.Escape : PadAction.Back,
        LB => popup || !hp2 ? PadAction.None : PadAction.Hp1,
        RB => popup || hp2 ? PadAction.None : PadAction.Hp2,
        Start => popup ? PadAction.None : PadAction.Menu,
        Y => popup || hp2 ? PadAction.None : PadAction.Play,
        Up or Left => popup ? PadAction.KeyUp : PadAction.Previous,
        Down or Right => popup ? PadAction.KeyDown : PadAction.Next,
        _ => PadAction.None,
    };

    // The left stick past half way (16000 of SDL's 32767) as d-pad bits; x right, y down, each -1..1.
    public static uint Stick(double x, double y)
    {
        const double half = 16000 / 32767.0;
        uint bits = 0;
        if (y < -half) bits |= 1u << Up; if (y > half) bits |= 1u << Down;
        if (x < -half) bits |= 1u << Left; if (x > half) bits |= 1u << Right;
        return bits;
    }

    // The buttons pressed since the last poll, in button order (one action per press).
    public static IEnumerable<int> Pressed(uint now, uint last)
    {
        for (int b = 0; b <= Right; b++) if ((now & ~last & (1u << b)) != 0) yield return b;
    }
}

// HP2 in the launcher, the release switch (author 2026-11-16, release 0.4.0): off = the HP2 tab shown but disabled (no click,
// Ctrl+2 or pad RB selects it; "Coming soon" under its name and a tooltip) and its saved state (folder, errors) ignored; on =
// the HP2 tab works (a preview build). Build-time: make_launcher.sh passes -p:HPEngineHp2=true when HPENGINE_HP2=1 (assembly
// metadata HPEngineHp2); the release build has none. MIRROR: launcher/Launcher.swift Hp2Release (Info.plist HPEngineHp2),
// compared by launcher/test_update.sh (--game-scope).
public static class Hp2Release
{
    public static bool Available = typeof(Hp2Release).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Any(m => m.Key == "HPEngineHp2" && m.Value == "true");
    public const string Soon = "Coming soon", SoonTip = "Harry Potter 2 is coming soon. Wait for updates.";
    // The caption under the tab's name (null: none) and its tooltip (null: none).
    public static string Caption(bool available, bool folderSet) => !available ? Soon : folderSet ? null : "not set up";
    public static string Tip(bool available) => available ? null : SoonTip;
}

// Per game (author 2026-11-16): what one game's run writes and shows belongs to that game's tab. MIRROR: launcher/Launcher.swift
// GameScope; launcher/test_update.sh compares the answers (--game-scope).
public static class GameScope
{
    // The error banner of the selected tab: the launcher's own error (its update, its location) on both tabs, else that game's.
    public static string Error(bool hp2Tab, string hp1, string hp2, string launcher) => launcher ?? (hp2Tab ? hp2 : hp1);
    // The folder HPEngine writes for the game's run (settings overlay, logs/): HP1 <game>/HPEngineData; HP2 <HP2Data>/HPEngineData
    // beside the port's HP2 save dir <HP2Data>/Save (the engine's InstallRoot.Hp2DataDir of --hp2-savedir).
    public static string DataDir(string hp1Game, bool hp2) =>
        hp2 ? Path.Combine(Path.GetDirectoryName(Hp2FrontEnd.SaveDir(hp1Game)), "HPEngineData") : Path.Combine(hp1Game, "HPEngineData");
    public static string LogsDir(string hp1Game, bool hp2) => Path.Combine(DataDir(hp1Game, hp2), "logs");
    // The launcher's capture of a run's console output: HP1's file, HP2's beside it with "-HP2" (HPEngine.log, HPEngine-HP2.log).
    public static string ConsoleLog(string hp1Log, bool hp2) => hp2 ? hp1Log[..^4] + "-HP2.log" : hp1Log;
    // The banner's × (author 2026-11-17): clears the error it shows — the launcher's own when that is shown, else the selected
    // tab's game error; the other game's stays. A new error shows the banner again.
    public static (string Hp1, string Hp2, string Launcher) Dismiss(bool hp2Tab, string hp1, string hp2, string launcher) =>
        launcher != null ? (hp1, hp2, null) : hp2Tab ? (hp1, null, null) : (null, hp2, null);
    // "Open log" of a failed run (author 2026-11-17): the run journal that run wrote (HPEngineProgram: logs/run_<yyyyMMdd_HHmmss>.log;
    // holds the [Crash] trace) = the newest run_*.log that was not there when the run started (`before`: RunLogs just before the
    // start; no clock or file-time compare, so a run in the same second as the previous one's journal is not confused with it);
    // none (codes 4/5/6 stop before the journal starts) → the console capture; neither → the game's logs folder.
    public static string[] RunLogs(string logsDir) =>
        Directory.Exists(logsDir) ? Directory.GetFiles(logsDir).Select(Path.GetFileName).Where(n => n.StartsWith("run_", StringComparison.Ordinal) && n.EndsWith(".log", StringComparison.Ordinal)).ToArray() : Array.Empty<string>();
    public static string RunLog(IEnumerable<string> now, IEnumerable<string> before) =>
        now.Where(n => n.StartsWith("run_", StringComparison.Ordinal) && n.EndsWith(".log", StringComparison.Ordinal)).Except(before ?? Array.Empty<string>(), StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal).LastOrDefault();
    public static string LogToOpen(string logsDir, string console, IEnumerable<string> before) =>
        RunLog(RunLogs(logsDir), before) is string run ? Path.Combine(logsDir, run) : File.Exists(console) ? console : logsDir;
}

// "Reset Gamepad Bindings". PAD RESET RULE (the same text in HP1.Core UEInput.Gamepad.cs and launcher/Launcher.swift
// padRebound; the launchers edit the files, the engine only reads them): the engine's saved overlay lives in
// <game>/HPEngineData — DefUser.ini for the User scope, Default.ini for the System scope. "Never rebound" = in DefUser.ini no
// line of [Engine.Input] whose key starts with "Joy" (any case), and in Default.ini no GamepadBindingsSaved line in
// [HPEngine]. Reset deletes exactly those lines and nothing else (bytes of every other line kept): the game's own
// DefUser.ini (every Joy* line empty) shows through again and the engine's default pad bindings apply.
public static class PadReset
{
    static readonly (string File, string Section, Func<string, bool> Key)[] Rules =
    {
        ("DefUser.ini", "Engine.Input", k => k.StartsWith("Joy", StringComparison.OrdinalIgnoreCase)),
        ("Default.ini", "HPEngine", k => k.Equals("GamepadBindingsSaved", StringComparison.OrdinalIgnoreCase)),
    };

    // The text without the section's lines whose key matches; removed = how many went.
    public static string Drop(string text, string section, Func<string, bool> key, out int removed)
    {
        var kept = new List<string>(); var inside = false; removed = 0;
        foreach (var l in text.Split('\n'))
        {
            if (ModsIni.Section(l, out var s)) inside = s.Equals(section, StringComparison.OrdinalIgnoreCase);
            else if (inside && l.IndexOf('=') is var eq && eq > 0 && key(l[..eq].Trim())) { removed++; continue; }
            kept.Add(l);
        }
        return string.Join('\n', kept);
    }

    // Lines a reset would remove (0 = nothing was rebound: the menu item is off). reset: also write the files back.
    public static int Rebound(string gameDir, bool reset = false)
    {
        var n = 0;
        foreach (var (file, section, key) in Rules)
        {
            var path = Path.Combine(gameDir, "HPEngineData", file);
            if (!File.Exists(path)) continue;
            var text = Drop(File.ReadAllText(path, Encoding.Latin1), section, key, out var removed);   // Latin1: every byte kept
            n += removed;
            if (reset && removed > 0) File.WriteAllText(path, text, Encoding.Latin1);
        }
        return n;
    }
}

public enum Os { Windows, Linux, MacOS }

public static class DataDir
{
    public static Os Current => OperatingSystem.IsWindows() ? Os.Windows : OperatingSystem.IsMacOS() ? Os.MacOS : Os.Linux;

    // The install root (game data, Save/, HPEngineData/): <data>/HPEngine/game. No admin rights needed anywhere.
    // Windows %LOCALAPPDATA%; Linux $XDG_DATA_HOME if set to an absolute path (XDG spec), else ~/.local/share;
    // macOS as Launcher.swift (the engine's own Mac launcher; here only so the tests can name all three).
    public static string Game(Os os, Func<string, string> env, string home)
    {
        string baseDir;
        switch (os)
        {
            case Os.Windows:
                var la = env("LOCALAPPDATA");
                baseDir = string.IsNullOrEmpty(la) ? Path.Combine(home, "AppData", "Local") : la; break;
            case Os.MacOS:
                baseDir = Path.Combine(home, "Library", "Application Support"); break;
            default:
                var x = env("XDG_DATA_HOME");
                baseDir = !string.IsNullOrEmpty(x) && Path.IsPathRooted(x) ? x : Path.Combine(home, ".local", "share"); break;
        }
        return Path.Combine(baseDir, "HPEngine", "game");
    }

    // The release archive unpacked INTO the player's game folder: the Launcher sits next to System/ and Maps/, and that
    // game is the one to run (the engine beside it picks the same folder: InstallRoot.Resolve, the executable's parent).
    // A game by the engine's own rule (InstallRoot.Missing). Not when that folder is the Launcher's data directory
    // itself: that copy is the Launcher's, installed and removed by it.
    public static bool OwnGame(string launcherDir, string dataGame) =>
        HPEngine.Packaging.InstallRoot.Missing(launcherDir) is null
        && !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(launcherDir)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataGame)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

// Archive/Sig: download URLs of this platform's release archive and its signature (null: the release has none, the page opens).
public sealed record Release(string Version, string Page, string Archive = null, string Sig = null);

public static class Updates
{
    // a < b by semver precedence (semver.org item 11), as Launcher.older.
    public static bool Older(string a, string b)
    {
        static (int[] nums, string[] pre) Parts(string v)
        {
            var plus = v.IndexOf('+'); if (plus >= 0) v = v[..plus];
            var dash = v.IndexOf('-');
            var pre = dash < 0 ? Array.Empty<string>() : v[(dash + 1)..].Split('.');
            var core = dash < 0 ? v : v[..dash];
            return (core.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(s => int.TryParse(s, out var n) ? n : 0).ToArray(), pre);
        }
        var (x, xp) = Parts(a); var (y, yp) = Parts(b);
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            int p = i < x.Length ? x[i] : 0, q = i < y.Length ? y[i] : 0;
            if (p != q) return p < q;
        }
        if (xp.Length == 0 || yp.Length == 0) return xp.Length != 0 && yp.Length == 0;
        for (int i = 0; i < Math.Min(xp.Length, yp.Length); i++)
        {
            if (xp[i] == yp[i]) continue;
            bool pn = int.TryParse(xp[i], out var m), qn = int.TryParse(yp[i], out var n);
            if (pn && qn) return m < n;
            if (pn) return true;
            if (qn) return false;
            return string.CompareOrdinal(xp[i], yp[i]) < 0;
        }
        return xp.Length < yp.Length;
    }

    // The newest release in the feed (GitHub lists newest first) this build may take, if newer than `current`: drafts
    // never; pre-releases only for a pre-release build. Throws JsonException/InvalidOperationException on a bad feed.
    public static Release Pick(string json, string current, string archiveName = null)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            bool Flag(string n) => rel.TryGetProperty(n, out var f) && f.ValueKind == JsonValueKind.True;
            if (Flag("draft") || (Flag("prerelease") && !current.Contains('-'))) continue;
            if (!rel.TryGetProperty("tag_name", out var tag) || !rel.TryGetProperty("html_url", out var page)) continue;
            var v = tag.GetString().TrimStart('v');
            if (!Older(current, v)) return null;
            string Asset(string n) => n != null && rel.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array
                ? assets.EnumerateArray().Where(a => a.TryGetProperty("name", out var an) && an.GetString() == n)
                    .Select(a => a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null).FirstOrDefault() : null;   // no url: absent
            return new Release(v, page.GetString(), Asset(archiveName), Asset(archiveName == null ? null : archiveName + ".sig"));
        }
        return null;
    }
}

// Removing the installed game (<data>/game). keep: everything except Save/, HPEngineData/ and Mods/ goes; all: the whole root.
// The marker goes first (an interrupted removal reads as "not installed", never as a broken install). Links are deleted as
// links, never followed (HPEngineData/modroot holds links to the mods). `dispose` replaces the final delete (Windows: Recycle Bin).
public static class GameRemoval
{
    public static readonly string[] Kept = { "Save", "HPEngineData", "Mods" };

    static bool IsLink(string p) => (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0;

    static void DeleteLink(string p) { if (Directory.Exists(p)) Directory.Delete(p, false); else File.Delete(p); }

    // Deletes `path` without following links; linksOnly: only the links below it are deleted (before a Recycle Bin move).
    public static void Walk(string path, bool linksOnly)
    {
        if (IsLink(path)) { DeleteLink(path); return; }
        if (Directory.Exists(path))
        {
            foreach (var e in Directory.EnumerateFileSystemEntries(path).ToList()) Walk(e, linksOnly);
            if (!linksOnly) Directory.Delete(path, false);
        }
        else if (!linksOnly) File.Delete(path);
    }

    // afterMarker: a test hook called once the marker is gone (an exception there simulates a failed removal).
    public static void Remove(string root, bool all, Action<string> dispose = null, Action afterMarker = null)
    {
        if (!Directory.Exists(root)) return;
        foreach (var m in new[] { ".hp1-installed", ".hp1-installing" }) File.Delete(Path.Combine(root, m));
        afterMarker?.Invoke();
        void Dispose(string p)
        {
            if (dispose == null) { Walk(p, false); return; }
            Walk(p, true); dispose(p);
        }
        if (all) { Dispose(root); return; }
        foreach (var e in Directory.EnumerateFileSystemEntries(root).ToList().Where(e => !Kept.Contains(Path.GetFileName(e), StringComparer.OrdinalIgnoreCase)))
            Dispose(e);
    }
}

// The Mods notice: which of the player's slots were saved with another mod set than the enabled one. A slot is
// <game>/Save/save<N>.hp1sav, N = 0..5, shown in the game as Slot N+1 (FESlotPage: SetText(I + 1)); the level-start autosave
// save99 is not a slot. The mod names are read from the save itself, without the engine (HP1.Core UELevelRuntime.SaveGame.cs,
// format 5): string "HP1SAV" (BinaryWriter: 7-bit length + UTF-8), int32 version, int32 count, count strings, in order.
// Versions 1..4 carry none: saved without mods, and the game refuses such a save while mods are on (it has no level-entry
// progress to restart with). A file that does not read (damaged, newer version) is unknown: no line. Same rules in
// Launcher.swift.
public static class ModsNotice
{
    public sealed record SlotMods(IReadOnlyList<string> Mods, bool Legacy);

    // The mod record at the start of a save body; null: unknown (not a save this reader understands).
    public static SlotMods ReadSlot(byte[] b)
    {
        int p = 0;
        bool Int7(out int v)
        {
            v = 0;
            for (int shift = 0; shift < 35; shift += 7)
            {
                if (p >= b.Length) return false;
                byte c = b[p++]; v |= (c & 0x7f) << shift;
                if ((c & 0x80) == 0) return v >= 0;
            }
            return false;
        }
        bool Str(out string s)
        {
            s = null;
            if (!Int7(out int n) || n > b.Length - p) return false;
            try { s = new UTF8Encoding(false, true).GetString(b, p, n); } catch (ArgumentException) { return false; }
            p += n; return true;
        }
        bool I32(out int v) { v = 0; if (b.Length - p < 4) return false; v = BitConverter.ToInt32(b, p); p += 4; return true; }
        if (b == null || !Str(out var magic) || magic != "HP1SAV" || !I32(out int ver) || ver < 1 || ver > 6) return null;
        if (ver < 5) return new SlotMods(Array.Empty<string>(), true);
        if (!I32(out int count) || count < 0 || count > 256) return null;
        var mods = new List<string>(count);
        for (int i = 0; i < count; i++) { if (!Str(out var m)) return null; mods.Add(m); }
        return new SlotMods(mods, false);
    }

    // The notice lines for these slots (Slot number as shown in the game) and the enabled mods; none: nothing to say.
    public static List<string> Lines(IReadOnlyList<string> enabled, IEnumerable<(int Slot, SlotMods Mods)> slots)
    {
        var restart = new List<int>(); var legacy = new List<int>();
        foreach (var (slot, m) in slots.OrderBy(s => s.Slot))
        {
            if (m == null) continue;
            bool same = m.Mods.Count == enabled.Count && m.Mods.Zip(enabled).All(x => string.Equals(x.First, x.Second, StringComparison.OrdinalIgnoreCase));
            if (same) continue;
            (m.Legacy ? legacy : restart).Add(slot);
        }
        var lines = new List<string>();
        if (restart.Count > 0)
            lines.Add(restart.Count == 1
                ? $"Slot {restart[0]} was saved with other mods: loading it restarts its level with the progress you had when you entered it."
                : $"Slots {Numbers(restart)} were saved with other mods: loading them restarts their level with the progress you had when you entered it.");
        if (legacy.Count > 0)
            lines.Add(legacy.Count == 1
                ? $"Slot {legacy[0]} was saved by an older version: it does not load while mods are on."
                : $"Slots {Numbers(legacy)} were saved by an older version: they do not load while mods are on.");
        return lines;
    }

    static string Numbers(List<int> n) => n.Count == 1 ? n[0].ToString() : string.Join(", ", n.Take(n.Count - 1)) + " and " + n[^1];

    public static List<string> Lines(string game, IEnumerable<ModEntry> mods)
    {
        var slots = new List<(int, SlotMods)>();
        for (int n = 0; n <= 5; n++)
        {
            var f = Path.Combine(game, "Save", $"save{n}.hp1sav");
            if (!File.Exists(f)) continue;
            byte[] head;
            try { using var s = File.OpenRead(f); head = new byte[Math.Min(s.Length, 65536)]; s.ReadExactly(head); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            slots.Add((n + 1, ReadSlot(head)));
        }
        return Lines(mods.Where(m => m.Enabled).Select(m => m.Name).ToList(), slots);
    }
}

// HP2's front end: the native dialog HP2's Game.exe shows before the engine boots (pages Main Menu / New Game / Load Game
// / Options; Window.dll WFrontEndDialog). Its page flow is ported here; the launcher's HP2 tab draws it in the launcher's
// own look ("not original", DECISIONS "HP2 main menu in the launcher"). Judge: tools/hp2/frontend_judge.py runs the
// Game.exe bodies under unicorn against this class (Desktop.Tests --hp2-frontend). Paths are the original's strings
// (Core's FString operator*, '\' separators); Files maps them onto the install.
public static class Hp2FrontEnd
{
    public const int Slots = 6;                     // OnInitDialog's loop: slots 1..6 [HP2 Game.exe 0x109117f1, cmp 7]

    // The file system the dialog touches (GFileManager): FileSize (-1 missing), Delete, MakeDirectory, FindFiles (files only).
    public interface IFiles
    {
        long FileSize(string path);
        void Delete(string path);
        void MakeDirectory(string path);
        List<string> FindFiles(string pattern);
        void DeleteCache(string dir);   // not original: the port's <slot>/cache folder (Clear)
    }

    // The slot's save file. The original's is Save0.usa (the judge's cases); the port's is save0.hp1sav (the saves line's
    // format, decided 2026-11-16: slot n holds save0.hp1sav, Save0.bmp, <Map>_pa.usa, cache/), "used" iff >= 1 byte as
    // the original's test. Not original: the name only; the rule is the original's.
    public const string OriginalSave = "Save0.usa", PortSave = "save0.hp1sav";

    // FString::operator*(const TCHAR*) of HP2 Core: a '\' between the parts unless the left part is empty or ends with '\'
    // ('/' gets one too; frontend_judge scan cases "../Save/", "..\Save\", "").
    public static string Join(string a, string b) => a.Length == 0 || a[^1] == '\\' ? a + b : a + "\\" + b;

    // Slot n's directory: GSys->SavePath * "Slot" + appSprintf("%d", n) [HP2 Game.exe 0x109117ff..0x1091184f].
    public static string SlotDir(string savePath, int n) => Join(savePath, "Slot") + n.ToString(CultureInfo.InvariantCulture);

    public sealed record Slot(int N, string Dir, bool Used, string Thumb);

    // Original: not exported: FUN_10911320 (WFrontEndDialog::OnInitDialog of HP2's Game.exe) [HP2 Game.exe 0x10911320],
    // its slot loop 0x109117f1..0x10911b5e: MakeDirectory(dir); used = FileSize(dir * "Save0.usa") >= 1; a used slot's
    // button shows dir * "Save0.bmp", an unused one is cleared (Clear).
    public static List<Slot> Scan(string savePath, IFiles fs, string saveFile = OriginalSave)
    {
        var slots = new List<Slot>();
        for (int n = 1; n <= Slots; n++)
        {
            string dir = SlotDir(savePath, n);
            fs.MakeDirectory(dir);
            bool used = fs.FileSize(Join(dir, saveFile)) >= 1;
            if (!used) Clear(dir, fs);
            slots.Add(new Slot(n, dir, used, used ? Join(dir, "Save0.bmp") : null));
        }
        return slots;
    }

    // Original: not exported: FUN_109124f0 (clear a slot) [HP2 Game.exe 0x109124f0]: a non-empty directory's files
    // (FindFiles(dir * "*.*", files, no directories)) are deleted, but a zero-length one (`FileSize != 0`), then the slot is
    // unused. Not original: then the port's cache folder of the slot goes too (the original has none; saves line
    // 2026-11-16: clearing slot n deletes its save0.hp1sav, Save0.bmp, <Map>_pa.usa and cache/).
    public static void Clear(string dir, IFiles fs)
    {
        if (dir.Length == 0) return;
        foreach (var f in fs.FindFiles(Join(dir, "*.*")))
            if (fs.FileSize(Join(dir, f)) != 0) fs.Delete(Join(dir, f));
        fs.DeleteCache(dir);
    }

    public enum Launch { NewGame = 1, LoadGame = 2 }   // [FrontEnd] LaunchCode "1" / "2"

    // Original: not exported: the six New Game slot buttons [HP2 Game.exe 0x10913c10, 0x10913d70, 0x10913ed0, 0x10914030,
    // 0x10914190, 0x109142f0] (WFrontEndPageNewGame's delegates): a used slot asks MessageBoxW(MB_YESNO,
    // Select_Game_0001, Select_Game_0002) and IDNO does nothing; otherwise the slot is cleared and the dialog ends with
    // LaunchCode 1, UseSaveSlot n. Returns false when nothing happens.
    public static bool AsksToReplace(Slot slot) => slot.Used;
    public static bool NewGame(Slot slot, Func<bool> confirmReplace, IFiles fs)
    {
        if (AsksToReplace(slot) && !confirmReplace()) return false;
        Clear(slot.Dir, fs);
        return true;
    }

    // Original: not exported: the six Load Game slot buttons [HP2 Game.exe 0x10915370, 0x10915450, 0x10915530, 0x10915610,
    // 0x109156f0, 0x109157d0]: an unused slot does nothing; a used one ends the dialog with LaunchCode 2, UseSaveSlot n.
    public static bool LoadGame(Slot slot) => slot.Used;

    // What WinMain runs after the dialog for LaunchCode 1 / 2 (disasm 0x1090de16..0x1090df71) with UseSaveSlot (the slot as
    // "%d"): a new Game.exe process with these parameters; cmdline is the menu process's own appCmdLine().
    // Original: not exported: FUN_10902ba0 (New Game) [HP2 Game.exe 0x10902ba0]: "PrivetDr.unr " + " -SAVESLOT=%s" + cmdline.
    // Original: not exported: FUN_10903290 (Load Game) [HP2 Game.exe 0x10903290]: cmdline + " -LOADGAME=0 " + " -SAVESLOT=%s".
    public static string CommandLine(Launch what, int slot, string cmdline)
    {
        string s = " -SAVESLOT=" + slot.ToString(CultureInfo.InvariantCulture);
        return what == Launch.NewGame ? "PrivetDr.unr " + s + cmdline : cmdline + " -LOADGAME=0 " + s;
    }

    // A slot button's text: appSprintf("%d - %s", n, Localize(<page>, IDC_SlotEmpty | IDC_SlotUsed, "Startup"))
    // [HP2 Game.exe 0x109136b0 New Game, 0x10914ce0 Load Game].
    public static string Label(Slot slot, string empty, string used) => $"{slot.N} - {(slot.Used ? used : empty)}";

    // The port's HP2 save directory (D-01: the port keeps its own saves; the install's Save folder holds the player's real
    // HP2 saves and is never read or changed): beside the HP1 data directory, a parameter until the saves line fixes it.
    // The menu's SavePath maps onto it: slot n is <save dir>/Slot<n> (Scan with SavePath "").
    public static string SaveDir(string hp1DataDir) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(hp1DataDir)) ?? hp1DataDir, "HP2Data", "Save");

    // The menu's files under one directory: an original path ('\' or '/') is relative to it; a path that resolves outside
    // it is refused (nothing outside the port's save directory is read, made or deleted).
    public sealed class Files : IFiles
    {
        readonly string root;
        public Files(string dir) { root = Path.GetFullPath(dir); }
        public string Host(string p)
        {
            var h = Path.GetFullPath(Path.Combine(root, p.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
            return h == root || h.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? h : throw new UnauthorizedAccessException("outside the HP2 save directory: " + p);
        }
        public long FileSize(string p) { try { var f = new FileInfo(Host(p)); return f.Exists ? f.Length : -1; } catch (UnauthorizedAccessException) { return -1; } }
        public void Delete(string p) { try { File.Delete(Host(p)); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        public void DeleteCache(string dir) { try { var c = Host(Join(dir, "cache")); if (Directory.Exists(c)) Directory.Delete(c, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        public void MakeDirectory(string p) { try { Directory.CreateDirectory(Host(p)); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        public List<string> FindFiles(string pattern)
        {
            try { string dir = Path.GetDirectoryName(Host(pattern)); return Directory.Exists(dir) ? Directory.GetFiles(dir).Select(Path.GetFileName).ToList() : new(); }
            catch (IOException) { return new(); }
            catch (UnauthorizedAccessException) { return new(); }
        }
    }

    // The launcher's own English words for HP2's front-end pages (author 2026-11-20, NOT ORIGINAL): the launcher is
    // English, so its HP2 pages are too, whatever the game's language (they were the game's Startup/HPMenu Localize text,
    // Russian on the RU EA edition beside English buttons). The words are the US edition's Startup.int / HPMenu.int. Same
    // table as the macOS Launcher's Hp2FrontEnd.words (launcher/test_update.sh compares --hp2-menu).
    static readonly Dictionary<string, string> Words = new()
    {
        ["Startup/IDDIALOG_FrontEndPageMainMenu/IDC_Title"] = "Main Menu",
        ["Startup/IDDIALOG_FrontEndPageMainMenu/IDC_Instructions"] = "To Play Harry Potter click on New Game or Load Game. Click on Options for Video, Sound and Joystick options.",
        ["Startup/IDDIALOG_FrontEndPageMainMenu/IDC_NewGame"] = "New Game",
        ["Startup/IDDIALOG_FrontEndPageMainMenu/IDC_LoadGame"] = "Load Game",
        ["Startup/IDDIALOG_FrontEndPageMainMenu/IDC_Options"] = "Options",
        ["Startup/IDDIALOG_FrontEndPageNewGame/IDC_Title"] = "New Game",
        ["Startup/IDDIALOG_FrontEndPageNewGame/IDC_Instructions"] = "Start a NEW game by clicking on the slot you wish to use.",
        ["Startup/IDDIALOG_FrontEndPageNewGame/IDC_SlotEmpty"] = "Empty",
        ["Startup/IDDIALOG_FrontEndPageNewGame/IDC_SlotUsed"] = "Used",
        ["Startup/IDDIALOG_FrontEndPageLoadGame/IDC_Title"] = "Loading Game",
        ["Startup/IDDIALOG_FrontEndPageLoadGame/IDC_Instructions"] = "Load a game by clicking on a previously used slot.",
        ["Startup/IDDIALOG_FrontEndPageLoadGame/IDC_SlotEmpty"] = "Empty",
        ["Startup/IDDIALOG_FrontEndPageLoadGame/IDC_SlotUsed"] = "Used",
        ["HPMenu/All/Select_Game_0001"] = "Are you sure you want to replace this game?",
        ["HPMenu/All/Select_Game_0002"] = "Replace Game",
    };
    public static string Localize(string gameDir, string package, string section, string key)
        => Words.TryGetValue(package + "/" + section + "/" + key, out var v) ? v : key;

    // HPEngine's arguments for the new process: the HP2 folder and the original's parameters, word for word
    // (--hp2-cmdline; the engine side belongs to the HP2 boot/saves lines).
    // --hp2-savedir: the port's HP2 save directory (SaveDir), where the slots are.
    public static List<string> PlayArgs(string gameDir, string saveDir, string cmdline) => new() { "--root", gameDir, "--launched-by-launcher", "--hp2-savedir", saveDir, "--hp2-cmdline", cmdline };
}

// The Options pages of HP2's front-end dialog (Config: Video / Sound / Joystick). The launcher has no Options page (author
// 2026-11-16: these settings live in the game's own menu). Kept only for their checks: ReadSound / WriteSound are judged
// against Game.exe by tools/hp2/frontend_judge.sh (soundinit / soundok) and, with ReadVideo / WriteVideo, pin the macOS
// mirror in launcher/test_update.sh (--hp2-frontend); delete them together with those cases.
public static class Hp2Options
{
    public const string Engine = "Engine.GameEngine", Audio = "ALAudio.ALAudioSubsystem";
    public sealed record Sound(bool NoSound, int SoundPos, int MusicPos);

    // Original: not exported: FUN_10919e40 (WFrontEndPageConfigSound::OnInitDialog) [HP2 Game.exe 0x10919e40]:
    // "Disable all sound" checked unless appStricmp(GConfig [Engine.GameEngine] UseSound, "True") == 0 (a missing key
    // reads ""); each slider (range 0..100) SetPos(ftol(appAtof(volume) * 100.0f)) — appAtof leaves atof's double on
    // the FPU, the product is truncated (disasm 0x1091a204..0x1091a213: "0.29" -> 28).
    public static Sound ReadSound(Func<string, string, string> get) => new(
        !string.Equals(get(Engine, "UseSound") ?? "", "True", StringComparison.OrdinalIgnoreCase),
        Pos(get(Audio, "SoundVolume")), Pos(get(Audio, "MusicVolume")));

    // msvcrt _ftol: truncation to 64 bits, the low 32 kept; out of range (or NaN) the integer indefinite (low half 0).
    static int Pos(string v)
    {
        double x = Atof(v ?? "") * 100.0;
        return double.IsNaN(x) || Math.Abs(x) >= 9.2233720368547758e18 ? 0 : unchecked((int)(long)Math.Truncate(x));
    }

    // atof: leading white space, then the longest decimal prefix (sign, digits, point, exponent); none -> 0.
    public static double Atof(string s)
    {
        var m = System.Text.RegularExpressions.Regex.Match(s, @"^[ \t\n\r\f\v]*([+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?)");
        return m.Success ? double.Parse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture) : 0.0;
    }

    // Original: not exported: FUN_1091a950 (WFrontEndPageConfigSound OK) [HP2 Game.exe 0x1091a950]: UseSound "False"
    // when BM_GETCHECK == 1 else "True", then each volume appSprintf("%f", GetPos / 100.0f), in this order; then
    // GConfig->Flush.
    public static List<(string Section, string Key, string Value)> WriteSound(int check, int soundPos, int musicPos) => new()
    {
        (Engine, "UseSound", check == 1 ? "False" : "True"),
        (Audio, "SoundVolume", (soundPos / 100.0).ToString("F6", CultureInfo.InvariantCulture)),
        (Audio, "MusicVolume", (musicPos / 100.0).ToString("F6", CultureInfo.InvariantCulture)),
    };

    // NOT ORIGINAL (DECISIONS 2026-11-16, Video page, coordinator): the original's Video page [HP2 Game.exe init 0x10916810,
    // OK 0x109187a0] chooses Direct3D / Software devices HPEngine does not have; this page shows HPEngine's own window
    // settings instead, the keys its window opens with (UEViewportStartup): [WinDrv.WindowsClient] StartupFullscreen and
    // WindowedViewportX/Y (fullscreen is always the desktop's size). Read as the engine does: "True" / "1" (any case,
    // trimmed) is on; a size is a positive integer, else 0 (shown as the field's minimum).
    public const string Client = "WinDrv.WindowsClient";
    public sealed record Video(bool Fullscreen, int Width, int Height);
    public static Video ReadVideo(Func<string, string, string> get) => new(
        (get(Client, "StartupFullscreen") ?? "").Trim() is var f && (f.Equals("True", StringComparison.OrdinalIgnoreCase) || f == "1"),
        Size(get(Client, "WindowedViewportX")), Size(get(Client, "WindowedViewportY")));
    static int Size(string v) => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : 0;
    public static List<(string Section, string Key, string Value)> WriteVideo(bool fullscreen, int width, int height) => new()
    {
        (Client, "StartupFullscreen", fullscreen ? "True" : "False"),
        (Client, "WindowedViewportX", width.ToString(CultureInfo.InvariantCulture)),
        (Client, "WindowedViewportY", height.ToString(CultureInfo.InvariantCulture)),
    };
}
