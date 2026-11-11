// Hud tests. Every expectation is computed BY HAND from the reference rule (file:line of the former
// Unity frontend in the comment), not from port output. Exit code: 0 — all checks passed,
// 1 — failures (each named by a FAIL line).
using HP1.UEScript;
using OpenHP1.Hud;

static class Program
{
    static int _fail, _pass;

    // Coronas are 3D-world tiles drawn through the HUD pass: bilinear as D3DDrv DrawTile (MAG/MINFILTER LINEAR at
    // 0x1000ae9b/0x1000aeaa), point only for PF_NoSmooth; UI quads stay nearest.
    static void CoronaSampling()
    {
        var q = ScreenEffects.CoronaQuad(0, 0, 10, 10, 0, 0, 1, 1, 1, 1, 1, "GenFX.LensFlar.Dot_A", noSmooth: false);
        Check("corona-bilinear", q.Smooth && q.Blend == HudBlend.Translucent, q.ToString());
        Check("corona-nosmooth-point", !ScreenEffects.CoronaQuad(0, 0, 10, 10, 0, 0, 1, 1, 1, 1, 1, "x", noSmooth: true).Smooth);
        Check("ui-quad-nearest", !new HudQuad(0, 0, 1, 1, 0, 0, 1, 1, 1, 1, 1, 1, HudBlend.Opaque, false, null, HudQuadSource.Fill, -1).Smooth);
    }

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; return; }
        _fail++;
        Console.WriteLine($"FAIL {name}{(detail.Length > 0 ? ": " + detail : "")}");
    }

    static void Eq(string name, float expected, float actual, float tol = 0f)
        => Check(name, MathF.Abs(expected - actual) <= tol, $"expected {expected}, got {actual}");

    static void Rect(string name, HudQuad q, float x0, float y0, float x1, float y1)
        => Check(name, q.X0 == x0 && q.Y0 == y0 && q.X1 == x1 && q.Y1 == y1,
            $"expected [{x0},{y0}..{x1},{y1}], got [{q.X0},{q.Y0}..{q.X1},{q.Y1}]");

    static void Uv(string name, HudQuad q, float u0, float v0, float u1, float v1)
        => Check(name, MathF.Abs(q.U0 - u0) < 1e-6f && MathF.Abs(q.V0 - v0) < 1e-6f
                       && MathF.Abs(q.U1 - u1) < 1e-6f && MathF.Abs(q.V1 - v1) < 1e-6f,
            $"expected uv[{u0},{v0}..{u1},{v1}], got uv[{q.U0},{q.V0}..{q.U1},{q.V1}]");

    // ── synthetic data ──────────────────────────────────────────────────────
    static readonly Dictionary<string, (int, int)> Sizes = new()
    {
        ["HUD.Icons.Bolt"] = (64, 32),
        ["HUD.Icons.Bar"] = (128, 16),
        ["HPFonts.BigFont_Page0"] = (256, 128),
        ["HPFonts.BigFont_Page1"] = (128, 64),
    };
    static readonly List<string> FontRequests = new();

    static HudBatcher NewBatcher()
    {
        FontRequests.Clear();
        return new HudBatcher(
            p => Sizes.TryGetValue(p, out var s) ? s : null,
            key =>
            {
                FontRequests.Add(key);
                return key.Equals("BigFont", StringComparison.OrdinalIgnoreCase)
                    ? new[] { "HPFonts.BigFont_Page0", "HPFonts.BigFont_Page1" } : null;
            });
    }

    static HudCommand Tile(float x, float y, float xl, float yl, float u, float v, float ul, float vl,
                           string tex = "HUD.Icons.Bolt", int pf = 0, float r = 255, float g = 255, float b = 255)
        => new HudCommand(HudKind.Tile, x, y, xl, yl, u, v, ul, vl, tex, null, null,
            new UEVector(r, g, b), 1, false, false, null, pf);

    static HudCommand Text(string font, HudGlyph[] glyphs, int pf = 0x1000802, float r = 255, float g = 255, float b = 255)
        => new HudCommand(HudKind.Text, 0, 0, 0, 0, 0, 0, 0, 0, null, "txt", font,
            new UEVector(r, g, b), 1, false, false, glyphs, pf);

    // scale: the interface scale Core built the frame's canvas with (WorldFrame.CanvasScale; Standard step at 1080 = 2).
    static WorldFrame Frame(IReadOnlyList<HudCommand> hud, bool drawWorld = true, ViewFlashState vf = default, int scale = 1)
        => new WorldFrame(1, "t", Array.Empty<ActorSnapshot>(), default, Array.Empty<AudioCommand>(), default,
            hud, Array.Empty<ParticleInstance>(), 0, drawWorld: drawWorld, viewFlash: vf, canvasScale: scale);

    // dotnet run              — port checks (CPU);
    // dotnet run -- --gpu      — plus running hud.hlsl on SDL_GPU (GpuTest.cs); device
    //                            unavailable -> code 2 (REFUSED), not success.
    static int Main(string[] args)
    {
        ScaleTests();
        TileGeometry1080();
        TileGeometry720();
        AdjacentTiles();
        DegenerateTiles();
        BlendModes();
        FrameOrder();
        GlyphsAndFonts();
        LevelInfoScreen();
        FlashTests();
        CoronaSampling();
        GammaTests();
        BlendFactorTable();

        Console.WriteLine($"HudTests: {_pass} checks passed, {_fail} failed");
        int rc = _fail == 0 ? 0 : 1;
        if (args.Contains("--gpu"))
        {
            int g = GpuTest.Run();
            if (g == 2) return 2;
            if (g != 0) rc = 1;
        }
        return rc;
    }

    // The scale is the frame's (WorldFrame.CanvasScale: Core built the canvas with it before the tick; the formula is
    // Core's UEConfig.UIScaleFor, SettingsTests write-ui-scale-steps). A 720 screen whose frame was built at scale 2 (the
    // Interface Size step changed after the canvas was made) draws at 2 — never re-derived from the screen height.
    static void ScaleTests()
    {
        var sb = NewBatcher();
        var sq = sb.Build(Frame(new[] { Tile(10, 20, 30, 10, 0, 0, 64, 32) }, scale: 2), 1280, 720, 0);
        Check("scale-from-frame", sb.Scale == 2 && sq.Count == 1, $"scale {sb.Scale}, quads {sq.Count}");
        if (sq.Count == 1) Rect("scale-from-frame-rect", sq[0], 20, 40, 80, 60);

        // HP1ConsoleRenderer.MenuRegionWidth (:46-47): menu open and W·3 > H·4 -> H·4/3.
        // 1920×1080: 5760 > 4320 -> 1440; 1440×1080 (exactly 4:3): 4320 > 4320 false -> 0;
        // 1280×1024 (narrower than 4:3) -> 0; menu closed -> 0; 2560×1080: 1440.
        Check("region-1080p", HudBatcher.ConsoleRegionWidth(true, 1920, 1080) == 1440, $"{HudBatcher.ConsoleRegionWidth(true, 1920, 1080)}");
        Check("region-exact-4x3", HudBatcher.ConsoleRegionWidth(true, 1440, 1080) == 0, $"{HudBatcher.ConsoleRegionWidth(true, 1440, 1080)}");
        Check("region-narrow", HudBatcher.ConsoleRegionWidth(true, 1280, 1024) == 0, $"{HudBatcher.ConsoleRegionWidth(true, 1280, 1024)}");
        Check("region-menu-closed", HudBatcher.ConsoleRegionWidth(false, 1920, 1080) == 0, $"{HudBatcher.ConsoleRegionWidth(false, 1920, 1080)}");
        Check("region-ultrawide", HudBatcher.ConsoleRegionWidth(true, 2560, 1080) == 1440, $"{HudBatcher.ConsoleRegionWidth(true, 2560, 1080)}");
    }

    // 1920×1080 -> scale 2. Tile X=10.25 Y=20.5 XL=30.5 YL=11.25:
    //   x=20.5 y=41 xl=61 yl=22.5 (HP1HudRender.cs:207);
    //   x0=Round(20.5)=20 (to even), y0=41, x1=Round(81.5)=82, y1=Round(63.5)=64 (:215-216).
    // Window U=8 V=4 UL=16 VL=8 on a 64×32 texture: u 8/64..24/64, v 4/32..12/32 (top-left).
    // Colour (255,128,0) -> (1, 128/255, 0), alpha 1 (:51); PolyFlags 0 -> opaque (:224-225).
    static void TileGeometry1080()
    {
        var hb = NewBatcher();
        var q = hb.Build(Frame(new[] { Tile(10.25f, 20.5f, 30.5f, 11.25f, 8, 4, 16, 8, r: 255, g: 128, b: 0) }, scale: 2), 1920, 1080, 0);
        Check("1080-count", q.Count == 1, $"quads {q.Count}");
        if (q.Count != 1) return;
        Check("1080-scale", hb.Scale == 2, $"{hb.Scale}");
        Rect("1080-rect", q[0], 20, 41, 82, 64);
        Uv("1080-uv", q[0], 0.125f, 0.125f, 0.375f, 0.375f);
        Eq("1080-r", 1f, q[0].R); Eq("1080-g", 128f / 255f, q[0].G, 1e-7f); Eq("1080-b", 0f, q[0].B); Eq("1080-a", 1f, q[0].A);
        Check("1080-blend", q[0].Blend == HudBlend.Opaque && !q[0].AlphaClip, q[0].ToString());
        Check("1080-tex", q[0].Texture == "HUD.Icons.Bolt" && q[0].Source == HudQuadSource.Tile && q[0].Command == 0, q[0].ToString());
    }

    // 1280×720 -> scale 1: x0=Round(10.25)=10, y0=Round(20.5)=20 (to even),
    // x1=Round(40.75)=41, y1=Round(31.75)=32.
    static void TileGeometry720()
    {
        var hb = NewBatcher();
        var q = hb.Build(Frame(new[] { Tile(10.25f, 20.5f, 30.5f, 11.25f, 8, 4, 16, 8) }), 1280, 720, 0);
        Check("720-count", q.Count == 1, $"quads {q.Count}");
        if (q.Count != 1) return;
        Check("720-scale", hb.Scale == 1, $"{hb.Scale}");
        Rect("720-rect", q[0], 10, 20, 41, 32);
        // 21.5 -> 22 (to even), 22.5 -> 22: distinguishes round-to-even from away-from-zero.
        q = hb.Build(Frame(new[] { Tile(21.5f, 22.5f, 10f, 10f, 0, 0, 64, 32) }), 1280, 720, 0);
        Rect("720-to-even", q[0], 22, 22, 32, 32);
    }

    // Adjacent tiles share the edge: A X=10.6 XL=99.8 -> x0=11, x1=Round(110.4)=110;
    // B X=110.4 -> x0=110 (HP1HudRender.cs:208-217, the right edge is Round(x+xl), not x0+Round(xl)=111).
    static void AdjacentTiles()
    {
        var hb = NewBatcher();
        var q = hb.Build(Frame(new[] { Tile(10.6f, 0, 99.8f, 10, 0, 0, 64, 32), Tile(110.4f, 0, 50, 10, 0, 0, 64, 32) }), 640, 480, 0);
        Check("adjacent-count", q.Count == 2, $"quads {q.Count}");
        if (q.Count != 2) return;
        Rect("adjacent-A", q[0], 11, 0, 110, 10);
        Check("adjacent-shared-edge", q[0].X1 == q[1].X0, $"A.X1={q[0].X1} B.X0={q[1].X0}");
    }

    // XL<=0 before scaling — refused BEFORE the texture (Calls, but not Tiles, :57); a width collapsed
    // by snapping (X=10.1, XL=0.3 -> 10..Round(10.4)=10) is not drawn (:218).
    static void DegenerateTiles()
    {
        var hb = NewBatcher();
        var q = hb.Build(Frame(new[] { Tile(5, 5, 0, 10, 0, 0, 1, 1), Tile(5, 5, 10, -1, 0, 0, 1, 1),
                                       Tile(10.1f, 0, 0.3f, 10, 0, 0, 1, 1), Tile(0, 0, 4, 4, 0, 0, 1, 1, tex: "No.Such.Tex") }), 640, 480, 0);
        Check("degenerate-none-drawn", q.Count == 0, $"quads {q.Count}");
        Check("degenerate-counters", hb.Calls == 4 && hb.Tiles == 2 && hb.TexHit == 1 && hb.TexMiss == 1,
            $"calls={hb.Calls} tiles={hb.Tiles} hit={hb.TexHit} miss={hb.TexMiss}");
        Check("missing-texture-named", hb.MissingTextures.Contains("No.Such.Tex"), string.Join(",", hb.MissingTextures));
    }

    // Mode by PolyFlags (HP1HudRender.cs:219-225): 4 — additive with tint (before 0x40);
    // else 0x40 — modulation with white colour, alpha test with 2; else CanvasTile:
    // with 2 — SrcAlpha/OneMinusSrcAlpha, else opaque.
    static void BlendModes()
    {
        (int pf, HudBlend blend, bool clip, bool white)[] cases =
        {
            (0x0, HudBlend.Opaque, false, false),
            (0x2, HudBlend.AlphaBlend, false, false),
            (0x4, HudBlend.Translucent, false, false),
            (0x6, HudBlend.Translucent, false, false),
            (0x40, HudBlend.Modulated, false, true),
            (0x42, HudBlend.Modulated, true, true),
            (0x44, HudBlend.Translucent, false, false),
            (0x1000802, HudBlend.AlphaBlend, false, false),
        };
        foreach (var (pf, blend, clip, white) in cases)
        {
            var hb = NewBatcher();
            var q = hb.Build(Frame(new[] { Tile(0, 0, 8, 8, 0, 0, 64, 32, pf: pf, r: 51, g: 102, b: 204) }), 640, 480, 0);
            string n = $"blend-0x{pf:x}";
            Check(n + "-count", q.Count == 1, $"quads {q.Count}");
            if (q.Count != 1) continue;
            Check(n + "-mode", q[0].Blend == blend, $"expected {blend}, got {q[0].Blend}");
            Check(n + "-clip", q[0].AlphaClip == clip, $"expected {clip}, got {q[0].AlphaClip}");
            // 51/255 = 0.2, 102/255 = 0.4, 204/255 = 0.8; modulation is white (:223).
            float er = white ? 1f : 0.2f, eg = white ? 1f : 0.4f, eb = white ? 1f : 0.8f;
            Check(n + "-color", MathF.Abs(q[0].R - er) < 1e-6f && MathF.Abs(q[0].G - eg) < 1e-6f && MathF.Abs(q[0].B - eb) < 1e-6f,
                $"expected ({er},{eg},{eb}), got ({q[0].R},{q[0].G},{q[0].B})");
        }
    }

    // Frame order HP1HudRenderer.OnGUI (:312-323): fill when !DrawWorld, commands, side bars.
    // 1920×1080 (scale 2): Core canvas 960×540, menu region 1440/2 = 720,
    // ConsoleRegionOffsetX = (960-720)/2 = 120 -> 240 on screen: side bars [0..240] and [1680..1920].
    static void FrameOrder()
    {
        var hb = NewBatcher();
        var cmds = new[]
        {
            Tile(0, 0, 10, 10, 0, 0, 64, 32),
            Text("HPFonts.BigFont", new[] { new HudGlyph(0, 20, 0, 8, 8, 0, 0, 8, 8) }),
            Tile(30, 0, 10, 10, 0, 0, 128, 16, tex: "HUD.Icons.Bar"),
        };
        var q = hb.Build(Frame(cmds, drawWorld: false, scale: 2), 1920, 1080, 120);
        string seq = string.Join(" ", q.Select(x => x.Source + "#" + x.Command));
        Check("order-sequence", seq == "Fill#-1 Tile#0 Glyph#1 Tile#2 Fill#-1 Fill#-1", seq);
        if (q.Count != 6) return;
        Rect("order-black", q[0], 0, 0, 1920, 1080);
        Check("order-black-style", q[0].Texture == null && q[0].Blend == HudBlend.Opaque
            && q[0].R == 0 && q[0].G == 0 && q[0].B == 0 && q[0].A == 1, q[0].ToString());
        Rect("order-bar-left", q[4], 0, 0, 240, 1080);
        Rect("order-bar-right", q[5], 1680, 0, 1920, 1080);
        Check("order-bar-style", q[4].Texture == null && q[5].Blend == HudBlend.Opaque && q[5].R == 0, q[5].ToString());

        // The world is drawn and the region is inactive: no fill, no side bars.
        q = hb.Build(Frame(cmds, drawWorld: true, scale: 2), 1920, 1080, 0);
        seq = string.Join(" ", q.Select(x => x.Source + "#" + x.Command));
        Check("order-world", seq == "Tile#0 Glyph#1 Tile#2", seq);

        // No frame: no commands, the side bars remain (drawn by region, not by frame).
        q = hb.Build(null, 1280, 720, 80);
        seq = string.Join(" ", q.Select(x => $"{x.Source}[{x.X0}..{x.X1}]"));
        Check("order-no-frame", seq == "Fill[0..80] Fill[1200..1280]", seq);
    }

    // Glyphs (HP1HudRender.cs:81-95; HP1FontBank.Get :225-234). Font "HPFonts.BigFont" -> key
    // "BigFont"; page 1 is 128×64. Glyph X=100.5 Y=7 XL=9 YL=12 U=16 V=32 UL=9 VL=12 at 1080p
    // (scale 2): x0=Round(201)=201, y0=14, x1=Round(219)=219, y1=38; uv 16/128..25/128, 32/64..44/64.
    static void GlyphsAndFonts()
    {
        var hb = NewBatcher();
        var glyphs = new[]
        {
            new HudGlyph(1, 100.5f, 7, 9, 12, 16, 32, 9, 12),
            new HudGlyph(0, 0, 0, 4, 4, 0, 0, 4, 4),
            new HudGlyph(2, 0, 0, 4, 4, 0, 0, 4, 4),     // no page 2 -> skipped
        };
        var q = hb.Build(Frame(new[] { Text("HPFonts.BigFont", glyphs, r: 255, g: 0, b: 51) }, scale: 2), 1920, 1080, 0);
        Check("glyph-count", q.Count == 2, $"quads {q.Count}");
        if (q.Count >= 1)
        {
            Rect("glyph-rect", q[0], 201, 14, 219, 38);
            Uv("glyph-uv", q[0], 0.125f, 0.5f, 25f / 128f, 44f / 64f);
            Check("glyph-page-tex", q[0].Texture == "HPFonts.BigFont_Page1", q[0].Texture);
            // Text flags 0x1000802 -> CanvasTile with alpha blending, tint (1, 0, 0.2).
            Check("glyph-blend", q[0].Blend == HudBlend.AlphaBlend && MathF.Abs(q[0].B - 0.2f) < 1e-6f && q[0].G == 0f, q[0].ToString());
        }
        if (q.Count >= 2) Check("glyph-second-page0", q[1].Texture == "HPFonts.BigFont_Page0", q[1].Texture);
        Check("glyph-page-miss", hb.GlyphPageMiss == 1 && hb.Texts == 1, $"pageMiss={hb.GlyphPageMiss} texts={hb.Texts}");
        Check("font-key-leaf", FontRequests.Count == 1 && FontRequests[0] == "BigFont", string.Join(",", FontRequests));

        // Case-insensitive cache: another package and case of the same leaf -> no new request.
        hb.Build(Frame(new[] { Text("Other.BIGFONT", glyphs) }), 640, 480, 0);
        Check("font-cache-ci", FontRequests.Count == 1, string.Join(",", FontRequests));

        // Text without glyphs breaks the contract: the port has no OS fonts (English only, D-39).
        string noGlyphs = "";
        try { hb.Build(Frame(new[] { Text("HPFonts.BigFont", null) }), 640, 480, 0); }
        catch (InvalidOperationException e) { noGlyphs = e.Message; }
        Check("text-without-glyphs-refused", noGlyphs.Contains("D-39"), noGlyphs);

        // Empty glyphs, "None", unknown font (twice — the miss is cached).
        q = hb.Build(Frame(new[]
        {
            Text("HPFonts.BigFont", Array.Empty<HudGlyph>()),
            Text("None", glyphs), Text("X.Missing", glyphs), Text("Y.missing", glyphs),
        }), 640, 480, 0);
        Check("text-skips-draw-nothing", q.Count == 0, $"quads {q.Count}");
        Check("text-fontmiss-counted", hb.FontMiss == 3, $"{hb.FontMiss}");
        Check("font-miss-cached", FontRequests.Count == 2 && FontRequests[1] == "Missing", string.Join(",", FontRequests));

        // Modulated text: white vertex colour, alpha test (0x40|0x2).
        q = hb.Build(Frame(new[] { Text("HPFonts.BigFont", new[] { glyphs[1] }, pf: 0x1000842, r: 10, g: 20, b: 30) }), 640, 480, 0);
        Check("glyph-modulated", q.Count == 1 && q[0].Blend == HudBlend.Modulated && q[0].AlphaClip && q[0].R == 1f,
            q.Count == 1 ? q[0].ToString() : $"quads {q.Count}");
    }

    // HP1TravelFrameHold.OnGUI (:197-203): black screen, then the loading-screen commands.
    static void LevelInfoScreen()
    {
        var hb = NewBatcher();
        var q = hb.BuildLevelInfoScreen(new[] { Tile(100, 50, 20, 10, 0, 0, 64, 32) }, 1920, 1080, 2);
        string seq = string.Join(" ", q.Select(x => x.Source.ToString()));
        Check("levelinfo-order", seq == "Fill Tile", seq);
        if (q.Count == 2)
        {
            Rect("levelinfo-black", q[0], 0, 0, 1920, 1080);
            Rect("levelinfo-tile-scaled", q[1], 200, 100, 240, 120);
        }
    }

    // HP1ScreenFade.Update (:58-62) and Identity (:50): scale = clamp01(W·0.5),
    // rgb = clamp01, alpha = min(2·scale, 1); skipped when rgb = 0 and alpha ≥ 1 (after clamping).
    static void FlashTests()
    {
        Check("flash-invalid", !ScreenEffects.ViewFlash(default).Draw);
        Check("flash-identity-W1", !ScreenEffects.ViewFlash(new ViewFlashState(0, 0, 0, 1)).Draw);
        Check("flash-identity-W2", !ScreenEffects.ViewFlash(new ViewFlashState(0, 0, 0, 2)).Draw);
        // Negative rgb is clamped to 0 BEFORE the identity check -> not drawn.
        Check("flash-identity-after-clamp", !ScreenEffects.ViewFlash(new ViewFlashState(-0.1f, 0, -2f, 1)).Draw);

        // Fade: W=0.5 -> scale 0.25 -> alpha 0.5; rgb 0.
        var o = ScreenEffects.ViewFlash(new ViewFlashState(0, 0, 0, 0.5f));
        Check("fade-draw", o.Draw);
        Eq("fade-alpha", 0.5f, o.A);
        // Flash: (1.5, -0.2, 0.25, W=3) -> rgb (1, 0, 0.25), scale clamp01(1.5)=1 -> alpha 1; drawn (rgb≠0).
        o = ScreenEffects.ViewFlash(new ViewFlashState(1.5f, -0.2f, 0.25f, 3f));
        Check("flash-draw", o.Draw);
        Eq("flash-r", 1f, o.R); Eq("flash-g", 0f, o.G); Eq("flash-b", 0.25f, o.B); Eq("flash-alpha", 1f, o.A);
        // W=-1 -> scale 0 -> alpha 0 (full fade to colour).
        o = ScreenEffects.ViewFlash(new ViewFlashState(0.2f, 0.4f, 0.6f, -1f));
        Eq("flash-neg-w-alpha", 0f, o.A);
        // W=1.2 -> scale 0.6 -> 2·0.6 = 1.2 -> min 1.
        o = ScreenEffects.ViewFlash(new ViewFlashState(0.1f, 0, 0, 1.2f));
        Eq("flash-alpha-cap", 1f, o.A);
        // W=0.8 -> scale 0.4 -> alpha 0.8.
        o = ScreenEffects.ViewFlash(new ViewFlashState(0.1f, 0, 0, 0.8f));
        Eq("flash-alpha-0.8", 0.8f, o.A, 1e-6f);

        var quad = ScreenEffects.FlashQuad(new ViewFlashState(0.5f, 0, 0, 1f), 1920, 1080);
        Check("flash-quad", quad.HasValue && quad.Value.Blend == HudBlend.Flash && quad.Value.Texture == null
            && quad.Value.X0 == 0 && quad.Value.Y0 == 0 && quad.Value.X1 == 1920 && quad.Value.Y1 == 1080
            && quad.Value.R == 0.5f && quad.Value.A == 1f, quad?.ToString() ?? "null");
        Check("flash-quad-identity-null", ScreenEffects.FlashQuad(new ViewFlashState(0, 0, 0, 1f), 1920, 1080) == null);

        // The flash's place in the 2D frame — UGameEngine::Draw (Engine.dll.c:117947-117956): DrawWorld (coronas) ->
        // RenDev +0xb0 EndFlash -> PostRender (HUD). Coronas under the flash, HUD above it (render audit D-4).
        var corona = new HudQuad(1, 1, 2, 2, 0, 0, 1, 1, 1, 1, 1, 1, HudBlend.Translucent, false, "C", HudQuadSource.Tile, -1);
        var hudq = new HudQuad(3, 3, 4, 4, 0, 0, 1, 1, 1, 1, 1, 1, HudBlend.Opaque, false, "H", HudQuadSource.Tile, -1);
        var dst = new List<HudQuad> { hudq };
        ScreenEffects.Overlay(dst, new[] { corona }, quad, new[] { hudq, hudq });
        Check("flash-under-hud", dst.Count == 4 && dst[0].Texture == "C" && dst[1].Blend == HudBlend.Flash && dst[2].Texture == "H" && dst[3].Texture == "H",
            string.Join(",", dst.Select(x => x.Blend + ":" + x.Texture)));
        ScreenEffects.Overlay(dst, new[] { corona }, null, new[] { hudq });
        Check("flash-none", dst.Count == 2 && dst[0].Texture == "C" && dst[1].Texture == "H", dst.Count.ToString());
    }

    // D3DDrv UD3DRenderDevice::Flush (disasm 0x10002f42..0x10002fd6): e = 1/(B·2.5), ramp
    // fistp(pow(i·(1/255), e)·65535); B ≤ 0 -> 1 (not seen in shipped); skipped when |e-1| < 1e-6. Values computed separately (python, double),
    // at indices whose fractional part is far from 0.5 (float32 does not change the rounding).
    static void GammaTests()
    {
        Eq("gamma-exp-0.5", 0.8f, ScreenEffects.GammaExponent(0.5f), 1e-6f);
        Eq("gamma-exp-0.2", 2f, ScreenEffects.GammaExponent(0.2f), 1e-6f);
        Eq("gamma-exp-0", 1f, ScreenEffects.GammaExponent(0f));
        Eq("gamma-exp-neg", 1f, ScreenEffects.GammaExponent(-1f));
        Check("gamma-retail-0.4-inactive", !ScreenEffects.GammaActive(0.4f));
        Check("gamma-0-inactive", !ScreenEffects.GammaActive(0f));
        Check("gamma-0.5-active", ScreenEffects.GammaActive(0.5f));

        ushort[] id = ScreenEffects.GammaRamp16(0.4f);
        bool identity = true;
        for (int i = 0; i < 256; i++) identity &= id[i] == i * 257;
        Check("gamma-ramp-identity", identity, $"ramp[128]={id[128]} (expected {128 * 257})");

        (float b, int i, int v)[] pins =
        {
            (0.5f, 2, 1355), (0.5f, 64, 21686), (0.5f, 128, 37758), (0.5f, 200, 53959), (0.5f, 254, 65329), (0.5f, 255, 65535),
            (0.2f, 2, 4), (0.2f, 64, 4128), (0.2f, 200, 40314),
            (0.8f, 64, 32832), (0.8f, 128, 46431), (0.8f, 200, 58039), (0.8f, 0, 0),
        };
        foreach (var (b, i, v) in pins)
        {
            ushort[] r = ScreenEffects.GammaRamp16(b);
            Check($"gamma-ramp-B{b}-i{i}", r[i] == v, $"expected {v}, got {r[i]}");
        }
    }

    // Factor pairs come from the reference shaders' `Blend`: CanvasTile (_SrcBlend/_DstBlend,
    // HP1HudRender.cs:151-152), ParticleAdd `One OneMinusSrcColor`, SpriteModulate
    // `DstColor SrcColor`, ViewFlash `One SrcAlpha`.
    static void BlendFactorTable()
    {
        void F(HudBlend b, HudBlendFactor s, HudBlendFactor d, int mode)
        {
            var f = HudBlendStates.Factors(b);
            Check($"factors-{b}", f.Src == s && f.Dst == d, $"expected {s}/{d}, got {f.Src}/{f.Dst}");
            Check($"shadermode-{b}", HudBlendStates.ShaderMode(b) == mode, $"expected {mode}, got {HudBlendStates.ShaderMode(b)}");
        }
        F(HudBlend.Opaque, HudBlendFactor.One, HudBlendFactor.Zero, 0);
        F(HudBlend.AlphaBlend, HudBlendFactor.SrcAlpha, HudBlendFactor.OneMinusSrcAlpha, 0);
        F(HudBlend.Translucent, HudBlendFactor.One, HudBlendFactor.OneMinusSrcColor, 1);
        F(HudBlend.Modulated, HudBlendFactor.DstColor, HudBlendFactor.SrcColor, 2);
        F(HudBlend.Flash, HudBlendFactor.One, HudBlendFactor.SrcAlpha, 0);
    }
}
