using System;

namespace HP1.UEScript
{
    // ── STARTUP MODE: HOW SHIPPED OPENS THE VIEWPORT THE FIRST TIME ─────────
    //
    // The shipped chain, read in full:
    //
    //   1) Engine.dll.c:38364-38372  UGameEngine::Init
    //         Viewport->OpenWindow(ParentWindow=0, Temporary=0,
    //                              NewX=-1, NewY=-1, OpenX=-1, OpenY=-1);
    //      i.e. the size is NOT set by the caller — the viewport fills it in.
    //
    //   2) WinDrv.dll.c:4816-4822    UWindowsViewport::OpenWindow
    //         THE WINDOW ITSELF is created at `Client->WindowedViewportX/Y` (for -1),
    //         with offset 100/100 — this is the frame, not the render mode.
    //
    //   3) WinDrv.dll.c:4896-4908    same place, render device selection:
    //         Fullscreen = Client->StartupFullscreen (UWindowsClient+0xb0,
    //                        declared :2813-2820, category Display)
    //                      && !ParseParam(appCmdLine(), "WINDOW");
    //         TryRenderDevice("ini:Engine.Engine.GameRenderDevice",
    //                         NewX, NewY, ColorBytes=-1, Fullscreen);
    //
    //   4) WinDrv.dll.c:4909-4921    IF the device did not come up:
    //         TryRenderDevice("ini:Engine.Engine.WindowedRenderDevice",
    //                         NewX, NewY, -1, 0);
    //         and only then appFailAssert("RenDev") — i.e. a fullscreen failure is a COMPLETE shipped path,
    //         not a crash path.
    //
    //   5) WinDrv.dll.c:15133-15144  UWindowsViewport::ResizeViewport, NewX==-1:
    //         size = (BlitFlags & BLIT_Fullscreen)
    //                  ? Client->FullscreenViewportX/Y (UClient+0x64/+0x68)
    //                  : Client->WindowedViewportX/Y   (UClient+0x58/+0x5c);
    //         offsets declared in Engine.dll.c:121845-121885.
    //
    //   6) WinDrv.dll.c:15366-15379  tail of a SUCCESSFUL resize — saving into THE SAME pair of keys, by the same
    //      fullscreen flag.
    //
    // HENCE TWO RULES:
    //   * the startup size comes from the Fullscreen* keys when starting fullscreen, and from Windowed* when windowed;
    //   * the applied size is saved into the key pair matching the CURRENT mode, not always into Windowed*.
    //
    // WHY THIS SHOWS ONLY ON OTHER DATA. In the shipped config both sizes are equal (640x480), so a fixture on shipped
    // numbers does NOT distinguish "reads Fullscreen*" from "reads Windowed*" (V9). Only a config with DIFFERENT
    // pairs does — fixtures and red mutations are built on it.
    //
    // BOUNDARY. The decision (which key, which mode, what to do on refusal) is a shipped rule and lives here. The
    // platform action (actually switching the mode) belongs to the frontend and arrives here as a transaction outcome,
    // as with a normal `SetRes`.
    public sealed class UEViewportStartup
    {
        // Startup mode: size and fullscreen flag, already resolved from data.
        public readonly struct Mode
        {
            public Mode(int sizeX, int sizeY, bool fullscreen)
            {
                SizeX = sizeX;
                SizeY = sizeY;
                Fullscreen = fullscreen;
            }

            public int SizeX { get; }
            public int SizeY { get; }
            public bool Fullscreen { get; }
        }

        public enum Phase
        {
            // The window has not been opened yet.
            NotStarted,
            // A fullscreen startup request is in flight.
            AwaitFullscreen,
            // A windowed startup request is in flight (primary or the step-4 fallback).
            AwaitWindowed,
            // The viewport is open.
            Opened,
            // Both attempts rejected — shipped has appFailAssert("RenDev") here.
            Failed,
        }

