// Movers: geometry and per-frame lightmap tiles from Core (UELevelRuntime.MoverLightmaps: brush FPolys at the base
// position, level branch with the +0x48 records, zone layer, mode 3); here only their own atlas and the draw batches.
// Texture and lightmap UVs are computed once (base position; invariant to motion), vertex positions every tick:
// brush point through the mover's ToWorld from the frame (MoverState.ToWorld). Blend mode follows the BSP rule (WorldMesh).
// Which polygons a view draws is Core's (WorldFrame.Drawn Movers: a brush polygon of which OccludeBsp drew a tracker
// fragment in that frame): Draws holds one batch per (material batch, view) with only those polygons; Batches is the
// whole geometry of the tick.
// Not ported: brushes without UPolys, MainScale |s|≠1, counted in Problems.
using HP1.UEScript;

namespace HPEngine
{
    public sealed class MoverScene
    {
        readonly UELevelRuntime _rt;
        public readonly UEMoverGeometry Geo;
        public readonly LightmapAtlas Atlas;
        public readonly WorldMesh Mesh;
        readonly List<ActorDraw> _draws = new();   // per material batch: the tick's geometry (every polygon)
        public IReadOnlyList<ActorDraw> Batches => _draws;
        readonly List<ActorDraw> _out = new(), _pool = new();
        public IReadOnlyList<ActorDraw> Draws => _out;   // per (batch, view): Core's drawn polygons only
        // Mover mirrors. The brush tracker puts the mover surface into the level model with PolyFlags FPoly & 0x3cffffff
        // (SetupActorBrush FUN_1038d500, UEMovingBrushTracker.SetupActorBrush): PF_Mirrored stays, and OccludeBsp
        // spawns the mirror child frame the same way as for a level node; the frame names the surface by mover and FPoly
        // number (WorldFrame.Views, ViewSource). Here only: which batch (plane Mesh.MirrorPlanes) takes it.
        readonly Dictionary<(int Mover, int Poly), int> _mirrorOf = new();
        // Reflagged polygons (DrawnWorld.Reflagged, Node -1): the batches WorldMesh.Build made with those flags
        // (runs in slots >= 2·NodeCount) take them in that view, the regular batches drop them.
        readonly Dictionary<long, int> _reflagOf = new();   // (mover, FPoly) -> flags of its reflagged runs
        readonly bool[] _isReflag;
        public int UnknownReflag;   // reflagged polygons without a run for Core's flags (drawn with their own flags), cumulative
        public int MirrorPlaneOf(int moverId, int poly) => _mirrorOf.TryGetValue((moverId, poly), out int mi) ? mi : -1;

        public MoverScene(UELevelRuntime rt, WorldFrame frame, WorldMesh.TextureInfo tex, WorldMesh.ZonePan zonePan, string defaultTexture)
        {
            _rt = rt;
            var nameById = new Dictionary<int, string>();
            foreach (ActorSnapshot a in frame.Actors) nameById[a.ObjectId] = a.Name;
            Geo = (rt.MoverLightmaps ?? throw new InvalidOperationException("MoverScene: Core has no mover lightmaps for this world")).Geo;
            Atlas = new LightmapAtlas(Geo.Geometry);
            Mesh = WorldMesh.Build(Geo.Geometry, tex, Atlas.Place, LightmapAtlas.Width, Atlas.Height, zonePan: zonePan, defaultTexture: defaultTexture, carrier: "mover");
            // Batches -> dynamic batches (positions rewritten every frame).
            foreach (Batch b in Mesh.Batches)
                _draws.Add(new ActorDraw { Texture = b.Material, Blend = ActorBlend.Opaque, Unlit = b.Unlit, WorldMode = (int)b.Blend, Lightmapped = !b.Unlit, MoverAtlas = true,
                    MirrorPlane = b.MirrorPlane, PolyFlags = b.PolyFlags, Group = b.Group, AlphaTest = b.AlphaTest,
                    LogKind = "MOVER", LogOwner = string.Join(",", b.Indices.Select(vi => Geo.MoverOfNode[Mesh.NodeOfVertex[(int)vi]]).Distinct().Select(mi => nameById.GetValueOrDefault(Geo.Movers[mi].Actor.ObjectId))) });
            for (int n = 0; n < Geo.Geometry.Nodes.Length; n++)
            {
                int surf = Geo.Geometry.Nodes[n].Surf;
                if (Mesh.MirrorPlaneOfSurf.TryGetValue(surf, out int mi))
                    _mirrorOf[(Geo.Movers[Geo.MoverOfNode[n]].Actor.ObjectId, Geo.PolyOfSurf[surf])] = mi;
                if (Mesh.ReflagFlags[n] != 0) _reflagOf[KeyOfNode(n)] = Mesh.ReflagFlags[n];
            }
            _isReflag = Mesh.Batches.Select(b => b.Runs.Count > 0 && b.Runs[0].Node >= 2 * Mesh.NodeCount).ToArray();
        }

