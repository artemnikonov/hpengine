// Checks of the desktop Launcher. Exit: 0 all passed, 1 a check failed, 3 NOVERDICT (a compared branch did not execute).
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using HPEngine.Launcher;

static class T
{
    static int fails;
    static readonly HashSet<string> fired = new();

    static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine((ok ? "ok    " : "FAIL  ") + name + (ok ? "" : " " + detail));
        if (!ok) fails++;
    }
    static void Eq<X>(string name, X want, X got) => Check(name, EqualityComparer<X>.Default.Equals(want, got), $"want '{want}', got '{got}'");
    static void Seq(string name, IEnumerable<string> want, IEnumerable<string> got) => Eq(name, string.Join("|", want), string.Join("|", got));

    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--shots") { Shots(args[1]); return 0; }
        // <game dir>: the Mods notice lines the window shows for it (MainWindow: ModsIni.Load, ModsNotice.Lines), one per
        // output line — for saves the engine wrote (audit/mods_saves_e2e.sh).
        if (args.Length == 2 && args[0] == "--mods-notice")
        {
            foreach (var l in ModsNotice.Lines(args[1], ModsIni.Load(args[1]))) Console.WriteLine(l);
            return 0;
        }
        // <game dir>: the window's "Start map" entries after "Game"; <code> <engine output file>: the error banner — for
        // real mods and the engine's real output (audit/mod_start_map_local.sh).
        if (args.Length == 2 && args[0] == "--start-maps")
        {
            foreach (var l in StartMap.Labels(StartMap.Added(args[1]))) Console.WriteLine(l);
            return 0;
        }
        if (args.Length == 3 && args[0] == "--engine-error")
        {
            Console.WriteLine(EngineError.Banner(File.ReadAllLines(args[2]), int.Parse(args[1]), "L."));
            return 0;
        }
        Protocol(); Ini(); Dirs(); Releases(); Removal(); Smoke(); Notice(); Errors(); ModProblems(); StartMaps(); OwnGame(); PadResetRule();
        Console.WriteLine("branches: " + string.Join(" ", fired.OrderBy(x => x)));
        if (fails > 0) return 1;
        foreach (var b in new[] { "proto:all-kinds", "proto:malformed", "ini:roundtrip", "ini:missing-section", "dir:3os", "pick:prerelease-build", "smoke:not-installed", "smoke:installed", "smoke:toggle", "smoke:reorder", "rm:keep", "rm:all", "rm:marker-first", "rm:symlink", "notice:cases", "notice:window", "err:pick", "mods:missing", "mods:misplaced", "mods:play-blocked", "map:list", "map:window", "own:rule", "own:window", "pad:reset", "pad:nothing" })
            if (!fired.Contains(b)) { Console.WriteLine("NOVERDICT: branch did not execute: " + b); return 3; }
        if (!OperatingSystem.IsWindows() && !fired.Contains("map:play-args")) { Console.WriteLine("NOVERDICT: branch did not execute: map:play-args"); return 3; }
        Console.WriteLine("all passed");
        return 0;
    }

    static void Protocol()
    {
        var k = (string l) => SetupProtocol.Parse(l);
        Eq("STEP copy", (SetupKind.Step, 3), (k("STEP copy").Kind, k("STEP copy").Index));
        Eq("PROGRESS 42", 0.42, k("PROGRESS 42").Progress);
        var e = k("ERROR not_ours This folder holds a game.");
        Eq("ERROR key/text", ("not_ours", "This folder holds a game."), (e.Key, e.Text));
        Eq("WARNING", (SetupKind.Warning, "modified", "Modified scripts."), (k("WARNING modified Modified scripts.").Kind, k("WARNING modified Modified scripts.").Key, k("WARNING modified Modified scripts.").Text));
        var ed = k("EDITION us153 US 1.53: exact");
        Eq("EDITION", ("us153", "US 1.53: exact"), (ed.Key, ed.Text));
        Eq("DONE", (SetupKind.Done, "/x/game"), (k("DONE /x/game").Kind, k("DONE /x/game").Text));
        fired.Add("proto:all-kinds");
        foreach (var bad in new[] { "", "HELLO", "STEP", "STEP bogus", "PROGRESS", "PROGRESS x", "PROGRESS 101", "PROGRESS -1", "ERROR", "ERROR keyonly", "WARNING k ", "EDITION", "DONE" })
            Eq($"malformed '{bad}'", SetupKind.Malformed, k(bad).Kind);
        fired.Add("proto:malformed");
    }

    static void Ini()
    {
        var src = "; my mods\r\n[Other]\r\nKey=1\r\n\r\n[Mods]\r\n; first\r\nNote=keep\r\nEnabled=Beta\r\nEnabled=Alpha\r\n\r\n[Tail]\r\nX=2\r\n";
        Seq("read order", new[] { "Beta", "Alpha" }, ModsIni.ReadEnabled(src));
        Eq("round-trip unchanged", src, ModsIni.WriteEnabled(src, new[] { "Beta", "Alpha" }));
        var re = ModsIni.WriteEnabled(src, new[] { "Alpha", "Gamma" });
        Eq("reorder keeps comments/sections/CRLF", "; my mods\r\n[Other]\r\nKey=1\r\n\r\n[Mods]\r\n; first\r\nNote=keep\r\nEnabled=Alpha\r\nEnabled=Gamma\r\n\r\n[Tail]\r\nX=2\r\n", re);
        Seq("read after write", new[] { "Alpha", "Gamma" }, ModsIni.ReadEnabled(re));
        Seq("Enabled in another section ignored", Array.Empty<string>(), ModsIni.ReadEnabled("[Other]\nEnabled=Zed\n"));
        Eq("empty list removes lines only", "[Mods]\n; c\n", ModsIni.WriteEnabled("[Mods]\nEnabled=A\n; c\n", Array.Empty<string>()));
        fired.Add("ini:roundtrip");
        Eq("no file: new section", "[Mods]\nEnabled=A\n", ModsIni.WriteEnabled("", new[] { "A" }));
        Eq("other section only: [Mods] appended", "[Other]\nK=1\n\n[Mods]\nEnabled=A\n", ModsIni.WriteEnabled("[Other]\nK=1\n", new[] { "A" }));
        Eq("empty [Mods] filled", "[Mods]\nEnabled=A\n[X]\n", ModsIni.WriteEnabled("[Mods]\n[X]\n", new[] { "A" }));
        fired.Add("ini:missing-section");
    }

    static void Dirs()
    {
        string Win(Dictionary<string, string> e) => DataDir.Game(Os.Windows, n => e.GetValueOrDefault(n), "/home/u");
        Eq("win LOCALAPPDATA", Path.Combine("C:/L", "HPEngine", "game"), Win(new() { ["LOCALAPPDATA"] = "C:/L" }));
        Eq("win fallback", Path.Combine("/home/u", "AppData", "Local", "HPEngine", "game"), Win(new()));
        Eq("linux XDG", Path.Combine("/x/data", "HPEngine", "game"), DataDir.Game(Os.Linux, n => n == "XDG_DATA_HOME" ? "/x/data" : null, "/home/u"));
        Eq("linux default", Path.Combine("/home/u", ".local", "share", "HPEngine", "game"), DataDir.Game(Os.Linux, _ => null, "/home/u"));
        Eq("linux relative XDG ignored (spec)", Path.Combine("/home/u", ".local", "share", "HPEngine", "game"), DataDir.Game(Os.Linux, n => n == "XDG_DATA_HOME" ? "rel/dir" : null, "/home/u"));
        Eq("mac", Path.Combine("/Users/u", "Library", "Application Support", "HPEngine", "game"), DataDir.Game(Os.MacOS, _ => null, "/Users/u"));
        fired.Add("dir:3os");
    }

    static void Releases()
    {
        const string feed = """
        [{"tag_name":"v0.4.0","draft":true,"prerelease":false,"html_url":"https://example.invalid/4"},
         {"tag_name":"v0.3.0-beta","draft":false,"prerelease":true,"html_url":"https://example.invalid/3"},
         {"tag_name":"v0.2.0","draft":false,"prerelease":false,"html_url":"https://example.invalid/2"}]
        """;
        string P(string json, string cur) => Updates.Pick(json, cur) is { } r ? r.Version + " " + r.Page : "none";
        Eq("final build skips prerelease and draft", "0.2.0 https://example.invalid/2", P(feed, "0.1.0"));
        Eq("prerelease build takes prerelease", "0.3.0-beta https://example.invalid/3", P(feed, "0.1.0-beta"));
        fired.Add("pick:prerelease-build");
        Eq("up to date", "none", P(feed, "0.2.0"));
        Eq("final after beta", "0.2.0 https://example.invalid/2", P("""[{"tag_name":"v0.2.0","html_url":"https://example.invalid/2"}]""", "0.2.0-beta"));
        string Rel(string v) => $$"""[{"tag_name":"v{{v}}","draft":false,"prerelease":true,"html_url":"https://example.invalid/p"}]""";
        Eq("beta.2 after beta", "0.1.0-beta.2 https://example.invalid/p", P(Rel("0.1.0-beta.2"), "0.1.0-beta"));
        Eq("numeric identifier as number", "0.1.0-beta.10 https://example.invalid/p", P(Rel("0.1.0-beta.10"), "0.1.0-beta.2"));
        Eq("rc after beta", "0.1.0-rc1 https://example.invalid/p", P(Rel("0.1.0-rc1"), "0.1.0-beta.10"));
        Eq("beta not after rc", "none", P(Rel("0.1.0-beta.2"), "0.1.0-rc1"));
        Check("bad feed is loud", Throws(() => Updates.Pick("{}", "0.1.0")) && Throws(() => Updates.Pick("nope", "0.1.0")));
    }
    static string MakeGame(string tmp, string name)
    {
        var g = Path.Combine(tmp, name);
        foreach (var d in new[] { "System", "Maps", "Save", "HPEngineData/logs", "Mods/Alpha", "Maps.new" }) Directory.CreateDirectory(Path.Combine(g, d));
        foreach (var f in new[] { ".hp1-installed", "System/a.u", "Maps/b.unr", "Maps.new/c.unr", "Save/slot1.sav", "HPEngineData/Default.ini", "HPEngineData/logs/l.txt", "Mods/Alpha/mod.txt" })
            File.WriteAllText(Path.Combine(g, f), "x:" + f);
        return g;
    }

    // A save body head as the engine writes it (BinaryWriter, the same encoding as HP1.Core UELevelRuntime.SaveGame).
    static byte[] SaveHead(int version, params string[] mods)
    {
        var ms = new MemoryStream(); var w = new BinaryWriter(ms, new System.Text.UTF8Encoding(false));
        w.Write("HP1SAV"); w.Write(version);
        if (version >= 5) { w.Write(mods.Length); foreach (var m in mods) w.Write(m); }
        w.Write("Lev_Tut1"); w.Write(0L); w.Flush();
        return ms.ToArray();
    }

    static void Notice()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "hp1-notice-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(tmp);
        try
        {
            var A = new[] { "A" };
            Seq("reader: v5 with mods, in order", new[] { "B", "A" }, ModsNotice.ReadSlot(SaveHead(5, "B", "A")).Mods);
            Check("reader: v5 without mods", ModsNotice.ReadSlot(SaveHead(5)) is { Mods.Count: 0, Legacy: false });
            Check("reader: v4 is legacy, no mods", ModsNotice.ReadSlot(SaveHead(4)) is { Mods.Count: 0, Legacy: true });
            var full = SaveHead(5, "Alpha");
            Check("reader: truncated -> unknown", ModsNotice.ReadSlot(full[..12]) == null && ModsNotice.ReadSlot(Array.Empty<byte>()) == null);
            Check("reader: not a save -> unknown", ModsNotice.ReadSlot(System.Text.Encoding.ASCII.GetBytes("garbage file")) == null);
            Check("reader: newer version -> unknown", ModsNotice.ReadSlot(SaveHead(6)) == null);
            var bad = (byte[])full.Clone(); bad[11] = 0xff; bad[12] = 0xff; bad[13] = 0xff; bad[14] = 0x7f;
            Check("reader: absurd mod count -> unknown", ModsNotice.ReadSlot(bad) == null);
            (int, ModsNotice.SlotMods) S(int n, int v, params string[] m) => (n, ModsNotice.ReadSlot(SaveHead(v, m)));
            Check("lines: every slot like the enabled mods: none", ModsNotice.Lines(A, new[] { S(1, 5, "a"), S(2, 5, "A") }).Count == 0);
            Check("lines: no mods, no-mod and legacy slots: none", ModsNotice.Lines(Array.Empty<string>(), new[] { S(1, 5), S(2, 4) }).Count == 0);
            Seq("lines: one slot", new[] { "Slot 2 was saved with other mods: loading it restarts its level with the progress you had when you entered it." },
                ModsNotice.Lines(A, new[] { S(1, 5, "A"), S(2, 5) }));
            Seq("lines: two slots", new[] { "Slots 1 and 3 were saved with other mods: loading them restarts their level with the progress you had when you entered it." },
                ModsNotice.Lines(Array.Empty<string>(), new[] { S(3, 5, "A"), S(1, 5, "B"), S(2, 5) }));
            Seq("lines: many slots, order counts, legacy with mods on, unknown skipped", new[] {
                    "Slots 1, 2 and 5 were saved with other mods: loading them restarts their level with the progress you had when you entered it.",
                    "Slot 4 was saved by an older version: it does not load while mods are on." },
                ModsNotice.Lines(new[] { "A", "B" }, new[] { S(1, 5, "B", "A"), S(2, 5, "A"), S(3, 5, "a", "b"), S(4, 4), S(5, 5), (6, null) }));

            var g = Path.Combine(tmp, "game"); Directory.CreateDirectory(Path.Combine(g, "HPEngineData")); Directory.CreateDirectory(Path.Combine(g, "Save"));
            File.WriteAllBytes(Path.Combine(g, "Save", "save0.hp1sav"), SaveHead(5, "Beta"));
            File.WriteAllBytes(Path.Combine(g, "Save", "save2.hp1sav"), SaveHead(5));
            File.WriteAllBytes(Path.Combine(g, "Save", "save99.hp1sav"), SaveHead(5));
            File.WriteAllBytes(Path.Combine(g, "Save", "save6.hp1sav"), SaveHead(5));
            File.WriteAllBytes(Path.Combine(g, "Save", "save4.hp1sav"), new byte[] { 1, 2, 3 });
            Seq("files: slots 0..5 as Slot N+1, autosave 99 and save6 not slots, damaged skipped",
                new[] { "Slot 3 was saved with other mods: loading it restarts its level with the progress you had when you entered it." },
                ModsNotice.Lines(g, new[] { new ModEntry("Beta", true, true), new ModEntry("Alpha", false, true) }));
            fired.Add("notice:cases");

            foreach (var m in new[] { "Alpha", "Beta" }) Directory.CreateDirectory(Path.Combine(g, "Mods", m));
            File.WriteAllText(Path.Combine(g, ".hp1-installed"), "edition=US 1.53: exact\n");
            File.WriteAllText(ModsIni.IniPath(g), "[Mods]\nEnabled=Beta\nEnabled=Alpha\n");
            var w = new MainWindow(g, tmp, "0.1.0-beta", false); w.Show(); Dispatcher.UIThread.RunJobs();
            Check("window: the slot lines shown", Find<StackPanel>(w, "ModsNotice").IsVisible && Find<TextBlock>(w, "ModsNoticeText").Text ==
                "Slots 1 and 3 were saved with other mods: loading them restarts their level with the progress you had when you entered it.");
            Find<ToggleSwitch>(w, "Mod_Alpha").IsChecked = false; Dispatcher.UIThread.RunJobs();
            Check("window: only the slot that differs from the new list", Find<StackPanel>(w, "ModsNotice").IsVisible && Find<TextBlock>(w, "ModsNoticeText").Text ==
                "Slot 3 was saved with other mods: loading it restarts its level with the progress you had when you entered it.");
            File.WriteAllBytes(Path.Combine(g, "Save", "save2.hp1sav"), SaveHead(5, "Beta")); Find<Button>(w, "RefreshMods").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
            Check("window: no line when every slot matches", !Find<StackPanel>(w, "ModsNotice").IsVisible);
            w.Close();
            fired.Add("notice:window");
        }
        finally { Directory.Delete(tmp, true); }
    }

    // The engine's own error line for the banner (lines as the engine prints them: HPEngineProgram, InstallRoot, ModOverlay).
    static void Errors()
    {
        var mods = new[] { "[Log] journal: /g/HPEngineData/logs/run.log", "[Mods] none", "[Mods] mod \"Ghost\" is enabled in /g/HPEngineData/Mods.ini but there is no folder /g/Mods/Ghost" };
        Eq("code 5: the [Mods] error line", mods[2], EngineError.Pick(mods, 5));
        var root = new[] { "--root /g: this is not a game install (/g: no directory Maps/).", "Hint: the easy way is the HPEngine Launcher: it installs your copy of the game and starts it. Or unpack HPEngine into the game install directory." };
        Eq("code 4: the folder line, not the hint", root[0], EngineError.Pick(root, 4));
        Eq("code 4: not found", "game install not found: --root not set", EngineError.Pick(new[] { "x", "game install not found: --root not set", "Hint: h" }, 4));
        var crash = new[] { "[Mods] none", "[Crash] unhandled exception: System.InvalidDataException: bad package", "   at HP1.UEScript.Load()", "Unhandled exception. System.InvalidDataException: bad package", "   at HP1.UEScript.Load()" };
        Eq("crash: the run log's crash line", crash[1], EngineError.Pick(crash, 134));
        Eq("crash: the runtime's line when there is no run log", crash[3], EngineError.Pick(crash.Skip(2), 134));
        Check("no line: nothing is made up", EngineError.Pick(new[] { "[Mods] none", "[Perf] 60 fps" }, 2) == null && EngineError.Pick(mods, 4) == null);
        Eq("banner with the line", "The game stopped with an error (code 5): " + mods[2] + " See the log: L.", EngineError.Banner(mods, 5, "L."));
        Eq("banner without a line", "The game stopped with an error (code 2). See the log: L.", EngineError.Banner(new[] { "[Mods] none" }, 2, "L."));
        var noMap = new[] { "[Mods] active: Xmas (0 game files replaced, 1 added) -> /g/HPEngineData/modroot", "[Map] there is no map \"nosuch\": no Maps/nosuch.unr in the game or in an enabled mod" };
        Eq("code 6: the [Map] line", noMap[1], EngineError.Pick(noMap, 6));
        fired.Add("err:pick");
    }

    // An enabled mod without its folder blocks Play; a mod unpacked one level too deep is named (ModOverlay.Result.Misplaced).
    static void ModProblems()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "hp1-mods-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var g = Path.Combine(tmp, "game");
            foreach (var d in new[] { "System", "Maps", "HPEngineData", "HPEngine", "Mods/Alpha/system", "Mods/Deep/Deep-v1/System", "Mods/Deep/Maps", "Mods/Off/Off-v1" }) Directory.CreateDirectory(Path.Combine(g, d));
            foreach (var f in new[] { "Mods/Alpha/system/A.u", "Mods/Alpha/A.u", "Mods/Deep/Deep-v1/System/D.u", "Mods/Deep/Deep-v1/D2.u", "Mods/Deep/Deep-v1/.DS_Store", "Mods/Deep/Maps/D.unr", "Mods/Off/Off-v1/O.u" })
                File.WriteAllText(Path.Combine(g, f), "x");
            File.WriteAllText(Path.Combine(g, ".hp1-installed"), "edition=US 1.53: exact\n");
            File.WriteAllText(ModsIni.IniPath(g), "[Mods]\nEnabled=alpha\nEnabled=Ghost\nEnabled=Deep\n");
            var mods = ModsIni.Load(g);
            Eq("load: folder found without case, listed once", "alpha+1|Ghost+0|Deep+1|Off-1", string.Join("|", mods.Select(m => m.Name + (m.Enabled ? "+" : "-") + (m.Present ? 1 : 0))));
            const string one = "The mod Ghost is on, but its folder is gone. Turn off Ghost or put its folder back into Mods.";
            Eq("missing: one", one, ModsIni.Missing(mods));
            Eq("missing: several", "The mods A, B and C are on, but their folders are gone. Turn off A, B and C or put their folders back into Mods.",
                ModsIni.Missing(new[] { "A", "B", "C" }.Select(n => new ModEntry(n, true, false))));
            Check("missing: none (a disabled mod without folder does not count)", ModsIni.Missing(new[] { new ModEntry("A", true, true), new ModEntry("B", false, false) }) == null);
            fired.Add("mods:missing");
            const string deep = "2 files of Deep are not in a game folder (System, Maps, ...): unpack the mod so these folders are directly inside Mods/Deep";
            Seq("misplaced: enabled mods only, hidden files and well-placed files not counted", new[] { deep }, ModsIni.Misplaced(g, mods));
            Seq("misplaced: one file", new[] { "1 file of Off is not in a game folder (System, Maps, ...): unpack the mod so these folders are directly inside Mods/Off" },
                ModsIni.Misplaced(g, new[] { new ModEntry("Off", true, true) }));
            fired.Add("mods:misplaced");

            var w = new MainWindow(g, tmp, "0.1.0-beta", false); w.Show(); Dispatcher.UIThread.RunJobs();
            Check("window: the misplaced line shown", Find<StackPanel>(w, "ModsNotice").IsVisible && Find<TextBlock>(w, "ModsNoticeText").Text == deep);
            Find<Button>(w, "PlayButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
            Eq("window: Play blocked, the mod named", one, Find<TextBlock>(w, "ErrorText").Text);
            Directory.CreateDirectory(Path.Combine(g, "Mods", "Ghost"));   // the folder is put back, no Refresh pressed
            Find<Button>(w, "PlayButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
            Check("window: folder back, Play goes on (to the engine, absent here)", Find<TextBlock>(w, "ErrorText").Text.StartsWith("HPEngine was not found"), Find<TextBlock>(w, "ErrorText").Text);
            w.Close();
            fired.Add("mods:play-blocked");
        }
        finally { Directory.Delete(tmp, true); }
    }

    // "Start map": the maps the enabled mods add (ModOverlay.AddedMaps, the engine's placement), the window's choice and
    // the engine arguments of Play (a stand-in engine that writes its arguments down; not on Windows: a shell script).
    static void StartMaps()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "hp1-startmap-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var g = Path.Combine(tmp, "game");
            var files = new[] { "System/Default.ini", "Maps/Lev_Tut1.unr", "Maps/Startup.unr", "HPEngineData/x",
                "Mods/Replace/Maps/lev_tut1.unr",                                   // a shipped map under another case: replaced, not added
                "Mods/Xmas/maps/acniabwc.unr", "Mods/Xmas/ReadMe.txt",
                "Mods/Flat/Early_Potions.unr", "Mods/Flat/STARTUP.UNR", "Mods/Flat/Maps/sub/deep.unr", "Mods/Flat/System/Flat.u",
                "Mods/Off/Maps/Zzz.unr",
                "Mods/Total/Maps/b.unr", "Mods/Total/Maps/A.unr", "Mods/Total/Maps/Zed.unr", "Mods/Total/Maps/acniabwc.unr", "Mods/Empty/readme.txt" };
            foreach (var f in files) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(g, f))); File.WriteAllText(Path.Combine(g, f), "x"); }
            File.WriteAllText(Path.Combine(g, "System/Default.ini"), "[Core.System]\nPaths=../System/*.u\nPaths=../Maps/*.unr\n");
            File.WriteAllText(Path.Combine(g, ".hp1-installed"), "edition=US 1.53: exact\n");
            string List(params string[] enabled)
            {
                File.WriteAllText(ModsIni.IniPath(g), "[Mods]\n" + string.Concat(enabled.Select(m => $"Enabled={m}\n")));
                var maps = StartMap.Added(g);
                return string.Join("|", maps.Select(m => m.Map + "@" + m.Mod)) + " / " + string.Join("|", StartMap.Labels(maps));
            }
            File.Delete(ModsIni.IniPath(g));
            Check("no Mods.ini: no start maps", StartMap.Added(g).Count == 0);
            Eq("no mod on (Off has a map)", " / ", List());
            Eq("a mod that only replaces a shipped map (other case)", " / ", List("Replace"));
            Eq("a laid-out map (maps/, any case)", "acniabwc@Xmas / acniabwc", List("Xmas"));
            Eq("a flat .unr; the flat copy of a shipped map and a map in a subfolder of Maps are not start maps", "Early_Potions@Flat / Early_Potions", List("Flat"));
            Eq("two mods: by name, the mod named", "acniabwc@Xmas|Early_Potions@Flat / acniabwc  (Xmas)|Early_Potions  (Flat)", List("Flat", "Xmas", "Replace"));
            Eq("many maps of one mod: by name without case", "A@Total|acniabwc@Total|b@Total|Zed@Total / A|acniabwc|b|Zed", List("Total"));
            Eq("a map two mods add: once, the later mod's", "A@Total|acniabwc@Total|b@Total|Zed@Total / A|acniabwc|b|Zed", List("Xmas", "Total"));
            Eq("mods that cannot be placed (no folder; no game files): none, no exception", " /  /  / ", List("Xmas", "Ghost") + " / " + List("Xmas", "Empty"));
            Seq("Play arguments: normal start", new[] { "--root", g, "--launched-by-launcher" }, StartMap.PlayArgs(g, null));
            Seq("Play arguments: a map", new[] { "--root", g, "--launched-by-launcher", "--map", "Early_Potions" }, StartMap.PlayArgs(g, "Early_Potions"));
            fired.Add("map:list");

            List("Replace");
            var w = new MainWindow(g, tmp, "0.1.0-beta", false); w.Show(); Dispatcher.UIThread.RunJobs();
            var box = Find<StackPanel>(w, "StartMapBox"); var combo = Find<ComboBox>(w, "StartMap");
            string Items() => string.Join("|", combo.Items.Cast<string>()) + " #" + combo.SelectedIndex;
            Check("window: no added map, no choice", !box.IsVisible && Find<Button>(w, "PlayButton").IsVisible);
            Find<ToggleSwitch>(w, "Mod_Xmas").IsChecked = true; Dispatcher.UIThread.RunJobs();
            Check("window: a map mod on, the choice appears", box.IsVisible);
            Eq("window: Game first and chosen", "Game (normal start)|acniabwc #0", Items());
            combo.SelectedIndex = 1;
            Find<ToggleSwitch>(w, "Mod_Flat").IsChecked = true; Dispatcher.UIThread.RunJobs();
            Eq("window: the chosen map stays chosen when the list changes", "Game (normal start)|acniabwc  (Xmas)|Early_Potions  (Flat) #1", Items());
            Find<ToggleSwitch>(w, "Mod_Xmas").IsChecked = false; Dispatcher.UIThread.RunJobs();
            Eq("window: the chosen map's mod off, back to Game", "Game (normal start)|Early_Potions #0", Items());
            fired.Add("map:window");
            if (!OperatingSystem.IsWindows())
            {
                var exe = Path.Combine(tmp, "HPEngine", "HPEngine"); Directory.CreateDirectory(Path.GetDirectoryName(exe));
                File.WriteAllText(exe, "#!/bin/sh\nprintf '%s\\n' \"$@\" > \"$0.args.tmp\" && mv \"$0.args.tmp\" \"$0.args\"\n");
                File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                string Played()
                {
                    File.Delete(exe + ".args");
                    Find<Button>(w, "PlayButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    for (int i = 0; i < 500 && !(File.Exists(exe + ".args") && Find<Button>(w, "PlayButton").IsEnabled); i++) { Thread.Sleep(20); Dispatcher.UIThread.RunJobs(); }
                    return File.Exists(exe + ".args") ? string.Join(" ", File.ReadAllLines(exe + ".args")) : Find<TextBlock>(w, "ErrorText").Text;
                }
                Eq("window: Play with Game passes no --map", $"--root {g} --launched-by-launcher", Played());
                combo.SelectedIndex = 1;
                Eq("window: Play with a map passes --map", $"--root {g} --launched-by-launcher --map Early_Potions", Played());
                combo.SelectedIndex = 1;
                Directory.Delete(Path.Combine(g, "Mods", "Flat"), true); File.WriteAllText(ModsIni.IniPath(g), "[Mods]\n");   // changed outside the window, no Refresh
                Eq("window: the chosen map gone at Play, normal start", $"--root {g} --launched-by-launcher", Played());
                Check("window: and the choice is gone", !box.IsVisible);
                fired.Add("map:play-args");
            }
            w.Close();
        }
        finally { Directory.Delete(tmp, true); }
    }

    // The archive unpacked into the player's game folder: that game is used, by the engine's own rule (InstallRoot).
    static void OwnGame()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "hp1-own-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string D(string name, params string[] sub) { var d = Path.Combine(tmp, name); Directory.CreateDirectory(d); foreach (var s in sub) Directory.CreateDirectory(Path.Combine(d, s)); return d; }
            var data = D("data"); var own = D("own", "System", "Maps"); var bare = D("bare", "HPEngine"); var half = D("half", "System");
            Check("rule: a game next to the Launcher", DataDir.OwnGame(own, data));
            Check("rule: no game next to the Launcher", !DataDir.OwnGame(bare, data));
            Check("rule: System/ alone is not a game (the engine's rule)", !DataDir.OwnGame(half, data));
            Check("rule: the Launcher's folder is the data directory itself", !DataDir.OwnGame(own, own + Path.DirectorySeparatorChar));
            fired.Add("own:rule");

            Directory.CreateDirectory(Path.Combine(own, "Mods", "Cam", "System"));
            var w = new MainWindow(data, own, "0.1.0-beta", false); w.Show(); Dispatcher.UIThread.RunJobs();
            Check("window: ready without the Launcher's marker, no Install", Find<StackPanel>(w, "ReadyPanel").IsVisible && Find<Button>(w, "PlayButton").IsVisible
                && !Find<StackPanel>(w, "SetupPanel").IsVisible && !Find<Button>(w, "InstallButton").IsVisible && Find<TextBlock>(w, "Status").Text.Contains("Ready to play"));
            Eq("window: the game folder shown", "Your game folder: " + own, Find<TextBlock>(w, "EditionText").Text);
            var cam = Find<ToggleSwitch>(w, "Mod_Cam");
            Check("window: the mods of that game", cam != null);
            if (cam != null) { cam.IsChecked = true; Dispatcher.UIThread.RunJobs(); }
            Check("window: Mods.ini of that game, nothing in the data directory", File.Exists(ModsIni.IniPath(own)) && !Directory.EnumerateFileSystemEntries(data).Any());
            w.Close();
            fired.Add("own:window");
        }
        finally { Directory.Delete(tmp, true); }
    }

    // "Reset Gamepad Bindings" (PadReset, the PAD RESET RULE): only the Joy* lines of [Engine.Input] and the saved flag go.
    static void PadResetRule()
    {
        var g = Path.Combine(Path.GetTempPath(), "hp1-pad-test-" + Guid.NewGuid().ToString("N")); var d = Path.Combine(g, "HPEngineData");
        Directory.CreateDirectory(d);
        try
        {
            string user = Path.Combine(d, "DefUser.ini"), sys = Path.Combine(d, "Default.ini");
            Eq("nothing saved: nothing to reset", 0, PadReset.Rebound(g));
            File.WriteAllText(user, "[Engine.Input]\nScrollLock=Walking\nUp=MoveForward\n\n[Other]\nJoy1=keep\n");
            File.WriteAllText(sys, "[HPEngine]\nUIScaleStep=1\nGamepadLookSpeed=6\n\n[Engine.Input]\nJoy1=not the user file\n");
            var (u0, s0) = (File.ReadAllBytes(user), File.ReadAllBytes(sys));
            Eq("keyboard-only config: nothing to reset", 0, PadReset.Rebound(g));
            Eq("keyboard-only config: reset removes nothing", 0, PadReset.Rebound(g, true));
            Check("keyboard-only config: files byte-identical", File.ReadAllBytes(user).SequenceEqual(u0) && File.ReadAllBytes(sys).SequenceEqual(s0));
            fired.Add("pad:nothing");
            File.WriteAllText(user, "[Engine.Input]\r\nScrollLock=Walking\r\nJoy1=\r\nJoy13=JUMP\r\njoypovup = Button bBroomBrake\r\nJoyful\r\nUp=MoveForward | Joy\r\n\r\n[Other]\r\nJoy1=keep\r\nName=caf\u00e9\r\n", System.Text.Encoding.Latin1);
            File.WriteAllText(sys, "[HPEngine]\nUIScaleStep=1\ngamepadbindingssaved=True\nGamepadLookSpeed=6\n\n[Engine.Input]\nJoy1=not the user file\nGamepadBindingsSaved=other section\n");
            Eq("rebound: lines to remove", 4, PadReset.Rebound(g));
            Check("rebound: counting writes nothing", File.ReadAllText(user).Contains("Joy13=JUMP"));
            Eq("reset: lines removed", 4, PadReset.Rebound(g, true));
            Eq("reset: user file keeps everything but the pad lines of [Engine.Input]",
                "[Engine.Input]\r\nScrollLock=Walking\r\nJoyful\r\nUp=MoveForward | Joy\r\n\r\n[Other]\r\nJoy1=keep\r\nName=caf\u00e9\r\n", File.ReadAllText(user, System.Text.Encoding.Latin1));
            Eq("reset: system file keeps everything but the saved flag of [HPEngine]",
                "[HPEngine]\nUIScaleStep=1\nGamepadLookSpeed=6\n\n[Engine.Input]\nJoy1=not the user file\nGamepadBindingsSaved=other section\n", File.ReadAllText(sys));
            Eq("reset: nothing left to reset", 0, PadReset.Rebound(g));
            fired.Add("pad:reset");
        }
        finally { Directory.Delete(g, true); }
    }

    static string Rd(string p) { try { return File.ReadAllText(p); } catch (IOException) { return null; } }

    static void Removal()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "hp1-rm-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(tmp);
        try
        {
            bool E(string g, string p) => File.Exists(Path.Combine(g, p)) || Directory.Exists(Path.Combine(g, p));
            var g = MakeGame(tmp, "keep");
            GameRemoval.Remove(g, false);
            Check("keep: game data and marker gone", !E(g, ".hp1-installed") && !E(g, "System") && !E(g, "Maps") && !E(g, "Maps.new"));
            Check("keep: Save, HPEngineData, Mods kept intact", Rd(Path.Combine(g, "Save/slot1.sav")) == "x:Save/slot1.sav"
                && Rd(Path.Combine(g, "HPEngineData/logs/l.txt")) == "x:HPEngineData/logs/l.txt" && Rd(Path.Combine(g, "HPEngineData/Default.ini")) == "x:HPEngineData/Default.ini"
                && Rd(Path.Combine(g, "Mods/Alpha/mod.txt")) == "x:Mods/Alpha/mod.txt");
            fired.Add("rm:keep");
            g = MakeGame(tmp, "all");
            GameRemoval.Remove(g, true);
            Check("all: everything removed", !Directory.Exists(g));
            fired.Add("rm:all");
            g = MakeGame(tmp, "fail");
            Check("failure after the marker is raised", Throws(() => GameRemoval.Remove(g, true, afterMarker: () => throw new IOException("simulated"))));
            Check("marker gone first, the rest still there (reads as not installed)", !E(g, ".hp1-installed") && E(g, "System/a.u") && E(g, "Save/slot1.sav"));
            fired.Add("rm:marker-first");
            g = MakeGame(tmp, "link");
            var outside = Path.Combine(tmp, "outside"); Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "keep.txt"), "target");
            Directory.CreateSymbolicLink(Path.Combine(g, "Maps", "lnk"), outside);                       // inside what keep-mode deletes
            Directory.CreateDirectory(Path.Combine(g, "HPEngineData", "modroot")); Directory.CreateSymbolicLink(Path.Combine(g, "HPEngineData", "modroot", "Alpha"), outside);
            GameRemoval.Remove(g, false);
            Check("symlink target outside survives (keep)", Rd(Path.Combine(outside, "keep.txt")) == "target" && !E(g, "Maps"));
            GameRemoval.Remove(g, true);
            Check("symlink target outside survives (all, link in HPEngineData)", Rd(Path.Combine(outside, "keep.txt")) == "target" && !Directory.Exists(g));
            fired.Add("rm:symlink");
        }
        finally { Directory.Delete(tmp, true); }
    }

    static bool Throws(Action a) { try { a(); return false; } catch (Exception) { return true; } }

    // ---- headless window ----
    static T1 Find<T1>(Window w, string name) where T1 : Control => w.GetLogicalDescendants().OfType<T1>().FirstOrDefault(c => c.Name == name);

    static void Smoke()
    {
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true }).SetupWithoutStarting();
        var tmp = Path.Combine(Path.GetTempPath(), "hp1-desktop-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            // not installed
            var game = Path.Combine(tmp, "game"); Directory.CreateDirectory(tmp);
            var w = new MainWindow(game, tmp, "0.1.0-beta", false); w.Show(); Dispatcher.UIThread.RunJobs();
            Check("not installed: setup visible", Find<StackPanel>(w, "SetupPanel").IsVisible && !Find<StackPanel>(w, "ReadyPanel").IsVisible);
            Check("not installed: Install shown and disabled, no Play", Find<Button>(w, "InstallButton") is { IsVisible: true, IsEnabled: false } && !Find<Button>(w, "PlayButton").IsVisible);
            Check("not installed: status", Find<TextBlock>(w, "Status").Text.Contains("Not installed"));
            w.Close(); fired.Add("smoke:not-installed");

            // installed, 3 mod folders, Beta then Alpha enabled
            Directory.CreateDirectory(Path.Combine(game, "HPEngineData"));
            File.WriteAllText(Path.Combine(game, ".hp1-installed"), "data=x\nedition=US 1.53: exact\n");
            foreach (var m in new[] { "Alpha", "Beta", "Gamma" }) Directory.CreateDirectory(Path.Combine(game, "Mods", m));
            var ini = Path.Combine(game, "HPEngineData", "Mods.ini");
            File.WriteAllText(ini, "[Mods]\n; order matters\nEnabled=Beta\nEnabled=Alpha\n");
            w = new MainWindow(game, tmp, "0.1.0-beta", false); w.Show(); Dispatcher.UIThread.RunJobs();
            Check("installed: ready, Play shown", Find<StackPanel>(w, "ReadyPanel").IsVisible && Find<Button>(w, "PlayButton").IsVisible && !Find<StackPanel>(w, "SetupPanel").IsVisible);
            Check("installed: edition from marker", Find<TextBlock>(w, "EditionText").Text == "US 1.53" && Find<TextBlock>(w, "EditionChip").Text == "verified");
            string Rows() => string.Join("|", Find<StackPanel>(w, "ModsList").GetLogicalDescendants().OfType<ToggleSwitch>().Select(c => c.Name[4..] + (c.IsChecked == true ? "+" : "-")));
            Eq("mods list reflects Mods.ini", "Beta+|Alpha+|Gamma-", Rows());
            fired.Add("smoke:installed");
            Find<ToggleSwitch>(w, "Mod_Gamma").IsChecked = true; Dispatcher.UIThread.RunJobs();
            Seq("toggle on: Gamma last, written", new[] { "Beta", "Alpha", "Gamma" }, ModsIni.ReadEnabled(File.ReadAllText(ini)));
            Check("toggle keeps the comment", File.ReadAllText(ini).Contains("; order matters"));
            Find<ToggleSwitch>(w, "Mod_Beta").IsChecked = false; Dispatcher.UIThread.RunJobs();
            Eq("toggle off", "Alpha+|Gamma+|Beta-", Rows());
            Seq("toggle off written", new[] { "Alpha", "Gamma" }, ModsIni.ReadEnabled(File.ReadAllText(ini)));
            fired.Add("smoke:toggle");
            Find<Button>(w, "Up_Gamma").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
            Eq("up", "Gamma+|Alpha+|Beta-", Rows());
            Seq("up written", new[] { "Gamma", "Alpha" }, ModsIni.ReadEnabled(File.ReadAllText(ini)));
            Check("first has no Up, last enabled no Down", !Find<Button>(w, "Up_Gamma").IsEnabled && !Find<Button>(w, "Down_Alpha").IsEnabled && Find<Button>(w, "Up_Beta") == null);
            fired.Add("smoke:reorder");
            // HP1Setup lines drive the same window
            var odd = new List<string>();
            w.Handle("WARNING unverified Odd edition.", odd); w.Handle("garbage", odd);
            Check("malformed setup line shown", Find<TextBlock>(w, "WarningText").Text.Contains("garbage") && odd.Count == 1);
            w.Close();
        }
        finally { Directory.Delete(tmp, true); }
    }

    // Screenshots (Skia, headless; no window opens): sh launcher/test_desktop.sh shots <dir>
    static void Shots(string dir)
    {
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(Path.GetTempPath(), "hp1-shots-" + Guid.NewGuid().ToString("N"));
        try
        {
            void Snap(MainWindow w, string name)
            {
                w.Show(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
                var f = w.CaptureRenderedFrame(); f.Save(Path.Combine(dir, name)); Console.WriteLine($"{name} {f.PixelSize.Width}x{f.PixelSize.Height}"); w.Close();
            }
            var empty = Path.Combine(tmp, "empty"); Directory.CreateDirectory(tmp);
            ShieldIcon.Draw(256).Save(Path.Combine(dir, "avalonia_icon.png"));   // the window icon (ShieldIcon.cs)
            Snap(new MainWindow(empty, tmp, "0.1.0-beta", false), "avalonia_empty.png");
            var game = Path.Combine(tmp, "game"); Directory.CreateDirectory(Path.Combine(game, "HPEngineData"));
            File.WriteAllText(Path.Combine(game, ".hp1-installed"), "data=x\nedition=US 1.53: exact\n");
            foreach (var m in new[] { "Alpha", "Beta", "Gamma" }) Directory.CreateDirectory(Path.Combine(game, "Mods", m));
            File.WriteAllText(Path.Combine(game, "HPEngineData", "Mods.ini"), "[Mods]\nEnabled=Beta\nEnabled=Alpha\n");
            Snap(new MainWindow(game, tmp, "0.1.0-beta", false), "avalonia_installed.png");
            var w = new MainWindow(game, tmp, "0.1.0-beta", false);
            w.Handle("WARNING unverified Unknown edition: System/HP.exe SHA-1 0123456789abcdef. Please report the hashes at the issues page so this release can be added.", new List<string>());
            Snap(w, "avalonia_warning.png");
            Directory.CreateDirectory(Path.Combine(game, "Save"));
            File.WriteAllBytes(Path.Combine(game, "Save", "save0.hp1sav"), SaveHead(5));
            File.WriteAllBytes(Path.Combine(game, "Save", "save2.hp1sav"), SaveHead(5, "Alpha"));
            Snap(new MainWindow(game, tmp, "0.1.0-beta", false), "avalonia_mods_notice.png");
            foreach (var f in new[] { "Maps/Lev_Tut1.unr", "Mods/Alpha/Maps/acniabwc.unr", "Mods/Beta/Maps/Early_Potions.unr" })
            { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(game, f))); File.WriteAllText(Path.Combine(game, f), "x"); }
            Snap(new MainWindow(game, tmp, "0.1.0-beta", false), "avalonia_start_map.png");   // the "Start map" choice next to Play
        }
        finally { Directory.Delete(tmp, true); }
    }
}
