// Tests for the render audit fixes (D-1..D-14).
// Expectations are source constants (address next to each check), not port output. How the rules reach the frame
// (blending, groups, alpha test, flags, placement, color) is judged by the draw log against the live original
// (tools/origprobe/drawlog_replay.sh, audit/drawlog_redtest.sh), not by searching the source for a string.
// Red mutations: redtest.sh next to this file.
using HP1.UEScript;
using HPEngine;
using HPEngine.Sprites;
using UELib;
using UELib.Core;

static class Program
{
    static int _fail, _pass;

    static void Check(string test, bool ok, string detail)
    {
        if (ok) { _pass++; return; }
        _fail++;
        Console.WriteLine($"FAIL {test}: {detail}");
    }

    // Snapshots as in SpritesTests (the same Core constructor).
    static ActorSnapshot Actor(int id, UEVector loc = default, int drawType = 1, string tex = null, int style = 1,
        float scaleGlow = 1f, float opacity = 1f, ParticleState emitter = default, bool noSmooth = false, bool latePass = false)
        => new ActorSnapshot(id, -1, -1, "A" + id, "Test", true, loc, default, default, default, 0, 0, -1,
            "-", "-", "-", "-", 0f, null, 0f, 0f, 0f, false, 0f, 0f, 0f, 0f, Array.Empty<string>(), false,
            drawType, null, tex, 1f, style, false, false, false, 0, scaleGlow, null, null, default, default, default, emitter,
            0, opacity: opacity, noSmooth: noSmooth, latePass: latePass);

    static ActorSnapshot UnlitActor(float scaleGlow, int ambientGlow, int zone, bool unlit)
        => new ActorSnapshot(1, -1, -1, "U", "Test", true, default, default, default, default, 0, zone, -1,
            "-", "-", "-", "-", 0f, null, 0f, 0f, 0f, false, 0f, 0f, 0f, 0f, Array.Empty<string>(), false,
            2, null, null, 1f, 1, false, unlit, false, ambientGlow, scaleGlow, null, null, default, default, default, default, 0);

    static WorldFrame Frame(IReadOnlyList<ActorSnapshot> actors, IReadOnlyList<ParticleInstance> particles = null)
        => new WorldFrame(1, "test", actors, new CameraSnapshot(true, 0, default, new UERotator(0, 0, 0), 90f, true),
            Array.Empty<AudioCommand>(), default, Array.Empty<HudCommand>(), particles ?? Array.Empty<ParticleInstance>(), 0,
            spriteActors: actors.Select(a => a.ObjectId).ToArray(),
            actorViews: ActorViewSets.From(actors.ToDictionary(a => a.ObjectId, _ => new List<int> { -1 })));   // master membership

