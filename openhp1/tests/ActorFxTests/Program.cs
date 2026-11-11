// ActorFx TESTS. Expected values are computed HERE, independently of the port:
//   * fog — analytically: a chord through the sphere centre (full FogMap.Density branch: v = 2·(F − b²)·b·K,
//     b = 1, F = 3 -> 4K), LightSource=2 (v = K·|T|/R), doubling of f and blending
//     a = min(1, f·VolumeFog/255), rgb = min(1, (1−a)·rgb + f·col), A = min(1, A + a) — the LightAndFog
//     formulas (Render.dll.c:16990-17110), rewritten independently;
//     colour — FGetHSV rewritten here, K = VolB·(Brightness·Scale/255)/64;
//     relative tolerance 3e-3 — table FastSqrt (10 mantissa bits) vs Math.Sqrt;
//   * Opacity/blending — the HP1ActorMaterials.Resolve branch table, by hand;
//   * Fatness — Fatness/16 − 8 (Render.dll.c:9328), by hand.
// Synthetic data: no game files needed.
using HP1.UEScript;
using OpenHP1.ActorFx;

static class Program
{
    static readonly List<string> Failed = new();
    static int _checks;

    static void Check(string name, bool ok, string detail = "")
    {
        _checks++;
        if (!ok) { Failed.Add(name); Console.WriteLine($"FAIL {name} {detail}"); }
    }

    static void Run(string name, Action test)
    {
        try { test(); }
        catch (Exception ex) { Check("crash:" + name, false, ex.GetType().Name + ": " + ex.Message); }
    }

    static int Main()
    {
        Run("FogThroughCenter", FogThroughCenter);
        Run("FogChildView", FogChildView);
        Run("FogRotatedCamera", FogRotatedCamera);
        Run("FogAmbientSource", FogAmbientSource);
        Run("FogOrderAndBlend", FogOrderAndBlend);
        Run("FogSaturation", FogSaturation);
        Run("FogSkipReasons", FogSkipReasons);
        Run("FogRecordSelection", FogRecordSelection);
        Run("FogStaleOnInvalidCamera", FogStaleOnInvalidCamera);
        Run("FogStaticChains", FogStaticChains);
        Run("FogSpriteMark", FogSpriteMark);
        Run("LightRecords", LightRecords);
        Run("FogDynamicLeaves", FogDynamicLeaves);
        Run("LightDispatched", LightDispatched);
        Run("FogSpecularByte", FogSpecularByte);
        Run("OpacityBlend", OpacityBlend);
        Run("OpacityPalette", OpacityPaletteTest);
        Run("Fatness", Fatness);
        Console.WriteLine($"checks={_checks} failed={Failed.Count}");
        if (Failed.Count == 0) { Console.WriteLine("ACTORFX TESTS PASS"); return 0; }
        Console.WriteLine("ACTORFX TESTS FAIL: " + string.Join(", ", Failed));
        return 1;
    }

    // ── synthetic data ───────────────────────────────────────────────────────

    static LightState Light(int brightness, int hue, int sat, int volB, int volR, int volFog, int source = 0,
        bool dynamic = false, int type = 1, int radius = 10, float scale = 1f)
        => new LightState(type, 0, brightness, hue, sat, radius, (radius + 1) * 25f, 0, 0, 0, 0, volB,
            false, dynamic, false, source, true, false, false, scale, volR, volFog);

    static ActorSnapshot Actor(int id, UEVector loc, int zone, LightState light, int exportIndex, bool destroyed = false)
        => new ActorSnapshot(id, -1, -1, "A" + id, "Light", true, loc, default, default, default, 0, zone, -1,
            "-", "-", "-", "-", 0f, null, 0f, 0f, 0f, false, 0f, 0f, 0f, 0f, Array.Empty<string>(), destroyed,
            0, null, null, 1f, 0, false, false, false, 0, 1f, null, null, light, default, default, default,
            exportIndex);

    static WorldFrame Frame(UEVector cam, UERotator rot, bool valid, params ActorSnapshot[] actors)
        => new WorldFrame(1, "t", actors, new CameraSnapshot(valid, 0, cam, rot, 90f, false),
            Array.Empty<AudioCommand>(), default, Array.Empty<HudCommand>(), Array.Empty<ParticleInstance>(), 0);

    static UEActorLighting.Exported Ex(int objectId) => new UEActorLighting.Exported { ObjectId = objectId };

    const int FogZone = 3, OtherZone = 4;

    // The tick's occlusion tree: master + one child per WorldFrame view; volume — dynamic lights whose flagged LeafLights
    // link leaf 0's LeafVolumetricLighting appended (UESceneNode.VolumetricDynamic) in every frame.
    // Every frame's walk visits leaf 0 (UESceneNode.VolumetricLeaves), whose volumetric chain is the statics (Geo).
    // Every frame draws the fogged mesh actor Mesh as a sprite PreRendered after leaf 0 (UESprite.VolLeafMark = 1).
    static readonly UEObject Mesh = new UEObject();
    static UESceneNode Tree(WorldFrame frame, int[] volume)
    {
        var m = new UESceneNode();
        m.VolumetricDynamic.AddRange((volume ?? Array.Empty<int>()).Select(id => (0, id))); m.VolumetricLeaves.Add(0); m.Sprites.Add(new UESprite { Actor = Mesh, VolLeafMark = 1 });
        foreach (var _ in frame.Views)
        {
            var c = new UESceneNode(); c.VolumetricDynamic.AddRange((volume ?? Array.Empty<int>()).Select(id => (0, id))); c.VolumetricLeaves.Add(0);
            c.Sprites.Add(new UESprite { Actor = Mesh, VolLeafMark = 1 }); m.Children.Add(c);
        }
        return m;
    }

