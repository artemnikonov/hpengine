// InputStage: SDL3 events -> Core input, as the WinDrv window procedure (UWindowsViewport) does it.
//
// The original handles every OS message synchronously between two engine ticks, in queue order
// key down (autorepeat too) / key up / mouse buttons -> CauseInputEvent(Press/Release),
// a batch of WM_MOUSEMOVE -> one MouseX and one MouseY Axis event, each wheel message -> MouseW Axis then
// MouseWheelUp/Down Press+Release, WM_CHAR -> Console.KeyType (control characters too, not Enter), focus changes ->
// UInput::ResetInput. Here the events of a frame are queued by Feed and handed to Core in that order by RunFrame,
// before the tick — nothing happens between, so it is the same order. Everything after CauseInputEvent (console
// KeyEvent first, PreProcess, bindings, ReadInput at the player's tick, the reset's re-press) is Core's (UEInput).
// The mouse axis is a raw "up = +" delta (IK_MouseY IST_Axis), so SDL yrel ("down = +") is negated.
using System;
using System.Collections.Generic;
using System.Text;
using HP1.UEScript;
using SDL;

namespace HPEngine.Input
{
    public sealed class InputStage
    {
        // Mouse delta unit. With UseDirectInput=False (HP.ini) the original takes the OS CURSOR delta: WM_MOUSEMOVE,
        // lParam minus the client-area centre, SetCursorPos back to the centre (WinDrv.dll.c:3682-3778), i.e. pixels
        // after OS pointer speed and acceleration. From there the engine is linear: aMouseX += px*Speed*0.01
        // (UInput::Exec AXIS, Engine.dll.c:251652), then ×20/dt (UInput::ReadInput :52582); a live-original
        // measurement gave pre = 0.06·px exactly over 3..200 px. In relative mode SDL3
        // by default yields RAW device counts (Windows raw input, macOS GCMouse, X11 raw_values); the hint below
        // restores the OS cursor unit (Windows: WIN_ApplySystemScale, pointer speed and "enhanced precision";
        // macOS: accelerated NSEvent deltaX; X11/Wayland: accelerated values). SDL reads the hint at event time,
        // so it is set here, before the first event, not necessarily before SDL_Init.
        public InputStage() => SDL3.SDL_SetHint(SDL3.SDL_HINT_MOUSE_RELATIVE_SYSTEM_SCALE, "1");

        // ── Warnings and loud counters ─────────────────────────────────────
        public Action<string> Warn;
        // A key/button missing from the table: not silent, counted by code.
        public readonly Dictionary<SDL_Scancode, int> UnknownScancodes = new();
        public readonly Dictionary<int, int> UnknownMouseButtons = new();
        // Wheel events with SDL_MOUSEWHEEL_FLIPPED: the sign is taken as is (see README).
        public int FlippedWheelEvents;

        // 4:3 menu centred on a wide screen (Core SetConsoleRegion) while the console's UWindow is active.
        bool _uwindow;
        public int MenuRegionWidth(int screenW, int screenH)
            => _uwindow && screenW * 3 > screenH * 4 ? screenH * 4 / 3 : 0;

