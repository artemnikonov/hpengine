using System;
using HP1.UEScript;
using System.Collections.Generic;
using System.IO;

namespace HP1.Core
{
    // ROUTE LOG — AN OBSERVER, NOT A PARTICIPANT.
    //
    // WHY SEPARATE FROM UETrace. `UETrace` is a per-frame trace for debugging one mechanism; over a level run
    // it produces a multi-megabyte dump where the first blocker cannot be found. This log answers "how far did
    // the route get and where did it stop", so it writes only ROUTE EVENTS, one line each.
    //
    // GUARANTEE. Nothing written here feeds back into the simulation: the class only reads already computed
    // values. A disabled log costs one bool check per observation point.
    //
    // REPEAT CAPS ARE REPORTED. `Walking -> Falling` happens thousands of times per run and would drown the log.
    // Repeats of one pair are collapsed, but not silently: `Close` prints how many lines of each kind were
    // suppressed. A silently truncated log would read as "this never happened".
    public static class UEPlaythroughLog
    {
        // How many times to print the same (kind, key) pair before collapsing. Three shows the first firing,
        // that it repeats, and keeps the third when debugging the pair.
        // ── THE COLLAPSE THRESHOLD IS CONFIGURABLE, NOT HARDCODED ─────────────
        //
        // 3 suits a NORMAL run read by a person asking "how far did the route get". ANALYSIS needs the opposite:
        // every repeated state and event in place, because a stall shows up exactly in repeats — a player
        // oscillating between two states looks like one transition at threshold 3.
        //
        // The default stays 3 (HP1_PLAYLOG_REPEATS raises it), so existing runs and logs are unchanged.
        // Suppressed lines are still counted and printed in the summary (no silent caps).
        public static int RepeatsShown { get; set; } = ParseRepeats();

        static int ParseRepeats()
        {
            string v = System.Environment.GetEnvironmentVariable("HP1_PLAYLOG_REPEATS");
            return int.TryParse(v, out int n) && n > 0 ? n : 3;
        }

        static TextWriter _out;
        // KEY IS A TUPLE, NOT A CONCATENATED STRING. A string key `kind + '\u0001' + detail` split back with
        // `Split` breaks if its invisible separator is lost in an edit (`Close` would throw IndexOutOfRange, on the
        // path that runs only when there is a repeated loud message). A tuple has nothing to lose.
        static readonly Dictionary<(string Kind, string Key), int> _seen = new();
        static readonly Dictionary<(string Kind, string Key), int> _suppressed = new();
        static int _written;

        public static bool Enabled { get; private set; }

        // Tick and map are published by the loop owner — the same data UETrace has, but the log must not depend
        // on whether tracing is enabled.
        public static int Tick;

        // NAMED SENTINEL "MAP NOT NAMED YET". It is also checked elsewhere: the deferred level snapshot uses it
        // to decide whether to substitute its own map. An `IsNullOrEmpty` check never fires because the field
        // is "?", not empty.
        public const string UnknownMap = "?";
        public static string Map = UnknownMap;

        public static string Path { get; private set; }

        // How many previous logs to keep next to the current one.
        const int KeepLogs = 12;

        // ROTATION LIVES IN THE GAME, NOT IN THE LAUNCHER. If a wrapper script rotated, launching the .app
        // directly (the usual way) would overwrite the previous log, since the file is opened with
        // FileMode.Create.
        //
        // The archive name uses the previous file's LAST WRITE time, not "now", so the stamp names the run the
        // log belongs to.
        static void RotatePrevious(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                string dir = System.IO.Path.GetDirectoryName(path);
                string archive = System.IO.Path.Combine(
                    string.IsNullOrEmpty(dir) ? "." : dir, "playlogs");
                Directory.CreateDirectory(archive);
                string stem = System.IO.Path.GetFileNameWithoutExtension(path);
                string stamp = File.GetLastWriteTime(path).ToString("yyyyMMdd_HHmmss");
                string dest = System.IO.Path.Combine(archive, $"{stem}_{stamp}.log");
                // Two launches within one second are rare, but then the stamp collides.
                for (int n = 1; File.Exists(dest) && n < 100; n++)
                    dest = System.IO.Path.Combine(archive, $"{stem}_{stamp}_{n}.log");
                File.Move(path, dest);

                var kept = new DirectoryInfo(archive).GetFiles(stem + "_*.log");
                Array.Sort(kept, (x, y) => y.LastWriteTimeUtc.CompareTo(x.LastWriteTimeUtc));
                for (int i = KeepLogs; i < kept.Length; i++) kept[i].Delete();
            }
            catch (Exception e)
            {
                // Losing a log is bad; failing to start is worse.
                UETrace.Warn($"[playlog] rotating the previous file failed: {e.Message}");
            }
        }

