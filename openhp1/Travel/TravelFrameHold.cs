// Frame hold across a map change — port of the former Unity frontend HP1TravelFrameHold.cs
// ("reference :line" below points into it) to plain C#.
//
// What is held (reference :1-31): shipped UGameEngine::LoadMap, after loading the new map's LevelInfo0,
// draws Console.DrawLevelInfo (the frame is built by Core: UELevelRuntime.BuildLevelInfoScreen /
// LastLoadMapScreen), and that frame stays on screen while the map loads. Here ONLY the hold: the loading
// screen (HUD commands on black) or, without a screen (no console), the last presented frame. It is released
// by the first applied frame of the new world with a valid camera — an observable condition, not "after
// N frames"; no splash, no delay (reference :29-31).
//
// Separately, the "gate" (reference :53-80): after the window opens and after every world change the 3D world
// is covered with black until the first NEW Core frame that has a camera or does not draw the world.
//
// The module makes no GPU calls and measures no time: per frame it returns a TravelView decision and
// (for the loading screen) HudBatcher quads. Unity specifics — MonoBehaviour/singleton, the
// WaitForEndOfFrame coroutine, Texture2D, the IMGUI gamma table — are not ported (README).
using System.Collections.Generic;
using HP1.UEScript;
using OpenHP1.Hud;

namespace OpenHP1.Travel
{
    // What to draw this frame (reference OnGUI :190-208, same check order).
    public enum TravelView
    {
        // Nothing held and the gate is open: a normal frame.
        World,
        // Gate closed (reference :192-196): black INSTEAD of the 3D world, as the lowest layer
        // (GUI.depth = int.MaxValue). The whole 2D frame (coronas, HUD, flash) draws as usual.
        GateBlack,
        // Loading screen (reference :197-203): full-screen black + DrawLevelInfo commands,
        // above everything (GUI.depth = int.MinValue); nothing else in the frame.
        LoadingScreen,
        // Held last frame (reference :205-207): above everything; it is a FRAME, not a UI element.
        HeldFrame,
    }

    public sealed class TravelFrameHold
    {
        // Counters for acceptance and the log (reference :42, :66).
        public int Armed, Released, GateReleased;

        // Log (reference Debug.Log). null — silent.
        public System.Action<string> Log;

        // Real time in seconds — only for the hold-duration log line
        // (reference Time.realtimeSinceStartup :93, :118, :177). Never affects decisions.
        readonly System.Func<double> _clock;

        bool _held;                              // reference _held (Texture2D); with SDL_GPU the backend holds the copy
        bool _captureRequested;                  // reference StartCoroutine(CaptureAtEndOfFrame) :119
        IReadOnlyList<HudCommand> _screen;       // reference _screen :45
        bool _payloadTaken;                      // reference :46
        WorldFrame _frameAtPayload;              // reference :47
        double _armedAt;                         // reference :48
        bool _gate;                              // reference :64
        WorldFrame _gateFrame;                   // reference :65

        // Reference InstallGate (:68-74): the gate closes on install (before the first scene).
        // presented — the frame presented at that moment (reference PresentedFrame, may be null).
        public TravelFrameHold(WorldFrame presented, System.Func<double> clock = null)
        {
            _clock = clock ?? (() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency);
            CloseGate(presented);
        }

        // Reference :41 — a hold is active (loading screen or captured frame).
        public bool Holding => _held || _screen != null;
        public bool GateClosed => _gate;
        public bool PayloadTaken => _payloadTaken;
        // The backend must copy the frame presented AFTER this Arm() call and
        // report Captured() (reference CaptureAtEndOfFrame :123-152, WaitForEndOfFrame).
        public bool CaptureRequested => _captureRequested;
        public IReadOnlyList<HudCommand> Screen => _screen;

        // Reference activeSceneChanged (:73) -> CloseGate (:76-80). presented — the frame presented
        // BEFORE the world change (the scene activates before the new world's first Core frame).
        public void SceneChanged(WorldFrame presented) => CloseGate(presented);

