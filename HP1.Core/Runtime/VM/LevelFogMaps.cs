using System;
using System.Collections.Generic;

namespace HP1.UEScript
{
    // ZONE FOG MAPS OF LEVEL SURFACES, PER FRAME OF THE OCCLUSION TREE (C4, moved from the frontend 2026-10-16).
    //
    // FLightManager::SetupForSurf builds a surface's fog map with the frame it is drawing (Render.dll.c:20006-20260:
    // light records in that frame's Coords, ComputeFromActor :479-500), so a surface drawn by the master frame and by a
    // mirror gets two different maps. Here, after the tick's DrawWorld, for every frame of the tree (UESceneNode: master,
    // then its children in the WorldFrame.Views order) and every fog surface that frame drew (UESceneNode.DrawnPolys):
    //   * surfaces — a level node with a lightmap, without PF_Unlit/PF_Translucent (surface | texture flags; translucent
    //     takes no fog map: SetupForSurf `(Draw->PolyFlags & 4) == 0`, Render.dll.c:19944-19947), in a zone whose ZoneInfo
    //     has bFogZone (:19936); one map per DRAW RECORD (C1, 2026-11-01): OccludeBsp merges a polygon into the frame's
    //     record of its surface and zone actor unless the polygon's flags have any of 0x4010047 — then the polygon is a
    //     record of its own (Render.dll.c:5928-5953; the search 0x10b2338c..0x10b23437 reads the flags BEFORE the
    //     translucent-mirror reflag 0x10b23aa1, so such a mirror never merges); SetupForSurf builds the fog map of each
    //     record from its own +0x40 light list (Render.dll.c:19943-20010) and flags it 0x10 so D3DDrv uploads it on every
    //     SetTexture (D3DDrv.dll.c:3435). The records are numbered statically (Records): (surface, zone) for a mergeable
    //     surface, (node, zone) for a no-merge one; texel grid SurfLightmap.ComputeBasis of the lightmap's first surface;
    //   * lights — the leaf volumetric lights of the leaves this frame's walk handed to LeafVolumetricLighting
    //     (UESceneNode.VolumetricLeaves -> StaticGeometry.LeafVolumetric -> LightExports) with its camera-sphere /
    //     side-plane cull (FrameStaticVolumetric), and
    //     the dynamic lights the same walk appended: +0x14-flagged LeafLights links of those leaves (FilterLight's leaf
    //     half, UESceneNode.VolumetricDynamic), one Stamp with the statics; far first (FogMap.SortFarFirst);
    //   * light colour — FGetHSV(Hue, Sat, 255) · Level.Brightness · (LightBrightness · type scale)/255
    //     (ComputeFromActor: `Level->Brightness (+0x44c) * this+0x3c` multiplies +0x8c..+0x98, Render.dll.c:19274-19280);
    //     FogMap.K takes the brightness without Level.Brightness (+0x3c is not rescaled).
    // Tiles: Tiles[frame][record] = UClamp·VClamp texels, BGRA7 as the FColor before the D3DDrv ×2 (FogMap.Build).
    // Which run reads which record — and whether a draw takes a fog map at all — is Core's (FogRecord, review F5); the
    // frontend only copies the tiles into its atlases.
    public sealed class LevelFogMaps
    {
        public sealed class Surf { public int LightMap, UClamp, VClamp; public SurfLightmap.Basis Basis; }
        // A draw record's fog map: Key = index in Records, Of = its lightmap's Surf.
        public sealed class Record { public int Key; public Surf Of; }

        const int PF_Invisible = 0x1, PF_Translucent = 0x4, PF_Unlit = 0x400000;
        readonly Dictionary<int, Surf> _byNode = new();
        readonly HashSet<int> _translucentMirror = new();   // nodes fogged only when drawn reflagged (DrawnWorld.Reflagged)
        // OccludeBsp merges a polygon into the frame's record of its surface and zone actor only when the record flags have
        // none of 0x4010047 (Render.dll.c:5930-5953); otherwise every polygon makes its own record.
        const int PF_NoMergeMask = 0x4010047;
        readonly HashSet<int> _noMerge = new();
        public readonly Dictionary<int, Surf> ByLightMap = new();
        public readonly List<Record> Records = new();
        readonly Dictionary<(bool Own, int Id, int Zone), Record> _recordOf = new();   // (no-merge, node | surface, zone)
        readonly StaticGeometry _g;
        readonly HashSet<int> _fogZones;
        readonly List<FogMap.Light> _lights = new();
        public readonly List<Dictionary<int, byte[]>> Tiles = new();
        // Per frame: record -> polygons merged into it, for the records that have a fog map in the original's sense (a
        // non-empty light list); compared with the live journal by tools/origprobe/compare_fog_records.py.
        public readonly List<Dictionary<int, int>> RecordPolys = new();
        public int LastLights;