        public static void Open(string path)
        {
            Close();
            Path = path;
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            RotatePrevious(path);
            _out = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write,
                FileShare.ReadWrite)) { AutoFlush = true };
            _seen.Clear();
            _suppressed.Clear();
            _loudRepeats.Clear();
            _written = 0;
            _filtered = 0;
            Enabled = true;
            _out.WriteLine($"# tut1 playthrough log, started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            _out.WriteLine($"# format: <tick> <map> <kind> <details>");
            _out.WriteLine($"# previous runs: next to it in the playlogs subdirectory (keeps {KeepLogs})");

            // LOUD STAYS LOUD. Core warnings and errors are the blocker candidates, so the log CHAINS onto the
            // existing sinks instead of replacing them: the previous sink is called first and keeps working.
            // Otherwise a diagnostic build would "fix" a problem's visibility by hiding it from the console.
            // A SNAPSHOT REQUESTED BEFORE OPEN IS PICKED UP HERE: a frontend may build the world before opening
            // the log, and the first map's initial snapshot would otherwise be lost silently.
            UELevelSnapshot.FlushPending();

            _prevWarn = UETrace.Warn;
            _prevError = UETrace.Error;
            UETrace.Warn = msg => { _prevWarn?.Invoke(msg); AlwaysDistinct("warn", msg); };
            UETrace.Error = msg => { _prevError?.Invoke(msg); AlwaysDistinct("error", msg); };
        }

        static Action<string> _prevWarn, _prevError;

        // Event kind and REPEAT KEY are separate: the key decides what counts as "the same". The caller must
        // choose a key that keeps distinct facts apart (e.g. state:actor:from->to, not just actor).
        public static void Note(string kind, string key, string detail)
        {
            if (!Enabled) return;
            var bucket = (kind, key);
            _seen.TryGetValue(bucket, out int n);
            _seen[bucket] = n + 1;
            if (n >= RepeatsShown)
            {
                _suppressed.TryGetValue(bucket, out int s);
                _suppressed[bucket] = s + 1;
                return;
            }
            _out.WriteLine($"{Tick} {Map} {kind} {detail}");
            _written++;
        }

        // An event that is NEVER collapsed: route checkpoint, travel, loud STOP. Collapsing them would lose
        // exactly what the log exists for.
        public static void Always(string kind, string detail)
        {
            if (!Enabled) return;
            _out.WriteLine($"{Tick} {Map} {kind} {detail}");
            _written++;
        }

        // LOUD, BUT NOT REPEATED INTO UNREADABILITY.
        //
        // Measured: 25 seconds in a built player gave 38 485 lines, 37 752 of them the SAME missing sprite shader
        // error. A log where the first blocker appears 37 thousand times answers nothing.
        //
        // NOTHING IS LOST: every DISTINCT text is printed once in full with its tick; repeats are counted and
        // printed as a number in the summary (no silent caps: suppressed means named).
        static readonly Dictionary<(string Kind, string Detail), int> _loudRepeats = new();

        public static void AlwaysDistinct(string kind, string detail)
        {
            if (!Enabled) return;
            var key = (kind, detail);
            if (_loudRepeats.TryGetValue(key, out int n))
            {
                _loudRepeats[key] = n + 1;
                return;
            }
            _loudRepeats[key] = 1;
            _out.WriteLine($"{Tick} {Map} {kind} {detail}");
            _written++;
        }

        // ── ROUTE CHECKPOINTS ─────────────────────────────────────────────
        //
        // The list is NOT hardcoded here: it is produced by the same analysis as the playability map
        // (a backward closure from the CHANGELEVEL action) and supplied by
        // the loop owner. Hardcoding it would create a SECOND owner of "what is mandatory on the route" that
        // would silently drift from that analysis.
        //
        // The marker ONLY OBSERVES: it does not teleport, open doors or advance progress. Each checkpoint is
        // printed once; passing it again shows in the ordinary log lines.
        static readonly Dictionary<string, string> _checkpoints = new(UEPropNameComparer.Instance);
        static readonly HashSet<string> _reached = new(UEPropNameComparer.Instance);

        public static void SetCheckpoints(IEnumerable<KeyValuePair<string, string>> pairs)
        {
            _checkpoints.Clear();
            _reached.Clear();
            if (pairs == null) return;
            foreach (var pair in pairs) _checkpoints[pair.Key] = pair.Value;
        }

        public static int CheckpointCount => _checkpoints.Count;

        public static void MarkIfCheckpoint(string actorName, string why)
        {
            if (!Enabled || actorName == null || _checkpoints.Count == 0) return;
            if (!_checkpoints.TryGetValue(actorName, out string label)) return;
            if (!_reached.Add(actorName)) return;
            Always("checkpoint", $"{_reached.Count}/{_checkpoints.Count} {actorName} "
                + $"[{label}] by {why}");
        }

        // Events dropped by the "log follows the player" filter. Counted separately from suppressed repeats:
        // these are two different answers to "what is missing from the log".
        static int _filtered;
        public static void CountFiltered() { if (Enabled) _filtered++; }

        public static void Close()
        {
            if (_out == null) return;
            _out.WriteLine($"# lines: {_written}");
            _out.WriteLine($"# events filtered out without player involvement: {_filtered}");
            int loud = 0;
            foreach (var pair in _loudRepeats) if (pair.Value > 1) loud++;
            if (loud > 0)
            {
                _out.WriteLine($"# LOUD MESSAGES WITH REPEATS: {loud} "
                    + "(each printed once above; here — how many times in total)");
                foreach (var pair in _loudRepeats)
                {
                    if (pair.Value <= 1) continue;
                    _out.WriteLine($"#   x{pair.Value} {pair.Key.Kind} {pair.Key.Detail}");
                }
            }
            _out.WriteLine($"# checkpoints reached: {_reached.Count}/{_checkpoints.Count}");
            foreach (var pair in _checkpoints)
                if (!_reached.Contains(pair.Key))
                    _out.WriteLine($"#   NOT REACHED: {pair.Key} [{pair.Value}]");
            if (_suppressed.Count > 0)
            {
                _out.WriteLine($"# repeats suppressed: {_suppressed.Count} kinds "
                    + $"(threshold {RepeatsShown} per kind)");
                foreach (var pair in _suppressed)
                {
                    _out.WriteLine($"#   {pair.Key.Kind} {pair.Key.Key}: +{pair.Value}");
                }
            }
            _out.Flush();
            _out.Dispose();
            _out = null;
            Enabled = false;
            // Restore the sinks that existed before Open, so reopening does not build a chain of wrappers.
            if (_prevWarn != null) { UETrace.Warn = _prevWarn; _prevWarn = null; }
            if (_prevError != null) { UETrace.Error = _prevError; _prevError = null; }
        }
    }
}
