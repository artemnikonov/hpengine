// VIEWPORT COMMAND EXECUTOR LOOKUP CHAIN (boot A5).
//
// Shipped order for a command that UInput::ExecInputCommands hands to the
// viewport on Press (Engine.dll.c:144880-144897 -> Viewport->Exec):
//   UWindowsViewport::Exec (WinDrv.dll.c:5049) FIRST calls UViewport::Exec and
//   only if it declines parses its own words (EndFullscreen … Preferences);
//   UViewport::Exec (0x10384560, Engine.dll.c:50068-50451):
//     1. Input->Exec   — UInput::Exec (BUTTON/PULSE/TOGGLE/AXIS/KEYNAME/
//        KEYBINDING and the alias table; in the port — UEInput.Exec, before this point);
//     2. RenDev->Exec  — URenderDevice::Exec (GetDetailTextures,
//        ToggleDetailTextures) + UD3DRenderDevice::Exec (GetRes, LodBias,
//        ShowPools; D3DDrv.dll.c:6773);
//     3. UViewport's own words (GETPING … SHOWBOUNDS);
//     4. Console->ScriptConsoleExec(Cmd, Ar, Actor)   (0x10385489..0x10385494);
//     5. UPlayer::Exec (0x103f9cd0, Engine.dll.c:74741):
//        XLevel->Exec (ULevel::Exec: DEMOREC, DEMOPLAY),
//        Level.Game, myHUD, the Actor itself, each Inventory along the chain
//        (+0x98 -> +0x98) — ScriptConsoleExec(Cmd, Ar, Actor);
//        XLevel->Engine->Exec (UGameEngine::Exec -> UEngine::Exec -> …).
//
// Native stage words are taken from the ParseCommand calls in their bodies. A command
// taken by a native stage is NOT silently executed by some other script in this chain:
// the port does not route key-bound native commands (DefUser.ini has none), so such a
// command is a loud refusal. The last stage (UEngine::Exec) further dispatches to
// sub-executors (Core StaticExec, USystem, Audio, Client, Render, FMemCache) whose
// tables are not listed here; a command taken by no listed stage stays in the log as
// unrecognised (shipped ExecInputCommands does not check it).
using System;
using System.Collections.Generic;
using UELib.Core;
using UELib.Flags;

namespace HP1.UEScript
{
    internal static class UEExecChain
    {
        static readonly string[] RenDevWords = { "GetDetailTextures", "ToggleDetailTextures", "GetRes", "LodBias", "ShowPools" };
        static readonly string[] ViewportWords = { "GETPING", "INJECT", "NETSPEED", "LANSPEED", "SHOWALL", "REPORT", "SHOT",
            "SNAP", "SAVESNAP", "SHOWACTORS", "HIDEACTORS", "RMODE", "EXEC", "SHOWBONES", "SHOWBOUNDS" };
        static readonly string[] LevelWords = { "DEMOREC", "DEMOPLAY" };
        static readonly string[] EngineWords = { "OPEN", "START", "SERVERTRAVEL", "DISCONNECT", "RECONNECT", "EXIT", "QUIT",
            "GETCURRENTTICKRATE", "GETMAXTICKRATE", "GSPYLITE", "OBJCLEAN", "SAVEGAME", "CANCEL",   // UGameEngine::Exec
            "FLUSH", "CRACKURL", "DUMPCACHE" };                                                      // UEngine::Exec, FMemCache
        static readonly string[] WinDrvWords = { "EndFullscreen", "ToggleFullscreen", "GetCurrentRes", "GetCurrentColorDepth",
            "GetColorDepths", "GetCurrentRenderDevice", "GetRes", "SetRes", "Preferences" };

