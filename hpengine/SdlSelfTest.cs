// --selftest-gpu <out.png>: SDL_GPU pipeline check without game files.
// Synthetic scenes are loaded into the same Renderer and drawn by the same Draw (mirror passes, pipelines,
// samplers, Camera.Rows matrix, FU/DL uniforms, the selected backend's shader blob); the frame is read back and
// compared with bytes computed here from the shader formulas, not from the frame itself.
//
// Scene 1 (384×128, three 128×128 panels; written to <out.png>):
//   panel 0, world batches: A lit quad saturate(t·L/63.75) (strict point StrictLm) with multipliers ≈×0.99/×0.5/×2.5;
//     B mask (alpha 129 stays, 127 is cut); C unlit; D mip (LOD ≈ 0.75: with bias −0.5 level 0, without it 1);
//     G background farther in depth, drawn last (depth test); quarters encode the clip X/Y sign and UV;
//   panel 1, actors via SetActors: E1/E2 and unlit F1/F2, mode 2 saturate(t·light) (unlit light = GUnlitColor);
//   panel 2, H fog over a lit surface saturate(lit)·(1−fog.a)+fog.rgb (quarter 254·96/64 > 1, so clamping before
//     fog is distinguishable); U UV shift (uvoff = (0.5, 0)).
//   Under HP1_DBG=red|tex|lm expectations are recomputed per debug branch (Draw passes only red to actors).
//   Also: the HUD pipeline is created from the device-format blob (EnsureHud).
// Scene 2 (128×128, <out>.mirror.png), mirror (mode 4): plane X = 300 facing the camera; quad Q behind the camera
//   (X = −100) is visible only in the reflected pass; quad Z behind the mirror (X = 500) is cut by the clip plane in
//   the reflected pass (otherwise it would cover Q) and hidden by the mirror's depth in the main pass.
//   <out>.mirror-mover.png: the same mirror as a mover batch (ActorDraw.MirrorPlane + SetMoverMirrors, DrawActors path).
// Scene 2b (128×128, <out>.warp.png), warp portal: invisible portal X = 300 with a warp zone behind it; quad W on the
//   other side (WarpCoords B = A + (0, 5000, 0)) is visible only in the child view; Near (other side, in front of the
//   portal) is cut by the child's NearClip; Behind (behind the portal in the camera world) is hidden by portal depth.
// (Scene 3, the shader DL light, is gone: dynamic lights on BSP are in Core's lightmap, LevelSurfLightmap +0x48.)
// Scene 4 (640×128, <out>.actorfx.png), ActorFx: vertex fog and Opacity < 1 (actor batches via SetActors):
//   panel 0, fog on a lit mesh saturate(t·light + fog): K1 Opaque, K2 Masked (saturating);
//   panel 1, fog on unlit: the same mode 2 saturate(t·light + fog), unlit light = GUnlitColor (U1, U2);
//   panel 2, fog present in vertices but NOT applied: NT Translucent (ONE, 1−SRC), NM Modulated (2·src·dst),
//     NA AlphaBlend, over the known world background bg2;
//   panel 3, Opacity 0.5 over the opaque actor OB (it is LAST in the list: the Opacity < 1 quads are in the late
//     actor group, Core's pass 2, so they are drawn after it; the renderer itself never reorders):
//     O1 palette alpha 255, O2 palette alpha 128 (t.a·A), O3 index 0 of a masked slot (0,0,0,0), O4 index 0 without
//     mask (palette as is);
//   panel 4, O5 bMasked texture in an unmasked slot (index 0 transparent), Z: AlphaBlend in front of Translucent
//     (inserted later and farther); AlphaBlend has no Z write, both blend.
//   AlphaBlend textures are P8 via Renderer.ExpandP8 by variant key (TexKey), as in game.
// Scene 5 (512×128, <out>.order.png), URender::DrawFrame frame groups (RenderRules: opaque world -> actors ->
//   blended world -> late actors) and world PF_Translucent blending ONE/INVSRCCOLOR:
//   panel 0, W0 translucent world IN FRONT of opaque actor A0 (the actor is drawn first, water lies over it);
//   panel 1, W1 translucent world over an opaque background bg (W + bg·(1−W), not W + bg); in RD a quad Core did not
//     draw (absent from WorldFrame.Drawn, e.g. its back side), nearest: not drawn, background visible; in RB (MV) two
//     blended-group mover surfaces, Translucent (nearer) listed before Masked (farther): the world groups of the dynamic
//     list draw opaque/masked first, so the translucent lies over the masked (MT + MK·(1−MT));
//   panel 2, decal DC (Modulated, opaque-world group) and translucent sprite S2 over it: the sprite precedes the
//     decal in the list, but the decal is drawn with its surface, before actors;
//   panel 3, background -> Modulated actor M3 (normal group) -> translucent world W3 -> late translucent actor T3
//     (Style 3, first in the list); in RD world Masked+Modulated with a transparent texel: cut by the alpha test.
//   Lit Modulated keeps its lightmap (D3DDrv DrawComplexSurface multitexture pass SetBlending(PolyFlags | 0x1000000) for
//   any PolyFlags, D3DDrv.dll.c:5775-5776, 6135-6137): 2·saturate(t·L/63.75)·bg — WM level surface (panel 2 RD), MM mover (panel 3 RB).
//   WF (panel 2 RB): the same lit Modulated surface with a fog map; D3DDrv's fog pass SetBlending(0x10000000) ONE/INVSRCALPHA
//   over the stored modulated result (D3DDrv.dll.c:6258-6265): Q(2·saturate(t·L/63.75)·bg)·(1 − a) + rgb.
// Scene 6 (128×128, <out>.sampler-original.png / .sampler-enhanced.png), Original / Enhanced world sampler
//   (Renderer.EnhancedVisuals = WorldFrame.EnhancedVisuals, not original): the smooth (not NoSmooth) mip quad of scene 1 D
//   (LOD ≈ 0.75, bias −0.5 -> 0.25), level 0 M0, level 1 M1. Original (D3DDrv MIPFILTER POINT) must give level 0
//   exactly; Enhanced (trilinear, D-48) must blend in level 1 (exact weight is the GPU's LOD, so a range is checked).
//   The pixel check runs with anisotropy off (anisotropic mip choice is driver-defined, D-53); sampler-anisotropy checks
//   the created samplers' create info: Enhanced LINEAR + anisotropy 16, Original NEAREST + none.
// Scene 1 again (<out>.gamma.png), Brightness 0.7: the same frame through the gamma pass (D3DDrv Flush ramp).
// Scene 1 again (<out>.hud.png), HUD tile sampling: a Smooth quad (coronas: D3DDrv DrawTile MAG/MINFILTER LINEAR,
//   HudQuad.Smooth) and a nearest one (UI) over the same 2×1 texture, consecutive with the same blend and texture.
// Scenes 2–4, gamma and HUD sampling run only without HP1_DBG.
//
// Tolerance TOL = 1 byte, for float -> UNORM8 rounding in the target and float32 shader arithmetic vs double
// expectations (mode 3 pow, fog, DL light). Filtering contributes nothing by construction: the main world texture is
// point-sampled (NoSmooth) over uniform texels/mips; actors (linear sampler) use a 1×1 texture; lightmap and fog
// (linear) are sampled at the centre of a uniform 4×4 block or at corner (0,0) with clamping; the mirror is sampled
// at the target texel centre; vertex light is constant per quad; checked pixels are at least MarginPx away from any
// quad or texel boundary.
using System.Runtime.InteropServices;
using HP1.UEScript;
using SDL;
using static SDL.SDL3;

namespace HPEngine
{
    sealed unsafe partial class Renderer
    {
        // Synthetic load instead of Upload: geometry, textures, atlases and mirror planes are given by the test;
        // _draws order is the test order. actorTextures: actor textures by path (SetActors takes them via TextureFor
        // from the same dictionary as in game).
        internal void LoadSynthetic(Vertex[] verts, uint[] idx, (int w, int h, byte[] rgba) atlas, (int w, int h, byte[] rgba) fog,
            List<(Batch b, (int w, int h, byte[] rgba)[] levels)> draws, Dictionary<string, (int w, int h, byte[] rgba)> actorTextures,
            List<(UEVector P, UEVector N)> mirrorPlanes, List<WarpPortal> portals = null)
        {
            _vb = Buffer(SDL_GPUBufferUsageFlags.SDL_GPU_BUFFERUSAGE_VERTEX, MemoryMarshal.AsBytes(verts.AsSpan()));
            _idx = idx;
            // Every world quad is model node = its draw index (one run); by default Core drew every node in every frame
            // (8 frames); a scene that needs Core's per-view sets gives them with SetDrawn.
            _nodeCount = draws.Count;
            for (int i = 0; i < draws.Count; i++) { draws[i].b.Runs.Clear(); draws[i].b.Runs.Add((i, i, 0, (int)draws[i].b.Count)); }
            var all = new DrawnWorld(Enumerable.Range(0, draws.Count).ToArray(), Array.Empty<ViewSource>(), Array.Empty<int>());
            _drawn = Enumerable.Repeat(all, 8).ToArray();
            _atlasH = atlas.h;
            _atlas = Tex(SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM, atlas.w, atlas.h, 1, SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_SAMPLER);
            UploadLevels(_atlas, new[] { atlas });
            _fogW = fog.w; _fogH = fog.h;
            UploadLevels(FogTex(0), new[] { fog });
            foreach (var (b, levels) in draws)
            {
                var tex = Tex(SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM, levels[0].w, levels[0].h, levels.Length, SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_SAMPLER);
                UploadLevels(tex, levels);
                _draws.Add((b, (nint)tex));
            }
            foreach (var (name, l) in actorTextures)
            {
                var tex = Tex(SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM, l.w, l.h, 1, SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_SAMPLER);
                UploadLevels(tex, new[] { l });
                _textures[name] = (nint)tex;
            }
            _batches.Clear(); foreach (var d in _draws) _batches.Add(d.b);
            _uvOff = new float[_draws.Count * 2]; _animTex = new nint[_draws.Count];
            _levelMirrors = mirrorPlanes?.Count ?? 0;
            _portalOfSurf.Clear();
            for (int i = 0; i < (portals?.Count ?? 0); i++) _portalOfSurf[portals[i].Surf] = i;
        }

        // Batch UV shift: what AnimateWorld puts into _uvOff from SurfacePan (the SurfacePan formula itself is not
        // checked here; the uvoff -> shader path is).
        internal void SetUvOffset(int draw, float u, float v) { _uvOff[draw * 2] = u; _uvOff[draw * 2 + 1] = v; }

        internal bool HudReady() => EnsureHud();

        // Synthetic HUD tile texture under the key and RGBA upload of HudTexture's package path.
        internal void LoadSyntheticHud(string path, int w, int h, byte[] rgba)
        {
            var tex = Tex(SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM, w, h, 1, SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_SAMPLER);
            UploadLevels(tex, new[] { (w, h, rgba) });
            _hudTex["hud|" + path] = (nint)tex;
        }
        internal bool SkyDrawn => SkyPasses > 0;

        // Synthetic P8 texture: all draw variants by the same key and expansion as TextureFor.
        internal void LoadSyntheticP8(string path, UETextureSource.TextureData d)
        {
            foreach (TexDraw v in Enum.GetValues<TexDraw>())
            {
                var levels = ExpandP8(d, v);
                var tex = Tex(SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM, levels[0].Item1, levels[0].Item2, levels.Length, SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_SAMPLER);
                UploadLevels(tex, levels);
                _textures[TexKey(path, v)] = (nint)tex;
            }
        }
    }

