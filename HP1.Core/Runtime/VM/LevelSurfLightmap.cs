using System;
using System.Collections.Generic;

namespace HP1.UEScript
{
    // PER-FRAME LEVEL SURFACE LIGHTMAP — the level branch of slot 3 of
    // FLightManager::SetupForSurf (FUN_10b077f0, Render.dll.c:19726-20700).
    //
    // SHIPPED RULE (read from the decompile, disasm verified where
    // named below; oracle — se_harness/bsp_diff/validate_level_lightmap.py):
    //
    //   1. SELECTION (R:19871-19893, disasm 0x10b07c6a-0x10b07cbe). For a level surface (the draw record
    //      has no mover actor, or the mover lacks bDynamicLightMover, `+0x2d0 & 0x10`)
    //      the light list is FLightMapIndex.iLightActors in Model->Lights,
    //      null-terminated; EVERY element goes to the categoriser
    //      thunk_FUN_10b097b0(0, light) (R:7828-7880) — the same as the mover
    //      branch (MoverMode3.Categorize), with param_1 = 0. The shadow mask
    //      pointer of an accepted element = LightBits + DataOffset + k·VClamp·
    //      ((UClamp+7)>>3), where k is the index IN THE LIST (the step also counts rejected ones,
    //      0x10b07cb6-0x10b07cbc). There is NO bSpecialLit selection in this branch: per
    //      disasm bit 0 of +0x1f0 is read only in the mover branch (0x10b07b08,
    //      0x10b07b38); the slot-3 body (0x10b077f0-0x10b09700), ComputeFromActor
    //      (0x10b06920-0x10b077f0), fillers and merge (0x10b03430-0x10b06920,
    //      0x10b0ca70-0x10b0cf00) have no other reads of +0x1f0 except bDarkLight (0x10b03632).
    //      Then the draw record's `+0x48` list (URender::SurfLights: the frame's dynamic lights, light gate of
    //      SetupDynamics + filter FUN_10b31c70 in the OccludeBsp walk, UEOcclusion.SetupLight/FilterLight) —
    //      every element to thunk(0, light) too (R:19892-19894), appended after the iLightActors records;
    //      their mask pointer stays 0 (no LightBits mask). The gate admits only lights that are not
    //      (bStatic|bNoDelete) without bDynamicLight, so the categoriser gives them category 2 (or 3 for
    //      LightEffect 15). Which frame's list: OccludeFrame gives each frame of the tree its own lists, while the
    //      result 0x31 is keyed by the viewport frame number (R:20501-20506) — the first frame in DrawFrame order
    //      (children before the parent) that draws the surface builds it, the others reuse it
    //      (UEWorldFrameProducer.SurfDynamicLights).
    //
    //   2. CATEGORIES (R:7840-7870): 0 — bStatic, LightType 1, effect without the
    //      0x10b38114/0x10b38118 flags (neither "spatially dynamic" nor
    //      "changing shadow"); 1 — other bStatic/bNoDelete without
    //      bDynamicLight; 2 — the rest; 3 — LightEffect 15 (does not reach the
    //      lightmap: loops R:20433 `[1]==0` and R:20580 `[1]∈{1,2}`). With UClient.NoDynamicLights set
    //      (FrameFx.NoDynamicLights) 1 becomes 0 and 2 is rejected (MoverMode3.Categorize).
    //
    //   3. CACHED PART — key 0x18 (R:20300-20323, creation R:20361):
    //      zone ambient seed (R:20385-20426) + category 0 in list order,
    //      each with ITS OWN mask from LightBits (FUN_10b02970, R:20448) —
    //      ComputeFromActor, filler, merge (R:20435-20468). The cache
    //      is rebuilt only on a miss or when DAT_10b5f684 ≠ 0, which
    //      the categoriser raises for a light with bLightChanged
    //      (`+0x28 & 0x1000`, R:7872-7874). In the Engine.dll DECOMPILE bit 0x1000
    //      of +0x28 is written only by AActor::PostEditChange under GIsEditor
    //      (Engine.dll.c:152096, 225380); absence of other writes is NOT
    //      verified by disasm (V11); the extracted .uc (Engine.u bodies there are stubs) have no
    //      bLightChanged writes.
    //      The port does not rebuild cache 0x18; FMemCache eviction (memory LRU)
    //      is not modelled either.
    //
    //   4. PER-FRAME PART — only when categories 1/2 exist (counters
    //      DAT_10b4b370 / DAT_10b4b35c, R:20471). Key 0x31 (R:20485) holds the
    //      result, reused only when it matches the frame number of the
    //      viewport (`*Frame->Viewport + 0xa8/+0xac`, R:20501-20506) — i.e.
    //      the result is REBUILT every frame: copy of cache 0x18 (R:20532-20550),
    //      then in list order every category 1 or 2 element (one loop,
    //      R:20580): ComputeFromActor (frame colour — type function, Level.Brightness,
    //      R:20586), mask, filler, merge with modulation 1/2/3 (R:20732).
    //      Mask: category 2 — no shadow (FUN_10b02970(…, NULL) = 0x7f,
    //      R:20593); category 1 — its own shadow from LightBits. Within category 1
    //      SHIPPED has two caches (record flag 0x84 = 0x10b38114[effect], R:19236):
    //        * flag ≠ 0 (frame-dependent filler): key 0x15 holds the
    //          EXPANDED MASK, the filler is called every frame (R:20621-20668);
    //        * flag = 0: key 0x16 holds the FILLER OUTPUT and the filler
    //          is called only on a miss or bLightChanged (R:20679-20729,
    //          filler skip — 0x10b09493 `test ah,0x10` -> 0x10b09551);
    //          merge (frame colour and modulation 1/2/3) — every frame.
    //
    // WHAT IS AND IS NOT HERE — STATED:
    //   * cache 0x18 is ALSO created once per (lightmap, zone) — from the
    //     light state on the world's FIRST frame (StaticSnapshot), not on the
    //     frame the surface is first drawn (the port does not track visibility). This is
    //     the same state the atlas bake uses for static lightmaps;
    //   * caches 0x15/0x16 are NOT maintained: a category-1 light's filler
    //     is computed every frame. For a stationary light these are the same bytes
    //     (the filler is a function of position, radius, cone, light and mask;
    //     colour does not take part). A category-1 light with flag 0 whose
    //     inputs changed after the first frame would keep its OLD filler output
    //     in shipped: here this is detected and
    //     printed (FillerInputsChanged), not hidden;
    //   * limit of 256 records (PTR_DAT_10b3810c, step 0xc4, R:7832) — refusal.
    //   * only the LEVEL MODEL is handled. Brushes of movers without bDynamicLightMover
    //     take the same branch in shipped but with their own model (Brush +0x168,
    //     R:19854); the port bakes their lightmaps as layer sets, and their per-frame part
    //     (categories 1/2) is NOT assembled — a named debt (the "mover_" set
    //     bake prints the number of lost references).
    public static class LevelSurfLightmap
    {
        // Lightmap zones: per lightmap — the DISTINCT iZone[1] of render nodes carrying its surface,
        // in node order (node filter — NumVertices ≥ 3, has a surface, not PF_Invisible,
        // has a lightmap; same rule as the bake — lmzones), then iZone[0] of the nodes OccludeBsp may draw from behind
        // (BackDrawable). A lightmap tile is per (lightmap, zone) pair: shipped cache keys 0x18/0x31 contain the zone byte
        // (R:20290-20300, R:20480-20484) and the zone is iZone[side] (R:5872-5874). One function for Core and the frontend
        // atlas layout.
        // nearBack: also iZone[0] of the one-sided nodes whose sides are different zones — drawn from behind only with the
        // eye within 1 unit of the plane (R:5879-5880); shipped creates their caches when such a draw happens, Core adds
        // the zone then (LevelSurfLightmapRuntime.AddBackZones); the frontend atlas reserves their slots up front.
        public static int[][] ZonesOfLightMaps(StaticGeometry g, bool nearBack = false)
        {
            int n = g.LightMaps.Length;
            var lists = new List<int>[n];
            for (int i = 0; i < g.Nodes.Length; i++)
            {
                StaticNode nd = g.Nodes[i];
                if (nd.NumVertices < 3 || nd.Surf < 0 || nd.Surf >= g.Surfaces.Length) continue;
                StaticSurface s = g.Surfaces[nd.Surf];
                if ((s.PolyFlags & 0x1) != 0 || s.LightMap < 0 || s.LightMap >= n) continue;
                var l = lists[s.LightMap] ??= new List<int>();
                if (!l.Contains(nd.Zone1)) l.Add(nd.Zone1);
            }
            for (int i = 0; i < g.Nodes.Length; i++)
            {
                StaticNode nd = g.Nodes[i];
                if (nd.NumVertices < 3 || nd.Surf < 0 || nd.Surf >= g.Surfaces.Length) continue;
                StaticSurface s = g.Surfaces[nd.Surf];
                if ((s.PolyFlags & 0x1) != 0 || s.LightMap < 0 || s.LightMap >= n || !BackDrawable(s.PolyFlags)) continue;
                var l = lists[s.LightMap];
                if (!l.Contains(nd.Zone0)) l.Add(nd.Zone0);
            }
            for (int i = 0; nearBack && i < g.Nodes.Length; i++)
            {
                StaticNode nd = g.Nodes[i];
                if (nd.NumVertices < 3 || nd.Surf < 0 || nd.Surf >= g.Surfaces.Length || nd.Zone0 == nd.Zone1) continue;
                StaticSurface s = g.Surfaces[nd.Surf];
                if ((s.PolyFlags & 0x1) != 0 || s.LightMap < 0 || s.LightMap >= n) continue;
                var l = lists[s.LightMap];
                if (!l.Contains(nd.Zone0)) l.Add(nd.Zone0);   // the frontend atlas slot
            }
            var r = new int[n][];
            for (int i = 0; i < n; i++) r[i] = lists[i]?.ToArray() ?? Array.Empty<int>();
            return r;
        }

