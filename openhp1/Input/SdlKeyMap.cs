// SdlKeyMap: SDL3 physical key -> UE1 key name (EInputKey without the IK_ prefix).
//
// Table order is significant: it sets the order of the per-level game input sweep and the order of menu keys.
// DeviceKeys = table + Escape.
//
// The table is by SCANCODE (US key position), but letters, digits and OEM signs follow the OS LAYOUT:
// WinDrv passes WM_KEYDOWN wParam to CauseInputEvent, a Windows virtual key code that depends on the layout
// (WinDrv.dll.c:3341, disassembly 0x1110b2f3-0x1110b2fa). LayoutScancode maps a physical key to the US scancode
// of the key whose character the layout produced (SDL_Keycode without modifiers; by default SDL3 makes the French
// digit row yield digits and non-Latin letters yield Latin by US position, as Windows VK does).
// A layout sign absent from the table's ASCII (ö, ü, ^, <) stays at its physical position: Windows assigns
// VK_OEM_* for those via its layout table, which is not reproduced here.
//
// Left and right Shift/Ctrl/Alt give ONE name (as VK_SHIFT/VK_CONTROL/VK_MENU in WinDrv); these are two physical
// controls with one name, and InputStage keeps the per-name pressed counter.
//
// Digits: physical digit i -> name "i" (IK_0..IK_9 = VK '0'..'9').
//
// An unknown key is not silent: NameOf returns null, and InputStage counts it by scancode
// (InputStage.UnknownScancodes) and warns on first appearance.
using System.Collections.Generic;
using SDL;

namespace OpenHP1.Input
{
    // Physical control: keyboard key (scancode) or mouse button (SDL_BUTTON_* number).
    public readonly record struct SdlControl(bool Mouse, int Code)
    {
        public static SdlControl Key(SDL_Scancode sc) => new(false, (int)sc);
        public static SdlControl Button(int sdlButton) => new(true, sdlButton);
        public override string ToString() => Mouse ? $"mouse:{Code}" : $"key:{(SDL_Scancode)Code}";
    }

    public static class SdlKeyMap
    {
        // Key table, in reference order.
        public static readonly IReadOnlyList<(SdlControl Control, string Name)> Keys = Build();

        // DeviceKeys: table + Escape at the end.
        public static readonly IReadOnlyList<(SdlControl Control, string Name)> DeviceKeys = BuildDevice();

        // DeviceKeys names without repeats, in order of first appearance: the order in which held names are
        // enumerated after clear+fill.
        public static readonly IReadOnlyList<string> DeviceNames = BuildNames();

        static readonly Dictionary<SdlControl, string> ByControl = BuildIndex();

        public static readonly SdlControl EscapeControl = SdlControl.Key(SDL_Scancode.SDL_SCANCODE_ESCAPE);
        public static readonly SdlControl LeftAltControl = SdlControl.Key(SDL_Scancode.SDL_SCANCODE_LALT);
        public static readonly SdlControl RightAltControl = SdlControl.Key(SDL_Scancode.SDL_SCANCODE_RALT);
        public static readonly SdlControl LeftMouseControl = SdlControl.Button(SDL3.SDL_BUTTON_LEFT);
        public static readonly SdlControl RightMouseControl = SdlControl.Button(SDL3.SDL_BUTTON_RIGHT);

        // UE1 name of a physical control from DeviceKeys; null means the table has no such entry.
        public static string NameOf(SdlControl c) => ByControl.TryGetValue(c, out string n) ? n : null;
        public static string NameOf(SDL_Scancode sc) => NameOf(SdlControl.Key(sc));
        public static string NameOfMouseButton(int sdlButton) => NameOf(SdlControl.Button(sdlButton));

