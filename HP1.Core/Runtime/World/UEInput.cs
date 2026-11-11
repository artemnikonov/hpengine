// UInput — input as the shipped engine executes it.
//
// A binding is not just a property write: instead of writing pawn input
// properties (`aBaseX`, `bFire`, `bAltFire`) directly, `Button bAltFire | AltFire`
// is a flag AND an exec function that must run; that is why spells can be cast from text input.
//
// Source, line by line from the decompile:
//   UInput::Exec                 Engine.dll.c:251600..251765 (guard label 251844)
//     ParseCommand BUTTON/PULSE/TOGGLE/AXIS, then lookup among 40 aliases
//   BUTTON   251741..251760  Press -> 1; Release -> 0 (only if it was not 0)
//   PULSE    251726..251739  Press -> 1; other actions — no-op
//   TOGGLE   251712..251721  Press -> ^= 0x80; others — no-op
//   AXIS     251629..251662  SPEED= (default 1.0), SPEEDBASE= (default 0.0):
//                              SpeedBase>0          -> += Delta*SpeedBase*Speed
//                              else action==Axis    -> += Delta*Speed*0.01
//                              else action==Hold    -> += Delta*Speed
//                              else no-op
//   ExecAlias                251638..251676 (FUN_103a55b9) — alias command
//   key edge dedup           FUN_103a57d0: state array at +0xeb0; Press on an already pressed key and
//                              Release on an already released one are IGNORED
//
// Layout and aliases are NOT invented: both are read from `DefUser.ini`
// ([Engine.Input] Aliases[n]=(Command="...",Alias=X) and <Key>=<command>).
// OS keys stay outside: Core takes a UE1 key NAME; which hardware produced it is the frontend adapter's business.
using System;
using System.Collections.Generic;
using HP1.Core;
using UELib.Core;

namespace HP1.UEScript
{
    public sealed partial class UEInput
    {
        // Shipped EInputAction, as UInput::Exec reads it at +0xe80.
        public enum InputAction { None = 0, Press = 1, Hold = 2, Release = 3, Axis = 4 }

        readonly Dictionary<string, string> _aliases = new(UEPropNameComparer.Instance);
        readonly Dictionary<string, string> _bindings = new(UEPropNameComparer.Instance);
        readonly HashSet<string> _down = new(UEPropNameComparer.Instance);

        // All CPF_Input fields of the pawn: ResetInput zeroes them every tick, and only then do commands accumulate
        // values again. The set is the fields with the Input flag in the class declaration, not a hand-made list.
        readonly List<UProperty> _inputProps = new();
        UClass _inputPropsOf;

        public Action<string> Journal;
        void Log(string line) => Journal?.Invoke(line);

        // ── Layout from ini ─────────────────────────────────────────────────
        // `Aliases[n]=(Command="Button bAltFire | AltFire",Alias=AltFire)` and `Alt=AltFire`. Parsed strictly by the
        // file format; anything unparsed becomes LOUD, not silently skipped.
        // Loading the layout REPLACES the previous one rather than adding to it: the layout is loaded twice — at
        // bootstrap and after the settings store is bound — and without clearing, a binding the user removed would
        // come back from the first load, since the second load's empty value does not override it
        // (see `kv.Value.Length > 0` below).
        public void LoadBindings(IEnumerable<KeyValuePair<string, string>> engineInputSection)
        {
            _aliases.Clear();
            _bindings.Clear();
            foreach (KeyValuePair<string, string> kv in engineInputSection)
            {
                if (kv.Key.StartsWith("Aliases[", StringComparison.OrdinalIgnoreCase))
                {
                    (string command, string alias) = ParseAliasValue(kv.Value);
                    if (string.IsNullOrEmpty(alias) || string.Equals(alias, "None", StringComparison.OrdinalIgnoreCase))
                        continue;                       // empty table slots
                    _aliases[alias] = command;
                }
                else if (kv.Value.Length > 0)
                    _bindings[kv.Key] = kv.Value;       // <Key>=<command>
            }
            Log($"input_bindings aliases={_aliases.Count} keys={_bindings.Count}");
        }

