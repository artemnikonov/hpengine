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
        // Original: Engine.dll ?physTrailer@AActor@@QAEXM@Z [HP1 0x103f5f80] [HP2 0x10402470]
        public void PhysTrailer(UEInterpreter interp, UEObject actor)
        {
            UEObject owner = actor.GetProp(PK.Owner) as UEObject;
            if (owner is null || owner.Destroyed) return;
            // The placement of a non-sprite trailer (bone or owner frame). HP1: FarMove(bNoCheck), then a zero-delta MoveActor
            // with the rotation. HP2 [0x10402844..0x104029ae]: a trailer that collides with the world or blocks actors is
            // FarMoved and then moved by (0.01, 0, 0) with the rotation; otherwise one MoveActor by NewLocation − Location.
            void Place(Vector3 location, UERotator rotation)
            {
                if (UEGameProfile.Current.Game == UEEdition.Game.HP2
                    && !(UEInterpreter.ToBool(actor.GetProp(PK.bCollideWorld)) || UEInterpreter.ToBool(actor.GetProp(PK.bBlockActors))))
                {
                    Vector3 cur = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
                    MoveActor(interp, actor, new Vector3(location.x - cur.x, location.y - cur.y, location.z - cur.z), rotation,
                        bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false);
                    return;
                }
                FarMove(interp, actor, location, bTest: false, bNoCheck: true);
                MoveActor(interp, actor, UEGameProfile.Current.Game == UEEdition.Game.HP2 ? new Vector3(0.01f, 0f, 0f) : Vector3.zero,
                    rotation, bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false);
            }

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
                Place(bone.Origin, boneRotation);
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

            Place(newLocation, newRotation);
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

        // Actor.AutonomousPhysics (native 3971). Original: AActor::execAutonomousPhysics 0x103e4b40: each Acceleration
        // component `fld; fmul 10.0f (0x104737f4); fstp dword; fld; fistp; fild; fmul 0.1f (0x104737fc); fstp`
        // (0x103e4b72..0x103e4bdd), then Physics != PHYS_None (0x103e4bd2) -> performPhysics(DeltaSeconds) (vtable +0xa8).
        public void AutonomousPhysics(UEInterpreter interp, UEObject actor, float deltaSeconds)
        {
            static float Tenths(float c) => HP1.Core.X87Ext.Round64(
                HP1.Core.X87Ext.From(MeshTriangleOrder.Fistp(c * 10f)) * HP1.Core.X87Ext.From(0.1f)).ToFloat();
            Vector3 a = UEInterpreter.AsVector3(actor.GetProp(PK.Acceleration));
            actor.Props["Acceleration"] = new Vector3(Tenths(a.x), Tenths(a.y), Tenths(a.z));
            int physics = UEInterpreter.ToInt(actor.GetProp(PK.Physics));
            if (physics != 0)
                PerformPhysics(interp, actor, physics, deltaSeconds, UEInterpreter.AsVector3(actor.GetProp(PK.Velocity)));
        }

        // The performPhysics body — Core-owned (was Runtime.PhysicsHook): the shipped mode
        // dispatch plus the performPhysics tail (rotation toward DesiredRotation,
        // MoveTimer decrement, PostTouch). Runs only for Physics != PHYS_None.
        // Original: Engine.dll ?performPhysics@APawn@@UAEXM@Z [HP1 0x103e5520] [HP2 0x103f2b80]
        // Original: Engine.dll ?performPhysics@AActor@@UAEXM@Z [HP1 0x103e52c0] [HP2 0x103f2740]
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
                PhysInterpolating(interp, actor, dt);   // both games: HP2's lines are profile branches inside
            else if (physics == 8)
            {
                // PHYS_Interpolating on a NON-manager: the AActor::performPhysics switch
                // (Engine.dll.c:92695) has no case 8 — only the common tail
                // (physicsRotation). The only override is
                // AInterpolationManager::performPhysics (UELevelRuntime.Interpolation.cs).
            }
            else if (physics == 11)
                PhysTrailer(interp, actor);
            else if (physics == 3 && UEInterpreter.ClassIsA(actor.Class, "Pawn"))
                PhysSwimming(interp, actor, dt, 0);
            else if (physics == 7)
                PhysRolling(interp, actor, dt, 0);   // case 7 in both switches (APawn calls AActor::physRolling)
            else if (physics == 10 && UEInterpreter.ClassIsA(actor.Class, "Pawn"))
                PhysSpider(interp, actor, dt, 0);
            else if (physics == 3 || physics == 10)
            {
                // PHYS_Swimming/PHYS_Spider on a NON-pawn: the AActor::performPhysics
                // switch (92687-92718) has no cases 3 and 10 — only the tail.
            }
            else
                throw new NotSupportedException(
                    $"TickPhysics mode={physics} actor={actor.GetProp(PK.Name)} class={actor.Class?.Name}");

            // APawn::performPhysics tail (Engine.dll.c:68713-68731): after the mode
            // a pawn turns toward DesiredRotation (players always; others when
            // Rotation differs or RotationRate.Roll > 0) unless its Physics, re-read
            // after the mode, is PHYS_Spider (0x103e55e5 `cmp byte [esi+0x30], 0xa; je`),
            // and MoveTimer decrements. PHYS_None pawns never reach performPhysics.
            // (PostTouch dispatch lives here too; PendingTouch is not tracked.)
            // HP2 [0x103f2c45..0x103f2cc8]: the step smoothing first (Hp2SmoothStep), PHYS_Interpolating skips the turn
            // too, and a non-player turns when (Rotation != DesiredRotation && bRotateToDesired) or (!bRotateToDesired
            // && bFixedRotationDir) — RotationRate.Roll no longer counts.
            if (!actor.Destroyed)
            {
                if (UEInterpreter.ClassIsA(actor.Class, "Pawn"))
                {
                    bool hp2 = UEGameProfile.Current.Game == UEEdition.Game.HP2;
                    if (hp2) Hp2SmoothStep(actor);
                    UERotator rot = UEInterpreter.AsRotator(actor.GetProp(PK.Rotation));
                    UERotator desired = UEInterpreter.AsRotator(actor.GetProp(PK.DesiredRotation));
                    int physicsAfter = UEInterpreter.ToInt(actor.GetProp(PK.Physics));
                    bool differs = rot.Pitch != desired.Pitch || rot.Yaw != desired.Yaw || rot.Roll != desired.Roll;
                    bool rtd = UEInterpreter.ToBool(actor.GetProp(PK.bRotateToDesired));
                    bool turn = UEInterpreter.ClassIsA(actor.Class, "PlayerPawn") || (hp2
                        ? (differs && rtd) || (!rtd && UEInterpreter.ToBool(actor.GetProp(PK.bFixedRotationDir)))
                        : differs || UEInterpreter.AsRotator(actor.GetProp(PK.RotationRate)).Roll > 0);
                    if (physicsAfter != 10 && !(hp2 && physicsAfter == 8) && turn)
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

        // HP2's step smoothing of a walking pawn: SavedPrePivotZ (read by USkeletalMesh::GetMeshCoords [HP2 0x10429a96])
        // decays by s = Min(|(Location - OldLocation).XY| · StepHeight / StepWidth, StepHeight) toward 0 and takes
        // ±(StepHeight - s) when the pawn moved up or down by more than StepHeight - 1 and less than (float)(StepHeight + 1).
        // Every step at PC64 as the disassembly: dx, dy, the sum stored as a double for appSqrt, the root, ·StepHeight,
        // /StepWidth in the register; compares on the register value (`fst` then `fcomp`).
        // Original: Engine.dll not exported: FUN_103f29a0 [HP2 0x103f29a0] (HP2 only; APawn::performPhysics calls it through
        // the thunk 0x10302cfc after the mode switch [HP2 0x103f2c45])
        internal static void Hp2SmoothStep(UEObject pawn)
        {
            const int C0 = X87Ext.C0, C3 = X87Ext.C3;
            if (UEInterpreter.ToInt(pawn.GetProp(PK.Physics)) != 1) { pawn.Props["SavedPrePivotZ"] = 0f; return; }
            Vector3 loc = UEInterpreter.AsVector3(pawn.GetProp(PK.Location)), old = UEInterpreter.AsVector3(pawn.GetProp(PK.OldLocation));
            float stepH = UEInterpreter.AsFloat(pawn.GetProp("StepHeight")), stepW = UEInterpreter.AsFloat(pawn.GetProp("StepWidth"));
            X87Ext dx = R64(X87(loc.x) - X87(old.x)), dy = R64(X87(loc.y) - X87(old.y));
            X87Ext s = X87Ext.Sqrt64(X87Ext.From(R64(R64(dy * dy) + R64(dx * dx)).ToDouble()));   // 0x103f29af..0x103f29db
            s = X87Ext.Div64(R64(s * X87(stepH)), X87(stepW));                                     // 0x103f29e1..0x103f29ea
            if ((X87Ext.Fcom(s, X87(stepH)) & (C0 | C3)) == 0) s = X87(stepH);                    // `test ah, 0x41; jne`
            float spz = UEInterpreter.AsFloat(pawn.GetProp("SavedPrePivotZ"));
            if ((X87Ext.Fcom(X87(spz), X87(0f)) & C0) != 0)                                       // < 0 or NaN: 0x103f2a18
            {
                X87Ext v = R64(s + X87(spz));
                spz = v.ToFloat();
                if ((X87Ext.Fcom(v, X87(0f)) & (C0 | C3)) == 0) spz = 0f;                           // passed 0: 0x103f2a63
            }
            else if ((X87Ext.Fcom(X87(spz), X87(0f)) & (C0 | C3)) == 0)                            // > 0: 0x103f2a48
            {
                X87Ext v = R64(X87(spz) - s);
                spz = v.ToFloat();
                if ((X87Ext.Fcom(v, X87(0f)) & C0) != 0) spz = 0f;
            }
            float band = R64(X87(stepH) + X87(1f)).ToFloat();                                     // fstp [esp+8]
            X87Ext lo = R64(X87(stepH) - X87(1f));
            X87Ext down = R64(X87(old.z) - X87(loc.z));                                            // 0x103f2a6d
            if ((X87Ext.Fcom(down, X87(band)) & C0) != 0 && (X87Ext.Fcom(down, lo) & (C0 | C3)) == 0)
                spz = R64(X87(stepH) - s).ToFloat();                                               // 0x103f2aab
            X87Ext up = R64(X87(loc.z) - X87(old.z));                                              // 0x103f2abd
            if ((X87Ext.Fcom(up, X87(band)) & C0) != 0 && (X87Ext.Fcom(up, lo) & (C0 | C3)) == 0)
                spz = (-R64(X87(stepH) - s)).ToFloat();                                            // fchs, 0x103f2af3
            pawn.Props["SavedPrePivotZ"] = spz;
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
        // Original: Engine.dll ?physicsRotation@APawn@@QAEXMVFVector@@@Z [HP1 0x103e5950] [HP2 0x103f2fe0]
        public void PhysicsRotation(UEObject pawn, float dt, Vector3 oldVelocity)
        {
            UERotator rate = UEInterpreter.AsRotator(pawn.GetProp(PK.RotationRate));
            UERotator desired = UEInterpreter.AsRotator(pawn.GetProp(PK.DesiredRotation));
            UERotator rot = UEInterpreter.AsRotator(pawn.GetProp(PK.Rotation));
            if (UEGameProfile.Current.Game == UEEdition.Game.HP2)
            {
                // HP2 [0x103f3022..0x103f304b]: !bRotateToDesired && bFixedRotationDir -> AActor::physicsRotation(DeltaTime)
                // and return; the flags are never forced.
                if (!UEInterpreter.ToBool(pawn.GetProp(PK.bRotateToDesired)) && UEInterpreter.ToBool(pawn.GetProp(PK.bFixedRotationDir)))
                {
                    ActorPhysicsRotation(pawn.Interp, pawn, dt);
                    return;
                }
            }
            else
            {
                pawn.Props["bFixedRotationDir"] = false; // 0x1f8 &~2 |4 (64092)
                pawn.Props["bRotateToDesired"] = true;
            }
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

        // HP2's bCollideWorld mover fall: KeyPos / OldPos add Location − before as the register holds it.
        // Original: Engine.dll ?physMovingBrush@AActor@@QAEXM@Z [HP2 0x104137a0] (the fall's key update [HP2 0x10413a48..0x10413aae])
        internal static float Hp2MoverFallAdd(float after, float before, float v) => R64(R64(X87(after) - X87(before)) + X87(v)).ToFloat();

        // HP2's mover target Z: ((KeyPos + BasePos − OldPos)·t + OldPos) − Location in the register, one fstp.
        // Original: Engine.dll ?physMovingBrush@AActor@@QAEXM@Z [HP2 0x104137a0] (the target's Z [HP2 0x10413dbc..0x10413e68])
        internal static float Hp2MoverTargetZ(float keyZ, float baseZ, float oldZ, float t, float locZ)
            => R64(R64(R64(R64(R64(X87(keyZ) + X87(baseZ)) - X87(oldZ)) * X87(t)) + X87(oldZ)) - X87(locZ)).ToFloat();

        // HP2's spring glide (MoverGlideType 2): the mover reaches the key at s0 = (MoveTime − MoverSpringTime) / MoveTime of
        // the alpha (s0 outside (0, 1) is 0.75), then overshoots back in MoverFluctuations half-waves of width
        // w = (1 − s0) / 2n, each a linear dip to 1 − amp / 2^i at its centre c = s0 + (2i + 1)·w (amp outside (0, 1) is 0.1).
        // Every step as the disassembly: c in the register with its float copy, lo = c − w unrounded, hi = float(c + w), the
        // compares against the float alpha, each t one fstp.
        // Original: Engine.dll ?physMovingBrush@AActor@@QAEXM@Z [HP2 0x104137a0] (the spring glide [HP2 0x10413b94..0x10413cf7])
        internal static float Hp2SpringAlpha(float a, float moveTime, float springTime, float maxAmplitude, byte fluctuations)
        {
            const int C0 = X87Ext.C0, C3 = X87Ext.C3;
            static X87Ext D(double v) => X87Ext.From(v);
            float s0 = X87Ext.Div64(R64(X87(moveTime) - X87(springTime)), X87(moveTime)).ToFloat();
            float amp = maxAmplitude;
            int n = fluctuations == 0 ? 1 : fluctuations;
            if (!((X87Ext.Fcom(X87(s0), D(0.0)) & (C0 | C3)) == 0 && (X87Ext.Fcom(X87(s0), D(1.0)) & C0) != 0)) s0 = 0.75f;
            if (!((X87Ext.Fcom(X87(amp), D(0.0)) & (C0 | C3)) == 0 && (X87Ext.Fcom(X87(amp), D(1.0)) & C0) != 0)) amp = 0.1f;
            if ((X87Ext.Fcom(X87(a), X87(s0)) & (C0 | C3)) != 0) return X87Ext.Div64(X87(a), X87(s0)).ToFloat();   // a <= s0, NaN
            float w = X87Ext.Div64(R64(D(1.0) - X87(s0)), X87Ext.From(n) + X87Ext.From(n)).ToFloat();
            for (int i = 0; i < n; i++)
            {
                X87Ext c = R64(R64(R64(X87Ext.From(i) + X87Ext.From(i) + D(1.0)) * X87(w)) + X87(s0));
                float cf = c.ToFloat();
                X87Ext lo = R64(c - X87(w));
                float hi = R64(X87(cf) + X87(w)).ToFloat();
                X87Ext ai = X87Ext.Div64(X87(amp), X87Ext.From(1 << i));
                float aif = ai.ToFloat();
                float slope = X87Ext.Div64(ai, X87(w)).ToFloat();
                int aboveLo = X87Ext.Fcom(X87(a), lo) & (C0 | C3), toC = X87Ext.Fcom(X87(a), X87(cf)) & (C0 | C3);
                if (aboveLo == 0 && toC != 0)                                  // lo < a <= c: the falling half
                    return R64(R64(R64(D(1.0) - R64(X87(slope) * X87(a))) - X87(aif)) + R64(X87(slope) * X87(cf))).ToFloat();
                if (toC != 0) continue;                                         // a <= c (or NaN) and not above lo
                if ((X87Ext.Fcom(X87(a), X87(hi)) & (C0 | C3)) == 0) continue;  // a > hi
                return R64(R64(R64(R64(X87(slope) * X87(a)) + D(1.0)) - X87(aif)) - R64(X87(slope) * X87(cf))).ToFloat();
            }
            return 1f;
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
        // Original: Engine.dll ?physMovingBrush@AActor@@QAEXM@Z [HP1 0x104061f0] [HP2 0x104137a0]
        public void PhysMovingBrush(UEInterpreter interp, UEObject actor, float dt)
        {
            bool hp2 = UEGameProfile.Current.Game == UEEdition.Game.HP2;
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
                        Vector3 after = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
                        Vector3 moved = after - before;
                        // HP2 [0x10413a48..0x10413aae]: KeyPos[KeyNum] and OldPos add Location − before as the register
                        // holds it (fsub, then fadd of the stored value); HP1 adds the float difference.
                        Vector3 Add(Vector3 v) => hp2
                            ? new Vector3(Hp2MoverFallAdd(after.x, before.x, v.x), Hp2MoverFallAdd(after.y, before.y, v.y),
                                Hp2MoverFallAdd(after.z, before.z, v.z))
                            : v + moved;
                        if (actor.GetProp(PK.KeyPos) is System.Collections.IList kl && keyNum < kl.Count)
                            kl[keyNum] = Add(KeyVector(actor, "KeyPos", keyNum));
                        actor.Props["OldPos"] = Add(UEInterpreter.AsVector3(actor.GetProp(PK.OldPos)));
                        fell = true;
                    }
                }
                float physAlpha = UEInterpreter.AsFloat(actor.GetProp(PK.PhysAlpha));
                float newAlpha = remaining * physRate + physAlpha;
                if (newAlpha <= 1f) remaining = 0f;
                else if (physAlpha >= 1f) { remaining = 0f; newAlpha = 1f; }
                else { remaining = (newAlpha - 1f) / (newAlpha - physAlpha) * remaining; newAlpha = 1f; }
                int glideType = UEInterpreter.ToInt(actor.GetProp(PK.MoverGlideType));
                if (hp2 && glideType == 2)
                {
                    // HP2 [0x10413b29..0x10413b61]: the spring glide only toward the last key, at rest, or back to key 0
                    int prevKey = UEInterpreter.ToInt(actor.GetProp(PK.PrevKeyNum)) & 0xff, key = UEInterpreter.ToInt(actor.GetProp(PK.KeyNum)) & 0xff;
                    if (prevKey < key && key != (UEInterpreter.ToInt(actor.GetProp("NumKeys")) & 0xff) - 1) glideType = 0;
                    else if (prevKey > key && key != 0) glideType = 0;
                }
                bool glide = glideType == 1;
                float t = glide ? 3f * newAlpha * newAlpha - 2f * newAlpha * newAlpha * newAlpha
                    : hp2 && glideType == 2 ? Hp2SpringAlpha(newAlpha, UEInterpreter.AsFloat(actor.GetProp("MoveTime")),
                        UEInterpreter.AsFloat(actor.GetProp("MoverSpringTime")), UEInterpreter.AsFloat(actor.GetProp("MoverMaxAmplitude")),
                        (byte)(UEInterpreter.ToInt(actor.GetProp("MoverFluctuations")) & 0xff))
                    : newAlpha;

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
                Vector3 delta = targetPos - curLoc;
                // HP2 [0x10413dbc..0x10413e68]: Z runs (KeyPos + BasePos − OldPos)·t + OldPos − Location in the register,
                // one rounding at the end; X and Y are stored at each step as in HP1.
                if (hp2) delta.z = Hp2MoverTargetZ(keyPos.z, basePos.z, oldPos.z, t, curLoc.z);
                // Its own FCheckResult(1.0) for EACH move (Engine.dll.c:92586-92591):
                // a standing mover (fast path) and a refusal read Time = 1. PhysAlpha
                // grows only if MoveActor RETURNED 1 (92638-92640): an encroachment
                // refusal and bStatic/!bMovable do not advance alpha.
                var interpHit = new MoveCheck();
                bool moveOk = MoveActor(interp, actor, delta, targetRot,
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
                    if (hp2)
                    {
                        // HP2 [0x10413f0f..0x10413f48]: the blocking hit is kept in Mover.HitPosition / HitNormal
                        actor.Props["HitPosition"] = interpHit.Location;
                        actor.Props["HitNormal"] = interpHit.Normal;
                    }
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
        // The water-zone, bBobbing Decoration and PlayerPawn wet-feet formulas, the swim
        // entry (startSwimming), ZoneVelocity and bBounce are ported. ──
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