        // OccludeBsp draws a node seen from behind when its surface flags carry PF_TwoSided|PF_Portal (0x4000100) —
        // otherwise only with the eye within 1 unit behind its plane (Render.dll.c:5879-5880, UEOcclusion.Surface).
        public static bool BackDrawable(int surfPolyFlags) => (surfPolyFlags & 0x4000100) != 0;

        public const int MaxEntries = 256;   // (0x10b5ae48 − 0x10b4ea48) / 0xc4

        // An element accepted by the categoriser: index IN THE LIST (the mask
        // is addressed by it) and category.
        public struct Entry { public int ListIndex; public int Cat; }

        // Categorise a lightmap's list (level branch, param_1 = 0). list[k] —
        // light k of the list; null — the reference is absent from the world (shipped
        // cannot read such an element; counted as missing). Returns true if
        // categories 1/2 exist (per-frame part).
        public static bool Classify(IReadOnlyList<MoverMode3.LightIn?> list, List<Entry> accepted, ref int missing,
                                    bool noDynamicLights)
        {
            accepted.Clear();
            bool frame = false;
            for (int k = 0; k < list.Count; k++)
            {
                if (list[k] is not MoverMode3.LightIn l) { missing++; continue; }
                int cat = MoverMode3.Categorize(in l, noDynamicLights);
                if (cat < 0 || cat == 3) continue;
                if (accepted.Count >= MaxEntries)
                    throw new InvalidOperationException($"lightmap: more than {MaxEntries} accepted lights — " +
                        "shipped would refuse via the categorizer (PTR_DAT_10b3810c, R:7832); the data has no such case");
                accepted.Add(new Entry { ListIndex = k, Cat = cat });
                if (cat != 0) frame = true;
            }
            return frame;
        }