        internal static (string Command, string Alias) ParseAliasValue(string value)
        {
            // (Command="....",Alias=Name)
            string v = value.Trim();
            if (v.StartsWith("(", StringComparison.Ordinal)) v = v.Substring(1);
            if (v.EndsWith(")", StringComparison.Ordinal)) v = v.Substring(0, v.Length - 1);
            string command = "", alias = "";
            int i = 0;
            while (i < v.Length)
            {
                int eq = v.IndexOf('=', i);
                if (eq < 0) break;
                string key = v.Substring(i, eq - i).Trim();
                i = eq + 1;
                string val;
                if (i < v.Length && v[i] == '"')
                {
                    int close = v.IndexOf('"', i + 1);
                    if (close < 0) break;
                    val = v.Substring(i + 1, close - i - 1);
                    i = close + 1;
                    if (i < v.Length && v[i] == ',') i++;
                }
                else
                {
                    int comma = v.IndexOf(',', i);
                    if (comma < 0) { val = v.Substring(i); i = v.Length; }
                    else { val = v.Substring(i, comma - i); i = comma + 1; }
                }
                if (string.Equals(key, "Command", StringComparison.OrdinalIgnoreCase)) command = val;
                else if (string.Equals(key, "Alias", StringComparison.OrdinalIgnoreCase)) alias = val.Trim();
            }
            return (command, alias);
        }

        public bool IsDown(string key) => _down.Contains(key);

        // Layout, READ-ONLY — for frontend boundary checks ("the binding came from the real DefUser.ini, not from a
        // C# table"). No writer on purpose: the only entry is LoadBindings from ini.
        public int BindingCount => _bindings.Count;
        public bool TryGetBinding(string key, out string command)
            => _bindings.TryGetValue(key ?? string.Empty, out command);

        // ── SET Input <Key> [<Command>] ─────────────────────────────────────
        //
        // The write counterpart of LoadBindings: shipped SET changes the LIVE value (see
        // UObject::GlobalSetProperty, Core.dll.c:56604), and the menu rebinds keys through it. Bindings must have
        // ONE owner, otherwise the menu and the game silently diverge.
        //
        // An empty command REMOVES the binding instead of storing an empty string: LoadBindings skips empty values
        // too (`kv.Value.Length > 0`), and KEYBINDING reads the same way — "no binding" and "empty binding" look
        // identical in shipped.
        //
        // ONLY THE LIVE VALUE HERE — the first step of GlobalSetProperty. The second (SaveConfig) is done by the
        // caller, `UEConsoleCommand`: UEConfig owns saving, and pulling it in here would give settings a second
        // owner. A live edit without a save does not survive restart — both steps must go together.
        public void SetBinding(string key, string command)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (string.IsNullOrEmpty(command)) _bindings.Remove(key);
            else _bindings[key] = command;
        }

        // ── UInput::ReadInput (Engine.dll.c, guard label 252133) ────────────
        //
        // One body for TWO modes, and that matters:
        //   ReadInput(dt):   IST_Hold is sent for every key still pressed,
        //                    then the multiplier becomes 20.0/dt;
        //   ReadInput(-1.0): Hold passes are skipped, multiplier 0.0 —
        //                    this is the reset after PlayerTick.
        // The multiplier is then applied to CPF_Input fields — and ONLY to float fields: the list is built by flag
        // 0x4, filtered by comparison against one property class and dereferenced as `float*`. Byte and bool inputs
        // (bAltFire, bRun, bPressedJump) are NOT touched at all (zeroing them would drop a held BUTTON between
        // frames, since BUTTON reacts only to Press/Release). Example scale: dt=0.02, Speed=300 -> 20*300 = 6000.
        public void ReadInput(UEInterpreter interp, UEObject pawn, float delta)
        {
            if (pawn?.Class is null) return;
            if (delta != -1f)
            {
                // Hold in key-index order 0..254 (Engine.dll.c:52583-52587), through Process (the binding table).
                foreach (string key in KeyNamesByIndex())
                    if (_down.Contains(key) && TryGetDispatchBinding(key, out string command))
                        ExecInputCommands(interp, pawn, command, InputAction.Hold, delta);
            }
            float scale = delta != -1f ? 20f / delta : 0f;
            foreach (UProperty p in InputFloatProps(pawn.Class))
            {
                string name = p.Name?.ToString();
                if (name is null) continue;
                pawn.Props[name] = UEInterpreter.AsFloat(pawn.GetProp(name)) * scale;
            }
        }

        // Reset after PlayerTick is the same ReadInput with -1.0 (128696).
        public void ResetInput(UEObject pawn) => ReadInput(null, pawn, -1f);

        // SetMouseCapture (WinDrv FUN_11109280, WinDrv.dll.c:11541): Viewport bit0 = !Capture (bWindowsMouseAvailable).
        public static void SetMouseCapture(UEObject console, bool captured)
        {
            if (console?.GetProp("Viewport") is UEObject vp) vp.Props["bWindowsMouseAvailable"] = !captured;
        }