        static List<(SdlControl, string)> Build()
        {
            var list = new List<(SdlControl, string)>();
            void K(SDL_Scancode sc, string name) => list.Add((SdlControl.Key(sc), name));
            // A..Z (SDL scancodes A..Z are contiguous, 4..29).
            for (int i = 0; i < 26; i++)
                K(SDL_Scancode.SDL_SCANCODE_A + i, ((char)('A' + i)).ToString());
            // Digits 0..9 (declared value, see "Digits" above).
            // SDL digits go 1..9, 0, hence by name, not arithmetic.
            K(SDL_Scancode.SDL_SCANCODE_0, "0"); K(SDL_Scancode.SDL_SCANCODE_1, "1");
            K(SDL_Scancode.SDL_SCANCODE_2, "2"); K(SDL_Scancode.SDL_SCANCODE_3, "3");
            K(SDL_Scancode.SDL_SCANCODE_4, "4"); K(SDL_Scancode.SDL_SCANCODE_5, "5");
            K(SDL_Scancode.SDL_SCANCODE_6, "6"); K(SDL_Scancode.SDL_SCANCODE_7, "7");
            K(SDL_Scancode.SDL_SCANCODE_8, "8"); K(SDL_Scancode.SDL_SCANCODE_9, "9");
            // NumPad0..NumPad9 (SDL KP_1..KP_9, KP_0, also by name).
            K(SDL_Scancode.SDL_SCANCODE_KP_0, "NumPad0"); K(SDL_Scancode.SDL_SCANCODE_KP_1, "NumPad1");
            K(SDL_Scancode.SDL_SCANCODE_KP_2, "NumPad2"); K(SDL_Scancode.SDL_SCANCODE_KP_3, "NumPad3");
            K(SDL_Scancode.SDL_SCANCODE_KP_4, "NumPad4"); K(SDL_Scancode.SDL_SCANCODE_KP_5, "NumPad5");
            K(SDL_Scancode.SDL_SCANCODE_KP_6, "NumPad6"); K(SDL_Scancode.SDL_SCANCODE_KP_7, "NumPad7");
            K(SDL_Scancode.SDL_SCANCODE_KP_8, "NumPad8"); K(SDL_Scancode.SDL_SCANCODE_KP_9, "NumPad9");
            // F1..F12 (contiguous in SDL, 58..69).
            for (int i = 1; i <= 12; i++) K(SDL_Scancode.SDL_SCANCODE_F1 + (i - 1), "F" + i);
            // HP1KeyTable.cs:26-41.
            K(SDL_Scancode.SDL_SCANCODE_SPACE, "Space"); K(SDL_Scancode.SDL_SCANCODE_TAB, "Tab");
            K(SDL_Scancode.SDL_SCANCODE_BACKSPACE, "Backspace");
            K(SDL_Scancode.SDL_SCANCODE_RETURN, "Enter"); K(SDL_Scancode.SDL_SCANCODE_KP_ENTER, "Enter");
            K(SDL_Scancode.SDL_SCANCODE_LSHIFT, "Shift"); K(SDL_Scancode.SDL_SCANCODE_RSHIFT, "Shift");
            K(SDL_Scancode.SDL_SCANCODE_LCTRL, "Ctrl"); K(SDL_Scancode.SDL_SCANCODE_RCTRL, "Ctrl");
            K(SDL_Scancode.SDL_SCANCODE_LALT, "Alt"); K(SDL_Scancode.SDL_SCANCODE_RALT, "Alt");
            K(SDL_Scancode.SDL_SCANCODE_PAUSE, "Pause"); K(SDL_Scancode.SDL_SCANCODE_CAPSLOCK, "CapsLock");
            K(SDL_Scancode.SDL_SCANCODE_PAGEUP, "PageUp"); K(SDL_Scancode.SDL_SCANCODE_PAGEDOWN, "PageDown");
            K(SDL_Scancode.SDL_SCANCODE_END, "End"); K(SDL_Scancode.SDL_SCANCODE_HOME, "Home");
            K(SDL_Scancode.SDL_SCANCODE_LEFT, "Left"); K(SDL_Scancode.SDL_SCANCODE_UP, "Up");
            K(SDL_Scancode.SDL_SCANCODE_RIGHT, "Right"); K(SDL_Scancode.SDL_SCANCODE_DOWN, "Down");
            K(SDL_Scancode.SDL_SCANCODE_PRINTSCREEN, "PrintScrn"); K(SDL_Scancode.SDL_SCANCODE_INSERT, "Insert");
            K(SDL_Scancode.SDL_SCANCODE_DELETE, "Delete");
            K(SDL_Scancode.SDL_SCANCODE_KP_MULTIPLY, "GreyStar"); K(SDL_Scancode.SDL_SCANCODE_KP_PLUS, "GreyPlus");
            K(SDL_Scancode.SDL_SCANCODE_KP_MINUS, "GreyMinus");
            K(SDL_Scancode.SDL_SCANCODE_KP_PERIOD, "NumPadPeriod"); K(SDL_Scancode.SDL_SCANCODE_KP_DIVIDE, "GreySlash");
            K(SDL_Scancode.SDL_SCANCODE_NUMLOCKCLEAR, "NumLock"); K(SDL_Scancode.SDL_SCANCODE_SCROLLLOCK, "ScrollLock");
            K(SDL_Scancode.SDL_SCANCODE_SEMICOLON, "Semicolon"); K(SDL_Scancode.SDL_SCANCODE_EQUALS, "Equals");
            K(SDL_Scancode.SDL_SCANCODE_COMMA, "Comma"); K(SDL_Scancode.SDL_SCANCODE_MINUS, "Minus");
            K(SDL_Scancode.SDL_SCANCODE_PERIOD, "Period"); K(SDL_Scancode.SDL_SCANCODE_SLASH, "Slash");
            K(SDL_Scancode.SDL_SCANCODE_LEFTBRACKET, "LeftBracket");
            K(SDL_Scancode.SDL_SCANCODE_BACKSLASH, "Backslash"); K(SDL_Scancode.SDL_SCANCODE_RIGHTBRACKET, "RightBracket");
            K(SDL_Scancode.SDL_SCANCODE_APOSTROPHE, "SingleQuote");
            K(SDL_Scancode.SDL_SCANCODE_GRAVE, "Tilde");
            // Mouse buttons.
            list.Add((SdlControl.Button(SDL3.SDL_BUTTON_LEFT), "LeftMouse"));
            list.Add((SdlControl.Button(SDL3.SDL_BUTTON_RIGHT), "RightMouse"));
            list.Add((SdlControl.Button(SDL3.SDL_BUTTON_MIDDLE), "MiddleMouse"));
            return list;
        }

