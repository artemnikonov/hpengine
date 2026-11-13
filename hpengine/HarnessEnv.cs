// HARNESS PROBE VARIABLES ON THE ENGINE: LOUD, NOT SILENT (D-19).
//
// HP1_TELEPORT(_AT), HP1_TRACE_MOVES/EVENTS/SETLOC/…, HP1_LEDGER and other comparison-log probes live in the
// harness native table on the UEGameEngineNatives engine hooks. The engine brings up
// the product Core table without them, so they do not affect the game. They are not moved into the engine: teleport
// WRITES game state (debug route bypass), and traces and the ledger are comparison log formats, hundreds of harness lines;
// in Core they would be a second owner of comparison diagnostics. Engine diagnostics are HP1_TRACE (Core UETrace)
// and the HPEngineData/logs startup log.
//
// Hence any set HP1_* the engine does not read is a stderr warning at startup.
// EngineReads: all HP1_* names read by HP1.Core and hpengine; it must stay in sync with the source literals.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace HPEngine
{
    static class HarnessEnv
    {
        internal static readonly string[] EngineReads =
        {
            "HP1_AS_PLAYER", "HP1_COMPACT_JOURNAL", "HP1_CORE_THREAD_CHECK", "HP1_DBG", "HP1_FOGDUMP", "HP1_FRAME_CACHE_VERIFY", "HP1_LOAD_WARM", "HP1_MEMLOG", "HP1_MEMROUTE",
            "HP1_PLAYLOG_REPEATS", "HP1_POOL_VERIFY", "HP1_PROCDBG", "HP1_SHOT_ALPHA",
            "HP1_TELEMETRY", "HP1_TICK_PROF", "HP1_TRACE", "HP1_TRACE_TIES", "HP1_WATCH",
        };

        // Set HP1_* variables the engine does not read, alphabetically.
        internal static List<string> Unread(IDictionary env)
            => env.Keys.Cast<object>().Select(k => k.ToString())
                  .Where(k => k.StartsWith("HP1_", StringComparison.Ordinal) && !EngineReads.Contains(k))
                  .OrderBy(k => k, StringComparer.Ordinal).ToList();

        internal static void WarnUnread()
        {
            foreach (string k in Unread(Environment.GetEnvironmentVariables()))
                Console.Error.WriteLine($"WARNING: {k} is set, but the HPEngine engine does not read it — it is a check-harness probe "
                    + "(runtime_poc), it does NOT affect the game (D-19)");
        }
    }
}