        // Offset of list element k's shadow mask in LightBits (R:19864-19880):
        // DataOffset + k·(VClamp·((UClamp+7)>>3)).
        public static int ShadowOffset(in StaticLightMap m, int listIndex)
            => m.DataOffset + listIndex * (m.VClamp * ((m.UClamp + 7) >> 3));

        // SHADOW MASK EXPANSION — FUN_10b02970 (R:16682-16775) with the kernel row
        // table 0x10b4b374 (R:16532-16548): kernel {24,40,24 / 40,64,40 /
        // 24,40,24}, Σ = 320; for each kernel row the sum of weights of
        // set bits is converted to a byte by INTEGER division (s·255)/320, and
        // the bytes of the three rows are added. Edge — the edge bit/row repeats.
        // Verified texel-for-texel against shipped; single owner of the rule (C7).
        // Output — W×H mask bytes as float.
        static readonly int[] KernelWeights = { 24, 40, 24, 40, 64, 40, 24, 40, 24 };
        const int KernelSum = 320;

        public static float[] ExpandShadowMask(byte[] lightBits, int offset, int width, int height)
        {
            int pitch = (width + 7) / 8;
            int bytes = pitch * height;
            if (offset < 0 || offset + bytes > lightBits.Length)
                throw new InvalidOperationException($"shadow mask outside LightBits (offset {offset}, {bytes} bytes of {lightBits.Length})");
            var blur = new float[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int value = 0;
                    for (int yy = -1; yy <= 1; yy++)
                    {
                        int rowSum = 0;
                        for (int xx = -1; xx <= 1; xx++)
                        {
                            int sx = Math.Min(Math.Max(x + xx, 0), width - 1);
                            int sy = Math.Min(Math.Max(y + yy, 0), height - 1);
                            if ((lightBits[offset + sy * pitch + (sx >> 3)] & (1 << (sx & 7))) != 0)
                                rowSum += KernelWeights[(yy + 1) * 3 + xx + 1];
                        }
                        value += rowSum * 255 / KernelSum;
                    }
                    blur[y * width + x] = value;
                }
            return blur;
        }

        // Cached part (key 0x18): seed + category 0 in list order,
        // each with its own mask. shadowOf(listIndex) — the expanded mask.
        // fx — the frame on which the cache is CREATED: ComputeFromActor in the cache loop
        // (R:20437) also multiplies colour by that frame's Level.Brightness (R:19276);
        // null — no multiplier (atlas bake: LevelInfo.Brightness = 1 in all
        // 41 maps per census).
        public static int FillStatic(UEVector[] acc, in SurfLightmap.Surface s, in SurfLightmap.Map m,
                                     in SurfLightmap.Basis basis, UEVector seed,
                                     IReadOnlyList<MoverMode3.LightIn?> list, List<Entry> accepted,
                                     Func<int, float[]> shadowOf, SurfLightmap.FrameFx fx = null)
        {
            for (int t = 0; t < m.Width * m.Height; t++) acc[t] = seed;
            if (basis.Degenerate) return 0;
            int touched = 0;
            for (int e = 0; e < accepted.Count; e++)
            {
                if (accepted[e].Cat != 0) continue;
                SurfLightmap.Light L = LightOf(list[accepted[e].ListIndex].Value);
                if (SurfLightmap.FillLight(acc, in s, in m, in basis, in L, shadowOf(accepted[e].ListIndex), fx)) touched++;
            }
            return touched;
        }

        // Per-frame part (key 0x31): acc = copy of cache 0x18, then
        // categories 1 (own mask) and 2 (no shadow) in list order, with frame fx.
        public static int FillFrame(UEVector[] acc, UEVector[] staticAcc, in SurfLightmap.Surface s, in SurfLightmap.Map m,
                                    in SurfLightmap.Basis basis, IReadOnlyList<MoverMode3.LightIn?> list,
                                    List<Entry> accepted, Func<int, float[]> shadowOf, SurfLightmap.FrameFx fx)
        {
            if (fx is null) throw new InvalidOperationException("frame lightmap: FrameFx not passed");
            int n = m.Width * m.Height;
            Array.Copy(staticAcc, acc, n);
            if (basis.Degenerate) return 0;
            int touched = 0;
            for (int e = 0; e < accepted.Count; e++)
            {
                int cat = accepted[e].Cat;
                if (cat != 1 && cat != 2) continue;
                SurfLightmap.Light L = LightOf(list[accepted[e].ListIndex].Value);
                float[] mask = cat == 1 ? shadowOf(accepted[e].ListIndex) : null;
                if (SurfLightmap.FillLight(acc, in s, in m, in basis, in L, mask, fx)) touched++;
            }
            return touched;
        }

        static SurfLightmap.Light LightOf(in MoverMode3.LightIn l)
        {
            SurfLightmap.Light L = l.L;
            L.BrightnessScale = l.Scale;
            return L;
        }

        // Surface face in world space — exactly as the slot-3 caller assembles it
        // (Render.dll.c:1252-1273): Origin = Points[pBase], X = Vectors[vTextureU],
        // Y = Vectors[vTextureV], Z = Vectors[vNormal].
        public static SurfLightmap.Surface SurfaceOf(StaticGeometry g, in StaticSurface s)
            => new SurfLightmap.Surface
            {
                B = g.Points[s.PBase], U = g.Vectors[s.VTextureU],
                V = g.Vectors[s.VTextureV], N = g.Vectors[s.VNormal],
            };

        public static SurfLightmap.Map MapOf(in StaticLightMap lm)
            => new SurfLightmap.Map
            {
                PanX = lm.Pan.x, PanY = lm.Pan.y, UScale = lm.UScale, VScale = lm.VScale,
                Width = lm.UClamp, Height = lm.VClamp,
            };
    }

