// The collision hash and actor collision primitives: mesh bounds, SetCollision/SetCollisionSize,
// hash boxes, cylinder extents, world collision boxes, hash add/remove. Fields live in UELevelRuntime.cs.
using System;
using System.Collections.Generic;
using System.IO;
using UELib.Core;
using Vector3 = HP1.UEScript.UEVector;
using X87Ext = HP1.Core.X87Ext;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        // Opens the shipped package set on first use. Both mesh readers need it,
        // and both need the same "the world has to be loaded first" diagnostic.
        void EnsureMeshBounds(UEResourceRef meshRef, UEObject actor, string what)
        {
            if (_meshBounds is not null) return;
            if (_brushMapPath is null)
                throw new InvalidOperationException(
                    $"{what} before the world was loaded "
                    + $"actor={actor?.GetProp(PK.Name)} mesh={meshRef.Name} — call "
                    + "LoadWorldGeometry (or SetBrushSource) first, or set the matching hook");
            // <install>/Maps/<level>.unr -> <install>/System, the layout the
            // shipped game ships in and both frontends already assume.
            string maps = Path.GetDirectoryName(_brushMapPath);
            string system = Path.Combine(Path.GetDirectoryName(maps) ?? "", "System");
            _meshBounds = new UEMeshBounds(system);
        }

        // Bone index + subtree span for a skeletal mesh actor. Resolved from the
        // shipped packages by default, exactly like MeshHashFieldsOf, so a
        // frontend needs no RefSkeleton reader of its own; reconstructing the
        // subtree span would be a guess about serialized data we can simply read.
        (int Index, int NumChildren) BoneLookupOf(UEResourceRef meshRef, UEObject actor, string boneName)
        {
            if (BoneLookupHook is not null) return BoneLookupHook(meshRef, boneName);
            UERefBone[] bones = RefSkeletonOf(meshRef, actor);
            for (int i = 0; i < bones.Length; i++)
                if (bones[i].Name.Equals(boneName, StringComparison.OrdinalIgnoreCase))
                    return (i, bones[i].NumChildren);
            return (-1, 0);
        }

        UERefBone[] RefSkeletonOf(UEResourceRef meshRef, UEObject actor)
        {
            EnsureMeshBounds(meshRef, actor, "skeletal RefSkeleton");
            return _meshBounds.GetRefSkeleton(meshRef.Name, meshRef.ObjectPath);
        }

        // The collision-hash extent fields for a CollideType=3 mesh actor.
        //
        // Core resolves these itself, from the shipped .u packages next to the
        // map it loaded. MeshHashFieldsHook remains an OVERRIDE for a frontend
        // that already has the mesh loaded and would rather not re-read it.
        // Local box of the mesh primitive — GetCollisionBoundingBox(Owner, 0) of its
        // slot +0x60 — for the UBox::LineCheck/PointCheck bodies.
        public (Vector3 Lo, Vector3 Hi) MeshPrimitiveLocalBox(UEObject actor)
        {
            var meshRef = (UEResourceRef)actor.GetProp(PK.Mesh);
            Hp1MeshHashFields m = MeshHashFieldsOf(meshRef, actor);
            EnsureMeshBounds(meshRef, actor, "UBox body: CollideType=3 mesh actor");
            bool skeletal = _meshBounds.IsSkeletal(meshRef.Name, meshRef.ObjectPath);
            object ds = actor.GetProp(PK.DrawScale);
            return CylinderExtent.MeshCollisionLocalBox(m.BoundMin, m.BoundMax, m.Scale, m.Origin,
                ds is null ? 1f : UEInterpreter.AsFloat(ds), skeletal);
        }

        public Hp1MeshHashFields MeshHashFieldsOf(UEResourceRef meshRef, UEObject actor)
        {
            if (MeshHashFieldsHook is not null) return MeshHashFieldsHook(meshRef);
            EnsureMeshBounds(meshRef, actor, "collision hash: CollideType=3 mesh actor");
            return _meshBounds.GetHashFields(meshRef.Name, meshRef.ObjectPath);
        }

        // AActor::SetCollision (Engine.dll.c:30526): remove from the collision
        // hash under the OLD bCollideActors, write the three flags, re-add under
        // the NEW one. Each optional argument defaults to the current value
        // (NActor.cpp:554-560).
        //
        // Core-owned because the ORDER is the contract: the removal has to see
        // the old flag or the actor is never unlinked, and the re-add has to see
        // the new one. A frontend that writes the flags and updates its own
        // colliders would leave Core's hash holding
        // membership that no longer matches the actor.
        public void SetCollision(UEObject self, bool? collideActors,
            bool? blockActors, bool? blockPlayers)
        {
            if (self is null) return;
            HashRemoveActor(self);
            self.Props["bCollideActors"] = collideActors ?? UEInterpreter.ToBool(self.GetProp(PK.bCollideActors));
            self.Props["bBlockActors"] = blockActors ?? UEInterpreter.ToBool(self.GetProp(PK.bBlockActors));
            self.Props["bBlockPlayers"] = blockPlayers ?? UEInterpreter.ToBool(self.GetProp(PK.bBlockPlayers));
            HashAddActor(self);
        }

        // THE THIRD ARGUMENT WRITES CollisionWidth — PER THE DISASM, NOT THE PSEUDOCODE.
        //
        // Ghidra's pseudocode (Engine.dll.c:61914-61935) loses the write: it renders
        // `*(undefined4 **)(this + 0x1d0) = &uStack_4` as an assignment of a local's
        // ADDRESS. The machine code of `AActor::execSetCollisionSize` (0x1040a870, VA from
        // PE export ordinal 2339):
        //
        //   0x1040a8c3  mov ecx,[edi+0x1d0]   3rd parameter buffer := current
        //   0x1040a8ce  mov [esp+0x14],ecx    CollisionWidth (optional default)
        //   0x1040a8e6  call GNatives[b]      optional evaluated INTO this buffer
        //   0x1040a8ec  mov ecx,[esp+0x14]
        //   0x1040a8fc  mov [edi+0x1d0],ecx   this->CollisionWidth = NewWidth
        //   0x1040a906  call 0x103041a1       and ONLY THEN the body (R,H)
        //   0x1040a911  mov [ecx],1           Result = true unconditionally
        //
        // Declaration from the package (`--dumpfunc Actor SetCollisionSize`):
        //   native(283) final function bool SetCollisionSize(
        //       float NewRadius, float NewHeight, optional float NewWidth);
        // Offset 0x1d0 = Actor.CollisionWidth (checked against the shipped class layout).
        //
        // ORDER IS PART OF THE CONTRACT. The width write comes BEFORE the body, i.e. BEFORE
        // the actor is removed from the hash, and HashRemoveActor computes the box from the
        // CURRENT properties. For a plain cylinder (CollideType 0) CollisionWidth is the
        // Z offset of the hash box centre (see HashBoxOf), so moving the write after the
        // removal changes WHICH cells the actor is removed from and leaves dangling
        // references — exactly as in shipped.
        //
        // Live in the campaign: only Harry.uc:1482/1490 pass three arguments (state Mounting
        // BeginState/EndState — the cylinder is halved and the box centre rises by half the
        // height, so the bottom stays put), and both calls belong to the PLAYER.
        //
        // The AActor::SetCollisionSize body (0x10379c80) itself does no fit probe: under the
        // OLD cylinder it removes a bCollideActors actor from FCollisionHash, writes
        // CollisionRadius/CollisionHeight and re-adds it with the new extent.
        public void SetCollisionSize(UEObject self, float radius, float height,
            float? width = null)
        {
            if (self is null) return;
            // The optional default is the CURRENT property value, because the argument
            // buffer is seeded with it (0x1040a8c3). A write without the argument writes
            // the same number, as in shipped.
            self.Props["CollisionWidth"] =
                width ?? UEInterpreter.AsFloat(self.GetProp(PK.CollisionWidth));
            HashRemoveActor(self);
            self.Props["CollisionRadius"] = radius;
            self.Props["CollisionHeight"] = height;
            HashAddActor(self);
        }

        // A brush actor's own UModel (its serialized BSP), parsed once per Model
        // export. Core-owned: it needs nothing but the map path it was loaded
        // from, so no frontend needs to supply it.
        // BrushModelHook remains an override for a frontend that resolves brush
        // geometry some other way.
        public HeadlessBspModel BrushModelOf(UEObject brushActor)
        {
            if (BrushModelHook is not null) return BrushModelHook(brushActor);
            if (brushActor.GetProp(PK.Brush) is not UEModelRef brush)
                throw new NotSupportedException(
                    $"brush model missing actor={brushActor.GetProp(PK.Name)} "
                    + $"class={brushActor.Class?.Name}");
            if (_brushMapPath is null)
                throw new InvalidOperationException(
                    "brush model requested before LoadWorldGeometry — the frontend "
                    + "must load the world geometry before any brush trace");
            if (!_brushModels.TryGetValue(brush.ExportIndex, out HeadlessBspModel model))
                _brushModels[brush.ExportIndex] = model =
                    HeadlessBspModel.LoadModelExport(_brushMapPath, brush.ExportIndex);
            return model;
        }

        // Candidate handle → actor (the encroachment walk resolves the
        // replica's handles back to actors through the owner of the identity).
        // A handle the replica just returned MUST resolve: an unknown one is a
        // maintenance defect, so this throws rather than skipping silently.
        public UEObject HashActor(int handle)
        {
            if (!_hashActor.TryGetValue(handle, out UEObject actor))
                throw new KeyNotFoundException(
                    $"collision hash: handle {handle} has no actor (a hash maintenance point is missing)");
            return actor;
        }

        public int HashLinkCount(UEObject actor)
            => actor is not null && CollisionHash is not null
                && _hashHandle.TryGetValue(actor, out int handle)
                ? CollisionHash.LinkCount(handle) : 0;

        // ULevel::SetActorCollision(1) (Engine.dll.c:161885): create the hash
        // and add every non-null bCollideActors actor IN ACTORS-ARRAY ORDER —
        // the base insertion order every later chain position builds on. Runs
        // at the shipped LoadMap moment: after the level actors are
        // registered, before any lifecycle event or spawn.
        // ATOMIC on purpose. HashAddActor throws for an actor whose dependency
        // is missing — a CollideType-3 mesh with no MeshHashFieldsHook, a brush
        // with no entry in BrushBoundsByExport. Publishing CollisionHash first
        // and populating afterwards left a HALF-BUILT hash on that throw, and
        // because the entry guard returns early when the field is non-null, the
        // retry after fixing the dependency reported success without ever
        // finishing the walk. Every later query then ran against a hash missing
        // whatever came after the failure — silently, which is the worst
        // possible shape for a collision structure.
        //
        // So: build into a local, snapshot the identity maps, and publish only
        // once the whole walk survived. On failure everything goes back exactly
        // as it was and the exception propagates, so a retry is a real retry.
        public void InitCollisionHash()
        {
            if (CollisionHash is not null) return;
            // AActor::PostLoad CT3 -> ValidateActor (see ValidateActorCollision).
            // Shipped runs it at package load, i.e. BEFORE ULevel::SetActorCollision(1);
            // InitCollisionHash is the single Core point that ALL world-load paths (map,
            // transition, save) and every frontend pass through at that moment.
            foreach (UEObject actor in Actors)
                if (actor is not null && !actor.Destroyed)
                    ValidateActorCollision(actor, postLoad: true);
            var handleSnapshot = new Dictionary<UEObject, int>(_hashHandle);
            var actorSnapshot = new Dictionary<int, UEObject>(_hashActor);
            var colLocSnapshot = new Dictionary<int, Vector3>(_hashColLocation);
            int handleCounter = _nextHashHandle;
            CollisionHash = new CollisionHashReplica();
            try
            {
                foreach (UEObject actor in Actors)
                    if (actor is not null && !actor.Destroyed
                        && UEInterpreter.ToBool(actor.GetProp(PK.bCollideActors)))
                        HashAddActor(actor);
            }
            catch
            {
                CollisionHash = null;
                _hashHandle.Clear();
                foreach (KeyValuePair<UEObject, int> e in handleSnapshot) _hashHandle[e.Key] = e.Value;
                _hashActor.Clear();
                foreach (KeyValuePair<int, UEObject> e in actorSnapshot) _hashActor[e.Key] = e.Value;
                _hashColLocation.Clear();
                foreach (KeyValuePair<int, Vector3> e in colLocSnapshot) _hashColLocation[e.Key] = e.Value;
                _nextHashHandle = handleCounter;
                throw;
            }
        }

        // GetActorExtent's box: actor->GetPrimitive() (vtable +0xa4, dispatch
        // on CollideType +0x1d8, Engine.dll.c:113085) ->GetCollisionBoundingBox
        // (vtable +0x60), per shipped override:
        //   CT 3 + Mesh  -> UMesh (33473): scaled mesh bound through the mesh
        //                   RotOrigin/actor Rotation coords.
        //   CT 3 + Brush -> UModel/"UBoxPrim" (14613): the serialized model FBox
        //                   corners through ABrush::ToWorld (validate_brush L1).
        //   CT 1         -> UOrientedCylinder (body 0x103fb190): local box
        //                   ±(R, R, H) through the base AActor::ToWorld coords
        //                   (full-rotation, no PrePivot; body 0x1031be20).
        //   CT 2         -> UBox (body 0x103fe130): local box ±(R, W, H), the Y
        //                   half-extent = CollisionWidth (+0x1d0) or R when zero,
        //                   through the same base ToWorld coords.
        //   else (CT 0)  -> UPrimitive (92136): the axis-aligned cylinder AABB;
        //                   +0x1d0 (CollisionWidth) acts as the Z-center offset.
        public (Vector3 Min, Vector3 Max) HashBoxOf(UEObject actor)
        {
            Vector3 loc = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
            float r = UEInterpreter.AsFloat(actor.GetProp(PK.CollisionRadius));
            float h = UEInterpreter.AsFloat(actor.GetProp(PK.CollisionHeight));
            float w = UEInterpreter.AsFloat(actor.GetProp(PK.CollisionWidth));
            UERotator rot = UEInterpreter.AsRotator(actor.GetProp(PK.Rotation));
            switch (UEPrimitive.Resolve(actor))
            {
                case UEPrimitiveKind.Mesh when actor.GetProp(PK.Mesh) is UEResourceRef meshRef:
                    return MeshPrimitiveBox(actor, meshRef);
                case UEPrimitiveKind.Brush:
                    return BrushPrimitiveBox(actor);
                case UEPrimitiveKind.OrientedCylinder:
                    return HashBoxes.OrientedLocalBox(r, r, h, loc, rot.Pitch, rot.Yaw, rot.Roll);
                case UEPrimitiveKind.Box:
                    return HashBoxes.OrientedLocalBox(r, w != 0f ? w : r, h, loc,
                        rot.Pitch, rot.Yaw, rot.Roll);
                default:
                    return HashBoxes.Cylinder(loc, r, h, w);
            }
        }

        // AActor::GetCylinderExtent (body 0x1037a900; analysis — CylinderExtent in
        // UEBspCollision.cs). Every shipped site that calls it takes the extent FROM HERE
        // rather than building (R, R, H) itself: for CollideType 2/3 these differ.
        public Vector3 GetCylinderExtent(UEObject actor)
            => CylinderExtentOf(actor.GetProp, actor, actor.GetProp(PK.Name));

        // The same answer for a class TEMPLATE (ULevel::SpawnActor calls
        // GetCylinderExtent(param_12) — the class default object, 0x103a67d8) —
        // without creating an instance: a spawn refusal must not consume an ObjectId.
        public Vector3 GetCylinderExtentOfTemplate(UClass cls)
            => CylinderExtentOf(n => n == "Mesh" || n == "Brush"
                    ? UEObject.ClassDefaultResource(cls, n)
                    : UEObject.ClassDefaultScalar(cls, n), null, $"default {cls?.Name}");

        Vector3 CylinderExtentOf(Func<string, object> get, UEObject actor, object who)
        {
            float r = UEInterpreter.AsFloat(get("CollisionRadius"));
            float h = UEInterpreter.AsFloat(get("CollisionHeight"));
            int collideType = UEInterpreter.ToInt(get("CollideType"));
            if (collideType == 3)
            {
                if (get("Mesh") is UEResourceRef meshRef)
                {
                    // UMesh::GetCollisionExtent 0x103b8820.
                    Hp1MeshHashFields m = MeshHashFieldsOf(meshRef, actor);
                    (Vector3 min, Vector3 max) = CylinderExtent.MeshLocalBox(m.BoundMin, m.BoundMax,
                        m.Scale, m.Origin, UEInterpreter.AsFloat(get("DrawScale")));
                    return CylinderExtent.FromLocalBox(min, max);
                }
                object brush = get("Brush");
                if (brush is not null)
                {
                    // "UBoxPrim"(UModel)::GetCollisionExtent 0x103fea90: the model's raw
                    // BoundingBox. A brush whose box the port does not know (not from the map)
                    // is a loud refusal, not a cylinder.
                    if (brush is not UEModelRef model
                        || !BrushBoundsByExport.TryGetValue(model.ExportIndex, out BrushBounds b))
                        throw new NotSupportedException(
                            $"GetCylinderExtent: CollideType 3 brush without model bounds actor={who}");
                    return CylinderExtent.FromLocalBox(b.BoxMin, b.BoxMax);
                }
            }
            if (collideType == 2)
            {
                float w = UEInterpreter.AsFloat(get("CollisionWidth"));
                return new Vector3(r, w != 0f ? w : r, h);
            }
            return new Vector3(r, r, h);
        }

        // AActor::PostLoad (body 0x10379910; Engine.dll.c:34407-34411) and
        // AActor::Spawned (body 0x10379a30; 96652-96655) with CollideType 3 call
        // GetPrimitive()->ValidateActor(this) (primitive vtable +0x6c). PostLoad
        // additionally requires Brush or Mesh; Spawned does not (for the cylinder
        // UPrimitive::ValidateActor 0x103fa260 is a bare `ret 4`).
        // For UMesh/ULodMesh/USkeletalMesh/UModel slot +0x6c is "UBoxPrim"::
        // ValidateActor 0x103feb50 (Engine.dll.c:128891-128910):
        //     e = this->GetCollisionExtent(Owner)   (+0x64 — the same extent
        //                                            as GetCylinderExtent)
        //     W = R = (e.y <= e.x ? e.x : e.y);  H = e.z.
        // So a CT3 actor with a mesh/brush has its cylinder REWRITTEN from the primitive box
        // at load and at spawn. Campaign data confirms it independently: for 1416 of 1420
        // placed CT3 actors on 41 maps the editor-saved R/W/H equal this formula bit for bit
        // (--extentcensus).
        public void ValidateActorCollision(UEObject actor, bool postLoad)
        {
            if (UEInterpreter.ToInt(actor.GetProp(PK.CollideType)) != 3) return;
            if (postLoad && actor.GetProp(PK.Brush) is null && actor.GetProp(PK.Mesh) is null) return;
            Vector3 e;
            switch (UEPrimitive.Resolve(actor))
            {
                case UEPrimitiveKind.Mesh when actor.GetProp(PK.Mesh) is UEResourceRef meshRef:
                {
                    Hp1MeshHashFields m = MeshHashFieldsOf(meshRef, actor);
                    (Vector3 min, Vector3 max) = CylinderExtent.MeshLocalBox(m.BoundMin, m.BoundMax,
                        m.Scale, m.Origin, UEInterpreter.AsFloat(actor.GetProp(PK.DrawScale)));
                    e = CylinderExtent.FromLocalBox(min, max);
                    break;
                }
                case UEPrimitiveKind.Brush:
                {
                    BrushBounds b = BrushBoundsOf(actor);
                    e = CylinderExtent.FromLocalBox(b.BoxMin, b.BoxMax);
                    break;
                }
                default:
                    return;                          // UPrimitive::ValidateActor — ret 4
            }
            float rw = e.y <= e.x ? e.x : e.y;
            actor.Props["CollisionWidth"] = rw;
            actor.Props["CollisionRadius"] = rw;
            actor.Props["CollisionHeight"] = e.z;
        }

        // Both visual formulas are separate because they have TWO callers: HashBoxOf
        // (primitive by CollideType) and WorldCollisionBox (bVisual takes Mesh/Brush
        // regardless of CollideType). Two copies would drift silently.
        (Vector3 Min, Vector3 Max) MeshPrimitiveBox(UEObject actor, UEResourceRef meshRef)
        {
            Hp1MeshHashFields m = MeshHashFieldsOf(meshRef, actor);
            UERotator rot = UEInterpreter.AsRotator(actor.GetProp(PK.Rotation));
            return HashBoxes.Mesh(m.BoundMin, m.BoundMax, m.Scale, m.Origin,
                m.RotPitch, m.RotYaw, m.RotRoll,
                UEInterpreter.AsFloat(actor.GetProp(PK.DrawScale)),
                UEInterpreter.AsVector3(actor.GetProp(PK.Location)),
                UEInterpreter.AsVector3(actor.GetProp(PK.PrePivot)),
                rot.Pitch, rot.Yaw, rot.Roll);
        }

        (Vector3 Min, Vector3 Max) BrushPrimitiveBox(UEObject actor)
        {
            BrushBounds b = BrushBoundsOf(actor);
            return HashBoxes.TransformedBox(b.BoxMin, b.BoxMax, BrushToWorldCoords(actor));
        }

        // AActor::execGetWorldCollisionBox (Engine.dll.c:134732) — native 286.
        //
        // THE BRANCHING IS SHIPPED'S:
        //     if (bVisual == 0)  primitive = this->vtable[0xa4]()      // :134750
        //     else {             primitive = this[0x164];              // Mesh
        //           if (!primitive) primitive = this[0x168];           // Brush
        //           if (!primitive) primitive = this->vtable[0xa4](); }
        //     result = primitive->vtable[0x60](&ret, this, 1);
        // Offsets 0x164/0x168 checked against the shipped layout (--dumpprops Actor):
        // exactly Actor.Mesh and Actor.Brush. Virtual slot 0xa4 picks the primitive by
        // CollideType, i.e. HashBoxOf; slot 0x60 is GetCollisionBoundingBox, already
        // accounted for inside its formulas.
        //
        // KEY DIFFERENCE FROM HashBoxOf: with bVisual mesh and brush are taken REGARDLESS
        // of CollideType. An actor with CollideType=0 and a mesh gives the mesh box here and
        // a cylinder in HashBoxOf — different questions, not a contradiction.
        //
        // OWNER IS CORE, because the answer must follow from the actor's DATA (the authored
        // mesh box via RotOrigin and Rotation), not from scene state such as a deformed
        // render mesh, which would drift with animation.
        public (Vector3 Min, Vector3 Max) WorldCollisionBox(UEObject actor, bool bVisual)
        {
            if (actor is null) return (default, default);
            if (bVisual)
            {
                if (actor.GetProp(PK.Mesh) is UEResourceRef meshRef)
                    return MeshPrimitiveBox(actor, meshRef);
                if (actor.GetProp(PK.Brush) is UEModelRef)
                    return BrushPrimitiveBox(actor);
            }
            return HashBoxOf(actor);
        }

        // FCollisionHash::AddActor (Engine.dll.c:49171): skip bDeleteMe, link
        // the extent cells, remember ColLocation (+0x120) for the un-rehashed-
        // move warning RemoveActor prints.
        public void HashAddActor(UEObject actor)
        {
            // FCollisionHash::AddActor: the body is under `if (!bDeleteMe)` (disasm
            // 0x10364b9f-0x10364ba5) — MoveActor/FarMove of the carcass do NOT return it to the hash.
            if (CollisionHash is null || actor is null || actor.Destroyed) return;
            if (!UEInterpreter.ToBool(actor.GetProp(PK.bCollideActors))) return;
            if (!_hashHandle.TryGetValue(actor, out int handle))
            {
                handle = ++_nextHashHandle;
                _hashHandle[actor] = handle;
                _hashActor[handle] = actor;
            }
            (Vector3 min, Vector3 max) = HashBoxOf(actor);
            CollisionHash.Add(handle, min, max);
            _hashColLocation[handle] = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
        }

        // FCollisionHash::RemoveActor(actor, false) (Engine.dll.c:7832): unlink
        // from the extent cells at the CURRENT location; a location that moved
        // since AddActor is the shipped "moved without proper hashing" warning
        // (stale links would survive — that would be a maintenance defect in
        // our wiring, so it is loud here).
        public void HashRemoveActor(UEObject actor)
        {
            // FCollisionHash::RemoveActor: the whole body is under `if (!bDeleteMe)`
            // (disasm 0x10364e8d-0x10364e93: `test ah,2` on +0x28 -> exit) — a corpse gets neither
            // removal, nor the warning "moved without proper hashing".
            if (CollisionHash is null || actor is null || actor.Destroyed) return;
            if (!UEInterpreter.ToBool(actor.GetProp(PK.bCollideActors))) return;
            if (!_hashHandle.TryGetValue(actor, out int handle)) return;
            Vector3 loc = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
            if (_hashColLocation.TryGetValue(handle, out Vector3 colLoc) && loc != colLoc)
                Log($"collision_hash_warn moved_without_proper_hashing "
                    + $"actor={actor.GetProp(PK.Name)} loc={loc} colLoc={colLoc}");
            (Vector3 min, Vector3 max) = HashBoxOf(actor);
            CollisionHash.Remove(handle, min, max);
        }

        // Shipped CheckActorNotReferenced is EMPTY in the shipped build (a bare ret,
        // Engine.dll.c:27503-27509), so it asserts no invariant; the removal itself
        // sweeps rather than asserting — see below.
        //
        // ULevel::DestroyActor's hash removal (0x103a75ca): gated on
        // bCollideActors, and it passes bSweepAll=TRUE (0x103a75d5 `push 1`) —
        // the all-buckets variant, NOT the extent walk MoveActor/FarMove use.
        //
        // Established by disassembling the shipped binary, because the Ghidra
        // output renders this call with NO arguments at all (Engine.dll.c:252935)
        // — the flag is invisible in the decompile. Argument order was settled
        // from the CALLEE's frame (RemoveActor 0x10364e40 dereferences [ebp+8]
        // as AActor* and tests [ebp+0xc] as the bool), not from convention.
        //
        // Consequence, and why this does not throw: shipped CANNOT leave a
        // stale link here — the sweep unlinks the actor wherever it is hashed,
        // including links an un-rehashed move left behind, and it does so
        // silently; an extent-remove + throw would abort where shipped recovers. (The "moved without proper
        // hashing" warning still belongs on the extent path, which shipped logs
        // BEFORE branching on the flag; that path is unchanged.)
        public void HashRemoveForDestroy(UEObject actor)
        {
            if (CollisionHash is null || actor is null) return;
            if (!UEInterpreter.ToBool(actor.GetProp(PK.bCollideActors))) return;
            if (!_hashHandle.TryGetValue(actor, out int handle)) return;
            // Shipped emits this BEFORE branching on bSweepAll, so the destroy
            // path warns exactly like the extent path does.
            Vector3 loc = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
            if (_hashColLocation.TryGetValue(handle, out Vector3 colLoc) && loc != colLoc)
                Log($"collision_hash_warn moved_without_proper_hashing "
                    + $"actor={actor.GetProp(PK.Name)} loc={loc} colLoc={colLoc}");
            CollisionHash.RemoveAll(handle);
        }
    }
}