    // A level whose leaf 0 has the volumetric chain `statics` (LeafVolumetric[0] = 0, LightExports = statics, 0).
    static StaticGeometry Geo(IEnumerable<int> statics, int[] leafVolumetric = null)
        => new StaticGeometry(Array.Empty<StaticSurface>(), Array.Empty<StaticNode>(), Array.Empty<UEVector>(), Array.Empty<UEVector>(),
            Array.Empty<int>(), Array.Empty<int>(), Array.Empty<StaticLightMap>(), Array.Empty<int>(), statics.Append(0).ToArray(),
            leafVolumetric: leafVolumetric ?? new[] { 0 });

    static ActorFogFrame Fog(WorldFrame frame, HashSet<int> statics, int[] volume = null, float levelBrightness = 1f)
    {
        var f = new ActorFogFrame();
        f.Bind(new HashSet<int> { FogZone }, Geo(statics));
        f.Begin(frame, Tree(frame, volume), levelBrightness);
        return f;
    }

    // FGetHSV, rewritten here independently.
    static (float r, float g, float b) Hsv(int h, int s, int b)
    {
        double x = b * 1.4 / 255.0;
        double lum = x > 0 ? Math.Min(1.0, Math.Max(0.0, 0.7 * x / (Math.Sqrt(x) + 0.01))) : 0;
        double wr, wg, wb;
        if (h < 86) { wb = 0; wr = (85 - h) / 85.0; wg = h / 85.0; }
        else if (h < 171) { wr = 0; wg = (170 - h) / 85.0; wb = (h - 85) / 85.0; }
        else { wg = 0; wr = (h - 170) / 85.0; wb = (255 - h) / 84.0; }
        double sat = s / 255.0;
        return ((float)(((1 - wr) * sat + wr) * lum), (float)(((1 - wg) * sat + wg) * lum), (float)(((1 - wb) * sat + wb) * lum));
    }

    static double K(int volB, float brightness) => volB * (brightness / 255.0) / 64.0;

    static bool Near(double got, double want, double rel = 3e-3)
        => Math.Abs(got - want) <= rel * Math.Max(Math.Abs(want), 1e-3);

    static string V4(float[] f, int v) => $"({f[v * 4]:F5}, {f[v * 4 + 1]:F5}, {f[v * 4 + 2]:F5}, {f[v * 4 + 3]:F5})";

    // Vertex behind the sphere on the camera->centre axis: full chord, v = 4K.
    static void ThroughCenter(string name, UEVector cam, UERotator rot, UEVector fwd)
    {
        UEVector P(float s) => new UEVector(cam.x + fwd.x * s, cam.y + fwd.y * s, cam.z + fwd.z * s);
        // VolumeRadius 3 -> R = 100; centre 300 from the camera, vertex at 500 (behind the sphere).
        var light = Light(brightness: 64, hue: 0, sat: 0, volB: 16, volR: 3, volFog: 128);
        var fog = Fog(Frame(cam, rot, true, Actor(10, P(300f), OtherZone, light, 7)), new HashSet<int> { 7 });
        var outv = new float[8];
        bool ok = fog.Apply(Mesh.ObjectId, new[] { P(500f), P(150f) }, outv);
        double f = 2.0 * 4.0 * K(16, 64f);                       // f = f + f (:17077)
        var c = Hsv(0, 0, 255);
        double col = c.r * (64.0 / 255.0);
        double a = f * 128.0 / 255.0;
        Check(name + "-applied", ok && fog.FoggedActors == 1 && fog.FoggedVertices == 2, $"ok={ok} actors={fog.FoggedActors} verts={fog.FoggedVertices}");
        Check(name + "-rgba", Near(outv[0], f * col) && outv[1] == 0f && outv[2] == 0f && Near(outv[3], a),
            $"got {V4(outv, 0)} want ({f * col:F5}, 0, 0, {a:F5})");
        // Vertex 1 is in front of the sphere (150 < 300 − 100): the ray to it misses the sphere.
        Check(name + "-before-sphere-zero", outv[4] == 0f && outv[5] == 0f && outv[6] == 0f && outv[7] == 0f, V4(outv, 1));
    }

    static void FogThroughCenter()
        => ThroughCenter("fog-chord", new UEVector(0f, 0f, 0f), new UERotator(0, 0, 0), new UEVector(1f, 0f, 0f));

