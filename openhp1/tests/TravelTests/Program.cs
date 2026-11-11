// Travel tests (the frame-hold state machine on a map change). Each expectation is written out
// BY HAND from the reference rule (HP1TravelFrameHold.cs of the removed reference port; ":NN" in the comments
// are its line numbers), not taken from port output. Frames are synthetic WorldFrames; a "world change" is the
// sequence: old world -> Arm -> frames with payload pending -> payload taken ->
// frames of the new world (without a camera, then with one).
// Exit code: 0 = all checks passed, 1 = failures (each named by a FAIL line).
using HP1.UEScript;
using OpenHP1.Hud;
using OpenHP1.Travel;

static class Program
{
    static int _fail, _pass;

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; return; }
        _fail++;
        Console.WriteLine($"FAIL {name}{(detail.Length > 0 ? ": " + detail : "")}");
    }

    static void Eq<T>(string name, T expected, T actual)
        => Check(name, EqualityComparer<T>.Default.Equals(expected, actual), $"expected {expected}, got {actual}");

    // ── synthetic data ──────────────────────────────────────────────────────
    static int _tick;
    // Core frame: camera = CameraSnapshot.Valid, drawWorld = WorldFrame.DrawWorld.
    static WorldFrame Frame(bool camera, bool drawWorld = true)
        => new WorldFrame(++_tick, "t", Array.Empty<ActorSnapshot>(),
            new CameraSnapshot(camera, 1, default, default, 90f, true),
            Array.Empty<AudioCommand>(), default, Array.Empty<HudCommand>(),
            Array.Empty<ParticleInstance>(), 0, drawWorld: drawWorld);

    static HudCommand Tile(float x, float y, float xl, float yl)
        => new HudCommand(HudKind.Tile, x, y, xl, yl, 0, 0, 64, 32, "HUD.Icons.Bolt", null, null,
            new UEVector(255, 255, 255), 1, false, false, null, 0);

    static readonly HudCommand[] ScreenA = { Tile(100, 50, 20, 10) };
    static readonly HudCommand[] ScreenB = { Tile(10, 10, 5, 5), Tile(20, 20, 5, 5) };

    static double _now;
    static TravelFrameHold New(WorldFrame presented, List<string> log = null)
    {
        var h = new TravelFrameHold(presented, () => _now);
        if (log != null) h.Log = log.Add;
        return h;
    }

    // One window frame protocol: LateUpdate (Update), then OnGUI (View), in Unity order.
    static string Step(TravelFrameHold h, WorldFrame presented, bool pending)
    {
        h.Update(presented, pending);
        return h.View switch
        {
            TravelView.World => "W",
            TravelView.GateBlack => "G",
            TravelView.LoadingScreen => "L",
            TravelView.HeldFrame => "H",
            _ => "?",
        };
    }

    static int Main()
    {
        GateInstall();
        GateDrawWorld();
        GateSceneChange();
        TravelWithScreen();
        TravelWithCapture();
        ReArm();
        ViewPriority();
        ScreenQuadsTest();
        Refusals();
        Console.WriteLine($"TravelTests: {_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    // Reference InstallGate :69-72 (gate closed from install), LateUpdate :156-164:
    // released when the frame is non-null, NOT the same object as at closing, and (camera || !DrawWorld).
    static void GateInstall()
    {
        WorldFrame f0 = Frame(camera: true);
        var h = New(f0);
        Eq("gate-install-closed", TravelView.GateBlack, h.View);
        Eq("gate-same-frame", "G", Step(h, f0, false));        // same object: not released although a camera exists
        Eq("gate-null-frame", "G", Step(h, null, false));
        Eq("gate-no-camera", "G", Step(h, Frame(camera: false), false));   // world drawn, no camera
        Eq("gate-released-count-0", 0, h.GateReleased);
        Eq("gate-new-camera", "W", Step(h, Frame(camera: true), false));
        Eq("gate-released-count-1", 1, h.GateReleased);
        Eq("gate-stays-open", "W", Step(h, Frame(camera: false), false));
        Eq("gate-released-count-still-1", 1, h.GateReleased);
    }

    // Reference :159 `!f.DrawWorld`: a menu frame without a camera releases the gate (the HUD draws the black field).
    static void GateDrawWorld()
    {
        var h = New(null);
        Eq("gate-null-install", TravelView.GateBlack, h.View);
        Eq("gate-menu-frame", "W", Step(h, Frame(camera: false, drawWorld: false), false));
        Eq("gate-menu-count", 1, h.GateReleased);
    }

    // Reference :73 activeSceneChanged -> CloseGate (:76-80) with the frame presented at that moment.
    static void GateSceneChange()
    {
        WorldFrame f0 = Frame(camera: true);
        var h = New(null);
        Step(h, f0, false);
        Eq("scene-open-before", TravelView.World, h.View);
        h.SceneChanged(f0);
        Eq("scene-closed", TravelView.GateBlack, h.View);
        Eq("scene-same-frame", "G", Step(h, f0, false));
        Eq("scene-new-frame", "W", Step(h, Frame(camera: true), false));
        Eq("scene-count", 2, h.GateReleased);
    }

    // Full world change with a loading screen (reference ArmScreen :86-97, LateUpdate :165-177).
    // Expected tape written out from the rules:
    //   frames 1-3: payload pending (:169), the screen holds;
    //   frame 4: payload taken, frame remembered and `return` (:170-172), still the screen although a camera exists;
    //   frame 5: the same frame object (:175), screen;
    //   frame 6: a new frame without a camera (:175), screen;
    //   frame 7: a new frame with a camera: release (:176), world.
    static void TravelWithScreen()
    {
        var log = new List<string>();
        WorldFrame old = Frame(camera: true);
        var h = New(null, log);
        Step(h, old, false);                         // gate opened by the old world
        _now = 10.0;
        h.ArmScreen(ScreenA);
        Eq("screen-armed", 1, h.Armed);
        Eq("screen-released-0", 0, h.Released);       // Release without a hold does not count (:182)
        Eq("screen-view-armed", TravelView.LoadingScreen, h.View);
        Check("screen-holding", h.Holding);
        Check("screen-no-capture", !h.CaptureRequested);
        Check("screen-ref", ReferenceEquals(h.Screen, ScreenA));
        WorldFrame atPayload = Frame(camera: true);
        var tape = new System.Text.StringBuilder();
        tape.Append(Step(h, old, true));
        tape.Append(Step(h, old, true));
        tape.Append(Step(h, Frame(camera: true), true));
        Check("screen-payload-pending", !h.PayloadTaken);
        tape.Append(Step(h, atPayload, false));
        Check("screen-payload-taken", h.PayloadTaken);
        tape.Append(Step(h, atPayload, false));
        tape.Append(Step(h, Frame(camera: false), false));
        Eq("screen-released-before", 0, h.Released);
        _now = 12.5;
        tape.Append(Step(h, Frame(camera: true), false));
        Eq("screen-tape", "LLLLLLW", tape.ToString());
        Eq("screen-released-1", 1, h.Released);
        Check("screen-not-holding", !h.Holding);
        Check("screen-cleared", h.Screen == null);
        Check("screen-log-arm", log.Count > 0 && log[0].Contains("commands 1"), string.Join(" | ", log));
        Check("screen-log-release", log.Count == 2 && log[1].Contains("2.50 s"), string.Join(" | ", log));
        tape.Clear();
        tape.Append(Step(h, Frame(camera: false), false));
        Eq("screen-after", "W", tape.ToString());
        Eq("screen-released-stays-1", 1, h.Released);
    }

    // Transition without a screen (reference :88 ArmScreen(null) -> Arm :107-121, capture :123-152):
    // nothing is held until the capture arrives (:165 early exit, payload NOT taken).
    static void TravelWithCapture()
    {
        WorldFrame old = Frame(camera: true);
        var h = New(null);
        Step(h, old, false);
        h.ArmScreen(null);
        Eq("capture-armed", 1, h.Armed);
        Check("capture-requested", h.CaptureRequested);
        Eq("capture-view-before", TravelView.World, h.View);
        // A frame where the capture has not arrived yet: payload no longer pending, but :165 exits.
        Eq("capture-early-return", "W", Step(h, Frame(camera: true), false));
        Check("capture-no-payload-before", !h.PayloadTaken);
        h.Captured(1280, 720);
        Check("capture-done", !h.CaptureRequested);
        Check("capture-holding", h.Holding);
        var tape = new System.Text.StringBuilder();
        WorldFrame atPayload = Frame(camera: true);
        tape.Append(Step(h, atPayload, true));
        tape.Append(Step(h, atPayload, false));      // payload taken: return
        tape.Append(Step(h, atPayload, false));      // the same frame
        tape.Append(Step(h, Frame(camera: true), false));
        Eq("capture-tape", "HHHW", tape.ToString());
        Eq("capture-released", 1, h.Released);
    }

    // Repeated Arm before release (reference :90, :115): the previous hold is released, payload reset.
    static void ReArm()
    {
        WorldFrame old = Frame(camera: true);
        var h = New(null);
        Step(h, old, false);
        h.ArmScreen(ScreenA);
        Step(h, Frame(camera: true), false);          // payload taken
        Check("rearm-taken", h.PayloadTaken);
        h.ArmScreen(ScreenB);
        Eq("rearm-released", 1, h.Released);
        Eq("rearm-armed", 2, h.Armed);
        Check("rearm-payload-reset", !h.PayloadTaken);
        Check("rearm-screen-b", ReferenceEquals(h.Screen, ScreenB));
        // A new frame with a camera: by rule :167-172 this only takes the payload, no release.
        Eq("rearm-no-early-release", "L", Step(h, Frame(camera: true), false));
        h.Arm();                                      // screen -> capture: screen released, payload reset
        Eq("rearm-arm-released", 2, h.Released);
        Eq("rearm-arm-armed", 3, h.Armed);
        Check("rearm-arm-payload-reset", !h.PayloadTaken);
        Check("rearm-arm-screen-cleared", h.Screen == null);
        Check("rearm-arm-capture", h.CaptureRequested);
    }

    // Reference OnGUI :192-207: gate only when nothing is held; the screen before the frame.
    static void ViewPriority()
    {
        WorldFrame f0 = Frame(camera: true);
        var h = New(f0);
        h.ArmScreen(ScreenA);
        Eq("prio-gate+screen", TravelView.LoadingScreen, h.View);
        var h2 = New(f0);
        h2.Arm(); h2.Captured(2, 2);
        Eq("prio-gate+held", TravelView.HeldFrame, h2.View);
        // the capture from Arm arrives after ArmScreen (the reference coroutine is not cancelled): both holds.
        var h3 = New(null);
        Step(h3, Frame(camera: true), false);
        h3.Arm(); h3.ArmScreen(ScreenA); h3.Captured(2, 2);
        Eq("prio-screen+held", TravelView.LoadingScreen, h3.View);
        h3.Update(Frame(camera: true), false);        // payload
        h3.Update(Frame(camera: true), false);        // release: both (:183-185)
        Eq("prio-both-released", TravelView.World, h3.View);
        Eq("prio-both-count", 1, h3.Released);
    }

    // Screen quads (reference :201-202): black over the full screen, then commands; scale max(1, H/480).
    static void ScreenQuadsTest()
    {
        var hb = new HudBatcher(p => p == "HUD.Icons.Bolt" ? (64, 32) : null, _ => null);
        var h = New(null);
        h.ArmScreen(ScreenA);
        var q = h.ScreenQuads(hb, 1920, 1080, 2);
        string seq = string.Join(" ", q.Select(x => x.Source.ToString()));
        Eq("quads-order", "Fill Tile", seq);
        if (q.Count == 2)
        {
            // canvas scale 2 (Standard at 1080): tile (100,50,20,10) -> [200,100..240,120].
            Check("quads-black", q[0].X0 == 0 && q[0].Y0 == 0 && q[0].X1 == 1920 && q[0].Y1 == 1080
                && q[0].R == 0 && q[0].G == 0 && q[0].B == 0, q[0].ToString());
            Check("quads-tile", q[1].X0 == 200 && q[1].Y0 == 100 && q[1].X1 == 240 && q[1].Y1 == 120, q[1].ToString());
        }
    }

    // Loud refusals: quads outside the loading screen; Captured without a request.
    static void Refusals()
    {
        var hb = new HudBatcher(_ => null, _ => null);
        var h = New(null);
        bool threw = false;
        try { h.ScreenQuads(hb, 640, 480, 1); } catch (InvalidOperationException) { threw = true; }
        Check("refuse-quads-without-screen", threw);
        threw = false;
        try { h.Captured(1, 1); } catch (InvalidOperationException) { threw = true; }
        Check("refuse-captured-without-arm", threw);
        // A null frame during a hold after payload: no release and no crash (:175 `now == null`).
        var h2 = New(null);
        h2.ArmScreen(ScreenA);
        h2.Update(Frame(camera: true), false);
        string r;
        try { r = Step(h2, null, false); } catch (Exception e) { r = "exception " + e.GetType().Name; }
        Eq("hold-null-frame", "L", r);
    }
}
