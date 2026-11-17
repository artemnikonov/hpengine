// The launcher's gamepad: the SDL3 library HPEngine ships beside it (HPEngine/SDL3.dll, libSDL3.so; no copy of its own),
// loaded at run time and polled by MainWindow.StartGamepad. No library or no pad: the window works by mouse and keyboard.
// Buttons in SDL_GamepadButton order (SOUTH 0 = A, EAST 1 = B, NORTH 3 = Y, START 6, LEFT/RIGHT_SHOULDER 9/10, DPAD 11..14:
// Logic.cs PadMap numbers); the left stick past half way counts as the d-pad (PadMap.Stick).
using System.Runtime.InteropServices;

namespace HPEngine.Launcher;

public static unsafe class Gamepad
{
    static delegate* unmanaged[Cdecl]<uint, byte> _init;
    static delegate* unmanaged[Cdecl]<void> _update;
    static delegate* unmanaged[Cdecl]<int*, uint*> _list;
    static delegate* unmanaged[Cdecl]<void*, void> _free;
    static delegate* unmanaged[Cdecl]<uint, nint> _open;
    static delegate* unmanaged[Cdecl]<nint, byte> _connected;
    static delegate* unmanaged[Cdecl]<nint, void> _close;
    static delegate* unmanaged[Cdecl]<nint, int, byte> _button;
    static delegate* unmanaged[Cdecl]<nint, int, short> _axis;
    static nint _pad;

    public static bool Load(string dir)
    {
        foreach (var n in new[] { "SDL3.dll", "libSDL3.so", "libSDL3.so.0", "libSDL3.dylib" })
            if (NativeLibrary.TryLoad(Path.Combine(dir, n), out var lib))
            {
                try
                {
                    _init = (delegate* unmanaged[Cdecl]<uint, byte>)NativeLibrary.GetExport(lib, "SDL_InitSubSystem");
                    _update = (delegate* unmanaged[Cdecl]<void>)NativeLibrary.GetExport(lib, "SDL_UpdateGamepads");
                    _list = (delegate* unmanaged[Cdecl]<int*, uint*>)NativeLibrary.GetExport(lib, "SDL_GetGamepads");
                    _free = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(lib, "SDL_free");
                    _open = (delegate* unmanaged[Cdecl]<uint, nint>)NativeLibrary.GetExport(lib, "SDL_OpenGamepad");
                    _connected = (delegate* unmanaged[Cdecl]<nint, byte>)NativeLibrary.GetExport(lib, "SDL_GamepadConnected");
                    _close = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(lib, "SDL_CloseGamepad");
                    _button = (delegate* unmanaged[Cdecl]<nint, int, byte>)NativeLibrary.GetExport(lib, "SDL_GetGamepadButton");
                    _axis = (delegate* unmanaged[Cdecl]<nint, int, short>)NativeLibrary.GetExport(lib, "SDL_GetGamepadAxis");
                }
                catch (EntryPointNotFoundException) { return false; }
                return _init(0x2000u) != 0;   // SDL_INIT_GAMEPAD
            }
        return false;
    }

    // The pressed buttons as bits (1 << button); null: no pad connected.
    public static uint? Read()
    {
        _update();
        if (_pad != 0 && _connected(_pad) == 0) { _close(_pad); _pad = 0; }
        if (_pad == 0)
        {
            int n; uint* ids = _list(&n);
            if (ids != null) { if (n > 0) _pad = _open(ids[0]); _free(ids); }
            if (_pad == 0) return null;
        }
        uint bits = 0;
        for (int b = 0; b <= PadMap.Right; b++) if (_button(_pad, b) != 0) bits |= 1u << b;
        return bits | PadMap.Stick(_axis(_pad, 0) / 32767.0, _axis(_pad, 1) / 32767.0);
    }
}