        // UGameEngine::MousePosition (Engine.dll.c:134790): Viewport+0x3c/+0x40 = WindowsMouseX/Y, the client position of the
        // OS cursor. WinDrv WM_MOUSEMOVE calls it when the mouse is NOT captured (WinDrv.dll.c:3810); captured moves go to
        // MouseDelta + the MouseX/MouseY axes instead. The units are the viewport's (canvas) pixels.
        public static void MousePosition(UEObject console, float x, float y)
        {
            if (console?.GetProp("Viewport") is not UEObject vp) return;
            vp.Props["WindowsMouseX"] = x;
            vp.Props["WindowsMouseY"] = y;
        }

        // ── Device side of UWindowsViewport (WinDrv), owned here ────────────
        // DeviceDown(key) answers GetKeyState for an EInputKey name (the frontend's device state); DeviceFocused is
        // GetFocus() == the window. Without a device (headless) DeviceDown is null and nothing is polled.
        public Func<string, bool> DeviceDown;
        public bool DeviceFocused = true;

        // EInputKey names (without IK_) by index — the order of KeyDownTable.
        static List<string> _keyNames;
        static IReadOnlyList<string> KeyNamesByIndex()
        {
            if (_keyNames is not null) return _keyNames;
            IReadOnlyList<string> names = HP1.Core.UEClassRegistry.Shared?.EnumNames("EInputKey", "Actor")
                ?? throw new InvalidOperationException("input: enum EInputKey not found in the loaded packages");
            var list = new List<string>();
            for (int i = 0; i < names.Count && i < 255; i++) list.Add(names[i].StartsWith("IK_") ? names[i].Substring(3) : names[i]);
            return _keyNames = list;
        }

        // ── Joystick (winmm), UWindowsViewport::UpdateInput joystick block ──
        //
        // JOYINFOEX as joyGetPosEx(JOYSTICKID1) fills it with dwFlags 0x4ff (JOY_RETURNALL | JOY_RETURNCENTERED, no
        // POVCTS): axes in winmm units (0..65535, centre 32768), dwButtons bit i = button i+1, dwPOV in hundredths of a
        // degree clockwise from forward or JOY_POVCENTERED (0xFFFF). The frontend fills it from the raw device; the meaning
        // is all here.
        public struct JoyInfo { public uint X, Y, Z, R, U, V, Buttons, Pov; }
        // JOYCAPS fields shipped reads: wNumButtons (Client+0x1c4 = 0x188+0x3c) and wCaps (Client+0x1e8 = 0x188+0x60;
        // JOYCAPS_HASZ 1, HASR 2, HASU 4, HASV 8, POV4DIR 0x20, POVCTS 0x40).
        public struct JoyCaps { public uint NumButtons, Caps; }
        // joyGetPosEx(0, ...) == JOYERR_NOERROR. Polled at the moment UpdateInput runs, like the original.
        public delegate bool JoyGetPosEx(out JoyInfo info);

        JoyGetPosEx _joyPoll;
        JoyCaps _joyCaps;
        // Client config (UWindowsClient::StaticConstructor, WinDrv.dll.c:2829-2865): DeadZoneXYZ +0xb8, DeadZoneRUV +0xbc,
        // InvertVertical +0xc0 (UBoolProperty), ScaleXYZ +0xc4, ScaleRUV +0xc8 (UFloatProperty); section [WinDrv.WindowsClient].
        bool _deadZoneXYZ, _deadZoneRUV, _invertVertical;
        float _scaleXYZ, _scaleRUV;

        // UWindowsClient::PostEditChange (WinDrv.dll.c:24-80; same body at :7735): JOYCAPS at Client+0x188 zeroed; if
        // UseJoystick (+0xac), no -NoJoy on the command line and not the editor: joyGetDevCapsA(0) into it; on failure the caps
        // stay zero ("joyGetDevCaps failed") and UpdateInput never polls (its gate is wNumButtons != 0). getDevCaps returning
        // null = joyGetDevCaps failed. The config is read here (client properties), loudly if a key is missing (the shipped
        // ini has all of them).
        public void InitJoystick(UEConfig config, string commandLine, Func<JoyCaps?> getDevCaps, JoyGetPosEx poll)
        {
            if (config is null) throw new ArgumentNullException(nameof(config));
            string section = config.ResolveSection(UEConfig.ScopeSystem, UEConfig.ViewportRef);
            _joyCaps = default;
            _joyPoll = poll;
            _gamepad = null;
            _deadZoneXYZ = UEViewportStartup.ImportBool(config, section, "DeadZoneXYZ");
            _deadZoneRUV = UEViewportStartup.ImportBool(config, section, "DeadZoneRUV");
            _invertVertical = UEViewportStartup.ImportBool(config, section, "InvertVertical");
            _scaleXYZ = ImportFloat(config, section, "ScaleXYZ");
            _scaleRUV = ImportFloat(config, section, "ScaleRUV");
            if (!UEViewportStartup.ImportBool(config, section, "UseJoystick") || UEViewportStartup.ParseParam(commandLine, "NoJoy"))
                return;
            JoyCaps? caps = getDevCaps?.Invoke();
            if (caps is null) { Log("joyGetDevCaps failed"); return; }
            _joyCaps = caps.Value;
            Log($"Detected joystick buttons={_joyCaps.NumButtons} caps=0x{_joyCaps.Caps:x}");
        }

