using System;
using System.Collections.Generic;

namespace HP1.UEScript
{
    // PER-FRAME LIGHTMAPS OF MOVER SURFACES — owned by Core.
    //
    // SetupForSurf takes a mover surface (BrushTracker->SurfIsDynamic, Render.dll.c:19845-19856) into one of two branches:
    //   * a mover without bDynamicLightMover — the LEVEL branch with the mover's brush model (+0x168): MoverLevelLightmap,
    //     run once per frame for every (lightmap, zone) whose tracker fragment Core's occlusion drew in this frame's tree
    //     (UESceneNode.DrawnPolys; zone iZone[side] of the fragment node, Render.dll.c:5872-5874);
    //   * a mover with bDynamicLightMover — the mover branch (MoverMode3Lightmaps).
    // Both light the face the caller hands SetupForSurf: the tracker surface (Points[pBase], Vectors[vTextureU/V/vNormal] of
    // Level->Model, Render.dll 0x10b25c84..0x10b25d42) where FMovingBrushTracker last put the brush polygon (TrackerFace).
    // Tiles: one list per Core frame, in write order (level branch, mode 3); the frontend copies them into its mover atlas,
    // one slot per mover lightmap (Tile.Zone = the slot zone of the mover geometry: a slot name, not a lighting input).
    // A polygon drawn through zones whose lightmaps differ (another ambient, or a cache 0x18 made at another pose) keeps its
    // current tile: the frontend draws a mover polygon whole, per-fragment pieces are not ported (B12, SpanningLightmaps).
    public sealed class UEMoverLightmaps
    {
        readonly UELevelRuntime _rt;
        public readonly UEMoverGeometry Geo;
        public readonly MoverMode3Lightmaps Mode3;
        public readonly List<LevelSurfLightmapRuntime.Tile> Tiles = new();
        public readonly List<string> Refusals = new();

        // A lit polygon of a level-branch mover. Movers sharing a brush share its lightmap body: shipped keys the caches by
        // the brush model (0x18/0x31: model index, iLightMap, zone byte; 0x16: light index, iSurf — the brush's surfaces).
        sealed class Lm
        {
            public int LightMap, SlotZone;
            public int[] Refs;              // iLightActors exports (Model->Lights, null-terminated), without the 0
            public MoverLevelLightmap Body;
            public byte[] Shown;            // bytes in the atlas slot (null: none written yet)
            public int ShownKey = -1;
            public bool Refused;
        }
        readonly List<Lm> _lms = new();
        readonly Dictionary<(int MoverId, int Poly), int> _lmOf = new();
        readonly HashSet<int> _referenced = new();
        public int SpanningLightmaps, ZoneSwitches, MissingLights;
        readonly Dictionary<(int Brush, int LightMap), MoverLevelLightmap> _bodies = new();

        public UEMoverLightmaps(UELevelRuntime rt, WorldFrame frame)
        {
            _rt = rt;
            var zoneById = new Dictionary<int, int>();
            foreach (ref readonly ActorSnapshot a in frame.ActorSpan) zoneById[a.ObjectId] = a.ZoneNumber;
            Geo = rt.BuildMoverGeometry(o => zoneById.TryGetValue(o.ObjectId, out int z) ? z : 0);
            Mode3 = new MoverMode3Lightmaps(rt, Geo, TrackerSurfOf);
            StaticGeometry g = Geo.Geometry;
            foreach (UEMoverGeometry.Mover m in Geo.Movers)
            {
                if (UEInterpreter.ToBool(m.Actor.GetProp("bDynamicLightMover"))) continue;
                int brush = ((UEModelRef)m.Actor.GetProp(PK.Brush)).ExportIndex;
                for (int n = m.FirstNode; n < m.FirstNode + m.NodeCount; n++)
                {
                    int s = g.Nodes[n].Surf, lm = g.Surfaces[s].LightMap;
                    if (lm < 0) continue;
                    StaticLightMap rec = g.LightMaps[lm];
                    if (rec.UClamp <= 0 || rec.VClamp <= 0) continue;
                    var refs = new List<int>();
                    for (int k = rec.LightActors; k >= 0 && k < g.LightExports.Length && g.LightExports[k] != 0; k++) refs.Add(g.LightExports[k]);
                    var key = (brush, lm - m.FirstLightMap);
                    if (!_bodies.TryGetValue(key, out MoverLevelLightmap body)) _bodies[key] = body = new MoverLevelLightmap(in rec, Geo.LightBits);
                    _lmOf[(m.Actor.ObjectId, Geo.PolyOfSurf[s])] = _lms.Count;
                    _lms.Add(new Lm { LightMap = lm, SlotZone = g.Nodes[n].Zone1, Refs = refs.ToArray(), Body = body });
                    _referenced.UnionWith(refs);
                }
            }
        }

