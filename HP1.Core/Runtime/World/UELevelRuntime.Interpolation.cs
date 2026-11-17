// PHYS_Interpolating (8) — AInterpolationManager::performPhysics and the AInterpolationPoint bodies it calls:
// GetInterpolatedPosition (cubic Bezier + rotation) and GetDesiredRotationAtPosition / AtPause (rotation smoothing over
// the neighbouring points). A KnowWonder class (absent from stock UE1); the source is each game's Engine.dll:
//
//   AInterpolationManager::performPhysics              [HP1 0x103f7ba0] [HP2 0x10403e60]  (HP1 Engine.dll.c:38731-39583)
//   AInterpolationPoint::GetInterpolatedPosition       [HP1 0x103f69f0] [HP2 0x10402f30]  (96958-97447)
//   AInterpolationPoint::GetDesiredRotationAtPosition  [HP1 0x103f65a0] [HP2 0x10402b50]  (83797-83875)
//   AInterpolationPoint::GetDesiredRotationAtPause     [HP1 0x103f6820] [HP2 0x10402da0]  (77903-77964)
//
// HP1 field offsets (HP2 reads the same fields by name): manager — Dest 0x25c, PhysAlpha 0x260, PhysRate 0x264,
// RemainingPause 0x268, StartPause 0x26c, PauseNum 0x270, TurnRateX 0x298, TurnRateZ 0x2a4, OldDesiredX 0x2b0,
// OldDesiredZ 0x2bc, bInstantMove 0x2c8&1 (HP2 adds Last 0x2b0); point — flags 0x260 (bEndOfPath 1, bInstantNextPath 2,
// bFaceMoveDirection 4, bConstantSpeed 0x20, bTurnChange 0x40), Pause[8] 0x264, ViewTargetTag[8] 0x284,
// bNewRotationSmoothing 0x2a4&1, ViewTarget[8] 0x2a8, Prev 0x3b0, Next 0x3b4, StartControlPoint 0x3b8, EndControlPoint
// 0x3c4, Smoothing 0x3dc, DesiredSpeed 0x3e0, PathDist 0x3e4. Events: UpdateCamera(float) on the manager,
// InterpolateEnd(manager, bForward) on Dest, FinishedInterpolation(None) on the manager; ULevel vtable +0x8c MoveActor,
// +0x98 DestroyActor.
//
// x87: game logic runs at PC64, RC nearest (Core appInit: appEnableFastMath(0)). Every chain is written from the
// disassembly: a value the body keeps in the register is an X87Ext, a stored one a float; a compare tests the status-word
// bits the body tests (unordered sets C0|C2|C3): `test ah,0x41; je` ordered greater (IpGt), `test ah,1; jne` less or
// unordered (IpLt), `test ah,0x40; jne` equal or unordered (IpEq).
//
// HP2's lines (profile branches; tools/hp2/physics_tick_judge.py Interpolating: HP1 = HP2 with them undone, InterpPort:
// each body = this port under its profile): the start of a segment is the manager's Last (InterpolationManager.Init sets
// it), not Dest.Prev, for the coordinates [HP2 0x104044d4..0x104045f5] and the old rotation smoothing's Smoothing
// [HP2 0x104049d9, 0x10404a2c..0x10404a57]; an Owner with bInterpolating_IgnoreRot moves with its own Rotation
// [HP2 0x10405136..0x104051b7]; a NULL Owner after each MoveActor and after the segment event returns [HP2 0x10405232,
// 0x10405327, 0x104056a2]; the velocities keep Z in the register and take X, Y times the float-rounded 1/dt
// [HP2 0x10405382..0x104053f4, 0x10405446..0x104054be], the overshoot's ratio is stored as a float [0x1040559d..0x104055c3]
// and its velocity uses the float 1/dt [0x104056e9..0x10405718]; GetInterpolatedPosition's blended X axis sums
// (x² + y²) + z² [HP2 0x10403961..0x10403990] (HP1 (y² + z²) + x²); GetDesiredRotationAtPosition (below). Not ported: the
// HP2 debug Logf when the Owner's CollisionRadius equals the double 49.4 (a float never does; NaN only) — it reads, logs
// and writes nothing [HP2 0x104041fc..0x104042ec].
//
// UNINITIALISED READ (both games): a pause iteration whose ViewTarget is not found leaves the result axes as they are —
// a previous iteration's of this call, else the caller's stack (HP1: AActor::Tick's ProcessState frame at the same depth —
// the manager pointer, a saved register — and older frames) — and OrthoRotation / the old rotation smoothing read them.
// The port carries the axes across iterations and stops loudly where the original reads the caller's stack (DECISIONS.md
// STOP "interpolation pause axes"; no shipped map of either game pauses on a path).

