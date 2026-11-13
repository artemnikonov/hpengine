// CORE TICK ON ITS OWN THREAD (author decision 2026-11-12: Steam Deck drew ~60 fps of 90 with Core and drawing on one thread).
//
// The window (main) thread keeps SDL: window, events, input polling, gamepad, GPU, the OS half of mouse capture. The Core
// thread runs one job per logic tick — PrepareStep, InputStage.RunFrame, UEGameEngine.Tick — on the schedule FramePacer
// gives the window, posted by the window thread, so the input of a tick, its dt and its screen size are decided exactly
// where the single-thread loop decides them. While a job runs the window thread draws only from frontend-owned data
// (the tick's frame taken while Core was idle, HPEngineProgram.TakeTick); it reads Core again only when the job is done.
//
// What Core calls back during a tick that belongs to the window thread (window device, synchronous presentations of
// LoadMap, the mouse capture, the first gamepad read) is marshalled: the Core thread posts the request and waits for its
// result (MainThread.Invoke), so the semantics the scripts rely on (GetRes / SetRes answers in the same tick) hold.
// While it waits, the window thread owns Core (UECoreOwner) and may read it.
//
// The single-thread loop (--single-thread) stays the judge baseline; --shot, --video and the probes never use this.
using System.Runtime.ExceptionServices;
using HP1.Core;
using HP1.UEScript;

namespace HPEngine
{
    static class MainThread
    {
        static int _id;
        static readonly Queue<Action> _queue = new();
        // Set when a request is queued or a job ends: the window thread's sleep returns early.
        public static readonly AutoResetEvent Wake = new(false);

        public static void Bind() { _id = Environment.CurrentManagedThreadId; UECoreOwner.SetFrontendThread(_id); }
        public static bool OnMain => _id == 0 || Environment.CurrentManagedThreadId == _id;
        public static bool Pending { get { lock (_queue) return _queue.Count > 0; } }

        // Runs `f` on the window thread and returns its result; the calling Core thread waits, handing Core to the window
        // thread for that time. On the window thread itself: a direct call.
        public static T Invoke<T>(Func<T> f)
        {
            if (OnMain) return f();
            T result = default; ExceptionDispatchInfo error = null;
            using var done = new ManualResetEventSlim(false);
            // Core is handed over BEFORE the request is queued: the window thread may run it at once.
            int me = Environment.CurrentManagedThreadId;
            UECoreOwner.SetOwner(_id);
            Enqueue(() =>
            {
                try { result = f(); } catch (Exception e) { error = ExceptionDispatchInfo.Capture(e); }
                done.Set();
            });
            done.Wait();
            UECoreOwner.SetOwner(me);
            error?.Throw();
            return result;
        }
        public static void Invoke(Action a) => Invoke<bool>(() => { a(); return true; });

        // Without waiting (window-only effects Core does not read back).
        public static void Post(Action a) { if (OnMain) a(); else Enqueue(a); }

        static void Enqueue(Action a) { lock (_queue) _queue.Enqueue(a); Wake.Set(); }

        // Window thread: run the queued requests in order.
        public static long Served;   // requests of the Core thread run here (the [Perf] line: the marshalled branch ran)
        public static int Service()
        {
            int n = 0;
            while (true)
            {
                Action a;
                lock (_queue) { if (_queue.Count == 0) return n; a = _queue.Dequeue(); }
                a(); n++; Served++;
            }
        }
    }

    // The Core thread: one job at a time, posted by the window thread.
    sealed class CoreThread : IDisposable
    {
        readonly Thread _thread;
        readonly SemaphoreSlim _go = new(0);
        Action _job;
        volatile bool _busy, _stop;
        ExceptionDispatchInfo _error;

        public CoreThread()
        {
            // Stack as large as the main thread's (8 MiB on macOS/Linux) and then some: the VM recurses through script.
            _thread = new Thread(Run, 64 << 20) { Name = "Core", IsBackground = true };
            _thread.Start();
        }

        public bool Busy => _busy;
        public int ThreadId => _thread.ManagedThreadId;

        public void Start(Action job)
        {
            if (_busy) throw new InvalidOperationException("CoreThread: a tick job is still running");
            _job = job;
            _busy = true;
            UECoreOwner.SetOwner(_thread.ManagedThreadId);
            _go.Release();
        }

