// CORE STATE OWNER CHECK (debug): a frontend that runs the Core tick on a thread of its own (HPEngine, author decision
// 2026-11-12) may read Core state only while Core is idle — between ticks, or while the Core thread waits for a request it
// handed to the frontend thread (a synchronous presentation, a window-device call). HP1_CORE_THREAD_CHECK=1 turns the
// check on: a read of an instrumented Core entry point FROM THE FRONTEND THREAD while another thread owns Core is a
// violation, counted per site and reported loudly once per site with its stack. Off (the default) the static readonly
// flag is false and every Check folds away. Core's own workers (the lightmap task) are not the frontend thread and are
// not judged here.
using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;

namespace HP1.Core
{
    public static class UECoreOwner
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("HP1_CORE_THREAD_CHECK") == "1";

        static int _frontend;   // managed thread id of the frontend (window) thread; 0: nothing judged
        static int _owner;      // thread that owns Core state now; 0: Core idle

        public static int Violations;
        public static long Checks;
        public static readonly ConcurrentDictionary<string, int> Sites = new();

        public static void SetFrontendThread(int managedThreadId) => Volatile.Write(ref _frontend, managedThreadId);
        // The thread that owns Core from now on (0: Core idle, the frontend may read).
        public static void SetOwner(int managedThreadId) => Volatile.Write(ref _owner, managedThreadId);
        public static int Owner => Volatile.Read(ref _owner);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Check(string site)
        {
            if (Enabled) Judge(site);
        }

        static void Judge(string site)
        {
            int me = Environment.CurrentManagedThreadId;
            if (me != Volatile.Read(ref _frontend)) return;
            Interlocked.Increment(ref Checks);
            int owner = Volatile.Read(ref _owner);
            if (owner == 0 || owner == me) return;
            Interlocked.Increment(ref Violations);
            if (Sites.TryAdd(site, 1))
                Console.Error.WriteLine($"[CoreThread] VIOLATION: the frontend thread read Core state ({site}) while thread {owner} runs Core\n{Environment.StackTrace}");
            else Sites.AddOrUpdate(site, 1, (_, n) => n + 1);
        }
    }
}