    // A child frame (mirror/portal) fogs the actor from ITS eye (ViewFrame.Coords): master camera at the origin looking +X,
    // sphere R 100 at 300, vertex at 500 (behind the sphere: full chord); the child eye at 1000 looking −X sees the vertex
    // at 500 BEFORE the sphere (600..800 from it): no fog.
    static void FogChildView()
    {
        var light = Light(brightness: 64, hue: 0, sat: 0, volB: 16, volR: 3, volFog: 128);
        var child = new BrushCoords { Origin = new UEVector(1000f, 0f, 0f), X = new UEVector(0f, -1f, 0f), Y = new UEVector(0f, 0f, -1f), Z = new UEVector(-1f, 0f, 0f) };
        var view = new ViewFrame(-1, ViewFrame.KindMirror, 1, Affine.Identity, default, 0f, true, Array.Empty<ViewSource>(), child);
        var frame = new WorldFrame(1, "t", new[] { Actor(10, new UEVector(300f, 0f, 0f), OtherZone, light, 7) },
            new CameraSnapshot(true, 0, default, default, 90f, false), Array.Empty<AudioCommand>(), default, Array.Empty<HudCommand>(),
            Array.Empty<ParticleInstance>(), 0, views: new[] { view });
        var fog = Fog(frame, new HashSet<int> { 7 });
        var master = new float[4]; var mirror = new float[4];
        fog.Apply(Mesh.ObjectId, new[] { new UEVector(500f, 0f, 0f) }, master);
        fog.Apply(Mesh.ObjectId, new[] { new UEVector(500f, 0f, 0f) }, mirror, view: 0);
        Check("fog-child-view", master[3] > 0f && mirror[0] == 0f && mirror[3] == 0f, $"master {V4(master, 0)} child {V4(mirror, 0)}");
    }

    // Camera off the origin and rotated (yaw 90°: forward = world +Y) — checks subtraction of the
    // camera position and that vertex and light end up in the same coordinates.
    static void FogRotatedCamera()
        => ThroughCenter("fog-rotated", new UEVector(100f, -200f, 50f), new UERotator(0, 16384, 0), new UEVector(0f, 1f, 0f));

    // LightSource = 2: density by depth, v = K·|T|/R, vertex 150 from the camera.
    static void FogAmbientSource()
    {
        var light = Light(brightness: 64, hue: 0, sat: 0, volB: 16, volR: 3, volFog: 255, source: 2);
        var fog = Fog(Frame(default, default, true, Actor(10, new UEVector(2000f, 0f, 0f), OtherZone, light, 7)), new HashSet<int> { 7 });
        var outv = new float[4];
        fog.Apply(Mesh.ObjectId, new[] { new UEVector(90f, 120f, 0f) }, outv);   // |T| = 150
        double f = 2.0 * K(16, 64f) * 150.0 / 100.0;
        double col = Hsv(0, 0, 255).r * (64.0 / 255.0);
        Check("fog-ambient-source", Near(outv[0], f * col) && Near(outv[3], f), $"got {V4(outv, 0)} want ({f * col:F5}, 0, 0, {f:F5})");
    }

    // Light order = far first by camera z (appQsort 0x10b0a5b1+, FogMap.SortFarFirst); blending is not commutative.
    static void FogOrderAndBlend()
    {
        // A: red, VolumeFog 255; B: green (Hue 85), VolumeFog 0. Both LightSource 2 (density by vertex depth only).
        var la = Light(brightness: 128, hue: 0, sat: 0, volB: 48, volR: 3, volFog: 255, source: 2);
        var lb = Light(brightness: 128, hue: 85, sat: 0, volB: 48, volR: 3, volFog: 0, source: 2);
        var verts = new[] { new UEVector(100f, 0f, 0f) };
        double f = 2.0 * K(48, 128f) * 100.0 / 100.0;
        var ca = Hsv(0, 0, 255); var cb = Hsv(85, 0, 255);
        double s = 128.0 / 255.0;
        foreach (var (order, name) in new[] { (new[] { 7, 8 }, "fog-order-AB"), (new[] { 8, 7 }, "fog-order-BA") })
        {
            double r = 0, g = 0, b = 0, A = 0;
            foreach (int e in order)
            {
                var c = e == 7 ? ca : cb; double fogA = e == 7 ? 1.0 : 0.0;
                double a = Math.Min(1.0, f * fogA);
                r = Math.Min(1.0, (1 - a) * r + f * c.r * s);
                g = Math.Min(1.0, (1 - a) * g + f * c.g * s);
                b = Math.Min(1.0, (1 - a) * b + f * c.b * s);
                A = Math.Min(1.0, A + a);
            }
            // The first light of the order is the far one.
            float za = order[0] == 7 ? 6000f : 5000f, zb = order[0] == 7 ? 5000f : 6000f;
            var fog = Fog(Frame(default, default, true, Actor(10, new UEVector(za, 0f, 0f), OtherZone, la, 7),
                Actor(11, new UEVector(zb, 0f, 0f), OtherZone, lb, 8)), new HashSet<int> { 7, 8 });
            var outv = new float[4];
            fog.Apply(Mesh.ObjectId, verts, outv);
            Check(name, Near(outv[0], r) && Near(outv[1], g) && Near(outv[2], b) && Near(outv[3], A),
                $"got {V4(outv, 0)} want ({r:F5}, {g:F5}, {b:F5}, {A:F5})");
        }
    }