using System;
using System.Collections.Generic;
using Vector3 = HP1.UEScript.UEVector;
using XE = HP1.Core.X87Ext;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        const float InterpSmallNumber = 1e-8f;          // [HP1 0x10475a78] [HP2 0x1047cc30]
        static readonly XE IpOneQ = XE.From(1.0);     // qword 1.0 [HP1 0x10474578] [HP2 0x1047b6c8]

        // The PC64 register chains (x87 in game logic: PC 64, RC nearest): every fld/fmul/fadd/fdiv result stays in the
        // register at 64 bits until its fstp dword.
        static XE X(float v) => XE.From(v);
        static XE R(XE v) => XE.Round64(v);
        static bool IpGt(XE a, XE b) => (XE.Fcom(a, b) & (XE.C0 | XE.C3)) == 0;
        static bool IpLt(XE a, XE b) => (XE.Fcom(a, b) & XE.C0) != 0;
        static bool IpEq(XE a, XE b) => (XE.Fcom(a, b) & XE.C3) != 0;
        static readonly XE IpZero = XE.From(0f), IpOne = XE.From(1f);
        static Vector3 IpLoc(UEObject a) => UEInterpreter.AsVector3(a.GetProp(PK.Location));
        static float IpF(UEObject a, int key) => UEInterpreter.AsFloat(a.GetProp(key));
        static bool IpB(UEObject a, string key) => UEInterpreter.ToBool(a.GetProp(key));
        static UEObject IpNotNull(UEObject o, string what) => o ?? throw new NotSupportedException(
            $"{UEGameProfile.Current.Game}: AInterpolationManager::performPhysics dereferences a NULL {what}: the original faults");
        // PhysAlpha's clamp: `fcomp 0; test ah,1` (less or unordered -> 0), then `fcomp 1.0; test ah,1` (less -> the value).
        static float IpClamp01(XE v) => IpLt(v, IpZero) ? 0f : IpLt(v, IpOne) ? v.ToFloat() : 1f;

        // ── AInterpolationManager::performPhysics ──────────────────────────
        // Original: Engine.dll ?performPhysics@AInterpolationManager@@UAEXM@Z [HP1 0x103f7ba0] [HP2 0x10403e60]
        public void PhysInterpolating(UEInterpreter interp, UEObject mgr, float dt)
        {
            bool hp2 = UEGameProfile.Current.Game == UEEdition.Game.HP2;
            UEObject owner = mgr.GetProp(PK.Owner) as UEObject;
            if (owner is null || owner.Destroyed)
            {
                // [HP1 0x103f9460]: XLevel->DestroyActor(this, 0) and return.
                Journal?.Invoke($"interp_manager_orphan actor={mgr.GetProp(PK.Name)} owner={(owner is null ? "None" : "deleted")}");
                DestroyActor(interp, mgr);
                return;
            }

            float speed = 0f;                                 // [ebp-0x24] — lives across iterations
            Vector3 startLoc = IpLoc(owner);
            Vector3 startVel = UEInterpreter.AsVector3(owner.GetProp(PK.Velocity));
            float remaining = dt;                             // [ebp-0x30]
            // The result coords (HP1 [ebp-0x74], HP2 [ebp-0x6c]) live across iterations; a pause whose ViewTarget is not
            // found keeps their axes, which before any write in this call are the caller's stack.
            BrushCoords result = default;
            bool axesWritten = false;
            int guard = 0;

            while (true)
            {
                owner = IpNotNull(mgr.GetProp(PK.Owner) as UEObject, "Owner");
                float physRate = IpF(mgr, PK.PhysRate);
                if (IpEq(X(physRate), IpZero) || !UEInterpreter.ToBool(owner.GetProp(PK.bInterpolating)) || !IpGt(X(remaining), IpZero))
                {
                    InterpFinal(interp, mgr, owner, dt, speed, startLoc, startVel, hp2);
                    return;
                }
                if (++guard > 64)
                    throw new InvalidOperationException($"PhysInterpolating: >64 iterations per tick on {mgr.GetProp(PK.Name)} (remaining={remaining})");

                float speedScale = 1f;                        // [ebp-0x80]
                Vector3 prevLoc = IpLoc(owner);               // [ebp-0xcc]
                UEObject dest = mgr.GetProp(PK.Dest) as UEObject;
                float physAlpha = IpF(mgr, PK.PhysAlpha);
                if (dest is not null)
                {
                    float ipSpeed = IpF(owner, PK.IPSpeed);
                    if (IpEq(X(ipSpeed), IpZero))
                    {
                        UEObject next = dest.GetProp(PK.Next) as UEObject;
                        if (next is not null)
                        {
                            float destSpeed = IpF(dest, PK.DesiredSpeed);
                            // DesiredSpeed, or |Velocity| as appSqrt leaves it in the register [HP1 0x103f7d1d..0x103f7d51].
                            XE cur;
                            if (IpGt(X(destSpeed), IpZero)) cur = X(destSpeed);
                            else
                            {
                                Vector3 v = UEInterpreter.AsVector3(owner.GetProp(PK.Velocity));
                                cur = UEVector.Size64Ext(v.x, v.y, v.z);
                            }
                            float nextSpeed = IpF(next, PK.DesiredSpeed);
                            // (1 − PhysAlpha)·cur + Next.DesiredSpeed·PhysAlpha in the register [HP1 0x103f7d76..0x103f7d94].
                            if (IpGt(X(nextSpeed), IpZero)) speed = R(R(R(IpOne - X(physAlpha)) * cur) + R(X(nextSpeed) * X(physAlpha))).ToFloat();
                            else if (IpGt(X(destSpeed), IpZero)) speed = destSpeed;
                            else speed = 0f;
                        }
                        // Next == None: speed keeps its value (goto 0x103f7cbc).
                    }
                    else speed = ipSpeed;

                    if (IpGt(X(speed), IpZero))
                    {
                        float pathDist = IpF(dest, PK.PathDist);
                        if (IpGt(X(pathDist), IpZero)) speedScale = speed / pathDist;
                    }
                    else
                    {
                        Vector3 ecp = UEInterpreter.AsVector3(dest.GetProp(PK.EndControlPoint));
                        Vector3 scp = UEInterpreter.AsVector3(dest.GetProp(PK.StartControlPoint));
                        // |EndControlPoint| stored as float, |StartControlPoint| kept in the register [HP1 0x103f7dc6..0x103f7e5f].
                        float eLen = UEVector.Size64(ecp.x, ecp.y, ecp.z);
                        XE sLen = UEVector.Size64Ext(scp.x, scp.y, scp.z);
                        speedScale = R(R(R(XE.Div64(X(eLen), sLen) - IpOne) * X(physAlpha)) + IpOne).ToFloat();
                    }
                }

                float oldAlpha = physAlpha;                   // [ebp-0xa4]
                // fld speedScale; fmul PhysRate; fmul remaining; fst step; fadd PhysAlpha; fst newAlpha [HP1 0x103f7e64..0x103f7e85];
                // the first clamp compare reads the register.
                XE stepX = R(R(X(speedScale) * X(physRate)) * X(remaining));
                float step = stepX.ToFloat();                 // [ebp-0x34]
                XE newAlphaX = R(stepX + X(physAlpha));
                float newAlpha = newAlphaX.ToFloat();         // [ebp-0x14]
                mgr.Props["PhysAlpha"] = IpLt(newAlphaX, IpZero) ? 0f : IpLt(X(newAlpha), IpOne) ? newAlpha : 1f;
                bool endReached = false;                      // [ebp-0xc0]
                float stepDt = remaining;                     // [ebp-0x2c]
                if (IpGt(X(newAlpha), IpOne) && !IpGt(X(IpF(mgr, PK.RemainingPause)), IpZero))
                    stepDt = R(R(X(step) - X(newAlpha)) + IpOne).ToFloat();   // [HP1 0x103f7eef]

                Vector3 newLoc = IpLoc(owner);                // [ebp-0xa0]
                if (dest is null) endReached = true;
                else
                {
                    if (IpGt(X(IpF(mgr, PK.RemainingPause)), IpZero))
                    {
                        // Pause [HP1 0x103f7f50..0x103f811d]: PhysAlpha = 0, RemainingPause − remaining (its sign read in the
                        // register), UpdateCamera(pause fraction), the rotation toward ViewTarget[PauseNum], no displacement.
                        XE rp = R(X(IpF(mgr, PK.RemainingPause)) - X(remaining));
                        speed = 0f;
                        mgr.Props["PhysAlpha"] = 0f;
                        newAlpha = 0f;
                        mgr.Props["RemainingPause"] = rp.ToFloat();
                        if (IpLt(rp, IpZero)) { mgr.Props["RemainingPause"] = 0f; remaining = 0f; endReached = true; }
                        float startPause = IpF(mgr, PK.StartPause);
                        float pct = XE.Div64(R(X(startPause) - X(IpF(mgr, PK.RemainingPause))), X(startPause)).ToFloat();
                        UELifecycle.CallEvent(interp, mgr, "UpdateCamera", pct);
                        dest = IpNotNull(mgr.GetProp(PK.Dest) as UEObject, "Dest");
                        owner = IpNotNull(mgr.GetProp(PK.Owner) as UEObject, "Owner");
                        UEObject vt = ResolveViewTarget(dest, UEInterpreter.ToInt(mgr.GetProp(PK.PauseNum)));
                        if (vt is not null)
                        {
                            Vector3 vl = IpLoc(vt), ol = IpLoc(owner);
                            UERotator dr = UEValue.RotatorFromVector(new Vector3(vl.x - ol.x, vl.y - ol.y, vl.z - ol.z));
                            result = BrushCoords.Identity;
                            result.DivRotator(dr.Pitch, dr.Yaw, dr.Roll);
                            axesWritten = true;
                        }
                        result.Origin = IpLoc(owner);
                    }
                    else
                    {
                        UELifecycle.CallEvent(interp, mgr, "UpdateCamera", oldAlpha);
                        dest = IpNotNull(mgr.GetProp(PK.Dest) as UEObject, "Dest");
                        owner = IpNotNull(mgr.GetProp(PK.Owner) as UEObject, "Owner");
                        // The segment's start: HP2 the manager's Last [HP2 0x104044d4], HP1 Dest.Prev [HP1 0x103f814e]; none —
                        // Dest's rotation at the Owner's Location, no control point [HP1 0x103f81e4..0x103f8274].
                        UEObject start = (hp2 ? mgr.GetProp("Last") : dest.GetProp(PK.Prev)) as UEObject;
                        BrushCoords prevCoords = BrushCoords.Identity;
                        Vector3 startCP;
                        if (start is null)
                        {
                            UERotator r = UEInterpreter.AsRotator(dest.GetProp(PK.Rotation));
                            prevCoords.DivRotator(r.Pitch, r.Yaw, r.Roll);
                            prevCoords.Origin = IpLoc(owner);
                            startCP = Vector3.zero;
                        }
                        else
                        {
                            UERotator r = UEInterpreter.AsRotator(start.GetProp(PK.Rotation));
                            prevCoords.DivRotator(r.Pitch, r.Yaw, r.Roll);
                            prevCoords.Origin = IpLoc(start);
                            startCP = UEInterpreter.AsVector3(start.GetProp(PK.StartControlPoint));
                        }
                        result = GetInterpolatedPosition(dest, prevCoords, startCP, IpF(mgr, PK.PhysAlpha),
                            UEInterpreter.ToInt(mgr.GetProp(PK.PauseNum)));
                        axesWritten = true;
                        newLoc = result.Origin;
                        if (UEInterpreter.ToBool(dest.GetProp(PK.bConstantSpeed)) && IpGt(X(speed), IpZero)
                            && IpLt(X(IpF(mgr, PK.PhysAlpha)), IpOne) && !UEInterpreter.ToBool(dest.GetProp(PK.bInstantNextPath)))
                        {
                            // [HP1 0x103f8341..0x103f83c8]: the differences, the size, size/stepDt and |… − speed| stay in the
                            // register; speed·0.05 (qword) against it with fcompp.
                            XE dx = R(X(newLoc.x) - X(prevLoc.x)), dy = R(X(newLoc.y) - X(prevLoc.y)), dz = R(X(newLoc.z) - X(prevLoc.z));
                            XE dist = XE.Sqrt64(XE.From(R(R(R(dx * dx) + R(dy * dy)) + R(dz * dz)).ToDouble()));
                            XE rate = XE.Div64(dist, X(stepDt));
                            XE err = R(rate - X(speed));
                            if (IpLt(err, IpZero)) err = -err;
                            if (IpLt(R(X(speed) * XE.From(0.05)), err))
                            {
                                newAlpha = R(XE.Div64(R(R(R(X(stepDt) * X(physRate)) * X(speedScale)) * X(speed)), rate)
                                    + X(oldAlpha)).ToFloat();
                                mgr.Props["PhysAlpha"] = IpClamp01(X(newAlpha));
                                result = GetInterpolatedPosition(IpNotNull(mgr.GetProp(PK.Dest) as UEObject, "Dest"), prevCoords, startCP,
                                    IpF(mgr, PK.PhysAlpha), UEInterpreter.ToInt(mgr.GetProp(PK.PauseNum)));
                                newLoc = result.Origin;
                                if (IpGt(X(newAlpha), IpOne) && !IpGt(X(IpF(mgr, PK.RemainingPause)), IpZero))
                                    stepDt = R(R(R(R(X(speedScale) * X(physRate)) * X(remaining)) - X(newAlpha)) + IpOne).ToFloat();   // [HP1 0x103f84a1]
                            }
                        }
                        dest = IpNotNull(mgr.GetProp(PK.Dest) as UEObject, "Dest");
                        if (UEInterpreter.ToBool(dest.GetProp(PK.bFaceMoveDirection)))
                        {
                            Vector3 ol = IpLoc(owner);
                            UERotator dr = UEValue.RotatorFromVector(new Vector3(newLoc.x - ol.x, newLoc.y - ol.y, newLoc.z - ol.z));
                            result = BrushCoords.Identity;
                            result.DivRotator(dr.Pitch, dr.Yaw, dr.Roll);
                            result.Origin = newLoc;
                        }
                    }

                    dest = IpNotNull(mgr.GetProp(PK.Dest) as UEObject, "Dest");
                    if (!UEInterpreter.ToBool(dest.GetProp(PK.bNewRotationSmoothing)))
                    {
                        if (!axesWritten) InterpAxesStop(mgr, dest);
                        OldStyleRotationSmoothing(mgr, owner, hp2 ? mgr.GetProp("Last") as UEObject : dest.GetProp(PK.Prev) as UEObject,
                            ref result, stepDt);
                    }

                    Vector3 cur = IpLoc(owner);
                    Vector3 delta = new Vector3(newLoc.x - cur.x, newLoc.y - cur.y, newLoc.z - cur.z);
                    UERotator rot;
                    if (hp2 && IpB(owner, "bInterpolating_IgnoreRot")) rot = UEInterpreter.AsRotator(owner.GetProp(PK.Rotation));
                    else
                    {
                        if (!axesWritten) InterpAxesStop(mgr, dest);
                        rot = OrthoRotation(in result);
                    }
                    MoveActor(interp, owner, delta, rot, bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false);
                    if (hp2 && mgr.GetProp(PK.Owner) is not UEObject) return;
                }

                // End of segment: events and remaining time [HP1 0x103f8ee9..0x103f9102] [HP2 0x1040523d..0x10405441].
                bool forward, fire = false;
                if (IpGt(X(IpF(mgr, PK.PhysRate)), IpZero))
                {
                    forward = true;
                    if (IpGt(X(newAlpha), IpOne))
                    {
                        mgr.Props["PhysAlpha"] = 0f;
                        remaining = R(XE.Div64(R(X(newAlpha) - IpOne), R(X(newAlpha) - X(oldAlpha))) * X(remaining)).ToFloat();
                        fire = true;
                    }
                }
                else
                {
                    forward = false;
                    if (IpLt(X(newAlpha), IpZero))
                    {
                        mgr.Props["PhysAlpha"] = 1f;
                        remaining = R(XE.Div64(-X(newAlpha), R(X(oldAlpha) - X(newAlpha))) * X(remaining)).ToFloat();
                        fire = true;
                    }
                }
                if (!fire && !endReached) { remaining = 0f; continue; }

                UEObject destNow = mgr.GetProp(PK.Dest) as UEObject;
                if (destNow is null)
                    UELifecycle.CallEvent(interp, mgr, "FinishedInterpolation", null);
                else
                    UELifecycle.CallEvent(interp, destNow, "InterpolateEnd", mgr, forward);
                if (hp2 && mgr.GetProp(PK.Owner) is not UEObject) return;
                owner = IpNotNull(mgr.GetProp(PK.Owner) as UEObject, "Owner");
                if (UEInterpreter.ToBool(mgr.GetProp(PK.bInstantMove)))
                {
                    mgr.Props["bInstantMove"] = false;
                    startLoc = IpLoc(owner);
                    prevLoc = startLoc;
                }
                Vector3 vv = InterpVelocity(IpLoc(owner), prevLoc, dt, hp2);
                owner.Props["Velocity"] = vv;
                // |V| in the register against speed: `fcomp; test ah,1; jne` — remaining stays only while |V| < speed (or NaN).
                if (!IpLt(UEVector.Size64Ext(vv.x, vv.y, vv.z), X(speed))) remaining = 0f;
            }
        }

        // The loop's exit [HP1 0x103f910e..0x103f944a] [HP2 0x10405446..0x104057a8]: Velocity = (Location − start)/dt; past
        // 1.05·speed the Owner is moved back onto the speed and the velocity taken again; Acceleration = (Velocity − startVel)/dt.
        void InterpFinal(UEInterpreter interp, UEObject mgr, UEObject owner, float dt, float speed, Vector3 startLoc,
            Vector3 startVel, bool hp2)
        {
            Vector3 vel = InterpVelocity(IpLoc(owner), startLoc, dt, hp2);
            owner.Props["Velocity"] = vel;
            float vsize = UEVector.Size64(vel.x, vel.y, vel.z);
            if (IpGt(X(speed), IpZero) && IpLt(R(X(speed) * X(1.05f)), X(vsize)))
            {
                Journal?.Invoke($"interp_overshoot actor={owner.GetProp(PK.Name)} speed={speed} vel={vsize}");
                Vector3 loc = IpLoc(owner);
                Vector3 m = new Vector3(loc.x - startLoc.x, loc.y - startLoc.y, loc.z - startLoc.z);
                Vector3 scaled;
                XE k = XE.Div64(X(speed), X(vsize));
                if (hp2)
                {
                    // HP2 stores the ratio as a float and keeps Z's difference in the register [HP2 0x1040557d..0x104055c3].
                    float kf = k.ToFloat();
                    scaled = new Vector3(m.x * kf, m.y * kf, R(R(X(loc.z) - X(startLoc.z)) * X(kf)).ToFloat());
                }
                else scaled = new Vector3(R(X(m.x) * k).ToFloat(), R(X(m.y) * k).ToFloat(), R(X(m.z) * k).ToFloat());
                Vector3 target = new Vector3(scaled.x + startLoc.x, scaled.y + startLoc.y, scaled.z + startLoc.z);
                MoveActor(interp, owner, new Vector3(target.x - loc.x, target.y - loc.y, target.z - loc.z),
                    UEInterpreter.AsRotator(owner.GetProp(PK.Rotation)), bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false);
                if (hp2 && mgr.GetProp(PK.Owner) is not UEObject) return;
                owner = IpNotNull(mgr.GetProp(PK.Owner) as UEObject, "Owner");
                loc = IpLoc(owner);
                Vector3 m2 = new Vector3(loc.x - startLoc.x, loc.y - startLoc.y, loc.z - startLoc.z);
                if (hp2)
                {
                    // [HP2 0x104056e9..0x10405718]: every component times the float 1/dt stored at the exit's start.
                    float invF = XE.Div64(IpOne, X(dt)).ToFloat();
                    owner.Props["Velocity"] = new Vector3(m2.x * invF, m2.y * invF, m2.z * invF);
                }
                else owner.Props["Velocity"] = DivByFloat(m2, dt);   // FVector::operator- and operator/(dt) [HP1 0x103f93d2..0x103f93e5]
            }
            Vector3 v2 = UEInterpreter.AsVector3(owner.GetProp(PK.Velocity));
            owner.Props["Acceleration"] = DivByFloat(new Vector3(v2.x - startVel.x, v2.y - startVel.y, v2.z - startVel.z), dt);
        }

        // (Location − from)/dt. HP1: `fld 1.0; fdiv dt` kept in the register, each float difference times it
        // [HP1 0x103f9060..0x103f909d, 0x103f9148..0x103f9183] (DivByFloat). HP2: Z's difference stays in the register
        // and takes the register 1/dt, X and Y take the float-rounded 1/dt [HP2 0x10405382..0x104053f4, 0x10405446..0x104054be].
        internal static Vector3 InterpVelocity(Vector3 loc, Vector3 from, float dt, bool hp2)
        {
            if (!hp2) return DivByFloat(new Vector3(loc.x - from.x, loc.y - from.y, loc.z - from.z), dt);
            XE inv = XE.Div64(IpOne, X(dt));
            float invF = inv.ToFloat();
            return new Vector3((loc.x - from.x) * invF, (loc.y - from.y) * invF, R(R(X(loc.z) - X(from.z)) * inv).ToFloat());
        }

        // The result axes read before any write in this call: the original reads the caller's stack there.
        static void InterpAxesStop(UEObject mgr, UEObject dest) => throw new NotSupportedException(
            $"{UEGameProfile.Current.Game}: AInterpolationManager::performPhysics pauses at {dest.GetProp(PK.Name)} "
            + $"(PauseNum={UEInterpreter.ToInt(mgr.GetProp(PK.PauseNum))}) without a ViewTarget before any rotation of this tick: "
            + "the original reads its rotation axes from the uninitialised stack (DECISIONS.md STOP \"interpolation pause axes\")");

        // !bNewRotationSmoothing [HP1 0x103f8595..0x103f8da5] [HP2 0x104048ec..0x10405092]: the X/Z axes chase the desired
        // ones through TurnRateX/Z, OldDesiredX/Z and the start point's Smoothing (HP2: the manager's Last, HP1: Dest.Prev).
        // The Z differences and the 1/Smoothing products of the X component stay in the register as noted.
        void OldStyleRotationSmoothing(UEObject mgr, UEObject owner, UEObject smoothFrom, ref BrushCoords result, float stepDt)
        {
            UERotator orot = UEInterpreter.AsRotator(owner.GetProp(PK.Rotation));
            BrushCoords cur = BrushCoords.Identity;
            cur.DivRotator(orot.Pitch, orot.Yaw, orot.Roll);
            float invDt = 1f / stepDt;                       // [ebp-0x34], stored
            Vector3 oldX = UEInterpreter.AsVector3(mgr.GetProp(PK.OldDesiredX));
            Vector3 oldZ = UEInterpreter.AsVector3(mgr.GetProp(PK.OldDesiredZ));
            // (result − OldDesired)·invDt: X and Y differences stored as floats, Z's kept in the register. A stepDt of 0 makes
            // invDt infinite: every operation here is the FPU's (its invalid results are the x87 indefinite).
            static float Sub(float a, float b) => (X(a) - X(b)).ToFloat();
            static Vector3 Rate(Vector3 a, Vector3 b, float k) => new Vector3(R(X(k) * X(Sub(a.x, b.x))).ToFloat(),
                R(X(k) * X(Sub(a.y, b.y))).ToFloat(), R(R(X(a.z) - X(b.z)) * X(k)).ToFloat());
            Vector3 dX = Rate(result.X, oldX, invDt);
            Vector3 dZ = Rate(result.Z, oldZ, invDt);
            mgr.Props["OldDesiredX"] = result.X;
            mgr.Props["OldDesiredZ"] = result.Z;
            // 1/Smoothing in the register; Smoothing == 0 (or NaN) -> 0; no start point -> 1.
            XE smooth = IpOne;
            if (smoothFrom is not null)
            {
                float ps = IpF(smoothFrom, PK.Smoothing);
                smooth = IpEq(X(ps), IpZero) ? IpZero : XE.Div64(IpOne, X(ps));
            }
            XE inv = XE.Div64(IpOne, smooth);
            // (result − cur)·inv: every difference stored, the products stored; Z's X component stays in the register for its dot.
            Vector3 eX = new Vector3(R(X(Sub(result.X.x, cur.X.x)) * inv).ToFloat(), R(X(Sub(result.X.y, cur.X.y)) * inv).ToFloat(),
                R(X(Sub(result.X.z, cur.X.z)) * inv).ToFloat());
            XE eZxReg = R(inv * X(Sub(result.Z.x, cur.Z.x)));
            Vector3 eZ = new Vector3(eZxReg.ToFloat(), R(X(Sub(result.Z.y, cur.Z.y)) * inv).ToFloat(), R(X(Sub(result.Z.z, cur.Z.z)) * inv).ToFloat());
            // (e·d) ≤ 0 (or NaN) -> e, else d: (x + y) + z in the register.
            Vector3 fX = !IpGt(R(R(X(eX.x) * X(dX.x) + X(eX.y) * X(dX.y)) + X(eX.z) * X(dX.z)), IpZero) ? eX : dX;
            Vector3 fZ = !IpGt(R(R(R(eZxReg * X(dZ.x)) + X(eZ.y) * X(dZ.y)) + X(eZ.z) * X(dZ.z)), IpZero) ? eZ : dZ;
            if (!IpEq(smooth, IpZero))
            {
                XE k = R(smooth * X(stepDt)), k1 = R(IpOne - k);
                static float C(float f, float tr, XE k, XE k1) => (X(R(X(f) * k).ToFloat()) + X(R(k1 * X(tr)).ToFloat())).ToFloat();
                static Vector3 Blend(Vector3 f, Vector3 tr, XE k, XE k1) => new Vector3(C(f.x, tr.x, k, k1), C(f.y, tr.y, k, k1), C(f.z, tr.z, k, k1));
                Vector3 trX = Blend(fX, UEInterpreter.AsVector3(mgr.GetProp(PK.TurnRateX)), k, k1);
                Vector3 trZ = Blend(fZ, UEInterpreter.AsVector3(mgr.GetProp(PK.TurnRateZ)), k, k1);
                mgr.Props["TurnRateX"] = trX;
                mgr.Props["TurnRateZ"] = trZ;
                // stepDt·TurnRate + cur: the X and Y products stored, Z's in the register; the sums in the register.
                (XE, XE, XE) Advance(Vector3 tr, Vector3 c) => (R(X(R(X(stepDt) * X(tr.x)).ToFloat()) + X(c.x)),
                    R(X(c.y) + X(R(X(stepDt) * X(tr.y)).ToFloat())), R(R(X(stepDt) * X(tr.z)) + X(c.z)));
                Vector3 x = IpNormalizeReg(Advance(trX, cur.X));
                Vector3 z = IpNormalizeReg(Advance(trZ, cur.Z));
                // Y = Z ^ X, Z = X ^ Y inline, the components normalized from the register.
                Vector3 y = IpNormalizeReg((R(X(x.z) * X(z.y) - X(x.y) * X(z.z)), R(X(z.z) * X(x.x) - X(x.z) * X(z.x)),
                    R(X(x.y) * X(z.x) - X(z.y) * X(x.x))));
                z = IpNormalizeReg((R(X(y.z) * X(x.y) - X(y.y) * X(x.z)), R(X(x.z) * X(y.x) - X(y.z) * X(x.x)),
                    R(X(y.y) * X(x.x) - X(x.y) * X(y.x))));
                result.X = x; result.Y = y; result.Z = z;
            }
            mgr.Props["TurnRateX"] = Rate(result.X, cur.X, invDt);
            mgr.Props["TurnRateZ"] = Rate(result.Z, cur.Z, invDt);
        }

        // The performPhysics inline normalize of register components: the floats are stored, (x² + y²) + z² of the register
        // values, < 1e-8 (or NaN) keeps them, else each float times 1.0 / (double)appSqrt((double)len²) [HP1 0x103f8a47..0x103f8a9d].
        static Vector3 IpNormalizeReg((XE x, XE y, XE z) v)
        {
            var f = new Vector3(v.x.ToFloat(), v.y.ToFloat(), v.z.ToFloat());
            XE len2 = R(R(R(v.x * v.x) + R(v.y * v.y)) + R(v.z * v.z));
            if (IpLt(len2, X(InterpSmallNumber))) return f;
            XE inv = XE.Div64(IpOneQ, XE.From(XE.Sqrt64(XE.From(len2.ToDouble())).ToDouble()));
            return new Vector3(R(inv * X(f.x)).ToFloat(), R(inv * X(f.y)).ToFloat(), R(inv * X(f.z)).ToFloat());
        }

        // GetInterpolatedPosition's inline normalize of the blended X axis: HP1 (y² + z²) + x² [HP1 0x103f75fe..0x103f762a],
        // HP2 (x² + y²) + z² [HP2 0x10403961..0x10403990]; < 1e-8 (or NaN) keeps it, else each component times
        // 1.0 / (double)appSqrt((double)len²).
        internal static Vector3 IpBlendNormalize(Vector3 v, bool hp2)
        {
            XE len2 = hp2 ? R(R(X(v.x) * X(v.x) + X(v.y) * X(v.y)) + X(v.z) * X(v.z))
                : R(R(X(v.y) * X(v.y) + X(v.z) * X(v.z)) + X(v.x) * X(v.x));
            if (IpLt(len2, X(InterpSmallNumber))) return v;
            XE inv = XE.Div64(IpOneQ, XE.From(XE.Sqrt64(XE.From(len2.ToDouble())).ToDouble()));
            return new Vector3(R(X(v.x) * inv).ToFloat(), R(X(v.y) * inv).ToFloat(), R(X(v.z) * inv).ToFloat());
        }

        // Core FVector::Normalize [HP1 Core 0x101198d0] (HP2's identical): (x² + y²) + z² in the register, < 1e-8 (or NaN)
        // keeps v, else each component times 1.0 / appSqrt((double)len²), the root kept in the register.
        static Vector3 IpCoreNormalize(Vector3 v)
        {
            XE len2 = R(R(X(v.x) * X(v.x) + X(v.y) * X(v.y)) + X(v.z) * X(v.z));
            if (IpLt(len2, X(InterpSmallNumber))) return v;
            XE inv = XE.Div64(IpOneQ, XE.Sqrt64(XE.From(len2.ToDouble())));
            return new Vector3(R(inv * X(v.x)).ToFloat(), R(inv * X(v.y)).ToFloat(), R(inv * X(v.z)).ToFloat());
        }

        // Core FVector::operator^ [HP1 Core 0x10119110] [HP2 Core 0x1011a030]: each component's difference of the two
        // products in the register, then stored.
        static Vector3 IpCoreCross(Vector3 a, Vector3 b) => new Vector3(
            R(X(a.y) * X(b.z) - X(b.y) * X(a.z)).ToFloat(), R(X(a.z) * X(b.x) - X(a.x) * X(b.z)).ToFloat(),
            R(X(b.y) * X(a.x) - X(a.y) * X(b.x)).ToFloat());

        // ── AInterpolationPoint::GetInterpolatedPosition ────────────────────
        // prev — the start's coordinates (axes UnitCoords/Start.Rotation, Origin = Start.Location; without a start — Dest's
        // rotation at the Owner), startCP — the start's StartControlPoint. HP2's line: the blended X axis's (x² + y²) + z².
        // Original: Engine.dll ?GetInterpolatedPosition@AInterpolationPoint@@QAE?AVFCoords@@V2@VFVector@@MH@Z [HP1 0x103f69f0] [HP2 0x10402f30]
        internal BrushCoords GetInterpolatedPosition(UEObject pt, in BrushCoords prev, Vector3 startCP, float alpha, int pauseNum)
        {
            Vector3 ptLoc = IpLoc(pt);
            if (UEInterpreter.ToBool(pt.GetProp(PK.bInstantNextPath)))
            {
                UERotator r0 = UEInterpreter.AsRotator(pt.GetProp(PK.Rotation));
                BrushCoords snap = BrushCoords.Identity;
                snap.DivRotator(r0.Pitch, r0.Yaw, r0.Roll);
                snap.Origin = ptLoc;
                return snap;
            }
            // Bernstein weights in the register [HP1 0x103f6a9d..0x103f6ae5]: fld 1; fsub alpha; fst s — the unstored 1−alpha
            // multiplies s; b0..b2 stored, b3 = alpha³ stays in st0 for the three products with Location.
            XE sx = R(IpOne - X(alpha));
            float s = sx.ToFloat();
            XE ss = R(sx * X(s));
            float b0 = R(ss * X(s)).ToFloat();
            float b1 = R(R(ss * X(alpha)) * X(3f)).ToFloat();
            float b2 = R(R(R(X(s) * X(alpha)) * X(alpha)) * X(3f)).ToFloat();
            XE t2x = R(X(alpha) * X(alpha));
            float t2 = t2x.ToFloat();
            XE b3 = R(t2x * X(alpha));
            Vector3 ecpRel = UEInterpreter.AsVector3(pt.GetProp(PK.EndControlPoint));
            Vector3 ecp = new Vector3(ecpRel.x + ptLoc.x, ecpRel.y + ptLoc.y, ecpRel.z + ptLoc.z);
            Vector3 scp = new Vector3(startCP.x + prev.Origin.x, startCP.y + prev.Origin.y, startCP.z + prev.Origin.z);
            Vector3 pos = Bezier(ptLoc, ecp, scp, prev.Origin, b3, b2, b1, b0);

            BrushCoords r = BrushCoords.Identity;
            if (!UEInterpreter.ToBool(pt.GetProp(PK.bFaceMoveDirection)))
            {
                if (UEInterpreter.ToBool(pt.GetProp(PK.bNewRotationSmoothing)))
                {
                    // [HP1 0x103f705c..0x103f73cc]: the four desired rotations, each weight one register chain stored as a float,
                    // each rotator component (float)(int·weight) rounded by fistp.
                    float t3 = t2 * alpha;
                    UERotator rm2 = GetDesiredRotationAtPosition(pt, -2, pauseNum, pos);
                    UERotator rm1 = GetDesiredRotationAtPosition(pt, -1, pauseNum, pos);
                    UERotator r0 = GetDesiredRotationAtPosition(pt, 0, pauseNum, pos);
                    UERotator r1 = GetDesiredRotationAtPosition(pt, 1, pauseNum, pos);
                    float k1 = R(R(X(t3) - X(t2)) * X(0.5f)).ToFloat();
                    UERotator a = ScaleRound(new UERotator(r1.Pitch - rm1.Pitch, r1.Yaw - rm1.Yaw, r1.Roll - rm1.Roll), k1);
                    float k2 = R(R(R(X(t3) - R(X(t2) + X(t2))) + X(alpha)) * X(0.5f)).ToFloat();
                    UERotator b = ScaleRound(new UERotator(r0.Pitch - rm2.Pitch, r0.Yaw - rm2.Yaw, r0.Roll - rm2.Roll), k2);
                    float t2x3 = t2 * 3f;
                    float t3x2 = t3 + t3;
                    UERotator c = ScaleRound(r0, t2x3 - t3x2);
                    UERotator d = ScaleRound(rm1, R(R(X(t3x2) - X(t2x3)) + IpOne).ToFloat());
                    r.DivRotator(a.Pitch + b.Pitch + c.Pitch + d.Pitch, a.Yaw + b.Yaw + c.Yaw + d.Yaw, a.Roll + b.Roll + c.Roll + d.Roll);
                }
                else if (ViewTargetTagAt(pt, pauseNum) is null)
                {
                    // [HP1 0x103f74ed..0x103f779b]: X = the point's and the start's X axes blended by alpha (sqrt under
                    // bTurnChange) and s, normalized inline; Y = Normalize(Zblend ^ X), Z = Normalize(X ^ Y) through Core.
                    float a = alpha;
                    if (UEInterpreter.ToBool(pt.GetProp(PK.bTurnChange))) a = XE.Sqrt64(XE.From((double)alpha)).ToFloat();
                    UERotator pr = UEInterpreter.AsRotator(pt.GetProp(PK.Rotation));
                    BrushCoords tc = BrushCoords.Identity;
                    tc.DivRotator(pr.Pitch, pr.Yaw, pr.Roll);
                    Vector3 x = IpBlendNormalize(new Vector3(tc.X.x * a + prev.X.x * s, tc.X.y * a + prev.X.y * s, tc.X.z * a + prev.X.z * s),
                        UEGameProfile.Current.Game == UEEdition.Game.HP2);
                    Vector3 zb = new Vector3(prev.Z.x * s + tc.Z.x * a, prev.Z.y * s + tc.Z.y * a, prev.Z.z * s + tc.Z.z * a);
                    Vector3 y = IpCoreNormalize(IpCoreCross(zb, x));
                    Vector3 z = IpCoreNormalize(IpCoreCross(x, y));
                    r.X = x; r.Y = y; r.Z = z;
                }
                else
                {
                    UEObject vt = ResolveViewTarget(pt, pauseNum);
                    if (vt is not null)
                    {
                        Vector3 vl = IpLoc(vt);
                        UERotator dr = UEValue.RotatorFromVector(new Vector3(vl.x - pos.x, vl.y - pos.y, vl.z - pos.z));
                        r.DivRotator(dr.Pitch, dr.Yaw, dr.Roll);
                    }
                    else r = prev;
                }
            }
            else
            {
                // t1 = alpha − 0.001f and 1 − t1 stay in the register; c0..c2 stored, c3 = t1³ kept [HP1 0x103f6d45..0x103f6dd9].
                XE t1 = R(X(alpha) - X(0.001f));
                if (IpGt(t1, IpZero))
                {
                    XE s1 = R(IpOne - t1);
                    XE s1s = R(s1 * s1);
                    float c0 = R(s1s * s1).ToFloat();
                    float c1 = R(R(s1s * t1) * X(3f)).ToFloat();
                    float c2 = R(R(R(s1 * t1) * t1) * X(3f)).ToFloat();
                    XE c3 = R(R(t1 * t1) * t1);
                    Vector3 pos2 = Bezier(ptLoc, ecp, scp, prev.Origin, c3, c2, c1, c0);
                    UERotator dr = UEValue.RotatorFromVector(new Vector3(pos.x - pos2.x, pos.y - pos2.y, pos.z - pos2.z));
                    r.DivRotator(dr.Pitch, dr.Yaw, dr.Roll);
                }
                else
                {
                    UEObject src = pt.GetProp(PK.Prev) as UEObject ?? pt;
                    UERotator sr = UEInterpreter.AsRotator(src.GetProp(PK.Rotation));
                    r.DivRotator(sr.Pitch, sr.Yaw, sr.Roll);
                }
            }
            r.Origin = pos;
            return r;
        }

        // p3·Location + p2·(EndCP+Location) + p1·(StartCP+PrevOrigin) + p0·PrevOrigin,
        // addition in shipped order: ((p1+p0)+p2)+p3 per component.
        // b3 is the register value (fld st0; fmul Location.c; fstp dword).
        static Vector3 Bezier(Vector3 loc, Vector3 ecp, Vector3 scp, Vector3 prevOrigin, XE b3, float b2, float b1, float b0)
        {
            float x = R(b3 * X(loc.x)).ToFloat(), y = R(b3 * X(loc.y)).ToFloat(), z = R(b3 * X(loc.z)).ToFloat();
            float ex = ecp.x * b2, ey = ecp.y * b2, ez = ecp.z * b2;
            float sx = scp.x * b1, sy = scp.y * b1, sz = scp.z * b1;
            float px = prevOrigin.x * b0, py = prevOrigin.y * b0, pz = prevOrigin.z * b0;
            float ax = sx + px, ay = sy + py, az = sz + pz;
            ax = ex + ax; ay = ey + ay; az = ez + az;
            return new Vector3(x + ax, y + ay, z + az);
        }

        // FRotator(fistp((float)(int·k))) — `fild; fmul; fstp dword; fld; fistp` at round-to-nearest-even.
        static UERotator ScaleRound(UERotator r, float k) => new UERotator(
            MeshTriangleOrder.Fistp(R(XE.From(r.Pitch) * X(k)).ToFloat()),
            MeshTriangleOrder.Fistp(R(XE.From(r.Yaw) * X(k)).ToFloat()),
            MeshTriangleOrder.Fistp(R(XE.From(r.Roll) * X(k)).ToFloat()));

        // ── AInterpolationPoint::GetDesiredRotationAtPosition (83797) ───────
        // n<0 — backward along Prev through pauses, n>0 — forward along Next; 0 — the point itself. The original's tail
        // recursion is the loop. HP2's three lines: Prev.bEndOfPath for Prev == NULL with Prev dereferenced unchecked
        // [HP2 0x10402bed..0x10402bfa], this.bEndOfPath for Next == NULL with Next dereferenced unchecked
        // [HP2 0x10402c8d..0x10402ca7], and n - 1 into Next [HP2 0x10402cb1]. HP1 keeps n along Next: on a ring of points
        // (InterpolationPoint.BeginPlay links the last to Position 0) without pauses it recurses until its stack overflows.
        // Pause[] past its 8 entries is read as 0 (the original reads the fields after it; no point pauses 8 times).
        // Original: Engine.dll ?GetDesiredRotationAtPosition@AInterpolationPoint@@QAE?AVFRotator@@HHVFVector@@@Z [HP1 0x103f65a0] [HP2 0x10402b50]
        internal UERotator GetDesiredRotationAtPosition(UEObject pt, int n, int pn, Vector3 pos)
        {
            bool hp2 = UEGameProfile.Current.Game == UEEdition.Game.HP2;
            HashSet<(UEObject, int, int)> seen = null;
            while (true)
            {
                UEObject target;
                if (n < 0)
                {
                    if (pn < 1)
                    {
                        if (UEInterpreter.ToBool(pt.GetProp(PK.bInstantNextPath))) return GetDesiredRotationAtPause(pt, pn, pos);
                        UEObject prev = pt.GetProp(PK.Prev) as UEObject;
                        if (hp2 ? UEInterpreter.ToBool(NotNull(prev, "Prev").GetProp("bEndOfPath")) : prev is null)
                            return GetDesiredRotationAtPause(pt, pn, pos);
                        pn = 0;
                        while (IpGt(X(PauseAt(prev, pn)), IpZero)) pn++;
                        n++;
                        target = prev;
                    }
                    else { pn--; n++; target = pt; }
                }
                else
                {
                    if (n == 0) return GetDesiredRotationAtPause(pt, pn, pos);
                    while (IpGt(X(PauseAt(pt, pn + 1)), IpZero) && n > 0) { pn++; n--; }
                    UEObject next = pt.GetProp(PK.Next) as UEObject;
                    if (hp2 ? UEInterpreter.ToBool(pt.GetProp("bEndOfPath")) || n == 0
                              || UEInterpreter.ToBool(NotNull(next, "Next").GetProp(PK.bInstantNextPath))
                            : next is null || n == 0 || UEInterpreter.ToBool(next.GetProp(PK.bInstantNextPath)))
                        return GetDesiredRotationAtPause(pt, pn, pos);
                    if (hp2) n--;
                    pn = 0;
                    target = next;
                }
                if (!(seen ??= new()).Add((target, n, pn)))
                    throw new NotSupportedException($"{UEGameProfile.Current.Game}: AInterpolationPoint::GetDesiredRotationAtPosition "
                        + $"recurses along {target.GetProp(PK.Name)} without end (n={n}): the original overflows its stack");
                pt = target;
            }

            static UEObject NotNull(UEObject o, string what) => o ?? throw new NotSupportedException(
                $"HP2: AInterpolationPoint::GetDesiredRotationAtPosition dereferences a NULL {what} [HP2 0x10402bf3 / 0x10402ca0]: "
                + "the original faults");
        }

        // ── AInterpolationPoint::GetDesiredRotationAtPause (77903) ──────────
        // HP2's body is HP1's (a side-by-side read: no semantic hunk).
        // Original: Engine.dll ?GetDesiredRotationAtPause@AInterpolationPoint@@QAE?AVFRotator@@HVFVector@@@Z [HP1 0x103f6820] [HP2 0x10402da0]
        internal UERotator GetDesiredRotationAtPause(UEObject pt, int pn, Vector3 pos)
        {
            if (ViewTargetTagAt(pt, pn) is not null)
            {
                UEObject vt = ResolveViewTarget(pt, pn);
                if (vt is not null)
                {
                    Vector3 vl = IpLoc(vt);
                    return UEValue.RotatorFromVector(new Vector3(vl.x - pos.x, vl.y - pos.y, vl.z - pos.z));
                }
            }
            return UEInterpreter.AsRotator(pt.GetProp(PK.Rotation));
        }

        static float PauseAt(UEObject pt, int i)
        {
            if (pt.GetProp(PK.Pause) is IList<object> list && i >= 0 && i < list.Count) return UEInterpreter.AsFloat(list[i]);
            return 0f;
        }

        // ViewTargetTag[pn] — the name, or null for None (FName index 0).
        static string ViewTargetTagAt(UEObject pt, int pn)
        {
            if (pt.GetProp(PK.ViewTargetTag) is not IList<object> list || pn < 0 || pn >= list.Count) return null;
            string s = list[pn]?.ToString();
            return string.IsNullOrEmpty(s) || string.Equals(s, "None", StringComparison.OrdinalIgnoreCase) ? null : s;
        }

        // ViewTarget[pn] when ViewTargetTag[pn] is set: an empty slot or one whose Tag differs is refreshed with the first
        // actor of the level with that Tag; when none has it the slot keeps what it held [HP1 0x103f7fdc..0x103f8066]
        // (Engine.dll.c:77913-77935).
        UEObject ResolveViewTarget(UEObject pt, int pn)
        {
            string tag = ViewTargetTagAt(pt, pn);
            if (tag is null) return null;
            IList<object> targets = pt.GetProp(PK.ViewTarget) as IList<object>;
            UEObject vt = targets is not null && pn < targets.Count ? targets[pn] as UEObject : null;
            if (vt is null || !string.Equals(vt.GetProp(PK.Tag)?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                foreach (UEObject a in Actors)
                {
                    if (a is null || !string.Equals(a.GetProp(PK.Tag)?.ToString(), tag, StringComparison.OrdinalIgnoreCase)) continue;
                    vt = a;
                    if (targets is not null && pn < targets.Count) targets[pn] = a;
                    break;
                }
            }
            return vt;
        }
    }
}
