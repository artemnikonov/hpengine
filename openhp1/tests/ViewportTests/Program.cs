// Windowed Resolution list of the OpenHP1 window device (bug 2026-10-15): on a MacBook (3024x1964 px, 1512x982 pt,
// density 2) the list offered 3024x1890/3024x1964; macOS keeps a window inside the usable area with its title bar, so the
// window stayed 3024x1648 and every larger choice showed the confirm dialog with nothing changed.
// Exit 0 — all green; 1 — FAILs (each named by a line).
using OpenHP1;

static class Program
{
    static int _fail;
    static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + name + (ok || detail == "" ? "" : ": " + detail));
        if (!ok) _fail++;
    }

    // A device that applies something other than the request (the OS keeps a window inside the usable area): only then is
    // "the applied size was saved" distinguishable from "the key kept what it had".
    sealed class ClampingDevice : HP1.UEScript.IViewportDevice
    {
        public int SizeX { get; private set; } = 100;
        public int SizeY { get; private set; } = 100;
        public int ColorBytes => 4;
        public bool Fullscreen { get; private set; }
        public string DisplayModes => "";
        public int Resizes;
        public HP1.UEScript.ViewportResizeOutcome Resize(int sizeX, int sizeY, int colorBytes, bool fullscreen)
        {
            Resizes++;
            SizeX = Math.Min(sizeX, 700); SizeY = Math.Min(sizeY, 500); Fullscreen = fullscreen;
            return HP1.UEScript.ViewportResizeOutcome.Applied;
        }
    }

    // STARTUP WINDOW (bug: OpenHP1Program opened it with a direct Resize, bypassing UEViewportStartup.Open, so the applied
    // size was never saved). The frontend's startup goes through Core's chain, whose step 4 saves the applied size into the
    // mode's key pair (WinDrv.dll.c:15366-15379).
    static void Startup()
    {
        string dir = Path.Combine(Path.GetTempPath(), "hp1_vp_" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        string sys = Path.Combine(dir, "sys.ini"), user = Path.Combine(dir, "user.ini");
        File.WriteAllText(sys, "[Engine.Engine]\nViewportManager=WinDrv.WindowsClient\n[WinDrv.WindowsClient]\nStartupFullscreen=False\n"
            + "WindowedViewportX=800\nWindowedViewportY=600\nFullscreenViewportX=640\nFullscreenViewportY=480\n");
        File.WriteAllText(user, "");
        HP1.UEScript.UEConfig Cfg() => new(new HP1.UEScript.UEIniFile(sys), new HP1.UEScript.UEIniFile(user));
        const string S = HP1.UEScript.UEConfig.ScopeSystem, Sec = "WinDrv.WindowsClient";

        HP1.UEScript.UEViewportStartup.ResetProcess();
        var cfg = Cfg(); var vp = new ClampingDevice();
        SdlViewportDevice.OpenStartup(vp, cfg, "HP1.app", null);
        Check("startup-requests-saved-size", vp.Resizes == 1 && !vp.Fullscreen, $"resizes={vp.Resizes} fullscreen={vp.Fullscreen}");
        Check("startup-saves-applied", cfg.Get(S, Sec, "WindowedViewportX") == "700" && cfg.Get(S, Sec, "WindowedViewportY") == "500",
            $"{cfg.Get(S, Sec, "WindowedViewportX")}x{cfg.Get(S, Sec, "WindowedViewportY")}");
        Check("startup-chain-closed", HP1.UEScript.UEViewportStartup.Process.State == HP1.UEScript.UEViewportStartup.Phase.Opened,
            $"{HP1.UEScript.UEViewportStartup.Process.State}");

        // --size: a debug window outside the shipped chain, never saved.
        HP1.UEScript.UEViewportStartup.ResetProcess();
        cfg = Cfg(); vp = new ClampingDevice();
        SdlViewportDevice.OpenStartup(vp, cfg, "HP1.app", (320, 240));
        Check("debug-size-not-saved", vp.SizeX == 320 && cfg.Get(S, Sec, "WindowedViewportX") == "800",
            $"device {vp.SizeX}, key {cfg.Get(S, Sec, "WindowedViewportX")}");
        Directory.Delete(dir, true);
    }

    static int Main()
    {
        // The author's case: usable area 1512x852 pt (menu bar and Dock out), title bar 28 pt, density 2.
        var limit = SdlViewportDevice.WindowLimit((1512, 852), (28, 0, 0, 0), 2f);
        Check("limit-mac", limit == (3024, 1648), $"{limit}");
        // Monitor modes in pixels (SDL mode w,h times pixel_density), as SdlViewportDevice.DisplayModes collects them.
        var modes = new[] { (3456, 2234), (3024, 1964), (3024, 1890), (2560, 1664), (2048, 1330), (1512, 982), (1024, 665) };
        string[] list = SdlViewportDevice.ModeList(modes, limit, false).Split(' ');
        Check("windowed-no-oversize", !list.Contains("3024x1964") && !list.Contains("3024x1890") && !list.Contains("2560x1664") && !list.Contains("3456x2234"),
            string.Join(" ", list));
        Check("windowed-fitting-kept", list.Contains("2048x1330") && list.Contains("1512x982") && list.Contains("1024x665"), string.Join(" ", list));
        Check("windowed-max-offered", list.Contains("3024x1648") && list[^1] == "3024x1648", string.Join(" ", list));
        Check("windowed-ascending", string.Join(" ", list) == "1024x665 1512x982 2048x1330 3024x1648", string.Join(" ", list));
        // Every listed size fits, side by side, in what the window can take: the dialog never lies.
        Check("windowed-all-fit", list.All(s => { var p = s.Split('x'); return int.Parse(p[0]) <= limit.W && int.Parse(p[1]) <= limit.H; }));

        // Units: borders are points and count twice on a 2x display; density 1 keeps points == pixels.
        Check("limit-borders-all-sides", SdlViewportDevice.WindowLimit((1920, 1040), (30, 8, 8, 8), 1f) == (1904, 1002),
            $"{SdlViewportDevice.WindowLimit((1920, 1040), (30, 8, 8, 8), 1f)}");
        Check("limit-density-floor", SdlViewportDevice.WindowLimit((1001, 701), (0, 0, 0, 0), 1.5f) == (1501, 1051),
            $"{SdlViewportDevice.WindowLimit((1001, 701), (0, 0, 0, 0), 1.5f)}");
        Check("limit-density-unknown", SdlViewportDevice.WindowLimit((800, 600), (0, 0, 0, 0), 0f) == (800, 600));

        // Fullscreen: only the desktop (native), unchanged by the fix.
        Check("fullscreen-desktop-only", SdlViewportDevice.ModeList(modes, (3024, 1964), true) == "3024x1964");

        Startup();

        Console.WriteLine(_fail == 0 ? "VIEWPORT PASS" : $"VIEWPORT FAIL ({_fail})");
        return _fail == 0 ? 0 : 1;
    }
}
