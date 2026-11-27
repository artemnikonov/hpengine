// LOGIC AT 60, RENDERING AT MONITOR RATE (NOT ORIGINAL; logic always at 60).
//
// Why: above 128 fps the original's fire freezes (particle float Age), above 200 the game outruns the clock
// (0.005 clamp in ULevel::Tick), and it is unknown where else logic depends on the rate. So the game (Core tick)
// runs 60 times per second, and frames between ticks are drawn by
// interpolation: the shown state is lerp(previous tick, current tick, α), α being the fraction of time until the
// next tick. Display latency is at most one logic tick. Presentation only: nothing is written to Core.
//
// Interpolated: camera and level clock (here), skeleton and wand poses (ActorBuilder), movers (SdlMovers),
// particles (by Core identity; newborns shifted back along velocity), decals (shifted with their own record,
// Interp.Decals), sprite tiles (actor Location, SpriteBuilder.Alpha), canvas items (Interp.Hud). Audit of every category:
// hpengine/tests/InterpAuditTests.
using HP1.UEScript;

namespace HPEngine
{
    public sealed class FramePacer
    {
        // Draw-rate cap (interpolation is always on, no menu item). Not above 120: the same
        // reasons as logic at 60 — the original's fire freezes above 128 fps (float Age), and the 0.005 s clamp in
        // ULevel::Tick outruns the clock above 200 — should anything ever tie to the draw rate.
        public const double MaxDrawHz = 120;

        // Draw rate for the pacer: the monitor rate capped at MaxDrawHz; 0 (no interpolation) when [HPEngine] SmoothMotion=False.
        public static double DrawHz(double displayHz, bool smooth) => smooth ? Math.Min(displayHz, MaxDrawHz) : 0;

        // Window title with fps and Core/frame timings — only with the launch flag --perf-title (no developer tools for the player, D-77); otherwise null and the window keeps its plain title.
        // The plain title the window is created with (SdlRenderer) and keeps without --perf-title: Core's
        // UELocalization.ViewportCaption, as WinDrv.
        public static bool PerfTitleFromArgs(string[] args) => Array.IndexOf(args, "--perf-title") >= 0;
        public static string PerfTitle(bool enabled, double fps, double logicHz, double coreMs, double frameMs, string suffix)
            => enabled ? $"HPEngine — {fps:F0} fps (logic {logicHz:F0}), Core tick {coreMs:F1} ms, frame {frameMs:F1} ms{suffix}" : null;

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
        // 90 Hz the ticks between monitor steps add frames (120 a second, 11.1/5.6 ms apart: uneven, seen on a Steam Deck).
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

        // How long to sleep until the next event (logic tick or frame). logic false: no tick can start now (the Core thread is
        // still running one), only a frame is awaited — also when not interpolating, where DrawNow repeats the frame on
        // the render step. frame false: no frame can be drawn now (Core's synchronous presentation holds the window).
        // Neither: +infinity (the caller waits for the Core thread's signal).
        public double SleepUntil(double now, bool logic = true, bool frame = true)
            => Math.Min(logic ? _last + LogicStep : double.PositiveInfinity,
                        frame && (Interpolates || !logic) ? _lastRender + RenderStep : double.PositiveInfinity) - now;
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
        // presentation choice, not an original value.
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
                 || IsCut(a.Location, b.Location) || IsCut(a.Rotation, b.Rotation)
                 || (a.FovAngle < 180f) != (b.FovAngle < 180f));   // Proj.Z changes sign: a lerp would pass the collapsed 180

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

