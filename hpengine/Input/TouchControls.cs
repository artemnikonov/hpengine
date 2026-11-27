// NOT ORIGINAL (iOS port): on-screen touch controls. They are a virtual gamepad: touches become the raw pad state
// SdlGamepad reports (sticks, South/West/Start buttons), merged with a connected physical pad, and Core's gamepad support
// (HP1.Core UEInput.Gamepad.cs) turns that into the game's input exactly as for a real pad. Nothing here reads or
// writes Core state; the overlay is frontend drawing only.
//
//   left part of the screen   floating movement stick (left stick) where the finger lands
//   right part of the screen  floating camera stick (right stick); in menus it moves the cursor, as a pad's does.
//                             Touch and hold still for a moment, then drag (in a spell lesson, any touch at once): the finger is the PC mouse with its left
//                             button held (DefUser.ini LeftMouse=AltFire, Cast): the spell lesson's wand follows the finger,
//                             so the pattern can be traced by dragging along it.
//   bottom right              Jump (South / A) and Cast (West / X); in menus A clicks
//   top left                  Menu (Start / Escape)
//
// All on the window thread: Feed from the event loop, Read through the pad read, AppendOverlay while building the frame.
using System;
using System.Collections.Generic;
using HP1.UEScript;
using HPEngine.Hud;
using SDL;

namespace HPEngine.Input
{
    public static class TouchControls
    {
        public static readonly bool Enabled = OperatingSystem.IsIOS();
        // Texture key the HUD pass resolves to its generated anti-aliased disc (SdlHudPass.HudTexture).
        public const string CircleTexture = "$touch-circle";

        enum Role { Move, Look, Draw, Jump, Cast, Menu, Pointer, None }
        sealed class Finger { public Role Role; public float OX, OY, X, Y; public long Down; public bool Moved; }

        // Draw: a right-side finger that stayed within HoldSlop of where it landed for HoldSeconds. Its motion is sent as
        // mouse motion (MouseMotion, InputStage.OnMouseMotion), scaled so the lesson wand stays under the finger: the wand
        // is drawn at WandX x FOVRatio (0.8) of WandDist, FOV 90 with the 4:3 vertical field kept (Hor+), so one wand unit
        // is 0.8 x (H/2) / 0.75 = 0.533 H pixels; one mouse pixel moves the wand 0.0016874 (measured with --window-script
        // on Lev_Tut1's SpellLearnTrigger0, both axes). Mouse pixels per screen pixel: 1 / (0.533 H x 0.0016874).
        const double HoldSeconds = 0.2;
        static float HoldSlop => 0.02f * Unit;
        static float MousePerPixel => 1f / (0.8f * 0.5f / 0.75f * Unit * 0.0016874f);
        public static Action<float, float> MouseMotion;
        // Without direct control (a menu, a cut-scene): a finger is the menu's mouse at that point (InputStage.OnMenuTouch ->
        // Core UEInput.MenuTap, which does nothing when no menu is open): down, moved (null), up. Window pixels.
        public static Action<float, float, bool?> MenuPointer;
        // The frame's WorldFrame.PointerTracing (a spell lesson): every touch but Menu draws at once, and no sticks or
        // buttons are drawn over the lesson.
        static bool _tracing;
        // The frame's WorldFrame.PlayerControl: the sticks and Jump/Cast work only while the player directly controls Harry
        // (not in a cut-scene, menu, pause or lesson). Menu stays usable everywhere: without a pad it is the only way to the menu.
        static bool _control;

        static readonly Dictionary<ulong, Finger> _fingers = new();
        static int _w = 1, _h = 1;
        static bool _physicalPad;

        // Layout in pixels from the screen size; the unit is the screen height.
        static float Unit => _h;
        static float StickRadius => 0.12f * Unit;
        static (float X, float Y, float R) JumpButton => (_w - 0.14f * Unit, _h - 0.15f * Unit, 0.085f * Unit);
        static (float X, float Y, float R) CastButton => (_w - 0.34f * Unit, _h - 0.13f * Unit, 0.075f * Unit);
        static (float X, float Y, float R) MenuButton => (0.07f * Unit, 0.07f * Unit, 0.045f * Unit);
        static (float X, float Y) MoveRest => (0.22f * Unit, _h - 0.24f * Unit);
        static float MoveZoneRight => _w * 0.4f;