        // texFlags(surf): the surface texture's PolyFlags word (Surf->Texture->PolyFlags).
        public LevelFogMaps(StaticGeometry g, Func<int, int> texFlags, HashSet<int> fogZones)
        {
            _fogZones = fogZones; _g = g;
            for (int n = 0; n < g.Nodes.Length; n++)
            {
                StaticNode node = g.Nodes[n];
                if (node.Surf < 0 || node.Surf >= g.Surfaces.Length || node.NumVertices < 3) continue;
                StaticSurface s = g.Surfaces[node.Surf];
                // The fog test reads the zone of the side the eye is on (iZone[side], Render.dll.c:5872-5874, :19936); any node
                // may be drawn from behind (a one-sided one with the eye within 1 unit of its plane, :5879-5880).
                if (!fogZones.Contains(node.Zone1) && !fogZones.Contains(node.Zone0)) continue;
                int eff = s.PolyFlags | texFlags(node.Surf);
                if ((eff & PF_NoMergeMask) != 0) _noMerge.Add(node.Surf);
                // A translucent mirror is a candidate: with ShinySurfaces == 0 its draw record loses PF_Translucent
                // (UEOcclusion.MirrorWithoutShine) and the test reads the record's flags; Update keeps it only then.
                bool fallback = UEOcclusion.MirrorFallsBack(eff);
                if ((s.PolyFlags & PF_Invisible) != 0 || (eff & PF_Unlit) != 0 || ((eff & PF_Translucent) != 0 && !fallback)) continue;
                if (fallback) _translucentMirror.Add(n);
                if (s.LightMap < 0 || s.LightMap >= g.LightMaps.Length) continue;
                StaticLightMap lm = g.LightMaps[s.LightMap];
                if (lm.UClamp <= 0 || lm.VClamp <= 0) continue;
                if (!ByLightMap.TryGetValue(s.LightMap, out Surf f))
                {
                    var basis = SurfLightmap.ComputeBasis(
                        new SurfLightmap.Surface { B = g.Points[s.PBase], U = g.Vectors[s.VTextureU], V = g.Vectors[s.VTextureV], N = g.Vectors[s.VNormal] },
                        new SurfLightmap.Map { PanX = lm.Pan.x, PanY = lm.Pan.y, UScale = lm.UScale, VScale = lm.VScale });
                    ByLightMap[s.LightMap] = f = new Surf { LightMap = s.LightMap, UClamp = lm.UClamp, VClamp = lm.VClamp, Basis = basis };
                }
                _byNode[n] = f;
                bool own = _noMerge.Contains(node.Surf);
                foreach (int z in new[] { node.Zone1, node.Zone0 })
                {
                    if (!fogZones.Contains(z)) continue;
                    var key = (own, own ? n : node.Surf, z);
                    if (!_recordOf.ContainsKey(key)) { var r = new Record { Key = Records.Count, Of = f }; Records.Add(r); _recordOf[key] = r; }
                }
            }
        }

        Record RecordOf(int node, int zone)
        {
            int surf = _g.Nodes[node].Surf;
            bool own = _noMerge.Contains(surf);
            return _recordOf.TryGetValue((own, own ? node : surf, zone), out Record r) ? r : null;
        }