    // Loud accounting of unhandled flags (hpengine/RenderUnhandled.cs): one line per combination, a counter per hit;
    // handled bits stay silent, unhandled ones do not. Route: the production path of SpriteBuilder/ParticleBuilder
    // (Build) and the structural calls of the other paths.
    static void CensusUnhandled()
    {
        var log = new List<string>();
        var prev = RenderUnhandled.Log;
        RenderUnhandled.Log = log.Add;
        RenderUnhandled.Reset();
        RenderUnhandled.Note("mover", "PF_Test", "a"); RenderUnhandled.Note("mover", "PF_Test", "b");
        Check("census.once", log.Count == 1 && log[0] == "[Render] unhandled: mover PF_Test a"
            && RenderUnhandled.Snapshot()["mover PF_Test"] == 2, string.Join(" | ", log));
        RenderUnhandled.Reset(); log.Clear();
        // Mirror + Translucent + Unlit (Erised mover 0x08400004), Semisolid (BSP build), BigWavy (D3D does not read it): silent.
        RenderUnhandled.Surface("mover", 0x08400004 | 0x20 | 0x1000, "erised");
        RenderUnhandled.MeshMaterial(0x102, "masked twosided");
        RenderUnhandled.SpriteTexture("sprite", 0x400002, "masked unlit");
        Check("census.handled_silent", log.Count == 0, string.Join(" | ", log));
        // PF_Environment (0x10) on a surface and a material, PF_NoSmooth on a sprite texture (0x800): loud.
        RenderUnhandled.Surface("bsp", 0x10 | 0x4, "env");
        RenderUnhandled.MeshMaterial(0x10, "envmat");
        RenderUnhandled.SpriteTexture("sprite", 0x800, "nosmooth");
        var snap = RenderUnhandled.Snapshot();
        Check("census.unhandled_loud", snap.ContainsKey("bsp PF_Environment") && snap.ContainsKey("mesh-material PF_Environment")
            && snap.ContainsKey("sprite PF_NoSmooth") && snap.Count == 3, string.Join(" | ", log));
        // Production route: SpriteBuilder.Build and ParticleBuilder.Build report a texture flag they do not read.
        RenderUnhandled.Reset(); log.Clear();
        var sb = new SpriteBuilder((string p, out int u, out int v) => { u = v = 8; return true; }, _ => 0x800, _ => false,
            _ => 1, (t, i) => t, _ => 1f);
        sb.Build(Frame(new[] { Actor(5, drawType: 1, tex: "T", loc: new UEVector(100, 0, 0)) }));
        Check("census.sprite_route", sb.Draws.Count == 1 && RenderUnhandled.Snapshot().ContainsKey("sprite PF_NoSmooth"), string.Join(" | ", log));
        // Census D-2: the AnimNext chain frame for particles/sprites/decals/skins is UETextureAnim.CurrentFrame (as BSP).
        // Census UNCLEAR: sprite AmbientGlow. DrawActorSprite (0x10b32850..0x10b338ac) reads only ScaleGlow (+0x17c)
        // from the actor, not +0x18c. Light277 (Tut1b): Style 3, ScaleGlow 0.5, AmbientGlow 200 -> color 0.5, census silent.
        RenderUnhandled.Reset(); log.Clear();
        var gsb = new SpriteBuilder((string p, out int u, out int v) => { u = v = 8; return true; }, _ => 0, _ => false,
            _ => 1, (t, i) => t, _ => 1f);
        var glowActor = new ActorSnapshot(9, -1, -1, "G", "Light", true, new UEVector(100, 0, 0), default, default, default, 0, 0, -1,
            "-", "-", "-", "-", 0f, null, 0f, 0f, 0f, false, 0f, 0f, 0f, 0f, Array.Empty<string>(), false,
            1, null, "T", 1f, 3, false, false, false, 200, 0.5f, null, null, default, default, default, default, 0);
        gsb.Build(Frame(new[] { glowActor }));
        Check("sprite.ambientglow_ignored", gsb.Draws.Count == 1 && gsb.Draws[0].Vertices[0].R == 0.5f && log.Count == 0,
            (gsb.Draws.Count == 1 ? gsb.Draws[0].Vertices[0].R.ToString("R") : gsb.Report.ToString()) + " | " + string.Join(" | ", log));
        RenderUnhandled.Log = prev;
        RenderUnhandled.Reset();
    }

