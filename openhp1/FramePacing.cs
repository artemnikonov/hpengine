// LOGIC AT 60, RENDERING AT MONITOR RATE (author decision: option "A"; logic always at 60).
//
// Why: above 128 fps the original's fire freezes (particle float Age), above 200 the game outruns the clock
// (0.005 clamp in ULevel::Tick), and it is unknown where else logic depends on the rate. So the game (Core tick)
// runs 60 times per second, and frames between ticks are drawn by
// interpolation: the shown state is lerp(previous tick, current tick, α), α being the fraction of time until the
// next tick. Display latency is at most one logic tick. Presentation only: nothing is written to Core.
//
// Interpolated: camera and level clock (here), skeleton and wand poses (ActorBuilder), movers (SdlMovers),
// particles (shifted back along velocity). Sprites, decals and static meshes run at the logic rate.
using HP1.UEScript;

namespace OpenHP1
{
    public sealed class FramePacer
    {
        // Draw-rate cap (author decision 2026-10-15: interpolation always on, no menu item). Not above 120: the same
        // reasons as logic at 60 — the original's fire freezes above 128 fps (float Age), and the 0.005 s clamp in
        // ULevel::Tick outruns the clock above 200 — should anything ever tie to the draw rate.
        public const double MaxDrawHz = 120;

        // Draw rate for the pacer: the monitor rate capped at MaxDrawHz; 0 (no interpolation) when [OpenHP1] SmoothMotion=False.
        public static double DrawHz(double displayHz, bool smooth) => smooth ? Math.Min(displayHz, MaxDrawHz) : 0;

        // Window title with fps and Core/frame timings — only with the launch flag --perf-title (author decision
        // 2026-10-30: no developer tools for the player, D-77); otherwise null and the window keeps its plain title.
        // The plain title the window is created with (SdlRenderer) and keeps without --perf-title: Core's
        // UELocalization.ViewportCaption, as WinDrv.
        public static bool PerfTitleFromArgs(string[] args) => Array.IndexOf(args, "--perf-title") >= 0;
        public static string PerfTitle(bool enabled, double fps, double logicHz, double coreMs, double frameMs, string suffix)
            => enabled ? $"OpenHP1 — {fps:F0} fps (logic {logicHz:F0}), Core tick {coreMs:F1} ms, frame {frameMs:F1} ms{suffix}" : null;

        public readonly double LogicStep;    // 1 / 60 (or --fps)
        public readonly double RenderStep;   // 1 / monitor rate; not less than LogicStep
        double _last = double.NegativeInfinity, _prev = double.NegativeInfinity, _lastRender = double.NegativeInfinity;

        public FramePacer(double logicHz, double displayHz)
        {
            if (!(logicHz > 0)) throw new ArgumentOutOfRangeException(nameof(logicHz));
            LogicStep = 1.0 / logicHz;
            RenderStep = displayHz > logicHz ? 1.0 / displayHz : LogicStep;
        }

        // A pacer rebuilt for a new draw rate (window moved to another monitor, mode change) continues the previous one's
        // clock: otherwise the next tick would come at once, off the 60 Hz grid, and the first frame after it would not interpolate.
        public FramePacer(double logicHz, double displayHz, FramePacer previous) : this(logicHz, displayHz)
        {
            if (previous is null) return;
            _last = previous._last; _prev = previous._prev; _lastRender = previous._lastRender;
        }

        // Interpolation is needed only if the monitor is faster than logic.
        public bool Interpolates => RenderStep < LogicStep;

        public bool LogicDue(double now) => now - _last >= LogicStep;
        public void LogicDone(double now) { _prev = _last; _last = now; }

        public bool RenderDue(double now) => now - _lastRender >= RenderStep;
        public void RenderDone(double now) => _lastRender = now;