        // FPoly.iLink after SetupActorBrush: the tracker surface of a brush polygon (-1 when the mover is not tracked).
        int TrackerSurfOf(UEObject mover, int poly)
        {
            HeadlessBspModel.TrackedBrushModel bm = _rt.Bsp?.Tracker is null ? null : _rt.TrackedBrushModelOf(mover);
            return bm is not null && poly >= 0 && poly < bm.Link.Length ? bm.Link[poly] : -1;
        }

        // Original: Render.dll ?DrawFrame@URender@@QAEXPAUFSceneNode@@@Z [HP1 0x10B254F0] (the slot-3 face, 0x10b25c84..0x10b25d42)
        // The face SetupForSurf gets for a tracker surface (0x10b25c84..0x10b25d42): Origin = Points[pBase],
        // X = Vectors[vTextureU], Y = Vectors[vTextureV], Z = Vectors[vNormal] of the level model as the tracker last wrote them.
        internal static SurfLightmap.Surface TrackerFace(HeadlessBspModel m, int surf)
        {
            BspSurfRecord s = m.Surf(surf);
            return new SurfLightmap.Surface { B = m.LivePoint(s.PBase), U = m.Vector(s.VTextureU), V = m.Vector(s.VTextureV), N = m.Vector(s.VNormal) };
        }

        // One Core frame. surfDyn — UEWorldFrameProducer.SurfDynamicLights (tracker iSurf -> light ObjectIds); master — the
        // frame's occlusion tree (null: no OccludeBsp this frame, nothing drawn).
        public void Update(WorldFrame frame, SurfLightmap.FrameFx fx, Func<int, (int h, int s, int b)> zoneAmbient,
                           IReadOnlyDictionary<int, int[]> surfDyn, UESceneNode master)
        {
            Tiles.Clear();
            if (frame is not null && fx is not null && master is not null) LevelBranch(frame, fx, zoneAmbient, surfDyn, master);
            Tiles.AddRange(Mode3.Update(frame, fx, surfDyn));
        }

        readonly SortedDictionary<int, (int Surf, List<int> Zones)> _drawn = new();
        readonly Dictionary<int, MoverMode3.LightIn> _byExport = new(), _byId = new();
        readonly List<MoverMode3.LightIn?> _records = new();