        // Particle between ticks. Drawn in both ticks (same Core particle: ParticleInstance.Serial, emitter and view): its
        // position is lerped between the two ticks — a particle the emitter carries (HP2 duel spells: Vel 0, moved with the
        // flying emitter) otherwise stepped at the logic rate (InterpAuditTests, Duel01). A move above CutDistance is a cut
        // (the tick's). Born this tick (no previous position): back along its own velocity by the missing tick fraction
        // (dt = logic step). The spin goes back by the same time in both cases (UParticle::Update advances SpinPos +=
        // dt·SpinRate), otherwise the billboard turns at the logic rate while it moves at the draw rate. The View (the
        // DrawParticleSystem call that kept it) stays: without it every view of the emitter drew every call's particles
        // (doubled in a mirror between ticks).
        public static IReadOnlyList<ParticleInstance> Particles(IReadOnlyList<ParticleInstance> prev, IReadOnlyList<ParticleInstance> cur, float t, float dt)
        {
            if (t >= 1f || cur is null || cur.Count == 0) return cur;
            Dictionary<(int, int, int), UEVector> before = PrevParticles(prev);
            float back = (1f - t) * dt;
            var r = new ParticleInstance[cur.Count];
            for (int i = 0; i < cur.Count; i++)
            {
                ParticleInstance p = cur[i];
                UEVector pos = p.Serial != 0 && before.TryGetValue((p.EmitterId, p.Serial, p.View), out UEVector a) && !IsCut(a, p.Pos)
                    ? Lerp(a, p.Pos, t)
                    : new UEVector(p.Pos.x - p.Vel.x * back, p.Pos.y - p.Vel.y * back, p.Pos.z - p.Vel.z * back);
                r[i] = new ParticleInstance(p.EmitterId, pos, p.Width, p.Length, p.Alpha, p.Color, p.SpinPos - p.SpinRate * back, p.TexturePath, p.Vel, p.DripTimer, p.SpinRate, p.View, p.Serial);
            }
            return r;
        }

        // The previous tick's particle positions by identity, built once per previous list (display frames of one tick pair
        // share it). Window thread only.
        static IReadOnlyList<ParticleInstance> _prevList;
        static readonly Dictionary<(int, int, int), UEVector> _prevPos = new();
        static Dictionary<(int, int, int), UEVector> PrevParticles(IReadOnlyList<ParticleInstance> prev)
        {
            if (ReferenceEquals(prev, _prevList)) return _prevPos;
            _prevList = prev; _prevPos.Clear();
            if (prev is not null) foreach (ParticleInstance p in prev) if (p.Serial != 0) _prevPos.TryAdd((p.EmitterId, p.Serial, p.View), p.Pos);
            return _prevPos;
        }