    // Caps: density and alpha saturate at 1, colour at 1.
    static void FogSaturation()
    {
        var light = Light(brightness: 255, hue: 0, sat: 255, volB: 255, volR: 3, volFog: 255, source: 2);
        var fog = Fog(Frame(default, default, true, Actor(10, new UEVector(1000f, 0f, 0f), OtherZone, light, 7)), new HashSet<int> { 7 });
        var outv = new float[4];
        fog.Apply(Mesh.ObjectId, new[] { new UEVector(400f, 0f, 0f) }, outv);
        // v = K·400/100 > 1 -> 1, f = 2; a = min(1, 2) = 1; rgb = min(1, 2·col) — col = lum ≈ 0.81 -> 1.
        Check("fog-saturation", outv[0] == 1f && outv[1] == 1f && outv[2] == 1f && outv[3] == 1f, V4(outv, 0));
    }

    static void FogSkipReasons()
    {
        var vol = Light(64, 0, 0, 16, 3, 128);
        var flat = Light(64, 0, 0, 0, 3, 128);   // VolumeBrightness 0 — not volumetric
        var frame = Frame(default, default, true,
            Actor(10, new UEVector(300f, 0f, 0f), OtherZone, vol, 7),
            Actor(11, new UEVector(300f, 0f, 0f), OtherZone, flat, 9));
        var verts = new[] { new UEVector(500f, 0f, 0f) };
        var outv = new float[4];

        var fog = Fog(frame, new HashSet<int> { 7, 9 });
        outv[0] = 5f;
        // An actor the frame did not draw as a sprite has no Volumetrics list.
        bool ok = fog.Apply(new UEObject().ObjectId, verts, outv);
        Check("fog-not-drawn", !ok && fog.SkipNotInZone == 1 && outv[0] == 0f && fog.FoggedActors == 0, $"ok={ok} skip={fog.SkipNotInZone} r={outv[0]}");

        var flatOnly = Fog(Frame(default, default, true, Actor(11, new UEVector(300f, 0f, 0f), OtherZone, flat, 9)), new HashSet<int> { 9 });
        ok = flatOnly.Apply(Mesh.ObjectId, verts, outv);
        Check("fog-no-volumetric", !ok && flatOnly.SkipNoVolumetric == 1 && flatOnly.Records.Count == 0, $"ok={ok} skip={flatOnly.SkipNoVolumetric} records={flatOnly.Records.Count}");

        ok = fog.Apply(Mesh.ObjectId, verts, outv);
        Check("fog-volumetric-among-flat", ok && outv[0] > 0f && fog.Records.Count == 1, $"ok={ok} r={outv[0]} records={fog.Records.Count}");

        ok = fog.Apply(Mesh.ObjectId, ReadOnlySpan<UEVector>.Empty, outv);
        Check("fog-empty-mesh-not-counted", !ok && fog.FoggedActors == 1, $"ok={ok} actors={fog.FoggedActors}");

        // Invalid camera on the very first frame — no basis.
        var nob = Fog(Frame(default, default, false, frame.Actors.ToArray()), new HashSet<int> { 7 });
        ok = nob.Apply(Mesh.ObjectId, verts, outv);
        Check("fog-no-basis", !ok && nob.SkipNoBasis == 1, $"ok={ok} skip={nob.SkipNoBasis}");
    }

    // Record selection: static (LeafVolumetric) — in any zone; dynamic — a light item this frame's SetupDynamics flagged
    // volumetric (UESceneNode.VolumetricDynamic); LightType 0, Brightness 0, Destroyed — not a light (LightRadius is not read); a spawned
    // light (ExportIndex 0) flagged volumetric DOES get a record (keyed by ObjectId).
    static void FogRecordSelection()
    {
        var p = new UEVector(300f, 0f, 0f);
        var frame = Frame(default, default, true,
            Actor(1, p, OtherZone, Light(64, 0, 0, 16, 3, 128), 1),                        // static
            Actor(2, p, OtherZone, Light(64, 0, 0, 16, 3, 128, dynamic: true), 2),         // dynamic outside a fog zone
            Actor(3, p, FogZone, Light(64, 0, 0, 16, 3, 128, dynamic: true), 3),           // dynamic in a fog zone
            Actor(4, p, FogZone, Light(64, 0, 0, 16, 3, 128, dynamic: false), 4),          // neither static nor dynamic
            Actor(5, p, OtherZone, Light(64, 0, 0, 16, 3, 128, type: 0), 5),
            Actor(6, p, OtherZone, Light(0, 0, 0, 16, 3, 128), 6),
            Actor(7, p, OtherZone, Light(64, 0, 0, 16, 3, 128, radius: 0), 7),            // LightRadius 0: still volumetric
            Actor(8, p, OtherZone, Light(64, 0, 0, 16, 3, 128), 8, destroyed: true),
            Actor(9, p, FogZone, Light(64, 0, 0, 16, 3, 128, dynamic: true), 0),
            Actor(10, p, OtherZone, Light(64, 0, 0, 16, 0, 128), 10));                     // VolumeRadius 0
        var fog = Fog(frame, new HashSet<int> { 1, 5, 6, 7, 8, 10 }, volume: new[] { 3, 9 });
        var got = string.Join(",", fog.Records.Keys.OrderBy(k => k));
        Check("fog-record-selection", got == "1,3,7,9", $"records [{got}], expected [1,3,7,9]");
        // ComputeFromActor scales every record colour by Level->Brightness (Render.dll.c:19274-19280).
        var half = Fog(Frame(default, default, true, Actor(1, p, OtherZone, Light(64, 0, 0, 16, 3, 128), 1)), new HashSet<int> { 1 }, levelBrightness: 0.5f);
        var full = Fog(Frame(default, default, true, Actor(1, p, OtherZone, Light(64, 0, 0, 16, 3, 128), 1)), new HashSet<int> { 1 });
        Check("fog-record-level-brightness", full.Records[1].ColR > 0f && Math.Abs(half.Records[1].ColR - 0.5f * full.Records[1].ColR) < 1e-6f,
            $"colour {half.Records[1].ColR} at Level.Brightness 0.5 vs {full.Records[1].ColR} at 1");
        // The record is in camera coordinates; K and colour come from brightness·Scale.
        var scaled = Fog(Frame(new UEVector(10f, 20f, 30f), default, true,
            Actor(1, new UEVector(310f, 20f, 30f), OtherZone, Light(128, 0, 0, 16, 3, 64, scale: 0.5f), 1)), new HashSet<int> { 1 });
        var r = scaled.Records[1];
        Check("fog-record-camera-space", Math.Abs(r.Lz - 300f) < 1e-3 && Math.Abs(r.Lx) < 1e-3 && Math.Abs(r.Ly) < 1e-3,
            $"L=({r.Lx}, {r.Ly}, {r.Lz})");
        Check("fog-record-k-scale", Near(r.K, K(16, 64f), 1e-5) && Near(r.ColR, Hsv(0, 0, 255).r * 64.0 / 255.0, 1e-5)
            && Near(r.FogA, 64.0 / 255.0, 1e-6), $"K={r.K} col={r.ColR} fog={r.FogA}");
    }

