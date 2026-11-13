// The launcher window (code only, no XAML): install flow, Play, Mods, More menu. Behaviour as launcher/Launcher.swift;
// the look is that file's `Theme`/cards/gold button (hex values in the comments), as far as Avalonia allows.
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Glyph = Avalonia.Controls.Shapes.Path;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;
using Rectangle = Avalonia.Controls.Shapes.Rectangle;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;

namespace HPEngine.Launcher;

static class Th
{
    static SolidColorBrush B(string hex, double a = 1) => new(Color.Parse(hex), a);
    public static readonly Color GoldC = Color.Parse("#D9A955"), GreenC = Color.Parse("#7BC47F"), AmberC = Color.Parse("#E0A54A"), RedC = Color.Parse("#E66B66");
    public static readonly IBrush Bg = B("#17141A"), Card = B("#221E25"), Stroke = B("#FFFFFF", 0.08), Gold = B("#D9A955"), GoldDark = B("#BE9042"), GoldHover = B("#E3B766"),
        Text = B("#EEE8DF"), Dim = B("#A39C93"), Green = B("#7BC47F"), Amber = B("#E0A54A"), Red = B("#E66B66"), OnGold = B("#291A0F");
    // the burgundy header: (0.42, 0.10, 0.10) -> (0.12, 0.05, 0.08)
    public static readonly FontFamily Serif = new("Georgia, Times New Roman, Liberation Serif, DejaVu Serif, serif");
}

public sealed class MainWindow : Window
{
    readonly string _game, _appDir, _version;
    readonly bool _own;   // _game is the player's own game folder (DataDir.OwnGame): nothing to install, never removed
    readonly List<string> _inputs = new();
    List<ModEntry> _mods = new();
    Process _setup, _play;
    int _step = -1; double _progress; bool _running, _playing; string _edition, _warning, _error;
    Release _newer;
    static readonly HttpClient Http = new();