    static int Main()
    {
        D1TranslucentBlend();
        D3FrameGroups();
        CensusUnhandled();
        // Empty mesh slot (Lev2_Inc_A GreenhouseHPlant0: material 0 -> Textures[1] empty, the original draws
        // GreenhouseHPlant1Tex0): disasm Render.dll 0x10b1174b-0x10b1183f takes the last non-null GetTexture of the
        // slots, overridden by Actor.Texture, Zone.EnvironmentMap, Level.EnvironmentMap (in that order).
        Check("d15.mesh_fallback", RenderRules.MeshFallbackTexture("None", null, "None", "P.Tex0") == "P.Tex0"
            && RenderRules.MeshFallbackTexture("A", "Z", "L", "P.Tex0") == "A" && RenderRules.MeshFallbackTexture(null, "Z", "L", "P.Tex0") == "Z"
            && RenderRules.MeshFallbackTexture("", "None", "L", "P.Tex0") == "L" && RenderRules.MeshFallbackTexture(null, null, null, null) == null,
            "Actor.Texture > Zone.EnvironmentMap > Level.EnvironmentMap > last slot texture");
        // D-13: Actor.bNoSmooth -> PF_NoSmooth (FUN_10b338f0, Render.dll.c:39787-39789) -> D3DDrv POINT (D3DDrv.dll.c:860-865).
        foreach (bool ns in new[] { true, false })
        {
            HP1.Core.UEClassRegistry.Shared ??= new HP1.Core.UEClassRegistry();
            var o = new UEObject { Class = new UELib.Core.UClass { Name = new UName("Actor") } };
            o.Props["bNoSmooth"] = ns;
            var snap = WorldFrameProjector.Project(o, outsideFrame: true);
            Check("d13.snapshot", snap.NoSmooth == ns, $"bNoSmooth={ns} -> NoSmooth={snap.NoSmooth}");
        }
        var nsb = new SpriteBuilder((string p, out int u, out int v) => { u = v = 8; return true; }, _ => 0, _ => false,
            _ => 1, (t, i) => t, _ => 1f);
        nsb.Build(Frame(new[] { Actor(8, drawType: 1, tex: "T", noSmooth: true, loc: new UEVector(100, 0, 0)), Actor(9, drawType: 1, tex: "T", loc: new UEVector(100, 0, 0)) }));
        Check("d13.sprite", nsb.Draws.Count == 2 && nsb.Draws[0].NoSmooth && !nsb.Draws[1].NoSmooth, nsb.Report.ToString());

        // Original / Enhanced world sampler (WorldFrame.EnhancedVisuals, [HPEngine] VisualMode, not original).
        // Original = D3DDrv: MIPFILTER POINT (UseTrilinear=False in the shipped Default.ini, 0x1000aed9), no
        // D3DTSS_MAXANISOTROPY; Enhanced = trilinear mips (D-48) + [HPEngine] Anisotropy (D-53, 0 = off).
        var orig = RenderRules.WorldSampling(false, 16);
        Check("world_sampling.original", !orig.mipLinear && orig.anisotropy == 0,
            $"Original: mip {(orig.mipLinear ? "LINEAR" : "POINT")}, anisotropy {orig.anisotropy} (D3DDrv: POINT, none)");
        var enh = RenderRules.WorldSampling(true, 16);
        var enhOff = RenderRules.WorldSampling(true, 0);
        Check("world_sampling.enhanced", enh.mipLinear && enh.anisotropy == 16 && enhOff.mipLinear && enhOff.anisotropy == 0,
            $"Enhanced: mip {(enh.mipLinear ? "LINEAR" : "POINT")}, anisotropy {enh.anisotropy} (16), with [HPEngine] Anisotropy=0 -> {enhOff.anisotropy}");

        // D-2: mirror = reflection, then the surface with its own texture when (flags & 0x47); pass 0 (Render.dll.c:6461-6482).
        Check("d2.mirror_surface", RenderRules.MirrorSurfaceDrawn(0x8100024) && RenderRules.MirrorSurfaceDrawn(0x8400004) && RenderRules.MirrorSurfaceDrawn(0x8000002)
            && !RenderRules.MirrorSurfaceDrawn(0x8400000), "MirrorSurfaceDrawn: Erised 0x8100024/0x8400004, Tut3 0x8400000");
        // A decal on a mover brush tracker surface (past the map surfaces: Harry's shadow on a pushed block, author's run
        // 2026-11-09) takes its surface's group from Core's flags; a map surface keeps the mesh's group.
        var mapGroups = new[] { RenderRules.OpaqueWorld, RenderRules.BlendedWorld };
        Check("decal.tracker_surface_group",
            RenderRules.DecalGroup(1, mapGroups, 0, 0) == RenderRules.BlendedWorld
            && RenderRules.DecalGroup(5, mapGroups, 0x4, 0) == RenderRules.BlendedWorld
            && RenderRules.DecalGroup(5, mapGroups, 0, 0x4) == RenderRules.BlendedWorld
            && RenderRules.DecalGroup(5, mapGroups, 0, 0) == RenderRules.OpaqueWorld
            && RenderRules.DecalGroup(5, mapGroups, RenderRules.PF_Mirrored | 0x4, 0) == RenderRules.OpaqueWorld,
            "tracker decal group from Surf | Texture PolyFlags, mirror opaque; map decal from the mesh");
        Check("d2.mirror_group", RenderRules.SurfaceGroup(0x8100024, true) == RenderRules.OpaqueWorld && RenderRules.SurfaceGroup(0x4, false) == RenderRules.BlendedWorld,
            "SurfaceGroup");
        // Child frames (level and mover mirrors, warp, sky) come from the Core frame (WorldFrame.Views, D-61); binding
        // lock. The frames themselves: SelfTest worldframe_views_from_occlusion (against the original's log); drawing:
        // --selftest-gpu; snapshots: audit/worldmesh_redtest.sh.

        // D-14: alpha test = PF_Masked bit, cleared only by PF_Translucent (D3DDrv SetBlending, D3DDrv.dll.c:787-791).
        Check("d14.alpha_test", RenderRules.AlphaTest(0x2) && RenderRules.AlphaTest(0x42) && !RenderRules.AlphaTest(0x6) && !RenderRules.AlphaTest(0x40)
            && !RenderRules.AlphaTest(0x46) && RenderRules.AlphaTest(0x10000002), "AlphaTest");

        // D-6 (mesh): D3DDrv DrawTriangles with PF_Modulated uses diffuse 0xFFFFFFFF (D3DDrv.dll.c:1017-1020): texel only.
        Check("d6.actor_frag", RenderRules.ActorFrag(true) == 1 && RenderRules.ActorFrag(false) == 2, "ActorFrag");
        // D-5: GUnlitColor = clamp(ScaleGlow·0.5 + AmbientGlow/256, 0, 1) (Render.dll.c:9113-9131), no zone ambient and
        // no pulse; PF_Unlit = bUnlit || ZoneNumber == 0 || !ViewLit (FUN_10b338f0, :39800-39804).
        foreach (var (sg, ag, want) in new[] { (1f, 0, 0.5f), (0.5f, 128, 0.75f), (1f, 255, 1f), (2.5f, 0, 1f), (-1f, 0, 0f), (0f, 255, 255f / 256f) })
        {
            var ua = UnlitActor(sg, ag, zone: 3, unlit: true);
            Check("d5.unlit_color", Math.Abs(HP1.UEScript.UEActorLighting.UnlitColor(ua) - want) < 1e-6f, $"SG={sg} AG={ag}: {HP1.UEScript.UEActorLighting.UnlitColor(ua)}, expected {want}");
        }
        Check("d5.unlit_flag", HP1.UEScript.UEActorLighting.UnlitFlag(UnlitActor(1f, 0, 3, true), true) && HP1.UEScript.UEActorLighting.UnlitFlag(UnlitActor(1f, 0, 0, false), true)
            && HP1.UEScript.UEActorLighting.UnlitFlag(UnlitActor(1f, 0, 3, false), false) && !HP1.UEScript.UEActorLighting.UnlitFlag(UnlitActor(1f, 0, 3, false), true), "UnlitFlag");
        // Core path: the point light of an unlit actor = GUnlitColor on all channels (FUN_10b0d3c0 :22074-22083), no zone ambient.
        var Lu = new float[6];
        HP1.UEScript.UEActorLighting.LightPoints(UnlitActor(0.5f, 128, 3, true), new HP1.UEScript.UEActorLighting.ActorInputs { Amb = (0.3f, 0.3f, 0.3f), Glow = 0.4f },
            new UEVector[2], new UEVector[2], 2, Array.Empty<HP1.UEScript.UEActorLighting.Exported>(), 0, default, Lu, true);
        Check("d5.light_points", Lu.All(x => Math.Abs(x - 0.75f) < 1e-6f), string.Join(",", Lu));
        // bMeshEnviroMap -> PF_Environment (FUN_10b338f0, Render.dll.c:39793); environment UV/light in FUN_10b0d3c0
        // (disasm 0x10b0d3f9-0x10b0d621): T = reflection of the view ray D about the world normal N; u = (T.x+1)/2,
        // v = (T.y+1)/2, light = pow(max(T.z,0), 0.25). Eye at the origin, point on +X (D = (1,0,0)); expectations by hand.
        float h = MathF.Sqrt(0.5f);
        foreach (var (n, wu, wv, wl) in new[] {
            (new UEVector(-h, 0, h), 0.5f, 0.5f, 1f),        // T = (0,0,1)
            (new UEVector(h, 0, -h), 0.5f, 0.5f, 1f),        // the sign of N does not matter
            (new UEVector(-h, h, 0), 0.5f, 1f, 0f),          // T = (0,1,0)
            (new UEVector(0, 0, 1), 1f, 0.5f, 0f),           // grazing ray: T = D
            (new UEVector(-1, 0, 0), 0f, 0.5f, 0f) })        // T = (-1,0,0)
        {
            var (u, v, l) = EnviroMapping.Of(new UEVector(100, 0, 0), n, default);
            Check("env.formula", Math.Abs(u - wu) < 1e-5f && Math.Abs(v - wv) < 1e-5f && Math.Abs(l - wl) < 1e-5f,
                $"N={n}: ({u},{v},{l}), expected ({wu},{wv},{wl})");
        }
        var (u4, _, l4) = EnviroMapping.Of(new UEVector(0, 0, 0), new UEVector(-h, 0, h), new UEVector(-50, 0, 0));
        Check("env.formula", Math.Abs(u4 - 0.5f) < 1e-5f && Math.Abs(l4 - 1f) < 1e-5f, $"eye (-50,0,0): u={u4} l={l4}");
        // N = (-√3/2, 0, 1/2): T = (-0.5, 0, √3/2), u = 0.25, light = (√3/2)^0.25.
        var (u5, _, l5) = EnviroMapping.Of(new UEVector(100, 0, 0), new UEVector(-0.8660254f, 0, 0.5f), default);
        Check("env.formula", Math.Abs(u5 - 0.25f) < 1e-5f && Math.Abs(l5 - MathF.Pow(0.8660254f, 0.25f)) < 1e-5f, $"30°: u={u5} l={l5}");
        // Corona ray: SingleLineCheck with TraceFlags 6 (level + movers), FUN_10b26f10; a mirror mover hides the corona.
        Console.WriteLine($"RenderAuditTests: passed {_pass}, failed {_fail}");
        return _fail == 0 ? 0 : 1;
    }