    // A frame with an invalid camera keeps the previous frame's basis and records.
    static void FogStaleOnInvalidCamera()
    {
        var fog = new ActorFogFrame();
        fog.Bind(new HashSet<int> { FogZone }, Geo(new[] { 7 }));
        var f1 = Frame(default, default, true, Actor(10, new UEVector(300f, 0f, 0f), OtherZone, Light(64, 0, 0, 16, 3, 128), 7));
        fog.Begin(f1, Tree(f1, null), 1f);
        var f2 = Frame(default, default, false, f1.Actors.ToArray());
        fog.Begin(f2, Tree(f2, null), 1f);
        var outv = new float[4];
        bool ok = fog.Apply(Mesh.ObjectId, new[] { new UEVector(500f, 0f, 0f) }, outv);
        Check("fog-stale-invalid-camera", ok && fog.CameraBasisValid && fog.Records.Count == 1 && outv[0] > 0f,
            $"ok={ok} basis={fog.CameraBasisValid} records={fog.Records.Count}");
        // The next valid frame without lights clears the records.
        var f3 = Frame(default, default, true);
        var t3 = Tree(f3, null); t3.VolumetricLeaves.Clear();   // the walk visited no volumetric leaf
        fog.Begin(f3, t3, 1f);
        Check("fog-records-reset-per-frame", fog.Records.Count == 0, $"records={fog.Records.Count}");
    }

    // LeafVolumetricLighting per frame: only the chains of the leaves the walk visited (leaf 1 and 3 here; leaf 0's light 5
    // and leaf 2's light 9 are not visited), each light once, minus the culled one: light 8 is 1000 behind a side plane
    // with VolumeRadius 1 ((1+1)·25 = 50 < 1000) and the camera outside its sphere.
    static void FogStaticChains()
    {
        var g = Geo(new[] { 5, 0, 6, 7, 0, 9, 0, 8, 6, 0 }.SkipLast(1), new[] { 0, 2, 5, 7 });
        var fr = new UESceneNode();
        fr.VolumetricLeaves.AddRange(new[] { 1, 3, 1 });
        fr.ViewPlanes[0] = (new UEVector(1, 0, 0), 0f);
        var at = new Dictionary<int, UEVector> { [5] = default, [6] = new UEVector(10, 0, 0), [7] = new UEVector(20, 0, 0), [8] = new UEVector(-1000, 0, 0), [9] = default };
        var s = LevelFogMaps.FrameStaticVolumetric(g, fr, ex => at.TryGetValue(ex, out UEVector p) ? (true, p, 1) : (false, default, 0));
        var got = string.Join(",", s.OrderBy(x => x));
        Check("fog-static-chains", got == "6,7", $"[{got}] expected [6,7]");
    }

