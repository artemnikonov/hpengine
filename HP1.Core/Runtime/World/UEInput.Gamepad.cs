// GAMEPAD SUPPORT — IMPROVEMENT, NOT ORIGINAL (author decision 2026-10-30; improvements list D-75 in
// audit/DEFERRED_DEBT_2026-10-04.md).
//
// The original polls the winmm joystick (WinDrv UpdateInput, ported exactly in UEInput.cs), but the shipped DefUser.ini
// leaves every Joy* binding empty, so a pad does nothing there, and the console-only keys (Escape opens the book, Space
// skips/throws/closes, the menus need a mouse) are out of a joystick's reach. Here a standard gamepad (the frontend's raw
// SDL gamepad state, GamepadState) is turned into input that then follows the SHIPPED path unchanged:
//   * through the ported joystick block: buttons -> IK_Joy1..12, d-pad -> JoyPovUp/Down/Left/Right;
//   * "virtual keys" for what only the console reads: East (B) -> Space, Start -> Escape; in a UWindow menu South (A) ->
//     LeftMouse and East (B) -> Escape;
//   * right stick -> MouseX/MouseY axis events (camera via the MouseX/MouseY bindings; in a menu WindowConsole moves the
//     cursor with them), through the WinDrv dead zone, scale [HPEngine] GamepadLookSpeed (pixels per poll at full tilt);
//     in a menu also times MouseSensitivity / its shipped default (MenuCursorSensitivity; author decision 2026-11-13);
//   * in a menu the d-pad snaps the cursor to controls, steps sliders and open lists; B closes an open list (UEInput.MenuNav.cs).
//
// ONE BINDING MODEL (author decision 2026-11-13). Every rebindable pad control is a key with one effective binding:
//   * the left stick is four ANALOG keys Joy13 (up), Joy14 (down), Joy15 (left), Joy16 (right) — slots no pad button uses —
//     and nothing else: the pad feeds no JoyX/JoyY. A direction is down while the stick is past the WinDrv dead zone that
//     way; while it is held, every poll runs its binding as an axis event scaled by the tilt (PollStickAnalog), so an Axis
//     alias (MoveForward) is analog and a Button command is a plain press;
//   * a key's effective binding is the user's [Engine.Input] line, else its port default (GamepadDefaultBindings; the stick
//     directions carry the arrow keys' commands, broom Buttons included; the d-pad has no default). The defaults are a fallback of the key dispatch while a
//     gamepad is connected (from the poll that reads it to the end of the poll that finds it gone, so the final loop's
//     releases still run their Button commands) and write nothing: a keyboard-only player's config is untouched;
//   * the first SET Input of a pad key (the Controls page binding or clearing one) writes every default the user's config
//     leaves empty into [Engine.Input] as an ordinary binding and sets [HPEngine] GamepadBindingsSaved=True: from then on
//     the fallback is off for this config, so a cleared key stays unbound and a rebound key does only its new command
//     (KeepPadDefaults). [HPEngine] GamepadDefaults=False: no defaults at all. The Launcher's "Reset Gamepad Bindings"
//     undoes it (PAD RESET RULE below);
//   * the Options "Controls" page shows one device at a time (ControlsLoad): in PAD VIEW — a gamepad is connected and the
//     last key or axis input came from it — the rows list the pad keys' effective bindings under the pad type's own names
//     (GamepadKeyNames), key capture takes only pad keys (A is its own key while the page waits for one) and two hint lines
//     name the fixed buttons; otherwise the page is the original's, without the pad keys, and takes only non-pad keys.
//
// PAD RESET RULE (the same text in launcher/Desktop/Logic.cs PadReset and launcher/Launcher.swift padRebound; the launchers
// edit the files, Core only reads them): the saved overlay lives in <game>/HPEngineData — DefUser.ini for the User scope,
// Default.ini for the System scope (UEConfig.FileStore). "Never rebound" = in DefUser.ini no line of [Engine.Input] whose
// key starts with "Joy" (any case), and in Default.ini no GamepadBindingsSaved line in [HPEngine]. Reset deletes exactly
// those lines and nothing else: the shipped DefUser.ini (every Joy* line empty) shows through again and the fallback is on.
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
        // Type: the SDL_GamepadType name; Faces: SDL_GetGamepadButtonLabel of South/East/West/North (label names without
        // "SDL_GAMEPAD_BUTTON_LABEL_", space-separated). Device facts only: Core picks the Controls page labels from them.
        public struct GamepadState { public short LeftX, LeftY, RightX, RightY, LeftTrigger, RightTrigger; public uint Buttons; public string Type, Faces; }
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

        // Player names of the pad keys on the Options "Controls" page (D-75 addition 2026-11-12, NOT ORIGINAL), DISPLAY ONLY.
        // The page draws a binding as LocalizedKeyName[IK] (FEOptionsPage.BeforePaint, uc:328/332), filled from Pickup2.int
        // localized_keyname_NNN by LocalizeStrings (uc:113-121; shipped text "Joy1", "JoyPovUp"...); it binds through
        // RealKeyName[IK] = KEYNAME (LoadExistingKeys uc:471-476, ProcessKey/SetKey uc:704-775 -> SET Input / KEYBINDING),
        // which never reads LocalizedKeyName. So the text changes and every key name stays (UEPortScriptOverrides
        // OptionsLocalizeKeyNames writes these after the shipped body). Built from JoySlots and the POV mapping below, so a
        // label is the control that really produces the key. Joy13-16 are the left stick directions. Joy2,
        // Joy8 (East and Start are virtual keys, no slot) and JoyX/Y/Z/R/U/V: no pad control produces them; the shipped text stays.
        // The labels follow the connected pad's type, which the frontend reports with the state (GamepadState.Type = the SDL
        // gamepad type name, Faces = SDL's printed labels of the South/East/West/North buttons); Core keeps the last one read.
        // The page takes the names when it is created and whenever it switches to the pad view. With no pad ever read, the
        // Xbox set. "Pad " in front of every button: a bare "A", "X", "Y" or "L" is also a keyboard key.
        //   Xbox (and Standard / Unknown / Steam / other):  A B X Y, View Menu, LS RS, LB RB, LT RT — face letters from Faces
        //       when SDL gives them (Steam Deck: A B X Y);
        //   PlayStation (PS3/PS4/PS5):  Cross Circle Square Triangle, Select / Share / Create, Start / Options, L3 R3, L1 R1, L2 R2;
        //   Nintendo Switch Pro / Joy-Con pair:  printed B (south), A (east), Y (west), X (north), - +, LS RS, L R, ZL ZR
        //       (SDL's buttons are positional: South is the bottom button whatever it is printed).
        // Index: the SDL_GamepadButton order (PadSouth..PadDPadRight), then 15 = left trigger, 16 = right trigger.
        static readonly string[] XboxLabels =
        {
            "Pad A", "Pad B", "Pad X", "Pad Y", "Pad View", "Pad Guide", "Pad Menu", "Pad LS", "Pad RS", "Pad LB", "Pad RB",
            "D-pad Up", "D-pad Down", "D-pad Left", "D-pad Right", "Pad LT", "Pad RT",
        };
        internal static string[] PadLabelsFor(string type, string faces)
        {
            var l = (string[])XboxLabels.Clone();
            void Set(params (int I, string Label)[] xs) { foreach (var (i, label) in xs) l[i] = "Pad " + label; }
            switch (type)
            {
                case "SDL_GAMEPAD_TYPE_PS3": case "SDL_GAMEPAD_TYPE_PS4": case "SDL_GAMEPAD_TYPE_PS5":
                    Set((PadSouth, "Cross"), (PadEast, "Circle"), (PadWest, "Square"), (PadNorth, "Triangle"),
                        (PadLeftStick, "L3"), (PadRightStick, "R3"), (PadLeftShoulder, "L1"), (PadRightShoulder, "R1"), (15, "L2"), (16, "R2"),
                        (PadBack, type == "SDL_GAMEPAD_TYPE_PS3" ? "Select" : type == "SDL_GAMEPAD_TYPE_PS4" ? "Share" : "Create"),
                        (PadStart, type == "SDL_GAMEPAD_TYPE_PS3" ? "Start" : "Options"));
                    break;
                case "SDL_GAMEPAD_TYPE_NINTENDO_SWITCH_PRO": case "SDL_GAMEPAD_TYPE_NINTENDO_SWITCH_JOYCON_PAIR":
                    Set((PadSouth, "B"), (PadEast, "A"), (PadWest, "Y"), (PadNorth, "X"), (PadBack, "-"), (PadStart, "+"),
                        (PadLeftShoulder, "L"), (PadRightShoulder, "R"), (15, "ZL"), (16, "ZR"));
                    break;
                default:
                    // SDL_GetGamepadButtonLabel for South/East/West/North, as SDL_GAMEPAD_BUTTON_LABEL_* names without the prefix.
                    string[] f = (faces ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length == 4)
                        for (int i = 0; i < 4; i++)
                            if (f[i] != "UNKNOWN") l[i] = "Pad " + (f[i].Length <= 1 ? f[i] : f[i][0] + f[i].Substring(1).ToLowerInvariant());
                    break;
            }
            return l;
        }
        string _padType, _padFaces;
        internal IEnumerable<(string Key, string Name)> GamepadKeyNames() => GamepadKeyNames(_padType, _padFaces);
        internal static IEnumerable<(string Key, string Name)> GamepadKeyNames(string type, string faces)
        {
            string[] l = PadLabelsFor(type, faces);
            foreach (var (button, slot) in JoySlots) yield return ("Joy" + slot, l[button]);
            yield return ("Joy" + JoyLeftTrigger, l[15]);
            yield return ("Joy" + JoyRightTrigger, l[16]);
            yield return ("JoyPovUp", l[PadDPadUp]);
            yield return ("JoyPovDown", l[PadDPadDown]);
            yield return ("JoyPovLeft", l[PadDPadLeft]);
            yield return ("JoyPovRight", l[PadDPadRight]);
            yield return ("Joy" + JoyStickUp, "Left Stick Up");
            yield return ("Joy" + JoyStickDown, "Left Stick Down");
            yield return ("Joy" + JoyStickLeft, "Left Stick Left");
            yield return ("Joy" + JoyStickRight, "Left Stick Right");
        }
        // The fixed buttons, for the Controls page's pad-view hint lines: what the virtual keys, the right stick and the
        // menu navigation do (VirtualOf, PollGamepad, UEInput.MenuNav.cs), under the connected pad type's names without
        // the "Pad " the key rows need (the lines are about the pad).
        internal string[] GamepadFixedHint() => GamepadFixedHint(_padType, _padFaces);
        internal static string[] GamepadFixedHint(string type, string faces)
        {
            string[] l = PadLabelsFor(type, faces);
            return new[]
            {
                $"{l[PadEast].Substring(4)}: skip, back   {l[PadStart].Substring(4)}: book   Right Stick: camera",
                $"{l[PadSouth].Substring(4)}: select   D-pad: menus",
            };
        }
        // A trigger counts as pressed past the WinDrv dead-zone fraction (0.2 of its range, .rdata 0x111146b8).
        const int TriggerPressed = (int)(0.2 * 32767);
        const int JoyStickUp = 13, JoyStickDown = 14, JoyStickLeft = 15, JoyStickRight = 16;

        // Port default bindings for the pad keys, built from the shipped DefUser.ini aliases and key commands: Jump (Ctrl),
        // AltFire = cast (Alt), LookToggle (Enter), broom brake/boost (Z/A), the arrow keys' commands on the left stick
        // directions. The d-pad has none (author decision 2026-11-13: with it the movement rows did not fit the page's key
        // buttons); the player may bind it, and in menus it navigates.
        public static readonly (string Key, string Command)[] GamepadDefaultBindings =
        {
            ("Joy1", "Jump"),
            ("Joy3", "AltFire"),
            ("Joy4", "LookToggle"),
            ("Joy5", "Button bBroomBrake"),
            ("Joy6", "Button bBroomBoost"),
            ("Joy12", "AltFire"),
            ("Joy13", "MoveForward | Button bBroomPitchUp"),
            ("Joy14", "MoveBackward | Button bBroomPitchDown"),
            ("Joy15", "RotLeft | StrafeLeft | Button bBroomYawLeft"),
            ("Joy16", "RotRight | StrafeRight | Button bBroomYawRight"),
        };
        // The pad's keys are the EInputKey Joy* family (Joy1-16, JoyX..JoyV, JoyPov*); no other key name starts with "Joy".
        internal static bool IsPadKey(string key) => key is not null && key.StartsWith("Joy", StringComparison.OrdinalIgnoreCase);
        public const string GamepadDefaultsKey = "GamepadDefaults", GamepadLookSpeedKey = "GamepadLookSpeed",
            GamepadBindingsSavedKey = "GamepadBindingsSaved";
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
            _padDefaults = !string.Equals(config.Get(UEConfig.ScopeSystem, UEConfig.HPEngineSection, GamepadDefaultsKey), "False",
                StringComparison.OrdinalIgnoreCase);
            _padSaved = string.Equals(config.Get(UEConfig.ScopeSystem, UEConfig.HPEngineSection, GamepadBindingsSavedKey), "True",
                StringComparison.OrdinalIgnoreCase);
            string raw = config.Get(UEConfig.ScopeSystem, UEConfig.HPEngineSection, GamepadLookSpeedKey);
            _lookSpeed = raw is null ? DefaultLookSpeed : UEValue.LeadingAtof(raw);
        }

        // Key dispatch (UInput::Process, the Hold pass): the user's binding, else — with a gamepad connected and the
        // defaults not yet saved into the config — the pad default.
        bool _padDefaults, _padSaved, _padConnected;
        internal bool TryGetDispatchBinding(string key, out string command)
        {
            if (_bindings.TryGetValue(key, out command)) return true;
            if (_padDefaults && !_padSaved && _gamepad is not null && _padConnected)
                foreach (var (k, c) in GamepadDefaultBindings)
                    if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) { command = c; return true; }
            return false;
        }

        // SET Input <pad key>, before the write (UEConsoleCommand): the first one turns the fallback into ordinary bindings —
        // every default the user's config leaves empty is bound and saved, and GamepadBindingsSaved switches the fallback
        // off for this config. DefUser.ini ships every Joy* line empty, so without this an emptied line could not mean
        // "cleared": the key would fall back to its default.
        internal void KeepPadDefaults(UEConfig config, string key)
        {
            if (config is null || _gamepad is null || !_padDefaults || _padSaved || !IsPadKey(key)) return;
            foreach (var (k, c) in GamepadDefaultBindings)
                if (!_bindings.ContainsKey(k))
                {
                    _bindings[k] = c;
                    config.Set(UEConfig.ScopeUser, UEConsoleCommand.InputSection, k, c);
                }
            config.Set(UEConfig.ScopeSystem, UEConfig.HPEngineSection, GamepadBindingsSavedKey, "True");
            _padSaved = true;
        }

        // Which device the player is using: the last key press or axis movement came from the gamepad's poll (_inPadPoll).
        bool _inPadPoll, _padLast;
        internal bool PadView => _gamepad is not null && _padConnected && _padLast;

        // The Controls page's key list (FEOptionsPage.LoadExistingKeys asks KEYBINDING for all 255 keys): one device per
        // view. Pad view: the pad keys' effective bindings; keyboard view: the original list without the pad keys.
        UEObject _controlsPage;
        bool _controlsPad, _controlsLoading;
        internal void ControlsLoad(UEObject page, Action shippedBody)
        {
            _controlsPage = page; _controlsPad = PadView; _controlsLoading = true;
            try { shippedBody(); } finally { _controlsLoading = false; }
        }
        internal bool ControlsShowsPad(UEObject page) => ReferenceEquals(page, _controlsPage) && _controlsPad;
        internal bool TryGetKeyBindingAnswer(string key, out string command)
        {
            if (!_controlsLoading) return TryGetBinding(key, out command);
            command = null;
            if (IsPadKey(key) != _controlsPad) return false;
            return _controlsPad ? TryGetDispatchBinding(key, out command) : TryGetBinding(key, out command);
        }
        // The shown pad-view page waits for a key (bPolling): A is then its own key Joy1, not the menu click, and the d-pad
        // does not move the cursor, so both can be bound.
        bool _dpadCaptured;
        bool ControlsCapturing() => _controlsPad && _controlsPage is not null
            && UEInterpreter.ToBool(_controlsPage.GetProp("bPolling")) && UEInterpreter.ToBool(_controlsPage.GetProp("bWindowVisible"));

        // Left stick, analog part: every poll a held direction runs its binding as an axis event whose delta is the tilt
        // through the WinDrv axis formula (JoystickAxis: dead zone, ScaleXYZ — 1000 at full tilt in the shipped ini). Under it
        // an AXIS command's Speed is divided by AnalogSpeedDivisor (UEInput.Exec): Hold adds dt*Speed, the axis event adds
        // delta*Speed*0.01, so Speed/600 makes full tilt add Speed/60 — the keyboard's Hold amount of one 60 Hz tick
        // (MoveForward 300 -> 0.5, RotRight 150 -> 0.25, StrafeRight 300 -> 0.5). BUTTON/PULSE/TOGGLE and exec functions act
        // on Press/Release only, so the Hold pass skips these keys (IsAnalogKey). X before Y.
        internal const float AnalogSpeedDivisor = 600f;
        float _stickX, _stickY;   // right = +, up = +
        bool _analog;
        bool IsAnalogKey(string key)
        {
            if (_gamepad is null) return false;
            IReadOnlyList<string> names = KeyNamesByIndex();
            for (int slot = JoyStickUp; slot <= JoyStickRight; slot++)
                if (string.Equals(names[199 + slot], key, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        void PollStickAnalog(UEInterpreter interp, UEObject pawn)
        {
            if (pawn?.Class is null) return;
            IReadOnlyList<string> names = KeyNamesByIndex();
            foreach (var (slot, amount) in new[] { (JoyStickLeft, -_stickX), (JoyStickRight, _stickX), (JoyStickUp, _stickY), (JoyStickDown, -_stickY) })
            {
                string key = names[199 + slot];
                if (!(amount > 0f) || !_down.Contains(key) || !TryGetDispatchBinding(key, out string command)) continue;
                _analog = true;
                try { ExecInputCommands(interp, pawn, command, InputAction.Axis, amount); } finally { _analog = false; }
            }
        }

        // Before the joystick block: the virtual keys and the right stick, then the JoyInfo the ported block consumes.
        bool PollGamepad(UEInterpreter interp, UEObject console, UEObject pawn, out JoyInfo j)
        {
            j = default;
            _padRead = _gamepad(out GamepadState g);
            if (_padRead) { _padConnected = true; _padType = g.Type; _padFaces = g.Faces; }
            if (!_padRead)
            {
                // Disconnected: nothing is marked handled, so the final loop releases every held Joy slot and virtual key that is
                // in KeyDownTable through the keyboard state, as for any device key; a key the console consumed was never in
                // the table and gets no Release, as with the shipped final loop.
                _padVirtualDown.Clear();
                Array.Clear(_padLatch);
                _padPrev = 0;
                _navDir = -1;
                return false;
            }
            bool Has(int b) => (g.Buttons & (1u << b)) != 0;
            bool menu = MenuActive(console);
            bool capture = menu && ControlsCapturing();

            foreach (int b in new[] { PadSouth, PadEast, PadStart })
            {
                bool was = (_padPrev & (1u << b)) != 0;
                // B with a combo list open closes the list instead (UEInput.MenuNav.cs); "-" is no key.
                if (Has(b) && !was) _padLatch[b] = b == PadEast && menu && CloseOpenList(interp, console) ? "-" : VirtualOf(b, menu);
                else if (!Has(b)) _padLatch[b] = null;
            }
            if (capture && Has(PadSouth) && (_padPrev & (1u << PadSouth)) == 0) _padLatch[PadSouth] = "";   // the page waits for a key: A is Joy1
            _padPrev = g.Buttons;
            bool Latched(string key) => _padLatch[PadSouth] == key || _padLatch[PadEast] == key || _padLatch[PadStart] == key;
            // Virtual keys in IK order (LeftMouse 1, Escape 27, Space 32): an edge of the pad state, not KeyDownTable (the
            // console consumes Escape, so the table would re-press it every poll); held -> slot handled, so the final loop
            // does not release it through the keyboard state.
            Virtual(interp, console, pawn, "LeftMouse", 1, Latched("LeftMouse"));
            Virtual(interp, console, pawn, "Escape", 27, Latched("Escape"));
            Virtual(interp, console, pawn, "Space", 32, Latched("Space"));

            IReadOnlyList<string> names = KeyNamesByIndex();
            float look = menu ? _lookSpeed * MenuCursorSensitivity(pawn) : _lookSpeed;
            float mx = JoystickAxis((uint)(g.RightX + 32768), look, true);
            float my = JoystickAxis((uint)(g.RightY + 32768), -look, true);   // mouse Y: up = +
            if (mx != 0f) InputEvent(interp, console, pawn, names[0xe4], InputAction.Axis, mx);
            if (my != 0f) InputEvent(interp, console, pawn, names[0xe5], InputAction.Axis, my);
            // D-pad in a menu: snap the cursor to a control (UEInput.MenuNav.cs); the POV below is reported unchanged.
            MenuListPoll(interp, console, pawn, menu);
            int dpad = Has(PadDPadUp) ? 0 : Has(PadDPadRight) ? 1 : Has(PadDPadDown) ? 2 : Has(PadDPadLeft) ? 3 : -1;
            // A d-pad press the Controls page takes as a key does not also move the cursor, until it is released.
            if (dpad < 0) _dpadCaptured = false; else if (capture) _dpadCaptured = true;
            MenuNavPoll(interp, console, pawn, menu, _dpadCaptured ? -1 : dpad);

            uint joy = 0;
            foreach (var (button, slot) in JoySlots)
                if (Has(button) && (button != PadSouth || _padLatch[PadSouth] == "")) joy |= 1u << (slot - 1);
            if (g.LeftTrigger > TriggerPressed) joy |= 1u << (JoyLeftTrigger - 1);
            if (g.RightTrigger > TriggerPressed) joy |= 1u << (JoyRightTrigger - 1);
            // Left stick: the values the WinDrv block gives a joystick's X/Y axes (ScaleXYZ, DeadZoneXYZ, InvertVertical); a
            // direction key is down while its value is past zero.
            _stickX = JoystickAxis((uint)(g.LeftX + 32768), _scaleXYZ, _deadZoneXYZ);
            _stickY = JoystickAxis((uint)(g.LeftY + 32768), _invertVertical ? _scaleXYZ : -_scaleXYZ, _deadZoneXYZ);
            if (_stickY > 0f) joy |= 1u << (JoyStickUp - 1);
            if (_stickY < 0f) joy |= 1u << (JoyStickDown - 1);
            if (_stickX < 0f) joy |= 1u << (JoyStickLeft - 1);
            if (_stickX > 0f) joy |= 1u << (JoyStickRight - 1);
            j.Buttons = joy;
            j.X = j.Y = j.Z = j.R = j.U = j.V = 32768;
            j.Pov = Has(PadDPadUp) ? 0u : Has(PadDPadRight) ? 9000u : Has(PadDPadDown) ? 18000u : Has(PadDPadLeft) ? 27000u : 0xFFFFu;
            return true;
        }

        // Menu cursor speed follows the Options "Sensitivity" slider (author decision 2026-11-13, NOT ORIGINAL: the original
        // has no pad cursor). In game PlayerInput scales aMouseX/aMouseY by MouseSensitivity; WindowConsole moves the cursor
        // by MouseScale*Delta without it, so the stick's menu step is scaled here by MouseSensitivity relative to its shipped
        // default (the class defaultproperties; DefUser.ini sets none): at the shipped setting the step is GamepadLookSpeed.
        // No player (no setting to follow): unscaled.
        static float MenuCursorSensitivity(UEObject pawn)
        {
            if (pawn is null) return 1f;
            float def = UEInterpreter.AsFloat(UEObject.ClassDefaultScalar(pawn.Class, "MouseSensitivity")
                ?? throw new InvalidOperationException($"gamepad: {pawn.Class?.Name} has no MouseSensitivity default"));
            return UEInterpreter.AsFloat(pawn.GetProp("MouseSensitivity")) / def;
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
