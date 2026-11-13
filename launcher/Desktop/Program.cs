// HPEngine Launcher (Windows/Linux). Finds HPEngine and HP1Setup in the HPEngine/ folder next to itself; the game data lives in the per-user data
// directory (Logic.cs DataDir), so nothing needs admin rights; unpacked into a game folder, that game is used (DataDir.OwnGame). Built by hpengine/packaging/make_launcher.sh.
using System.Reflection;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace HPEngine.Launcher;

public sealed class App : Application
{
    public override void Initialize() { RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark; Styles.Add(new FluentTheme()); }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            var game = DataDir.Game(DataDir.Current, Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            d.MainWindow = new MainWindow(game, exeDir, Program.Version, true);
        }
        base.OnFrameworkInitializationCompleted();
    }
}

public static class Program
{
    // launcher/VERSION through the assembly version ("0.1.0-beta"; the SDK appends "+<commit>", ignored by the comparison).
    public static string Version => (typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0").Split('+')[0];

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--version") { Console.WriteLine(Version); return 0; }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();
}
