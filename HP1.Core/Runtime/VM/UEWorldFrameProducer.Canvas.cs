// HUD and console canvas: HUD Pre/PostRender, the LevelInfo frame, console Pre/PostRender,
// HorPlus/console region, DrawTile and tile clipping, canvas frames. Text drawing is in
// UEWorldFrameProducer.Text.cs. Fields live in UEWorldFrameProducer.cs.
using System;
using System.Collections.Generic;
using System.IO;
using HP1.Core;
using UELib;

namespace HP1.UEScript
{
    public sealed partial class UEWorldFrameProducer
    {
        // ── The Core-owned HUD drive ────────────────────
        // Faithful order per shipped UGameEngine::Draw, once per core tick:
        // Console->PreRender, then UCanvas::Update (eventReset, then the frame
        // dims), then the view actor's PreRender — Engine.u
        // PlayerPawn.PreRender LAZILY SPAWNS myHUD — then the view actor's
        // PostRender drives myHUD.PostRender through the Canvas natives, then
        // Console->PostRender and Console.PostRender (ConsolePostRender).
        // TWO HALVES OF ONE UGameEngine::Draw (Engine.dll.c:117794): between
        // `Actor.PreRender(Canvas)` and `Actor.PostRender(Canvas)` shipped draws
        // the world — `Render->DrawWorld(Frame)`, and with it SetupDynamics (shadows), particles
        // and the weapon point; the event order and RNG use depend on this split.
        // Returns the frame size after the console border (for FSceneNode).
        (int X, int Y) HudPreRender(UEInterpreter interp, UEObject player)
        {
            _hud.Clear();
            if (interp is null || player is null)
                return (_viewportX, _viewportY);
            if (_canvas is null)
            {
                var canvasClass = HP1.Core.UEClassRegistry.Shared.Find("Canvas")
                    ?? throw new InvalidDataException("shipped Engine.Canvas class missing");
                _canvas = new UEObject { Class = canvasClass, Interp = interp };
                _canvas.LoadDefaults();
                _canvas.Props["Name"] = new UEName("HeadlessCanvas");
            }
            BindCanvasViewport();
            // FSceneNode frame: X/Y — viewport size (+0xa8/+0xac), XB/YB — origin
            // (+0xb0/+0xb4). The console may shrink it with a border (PreRender) and restore it
            // (PostRender); Canvas->Update takes the already shrunk frame. Before Update the canvas
            // holds last frame's values (Console->PreRender runs earlier).
            int frameX = _viewportX, frameY = _viewportY;
            ConsolePreRender(interp, ref frameX, ref frameY);
            // UCanvas::Update 0x10388f60: FIRST eventReset (0x10388fad), then
            // Frame, ClipX = (float)Frame.X, SizeX, ClipY, SizeY.
            var reset = interp.FindEventFunction(_canvas, "Reset");
            if (reset is not null) interp.Invoke(_canvas, reset, Array.Empty<object>());
            _masterFrame = new CanvasFrame { X = frameX, Y = frameY };
            _canvasFrames.Remove(_canvas);
            _canvasFrames.Add(_canvas, _masterFrame);
            _canvas.Props["ClipX"] = (float)frameX;
            _canvas.Props["SizeX"] = frameX;
            _canvas.Props["ClipY"] = (float)frameY;
            _canvas.Props["SizeY"] = frameY;
            var pre = interp.FindEventFunction(player, "PreRender");
            if (pre is not null) interp.Invoke(player, pre, new object[] { _canvas });
            return (frameX, frameY);
        }
        static Dictionary<int, (long fp, string what)> OverlayFingerprints(IEnumerable<UEObject> actors)
        {
            var d = new Dictionary<int, (long, string)>();
            foreach (UEObject a in actors)
            {
                if (a is null) continue;
                var h = new System.HashCode();
                string[] keys = { "Location", "Rotation", "bHidden", "DrawScale", "DrawType", "Mesh", "Texture", "Skin", "Style",
                    "Opacity", "LightType", "LightBrightness", "LightHue", "LightSaturation", "LightRadius", "AnimSequence", "AnimFrame",
                    "ScaleGlow", "AmbientGlow", "bUnlit", "Fatness" };
                foreach (string k in keys) h.Add(a.GetProp(k)?.ToString());
                h.Add(a.Destroyed);
                d[a.ObjectId] = (h.ToHashCode(), a.Class?.Name + "#" + a.ObjectId);
            }
            return d;
        }
        public WorldFrame BuildLevelInfoFrame(UEInterpreter interp, int tick, string text)
        {
            _hud.Clear();
            UEObject c = Console;
            if (c is not null)
            {
                if (_canvas is null)
                {
                    var canvasClass = HP1.Core.UEClassRegistry.Shared.Find("Canvas")
                        ?? throw new InvalidDataException("shipped Engine.Canvas class missing");
                    _canvas = new UEObject { Class = canvasClass, Interp = interp };
                    _canvas.LoadDefaults();
                    _canvas.Props["Name"] = new UEName("HeadlessCanvas");
                    BindCanvasViewport();
                    _canvas.Props["SizeX"] = _viewportX; _canvas.Props["SizeY"] = _viewportY;
                    _canvas.Props["ClipX"] = (float)_viewportX; _canvas.Props["ClipY"] = (float)_viewportY;
                    _masterFrame = new CanvasFrame { X = _viewportX, Y = _viewportY };
                    _canvasFrames.Remove(_canvas);
                    _canvasFrames.Add(_canvas, _masterFrame);
                }
                // No Canvas->Update here: the vtbl+0xb0 call before DrawLevelInfo (0x1039cd28-0x1039cd2d) is on Viewport+0x5c,
                // the RenDev (UViewport::Lock asserts "RenDev" on +0x5c, Engine.dll.c:60666); the Canvas is +0x54
                // (0x1039cd3a, DrawLevelInfo's argument). The canvas keeps the state of the last Draw's PostRender.
                // FULL-BRIGHTNESS LOADING SCREEN (NOT ORIGINAL): DrawLevelInfo draws the parchment with the canvas DrawColor it finds — whatever the last
                // Draw's PostRender set last (Canvas.Reset gives 127 at the start of a Draw; after a load from the menu
                // RenderUWindow set 255 before its windows drew, HPConsole.uc:731-733) — times D3DDrv stage 0 MODULATE 1x
                // (D3DDrv.dll.c:2656): at 127, half brightness. The shipped value per route is not measured. Core always
                // gives DrawLevelInfo the RenderUWindow colour 255; the script then sets the text colour itself
                // (HPConsole.uc:848-850).
                if (_canvas.GetProp(PK.DrawColor) is UEObject dc)
                    foreach (string k in new[] { "R", "G", "B" }) dc.Props[k] = (byte)255;
                // 4:3 REGION (NOT ORIGINAL): the loading screen, like the menu and pause, is a 4:3
                // picture centred on a wide screen (shipped has no region — FrameX is the whole viewport). The frontend
                // turns on LevelInfo43 and places bars by LevelInfoOffsetX. FrameX is always set: otherwise DrawLevelInfo
                // would take the width of the last console frame (the menu region) and draw the picture at the left edge.
                int frameX = LevelInfo43 && (long)_viewportX * 3 > (long)_viewportY * 4 ? _viewportY * 4 / 3 : _viewportX;
                LevelInfoOffsetX = (_viewportX - frameX) / 2;
                c.Props["FrameX"] = (float)frameX;
                c.Props["FrameY"] = (float)_viewportY;
                object sizeX = _canvas.GetProp(PK.SizeX), clipX = _canvas.GetProp(PK.ClipX);
                _canvas.Props["SizeX"] = frameX;
                _canvas.Props["ClipX"] = (float)frameX;
                var draw = interp.FindEventFunction(c, "DrawLevelInfo")
                    ?? throw new InvalidOperationException(
                        $"LoadMap: console {c.Class?.Name} has no DrawLevelInfo event (FindFunctionChecked)");
                interp.Invoke(c, draw, new object[] { _canvas, text ?? "" });
                _canvas.Props["SizeX"] = sizeX; _canvas.Props["ClipX"] = clipX;
                if (LevelInfoOffsetX > 0) ShiftHud(0, LevelInfoOffsetX);
            }
            HudCommand[] hud = _hud.ToArray();
            _hud.Clear();
            return new WorldFrame(tick, "levelinfo", Array.Empty<ActorSnapshot>(), default,
                Array.Empty<AudioCommand>(), default, hud, Array.Empty<ParticleInstance>(), -1, canvasScale: _canvasScale);
        }
        public UEObject CanvasForTest => _canvas;

