using System;
using System.Collections.Generic;

namespace HP1.UEScript
{
    // PER-FRAME LIGHTMAPS OF MODE-3 MOVERS (bDynamicLightMover) — MoverMode3 wiring over mover geometry.
    //
    // Slot-3 mover branch (Render.dll.c:19876-19930): such a mover takes its lights not from the brush lists
    // (the editor does not bake them) but from the LEAF it currently stands in (Model->Leaves[Region.iLeaf].iPermeating ->
    // Model->Lights), with the face in its CURRENT pose. Selection (MoverMode3.Select) and fill (MoverMode3.FillSurface)
    // are verified against shipped (validate_mover_light_branch.py, validate_mover_mode3_fill.py); this only applies them:
    //   * face: brush B/U/V/N (FPoly) through BrushToWorldCoords(mover) — same coordinates as the vertices;
    //   * seed — ambient of the mover's CURRENT zone; no shadow mask (NoShadowMask, as in shipped);
    //   * tile — same format as level lightmaps (LevelSurfLightmapRuntime.Tile); tile zone = atlas slot zone.
    // A mover is recomputed only when its pose/leaf/zone/leaf lights or a light's per-frame effect changes.
    // Dynamic lights: the draw record's +0x48 list = URender::SurfLights of the face's tracker surface (UEOcclusion
    // FilterLight, via UEWorldFrameProducer.SurfDynamicLights), each element whose bSpecialLit equals the mover's
    // (0x10b07b38-0x10b07b49) through thunk(mover, light) — the mover itself rejected, the rest category 2 (R:19905-19909);
    // after the leaf records, in list order. With no mover leaf (iLeaf == -1) the whole branch is skipped.
    public sealed class MoverMode3Lightmaps
    {
        sealed class Face { public int LightMap, Zone, TrackerSurf; public UEVector B, U, V, N; public SurfLightmap.Map Map; }
        sealed class Entry { public UEObject Actor; public List<Face> Faces = new(); public ulong Key; }

        readonly UELevelRuntime _rt;
        readonly List<Entry> _movers = new();
        readonly List<LevelSurfLightmapRuntime.Tile> _out = new();
        readonly Dictionary<int, int> _byExport = new();   // export -> frame actor index (last wins); not whole ~500-byte snapshots
        readonly Dictionary<int, int> _indexOfId = new();
        readonly List<MoverMode3.LightIn> _leaf = new();
        readonly Dictionary<int, MoverMode3.LightIn> _byId = new();
        readonly List<int> _order = new();
        readonly List<MoverMode3.LightIn> _all = new();   // leaf, then dynamic — shipped record order
        public int Movers => _movers.Count;
        public int TilesBuilt { get; private set; }
        public int DynamicApplied { get; private set; }   // accepted faces × dynamic lights, cumulative (V8)

        public MoverMode3Lightmaps(UELevelRuntime rt, UEMoverGeometry geo, Func<UEObject, int, int> trackerSurfOf)
        {
            _rt = rt;
            StaticGeometry g = geo.Geometry;
            int[][] zones = LevelSurfLightmap.ZonesOfLightMaps(g);
            foreach (UEMoverGeometry.Mover m in geo.Movers)
            {
                if (!UEInterpreter.ToBool(m.Actor.GetProp("bDynamicLightMover"))) continue;
                var e = new Entry { Actor = m.Actor };
                for (int n = m.FirstNode; n < m.FirstNode + m.NodeCount; n++)
                {
                    StaticSurface s = g.Surfaces[g.Nodes[n].Surf];
                    if (s.LightMap < 0 || s.LightMap >= g.LightMaps.Length) continue;
                    StaticLightMap rec = g.LightMaps[s.LightMap];
                    if (rec.UClamp <= 0 || rec.VClamp <= 0) continue;
                    e.Faces.Add(new Face
                    {
                        LightMap = s.LightMap, Zone = zones[s.LightMap].Length > 0 ? zones[s.LightMap][0] : -1,
                        B = geo.LocalOfPoint[s.PBase], U = geo.LocalOfVector[s.VTextureU],
                        V = geo.LocalOfVector[s.VTextureV], N = geo.LocalOfVector[s.VNormal],
                        Map = LevelSurfLightmap.MapOf(in rec),
                        TrackerSurf = trackerSurfOf(m.Actor, geo.PolyOfSurf[g.Nodes[n].Surf]),
                    });
                }
                if (e.Faces.Count > 0) _movers.Add(e);
            }
        }