    static class GpuSelfTest
    {
        const int N = 128, Tol = 1, MarginPx = 3;
        const int AtlasW = 8, AtlasH = 8;

        // Scene 1 bytes (RGBA). Lit quad: T·L/63.75 (MODULATE2X over the RGBA7·2 lightmap, D3DDrv). Quarter 3 of A is
        // a strict point: R = 255·63/63.75 = 252 exactly; a divisor of 64 would give 251.02 -> 251 (StrictLm, tolerance 0).
        static readonly byte[] LA = { 63, 32, 160 }, LB = { 128, 64, 32 }, LRest = { 1, 2, 3 };
        static readonly byte[][] T1 = { new byte[] { 200, 100, 50, 255 }, new byte[] { 40, 80, 120, 255 }, new byte[] { 254, 2, 128, 255 }, new byte[] { 255, 20, 30, 255 } };
        static readonly byte[][] T2 = { new byte[] { 60, 90, 200, 255 }, new byte[] { 100, 50, 250, 129 }, new byte[] { 30, 40, 50, 127 }, new byte[] { 70, 80, 90, 0 } };
        static readonly byte[][] T3 = { new byte[] { 11, 22, 33, 255 }, new byte[] { 44, 55, 66, 255 }, new byte[] { 77, 88, 99, 255 }, new byte[] { 255, 128, 1, 255 } };
        static readonly byte[] G = { 17, 34, 51, 255 }, M0 = { 0, 200, 0, 255 }, M1 = { 0, 0, 200, 255 };
        static readonly byte[] Fog = { 40, 20, 10, 128 };   // fog texel with alpha (block x ≥ 4 of the fog atlas)

        // Rectangles in panel coordinates (−1..1; x0, x1, y0, y1; y1 is the top).
        static readonly float[] RA = { -0.9f, -0.1f, 0.1f, 0.9f }, RB = { 0.1f, 0.9f, 0.1f, 0.9f },
            RC = { -0.9f, -0.1f, -0.9f, -0.1f }, RD = { 0.1f, 0.9f, -0.9f, -0.1f }, RG = { 0.05f, 0.95f, -0.95f, 0.95f };
        // Mip quad: 4 texels × 21.5 repeats over 0.8 panel = 51.2 px -> 1.68 texels/px, LOD ≈ 0.748.
        const float MipRepeat = 21.5f;

        sealed record ActorQuad(string Name, float[] Rect, bool Unlit, byte[] Tex, float[] Light);
        static readonly ActorQuad[] Actors =
        {
            new("E1", RA, false, new byte[] { 200, 100, 50, 255 }, new[] { 1.5f, 0.5f, 2.0f }),
            new("E2", RB, false, new byte[] { 40, 90, 128, 255 }, new[] { 0.25f, 2.0f, 1.0f }),
            new("F1", RC, true, new byte[] { 200, 100, 50, 255 }, new[] { 1.0f, 1.0f, 1.0f }),
            new("F2", RD, true, new byte[] { 128, 64, 32, 255 }, new[] { 0.5f, 1.25f, 2.0f }),
        };

        // ---- construction ----

        sealed class Scene
        {
            public readonly int Panels;
            public readonly List<Vertex> V = new();
            public readonly List<uint> I = new();
            public readonly List<(Batch, (int, int, byte[])[])> Draws = new();
            public readonly Dictionary<string, (int, int, byte[])> ActorTex = new();
            public readonly List<ActorDraw> Actors = new();
            public Scene(int panels) { Panels = panels; }

            // Camera at the origin, looking along UE +X, FOV 90 (sx = 1), aspect = Panels (sy = Panels):
            // ndc x = Y/X, ndc y = Panels·Z/X. Panel point (px, py) at depth X = d; proj is the depth used for the
            // projection (≠ d only for a quad seen through the mirror).
            public Vertex P(int panel, float px, float py, float d, float proj, float u, float v)
            {
                float nx = (px + 2 * panel + 1 - Panels) / Panels;
                return new Vertex { X = d, Y = nx * proj, Z = py * proj / Panels, U = u, V = v };
            }

            public void Quad(List<Vertex> v, List<uint> idx, int panel, float[] r, float d, float span, float u2, float v2,
                float u3, float v3, float[] light, out uint first, out uint count, float proj = 0)
            {
                if (proj == 0) proj = d;
                uint b0 = (uint)v.Count;
                Vertex[] q = { P(panel, r[0], r[3], d, proj, 0, 0), P(panel, r[1], r[3], d, proj, span, 0), P(panel, r[0], r[2], d, proj, 0, span), P(panel, r[1], r[2], d, proj, span, span) };
                for (int k = 0; k < 4; k++)
                {
                    q[k].U2 = u2; q[k].V2 = v2; q[k].U3 = u3; q[k].V3 = v3;
                    if (light != null) { q[k].R = light[0]; q[k].G = light[1]; q[k].B = light[2]; }
                    v.Add(q[k]);
                }
                first = (uint)idx.Count; count = 6;
                idx.AddRange(new[] { b0, b0 + 1, b0 + 3, b0, b0 + 3, b0 + 2 });
            }

            public int World(Batch b, int panel, float[] rect, float d, float span, float u2, float v2, float u3, float v3,
                (int, int, byte[])[] levels, float proj = 0)
            {
                Quad(V, I, panel, rect, d, span, u2, v2, u3, v3, null, out b.First, out b.Count, proj);
                Draws.Add((b, levels));
                return Draws.Count - 1;
            }

            public void Actor(string name, int panel, float[] rect, bool unlit, byte[] tex, float[] light)
            {
                ActorTex["selftest/" + name] = (1, 1, Fill(1, 1, tex));
                var ad = new ActorDraw { Texture = "selftest/" + name, Blend = ActorBlend.Opaque, Unlit = unlit };
                var av = new List<Vertex>(); var ai = new List<uint>();
                Quad(av, ai, panel, rect, 100, 1, 0, 0, 0, 0, light, out _, out _);
                ad.Vertices.AddRange(av); ad.Indices.AddRange(ai);
                Actors.Add(ad);
            }

            // ActorFx actor batch: light, fog (specular bytes / 255), vertex alpha, depth.
            public void ActorFx(string tex, int panel, float[] rect, float depth, ActorBlend blend, bool unlit, bool opacityMasked,
                float[] light, byte[] fog, float alpha)
            {
                // Group as Core's pass gives it (UESpriteSort.Pass: Translucent style or Opacity < 1 -> pass 2, LateActors).
                var ad = new ActorDraw { Texture = tex, Blend = blend, Unlit = unlit, OpacityMasked = opacityMasked, AlphaTest = blend == ActorBlend.Masked,
                    Group = RenderRules.ActorGroup(blend is ActorBlend.AlphaBlend or ActorBlend.Translucent) };
                var av = new List<Vertex>(); var ai = new List<uint>();
                Quad(av, ai, panel, rect, depth, 1, 0, 0, 0, 0, light, out _, out _);
                for (int k = 0; k < av.Count; k++)
                {
                    Vertex x = av[k];
                    x.FogR = fog[0] / 255f; x.FogG = fog[1] / 255f; x.FogB = fog[2] / 255f; x.A = alpha;
                    av[k] = x;
                }
                ad.Vertices.AddRange(av); ad.Indices.AddRange(ai);
                Actors.Add(ad);
            }

            public readonly Dictionary<string, UETextureSource.TextureData> P8 = new();

            public int Anisotropy = 16;   // as Renderer's default ([HPEngine] Anisotropy)

            public Renderer Load(byte[] atlas, byte[] fog, List<(UEVector, UEVector)> mirrors, List<WarpPortal> portals = null)
            {
                var r = new Renderer { Anisotropy = Anisotropy };
                if (!r.Create(false, Panels * N, N)) return null;
                r.LoadSynthetic(V.ToArray(), I.ToArray(), (AtlasW, AtlasH, atlas), (AtlasW, AtlasH, fog), Draws, ActorTex, mirrors ?? new(), portals);
                foreach (var (path, d) in P8) r.LoadSyntheticP8(path, d);
                r.SetActors(Actors);
                return r;
            }
        }

        static byte[] Fill(int w, int h, byte[] c) { var a = new byte[w * h * 4]; for (int i = 0; i < w * h; i++) c.AsSpan(0, 4).CopyTo(a.AsSpan(i * 4)); return a; }
        static byte[] Tex2x2(byte[][] t) { var a = new byte[16]; for (int i = 0; i < 4; i++) t[i].CopyTo(a, i * 4); return a; }
        static byte[] Rgba(byte[] c) => c.Length == 4 ? c : new byte[] { c[0], c[1], c[2], 255 };

        // 8×8 atlas of 4×4 blocks: (x<4, y<4), (x≥4, y<4), y≥4.
        static byte[] Atlas(byte[] left, byte[] right, byte[] rest)
        {
            var a = new byte[AtlasW * AtlasH * 4];
            for (int y = 0; y < AtlasH; y++)
                for (int x = 0; x < AtlasW; x++)
                    Rgba(y < 4 ? (x < 4 ? left : right) : rest).CopyTo(a, (y * AtlasW + x) * 4);
            return a;
        }

        // ---- comparison ----

        delegate int[] ExpectFn(int panel, float sx, float sy, out string region);
        delegate bool EdgeFn(int panel, float sx, float sy);

        static int Compare(string name, Renderer r, int panels, byte[] rgba, ExpectFn expect, EdgeFn nearEdge, string[] regions,
            Func<string, bool> mayBeEmpty, string png, string dbg)
        {
            int w = panels * N;
            var checkedBy = new SortedDictionary<string, int>();
            foreach (string k in regions) checkedBy[k] = 0;
            int bad = 0, maxDev = 0, skipped = 0;
            for (int py = 0; py < N; py++)
                for (int px = 0; px < w; px++)
                {
                    int panel = px / N;
                    float sx = (px - panel * N + 0.5f) * 2f / N - 1f, sy = 1f - (py + 0.5f) * 2f / N;
                    if (nearEdge(panel, sx, sy)) { skipped++; continue; }
                    int[] e = expect(panel, sx, sy, out string region);
                    if (e is null) { skipped++; continue; }
                    checkedBy[region] = checkedBy.GetValueOrDefault(region) + 1;
                    int o = (py * w + px) * 4, dev = 0;
                    for (int c = 0; c < 3; c++) dev = Math.Max(dev, Math.Abs(rgba[o + c] - e[c]));
                    maxDev = Math.Max(maxDev, dev);
                    if (dev > Tol)
                    {
                        if (bad < 8) Console.WriteLine($"  MISMATCH {name}/{region} px ({px},{py}): frame ({rgba[o]},{rgba[o + 1]},{rgba[o + 2]}) expected ({e[0]},{e[1]},{e[2]})");
                        bad++;
                    }
                }
            Console.WriteLine($"selftest-gpu {name} {r.Driver} HP1_DBG={dbg ?? "-"}: pixels checked per region "
                + string.Join(", ", checkedBy.Select(kv => $"{kv.Key}={kv.Value}")) + $", skipped {skipped}; "
                + $"mismatches {bad}, max deviation {maxDev} (tolerance {Tol}) -> {png}");
            // A region that was not checked is not a PASS (its branch was not exercised by the comparison).
            string missing = string.Join(",", checkedBy.Where(kv => kv.Value == 0 && !mayBeEmpty(kv.Key)).Select(kv => kv.Key));
            if (missing.Length > 0) { Console.WriteLine($"selftest-gpu REFUSED: {name}: regions not checked {missing}"); return 3; }
            return bad == 0 ? 0 : 1;
        }

