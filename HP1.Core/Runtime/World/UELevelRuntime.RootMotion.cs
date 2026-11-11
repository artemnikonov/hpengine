// Animation root motion (bAnimMove) — Core-owned.
//
// The WHOLE DECISION lives here: how much to move, where the blocked remainder goes,
// how to return it later. Nothing is left to the frontend: both the root track and the
// mesh coordinate system are read by Core from the package. Ledge climbing
// (climb32/64/96) goes through this channel.
//
// Source: shipped USkeletalMesh::GetRootMovement (Engine.dll.c:48348-48429),
// the ApplyAnim root block (142567-142858, disasm 0x1041cabf-0x1041d408),
// USkeletalMesh::AdjustRootMovement (96222-96257), GetMeshCoords (157028-157162)
// and the AActor::Tick tail (128808-128855). State — skeleton cache header fields
// (UELevelRuntime.SkeletalPose.cs, SkelCache). Proof — execution of the
// real bodies under unicorn (se_harness/bsp_diff/skelanim_oracle.py, fixture
// skelanim_matches_shipped_oracle).
using System;
using System.Collections.Generic;
using System.IO;
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        // Core resolves the pair (actor mesh, its AnimSequence) itself, from its own
        // catalog, independent of what the frontend shows (a frontend player switches
        // later, on its visual Update, so in the tick where the script called
        // PlayAnim('climb32') it would still sample 'breath').
        //
        // Mesh coordinate fields are read by Core itself with the same package reader that
        // supplies the collision-hash extent fields
        // (UEPackageReader.ReadMeshHashFields: Scale +0x100, RotOrigin +0x118).

        // ROOT MOTION STATE — SKELETON CACHE HEADER FIELDS
        // (SkelCache, UELevelRuntime.SkeletalPose.cs: +0x6c..+0x94), maintained only by
        // ApplyAnim (shipped PlayAnim does not touch the cache).
        public int RootMotionStateCount => _skelCache.Count;

        // AActor::Tick, tail (Engine.dll.c:128808-128855). FCheckResult Hit
        // is set up ONCE before the first move: Time = 1.0, Normal = 0
        // (128812-128823). ULevel::MoveActor on early exits (bStatic/
        // !bMovable refusal, tiny move) does not write Hit (123784-123819), and the
        // move fraction stays 1.0 (not the previous move's). The second move reuses
        // the same Hit without a reset.
        public void TickRootMovement(UEInterpreter interp, UEObject actor)
        {
            Vector3 delta = GetRootMovement(actor);
            if (delta == Vector3.zero) return;

            UERotator rotation = UEInterpreter.AsRotator(actor.GetProp(PK.Rotation));
            var hit = new MoveCheck();
            MoveActor(interp, actor, delta, rotation,
                bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false, hit);
            float firstTime = hit.Time;
            Vector3 residual = delta * (1f - firstTime);
            if (firstTime < 1f)
            {
                Vector3 normal = hit.Normal;
                Vector3 slide = residual - normal * Vector3.Dot(residual, normal);
                MoveActor(interp, actor, slide, rotation,
                    bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false, hit);
                float secondTime = hit.Time;
                residual -= slide * secondTime;
            }
            AdjustRootMovement(actor, residual);
            Log($"root_move actor={actor.GetProp(PK.Name)} delta={delta} residual={residual}");
        }

        // USkeletalMesh::GetRootMovement (Engine.dll.c:48348-48429): ApplyAnim
        // root only, then the accumulated delta (+0x7c) is transformed FROM the header
        // axes (+0x20) to world — X·d.x + Y·d.y + Z·d.z — and cleared.
        internal Vector3 GetRootMovement(UEObject actor)
        {
            if (MeshCatalog is null)
                throw new InvalidOperationException(
                    $"bAnimMove without a mesh catalog actor={actor.GetProp(PK.Name)}");
            float frame = UEInterpreter.AsFloat(actor.GetProp(PK.AnimFrame));
            // Shipped has NO negative-AnimFrame tween phase (AActor::PlayAnim
            // starts at 0 and TweenAlpha ramps separately; Engine.dll.c:73780,
            // disasm 0x103b3a37). A negative frame here means the anim state
            // machine broke its invariant, not a data condition.
            if (frame < 0f)
                throw new InvalidOperationException(
                    $"negative AnimFrame={frame:R} reached root motion "
                    + $"actor={actor.GetProp(PK.Name)} seq={actor.GetProp(PK.AnimSequence)} — the shipped "
                    + "TweenAlpha model never goes below 0");
            // UMesh::GetRootMovement (Engine.dll.c:130292-130297): for a non-skeletal
            // mesh — zero, and the Tick tail does not move the actor (C3).
            if (actor.GetProp(PK.Mesh) is not UEResourceRef meshRef
                || !meshRef.Class.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
                return Vector3.zero;
            SkelCache cache = ApplyAnim(actor, rootOnly: true);
            Vector3 d = cache.RootDelta;
            cache.RootDelta = Vector3.zero;
            return cache.MeshCoords.UntransformVector(d);
        }

        // USkeletalMesh::AdjustRootMovement (Engine.dll.c:96222-96257): a non-zero
        // remainder is projected SCALARLY onto the header axes (+0x2c/+0x38/+0x44) and
        // accumulated in +0x88. No cache — nothing.
        internal void AdjustRootMovement(UEObject actor, Vector3 residual)
        {
            if (residual == Vector3.zero) return;
            if (!_skelCache.TryGetValue(actor, out SkelCache cache)) return;
            Vector3 p = cache.MeshCoords.TransformVector(residual);
            cache.Adjust = new Vector3(cache.Adjust.x + p.x, cache.Adjust.y + p.y, cache.Adjust.z + p.z);
        }

        // DIAGNOSTIC ACCESS to the same body. Needed by probes that compare the
        // actor's final basis with shipped GetMeshCoords: without it a probe would keep
        // a SECOND copy of the rule, and two owners of one behaviour are forbidden (C7).
        // Computes nothing itself — only returns MeshCoordsOf.
        public BrushCoords ProbeMeshCoords(UEObject actor) => MeshCoordsOf(actor);

        // USkeletalMesh::GetMeshCoords (Engine.dll.c:157028-157162, disasm
        // 0x1041aef0-0x1041b20a), whole — axes AND origin:
        //   S = DrawScale·mesh Scale, S.x ·= Wideness/128, S.y = −S.y·Wideness/128
        //       (byte +0x18f is Wideness per the offset oracle; Fatness is +0x18e —
        //       they differ in data: Branch01 Lev2_Inc_A has Fatness 87–97
        //       with Wideness 128, HagridTable Lev2_fire1 the reverse);
        //   C = FCoords(−Origin⊙S, (S.x,0,0), (0,S.y,0), (0,0,S.z));
        //   C /= mesh RotOrigin; C.Origin += PrePivot + (0,0,bAlignBottom shift);
        //   C /= actor Rotation (rotates the origin too); C.Origin += Location.
        BrushCoords MeshCoordsOf(UEObject actor)
        {
            if (actor.GetProp(PK.Mesh) is not UEResourceRef meshRef)
                throw new InvalidOperationException(
                    $"mesh coords on an actor without a Mesh actor={actor.GetProp(PK.Name)}");
            EnsureMeshBounds(meshRef, actor, "mesh coords");
            Hp1MeshHashFields mesh = MeshHashFieldsHook is not null
                ? MeshHashFieldsHook(meshRef)
                : _meshBounds.GetHashFields(meshRef.Name, meshRef.ObjectPath);
            float drawScale = UEInterpreter.AsFloat(actor.GetProp(PK.DrawScale));
            float wideness = UEInterpreter.ToInt(actor.GetProp(PK.Wideness)) * 0.0078125f;
            float sx = drawScale * mesh.Scale.x * wideness;
            float sy = -(drawScale * mesh.Scale.y * wideness);
            float sz = drawScale * mesh.Scale.z;
            BrushCoords coords = new BrushCoords
            {
                Origin = new Vector3(-mesh.Origin.x * sx, -mesh.Origin.y * sy, -mesh.Origin.z * sz),
                X = new Vector3(sx, 0f, 0f),
                Y = new Vector3(0f, sy, 0f),
                Z = new Vector3(0f, 0f, sz),
            };
            coords.DivRotator(mesh.RotPitch, mesh.RotYaw, mesh.RotRoll);
            Vector3 prePivot = UEInterpreter.AsVector3(actor.GetProp(PK.PrePivot));
            // GetMeshCoords — the USkeletalMesh body: the mesh here is always skeletal.
            float shiftZ = AlignBottomActive(actor) ? SkeletalAlignShiftZ(actor, mesh) : 0f;
            coords.Origin = new Vector3(coords.Origin.x + prePivot.x, coords.Origin.y + prePivot.y,
                coords.Origin.z + (shiftZ + prePivot.z));
            UERotator actorRotation = UEInterpreter.AsRotator(actor.GetProp(PK.Rotation));
            coords.DivRotator(actorRotation.Pitch, actorRotation.Yaw, actorRotation.Roll);
            Vector3 loc = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
            coords.Origin = new Vector3(coords.Origin.x + loc.x, coords.Origin.y + loc.y, coords.Origin.z + loc.z);
            return coords;
        }

        // ── SKELETAL MESH VERTICAL: bAlignBottom ─────────────────────────────
        //
        // USkeletalMesh::GetMeshCoords (Engine.dll.c:157095-157106; clone copy
        // FUN_1041aef0:317600-317618 — same offsets, same constant). Before the
        // actor rotation shipped adds to the mesh translation, TOGETHER with
        // PrePivot, the vector (0, 0, shift):
        //
        //   shift = (Origin.z − Bound.Min.z)·Scale.z·DrawScale − (CollisionHeight + 2.5)
        //
        // and only when bAlignBottom && bCollideWorld && Physics != PHYS_None &&
        // CollideType != 3 (word 0x1dc, actor bytes 0x30/0x1d8; decoded via
        // prop_offsets). Otherwise the term is zero. Bound is the UPrimitive.BoundingBox
        // of the mesh ITSELF (this+0x30), Origin/Scale are UMesh intrinsic fields.
        //
        // SKELETAL PATH ONLY: both bodies found are USkeletalMesh methods; in
        // ULodMesh::GetFrame (Engine.dll.c:57172) neither CollisionHeight nor 2.5 is
        // read. This is a positive reading of two bodies, not a proven "nowhere
        // else" (V11) — so the census (--probe-standing) prints a vertex mesh that
        // passes the condition as a loud line instead of silently aligning it.
        public float MeshAlignShiftZOf(UEObject actor)
        {
            if (actor?.GetProp(PK.Mesh) is not UEResourceRef meshRef) return 0f;
            if (!AlignBottomActive(actor)) return 0f;
            EnsureMeshBounds(meshRef, actor, "bAlignBottom mesh vertical");
            if (!_meshBounds.IsSkeletal(meshRef.Name, meshRef.ObjectPath)) return 0f;
            Hp1MeshHashFields mesh = MeshHashFieldsHook is not null
                ? MeshHashFieldsHook(meshRef)
                : _meshBounds.GetHashFields(meshRef.Name, meshRef.ObjectPath);
            return SkeletalAlignShiftZ(actor, mesh);
        }

        static bool AlignBottomActive(UEObject actor)
        {
            if (!UEInterpreter.ToBool(actor.GetProp(PK.bAlignBottom))
                || !UEInterpreter.ToBool(actor.GetProp(PK.bCollideWorld))) return false;
            if (UEInterpreter.ToInt(actor.GetProp(PK.Physics)) == 0) return false;
            if (UEInterpreter.ToInt(actor.GetProp(PK.CollideType)) == 3) return false;
            return true;
        }

        // The shift body is ONE for both entries (skeletal GetMeshCoords and
        // frontend frame alignment).
        static float SkeletalAlignShiftZ(UEObject actor, Hp1MeshHashFields mesh)
        {
            // DrawScale: a field the actor does not set takes the engine default 1.0
            // (the same projection as in WorldFrameProjector.Project).
            float drawScale = actor.GetProp(PK.DrawScale) is { } ds
                ? UEInterpreter.AsFloat(ds) : 1f;
            float height = UEInterpreter.AsFloat(actor.GetProp(PK.CollisionHeight));
            return (mesh.Origin.z - mesh.BoundMin.z) * mesh.Scale.z * drawScale
                   - (height + 2.5f);
        }

        // Diagnostic wrappers of the SAME data body (C7: a probe does not keep
        // a second copy of the rule) — read by --probe-standing.
        public bool ProbeMeshIsSkeletal(UEResourceRef meshRef, UEObject actor)
        {
            EnsureMeshBounds(meshRef, actor, "standing probe: mesh kind");
            return _meshBounds.IsSkeletal(meshRef.Name, meshRef.ObjectPath);
        }

        public Hp1MeshHashFields ProbeMeshHashFields(UEResourceRef meshRef, UEObject actor)
        {
            EnsureMeshBounds(meshRef, actor, "standing probe: hash fields");
            return _meshBounds.GetHashFields(meshRef.Name, meshRef.ObjectPath);
        }
    }
}
