// IInputSink: what InputStage hands to Core — the WinDrv side of the original (UWindowsViewport). The call order is
// covered by tests; the Core implementation is CoreInputSink.
using System;
using HP1.UEScript;
namespace HPEngine.Input
{
    public interface IInputSink
    {
        // Viewport.Console exists.
        bool ConsoleReady { get; }
        // console.bUWindowActive (menu region width only).
        bool UWindowActive { get; }
        // SetMouseCapture (WinDrv FUN_11109280): Viewport.bWindowsMouseAvailable = !Capture (WinDrv.dll.c:11541); called on
        // capture changes only (window capture, focus loss), as WinDrv does.
        void MouseCapture(bool captured);
        // UGameEngine::MousePosition: Viewport.WindowsMouseX/Y = the OS cursor (Core UEInput.MousePosition); WinDrv calls it
        // for a mouse move while NOT captured.
        void MousePosition(float x, float y);
        // GetKeyState of the device, by EInputKey name (Core's UpdateInput polls it).
        void BindDevice(Func<string, bool> keyDown);
        // The gamepad in the joystick's place (Core UEInput.InitGamepad; improvement, not original): raw state read by Core's poll.
        void BindGamepad(UEInput.GamepadRead read);
        // UWindowsViewport::CauseInputEvent -> UEngine::InputEvent (Core UEInput.InputEvent).
        void CauseInput(string key, UEInput.InputAction action, float delta);
        // WM_CHAR -> UEngine::Key -> Console.KeyType (Core UELevelRuntime.ViewportCharInput).
        void CharInput(int ch);
        // WM_SETFOCUS / WM_KILLFOCUS: UInput::ResetInput (Core UEInput.ResetInputFull).
        void Focus(bool focused);
        // Alt+Enter: UWindowsViewport::ToggleFullscreen (WinDrv.dll.c:3335-3343), Core UEConsoleCommand.
        void ToggleFullscreen();
    }
}