        public static void Resize(int w, int h) { if (w > 0 && h > 0) { _w = w; _h = h; } }

        static bool Inside((float X, float Y, float R) b, float x, float y)
        {
            float dx = x - b.X, dy = y - b.Y, r = b.R * 1.25f;   // a little larger than drawn: fingers are imprecise
            return dx * dx + dy * dy <= r * r;
        }

        public static unsafe void Feed(in SDL_Event e)
        {
            if (!Enabled) return;
            var type = (SDL_EventType)e.type;
            if (type == SDL_EventType.SDL_EVENT_WINDOW_FOCUS_LOST)
            {
                foreach (var g in _fingers.Values) if (g.Role == Role.Pointer) MenuPointer?.Invoke(g.X, g.Y, false);   // no stuck click
                _fingers.Clear();
                return;
            }
            if (type is not (SDL_EventType.SDL_EVENT_FINGER_DOWN or SDL_EventType.SDL_EVENT_FINGER_MOTION
                or SDL_EventType.SDL_EVENT_FINGER_UP or SDL_EventType.SDL_EVENT_FINGER_CANCELED)) return;
            ulong id = (ulong)e.tfinger.fingerID;
            float x = e.tfinger.x * _w, y = e.tfinger.y * _h;   // SDL gives the touch in window fractions
            switch (type)
            {
                case SDL_EventType.SDL_EVENT_FINGER_DOWN:
                    Role role = Inside(MenuButton, x, y) ? Role.Menu
                        : _tracing ? Role.Draw
                        : !_control ? Role.Pointer
                        : Inside(JumpButton, x, y) ? Role.Jump
                        : Inside(CastButton, x, y) ? Role.Cast
                        : x < MoveZoneRight ? Role.Move : Role.Look;
                    _fingers[id] = new Finger { Role = role, OX = x, OY = y, X = x, Y = y, Down = System.Diagnostics.Stopwatch.GetTimestamp() };
                    if (role == Role.Pointer) MenuPointer?.Invoke(x, y, true);
                    break;
                case SDL_EventType.SDL_EVENT_FINGER_MOTION:
                    if (_fingers.TryGetValue(id, out var f))
                    {
                        if (f.Role == Role.Look && !f.Moved && HeldStill(f)) f.Role = Role.Draw;
                        if (f.Role == Role.Draw) MouseMotion?.Invoke((x - f.X) * MousePerPixel, (y - f.Y) * MousePerPixel);
                        else if (f.Role == Role.Pointer) MenuPointer?.Invoke(x, y, null);
                        else if (MathF.Abs(x - f.OX) > HoldSlop || MathF.Abs(y - f.OY) > HoldSlop) f.Moved = true;
                        f.X = x; f.Y = y;
                    }
                    break;
                default:
                    if (_fingers.TryGetValue(id, out var up) && up.Role == Role.Pointer) MenuPointer?.Invoke(x, y, false);
                    _fingers.Remove(id);
                    break;
            }
        }

        static bool HeldStill(Finger f) =>
            (System.Diagnostics.Stopwatch.GetTimestamp() - f.Down) >= HoldSeconds * System.Diagnostics.Stopwatch.Frequency;

        // A right-side finger that has not moved becomes a drawing finger once held long enough, also without a motion event.
        static void PromoteHeld()
        {
            foreach (var f in _fingers.Values)
                if (f.Role == Role.Look && !f.Moved && HeldStill(f)) f.Role = Role.Draw;
        }

        static (float X, float Y) Stick(Role role)
        {
            foreach (var f in _fingers.Values)
            {
                if (f.Role != role) continue;
                float dx = (f.X - f.OX) / StickRadius, dy = (f.Y - f.OY) / StickRadius;
                float len = MathF.Sqrt(dx * dx + dy * dy);
                if (len > 1f) { dx /= len; dy /= len; }
                return (dx, dy);
            }
            return (0f, 0f);
        }

        static bool Held(Role role)
        {
            foreach (var f in _fingers.Values) if (f.Role == role) return true;
            return false;
        }

        static short Axis(float v) => (short)Math.Clamp((int)MathF.Round(v * 32767f), -32767, 32767);

