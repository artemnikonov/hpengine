// GAMEPAD SUPPORT — IMPROVEMENT, NOT ORIGINAL (author decision 2026-10-30; improvements list D-75 in
// audit/DEFERRED_DEBT_2026-10-04.md).
//
// The original polls the winmm joystick (WinDrv UpdateInput, ported exactly in UEInput.cs), but the shipped DefUser.ini
// leaves every Joy* binding empty, so a pad does nothing there, and the console-only keys (Escape opens the book, Space
// skips/throws/closes, the menus need a mouse) are out of a joystick's reach. Here a standard gamepad (the frontend's raw
// SDL gamepad state, GamepadState) is turned into input that then follows the SHIPPED path unchanged:
//   * through the ported joystick block: buttons -> IK_Joy1..12, left stick -> JoyX/JoyY (the WinDrv dead zone and
//     ScaleXYZ from the ini), d-pad -> JoyPovUp/Down/Left/Right;
//   * "virtual keys" for what only the console reads: East (B) -> Space, Start -> Escape; in a UWindow menu South (A) ->
//     LeftMouse and East (B) -> Escape;
//   * right stick -> MouseX/MouseY axis events (camera via the MouseX/MouseY bindings; in a menu WindowConsole moves the
//     cursor with them), through the WinDrv dead zone, scale [OpenHP1] GamepadLookSpeed (pixels per poll at full tilt);
//   * port default bindings for the Joy* keys (GamepadDefaultBindings): a FALLBACK of the key dispatch for a Joy* key the
//     user's [Engine.Input] leaves empty, active only while a gamepad is connected (from the poll that reads it to the end
//     of the poll that finds it gone, so the final loop's releases still run their Button commands). They are not bindings: nothing is written into the config and KEYBINDING (the Options "Controls" page,
//     FEOptionsPage.LoadExistingKeys) does not see them, so a keyboard-only player sees the original page; a key the user
//     binds overrides its default. [OpenHP1] GamepadDefaults=False turns the fallback off.
// Hot-plug: the frontend reports a pad whenever one is connected; a disconnect makes the read fail, and the final loop of
// UpdateInput releases every held Joy slot and virtual key in KeyDownTable through the normal path.
using System;
using System.Collections.Generic;
using UELib.Core;

namespace HP1.UEScript
{
    public sealed partial class UEInput
    {
        // Raw state of a standard gamepad: sticks and triggers in SDL units (sticks -32768..32767, down/right = +; triggers
        // 0..32767), Buttons bit i = standard button i (the SDL_GamepadButton order below).
        public struct GamepadState { public short LeftX, LeftY, RightX, RightY, LeftTrigger, RightTrigger; public uint Buttons; }
        public delegate bool GamepadRead(out GamepadState state);   // false: no pad connected

        public const int PadSouth = 0, PadEast = 1, PadWest = 2, PadNorth = 3, PadBack = 4, PadGuide = 5, PadStart = 6,
            PadLeftStick = 7, PadRightStick = 8, PadLeftShoulder = 9, PadRightShoulder = 10,
            PadDPadUp = 11, PadDPadDown = 12, PadDPadLeft = 13, PadDPadRight = 14;

        // Pad button -> IK_Joy slot (1-based), the XInput order winmm reports (A B X Y LB RB Back Start LS RS); triggers are
        // separate keys Joy11/Joy12 instead of winmm's shared Z axis. East and Start are virtual keys (no slot).
        static readonly (int Button, int Joy)[] JoySlots =
        {
            (PadSouth, 1), (PadWest, 3), (PadNorth, 4), (PadLeftShoulder, 5), (PadRightShoulder, 6),
            (PadBack, 7), (PadLeftStick, 9), (PadRightStick, 10),
        };
        const int JoyLeftTrigger = 11, JoyRightTrigger = 12;
        // A trigger counts as pressed past the WinDrv dead-zone fraction (0.2 of its range, .rdata 0x111146b8).
        const int TriggerPressed = (int)(0.2 * 32767);