        // Draw a frame now? Interpolating: only on the monitor step — a tick alone does not force a frame, otherwise at
        // 90 Hz the ticks between monitor steps add frames (120 a second, 11.1/5.6 ms apart: uneven, Steam Deck 2026-11-08).
        // Not interpolating: one frame per tick.
        public bool DrawNow(double now, bool ticked) => Interpolates ? RenderDue(now) : ticked || RenderDue(now);

        // Display fraction between the previous and current tick: 0 = previous tick, 1 = current.
        public float Alpha(double now)
        {
            if (!Interpolates || double.IsNegativeInfinity(_prev)) return 1f;
            double span = _last - _prev;
            if (!(span > 0)) return 1f;
            double a = (now - _last) / span;
            return a <= 0 ? 0f : a >= 1 ? 1f : (float)a;
        }

        // How long to sleep until the next event (logic tick or frame).
        public double SleepUntil(double now)
            => Math.Min(_last + LogicStep, Interpolates ? _lastRender + RenderStep : double.PositiveInfinity) - now;
    }

    public static class Interp
    {
        // CUT: a move larger than CutDistance in one logic tick is a teleport or a camera cut; such a transition is
        // shown at once, without in-between "fly-through" frames. 256 uu per tick is 15 360 uu/s at 60 Hz, faster
        // than any camera or actor moves in the game; a rotation larger than CutAngle (45°) per tick is also a cut.
        public const float CutDistance = 256f;
        public const int CutAngle = 8192;
        // JERK: the anchor's step this tick differs from last tick's step by more than CutJerk: the actor was placed
        // by script (SetLocation), not moving. Example: the HProps.Star in state pickupstar: the pickup tick is at the
        // camera (ZoomDist = −400·dt: the Tick event runs before Begin code, Engine.dll.c:128700), the next is 160 uu in
        // front of the camera; both steps are below CutDistance, and an in-between frame would draw the star halfway
        // (73 uu), a jerk to the screen and back the original does not have (it draws ticks). A cut is always safe: the
        // frame shows the current tick, as without interpolation. 32 uu/tick per tick (≈1900 uu/s velocity jump) is a
        // presentation choice (§1), not an original value.
        public const float CutJerk = 32f;
        public static bool IsJerk(UEVector a0, UEVector a1, UEVector a2)
        {
            float dx = a2.x - 2 * a1.x + a0.x, dy = a2.y - 2 * a1.y + a0.y, dz = a2.z - 2 * a1.z + a0.z;
            return dx * dx + dy * dy + dz * dz > CutJerk * CutJerk;
        }

        public static float Lerp(float a, float b, float t) => a + (b - a) * t;
        public static UEVector Lerp(UEVector a, UEVector b, float t) => new(Lerp(a.x, b.x, t), Lerp(a.y, b.y, t), Lerp(a.z, b.z, t));

        // UE angle (65536 per turn) along the shortest arc.
        public static int AngleDelta(int a, int b) => (short)(ushort)((b - a) & 0xffff);
        public static int LerpAngle(int a, int b, float t) => a + (int)MathF.Round(AngleDelta(a, b) * t);
        public static UERotator Lerp(UERotator a, UERotator b, float t)
            => new(LerpAngle(a.Pitch, b.Pitch, t), LerpAngle(a.Yaw, b.Yaw, t), LerpAngle(a.Roll, b.Roll, t));

        public static bool IsCut(UEVector a, UEVector b)
        {
            float dx = b.x - a.x, dy = b.y - a.y, dz = b.z - a.z;
            return dx * dx + dy * dy + dz * dz > CutDistance * CutDistance;
        }
        static bool IsCut(UERotator a, UERotator b)
            => Math.Abs(AngleDelta(a.Pitch, b.Pitch)) > CutAngle || Math.Abs(AngleDelta(a.Yaw, b.Yaw)) > CutAngle
               || Math.Abs(AngleDelta(a.Roll, b.Roll)) > CutAngle;

        static bool CameraLerps(in CameraSnapshot a, in CameraSnapshot b, float t)
            => !(t >= 1f || !a.Valid || !b.Valid || a.ViewActorId != b.ViewActorId
                 || IsCut(a.Location, b.Location) || IsCut(a.Rotation, b.Rotation));