    // PER-FRAME WORLD LIGHTMAPS — owned by Core (C5/C7): the frontend receives
    // ready tile bytes and only puts them into its atlas.
    //
    // Unit — (lightmap, zone): shipped cache keys 0x18/0x31 contain the
    // zone byte (R:20290-20300, R:20480-20484). Tile zone — the front-facing
    // iZone[1] of the nodes carrying the surface (URender::OccludeBsp walk,
    // Render.dll.c:5886/5827; same rule as the bake — lmzones). A lightmap
    // whose nodes lie in DIFFERENT zones gets one tile per zone — each with
    // its own ambient seed.
    public sealed class LevelSurfLightmapRuntime
    {
        public sealed class Tile
        {
            public int LightMap, Zone, Width, Height;
            // Bytes 0..127 per channel, lightmap row j — array row j,
            // order R,G,B (as the atlas bake writes, EncodeChan).
            public byte[] Rgb;
            // true — per-frame lightmap (cache 0x31); false — the lightmap
            // no longer has categories 1/2 and has returned to cache 0x18.
            public bool Frame;
        }

        readonly StaticGeometry _g;
        readonly byte[] _bits;
        readonly int[][] _zonesOf;            // per lightmap: distinct node zones (LevelSurfLightmap.ZonesOfLightMaps)
        readonly int[][] _list;               // per lightmap: list exports (without 0)
        readonly List<Tile> _tiles = new List<Tile>();
        // Frame lights by export number. An array, not a dictionary: lookup happens
        // for every reference of every lightmap every frame (a dictionary cost ~0.8 ms
        // in a player profile). Same semantics: the last write wins.
        MoverMode3.LightIn[] _byExport = new MoverMode3.LightIn[1024];
        bool[] _hasExport = new bool[1024];
        readonly List<int> _exportsSet = new List<int>();
        readonly List<int> _previousSet = new List<int>();
        // Exports some lightmap list references: only their snapshots are read (LookupExport, AnyRefChanged), so the
        // per-frame conversion and fingerprint skip every other actor (CPU only; the arrays still grow as before).
        bool[] _referenced = Array.Empty<bool>();
        readonly List<MoverMode3.LightIn?> _scratchList = new List<MoverMode3.LightIn?>();
        readonly List<LevelSurfLightmap.Entry> _scratchAcc = new List<LevelSurfLightmap.Entry>();
        UEVector[] _acc = new UEVector[0];

        // Cache 0x18 per lightmap: list snapshot on the first frame and the assembled seed+cat.0 —
        // per zone (Acc[zi]/Seed[zi], zi — index in _zonesOf[lm]; a lightmap without nodes has one, zone 0).
        sealed class StaticPart
        {
            public MoverMode3.LightIn?[] List;
            public List<LevelSurfLightmap.Entry> Accepted;
            public UEVector[][] Acc;         // null element — not assembled yet
            public UEVector[] Seed;
            public SurfLightmap.FrameFx Fx;  // snapshot frame (Level.Brightness at cache creation)
        }
        readonly StaticPart[] _static;
        readonly bool[] _wasFrame;
        readonly ulong[] _lastKey;
        // SKIPPING UNCHANGED LIGHTMAPS (Core frame, Update). A lightmap's result is a function of
        // inputs: the lights of its list, Level.Brightness and — for frame-dependent
        // effects — tables/time. If no light of the list
        // changed, there are no frame effects and the level brightness is the same, the previous frame
        // already produced exactly this result (the same tile or its absence), and the loop does
        // not recompute it. A direct UpdateLights call (corpus check) does not skip.
        readonly bool[] _lmEvaluated, _lmNeedsFrame, _lmIsFrame, _lmHadDyn;
        readonly bool[] _grown;   // a zone was added this frame (AddBackZones): its tile is built now
        readonly int[] _lmMissing;
        ulong[] _exportFp = Array.Empty<ulong>();
        bool[] _exportChanged = Array.Empty<bool>();
        readonly List<int> _changedExports = new List<int>();
        bool _skipValid;
        float _lastLevelBrightness = float.NaN;
        bool _lastNoDynamicLights;
        public int SkippedLightmaps;
        // Only for checking skip equivalence (HP1_SURF_LM_NOSKIP in headless).
        public bool DisableSkip;
        uint _generation;
        readonly Dictionary<long, float[]> _masks = new Dictionary<long, float[]>();
        readonly Dictionary<long, ulong> _fillerKey = new Dictionary<long, ulong>();
        bool _snapshotTaken;

        public readonly List<int> MultiZoneLightmaps = new List<int>();
        public readonly List<string> Refusals = new List<string>();
        readonly HashSet<int> _refused = new HashSet<int>();
        // Counters (V8): which branches ran.
        public int FrameLightmaps { get; private set; }     // lightmaps with categories 1/2 this frame
        public int RestoredLightmaps { get; private set; }  // returned to cache 0x18 this frame
        public int MissingLights { get; private set; }      // list references with no actor in the frame
        public long FrameUpdates { get; private set; }
        public long FillerInputsChanged { get; private set; }

        public LevelSurfLightmapRuntime(StaticGeometry g, byte[] lightBits)
        {
            _g = g ?? throw new ArgumentNullException(nameof(g));
            _bits = lightBits ?? throw new ArgumentNullException(nameof(lightBits));
            int n = g.LightMaps.Length;
            _list = new int[n][];
            _static = new StaticPart[n];
            _wasFrame = new bool[n];
            _lastKey = new ulong[n];
            _lmEvaluated = new bool[n];
            _lmNeedsFrame = new bool[n];
            _lmIsFrame = new bool[n];
            _lmHadDyn = new bool[n];
            _lmMissing = new int[n];
            _grown = new bool[n];
            _zonesOf = LevelSurfLightmap.ZonesOfLightMaps(g);
            for (int i = 0; i < n; i++) if (_zonesOf[i].Length > 1) MultiZoneLightmaps.Add(i);
            for (int i = 0; i < n; i++)
            {
                int start = g.LightMaps[i].LightActors;
                var refs = new List<int>();
                if (start >= 0)
                    for (int k = start; k < g.LightExports.Length && g.LightExports[k] != 0; k++) refs.Add(g.LightExports[k]);
                _list[i] = refs.ToArray();
            }
            foreach (int[] refs in _list)
                foreach (int e in refs)
                {
                    if (e < 0) continue;
                    if (e >= _referenced.Length) Array.Resize(ref _referenced, Math.Max(e + 1, _referenced.Length * 2));
                    _referenced[e] = true;
                }
        }

