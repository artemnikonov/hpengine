// FramePacing (author decision): the Core tick runs at 60 Hz; frames between ticks are interpolated (draw rate capped at 120).
// Code 0 — all green; 1 — FAILs (each named by a line).
using HP1.UEScript;
using OpenHP1;

static class Program
{
    static int _pass, _fail;
    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) _pass++;
        else { _fail++; Console.WriteLine($"FAIL {name}{(detail.Length > 0 ? ": " + detail : "")}"); }
    }

    static int Main()
    {
        // ── schedule: 60 Hz logic on a 144 Hz monitor ──
        var p = new FramePacer(60, 144);
        Check("interpolation on when the monitor is faster than the logic", p.Interpolates);
        Check("steps", Math.Abs(p.LogicStep - 1 / 60.0) < 1e-12 && Math.Abs(p.RenderStep - 1 / 144.0) < 1e-12);
        Check("first tick immediately", p.LogicDue(0));
        p.LogicDone(0);
        Check("before the second tick α = 1 (no past)", p.Alpha(0.005) == 1f);
        Check("tick not before the step", !p.LogicDue(1 / 60.0 - 1e-6) && p.LogicDue(1 / 60.0));
        p.LogicDone(1 / 60.0);
        float a = p.Alpha(1 / 60.0 + 1 / 120.0);
        Check("α — fraction of the step after the tick", Math.Abs(a - 0.5f) < 1e-5f, $"{a}");
        Check("α clamped to [0,1]", p.Alpha(1.0) == 1f && p.Alpha(0.0) == 0f);
        p.RenderDone(1 / 60.0);
        Check("frame not before the monitor step", !p.RenderDue(1 / 60.0 + 1 / 150.0) && p.RenderDue(1 / 60.0 + 1 / 144.0));
        double w = p.SleepUntil(1 / 60.0);
        Check("sleep — until the nearest event (monitor frame)", Math.Abs(w - 1 / 144.0) < 1e-9, $"{w}");

        // monitor not faster than logic: one frame per tick, α = 1
        var q = new FramePacer(120, 60);
        Check("monitor 60 with logic 120: no interpolation", !q.Interpolates && q.RenderStep == q.LogicStep);
        q.LogicDone(0); q.LogicDone(1 / 120.0);
        Check("without interpolation α = 1", q.Alpha(1 / 120.0 + 0.004) == 1f);
        var z = new FramePacer(60, 0);
        Check("monitor rate unknown — no interpolation", !z.Interpolates);
        // A pacer rebuilt for a new draw rate (moved to a 144 Hz monitor after 10 minutes) continues the old clock: no
        // extra tick off the 60 Hz grid right after the rebuild.
        var old60 = new FramePacer(60, 60); old60.LogicDone(600.0); old60.RenderDone(600.0);
        var re144 = new FramePacer(60, 120, old60);
        Check("rebuilt pacer continues the clock (no tick before the step after a rate change)",
            !re144.LogicDue(600.0 + 1 / 120.0) && re144.LogicDue(600.0 + 1 / 60.0 + 1e-9));

        // ── draw rate (author decision 2026-10-15): interpolation always on, monitor rate capped at 120 ──
        Check("draw cap is 120", FramePacer.MaxDrawHz == 120);
        Check("draw: 144 Hz monitor capped at 120", FramePacer.DrawHz(144, true) == 120, $"{FramePacer.DrawHz(144, true)}");
        Check("draw: 100 Hz monitor as is", FramePacer.DrawHz(100, true) == 100, $"{FramePacer.DrawHz(100, true)}");
        var d = new FramePacer(60, FramePacer.DrawHz(240, true));
        Check("draw: 240 Hz monitor interpolates at 120", d.Interpolates && Math.Abs(d.RenderStep - 1 / 120.0) < 1e-12, $"{1 / d.RenderStep}");
        Check("draw: SmoothMotion=False — one frame per tick", FramePacer.DrawHz(144, false) == 0 && !new FramePacer(60, FramePacer.DrawHz(144, false)).Interpolates);

        // ── frames per second: a tick alone does not add a frame while interpolating (90 Hz Steam Deck drew 120) ──
        foreach (double hz in new[] { 90.0, 75.0, 120.0, 144.0 })
        {
            double draw = FramePacer.DrawHz(hz, true);
            var s = new FramePacer(60, draw);
            int frames = 0, ticks = 0;
            for (double t = 0; t < 10.0; t += 1e-4)
            {
                bool tk = false;
                if (s.LogicDue(t)) { s.LogicDone(t); tk = true; ticks++; }
                if (s.DrawNow(t, tk)) { s.RenderDone(t); frames++; }
            }
            Check($"frames per second at {hz} Hz = draw rate {draw}", Math.Abs(frames / 10.0 - draw) <= draw * 0.02, $"{frames / 10.0} fps, {ticks / 10.0} ticks/s");
        }
        var n = new FramePacer(60, 60); n.LogicDone(0); n.RenderDone(0);
        Check("not interpolating: a tick draws its frame", n.DrawNow(1 / 60.0, true));

        // ── angles and vectors ──
        Check("angle along the shortest arc through 0 (65000 -> 536: +1072, midpoint 65536)", Interp.LerpAngle(65000, 536, 0.5f) == 65536,
            $"{Interp.LerpAngle(65000, 536, 0.5f)}");
        Check("angle backwards", Interp.LerpAngle(1000, 0, 0.5f) == 500);
        Check("vector", Interp.Lerp(new UEVector(0, 10, -4), new UEVector(10, 20, 4), 0.25f).Equals(new UEVector(2.5f, 12.5f, -2f)));

        // ── camera: interpolation and cuts ──
        var c0 = new CameraSnapshot(true, 7, new UEVector(0, 0, 0), new UERotator(0, 0, 0), 90f, true);
        var c1 = new CameraSnapshot(true, 7, new UEVector(100, 0, 0), new UERotator(0, 4000, 0), 80f, true);
        var cm = Interp.Lerp(c0, c1, 0.5f);
        Check("camera: midpoint", cm.Location.x == 50f && cm.Rotation.Yaw == 2000 && cm.FovAngle == 85f);
        var noCam = new CameraSnapshot(false, 0, default, default, 0f, false);   // a browsing tick's loading screen
        Check("window camera: a frame without a camera keeps the last one", Interp.Follow(c0, noCam).FovAngle == 90f);
        Check("window camera: a frame with a camera is followed", Interp.Follow(c0, c1).Location.x == 100f);
        Check("camera: cut by distance", Interp.Lerp(c0, new CameraSnapshot(true, 7, new UEVector(257, 0, 0), default, 90f, true), 0.5f).Location.x == 257f);
        Check("camera: 256 uu — not a cut yet", Interp.Lerp(c0, new CameraSnapshot(true, 7, new UEVector(256, 0, 0), default, 90f, true), 0.5f).Location.x == 128f);
        Check("camera: cut by rotation (> 45°)", Interp.Lerp(c0, new CameraSnapshot(true, 7, default, new UERotator(0, 8193, 0), 90f, true), 0.5f).Rotation.Yaw == 8193);
        Check("camera: view target change — cut", Interp.Lerp(c0, new CameraSnapshot(true, 8, new UEVector(10, 0, 0), default, 90f, true), 0.5f).Location.x == 10f);

        // ── pose history: only adjacent ticks, cut by anchor ──
        var h = new PoseHistory();
        UEVector[] P(float x) => new[] { new UEVector(x, 0, 0), new UEVector(x, 1, 0) };
        h.Store(1, 10, P(0), new UEVector(0, 0, 0));
        Check("first tick — current pose", h.Get(1, 0.5f)[0].x == 0f);
        h.Store(1, 11, P(10), new UEVector(10, 0, 0));
        Check("adjacent ticks — lerp", h.Get(1, 0.5f)[0].x == 5f && h.Get(1, 1f)[0].x == 10f);
        h.Store(1, 11, P(99), default);
        Check("a repeated Store of the same tick does not shift the history", h.Get(1, 0.5f)[0].x == 5f);
        h.Store(1, 13, P(20), new UEVector(20, 0, 0));
        Check("skipped tick (actor not drawn) — no interpolation", h.Get(1, 0.5f)[0].x == 20f);
        h.Store(1, 14, P(1000), new UEVector(1000, 0, 0));
        Check("anchor teleport — no interpolation", h.Get(1, 0.5f)[0].x == 1000f);
        Check("Has by tick", h.Has(1, 14) && !h.Has(1, 13) && !h.Has(2, 14));

        // ── HProps.Star on pickup (measured with --star-probe, Lev_Tut1b, camera at the origin, looking +X):
        // at rest in the world -> at the camera (ZoomDist −6.67) -> 153.3 in front of the camera -> 146.7 -> 140. Jumps < CutDistance.
        var hs = new PoseHistory();
        float[] starX = { 120, 120, 120, -6.667f, 153.333f, 146.667f, 140f };
        var shown = new List<float>();
        for (int i = 0; i < starX.Length; i++)
        {
            var at = new UEVector(starX[i], 0, i < 3 ? -40 : 0);
            hs.Store(3, 100 + i, new[] { at }, at);
            shown.Add(hs.Get(3, 0.5f)[0].x);
        }
        Check("star: frame between rest and camera — tick, not a fly-through", shown[3] == starX[3], $"{shown[3]}");
        Check("star: frame between camera and 153 uu — tick, not the midpoint of 73 uu", shown[4] == starX[4], $"{shown[4]}");
        Check("star: step reversal (+160 -> −6.7) — tick", shown[5] == starX[5], $"{shown[5]}");
        Check("star: a smooth approach is interpolated again", Math.Abs(shown[6] - 143.333f) < 1e-3f, $"{shown[6]}");
        // smooth motion and a soft speed change (landing 15 uu/tick -> 0) are interpolated
        var hm = new PoseHistory();
        float[] walk = { 0, 20, 40, 60, 75, 75 };
        var ws2 = new List<float>();
        for (int i = 0; i < walk.Length; i++) { var at = new UEVector(walk[i], 0, 0); hm.Store(4, i, new[] { at }, at); ws2.Add(hm.Get(4, 0.5f)[0].x); }
        Check("smooth motion 20 uu/tick — lerp", ws2[2] == 30f && ws2[3] == 50f, $"{ws2[2]} {ws2[3]}");
        Check("stop 15 uu/tick -> 0 — lerp", ws2[5] == 75f && ws2[4] == 67.5f, $"{ws2[4]} {ws2[5]}");

        // ── particles: backwards along velocity, the frame keeps all fields ──
        var parts = new[] { new ParticleInstance(5, new UEVector(10, 0, 0), 1, 2, 0.5f, new UEVector(1, 1, 1), 0.3f, "T", new UEVector(60, 0, 0), 0.7f) };
        var sp = Interp.Particles(parts, 0.5f, 1 / 60f);
        Check("particle: pos − vel·(1−α)·dt", Math.Abs(sp[0].Pos.x - 9.5f) < 1e-5f && sp[0].DripTimer == 0.7f && sp[0].EmitterId == 5, $"{sp[0].Pos.x}");
        // spin back by the same time: SpinPos 0.3, SpinRate 6 rad/s, α 0.5 -> 0.3 − 6·(1/120) = 0.25; SpinRate 0 keeps SpinPos
        // and the View of the DrawParticleSystem call that kept it stays (2 = a child frame's call)
        var spun = Interp.Particles(new[] { new ParticleInstance(5, new UEVector(10, 0, 0), 1, 2, 0.5f, new UEVector(1, 1, 1), 0.3f, "T", new UEVector(60, 0, 0), 0.7f, 6f, 2) }, 0.5f, 1 / 60f);
        Check("particle: spinPos − spinRate·(1−α)·dt", Math.Abs(spun[0].SpinPos - 0.25f) < 1e-6f && spun[0].SpinRate == 6f && sp[0].SpinPos == 0.3f, $"{spun[0].SpinPos} {sp[0].SpinPos}");
        Check("particle: view of its draw call kept", spun[0].View == 2 && sp[0].View == -1, $"{spun[0].View} {sp[0].View}");
        var f0 = new WorldFrame(1, "a", Array.Empty<ActorSnapshot>(), c0, Array.Empty<AudioCommand>(), default, Array.Empty<HudCommand>(), parts, 3);
        var f1 = new WorldFrame(2, "b", Array.Empty<ActorSnapshot>(), c1, Array.Empty<AudioCommand>(), default, Array.Empty<HudCommand>(), parts, 3,
            default, false, null, true, default, false,
            ReverbState.FromZoneBytes(128, 4000, new byte[6], new byte[6]),
            new[] { new ViewFrame(-1, ViewFrame.KindMirror, 1, Affine.Identity, default, 0f, true, new[] { new ViewSource(7, 0, 0) }, default) },
            new[] { new MeshPose(9, false, "Pkg.Mesh", new[] { new UEVector(1, 2, 3) }) }, enhancedVisuals: false);
        var fm = Interp.Frame(f0, f1, 0.5f, 1 / 60f);
        Check("display frame: camera interpolated, the rest — the current tick",
            fm.Camera.Location.x == 50f && fm.Tick == 2 && fm.Label == "b" && fm.LocalPlayerId == 3 && !fm.HighDetailActors
            && fm.Paused && !fm.DrawWorld && ReferenceEquals(fm.Actors, f1.Actors) && fm.Particles[0].Pos.x != 10f
            && !fm.EnhancedVisuals && f0.EnhancedVisuals);
        // Zone reverb comes from the current tick (the display frame must not lose Reverb); child views
        // (WorldFrame.Views) differ from the previous tick's (there are none) — the current tick's.
        Check("display frame carries Reverb and Views", fm.Reverb.Valid && fm.Reverb.CutoffHz == 4000f && ReferenceEquals(fm.Views, f1.Views),
            $"Reverb.Valid={fm.Reverb.Valid}");
        // The same sky view in both ticks: M is interpolated between ticks together with the camera (sky shifts with the camera), NearClip too;
        // the same sky zone seen through other windows — still interpolated; another sky zone — the current tick.
        static ViewFrame Sky(float tx, float cd, int surf, int zone = 7) => new(-1, ViewFrame.KindSky, 1, new Affine { M00 = 1, M11 = 1, M22 = 1, M03 = tx },
            new UEVector(0, 0, 1), cd, false, new[] { new ViewSource(surf, 0, 0) },
            new BrushCoords { Origin = new UEVector(tx, 0, 0), X = new UEVector(0, 1, 0), Y = new UEVector(0, 0, -1), Z = new UEVector(1, 0, 0) }, zone);
        WorldFrame Fv(WorldFrame f, ViewFrame v) => new(f.Tick, f.Label, f.Actors, f.Camera, f.Audio, f.Music, f.Hud, f.Particles, f.LocalPlayerId,
            views: new[] { v });
        var vm = Interp.Frame(Fv(f0, Sky(10, 2, 4)), Fv(f1, Sky(20, 6, 4)), 0.5f, 1 / 60f);
        Check("sky frame: M and NearClip between ticks", vm.Views.Count == 1 && vm.Views[0].M.M03 == 15f && vm.Views[0].ClipD == 4f && vm.Views[0].Kind == ViewFrame.KindSky,
            $"M03={vm.Views[0].M.M03} ClipD={vm.Views[0].ClipD}");
        // The frame's own Coords (the eye of per-view setup) move with M.
        Check("sky frame: Coords between ticks", vm.Views[0].Coords.Origin.x == 15f && vm.Views[0].Coords.Z.x == 1f, $"origin={vm.Views[0].Coords.Origin}");
        var vw = Interp.Frame(Fv(f0, Sky(10, 2, 4)), Fv(f1, Sky(20, 6, 5)), 0.5f, 1 / 60f);
        Check("sky frame: other sky windows, same sky zone — between ticks", vw.Views[0].M.M03 == 15f, $"M03={vw.Views[0].M.M03}");
        var vc = Interp.Frame(Fv(f0, Sky(10, 2, 4, 7)), Fv(f1, Sky(20, 6, 4, 8)), 0.5f, 1 / 60f);
        Check("another sky zone — the current tick", vc.Views[0].M.M03 == 20f, $"M03={vc.Views[0].M.M03}");
        static ViewFrame Mirror(float tx, int surf) => new(-1, ViewFrame.KindMirror, 1, new Affine { M00 = 1, M11 = 1, M22 = 1, M03 = tx },
            new UEVector(0, 0, 1), 1, true, new[] { new ViewSource(surf, 0, 0) }, default, 7);
        var vx = Interp.Frame(Fv(f0, Mirror(10, 4)), Fv(f1, Mirror(20, 5)), 0.5f, 1 / 60f);
        Check("mirror: another source surface — the current tick", vx.Views[0].M.M03 == 20f, $"M03={vx.Views[0].M.M03}");
        // Drawn polygons of a child view: the union of both ticks only when the frame tree is the same (SameTree); a mirror on
        // another source surface, with as many views, is another tree — the child view draws the current tick's set alone.
        static DrawnWorld Dw(int node) => new(new[] { node }, Array.Empty<ViewSource>(), Array.Empty<int>());
        WorldFrame Fd(WorldFrame f, ViewFrame v, int child) => new(f.Tick, f.Label, f.Actors, f.Camera, f.Audio, f.Music, f.Hud, f.Particles,
            f.LocalPlayerId, views: new[] { v }, drawn: new[] { Dw(100 + child), Dw(child) });
        var dt1 = Interp.Frame(Fd(f0, Mirror(10, 4), 1), Fd(f1, Mirror(20, 4), 2), 0.5f, 1 / 60f);
        var dt2 = Interp.Frame(Fd(f0, Mirror(10, 4), 1), Fd(f1, Mirror(20, 5), 2), 0.5f, 1 / 60f);
        Check("child view drawn: same tree — union of both ticks", dt1.Drawn[1].Nodes.OrderBy(n => n).SequenceEqual(new[] { 1, 2 }),
            string.Join(",", dt1.Drawn[1].Nodes));
        Check("child view drawn: another mirror source — the current tick only", dt2.Drawn[1].Nodes.SequenceEqual(new[] { 2 })
            && dt2.Drawn[0].Nodes.Length == 2, string.Join(",", dt2.Drawn[1].Nodes));
        // Frame mesh vertices (WorldFrame.MeshPoses) come from the current tick: without them the frontend draws no mesh at all.
        Check("display frame carries MeshPoses", ReferenceEquals(fm.MeshPoses, f1.MeshPoses) && fm.MeshPoses.Count == 1, $"{fm.MeshPoses.Count}");
        Check("α = 1 — the same frame", ReferenceEquals(Interp.Frame(f0, f1, 1f, 1 / 60f), f1));
        // Every WorldFrame field the display frame does not interpolate is the current tick's (review 2026-11-09: CanvasScale
        // was dropped, the HUD drew at scale 1 between ticks). Fields are enumerated: a new field must be set here and carried.
        var fx = new WorldFrame(f1.Tick, f1.Label, new ActorSnapshot[0], f1.Camera, new AudioCommand[0], new MusicState(true, "Song", 1, 2, 3, true, 50),
            new HudCommand[0], f1.Particles, 4, new LevelAmbientState(1, 2, 3), f1.HighDetailActors, new DecalInstance[0],
            f1.Paused, new ViewFlashState(1, 2, 3, 4), f1.DrawWorld, f1.Reverb, f1.Views, f1.MeshPoses,
            new[] { 9 }, new UEGalaxyAudio.Playback[0], 5, new ActorViewSets(new Dictionary<int, int>(), new List<int[]>()),
            new CoronaInstance[0], f1.EnhancedVisuals, f1.Drawn, f1.DrawnTextures, new[] { 4 }, 0.25f, new[] { new[] { 1 } }, 3);
        var fxm = Interp.Frame(f0, fx, 0.5f, 1 / 60f);
        var bare = new WorldFrame(0, null, null, default, null, default, null, null, 0);
        var lerped = new HashSet<string> { "Camera", "Particles", "Views", "Drawn", "DrawnTextures" };
        var fields = typeof(WorldFrame).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(fi => !lerped.Contains(fi.Name)).ToList();
        // The fixture gives every compared field a value other than the constructor default and the previous tick's: a field
        // equal to either (or a new field nobody set here) would compare equal even if Interp.Frame dropped it or read prev.
        var unset = fields.Where(fi => Equals(fi.GetValue(fx), fi.GetValue(bare)) || Equals(fi.GetValue(fx), fi.GetValue(f0)))
            .Select(fi => fi.Name).ToList();
        Check("display frame fixture sets every field", unset.Count == 0, string.Join(",", unset));
        var lost = fields.Where(fi => !Equals(fi.GetValue(fxm), fi.GetValue(fx))).Select(fi => fi.Name).ToList();
        Check("display frame carries every non-interpolated field", !ReferenceEquals(fxm, fx) && lost.Count == 0, string.Join(",", lost));

        // Window title (D-77): fps/timings only with --perf-title; a plain launch keeps the plain title.
        Check("perf title off by default", !FramePacer.PerfTitleFromArgs(new string[0]) && !FramePacer.PerfTitleFromArgs(new[] { "--root", "x", "--map", "Lev_Tut1" }));
        Check("perf title flag", FramePacer.PerfTitleFromArgs(new[] { "--root", "x", "--perf-title" }));
        Check("no fps in the title without the flag", FramePacer.PerfTitle(false, 60, 60, 5, 5, "") is null);
        Check("fps in the title with the flag", FramePacer.PerfTitle(true, 60, 60, 5, 5, "")?.Contains(" fps") == true);

        Console.WriteLine($"PacingTests: {_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }
}