        static List<(SdlControl, string)> BuildDevice()
        {
            var list = new List<(SdlControl, string)>(Keys) { (SdlControl.Key(SDL_Scancode.SDL_SCANCODE_ESCAPE), "Escape") };
            return list;
        }

        static List<string> BuildNames()
        {
            var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            var names = new List<string>();
            foreach (var (_, n) in DeviceKeys) if (seen.Add(n)) names.Add(n);
            return names;
        }

        // Keys whose Windows VK depends on the layout: letters, digit row, OEM signs.
        static readonly Dictionary<uint, SDL_Scancode> ByLayoutChar = BuildLayout();

        static Dictionary<uint, SDL_Scancode> BuildLayout()
        {
            var d = new Dictionary<uint, SDL_Scancode>();
            for (int i = 0; i < 26; i++) d['a' + (uint)i] = SDL_Scancode.SDL_SCANCODE_A + i;
            d['0'] = SDL_Scancode.SDL_SCANCODE_0;
            for (int i = 1; i <= 9; i++) d['0' + (uint)i] = SDL_Scancode.SDL_SCANCODE_1 + (i - 1);
            d[';'] = SDL_Scancode.SDL_SCANCODE_SEMICOLON; d['='] = SDL_Scancode.SDL_SCANCODE_EQUALS;
            d[','] = SDL_Scancode.SDL_SCANCODE_COMMA; d['-'] = SDL_Scancode.SDL_SCANCODE_MINUS;
            d['.'] = SDL_Scancode.SDL_SCANCODE_PERIOD; d['/'] = SDL_Scancode.SDL_SCANCODE_SLASH;
            d['['] = SDL_Scancode.SDL_SCANCODE_LEFTBRACKET; d['\\'] = SDL_Scancode.SDL_SCANCODE_BACKSLASH;
            d[']'] = SDL_Scancode.SDL_SCANCODE_RIGHTBRACKET; d['\''] = SDL_Scancode.SDL_SCANCODE_APOSTROPHE;
            d['`'] = SDL_Scancode.SDL_SCANCODE_GRAVE;
            return d;
        }

        static readonly HashSet<SDL_Scancode> LayoutPositions = new(ByLayoutChar.Values);

        // Physical key + layout character -> US scancode of the key with that character (Windows VK).
        // Other keys (F, arrows, NumPad, modifiers) are not changed by the layout.
        public static SDL_Scancode LayoutScancode(SDL_Scancode sc, SDL_Keycode key)
            => LayoutPositions.Contains(sc) && ByLayoutChar.TryGetValue((uint)key, out var us) ? us : sc;

        static Dictionary<SdlControl, string> BuildIndex()
        {
            var d = new Dictionary<SdlControl, string>();
            foreach (var (c, n) in DeviceKeys)
                if (!d.TryAdd(c, n))
                    throw new System.InvalidOperationException($"SdlKeyMap: control {c} named twice");
            return d;
        }
    }
}