        static bool NearRect(float[] rc, bool mid, float nx, float ny)
        {
            float m = MarginPx * 2f / N;
            foreach (float x in new[] { rc[0], rc[1], mid ? (rc[0] + rc[1]) / 2 : float.NaN }) if (Math.Abs(nx - x) < m && ny > rc[2] - m && ny < rc[3] + m) return true;
            foreach (float y in new[] { rc[2], rc[3], mid ? (rc[2] + rc[3]) / 2 : float.NaN }) if (Math.Abs(ny - y) < m && nx > rc[0] - m && nx < rc[1] + m) return true;
            return false;
        }

        static bool In(float[] rc, float nx, float ny) => nx > rc[0] && nx < rc[1] && ny > rc[2] && ny < rc[3];
        static int Quarter(float[] rc, float nx, float ny) => (nx > (rc[0] + rc[1]) / 2 ? 1 : 0) + (ny < (rc[2] + rc[3]) / 2 ? 2 : 0);
        static int[] Rgb(byte[] c) => new int[] { c[0], c[1], c[2] };
        static int Byte(double v) => (int)Math.Round(Math.Clamp(v, 0.0, 1.0) * 255.0);
        static int[] Lit(byte[] t, byte[] l)
        {
            var o = new int[3];
            for (int c = 0; c < 3; c++) o[c] = (int)Math.Round(Math.Min(255.0, t[c] * l[c] / 63.75));
            return o;
        }
        static int[] Lm2(byte[] l) => new[] { Math.Min(255, l[0] * 2), Math.Min(255, l[1] * 2), Math.Min(255, l[2] * 2) };

        // ---- run ----

        public static int Run(string png)
        {
            string dbg = Environment.GetEnvironmentVariable("HP1_DBG");
            if (dbg == "") dbg = null;
            int rc = Scene1(png, dbg);
            if (rc == 2) return 2;
            if (dbg is null)
            {
                string stem = png.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? png[..^4] : png;
                rc = Worst(rc, SceneMirror(stem + ".mirror.png", mover: false));
                rc = Worst(rc, SceneMirror(stem + ".mirror-mover.png", mover: true));
                rc = Worst(rc, SceneWarp(stem + ".warp.png"));
                rc = Worst(rc, SceneSky(stem + ".sky.png"));
                rc = Worst(rc, SceneSkyChild(stem + ".sky-child.png"));
                rc = Worst(rc, SceneActorFx(stem + ".actorfx.png"));
                rc = Worst(rc, SceneOrder(stem + ".order.png"));
                rc = Worst(rc, SceneSampler(stem));
                rc = Worst(rc, SceneCull(stem + ".cull.png"));
                rc = Worst(rc, SceneUploads(stem + ".uploads.png"));
            }
            Console.WriteLine(rc == 0 ? "selftest-gpu PASS" : rc == 3 ? "selftest-gpu REFUSED" : rc == 2 ? "selftest-gpu NO GPU" : "selftest-gpu FAIL");
            return rc;
        }

        // Result priority: no GPU (2) > refusal (3) > failure (1) > success (0).
        static int Worst(int a, int b) => a == 2 || b == 2 ? 2 : a == 3 || b == 3 ? 3 : Math.Max(a, b);

        // ---- scene 1: world, actors, fog, UV shift ----

        static readonly float[] RH = RA, RU = RB;   // panel 2

        static int Scene1(string png, string dbg)
        {
            var s = new Scene(3);
            // Centres of the atlas 4×4 blocks: LA is texel (2,2), LB is (6,2); fog is block x ≥ 4: (6,2).
            s.World(new Batch { Blend = Blend.Opaque, NoSmooth = true }, 0, RA, 100, 1, 2f / AtlasW, 2f / AtlasH, 0, 0, new[] { (2, 2, Tex2x2(T1)) });
            s.World(new Batch { Blend = Blend.Opaque, NoSmooth = true, Unlit = true }, 0, RC, 100, 1, 0, 0, 0, 0, new[] { (2, 2, Tex2x2(T3)) });
            s.World(new Batch { Blend = Blend.Masked, NoSmooth = true, AlphaTest = RenderRules.AlphaTest(WorldMesh.PF_Masked) }, 0, RB, 100, 1, 6f / AtlasW, 2f / AtlasH, 0, 0, new[] { (2, 2, Tex2x2(T2)) });
            s.World(new Batch { Blend = Blend.Opaque, NoSmooth = true, Unlit = true }, 0, RD, 100, MipRepeat, 0, 0, 0, 0, new[] { (4, 4, Fill(4, 4, M0)), (2, 2, Fill(2, 2, M1)) });
            s.World(new Batch { Blend = Blend.Opaque, NoSmooth = true, Unlit = true }, 0, RG, 200, 1, 0, 0, 0, 0, new[] { (1, 1, Fill(1, 1, G)) });
            s.World(new Batch { Blend = Blend.Opaque, NoSmooth = true }, 2, RH, 100, 1, 2f / AtlasW, 2f / AtlasH, 6f / AtlasW, 2f / AtlasH, new[] { (2, 2, Tex2x2(T1)) });
            int uDraw = s.World(new Batch { Blend = Blend.Opaque, NoSmooth = true, Unlit = true }, 2, RU, 100, 1, 0, 0, 0, 0, new[] { (2, 2, Tex2x2(T3)) });
            foreach (ActorQuad a in Actors) s.Actor(a.Name, 1, a.Rect, a.Unlit, a.Tex, a.Light);

            Renderer r = s.Load(Atlas(LA, LB, LRest), Atlas(new byte[] { 0, 0, 0, 0 }, Fog, new byte[] { 0, 0, 0, 0 }), null);
            if (r is null) { Console.Error.WriteLine("GPU: " + SDL_GetError()); return 2; }
            r.SetUvOffset(uDraw, 0.5f, 0f);   // half a texture in U: the 2×2 columns swap
            bool hud = r.HudReady();
            Console.WriteLine($"selftest-gpu hud: pipeline from blob {(hud ? "created" : "NOT created")}");

            r.RenderOffscreen(new Camera { Fov = 90f }, out byte[] rgba);
            Png.Write(png, r.Width, r.Height, rgba);
            int rc = Compare("scene1", r, 3, rgba, (int panel, float sx, float sy, out string region) =>
                    panel == 0 ? ExpectWorld(sx, sy, dbg, out region) : panel == 1 ? ExpectActor(sx, sy, dbg, out region) : ExpectPanel2(sx, sy, dbg, out region),
                (panel, sx, sy) => panel switch
                {
                    0 => NearRect(RA, true, sx, sy) || NearRect(RB, true, sx, sy) || NearRect(RC, true, sx, sy) || NearRect(RD, false, sx, sy) || NearRect(RG, false, sx, sy),
                    1 => Actors.Any(a => NearRect(a.Rect, false, sx, sy)),
                    _ => NearRect(RH, true, sx, sy) || NearRect(RU, true, sx, sy),
                },
                new[] { "A", "B-keep", "B-cut", "C", "D", "G", "E1", "E2", "F1", "F2", "H", "U", "clear" },
                k => k == "B-cut" && dbg is not null,   // in debug modes the shader returns before the mask cut
                png, dbg);
            if (dbg is null) rc = Worst(rc, SceneGamma(r, rgba, png));
            if (dbg is null) rc = Worst(rc, SceneHudSampling(r, png));
            if (dbg is null) rc = Worst(rc, StrictLm(rgba));
            return hud ? rc : Worst(rc, 1);
        }

        // Lightmap divisor without tolerance: D3DDrv MODULATE2X(texel, lightmap RGBA7·2) = t·l7/63.75 (live original:
        // stage 1 COLOROP 5, hp1drawlog detail=1). Centre of quarter 3 of quad A: t = 255, l = 63 -> R exactly 252;
        // divisor 64 gives 251. The scene tolerance (1 byte) cannot see this difference, hence exact equality here.
        static int StrictLm(byte[] rgba)
        {
            float cx = ((RA[0] + RA[1]) / 2 + RA[1]) / 2, cy = (RA[2] + (RA[2] + RA[3]) / 2) / 2;
            int px = (int)((cx + 1f) * N / 2f), py = (int)((1f - cy) * N / 2f);
            int want = Lit(T1[3], LA)[0], got = rgba[(py * 3 * N + px) * 4];
            Console.WriteLine($"selftest-gpu lm-strict: px ({px},{py}) R frame {got} expected {want} (t·l/63.75, tolerance 0)");
            return got == want ? 0 : 1;
        }

        // ---- scene 1 again: brightness (D3DDrv gamma ramp) ----
        // The same frame with Brightness 0.7 (slider 7) via ScreenEffects.GammaExponent -> Renderer.GammaExponent.
        // Expectation is independent, per the UD3DRenderDevice::Flush disassembly (0x10002f42..0x10002fd6), from the
        // frame byte without gamma: ramp = fistp((float)(pow(i·(1/255)f, 1/(float)(B·2.5f))·65535)), output ramp/65535 as a byte.
        static int SceneGamma(Renderer r, byte[] plain, string png)
        {
            const float B = 0.7f;
            r.GammaExponent = HPEngine.Hud.ScreenEffects.GammaExponent(B);
            r.RenderOffscreen(new Camera { Fov = 90f }, out byte[] rgba);
            r.GammaExponent = 1f;
            string stem = png.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? png[..^4] : png;
            Png.Write(stem + ".gamma.png", r.Width, r.Height, rgba);
            double e = 1.0 / (float)(B * 2.5f);
            int bad = 0, maxDev = 0, changed = 0;
            for (int i = 0; i < plain.Length; i += 4)
                for (int c = 0; c < 3; c++)
                {
                    int ramp = (int)MathF.Round((float)(Math.Pow(plain[i + c] * (double)(1f / 255f), e) * 65535.0), MidpointRounding.ToEven);
                    int want = (int)Math.Round(ramp / 65535.0 * 255.0), dev = Math.Abs(rgba[i + c] - want);
                    if (Math.Abs(want - plain[i + c]) > Tol) changed++;
                    maxDev = Math.Max(maxDev, dev);
                    if (dev > Tol && bad++ < 8) Console.WriteLine($"  MISMATCH gamma px {i / 4}: frame {rgba[i + c]} expected {want} (without gamma {plain[i + c]})");
                }
            Console.WriteLine($"selftest-gpu gamma B={B}: channels {plain.Length / 4 * 3}, distinguishable by gamma {changed}, mismatches {bad}, max deviation {maxDev} (tolerance {Tol})");
            // A frame where gamma is indistinguishable from identity proves nothing.
            if (changed == 0) { Console.WriteLine("selftest-gpu REFUSED: gamma: the scene does not distinguish the ramp"); return 3; }
            return bad == 0 ? 0 : 1;
        }

