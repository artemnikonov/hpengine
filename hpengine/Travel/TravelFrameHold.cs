// Frame hold across a map change: keeps the loading frame on screen until the new world has a frame to show.
//
// What is held: shipped UGameEngine::LoadMap, after loading the new map's LevelInfo0,
// draws Console.DrawLevelInfo (the frame is built by Core: UELevelRuntime.BuildLevelInfoScreen /
// LastLoadMapScreen), and that frame stays on screen while the map loads. Here ONLY the hold: the loading
// screen (HUD commands on black) or, without a screen (no console), the last presented frame. It is released
// by the first applied frame of the new world with a valid camera — an observable condition, not "after
// N frames"; no splash, no delay.
//
// Separately, the "gate": after the window opens and after every world change the 3D world
// is covered with black until the first NEW Core frame that has a camera or does not draw the world.
//
// The module makes no GPU calls and measures no time: per frame it returns a TravelView decision and
// (for the loading screen) HudBatcher quads.
using System.Collections.Generic;
using HP1.UEScript;
using HPEngine.Hud;

namespace HPEngine.Travel
{
    // What to draw this frame (checked in this order).
    public enum TravelView
    {
        // Nothing held and the gate is open: a normal frame.
        World,
        // Gate closed: black INSTEAD of the 3D world, as the lowest layer
        // The whole 2D frame (coronas, HUD, flash) draws as usual.
        GateBlack,
        // Loading screen: full-screen black + DrawLevelInfo commands,
        // above everything (GUI.depth = int.MinValue); nothing else in the frame.
        LoadingScreen,
        // Held last frame: above everything; it is a FRAME, not a UI element.
        HeldFrame,
    }

    public sealed class TravelFrameHold
    {
        // Counters for acceptance and the log.
        public int Armed, Released, GateReleased;

        // Log. null — silent.
        public System.Action<string> Log;

        // Real time in seconds — only for the hold-duration log line
        // Never affects decisions.
        readonly System.Func<double> _clock;

        bool _held;                              // with SDL_GPU the backend holds the copy
        bool _captureRequested;
        IReadOnlyList<HudCommand> _screen;
        bool _payloadTaken;
        WorldFrame _frameAtPayload;
        double _armedAt;
        bool _gate;
        WorldFrame _gateFrame;

        // InstallGate: the gate closes on install (before the first scene).
        // presented — the frame presented at that moment (reference PresentedFrame, may be null).
        public TravelFrameHold(WorldFrame presented, System.Func<double> clock = null)
        {
            _clock = clock ?? (() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency);
            CloseGate(presented);
        }

        // A hold is active (loading screen or captured frame).
        public bool Holding => _held || _screen != null;
        public bool GateClosed => _gate;
        public bool PayloadTaken => _payloadTaken;
        // The backend must copy the frame presented AFTER this Arm() call and
        // report Captured().
        public bool CaptureRequested => _captureRequested;
        public IReadOnlyList<HudCommand> Screen => _screen;

        // Scene change -> CloseGate. presented — the frame presented
        // BEFORE the world change (the scene activates before the new world's first Core frame).
        public void SceneChanged(WorldFrame presented) => CloseGate(presented);

        void CloseGate(WorldFrame presented)
        {
            _gate = true;
            _gateFrame = presented;
        }

        // ArmScreen: the loading screen is held from this frame on.
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

        // Arm: no screen — the last presented frame is held.
        public void Arm()
        {
            Release("repeated Arm before the previous hold was released");
            _payloadTaken = false;
            _frameAtPayload = null;
            _armedAt = _clock();
            _captureRequested = true;
            Armed++;
        }

        // After the screen capture: the backend has copied the frame.
        public void Captured(int width, int height)
        {
            if (!_captureRequested) throw new System.InvalidOperationException("Travel: Captured without a capture request (Arm)");
            _captureRequested = false;
            _held = true;
            Log?.Invoke($"[Travel] the last frame of the old world is held ({width}x{height}) "
                + "until the first frame of the new world — like the shipped one blocked in LoadMap");
        }

        // Once per frame, AFTER the Core frame is applied.
        // presented — the presented Core frame;
        // payloadPending — the new world has not yet taken the transition payload.
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
            // The new world started coming up: the payload was taken by the world setup.
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

        // Release.
        void Release(string why)
        {
            if (!_held && _screen == null) return;
            _held = false;
            _screen = null;
            Released++;
            Log?.Invoke($"[Travel] held frame released: {why}");
        }

        // Checked in this order.
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

        // Loading-screen quads (full-screen black, then the
        // rasterised screen) — via HudBatcher.BuildLevelInfoScreen.
        // Outside LoadingScreen: a refusal, not an empty list.
        // canvasScale: the scale Core built the screen's canvas with (UEWorldFrameProducer.CanvasScale).
        public IReadOnlyList<HudQuad> ScreenQuads(HudBatcher hud, int screenW, int screenH, int canvasScale)
        {
            if (_screen == null) throw new System.InvalidOperationException($"Travel: loading screen quads requested in state {View}");
            return hud.BuildLevelInfoScreen(_screen, screenW, screenH, canvasScale);
        }
    }
}
