// The launcher window (code only, no XAML): install flow, Play, Mods, Menu. Behaviour as launcher/Launcher.swift; the look
// is the approved redesign (the maintainer's notes, 2026-11-16): a fixed 600x560 window that each game paints
// whole (its palette, its serif title, one gold primary button); rare actions in the Menu; gamepad A/B/LB/RB/Start/Y.
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Glyph = Avalonia.Controls.Shapes.Path;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;
using Rectangle = Avalonia.Controls.Shapes.Rectangle;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace HPEngine.Launcher;

// The palette of the selected game (proposal §4, contrast checked there). The brushes are shared and recoloured in place
// by Use(), so every control painted with them follows a game switch.
static class Th
{
    static SolidColorBrush B() => new(Colors.Transparent);
    public static readonly SolidColorBrush Text = B(), Dim = B(), Title = B(), Accent = B(), AccentHover = B(), AccentPress = B(), OnAccent = B(),
        Surface = B(), Surface2 = B(), Stroke = B(), Hot = B(), ItemHover = B(), ItemPress = B(), Ok = B(), Warn = B(), Err = B(), Popup = B(), Well = new(Colors.Black, 0.22);
    public static readonly GradientStop Top = new(), Mid = new() { Offset = 0.5 }, Bottom = new() { Offset = 1 }, Glow = new(), GlowEnd = new() { Offset = 0.6 }, Glow2 = new(), Glow2End = new() { Offset = 0.7 };
    public static readonly FontFamily Serif = LoadSerif();

    public static void Use(bool hp2)
    {
        static Color C(string hex, double a = 1) { var c = Color.Parse(hex); return Color.FromArgb((byte)Math.Round(a * 255), c.R, c.G, c.B); }
        // HP1 "Stone" in warm gold (author 2026-11-16; proposal §4): text >= 4.5:1 on the lightest point (#8C6224), the title (large) >= 3:1.
        Text.Color = C(hp2 ? "#F3EAE2" : "#FFF6E6"); Dim.Color = C(hp2 ? "#C9B29A" : "#FAF0DD"); Title.Color = C(hp2 ? "#E8C066" : "#F2C14E");
        Accent.Color = C(hp2 ? "#D4AE62" : "#FFE7A3"); AccentHover.Color = C(hp2 ? "#E0BE78" : "#FFF0C4"); AccentPress.Color = C(hp2 ? "#B8956A" : "#F2D27F");
        OnAccent.Color = C(hp2 ? "#1E0A06" : "#2A1A08");
        Surface.Color = hp2 ? C("#FFF0DC", 0.06) : C("#000000", 0.20); Surface2.Color = hp2 ? C("#FFF0DC", 0.11) : C("#000000", 0.30);
        Stroke.Color = hp2 ? C("#B8956A", 0.28) : C("#FFE7A3", 0.35);
        Hot.Color = Accent.Color.WithA(153);   // a hovered / focused button's border: the accent at 60 %
        ItemHover.Color = Title.Color.WithA(41); ItemPress.Color = Title.Color.WithA(71);   // a list item under the pointer / pressed: the title gold 16 / 28 %
        Ok.Color = C(hp2 ? "#86B97F" : "#DBFAD6"); Warn.Color = C(hp2 ? "#E8C066" : "#FFEDBD"); Err.Color = C(hp2 ? "#F0A3A0" : "#FFDDD8");
        Popup.Color = C(hp2 ? "#22100F" : "#2A1C0D");
        Top.Color = C(hp2 ? "#4A1216" : "#8C6224"); Mid.Color = C(hp2 ? "#2F0C10" : "#5A3A17"); Bottom.Color = C(hp2 ? "#14050A" : "#2B1D0E");
        Glow.Color = hp2 ? C("#8E1A1F", 0.45) : C("#F2C14E", 0); GlowEnd.Color = Glow.Color.WithA(0) ;
        Glow2.Color = hp2 ? C("#3F6B3B", 0.16) : C("#C8323C", 0.10); Glow2End.Color = Glow2.Color.WithA(0);
    }
    static Color WithA(this Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    // Cormorant Garamond Bold from the launcher's resources; if it cannot be loaded, the system serif.
    public const string SerifUri = "avares://HPEngineLauncher/Fonts#Cormorant Garamond";
    public const string SystemSerif = "Georgia, Times New Roman, Liberation Serif, DejaVu Serif, serif";
    static FontFamily LoadSerif()
    {
        try
        {
            var f = new FontFamily(SerifUri);
            if (FontManager.Current.TryGetGlyphTypeface(new Typeface(f, FontStyle.Normal, FontWeight.Bold), out var g) && g.FamilyName == "Cormorant Garamond") return f;
        }
        catch (Exception) { }
        return new FontFamily(SystemSerif);
    }
}

// Reduce motion (Windows: "Show animations in Windows" off; GNOME: enable-animations false): colours still change, nothing
// moves (no press scale, no list or banner entrance), as Launcher.swift with accessibilityReduceMotion.
static class Motion
{
    public static readonly bool Reduced = Detect();
    static bool Detect()
    {
        if (Environment.GetEnvironmentVariable("HPENGINE_REDUCE_MOTION") is { Length: > 0 } v) return v != "0";   // an override, also for the headless shots
        try
        {
            if (OperatingSystem.IsWindows()) { bool on = true; return SystemParametersInfo(0x1042 /* SPI_GETCLIENTAREAANIMATION */, 0, ref on, 0) && !on; }
            if (OperatingSystem.IsLinux())
            {
                using var p = Process.Start(new ProcessStartInfo("gsettings", "get org.gnome.desktop.interface enable-animations") { RedirectStandardOutput = true, UseShellExecute = false });
                var o = p.StandardOutput.ReadToEnd(); p.WaitForExit(1000);
                return o.Trim() == "false";
            }
        }
        catch (Exception) { /* no gsettings: animations as usual */ }
        return false;
    }
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint param, ref bool value, uint ini);
}

public sealed partial class MainWindow : Window
{
    readonly string _game, _appDir, _version;
    readonly bool _own;   // _game is the player's own game folder (DataDir.OwnGame): nothing to install, never removed
    readonly List<string> _inputs = new();
    List<ModEntry> _mods = new();
    Process _setup, _play;
    int _step = -1; double _progress; bool _running, _playing; string _edition, _warning, _error, _report, _runError;
    string _hp2Error, _hp2RunError, _launcherError;   // per game (GameScope): HP1's in _error, HP2's, the launcher's own (update)
    string[] _runLogsBefore, _hp2RunLogsBefore;   // each game's run journals before its last run started: "Open log" opens the one that run wrote
    bool _modsOpen;   // the Mods page in place of the main page (HP1)
    Release _newer;
    static readonly HttpClient Http = new();