        // Stages 2..5 and the WinDrv tail — after UInput::Exec declined.
        // Returns the object whose exec function took the command (null = nobody).
        public static UEObject ExecAfterInput(UEInterpreter interp, UEObject actor, string command, Action<string> log)
        {
            NativeStage(command, RenDevWords, "RenDev->Exec (URenderDevice/UD3DRenderDevice)");
            NativeStage(command, ViewportWords, "UViewport::Exec");
            UEObject console = (actor?.GetProp(PK.Player) as UEObject)?.GetProp(PK.Console) as UEObject;
            if (console is not null && ScriptConsoleExec(interp, console, command, actor, log)) return console;
            NativeStage(command, LevelWords, "ULevel::Exec");
            if (actor?.GetProp(PK.Level) is UEObject level && level.GetProp(PK.Game) is UEObject game
                && ScriptConsoleExec(interp, game, command, actor, log)) return game;
            if (actor?.GetProp(PK.myHUD) is UEObject hud && ScriptConsoleExec(interp, hud, command, actor, log)) return hud;
            if (actor is not null && ScriptConsoleExec(interp, actor, command, actor, log)) return actor;
            for (UEObject inv = actor?.GetProp(PK.Inventory) as UEObject; inv is not null; inv = inv.GetProp(PK.Inventory) as UEObject)
                if (ScriptConsoleExec(interp, inv, command, actor, log)) return inv;
            NativeStage(command, EngineWords, "UGameEngine::Exec/UEngine::Exec");
            NativeStage(command, WinDrvWords, "UWindowsViewport::Exec");
            log?.Invoke($"input_exec_unrecognized command=\"{command}\" "
                + "(none of the listed stages; UEngine::Exec sub-executors are not listed)");
            return null;
        }

        // ParseCommand: the word at the start of the line (after spaces/tabs), case-insensitive,
        // followed by end of line, a space or a tab.
        static void NativeStage(string command, string[] words, string stage)
        {
            string s = command.TrimStart(' ', '\t');
            foreach (string w in words)
                if (s.Length >= w.Length && string.Compare(s, 0, w, 0, w.Length, StringComparison.OrdinalIgnoreCase) == 0
                    && (s.Length == w.Length || s[w.Length] == ' ' || s[w.Length] == '\t'))
                    throw new NotSupportedException(
                        $"command \"{command}\" is taken by native stage {stage} (word {w}); "
                        + "key-bound native commands are not routed in the port");
        }

        // ── UObject::ScriptConsoleExec (Core.dll.c:16255-16398) ────────────────
        // Token (ParseToken, 0x40, escape) -> function by name (FindObjectField:
        // VfHash of the state node, then of the class) -> FUNC_Exec (0x200) only ->
        // parameters in order while (flags & 0x480) == 0x80: the first object
        // parameter receives Executor if it IsA the property's class; the rest —
        // ParseNext + ImportText(PPF 1). A parse failure of a required
        // parameter -> BadProperty, the function is NOT called, but the command counts as "taken" (1);
        // failure of an optional one -> call with whatever was parsed. The parameter buffer
        // is zeroed: an unparsed parameter is the zero of its type.
        public static bool ScriptConsoleExec(UEInterpreter interp, UEObject obj, string command, UEObject executor,
            Action<string> log)
        {
            int pos = 0;
            string token = ParseToken(command, ref pos, 0x40, useEscape: true);
            if (token.Length == 0) return false;
            UFunction fn = interp.FindEventFunction(obj, token);
            if (fn is null || !fn.FunctionFlags.HasFlag(FunctionFlag.Exec)) return false;

            var parms = new List<UProperty>();
            foreach (UProperty p in fn.EnumerateFields<UProperty>())
            {
                if (!p.PropertyFlags.HasFlag(PropertyFlag.Parm) || p.PropertyFlags.HasFlag(PropertyFlag.ReturnParm)) break;
                parms.Add(p);
            }
            var args = new object[parms.Count];
            for (int i = 0; i < parms.Count; i++) args[i] = ZeroOf(parms[i], fn);

            for (int i = 0; i < parms.Count; i++)
            {
                UProperty p = parms[i];
                if (i == 0 && executor is not null && p is UObjectProperty op
                    && (op.Object is not UClass pc || UEInterpreter.ClassIsA(executor.Class, pc.Name)))
                {
                    args[i] = executor;
                    continue;
                }
                ParseNext(command, ref pos);
                if (!ImportText(p, fn, command, ref pos, out object value))
                {
                    if (!p.PropertyFlags.HasFlag(PropertyFlag.OptionalParm))
                    {
                        log?.Invoke($"exec_bad_property function={fn.Name} param={p.Name}");
                        return true;
                    }
                    break;
                }
                args[i] = value;
            }
            interp.Invoke(obj, fn, args);
            return true;
        }