        // Decal between ticks (author report 2026-11-17: Harry's shadow jerked while he was interpolated). The original draws
        // one frame per tick and re-attaches the shadow every frame its owner moved more than MoveThreshold (1 uu,
        // ActorShadow.Update from SetupDynamics), so the shadow follows its owner. A record on the same surface in both ticks
        // is interpolated piece by piece: a piece with the same node, view and point count is lerped point by point with
        // its U/V (a piece is the record's rectangle clipped by fixed polygon edges, so its points move linearly while the
        // rectangle moves); another piece of that record (its clip changed) is moved by the record's step d =
        // lerp(prev, cur, t) - cur of the record centre (Base + (V0 + V2) / 2), projected on the record's plane. A record
        // with no counterpart on its surface, or a cut, stays the current tick's. α = 1 is the tick's frame untouched.
        // ponytail: a record that appears or vanishes between the ticks (the decal crossing onto another surface, a
        // MultiDecalLevel neighbour) is drawn as the tick's — its in-between part (at most one tick's step wide) is only
        // known to a re-clip; Core would have to clip per display frame to draw it.
        public static IReadOnlyList<DecalInstance> Decals(IReadOnlyList<DecalInstance> prev, IReadOnlyList<DecalInstance> cur, float t)
        {
            if (t >= 1f || prev is null || cur is null || cur.Count == 0) return cur;
            var match = new DecalInstance?[cur.Count];
            bool any = false;
            for (int i = 0; i < cur.Count; i++)
                foreach (DecalInstance p in prev)
                {
                    if (p.DecalId != cur[i].DecalId || p.Surf != cur[i].Surf) continue;
                    UEVector a = Centre(p), b = Centre(cur[i]);
                    if (IsCut(a, b)) break;
                    match[i] = p; any = true;
                    break;
                }
            if (!any) return cur;
            var r = new DecalInstance[cur.Count];
            for (int i = 0; i < cur.Count; i++)
            {
                DecalInstance c = cur[i];
                if (match[i] is not { } p || c.Pieces is null) { r[i] = c; continue; }
                UEVector a = Centre(p), b = Centre(c), n = c.Normal;
                var d = new UEVector((a.x - b.x) * (1f - t), (a.y - b.y) * (1f - t), (a.z - b.z) * (1f - t));
                float nn = n.x * n.x + n.y * n.y + n.z * n.z, k = nn > 0f ? (d.x * n.x + d.y * n.y + d.z * n.z) / nn : 0f;
                d = new UEVector(d.x - n.x * k, d.y - n.y * k, d.z - n.z * k);
                DecalPiece[] old = p.Pieces;
                var pieces = new DecalPiece[c.Pieces.Length];
                for (int j = 0; j < pieces.Length; j++)
                {
                    DecalPiece pc = c.Pieces[j];
                    if (pc.Points is null) { pieces[j] = pc; continue; }
                    int m = pc.Points.Length;
                    var pts = new UEVector[m];
                    if (old is not null && j < old.Length && old[j].Node == pc.Node && old[j].View == pc.View && old[j].Points?.Length == m)
                    {
                        DecalPiece o = old[j];
                        float[] u = new float[m], v = new float[m];
                        for (int q = 0; q < m; q++)
                        { pts[q] = Lerp(o.Points[q], pc.Points[q], t); u[q] = Lerp(o.U[q], pc.U[q], t); v[q] = Lerp(o.V[q], pc.V[q], t); }
                        pieces[j] = new DecalPiece(pc.Node, pts, u, v, pc.View);
                        continue;
                    }
                    for (int q = 0; q < m; q++) pts[q] = new UEVector(pc.Points[q].x + d.x, pc.Points[q].y + d.y, pc.Points[q].z + d.z);
                    pieces[j] = new DecalPiece(pc.Node, pts, pc.U, pc.V, pc.View);
                }
                r[i] = new DecalInstance(c.DecalId, c.OwnerId, c.Surf, c.Normal, c.Base, c.V0, c.V1, c.V2, c.V3, c.Nodes, pieces,
                    c.SurfPolyFlags, c.SurfTexturePolyFlags);
            }
            return r;
        }
        static UEVector Centre(in DecalInstance d)
            => new(d.Base.x + (d.V0.x + d.V2.x) * 0.5f, d.Base.y + (d.V0.y + d.V2.y) * 0.5f, d.Base.z + (d.V0.z + d.V2.z) * 0.5f);