        // Tiles of this frame (changed movers only). The list is owned by this object until the next call.
        public IReadOnlyList<LevelSurfLightmapRuntime.Tile> Update(WorldFrame frame, SurfLightmap.FrameFx fx,
                                                                    IReadOnlyDictionary<int, int[]> surfDyn)
        {
            _out.Clear();
            StaticGeometry level = _rt.LatestWorldStatic?.Geometry;
            if (frame is null || level is null || _movers.Count == 0) return _out;
            _byExport.Clear();
            // First actor with the id, as the former linear scan (TryAdd keeps the first occurrence).
            _indexOfId.Clear();
            ReadOnlySpan<ActorSnapshot> actors = frame.ActorSpan;
            for (int i = 0; i < actors.Length; i++) _indexOfId.TryAdd(actors[i].ObjectId, i);
            for (int i = 0; i < actors.Length; i++) { int ex = actors[i].ExportIndex; if (ex > 0) _byExport[ex] = i; }
            _byId.Clear();
            if (surfDyn is { Count: > 0 })
            {
                var want = new HashSet<int>();
                foreach (int[] ids in surfDyn.Values) want.UnionWith(ids);
                foreach (ref readonly ActorSnapshot a in actors) if (want.Contains(a.ObjectId)) _byId[a.ObjectId] = MoverMode3.FromSnapshot(in a);
            }
            foreach (Entry e in _movers)
            {
                if (!_indexOfId.TryGetValue(e.Actor.ObjectId, out int meIndex)) continue;
                ref readonly ActorSnapshot me = ref actors[meIndex];
                // Mover leaf lights in pool order (Model->Lights, null-terminated).
                _leaf.Clear();
                int leaf = me.Leaf;
                bool branch = leaf >= 0 && level.LeafPermeating != null && leaf < level.LeafPermeating.Length;
                if (branch && level.LeafPermeating[leaf] >= 0)
                    for (int k = level.LeafPermeating[leaf]; k < level.LightExports.Length && level.LightExports[k] != 0; k++)
                        if (_byExport.TryGetValue(level.LightExports[k], out int li))
                        {
                            _leaf.Add(MoverMode3.FromSnapshot(in actors[li]));
                        }
                int nLeaf = _leaf.Count;
                bool specialLit = me.Light.SpecialLit;
                _all.Clear(); _all.AddRange(_leaf);
                // Per face: the +0x48 elements that pass (bSpecialLit pair, not the mover itself), appended after the leaf.
                var faceDyn = new List<(int From, int Count)>();
                foreach (Face f in e.Faces)
                {
                    int from = _all.Count;
                    if (branch && surfDyn is not null && f.TrackerSurf >= 0 && surfDyn.TryGetValue(f.TrackerSurf, out int[] ids))
                        foreach (int id in ids)
                        {
                            if (id == e.Actor.ObjectId || !_byId.TryGetValue(id, out MoverMode3.LightIn l)) continue;
                            if (l.SpecialLit != specialLit) continue;
                            if (l.L.Type == 0 || l.L.Brightness == 0) continue;   // thunk: +0x1e0 / +0x1e2
                            _all.Add(l);
                        }
                    faceDyn.Add((from, _all.Count - from));
                }
                ulong key = KeyOf(in me, fx, frame.Tick);
                if (key == e.Key) continue;
                e.Key = key;
                BrushCoords c = _rt.BrushToWorldCoords(e.Actor);
                (int h, int s, int b) amb = _rt.ZoneAmbientHSB(me.ZoneNumber);
                UEVector seed = SurfLightmap.AmbientSeed(amb.h, amb.s, amb.b);
                for (int fi = 0; fi < e.Faces.Count; fi++)
                {
                    Face f = e.Faces[fi];
                    var sf = new SurfLightmap.Surface
                    {
                        B = c.TransformPoint(f.B), U = c.TransformVector(f.U), V = c.TransformVector(f.V), N = c.TransformVector(f.N),
                    };
                    int rs = 0, rsp = 0, c3 = 0, rd = 0;
                    MoverMode3.Select(_leaf, in sf.B, in sf.N, specialLit,
                        (fx ?? throw new InvalidOperationException("mode-3 mover lightmap: no FrameFx (UClient.NoDynamicLights)")).NoDynamicLights,
                        _order, ref rs, ref rsp, ref c3, ref rd);
                    for (int j = 0; j < faceDyn[fi].Count; j++) { _order.Add(faceDyn[fi].From + j); DynamicApplied++; }
                    int n = f.Map.Width * f.Map.Height;
                    var acc = new UEVector[n];
                    MoverMode3.FillSurface(acc, in sf, in f.Map, seed, _all, _order, fx);
                    var t = new LevelSurfLightmapRuntime.Tile { LightMap = f.LightMap, Zone = f.Zone, Width = f.Map.Width, Height = f.Map.Height, Rgb = new byte[n * 3], Frame = true };
                    for (int i = 0; i < n; i++)
                    {
                        t.Rgb[i * 3] = SurfLightmap.EncodeChan(acc[i].x);
                        t.Rgb[i * 3 + 1] = SurfLightmap.EncodeChan(acc[i].y);
                        t.Rgb[i * 3 + 2] = SurfLightmap.EncodeChan(acc[i].z);
                    }
                    _out.Add(t);
                }
            }
            TilesBuilt += _out.Count;
            return _out;
        }