    // The sprite's Volumetrics list = the walk's list at its last successful PreRender (UESprite.VolLeafMark): leaf 0 carries
    // light 7, leaf 1 light 8; a sprite marked after leaf 0 is fogged by 7 only, one marked after leaf 1 by both. A light
    // with LightRadius 1 (52 units) fogs a vertex 500 units away: the list does not depend on the lighting radius.
    static void FogSpriteMark()
    {
        var g = Geo(new[] { 7, 0, 8 }, new[] { 0, 2 });
        var l7 = Light(64, 0, 0, 16, 3, 128, source: 2, radius: 1);
        var l8 = Light(64, 85, 0, 16, 3, 128, source: 2, radius: 1);
        var frame = Frame(default, default, true, Actor(10, new UEVector(300f, 0f, 0f), OtherZone, l7, 7), Actor(11, new UEVector(400f, 0f, 0f), OtherZone, l8, 8));
        float[] Run(int mark)
        {
            var t = new UESceneNode(); t.VolumetricLeaves.AddRange(new[] { 0, 1 });
            t.Sprites.Add(new UESprite { Actor = Mesh, VolLeafMark = mark });
            var f = new ActorFogFrame(); f.Bind(new HashSet<int> { FogZone }, g); f.Begin(frame, t, 1f);
            var o = new float[4]; f.Apply(Mesh.ObjectId, new[] { new UEVector(500f, 0f, 0f) }, o); return o;
        }
        float[] one = Run(1), two = Run(2);
        Check("fog-sprite-mark", one[0] > 0f && one[1] == 0f && two[1] > 0f, $"mark 1 {V4(one, 0)}, mark 2 {V4(two, 0)}");
    }

    // SetupForActor records: no 16-light limit on the exported list (only the 256-record array, 0x10b097b0..0x10b097c0),
    // and the actor's own light is never a record (FUN_10b097b0 0x10b097e6..0x10b097ec). 20 dynamic lights around the
    // origin, all fresh (counter 0xff): 3 attach, the rest are rejected by the cap but keep a counter > 0 -> exported.
    // D-65: dynamic entries of the walk's volumetric list (UESceneNode.VolumetricDynamic) come after each leaf's static chain
    // (Render.dll.c:4642-4680), without a cull, each light once — the actor's +0xec Stamp is shared with the statics.
    // Leaves walked: 1 (chain 6, 7), then 3 (chain 8); dynamic links: light 100 at the 1st leaf, 101, 100 and 102 at the 2nd.
    // Export 7 is ObjectId 101 (stamped by the static chain first: its dynamic link is skipped), export 8 is ObjectId 100
    // (stamped by its dynamic link first: its static entry is skipped). A sprite marked after the 1st leaf sees only that leaf.
    static void FogDynamicLeaves()
    {
        var g = Geo(new[] { 5, 0, 6, 7, 0, 9, 0, 8, 0 }.SkipLast(1), new[] { 0, 2, 5, 7 });
        var fr = new UESceneNode();
        fr.VolumetricLeaves.AddRange(new[] { 1, 3 });
        fr.VolumetricDynamic.AddRange(new[] { (0, 100), (1, 101), (1, 100), (1, 102) });
        var at = new Dictionary<int, UEVector> { [6] = default, [7] = default, [8] = default };
        Func<int, (bool, UEVector, int)> lightOf = ex => at.TryGetValue(ex, out UEVector p) ? (true, p, 1) : (false, default, 0);
        var objectOf = new Dictionary<int, int> { [7] = 101, [8] = 100 };
        var dynAll = new HashSet<int>();
        var stAll = LevelFogMaps.FrameStaticVolumetric(g, fr, lightOf, -1, dynAll, objectOf);
        var dyn1 = new HashSet<int>();
        var st1 = LevelFogMaps.FrameStaticVolumetric(g, fr, lightOf, 1, dyn1, objectOf);
        string S(HashSet<int> h) => string.Join(",", h.OrderBy(x => x));
        Check("fog-dynamic-leaves", S(stAll) == "6,7" && S(dynAll) == "100,102" && S(st1) == "6,7" && S(dyn1) == "100",
            $"all: static [{S(stAll)}] dynamic [{S(dynAll)}]; mark 1: static [{S(st1)}] dynamic [{S(dyn1)}] (expected [6,7] [100,102] / [6,7] [100])");
        var fr2 = new UESceneNode();
        fr2.VolumetricLeaves.AddRange(new[] { 3, 1 });
        fr2.VolumetricDynamic.AddRange(new[] { (0, 101), (1, 100) });
        var dyn2 = new HashSet<int>();
        var st2 = LevelFogMaps.FrameStaticVolumetric(g, fr2, lightOf, -1, dyn2, objectOf);
        // The actor's vertex fog takes the dynamic light only when its sprite was marked after the leaf that appended it.
        var frame = Frame(default, default, true, Actor(100, new UEVector(300f, 0f, 0f), OtherZone, Light(64, 0, 0, 16, 3, 128, dynamic: true), 0));
        bool Fogged(int mark)
        {
            var t = new UESceneNode();
            t.VolumetricLeaves.AddRange(new[] { 0, 0 });
            t.VolumetricDynamic.Add((1, 100));
            t.Sprites.Add(new UESprite { Actor = Mesh, VolLeafMark = mark });
            var f = new ActorFogFrame(); f.Bind(new HashSet<int> { FogZone }, Geo(Array.Empty<int>()));
            f.Begin(frame, t, 1f);
            var o = new float[4];
            return f.Apply(Mesh.ObjectId, new[] { new UEVector(300f, 0f, 0f) }, o) && o[0] > 0f;
        }
        bool before = Fogged(1), after = Fogged(2);
        Check("fog-dynamic-apply", !before && after, $"sprite marked after leaf 1: fogged {before}; after leaf 2 (the link's): {after}");
        Check("fog-dynamic-stamp", S(st2) == "6,8" && S(dyn2) == "101",
            $"leaves 3 then 1: static [{S(st2)}] dynamic [{S(dyn2)}] (expected [6,8] [101]: 101 stamped first by its link, 7 skipped)");
    }