        public int LightMapCount => _list.Length;
        public IReadOnlyList<int> ZonesOf(int lightMap) => _zonesOf[lightMap];
        public IReadOnlyList<Tile> Tiles => _tiles;
        public void ClearTiles() => _tiles.Clear();

        // A lightmap's surface — the first surface referencing it
        // (every FBspSurf has its own lightmap; no repeats in the data — checked).
        int[] _surfOf;
        int SurfaceOfLightMap(int lm)
        {
            if (_surfOf == null)
            {
                _surfOf = new int[_list.Length];
                for (int i = 0; i < _surfOf.Length; i++) _surfOf[i] = -1;
                for (int s = 0; s < _g.Surfaces.Length; s++)
                {
                    int l = _g.Surfaces[s].LightMap;
                    if (l < 0 || l >= _surfOf.Length) continue;
                    if (_surfOf[l] >= 0 && _surfOf[l] != s)
                        throw new InvalidOperationException($"lightmap {l}: referenced by two surfaces ({_surfOf[l]}, {s})");
                    _surfOf[l] = s;
                }
            }
            return _surfOf[lm];
        }

        // One frame. frame — the Core frame (light snapshots, level ambient);
        // fx — render frame state (UEWorldFrameProducer.LightmapFx);
        // zoneAmbient(zone) — the zone's Ambient{Hue,Saturation,Brightness} bytes
        // (zone actor, else LevelInfo — shipped fallback R:5996-6000).
        // Result — Tiles: tiles to rewrite IN THIS frame.
        // surfDyn — the +0x48 list per surface for this frame (light ObjectIds in list order), null — none.
        public void Update(WorldFrame frame, SurfLightmap.FrameFx fx, Func<int, (int h, int s, int b)> zoneAmbient,
                           IReadOnlyDictionary<int, int[]> surfDyn = null)
        {
            if (frame?.Actors == null || fx == null) { _tiles.Clear(); FrameLightmaps = 0; RestoredLightmaps = 0; return; }
            foreach (int e in _changedExports) _exportChanged[e] = false;
            _changedExports.Clear();
            // A vanished export is a change (its light in the list became null).
            foreach (int e in _exportsSet) { _hasExport[e] = false; }
            List<int> previous = null;
            if (_exportsSet.Count != 0) { _previousSet.Clear(); _previousSet.AddRange(_exportsSet); previous = _previousSet; }
            _exportsSet.Clear();
            ReadOnlySpan<ActorSnapshot> actors = frame.ActorSpan;
            for (int i = 0; i < actors.Length; i++)
            {
                ref readonly ActorSnapshot a = ref actors[i];
                if (a.Destroyed || a.ExportIndex <= 0) continue;
                int ex = a.ExportIndex;
                if (ex >= _byExport.Length)
                {
                    int n = Math.Max(ex + 1, _byExport.Length * 2);
                    Array.Resize(ref _byExport, n);
                    Array.Resize(ref _hasExport, n);
                }
                if (ex >= _exportFp.Length)
                {
                    int n = Math.Max(ex + 1, _exportFp.Length * 2);
                    Array.Resize(ref _exportFp, n);
                    Array.Resize(ref _exportChanged, n);
                }
                if (ex >= _referenced.Length || !_referenced[ex]) continue;
                MoverMode3.LightIn li = MoverMode3.FromSnapshot(in a);
                _byExport[ex] = li;
                ulong fp = Fingerprint(in li);
                if (_exportFp[ex] != fp) { _exportFp[ex] = fp; MarkChanged(ex); }
                if (!_hasExport[ex]) { _hasExport[ex] = true; _exportsSet.Add(ex); }
            }
            if (previous != null)
                foreach (int e in previous)
                    if (!_hasExport[e]) { _exportFp[e] = 0; MarkChanged(e); }
            AddBackZones(frame);
            Func<int, IReadOnlyList<MoverMode3.LightIn>> dynOf = null;
            if (surfDyn is { Count: > 0 })
            {
                var byId = new Dictionary<int, MoverMode3.LightIn?>();
                foreach (int[] ids in surfDyn.Values) foreach (int id in ids) byId[id] = null;
                for (int i = 0; i < actors.Length; i++)
                    if (byId.ContainsKey(actors[i].ObjectId)) byId[actors[i].ObjectId] = MoverMode3.FromSnapshot(in actors[i]);
                // The lists are made in DrawFrame, the snapshot after RenderOverlays/PostRender: a light destroyed by script in
                // between is not in the frame. Shipped builds the lightmap inside DrawFrame from the live actor (it would
                // still apply it); the port drops it and counts it (DynamicLightsLost) — the same snapshot timing as the
                // iLightActors path (D-63).
                var lists = new Dictionary<int, MoverMode3.LightIn[]>();
                foreach (var kv in surfDyn)
                {
                    var l = new List<MoverMode3.LightIn>(kv.Value.Length);
                    foreach (int id in kv.Value)
                        if (byId[id] is MoverMode3.LightIn li) l.Add(li); else DynamicLightsLost++;
                    lists[kv.Key] = l.ToArray();
                }
                dynOf = lm => { int sf = SurfaceOfLightMap(lm); return sf >= 0 && lists.TryGetValue(sf, out var d) ? d : null; };
            }
            _skipValid = true;
            try { UpdateLights(_lookup ??= LookupExport, fx, zoneAmbient, dynOf); }
            finally { _skipValid = false; }
        }

