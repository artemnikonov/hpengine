// PHYS_Interpolating (8) — AInterpolationManager::performPhysics and both
// AInterpolationPoint bodies that read the path: GetInterpolatedPosition (cubic
// Bezier + rotation) and GetDesiredRotationAtPosition/AtPause (rotation smoothing
// over neighbouring points). A KnowWonder class (absent from stock UE1);
// the only source is Engine.dll:
//
//   AInterpolationManager::performPhysics   Engine.dll.c:38729-39578, body 0x103f7a00..0x103f945d
//   AInterpolationPoint::GetInterpolatedPosition  96958-97447, body 0x103f69f0 (thunk 0x1030346d)
//   AInterpolationPoint::GetDesiredRotationAtPosition  83797-83875 (thunk 0x10302f27)
//   AInterpolationPoint::GetDesiredRotationAtPause     77903-77964
//
// Field offsets checked against the shipped class layout:
// manager — Dest 0x25c, PhysAlpha 0x260, PhysRate 0x264,
// RemainingPause 0x268, StartPause 0x26c, PauseNum 0x270, TurnRateX 0x298,
// TurnRateZ 0x2a4, OldDesiredX 0x2b0, OldDesiredZ 0x2bc, bInstantMove 0x2c8&1;
// point — flags 0x260 (bEndOfPath 1, bInstantNextPath 2, bFaceMoveDirection 4,
// bRollIntoTurn 8, bSmoothPath 0x10, bConstantSpeed 0x20, bTurnChange 0x40),
// Pause[8] 0x264, ViewTargetTag[8] 0x284, bNewRotationSmoothing 0x2a4&1,
// ViewTarget[8] 0x2a8, Prev 0x3b0, Next 0x3b4, StartControlPoint 0x3b8,
// EndControlPoint 0x3c4, Smoothing 0x3dc, DesiredSpeed 0x3e0, PathDist 0x3e4;
// actor — Location 0xfc, Rotation 0x108, Velocity 0x12c, IPSpeed 0x138,
// Acceleration 0x13c, Tag 0x84, bDeleteMe 0x28&0x200, bInterpolating 0x1f8&8.
//
// Event names — disasm (x86dis engine): FName globals 0x105e9d40 UpdateCamera
// (two calls, float), 0x105e9e68 InterpolateEnd (on Dest, (this, bForward)),
// 0x105e9d48 FinishedInterpolation (on the manager, argument 0 = None).
// ULevel calls: vtable+0x8c MoveActor(Owner, delta, rot, hit, 0,0,0,0),
// vtable+0x98 DestroyActor(this, 0) when Owner is missing/deleted.
//
// Constants from the image: 1.05f (0x104770dc, float), 0.05 (0x1047a610, double),
// 0.001f (0x10473814), SMALL_NUMBER 1e-8f (0x10475a78), 1.0 (0x10474578, double —
// divisor in Normalize). Rotation rounding — `fistp` with the default FPCW
// (round to nearest even), integer casts — `_ftol`.
//
// CAMPAIGN MEASUREMENT (14 maps with InterpolationPoint): only
// StartControlPoint/EndControlPoint/DesiredSpeed/bEndOfPath are overridden. No
// pauses, no ViewTargetTag, no bFaceMoveDirection=false,
// bNewRotationSmoothing=false, bInstantNextPath, bTurnChange. So the live
// route is Bezier + rotation along the motion direction + bConstantSpeed
// correction; the other branches are ported from the same source but the
// campaign does not execute them (shape from the source, no proof here).
//
// STOP on original data: a pause without ViewTargetTag with bNewRotationSmoothing —
// shipped reads the result axes from UNINITIALISED stack (afStack_78) and
// passes them to OrthoRotation. Garbage cannot be reproduced; unreachable in the
// campaign (no pauses) — the port stops loudly.

