// RASTERIZER OF READY Core HUD COMMANDS (WorldFrame.Hud) -> quad batches.
//
// All Canvas semantics (pen, line wrap, glyph layout, CharRemap, clipping by ClipX/ClipY and by the frame, PolyFlags
// choice) execute in Core (UEWorldFrameProducer). Here ready commands only become screen quads: interface scale,
// edge snapping to pixels, texture window, blend mode. The module does not call the GPU: the backend draws, in list order.
using HP1.UEScript;

namespace HPEngine.Hud
{
    public sealed class HudBatcher
    {
        // UE1 PolyFlags (Engine/Inc/UnObj.h) applied by a tile.
        public const int PF_Masked = 0x2, PF_Translucent = 0x4, PF_Modulated = 0x40;

        // Logical texture size by UE path: USize/VSize of mip 0 (D3DDrv scales UV by 1/(1<<firstMip) rather than
        // changing USize). null means no texture.
        readonly Func<string, (int W, int H)?> _textureSize;
        // Font page paths by font key (name leaf); null means no font.
        readonly Func<string, string[]> _fontPages;
        readonly Dictionary<string, string[]> _fonts = new(StringComparer.OrdinalIgnoreCase);
        readonly List<HudQuad> _quads = new();

        // Counters of the last Build, and what would otherwise be skipped silently, as named counters (loud).
        public int Calls, Tiles, TexHit, TexMiss, Texts;
        public int FontMiss;             // provider has no such font
        public int GlyphPageMiss;        // Page outside the font pages, or a page without texture
        // Paths the provider did not find (over the batcher's lifetime).
        public readonly HashSet<string> MissingTextures = new(StringComparer.Ordinal);
        public readonly HashSet<string> MissingFonts = new(StringComparer.Ordinal);

        // Interface scale of the last Build: the one Core built the frame's canvas with (WorldFrame.CanvasScale).
        public int Scale { get; private set; } = 1;

        public HudBatcher(Func<string, (int W, int H)?> textureSize, Func<string, string[]> fontPages)
        {
            _textureSize = textureSize ?? throw new ArgumentNullException(nameof(textureSize));
            _fontPages = fontPages ?? throw new ArgumentNullException(nameof(fontPages));
        }

        // Width of the 4:3 menu region in SCREEN pixels (NOT ORIGINAL): menu open and screen wider
        // than 4:3 -> H·4/3, otherwise 0. Core receives it in canvas units: SetConsoleRegion(width / CanvasScale).
        public static int ConsoleRegionWidth(bool menuActive, int screenW, int screenH)
            => menuActive && screenW * 3 > screenH * 4 ? screenH * 4 / 3 : 0;

        // HUD frame, order:
        //   1) [coronas are not here, the sprite subsystem draws them BEFORE this list];
        //   2) black screen fill when !frame.DrawWorld (menu: world not drawn, gate Engine.dll.c:117935-117939);
        //   3) frame.Hud commands;
        //   4) black 4:3 menu sides (UEWorldFrameProducer.ConsoleRegionOffsetX, canvas units; 0 = region inactive).
        // The list belongs to the batcher and is valid until the next Build.
        public IReadOnlyList<HudQuad> Build(WorldFrame frame, int screenW, int screenH, int consoleRegionOffsetX)
        {
            Begin(frame?.CanvasScale ?? 1);
            if (frame != null && !frame.DrawWorld)
                FillBlack(0, 0, screenW, screenH);
            if (frame != null) Rasterize(frame.Hud);
            if (consoleRegionOffsetX > 0)
            {
                int off = consoleRegionOffsetX * Scale, w = screenW - 2 * off;
                FillBlack(0, 0, off, screenH);
                FillBlack(off + w, 0, screenW - off - w, screenH);
            }
            return _quads;
        }

        // Loading screen of UGameEngine::LoadMap (Console.DrawLevelInfo; Core
        // UELevelRuntime.BuildLevelInfoScreen(...).Hud): black screen and its commands
        // (Viewport->Lock clears to black, no world).
        public IReadOnlyList<HudQuad> BuildLevelInfoScreen(IReadOnlyList<HudCommand> screen, int screenW, int screenH, int canvasScale)
        {
            Begin(canvasScale);
            FillBlack(0, 0, screenW, screenH);
            Rasterize(screen);
            return _quads;
        }

        void Begin(int canvasScale)
        {
            _quads.Clear();
            Calls = Tiles = TexHit = TexMiss = Texts = FontMiss = GlyphPageMiss = 0;
            Scale = canvasScale;
        }