        // A one-sided node drawn from behind (eye within 1 unit of its plane, R:5879-5880) is lit with iZone[0]
        // (R:5872-5874); the lightmap cache key holds the zone byte (R:20290-20300, R:20480-20484), so shipped makes that
        // (lightmap, zone) cache when the draw happens. Here: the zone joins the lightmap's list on the first frame that
        // draws such a node and its tile is built in the same frame (from the cache-0x18 snapshot, as every zone's).
        public int BackZonesAdded { get; private set; }
        void AddBackZones(WorldFrame frame)
        {
            if (frame.Drawn is null) return;
            foreach (DrawnWorld dw in frame.Drawn)
                foreach (int node in dw.BackNodes)
                {
                    if ((uint)node >= (uint)_g.Nodes.Length) continue;
                    StaticNode nd = _g.Nodes[node];
                    if (nd.Surf < 0 || nd.Surf >= _g.Surfaces.Length) continue;
                    int lm = _g.Surfaces[nd.Surf].LightMap;
                    if ((uint)lm >= (uint)_zonesOf.Length || Array.IndexOf(_zonesOf[lm], nd.Zone0) >= 0) continue;
                    int[] z = _zonesOf[lm];
                    Array.Resize(ref z, z.Length + 1);
                    z[^1] = nd.Zone0;
                    _zonesOf[lm] = z;
                    _grown[lm] = true;
                    BackZonesAdded++;
                }
        }

        void MarkChanged(int ex)
        {
            if (_exportChanged[ex]) return;
            _exportChanged[ex] = true;
            _changedExports.Add(ex);
        }

        // Fingerprint of ALL light inputs the lightmap reads (Light fields
        // except the name, LightIn flags, multiplier). 0 — "no light".
        static ulong Fingerprint(in MoverMode3.LightIn l)
        {
            ulong h = 0xcbf29ce484222325UL;
            void Mix(uint v) { h ^= v; h *= 0x100000001b3UL; }
            SurfLightmap.Light L = l.L;
            Mix((uint)l.Export);
            Mix((uint)BitConverter.SingleToInt32Bits(L.Pos.x)); Mix((uint)BitConverter.SingleToInt32Bits(L.Pos.y));
            Mix((uint)BitConverter.SingleToInt32Bits(L.Pos.z)); Mix((uint)BitConverter.SingleToInt32Bits(L.DrawScale));
            Mix((uint)L.Pitch); Mix((uint)L.Yaw); Mix((uint)L.Roll);
            Mix((uint)(L.Type | (L.Effect << 8) | (L.Brightness << 16) | (L.Hue << 24)));
            Mix((uint)(L.Saturation | (L.Radius << 8) | (L.RadiusInner << 16) | (L.Cone << 24)));
            Mix((uint)((L.DarkLight ? 1 : 0) | (L.Period << 8) | (L.Phase << 16)));
            Mix((uint)L.Source);
            Mix((uint)BitConverter.SingleToInt32Bits(L.BrightnessScale));
            Mix((uint)((l.Static ? 1 : 0) | (l.NoDelete ? 2 : 0) | (l.DynamicLight ? 4 : 0) | (l.SpecialLit ? 8 : 0)));
            Mix((uint)BitConverter.SingleToInt32Bits(l.Scale));
            return h == 0 ? 1UL : h;
        }

        bool AnyRefChanged(int[] refs)
        {
            for (int k = 0; k < refs.Length; k++)
            {
                int e = refs[k];
                if ((uint)e >= (uint)_exportChanged.Length || _exportChanged[e]) return true;
            }
            return false;
        }

        Func<int, MoverMode3.LightIn?> _lookup;
        MoverMode3.LightIn? LookupExport(int export)
            => (uint)export < (uint)_hasExport.Length && _hasExport[export]
                ? _byExport[export] : (MoverMode3.LightIn?)null;

