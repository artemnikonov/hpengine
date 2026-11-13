// PHYS_Walking: the walk/trace hooks (WalkMove, LineTrace, FarMoveTo), PhysWalking, its substep
// (MovingSubstep, StepDownAndGate), StepUp and TwoWallAdjust, plus the x87 helper math of the walk.
// Hook delegates and fields live in UELevelRuntime.cs.
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
        // Core's own geometry, with the hook as an OVERRIDE.
        //
        // Core answers these itself: the swept move IS MoveActor, the trace IS
        // SingleLineCheck, the teleport IS FarMove. The hooks remain for a
        // frontend that must still interpose (headless keeps LineTraceHook for
        // its journal line), but nothing depends on their being set.
        // A physics-body move into ITS FCheckResult (`hit` is the caller's local,
        // see MoveCheck): the refusal and the MoveActor fast path do not touch the record,
        // and the caller reads what was there (the previous move or trace).
        // The hook is a fixture seam: its answer is stored into the record whole.
        (float Fraction, Vector3 Normal, UEObject HitActor) WalkMove(
            UEInterpreter interp, UEObject pawn, Vector3 delta, MoveCheck hit)
        {
            if (WalkMoveHook is not null)
            {
                // The hook replaces only the full-path SWEEP; the refusal and fast path are
                // MoveActor's own exits before writing Hit (0x103aa3ed-0x103aa501) and stay
                // with Core (the physics move carries the current rotation).
                if (MoveActorRefuses(pawn) || IsTinyMoveDelta(delta)) return (hit.Time, hit.Normal, hit.Actor);
                (float f, Vector3 n, UEObject a) = WalkMoveHook(interp, pawn, delta);
                hit.Set(f, n, a, -1, Vector3.zero);
                return (f, n, a);
            }
            MoveActor(interp, pawn, delta, UEInterpreter.AsRotator(pawn.GetProp(PK.Rotation)),
                bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false, hit);
            return (hit.Time, hit.Normal, hit.Actor);
        }

        // physWalking/stepUp trace into the SAME FCheckResult as the moves: every
        // SingleLineCheck of APawn::physWalking goes into &uStack_80 (the same address as
        // MoveActor's, Engine.dll.c:135495-136341), so the next fast-path move reads the
        // trace result. Disasm (physWalking 0x103e6b60..0x103eb3e0): all 22 ULevel vtable
        // +0xc4 calls (SingleLineCheck; vtable slot holds 0x1030348b), all 7 +0x8c calls
        // (MoveActor, 0x1030404d) and the stepUp call 0x103e9008 pass `lea [ebp-0x7c]`;
        // that record is built once at 0x103e6ecb..0x103e6ef2 (Time 1.0).
        LineCheckHit WalkTrace(UEInterpreter interp, UEObject actor, MoveCheck hit,
            Vector3 start, Vector3 end, Vector3 extent, int traceFlags)
        {
            LineCheckHit h = LineTrace(interp, actor, start, end, extent, traceFlags);
            hit.FromTrace(h);
            return h;
        }

        // FVector::operator/(FLOAT) Core.dll 0x10119310: `fld 1.0; fdiv V` —
        // the reciprocal stays in x87 (not rounded to float), each component X·RV is
        // rounded at fstp dword.
        // EQUIVALENT TO FLOAT DIVISION: for normal floats the quotient X/V is at least
        // 2^-49 relative away from any midpoint between adjacent floats (X·2^k − M·V is a
        // multiple of the low bit of the 49-bit M·V), while X·RV in the register (and in
        // double) errs by less than 2^-52 — rounding to float always matches. Only the form
        // with RV ROUNDED to float differs from division (see DisplacementVelocityX87).
        public static Vector3 DivByFloat(Vector3 v, float d)
        {
            double rv = 1.0 / d;
            return new Vector3((float)(v.x * rv), (float)(v.y * rv), (float)(v.z * rv));
        }

        // Velocity from displacement in tails where the compiler kept ONE difference in an
        // x87 register (disasm):
        //   physProjectile 0x103f2e6c-0x103f2ec4, physFlying 0x103f19b3-0x103f1a16 —
        //     X in the register; AActor::performPhysics, PHYS_MovingBrush
        //     0x103e5351-0x103e539e — Z in the register.
        // Form of all three: `fld 1.0; fdiv dt; fst dword [tmp]` — 1/dt both in the register
        // and as a float-rounded copy; the register component = (L−O)·(1/dt) without
        // intermediate rounding, the other two = float(L−O) · float(1/dt). Each product is
        // rounded at fstp dword. The register (64-bit mantissa) is modelled by double, as in
        // DivByFloat.
        public static Vector3 DisplacementVelocityX87(Vector3 loc, Vector3 old, float dt, int registerAxis)
        {
            double rv = 1.0 / dt;
            float rvF = (float)rv;
            float C(int axis, float l, float o)
                => axis == registerAxis ? (float)(((double)l - o) * rv) : (l - o) * rvF;
            return new Vector3(C(0, loc.x, old.x), C(1, loc.y, old.y), C(2, loc.z, old.z));
        }

        LineCheckHit LineTrace(
            UEInterpreter interp, UEObject actor, Vector3 start, Vector3 end,
            Vector3 extent, int traceFlags)
        {
            if (LineTraceHook is not null)
                return LineTraceHook(interp, actor, start, end, extent, traceFlags);
            return SingleLineCheck(interp, actor, actor, start, end, extent, traceFlags);
        }

        bool FarMoveTo(UEInterpreter interp, UEObject actor, Vector3 newLoc)
            => FarMoveHook is not null
                ? FarMoveHook(interp, actor, newLoc)
                : FarMove(interp, actor, newLoc, bTest: false, bNoCheck: false);
        bool TraceMovesNow => TraceMovesHook?.Invoke() ?? false;

        // ── APawn::physWalking (Engine.dll.c:135282) — the FULL walking body,
        // one state machine for every frontend: the zone-0 head, the
        // stationary floor check with the Falling transition, and the moving
        // branch (calcVelocity → zone step velocity → the shipped substep
        // plan → sweep → stepUp on a block → step-down grounding with
        // SetBase → the displacement velocity tail). Geometry rides the
        // hooks above. ──
        public void PhysWalking(UEInterpreter interp, UEObject actor, float dt, int iteration = 0)
        {
            // APawn::physWalking is a PAWN method — a non-Pawn carrying
            // PHYS_Walking (jelly beans and other pickups) never runs it.
            if (!UEInterpreter.ClassIsA(actor.Class, "Pawn")) return;
            UEObject region = actor.GetProp(PK.Region) as UEObject;
            if (region is not null && UEInterpreter.ToInt(region.GetProp(PK.ZoneNumber)) == 0)
            {
                // APawn::physWalking zone-0 head (Engine.dll.c:290948-290959).
                FellOutOfWorld(interp, actor, falling: false);
                return;
            }

            Vector3 location = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
            Vector3 velocity = UEInterpreter.AsVector3(actor.GetProp(PK.Velocity));
            // JOURNAL FILTER ONLY — this is NOT a shipped dispatch; the shipped
            // head has none (it runs calcVelocity unconditionally and dispatches
            // per SUBSTEP at 136181-136191, which is `stationarySubstep` below).
            // It exists purely so a fully idle pawn does not emit a walking_move
            // line every tick. Its horizontal-only test is a journal-volume
            // choice, not a ported predicate — do not read physics into it.
            bool journalMoving = UEInterpreter.AsVector3(actor.GetProp(PK.Acceleration)).sqrMagnitude > 0.0001f
                || new Vector3(velocity.x, velocity.y, 0f).sqrMagnitude > 0f;
            actor.Props["OldLocation"] = location;
            actor.Props["bJustTeleported"] = false;
            // NOTE: the shipped head zeroes Acceleration.Z ONLY (135300:
            // *(this+0x144)=0 with this+0x13c = Acceleration). Velocity keeps
            // all three components — calcVelocity reads/writes 0x12c/0x130/
            // 0x134 (135320-135322), the substep planner squares all three
            // (135432) and the substep dispatch compares all three deltas
            // (136181-136191). Never zero Velocity.Z here.

            // The shipped head (135300-135319): Acceleration.z zeroed, the
            // accel DIRECTION cached, calcVelocity ALWAYS (a braking no-op
            // for an idle pawn), ONE zone-adjusted step velocity for the
            // whole call.
            UEObject zone = region?.GetProp(PK.Zone) as UEObject;
            Vector3 gravity = UEInterpreter.AsVector3(zone?.GetProp(PK.ZoneGravity));
            float friction = UEInterpreter.AsFloat(zone?.GetProp(PK.ZoneGroundFriction));
            Vector3 zoneVel = UEInterpreter.AsVector3(zone?.GetProp(PK.ZoneVelocity));
            Vector3 accel = UEInterpreter.AsVector3(actor.GetProp(PK.Acceleration));
            accel.z = 0f;
            actor.Props["Acceleration"] = accel;
            Vector3 accelDir = accel.sqrMagnitude > 0f ? accel.normalized : Vector3.zero;
            UEPawnMovement.CalcVelocity(actor, accelDir,
                dt, UEInterpreter.AsFloat(actor.GetProp(PK.GroundSpeed)), friction,
                bFluid: false, bBrake: true, bBuoyant: false);
            Vector3 moveVel = UEPawnMovement.WalkingMoveVelocity(actor, zoneVel, dt);
            bool walkerIsPlayer = UEInterpreter.ClassIsA(actor.Class, "PlayerPawn");

            var walk = new WalkState
            {
                FullDt = dt,
                Remaining = dt,
                Iteration = iteration,
                FloorNormal = new Vector3(0f, 0f, 1f),
                MoveVel = moveVel,
                AccelDir = accelDir,
                GravSign = gravity.z > 0f ? 1f : -1f,
                Extent = GetCylinderExtent(actor),
                Hit = new MoveCheck(),
            };
            UEObject level = interp.GetLevelInfo();
            while (walk.Remaining > 0f && walk.Iteration <= 7)
            {
                walk.Iteration++;
                float sub = UEPawnMovement.NextWalkSubstep(walk.Remaining, walkerIsPlayer,
                    moveVel);
                walk.Remaining -= sub;
                walk.Sub = sub;
                walk.PreSubstepLoc = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
                Vector3 delta = moveVel * sub;
                // fStack_128 = delta at the start of the substep (135441); a moving
                // substep rewrites it to adjust (LAB_103e86bb).
                walk.Intended = delta;
                // The per-substep moving/stationary dispatch (135458 + 136181-
                // 136191): shipped tests |delta.X|, then |delta.Y|, then
                // |delta.Z| against 0.0001 and takes the moving machinery on
                // the FIRST one that passes — a purely VERTICAL substep is a
                // moving substep. Otherwise the stationary path — remaining
                // zeroed (136193, ONE gate pass), bHitSlopedWall cleared
                // (136194), then the hover check (LAB_103e9861).
                bool stationarySubstep = Math.Abs(delta.x) < 0.0001f && Math.Abs(delta.y) < 0.0001f
                    && Math.Abs(delta.z) < 0.0001f;
                if (!stationarySubstep)
                {
                    if (MovingSubstep(interp, actor, delta, walkerIsPlayer, ref walk)
                        == WalkGateResult.Return)
                        return;
                }
                else
                {
                    walk.Remaining = 0f;
                    actor.Props["bHitSlopedWall"] = false;
                    // Hover check: a POINT trace (extent zero, flags 6 =
                    // Movers|Level) 20 units below the feet (Location.z −
                    // CollisionHeight); hitting the CURRENT Base inside the
                    // [4.1, 4.6] hover band means the pawn is settled — skip
                    // the step-down entirely.
                    Vector3 feet = walk.PreSubstepLoc
                        - new Vector3(0f, 0f, UEInterpreter.AsFloat(actor.GetProp(PK.CollisionHeight)));
                    (bool hBlocked, float hFraction, _, UEObject hActor) =
                        WalkTrace(interp, actor, walk.Hit, feet, feet - new Vector3(0f, 0f, 20f),
                            Vector3.zero, 6);
                    if (hBlocked
                        && ReferenceEquals(hActor ?? level, actor.GetProp(PK.Base) as UEObject)
                        && WalkHoverSettled(hFraction))
                        continue;
                }
                if (StepDownAndGate(interp, actor, region, ref walk, stationarySubstep)
                    == WalkGateResult.Return)
                    return;
                // The Physics loop head does NOT check (0x103e6f30: only
                // remainder and the iteration counter) — the willFall route and a script
                // mode change reach the velocity tail below.
            }

            // The shipped velocity tail (135377-135418): Velocity from the
            // actual displacement over the WHOLE dt — zero for a pawn that
            // never moved (early-out on zero magnitude keeps the projection
            // dormant).
            if (!UEInterpreter.ToBool(actor.GetProp(PK.bJustTeleported)))
                UEPawnMovement.FinishWalkingVelocity(actor, location, dt, walk.FloorNormal);

            if (journalMoving)
                Journal?.Invoke($"walking_move actor={actor.GetProp(PK.Name)} location={actor.GetProp(PK.Location)} velocity={actor.GetProp(PK.Velocity)}");
        }

        // Mutable per-call walking state shared between the substep loop, the
        // moving-branch look-ahead machinery and the common step-down gate.
        struct WalkState
        {
            public float FullDt;        // param_1 — the whole physWalking dt
            public float Remaining;     // fStack_18
            public int Iteration;       // param_2
            public Vector3 FloorNormal; // fStack_16c/168/164
            public Vector3 MoveVel;     // fStack_e8/e4/e0 (with zone velocity)
            public Vector3 PreSubstepLoc; // fStack_1c4/1c0/1bc
            public float Sub;           // fStack_44 — current substep
            public bool MayFallCalled;  // fStack_c0
            public Vector3 AccelDir;    // fStack_8c/88/84
            public float GravSign;      // fStack_d4 (±1 from ZoneGravity.z)
            public bool WillFall;       // fStack_f0 — the look-ahead's fall pre-approval
            public Vector3 Intended;    // fStack_128/124/120 — the delta handed to the move
            public Vector3 Extent;      // uStack_9c/98/94 — GetCylinderExtent(this), ONCE
                                        // at the head (Engine.dll.c:135353, call 0x103e6ea6)
            public MoveCheck Hit;       // uStack_80 — FCheckResult(1.0) ONCE per call
                                        // (135357-135363); all traces and body moves go into it
                                        // (disasm [ebp-0x7c], built 0x103e6ecb..0x103e6ef2)
        }

        enum WalkGateResult { Continue, Return }

        // ── physWalking x87 chains. Shipped keeps these on the x87 stack at PC64 and rounds to float
        // only at fstp/fst dword; each helper follows its disasm range (checked against exact fractions computed from
        // the disasm). Helpers guarded by Fin use the plain
        // float formula for non-finite inputs (their compares use CompareTo/Sign, which refuse NaN); WalkSlopeProbe
        // and the P/Q/R ends run NaN through X87Ext. float·float and float+float are single roundings in C# and on x87
        // (the exact value fits 64 bits), so only chains through an unrounded register value need X87Ext. ──
        static bool Fin(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);

        // fmul of a float by a register value, fstp dword (a zero product keeps the IEEE sign: X87Ext carries it).
        static float MulF87(float c, X87Ext m) => R64(X87(c) * m).ToFloat();

        // (a.x·b.x + a.y·b.y) + a.z·b.z in the register.
        static X87Ext Dot87(Vector3 a, Vector3 b)
            => R64(R64(X87(a.x) * X87(b.x) + X87(a.y) * X87(b.y)) + X87(a.z) * X87(b.z));

        // moveSize, 0x103e72b5..0x103e72da: (Y·Y + Z·Z) + X·X, fstp qword, appSqrt, fstp dword.
        internal static float WalkMoveSize(Vector3 d)
        {
            if (!Fin(d)) return (float)Math.Sqrt(((double)d.y * d.y + (double)d.z * d.z) + (double)d.x * d.x);
            return Vector3.Size64Ext(d.y, d.z, d.x).ToFloat();
        }

        // B probe depth, 0x103e72dd..0x103e7326: A = ((CH + R) + moveSize) + 4 and B = (CH + MaxStep) + 4 in the
        // register; B < A stores A, otherwise the float B (fst) — each rounded once.
        internal static float WalkProbeDepth(float ch, float r, float moveSize, float maxStep)
        {
            if (!float.IsFinite(ch) || !float.IsFinite(r) || !float.IsFinite(moveSize) || !float.IsFinite(maxStep))
                return Math.Max(ch + r + moveSize + 4f, ch + maxStep + 4f);
            X87Ext a = R64(R64(R64(X87(ch) + X87(r)) + X87(moveSize)) + X87(4f));
            X87Ext b = R64(R64(X87(ch) + X87(maxStep)) + X87(4f));
            return b.CompareTo(a) < 0 ? a.ToFloat() : b.ToFloat();
        }

        // The far-slope half of the C condition, 0x103e7437..0x103e74be: N.z > 0.7 (double 0x10479e48);
        // (CH + MaxStep) + 4 < depth·time in the register; s = appSqrt(1 − N.z²) stored as DOUBLE;
        // (CH + 4) + ((moveSize + R)·s)/N.z < depth·time in the register. A negative 1 − N.z² gives NaN: the
        // compare is unordered (C0 set), the je is not taken and C runs.
        internal static bool WalkFarSlope(float nz, float bTime, float ch, float r, float maxStep, float moveSize, float depth)
        {
            if (!float.IsFinite(nz) || !float.IsFinite(bTime) || !float.IsFinite(ch) || !float.IsFinite(r)
                || !float.IsFinite(maxStep) || !float.IsFinite(moveSize) || !float.IsFinite(depth))
                return nz > 0.7f && ch + maxStep + 4f < depth * bTime
                    && ch + 4f + ((moveSize + r) * MathF.Sqrt(1f - nz * nz)) / nz < depth * bTime;
            if (!((double)nz > 0.7)) return false;
            X87Ext d = X87(depth) * X87(bTime);
            if (!(R64(R64(X87(ch) + X87(maxStep)) + X87(4f)).CompareTo(d) < 0)) return false;
            X87Ext a = R64(X87(1f) - X87(nz) * X87(nz));
            if (a.Sign < 0) return true;
            X87Ext s = X87Ext.From(X87Ext.Sqrt64(X87Ext.From(a.ToDouble())).ToDouble());
            X87Ext q = X87Ext.Div64(R64(R64(X87(moveSize) + X87(r)) * s), X87(nz));
            return R64(R64(X87(ch) + X87(4f)) + q).CompareTo(d) < 0;
        }

        // C probe (135521-135527, 0x103e7423..0x103e75ac): a B miss (time == 1) leads to C JUST LIKE a far gently sloped
        // floor; the ledge is declared only from C's result. cTrace(start, end, extent, flags) answers "hit".
        // Checked against the real original fragment under unicorn.
        internal static bool WalkLedge(bool bHit, float bTime, Vector3 bNormal, float ch, float r, float maxStep, float moveSize,
            float probeDepth, Vector3 aEnd, Vector3 gravDir, float thick, Func<Vector3, Vector3, Vector3, int, bool> cTrace)
        {
            bool ledge = false;
            if (!bHit || WalkFarSlope(bNormal.z, bTime, ch, r, maxStep, moveSize, probeDepth))
                ledge = !cTrace(aEnd, aEnd + gravDir * (maxStep + 4f), new Vector3(thick, thick, ch), 6);
            return ledge;
        }

        // Ledge direction, 0x103e7641..0x103e769e: r = 1/moveSize stored as float, each component float(c·r).
        internal static Vector3 WalkDeltaDir(Vector3 delta, float moveSize)
        {
            float r = 1f / moveSize;
            return new Vector3(delta.x * r, delta.y * r, delta.z * r);
        }

        // D clear sidestep, 0x103e7b30..0x103e7be8: m = min(DesiredSpeed, 0.8f)·GroundSpeed·sub in the register;
        // side = (m·deltaDir.y, (deltaDir.x·−1)·m, 0).
        internal static Vector3 WalkSidestep(Vector3 deltaDir, float desiredSpeed, float groundSpeed, float sub)
        {
            if (!Fin(deltaDir) || !float.IsFinite(desiredSpeed) || !float.IsFinite(groundSpeed) || !float.IsFinite(sub))
                return new Vector3(deltaDir.y, -deltaDir.x, 0f) * (Math.Min(desiredSpeed, 0.8f) * groundSpeed * sub);
            X87Ext m = R64(R64(X87(0.8f > desiredSpeed ? desiredSpeed : 0.8f) * X87(groundSpeed)) * X87(sub));
            return new Vector3(MulF87(deltaDir.y, m), MulF87(-deltaDir.x, m), 0f);
        }

        // D hit, steer along the edge, 0x103e793b..0x103e7b2b: p = SafeNormal(N.y, −N.x, 0); the alignment
        // (p.x·A.x + A.y·p.y) + A.z·p.z stays in the register (negative: p *= −1, recomputed); wall-ish below 0.5 or,
        // for bCanJump, below 0.7 (double constants); at ≥ 0.7 s = GroundSpeed·DesiredSpeed·sub, below it
        // min(DesiredSpeed, 0.8f)·GroundSpeed·sub, each rounded once; adjust = p·s per component.
        internal static (Vector3 adjust, bool wallish) WalkAlongEdge(Vector3 n, Vector3 accelDir, float desiredSpeed,
            float groundSpeed, float sub, bool canJump)
        {
            if (!Fin(n) || !Fin(accelDir) || !float.IsFinite(desiredSpeed) || !float.IsFinite(groundSpeed) || !float.IsFinite(sub))
            {
                Vector3 perp = new Vector3(n.y, -n.x, 0f);
                float pm = perp.magnitude;
                if (pm > 0f) perp /= pm;
                if (Vector3.Dot(perp, accelDir) < 0f) perp = -perp;
                float along = Vector3.Dot(perp, accelDir);
                return (along >= 0.7f ? perp * (groundSpeed * desiredSpeed * sub)
                        : perp * (groundSpeed * Math.Min(desiredSpeed, 0.8f) * sub),
                    along < 0.5f || (canJump && along < 0.7f));
            }
            Vector3 p = new Vector3(n.y, -n.x, 0f).normalized;
            if (Dot87(p, accelDir).Sign < 0) p = -p;
            X87Ext d = Dot87(p, accelDir);
            bool below07 = d.CompareTo(X87Ext.From(0.7)) < 0;
            bool wallish = d.CompareTo(X87Ext.From(0.5)) < 0 || (canJump && below07);
            float s = !below07 ? R64(R64(X87(groundSpeed) * X87(desiredSpeed)) * X87(sub)).ToFloat()
                : R64(R64(X87(0.8f > desiredSpeed ? desiredSpeed : 0.8f) * X87(groundSpeed)) * X87(sub)).ToFloat();
            return (p * s, wallish);
        }

        // D hit with bAvoidLedges, 0x103e788a..0x103e7910: s = ((GroundSpeed·DesiredSpeed)·sub)·−1 rounded once;
        // adjust = (N.x·s, N.y·s, 0.0) — Z is the constant 0.0, not 0·s.
        internal static Vector3 WalkBackOff(Vector3 n, float desiredSpeed, float groundSpeed, float sub)
        {
            if (!float.IsFinite(desiredSpeed) || !float.IsFinite(groundSpeed) || !float.IsFinite(sub))
                return new Vector3(n.x, n.y, 0f) * (-groundSpeed * desiredSpeed * sub);
            float s = -R64(R64(X87(groundSpeed) * X87(desiredSpeed)) * X87(sub)).ToFloat();
            return new Vector3(n.x * s, n.y * s, 0f);
        }

        // Wall-ish non-jumper probes I/K. I end, 0x103e8076..0x103e80e1: moveSize + R in the register, loc +
        // float(deltaDir·it). K, 0x103e81dd..0x103e8262: X, Y float(float(deltaDir·moveSize) + loc); Z keeps
        // deltaDir.z·moveSize + loc.z in the register — the start stores it (fst), the end adds down.z to it.
        internal static (Vector3 iEnd, Vector3 kStart, Vector3 kEnd) WalkStraightProbes(Vector3 loc, Vector3 deltaDir,
            float moveSize, float r, Vector3 down)
        {
            if (!Fin(loc) || !Fin(deltaDir) || !float.IsFinite(moveSize) || !float.IsFinite(r) || !Fin(down))
            {
                Vector3 k0 = loc + deltaDir * moveSize;
                return (loc + deltaDir * (moveSize + r), k0, k0 + down);
            }
            X87Ext l = R64(X87(moveSize) + X87(r));
            Vector3 iEnd = new(MulF87(deltaDir.x, l) + loc.x, MulF87(deltaDir.y, l) + loc.y, MulF87(deltaDir.z, l) + loc.z);
            X87Ext zs = R64(X87(deltaDir.z) * X87(moveSize) + X87(loc.z));
            Vector3 kStart = new(deltaDir.x * moveSize + loc.x, deltaDir.y * moveSize + loc.y, zs.ToFloat());
            return (iEnd, kStart, new Vector3(kStart.x + down.x, kStart.y + down.y, R64(zs + X87(down.z)).ToFloat()));
        }

        // L normal against MinHitWall, 0x103e84b7..0x103e84e1: the dot product in the register < the float.
        internal static bool WalkDotBelow(Vector3 a, Vector3 b, float limit)
        {
            if (!Fin(a) || !Fin(b) || !float.IsFinite(limit)) return Vector3.Dot(a, b) < limit;
            return Dot87(a, b).CompareTo(X87(limit)) < 0;
        }

        // O probe, 0x103e86c8..0x103e87b0: s = appSqrt((Y·Y + Z·Z) + X·X) is never rounded to float; r = 1/s
        // (fdiv st, st(1)); dir = float(c·r); L = max(s + 4, 30f) in the register (`fld 30; fcomp st(1); test ah,1;
        // jne` keeps s + 4 only when 30 is below it); end = float(float(dir·L) + loc) per component.
        // A zero adjust (bHitSlopedWall set and adjust zeroed by the G/H ledge stop or the N probe) has no guard in
        // shipped: s = +0, 1/+0 = +inf (zero divide masked), 0·inf is invalid → dir = the real indefinite 0xFFC00000
        // per component, L = 30 and end = (indefinite, indefinite, indefinite). X87Ext carries that NaN bit for bit.
        internal static (Vector3 dir, Vector3 end) WalkSlopeProbe(Vector3 adjust, Vector3 loc)
        {
            X87Ext s = Vector3.Size64Ext(adjust.y, adjust.z, adjust.x);
            X87Ext rr = X87Ext.Div64(X87(1f), s);
            Vector3 dir = new(MulF87(adjust.x, rr), MulF87(adjust.y, rr), MulF87(adjust.z, rr));
            X87Ext l = R64(s + X87(4f));
            if ((X87Ext.Fcom(X87(30f), l) & X87Ext.C0) == 0) l = X87(30f);
            static float End(float d, X87Ext l, float c) => R64(X87(MulF87(d, l)) + X87(c)).ToFloat();
            return (dir, new Vector3(End(dir.x, l, loc.x), End(dir.y, l, loc.y), End(dir.z, l, loc.z)));
        }

        // The bHitSlopedWall machinery of a move with the flag set, 0x103e86c8..0x103e8e40: O from loc along adjust
        // (WalkSlopeProbe, extent cyl); O on the slope → adjust slides along its z-flattened normal (136079-136093,
        // 0x103e885a-0x103e88f3); otherwise a walking player re-probes P, Q, R from base (WalkPlayerReprobes, zero
        // extent) until one finds the slope — they only rewrite the flag, the move goes without projection
        // (0x103e88f8-0x103e8c58 → jmp 0x103e8e40). Every trace is SingleLineCheck flags 6 into the walk's record; the
        // flag is written after each test (0x103e884e, 0x103e8a82, 0x103e8c4b, 0x103e8e3a), nothing in between reads
        // it, so the caller stores the last value. Checked against the original under unicorn.
        internal static Vector3 WalkSlopedWallProbes(Vector3 adjust, Vector3 loc, Vector3 cyl, float ch, float maxStep,
            bool isPlayer, Func<Vector3, Vector3, Vector3, (bool hit, Vector3 normal)> trace, out bool sloped)
        {
            (Vector3 dir, Vector3 oEnd) = WalkSlopeProbe(adjust, loc);
            (bool oHit, Vector3 oNormal) = trace(loc, oEnd, cyl);
            sloped = WalkSlopedWall(oHit, oNormal.z);
            if (sloped) return WalkSlopeProject(adjust, oNormal);
            if (!isPlayer) return adjust;
            (Vector3 baseP, Vector3 pEnd, Vector3 qEnd, Vector3 rEnd) = WalkPlayerReprobes(loc, ch, maxStep, dir);
            foreach (Vector3 probeEnd in new[] { pEnd, qEnd, rEnd })
            {
                (bool pHit, Vector3 pNormal) = trace(baseP, probeEnd, Vector3.zero);
                sloped = WalkSlopedWall(pHit, pNormal.z);
                if (sloped) break;
            }
            return adjust;
        }

        // Walking-player re-probes P/Q/R, 0x103e8967..0x103e8d8c: base Z = ((loc.z − CH) + MaxStep) + 4 rounded once.
        // P = base + dir·100 and Q = base + SafeNormal(dir.x+dir.y, dir.y−dir.x, dir.z)·100 keep Z in the register
        // (n.z·100 + base.z); R = base + SafeNormal(dir.x−dir.y, dir.x+dir.y, dir.z)·100 is float per component.
        // The NaN dir of a zero adjust (WalkSlopeProbe): P ends in indefinites, SafeNormal of the NaN sums is zero
        // (Core 0x1014f504 `fcom 1e-8; test ah,1` takes C0 on unordered), so Q and R end at base.
        internal static (Vector3 start, Vector3 p, Vector3 q, Vector3 r) WalkPlayerReprobes(Vector3 loc, float ch,
            float maxStep, Vector3 dir)
        {
            Vector3 qn = new Vector3(dir.x + dir.y, dir.y - dir.x, dir.z).normalized;
            Vector3 rn = new Vector3(dir.x - dir.y, dir.x + dir.y, dir.z).normalized;
            if (!Fin(loc) || !float.IsFinite(ch) || !float.IsFinite(maxStep))
            {
                Vector3 b0 = new(loc.x, loc.y, loc.z - ch + maxStep + 4f);
                return (b0, b0 + dir * 100f, b0 + qn * 100f, b0 + rn * 100f);
            }
            float bz = R64(R64(R64(X87(loc.z) - X87(ch)) + X87(maxStep)) + X87(4f)).ToFloat();
            Vector3 b = new(loc.x, loc.y, bz);
            Vector3 End(Vector3 n) => new(n.x * 100f + b.x, n.y * 100f + b.y, R64(X87(n.z) * X87(100f) + X87(bz)).ToFloat());
            return (b, End(dir), End(qn), b + rn * 100f);
        }

        // Slide along O's wall, 0x103e885a..0x103e88f3: n = SafeNormal(O.x, O.y, 0); the dot
        // (n.x·a.x + a.y·n.y) + a.z·n.z stays in the register; a_i − float(dot·n_i).
        internal static Vector3 WalkSlopeProject(Vector3 adjust, Vector3 oNormal)
        {
            if (!Fin(adjust) || !Fin(oNormal))
            {
                Vector3 flat = new(oNormal.x, oNormal.y, 0f);
                float fm = flat.magnitude;
                if (fm > 0f) flat /= fm;
                return adjust - flat * Vector3.Dot(adjust, flat);
            }
            Vector3 n = new Vector3(oNormal.x, oNormal.y, 0f).normalized;
            X87Ext d = Dot87(n, adjust);
            return new Vector3(adjust.x - MulF87(n.x, d), adjust.y - MulF87(n.y, d), adjust.z - MulF87(n.z, d));
        }

        // Unused-time give-back of both falling tails, 0x103e90a5..0x103e9110 and 0x103ea0ca..0x103ea145:
        // moved2D = appSqrt(dx·dx + dy·dy) (two terms, order-free) in the register; remaining +
        // (1 − min(moved2D / intended, 1))·sub, one fstp. intended ≠ 0 (both callers branch on it first).
        internal static float WalkRemainingGiveBack(float intended, float dx, float dy, float sub, float remaining)
        {
            if (!float.IsFinite(intended) || !float.IsFinite(dx) || !float.IsFinite(dy) || !float.IsFinite(sub)
                || !float.IsFinite(remaining))
                return remaining + (1f - Math.Min(new Vector3(dx, dy, 0f).magnitude / intended, 1f)) * sub;
            X87Ext ratio = X87Ext.Div64(Vector3.Size64Ext(dx, dy, 0f), X87(intended));
            X87Ext m = X87(1f).CompareTo(ratio) > 0 ? ratio : X87(1f);
            return R64(R64(R64(X87(1f) - m) * X87(sub)) + X87(remaining)).ToFloat();
        }

        // Stationary hover band, 0x103e9952..0x103e9977: time·20 in the register, settled when it is neither above
        // 4.6f nor below 4.1f.
        internal static bool WalkHoverSettled(float time)
        {
            if (!float.IsFinite(time)) return time * 20f >= 4.1f && time * 20f <= 4.6f;
            X87Ext v = X87(time) * X87(20f);
            return !(v.CompareTo(X87(4.6f)) > 0) && !(v.CompareTo(X87(4.1f)) < 0);
        }

        // Step-down drop, 0x103e9a14..0x103e9a23: (MaxStepHeight + 2)·time, one fstp.
        internal static float WalkDropDist(float maxStep, float time)
        {
            if (!float.IsFinite(maxStep) || !float.IsFinite(time)) return (maxStep + 2f) * time;
            return R64(R64(X87(maxStep) + X87(2f)) * X87(time)).ToFloat();
        }

        // 0x103e9b27: the hover lift compares the float drop with the DOUBLE 1.9 (0x1047a5a8).
        internal static bool WalkHoverLift(float dropDist) => (double)dropDist < 1.9;

        // Slope slip, 0x103e9c26..0x103e9e25: N.z < 1 and N.z·GroundFriction < 3.3f in the register; f =
        // max(GroundFriction, 0.5f); S = float(float(float(dt·G)·(1/(f+f)))·dt); dot = (S.z·N.z + S.x·N.x) + S.y·N.y
        // in the register; NN = S − float(dot·N); move when (NN.x·S.x + NN.z·S.z) + NN.y·S.y ≥ 0 (register).
        internal static (bool slip, bool move, Vector3 nonNormal) WalkSlopeSlip(Vector3 n, float groundFriction,
            Vector3 gravity, float dt)
        {
            if (!Fin(n) || !float.IsFinite(groundFriction) || !Fin(gravity) || !float.IsFinite(dt))
            {
                if (!(n.z < 1f && n.z * groundFriction < 3.3f)) return (false, false, Vector3.zero);
                Vector3 slide = gravity * (dt * dt / (2f * Math.Max(groundFriction, 0.5f)));
                Vector3 nn0 = slide - n * Vector3.Dot(slide, n);
                return (true, Vector3.Dot(nn0, slide) >= 0f, nn0);
            }
            if (!(n.z < 1f && R64(X87(n.z) * X87(groundFriction)).CompareTo(X87(3.3f)) < 0)) return (false, false, Vector3.zero);
            float f = 0.5f < groundFriction ? groundFriction : 0.5f;
            X87Ext inv = X87Ext.Div64(X87(1f), R64(X87(f) + X87(f)));
            Vector3 s = new(MulF87(dt * gravity.x, inv) * dt, MulF87(dt * gravity.y, inv) * dt, MulF87(dt * gravity.z, inv) * dt);
            X87Ext d = Dot87(new Vector3(s.z, s.x, s.y), new Vector3(n.z, n.x, n.y));
            Vector3 nn = new(s.x - MulF87(n.x, d), s.y - MulF87(n.y, d), s.z - MulF87(n.z, d));
            return (true, Dot87(new Vector3(nn.x, nn.z, nn.y), new Vector3(s.x, s.z, s.y)).Sign >= 0, nn);
        }

        // Falling-exit velocity, 0x103ea1fb..0x103ea24d: 1/(dt − remaining) in the register, float(d·it) per component.
        // (The former double form gives the same floats unless dt − remaining needs more than 53 bits.)
        internal static Vector3 WalkFallVelocity(Vector3 d, float dt, float remaining)
        {
            if (!Fin(d) || !float.IsFinite(dt) || !float.IsFinite(remaining))
            {
                double inv0 = 1.0 / ((double)dt - remaining);
                return new Vector3((float)(inv0 * d.x), (float)(inv0 * d.y), (float)(inv0 * d.z));
            }
            X87Ext inv = X87Ext.Div64(X87(1f), R64(X87(dt) - X87(remaining)));
            return new Vector3(MulF87(d.x, inv), MulF87(d.y, inv), MulF87(d.z, inv));
        }

        // bHitSlopedWall test of the move, O and P/Q/R (0x103e8edd, 0x103e8802, 0x103e8a36, 0x103e8c01, 0x103e8df0):
        // fcomp 0.01f; test ah,0x41; jne — N.z must be ABOVE 0.01f (the decompile prints "< 0.01", losing the
        // equality); then N.z < 0.7f.
        internal static bool WalkSlopedWall(bool hit, float nz) => hit && nz > 0.01f && nz < 0.7f;

        // ── ONE moving substep (Engine.dll.c:135442-136195): the shipped
        // NPC/walking-player ledge look-ahead, the adjusted move with the
        // bHitSlopedWall machinery, stepUp on a block with the falling/flying
        // handoffs, the swim check and the walking-player auto-jump gate.
        //
        // Look-ahead (LAB_103e71d1, NPCs + bIsWalking players, non-flyers):
        //   A: point probe to (Loc + delta + accelDir·r·(bAvoidLedges?1:0.5))
        //      — a blocked A skips the whole machinery (plain move).
        //   B: point probe DOWN from A's end, depth max(CH+r+|delta|+4,
        //      CH+MaxStep+4); a walkable floor within reach cancels the ledge
        //      handling. A B MISS or the far-floor slope allowance re-probes C:
        //      thick (min(r/2,14), ·, CH) box down MaxStep+4 — the ledge is
        //      declared only when C misses too (0x103e7423-0x103e7431).
        //   Everything below (D…N, the player G/H, MayFall) runs ONLY on a
        //      ledge: no ledge jumps straight to the move (0x103e75ac →
        //      0x103e86a0).
        //   Ledge found → D: point probe from the destination edge
        //      (edge + g·(CH+6)) to Location + g·(CH+6) − accelDir·2r
        //      (0x103e7764-0x103e77fc):
        //      - D clear (open air both sides): perpendicular SIDESTEP
        //        (deltaDir.y, −deltaDir.x, 0)·min(DesiredSpeed,0.8)·
        //        GroundSpeed·sub, written into the adjust BEFORE the E probe;
        //        E forward + F floor cylinder probes — both clear flips the
        //        sidestep sign blindly, a hit on either keeps the sidestep
        //        (135598-135641, 0x103e7be8-0x103e7c0c).
        //      - D hit, !bAvoidLedges: steer ALONG the edge — the
        //        horizontal perpendicular of the edge normal signed toward
        //        accelDir, wall-ish when the alignment < 0.5 (or < 0.7 for
        //        a bCanJump pawn), speed clamped to min(DesiredSpeed,0.8)
        //        below 0.7 alignment (135643-135684).
        //      - D hit, bAvoidLedges: back off along the horizontal normal,
        //        MoveTimer −0.25 (−1 with bStopAtLedges) (135686-135703).
        //   Walking player (LAB_103e7d25): forward+floor cylinder probes of
        //      the adjust; both clear → Acceleration and the adjust zeroed
        //      (the walking player stops at the ledge) (135705-135751).
        //   MayFall pre-approval (LAB_103e7f53): a bCanJump pawn on a
        //      wall-ish edge consults the probed MayFall ONCE; if bCanJump
        //      survives, willFall=1 and the move reverts to accelDir·|delta|
        //      — the gate below then falls WITHOUT the displacement-velocity
        //      correction (135755-135787).
        //   Non-jumper (bCanJump re-read LIVE at 0x103e805f, so a MayFall
        //      veto lands here too), wall-ish only (135789-135851): I/J/K
        //      probes — an I HIT (wall ahead) or a J+K floor both walk
        //      straight at deltaDir·|delta| (0x103e8123-0x103e8131,
        //      0x103e82ce-0x103e8323); otherwise the appFrand MoveTimer
        //      jitter (r ≥ 2·sub → −0.1, else −1). Then, wall-ish OR NOT
        //      (0x103e806e je 0x103e835e), unless the sidestep was probed:
        //      the L/M adjust probes (a head-on wall below the MinHitWall
        //      alignment aborts via MoveTimer=−1) and the N self-floor probe
        //      → Acceleration zeroed, remaining zeroed, back-off or freeze
        //      (135853-135918).
        // Move (LAB_103e86bb): bHitSlopedWall clear → plain move, the flag
        //   recomputed from the hit (0.01 ≤ n.z < 0.7). Flag set → the O
        //   probe (dir·max(dist+4,30)); a slope wall found BY O projects the
        //   delta off O's z-flattened normal (0x103e88c0-0x103e88f3,
        //   136079-136093). O finding none: players re-probe P/Q/R (100-unit
        //   point probes at feet+MaxStep+4 along dir and both diagonals),
        //   which only re-set the flag — the move goes unprojected
        //   (0x103e88f8-0x103e8c58 → jmp 0x103e8e40).
        // Post (136100-136195): blocked → stepUp; a stepUp that left the
        //   pawn falling gives back the unmoved 2D fraction, fires Falling
        //   and hands the remainder to physFalling THIS tick; a flying exit
        //   sets Velocity=(0,0,AirSpeed)/Acceleration=(0,0,AccelRate) and
        //   hands the remainder to physFlying (136141-136158, a branch added by
        //   KnowWonder); PHYS_Projectile returns; PHYS_Swimming is a loud
        //   STOP; ANY OTHER mode (Walking, Flying set by a HitWall handler,
        //   None…) continues into the auto-jump and step-down below —
        //   shipped has no «Physics≠Walking → return» here (0x103e9222 →
        //   0x103e9293); the bAutoJump walking-player ledge jump is ported
        //   (UELevelRuntime.Trajectory.cs). ──
        WalkGateResult MovingSubstep(UEInterpreter interp, UEObject actor, Vector3 delta,
            bool isPlayer, ref WalkState walk)
        {
            float r = UEInterpreter.AsFloat(actor.GetProp(PK.CollisionRadius));
            float ch = UEInterpreter.AsFloat(actor.GetProp(PK.CollisionHeight));
            float maxStep = UEInterpreter.AsFloat(actor.GetProp(PK.MaxStepHeight));
            float groundSpeed = UEInterpreter.AsFloat(actor.GetProp(PK.GroundSpeed));
            float desiredSpeed = UEInterpreter.AsFloat(actor.GetProp(PK.DesiredSpeed));
            bool avoidLedges = UEInterpreter.ToBool(actor.GetProp(PK.bAvoidLedges));
            bool canJump = UEInterpreter.ToBool(actor.GetProp(PK.bCanJump));
            Vector3 gravDir = new(0f, 0f, walk.GravSign);
            // Shipped runs probes E/F/G/I/J/K/L/M/O with extent uStack_9c —
            // GetCylinderExtent (135624…135965), not (R, R, H).
            Vector3 cyl = walk.Extent;
            Vector3 loc = walk.PreSubstepLoc;
            Vector3 accelDir = walk.AccelDir;
            Vector3 adjust = delta;
            Vector3 lookAhead = accelDir * (r * (avoidLedges ? 1f : 0.5f));
            Vector3 downStep = gravDir * (maxStep + 6f);
            bool wallish = false, probedSide = false;
            // A moving substep has a component of at least 1e-4 (0x103e706c..0x103e70d0), so moveSize > 0: shipped has
            // no guard (0x103e7641), the one here is never taken.
            float moveSize = WalkMoveSize(delta);
            Vector3 deltaDir = moveSize > 0f ? WalkDeltaDir(delta, moveSize) : Vector3.zero;
            bool useMachinery = !isPlayer || UEInterpreter.ToBool(actor.GetProp(PK.bIsWalking));
            bool runMayFallBlock = false;

            if (useMachinery && !UEInterpreter.ToBool(actor.GetProp(PK.bCanFly)))
            {
                (bool aHit, _, _, _) = WalkTrace(interp, actor, walk.Hit, loc,
                    loc + delta + lookAhead, Vector3.zero, 6);
                if (!aHit)
                {
                    Vector3 aEnd = loc + delta + lookAhead;
                    float probeDepth = WalkProbeDepth(ch, r, moveSize, maxStep);
                    (bool bHit, float bTime, Vector3 bNormal, _) = LineTrace(interp, actor,
                        aEnd, aEnd + gravDir * probeDepth, Vector3.zero, 6);
                    float thick = Math.Min(r * 0.5f, 14f);
                    MoveCheck hitRecord = walk.Hit;
                    bool ledge = WalkLedge(bHit, bTime, bNormal, ch, r, maxStep, moveSize, probeDepth, aEnd, gravDir, thick,
                        (s, e, x, f) => { (bool h, _, _, _) = WalkTrace(interp, actor, hitRecord, s, e, x, f); return h; });
                    // No ledge — straight to the move (0x103e75ac je 0x103e86a0): no
                    // D…N, no player G/H, no MayFall.
                    if (ledge)
                    {
                        runMayFallBlock = true;
                        Vector3 edgeBase = loc + accelDir * moveSize + lookAhead;
                        Vector3 dStart = edgeBase + gravDir * (ch + 6f);
                        // D's end is from Location, not from the edge (135565-135592,
                        // 0x103e7764-0x103e77fc: [edi] = this+0xfc).
                        Vector3 dEnd = loc + gravDir * (ch + 6f) - accelDir * (2f * r);
                        (bool dHit, _, Vector3 dNormal, _) = LineTrace(interp, actor,
                            dStart, dEnd, Vector3.zero, 6);
                        if (!dHit)
                        {
                            // Open air on the near side too — blind sidestep.
                            wallish = true;
                            Vector3 side = WalkSidestep(deltaDir, desiredSpeed, groundSpeed, walk.Sub);
                            // The sidestep is written into adjust BEFORE the E probe
                            // (0x103e7ba8-0x103e7be8) and stays when E/F hit.
                            adjust = side;
                            (bool eHit, _, _, _) = WalkTrace(interp, actor, walk.Hit, loc,
                                loc + side, cyl, 6);
                            if (!eHit)
                            {
                                (bool fHit, _, _, _) = WalkTrace(interp, actor, walk.Hit, loc + side,
                                    loc + side + downStep, cyl, 6);
                                if (!fHit) { adjust = -side; goto ApplyAdjust; }
                            }
                            probedSide = true;
                        }
                        else if (!avoidLedges)
                        {
                            // Steer along the edge.
                            (adjust, wallish) = WalkAlongEdge(dNormal, accelDir, desiredSpeed, groundSpeed, walk.Sub, canJump);
                        }
                        else
                        {
                            // bAvoidLedges: back off along the horizontal normal.
                            wallish = false;
                            adjust = WalkBackOff(dNormal, desiredSpeed, groundSpeed, walk.Sub);
                            if (UEInterpreter.ToBool(actor.GetProp(PK.bStopAtLedges)))
                                actor.Props["MoveTimer"] = -1f;
                            else
                                actor.Props["MoveTimer"] =
                                    UEInterpreter.AsFloat(actor.GetProp(PK.MoveTimer)) - 0.25f;
                        }
                    }
                ApplyAdjust:
                    // Player G/H — only at a ledge (block 135705-135751 inside the
                    // 0x103e75ac branch); the goto reaches here only from the ledge branch.
                    if (ledge && isPlayer)
                    {
                        // The walking player stops accelerating into open air
                        // (135705-135751).
                        wallish = false;
                        if (!probedSide)
                        {
                            (bool gHit, _, _, _) = WalkTrace(interp, actor, walk.Hit, loc,
                                loc + adjust + lookAhead, cyl, 6);
                            if (!gHit)
                            {
                                Vector3 gEnd = loc + adjust + lookAhead;
                                (bool hHit, _, _, _) = WalkTrace(interp, actor, walk.Hit, gEnd,
                                    gEnd + downStep, new Vector3(thick, thick, ch), 6);
                                if (!hHit)
                                {
                                    actor.Props["Acceleration"] = Vector3.zero;
                                    adjust = Vector3.zero;
                                }
                            }
                        }
                    }
                }
            }

            if (runMayFallBlock)
            {
                // LAB_103e7f53 — MayFall pre-approval / the non-jumper wall
                // machinery. Only reached on a ledge (see above).
                if (canJump && wallish)
                {
                    // UObject::IsProbing(NAME_MayFall) (0x103e7f70-0x103e7f90: FName::Names[230], probe bit 33), not
                    // "a MayFall function exists": a state whose probe mask lacks MayFall walks straight (the else arm).
                    bool probed = UEInterpreter.IsProbing(actor, "MayFall");
                    if (!probed)
                    {
                        adjust = accelDir * moveSize;
                    }
                    else if (!walk.MayFallCalled)
                    {
                        walk.MayFallCalled = true;
                        wallish = false;   // 0x103e7fe8: xor edi, edi — before the call
                        Guard(() => UELifecycle.CallEvent(interp, actor, "MayFall"), "MayFall");
                        if (UEInterpreter.ToBool(actor.GetProp(PK.bCanJump)))
                        {
                            walk.WillFall = true;
                            adjust = accelDir * moveSize;
                        }
                    }
                }
                // 0x103e805f: bCanJump is re-read LIVE — a MayFall veto
                // (0x103e8009-0x103e8012 → 0x103e806e) lands here too.
                if (!UEInterpreter.ToBool(actor.GetProp(PK.bCanJump)))
                {
                    bool walkStraight = false;
                    if (wallish)   // 0x103e806e: test edi; je 0x103e835e
                    {
                        // I/J/K: an I hit (wall ahead) OR floor at J and K → walk
                        // straight (0x103e8123-0x103e8131 → 0x103e82ce-0x103e8323);
                        // stepUp will climb it.
                        (Vector3 iEnd, Vector3 kStart, Vector3 kEnd) = WalkStraightProbes(loc, deltaDir, moveSize, r, downStep);
                        (bool iHit, _, _, _) = WalkTrace(interp, actor, walk.Hit, loc,
                            iEnd, cyl, 6);
                        if (iHit) walkStraight = true;
                        else
                        {
                            (bool jHit, _, _, _) = WalkTrace(interp, actor, walk.Hit, iEnd,
                                iEnd + downStep, cyl, 6);
                            if (jHit)
                            {
                                (bool kHit, _, _, _) = WalkTrace(interp, actor, walk.Hit, kStart,
                                    kEnd, cyl, 6);
                                walkStraight = kHit;
                            }
                        }
                        if (walkStraight)
                            adjust = deltaDir * moveSize;
                        else
                        {
                            // The shipped MoveTimer jitter (135844-135851) — the
                            // ONE shared appFrand stream.
                            if (FrandHook is null)
                                throw new NotSupportedException(
                                    $"physWalking wall jitter reached (Engine.dll.c:135844) actor={actor.GetProp(PK.Name)} without FrandHook");
                            // 0x103e832e: fstp dword — the comparison uses the float store.
                            actor.Props["MoveTimer"] = FrandHook().ToFloat() >= walk.Sub + walk.Sub
                                ? UEInterpreter.AsFloat(actor.GetProp(PK.MoveTimer)) - 0.1f
                                : -1f;
                        }
                    }
                    // L/M/N — with any wallish (0x103e835e), except "walk straight"
                    // and an already probed sidestep (fStack_90).
                    if (!walkStraight && !probedSide)
                    {
                        (bool lHit, _, Vector3 lNormal, _) = WalkTrace(interp, actor, walk.Hit, loc,
                            loc + adjust + lookAhead, cyl, 6);
                        bool clear = !lHit;
                        if (!lHit)
                        {
                            Vector3 lEnd = loc + adjust + lookAhead;
                            (bool mHit, _, _, _) = WalkTrace(interp, actor, walk.Hit, lEnd,
                                lEnd + downStep, cyl, 6);
                            clear = !mHit;
                        }
                        else if (WalkDotBelow(deltaDir, lNormal, UEInterpreter.AsFloat(actor.GetProp(PK.MinHitWall))))
                            actor.Props["MoveTimer"] = -1f;
                        if (clear)
                        {
                            float thickN = Math.Min(r * 0.5f, 14f);
                            (bool nHit, _, _, _) = WalkTrace(interp, actor, walk.Hit, loc,
                                loc + downStep, new Vector3(thickN, thickN, ch), 6);
                            actor.Props["Acceleration"] = Vector3.zero;
                            walk.Remaining = 0f;
                            actor.Props["MoveTimer"] = -1f;
                            adjust = nHit ? Vector3.zero
                                : deltaDir * (-groundSpeed * walk.Sub);
                        }
                    }
                }
            }

            // ── The move (LAB_103e86bb) with the bHitSlopedWall machinery. ──
            walk.Intended = adjust;
            float fraction; Vector3 normal; UEObject hitActor;
            if (!UEInterpreter.ToBool(actor.GetProp(PK.bHitSlopedWall)))
            {
                (fraction, normal, hitActor) = WalkMove(interp, actor, adjust, walk.Hit);
                actor.Props["bHitSlopedWall"] = WalkSlopedWall(fraction < 1f, normal.z);
            }
            else
            {
                MoveCheck record = walk.Hit;
                adjust = WalkSlopedWallProbes(adjust, loc, cyl, ch, maxStep, isPlayer, (start, end, extent) =>
                {
                    (bool hit, _, Vector3 n, _) = WalkTrace(interp, actor, record, start, end, extent, 6);
                    return (hit, n);
                }, out bool sloped);
                actor.Props["bHitSlopedWall"] = sloped;
                (fraction, normal, hitActor) = WalkMove(interp, actor, adjust, walk.Hit);
            }
            if (TraceMovesNow)
                Log($"walking_sweep actor={actor.GetProp(PK.Name)} from={walk.PreSubstepLoc}"
                    + $" delta={adjust} to={actor.GetProp(PK.Location)} fraction={fraction:R} normal={normal}"
                    + $" hit={hitActor?.GetProp(PK.Name)}");

            // ── Post (136100-136195). ──
            if (fraction < 1f)
            {
                if (normal.z > 0.7f) walk.FloorNormal = normal;
                Vector3 slideDir = adjust.sqrMagnitude > 0f ? adjust.normalized : Vector3.zero;
                StepUp(interp, actor, gravDir, slideDir, adjust * (1f - fraction), walk.Hit);
                int phys = UEInterpreter.ToInt(actor.GetProp(PK.Physics));
                if (phys == 2)
                {
                    // A script turned the pawn falling inside the step: give
                    // back the unmoved 2D fraction, fire Falling and hand the
                    // remainder to physFalling THIS tick (136114-136140).
                    // A zero |Intended| gives inf or NaN in shipped (0x103e90da), both replaced by 1: remaining unchanged.
                    float intended = walk.Intended.magnitude;
                    Vector3 nowLoc = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
                    if (intended > 0f)
                        walk.Remaining = WalkRemainingGiveBack(intended, nowLoc.x - walk.PreSubstepLoc.x,
                            nowLoc.y - walk.PreSubstepLoc.y, walk.Sub, walk.Remaining);
                    Guard(() => UELifecycle.CallEvent(interp, actor, "Falling"), "Falling");
                    if (UEInterpreter.ToInt(actor.GetProp(PK.Physics)) == 2)
                    {
                        if (walk.Remaining > 0.01f)
                            PhysFalling(interp, actor, walk.Remaining, walk.Iteration);
                        return WalkGateResult.Return;
                    }
                    // ── WALKING TO FLYING HANDOFF (Engine.dll.c:136141-136158) ──
                    //
                    // A KnowWonder addition — in stock UE1 physWalking simply returns after
                    // Falling. Body read byte by byte, field offsets checked against the
                    // shipped Pawn layout:
                    //
                    //   +0x12c/+0x130/+0x134 = Actor.Velocity     (x/y/z)
                    //   +0x13c/+0x140/+0x144 = Actor.Acceleration (x/y/z)
                    //   +0x2c4 = Pawn.AirSpeed,  +0x2c8 = Pawn.AccelRate
                    //
                    //   Velocity     = (0, 0, AirSpeed);
                    //   Acceleration = (0, 0, AccelRate);
                    //   if (remaining <= 0.01) return;
                    //   physFlying(remaining, Iterations);
                    //
                    // The 0.01 threshold and its place match the falling branch just above;
                    // both come AFTER the Falling event, and the order matters: the script
                    // may change physics in the handler, and its choice is what these two
                    // comparisons read.
                    if (UEInterpreter.ToInt(actor.GetProp(PK.Physics)) == 4)
                    {
                        actor.Props["Velocity"] = new Vector3(0f, 0f,
                            UEInterpreter.AsFloat(actor.GetProp(PK.AirSpeed)));
                        actor.Props["Acceleration"] = new Vector3(0f, 0f,
                            UEInterpreter.AsFloat(actor.GetProp(PK.AccelRate)));
                        // STATUS: PORTED only — no activating fixture, and reachability in
                        // the campaign is NOT ESTABLISHED (a `Falling` handler cannot be found
                        // by searching extracted/Scripts — Engine classes there are import
                        // stubs). So the branch is LOUD: the first live hit shows in the
                        // journal, like the neighbouring `walking_fall`.
                        Log($"walking_fly actor={actor.GetProp(PK.Name)} "
                            + $"velocity={actor.GetProp(PK.Velocity)} "
                            + $"acceleration={actor.GetProp(PK.Acceleration)} "
                            + $"remaining={walk.Remaining}");
                        if (walk.Remaining > 0.01f)
                            PhysFlying(interp, actor, walk.Remaining);
                        return WalkGateResult.Return;
                    }
                    return WalkGateResult.Return;
                }
                if (phys == 6) return WalkGateResult.Return;
                // Other modes (except 2/6 above and 3 below) do NOT exit: shipped goes on
                // to auto-jump and step-down (0x103e9222 → 0x103e9293, 136159-136176).
            }
            if (UEInterpreter.ToInt(actor.GetProp(PK.Physics)) == 3)
                throw new NotSupportedException(
                    $"physWalking startSwimming reached (Engine.dll.c:136170) actor={actor.GetProp(PK.Name)} — port the shipped swim entry before using it");
            // bAutoJump BRANCH (Engine.dll.c:136196-136305) — UELevelRuntime.Trajectory.cs,
            // matched bit for bit against the real physWalking fragment.
            // Armed by the "Auto Jump" options menu checkbox (PlayerPawn.AutoJump); the flag
            // defaults to false, the ini does not set it.
            if (isPlayer && UEInterpreter.ToBool(actor.GetProp(PK.bAutoJump)))
            {
                LineCheckHit last = default;
                // Traces of the branch and the trajectory simulator go into uStack_80
                // (136272-136291, FUN_103e6310(&uStack_80, ...)).
                MoveCheck walkHit = walk.Hit;
                UEObject zone = (actor.GetProp(PK.Region) as UEObject)?.GetProp(PK.Zone) as UEObject ?? interp.GetLevelInfo();
                AutoJumpResult jump = AutoJumpGate(true, true, false,
                    UEInterpreter.AsVector3(actor.GetProp(PK.Location)), UEInterpreter.AsVector3(actor.GetProp(PK.Velocity)),
                    UEInterpreter.AsRotator(actor.GetProp(PK.Rotation)), UEInterpreter.AsFloat(actor.GetProp(PK.CollisionRadius)),
                    UEInterpreter.AsFloat(actor.GetProp(PK.CollisionHeight)), walk.Extent, UEInterpreter.AsFloat(actor.GetProp(PK.JumpZ)),
                    UEInterpreter.AsFloat(actor.GetProp(PK.MaxMountHeight)), UEInterpreter.AsVector3(zone.GetProp(PK.ZoneGravity)),
                    walk.AccelDir, new Vector3(0f, 0f, (maxStep + 2f) * walk.GravSign), walk.Sub, walk.Remaining,
                    (from, to, ext) =>
                    {
                        last = SingleLineCheck(interp, actor, null, from, to, ext, 7);
                        walkHit.FromTrace(last);
                        return (!last.Blocked, last.Time, last.Normal);
                    },
                    maxExtent =>
                    {
                        UEObject hitActor = last.Actor;
                        if (hitActor is null || ReferenceEquals(hitActor, interp.GetLevelInfo()))
                            return Bsp?.HitSurfaceFlags(last.Node, last.Location, last.Normal, maxExtent, null);
                        if (hitActor.GetProp(PK.Brush) is not null || UEInterpreter.ClassIsA(hitActor.Class, "Mover"))
                            return BrushModelOf(hitActor)?.HitSurfaceFlags(last.Node, last.Location, last.Normal, maxExtent,
                                BrushToLocalCoords(hitActor));
                        return null;                       // FCheckResult.Primitive is not UModel
                    });
                if (jump.Exit == AutoJumpExit.Jump)
                {
                    MoveActor(interp, actor, jump.MoveDelta, UEInterpreter.AsRotator(actor.GetProp(PK.Rotation)),
                        false, false, false, false);
                    Guard(() => UELifecycle.CallEvent(interp, actor, "DoJump", 1f), "DoJump");
                    walk.Remaining = jump.Remaining;
                    // LAB_103ea28d: physFalling(Remaining, Iterations); return.
                    PhysFalling(interp, actor, walk.Remaining, walk.Iteration);
                    return WalkGateResult.Return;
                }
            }
            return WalkGateResult.Continue;
        }

        // ── The COMMON physWalking step-down + no-floor gate, shared by the
        // stationary and moving substeps (Engine.dll.c:136333-136544).
        //  1. TRACE (not a move) (MaxStepHeight+2) along the gravity sign
        //     with the cylinder extent, flags 7 (136333-136341).
        //  2. A hit on the CURRENT Base with a drop distance ≤ 2.4 is the
        //     hover band (136367→LAB_103e9b27): below 1.9 the pawn is lifted
        //     by (2.1 − drop) and the hit time zeroed — pawns HOVER ~2 units
        //     above the hull contact; inside [1.9, 2.4] nothing moves.
        //  3. Any other hit COMMITS the drop (MoveActor 136368) and re-bases
        //     onto the move's hit actor (SetBase 136371-136373) — even a
        //     steep one; the gate below then takes it away via setPhysics.
        //  4. The gate (LAB_103e9bf0, 136383-136414): no hit OR Normal.z<0.7
        //     → the pawn may not stand here. bCanJump pawns consult MayFall
        //     ONCE per call if the event is probed (the script may clear
        //     bCanJump); then !bCanJump || bIsWalking = FULL STOP (Velocity/
        //     Acceleration zeroed, FarMoveActor back to OldLocation,
        //     MoveTimer=-1 — HP1 pawns default bCanJump=False, so NPCs stop
        //     at ledges; only jump-capable pawns fall off). Otherwise the
        //     Falling exit (LAB_103ea025, 136472-136529): bHitSlopedWall on
        //     a steep hit, unused substep time given back by the 2D travel
        //     over |Intended| (the post-look-ahead delta), Velocity.z=0, the
        //     Falling event, setPhysics(PHYS_Falling, NULL), xy velocity from the actual displacement, then
        //     physFalling with the REMAINING time — the SAME tick (136528).
        //  5. A walkable floor runs the slope-slip (136415-136454): on a
        //     non-flat floor with Normal.z·GroundFriction < 3.3 the pawn
        //     slides by Gravity·dt²/(2·max(GroundFriction, 0.5)) minus the
        //     floor-normal component.
        //  6. The willFall route (fStack_f0, set by the moving branch's
        //     MayFall pre-approval): the gate is BYPASSED — the falling exit
        //     runs without the displacement-velocity correction and without
        //     the same-tick physFalling; a final move consumes the remaining
        //     delta and the loop ends into the velocity tail (136383 +
        //     136502/136532-136542). ──
        WalkGateResult StepDownAndGate(UEInterpreter interp, UEObject actor, UEObject region,
            ref WalkState walk, bool stationary)
        {
            UEObject zone = region?.GetProp(PK.Zone) as UEObject;
            Vector3 gravity = UEInterpreter.AsVector3(zone?.GetProp(PK.ZoneGravity));
            float downSign = gravity.z > 0f ? 1f : -1f;
            float maxStep = UEInterpreter.AsFloat(actor.GetProp(PK.MaxStepHeight));
            float radius = UEInterpreter.AsFloat(actor.GetProp(PK.CollisionRadius));
            float height = UEInterpreter.AsFloat(actor.GetProp(PK.CollisionHeight));
            Vector3 stepDown = new(0f, 0f, downSign * (maxStep + 2f));
            UEObject level = interp.GetLevelInfo();

            Vector3 loc = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
            // Extent — uStack_9c (GetCylinderExtent, 136341).
            (bool blocked, float time, Vector3 normal, UEObject hitActor) =
                WalkTrace(interp, actor, walk.Hit, loc, loc + stepDown, walk.Extent, 7);
            if (!blocked) time = 1f;
            float dropDist = WalkDropDist(maxStep, time);
            if (TraceMovesNow)
                Log($"walking_step_down actor={actor.GetProp(PK.Name)} location={loc}"
                    + $" fraction={time:R} drop={dropDist:R} normal={normal}"
                    + $" hit={hitActor?.GetProp(PK.Name)}");
            if (time < 1f)
            {
                if (normal.z > 0.7f) walk.FloorNormal = normal;
                UEObject curBase = actor.GetProp(PK.Base) as UEObject;
                if (ReferenceEquals(hitActor ?? level, curBase) && dropDist <= 2.4f)
                {
                    // Hover band (LAB_103e9b27): lift by (2.1 − drop) when the
                    // gap shrank below 1.9; the shipped code saves/restores the
                    // trace normal around the lift and zeroes the hit time.
                    if (WalkHoverLift(dropDist))
                    {
                        WalkMove(interp, actor, new Vector3(0f, 0f, 2.1f - dropDist), walk.Hit);
                        time = 0f;
                        // The FCheckResult itself too (0x103e9b27..: fStack_5c = 0,
                        // trace normal returned into the record; Actor from the move).
                        walk.Hit.Time = 0f;
                        walk.Hit.Normal = normal;
                    }
                }
                else
                {
                    // Commit the drop; the MOVE's hit feeds the gate and the
                    // re-base, exactly like shipped's shared FCheckResult —
                    // including SetBase(NULL) when the move found nothing
                    // (136371 compares the move's Actor, NULL on a clean
                    // move). The hook reports null for BOTH world hits and
                    // clean moves; the fraction disambiguates. The Hit is the
                    // one the floor trace just filled (0xc4 then 0x8c on the
                    // same &uStack_80, 136343/136367): a REFUSED or fast-path
                    // move leaves the trace result for the gate — walk.Hit
                    // carries it (WalkTrace above wrote it).
                    (time, normal, hitActor) = WalkMove(interp, actor, stepDown, walk.Hit);
                    UEObject floorActor = time < 1f ? hitActor ?? level : null;
                    if (!ReferenceEquals(floorActor, actor.GetProp(PK.Base) as UEObject))
                        SetBase(interp, actor, floorActor, notify: true);
                    if (UEInterpreter.ToInt(actor.GetProp(PK.Physics)) == 3)
                        throw new NotSupportedException(
                            $"physWalking startSwimming reached (Engine.dll.c:136377) actor={actor.GetProp(PK.Name)} — port the shipped swim entry before using it");
                }
            }

            // LAB_103e9bf0 — the no-floor gate. The willFall route (the
            // moving branch's MayFall pre-approval) bypasses it and goes
            // STRAIGHT to the falling exit (136383-136384).
            if (walk.WillFall || time >= 1f || normal.z < 0.7f)
            {
                if (!walk.WillFall)
                {
                    bool canJump = UEInterpreter.ToBool(actor.GetProp(PK.bCanJump));
                    if (canJump && !walk.MayFallCalled
                        && UEInterpreter.IsProbing(actor, "MayFall"))   // UObject::IsProbing, 0x103e9f02-0x103e9f22
                    {
                        walk.MayFallCalled = true;
                        Guard(() => UELifecycle.CallEvent(interp, actor, "MayFall"), "MayFall");
                        canJump = UEInterpreter.ToBool(actor.GetProp(PK.bCanJump));
                    }
                    if (!canJump || UEInterpreter.ToBool(actor.GetProp(PK.bIsWalking)))
                    {
                        // FULL STOP at the ledge (136396-136411).
                        actor.Props["Velocity"] = Vector3.zero;
                        actor.Props["Acceleration"] = Vector3.zero;
                        FarMoveTo(interp, actor, UEInterpreter.AsVector3(actor.GetProp(PK.OldLocation)));
                        actor.Props["MoveTimer"] = -1f;
                        Log($"walking_ledge_stop actor={actor.GetProp(PK.Name)} location={actor.GetProp(PK.Location)}");
                        return WalkGateResult.Return;
                    }
                }
                // The Falling exit (LAB_103ea025).
                if (time < 1f) actor.Props["bHitSlopedWall"] = true;
                // Divisor is |Intended| (fStack_128 = delta AFTER the ledge
                // machinery, 136476-136490, 0x103ea043-0x103ea07a).
                float speed = walk.Intended.magnitude;
                Vector3 nowLoc = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
                if (speed == 0f) walk.Remaining = 0f;
                else walk.Remaining = WalkRemainingGiveBack(speed, nowLoc.x - walk.PreSubstepLoc.x,
                    nowLoc.y - walk.PreSubstepLoc.y, walk.Sub, walk.Remaining);
                Vector3 v = UEInterpreter.AsVector3(actor.GetProp(PK.Velocity));
                v.z = 0f;
                actor.Props["Velocity"] = v;
                Guard(() => UELifecycle.CallEvent(interp, actor, "Falling"), "Falling");
                if (UEInterpreter.ToInt(actor.GetProp(PK.Physics)) == 1)
                    SetPhysics(interp, actor, 2, null);
                Log(stationary
                    ? $"walking_fall actor={actor.GetProp(PK.Name)}"
                    : $"walking_falloff actor={actor.GetProp(PK.Name)}");
                if (!walk.WillFall && UEInterpreter.ToInt(actor.GetProp(PK.Physics)) == 2)
                {
                    // 136502-136529: xy velocity from the ACTUAL displacement
                    // over the consumed time (z keeps the post-event value —
                    // shipped saves it at 136503 and restores at 136522),
                    // then hand the remaining time to physFalling this tick.
                    Vector3 vel = UEInterpreter.AsVector3(actor.GetProp(PK.Velocity));
                    float zAfterEvent = vel.z;
                    if (!UEInterpreter.ToBool(actor.GetProp(PK.bJustTeleported))
                        && walk.Remaining < walk.FullDt)
                    {
                        // Location is read HERE, after the Falling event (Engine.dll.c:
                        // 136507-136510): the handler may have moved the pawn. Differences
                        // are float (FVector arguments), 1/(dt − rem) stays in the register.
                        Vector3 afterLoc = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
                        Vector3 oldLoc = UEInterpreter.AsVector3(actor.GetProp(PK.OldLocation));
                        vel = WalkFallVelocity(afterLoc - oldLoc, walk.FullDt, walk.Remaining);
                    }
                    vel.z = zAfterEvent;
                    actor.Props["Velocity"] = vel;
                    if (walk.Remaining > 0.01f)
                        PhysFalling(interp, actor, walk.Remaining, walk.Iteration);
                    return WalkGateResult.Return;
                }
                // The willFall route, or the Falling event moved the pawn OFF
                // falling (136532-136542): one final move with the whole
                // remaining delta, then the loop ends into the velocity tail
                // (the loop head does not test Physics).
                WalkMove(interp, actor, walk.MoveVel * walk.Remaining, walk.Hit);
                walk.Remaining = 0f;
                return WalkGateResult.Continue;
            }

            // Walkable floor: the slope-slip (136415-136454). The zone is the CURRENT one
            // (this+0xa0 at substep time, 136416 / 0x103e9c3a), not the one taken at the
            // call head: the substep move may have changed Region.
            UEObject slipZone = (actor.GetProp(PK.Region) as UEObject)?.GetProp(PK.Zone) as UEObject;
            float groundFriction = UEInterpreter.AsFloat(slipZone?.GetProp(PK.ZoneGroundFriction));
            (bool slip, bool slipMove, Vector3 nonNormal) = WalkSlopeSlip(normal, groundFriction,
                UEInterpreter.AsVector3(slipZone?.GetProp(PK.ZoneGravity)), walk.FullDt);
            if (slip)
            {
                if (slipMove)
                    WalkMove(interp, actor, nonNormal, walk.Hit);
                if (UEInterpreter.ToInt(actor.GetProp(PK.Physics)) == 3)
                    throw new NotSupportedException(
                        $"physWalking startSwimming reached (Engine.dll.c:136458) actor={actor.GetProp(PK.Name)} — port the shipped swim entry before using it");
            }
            return WalkGateResult.Continue;
        }

        // ── APawn::stepUp (Engine.dll.c:66559): the up→forward→down walk step
        // used when the flat forward move hit something. Mount first (shipped
        // first operation; hook — geometry-heavy), then up MaxStepHeight,
        // forward, and either slide along a wall (with the SHARED
        // ProcessHitWall on the first AND second wall + TwoWallAdjust) or
        // recurse onto a shallow floor, then step back down (sliding on a
        // steep slope). ──
        // `hit` is the caller's FCheckResult (param_11, Engine.dll.c:66564):
        // stepUp runs all its moves and Mount into the SAME record.
        // Test seam: a record built from the _lastMove* publication of the last full move.
        public void StepUp(UEInterpreter interp, UEObject pawn, Vector3 gravDir, Vector3 desiredDir,
            Vector3 delta)
            => StepUp(interp, pawn, gravDir, desiredDir, delta, MoveCheck.FromActorProps(pawn));

        public void StepUp(UEInterpreter interp, UEObject pawn, Vector3 gravDir, Vector3 desiredDir,
            Vector3 delta, MoveCheck hit)
        {
            if (TryMount(interp, pawn, -gravDir, hit)) return;

            float maxStep = UEInterpreter.AsFloat(pawn.GetProp(PK.MaxStepHeight));
            Vector3 up = -gravDir * maxStep;
            Vector3 down = gravDir * maxStep;
            if (TraceMovesNow)
                Log($"step_up begin actor={pawn.GetProp(PK.Name)} location={pawn.GetProp(PK.Location)}"
                    + $" grav={gravDir} desired={desiredDir} delta={delta} max={maxStep:R}");
            (float upFraction, Vector3 upNormal, _) = WalkMove(interp, pawn, up, hit);
            if (TraceMovesNow)
                Log($"step_up up actor={pawn.GetProp(PK.Name)} location={pawn.GetProp(PK.Location)}"
                    + $" fraction={upFraction} normal={upNormal}");
            (float time, Vector3 normal, UEObject hitActor) = WalkMove(interp, pawn, delta, hit);
            if (TraceMovesNow)
                Log($"step_up forward actor={pawn.GetProp(PK.Name)} location={pawn.GetProp(PK.Location)}"
                    + $" fraction={time:R} normal={normal} hit={hitActor?.GetProp(PK.Name)}");
            if (time < 1f)
            {
                // Player pushing a Decoration (66724-66745, disasm 0x103ec806-
                // 0x103ec8ff): player, Hit.Actor is a Decoration with bPushable (0x260&1)
                // and DesiredDir·HitNormal < −0.9 (double 0x1047a5d8) →
                // bJustTeleported, Velocity *= Mass/(Mass_deco+Mass),
                // processHitWall, exit on Falling, otherwise straight to the step down. Other
                // cases (not a player, not a Decoration, !bPushable, dot ≥ −0.9)
                // take the ordinary wall branch (0x103ec904).
                if (UEInterpreter.ClassIsA(pawn.Class, "PlayerPawn") && hitActor is not null
                    && UEInterpreter.ClassIsA(hitActor.Class, "Decoration")
                    && UEInterpreter.ToBool(hitActor.GetDeclared("Decoration", "bPushable"))
                    && ((double)desiredDir.z * normal.z + (double)desiredDir.y * normal.y
                        + (double)desiredDir.x * normal.x) < -0.9)
                {
                    pawn.Props["bJustTeleported"] = true;
                    double mass = UEInterpreter.AsFloat(pawn.GetProp(PK.Mass));
                    double ratio = mass / (UEInterpreter.AsFloat(hitActor.GetProp(PK.Mass)) + mass);
                    Vector3 v = UEInterpreter.AsVector3(pawn.GetProp(PK.Velocity));
                    pawn.Props["Velocity"] = new Vector3((float)(ratio * v.x), (float)(ratio * v.y),
                        (float)(ratio * v.z));
                    ProcessHitWall(interp, pawn, normal, hitActor);
                    if (UEInterpreter.ToInt(pawn.GetProp(PK.Physics)) == 2) return;
                }
                else
                {
                    // 0x103ec904-0x103ec94b: |N.z| against 0.2f; else |Delta|² =
                    // (y² + z²) + x² and ·Time in the register against 144f, `test ah,0x41;
                    // jne` — a wall both on equality and on NaN.
                    double deltaSq = ((double)delta.y * delta.y + (double)delta.z * delta.z)
                                     + (double)delta.x * delta.x;
                    bool wall = Math.Abs(normal.z) >= 0.2f || !(deltaSq * time > 144.0);
                    if (wall)
                    {
                        ProcessHitWall(interp, pawn, normal, hitActor);
                        if (StepUpSlide(delta, normal, time, out Vector3 slide))
                        {
                            (float sFraction, Vector3 sNormal, UEObject sHitActor) =
                                WalkMove(interp, pawn, slide, hit);
                            if (sFraction < 1f)
                            {
                                ProcessHitWall(interp, pawn, sNormal, sHitActor);
                                if (UEInterpreter.ToInt(pawn.GetProp(PK.Physics)) == 2) return; // Falling
                                Vector3 adjusted = slide;
                                TwoWallAdjust(desiredDir, ref adjusted, sNormal, normal, sFraction);
                                WalkMove(interp, pawn, adjusted, hit);
                            }
                        }
                    }
                    else
                    {
                        // Shallow floor ahead → recurse with the leftover delta.
                        StepUp(interp, pawn, gravDir, desiredDir, delta * (1f - time), hit);
                        if (UEInterpreter.ToInt(pawn.GetProp(PK.Physics)) == 2) return; // Falling
                    }
                }
            }
            // Step back down; slide if we landed on a steep slope.
            (float dTime, Vector3 dDownNormal, _) = WalkMove(interp, pawn, down, hit);
            if (TraceMovesNow)
                Log($"step_up down actor={pawn.GetProp(PK.Name)} location={pawn.GetProp(PK.Location)}"
                    + $" fraction={dTime:R} normal={dDownNormal}");
            // 0x103ecbff-0x103eccc4: the same slide computation, and the step down HAS
            // its own "not backwards" check (Down·slide < 0 or NaN -> exit without a move).
            if (dTime < 1f && dDownNormal.z < 0.5f
                && StepUpSlide(down, dDownNormal, dTime, out Vector3 downSlide))
                WalkMove(interp, pawn, downSlide, hit);
        }

        // stepUp slide along the hit normal — the same machine code for the wall
        // (0x103ec9f6-0x103ecab3) and the step down (0x103ecc27-0x103eccc4):
        //   dn = (D.y·N.y + D.z·N.z) + D.x·N.x                      — in the register;
        //   z' = float(D.z − float(dn·N.z)), y' = float(D.y − float(dn·N.y));
        //   x' = D.x − dn·N.x                                         — in the register;
        //   s = 1 − Time (register); S.z = float(z'·s), S.y = float(y'·s),
        //   x'·s stays in the register (`fst`), S.x is its float;
        //   move only if ((D.y·S.y + D.z·S.z) + (x'·s)·D.x) ≥ 0 — `fcomp 0.0;
        //   test ah,1; jne`, i.e. "less than zero" and NaN skip the move.
        // The x87 register has a 64-bit mantissa, modelled here by double (53): the same
        // convention as TwoWallAdjust; float·float products are exact in both.
        static bool StepUpSlide(Vector3 d, Vector3 n, float time, out Vector3 slide)
        {
            double dn = ((double)d.y * n.y + (double)d.z * n.z) + (double)d.x * n.x;
            float zp = d.z - (float)(dn * n.z);
            float yp = d.y - (float)(dn * n.y);
            double xp = d.x - dn * n.x;
            double s = 1.0 - time;
            float sz = (float)(zp * s), sy = (float)(yp * s);
            double xs = xp * s;
            slide = new Vector3((float)xs, sy, sz);
            double back = ((double)d.y * sy + (double)d.z * sz) + xs * d.x;
            return back >= 0.0;
        }

        // ── AActor::TwoWallAdjust (Engine.dll.c): fold the slide delta when a
        // second wall is hit — slide along the newer wall's tangent when the
        // walls face the same way, else follow the crease between them (never
        // backwards vs desired). ──
        // Body at 0x1031c3e0, transcribed from the machine code. Shipped runs the crease
        // through FVector::SafeNormal (0x1031c47b) before scaling by it; skipping that
        // would scale the whole adjusted delta by
        // |n x o|² = sin²θ, which is invisible while the two walls are near
        // perpendicular and wrong everywhere else.
        //
        // The float32/extended split below is register allocation, read at the
        // same address: dot products never leave the register file, the Y and Z
        // components round before being scaled, and X does not.
        public static void TwoWallAdjust(Vector3 desiredDir, ref Vector3 delta, Vector3 hitNormal,
            Vector3 oldNormal, float time)
        {
            double f = 1.0 - time;
            double facing = ((double)hitNormal.y * oldNormal.y
                             + (double)oldNormal.z * hitNormal.z)
                          + (double)hitNormal.x * oldNormal.x;
            if (facing > 0.0)
            {
                // Both walls face the same way: slide along the newer one.
                double d = ((double)hitNormal.y * delta.y + (double)delta.x * hitNormal.x)
                         + (double)delta.z * hitNormal.z;
                float az = (float)(delta.z - (float)(d * hitNormal.z));
                float ay = (float)(delta.y - (float)(d * hitNormal.y));
                double ax = delta.x - d * hitNormal.x;
                // RU EA (UEEdition.RuOrder; RU Engine 0x1031c558..0x1031c56a): x' and 1 − Time are stored as float before
                // the three products (US keeps both in the register).
                if (UEEdition.RuOrder)
                {
                    float axf = (float)ax, ff = (float)f;
                    delta = new Vector3(axf * ff, ay * ff, az * ff);
                }
                else delta = new Vector3((float)(ax * f), (float)(ay * f), (float)(az * f));
                // This branch has its own "never backwards" test (0x1031c5ca)
                // and it does NOT mirror the crease branch's: where that one
                // FLIPS the delta, this one ZEROES it, and it keeps the slide
                // only on a strictly positive dot. Without this test a pawn would keep
                // sliding along a wall in a direction where the
                // shipped engine stops dead.
                if (!(((double)desiredDir.z * delta.z + (double)desiredDir.y * delta.y)
                      + (double)desiredDir.x * delta.x > 0.0))
                    delta = Vector3.zero;
                return;
            }

            // Facing each other: follow the crease, and never backwards.
            var crease = new Vector3(
                (float)((double)hitNormal.y * oldNormal.z - (double)hitNormal.z * oldNormal.y),
                (float)((double)oldNormal.x * hitNormal.z - (double)hitNormal.x * oldNormal.z),
                (float)((double)hitNormal.x * oldNormal.y - (double)hitNormal.y * oldNormal.x));
            crease = crease.normalized;
            // RU EA (UEEdition.RuOrder; RU Engine 0x1031c465..0x1031c478): (c.z·D.z + c.y·D.y) + c.x·D.x.
            double along = UEEdition.RuOrder
                ? ((double)crease.z * delta.z + (double)crease.y * delta.y) + (double)crease.x * delta.x
                : ((double)crease.x * delta.x + (double)crease.z * delta.z) + (double)crease.y * delta.y;
            double scale = along * f;
            delta = new Vector3((float)(crease.x * scale), (float)(crease.y * scale),
                                (float)(crease.z * scale));
            double forward = ((double)desiredDir.z * delta.z + (double)desiredDir.y * delta.y)
                           + (double)desiredDir.x * delta.x;
            if (forward < 0.0)
                delta = new Vector3(delta.x * -1f, delta.y * -1f, delta.z * -1f);
        }
    }
}
