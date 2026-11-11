// The shipped reachability family (Engine.dll): APawn::Reachable (the mode
// dispatcher, 102966-103006), APawn::pointReachable (73859-73906, native 521),
// APawn::actorReachable (62486-62633, native 520) and the three mode bodies
// walk/fly/swimReachable, which are mutually recursive and live here too.
//
// WIRED. The jump branch is no longer a STOP (SuggestJumpVelocity, jumpLanding
// and FindBestJump live in UELevelRuntime.Jump.cs and are diffed against the
// real shipped bodies under the emulator, gate step [jump]), and both entry
// points are registered by BOTH frontends: native 520 through
// ActorReachableNative, native 521 through PointReachable. Both are also
// VM-activated: 520 through the two shipped call sites inside
// Engine.Mover.HandleTriggerDoor, and 521 — which has no campaign caller at all
// — through its REAL declaration, Engine.u Pawn.pointReachable native(521),
// executed by the interpreter. "The campaign never calls it" stays measured and
// true (the coverage index lists only natives with >= 1 call and 521 is absent
// while 518/520/524 are present); it just is not the same question as whether
// the registered callback is reachable.
//
// Three properties of this family are easy to lose and are load-bearing:
//
//   * The result is NOT a bool. It is a bitmask of the modes that carried the
//     query — walk 1, fly 2, swim 4, jump 8 — folded as `success ? flags : 0`.
//     Callers can therefore ask HOW a point is reachable, not just whether.
//   * The bodies MOVE the pawn. A query walks the world up to 100 steps and each
//     mode body restores Location AND Velocity in its own tail. pointReachable
//     and actorReachable probe with a FarMove and adopt the spot they actually
//     reached, not the one they asked for.
//   * "My zone" is read from TWO DIFFERENT fields. The water gate reads Region,
//     the pain gate reads FootRegion. They are not interchangeable and the
//     asymmetry repeats in actorReachable (Pawn targets use the target's
//     FootRegion, everything else its Region).
//
// GIsEditor is 0 in the shipped game runtime, so the editor disjuncts below are
// constant-false there. They are kept as comments rather than code because the
// port has no editor mode — see the note at each site.
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        // 800² — the horizontal range gate shared by pointReachable and the
        // non-Pawn branch of actorReachable (Engine.dll.c:73865, 62511).
        const float ReachMaxRangeSquared = 640000f;

        // The tolerance pointReachable hands the dispatcher (0x41700000 at
        // Engine.dll.c:73900) and actorReachable's non-Pawn default (62593).
        const float ReachDefaultTolerance = 15f;

        // APawn::Reachable (Engine.dll.c:102966-103006). Mode selection only —
        // it computes nothing itself and forwards `tolerance` untouched.
        public int Reachable(UEInterpreter interp, UEObject self, Vector3 dest,
            float tolerance, UEObject target)
        {
            if (self is null) return 0;

            // Water WINS over physics: a pawn whose own Region zone is water
            // swims regardless of its Physics value (102980).
            UEObject myZone = ZoneOf(self, "Region");
            if (UEInterpreter.ToBool(myZone?.GetProp(PK.bWaterZone)))
                return SwimReachable(interp, self, dest, tolerance, 0, target);

            // PHYS_Walking (1) and PHYS_Falling (3) both walk; PHYS_Flying (4)
            // flies; EVERY other physics value is "unreachable" with no
            // computation at all (102998).
            switch (UEInterpreter.ToInt(self.GetProp(PK.Physics)))
            {
                case 1:
                case 3: return WalkReachable(interp, self, dest, tolerance, 0, target);
                case 4: return FlyReachable(interp, self, dest, tolerance, 0, target);
                default: return 0;
            }
        }

        // APawn::pointReachable (Engine.dll.c:73859-73906), native 521.
        public int PointReachable(UEInterpreter interp, UEObject self, Vector3 dest,
            bool bKnowVisible)
        {
            if (self is null) return 0;
            Vector3 loc = UEInterpreter.AsVector3(self.GetProp(PK.Location));

            // Range gate — HORIZONTAL ONLY, Z is not part of the distance
            // (73865). Shipped also passes when GIsEditor != 0; the port has no
            // editor mode, so only the range arm can be taken here.
            if (!PointReachInRange(dest, loc)) return 0;

            UEObject level = interp.GetLevelInfo();
            UEObject destZone = ResolveRegion(dest, level).Zone;

            // Water gate: refuse only when I am not ALREADY in water, cannot
            // swim, and the destination is water (73873).
            if (!UEInterpreter.ToBool(ZoneOf(self, "Region")?.GetProp(PK.bWaterZone))
                && !UEInterpreter.ToBool(self.GetProp(PK.bCanSwim))
                && UEInterpreter.ToBool(destZone?.GetProp(PK.bWaterZone)))
                return 0;

            // Pain gate — reads FootRegion, NOT Region (73876).
            if (!UEInterpreter.ToBool(ZoneOf(self, "FootRegion")?.GetProp(PK.bPainZone))
                && UEInterpreter.ToBool(destZone?.GetProp(PK.bPainZone))
                && !SameDamageType(destZone, self))
                return 0;

            // Visibility probe runs from the EYES, not the feet, and traces
            // FROM the destination (73882: FastLineCheck(dest, eye)).
            if (!bKnowVisible)
            {
                Vector3 eye = new Vector3(loc.x, loc.y,
                    loc.z + UEInterpreter.AsFloat(self.GetProp(PK.BaseEyeHeight)));
                if (Bsp is null)
                    throw new System.InvalidOperationException("pointReachable: level BSP not loaded — the shipped visibility check would be skipped silently (§8)");
                if (!Bsp.FastLineCheckClear(dest, eye)) return 0;
            }

            // Probe-and-restore (73889-73896). On a successful probe the query
            // adopts the spot actually REACHED, then puts the pawn back.
            if (FarMove(interp, self, dest, bTest: true, bNoCheck: false))
            {
                dest = UEInterpreter.AsVector3(self.GetProp(PK.Location));
                FarMove(interp, self, loc, bTest: true, bNoCheck: true);
            }

            return Reachable(interp, self, dest, ReachDefaultTolerance, null);
        }

        // APawn::actorReachable (Engine.dll.c:62486-62633), native 520.
        public int ActorReachable(UEInterpreter interp, UEObject self, UEObject target,
            bool bKnowVisible)
        {
            if (self is null || target is null) return 0;

            Vector3 loc = UEInterpreter.AsVector3(self.GetProp(PK.Location));
            Vector3 targetLoc = UEInterpreter.AsVector3(target.GetProp(PK.Location));
            Vector3 delta = targetLoc - loc;
            // 0x103dbac4..0x103dbb1a: differences and (dx² + dy²) + dz² in the register, one fstp dword. FULL 3D here.
            float dist2 = DistSq87(targetLoc, loc).ToFloat();

            bool targetIsPawn = UEInterpreter.ClassIsA(target.Class, "Pawn");
            if (targetIsPawn)
            {
                // Pawn targets: pain read from the TARGET's FootRegion, and the
                // 800-unit range gate is SKIPPED entirely (62500-62508).
                UEObject footZone = ZoneOf(target, "FootRegion");
                if (UEInterpreter.ToBool(footZone?.GetProp(PK.bPainZone))
                    && !SameDamageType(footZone, self))
                    return 0;
            }
            else
            {
                // Non-Pawn targets keep the range gate and read pain from the
                // target's Region (62511-62517). Shipped guards both on
                // GIsEditor == 0, which always holds in the game runtime.
                if (dist2 > ReachMaxRangeSquared) return 0;
                UEObject regionZone = ZoneOf(target, "Region");
                if (UEInterpreter.ToBool(regionZone?.GetProp(PK.bPainZone))
                    && !SameDamageType(regionZone, self))
                    return 0;
            }

            // Shared water gate (62520). NOTE: unlike pointReachable there is no
            // "I am already in water" disjunct — being in water does not excuse
            // a non-swimmer here.
            if (UEInterpreter.ToBool(ZoneOf(target, "Region")?.GetProp(PK.bWaterZone))
                && !UEInterpreter.ToBool(self.GetProp(PK.bCanSwim)))
                return 0;

            // Line probe from the EYES (62527-62541). Shipped runs the ACTOR-AWARE
            // ULevel::SingleLineCheck with flags 6 (Movers | Level, Pawns
            // deliberately excluded) and accepts a hit ON THE TARGET ITSELF —
            // only something else in the way blocks. A BSP-only check cannot make
            // that distinction: it would either miss a blocking mover entirely or,
            // once movers were added, refuse a target that is itself the mover.
            if (!bKnowVisible)
            {
                Vector3 eye = new Vector3(loc.x, loc.y,
                    loc.z + UEInterpreter.AsFloat(self.GetProp(PK.BaseEyeHeight)));
                // Shipped passes `this` as the chain root (62533). The walk goes
                // UPWARDS — self, self.Owner, self.Owner.Owner … — and a candidate
                // is skipped only when it appears IN that chain. So this excludes
                // the pawn's OWNERS, not the actors the pawn owns: something whose
                // Owner is the pawn is NOT in the chain and still blocks.
                // Passing null skipped nothing at all, so the pawn's own owner
                // could refuse its reachability.
                LineCheckHit probe = SingleLineCheck(interp, self, ownerToSkip: self,
                    start: eye, end: targetLoc, extent: Vector3.zero, traceFlags: 6);
                if (probe.Time != 1f && !ReferenceEquals(probe.Actor, target))
                    return 0;
            }

            float tolerance;
            if (targetIsPawn)
            {
                // Melee envelope: min(CollisionRadius * 1.5, MeleeRange) plus
                // both radii. The tolerance stays FLOAT: the decompile types param_2
                // as int and writes `(int)(...)` (62549), but the disasm has
                // `fst dword [ebp+0xc]` (0x103dbd35) without fistp/_ftol.
                // Inside the envelope the answer is a bare 1 — the dispatcher is
                // never consulted (62556).
                if (ActorReachMelee(UEInterpreter.AsFloat(self.GetProp(PK.CollisionRadius)),
                        UEInterpreter.AsFloat(self.GetProp(PK.MeleeRange)),
                        UEInterpreter.AsFloat(target.GetProp(PK.CollisionRadius)), dist2, out tolerance))
                    return 1;

                // Far Pawn targets restate the goal as "get within 800 units"
                // (62562-62568), again without _ftol.
                if (dist2 > ReachMaxRangeSquared) tolerance = ActorReachFar(delta, tolerance);
            }
            else
            {
                tolerance = ReachDefaultTolerance;
                // Inventory and Trigger targets shrink the tolerance to the
                // touching envelope (62620 via LAB_103dbed0). Also float:
                // `fstp dword [ebp+0xc]` (0x103dbee2), no truncation.
                if (UEInterpreter.ClassIsA(target.Class, "Inventory")
                    || UEInterpreter.ClassIsA(target.Class, "Trigger"))
                    tolerance = (float)(((double)UEInterpreter.AsFloat(target.GetProp(PK.CollisionRadius))
                        + UEInterpreter.AsFloat(self.GetProp(PK.CollisionRadius))) - 2.0);
            }

            // Probe-and-restore, identical to pointReachable (62571-62583).
            Vector3 dest = targetLoc;
            if (FarMove(interp, self, targetLoc, bTest: true, bNoCheck: false))
            {
                dest = UEInterpreter.AsVector3(self.GetProp(PK.Location));
                FarMove(interp, self, loc, bTest: true, bNoCheck: true);
            }

            // A target that does not block actors must not lend its extents to
            // the mode bodies, so the reference is DROPPED — unless it is a
            // WarpZoneMarker (62612-62618).
            UEObject dispatchTarget = target;
            if (!targetIsPawn
                && !UEInterpreter.ToBool(target.GetProp(PK.bBlockActors))
                && !UEInterpreter.ClassIsA(target.Class, "WarpZoneMarker"))
                dispatchTarget = null;

            return Reachable(interp, self, dest, tolerance, dispatchTarget);
        }

        // actorReachable 0x103dbd05..0x103dbd47: reach = min(R·1.5f, MeleeRange) (`fcom MeleeRange; test ah,0x41`: the
        // register value only when not above), + target R + own R in the register; `fst` stores the float tolerance
        // and `fmul` multiplies the UNROUNDED sum by that float copy; dist2 not above it -> inside the envelope.
        // NaN: `test ah,0x41` reads C0|C3, which an unordered compare sets — a NaN reach is kept, a NaN dist2 is inside.
        internal static bool ActorReachMelee(float radius, float melee, float targetRadius, float dist2, out float tolerance)
        {
            HP1.Core.X87Ext reach = X87(radius) * X87(1.5f);
            if ((HP1.Core.X87Ext.Fcom(reach, X87(melee)) & 0x41) == 0) reach = X87(melee);
            HP1.Core.X87Ext sum = R64(R64(reach + X87(targetRadius)) + X87(radius));
            tolerance = sum.ToFloat();
            return (HP1.Core.X87Ext.Fcom(X87(dist2), R64(sum * X87(tolerance))) & 0x41) != 0;
        }

        // 0x103dbd71..0x103dbdac: appSqrt((dy·dy + dz·dz) + dx·dx) of the stored float differences (fstp qword,
        // fsqrt PC64), − 800f in the register; replaces the tolerance unless below it, fstp dword.
        // pointReachable range gate 0x103dc1c5..0x103dc1e6: only dx is stored (fstp dword); dy stays in the register;
        // dy² + dx² in the register against 640000f; `test ah,0x41; je fail` (0x103dc1ee..0x103dc1f3): only an ordered
        // "above" fails, NaN is in range.
        internal static bool PointReachInRange(Vector3 dest, Vector3 loc)
        {
            HP1.Core.X87Ext dy = R64(X87(dest.y) - X87(loc.y));
            HP1.Core.X87Ext dx = X87(R64(X87(dest.x) - X87(loc.x)).ToFloat());
            return (HP1.Core.X87Ext.Fcom(R64(R64(dy * dy) + R64(dx * dx)), X87(ReachMaxRangeSquared)) & 0x41) != 0;
        }

        // walkReachable per-iteration compares 0x103dd1d4..0x103dd223 (dx, dy are the stored floats):
        // horiz2 = dy² + dx² stays in the register; the slope gate (dz − heightTol)² · 0.8 (qword 0x10479df0) in the
        // register against it; arrival horiz2 <= tolerance² (float [ebp-0x78]); a full step when not horiz2 < step²
        // (float [ebp-0x80]). Returns false when the slope gate breaks the loop. Status-word masks as tested: the gate is
        // entered on an ordered dz > heightTol and breaks on an ordered over²·0.8 > horiz2 (`test ah,0x41`); arrival is
        // `test ah,0x41; jne` (not above, NaN included); the full step is `test ah,1; je` (C0 clear: not below, NaN not).
        internal static bool WalkReachCompares(float dx, float dy, float dz, float heightTol, float tolSq, float stepSq,
            out bool arrived, out bool fullStep)
        {
            HP1.Core.X87Ext h2 = R64(R64(X87(dy) * X87(dy)) + R64(X87(dx) * X87(dx)));
            arrived = (HP1.Core.X87Ext.Fcom(h2, X87(tolSq)) & 0x41) != 0;
            fullStep = (HP1.Core.X87Ext.Fcom(h2, X87(stepSq)) & HP1.Core.X87Ext.C0) == 0;
            if ((HP1.Core.X87Ext.Fcom(X87(dz), X87(heightTol)) & 0x41) != 0) return true;
            HP1.Core.X87Ext over = R64(X87(dz) - X87(heightTol));
            return (HP1.Core.X87Ext.Fcom(R64(R64(over * over) * HP1.Core.X87Ext.From(0.8)), h2) & 0x41) != 0;
        }

        // walkReachable ramp 0x103dd570..0x103dd594 (and 0x103dd5e5..0x103dd609): 1/(nz·nz) − 1.0f in the register,
        // fstp qword, appSqrt, fstp qword [ebp-0x94]. nz = 0: the masked zero divide gives +inf, tan = +inf; NaN stays.
        // Both fits compares are `fcompp; test ah,0x41`: success only on an ordered "above" (NaN fails).
        internal static double WalkRampTan(float nz)
        {
            HP1.Core.X87Ext a = R64(HP1.Core.X87Ext.Div64(X87(1f), X87(nz) * X87(nz)) - X87(1f));
            return HP1.Core.X87Ext.Sqrt64(HP1.Core.X87Ext.From(a.ToDouble())).ToDouble();
        }

        // 0x103dd59a..0x103dd5bf: target below succeeds when radius·tan + CollisionHeight > dz·-1.0f (register).
        internal static bool WalkRampBelowFits(float radius, double tan, float height, float dz)
            => (HP1.Core.X87Ext.Fcom(R64(R64(X87(radius) * HP1.Core.X87Ext.From(tan)) + X87(height)), X87(dz) * X87(-1f)) & 0x41) == 0;

        // 0x103dd60f..0x103dd634: a wider target succeeds unless ((other + 15f) − radius)·tan + heightTol <= dz.
        internal static bool WalkRampWideFits(float otherRadius, float radius, double tan, float heightTol, float dz)
            => (HP1.Core.X87Ext.Fcom(R64(R64(R64(R64(X87(otherRadius) + X87(15f)) - X87(radius)) * HP1.Core.X87Ext.From(tan))
                   + X87(heightTol)), X87(dz)) & 0x41) == 0;

        internal static float ActorReachFar(Vector3 delta, float tolerance)
        {
            HP1.Core.X87Ext far = R64(UEVector.Size64Ext(delta.y, delta.z, delta.x) - X87(800f));
            // `fcom tol; test ah,1; jne` (0x103dbd9f..0x103dbda7): C0 — below or NaN — keeps the tolerance.
            return (HP1.Core.X87Ext.Fcom(far, X87(tolerance)) & HP1.Core.X87Ext.C0) != 0 ? tolerance : far.ToFloat();
        }

        // Region/FootRegion are FPointRegion structs; +0 is the AZoneInfo*.
        static UEObject ZoneOf(UEObject actor, string region)
            => (actor?.GetProp(region) as UEObject)?.GetProp(PK.Zone) as UEObject;

        // Shipped compares the zone's DamageType against the pawn's
        // ReducedDamageType as raw FNames (Engine.dll.c:73880).
        static bool SameDamageType(UEObject zone, UEObject pawn)
            => Equals(zone?.GetProp(PK.DamageType), pawn?.GetProp(PK.ReducedDamageType));

        // ── The three mode bodies. All three share one skeleton: remember
        // Location AND Velocity, walk up to 100 steps, fall back to the
        // WarpZoneMarker zone test, restore both, and fold the outcome into the
        // mode mask. They differ only in the step body and the handoff tests.

        const int ReachMaxSteps = 100;      // Engine.dll.c:124740, 11825, 149025

        // Bits the family ORs into the mask (walk 149006, fly 124726, swim 11805).
        const int ReachFlagWalk = 1, ReachFlagFly = 2, ReachFlagSwim = 4, ReachFlagJump = 8;

        // APawn::walkReachable (Engine.dll.c:148998-149183).
        int WalkReachable(UEInterpreter interp, UEObject self, Vector3 dest,
            float tolerance, int flags, UEObject target)
        {
            flags |= ReachFlagWalk;
            Vector3 savedLoc = UEInterpreter.AsVector3(self.GetProp(PK.Location));
            Vector3 savedVel = UEInterpreter.AsVector3(self.GetProp(PK.Velocity));

            // Probe step. Shipped uses a flat 16 in the editor; the game path is
            // CollisionRadius, widened to at least 128 for a jumper (149014).
            float radius = UEInterpreter.AsFloat(self.GetProp(PK.CollisionRadius));
            float step = radius;
            bool canJump = UEInterpreter.ToBool(self.GetProp(PK.bCanJump));
            if (canJump && step < 128f) step = 128f;
            // The step square is computed ONCE, before the loop (`fstp dword [ebp-0x80]`,
            // 0x103dd0ff — the only store to this slot). Clamping the step to
            // MaxStepHeight below (0x103dd46b) does NOT recompute it: from then on
            // a short step is compared against the old square.
            float stepSquared = step * step;

            // Height tolerance takes the LARGER of the two collision heights
            // when a target is named (149027).
            float heightTol = UEInterpreter.AsFloat(self.GetProp(PK.CollisionHeight));
            if (target is not null)
            {
                float t = UEInterpreter.AsFloat(target.GetProp(PK.CollisionHeight));
                if (t > heightTol) heightTol = t;
            }

            bool success = false;
            // Mirrors the shipped loop variable (iVar5): the loop runs while it
            // is exactly 1. Seeded to 1 so the first pass happens.
            int stepResult = 1;
            // The pawn's OWN hit record for this query. Shipped walkReachable
            // keeps a local FCheckResult seeded with a zero normal (149032) and
            // nested helpers never touch it, so the surface test below must NOT
            // read the actor-wide _lastMove* channel — that would see a normal
            // left by unrelated gameplay movement or by FindJumpUp.
            var localHit = new MoveCheck();
            for (int budget = ReachMaxSteps; stepResult == 1 && budget >= 0; budget--)
            {
                Vector3 loc = UEInterpreter.AsVector3(self.GetProp(PK.Location));
                float dx = dest.x - loc.x, dy = dest.y - loc.y, dz = dest.z - loc.z;
                // HORIZONTAL only; slope gate (too high for the horizontal run available, 149053), arrival and
                // step size from one register chain (WalkReachCompares).
                if (!WalkReachCompares(dx, dy, dz, heightTol, tolerance * tolerance, stepSquared,
                        out bool arrivedXY, out bool fullStep))
                    break;

                if (arrivedXY)
                {
                    // Arrived horizontally.
                    stepResult = 0;
                    if (System.Math.Abs(dz) < heightTol) { success = true; break; }

                    // Off in Z: decide on the surface underfoot. A near-flat
                    // floor or a face too steep to stand on both just end the
                    // iteration (149060); only a ramp is examined further.
                    // Both bounds are double constants (`fcomp qword`, 0x103dd538
                    // 0.95 and 0x103dd54f 0.7); the normal is float.
                    double nz = localHit.Normal.z;
                    if (nz >= 0.95 || nz <= 0.7) continue;

                    // Ramp, 0x103dd560-0x103dd636. tan is a double from appSqrt
                    // (`fstp qword [ebp-0x94]`).
                    double tan = WalkRampTan(localHit.Normal.z);
                    // Target BELOW: success if the descent along the ramp within the radius
                    // fits in the pawn's OWN height — [esi+0x1d4], not
                    // heightTol (0x103dd5a3-0x103dd5bf: `je` to success when
                    // radius*tan + CollisionHeight > -dz).
                    if (dz < 0f
                        && WalkRampBelowFits(radius, tan, UEInterpreter.AsFloat(self.GetProp(PK.CollisionHeight)), dz))
                    {
                        success = true;
                        break;
                    }

                    // Otherwise (target above, or below but too deep): the target's
                    // radius decides (0x103dd5c1-0x103dd636). A target no wider than
                    // the pawn — failure (0x103dd5d5-0x103dd5e3).
                    float otherRadius = target is not null
                        ? UEInterpreter.AsFloat(target.GetProp(PK.CollisionRadius))
                        : 46f;                                  // shipped default
                    if (radius >= otherRadius) continue;
                    if (!WalkRampWideFits(otherRadius, radius, tan, heightTol, dz)) continue;

                    success = true;
                    break;
                }

                // Still far: step HORIZONTALLY (Z is zeroed before normalising).
                // A full step needs progress 4.1 (`push 0x40833333`,
                // 0x103dd271), a short remainder 8.0 (`push 0x41000000`,
                // 0x103dd22f).
                Vector3 flat = new Vector3(dx, dy, 0f);
                Vector3 move = fullStep ? SafeNormal(flat) * step : flat;
                float minProgress = fullStep ? 4.1f : 8.0f;

                // The step's answer IS the loop variable: shipped spins on
                // `while (iVar5 == 1)` (149045), so ONLY 1 continues. Everything
                // else stops unless a branch deliberately puts 1 back.
                int result = WalkMove(interp, self, move, target, minProgress,
                    bRestoreToEntry: false, localHit);
                stepResult = result;
                if (result != 1)
                {
                    if (result == 5) { success = true; stepResult = 0; }
                    else if (UEInterpreter.ToInt(
                        (self.GetProp(PK.Region) as UEObject)?.GetProp(PK.ZoneNumber)) == 0)
                    {
                        stepResult = 0;                         // outside the zones
                    }
                    else if (UEInterpreter.ToBool(self.GetProp(PK.bCanFly)))
                    {
                        flags = FlyReachable(interp, self, dest, tolerance, flags, target);
                        success = flags != 0;
                        stepResult = 0;
                    }
                    else if (!canJump)
                    {
                        // No jump available: shrink the step to a stair and RETRY
                        // — this, not a fall, is why a pawn stops at a ledge. The
                        // retry is exactly the `iVar5 = 1` at 149133; without a
                        // shrink the loop ends here.
                        float maxStep = UEInterpreter.AsFloat(self.GetProp(PK.MaxStepHeight));
                        if (result == -1 && maxStep < step) { step = maxStep; stepResult = 1; }
                    }
                    else
                    {
                        // Jumper: the helper's answer becomes the loop variable
                        // (149126/149137), so it decides whether stepping goes on.
                        flags |= ReachFlagJump;
                        // Shipped normalises slot [ebp-0x24] (0x103dd347/0x103dd3d8),
                        // and the full step ABOVE has already overwritten that slot with
                        // the normal (0x103dd25e..0x103dd26e): after a full step the
                        // normal is normalised, after a short one (dx, dy, 0). The port
                        // always normalises flat — a ULP divergence; FindBestJump does not
                        // read the vector, FindJumpUp does.
                        Vector3 jumpVec = SafeNormal(flat)
                            * UEInterpreter.AsFloat(self.GetProp(PK.GroundSpeed));
                        // Restore flag 1 at this call site — the pawn KEEPS the
                        // ground it gained and the loop walks on from the
                        // landing spot (disassembly 0x103dd353 `push 1`; the
                        // decompile at 149126 drops the last two arguments).
                        if (result == -1)
                            stepResult = FindBestJump(interp, self, dest, jumpVec,
                                bRestoreToEntry: false);
                        else if (result == 0)
                            stepResult = FindJumpUp(interp, self, jumpVec);
                    }
                }

                // Pain underfoot (149148) kills success and the loop, but does NOT leave
                // the iteration: the water check below runs in the same iteration
                // (0x103dd479-0x103dd49a -> 0x103dd49d).
                if (UEInterpreter.ToBool(ZoneOf(self, "FootRegion")?.GetProp(PK.bPainZone))
                    && !SameDamageType(ZoneOf(self, "FootRegion"), self))
                {
                    success = false;
                    stepResult = 0;
                }
                // In water the loop ALWAYS stops — `xor edi, edi` (0x103dd4b7)
                // comes before the bCanSwim check; swimming only for a swimmer outside
                // a foreign pain zone (149152).
                UEObject zone = ZoneOf(self, "Region");
                if (UEInterpreter.ToBool(zone?.GetProp(PK.bWaterZone)))
                {
                    stepResult = 0;
                    if (UEInterpreter.ToBool(self.GetProp(PK.bCanSwim))
                        && (!UEInterpreter.ToBool(zone.GetProp(PK.bPainZone))
                            || SameDamageType(zone, self)))
                    {
                        flags = SwimReachable(interp, self, dest, tolerance, flags, target);
                        success = flags != 0;
                    }
                }
            }

            return FinishReach(interp, self, savedLoc, savedVel, success, flags, target);
        }

        // flyReachable 0x103dc6ef..0x103dc75a, swimReachable 0x103dcb1e..0x103dcb93: D = dest − Location in the
        // register (stored as float for the move), (Dx·Dx + Dy·Dy) + Dz·Dz in the register — `fst` keeps a float copy
        // for the step compare, `fcomp tol²` reads the unrounded sum; |Dz| (register) against CollisionHeight.
        // Masks (0x103dc736..0x103dc75a): `fcomp tol²; test ah,0x41; je out` — only an ordered "above" leaves (NaN goes on);
        // `fcom 0; test ah,1; je` — fchs on C0 (below zero or NaN); `fcomp CollisionHeight; test ah,0x41; je out`.
        internal static bool ReachArrived(Vector3 dest, Vector3 loc, float tolSq, float height, out float d2)
        {
            HP1.Core.X87Ext s = DistSq87(dest, loc);
            d2 = s.ToFloat();
            if ((HP1.Core.X87Ext.Fcom(s, X87(tolSq)) & 0x41) == 0) return false;
            HP1.Core.X87Ext dz = R64(X87(dest.z) - X87(loc.z));
            if ((HP1.Core.X87Ext.Fcom(dz, X87(0f)) & HP1.Core.X87Ext.C0) != 0) dz = HP1.Core.X87Ext.From(0) - dz;
            return (HP1.Core.X87Ext.Fcom(dz, X87(height)) & 0x41) != 0;
        }

        // APawn::flyReachable (Engine.dll.c:124716-124811).
        int FlyReachable(UEInterpreter interp, UEObject self, Vector3 dest,
            float tolerance, int flags, UEObject target)
        {
            flags |= ReachFlagFly;
            Vector3 savedLoc = UEInterpreter.AsVector3(self.GetProp(PK.Location));
            Vector3 savedVel = UEInterpreter.AsVector3(self.GetProp(PK.Velocity));

            float step = UEInterpreter.AsFloat(self.GetProp(PK.CollisionRadius));
            if (step < 200f) step = 200f;
            float height = UEInterpreter.AsFloat(self.GetProp(PK.CollisionHeight));

            bool success = false, running = true;
            for (int budget = ReachMaxSteps; running && budget >= 0; budget--)
            {
                Vector3 loc = UEInterpreter.AsVector3(self.GetProp(PK.Location));
                Vector3 delta = dest - loc;
                if (ReachArrived(dest, loc, tolerance * tolerance, height, out float d2))   // FULL 3D here
                {
                    success = true;
                    break;
                }

                Vector3 move = d2 >= step * step ? SafeNormal(delta) * step : delta;
                float minProgress = d2 >= step * step ? 4.1f : 8.0f;

                // Shipped spins on `while (iVar12 != 0)` (124742): the step's
                // answer IS the loop condition, so a 0 ENDS the query without
                // success — it does not skip to the next attempt.
                int result = FlyMove(interp, self, move, target, minProgress,
                    bRestoreOnFail: false);
                if (result == 5) { success = true; break; }
                if (result == 0) break;

                // In water flight ALWAYS stops — `xor edi, edi`
                // (0x103dc820) comes before the bCanSwim check (124766-124772).
                UEObject zone = ZoneOf(self, "Region");
                if (UEInterpreter.ToBool(zone?.GetProp(PK.bWaterZone)))
                {
                    if (UEInterpreter.ToBool(self.GetProp(PK.bCanSwim))
                        && (!UEInterpreter.ToBool(zone.GetProp(PK.bPainZone))
                            || SameDamageType(zone, self)))
                    {
                        flags = SwimReachable(interp, self, dest, tolerance, flags, target);
                        success = flags != 0;
                    }
                    break;
                }
            }

            return FinishReach(interp, self, savedLoc, savedVel, success, flags, target);
        }

        // APawn::swimReachable (Engine.dll.c:11801-11946).
        //
        // CLASSIFICATION WITHDRAWN 2026-07-31. An earlier revision of this
        // comment claimed the shipped campaign never exercises this body,
        // because `bWaterZone` occurs in ZERO of the 41 maps in
        // HARRY_POTTER_US_/maps. That search was too narrow: a `.unr` name table
        // only carries SERIALIZED property overrides. It cannot see inherited
        // class defaults, a native constructor default, a ZoneInfo subclass, or
        // a runtime write from script.
        //
        // Measured against the script packages, the claim does not hold: the
        // name occurs in FOUR of the 29 `.u` packages — Engine, HPBase,
        // HarryPotter and Hub4. Engine is expected (ZoneInfo declares the
        // property there), but the three game packages are real script-side
        // references, and Hub4 is a level package. Control for the search being
        // discriminating rather than matching everything: the SIBLING flag
        // `bPainZone` occurs in only ONE package.
        //
        // So this body may well be reachable, and "campaign-dead" must NOT be
        // relied on. Closing this properly requires enumerating the EFFECTIVE
        // bWaterZone of every loaded zone after class defaults are applied, plus
        // auditing the script and native writes of the property — with a
        // committed, reproducible script, not an ad-hoc grep.
        //
        // Consequence for validation, and this part is unchanged: the
        // "step touched the target (5), then
        // FALL THROUGH to the zone checks in the SAME iteration" branch below
        // has no fixture and cannot get one through the public query. A pawn
        // made by the harness has no Region at spawn, so the dispatcher takes
        // the physics switch and answers 0 without computing anything; and a
        // substituted Region does not survive the first step, because MoveActor
        // re-zones the pawn (same reason DownSign is pinned directly rather than
        // observed through a query — see UELevelRuntime.ReachMoves.cs).
        //
        // This is a LOUD, DELIBERATE debt, not an oversight: the body is ported
        // from the shipped source, and the campaign never reaches it. Adding a
        // test seam for a path the game does not take was judged not worth the
        // non-shipped machinery. If a later level pack introduces water, this
        // comment is the place that must be revisited FIRST.
        int SwimReachable(UEInterpreter interp, UEObject self, Vector3 dest,
            float tolerance, int flags, UEObject target)
        {
            flags |= ReachFlagSwim;
            Vector3 savedLoc = UEInterpreter.AsVector3(self.GetProp(PK.Location));
            Vector3 savedVel = UEInterpreter.AsVector3(self.GetProp(PK.Velocity));

            float step = UEInterpreter.AsFloat(self.GetProp(PK.CollisionRadius));
            if (step < 200f) step = 200f;
            float height = UEInterpreter.AsFloat(self.GetProp(PK.CollisionHeight));

            bool success = false;
            int stepResult = 1;                 // shipped fVar6, seeded non-zero
            for (int budget = ReachMaxSteps; stepResult != 0 && budget >= 0; budget--)
            {
                Vector3 loc = UEInterpreter.AsVector3(self.GetProp(PK.Location));
                Vector3 delta = dest - loc;
                if (ReachArrived(dest, loc, tolerance * tolerance, height, out float d2))
                {
                    success = true;
                    break;
                }

                Vector3 move = d2 >= step * step ? SafeNormal(delta) * step : delta;
                float minProgress = d2 >= step * step ? 4.1f : 8.0f;

                // Shipped spins on `while (fVar6 != 0.0)` (11830) where fVar6 is
                // the step's answer — but the zone checks below are NOT gated on
                // it and still run in this same iteration. That matters: a step
                // that surfaces returns 0, and the handover must still happen.
                // So the answer only decides whether ANOTHER iteration follows.
                stepResult = SwimMove(interp, self, move, target, minProgress,
                    bRestoreOnFail: false);
                if (stepResult == 5)
                {
                    // Shipped sets success and zeroes the continuation, then FALLS
                    // THROUGH to the zone checks in this same iteration (11858).
                    // Breaking out here would report success even when the very
                    // step that touched the target also surfaced the pawn or put
                    // it into a pain zone it has no immunity to.
                    success = true;
                    stepResult = 0;
                }

                UEObject zone = ZoneOf(self, "Region");
                if (!UEInterpreter.ToBool(zone?.GetProp(PK.bWaterZone)))
                {
                    // Surfaced. A flier just switches mode; a walker only does so
                    // when the destination sits within a step band overhead AND
                    // an upward probe finds nothing in the way (11876-11900).
                    if (UEInterpreter.ToBool(self.GetProp(PK.bCanFly)))
                    {
                        flags = FlyReachable(interp, self, dest, tolerance, flags, target);
                        success = flags != 0;
                        break;
                    }
                    // Location is read AGAIN here, AFTER swimMove
                    // (`fadd dword [esi+0x104]` 0x103dccab, `fsub dword
                    // [esi+0x104]` 0x103dcd0d; 11876-11887), not from the `loc`
                    // of the iteration start.
                    float nowZ = UEInterpreter.AsVector3(self.GetProp(PK.Location)).z;
                    if (UEInterpreter.ToBool(self.GetProp(PK.bCanWalk))
                        && dest.z < UEInterpreter.AsFloat(self.GetProp(PK.MaxStepHeight))
                                    + nowZ + 50f)
                    {
                        float zExtent = height
                            + UEInterpreter.AsFloat(self.GetProp(PK.MaxStepHeight));
                        if (dest.z - nowZ > zExtent) zExtent = dest.z - nowZ;
                        // A fresh FCheckResult(1.0) right before the probe (Engine.dll.c:11878-11882).
                        ReachHit probe = ReachMoveActor(interp, self,
                            new Vector3(0f, 0f, zExtent), new MoveCheck());
                        if (probe.Time == 1f)
                        {
                            success = FlyReachable(interp, self, dest, tolerance,
                                flags, target) != 0;
                            // Shipped OVERWRITES the mask with a bare walk bit
                            // here (11893) — it does not accumulate.
                            flags = ReachFlagWalk;
                        }
                    }
                    break;
                }

                // Still in water: a pain zone we are not immune to ends it.
                if (UEInterpreter.ToBool(zone.GetProp(PK.bPainZone))
                    && !SameDamageType(zone, self))
                {
                    success = false;
                    break;
                }
            }

            return FinishReach(interp, self, savedLoc, savedVel, success, flags, target);
        }

        // The tail every mode body shares: the WarpZoneMarker consolation test,
        // the unconditional Location+Velocity restore, and the mask fold.
        int FinishReach(UEInterpreter interp, UEObject self, Vector3 savedLoc,
            Vector3 savedVel, bool success, int flags, UEObject target)
        {
            // A marker counts as reached when we already stand in the zone it
            // marks, even though the movement failed (124794-124802).
            if (!success && target is not null
                && UEInterpreter.ClassIsA(target.Class, "WarpZoneMarker"))
                success = ReferenceEquals(ZoneOf(self, "Region"),
                    target.GetProp(PK.markedWarpZone) as UEObject);

            FarMove(interp, self, savedLoc, bTest: true, bNoCheck: true);
            self.Props["Velocity"] = savedVel;
            return success ? flags : 0;
        }

        // APawn::FindJumpUp (Engine.dll.c:109789-109818). Temporarily RAISES
        // MaxStepHeight to 48 while stepping by the ORIGINAL step height, then
        // puts the property back. The two heights are different on purpose.
        //
        // FindBestJump and the rest of the jump subgraph live in
        // UELevelRuntime.Jump.cs.
        int FindJumpUp(UEInterpreter interp, UEObject self, Vector3 jumpVec)
        {
            float saved = UEInterpreter.AsFloat(self.GetProp(PK.MaxStepHeight));
            self.Props["MaxStepHeight"] = 48f;
            try
            {
                // Its own hit record — deliberately discarded, so it cannot
                // clobber the caller's (shipped passes a separate local here).
                int result = WalkMove(interp, self, SafeNormal(jumpVec) * saved,
                    target: null, minProgress: 4.1f, bRestoreToEntry: true,
                    new MoveCheck());
                // Shipped folds 5 into 1; with a null target 5 cannot occur, so
                // this arm never runs in the original.
                return result == 5 ? 1 : result;
            }
            finally { self.Props["MaxStepHeight"] = saved; }
        }

        // FVector::SafeNormal. This used to be a second, float32 transcription
        // of a body UEVector.normalized already carries at shipped precision,
        // and the jump oracle caught the difference: Core.dll keeps the square
        // sum AND the reciprocal wider than float32 (0x1014f4e6-0x1014f557,
        // one rounding per component at the store), while this copy rounded
        // both. One authority now, so the two cannot drift apart again.
        static Vector3 SafeNormal(Vector3 v) => v.normalized;
    }
}
