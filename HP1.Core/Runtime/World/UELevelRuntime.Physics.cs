// Physics dispatch (PerformPhysics) and the other movement modes: projectile, trailer, flying,
// moving brush (PhysMovingBrush), plus actor rotation (ActorPhysicsRotation, PhysicsRotation, FixedTurn).
// PHYS_Walking and PHYS_Falling have their own files. Fields live in UELevelRuntime.cs.
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
        // AActor::physProjectile (shipped Engine.dll 0x103f2ab0 / c:56153).
        // This physics mode is not projectile-class-only: Harry's authored
        // Mounting state deliberately sets PHYS_Projectile with zero velocity.
        // Water drag, acceleration, Projectile.MaxSpeed, MoveActor, HitWall and
        // bounce-to-falling order therefore belong to the generic actor runtime.
        public void PhysProjectile(UEInterpreter interp, UEObject actor, float dt, int iteration = 0)
        {
            UEObject region = actor.GetProp(PK.Region) as UEObject;
            UEObject zone = region?.GetProp(PK.Zone) as UEObject;
            // AActor::physProjectile zone-0 head (Engine.dll.c:295433-295437):
            // ZoneNumber==0 → Level->DestroyActor(this), no event.
            if (zone is null || UEInterpreter.ToInt(region.GetProp(PK.ZoneNumber)) == 0)
            {
                DestroyActor(interp, actor);
                return;
            }

            Vector3 oldLocation = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
            actor.Props["OldLocation"] = oldLocation;
            actor.Props["bJustTeleported"] = false;
            float remaining = dt;
            int bounceCount = 0;

            // Body FCheckResult: FCheckResult(1.0) before the loop (Engine.dll.c:
            // 56213-56220), and before EACH move shipped resets only Time
            // (56225: fStack_64 = 1.0) — normal and actor remain from the previous one.
            var projHit = new MoveCheck();
            // Iteration counter is a call parameter (param_2), loop head
            // `0 < remaining && param_2 < 8` (56231).
            while (remaining > 0f && iteration < 8)
            {
                iteration++;
                // The zone is read LIVE on every iteration (this+0xa0, 56233).
                zone = (actor.GetProp(PK.Region) as UEObject)?.GetProp(PK.Zone) as UEObject ?? zone;
                Vector3 velocity = UEInterpreter.AsVector3(actor.GetProp(PK.Velocity));
                if (UEInterpreter.ToBool(zone.GetProp(PK.bWaterZone)))
                {
                    float drag = 1f - remaining
                        * UEInterpreter.AsFloat(zone.GetProp(PK.ZoneFluidFriction)) * 0.2f;
                    velocity *= drag;
                }
                velocity += UEInterpreter.AsVector3(actor.GetProp(PK.Acceleration)) * remaining;

                if (UEInterpreter.ClassIsA(actor.Class, "Projectile"))
                {
                    float maxSpeed = UEInterpreter.AsFloat(actor.GetProp(PK.MaxSpeed));
                    if (velocity.sqrMagnitude > maxSpeed * maxSpeed)
                        velocity = velocity.normalized * maxSpeed;
                }
                actor.Props["Velocity"] = velocity;

                float thisSlice = remaining;
                remaining = 0f;
                // Shipped uses the function's original DeltaSeconds here, even
                // after a bounce leaves a smaller `remaining` slice.
                Vector3 delta = velocity * dt;
                projHit.Time = 1f;
                MoveActor(interp, actor, delta, UEInterpreter.AsRotator(actor.GetProp(PK.Rotation)),
                    bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false, projHit);
                float fraction = projHit.Time;
                if (fraction < 1f && !actor.Destroyed
                    && !UEInterpreter.ToBool(actor.GetProp(PK.bJustTeleported)))
                {
                    Vector3 normal = projHit.Normal;
                    UEObject hitActor = projHit.Actor ?? interp.GetLevelInfo();
                    UELifecycle.CallEvent(interp, actor, "HitWall", normal, hitActor);
                    if (UEInterpreter.ToBool(actor.GetProp(PK.bBounce)))
                    {
                        if (bounceCount < 2) remaining = (1f - fraction) * thisSlice;
                        bounceCount++;
                        // 56275-56283, disasm 0x103f2e3c-0x103f2e5a: physFalling(
                        // remaining, param_2) WITHOUT a remaining>0 condition (when exhausted —
                        // with zero: the physFalling head still runs), then `jmp 0x103f2b6e` —
                        // the head of THIS loop; the remainder is not zeroed, and the projectile
                        // integrates it once more with its own formula.
                        if (UEInterpreter.ToInt(actor.GetProp(PK.Physics)) == 2)
                            PhysFalling(interp, actor, remaining, iteration);
                    }
                }
            }

            // Tail (disasm 0x103f2e5f-0x103f2ec4): X in the x87 register, Y and Z
            // through float (see DisplacementVelocityX87).
            if (!UEInterpreter.ToBool(actor.GetProp(PK.bBounce))
                && !UEInterpreter.ToBool(actor.GetProp(PK.bJustTeleported)) && dt != 0f)
                actor.Props["Velocity"] = DisplacementVelocityX87(
                    UEInterpreter.AsVector3(actor.GetProp(PK.Location)), oldLocation, dt, 0);
        }

        // AActor::physTrailer (shipped Engine.dll.c:149807-150025). A trailer is
        // repositioned from its authoritative Owner every physics tick via
        // FarMove(..., bNoCheck=1), then (for non-sprites) rotated with one
        // zero-delta MoveActor. The reached PeevesTrail path is non-sprite,
        // AnimBone=0. Keep the skeletal-bone branch loud until the native mesh
        // GetBoneCoords path is available in Core; using a bone from the frontend
        // would invert the architecture boundary.
        public void PhysTrailer(UEInterpreter interp, UEObject actor)
        {
            UEObject owner = actor.GetProp(PK.Owner) as UEObject;
            if (owner is null || owner.Destroyed) return;

            bool prePivot = UEInterpreter.ToBool(actor.GetProp(PK.bTrailerPrePivot));
            bool sameRotation = UEInterpreter.ToBool(actor.GetProp(PK.bTrailerSameRotation));
            int drawType = UEInterpreter.ToInt(actor.GetProp(PK.DrawType));
            Vector3 ownerLocation = UEInterpreter.AsVector3(owner.GetProp(PK.Location));
            Vector3 trailerPrePivot = UEInterpreter.AsVector3(actor.GetProp(PK.PrePivot));

            // DT_Sprite — a separate shipped branch (Engine.dll.c:149862-149915):
            // bTrailerPrePivot → Owner.Location + PrePivot; else with
            // bTrailerSameRotation → Owner.Location − X·(this+0x200), where X is the
            // X axis of UnitCoords/Owner.Rotation, and field +0x200 per the shipped
            // class layout is Actor.MASS (there is no "SpriteProjForward" field). Live user:
            // Halo (HPParticle/Halo.uc: SetPhysics(11), bTrailerSameRotation=true,
            // Mass=−11 — i.e. the sprite is pushed 11 units FORWARD along the owner's view);
            // Quidditch: Bludger_Halo/Snitch_Halo. Without flags — Owner.Location.
            // The sprite gets no rotation: only FarMove(bNoCheck).
            if (drawType == 1)
            {
                Vector3 spriteLocation;
                if (prePivot) spriteLocation = ownerLocation + trailerPrePivot;
                else if (sameRotation)
                {
                    UERotator ownerRotation = UEInterpreter.AsRotator(owner.GetProp(PK.Rotation));
                    BrushCoords coords = BrushCoords.Identity;
                    coords.DivRotator(ownerRotation.Pitch, ownerRotation.Yaw, ownerRotation.Roll);
                    float k = UEInterpreter.AsFloat(actor.GetProp(PK.Mass));
                    spriteLocation = new Vector3(ownerLocation.x - coords.X.x * k,
                        ownerLocation.y - coords.X.y * k, ownerLocation.z - coords.X.z * k);
                }
                else spriteLocation = ownerLocation;
                FarMove(interp, actor, spriteLocation, bTest: false, bNoCheck: true);
                return;
            }

            int animBone = UEInterpreter.ToInt(actor.GetProp(PK.AnimBone));
            if (animBone != 0 && owner.GetProp(PK.Mesh) is not null)
            {
                // BONE BRANCH (physTrailer, Engine.dll.c:150000-150020): coordinates of the
                // owner's bone AnimBone−1 via Mesh->GetBoneCoords (vtable slot +0x9c,
                // resolved from the image: 0x1047b4d8), position — the coords origin,
                // rotation — FCoords::OrthoRotation; then FarMove (slot +0x90, bNoCheck) and
                // MoveActor with a zero delta and this rotation (slot +0x8c). The pose is
                // Core's (UELevelRuntime.SkeletalPose).
                BoneWorld bone = BoneWorldOf(owner, animBone - 1);
                UERotator boneRotation = OrthoRotation(bone);
                FarMove(interp, actor, bone.Origin, bTest: false, bNoCheck: true);
                MoveActor(interp, actor, Vector3.zero, boneRotation,
                    bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false);
                if (DetailedJournal)
                    Journal?.Invoke($"trailer_bone actor={actor.GetProp(PK.Name)} owner={owner.GetProp(PK.Name)} "
                        + $"bone={animBone - 1} location={actor.GetProp(PK.Location)} rotation={actor.GetProp(PK.Rotation)}");
                return;
            }

            Vector3 newLocation = ownerLocation;
            if (prePivot || sameRotation)
            {
                UERotator ownerRotation = UEInterpreter.AsRotator(owner.GetProp(PK.Rotation));
                BrushCoords coords = BrushCoords.Identity;
                coords.MulRotator(ownerRotation.Pitch, ownerRotation.Yaw, ownerRotation.Roll);
                newLocation += coords.TransformVector(trailerPrePivot);
            }

            UERotator newRotation;
            if (sameRotation)
            {
                newRotation = UEInterpreter.AsRotator(owner.GetProp(PK.Rotation));
            }
            else
            {
                Vector3 ownerVelocity = UEInterpreter.AsVector3(owner.GetProp(PK.Velocity));
                if (Math.Abs(ownerVelocity.x) < 0.0001f
                    && Math.Abs(ownerVelocity.y) < 0.0001f
                    && Math.Abs(ownerVelocity.z) < 0.0001f)
                    newRotation = new UERotator(0x4000, 0, 0);
                else
                    newRotation = UEValue.RotatorFromVector(-ownerVelocity);
            }

            FarMove(interp, actor, newLocation, bTest: false, bNoCheck: true);
            MoveActor(interp, actor, Vector3.zero, newRotation,
                bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false);
            if (DetailedJournal)
                Journal?.Invoke($"trailer actor={actor.GetProp(PK.Name)} owner={owner.GetProp(PK.Name)} "
                    + $"location={actor.GetProp(PK.Location)} rotation={actor.GetProp(PK.Rotation)}");
        }

        // APawn::physFlying (Engine.dll.c:45551): fluid-friction calcVelocity,
        // optional zone flow, one swept MoveActor, then the same wall-slide /
        // two-wall / step-up split used by the shipped native. Velocity is finally
        // rederived from actual displacement unless the move teleported the pawn.
        public void PhysFlying(UEInterpreter interp, UEObject actor, float dt)
        {
            UEObject region = actor.GetProp(PK.Region) as UEObject;
            UEObject zone = region?.GetProp(PK.Zone) as UEObject;
            if (zone is null)
                throw new InvalidDataException($"physFlying missing zone actor={actor.GetProp(PK.Name)}");
            // APawn::physFlying zone-0 head (Engine.dll.c:45739-45749, disasm
            // 0x103f13c2-0x103f142d): bCollideWorld (0x1dc&2) && ZoneNumber==0 →
            // exit; a NON-player (bIsPlayer, Pawn+0x25c&2, cleared) first logs
            // "%s flew out of the world!" and is destroyed — XLevel vtable+0x98
            // (this, 0), the same DestroyActor slot as in physProjectile.
            // FellOutOfWorld is not sent (the decompile at 294778 shows only one branch).
            if (UEInterpreter.ToBool(actor.GetProp(PK.bCollideWorld))
                && UEInterpreter.ToInt(region.GetProp(PK.ZoneNumber)) == 0)
            {
                if (!UEInterpreter.ToBool(actor.GetProp(PK.bIsPlayer)))
                {
                    Log($"flew_out_of_world actor={actor.GetProp(PK.Name)}");
                    DestroyActor(interp, actor);
                }
                return;
            }
            Vector3 acceleration = UEInterpreter.AsVector3(actor.GetProp(PK.Acceleration));
            Vector3 zoneVelocity = UEInterpreter.AsVector3(zone.GetProp(PK.ZoneVelocity));
            Vector3 accelDir = acceleration.sqrMagnitude > 0f ? acceleration.normalized : Vector3.zero;
            UEPawnMovement.CalcVelocity(actor, accelDir, dt,
                UEInterpreter.AsFloat(actor.GetProp(PK.AirSpeed)),
                UEInterpreter.AsFloat(zone.GetProp(PK.ZoneFluidFriction)),
                bFluid: true, bBrake: false, bBuoyant: false);

            Vector3 location = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
            actor.Props["OldLocation"] = location;
            actor.Props["bJustTeleported"] = false;

            // PlayerPawns always inherit ZoneVelocity. NPCs do so only above the
            // shipped 300 uu/s threshold (strictly SizeSquared > 90000).
            Vector3 flow = UEInterpreter.ClassIsA(actor.Class, "PlayerPawn")
                || zoneVelocity.sqrMagnitude > 90000f ? zoneVelocity : Vector3.zero;
            Vector3 velocity = UEInterpreter.AsVector3(actor.GetProp(PK.Velocity));
            Vector3 delta = (velocity + flow) * dt;
            // Body FCheckResult — FCheckResult(1.0) right before the first move
            // (Engine.dll.c:45659-45661); the second and third moves and stepUp use it too.
            var flyHit = new MoveCheck();
            MoveActor(interp, actor, delta, UEInterpreter.AsRotator(actor.GetProp(PK.Rotation)),
                bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false, flyHit);

            float fraction = flyHit.Time;
            if (fraction < 1f)
            {
                Vector3 hitNormal = flyHit.Normal;
                UEObject hitActor = flyHit.Actor;
                Vector3 gravity = UEInterpreter.AsVector3(zone.GetProp(PK.ZoneGravity));
                Vector3 gravityDir = new(0f, 0f, gravity.z > 0f ? 1f : -1f);
                Vector3 desiredDir = delta.sqrMagnitude > 0f ? delta.normalized : Vector3.zero;
                Vector3 velocityDir = velocity.sqrMagnitude > 0f ? velocity.normalized : Vector3.zero;
                float verticalMotion = Vector3.Dot(velocityDir, gravityDir);

                if (Math.Abs(hitNormal.z) >= 0.2f || verticalMotion >= 0.5f || verticalMotion <= -0.2f)
                {
                    ProcessHitWall(interp, actor, hitNormal, hitActor);
                    float projection = Vector3.Dot(delta, hitNormal);
                    Vector3 slide = (delta - hitNormal * projection) * (1f - fraction);
                    if (Vector3.Dot(slide, delta) >= 0f)
                    {
                        MoveActor(interp, actor, slide, UEInterpreter.AsRotator(actor.GetProp(PK.Rotation)),
                            bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false, flyHit);
                        float secondFraction = flyHit.Time;
                        if (secondFraction < 1f)
                        {
                            Vector3 secondNormal = flyHit.Normal;
                            ProcessHitWall(interp, actor, secondNormal, flyHit.Actor);
                            TwoWallAdjust(desiredDir, ref slide, secondNormal, hitNormal, secondFraction);
                            MoveActor(interp, actor, slide, UEInterpreter.AsRotator(actor.GetProp(PK.Rotation)),
                                bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false, flyHit);
                        }
                    }
                }
                else
                {
                    float beforeStepZ = UEInterpreter.AsVector3(actor.GetProp(PK.Location)).z;
                    StepUp(interp, actor, gravityDir, desiredDir, delta * (1f - fraction), flyHit);
                    Vector3 oldLocation = UEInterpreter.AsVector3(actor.GetProp(PK.OldLocation));
                    oldLocation.z = (oldLocation.z - beforeStepZ)
                        + UEInterpreter.AsVector3(actor.GetProp(PK.Location)).z;
                    actor.Props["OldLocation"] = oldLocation;
                }
            }

            // Tail (disasm 0x103f19b3-0x103f1a16): X in the x87 register, Y and Z
            // through float (see DisplacementVelocityX87).
            if (!UEInterpreter.ToBool(actor.GetProp(PK.bJustTeleported)))
                actor.Props["Velocity"] = DisplacementVelocityX87(
                    UEInterpreter.AsVector3(actor.GetProp(PK.Location)),
                    UEInterpreter.AsVector3(actor.GetProp(PK.OldLocation)), dt, 0);
            if (DetailedJournal || ReferenceEquals(actor, LocalPlayer))
                Journal?.Invoke($"flying_move actor={actor.GetProp(PK.Name)} from={location}"
                    + $" to={actor.GetProp(PK.Location)} velocity={actor.GetProp(PK.Velocity)}"
                    + $" acceleration={actor.GetProp(PK.Acceleration)} flow={flow}"
                    + $" blocked={(fraction < 1f).ToString().ToLowerInvariant()} fraction={fraction:R}");
        }
        readonly struct PhysicsTapEnd : IDisposable
        {
            readonly UELevelRuntime _rt; readonly UEObject _a; readonly int _p; readonly float _dt;
            public PhysicsTapEnd(UELevelRuntime rt, UEObject a, int p, float dt) { _rt = rt; _a = a; _p = p; _dt = dt; }
            public void Dispose() => PerformPhysicsTap?.Invoke(_rt, _a, _p, _dt, false);
        }

        // The performPhysics body — Core-owned (was Runtime.PhysicsHook): the shipped mode
        // dispatch plus the performPhysics tail (rotation toward DesiredRotation,
        // MoveTimer decrement, PostTouch). Runs only for Physics != PHYS_None.
        public void PerformPhysics(UEInterpreter interp, UEObject actor, int physics,
            float dt, Vector3 preStepVelocity)
        {
            PerformPhysicsTap?.Invoke(this, actor, physics, dt, true);
            using var _tap = new PhysicsTapEnd(this, actor, physics, dt);
            if (physics == 1)
                PhysWalking(interp, actor, dt);
            else if (physics == 2)
                PhysFalling(interp, actor, dt);
            else if (physics == 4 && UEInterpreter.ClassIsA(actor.Class, "Pawn"))
                PhysFlying(interp, actor, dt);
            else if (physics == 4)
            {
                // PHYS_Flying on a NON-pawn: base AActor::performPhysics
                // (Engine.dll.c:92695, switch this[0x30]) has NO case 4 — the mode falls
                // through the switch without translation; of all physics only the common
                // tail runs (physicsRotation with non-zero RotationRate + PostTouch).
                // physFlying exists ONLY in APawn (the only performPhysics overrides are
                // APawn:68659 and AInterpolationManager:38731). Live user —
                // FlipendoVaseBronzeShard shards (Lev_Tut1b): their scatter comes from the
                // rotation tail, not positional physics.
            }
            else if (physics == 5)
            {
                // PHYS_Rotating has NO case in either shipped performPhysics
                // switch (APawn Engine.dll.c:68660, AActor 92697): the mode only
                // keeps the actor physics-active so the rotation tail below runs.
            }
            else if (physics == 6)
                PhysProjectile(interp, actor, dt);
            else if (physics == 9 && UEInterpreter.ClassIsA(actor.Class, "Mover"))
                PhysMovingBrush(interp, actor, dt);
            else if (physics == 9 && UEInterpreter.ClassIsA(actor.Class, "Pawn"))
            {
                // PHYS_MovingBrush on a PAWN: the APawn::performPhysics switch
                // (Engine.dll.c:68671-68708) has no case 9 — neither OldLocation nor
                // Velocity; only the common pawn tail.
            }
            else if (physics == 9)
            {
                // PHYS_MovingBrush on a NON-mover (live user: 5 spellTrigger
                // on Lev2_Inc_A). Shipped AActor::performPhysics, case 9
                // (Engine.dll.c:92709-92719): OldLocation = Location, call
                // physMovingBrush, then Velocity = (Location − OldLocation)/dt.
                // physMovingBrush (92398) first walks the class chain to AMover and,
                // not finding it, RETURNS — so for a non-mover the mode reduces to
                // OldLocation = Location and zero velocity; then the common tail
                // (physicsRotation).
                Vector3 loc = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
                actor.Props["OldLocation"] = loc;
                actor.Props["Velocity"] = Vector3.zero;
            }
            else if (physics == 8 && UEInterpreter.ClassIsA(actor.Class, "InterpolationManager"))
                PhysInterpolating(interp, actor, dt);
            else if (physics == 8)
            {
                // PHYS_Interpolating on a NON-manager: the AActor::performPhysics switch
                // (Engine.dll.c:92695) has no case 8 — only the common tail
                // (physicsRotation). The only override is
                // AInterpolationManager::performPhysics (UELevelRuntime.Interpolation.cs).
            }
            else if (physics == 11)
                PhysTrailer(interp, actor);
            else if ((physics == 3 || physics == 10) && !UEInterpreter.ClassIsA(actor.Class, "Pawn"))
            {
                // PHYS_Swimming/PHYS_Spider on a NON-pawn: the AActor::performPhysics
                // switch (92687-92718) has no cases 3 and 10 — only the tail.
            }
            else
            {
                // STOP: physSwimming (3), physRolling (7), physSpider (10) are not
                // ported. physSwimming is UNREACHABLE in the campaign (measured):
                // Physics=3 is set only by SetPhysics(3) under a water-zone check
                // (Harry.uc:1880/2689, Engine.u PlayerPawn.uc:4628/5109/5391, Pawn.uc:667)
                // or in state PlayerSwimming, entered only from there (PlayerPawn.uc:
                // 5484); Engine.dll has no native write of Physics=3; all 226 zones
                // of 41 maps have effective bWaterZone=False (see PhysFalling).
                // SetPhysics(7|10) appears neither in Engine.u nor in package scripts.
                Journal?.Invoke($"physics_boundary actor={actor.GetProp(PK.Name)} mode={physics} location={actor.GetProp(PK.Location)} velocity={actor.GetProp(PK.Velocity)} acceleration={actor.GetProp(PK.Acceleration)} radius={actor.GetProp(PK.CollisionRadius)} height={actor.GetProp(PK.CollisionHeight)} maxstep={actor.GetProp(PK.MaxStepHeight)} region={actor.GetDeclared("Actor", "Region")}");
                throw new NotSupportedException(
                    $"TickPhysics mode={physics} actor={actor.GetProp(PK.Name)} class={actor.Class?.Name}");
            }

            // APawn::performPhysics tail (Engine.dll.c:68713-68731): after ANY
            // active physics mode a pawn turns toward DesiredRotation (players
            // always; others when Rotation differs or RotationRate.Roll > 0) and
            // MoveTimer decrements. PHYS_None pawns never reach performPhysics.
            // (PostTouch dispatch lives here too; PendingTouch is not tracked.)
            if (!actor.Destroyed)
            {
                if (UEInterpreter.ClassIsA(actor.Class, "Pawn"))
                {
                    UERotator rot = UEInterpreter.AsRotator(actor.GetProp(PK.Rotation));
                    UERotator desired = UEInterpreter.AsRotator(actor.GetProp(PK.DesiredRotation));
                    if (UEInterpreter.ClassIsA(actor.Class, "PlayerPawn")
                        || rot.Pitch != desired.Pitch || rot.Yaw != desired.Yaw || rot.Roll != desired.Roll
                        || UEInterpreter.AsRotator(actor.GetProp(PK.RotationRate)).Roll > 0)
                        PhysicsRotation(actor, dt, preStepVelocity);
                    UEPawnMovement.DecrementMoveTimer(actor, dt);
                }
                else
                {
                    // AActor::performPhysics (Engine.dll.c:92723-92726): any actor
                    // whose RotationRate has a non-zero uint16 component runs
                    // AActor::physicsRotation.
                    UERotator rate = UEInterpreter.AsRotator(actor.GetProp(PK.RotationRate));
                    if ((rate.Pitch & 0xffff) != 0 || (rate.Yaw & 0xffff) != 0 || (rate.Roll & 0xffff) != 0)
                        ActorPhysicsRotation(interp, actor, dt);
                }
                // PostTouch chain (APawn::performPhysics Engine.dll.c:68726-68737 /
                // AActor 92727-92735): fire PostTouch on the pending actor with us
                // as the toucher, then pop our link off its chain.
                if (actor.GetProp(PK.PendingTouch) is UEObject pending)
                {
                    UELifecycle.CallEvent(interp, pending, "PostTouch", actor);
                    if (actor.GetProp(PK.PendingTouch) is UEObject still)
                    {
                        actor.Props["PendingTouch"] = still.GetProp(PK.PendingTouch);
                        still.Props["PendingTouch"] = null;
                    }
                }
            }
        }

        // AActor::fixedTurn (Engine.dll.c:120438): step a 16-bit angle from cur toward
        // desired by at most ±maxDelta, shortest arc (physicsRotation always uses the
        // shortest-turn mode). maxDelta==0 → no turn this frame.
        // AActor::physicsRotation (Engine.dll.c:158362, ?physicsRotation@AActor@@QAEXM@Z):
        // gate on bRotateToDesired (0x1f8&4) OR bFixedRotationDir (&2), and when
        // rotating-to-desired skip once Rotation == DesiredRotation. Steps are
        // ROUND(RotationRate.{Pitch,Yaw,Roll} * dt); each component turns via the
        // flag-aware fixedTurn when its step is non-zero and (when bRTD) its
        // desired differs; a changed rotator commits through MoveActor(delta=0,
        // newRot) (vtable 0x8c); with bRTD and Rotation fully at Desired the
        // EndedRotation event fires when probed. Reached by rotating non-pawn
        // decor/pickups (the wand: its pickup state spins via
        // bFixedRotationDir).
        void ActorPhysicsRotation(UEInterpreter interp, UEObject actor, float dt)
        {
            bool rtd = UEInterpreter.ToBool(actor.GetProp(PK.bRotateToDesired));
            bool fixedDir = UEInterpreter.ToBool(actor.GetProp(PK.bFixedRotationDir));
            if (!rtd && !fixedDir) return;
            UERotator rot = UEInterpreter.AsRotator(actor.GetProp(PK.Rotation));
            UERotator desired = UEInterpreter.AsRotator(actor.GetProp(PK.DesiredRotation));
            if (rtd && rot.Pitch == desired.Pitch && rot.Yaw == desired.Yaw && rot.Roll == desired.Roll)
                return;
            UERotator rate = UEInterpreter.AsRotator(actor.GetProp(PK.RotationRate));
            int rollStep = (int)Math.Round(rate.Roll * dt, MidpointRounding.ToEven);
            int yawStep = (int)Math.Round(rate.Yaw * dt, MidpointRounding.ToEven);
            int pitchStep = (int)Math.Round(rate.Pitch * dt, MidpointRounding.ToEven);
            int pitch = rot.Pitch, yaw = rot.Yaw, roll = rot.Roll;
            if (yawStep != 0 && (!rtd || desired.Yaw != yaw))
                yaw = ActorFixedTurn(fixedDir, rtd, yaw, desired.Yaw, yawStep);
            if (pitchStep != 0 && (!rtd || desired.Pitch != pitch))
                pitch = ActorFixedTurn(fixedDir, rtd, pitch, desired.Pitch, pitchStep);
            if (rollStep != 0 && (!rtd || desired.Roll != roll))
                roll = ActorFixedTurn(fixedDir, rtd, roll, desired.Roll, rollStep);
            if (pitch != rot.Pitch || yaw != rot.Yaw || roll != rot.Roll)
                MoveActor(interp, actor, Vector3.zero, new UERotator(pitch, yaw, roll),
                    bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false);
            rot = UEInterpreter.AsRotator(actor.GetProp(PK.Rotation));
            if (rtd && rot.Pitch == desired.Pitch && rot.Yaw == desired.Yaw && rot.Roll == desired.Roll)
                UELifecycle.CallEvent(interp, actor, "EndedRotation");   // via ProcessEvent (wave B3)
        }

        // AActor::fixedTurn (Engine.dll.c:120438) — FLAG-AWARE:
        //   bFixedRotationDir && !bRotateToDesired → raw signed step (the
        //     constant pickup spin: (cur + step) & 0xffff);
        //   bFixedRotationDir && bRotateToDesired → one-DIRECTIONAL approach
        //     (the step's sign picks the direction, clamped at desired);
        //   bRotateToDesired only → the shortest-arc turn (the static FixedTurn
        //     below — the pawn path lands here after physicsRotation forces the
        //     flag state);
        //   neither → no turn (unreachable from the physicsRotation gate).
        private static int ActorFixedTurn(bool fixedDir, bool rotateToDesired,
            int cur, int desired, int step)
        {
            if (step == 0) return cur & 0xffff;
            int c = cur & 0xffff, d = desired & 0xffff;
            if (fixedDir)
            {
                if (!rotateToDesired) return (c + step) & 0xffff;
                if (step < 1)
                {
                    int cc = c < d ? c + 0x10000 : c;
                    int delta = d - cc;
                    if (delta <= step) delta = step;
                    return (c + delta) & 0xffff;
                }
                int dd = d < c ? d + 0x10000 : d;
                int gap = dd - c;
                if (step <= gap) return (c + step) & 0xffff;
                return (c + gap) & 0xffff;
            }
            if (rotateToDesired) return FixedTurn(cur, desired, step);
            return c;
        }

        private static int FixedTurn(int cur, int desired, int maxDelta)
        {
            cur &= 0xffff; desired &= 0xffff;
            if (maxDelta == 0) return cur;
            int m = Math.Abs(maxDelta);
            if (desired < cur)
            {
                int gap = cur - desired;
                if (gap > 0x7fff) { int d = (desired - cur) + 0x10000; return (cur + (m < d ? m : d)) & 0xffff; }
                if (m < gap) return (cur - m) & 0xffff;
                return (cur - gap) & 0xffff;
            }
            else
            {
                int gap = desired - cur;
                if (gap < 0x8000) return (cur + Math.Min(m, gap)) & 0xffff;
                int step = Math.Min(m, (cur - desired) + 0x10000);
                return (cur - step) & 0xffff;
            }
        }

        // APawn::physicsRotation (Engine.dll.c:64031): turn Rotation toward
        // DesiredRotation by RotationRate*dt (Yaw/Pitch via fixedTurn). Roll banking
        // is a loud boundary unless RotationRate.Roll<1 (then Roll=0).
        // APawn::physicsRotation (Engine.dll.c:64034): turn Pitch/Yaw toward
        // DesiredRotation at RotationRate*dt (raw-int inequality gates, 64094/
        // 64099), then bank Roll (64104-64201): rate<1 zeroes it; a slow walker or
        // small dv/dt decays it toward 0 with t=min(8dt,1); otherwise the target
        // bank is lateral(dv/dt)*maxBank/AccelRate (maxBank 4096 walking, 28000
        // else), clamped to ±RotationRate.Roll, blended at t=min(5dt,1) after
        // normalizing the current roll for continuity. Lateral = dv/dt projected
        // on the new-rotation world Y basis (the shipped GMath /= FRotator coords).
        public void PhysicsRotation(UEObject pawn, float dt, Vector3 oldVelocity)
        {
            UERotator rate = UEInterpreter.AsRotator(pawn.GetProp(PK.RotationRate));
            UERotator desired = UEInterpreter.AsRotator(pawn.GetProp(PK.DesiredRotation));
            UERotator rot = UEInterpreter.AsRotator(pawn.GetProp(PK.Rotation));
            pawn.Props["bFixedRotationDir"] = false; // 0x1f8 &~2 |4 (64092)
            pawn.Props["bRotateToDesired"] = true;
            int yaw = rot.Yaw, pitch = rot.Pitch, roll = rot.Roll;
            int yawStep = (int)Math.Round(rate.Yaw * dt, MidpointRounding.ToEven);
            if (desired.Yaw != rot.Yaw) yaw = FixedTurn(rot.Yaw, desired.Yaw, yawStep);
            int pitchStep = (int)Math.Round(rate.Pitch * dt, MidpointRounding.ToEven);
            if (desired.Pitch != rot.Pitch) pitch = FixedTurn(rot.Pitch, desired.Pitch, pitchStep);

            if (rate.Roll < 1)
            {
                roll = 0;
            }
            else
            {
                // x87 chains per disasm 0x103e5a17-0x103e5d5e (PC64; the register is
                // double, the same convention as in neighbouring bodies).
                Vector3 velocity = UEInterpreter.AsVector3(pawn.GetProp(PK.Velocity));
                int physics = UEInterpreter.ToInt(pawn.GetProp(PK.Physics));
                // Walking pawn: (x² + y²) + z² in the register against 40000f; `test cl,ah`
                // (C0) — damping on "less" and on NaN (0x103e5a23-0x103e5a55).
                double vsq = ((double)velocity.x * velocity.x + (double)velocity.y * velocity.y)
                             + (double)velocity.z * velocity.z;
                bool decay = physics == 1 && !(vsq >= 40000.0);
                float dvx = 0f, dvy = 0f, dvz = 0f;
                if (!decay)
                {
                    // dv (0x103e5ac4-0x103e5b3f): 1/dt in the register plus its float copy;
                    // Z — difference and product in the register, X/Y — float differences
                    // multiplied by float(1/dt); |dv|² from register values against
                    // 10000f, damping on ≤ and NaN.
                    double inv = 1.0 / dt;
                    float invf = (float)inv;
                    double rz = ((double)velocity.z - oldVelocity.z) * inv;
                    double ry = (double)invf * (velocity.y - oldVelocity.y);
                    double rx = (double)invf * (velocity.x - oldVelocity.x);
                    dvx = (float)rx; dvy = (float)ry; dvz = (float)rz;
                    decay = !((rx * rx + ry * ry) + rz * rz > 10000.0);
                }
                if (decay)
                {
                    // t = dt·8 in the register, 1 when t ≥ 1 and on NaN; stored to float and fistp.
                    double t = (double)dt * 8.0;
                    if (!(1.0 > t)) t = 1.0;
                    roll = rot.Roll < 0x8000
                        ? (int)Math.Round((float)((1.0 - t) * rot.Roll), MidpointRounding.ToEven)
                        : (int)Math.Round((float)((0x10000 - rot.Roll) * t + rot.Roll), MidpointRounding.ToEven);
                }
                else
                {
                    float maxBank = physics == 1 ? 4096f : 28000f;
                    // Shipped frame: GMath identity /= FRotator(newPitch, newYaw,
                    // 0) — iStack_18 is zeroed right before the coords build
                    // (Engine.dll.c:64147). lateral is the Y component of the inlined
                    // TransformVectorBy (0x103e5ba7-0x103e5bfb) over the float copies of dv:
                    // (dv.x·Y.x + dv.y·Y.y) + dv.z·Y.z, stored to float.
                    BrushCoords frame = BrushCoords.Identity;
                    frame.DivRotator(pitch, yaw, 0);
                    float lateral = (float)(((double)dvx * frame.Y.x + (double)dvy * frame.Y.y)
                        + (double)dvz * frame.Y.z);
                    float accelRate = UEInterpreter.AsFloat(pawn.GetProp(PK.AccelRate));
                    // lateral·maxBank / AccelRate in the register (0x103e5c0d-0x103e5c21).
                    double target = (double)lateral * maxBank / accelRate;
                    int clamped;
                    if (!(lateral > 0f))
                    {
                        // `fcomp 0.0; test ah,0x41; jne` — ≤ 0 and NaN; +65536 in the register.
                        int tv = (int)Math.Round((float)(target + 65536.0), MidpointRounding.ToEven);
                        int floor = 0x10000 - rate.Roll;
                        clamped = floor < tv ? tv : floor;
                    }
                    else
                    {
                        int tv = (int)Math.Round((float)target, MidpointRounding.ToEven);
                        clamped = tv < rate.Roll ? tv : rate.Roll;
                    }
                    // 0x103e5c69-0x103e5ca5: Rotation.Roll IS WRITTEN to the actor — &0xffff,
                    // then ±0x10000 toward the target side — before blending and before the
                    // "rotation changed" comparison.
                    int cur = rot.Roll & 0xffff;
                    if (clamped <= 0x8000) { if (cur > 0x8000) cur -= 0x10000; }
                    else if (cur < 0x8000) cur += 0x10000;
                    rot = new UERotator(rot.Pitch, rot.Yaw, cur);
                    pawn.Props["Rotation"] = rot;
                    // (1 − t)·cur + clamped·t in the register, t = dt·5 (1 when ≥ 1/NaN).
                    double t = (double)dt * 5.0;
                    if (!(1.0 > t)) t = 1.0;
                    roll = (int)Math.Round((float)((1.0 - t) * cur + clamped * t), MidpointRounding.ToEven);
                }
            }
            var newRot = new UERotator(pitch, yaw, roll);
            if (newRot.Pitch != rot.Pitch || newRot.Yaw != rot.Yaw || newRot.Roll != rot.Roll)
                MoveActor(pawn.Interp, pawn, Vector3.zero, newRot, bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false);
        }

        // Mover key arrays are ArrayDim=8 static arrays: an UNSET slot is a
        // faithful UE1 zero, but a mover with no key property at all (class or
        // loader defect) must be loud, never a silent zero pose.
        private static Vector3 KeyVector(UEObject mover, string prop, int idx)
        {
            if (mover.GetProp(prop) is not System.Collections.IList list)
                throw new NotSupportedException(
                    $"mover key property {prop} missing on {mover.GetProp(PK.Name)}");
            return idx >= 0 && idx < list.Count ? UEInterpreter.AsVector3(list[idx]) : Vector3.zero;
        }

        private static UERotator KeyRotator(UEObject mover, string prop, int idx)
        {
            if (mover.GetProp(prop) is not System.Collections.IList list)
                throw new NotSupportedException(
                    $"mover key property {prop} missing on {mover.GetProp(PK.Name)}");
            return idx >= 0 && idx < list.Count
                ? UEInterpreter.AsRotator(list[idx]) : new UERotator(0, 0, 0);
        }

        // ── APawn::physMovingBrush integrator (Engine.dll.c:92396) — advance
        // PhysAlpha at PhysRate, ease with MoverGlideType (1 = smoothstep
        // 3t²−2t³, else linear), interpolate the pose from OldPos/OldRot toward
        // BasePos+KeyPos[KeyNum] / BaseRot+KeyRot[KeyNum], MoveActor to it.
        // Clear bInterpolating + fire KeyFrameReached on arrival; a blocked
        // partial move also stops.
        //
        // Core-owned. This lived in the headless frontend, which meant a
        // frontend adopting HP1.Core got the collision math but NOT the code
        // that moves doors and lifts — Core could only project a mover's state,
        // never advance it. Moved verbatim (the body was already plain C# over
        // Core objects, ported from the decompile); only the journal sink and
        // the event call were rebound to Core's own. ──
        public void PhysMovingBrush(UEInterpreter interp, UEObject actor, float dt)
        {
            Vector3 startLoc = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
            actor.Props["OldLocation"] = startLoc;

            if (!UEInterpreter.ToBool(actor.GetProp(PK.bInterpolating)))
            {
                actor.Props["Velocity"] = Vector3.zero;
                if (DetailedJournal)
                    Journal?.Invoke($"moving_brush_idle actor={actor.GetProp(PK.Name)} location={startLoc}");
                return;
            }

            // Shipped has NO PhysRate gate (disasm 0x10406261-0x104065c3): the loop head is
            // only bInterpolating (0x1f8&8) and dt > 0; PhysRate is read once, in the alpha
            // formula (0x104065bd). With PhysRate <= 0 alpha does not grow, but the mover
            // is posed at the current alpha (bCollideWorld fall too), and with
            // PhysAlpha >= 1 KeyFrameReached fires.
            float physRate = UEInterpreter.AsFloat(actor.GetProp(PK.PhysRate));
            // KeyNum — ONCE before the loop (this[699], 92481-92495); BasePos,
            // OldPos, BaseRot, OldRot, KeyPos/KeyRot[k] and the smoothing type
            // shipped reads LIVE on every iteration (92590-92620): a KeyFrameReached
            // script calling InterpolateTo rewrites OldPos/OldRot/PhysAlpha, and the
            // rest of the tick runs from the new OldPos — but toward the key whose
            // index was taken before the loop.
            int keyNum = Math.Clamp(UEInterpreter.ToInt(actor.GetProp(PK.KeyNum)), 0, 7);
            if (HP1.Core.UETrace.Enabled)
                HP1.Core.UETrace.Log($"mover_interp actor={actor.GetProp(PK.Name)}"
                    + $" keynum={keyNum} prevkeynum={actor.GetProp(PK.PrevKeyNum)}"
                    + $" alpha={actor.GetProp(PK.PhysAlpha)} loc={startLoc}");

            float remaining = dt;
            int iter = 0;
            while (UEInterpreter.ToBool(actor.GetProp(PK.bInterpolating)) && remaining > 0f && iter < 64)
            {
                iter++;
                // MOVER FALL WITH bCollideWorld (Engine.dll.c:92481-92525, HP1):
                // before the interpolation step the mover falls in its zone's ZoneGravity —
                // offset (V·g)/(g·g)·g·dt + ½·g·dt², Velocity += g·dt, MoveActor
                // with the current rotation. Blocked (Time <= 0) — FindBase; otherwise
                // the travelled distance is added to KeyPos[KeyNum] and OldPos, and
                // interpolation does not finish on this iteration (cStack_19). Live
                // original (hp1gridpush.c, Lev3_Dungeon): a GridMover pushed to the
                // chasm drops by 192 at interp=1 until it touches the bottom.
                bool fell = false;
                if (UEInterpreter.ToBool(actor.GetProp(PK.bCollideWorld))
                    && (actor.GetDeclared("Actor", "Region") as UEObject)?.GetProp(PK.Zone) is UEObject fallZone)
                {
                    Vector3 g = UEInterpreter.AsVector3(fallZone.GetProp(PK.ZoneGravity));
                    Vector3 v = UEInterpreter.AsVector3(actor.GetProp(PK.Velocity));
                    float along = g.x * v.x + g.y * v.y + g.z * v.z;
                    float inv = 1f / (g.z * g.z + g.y * g.y + g.x * g.x);
                    float half = remaining * remaining * 0.5f;
                    Vector3 fall = new(inv * along * g.x * remaining + half * g.x,
                                       inv * along * g.y * remaining + half * g.y,
                                       inv * along * g.z * remaining + half * g.z);
                    actor.Props["Velocity"] = new Vector3(remaining * g.x + v.x, remaining * g.y + v.y, remaining * g.z + v.z);
                    Vector3 before = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
                    // Its own FCheckResult(1.0) for EACH move (Engine.dll.c:92526-92531).
                    var fallHit = new MoveCheck();
                    MoveActor(interp, actor, fall, UEInterpreter.AsRotator(actor.GetProp(PK.Rotation)),
                        bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false, fallHit);
                    if (fallHit.Time <= 0f)
                        FindBase(interp, actor);
                    else
                    {
                        Vector3 moved = UEInterpreter.AsVector3(actor.GetProp(PK.Location)) - before;
                        if (actor.GetProp(PK.KeyPos) is System.Collections.IList kl && keyNum < kl.Count)
                            kl[keyNum] = KeyVector(actor, "KeyPos", keyNum) + moved;
                        actor.Props["OldPos"] = UEInterpreter.AsVector3(actor.GetProp(PK.OldPos)) + moved;
                        fell = true;
                    }
                }
                float physAlpha = UEInterpreter.AsFloat(actor.GetProp(PK.PhysAlpha));
                float newAlpha = remaining * physRate + physAlpha;
                if (newAlpha <= 1f) remaining = 0f;
                else if (physAlpha >= 1f) { remaining = 0f; newAlpha = 1f; }
                else { remaining = (newAlpha - 1f) / (newAlpha - physAlpha) * remaining; newAlpha = 1f; }
                bool glide = UEInterpreter.ToInt(actor.GetProp(PK.MoverGlideType)) == 1;
                float t = glide ? 3f * newAlpha * newAlpha - 2f * newAlpha * newAlpha * newAlpha : newAlpha;

                Vector3 basePos = UEInterpreter.AsVector3(actor.GetProp(PK.BasePos));
                Vector3 oldPos = UEInterpreter.AsVector3(actor.GetProp(PK.OldPos));
                UERotator baseRot = UEInterpreter.AsRotator(actor.GetProp(PK.BaseRot));
                UERotator oldRot = UEInterpreter.AsRotator(actor.GetProp(PK.OldRot));
                Vector3 keyPos = KeyVector(actor, "KeyPos", keyNum);
                UERotator keyRot = KeyRotator(actor, "KeyRot", keyNum);

                Vector3 targetPos = oldPos + (basePos + keyPos - oldPos) * t;
                var targetRot = new UERotator(
                    oldRot.Pitch + (int)Math.Round((baseRot.Pitch + keyRot.Pitch - oldRot.Pitch) * t, MidpointRounding.ToEven),
                    oldRot.Yaw + (int)Math.Round((baseRot.Yaw + keyRot.Yaw - oldRot.Yaw) * t, MidpointRounding.ToEven),
                    oldRot.Roll + (int)Math.Round((baseRot.Roll + keyRot.Roll - oldRot.Roll) * t, MidpointRounding.ToEven));

                Vector3 curLoc = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
                // Its own FCheckResult(1.0) for EACH move (Engine.dll.c:92586-92591):
                // a standing mover (fast path) and a refusal read Time = 1. PhysAlpha
                // grows only if MoveActor RETURNED 1 (92638-92640): an encroachment
                // refusal and bStatic/!bMovable do not advance alpha.
                var interpHit = new MoveCheck();
                bool moveOk = MoveActor(interp, actor, targetPos - curLoc, targetRot,
                    bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false, interpHit);
                float frac = interpHit.Time;
                if (moveOk) actor.Props["PhysAlpha"] = physAlpha + (newAlpha - physAlpha) * frac;
                if (fell) continue;
                if (frac >= 1f)
                {
                    if (UEInterpreter.AsFloat(actor.GetProp(PK.PhysAlpha)) >= 1f)
                    {
                        actor.Props["bInterpolating"] = false;
                        if (HP1.Core.UETrace.Enabled)
                            HP1.Core.UETrace.Log($"mover_key_reached actor={actor.GetProp(PK.Name)}"
                                + $" keynum={keyNum} loc={actor.GetProp(PK.Location)}");
                        if (HP1.Core.UEPlaythroughLog.Enabled)
                            HP1.Core.UEPlaythroughLog.Note("mover",
                                $"reached:{actor.GetProp(PK.Name)}:{keyNum}",
                                $"key_reached actor={actor.GetProp(PK.Name)} keynum={keyNum}");
                        UELifecycle.CallEvent(interp, actor, "KeyFrameReached");
                    }
                }
                else
                {
                    actor.Props["bInterpolating"] = false; // blocked mid-move
                    if (HP1.Core.UETrace.Enabled)
                        HP1.Core.UETrace.Log($"mover_blocked actor={actor.GetProp(PK.Name)}"
                            + $" keynum={keyNum} frac={frac} loc={actor.GetProp(PK.Location)}");
                    if (HP1.Core.UEPlaythroughLog.Enabled)
                        HP1.Core.UEPlaythroughLog.Note("mover",
                            $"blocked:{actor.GetProp(PK.Name)}:{keyNum}",
                            $"BLOCKED actor={actor.GetProp(PK.Name)} keynum={keyNum} frac={frac}");
                }
            }
            // AActor::performPhysics, PHYS_MovingBrush (disasm 0x103e5351-0x103e539e):
            // Z in the x87 register, X and Y through float (DisplacementVelocityX87).
            actor.Props["Velocity"] = dt > 0f
                ? DisplacementVelocityX87(UEInterpreter.AsVector3(actor.GetProp(PK.Location)), startLoc, dt, 2)
                : Vector3.zero;
            Journal?.Invoke($"moving_brush actor={actor.GetProp(PK.Name)} location={actor.GetProp(PK.Location)} alpha={actor.GetProp(PK.PhysAlpha)} interp={actor.GetProp(PK.bInterpolating)}");
        }

        // ── AActor::physFalling (Engine.dll.c:84522-85423) — the FULL shipped
        // body, one state machine for every frontend (lifted from the
        // validated headless port; geometry rides WalkMoveHook/LineTraceHook).
        //  - zone-0 → FellOutOfWorld with the falling-mode Logf filter
        //    (85230-85251), stop.
        //  - Pawn air-control prepass (84759-84833): AirControl>0.15 runs a
        //    cylinder clearance trace along the predicted lateral travel
        //    (flags 6) — ANY block clamps AirControl to 0.05; the lateral
        //    acceleration budget is AirControl·AccelRate, raised by
        //    (10−speed2D)/dt below 10 u/s, collapsed to 1 at/above
        //    GroundSpeed with clamped AirControl; at/above GroundSpeed with
        //    LIVE AirControl the 2D speed is capped at its current value
        //    (the fVar1 dance, 84806-84816). Acceleration.z zeroed.
        //  - Substeps (84846-84863): remaining<=0.1 whole, else
        //    min(remaining/2, 0.1), 8 iterations; per substep the midpoint
        //    velocity V + (Gravity+Accel)·sub/2 (water: fluid drag + the
        //    buoyancy-scaled gravity; bBobbing Decoration: half gravity;
        //    player with wet feet: the FootRegion drag), the apex split
        //    (84981-85005), the 2D cap, ZoneVelocity (skipped for a non-
        //    player pawn unless |ZoneVel|²>40000, 85175-85192), ONE
        //    committed move.
        //  - A floor hit (Normal.z>0.7) gives the unused time back,
        //    re-derives Velocity from the actual displacement and lands
        //    through the full processLanded (85265-85285). A wall runs
        //    APawn::Mount + processHitWall + the slide; a second wall runs
        //    processHitWall + TwoWallAdjust; slide/adjust landings call
        //    processLanded with ZERO remaining time (shipped 85320/85345 —
        //    no walking continuation after a slide landing).
        //  - The tail (85380-85410) re-derives the velocity from the
        //    displacement minus ZoneVelocity, doubles it against the
        //    pre-substep velocity when the z-gain fell short (the midpoint
        //    integrator's second half), and clamps to ZoneTerminalVelocity.
        //  - bBounce (fork at 85264, body 85370-85379): the hit is NOT treated as a
        //    landing or a wall — the engine sends the script HitWall, exits on
        //    PHYS_None, gives the unused time back for the first two hits per call
        //    and leaves the velocity alone in the tail (85381 `& 0x11`).
        //    The bounce is set by script, not by the engine.
        // Un-reached shipped branches stay LOUD STOPs, checked on EVERY
        // substep: the water-zone formula, the bBobbing Decoration formula, the
        // PlayerPawn wet-feet formula, and the swim entry (startSwimming).
        // ZoneVelocity and bBounce are ported. ──
        // MoveActor 0x103aa58c..0x103aa65d: every |component| below 0.0001f (a NaN counts as below) -> dist 0, direction =
        // Delta; otherwise dist = appSqrt((Z·Z + Y·Y) + X·X) stored as float, r = float(1/dist), direction = float(c·r).
        internal static (float dist, Vector3 dir) MoveActorDirection(Vector3 d)
        {
            static bool Small(float c) => !(Math.Abs(c) >= 0.0001f);
            if (Small(d.x) && Small(d.y) && Small(d.z)) return (0f, d);
            if (!float.IsFinite(d.x) || !float.IsFinite(d.y) || !float.IsFinite(d.z)) { float m = d.magnitude; return (m, d / m); }
            float dist = HP1.Core.X87Ext.Sqrt64(HP1.Core.X87Ext.From(Vector3.SizeSquared64(d.z, d.y, d.x).ToDouble())).ToFloat();
            float r = 1f / dist;   // fld 1.0; fdiv dword; fstp dword: one rounding of the quotient
            return (dist, new Vector3(d.x * r, d.y * r, d.z * r));
        }

        // MoveActor 0x103aaafe..0x103aab5e (Time < 1, not bNoFail): t' = ((pad + dist)·T − pad) / dist in the register;
        // t' <= 0.0001f (C0|C3: also NaN, and dist 0) refuses the move; Time = float(t'); the delta is scaled by the
        // register t' per component. Returns false on refusal.
        internal static bool MoveActorRebase(float pad, float dist, float time, Vector3 delta, out float stored, out Vector3 scaled)
        {
            stored = 0f; scaled = delta;
            if (dist == 0f) return false;   // (pad·T − pad) <= 0 over 0: −inf or NaN, both refused
            if (!float.IsFinite(pad) || !float.IsFinite(dist) || !float.IsFinite(time)
                || !float.IsFinite(delta.x) || !float.IsFinite(delta.y) || !float.IsFinite(delta.z))
            {
                float t0 = ((pad + dist) * time - pad) / dist;
                if (!(t0 > 0.0001f)) return false;
                stored = t0; scaled = delta * t0; return true;
            }
            static HP1.Core.X87Ext X(float v) => HP1.Core.X87Ext.From(v);
            static HP1.Core.X87Ext R(HP1.Core.X87Ext v) => HP1.Core.X87Ext.Round64(v);
            HP1.Core.X87Ext t = HP1.Core.X87Ext.Div64(R(R(R(X(pad) + X(dist)) * X(time)) - X(pad)), X(dist));
            if (!(t.CompareTo(X(0.0001f)) > 0)) return false;
            stored = t.ToFloat();
            scaled = new Vector3(R(t * X(delta.x)).ToFloat(), R(t * X(delta.y)).ToFloat(), R(t * X(delta.z)).ToFloat());
            return true;
        }
    }
}
