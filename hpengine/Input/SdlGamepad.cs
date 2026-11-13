// SdlGamepad: raw state of the first connected SDL3 gamepad (standard mapping) for Core's gamepad support
// (HP1.Core UEInput.Gamepad.cs — improvement, not original, author decision 2026-10-30). Only device state: sticks and
// triggers as SDL reports them, buttons as bits in SDL_GamepadButton order (Core names that order), the SDL gamepad type and
// the printed labels of the four face buttons (Core picks the Controls page names from them). What it means is Core's.
// Hot-plug: when no pad is open, every read looks for one; a pad that disconnects is closed and the read fails.
using System;
using System.Linq;
using HP1.UEScript;
using SDL;

namespace HPEngine.Input
{
    public static unsafe class SdlGamepad
    {
        static SDL_Gamepad* _pad;
        static bool _init, _initOk;

        public static bool Read(out UEInput.GamepadState s)
        {
            s = default;
            if (!_init)
            {
                _init = true;
                // winmm polls the device regardless of window focus; keep SDL reporting it in the background too.
                SDL3.SDL_SetHint(SDL3.SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS, "1");
                _initOk = SDL3.SDL_InitSubSystem(SDL_InitFlags.SDL_INIT_GAMEPAD);
                if (!_initOk) Console.Error.WriteLine("[Input] gamepad subsystem: " + SDL3.SDL_GetError());
            }
            if (!_initOk) return false;
            if (_pad != null && !SDL3.SDL_GamepadConnected(_pad)) { SDL3.SDL_CloseGamepad(_pad); _pad = null; }
            if (_pad == null)
            {
                if (!SDL3.SDL_HasGamepad()) return false;
                using var ids = SDL3.SDL_GetGamepads();
                if (ids is null || ids.Count == 0) return false;
                _pad = SDL3.SDL_OpenGamepad(ids[0]);
                if (_pad == null) return false;
                Console.WriteLine($"[Input] gamepad opened: {SDL3.SDL_GetGamepadName(_pad)} (of {ids.Count})");   // run log: which device Core reads
            }
            s.LeftX = SDL3.SDL_GetGamepadAxis(_pad, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTX);
            s.LeftY = SDL3.SDL_GetGamepadAxis(_pad, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTY);
            s.RightX = SDL3.SDL_GetGamepadAxis(_pad, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTX);
            s.RightY = SDL3.SDL_GetGamepadAxis(_pad, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTY);
            s.LeftTrigger = SDL3.SDL_GetGamepadAxis(_pad, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFT_TRIGGER);
            s.RightTrigger = SDL3.SDL_GetGamepadAxis(_pad, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHT_TRIGGER);
            s.Type = SDL3.SDL_GetGamepadType(_pad).ToString();
            s.Faces = string.Join(" ", new[] { SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST,
                    SDL_GamepadButton.SDL_GAMEPAD_BUTTON_WEST, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_NORTH }
                .Select(b => SDL3.SDL_GetGamepadButtonLabel(_pad, b).ToString().Replace("SDL_GAMEPAD_BUTTON_LABEL_", "")));
            for (int b = 0; b <= UEInput.PadDPadRight; b++)
                if (SDL3.SDL_GetGamepadButton(_pad, (SDL_GamepadButton)b)) s.Buttons |= 1u << b;
            return true;
        }
    }
}