    // D-65: SetupForActor's dynamic candidates are the links of URender::LeafLights[actor leaf] (sprite +0xbc,
    // Render.dll.c:690-696, :21346-21372), not every dynamic light in range. Leaf 0's sublist holds export 50; light 2 is a
    // dynamic light in range without a link, light 3 one with a link (counter starts at 0xff), light 4 a pool light
    // outside the leaf sublist with a link.
    static void LightDispatched()
    {
        var lt = new UEActorLighting();
        lt.SetLeafChain(new[] { 0 }, new[] { 50, 0, 60, 0 });
        var p = new UEVector(10f, 0f, 0f);
        lt.BeginFrame(Frame(default, default, true,
            Actor(1, p, OtherZone, Light(64, 0, 0, 0, 0, 0), 50),
            Actor(2, p, OtherZone, Light(64, 0, 0, 0, 0, 0, dynamic: true), 0),
            Actor(3, p, OtherZone, Light(64, 0, 0, 0, 0, 0, dynamic: true), 0),
            Actor(4, p, OtherZone, Light(64, 0, 0, 0, 0, 0, dynamic: true), 60)), 0.0, true, 1f);
        var into = new UEActorLighting.Exported[UEActorLighting.ExportCap];
        int n = lt.Gather(99, default, 0, false, false, (_, _) => true, into, new[] { 4, 3 });
        string got = string.Join(",", into.Take(n).Select(e => e.ObjectId).OrderBy(x => x));
        Check("light-dispatched", got == "1,3,4", $"records [{got}], expected [1,3,4] (2 has no LeafLights link)");
    }

    static void LightRecords()
    {
        var actors = new List<ActorSnapshot>();
        for (int i = 0; i < 20; i++) actors.Add(Actor(100 + i, new UEVector(10f + i, 0f, 0f), OtherZone, Light(64, 0, 0, 0, 0, 0, dynamic: true), 0));
        var lt = new UEActorLighting();
        lt.BeginFrame(Frame(default, default, true, actors.ToArray()), 0.0, true, 1f);
        var into = new UEActorLighting.Exported[UEActorLighting.ExportCap];
        int n = lt.Gather(1, default, 0, false, false, (_, _) => true, into);
        Check("light-records-past-16", n == 20, $"exported {n} of 20 lights");
        int self = lt.Gather(100, default, 0, false, false, (_, _) => true, into);
        Check("light-records-no-self", self == 19 && !into.Take(self).Any(e => e.ObjectId == 100), $"exported {self}, own light among them: {into.Take(self).Any(e => e.ObjectId == 100)}");
    }

    // ROUND to even.
    static void FogSpecularByte()
    {
        float f = 2.5f / 255f;
        while (f * 255f < 2.5f) f = MathF.BitIncrement(f);
        while (f * 255f > 2.5f) f = MathF.BitDecrement(f);
        bool exact = f * 255f == 2.5f;
        Check("fog-byte-round-even", exact && ActorFogFrame.SpecularByte(f) == 2, $"exact={exact} byte={ActorFogFrame.SpecularByte(f)}");
        Check("fog-byte-scale", ActorFogFrame.SpecularByte(1f) == 255 && ActorFogFrame.SpecularByte(0f) == 0
            && ActorFogFrame.SpecularByte(0.5f) == 128 && ActorFogFrame.SpecularByte(100f / 255f) == 100,
            $"{ActorFogFrame.SpecularByte(1f)} {ActorFogFrame.SpecularByte(0.5f)} {ActorFogFrame.SpecularByte(100f / 255f)}");
    }

    // ── Opacity / blending ─────────────────────────────────────────────────

    static string D(ActorFxBlend b) => $"{b.Mode} {b.Src}/{b.Dst} z={b.ZWrite} test={b.AlphaTest} cut={b.Cutoff} tq={b.TransparentQueue} op={b.OpacityPalette} mp={b.MaskedPalette}";

