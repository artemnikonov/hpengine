// The port's answers to tools/hp2/frontend_judge.py's cases (the HP2 Game.exe front-end bodies under unicorn):
//   dotnet run --project launcher/Desktop.Tests -- --hp2-frontend <cases> <answers>
// A synthetic file system stands in for GFileManager, as in the judge.
using Avalonia.LogicalTree;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HPEngine.Launcher;

static partial class T
{
    sealed class FakeFiles : Hp2FrontEnd.IFiles
    {
        public Func<string, long> Size = _ => -1;
        public List<string> Names = new();
        public readonly List<string> Finds = new(), Deletes = new(), Dirs = new();
        public long FileSize(string p) => Size(p);
        public void Delete(string p) => Deletes.Add(p);
        public void MakeDirectory(string p) => Dirs.Add(p);
        public List<string> FindFiles(string pattern) { Finds.Add(pattern); return Names; }
        public void DeleteCache(string dir) { }   // the port's cache folder: no counterpart in the original's calls
    }

    static int Hp2FrontEndCases(string cases, string answers)
    {
        var o = new List<string>();
        foreach (var line in File.ReadLines(cases))
        {
            var f = line.Split('\t');
            string got;
            if (f[0] == "launch")
                got = "open|" + Hp2FrontEnd.CommandLine(f[1] == "new" ? Hp2FrontEnd.Launch.NewGame : Hp2FrontEnd.Launch.LoadGame, int.Parse(f[2]), f.Length > 3 ? f[3] : "");
            else if (f[0] == "click")
            {
                int n = int.Parse(f[2]); bool used = f[3] == "1", yes = f[4] == "1", asked = false;
                var fs = new FakeFiles();
                var slot = new Hp2FrontEnd.Slot(n, $"S{n}", used, null);
                bool go = f[1] == "new" ? Hp2FrontEnd.NewGame(slot, () => { asked = true; return yes; }, fs) : Hp2FrontEnd.LoadGame(slot);
                string code = f[1] == "new" ? "1" : "2";
                got = $"ask={(asked ? 1 : 0)}|clear={(fs.Finds.Count > 0 ? n.ToString() : "")}|ini={(go ? $"LaunchCode={code},UseSaveSlot={n}" : "")}|end={(go ? "0" : "")}";
            }
            else if (f[0] == "soundinit")
            {
                var v = new Dictionary<string, string> { ["UseSound"] = f[1], ["SoundVolume"] = f[2], ["MusicVolume"] = f[3] };
                var s = Hp2Options.ReadSound((_, k) => v[k] == "-" ? "" : v[k]);
                got = $"check={(s.NoSound ? 1 : 0)}|sound={s.SoundPos}|music={s.MusicPos}";
            }
            else if (f[0] == "soundok")
                got = "set=" + string.Join(",", Hp2Options.WriteSound(int.Parse(f[1]), int.Parse(f[2]), int.Parse(f[3])).Select(w => $"{w.Section}.{w.Key}={w.Value}")) + "|flush=1";
            else if (f[0] == "videoinit")   // not original: HPEngine's own window keys (no judge against Game.exe)
            {
                var v = new Dictionary<string, string> { ["StartupFullscreen"] = f[1], ["WindowedViewportX"] = f[2], ["WindowedViewportY"] = f[3] };
                var x = Hp2Options.ReadVideo((_, k) => v[k] == "-" ? null : v[k]);
                got = $"full={(x.Fullscreen ? 1 : 0)}|w={x.Width}|h={x.Height}";
            }
            else if (f[0] == "videook")
                got = "set=" + string.Join(",", Hp2Options.WriteVideo(f[1] == "1", int.Parse(f[2]), int.Parse(f[3])).Select(w => $"{w.Section}.{w.Key}={w.Value}"));
            else if (f[0] == "clear")
            {
                var fs = new FakeFiles();
                var files = f[3] == "-" ? new List<(string N, long S)>() : f[3].Split(',').Select(x => (N: x.Split(':')[0], S: long.Parse(x.Split(':')[1]))).ToList();
                fs.Names = files.Select(x => x.N).ToList();
                fs.Size = p => files.Where(x => Hp2FrontEnd.Join(f[2], x.N) == p).Select(x => x.S).DefaultIfEmpty(-1).First();
                Hp2FrontEnd.Clear(f[2], fs);
                got = $"find={string.Join(",", fs.Finds)}|delete={string.Join(",", fs.Deletes)}|used=0";
            }
            else // scan
            {
                var sz = f[2].Split(',');
                var fs = new FakeFiles();
                fs.Size = p => Regex.Match(p, @"Slot(\d)\\Save0\.usa$") is { Success: true } m && sz[int.Parse(m.Groups[1].Value) - 1] != "-" ? long.Parse(sz[int.Parse(m.Groups[1].Value) - 1]) : -1;
                var slots = Hp2FrontEnd.Scan(f[1], fs);
                var cleared = fs.Finds.Select(p => Regex.Match(p, @"Slot(\d)\\\*\.\*$").Groups[1].Value);
                got = $"slots={string.Join(";", slots.Select(s => $"{s.N}:{s.Dir}:{(s.Used ? 1 : 0)}"))}|mkdir={string.Join(",", fs.Dirs)}" +
                      $"|clear={string.Join(",", cleared)}|thumb={string.Join(",", slots.Where(s => s.Used).Select(s => s.Thumb))}";
            }
            o.Add(line + "\t=\t" + got);
        }
        File.WriteAllLines(answers, o);
        return 0;
    }