        // Original: Render.dll not exported: FUN_10b077f0 FLightManager::SetupForSurf [HP1 0x10B077F0] — slot 3 of the FLightManager vtable 0x10b386d4 (unwind string "FLightManager::SetupForSurf") (one call per drawn tracker fragment's record)
        void LevelBranch(WorldFrame frame, SurfLightmap.FrameFx fx, Func<int, (int h, int s, int b)> zoneAmbient,
                         IReadOnlyDictionary<int, int[]> surfDyn, UESceneNode master)
        {
            HeadlessBspModel m = _rt.Bsp;
            if (m?.Tracker is null) return;
            _drawn.Clear();
            void Walk(UESceneNode f)
            {
                foreach (var (surf, node, _, back) in f.DrawnPolys)
                {
                    if (node < m.LoadedNodeCount || m.Tracker.NodeOwner(node) is not UEObject mover) continue;
                    if (!_lmOf.TryGetValue((mover.ObjectId, m.Surf(surf).BrushPoly), out int li)) continue;
                    m.LiveNode(node, out _, out _, out _, out _, out _, out _, out _, out _, out int z0, out int z1, out _, out _, out _, out _, out _, out _, out _);
                    if (!_drawn.TryGetValue(li, out var d)) _drawn[li] = d = (surf, new List<int>());
                    int zone = back ? z0 : z1;
                    if (!d.Zones.Contains(zone)) d.Zones.Add(zone);
                }
                foreach (UESceneNode c in f.Children) Walk(c);
            }
            Walk(master);
            if (_drawn.Count == 0) return;
            _byExport.Clear(); _byId.Clear();
            var want = new HashSet<int>();
            if (surfDyn is not null) foreach (var (_, d) in _drawn) if (surfDyn.TryGetValue(d.Surf, out int[] ids)) want.UnionWith(ids);
            foreach (ref readonly ActorSnapshot a in frame.ActorSpan)
            {
                if (a.Destroyed) continue;
                if (a.ExportIndex > 0 && _referenced.Contains(a.ExportIndex)) _byExport[a.ExportIndex] = MoverMode3.FromSnapshot(in a);
                if (want.Contains(a.ObjectId)) _byId[a.ObjectId] = MoverMode3.FromSnapshot(in a);
            }
            foreach (var (li, d) in _drawn)
            {
                Lm e = _lms[li];
                if (e.Refused) continue;
                try
                {
                    SurfLightmap.Surface face = TrackerFace(m, d.Surf);
                    _records.Clear();
                    foreach (int ex in e.Refs) _records.Add(_byExport.TryGetValue(ex, out var l) ? l : (MoverMode3.LightIn?)null);
                    // The draw record's +0x48 list (URender::SurfLights[iSurf]) after the iLightActors records (R:19892-19894).
                    // A light destroyed between DrawFrame and the snapshot is not in the frame (as LevelSurfLightmapRuntime).
                    if (surfDyn is not null && surfDyn.TryGetValue(d.Surf, out int[] dyn))
                        foreach (int id in dyn) if (_byId.TryGetValue(id, out var l)) _records.Add(l);
                    UEVector[] texels = null; int shownKey = -1; bool frameLm = false, spanning = false;
                    foreach (int zone in d.Zones)
                    {
                        int key = _rt.ZoneCacheByte(zone);
                        (int h, int s, int b) amb = zoneAmbient(zone);
                        int missing = 0;
                        UEVector[] t = e.Body.Draw(in face, key, SurfLightmap.AmbientSeed(amb.h, amb.s, amb.b), _records, e.Refs.Length, fx, ref missing, out bool fr);
                        MissingLights += missing;
                        if (texels is null) { texels = t; shownKey = key; frameLm = fr; }
                        else if (!t.AsSpan().SequenceEqual(texels)) spanning = true;
                    }
                    if (spanning) { SpanningLightmaps++; continue; }
                    var rgb = new byte[texels.Length * 3];
                    for (int i = 0; i < texels.Length; i++)
                    {
                        rgb[i * 3] = SurfLightmap.EncodeChan(texels[i].x);
                        rgb[i * 3 + 1] = SurfLightmap.EncodeChan(texels[i].y);
                        rgb[i * 3 + 2] = SurfLightmap.EncodeChan(texels[i].z);
                    }
                    if (e.Shown is not null && e.Shown.AsSpan().SequenceEqual(rgb)) continue;
                    if (e.ShownKey >= 0 && e.ShownKey != shownKey) ZoneSwitches++;
                    e.Shown = rgb; e.ShownKey = shownKey;
                    Tiles.Add(new LevelSurfLightmapRuntime.Tile { LightMap = e.LightMap, Zone = e.SlotZone, Width = e.Body.Width, Height = e.Body.Height, Rgb = rgb, Frame = frameLm });
                }
                catch (InvalidOperationException ex)
                {
                    e.Refused = true;
                    string why = $"mover lightmap {e.LightMap}: {ex.Message}";
                    Refusals.Add(why);
                    HP1.Core.UETrace.Warn("[MoverLightmaps] REFUSED " + why);
                }
            }
        }
    }

    // ONE MOVER LIGHTMAP IN THE LEVEL BRANCH — slot 3 of FLightManager::SetupForSurf (FUN_10b077f0) with the brush model of a
    // mover without bDynamicLightMover (+0x168, Render.dll.c:19845-19856), called as shipped calls it: per draw record of
    // the polygon's tracker surface, with that frame's face (UEMoverLightmaps.TrackerFace).
    //   * cache 0x18 (zone seed + category 0 with its own masks; key brush model + iLightMap + zone byte, R:20290-20323) is
    //     made on the first draw of (lightmap, zone byte) at that draw's face, lights and Level.Brightness, and then kept:
    //     the level branch zeroes the mover pointer (0x10b07c83 `mov [ebp-0x4c],ebx`, ebx = 0 at 0x10b07c70), so the
    //     cache's pose test (0x10b08a87 `test edi,edi; je 0x10b08b2e`; mover +0xa4/+0xfc..+0x110 against the cache header,
    //     0x10b08a9a..0x10b08b2c) never runs here — the static part keeps the pose of its first draw (the mover branch,
    //     bDynamicLightMover, runs that test);
    //   * the per-frame part (0x31, rebuilt every frame: copy of 0x18 + categories 1/2 in record order, R:20480-20732) at the
    //     face of the draw; a category-1 light with flag 0x84 = 0 merges its cache 0x16 (SurfLightmap.Held, one per light:
    //     the key is light index + iSurf) from its first per-frame build.
    // NOT MODELLED (as the level surfaces, LevelSurfLightmap): FMemCache eviction (a cache dropped by LRU is made again at
    // the pose of its next draw) and bLightChanged (DAT_10b5f684; set only by the editor).
    public sealed class MoverLevelLightmap
    {
        readonly StaticLightMap _rec;
        readonly SurfLightmap.Map _map;
        readonly byte[] _bits;
        readonly Dictionary<int, UEVector[]> _static = new();         // zone byte -> cache 0x18
        readonly Dictionary<int, SurfLightmap.Held> _held = new();    // light export -> cache 0x16
        readonly Dictionary<int, float[]> _masks = new();
        readonly List<LevelSurfLightmap.Entry> _acc = new();
        IReadOnlyList<MoverMode3.LightIn?> _list;
        int _refs;

