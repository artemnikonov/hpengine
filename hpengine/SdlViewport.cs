// HPEngine WINDOW DEVICE FOR CORE (IViewportDevice). Resolution "fits the monitor" (NOT ORIGINAL).
//
// Viewport size is window pixels (3D draws straight at swapchain size), ColorBytes is 4 (no 16/32 depth in the menu).
// Fullscreen covers the whole desktop without a monitor mode change: its size is always native, so the fullscreen
// mode list is the single native mode. Windowed: monitor modes that fit the largest window client area (WindowMax),
// plus that area itself.
//
// WHY NOT THE DESKTOP (seen on a MacBook, 3024x1964 px / 1512x982 pt): a window cannot be as large as the desktop —
// the OS keeps it inside the usable area (macOS visibleFrame: minus the menu bar and the Dock) together with its title
// bar. A listed 3024x1890 or 3024x1964 was set, macOS shrank the window to the 1512x824 pt that fit (3024x1648 px), the
// device honestly reported the applied size, and every larger choice showed the confirm dialog with nothing changed.
// The original never meets this: its windowed GetRes is the constant list up to 800x600 (WinDrv.dll.c:5126,
// UEConsoleCommand.WindowedResolutions); UWindowsViewport::ResizeViewport neither clamps nor refuses a window size
// (WinDrv.dll.c:15322-15342: AdjustWindowRect + MoveWindow of the requested size, viewport size = the request,
// :15297-15300). Offering monitor modes in a window is the port's own list (NOT ORIGINAL), so the
// list must offer only sizes a window can take.
// The script canvas (Canvas.SizeX/Y) is still set by PrepareStep: window / interface scale.
using System;
using HP1.UEScript;
using SDL;
using static SDL.SDL3;

namespace HPEngine
{
    public unsafe sealed class SdlViewportDevice : IViewportDevice
    {
        readonly SDL_Window* _w;
        public SdlViewportDevice(SDL_Window* window) { _w = window; }

        (int W, int H) Pixels() { int w, h; SDL_GetWindowSizeInPixels(_w, &w, &h); return (w, h); }
        public int SizeX => Pixels().W;
        public int SizeY => Pixels().H;
        public int ColorBytes => 4;
        public bool Fullscreen => (SDL_GetWindowFlags(_w) & SDL_WindowFlags.SDL_WINDOW_FULLSCREEN) != 0;

        (int W, int H) Desktop()
        {
            SDL_DisplayMode* d = SDL_GetDesktopDisplayMode(SDL_GetDisplayForWindow(_w));
            return d == null ? Pixels() : ((int)MathF.Round(d->w * d->pixel_density), (int)MathF.Round(d->h * d->pixel_density));
        }

        // Largest window client area in PIXELS: the display's usable bounds (points; SDL_GetDisplayUsableBounds excludes
        // the menu bar and the Dock/taskbar) minus the window decorations (points), times the window pixel density.
        (int W, int H) WindowMax()
        {
            SDL_Rect u;
            if (!SDL_GetDisplayUsableBounds(SDL_GetDisplayForWindow(_w), &u)) return Desktop();
            int top, left, bottom, right;
            if (!SDL_GetWindowBordersSize(_w, &top, &left, &bottom, &right))
            {
                if (!_bordersUnknownNoted) { _bordersUnknownNoted = true; Console.Error.WriteLine("[Viewport] window decoration size unknown (" + SDL_GetError() + "); window sizes limited by the usable display area only"); }
                top = left = bottom = right = 0;
            }
            return WindowLimit((u.w, u.h), (top, left, bottom, right), SDL_GetWindowPixelDensity(_w));
        }
        bool _bordersUnknownNoted;

        // Pure part of WindowMax (tested in hpengine/tests/ViewportTests): usable points minus borders, to pixels (floor:
        // a size that does not fit by a fraction of a point is not offered).
        public static (int W, int H) WindowLimit((int W, int H) usablePoints, (int Top, int Left, int Bottom, int Right) borders, float density)
        {
            if (!(density > 0)) density = 1f;
            return ((int)MathF.Floor((usablePoints.W - borders.Left - borders.Right) * density),
                    (int)MathF.Floor((usablePoints.H - borders.Top - borders.Bottom) * density));
        }

