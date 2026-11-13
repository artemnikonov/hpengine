// PHYS_Falling and the physics notifications around it: SetPhysics, ProcessLanded, ProcessHitWall,
// DispatchBump, FellOutOfWorld, base/owner-chain tests. Fields live in UELevelRuntime.cs.
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
        // physFalling 0x103eed2a..0x103eed5e and 0x103eee88: hs = appSqrt(Vx·Vx + Vy·Vy) in the register; `fcom 10.0` and
        // `fcom GroundSpeed` test C0 (`test ah, 1`): below 10 and "not at GroundSpeed" are C0, so an unordered hs (NaN)
        // counts as below both; the cap is hs stored as float (0x103eeeb7). NaN / ±inf run the same chain (X87Ext).
        internal static (float hs, bool below10, bool atGroundSpeed) FallingHorizontalSpeed(float vx, float vy, float groundSpeed)
        {
            HP1.Core.X87Ext e = Vector3.Size64Ext(vx, vy, 0f);
            return (e.ToFloat(), (HP1.Core.X87Ext.Fcom(e, HP1.Core.X87Ext.From(10f)) & HP1.Core.X87Ext.C0) != 0,
                (HP1.Core.X87Ext.Fcom(e, HP1.Core.X87Ext.From(groundSpeed)) & HP1.Core.X87Ext.C0) == 0);
        }

        // 0x103eee99..0x103eeea7: `fld AirControl; fcomp 0.05f; test ah, 0x41; je` keeps the cap only when AirControl > 0.05
        // (ordered); otherwise (also NaN) the budget collapses to 1.
        internal static bool FallingCapDropped(float airControl) => !(airControl > 0.05f);

        // 0x103eed64..0x103eed72: budget = ((10 − hs) / dt + budget) in the register, one fstp dword (dt = 0 divides to
        // ±inf or the indefinite, as the FPU with exceptions masked).
        internal static float FallingLowSpeedBudget(float vx, float vy, float budget, float dt)
        {
            var R = (Func<HP1.Core.X87Ext, HP1.Core.X87Ext>)HP1.Core.X87Ext.Round64;
            HP1.Core.X87Ext hs = Vector3.Size64Ext(vx, vy, 0f);
            return R(HP1.Core.X87Ext.Div64(R(HP1.Core.X87Ext.From(10f) - hs), HP1.Core.X87Ext.From(dt)) + HP1.Core.X87Ext.From(budget)).ToFloat();
        }

        // 0x103efc15..0x103efccc: Vx·Vx + Vy·Vy against cap·cap in the register (no sqrt); over it,
        // (Vx, Vy) = SafeNormal(Vx, Vy, 0) · cap, per-component fstp.
        internal static (float x, float y) FallingHorizontalCap(float vx, float vy, float cap)
        {
            if (!RegisterGreater(new Vector3(vx, vy, 0f), cap)) return (vx, vy);
            Vector3 c = new Vector3(vx, vy, 0f).normalized * cap;
            return (c.x, c.y);
        }

        // (X·X + Y·Y) + Z·Z > limit·limit with both sides in the register at PC64, as physFalling compares (0x103eed95,
        // 0x103efc57): st0 = limit·limit, st1 = the sum, `fcompp; test ah, 1` — C0, so an unordered side (NaN) counts as greater.
        internal static bool RegisterGreater(Vector3 v, float limit)
        {
            var l = HP1.Core.X87Ext.From(limit);
            return (HP1.Core.X87Ext.Fcom(l * l, Vector3.SizeSquared64(v.x, v.y, v.z)) & HP1.Core.X87Ext.C0) != 0;
        }

        public void PhysFalling(UEInterpreter interp, UEObject actor, float dt, int iteration = 0)
        {
            // (No hook requirement: like PhysWalking, this answers its own
            // geometry through WalkMove/LineTrace, which fall back to Core's
            // MoveActor/SingleLineCheck when no frontend override is set.)
            UEObject region = actor.GetProp(PK.Region) as UEObject;
            UEObject zone = region?.GetProp(PK.Zone) as UEObject;
            if (zone is null)
                throw new InvalidDataException(
                    $"Core PhysFalling missing zone actor={actor.GetProp(PK.Name)}");
            if (UEInterpreter.ToInt(region.GetProp(PK.ZoneNumber)) == 0)
            {
                FellOutOfWorld(interp, actor, falling: true);
                return;
            }

            Vector3 originalAcceleration = UEInterpreter.AsVector3(actor.GetProp(PK.Acceleration));
            Vector3 acceleration = originalAcceleration;
            bool pawn = UEInterpreter.ClassIsA(actor.Class, "Pawn");
            UEObject level = interp.GetLevelInfo();

            float airControl = UEInterpreter.AsFloat(actor.GetProp(PK.AirControl));
            // bBounce is ported (85374-85379).
            //
            // THIS BIT MUST NOT BE CACHED. Shipped reads it LIVE from actor memory at
            // EVERY point: at the hit-handling fork (85264, inside the substep loop) and
            // in the tail (85381) — AFTER the script HitWall has run. The event may clear
            // or set bBounce, change physics and velocity, and real campaign classes
            // have their own handlers (spellTrollRock, spellFireCracker, Jellybean,
            // WizzardCardIcon). A bool saved before the loop would pick the next
            // substep's branch from a stale value and wrongly skip the tail velocity
            // recomputation.
            // The STOP branches (water, bBobbing, player feet in water) are checked on
            // EVERY substep — where shipped picks the velocity formula (84873-84910),
            // see the loop below.

            float horizontalSpeedCap = 0f;
            if (pawn)
            {
                float effectiveAirControl = airControl;
                float accelRate = UEInterpreter.AsFloat(actor.GetProp(PK.AccelRate));
                if (effectiveAirControl > 0.15f)
                {
                    Vector3 predictedVelocity = UEInterpreter.AsVector3(actor.GetProp(PK.Velocity));
                    if (acceleration.sqrMagnitude > 0f)
                        predictedVelocity += acceleration.normalized
                            * (effectiveAirControl * accelRate);
                    Vector3 start = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
                    Vector3 end = start
                        + new Vector3(predictedVelocity.x * dt, predictedVelocity.y * dt, 0f);
                    float r = UEInterpreter.AsFloat(actor.GetProp(PK.CollisionRadius));
                    float h = UEInterpreter.AsFloat(actor.GetProp(PK.CollisionHeight));
                    (bool clearanceHit, _, _, _) = LineTrace(interp, actor, start, end,
                        new Vector3(r, r, h), 6);
                    if (clearanceHit) effectiveAirControl = 0.05f;
                }

                float maxAcceleration = effectiveAirControl * accelRate;
                acceleration.z = 0f;
                Vector3 velocity = UEInterpreter.AsVector3(actor.GetProp(PK.Velocity));
                // Disasm 0x103eed2a..0x103eed72: hs = appSqrt(Vx·Vx + Vy·Vy) stays in the register (PC64) for the
                // compares and for the budget (10 − hs) / dt + budget, rounded to float once.
                (float horizontalSpeed, bool below10, bool atGroundSpeed) = FallingHorizontalSpeed(velocity.x, velocity.y,
                    UEInterpreter.AsFloat(actor.GetProp(PK.GroundSpeed)));
                if (below10)
                    maxAcceleration = FallingLowSpeedBudget(velocity.x, velocity.y, maxAcceleration, dt);
                else if (atGroundSpeed)
                {
                    // Shipped 84806-84816: the cap survives only with LIVE
                    // AirControl; the clamped case collapses the budget to 1
                    // and drops the cap.
                    if (FallingCapDropped(effectiveAirControl)) maxAcceleration = 1f;
                    else horizontalSpeedCap = horizontalSpeed;
                }
                // 0x103eed77..0x103eeda2: (X·X + Y·Y) + Z·Z against budget·budget, both in the register.
                if (RegisterGreater(acceleration, maxAcceleration))
                    acceleration = acceleration.normalized * maxAcceleration;
                actor.Props["Acceleration"] = acceleration;
            }

            float remaining = dt;
            // bBounce hit counter. In shipped it is a stack slot (fStack_34) zeroed at
            // 84837 — BEFORE the substep loop — and incremented ONLY in the bounce branch
            // (85378). It lives for the whole PhysFalling call, not per substep: it limits
            // the time give-back to the first two hits per call (85375). The decompile
            // reuses the slot (float arithmetic before the loop), so it is counted from
            // here, not from the function start.
            int bounceHits = 0;
            // Body FCheckResult (uStack_84): FCheckResult(1.0) ONCE before the substep
            // loop (84837-84842: Time=1, Item=-1, Actor=NULL); all moves and Mount go into
            // it; the AirControl probe above uses ITS OWN local (uStack_27c). Disasm
            // (physFalling 0x103eea20): the record is [ebp-0x80], built at 0x103eee01..
            // 0x103eee23 before the loop head 0x103eee2d; MoveActor 0x103efe88/0x103f02ef/
            // 0x103f0474 and Mount 0x103f0122 pass `lea [ebp-0x80]`; the AirControl
            // SingleLineCheck 0x103eece6 passes [ebp-0x278]. The bounce counter is
            // [ebp-0x30]: zeroed 0x103eedfe, `cmp 2` 0x103effc0, `inc` 0x103effd8. A small move
            // and a MoveActor refusal leave it untouched, and the substep then handles the
            // PREVIOUS hit. A head-on wall hit gives almost zero slide: shipped sees the same
            // wall again — a second processHitWall and TwoWallAdjust (85318-85336).
            var fallHit = new MoveCheck();
            // fStack_38 (84845, 85007-85013, LAB_103efc0a): "apex already handled" flag.
            // Set as soon as the |vz|>5 thresholds pass (even if the halves are not longer
            // than 15 ms), and disables the apex check on the NEXT substep, which also
            // resets it. Form taken from the source: no distinguishing fixture input found.
            bool apexSeen = false;
            // The loop head is ONLY the remainder and the counter (disasm 0x103eee2d-
            // 0x103eee4b; three back edges 0x103f056e/0x103f0690/0x103f06d3).
            // Neither destruction nor a Physics change breaks the loop: a script that took
            // physics away from HitWall/processHitWall gets the remaining falling substeps
            // (exit on PHYS_None exists only in the bBounce branch, 85373, and on
            // bDeleteMe — after the move, 85197).
            while (remaining > 0f && iteration <= 7)
            {
                iteration++;
                float timeTick = remaining <= 0.1f ? remaining : Math.Min(remaining * 0.5f, 0.1f);
                remaining -= timeTick;

                Vector3 location = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
                Vector3 oldVelocity = UEInterpreter.AsVector3(actor.GetProp(PK.Velocity));
                actor.Props["OldLocation"] = location;
                actor.Props["bJustTeleported"] = false;

                // Zone, gravity and acceleration are read LIVE on every substep
                // (this+0xa0, +0x13c in each formula 84873-85144): a zone change by the move
                // or by script in HitWall changes them for the next substep.
                zone = (actor.GetProp(PK.Region) as UEObject)?.GetProp(PK.Zone) as UEObject
                    ?? throw new InvalidDataException(
                        $"Core PhysFalling missing zone actor={actor.GetProp(PK.Name)}");
                Vector3 gravity = UEInterpreter.AsVector3(zone.GetProp(PK.ZoneGravity));
                Vector3 liveAcceleration = UEInterpreter.AsVector3(actor.GetProp(PK.Acceleration));
                // Velocity formula choice (84873-84910; repeated at the apex 85029-85081):
                // zone water (0x2cc&1) → buoyancy and fluid friction; else
                // Decoration.bBobbing (0x260&8) → half gravity; else PlayerPawn with
                // FootRegion.Zone.bWaterZone and oldVz<0 → foot-zone friction. These three
                // branches are NOT ported — a loud STOP on every substep. Unreachable in the
                // campaign (measured): all 226 zones of 41 maps have effective
                // bWaterZone=False (classes ZoneInfo/SkyZoneInfo/WarpZoneInfo/LevelInfo
                // without a default, no map overrides; control — bPainZone=True on 46 zones
                // is visible in the same measurement); there are no bWaterZone writes in the
                // Engine.u decompile (reads only) or in package scripts, in Engine.dll only the
                // copy constructor (12391). Decoration.bBobbing is set only by
                // Decoration.ZoneChange in a water zone (Engine.u Decoration.uc:92).
                bool water = UEInterpreter.ToBool(zone.GetProp(PK.bWaterZone));
                bool bobbing = !water && UEInterpreter.ClassIsA(actor.Class, "Decoration")
                    && UEInterpreter.ToBool(actor.GetDeclared("Decoration", "bBobbing"));
                bool wetFeet = !water && !bobbing && UEInterpreter.ClassIsA(actor.Class, "PlayerPawn")
                    && oldVelocity.z < 0f
                    && UEInterpreter.ToBool(((actor.GetProp(PK.FootRegion) as UEObject)?.GetProp(PK.Zone) as UEObject)
                        ?.GetProp(PK.bWaterZone));
                if (water || bobbing || wetFeet)
                    throw new NotSupportedException(
                        $"Core PhysFalling STOP branch (Engine.dll.c:84873-84910) actor={actor.GetProp(PK.Name)} water={water} bobbing={bobbing} wetFeet={wetFeet}");

                Vector3 force = gravity + liveAcceleration;
                Vector3 midpointVelocity = oldVelocity + force * (0.5f * timeTick);

                // Apex split (Engine.dll.c:84981-85005): both halves must
                // exceed 15 ms.
                if (!apexSeen && (0f < oldVelocity.z) != (0f < midpointVelocity.z)
                    && Math.Abs(oldVelocity.z) > 5f && Math.Abs(midpointVelocity.z) > 5f)
                {
                    apexSeen = true;
                    float first = Math.Abs(oldVelocity.z)
                        / (Math.Abs(oldVelocity.z) + Math.Abs(midpointVelocity.z)) * timeTick;
                    float second = timeTick - first;
                    if (first > 0.015f && second > 0.015f)
                    {
                        remaining += second;
                        timeTick = first;
                        midpointVelocity = oldVelocity + force * (0.5f * timeTick);
                    }
                }
                else apexSeen = false;

                // Disasm 0x103efc15..0x103efccc: Vx·Vx + Vy·Vy against cap·cap in the register (no sqrt); over it,
                // (Vx, Vy) = SafeNormal(Vx, Vy, 0) · cap (per-component fstp), Z kept.
                if (horizontalSpeedCap != 0f)
                    (midpointVelocity.x, midpointVelocity.y) = FallingHorizontalCap(midpointVelocity.x, midpointVelocity.y, horizontalSpeedCap);

                actor.Props["Velocity"] = midpointVelocity;
                // Move ZoneVelocity (85175-85192, disasm 0x103efcd9-0x103efd92):
                // taken if the bIsPawn bit (Actor+0x28 & 0x2000000) is clear, or the actor
                // is a PlayerPawn, or |ZoneVelocity|² > 40000; otherwise zero.
                // The same vector is subtracted in the tail ([ebp-0x8c], 0x103f05e1).
                Vector3 zoneVelocity = UEInterpreter.AsVector3(zone.GetProp(PK.ZoneVelocity));
                if (UEInterpreter.ToBool(actor.GetProp(PK.bIsPawn))
                    && !UEInterpreter.ClassIsA(actor.Class, "PlayerPawn")
                    && !(zoneVelocity.sqrMagnitude > 40000f))
                    zoneVelocity = Vector3.zero;
                Vector3 delta = (zoneVelocity + midpointVelocity) * timeTick;
                (float hitFraction, Vector3 hitNormal, UEObject hitActor) =
                    WalkMove(interp, actor, delta, fallHit);
                Vector3 moved = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
                Log($"falling_move actor={actor.GetProp(PK.Name)} from={location} to={moved} velocity={actor.GetProp(PK.Velocity)} gravity={gravity} blocked={(hitFraction < 1f).ToString().ToLowerInvariant()} fraction={hitFraction:R} normal={hitNormal} hit={hitActor?.GetProp(PK.Name) ?? (object)"Level"}");
                if (actor.Destroyed) return;
                // 85210-85214: a pawn that moved into water (Physics==3) —
                // APawn::startSwimming with the remainder (1−Time)·dt, before hit handling.
                if (pawn && UEInterpreter.ToInt(actor.GetProp(PK.Physics)) == 3)
                    throw new NotSupportedException(
                        $"physFalling startSwimming reached (Engine.dll.c:85211) actor={actor.GetProp(PK.Name)} — port the shipped swim entry before using it");
                // 85215-85257: a DECORATION hitting a PlayerPawn decrements numLandings
                // (max(n−1, 0), bit trick 85255) — before the bBounce fork.
                if (hitFraction < 1f && hitActor is not null
                    && UEInterpreter.ClassIsA(hitActor.Class, "PlayerPawn")
                    && UEInterpreter.ClassIsA(actor.Class, "Decoration"))
                    actor.Props["numLandings"] = Math.Max(UEInterpreter.ToInt(actor.GetProp(PK.numLandings)) - 1, 0);

                if (hitFraction < 1f && UEInterpreter.ToBool(actor.GetProp(PK.bBounce)))
                {
                    // ── bBounce BRANCH (85370-85379) ─────────────────────────
                    //
                    // Shipped calls the SCRIPT HitWall event directly —
                    // FindFunctionChecked(_ENGINE_HitWall) + ProcessEvent — NOT
                    // processHitWall. The gates of the latter (exit on a pawn hitActor,
                    // the IsProbing check, the MoveTimer=-1 branch for pawns) do not run
                    // in this branch at all. Arguments as in the neighbour: normal, then
                    // the hit actor (85366-85369).
                    //
                    // There is NO delta reflection here, and that is not an omission:
                    // shipped does not reflect the move vector off the normal — the script
                    // sets the bounce from its own HitWall.
                    //
                    // All landing and wall handling is skipped under bBounce:
                    // no Mount, processHitWall, slide, TwoWallAdjust or processLanded
                    // (85264 is the fork).
                    Guard(() => UELifecycle.CallEvent(interp, actor, "HitWall",
                        hitNormal, hitActor ?? level), "HitWall");
                    // 85373: exit if the script moved physics to PHYS_None. Exactly
                    // PHYS_None (field Actor+0x30 == 0), not "anything but PHYS_Falling":
                    // for other values shipped continues the falling substeps — the loop
                    // head does not check Physics.
                    if (UEInterpreter.ToInt(actor.GetProp(PK.Physics)) == 0) return;
                    // 85375-85378: only the first two hits per call give the unused time
                    // back, but the counter always grows.
                    // `fld 1.0; fsub T; fmul dt; fadd rem; fstp` (0x103effc6-0x103effd5) —
                    // one rounding; the register is modelled by double.
                    if (bounceHits < 2) remaining = (float)((1.0 - hitFraction) * timeTick + remaining);
                    bounceHits++;
                }
                else if (hitFraction < 1f)
                {
                    if (hitNormal.z > 0.7f)
                    {
                        // Landed (85265-85285): the unused time comes back
                        // HERE (shipped gives it back only on the landed/
                        // swim/bounce branches, never for a wall slide).
                        // x87 (disasm 0x103efff4-0x103f00ce): remainder —
                        // `fld 1.0; fsub T; fmul dt; fadd rem; fstp`, one rounding; the 0.003f
                        // threshold is compared with T·dt from the register; velocity is
                        // d·(1/(T·dt)), where T·dt and the reciprocal never leave the register,
                        // d is float (FVector). The register is modelled by double.
                        remaining = (float)((1.0 - hitFraction) * timeTick + remaining);
                        if (!UEInterpreter.ToBool(actor.GetProp(PK.bJustTeleported))
                            && hitFraction > 0.1f && (double)hitFraction * timeTick > 0.003f)
                        {
                            Vector3 d = moved - location;
                            double rv = 1.0 / ((double)hitFraction * timeTick);
                            actor.Props["Velocity"] =
                                new Vector3((float)(d.x * rv), (float)(d.y * rv), (float)(d.z * rv));
                        }
                        ProcessLanded(interp, actor, hitNormal, hitActor ?? level,
                            remaining, iteration);
                        return;
                    }

                    // Wall: shipped calls APawn::Mount(Up, Hit) for every
                    // non-floor pawn contact and ignores its bool result,
                    // then continues through processHitWall/slide (85286).
                    if (pawn) TryMount(interp, actor, new Vector3(0f, 0f, 1f), fallHit);
                    ProcessHitWall(interp, actor, hitNormal, hitActor);
                    Vector3 slide = (delta - hitNormal * Vector3.Dot(delta, hitNormal))
                        * (1f - hitFraction);
                    if (Vector3.Dot(delta, slide) >= 0f)
                    {
                        Vector3 oldNormal = hitNormal;
                        (float secondFraction, Vector3 secondNormal, UEObject secondActor) =
                            WalkMove(interp, actor, slide, fallHit);
                        if (secondFraction < 1f)
                        {
                            if (secondNormal.z > 0.7f)
                            {
                                // Slide landing: ZERO remaining time (85320).
                                ProcessLanded(interp, actor, secondNormal,
                                    secondActor ?? level, 0f, iteration);
                                return;
                            }
                            ProcessHitWall(interp, actor, secondNormal, secondActor);
                            Vector3 adjusted = slide;
                            TwoWallAdjust(delta.normalized, ref adjusted, secondNormal,
                                oldNormal, secondFraction);
                            bool opposingUpSlopes = oldNormal.z > 0f && secondNormal.z > 0f
                                && adjusted.z == 0f && Vector3.Dot(oldNormal, secondNormal) < 0f;
                            (float thirdFraction, Vector3 finalNormal, UEObject thirdActor) =
                                WalkMove(interp, actor, adjusted, fallHit);
                            // 85336-85345: `bVar4 || Hit.Normal.z > 0.7` — WITHOUT Time < 1.
                            // A clean third move resets Hit (Time=1, Normal=0,
                            // Actor=NULL, 0x103aa504-0x103aa552), and opposing slopes
                            // still land — with a zero normal and Actor=NULL
                            // (setPhysics then calls FindBase).
                            if (opposingUpSlopes || finalNormal.z > 0.7f)
                            {
                                // Adjust landing: ZERO remaining time (85345).
                                ProcessLanded(interp, actor, finalNormal,
                                    thirdActor, 0f, iteration);
                                return;
                            }
                        }
                    }
                    // 85349-85362: a wall substep replaces the pre-substep
                    // velocity's xy with the ACTUAL travel over the substep
                    // (z untouched) before the tail's midpoint doubling.
                    Vector3 afterWalls = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
                    oldVelocity = new Vector3((afterWalls.x - location.x) / timeTick,
                        (afterWalls.y - location.y) / timeTick, oldVelocity.z);
                }

                // The tail (85380-85410): velocity from the actual movement
                // minus ZoneVelocity; the midpoint's second half doubles it
                // against the pre-substep velocity; ZoneTerminalVelocity
                // clamps the magnitude (an |V|² above TV² — shipped has no
                // zero guard).
                // 85381 checks BOTH bits at once (`& 0x11`): bBounce (0x1) and
                // bJustTeleported (0x10). A bounced actor must NOT get the velocity
                // recomputed from displacement, otherwise the engine would overwrite the
                // velocity the script HitWall just set.
                if (!UEInterpreter.ToBool(actor.GetProp(PK.bBounce))
                    && !UEInterpreter.ToBool(actor.GetProp(PK.bJustTeleported)))
                {
                    // FVector::operator/(dt) (0x103f05db → Core.dll 0x10119310): RV = 1/dt
                    // in x87, X·RV is rounded to float.
                    Vector3 derived = DivByFloat(UEInterpreter.AsVector3(actor.GetProp(PK.Location)) - location,
                        timeTick) - zoneVelocity;
                    if (derived.z < oldVelocity.z || oldVelocity.z >= 0f)
                        derived = derived * 2f - oldVelocity;
                    // The zone is the CURRENT one, after the move (0x103f0671: [esi+0xa0] is
                    // re-read in the tail; the move may have re-zoned the actor).
                    UEObject tailZone = (actor.GetProp(PK.Region) as UEObject)?.GetProp(PK.Zone) as UEObject ?? zone;
                    float terminal = UEInterpreter.AsFloat(tailZone.GetProp(PK.ZoneTerminalVelocity));
                    if (derived.sqrMagnitude > terminal * terminal)
                        derived = derived.normalized * terminal;
                    actor.Props["Velocity"] = derived;
                }
            }
            actor.Props["Acceleration"] = originalAcceleration;
        }

        // PUBLIC: the Cut* legacy half must apply the SAME reset at the SAME
        // shipped boundary — between the player events and the state frame
        // (128696 precedes ProcessState at 128703). The skeleton's own
        // localPlayer branch calls it below.
        public static void ResetPlayerInputAxes(UEObject actor)
        {
            if (actor.Class is null) return;
            if (!_inputFloatProps.TryGetValue(actor.Class, out List<string> names))
            {
                names = new List<string>();
                var seen = new HashSet<string>(UEPropNameComparer.Instance);
                for (var c = actor.Class; c != null; c = HP1.Core.UEClassRegistry.Shared.CanonicalSuper(c))
                    foreach (var p in c.EnumerateFields<UELib.Core.UProperty>())
                        if (p?.Name != null
                            && p.PropertyFlags.HasFlag(UELib.Flags.PropertyFlag.Input)
                            && p is UELib.Core.UFloatProperty
                            && seen.Add(p.Name.ToString()))
                            names.Add(p.Name.ToString());
                _inputFloatProps[actor.Class] = names;
            }
            foreach (string n in names) actor.Props[n] = 0f;
        }

        // ── AActor::setPhysics (Engine.dll.c:134160) — the shipped helper,
        // richer than a bare Physics()=newPhysics: for
        // the based physics modes it (re)finds/sets the Base, for the rest it
        // clears the Base, and it zeroes Velocity/Acceleration when the LIVE
        // Physics after the base change is None/Rotating. No-op if unchanged.
        // The base probe is Core's own FindBase. ──
        public void SetPhysics(UEInterpreter interp, UEObject actor, int newPhysics, UEObject newFloor)
        {
            if (UEInterpreter.ToInt(actor.GetProp(PK.Physics)) == newPhysics) return;
            actor.Props["Physics"] = (byte)newPhysics;
            // Based set {None(0), Walking(1), Rotating(5), Rolling(7), Spider(10)}.
            bool based = newPhysics is 0 or 1 or 5 or 7 or 10;
            if (based)
            {
                if (newFloor is null) FindBase(interp, actor);
                else if (!ReferenceEquals(actor.GetProp(PK.Base) as UEObject, newFloor))
                    SetBase(interp, actor, newFloor, true);
            }
            else if (actor.GetProp(PK.Base) is UEObject)
                SetBase(interp, actor, null, true);
            // Zeroing follows the LIVE Physics, re-read after SetBase/FindBase
            // (disasm 0x103e51c8: `mov al, byte [esi+0x30]`; 0 or 5 -> Velocity
            // +0x12c and Acceleration +0x13c = 0, else exit at 0x103e522b).
            // A BaseChange event inside SetBase may change Physics from script —
            // then the new value decides, not the argument.
            if (UEInterpreter.ToInt(actor.GetProp(PK.Physics)) is 0 or 5)
            {
                actor.Props["Velocity"] = Vector3.zero;
                actor.Props["Acceleration"] = Vector3.zero;
            }
        }

        // ── AActor::processLanded (Engine.dll.c:33689-34199) — the FULL
        // shipped body:
        //  1. A NON-pawn landing in a bBounceVelocity zone with a non-zero
        //     ZoneVelocity is kicked out with ZoneVelocity + (0,0,80) and
        //     stays falling (33874-33896).
        //  2. A Decoration with numLandings<5 runs four corner probes
        //     (±radius/2 offsets, 8 down, extent (r/2, r/2, h)); support on
        //     both diagonals with ≥2 corners re-launches it with the
        //     imbalance-vector tumble velocity and numLandings++ (33909-34110).
        //     A Carcass on a slope (Normal.z<0.9) with bSlidingCarcass slides
        //     off with Normal·120 (z=70), numLandings++ on appFrand<0.2
        //     (34112-34136). Otherwise numLandings resets.
        //  3. The Landed(HitNormal) event; if the script left the actor
        //     falling, a Pawn returns to Walking and anything else to None
        //     with Velocity zeroed — through the full shipped setPhysics with
        //     the landing floor actor (34150-34175).
        //  4. A Pawn that IS walking normalizes Acceleration and continues
        //     physWalking with the remaining substep time when it exceeds
        //     10 ms — the same tick (34177-34195). Callers without remaining
        //     time (the legacy player bridge) default to 0 → no continuation.
        // ──
        public void ProcessLanded(UEInterpreter interp, UEObject actor, Vector3 normal,
            UEObject floorActor, float remainingTime = 0f, int iteration = 0)
        {
            bool pawn = UEInterpreter.ClassIsA(actor.Class, "Pawn");
            UEObject zone = (actor.GetProp(PK.Region) as UEObject)?.GetProp(PK.Zone) as UEObject;
            if (!pawn && zone is not null && UEInterpreter.ToBool(zone.GetProp(PK.bBounceVelocity)))
            {
                Vector3 zoneVel = UEInterpreter.AsVector3(zone.GetProp(PK.ZoneVelocity));
                if (zoneVel != Vector3.zero)
                {
                    actor.Props["Velocity"] = zoneVel + new Vector3(0f, 0f, 80f);
                    return;
                }
            }
            // Carcass sliding is nested in `numLandings < 5` (34112-34146): with
            // numLandings >= 5 only the counter is reset.
            if (UEInterpreter.ClassIsA(actor.Class, "Decoration")
                && UEInterpreter.ToInt(actor.GetProp(PK.numLandings)) < 5)
            {
                if (LandedDecorationTumble(interp, actor))
                    return;
                if (UEInterpreter.ClassIsA(actor.Class, "Carcass") && normal.z < 0.9f
                    && UEInterpreter.ToBool(actor.GetProp(PK.bSlidingCarcass)))
                {
                    if (FrandHook is null)
                        throw new NotSupportedException(
                            $"processLanded Carcass slide reached (Engine.dll.c:34122) actor={actor.GetProp(PK.Name)} without FrandHook — wire the shared appFrand stream");
                    // 0x103ee1f2: fcomp dword 0.2 on st0 — exact comparison.
                    if (FrandHook().CompareTo(HP1.Core.X87Ext.From(0.2f)) < 0)
                        actor.Props["numLandings"] =
                            UEInterpreter.ToInt(actor.GetProp(PK.numLandings)) + 1;
                    actor.Props["Velocity"] = new Vector3(normal.x * 120f, normal.y * 120f, 70f);
                    return;
                }
                actor.Props["numLandings"] = 0;
            }
            else if (UEInterpreter.ClassIsA(actor.Class, "Decoration"))
                actor.Props["numLandings"] = 0;
            Guard(() => UELifecycle.CallEvent(interp, actor, "Landed", normal), "Landed");
            if (UEInterpreter.ToInt(actor.GetProp(PK.Physics)) == 2)
            {
                SetPhysics(interp, actor, pawn ? 1 : 0, floorActor);
                if (!pawn) actor.Props["Velocity"] = Vector3.zero;
            }
            // The falling-body call sites carry the substep bookkeeping; the
            // journal line sits between the physics fallout and the walking
            // continuation, exactly where the pre-lift headless host put it.
            if (remainingTime > 0f || iteration > 0)
                Log($"falling_landed actor={actor.GetProp(PK.Name)} physics={actor.GetProp(PK.Physics)}");
            if (pawn && UEInterpreter.ToInt(actor.GetProp(PK.Physics)) == 1)
            {
                Vector3 accel = UEInterpreter.AsVector3(actor.GetProp(PK.Acceleration));
                actor.Props["Acceleration"] = accel.sqrMagnitude > 0f ? accel.normalized : Vector3.zero;
                if (remainingTime > 0.01f)
                    PhysWalking(interp, actor, remainingTime, iteration);
            }
        }

        // The Decoration landing wobble (Engine.dll.c:33909-34110): a centre
        // POINT probe first (from 0.8·CollisionHeight below the centre, down
        // CollisionHeight+CollisionRadius+8) — a supported centre is stable
        // and skips the wobble. An unsupported centre runs four corner
        // probes (±CollisionRadius/2 offsets, 8 down, extent (r/2, r/2, h));
        // the corner counters are ULevel::SingleLineCheck answers (ULevel vtable +0xc4,
        // disasm 0x103ed964/0x103edb74/0x103edd82/0x103edf90), which returns
        // `Hit == NULL` (97953-98015): 1 when the path is CLEAR. So shipped counts
        // UNsupported corners: ≥2 clear corners on BOTH diagonals → numLandings++ and a
        // tumble velocity towards the clear corners (off the edge), scale
        // 2·clamp(−Velocity.z, 30, CollisionRadius+30). Returns true when the tumble
        // restarted the decoration.
        internal bool LandedDecorationTumble(UEInterpreter interp, UEObject actor)
        {
            // (The corner probes go through LineTrace, which answers from Core's
            // own SingleLineCheck when no frontend override is set.)
            Vector3 loc = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
            float r = UEInterpreter.AsFloat(actor.GetProp(PK.CollisionRadius));
            float h = UEInterpreter.AsFloat(actor.GetProp(PK.CollisionHeight));
            Vector3 centreStart = new(loc.x, loc.y, loc.z - h * 0.8f);
            Vector3 centreEnd = new(loc.x, loc.y, loc.z - (h + r + 8f));
            (bool centreHit, _, _, _) =
                LineTrace(interp, actor, centreStart, centreEnd, Vector3.zero, 7);
            if (centreHit) return false;
            // Corner probe extent (Engine.dll.c:33952-33956, call 0x103ed6a9):
            // e = GetCylinderExtent(this); (e.x·0.5, e.y·0.5, e.z·0.5 + e.z·0.5).
            // Corner offsets use the RAW CollisionRadius·0.5 (33957-33958).
            Vector3 cylExt = GetCylinderExtent(actor);
            float halfZ = cylExt.z * 0.5f;
            Vector3 extent = new(cylExt.x * 0.5f, cylExt.y * 0.5f, halfZ + halfZ);
            int Probe(float dx, float dy)
            {
                Vector3 start = new(loc.x + dx, loc.y + dy, loc.z);
                Vector3 end = new(start.x, start.y, start.z - 8f);
                (bool blocked, _, _, _) = LineTrace(interp, actor, start, end, extent, 7);
                return blocked ? 0 : 1;                  // SingleLineCheck: 1 = free
            }
            int t1 = Probe(r * 0.5f, r * 0.5f);
            int t2 = Probe(-r * 0.5f, r * 0.5f);
            int t3 = Probe(-r * 0.5f, -r * 0.5f);
            int t4 = Probe(r * 0.5f, -r * 0.5f);
            if (t1 + t2 + t3 + t4 <= 1 || t1 + t3 == 0 || t2 + t4 == 0) return false;
            actor.Props["numLandings"] = UEInterpreter.ToInt(actor.GetProp(PK.numLandings)) + 1;
            float imbX = (t1 + t4) - t2 - t3;
            float imbY = (t1 + t2) - t3 - t4;
            float k = 2f * Math.Clamp(-UEInterpreter.AsVector3(actor.GetProp(PK.Velocity)).z,
                30f, r + 30f);
            actor.Props["Velocity"] = new Vector3(imbX * k, imbY * k, k * 0.5f);
            return true;
        }

        // ── AActor::processHitWall (Engine.dll.c): a wall hit against a Pawn
        // fires nothing. A Pawn with no HitWall probe (and not falling) aborts
        // its move by setting MoveTimer=-1 so the next moveToward poll
        // completes; otherwise the HitWall(Normal, HitActor) event fires. ──
        public void ProcessHitWall(UEInterpreter interp, UEObject actor, Vector3 normal,
            UEObject hitActor)
        {
            // AActor::processHitWall (Engine.dll.c:161677-161770, disasm
            // 0x103ecf20-0x103ed13d):
            //  * hitting a pawn — nothing;
            //  * pawn: Acceleration == 0 → exit without event and MoveTimer;
            //    dir = SafeNormal(Destination − Location); for a walking pawn
            //    (Physics==1) the z of the normal AND of dir are zeroed — the event normal too
            //    (0x103ed04e writes the argument); dir·N > MinHitWall → exit;
            //    not IsProbing(HitWall) and not falling → MoveTimer=-1, bFromWall=1;
            //  * others: not IsProbing(HitWall) → exit;
            //  * HitWall(N, Wall) event through ProcessEvent.
            if (hitActor is not null && UEInterpreter.ClassIsA(hitActor.Class, "Pawn")) return;
            bool probing = UEInterpreter.IsProbing(actor, "HitWall");
            if (UEInterpreter.ClassIsA(actor.Class, "Pawn"))
            {
                if (UEInterpreter.AsVector3(actor.GetProp(PK.Acceleration)) == Vector3.zero) return;
                Vector3 dir = (UEInterpreter.AsVector3(actor.GetProp(PK.Destination))
                    - UEInterpreter.AsVector3(actor.GetProp(PK.Location))).normalized;
                int physics = UEInterpreter.ToInt(actor.GetProp(PK.Physics));
                if (physics == 1) { normal = new Vector3(normal.x, normal.y, 0f); dir = new Vector3(dir.x, dir.y, 0f); }
                double dot = (double)dir.z * normal.z + (double)normal.x * dir.x + (double)dir.y * normal.y;
                if (UEInterpreter.AsFloat(actor.GetProp(PK.MinHitWall)) < dot) return;
                if (!probing && physics != 2)
                {
                    actor.Props["MoveTimer"] = -1f;
                    actor.Props["bFromWall"] = true;
                    return;
                }
            }
            else if (!probing) return;
            Vector3 eventNormal = normal;
            Guard(() => UELifecycle.CallEvent(interp, actor, "HitWall", eventNormal, hitActor), "HitWall");
        }

        // ── MoveActor's actor-block Bump pair (Engine.dll.c:124148-124166):
        // both parties get Bump, hit actor first. The shipped gates live IN
        // the primitive so every frontend call site shares them: no pair for
        // the world sentinel (Actors[0]/LevelInfo, 124157) and none when the
        // mover is based — transitively — on the blocker (AActor::IsBasedOn,
        // 124158; a pawn must not Bump its own lift). ──
        public void DispatchBump(UEInterpreter interp, UEObject actor, UEObject blockActor)
        {
            if (actor is null || blockActor is null) return;
            if (ReferenceEquals(blockActor, interp.GetLevelInfo())) return;
            if (IsBasedOn(actor, blockActor)) return;
            Guard(() => UELifecycle.CallEvent(interp, blockActor, "Bump", actor), "Bump");
            Guard(() => UELifecycle.CallEvent(interp, actor, "Bump", blockActor), "Bump");
        }

        // AActor::IsBasedOn: walk the actor's Base chain (self included)
        // looking for the target.
        public static bool IsBasedOn(UEObject actor, UEObject target)
        {
            for (UEObject p = actor; p is not null; p = p.GetProp(PK.Base) as UEObject)
                if (ReferenceEquals(p, target)) return true;
            return false;
        }

        // The Owner-chain twin of IsBasedOn: ULevel::SingleLineCheck skips any
        // hit actor reachable from the source through Owner (98017-98021 walks
        // param_2 by +0x38 before the class branches), and SpawnActor's owner
        // filtering uses the same walk. Core-owned so the collision consumers
        // cannot drift apart on it.
        public static bool IsInOwnerChain(UEObject self, UEObject candidate)
        {
            for (UEObject current = self; current is not null;
                 current = current.GetProp(PK.Owner) as UEObject)
                if (ReferenceEquals(current, candidate)) return true;
            return false;
        }

        // Component-wise rotator identity — the shipped MoveActor rotation
        // comparison (123791-123819 tests Pitch/Yaw/Roll against the current
        // ones), NOT a tolerance compare.
        public static bool SameRotator(UERotator a, UERotator b)
            => a.Pitch == b.Pitch && a.Yaw == b.Yaw && a.Roll == b.Roll;

        // ── The zone-0 heads of the shipped pawn physics bodies — the actor
        // left the world. physWalking (Engine.dll.c:290948-290959): Logf when
        // Role==ROLE_Authority, then eventFellOutOfWorld, stop. physFalling
        // (85230-85251): the Logf additionally filters Inventory/Decoration/
        // Pawn; the event fires for EVERY actor, stop. (physFlying zone-0
        // destroys a non-player with a "flew out of the world" Logf and is a
        // silent exit for bIsPlayer; physProjectile destroys — those
        // policies stay at their shipped physics call sites.) ──
        public void FellOutOfWorld(UEInterpreter interp, UEObject actor, bool falling)
        {
            if (UEInterpreter.ToInt(actor.GetProp(PK.Role)) == 4
                && (!falling
                    || UEInterpreter.ClassIsA(actor.Class, "Inventory")
                    || UEInterpreter.ClassIsA(actor.Class, "Decoration")
                    || UEInterpreter.ClassIsA(actor.Class, "Pawn")))
                Log($"fell_out_of_world actor={actor.GetProp(PK.Name)} mode={(falling ? "falling" : "walking")}");
            Guard(() => UELifecycle.CallEvent(interp, actor, "FellOutOfWorld"), "FellOutOfWorld");
        }
    }
}