    static void OpacityBlend()
    {
        // (style, slotFlags, opacity) -> expectation by hand from Resolve; cut = D3DRS_ALPHAREF 0x7f / 255 (GREATER).
        var cases = new (int style, int flags, float op, string want, string name)[]
        {
            (1, 0, 1f,   "Opaque One/Zero z=True test=False cut=0 tq=False op=False mp=False", "blend-opaque"),
            (2, 0, 1f,   "Masked One/Zero z=True test=True cut=0.49803922 tq=False op=False mp=True", "blend-style-masked"),
            (1, 2, 1f,   "Masked One/Zero z=True test=True cut=0.49803922 tq=False op=False mp=True", "blend-slot-masked"),
            (3, 0, 1f,   "Translucent One/InvSrcColor z=False test=False cut=0 tq=True op=False mp=False", "blend-style-translucent"),
            (1, 4, 1f,   "Translucent One/InvSrcColor z=False test=False cut=0 tq=True op=False mp=False", "blend-slot-translucent"),
            (1, 6, 1f,   "Translucent One/InvSrcColor z=False test=False cut=0 tq=True op=False mp=False", "blend-translucent-over-mask"),
            // The mask stays an alpha test under Modulated (SetBlending drops it only under Translucent, D3DDrv.dll.c:787-791).
            (4, 2, 1f,   "Modulated DestColor/SrcColor z=False test=True cut=0.49803922 tq=True op=False mp=True", "blend-modulated-over-mask"),
            // D3DDrv SetBlending: `& 4` before `& 0x40` (D3DDrv.dll.c:803-824) — Translucent wins over Modulated.
            (4, 4, 1f,   "Translucent One/InvSrcColor z=False test=False cut=0 tq=True op=False mp=False", "blend-translucent-over-modulated"),
            // PF_Modulated from the slot material (triangle flags = material | actor, Render.dll.c:9380, 14616).
            (1, 0x40, 1f, "Modulated DestColor/SrcColor z=False test=False cut=0 tq=True op=False mp=False", "blend-slot-modulated"),
            (1, 0, 0.4f, "AlphaBlend SrcAlpha/InvSrcAlpha z=False test=False cut=0 tq=True op=True mp=False", "blend-opacity"),
            (4, 0, 0.4f, "AlphaBlend SrcAlpha/InvSrcAlpha z=False test=False cut=0 tq=True op=True mp=False", "blend-opacity-over-modulated"),
            (3, 0, 0.3f, "AlphaBlend SrcAlpha/InvSrcAlpha z=False test=False cut=0 tq=True op=True mp=False", "blend-opacity-over-translucent"),
            (2, 0, 0.15f,"AlphaBlend SrcAlpha/InvSrcAlpha z=False test=False cut=0 tq=True op=True mp=True", "blend-opacity-masked-palette"),
            (1, 0, 0.999999f, "AlphaBlend SrcAlpha/InvSrcAlpha z=False test=False cut=0 tq=True op=True mp=False", "blend-opacity-just-below-1"),
        };
        foreach (var c in cases)
        {
            string got = D(ActorOpacity.Resolve(c.style, c.flags, c.op));
            Check(c.name, got == c.want, $"got [{got}] want [{c.want}]");
        }
        Check("opacity-vertex-alpha", ActorOpacity.VertexAlpha(1f) == 1f && ActorOpacity.VertexAlpha(0.3f) == 0.3f
            && ActorOpacity.VertexAlpha(1.5f) == 1f && ActorOpacity.VertexAlpha(0f) == 0f,
            $"{ActorOpacity.VertexAlpha(1f)} {ActorOpacity.VertexAlpha(0.3f)} {ActorOpacity.VertexAlpha(1.5f)}");
    }

    static void OpacityPaletteTest()
    {
        var pal = new byte[1024];
        for (int i = 0; i < 256; i++) { pal[i * 4] = (byte)i; pal[i * 4 + 1] = (byte)(255 - i); pal[i * 4 + 2] = 7; pal[i * 4 + 3] = (byte)(i * 3); }
        pal[3] = 200;
        var plain = ActorOpacity.OpacityPalette(pal, masked: false);
        var masked = ActorOpacity.OpacityPalette(pal, masked: true);
        bool plainOk = plain.AsSpan().SequenceEqual(pal);
        bool maskOk = masked[0] == 0 && masked[1] == 0 && masked[2] == 0 && masked[3] == 0 && masked.AsSpan(4).SequenceEqual(pal.AsSpan(4));
        Check("opacity-palette-alpha", plainOk && plain[3] == 200 && plain[5 * 4 + 3] == 15, $"plain={plainOk} a0={plain[3]}");
        Check("opacity-palette-masked", maskOk, $"masked0=({masked[0]},{masked[1]},{masked[2]},{masked[3]})");
    }

    // ── Fatness ──────────────────────────────────────────────────────────────

    static void Fatness()
    {
        Check("fat-offset", ActorFatness.Offset(128) == 0f && ActorFatness.Offset(144) == 1f && ActorFatness.Offset(0) == -8f
            && ActorFatness.Offset(255) == 7.9375f && ActorFatness.Offset(87) == 87 / 16f - 8f,
            $"{ActorFatness.Offset(144)} {ActorFatness.Offset(0)} {ActorFatness.Offset(255)} {ActorFatness.Offset(87)}");
        var pts = new[] { new UEVector(10f, 20f, 30f), new UEVector(-5f, 0f, 1f) };
        var nrm = new[] { new UEVector(0f, 0f, 1f), new UEVector(0.6f, 0.8f, 0f) };
        float d = ActorFatness.Apply(pts, nrm, 160);   // 160/16 − 8 = 2
        Check("fat-apply", d == 2f && pts[0].x == 10f && pts[0].y == 20f && pts[0].z == 32f
            && Math.Abs(pts[1].x - (-3.8f)) < 1e-6 && Math.Abs(pts[1].y - 1.6f) < 1e-6 && pts[1].z == 1f,
            $"d={d} p0={pts[0]} p1={pts[1]}");
        var keep = new[] { new UEVector(1f, 2f, 3f) };
        ActorFatness.Apply(keep, new[] { new UEVector(1f, 0f, 0f) }, 128);
        Check("fat-128-identity", keep[0].x == 1f && keep[0].y == 2f && keep[0].z == 3f, $"{keep[0]}");
    }
}