using System;
using System.Collections.Generic;
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        const float InterpSmallNumber = 1e-8f;          // 0x10475a78

        // Vector sizes: shipped loads z, y, x, sums ((x²+y²)+z²) in the register, stores the qword for appSqrt and keeps
        // its result in st0 (disasm 0x103f7d1d-0x103f7d51, 0x103f90c7-0x103f90ee, 0x103f91af-0x103f91dc):
        // UEVector.Size64Ext / Size64 (stored as float).
        // The PC64 register chains of the loop below (x87 in game logic: appEnableFastMath(0), PC 64, RC nearest):
        // every fld/fmul/fadd/fdiv result stays in the register at 64 bits until its fstp dword.
        static HP1.Core.X87Ext X(float v) => HP1.Core.X87Ext.From(v);
        static HP1.Core.X87Ext R(HP1.Core.X87Ext v) => HP1.Core.X87Ext.Round64(v);

        // ── AInterpolationManager::performPhysics ──────────────────────────
        public void PhysInterpolating(UEInterpreter interp, UEObject mgr, float dt)
        {
            UEObject owner = mgr.GetProp(PK.Owner) as UEObject;
            if (owner is null || owner.Destroyed)
            {
                // 0x103f9460: XLevel->DestroyActor(this, 0) and return.
                Journal?.Invoke($"interp_manager_orphan actor={mgr.GetProp(PK.Name)} owner={(owner is null ? "None" : "deleted")}");
                DestroyActor(interp, mgr);
                return;
            }

            float speed = 0f;                                 // fStack_28 — lives across iterations
            Vector3 startLoc = UEInterpreter.AsVector3(owner.GetProp(PK.Location));
            Vector3 startVel = UEInterpreter.AsVector3(owner.GetProp(PK.Velocity));
            float remaining = dt;                             // fStack_34
            int guard = 0;

            while (true)
            {
                float physRate = UEInterpreter.AsFloat(mgr.GetProp(PK.PhysRate));
                if (physRate == 0f || !UEInterpreter.ToBool(owner.GetProp(PK.bInterpolating)) || remaining <= 0f)
                {
                    // Final: Velocity = (Location − startLoc)/dt; on overshoot
                    // (|V| > speed·1.05f) roll back to startLoc + moved·(speed/|V|)
                    // via MoveActor; Acceleration = (Velocity − startVel)/dt.
                    Vector3 loc = UEInterpreter.AsVector3(owner.GetProp(PK.Location));
                    // FVector(L − start)·(1/dt): `fld 1.0; fdiv dt` — 1/dt stays in an
                    // x87 register for all three components (disasm 0x103f9148-0x103f9183)
                    // = DivByFloat. Rounding 1/dt to float and multiplying in float
                    // would diverge in the low bits of the velocity.
                    Vector3 moved = new Vector3(loc.x - startLoc.x, loc.y - startLoc.y, loc.z - startLoc.z);
                    Vector3 vel = DivByFloat(moved, dt);
                    owner.Props["Velocity"] = vel;
                    float vsize = UEVector.Size64(vel.x, vel.y, vel.z);   // fstp dword [ebp-0x20] (0x103f91dc)
                    // fld speed; fmul 1.05f; fcomp [vsize as float] (0x103f91f6-0x103f9207):
                    // the product is not rounded to float.
                    if (speed > 0f && (double)speed * 1.05f < vsize)
                    {
                        Journal?.Invoke($"interp_overshoot actor={owner.GetProp(PK.Name)} speed={speed} vel={vsize}");
                        // fld speed; fdiv vsize — the ratio stays in st(1), each
                        // product is rounded to float (fstp, 0x103f9298-
                        // 0x103f92cd), then + startLoc (0x103f92e7-0x103f9313).
                        double k = (double)speed / vsize;
                        Vector3 scaled = new Vector3((float)(moved.x * k), (float)(moved.y * k), (float)(moved.z * k));
                        Vector3 target = new Vector3(scaled.x + startLoc.x, scaled.y + startLoc.y, scaled.z + startLoc.z);
                        Vector3 delta = new Vector3(target.x - loc.x, target.y - loc.y, target.z - loc.z);
                        MoveActor(interp, owner, delta, UEInterpreter.AsRotator(owner.GetProp(PK.Rotation)),
                            bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false);
                        loc = UEInterpreter.AsVector3(owner.GetProp(PK.Location));
                        // FVector::operator- and operator/(dt) (0x103f93d2-0x103f93e5);
                        // operator/ Core.dll 0x10119310: RV = 1/dt in x87, X·RV → float.
                        owner.Props["Velocity"] = DivByFloat(new Vector3(loc.x - startLoc.x, loc.y - startLoc.y, loc.z - startLoc.z), dt);
                    }
                    Vector3 v2 = UEInterpreter.AsVector3(owner.GetProp(PK.Velocity));
                    owner.Props["Acceleration"] = DivByFloat(new Vector3(v2.x - startVel.x, v2.y - startVel.y, v2.z - startVel.z), dt);
                    return;
                }
                if (++guard > 64)
                    throw new InvalidOperationException($"PhysInterpolating: >64 iterations per tick on {mgr.GetProp(PK.Name)} (remaining={remaining})");

                float speedScale = 1f;                        // fStack_84
                Vector3 prevLoc = UEInterpreter.AsVector3(owner.GetProp(PK.Location));   // fStack_d0
                UEObject dest = mgr.GetProp(PK.Dest) as UEObject;
                float physAlpha = UEInterpreter.AsFloat(mgr.GetProp(PK.PhysAlpha));
                if (dest is not null)
                {
                    float ipSpeed = UEInterpreter.AsFloat(owner.GetProp(PK.IPSpeed));
                    if (ipSpeed == 0f)
                    {
                        UEObject next = dest.GetProp(PK.Next) as UEObject;
                        if (next is not null)
                        {
                            float destSpeed = UEInterpreter.AsFloat(dest.GetProp(PK.DesiredSpeed));
                            // cur: DesiredSpeed (fld dword) or |Velocity| as appSqrt left it in the register
                            // (0x103f7d1d-0x103f7d51, fst qword keeps st0).
                            HP1.Core.X87Ext cur;
                            if (destSpeed <= 0f)
                            {
                                Vector3 v = UEInterpreter.AsVector3(owner.GetProp(PK.Velocity));
                                cur = UEVector.Size64Ext(v.x, v.y, v.z);
                            }
                            else cur = X(destSpeed);
                            float nextSpeed = UEInterpreter.AsFloat(next.GetProp(PK.DesiredSpeed));
                            // cur·(1 − PhysAlpha) + Next.DesiredSpeed·PhysAlpha in the register (0x103f7d76-0x103f7d94).
                            if (nextSpeed > 0f) speed = R(R(cur * R(X(1f) - X(physAlpha))) + R(X(nextSpeed) * X(physAlpha))).ToFloat();
                            else if (destSpeed > 0f) speed = destSpeed;
                            else speed = 0f;
                        }
                        // next == None: speed keeps its previous value (goto LAB_103f7cbc).
                    }
                    else speed = ipSpeed;

                    if (speed > 0f)
                    {
                        float pathDist = UEInterpreter.AsFloat(dest.GetProp(PK.PathDist));
                        if (pathDist > 0f) speedScale = speed / pathDist;
                    }
                    else
                    {
                        Vector3 ecp = UEInterpreter.AsVector3(dest.GetProp(PK.EndControlPoint));
                        Vector3 scp = UEInterpreter.AsVector3(dest.GetProp(PK.StartControlPoint));
                        // |EndControlPoint| stored as float, |StartControlPoint| kept in the register; the
                        // quotient chain stays there (0x103f7dc6-0x103f7e5f).
                        float eLen = UEVector.Size64(ecp.x, ecp.y, ecp.z);
                        HP1.Core.X87Ext sLen = UEVector.Size64Ext(scp.x, scp.y, scp.z);
                        speedScale = R(R(R(HP1.Core.X87Ext.Div64(X(eLen), sLen) - X(1f)) * X(physAlpha)) + X(1f)).ToFloat();
                    }
                }

                float oldAlpha = physAlpha;                   // fStack_a8
                // fld speedScale; fmul PhysRate; fmul remaining; fst step; fadd PhysAlpha; fst newAlpha
                // (0x103f7e64-0x103f7e85): newAlpha adds the UNSTORED product.
                HP1.Core.X87Ext stepX = R(R(X(speedScale) * X(physRate)) * X(remaining));
                float step = stepX.ToFloat();                 // fStack_38
                float newAlpha = R(stepX + X(physAlpha)).ToFloat();   // fStack_18
                mgr.Props["PhysAlpha"] = newAlpha < 0f ? 0f : (newAlpha >= 1f ? 1f : newAlpha);
                bool endReached = false;                      // uStack_c4
                float stepDt = remaining;                     // fStack_30
                float remainingPause = UEInterpreter.AsFloat(mgr.GetProp(PK.RemainingPause));
                if (newAlpha > 1f && remainingPause <= 0f) stepDt = R(R(X(step) - X(newAlpha)) + X(1f)).ToFloat();   // 0x103f7eef

                Vector3 newLoc = UEInterpreter.AsVector3(owner.GetProp(PK.Location));   // fStack_a4
                BrushCoords result = BrushCoords.Identity;    // afStack_78
                if (dest is null) endReached = true;
                else
                {
                    if (remainingPause <= 0f)
                    {
                        UELifecycle.CallEvent(interp, mgr, "UpdateCamera", oldAlpha);
                        UEObject prev = dest.GetProp(PK.Prev) as UEObject;
                        BrushCoords prevCoords = BrushCoords.Identity;
                        Vector3 startCP;
                        if (prev is null)
                        {
                            // Prev == None (disasm 0x103f81e4-0x103f8206): axes —
                            // UnitCoords / Dest.Rotation (eax = [this+0x25c] = Dest,
                            // +0x108), origin — Owner.Location (0x103f820c). Not the
                            // owner's rotation.
                            UERotator r = UEInterpreter.AsRotator(dest.GetProp(PK.Rotation));
                            prevCoords.DivRotator(r.Pitch, r.Yaw, r.Roll);
                            prevCoords.Origin = UEInterpreter.AsVector3(owner.GetProp(PK.Location));
                            startCP = Vector3.zero;
                        }
                        else
                        {
                            UERotator r = UEInterpreter.AsRotator(prev.GetProp(PK.Rotation));
                            prevCoords.DivRotator(r.Pitch, r.Yaw, r.Roll);
                            prevCoords.Origin = UEInterpreter.AsVector3(prev.GetProp(PK.Location));
                            startCP = UEInterpreter.AsVector3(prev.GetProp(PK.StartControlPoint));
                        }
                        int pauseNum = UEInterpreter.ToInt(mgr.GetProp(PK.PauseNum));
                        result = GetInterpolatedPosition(dest, prevCoords, startCP,
                            UEInterpreter.AsFloat(mgr.GetProp(PK.PhysAlpha)), pauseNum);
                        newLoc = result.Origin;
                        if (UEInterpreter.ToBool(dest.GetProp(PK.bConstantSpeed)) && speed > 0f
                            && UEInterpreter.AsFloat(mgr.GetProp(PK.PhysAlpha)) < 1f
                            && !UEInterpreter.ToBool(dest.GetProp(PK.bInstantNextPath)))
                        {
                            // 0x103f8341-0x103f83c8: the differences, the size, size/stepDt and |… − speed|
                            // stay in the register; speed·0.05 (qword 0x1047a610) against it with fcompp.
                            HP1.Core.X87Ext dx = R(X(newLoc.x) - X(prevLoc.x)), dy = R(X(newLoc.y) - X(prevLoc.y)),
                                dz = R(X(newLoc.z) - X(prevLoc.z));
                            HP1.Core.X87Ext dist = HP1.Core.X87Ext.Sqrt64(HP1.Core.X87Ext.From(
                                R(R(R(dx * dx) + R(dy * dy)) + R(dz * dz)).ToDouble()));
                            HP1.Core.X87Ext rate = HP1.Core.X87Ext.Div64(dist, X(stepDt));
                            HP1.Core.X87Ext err = R(rate - X(speed));
                            if ((HP1.Core.X87Ext.Fcom(err, X(0f)) & HP1.Core.X87Ext.C0) != 0) err = -err;
                            if ((HP1.Core.X87Ext.Fcom(R(X(speed) * HP1.Core.X87Ext.From(0.05)), err) & HP1.Core.X87Ext.C0) != 0)
                            {
                                newAlpha = R(HP1.Core.X87Ext.Div64(R(R(R(X(stepDt) * X(physRate)) * X(speedScale)) * X(speed)), rate)
                                    + X(oldAlpha)).ToFloat();
                                mgr.Props["PhysAlpha"] = newAlpha < 0f ? 0f : (newAlpha >= 1f ? 1f : newAlpha);
                                result = GetInterpolatedPosition(dest, prevCoords, startCP,
                                    UEInterpreter.AsFloat(mgr.GetProp(PK.PhysAlpha)), pauseNum);
                                newLoc = result.Origin;
                                if (newAlpha > 1f && remainingPause <= 0f)
                                    stepDt = R(R(R(R(X(speedScale) * X(physRate)) * X(remaining)) - X(newAlpha)) + X(1f)).ToFloat();   // 0x103f84a1
                            }
                        }
                        if (UEInterpreter.ToBool(dest.GetProp(PK.bFaceMoveDirection)))
                        {
                            Vector3 ol = UEInterpreter.AsVector3(owner.GetProp(PK.Location));
                            Vector3 dir = new Vector3(newLoc.x - ol.x, newLoc.y - ol.y, newLoc.z - ol.z);
                            UERotator dr = UEValue.RotatorFromVector(dir);
                            result = BrushCoords.Identity;
                            result.DivRotator(dr.Pitch, dr.Yaw, dr.Roll);
                            result.Origin = newLoc;
                        }
                    }
                    else
                    {
                        // Pause: PhysAlpha = 0, RemainingPause decreases, UpdateCamera(pause fraction),
                        // rotation toward ViewTarget[PauseNum], no displacement.
                        float rp = remainingPause - remaining;
                        speed = 0f;
                        mgr.Props["PhysAlpha"] = 0f;
                        newAlpha = 0f;
                        mgr.Props["RemainingPause"] = rp;
                        if (rp < 0f) { mgr.Props["RemainingPause"] = 0f; remaining = 0f; }
                        endReached = rp < 0f;
                        float startPause = UEInterpreter.AsFloat(mgr.GetProp(PK.StartPause));
                        float pct = (startPause - UEInterpreter.AsFloat(mgr.GetProp(PK.RemainingPause))) / startPause;
                        UELifecycle.CallEvent(interp, mgr, "UpdateCamera", pct);
                        int pauseNum = UEInterpreter.ToInt(mgr.GetProp(PK.PauseNum));
                        UEObject vt = ResolveViewTarget(dest, pauseNum);
                        if (vt is null)
                            throw new NotSupportedException(
                                $"PhysInterpolating: pause without ViewTarget on {dest.GetProp(PK.Name)} (PauseNum={pauseNum}) — "
                                + "shipped takes the rotation axes from an uninitialized stack (Engine.dll.c:39074-39100); "
                                + "unreachable in the campaign, the port stops");
                        Vector3 ol = UEInterpreter.AsVector3(owner.GetProp(PK.Location));
                        Vector3 vl = UEInterpreter.AsVector3(vt.GetProp(PK.Location));
                        UERotator dr = UEValue.RotatorFromVector(new Vector3(vl.x - ol.x, vl.y - ol.y, vl.z - ol.z));
                        result = BrushCoords.Identity;
                        result.DivRotator(dr.Pitch, dr.Yaw, dr.Roll);
                        newLoc = ol;
                        result.Origin = ol;
                    }

                    if (!UEInterpreter.ToBool(dest.GetProp(PK.bNewRotationSmoothing)))
                        OldStyleRotationSmoothing(mgr, owner, dest, ref result, stepDt);

                    Vector3 cur = UEInterpreter.AsVector3(owner.GetProp(PK.Location));
                    Vector3 delta = new Vector3(newLoc.x - cur.x, newLoc.y - cur.y, newLoc.z - cur.z);
                    MoveActor(interp, owner, delta, OrthoRotation(in result),
                        bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false);
                }

                // End of segment: events and remaining time (0x103f8f23-0x103f9102).
                bool forward, fire;
                if (physRate <= 0f)
                {
                    forward = false;
                    if (newAlpha < 0f)
                    {
                        mgr.Props["PhysAlpha"] = 1f;
                        remaining = R(HP1.Core.X87Ext.Div64(-X(newAlpha), R(X(oldAlpha) - X(newAlpha))) * X(remaining)).ToFloat();   // 0x103f8f49
                        fire = true;
                    }
                    else fire = endReached;
                }
                else
                {
                    forward = true;
                    if (newAlpha <= 1f) fire = endReached;
                    else
                    {
                        mgr.Props["PhysAlpha"] = 0f;
                        remaining = R(HP1.Core.X87Ext.Div64(R(X(newAlpha) - X(1f)), R(X(newAlpha) - X(oldAlpha))) * X(remaining)).ToFloat();   // 0x103f8f11
                        fire = true;
                    }
                }
                if (!fire) { remaining = 0f; continue; }

                UEObject destNow = mgr.GetProp(PK.Dest) as UEObject;
                if (destNow is null)
                    UELifecycle.CallEvent(interp, mgr, "FinishedInterpolation", null);
                else
                    UELifecycle.CallEvent(interp, destNow, "InterpolateEnd", mgr, forward);
                if (UEInterpreter.ToBool(mgr.GetProp(PK.bInstantMove)))
                {
                    mgr.Props["bInstantMove"] = false;
                    startLoc = UEInterpreter.AsVector3(owner.GetProp(PK.Location));
                    prevLoc = startLoc;
                }
                // Same shape as in the final (disasm 0x103f9060-0x103f909d): 1/dt in a register.
                Vector3 l2 = UEInterpreter.AsVector3(owner.GetProp(PK.Location));
                Vector3 vv = DivByFloat(new Vector3(l2.x - prevLoc.x, l2.y - prevLoc.y, l2.z - prevLoc.z), dt);
                owner.Props["Velocity"] = vv;
                // |V| in the register against speed: fcomp; test ah,1 — remaining stays only while |V| < speed (0x103f90f1).
                if ((HP1.Core.X87Ext.Fcom(UEVector.Size64Ext(vv.x, vv.y, vv.z), X(speed)) & HP1.Core.X87Ext.C0) == 0) remaining = 0f;
            }
        }

        // Branch !bNewRotationSmoothing (Engine.dll.c:39101-39277): smoothing of the
        // X/Z axes via TurnRateX/Z, OldDesiredX/Z and Prev.Smoothing.
        // Not executed by the campaign (see header).
        void OldStyleRotationSmoothing(UEObject mgr, UEObject owner, UEObject dest, ref BrushCoords result, float stepDt)
        {
            UERotator orot = UEInterpreter.AsRotator(owner.GetProp(PK.Rotation));
            BrushCoords cur = BrushCoords.Identity;
            cur.DivRotator(orot.Pitch, orot.Yaw, orot.Roll);
            float invDt = 1f / stepDt;
            Vector3 oldX = UEInterpreter.AsVector3(mgr.GetProp(PK.OldDesiredX));
            Vector3 oldZ = UEInterpreter.AsVector3(mgr.GetProp(PK.OldDesiredZ));
            Vector3 dX = new Vector3((result.X.x - oldX.x) * invDt, (result.X.y - oldX.y) * invDt, (result.X.z - oldX.z) * invDt);
            Vector3 dZ = new Vector3((result.Z.x - oldZ.x) * invDt, (result.Z.y - oldZ.y) * invDt, (result.Z.z - oldZ.z) * invDt);
            mgr.Props["OldDesiredX"] = result.X;
            mgr.Props["OldDesiredZ"] = result.Z;
            float smooth = 1f;
            if (dest.GetProp(PK.Prev) is UEObject prev)
            {
                float ps = UEInterpreter.AsFloat(prev.GetProp(PK.Smoothing));
                smooth = ps == 0f ? 0f : 1f / ps;
            }
            float inv = 1f / smooth;
            Vector3 eX = new Vector3((result.X.x - cur.X.x) * inv, (result.X.y - cur.X.y) * inv, (result.X.z - cur.X.z) * inv);
            Vector3 eZ = new Vector3((result.Z.x - cur.Z.x) * inv, (result.Z.y - cur.Z.y) * inv, (result.Z.z - cur.Z.z) * inv);
            Vector3 fX = (eX.z * dX.z + eX.y * dX.y + eX.x * dX.x) <= 0f ? eX : dX;
            Vector3 fZ = (eZ.z * dZ.z + eZ.y * dZ.y + eZ.x * dZ.x) <= 0f ? eZ : dZ;
            if (smooth != 0f)
            {
                float k = smooth * stepDt, k1 = 1f - k;
                Vector3 trX = UEInterpreter.AsVector3(mgr.GetProp(PK.TurnRateX));
                Vector3 trZ = UEInterpreter.AsVector3(mgr.GetProp(PK.TurnRateZ));
                trX = new Vector3(fX.x * k + k1 * trX.x, fX.y * k + k1 * trX.y, fX.z * k + k1 * trX.z);
                trZ = new Vector3(fZ.x * k + k1 * trZ.x, fZ.y * k + k1 * trZ.y, fZ.z * k + k1 * trZ.z);
                mgr.Props["TurnRateX"] = trX;
                mgr.Props["TurnRateZ"] = trZ;
                Vector3 x = InterpNormalize(new Vector3(stepDt * trX.x + cur.X.x, stepDt * trX.y + cur.X.y, stepDt * trX.z + cur.X.z));
                Vector3 z = InterpNormalize(new Vector3(stepDt * trZ.x + cur.Z.x, stepDt * trZ.y + cur.Z.y, stepDt * trZ.z + cur.Z.z));
                Vector3 y = InterpNormalize(InterpCross(z, x));
                z = InterpNormalize(InterpCross(x, y));
                result.X = x; result.Y = y; result.Z = z;
            }
            mgr.Props["TurnRateX"] = new Vector3((result.X.x - cur.X.x) * invDt, (result.X.y - cur.X.y) * invDt, (result.X.z - cur.X.z) * invDt);
            mgr.Props["TurnRateZ"] = new Vector3((result.Z.x - cur.Z.x) * invDt, (result.Z.y - cur.Z.y) * invDt, (result.Z.z - cur.Z.z) * invDt);
        }

        // ── AInterpolationPoint::GetInterpolatedPosition ────────────────────
        // prev — coordinates of the previous point (axes UnitCoords/Prev.Rotation,
        // Origin = Prev.Location; without Prev — Owner), startCP — Prev.StartControlPoint.
        internal BrushCoords GetInterpolatedPosition(UEObject pt, in BrushCoords prev, Vector3 startCP, float alpha, int pauseNum)
        {
            Vector3 ptLoc = UEInterpreter.AsVector3(pt.GetProp(PK.Location));
            if (UEInterpreter.ToBool(pt.GetProp(PK.bInstantNextPath)))
            {
                UERotator r0 = UEInterpreter.AsRotator(pt.GetProp(PK.Rotation));
                BrushCoords snap = BrushCoords.Identity;
                snap.DivRotator(r0.Pitch, r0.Yaw, r0.Roll);
                snap.Origin = ptLoc;
                return snap;
            }
            // Bernstein weights in the register (0x103f6a9d-0x103f6ae5): fld 1; fsub alpha; fst s — the unstored 1−alpha
            // multiplies s; b0..b2 are stored as floats, b3 = alpha³ stays in st0 for the three products with Location.
            HP1.Core.X87Ext sx = R(X(1f) - X(alpha));
            float s = sx.ToFloat();
            HP1.Core.X87Ext ss = R(sx * X(s));
            float b0 = R(ss * X(s)).ToFloat();
            float b1 = R(R(ss * X(alpha)) * X(3f)).ToFloat();
            float b2 = R(R(R(X(s) * X(alpha)) * X(alpha)) * X(3f)).ToFloat();
            HP1.Core.X87Ext t2x = R(X(alpha) * X(alpha));
            float t2 = t2x.ToFloat();
            HP1.Core.X87Ext b3 = R(t2x * X(alpha));
            Vector3 ecpRel = UEInterpreter.AsVector3(pt.GetProp(PK.EndControlPoint));
            Vector3 ecp = new Vector3(ecpRel.x + ptLoc.x, ecpRel.y + ptLoc.y, ecpRel.z + ptLoc.z);
            Vector3 scp = new Vector3(startCP.x + prev.Origin.x, startCP.y + prev.Origin.y, startCP.z + prev.Origin.z);
            Vector3 pos = Bezier(ptLoc, ecp, scp, prev.Origin, b3, b2, b1, b0);

            BrushCoords r = BrushCoords.Identity;
            bool faceMove = UEInterpreter.ToBool(pt.GetProp(PK.bFaceMoveDirection));
            if (!faceMove)
            {
                if (UEInterpreter.ToBool(pt.GetProp(PK.bNewRotationSmoothing)))
                {
                    UERotator rm2 = GetDesiredRotationAtPosition(pt, -2, pauseNum, pos);
                    UERotator rm1 = GetDesiredRotationAtPosition(pt, -1, pauseNum, pos);
                    UERotator r0 = GetDesiredRotationAtPosition(pt, 0, pauseNum, pos);
                    UERotator r1 = GetDesiredRotationAtPosition(pt, 1, pauseNum, pos);
                    float t3 = t2 * alpha;
                    float k1 = (t3 - t2) * 0.5f;
                    UERotator a = ScaleRound(new UERotator(r1.Pitch - rm1.Pitch, r1.Yaw - rm1.Yaw, r1.Roll - rm1.Roll), k1);
                    float k2 = ((t3 - (t2 + t2)) + alpha) * 0.5f;
                    UERotator b = ScaleRound(new UERotator(r0.Pitch - rm2.Pitch, r0.Yaw - rm2.Yaw, r0.Roll - rm2.Roll), k2);
                    float t2x3 = t2 * 3f;
                    float t3x2 = t3 + t3;
                    UERotator c = ScaleRound(r0, t2x3 - t3x2);
                    UERotator d = ScaleRound(rm1, (t3x2 - t2x3) + 1f);
                    UERotator rot = new UERotator(a.Pitch + b.Pitch + c.Pitch + d.Pitch,
                        a.Yaw + b.Yaw + c.Yaw + d.Yaw, a.Roll + b.Roll + c.Roll + d.Roll);
                    r.DivRotator(rot.Pitch, rot.Yaw, rot.Roll);
                }
                else if (ViewTargetTagAt(pt, pauseNum) is null)
                {
                    float a = alpha;
                    if (UEInterpreter.ToBool(pt.GetProp(PK.bTurnChange))) a = (float)Math.Sqrt((double)alpha);
                    UERotator pr = UEInterpreter.AsRotator(pt.GetProp(PK.Rotation));
                    BrushCoords tc = BrushCoords.Identity;
                    tc.DivRotator(pr.Pitch, pr.Yaw, pr.Roll);
                    Vector3 x = InterpNormalize(new Vector3(tc.X.x * a + prev.X.x * s, tc.X.y * a + prev.X.y * s, tc.X.z * a + prev.X.z * s));
                    Vector3 zb = new Vector3(prev.Z.x * s + tc.Z.x * a, prev.Z.y * s + tc.Z.y * a, prev.Z.z * s + tc.Z.z * a);
                    Vector3 y = InterpNormalize(InterpCross(zb, x));
                    Vector3 z = InterpNormalize(InterpCross(x, y));
                    r.X = x; r.Y = y; r.Z = z;
                }
                else
                {
                    UEObject vt = ResolveViewTarget(pt, pauseNum);
                    if (vt is not null)
                    {
                        Vector3 vl = UEInterpreter.AsVector3(vt.GetProp(PK.Location));
                        UERotator dr = UEValue.RotatorFromVector(new Vector3(vl.x - pos.x, vl.y - pos.y, vl.z - pos.z));
                        r.DivRotator(dr.Pitch, dr.Yaw, dr.Roll);
                    }
                    else r = prev;
                }
            }
            else
            {
                // t1 = alpha − 0.001f (0x10473814) and 1 − t1 stay in the register; c0..c2 stored, c3 = t1³ kept
                // (0x103f6d45-0x103f6dd9).
                HP1.Core.X87Ext t1 = R(X(alpha) - X(0.001f));
                if (t1.Sign > 0)
                {
                    HP1.Core.X87Ext s1 = R(X(1f) - t1);
                    HP1.Core.X87Ext s1s = R(s1 * s1);
                    float c0 = R(s1s * s1).ToFloat();
                    float c1 = R(R(s1s * t1) * X(3f)).ToFloat();
                    float c2 = R(R(R(s1 * t1) * t1) * X(3f)).ToFloat();
                    HP1.Core.X87Ext c3 = R(R(t1 * t1) * t1);
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
        static Vector3 Bezier(Vector3 loc, Vector3 ecp, Vector3 scp, Vector3 prevOrigin, HP1.Core.X87Ext b3, float b2, float b1, float b0)
        {
            float x = R(b3 * X(loc.x)).ToFloat(), y = R(b3 * X(loc.y)).ToFloat(), z = R(b3 * X(loc.z)).ToFloat();
            float ex = ecp.x * b2, ey = ecp.y * b2, ez = ecp.z * b2;
            float sx = scp.x * b1, sy = scp.y * b1, sz = scp.z * b1;
            float px = prevOrigin.x * b0, py = prevOrigin.y * b0, pz = prevOrigin.z * b0;
            float ax = sx + px, ay = sy + py, az = sz + pz;
            ax = ex + ax; ay = ey + ay; az = ez + az;
            return new Vector3(x + ax, y + ay, z + az);
        }

        // FRotator(ROUND(int·k)) — `fild; fmul; fistp` with round-to-nearest-even.
        static UERotator ScaleRound(UERotator r, float k) => new UERotator(
            (int)Math.Round((double)((float)r.Pitch * k), MidpointRounding.ToEven),
            (int)Math.Round((double)((float)r.Yaw * k), MidpointRounding.ToEven),
            (int)Math.Round((double)((float)r.Roll * k), MidpointRounding.ToEven));

        // ── AInterpolationPoint::GetDesiredRotationAtPosition (83797) ───────
        // n<0 — backward along Prev through pauses, n>0 — forward along Next; 0 — the point itself.
        internal UERotator GetDesiredRotationAtPosition(UEObject pt, int n, int pn, Vector3 pos)
        {
            UEObject target;
            if (n < 0)
            {
                if (pn < 1)
                {
                    if (UEInterpreter.ToBool(pt.GetProp(PK.bInstantNextPath))) return GetDesiredRotationAtPause(pt, pn, pos);
                    UEObject prev = pt.GetProp(PK.Prev) as UEObject;
                    if (prev is null) return GetDesiredRotationAtPause(pt, pn, pos);
                    pn = 0;
                    while (PauseAt(prev, pn) > 0f) pn++;
                    n++;
                    target = prev;
                }
                else { pn--; n++; target = pt; }
            }
            else
            {
                if (n == 0) return GetDesiredRotationAtPause(pt, pn, pos);
                while (PauseAt(pt, pn + 1) > 0f && n > 0) { pn++; n--; }
                UEObject next = pt.GetProp(PK.Next) as UEObject;
                if (next is null || n == 0 || UEInterpreter.ToBool(next.GetProp(PK.bInstantNextPath)))
                    return GetDesiredRotationAtPause(pt, pn, pos);
                pn = 0;
                target = next;
            }
            return GetDesiredRotationAtPosition(target, n, pn, pos);
        }

        // ── AInterpolationPoint::GetDesiredRotationAtPause (77903) ──────────
        internal UERotator GetDesiredRotationAtPause(UEObject pt, int pn, Vector3 pos)
        {
            if (ViewTargetTagAt(pt, pn) is not null)
            {
                UEObject vt = ResolveViewTarget(pt, pn);
                if (vt is not null)
                {
                    Vector3 vl = UEInterpreter.AsVector3(vt.GetProp(PK.Location));
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

        // ViewTarget[pn] is refreshed by a search over Level.Actors when empty or its
        // Tag does not match (Engine.dll.c:77913-77935); first actor with that Tag.
        UEObject ResolveViewTarget(UEObject pt, int pn)
        {
            string tag = ViewTargetTagAt(pt, pn);
            if (tag is null) return null;
            IList<object> targets = pt.GetProp(PK.ViewTarget) as IList<object>;
            UEObject vt = targets is not null && pn < targets.Count ? targets[pn] as UEObject : null;
            if (vt is null || !string.Equals(vt.GetProp(PK.Tag)?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                vt = null;
                foreach (UEObject a in Actors)
                {
                    if (a is null) continue;
                    if (string.Equals(a.GetProp(PK.Tag)?.ToString(), tag, StringComparison.OrdinalIgnoreCase)) { vt = a; break; }
                }
                if (vt is not null && targets is not null && pn < targets.Count) targets[pn] = vt;
            }
            return vt;
        }

        // FVector::Normalize: if |v|² ≥ 1e-8 multiply by 1/sqrt (double), else unchanged.
        static Vector3 InterpNormalize(Vector3 v)
        {
            float len2 = v.x * v.x + v.y * v.y + v.z * v.z;
            if (len2 < InterpSmallNumber) return v;
            double inv = 1.0 / Math.Sqrt((double)len2);
            return new Vector3((float)(v.x * inv), (float)(v.y * inv), (float)(v.z * inv));
        }

        // FVector::operator^ — cross product.
        static Vector3 InterpCross(Vector3 a, Vector3 b) => new Vector3(
            a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
    }
}