        // ---- scene 1 again: HUD tile sampling ----
        // Quads 32×16 px, U 0..1 over a 2×1 texture (texel 0 black, texel 1 white), Opaque (branch 0, white colour):
        // S Smooth at x 0..32, P nearest at x 64..96. Column i samples u = (i + 0.5)/32: bilinear gives
        // 255·clamp(2u − 0.5, 0, 1) (texel centres 0.25 / 0.75, weights in 1/32 steps), nearest 0 below u = 0.5 and
        // 255 above. Checked columns 8..23 stay off the REPEAT wrap (u < 0.25 would blend texel 1 back in).
        static int SceneHudSampling(Renderer r, string png)
        {
            const string T = "selftest.hud2x1";
            r.LoadSyntheticHud(T, 2, 1, new byte[] { 0, 0, 0, 255, 255, 255, 255, 255 });
            r.SetHud(new[]
            {
                new HPEngine.Hud.HudQuad(0, 0, 32, 16, 0, 0, 1, 1, 1, 1, 1, 1, HPEngine.Hud.HudBlend.Opaque, false, T, HPEngine.Hud.HudQuadSource.Tile, -1, smooth: true),
                new HPEngine.Hud.HudQuad(64, 0, 96, 16, 0, 0, 1, 1, 1, 1, 1, 1, HPEngine.Hud.HudBlend.Opaque, false, T, HPEngine.Hud.HudQuadSource.Tile, -1, smooth: false),
            });
            r.RenderOffscreen(new Camera { Fov = 90f }, out byte[] rgba);
            r.SetHud(Array.Empty<HPEngine.Hud.HudQuad>());
            string stem = png.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? png[..^4] : png;
            Png.Write(stem + ".hud.png", r.Width, r.Height, rgba);
            int bad = 0, graded = 0;
            for (int i = 8; i < 24; i++)
            {
                double u = (i + 0.5) / 32.0;
                int wantS = (int)Math.Round(255 * Math.Clamp(2 * u - 0.5, 0, 1)), wantP = u < 0.5 ? 0 : 255;
                int gotS = rgba[(8 * r.Width + i) * 4], gotP = rgba[(8 * r.Width + 64 + i) * 4];
                if (wantS != 0 && wantS != 255 && wantS != wantP) graded++;
                if (Math.Abs(gotS - wantS) > Tol && bad++ < 8) Console.WriteLine($"  MISMATCH hud smooth col {i}: frame {gotS} expected {wantS}");
                if (gotP != wantP && bad++ < 8) Console.WriteLine($"  MISMATCH hud nearest col {i}: frame {gotP} expected {wantP}");
            }
            Console.WriteLine($"selftest-gpu hud sampling: columns 16, bilinear distinguishable from nearest {graded}, mismatches {bad} (tolerance {Tol})");
            if (graded == 0) { Console.WriteLine("selftest-gpu REFUSED: hud sampling: the scene does not distinguish bilinear from nearest"); return 3; }
            return bad == 0 ? 0 : 1;
        }

        // World per fs_main branch: red -> red; tex -> texel (no cut); lm -> lightmap ×2 (for unlit
        // uv2 = 0 -> atlas texel (0,0) = LA); otherwise Classic.
        static int[] ExpectWorld(float nx, float ny, string dbg, out string region)
        {
            byte[] tex = null, lm = null;
            bool unlit = false;
            region = "clear";
            if (In(RA, nx, ny)) { region = "A"; tex = T1[Quarter(RA, nx, ny)]; lm = LA; }
            else if (In(RC, nx, ny)) { region = "C"; tex = T3[Quarter(RC, nx, ny)]; unlit = true; }
            else if (In(RD, nx, ny)) { region = "D"; tex = M0; unlit = true; }   // bias −0.5: LOD 0.75 -> 0.25 -> level 0
            if (In(RB, nx, ny))
            {
                byte[] t = T2[Quarter(RB, nx, ny)];
                bool cut = dbg is null && t[3] < 128;   // 127/255 < 0.5 <= 129/255
                region = cut ? "B-cut" : "B-keep";
                if (!cut) { tex = t; lm = LB; }
            }
            if (tex is null && In(RG, nx, ny)) { if (region == "clear") region = "G"; tex = G; unlit = true; }
            if (tex is null) return new[] { 0, 0, 0 };
            return dbg switch
            {
                "red" => new[] { 255, 0, 0 },
                "tex" => Rgb(tex),
                "lm" => Lm2(unlit ? LA : lm),
                _ => unlit ? Rgb(tex) : Lit(tex, lm),
            };
        }

        // Actors: Draw passes only red from the debug modes; mode 2 is saturate(t·light).
        static int[] ExpectActor(float nx, float ny, string dbg, out string region)
        {
            region = "clear";
            foreach (ActorQuad a in Actors)
            {
                if (!In(a.Rect, nx, ny)) continue;
                region = a.Name;
                if (dbg == "red") return new[] { 255, 0, 0 };
                var o = new int[3];
                for (int c = 0; c < 3; c++)
                {
                    double t = a.Tex[c] / 255.0;
                    o[c] = Byte(t * a.Light[c]);   // unlit is in gamma too: light = GUnlitColor (UEActorLighting.UnlitColor), mode 2
                }
                return o;
            }
            return new[] { 0, 0, 0 };
        }

        // Panel 2. H: lit = t·L/63.75; fog: lit·(1 − a) + rgb (a, rgb = fog texel / 255).
        // U: unlit, uvoff = (0.5, 0) -> quarter q shows texel q^1. Debug branches ignore the shift.
        static int[] ExpectPanel2(float nx, float ny, string dbg, out string region)
        {
            region = "clear";
            if (In(RH, nx, ny))
            {
                region = "H";
                byte[] t = T1[Quarter(RH, nx, ny)];
                if (dbg == "red") return new[] { 255, 0, 0 };
                if (dbg == "tex") return Rgb(t);
                if (dbg == "lm") return Lm2(LA);
                var o = new int[3];
                for (int c = 0; c < 3; c++) o[c] = Byte(Math.Min(1.0, t[c] / 255.0 * LA[c] / 63.75) * (1.0 - Fog[3] / 255.0) + Fog[c] / 255.0);
                return o;
            }
            if (In(RU, nx, ny))
            {
                region = "U";
                int q = Quarter(RU, nx, ny);
                return dbg switch
                {
                    "red" => new[] { 255, 0, 0 },
                    "tex" => Rgb(T3[q]),
                    "lm" => Lm2(LA),
                    _ => Rgb(T3[q ^ 1]),
                };
            }
            return new[] { 0, 0, 0 };
        }

        // ---- scene 2: mirror ----

        static readonly float[] RMirror = { -0.6f, 0.6f, -0.6f, 0.6f }, RQ = { -0.3f, 0.3f, -0.3f, 0.3f };
        static readonly byte[] ZC = { 250, 0, 250, 255 };

        // mover: the same mirror as a mover batch (ActorDraw.MirrorPlane, plane via Renderer.SetMoverMirrors), DrawActors
        // path; no level planes. Same expectation: the mover node is drawn by the OccludeBsp mirror pass.
        static int SceneMirror(string png, bool mover)
        {
            var s = new Scene(1);
            // Mirror plane X = 300, normal towards the camera. Q behind the camera: projected in the reflected pass at
            // depth 600 − (−100) = 700. Z behind the mirror (X = 500): hidden by the mirror in the main pass.
            if (!mover)
                s.World(new Batch { Blend = Blend.Opaque, NoSmooth = true, Unlit = true, MirrorPlane = 0 }, 0, RMirror, 300, 1, 0, 0, 0, 0, new[] { (1, 1, Fill(1, 1, G)) });
            else
            {
                s.ActorTex["selftest/mirror"] = (1, 1, Fill(1, 1, G));
                // Drawn by the master frame only (MoverScene: one batch per view of Core's drawn mover polygons).
                var ad = new ActorDraw { Texture = "selftest/mirror", WorldMode = 0, Unlit = true, MoverAtlas = true, NoSmooth = true, FrameView = -1,
                    Group = RenderRules.SurfaceGroup(0x08000000, true), MirrorPlane = 0, PolyFlags = 0x08000000 };
                var av = new List<Vertex>(); var ai = new List<uint>();
                s.Quad(av, ai, 0, RMirror, 300, 1, 0, 0, 0, 0, null, out _, out _);
                ad.Vertices.AddRange(av); ad.Indices.AddRange(ai);
                s.Actors.Add(ad);
            }
            // Quads Q and Z: the scene checks mirror mechanics; which side of a polygon is drawn is Core's (WorldFrame.Drawn).
            s.World(new Batch { Blend = Blend.Opaque, NoSmooth = true, Unlit = true }, 0, RQ, -100, 1, 0, 0, 0, 0, new[] { (2, 2, Tex2x2(T3)) }, proj: 700);
            s.World(new Batch { Blend = Blend.Opaque, NoSmooth = true, Unlit = true }, 0, RQ, 500, 1, 0, 0, 0, 0, new[] { (1, 1, Fill(1, 1, ZC)) });
            var mirrors = new List<(UEVector, UEVector)> { (new UEVector(300, 0, 0), new UEVector(-1, 0, 0)) };
            Renderer r = s.Load(Atlas(LA, LB, LRest), Atlas(new byte[] { 0, 0, 0, 0 }, new byte[] { 0, 0, 0, 0 }, new byte[] { 0, 0, 0, 0 }), mover ? null : mirrors);
            if (r is null) { Console.Error.WriteLine("GPU: " + SDL_GetError()); return 2; }
            // The mirror child frame is a scene input (in game Core occlusion builds it): reflection in plane X = 300,
            // NearClip is the same plane, keeping the camera side.
            r.SetViews(new[] { new ViewFrame(-1, ViewFrame.KindMirror, 1, Affine.Reflect(new UEVector(-1, 0, 0), 300f), new UEVector(-1, 0, 0), 300f, true,
                new[] { new ViewSource(mover ? -1 : 0, mover ? 1 : 0, 0) }, default) }, _ => 0);
            // Core's drawn sets: the mirror frame does not draw the mirror itself (its own side/NearClip); Z stays in both so
            // the clip plane (reflected pass) and the mirror depth (main pass) are what hide it.
            int[] q = mover ? new[] { 0, 1 } : new[] { 1, 2 }, main = mover ? new[] { 0, 1 } : new[] { 0, 1, 2 };
            r.SetDrawn(new[] { new DrawnWorld(main, Array.Empty<ViewSource>(), Array.Empty<int>()), new DrawnWorld(q, Array.Empty<ViewSource>(), Array.Empty<int>()) }, null);
            string name = mover ? "mirror-mover" : "mirror";
            r.RenderOffscreen(new Camera { Fov = 90f }, out byte[] rgba);
            Png.Write(png, r.Width, r.Height, rgba);
            int rc = Compare(name, r, 1, rgba, (int panel, float sx, float sy, out string region) =>
                {
                    if (In(RQ, sx, sy)) { region = "mirror-Q"; return Rgb(T3[Quarter(RQ, sx, sy)]); }
                    region = In(RMirror, sx, sy) ? "mirror-empty" : "clear";
                    return new[] { 0, 0, 0 };
                },
                (panel, sx, sy) => NearRect(RMirror, false, sx, sy) || NearRect(RQ, true, sx, sy),
                new[] { "mirror-Q", "mirror-empty", "clear" }, _ => false, png, null);
            if (r.MirrorPasses != 1) { Console.WriteLine($"selftest-gpu REFUSED: {name}: mirror passes {r.MirrorPasses}, expected 1"); return 3; }
            return rc;
        }