        // ONE VIEWPORT PER PROCESS. Shipped opens the window ONCE — in UGameEngine::Init; a map change reuses the
        // window and issues no resize. The port brings the world up for EVERY scene (travel, -hp1level), so "once"
        // must be a property of the process, not of the call. The decision (which key to read) and this latch have a
        // single owner (C7).
        public static UEViewportStartup Process { get; private set; } = new UEViewportStartup();

        // A new process (or a new Play entry in an editor host) starts from a clean state: otherwise a second run
        // would consider the window already open.
        public static void ResetProcess() => Process = new UEViewportStartup();

        public Phase State { get; private set; } = Phase.NotStarted;

        // What the last submitted request was — decides whether this was the windowed fallback (step 4) or the
        // primary attempt.
        bool _requestedFullscreen;
        bool _opened;

        // ── ParseParam (Core.dll.c:52391) ───────────────────────────────────
        //
        // Verbatim: case-insensitive substring search (appStrfind, Core.dll.c:42097 — folds ASCII a-z only); a match
        // counts when preceded by '-' or '/'. HP1 does not check the end of the match (unlike later UE), so
        // `-windowed` also yields true — this is not a simplification but the binary's observed behaviour.
        public static bool ParseParam(string commandLine, string param)
        {
            if (string.IsNullOrEmpty(commandLine) || string.IsNullOrEmpty(param)) return false;
            for (int at = 1; at + param.Length <= commandLine.Length; at++)
            {
                if (string.Compare(commandLine, at, param, 0, param.Length,
                        StringComparison.OrdinalIgnoreCase) != 0) continue;
                char before = commandLine[at - 1];
                if (before == '-' || before == '/') return true;
            }
            return false;
        }

        // ── WHAT TO OPEN ────────────────────────────────────────────────────
        //
        // The section is the same `Engine.Engine.ViewportManager` indirection as for Brightness and SetRes: in the
        // shipped config it leads to [WinDrv.WindowsClient], which holds all four keys.
        public static Mode Resolve(UEConfig config, string commandLine)
        {
            if (config is null) throw new ArgumentNullException(nameof(config));
            string section = config.ResolveSection(UEConfig.ScopeSystem, UEConfig.ViewportRef);

            bool fullscreen = ImportBool(config, section, "StartupFullscreen")
                              && !ParseParam(commandLine, "WINDOW");

            (int x, int y) = SavedSize(config, fullscreen);
            return new Mode(x, y, fullscreen);
        }

        // Mode key pair (ResizeViewport with NewX == -1, WinDrv.dll.c:15133-15144): Fullscreen* or Windowed*.
        public static (int X, int Y) SavedSize(UEConfig config, bool fullscreen)
        {
            if (config is null) throw new ArgumentNullException(nameof(config));
            string section = config.ResolveSection(UEConfig.ScopeSystem, UEConfig.ViewportRef);
            return (ReadSize(config, section, fullscreen ? "FullscreenViewportX" : "WindowedViewportX"),
                    ReadSize(config, section, fullscreen ? "FullscreenViewportY" : "WindowedViewportY"));
        }

        // UBoolProperty::ImportText (Core.dll.c:33202) knows SIX spellings: "1"/"True"/GTrue and "0"/"False"/GFalse,
        // case-insensitive; on any other text it returns NULL and the property keeps its CLASS DEFAULT. The port does
        // not carry UWindowsClient class defaults, so neither a missing key nor garbage in it is silently tolerated
        // (§8).
        // UClient.NoLighting (Client+0x48) comes from the same client section; read by the actor light gate
        // (UEActorLighting.ViewLit).
        public static bool ClientNoLighting(UEConfig config)
        {
            if (config is null) throw new ArgumentNullException(nameof(config));
            return ImportBool(config, config.ResolveSection(UEConfig.ScopeSystem, UEConfig.ViewportRef), "NoLighting");
        }

        // UClient.NoDynamicLights (Client+0x50) from the same client section; read by the lightmap categoriser
        // (MoverMode3.Categorize, via SurfLightmap.FrameFx).
        public static bool ClientNoDynamicLights(UEConfig config)
        {
            if (config is null) throw new ArgumentNullException(nameof(config));
            return ImportBool(config, config.ResolveSection(UEConfig.ScopeSystem, UEConfig.ViewportRef), "NoDynamicLights");
        }