        static object ZeroOf(UProperty p, UFunction fn) => p switch
        {
            UFloatProperty => 0f,
            UIntProperty => 0,
            UBoolProperty => false,
            UByteProperty => (byte)0,
            UNameProperty => new UEName("None"),
            UStrProperty => "",
            UObjectProperty => null,
            _ => throw new NotSupportedException(
                $"ScriptConsoleExec: parameter {fn.Name}.{p.Name} of type {p.GetType().Name} — ImportText not ported"),
        };

        // ParseToken (Core.dll.c:50928).
        static string ParseToken(string s, ref int pos, int maxLen, bool useEscape)
        {
            var sb = new System.Text.StringBuilder();
            while (pos < s.Length && (s[pos] == ' ' || s[pos] == '\t')) pos++;
            if (pos < s.Length && s[pos] == '"')
            {
                pos++;
                int n = 1;
                while (pos < s.Length)
                {
                    char c = s[pos];
                    if (c == '"' || maxLen <= n) break;
                    pos++;
                    if (c == '\\' && useEscape)
                    {
                        if (pos >= s.Length) break;
                        c = s[pos++];
                    }
                    sb.Append(c);
                    n++;
                }
                if (pos < s.Length && s[pos] == '"') pos++;
            }
            else
            {
                int n = 1;
                while (pos < s.Length)
                {
                    char c = s[pos];
                    if (c == ' ' || c == '\t') break;
                    if (n < maxLen) { sb.Append(c); n++; }
                    pos++;
                }
            }
            return sb.ToString();
        }

        // ParseNext (Core.dll.c): spaces/tabs/CR/LF; ';' starts a comment to end of line.
        static void ParseNext(string s, ref int pos)
        {
            while (true)
            {
                while (pos < s.Length && (s[pos] == ' ' || s[pos] == '\t' || s[pos] == '\r' || s[pos] == '\n')) pos++;
                if (pos >= s.Length || s[pos] != ';') return;
                do
                {
                    if (s[pos] == '\n' || s[pos] == '\r') break;
                    pos++;
                } while (pos < s.Length);
            }
        }

        static char At(string s, int pos) => pos < s.Length ? s[pos] : '\0';