    // D-1: D3DDrv SetBlending (D3DDrv.dll.c:806-823) — PF_Translucent: RS 0x13 (SRCBLEND) = 2, 0x14 (DESTBLEND) = 4.
    static void D1TranslucentBlend()
    {
        var want = (src: 2, dst: 4);
        Check("d1.world_translucent", RenderRules.Blend(2) == want, $"mode 2 = {RenderRules.Blend(2)}, expected {want}");
        Check("d1.actor_translucent", RenderRules.Blend(4) == want, $"mode 4 = {RenderRules.Blend(4)}, expected {want}");
        Check("d1.modulated", RenderRules.Blend(3) == (9, 3), $"mode 3 = {RenderRules.Blend(3)}, expected (9, 3)");
        Check("d1.highlighted", RenderRules.Blend(6) == (5, 6), $"mode 6 = {RenderRules.Blend(6)}, expected (5, 6)");
    }

    // D-3, D-11: URender::DrawFrame (Render.dll.c:1108-1560): surface pass 1, actors with bVar13 = 0, pass 2,
    // actors with bVar13 = 1; pass = (PolyFlags | Texture.PolyFlags) & 0x47 ? 2 : 1 (:5927-5930, :5988);
    // bVar13 is Core's (ActorSnapshot.LatePass, judged by SelfTest sprite_pass_is_core_and_reaches_the_frame). A decal is
    // drawn in the surface loop (ClipDecal :1491), i.e. in the pass of its surface.
    static void D3FrameGroups()
    {
        Check("d3.group_order", RenderRules.OpaqueWorld < RenderRules.Actors && RenderRules.Actors < RenderRules.BlendedWorld
            && RenderRules.BlendedWorld < RenderRules.LateActors && RenderRules.Groups == 4, "frame group order");
        foreach (var (flags, want) in new[] { (0, 0), (0x2, 2), (0x4, 2), (0x40, 2), (0x1, 2), (0x80, 0), (0x100, 0), (0x8000000, 0), (0x800, 0), (0x400000, 0) })
            Check("d3.world_pass", RenderRules.WorldGroup(flags) == want, $"WorldGroup(0x{flags:x}) = {RenderRules.WorldGroup(flags)}, expected {want}");
        foreach (var (late, want) in new[] { (false, 1), (true, 3) })
            Check("d3.actor_pass", RenderRules.ActorGroup(late) == want, $"ActorGroup({late}) = {RenderRules.ActorGroup(late)}, expected {want}");
        // The actor snapshot reaches SpriteDraw (the group comes from the LatePass of the actor/emitter ITSELF).
        var sb = new SpriteBuilder((string p, out int u, out int v) => { u = v = 8; return true; }, _ => 0, _ => false,
            _ => 1, (t, i) => t, _ => 1f);
        sb.Build(Frame(new[] { Actor(7, drawType: 1, tex: "T", style: 3, opacity: 0.5f, loc: new UEVector(100, 0, 0), latePass: true) }));
        Check("d3.sprite_carries_style", sb.Draws.Count == 1 && sb.Draws[0].LatePass, sb.Report.ToString());
    }
}
