#if IOS
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Foundation;

namespace HPEngine
{
    static unsafe partial class Program
    {
        static string[] iosArgs;

        static int RunIos(string[] args)
        {
            string framework = Path.Combine(NSBundle.MainBundle.BundlePath, "Frameworks", "SDL3.framework", "SDL3");
            nint sdl = NativeLibrary.Load(framework);
            NativeLibrary.SetDllImportResolver(typeof(SDL.SDL3).Assembly,
                (name, _, _) => name == "SDL3" ? sdl : 0);
            // The .NET iOS host passes Main no arguments; the launch arguments (Xcode scheme, devicectl) are in NSProcessInfo.
            iosArgs = args.Length > 0 ? args : NSProcessInfo.ProcessInfo.Arguments.Skip(1).ToArray();
            // Landscape only, as Info.plist. Without it SDL (3.4.16 UIKit_ComputeViewFrame) judges the orientation by
            // statusBarOrientation, which a scene-based app does not report, and swapped the landscape window to portrait
            // (1640x2360 drawn into a 2360x1640 screen: shifted, black bars, edges cut off).
            SDL.SDL3.SDL_SetHint("SDL_ORIENTATIONS", "LandscapeLeft LandscapeRight");
            // Touches are the on-screen controls (Input/TouchControls.cs), not also a mouse: SDL's emulated mouse would turn
            // and click as well.
            SDL.SDL3.SDL_SetHint("SDL_TOUCH_MOUSE_EVENTS", "0");

            // SDL owns UIApplicationMain and calls the engine on UIKit's main thread.
            var runApp = (delegate* unmanaged[Cdecl]<int, byte**, delegate* unmanaged[Cdecl]<int, byte**, int>, void*, int>)
                NativeLibrary.GetExport(sdl, "SDL_RunApp");
            return runApp(0, null, &IosMain, null);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static int IosMain(int argc, byte** argv)
        {
            try
            {
                Console.WriteLine($"[iOS] launch arguments: {(iosArgs.Length == 0 ? "none" : string.Join(' ', iosArgs))}");
                if (iosArgs.Contains("--root") || iosArgs.Contains("--selftest-gpu")) return Run(iosArgs);

                string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string hp1 = Path.Combine(documents, "game");
                string hp2Data = Path.Combine(documents, "HP2Data");
                string hp2 = Path.Combine(hp2Data, "game");
                ContentDownload.EnsureGames(("hp1", hp1), ("hp2", hp2));

                if (Packaging.InstallRoot.Missing(hp1) is null)
                    return Run(["--root", hp1, .. iosArgs]);
                if (Packaging.InstallRoot.Missing(hp2) is null)
                {
                    string saves = Path.Combine(hp2Data, "Save");
                    const string newGame = "PrivetDr.unr -SAVESLOT=1";
                    const string loadGame = "-LOADGAME=0 -SAVESLOT=1";
                    string command = File.Exists(Path.Combine(saves, "Slot1", "save0.hp1sav")) ? loadGame : newGame;
                    return Run(["--root", hp2, "--hp2-savedir", saves, "--hp2-cmdline", command, .. iosArgs]);
                }

                const string message = "Copy your installed PC game folder into On My iPhone or On My iPad → HPEngine in Files. "
                    + "Use game for Harry Potter 1 or HP2Data/game for Harry Potter 2. "
                    + "The game folder must contain System and Maps. Reopen HPEngine when the copy finishes.";
                Console.Error.WriteLine(message);
                FatalBox(message);
                return Packaging.InstallRoot.ExitCode;
            }
            catch (Exception error)
            {
                // Managed exceptions cannot cross the native SDL_main callback.
                Console.Error.WriteLine(error);
                return 1;
            }
        }
    }
}
#endif
