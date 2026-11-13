// Tests of the sprite subsystem on synthetic frames.
//
// Expected values are computed BY HAND from the reference rules (numbers in the comment
// next to each check), not taken from port output. Input goes through the builders' Build(),
// i.e. the same route the engine calls.
//
// Exit code: 0 = all checks passed; 1 = a failure (names in the output).
using HP1.UEScript;
using HPEngine.Sprites;

static class Program
{
    static int _fail, _pass;
    static readonly List<string> _failed = new();

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; return; }
        _fail++; _failed.Add(name);
        Console.WriteLine($"FAIL {name} {detail}");
    }

    static bool Near(float a, float b, float tol = 1e-3f) => MathF.Abs(a - b) <= tol;

    static void CheckV(string name, SpriteVertex v, float x, float y, float z, float tol = 1e-3f)
        => Check(name, Near(v.X, x, tol) && Near(v.Y, y, tol) && Near(v.Z, z, tol),
            $"got=({v.X:R},{v.Y:R},{v.Z:R}) want=({x},{y},{z})");

    static void CheckUV(string name, SpriteVertex v, float u, float w)
        => Check(name, Near(v.U, u, 1e-6f) && Near(v.V, w, 1e-6f), $"got=({v.U:R},{v.V:R}) want=({u},{w})");

    static void CheckRGB(string name, SpriteVertex v, float r, float g, float b, float tol = 1e-4f)
        => Check(name, Near(v.R, r, tol) && Near(v.G, g, tol) && Near(v.B, b, tol) && v.A == 1f,
            $"got=({v.R:R},{v.G:R},{v.B:R},{v.A:R}) want=({r},{g},{b},1)");

    // ── Synthetic frame ───────────────────────────────────────────────────
    static ActorSnapshot Actor(int id, string cls = "Test", UEVector loc = default, int drawType = 1,
        string tex = null, float drawScale = 1f, int style = 1, float scaleGlow = 1f, float lifeSpan = 0f,
        bool destroyed = false, bool hidden = false, string skin = null, LightState light = default,
        ParticleState emitter = default, int exportIndex = 0, int leaf = -1, float opacity = 1f, float spriteProjForward = 0f,
        bool latePass = false)
        => new ActorSnapshot(id, -1, -1, "A" + id, cls, true, loc, default, default, default, 0, 0, leaf,
            "-", "-", "-", "-", 0f, null, 0f, 0f, 0f, false, 0f, 0f, 0f, lifeSpan, Array.Empty<string>(), destroyed,
            drawType, null, tex, drawScale, style, hidden, false, false, 0, scaleGlow, skin, null, light, default, default, emitter,
            exportIndex, opacity: opacity, spriteProjForward: spriteProjForward, latePass: latePass);

    static ParticleState Emitter(int prim)
    {
        var z = new FloatRange(0f, 0f);
        return new ParticleState(true, z, z, prim, Array.Empty<string>());
    }

    static CameraSnapshot Cam(UEVector loc, int pitch, int yaw, float fov = 90f, int viewActor = 0)
        => new CameraSnapshot(true, viewActor, loc, new UERotator(pitch, yaw, 0), fov, true);

    // Sprites drawn by the Core renderer (WorldFrame.SpriteActors): by default all non-hidden actors of the frame
    // (Core creates no FDynamicSprite for a hidden actor, UERenderVisibility.DynamicsActorPasses).
    static WorldFrame Frame(CameraSnapshot cam, IReadOnlyList<ActorSnapshot> actors,
        IReadOnlyList<ParticleInstance> particles = null, IReadOnlyList<DecalInstance> decals = null, IReadOnlyList<int> sprites = null,
        IReadOnlyList<ViewFrame> views = null, ActorViewSets actorViews = null)
        => new WorldFrame(1, "test", actors, cam, Array.Empty<AudioCommand>(), default, Array.Empty<HudCommand>(),
            particles ?? Array.Empty<ParticleInstance>(), 0, decals: decals,
            spriteActors: sprites ?? actors.Where(a => !a.Hidden).Select(a => a.ObjectId).ToArray(), views: views,
            // Core gives every drawn actor a frame membership; by default the master frame (−1).
            actorViews: actorViews ?? ActorViewSets.From(actors.ToDictionary(a => a.ObjectId, _ => new List<int> { -1 })));

    // Textures: path -> (USize, VSize, bMasked, ice). An unknown path does not resolve.
    static readonly Dictionary<string, (int u, int v, bool masked, bool ice)> Tex = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Pkg.T64x32"] = (64, 32, false, false),
        ["Pkg.T64x64"] = (64, 64, false, false),
        ["Pkg.Masked"] = (16, 16, true, false),
        ["Pkg.TexTransl"] = (16, 16, false, false), ["Pkg.TexMod"] = (16, 16, false, false),
        ["Pkg.Ice"] = (32, 32, false, true),
        ["Pkg.F0"] = (8, 8, false, false), ["Pkg.F1"] = (8, 8, false, false),
        ["Pkg.F2"] = (8, 8, false, false), ["Pkg.F3"] = (8, 8, false, false),
        ["Pkg.T10x20"] = (10, 20, false, false),
        ["Pkg.ShadowT"] = (32, 32, false, false),
    };
    static bool Size(string p, out int u, out int v)
    {
        if (p != null && Tex.TryGetValue(p, out var t)) { u = t.u; v = t.v; return true; }
        u = v = 0; return false;
    }
    // AnimNext chain Pkg.F0 -> F1 -> F2 -> F3 (end).
    static readonly string[] Chain = { "Pkg.F0", "Pkg.F1", "Pkg.F2", "Pkg.F3" };
    static int ChainLength(string head) => head == "Pkg.F0" ? 4 : 1;
    static string ChainAt(string head, int k) => head == "Pkg.F0" ? Chain[k] : head;

    static bool Visible(WorldFrame f, in ActorSnapshot a) => !a.Hidden;

    // Texture.PolyFlags: bMasked -> 0x2, bTransparent -> 0x4, bModulate -> 0x40 (UETextureMetrics.FlagBits).
    static int TexFlags(string p) => p == "Pkg.TexTransl" ? 0x4 : p == "Pkg.TexMod" ? 0x40 : Tex.TryGetValue(p, out var t) && t.masked ? 0x2 : 0;

    static SpriteBuilder NewSprites(Func<string, float> defLife = null)
        => new SpriteBuilder(Size, TexFlags, p => Tex.TryGetValue(p, out var t) && t.ice,
            ChainLength, ChainAt, defLife ?? (_ => 2f));

    static int Main()
    {
        Sprites();
        ProjForward();
        DrawOrder();
        CoreDrawOrder();
        AnimOnce();
        Particles();
        Liquid();
        Coronas();
        Decals();
        AnimNextFrames();
        MirrorViews();
        Depth();
        Console.WriteLine($"SpritesTests: pass={_pass} fail={_fail}");
        if (_fail > 0) { Console.WriteLine("FAILED: " + string.Join(",", _failed.Distinct())); return 1; }
        Console.WriteLine("OK");
        return 0;
    }

    // ── Sprite tile depth: SpriteProjForward ────────────────────────────────
    // Render.dll.c:38114-38118: Z = (Location − ViewOrigin)·ViewDir − SpriteProjForward;
    // the screen size uses the real Location. Camera at the origin, yaw 90°: forward (0,1,0),
    // right (−1,0,0), up (0,0,1). Actor (0,200,0), 64×32, DrawScale 1, P = 32:
    // depth 200, Z 168, k = 0.84; center (0,168,0), half-axes 32·0.84 = 26.88 and 16·0.84 = 13.44.
    // TL = (26.88,168,13.44), BR = (−26.88,168,−13.44); TL projection 26.88/168 = 32/200.
    static void ProjForward()
    {
        var sb = NewSprites();
        sb.Build(Frame(Cam(default, 0, 16384), new[] { Actor(1, loc: new UEVector(0, 200, 0), tex: "Pkg.T64x32", spriteProjForward: 32f) }));
        if (sb.Draws.Count == 1)
        {
            CheckV("sprite.projforward.TL", sb.Draws[0].Vertices[0], 26.88f, 168, 13.44f);
            CheckV("sprite.projforward.BR", sb.Draws[0].Vertices[2], -26.88f, 168, -13.44f);
        }
        else Check("sprite.projforward.count", false, sb.Report.ToString());
        // Closer than SpriteProjForward (depth 20 < 32): shipped outcome unknown; loud report, quad without the shift.
        sb.Build(Frame(Cam(default, 0, 16384), new[] { Actor(2, loc: new UEVector(0, 20, 0), tex: "Pkg.T64x32", spriteProjForward: 32f) }));
        Check("sprite.projforward.near", sb.Draws.Count == 1 && sb.Report.Get("projforward_z_not_positive") == 1
            && Near(sb.Draws[0].Vertices[0].Y, 20f), sb.Report.ToString());
    }

    // ── Child frames: sprites and particles set up with the frame's own Coords ──
    // FDynamicSprite::Setup (Render.dll 0x10b30e95..0x10b30ecd) and DrawParticleSystem (FUN_10b16a10) use the Frame they are
    // given. Live original, Lev_Tut3 camera M1 (−2900, −10740, 880; pitch −5825, yaw −12379; 800×600, FOV 90): the mirror
    // child (CHILD plane −0.37452,−0.49155,0.78620,348.0059 in master camera axes = world plane y = −10740 − 348.0059) draws
    // the CandleF tile of Light99 (−2676.925, −10698.34, 827.1101) at xy 349.7,77.2 size 18.9 -> centre (359.15, 86.65),
    // tile z 646.837 (depth 678.837 − SpriteProjForward 32). The mirror frame's M (frame world -> master world) is the
    // reflection in that plane. CandleF is 64×64 (extracted/Textures/HP_FX/Texture/CandleF.png).
    static readonly UEVector M1Eye = new(-2900, -10740, 880);
    const int M1Pitch = -5825, M1Yaw = -12379;

    // Master camera projection through the frontend's frame camera (HPEngine.Camera.From + Rows, the matrix the renderer
    // draws with), 800×600: screen x = (clip.x/w + 1)·400, y = (1 − clip.y/w)·300; w = depth along the view axis.
    static (float x, float y, float w) ProjectM1(UEVector p)
    {
        var rows = new float[16];
        HPEngine.Camera.From(Cam(M1Eye, M1Pitch, M1Yaw)).Rows(800, 600, rows);
        float Row(int i) => rows[i * 4] * p.x + rows[i * 4 + 1] * p.y + rows[i * 4 + 2] * p.z + rows[i * 4 + 3];
        float w = Row(3);
        return ((Row(0) / w + 1f) * 400f, (1f - Row(1) / w) * 300f, w);
    }

    // The original drawlog prints tiles to 0.1 px (xy = top-left, xlyl = size) and z to 0.001: centre within ±0.075,
    // size within ±0.05, z within ±0.005. The frame camera's axes come from SinTab (angles quantised to multiples of 4:
    // pitch −5828, yaw −12380, matching the original's CHILD parent_coords X = (0.92722, 0.37452, 0)); exact sin/cos of
    // −5825/−12379 put every tile ~0.15 px lower (Light99 centre (359.05, 86.82), z 646.935).
    static void CheckTile(string name, SpriteVertex tlv, SpriteVertex brv, Affine? m, float ox, float oy, float osize, float oz)
    {
        UEVector Pt(SpriteVertex v) { var q = new UEVector(v.X, v.Y, v.Z); return m is Affine a ? a.Apply(q) : q; }
        var tl = ProjectM1(Pt(tlv)); var br = ProjectM1(Pt(brv));
        float cx = (tl.x + br.x) / 2, cy = (tl.y + br.y) / 2, w = br.x - tl.x, h = br.y - tl.y;
        float ocx = ox + osize / 2, ocy = oy + osize / 2;
        Console.WriteLine($"{name}: TL ({tl.x:F2}, {tl.y:F2}) size {w:F2}x{h:F2} z {tl.w:F3}; original ({ox}, {oy}) {osize} z {oz}");
        Check(name + ".centre", Near(cx, ocx, 0.075f) && Near(cy, ocy, 0.075f), $"centre=({cx},{cy}) want=({ocx},{ocy})");
        Check(name + ".size", Near(w, osize, 0.05f) && Near(h, osize, 0.05f), $"size={w}x{h}");
        Check(name + ".z", Near(tl.w, oz, 0.005f), $"z={tl.w} want={oz}");
    }

    static void MirrorViews()
    {
        Affine m = Affine.Reflect(new UEVector(0, 1, 0), 10740f + 348.0059f);
        var cam = Cam(M1Eye, M1Pitch, M1Yaw);
        // The mirror frame's own Coords as Core's OccludeBsp makes them (MirrorByVector / MirrorByPlane of the master
        // Coords about the mirror plane): here the plane reflection M applied to the master frame — an input of the case.
        BrushCoords mc = UESceneNode.MasterCoords(cam.Location, cam.Rotation);
        UEVector L(UEVector v) => new(m.M00 * v.x + m.M01 * v.y + m.M02 * v.z, m.M10 * v.x + m.M11 * v.y + m.M12 * v.z, m.M20 * v.x + m.M21 * v.y + m.M22 * v.z);
        var childCoords = new BrushCoords { Origin = m.Apply(mc.Origin), X = L(mc.X), Y = L(mc.Y), Z = L(mc.Z) };
        var views = new[] { new ViewFrame(-1, ViewFrame.KindMirror, 1, m, default, 0f, true, Array.Empty<ViewSource>(), childCoords) };
        var inViews = ActorViewSets.From(new Dictionary<int, List<int>> { [99] = new() { 0 }, [98] = new() { -1, 0 } });
        var loc = new UEVector(-2676.925f, -10698.34f, 827.1101f);

        var sb = NewSprites();
        sb.Build(Frame(cam, new[] { Actor(99, cls: "Light", loc: loc, tex: "Pkg.T64x64", drawScale: 0.5f, style: 3, spriteProjForward: 32f) },
            views: views, actorViews: inViews));
        Check("mirror.sprite.count", sb.Draws.Count == 1 && sb.Draws[0].FrameView == 0, $"draws={sb.Draws.Count} report={sb.Report}");
        if (sb.Draws.Count == 1) CheckTile("mirror.sprite", sb.Draws[0].Vertices[0], sb.Draws[0].Vertices[2], m, 349.7f, 77.2f, 18.9f, 646.837f);

        // Master-frame tiles at the same camera (original drawlog M1, frame 0BF90008 depth 0): Light103/104/105.
        var master = new (int id, UEVector loc, float x, float y, float size, float z)[]
        {
            (103, new(-2684.557f, -10901.17f, 827.1101f), 621.2f, 133.0f, 57.4f, 191.167f),
            (104, new(-2649.162f, -10900.1f, 827.1101f), 668.2f, 129.3f, 54.8f, 201.566f),
            (105, new(-2667.161f, -10901.07f, 833.1101f), 647.6f, 119.6f, 56.8f, 193.433f),
        };
        foreach (var t in master)
        {
            sb.Build(Frame(cam, new[] { Actor(t.id, cls: "Light", loc: t.loc, tex: "Pkg.T64x64", drawScale: 0.5f, style: 3, spriteProjForward: 32f) }));
            if (sb.Draws.Count == 1) CheckTile($"master.sprite.Light{t.id}", sb.Draws[0].Vertices[0], sb.Draws[0].Vertices[2], null, t.x, t.y, t.size, t.z);
            else Check($"master.sprite.Light{t.id}.count", false, sb.Report.ToString());
        }

        // An actor drawn by the master and the mirror gets one tile per frame, in membership order.
        sb.Build(Frame(cam, new[] { Actor(98, loc: loc, tex: "Pkg.T64x64", drawScale: 0.5f) }, views: views, actorViews: inViews));
        Check("mirror.sprite.per_view", sb.Draws.Select(d => d.FrameView).SequenceEqual(new[] { -1, 0 }), string.Join(",", sb.Draws.Select(d => d.FrameView)));

        // Particle in the mirror: the billboard is built on the mirror frame's axes, so through M it faces the master camera
        // (corners differ from M(Pos) only along screen right/up) at full size: the 4×2 quad's TL is (−2, −1)·Persp/z off centre.
        var pb = new ParticleBuilder(Size, TexFlags);
        pb.Build(Frame(cam, new[] { Actor(99, emitter: Emitter(1)) }, new[] { P(99, loc) }, views: views, actorViews: inViews));
        Check("mirror.particle.count", pb.Draws.Count == 1 && pb.Draws[0].FrameView == 0, $"draws={pb.Draws.Count} report={pb.Report}");
        if (pb.Draws.Count == 1)
        {
            var v = pb.Draws[0].Vertices;
            var c = ProjectM1(m.Apply(loc));
            var tl = ProjectM1(m.Apply(new UEVector(v[0].X, v[0].Y, v[0].Z)));
            var tr = ProjectM1(m.Apply(new UEVector(v[1].X, v[1].Y, v[1].Z)));
            float k = 400f / 678.837f;   // Persp / depth of the reflected Pos (tile z 646.837 + SpriteProjForward 32)
            Console.WriteLine($"mirror.particle: TL ({tl.x:F3}, {tl.y:F3}) TR ({tr.x:F3}, {tr.y:F3}) centre ({c.x:F3}, {c.y:F3})");
            Check("mirror.particle.facing", Near(tr.x - tl.x, 4 * k, 0.02f) && Near(tr.y, tl.y, 0.02f)
                && Near(tl.x, c.x - 2 * k, 0.02f) && Near(tl.y, c.y - 1 * k, 0.02f), $"tl=({tl.x},{tl.y}) tr=({tr.x},{tr.y}) c=({c.x},{c.y})");
        }
    }

    // ── Batch order: opaque first, frame order within groups ───────────────
    // 40 batches, odd ones blended: expect 0,2,…,38, then 1,3,…,39.
    static void DrawOrder()
    {
        var items = Enumerable.Range(0, 40).ToList();
        SpriteMath.OpaqueFirst(items, x => (x & 1) == 1, new List<int>());
        var want = Enumerable.Range(0, 20).Select(k => 2 * k).Concat(Enumerable.Range(0, 20).Select(k => 2 * k + 1));
        Check("order.opaque_first_stable", items.SequenceEqual(want), string.Join(",", items));
    }

    // ── Actor draws in Core's DrawActorSprite order (WorldFrame.DrawOrder) ─
    // Draws (actor, view, blended?): built per kind (meshes, then sprites, then particles), plus a world draw (actor −9).
    // Core's order: master [5, 3, 7], view 0 [7, 5]. Expect the world draw first, then master 5a,5b,3,7, then view 0 7,5 —
    // blended or not, no draw moves past its actor's place, an actor's own draws keep their order.
    static void CoreDrawOrder()
    {
        var draws = new List<(int Actor, int View, string Tag)>
        {
            (3, -1, "3"), (5, -1, "5a"), (7, 0, "7v"), (-9, -1, "world"), (5, -1, "5b"), (7, -1, "7"), (5, 0, "5v"),
        };
        var f = new WorldFrame(1, "t", Array.Empty<ActorSnapshot>(), default, Array.Empty<AudioCommand>(), default,
            Array.Empty<HudCommand>(), Array.Empty<ParticleInstance>(), 0, drawOrder: new[] { new[] { 5, 3, 7 }, new[] { 7, 5 } });
        var got = new List<(int Actor, int View, string Tag)>();
        SpriteMath.InCoreOrder(draws, f, d => d.Actor >= 0, d => d.Actor, d => d.View, got);
        string seq = string.Join(",", got.Select(d => d.Tag));
        Check("order.core_sequence", seq == "world,5a,5b,3,7,7v,5v", seq);
        // An actor draw without a place in Core's order is a broken contract: loud, never drawn at a guessed place.
        bool threw = false;
        try { SpriteMath.InCoreOrder(new List<(int Actor, int View, string Tag)> { (4, -1, "4") }, f, d => true, d => d.Actor, d => d.View, got); }
        catch (InvalidOperationException) { threw = true; }
        Check("order.core_unplaced_loud", threw, "no exception for actor 4");
        // Storage is reused across frames: the next frame after a refused one gets exactly its own sequence.
        try { SpriteMath.InCoreOrder(draws, f, d => d.Actor >= 0, d => d.Actor, d => d.View, got); seq = string.Join(",", got.Select(d => d.Tag)); }
        catch (InvalidOperationException e) { seq = e.Message; }
        Check("order.core_reuse_after_throw", seq == "world,5a,5b,3,7,7v,5v", seq);
    }

    // ── Sprite: quad corners from the frame camera ────────────────────────
    static void Sprites()
    {
        // Camera yaw = 16384 (90°): GetAxes gives X(forward) = (0,1,0), Y(right) = (−1,0,0), Z(up) = (0,0,1).
        // Actor (100,200,300), texture 64×32, DrawScale 0.5 -> quad 32×16, half-axes 16 and 8.
        // TL = loc − Y·16 + Z·8 = (116,200,308); TR = (84,200,308); BR = (84,200,292); BL = (116,200,292).
        var sb = NewSprites();
        sb.Build(Frame(Cam(default, 0, 16384), new[] { Actor(1, loc: new UEVector(100, 200, 300), tex: "Pkg.T64x32", drawScale: 0.5f) }));
        Check("sprite.yaw90.count", sb.Draws.Count == 1, "draws=" + sb.Draws.Count + " report=" + sb.Report);
        // A drawn actor without Core's frame membership is a refusal, not "every pass with the master camera" (§4).
        bool refused = false;
        try { NewSprites().Build(Frame(Cam(default, 0, 16384), new[] { Actor(1, loc: new UEVector(100, 200, 300), tex: "Pkg.T64x32", drawScale: 0.5f) }, actorViews: ActorViewSets.Empty)); }
        catch (InvalidOperationException e) { refused = e.Message.Contains("without frame membership"); }
        Check("sprite.no-membership-refused", refused, "a sprite actor without WorldFrame.ActorViews membership must be refused");
        if (sb.Draws.Count == 1)
        {
            var v = sb.Draws[0].Vertices;
            CheckV("sprite.yaw90.TL", v[0], 116, 200, 308);
            CheckV("sprite.yaw90.TR", v[1], 84, 200, 308);
            CheckV("sprite.yaw90.BR", v[2], 84, 200, 292);
            CheckV("sprite.yaw90.BL", v[3], 116, 200, 292);
            CheckUV("sprite.uv.TL", v[0], 0, 0); CheckUV("sprite.uv.BR", v[2], 1, 1);
            Check("sprite.indices", sb.Draws[0].Indices.SequenceEqual(new[] { 0, 1, 2, 0, 2, 3 }));
            Check("sprite.texture", sb.Draws[0].Texture == "Pkg.T64x32");
        }

        // Camera pitch = 8192 (45° up), yaw 0: Y = (0,1,0), Z = (−√½, 0, √½).
        // Texture 10×20, DrawScale 1, actor at the origin: half-axes 5 and 10.
        // TL = −Y·5 + Z·10 = (−7.0711, −5, 7.0711); BR = Y·5 − Z·10 = (7.0711, 5, −7.0711).
        sb.Build(Frame(Cam(new UEVector(-500, 0, 0), 8192, 0), new[] { Actor(2, tex: "Pkg.T10x20") }));
        if (sb.Draws.Count == 1)
        {
            float s = MathF.Sqrt(0.5f) * 10f;
            CheckV("sprite.pitch45.TL", sb.Draws[0].Vertices[0], -s, -5, s);
            CheckV("sprite.pitch45.BR", sb.Draws[0].Vertices[2], s, 5, -s);
        }
        else Check("sprite.pitch45.count", false, sb.Report.ToString());

        // Mode: PolyFlags = Texture.PolyFlags | style (FUN_10b338f0: 2 Masked, 3 Translucent, 4 Modulated; disasm
        // DrawActorSprite 0x10b32b83); blending is D3DDrv SetBlending (Translucent > Modulated > Masked).
        var cam = Cam(default, 0, 0);
        sb.Build(Frame(cam, new[] {
            Actor(10, tex: "Pkg.T64x32", style: 4, scaleGlow: 0.25f), Actor(11, tex: "Pkg.T64x32", style: 3),
            Actor(12, tex: "Pkg.Masked", style: 1), Actor(13, tex: "Pkg.T64x32", style: 1),
            Actor(14, tex: "Pkg.Masked", style: 3), Actor(15, tex: "Pkg.T64x32", style: 2),
            Actor(16, tex: "Pkg.TexTransl", style: 1), Actor(17, tex: "Pkg.TexMod", style: 1), Actor(18, tex: "Pkg.TexTransl", style: 4) }));
        var blends = sb.Draws.Select(d => d.Blend).ToArray();
        Check("sprite.blend", blends.Take(5).SequenceEqual(new[] { SpriteBlend.Modulated, SpriteBlend.Translucent, SpriteBlend.Masked, SpriteBlend.Opaque, SpriteBlend.Translucent }),
            string.Join(",", blends));
        Check("sprite.flags.style2", blends.Length == 9 && blends[5] == SpriteBlend.Masked, string.Join(",", blends));
        Check("sprite.flags.texture", blends.Length == 9 && blends[6] == SpriteBlend.Translucent && blends[7] == SpriteBlend.Modulated && blends[8] == SpriteBlend.Translucent,
            string.Join(",", blends));
        // PF_Modulated: D3DDrv DrawTile takes the color only without 0x40 (D3DDrv.dll.c:5295): white, ScaleGlow has no effect.
        if (sb.Draws.Count == 9)
        {
            CheckRGB("sprite.modulated_white", sb.Draws[0].Vertices[0], 1f, 1f, 1f);
            CheckRGB("sprite.modulated_white", sb.Draws[7].Vertices[0], 1f, 1f, 1f);
        }

        // Color = clamp01(ScaleGlow), alpha 1: 1.5 -> 1; 0.25 -> 0.25.
        sb.Build(Frame(cam, new[] { Actor(20, tex: "Pkg.T64x32", scaleGlow: 1.5f), Actor(21, tex: "Pkg.T64x32", scaleGlow: 0.25f) }));
        if (sb.Draws.Count == 2)
        {
            CheckRGB("sprite.glow.clamp", sb.Draws[0].Vertices[0], 1, 1, 1);
            CheckRGB("sprite.glow.quarter", sb.Draws[1].Vertices[3], 0.25f, 0.25f, 0.25f);
        }
        else Check("sprite.glow.count", false);

        // Not drawn: DrawType 2 (mesh), hidden (Core does not name it), destroyed,
        // no texture, unresolved texture (loud), ice with Style 1 (loud).
        sb.Build(Frame(cam, new[] {
            Actor(30, tex: "Pkg.T64x32", drawType: 2), Actor(31, tex: "Pkg.T64x32", hidden: true),
            Actor(32, tex: "Pkg.T64x32", destroyed: true), Actor(33, tex: "None"),
            Actor(34, tex: "Pkg.Missing"), Actor(35, tex: "Pkg.Ice", style: 1), Actor(36, tex: "Pkg.Ice", style: 3) }));
        Check("sprite.reject.count", sb.Draws.Count == 1 && sb.Draws[0].ActorId == 36, "draws=" + string.Join(",", sb.Draws.Select(d => d.ActorId)));
        Check("sprite.reject.unresolved", sb.Report.Get("texture_unresolved:Pkg.Missing") == 1, sb.Report.ToString());
        Check("sprite.reject.ice", sb.Report.Get("ice_style_not_ported:1") == 1, sb.Report.ToString());

        // Only sprites named by the Core frame are drawn (WorldFrame.SpriteActors): not named, not drawn.
        sb.Build(Frame(cam, new[] { Actor(40, tex: "Pkg.T64x32"), Actor(42, tex: "Pkg.T64x32") }, sprites: new[] { 42 }));
        Check("sprite.core_list", sb.Draws.Count == 1 && sb.Draws[0].ActorId == 42, "draws=" + string.Join(",", sb.Draws.Select(d => d.ActorId)));

        // Without a frame camera there is no orientation: loud refusal.
        sb.Build(new WorldFrame(1, "nocam", new[] { Actor(41, tex: "Pkg.T64x32") }, default, Array.Empty<AudioCommand>(), default,
            Array.Empty<HudCommand>(), Array.Empty<ParticleInstance>(), 0, spriteActors: new[] { 41 }));
        Check("sprite.no_camera", sb.Draws.Count == 0 && sb.Report.Get("no_camera") == 1, sb.Report.ToString());
    }

    // ── DT_SpriteAnimOnce: chain frame by the elapsed fraction of life ────
    static void AnimOnce()
    {
        // n = 4, Default.LifeSpan = 2. frame = clamp(round_even(4·clamp(1 − L/2, 0, 1) − 0.5), 0, 3):
        //   L=2    -> fraction 0 -> −0.5 -> −0 -> 0
        //   L=1.5  -> 0.25       -> 0.5  -> 0 (to even!)
        //   L=1.25 -> 0.375      -> 1.0  -> 1
        //   L=1    -> 0.5        -> 1.5  -> 2
        //   L=0.5  -> 0.75       -> 2.5  -> 2 (to even!)
        //   L=0.25 -> 0.875      -> 3.0  -> 3
        //   L=0    -> 1          -> 3.5  -> 4 -> clamp 3
        //   L=3    -> −0.5 -> 0  -> 0
        var cases = new (float life, string want)[]
        {
            (2f, "Pkg.F0"), (1.5f, "Pkg.F0"), (1.25f, "Pkg.F1"), (1f, "Pkg.F2"),
            (0.5f, "Pkg.F2"), (0.25f, "Pkg.F3"), (0f, "Pkg.F3"), (3f, "Pkg.F0"),
        };
        var sb = NewSprites();
        var cam = Cam(default, 0, 0);
        foreach (var (life, want) in cases)
        {
            sb.Build(Frame(cam, new[] { Actor(50, drawType: 7, tex: "Pkg.F0", lifeSpan: life) }));
            string got = sb.Draws.Count == 1 ? sb.Draws[0].Texture : "<none>";
            Check($"anim.life={life}", got == want, $"got={got} want={want}");
        }
        // Default.LifeSpan = 0 and LifeSpan = 0: 0/0 = NaN -> fraction 0 -> frame 0.
        var nan = NewSprites(_ => 0f);
        nan.Build(Frame(cam, new[] { Actor(51, drawType: 7, tex: "Pkg.F0", lifeSpan: 0f) }));
        Check("anim.nan", nan.Draws.Count == 1 && nan.Draws[0].Texture == "Pkg.F0", nan.Draws.Count == 1 ? nan.Draws[0].Texture : "<none>");
        // DT 4 (DT_RopeSprite) is not the sprite branch.
        sb.Build(Frame(cam, new[] { Actor(52, drawType: 4, tex: "Pkg.F0") }));
        Check("anim.dt4_not_sprite", sb.Draws.Count == 0);
    }

    static ParticleInstance P(int emitter, UEVector pos, float w = 4, float l = 2, float alpha = 1f, UEVector color = default,
        float spin = 0f, string tex = "Pkg.T64x32", UEVector vel = default, float drip = 0f, float spinRate = 0f)
        => new ParticleInstance(emitter, pos, w, l, alpha, color, spin, tex, vel, drip, spinRate);

    // ── Particles: vertex color, billboard, refusals ──────────────────────
    static void Particles()
    {
        // Camera: FovAngle 0 has no projection (ComputeRenderSize) — a loud refusal, not an invented 90.
        bool refused = false;
        try { HPEngine.Camera.From(Cam(default, 0, 0, 0f)); } catch (InvalidOperationException) { refused = true; }
        Check("camera.fov_refused", refused, "Camera.From(FovAngle 0) did not refuse");
        var pb = new ParticleBuilder(Size, TexFlags);
        var cam = Cam(default, 0, 0);   // forward (1,0,0), right (0,1,0), up (0,0,1)

        // Color FUN_10b16ab0: a = Opacity·alpha = 0.5·0.8 = 0.4 < 1 -> color ×0.4:
        //   (1, 0.5, 0.25) -> (0.4, 0.2, 0.1); Style 4 adds (1 − 0.4)·0.5 = 0.3 -> (0.7, 0.5, 0.4).
        // a = 1 -> unchanged, then clamp01: (1.5, 0.2, 0.3) -> (1, 0.2, 0.3).
        var col = new UEVector(1f, 0.5f, 0.25f);
        pb.Build(Frame(cam, new[] { Actor(60, style: 1, opacity: 0.5f, emitter: Emitter(1)), Actor(61, style: 4, opacity: 0.5f, emitter: Emitter(1)),
                                    Actor(62, style: 1, emitter: Emitter(1)) },
            new[] { P(60, new UEVector(50, 0, 0), alpha: 0.8f, color: col), P(61, new UEVector(50, 0, 0), alpha: 0.8f, color: col),
                    P(62, new UEVector(50, 0, 0), color: new UEVector(1.5f, 0.2f, 0.3f)) }));
        Check("particle.count", pb.Draws.Count == 3, pb.Report.ToString());
        if (pb.Draws.Count == 3)
        {
            CheckRGB("particle.color.translucent", pb.Draws[0].Vertices[0], 0.4f, 0.2f, 0.1f);
            // Style 4: D3DDrv DrawTriangles replaces the Render color (0.7, 0.5, 0.4) with white (D3DDrv.dll.c:1017-1020).
            CheckRGB("particle.modulated_white", pb.Draws[1].Vertices[0], 1f, 1f, 1f);
            CheckRGB("particle.color.clamp", pb.Draws[2].Vertices[0], 1f, 0.2f, 0.3f);
            // FUN_10b15090 (Render.dll.c:24688-24697): Style 1 without texture flags is 0x400000, opaque.
            Check("particle.blend", pb.Draws[0].Blend == SpriteBlend.Opaque && pb.Draws[1].Blend == SpriteBlend.Modulated, $"{pb.Draws[0].Blend},{pb.Draws[1].Blend}");
        }
        // DrawFrame sprite pass (D-79 (c)): Core puts a translucent emitter in the late pass (UESpriteSort.Pass ->
        // ActorSnapshot.LatePass); its particle batch carries that pass, an opaque emitter's stays early.
        pb.Build(Frame(cam, new[] { Actor(63, style: 3, latePass: true, emitter: Emitter(1)), Actor(64, style: 1, emitter: Emitter(1)) },
            new[] { P(63, new UEVector(50, 0, 0)), P(64, new UEVector(50, 0, 0)) }));
        SpriteDraw late = pb.Draws.FirstOrDefault(d => d.ActorId == 63), early = pb.Draws.FirstOrDefault(d => d.ActorId == 64);
        Check("particle.late_pass", late is { LatePass: true } && early is { LatePass: false },
            $"draws={pb.Draws.Count} late={late?.LatePass} early={early?.LatePass} report={pb.Report}");
        var (mr, mg, mb) = ParticleBuilder.VertexColor(col, 0.8f, 0.5f, 4);
        Check("particle.color.modulated", Math.Abs(mr - 0.7f) < 1e-5f && Math.Abs(mg - 0.5f) < 1e-5f && Math.Abs(mb - 0.4f) < 1e-5f, $"{mr},{mg},{mb}");

        // Billboard 4×2 at (50,0,0), SpinPos 0: TL = pos − right·2 + up·1 = (50, −2, 1); BR = (50, 2, −1).
        // SpinPos π/2 (clockwise on screen): corner (−2, 1) -> (x·cos + y·sin, −x·sin + y·cos) = (1, 2):
        //   TL = pos + right·1 + up·2 = (50, 1, 2).
        pb.Build(Frame(cam, new[] { Actor(70, emitter: Emitter(1)) },
            new[] { P(70, new UEVector(50, 0, 0)), P(70, new UEVector(50, 0, 0), spin: MathF.PI / 2, spinRate: 1f),
                    P(70, new UEVector(50, 0, 0), spin: MathF.PI / 2) }));
        if (pb.Draws.Count == 1 && pb.Draws[0].Vertices.Count == 12)
        {
            var v = pb.Draws[0].Vertices;
            CheckV("particle.billboard.TL", v[0], 50, -2, 1);
            CheckV("particle.billboard.BR", v[2], 50, 2, -1);
            // FUN_10b184d0: quad top is V = VSize−1, edge is USize−1 (confirmed on the live original). Pkg.Tex 64×32.
            CheckUV("particle.billboard.uvTL", v[0], 0f, 31f / 32f);
            CheckUV("particle.billboard.uvBR", v[2], 63f / 64f, 0f);
            CheckV("particle.billboard.spin.TL", v[4], 50, 1, 2);
            // SpinRate 0 (Render.dll 0x10b18603): not rotated whatever SpinPos holds.
            CheckV("particle.billboard.spinrate0.TL", v[8], 50, -2, 1, 0f);
            Check("particle.billboard.indices", pb.Draws[0].Indices.Take(12).SequenceEqual(new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 }));
        }
        else Check("particle.billboard.count", false, pb.Report.ToString());
        // The angle goes through the GMath table (SinTab index ROUND(SpinPos·65536/2π)), not exact sin/cos: π/3 lands on
        // index 10923 -> SinTab[2730]; exact sin(π/3) differs from the table entry.
        int a3 = ParticleBuilder.SpinAngle(MathF.PI / 3);
        Check("particle.spin.table", a3 == 10923 && BrushCoords.SinAngle(a3) != MathF.Sin(MathF.PI / 3), $"angle {a3} sin {BrushCoords.SinAngle(a3):R} vs {MathF.Sin(MathF.PI / 3):R}");
        pb.Build(Frame(cam, new[] { Actor(71, emitter: Emitter(1)) }, new[] { P(71, new UEVector(50, 0, 0), spin: MathF.PI / 3, spinRate: 1f) }));
        if (pb.Draws.Count == 1)
        {
            float s = BrushCoords.SinAngle(a3), c = BrushCoords.CosAngle(a3);
            // corner (−2, 1) -> (x·c + y·s, −x·s + y·c)
            CheckV("particle.billboard.spin_table.TL", pb.Draws[0].Vertices[0], 50, -2 * c + s, 2 * s + c, 0f);
        }
        else Check("particle.billboard.spin_table.count", false, pb.Report.ToString());

        // Negative size (SizeEndScale −1 on the flying lesson rings): shipped takes Width·0.5 with its sign
        // (Render.dll.c:26037), the 4×2 quad flips instead of vanishing: corner (−w/2, +l/2) = (2, −1) -> TL = (50, 2, −1).
        pb.Build(Frame(cam, new[] { Actor(71, emitter: Emitter(1)) }, new[] { P(71, new UEVector(50, 0, 0), w: -4, l: -2) }));
        if (pb.Draws.Count == 1 && pb.Draws[0].Vertices.Count == 4)
        {
            CheckV("particle.billboard.negative.TL", pb.Draws[0].Vertices[0], 50, 2, -1);
            CheckV("particle.billboard.negative.BR", pb.Draws[0].Vertices[2], 50, -2, 1);
        }
        else Check("particle.billboard.negative.count", false, pb.Report.ToString());

        // Refusals: RenderPrimitive 4, emitter not drawn (loud); Style 2 is Masked (FUN_10b15090 0x400002);
        // Style 1 with a bTransparent texture is Translucent; a particle without an emitter snapshot is loud.
        pb.Build(Frame(cam, new[] { Actor(80, emitter: Emitter(4)), Actor(81, style: 2, emitter: Emitter(3)), Actor(83, style: 1, emitter: Emitter(1)) },
            new[] { P(80, new UEVector(50, 0, 0)), P(81, new UEVector(50, 0, 0)), P(82, new UEVector(50, 0, 0)), P(83, new UEVector(50, 0, 0), tex: "Pkg.TexTransl") }));
        Check("particle.flags.texture", pb.Draws.SingleOrDefault(d => d.ActorId == 83)?.Blend == SpriteBlend.Translucent, pb.Report.ToString());
        Check("particle.prim4_refused", pb.Draws.All(d => d.ActorId != 80) && pb.Report.Get("primitive_not_ported:4") == 1, pb.Report.ToString());
        Check("particle.prim3_drawn", pb.Draws.Any(d => d.ActorId == 81));
        Check("particle.style2", pb.Draws.Single(d => d.ActorId == 81).Blend == SpriteBlend.Masked, pb.Draws.Single(d => d.ActorId == 81).Blend.ToString());
        Check("particle.no_snapshot", pb.Report.Get("emitter_no_snapshot") == 1, pb.Report.ToString());

        // Particles in the frame belong only to emitters drawn by the Core renderer: no second visibility gate here.
        pb.Build(Frame(cam, new[] { Actor(90, hidden: true, emitter: Emitter(1)) }, new[] { P(90, new UEVector(50, 0, 0)) }));
        Check("particle.core_owned", pb.Draws.Count == 1, $"draws={pb.Draws.Count}");
    }

    // ── PPRIM_Liquid: drop geometry ───────────────────────────────────────
    static void Liquid()
    {
        var pb = new ParticleBuilder(Size, TexFlags);
        var cam = Cam(default, 0, 0);   // right (0,1,0), down = −Z = (0,0,−1)
        // Edge UVs of a 64×32 texture: u1 = 63/64 = 0.984375, v1 = 31/32 = 0.96875.
        const float u1 = 0.984375f, v1 = 0.96875f;

        // Hanging drop (DripTimer > 0), W = 8, L = 10 at p = (100,0,0):
        //   (0,0) -> (100,0,0); (+4, 7.5) -> (100,4,−7.5); (0,10) -> (100,0,−10); (−4, 7.5) -> (100,−4,−7.5).
        //   UV: (0,v1), (u1,v1), (u1,0), (0,0).
        pb.Build(Frame(cam, new[] { Actor(100, emitter: Emitter(2)) },
            new[] { P(100, new UEVector(100, 0, 0), w: 8, l: 10, drip: 1f) }));
        Check("liquid.hang.count", pb.Draws.Count == 1 && pb.Draws[0].Vertices.Count == 8 && pb.LiquidQuads == 1, pb.Report.ToString());
        if (pb.Draws.Count == 1 && pb.Draws[0].Vertices.Count == 8)
        {
            var v = pb.Draws[0].Vertices;
            CheckV("liquid.hang.v0", v[0], 100, 0, 0);
            CheckV("liquid.hang.v1", v[1], 100, 4, -7.5f);
            CheckV("liquid.hang.v2", v[2], 100, 0, -10);
            CheckV("liquid.hang.v3", v[3], 100, -4, -7.5f);
            CheckUV("liquid.hang.uv0", v[0], 0, v1); CheckUV("liquid.hang.uv1", v[1], u1, v1);
            CheckUV("liquid.hang.uv2", v[2], u1, 0); CheckUV("liquid.hang.uv3", v[3], 0, 0);
            // After the drop, the same billboard as prim 1 (fall-through case): W×L = 8×10 at p,
            // top left = p − right·4 + up·5 = (100, −4, 5).
            CheckV("liquid.hang.billboard", v[4], 100, -4, 5);
        }

        // Falling drop, W = 2, L = 2, Vel = (0,0,−16), p = (100,0,0), camera at the origin:
        //   s = √|Vel| = 4; tail T = (0,0,−1)·(4·2) = (0,0,−8), |T| = 8;
        //   r = √(W³·k·3 / ((|T|+W)·π')) = √(8·3·k / (10·π')) ≈ √0.8 = 0.894427 (k·3 ≈ π').
        //   Head in view (0,0,100); tail p − T = (100,0,8) -> (right 0, down −8, depth 100):
        //   d = (0,−8,0), s' = 8 > r -> L = (8 − r)·√(64/64) + r = 8; screen e = (0, −1).
        //   Vertices (right, down): (−ey·r, ex·r) = (r, 0) -> (100, r, 0); (−ex·r, −ey·r) = (0, r) -> (100, 0, −r);
        //   (ey·r, −ex·r) = (−r, 0) -> (100, −r, 0); (ex·L, ey·L) = (0, −8) -> (100, 0, 8).
        //   UV: (0,0), (0,v1), (u1,v1), (u1,0).
        pb.Build(Frame(cam, new[] { Actor(101, emitter: Emitter(2)) },
            new[] { P(101, new UEVector(100, 0, 0), w: 2, l: 2, vel: new UEVector(0, 0, -16)) }));
        if (pb.Draws.Count == 1 && pb.Draws[0].Vertices.Count == 8)
        {
            var v = pb.Draws[0].Vertices;
            float r = MathF.Sqrt(0.8f);
            CheckV("liquid.fly.v0", v[0], 100, r, 0);
            CheckV("liquid.fly.v1", v[1], 100, 0, -r);
            CheckV("liquid.fly.v2", v[2], 100, -r, 0);
            CheckV("liquid.fly.v3", v[3], 100, 0, 8);
            CheckUV("liquid.fly.uv1", v[1], 0, v1); CheckUV("liquid.fly.uv3", v[3], u1, 0);
            Check("liquid.fly.indices", pb.Draws[0].Indices.Take(6).SequenceEqual(new[] { 0, 1, 2, 0, 2, 3 }));
        }
        else Check("liquid.fly.count", false, pb.Report.ToString());

        // A drop behind the camera (head depth <= 0) is not built; the billboard stays.
        pb.Build(Frame(cam, new[] { Actor(102, emitter: Emitter(2)) },
            new[] { P(102, new UEVector(-100, 0, 0), w: 2, l: 2, vel: new UEVector(0, 0, -16)) }));
        Check("liquid.behind", pb.Draws.Count == 1 && pb.Draws[0].Vertices.Count == 4 && pb.LiquidQuads == 0);

        // Non-Liquid (prim 1) builds no drops.
        pb.Build(Frame(cam, new[] { Actor(103, emitter: Emitter(1)) }, new[] { P(103, new UEVector(100, 0, 0), drip: 1f) }));
        Check("liquid.prim1_no_drop", pb.LiquidQuads == 0 && pb.Draws[0].Vertices.Count == 4);
    }

    // ── Depth mapping ─────────────────────────────────────────────────────
    // D3DDrv DrawTriangles: sz = rhw·(−(1 + 2^-15)) + (1 + 2^-15), rhw = 1/Z (D3DDrv.dll.c:985-986; 0xbf800100 / 0x3f800100 at
    // 16248/16250), i.e. sz = (1 − 1/Z)·(1 + 2^-15): the expectation is that formula, the port's clip z/w through Camera.Rows.
    static void Depth()
    {
        var rows = new float[16];
        HPEngine.Camera.From(Cam(default, 0, 0)).Rows(800, 600, rows);
        double k = 1.0 + Math.Pow(2, -15), worst = 0;
        foreach (float z in new[] { 1.5f, 10f, 100f, 1000f, 9783f, 20000f, 32768f, 40000f })
        {
            var p = new UEVector(z, 0, 0);
            double cz = rows[8] * p.x + rows[9] * p.y + rows[10] * p.z + rows[11], cw = rows[12] * p.x + rows[13] * p.y + rows[14] * p.z + rows[15];
            worst = Math.Max(worst, Math.Abs(cz / cw - (1 - 1 / (double)z) * k));
        }
        Check("depth.d3d_sz", worst < 2e-7, $"max |sz − (1 − 1/Z)(1 + 2^-15)| = {worst:E2}");
    }

    // ── Coronas ───────────────────────────────────────────────────────────
    static void Coronas()
    {
        // Core decides the coronas (WorldFrame.Coronas); the builder only projects them. Expected values from the
        // UE1 projection (Render.dll 0x10b26b03..0x10b26d71, FSceneNode::ComputeRenderSize): camera at 0, yaw 0,
        // FOV 90, frame 800x600 -> FX2 400, FY2 300, Proj.Z = 400 / tan(45 deg) = 400. Lamp (100, 50, 25): Z 100,
        // X 50 (right), Y -25 (down axis) -> 50*4 + 400 = 600, -25*4 + 300 = 200. Side 800 * 0.25 * 0.8 = 160 ->
        // (520,120)-(680,280).
        var cam = Cam(default, 0, 0, 90f);
        WorldFrame F(params CoronaInstance[] c) => new WorldFrame(1, "test", Array.Empty<ActorSnapshot>(), cam,
            Array.Empty<AudioCommand>(), default, Array.Empty<HudCommand>(), Array.Empty<ParticleInstance>(), 0, coronas: c);
        var cb = new CoronaBuilder();
        // The tile is projected by Core (UECoronas.Project on the master frame of the camera), the builder scales to pixels.
        float fov = 90f;
        CoronaInstance L(int id, UEVector loc, float ds, float r, float g, float b)
        {
            var mf = UESceneNode.CreateMaster(800, 600, cam.Location, cam.Rotation, fov, 5);
            bool on = UECoronas.Project(mf, loc, ds, out float x0, out float y0, out float x1, out float y1);
            return new CoronaInstance(id, "Pkg.Corona", loc, ds, r, g, b, on, x0, y0, x1, y1, mf.X);
        }
        var lamp = L(200, new UEVector(100, 50, 25), 0.25f, 0.6f, 0.1f, 0.2f);
        cb.Build(F(lamp), 800, 600);
        Check("corona.drawn", cb.Draws.Count == 1, $"draws={cb.Draws.Count}");
        if (cb.Draws.Count == 1)
        {
            var v = cb.Draws[0].Vertices;
            CheckV("corona.rect.TL", v[0], 520, 120, 0);
            CheckV("corona.rect.BR", v[2], 680, 280, 0);
            CheckRGB("corona.color", v[0], 0.6f, 0.1f, 0.2f);
            Check("corona.flags", cb.Draws[0].ScreenSpace && cb.Draws[0].Blend == SpriteBlend.Translucent
                && cb.Draws[0].Texture == "Pkg.Corona" && cb.Draws[0].ActorId == 200);
        }
        // FOV 60: Proj.Z = 400 / tan(30 deg) = 692.8203 (independent of the Core body) -> centre (50·k + 400, −25·k + 300),
        // k = Proj.Z / 100; TL = centre − 80.
        cam = Cam(default, 0, 0, 60f); fov = 60f;
        lamp = L(200, new UEVector(100, 50, 25), 0.25f, 0.6f, 0.1f, 0.2f);
        cb.Build(F(lamp), 800, 600);
        if (cb.Draws.Count == 1) CheckV("corona.rect.fov60", cb.Draws[0].Vertices[0], 666.4102f, 46.7949f, 0, 2e-3f);
        else Check("corona.rect.fov60", false, $"draws={cb.Draws.Count}");
        // Drawn only when Z > 1 (fcomp 1.0; test ah,0x41): Z = 0.5 and Z = 1 are not drawn, Z = 1.5 is; behind -> not.
        cb.Build(F(L(201, new UEVector(0.5f, 0, 0), 0.25f, 1, 1, 1),
                   L(202, new UEVector(1f, 0, 0), 0.25f, 1, 1, 1),
                   L(203, new UEVector(1.5f, 0, 0), 0.25f, 1, 1, 1),
                   L(204, new UEVector(-100, 0, 0), 0.25f, 1, 1, 1)), 800, 600);
        Check("corona.near", cb.Draws.Count == 1 && cb.Draws[0].ActorId == 203 && cb.Report.Get("behind") == 3,
            $"draws={string.Join(",", cb.Draws.Select(d => d.ActorId))} {cb.Report}");
        // Slot order is kept.
        cb.Build(F(L(7, new UEVector(100, 0, 0), 0.25f, 1, 1, 1), lamp), 800, 600);
        Check("corona.order", cb.Draws.Count == 2 && cb.Draws[0].ActorId == 7 && cb.Draws[1].ActorId == 200);
        // Display frame between ticks (FramePacing, alpha < 1): Core projected the tile at the tick camera (yaw 0, FOV 90 ->
        // (520,120)-(680,280) above); the frame's camera is interpolated to yaw 4096 (22.5 deg). The tile follows the
        // frame's camera: forward (cos, sin, 0), right (-sin, cos, 0): Z = 100cos + 50sin = 111.5221, X = 50cos - 100sin =
        // 7.9256 -> centre (400 + X·400/Z, 300 − 25·400/Z) = (428.4271, 210.3317), TL = centre − 80. A tick frame
        // (no lerped camera) keeps Core's tile. A lamp in front of the tick camera and behind the frame's is not drawn.
        cam = Cam(default, 0, 0, 90f); fov = 90f;
        lamp = L(200, new UEVector(100, 50, 25), 0.25f, 0.6f, 0.1f, 0.2f);
        var back = L(205, new UEVector(10, -100, 0), 0.25f, 1, 1, 1);   // tick camera: Z 10 > 1, drawn
        cam = Cam(default, 0, 4096, 90f);                                  // the display frame's interpolated camera
        cb.Build(F(lamp), 800, 600, F(lamp).Camera);
        if (cb.Draws.Count == 1) CheckV("corona.between_ticks", cb.Draws[0].Vertices[0], 348.4271f, 130.3317f, 0, 2e-2f);
        else Check("corona.between_ticks", false, $"draws={cb.Draws.Count}");
        cb.Build(F(lamp), 800, 600);
        if (cb.Draws.Count == 1) CheckV("corona.tick_frame", cb.Draws[0].Vertices[0], 520, 120, 0);
        else Check("corona.tick_frame", false, $"draws={cb.Draws.Count}");
        cb.Build(F(back), 800, 600, F(back).Camera);   // frame camera yaw 22.5: Z = 10cos − 100sin < 0
        Check("corona.between_ticks_behind", cb.Draws.Count == 0 && cb.Report.Get("behind") == 1, $"draws={cb.Draws.Count} {cb.Report}");
    }

    // ── Shadows (decals) ──────────────────────────────────────────────────
    static void Decals()
    {
        var db = new DecalBuilder(Size);
        var cam = Cam(default, 0, 0);
        // A triangle piece and a quad piece -> fan: [0,1,2] and [3,4,5, 3,5,6].
        var tri = new DecalPiece(0, new[] { new UEVector(0, 0, 0), new UEVector(10, 0, 0), new UEVector(0, 10, 0) },
            new[] { 0f, 1f, 0f }, new[] { 0f, 0f, 1f }, -1);
        var quad = new DecalPiece(1, new[] { new UEVector(0, 0, 1), new UEVector(5, 0, 1), new UEVector(5, 5, 1), new UEVector(0, 5, 1) },
            new[] { 0.1f, 0.9f, 0.9f, 0.1f }, new[] { 0.2f, 0.2f, 0.8f, 0.8f }, -1);
        DecalInstance Rec(int id, params DecalPiece[] pcs) => new DecalInstance(id, 0, 3, new UEVector(0, 0, 1), default, default, default, default, default, Array.Empty<int>(), pcs);

        db.Build(Frame(cam, new[] { Actor(300, cls: "ActorShadow", tex: "Pkg.ShadowT", style: 4, drawType: 1) }, decals: new[] { Rec(300, tri, quad) }));
        Check("decal.count", db.Draws.Count == 1, db.Report.ToString());
        if (db.Draws.Count == 1)
        {
            var d = db.Draws[0];
            Check("decal.fan", d.Indices.SequenceEqual(new[] { 0, 1, 2, 3, 4, 5, 3, 5, 6 }), string.Join(",", d.Indices));
            CheckV("decal.point", d.Vertices[5], 5, 5, 1);
            CheckUV("decal.uv", d.Vertices[5], 0.9f, 0.8f);
            CheckRGB("decal.white", d.Vertices[0], 1, 1, 1);
            Check("decal.blend", d.Blend == SpriteBlend.Modulated && d.Texture == "Pkg.ShadowT");
        }

        // An FDecal record is its own draw with the record's surface: the decal is drawn in its surface's pass
        // (ClipDecal in the surface loop, Render.dll.c:1491).
        DecalInstance RecS(int id, int surf, params DecalPiece[] pcs) => new DecalInstance(id, 0, surf, new UEVector(0, 0, 1), default, default, default, default, default, Array.Empty<int>(), pcs);
        db.Build(Frame(cam, new[] { Actor(301, cls: "ActorShadow", tex: "Pkg.ShadowT", style: 4, drawType: 1) }, decals: new[] { RecS(301, 7, tri), RecS(301, 9, quad) }));
        Check("decal.per_record_surf", db.Draws.Count == 2 && db.Draws[0].Surf == 7 && db.Draws[1].Surf == 9 && db.Draws[1].Indices.Count == 6,
            string.Join(",", db.Draws.Select(d => d.Surf + ":" + d.Indices.Count)));

        // Style != 4, unresolved/empty texture, no snapshot, no pieces: not drawn; a hidden decal actor is
        // drawn (the original does not check hiddenness, Render.dll.c:1455-1530; which pieces is decided by Core DrawFrame).
        db.Build(Frame(cam, new[] {
                Actor(310, tex: "Pkg.ShadowT", style: 3), Actor(311, tex: "None", style: 4),
                Actor(312, tex: "Pkg.Missing", style: 4), Actor(313, tex: "Pkg.ShadowT", style: 4, hidden: true),
                Actor(314, tex: "Pkg.ShadowT", style: 4) },
            decals: new[] { Rec(310, tri), Rec(311, tri), Rec(312, tri), Rec(313, tri), Rec(314), Rec(315, tri) }));
        Check("decal.rejects", db.Draws.Count == 1 && db.Draws[0].ActorId == 313, string.Join(",", db.Draws.Select(d => d.ActorId)));
        Check("decal.style", db.Report.Get("style_not_ported:3") == 1, db.Report.ToString());
        Check("decal.tex_none", db.Report.Get("texture_unresolved:<none>") == 1, db.Report.ToString());
        Check("decal.tex_missing", db.Report.Get("texture_unresolved:Pkg.Missing") == 1, db.Report.ToString());
        Check("decal.no_pieces", db.Report.Get("no_decal_pieces") == 1, db.Report.ToString());
        Check("decal.no_snapshot", db.Report.Get("no_snapshot") == 1, db.Report.ToString());

        // A piece lives in its own frame: pieces of two frames of one record are two draws, each with its frame.
        var triChild = new DecalPiece(0, tri.Points, tri.U, tri.V, 2);
        db.Build(Frame(cam, new[] { Actor(320, cls: "ActorShadow", tex: "Pkg.ShadowT", style: 4, drawType: 1) }, decals: new[] { Rec(320, tri, triChild) }));
        Check("decal.frame_view", db.Draws.Count == 2 && db.Draws.Select(d => d.FrameView).OrderBy(v => v).SequenceEqual(new[] { -1, 2 }),
            string.Join(",", db.Draws.Select(d => d.FrameView)));
    }

    // ── AnimNext chain frame at draw time (UTexture::Get(Time) = Update + AnimCur) ─────────────
    // Particles FUN_10b15090 (Render.dll.c:9703-9713, flags +0x80 with AnimCur :9738), sprite DT_Sprite
    // (DrawActorSprite :12024), decal (:1468-1475). Time comes from the production UETextureAnim (the same object as
    // BSP batches). Chain Pkg.F0 -> Pkg.TexTransl -> Pkg.F0, Max = 0 (a step per Update; Engine 0x10421070):
    // t=100: first draw, step: TexTransl (bTransparent, 0x4 -> Translucent; the head has no flags, Opaque);
    // the second consumer in the same frame gets the same frame (one Update per Time); t=100.5: F0 again.
    // DT_SpriteAnimOnce: the frame's AnimNext/AnimCur are zeroed during Get (:12002-12005); FrameOf does not apply.
    static void AnimNextFrames()
    {
        var anim = new UETextureAnim(Path.GetTempPath());
        var byPath = (System.Collections.IDictionary)typeof(UETextureAnim).GetField("_byPath",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(anim);
        byPath["Pkg.F0"] = new UETextureAnim.Entry { Path = "Pkg.F0", Next = "Pkg.TexTransl", Min = 0f, Max = 0f };
        byPath["Pkg.TexTransl"] = new UETextureAnim.Entry { Path = "Pkg.TexTransl", Next = "Pkg.F0", Min = 0f, Max = 0f };
        var cam = Cam(default, 0, 0);
        var pb = new ParticleBuilder(Size, TexFlags) { FrameOf = anim.CurrentFrame };
        var sb = NewSprites(); sb.FrameOf = anim.CurrentFrame;
        var db = new DecalBuilder(Size) { FrameOf = anim.CurrentFrame };
        var tri = new DecalPiece(0, new[] { new UEVector(0, 0, 0), new UEVector(10, 0, 0), new UEVector(0, 10, 0) },
            new[] { 0f, 1f, 0f }, new[] { 0f, 0f, 1f }, -1);
        WorldFrame F() => Frame(cam, new[] { Actor(400, emitter: Emitter(1)), Actor(401, loc: new UEVector(100, 0, 0), tex: "Pkg.F0"),
                Actor(402, cls: "ActorShadow", tex: "Pkg.F0", style: 4, drawType: 0) },
            new[] { P(400, new UEVector(50, 0, 0), tex: "Pkg.F0") },
            new[] { new DecalInstance(402, 0, 3, new UEVector(0, 0, 1), default, default, default, default, default, Array.Empty<int>(), new[] { tri }) });

        anim.BeginFrame(100.0);
        pb.Build(F()); sb.Build(F()); db.Build(F());
        Check("anim.particle", pb.Draws.Count == 1 && pb.Draws[0].Texture == "Pkg.TexTransl" && pb.Draws[0].Blend == SpriteBlend.Translucent,
            pb.Draws.Count == 1 ? pb.Draws[0].Texture + " " + pb.Draws[0].Blend : pb.Report.ToString());
        Check("anim.sprite", sb.Draws.Count == 1 && sb.Draws[0].Texture == "Pkg.TexTransl" && sb.Draws[0].Blend == SpriteBlend.Translucent,
            sb.Draws.Count == 1 ? sb.Draws[0].Texture + " " + sb.Draws[0].Blend : sb.Report.ToString());
        Check("anim.decal", db.Draws.Count == 1 && db.Draws[0].Texture == "Pkg.TexTransl", db.Draws.Count == 1 ? db.Draws[0].Texture : db.Report.ToString());
        anim.BeginFrame(100.5);
        pb.Build(F()); sb.Build(F());
        Check("anim.particle.next", pb.Draws.Count == 1 && pb.Draws[0].Texture == "Pkg.F0" && pb.Draws[0].Blend == SpriteBlend.Opaque,
            pb.Draws.Count == 1 ? pb.Draws[0].Texture + " " + pb.Draws[0].Blend : "");
        Check("anim.sprite.next", sb.Draws.Count == 1 && sb.Draws[0].Texture == "Pkg.F0", sb.Draws.Count == 1 ? sb.Draws[0].Texture : "");
        // DT_SpriteAnimOnce: frame only by life fraction (LifeSpan 1 of 2 -> fraction 0.5, frame 2 of 4: Pkg.F2); FrameOf is not called.
        var once = NewSprites(); once.FrameOf = _ => "Pkg.TexTransl";
        once.Build(Frame(cam, new[] { Actor(403, loc: new UEVector(100, 0, 0), drawType: 7, tex: "Pkg.F0", lifeSpan: 1f) }));
        Check("anim.spriteonce_no_frameof", once.Draws.Count == 1 && once.Draws[0].Texture == "Pkg.F2", once.Draws.Count == 1 ? once.Draws[0].Texture : once.Report.ToString());
    }
}