        // UCanvas::Init (Engine 0x10388f40): `mov [ecx+0x78], eax` — Canvas.Viewport = the viewport of the
        // canvas owner. In the port the canvas is created lazily, the viewport belongs to the console.
        void BindCanvasViewport()
        {
            if (_canvas.GetProp("Viewport") is null && Console?.GetProp("Viewport") is UEObject vp)
                _canvas.Props["Viewport"] = vp;
        }

        void HudPostRender(UEInterpreter interp, UEObject player)
        {
            if (interp is null || player is null)
            {
                _hudCache = Array.Empty<HudCommand>();
                return;
            }
            if (_canvas is not null) { CanvasFrame hf = FrameOf(_canvas); LastPlayerHudFrame = (hf.X, hf.Y); }
            var post = interp.FindEventFunction(player, "PostRender");
            if (post is not null) interp.Invoke(player, post, new object[] { _canvas });
            ConsolePostRender(interp);
            _hudCache = _hud.ToArray();
        }

        // UConsole::PreRender (Engine.dll.c:91467-91537): console tick on the viewport
        // clock, then the border. ConsoleLines = ROUND(min(Y·ConsolePos, Y)) when
        // ConsolePos > 0; when BorderSize > 1: s = (BorderSize−1)·0.2,
        // BorderLines = max(ROUND(min(Y·s·0.25, Y)), 0), BorderPixels =
        // ROUND(min(X·s·0.25, X)) & ~3; the frame shrinks by 2·Border on both axes.
        void ConsolePreRender(UEInterpreter interp, ref int frameX, ref int frameY)
        {
            UEObject c = Console;
            if (c is null) return;
            var tick = interp.FindEventFunction(c, "Tick");
            if (tick is not null) { interp.Invoke(c, tick, new object[] { ViewportDelta }); ConsoleTickCount++; }
            c.Props["ConsoleLines"] = 0; c.Props["BorderLines"] = 0; c.Props["BorderPixels"] = 0;
            float pos = UEValue.AsFloat(c.GetProp(PK.ConsolePos));
            if (0f < pos)
            {
                float y = frameY, v = y * pos;
                if (v <= y) y = v;
                c.Props["ConsoleLines"] = (int)Math.Round(y, MidpointRounding.ToEven);
            }
            int border = UEValue.ToInt(c.GetProp(PK.BorderSize));
            if (1 < border)
            {
                float sc = (border - 1) * 0.2f;
                float y = frameY, vy = y * sc * 0.25f;
                if (vy <= y) y = vy;
                int lines = (int)Math.Round(y, MidpointRounding.ToEven);
                if (lines < 1) lines = 0;
                c.Props["BorderLines"] = lines;
                frameY -= lines * 2;
                float x = frameX, vx = x * sc * 0.25f;
                if (vx <= x) x = vx;
                int pixels = (int)Math.Round(x, MidpointRounding.ToEven) & ~3;
                c.Props["BorderPixels"] = pixels;
                frameX -= pixels * 2;
            }
        }

