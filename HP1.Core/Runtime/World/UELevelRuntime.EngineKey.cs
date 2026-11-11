// CHARACTER INPUT: WinDrv WM_CHAR -> UGameEngine::Key -> UEngine::Key -> Console.KeyType.
//
// Shipped chain:
//   * UWindowsViewport::ViewportWndProc, WM_CHAR (0x102) and WM_SYSCHAR (0x106)
//     (WinDrv.dll.c:3392-3400): character 0xd (Enter) is NOT passed to the engine;
//     otherwise Client->Engine->Key(this, wParam) (engine vtable +0x74). The rest of
//     this branch is window repaint and DefWindowProc, the platform side.
//   * UGameEngine::Key (0x103a2b50, Engine.dll.c:79947): UEngine::Key and, on
//     success, a demo frame record (FReplay, this+0xe8 && this[0xf8]); the port has
//     no demo recording, so that tail is not executed.
//   * UEngine::Key (0x103937d0, disasm 0x103937f1..0x10393855): if the viewport
//     has a Console (+0x34) — Console.eventKeyType((BYTE)Key)
//     (FName 0xb3, argument is a byte, returns bool); its result is the
//     function's return. Without a console — 0.
//
// Receivers in shipped data: HPConsole state UWindow (-> Root.WindowEvent
// (WM_KeyType)), Engine.Console states Typing/MenuTyping/KeyMenuing; in the
// other states the Console event body is empty (returns false).
// The name KeyType is outside the probe range (0xb3 < 0xc5): the probe-mask
// gate does not touch it.
//
// Owner is Core: the frontend only hands the platform character to
// ViewportCharInput and does nothing else with it.
using UELib.Core;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        // UWindowsViewport::ViewportWndProc, WM_CHAR/WM_SYSCHAR branch.
        public bool ViewportCharInput(UEInterpreter interp, int ch)
        {
            if (ch == 0xd) return false;
            return EngineKey(interp, Viewport, ch);
        }

        // UGameEngine::Key -> UEngine::Key.
        public bool EngineKey(UEInterpreter interp, UEObject viewport, int key)
        {
            if (viewport?.GetProp(PK.Console) is not UEObject console) return false;
            // ProcessEvent: FindFunctionChecked (Console always declares the
            // event); no body — default return 0.
            UFunction fn = interp.FindEventFunction(console, "KeyType");
            if (fn is null) return false;
            object result = null;
            UELifecycle.Guard(() => result = interp.Invoke(console, fn, (byte)key), "KeyType");
            Log($"engine_key key={key & 0xff} console_state={console.CurrentStateName} result={UEInterpreter.ToBool(result)}");
            return UEInterpreter.ToBool(result);
        }
    }
}