        // URender::LeafVolumetricLighting (Render.dll.c:4535-4640) for one frame: export indices of the volumetric lights
        // of fr.VolumetricLeaves (each light once per frame: the Stamp test +0xec) kept by the cull (0x10b220fb..0x10b2218d):
        // the camera inside the sphere (rv² >= (dz²+dy²)+dx²), else not below a side plane by more than rv
        // (d = ((P.z·N.z + P.x·N.x) + N.y·P.y) − W < −rv drops it). locOf/radiusOf: the light's Location and VolumeRadius.
        // leafCount: only the first leafCount leaves of the walk (a sprite's Volumetrics, UESprite.VolLeafMark); -1 = all.
        // joined (optional): (dynamic, export or ObjectId, index of the walk leaf that appended the kept light).
        // dynamic (optional): receives the ObjectIds of fr.VolumetricDynamic from those leaves — appended after each leaf's
        // static chain (Render.dll.c:4642-4680) without a cull, each light once: the actor's +0xec Stamp is shared with
        // the statics (objectOf: export -> ObjectId of the static lights).
        public static HashSet<int> FrameStaticVolumetric(StaticGeometry g, UESceneNode fr, Func<int, (bool ok, UEVector p, int vrad)> lightOf,
            int leafCount = -1, HashSet<int> dynamic = null, IReadOnlyDictionary<int, int> objectOf = null,
            Action<bool, int, int> joined = null)
        {
            var keep = new HashSet<int>();
            if (g.LeafVolumetric is null || g.LightExports is null) return keep;
            var seen = new HashSet<int>();
            var stamped = new HashSet<int>();   // dynamic ObjectIds and statics' ObjectIds (when known)
            int di = 0;
            UEVector cam = fr.Coords.Origin;
            int nLeaves = leafCount < 0 ? fr.VolumetricLeaves.Count : Math.Min(leafCount, fr.VolumetricLeaves.Count);
            for (int li = 0; li < nLeaves; li++)
            {
                int leaf = fr.VolumetricLeaves[li];
                if (leaf < 0 || leaf >= g.LeafVolumetric.Length)
                    throw new InvalidOperationException($"LeafVolumetricLighting: leaf {leaf} outside Model->Leaves ({g.LeafVolumetric.Length})");
                for (int j = g.LeafVolumetric[leaf]; j >= 0 && g.LightExports[j] != 0; j++)
                {
                    int ex = g.LightExports[j];
                    if (!seen.Add(ex)) continue;
                    if (objectOf is not null && objectOf.TryGetValue(ex, out int sid) && !stamped.Add(sid)) continue;
                    var (ok, p, vrad) = lightOf(ex);
                    if (!ok) throw new InvalidOperationException($"LeafVolumetricLighting: Model->Lights export {ex} is not a level actor");
                    float r = FogMap.WorldVolumetricRadius(vrad);
                    float dx = p.x - cam.x, dy = p.y - cam.y, dz = p.z - cam.z;
                    if (r * r < (dz * dz + dy * dy) + dx * dx)
                    {
                        bool culled = false;
                        for (int pk = 0; pk < 4 && !culled; pk++)
                        {
                            (UEVector n, float w) = fr.ViewPlanes[pk];
                            culled = ((n.z * p.z + n.x * p.x) + p.y * n.y) - w < -r;
                        }
                        if (culled) continue;
                    }
                    keep.Add(ex);
                    joined?.Invoke(false, ex, li);
                }
                for (; di < fr.VolumetricDynamic.Count && fr.VolumetricDynamic[di].Leaf <= li; di++)
                    if (stamped.Add(fr.VolumetricDynamic[di].Id))
                    {
                        dynamic?.Add(fr.VolumetricDynamic[di].Id);
                        joined?.Invoke(true, fr.VolumetricDynamic[di].Id, li);
                    }
            }
            return keep;
        }

        public static Dictionary<int, int> ObjectsByExport(IReadOnlyList<ActorSnapshot> actors)
            => FillObjectsByExport(actors, new Dictionary<int, int>());

        // The same map into a caller-owned dictionary (cleared first; reused per tick — allocation only).
        public static Dictionary<int, int> FillObjectsByExport(IReadOnlyList<ActorSnapshot> actors, Dictionary<int, int> by)
        {
            by.Clear();
            foreach (ref readonly ActorSnapshot a in WorldFrame.SnapshotSpan(actors)) if (a.ExportIndex > 0) by[a.ExportIndex] = a.ObjectId;
            return by;
        }

        public static Func<int, (bool, UEVector, int)> LightsByExport(IReadOnlyList<ActorSnapshot> actors)
        {
            var by = new Dictionary<int, (UEVector Location, int VolumeRadius)>(actors.Count);
            FillLightsByExport(actors, by);
            return LightLookup(by);
        }

        // Only the two read fields are kept: a whole ActorSnapshot per entry was ~70% of Core's per-tick allocation.
        // Caller-owned dictionary (cleared first) for the per-tick callers; LightLookup(by) reads it at call time.
        public static void FillLightsByExport(IReadOnlyList<ActorSnapshot> actors, Dictionary<int, (UEVector Location, int VolumeRadius)> by)
        {
            by.Clear();
            foreach (ref readonly ActorSnapshot a in WorldFrame.SnapshotSpan(actors)) if (a.ExportIndex > 0) by[a.ExportIndex] = (a.Location, a.Light.VolumeRadius);   // a destroyed light stays in Model->Lights
        }

        public static Func<int, (bool, UEVector, int)> LightLookup(Dictionary<int, (UEVector Location, int VolumeRadius)> by)
            => ex => by.TryGetValue(ex, out var a) ? (true, a.Location, a.VolumeRadius) : (false, default, 0);

