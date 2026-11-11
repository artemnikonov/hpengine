// Actor skeletal-mesh query natives. These are gameplay-visible VM results,
// so both frontends resolve them from the shipped mesh packages through Core;
// Unity's skinner and Renderer bounds are presentation mirrors only.
//
// Sources:
//   AActor::execBoneNumber       Engine.dll.c:106189
//   AActor::execBoneName         Engine.dll.c:42222
//   USkeletalMesh::BoneIndex     Engine.dll.c:14837
//   USkeletalMesh::BoneName      Engine.dll.c:46954
//   AActor::execGetRenderExtent  Engine.dll.c:66757
using System;
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        Hp1SkeletalMesh SkeletalMeshOf(UEObject actor)
        {
            if (actor?.GetProp(PK.Mesh) is not UEResourceRef meshRef
                || !meshRef.Class.Equals("SkeletalMesh",
                    StringComparison.OrdinalIgnoreCase))
                return null;
            if (MeshCatalog is null)
                throw new InvalidOperationException(
                    $"skeletal query before LoadMeshCatalogFromInstall "
                    + $"actor={actor.GetProp(PK.Name)} mesh={meshRef.ObjectPath}");
            return MeshCatalog.Get(meshRef);
        }

        static string BoneNameArg(object[] args, int index)
        {
            if (args.Length <= index || args[index] is null) return null;
            return args[index] is UEName name ? name.Value : args[index].ToString();
        }

        // Non-skeletal/no-mesh returns 0. A skeletal lookup miss, including
        // FName None, returns -1.
        public object BoneNumberNative(
            UEInterpreter interp, UEFrame frame, object[] args)
        {
            UEObject self = frame.Context ?? frame.Object;
            Hp1SkeletalMesh mesh = SkeletalMeshOf(self);
            return mesh is null ? 0 : mesh.FindBone(BoneNameArg(args, 0));
        }

        // Invalid index and non-skeletal/no-mesh return NAME_None.
        public object BoneNameNative(
            UEInterpreter interp, UEFrame frame, object[] args)
        {
            UEObject self = frame.Context ?? frame.Object;
            Hp1SkeletalMesh mesh = SkeletalMeshOf(self);
            int index = args.Length > 0 ? UEInterpreter.ToInt(args[0]) : 0;
            string name = mesh is not null && index >= 0 && index < mesh.Bones.Length
                ? mesh.Bones[index].Name
                : "None";
            return new UEName(name);
        }

        // Shipped skeletal meshes bypass GetCollisionBoundingBox here and
        // return the size of USkeletalMesh::SkeletalMeshSkeleton, the averaged
        // serialized per-frame FBox built by USkeletalMesh::Serialize.
        public object GetRenderExtentNative(
            UEInterpreter interp, UEFrame frame, object[] args)
        {
            UEObject self = frame.Context ?? frame.Object;
            Hp1SkeletalMesh mesh = SkeletalMeshOf(self);
            if (mesh is not null) return mesh.RenderExtent;

            if (self is null) return Vector3.zero;
            // AActor::execGetRenderExtent (Engine.dll.c:66757-66797, 0x1040aa10): prim = Mesh, else Brush, else
            // GetPrimitive(); box = prim->vtbl[+0x60](&tmp, this, 0). Slot +0x60 is GetCollisionBoundingBox in ALL
            // vtables (UMesh/ULodMesh/USkeletalMesh/UPrimitive/UModel->UBoxPrim, checked against the Engine.dll 1.53
            // vftable exports); third argument 0 = LOCAL box without rotation or location (not the world AABB of
            // the rotated box: MirrorInnerBoxObj0 in Lev5_Final, CollideType 2, Yaw 90°, would swap X and Y).
            // Consumer: ActorShadow.Update (shadow size).
            if (self.GetProp(PK.Mesh) is UEResourceRef meshRef)
                return MeshLocalRenderExtent(MeshHashFieldsOf(meshRef, self), UEInterpreter.AsFloat(self.GetProp(PK.DrawScale)));
            if (self.GetProp(PK.Brush) is UEModelRef)
            {
                BrushBounds brush = BrushBoundsOf(self);   // UBoxPrim: the model's serialized FBox, no ToWorld
                return brush.BoxMax - brush.BoxMin;
            }
            return PrimitiveLocalRenderExtent(UEPrimitive.Resolve(self),
                UEInterpreter.AsFloat(self.GetProp(PK.CollisionRadius)),
                UEInterpreter.AsFloat(self.GetProp(PK.CollisionWidth)),
                UEInterpreter.AsFloat(self.GetProp(PK.CollisionHeight)));
        }

        // UMesh::GetCollisionBoundingBox with bool = 0 (Engine.dll.c:33476-33600): Min/Max = (Bound − Origin) ·
        // (DrawScale·Scale) on X/Y, ((Bound − Origin)·DrawScale)·Scale.z on Z; extent = Max − Min.
        public static Vector3 MeshLocalRenderExtent(Hp1MeshHashFields f, float drawScale)
        {
            float sx = drawScale * f.Scale.x, sy = drawScale * f.Scale.y;
            Vector3 mn = new Vector3((f.BoundMin.x - f.Origin.x) * sx, (f.BoundMin.y - f.Origin.y) * sy, (f.BoundMin.z - f.Origin.z) * drawScale * f.Scale.z);
            Vector3 mx = new Vector3((f.BoundMax.x - f.Origin.x) * sx, (f.BoundMax.y - f.Origin.y) * sy, (f.BoundMax.z - f.Origin.z) * drawScale * f.Scale.z);
            return mx - mn;
        }

        // Primitive box with bool = 0: UBox (0x103049da) ±R, ±(W ≠ 0 ? W : R), ±H; UOrientedCylinder (0x103047eb)
        // ±R, ±R, ±H; UPrimitive (0x10303260, Engine.dll.c:92136) ±R, ±R around 0 and ±H around W on Z.
        public static Vector3 PrimitiveLocalRenderExtent(UEPrimitiveKind kind, float r, float w, float h)
        {
            float y = kind == UEPrimitiveKind.Box && w != 0f ? w : r;
            if (kind == UEPrimitiveKind.Box || kind == UEPrimitiveKind.OrientedCylinder)
                return new Vector3(r - -r, y - -y, h - -h);
            return new Vector3(r - -r, r - -r, (w + h) - (w - h));
        }
    }
}