        ulong KeyOf(in ActorSnapshot me, SurfLightmap.FrameFx fx, int tick)
        {
            ulong h = 0xcbf29ce484222325UL;
            void Mix(uint v) { h ^= v; h *= 0x100000001b3UL; }
            void F(float f) => Mix((uint)BitConverter.SingleToInt32Bits(f));
            F(me.Location.x); F(me.Location.y); F(me.Location.z);
            Mix((uint)me.Rotation.Pitch); Mix((uint)me.Rotation.Yaw); Mix((uint)me.Rotation.Roll);
            Mix((uint)me.Leaf); Mix((uint)me.ZoneNumber); Mix(me.Light.SpecialLit ? 1u : 0u);
            if (fx != null) F(fx.LevelBrightness);
            bool frameDep = false;
            foreach (MoverMode3.LightIn l in _all)
            {
                SurfLightmap.Light L = l.L;
                Mix((uint)l.Export); F(L.Pos.x); F(L.Pos.y); F(L.Pos.z); F(L.DrawScale); F(l.Scale);
                Mix((uint)L.Pitch); Mix((uint)L.Yaw); Mix((uint)L.Roll);
                Mix((uint)(L.Type | (L.Effect << 8) | (L.Brightness << 16) | (L.Hue << 24)));
                Mix((uint)(L.Saturation | (L.Radius << 8) | (L.RadiusInner << 16) | (L.Cone << 24)));
                Mix((uint)((L.DarkLight ? 1 : 0) | (L.Source << 1) | (L.Period << 8) | (L.Phase << 16)));
                Mix((uint)((l.Static ? 1 : 0) | (l.NoDelete ? 2 : 0) | (l.DynamicLight ? 4 : 0) | (l.SpecialLit ? 8 : 0)));
                if (SurfLightmap.NeedsFrame(L.Effect)) frameDep = true;
            }
            if (frameDep) Mix((uint)tick);
            return h;
        }
    }
}
