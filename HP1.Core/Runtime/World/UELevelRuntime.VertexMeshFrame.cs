// ULodMesh::GetFrame (Engine 0x1035e460, Engine.dll.c:211393-211724) — WORLD-space vertices
// of a drawn vertex mesh (input coords = identity).
//
// Presentation: only the frontend draws a vertex mesh; the body writes no Core actor
// fields (see RenderMeshFrame). The body's shape is ported, without a bit-exact x87 check.
// LOD REQUEST (D-71): GetFrame runs once per DrawLodMesh, i.e. per
// (actor, frame) draw in DrawFrame order (UEWorldFrameProducer.DirectRenderStep), with that draw's request
// (*param, the DrawLodMesh level; ModelVerts when the draw has no LOD). 0x1035e4ac..0x1035e4c7:
// full = min(SpecialVerts + request, FrameVerts); only that many vertices are computed into the cache and the
// count is stored at cache+0xc (0 on creation); the frame branch writes *request = count − SpecialVerts; the tween
// branch (0x1035e7bb..0x1035e7dc) first caps the count at cache+0xc and writes the capped level back, and at
// acc > 0.97 (0x1035e888..0x1035e900) fills the missing vertices [count, full) from the raw sequence Start frame
// and restores the full count; DrawLodMesh draws the level GetFrame returns (Render 0x10b10270, 0x10b103c8,
// 0x10b109ac, 0x10b109d9). Points at and above the returned count are stale and no drawn face reaches them.
// The cache is a GCache item: GCache.Flush (SaveGame 0x103a26b9, UEngine::Flush) drops it (FlushGCache); LRU
// eviction under memory pressure is not modelled (STOP audio-anim FMemCache).
//
// Coords (211520-211596): C = input; C.Origin −= PrePivot + Location;
// C *= actor Rotation; C *= mesh RotOrigin; C *= FScale(DrawScale·mesh Scale,
// no shift); C.Origin += mesh Origin; vertex = (v − C.Origin)·axes of C.
//
// Frame (211597-211656), if AnimFrame ≥ 0 or there was no cache yet: for the found
// sequence f = NumFrames·max(AnimFrame, 0), i = fistp(f − 0.5)
// (round to even), t = f − i; frames (i mod N + Start) and ((i+1) mod N +
// Start), for t > 0 their blend (b − a)·t + a. No sequence — frame 0.
//
// Tween (211658-211714), AnimFrame < 0 with a live cache: t = 1 − AnimFrame/previous
// AnimFrame; a sequence change zeroes the accumulator; a change or t outside
// [0, 1] — t = 0 and previous AnimFrame := −1/NumFrames; accumulator
// acc = (1 − acc)·t + acc; for acc ≤ 0.97 and t > 0 the cache is blended with the
// sequence's Start frame; otherwise the cache is returned as is. Finally previous
// AnimFrame := AnimFrame. Cache vertices are in mesh space, updated by both branches.
using System;
using System.Collections.Generic;
using System.IO;
using HP1.Core;
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        // One GCache item per actor (key Index·0x100 + 0x1d in both ULodMesh::GetFrame 0x1035e4ec and UMesh::GetFrame
        // 0x103b897f), recreated when its mesh pointer [0] is another mesh. UMesh uses Mesh, PrevFrame, Seq and Verts (+0x10).
        internal sealed class VertexFrameCache
        {
            public object Mesh;
            public float PrevFrame;      // +4
            public string Seq = "None";  // +8 (FName), new cache — 0 = NAME_None
            public int Count;            // +0xc, vertices computed (0 on creation)
            public float Accum = 1f;     // +0x10, new cache — 1.0 (0x3f800000)
            public Vector3[] Verts;      // +0x20
        }
        readonly System.Runtime.CompilerServices.ConditionalWeakTable<UEObject, VertexFrameCache> _vertexFrames = new();
        // Test seam (headless, InternalsVisibleTo): the actor's vertex cache, for the UMesh oracle replay.
        internal VertexFrameCache VertexFrameCacheOf(UEObject actor) => _vertexFrames.TryGetValue(actor, out var c) ? c : null;

        // Original: Engine.dll ?GetFrame@UMesh@@UAEXPAVFVector@@HVFCoords@@PAVAActor@@@Z [HP1 0x103B88F0]
        // UMesh::GetFrame of a plain UMesh (ULodMesh overrides the slot): body 0x103b88f0..0x103b8e23, frame branch
        // 0x103b8e26..0x103b9050 (Engine.dll.c:110264-110511).
        // Cache: created ([0] mesh, +4 PrevFrame 0.0, +8 sequence NAME_None, 0x103b8a34..0x103b8a3c; vertices at +0x10 in mesh
        //   space); another mesh in [0] flushes and recreates it (0x103b89be..0x103b89dc).
        // Coords C (input): C.Origin −= PrePivot + Location (the sum in the register, one fstp, 0x103b8a8f..0x103b8aff);
        //   C *= Rotation; C *= mesh RotOrigin; C *= FScale(fstp(DrawScale·mesh Scale), no sheer); a vertex leaves as
        //   fstp(v − mesh Origin) transformed by C (TransformPointBy inline: ((dx·X.x + dy·X.y) + dz·X.z) in the register).
        // Animation of AnimSourceOf (the owner under bAnimByOwner, 0x103b894a..0x103b895d; key and placement stay the actor's);
        //   sequence by GetAnimSeq (vtable +0x74, first FName match).
        //   Frame branch (AnimFrame >= 0, or a fresh cache): f = fstp(N·max(AnimFrame, 0)), i = fistp(f − 0.5), t = f − i,
        //     frames (i mod N + Start) and ((i+1) mod N + Start) (no sequence: frame 0, t = 0); cache = (b − a)·t + a with x in
        //     the register and y, z through fstp of each step. PrevFrame and the sequence are NOT written (the loop leaves to
        //     0x103b8e04, past the store at 0x103b8dfc).
        //   Tween branch (AnimFrame < 0 or NaN, live cache): t = fstp(1 − AnimFrame/PrevFrame); another sequence, t < 0, NaN
        //     or t > 1 — sequence := AnimSequence, t := 0, PrevFrame := −1/N (0.0 without a sequence); cache =
        //     (Start frame − cache)·t + cache, x in the register; then PrevFrame := AnimFrame.
        // Precision: no control word of its own; DrawMesh calls it in the render window (PC24, FastMathWindow); the PC64 path
        //   follows the 64-bit register (X87Ext). The pool is read with no bound in the original: out of range is loud here.
        public Vector3[] UMeshFramePoints(UEObject actor, in BrushCoords coords)
        {
            if (actor.GetProp(PK.Mesh) is not UEResourceRef meshRef)
                throw new NotSupportedException("UMesh::GetFrame: actor without Mesh");
            if (MeshCatalog is null) throw new InvalidOperationException("UMesh::GetFrame without a mesh catalog");
            Hp1VertexMesh vm = MeshCatalog.GetVertexMesh(meshRef.Name, meshRef.ObjectPath);
            if (!vm.Plain) throw new InvalidOperationException($"UMesh::GetFrame: {meshRef.Name} is a ULodMesh (its own GetFrame)");
            int n = vm.FrameVerts;
            bool fresh = !_vertexFrames.TryGetValue(actor, out VertexFrameCache c) || !ReferenceEquals(c.Mesh, vm);
            if (fresh)
            {
                c = new VertexFrameCache { Mesh = vm, PrevFrame = 0f, Seq = "None", Verts = new Vector3[n] };
                _vertexFrames.AddOrUpdate(actor, c);
            }
            bool pc24 = UEWorldFrameProducer.FastMathWindow;
            X87Ext R(X87Ext v) => pc24 ? X87Ext.Round24(v) : X87Ext.Round64(v);
            static X87Ext E(float v) => X87Ext.From(v);

            Vector3 loc = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
            Vector3 pre = UEInterpreter.AsVector3(actor.GetProp(PK.PrePivot));
            UERotator rot = UEInterpreter.AsRotator(actor.GetProp(PK.Rotation));
            float ds = actor.GetProp(PK.DrawScale) is { } dsv ? UEInterpreter.AsFloat(dsv) : 1f;
            BrushCoords cc = coords;
            float Less(float o, float p, float l) => R(E(o) - R(E(p) + E(l))).ToFloat();
            cc.Origin = new Vector3(Less(coords.Origin.x, pre.x, loc.x), Less(coords.Origin.y, pre.y, loc.y), Less(coords.Origin.z, pre.z, loc.z));
            cc.MulRotator(rot.Pitch, rot.Yaw, rot.Roll);
            cc.MulRotator(vm.RotPitch, vm.RotYaw, vm.RotRoll);
            cc.MulScale(new Vector3(ds * vm.Scale.x, ds * vm.Scale.y, ds * vm.Scale.z), 0f, 0);

            UEObject animActor = AnimSourceOf(actor);
            string seqName = (animActor.GetProp(PK.AnimSequence) is UEName sn ? sn.ToString() : animActor.GetProp(PK.AnimSequence)?.ToString()) ?? "None";
            Hp1AnimSequence seq = MeshCatalog.FindSequence(meshRef, seqName);
            float af = UEInterpreter.AsFloat(animActor.GetProp(PK.AnimFrame));
            Vector3[] pool = vm.Verts;
            Vector3 Pool(int at)
            {
                if (at < 0 || at >= pool.Length)
                    throw new InvalidDataException($"UMesh::GetFrame: vertex {at} outside the pool [{pool.Length}] of {vm.Name} (the original reads past it)");
                return pool[at];
            }
            var result = new Vector3[n];
            Vector3 Out(Vector3 p)
            {
                float dx = p.x - vm.Origin.x, dy = p.y - vm.Origin.y, dz = p.z - vm.Origin.z;
                if (pc24)
                {
                    float x = dx - cc.Origin.x, y = dy - cc.Origin.y, z = dz - cc.Origin.z;
                    return new Vector3((x * cc.X.x + y * cc.X.y) + z * cc.X.z, (x * cc.Y.x + y * cc.Y.y) + z * cc.Y.z,
                        (x * cc.Z.x + y * cc.Z.y) + z * cc.Z.z);
                }
                X87Ext ex = R(E(dx) - E(cc.Origin.x)), ey = R(E(dy) - E(cc.Origin.y)), ez = R(E(dz) - E(cc.Origin.z));
                float Dot(Vector3 a) => R(R(R(ex * E(a.x)) + R(ey * E(a.y))) + R(ez * E(a.z))).ToFloat();
                return new Vector3(Dot(cc.X), Dot(cc.Y), Dot(cc.Z));
            }

            if (af >= 0f || fresh)
            {
                int a = 0, b = 0; float t = 0f;
                if (seq is not null)
                {
                    if (seq.NumFrames == 0) throw new InvalidDataException($"UMesh::GetFrame: sequence {seq.Name} of {vm.Name} has no frames (idiv by 0)");
                    // fild N; fmul max(AnimFrame, 0); fstp (0x103b8e35..0x103b8e52); fsub 0.5; fistp; fild i; fsubr f; fstp.
                    float f = R(X87Ext.From(seq.NumFrames) * E(af >= 0f ? af : 0f)).ToFloat();
                    int i = Fistp(R(E(f) - E(0.5f)));
                    t = R(E(f) - X87Ext.From(i)).ToFloat();
                    a = unchecked((i % seq.NumFrames + seq.StartFrame) * n);
                    b = unchecked(((i + 1) % seq.NumFrames + seq.StartFrame) * n);
                }
                for (int k = 0; k < n; k++)
                {
                    Vector3 va = Pool(a + k), vb = Pool(b + k);
                    // x: (fild b − fild a)·t + a in the register (0x103b8f1f..0x103b8f56); y, z: fstp after each step.
                    float x = pc24 ? (vb.x - va.x) * t + va.x : R(R(E(vb.x - va.x) * E(t)) + E(va.x)).ToFloat();
                    c.Verts[k] = new Vector3(x, (vb.y - va.y) * t + va.y, (vb.z - va.z) * t + va.z);
                    result[k] = Out(c.Verts[k]);
                }
                return result;
            }
            int start = 0; float neg = 0f;
            if (seq is not null)
            {
                start = unchecked(seq.StartFrame * n);
                neg = X87Ext.Div64(E(-1f), X87Ext.From(seq.NumFrames)).ToFloat();   // fild N; fdivr −1.0; fstp (one division)
            }
            // fld AnimFrame; fdiv PrevFrame; fsubr 1.0; fstp (0x103b8c49..0x103b8c5b).
            float tw = pc24 ? 1f - af / c.PrevFrame : R(E(1f) - R(X87Ext.Div64(E(af), E(c.PrevFrame)))).ToFloat();
            if (!string.Equals(c.Seq, seqName, StringComparison.OrdinalIgnoreCase) || !(tw >= 0f) || tw > 1f)
            {
                c.Seq = seqName;
                tw = 0f;
                c.PrevFrame = neg;
            }
            for (int k = 0; k < n; k++)
            {
                Vector3 v = Pool(start + k), p = c.Verts[k];
                // x: (fild v − cache)·t + cache in the register (0x103b8ce3..0x103b8d0e); y, z: fstp after each step.
                float x = pc24 ? (v.x - p.x) * tw + p.x : R(R(R(E(v.x) - E(p.x)) * E(tw)) + E(p.x)).ToFloat();
                c.Verts[k] = new Vector3(x, (v.y - p.y) * tw + p.y, (v.z - p.z) * tw + p.z);
                result[k] = Out(c.Verts[k]);
            }
            c.PrevFrame = af;
            return result;
        }

        // fistp (RC nearest even); out of range or NaN gives the integer indefinite 0x80000000.
        static int Fistp(X87Ext v)
        {
            if (!v.IsFinite) return int.MinValue;
            double d = Math.Round(v.ToDouble(), MidpointRounding.ToEven);
            return d >= int.MinValue && d <= int.MaxValue ? (int)d : int.MinValue;
        }

        // request: the DrawLodMesh level in, the level GetFrame leaves in it out.
        public Vector3[] EvaluateVertexMeshPoints(UEObject actor, ref int request)
        {
            if (actor.GetProp(PK.Mesh) is not UEResourceRef meshRef)
                throw new NotSupportedException("vertex mesh frame: actor without Mesh");
            if (!meshRef.Class.Equals("LodMesh", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"ULodMesh::GetFrame: mesh of class {meshRef.Class} is not a ULodMesh (a plain UMesh draws through UMeshFramePoints) mesh={meshRef.Name}");
            if (MeshCatalog is null)
                throw new InvalidOperationException("vertex mesh frame without a mesh catalog");
            Hp1VertexMesh vm = MeshCatalog.GetVertexMesh(meshRef.Name, meshRef.ObjectPath);
            int n = vm.FrameVerts;

            bool fresh = !_vertexFrames.TryGetValue(actor, out VertexFrameCache c) || !ReferenceEquals(c.Mesh, vm);
            if (fresh)
            {
                c = new VertexFrameCache { Mesh = vm, Verts = new Vector3[n] };
                _vertexFrames.AddOrUpdate(actor, c);
            }

            BrushCoords coords = BrushCoords.Identity;
            Vector3 loc = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
            Vector3 pre = UEInterpreter.AsVector3(actor.GetProp(PK.PrePivot));
            coords.Origin = new Vector3(-(pre.x + loc.x), -(pre.y + loc.y), -(pre.z + loc.z));
            UERotator rot = UEInterpreter.AsRotator(actor.GetProp(PK.Rotation));
            coords.MulRotator(rot.Pitch, rot.Yaw, rot.Roll);
            coords.MulRotator(vm.RotPitch, vm.RotYaw, vm.RotRoll);
            float ds = actor.GetProp(PK.DrawScale) is { } dsv ? UEInterpreter.AsFloat(dsv) : 1f;
            coords.MulScale(new Vector3(ds * vm.Scale.x, ds * vm.Scale.y, ds * vm.Scale.z), 0f, 0);
            coords.Origin = new Vector3(coords.Origin.x + vm.Origin.x, coords.Origin.y + vm.Origin.y, coords.Origin.z + vm.Origin.z);

            // bAnimByOwner (AnimSourceOf): the owner's AnimSequence (0x1035e73d..0x1035e74e) and AnimFrame (0x1035e808,
            // 0x1035eb0b, 0x1035eb54); the cache key and every placement field stay the drawn actor's (0x1035e4db,
            // 0x1035e5ef, 0x1035e6dc).
            UEObject animActor = AnimSourceOf(actor);
            string seqName = (animActor.GetProp(PK.AnimSequence) is UEName sn ? sn.ToString() : animActor.GetProp(PK.AnimSequence)?.ToString()) ?? "None";
            Hp1AnimSequence seq = MeshCatalog.FindSequence(meshRef, seqName);
            float animFrame = UEInterpreter.AsFloat(animActor.GetProp(PK.AnimFrame));
            Vector3[] pool = vm.Verts;
            int full = Math.Min(vm.SpecialVerts + request, n), cnt = full;

            if (animFrame >= 0f || fresh)
            {
                request = cnt - vm.SpecialVerts;
                c.Count = cnt;
                int a = 0, b = 0; float t = 0f;
                if (seq is not null && seq.NumFrames > 0)
                {
                    float f = seq.NumFrames * Math.Max(animFrame, 0f);
                    int i = (int)Math.Round((double)f - 0.5, MidpointRounding.ToEven);
                    t = f - i;
                    a = (i % seq.NumFrames + seq.StartFrame) * n;
                    b = ((i + 1) % seq.NumFrames + seq.StartFrame) * n;
                }
                for (int k = 0; k < cnt; k++)
                {
                    Vector3 va = pool[a + k];
                    c.Verts[k] = t > 0f
                        ? new Vector3((pool[b + k].x - va.x) * t + va.x, (pool[b + k].y - va.y) * t + va.y, (pool[b + k].z - va.z) * t + va.z)
                        : va;
                }
            }
            else
            {
                if (c.Count < cnt) cnt = c.Count;
                c.Count = cnt;
                request = cnt - vm.SpecialVerts;
                int start = 0; float neg = 0f;
                if (seq is not null && seq.NumFrames > 0) { start = seq.StartFrame * n; neg = -1f / seq.NumFrames; }
                float t = 1f - animFrame / c.PrevFrame;
                bool sameSeq = string.Equals(c.Seq, seqName, StringComparison.OrdinalIgnoreCase);
                if (!sameSeq) c.Accum = 0f;
                if (!sameSeq || t < 0f || 1f < t) { c.PrevFrame = neg; t = 0f; c.Seq = seqName; }
                float acc = (1f - c.Accum) * t + c.Accum;
                c.Accum = acc;
                if (acc > 0.97f)
                {
                    t = 0f;
                    if (cnt < full)
                    {
                        for (int k = cnt; k < full; k++) c.Verts[k] = pool[start + k];
                        cnt = full;
                        request = full - vm.SpecialVerts;
                        c.Count = full;
                    }
                }
                if (t > 0f)
                    for (int k = 0; k < cnt; k++)
                    {
                        Vector3 p = c.Verts[k], q = pool[start + k];
                        c.Verts[k] = new Vector3((q.x - p.x) * t + p.x, (q.y - p.y) * t + p.y, (q.z - p.z) * t + p.z);
                    }
                c.PrevFrame = animFrame;
            }

            var result = new Vector3[n];
            for (int k = 0; k < n; k++) result[k] = coords.TransformPoint(c.Verts[k]);
            return result;
        }
    }
}