        // Per batch: its global vertices and local indices (computed once).
        List<(int[] verts, uint[] idx)> _parts;

        // Coplanar cut (Core UELevelRuntime.MoverCoplanarPieces, once per tick): a brush polygon whose tracker fragments
        // the span buffer hides behind earlier coplanar level nodes is drawn as its visible pieces only. Per batch the
        // layout in use: null — the batch as built (no cut polygon); else template vertices (attributes interpolated
        // on the polygon plane), brush-local points, mover per vertex and indices.
        sealed class CutLayout { public Vertex[] Tmpl; public UEVector[] Local; public int[] Mover; public uint[] Idx; public long[] TriKey; }
        long[][] _triKey;   // per batch, uncut layout: the (mover, FPoly) key of every triangle
        long KeyOfNode(int n) => Key(Geo.Movers[Geo.MoverOfNode[n]].Actor.ObjectId, Geo.PolyOfSurf[Geo.Geometry.Nodes[n].Surf]);
        static long Key(int moverId, int poly) => ((long)moverId << 32) | (uint)poly;
        CutLayout[] _cut;
        int _cutTick = int.MinValue;
        Dictionary<(int MoverId, int Poly), List<UEVector[]>> _cutFrom;
        List<(int Node, uint[] Tri)>[] _nodeTris;   // per batch: the triangles of each model node, in index order

        // Clock panning of mover surfaces (SurfacePan with the zone speed), per batch.
        // tick/alpha: interpolation between logic ticks (FramePacing.cs): vertices computed once per tick from the frame's
        // MoverState.ToWorld. The cut (teleport, jerk) is decided per MOVER by its own anchor (ToWorld origin), not per
        // batch: a batch holds the polygons of every mover with that material.
        readonly PoseHistory _hist = new(), _moverAt = new();
        readonly Dictionary<int, BrushCoords> _toWorld = new();
        int _toWorldTick = int.MinValue;
        public int MissingMovers;   // movers of the geometry absent from the frame (their polygons keep the last tick)
        // Core's coplanar cut of the tick (UELevelRuntime.MoverCoplanarOfTick), read while Core is idle (HPEngineProgram.TakeTick);
        // Build uses it on the tick's first frame.
        Dictionary<(int MoverId, int Poly), List<UEVector[]>> _tickCut;
        int _tickCutOf = int.MinValue;
        public void TakeTick(int tick)
        {
            _tickCut = _rt.MoverCoplanarOfTick ?? throw new InvalidOperationException("movers: no coplanar cut of the tick (UELevelRuntime.MoverCoplanarOfTick)");
            _tickCutOf = tick;
        }