        // ImportText by type (Core.dll.c: Float 2075, Int 4375, Bool 33202,
        // Name 35556, Str 42562, Byte 65128) with flag 1 (not PPF_Delimited).
        static bool ImportText(UProperty p, UFunction fn, string s, ref int pos, out object value)
        {
            value = null;
            switch (p)
            {
                case UFloatProperty:
                    value = UEValue.LeadingAtof(s.Substring(pos));   // appAtof (Core 0x101264c0)
                    while (pos < s.Length && s[pos] != ',' && s[pos] != ')' && s[pos] != '\r' && s[pos] != '\n') pos++;
                    return true;
                case UIntProperty:
                {
                    int v = 0;
                    char c0 = At(s, pos);
                    if (c0 == '-' || (c0 >= '0' && c0 <= '9')) v = AppAtoi(s, pos);
                    while (At(s, pos) == '-' || (At(s, pos) >= '0' && At(s, pos) <= '9')) pos++;
                    value = v;
                    return true;
                }
                case UBoolProperty:
                {
                    if (!ReadToken(s, ref pos, 0x400, false, out string t)) return false;
                    // GTrue/GFalse are Core's localised "True"/"False".
                    if (Eq(t, "1") || Eq(t, "True")) { value = true; return true; }
                    if (Eq(t, "0") || Eq(t, "False")) { value = false; return true; }
                    return false;
                }
                case UNameProperty:
                {
                    if (!ReadToken(s, ref pos, 0x400, false, out string t)) return false;
                    value = new UEName(t.Length == 0 ? "None" : t);
                    return true;
                }
                case UStrProperty:
                    // Without PPF_Delimited — the WHOLE rest of the line; the pointer is NOT advanced.
                    value = s.Substring(Math.Min(pos, s.Length));
                    return true;
                case UByteProperty bp:
                {
                    if (bp.Enum is UEnum en)
                    {
                        if (!ReadToken(s, ref pos, 0x400, false, out string t)) return false;
                        var names = en.Names;
                        for (int i = 0; i < names.Count; i++)
                            if (Eq(names[i].ToString(), t)) { value = (byte)i; return true; }
                    }
                    char c = At(s, pos);
                    if (c >= '0' && c <= '9')
                    {
                        value = (byte)AppAtoi(s, pos);
                        while (At(s, pos) >= '0' && At(s, pos) <= '9') pos++;
                        return true;
                    }
                    return false;
                }
                default:
                    throw new NotSupportedException(
                        $"ScriptConsoleExec: parameter {fn.Name}.{p.Name} of type {p.GetType().Name} — ImportText not ported");
            }
        }

        static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        // Property token read (Core.dll.c:32281, thunk_FUN_1016c5e0): a quoted string
        // with escapes \\ and \XY; a non-alphanumeric character is a ONE-character token
        // without advancing the position; otherwise [A-Za-z0-9_-] (plus '.' when dots).
        // Overflow and an unterminated quote fail.
        static bool ReadToken(string s, ref int pos, int max, bool dots, out string token)
        {
            var sb = new System.Text.StringBuilder();
            token = "";
            char c = At(s, pos);
            if (c == '"')
            {
                int p = pos + 1, n = 0;
                while (true)
                {
                    char d = At(s, p);
                    if (d == '\0' || d == '"' || d == '\r' || d == '\n' || max - 1 <= n) break;
                    if (d == '\\')
                    {
                        char e = At(s, p + 1);
                        if (e == '\\') { sb.Append('\\'); n++; p += 2; continue; }
                        int hi = Hex(e), lo = Hex(At(s, p + 2));
                        sb.Append((char)(lo < 0 ? Math.Max(hi, 0) * 16 : Math.Max(hi, 0) * 16 + lo));
                        n++; p += 3;
                        continue;
                    }
                    sb.Append(d); n++; p++;
                }
                if (n == max - 1 || At(s, p) != '"') return false;
                pos = p + 1;
            }
            else if (!IsAlnum(c))
            {
                if (c != '\0') sb.Append(c);
            }
            else
            {
                int n = 0;
                while (n < max - 1)
                {
                    char d = At(s, pos);
                    if (!(IsAlnum(d) || d == '_' || d == '-' || (dots && d == '.'))) break;
                    sb.Append(d); n++; pos++;
                }
                if (n == max - 1) return false;
            }
            token = sb.ToString();
            return true;
        }

        static bool IsAlnum(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');

        // Hex digit for \XY: 0-9, a-f, A-F; anything else is 0 for the high digit, "none" for the low one.
        static int Hex(char c) => c >= '0' && c <= '9' ? c - '0'
            : c >= 'a' && c <= 'f' ? c - 'a' + 10
            : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;

        // (appAtoi — UEValue.LeadingAtoi, appAtof — UEValue.LeadingAtof.)
        static int AppAtoi(string s, int pos) => UEValue.LeadingAtoi(s.Substring(pos));
    }
}