        public MoverLevelLightmap(in StaticLightMap rec, byte[] lightBits)
        {
            _rec = rec;
            _map = LevelSurfLightmap.MapOf(in rec);
            _bits = lightBits ?? throw new ArgumentNullException(nameof(lightBits));
        }
        public int Width => _map.Width;
        public int Height => _map.Height;
        public int StaticParts => _static.Count;

        // One draw record. face — this frame's tracker surface; zoneKey — the record's zone byte (UELevelRuntime.ZoneCacheByte)
        // and seed its ambient; records — the iLightActors list (null: the reference has no actor), then the +0x48 lights;
        // refs — how many come from the list (only they have a LightBits mask: DataOffset + k·VClamp·((UClamp+7)>>3),
        // R:19864-19880). Returns the texels (W×H, channels 0..127); frame — the per-frame result (0x31), else cache 0x18.
        // Original: Render.dll not exported: FUN_10b077f0 FLightManager::SetupForSurf [HP1 0x10B077F0] — slot 3 of the FLightManager vtable 0x10b386d4 (unwind string "FLightManager::SetupForSurf") (the level branch 0x10b07c6a..0x10b07cdc, caches 0x18/0x31/0x16)
        public UEVector[] Draw(in SurfLightmap.Surface face, int zoneKey, UEVector seed, IReadOnlyList<MoverMode3.LightIn?> records,
                               int refs, SurfLightmap.FrameFx fx, ref int missing, out bool frame)
        {
            if (fx is null) throw new InvalidOperationException("mover lightmap: FrameFx not passed");
            frame = LevelSurfLightmap.Classify(records, _acc, ref missing, fx.NoDynamicLights);
            foreach (LevelSurfLightmap.Entry e in _acc)
                if (e.ListIndex >= refs && e.Cat is 0 or 1)
                    throw new InvalidOperationException($"a +0x48 light got category {e.Cat} — the SetupDynamics gate admits only category 2/3 lights");
            _list = records; _refs = refs;
            SurfLightmap.Basis basis = SurfLightmap.ComputeBasis(in face, in _map);
            if (!_static.TryGetValue(zoneKey, out UEVector[] st))
            {
                st = new UEVector[_map.Width * _map.Height];
                LevelSurfLightmap.FillStatic(st, in face, in _map, in basis, seed, records, _acc, _maskAt ??= MaskAt, fx);
                _static[zoneKey] = st;
            }
            if (!frame) return st;
            var acc = new UEVector[st.Length];
            LevelSurfLightmap.FillFrame(acc, st, in face, in _map, in basis, records, _acc, _maskAt ??= MaskAt, fx, _heldAt ??= HeldAt);
            return acc;
        }

        Func<int, float[]> _maskAt;
        Func<int, SurfLightmap.Held> _heldAt;
        float[] MaskAt(int k)
        {
            if (k >= _refs) return null;   // a +0x48 record: mask pointer 0 (the categoriser zeroes it, R:7877)
            if (_masks.TryGetValue(k, out float[] m)) return m;
            m = LevelSurfLightmap.ExpandShadowMask(_bits, LevelSurfLightmap.ShadowOffset(in _rec, k), _rec.UClamp, _rec.VClamp);
            _masks[k] = m;
            return m;
        }
        SurfLightmap.Held HeldAt(int k)
        {
            int export = _list[k].Value.Export;
            if (export <= 0) throw new InvalidOperationException("a category-1 light without a map export (bStatic/bNoDelete actors are placed)");
            if (!_held.TryGetValue(export, out SurfLightmap.Held h)) _held[export] = h = new SurfLightmap.Held();
            return h;
        }
    }

    public sealed partial class UELevelRuntime
    {
        UEMoverLightmaps _moverLightmaps;
        WorldStatic _moverLightmapsWorld;

        // Mover lightmaps of the CURRENT world (built on the first frame of a world, like SurfaceLightmaps).
        public UEMoverLightmaps MoverLightmaps
        {
            get
            {
                HP1.Core.UECoreOwner.Check("UELevelRuntime.MoverLightmaps");
                WorldStatic w = LatestWorldStatic;
                if (w?.Geometry is null || LatestFrame is null || Bsp is null) return null;
                if (_moverLightmaps is not null && ReferenceEquals(_moverLightmapsWorld, w)) return _moverLightmaps;
                _moverLightmaps = new UEMoverLightmaps(this, LatestFrame);
                _moverLightmapsWorld = w;
                return _moverLightmaps;
            }
        }
    }
}