        // UConsole::PostRender (Engine.dll.c:32512-32528) + Console.eventPostRender
        // (UGameEngine::Draw after Actor.PostRender): the frame is restored, FrameX/Y =
        // its size, bDrewWorld = !bNoDrawWorld; then the script PostRender(Canvas).
        void ConsolePostRender(UEInterpreter interp)
        {
            UEObject c = Console;
            if (c is null) return;
            // Console region (presentation, not shipped): see SetConsoleRegion.
            int frameX = ConsoleRegionActive ? _consoleRegionX : _viewportX;
            // UConsole::PostRender (Engine.dll.c:32520-32525) restores the frame in
            // place from the PreRender copy — Canvas.Frame points at it.
            if (_masterFrame is not null) { _masterFrame.X = frameX; _masterFrame.Y = _viewportY; }
            c.Props["FrameX"] = (float)frameX;
            c.Props["FrameY"] = (float)_viewportY;
            c.Props["bDrewWorld"] = !UEValue.ToBool(c.GetProp(PK.bNoDrawWorld));
            int first = _hud.Count;
            // In the region the console canvas is region-wide too: UWindow takes the root
            // width from Canvas.ClipX (HPConsole.uc:768), not from the frame.
            object sizeX = _canvas?.GetProp(PK.SizeX), clipX = _canvas?.GetProp(PK.ClipX);
            if (ConsoleRegionActive && _canvas is not null)
            {
                _canvas.Props["SizeX"] = frameX;
                _canvas.Props["ClipX"] = (float)frameX;
            }
            var post = interp.FindEventFunction(c, "PostRender");
            if (post is not null) { interp.Invoke(c, post, new object[] { _canvas }); ConsolePostRenderCount++; }
            if (ConsoleRegionActive)
            {
                ShiftHud(first, ConsoleRegionOffsetX);
                if (_canvas is not null) { _canvas.Props["SizeX"] = sizeX; _canvas.Props["ClipX"] = clipX; }
            }
        }
        public void SetWideScreen(bool horPlus, bool levelInfo43) { HorPlus = horPlus; LevelInfo43 = levelInfo43; }
        public float HorPlusFov(float fov)
        {
            // the aspect of the world frame (WorldFrameSize: the window), not of the rounded-up (D-44) canvas
            if (!HorPlus || !(fov > 0f) || !(fov < 180f) || (long)_screenX * 3 <= (long)_screenY * 4) return fov;
            double t = Math.Tan(fov * 0.5 * Math.PI / 180.0) * (_screenX * 3.0) / (_screenY * 4.0);
            return (float)(2.0 * Math.Atan(t) * 180.0 / Math.PI);
        }
        public void SetConsoleRegion(int width) => _consoleRegionX = width;
        public bool ConsoleRegionActive => _consoleRegionX > 0 && _consoleRegionX < _viewportX;
        public int ConsoleRegionOffsetX => ConsoleRegionActive ? (_viewportX - _consoleRegionX) / 2 : 0;