        // ---- scene 2b: warp portal ----
        //
        // Invisible portal X = 300 (normal towards the camera, the zone behind it is warp zone A), quad W on the other
        // side (zone B: WarpCoords B = A + (0, 5000, 0), same basis): in the level world it is at Y + 5000 and visible
        // only through the portal. Near is on the other side IN FRONT of the portal (X = 200): cut by the child's
        // NearClip (otherwise it would cover W). Behind is behind the portal in the camera world (X = 500): hidden by
        // the portal's depth (PF_Occlude).
        static readonly byte[] WN = { 0, 220, 220, 255 };
        static int SceneWarp(string png)
        {
            var s = new Scene(1);
            s.World(new Batch { Blend = Blend.Opaque, NoSmooth = true, Unlit = true, WarpPortal = 0, Group = RenderRules.OpaqueWorld }, 0, RMirror, 300, 1, 0, 0, 0, 0, new[] { (1, 1, Fill(1, 1, G)) });
            var wp = new WarpPortal { Surf = 1, Zone0 = 1, Zone1 = 2, PolyFlags = 0x04000129, Normal = new UEVector(-1, 0, 0), Dist = -300 };
            void Shifted(Batch b, float d, byte[][] tex)
            {
                s.World(b, 0, RQ, d, 1, 0, 0, 0, 0, new[] { (2, 2, Tex2x2(tex)) });
                for (int k = s.V.Count - 4; k < s.V.Count; k++) { Vertex v = s.V[k]; v.Y += 5000; s.V[k] = v; }
            }
            Shifted(new Batch { Blend = Blend.Opaque, NoSmooth = true, Unlit = true }, 500, T3);
            Shifted(new Batch { Blend = Blend.Opaque, NoSmooth = true, Unlit = true }, 200, new[] { WN, WN, WN, WN });
            s.World(new Batch { Blend = Blend.Opaque, NoSmooth = true, Unlit = true }, 0, RQ, 500, 1, 0, 0, 0, 0, new[] { (1, 1, Fill(1, 1, ZC)) });
            Renderer r = s.Load(Atlas(LA, LB, LRest), Atlas(new byte[] { 0, 0, 0, 0 }, new byte[] { 0, 0, 0, 0 }, new byte[] { 0, 0, 0, 0 }), null, new List<WarpPortal> { wp });
            if (r is null) { Console.Error.WriteLine("GPU: " + SDL_GetError()); return 2; }
            // The portal child frame is a scene input (in game, Core occlusion): world B = world A + (0, 5000, 0), NearClip
            // is the portal plane X = 300 in the child's world, keeping the far side (X ≥ 300).
            var warpView = new ViewFrame(-1, ViewFrame.KindWarp, 1, new Affine { M00 = 1, M11 = 1, M22 = 1, M13 = -5000 },
                new UEVector(1, 0, 0), -300f, false, new[] { new ViewSource(1, 0, 0) }, default);
            r.SetViews(new[] { warpView }, _ => -1);
            r.RenderOffscreen(new Camera { Fov = 90f }, out byte[] rgba);
            Png.Write(png, r.Width, r.Height, rgba);
            int rc = Compare("warp", r, 1, rgba, (int panel, float sx, float sy, out string region) =>
                {
                    if (In(RQ, sx, sy)) { region = "warp-W"; return Rgb(T3[Quarter(RQ, sx, sy)]); }
                    region = In(RMirror, sx, sy) ? "warp-empty" : "clear";
                    return new[] { 0, 0, 0 };
                },
                (panel, sx, sy) => NearRect(RMirror, false, sx, sy) || NearRect(RQ, true, sx, sy),
                new[] { "warp-W", "warp-empty", "clear" }, _ => false, png, null);
            if (r.WarpPasses != 1) { Console.WriteLine($"selftest-gpu REFUSED: warp: portal passes {r.WarpPasses}, expected 1"); return 3; }
            return rc;
        }

        // ---- scene 2c: sky only through a PF_FakeBackdrop window ----
        // Sky zone SK (covering the sky camera view, X = 400) and sky window W (RQ, X = 100) over wall WL (bottom strip
        // RWall, X = 100). The sky is a child frame of the visible window (Render.dll.c:5994), so the window shows SK,
        // the wall shows the wall, and where there is no world (a gap between polygons) the frame clear (0,0,0), not sky.
        static readonly float[] RSky = { -1f, 1f, -1f, 1f }, RWall = { -0.9f, 0.9f, -0.9f, -0.5f };
        static readonly byte[] SK = { 170, 200, 230, 255 }, WL = { 60, 40, 20, 255 };

        static int SceneSky(string png)
        {
            var s = new Scene(1);
            s.World(new Batch { Blend = Blend.Opaque, NoSmooth = true, Unlit = true }, 0, RSky, 400, 1, 0, 0, 0, 0, new[] { (1, 1, Fill(1, 1, SK)) });
            s.World(new Batch { Blend = Blend.Opaque, NoSmooth = true, Unlit = true, Backdrop = true }, 0, RQ, 100, 1, 0, 0, 0, 0, new[] { (1, 1, Fill(1, 1, G)) });
            s.World(new Batch { Blend = Blend.Opaque, NoSmooth = true, Unlit = true }, 0, RWall, 100, 1, 0, 0, 0, 0, new[] { (1, 1, Fill(1, 1, WL)) });
            Renderer r = s.Load(Atlas(LA, LB, LRest), Atlas(new byte[] { 0, 0, 0, 0 }, new byte[] { 0, 0, 0, 0 }, new byte[] { 0, 0, 0, 0 }), null);
            if (r is null) { Console.Error.WriteLine("GPU: " + SDL_GetError()); return 2; }
            // The sky frame is a scene input (in game, Core occlusion): the sky camera equals the camera, M = I. Core's drawn
            // sets: the master frame draws the wall (2) and shows the sky through window 1; the sky frame draws SK (0).
            r.SetViews(new[] { new ViewFrame(-1, ViewFrame.KindSky, 1, Affine.Identity, default, 0f, false, new[] { new ViewSource(0, 0, 0) }, default) }, _ => -1);
            r.SetDrawn(new[] { new DrawnWorld(new[] { 2 }, Array.Empty<ViewSource>(), new[] { 1 }), new DrawnWorld(new[] { 0 }, Array.Empty<ViewSource>(), Array.Empty<int>()) }, null);
            r.RenderOffscreen(new Camera { Fov = 90f }, out byte[] rgba);
            Png.Write(png, r.Width, r.Height, rgba);
            int rc = Compare("sky", r, 1, rgba, (int panel, float sx, float sy, out string region) =>
                {
                    if (In(RQ, sx, sy)) { region = "sky-window"; return Rgb(SK); }
                    if (In(RWall, sx, sy)) { region = "wall"; return Rgb(WL); }
                    region = "clear"; return new[] { 0, 0, 0 };
                },
                (panel, sx, sy) => NearRect(RQ, false, sx, sy) || NearRect(RWall, false, sx, sy),
                new[] { "sky-window", "wall", "clear" }, _ => false, png, null);
            // Without a sky pass the window comparison proves nothing.
            if (!r.SkyDrawn) { Console.WriteLine("selftest-gpu REFUSED: sky: the sky pass did not execute"); return 3; }
            return rc;
        }

        // ---- scene 2d: sky in a child view ----
        // Sky window W (RQ, X = 500) exists only on the other side of a warp portal (as in scene 2b: zone B = A +
        // (0, 5000, 0)), invisible in the camera world. A child frame also spawns a sky frame (the child's OccludeBsp is
        // the same code, Recursion < 3; sky Coords copy this frame's Coords, Render.dll.c:6030-6094), so the window
        // behind the portal shows SK.
        static int SceneSkyChild(string png)
        {
            var s = new Scene(1);
            s.World(new Batch { Blend = Blend.Opaque, NoSmooth = true, Unlit = true, WarpPortal = 0, Group = RenderRules.OpaqueWorld }, 0, RMirror, 300, 1, 0, 0, 0, 0, new[] { (1, 1, Fill(1, 1, G)) });
            var wp = new WarpPortal { Surf = 1, Zone0 = 1, Zone1 = 2, PolyFlags = 0x04000129, Normal = new UEVector(-1, 0, 0), Dist = -300 };
            s.World(new Batch { Blend = Blend.Opaque, NoSmooth = true, Unlit = true }, 0, RSky, 400, 1, 0, 0, 0, 0, new[] { (1, 1, Fill(1, 1, SK)) });
            s.World(new Batch { Blend = Blend.Opaque, NoSmooth = true, Unlit = true, Backdrop = true }, 0, RQ, 500, 1, 0, 0, 0, 0, new[] { (1, 1, Fill(1, 1, G)) });
            for (int k = s.V.Count - 4; k < s.V.Count; k++) { Vertex v = s.V[k]; v.Y += 5000; s.V[k] = v; }
            Renderer r = s.Load(Atlas(LA, LB, LRest), Atlas(new byte[] { 0, 0, 0, 0 }, new byte[] { 0, 0, 0, 0 }, new byte[] { 0, 0, 0, 0 }), null, new List<WarpPortal> { wp });
            if (r is null) { Console.Error.WriteLine("GPU: " + SDL_GetError()); return 2; }
            // The portal child frame is a scene input (in game, Core occlusion): world B = world A + (0, 5000, 0), NearClip
            // is the portal plane X = 300 in the child's world, keeping the far side (X ≥ 300).
            var warpView = new ViewFrame(-1, ViewFrame.KindWarp, 1, new Affine { M00 = 1, M11 = 1, M22 = 1, M13 = -5000 },
                new UEVector(1, 0, 0), -300f, false, new[] { new ViewSource(1, 0, 0) }, default);
            // Sky frame under the portal frame: sky camera at the origin, rotated like the camera; in root world M = I.
            r.SetViews(new[] { warpView, new ViewFrame(0, ViewFrame.KindSky, 2, Affine.Identity, default, 0f, false, new[] { new ViewSource(0, 0, 0) }, default) }, _ => -1);
            // Core's drawn sets: master — the portal (0); portal frame — sky window 2; its sky frame — SK (1).
            r.SetDrawn(new[] { new DrawnWorld(new[] { 0 }, Array.Empty<ViewSource>(), Array.Empty<int>()),
                new DrawnWorld(Array.Empty<int>(), Array.Empty<ViewSource>(), new[] { 2 }),
                new DrawnWorld(new[] { 1 }, Array.Empty<ViewSource>(), Array.Empty<int>()) }, null);
            r.RenderOffscreen(new Camera { Fov = 90f }, out byte[] rgba);
            Png.Write(png, r.Width, r.Height, rgba);
            int rc = Compare("sky-child", r, 1, rgba, (int panel, float sx, float sy, out string region) =>
                {
                    if (In(RQ, sx, sy)) { region = "sky-window"; return Rgb(SK); }
                    region = In(RMirror, sx, sy) ? "warp-empty" : "clear";
                    return new[] { 0, 0, 0 };
                },
                (panel, sx, sy) => NearRect(RMirror, false, sx, sy) || NearRect(RQ, false, sx, sy),
                new[] { "sky-window", "warp-empty", "clear" }, _ => false, png, null);
            // Without the portal view there is nothing to see the window from: refusal; a portal view without sky in it is the defect.
            if (r.WarpPasses != 1) { Console.WriteLine($"selftest-gpu REFUSED: sky-child: portal passes {r.WarpPasses}, expected 1"); return 3; }
            if (r.SkyPasses != 1) { Console.WriteLine($"FAIL sky-child: sky passes {r.SkyPasses}, expected 1 (in the portal view)"); return 1; }
            return rc;
        }

