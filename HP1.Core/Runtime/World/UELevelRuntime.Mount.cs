// APawn::Mount — Core-owned; the single implementation of ledge mounting (no frontend hook).
//
// Source: shipped `APawn::Mount` (Engine.dll 0x103ec190 / Engine.dll.c:9414).
// FCheckResult::BspNode resolves to the authored PF_SpecialPoly surface;
// then the native probes the ledge and two free cylinder paths via
// ULevel::SingleLineCheck(flags=6), bases the pawn on the source wall actor and
// sends the script event Mount.
using System;
using UELib.Core;
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        public const int PfSpecialPoly = 0x00001000;

        // WHY it refused — diagnostics, not behaviour.
        //
        // The shipped body is eight consecutive gates, and "false" at the exit says
        // nothing: a refusal on the normal band and one on the ledge probe are
        // different news. Written on every exit and read by a fixture; nothing goes
        // to the log, so logs are unaffected.
        public string LastMountReject;

        // Test seam: the record from the _lastMove* publication of the last full move
        // (fixtures feed Mount by hand). Production calls the overload with the
        // caller's FCheckResult.
        public bool TryMount(UEInterpreter interp, UEObject pawn, Vector3 up)
            => TryMount(interp, pawn, up, MoveCheck.FromActorProps(pawn));

        // `hit` — the caller's FCheckResult (APawn::Mount(Delta, Hit&),
        // Engine.dll.c:9414): physFalling and stepUp pass their OWN local.
        public bool TryMount(UEInterpreter interp, UEObject pawn, Vector3 up, MoveCheck hit)
        {
            LastMountReject = null;
            float maxMount = UEInterpreter.AsFloat(pawn.GetProp(PK.MaxMountHeight));
            Vector3 hitNormal = hit.Normal;
            if (!(maxMount > 0f) || !(hitNormal.z > -0.1f) || !(hitNormal.z < 0.7f))
                { LastMountReject = "gate1_band_or_maxmount"; return false; }

            UERotator rotation = UEInterpreter.AsRotator(pawn.GetProp(PK.Rotation));
            BrushCoords facing = BrushCoords.Identity;
            facing.DivRotator(rotation.Pitch, rotation.Yaw, rotation.Roll);
            if (!(Vector3.Dot(facing.X, hitNormal) < 0f))
            { LastMountReject = "gate2_not_facing_wall"; return false; }

            float radius = UEInterpreter.AsFloat(pawn.GetProp(PK.CollisionRadius));
            // Extent — AActor::GetCylinderExtent(this) (Engine.dll.c:9484, call
            // 0x103ec08c); all three probes below use it (9562/9576/9579).
            // GetMax over absolute values in shipped order (9488-9505): |x| vs |y|
            // strictly less, then |z| vs the result non-strictly.
            Vector3 extent = GetCylinderExtent(pawn);
            float maxExtent = Math.Abs(extent.x);
            if (maxExtent < Math.Abs(extent.y)) maxExtent = Math.Abs(extent.y);
            if (!(Math.Abs(extent.z) <= maxExtent)) maxExtent = Math.Abs(extent.z);
            int node = hit.Item;
            Vector3 hitLocation = hit.Location;
            UEObject hitActor = hit.Actor;

            HeadlessBspModel hitModel;
            BrushCoords? toLocal = null;
            if (hitActor is null || ReferenceEquals(hitActor, interp.GetLevelInfo()))
            {
                hitModel = Bsp;
                hitActor = interp.GetLevelInfo();
            }
            else if (hitActor.GetProp(PK.Brush) is not null
                     || UEInterpreter.ClassIsA(hitActor.Class, "Mover"))
            {
                hitModel = BrushModelOf(hitActor);
                toLocal = BrushToLocalCoords(hitActor);
            }
            else
            {
                LastMountReject = "gate3_hit_primitive_not_a_model";
                return false; // FCheckResult.Primitive is not a UModel.
            }

            int? surfaceFlags = hitModel?.HitSurfaceFlags(node, hitLocation, hitNormal,
                maxExtent, toLocal);
            if (!surfaceFlags.HasValue || (surfaceFlags.Value & PfSpecialPoly) == 0)
            { LastMountReject = "gate4_surface_not_special_poly"; return false; }

            Vector3 towardWall = new(-hitNormal.x, -hitNormal.y, 0f);
            float horizontal2 = towardWall.x * towardWall.x + towardWall.y * towardWall.y;
            if (horizontal2 >= 1e-8f)
                towardWall *= (float)(1.0 / Math.Sqrt(horizontal2));

            Vector3 location = UEInterpreter.AsVector3(pawn.GetProp(PK.Location));
            Vector3 raised = location + up * maxMount;
            Vector3 ledgeProbeStart = raised
                + towardWall * (maxMount * hitNormal.z + radius + radius);
            Vector3 ledgeProbeEnd = ledgeProbeStart - up * maxMount
                - towardWall * (maxMount * hitNormal.z);
            LineCheckHit ledge = SingleLineCheck(interp, pawn, pawn,
                ledgeProbeStart, ledgeProbeEnd, extent, traceFlags: 6);
            if (!ledge.Blocked) { LastMountReject = "gate5_no_ledge_under_probe"; return false; }

            float minimumRise = UEInterpreter.ToInt(pawn.GetProp(PK.Physics)) == 2
                ? 0f : UEInterpreter.AsFloat(pawn.GetProp(PK.MaxStepHeight));
            if (!(minimumRise <= ledge.Location.z - location.z))
            { LastMountReject = "gate6_ledge_too_low"; return false; }

            Vector3 mountPoint = new(ledge.Location.x, ledge.Location.y, ledge.Location.z + 2f);
            if (SingleLineCheck(interp, pawn, pawn, location, raised, extent, 6).Blocked)
            { LastMountReject = "gate7_rise_blocked"; return false; }
            if (SingleLineCheck(interp, pawn, pawn, raised, mountPoint, extent, 6).Blocked)
            { LastMountReject = "gate8_reach_blocked"; return false; }

            SetBase(interp, pawn, hitActor, notify: true);
            Vector3 delta = mountPoint - location;
            Log($"mount actor={pawn.GetProp(PK.Name)} base={hitActor?.GetProp(PK.Name)}"
                + $" delta={delta} node={node} surfFlags=0x{surfaceFlags.Value:x}");
            UELifecycle.CallEvent(interp, pawn, "Mount", delta);
            return true;
        }
    }
}