        readonly Dictionary<int, (UEVector Location, int VolumeRadius)> _lightsBy = new();
        readonly Dictionary<int, int> _objectsBy = new();
        Func<int, (bool, UEVector, int)> _lightOf;

        // The fog record a draw of node `node` through zone `zone` with draw-record flags `polyFlags` reads, or -1: no fog
        // map — not a fog surface, a zone without bFogZone, or a translucent record (SetupForSurf `(Draw->PolyFlags & 4)
        // == 0`, Render.dll.c:19944-19947; review F5: Core decides it, the frontend passes the flags of its run).
        public int FogRecord(int node, int zone, int polyFlags)
        {
            if ((polyFlags & PF_Translucent) != 0 || !_byNode.ContainsKey(node)) return -1;
            return RecordOf(node, zone)?.Key ?? -1;
        }

        // Fog zones of the level: zones whose ZoneInfo (frame actor) has bFogZone.
        public static HashSet<int> Zones(StaticGeometry g, WorldFrame frame)
        {
            var zones = new HashSet<int>();
            if (g.ZoneActorExports is null) return zones;
            var fog = new HashSet<int>();
            foreach (ref readonly ActorSnapshot a in frame.ActorSpan) if (a.Placed && a.ExportIndex > 0 && a.FogZone) fog.Add(a.ExportIndex);
            for (int z = 0; z < g.ZoneActorExports.Length; z++) if (fog.Contains(g.ZoneActorExports[z])) zones.Add(z);
            return zones;
        }

        static float Dot(UEVector a, UEVector b) => a.x * b.x + a.y * b.y + a.z * b.z;