        void Run()
        {
            while (true)
            {
                _go.Wait();
                if (_stop) return;
                try { _job(); }
                catch (Exception e) { _error = ExceptionDispatchInfo.Capture(e); }
                _job = null;
                UECoreOwner.SetOwner(0);
                _busy = false;
                MainThread.Wake.Set();
            }
        }

        // Window thread, job done: a Core exception surfaces here as in the single-thread loop.
        public void ThrowIfFailed() { var e = _error; _error = null; e?.Throw(); }

        public void Dispose() { _stop = true; _go.Release(); }
    }

    // The window device seen from the Core thread: every member runs on the window thread, the Core thread waits.
    sealed class MarshalledViewportDevice(IViewportDevice inner) : IViewportDevice
    {
        public int SizeX => MainThread.Invoke(() => inner.SizeX);
        public int SizeY => MainThread.Invoke(() => inner.SizeY);
        public int ColorBytes => MainThread.Invoke(() => inner.ColorBytes);
        public bool Fullscreen => MainThread.Invoke(() => inner.Fullscreen);
        public string DisplayModes => MainThread.Invoke(() => inner.DisplayModes);
        public ViewportResizeOutcome Resize(int sizeX, int sizeY, int colorBytes, bool fullscreen)
            => MainThread.Invoke(() => inner.Resize(sizeX, sizeY, colorBytes, fullscreen));
    }

    // JUDGE SESSION (--window-script <file>): the window loop on a fixed step with a scripted input sequence, so the single-
    // thread and the Core-thread loops can be compared tick by tick (--digest). Lines "<tick> <verb> <args>", '#' comments:
    //   key <SDL scancode name, e.g. W, ESCAPE> down|up   — InputStage.OnKey (the window's key route)
    //   mouse <dx> <dy>                                   — InputStage.OnMouseMotion
    //   text <characters>                                 — InputStage.OnTextInput (WM_CHAR)
    //   console <command>                                 — UEConsoleCommand.Execute on the Core thread before the tick's input
    //   pad <lx> <ly> <rx> <ry> <lt> <rt> <buttons> | pad none — the gamepad state Core reads from that tick on
    //   probe                                             — after the tick, log the menu state (page, control under the
    //                                                       cursor, Brightness, Interface Size, an open combo list)
    //   kill                                              — baseHarry.KillHarry(true) on the Core thread before the tick's input
    //                                                       (UEGameEngine.KillPlayerProbe: death -> stateDead -> slot 99 reload)
    //   quit                                              — no tick is run from this one on
    // Events of tick k are handed to InputStage where the loop hands it the polled OS events of that tick (both loops), so
    // a late or early delivery in either loop changes the digest. Real OS input is ignored, the cursor is (0, 0), the mouse
    // is never captured, the clocks Core reads (dt, LoadMap fade clock, local time) are fixed.
    //
    // STRESS (--window-stress, audit/core_thread_judge.sh): the key and mouse lines of tick k do not go to InputStage at the
    // dispatch; they are pushed into the SDL event queue (SDL_PushEvent) when tick k-1 is posted — events that arrive while
    // Core runs a tick — and reach InputStage only through the loop's own SDL_PollEvent. So the digest equals the injected
    // run's only if every tick is posted after the events queued before it were polled. Core ticks are slowed (20·(k mod 3)
    // ms, so ticks run late), and on every second tick the window loop, having found Core busy at the poll, waits for the
    // tick to end before it decides the dispatch: the interleaving in which a second read of the busy flag posts a tick
    // without its events. text and pad lines stay injected (a text event carries a pointer; the pad is not an event).
    sealed class WindowScript
    {
        public const float Dt = 1f / 60f;
        // Marks the pushed events (SDL keeps a non-zero timestamp): the isolated window ignores every other OS event.
        public const ulong OsStamp = 1;
        public bool OsEvents;
        public int Pushed;
        readonly List<(int Tick, string Verb, string Args)> _lines = new();
        public int QuitTick = int.MaxValue;
        bool _padOk; UEInput.GamepadState _pad;
        double _clock;