        // The pad read Core polls: a physical pad's state with the touches merged in (a touched stick replaces that pad
        // stick, buttons add). With touch controls on there is always a pad, so Core keeps the gamepad input active.
        public static bool Read(out UEInput.GamepadState s)
        {
            _physicalPad = SdlGamepad.Read(out s);
            if (!Enabled) return _physicalPad;
            if (!_physicalPad) { s = default; s.Type = "SDL_GAMEPAD_TYPE_STANDARD"; s.Faces = "A B X Y"; }
            var (mx, my) = Stick(Role.Move);
            if (mx != 0f || my != 0f) { s.LeftX = Axis(mx); s.LeftY = Axis(my); }   // SDL axes: +Y is down, as the screen
            PromoteHeld();
            var (lx, ly) = Stick(Role.Look);
            if (lx != 0f || ly != 0f) { s.RightX = Axis(lx); s.RightY = Axis(ly); }
            if (Held(Role.Jump)) s.Buttons |= 1u << UEInput.PadSouth;
            if (Held(Role.Cast) || Held(Role.Draw)) s.Buttons |= 1u << UEInput.PadWest;   // drawing holds Cast
            if (Held(Role.Menu)) s.Buttons |= 1u << UEInput.PadStart;
            return true;
        }

        static void Disc(List<HudQuad> quads, float x, float y, float r, float red, float green, float blue, float a) =>
            quads.Add(new HudQuad(x - r, y - r, x + r, y + r, 0, 0, 1, 1, red, green, blue, a,
                HudBlend.AlphaBlend, false, CircleTexture, HudQuadSource.Fill, -1, smooth: true));

        // Drawn over the frame's 2D layer; hidden while a physical pad is in use and the screen is not being touched.
        public static void AppendOverlay(List<HudQuad> quads, int w, int h, bool tracing, bool control)
        {
            Resize(w, h);
            _tracing = tracing; _control = control;
            // Control lost (a cut-scene started, the menu opened): a held stick or button lets go.
            if (!control)
                foreach (var g in _fingers.Values)
                    if (g.Role is Role.Move or Role.Look or Role.Jump or Role.Cast) g.Role = Role.None;
            if (!Enabled || (_physicalPad && _fingers.Count == 0)) return;
            if (tracing || !control)
            {
                foreach (var g in _fingers.Values)
                    if (g.Role == Role.Draw) Disc(quads, g.X, g.Y, 0.05f * Unit, 0.45f, 0.6f, 1f, 0.45f);
                var m = MenuButton;
                Disc(quads, m.X, m.Y, m.R, 1, 1, 1, Held(Role.Menu) ? 0.5f : 0.2f);
                return;
            }
            foreach (var role in new[] { Role.Move, Role.Look })
            {
                Finger f = null;
                foreach (var g in _fingers.Values) if (g.Role == role) { f = g; break; }
                if (f is null)
                {
                    if (role == Role.Move) Disc(quads, MoveRest.X, MoveRest.Y, StickRadius, 1, 1, 1, 0.12f);   // where to touch
                    continue;
                }
                var (sx, sy) = Stick(role);
                Disc(quads, f.OX, f.OY, StickRadius, 1, 1, 1, 0.15f);
                Disc(quads, f.OX + sx * StickRadius, f.OY + sy * StickRadius, StickRadius * 0.45f, 1, 1, 1, 0.35f);
            }
            foreach (var g in _fingers.Values)   // a drawing finger: a blue ring under it (Cast is held)
                if (g.Role == Role.Draw) Disc(quads, g.X, g.Y, 0.05f * Unit, 0.45f, 0.6f, 1f, 0.45f);
            var jump = JumpButton; var cast = CastButton; var menu = MenuButton;
            Disc(quads, jump.X, jump.Y, jump.R, 0.35f, 0.85f, 0.45f, Held(Role.Jump) ? 0.6f : 0.3f);   // Jump: green
            Disc(quads, cast.X, cast.Y, cast.R, 0.45f, 0.6f, 1f, Held(Role.Cast) ? 0.6f : 0.3f);      // Cast: blue
            Disc(quads, menu.X, menu.Y, menu.R, 1, 1, 1, Held(Role.Menu) ? 0.5f : 0.2f);              // Menu
        }
    }
}
