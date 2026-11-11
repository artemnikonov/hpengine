// Startup splash: the window HP.exe shows while the engine and the first map load. Which file and whether to show it are
// Core decisions (UELevelRuntime.SplashBitmapPath / ShowsSplash); this only draws it.
// HP.exe (decrypted image): dialog 119 = WS_POPUP | DS_CENTER (no frame), WS_EX_TOOLWINDOW (no taskbar button), one
// SS_BITMAP control at (0,0); SetWindowPos(HWND_TOPMOST, (SM_CXSCREEN - W) / 2, (SM_CYSCREEN - H) / 2, W, H) then
// HWND_NOTOPMOST (0x109027dc..0x10902835); the bitmap is drawn 1:1, no scaling. ExitSplash (0x10902aa0) after
// InitEngine closes it; no minimum time, input is ignored.
using System;
using SDL;
using static SDL.SDL3;

namespace OpenHP1
{
    static unsafe class Splash
    {
        static SDL_Window* _window;

        public static void Show(string bmpPath)
        {
            if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO)) throw new InvalidOperationException("splash: " + SDL_GetError());
            SDL_Surface* image = SDL_LoadBMP(bmpPath);
            if (image == null) throw new InvalidOperationException($"splash: LoadFileToBitmap failed for '{bmpPath}': {SDL_GetError()}");
            try
            {
                int w = image->w, h = image->h;
                _window = SDL_CreateWindow("HP1", w, h,
                    SDL_WindowFlags.SDL_WINDOW_BORDERLESS | SDL_WindowFlags.SDL_WINDOW_UTILITY | SDL_WindowFlags.SDL_WINDOW_HIDDEN);
                if (_window == null) throw new InvalidOperationException("splash: " + SDL_GetError());
                SDL_Rect screen;
                if (!SDL_GetDisplayBounds(SDL_GetPrimaryDisplay(), &screen))
                    throw new InvalidOperationException("splash: " + SDL_GetError());
                // cdq/sub/sar: signed division truncating toward zero, as C# int division.
                SDL_SetWindowPosition(_window, screen.x + (screen.w - w) / 2, screen.y + (screen.h - h) / 2);
                SDL_SetWindowAlwaysOnTop(_window, true);    // HWND_TOPMOST once ...
                SDL_ShowWindow(_window);
                SDL_SetWindowAlwaysOnTop(_window, false);   // ... then HWND_NOTOPMOST
                SDL_Surface* target = SDL_GetWindowSurface(_window);
                if (target == null || !SDL_BlitSurface(image, null, target, null) || !SDL_UpdateWindowSurface(_window))
                    throw new InvalidOperationException("splash: " + SDL_GetError());
                SDL_PumpEvents();   // let the window system put it on screen before the long synchronous load
            }
            finally { SDL_DestroySurface(image); }
        }

        public static void Close()
        {
            if (_window == null) return;
            SDL_DestroyWindow(_window);
            _window = null;
            SDL_PumpEvents();
        }
    }
}
