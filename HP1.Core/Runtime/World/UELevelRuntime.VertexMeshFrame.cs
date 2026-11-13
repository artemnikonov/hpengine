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
using HP1.Core;
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        sealed class VertexFrameCache
        {
            public object Mesh;
            public float PrevFrame;      // +4
            public string Seq = "None";  // +8 (FName), new cache — 0 = NAME_None
            public int Count;            // +0xc, vertices computed (0 on creation)
            public float Accum = 1f;     // +0x10, new cache — 1.0 (0x3f800000)
            public Vector3[] Verts;      // +0x20
        }
        readonly System.Runtime.CompilerServices.ConditionalWeakTable<UEObject, VertexFrameCache> _vertexFrames = new();

        // request: the DrawLodMesh level in, the level GetFrame leaves in it out.
        public Vector3[] EvaluateVertexMeshPoints(UEObject actor, ref int request)
        {
            if (actor.GetProp(PK.Mesh) is not UEResourceRef meshRef)
                throw new NotSupportedException("vertex mesh frame: actor without Mesh");
            if (!meshRef.Class.Equals("LodMesh", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException(
                    $"vertex mesh frame: mesh of class {meshRef.Class} (UMesh vertex branch) not ported mesh={meshRef.Name}");
            if (MeshCatalog is null)
                throw new InvalidOperationException("vertex mesh frame without a mesh catalog");
            RefuseAnimByOwner(actor, "vertex mesh frame");
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
            coords.MulScale(new Vector3(ds * vm.Scale.x, ds * vm.Scale.y, ds * vm.Scale.z), 0f, "vertex mesh frame");
            coords.Origin = new Vector3(coords.Origin.x + vm.Origin.x, coords.Origin.y + vm.Origin.y, coords.Origin.z + vm.Origin.z);

            string seqName = (actor.GetProp(PK.AnimSequence) is UEName sn ? sn.ToString() : actor.GetProp(PK.AnimSequence)?.ToString()) ?? "None";
            Hp1AnimSequence seq = MeshCatalog.FindSequence(meshRef, seqName);
            float animFrame = UEInterpreter.AsFloat(actor.GetProp(PK.AnimFrame));
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