        // master — the tick's occlusion tree (null: no frame drew, no tiles); levelBrightness — Level.Brightness.
        public void Update(UESceneNode master, IReadOnlyList<ActorSnapshot> actors, float levelBrightness)
        {
            Tiles.Clear(); RecordPolys.Clear();
            if (master is null) return;
            var frames = new List<UESceneNode>();
            void Walk(UESceneNode f) { frames.Add(f); foreach (UESceneNode c in f.Children) Walk(c); }
            Walk(master);
            LastLights = 0;
            FillLightsByExport(actors, _lightsBy);
            Func<int, (bool, UEVector, int)> lightOf = _lightOf ??= LightLookup(_lightsBy);
            Dictionary<int, int> objectOf = null;
            foreach (UESceneNode fr in frames)
            {
                var tiles = new Dictionary<int, byte[]>();
                Tiles.Add(tiles);
                var drawn = new List<Record>();
                // One tile per draw record (surface + zone, or the polygon itself when no-merge); the record's +0x40 list
                // collects, per polygon merged into it, the volumetric lights of the walk so far that pass the polygon's
                // edge planes (UEOcclusion.PassesEdges).
                var polysOf = new Dictionary<int, List<int>>();
                var counts = new Dictionary<int, int>();
                RecordPolys.Add(counts);
                HashSet<int> opaqued = null;
                foreach (var (_, node, _) in fr.Reflagged) (opaqued ??= new HashSet<int>()).Add(node);
                for (int pi = 0; pi < fr.DrawnPolys.Count; pi++)
                {
                    var (surf, node, _, back) = fr.DrawnPolys[pi];
                    if (!_byNode.ContainsKey(node) || (_translucentMirror.Contains(node) && opaqued?.Contains(node) != true)) continue;
                    int zone = back ? _g.Nodes[node].Zone0 : _g.Nodes[node].Zone1;
                    if (!_fogZones.Contains(zone)) continue;
                    Record rec = RecordOf(node, zone);
                    if (!tiles.ContainsKey(rec.Key)) { tiles[rec.Key] = null; drawn.Add(rec); polysOf[rec.Key] = new List<int>(); }
                    polysOf[rec.Key].Add(pi); counts[rec.Key] = polysOf[rec.Key].Count;
                }
                if (drawn.Count == 0) continue;
                BrushCoords c = fr.Coords;
                UEVector cam = c.Origin, ax = c.X, ay = -c.Y, az = c.Z;
                var vol = new HashSet<int>();
                var joinStatic = new Dictionary<int, int>(); var joinDynamic = new Dictionary<int, int>();
                HashSet<int> stat = FrameStaticVolumetric(_g, fr, lightOf, -1, vol, objectOf ??= FillObjectsByExport(actors, _objectsBy),
                    (dyn, key, li) => (dyn ? joinDynamic : joinStatic)[key] = li);
                _lights.Clear();
                var lightJoin = new List<int>(); var lightAt = new List<UEVector>(); var lightVrad = new List<int>();
                foreach (ActorSnapshot a in actors)
                {
                    if (a.Destroyed) continue;
                    LightState L = a.Light;
                    if (L.Type == 0 || L.Brightness <= 0) continue;   // LightRadius is not read (LeafVolumetricLighting 0x10b22090..0x10b2236b reads only VolumeRadius +0x1eb)
                    // Dynamic: a +0x14-flagged LeafLights link of a walked leaf joined the frame's volumetric list.
                    // Static: a leaf volumetric light (LeafVolumetricLighting 0x10b220fb..0x10b2218d): kept when the camera
                    // is inside its sphere (rv² >= (dz²+dy²)+dx²), else dropped below a side plane by more than rv
                    // (d = ((P.z·L.z + P.x·L.x) + L.y·P.y) − W < −rv).
                    bool isDynamic = vol.Contains(a.ObjectId);
                    bool isStatic = !isDynamic && a.ExportIndex > 0 && stat.Contains(a.ExportIndex);
                    if (!isStatic && !isDynamic) continue;
                    UEVector p = a.Location;
                    var rel = new UEVector(p.x - cam.x, p.y - cam.y, p.z - cam.z);
                    float lx = Dot(rel, ax), ly = Dot(rel, ay), lz = Dot(rel, az);
                    _lights.Add(FogMap.FromSnapshot(in a, lx, ly, lz, levelBrightness));
                    // The light record's frame-axes position: static — LeafVolumetricLighting (Render.dll.c:4617-4624,
                    // ((dz·X.z + dy·X.y) + dx·X.x)); dynamic — the FVolActorLink of FilterLight (0x10b31da9..0x10b31e12,
                    // ((dx·X.x + dy·X.y) + dz·X.z)). PC24: float per operation.
                    float dx = p.x - cam.x, dy = p.y - cam.y, dz = p.z - cam.z;
                    lightAt.Add(isDynamic
                        ? new UEVector((dx * c.X.x + dy * c.X.y) + dz * c.X.z, (dx * c.Y.x + dy * c.Y.y) + dz * c.Y.z, (dx * c.Z.x + dy * c.Z.y) + dz * c.Z.z)
                        : new UEVector((dz * c.X.z + dy * c.X.y) + dx * c.X.x, (dz * c.Y.z + dy * c.Y.y) + dx * c.Y.x, (dz * c.Z.z + dy * c.Z.y) + dx * c.Z.x));
                    lightJoin.Add(isDynamic ? joinDynamic[a.ObjectId] : joinStatic[a.ExportIndex]);
                    lightVrad.Add(L.VolumeRadius);
                }
                LastLights += _lights.Count;
                int outsideState = 0;
                foreach (Record rec in drawn)
                {
                    Surf s = rec.Of;
                    var recLights = new List<FogMap.Light>();
                    for (int li = 0; li < _lights.Count; li++)
                        foreach (int pi in polysOf[rec.Key])
                        {
                            var (mark, edges) = fr.DrawnPolyVolumetrics[pi];
                            if (lightJoin[li] < mark && UEOcclusion.PassesEdges(edges, lightAt[li], lightVrad[li])) { recLights.Add(_lights[li]); break; }
                        }
                    // SetupForSurf gives the record a fog map only when its +0x40 list is not empty (Render.dll.c:19944-19947):
                    // without lights the tile stays all zero — the same pixels as no fog map (light·(1 − 0) + 0) — and the
                    // record is not counted in RecordPolys (the journal's SURF fogmap=0).
                    if (recLights.Count == 0) counts.Remove(rec.Key);
                    FogMap.SortFarFirst(recLights);
                    var buf = new byte[s.UClamp * s.VClamp * 4];
                    SurfLightmap.Basis b = s.Basis;
                    var p0 = new UEVector(b.StartX - cam.x, b.StartY - cam.y, b.StartZ - cam.z);
                    var su = new UEVector(b.UsX, b.UsY, b.UsZ);
                    var sv = new UEVector(b.VsX, b.VsY, b.VsZ);
                    FogMap.Build(recLights, s.UClamp, s.VClamp, Dot(p0, ax), Dot(p0, ay), Dot(p0, az),
                        Dot(su, ax), Dot(su, ay), Dot(su, az), Dot(sv, ax), Dot(sv, ay), Dot(sv, az), buf, ref outsideState);
                    tiles[rec.Key] = buf;
                }
            }
        }
    }
}
