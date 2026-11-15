// "Add to Steam" (Linux): HPEngine as a non-Steam game in <steam>/userdata/<id>/config/shortcuts.vdf (binary VDF). Steam rewrites that
// file when it exits, so it is written only while Steam is not running. The new entry is spliced in before the end of the "shortcuts"
// map: every existing byte stays as it is. One entry per Exe (an earlier add is recognised and skipped); the old file is kept as
// shortcuts.vdf.hpengine-backup-<time>. Every Steam account on this machine (each numeric userdata/<id>, not 0) gets the entry.
using System.Text;

namespace HPEngine.Launcher;

public static class SteamShortcuts
{
    // The Steam folder (userdata/ inside), null if none: the native install, then the Flatpak one (com.valvesoftware.Steam keeps
    // its data under ~/.var/app). SteamRunning needs no root: the Flatpak client's process is "steam" in the host's /proc too.
    public static string FindRoot(string home) =>
        new[] { Path.Combine(home, ".steam", "steam"), Path.Combine(home, ".local", "share", "Steam"),
                Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam") }.FirstOrDefault(d => Directory.Exists(Path.Combine(d, "userdata")));

    public static List<string> Accounts(string root) =>
        Directory.GetDirectories(Path.Combine(root, "userdata")).Where(d => Path.GetFileName(d).All(char.IsDigit) && Path.GetFileName(d) != "0").OrderBy(d => d, StringComparer.Ordinal).ToList();

    public static bool SteamRunning() =>
        Directory.Exists("/proc") && Directory.EnumerateDirectories("/proc").Any(d => { try { return File.ReadAllText(Path.Combine(d, "comm")).Trim() == "steam"; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; } });

    static string CStr(byte[] b, ref int p)
    {
        int s = p; while (p < b.Length && b[p] != 0) p++;
        if (p >= b.Length) throw new FormatException("unterminated string");
        return Encoding.UTF8.GetString(b, s, p++ - s);
    }

    // Reads a map's children up to its 0x08; calls `str` for string values. Types: 0 map, 1 string, 2 int32 (all Steam writes here).
    static void ReadMap(byte[] b, ref int p, int depth, Action<int, string, string> str)
    {
        while (true)
        {
            if (p >= b.Length) throw new FormatException("missing end of map");
            byte t = b[p++];
            if (t == 8) return;
            var key = CStr(b, ref p);
            switch (t)
            {
                case 0: ReadMap(b, ref p, depth + 1, str); break;
                case 1: str(depth, key, CStr(b, ref p)); break;
                case 2: if (p + 4 > b.Length) throw new FormatException("cut int"); p += 4; break;
                default: throw new FormatException($"unsupported value type {t}");
            }
        }
    }

    // The entries (index, Exe) and the offset of the 0x08 that ends the "shortcuts" map. FormatException: not a file we can splice into.
    public static (List<(int Index, string Exe)> Entries, int End) Parse(byte[] b)
    {
        int p = 0;
        if (b.Length < 2 || b[p++] != 0 || !CStr(b, ref p).Equals("shortcuts", StringComparison.OrdinalIgnoreCase)) throw new FormatException("not a shortcuts.vdf");
        var list = new List<(int, string)>(); int end;
        while (true)   // the children of "shortcuts": numbered maps
        {
            if (p >= b.Length) throw new FormatException("missing end of shortcuts");
            if (b[p] == 8) { end = p++; break; }
            if (b[p++] != 0) throw new FormatException("a shortcut is not a map");
            if (!int.TryParse(CStr(b, ref p), out var cur)) throw new FormatException("a shortcut key is not a number");
            ReadMap(b, ref p, 2, (d, k, v) => { if (d == 2 && k.Equals("exe", StringComparison.OrdinalIgnoreCase)) list.Add((cur, v)); });
        }
        if (p != b.Length - 1 || b[p] != 8) throw new FormatException("unexpected data after shortcuts");
        return (list, end);
    }

    static string Q(string s) => "\"" + s + "\"";
    public static bool Has(byte[] vdf, string exe) => Parse(vdf).Entries.Any(e => e.Exe == Q(exe) || e.Exe == exe);
    public static readonly byte[] Empty = Encoding.ASCII.GetBytes("\0shortcuts\0\b\b");

    static uint Crc32(byte[] d) { uint c = 0xFFFFFFFF; foreach (var x in d) { c ^= x; for (int i = 0; i < 8; i++) c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320 : c >> 1; } return ~c; }

    // The file with the entry added (Has must be false); the bytes before and after the splice are the old ones.
    public static byte[] Add(byte[] vdf, string name, string exe, string startDir)
    {
        var (entries, end) = Parse(vdf);
        var ms = new MemoryStream(); ms.Write(vdf, 0, end);
        void Z(string s) { ms.Write(Encoding.UTF8.GetBytes(s)); ms.WriteByte(0); }
        void S(string k, string v) { ms.WriteByte(1); Z(k); Z(v); }
        void I(string k, uint v) { ms.WriteByte(2); Z(k); ms.Write(BitConverter.GetBytes(v)); }
        ms.WriteByte(0); Z((entries.Count == 0 ? 0 : entries.Max(e => e.Index) + 1).ToString());
        I("appid", Crc32(Encoding.UTF8.GetBytes(Q(exe) + name)) | 0x80000000);   // as Steam derives it for a non-Steam shortcut
        S("AppName", name); S("Exe", Q(exe)); S("StartDir", Q(startDir)); S("icon", ""); S("ShortcutPath", ""); S("LaunchOptions", "");
        I("IsHidden", 0); I("AllowDesktopConfig", 1); I("AllowOverlay", 1); I("OpenVR", 0); I("Devkit", 0); S("DevkitGameID", ""); I("DevkitOverrideAppID", 0);
        I("LastPlayTime", 0); S("FlatpakAppID", "");
        ms.WriteByte(0); Z("tags"); ms.WriteByte(8);   // empty tags map
        ms.WriteByte(8);                               // end of the entry
        ms.Write(vdf, end, vdf.Length - end);
        return ms.ToArray();
    }

    // The text for the player. Writes nothing while Steam runs, and nothing to a file it cannot read as a shortcuts.vdf.
    public static string AddTo(string root, string name, string exe, string startDir, Func<bool> running)
    {
        if (root == null) return "Steam was not found (looked in ~/.steam/steam, ~/.local/share/Steam and the Flatpak ~/.var/app/com.valvesoftware.Steam).";
        List<string> accounts; bool isRunning;
        try { accounts = Accounts(root); isRunning = accounts.Count > 0 && running(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "Nothing was added: Steam's folders could not be read (" + e.Message + ")."; }
        if (accounts.Count == 0) return "Steam has no user account on this computer yet: start Steam and sign in once, then try again.";
        if (isRunning) return "Steam is running: close it completely, then try again (Steam overwrites its shortcuts when it exits).";
        int added = 0, had = 0; var problems = new List<string>();
        foreach (var acc in accounts)
        {
            var file = Path.Combine(acc, "config", "shortcuts.vdf");
            try
            {
                var old = File.Exists(file) ? File.ReadAllBytes(file) : Empty;
                if (Has(old, exe)) { had++; continue; }
                var nw = Add(old, name, exe, startDir);
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                if (File.Exists(file)) File.Copy(file, file + ".hpengine-backup-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
                File.WriteAllBytes(file + ".tmp", nw); File.Move(file + ".tmp", file, true);
                added++;
            }
            catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException)
            { problems.Add($"{Path.GetFileName(acc)}: {e.Message}"); }
        }
        var r = added > 0 ? $"Added HPEngine to Steam ({added} account{(added == 1 ? "" : "s")}). Start Steam to see it in your library."
            : had > 0 && problems.Count == 0 ? "HPEngine is already in your Steam library." : "Nothing was added.";
        if (added > 0 && had > 0) r += $" {had} account(s) had it already.";
        return problems.Count == 0 ? r : r + " Not changed: " + string.Join("; ", problems);
    }
}