        public string DisplayModes
        {
            get
            {
                var modes = new List<(int, int)>();
                int n;
                SDL_DisplayMode** list = SDL_GetFullscreenDisplayModes(SDL_GetDisplayForWindow(_w), &n);
                if (list != null)
                {
                    for (int i = 0; i < n; i++)
                        modes.Add(((int)MathF.Round(list[i]->w * list[i]->pixel_density), (int)MathF.Round(list[i]->h * list[i]->pixel_density)));
                    SDL_free(list);
                }
                return Fullscreen ? ModeList(modes, Desktop(), true) : ModeList(modes, WindowMax(), false);
            }
        }

        // "WxH" space-separated by ascending area (as GetRes): fullscreen has only the desktop (`limit`); windowed has
        // modes that fit `limit` (WindowMax), without repeats, plus `limit` itself.
        public static string ModeList(IEnumerable<(int W, int H)> modes, (int W, int H) limit, bool fullscreen)
        {
            if (fullscreen) return $"{limit.W}x{limit.H}";
            var set = new HashSet<(int, int)> { limit };
            foreach (var (w, h) in modes) if (w > 0 && h > 0 && w <= limit.W && h <= limit.H) set.Add((w, h));
            return string.Join(" ", set.OrderBy(m => (long)m.Item1 * m.Item2).ThenBy(m => m.Item1).Select(m => $"{m.Item1}x{m.Item2}"));
        }

        // Startup window (UGameEngine::Init -> OpenWindow): Core's chain UEViewportStartup.Open picks the mode, falls back
        // from fullscreen to windowed (WinDrv.dll.c:4909-4921) and saves the APPLIED size into the mode's key pair
        // (WinDrv.dll.c:15366-15379). debugSize (--size) is a debug window outside the shipped chain: not saved.
        public static void OpenStartup(IViewportDevice vp, UEConfig config, string commandLine, (int W, int H)? debugSize)
        {
            if (debugSize is { } s) vp.Resize(s.W, s.H, 0, false);
            else UEViewportStartup.Process.Open(config, commandLine, vp);
        }

        // Called after a switch the window really reached, with whether the mode differs from before (the frontend
        // re-applies the mouse capture only then).
        public Action<bool> ModeApplied;

        // Synchronous (SDL_SyncWindow), as the original ResizeViewport: the outcome is known on return, Core stores it.
        public ViewportResizeOutcome Resize(int sizeX, int sizeY, int colorBytes, bool fullscreen)
        {
            var before = (SizeX, SizeY, Fullscreen);
            if (fullscreen)
            {
                if (!SDL_SetWindowFullscreenMode(_w, null) || !SDL_SetWindowFullscreen(_w, true)) return ViewportResizeOutcome.Rejected;
            }
            else
            {
                if (Fullscreen && !SDL_SetWindowFullscreen(_w, false)) return ViewportResizeOutcome.Rejected;
                SDL_SyncWindow(_w);
                float d = SDL_GetWindowPixelDensity(_w);
                if (!(d > 0)) d = 1f;
                var (mw, mh) = WindowMax();
                int w = Math.Min(sizeX, mw), h = Math.Min(sizeY, mh);
                if (!SDL_SetWindowSize(_w, (int)MathF.Round(w / d), (int)MathF.Round(h / d))) return ViewportResizeOutcome.Rejected;
            }
            SDL_SyncWindow(_w);
            // The platform may finish the mode switch after SyncWindow returns (macOS animates fullscreen); Core stores the
            // mode it reads back, so wait until the window really is in the requested mode (bounded) before reporting.
            // LIMITATIONS: the wait runs inside the Core tick (ToggleFullscreen / SetRes / startup come from the VM),
            // so for up to 3 s (the macOS animation, ~0.5-1 s) no frame is drawn and no audio is mixed. SDL reports no
            // asynchronous failure of a fullscreen request: "not in the mode after 3 s" is the only Rejected signal here, so
            // where the window manager defers the switch (X11/Wayland: window minimised or on another desktop) a request
            // that is still pending is reported Rejected — ToggleFullscreen then saves nothing, the startup chain falls back
            // to windowed and saves StartupFullscreen=False — while the window may enter fullscreen later. Not checked on
            // Linux; a pending-vs-failed signal needs a live X11/Wayland check (SDL_EVENT_WINDOW_ENTER/LEAVE_FULLSCREEN).
            for (int waited = 0; Fullscreen != fullscreen && waited < 3000; waited += 10)
            {
                SDL_PumpEvents();
                SDL_Delay(10);
            }
            if (Fullscreen != fullscreen) return ViewportResizeOutcome.Rejected;
            ModeApplied?.Invoke((SizeX, SizeY, Fullscreen) != before);
            return ViewportResizeOutcome.Applied;
        }
    }
}