        // Port default bindings for the Joy* keys, built from the shipped DefUser.ini aliases and key commands: Jump (Ctrl),
        // AltFire = cast (Alt), LookToggle (Enter), broom brake/boost (Z/A), the arrow commands on the d-pad, analog movement
        // on the left stick (full tilt = the keyboard's Hold rate at 60 Hz logic: MoveForward 300 -> 0.5, RotRight 150 -> 0.25,
        // StrafeRight 300 -> 0.5; Axis adds Delta*Speed*0.01 per poll with Delta up to ScaleXYZ = 1000).
        public static readonly (string Key, string Command)[] GamepadDefaultBindings =
        {
            ("Joy1", "Jump"),
            ("Joy3", "AltFire"),
            ("Joy4", "LookToggle"),
            ("Joy5", "Button bBroomBrake"),
            ("Joy6", "Button bBroomBoost"),
            ("Joy12", "AltFire"),
            ("JoyPovUp", "MoveForward | Button bBroomPitchUp"),
            ("JoyPovDown", "MoveBackward | Button bBroomPitchDown"),
            ("JoyPovLeft", "RotLeft | StrafeLeft | Button bBroomYawLeft"),
            ("JoyPovRight", "RotRight | StrafeRight | Button bBroomYawRight"),
            ("JoyX", "Axis aBaseX Speed=0.25 | Axis aStrafe Speed=0.5"),
            ("JoyY", "Axis aBaseY Speed=0.5"),
        };
        public const string GamepadDefaultsKey = "GamepadDefaults", GamepadLookSpeedKey = "GamepadLookSpeed";
        const float DefaultLookSpeed = 8f;

        // The console's UWindow (menu) is up; a test seam for the menu layout without running the menu script.
        internal static Func<UEObject, bool> MenuActive = c => c is not null && UEInterpreter.ToBool(c.GetProp("bUWindowActive"));

        GamepadRead _gamepad;
        bool _padRead;   // this poll's read; _padConnected follows it at the end of UpdateInput
        float _lookSpeed;
        readonly HashSet<string> _padVirtualDown = new(UEPropNameComparer.Instance);
        // What a held South/East/Start produces, chosen when the button went down ("" = its Joy slot), so opening or
        // closing a menu while it is held does not turn one press into another key.
        readonly string[] _padLatch = new string[15];
        uint _padPrev;

        static string VirtualOf(int button, bool menu) => button switch
        {
            PadSouth => menu ? "LeftMouse" : "",
            PadEast => menu ? "Escape" : "Space",
            PadStart => "Escape",
            _ => "",
        };

        // The gamepad takes the joystick's place: the same UseJoystick / -NoJoy gate and client keys (InitJoystick), with the
        // caps of a standard pad (16 buttons, a 4-direction POV, no Z/R/U/V axes: the right stick is the mouse here).
        public void InitGamepad(UEConfig config, string commandLine, GamepadRead read)
        {
            InitJoystick(config, commandLine, () => new JoyCaps { NumButtons = 16, Caps = 0x30 }, null);
            if (_joyCaps.NumButtons == 0) return;
            _gamepad = read;
            _padDefaults = !string.Equals(config.Get(UEConfig.ScopeSystem, UEConfig.OpenHP1Section, GamepadDefaultsKey), "False",
                StringComparison.OrdinalIgnoreCase);
            string raw = config.Get(UEConfig.ScopeSystem, UEConfig.OpenHP1Section, GamepadLookSpeedKey);
            _lookSpeed = raw is null ? DefaultLookSpeed : UEValue.LeadingAtof(raw);
        }