        void ShiftHud(int first, int dx)
        {
            for (int i = first; i < _hud.Count; i++)
            {
                HudCommand h = _hud[i];
                HudGlyph[] g = h.Glyphs;
                if (g is not null)
                {
                    var moved = new HudGlyph[g.Length];
                    for (int k = 0; k < g.Length; k++)
                        moved[k] = new HudGlyph(g[k].Page, g[k].X + dx, g[k].Y, g[k].XL, g[k].YL, g[k].U, g[k].V, g[k].UL, g[k].VL);
                    g = moved;
                }
                _hud[i] = new HudCommand(h.Kind, h.X + dx, h.Y, h.XL, h.YL, h.U, h.V, h.UL, h.VL,
                    h.TexturePath, h.Text, h.FontPath, h.Color, h.Style, h.Smooth, h.Clipped, g, h.PolyFlags);
            }
        }

        static float CanvasF(UEObject c, string p) => UEValue.AsFloat(c.GetProp(p));
        static UEVector DrawColorOf(UEObject canvas)
            => canvas.GetProp(PK.DrawColor) is UEObject c
                ? new UEVector(UEValue.AsByte(c.GetProp(PK.R)),
                               UEValue.AsByte(c.GetProp(PK.G)),
                               UEValue.AsByte(c.GetProp(PK.B)))
                : default;
        static string TexPathOf(object v)
            => v is UEResourceRef rr ? (rr.ObjectPath ?? rr.Name)
             // Texture literals (Texture'HPBase.Icons.HarryBarFull') evaluate
             // to the UELib import/export object — same identity AssetPath uses.
             : v is UELib.Core.UObject uo ? uo.GetPath()
             // Runtime texture (CreateTextureFromBMP): path "SavePics.<name>",
             // by which the frontend takes pixels from UERuntimeTextures.
             : v is UEObject rto && UERuntimeTextures.PathOf(rto) is string rtp ? rtp
             : (v as UEObject)?.GetProp(PK.Name)?.ToString();
        static string FontPathOf(object f)
            => f is UEResourceRef rr ? (rr.ObjectPath ?? rr.Name)
            // The literal `Font'HPBase.HugeInkFont'` in a function BODY is an
            // ObjectConstToken and yields a UELib object (see the interpreter's ObjectConstToken handling),
            // not a UEResourceRef: a literal becomes a resource reference only on
            // the defaults path (UEObject.ParseResourceRef). Both forms reach
            // Canvas.Font — HPConsole.SetupLanguage assigns fonts as
            // literals — and without this branch the path would be null. The parse is
            // identical to the neighbouring AssetPathOf, where the same form is read
            // as GetPath(); the font catalog expects exactly `Package.Name`.
             : f is UELib.Core.UObject uo ? uo.GetPath()
             : (f as UEObject)?.GetProp(PK.Name)?.ToString();