        // ── Device state (GetKeyState) and the frame's queue ─────────────────
        readonly Dictionary<SdlControl, bool> _ctlDown = new();
        readonly Dictionary<string, int> _nameDown = new(StringComparer.OrdinalIgnoreCase);
        // ResetRelease: a key-up with the SDL_ResetKeyboard signature (see Feed); FocusRelease: such a key-up that the focus
        // loss right after it proved synthetic — it moves the device state (GetKeyState) but is not an input event.
        enum Kind { Control, Mouse, Wheel, Char, Focus, ResetRelease, FocusRelease }
        // Control events keep the raw device transition: the device state (_ctlDown/_nameDown, i.e. GetKeyState) changes
        // when RunFrame handles the event, so a reset handled in the middle of the queue polls the state AT that point.
        readonly List<(Kind K, string Key, UEInput.InputAction Action, float Delta, SdlControl C, bool Repeat)> _evq = new();
        bool _captured, _focused = true;
        // The host window carries out each capture change (SdlMouseCapture.Apply): WinDrv SetMouseCapture does the OS
        // half itself right after the bWindowsMouseAvailable write — SetForegroundWindow, SetCapture, ShowCursor(0),
        // ClipCursor on capture; ShowCursor(1), ClipCursor(NULL) on release (FUN_11109280, WinDrv.dll.c:11541-11600).
        public Action<bool> OsCapture;
        // Where the OS cursor is, in viewport (canvas) pixels (host window). Read for an uncaptured mouse move and right
        // after a release: WinDrv's release puts the cursor back with SetCursorPos, whose WM_MOUSEMOVE goes to
        // MousePosition, so Viewport.WindowsMouseX/Y (what HPConsole draws while bWindowsMouseAvailable) is the OS cursor.
        public Func<(float X, float Y)> CursorPosition;
        public bool Captured => _captured;
        void SetCapture(IInputSink sink, bool captured)
        {
            sink.MouseCapture(captured); _captured = captured; OsCapture?.Invoke(captured);
            if (!captured) MousePosition(sink);
        }
        void MousePosition(IInputSink sink) { if (CursorPosition?.Invoke() is { } p) sink.MousePosition(p.X, p.Y); }
        // The window mode changed (Alt+Enter, SetRes, fullscreen <-> windowed). The original: ResizeViewport captures on
        // entering fullscreen (SetMouseCapture(1,1,0), WinDrv 0x1110eb82) and releases on leaving it ((0,0,0), 0x1110eb60);
        // windowed it captures only on a click (UGameEngine::MouseDelta, MOUSE_FirstHit -> SetMouseCapture(1,1,1)).
        // NOT ORIGINAL: the port stays captured windowed too, so
        // Core's state does not change on a switch and only the OS half is re-applied; without focus nothing is captured.
        // changed: the window's mode really differs from before the request (a SetRes to the same mode re-applies nothing).
        public void ViewportChanged(bool changed) { if (changed && _captured) OsCapture?.Invoke(true); }
        float _mouseX, _mouseY;
        bool _deviceBound;
        public int QueuedEvents => _evq.Count;
        // The gamepad reader Core polls in its tick (SdlGamepad.Read; the window's Core-thread mode hands it the state read on
        // the window thread before the tick, HPEngineProgram.Window).
        public UEInput.GamepadRead Gamepad = SdlGamepad.Read;
        // GetKeyState for an EInputKey name: any device control with that name is down.
        public bool IsKeyDown(string name) => _nameDown.TryGetValue(name, out int n) && n > 0;

        // ════════════════════════════════════════════════════════════════════
        // SDL EVENT INTAKE (in the SDL_PollEvent loop, before RunFrame)
        // ════════════════════════════════════════════════════════════════════

        public unsafe void Feed(in SDL_Event e)
        {
            switch ((SDL_EventType)e.type)
            {
                case SDL_EventType.SDL_EVENT_KEY_DOWN:
                case SDL_EventType.SDL_EVENT_KEY_UP:
                    OnKey(SdlKeyMap.LayoutScancode(e.key.scancode, e.key.key), e.key.down, e.key.repeat,
                        resetSignature: !e.key.down && e.key.which == 0 && e.key.raw == 0);
                    break;
                case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN:
                case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP:
                    OnMouseButton(e.button.button, e.button.down);
                    break;
                case SDL_EventType.SDL_EVENT_MOUSE_MOTION:
                    OnMouseMotion(e.motion.xrel, e.motion.yrel);
                    break;
                case SDL_EventType.SDL_EVENT_MOUSE_WHEEL:
                    if (e.wheel.direction == SDL_MouseWheelDirection.SDL_MOUSEWHEEL_FLIPPED) FlippedWheelEvents++;
                    OnMouseWheel(e.wheel.y);
                    break;
                case SDL_EventType.SDL_EVENT_TEXT_INPUT:
                    OnTextInput(Utf8(e.text.text));
                    break;
            }
        }

        // Window event. QUIT -> false. Focus: sound (Audio->SetViewport on WM_ACTIVATE/WM_SIZE) and UInput::ResetInput:
        // WinDrv WM_SETFOCUS / WM_KILLFOCUS (WinDrv.dll.c:3125, 3139). Minimize/restore do not reset input (WM_SIZE).
        public bool OnWindowEvent(in SDL_Event e, IInputSink sink, Action<bool> audioFocus)
        {
            switch ((SDL_EventType)e.type)
            {
                case SDL_EventType.SDL_EVENT_QUIT: return false;
                case SDL_EventType.SDL_EVENT_WINDOW_FOCUS_LOST: audioFocus(false); DropFocusLossReleases(); _evq.Add((Kind.Focus, null, default, 0f, default, false)); break;
                case SDL_EventType.SDL_EVENT_WINDOW_FOCUS_GAINED: audioFocus(true); _evq.Add((Kind.Focus, null, default, 1f, default, false)); break;
                case SDL_EventType.SDL_EVENT_WINDOW_MINIMIZED: audioFocus(false); break;
                case SDL_EventType.SDL_EVENT_WINDOW_RESTORED: audioFocus(true); break;
            }
            return true;
        }