        // Key dispatch (UInput::Process, the Hold pass): the user's binding, else — with a gamepad connected — the pad default.
        bool _padDefaults, _padConnected;
        internal bool TryGetDispatchBinding(string key, out string command)
        {
            if (_bindings.TryGetValue(key, out command)) return true;
            if (_padDefaults && _gamepad is not null && _padConnected)
                foreach (var (k, c) in GamepadDefaultBindings)
                    if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) { command = c; return true; }
            return false;
        }

        // Before the joystick block: the virtual keys and the right stick, then the JoyInfo the ported block consumes.
        bool PollGamepad(UEInterpreter interp, UEObject console, UEObject pawn, out JoyInfo j)
        {
            j = default;
            _padRead = _gamepad(out GamepadState g);
            if (_padRead) _padConnected = true;
            if (!_padRead)
            {
                // Disconnected: nothing is marked handled, so the final loop releases every held Joy slot and virtual key that is
                // in KeyDownTable through the keyboard state, as for any device key; a key the console consumed was never in
                // the table and gets no Release, as with the shipped final loop.
                _padVirtualDown.Clear();
                Array.Clear(_padLatch);
                _padPrev = 0;
                return false;
            }
            bool Has(int b) => (g.Buttons & (1u << b)) != 0;
            bool menu = MenuActive(console);

            foreach (int b in new[] { PadSouth, PadEast, PadStart })
            {
                bool was = (_padPrev & (1u << b)) != 0;
                if (Has(b) && !was) _padLatch[b] = VirtualOf(b, menu);
                else if (!Has(b)) _padLatch[b] = null;
            }
            _padPrev = g.Buttons;
            bool Latched(string key) => _padLatch[PadSouth] == key || _padLatch[PadEast] == key || _padLatch[PadStart] == key;
            // Virtual keys in IK order (LeftMouse 1, Escape 27, Space 32): an edge of the pad state, not KeyDownTable (the
            // console consumes Escape, so the table would re-press it every poll); held -> slot handled, so the final loop
            // does not release it through the keyboard state.
            Virtual(interp, console, pawn, "LeftMouse", 1, Latched("LeftMouse"));
            Virtual(interp, console, pawn, "Escape", 27, Latched("Escape"));
            Virtual(interp, console, pawn, "Space", 32, Latched("Space"));

            IReadOnlyList<string> names = KeyNamesByIndex();
            float mx = JoystickAxis((uint)(g.RightX + 32768), _lookSpeed, true);
            float my = JoystickAxis((uint)(g.RightY + 32768), -_lookSpeed, true);   // mouse Y: up = +
            if (mx != 0f) InputEvent(interp, console, pawn, names[0xe4], InputAction.Axis, mx);
            if (my != 0f) InputEvent(interp, console, pawn, names[0xe5], InputAction.Axis, my);

            uint joy = 0;
            foreach (var (button, slot) in JoySlots)
                if (Has(button) && (button != PadSouth || _padLatch[PadSouth] == "")) joy |= 1u << (slot - 1);
            if (g.LeftTrigger > TriggerPressed) joy |= 1u << (JoyLeftTrigger - 1);
            if (g.RightTrigger > TriggerPressed) joy |= 1u << (JoyRightTrigger - 1);
            j.Buttons = joy;
            j.X = (uint)(g.LeftX + 32768);
            j.Y = (uint)(g.LeftY + 32768);
            j.Z = j.R = j.U = j.V = 32768;
            j.Pov = Has(PadDPadUp) ? 0u : Has(PadDPadRight) ? 9000u : Has(PadDPadDown) ? 18000u : Has(PadDPadLeft) ? 27000u : 0xFFFFu;
            return true;
        }

        void Virtual(UEInterpreter interp, UEObject console, UEObject pawn, string key, int ik, bool held)
        {
            if (held)
            {
                if (_padVirtualDown.Add(key)) InputEvent(interp, console, pawn, key, InputAction.Press, 0f);
                _handled[ik] = true;
            }
            else if (_padVirtualDown.Remove(key)) InputEvent(interp, console, pawn, key, InputAction.Release, 0f);
        }
    }
}