    // The HP2 tab's menu on real folders, as launcher/Launcher.swift --hp2-menu prints it (launcher/test_update.sh compares):
    // <HP1 data dir> <HP2 root> [new|load <slot> <yes 0|1>].
    static int Hp2Menu(string[] a)
    {
        string sd = Hp2FrontEnd.SaveDir(a[1]), root = a[2];
        var fs = new Hp2FrontEnd.Files(sd);
        string L(string s, string k) => Hp2FrontEnd.Localize(root, "Startup", s, k);
        var slots = Hp2FrontEnd.Scan("", fs, Hp2FrontEnd.PortSave);
        var o = new List<string> { "savedir=" + sd,
            "main=" + string.Join("|", new[] { "IDC_Title", "IDC_Instructions", "IDC_NewGame", "IDC_LoadGame", "IDC_Options" }.Select(k => L("IDDIALOG_FrontEndPageMainMenu", k))) };
        foreach (var p in new[] { "NewGame", "LoadGame" })
        {
            string s = "IDDIALOG_FrontEndPage" + p;
            o.Add(p + "=" + L(s, "IDC_Title") + "|" + string.Join("|", slots.Select(x => Hp2FrontEnd.Label(x, L(s, "IDC_SlotEmpty"), L(s, "IDC_SlotUsed")))));
        }
        o.Add("slots=" + string.Join(";", slots.Select(s => $"{s.N}:{(s.Used ? 1 : 0)}:{s.Thumb ?? "-"}")));
        o.Add("ask=" + Hp2FrontEnd.Localize(root, "HPMenu", "All", "Select_Game_0002") + "|" + Hp2FrontEnd.Localize(root, "HPMenu", "All", "Select_Game_0001"));
        if (a.Length == 6)
        {
            var what = a[3] == "new" ? Hp2FrontEnd.Launch.NewGame : Hp2FrontEnd.Launch.LoadGame;
            int n = int.Parse(a[4]); var s = slots[n - 1];
            bool go = what == Hp2FrontEnd.Launch.NewGame ? Hp2FrontEnd.NewGame(s, () => a[5] == "1", fs) : Hp2FrontEnd.LoadGame(s);
            o.Add($"go={(go ? 1 : 0)}" + (go ? " " + string.Join("|", Hp2FrontEnd.PlayArgs(root, sd, Hp2FrontEnd.CommandLine(what, n, ""))) : ""));
        }
        var files = Directory.Exists(sd) ? Directory.GetFiles(sd, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(sd, f)).ToList() : new();
        files.Sort(string.CompareOrdinal);
        o.Add("files=" + string.Join(",", files));
        foreach (var l in o) Console.WriteLine(l);
        return 0;
    }