        // UFloatProperty::ImportText = appAtof of the value; no key -> loud (the port carries no class defaults).
        static float ImportFloat(UEConfig config, string section, string key)
        {
            string raw = config.Get(UEConfig.ScopeSystem, section, key)
                ?? throw new System.IO.InvalidDataException("[" + section + "] " + key + ": no key; the port does not carry UWindowsClient defaults");
            return UEValue.LeadingAtof(raw);
        }

        // UWindowsViewport::JoystickInputEvent (disasm 0x111095a0-0x1110963e), all in the x87 register (PC64):
        // f = (Pos - 32768.0) * 2^-15; with the dead zone: f > 0.2 -> (f - 0.2) * 1.25; f < -0.2 -> (f + 0.2) * 1.25;
        // otherwise 0.0f (fcom + test ah,0x41 / test ah,0x01); then f * Scale, fstp dword -> CauseInputEvent(Key, IST_Axis, f).
        // Constants read from WinDrv.dll .rdata: 0x111146d8 = 32768.0, 0x111146c8 = 2^-15, 0x111146b8 = 0.2 (double),
        // 0x11114698 = -0.2, 0x111146a8 = 1.25, 0x11114694 = 0.0f. Pos: fild qword of the zero-extended DWORD, fstp dword.
        internal static float JoystickAxis(uint pos, float scale, bool deadZone)
        {
            X87Ext f = X87Ext.Round64(X87Ext.From((float)pos) - X87Ext.From(32768.0));
            f = X87Ext.Round64(f * X87Ext.From(1.0 / 32768.0));
            if (deadZone)
            {
                X87Ext zone = X87Ext.From(0.2);
                if ((X87Ext.Fcom(f, zone) & (X87Ext.C0 | X87Ext.C3)) == 0)
                    f = X87Ext.Round64(X87Ext.Round64(f - zone) * X87Ext.From(1.25));
                else if ((X87Ext.Fcom(f, X87Ext.From(-0.2)) & X87Ext.C0) != 0)
                    f = X87Ext.Round64(X87Ext.Round64(f + zone) * X87Ext.From(1.25));
                else
                    return 0f * scale;   // fld 0.0f; fmul Scale; fstp: exact, a signed zero (X87Ext keeps none)
            }
            return X87Ext.Round64(f * X87Ext.From(scale)).ToFloat();
        }

        readonly bool[] _handled = new bool[256];