        public void DrawTile(UEFrame frame, object[] args, bool clipped)
        {
            UEObject canvas = frame.Context ?? frame.Object;
            object tex = args is { Length: > 0 } ? args[0] : null;
            if (tex is null)
            {
                // Shipped execDrawTile: log and EARLY RETURN — no pen update.
                HP1.Core.UETrace.Warn("DrawTile: Missing Texture");
                return;
            }
            float xl = UEValue.AsFloat(args[1]);
            float yl = UEValue.AsFloat(args[2]);
            float u = UEValue.AsFloat(args[3]), v = UEValue.AsFloat(args[4]);
            float ul = UEValue.AsFloat(args[5]), vl = UEValue.AsFloat(args[6]);
            float curX = CanvasF(canvas, "CurX"), curY = CanvasF(canvas, "CurY");

            if (clipped)
            {
                // ── UCanvas::execDrawTileClipped (Engine.dll.c:32151-32200) ──
                //
                // CORE COMPUTES THE GEOMETRY, not the rasteriser.
                //
                // Shipped order: first a negative pen (shifting the texture window
                // along with it), then overflow past ClipX/ClipY.
                // Every adjustment goes PROPORTIONALLY through UV, so the command carries
                // the final rectangle and the frontend has nothing to clip.
                // 32154 (0x10389fdd: fcomp 0; test ah,0x41): XL/YL <= 0 or NaN — draw nothing, do not move the pen.
                if (!(xl > 0f) || !(yl > 0f)) return;
                // x87 chains (disasm 0x1038a000..0x1038a0b7) in the PC24 window — per operation in float (ClipTileAxis*).
                ClipTileAxisNegative(ref curX, ref u, ref ul, ref xl);
                ClipTileAxisNegative(ref curY, ref v, ref vl, ref yl);
                ClipTileAxisOverflow(CanvasF(canvas, "ClipX"), curX, ref ul, ref xl);
                ClipTileAxisOverflow(CanvasF(canvas, "ClipY"), curY, ref vl, ref yl);
                canvas.Props["CurX"] = curX;           // 32158/32167: pen clamped to zero
                canvas.Props["CurY"] = curY;
            }

            // exec (0x1038971d / 0x1038a0b7): Style == STY_None — DrawTile is not
            // called at all, the pen moves (tail below).
            int style = UEValue.ToInt(canvas.GetProp(PK.Style));
            if (style != 0)
                EmitTile(canvas, tex, curX + CanvasF(canvas, "OrgX"), curY + CanvasF(canvas, "OrgY"),
                    xl, yl, u, v, ul, vl, style, TilePolyFlags(style, UEValue.ToBool(canvas.GetProp(PK.bNoSmooth))), clipped);
            // Tail shared by both commands (32193-32197 / execDrawTile):
            // CurX += SpaceX + XL; CurYL = max(CurYL, YL). For a clipped tile XL is
            // ALREADY the clipped width, so the pen follows what was drawn.
            // Disasm: fld SpaceX; fadd CurX; fadd XL; fstp CurX (execDrawTile 0x103897c4..0x103897cd,
            // execDrawTileClipped 0x1038a15e..0x1038a167); PC24 window — each addition in float (TilePenAdvance).
            canvas.Props["CurX"] = TilePenAdvance(CanvasF(canvas, "SpaceX"), curX, xl);
            // fld CurYL; fcom YL; test ah,1 (0x103897d0 / 0x1038a16a): YL is taken when CurYL < YL
            // and on NaN ("unordered" sets C0) — MathF.Max would return NaN.
            float curYL = CanvasF(canvas, "CurYL");
            canvas.Props["CurYL"] = curYL >= yl ? curYL : yl;
        }