        internal static bool ImportBool(UEConfig config, string section, string key)
        {
            string raw = config.Get(UEConfig.ScopeSystem, section, key)?.Trim();
            if (string.Equals(raw, "True", StringComparison.OrdinalIgnoreCase)
             || raw == "1") return true;
            if (string.Equals(raw, "False", StringComparison.OrdinalIgnoreCase)
             || raw == "0") return false;
            throw new System.IO.InvalidDataException(
                "[" + section + "] " + key + " = '" + (raw ?? "<no key>")
                + "': on unrecognized text shipped keeps the CLASS DEFAULT "
                + "(UBoolProperty::ImportText, Core.dll.c:33202), and the port does not carry "
                + "UWindowsClient defaults — substituting its own value would mean "
                + "inventing a launch mode.");
        }

        static int ReadSize(UEConfig config, string section, string key)
        {
            string raw = config.Get(UEConfig.ScopeSystem, section, key)?.Trim();
            if (int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int v) && v > 0)
                return v;
            throw new System.IO.InvalidDataException(
                "[" + section + "] " + key + " = '" + (raw ?? "<no key>")
                + "': nothing to take the startup viewport size from. Shipped substitutes "
                + "a client field here (WinDrv.dll.c:15133-15144), which has a class "
                + "default; the port does not have it.");
        }

        // ── OPENING (chain steps 1-3) ───────────────────────────────────────
        //
        // The device resizes synchronously (IViewportDevice.Resize), so the chain ends inside this call.
        public void Open(UEConfig config, string commandLine, IViewportDevice device)
        {
            if (device is null) throw new ArgumentNullException(nameof(device));
            if (_opened) return;
            _opened = true;
            Mode mode = Resolve(config, commandLine);
            Request(config, device, mode.SizeX, mode.SizeY, mode.Fullscreen);
        }

        void Request(UEConfig config, IViewportDevice device, int sizeX, int sizeY, bool fullscreen)
        {
            _requestedFullscreen = fullscreen;
            State = fullscreen ? Phase.AwaitFullscreen : Phase.AwaitWindowed;
            Settle(device.Resize(sizeX, sizeY, 0, fullscreen), config, device);
        }

        // ── REQUEST OUTCOME (chain step 4) ──────────────────────────────────
        //
        // Saving the applied size is the same step 4 of the shipped resize as for `SetRes`
        // (UEConsoleCommand.CommitAppliedResize, WinDrv.dll.c:15366-15379). ONLY what a normal SetRes lacks lives
        // here: falling back from the fullscreen attempt to windowed (WinDrv.dll.c:4909-4921).
        void Settle(ViewportResizeOutcome outcome, UEConfig config, IViewportDevice device)
        {
            if (outcome == ViewportResizeOutcome.Applied)
            {
                UEConsoleCommand.CommitAppliedResize(config, device);
                State = Phase.Opened;
                return;
            }

            if (_requestedFullscreen)
            {
                // Step 4: the fullscreen device did not come up — shipped takes the WINDOWED one with the same
                // NewX=-1, i.e. with the Windowed* size.
                string section = config.ResolveSection(UEConfig.ScopeSystem, UEConfig.ViewportRef);
                int x = ReadSize(config, section, "WindowedViewportX");
                int y = ReadSize(config, section, "WindowedViewportY");
                HP1.Core.UETrace.Warn("[viewport] fullscreen start not accepted by the device — "
                    + "opening windowed " + x + "x" + y + " (shipped path: "
                    + "TryRenderDevice(WindowedRenderDevice, …, Fullscreen=0), "
                    + "WinDrv.dll.c:4909-4921)");
                Request(config, device, x, y, false);
                return;
            }

            State = Phase.Failed;
            HP1.Core.UETrace.Warn("[viewport] startup viewport NOT OPENED: the windowed request "
                + "was rejected by the device. Shipped has appFailAssert(\"RenDev\") here "
                + "(WinDrv.dll.c:4921) — the window stays as the platform created it.");
        }
    }
}