        // The camera the window draws a display frame from: a frame without one (the held loading screen of the browsing
        // tick after a travel or load, UGameEngine::Tick returns after Browse) keeps the last drawn camera; the travel gate
        // hides the world until the new world's first frame.
        public static CameraSnapshot Follow(in CameraSnapshot last, in CameraSnapshot shown) => shown.Valid ? shown : last;

        public static CameraSnapshot Lerp(in CameraSnapshot a, in CameraSnapshot b, float t)
        {
            if (!CameraLerps(a, b, t)) return b;
            return new CameraSnapshot(b.Valid, b.ViewActorId, Lerp(a.Location, b.Location, t), Lerp(a.Rotation, b.Rotation, t),
                Lerp(a.FovAngle, b.FovAngle, t), b.BehindView);
        }

        // Particle without identity: back along its own velocity by the missing tick fraction (dt = logic step), and its
        // spin back by the same time (UParticle::Update advances SpinPos += dt·SpinRate, as Pos by Vel), otherwise the
        // billboard turns at the logic rate while it moves at the draw rate. The View (the DrawParticleSystem call that kept
        // it) stays: without it every view of the emitter drew every call's particles (doubled in a mirror between ticks).
        public static IReadOnlyList<ParticleInstance> Particles(IReadOnlyList<ParticleInstance> cur, float t, float dt)
        {
            if (t >= 1f || cur is null || cur.Count == 0) return cur;
            float back = (1f - t) * dt;
            var r = new ParticleInstance[cur.Count];
            for (int i = 0; i < cur.Count; i++)
            {
                ParticleInstance p = cur[i];
                var pos = new UEVector(p.Pos.x - p.Vel.x * back, p.Pos.y - p.Vel.y * back, p.Pos.z - p.Vel.z * back);
                r[i] = new ParticleInstance(p.EmitterId, pos, p.Width, p.Length, p.Alpha, p.Color, p.SpinPos - p.SpinRate * back, p.TexturePath, p.Vel, p.DripTimer, p.SpinRate, p.View);
            }
            return r;
        }

        // Child display frames (WorldFrame.Views): M, NearClip and the frame Coords are interpolated with the camera, view by
        // view, when both ticks have as many views and the view is the same one (SameCamera); otherwise the current tick. For
        // mirror and portal M does not depend on the camera; for sky it is a shift by camera position (the sky frame sits in
        // SkyZone, rotated like the camera: UEOcclusion SkyCoords of the parent frame and the sky zone), and a linear M gives
        // the same camera as the interpolated frame camera. A sky view whose visible sky windows changed between the ticks
        // is still the same camera: taking the current tick's M there shifted the sky against the lerped world for one
        // frame (author 2026-11-10, Lev2_Quid1).
        public static IReadOnlyList<ViewFrame> Views(IReadOnlyList<ViewFrame> prev, IReadOnlyList<ViewFrame> cur, float t)
        {
            if (t >= 1f || cur.Count == 0 || prev is null || prev.Count != cur.Count) return cur;
            var r = new ViewFrame[cur.Count];
            for (int i = 0; i < r.Length; i++)
            {
                ViewFrame a = prev[i], b = cur[i];
                if (!SameCamera(a, b)) { r[i] = b; continue; }
                Affine m = new()
                {
                    M00 = Lerp(a.M.M00, b.M.M00, t), M01 = Lerp(a.M.M01, b.M.M01, t), M02 = Lerp(a.M.M02, b.M.M02, t), M03 = Lerp(a.M.M03, b.M.M03, t),
                    M10 = Lerp(a.M.M10, b.M.M10, t), M11 = Lerp(a.M.M11, b.M.M11, t), M12 = Lerp(a.M.M12, b.M.M12, t), M13 = Lerp(a.M.M13, b.M.M13, t),
                    M20 = Lerp(a.M.M20, b.M.M20, t), M21 = Lerp(a.M.M21, b.M.M21, t), M22 = Lerp(a.M.M22, b.M.M22, t), M23 = Lerp(a.M.M23, b.M.M23, t),
                };
                var c = new BrushCoords { Origin = Lerp(a.Coords.Origin, b.Coords.Origin, t), X = Lerp(a.Coords.X, b.Coords.X, t),
                    Y = Lerp(a.Coords.Y, b.Coords.Y, t), Z = Lerp(a.Coords.Z, b.Coords.Z, t) };
                r[i] = new ViewFrame(b.Parent, b.Kind, b.Recursion, m, Lerp(a.ClipN, b.ClipN, t), Lerp(a.ClipD, b.ClipD, t), b.Flip, b.Sources, c, b.Zone);
            }
            return r;
        }

