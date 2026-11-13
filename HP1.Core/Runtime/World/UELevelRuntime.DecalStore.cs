// ADecal::execAttachDecal / execDetachDecal (Engine.dll 0x10410770 / 0x10412ef0) and the
// Surf->Decals records — CORE'S OWN CODE.
//
// Ported from the DISASSEMBLY (the body's decompile is distorted: Ghidra confuses locals):
//   0x10410770-0x10410dd3  entry, gates 1-9 (main surface selection — same order
//                          as UELevelRuntime.TraceDecalSurface);
//   0x10410dd9-0x104113cd  FDecal record on the main surface: inserted BEFORE the
//                          record with the same texture (FArray::InsertZeroed, else
//                          AddZeroed), SurfList.Add, radius sqrt(DS²·USize²·0.5),
//                          basis (rotated 45° for a given direction, straight for a
//                          random/degenerate one), vertices in world space;
//   0x104127c0             FDecal.Nodes: surface nodes against which the record is not
//                          clipped to nothing (2D clip by "edge × normal" planes);
//   0x104113d2-0x10411e6a  MultiDecalLevel wrap: an MDL×MDL grid of record points,
//                          a ±50 trace along the normal, a neighbouring surface with
//                          |cos| > 0.7 gets its own record with vertices projected onto
//                          its plane; nodes; vertices relative to its pBase;
//   0x10411e75-0x10411edb  main record vertices relative to its pBase; return —
//                          the main surface's texture.
//   0x10412ef0             detach: over SurfList from the end, the actor's first record.
//
// PRECISION. The live call is ActorShadow.Update from the renderer, in the appEnableFastMath(1)
// window (PC24): all body arithmetic here is C# float, which under PC24 matches x87
// operation for operation (including gates 7-8 with x87 comparison semantics for NaN).
// LineCheck is Core's model body, which takes the precision of the caller's window (PC24 here: every
// operation to float; HeadlessBspModel.Pc24); FindCoplanarSurface is the PC24 body (checked under unicorn at FPCW = PC24):
// in the render window shipped rounds every operation to 24 bits.
// Checked against the translation in the PC24 window:
// bit-exact, Tut1/Tut3/Tut1b 60 cases each, 0 divergences.
// Outside the PC24 window (game tick, PC64) the campaign never calls the native (Scorch/ectoMark
// are not spawned); a call there is a loud failure, not a silent PC24.
//
// Mover brush surfaces are inserted into the level model by the tracker (FMovingBrushTracker,
// UEMovingBrushTracker.cs): the LineCheck(6) trace finds a mover fragment as a level node,
// FindCoplanarSurface walks its iPlane chain and picks the tracker surface — the record
// lands on it with the same body as on a map surface. FBspSurf.Nodes of a tracker surface is
// empty (its nodes are fragments, which SetupActorBrush/AddPolyFragment do not add to
// Surf.Nodes), so FDecal.Nodes is empty and DrawDecals clips the record against every drawn
// fragment of the surface.
using System;
using System.Collections.Generic;
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        // FDecal (0x40 in shipped): Vertices[4] (+0x00, after attaching — relative to
        // the surface pBase), Actor (+0x30), Nodes (+0x34).
        public sealed class SurfDecal
        {
            public UEObject Actor;
            public readonly Vector3[] Vertices = new Vector3[4];
            public readonly List<int> Nodes = new();
        }

        Dictionary<int, List<SurfDecal>> _surfDecals;
        List<int>[] _surfNodes;
        HeadlessBspModel _surfDecalsBsp;

        // Surf->Decals of the current model; a map change gives an empty store.
        List<SurfDecal> SurfDecals(int surf)
        {
            EnsureDecalStore();
            if (!_surfDecals.TryGetValue(surf, out var list)) _surfDecals[surf] = list = new List<SurfDecal>();
            return list;
        }

        void EnsureDecalStore()
        {
            if (ReferenceEquals(_surfDecalsBsp, Bsp) && _surfDecals is not null) return;
            _surfDecalsBsp = Bsp;
            _surfDecals = new Dictionary<int, List<SurfDecal>>();
            _surfNodes = null;
        }

        // FBspSurf.Nodes — UModel::PostLoad (Engine.dll.c:45911-45933): walk nodes in
        // order, Surfs[node.iSurf].Nodes.AddItem(iNode).
        public IReadOnlyList<int> SurfNodeList(int surf)
        {
            EnsureDecalStore();
            if (_surfNodes is null)
            {
                _surfNodes = new List<int>[Bsp.ParseTables.Surfs.Length];   // map surfaces only (PostLoad)
                for (int i = 0; i < Bsp.NodeCount; i++)
                {
                    int s = Bsp.NodeSurf(i);
                    if (s >= 0 && s < _surfNodes.Length) (_surfNodes[s] ??= new List<int>()).Add(i);
                }
            }
            // Tracker surface (after the map surfaces): Nodes is empty — UModel::PostLoad never
            // saw them, and the tracker keeps no list (FUN_1038d500/FUN_1038da40).
            if (surf >= _surfNodes.Length) return Array.Empty<int>();
            return _surfNodes[surf] ?? (IReadOnlyList<int>)Array.Empty<int>();
        }

        // All model records in surface and record order — the frame snapshot.
        public IEnumerable<(int Surf, int Index, SurfDecal Record)> AllSurfDecals()
        {
            if (_surfDecals is null || !ReferenceEquals(_surfDecalsBsp, Bsp)) yield break;
            var keys = new List<int>(_surfDecals.Keys);
            keys.Sort();
            foreach (int s in keys)
            {
                var list = _surfDecals[s];
                for (int i = 0; i < list.Count; i++) yield return (s, i, list[i]);
            }
        }

        static string TextureKey(object t)
            => t is UEResourceRef r ? (r.IdentityKey ?? r.ObjectPath ?? r.Name)
             : t is UELib.Core.UObject u ? u.GetPath()
             : null;

        // Record on a surface: before the first record with the same actor texture
        // (FArray::InsertZeroed), else at the end (AddZeroed); Actor; SurfList.Add(surf).
        SurfDecal NewSurfDecal(UEObject self, int surf)
        {
            var list = SurfDecals(surf);
            string tex = TextureKey(self.GetProp(PK.Texture));
            var rec = new SurfDecal { Actor = self };
            int at = list.Count;
            for (int i = 0; i < list.Count; i++)
                if (TextureKey(list[i].Actor.GetProp(PK.Texture)) == tex) { at = i; break; }
            list.Insert(at, rec);
            var surfList = self.GetProp(PK.SurfList) as System.Collections.IList;
            if (surfList is null) { surfList = new List<object>(); self.Props["SurfList"] = surfList; }
            surfList.Add(surf);
            return rec;
        }

        static bool InSurfList(UEObject self, int surf)
        {
            if (self.GetProp(PK.SurfList) is not System.Collections.IList l) return false;
            foreach (object o in l) if (UEValue.ToInt(o) == surf) return true;
            return false;
        }

        static bool ClientDecalsEnabled(UEConfig config)
        {
            string client = config?.ResolveSection(UEConfig.ScopeSystem, "Engine.Engine.ViewportManager");
            if (client is null) throw new InvalidOperationException("AttachDecal: the config does not name ViewportManager — no Client.Decals gate");
            string raw = config.Get(UEConfig.ScopeSystem, client, "Decals");
            return raw is not null && string.Equals(raw.Trim(), "True", StringComparison.OrdinalIgnoreCase);
        }

        // x87 after fcomp: "less" — C0, "less or equal" — C0|C3; NaN sets both.
        static bool X87Lt(float a, float b) => !(a >= b);
        static bool X87Le(float a, float b) => !(a > b);

        // PC24: every x87 operation is float.
        static float Dot(Vector3 a, Vector3 b) => (a.z * b.z + a.y * b.y) + a.x * b.x;   // FVector::operator| (Core 0x10119190)
        static Vector3 Sub(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        static Vector3 Add(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        static Vector3 Mul(Vector3 a, float s) => new Vector3(a.x * s, a.y * s, a.z * s);
        static float SizeSq(Vector3 v) => (v.x * v.x + v.y * v.y) + v.z * v.z;
        // Normalisation with a 1e-8 threshold (Core FVector::Normalize 0x101198d0 and the same form in the body):
        // below the threshold the vector is unchanged.
        static Vector3 NormalizeLoose(Vector3 v)
        {
            float sq = SizeSq(v);
            if (X87Lt(sq, 9.99999993922529e-09f)) return v;
            float inv = (float)(1.0 / (float)Math.Sqrt(sq));
            return new Vector3(inv * v.x, inv * v.y, inv * v.z);
        }
        // Intersection of segment from->to with plane (base, n): from + (to−from)·((base−from)|n / (to−from)|n).
        static Vector3 PlanePoint(Vector3 from, Vector3 to, Vector3 basePt, Vector3 n)
        {
            float num = Dot(Sub(basePt, from), n), den = Dot(Sub(to, from), n);
            return Add(from, Mul(Sub(to, from), num / den));
        }

        public int DecalCoreAttachCalls, DecalCoreAttachOk, DecalCoreWrapRecords;

        // execAttachDecal. distance, decalDir — native arguments; randInt — the render
        // rand() stream (appFrand of the random-direction branch, PC24 window).
        public UEResourceRef AttachDecalCore(UEObject self, float distance, Vector3 decalDir, Func<int> randInt)
        {
            DecalCoreAttachCalls++;
            // Gate 1: Level->Engine->Client && Client.Decals (0x1041081d-0x1041085a).
            if (!ClientDecalsEnabled(Config)) return null;
            object texture = self.GetProp(PK.Texture);
            if (texture is null || TextureKey(texture) is null)
            {
                HP1.Core.UETrace.Warn("AttachDecal: No Texture");   // GLog, string 0x104db7fc
                return null;
            }
            // MultiDecalLevel is clamped to 4 and WRITTEN to the actor (0x1041086d-0x10410880).
            object mdlRaw = self.GetProp("MultiDecalLevel");
            int mdl = UEValue.ToInt(mdlRaw);
            if (mdl > 4) mdl = 4;
            self.Props["MultiDecalLevel"] = mdlRaw is byte ? (object)(byte)mdl : mdl;
            if (Bsp is null) return null;

            Vector3 location = UEValue.AsVector3(self.GetProp(PK.Location));
            Vector3 xAxis = UEValue.GetAxes(UEValue.AsRotator(self.GetProp(PK.Rotation))).X;
            bool random = false;
            if (decalDir.x == 0f && decalDir.y == 0f && decalDir.z == 0f)
            {
                // 0x104109ab-0x10410a2e: 2·appFrand−1 until inside the ball, UnsafeNormal; PC24.
                if (randInt is null) throw new InvalidOperationException("AttachDecal: zero DecalDir, and the render appFrand stream is not bound");
                decalDir = RandomDecalDir(randInt);
                random = true;
            }

            // Gates 2-9 (0x10410a31-0x10410dd3) in PC24: the LineCheck(6) trace and
            // FindCoplanarSurface are Core model bodies; planes and gates are float with
            // x87 comparison semantics (NaN in gate 7 gives C3 — "equal to zero", reject).
            Vector3 rayEnd = new Vector3(location.x + -xAxis.x * distance, location.y + -xAxis.y * distance, location.z + -xAxis.z * distance);
            BspHit tr = Bsp.TraceWorldLine(location, rayEnd, null, DecalExtraNodeFlags);
            if (!tr.Blocked || tr.Node < 0) return null;
            SurfDecal main = MainRecord(Bsp, SurfNodeList, tr, location, rayEnd, xAxis, decalDir, random, texture, self,
                s => NewSurfDecal(self, s), out int surf2, out Vector3 n2, out Vector3 base2);
            if (main is null) return null;

            // Wrap (0x104113d2-0x10411e6a).
            Vector3 nh = Mul(n2, 1.0f / (float)Math.Sqrt(SizeSq(n2)));
            Vector3 k = Mul(nh, -50f);
            Vector3 v0 = main.Vertices[0];
            Vector3 e1 = Sub(main.Vertices[1], v0), e3 = Sub(main.Vertices[3], v0);
            for (int i = 0; i < mdl; i++)
                for (int j = 0; j < mdl; j++)
                {
                    float fj = (float)((float)(j + 1.0) / mdl), fi = (float)((float)(i + 1.0) / mdl);
                    Vector3 s1 = Add(Mul(e1, fi), v0);
                    Vector3 q = Add(Mul(e3, fj), s1);
                    Vector3 start = Sub(q, k), end = Add(q, k);
                    BspHit h = Bsp.TraceWorldLine(start, end, null, DecalExtraNodeFlags);
                    if (!h.Blocked || h.Node < 0) continue;
                    int hs = Bsp.NodeSurf(h.Node);
                    BspSurfRecord hr = Bsp.Surf(hs);
                    Vector3 p = PlanePoint(start, end, Bsp.Point(hr.PBase), Bsp.Vector(hr.VNormal));
                    int sS = Bsp.FindCoplanarSurface(h.Node, p);
                    if (sS < 0 || InSurfList(self, sS)) continue;
                    BspSurfRecord rs = Bsp.Surf(sS);
                    if ((rs.PolyFlags & PfDecalReject) != 0) continue;
                    Vector3 nS = Bsp.Vector(rs.VNormal), bS = Bsp.Point(rs.PBase);
                    float len2 = (float)Math.Sqrt(SizeSq(n2)), lenS = (float)Math.Sqrt(SizeSq(nS));
                    // 0x104119b5; RU EA (UEEdition.RuOrder; RU Engine 0x1040ed34): (z·z + y·y) + x·x.
                    float c = (UEEdition.RuOrder ? (n2.z * nS.z + n2.y * nS.y) + n2.x * nS.x
                        : (n2.x * nS.x + nS.z * n2.z) + nS.y * n2.y) / (len2 * lenS);
                    if (!(Math.Abs((double)c) > 0.7)) continue;
                    SurfDecal wr = NewSurfDecal(self, sS);
                    DecalCoreWrapRecords++;
                    for (int m = 0; m < 4; m++)
                    {
                        Vector3 vk = main.Vertices[m];
                        Vector3 a1 = Sub(vk, n2);
                        Vector3 p1 = Add(a1, Mul(Sub(vk, a1), Dot(Sub(bS, a1), nS) / Dot(Sub(vk, a1), nS)));
                        Vector3 b1 = Sub(vk, nS);
                        Vector3 p2 = Add(b1, Mul(Sub(vk, b1), Dot(Sub(bS, b1), nS) / Dot(Sub(vk, b1), nS)));
                        Vector3 w2 = Sub(vk, p2), w1 = Sub(vk, p1);
                        float la = (float)Math.Sqrt((w2.z * w2.z + w2.y * w2.y) + w2.x * w2.x) / c;
                        float lb = (float)Math.Sqrt((w1.z * w1.z + w1.y * w1.y) + w1.x * w1.x) / c;
                        Vector3 dir = NormalizeLoose(Sub(p2, p1));
                        wr.Vertices[m] = Sub(p2, Mul(dir, lb - la));
                    }
                    BuildDecalNodes(sS, wr);
                    for (int m = 0; m < 4; m++) wr.Vertices[m] = Sub(wr.Vertices[m], bS);
                }
            for (int m = 0; m < 4; m++) main.Vertices[m] = Sub(main.Vertices[m], base2);
            DecalCoreAttachOk++;
            SurfMaterialRef mat = Bsp.MaterialOfSurf(surf2);
            return mat is null ? null : new UEResourceRef(mat.ClassName ?? "Texture", mat.Name, mat.Path, mat.IdentityKey);
        }

        // Main record (gates 2-9 and 0x10410dd9-0x104113cd) over model m — the level or a
        // mover brush (then everything in its local coordinates). null — a gate rejected.
        SurfDecal MainRecord(HeadlessBspModel m, Func<int, IReadOnlyList<int>> surfNodes, BspHit tr,
            Vector3 location, Vector3 rayEnd, Vector3 xAxis, Vector3 decalDir, bool random, object texture, UEObject self,
            Func<int, SurfDecal> newRecord, out int surf2, out Vector3 n2, out Vector3 base2)
        {
            surf2 = -1; n2 = default; base2 = default;
            BspSurfRecord rec1 = m.Surf(m.NodeSurf(tr.Node));
            Vector3 point1 = PlanePoint(location, rayEnd, m.Point(rec1.PBase), m.Vector(rec1.VNormal));
            surf2 = m.FindCoplanarSurface(tr.Node, point1);
            if (surf2 < 0) return null;
            BspSurfRecord rec2 = m.Surf(surf2);
            n2 = m.Vector(rec2.VNormal);
            base2 = m.Point(rec2.PBase);
            Vector3 point2 = PlanePoint(location, rayEnd, base2, n2);
            if (!PlaneGates(xAxis, n2, base2, point2)) return null;
            if ((rec2.PolyFlags & PfDecalReject) != 0) return null;                  // 0x10410dcd-0x10410dd3

            SurfDecal main = newRecord(surf2);
            MainVertices(main, point2, n2, base2, decalDir, random, texture, self);
            BuildDecalNodes(m, surfNodes(surf2), n2, main);
            return main;
        }

        // Gates 7-8 (0x10410d29-0x10410dc7) — plane of the chosen surface against the ray.
        static bool PlaneGates(Vector3 xAxis, Vector3 n2, Vector3 base2, Vector3 point2)
        {
            float g7 = (xAxis.x * n2.x + xAxis.z * n2.z) + xAxis.y * n2.y;          // 0x10410d29-0x10410d52
            // C3 after fcomp: zero or NaN — reject. Shape from the listing; the check corpus never
            // meets NaN here, so this branch specifically is not proven by comparison.
            if (!(g7 < 0f || g7 > 0f)) return false;
            var off = new Vector3(base2.x - point2.x, base2.y - point2.y, base2.z - point2.z);
            float g8 = (off.x * n2.x + off.z * n2.z) + off.y * n2.y;                // 0x10410d5e-0x10410dc7
            if (X87Lt(g8, 0f)) g8 = -g8;
            if (g8 > 0.0010000000474974513f) return false;                           // strictly greater; NaN passes
            return true;
        }

        // d = (D.x·N.x + D.z·N.z) + D.y·N.y (0x10410e9c, 0x1041110d); RU EA (UEEdition.RuOrder; RU Engine 0x1040e2f0,
        // 0x1040e564): (D.y·N.y + D.z·N.z) + D.x·N.x.
        static float DecalDot(Vector3 D, Vector3 n)
            => UEEdition.RuOrder ? (D.y * n.y + D.z * n.z) + D.x * n.x : (D.x * n.x + D.z * n.z) + D.y * n.y;

        // Main record vertices (0x10410e5b-0x104113cd): radius, basis, four points in world space.
        void MainVertices(SurfDecal main, Vector3 point2, Vector3 n2, Vector3 base2, Vector3 decalDir, bool random, object texture, UEObject self)
        {
            if (!UEInterpreter.TryTextureSize(texture, out int uSize, out _))
                throw new InvalidOperationException($"AttachDecal: decal texture size of {TextureKey(texture)} not read");
            float ds = UEValue.AsFloat(self.GetProp(PK.DrawScale));
            float radius = (float)Math.Sqrt((((ds * ds) * uSize) * uSize) * 0.5f);   // 0x10410e5b-0x10410e8e

            // Axis A — direction projected onto the plane: d = (Dx·Nx + Dz·Nz) + Dy·Ny.
            Vector3 D = decalDir;
            Vector3 r0, r1;
            if (!random)
            {
                float d = DecalDot(D, n2);
                var a = new Vector3(D.x - d * n2.x, D.y - d * n2.y, D.z - d * n2.z);
                const float tiny = 9.99999974737875e-05f;
                if (X87Lt(Math.Abs(a.x), tiny) && X87Lt(Math.Abs(a.y), tiny) && X87Lt(Math.Abs(a.z), tiny))
                {
                    D = Sub(base2, point2);                                         // degenerate: pBase − point
                    random = true;
                }
                else
                {
                    var v = new Vector3(a.y * n2.z - a.z * n2.y, a.z * n2.x - a.x * n2.z, a.x * n2.y - a.y * n2.x);
                    a = NormalizeLoose(a);
                    // |V|² here is reduced in z, y, x order (0x1041104d-0x10411061).
                    float vq = (v.z * v.z + v.y * v.y) + v.x * v.x;
                    if (!X87Lt(vq, 9.99999993922529e-09f))
                    {
                        float vi = (float)(1.0 / (float)Math.Sqrt(vq));
                        v = new Vector3(v.x * vi, v.y * vi, v.z * vi);
                    }
                    r0 = new Vector3(a.x + v.x, a.y + v.y, a.z + v.z);
                    r1 = new Vector3(a.x - v.x, a.y - v.y, a.z - v.z);
                    goto basis;
                }
            }
            {
                float d = DecalDot(D, n2);                                          // 0x1041110d
                r0 = new Vector3(D.x - d * n2.x, D.y - d * n2.y, D.z - d * n2.z);
                r1 = new Vector3(r0.y * n2.z - r0.z * n2.y, r0.z * n2.x - r0.x * n2.z, r0.x * n2.y - r0.y * n2.x);
            }
        basis:
            r0 = NormalizeLoose(r0); r1 = NormalizeLoose(r1);
            r0 = Mul(r0, radius); r1 = Mul(r1, radius);
            main.Vertices[0] = Add(point2, r0);
            main.Vertices[1] = Add(point2, r1);
            main.Vertices[2] = Add(point2, new Vector3(-r0.x, -r0.y, -r0.z));
            main.Vertices[3] = Add(point2, new Vector3(-r1.x, -r1.y, -r1.z));
        }

        // FUN_104127c0: surface nodes on which the record (in world space) is not clipped to
        // nothing by the "normal × edge" planes of the node polygon.
        void BuildDecalNodes(int surf, SurfDecal rec)
            => BuildDecalNodes(Bsp, SurfNodeList(surf), Bsp.Vector(Bsp.Surf(surf).VNormal), rec);

        void BuildDecalNodes(HeadlessBspModel model, IReadOnlyList<int> surfNodes, Vector3 n, SurfDecal rec)
        {
            foreach (int node in surfNodes)
            {
                int nv = model.NodeVertexCount(node);
                if (nv == 0) continue;
                if (RecordSurvivesPolygon(k => model.NodeVertex(node, k), nv, n, rec)) rec.Nodes.Add(node);
            }
        }

        // One-node body of FUN_104127c0: the record (in world space) is clipped by the "normal ×
        // edge" planes of the polygon; true — something remains.
        static bool RecordSurvivesPolygon(Func<int, Vector3> vertex, int nv, Vector3 n, SurfDecal rec)
        {
            Span<float> px = stackalloc float[24], py = stackalloc float[24], pz = stackalloc float[24], dist = stackalloc float[24];
            {
                int count = 4;
                for (int i = 0; i < 4; i++) { px[i] = rec.Vertices[i].x; py[i] = rec.Vertices[i].y; pz[i] = rec.Vertices[i].z; }
                Vector3 A = vertex(nv - 1);
                bool alive = true;
                for (int e = 0; e < nv && alive; e++)
                {
                    Vector3 B = vertex(e);
                    float ez = B.z - A.z, ey = B.y - A.y, ex = B.x - A.x;
                    float Mz = ey * n.x - ex * n.y, My = ex * n.z - ez * n.x, Mx = ez * n.y - ey * n.z;
                    float Dp = (B.y * My + B.z * Mz) + B.x * Mx;
                    for (int i = 0; i < count; i++) dist[i] = ((Mz * pz[i] + My * py[i]) + Mx * px[i]) - Dp;
                    for (int i = 0; i < count; i++)
                    {
                        int nx = (i + 1) % count;
                        bool cross = (!X87Le(dist[i], 0f) && X87Lt(dist[nx], 0f)) || (X87Lt(dist[i], 0f) && !X87Le(dist[nx], 0f));
                        if (!cross) continue;
                        float dz = pz[nx] - pz[i], dy = py[nx] - py[i], dx = px[nx] - px[i];
                        float t = (Dp - ((Mx * px[i] + Mz * pz[i]) + My * py[i])) / ((My * dy + Mz * dz) + dx * Mx);
                        float ix = dx * t + px[i], iy = dy * t + py[i], iz = dz * t + pz[i];
                        for (int m = count; m > i + 1; m--) { dist[m] = dist[m - 1]; px[m] = px[m - 1]; py[m] = py[m - 1]; pz[m] = pz[m - 1]; }
                        dist[i + 1] = 0f; px[i + 1] = ix; py[i + 1] = iy; pz[i + 1] = iz;
                        count++;
                        if (count >= 24) throw new InvalidOperationException("AttachDecal: a node cuts the record into more than 23 points — shipped hits appFailAssert (Engine.dll 0x10412b9d)");
                        i++;
                    }
                    for (int i = 0; i < count; i++)
                    {
                        if (!X87Lt(dist[i], 0f)) continue;
                        for (int m = i; m < count - 1; m++) { dist[m] = dist[m + 1]; px[m] = px[m + 1]; py[m] = py[m + 1]; pz[m] = pz[m + 1]; }
                        count--; i--;
                    }
                    if (count == 0) alive = false;
                    A = B;
                }
                return alive;
            }
        }

        // ~FMovingBrushTracker truncates Model->Surfs to the constructor's count (FUN_1038cff0):
        // tracker surfaces go away with their Decals. Decal actors' SurfList keeps numbers
        // beyond that — a new tracker creates surfaces with the same numbers, but empty.
        internal void DropTrackerSurfDecals()
        {
            if (_surfDecals is null || !ReferenceEquals(_surfDecalsBsp, Bsp) || Bsp is null) return;
            int first = Bsp.ParseTables.Surfs.Length;
            var gone = new List<int>();
            foreach (int k in _surfDecals.Keys) if (k >= first) gone.Add(k);
            foreach (int k in gone) _surfDecals.Remove(k);
        }

        // execDetachDecal: while SurfList is not empty — the last surface; the actor's first
        // record on it is removed, SurfList loses its last element.
        public void DetachDecalCore(UEObject self)
        {
            if (self.GetProp(PK.SurfList) is not System.Collections.IList l) return;
            while (l.Count > 0)
            {
                int s = UEValue.ToInt(l[l.Count - 1]);
                if (_surfDecals is not null && ReferenceEquals(_surfDecalsBsp, Bsp) && _surfDecals.TryGetValue(s, out var list))
                    for (int i = 0; i < list.Count; i++)
                        if (ReferenceEquals(list[i].Actor, self)) { list.RemoveAt(i); break; }
                l.RemoveAt(l.Count - 1);
            }
        }
    }
}