        // Loading-screen hold: in the original OS messages during LoadMap wait in the queue and are handled by the
        // window procedure afterwards, so they go to Feed as in the main loop. QUIT -> false.
        public bool PumpLoadHold(in SDL_Event e, IInputSink sink, Action<bool> audioFocus)
        {
            if (!OnWindowEvent(e, sink, audioFocus)) return false;
            Feed(e);
            return true;
        }

        static unsafe string Utf8(byte* p)
        {
            if (p == null) return "";
            int n = 0;
            while (p[n] != 0) n++;
            return Encoding.UTF8.GetString(p, n);
        }

        // SDL 3.5.0 (ea7a2da, bundled by ppy.SDL3-CS 2026.1002.1) on focus loss: SDL_SetKeyboardFocus(NULL)
        // (src/events/SDL_keyboard.c:334-355) first calls SDL_ResetKeyboard (:224-236), which pushes a KEY_UP for every held
        // scancode with which = SDL_GLOBAL_KEYBOARD_ID (0) and raw = 0, then pushes WINDOW_FOCUS_LOST — synchronously, so
        // nothing lies between them. Every backend loses focus through it (cocoa windowDidResignKey, windows WM_ACTIVATE
        // deactivation, x11 FocusOut, wayland keyboard leave). Shipped WM_KILLFOCUS sends no Release: ResetInput clears
        // KeyDownTable and the inputs, and its UpdateInput(1) presses nothing without focus So the run of
        // such key-ups right before FOCUS_LOST is no input event; it only moves the device state, as GetKeyState follows the
        // keyboard. A key-up with that signature not followed by the focus loss (e.g. the Windows shift unstick,
        // SDL_windowsevents.c:2682) stays an ordinary release.
        void DropFocusLossReleases()
        {
            for (int i = _evq.Count - 1; i >= 0 && _evq[i].K == Kind.ResetRelease; i--)
                _evq[i] = _evq[i] with { K = Kind.FocusRelease };
        }

        public void OnKey(SDL_Scancode sc, bool down, bool repeat = false, bool resetSignature = false)
        {
            var c = SdlControl.Key(sc);
            string name = SdlKeyMap.NameOf(c);
            if (name is null)
            {
                if (down) CountUnknown(UnknownScancodes, sc, $"SDL key {sc} ({(int)sc}) is not named in the reference table HP1KeyTable — not sent to Core");
                return;
            }
            _evq.Add((resetSignature ? Kind.ResetRelease : Kind.Control, name, down ? UEInput.InputAction.Press : UEInput.InputAction.Release, 0f, c, repeat));
            // WM_CHAR control characters the SDL text input does not carry: Backspace 0x08, Tab 0x09, Escape 0x1b (WinDrv
            // posts WM_CHAR after the WM_KEYDOWN, autorepeat too; Enter 0x0d is not sent to KeyType, WinDrv.dll.c:3392).
            if (down)
            {
                int ch = name == "Backspace" ? 0x08 : name == "Tab" ? 0x09 : name == "Escape" ? 0x1b : 0;
                if (ch != 0) _evq.Add((Kind.Char, null, default, ch, default, false));
            }
        }

        public void OnMouseButton(int sdlButton, bool down)
        {
            var c = SdlControl.Button(sdlButton);
            string name = SdlKeyMap.NameOf(c);
            if (name is null)
            {
                if (down) CountUnknown(UnknownMouseButtons, sdlButton, $"SDL mouse button {sdlButton} is not named in the reference table HP1KeyTable — not sent to Core");
                return;
            }
            _evq.Add((Kind.Control, name, down ? UEInput.InputAction.Press : UEInput.InputAction.Release, 0f, c, false));
        }

        void CountUnknown<T>(Dictionary<T, int> counts, T key, string message)
        {
            counts.TryGetValue(key, out int n);
            counts[key] = n + 1;
            if (n == 0) Warn?.Invoke("[Input] " + message);
        }