        // HP1HudRender.Rasterize (:45-98).
        void Rasterize(IReadOnlyList<HudCommand> cmds)
        {
            if (cmds == null) return;
            for (int i = 0; i < cmds.Count; i++)
            {
                HudCommand c = cmds[i];
                float r = c.Color.x / 255f, g = c.Color.y / 255f, b = c.Color.z / 255f;
                if (c.Kind == HudKind.Tile)
                {
                    Calls++;
                    // STY_None and per-frame refusals are decided by Core: such a command is not in the frame.
                    if (c.XL <= 0f || c.YL <= 0f) continue;
                    Tiles++;
                    (int W, int H)? size = Size(c.TexturePath);
                    if (size == null) { TexMiss++; continue; }
                    TexHit++;
                    DrawWithFlags(c.TexturePath, size.Value, c.X, c.Y, c.XL, c.YL, c.U, c.V, c.UL, c.VL,
                        c.PolyFlags, r, g, b, HudQuadSource.Tile, i);
                }
                else
                {
                    // Glyphs computed by Core; null means a native font (drawn by the OS device; Core has no layout
                    // for it, and none is invented here).
                    if (c.Glyphs == null) throw new InvalidOperationException("HUD text without glyphs: Core supplies the glyphs of every font, native ones included (D-39)");
                    if (c.Glyphs.Length == 0) continue;
                    string[] pages = Font(c.FontPath);
                    if (pages == null) { FontMiss++; continue; }
                    foreach (HudGlyph gl in c.Glyphs)
                    {
                        string page = gl.Page >= 0 && gl.Page < pages.Length ? pages[gl.Page] : null;
                        (int W, int H)? ps = page == null ? null : Size(page);
                        if (ps == null) { GlyphPageMiss++; continue; }
                        // Glyph flags are the text command's c.PolyFlags (Core: TextPolyFlags).
                        DrawWithFlags(page, ps.Value, gl.X, gl.Y, gl.XL, gl.YL, gl.U, gl.V, gl.UL, gl.VL,
                            c.PolyFlags, r, g, b, HudQuadSource.Glyph, i);
                    }
                    Texts++;
                }
            }
        }

        // HP1HudRender.DrawWithFlags (:204-226) + Blit/BlitMaterial (:115-135, :230-237).
        void DrawWithFlags(string tex, (int W, int H) size, float x, float y, float xl, float yl,
                           float u, float v, float ul, float vl, int pf, float r, float g, float b,
                           HudQuadSource source, int cmd)
        {
            // Interface scale: Core canvas commands are stretched back to the screen.
            x *= Scale; y *= Scale; xl *= Scale; yl *= Scale;
            // Edges snap to integer pixels, each separately (adjacent tiles share one boundary).
            // Rounding is to even.
            float x0 = MathF.Round(x, MidpointRounding.ToEven), y0 = MathF.Round(y, MidpointRounding.ToEven);
            float x1 = MathF.Round(x + xl, MidpointRounding.ToEven), y1 = MathF.Round(y + yl, MidpointRounding.ToEven);
            if (x1 - x0 <= 0f || y1 - y0 <= 0f) return;
            // Window [U,U+UL)×[V,V+VL) as is, from UNROUNDED U,V (UV is not adjusted for edge snapping),
            // in fractions of the logical size.
            float u0 = u / size.W, u1 = (u + ul) / size.W, v0 = v / size.H, v1 = (v + vl) / size.H;
            bool masked = (pf & PF_Masked) != 0;
            HudBlend blend;
            bool clip = false;
            if ((pf & PF_Translucent) != 0)
                blend = HudBlend.Translucent;                 // ParticleAdd, vertex colour = tint
            else if ((pf & PF_Modulated) != 0)
            {
                blend = HudBlend.Modulated;                   // SpriteModulate, vertex colour white
                clip = masked;
                r = g = b = 1f;
            }
            else
                blend = masked ? HudBlend.AlphaBlend : HudBlend.Opaque;   // CanvasTile
            _quads.Add(new HudQuad(x0, y0, x1, y1, u0, v0, u1, v1, r, g, b, 1f, blend, clip, tex, source, cmd));
        }

        // FillBlack: CanvasTile without blending, white texture, black colour.
        void FillBlack(int x, int y, int w, int h)
        {
            if (w <= 0 || h <= 0) return;   // empty rectangle is not drawn
            _quads.Add(new HudQuad(x, y, x + w, y + h, 0f, 0f, 1f, 1f, 0f, 0f, 0f, 1f,
                HudBlend.Opaque, false, null, HudQuadSource.Fill, -1));
        }

        (int W, int H)? Size(string path)
        {
            (int W, int H)? s = string.IsNullOrEmpty(path) ? null : _textureSize(path);
            if (s == null)
            {
                MissingTextures.Add(path ?? "<null>");
                return null;
            }
            if (s.Value.W <= 0 || s.Value.H <= 0)
                throw new InvalidOperationException($"HUD: texture '{path}' of size {s.Value.W}×{s.Value.H} — UV window undefined");
            return s;
        }

        // Font lookup: empty/"None" means no font; key is the name leaf after the last dot;
        // cache is case-insensitive, misses are cached too.
        string[] Font(string name)
        {
            if (string.IsNullOrEmpty(name) || name == "None") return null;
            int dot = name.LastIndexOf('.');
            string key = dot >= 0 ? name.Substring(dot + 1) : name;
            if (!_fonts.TryGetValue(key, out string[] pages))
            {
                pages = _fontPages(key);
                _fonts[key] = pages;
                if (pages == null) MissingFonts.Add(key);
            }
            return pages;
        }
    }
}