        // ── UCanvas::DrawTile (virtual, vtable +0x60; Engine.dll 0x10387630) ──
        //
        // Rejection and clipping by the canvas FRAME (Frame->FX/FY, +0xb8/+0xbc), then
        // RenDev->DrawTile. Reject: XL <= 0, YL <= 0, X+XL <= 0, Y+YL <= 0,
        // X >= FX, Y >= FY (0x10387679..0x10387700). Left/top clip
        // (0x10387706..0x1038776b): t = X·UL/XL (by the ORIGINAL XL), X = 0,
        // XL = X+XL, U -= t, UL += t. Right/bottom (0x1038776e..0x103877c6):
        // XL > FX-X -> UL += ((FX-X) - XL)·UL/XL, XL = FX-X. PRECISION: inside the
        // Render window (FastMathWindow, HUD/console) — PC24, every operation in float,
        // like the exec helpers ClipTileAxis*; outside the window (LoadMap loading screen)
        // — PC64, intermediates in the register (modelled as double), stored as float.
        // Rest of the body: Texture->Update/AnimCur (a texture with AnimNext is a STOP in
        // TexturePolyFlags: Update is not ported), Lock with scale USize·UScale/USize
        // (cancels against the device division) and device PolyFlags =
        // exec flags | (Texture->PolyFlags & PF_Masked) (0x1038781f-0x1038782e).
        // Core decides the flags and puts them into the command; the frontend only applies
        // the blend mode they select.
        internal void EmitTile(UEObject canvas, object tex, float x, float y, float xl, float yl,
            float u, float v, float ul, float vl, int style, int execFlags, bool clipped)
        {
            CanvasFrame fr = FrameOf(canvas);
            float fx = fr.X, fy = fr.Y;
            if (!(xl > 0f) || !(yl > 0f)) return;
            if (FastMathWindow)
            {
                if (!FrameClipTilePc24(fx, fy, ref x, ref y, ref xl, ref yl, ref u, ref v, ref ul, ref vl)) return;
                EmitClippedTile(canvas, tex, x, y, xl, yl, u, v, ul, vl, style, execFlags, clipped);
                return;
            }
            float xEnd = (float)((double)x + xl), yEnd = (float)((double)y + yl);
            if (!((double)x + xl > 0.0) || !((double)y + yl > 0.0)) return;
            if (!(x < fx) || !(y < fy)) return;
            if (x < 0f)
            {
                double t = (double)x * ul / xl;
                x = 0f; xl = xEnd;
                u = (float)(u - t); ul = (float)(t + ul);
            }
            if (y < 0f)
            {
                double t = (double)y * vl / yl;
                y = 0f; yl = yEnd;
                v = (float)(v - t); vl = (float)(t + vl);
            }
            double availX = (double)fx - x;
            if (xl > availX) { ul = (float)((availX - xl) * ul / xl + ul); xl = (float)availX; }
            double availY = (double)fy - y;
            if (yl > availY) { vl = (float)((availY - yl) * vl / yl + vl); yl = (float)availY; }
            EmitClippedTile(canvas, tex, x, y, xl, yl, u, v, ul, vl, style, execFlags, clipped);
        }