        void CloseGate(WorldFrame presented)
        {
            _gate = true;
            _gateFrame = presented;
        }

        // Reference ArmScreen (:86-97): the loading screen is held from this frame on.
        public void ArmScreen(IReadOnlyList<HudCommand> screen)
        {
            if (screen == null) { Arm(); return; }
            Release("repeated Arm before the previous hold was released");
            _payloadTaken = false;
            _frameAtPayload = null;
            _armedAt = _clock();
            _screen = screen;
            Armed++;
            Log?.Invoke($"[Travel] loading screen (Console.DrawLevelInfo, commands {screen.Count}) held until the first frame of the new world");
        }

        // Reference Arm (:107-121): no screen — the last presented frame is held.
        public void Arm()
        {
            Release("repeated Arm before the previous hold was released");
            _payloadTaken = false;
            _frameAtPayload = null;
            _armedAt = _clock();
            _captureRequested = true;
            Armed++;
        }

        // Reference CaptureAtEndOfFrame (:123-152) after ScreenCapture: the backend has copied the frame.
        public void Captured(int width, int height)
        {
            if (!_captureRequested) throw new System.InvalidOperationException("Travel: Captured without a capture request (Arm)");
            _captureRequested = false;
            _held = true;
            Log?.Invoke($"[Travel] the last frame of the old world is held ({width}x{height}) "
                + "until the first frame of the new world — like the shipped one blocked in LoadMap");
        }

        // Reference LateUpdate (:154-178), once per frame, AFTER the Core frame is applied.
        // presented — the presented Core frame (reference Runtime.PresentedFrame);
        // payloadPending — the new world has not yet taken the transition payload (reference
        // HP1LevelPresentation.Pending != null).
        public void Update(WorldFrame presented, bool payloadPending)
        {
            if (_gate)
            {
                WorldFrame f = presented;
                if (f != null && !ReferenceEquals(f, _gateFrame) && (f.Camera.Valid || !f.DrawWorld))
                {
                    _gate = false;
                    GateReleased++;
                }
            }
            if (!_held && _screen == null) return;
            // The new world started coming up: the payload was taken by the world setup (reference :166-173).
            if (!_payloadTaken)
            {
                if (payloadPending) return;
                _payloadTaken = true;
                _frameAtPayload = presented;
                return;
            }
            WorldFrame now = presented;
            if (now == null || ReferenceEquals(now, _frameAtPayload) || !now.Camera.Valid) return;
            Release($"the first frame of the new world applied (tick {now.Tick}), "
                + $"the hold lasted {_clock() - _armedAt:F2} s");
        }

        // Reference Release (:180-188).
        void Release(string why)
        {
            if (!_held && _screen == null) return;
            _held = false;
            _screen = null;
            Released++;
            Log?.Invoke($"[Travel] held frame released: {why}");
        }

        // Reference OnGUI (:190-208), same check order.
        public TravelView View
        {
            get
            {
                if (_gate && _screen == null && !_held) return TravelView.GateBlack;
                if (_screen != null) return TravelView.LoadingScreen;
                if (_held) return TravelView.HeldFrame;
                return TravelView.World;
            }
        }

        // Loading-screen quads (reference :200-202: full-screen FillBlack, then
        // HP1HudRender.Rasterize(_screen)) — via HudBatcher.BuildLevelInfoScreen.
        // Outside LoadingScreen: a refusal, not an empty list.
        // canvasScale: the scale Core built the screen's canvas with (UEWorldFrameProducer.CanvasScale).
        public IReadOnlyList<HudQuad> ScreenQuads(HudBatcher hud, int screenW, int screenH, int canvasScale)
        {
            if (_screen == null) throw new System.InvalidOperationException($"Travel: loading screen quads requested in state {View}");
            return hud.BuildLevelInfoScreen(_screen, screenW, screenH, canvasScale);
        }
    }
}