        // Joystick block of UpdateInput (WinDrv.dll.c:180-300; disasm 0x11109701-0x111099db). Runs only when wNumButtons != 0
        // and joyGetPosEx succeeds. Buttons 1..16 (IK 200..215, low bit first): Press if KeyDownTable is clear and the bit set,
        // Release if the table is set and the bit clear; every slot is marked handled for the final loop. Axes, EVERY poll:
        // JoyX (ScaleXYZ, DeadZoneXYZ), JoyY (-ScaleXYZ, +ScaleXYZ with InvertVertical), and by caps JoyZ (HASZ), JoyR, JoyU
        // (HASR/HASU; ScaleRUV, DeadZoneRUV), JoyV (HASV; -ScaleRUV / +ScaleRUV). POV (caps & 0x60): 0 -> JoyPovUp,
        // 18000 -> JoyPovDown, 27000 -> JoyPovLeft, 9000 -> JoyPovRight: Press if not down, that slot marked handled; the other
        // directions are left to the final loop, which releases them through GetKeyState (a keyboard VK, never down there).
        void PollJoystick(UEInterpreter interp, UEObject console, UEObject pawn)
        {
            if (_joyCaps.NumButtons == 0) return;
            JoyInfo j;
            if (_gamepad is not null) { if (!PollGamepad(interp, console, pawn, out j)) return; }   // improvement, UEInput.Gamepad.cs
            else if (_joyPoll is null || !_joyPoll(out j)) return;
            IReadOnlyList<string> names = KeyNamesByIndex();
            uint buttons = j.Buttons;
            for (int i = 0; i < 16; i++)
            {
                string key = names[200 + i];
                bool bit = (buttons & 1) != 0;
                if (!_down.Contains(key) && bit) InputEvent(interp, console, pawn, key, InputAction.Press, 0f);
                else if (_down.Contains(key) && !bit) InputEvent(interp, console, pawn, key, InputAction.Release, 0f);
                _handled[200 + i] = true;
                buttons >>= 1;
            }
            float yScale = _invertVertical ? _scaleXYZ : -_scaleXYZ;     // fld +-1.0; fmul dword; fstp dword: exact
            float vScale = _invertVertical ? _scaleRUV : -_scaleRUV;
            InputEvent(interp, console, pawn, names[0xe0], InputAction.Axis, JoystickAxis(j.X, _scaleXYZ, _deadZoneXYZ));
            InputEvent(interp, console, pawn, names[0xe1], InputAction.Axis, JoystickAxis(j.Y, yScale, _deadZoneXYZ));
            if ((_joyCaps.Caps & 1) != 0) InputEvent(interp, console, pawn, names[0xe2], InputAction.Axis, JoystickAxis(j.Z, _scaleXYZ, _deadZoneXYZ));
            if ((_joyCaps.Caps & 2) != 0) InputEvent(interp, console, pawn, names[0xe3], InputAction.Axis, JoystickAxis(j.R, _scaleRUV, _deadZoneRUV));
            if ((_joyCaps.Caps & 4) != 0) InputEvent(interp, console, pawn, names[0xe8], InputAction.Axis, JoystickAxis(j.U, _scaleRUV, _deadZoneRUV));
            if ((_joyCaps.Caps & 8) != 0) InputEvent(interp, console, pawn, names[0xe9], InputAction.Axis, JoystickAxis(j.V, vScale, _deadZoneRUV));
            if ((_joyCaps.Caps & 0x60) == 0) return;
            int pov = j.Pov switch { 0 => 0xf0, 18000 => 0xf1, 27000 => 0xf2, 9000 => 0xf3, _ => -1 };
            if (pov < 0) return;
            if (!_down.Contains(names[pov])) InputEvent(interp, console, pawn, names[pov], InputAction.Press, 0f);
            _handled[pov] = true;
        }

        // UWindowsViewport::UpdateInput(Reset) (WinDrv.dll.c:143-430): the joystick block, then (DirectInput block: off in
        // the shipped ini, UseDirectInput=False) the final loop (disasm 0x11109c85-0x11109cf6, `cmp edi,0x100; jge`) over
        // keys 0..255 not handled above: a key the table holds down that the device reports up -> CauseInputEvent(Release),
        // regardless of focus; otherwise, on Reset with focus, a key the device holds -> CauseInputEvent(Press). The table
        // index is clamped to 0xfe (0x11109ca6-0x11109cb0), so key 255 reads key 254's entry and calls GetKeyState(0xff)
        // and CauseInputEvent(0xff) — which refuses a key >= 0xff before any event (`cmp edx,0xff; jge`, 0x111091cf). Key 255
        // therefore has no effect and is not walked here (EInputKey has 255 names, 0..254).
        public void UpdateInput(UEInterpreter interp, UEObject console, UEObject pawn, bool reset)
        {
            Array.Clear(_handled);
            PollJoystick(interp, console, pawn);
            IReadOnlyList<string> names = KeyNamesByIndex();
            for (int k = 0; DeviceDown is not null && k < names.Count; k++)
            {
                if (_handled[k]) continue;
                string key = names[k];
                if (_down.Contains(key)) { if (!DeviceDown(key)) InputEvent(interp, console, pawn, key, InputAction.Release, 0f); }
                else if (reset && DeviceFocused && DeviceDown(key)) InputEvent(interp, console, pawn, key, InputAction.Press, 0f);
            }
            _padConnected = _gamepad is not null && _padRead;   // improvement D-75: pad defaults end after the releases
        }

        // UViewport::ReadInput (Engine.dll.c:108203, body 0x10385d20): dt != -1 -> UpdateInput(0), then UInput::ReadInput.
        public void ViewportReadInput(UEInterpreter interp, UEObject console, UEObject pawn, float delta)
        {
            if (delta != -1f) UpdateInput(interp, console, pawn, false);
            ReadInput(interp, pawn, delta);
        }

        // UInput::ResetInput in full: the table and CPF_Input properties zeroed (ResetAll), then Viewport->UpdateInput(1)
        // (0x103a608b): held keys are pressed again through the whole chain at once. Callers: WM_SETFOCUS/WM_KILLFOCUS and
        // LoadMap (Engine.dll.c:247645-247651) — not menus or console state changes.
        public void ResetInputFull(UEInterpreter interp, UEObject console, UEObject pawn)
        {
            ResetAll(pawn);
            UpdateInput(interp, console, pawn, true);
        }

