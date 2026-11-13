// OS half of SetMouseCapture for the SDL window (InputStage.OsCapture). WinDrv FUN_11109280 (WinDrv.dll.c:11541-11600):
// capture = SetForegroundWindow, cursor to the client centre, SetCapture, ShowCursor(0), ClipCursor(client);
// release = ShowCursor(1), cursor back, ClipCursor(NULL). SDL relative mode is that set (hidden, confined, centred,
// deltas); SDL_RaiseWindow is SetForegroundWindow. It executes InputStage's decision, decides nothing.
using System;
using System.Runtime.InteropServices;
using SDL;
using static SDL.SDL3;

namespace HPEngine.Input
{
    public static unsafe class SdlMouseCapture
    {
        public static Action<string> Log = Console.WriteLine;

        // Windows delivers the click that activates an inactive window (WM_MOUSEACTIVATE -> MA_ACTIVATE); macOS swallows
        // it unless the view accepts the first mouse. Set before the window gets mouse events.
        public static void Init() => SDL_SetHint(SDL_HINT_MOUSE_FOCUS_CLICKTHROUGH, "1");

        public static void Apply(SDL_Window* w, bool captured)
        {
            if (captured)
            {
                SDL_RaiseWindow(w);
                // SDL skips a mode it already has; off -> on runs the platform capture again (macOS: warp to the centre,
                // CGAssociateMouseAndMouseCursorPosition(NO), [NSCursor hide]) after a mode switch or a focus return.
                SDL_SetWindowRelativeMouseMode(w, false);
                if (OperatingSystem.IsMacOS()) MacReleaseStuckMove(w);
                if (!SDL_SetWindowRelativeMouseMode(w, true)) Log($"[Mouse] relative mode refused: {SDL_GetError()}");
            }
            else SDL_SetWindowRelativeMouseMode(w, false);
            Log($"[Mouse] capture {(captured ? "on" : "off")}: relative {SDL_GetWindowRelativeMouseMode(w)}, focus {(SDL_GetWindowFlags(w) & SDL_WindowFlags.SDL_WINDOW_INPUT_FOCUS) != 0}");
        }

        // SDL 3 cocoa (SDL_cocoawindow.m windowWillMove / sendEvent): any NSWindowWillMoveNotification sets the listener's
        // isMoving, and only a left mouse-up clears it; while it is set Cocoa_SetRelativeMouseMode returns early (no
        // confinement, no hide) yet SDL reports relative mode on. A mode switch moves the window, so the cursor stayed
        // free and visible next to the drawn one until a click. With no button held there is no user drag: finish it.
        static void MacReleaseStuckMove(SDL_Window* w)
        {
            IntPtr nswindow = SDL_GetPointerProperty(SDL_GetWindowProperties(w), SDL_PROP_WINDOW_COCOA_WINDOW_POINTER, IntPtr.Zero);
            if (nswindow == IntPtr.Zero) return;
            IntPtr listener = Msg(nswindow, Sel("delegate"));
            if (listener == IntPtr.Zero || MsgBool(listener, Sel("respondsToSelector:"), Sel("isMoving")) == 0) return;
            if (MsgBool(listener, Sel("isMoving")) == 0) return;
            float x, y;
            if (SDL_GetGlobalMouseState(&x, &y) != 0) return;
            Msg(listener, Sel("windowDidFinishMoving"));
            Log("[Mouse] macOS: window stuck in a move (SDL isMoving) released");
        }

        const string ObjC = "/usr/lib/libobjc.A.dylib";
        [DllImport(ObjC, EntryPoint = "sel_registerName")] static extern IntPtr Sel(string name);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Msg(IntPtr self, IntPtr sel);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern byte MsgBool(IntPtr self, IntPtr sel);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern byte MsgBool(IntPtr self, IntPtr sel, IntPtr arg);
    }
}
