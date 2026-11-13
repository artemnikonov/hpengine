// The three STEP bodies the reachability mode bodies drive — APawn::walkMove
// (Engine.dll.c:139711-139811), APawn::flyMove (92952-93003), APawn::swimMove
// (133612-133686) — plus the private APawn::findWaterLine (56662-56692).
//
// All three share one skeleton: move once, and if a target was named and the
// hit is that target, answer 5. What differs is what each does when the move
// is blocked, and what each considers "progress".
//
// The return codes are a real protocol, not a bool in disguise:
//    5  the step touched the NAMED TARGET (checked after every sub-move)
//    1  the step covered at least `minProgress` — the pawn STAYS moved
//    0  no meaningful progress; walkMove also answers 0 for "walled in by
//       something steep", which is what sends walkReachable to FindJumpUp
//   -1  walkMove only: no floor ahead (a ledge), or landed on a steep face —
//       what sends walkReachable to FindBestJump or shrinks the step
//
// Only walkMove sees a FCheckResult in its signature and only walkMove can
// answer -1; flying and swimming have no notion of a ledge.
//
// The "down" direction is NOT hardcoded: it comes from the sign of the zone's
// gravity, so inverted-gravity zones mirror the whole step-up/step-down dance.
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        // Snapshot of the FCheckResult record after a move, as the step bodies read it.
        // The record itself belongs to the CALLER's MoveCheck (PF-7): a refusal and the MoveActor
        // fast path leave it untouched, so the snapshot then repeats the previous move.
        readonly struct ReachHit
        {
            public readonly float Time;      // FCheckResult.Time — 1.0 means "no hit"
            public readonly Vector3 Normal;
            public readonly UEObject Actor;  // null = world hit or nothing blocked
            public ReachHit(float time, Vector3 normal, UEObject actor)
            { Time = time; Normal = normal; Actor = actor; }
        }

        // Test seam, named so at the call site. The loop protocol of the
        // reachability bodies is a property of HOW MANY ITERATIONS run, not of the
        // number they return: a body that wrongly retries a failed step exhausts
        // its 100-step budget and answers the same 0 it would have answered at
        // once. Nothing observable from outside separates those.
        //
        // Counts ITERATIONS — one per walkMove/flyMove/swimMove call — not the
        // inner MoveActor calls, of which a single step body issues up to four
        // (move, lift, carry, descend). Counting those conflated "how many times
        // the loop ran" with "how blocked each step was".
        // internal, not public: this is not shipped state and the frontends have
        // no business reading or resetting it. Tests get access
        // through InternalsVisibleTo in HP1.Core.csproj.
        internal int ReachStepCountForTest { get; private set; }
        internal void ResetReachStepCountForTest() => ReachStepCountForTest = 0;

        // Shipped passes (1,1,0,0) for params 10-13 at every step call site.
        ReachHit ReachMoveActor(UEInterpreter interp, UEObject pawn, Vector3 delta, MoveCheck rec)
        {
            MoveActor(interp, pawn, delta, UEInterpreter.AsRotator(pawn.GetProp(PK.Rotation)),
                bTest: true, bIgnorePawns: true, bIgnoreBases: false, bNoFail: false, rec);
            return new ReachHit(rec.Time, rec.Normal, rec.Actor);
        }

        // walkMove/flyMove/swimMove all derive "down" from ZoneGravity.Z rather
        // than assuming -Z (139737: `-1.0` unless gravity is non-negative).
        // Public as a test seam: a fixture cannot observe this through a whole
        // query, because MoveActor re-zones the pawn on every step and a
        // substituted zone only survives the first one. Pinned directly instead.
        public static float DownSign(UEObject pawn)
            => UEInterpreter.AsVector3(
                   ZoneOf(pawn, "Region")?.GetProp(PK.ZoneGravity)).z > 0f ? 1f : -1f;

        static bool TouchedTarget(in ReachHit hit, UEObject target)
            => target is not null && ReferenceEquals(hit.Actor, target);

        // Step progress of walkMove 0x103dec4f..0x103dec8d, flyMove 0x103df04f..0x103df08d and swimMove
        // 0x103df4ff..0x103df53d (one layout): differences and (dx² + dy²) + dz² in the register, minProgress² in the
        // register; progressed unless minProgress² > the sum (`fcompp; test ah,0x41; jne`: an ordered "above" only, NaN
        // progresses).
        internal static bool ReachProgressed(Vector3 now, Vector3 entry, float minProgress)
            => (HP1.Core.X87Ext.Fcom(R64(HP1.Core.X87Ext.From(minProgress) * HP1.Core.X87Ext.From(minProgress)), DistSq87(now, entry)) & 0x41) != 0;

        static float DistanceSquared(Vector3 a, Vector3 b)
        {
            Vector3 d = a - b;
            return Vector3.Dot(d, d);
        }

        // APawn::walkMove (Engine.dll.c:139711-139811).
        // `rec` is the caller's OWN FCheckResult, the way shipped passes one in
        // by reference (walkMove's second parameter, param_5): EVERY move of
        // the body — the lift included — writes it, and a fast-path/refused
        // move leaves what the previous one wrote (PF-7).
        int WalkMove(UEInterpreter interp, UEObject pawn, Vector3 delta,
            UEObject target, float minProgress, bool bRestoreToEntry, MoveCheck rec)
        {
            ReachHit hit;
            ReachStepCountForTest++;
            Vector3 entry = UEInterpreter.AsVector3(pawn.GetProp(PK.Location));
            float down = DownSign(pawn);
            float step = UEInterpreter.AsFloat(pawn.GetProp(PK.MaxStepHeight));

            // The vertical component of the request is DISCARDED (139733).
            delta = new Vector3(delta.x, delta.y, 0f);

            hit = ReachMoveActor(interp, pawn, delta, rec);
            if (TouchedTarget(hit, target)) return 5;

            if (hit.Time < 1f)
            {
                // Step up, carry the unspent fraction, step back down; the
                // target is re-checked after each (139746-139768).
                float remaining = 1f - hit.Time;
                ReachMoveActor(interp, pawn, new Vector3(0f, 0f, step * down * -1f), rec);
                hit = ReachMoveActor(interp, pawn,
                    new Vector3(delta.x * remaining, delta.y * remaining, 0f), rec);
                if (TouchedTarget(hit, target)) return 5;

                hit = ReachMoveActor(interp, pawn, new Vector3(0f, 0f, step * down), rec);
                if (TouchedTarget(hit, target)) return 5;

                // Still blocked by something too steep to walk: give up here so
                // the caller can try to jump UP (139772).
                if (hit.Time < 1f && hit.Normal.z < 0.7f)
                {
                    FarMove(interp, pawn, entry, bTest: true, bNoCheck: true);
                    return 0;
                }
            }

            // Ledge probe: reach one step (plus 2) further down and see whether
            // anything is there (139778-139795).
            Vector3 beforeDescent = UEInterpreter.AsVector3(pawn.GetProp(PK.Location));
            hit = ReachMoveActor(interp, pawn,
                new Vector3(0f, 0f, (step + 2f) * down), rec);

            if (hit.Time == 1f)
            {
                // Nothing underneath. WHERE the pawn is put back depends on the
                // caller's flag — the two restores target different points.
                FarMove(interp, pawn, bRestoreToEntry ? entry : beforeDescent,
                    bTest: true, bNoCheck: true);
                return -1;
            }

            // Here 0.7 is a DOUBLE (`fcomp qword [0x10479e48]`, 0x103dec03), unlike the
            // float 0.7 of the step-up above (0x103deaac). A normal of exactly
            // 0.7f (0.69999998…) is a steep face in shipped.
            if (hit.Normal.z >= 0.7)
            {
                // Landed on walkable ground: it only counts as a step if the
                // pawn actually covered minProgress (139797-139806).
                Vector3 now = UEInterpreter.AsVector3(pawn.GetProp(PK.Location));
                if (!ReachProgressed(now, entry, minProgress))
                {
                    if (bRestoreToEntry)
                        FarMove(interp, pawn, entry, bTest: true, bNoCheck: true);
                    return 0;
                }
                return 1;
            }

            // Landed on a steep face — same answer as a ledge (139809).
            FarMove(interp, pawn, entry, bTest: true, bNoCheck: true);
            return -1;
        }

        // APawn::flyMove (Engine.dll.c:92952-93003). No ledge probe, no -1, and
        // the request keeps its vertical component.
        int FlyMove(UEInterpreter interp, UEObject pawn, Vector3 delta,
            UEObject target, float minProgress, bool bRestoreOnFail)
        {
            ReachStepCountForTest++;
            Vector3 entry = UEInterpreter.AsVector3(pawn.GetProp(PK.Location));
            float step = UEInterpreter.AsFloat(pawn.GetProp(PK.MaxStepHeight));

            // A fresh FCheckResult(1.0) per call (Engine.dll.c:92968-92972).
            var rec = new MoveCheck();
            ReachHit hit = ReachMoveActor(interp, pawn, delta, rec);
            if (TouchedTarget(hit, target)) return 5;

            if (hit.Time < 1f)
            {
                float remaining = 1f - hit.Time;
                // Shipped writes NEGATIVE ZERO into X and Y of the lift vector
                // (0x80000000 at 92959); kept literally.
                ReachMoveActor(interp, pawn, new Vector3(-0f, -0f, step), rec);
                hit = ReachMoveActor(interp, pawn, delta * remaining, rec);
                if (TouchedTarget(hit, target)) return 5;
            }

            Vector3 now = UEInterpreter.AsVector3(pawn.GetProp(PK.Location));
            if (ReachProgressed(now, entry, minProgress)) return 1;

            if (bRestoreOnFail) FarMove(interp, pawn, entry, bTest: true, bNoCheck: true);
            return 0;
        }

        // APawn::swimMove (Engine.dll.c:133612-133686). Same skeleton as flyMove
        // until the pawn leaves the water — that case has its OWN tail.
        int SwimMove(UEInterpreter interp, UEObject pawn, Vector3 delta,
            UEObject target, float minProgress, bool bRestoreOnFail)
        {
            ReachStepCountForTest++;
            Vector3 entry = UEInterpreter.AsVector3(pawn.GetProp(PK.Location));
            float step = UEInterpreter.AsFloat(pawn.GetProp(PK.MaxStepHeight));

            // A fresh FCheckResult(1.0) per call (Engine.dll.c:133629-133633).
            var rec = new MoveCheck();
            ReachHit hit = ReachMoveActor(interp, pawn, delta, rec);
            if (TouchedTarget(hit, target)) return 5;

            if (!UEInterpreter.ToBool(ZoneOf(pawn, "Region")?.GetProp(PK.bWaterZone)))
            {
                // Surfaced. Settle onto the water line and answer 0 — surfacing
                // is NEVER a successful step, and the position is NOT restored.
                // swimReachable is what notices the zone change and switches
                // mode (133645-133659).
                Vector3 surfaced = UEInterpreter.AsVector3(pawn.GetProp(PK.Location));
                Vector3 line = surfaced;
                FindWaterLine(pawn, entry, ref line);
                // Shipped compares componentwise and EXACTLY, with no epsilon.
                if (line.x != surfaced.x || line.y != surfaced.y || line.z != surfaced.z)
                    ReachMoveActor(interp, pawn, line - surfaced, rec);
                return 0;
            }

            if (hit.Time < 1f)
            {
                float remaining = 1f - hit.Time;
                ReachMoveActor(interp, pawn, new Vector3(-0f, -0f, step), rec);
                hit = ReachMoveActor(interp, pawn, delta * remaining, rec);
                if (TouchedTarget(hit, target)) return 5;
            }

            Vector3 now = UEInterpreter.AsVector3(pawn.GetProp(PK.Location));
            if (ReachProgressed(now, entry, minProgress)) return 1;

            if (bRestoreOnFail) FarMove(interp, pawn, entry, bTest: true, bNoCheck: true);
            return 0;
        }

        // APawn::findWaterLine (Engine.dll.c:56662-56692) — a recursive bisection
        // that converges on the water boundary, NOT a trace and not a formula.
        // The side test compares the midpoint's water flag against the PAWN'S
        // CURRENT zone, so the caller must invoke it after the pawn has already
        // crossed; comparing against `start`'s zone converges to the mirror point.
        void FindWaterLine(UEObject pawn, Vector3 start, ref Vector3 end)
        {
            if (DistanceSquared(end, start) < 1f) return;

            Vector3 mid = (start + end) * 0.5f;
            UEObject midZone = ResolveRegion(mid, pawn.GetProp(PK.Level) as UEObject).Zone;
            bool midMatchesPawn =
                UEInterpreter.ToBool(midZone?.GetProp(PK.bWaterZone))
                == UEInterpreter.ToBool(ZoneOf(pawn, "Region")?.GetProp(PK.bWaterZone));

            if (midMatchesPawn) end = mid;
            else start = mid;
            FindWaterLine(pawn, start, ref end);
        }
    }
}