    // The HP2 tab of the window on a synthetic HP2 folder: the slot scan (an unused slot's stray file deleted, a used
    // slot kept), the slot labels from Startup.int, Load ignoring an unused slot, New Game on an unused slot starting
    // HPEngine with the original's parameters; the HP1 tab as before (its ready panel back).
    static void Hp2Tab()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "hp2-tab-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var g = Path.Combine(tmp, "game"); var h2 = Path.Combine(tmp, "hp2");
            Directory.CreateDirectory(Path.Combine(g, "System")); File.WriteAllText(Path.Combine(g, ".hp1-installed"), "edition=US 1.53: exact\n");
            Directory.CreateDirectory(Path.Combine(h2, "System"));
            File.WriteAllText(Path.Combine(h2, "System", "Engine.dll"), "x");
            File.WriteAllText(Path.Combine(h2, "System", "default.ini"), "[Engine.Engine]\nLanguage=usa\n[Core.System]\nSavePath=../Save\n"
                + "[Engine.GameEngine]\nUseSound=True\n[ALAudio.ALAudioSubsystem]\nSoundVolume=0.29\nMusicVolume=1.5\n"
                + "[WinDrv.WindowsClient]\nWindowedViewportX=640\nWindowedViewportY=480\nStartupFullscreen=True\n");
            File.WriteAllText(Path.Combine(h2, "System", "Startup.int"), "[IDDIALOG_FrontEndPageMainMenu]\nIDC_Title=Main Menu\nIDC_NewGame=New Game\nIDC_LoadGame=Load Game\nIDC_Options=Options\n"
                + "[IDDIALOG_FrontEndPageNewGame]\nIDC_Title=New Game\nIDC_SlotEmpty=Empty\nIDC_SlotUsed=Used\n[IDDIALOG_FrontEndPageLoadGame]\nIDC_Title=Loading Game\nIDC_SlotEmpty=Empty\nIDC_SlotUsed=Used\n");
            // the player's real HP2 saves in the install (an unused slot with a stray file, a used slot): never touched (D-01)
            Directory.CreateDirectory(Path.Combine(h2, "Save", "Slot1")); File.WriteAllText(Path.Combine(h2, "Save", "Slot1", "stray.txt"), "x");
            Directory.CreateDirectory(Path.Combine(h2, "Save", "Slot3")); File.WriteAllText(Path.Combine(h2, "Save", "Slot3", "Save0.usa"), "real");
            // the port's own HP2 saves (Hp2FrontEnd.SaveDir): the same shapes
            var sd = Hp2FrontEnd.SaveDir(g);
            Eq("hp2 save dir: beside the HP1 data directory", Path.Combine(tmp, "HP2Data", "Save"), sd);
            Directory.CreateDirectory(Path.Combine(sd, "Slot1")); File.WriteAllText(Path.Combine(sd, "Slot1", "stray.txt"), "x");
            Directory.CreateDirectory(Path.Combine(sd, "Slot1", "cache")); File.WriteAllText(Path.Combine(sd, "Slot1", "cache", "c.bin"), "c");
            Directory.CreateDirectory(Path.Combine(sd, "Slot2")); File.WriteAllText(Path.Combine(sd, "Slot2", "save0.hp1sav"), "0123456789");
            File.WriteAllText(Path.Combine(tmp, "hp2-root.txt"), h2);
            string Tree(string d) => string.Join("|", Directory.EnumerateFileSystemEntries(d, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal)
                .Select(x => x[d.Length..] + (File.Exists(x) ? $":{new FileInfo(x).Length}:{File.GetLastWriteTimeUtc(x).Ticks}" : "/")));
            string before = Tree(h2);
            var guard = new Hp2FrontEnd.Files(sd);
            Check("hp2 save dir: a path out of it is refused", guard.FileSize("..\\..\\hp2\\Save\\Slot3\\Save0.usa") == -1 && guard.FindFiles("..\\*.*").Count == 0);
            var w = new MainWindow(g, tmp, "0.1.0-beta", false); w.Show(); Dispatcher.UIThread.RunJobs();
            void Click(string n) { Find<Button>(w, n)?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); }
            string Label(int n) => ((Find<Button>(w, $"Hp2Slot{n}")?.Content as Border)?.Child as Grid)?.Children.OfType<TextBlock>().Last().Text ?? "-";
            Click("TabHp2");
            Check("hp2 tab: HP1 panels hidden, menu shown", !Find<StackPanel>(w, "ReadyPanel").IsVisible && Find<StackPanel>(w, "Hp2Panel").IsVisible && Find<Button>(w, "Hp2NewGame") != null);
            Check("hp2 tab: the scan made Slot1..6 and cleared the unused Slot1 (port save dir)", Directory.Exists(Path.Combine(sd, "Slot6")) && !File.Exists(Path.Combine(sd, "Slot1", "stray.txt"))
                && !Directory.Exists(Path.Combine(sd, "Slot1", "cache")) && File.Exists(Path.Combine(sd, "Slot2", "save0.hp1sav")));
            Check("hp2 tab: a used slot makes Load Game the gold primary, New Game secondary", Find<Button>(w, "Hp2LoadGame").Classes.Contains("gold") && !Find<Button>(w, "Hp2NewGame").Classes.Contains("gold"));
            Click("Hp2LoadGame");
            Eq("hp2 tab: Load labels", "1 - Empty|2 - Used|6 - Empty", Label(1) + "|" + Label(2) + "|" + Label(6));
            Check("hp2 tab: no IDC_Instructions line, no instructions (not the key's name; as Launcher.swift)",
                !Find<StackPanel>(w, "Hp2Panel").GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == "IDC_Instructions"));
            Click("Hp2Slot1");
            Check("hp2 tab: Load on an unused slot does nothing", Find<TextBlock>(w, "ErrorText").Text == null && Find<Button>(w, "Hp2Slot1") != null);
            Click("Hp2Back"); Click("Hp2NewGame");
            fired.Add("hp2:tab");
            if (!OperatingSystem.IsWindows())
            {
                var exe = Path.Combine(tmp, "HPEngine", "HPEngine"); Directory.CreateDirectory(Path.GetDirectoryName(exe));
                File.WriteAllText(exe, "#!/bin/sh\nprintf '%s\\n' \"$@\" > \"$0.args.tmp\" && mv \"$0.args.tmp\" \"$0.args\"\n");
                File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Click("Hp2Slot3");
                for (int i = 0; i < 500 && !File.Exists(exe + ".args"); i++) { Thread.Sleep(20); Dispatcher.UIThread.RunJobs(); }
                Eq("hp2 tab: New Game on slot 3 starts HPEngine with the original's parameters",
                    $"--root|{h2}|--launched-by-launcher|--hp2-savedir|{sd}|--hp2-cmdline|PrivetDr.unr  -SAVESLOT=3", File.Exists(exe + ".args") ? string.Join("|", File.ReadAllLines(exe + ".args")) : Find<TextBlock>(w, "ErrorText").Text);
                for (int i = 0; i < 200 && Find<Button>(w, "Hp2NewGame") == null; i++) { Thread.Sleep(20); Dispatcher.UIThread.RunJobs(); }
                fired.Add("hp2:play-args");
            }
            Click("Hp2Back");   // Windows: still on the New Game page (no stand-in engine run)
            Check("hp2 tab: New Game and Load Game only (Options are the game's own menu, author 2026-11-16)", Find<Button>(w, "Hp2Options") == null
                && Find<StackPanel>(w, "Hp2Panel").GetLogicalDescendants().OfType<Button>().Select(b => b.Name).Where(n => n != null).SequenceEqual(new[] { "Hp2NewGame", "Hp2LoadGame" }));
            Eq("hp2 tab: nothing in the install touched (its Save folder: the player's real saves)", before, Tree(h2));
            Click("TabHp1");
            Check("hp1 tab: the ready panel and Play back, the HP2 menu hidden", Find<StackPanel>(w, "ReadyPanel").IsVisible && Find<Button>(w, "PlayButton").IsVisible && !Find<StackPanel>(w, "Hp2Panel").IsVisible);
            fired.Add("hp2:hp1-unchanged");
            w.Close();
        }
        finally { Directory.Delete(tmp, true); }
    }

    // The HP2 tab without an HP2: HP1's setup view installing HP2 (a stand-in HP1Setup: --game hp2 and the HP2 install root
    // <HP2Data>/game), the tabs held while it runs, then the HP2 menu with the edition chip; files chosen again: the setup
    // view with Install Again; a WARNING shown on the HP2 tab; switching to HP1 starts its own setup state afresh.
    static void Hp2Install()
    {
        if (OperatingSystem.IsWindows()) return;   // the stand-in HP1Setup is a shell script
        var tmp = Path.Combine(Path.GetTempPath(), "hp2-install-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var g = Path.Combine(tmp, "game"); Directory.CreateDirectory(g); File.WriteAllText(Path.Combine(g, ".hp1-installed"), "edition=US 1.53: exact\n");
            var setup = Path.Combine(tmp, "HPEngine", "HP1Setup"); Directory.CreateDirectory(Path.GetDirectoryName(setup));
            File.WriteAllText(setup, "#!/bin/sh\nprintf '%s\\n' \"$@\" > \"$0.args\"\nwhile [ \"$1\" != --dest ]; do shift; done; d=$2\n"
                + "echo STEP find; echo STEP check; echo 'EDITION HP2_RU_EA RU EA: supported'; echo STEP copy; echo PROGRESS 50\nread x < \"$0.go\" 2> /dev/null || sleep 1\n"
                + "mkdir -p \"$d/System\" \"$d/Maps\"; echo x > \"$d/System/Engine.dll\"\n"
                + "printf '[IDDIALOG_FrontEndPageMainMenu]\\nIDC_NewGame=New Game\\nIDC_LoadGame=Load Game\\n' > \"$d/System/Startup.int\"\n"
                + "printf 'data=HP2_RU_EA\\nedition=RU EA: supported\\n' > \"$d/.hp1-installed\"; echo \"DONE $d\"\n");
            File.SetUnixFileMode(setup, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var w = new MainWindow(g, tmp, "0.4.0", false); w.Show(); Dispatcher.UIThread.RunJobs();
            void Click(string n) { Find<Button>(w, n)?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); }
            Click("TabHp2");
            Check("hp2 install: no HP2 -> the setup view (drop zone, Choose files gold), not the menu", Find<StackPanel>(w, "SetupPanel").IsVisible && !Find<StackPanel>(w, "Hp2Panel").IsVisible
                && Find<Button>(w, "ChooseButton") is { IsVisible: true } c && c.Classes.Contains("gold") && Find<TextBlock>(w, "Status").Text == "Not installed"
                && Find<TextBlock>(w, "Title").Text == MainWindow.Hp2Title);
            w.Add(Path.Combine(tmp, "HP2 CD"));
            Check("hp2 install: files chosen -> Install", Find<Button>(w, "InstallButton") is { IsVisible: true, IsEnabled: true });
            Click("InstallButton");
            for (int i = 0; i < 250 && Find<ProgressBar>(w, "ProgressBar").Value < 0.5; i++) { Thread.Sleep(20); Dispatcher.UIThread.RunJobs(); }
            Click("TabHp1");
            Check("hp2 install: running, the tabs stay (HP2 installing)", Find<StackPanel>(w, "SetupPanel").IsVisible && Find<TextBlock>(w, "Title").Text == MainWindow.Hp2Title
                && Find<TextBlock>(w, "Status").Text == "Installing…");
            for (int i = 0; i < 300 && Find<Button>(w, "Hp2NewGame") == null; i++) { Thread.Sleep(20); Dispatcher.UIThread.RunJobs(); }
            Eq("hp2 install: HP1Setup --game hp2 into <HP2Data>/game", $"--game|hp2|--dest|{Path.Combine(tmp, "HP2Data", "game")}|{Path.Combine(tmp, "HP2 CD")}",
                File.Exists(setup + ".args") ? string.Join("|", File.ReadAllLines(setup + ".args")) : "-");
            Check("hp2 install: done -> the HP2 menu, no edition chip for a tested edition (RU EA supported)", Find<Button>(w, "Hp2NewGame") != null && !Find<StackPanel>(w, "SetupPanel").IsVisible
                && !Find<TextBlock>(w, "EditionText").IsEffectivelyVisible && !Find<TextBlock>(w, "EditionChip").IsEffectivelyVisible);
            w.Add(Path.Combine(tmp, "other"));
            Check("hp2 install: files chosen again -> the setup view, Install Again", Find<StackPanel>(w, "SetupPanel").IsVisible && Find<Button>(w, "InstallButton").IsVisible
                && ((StackPanel)Find<Button>(w, "InstallButton").Content).Children.OfType<TextBlock>().Any(t => t.Text == "Install Again"));
            w.Handle("WARNING modified This copy of the game is modified.", new List<string>());
            Check("hp2 install: a WARNING shows on the HP2 tab", Find<TextBlock>(w, "WarningText") is { IsEffectivelyVisible: true, Text: "This copy of the game is modified." });
            Click("TabHp1");
            Check("hp2 install: HP1's tab afresh (its ready panel, no HP2 warning or files)", Find<StackPanel>(w, "ReadyPanel").IsVisible && !Find<TextBlock>(w, "WarningText").IsEffectivelyVisible
                && Find<TextBlock>(w, "Title").Text != MainWindow.Hp2Title);
            Click("TabHp2");
            Check("hp2 install: back on HP2: its menu (the chosen files dropped on the switch)", Find<Button>(w, "Hp2NewGame") != null);
            fired.Add("hp2:install");
            w.Close();
        }
        finally { Directory.Delete(tmp, true); }
    }
}