    readonly TextBlock _status = new() { Name = "Status", Foreground = new SolidColorBrush(Colors.White, 0.85) },
        _editionText = new() { Name = "EditionText", FontSize = 14, FontWeight = FontWeight.Medium },
        _editionChip = new() { Name = "EditionChip", FontSize = 11, FontWeight = FontWeight.SemiBold },
        _warningText = new() { Name = "WarningText", TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center },
        _errorText = new() { Name = "ErrorText", TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    readonly Ellipse _dot = new() { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
    readonly Border _chipBox = new() { CornerRadius = new CornerRadius(99), Padding = new Thickness(8, 3) };
    readonly StackPanel _setupPanel = new() { Name = "SetupPanel", Spacing = 12 }, _inputsList = new() { Name = "InputsList", Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center },
        _readyPanel = new() { Name = "ReadyPanel", Spacing = 12 }, _modsList = new() { Name = "ModsList", Spacing = 2 };
    readonly Grid _stepsRow = new();
    readonly Border _stepsCard;
    readonly TextBlock _hintText = new() { Text = "Supported: US 1.53 (exact) · UK 1.1 · Russian EA release. Game data is copied from your own copy; nothing of the game is included.", FontSize = 12, Foreground = Th.Dim, TextWrapping = TextWrapping.Wrap };
    readonly ProgressBar _bar = new() { Name = "ProgressBar", Maximum = 1, Height = 4, Foreground = Th.Gold, Background = new SolidColorBrush(Colors.White, 0.08) };
    readonly StackPanel _dropIdle = new() { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
    readonly Border _warningBanner, _errorBanner;
    readonly Button _play2 = new() { Name = "PlayButton" }, _install = new() { Name = "InstallButton" };
    // "Start map" (Logic.cs StartMap): shown only while an enabled mod adds a map; never saved.
    readonly ComboBox _startMap = new() { Name = "StartMap", MinWidth = 180, MaxWidth = 300, MaxDropDownHeight = 320, VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel _startMapBox = new() { Name = "StartMapBox", Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 12, 0), IsVisible = false };
    List<(string Mod, string Map)> _addedMaps = new();
    string SelectedMap => _startMap.SelectedIndex > 0 && _startMap.SelectedIndex <= _addedMaps.Count ? _addedMaps[_startMap.SelectedIndex - 1].Map : null;
    readonly TextBlock _installText = new() { Text = "Install", MinWidth = 90, TextAlignment = TextAlignment.Center };
    readonly Border _updateBanner = new() { Name = "UpdateBanner", IsVisible = false, Padding = new Thickness(20, 0), Height = 44, Background = new SolidColorBrush(Th.GoldC, 0.08) };
    readonly TextBlock _updateText = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 13, FontWeight = FontWeight.Medium };
    readonly Button _updateOpen = new() { Content = "What's new" };
    readonly Rectangle _dropRect = new() { RadiusX = 14, RadiusY = 14, StrokeThickness = 1.5, StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 3.3 }, Fill = Th.Card };
    readonly TextBlock _versionText = new() { Foreground = new SolidColorBrush(Colors.White, 0.7) };

    public MainWindow(string gameDir, string appDir, string version, bool checkUpdates)
    {
        _own = DataDir.OwnGame(appDir, gameDir);   // the Launcher was unpacked into the player's own game folder: that game, not the data directory
        _game = _own ? appDir : gameDir; _appDir = appDir; _version = version;
        Title = "HPEngine Launcher"; Width = 600; SizeToContent = SizeToContent.Height; CanResize = false;
        Background = Th.Bg; Foreground = Th.Text; FontSize = 13;
        AddStyles();
        _edition = Edition();
        try { Icon = ShieldIcon.Make(); } catch (Exception) { /* no renderer for the bitmap: the default icon stays */ }

        var title = new TextBlock { Text = "Harry Potter and the Philosopher's Stone", FontSize = 24, FontWeight = FontWeight.Bold, Foreground = Brushes.White, FontFamily = Th.Serif };
        _versionText.Text = "HPEngine " + version;
        var header = new Border
        {
            Padding = new Thickness(22, 26, 22, 18),
            Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromRgb(107, 26, 26), 0), new GradientStop(Color.FromRgb(31, 13, 20), 1) } },
            Child = new StackPanel { Spacing = 6, Children = { title,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _versionText, _dot, _status } } } },
        };

        _updateOpen.Classes.Add("quiet");
        _updateOpen.Click += (_, _) => { if (_newer != null) Open(_newer.Page); };
        _updateBanner.Child = new DockPanel { Children = { Dock(_updateOpen, Avalonia.Controls.Dock.Right), _updateText } };

        _stepsCard = MakeCard(null, _stepsRow, _bar);
        _warningBanner = MakeBanner(Th.Amber, "!", _warningText);
        _errorBanner = MakeBanner(Th.Red, "×", _errorText);
        BuildSetupPanel();
        BuildReadyPanel();

        _play2.Classes.Add("gold"); _install.Classes.Add("gold");
        _play2.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Children = {
            new Glyph { Data = Geometry.Parse("M0,0 L9,5.5 L0,11 Z"), Fill = Th.OnGold, VerticalAlignment = VerticalAlignment.Center }, new TextBlock { Text = "Play", MinWidth = 60, TextAlignment = TextAlignment.Left } } };
        _install.Content = _installText;
        _play2.Click += (_, _) => Play();
        _install.Click += (_, _) => Install();
        var more = new Button { Name = "MoreButton", Content = "More…", Background = Brushes.Transparent, Foreground = Th.Dim, Flyout = BuildMoreMenu() };
        _startMapBox.Children.Add(new TextBlock { Text = StartMap.Title, FontSize = 12, Foreground = Th.Dim, VerticalAlignment = VerticalAlignment.Center });
        _startMapBox.Children.Add(_startMap);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { _startMapBox, _play2, _install } };
        var footer = new DockPanel { Margin = new Thickness(0, 6, 0, 0), Children = { Dock(buttons, Avalonia.Controls.Dock.Right), more } };
        var body = new StackPanel { Margin = new Thickness(20), Spacing = 12, Children = { _setupPanel, _readyPanel, _warningBanner, _errorBanner, footer } };
        Content = new StackPanel { Children = { header, _updateBanner, body } };

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            foreach (var f in e.DataTransfer.TryGetFiles() ?? Array.Empty<IStorageItem>())
                if (f.TryGetLocalPath() is { } p) Add(p);
        });
        Closing += (_, _) => { if (_setup is { HasExited: false }) try { _setup.Kill(true); } catch { } };   // HP1Setup resumes an interrupted copy
        Refresh();
        if (checkUpdates) _ = CheckForUpdate(false);
    }

    static Control Dock(Control c, Dock d) { DockPanel.SetDock(c, d); return c; }
    bool Installed => _own || File.Exists(Path.Combine(_game, ".hp1-installed"));   // HP1Setup writes it last
    bool Setup => !Installed || _running || _inputs.Count > 0;
    string Edition() => !File.Exists(Path.Combine(_game, ".hp1-installed")) ? null : File.ReadLines(Path.Combine(_game, ".hp1-installed")).FirstOrDefault(l => l.StartsWith("edition="))?[8..];
    // The engine and HP1Setup live in the HPEngine/ folder of the archive, beside the Launcher (make_launcher.sh); no other layout is built.
    string Exe(string n) => Path.Combine(_appDir, "HPEngine", OperatingSystem.IsWindows() ? n + ".exe" : n);
    string LogFile => _own ? Path.Combine(_game, "HPEngineData", "HPEngine.log") : Path.Combine(Path.GetDirectoryName(_game), "HPEngine.log");

    // ---- look ----
    // Button looks: "gold" (primary: #D9A955, pressed #BE9042, dark text, 40 high, radius 10), "quiet" (white 7% / 14% pressed,
    // 8% border, radius 8, 30 high), "flat" (no box). Fluent restyles the template's presenter on hover, so the states are set there.
    void AddStyles()
    {
        Resources["ToggleSwitchFillOn"] = Th.Gold; Resources["ToggleSwitchFillOnPointerOver"] = Th.GoldHover; Resources["ToggleSwitchFillOnPressed"] = Th.GoldDark;
        Resources["ProgressBarForeground"] = Th.Gold;
        void Look(string cls, IBrush bg, IBrush hover, IBrush pressed, IBrush fg, IBrush border, double height, double radius, Thickness pad, FontWeight weight, double size)
        {
            Style Pres(string state, IBrush b)
            {
                var s = new Style(x => state == null ? x.OfType<Button>().Class(cls).Template().OfType<ContentPresenter>() : x.OfType<Button>().Class(cls).Class(state).Template().OfType<ContentPresenter>());
                s.Setters.Add(new Setter(ContentPresenter.BackgroundProperty, b)); s.Setters.Add(new Setter(ContentPresenter.ForegroundProperty, fg));
                s.Setters.Add(new Setter(ContentPresenter.BorderBrushProperty, border));
                return s;
            }
            Styles.Add(new Style(x => x.OfType<Button>().Class(cls)) { Setters = { new Setter(Button.HeightProperty, height), new Setter(Button.CornerRadiusProperty, new CornerRadius(radius)),
                new Setter(Button.PaddingProperty, pad), new Setter(Button.FontWeightProperty, weight), new Setter(Button.FontSizeProperty, size), new Setter(Button.BackgroundProperty, bg), new Setter(Button.ForegroundProperty, fg),
                new Setter(Button.BorderBrushProperty, border), new Setter(Button.BorderThicknessProperty, new Thickness(border == Brushes.Transparent ? 0 : 1)), new Setter(Button.VerticalContentAlignmentProperty, VerticalAlignment.Center) } });
            Styles.Add(Pres(null, bg)); Styles.Add(Pres(":pointerover", hover)); Styles.Add(Pres(":pressed", pressed));
            Styles.Add(new Style(x => x.OfType<Button>().Class(cls).Class(":disabled")) { Setters = { new Setter(OpacityProperty, 0.45) } });
            var d = Pres(":disabled", bg); Styles.Add(d);
        }
        Look("gold", Th.Gold, Th.GoldHover, Th.GoldDark, Th.OnGold, Brushes.Transparent, 40, 10, new Thickness(22, 0), FontWeight.SemiBold, 15);
        Look("quiet", new SolidColorBrush(Colors.White, 0.07), new SolidColorBrush(Colors.White, 0.11), new SolidColorBrush(Colors.White, 0.14), Th.Text, Th.Stroke, 30, 8, new Thickness(12, 0), FontWeight.Medium, 13);
        Look("flat", Brushes.Transparent, new SolidColorBrush(Colors.White, 0.07), new SolidColorBrush(Colors.White, 0.12), Th.Text, Brushes.Transparent, 24, 6, new Thickness(6, 0), FontWeight.Normal, 13);
    }

    // A card: #221E25, radius 12, 8% white border, optional small uppercase title.
    static Border MakeCard(string title, params Control[] content)
    {
        var s = new StackPanel { Spacing = 10 };
        if (title != null) s.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.8, Foreground = Th.Dim });
        foreach (var c in content) s.Children.Add(c);
        return new Border { Background = Th.Card, BorderBrush = Th.Stroke, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(14), Child = s };
    }

    // One-line banner (tinted 12%, 35% border, radius 10) with More/Less to show the whole text.
    static Border MakeBanner(IBrush color, string glyph, TextBlock text)
    {
        var c = ((SolidColorBrush)color).Color;
        text.Foreground = Th.Text;
        var more = new Button { Content = "More", Foreground = color, FontSize = 12, FontWeight = FontWeight.Medium, Background = Brushes.Transparent, Padding = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Top };
        more.Classes.Add("flat"); more.Foreground = color;
        more.Click += (_, _) =>
        {
            var open = text.TextWrapping == TextWrapping.NoWrap;
            text.TextWrapping = open ? TextWrapping.Wrap : TextWrapping.NoWrap; text.TextTrimming = open ? TextTrimming.None : TextTrimming.CharacterEllipsis;
            more.Content = open ? "Less" : "More";
        };
        var icon = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = color, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 8, 0),
            Child = new TextBlock { Text = glyph, Foreground = Th.OnGold, FontSize = 12, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        g.Children.Add(icon); Grid.SetColumn(text, 1); g.Children.Add(text); Grid.SetColumn(more, 2); g.Children.Add(more);
        return new Border { Background = new SolidColorBrush(c, 0.12), BorderBrush = new SolidColorBrush(c, 0.35), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 9), Child = g, IsVisible = false };
    }

    void BuildSetupPanel()
    {
        var choose = new Button { Name = "ChooseButton", Content = "Add file…" }; var addDir = new Button { Content = "Add folder…" }; var clear = new Button { Content = "Clear" };
        foreach (var b in new[] { choose, addDir, clear }) b.Classes.Add("quiet");
        choose.Click += async (_, _) => { foreach (var f in await StorageProvider.OpenFilePickerAsync(new() { AllowMultiple = true, Title = "Game installer (Setup.exe, data1.hdr) or .zip" })) if (f.TryGetLocalPath() is { } p) Add(p); };
        addDir.Click += async (_, _) => { foreach (var f in await StorageProvider.OpenFolderPickerAsync(new() { AllowMultiple = true, Title = "Installed game folder or the game CD" })) if (f.TryGetLocalPath() is { } p) Add(p); };
        clear.Click += (_, _) => { _inputs.Clear(); Refresh(); };
        // tray-with-arrow icon, a path (no icon font)
        _dropIdle.Children.Add(new Glyph { Data = Geometry.Parse("M15,3 L15,18 M9,12 L15,18 L21,12 M4,19 L4,26 L26,26 L26,19"), Stroke = Th.Gold, StrokeThickness = 1.5, Width = 30, Height = 30,
            StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, HorizontalAlignment = HorizontalAlignment.Center });
        _dropIdle.Children.Add(new TextBlock { Text = "Drop your copy of the game here", FontSize = 15, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        _dropIdle.Children.Add(new TextBlock { Text = "Installed game folder, CD folder, installer or .zip (mount a disc image first)", FontSize = 12, Foreground = Th.Dim, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Children = { choose, addDir, clear } };
        var zone = new Grid { MinHeight = 150 };
        zone.Children.Add(_dropRect);
        zone.Children.Add(new StackPanel { Margin = new Thickness(14), Spacing = 10, VerticalAlignment = VerticalAlignment.Center, Children = { _dropIdle, _inputsList, buttons } });
        _setupPanel.Children.Add(zone);
        _setupPanel.Children.Add(_stepsCard);
        _setupPanel.Children.Add(_hintText);
        for (int i = 0; i < 7; i++) _stepsRow.ColumnDefinitions.Add(new ColumnDefinition(i % 2 == 0 ? GridLength.Auto : GridLength.Star));
    }

    void BuildReadyPanel()
    {
        var saves = new Button { Name = "SavesButton", Content = "Saved Games" }; var logs = new Button { Name = "LogsButton", Content = "Logs" };
        saves.Classes.Add("quiet"); logs.Classes.Add("quiet");
        saves.Click += (_, _) => OpenFolder(Path.Combine(_game, "Save")); logs.Click += (_, _) => OpenFolder(Path.Combine(_game, "HPEngineData", "logs"));
        _chipBox.Child = _editionChip;
        var gameCard = MakeCard("Game",
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _editionText, _chipBox } },
            new DockPanel { Children = { Dock(new TextBlock { Text = "Drop other game files here to reinstall", IsVisible = !_own, FontSize = 11, Foreground = Th.Dim, VerticalAlignment = VerticalAlignment.Center }, Avalonia.Controls.Dock.Right),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { saves, logs } } } });
        var openMods = new Button { Name = "OpenModsButton", Content = "Open Mods Folder" }; var refresh = new Button { Name = "RefreshMods", Content = "Refresh" };
        openMods.Classes.Add("quiet"); refresh.Classes.Add("quiet");
        openMods.Click += (_, _) => OpenFolder(Path.Combine(_game, "Mods"));
        refresh.Click += (_, _) => Refresh();
        _modsHint.Foreground = Th.Dim;
        var modsCard = MakeCard("Mods", _noMods, new ScrollViewer { MaxHeight = 128, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = _modsList },
            _modsNotice,
            new DockPanel { Children = { Dock(_modsHint, Avalonia.Controls.Dock.Right), new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { openMods, refresh } } } });
        _readyPanel.Children.Add(gameCard);
        _readyPanel.Children.Add(modsCard);
    }
    // "i" in a ring (amber) + the dim hint lines of the card; shown by RebuildMods per ModsNotice.Lines
    readonly StackPanel _modsNotice = new() { Name = "ModsNotice", Orientation = Orientation.Horizontal, Spacing = 8, IsVisible = false, Children = {
        new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1.2), BorderBrush = Th.Amber, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0),
            Child = new TextBlock { Text = "i", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Th.Amber, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } },
        new TextBlock { Name = "ModsNoticeText", FontSize = 12, Foreground = Th.Dim, TextWrapping = TextWrapping.Wrap, MaxWidth = 470 } } };
    readonly TextBlock _modsHint = new() { Text = "Lower in the list wins", FontSize = 11, VerticalAlignment = VerticalAlignment.Center },
        _noMods = new() { Name = "NoMods", Text = "No mods yet. Put a mod's folder into the Mods folder; it shows up here to switch on and off.", FontSize = 12, Foreground = Th.Dim, TextWrapping = TextWrapping.Wrap };

    FlyoutBase BuildMoreMenu()
    {
        var fly = new MenuFlyout { Placement = PlacementMode.TopEdgeAlignedLeft };
        var enabled = new List<(MenuItem, Func<bool>)>();
        void M(string name, string text, Action a, Func<bool> en = null)
        {
            var mi = new MenuItem { Name = name, Header = text }; mi.Click += (_, _) => a();
            if (en != null) enabled.Add((mi, en));
            fly.Items.Add(mi);
        }
        M("OpenSaves", "Open Saved Games Folder", () => OpenFolder(Path.Combine(_game, "Save")), () => Installed);
        M("OpenLogs", "Open Logs", () => OpenFolder(Path.Combine(_game, "HPEngineData", "logs")));
        M("ResetSettings", "Reset Settings…", () => _ = ResetSettings(), () => Installed);
        M("ResetPad", "Reset Gamepad Bindings…", () => _ = ResetPad(), () => Installed && !_playing && PadReset.Rebound(_game) > 0);
        M("RemoveGame", _own ? "Remove Game: not for your own game folder" : "Remove Game…", () => _ = RemoveGame(), () => !_own && Installed && !_running && !_playing);
        fly.Items.Add(new Separator());
        M("CheckUpdates", "Check for Updates", () => _ = CheckForUpdate(true));
        M("About", "About HPEngine Launcher", () => _ = About());
        fly.Opening += (_, _) => { foreach (var (mi, e) in enabled) mi.IsEnabled = e(); };
        return fly;
    }

    // ---- state -> view ----
    void Refresh()
    {
        var (st, col) = _running ? ("Installing…", Th.Gold) : _playing ? ("Playing", Th.Green) : Installed ? ("Ready to play", Th.Green) : ("Not installed", Th.Dim);
        _status.Text = st; _dot.Fill = col;
        _setupPanel.IsVisible = Setup; _readyPanel.IsVisible = !Setup;
        // the Game card: "US 1.53: exact" -> name + chip (exact: verified, green; supported: gold; others amber with the tier text)
        var parts = (_edition ?? "").Split(": ");
        var tier = parts.Length > 1 ? parts[^1] : "";
        _editionText.Text = _edition == null ? (_own ? "Your game folder: " + _game : "Installed") : parts.Length > 1 ? string.Join(": ", parts[..^1]) : _edition;
        var (cc, label) = tier switch { "exact" => (Th.GreenC, "verified"), "supported" => (Th.GoldC, "supported"), _ => (Th.AmberC, tier.Length == 0 ? "unknown" : tier) };
        _chipBox.IsVisible = _edition != null; _editionChip.Text = label; _editionChip.Foreground = new SolidColorBrush(cc); _chipBox.Background = new SolidColorBrush(cc, 0.15);
        UpdateBanners();
        _inputsList.Children.Clear();
        foreach (var i in _inputs) _inputsList.Children.Add(new TextBlock { Text = Path.GetFileName(i.TrimEnd('/', '\\')) is { Length: > 0 } n ? n : i, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center });
        _dropIdle.IsVisible = _inputs.Count == 0;
        _dropRect.Stroke = new SolidColorBrush(Th.GoldC, 0.35);
        bool steps = _running || _step >= 0;
        _stepsCard.IsVisible = steps; _hintText.IsVisible = !steps;
        _stepsRow.Children.Clear();
        for (int i = 0; i < SetupProtocol.Steps.Length; i++)
        {
            var done = i < _step; var cur = i == _step;
            var ring = new Ellipse { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center, StrokeThickness = 1.5,
                Fill = done ? Th.Green : Brushes.Transparent, Stroke = done ? Th.Green : cur ? Th.Gold : Th.Dim };
            var cell = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { ring, new TextBlock { Text = SetupProtocol.Steps[i].Text, FontSize = 12, Foreground = i <= _step ? Th.Text : Th.Dim } } };
            Grid.SetColumn(cell, i * 2); _stepsRow.Children.Add(cell);
        }
        _bar.IsVisible = _running && _step >= 2; _bar.Value = _progress;
        bool ready = Installed && !_running && _inputs.Count == 0;
        _play2.IsVisible = ready; _play2.IsEnabled = !_playing;
        _install.IsVisible = !ready; _installText.Text = Installed ? "Install Again" : "Install";
        _install.IsEnabled = _inputs.Count > 0 && !_running && !_playing;
        _mods = Installed ? ModsIni.Load(_game) : new List<ModEntry>();
        RebuildMods();
    }

    void UpdateBanners()
    {
        _warningText.Text = _warning; _warningBanner.IsVisible = _warning != null;
        _errorText.Text = _error; _errorBanner.IsVisible = _error != null;
    }

    static Button Chevron(string name, string path, bool on)
    {
        var b = new Button { Name = name, IsEnabled = on, Width = 26, Height = 24, Padding = new Thickness(0),
            Content = new Glyph { Data = Geometry.Parse(path), Stroke = on ? Th.Text : Th.Dim, StrokeThickness = 1.6, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, HorizontalAlignment = HorizontalAlignment.Center } };
        b.Classes.Add("flat"); b.Width = 26; b.Opacity = on ? 1 : 0.4;
        return b;
    }

    void RebuildMods()
    {
        _modsList.Children.Clear();
        var notice = Installed ? ModsNotice.Lines(_game, _mods).Concat(ModsIni.Misplaced(_game, _mods)).ToList() : new List<string>();
        _modsNotice.IsVisible = notice.Count > 0;
        ((TextBlock)_modsNotice.Children[1]).Text = string.Join("\n", notice);
        _noMods.IsVisible = _mods.Count == 0; _modsHint.IsVisible = _mods.Any(m => m.Enabled);
        // The start maps of the mods as they are enabled now; the chosen one stays while it is still there, else "Game".
        var chosen = SelectedMap;
        _addedMaps = _play2.IsVisible ? StartMap.Added(_game) : new();
        _startMap.ItemsSource = StartMap.Labels(_addedMaps).Prepend(StartMap.Game).ToList();
        _startMap.SelectedIndex = _addedMaps.FindIndex(m => m.Map == chosen) + 1;
        _startMapBox.IsVisible = _addedMaps.Count > 0; _startMap.IsEnabled = !_playing;
        int firstOff = _mods.FindIndex(m => !m.Enabled), enabledCount = firstOff < 0 ? _mods.Count : firstOff;
        for (int i = 0; i < _mods.Count; i++)
        {
            var m = _mods[i]; int idx = i;
            var sw = new ToggleSwitch { Name = "Mod_" + m.Name, IsChecked = m.Enabled, OnContent = null, OffContent = null, IsEnabled = !_playing, VerticalAlignment = VerticalAlignment.Center };
            sw.IsCheckedChanged += (_, _) =>
            {
                var on = sw.IsChecked == true; if (on == m.Enabled) return;
                _mods.RemoveAt(idx); _mods.Insert(_mods.Count(x => x.Enabled), m with { Enabled = on });   // on: last of the enabled (wins); off: first of the disabled
                SaveMods();
            };
            var up = Chevron("Up_" + m.Name, "M3,8 L7,4 L11,8", m.Enabled && i > 0 && !_playing);
            var down = Chevron("Down_" + m.Name, "M3,4 L7,8 L11,4", m.Enabled && i < enabledCount - 1 && !_playing);
            up.Click += (_, _) => { (_mods[idx - 1], _mods[idx]) = (_mods[idx], _mods[idx - 1]); SaveMods(); };
            down.Click += (_, _) => { (_mods[idx + 1], _mods[idx]) = (_mods[idx], _mods[idx + 1]); SaveMods(); };
            var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Children = {
                new TextBlock { Text = m.Name, Foreground = m.Enabled ? Th.Text : Th.Dim, TextTrimming = TextTrimming.CharacterEllipsis } } };
            if (!m.Present) name.Children.Add(new TextBlock { Text = "folder missing", FontSize = 11, Foreground = Th.Amber, VerticalAlignment = VerticalAlignment.Center });
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
            row.Children.Add(sw); Grid.SetColumn(name, 1); row.Children.Add(name);
            if (m.Enabled) { Grid.SetColumn(up, 2); row.Children.Add(up); Grid.SetColumn(down, 3); row.Children.Add(down); }
            _modsList.Children.Add(new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 3), Background = new SolidColorBrush(Colors.White, m.Enabled ? 0.04 : 0), Child = row });
        }
    }

    void SaveMods()
    {
        try { ModsIni.Save(_game, _mods); _error = null; }
        catch (Exception e) { _error = "Could not save Mods.ini: " + e.Message; }
        _mods = Installed ? ModsIni.Load(_game) : _mods;
        RebuildMods(); Refresh2();
    }
    void Refresh2() => UpdateBanners();

    // ---- install ----
    void Add(string path)
    {
        if (_own) return;   // the game next to the Launcher is used: nothing to install
        if (!_inputs.Contains(path)) _inputs.Add(path);
        _error = null; _step = -1; Refresh();
    }

    void Install()
    {
        if (_own || _inputs.Count == 0 || _running || _playing) return;
        if (!File.Exists(Exe("HP1Setup"))) { _error = $"HP1Setup was not found (the HPEngine folder must stay next to the Launcher) ({Exe("HP1Setup")})."; Refresh(); return; }
        _running = true; _error = null; _warning = null; _step = 0; _progress = 0;
        var psi = new ProcessStartInfo(Exe("HP1Setup")) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true, UseShellExecute = false };
        psi.ArgumentList.Add("--dest"); psi.ArgumentList.Add(_game);
        foreach (var i in _inputs) psi.ArgumentList.Add(i);
        var odd = new List<string>();
        void Line(string l) => Dispatcher.UIThread.Post(() => { if (l != null) Handle(l, odd); });   // posted in line order per stream
        try
        {
            _setup = Process.Start(psi);
            _setup.StandardInput.Close();
            _setup.OutputDataReceived += (_, e) => Line(e.Data);
            _setup.ErrorDataReceived += (_, e) => Line(e.Data);
            _setup.BeginOutputReadLine(); _setup.BeginErrorReadLine();
            var p = _setup;
            _ = Task.Run(async () =>
            {
                await p.WaitForExitAsync();
                Dispatcher.UIThread.Post(() =>   // after the queued output lines
                {
                    _running = false;
                    if (p.ExitCode != 0 && _error == null)
                        _error = $"The setup stopped unexpectedly (code {p.ExitCode})." + (odd.Count > 0 ? " Its output: " + string.Join(" | ", odd.TakeLast(3)) : "");
                    Refresh();
                });
            });
        }
        catch (Exception e) { _error = e.Message; _running = false; }
        Refresh();
    }

    // Public for the headless check: one HP1Setup line applied to the state.
    public void Handle(string line, List<string> odd)
    {
        var l = SetupProtocol.Parse(line);
        switch (l.Kind)
        {
            case SetupKind.Step: _step = l.Index; _progress = 0; break;
            case SetupKind.Progress: _progress = l.Progress; break;
            case SetupKind.Error: _error = l.Text; _edition = Edition(); break;   // the installed edition is still the marker's
            case SetupKind.Warning: _warning = l.Text; break;
            case SetupKind.Edition: _edition = l.Text; break;
            case SetupKind.Done: _step = SetupProtocol.Steps.Length; _inputs.Clear(); _edition = Edition(); break;
            case SetupKind.Malformed:
                odd.Add(l.Raw);
                _warning = $"Unexpected output from the setup ({l.Text}): {l.Raw}"; break;
        }
        Refresh();
    }

    // ---- play ----
    void Play()
    {
        if (_playing) return;
        if (Installed) { _mods = ModsIni.Load(_game); RebuildMods(); }   // the folders as they are now
        if (ModsIni.Missing(_mods) is string gone) { _error = gone; Refresh(); return; }
        if (!File.Exists(Exe("HPEngine"))) { _error = $"HPEngine was not found (the HPEngine folder must stay next to the Launcher) ({Exe("HPEngine")})."; Refresh(); return; }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile));
            var log = new StreamWriter(new FileStream(LogFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };   // the run's own console output (crashes included)
            var psi = new ProcessStartInfo(Exe("HPEngine")) { WorkingDirectory = _game, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in StartMap.PlayArgs(_game, SelectedMap)) psi.ArgumentList.Add(a);
            var said = new List<string>();   // the engine's console output, for the error banner
            var p = Process.Start(psi); _play = p; p.StandardInput.Close();
            p.OutputDataReceived += (_, e) => { lock (log) if (e.Data != null) { log.WriteLine(e.Data); said.Add(e.Data); } };
            p.ErrorDataReceived += (_, e) => { lock (log) if (e.Data != null) { log.WriteLine(e.Data); said.Add(e.Data); } };
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            _playing = true; _error = null;
            _ = Task.Run(async () =>
            {
                await p.WaitForExitAsync(); lock (log) log.Dispose();
                Dispatcher.UIThread.Post(() =>
                {
                    _playing = false;
                    if (p.ExitCode != 0) lock (log) _error = EngineError.Banner(said, p.ExitCode, $"More → Open Logs, and {LogFile}.");
                    Refresh();
                });
            });
        }
        catch (Exception e) { _error = e.Message; }
        Refresh();
    }

    // ---- More ----
    async Task ResetSettings()
    {
        if (!await Confirm("Reset settings?", "Video, sound and control settings go back to the game's defaults. Saved games are kept. The old files are moved to HPEngineData/settings-backup.", "Reset")) return;
        var data = Path.Combine(_game, "HPEngineData");
        foreach (var n in new[] { "Default.ini", "DefUser.ini" })   // the overlay HPEngine writes; the game's own ini files are never changed
        {
            var f = Path.Combine(data, n);
            if (!File.Exists(f)) continue;
            var bak = Path.Combine(data, "settings-backup"); Directory.CreateDirectory(bak);
            File.Move(f, Path.Combine(bak, $"{n}.{DateTime.Now:yyyyMMdd-HHmmss}"), true);
        }
    }

    async Task ResetPad()
    {
        if (!await Confirm("Reset gamepad bindings?", "The gamepad buttons and the left stick go back to their default actions. Keyboard and mouse bindings and all other settings are kept.", "Reset")) return;
        try { PadReset.Rebound(_game, true); } catch (IOException e) { _error = e.Message; Refresh(); }
    }

    // Remove the installed game copy (saved games, settings and mods kept unless asked). Windows: Recycle Bin; Linux: permanent.
    async Task RemoveGame()
    {
        if (_own || !Installed || _running || _playing) return;
        var fate = OperatingSystem.IsWindows() ? "It is moved to the Recycle Bin." : "It is deleted permanently (there is no portable trash on Linux).";
        var all = new CheckBox { Name = "AlsoDelete", Content = "Also delete saved games, settings and mods" };
        var d = new Window { Title = "Remove game?", Width = 460, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        var yes = new Button { Content = "Remove", MinWidth = 80 }; var no = new Button { Content = "Cancel", MinWidth = 80 };
        yes.Click += (_, _) => d.Close(true); no.Click += (_, _) => d.Close(false);
        d.Content = new StackPanel { Margin = new Thickness(20), Spacing = 14, Children = {
            new TextBlock { Text = $"The installed game copy in {_game} is removed. Saved games, settings and mods are kept. {fate} Your own game files are not touched.", TextWrapping = TextWrapping.Wrap },
            all, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { yes, no } } } };
        if (!await d.ShowDialog<bool>(this)) return;
        try { GameRemoval.Remove(_game, all.IsChecked == true, OperatingSystem.IsWindows() ? Recycle : null); _error = null; _warning = null; _step = -1; _edition = null; }
        catch (Exception e) { _error = "Could not remove the game: " + e.Message; }
        Refresh();
    }

    static void Recycle(string p)
    {
        if (Directory.Exists(p)) Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(p, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        else Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(p, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
    }

    async Task About() => await Confirm("HPEngine Launcher " + _version,
        $"HPEngine runs Harry Potter and the Philosopher's Stone (PC, 2001) from your own copy of the game. No game files are included. Game data: {_game}" + (_own ? " (your game folder, the one this Launcher is in)" : ""), "OK", false);

    async Task<bool> Confirm(string title, string text, string ok, bool cancel = true)
    {
        var d = new Window { Title = title, Width = 420, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        var yes = new Button { Content = ok, MinWidth = 80 }; var no = new Button { Content = "Cancel", MinWidth = 80, IsVisible = cancel };
        yes.Click += (_, _) => d.Close(true); no.Click += (_, _) => d.Close(false);
        d.Content = new StackPanel { Margin = new Thickness(20), Spacing = 14, Children = { new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { yes, no } } } };
        return await d.ShowDialog<bool>(this);
    }

    static void Open(string target)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { ArgumentList = { target }, UseShellExecute = false });
        }
        catch (Exception) { /* no handler (e.g. no xdg-open): nothing to show the player beyond the path in the About box */ }
    }

    void OpenFolder(string dir) { Directory.CreateDirectory(dir); Open(dir); }

    // ---- updates: no self-update on Windows/Linux; the release page opens ----
    async Task CheckForUpdate(bool manual)
    {
        string text = null; Release found = null;
        try
        {
            using var rq = new HttpRequestMessage(HttpMethod.Get, Environment.GetEnvironmentVariable("HPENGINE_RELEASES_URL") ?? "https://api.github.com/repos/artemnikonov/hpengine/releases?per_page=30");
            rq.Headers.UserAgent.ParseAdd("HPEngine-Launcher");
            text = await (await Http.SendAsync(rq)).EnsureSuccessStatusCode().Content.ReadAsStringAsync();
            found = Updates.Pick(text, _version);
        }
        catch (Exception) { text = null; }
        if (found != null) { _newer = found; _updateText.Text = $"Launcher {found.Version} is available"; _updateBanner.IsVisible = true; return; }
        if (manual) await Confirm("Check for Updates", text == null ? "Could not check for updates." : $"HPEngine Launcher {_version} is up to date.", "OK", false);
    }
}
