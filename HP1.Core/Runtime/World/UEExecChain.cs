// VIEWPORT COMMAND EXECUTOR LOOKUP CHAIN.
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
// the port does not route key-bound native commands (DefUser.ini has none). The original
// executes such a binding; the port cannot, and a key press must not end the session, so the
// command stops at its stage with a loud line (journal input_exec_native_unported + a warning
// once per command) and nobody takes it. A script/console call of the same word
// (UEConsoleCommand) stays a STOP. The last stage (UEngine::Exec) further dispatches to
// sub-executors (Core StaticExec, USystem, Audio, Client, Render, FMemCache; table
// SubExecutorWords) and HP.exe's GExec (words unknown: packed exe); a command taken by no
// listed stage stays in the log as unrecognised (shipped ExecInputCommands does not check it).
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
        // UEngine::Exec's sub-executors (Engine 0x103932e0, asked before its own FLUSH/CRACKURL): GSys = USystem::Exec
        // (Core FUN_10173280), UObject::StaticExec (Core 0x1015cde0), GCache = FMemCache::Exec (Core 0x10128350,
        // DUMPCACHE above), GExec, Client = UWindowsClient::Exec -> UClient::Exec (Engine 0x10386cf0), Render =
        // URender::Exec (Render FUN_10b1f240), Audio = Galaxy Exec. Their top-level ParseCommand words, the counts and
        // the pushed UTF-16 constants checked against the disassembly. GExec is HP.exe's FExecHook::Exec: the shipped
        // HP.exe is packed (BitArts sections), its words cannot be read from the file — not listed (unknown).
        static readonly string[] SubExecutorWords = {
            "MEMSTAT", "CONFIGHASH", "RELAUNCH", "DEBUG",                 // USystem::Exec
            "MEM", "DUMPNATIVES", "GET", "SET", "OBJ", "GTIME",           // UObject::StaticExec
            "BRIGHTNESS",                                                 // UClient::Exec
            "STAT", "REND", "TLOD", "MLOD", "MLMODE", "MLFIX",            // URender::Exec
            "CDTRACK", "CDVOLUME", "MUSICORDER", "ASTAT" };               // Galaxy (audio subsystem) Exec
        static readonly string[] WinDrvWords = { "EndFullscreen", "ToggleFullscreen", "GetCurrentRes", "GetCurrentColorDepth",
            "GetColorDepths", "GetCurrentRenderDevice", "GetRes", "SetRes", "Preferences" };

        // Stages 2..5 and the WinDrv tail — after UInput::Exec declined.
        // Returns the object whose exec function took the command (null = nobody).
        public static UEObject ExecAfterInput(UEInterpreter interp, UEObject actor, string command, Action<string> log)
        {
            if (NativeStage(command, RenDevWords, "RenDev->Exec (URenderDevice/UD3DRenderDevice)", log)) return null;
            if (NativeStage(command, ViewportWords, "UViewport::Exec", log)) return null;
            UEObject console = (actor?.GetProp(PK.Player) as UEObject)?.GetProp(PK.Console) as UEObject;
            if (console is not null && ScriptConsoleExec(interp, console, command, actor, log)) return console;
            if (NativeStage(command, LevelWords, "ULevel::Exec", log)) return null;
            if (actor?.GetProp(PK.Level) is UEObject level && level.GetProp(PK.Game) is UEObject game
                && ScriptConsoleExec(interp, game, command, actor, log)) return game;
            if (actor?.GetProp(PK.myHUD) is UEObject hud && ScriptConsoleExec(interp, hud, command, actor, log)) return hud;
            if (actor is not null && ScriptConsoleExec(interp, actor, command, actor, log)) return actor;
            for (UEObject inv = actor?.GetProp(PK.Inventory) as UEObject; inv is not null; inv = inv.GetProp(PK.Inventory) as UEObject)
                if (ScriptConsoleExec(interp, inv, command, actor, log)) return inv;
            if (NativeStage(command, EngineWords, "UGameEngine::Exec/UEngine::Exec", log)) return null;
            if (NativeStage(command, SubExecutorWords, "a UEngine::Exec sub-executor (USystem, StaticExec, Client, Render, Audio)", log)) return null;
            if (NativeStage(command, WinDrvWords, "UWindowsViewport::Exec", log)) return null;
            log?.Invoke($"input_exec_unrecognized command=\"{command}\" "
                + "(none of the listed stages; HP.exe's GExec words are unknown)");
            return null;
        }

        // ParseCommand: the word at the start of the line (after spaces/tabs), case-insensitive,
        // followed by end of line, a space or a tab.
        // True = the stage takes the command (the chain stops here); the port does not execute it — loud, not fatal.
        // NOT ORIGINAL (the original runs such a binding): kept by the author's decision 2026-11-15 (D-89 (2)).
        static bool NativeStage(string command, string[] words, string stage, Action<string> log)
        {
            if (WordOf(command, words) is not string w) return false;
            log?.Invoke($"input_exec_native_unported command=\"{command}\" stage=\"{stage}\" word={w}");
            if (_nativeNoted.Add(command))   // once per distinct command: a held key repeats it
                HP1.Core.UETrace.Warn($"[input] key-bound command \"{command}\" is taken by native stage {stage} "
                    + $"(word {w}), which the port does not execute; the key does nothing");
            return true;
        }

        static readonly HashSet<string> _nativeNoted = new(StringComparer.OrdinalIgnoreCase);

        static string WordOf(string command, string[] words)
        {
            string s = command.TrimStart(' ', '\t');
            foreach (string w in words)
                if (s.Length >= w.Length && string.Compare(s, 0, w, 0, w.Length, StringComparison.OrdinalIgnoreCase) == 0
                    && (s.Length == w.Length || s[w.Length] == ' ' || s[w.Length] == '\t'))
                    return w;
            return null;
        }

        // A word of one of the listed native executors (the tables above), or null.
        public static string KnownNativeWord(string command)
            => WordOf(command, RenDevWords) ?? WordOf(command, ViewportWords) ?? WordOf(command, LevelWords)
               ?? WordOf(command, EngineWords) ?? WordOf(command, SubExecutorWords) ?? WordOf(command, WinDrvWords);

        // ── UObject::ScriptConsoleExec (Core.dll.c:16255-16398) ────────────────
        // Token (ParseToken, 0x40 / HP2 0x80, escape) -> function by name (FindObjectField:
        // VfHash of the state node, then of the class) -> FUNC_Exec (0x200) only ->
        // parameters in order while (flags & 0x480) == 0x80: the first object
        // parameter receives Executor if it IsA the property's class; the rest —
        // ParseNext + ImportText(PPF 1). A parse failure of a required
        // parameter -> BadProperty, the function is NOT called, but the command counts as "taken" (1);
        // failure of an optional one -> call with whatever was parsed. The parameter buffer
        // is zeroed: an unparsed parameter is the zero of its type.
        // HP2's token buffer is 0x80 characters (ParseToken's MaxLen [HP2 0x1015d76c]; the rest of the body is HP1's,
        // tools/hp2/native_match.py: frame size and that constant only).
        // Original: Core.dll ?ScriptConsoleExec@UObject@@UAEHPBGAAVFOutputDevice@@PAV1@@Z [HP1 0x1015a360] [HP2 0x1015d710]
        public static bool ScriptConsoleExec(UEInterpreter interp, UEObject obj, string command, UEObject executor,
            Action<string> log)
        {
            int pos = 0;
            string token = ParseToken(command, ref pos, ScriptConsoleExecTokenMax, useEscape: true);
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

        internal static int ScriptConsoleExecTokenMax => UEGameProfile.Current.Game == UEEdition.Game.HP2 ? 0x80 : 0x40;

        static object ZeroOf(UProperty p, UFunction fn) => p switch
        {
            UFloatProperty => 0f,
            UIntProperty => 0,
            UBoolProperty => false,
            UByteProperty => (byte)0,
            UNameProperty => new UEName("None"),
            UStrProperty => "",
            UObjectProperty => null,
            UStructProperty => UEObject.ZeroStructValue(p),
            _ => throw new NotSupportedException(
                $"ScriptConsoleExec: parameter {fn.Name}.{p.Name} of type {p.GetType().Name} — ImportText not ported"),
        };

        // ParseToken (Core.dll.c:50928).
        internal static string ParseToken(string s, ref int pos, int maxLen, bool useEscape)
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

        // UProperty::ImportText of a parameter with PortFlags 1 (not PPF_Delimited): the bodies and their return pointers
        // are UEObjectConfig.ImportItem's; NULL -> false.
        static bool ImportText(UProperty p, UFunction fn, string s, ref int pos, out object value)
        {
            value = ZeroOf(p, fn);
            int end = UEObjectConfig.ImportItem(p, s, Math.Min(pos, s.Length), 1, ref value, out _);
            if (end < 0) return false;
            pos = end;
            return true;
        }
    }
}