        // ---- scene 3: spawned BSP light ----

        // ---- scene 5: frame groups ----

        static readonly byte[] OrdA0 = { 30, 160, 90, 255 }, OrdW = { 90, 60, 150, 255 }, OrdBg = { 100, 120, 140, 255 },
            OrdDc = { 100, 80, 60, 255 }, OrdS = { 60, 40, 20, 255 }, OrdM = { 160, 128, 96, 255 }, OrdT = { 40, 80, 60, 255 },
            OrdWm = { 100, 200, 60, 255 }, OrdMm = { 180, 90, 200, 255 }, OrdMk = { 200, 60, 40, 255 }, OrdMt = { 40, 90, 120, 255 };

        static int SceneOrder(string png)
        {
            var s = new Scene(4);
            int blended = RenderRules.WorldGroup(WorldMesh.PF_Translucent);
            var w = new Batch { Blend = Blend.Translucent, NoSmooth = true, Unlit = true, Group = blended };
            s.World(w, 0, RA, 100, 1, 0, 0, 0, 0, new[] { (1, 1, Fill(1, 1, OrdW)) });
            s.World(new Batch { Blend = Blend.Opaque, NoSmooth = true, Unlit = true }, 1, RFull, 200, 1, 0, 0, 0, 0, new[] { (1, 1, Fill(1, 1, OrdBg)) });
            s.World(new Batch { Blend = Blend.Translucent, NoSmooth = true, Unlit = true, Group = blended }, 1, RA, 100, 1, 0, 0, 0, 0, new[] { (1, 1, Fill(1, 1, OrdW)) });
            // A surface facing away from the camera (rectangle with x0 > x1, reversed winding), nearest: OccludeBsp does not
            // draw it (Render.dll.c:5875-5879), so it is absent from Core's drawn set and RD of panel 1 keeps the background.
            int away = s.World(new Batch { Blend = Blend.Opaque, NoSmooth = true, Unlit = true }, 1, new[] { RD[1], RD[0], RD[2], RD[3] }, 50, 1, 0, 0, 0, 0, new[] { (1, 1, Fill(1, 1, OrdA0)) });
            s.World(new Batch { Blend = Blend.Opaque, NoSmooth = true, Unlit = true }, 2, RFull, 200, 1, 0, 0, 0, 0, new[] { (1, 1, Fill(1, 1, OrdBg)) });
            s.World(new Batch { Blend = Blend.Opaque, NoSmooth = true, Unlit = true }, 3, RFull, 250, 1, 0, 0, 0, 0, new[] { (1, 1, Fill(1, 1, OrdBg)) });
            s.World(new Batch { Blend = Blend.Translucent, NoSmooth = true, Unlit = true, Group = blended }, 3, RA, 150, 1, 0, 0, 0, 0, new[] { (1, 1, Fill(1, 1, OrdW)) });
            // World Masked+Modulated with an alpha-0 texel: D3DDrv cuts it by the alpha test (the mask is cleared only
            // under Translucent), so RD of panel 3 keeps the background, not 2·0·bg.
            s.World(new Batch { Blend = Blend.Modulated, NoSmooth = true, Unlit = true, Group = blended, AlphaTest = RenderRules.AlphaTest(WorldMesh.PF_Masked | WorldMesh.PF_Modulated) },
                3, RD, 150, 1, 0, 0, 0, 0, new[] { (1, 1, Fill(1, 1, new byte[] { 0, 0, 0, 0 })) });
            // Lit Modulated level surface over the background, lightmap block LA.
            s.World(new Batch { Blend = Blend.Modulated, NoSmooth = true, Group = blended }, 2, RD, 150, 1, 2f / AtlasW, 2f / AtlasH, 0, 0, new[] { (1, 1, Fill(1, 1, OrdWm)) });
            // Lit Modulated level surface with a fog map (fog block x >= 4), panel 2 RB: D3DDrv draws the fog map as its own
            // ONE/INVSRCALPHA pass over the modulated result (D3DDrv.dll.c:6258-6265), not folded before the blend.
            s.World(new Batch { Blend = Blend.Modulated, NoSmooth = true, Group = blended, FogPass = true }, 2, RB, 150, 1, 2f / AtlasW, 2f / AtlasH, 6f / AtlasW, 2f / AtlasH, new[] { (1, 1, Fill(1, 1, OrdWm)) });
            // Lit Modulated mover surface (MoverScene batch: WorldMode = Blend, Lightmapped), lightmap block LB; R saturates
            // t·L before the blend (2·1·bg, not 2·1.42·bg).
            s.ActorTex["order/MM"] = (1, 1, Fill(1, 1, OrdMm));
            var mm = new ActorDraw { Texture = "order/MM", WorldMode = (int)Blend.Modulated, Lightmapped = true, MoverAtlas = true, NoSmooth = true, Group = blended };
            var mv = new List<Vertex>(); var mi = new List<uint>();
            s.Quad(mv, mi, 3, RB, 150, 1, 6f / AtlasW, 2f / AtlasH, 0, 0, null, out _, out _);
            mm.Vertices.AddRange(mv); mm.Indices.AddRange(mi);
            s.Actors.Add(mm);
            // Two mover surfaces of the blended group, panel 1 RB: a Translucent one (nearer) listed BEFORE a Masked one
            // (farther), as MoverScene emits them in material first-appearance order. The world groups of the dynamic list
            // keep opaque/masked before blended (the pre-DrawOrder rule, SdlRenderer.SetActors): masked first, then the
            // translucent over it. In list order the translucent would land on the background and the masked overwrite it.
            foreach (var (tex, mode, depth, at) in new[] { ("order/MT", (int)Blend.Translucent, 120f, false), ("order/MK", (int)Blend.Masked, 180f, true) })
            {
                s.ActorTex[at ? Renderer.TexKey(tex, Renderer.TexDraw.Masked) : tex] = (1, 1, Fill(1, 1, at ? OrdMk : OrdMt));
                var md = new ActorDraw { Texture = tex, WorldMode = mode, Unlit = true, MoverAtlas = true, NoSmooth = true, AlphaTest = at, Group = blended };
                var dv = new List<Vertex>(); var di = new List<uint>();
                s.Quad(dv, di, 1, RB, depth, 1, 0, 0, 0, 0, null, out _, out _);
                md.Vertices.AddRange(dv); md.Indices.AddRange(di);
                s.Actors.Add(md);
            }
            void A(string name, int panel, float[] rect, float depth, ActorBlend blend, byte[] tex, int group, float[] light = null)
            {
                s.ActorTex["order/" + name] = (1, 1, Fill(1, 1, tex));
                s.ActorFx("order/" + name, panel, rect, depth, blend, false, false, light ?? One3, new byte[3], 1f);
                s.Actors[^1].Group = group;
            }
            A("T3", 3, RA, 100, ActorBlend.Translucent, OrdT, RenderRules.ActorGroup(true));
            A("S2", 2, RA, 100, ActorBlend.Translucent, OrdS, RenderRules.ActorGroup(true));
            A("DC", 2, RA, 150, ActorBlend.Modulated, OrdDc, RenderRules.WorldGroup(0));
            // Vertex light 0.5: a Modulated mesh does not see it (D3DDrv DrawTriangles: white under PF_Modulated).
            A("M3", 3, RA, 200, ActorBlend.Modulated, OrdM, RenderRules.ActorGroup(false), new[] { 0.5f, 0.5f, 0.5f });
            A("A0", 0, RFull, 150, ActorBlend.Opaque, OrdA0, RenderRules.ActorGroup(false));
            Renderer r = s.Load(Atlas(LA, LB, LRest), Atlas(new byte[] { 0, 0, 0, 0 }, Fog, new byte[] { 0, 0, 0, 0 }), null);
            if (r is null) { Console.Error.WriteLine("GPU: " + SDL_GetError()); return 2; }
            r.SetDrawn(new[] { new DrawnWorld(Enumerable.Range(0, s.Draws.Count).Where(i => i != away).ToArray(), Array.Empty<ViewSource>(), Array.Empty<int>()) }, null);
            r.RenderOffscreen(new Camera { Fov = 90f }, out byte[] rgba);
            Png.Write(png, r.Width, r.Height, rgba);
            return Compare("order", r, 4, rgba, ExpectOrder, (panel, sx, sy) => NearRect(RA, false, sx, sy) || NearRect(RFull, false, sx, sy)
                    || (panel == 2 && (NearRect(RD, false, sx, sy) || NearRect(RB, false, sx, sy))) || (panel is 1 or 3 && NearRect(RB, false, sx, sy)),
                new[] { "W0", "A0", "W1", "bg", "S2", "M3T3", "WM", "WF", "MM", "MV" }, _ => false, png, null);
        }

        static double[] Tr(byte[] c, double[] dst) => Q(D(c).Select((x, k) => x + dst[k] * (1.0 - x)).ToArray());
        static double[] Mo(byte[] c, double[] dst) => Mo(D(c), dst);
        static double[] Mo(double[] c, double[] dst) => Q(c.Select((x, k) => 2.0 * x * dst[k]).ToArray());
        // Lightmap stage: saturate(t·L/63.75) (MODULATE2X over the RGBA7·2 lightmap; stage output is clamped).
        static double[] LitF(byte[] t, byte[] l) => D(t).Select((x, k) => Math.Min(1.0, x * l[k] / 63.75)).ToArray();

        static int[] ExpectOrder(int panel, float sx, float sy, out string region)
        {
            static int[] B(double[] v) => v.Select(x => (int)Math.Round(x * 255.0)).ToArray();
            if (!In(RFull, sx, sy)) { region = "clear"; return new[] { 0, 0, 0 }; }
            bool inA = In(RA, sx, sy);
            switch (panel)
            {
                case 0: region = inA ? "W0" : "A0"; return B(inA ? Tr(OrdW, D(OrdA0)) : D(OrdA0));
                case 1:
                    if (In(RB, sx, sy)) { region = "MV"; return B(Tr(OrdMt, D(OrdMk))); }
                    region = inA ? "W1" : "bg"; return B(inA ? Tr(OrdW, D(OrdBg)) : D(OrdBg));
                case 2:
                    if (In(RD, sx, sy)) { region = "WM"; return B(Mo(LitF(OrdWm, LA), D(OrdBg))); }
                    // Fog pass ONE/INVSRCALPHA over the stored modulated result: dst·(1 − a) + rgb.
                    if (In(RB, sx, sy)) { region = "WF"; return B(Q(Mo(LitF(OrdWm, LA), D(OrdBg)).Select((x, k) => x * (1.0 - Fog[3] / 255.0) + Fog[k] / 255.0).ToArray())); }
                    region = inA ? "S2" : "bg"; return B(inA ? Tr(OrdS, Mo(OrdDc, D(OrdBg))) : D(OrdBg));
                default:
                    if (In(RB, sx, sy)) { region = "MM"; return B(Mo(LitF(OrdMm, LB), D(OrdBg))); }
                    region = inA ? "M3T3" : "bg"; return B(inA ? Tr(OrdT, Tr(OrdW, Mo(OrdM, D(OrdBg)))) : D(OrdBg));
            }
        }

        // ---- scene 4: ActorFx, vertex fog and Opacity < 1 ----

        static readonly float[] RFull = { -0.95f, 0.95f, -0.95f, 0.95f };
        static readonly byte[] Bg2 = { 100, 120, 140, 255 }, Bg4 = { 70, 50, 110, 255 }, OBc = { 30, 160, 90, 255 };
        static readonly float[] One3 = { 1f, 1f, 1f };