        // The camera of a view is the same function of the parent camera in both ticks: sky — the same sky zone (its sources
        // are only the windows it shows through); mirror and portal — the same source surfaces (another plane otherwise).
        static bool SameCamera(in ViewFrame a, in ViewFrame b)
            => a.Kind == ViewFrame.KindSky
                ? b.Kind == ViewFrame.KindSky && a.Parent == b.Parent && a.Zone == b.Zone && a.Flip == b.Flip
                : SameView(a, b);

        static bool SameView(in ViewFrame a, in ViewFrame b)
        {
            if (a.Parent != b.Parent || a.Kind != b.Kind || a.Flip != b.Flip || a.Sources.Length != b.Sources.Length) return false;
            for (int i = 0; i < a.Sources.Length; i++)
                if (a.Sources[i].Surf != b.Sources[i].Surf || a.Sources[i].MoverId != b.Sources[i].MoverId || a.Sources[i].MoverPoly != b.Sources[i].MoverPoly) return false;
            return true;
        }

        // WORLD POLYGONS OF A DISPLAY FRAME (author decision 2026-10-16): Core's occlusion ran at the two tick cameras, not at
        // the lerped one, so a frame between them draws the UNION of both ticks' drawn sets (WorldFrame.Drawn, per view; a
        // child view only when both ticks have the same frame tree, otherwise the current tick's) and of their drawn
        // textures. A camera cut shows the current tick, and its sets alone.
        public static IReadOnlyList<DrawnWorld> Drawn(IReadOnlyList<DrawnWorld> prev, IReadOnlyList<DrawnWorld> cur, bool sameTree)
        {
            if (prev is null || cur is null) return cur;
            var r = new DrawnWorld[cur.Count];
            for (int i = 0; i < r.Length; i++)
                r[i] = (i == 0 || sameTree) && i < prev.Count ? Union(prev[i], cur[i]) : cur[i];
            return r;
        }

        // A node in both ticks keeps the current tick's side and draw-record flags (BackNodes / Reflagged of b, plus a's
        // for the polygons b did not draw).
        static DrawnWorld Union(DrawnWorld a, DrawnWorld b)
            => new(b.Nodes.Union(a.Nodes).ToArray(),
                b.Movers.Union(a.Movers).ToArray(),
                b.SkyWindows.Union(a.SkyWindows).ToArray(),
                b.BackNodes.Union(a.BackNodes.Except(b.Nodes)).ToArray(),
                b.Reflagged.Concat(a.Reflagged.Where(r => r.Node >= 0 ? !b.Nodes.Contains(r.Node) : !b.Movers.Contains(r.Mover))).ToArray());

