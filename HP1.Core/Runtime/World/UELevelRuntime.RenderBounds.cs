// RENDER BOX OF A MESH ACTOR — AActor::GetRenderBoundingBox (Engine.dll.c:31261):
// Mesh != NULL -> mesh slot +0x70. Called by FDynamicSprite::Setup (Render 0x10b30d56-
// 0x10b30d61, push 1) before BoundVisible: shipped uses it to decide whether to draw the actor,
// i.e. whether to run its GetFrame (ApplyAnim, weapon point). Returns the box FCoords
// (Origin = Min corner, axes = sides), as shipped does (`?AVFCoords@@` in the export name).
//
// USkeletalMesh::GetRenderBoundingBox (export 0x10303f35 -> 0x1041b2f0, disasm):
//   1. ApplyAnim(actor, NULL, true) (0x1041b319-0x1041b325);
//   2. skeleton cache header — FMemCache::Get(Index*0x100+0x34) without creating it;
//   3. B = sequence box (FUN_1041b500, below);
//   4. B invalid -> operator<<(Mesh.BoundingBox +0x28, header+0x20 = MeshCoords)
//      (0x1041b37b-0x1041b399); valid -> C = FCoords(places+0x1c [0]) /= MeshCoords
//      (0x1041b3bf-0x1041b40f), operator<<(B, C). The "no cache" branch (bone 0 ref pose,
//      /= at address 0x20) crashes in shipped — after ApplyAnim the cache always exists.
//
// FUN_1041b500 (disasm 0x1041b500-0x1041b7ce), actor box from the sequence:
//   B = empty; SkelAnim (+0x68) and AnimSequence (+0x50) non-null -> sequence index in
//   SkelAnim.Sequences (+0x64, stride 0x20, exact FName) -> B = Mesh(+0x164).BoundingBoxes
//   (+0xa8)[index] as a whole, with IsValid. Skeleton cache exists: TweenAlpha (+0x60) ≠ 1.0
//   (bitwise 0x3f800000) and the cache box (+0x50, IsValid +0x68) is valid -> B invalid ?
//   B = cache box : union; then cache box = B (always, with a live cache). Then
//   every AuxAnims entry (+0x6c/+0x70) recursively; valid ones are unioned into B. The cache
//   box is empty on creation (ApplyAnim 0x1041bc1e-0x1041bc33).
//
// UMesh::GetRenderBoundingBox (export 0x10303562 -> 0x103b80f0, disasm; ULodMesh has none of
// its own): sequence by name (vtable +0x74) and AnimFrame ≥ 0 -> f = (AnimFrame + 1)·NumFrames
// (fimul, stored to float), i = fistp(f − 0.5), box = BoundingBoxes[i % N + Start] +=
// BoundingBoxes[(i + 1) % N + Start]; else BoundingBox (+0x28). Coords:
// Origin = −Mesh.Origin, unit axes; Origin += DrawScale·Mesh.Scale — really an addition
// (import 0x106091f0 = FVector::operator+=, Core body 0x101194e0), the box is not scaled;
// /= RotOrigin; Origin += PrePivot; /= Rotation; Origin += Location; operator<<(box, C).
//
// Precision: the bodies set no control word and are called from the renderer (FDynamicSprite::Setup, PC24 window):
// every step follows UEWorldFrameProducer.FastMathWindow (D-59; checked against the real bodies at both FPCWs).
using System;
using System.Collections.Generic;
using System.IO;
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        public BrushCoords MeshRenderBoundingBox(UEObject actor)
        {
            if (actor.GetProp(PK.Mesh) is not UEResourceRef meshRef)
                throw new InvalidOperationException($"GetRenderBoundingBox: actor {actor.GetProp(PK.Name)} without Mesh");
            if (MeshCatalog is null) throw new InvalidOperationException("GetRenderBoundingBox without a mesh catalog");
            if (meshRef.Class.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
            {
                SkelCache cache = ApplyAnim(actor, rootOnly: true);
                Hp1SkeletalMesh mesh = MeshCatalog.Get(meshRef);
                Hp1Box b = SequenceRenderBox(actor);
                if (!b.IsValid) return BoxByCoords(mesh.PrimitiveBox, cache.MeshCoords);
                BoneWorld root = DivCoords(CoordsOfPlace(cache.Places[0]), AsBoneWorld(cache.MeshCoords));
                return BoxByCoords(b, new BrushCoords { Origin = root.Origin, X = root.X, Y = root.Y, Z = root.Z });
            }
            if (meshRef.Class.Equals("LodMesh", StringComparison.OrdinalIgnoreCase))
                return VertexMeshRenderBoundingBox(actor, meshRef);
            throw new NotSupportedException(
                $"GetRenderBoundingBox: mesh of class {meshRef.Class} not ported mesh={meshRef.Name}");
        }

        Hp1Box SequenceRenderBox(UEObject actor)
        {
            var box = new Hp1Box(default, default, false);
            string seq = SequenceNameOf(actor);
            if (actor.GetProp(PK.SkelAnim) is UEResourceRef anim && !SameName(seq, "None"))
            {
                if (!anim.Class.Equals("Animation", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Actor.SkelAnim is {anim.Class} actor={actor.GetProp(PK.Name)}");
                if (MeshCatalog.GetAnimation(anim).TryGetValue(seq, out Hp1AnimSequence s))
                {
                    if (actor.GetProp(PK.Mesh) is not UEResourceRef meshRef
                        || !meshRef.Class.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
                        throw new NotSupportedException(
                            $"sequence box: Mesh of actor {actor.GetProp(PK.Name)} is not skeletal ({actor.GetProp(PK.Mesh)})");
                    Hp1Box[] boxes = MeshCatalog.Get(meshRef).BoundingBoxes;
                    if (s.Index < 0 || s.Index >= boxes.Length)
                        throw new InvalidDataException(
                            $"sequence box: index {s.Index} ({seq}) outside BoundingBoxes[{boxes.Length}] of mesh {meshRef.Name} — shipped reads past the array");
                    box = boxes[s.Index];
                }
            }
            if (_skelCache.TryGetValue(actor, out SkelCache cache))
            {
                if (BitConverter.SingleToInt32Bits(UEInterpreter.AsFloat(actor.GetProp(PK.TweenAlpha))) != 0x3f800000
                    && cache.RenderBox.IsValid)
                    box = box.IsValid ? Union(box, cache.RenderBox) : cache.RenderBox;
                cache.RenderBox = box;
            }
            if (actor.GetProp(PK.AuxAnims) is System.Collections.IList aux)
                for (int i = 0; i < aux.Count; i++)
                {
                    if (aux[i] is not UEObject ch)
                        throw new InvalidOperationException($"sequence box: AuxAnims[{i}] of actor {actor.GetProp(PK.Name)} is not an actor");
                    Hp1Box b = SequenceRenderBox(ch);
                    if (b.IsValid) box = box.IsValid ? Union(box, b) : b;
                }
            return box;
        }

        // Union of FUN_1041b500 and FBox::operator+= (Core 0x1011bc80): the minimum stays
        // its own on "less, equal or unordered" (test ah,0x41), the maximum is taken
        // from the other on "less or unordered" (test ah,1).
        static Hp1Box Union(in Hp1Box a, in Hp1Box b)
        {
            static float Min(float x, float y) => x > y ? y : x;
            static float Max(float x, float y) => !(x >= y) ? y : x;
            return new Hp1Box(new Vector3(Min(a.Min.x, b.Min.x), Min(a.Min.y, b.Min.y), Min(a.Min.z, b.Min.z)),
                new Vector3(Max(a.Max.x, b.Max.x), Max(a.Max.y, b.Max.y), Max(a.Max.z, b.Max.z)), true);
        }

        // operator<<(const FBox&, const FCoords&) — Core 0x1014f3a0: side differences in a
        // register, axes = side·axis of C (stored to float); origin — sum in disasm order.
        // The body sets no control word; its only caller is GetRenderBoundingBox from FDynamicSprite::Setup, inside the
        // render window (PC24): every fsub/fmul/fadd rounds to 24 bits, i.e. float steps in the same order.
        // RU EA (UEEdition.RuOrder; RU Core 0x1014ed12..0x1014ed50): Origin.y and .z summed as x is,
        // ((n.y·Y + n.z·Z) + n.x·X) + O.
        static BrushCoords BoxByCoords(in Hp1Box b, in BrushCoords c)
        {
            bool ru = UEEdition.RuOrder;
            if (UEWorldFrameProducer.FastMathWindow)
            {
                float fz = b.Max.z - b.Min.z, fy = b.Max.y - b.Min.y, fx = b.Max.x - b.Min.x;
                Vector3 n = b.Min;
                return new BrushCoords
                {
                    Origin = new Vector3(((n.y * c.Y.x + n.z * c.Z.x) + n.x * c.X.x) + c.Origin.x,
                        ru ? ((n.y * c.Y.y + n.z * c.Z.y) + n.x * c.X.y) + c.Origin.y
                           : ((c.Z.y * n.z + c.X.y * n.x) + c.Y.y * n.y) + c.Origin.y,
                        ru ? ((n.y * c.Y.z + n.z * c.Z.z) + n.x * c.X.z) + c.Origin.z
                           : ((c.Z.z * n.z + c.X.z * n.x) + c.Y.z * n.y) + c.Origin.z),
                    X = new Vector3(fx * c.X.x, fx * c.X.y, fx * c.X.z),
                    Y = new Vector3(fy * c.Y.x, fy * c.Y.y, fy * c.Y.z),
                    Z = new Vector3(fz * c.Z.x, fz * c.Z.y, fz * c.Z.z),
                };
            }
            double dz = (double)b.Max.z - b.Min.z, dy = (double)b.Max.y - b.Min.y, dx = (double)b.Max.x - b.Min.x;
            Vector3 m = b.Min;
            return new BrushCoords
            {
                Origin = new Vector3(
                    (float)(((double)m.y * c.Y.x + (double)m.z * c.Z.x) + (double)m.x * c.X.x + c.Origin.x),
                    ru ? (float)(((double)m.y * c.Y.y + (double)m.z * c.Z.y) + (double)m.x * c.X.y + c.Origin.y)
                       : (float)(((double)c.Z.y * m.z + (double)c.X.y * m.x) + (double)c.Y.y * m.y + c.Origin.y),
                    ru ? (float)(((double)m.y * c.Y.z + (double)m.z * c.Z.z) + (double)m.x * c.X.z + c.Origin.z)
                       : (float)(((double)c.Z.z * m.z + (double)c.X.z * m.x) + (double)c.Y.z * m.y + c.Origin.z)),
                X = new Vector3((float)(dx * c.X.x), (float)(dx * c.X.y), (float)(dx * c.X.z)),
                Y = new Vector3((float)(dy * c.Y.x), (float)(dy * c.Y.y), (float)(dy * c.Y.z)),
                Z = new Vector3((float)(dz * c.Z.x), (float)(dz * c.Z.y), (float)(dz * c.Z.z)),
            };
        }

        BrushCoords VertexMeshRenderBoundingBox(UEObject actor, UEResourceRef meshRef)
        {
            Hp1VertexMesh vm = MeshCatalog.GetVertexMesh(meshRef.Name, meshRef.ObjectPath);
            string seqName = actor.GetProp(PK.AnimSequence)?.ToString() ?? "None";
            return UMeshRenderBoundingBox(vm, MeshCatalog.FindSequence(meshRef, seqName),
                UEInterpreter.AsFloat(actor.GetProp(PK.AnimFrame)), UEInterpreter.AsFloat(actor.GetProp(PK.DrawScale)),
                UEInterpreter.AsVector3(actor.GetProp(PK.PrePivot)), UEInterpreter.AsRotator(actor.GetProp(PK.Rotation)),
                UEInterpreter.AsVector3(actor.GetProp(PK.Location)));
        }

        // UMesh::GetRenderBoundingBox body over its inputs (also called by the --lodboxdump export for the oracle).
        public static BrushCoords UMeshRenderBoundingBox(Hp1VertexMesh vm, Hp1AnimSequence seq, float animFrame,
            float ds, Vector3 pre, UERotator rot, Vector3 loc)
        {
            Hp1Box box;
            if (seq is null || !(animFrame >= 0f)) box = vm.PrimitiveBox;
            else
            {
                if (seq.NumFrames <= 0)
                    throw new InvalidDataException($"UMesh::GetRenderBoundingBox: sequence {seq.Name} of mesh {vm.Name} has no frames (idiv by 0)");
                // fld AnimFrame; fadd 1.0f; fimul NumFrames; fstp dword (0x103b8148..0x103b8154): inside the render window
                // (PC24) the sum rounds to 24 bits before the product; at PC64 it is exact.
                float f = UEWorldFrameProducer.FastMathWindow ? (float)((double)(animFrame + 1f) * seq.NumFrames)
                    : (float)(((double)animFrame + 1.0) * seq.NumFrames);
                int i = (int)Math.Round((double)f - 0.5, MidpointRounding.ToEven);
                int a = i % seq.NumFrames + seq.StartFrame, b = (i + 1) % seq.NumFrames + seq.StartFrame;
                if (a < 0 || b < 0 || a >= vm.BoundingBoxes.Length || b >= vm.BoundingBoxes.Length)
                    throw new InvalidDataException(
                        $"UMesh::GetRenderBoundingBox: frames {a}/{b} outside BoundingBoxes[{vm.BoundingBoxes.Length}] of mesh {vm.Name}");
                box = vm.BoundingBoxes[a];
                Hp1Box other = vm.BoundingBoxes[b];
                if (other.IsValid) box = box.IsValid ? Union(box, other) : other;
            }
            BrushCoords c = BrushCoords.Identity;
            c.Origin = new Vector3(ds * vm.Scale.x + -vm.Origin.x, ds * vm.Scale.y + -vm.Origin.y, ds * vm.Scale.z + -vm.Origin.z);
            c.DivRotator(vm.RotPitch, vm.RotYaw, vm.RotRoll);
            c.Origin = new Vector3(c.Origin.x + pre.x, c.Origin.y + pre.y, c.Origin.z + pre.z);
            c.DivRotator(rot.Pitch, rot.Yaw, rot.Roll);
            c.Origin = new Vector3(c.Origin.x + loc.x, c.Origin.y + loc.y, c.Origin.z + loc.z);
            return BoxByCoords(box, c);
        }
    }
}
