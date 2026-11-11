// UEPawnMovement — the ONE owner of the pawn latent-movement layer
// (MoveTo/MoveToward/TurnTo/TurnToward entries + polls, rotateToward,
// setMoveTimer, moveToward steering) and of the walking FORMULA layer
// (calcVelocity, the zone-velocity move vector, the shipped sub-step
// planner, the displacement→velocity tail). Ported from the shipped HP1
// Engine.dll decompile (citations inline); SurrealEngine only where noted.
// Geometry (the swept move itself) is not in this file — all callers
// consume THESE formulas and orders.
using System;
using System.Collections.Generic;
using UELib.Core;
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    public static class UEPawnMovement
    {
        // ── The exact GMath trig table + the FCoords rotator product the
        // shipped movement natives use (Core.dll ctor 0x1014e26e builds the
        // 16384-entry sin table from step _DAT_1017fa38; FCoords::operator/=
        // (FRotator) via 0x1011e190 applies roll→pitch→yaw elementary coords).
        // GMathForward(rot) = the X axis of GMath.UnitCoords /= rot — the
        // steering axis of the !bCanStrafe fly/swim branch (139140) and of
        // the turn acceleration (71413/48544/40230/53966). ──
        // The table and the product have one owner — BrushCoords (UEBspCollision.cs);
        // TransformVectorBy sums in shipped order, not (z + y) + x.
        public static Vector3 GMathForward(UERotator rot)
        {
            BrushCoords c = BrushCoords.Identity;
            c.DivRotator(rot.Pitch, rot.Yaw, rot.Roll);
            return c.X;
        }

        // Shipped latent execs park the poll through the CONTEXT pawn's
        // GetStateFrame()->LatentAction — the write is UNCONDITIONAL in the
        // decompile (execMoveTo, Engine.dll.c:85958: no observable null
        // check), and shipped ULevel::DestroyActor (252940) never touches
        // the frame either: a pawn that ran Destroy(self) mid-state-body
        // keeps its frame, the trailing latent installs into it, and the
        // suspended body simply never resumes because the tick loops skip
        // bDeleteMe actors. Frontends therefore must NOT null StateFrame on
        // destroy — a missing frame here is a modelling error on ANY pawn,
        // destroyed or live (§8, no invented fallback).
        static void InstallLatent(UEObject self, UEFrame frame, LatentRunState latent)
        {
            UEFrame sf = self?.StateFrame;
            if (sf is null)
                throw new NotSupportedException(
                    $"latent {latent} on {self?.GetProp(PK.Name)} (destroyed={self?.Destroyed}) "
                    + "without a state frame — shipped writes this->StateFrame unconditionally "
                    + "(85958) and DestroyActor (252940) keeps the frame; a frontend that "
                    + "nulls it on teardown breaks the shared lifecycle model");
            sf.LatentState = latent;
        }

        // DesiredSpeed of execMoveTo / execMoveToward (disasm 0x103d85f8-0x103d8639 /
        // 0x103d8845-0x103d8873): `fld MaxDesiredSpeed; fcom speed; test ah,0x41;
        // jne` — speed is taken only when MaxDesiredSpeed > speed (a NaN speed
        // keeps MaxDesiredSpeed); then `fld 0.0; fcomp; test ah,1; jne` —
        // zero unless 0 < v (a NaN v stays).
        public static float ClampDesiredSpeed(float speed, float maxDesired)
        {
            float v = maxDesired > speed ? speed : maxDesired;
            return 0f < v || float.IsNaN(v) ? v : 0f;
        }

        // Distance for setMoveTimer: (dz² + dy²) + dx² on the x87 stack (PC64),
        // fstp qword, appSqrt = `fld qword; fsqrt`, fstp dword. roundDiffs —
        // whether the differences are stored as float before squaring (execMoveToward:
        // yes, execMoveTo: no).
        public static float RegisterSqrtDist(float ax, float bx, float ay, float by, float az, float bz,
            bool roundDiffs)
        {
            if (!float.IsFinite(ax) || !float.IsFinite(bx) || !float.IsFinite(ay) || !float.IsFinite(by)
                || !float.IsFinite(az) || !float.IsFinite(bz))
            {
                double fx = (double)ax - bx, fy = (double)ay - by, fz = (double)az - bz;
                return (float)Math.Sqrt((fz * fz + fy * fy) + fx * fx);
            }
            HP1.Core.X87Ext D(float a, float b)
            {
                var d = HP1.Core.X87Ext.Round64(HP1.Core.X87Ext.From(a) - HP1.Core.X87Ext.From(b));
                return roundDiffs ? HP1.Core.X87Ext.From(a - b) : d;
            }
            HP1.Core.X87Ext dx = D(ax, bx), dy = D(ay, by), dz = D(az, bz);
            HP1.Core.X87Ext R(HP1.Core.X87Ext v) => HP1.Core.X87Ext.Round64(v);
            HP1.Core.X87Ext sum = R(R(R(dz * dz) + R(dy * dy)) + R(dx * dx));
            return HP1.Core.X87Ext.Sqrt64(HP1.Core.X87Ext.From(sum.ToDouble())).ToFloat();
        }

        // ── APawn::setMoveTimer (Engine.dll.c:8882): movement timeout from
        // distance over the mode speed × DesiredSpeed. ──
        public static void SetMoveTimer(UEObject pawn, float dist)
        {
            int physics = UEInterpreter.ToInt(pawn.GetProp(PK.Physics));
            float speed = physics switch
            {
                1 or 2 or 10 => UEInterpreter.AsFloat(pawn.GetProp(PK.GroundSpeed)),
                4 => UEInterpreter.AsFloat(pawn.GetProp(PK.AirSpeed)),
                3 => UEInterpreter.AsFloat(pawn.GetProp(PK.WaterSpeed)),
                _ => 200f
            };
            pawn.Props["MoveTimer"] = MoveTimerValue(dist, speed, UEInterpreter.AsFloat(pawn.GetProp(PK.DesiredSpeed)));
        }

        // Disasm 0x103d9689-0x103d96bd: fmul DesiredSpeed; fcom 0.0, `test ah,0x40; je` — equal OR unordered (NaN)
        // -> 0.5; fld dist; fmul 1.3f [0x10479e04]; fdiv st,st(1); fadd 1.0f [0x1046dd9c]; fstp [+0x28c]. Every step
        // rounds to the 64-bit mantissa (PC64), one float store (judge: x87_size_corpus kind mvtimer).
        public static float MoveTimerValue(float dist, float speed, float desired)
        {
            double denom = (double)speed * desired;               // float·float: exact in double and in the register
            if (denom == 0.0 || double.IsNaN(denom)) return 0.5f;
            // NaN / ±inf take the same chain (X87Ext carries them; judge nan_slope_oracle kind mvtimer).
            HP1.Core.X87Ext r = HP1.Core.X87Ext.Round64(HP1.Core.X87Ext.Div64(
                HP1.Core.X87Ext.Round64(HP1.Core.X87Ext.From(dist) * HP1.Core.X87Ext.From(1.3f)),
                HP1.Core.X87Ext.Round64(HP1.Core.X87Ext.From(speed) * HP1.Core.X87Ext.From(desired)))
                + HP1.Core.X87Ext.From(1f));
            return r.ToFloat();
        }

        // execPollMoveToward flying, Pawn target (0x103d8a2b..0x103d8a3d): fld H; fmul 0.7f [0x10477b50]; fadd z; fstp.
        public static float PollFlyZ(float h, float z)
        {
            return HP1.Core.X87Ext.Round64(HP1.Core.X87Ext.From(h) * HP1.Core.X87Ext.From(0.7f) + HP1.Core.X87Ext.From(z)).ToFloat();
        }

        // execPollMoveToward spider Z (0x103d8a6a..0x103d8a97): st(1) = R·Floor.z in the register; fld z; fsub st,st(1).
        public static float PollSpiderZ(float z, float r, float fz)
        {
            return HP1.Core.X87Ext.Round64(HP1.Core.X87Ext.From(z) - HP1.Core.X87Ext.From(r) * HP1.Core.X87Ext.From(fz)).ToFloat();
        }

        // ── APawn::rotateToward (Engine.dll.c:118503): set DesiredRotation
        // toward the focus point; return whether already facing it (the
        // shipped completion band is ±100 units, 118555). Walking flattens
        // pitch; Spider never turns. ──
        public static bool RotateToward(UEObject pawn, Vector3 focus)
        {
            int physics = UEInterpreter.ToInt(pawn.GetProp(PK.Physics));
            if (physics == 10) return true; // Spider
            Vector3 loc = UEInterpreter.AsVector3(pawn.GetProp(PK.Location));
            UERotator dir = UEValue.RotatorFromVector(focus - loc);
            // Walking flattens pitch — unless MoveTarget IS a Pawn (118542-118549,
            // disasm 0x103d9f61 `cmp eax, APawn::PrivateStaticClass`).
            bool towardPawn = pawn.GetProp(PK.MoveTarget) is UEObject mt && UEInterpreter.ClassIsA(mt.Class, "Pawn");
            int pitch = physics == 1 && !towardPawn ? 0 : dir.Pitch;
            pawn.Props["DesiredRotation"] = new UERotator(pitch, dir.Yaw & 0xffff, 0);
            int curYaw = UEInterpreter.AsRotator(pawn.GetProp(PK.Rotation)).Yaw & 0xffff;
            int dYaw = Math.Abs((dir.Yaw & 0xffff) - curYaw);
            return dYaw < 100 || dYaw > 65435;
        }

        // ── TurnTo/TurnToward entry and poll shared side effect (shipped
        // Engine.dll.c:71413-71432, 48544-48563, 40230-40251, 53966-53985):
        // when strafing is disabled in Flying/Swimming, acceleration follows
        // the actor's GMath-table forward X axis at AccelRate. Pawn.uc
        // declaration order pins Pawn+0x25c bit 0x200 to bCanStrafe. ──
        public static void SetTurnAcceleration(UEObject pawn)
        {
            int physics = UEInterpreter.ToInt(pawn.GetProp(PK.Physics));
            if (UEInterpreter.ToBool(pawn.GetProp(PK.bCanStrafe)) || physics is not (3 or 4)) return;
            UERotator rotation = UEInterpreter.AsRotator(pawn.GetProp(PK.Rotation));
            pawn.Props["Acceleration"] =
                GMathForward(rotation) * UEInterpreter.AsFloat(pawn.GetProp(PK.AccelRate));
        }

        // ── APawn::moveToward (Engine.dll.c:138978): steer toward the point,
        // write Acceleration, test arrival (horiz² < 256, |dz| <
        // max(height,48)) and the MoveTimer timeout. Returns true when the
        // move is complete. The special 3D branch needs BOTH !bCanStrafe AND
        // Flying/Swimming (139102: (Pawn+0x25c & 0x200)==0 && Physics 4|3) —
        // a strafing flyer keeps the ordinary steering/slowdown/arrival.
        //
        // CLASS BRANCHES BY TARGET — FROM THE DISASM, NOT THE DECOMPILE.
        // Ghidra prints an unqualified `&PrivateStaticClass` in APawn::moveToward;
        // the compared immediates in machine code are:
        //   0x103d9779 cmp eax, ?PrivateStaticClass@AInventory@@  (Touch branch);
        //   0x103d9ae7 cmp eax, ?PrivateStaticClass@APawn@@       (approach branch).
        // Reading the second as NavigationPoint gives Peeves (MoveToward(basestation2),
        // Taunt) MoveTimer=1.2 s and cuts the flight by timeout.
        // `interp` is needed by the Touch branch; without it the dispatch is not
        // swallowed silently but warned about.
        public static bool MoveToward(UEObject pawn, Vector3 point, UEInterpreter interp = null)
        {
            int physics = UEInterpreter.ToInt(pawn.GetProp(PK.Physics));
            Vector3 loc = UEInterpreter.AsVector3(pawn.GetProp(PK.Location));
            Vector3 dir = point - loc;
            if (physics == 1) dir.z = 0f;
            // PHYS_Falling (Engine.dll.c:138999-139035; disasm 0x103d9789-0x103d987a):
            //   Region.Zone (+0xa0) -> Class -> Defaults; if
            //   Zone.ZoneGravity.Z (+0x268) <= Defaults.ZoneGravity.Z * 0.9 (qword
            //   0x10478e78, fcompp in extended precision) — return 0, writing NOTHING
            //   (0x103d9cb3: xor eax,eax). Else dir.Z = 0,
            //   Acceleration (+0x13c) = SafeNormal(dir) * AccelRate (+0x2c8);
            //   Velocity.Z (+0x134) >= 0.0 -> 0; Location.Z + 100.0 < Point.Z -> 1
            //   (sum in x87 without rounding to float — here double, exact); else 0.
            //   The branch is BEFORE the inventory target, the arrival test and MoveTimer.
            // Offsets checked by audit/prop_offsets.py (Actor.Region/Velocity/
            // Acceleration, Pawn.AccelRate, ZoneInfo.ZoneGravity).
            if (physics == 2)
            {
                UEObject zone = (pawn.GetDeclared("Actor", "Region") as UEObject)?.GetProp(PK.Zone) as UEObject;
                if (zone is null || interp is null)
                    throw new InvalidOperationException(
                        $"moveToward PHYS_Falling on {pawn.GetProp(PK.Name)}: "
                        + (zone is null ? "no Region.Zone" : "no interpreter for the zone class defaults"));
                float zoneZ = UEInterpreter.AsVector3(zone.GetProp(PK.ZoneGravity)).z;
                float defZ = UEInterpreter.AsVector3(interp.ClassDefaultVectorNoAlloc(zone.Class, "ZoneGravity")).z;
                if (!MoveTowardFallingAirControl(zoneZ, defZ)) return false;
                Vector3 flat = new Vector3(dir.x, dir.y, 0f).normalized;
                float accelRate = UEInterpreter.AsFloat(pawn.GetProp(PK.AccelRate));
                pawn.Props["Acceleration"] = new Vector3(flat.x * accelRate, flat.y * accelRate, flat.z * accelRate);
                if (UEInterpreter.AsVector3(pawn.GetProp(PK.Velocity)).z >= 0f) return false;
                return (double)loc.z + 100.0 < point.z;
            }
            // INVENTORY target (139050-139075, disasm 0x103d9779): within the pawn's own
            // cylinder (|dz| < CollisionHeight, dx²+dy² < CollisionRadius²) shipped calls
            // MoveTarget.Touch(self) — pickup by movement; the branch is BEFORE the dist
            // computation and the arrival test.
            if (pawn.GetDeclared("Pawn", "MoveTarget") is UEObject invTarget
                && UEInterpreter.ClassIsA(invTarget.Class, "Inventory"))
            {
                Vector3 tl = UEInterpreter.AsVector3(invTarget.GetProp(PK.Location));
                if (MoveTowardTouchesInventory(loc, tl, UEInterpreter.AsFloat(pawn.GetProp(PK.CollisionHeight)),
                        UEInterpreter.AsFloat(pawn.GetProp(PK.CollisionRadius))))
                {
                    if (interp is not null) UELifecycle.CallEvent(interp, invTarget, "Touch", pawn);
                    else HP1.Core.UETrace.Warn($"[Core] moveToward: inventory target {invTarget.GetProp(PK.Name)} "
                        + $"within reach of {pawn.GetProp(PK.Name)}, Touch not dispatched — entry without an interpreter (139050)");
                }
            }
            bool fly3d = !UEInterpreter.ToBool(pawn.GetProp(PK.bCanStrafe))
                && (physics == 4 || physics == 3);
            if (MoveTowardNear2D(dir.x, dir.y))
            {
                float vtol = Math.Max(UEInterpreter.AsFloat(pawn.GetProp(PK.CollisionHeight)), 48f);
                if (Math.Abs(dir.z) < vtol)
                {
                    if (!fly3d) pawn.Props["Acceleration"] = Vector3.zero;
                    return true; // reached
                }
            }
            float dist = dir.magnitude;
            // The steering axis: the ordinary branch normalizes point−Location
            // (139153-139159); the fly3d branch REPLACES it with the GMath
            // forward axis of the CURRENT Rotation (139132-139151) — a
            // non-strafing flyer accelerates where it faces and rotateToward
            // brings the nose around, it does not slide sideways at the goal.
            Vector3 unit = fly3d
                ? GMathForward(UEInterpreter.AsRotator(pawn.GetProp(PK.Rotation)))
                : MoveTowardUnit(dir, dist);
            Vector3 accel = unit * UEInterpreter.AsFloat(pawn.GetProp(PK.AccelRate));
            pawn.Props["Acceleration"] = accel;
            if (UEInterpreter.AsFloat(pawn.GetProp(PK.MoveTimer)) < 0f) return true; // timed out
            // PAWN target (139166-139177, disasm 0x103d9ae7): completion by approach
            // distance, not by cylinder — MeleeRange·0.8 + MoveTarget.CollisionRadius +
            // CollisionRadius <= dist → keep going, else arrived. Placed BEFORE steering
            // correction and slowdown.
            if (pawn.GetDeclared("Pawn", "MoveTarget") is UEObject pawnTarget
                && UEInterpreter.ClassIsA(pawnTarget.Class, "Pawn"))
            {
                return MoveTowardPawnReached(dist, UEInterpreter.AsFloat(pawn.GetProp(PK.MeleeRange)),
                    UEInterpreter.AsFloat(pawnTarget.GetProp(PK.CollisionRadius)),
                    UEInterpreter.AsFloat(pawn.GetProp(PK.CollisionRadius)));
            }
            Vector3 vel = UEInterpreter.AsVector3(pawn.GetProp(PK.Velocity));
            float speed = vel.magnitude;
            if (!fly3d && speed > 100f)
            {
                accel = MoveTowardSteer(accel, unit, vel, speed);
                pawn.Props["Acceleration"] = accel;
            }
            float avg = UEInterpreter.AsFloat(pawn.GetProp(PK.AvgPhysicsTime));
            if (MoveTowardSlowdown(dist, avg, speed))
            {
                if (!UEInterpreter.ToBool(pawn.GetProp(PK.bReducedSpeed)))
                {
                    pawn.Props["bReducedSpeed"] = true;
                    pawn.Props["DesiredSpeed"] = UEInterpreter.AsFloat(pawn.GetProp(PK.DesiredSpeed)) * 0.5f;
                }
                if (speed > 0f)
                    // 0x103d9c85..0x103d9ca5 divides in the register and compares DesiredSpeed with the unrounded
                    // quotient; for float operands that is the same value and decision (rounding at 64 bits, then at 24,
                    // is innocuous for a quotient of 24-bit numbers: 64 >= 2·24 + 2).
                    pawn.Props["DesiredSpeed"] = Math.Min(UEInterpreter.AsFloat(pawn.GetProp(PK.DesiredSpeed)), 200f / speed);
                if (fly3d) return true;
            }
            return false;
        }

        // ── moveToward's x87 chains (disasm 0x103d9716..0x103d9cb3), one helper per chain; judge
        // se_harness/bsp_diff/x87_size_oracle.py (kinds mt*). PC64: a value rounds to float only at fst/fstp dword. ──

        // fmul / fsubr of a float and a register value, fstp dword (X87Ext keeps the IEEE sign of a zero).
        static float MulF(float c, HP1.Core.X87Ext m) => R64(X(c) * m).ToFloat();
        static float SubF(float a, HP1.Core.X87Ext b) => R64(X(a) - b).ToFloat();

        // 0x103d97cf..0x103d97e8: Zone.ZoneGravity.Z against Defaults.ZoneGravity.Z · 0.9 (qword 0x10478e78), the product
        // in the register; `fcompp; test ah,1; je` (0x103d97e1..0x103d97e8): C0 — the product below the zone value, or
        // unordered (NaN) — goes on; otherwise (not below) returns 0 writing nothing.
        internal static bool MoveTowardFallingAirControl(float zoneZ, float defZ)
        {
            if (!float.IsFinite(zoneZ) || !float.IsFinite(defZ)) return !(zoneZ <= defZ * 0.9);
            return R64(X(defZ) * HP1.Core.X87Ext.From(0.9)).CompareTo(X(zoneZ)) < 0;
        }

        // 0x103d9890..0x103d98f7 (Inventory MoveTarget): the differences Location − target Location stay in the register;
        // |dZ| < CollisionHeight, then dY·dY + dX·dX < CollisionRadius² (both in the register).
        internal static bool MoveTowardTouchesInventory(Vector3 loc, Vector3 tl, float height, float radius)
        {
            if (!Finite(loc) || !Finite(tl) || !float.IsFinite(height) || !float.IsFinite(radius))
            {
                double ex = (double)loc.x - tl.x, ey = (double)loc.y - tl.y;
                return Math.Abs((double)loc.z - tl.z) < height && ey * ey + ex * ex < (double)radius * radius;
            }
            var dz = R64(X(loc.z) - X(tl.z));
            if (dz.Sign < 0) dz = X(0f) - dz;
            if (!(dz.CompareTo(X(height)) < 0)) return false;
            var dx = R64(X(loc.x) - X(tl.x));
            var dy = R64(X(loc.y) - X(tl.y));
            return R64(R64(dy * dy) + R64(dx * dx)).CompareTo(X(radius) * X(radius)) < 0;
        }

        // 0x103d9970..0x103d9989: (dY·dY + dX·dX) in the register against 256f.
        internal static bool MoveTowardNear2D(float dx, float dy)
        {
            if (!float.IsFinite(dx) || !float.IsFinite(dy)) return dx * dx + dy * dy < 256f;
            return R64(X(dy) * X(dy) + X(dx) * X(dx)).CompareTo(X(256f)) < 0;
        }

        // 0x103d9a3e..0x103d9a81: only dist > 0 normalizes (otherwise the differences stay); r = 1.0f/dist in the
        // register and stored as float (fst); X, Y = float(r)·d; Z = r·dZ from the register.
        internal static Vector3 MoveTowardUnit(Vector3 d, float dist)
        {
            if (!(dist > 0f)) return d;
            if (!Finite(d) || !float.IsFinite(dist)) return d / dist;
            var r = HP1.Core.X87Ext.Div64(X(1f), X(dist));
            float rf = r.ToFloat();
            return new Vector3(d.x * rf, d.y * rf, MulF(d.z, r));
        }

        // 0x103d9b02..0x103d9b2c (Pawn MoveTarget): (target.CollisionRadius + CollisionRadius) + MeleeRange·0.8 (qword
        // 0x10479df0) in the register against the float dist; reach <= dist keeps going (0).
        internal static bool MoveTowardPawnReached(float dist, float melee, float targetRadius, float radius)
        {
            if (!float.IsFinite(dist) || !float.IsFinite(melee) || !float.IsFinite(targetRadius) || !float.IsFinite(radius))
                return !((double)targetRadius + radius + melee * 0.8 <= dist);
            var reach = R64(R64(X(targetRadius) + X(radius)) + R64(X(melee) * HP1.Core.X87Ext.From(0.8)));
            return !(reach.CompareTo(X(dist)) <= 0);
        }

        // 0x103d9b90..0x103d9c32 (!fly3d, speed > 100): r = 1.0f/speed in the register; vn.X, vn.Y stored as float,
        // vn.Z = r·Vz stays in the register; d = vn − unit stored as float; dot = (vnY·uY + vnZ·uZ) + vnX·uX and
        // f = (1 − dot)·speed·0.2f in the register; X, Y: Accel − float(dX·f); Z: Accel − f·dZ with no store between.
        internal static Vector3 MoveTowardSteer(Vector3 accel, Vector3 unit, Vector3 vel, float speed)
        {
            if (!Finite(accel) || !Finite(unit) || !Finite(vel) || !float.IsFinite(speed))
            {
                Vector3 vn0 = vel / speed;
                return accel - (vn0 - unit) * ((1f - Vector3.Dot(vn0, unit)) * speed * 0.2f);
            }
            var r = HP1.Core.X87Ext.Div64(X(1f), X(speed));
            float vnx = MulF(vel.x, r), vny = MulF(vel.y, r);
            var vnz = R64(r * X(vel.z));
            float dX = SubF(vnx, X(unit.x)), dY = SubF(vny, X(unit.y));
            float dZ = R64(vnz - X(unit.z)).ToFloat();
            var dot = R64(R64(R64(X(vny) * X(unit.y)) + R64(vnz * X(unit.z))) + X(vnx) * X(unit.x));
            var f = R64(R64(R64(X(1f) - dot) * X(speed)) * X(0.2f));
            float px = MulF(dX, f), py = MulF(dY, f);
            return new Vector3(SubF(accel.x, X(px)), SubF(accel.y, X(py)), SubF(accel.z, R64(f * X(dZ))));
        }

        // 0x103d9c34..0x103d9c4d: dist < AvgPhysicsTime·speed·1.4 (qword 0x10479e10), the product in the register.
        internal static bool MoveTowardSlowdown(float dist, float avg, float speed)
        {
            if (!float.IsFinite(dist) || !float.IsFinite(avg) || !float.IsFinite(speed)) return dist < (double)avg * speed * 1.4;
            return X(dist).CompareTo(R64(X(avg) * X(speed) * HP1.Core.X87Ext.From(1.4))) < 0;
        }

        // ── APawn::calcVelocity (Engine.dll.c:53648): integrate Velocity from
        // Acceleration with friction, braking and a max-speed clamp. iWalk
        // (player walk mode) is player-only; NPCs take the simple branches. ──
        public static void CalcVelocity(UEObject pawn, Vector3 accelDir, float dt, float maxSpeed,
            float friction, bool bFluid, bool bBrake, bool bBuoyant)
        {
            // First calcVelocity statement: Friction = Max(Friction, bFluid).
            float effFriction = Math.Max(friction, bFluid ? 1f : 0f);
            Vector3 vel = UEInterpreter.AsVector3(pawn.GetProp(PK.Velocity));
            Vector3 accel = UEInterpreter.AsVector3(pawn.GetProp(PK.Acceleration));
            bool playerPawn = UEInterpreter.ClassIsA(pawn.Class, "PlayerPawn");
            bool walkingPlayer = playerPawn && UEInterpreter.ToBool(pawn.GetProp(PK.bIsWalking));
            if (bBrake && accel == Vector3.zero)
            {
                Vector3 oldVel = vel, sumVel = Vector3.zero;
                float t = dt;
                while (t > 0f)
                {
                    float step = t > 0.03f ? 0.03f : t;
                    vel -= 2f * vel * step * effFriction;
                    if (Vector3.Dot(oldVel, vel) > 0f) sumVel += vel * step * (1f / dt);
                    t -= 0.03f;
                }
                vel = sumVel;
                if (Vector3.Dot(oldVel, vel) < 0f || vel.sqrMagnitude < 100f) vel = Vector3.zero;
                // Tail LAB_103ebb44 (disasm 0x103ebb5b-0x103ebb89) shared by both
                // branches: Velocity·(1 − bFluid·dt·Friction) + Acceleration·dt, where
                // Friction is the argument itself (ebp+0x1c), not Max(Friction, bFluid).
                vel = vel * (1f - (bFluid ? 1f : 0f) * dt * friction) + accel * dt;
            }
            else
            {
                float speed = vel.magnitude;
                float accelRate = UEInterpreter.AsFloat(pawn.GetProp(PK.AccelRate));
                accel = CalcVelAccelLimit(accel, accelDir, accelRate, walkingPlayer);
                vel -= (vel - accelDir * speed) * dt * effFriction;
                vel = vel * (1f - (bFluid ? 1f : 0f) * dt * friction) + accel * dt;   // tail LAB_103ebb44
                pawn.Props["Acceleration"] = accel;
            }

            // Engine.dll.c:53911-53955: DesiredSpeed scales NPCs only. A walking
            // PlayerPawn above 30% of its nominal maximum takes the original
            // frictional walk-speed branch instead of the ordinary hard clamp.
            if (!playerPawn) maxSpeed *= UEInterpreter.AsFloat(pawn.GetProp(PK.DesiredSpeed));
            // bBuoyant (disasm 0x103ebbbc-0x103ebc1e): Region.Zone.ZoneGravity (+0x260)
            // · dt · (1 − Buoyancy/Mass).
            if (bBuoyant)
            {
                UEObject zone = (pawn.GetDeclared("Actor", "Region") as UEObject)?.GetProp(PK.Zone) as UEObject;
                Vector3 zg = UEInterpreter.AsVector3(zone?.GetProp(PK.ZoneGravity));
                float buoy = 1f - UEInterpreter.AsFloat(pawn.GetProp(PK.Buoyancy)) / UEInterpreter.AsFloat(pawn.GetProp(PK.Mass));
                vel += zg * dt * buoy;
            }
            vel = CalcVelSpeedLimit(vel, maxSpeed, walkingPlayer, effFriction, dt);
            pawn.Props["Velocity"] = vel;
        }

        static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
        static HP1.Core.X87Ext X(float v) => HP1.Core.X87Ext.From(v);
        static HP1.Core.X87Ext R64(HP1.Core.X87Ext v) => HP1.Core.X87Ext.Round64(v);

        // calcVelocity 0x103eb919..0x103eba1e: (Ax·Ax + Ay·Ay) + Az·Az against AccelRate² (walking player:
        // AccelRate²·0.09f), both in the register; over it Acceleration = AccelDir·AccelRate per component, stored as
        // float (walking player: then ·0.3f, stored again).
        internal static Vector3 CalcVelAccelLimit(Vector3 accel, Vector3 accelDir, float accelRate, bool walkingPlayer)
        {
            if (!Finite(accel) || !Finite(accelDir) || !float.IsFinite(accelRate))
            {
                float l = walkingPlayer ? accelRate * 0.3f : accelRate;
                return l * l < accel.sqrMagnitude ? accelDir * l : accel;
            }
            HP1.Core.X87Ext lim = X(accelRate) * X(accelRate);
            if (walkingPlayer) lim = R64(lim * X(0.09f));
            if (!(lim.CompareTo(Vector3.SizeSquared64(accel.x, accel.y, accel.z)) < 0)) return accel;
            var c = new Vector3(accelDir.x * accelRate, accelDir.y * accelRate, accelDir.z * accelRate);
            return walkingPlayer ? new Vector3(c.x * 0.3f, c.y * 0.3f, c.z * 0.3f) : c;
        }

        // calcVelocity 0x103ebc2c..0x103ebd1a. SizeSquared is stored as float; MaxSpeed²(·0.09f) stays in the register.
        // Not a walking player, or not above 0.3·MaxSpeed: over MaxSpeed² -> SafeNormal · MaxSpeed. Walking player above
        // it: speed = Size (float); V = V / speed (1/speed in the register, float per component); walk speed =
        // (1 − (F·dt + F·dt))·speed in the register, the larger of it and MaxSpeed·0.3f stored as float; V *= it.
        internal static Vector3 CalcVelSpeedLimit(Vector3 vel, float maxSpeed, bool walkingPlayer, float friction, float dt)
        {
            if (!Finite(vel) || !float.IsFinite(maxSpeed) || !float.IsFinite(friction) || !float.IsFinite(dt))
            {
                if (!walkingPlayer || vel.sqrMagnitude <= maxSpeed * maxSpeed * 0.09f)
                    return vel.sqrMagnitude > maxSpeed * maxSpeed ? vel.normalized * maxSpeed : vel;
                float sp = vel.magnitude;
                float ws0 = Math.Max((1f - 2f * friction * dt) * sp, maxSpeed * 0.3f);
                return sp > 0f ? vel * (ws0 / sp) : Vector3.zero;
            }
            HP1.Core.X87Ext ms2 = X(maxSpeed) * X(maxSpeed);
            HP1.Core.X87Ext sq = X(Vector3.SizeSquared64(vel.x, vel.y, vel.z).ToFloat());
            if (!walkingPlayer || !(R64(ms2 * X(0.09f)).CompareTo(sq) < 0))
                return ms2.CompareTo(sq) < 0 ? vel.normalized * maxSpeed : vel;
            float speed = vel.magnitude;
            if (speed == 0f) return Vector3.zero;   // unreachable: SizeSquared > 0 here
            HP1.Core.X87Ext r = HP1.Core.X87Ext.Div64(HP1.Core.X87Ext.From(1.0), X(speed));
            var u = new Vector3(R64(r * X(vel.x)).ToFloat(), R64(r * X(vel.y)).ToFloat(), R64(r * X(vel.z)).ToFloat());
            HP1.Core.X87Ext fd = X(friction) * X(dt);
            HP1.Core.X87Ext w = R64(R64(X(1f) - R64(fd + fd)) * X(speed));
            HP1.Core.X87Ext m3 = X(maxSpeed) * X(0.3f);
            float ws = m3.CompareTo(w) < 0 ? w.ToFloat() : m3.ToFloat();
            return new Vector3(u.x * ws, u.y * ws, u.z * ws);
        }

        // ── Latent ENTRIES (the native bodies minus arg parsing). ──

        // APawn::execMoveTo (Engine.dll.c:85918): Destination/Focus,
        // DesiredSpeed = clamp(speed, 0, MaxDesiredSpeed) — shipped clamps
        // honestly, NO invented fallback for MaxDesiredSpeed <= 0 —
        // setMoveTimer(dist), install the latent poll on the pawn's state
        // frame and prime one rotateToward + moveToward immediately.
        public static void EnterMoveTo(UEObject self, UEFrame frame, Vector3 dest, float speed,
            UEInterpreter interp = null)
        {
            self.SetDeclared("Pawn", "MoveTarget", null);
            self.Props["bReducedSpeed"] = false;
            self.Props["DesiredSpeed"] = ClampDesiredSpeed(speed, UEInterpreter.AsFloat(self.GetProp(PK.MaxDesiredSpeed)));
            // Disasm 0x103d85d0-0x103d8661: the differences Dest − Location stay on the
            // x87 stack (not stored as float), (dz² + dy²) + dx², fstp qword,
            // appSqrt (fsqrt PC64), fstp dword — the setMoveTimer argument.
            Vector3 loc = UEInterpreter.AsVector3(self.GetProp(PK.Location));
            SetMoveTimer(self, RegisterSqrtDist(dest.x, loc.x, dest.y, loc.y, dest.z, loc.z, roundDiffs: false));
            InstallLatent(self, frame, LatentRunState.MoveTo);
            self.Props["Destination"] = dest;
            self.Props["Focus"] = dest;
            RotateToward(self, dest);
            MoveToward(self, dest, interp);
        }

        // APawn::execMoveToward (Engine.dll.c:80783): latent move toward a
        // live ACTOR — the destination reads the TARGET's Location prop; a
        // PAWN target uses the fixed MoveTimer=1.2 («max before re-assess»)
        // instead of setMoveTimer(dist). A null target starts nothing.
        //
        // PAWN, NOT NavigationPoint: the decompile prints `&PrivateStaticClass` without
        // owner; disasm 0x103d8880 compares with ?PrivateStaticClass@APawn@@. E.g. Peeves
        // flying to basestation2 (1821 UU at AirSpeed 200): shipped gives
        // 1821·1.3/200+1 = 12.8 s, not 1.2.
        public static void EnterMoveToward(UEObject self, UEFrame frame, UEObject target, float speed,
            UEInterpreter interp = null)
        {
            if (target is null) return;
            self.Props["bReducedSpeed"] = false;
            self.Props["DesiredSpeed"] = ClampDesiredSpeed(speed, UEInterpreter.AsFloat(self.GetProp(PK.MaxDesiredSpeed)));
            Vector3 dest = UEInterpreter.AsVector3(target.GetProp(PK.Location));
            if (UEInterpreter.ClassIsA(target.Class, "Pawn"))
                self.Props["MoveTimer"] = 1.2f;
            else
            {
                // Disasm 0x103d8806-0x103d88d2: the differences ARE stored as float (fstp
                // dword), then (dz² + dy²) + dx² on the stack, fstp qword, appSqrt, fstp dword.
                Vector3 loc = UEInterpreter.AsVector3(self.GetProp(PK.Location));
                SetMoveTimer(self, RegisterSqrtDist(dest.x, loc.x, dest.y, loc.y, dest.z, loc.z, roundDiffs: true));
            }
            self.SetDeclared("Pawn", "MoveTarget", target);
            self.Props["Destination"] = dest;
            self.Props["Focus"] = dest;
            InstallLatent(self, frame, LatentRunState.MoveToward);
            RotateToward(self, dest);
            MoveToward(self, dest, interp);
        }

        // APawn::execTurnTo (Engine.dll.c:71393): fixed Focus point; the
        // initial rotateToward result is deliberately ignored — even an
        // already-facing pawn resumes only when the poll runs.
        public static void EnterTurnTo(UEObject self, UEFrame frame, Vector3 focus)
        {
            self.SetDeclared("Pawn", "MoveTarget", null);
            self.Props["Focus"] = focus;
            InstallLatent(self, frame, LatentRunState.TurnTo);
            SetTurnAcceleration(self);
            RotateToward(self, focus);
        }

        // APawn::execTurnToward (Engine.dll.c:40203): a null target is a true
        // no-op; otherwise store FaceTarget, install the latent, aim at its
        // current Location (the poll refreshes it every tick).
        public static void EnterTurnToward(UEObject self, UEFrame frame, UEObject target)
        {
            if (target is null) return;
            self.Props["FaceTarget"] = target;
            InstallLatent(self, frame, LatentRunState.TurnToward);
            SetTurnAcceleration(self);
            Vector3 focus = UEInterpreter.AsVector3(target.GetProp(PK.Location));
            self.Props["Focus"] = focus;
            RotateToward(self, focus);
        }

        // ── Latent POLLS (steering + completion tests ONLY — the movement
        // itself runs in the shipped physics slot). Return true = complete. ──

        // execPollMoveTo (Engine.dll.c:38490): rotateToward + moveToward each
        // tick; moveToward sets Acceleration/tests arrival, physics consumes
        // it after.
        public static bool PollMoveTo(UEObject actor, UEInterpreter interp = null)
        {
            RotateToward(actor, UEInterpreter.AsVector3(actor.GetProp(PK.Focus)));
            return MoveToward(actor, UEInterpreter.AsVector3(actor.GetProp(PK.Destination)), interp);
        }

        // execPollMoveToward (Engine.dll.c:88839): re-track the live
        // MoveTarget each tick. A lost target completes the latent.
        // PHYS_Flying toward a PAWN aims 0.7*CollisionHeight above it;
        // PHYS_Spider offsets by CollisionRadius*Floor.
        // bAdvancedTactics+walking fires AlterDestination around the move; a
        // PAWN target restores DesiredSpeed after it and aborts
        // (MoveTimer=-1) into a water zone when !bCanSwim.
        //
        // BOTH CLASS CHECKS ARE PAWN: disasm 0x103d8a0d and 0x103d8b54 compare with
        // ?PrivateStaticClass@APawn@@ (the decompile's collapsed symbol is ambiguous).
        // For a NavigationPoint target (Peeves -> basestation2) shipped does NOT restore
        // DesiredSpeed after moveToward: the slowdown at the target (bReducedSpeed,
        // 200/speed) stays.
        public static bool PollMoveToward(UEInterpreter interp, UEObject actor)
        {
            if (actor.GetDeclared("Pawn", "MoveTarget") is not UEObject mt) return true;
            Vector3 mtDest = UEInterpreter.AsVector3(mt.GetProp(PK.Location));
            int phys = UEInterpreter.ToInt(actor.GetProp(PK.Physics));
            bool pawnTarget = UEInterpreter.ClassIsA(mt.Class, "Pawn");
            // Precision — disasm 0x103d8a2b..0x103d8aa1: flying — fld H; fmul 0.7f
            // (0x10477b50); fadd z; one store. Spider — X, Y: the product R·Floor and the
            // difference are stored as float; Z: product in the register, one store of the
            // difference (64-bit mantissa: PollFlyZ/PollSpiderZ).
            if (phys == 4 && pawnTarget)
                mtDest.z = PollFlyZ(UEInterpreter.AsFloat(mt.GetProp(PK.CollisionHeight)), mtDest.z);
            else if (phys == 10)
            {
                float r = UEInterpreter.AsFloat(mt.GetProp(PK.CollisionRadius));
                Vector3 floor = UEInterpreter.AsVector3(actor.GetProp(PK.Floor));
                mtDest = new Vector3(mtDest.x - r * floor.x, mtDest.y - r * floor.y, PollSpiderZ(mtDest.z, r, floor.z));
            }
            actor.Props["Destination"] = mtDest;
            actor.Props["Focus"] = mtDest;
            RotateToward(actor, mtDest);
            object savedSpeed = actor.GetProp(PK.DesiredSpeed);
            bool alterHook = UEInterpreter.ToBool(actor.GetProp(PK.bAdvancedTactics)) && phys == 1;
            if (alterHook) UELifecycle.CallEvent(interp, actor, "AlterDestination");
            bool arrived = MoveToward(actor, UEInterpreter.AsVector3(actor.GetProp(PK.Destination)), interp);
            if (alterHook)
            {
                mtDest = UEInterpreter.AsVector3(mt.GetProp(PK.Location));
                actor.Props["Destination"] = mtDest;
            }
            if (pawnTarget)
            {
                actor.Props["DesiredSpeed"] = savedSpeed;
                UEObject mtZone = (mt.GetDeclared("Actor", "Region") as UEObject)?.GetProp(PK.Zone) as UEObject;
                if (!UEInterpreter.ToBool(actor.GetProp(PK.bCanSwim))
                    && UEInterpreter.ToBool(mtZone?.GetProp(PK.bWaterZone)))
                    actor.Props["MoveTimer"] = -1f;
            }
            return arrived;
        }

        // execPollTurnTo (Engine.dll.c:48528): update the flying/swimming
        // acceleration and test the fixed Focus.
        public static bool PollTurnTo(UEObject actor)
        {
            SetTurnAcceleration(actor);
            return RotateToward(actor, UEInterpreter.AsVector3(actor.GetProp(PK.Focus)));
        }

        // execPollTurnToward (Engine.dll.c:53949): a lost FaceTarget completes
        // immediately; else refresh Focus from its live Location, update
        // acceleration, and test facing. The TURN itself happens only in
        // physicsRotation inside the performPhysics tail.
        public static bool PollTurnToward(UEObject actor)
        {
            if (actor.GetProp(PK.FaceTarget) is not UEObject ft) return true;
            SetTurnAcceleration(actor);
            Vector3 focus = UEInterpreter.AsVector3(ft.GetProp(PK.Location));
            actor.Props["Focus"] = focus;
            return RotateToward(actor, focus);
        }

        // ── Walking formula layer (APawn::physWalking, Engine.dll.c:135282) ──

        // The step velocity the whole substep loop uses (135320-135339):
        // Velocity snapshotted ONCE after calcVelocity, plus
        // ZoneVelocity·25·dt — ALWAYS for a PlayerPawn, for others only when
        // ZoneVelocity.SizeSquared() > 90000. Not written back to Velocity —
        // the tail rederives Velocity from displacement.
        //
        // ORDER AND PRECISION — DISASM 0x103e6cfa-0x103e6dc0:
        //   threshold: ((ZVx²+ZVy²)+ZVz²) from the register vs 90000f (0x1047a5c4);
        //   X, Y: float(ZV·25f) -> float(·dt) -> float(V + ·) — three stores;
        //   Z:   ZV·25f·dt in the register (both products exact) + V.z, one store.
        public static Vector3 WalkingMoveVelocity(UEObject actor, Vector3 zoneVelocity, float dt)
        {
            Vector3 moveVel = UEInterpreter.AsVector3(actor.GetProp(PK.Velocity));
            if (UEInterpreter.ClassIsA(actor.Class, "PlayerPawn")
                || ((double)zoneVelocity.x * zoneVelocity.x + (double)zoneVelocity.y * zoneVelocity.y)
                    + (double)zoneVelocity.z * zoneVelocity.z > 90000.0)
            {
                float tx = zoneVelocity.x * 25f, ty = zoneVelocity.y * 25f;
                moveVel = new Vector3(moveVel.x + tx * dt, moveVel.y + ty * dt,
                    (float)((double)zoneVelocity.z * 25f * dt + moveVel.z));
            }
            return moveVel;
        }

        // The shipped substep planner (135422-135439): a leftover ≤ 0.05
        // walks in one step; a PLAYER always splits a longer leftover into
        // min(remaining/2, 0.05); an NPC splits only when the move would
        // cover more than 20 units (|moveVel|²·remaining² > 400), else takes
        // the whole leftover. The loop caps at 8 iterations (iter > 7 exits).
        // Disasm 0x103e6f50..0x103e6ff0: ((V.y·V.y + V.z·V.z) + V.x·V.x)·rem·rem stays in the register for the
        // compare with 400f.
        public static float NextWalkSubstep(float remaining, bool isPlayer, Vector3 moveVel)
        {
            if (remaining <= 0.05f) return remaining;
            if (!isPlayer)
            {
                bool split = !Finite(moveVel) || !float.IsFinite(remaining)
                    ? moveVel.sqrMagnitude * remaining * remaining > 400f
                    : R64(R64(Vector3.SizeSquared64(moveVel.y, moveVel.z, moveVel.x) * X(remaining)) * X(remaining))
                        .CompareTo(X(400f)) > 0;
                if (!split) return remaining;
            }
            return Math.Min(remaining * 0.5f, 0.05f);
        }

        // The physWalking tail (135377-135419): recompute Velocity from the
        // actual displacement over dt, project it onto the floor plane and
        // restore the pre-projection magnitude.
        //
        // x87 PRECISION — DISASM 0x103ea36d-0x103ea4f8:
        //   d = Location − OldLocation            (FVector: float components)
        //   V = d · (1/dt)                         (fld 1.0; fdiv dt — 1/dt in the register;
        //                                           each product — fstp dword)
        //   speed = appSqrt((Vx²+Vy²)+Vz²)         (sum in the register -> qword, fsqrt;
        //                                           fst dword — speed as float for k)
        //   speed <= 0 or N.z <= 0 -> exit         (V already written)
        //   V.z -= ((Ny·Vy + Nz·Vz) + Nx·Vx) / Nz  (in the register, one fstp store)
        //   newSpeed = appSqrt(...)                (stays in the register)
        //   newSpeed <= 0 -> exit                  (V.z already written)
        //   V *= (float)(speedF / newSpeed)        (Core FVector::operator*=: fmul; fstp)
        // The x87 register (64-bit mantissa) is modelled by double — same convention
        // as DivByFloat and SafeNormal (UEVector.normalized).
        public static void FinishWalkingVelocity(UEObject actor, Vector3 startLocation, float dt,
            Vector3 floorNormal)
        {
            Vector3 disp = UEInterpreter.AsVector3(actor.GetProp(PK.Location)) - startLocation;
            double rv = 1.0 / dt;
            Vector3 vel = new((float)(disp.x * rv), (float)(disp.y * rv), (float)(disp.z * rv));
            double speed = Math.Sqrt(((double)vel.x * vel.x + (double)vel.y * vel.y) + (double)vel.z * vel.z);
            if (speed > 0.0 && floorNormal.z > 0f)
            {
                float speedF = (float)speed;
                double num = ((double)floorNormal.y * vel.y + (double)floorNormal.z * vel.z)
                    + (double)floorNormal.x * vel.x;
                vel.z = (float)(vel.z - num / floorNormal.z);
                double newSpeed = Math.Sqrt(((double)vel.x * vel.x + (double)vel.y * vel.y)
                    + (double)vel.z * vel.z);
                if (newSpeed > 0.0)
                {
                    float k = (float)(speedF / newSpeed);
                    vel = new Vector3(vel.x * k, vel.y * k, vel.z * k);
                }
            }
            actor.Props["Velocity"] = vel;
        }

        // ── APawn::performPhysics tail slice shared by all callers
        // (Engine.dll.c:68713-68731): after ANY active physics mode a pawn's
        // MoveTimer decrements — NOT only while a movement latent polls — and
        // AvgPhysicsTime follows it (68728: `dt·0.2 + AvgPhysicsTime·0.8`).
        // AvgPhysicsTime feeds the moveToward slowdown/completion radius
        // (dist < AvgPhysicsTime·|Velocity|·1.4, 139186); at dt=0.02 the shipped radius
        // for flying Peeves is ~5.6 UU (the class default 0.1 would give 28).
        // CONSTANTS ARE FLOAT, NOT DOUBLE (disasm 0x103e5662-0x103e5690):
        // fld AvgPhysicsTime; fmul dword [0x10479e00] (0.8f); fld dt;
        // fmul dword [0x104737f0] (0.2f); faddp; fstp. The decompile prints them without
        // suffix, but 0.2f = 0.20000000298…, 0.8f = 0.80000001192…; double constants shift
        // the average's fixed point by an ULP.
        // float·float products are exact in double, the sum is double instead of the
        // 64-bit mantissa, one store to float. MoveTimer: fld; fsub dt; fstp —
        // one rounding, same as a float subtraction. ──
        public static void DecrementMoveTimer(UEObject pawn, float dt)
        {
            pawn.Props["MoveTimer"] = UEInterpreter.AsFloat(pawn.GetProp(PK.MoveTimer)) - dt;
            pawn.Props["AvgPhysicsTime"] = (float)(
                (double)UEInterpreter.AsFloat(pawn.GetProp(PK.AvgPhysicsTime)) * 0.8f + (double)dt * 0.2f);
        }
    }
}