        // Display frame: camera and particles interpolated, world polygons the union of both ticks, the rest is the current tick.
        public static WorldFrame Frame(WorldFrame prev, WorldFrame cur, float t, float dt)
        {
            if (t >= 1f || prev is null || cur is null) return cur;
            bool lerps = CameraLerps(prev.Camera, cur.Camera, t);
            IReadOnlyList<ViewFrame> views = lerps ? Views(prev.Views, cur.Views, t) : cur.Views;
            bool sameTree = lerps && SameTree(prev.Views, cur.Views);
            return new WorldFrame(cur.Tick, cur.Label, cur.Actors, Lerp(prev.Camera, cur.Camera, t), cur.Audio, cur.Music,
                cur.Hud, Particles(cur.Particles, t, dt), cur.LocalPlayerId, cur.LevelAmbient, cur.HighDetailActors,
                cur.Decals, cur.Paused, cur.ViewFlash, cur.DrawWorld, cur.Reverb,
                views, cur.MeshPoses, cur.SpriteActors,
                cur.AudioChannels, cur.AudioResumeSerial, cur.ActorViews, cur.Coronas, cur.EnhancedVisuals,
                lerps ? Drawn(prev.Drawn, cur.Drawn, sameTree) : cur.Drawn,
                lerps ? cur.DrawnTextures.Union(prev.DrawnTextures, StringComparer.OrdinalIgnoreCase).ToArray() : cur.DrawnTextures,
                cur.AudioCallStops, cur.AudioMixDt, cur.DrawOrder, cur.CanvasScale);
        }

        static bool SameTree(IReadOnlyList<ViewFrame> prev, IReadOnlyList<ViewFrame> cur)
        {
            if (prev is null || cur is null || prev.Count != cur.Count) return false;
            for (int i = 0; i < cur.Count; i++) if (!SameView(prev[i], cur[i])) return false;
            return true;
        }
    }

    // Pose points by tick: on a new tick the current ones become "previous". Previous points are valid only from the tick
    // right before the current one (an actor that was not in the frame last tick is drawn without interpolation).
    public sealed class PoseHistory
    {
        sealed class Entry { public int Tick = int.MinValue; public UEVector[] Cur, Prev, Buf; public UEVector CurAt, PrevAt, PrevPrevAt; public int PrevTick = int.MinValue, PrevPrevTick = int.MinValue; }
        readonly Dictionary<int, Entry> _e = new();

        public bool Has(int key, int tick) => _e.TryGetValue(key, out var e) && e.Tick == tick;

        public void Store(int key, int tick, UEVector[] pts, UEVector anchor)
        {
            if (!_e.TryGetValue(key, out var e)) _e[key] = e = new Entry();
            if (e.Tick == tick) return;
            e.PrevPrevAt = e.PrevAt; e.PrevPrevTick = e.PrevTick;
            e.Prev = e.Cur; e.PrevAt = e.CurAt; e.PrevTick = e.Tick;
            e.Cur = (UEVector[])pts.Clone(); e.CurAt = anchor; e.Tick = tick;
        }

        // true if the display frame at t interpolates this key (consecutive ticks, no cut, no jerk of its anchor).
        public bool Lerps(int key, float t)
        {
            Entry e = _e[key];
            return !(t >= 1f || e.Prev is null || e.PrevTick != e.Tick - 1 || e.Prev.Length != e.Cur.Length || Interp.IsCut(e.PrevAt, e.CurAt)
                     || (e.PrevPrevTick == e.Tick - 2 && Interp.IsJerk(e.PrevPrevAt, e.PrevAt, e.CurAt)));
        }

        // lerpsPoint (optional): point i is interpolated only if true, otherwise it is the current tick's (a cut decided by
        // another owner of the point, e.g. its mover).
        public UEVector[] Get(int key, float t, Func<int, bool> lerpsPoint = null)
        {
            Entry e = _e[key];
            if (!Lerps(key, t)) return e.Cur;
            if (e.Buf is null || e.Buf.Length != e.Cur.Length) e.Buf = new UEVector[e.Cur.Length];
            for (int i = 0; i < e.Cur.Length; i++) e.Buf[i] = lerpsPoint is null || lerpsPoint(i) ? Interp.Lerp(e.Prev[i], e.Cur[i], t) : e.Cur[i];
            return e.Buf;
        }

        public void Forget(int key) => _e.Remove(key);
        public void Clear() => _e.Clear();
    }
}
