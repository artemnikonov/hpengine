// Actor skeletal-mesh query natives. These are gameplay-visible VM results,
// so they are resolved from the shipped mesh packages through Core;
// any frontend skinner or renderer bounds are presentation mirrors only.
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

        // UMesh::GetCollisionBoundingBox with bool = 0 (CylinderExtent.MeshLocalBox); extent = Max − Min, one fsub each
        // [HP1 0x1040aa96..0x1040aac9].
        public static Vector3 MeshLocalRenderExtent(Hp1MeshHashFields f, float drawScale)
        {
            (Vector3 mn, Vector3 mx) = CylinderExtent.MeshLocalBox(f.BoundMin, f.BoundMax, f.Scale, f.Origin, drawScale);
            return new Vector3(mx.x - mn.x, mx.y - mn.y, mx.z - mn.z);
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