    readonly TextBlock _title = new() { Name = "Title" }, _status = new() { Name = "Status", VerticalAlignment = VerticalAlignment.Center },
        _heroSub = new() { Name = "HeroSub", FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) },
        _metaExtra = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center },
        _editionText = new() { Name = "EditionText", FontSize = 12, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 420 },
        _editionDetail = new() { Name = "EditionDetail", FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new Thickness(0, 6, 0, 0) },
        _editionChip = new() { Name = "EditionChip", FontSize = 13, VerticalAlignment = VerticalAlignment.Center },
        _warningText = new() { Name = "WarningText", VerticalAlignment = VerticalAlignment.Center },
        _errorText = new() { Name = "ErrorText", VerticalAlignment = VerticalAlignment.Center };
    readonly Glyph _statusIcon = new() { Width = 16, Height = 16, StrokeThickness = 1.8, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
    readonly Border _chipBox = new() { CornerRadius = new CornerRadius(99), Padding = new Thickness(8, 2), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel _hero = new() { Name = "Hero", Margin = new Thickness(24, 20, 24, 0) };
    readonly StackPanel _setupPanel = new() { Name = "SetupPanel", Spacing = 12, Margin = new Thickness(24, 20, 24, 0) },
        _inputsList = new() { Name = "InputsList", Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center },
        _readyPanel = new() { Name = "ReadyPanel", Spacing = 12, Margin = new Thickness(24, 24, 24, 0) }, _modsList = new() { Name = "ModsList", Spacing = 4 },
        _modsPage = new() { Name = "ModsPage", Spacing = 12, Margin = new Thickness(24, 16, 24, 0), IsVisible = false },
        _stepsList = new() { Spacing = 0 }, _actions = new() { Name = "Actions", Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(24, 20, 24, 0) };
    readonly Border _stepsCard;
    readonly TextBlock _percent = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right },
        _resumeNote = new() { Text = "You can close the window: the copy resumes next time.", FontSize = 12, Margin = new Thickness(24, 10, 24, 0) };
    readonly ProgressBar _bar = new() { Name = "ProgressBar", Maximum = 1, Height = 6, MinHeight = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 10, 0, 0) };
    readonly StackPanel _dropIdle = new() { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
    readonly Border _warningBanner, _errorBanner;
    readonly Button _play2 = new() { Name = "PlayButton" }, _install = new() { Name = "InstallButton" },
        _choose = new() { Name = "ChooseButton" }, _chooseDir = new() { Name = "ChooseFolderButton", Content = "Choose folder…" }, _clear = new() { Name = "ClearButton", Content = "Clear" };
    // "Start map" (Logic.cs StartMap): shown only while an enabled mod adds a map; never saved.
    readonly ComboBox _startMap = new() { Name = "StartMap", MinWidth = 160, MaxWidth = 260, MaxDropDownHeight = 320, VerticalAlignment = VerticalAlignment.Center };
    readonly ComboBox _language = new() { Name = "GameLanguage", MinWidth = 180, MaxDropDownHeight = 240, VerticalAlignment = VerticalAlignment.Center };
    readonly Grid _languageRow = new() { Name = "GameLanguageRow", ColumnDefinitions = new ColumnDefinitions("*,Auto"), Height = 52 };
    List<HPEngine.Packaging.LanguageChoice> _languages = new();
    bool _loadingLanguage;
    readonly StackPanel _startMapBox = new() { Name = "StartMapBox", Orientation = Orientation.Horizontal, Spacing = 8, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
    List<(string Mod, string Map)> _addedMaps = new();
    string SelectedMap => _startMap.SelectedIndex > 0 && _startMap.SelectedIndex <= _addedMaps.Count ? _addedMaps[_startMap.SelectedIndex - 1].Map : null;
    readonly TextBlock _installText = new() { Text = "Install", TextAlignment = TextAlignment.Center };
    readonly Border _updateBanner = new() { Name = "UpdateBanner", IsVisible = false, Margin = new Thickness(16, 12, 16, 0), Padding = new Thickness(14, 4, 4, 4), CornerRadius = new CornerRadius(10),
        Background = new SolidColorBrush(Colors.Black, 0.30), BorderBrush = Th.Stroke, BorderThickness = new Thickness(1) };
    readonly TextBlock _updateText = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
    // An unrecognised release: HP1Setup's REPORT link (UEEdition.ReportUrl), opened from the warning banner.
    readonly Button _reportButton = new() { Name = "ReportButton", Content = "Report…", IsVisible = false };
    readonly Button _openLog = new() { Name = "OpenLogButton", Content = "Open log", IsVisible = false };
    readonly Button _dismiss = new() { Name = "DismissButton", IsVisible = false };   // the error banner's ×
    readonly Button _updateOpen = new() { Content = "What's new" }, _updateInstall = new() { Name = "UpdateButton", Content = "Update" };
    bool _updating;
    readonly Button _menuButton = new() { Name = "MenuButton" }, _modsRow = new() { Name = "ModsRow" }, _modsBack = new() { Name = "ModsBack" };
    readonly TextBlock _modsValue = new() { Name = "ModsSummary", FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    readonly Border _modsAlert = new() { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
    readonly Border _stripe1 = new() { Height = 4, Opacity = 0.85 }, _stripe2 = new() { Height = 4, Opacity = 0.85 };
    readonly Rectangle _dropRect = new() { RadiusX = 14, RadiusY = 14, StrokeThickness = 2, StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 3 }, Fill = new SolidColorBrush(Colors.Black, 0.18) };
    readonly TextBlock _versionText = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel _padHints = new() { Name = "PadHints", Orientation = Orientation.Horizontal, Spacing = 14, IsVisible = false };
    Control _primary;   // where focus goes when it has nowhere else (start, page change): the screen's gold button
    bool _padUsed;

    public MainWindow(string gameDir, string appDir, string version, bool checkUpdates)
    {
        _own = DataDir.OwnGame(appDir, gameDir);   // the Launcher was unpacked into the player's own game folder: that game, not the data directory
        _game = _own ? appDir : gameDir; _appDir = appDir; _version = version;
        Title = "HPEngine Launcher"; Width = 600; Height = 560; CanResize = false;
        Th.Use(false);
        Background = new SolidColorBrush(Color.Parse("#14050A")); Foreground = Th.Text; FontSize = 14;
        AddStyles();
        _edition = Edition();
        try { Icon = ShieldIcon.Make(); } catch (Exception) { /* no renderer for the bitmap: the default icon stays */ }

        _title.FontSize = 32; _title.FontWeight = FontWeight.Bold; _title.Foreground = Th.Title; _title.FontFamily = Th.Serif; _title.TextWrapping = TextWrapping.Wrap; _title.LineHeight = 35;
        foreach (var t in new[] { _heroSub, _metaExtra, _editionDetail, _editionChip, _percent, _resumeNote, _versionText, _modsValue }) t.Foreground = Th.Dim;
        _chipBox.Child = _editionText; _chipBox.BorderBrush = Th.Stroke;
        _hero.Children.Add(_title);
        _hero.Children.Add(_heroSub);
        _hero.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 8, 0, 0), Children = {
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _statusIcon, _status } }, _chipBox, _editionChip, _metaExtra } });
        _hero.Children.Add(_editionDetail);
        _versionText.Text = "HPEngine " + version;

        _updateOpen.Classes.Add("flat");
        _updateOpen.Click += (_, _) => { if (_newer != null) Open(_newer.Page); };
        _updateInstall.Classes.Add("quiet"); _updateInstall.BorderBrush = Th.Accent;
        _updateInstall.Click += (_, _) => _ = InstallUpdate();
        _updateBanner.Child = new DockPanel { Children = { Dock(_updateInstall, Avalonia.Controls.Dock.Right), Dock(_updateOpen, Avalonia.Controls.Dock.Right),
            Dock(Icon18(Spark, Th.Accent), Avalonia.Controls.Dock.Left), _updateText } };
        _updateText.Margin = new Thickness(10, 0, 0, 0);

        _stepsCard = Well(_stepsList, _bar, _percent);
        _reportButton.Classes.Add("quiet");
        _reportButton.Click += (_, _) => { if (_report != null) Open(_report); };
        _openLog.Classes.Add("quiet");
        _openLog.Click += (_, _) => { var f = LogToOpen(); if (f == GameScope.LogsDir(_game, _hp2Tab)) OpenFolder(f); else Open(f); };
        _dismiss.Classes.Add("flat"); _dismiss.Width = 32; _dismiss.Height = 32; _dismiss.Padding = new Thickness(0); _dismiss.VerticalAlignment = VerticalAlignment.Top;
        var cross = Glyph24(Cross, Th.Dim, 14); cross.HorizontalAlignment = HorizontalAlignment.Center; _dismiss.Content = cross;
        Avalonia.Automation.AutomationProperties.SetName(_dismiss, "Dismiss"); ToolTip.SetTip(_dismiss, "Dismiss");
        _dismiss.Click += (_, _) => DismissError();
        _warningBanner = MakeBanner(Th.Warn, Alert, _warningText, _reportButton);
        _errorBanner = MakeBanner(Th.Err, Stop, _errorText, _openLog, _dismiss);
        BuildSetupPanel();
        BuildReadyPanel();
        BuildModsPage();
        _language.SelectionChanged += (_, _) =>
        {
            if (_loadingLanguage || _playing || _running || _language.SelectedIndex < 0) return;
            try
            {
                HPEngine.Packaging.GameLanguage.Save(_game, _language.SelectedIndex == 0 ? null : _languages[_language.SelectedIndex - 1].Code);
                _error = null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { _error = "Could not change game language: " + e.Message; }
            Refresh();
        };

        // a drop-down list fits the window: from its box down to the footer (it scrolls when longer)
        foreach (var c in new[] { _startMap, _language })
            c.DropDownOpened += (_, _) => { if (c.TranslatePoint(new Point(0, c.Bounds.Height), this) is { } p) c.MaxDropDownHeight = Math.Max(3 * 36 + 8, Height - 52 - p.Y - 8); };
        _play2.Classes.Add("gold"); _install.Classes.Add("gold");
        _play2.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, Children = {
            _playSpin, new Glyph { Data = Geometry.Parse("M0,0 L11,7 L0,14 Z"), Fill = Th.OnAccent, VerticalAlignment = VerticalAlignment.Center }, _playText } };
        _install.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, Children = { _installSpin, _installText } };
        _play2.Click += (_, _) => Play();
        _install.Click += (_, _) => Install();
        _startMapBox.Children.Add(new TextBlock { Text = StartMap.Title, FontSize = 13, Foreground = Th.Dim, VerticalAlignment = VerticalAlignment.Center });
        _startMapBox.Children.Add(_startMap);
        _actions.Children.Add(_play2); _actions.Children.Add(_install); _actions.Children.Add(_startMapBox);

        _menuButton.Classes.Add("quiet");
        _menuButton.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Icon18(MenuLines, Th.Text), new TextBlock { Text = "Menu", VerticalAlignment = VerticalAlignment.Center } } };
        _menuButton.Flyout = BuildMenu();
        // pressed while its Menu is open, as Launcher.swift
        _menuButton.Flyout.Opened += (_, _) => _menuButton.Classes.Set("held", true);
        _menuButton.Flyout.Closed += (_, _) => _menuButton.Classes.Set("held", false);
        var switcher = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 4 };
        var tabs = BuildTabs(); Grid.SetColumn(tabs[1], 1); switcher.Children.Add(tabs[0]); switcher.Children.Add(tabs[1]);
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Margin = new Thickness(16, 12, 16, 0) };
        top.Children.Add(new Border { Background = new SolidColorBrush(Colors.Black, 0.28), CornerRadius = new CornerRadius(12), Padding = new Thickness(4), Child = switcher });
        Grid.SetColumn(_menuButton, 1); top.Children.Add(_menuButton);

        foreach (var (k, txt) in new[] { ("A", "Select"), ("B", "Back"), ("LB RB", "Game"), ("☰", "Menu") })
        {
            var keys = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
            foreach (var key in k.Split(' '))
                keys.Children.Add(new Border { MinWidth = 22, Height = 22, CornerRadius = new CornerRadius(11), Padding = new Thickness(5, 0), Background = Th.Surface2, BorderBrush = Th.Stroke, BorderThickness = new Thickness(1),
                    Child = new TextBlock { Text = key, FontSize = 11, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
            _padHints.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { keys, new TextBlock { Text = txt, FontSize = 12, Foreground = Th.Dim, VerticalAlignment = VerticalAlignment.Center } } });
        }
        var footer = new DockPanel { Margin = new Thickness(24, 10, 24, 12), Children = { Dock(_padHints, Avalonia.Controls.Dock.Right), _versionText } };

        var page = new StackPanel { Children = { _hero, _setupPanel, _actions, _resumeNote, _readyPanel, _modsPage, _hp2Panel, _warningBanner, _errorBanner }, Margin = new Thickness(0, 0, 0, 16) };
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto") };
        Grid.SetRow(_updateBanner, 1); var scroll = new ScrollViewer { Content = page }; Grid.SetRow(scroll, 2);
        Grid.SetRow(_stripe1, 3); Grid.SetRow(_stripe2, 3); Grid.SetRow(footer, 4);
        foreach (var c in new Control[] { top, _updateBanner, scroll, _stripe1, _stripe2, footer }) layout.Children.Add(c);
        // HP1: harlequin red / blue / gold; HP2: snake green (proposal §4)
        _stripe1.Background = new LinearGradientBrush { SpreadMethod = GradientSpreadMethod.Repeat, StartPoint = new RelativePoint(0, 0, RelativeUnit.Absolute), EndPoint = new RelativePoint(12.7, 12.7, RelativeUnit.Absolute),
            GradientStops = { new(Color.Parse("#C8323C"), 0), new(Color.Parse("#C8323C"), 8 / 18.0), new(Color.Parse("#2B4FA3"), 8 / 18.0), new(Color.Parse("#2B4FA3"), 16 / 18.0), new(Color.Parse("#E2AE4A"), 16 / 18.0), new(Color.Parse("#E2AE4A"), 1) } };
        _stripe2.Background = new LinearGradientBrush { SpreadMethod = GradientSpreadMethod.Repeat, StartPoint = new RelativePoint(0, 0, RelativeUnit.Absolute), EndPoint = new RelativePoint(12, 0, RelativeUnit.Absolute),
            GradientStops = { new(Color.Parse("#3F6B3B"), 0), new(Color.Parse("#3F6B3B"), 10 / 12.0), new(Color.Parse("#2C4E29"), 10 / 12.0), new(Color.Parse("#2C4E29"), 1) } };
        Content = new Panel { Children = {
            new Border { Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative), GradientStops = { Th.Top, Th.Mid, Th.Bottom } } },
            new Border { Background = new RadialGradientBrush { Center = new RelativePoint(1, 0, RelativeUnit.Relative), GradientOrigin = new RelativePoint(1, 0, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(1.2, RelativeUnit.Relative), RadiusY = new RelativeScalar(0.8, RelativeUnit.Relative), GradientStops = { Th.Glow, Th.GlowEnd } } },
            new Border { Background = new RadialGradientBrush { Center = new RelativePoint(0, 1, RelativeUnit.Relative), GradientOrigin = new RelativePoint(0, 1, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.9, RelativeUnit.Relative), RadiusY = new RelativeScalar(0.6, RelativeUnit.Relative), GradientStops = { Th.Glow2, Th.Glow2End } } },
            layout } };

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            foreach (var f in e.DataTransfer.TryGetFiles() ?? Array.Empty<IStorageItem>())
                if (f.TryGetLocalPath() is { } p) Add(p);
        });
        Closing += (_, _) => { if (_setup is { HasExited: false }) try { _setup.Kill(true); } catch { } };   // HP1Setup resumes an interrupted copy
        Opened += (_, _) => KeepFocus();
        Refresh();
        if (checkUpdates) _ = CheckForUpdate(false);
    }

    static Control Dock(Control c, Dock d) { DockPanel.SetDock(c, d); return c; }
    bool Installed => _own || File.Exists(Path.Combine(_game, ".hp1-installed"));   // HP1Setup writes it last
    bool Setup => !Installed || _running || _inputs.Count > 0;
    string Edition() => EditionOf(_hp2Tab ? Hp2Game : _game);   // the selected tab's game
    static string EditionOf(string root) => !File.Exists(Path.Combine(root, ".hp1-installed")) ? null : File.ReadLines(Path.Combine(root, ".hp1-installed")).FirstOrDefault(l => l.StartsWith("edition="))?[8..];
    void SetupError(string e) { if (_hp2Tab) _hp2Error = e; else _error = e; }   // the setup's errors: the selected tab's game
    // The engine and HP1Setup live in the HPEngine/ folder of the archive, beside the Launcher (make_launcher.sh); no other layout is built.
    string Exe(string n) => Path.Combine(_appDir, "HPEngine", OperatingSystem.IsWindows() ? n + ".exe" : n);
    string LogFile(bool hp2) => GameScope.ConsoleLog(_own ? Path.Combine(_game, "HPEngineData", "HPEngine.log") : Path.Combine(Path.GetDirectoryName(_game), "HPEngine.log"), hp2);

    // ---- look ----
    // 24-unit stroke icons (mockup.html symbols)
    const string Check = "M5,12.5 L9.5,17 L19,7.5", Spark = "M12,3 V7 M12,17 V21 M3,12 H7 M17,12 H21 M6,6 L8.5,8.5 M15.5,15.5 L18,18 M6,18 L8.5,15.5 M15.5,8.5 L18,6",
        Alert = "M12,3 L22,21 H2 Z M12,10 V15 M12,18 V18.01", Stop = "M8,3 H16 L21,8 V16 L16,21 H8 L3,16 V8 Z M9,9 L15,15 M15,9 L9,15", Cross = "M6,6 L18,18 M18,6 L6,18", MenuLines = "M4,7 H20 M4,12 H20 M4,17 H20", Chev = "M9,6 L15,12 L9,18", BackChev = "M15,6 L9,12 L15,18",
        Folder = "M3,7 A2,2 0 0 1 5,5 H9 L11,7 H19 A2,2 0 0 1 21,9 V17 A2,2 0 0 1 19,19 H5 A2,2 0 0 1 3,17 Z", Disc = "M3,12 A9,9 0 1 0 21,12 A9,9 0 1 0 3,12 M9.5,12 A2.5,2.5 0 1 0 14.5,12 A2.5,2.5 0 1 0 9.5,12",
        Ring = "M5,12 A7,7 0 1 0 19,12 A7,7 0 1 0 5,12";
    static Glyph Glyph24(string path, IBrush color, double size) => new() { Data = Geometry.Parse("M0,0 M24,24 " + path), Stroke = color, StrokeThickness = 1.8, Width = size, Height = size, Stretch = Stretch.Uniform,
        StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, VerticalAlignment = VerticalAlignment.Center };
    static Glyph Icon18(string path, IBrush color) => Glyph24(path, color, 18);

    // Button looks (proposal §5): "gold" (primary: accent fill, 52 high, radius 10, one per screen), "quiet" (secondary:
    // surface + border, 44 high), "flat" (no box, 44 high), "row" (a 52-high list row), "tab" (game switcher segment, 44
    // high; "on" = selected). Fluent restyles the template's presenter on hover, so the states are set there.
    void AddStyles()
    {
        Resources["ToggleSwitchFillOn"] = Th.Accent; Resources["ToggleSwitchFillOnPointerOver"] = Th.AccentHover; Resources["ToggleSwitchFillOnPressed"] = Th.AccentPress;
        Resources["ToggleSwitchKnobFillOn"] = Th.OnAccent; Resources["ToggleSwitchKnobFillOnPointerOver"] = Th.OnAccent; Resources["ToggleSwitchKnobFillOnPressed"] = Th.OnAccent;
        foreach (var k in new[] { "CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckBackgroundFillCheckedPointerOver", "CheckBoxCheckBackgroundFillCheckedPressed",
            "CheckBoxCheckBackgroundStrokeChecked", "CheckBoxCheckBackgroundStrokeCheckedPointerOver", "CheckBoxCheckBackgroundStrokeCheckedPressed" }) Resources[k] = Th.Accent;
        foreach (var k in new[] { "CheckBoxCheckGlyphForegroundChecked", "CheckBoxCheckGlyphForegroundCheckedPointerOver", "CheckBoxCheckGlyphForegroundCheckedPressed" }) Resources[k] = Th.OnAccent;
        Resources["ProgressBarForeground"] = Th.Accent; Resources["ProgressBarBackground"] = new SolidColorBrush(Colors.White, 0.12);
        Resources["MenuFlyoutPresenterBackground"] = Th.Popup; Resources["MenuFlyoutPresenterBorderBrush"] = new SolidColorBrush(Colors.White, 0.14);
        Resources["ComboBoxBackground"] = new SolidColorBrush(Colors.Black, 0.25); Resources["ComboBoxBorderBrush"] = Th.Stroke; Resources["ComboBoxDropDownBackground"] = Th.Popup;
        void Look(string cls, IBrush bg, IBrush hover, IBrush pressed, IBrush fg, IBrush border, double height, double radius, Thickness pad, FontWeight weight, double size, IBrush offBg = null, IBrush offFg = null)
        {
            Style Pres(string state, IBrush b, IBrush f, IBrush edge = null)
            {
                var s = new Style(x => state == null ? x.OfType<Button>().Class(cls).Template().OfType<ContentPresenter>() : x.OfType<Button>().Class(cls).Class(state).Template().OfType<ContentPresenter>());
                s.Setters.Add(new Setter(ContentPresenter.BackgroundProperty, b)); s.Setters.Add(new Setter(ContentPresenter.ForegroundProperty, f));
                s.Setters.Add(new Setter(ContentPresenter.BorderBrushProperty, edge ?? border));
                return s;
            }
            Styles.Add(new Style(x => x.OfType<Button>().Class(cls)) { Setters = { new Setter(Button.MinHeightProperty, height), new Setter(Button.CornerRadiusProperty, new CornerRadius(radius)),
                new Setter(Button.PaddingProperty, pad), new Setter(Button.FontWeightProperty, weight), new Setter(Button.FontSizeProperty, size), new Setter(Button.BackgroundProperty, bg), new Setter(Button.ForegroundProperty, fg),
                new Setter(Button.BorderBrushProperty, border), new Setter(Button.BorderThicknessProperty, new Thickness(border == Brushes.Transparent ? 0 : 1)),
                new Setter(Button.VerticalContentAlignmentProperty, VerticalAlignment.Center), new Setter(Button.VerticalAlignmentProperty, VerticalAlignment.Center) } });
            // keyboard / pad focus looks like hover (plus the ring); a framed button's border brightens on both
            var hotEdge = border == Brushes.Transparent ? null : Th.Hot;
            Styles.Add(Pres(null, bg, fg)); Styles.Add(Pres(":focus-visible", hover, fg, hotEdge)); Styles.Add(Pres(":pointerover", hover, fg, hotEdge)); Styles.Add(Pres(":pressed", pressed, fg, hotEdge));
            if (offBg == null) Styles.Add(new Style(x => x.OfType<Button>().Class(cls).Class(":disabled")) { Setters = { new Setter(OpacityProperty, 0.45) } });
            Styles.Add(Pres(":disabled", offBg ?? bg, offFg ?? fg));
        }
        Look("gold", Th.Accent, Th.AccentHover, Th.AccentPress, Th.OnAccent, Brushes.Transparent, 52, 10, new Thickness(28, 0), FontWeight.SemiBold, 17, Th.Surface2, Th.Dim);
        Look("quiet", Th.Surface, Th.Surface2, new SolidColorBrush(Colors.Black, 0.40), Th.Text, Th.Stroke, 44, 10, new Thickness(18, 0), FontWeight.SemiBold, 14);
        Styles.Add(new Style(x => x.OfType<Button>().Class("quiet").Class("held").Template().OfType<ContentPresenter>()) { Setters = { new Setter(ContentPresenter.BackgroundProperty, Th.Surface2) } });
        Look("flat", Brushes.Transparent, Th.Surface, Th.Surface2, Th.Dim, Brushes.Transparent, 44, 10, new Thickness(12, 0), FontWeight.SemiBold, 14);
        Look("row", Brushes.Transparent, Th.Surface, Th.Surface2, Th.Text, Brushes.Transparent, 52, 0, new Thickness(16, 0), FontWeight.Normal, 14);
        Look("tab", Brushes.Transparent, Th.Surface, Th.Surface2, Th.Dim, Brushes.Transparent, 44, 9, new Thickness(8, 0), FontWeight.Medium, 14);
        Styles.Add(new Style(x => x.OfType<Button>().Class("gold")) { Setters = { new Setter(MinWidthProperty, 168.0), new Setter(Button.HorizontalContentAlignmentProperty, HorizontalAlignment.Center) } });
        Styles.Add(new Style(x => x.OfType<Button>().Class("row")) { Setters = { new Setter(Button.HorizontalAlignmentProperty, HorizontalAlignment.Stretch), new Setter(Button.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
        Styles.Add(new Style(x => x.OfType<Button>().Class("tab")) { Setters = { new Setter(Button.HorizontalAlignmentProperty, HorizontalAlignment.Stretch), new Setter(Button.HorizontalContentAlignmentProperty, HorizontalAlignment.Center) } });
        Styles.Add(new Style(x => x.OfType<Button>().Class("tab").Class("on").Template().OfType<ContentPresenter>()) { Setters = {
            new Setter(ContentPresenter.BackgroundProperty, Th.Surface2), new Setter(ContentPresenter.ForegroundProperty, Th.Text), new Setter(ContentPresenter.BorderBrushProperty, Th.Stroke),
            new Setter(ContentPresenter.BorderThicknessProperty, new Thickness(1)) } });
        Styles.Add(new Style(x => x.OfType<Button>().Class("tab").Class("on")) { Setters = { new Setter(Button.FontWeightProperty, FontWeight.SemiBold) } });
        Styles.Add(new Style(x => x.OfType<ComboBox>()) { Setters = { new Setter(MinHeightProperty, 44.0), new Setter(ComboBox.CornerRadiusProperty, new CornerRadius(10)) } });
        Styles.Add(new Style(x => x.OfType<MenuItem>()) { Setters = { new Setter(MinHeightProperty, 40.0) } });
        Interaction();
        // Focus ring (proposal §6): 3 px in the title colour, 3 px outside the control, whenever focus came by keyboard or pad.
        Styles.Add(new Style(x => x.Is<Control>()) { Setters = { new Setter(FocusAdornerProperty, new FuncTemplate<Control>(() =>
        {
            var ring = new Border { BorderBrush = Th.Title, BorderThickness = new Thickness(3), CornerRadius = new CornerRadius(13), Margin = new Thickness(-6), IsHitTestVisible = false };
            ring.SetValue(AdornerLayer.IsClipEnabledProperty, false);   // the ring sits outside the control's bounds
            return ring;
        })) } });
    }

    // The interaction layer of every control (proposal §5; the same as Launcher.swift's Interactive): hover brightens, a
    // press darkens and scales to 0.97, the primary lifts 1 px, the pointing hand over what can be clicked, list items lit
    // in the title gold; a list grows out of its control (160 ms) and a new banner slides in (200 ms). With reduce motion
    // (Motion) only the colours change.
    void Interaction()
    {
        var hand = new Cursor(StandardCursorType.Hand);
        foreach (var t in new[] { typeof(Button), typeof(MenuItem), typeof(ComboBox), typeof(ComboBoxItem), typeof(ToggleSwitch) })
            Styles.Add(new Style(x => x.Is(t).Not(y => y.Class(":disabled"))) { Setters = { new Setter(CursorProperty, hand) } });
        Styles.Add(new Style(x => x.OfType<Button>().Class("gold").Class(":pointerover")) { Setters = { new Setter(RenderTransformProperty, TransformOperations.Parse(Motion.Reduced ? "none" : "translateY(-1px)")) } });   // the primary lifts
        Styles.Add(new Style(x => x.OfType<Button>().Class("gold").Class(":pointerover")) { Setters = { new Setter(RenderTransformProperty, TransformOperations.Parse(Motion.Reduced ? "none" : "translateY(-1px)")) } });   // the primary lifts
        Styles.Add(new Style(x => x.OfType<Button>().Class(":pressed")) { Setters = { new Setter(RenderTransformProperty, TransformOperations.Parse(Motion.Reduced ? "none" : "scale(0.97)")) } });
        Styles.Add(new Style(x => x.OfType<Button>().Class("row").Class(":pressed")) { Setters = { new Setter(RenderTransformProperty, TransformOperations.Parse("none")) } });   // a full-width row: the fill says it
        // the gold primary: a soft shadow that grows on hover
        Styles.Add(new Style(x => x.OfType<Button>().Class("gold").Template().OfType<ContentPresenter>()) { Setters = { new Setter(ContentPresenter.BoxShadowProperty, BoxShadows.Parse("0 6 18 0 #59000000")) } });
        Styles.Add(new Style(x => x.OfType<Button>().Class("gold").Class(":pointerover").Template().OfType<ContentPresenter>()) { Setters = { new Setter(ContentPresenter.BoxShadowProperty, BoxShadows.Parse("0 8 24 0 #73000000")) } });
        Styles.Add(new Style(x => x.OfType<Button>().Class("gold").Class(":disabled").Template().OfType<ContentPresenter>()) { Setters = { new Setter(ContentPresenter.BoxShadowProperty, BoxShadows.Parse("0 0 0 0 #00000000")) } });
        // list items (Menu, Language, Start map) and the drop-down boxes themselves
        foreach (var k in new[] { "MenuFlyoutItemBackgroundPointerOver", "ComboBoxItemBackgroundPointerOver", "ComboBoxItemBackgroundSelectedPointerOver" }) Resources[k] = Th.ItemHover;
        foreach (var k in new[] { "MenuFlyoutItemBackgroundPressed", "ComboBoxItemBackgroundPressed", "ComboBoxItemBackgroundSelectedPressed", "ComboBoxItemBackgroundSelected" }) Resources[k] = Th.ItemPress;
        foreach (var k in new[] { "MenuFlyoutItemForegroundPointerOver", "MenuFlyoutItemForegroundPressed", "ComboBoxItemForegroundPointerOver", "ComboBoxItemForegroundPressed",
            "ComboBoxItemForegroundSelected", "ComboBoxItemForegroundSelectedPointerOver", "ComboBoxItemForegroundSelectedPressed" }) Resources[k] = Th.Text;
        Resources["ComboBoxBackgroundPointerOver"] = Th.Surface2; Resources["ComboBoxBackgroundPressed"] = new SolidColorBrush(Colors.Black, 0.40);
        Resources["ComboBoxBorderBrushPointerOver"] = Th.Hot; Resources["ComboBoxBorderBrushPressed"] = Th.Hot;
        Styles.Add(new Style(x => x.OfType<ComboBox>().Class(":focus-visible")) { Setters = { new Setter(BackgroundProperty, Th.Surface2), new Setter(BorderBrushProperty, Th.Hot) } });
        // a long drop-down (Start map, Language) scrolls within MaxDropDownHeight; its scroll bar always shows; typing jumps
        Styles.Add(new Style(x => x.OfType<ComboBox>().Template().OfType<ScrollViewer>()) { Setters = { new Setter(ScrollViewer.AllowAutoHideProperty, false) } });
        Styles.Add(new Style(x => x.OfType<ComboBox>()) { Setters = { new Setter(ComboBox.IsTextSearchEnabledProperty, true) } });
        Styles.Add(new Style(x => x.OfType<ComboBoxItem>()) { Setters = { new Setter(MinHeightProperty, 36.0), new Setter(VerticalContentAlignmentProperty, VerticalAlignment.Center) } });
        Styles.Add(new Style(x => x.OfType<ComboBoxItem>().Class(":selected")) { Setters = { new Setter(FontWeightProperty, FontWeight.SemiBold) } });
        // Colours switch at once: a BrushTransition alone was seen to stall part way (Avalonia 11.3, headless), which would leave
        // a half-lit state; the movement (press 0.97, the primary's 1 px lift) is the animated part.
        if (!_handlers) TemplateAppliedEvent.AddClassHandler<Button>((b, _) =>   // a class handler: once per process
        {
            if (!Motion.Reduced) b.Transitions = new Transitions { new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(120), Easing = new CubicEaseOut() } };
        });
        _handlers = true;
        if (Motion.Reduced) return;
        Styles.Add(new Style(x => x.OfType<Glyph>().Class("spin").PropertyEquals(IsVisibleProperty, true)) { Animations = { new Animation { Duration = TimeSpan.FromMilliseconds(900), IterationCount = IterationCount.Infinite, Children = {
            new KeyFrame { Cue = new Cue(0), Setters = { Turn("rotate(0deg)") } },
            new KeyFrame { Cue = new Cue(1), Setters = { Turn("rotate(360deg)") } } } } } });
        // entrances: a list from its control (the Menu from its right edge, a drop-down from its top), a banner from above
        Animation Enter(double ms, string from) => new() { Duration = TimeSpan.FromMilliseconds(ms), Easing = new CubicEaseOut(), FillMode = FillMode.Forward, Children = {
            new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0.0), Turn(from) } },
            new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1.0), Turn("none") } } } };
        Styles.Add(new Style(x => x.OfType<MenuFlyoutPresenter>()) { Setters = { new Setter(RenderTransformOriginProperty, new RelativePoint(1, 0, RelativeUnit.Relative)) }, Animations = { Enter(160, "translateY(-6px) scale(0.94)") } });
        Styles.Add(new Style(x => x.OfType<ComboBox>().Class(":dropdownopen").Template().OfType<Border>().Name("PopupBorder")) { Setters = { new Setter(RenderTransformOriginProperty, new RelativePoint(0.5, 0, RelativeUnit.Relative)) },
            Animations = { Enter(160, "translateY(-6px) scale(0.94)") } });
        Styles.Add(new Style(x => x.OfType<Border>().Class("banner").PropertyEquals(IsVisibleProperty, true)) { Animations = { Enter(200, "translateY(-6px)") } });
    }

    static bool _handlers;
    // A keyframe setter of RenderTransform: code-built animations need its animator named (XAML infers it).
    static Setter Turn(string ops) { var t = new Setter(RenderTransformProperty, TransformOperations.Parse(ops)); Animation.SetAnimator(t, Lerp); return t; }
    static readonly TransformLerp Lerp = new();
    sealed class TransformLerp : InterpolatingAnimator<ITransform>
    {
        public override ITransform Interpolate(double p, ITransform from, ITransform to) => TransformOperations.Interpolate((TransformOperations)from, (TransformOperations)to, p);
    }

    // The busy mark of Install / Play (a turning arc; with reduce motion it stands still), shown while the setup runs or the game plays.
    static Glyph Spinner()
    {
        var g = Glyph24("M12,3 A9,9 0 1 1 3,12", Th.Dim, 16); g.StrokeThickness = 2.2; g.IsVisible = false;
        g.Classes.Add("spin");
        return g;
    }
    readonly Glyph _installSpin = Spinner(), _playSpin = Spinner();
    readonly TextBlock _playText = new() { Text = "Play" };

    // A recessed card: black 22 %, stroke, radius 12.
    static Border Well(params Control[] content)
    {
        var s = new StackPanel();
        foreach (var c in content) s.Children.Add(c);
        return new Border { Background = Th.Well, BorderBrush = Th.Stroke, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(16), Child = s };
    }

    // Banner (as the macOS Launcher's Banner, below the page's content): dark well with the state colour as border, icon,
    // the whole text (wrapped) with its button under it, and its × at the top right.
    static Border MakeBanner(IBrush color, string glyph, TextBlock text, Button extra = null, Button close = null)
    {
        text.Foreground = Th.Text; text.FontSize = 13; text.TextWrapping = TextWrapping.Wrap;
        var icon = Icon18(glyph, color); icon.Margin = new Thickness(0, 2, 10, 0); icon.VerticalAlignment = VerticalAlignment.Top;
        var body = new StackPanel { Spacing = 8, Children = { text } };
        if (extra != null) { extra.HorizontalAlignment = HorizontalAlignment.Left; body.Children.Add(extra); }
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 4 };
        g.Children.Add(icon); Grid.SetColumn(body, 1); g.Children.Add(body);
        if (close != null) { close.VerticalAlignment = VerticalAlignment.Top; Grid.SetColumn(close, 2); g.Children.Add(close); }
        var banner = new Border { Background = new SolidColorBrush(Colors.Black, 0.30), BorderBrush = color, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10), Margin = new Thickness(24, 16, 24, 0), Child = g, IsVisible = false };
        banner.Classes.Add("banner");
        return banner;
    }

    void BuildSetupPanel()
    {
        _choose.Content = "Choose files…"; _chooseDir.Classes.Add("quiet"); _clear.Classes.Add("flat");
        _choose.Click += async (_, _) => await PickFiles();
        _chooseDir.Click += async (_, _) => { foreach (var f in await StorageProvider.OpenFolderPickerAsync(new() { AllowMultiple = true, Title = "Installed game folder or the game CD" })) if (f.TryGetLocalPath() is { } p) Add(p); };
        _clear.Click += (_, _) => { _inputs.Clear(); Refresh(); };
        var icon = Glyph24(Disc, Th.Accent, 30); icon.HorizontalAlignment = HorizontalAlignment.Center;
        _dropIdle.Children.Add(icon);
        _dropIdle.Children.Add(new TextBlock { Text = "Drop your copy of the game here", FontSize = 16, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        _dropIdle.Children.Add(new TextBlock { Text = "Installed game folder, CD folder, installer (Setup.exe) or .zip (mount a disc image first)", FontSize = 13, Foreground = Th.Dim, MaxWidth = 400,
            HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0), Children = { _choose, _chooseDir, _clear } };
        var zone = new Grid { MinHeight = 208 };
        zone.Children.Add(_dropRect);
        zone.Children.Add(new StackPanel { Margin = new Thickness(16), Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Children = { _dropIdle, _inputsList, buttons } });
        _setupPanel.Children.Add(zone);
        _setupPanel.Children.Add(_stepsCard);
    }

    async Task PickFiles()
    {
        foreach (var f in await StorageProvider.OpenFilePickerAsync(new() { AllowMultiple = true, Title = "Game installer (Setup.exe, data1.hdr) or .zip" })) if (f.TryGetLocalPath() is { } p) Add(p);
    }

    // The main page's secondary rows (Language only while there is a choice; Mods: a summary that opens the Mods page).
    void BuildReadyPanel()
    {
        _languageRow.Margin = new Thickness(16, 0, 8, 0);
        _languageRow.Children.Add(new TextBlock { Text = "Language", VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_language, 1); _languageRow.Children.Add(_language);
        _modsRow.Classes.Add("row");
        _modsAlert.Background = Th.Warn;
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = 8 };
        row.Children.Add(new TextBlock { Text = "Mods", VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_modsAlert, 1); row.Children.Add(_modsAlert); Grid.SetColumn(_modsValue, 2); row.Children.Add(_modsValue);
        var chev = Glyph24(Chev, Th.Dim, 16); Grid.SetColumn(chev, 3); row.Children.Add(chev);
        _modsRow.Content = row;
        _modsRow.Click += (_, _) => { _modsOpen = true; Refresh(); };
        var rows = new StackPanel { Children = { _languageRow, new Border { Height = 1, Background = Th.Stroke, Name = "RowRule" }, _modsRow } };
        _readyPanel.Children.Add(new Border { Background = Th.Well, BorderBrush = Th.Stroke, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), ClipToBounds = true, Child = rows });
    }

    void BuildModsPage()
    {
        var back = _modsBack; Back(back);
        back.Click += (_, _) => { _modsOpen = false; Refresh(); };
        var openMods = new Button { Name = "OpenModsButton", Content = "Open Mods Folder" }; var refresh = new Button { Name = "RefreshMods", Content = "Refresh" };
        openMods.Classes.Add("quiet"); refresh.Classes.Add("quiet");
        openMods.Click += (_, _) => OpenFolder(Path.Combine(_game, "Mods"));
        refresh.Click += (_, _) => Refresh();
        _modsHint.Foreground = Th.Dim; _noMods.Foreground = Th.Dim; ((TextBlock)_modsNotice.Children[1]).Foreground = Th.Dim;
        ((Border)_modsNotice.Children[0]).BorderBrush = Th.Warn; ((TextBlock)((Border)_modsNotice.Children[0]).Child).Foreground = Th.Warn;
        _modsPage.Children.Add(SubHeader(back, "Mods"));
        _modsPage.Children.Add(Well(_noMods, _modsList));
        _modsPage.Children.Add(_modsNotice);
        _modsPage.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { openMods, refresh } });
        _modsPage.Children.Add(_modsHint);
    }

    // The Back button of a sub-page (quiet, chevron) and its header with the page title in the serif.
    static void Back(Button b)
    {
        b.Classes.Add("quiet");
        b.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Glyph24(BackChev, Th.Text, 16), new TextBlock { Text = "Back", VerticalAlignment = VerticalAlignment.Center } } };
    }
    static Control SubHeader(Button back, string title) => new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { back,
        new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeight.Bold, FontFamily = Th.Serif, Foreground = Th.Title, VerticalAlignment = VerticalAlignment.Center } } };

    // "i" in a ring (warning colour) + the dim hint lines of the Mods page; shown by RebuildMods per ModsNotice.Lines
    readonly StackPanel _modsNotice = new() { Name = "ModsNotice", Orientation = Orientation.Horizontal, Spacing = 8, IsVisible = false, Children = {
        new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1.2), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0),
            Child = new TextBlock { Text = "i", FontSize = 10, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } },
        new TextBlock { Name = "ModsNoticeText", FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 500 } } };
    readonly TextBlock _modsHint = new() { Name = "ModsHint", Text = ModsIni.OrderHint, FontSize = 12, TextWrapping = TextWrapping.Wrap },
        _noMods = new() { Name = "NoMods", Text = "No mods yet. Put a mod's folder into the Mods folder; it shows up here to switch on and off.", FontSize = 13, TextWrapping = TextWrapping.Wrap };

    // The Menu (proposal §2): "This game" (per tab; Remove game its last item, in red), "Launcher".
    readonly List<(MenuItem Item, Func<bool> Visible, Func<bool> Enabled)> _menuItems = new();
    FlyoutBase BuildMenu()
    {
        var fly = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        bool hp1() => !_hp2Tab;
        void Group(string text, Func<bool> visible)
        {
            var mi = new MenuItem { Header = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.6, Foreground = Th.Dim }, Focusable = false, IsHitTestVisible = false, MinHeight = 28 };
            _menuItems.Add((mi, visible, null)); fly.Items.Add(mi);
        }
        MenuItem M(string name, string text, Action a, Func<bool> visible = null, Func<bool> en = null)
        {
            var mi = new MenuItem { Name = name, Header = text }; mi.Click += (_, _) => a();
            _menuItems.Add((mi, visible, en)); fly.Items.Add(mi);
            return mi;
        }
        Group("THIS GAME", null);
        M("OpenSaves", "Open saved games folder", () => OpenFolder(_hp2Tab ? Hp2FrontEnd.SaveDir(_game) : Path.Combine(_game, "Save")), null, () => _hp2Tab ? Hp2Root != null : Installed);
        M("OpenLogs", "Open logs folder", () => OpenFolder(GameScope.LogsDir(_game, _hp2Tab)));
        M("InstallAgain", "Install again from other files…", () => _ = PickFiles(), () => _hp2Tab || !_own, () => !_running && !_playing);
        M("Hp2Folder", "Change game folder…", () => _ = Hp2ChooseFolder(), () => _hp2Tab && !Hp2Installed, () => !_playing && !_running);
        M("ResetSettings", "Reset settings…", () => _ = ResetSettings(), hp1, () => Installed);
        M("ResetPad", "Reset gamepad buttons…", () => _ = ResetPad(), hp1, () => Installed && !_playing && PadReset.Rebound(_game) > 0);
        var rm = M("RemoveGame", "Remove game…", () => _ = RemoveGame(), null, () => (_hp2Tab ? Hp2Installed : !_own && Installed) && !_running && !_playing);
        rm.Foreground = Th.Err;
        fly.Items.Add(new Separator());
        Group("LAUNCHER", null);
        M("CheckUpdates", "Check for updates", () => _ = CheckForUpdate(true));
        if (OperatingSystem.IsLinux()) M("AddToSteam", "Add to Steam…", () => _ = AddToSteam(), null, () => !_running);
        M("About", "About", () => _ = About());
        fly.Opening += (_, _) =>
        {
            foreach (var (mi, v, e) in _menuItems) { mi.IsVisible = v?.Invoke() ?? true; if (e != null) mi.IsEnabled = e(); }
            if (_padUsed) Dispatcher.UIThread.Post(() => _menuItems.FirstOrDefault(x => x.Item.IsVisible && x.Item.IsEnabled && x.Item.Focusable).Item?.Focus(NavigationMethod.Directional));
        };
        return fly;
    }

    // ---- state -> view ----
    void Refresh()
    {
        Th.Use(_hp2Tab); _stripe1.IsVisible = !_hp2Tab; _stripe2.IsVisible = _hp2Tab;
        // a setup runs on the selected tab only (the tabs do not switch while it runs)
        Tab(_tabHp1, !_hp2Tab, Installed ? Th.Ok : _running && !_hp2Tab ? Th.Warn : null, _running && !_hp2Tab ? "installing" : Installed ? null : "not set up");
        Tab(_tabHp2, _hp2Tab, Hp2Root != null ? Th.Ok : _running && _hp2Tab ? Th.Warn : null, _running && _hp2Tab ? "installing" : Hp2Release.Caption(Hp2Release.Available, Hp2Root != null));
        _tabHp2.IsEnabled = Hp2Release.Available; ToolTip.SetTip(_tabHp2, Hp2Release.Tip(Hp2Release.Available)); ToolTip.SetShowOnDisabled(_tabHp2, true);
        if (_hp2Tab) { RefreshHp2(); KeepFocus(); return; }   // the HP2 tab (MainWindow.Hp2.cs); below, the HP1 tab
        _hp2Panel.IsVisible = false; _actions.IsVisible = !_modsOpen;
        var (st, col, icon) = _running ? ("Installing…", Th.Accent, Spark) : _playing ? ("Playing", Th.Ok, Check) : Installed ? ("Ready to play", Th.Ok, Check) : ("Not installed", Th.Dim, Ring);
        Status(st, col, icon); _title.Text = GameTitle.For(_edition);
        _heroSub.Text = "Set up once from your own copy of the game. Nothing of the game is included."; _heroSub.IsVisible = !Installed && !_running;
        _metaExtra.IsVisible = false;
        _hero.IsVisible = !_modsOpen; _modsPage.IsVisible = _modsOpen;
        _setupPanel.IsVisible = Setup && !_modsOpen; _readyPanel.IsVisible = !Setup && !_modsOpen;
        RefreshSetup(Installed, Setup, _own);
        _loadingLanguage = true;
        try
        {
            _languages = Installed ? HPEngine.Packaging.GameLanguage.Available(_game) : new();
            var editionDisplay = EditionLabel.Describe(_edition, _languages);
            if (_languages.Count > 1)
            {
                _editionText.Text = editionDisplay.Name;
                _editionChip.Text = editionDisplay.Tier;
                _editionDetail.Text = editionDisplay.Detail;
            }
            _editionDetail.IsVisible = _languages.Count > 1 && !Setup;
            if (EditionLabel.Quiet(editionDisplay.Tier)) _chipBox.IsVisible = _editionChip.IsVisible = false;
            ToolTip.SetTip(_chipBox, editionDisplay.Tier == "different data"
                ? "These files differ from the engine family's reference manifest. This alone does not establish that your disc was modified."
                : null);
            string selected = HPEngine.Packaging.GameLanguage.Selected(_game);
            var labels = _languages.Select(l => l.Label).Prepend(HPEngine.Packaging.GameLanguage.DefaultLabel).ToList();
            int index = _languages.FindIndex(l => l.Code == selected) + 1;
            if (!string.IsNullOrEmpty(selected) && index == 0) { labels.Add("Unavailable (" + selected + ")"); index = labels.Count - 1; }
            _language.ItemsSource = labels; _language.SelectedIndex = index;
            _languageRow.IsVisible = Installed && labels.Count > 2;   // a choice: two languages, or one and a pick whose folder is gone
            _language.IsEnabled = !_playing && !_running;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { _error = "Could not read game languages: " + e.Message; UpdateBanners(); }
        finally { _loadingLanguage = false; }
        ((Control)((Panel)_languageRow.Parent).Children[1]).IsVisible = _languageRow.IsVisible;
        _mods = Installed ? ModsIni.Load(_game) : new List<ModEntry>();
        RebuildMods();
        KeepFocus();
    }

    // The setup view and the edition chip, for the selected tab's game (HP1: its install root or the player's own folder;
    // HP2: its install root). Both tabs, one look.
    void RefreshSetup(bool installed, bool setup, bool own)
    {
        // "US 1.53: exact" -> name in the chip + the tier (exact: verified, green; supported: gold; others the warning colour with the tier text)
        var parts = (_edition ?? "").Split(": ");
        var tier = parts.Length > 1 ? parts[^1] : "";
        _editionText.Text = _edition == null ? (own ? "Your game folder: " + _game : "Installed") : parts.Length > 1 ? string.Join(": ", parts[..^1]) : _edition;
        var (cc, label) = tier switch { "exact" => (Th.Ok, "verified"), "supported" => (Th.Accent, "supported"), _ => (Th.Warn, tier.Length == 0 ? "unknown" : tier) };
        bool steps = _running || _step >= 0;
        bool quiet = EditionLabel.Quiet(tier);
        _chipBox.IsVisible = (!setup || steps) && (_edition != null ? !quiet : own); _chipBox.Background = _edition != null ? Th.Surface : null; _chipBox.BorderThickness = new Thickness(_edition != null ? 1 : 0);
        _editionText.Foreground = _edition != null ? Th.Text : Th.Dim;
        _editionChip.IsVisible = _edition != null && !quiet && (!setup || steps); _editionChip.Text = label; _editionChip.Foreground = cc;
        UpdateBanners();
        _inputsList.Children.Clear();
        foreach (var i in _inputs) _inputsList.Children.Add(new TextBlock { Text = Path.GetFileName(i.TrimEnd('/', '\\')) is { Length: > 0 } n ? n : i, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center });
        _dropIdle.IsVisible = _inputs.Count == 0;
        _dropRect.Stroke = Th.Stroke;
        _stepsCard.IsVisible = steps; ((Control)_dropRect.Parent).IsVisible = !_running;
        _stepsList.Children.Clear();
        for (int i = 0; i < SetupProtocol.Steps.Length; i++)
        {
            var done = i < _step; var cur = i == _step;
            var mark = Glyph24(done ? Check : cur ? Spark : Ring, done ? Th.Ok : cur ? Th.Accent : Th.Dim, 16);
            _stepsList.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Height = 34, Children = { mark,
                new TextBlock { Text = SetupProtocol.Steps[i].Text, VerticalAlignment = VerticalAlignment.Center, FontWeight = cur ? FontWeight.SemiBold : FontWeight.Normal, Foreground = cur ? Th.Text : Th.Dim } } });
        }
        _bar.IsVisible = _running && _step >= 2; _bar.Value = _progress; _percent.IsVisible = _bar.IsVisible; _percent.Text = $"{Math.Round(_progress * 100)}%";
        bool ready = installed && !_running && _inputs.Count == 0;
        _play2.IsVisible = ready; _play2.IsEnabled = !_playing;
        _playSpin.IsVisible = _playing; ((StackPanel)_play2.Content).Children[1].IsVisible = !_playing; _playText.Text = _playing ? "Playing…" : "Play";
        _installSpin.IsVisible = _running;
        _install.IsVisible = !ready && (_inputs.Count > 0 || _running); _installText.Text = _running ? "Installing…" : installed ? "Install Again" : "Install";
        _install.IsEnabled = _inputs.Count > 0 && !_running && !_playing;
        _resumeNote.IsVisible = _running;
        // one gold button per screen: "Choose files…" while nothing is chosen, then Install
        _choose.Classes.Set("gold", _inputs.Count == 0); _choose.Classes.Set("quiet", _inputs.Count > 0); _choose.Content = _inputs.Count == 0 ? "Choose files…" : "Add files…";
        _clear.IsVisible = _inputs.Count > 0;
        _primary = _modsOpen ? _modsBack : ready ? _play2 : _install.IsVisible ? _install : _choose;
    }

    void Status(string text, IBrush color, string icon)
    {
        _status.Text = text; _statusIcon.Data = Geometry.Parse("M0,0 M24,24 " + icon); _statusIcon.Stroke = color;
    }

    // A game switcher segment: status dot (none: not set up) and a caption ("not set up", "installing") — state not by colour alone.
    static void Tab(Button b, bool on, IBrush dot, string caption)
    {
        b.Classes.Set("on", on);
        var p = (StackPanel)b.Content;
        ((Ellipse)p.Children[0]).IsVisible = dot != null; ((Ellipse)p.Children[0]).Fill = dot;
        ((TextBlock)p.Children[2]).IsVisible = caption != null; ((TextBlock)p.Children[2]).Text = "· " + caption;
    }

    void UpdateBanners()
    {
        _warningText.Text = _warning; _warningBanner.IsVisible = _warning != null; _reportButton.IsVisible = _report != null;   // HP1Setup's: the selected tab's (reset on a tab switch)
        // An unfinished rollback of an earlier update stays on screen while its marker exists (SelfUpdate.IncompleteNotice).
        var error = GameScope.Error(_hp2Tab, _error, _hp2Error, _launcherError) ?? SelfUpdate.IncompleteNotice(_appDir);
        _errorText.Text = error; _errorBanner.IsVisible = error != null;
        _openLog.IsVisible = error != null && ReferenceEquals(error, _hp2Tab ? _hp2RunError : _runError);   // a failed run of this game: its log
        _dismiss.IsVisible = GameScope.Error(_hp2Tab, _error, _hp2Error, _launcherError) != null;   // an unfinished rollback's notice stays
    }

    // Public for the headless check: what the error banner's "Open log" opens for the selected tab's last run (GameScope.LogToOpen).
    public string LogToOpen() => GameScope.LogToOpen(GameScope.LogsDir(_game, _hp2Tab), LogFile(_hp2Tab), _hp2Tab ? _hp2RunLogsBefore : _runLogsBefore);

    // The error banner's × (and pad B / Escape on its buttons): clears the shown error only (GameScope.Dismiss).
    void DismissError()
    {
        (_error, _hp2Error, _launcherError) = GameScope.Dismiss(_hp2Tab, _error, _hp2Error, _launcherError);
        if (_error == null) _runError = null;
        if (_hp2Error == null) _hp2RunError = null;
        UpdateBanners();
    }

    static Button Chevron(string name, string path, bool on)
    {
        var b = new Button { Name = name, IsEnabled = on, Width = 44, Padding = new Thickness(0),
            Content = new Glyph { Data = Geometry.Parse(path), Stroke = on ? Th.Text : Th.Dim, StrokeThickness = 1.6, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, HorizontalAlignment = HorizontalAlignment.Center } };
        b.Classes.Add("flat"); b.Opacity = on ? 1 : 0.4;
        return b;
    }

    void RebuildMods()
    {
        _modsList.Children.Clear();
        var notice = Installed ? ModsNotice.Lines(_game, _mods).Concat(ModsIni.Misplaced(_game, _mods)).Concat(ModsIni.Conflicts(_game)).ToList() : new List<string>();
        _modsNotice.IsVisible = notice.Count > 0;
        ((TextBlock)_modsNotice.Children[1]).Text = string.Join("\n", notice);
        _noMods.IsVisible = _mods.Count == 0; _modsHint.IsVisible = _mods.Any(m => m.Enabled);
        _modsValue.Text = _mods.Count == 0 ? "None" : $"{_mods.Count(m => m.Enabled)} of {_mods.Count} on"; _modsAlert.IsVisible = notice.Count > 0;
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
            var sw = new ToggleSwitch { Name = "Mod_" + m.Name, IsChecked = m.Enabled, OnContent = null, OffContent = null, IsEnabled = !_playing, VerticalAlignment = VerticalAlignment.Center, MinWidth = 0 };
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
            var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            // The apply order of an enabled mod (1 first; a higher number wins a file both have), as Launcher.swift orderLabel.
            if (m.Enabled) name.Children.Add(new TextBlock { Name = "ModOrder_" + m.Name, Text = (i + 1).ToString(), FontSize = 11, Foreground = Th.Dim, MinWidth = 12, VerticalAlignment = VerticalAlignment.Center });
            name.Children.Add(new TextBlock { Text = m.Name, Foreground = m.Enabled ? Th.Text : Th.Dim, TextTrimming = TextTrimming.CharacterEllipsis });
            if (!m.Present) name.Children.Add(new TextBlock { Text = "folder missing", FontSize = 12, Foreground = Th.Warn, VerticalAlignment = VerticalAlignment.Center });
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), MinHeight = 44 };
            row.Children.Add(sw); Grid.SetColumn(name, 1); row.Children.Add(name);
            if (m.Enabled) { Grid.SetColumn(up, 2); row.Children.Add(up); Grid.SetColumn(down, 3); row.Children.Add(down); }
            _modsList.Children.Add(new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 0), Background = m.Enabled ? Th.Surface : null, Child = row });
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

    // ---- focus and gamepad (proposal §6) ----
    // Focus lost (start, a rebuilt page): the screen's primary button gets it; with a pad in use the ring shows.
    void KeepFocus()
    {
        var f = FocusManager?.GetFocusedElement() as Control;
        if (f != null && f.IsAttachedToVisualTree() && f.IsEffectivelyVisible && f.IsEffectivelyEnabled) return;
        var to = _primary is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true } ? _primary
            : _hp2Tab ? _hp2Panel.GetLogicalDescendants().OfType<Button>().FirstOrDefault(b => b.IsEffectivelyVisible && b.IsEffectivelyEnabled) : null;
        to?.Focus(_padUsed ? NavigationMethod.Directional : NavigationMethod.Unspecified);
    }

    // One gamepad button (PadMap numbers; what it does is PadMap.For, the answer launcher/Launcher.swift shares): A = activate
    // the focused control, B = back (popup, sub-page), LB/RB = game, Start = Menu, Y = Play, d-pad / stick = move through the
    // one focus column (inside an open menu or list: its items).
    public void Pad(int button)
    {
        _padUsed = true; _padHints.IsVisible = true;
        var f = FocusManager?.GetFocusedElement() as Control;
        bool popup = f != null && (TopLevel.GetTopLevel(f) != this || f is ComboBox { IsDropDownOpen: true });
        if (f == null || (!popup && !f.IsEffectivelyVisible)) { KeepFocus(); f = FocusManager?.GetFocusedElement() as Control; }
        else if (!popup) f.Focus(NavigationMethod.Directional);   // the ring shows from the first press
        void Key(Key k) => f?.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = k, Source = f });
        var act = PadMap.For(button, popup, _hp2Tab);
        switch (act)
        {
            case PadAction.Activate: Key(Avalonia.Input.Key.Enter); break;
            case PadAction.Escape: Key(Avalonia.Input.Key.Escape); break;
            case PadAction.Back: GoBack(); break;
            case PadAction.Hp1: Click(_tabHp1); break;
            case PadAction.Hp2: Click(_tabHp2); break;
            case PadAction.Menu: _menuButton.Flyout?.ShowAt(_menuButton); break;
            case PadAction.Play: if (_play2.IsEffectivelyVisible && _play2.IsEnabled) Play(); break;
            case PadAction.KeyUp: Key(Avalonia.Input.Key.Up); break;
            case PadAction.KeyDown: Key(Avalonia.Input.Key.Down); break;
            case PadAction.Previous or PadAction.Next:
                if (f != null && KeyboardNavigationHandler.GetNext(f, act == PadAction.Previous ? NavigationDirection.Previous : NavigationDirection.Next) is Control next)
                    next.Focus(NavigationMethod.Directional);
                break;
        }
    }

    static void Click(Button b) => b.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    // The visible sub-page's Back (Mods, HP2 New/Load Game): pad B and Escape.
    void GoBack()
    {
        if (_dismiss.IsEffectivelyVisible && FocusManager?.GetFocusedElement() is { } f && (f == _dismiss || f == _openLog)) { DismissError(); return; }   // on the error banner: close it
        if (this.GetLogicalDescendants().OfType<Button>().FirstOrDefault(b => b.Name is "ModsBack" or "Hp2Back" && b.IsEffectivelyVisible) is { } back) Click(back);
    }

    // The keyboard shortcuts of both launchers (launcher/Launcher.swift): Ctrl+1 / Ctrl+2 the game, Ctrl+, the Menu,
    // Escape Back (macOS: Cmd for Ctrl). Enter presses the focused button, which starts on the screen's primary.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        bool ctrl = e.KeyModifiers == KeyModifiers.Control;
        if (ctrl && e.Key is Avalonia.Input.Key.D1 or Avalonia.Input.Key.D2) Click(e.Key == Avalonia.Input.Key.D2 ? _tabHp2 : _tabHp1);
        else if (ctrl && e.Key == Avalonia.Input.Key.OemComma) _menuButton.Flyout?.ShowAt(_menuButton);
        else if (e.Key == Avalonia.Input.Key.Escape && e.KeyModifiers == KeyModifiers.None) GoBack();
        else return;
        e.Handled = true;
    }

    // Polls the pad (the SDL3 library in HPEngine/, Gamepad.cs) while this window is in front and no game runs.
    public void StartGamepad()
    {
        if (!Gamepad.Load(Path.Combine(_appDir, "HPEngine"))) return;
        uint last = 0;
        DispatcherTimer.Run(() =>
        {
            var now = Gamepad.Read();
            _padHints.IsVisible = now != null;
            if (now is uint bits && IsActive && !_playing)
                foreach (var b in PadMap.Pressed(bits, last)) Pad(b);
            last = now ?? 0;
            return true;
        }, TimeSpan.FromMilliseconds(50));
    }

    // ---- install ----
    // Public for the headless check: a dropped or chosen path.
    public void Add(string path)
    {
        if (_own && !_hp2Tab) return;   // the HP1 game next to the Launcher is used: nothing to install
        if (!_inputs.Contains(path)) _inputs.Add(path);
        SetupError(null); _step = -1; _modsOpen = false; Refresh();
    }

    void Install()
    {
        if ((_own && !_hp2Tab) || _inputs.Count == 0 || _running || _playing) return;
        if (!File.Exists(Exe("HP1Setup"))) { SetupError($"HP1Setup was not found (the HPEngine folder must stay next to the Launcher) ({Exe("HP1Setup")})."); Refresh(); return; }
        _running = true; SetupError(null); _warning = null; _report = null; _step = 0; _progress = 0;
        var psi = new ProcessStartInfo(Exe("HP1Setup")) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true, UseShellExecute = false };
        if (_hp2Tab) { psi.ArgumentList.Add("--game"); psi.ArgumentList.Add("hp2"); }
        psi.ArgumentList.Add("--dest"); psi.ArgumentList.Add(_hp2Tab ? Hp2Game : _game);
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
                    if (p.ExitCode != 0 && (_hp2Tab ? _hp2Error : _error) == null)
                        SetupError($"The setup stopped unexpectedly (code {p.ExitCode})." + (odd.Count > 0 ? " Its output: " + string.Join(" | ", odd.TakeLast(3)) : ""));
                    if (_hp2Tab) Hp2Scan();
                    Refresh();
                });
            });
        }
        catch (Exception e) { SetupError(e.Message); _running = false; }
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
            case SetupKind.Error: SetupError(l.Text); _edition = Edition(); break;   // the installed edition is still the marker's
            case SetupKind.Warning: _warning = l.Text; break;
            case SetupKind.Report: _report = l.Text; break;
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
            var logFile = LogFile(false);
            Directory.CreateDirectory(Path.GetDirectoryName(logFile));
            var log = new StreamWriter(new FileStream(logFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };   // the run's own console output (crashes included)
            var psi = new ProcessStartInfo(Exe("HPEngine")) { WorkingDirectory = _game, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in StartMap.PlayArgs(_game, SelectedMap)) psi.ArgumentList.Add(a);
            var said = new List<string>();   // the engine's console output, for the error banner
            _runLogsBefore = GameScope.RunLogs(GameScope.LogsDir(_game, false));
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
                    if (p.ExitCode != 0) lock (log) _error = _runError = EngineError.Banner(said, p.ExitCode);
                    Refresh();
                });
            });
        }
        catch (Exception e) { _error = e.Message; }
        Refresh();
    }

    // ---- Menu ----
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
        bool hp2 = _hp2Tab;
        if ((hp2 ? !Hp2Installed : _own || !Installed) || _running || _playing) return;
        string root = hp2 ? Hp2Game : _game;
        var fate = OperatingSystem.IsWindows() ? "It is moved to the Recycle Bin." : "It is deleted permanently (there is no portable trash on Linux).";
        var all = new CheckBox { Name = "AlsoDelete", Content = "Also delete saved games, settings and mods" };
        var d = Dialog("Remove game?", 460);
        var yes = new Button { Content = "Remove", MinWidth = 96, Foreground = Th.Err }; var no = new Button { Content = "Cancel", MinWidth = 96 };
        yes.Classes.Add("quiet"); no.Classes.Add("quiet");
        yes.Click += (_, _) => d.Close(true); no.Click += (_, _) => d.Close(false);
        d.Content = new StackPanel { Margin = new Thickness(20), Spacing = 14, Children = {
            new TextBlock { Text = $"The installed game copy in {root} is removed. Saved games, settings and mods are kept. {fate} Your own game files are not touched.", TextWrapping = TextWrapping.Wrap },
            all, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { no, yes } } } };
        if (!await d.ShowDialog<bool>(this)) return;
        try
        {
            GameRemoval.Remove(root, all.IsChecked == true, OperatingSystem.IsWindows() ? Recycle : null);
            // HP2's saved games and settings live beside its install root (Hp2FrontEnd.SaveDir, InstallRoot.Hp2DataDir)
            if (hp2 && all.IsChecked == true)
                foreach (var dir in new[] { Hp2FrontEnd.SaveDir(_game), GameScope.DataDir(_game, true) }.Where(Directory.Exists))
                    if (OperatingSystem.IsWindows()) Recycle(dir); else GameRemoval.Walk(dir, false);
            SetupError(null); _warning = null; _report = null; _step = -1; _edition = null;
        }
        catch (Exception e) { SetupError("Could not remove the game: " + e.Message); }
        if (hp2) { _hp2Page = "Main"; Hp2Scan(); }
        Refresh();
    }

    static void Recycle(string p)
    {
        if (Directory.Exists(p)) Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(p, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        else Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(p, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
    }

    // Linux: HPEngine as a non-Steam game (SteamShortcuts.cs). Always asked first; nothing is written while Steam runs.
    async Task AddToSteam()
    {
        var exe = Environment.ProcessPath;
        if (!await Confirm("Add to Steam", $"Add HPEngine to your Steam library as a non-Steam game? Steam will start this Launcher ({exe}). Close Steam completely first. "
            + "Steam's shortcut list is saved as a backup copy next to it; every Steam account on this computer gets the entry (once).", "Add")) return;
        string text;   // fire-and-forget from the menu: an exception here would vanish unseen, so it is shown
        try { text = SteamShortcuts.AddTo(SteamShortcuts.FindRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), "HPEngine", exe, Path.GetDirectoryName(exe), SteamShortcuts.SteamRunning); }
        catch (Exception e) { text = "Could not add HPEngine to Steam: " + e.Message; }
        await Confirm("Add to Steam", text, "OK", false);
    }

    async Task About() => await Confirm("HPEngine Launcher " + _version,
        $"HPEngine runs {GameTitle.For(EditionOf(_game))} (PC, 2001) from your own copy of the game. No game files are included. Game data: {_game}" + (_own ? " (your game folder, the one this Launcher is in)" : "")
        + (Hp2Root is { } h2 ? $"\n\n{Hp2Title}: {h2}. Its menu is drawn in HPEngine's own look (not the original dialog's); what it offers and starts is the original's." : ""), "OK", false);

    // A dialog in the selected game's colours.
    Window Dialog(string title, double width)
    {
        var d = new Window { Title = title, Width = width, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false, Background = new SolidColorBrush(Th.Popup.Color), Foreground = Th.Text, FontSize = 14 };
        foreach (var s in Styles) d.Styles.Add(s);
        return d;
    }

    async Task<bool> Confirm(string title, string text, string ok, bool cancel = true)
    {
        var d = Dialog(title, 420);
        var yes = new Button { Content = ok, MinWidth = 96 }; var no = new Button { Content = "Cancel", MinWidth = 96, IsVisible = cancel };
        yes.Classes.Add("quiet"); no.Classes.Add("quiet");
        yes.Click += (_, _) => d.Close(true); no.Click += (_, _) => d.Close(false);
        d.Content = new StackPanel { Margin = new Thickness(20), Spacing = 14, Children = { new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { no, yes } } } };
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

    // ---- updates: the signed archive is downloaded, verified and swapped in by a helper (SelfUpdate.cs); no key/archive: the release page ----
    async Task InstallUpdate()
    {
        if (_newer?.Archive == null || _newer.Sig == null || SelfUpdate.IncompleteNotice(_appDir) != null || _updating || _running || _playing) return;
        _updating = true; _updateInstall.IsEnabled = false; _updateText.Text = "Downloading Launcher " + _newer.Version + "…"; _launcherError = null; UpdateBanners();
        var tmp = Path.Combine(Path.GetTempPath(), "hpengine-download-" + Guid.NewGuid().ToString("N"));
        var stage = Path.Combine(_appDir, SelfUpdate.StageName);
        bool handedOver = false;
        try
        {
            Directory.CreateDirectory(tmp);
            var sig = await Http.GetByteArrayAsync(_newer.Sig);
            var file = Path.Combine(tmp, Path.GetFileName(new Uri(_newer.Archive).LocalPath));
            using (var rs = await Http.GetStreamAsync(_newer.Archive)) using (var fs = File.Create(file)) await rs.CopyToAsync(fs);
            var err = await Task.Run(() => SelfUpdate.Verify(file, sig, Program.UpdateKey, _newer.Version, SelfUpdate.LauncherName, stage));
            if (err != null) { _launcherError = err; return; }   // nothing of the install was touched
            SelfUpdate.StartHelper(_appDir, stage);
            handedOver = true;
            Close();
        }
        // Every failure (a bad URL, a helper that cannot start, …): this task is fire-and-forget, so it is shown here or never.
        catch (Exception e) { _launcherError = "Could not update: " + e.Message; }
        finally
        {
            try { Directory.Delete(tmp, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            if (!handedOver) try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            _updating = false; _updateInstall.IsEnabled = true; _updateText.Text = $"HPEngine {_newer.Version} is available."; UpdateBanners();
        }
    }

    async Task CheckForUpdate(bool manual)
    {
        string text = null; Release found = null;
        try
        {
            using var rq = new HttpRequestMessage(HttpMethod.Get, Environment.GetEnvironmentVariable("HPENGINE_RELEASES_URL") ?? "https://api.github.com/repos/artemnikonov/hpengine/releases?per_page=30");
            rq.Headers.UserAgent.ParseAdd("HPEngine-Launcher");
            text = await (await Http.SendAsync(rq)).EnsureSuccessStatusCode().Content.ReadAsStringAsync();
            found = Updates.Pick(text, _version, SelfUpdate.ArchiveName);
        }
        catch (Exception) { text = null; }
        if (found != null) { OfferUpdate(found); return; }
        if (manual) await Confirm("Check for updates", text == null ? "Could not check for updates." : $"HPEngine Launcher {_version} is up to date.", "OK", false);
    }

    // Public for the headless screenshots: the update banner for a newer release.
    public void OfferUpdate(Release found)
    {
        _newer = found; _updateText.Text = $"HPEngine {found.Version} is available."; _updateBanner.IsVisible = true;
        _updateInstall.IsVisible = SelfUpdate.Offered(found, Program.UpdateKey, _appDir);   // else only the release page
    }
}