        // UInput::ResetInput (disasm 0x103a5f30-0x103a60a1; NOT the same as ResetInput above — that is
        // ReadInput(-1)): KeyDownTable[0..254] = 0 (0x103a5f7d), CPF_Input UByteProperty of the viewport actor = 0
        // (0x103a5fbc-0x103a5fce), CPF_Input UFloatProperty = 0 (0x103a6025-0x103a603a), then
        // Viewport->UpdateInput(1) (0x103a608b) — ResetInputFull.
        public void ResetAll(UEObject pawn)
        {
            _down.Clear();
            if (pawn?.Class is null) return;
            for (UClass c = pawn.Class; c is not null; c = UEInterpreter.CanonicalSuper(c))
                foreach (UProperty p in c.EnumerateFields<UProperty>())
                {
                    if (!p.PropertyFlags.HasFlag(UELib.Flags.PropertyFlag.Input)) continue;
                    string name = p.Name?.ToString();
                    if (name is null) continue;
                    if (p is UByteProperty) pawn.Props[name] = (byte)0;
                    else if (p is UFloatProperty) pawn.Props[name] = 0f;
                }
        }

        // UInput::FindButtonName / FindAxisName (Engine.dll.c:251221/251354, vtable UInput +31/+32): a name from
        // BUTTON/PULSE/TOGGLE/AXIS is accepted only if the pawn's class chain has a CPF_Input property (flag 0x4) of
        // the right class — UByteProperty for a button, UFloatProperty for an axis; otherwise the command is rejected
        // ("Bad Button command") rather than creating a dynamic property.
        static bool IsButtonProp(UClass cls, string name) => FindInputProp(cls, name) is UByteProperty;
        static bool IsAxisProp(UClass cls, string name) => FindInputProp(cls, name) is UFloatProperty;
        static UProperty FindInputProp(UClass cls, string name)
        {
            for (UClass c = cls; c is not null; c = UEInterpreter.CanonicalSuper(c))
                foreach (UProperty p in c.EnumerateFields<UProperty>())
                    if (p.PropertyFlags.HasFlag(UELib.Flags.PropertyFlag.Input)
                        && string.Equals(p.Name?.ToString(), name, StringComparison.OrdinalIgnoreCase))
                        return p;
            return null;
        }

        IReadOnlyList<UProperty> InputFloatProps(UClass cls)
        {
            if (!ReferenceEquals(_inputPropsOf, cls))
            {
                _inputProps.Clear();
                for (UClass c = cls; c is not null; c = UEInterpreter.CanonicalSuper(c))
                    foreach (UProperty p in c.EnumerateFields<UProperty>())
                        if (p.PropertyFlags.HasFlag(UELib.Flags.PropertyFlag.Input)
                            && p is UFloatProperty)
                            _inputProps.Add(p);
                _inputPropsOf = cls;
                Log($"input_reset_set class={cls.Name} props={_inputProps.Count}");
            }
            return _inputProps;
        }

        // ── UEngine::InputEvent (Engine.dll.c:15766; disasm 0x10393985..0x10393a9f) ──
        //
        // The viewport console gets the device event FIRST — eventKeyEvent (FName 0xb4) with (key, action, delta).
        // If it returns true the event is consumed: on IST_Release only PreProcess is called (vtable +0x60, dedup —
        // remove the key from the pressed set), Process is not called, result 1. If it returns false or there is no
        // console — PreProcess, and on success Process (vtable +0x64): the ini bindings. Ghidra dropped the
        // "consumed" branch as an "unreachable block" (0x10393a25/0x10393a2b) — read it from the disasm.
        //
        // This step is what runs HPConsole.KeyEvent: Space sets bSpacePressed/bSkipKeyPressed (throwing a carried
        // object, Harry.uc:2027; closing a scroll, baseScroll.uc:47), Escape reaches FEBook.EscFromConsole
        // (quidditch branch, FEBook.uc:681).
        public bool InputEvent(UEInterpreter interp, UEObject console, UEObject pawn, string key,
            InputAction action, float delta)
        {
            bool r = InputEventCore(interp, console, pawn, key, action, delta);
            // Author decision 2026-10-01 (not original; improvements list D-70): the mouse wheel scrolls an open combo list.
            // UWindow of this version has no wheel handler; the override runs after the wheel event went through the
            // console, from Core's own event path (A7 item 6), never from the frontend.
            if (action == InputAction.Press && (key == "MouseWheelUp" || key == "MouseWheelDown"))
                UEPortScriptOverrides.ScrollOpenComboList(interp, console, key == "MouseWheelUp" ? -1 : 1);
            return r;
        }

