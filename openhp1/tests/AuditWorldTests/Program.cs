// Comparison of the Core world lifecycle against shipped HP1 1.53, no game files needed.
// Input is UEConsoleCommand.Execute, the same one the VM ConsoleCommand native calls
// (runtime_poc/HeadlessSmoke.cs ConsoleCommand -> Execute).
// Expectations are computed here by hand from the shipped body:
//   UGameEngine::Exec, SAVEGAME branch (Engine.dll.c:244086-244098):
//     ParseCommand(&Cmd, "SAVEGAME")  — Core.dll.c:51670: spaces/tabs before the word
//       are skipped, the word is case-insensitive, the next character is NOT [A-Za-z0-9],
//       then spaces/tabs are skipped;
//     if (*Cmd < '0' || *Cmd > '9') return 1;   — accepted silently, no save, empty output;
//     SaveGame(appAtoi(Cmd));                   — leading digits.
// Red mutations — redtest.sh.
using HP1.UEScript;

static class Program
{
    static int _fail, _pass;

    static void Check(string name, bool ok, string detail)
    {
        if (ok) _pass++; else { _fail++; Console.WriteLine($"FAIL {name}: {detail}"); }
    }

    // What the command did: the saved slot number (null — no save) and the output,
    // or an exception (command not recognised as SAVEGAME, or a port refusal).
    static (int? Slot, string Out, string Error) Run(string command)
    {
        int? slot = null;
        UEConsoleCommand.SaveGameHandler = s => { slot = s; return ""; };
        try { string output = UEConsoleCommand.Execute(null, command); return (slot, output, null); }
        catch (Exception e) { return (slot, null, e.GetType().Name); }
        finally { UEConsoleCommand.SaveGameHandler = null; }
    }

    static void ExpectSave(string command, int slot)
    {
        var r = Run(command);
        Check($"\"{command}\"", r.Error is null && r.Slot == slot && r.Out == "",
            $"expected SaveGame({slot}) and empty output, got slot={r.Slot?.ToString() ?? "none"} out=\"{r.Out}\" err={r.Error}");
    }

    static void ExpectSilent(string command)
    {
        var r = Run(command);
        Check($"\"{command}\"", r.Error is null && r.Slot is null && r.Out == "",
            $"expected \"accepted silently\" (no save, output empty), got slot={r.Slot?.ToString() ?? "none"} out=\"{r.Out}\" err={r.Error}");
    }

    static void ExpectNotSaveGame(string command)
    {
        var r = Run(command);
        Check($"\"{command}\"", r.Slot is null,
            $"ParseCommand does not recognise SAVEGAME, but the port saved slot {r.Slot}");
    }

    // EXIT/QUIT and OPEN (UGameEngine::Exec, Engine.dll.c:243987 / 244045): the same ParseCommand.
    static (bool Exited, string Opened, string Error) RunExitOpen(string command)
    {
        bool exited = false; string opened = null;
        UEConsoleCommand.ExitHandler = () => exited = true;
        UEConsoleCommand.OpenHandler = u => { opened = u; return ""; };
        try { UEConsoleCommand.Execute(null, command); return (exited, opened, null); }
        catch (Exception e) { return (exited, opened, e.GetType().Name); }
        finally { UEConsoleCommand.ExitHandler = null; UEConsoleCommand.OpenHandler = null; }
    }

    static void ExpectExit(string command, bool exit)
    {
        var r = RunExitOpen(command);
        Check($"exit \"{command}\"", r.Exited == exit,
            $"ParseCommand(EXIT/QUIT): expected exit={exit}, got {r.Exited} err={r.Error}");
    }

    static void ExpectOpen(string command, string url)
    {
        var r = RunExitOpen(command);
        Check($"open \"{command}\"", r.Opened == url,
            $"ParseCommand(OPEN): expected URL \"{url ?? "none"}\", got \"{r.Opened ?? "none"}\" err={r.Error}");
    }

    static int Main()
    {
        ExpectExit("exit", true);
        ExpectExit("QUIT", true);
        ExpectExit("  exit", true);              // spaces before the word
        ExpectExit("exit\t", true);             // a tab after the word is not a letter/digit
        ExpectExit("quit;", true);               // ';' is not a letter/digit
        ExpectExit("exits", false);              // a letter after the word
        ExpectOpen("open save99.usa", "save99.usa");
        ExpectOpen("OPEN\tsave3.usa", "save3.usa");   // a tab is a word boundary
        ExpectOpen("  open Lev_Tut2", "Lev_Tut2");
        ExpectOpen("opensave1.usa", null);       // a letter after the word — not OPEN

        ExpectSave("SaveGame 3", 3);
        ExpectSave("savegame 12", 12);           // the word is case-insensitive
        ExpectSave("SaveGame\t7", 7);            // a tab is not a letter/digit, then tabs are skipped
        ExpectSave("  SaveGame 2", 2);           // spaces before the word
        ExpectSave("SaveGame 5abc", 5);          // appAtoi: leading digits
        ExpectSave("SaveGame   0", 0);
        ExpectSilent("SaveGame x");              // not a digit — return 1 without saving
        ExpectSilent("SaveGame -1");             // '-' < '0'
        ExpectSilent("SaveGame +4");             // '+' < '0'
        ExpectSilent("SaveGame");                // empty remainder: '\0' < '0'
        ExpectSilent("SaveGame ");
        ExpectNotSaveGame("SaveGames 1");        // a letter after the word — not SAVEGAME
        ExpectNotSaveGame("SaveGame1");          // a digit after the word — not SAVEGAME

        Console.WriteLine($"AuditWorldTests: {_pass} pass, {_fail} fail");
        return _fail == 0 ? 0 : 1;
    }
}