        // WM_KEYDOWN/WM_SYSKEYDOWN -> CauseInputEvent(Press) on EVERY message, autorepeat included (the repeat bit is
        // not read, 0x1110b2d1-0x1110b2fa); WM_KEYUP -> Release. Alt+Enter: ToggleFullscreen and no input event
        // (0x1110b2d8-0x1110b2f1); the release of Enter still goes. Handled in RunFrame, in queue order.
        void OnControl(IInputSink sink, SdlControl c, string key, bool down, bool repeat, bool deliver = true)
        {
            _ctlDown.TryGetValue(c, out bool was);
            bool altHeld = IsDown(SdlKeyMap.LeftAltControl) || IsDown(SdlKeyMap.RightAltControl);
            if (down && key == "Enter" && altHeld)
            {
                // Author improvement (not original): autorepeat of Alt+Enter does not toggle again.
                if (!repeat) sink.ToggleFullscreen();
                if (!was) { _ctlDown[c] = true; _nameDown[key] = _nameDown.GetValueOrDefault(key) + 1; }
                return;
            }
            if (down != was)
            {
                _ctlDown[c] = down;
                _nameDown[key] = Math.Max(0, _nameDown.GetValueOrDefault(key) + (down ? 1 : -1));
            }
            else if (!down) return;   // a release of a key already up
            if (!deliver) return;     // focus-loss release: device state only
            sink.CauseInput(key, down ? UEInput.InputAction.Press : UEInput.InputAction.Release, 0f);
        }

        bool IsDown(SdlControl c) => _ctlDown.TryGetValue(c, out bool d) && d;

        // A WM_MOUSEMOVE batch: the frame's deltas are summed; the axis events go at the place of the first move.
        public void OnMouseMotion(float xrel, float yrel)
        {
            if (_mouseX == 0f && _mouseY == 0f) _evq.Add((Kind.Mouse, null, default, 0f, default, false));
            _mouseX += xrel; _mouseY -= yrel;
        }

        // WM_MOUSEWHEEL, per message: MouseW Axis with the raw delta (±120 per notch), then MouseWheelDown (delta < 0)
        // or MouseWheelUp: Press then Release (0x1110bec9..0x1110bf15).
        public void OnMouseWheel(float y)
        {
            if (y == 0f) return;
            _evq.Add((Kind.Wheel, null, default, y * 120f, default, false));
        }

        // WM_CHAR, per UTF-16 character -> Console.KeyType.
        public void OnTextInput(string text)
        {
            foreach (char ch in text) _evq.Add((Kind.Char, null, default, ch, default, false));
        }

        // ════════════════════════════════════════════════════════════════════
        // FRAME: the queued events go to Core in order, before the tick
        // ════════════════════════════════════════════════════════════════════

        public void RunFrame(IInputSink sink, float dt)
        {
            if (!_deviceBound) { sink.BindDevice(IsKeyDown); sink.BindGamepad(Gamepad); _deviceBound = true; }
            // SetMouseCapture (WinDrv FUN_11109280): the window captures the mouse (relative mode) once the viewport exists
            // — windowed as well, not original (see ViewportChanged).
            if (!_captured && _focused && sink.ConsoleReady) SetCapture(sink, true);
            foreach (var (k, key, action, delta, c, repeat) in _evq)
            {
                switch (k)
                {
                    case Kind.Control:
                    case Kind.ResetRelease:
                        OnControl(sink, c, key, action == UEInput.InputAction.Press, repeat);
                        break;
                    case Kind.FocusRelease:
                        OnControl(sink, c, key, false, false, deliver: false);
                        break;
                    case Kind.Mouse:
                        // WM_MOUSEMOVE (WinDrv.dll.c:3685-3812): captured -> MouseX/MouseY axes, otherwise MousePosition.
                        if (!_captured) MousePosition(sink);
                        else
                        {
                            if (_mouseX != 0f) sink.CauseInput("MouseX", UEInput.InputAction.Axis, _mouseX);
                            if (_mouseY != 0f) sink.CauseInput("MouseY", UEInput.InputAction.Axis, _mouseY);
                        }
                        _mouseX = _mouseY = 0f;
                        break;
                    case Kind.Wheel:
                        sink.CauseInput("MouseW", UEInput.InputAction.Axis, delta);
                        string w = delta < 0f ? "MouseWheelDown" : "MouseWheelUp";
                        sink.CauseInput(w, UEInput.InputAction.Press, 0f);
                        sink.CauseInput(w, UEInput.InputAction.Release, 0f);   // Core scrolls an open combo list on the Press
                        break;
                    case Kind.Char:
                        sink.CharInput((int)delta);
                        break;
                    case Kind.Focus:
                        // WM_KILLFOCUS: SetMouseCapture(0) then ResetInput (0x1110acf4/0x1110ad08); WM_SETFOCUS: ResetInput, then
                        // the cursor mode captures again (0x1110add4).
                        _focused = delta != 0f;
                        if (!_focused && _captured) SetCapture(sink, false);
                        sink.Focus(_focused);
                        if (_focused && !_captured && sink.ConsoleReady) SetCapture(sink, true);
                        break;
                }
            }
            _evq.Clear();
            _mouseX = _mouseY = 0f;
            _uwindow = sink.UWindowActive;
        }
    }
}