        bool InputEventCore(UEInterpreter interp, UEObject console, UEObject pawn, string key,
            InputAction action, float delta)
        {
            if (console is not null)
            {
                int ik = UEConsoleCommand.FindKeyName(key);
                UFunction fn = interp.FindEventFunction(console, "KeyEvent");
                if (ik < 0 || fn is null)
                    throw new InvalidOperationException(
                        $"InputEvent: console {console.Class?.Name} has no KeyEvent or key IK_{key} is not in EInputKey");
                if (UEInterpreter.ToBool(interp.Invoke(console, fn, ik, (int)action, delta)))
                {
                    if (action == InputAction.Release) _down.Remove(key);
                    return true;
                }
            }
            return KeyEvent(interp, pawn, key, action, delta);
        }

        // ── Key edge dedup (FUN_103a57d0) ───────────────────────────────────
        // Press on an already pressed key and Release on an already released one produce NOTHING.
        // Returns true if the event was accepted (i.e. the command was executed).
        public bool KeyEvent(UEInterpreter interp, UEObject pawn, string key,
            InputAction action, float delta)
        {
            if (action == InputAction.Press)
            {
                if (!_down.Add(key)) return false;
            }
            else if (action == InputAction.Release)
            {
                if (!_down.Remove(key)) return false;
            }
            if (!TryGetDispatchBinding(key, out string command))
            {
                // A pressed key without a binding is not an error (half the ini lines are empty), but not silence
                // either: without a log line this is debugged blind.
                Log($"input_key_unbound key={key} action={action}");
                return false;
            }
            Log($"input_key key={key} action={action} command=\"{command}\"");
            ExecInputCommands(interp, pawn, command, action, delta);
            return true;
        }

        // UInput::ExecInputCommands (Engine.dll.c:144863) — commands are split by
        // ParseLine(.., 0x100, Exact=0), see ParseLines.
        public void ExecInputCommands(UEInterpreter interp, UEObject pawn, string commands,
            InputAction action, float delta)
        {
            foreach (string part in ParseLines(commands))
            {
                string command = part.Trim();
                if (command.Length != 0) Exec(interp, pawn, command, action, delta, depth: 0);
            }
        }

        // ParseLine(Stream, Result, MaxLen, Exact = 0) (Core.dll.c:62190), repeatedly to the end of the string:
        // the separator is '|' OUTSIDE quotes (a quote toggles the mode), '\r'/'\n'; after "//" outside quotes the
        // characters up to the end of the segment are not copied; a segment holds at most MaxLen-1 = 0xff
        // characters, the excess starts the next one. After a segment all consecutive '|', '\r', '\n' are skipped.
        internal static IEnumerable<string> ParseLines(string text)
        {
            int i = 0;
            var sb = new System.Text.StringBuilder();
            while (true)
            {
                sb.Clear();
                bool quoted = false, comment = false, any = false;
                int room = 0x100;
                while (i < text.Length)
                {
                    char c = text[i];
                    if (c == '\n' || c == '\r' || --room < 1) break;
                    if (!quoted)
                    {
                        if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') comment = true;
                        if (c == '|') break;
                    }
                    if (c == '"') quoted = !quoted;
                    any = true;
                    if (!comment) sb.Append(c);
                    i++;
                }
                while (i < text.Length && (text[i] == '\n' || text[i] == '\r' || text[i] == '|')) i++;
                if (i >= text.Length && !any) yield break;
                yield return sb.ToString();
                if (i >= text.Length) yield break;
            }
        }

        // Alias re-entrancy flag (DAT_105ed584, Engine.dll.c:251667/251678): alias table lookup is ALLOWED only
        // while the flag is clear, and it is set while an alias command executes. That is why
        // `AltFire` -> "Button bAltFire | AltFire" does not loop: the nested `AltFire` no longer resolves as an
        // alias and goes out as an exec command.
        bool _inAlias;

