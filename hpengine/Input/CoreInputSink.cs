// CoreInputSink: InputStage sink over Core (UEInput, Viewport.Console, LevelInfo).
// No bodies of its own: each method calls into Core (see IInputSink for what each one replaces).
//
// The player is the player's VM object. Who it is in HPEngine is decided by the host: passed as a delegate,
// Viewport.Actor by default (the same object the menu axes are addressed to).
using System;
using HP1.UEScript;
using UELib.Core;

namespace HPEngine.Input
{
    public sealed class CoreInputSink : IInputSink
    {
        readonly UEInterpreter _interp;
        readonly UELevelRuntime _rt;
        readonly Func<UEObject> _player;

        // Key name missing from EInputKey: counted and warned about instead of silently skipped.
        public int UnresolvedKeys;
        public Action<string> Warn;

        public CoreInputSink(UEInterpreter interp, UELevelRuntime rt, Func<UEObject> player = null)
        {
            _interp = interp ?? throw new ArgumentNullException(nameof(interp));
            _rt = rt ?? throw new ArgumentNullException(nameof(rt));
            _player = player ?? (() => _rt.Viewport?.GetProp("Actor") as UEObject);
        }

        UEObject Console => _rt.ViewportConsole;
        UEObject Player => _player();

        public bool ConsoleReady => Console is not null;

        public bool UWindowActive => Console is UEObject c && UEInterpreter.ToBool(c.GetProp("bUWindowActive"));

        public void MouseCapture(bool captured) => UEInput.SetMouseCapture(Console, captured);

        public void MousePosition(float x, float y) => UEInput.MousePosition(Console, x, y);

        public void BindDevice(Func<string, bool> keyDown) => _rt.Input.DeviceDown = keyDown;

        public void BindGamepad(UEInput.GamepadRead read)
            => _rt.Input.InitGamepad(_rt.Config, Environment.CommandLine, read);

        public void CauseInput(string key, UEInput.InputAction action, float delta)
        {
            if (UEConsoleCommand.FindKeyName(key) < 0)
            {
                if (UnresolvedKeys++ == 0) Warn?.Invoke($"[Input] key IK_{key} is not in EInputKey — not sent");
                return;
            }
            _rt.Input.InputEvent(_interp, Console, Player, key, action, delta);
        }

        public void CharInput(int ch) => _rt.ViewportCharInput(_interp, ch);

        public void Focus(bool focused)
        {
            _rt.Input.DeviceFocused = focused;
            _rt.Input.ResetInputFull(_interp, Console, Player);
        }


        // Window device (the same as Core's UseViewportDevice); without it Alt+Enter is a loud refusal.
        public IViewportDevice Viewport;

        public void ToggleFullscreen()
        {
            if (Viewport is null) { Warn?.Invoke("[Input] Alt+Enter: the sink has no window device — screen not switched"); return; }
            UEConsoleCommand.Execute(_rt.Config, "ToggleFullscreen", null, Viewport);
        }
    }
}