        public void Build(WorldFrame frame, float levelTime, int tick = 0, float alpha = 1f)
        {
            if (_toWorldTick != tick)
            {
                _toWorldTick = tick;
                _toWorld.Clear();
                foreach (ActorSnapshot a in frame.Actors) if (!a.Destroyed && a.Mover.NumKeys > 0) _toWorld[a.ObjectId] = a.Mover.ToWorld;
                MissingMovers = 0;
                for (int i = 0; i < Geo.Movers.Count; i++)
                {
                    if (!_toWorld.TryGetValue(Geo.Movers[i].Actor.ObjectId, out BrushCoords c)) { MissingMovers++; continue; }
                    _moverAt.Store(i, tick, System.Array.Empty<UEVector>(), c.Origin);
                }
            }
            if (_parts is null)
            {
                _parts = new();
                _nodeTris = new List<(int, uint[])>[Mesh.Batches.Count];
                _triKey = new long[Mesh.Batches.Count][];
                foreach (Batch src in Mesh.Batches)
                {
                    var tk = new long[src.Indices.Count / 3];
                    for (int k = 0; k < tk.Length; k++) tk[k] = KeyOfNode(Mesh.NodeOfVertex[(int)src.Indices[k * 3]]);
                    _triKey[_parts.Count] = tk;
                    var map = new Dictionary<uint, uint>();
                    var vs = new List<int>();
                    var ix = new uint[src.Indices.Count];
                    for (int k = 0; k < ix.Length; k++)
                    {
                        uint g = src.Indices[k];
                        if (!map.TryGetValue(g, out uint l)) { l = (uint)vs.Count; map[g] = l; vs.Add((int)g); }
                        ix[k] = l;
                    }
                    var groups = new List<(int, uint[])>();
                    for (int k = 0; k + 2 < src.Indices.Count; k += 3)
                    {
                        int n = Mesh.NodeOfVertex[(int)src.Indices[k]];
                        uint[] tri = { src.Indices[k], src.Indices[k + 1], src.Indices[k + 2] };
                        if (groups.Count > 0 && groups[^1].Item1 == n) groups[^1] = (n, groups[^1].Item2.Concat(tri).ToArray());
                        else groups.Add((n, tri));
                    }
                    _nodeTris[_parts.Count] = groups;
                    _parts.Add((vs.ToArray(), ix));
                }
                _cut = new CutLayout[_parts.Count];
            }
            var pieces = _cutTick == tick ? _cutFrom : _tickCutOf == tick ? _tickCut
                : _rt.MoverCoplanarOfTick ?? throw new InvalidOperationException("movers: no coplanar cut of the tick (UELevelRuntime.MoverCoplanarOfTick)");   // a caller without TakeTick (tests), Core idle
            _cutTick = tick;
            if (!ReferenceEquals(pieces, _cutFrom))   // Core returns the same object while nothing moved
            {
                _cutFrom = pieces;
                for (int b = 0; b < _parts.Count; b++)
                {
                    CutLayout l = pieces.Count == 0 ? null : Layout(b, pieces);
                    if (!SameLayout(l, _cut[b])) _hist.Forget(b);   // other vertices: no interpolation across the change
                    _cut[b] = l;
                }
            }
            BrushCoords[] coords = null;
            var lerps = new bool[Geo.Movers.Count];
            for (int i = 0; i < lerps.Length; i++) lerps[i] = _moverAt.Has(i, tick) && _moverAt.Lerps(i, alpha);
            for (int b = 0; b < _parts.Count; b++)
            {
                ActorDraw d = _draws[b];
                CutLayout cut = _cut[b];
                d.Vertices.Clear(); d.Indices.Clear();
                d.Indices.AddRange(cut?.Idx ?? _parts[b].idx);
                Batch src = Mesh.Batches[b];
                d.UvOffU = d.UvOffV = 0;
                if (src.PanFlags != 0)
                {
                    SurfacePan.Pan(src.PanFlags, 0, 0, levelTime, src.PanSpeedU, src.PanSpeedV, out float pu, out float pv);
                    d.UvOffU = pu / src.TexW; d.UvOffV = pv / src.TexH;
                }
                int[] verts = _parts[b].verts;
                int count = cut?.Tmpl.Length ?? verts.Length;
                if (!_hist.Has(b, tick))
                {
                    if (coords is null)
                    {
                        coords = new BrushCoords[Geo.Movers.Count];
                        for (int i = 0; i < coords.Length; i++) coords[i] = _toWorld.TryGetValue(Geo.Movers[i].Actor.ObjectId, out BrushCoords c) ? c : default;
                    }
                    var at = new UEVector[count];
                    for (int k = 0; k < count; k++)
                        at[k] = cut is not null ? coords[cut.Mover[k]].TransformPoint(cut.Local[k])
                            : coords[Geo.MoverOfNode[Mesh.NodeOfVertex[verts[k]]]].TransformPoint(Geo.LocalOfPoint[Mesh.PointOfVertex[verts[k]]]);
                    _hist.Store(b, tick, at, default);   // no batch anchor: the cut is per mover (lerps)
                }
                UEVector[] pts = _hist.Get(b, alpha, k => lerps[cut is not null ? cut.Mover[k] : Geo.MoverOfNode[Mesh.NodeOfVertex[verts[k]]]]);
                for (int k = 0; k < count; k++)
                {
                    Vertex x = cut is not null ? cut.Tmpl[k] : Mesh.Vertices[verts[k]];
                    x.X = pts[k].x; x.Y = pts[k].y; x.Z = pts[k].z;
                    d.Vertices.Add(x);
                }
            }
            EmitViews(frame);
        }