        void Exec(UEInterpreter interp, UEObject pawn, string command,
            InputAction action, float delta, int depth)
        {
            string[] token = command.Split((char[])null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (token.Length == 0) return;
            string head = token[0];
            string rest = token.Length > 1 ? token[1] : "";
            // UInput::ExecInputCommands (Engine.dll.c:144880-144897): the line goes to the WHOLE Viewport->Exec chain
            // (exec functions included) only on Press, or on Release with the OnRelease prefix (which is stripped);
            // on Hold/Axis and plain Release only to UInput::Exec (BUTTON/PULSE/TOGGLE/AXIS/alias), where a bare
            // function name executes nothing.
            bool viewportExec = action == InputAction.Press;
            if (action == InputAction.Release
                && string.Equals(head, "OnRelease", StringComparison.OrdinalIgnoreCase))
            {
                token = rest.Split((char[])null, 2, StringSplitOptions.RemoveEmptyEntries);
                if (token.Length == 0) return;
                head = token[0];
                rest = token.Length > 1 ? token[1] : "";
                command = rest.Length > 0 ? head + " " + rest : head;
                viewportExec = true;
            }

            if (string.Equals(head, "BUTTON", StringComparison.OrdinalIgnoreCase))
            {
                string prop = FirstToken(rest);
                if (prop is null || !IsButtonProp(pawn.Class, prop)) { Log($"input_bad command=\"{command}\" reason=Bad_Button_command"); return; }
                if (action == InputAction.Press) pawn.Props[prop] = (byte)1;
                else if (action == InputAction.Release
                      && UEInterpreter.ToInt(pawn.GetProp(prop)) != 0) pawn.Props[prop] = (byte)0;
                return;
            }
            if (string.Equals(head, "PULSE", StringComparison.OrdinalIgnoreCase))
            {
                string prop = FirstToken(rest);
                if (prop is null || !IsButtonProp(pawn.Class, prop)) { Log($"input_bad command=\"{command}\" reason=Bad_Button_command"); return; }
                if (action == InputAction.Press) pawn.Props[prop] = (byte)1;
                return;
            }
            if (string.Equals(head, "TOGGLE", StringComparison.OrdinalIgnoreCase))
            {
                string prop = FirstToken(rest);
                if (prop is null || !IsButtonProp(pawn.Class, prop)) { Log($"input_bad command=\"{command}\" reason=Bad_Toggle_command"); return; }
                if (action == InputAction.Press)
                    pawn.Props[prop] = (byte)(UEInterpreter.ToInt(pawn.GetProp(prop)) ^ 0x80);
                return;
            }
            if (string.Equals(head, "AXIS", StringComparison.OrdinalIgnoreCase))
            {
                string prop = FirstToken(rest);
                if (prop is null || !IsAxisProp(pawn.Class, prop)) { Log($"input_bad command=\"{command}\" reason=Bad_Axis_command"); return; }
                float speed = ParseFloatOption(rest, "SPEED=", 1.0f);
                float speedBase = ParseFloatOption(rest, "SPEEDBASE=", 0.0f);
                float current = UEInterpreter.AsFloat(pawn.GetProp(prop));
                // Chains in an x87 register, one fstp store (disasm 0x103a547b..0x103a54bd).
                if (speedBase > 0f) pawn.Props[prop] = X87.MulMulAdd(delta, speedBase, speed, current);
                else if (action == InputAction.Axis) pawn.Props[prop] = X87.MulMulAdd(delta, speed, 0.01f, current);
                else if (action == InputAction.Hold) pawn.Props[prop] = X87.MulMulAdd(delta, speed, 1f, current);
                return;
            }

            // Not BUTTON/PULSE/TOGGLE/AXIS — the alias table first (shipped compares FName over 40 slots), and only
            // then does it go out as an ordinary console command, i.e. a pawn exec function.
            if (!_inAlias && _aliases.TryGetValue(head, out string aliased))
            {
                _inAlias = true;
                try
                {
                    foreach (string part in ParseLines(aliased))
                    {
                        string inner = part.Trim();
                        if (inner.Length != 0) Exec(interp, pawn, inner, action, delta, depth + 1);
                    }
                }
                finally { _inAlias = false; }
                return;
            }
            if (!viewportExec) return;
            DispatchExec(interp, pawn, head, rest, action);
        }

        // Bare command: shipped UInput does not handle it and returns 0 — next the UViewport::Exec/UPlayer::Exec
        // chain looks for it (console, level, GameInfo, HUD, pawn, inventory, engine; boot A5) — UEExecChain.
        // Arguments are the rest of the line: ScriptConsoleExec parses them itself.
        void DispatchExec(UEInterpreter interp, UEObject pawn, string name, string args,
            InputAction action)
        {
            string command = args.Length > 0 ? name + " " + args : name;
            UEObject taker = UEExecChain.ExecAfterInput(interp, pawn, command, Log);
            if (taker is not null)
                Log($"input_exec name={name} action={action} object={taker.GetProp(PK.Name)}");
        }

        static string FirstToken(string s)
        {
            string[] parts = s.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 0 ? null : parts[0];
        }

        // Parse(Text, "SPEED=", float&) (Core.dll.c:12855): appStrfind (word start, case-insensitive); if found —
        // appAtof of the tail AFTER the key (leading number, "300," -> 300, non-number -> 0), else the value is unchanged.
        internal static float ParseFloatOption(string text, string key, float fallback)
        {
            int at = UEValue.AppStrfind(text, key);
            return at < 0 ? fallback : UEValue.LeadingAtof(text.Substring(at + key.Length));
        }
    }
}
