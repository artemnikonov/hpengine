// Launcher logic without UI (checked by launcher/Desktop.Tests): HP1Setup's output protocol, Mods.ini, the data
// directory per OS, the GitHub release choice (the same rules as Launcher.older/pick in launcher/Launcher.swift).
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace HPEngine.Launcher;

public enum SetupKind { Step, Progress, Error, Warning, Edition, Done, Malformed }

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
            case "DONE":
                return arg.Length == 0 ? Bad("DONE without a root") : new(SetupKind.Done, "", arg, -1, 0, line);
            default:
                return Bad("unknown line");
        }
    }
}

public sealed record ModEntry(string Name, bool Enabled, bool Present);

// <game>/HPEngineData/Mods.ini: "[Mods]" with one "Enabled=<name>" per enabled mod, in order (a later mod wins); ";"
// starts a comment. Everything else (comments, other keys, other sections, line endings) is written back unchanged.
public static class ModsIni
{
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

    // The banner: the engine's line when there is one, then where the whole log is.
    public static string Banner(IEnumerable<string> lines, int code, string log) =>
        $"The game stopped with an error (code {code})" + (Pick(lines, code) is string l ? ": " + l.Trim() : ".") + " See the log: " + log;
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

public sealed record Release(string Version, string Page);

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
    public static Release Pick(string json, string current)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            bool Flag(string n) => rel.TryGetProperty(n, out var f) && f.ValueKind == JsonValueKind.True;
            if (Flag("draft") || (Flag("prerelease") && !current.Contains('-'))) continue;
            if (!rel.TryGetProperty("tag_name", out var tag) || !rel.TryGetProperty("html_url", out var page)) continue;
            var v = tag.GetString().TrimStart('v');
            return Older(current, v) ? new Release(v, page.GetString()) : null;
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
        if (b == null || !Str(out var magic) || magic != "HP1SAV" || !I32(out int ver) || ver < 1 || ver > 5) return null;
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
