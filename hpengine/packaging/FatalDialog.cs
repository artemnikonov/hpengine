// NOT ORIGINAL: when a start-up failure of the game is also shown in a native dialog. The engine is a windowed
// executable (HPEngine.csproj WinExe): started by double-click it has no console, so the stderr text of "no game
// folder" (exit code 4), a mod problem (exit code 5), a missing start map (exit code 6) or a crash is seen by nobody.
// This file is the rule only (pure, checked by hpengine/tests/PackagingTests); HPEngineProgram shows the dialog
// (SDL_ShowSimpleMessageBox).
using System;
using System.Linq;

namespace HPEngine.Packaging
{
    public static class FatalDialog
    {
        // The Launchers pass this: they capture the engine's console output and show the error in their own window.
        // An explicit flag, not Console.IsErrorRedirected: without a console (Windows double-click of a WinExe, a Linux
        // file manager) stderr also reads as redirected, and that is exactly the case the dialog is for.
        public const string LauncherFlag = "--launched-by-launcher";

        // Modes that are not the player's game window: no window at all, or a judge/bench window.
        static readonly string[] NotTheGame =
        {
            "--shot", "--video", "--selftest-gpu", "--window-script", "--offscreen-window",
            "--audio-probe", "--ice-png", "--input-probe", "--mesh-dump", "--mover-lmatlas", "--movers", "--travel-at", "--travel-png", "--weapon",
        };

        // The windowed game: a player session with its run log, mods, splash and saved settings.
        public static bool WindowedGame(string[] args) => !args.Any(NotTheGame.Contains);

        // The dialog: only the windowed game, not under a Launcher, and not a run told to close itself (--quit-after:
        // a bench with nobody to press OK).
        public static bool Wanted(string[] args) =>
            WindowedGame(args) && !args.Contains(LauncherFlag) && !args.Contains("--quit-after");
    }
}