        // Canvas items between ticks: the HUD is script output per tick (PostRender), so an animated item (the cut-scene
        // bars, HPBase/HGame.Icons.leftPanel tiles growing from the screen edges; a sliding line of text; InterpAuditTests)
        // moved at 60 Hz over a world drawn at the display rate. An item at the same index in both ticks, the same in
        // everything but its place (kind, texture window, text, font, colour, style, glyph windows and their offsets; a
        // tile may also change its size), is drawn at the lerped place and size, a text's glyphs moved with it; anything
        // else (another texture window: a digit, a bar fill), a list of another length, a canvas actor (HudKind.Actor) or a
        // move above HudCut canvas units is the tick's. α = 1 is the tick's list untouched.
        public const float HudCut = 128f;
        public static IReadOnlyList<HudCommand> Hud(IReadOnlyList<HudCommand> prev, IReadOnlyList<HudCommand> cur, float t)
        {
            if (t >= 1f || prev is null || cur is null || prev.Count != cur.Count) return cur;
            HudCommand[] r = null;
            for (int i = 0; i < cur.Count; i++)
            {
                HudCommand a = prev[i], b = cur[i];
                float dx = a.X - b.X, dy = a.Y - b.Y, dw = a.XL - b.XL, dh = a.YL - b.YL;
                if (dx == 0f && dy == 0f && dw == 0f && dh == 0f || dx * dx + dy * dy > HudCut * HudCut
                    || dw * dw + dh * dh > HudCut * HudCut || !SameItem(a, b)) continue;
                r ??= cur.ToArray();
                float ox = dx * (1f - t), oy = dy * (1f - t);
                HudGlyph[] g = b.Glyphs;
                if (g is not null)
                {
                    g = new HudGlyph[b.Glyphs.Length];
                    for (int k = 0; k < g.Length; k++)
                    {
                        HudGlyph q = b.Glyphs[k];
                        g[k] = new HudGlyph(q.Page, q.X + ox, q.Y + oy, q.XL, q.YL, q.U, q.V, q.UL, q.VL);
                    }
                }
                r[i] = new HudCommand(b.Kind, b.X + ox, b.Y + oy, b.XL + dw * (1f - t), b.YL + dh * (1f - t), b.U, b.V, b.UL, b.VL, b.TexturePath, b.Text, b.FontPath,
                    b.Color, b.Style, b.Smooth, b.Clipped, g, b.PolyFlags, b.ActorId);
            }
            return r ?? cur;
        }

        // The same texture window: equal up to float noise of the script's arithmetic (1 vs 0.99999994).
        static bool Near(float a, float b) => MathF.Abs(a - b) <= 1e-4f * MathF.Max(1f, MathF.Abs(b));
        static bool SameItem(in HudCommand a, in HudCommand b)
        {
            if (b.Kind == HudKind.Actor || a.Kind != b.Kind || a.TexturePath != b.TexturePath || a.Text != b.Text || a.FontPath != b.FontPath
                || (b.Kind != HudKind.Tile && (a.XL != b.XL || a.YL != b.YL))
                || !Near(a.U, b.U) || !Near(a.V, b.V) || !Near(a.UL, b.UL) || !Near(a.VL, b.VL) || a.Color != b.Color
                || a.Style != b.Style || a.Smooth != b.Smooth || a.Clipped != b.Clipped || a.PolyFlags != b.PolyFlags || a.ActorId != b.ActorId)
                return false;
            if (a.Glyphs is null || b.Glyphs is null) return a.Glyphs is null && b.Glyphs is null;
            if (a.Glyphs.Length != b.Glyphs.Length) return false;
            for (int k = 0; k < a.Glyphs.Length; k++)
            {
                HudGlyph p = a.Glyphs[k], q = b.Glyphs[k];
                if (p.Page != q.Page || p.U != q.U || p.V != q.V || p.UL != q.UL || p.VL != q.VL || p.XL != q.XL || p.YL != q.YL
                    || p.X - a.X != q.X - b.X || p.Y - a.Y != q.Y - b.Y) return false;
            }
            return true;
        }

        // Child display frames (WorldFrame.Views): M, NearClip and the frame Coords are interpolated with the camera, view by
        // view, when both ticks have as many views and the view is the same one (SameCamera); otherwise the current tick. For
        // mirror and portal M does not depend on the camera; for sky it is a shift by camera position (the sky frame sits in
        // SkyZone, rotated like the camera: UEOcclusion SkyCoords of the parent frame and the sky zone), and a linear M gives
        // the same camera as the interpolated frame camera. A sky view whose visible sky windows changed between the ticks
        // is still the same camera: taking the current tick's M there shifted the sky against the lerped world for one
        // frame (seen in Lev2_Quid1).
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

        // WORLD POLYGONS OF A DISPLAY FRAME: Core's occlusion ran at the two tick cameras, not at
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