        // Frame body: a list light by export — lightOf (Update builds it
        // from frame snapshots via MoverMode3.FromSnapshot; the
        // validate_level_lightmap.py check feeds corpus lights).
        public int DynamicLightmaps { get; private set; }   // lightmaps with +0x48 records this frame
        public long DynamicLightsLost;                        // +0x48 lights destroyed between DrawFrame and the snapshot
        public void UpdateLights(Func<int, MoverMode3.LightIn?> lightOf, SurfLightmap.FrameFx fx,
                                 Func<int, (int h, int s, int b)> zoneAmbient,
                                 Func<int, IReadOnlyList<MoverMode3.LightIn>> dynOf = null)
        {
            DynamicLightmaps = 0;
            _tiles.Clear();
            FrameLightmaps = 0; RestoredLightmaps = 0;
            if (fx == null) return;
            _generation++;
            bool first = !_snapshotTaken;
            _snapshotTaken = true;
            // The categories depend on UClient.NoDynamicLights: a change re-classifies every lightmap.
            bool skipOk = _skipValid && !DisableSkip && !first && fx.LevelBrightness.Equals(_lastLevelBrightness)
                          && fx.NoDynamicLights == _lastNoDynamicLights;
            _lastLevelBrightness = fx.LevelBrightness;
            _lastNoDynamicLights = fx.NoDynamicLights;
            for (int lm = 0; lm < _list.Length; lm++)
            {
                if (_refused.Contains(lm)) continue;
                bool grown = _grown[lm];
                _grown[lm] = false;
                int[] refs = _list[lm];
                IReadOnlyList<MoverMode3.LightIn> dyn = dynOf?.Invoke(lm);
                int dynCount = dyn?.Count ?? 0;
                bool hadDyn = _lmHadDyn[lm];
                _lmHadDyn[lm] = dynCount > 0;
                if (dynCount > 0) DynamicLightmaps++;
                if (refs.Length == 0 && dynCount == 0 && !hadDyn)
                {
                    // A lightmap without lights: zone ambient seed only. Its static
                    // tile is emitted on the first frame — the atlas is assembled from Core, not
                    // from a baked PNG.
                    if (first) EmitStatic(lm, Array.Empty<MoverMode3.LightIn?>(), fx, zoneAmbient);
                    else if (grown) EmitStatic(lm, null, fx, zoneAmbient);
                    continue;
                }
                if (skipOk && !grown && dynCount == 0 && !hadDyn && _lmEvaluated[lm] && !_lmNeedsFrame[lm] && !AnyRefChanged(refs))
                {
                    // Same result as the previous frame: its counters apply.
                    MissingLights += _lmMissing[lm];
                    if (_lmIsFrame[lm]) FrameLightmaps++;
                    SkippedLightmaps++;
                    continue;
                }
                int missing = 0;
                _scratchList.Clear();
                for (int k = 0; k < refs.Length; k++) _scratchList.Add(lightOf(refs[k]));
                if (first)
                {
                    // Snapshot for cache 0x18 — world state on the first frame (the iLightActors records only).
                    var sp = new StaticPart { List = _scratchList.ToArray(), Accepted = new List<LevelSurfLightmap.Entry>(), Fx = fx };
                    int m0 = 0;
                    LevelSurfLightmap.Classify(sp.List, sp.Accepted, ref m0, fx.NoDynamicLights);
                    _static[lm] = sp;
                }
                // +0x48 records after the iLightActors records (R:19892-19894); no LightBits mask (pointer 0).
                for (int k = 0; k < dynCount; k++) _scratchList.Add(dyn[k]);
                bool isFrame = LevelSurfLightmap.Classify(_scratchList, _scratchAcc, ref missing, fx.NoDynamicLights);
                foreach (LevelSurfLightmap.Entry e in _scratchAcc)
                    if (e.ListIndex >= refs.Length && e.Cat is 0 or 1)
                        throw new InvalidOperationException($"lightmap {lm}: a +0x48 light got category {e.Cat} — the SetupDynamics gate admits only category 2/3 lights");
                MissingLights += missing;
                _lmEvaluated[lm] = true;
                _lmIsFrame[lm] = isFrame;
                _lmMissing[lm] = missing;
                _lmNeedsFrame[lm] = false;
                if (isFrame) { FrameLightmaps++; CheckFillerInputs(lm); }
                else if (!_wasFrame[lm])
                {
                    // First frame: the static tile (cache 0x18) of every non-per-frame lightmap —
                    // the atlas is built from Core; such tiles do not change afterwards.
                    if (first || grown) EmitStatic(lm, null, fx, zoneAmbient);
                    continue;
                }
                // The frame's result is a function of inputs: cache 0x18, the set and state
                // of category 1/2 lights and the frame (Level.Brightness; for
                // effects 1/2/3/5/16 — also tables and time). Unchanged inputs
                // give the same bytes and the tile is not rewritten (the frontend keeps
                // the previous one). This is not a SHIPPED cache, just avoided rework.
                ulong key = isFrame ? FrameKey(fx, out _lmNeedsFrame[lm]) : 0UL;
                if (isFrame && _wasFrame[lm] && _lastKey[lm] == key && !grown) continue;
                try
                {
                    int before = _tiles.Count;
                    BuildTiles(lm, isFrame, fx, zoneAmbient, _tiles);
                    if (_tiles.Count == before) continue;
                    if (isFrame) FrameUpdates++; else RestoredLightmaps++;
                    _wasFrame[lm] = isFrame;
                    _lastKey[lm] = key;
                }
                catch (InvalidOperationException e)
                {
                    _refused.Add(lm);
                    Refusals.Add($"lightmap {lm}: {e.Message}");
                }
            }
        }

        // Static lightmap tile on the first frame. list != null — snapshot for a lightmap without
        // references (the others already took their snapshot above on the same frame).
        public int StaticTilesEmitted { get; private set; }
        void EmitStatic(int lm, MoverMode3.LightIn?[] list, SurfLightmap.FrameFx fx, Func<int, (int h, int s, int b)> zoneAmbient)
        {
            if (list != null)
                _static[lm] = new StaticPart { List = list, Accepted = new List<LevelSurfLightmap.Entry>(), Fx = fx };
            try
            {
                int before = _tiles.Count;
                BuildTiles(lm, false, fx, zoneAmbient, _tiles);
                StaticTilesEmitted += _tiles.Count - before;
            }
            catch (InvalidOperationException e)
            {
                _refused.Add(lm);
                Refusals.Add($"lightmap {lm}: {e.Message}");
            }
        }

        ulong FrameKey(SurfLightmap.FrameFx fx, out bool frameDependent)
        {
            ulong h = 0xcbf29ce484222325UL;
            void Mix(uint v) { h ^= v; h *= 0x100000001b3UL; }
            Mix((uint)BitConverter.SingleToInt32Bits(fx.LevelBrightness));
            bool needsFrame = false;
            for (int e = 0; e < _scratchAcc.Count; e++)
            {
                int cat = _scratchAcc[e].Cat;
                if (cat == 0) continue;
                MoverMode3.LightIn l = _scratchList[_scratchAcc[e].ListIndex].Value;
                SurfLightmap.Light L = l.L;
                Mix((uint)(_scratchAcc[e].ListIndex | (cat << 24)));
                Mix((uint)BitConverter.SingleToInt32Bits(L.Pos.x)); Mix((uint)BitConverter.SingleToInt32Bits(L.Pos.y));
                Mix((uint)BitConverter.SingleToInt32Bits(L.Pos.z)); Mix((uint)BitConverter.SingleToInt32Bits(L.DrawScale));
                Mix((uint)L.Pitch); Mix((uint)L.Yaw); Mix((uint)L.Roll);
                Mix((uint)(L.Type | (L.Effect << 8) | (L.Brightness << 16) | (L.Hue << 24)));
                Mix((uint)(L.Saturation | (L.Radius << 8) | (L.RadiusInner << 16) | (L.Cone << 24)));
                Mix((uint)((L.DarkLight ? 1 : 0) | (L.Source << 1) | (L.Period << 8) | (L.Phase << 16)));
                Mix((uint)BitConverter.SingleToInt32Bits(l.Scale));
                if (SurfLightmap.NeedsFrame(L.Effect)) needsFrame = true;
            }
            if (needsFrame) Mix(_generation);
            frameDependent = needsFrame;
            return h;
        }