        // Scene batch: name = region, RGBA 1×1 texture (variant key) or P8 (Pal != null), mode, light, fog, alpha.
        sealed record FxQuad(string Name, int Panel, float[] Rect, float Depth, ActorBlend Blend, bool Unlit, bool OpacityMasked,
            byte[] Tex, string P8, float[] Light, byte[] Fog, float Alpha);

        // 1×1 P8 textures: texel index and palette (entries not named here are zero).
        sealed record P8Tex(string Path, int Index, bool BMasked, byte[] Entry0, byte[] Entry1);
        static readonly P8Tex[] P8s =
        {
            new("p8/a", 1, false, new byte[] { 9, 9, 9, 255 }, new byte[] { 200, 40, 100, 255 }),
            new("p8/h", 1, false, new byte[] { 9, 9, 9, 255 }, new byte[] { 60, 200, 120, 128 }),
            new("p8/z", 0, false, new byte[] { 250, 220, 30, 255 }, new byte[] { 9, 9, 9, 255 }),
            new("p8/m", 0, true, new byte[] { 250, 220, 30, 255 }, new byte[] { 9, 9, 9, 255 }),
        };

        static readonly FxQuad[] FxQuads =
        {
            new("K1", 0, RA, 100, ActorBlend.Opaque, false, false, new byte[] { 200, 100, 50, 255 }, null, new[] { 1.0f, 0.5f, 0.25f }, new byte[] { 26, 51, 77 }, 1f),
            new("K2", 0, RB, 100, ActorBlend.Masked, false, false, new byte[] { 200, 90, 128, 255 }, null, new[] { 1.0f, 1.0f, 0.5f }, new byte[] { 102, 13, 153 }, 1f),
            new("U1", 1, RA, 100, ActorBlend.Opaque, true, false, new byte[] { 200, 100, 50, 255 }, null, new[] { 0.5f, 1.25f, 2.0f }, new byte[] { 30, 60, 90 }, 1f),
            new("U2", 1, RB, 100, ActorBlend.Opaque, true, false, new byte[] { 128, 64, 32, 255 }, null, One3, new byte[] { 20, 0, 240 }, 1f),
            new("NT", 2, RA, 100, ActorBlend.Translucent, false, false, new byte[] { 60, 40, 20, 255 }, null, One3, new byte[] { 80, 80, 80 }, 1f),
            new("NM", 2, RB, 100, ActorBlend.Modulated, false, false, new byte[] { 128, 100, 200, 255 }, null, One3, new byte[] { 50, 50, 50 }, 1f),
            new("NA", 2, RC, 100, ActorBlend.AlphaBlend, false, false, null, "p8/a", One3, new byte[] { 90, 90, 90 }, 0.5f),
            new("O1", 3, RA, 100, ActorBlend.AlphaBlend, false, false, null, "p8/a", One3, new byte[] { 0, 0, 0 }, 0.5f),
            new("O2", 3, RB, 100, ActorBlend.AlphaBlend, false, false, null, "p8/h", One3, new byte[] { 0, 0, 0 }, 0.5f),
            new("O3", 3, RC, 100, ActorBlend.AlphaBlend, false, true, null, "p8/z", One3, new byte[] { 0, 0, 0 }, 0.5f),
            new("O4", 3, RD, 100, ActorBlend.AlphaBlend, false, false, null, "p8/z", One3, new byte[] { 0, 0, 0 }, 0.5f),
            new("O5", 4, RA, 100, ActorBlend.AlphaBlend, false, false, null, "p8/m", One3, new byte[] { 0, 0, 0 }, 0.5f),
            new("ZA", 4, RB, 100, ActorBlend.AlphaBlend, false, false, null, "p8/a", One3, new byte[] { 0, 0, 0 }, 0.5f),
            new("ZT", 4, RB, 150, ActorBlend.Translucent, false, false, new byte[] { 40, 80, 60, 255 }, null, One3, new byte[] { 0, 0, 0 }, 1f),
            // Last: the opaque background of panel 3 is FARTHER than the Opacity quads; without sorting it would cover them.
            new("OB", 3, RFull, 150, ActorBlend.Opaque, false, false, OBc, null, One3, new byte[] { 0, 0, 0 }, 1f),
        };

        // P8 texel colour and alpha after OpacityPalette (mask: slot or bMasked -> entry 0 = (0,0,0,0)).
        static double[] P8Texel(string path, bool slotMasked)
        {
            P8Tex p = P8s.First(x => x.Path == path);
            byte[] e = p.Index == 0 ? p.Entry0 : p.Entry1;
            if (p.Index == 0 && (slotMasked || p.BMasked)) return new double[4];
            return new[] { e[0] / 255.0, e[1] / 255.0, e[2] / 255.0, e[3] / 255.0 };
        }

        // ---- mesh back-face culling ----
        //
        // Shipped culls a mesh triangle on the CPU (DrawLodMesh, Render.dll.c:14669-14697): N = (P0−P1)×(P2−P0) of the
        // camera-space points (frame axes X right, Y down, Z forward), the triangle is kept when PF_TwoSided (0x100) or
        // (N·P0)·Frame->Mirror (+0x20) < 0 (fcomp 0x10b385bc = 0.0). The port culls on the GPU (front face CW, back cull for
        // a slot without PF_TwoSided). Two quads with opposite index order, no PF_TwoSided: the expectation is the shipped
        // rule evaluated here on the same vertices, independent of the GPU state. Mirror = 1 only; the reflected pass
        // (Mirror = −1, front cull) is not covered by this scene.
        static int SceneCull(string png)
        {
            var s = new Scene(2);
            bool[] keep = new bool[2];
            for (int panel = 0; panel < 2; panel++)
            {
                s.World(new Batch { Blend = Blend.Opaque, NoSmooth = true, Unlit = true }, panel, RFull, 200, 1, 0, 0, 0, 0, new[] { (1, 1, Fill(1, 1, Bg2)) });
                string tex = "selftest/cull" + panel;
                s.ActorTex[tex] = (1, 1, Fill(1, 1, G));
                var ad = new ActorDraw { Texture = tex, Blend = ActorBlend.Opaque, Unlit = true, TwoSided = false };
                var av = new List<Vertex>(); var ai = new List<uint>();
                s.Quad(av, ai, panel, RA, 100, 1, 0, 0, 0, 0, new[] { 1f, 1f, 1f }, out _, out _);
                if (panel == 1) for (int t = 0; t < ai.Count; t += 3) (ai[t + 1], ai[t + 2]) = (ai[t + 2], ai[t + 1]);
                ad.Vertices.AddRange(av); ad.Indices.AddRange(ai);
                s.Actors.Add(ad);
                // UE (X forward, Y right, Z up) -> frame (Y, −Z, X).
                (float x, float y, float z) C(Vertex v) => (v.Y, -v.Z, v.X);
                var p0 = C(av[(int)ai[0]]); var p1 = C(av[(int)ai[1]]); var p2 = C(av[(int)ai[2]]);
                float nz = (p0.x - p1.x) * (p2.y - p0.y) - (p0.y - p1.y) * (p2.x - p0.x);
                float ny = (p0.z - p1.z) * (p2.x - p0.x) - (p0.x - p1.x) * (p2.z - p0.z);
                float nx = (p0.y - p1.y) * (p2.z - p0.z) - (p0.z - p1.z) * (p2.y - p0.y);
                keep[panel] = (nx * p0.x + nz * p0.z + ny * p0.y) * 1f < 0f;
            }
            Console.WriteLine($"selftest-gpu cull: shipped rule keeps panel 0 {keep[0]}, panel 1 {keep[1]}");
            if (keep[0] == keep[1]) { Console.WriteLine("selftest-gpu REFUSED: cull: the scene does not distinguish the facing"); return 3; }
            Renderer r = s.Load(Atlas(LA, LB, LRest), Atlas(new byte[] { 0, 0, 0, 0 }, new byte[] { 0, 0, 0, 0 }, new byte[] { 0, 0, 0, 0 }), null);
            if (r is null) { Console.Error.WriteLine("GPU: " + SDL_GetError()); return 2; }
            r.RenderOffscreen(new Camera { Fov = 90f }, out byte[] rgba);
            Png.Write(png, r.Width, r.Height, rgba);
            return Compare("cull", r, 2, rgba, (int panel, float sx, float sy, out string region) =>
                {
                    if (In(RA, sx, sy)) { region = keep[panel] ? "cull-kept" : "cull-culled"; return Rgb(keep[panel] ? G : Bg2); }
                    if (In(RFull, sx, sy)) { region = "bg"; return Rgb(Bg2); }
                    region = "clear";
                    return new[] { 0, 0, 0 };
                },
                (panel, sx, sy) => NearRect(RA, false, sx, sy) || NearRect(RFull, false, sx, sy),
                new[] { "cull-kept", "cull-culled", "bg", "clear" }, reg => reg == "clear", png, null);
        }

        // ---- NOT ORIGINAL (D-78): per-use upload of a world texture ----
        //
        // Shipped D3DDrv keeps ONE upload per CacheID whose palette entry 0 follows the first use (D-78). The port uploads per
        // use: an opaque draw keeps index 0's palette colour, a masked draw cuts index 0, whatever the order. Two mover draws
        // of 1×1 P8 textures whose texel is index 0 (entry 0 = opaque colour, alpha 255) over the background Bg2:
        //   UN masked draw -> index 0 cut, background;  UP opaque draw -> the colour.
        static readonly byte[] UpC = { 210, 60, 30, 255 };
        static readonly (string Name, float[] Rect, bool Masked)[] UpQuads = { ("UN", RC, true), ("UP", RD, false) };

        static int SceneUploads(string png)
        {
            var s = new Scene(1);
            s.World(new Batch { Blend = Blend.Opaque, NoSmooth = true, Unlit = true }, 0, RFull, 200, 1, 0, 0, 0, 0, new[] { (1, 1, Fill(1, 1, Bg2)) });
            foreach (var q in UpQuads)
            {
                string path = "up/" + q.Name;
                var pal = new byte[1024];
                UpC.CopyTo(pal, 0);
                s.P8[path] = new UETextureSource.TextureData { Path = path, State = "EXPORTED", Width = 1, Height = 1, Masked = false,
                    MipCount = 1, Palette = pal, Levels = new[] { (1, 1, new byte[] { 0 }) } };
                var ad = new ActorDraw { Texture = path, WorldMode = (int)(q.Masked ? Blend.Masked : Blend.Opaque), NoSmooth = true,
                    AlphaTest = q.Masked, Group = RenderRules.WorldGroup(0) };
                var av = new List<Vertex>(); var ai = new List<uint>();
                s.Quad(av, ai, 0, q.Rect, 100, 1, 0, 0, 0, 0, null, out _, out _);
                ad.Vertices.AddRange(av); ad.Indices.AddRange(ai);
                s.Actors.Add(ad);
            }
            Renderer r = s.Load(Atlas(LA, LB, LRest), Atlas(new byte[] { 0, 0, 0, 0 }, new byte[] { 0, 0, 0, 0 }, new byte[] { 0, 0, 0, 0 }), null);
            if (r is null) { Console.Error.WriteLine("GPU: " + SDL_GetError()); return 2; }
            r.RenderOffscreen(new Camera { Fov = 90f }, out byte[] rgba);
            Png.Write(png, r.Width, r.Height, rgba);
            return Compare("uploads", r, 1, rgba, (int panel, float sx, float sy, out string region) =>
                {
                    foreach (var q in UpQuads)
                        if (In(q.Rect, sx, sy)) { region = q.Name; return Rgb(q.Masked ? Bg2 : UpC); }
                    if (In(RFull, sx, sy)) { region = "bg"; return Rgb(Bg2); }
                    region = "clear";
                    return new[] { 0, 0, 0 };
                },
                (panel, sx, sy) => UpQuads.Any(q => NearRect(q.Rect, false, sx, sy)) || NearRect(RFull, false, sx, sy),
                new[] { "UN", "UP", "bg", "clear" }, reg => reg == "clear", png, null);
        }

