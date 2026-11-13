// Moving actors: MoveActor, FarMove, MoveSmooth, encroachment (ActorEncroachmentOverlaps,
// CheckEncroachment), brush coords, actor traces, touch/untouch, MoveCheck/MoveHit.
// Fields live in UELevelRuntime.cs.
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
        // FCollisionHash::ActorEncroachmentCheck (Engine.dll.c:12460): the
        // overlap set at the test pose. Swaps the actor's Location AND
        // Rotation to the test values, walks the collision-hash cells of the
        // actor's extent box in the shipped order (CollisionHashReplica:
        // x/y/z nested cells, chain order, first-visit CollisionTag dedup),
        // filters non-static brush candidates, and tests the rest with the
        // moving actor's OWN primitive PointCheck — the brush UModel
        // (validated against the original) or the shipped
        // cylinder point check (UPrimitive::PointCheck, Engine.dll.c:24173) —
        // then restores the pose. The returned order IS the shipped candidate
        // order.
        public List<UEObject> ActorEncroachmentOverlaps(UEObject actor,
            Vector3 testLoc, UERotator testRot)
        {
            if (CollisionHash is null)
                throw new InvalidOperationException("encroachment before InitCollisionHash");
            var result = new List<UEObject>();
            Vector3 oldLoc = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
            UERotator oldRot = UEInterpreter.AsRotator(actor.GetProp(PK.Rotation));
            actor.Props["Location"] = testLoc;
            actor.Props["Rotation"] = testRot;
            try
            {
                // Shipped tests each candidate with the MOVING actor's own
                // primitive: GetPrimitive (vtable +0xa4) -> PointCheck (+0x58,
                // 12546-12550). Dispatch through the one resolver, not through a
                // Brush-pointer test — that ignored Mesh priority at CollideType
                // 3, ABrush's fallback when Brush is NULL, and both UBox and
                // UOrientedCylinder.
                UEPrimitiveKind actorPrim = UEPrimitive.Resolve(actor);
                bool actorBrush = actorPrim == UEPrimitiveKind.Brush;
                HeadlessBspModel brushModel = actorBrush ? BrushModelOf(actor) : null;
                BrushCoords? coords = actorBrush ? BrushToWorldCoords(actor) : null;
                float r = UEInterpreter.AsFloat(actor.GetProp(PK.CollisionRadius));
                float h = UEInterpreter.AsFloat(actor.GetProp(PK.CollisionHeight));
                // THE MOVING ACTOR'S WIDTH, NOT THE CANDIDATE'S — this follows from the call
                // roles, which are inverted relative to intuition.
                // Disasm of the call site (10365a88):
                //   GetCylinderExtent(candidate)      -> puVar9   (the PROBE EXTENT)
                //   param_2->GetPrimitive() (+0xa4)   -> primitive of the MOVING actor
                //   primitive->PointCheck(+0x58)( Result,
                //       param_2,                       Owner = MOVING ACTOR
                //       candidate+0xfc/0x100/0x104,    probe point = CANDIDATE Location
                //       puVar9[0..2], 0 )              probe extent = candidate extent
                // So Owner (whose 0x1cc/0x1d0/0x1d4 and Location the body reads) is the moving
                // actor, and the candidate comes in as a POINT. The same value is needed by both
                // branches below (cylinder and box), so it is read once: two copies would drift
                // silently.
                float w = UEInterpreter.AsFloat(actor.GetProp(PK.CollisionWidth));
                (Vector3 qmin, Vector3 qmax) = HashBoxOf(actor);
                foreach (int handle in CollisionHash.EncroachmentCandidates(qmin, qmax))
                {
                    UEObject candidate = HashActor(handle);
                    if (candidate is null || candidate.Destroyed || ReferenceEquals(candidate, actor)) continue;
                    if (!UEInterpreter.ToBool(candidate.GetProp(PK.bCollideActors))) continue;
                    // Non-static brush candidates (other movers) are skipped by
                    // the shipped hash walk (12581-12590): Brush != NULL AND a
                    // class-chain walk to ABrush AND !bStatic. All three are
                    // required — a Brush model on a NON-ABrush class exits that
                    // chain walk normally and falls through to the candidate
                    // processing at LAB_10365a88, so testing the Brush pointer
                    // alone would silently drop a real overlap.
                    if (UEBlocking.IsBrush(candidate)
                        && !UEInterpreter.ToBool(candidate.GetProp(PK.bStatic))) continue;
                    Vector3 cloc = UEInterpreter.AsVector3(candidate.GetProp(PK.Location));
                    // Probe extent — AActor::GetCylinderExtent(candidate)
                    // (Engine.dll.c:12591, call 0x10365a99), not (R, R, H).
                    Vector3 cext = GetCylinderExtent(candidate);
                    bool hit;
                    if (actorBrush)
                    {
                        hit = brushModel.PointCheck(cloc, cext, coords).Hit;
                    }
                    else if (actorPrim == UEPrimitiveKind.Cylinder)
                    {
                        // UPrimitive::PointCheck: z slab then XY circle, both with
                        // the <= clear edges (Engine.dll.c:24204-24210).
                        //
                        // THE Z SLAB IS MEASURED FROM THE SHIFTED CENTRE. Disasm (24204), with its
                        // operand order:
                        //   fVar1 = (Owner+0x1d0 + Owner+0x104) - param_6
                        // Owner is the moving actor (role analysis above), 0x1d0 its
                        // CollisionWidth, 0x104 Location.Z, param_6 the probe point Z. So for a
                        // PLAIN CYLINDER the width shifts the volume itself, not only the hash box:
                        // the same `0x1d0 + 0x104` appears in UPrimitive::LineCheck (35190) and
                        // UPrimitive::GetCollisionBoundingBox (92160).
                        //
                        // THIS IS A LIVE PATH: the only campaign actor with a non-zero width is the
                        // player in state Mounting (Harry.uc:1482), and CheckEncroachment is called
                        // exactly on its moves.
                        float dz = (w + testLoc.z) - cloc.z;
                        float hz = cext.z + h;
                        float dx = testLoc.x - cloc.x, dy = testLoc.y - cloc.y;
                        float rr = cext.x + r;
                        hit = hz * hz > dz * dz && rr * rr > dx * dx + dy * dy;
                    }
                    else if (actorPrim == UEPrimitiveKind.Box || actorPrim == UEPrimitiveKind.Mesh)
                    {
                        // UBox::PointCheck (body 0x103FE590), validated against
                        // the real shipped body under unicorn.
                        //
                        // MESH IS THE SAME BOX, BY VTABLE: slots +0x58/+0x5c of the vtables of
                        // UMesh (0x10471ebc), ULodMesh and USkeletalMesh (0x1047b43c) point to the
                        // bodies UBox::PointCheck 0x103fe590 and UBox::LineCheck 0x103fe620; the mesh
                        // has only its own GetCollisionBoundingBox/GetCollisionExtent (+0x60/+0x64).
                        // Verified against the original DLL under unicorn.
                        if (actorPrim == UEPrimitiveKind.Mesh)
                        {
                            (Vector3 lo, Vector3 hi) = MeshPrimitiveLocalBox(actor);
                            hit = ActorBoxTrace.PointCheck(cloc, cext, testLoc, lo, hi, testRot).Hit;
                        }
                        else
                            hit = ActorBoxTrace.PointCheck(cloc, cext, testLoc, r, w, h,
                                testRot).Hit;
                    }
                    else
                    {
                        // UOrientedCylinder::PointCheck (body 0x103FB7A0),
                        // validated against the real shipped body under
                        // unicorn. Only the hit decision is
                        // claimed — which is all this call site consumes.
                        hit = ActorOrientedCylinder.PointCheckHit(
                            cloc, cext, testLoc, r, w, h, testRot);
                    }
                    if (hit) result.Add(candidate);
                }
            }
            finally
            {
                actor.Props["Location"] = oldLoc;
                actor.Props["Rotation"] = oldRot;
            }
            return result;
        }

        // ── Brush actor transform ───────────────────────────────────────────
        // ABrush::ToWorld/ToLocal from the actor's serialized transform fields
        // (validated against the real Engine.dll ToWorld on all Tut1 movers
        // plus synthetic rotations). Pure property math on Core types, kept with the
        // collision world it serves.
        public BrushCoords BrushToWorldCoords(UEObject brushActor) => BrushToWorld(brushActor);

        public static BrushCoords BrushToWorld(UEObject brushActor)
        {
            Vector3 location = UEInterpreter.AsVector3(brushActor.GetProp(PK.Location));
            UERotator rot = UEInterpreter.AsRotator(brushActor.GetProp(PK.Rotation));
            Vector3 pivot = UEInterpreter.AsVector3(brushActor.GetProp(PK.PrePivot));
            return BrushCoords.ToWorld(location, rot.Pitch, rot.Yaw, rot.Roll, pivot,
                BrushScaleOf(brushActor, "MainScale"), BrushSheerOf(brushActor, "MainScale"),
                BrushScaleOf(brushActor, "PostScale"), BrushSheerOf(brushActor, "PostScale"));
        }

        public BrushCoords BrushToLocalCoords(UEObject brushActor)
        {
            Vector3 location = UEInterpreter.AsVector3(brushActor.GetProp(PK.Location));
            UERotator rot = UEInterpreter.AsRotator(brushActor.GetProp(PK.Rotation));
            Vector3 pivot = UEInterpreter.AsVector3(brushActor.GetProp(PK.PrePivot));
            return BrushCoords.ToLocal(location, rot.Pitch, rot.Yaw, rot.Roll, pivot,
                BrushScaleOf(brushActor, "MainScale"), BrushSheerOf(brushActor, "MainScale"),
                BrushScaleOf(brushActor, "PostScale"), BrushSheerOf(brushActor, "PostScale"));
        }

        public BrushBounds BrushBoundsOf(UEObject brushActor)
        {
            if (brushActor.GetProp(PK.Brush) is not UEModelRef brush
                || !BrushBoundsByExport.TryGetValue(brush.ExportIndex, out BrushBounds b))
                throw new NotSupportedException(
                    $"headless brush bounds missing actor={brushActor.GetProp(PK.Name)} class={brushActor.Class?.Name}");
            return b;
        }

        static Vector3 BrushScaleOf(UEObject brushActor, string prop)
        {
            if (brushActor.GetProp(prop) is not UEObject sc) return Vector3.one;
            Vector3 v = UEInterpreter.AsVector3(sc.GetProp(PK.Scale));
            return v == Vector3.zero ? Vector3.one : v; // missing decodes as identity
        }

        static float BrushSheerOf(UEObject brushActor, string prop)
            => brushActor.GetProp(prop) is UEObject sc
                ? UEInterpreter.AsFloat(sc.GetProp(PK.SheerRate)) : 0f;

        // Shipped brush-actor trace: the brush's own UModel::LineCheck with the
        // actor's ToWorld coords (both trace branches validated 4800/4800
        // against the Engine.dll emulator). Runs the
        // exact trace unconditionally: a corner-transform envelope
        // cull would use unvalidated math (continuous trig, its own AABB formula)
        // and could silently drop a real contact; the shipped collision-hash
        // broadphase is a separate unit to port and validate on its own.
        public BspHit TraceBrushActor(UEObject brushActor, Vector3 from, Vector3 to, Vector3 extent)
        {
            HeadlessBspModel model = BrushModelOf(brushActor);
            BrushCoords coords = BrushToWorldCoords(brushActor);
            return extent.x == 0f && extent.y == 0f && extent.z == 0f
                ? model.TraceWorldLine(from, to, coords)
                : model.TraceWorldBox(from, to, extent, coords);
        }

        // Non-brush actor primitive for a trace: the CollideType dispatch —
        // UBox (2), UOrientedCylinder (1, tilt unsupported and loud), else the
        // shipped cylinder. Both trace bodies are validated against the
        // original (2000 cases each for the cylinder and the box).
        public ActorHit TraceNonBrushActor(UEObject candidate, Vector3 start,
            Vector3 end, Vector3 extent)
        {
            Vector3 location = UEInterpreter.AsVector3(candidate.GetProp(PK.Location));
            float radius = UEInterpreter.AsFloat(candidate.GetProp(PK.CollisionRadius));
            float width = UEInterpreter.AsFloat(candidate.GetProp(PK.CollisionWidth));
            float height = UEInterpreter.AsFloat(candidate.GetProp(PK.CollisionHeight));
            UERotator rotation = UEInterpreter.AsRotator(candidate.GetProp(PK.Rotation));
            // The body is chosen by the PRIMITIVE (AActor::GetPrimitive -> vtable +0x5c), not
            // the raw CollideType: CT 3 with a mesh is a UMesh, whose vtable LineCheck is the
            // body UBox::LineCheck 0x103fe620 (see PointCheck in ActorEncroachmentOverlaps);
            // CT 3 without mesh or brush is a cylinder.
            UEPrimitiveKind prim = UEPrimitive.Resolve(candidate);
            if (prim == UEPrimitiveKind.Mesh)
            {
                // UBox body, box from the mesh (its slot +0x60), not (R, W, H).
                (Vector3 lo, Vector3 hi) = MeshPrimitiveLocalBox(candidate);
                return ActorBoxTrace.LineCheck(start, end, extent, location, lo, hi, rotation);
            }
            if (prim == UEPrimitiveKind.Box)
                return ActorBoxTrace.LineCheck(start, end, extent, location,
                    radius, width, height, rotation);
            if (prim == UEPrimitiveKind.OrientedCylinder)
            {
                // UOrientedCylinder::LineCheck (Engine.dll.c:18845-18930, thunk
                // 0x1030155f): Start/End are transformed into the owner's LOCAL frame
                // (AActor::ToLocal, vtable +0x98 — UnitCoords/Rotation axes with origin at
                // Location) and IMMEDIATELY re-based onto Location (`+ param_2[0x3f..0x41]`),
                // extent = FBox(−e,e).TransformBy (sum of absolute axes), then THE SAME
                // UPrimitive::LineCheck with the same Owner; on a hit Location/Normal are returned
                // via FCoords::Transpose (origin −Origin·M, axes as columns). Both tilted
                // (Pitch/Roll ≠ 0) and yaw-only go through this shipped body. Live carrier of a
                // tilt: the BlockPlayer rings on the Quidditch maps (Pitch 16288, CT1, R 60..88, H 5).
                BrushCoords local = BrushCoords.Identity;
                local.DivRotator(rotation.Pitch, rotation.Yaw, rotation.Roll);
                Vector3 s0 = local.TransformVector(start - location);
                Vector3 e0 = local.TransformVector(end - location);
                Vector3 ls = new(s0.x + location.x, s0.y + location.y, s0.z + location.z);
                Vector3 le = new(e0.x + location.x, e0.y + location.y, e0.z + location.z);
                Vector3 lext = new(
                    Math.Abs(local.X.x) * extent.x + Math.Abs(local.X.y) * extent.y + Math.Abs(local.X.z) * extent.z,
                    Math.Abs(local.Y.x) * extent.x + Math.Abs(local.Y.y) * extent.y + Math.Abs(local.Y.z) * extent.z,
                    Math.Abs(local.Z.x) * extent.x + Math.Abs(local.Z.y) * extent.y + Math.Abs(local.Z.z) * extent.z);
                ActorHit lh = ActorCylinderTrace.LineCheck(ls, le, lext,
                    location.x, location.y, location.z, width, radius, height);
                if (!lh.Blocked) return lh;
                BrushCoords inv = new()
                {
                    Origin = -local.TransformVector(location),
                    X = new Vector3(local.X.x, local.Y.x, local.Z.x),
                    Y = new Vector3(local.X.y, local.Y.y, local.Z.y),
                    Z = new Vector3(local.X.z, local.Y.z, local.Z.z),
                };
                Vector3 d = new((lh.Location.x - location.x) - inv.Origin.x,
                    (lh.Location.y - location.y) - inv.Origin.y, (lh.Location.z - location.z) - inv.Origin.z);
                return new ActorHit(true, lh.Time, inv.TransformVector(d), inv.TransformVector(lh.Normal));
            }
            // THE Z SHIFT BELONGS TO THE BODY, NOT TO CollideType. UPrimitive::LineCheck reads
            // `Owner+0x1d0 + Owner+0x104` UNCONDITIONALLY (Engine.dll.c:35190), and exactly the
            // primitives that run this body arrive here:
            //   CT 0        — UPrimitive itself;
            //   CT 3 without mesh or brush — also a UPrimitive cylinder (3 is neither 1 nor 2);
            //                 CT 3 WITH A MESH does NOT come here — that is the UBox body above;
            //   CT 1        — UOrientedCylinder::LineCheck (18845) is handled by the branch above
            //                 (any rotation, including tilt) and does not come here.
            // This is live: dozens of actors on the TAS route carry a non-zero width
            // (CollisionWidth is serialized in 40 of 41 maps; note `grep` needs `-a` on .unr, and
            // `--actorpropsall` Props is a LIST, not a dictionary). Mesh actors such as
            // TransTrestleTable0 (48.6), HogTrestleBench22 (49.93), TransBlackboard0 (35.84) go
            // through the UBox body (UMesh vtable, see above), not this cylinder — shifting them by
            // 0x1d0 made the Lev_Tut1b gnomes pass under a sofa whose cylinder hung
            // CollisionWidth=73 above the floor.
            return ActorCylinderTrace.LineCheck(start, end, extent,
                location.x, location.y, location.z, width, radius, height);
        }

        // The world sentinel the shipped blocking rule compares against
        // (Actors[0] is asserted to be the LevelInfo, ../Inc/UnLevel.h:0xf7).
        UEObject LevelSentinel => Actors.Count > 0 ? Actors[0] : null;

        bool IsBlockedByActor(UEObject actor, UEObject other)
            => UEBlocking.IsBlockedBy(actor, other, LevelSentinel);

        // The rider orbit: build the yaw-only coords from the MASKED yaw delta
        // through the shipped rotator path — NOT continuous trig — then
        // TransformVectorBy(rel, coords).
        static Vector3 OrbitTransform(Vector3 rel, int maskedYawDelta)
        {
            BrushCoords c = BrushCoords.Identity;
            c.MulRotator(0, maskedYawDelta, 0);
            return c.TransformVector(rel);
        }

        // Shipped AActor::BeginTouch (Engine.dll.c:225598) — sequential
        // asymmetric TouchTo with slot eviction; events interleaved with the
        // bookkeeping (see UELifecycle).
        public void TouchActor(UEInterpreter interp, UEObject a, UEObject b)
        {
            UELifecycle.BeginTouch(interp, a, b);
            if (TraceEvents && (a.GetProp(PK.Player) is not null || b.GetProp(PK.Player) is not null)
                && (Array.IndexOf(a.TouchList(), b) >= 0 || Array.IndexOf(b.TouchList(), a) >= 0))
                Log($"touch_begin a={a.GetProp(PK.Name)} b={b.GetProp(PK.Name)}");
        }

        // Shipped AActor::EndTouch (Engine.dll.c FUN_1037a3e0).
        public void UnTouchActor(UEInterpreter interp, UEObject a, UEObject b)
        {
            bool linked = Array.IndexOf(a.TouchList(), b) >= 0 || Array.IndexOf(b.TouchList(), a) >= 0;
            UELifecycle.EndTouch(interp, a, b, noNotifySelf: false);
            if (linked && TraceEvents && (a.GetProp(PK.Player) is not null || b.GetProp(PK.Player) is not null))
                Log($"touch_end a={a.GetProp(PK.Name)} b={b.GetProp(PK.Name)}");
        }

        // The CALLER's FCheckResult of ULevel::MoveActor / SingleLineCheck.
        // Shipped fields: Actor +4, Location +8, Normal +0x14, Time +0x24, Item
        // +0x28; constructor FCheckResult(1.0): Time 1, Item −1, rest zero.
        //   * MoveActor writes it ONLY on the full path (0x103aa530-0x103aa552:
        //     a copy of FCheckResult(1.0), then the move result). The fast path
        //     (0x103aa48c-0x103aa501) and the bStatic/!bMovable refusal (0x103aa3ed-
        //     0x103aa41b) do NOT touch it — the caller reads what was there.
        //   * SingleLineCheck on a miss writes only Time=1 and Actor=NULL
        //     (0x103ac4af-0x103ac4b9), on a hit — a copy of the whole record.
        // It is the caller's LOCAL, so in the port it lives with the caller too: each
        // physics body creates it where shipped constructs its own and passes it to
        // MoveActor/traces. MoveActor still publishes the actor's _lastMove* properties on
        // the full path — for fixtures and diagnostics; production bodies do not read them.
        public sealed class MoveCheck
        {
            public float Time = 1f;
            public Vector3 Normal;
            public UEObject Actor;
            public int Item = -1;
            public Vector3 Location;

            public void Set(float time, Vector3 normal, UEObject actor, int item, Vector3 location)
            {
                Time = time; Normal = normal; Actor = actor; Item = item; Location = location;
            }

            // ULevel::SingleLineCheck into the same record (0x103ac297-0x103ac4c0).
            public void FromTrace(in LineCheckHit h)
            {
                if (!h.Blocked) { Time = 1f; Actor = null; return; }
                Set(h.Time, h.Normal, h.Actor, h.Node, h.Location);
            }

            // Test seam: the record from the published _lastMove* of the actor's last full
            // move (fixtures that feed Mount by hand).
            public static MoveCheck FromActorProps(UEObject a) => new MoveCheck
            {
                Time = UEInterpreter.AsFloat(a.GetProp(PK._lastMoveFraction)),
                Normal = UEInterpreter.AsVector3(a.GetProp(PK._lastMoveNormal)),
                Actor = a.GetProp(PK._lastMoveActor) as UEObject,
                Item = a.GetProp(PK._lastMoveNode) is null ? -1 : UEInterpreter.ToInt(a.GetProp(PK._lastMoveNode)),
                Location = UEInterpreter.AsVector3(a.GetProp(PK._lastMoveHitLocation)),
            };
        }

        // A swept MoveActor hit. Plain struct, not a positional record struct
        // (C# 10) — Core builds on the netstandard2.1 + C#9 profile.
        public readonly struct MoveHit
        {
            public float Fraction { get; }
            public Vector3 Normal { get; }
            public UEObject Actor { get; }
            public bool Blocking { get; }
            public int Node { get; }
            public Vector3 Location { get; }

            public MoveHit(float Fraction, Vector3 Normal, UEObject Actor,
                bool Blocking, int Node, Vector3 Location)
            {
                this.Fraction = Fraction;
                this.Normal = Normal;
                this.Actor = Actor;
                this.Blocking = Blocking;
                this.Node = Node;
                this.Location = Location;
            }
        }

        // The MoveActor sweep's hit list: shipped MultiLineCheck, then Blocking
        // filled in the way the caller does — one IsBlockedBy against the moving
        // actor, which resolves the world hit through its own Level leg.
        public void CollectMoveHits(UEObject actor, Vector3 from, Vector3 to, Vector3 extent,
            bool traceWorld, bool traceActors, List<MoveHit> hits)
        {
            UEObject levelInfo = LevelSentinel;
            foreach (MoveHit hit in MultiLineCheck(from, to, extent, traceActors,
                         traceWorld ? levelInfo : null))
            {
                // Shipped drops the mover itself through the Owner/CollisionTag
                // walk; the hash can still hand it back, so drop it here.
                if (ReferenceEquals(hit.Actor, actor)) continue;
                hits.Add(new MoveHit(hit.Fraction, hit.Normal, hit.Actor,
                    UEBlocking.IsBlockedBy(actor, hit.Actor, levelInfo), hit.Node, hit.Location));
            }
        }

        // ULevel::FarMoveActor (Engine.dll.c:159147) — the SetLocation(267)
        // path: teleport the actor to a new location. The shipped gate (asm
        // 0x103a9f61 block) is bCollideWorld (0x1dc bit 1) or
        // bCollideWhenPlacing (0x198 bit 15, non-client): those run FindSpot
        // with bTryCurrent=0 — the nudge loop ALWAYS runs, there is no direct
        // accept — and a failed FindSpot refuses the teleport. Then
        // CheckEncroachment at the (possibly nudged) spot. A successful move
        // commits Location + OldLocation, marks bJustTeleported, drops based
        // riders, and re-zones.
        public bool FarMove(UEInterpreter interp, UEObject actor, Vector3 newLoc,
            bool bTest, bool bNoCheck)
        {
            // Guard 0x103a9e29..0x103a9e45: (bStatic || !bMovable) && !GIsEditor → 0.
            // The head does NOT check bDeleteMe: a destroyed actor moves, and the hash refuses
            // it on its own — FCollisionHash::RemoveActor/AddActor exit on bDeleteMe
            // (0x10364e93, 0x10364ba5), see HashRemoveActor/HashAddActor.
            if (UEInterpreter.ToBool(actor.GetProp(PK.bStatic))
                || !UEInterpreter.ToBool(actor.GetProp(PK.bMovable))) return false;
            HashRemoveActor(actor);
            bool rejoined = false;
            try
            {
                if (!bNoCheck && (UEInterpreter.ToBool(actor.GetProp(PK.bCollideWorld))
                    || UEInterpreter.ToBool(actor.GetProp(PK.bCollideWhenPlacing))))
                {
                    if (Bsp is null)
                        throw new NotSupportedException(
                            $"FarMoveActor FindSpot without a world model actor={actor.GetProp(PK.Name)}");
                    // Extent — AActor::GetCylinderExtent(this) (Engine.dll.c:159191,
                    // call 0x103a9f24), not (R, R, H): they differ for CollideType 2/3.
                    Vector3 spot = newLoc;
                    bool fits = FindSpot(GetCylinderExtent(actor), ref spot, tryCurrent: false);
                    if (!fits)
                    {
                        Log($"far_move_findspot_fail actor={actor.GetProp(PK.Name)} "
                            + $"class={actor.Class?.Name} location={newLoc}");
                        return false;
                    }
                    if (spot != newLoc)
                        Log($"far_move_findspot_adjust actor={actor.GetProp(PK.Name)} "
                            + $"from={newLoc} to={spot}");
                    newLoc = spot;
                }
                // CheckEncroachment (Engine.dll.c:62906) at the new location; a
                // blocking encroachment refuses the move (bTouchActors=1 per asm
                // 0x103a9f6c).
                if (!bTest && !bNoCheck && CheckEncroachment(interp, actor, newLoc,
                        UEInterpreter.AsRotator(actor.GetProp(PK.Rotation)), bTouchActors: true))
                    return false;

                if (!bTest && UEInterpreter.ToInt(actor.GetProp(PK.StandingCount)) != 0)
                    foreach (UEObject rider in Actors.ToArray())
                        if (rider is not null && ReferenceEquals(rider.GetProp(PK.Base) as UEObject, actor))
                            SetBase(interp, rider, null, notify: true);
                if (!bTest) actor.Props["bJustTeleported"] = true;
                actor.Props["Location"] = newLoc;
                actor.Props["OldLocation"] = newLoc;
                // 0x103aa09f..0x103aa0b5: Hash->AddActor BEFORE SetActorZone (0x103aa0b8..0x103aa0ca): the zone event
                // handlers see the actor in the collision hash. SetActorZone(Actor, bTest, 0) reads the live Location;
                // a test move refreshes the region silently, no zone events.
                HashAddActor(actor); rejoined = true;
                SetActorZone(interp, actor, UEInterpreter.AsVector3(actor.GetProp(PK.Location)), bTest);
                return true;
            }
            finally
            {
                // LAB_103aa09f: the actor rejoins the hash on every exit, at
                // whatever Location the path left it with.
                if (!rejoined) HashAddActor(actor);
            }
        }

        // ULevel::CheckEncroachment (Engine.dll.c:62906, body 0x103ab600): does
        // `actor` at (testLoc, testRot) encroach a blocking actor? Returns true =
        // blocked (the caller refuses the move). Full shipped flow: overlap set at
        // the test pose (ActorEncroachmentOverlaps), per-overlap block-bit
        // discrimination, the moving-brush push (moveSmooth by the move delta,
        // re-query, second push with the NEGATED delta from the test pose), the
        // EncroachingOn event whose nonzero return blocks (asm 0x103aba72), then
        // touch maintenance and the EncroachedBy pass.
        public bool CheckEncroachment(UEInterpreter interp, UEObject actor, Vector3 testLoc,
            UERotator testRot, bool bTouchActors)
        {
            // Gate (0x103ab63a `test byte [esi+0x1dc], 0xd`): bit0|bit2|bit3 =
            // bCollideActors|bBlockActors|bBlockPlayers (AActor::SetCollision
            // writes them at 0x1dc bits 0/2/3; bProjTarget is bit4, NOT in the
            // mask), else only a NON-static brush enters (0x103ab643).
            bool hasBits = UEInterpreter.ToBool(actor.GetProp(PK.bCollideActors))
                || UEInterpreter.ToBool(actor.GetProp(PK.bBlockActors))
                || UEInterpreter.ToBool(actor.GetProp(PK.bBlockPlayers));
            // Same three shipped tests as the candidate walk (62942-62952): the
            // gate lets a brush in ONLY when it is a real ABrush and NOT static.
            bool actorMovingBrush = UEBlocking.IsBrush(actor)
                && !UEInterpreter.ToBool(actor.GetProp(PK.bStatic));
            if (!hasBits && !actorMovingBrush) return false;

            List<UEObject> overlaps = ActorEncroachmentOverlaps(actor, testLoc, testRot);
            UEObject level = interp.GetLevelInfo();
            if (overlaps.Count > 0)
            {
                var names = new List<string>();
                foreach (UEObject o in overlaps) names.Add($"{o.GetProp(PK.Name)}");
                Log($"encroach_candidates actor={actor.GetProp(PK.Name)} "
                    + $"order=[{string.Join(",", names)}]");
            }
            foreach (UEObject other in overlaps)
            {
                if (ReferenceEquals(other, actor) || ReferenceEquals(other, level)) continue;
                if (!IsBlockedByActor(actor, other)) continue;
                bool otherMovingBrush = UEBlocking.IsBrush(other)
                    && !UEInterpreter.ToBool(other.GetProp(PK.bStatic));
                if (actorMovingBrush && !otherMovingBrush)
                {
                    // Push the encroached actor by the move delta (0x103ab7d7);
                    // if it still overlaps the test pose, push it again with the
                    // NEGATED delta while the brush sits at the test location
                    // (0x103ab99b), then fall through to the event.
                    Vector3 loc = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
                    var delta = new Vector3(testLoc.x - loc.x, testLoc.y - loc.y, testLoc.z - loc.z);
                    MoveSmooth(interp, other, delta);
                    List<UEObject> again = ActorEncroachmentOverlaps(actor, testLoc, testRot);
                    if (!again.Contains(other)) continue; // pushed clear
                    actor.Props["Location"] = testLoc;
                    MoveSmooth(interp, other, new Vector3(-delta.x, -delta.y, -delta.z));
                    actor.Props["Location"] = loc;
                }
                // Actor.EncroachingOn(Other): a nonzero return blocks the move
                // (return 1 path at 0x103aba72 that the decompiler dropped).
                // Through ProcessEvent: an actor destroyed during the move and the probe mask
                // cut off the body, ReturnValue stays 0 — the move is not blocked.
                if (GuardResult(() => UEInterpreter.ToBool(
                        UELifecycle.CallEventResult(interp, actor, "EncroachingOn", other)), "EncroachingOn"))
                    return true;
            }

            if (bTouchActors)
                foreach (UEObject t in actor.TouchList())
                    if (t is not null && !IsOverlapping(actor, t))
                        UnTouchActor(interp, actor, t);

            // Second pass over the ORIGINAL overlap list (0x103abb30): the real
            // IsBlockedBy test — non-blocking overlaps BeginTouch (if asked),
            // blocking ones fire Other.EncroachedBy(Actor).
            foreach (UEObject other in overlaps)
            {
                if (ReferenceEquals(other, actor) || ReferenceEquals(other, level)) continue;
                if (!IsBlockedByActor(actor, other))
                {
                    if (bTouchActors) TouchActor(interp, actor, other);
                    continue;
                }
                UELifecycle.CallEvent(interp, other, "EncroachedBy", actor);
            }
            return false;
        }

        // Shipped ULevel::MoveActor (Engine.dll.c:123689, 0x103aa390..0x103ab231)
        // (UActor.cpp:1367): the atomic sweep-move-resolve primitive behind the
        // Move native. Orchestration follows the shipped control flow: tiny-delta
        // fast path, 2-unit padded sweep with the hit time re-based to the
        // unpadded span, rider carry (with yaw orbit) BEFORE the commit,
        // unconditional rotation commit, then Bump/Touch/UnTouch/re-zone. Returns
        // "moved at all" (Result.Time > 0), not "unblocked" — a partial move is
        // TRUE.
        // The shipped head guard alone (0x103aa3ed): bStatic || !bMovable.
        static bool MoveActorRefuses(UEObject actor)
            => UEInterpreter.ToBool(actor.GetProp(PK.bStatic))
               || !UEInterpreter.ToBool(actor.GetProp(PK.bMovable));

        // Fast-path threshold: |dx|,|dy|,|dz| < 1e-4 (0x1047381c, 0x103aa41e-0x103aa48a).
        static bool IsTinyMoveDelta(Vector3 delta)
            => Math.Abs(delta.x) < 1e-4f && Math.Abs(delta.y) < 1e-4f && Math.Abs(delta.z) < 1e-4f;

        // Sweep end: 0x103aa696..0x103aa6dc stores paddedDelta = delta + dir·pad per component as float, then
        // 0x103aa8ac..0x103aa8f0 End = centre + paddedDelta (one rounding each), not (centre + delta) + dir·pad.
        internal static Vector3 MoveSweepEnd(Vector3 centre, Vector3 delta, Vector3 dir, float pad)
            => centre + (delta + dir * pad);

        public bool MoveActor(UEInterpreter interp, UEObject actor, Vector3 delta, UERotator newRot,
            bool bTest, bool bIgnorePawns, bool bIgnoreBases, bool bNoFail, MoveCheck callerHit = null)
        {
            // A. Guard: bStatic || !bMovable → refuse (Engine.dll.c:123784;
            // shipped tests bMovable via 0x198&0x20000). The head does NOT check bDeleteMe
            // (disasm 0x103aa3ed/0x103aa3f3): a destroyed actor moves; the hash refuses it on
            // its own (FCollisionHash::RemoveActor/AddActor exit on bDeleteMe —
            // HashRemoveActor/HashAddActor), and events on the corpse are suppressed by
            // ProcessEvent (!IsPendingKill).
            if (MoveActorRefuses(actor)) return false;

            UERotator curRot = UEInterpreter.AsRotator(actor.GetProp(PK.Rotation));
            int standingCount = UEInterpreter.ToInt(actor.GetProp(PK.StandingCount));
            // "A live brush" per shipped, in ALL THREE places that ask (the fast
            // path 123810-123812, the sweep padding 103aa663, the 0.51 extent
            // shrink 103aa8c4): Brush != NULL AND the class chain reaches ABrush
            // AND !bStatic. bStatic is already refused by guard A above, so this
            // reduces to IsBrush in practice; kept in the shipped form.
            bool movingBrush = UEBlocking.IsBrush(actor)
                && !UEInterpreter.ToBool(actor.GetProp(PK.bStatic));

            // Tiny-delta fast path (Engine.dll.c:123791-123819): identical rotation
            // is a no-op; a rotation-only change commits directly UNLESS someone
            // stands on us or we are a live brush — those fall THROUGH to the full
            // path so riders orbit and encroachment stays honest.
            // The fast path does NOT TOUCH the caller's FCheckResult (0x103aa48c-
            // 0x103aa501: returns 1 before copying FCheckResult(1.0) into Hit at
            // 0x103aa530) — neither the callerHit record nor the _lastMove* publication.
            bool tiny = IsTinyMoveDelta(delta);
            if (tiny)
            {
                if (SameRotator(curRot, newRot) || (standingCount == 0 && !movingBrush))
                {
                    if (!SameRotator(curRot, newRot)) actor.Props["Rotation"] = newRot;
                    return true;
                }
            }

            // B. Sweep. The shipped full path normalizes the delta and pads the
            // sweep end by 2 units of direction (Engine.dll.c:123865-123884);
            // hits therefore carry PADDED-span times. Brush identity does NOT
            // skip the sweep — at 103aa663 it only ZEROES that padding for a
            // real non-static ABrush (the constant loaded otherwise is
            // 0x40000000 = 2.0f). A moving mover must sweep like anything else,
            // or it tunnels through the world and through actors on the way and
            // only ever gets the destination encroachment check.
            Vector3 location = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
            (float dist, Vector3 dir) = MoveActorDirection(delta);
            float pad = movingBrush ? 0f : 2f;
            var hits = new List<MoveHit>();
            float time = 1f;             // FCheckResult.Time
            Vector3 blockNormal = Vector3.zero;
            UEObject blockActor = null;  // null = world hit or no block
            int blockNode = -1;
            Vector3 blockLocation = Vector3.zero;
            bool blocked = false;
            bool anyActorConsidered = false;

            bool collideWorld = UEInterpreter.ToBool(actor.GetProp(PK.bCollideWorld));
            bool collideActors = UEInterpreter.ToBool(actor.GetProp(PK.bCollideActors));
            // Shipped sweep gate (103aa69d): (0x1dc & 3) — bCollideActors or
            // bCollideWorld — AND a non-zero delta. Brush-ness is not in it.
            if ((collideActors || collideWorld)
                && (delta.x != 0f || delta.y != 0f || delta.z != 0f))
            {
                // The sweep runs in the primitive's COLLISION BOX space:
                // GetPrimitive (vtable +0xa4) -> GetCollisionBoundingBox
                // (+0x60), then centre = (min+max)/2 and the half-extents.
                // For an ordinary cylinder with CollisionWidth 0 the centre is
                // the Location and the half-extents are (R, R, H); for a brush it is the model box through ToWorld.
                (Vector3 boxMin, Vector3 boxMax) = HashBoxOf(actor);
                var centre = new Vector3((boxMin.x + boxMax.x) * 0.5f,
                    (boxMin.y + boxMax.y) * 0.5f, (boxMin.z + boxMax.z) * 0.5f);
                var extent = new Vector3((boxMax.x - boxMin.x) * 0.5f,
                    (boxMax.y - boxMin.y) * 0.5f, (boxMax.z - boxMin.z) * 0.5f);
                // A moving brush sweeps 0.51 tighter on every axis (103aa8c4).
                if (movingBrush)
                    extent = new Vector3(extent.x - 0.51f, extent.y - 0.51f, extent.z - 0.51f);
                Vector3 to = MoveSweepEnd(centre, delta, dir, pad);
                CollectMoveHits(actor, centre, to, extent, collideWorld, collideActors, hits);
                // ASCENDING SORT BY TIME — READ, NOT INFERRED.
                // Shipped ULevel::MultiLineCheck (0x103ac620) collects candidates into an
                // array of 0x2c-byte records capped at 0x40 and calls appQsort with comparator
                // 0x1030385a. That address is ONLY a thunk (`jmp 0x103ad500`); the body at
                // 0x103ad500 compares the float at record offset +0x24 (FCheckResult.Time):
                //     A.Time < B.Time -> -1 ; A.Time > B.Time -> +1 ; else 0.
                // Ghidra did not recover this body (the decompile has a bare label
                // LAB_1030385a), so it is taken from the disasm.
                //
                // Consequence: the candidate collection order does NOT affect the outcome — the
                // sort fully overrides it.
                // THERE IS NO SECOND SORT HERE AND THERE MUST NOT BE. Shipped sorts ONCE —
                // appQsort in ULevel::MultiLineCheck (0x103ac620, Engine.dll.c:256153), and the
                // port does it in the same place: `results.Sort` in MultiLineCheck. A repeated
                // UNSTABLE sort may reorder equal-time records, i.e. change a tie resolution that
                // shipped does not. The list arrives already sorted by MultiLineCheck.

                // Result selection walk (Engine.dll.c:123938-124015), run only when
                // bCollideWorld|bBlockActors|bBlockPlayers (0x1dc & 0xe).
                if (collideWorld || UEInterpreter.ToBool(actor.GetProp(PK.bBlockActors))
                    || UEInterpreter.ToBool(actor.GetProp(PK.bBlockPlayers)))
                {
                    foreach (MoveHit hit in hits)
                    {
                        // No special "world" arm: shipped walks ONE list and lets
                        // the inlined IsBlockedBy resolve the sentinel — 123958
                        // tests `hit.Actor != this->Level` and, when it IS the
                        // Level, takes 0x1dc>>1 = bCollideWorld. Hit.Blocking
                        // already carries exactly that.
                        // bIgnorePawns skips Pawn and Decoration hits — only NON-static
                        // ones: `(hit+0x28 & 1) == 0` (bStatic) gates both IsA tests
                        // (123942-123952).
                        if (bIgnorePawns && !UEInterpreter.ToBool(hit.Actor.GetProp(PK.bStatic))
                            && (UEInterpreter.ClassIsA(hit.Actor.Class, "Pawn")
                                || UEInterpreter.ClassIsA(hit.Actor.Class, "Decoration"))) continue;
                        // Skips: our own riders unconditionally; our base chain only
                        // under bIgnoreBases (123954-123957).
                        if (bIgnoreBases && IsBasedOn(actor, hit.Actor)) continue;
                        if (IsBasedOn(hit.Actor, actor)) continue;
                        anyActorConsidered = true;
                        if (hit.Blocking)
                        {
                            time = hit.Fraction; blockNormal = hit.Normal; blockActor = hit.Actor;
                            blockNode = hit.Node; blockLocation = hit.Location; blocked = true; break;
                        }
                    }
                }
            }

            Vector3 rebasedDelta = delta;
            // C. Re-base the hit time to the unpadded span and refuse micro-moves:
            // t' = ((dist+pad)*T - pad)/dist, whole move refused at t' <= 0.0001
            // (Engine.dll.c:124021-124034). bNoFail skips the adjustment entirely
            // (the move commits in full).
            if (time < 1f && !bNoFail)
            {
                bool refused = !MoveActorRebase(pad, dist, time, delta, out time, out rebasedDelta);
                if (refused)
                {
                    actor.Props["_lastMoveNormal"] = blockNormal;
                    actor.Props["_lastMoveFraction"] = 0f;
                    actor.Props["_lastMoveActor"] = blockActor;
                    actor.Props["_lastMoveNode"] = blockNode;
                    actor.Props["_lastMoveHitLocation"] = blockLocation;
                    callerHit?.Set(0f, blockNormal, blockActor, blockNode, blockLocation);
                    return false;
                }
            }

            // Expose the block result so walking physics can read the hit after a
            // committed move (fraction/normal/actor), not only in a dry-run probe.
            actor.Props["_lastMoveNormal"] = blockNormal;
            actor.Props["_lastMoveFraction"] = blocked ? time : 1f;
            actor.Props["_lastMoveActor"] = blockActor;
            actor.Props["_lastMoveNode"] = blocked ? blockNode : -1;
            actor.Props["_lastMoveHitLocation"] = blocked ? blockLocation : Vector3.zero;
            callerHit?.Set(blocked ? time : 1f, blockNormal, blockActor,
                blocked ? blockNode : -1, blocked ? blockLocation : Vector3.zero);
            // bNoFail (param_13) commits the WHOLE delta: the shipped rebase block
            // at 124021 owns BOTH the time re-base and the delta scaling
            // (fStack_24/20/1c), so with bNoFail set the unscaled delta assigned at
            // 124018-124020 survives to the commit even through a blocker.
            Vector3 actuallyMoved = blocked && !bNoFail ? rebasedDelta : delta;

            // D. Based riders move BEFORE the base commits (Engine.dll.c:124035-
            // 124105): translated by the committed delta, orbited around the base by
            // the masked yaw delta, rotated by the raw yaw delta; pawn riders also
            // carry ViewRotation.Yaw. The shipped rider call's trailing flags are
            // ambiguous in the decompile; bIgnoreBases=true matches both the skip
            // semantics above and SurrealEngine's rider carry.
            if (standingCount > 0 && !bTest)
            {
                int rawYawDelta = newRot.Yaw - curRot.Yaw;
                int maskedYawDelta = (newRot.Yaw & 0xfffc) - (curRot.Yaw & 0xfffc);
                bool rotChanged = !SameRotator(curRot, newRot);
                foreach (UEObject rider in Actors.ToArray())
                {
                    if (rider is null || rider.Destroyed
                        || !ReferenceEquals(rider.GetProp(PK.Base) as UEObject, actor)) continue;
                    Vector3 riderDelta = actuallyMoved;
                    UERotator riderRot = UEInterpreter.AsRotator(rider.GetProp(PK.Rotation));
                    UERotator newRiderRot = riderRot;
                    if (rotChanged)
                    {
                        Vector3 rel = location - UEInterpreter.AsVector3(rider.GetProp(PK.Location));
                        riderDelta += rel - OrbitTransform(rel, maskedYawDelta);
                        newRiderRot = new UERotator(riderRot.Pitch, riderRot.Yaw + rawYawDelta, riderRot.Roll);
                    }
                    MoveActor(interp, rider, riderDelta, newRiderRot,
                        bTest: false, bIgnorePawns: false, bIgnoreBases: true, bNoFail: false);
                    if (rotChanged && UEInterpreter.ClassIsA(rider.Class, "Pawn"))
                    {
                        UERotator view = UEInterpreter.AsRotator(rider.GetProp(PK.ViewRotation));
                        rider.Props["ViewRotation"] = new UERotator(view.Pitch, view.Yaw + rawYawDelta, view.Roll);
                    }
                }
            }

            // E. Non-pawn actors verify encroachment at the destination before the
            // commit; a hit refuses the whole move — riders were already carried,
            // exactly like the shipped order (asm 0x103aaf20-0x103aafcf: riders,
            // hash-remove, CheckEncroachment(loc+delta, newRot, bTouchActors=0),
            // blocked -> return 0 without committing).
            // The test location and the commit read the LIVE Location, each at its own point (Engine.dll.c:124112,
            // 0x103aaf38; commit 0x103aafd2..0x103ab005 `fld delta; fadd [ebx+0xfc]; fstp` AFTER CheckEncroachment,
            // whose EncroachingOn script event may move the actor): a rider carry or a handler may have moved it.
            HashRemoveActor(actor);
            if (!bTest && !bNoFail && !UEInterpreter.ClassIsA(actor.Class, "Pawn")
                && CheckEncroachment(interp, actor, UEInterpreter.AsVector3(actor.GetProp(PK.Location)) + actuallyMoved, newRot, bTouchActors: false))
            {
                // Shipped 124124-124136: hash re-add and `return 0` — Hit keeps the
                // sweep result (time, normal, actor) written above.
                HashAddActor(actor);
                return false;
            }

            // F. Commit: Location AND Rotation only (Engine.dll.c:124139-124144,
            // 0x103aafd2..): OldLocation (+0x114) is not written anywhere in the
            // body 0x103aa390..0x103ab231 — physics writes it at its own start.
            actor.Props["Location"] = UEInterpreter.AsVector3(actor.GetProp(PK.Location)) + actuallyMoved;
            actor.Props["Rotation"] = newRot;
            HashAddActor(actor);

            // G+H. The whole notification block (Bump, Touch, UnTouch) is gated on
            // bTest (param_10) — shipped commits the pose above regardless and only
            // skips the events here (124145-124189). The zone update below is NOT
            // gated: shipped forwards the flag into it (vtable +0xb0, param_10).
            if (!bTest)
            {
                // Bump both parties on an actor block, hit actor first
                // (Engine.dll.c:124148-124166) — pair dispatch AND the shipped gates
                // (world sentinel + IsBasedOn).
                DispatchBump(interp, actor, blockActor);

                // Touch pass (Engine.dll.c:124169-124181), gated the shipped way:
                // run when an actor candidate was actually considered OR the mover
                // does not fully block (either block bit clear). Raw padded-span hit
                // times compare against the re-based Result.Time — the shipped quirk.
                if (anyActorConsidered
                    || !UEInterpreter.ToBool(actor.GetProp(PK.bBlockActors))
                    || !UEInterpreter.ToBool(actor.GetProp(PK.bBlockPlayers)))
                {
                    foreach (MoveHit hit in hits)
                    {
                        if (blocked && hit.Fraction >= time) break;
                        if (hit.Actor is not null && !hit.Blocking
                            && !IsBasedOn(hit.Actor, actor)
                            && (!bIgnoreBases || !IsBasedOn(actor, hit.Actor)))
                            TouchActor(interp, actor, hit.Actor);
                    }
                }
                // UnTouch anything no longer overlapping (Engine.dll.c:124182-124188).
                foreach (UEObject t in actor.TouchList())
                    if (t is not null && !IsOverlapping(actor, t))
                        UnTouchActor(interp, actor, t);
            }

            // The zone update takes bTest AS AN ARGUMENT (124190: vtable +0xb0 is
            // called as (this, param_10, 0)) — SetActorZone gates ActorLeaving /
            // ZoneChange / ActorEntered / FootZoneChange / HeadZoneChange behind it,
            // so dropping the argument let a TEST move fire real zone lifecycle
            // events.
            // SetActorZone (vtable +0xb0, FUN_103acdd0) has no location argument: it reads Actor+0xfc at the call
            // (Engine.dll.c:256385-256387) — after Bump/Touch/UnTouch, which may have relocated the actor.
            SetActorZone(interp, actor, UEInterpreter.AsVector3(actor.GetProp(PK.Location)), bTest);
            // Shipped return (124194-124199), with NO bTest branch of its own:
            //     if (Result.Time <= 0.0) return 0; return 1;
            // Result.Time is the re-based hit time when blocked and 1.0 otherwise,
            // so "moved at all" — a move blocked PART WAY still returns true.
            return time > 0f;
        }

        // Native MoveSmooth (3969): execMoveSmooth (Engine.dll.c:62637) clears
        // bJustTeleported and returns the moveSmooth result. The body is Core's.
        public bool ExecMoveSmooth(UEInterpreter interp, UEObject actor, Vector3 delta)
        {
            actor.Props["bJustTeleported"] = false;
            return MoveSmooth(interp, actor, delta);
        }

        // Shipped AActor::moveSmooth (Engine.dll 0x103e4c30): one move at the
        // current rotation; on a block, slide along the wall by the remaining
        // fraction (never backwards), dispatching the HitWall(Normal, Hit.Actor)
        // event BEFORE the slide; a second block folds the slide with TwoWallAdjust and
        // moves once more. Returns the FIRST move's result (moved at all), not
        // the slide's — the shipped encroachment push and the MoveSmooth native
        // both consume that value.
        public bool MoveSmooth(UEInterpreter interp, UEObject actor, Vector3 delta)
        {
            // Rotation is re-read for EACH move: shipped keeps the field ADDRESS in edi
            // (`lea edi, [esi+0x108]`, 0x103e4c83) and copies three words before each
            // MoveActor (0x103e4c92, 0x103e4e1c, 0x103e4e7b). HitWall between the first and
            // second move may rotate the actor — the slide carries the new rotation, not a
            // snapshot from before the hit.
            // The body's FCheckResult is FCheckResult(1.0) once (Engine.dll.c:
            // 14563-14568); all three moves go into it.
            var smoothHit = new MoveCheck();
            bool moved = MoveActor(interp, actor, delta, UEInterpreter.AsRotator(actor.GetProp(PK.Rotation)),
                bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false, smoothHit);
            float fraction = smoothHit.Time;
            if (fraction < 1f)
            {
                Vector3 normal = smoothHit.Normal;
                float dot = normal.x * delta.x + delta.y * normal.y + delta.z * normal.z;
                Vector3 slide = (delta - normal * dot) * (1f - fraction);
                if (delta.x * slide.x + delta.z * slide.z + delta.y * slide.y >= 0f)
                {
                    Vector3 desiredDir = delta.normalized; // FVector::SafeNormal
                    // Through UELifecycle, whose Guard now carries the frontend
                    // policy (the slide below must still run either way).
                    // Wall = Hit.Actor (disasm 0x103e4dcc/0x103e4dde: [ebp-0x80] —
                    // FCheckResult.Actor), for BSP — the level sentinel.
                    UELifecycle.CallEvent(interp, actor, "HitWall", normal, smoothHit.Actor);
                    MoveActor(interp, actor, slide, UEInterpreter.AsRotator(actor.GetProp(PK.Rotation)),
                        bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false, smoothHit);
                    if (smoothHit.Time < 1f)
                    {
                        Vector3 n2 = smoothHit.Normal;
                        TwoWallAdjust(desiredDir, ref slide, n2, normal, smoothHit.Time);
                        MoveActor(interp, actor, slide, UEInterpreter.AsRotator(actor.GetProp(PK.Rotation)),
                            bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false, smoothHit);
                    }
                }
            }
            return moved;
        }

        // AActor::IsOverlapping (Engine thunk 0x103029d7 -> 0x10379d90): cylinder-cylinder overlap; a brush on either
        // side, or B == A.Level (+0x7c), never overlaps.
        public static bool IsOverlapping(UEObject a, UEObject b)
        {
            // Shipped AActor::IsOverlapping (Engine.dll.c:71052-71067) bails on a
            // brush side only when BOTH tests hold: Brush != NULL and the class
            // chain reaches ABrush. A non-ABrush actor that merely carries a
            // Brush reference must continue into the cylinder math — a
            // pointer-only test produces false UnTouch/encroachment results.
            if (UEBlocking.IsBrush(a) || UEBlocking.IsBrush(b)) return false;
            if (ReferenceEquals(b, a.GetProp(PK.Level))) return false;
            return CylindersOverlap(UEInterpreter.AsVector3(a.GetProp(PK.Location)),
                UEInterpreter.AsFloat(a.GetProp(PK.CollisionRadius)), UEInterpreter.AsFloat(a.GetProp(PK.CollisionHeight)),
                UEInterpreter.AsVector3(b.GetProp(PK.Location)),
                UEInterpreter.AsFloat(b.GetProp(PK.CollisionRadius)), UEInterpreter.AsFloat(b.GetProp(PK.CollisionHeight)));
        }

        // The cylinder test of the body (disasm 0x10379e04..0x10379e70, PC64): dx = A.x − B.x, dy, r = B.R + A.R, all in
        // st0; overlap when dy·dy + dx·dx < r·r (fcompp, test ah,0x41) and dz·dz < (B.H + A.H)². Every operation rounds
        // at 64 bits (X87Ext); a float model disagrees near the boundary (corpus is_overlapping_matches_engine).
        public static bool CylindersOverlap(Vector3 la, float ra, float ha, Vector3 lb, float rb, float hb)
        {
            static X87Ext Sub(float x, float y) => X87Ext.Round64(X87Ext.From(x) - X87Ext.From(y));
            static X87Ext Sq(X87Ext v) => X87Ext.Round64(v * v);
            X87Ext dx = Sub(la.x, lb.x), dy = Sub(la.y, lb.y);
            X87Ext r = X87Ext.Round64(X87Ext.From(rb) + X87Ext.From(ra));
            if (X87Ext.Round64(Sq(dy) + Sq(dx)).CompareTo(Sq(r)) >= 0) return false;
            X87Ext dz = Sub(la.z, lb.z), h = X87Ext.Round64(X87Ext.From(hb) + X87Ext.From(ha));
            return Sq(dz).CompareTo(Sq(h)) < 0;
        }
    }
}
