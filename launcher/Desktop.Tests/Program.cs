// Checks of the desktop Launcher. Exit: 0 all passed, 1 a check failed, 3 NOVERDICT (a compared branch did not execute).
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HPEngine.Launcher;

static partial class T
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
        // The language modes of launcher/Launcher.swift, run on GameLanguage.cs (the owner): launcher/test_update.sh compares.
        if (args.Length == 3 && args[0] == "--language-select")
        {
            try { HPEngine.Packaging.GameLanguage.Save(args[1], args[2] == "default" ? null : args[2]); Console.WriteLine("OK"); return 0; }
            catch (InvalidDataException e) { Console.WriteLine("FAIL " + e.Message); return 1; }
        }
        if (args.Length == 2 && args[0] == "--language-list")
        {
            foreach (var l in HPEngine.Packaging.GameLanguage.Available(args[1])) Console.WriteLine($"{l.Code}|{l.Label}|{l.Folder ?? "-"}");
            Console.WriteLine("selected=" + (HPEngine.Packaging.GameLanguage.Selected(args[1]) ?? "-"));
            return 0;
        }
        if (args.Length == 2 && args[0] == "--override-warning")
        {
            Console.WriteLine(EngineError.OverrideWarning(File.ReadAllLines(args[1])) ?? "-");
            return 0;
        }
        if (args.Length == 3 && args[0] == "--edition-label")
        {
            var d = EditionLabel.Describe(args[1], HPEngine.Packaging.GameLanguage.Available(args[2]));
            Console.WriteLine($"{d.Name}|{d.Detail ?? "-"}");
            return 0;
        }
        if (args.Length == 3 && args[0] == "--hp2-frontend") return Hp2FrontEndCases(args[1], args[2]);
        if ((args.Length == 3 || args.Length == 6) && args[0] == "--hp2-menu") return Hp2Menu(args);
        if (args.Length is 2 or 3 && args[0] == "--mods-conflicts")   // [data dir]: HP2's layout
        {
            foreach (var l in ModsIni.Conflicts(args[1], args.Length == 3 ? args[2] : null)) Console.WriteLine(l);
            return 0;
        }
        if (args.Length == 3 && args[0] == "--newer-package")   // the owner of launcher/Launcher.swift newerPackage
        { Console.WriteLine(HPEngine.Packaging.ModOverlay.NewerPackage(args[1], int.Parse(args[2])) ?? "-"); return 0; }
        if (args.Length == 3 && args[0] == "--language-shot")
        { LanguageShot(args[1], args[2]); return 0; }
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
        if (args.Length == 2 && args[0] == "--game-scope") { foreach (var l in ScopeTable(args[1])) Console.WriteLine(l); return 0; }
        if (args.Length == 1 && args[0] == "--pad-map") { foreach (var l in PadTable()) Console.WriteLine(l); return 0; }
        if (args.Length == 3 && args[0] == "--engine-error")
        {
            Console.WriteLine(EngineError.Banner(File.ReadAllLines(args[2]), int.Parse(args[1])));
            return 0;
        }
        // The checks below run the preview build's HP2 tab; Hp2Off checks the release switch off.
        bool releaseDefault = Hp2Release.Available; Hp2Release.Available = true;
        Protocol(); Ini(); Dirs(); Releases(); Removal(); Smoke(); Hp2Off(releaseDefault); Notice(); Errors(); ModProblems(); ModConflicts(); StartMaps(); OwnGame(); PadResetRule(); PadCases(); ScopeCases(); SelfUpdateCases(); SteamCases(); TitleCases(); Languages(); Hp2Tab(); Hp2Install(); ErrorBannerWindow(); ModAdd(); Hp2Mods();
        Console.WriteLine("branches: " + string.Join(" ", fired.OrderBy(x => x)));
        if (fails > 0) return 1;
        foreach (var b in new[] { "language:overlay", "language:window", "language:missing", "language:stale", "language:default", "language:edition" })
            if (!fired.Contains(b)) { Console.WriteLine("NOVERDICT: branch did not execute: " + b); return 3; }
        foreach (var b in new[] { "proto:all-kinds", "proto:malformed", "ini:roundtrip", "ini:missing-section", "dir:3os", "pick:prerelease-build", "smoke:not-installed", "smoke:installed", "smoke:toggle", "smoke:reorder", "smoke:pad", "rm:keep", "rm:all", "rm:marker-first", "rm:symlink", "notice:cases", "notice:window", "err:pick", "mods:missing", "mods:misplaced", "mods:play-blocked", "mods:conflict", "mods:order-window", "map:list", "map:window", "own:rule", "own:window", "pad:reset", "pad:nothing", "pad:map", "keys:shortcuts", "scope:rules", "scope:window", "hp2:off", "title:rules", "title:window", "steam:vdf", "steam:running", "steam:add", "steam:damaged", "steam:denied" })
            if (!fired.Contains(b)) { Console.WriteLine("NOVERDICT: branch did not execute: " + b); return 3; }
        foreach (var b in new[] { "upd:signed-good", "upd:refused", "upd:zip", "upd:apply", "upd:rollback", "upd:rollback-backup", "upd:rollback-incomplete", "upd:marker-shown", "upd:log-denied", "upd:helper-cleanup", "upd:helper", "upd:helper-waits", "upd:helper-timeout", "upd:install-error", "upd:pick" })
            if (!fired.Contains(b)) { Console.WriteLine("NOVERDICT: branch did not execute: " + b); return 3; }
        if (!OperatingSystem.IsWindows() && !Environment.IsPrivilegedProcess && !fired.Contains("upd:cleanup-denied")) { Console.WriteLine("NOVERDICT: branch did not execute: upd:cleanup-denied"); return 3; }
        foreach (var b in new[] { "hp2:tab", "hp2:hp1-unchanged", "hp2:mods", "mods:add", "hp2:newer-engine" })
            if (!fired.Contains(b)) { Console.WriteLine("NOVERDICT: branch did not execute: " + b); return 3; }
        foreach (var b in new[] { "hp2:play-args", "hp2:install" })
            if (!OperatingSystem.IsWindows() && !fired.Contains(b)) { Console.WriteLine("NOVERDICT: branch did not execute: " + b); return 3; }
        foreach (var b in new[] { "banner:run-log", "banner:console", "banner:dismiss" })
            if (!OperatingSystem.IsWindows() && !fired.Contains(b)) { Console.WriteLine("NOVERDICT: branch did not execute: " + b); return 3; }
        if (!fired.Contains("banner:open-log-rule")) { Console.WriteLine("NOVERDICT: branch did not execute: banner:open-log-rule"); return 3; }
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
        Eq("REPORT", (SetupKind.Report, "https://github.com/artemnikonov/hpengine/issues/new?title=x"), (k("REPORT https://github.com/artemnikonov/hpengine/issues/new?title=x").Kind, k("REPORT https://github.com/artemnikonov/hpengine/issues/new?title=x").Text));
        Eq("WARNING", (SetupKind.Warning, "modified", "Modified scripts."), (k("WARNING modified Modified scripts.").Kind, k("WARNING modified Modified scripts.").Key, k("WARNING modified Modified scripts.").Text));
        var ed = k("EDITION us153 US 1.53: exact");
        Eq("EDITION", ("us153", "US 1.53: exact"), (ed.Key, ed.Text));
        var miss = "[Overrides] WARNING: FEOptionsPage.IsSupportedResolution is not in the loaded scripts: the port's override of it does not apply";
        Eq("override miss -> a warning naming it", true, EngineError.OverrideWarning(new[] { "x", miss }) is string ow && ow.Contains("FEOptionsPage.IsSupportedResolution") && ow.StartsWith("1 of"));
        Eq("a changed override or other log lines -> no warning", null, EngineError.OverrideWarning(new[] { "[Overrides] WARNING: A.B differs from the original", "core boot 5 ms" }));
        Eq("DONE", (SetupKind.Done, "/x/game"), (k("DONE /x/game").Kind, k("DONE /x/game").Text));
        fired.Add("proto:all-kinds");
        foreach (var bad in new[] { "", "HELLO", "STEP", "STEP bogus", "PROGRESS", "PROGRESS x", "PROGRESS 101", "PROGRESS -1", "ERROR", "ERROR keyonly", "WARNING k ", "EDITION", "DONE", "REPORT", "REPORT http://x" })
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
            Check("reader: v6 (actor slots) reads like v5", ModsNotice.ReadSlot(SaveHead(6, "A")) is { Mods.Count: 1, Legacy: false });
            Check("reader: newer version -> unknown", ModsNotice.ReadSlot(SaveHead(7)) == null);
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
        // author 2026-11-17: plain words, no file path ("Open log" opens the right one), the cause on its own line
        Eq("banner with the line", "The game stopped with an error (code 5).\n" + mods[2], EngineError.Banner(mods, 5));
        Eq("banner without a line", "The game stopped with an error (code 2).", EngineError.Banner(new[] { "[Mods] none" }, 2));
        Eq("banner: crash", "The game stopped with an error (code 134).\n" + crash[1], EngineError.Banner(crash, 134));
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

    // Two enabled mods with a file at the same game path (ModOverlay.Conflicts: the engine's placement, without case; a
    // flat file goes where its extension's game directory is), and the apply-order numbers of the list.
    static void ModConflicts()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "hp1-conflict-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var g = Path.Combine(tmp, "game");
            foreach (var f in new[] { "System/Default.ini", "System/Shared.u", "Textures/A.utx", "HPEngineData/x",
                "Mods/A/Textures/A.utx", "Mods/A/System/Shared.u", "Mods/B/textures/a.utx", "Mods/B/Shared.u", "Mods/B/readme.txt",
                "Mods/C/Textures/A.UTX", "Mods/C/Textures/Own.utx", "Mods/D/Textures/A.utx", "Mods/E/System/E.u", "Mods/E/E.u" })
            { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(g, f))); File.WriteAllText(Path.Combine(g, f), "x"); }
            File.WriteAllText(Path.Combine(g, "System/Default.ini"), "[Core.System]\nPaths=../System/*.u\nPaths=../Textures/*.utx\n");
            File.WriteAllText(Path.Combine(g, ".hp1-installed"), "edition=US 1.53: exact\n");
            void On(params string[] m) => File.WriteAllText(ModsIni.IniPath(g), "[Mods]\n" + string.Concat(m.Select(x => $"Enabled={x}\n")));
            On("A", "B", "C");
            Seq("conflicts: per pair in order, paths without case, a flat file at its game directory", new[] {
                "B and A both have System/Shared.u and 1 more file: the game uses B's (later in the order)",
                "C and B both have Textures/A.UTX: the game uses C's (later in the order)" }, ModsIni.Conflicts(g));
            On("C", "A");
            Seq("conflicts: the other order names the other winner", new[] { "A and C both have Textures/A.utx: the game uses A's (later in the order)" }, ModsIni.Conflicts(g));
            On("A"); Seq("conflicts: one mod alone replaces game files, no line", Array.Empty<string>(), ModsIni.Conflicts(g));
            On("E", "D"); Seq("conflicts: one mod's flat and laid-out copy of a file is not a conflict", Array.Empty<string>(), ModsIni.Conflicts(g));
            On("A", "Ghost"); Seq("conflicts: mods that cannot be placed: none, no exception", Array.Empty<string>(), ModsIni.Conflicts(g));
            fired.Add("mods:conflict");
            On("A", "B", "C");
            var w = new MainWindow(g, tmp, "0.1.0-beta", false); w.Show(); Dispatcher.UIThread.RunJobs();
            Check("window: conflict lines in the Mods notice", Find<TextBlock>(w, "ModsNoticeText").Text.Contains("C and B both have Textures/A.UTX"), Find<TextBlock>(w, "ModsNoticeText").Text);
            string Order() => string.Join("|", new[] { "A", "B", "C", "D" }.Select(n => n + "=" + (Find<TextBlock>(w, "ModOrder_" + n)?.Text ?? "-")));
            Eq("window: enabled mods numbered in apply order, a disabled one not", "A=1|B=2|C=3|D=-", Order());
            Eq("window: the order hint", ModsIni.OrderHint, Find<TextBlock>(w, "ModsHint").Text);
            Find<Button>(w, "Up_C").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
            Eq("window: numbers follow a move", "A=1|B=3|C=2|D=-", Order());
            Check("window: the conflict lines follow a move", Find<TextBlock>(w, "ModsNoticeText").Text.Contains("B and C both have textures/a.utx"), Find<TextBlock>(w, "ModsNoticeText").Text);
            w.Close(); fired.Add("mods:order-window");
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
            // A long list (author 2026-11-16): 30 maps scroll in a drop-down that fits the window; End reaches the last map and
            // scrolls it into view, Home the first; the gamepad's Down (sent as the Down key) moves the choice and keeps it in view.
            for (int k = 1; k <= 30; k++) { var f = Path.Combine(g, "Mods", "Long", "Maps", $"L{k:00}.unr"); Directory.CreateDirectory(Path.GetDirectoryName(f)); File.WriteAllText(f, "x"); }
            var kept = File.ReadAllText(ModsIni.IniPath(g)); List("Long");
            var lw = new MainWindow(g, tmp, "0.1.0-beta", false); lw.Show(); Dispatcher.UIThread.RunJobs();
            var lc = Find<ComboBox>(lw, "StartMap");
            void Tick() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
            Control Focused() => TopLevel.GetTopLevel(lc.ContainerFromIndex(0) as Control ?? lc)?.FocusManager?.GetFocusedElement() as Control;
            int At() => Focused() is { } f ? lc.IndexFromContainer(f) : -1;
            void LKey(Avalonia.Input.Key k) { var f = Focused(); f?.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = k, Source = f }); Tick(); }
            bool InView(int index)
            {
                var item = lc.ContainerFromIndex(index) as Control; var sv = item?.FindAncestorOfType<ScrollViewer>();
                if (sv == null || item == null || !item.IsEffectivelyVisible) return false;
                var top = item.TranslatePoint(new Point(0, 0), sv); var bottom = item.TranslatePoint(new Point(0, item.Bounds.Height), sv);
                return top is { } t && bottom is { } b && t.Y >= -0.5 && b.Y <= sv.Viewport.Height + 0.5;
            }
            lc.Focus(); lc.IsDropDownOpen = true; Tick();
            var lsv = (lc.ContainerFromIndex(0) as Control)?.FindAncestorOfType<ScrollViewer>();   // in the drop-down's own popup
            Check("long list: the drop-down scrolls (its content taller than its viewport, at most the window)", lsv != null && lsv.Extent.Height > lsv.Viewport.Height + 1 && lsv.Viewport.Height <= lw.Height,
                $"extent {lsv?.Extent} viewport {lsv?.Viewport}");
            Check("long list: the scroll bar is always shown", lsv != null && !lsv.AllowAutoHide);
            Check("long list: opens with the choice focused", At() == 0, $"at {At()}");
            LKey(Avalonia.Input.Key.End);
            Check("long list: End reaches the last map, in view", At() == 30 && InView(30), $"at {At()}");
            LKey(Avalonia.Input.Key.Home);
            Check("long list: Home back to Game, in view", At() == 0 && InView(0), $"at {At()}");
            for (int k = 0; k < 15; k++) { lw.Pad(PadMap.Down); Tick(); }
            Check("long list: the pad's d-pad x15 keeps the focused map in view", At() == 15 && InView(15), $"at {At()}");
            lw.Pad(PadMap.A); Tick();
            Check("long list: A picks the focused map", !lc.IsDropDownOpen && lc.SelectedItem as string == "L15", $"{lc.SelectedItem} open={lc.IsDropDownOpen}");
            lw.Close(); File.WriteAllText(ModsIni.IniPath(g), kept); fired.Add("map:long-list");
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

    // The gamepad's one answer (PadMap): the whole table as launcher/Launcher.swift --pad-map prints it (test_update.sh
    // compares the two byte for byte): every button x popup x tab, the stick around half way, the presses between two polls.
    static IEnumerable<string> PadTable()
    {
        for (int b = 0; b <= 15; b++) foreach (var popup in new[] { false, true }) foreach (var hp2 in new[] { false, true })
            yield return $"button {b} popup={(popup ? 1 : 0)} hp2={(hp2 ? 1 : 0)} {PadMap.For(b, popup, hp2)}";
        foreach (var (x, y) in new[] { (0, 0), (16000, 0), (16001, 0), (-16000, 0), (-16001, 0), (0, 16001), (0, -16001), (0, 16000), (32767, 32767), (-32768, -32768), (-32768, 16001) })
            yield return $"stick {x} {y} {PadMap.Stick(x / 32767.0, y / 32767.0)}";
        foreach (var (now, last) in new[] { (0u, 0u), (1u, 0u), (1u, 1u), (0b1011u, 0b0001u), (1u << 14 | 1u << 9, 1u << 14), (1u << 15, 0u), (0x7FFFu, 0u) })
            yield return $"pressed {now} {last} {string.Join(",", PadMap.Pressed(now, last))}";
    }
    // Per game (GameScope): the table launcher/Launcher.swift --game-scope prints (test_update.sh compares).
    static IEnumerable<string> ScopeTable(string game)
    {
        foreach (var tab in new[] { false, true }) foreach (var h1 in new[] { null, "e1" }) foreach (var h2 in new[] { null, "e2" }) foreach (var l in new[] { null, "eL" })
            yield return $"error hp2Tab={(tab ? 1 : 0)} hp1={h1 ?? "-"} hp2={h2 ?? "-"} launcher={l ?? "-"} -> {GameScope.Error(tab, h1, h2, l) ?? "-"}";
        foreach (var hp2 in new[] { false, true })
            yield return $"game hp2={(hp2 ? 1 : 0)} data={GameScope.DataDir(game, hp2)} logs={GameScope.LogsDir(game, hp2)} console={GameScope.ConsoleLog("/d/HPEngine.log", hp2)}";
        foreach (var on in new[] { false, true }) foreach (var folder in new[] { false, true })
            yield return $"hp2tab available={(on ? 1 : 0)} folder={(folder ? 1 : 0)} caption={Hp2Release.Caption(on, folder) ?? "-"} tip={Hp2Release.Tip(on) ?? "-"}";
        foreach (var tab in new[] { false, true }) foreach (var h1 in new[] { null, "e1" }) foreach (var h2 in new[] { null, "e2" }) foreach (var l in new[] { null, "eL" })
        {
            var d = GameScope.Dismiss(tab, h1, h2, l);
            yield return $"dismiss hp2Tab={(tab ? 1 : 0)} hp1={h1 ?? "-"} hp2={h2 ?? "-"} launcher={l ?? "-"} -> hp1={d.Hp1 ?? "-"} hp2={d.Hp2 ?? "-"} launcher={d.Launcher ?? "-"}";
        }
        foreach (var (now, before) in OpenLogNames)
            yield return $"runlog now={string.Join(",", now)} before={string.Join(",", before)} -> {GameScope.RunLog(now, before) ?? "-"}";
        foreach (var (c, logs, console) in OpenLogFolders(game))
            yield return $"openlog {c} -> {GameScope.LogToOpen(logs, console, Before)[(game.Length + 1)..]}";
    }
    // The open-log cases both launchers print (Launcher.swift GameScope.table): names, then real folders.
    static readonly string[] Before = { "run_20261117_090000.log" };
    static readonly (string[] Now, string[] Before)[] OpenLogNames = {
        (new string[0], new string[0]), (new[] { "run_20261117_090000.log" }, Before), (new[] { "run_20261117_090000.log" }, new string[0]),
        (new[] { "run_20261117_090000.log", "run_20261117_090000_x.log" }, Before),
        (new[] { "run_20261117_090000.log", "run_20261117_100512.log", "run_20261117_100001.log", "notes.txt", "run_20261117_110000.txt" }, Before) };
    static IEnumerable<(string Case, string Logs, string Console)> OpenLogFolders(string game)
    {
        foreach (var c in new[] { "run", "console", "none" })
        {
            var d = Path.Combine(game, "openlog", c); var logs = Path.Combine(d, "logs"); var console = Path.Combine(d, "HPEngine.log");
            if (c != "none") { Directory.CreateDirectory(logs); File.WriteAllText(Path.Combine(logs, "run_20261117_090000.log"), "an earlier run"); File.WriteAllText(console, "console"); }
            if (c == "run") File.WriteAllText(Path.Combine(logs, "run_20261117_100000.log"), "[Crash] x");
            yield return (c, logs, console);
        }
    }
    // The game-error banner in the window (author 2026-11-17), with a stand-in engine that fails: plain text without a path,
    // no More/Less, "Open log" = that run's journal (an earlier one is not it) else the console capture, the × (and pad B on
    // it) clears that tab's error only, a new error shows the banner again.
    static void ErrorBannerWindow()
    {
        if (OperatingSystem.IsWindows()) return;   // the stand-in engine is a shell script
        var tmp = Path.Combine(Path.GetTempPath(), "hp1-banner-" + Guid.NewGuid().ToString("N"));
        try
        {
            var g = Path.Combine(tmp, "game"); var logs = Path.Combine(g, "HPEngineData", "logs"); Directory.CreateDirectory(logs);
            File.WriteAllText(Path.Combine(g, ".hp1-installed"), "data=x\nedition=US 1.53: exact\n");
            var old = Path.Combine(logs, "run_20000101_000000.log"); File.WriteAllText(old, "an earlier run");
            var h2 = Path.Combine(tmp, "hp2"); Directory.CreateDirectory(Path.Combine(h2, "System"));
            File.WriteAllText(Path.Combine(h2, "System", "Engine.dll"), "x");
            File.WriteAllText(Path.Combine(h2, "System", "Startup.int"), "[IDDIALOG_FrontEndPageMainMenu]\nIDC_NewGame=New Game\nIDC_LoadGame=Load Game\n[IDDIALOG_FrontEndPageNewGame]\nIDC_SlotEmpty=Empty\nIDC_SlotUsed=Used\n");
            File.WriteAllText(Path.Combine(tmp, "hp2-root.txt"), h2);
            var exe = Path.Combine(tmp, "HPEngine", "HPEngine"); Directory.CreateDirectory(Path.GetDirectoryName(exe));
            // "run": a crash after the run journal started (HPEngineProgram's run_<stamp>.log); else: no start map, before any journal (code 6)
            File.WriteAllText(exe, "#!/bin/sh\nread mode < \"$0.mode\"\nif [ \"$mode\" = run ]; then\n  echo '[Crash] unhandled exception: boom' > \"$2/HPEngineData/logs/run_$(date +%Y%m%d_%H%M%S).log\"\n"
                + "  echo '[Mods] none'; echo '[Crash] unhandled exception: boom'; echo '   at X()'; exit 134\nfi\necho '[Map] there is no map \"nosuch\"'; exit 6\n");
            File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var w = new MainWindow(g, tmp, "0.4.0", false); w.Show(); Dispatcher.UIThread.RunJobs();
            void Click(string n) { Find<Button>(w, n).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); }
            var text = Find<TextBlock>(w, "ErrorText");
            void Run(string mode, string button, string code)
            {
                File.WriteAllText(exe + ".mode", mode + "\n");
                Click(button);
                for (int i = 0; i < 500 && !(text.IsEffectivelyVisible && text.Text?.Contains(code) == true); i++) { Thread.Sleep(20); Dispatcher.UIThread.RunJobs(); }
            }
            Run("run", "PlayButton", "(code 134)");
            Eq("banner: what happened in plain words, the cause on its own line, no file path", "The game stopped with an error (code 134).\n[Crash] unhandled exception: boom", text.Text);
            var banner = text.GetLogicalAncestors().OfType<Border>().First();
            Seq("banner: Open log and the × only (no More / Less)", new[] { "OpenLogButton", "DismissButton" },
                banner.GetLogicalDescendants().OfType<Button>().Where(b => b.IsVisible).Select(b => b.Name ?? b.Content?.ToString()));
            Check("banner: the whole text wraps (no clamp)", text.TextWrapping == Avalonia.Media.TextWrapping.Wrap && text.TextTrimming == Avalonia.Media.TextTrimming.None);
            Eq("banner: the × is labelled Dismiss", "Dismiss", Avalonia.Automation.AutomationProperties.GetName(Find<Button>(w, "DismissButton")));
            var run = w.LogToOpen();
            Check("open log: this run's journal, not the earlier one", run != old && Path.GetDirectoryName(run) == logs && Path.GetFileName(run).StartsWith("run_") && File.ReadAllText(run).Contains("[Crash]"), run);
            fired.Add("banner:run-log");
            Run("early", "PlayButton", "(code 6)");
            Eq("banner: code 6 with its [Map] line", "The game stopped with an error (code 6).\n[Map] there is no map \"nosuch\"", text.Text);
            Eq("open log: no journal of this run: the console capture", Path.Combine(tmp, "HPEngine.log"), w.LogToOpen());
            fired.Add("banner:console");
            Click("TabHp2"); Click("Hp2NewGame");
            Run("early", "Hp2Slot3", "(code 6)");
            Check("banner: HP2's own error on its tab", text.IsEffectivelyVisible && Find<Button>(w, "DismissButton").IsEffectivelyVisible);
            Find<Button>(w, "DismissButton").Focus(Avalonia.Input.NavigationMethod.Directional); w.Pad(PadMap.B); Dispatcher.UIThread.RunJobs();
            Check("dismiss: pad B on the × closes HP2's banner", !text.IsEffectivelyVisible);
            Click("TabHp1");
            Check("dismiss: HP1's error stays on its tab", text.IsEffectivelyVisible && text.Text?.Contains("(code 6)") == true);
            Click("DismissButton");
            Check("dismiss: the × closes HP1's banner", !text.IsEffectivelyVisible);
            Run("run", "PlayButton", "(code 134)");
            Check("dismiss: a new error shows the banner again", text.IsEffectivelyVisible && Find<Button>(w, "OpenLogButton").IsEffectivelyVisible);
            fired.Add("banner:dismiss");
            w.Close();
        }
        finally { Directory.Delete(tmp, true); }
    }

    // The release switch off (Hp2Release, release 0.4.0): the HP2 tab disabled, "Coming soon", its tooltip; no click, Ctrl+2
    // or pad RB selects it; a saved HP2 folder is ignored. The plain build (no -p:HPEngineHp2) is the release.
    static void Hp2Off(bool releaseDefault)
    {
        Check("hp2 off: the default build is the release (HP2 off)", !releaseDefault);   // after Smoke: the headless platform is set up
        var tmp = Path.Combine(Path.GetTempPath(), "hp1-hp2off-" + Guid.NewGuid().ToString("N"));
        Hp2Release.Available = false;
        try
        {
            var game = Path.Combine(tmp, "game"); Directory.CreateDirectory(Path.Combine(game, "HPEngineData"));
            File.WriteAllText(Path.Combine(game, ".hp1-installed"), "data=x\nedition=US 1.53: exact\n");
            var h2 = Path.Combine(tmp, "hp2"); Directory.CreateDirectory(Path.Combine(h2, "System"));
            File.WriteAllText(Path.Combine(h2, "System", "Engine.dll"), "x"); File.WriteAllText(Path.Combine(h2, "System", "Startup.int"), "[IDDIALOG_FrontEndPageMainMenu]\n");
            File.WriteAllText(Path.Combine(tmp, "hp2-root.txt"), h2);   // a saved HP2 folder: ignored while off
            var w = new MainWindow(game, tmp, "0.4.0", false); w.Show(); Dispatcher.UIThread.RunJobs();
            var tab = Find<Button>(w, "TabHp2");
            Check("hp2 off: the tab disabled, \"Coming soon\", its tooltip", !tab.IsEnabled && ((StackPanel)tab.Content).Children.OfType<TextBlock>().Any(t => t.IsVisible && t.Text == "· Coming soon")
                && (ToolTip.GetTip(tab) as string) == "Harry Potter 2 is coming soon. Wait for updates.");
            tab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
            w.Pad(PadMap.RB); Dispatcher.UIThread.RunJobs();
            w.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.D2, KeyModifiers = Avalonia.Input.KeyModifiers.Control }); Dispatcher.UIThread.RunJobs();
            Check("hp2 off: click, pad RB and Ctrl+2 leave HP1 selected", Find<StackPanel>(w, "ReadyPanel").IsVisible && !Find<StackPanel>(w, "Hp2Panel").IsVisible);
            w.Close(); fired.Add("hp2:off");
        }
        finally { Hp2Release.Available = true; Directory.Delete(tmp, true); }
    }

    static void ScopeCases()
    {
        Check("scope: HP2's error not on the HP1 tab, HP1's not on HP2", GameScope.Error(false, null, "e2", null) == null && GameScope.Error(true, "e1", null, null) == null);
        Check("scope: each tab its own game's error", GameScope.Error(false, "e1", "e2", null) == "e1" && GameScope.Error(true, "e1", "e2", null) == "e2");
        Check("scope: the launcher's own error on both tabs", GameScope.Error(false, "e1", "e2", "eL") == "eL" && GameScope.Error(true, null, null, "eL") == "eL");
        var g = Path.Combine(Path.GetTempPath(), "scope", "game");
        Eq("scope: HP1 logs in the install's HPEngineData", Path.Combine(g, "HPEngineData", "logs"), GameScope.LogsDir(g, false));
        Eq("scope: HP2 logs beside the HP2 saves (the engine's Hp2DataDir)", Path.Combine(Path.GetDirectoryName(Hp2FrontEnd.SaveDir(g)), "HPEngineData", "logs"), GameScope.LogsDir(g, true));
        Eq("scope: HP2's console log apart", "/d/HPEngine-HP2.log", GameScope.ConsoleLog("/d/HPEngine.log", true));
        Check("dismiss: clears the shown tab's error only", GameScope.Dismiss(true, "e1", "e2", null) == ("e1", null, null) && GameScope.Dismiss(false, "e1", "e2", null) == (null, "e2", null));
        Check("dismiss: the launcher's own error when that is shown", GameScope.Dismiss(false, "e1", "e2", "eL") == ("e1", "e2", null));
        var og = Path.Combine(Path.GetTempPath(), "hp1-openlog-" + Guid.NewGuid().ToString("N"));
        try
        {
            var got = OpenLogFolders(og).ToDictionary(x => x.Case, x => GameScope.LogToOpen(x.Logs, x.Console, Before));
            Eq("open log: the run's own journal (not an earlier one)", Path.Combine(og, "openlog", "run", "logs", "run_20261117_100000.log"), got["run"]);
            Eq("open log: no journal of this run (codes 4/5/6): the console capture", Path.Combine(og, "openlog", "console", "HPEngine.log"), got["console"]);
            Eq("open log: neither: the logs folder", Path.Combine(og, "openlog", "none", "logs"), got["none"]);
            Eq("open log: the newest journal the run added", "run_20261117_100512.log", GameScope.RunLog(OpenLogNames[4].Now, OpenLogNames[4].Before));
            Check("open log: a journal already there before the run is not this run's", GameScope.RunLog(OpenLogNames[1].Now, OpenLogNames[1].Before) == null);
            fired.Add("banner:open-log-rule");
        }
        finally { Directory.Delete(og, true); }
        fired.Add("scope:rules");
    }

    static void PadCases()
    {
        var t = PadTable().ToList();
        Check("pad map: A activates, also in a popup", t.Contains("button 0 popup=0 hp2=0 Activate") && t.Contains("button 0 popup=1 hp2=1 Activate"));
        Check("pad map: B is Back on the page, Escape in a popup", t.Contains("button 1 popup=0 hp2=1 Back") && t.Contains("button 1 popup=1 hp2=0 Escape"));
        Check("pad map: RB to HP2 only from HP1, LB to HP1 only from HP2", t.Contains("button 10 popup=0 hp2=0 Hp2") && t.Contains("button 10 popup=0 hp2=1 None")
            && t.Contains("button 9 popup=0 hp2=1 Hp1") && t.Contains("button 9 popup=0 hp2=0 None") && t.Contains("button 10 popup=1 hp2=0 None"));
        Check("pad map: Start the Menu, Y Play on HP1 only", t.Contains("button 6 popup=0 hp2=0 Menu") && t.Contains("button 6 popup=1 hp2=0 None")
            && t.Contains("button 3 popup=0 hp2=0 Play") && t.Contains("button 3 popup=0 hp2=1 None"));
        Check("pad map: d-pad moves focus, keys in a popup", t.Contains("button 11 popup=0 hp2=0 Previous") && t.Contains("button 13 popup=0 hp2=0 Previous")
            && t.Contains("button 12 popup=0 hp2=0 Next") && t.Contains("button 14 popup=1 hp2=0 KeyDown") && t.Contains("button 11 popup=1 hp2=0 KeyUp"));
        Check("pad map: X, Back/Guide, stick clicks do nothing", t.Contains("button 2 popup=0 hp2=0 None") && t.Contains("button 4 popup=0 hp2=0 None") && t.Contains("button 7 popup=0 hp2=0 None"));
        Check("pad map: the stick past 16000 only", t.Contains("stick 16000 0 0") && t.Contains($"stick 16001 0 {1u << 14}") && t.Contains($"stick 0 -16001 {1u << 11}") && t.Contains($"stick -32768 16001 {1u << 12 | 1u << 13}"));
        Check("pad map: a held button acts once", t.Contains("pressed 1 1 ") && t.Contains("pressed 11 1 1,3") && t.Contains("pressed 32768 0 "));
        fired.Add("pad:map");
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
            Check("not installed: Choose files is the gold primary, no Install or Play yet", Find<Button>(w, "ChooseButton") is { IsVisible: true } c && c.Classes.Contains("gold")
                && !Find<Button>(w, "InstallButton").IsVisible && !Find<Button>(w, "PlayButton").IsVisible);
            w.Add(Path.Combine(tmp, "Setup.exe"));
            Check("files chosen: Install is the gold primary, Choose files steps back", Find<Button>(w, "InstallButton") is { IsVisible: true, IsEnabled: true } i && i.Classes.Contains("gold")
                && !Find<Button>(w, "ChooseButton").Classes.Contains("gold"));
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
            Check("installed: no edition chip (US 1.53)", !Find<TextBlock>(w, "EditionText").IsEffectivelyVisible);
            string Rows() => string.Join("|", Find<StackPanel>(w, "ModsList").GetLogicalDescendants().OfType<ToggleSwitch>().Select(c => c.Name[4..] + (c.IsChecked == true ? "+" : "-")));
            Eq("mods list reflects Mods.ini", "Beta+|Alpha+|Gamma-", Rows());
            // the bundled Cormorant Garamond where a renderer loads it (the Skia screenshots print it), else the system serif
            Check("title serif: bundled or the system serif", Find<TextBlock>(w, "Title").FontFamily.Name is "Cormorant Garamond" or "Georgia", Find<TextBlock>(w, "Title").FontFamily.ToString());
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
            // Author 2026-11-20 lock: WARNING (modified / localized / unverified) and REPORT lines never reach the player
            foreach (var quiet in new[] { "WARNING modified 3 game file(s) differ from UK 1.1", "WARNING localized 3 localized game file(s) are for the language of this copy", "WARNING unverified Odd edition.", "REPORT https://github.com/artemnikonov/hpengine/issues/new?title=x" })
                w.Handle(quiet, odd);
            Check("WARNING modified/localized/unverified and REPORT are not shown", !Find<TextBlock>(w, "WarningText").IsEffectivelyVisible && odd.Count == 0, Find<TextBlock>(w, "WarningText").Text);
            w.Handle("garbage", odd);
            Check("malformed setup line shown", Find<TextBlock>(w, "WarningText").Text.Contains("garbage") && odd.Count == 1);
            fired.Add("smoke:report");
            // Gamepad (proposal §6): focus starts on the primary, A presses it, RB/LB switch the game, B leaves a sub-page
            Check("pad: focus starts on Play", w.FocusManager?.GetFocusedElement() == Find<Button>(w, "PlayButton"));
            w.Pad(PadMap.A); Dispatcher.UIThread.RunJobs();
            Check("pad: A presses the focused Play (the engine is absent here)", Find<TextBlock>(w, "ErrorText").Text?.StartsWith("HPEngine was not found") == true, Find<TextBlock>(w, "ErrorText").Text);
            w.Pad(PadMap.RB); Dispatcher.UIThread.RunJobs();
            Check("pad: RB selects Harry Potter 2 (no HP2 here: its setup view)", Find<StackPanel>(w, "SetupPanel").IsVisible && Find<TextBlock>(w, "Title").Text == MainWindow.Hp2Title && Find<Button>(w, "TabHp2").Classes.Contains("on"));
            Check("scope: HP1's error is not carried to the HP2 tab", !Find<TextBlock>(w, "ErrorText").IsEffectivelyVisible, Find<TextBlock>(w, "ErrorText").Text);
            w.Pad(PadMap.LB); Dispatcher.UIThread.RunJobs();
            Check("scope: back on HP1 its error is there", Find<TextBlock>(w, "ErrorText").IsEffectivelyVisible && Find<TextBlock>(w, "ErrorText").Text?.StartsWith("HPEngine was not found") == true);
            fired.Add("scope:window");
            Check("pad: LB back to Harry Potter 1", Find<StackPanel>(w, "ReadyPanel").IsVisible && !Find<StackPanel>(w, "Hp2Panel").IsVisible);
            Find<Button>(w, "ModsRow").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
            Check("mods row opens the Mods page", Find<StackPanel>(w, "ModsPage").IsVisible && !Find<StackPanel>(w, "ReadyPanel").IsVisible);
            w.Pad(PadMap.Down); Dispatcher.UIThread.RunJobs();
            w.Pad(PadMap.B); Dispatcher.UIThread.RunJobs();
            Check("pad: B leaves the Mods page", !Find<StackPanel>(w, "ModsPage").IsVisible && Find<StackPanel>(w, "ReadyPanel").IsVisible);
            Eq("mods row summary", "2 of 3 on", Find<TextBlock>(w, "ModsSummary").Text);
            fired.Add("smoke:pad");
            Find<Button>(w, "ModsRow").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
            Check("pad: on the Mods page focus starts on Back", w.FocusManager?.GetFocusedElement() == Find<Button>(w, "ModsBack"));
            // keyboard shortcuts (as Launcher.swift: Cmd there): Escape = Back, Ctrl+2 / Ctrl+1 = the game
            void Press(Avalonia.Input.Key k, Avalonia.Input.KeyModifiers m = Avalonia.Input.KeyModifiers.None) { w.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = k, KeyModifiers = m }); Dispatcher.UIThread.RunJobs(); }
            Press(Avalonia.Input.Key.Escape);
            Check("keys: Escape leaves the Mods page", !Find<StackPanel>(w, "ModsPage").IsVisible && Find<StackPanel>(w, "ReadyPanel").IsVisible);
            Press(Avalonia.Input.Key.OemComma, Avalonia.Input.KeyModifiers.Control);
            Check("keys: Ctrl+, opens the Menu, its button shown pressed while open", Find<Button>(w, "MenuButton").Classes.Contains("held"));
            Find<Button>(w, "MenuButton").Flyout.Hide(); Dispatcher.UIThread.RunJobs();
            Check("menu closed: its button no longer pressed", !Find<Button>(w, "MenuButton").Classes.Contains("held"));
            Press(Avalonia.Input.Key.D2, Avalonia.Input.KeyModifiers.Control);
            Check("keys: Ctrl+2 selects Harry Potter 2", Find<Button>(w, "TabHp2").Classes.Contains("on") && Find<TextBlock>(w, "Title").Text == MainWindow.Hp2Title);
            Press(Avalonia.Input.Key.D1, Avalonia.Input.KeyModifiers.Control);
            Check("keys: Ctrl+1 back to Harry Potter 1", Find<StackPanel>(w, "ReadyPanel").IsVisible && !Find<StackPanel>(w, "Hp2Panel").IsVisible);
            fired.Add("keys:shortcuts");
            w.Close();
        }
        finally { Directory.Delete(tmp, true); }
    }

    // The open popup: a drop-down's (in its template) or the Menu flyout's (not in the window's visual tree; its protected Popup).
    static Avalonia.Controls.Primitives.Popup OpenPopup(MainWindow w) =>
        w.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>().FirstOrDefault(p => p.IsOpen)
        ?? (typeof(Avalonia.Controls.Primitives.PopupFlyoutBase).GetProperty("Popup", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(Find<Button>(w, "MenuButton").Flyout) is Avalonia.Controls.Primitives.Popup { IsOpen: true } m ? m : null);

    static void Put(string root, string rel, string text) { var p = Path.Combine(root, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)); File.WriteAllText(p, text); }

    // Screenshots of every state of the window (Skia, headless; no window opens): dotnet run --project launcher/Desktop.Tests -- --shots <dir>
    static void Shots(string dir)
    {
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(Path.GetTempPath(), "hp1-shots-" + Guid.NewGuid().ToString("N"));
        void Frame() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
        void Save(MainWindow w, string name, bool close = true)
        {
            Frame();
            using var f = w.CaptureRenderedFrame();
            // An open popup (the Menu) lives in its own top level: drawn over the window frame where it sits.
            var popup = OpenPopup(w)?.Host as TopLevel;
            if (popup == null) f.Save(Path.Combine(dir, name));
            else
            {
                using var pf = popup.CaptureRenderedFrame();
                var at = popup.PointToScreen(new Point(0, 0)) - w.PointToScreen(new Point(0, 0));
                using var rt = new Avalonia.Media.Imaging.RenderTargetBitmap(f.PixelSize);
                using (var dc = rt.CreateDrawingContext())
                {
                    dc.DrawImage(f, new Rect(0, 0, f.Size.Width, f.Size.Height));
                    dc.DrawImage(pf, new Rect(at.X, at.Y, pf.Size.Width, pf.Size.Height));
                }
                rt.Save(Path.Combine(dir, name));
            }
            Console.WriteLine(name);
            if (close) w.Close();
        }
        MainWindow Show(MainWindow w) { w.Show(); Frame(); return w; }
        void Click(MainWindow w, string n) { Find<Button>(w, n).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Frame(); }
        try
        {
            Directory.CreateDirectory(tmp);
            Hp2Release.Available = false;   // the HP1 shots as released (HP2 tab "Coming soon"); the HP2 shots as a preview build
            var empty = Path.Combine(tmp, "empty");
            var first = Show(new MainWindow(empty, tmp, "0.4.0", false));
            Console.WriteLine("title font: " + Find<TextBlock>(first, "Title").FontFamily.Name);
            Save(first, "desktop_hp1_empty.png");
            var w = Show(new MainWindow(empty, tmp, "0.4.0", false));
            w.Add(Path.Combine(tmp, "Setup.exe")); w.Add(Path.Combine(tmp, "Setup-1.bin"));
            Save(w, "desktop_hp1_files_chosen.png");
            if (!OperatingSystem.IsWindows())
            {
                // installing: a stand-in HP1Setup that reports the steps and waits (killed when the window closes)
                var setup = Path.Combine(tmp, "HPEngine", "HP1Setup"); Directory.CreateDirectory(Path.GetDirectoryName(setup));
                File.WriteAllText(setup, "#!/bin/sh\necho STEP find; echo STEP unpack; echo STEP check; echo STEP copy; echo PROGRESS 64; exec sleep 30\n");
                File.SetUnixFileMode(setup, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                w = Show(new MainWindow(empty, tmp, "0.4.0", false));
                w.Add(Path.Combine(tmp, "Setup.exe"));
                Click(w, "InstallButton");
                for (int i = 0; i < 250 && Find<ProgressBar>(w, "ProgressBar").Value < 0.6; i++) { Thread.Sleep(20); Dispatcher.UIThread.RunJobs(); }
                Save(w, "desktop_hp1_installing.png");
                File.Delete(setup);
            }
            var game = Path.Combine(tmp, "game"); Directory.CreateDirectory(Path.Combine(game, "HPEngineData"));
            File.WriteAllText(Path.Combine(game, ".hp1-installed"), "data=x\nedition=UK 1.1: supported\n");
            foreach (var m in new[] { "Alpha", "Beta", "Gamma", "Delta", "Epsilon" }) Directory.CreateDirectory(Path.Combine(game, "Mods", m));
            File.WriteAllText(Path.Combine(game, "HPEngineData", "Mods.ini"), "[Mods]\nEnabled=Beta\nEnabled=Alpha\n");
            w = Show(new MainWindow(game, tmp, "0.4.0", false));
            w.OfferUpdate(new Release("0.4.1", "https://example.invalid/release"));
            Save(w, "desktop_hp1_ready_update.png");
            // a mod that adds a map (Start map next to Play), a failed setup (error banner), then the Menu open
            foreach (var f in new[] { "Maps/Lev_Tut1.unr", "Mods/Alpha/Maps/acniabwc.unr" })
            { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(game, f))); File.WriteAllText(Path.Combine(game, f), "x"); }
            w = Show(new MainWindow(game, tmp, "0.4.0", false));
            w.Handle("ERROR crashed The game stopped unexpectedly. Missing package \"HP_Menu\".", new List<string>());
            Save(w, "desktop_hp1_error_start_map.png");
            if (!OperatingSystem.IsWindows())
            {
                // a failed game run (a stand-in engine): the game-error banner with Open log and the ×, then the × focused by the pad
                var engine = Path.Combine(tmp, "HPEngine", "HPEngine"); Directory.CreateDirectory(Path.GetDirectoryName(engine));
                File.WriteAllText(engine, "#!/bin/sh\necho '[Crash] unhandled exception: System.IO.InvalidDataException: package HP_Menu: bad header'; exit 134\n");
                File.SetUnixFileMode(engine, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                foreach (var focus in new[] { false, true })
                {
                    w = Show(new MainWindow(game, tmp, "0.4.0", false));
                    Click(w, "PlayButton");
                    for (int i = 0; i < 500 && !Find<Button>(w, "OpenLogButton").IsEffectivelyVisible; i++) { Thread.Sleep(20); Dispatcher.UIThread.RunJobs(); }
                    if (focus) { Find<Button>(w, "DismissButton").Focus(NavigationMethod.Directional); w.Pad(-1); }
                    Settle();
                    Save(w, focus ? "error_banner_desktop_hp1_focus.png" : "error_banner_desktop_hp1_crash.png");
                }
                File.Delete(engine);
            }
            Put(game, "System/Default.ini", "[Core.System]\nPaths=../System/*.u\nPaths=../Maps/*.unr\n"); Put(game, "Maps/Startup.unr", "x"); Put(game, "Mods/Beta/Textures/Beta.utx", "x");   // map paths and game files in both enabled mods: Alpha's map becomes a start map
            w = Show(new MainWindow(game, tmp, "0.4.0", false));
            Find<ComboBox>(w, "StartMap").IsDropDownOpen = true; Settle();
            HoverItem(w, c => c is ComboBoxItem { Content: string t } && t != StartMap.Game);
            Save(w, "polish_desktop_startmap_open.png");
            // a long list: 30 maps, scrolled to the middle by the pad's d-pad (the focused map kept in view, the scroll bar shown)
            for (int k = 1; k <= 30; k++) Put(game, $"Mods/Alpha/Maps/Map{k:00}.unr", "x");
            w = Show(new MainWindow(game, tmp, "0.4.0", false));
            Find<ComboBox>(w, "StartMap").Focus(); Find<ComboBox>(w, "StartMap").IsDropDownOpen = true; Settle();
            for (int k = 0; k < 15; k++) { w.Pad(PadMap.Down); Frame(); }
            Settle();
            Save(w, "polish_desktop_startmap_scrolled.png");
            for (int k = 1; k <= 30; k++) File.Delete(Path.Combine(game, "Mods", "Alpha", "Maps", $"Map{k:00}.unr"));
            w = Show(new MainWindow(game, tmp, "0.4.0", false));
            Find<Button>(w, "MenuButton").Flyout.ShowAt(Find<Button>(w, "MenuButton")); Frame();
            Save(w, "desktop_hp1_menu.png");
            Directory.CreateDirectory(Path.Combine(game, "Save"));
            File.WriteAllBytes(Path.Combine(game, "Save", "save2.hp1sav"), SaveHead(5, "Alpha"));
            w = Show(new MainWindow(game, tmp, "0.4.0", false));
            Click(w, "ModsRow");
            Save(w, "desktop_hp1_mods.png");
            Hp2Release.Available = true;
            // HP2: no folder; then a folder with two used slots (synthetic thumbnails: plain gradients, no game image)
            w = Show(new MainWindow(game, tmp, "0.4.0", false));
            Click(w, "TabHp2");
            Save(w, "desktop_hp2_no_folder.png");
            var h2 = Path.Combine(tmp, "hp2"); Directory.CreateDirectory(Path.Combine(h2, "System"));
            File.WriteAllText(Path.Combine(h2, "System", "Engine.dll"), "x");
            File.WriteAllText(Path.Combine(h2, "System", "Startup.int"), "[IDDIALOG_FrontEndPageMainMenu]\nIDC_Title=Main Menu\nIDC_NewGame=New Game\nIDC_LoadGame=Load Game\nIDC_Options=Options\n"
                + "[IDDIALOG_FrontEndPageLoadGame]\nIDC_Title=Load Game\nIDC_Instructions=Choose a saved game to continue.\nIDC_SlotEmpty=Empty\nIDC_SlotUsed=Saved game\n");
            File.WriteAllText(Path.Combine(tmp, "hp2-root.txt"), h2);
            var sd = Hp2FrontEnd.SaveDir(game);
            foreach (var n in new[] { 1, 2 })
            {
                Directory.CreateDirectory(Path.Combine(sd, "Slot" + n)); File.WriteAllText(Path.Combine(sd, "Slot" + n, "save0.hp1sav"), "0123456789");
                using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(128, 96));
                using (var dc = bmp.CreateDrawingContext())
                    dc.FillRectangle(new Avalonia.Media.LinearGradientBrush { GradientStops = { new(Avalonia.Media.Color.Parse("#8A6F55"), 0), new(Avalonia.Media.Color.Parse("#2C3B26"), 0.6), new(Avalonia.Media.Color.Parse("#14050A"), 1) } }, new Rect(0, 0, 128, 96));
                bmp.Save(Path.Combine(sd, "Slot" + n, "Save0.bmp"));
            }
            w = Show(new MainWindow(game, tmp, "0.4.0", false));
            Click(w, "TabHp2");
            Save(w, "desktop_hp2_main.png", false);
            Click(w, "Hp2LoadGame");
            w.Pad(PadMap.Right); Frame();
            Save(w, "desktop_hp2_load_pad.png");
            // Interaction states (headless pointer on the real controls): hover, press, keyboard focus, and each list open at
            // its control with an item under the pointer. Each shot waits out the 150-200 ms transitions and entrances.
            Hp2Release.Available = false;
            void Settle() { for (int i = 0; i < 20; i++) { Thread.Sleep(15); Frame(); } }
            Point At(Control c, TopLevel top) => c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), top) ?? default;
            TopLevel PopupTop(MainWindow win) => OpenPopup(win)?.Host as TopLevel ?? win;   // the Menu flyout: an overlay inside the window
            void HoverItem(MainWindow win, Func<Control, bool> pick)
            {
                var top = PopupTop(win); var item = top.GetVisualDescendants().OfType<Control>().First(pick);
                top.MouseMove(At(item, top)); Settle();
            }
            Put(game, "System/0/Default.ini", "[Engine.Engine]\nLanguage=eng\n"); Put(game, "System/7/Default.ini", "[Engine.Engine]\nLanguage=fin\n");
            Put(game, "System/8/Default.ini", "[Engine.Engine]\nLanguage=swe\n");
            w = Show(new MainWindow(game, tmp, "0.4.0", false));
            w.MouseMove(At(Find<Button>(w, "PlayButton"), w)); Settle();
            Save(w, "polish_desktop_hover.png", false);
            w.MouseDown(At(Find<Button>(w, "PlayButton"), w), MouseButton.Left); Settle();
            Save(w, "polish_desktop_pressed.png", false);
            w.MouseUp(new Point(5, 550), MouseButton.Left); w.MouseMove(new Point(5, 550));
            Find<Button>(w, "ModsRow").Focus(NavigationMethod.Tab); Settle();
            Save(w, "polish_desktop_focus.png", false);
            Find<Button>(w, "MenuButton").Flyout.ShowAt(Find<Button>(w, "MenuButton")); Settle();
            HoverItem(w, c => c is MenuItem { Name: "OpenLogs" });
            Save(w, "polish_desktop_menu_open.png", false);
            Find<Button>(w, "MenuButton").Flyout.Hide(); Settle();
            Find<ComboBox>(w, "GameLanguage").IsDropDownOpen = true; Settle();
            HoverItem(w, c => c is ComboBoxItem { Content: string t } && t != Find<ComboBox>(w, "GameLanguage").SelectedItem as string);
            Save(w, "polish_desktop_language_open.png", false);
            w.Close();
        }
        finally { Directory.Delete(tmp, true); }
    }
}