        // A node in both ticks keeps the current tick's side, draw-record flags and Lumos arguments (BackNodes / Reflagged /
        // Lumos of b, plus a's for the polygons b did not draw).
        static DrawnWorld Union(DrawnWorld a, DrawnWorld b)
            => new(b.Nodes.Union(a.Nodes).ToArray(),
                b.Movers.Union(a.Movers).ToArray(),
                b.SkyWindows.Union(a.SkyWindows).ToArray(),
                b.BackNodes.Union(a.BackNodes.Except(b.Nodes)).ToArray(),
                b.Reflagged.Concat(a.Reflagged.Where(r => r.Node >= 0 ? !b.Nodes.Contains(r.Node) : !b.Movers.Contains(r.Mover))).ToArray(),
                b.Lumos.Concat(a.Lumos.Where(r => r.Node >= 0 ? !b.Nodes.Contains(r.Node) : !b.Movers.Contains(r.Mover))).ToArray());

        // Display frame: camera, canvas items, particles and decals interpolated, world polygons the union of both ticks, the
        // rest is the current tick.
        public static WorldFrame Frame(WorldFrame prev, WorldFrame cur, float t, float dt)
        {
            if (t >= 1f || prev is null || cur is null) return cur;
            bool lerps = CameraLerps(prev.Camera, cur.Camera, t);
            IReadOnlyList<ViewFrame> views = lerps ? Views(prev.Views, cur.Views, t) : cur.Views;
            bool sameTree = lerps && SameTree(prev.Views, cur.Views);
            return new WorldFrame(cur.Tick, cur.Label, cur.Actors, Lerp(prev.Camera, cur.Camera, t), cur.Audio, cur.Music,
                Hud(prev.Hud, cur.Hud, t), Particles(prev.Particles, cur.Particles, t, dt), cur.LocalPlayerId, cur.LevelAmbient, cur.HighDetailActors,
                Decals(prev.Decals, cur.Decals, t), cur.Paused, cur.ViewFlash, cur.DrawWorld, cur.Reverb,
                views, cur.MeshPoses, cur.SpriteActors,
                cur.AudioChannels, cur.AudioResumeSerial, cur.ActorViews, cur.Coronas, cur.EnhancedVisuals,
                lerps ? Drawn(prev.Drawn, cur.Drawn, sameTree) : cur.Drawn,
                lerps ? cur.DrawnTextures.Union(prev.DrawnTextures, StringComparer.OrdinalIgnoreCase).ToArray() : cur.DrawnTextures,
                cur.AudioCallStops, cur.AudioMixDt, cur.DrawOrder, cur.CanvasScale, cur.TextureFrames, cur.ScriptedTexels,
                cur.PointerTracing, cur.PlayerControl);
        }

        // The tick's frame with its own copy of the actor list: Core rotates three snapshot buffers (WorldFrameProjector.
        // ProjectScratch), and a tick that builds several frames (LoadMap fade, post-load, save progress) would overwrite the
        // list the window thread is drawing from while the Core thread runs. The other lists are built new per frame.
        // `into`: the caller's buffer, reused (the window alternates two: the frame being replaced may still be drawn).
        public static WorldFrame Detach(WorldFrame f, List<ActorSnapshot> into)
        {
            if (f is null) return null;
            into.Clear();
            into.AddRange(f.Actors);
            return new WorldFrame(f.Tick, f.Label, into, f.Camera, f.Audio, f.Music,
                f.Hud, f.Particles, f.LocalPlayerId, f.LevelAmbient, f.HighDetailActors,
                f.Decals, f.Paused, f.ViewFlash, f.DrawWorld, f.Reverb,
                f.Views, f.MeshPoses, f.SpriteActors,
                f.AudioChannels, f.AudioResumeSerial, f.ActorViews, f.Coronas, f.EnhancedVisuals,
                f.Drawn, f.DrawnTextures, f.AudioCallStops, f.AudioMixDt, f.DrawOrder, f.CanvasScale, f.TextureFrames, f.ScriptedTexels,
                f.PointerTracing, f.PlayerControl);
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
