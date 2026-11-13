using System;

namespace HP1.Core
{
    // Diagnostic trigger/state/mover trace. Enabled by HP1_TRACE=1, OFF by
    // default. Writes to stderr so the SHA-pinned stdout journals stay
    // byte-identical whether or not the trace runs. Read-only: never feeds a
    // value back into the simulation.
    public static class UETrace
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("HP1_TRACE") == "1";

        // Current tick, published by the host's tick loop for line context.
        public static int Tick;

        public static void Log(string line)
        {
            if (Enabled) Console.Error.WriteLine($"trace tick={Tick} {line}");
        }

        // Warning sink for Core code. Frontends may reassign it at bootstrap;
        // the default keeps warnings loud on stderr.
        public static Action<string> Warn = msg => Console.Error.WriteLine(msg);

        // Error/info sinks for the interpreter's diagnostics. Same contract as Warn:
        // frontends may reassign them at bootstrap, defaults stay on stderr so
        // stdout journals are untouched.
        public static Action<string> Error = msg => Console.Error.WriteLine(msg);
        public static Action<string> Info  = msg => Console.Error.WriteLine(msg);

        // Playthrough diagnostics (ClientMessage text with time and place): off by default, so headless runs and their
        // pinned journals are untouched; the HPEngine window points it at its log.
        public static Action<string> Playthrough;
    }
}
