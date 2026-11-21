// The HP2 tab of the launcher: HP2's own front end (the dialog its Game.exe shows before the engine: Main Menu, New Game,
// Load Game, Options), its page flow from Logic.cs Hp2FrontEnd (judged against Game.exe), drawn in the launcher's look — not
// the original's Win32 dialog bitmaps ("not original", DECISIONS "HP2 main menu in the launcher"). One window, the game a
// branch on the selected tab; the tab paints the whole window in HP2's palette (Th.Use).
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;
using Rectangle = Avalonia.Controls.Shapes.Rectangle;

namespace HPEngine.Launcher;

public sealed partial class MainWindow
{
    public const string Hp2Title = "Harry Potter and the Chamber of Secrets";
    bool _hp2Tab;
    string _hp2Page = "Main";                        // Main | NewGame | LoadGame (the dialog's pages; its Options are the game's own menu, author 2026-11-16)
    List<Hp2FrontEnd.Slot> _hp2Slots = new();
    readonly StackPanel _hp2Panel = new() { Name = "Hp2Panel", Spacing = 12, IsVisible = false };
    readonly Button _tabHp1 = new() { Name = "TabHp1" }, _tabHp2 = new() { Name = "TabHp2" };

    // HP2's install root: HP1Setup --game hp2 installs here (<HP2Data>/game, beside HP2's saves and settings); installed
    // once its marker is written (the copy's last step), as HP1's.
    string Hp2Game => Path.Combine(Path.GetDirectoryName(Hp2FrontEnd.SaveDir(_game)), "game");
    bool Hp2Installed => File.Exists(Path.Combine(Hp2Game, ".hp1-installed"));
    // Without an installed copy: an HP2 game folder the player chose (an installed HP2: System/Engine.dll and
    // System/Startup.int; Menu "Change game folder…"), kept beside the HP1 data directory; HP1's data is not touched.
    string Hp2RootFile => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_game)) ?? _game, "hp2-root.txt");
    string Hp2Root
    {
        get { if (!Hp2Release.Available) return null;   // the release switch off: the saved folder is ignored
              if (Hp2Installed) return Hp2Game;
              try { var r = File.Exists(Hp2RootFile) ? File.ReadAllText(Hp2RootFile).Trim() : null; return Hp2Valid(r) ? r : null; } catch (IOException) { return null; } }
    }
    static bool Hp2Valid(string r) => r != null && File.Exists(Path.Combine(r, "System", "Engine.dll")) && File.Exists(Path.Combine(r, "System", "Startup.int"));

    // The game switcher's two segments: status dot, name, caption (MainWindow.Tab fills them).
    Button[] BuildTabs()
    {
        foreach (var (b, text) in new[] { (_tabHp1, "Harry Potter 1"), (_tabHp2, "Harry Potter 2") })
        {
            b.Classes.Add("tab");
            b.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = {
                new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { FontSize = 12, FontWeight = FontWeight.Medium, Foreground = Th.Dim, VerticalAlignment = VerticalAlignment.Center } } };
        }
        _tabHp1.Click += (_, _) => { if (_running) return; SwitchTo(false); _modsOpen = false; Refresh(); };
        _tabHp2.Click += (_, _) => { if (!Hp2Release.Available || _running) return; SwitchTo(true); _modsOpen = false; _hp2Page = "Main"; Hp2Scan(); Refresh(); };
        return new[] { _tabHp1, _tabHp2 };
    }

    // The setup state (chosen files, steps, warning, edition) is the selected tab's game: a switch starts the other's afresh.
    void SwitchTo(bool hp2)
    {
        if (_hp2Tab == hp2) return;
        _hp2Tab = hp2; _inputs.Clear(); _step = -1; _warning = null; _edition = Edition();
    }

    // The dialog's OnInitDialog: the slot scan (MakeDirectory, used/unused, an unused slot cleared) — each time the menu
    // opens, as each start of Game.exe ran it.
    void Hp2Scan()
    {
        var root = Hp2Root;
        _hp2Slots = root == null ? new() : Hp2FrontEnd.Scan("", new Hp2FrontEnd.Files(Hp2FrontEnd.SaveDir(_game)), Hp2FrontEnd.PortSave);
    }

    // "Choose game folder…" (the empty tab) and Menu → "Change game folder…".
    async Task Hp2ChooseFolder()
    {
        foreach (var f in await StorageProvider.OpenFolderPickerAsync(new() { Title = "Installed Harry Potter and the Chamber of Secrets" }))
            if (f.TryGetLocalPath() is { } p)
            {
                if (!Hp2Valid(p)) { _hp2Error = $"Not an installed HP2 game folder (no System/Engine.dll and System/Startup.int): {p}"; break; }
                try { File.WriteAllText(Hp2RootFile, p); _hp2Error = null; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _hp2Error = e.Message; }
            }
        _hp2Page = "Main"; Hp2Scan(); Refresh();
    }

    Button Hp2Button(string name, object content, string cls, bool enabled = true)
    {
        var b = new Button { Name = name, Content = content, IsEnabled = enabled };
        b.Classes.Add(cls);
        return b;
    }

    void RefreshHp2()
    {
        _setupPanel.IsVisible = _readyPanel.IsVisible = _modsPage.IsVisible = _actions.IsVisible = _resumeNote.IsVisible = false; _hp2Panel.IsVisible = true;
        var root = Hp2Root;
        _title.Text = Hp2Title; _editionDetail.IsVisible = false;
        if (root == null || _running || _inputs.Count > 0)
        {
            // Not installed, or files chosen to install (again): HP1's setup view, installing HP2 (HP1Setup --game hp2).
            _hp2Panel.IsVisible = _metaExtra.IsVisible = _startMapBox.IsVisible = false; _hero.IsVisible = _setupPanel.IsVisible = _actions.IsVisible = true;
            var (s, c, i) = _running ? ("Installing…", Th.Accent, Spark) : Hp2Installed ? ("Ready to play", Th.Ok, Check) : ("Not installed", Th.Dim, Ring);
            Status(s, c, i);
            _heroSub.Text = "Set up once from your own copy of the game. Nothing of the game is included."; _heroSub.IsVisible = !Hp2Installed && !_running;
            RefreshSetup(Hp2Installed, true, false);
            return;
        }
        LoadMods();
        if (_modsOpen)   // the Mods page (MainWindow.BuildModsPage), HP2's folders (ModsGame, ModsData)
        {
            _hp2Panel.IsVisible = _hero.IsVisible = false; _modsPage.IsVisible = true; _primary = _modsBack;
            UpdateBanners();
            return;
        }
        var (st, col, icon) = _playing ? ("Playing", Th.Ok, Check) : ("Ready to play", Th.Ok, Check);
        Status(st, col, icon);
        RefreshSetup(Hp2Installed, false, false);   // the edition chip (an installed copy)
        int used = _hp2Slots.Count(s => s.Used);
        _metaExtra.Text = used == 1 ? "1 saved game" : $"{used} saved games"; _metaExtra.IsVisible = true;
        _heroSub.IsVisible = false;
        _hero.IsVisible = _hp2Page == "Main";
        UpdateBanners();
        _hp2Panel.Children.Clear();
        _hp2Panel.Margin = new Thickness(24, _hero.IsVisible ? 20 : 16, 24, 0);
        string L(string page, string key) => Hp2FrontEnd.Localize(root, "Startup", page, key);
        string section = "IDDIALOG_FrontEndPage" + (_hp2Page == "Main" ? "MainMenu" : _hp2Page);
        // A sub-page: Back + the page's title (Startup.int) in the serif, its instructions under it.
        Button SubPage(string parent)
        {
            var back = new Button { Name = "Hp2Back" }; Back(back);
            back.Click += (_, _) => { _hp2Page = parent; Refresh(); };
            _hp2Panel.Children.Add(SubHeader(back, L(section, "IDC_Title")));
            // no such line (Localize returns the key): none, as Launcher.swift
            if (L(section, "IDC_Instructions") is { Length: > 0 } ins && ins != "IDC_Instructions") _hp2Panel.Children.Add(new TextBlock { Text = ins, FontSize = 13, Foreground = Th.Dim, TextWrapping = TextWrapping.Wrap });
            return back;
        }
        if (_hp2Page == "Main")
        {
            // New Game and Load Game in the original's order (its Options: the game's own menu, author 2026-11-16); the accent:
            // Load Game when a slot is used, else New Game.
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            string primary = used > 0 ? "Hp2LoadGame" : "Hp2NewGame";
            foreach (var (name, key, to) in new[] { ("Hp2NewGame", "IDC_NewGame", "NewGame"), ("Hp2LoadGame", "IDC_LoadGame", "LoadGame") })
            {
                var b = Hp2Button(name, L(section, key), name == primary ? "gold" : "quiet", !_playing);
                if (name != primary) b.MinHeight = 52;
                b.Click += (_, _) => { _hp2Page = to; Refresh(); };
                row.Children.Add(b);
                if (name == primary) _primary = b;
            }
            _hp2Panel.Children.Add(row);
            // Mods: a summary row that opens the Mods page, as HP1's (BuildReadyPanel)
            var mods = new Button { Name = "Hp2ModsRow", Margin = new Thickness(0, 8, 0, 0) }; mods.Classes.Add("row");
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = 8 };
            line.Children.Add(new TextBlock { Text = "Mods", VerticalAlignment = VerticalAlignment.Center });
            var alert = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center, Background = Th.Warn, IsVisible = _modsAlert.IsVisible };
            var value = new TextBlock { Name = "Hp2ModsSummary", Text = _modsValue.Text, FontSize = 13, Foreground = Th.Dim, VerticalAlignment = VerticalAlignment.Center };
            var chev = Glyph24(Chev, Th.Dim, 16);
            Grid.SetColumn(alert, 1); Grid.SetColumn(value, 2); Grid.SetColumn(chev, 3);
            line.Children.Add(alert); line.Children.Add(value); line.Children.Add(chev);
            mods.Content = line;
            mods.Click += (_, _) => { _modsOpen = true; Refresh(); };
            _hp2Panel.Children.Add(new Border { Background = Th.Well, BorderBrush = Th.Stroke, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), ClipToBounds = true, Child = mods });
        }
        else
        {
            // Load Game / New Game: the six slots 3x2, the save's thumbnail; an empty slot dashed (inert for Load, chosen for New Game).
            var back = SubPage("Main");
            var grid = new UniformGrid { Columns = 3 };
            Control first = null;
            foreach (var s in _hp2Slots)
            {
                // The card (author 2026-11-16, both launchers): the save's thumbnail fills it (aspect-fill, cropped, clipped to the
                // rounded corners) and the label sits on it at the bottom over a dark scrim (text >= 4.5:1 on any picture).
                var label = new TextBlock { Text = Hp2FrontEnd.Label(s, L(section, "IDC_SlotEmpty"), L(section, "IDC_SlotUsed")), FontSize = 13, FontWeight = FontWeight.SemiBold,
                    Foreground = s.Used ? Th.Text : Th.Dim, Margin = new Thickness(10, 0, 10, 9), VerticalAlignment = VerticalAlignment.Bottom };
                var card = new Grid();
                if (s.Used && Hp2Thumb(Hp2FrontEnd.SaveDir(_game), s.Thumb) is { } bmp) card.Children.Add(new Image { Source = bmp, Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
                else if (!s.Used) card.Children.Add(new TextBlock { Text = L(section, "IDC_SlotEmpty"), Foreground = Th.Dim, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 24) });
                if (s.Used) card.Children.Add(new Border { Height = 64, VerticalAlignment = VerticalAlignment.Bottom, Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops = { new(Color.FromArgb(0, 0, 0, 0), 0), new(Color.FromArgb(204, 0, 0, 0), 0.55), new(Color.FromArgb(217, 0, 0, 0), 1) } } });
                card.Children.Add(label);
                var content = new Border { CornerRadius = new CornerRadius(12), ClipToBounds = true, Child = card };
                var b = new Button { Name = $"Hp2Slot{s.N}", Content = content, Margin = new Thickness(6), Height = 132, Padding = new Thickness(0), CornerRadius = new CornerRadius(12), HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch, IsEnabled = !_playing && (s.Used || _hp2Page == "NewGame") };
                b.Classes.Add("quiet"); b.FontWeight = FontWeight.Normal;
                if (!s.Used) { b.Background = Brushes.Transparent; b.BorderThickness = new Thickness(0); }
                var slot = s;
                b.Click += async (_, _) =>
                {
                    if (_hp2Page == "NewGame")
                    {
                        // MessageBoxW(MB_YESNO, HPMenu [All] Select_Game_0001, caption Select_Game_0002) for a used slot
                        bool yes = !Hp2FrontEnd.AsksToReplace(slot) || await Confirm(Hp2FrontEnd.Localize(root, "HPMenu", "All", "Select_Game_0002"),
                            Hp2FrontEnd.Localize(root, "HPMenu", "All", "Select_Game_0001"), "Yes");
                        if (Hp2FrontEnd.NewGame(slot, () => yes, new Hp2FrontEnd.Files(Hp2FrontEnd.SaveDir(_game))))
                            Hp2Play(root, Hp2FrontEnd.CommandLine(Hp2FrontEnd.Launch.NewGame, slot.N, ""));
                    }
                    else if (Hp2FrontEnd.LoadGame(slot)) Hp2Play(root, Hp2FrontEnd.CommandLine(Hp2FrontEnd.Launch.LoadGame, slot.N, ""));
                };
                // the dashed outline of an empty slot, drawn over the transparent button
                var cell = new Panel { Children = { b } };
                if (!s.Used) cell.Children.Add(new Rectangle { Margin = new Thickness(6), RadiusX = 12, RadiusY = 12, StrokeThickness = 1, Stroke = Th.Stroke, StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 3 }, IsHitTestVisible = false });
                grid.Children.Add(cell);
                if (first == null && b.IsEnabled) first = b;
            }
            _hp2Panel.Children.Add(grid);
            _primary = first ?? back;
        }
    }

    static Bitmap Hp2Thumb(string saveDir, string path)
    {
        try { var f = new Hp2FrontEnd.Files(saveDir).Host(path); return File.Exists(f) ? new Bitmap(f) : null; }
        catch (Exception) { return null; }   // an unreadable thumbnail: the label alone, as the original's empty bitmap
    }

    // The new Game.exe process the original starts, here HPEngine on the HP2 folder with the original's parameters.
    void Hp2Play(string root, string cmdline)
    {
        if (_playing) return;
        LoadMods();   // the folders as they are now; an enabled mod whose folder is gone stops the engine (exit code 5)
        if ((ModsIni.Missing(_mods) ?? ModsIni.NewerEngine(root, _mods)) is string gone) { _hp2Error = gone; Refresh(); return; }
        if (!File.Exists(Exe("HPEngine"))) { _hp2Error = $"HPEngine was not found (the HPEngine folder must stay next to the Launcher) ({Exe("HPEngine")})."; Refresh(); return; }
        try
        {
            var logFile = LogFile(true);
            Directory.CreateDirectory(Path.GetDirectoryName(logFile));
            var log = new StreamWriter(new FileStream(logFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };   // the run's own console output, as Play's
            var psi = new ProcessStartInfo(Exe("HPEngine")) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in Hp2FrontEnd.PlayArgs(root, Hp2FrontEnd.SaveDir(_game), cmdline)) psi.ArgumentList.Add(a);
            var said = new List<string>();
            _hp2RunLogsBefore = GameScope.RunLogs(GameScope.LogsDir(_game, true));
            var p = Process.Start(psi); _play = p; p.StandardInput.Close();
            p.OutputDataReceived += (_, e) => { lock (log) if (e.Data != null) { log.WriteLine(e.Data); said.Add(e.Data); } };
            p.ErrorDataReceived += (_, e) => { lock (log) if (e.Data != null) { log.WriteLine(e.Data); said.Add(e.Data); } };
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            _playing = true; _hp2Error = null;
            _ = Task.Run(async () =>
            {
                await p.WaitForExitAsync(); lock (log) log.Dispose();
                Dispatcher.UIThread.Post(() =>
                {
                    _playing = false;
                    lock (log) _warning = EngineError.OverrideWarning(said);
                    if (p.ExitCode != 0) lock (log) _hp2Error = _hp2RunError = EngineError.Banner(said, p.ExitCode);
                    _hp2Page = "Main"; Hp2Scan(); Refresh();
                });
            });
        }
        catch (Exception e) { _hp2Error = e.Message; }
        Refresh();
    }
}