        // UCanvas::DrawTile 0x10387679..0x103877c6 in the PC24 window: each fadd/fmul/fdiv
        // rounds to 24 bits = a float operation. false — rejected (no command).
        public static bool FrameClipTilePc24(float fx, float fy, ref float x, ref float y, ref float xl, ref float yl,
            ref float u, ref float v, ref float ul, ref float vl)
        {
            float xEnd = (float)(x + xl);
            if (!(0f < xEnd)) return false;
            float yEnd = (float)(y + yl);
            if (!(0f < yEnd) || !(x < fx) || !(y < fy)) return false;
            if (x < 0f)
            {
                float t = (float)((float)(x * ul) / xl);
                x = 0f; u = (float)(u - t); ul = (float)(t + ul); xl = xEnd;
            }
            if (y < 0f)
            {
                float t = (float)((float)(y * vl) / yl);
                y = 0f; v = (float)(v - t); vl = (float)(t + vl); yl = yEnd;
            }
            float ax = (float)(fx - x);
            if (ax < xl) { ul = (float)((float)((float)((float)(ax - xl) * ul) / xl) + ul); xl = ax; }
            float ay = (float)(fy - y);
            if (ay < yl) { vl = (float)((float)((float)((float)(ay - yl) * vl) / yl) + vl); yl = ay; }
            return true;
        }

        void EmitClippedTile(UEObject canvas, object tex, float x, float y, float xl, float yl,
            float u, float v, float ul, float vl, int style, int execFlags, bool clipped)
        {
            Func<object, int> texFlags = TexturePolyFlags
                ?? throw new InvalidOperationException("UCanvas::DrawTile: TexturePolyFlags not assigned (UELevelRuntime.FrameProducer)");
            int polyFlags = (texFlags(tex) & PF_Masked) | execFlags;
            _hud.Add(new HudCommand(HudKind.Tile, x, y, xl, yl, u, v, ul, vl,
                TexPathOf(tex), null, null, DrawColorOf(canvas), style,
                !UEValue.ToBool(canvas.GetProp(PK.bNoSmooth)), clipped, polyFlags: polyFlags));
        }