        static int SceneActorFx(string png)
        {
            var s = new Scene(5);
            s.World(new Batch { Blend = Blend.Opaque, NoSmooth = true, Unlit = true }, 2, RFull, 200, 1, 0, 0, 0, 0, new[] { (1, 1, Fill(1, 1, Bg2)) });
            s.World(new Batch { Blend = Blend.Opaque, NoSmooth = true, Unlit = true }, 4, RFull, 200, 1, 0, 0, 0, 0, new[] { (1, 1, Fill(1, 1, Bg4)) });
            foreach (P8Tex p in P8s)
            {
                var pal = new byte[1024];
                p.Entry0.CopyTo(pal, 0); p.Entry1.CopyTo(pal, 4);
                s.P8[p.Path] = new UETextureSource.TextureData { Path = p.Path, State = "EXPORTED", Width = 1, Height = 1, Masked = p.BMasked,
                    MipCount = 1, Palette = pal, Levels = new[] { (1, 1, new[] { (byte)p.Index }) } };
            }
            foreach (FxQuad q in FxQuads)
            {
                string tex = q.P8 ?? "fx/" + q.Name;
                if (q.Tex != null) s.ActorTex[Renderer.TexKey(tex, q.Blend == ActorBlend.Masked ? Renderer.TexDraw.Masked : Renderer.TexDraw.Normal)] = (1, 1, Fill(1, 1, q.Tex));
                s.ActorFx(tex, q.Panel, q.Rect, q.Depth, q.Blend, q.Unlit, q.OpacityMasked, q.Light, q.Fog, q.Alpha);
            }
            Renderer r = s.Load(Atlas(LA, LB, LRest), Atlas(new byte[] { 0, 0, 0, 0 }, new byte[] { 0, 0, 0, 0 }, new byte[] { 0, 0, 0, 0 }), null);
            if (r is null) { Console.Error.WriteLine("GPU: " + SDL_GetError()); return 2; }
            r.RenderOffscreen(new Camera { Fov = 90f }, out byte[] rgba);
            Png.Write(png, r.Width, r.Height, rgba);
            var regions = FxQuads.Where(q => q.Name != "ZT").Select(q => q.Name).Append("bg2").Append("bg4").Append("clear").ToArray();
            return Compare("actorfx", r, 5, rgba, (int panel, float sx, float sy, out string region) => ExpectFx(panel, sx, sy, out region),
                (panel, sx, sy) => FxQuads.Any(q => q.Panel == panel && NearRect(q.Rect, false, sx, sy))
                    || ((panel == 2 || panel == 4) && NearRect(RFull, false, sx, sy)),
                regions, _ => false, png, null);
        }

        // Batch fragment colour without blending: lit is saturate(t·light + fog·admission), unlit via sRGB;
        // fog admitted only for Opaque/Masked. Alpha: AlphaBlend t.a·A, otherwise 1.
        static (double[] c, double a) FxSource(FxQuad q)
        {
            double[] t = q.P8 != null ? P8Texel(q.P8, q.OpacityMasked) : new[] { q.Tex[0] / 255.0, q.Tex[1] / 255.0, q.Tex[2] / 255.0, q.Tex[3] / 255.0 };
            bool fogOn = q.Blend == ActorBlend.Opaque || q.Blend == ActorBlend.Masked;
            var c = new double[3];
            for (int k = 0; k < 3; k++)
            {
                double f = fogOn ? q.Fog[k] / 255.0 : 0.0;
                c[k] = Math.Clamp(t[k] * q.Light[k] + f, 0.0, 1.0);   // unlit uses the same mode 2 (GUnlitColor in vertex light)
            }
            return (c, q.Blend == ActorBlend.AlphaBlend ? t[3] * q.Alpha : 1.0);
        }

        // Pipeline blending by mode over dst (0..1).
        static double[] FxBlend(FxQuad q, double[] dst)
        {
            var (c, a) = FxSource(q);
            var o = new double[3];
            for (int k = 0; k < 3; k++)
                o[k] = q.Blend switch
                {
                    ActorBlend.Translucent => c[k] + dst[k] * (1.0 - c[k]),
                    ActorBlend.Modulated => c[k] * dst[k] + dst[k] * c[k],
                    ActorBlend.AlphaBlend => c[k] * a + dst[k] * (1.0 - a),
                    _ => c[k],
                };
            return o;
        }

        static double[] D(byte[] b) => new[] { b[0] / 255.0, b[1] / 255.0, b[2] / 255.0 };
        // The intermediate result is stored in the target as a byte.
        static double[] Q(double[] v) => v.Select(x => Byte(x) / 255.0).ToArray();

        static int[] ExpectFx(int panel, float sx, float sy, out string region)
        {
            double[] dst = panel switch { 2 => D(Bg2), 3 => null, 4 => D(Bg4), _ => new double[3] };
            region = panel switch { 2 => "bg2", 4 => "bg4", _ => "clear" };
            if (panel == 3)
            {
                FxQuad ob = FxQuads.First(q => q.Name == "OB");
                if (!In(ob.Rect, sx, sy)) { region = "clear"; return new[] { 0, 0, 0 }; }
                dst = Q(FxBlend(ob, new double[3])); region = "OB";
            }
            if ((panel == 2 || panel == 4) && !In(RFull, sx, sy)) { region = "clear"; return new[] { 0, 0, 0 }; }
            // Blended batches in list order after opaque ones (OB already counted as background).
            foreach (FxQuad q in FxQuads.Where(q => q.Panel == panel && q.Name != "OB" && In(q.Rect, sx, sy))
                         .OrderBy(q => q.Blend == ActorBlend.Opaque || q.Blend == ActorBlend.Masked ? 0 : 1))
            {
                dst = Q(FxBlend(q, dst));
                if (q.Name != "ZT") region = q.Name;
            }
            return dst.Select(x => (int)Math.Round(x * 255.0)).ToArray();
        }

        // ---- scene 6: Original / Enhanced world sampler ----
        static int SceneSampler(string stem)
        {
            // The pixel check runs with anisotropy OFF: how an anisotropic sampler picks the mip level is driver-defined
            // (measured on this quad: Metal B 49..49, RADV with anisotropy 16 B 0..0 — native Vulkan and D3D12
            // via vkd3d; RADV without anisotropy 49..49). Trilinear blending itself is portable. Anisotropy is checked
            // below on the sampler create info the renderer passed (sampler-anisotropy).
            var s = new Scene(1) { Anisotropy = 0 };
            s.World(new Batch { Blend = Blend.Opaque, Unlit = true }, 0, RD, 100, MipRepeat, 0, 0, 0, 0, new[] { (4, 4, Fill(4, 4, M0)), (2, 2, Fill(2, 2, M1)) });
            byte[] zero = { 0, 0, 0, 0 };
            Renderer r = s.Load(Atlas(LA, LB, LRest), Atlas(zero, zero, zero), null);
            if (r is null) { Console.Error.WriteLine("GPU: " + SDL_GetError()); return 2; }
            int rc = 0;
            foreach (bool enhanced in new[] { false, true })
            {
                r.EnhancedVisuals = enhanced;
                r.RenderOffscreen(new Camera { Fov = 90f }, out byte[] rgba);
                string png = stem + (enhanced ? ".sampler-enhanced.png" : ".sampler-original.png");
                Png.Write(png, r.Width, r.Height, rgba);
                int n = 0, bad = 0, minB = 255, maxB = 0;
                for (int py = 0; py < N; py++)
                    for (int px = 0; px < N; px++)
                    {
                        float sx = (px + 0.5f) * 2f / N - 1f, sy = 1f - (py + 0.5f) * 2f / N;
                        if (!In(RD, sx, sy) || NearRect(RD, false, sx, sy)) continue;
                        int o = (py * N + px) * 4, g = rgba[o + 1], b = rgba[o + 2];
                        n++; minB = Math.Min(minB, b); maxB = Math.Max(maxB, b);
                        // Original: level 0 exactly (point mip). Enhanced: level 1 blended in (trilinear), at LOD ≈ 0.25
                        // about a quarter of it; a point mip gives B = 0 or 200 (one level), never in between.
                        bool ok = enhanced ? b >= 20 && b <= 180 && g >= 20 && g <= 180
                            : Math.Abs(g - M0[1]) <= Tol && b <= Tol && rgba[o] <= Tol;
                        if (!ok && bad++ < 4) Console.WriteLine($"  MISMATCH sampler-{(enhanced ? "enhanced" : "original")} px ({px},{py}): ({rgba[o]},{g},{b})");
                    }
                Console.WriteLine($"selftest-gpu sampler-{(enhanced ? "enhanced" : "original")} {r.Driver}: pixels {n}, B range {minB}..{maxB}, mismatches {bad} -> {png}");
                if (n == 0) { Console.WriteLine("selftest-gpu REFUSED: sampler: no pixels of the mip quad"); return 3; }
                if (bad > 0) rc = 1;
            }
            // Sampler state of the default renderer ([HPEngine] Anisotropy 16): Enhanced = linear mips + anisotropy 16 (D-48,
            // D-53), Original = point mips, no anisotropy (D3DDrv). Read from the create info of samplers actually created.
            var sa = new Scene(1);
            sa.World(new Batch { Blend = Blend.Opaque, Unlit = true }, 0, RD, 100, MipRepeat, 0, 0, 0, 0, new[] { (4, 4, Fill(4, 4, M0)), (2, 2, Fill(2, 2, M1)) });
            Renderer ra = sa.Load(Atlas(LA, LB, LRest), Atlas(zero, zero, zero), null);
            if (ra is null) { Console.Error.WriteLine("GPU: " + SDL_GetError()); return 2; }
            SDL_GPUSamplerCreateInfo se = ra.EnhancedSamplerInfo, so = ra.OriginalSamplerInfo;
            bool okE = se.mipmap_mode == SDL_GPUSamplerMipmapMode.SDL_GPU_SAMPLERMIPMAPMODE_LINEAR && se.enable_anisotropy && se.max_anisotropy == 16f;
            bool okO = so.mipmap_mode == SDL_GPUSamplerMipmapMode.SDL_GPU_SAMPLERMIPMAPMODE_NEAREST && !so.enable_anisotropy;
            Console.WriteLine($"selftest-gpu sampler-anisotropy {ra.Driver}: enhanced mip {se.mipmap_mode} aniso {(bool)se.enable_anisotropy}/{se.max_anisotropy}, "
                + $"original mip {so.mipmap_mode} aniso {(bool)so.enable_anisotropy}/{so.max_anisotropy}; {(okE && okO ? "ok" : "MISMATCH")}");
            if (!okE || !okO) rc = 1;
            return rc;
        }
    }
}