        public WindowScript(string path)
        {
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                string[] p = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                int tick = int.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture);
                if (p[1] == "quit") { QuitTick = Math.Min(QuitTick, tick); continue; }
                _lines.Add((tick, p[1], p.Length > 2 ? p[2] : ""));
            }
        }

        // Fixed clock for LoadMap's fade (UEGameEngineNatives.WallSeconds): one step per read.
        public double WallSeconds() => _clock += Dt;

        public IEnumerable<string> ConsoleAt(int tick)
        {
            foreach (var (t, verb, args) in _lines) if (t == tick && verb == "console") yield return args;
        }
        public bool ProbeAt(int tick) => _lines.Exists(l => l.Tick == tick && l.Verb == "probe");
        public bool KillAt(int tick) => _lines.Exists(l => l.Tick == tick && l.Verb == "kill");

        // Stress: the tick's key and mouse lines into the SDL event queue, in file order (window thread).
        public unsafe void Push(int tick)
        {
            if (!OsEvents) return;
            foreach (var (t, verb, args) in _lines)
            {
                if (t != tick || verb is not ("key" or "mouse")) continue;
                string[] a = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                SDL.SDL_Event e = default;
                if (verb == "key")
                {
                    bool down = a[1] == "down";
                    e.key.type = down ? SDL.SDL_EventType.SDL_EVENT_KEY_DOWN : SDL.SDL_EventType.SDL_EVENT_KEY_UP;
                    // No keycode: the key is its scancode whatever the machine's layout (SdlKeyMap.LayoutScancode), as the
                    // injected line; raw != 0: not the SDL_ResetKeyboard signature (InputStage.Feed).
                    e.key.scancode = Enum.Parse<SDL.SDL_Scancode>("SDL_SCANCODE_" + a[0], true); e.key.down = down; e.key.raw = 1;
                }
                else { e.motion.type = SDL.SDL_EventType.SDL_EVENT_MOUSE_MOTION; e.motion.xrel = F(a[0]); e.motion.yrel = F(a[1]); }
                e.common.timestamp = OsStamp;
                if (!SDL.SDL3.SDL_PushEvent(&e)) throw new InvalidOperationException($"window script: SDL_PushEvent refused '{verb} {args}' of tick {tick}");
                Pushed++;
            }
        }

        // The menu state after a tick (Core thread): which page, which control the cursor is over, the Options values.
        public static string MenuProbe(UEGameEngine host)
        {
            UEObject con = host.Runtime.ViewportConsole;
            UEObject book = con?.GetProp("menuBook") as UEObject, page = book?.GetProp("curPage") as UEObject;
            UEObject root = con?.GetProp("Root") as UEObject, over = root?.GetProp("MouseWindow") as UEObject;
            string Val(UEObject combo) => ((combo?.GetProp("EditBox") as UEObject)?.GetProp("Value"))?.ToString();
            string list = "";
            for (UEObject w = root?.GetProp("FirstChildWindow") as UEObject; w is not null; w = w.GetProp("NextSiblingWindow") as UEObject)
                if (UEInterpreter.ClassIsA(w.Class, "UWindowComboList")) list = $" list={(w.GetProp("Selected") as UEObject)?.GetProp("Value")}";
            return $"page={page?.Class?.Name} over={over?.Class?.Name}:{over?.GetProp("Name")}"
                + (over is not null && UEInterpreter.ClassIsA(over.Class, "UWindowHSliderControl") ? $" value={over.GetProp("Value")}" : "")
                + $" brightness={(page?.GetProp("BrightnessSlider") as UEObject)?.GetProp("Value")} size={Val(page?.GetProp("ObjectDetailCombo") as UEObject)}{list}";
        }

        // The tick's input, in file order (window thread, where the loop hands InputStage the tick's polled events).
        public void Inject(HPEngine.Input.InputStage input, int tick)
        {
            foreach (var (t, verb, args) in _lines)
            {
                if (t != tick) continue;
                string[] a = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                switch (verb)
                {
                    case "key": if (!OsEvents) input.OnKey(Enum.Parse<SDL.SDL_Scancode>("SDL_SCANCODE_" + a[0], true), a[1] == "down"); break;
                    case "mouse": if (!OsEvents) input.OnMouseMotion(F(a[0]), F(a[1])); break;
                    case "text": input.OnTextInput(args); break;
                    case "pad":
                        _padOk = a[0] != "none";
                        if (_padOk) _pad = new UEInput.GamepadState { LeftX = S(a[0]), LeftY = S(a[1]), RightX = S(a[2]), RightY = S(a[3]),
                            LeftTrigger = S(a[4]), RightTrigger = S(a[5]), Buttons = uint.Parse(a[6], System.Globalization.CultureInfo.InvariantCulture) };
                        break;
                    case "console": case "probe": case "kill": break;
                    default: throw new InvalidDataException($"window script: unknown verb '{verb}' at tick {tick}");
                }
            }
        }

        public bool ReadPad(out UEInput.GamepadState s) { s = _pad; return _padOk; }

        static float F(string s) => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        static short S(string s) => short.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
    }

    // Per-tick state digest (--digest <file>), written on the thread that ran the tick right after UEGameEngine.Tick: every
    // live actor's gameplay projection (the fields of the harness digest, HeadlessSmoke.FormatSnapshot), the frame's
    // HUD and audio table (the camera is the view actor's projection), level time and the game/render random draw counts — hashed, one line per tick.
    static class StateDigest
    {
        static string F(float v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        public static void Write(TextWriter w, UEGameEngine host, int tick, float dt, bool menu)
        {
            UELevelRuntime rt = host.Runtime;
            var sb = new System.Text.StringBuilder();
            int n = 0;
            foreach (UEObject a in rt.Actors)
            {
                if (a is null || a.Destroyed) continue;
                n++;
                ActorSnapshot s = WorldFrameProjector.Project(a, outsideFrame: true);
                sb.Append(s.Name).Append(' ').Append(s.Class).Append(" loc=").Append(F(s.Location.x)).Append(',').Append(F(s.Location.y)).Append(',').Append(F(s.Location.z))
                  .Append(" rot=").Append(s.Rotation.Pitch).Append(',').Append(s.Rotation.Yaw).Append(',').Append(s.Rotation.Roll)
                  .Append(" vel=").Append(F(s.Velocity.x)).Append(',').Append(F(s.Velocity.y)).Append(',').Append(F(s.Velocity.z))
                  .Append(" accel=").Append(F(s.Acceleration.x)).Append(',').Append(F(s.Acceleration.y)).Append(',').Append(F(s.Acceleration.z))
                  .Append(" phys=").Append(s.Physics).Append(" zone=").Append(s.ZoneNumber).Append('/').Append(s.Leaf)
                  .Append(" base=").Append(s.Base).Append(" owner=").Append(s.Owner).Append(" state=").Append(s.State).Append(" latent=").Append(s.Latent)
                  .Append(" movetimer=").Append(F(s.MoveTimer)).Append(" anim=").Append(s.AnimSequence).Append(':').Append(F(s.AnimFrame)).Append(':').Append(F(s.AnimRate))
                  .Append(" timer=").Append(F(s.TimerRate)).Append('/').Append(F(s.TimerCounter)).Append(" lifespan=").Append(F(s.LifeSpan))
                  .Append(" touch=").Append(string.Join(',', s.Touching)).Append('\n');
            }
            WorldFrame f = rt.LatestFrame;
            if (f is not null)
            {
                foreach (HudCommand h in f.Hud)
                    sb.Append("hud ").Append(h.Kind).Append(' ').Append(F(h.X)).Append(',').Append(F(h.Y)).Append(',').Append(F(h.XL)).Append(',').Append(F(h.YL))
                      .Append(' ').Append(h.TexturePath).Append(' ').Append(h.Text).Append('\n');
                if (f.AudioChannels is { } ch)
                    foreach (var p in ch) sb.Append("chan ").Append(p is null ? "-" : $"{p.Serial} {p.Sound} {p.Playing}").Append('\n');
            }
            var nat = (UENativesCore)host.Natives;
            string map = Path.GetFileNameWithoutExtension(rt.LoadedMapPath ?? "");
            string time = F(rt.PresentedLevelTime);
            sb.Append("time ").Append(time).Append(" rand ").Append(nat.GameRandomDraws).Append(' ').Append(nat.RenderRandomDraws).Append(" map ").Append(map);
            byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
            w.WriteLine($"tick {tick} dt={F(dt)} map={map} frame={f?.Tick} time={time} actors={n} menu={menu} rand={nat.GameRandomDraws}/{nat.RenderRandomDraws} sha={Convert.ToHexString(hash, 0, 12)}");
        }
    }
}