        // Lightmap tiles — one per zone of its nodes (a lightmap without nodes gets one, zone −1
        // with zone 0's seed). Empty — no surface or zero size.
        void BuildTiles(int lm, bool isFrame, SurfLightmap.FrameFx fx, Func<int, (int h, int s, int b)> zoneAmbient, List<Tile> into)
        {
            int surf = SurfaceOfLightMap(lm);
            if (surf < 0) return;
            StaticLightMap rec = _g.LightMaps[lm];
            if (rec.UClamp <= 0 || rec.VClamp <= 0) return;
            SurfLightmap.Surface sf = LevelSurfLightmap.SurfaceOf(_g, in _g.Surfaces[surf]);
            SurfLightmap.Map map = LevelSurfLightmap.MapOf(in rec);
            SurfLightmap.Basis basis = SurfLightmap.ComputeBasis(in sf, in map);
            int n = map.Width * map.Height;
            StaticPart sp = _static[lm];
            int[] zones = _zonesOf[lm];
            int count = Math.Max(1, zones.Length);
            sp.Acc ??= new UEVector[count][];
            sp.Seed ??= new UEVector[count];
            if (sp.Acc.Length < count) { Array.Resize(ref sp.Acc, count); Array.Resize(ref sp.Seed, count); }   // AddBackZones
            if (_acc.Length < n) _acc = new UEVector[n];
            for (int zi = 0; zi < count; zi++)
            {
                int zone = zones.Length == 0 ? -1 : zones[zi];
                if (sp.Acc[zi] == null)
                {
                    (int h, int s, int b) amb = zoneAmbient(zone < 0 ? 0 : zone);
                    sp.Seed[zi] = SurfLightmap.AmbientSeed(amb.h, amb.s, amb.b);
                    sp.Acc[zi] = new UEVector[n];
                    _maskLm = lm; _maskLen = int.MaxValue;
                    LevelSurfLightmap.FillStatic(sp.Acc[zi], in sf, in map, in basis, sp.Seed[zi], sp.List, sp.Accepted,
                        _maskAt ??= MaskAt, sp.Fx);
                }
                // A +0x48 record (index past the iLightActors list) has mask pointer 0: the categoriser zeroes it
                // (DAT_10b5b67c[0x13] = 0, R:7877) and only the list loop sets it (R:19886).
                _maskLm = lm; _maskLen = _list[lm].Length;
                if (isFrame)
                    LevelSurfLightmap.FillFrame(_acc, sp.Acc[zi], in sf, in map, in basis, _scratchList, _scratchAcc,
                        _maskAt ??= MaskAt, fx);
                else Array.Copy(sp.Acc[zi], _acc, n);
                var t = new Tile { LightMap = lm, Zone = zone, Width = map.Width, Height = map.Height, Rgb = new byte[n * 3], Frame = isFrame };
                for (int i = 0; i < n; i++)
                {
                    t.Rgb[i * 3] = SurfLightmap.EncodeChan(_acc[i].x);
                    t.Rgb[i * 3 + 1] = SurfLightmap.EncodeChan(_acc[i].y);
                    t.Rgb[i * 3 + 2] = SurfLightmap.EncodeChan(_acc[i].z);
                }
                into.Add(t);
            }
        }

        // The fillers' shadowOf (called synchronously): MaskOf(_maskLm, k) for k < _maskLen, else null — one delegate
        // instead of two closures per tile (allocation only).
        int _maskLm, _maskLen;
        Func<int, float[]> _maskAt;
        float[] MaskAt(int k) => k < _maskLen ? MaskOf(_maskLm, k) : null;

        float[] MaskOf(int lm, int listIndex)
        {
            long key = ((long)lm << 32) | (uint)listIndex;
            if (_masks.TryGetValue(key, out float[] m)) return m;
            StaticLightMap rec = _g.LightMaps[lm];
            m = LevelSurfLightmap.ExpandShadowMask(_bits, LevelSurfLightmap.ShadowOffset(in rec, listIndex), rec.UClamp, rec.VClamp);
            _masks[key] = m;
            return m;
        }

        // Shipped cache 0x16: a category-1 light's filler output with flag
        // 0x84 = 0 is created once. If its inputs changed, shipped
        // would show the OLD output and the port the new one; this is counted and printed.
        void CheckFillerInputs(int lm)
        {
            for (int e = 0; e < _scratchAcc.Count; e++)
            {
                if (_scratchAcc[e].Cat != 1) continue;
                MoverMode3.LightIn l = _scratchList[_scratchAcc[e].ListIndex].Value;
                if (MoverMode3.EffectIsDynamic(l.L.Effect)) continue;
                ulong h = 0xcbf29ce484222325UL;
                void Mix(uint v) { h ^= v; h *= 0x100000001b3UL; }
                Mix((uint)BitConverter.SingleToInt32Bits(l.L.Pos.x)); Mix((uint)BitConverter.SingleToInt32Bits(l.L.Pos.y));
                Mix((uint)BitConverter.SingleToInt32Bits(l.L.Pos.z)); Mix((uint)BitConverter.SingleToInt32Bits(l.L.DrawScale));
                Mix((uint)l.L.Pitch); Mix((uint)l.L.Yaw); Mix((uint)l.L.Roll);
                Mix((uint)(l.L.Radius | (l.L.RadiusInner << 8) | (l.L.Cone << 16) | (l.L.Effect << 24))); Mix((uint)l.L.Source);
                long key = ((long)lm << 32) | (uint)_scratchAcc[e].ListIndex;
                if (_fillerKey.TryGetValue(key, out ulong prev))
                {
                    if (prev != h) { FillerInputsChanged++; _fillerKey[key] = h; }
                }
                else _fillerKey[key] = h;
            }
        }

        // For fixtures/checks: the lightmap's list exports.
        public IReadOnlyList<int> ListOf(int lightMap) => _list[lightMap];
    }
}