        // Per view (WorldFrame.Drawn index = Core frame + 1) the triangles of the brush polygons Core drew there.
        readonly List<HashSet<long>> _viewSets = new(), _viewReflag = new();
        void EmitViews(WorldFrame frame)
        {
            _out.Clear();
            int nv = frame.Drawn?.Count ?? 0, used = 0;
            while (_viewSets.Count < nv) { _viewSets.Add(new HashSet<long>()); _viewReflag.Add(new HashSet<long>()); }
            for (int v = 0; v < nv; v++)
            {
                HashSet<long> set = _viewSets[v], re = _viewReflag[v];
                set.Clear(); re.Clear();
                foreach (ViewSource m in frame.Drawn[v].Movers) set.Add(Key(m.MoverId, m.MoverPoly));
                foreach (var (node, m, pf) in frame.Drawn[v].Reflagged)
                {
                    if (node >= 0) continue;
                    long k = Key(m.MoverId, m.MoverPoly);
                    if (_reflagOf.TryGetValue(k, out int have) && have == pf) re.Add(k);
                    else if (UnknownReflag++ == 0) Console.Error.WriteLine("[Movers] STOP: Core reflagged a mover polygon with flags the mesh has no run for — drawn with its surface flags");
                }
            }
            for (int b = 0; b < _draws.Count; b++)
            {
                ActorDraw d = _draws[b];
                long[] keys = _cut[b]?.TriKey ?? _triKey[b];
                for (int v = 0; v < nv; v++)
                {
                    HashSet<long> set = _viewSets[v], re = _viewReflag[v];
                    if (set.Count == 0) continue;
                    ActorDraw o = null;
                    for (int t = 0; t < keys.Length; t++)
                    {
                        if (!set.Contains(keys[t]) || re.Contains(keys[t]) != _isReflag[b]) continue;
                        if (o is null)
                        {
                            if (_pool.Count <= used) _pool.Add(new ActorDraw());
                            o = _pool[used++];
                            o.Texture = d.Texture; o.Blend = d.Blend; o.Unlit = d.Unlit; o.WorldMode = d.WorldMode; o.Lightmapped = d.Lightmapped;
                            o.MoverAtlas = d.MoverAtlas; o.MirrorPlane = d.MirrorPlane; o.PolyFlags = d.PolyFlags; o.Group = d.Group;
                            o.AlphaTest = d.AlphaTest; o.LogKind = d.LogKind; o.LogOwner = d.LogOwner; o.UvOffU = d.UvOffU; o.UvOffV = d.UvOffV;
                            o.FrameView = v - 1;
                            o.Vertices.Clear(); o.Vertices.AddRange(d.Vertices); o.Indices.Clear();
                            _out.Add(o);
                        }
                        o.Indices.Add(d.Indices[t * 3]); o.Indices.Add(d.Indices[t * 3 + 1]); o.Indices.Add(d.Indices[t * 3 + 2]);
                    }
                }
            }
        }

        UEVector LocalOf(uint v) => Geo.LocalOfPoint[Mesh.PointOfVertex[(int)v]];