        // Tile exec-body flags are THE SAME for execDrawTile (0x1038971d-0x10389750)
        // and execDrawTileClipped (0x1038a0b7-0x1038a0ea): Style 3 -> PF_Translucent,
        // Style 4 -> PF_Modulated, others (incl. STY_Masked) -> 0; then
        // | ((Canvas+0x5c >> 1) & 1) << 11 — bNoSmooth -> PF_NoSmooth — and
        // `or dh,1` — PF_TwoSided. The mask comes ONLY from the texture (UCanvas::DrawTile).
        // PRECISION — PC24 (Render disasm): execDrawTile/execDrawTileClipped
        // run from HUD/console scripts inside the URender::PreRender window (appEnableFastMath(1), 0x10b1eeb1) ..
        // PostRender (appEnableFastMath(0), 0x10b1efa9); _controlfp(_PC_24) — every x87 operation rounds the mantissa
        // to 24 bits, i.e. matches a float operation (the exponent range is not reached here). The PC64
        // "single rounding" model is right for game logic, not for the canvas.
        // Negative pen: fld UL; fmul Cur; fdiv XL (d in the register); U = U − d; UL = d + UL; XL = XL + Cur; Cur = 0.
        // fcomp 0; test ah,1 (0x1038a000): C0 also on NaN ("unordered") — a NaN pen is clipped too.
        public static void ClipTileAxisNegative(ref float cur, ref float uv, ref float ulen, ref float len)
        {
            if (cur >= 0f) return;
            float d = (float)((float)(ulen * cur) / len);
            uv = (float)(uv - d);
            ulen = (float)(d + ulen);
            len = (float)(len + cur);
            cur = 0f;
        }

        // Overflow: a = Clip − Cur (in the register); if XL > a: UL = ((a − XL)·UL)/XL + UL; XL = a. fcomp on NaN is
        // "unordered", the branch is skipped (test ah, 0x41). PC24 — see above.
        public static void ClipTileAxisOverflow(float clip, float cur, ref float ulen, ref float len)
        {
            float a = (float)(clip - cur);
            if (!(len > a)) return;
            float diff = (float)(a - len);
            float q = (float)((float)(diff * ulen) / len);
            ulen = (float)(q + ulen);
            len = a;
        }

        // DrawTile/DrawTileClipped pen advance: (SpaceX + CurX) + XL, PC24 — each addition rounded to float.
        public static float TilePenAdvance(float spaceX, float curX, float xl)
            => (float)((float)(spaceX + curX) + xl);

        internal static int TilePolyFlags(int style, bool noSmooth)
            => (style == 3 ? PF_Translucent : style == 4 ? PF_Modulated : 0)
               | (noSmooth ? PF_NoSmooth : 0) | PF_TwoSided;

        // ── Canvas frame: Canvas.FramePtr (+0x7c) ─────────────────────────────
        //
        // UCanvas::Update (Engine.dll.c:79722) sets Frame = the UGameEngine::Draw
        // master frame — THE SAME FSceneNode that UConsole::PreRender
        // shrinks by the border (Engine.dll.c:91467: Frame->X/Y -= 2·Border), and
        // UConsole::PostRender restores in place from a copy taken before the shrink
        // (Engine.dll.c:32512: 0x5b words from DAT_105e89d8) — BEFORE the script
        // Console.PostRender. So the player's HUD sees the shrunk frame and the console
        // the full one. The frame is read by the text bodies: WrappedPrint (Frame->Y,
        // 0x1038800c) and glyph drawing FUN_10388b60 (Frame->X/Y).
        sealed class CanvasFrame { public int X, Y; }

        CanvasFrame FrameOf(UEObject canvas)
            => _canvasFrames.TryGetValue(canvas, out CanvasFrame f) ? f
             : throw new InvalidOperationException(
                 "Canvas.Frame not assigned: UCanvas::Update has not run for this canvas "
                 + "(shipped would dereference a NULL FramePtr)");

        // Fixture seam: a fixture canvas gets its own frame the same way the real one
        // gets the master frame in UCanvas::Update.
        public void SetCanvasFrameForTest(UEObject canvas, int x, int y)
        {
            _canvasFrames.Remove(canvas);
            _canvasFrames.Add(canvas, new CanvasFrame { X = x, Y = y });
        }
        public (int X, int Y) CanvasFrameForTest(UEObject canvas)
        {
            CanvasFrame f = FrameOf(canvas);
            return (f.X, f.Y);
        }
    }
}