        // Batch b with its cut polygons replaced by their pieces; null when none of its polygons is cut.
        CutLayout Layout(int b, Dictionary<(int MoverId, int Poly), List<UEVector[]>> pieces)
        {
            var tm = new List<Vertex>(); var lo = new List<UEVector>(); var mv = new List<int>(); var ix = new List<uint>(); var tk = new List<long>();
            bool any = false;
            foreach (var (n, tri) in _nodeTris[b])
            {
                int mi = Geo.MoverOfNode[n];
                long key = KeyOfNode(n);
                if (!pieces.TryGetValue((Geo.Movers[mi].Actor.ObjectId, Geo.PolyOfSurf[Geo.Geometry.Nodes[n].Surf]), out List<UEVector[]> vis))
                {
                    var map = new Dictionary<uint, uint>();
                    foreach (uint g in tri)
                    {
                        if (!map.TryGetValue(g, out uint l)) { l = (uint)tm.Count; map[g] = l; tm.Add(Mesh.Vertices[(int)g]); lo.Add(LocalOf(g)); mv.Add(mi); }
                        ix.Add(l);
                    }
                    for (int k = 0; k < tri.Length / 3; k++) tk.Add(key);
                    continue;
                }
                any = true;
                // Reference triangle: the polygon's largest triangle. Every vertex
                // attribute is affine on the plane (texture/lightmap/fog UV along the surface axes, constant light), so
                // barycentric weights over it give a piece vertex what WorldMesh.Build gives a polygon vertex.
                int best = 0; double bestArea = -1;
                for (int k = 0; k + 2 < tri.Length; k += 3)
                {
                    double a2 = Cross(LocalOf(tri[k]), LocalOf(tri[k + 1]), LocalOf(tri[k + 2])).Len;
                    if (a2 > bestArea) { bestArea = a2; best = k; }
                }
                uint ia = tri[best], ib = tri[best + 1], ic = tri[best + 2];
                UEVector pa = LocalOf(ia), pb = LocalOf(ib), pc = LocalOf(ic);
                foreach (UEVector[] piece in vis)
                {
                    uint first = (uint)tm.Count;
                    foreach (UEVector p in piece)
                    {
                        Bary(pa, pb, pc, p, out double u, out double v, out double w);
                        tm.Add(Mix(Mesh.Vertices[(int)ia], Mesh.Vertices[(int)ib], Mesh.Vertices[(int)ic], u, v, w));
                        lo.Add(p); mv.Add(mi);
                    }
                    for (int k = 1; k + 1 < piece.Length; k++) { ix.Add(first); ix.Add(first + (uint)k); ix.Add(first + (uint)k + 1); tk.Add(key); }
                }
            }
            return any ? new CutLayout { Tmpl = tm.ToArray(), Local = lo.ToArray(), Mover = mv.ToArray(), Idx = ix.ToArray(), TriKey = tk.ToArray() } : null;
        }

        static bool SameLayout(CutLayout a, CutLayout b)
            => a is null ? b is null : b is not null && a.Idx.AsSpan().SequenceEqual(b.Idx) && a.Local.AsSpan().SequenceEqual(b.Local);

        static (double X, double Y, double Z, double Len) Cross(UEVector a, UEVector b, UEVector c)
        {
            double ux = b.x - a.x, uy = b.y - a.y, uz = b.z - a.z, vx = c.x - a.x, vy = c.y - a.y, vz = c.z - a.z;
            double x = uy * vz - uz * vy, y = uz * vx - ux * vz, z = ux * vy - uy * vx;
            return (x, y, z, Math.Sqrt(x * x + y * y + z * z));
        }

        static void Bary(UEVector a, UEVector b, UEVector c, UEVector p, out double u, out double v, out double w)
        {
            double e0x = b.x - a.x, e0y = b.y - a.y, e0z = b.z - a.z, e1x = c.x - a.x, e1y = c.y - a.y, e1z = c.z - a.z;
            double dx = p.x - a.x, dy = p.y - a.y, dz = p.z - a.z;
            double d00 = e0x * e0x + e0y * e0y + e0z * e0z, d01 = e0x * e1x + e0y * e1y + e0z * e1z, d11 = e1x * e1x + e1y * e1y + e1z * e1z;
            double d20 = dx * e0x + dy * e0y + dz * e0z, d21 = dx * e1x + dy * e1y + dz * e1z, den = d00 * d11 - d01 * d01;
            v = (d11 * d20 - d01 * d21) / den; w = (d00 * d21 - d01 * d20) / den; u = 1 - v - w;
        }

        static Vertex Mix(Vertex a, Vertex b, Vertex c, double u, double v, double w)
        {
            float M(float x, float y, float z) => (float)(u * x + v * y + w * z);
            return new Vertex { U = M(a.U, b.U, c.U), V = M(a.V, b.V, c.V), U2 = M(a.U2, b.U2, c.U2), V2 = M(a.V2, b.V2, c.V2),
                R = M(a.R, b.R, c.R), G = M(a.G, b.G, c.G), B = M(a.B, b.B, c.B), U3 = M(a.U3, b.U3, c.U3), V3 = M(a.V3, b.V3, c.V3),
                FogR = M(a.FogR, b.FogR, c.FogR), FogG = M(a.FogG, b.FogG, c.FogG), FogB = M(a.FogB, b.FogB, c.FogB), A = M(a.A, b.A, c.A) };
        }
    }
}
