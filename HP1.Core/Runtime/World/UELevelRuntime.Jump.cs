// APawn's jump subgraph: SuggestJumpVelocity (Engine.dll.c:104282, body
// 0x103de290), jumpLanding (19594 / 0x103ddab0) and FindBestJump (99425 /
// 0x103de560). walkReachable reaches all three through the last one.
//
// The three are ported together because they only make sense together: the
// first invents a velocity, the second flies the arc and LEAVES THE PAWN where
// it lands, and the third owns the decision and the restore. Splitting them
// produced the STOP this file replaces.
//
// Semantics that a "plausible" port silently gets wrong, each read out of the
// shipped machine code and each load-bearing:
//
//   * The integration step is 0.05 in SuggestJumpVelocity and 0.1 in
//     jumpLanding. Two bodies of one subgraph, two different steps.
//   * SuggestJumpVelocity's zeroing of the output Z is TEMPORARY. The incoming
//     Z is saved as a dword on entry and copied back as the LAST instruction
//     (0x103de458), so the caller's seed survives the whole body. Porting the
//     decompile literally yields a strictly horizontal jump.
//   * jumpLanding reads gravity from the zone it starts the step in, but the
//     water gate RE-READS this->Region.Zone AFTER the move (0x103ddeb9 block
//     dereferences this+0xa0 a second time). The two are not the same zone
//     when the step crosses a boundary.
//   * A landing detected by the SECOND move does not end the iteration: the
//     two-wall-adjusted THIRD move still runs (19726-19741).
//   * There is no "the jump failed" branch. Every emergency exit farmoves the
//     pawn back to entry and only then does the top of the loop read the
//     landing spot, so the caller sees zero progress and rejects the jump.
//   * walkReachable calls FindBestJump with the restore flag SET TO 1, i.e.
//     asks it NOT to restore (disassembly 0x103dd353: `push 1` is the last
//     argument pushed). The pawn keeps walking from where it landed. The
//     decompile at 149126 drops the last two call arguments entirely, which is
//     why this had to come off the disassembly.
//
// PRECISION IS PART OF THE PORT. The shipped build is x87 and the disassembly
// says exactly which values round to float32 and which stay wide:
//   * vz and time round on every iteration (fst [esi+8], fstp [ebp+0x14]);
//   * the flight height NEVER touches memory — it lives in ST(0) across the
//     whole loop (faddp st(1),st at 0x103de4be) and the loop's exit iteration
//     depends on it, so a float32 accumulator here moves `time` by a whole
//     0.05 step;
//   * the horizontal length is a double (appSqrt returns one, stored with
//     fstp qword), and so are 1/len, len/time and the |velocity|² cutoff sum.
// `double` in those places is faithfulness, not convenience —
// se_harness/bsp_diff/jump_oracle.py executes the real bodies under the
// emulator and validate_jump.py diffs this port against them.
using System;
using System.Collections.Generic;
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        // What the jump subgraph read from the world and what the world answered,
        // recorded for the oracle. The recorder is OFF unless a fixture
        // allocates the list: production runs never allocate and never branch on
        // anything but a null check, so the route the oracle diffs is the
        // production route byte for byte (C9). It only OBSERVES — nothing here
        // can change what is asked, in which order, or where the pawn ends up.
        //
        // Both directions are needed. The REQUESTS are what the diff compares
        // (a port that computes a wrong step asks a different question); the
        // ANSWERS and the per-iteration zone inputs are what the oracle replays,
        // so the shipped body runs against the same world without that world
        // being modelled a second time anywhere.
        internal readonly struct JumpTraceStep
        {
            public readonly string Kind;         // "iter" | "move" | "farmove" | "gate"
            public readonly Vector3 Request;     // move: the delta; farmove: the destination
            public readonly float Time;          // move: hit fraction the world answered
            public readonly Vector3 Normal;      // move: hit normal
            public readonly Vector3 Gravity;     // iter: the zone's, BEFORE the step
            public readonly Vector3 ZoneVelocity;// iter
            public readonly float Friction;      // iter
            public readonly bool Water;          // move: the zone the pawn is in AFTER it
            public readonly Vector3 Location;
            public readonly int ZoneNumber;
            public readonly UEObject Zone;       // identity — the oracle models ONE zone
            public readonly bool Pain, SameDamage, CanSwim, WaterGate;   // gate

            JumpTraceStep(string kind, Vector3 request, float time, Vector3 normal,
                Vector3 gravity, Vector3 zoneVelocity, float friction, bool water,
                Vector3 location, int zoneNumber, UEObject zone,
                bool pain, bool sameDamage, bool canSwim, bool waterGate)
            {
                Kind = kind; Request = request; Time = time; Normal = normal;
                Gravity = gravity; ZoneVelocity = zoneVelocity; Friction = friction;
                Water = water; Location = location; ZoneNumber = zoneNumber; Zone = zone;
                Pain = pain; SameDamage = sameDamage; CanSwim = canSwim; WaterGate = waterGate;
            }

            public static JumpTraceStep Iteration(Vector3 gravity, Vector3 zoneVelocity,
                float friction, UEObject zone)
                => new JumpTraceStep("iter", Vector3.zero, 0f, Vector3.zero,
                    gravity, zoneVelocity, friction, false, Vector3.zero, 0, zone,
                    false, false, false, false);

            public static JumpTraceStep Move(string kind, Vector3 request, float time,
                Vector3 normal, bool water, Vector3 location, int zoneNumber)
                => new JumpTraceStep(kind, request, time, normal,
                    Vector3.zero, Vector3.zero, 0f, water, location, zoneNumber, null,
                    false, false, false, false);

            public static JumpTraceStep Gate(bool pain, bool sameDamage, bool canSwim,
                bool waterGate)
                => new JumpTraceStep("gate", Vector3.zero, 0f, Vector3.zero,
                    Vector3.zero, Vector3.zero, 0f, false, Vector3.zero, 0, null,
                    pain, sameDamage, canSwim, waterGate);
        }

        internal List<JumpTraceStep> JumpTraceForTest;

        void RecordJumpStep(string kind, Vector3 request, in ReachHit hit, UEObject pawn)
        {
            if (JumpTraceForTest is null) return;
            JumpTraceForTest.Add(JumpTraceStep.Move(kind, request, hit.Time, hit.Normal,
                UEInterpreter.ToBool(ZoneOf(pawn, "Region")?.GetProp(PK.bWaterZone)),
                UEInterpreter.AsVector3(pawn.GetProp(PK.Location)),
                UEInterpreter.ToInt((pawn.GetProp(PK.Region) as UEObject)?.GetProp(PK.ZoneNumber))));
        }

        // APawn::SuggestJumpVelocity (104282-104358). Computes only — it never
        // moves the pawn and never touches the world.
        //
        // The shipped signature is (FVector Destination, FVector& Velocity) and
        // the second argument is IN/OUT: the caller seeds its Z, the body works
        // on that slot as the vertical speed of a simulated arc, and hands the
        // original bit pattern back untouched. The port takes the seed by value
        // and returns the whole vector, which is the same contract without the
        // aliasing.
        internal Vector3 SuggestJumpVelocity(UEObject pawn, Vector3 dest, float seedZ)
        {
            UEObject zone = ZoneOf(pawn, "Region");
            if (zone is null)
                throw new NotSupportedException(
                    "SuggestJumpVelocity without a zone — shipped dereferences "
                    + "Region.Zone unconditionally (0x103de2ba), so this state cannot "
                    + $"be reached in the original: pawn={pawn?.GetProp(PK.Name)}");

            // A zone that does not pull DOWN is replaced outright, not clamped
            // to zero: 0x103de2d6 stores 0xc2c80000 = -100.
            float gravity = UEInterpreter.AsVector3(zone.GetProp(PK.ZoneGravity)).z;
            if (gravity >= 0f) gravity = -100f;

            Vector3 loc = UEInterpreter.AsVector3(pawn.GetProp(PK.Location));
            // Computed ONCE before the loop and kept as float32 (0x103de2dd).
            float targetDz = dest.z - loc.z;

            float vz = seedZ;
            float time = 0f;
            double height = 0.0;

            // Keep flying while the arc is still ABOVE the wanted height or
            // still rising; it leaves only when both are false. Both tests are
            // strict (0x103de2ff, 0x103de30d).
            while (height > targetDz || vz > 0f)
            {
                // fld g; fmul 0.05; fadd vz — the sum stays in ST(0) unrounded
                // and THAT value feeds the height accumulation; only the copy
                // written back into the vertical speed is float32.
                double step = (double)gravity * 0.05f + vz;
                vz = (float)step;
                time += 0.05f;
                height += step * 0.05f;
            }

            // Trim the overshoot of the last whole step, but only when the
            // vertical speed is big enough to divide by (0x103de333: strict
            // 1.0 < |vz|).
            if (Math.Abs(vz) > 1f)
                time = (float)(time - (height - targetDz) / vz);

            // The output starts as the flat offset to the destination: Z is
            // written and then immediately overwritten with an integer zero
            // (0x103de390), so the normalisation below is horizontal-only.
            float outX = dest.x - loc.x;
            float outY = dest.y - loc.y;
            float outZ = 0f;
            float groundSpeed = UEInterpreter.AsFloat(pawn.GetProp(PK.GroundSpeed));
            double scale;

            if (time <= 0f)
            {
                // No flight time at all: keep the direction and run at the
                // pawn's ground speed.
                Vector3 dir = SafeNormal(new Vector3(outX, outY, outZ));
                outX = dir.x; outY = dir.y; outZ = dir.z;
                scale = groundSpeed;
            }
            else
            {
                // appSqrt takes and returns a double; the length and the
                // reciprocal are never rounded to float32 (0x103de3b0/3d6).
                double len = Math.Sqrt((double)outX * outX + (double)outY * outY);
                if (len > 0.0)
                {
                    double inv = 1.0 / len;
                    outX = (float)(inv * outX);
                    outY = (float)(inv * outY);
                    outZ = (float)(inv * outZ);
                }
                scale = len / time;
                if (groundSpeed <= scale) scale = groundSpeed;   // non-strict, 0x103de40c
            }

            // The seed is restored by an integer copy of the saved dword, so it
            // comes back bit-for-bit whatever the arithmetic above did.
            return new Vector3((float)(scale * outX), (float)(scale * outY), seedZ);
        }

        // APawn::jumpLanding (19594-19755). MOVES the pawn along the arc and
        // leaves it at the landing spot unless asked to put it back.
        //
        // `bRestoreToEntry` is the shipped int parameter tested against 0:
        // FindBestJump passes 1 and therefore owns the restore itself.
        internal Vector3 JumpLanding(UEInterpreter interp, UEObject pawn, Vector3 velocity,
            bool bRestoreToEntry)
        {
            Vector3 entry = UEInterpreter.AsVector3(pawn.GetProp(PK.Location));
            const float dt = 0.1f;          // 19657 — NOT SuggestJumpVelocity's 0.05
            bool done = false;
            int count = 0;

            while (true)
            {
                if (done)
                {
                    // Read at the TOP of the iteration, which is why an arc that
                    // died on one of the three emergency exits reports the entry
                    // spot: the restore below already ran last iteration.
                    Vector3 landing = UEInterpreter.AsVector3(pawn.GetProp(PK.Location));
                    if (bRestoreToEntry) JumpFarMove(interp, pawn, entry);
                    return landing;
                }

                UEObject zone = ZoneOf(pawn, "Region");
                if (zone is null)
                    throw new NotSupportedException(
                        "jumpLanding without a zone — shipped dereferences Region.Zone "
                        + "unconditionally (19680), so this state cannot be reached in "
                        + $"the original: pawn={pawn?.GetProp(PK.Name)}");

                Vector3 gravity = UEInterpreter.AsVector3(zone.GetProp(PK.ZoneGravity));
                Vector3 zoneVelocity = UEInterpreter.AsVector3(zone.GetProp(PK.ZoneVelocity));
                float friction = UEInterpreter.AsFloat(zone.GetProp(PK.ZoneFluidFriction));
                JumpTraceForTest?.Add(
                    JumpTraceStep.Iteration(gravity, zoneVelocity, friction, zone));

                // Which of these round to float32 and which do not is not a
                // taste question — it is x87 register allocation, read at
                // 0x103ddb0f-0x103ddbe6, and the oracle measures the difference:
                //   * `drag` is never stored, so it stays wide;
                //   * dt*gravity.X and .Y ARE stored (float32), dt*gravity.Z is
                //     NOT — it survives in a register until the add;
                //   * every velocity*drag product IS stored before the add.
                // The Z asymmetry has no meaning beyond "the compiler kept one
                // more value in a register", which is exactly why guessing here
                // is hopeless and reading is cheap.
                double drag = 1.0 - (double)dt * friction;
                float dtGravX = dt * gravity.x;
                float dtGravY = dt * gravity.y;
                double dtGravZ = (double)dt * gravity.z;
                velocity = new Vector3(
                    (float)(velocity.x * drag) + dtGravX,
                    (float)(velocity.y * drag) + dtGravY,
                    (float)((float)(velocity.z * drag) + dtGravZ));

                // The zone's own drift is added to the step but NOT to the
                // velocity that carries into the next iteration. Same story:
                // the X and Y sums are stored before the scale, the Z sum is not
                // (0x103ddb80-0x103ddbe3).
                float driftX = velocity.x + zoneVelocity.x;
                float driftY = velocity.y + zoneVelocity.y;
                double driftZ = (double)velocity.z + zoneVelocity.z;
                Vector3 delta = new Vector3(
                    driftX * dt, driftY * dt, (float)(driftZ * dt));

                // A fresh FCheckResult(1.0) per iteration, before the first move
                // (Engine.dll.c:19694-19699); the iteration's slides write into it too.
                var jumpHit = new MoveCheck();
                ReachHit hit = JumpMove(interp, pawn, delta, jumpHit);

                // Water is read from the zone the pawn is in AFTER the move, not
                // from the one the step started in. In water there is no sliding
                // at all: the iteration ends right here.
                if (UEInterpreter.ToBool(ZoneOrStop(pawn, "Region",
                        "jumpLanding after the move").GetProp(PK.bWaterZone)))
                {
                    done = true;
                }
                else if (hit.Time < 1f)
                {
                    if (hit.Normal.z > 0.7f)
                    {
                        done = true;                    // stood on a walkable face
                    }
                    else
                    {
                        Vector3 firstNormal = hit.Normal;
                        Vector3 n = hit.Normal;
                        // Same x87 reading as the step above (0x103ddcc9-
                        // 0x103ddd4f). The dot never leaves the register file;
                        // the Y and Z components of the slide are stored before
                        // being scaled, the X component is not.
                        double into = ((double)delta.y * n.y + (double)n.x * delta.x)
                                    + (double)delta.z * n.z;
                        double f = 1.0 - hit.Time;
                        double slideX = (delta.x - into * n.x) * f;
                        float sy = (float)((float)(delta.y - (float)(n.y * into)) * f);
                        float sz = (float)((float)(delta.z - (float)(into * n.z)) * f);
                        Vector3 slide = new Vector3((float)slideX, sy, sz);

                        // Slide only while it still goes the way the step was
                        // asked to go. NON-strict, unlike every landing test in
                        // this body — the mixture is deliberate. The test reads
                        // the UNROUNDED X while the move that follows gets the
                        // rounded one (fst, not fstp, at 0x103ddd4f).
                        double forward = ((double)delta.y * sy + (double)delta.z * sz)
                                       + slideX * delta.x;
                        if (forward >= 0.0)
                        {
                            hit = JumpMove(interp, pawn, slide, jumpHit);
                            if (hit.Time < 1f)
                            {
                                // Landing here does NOT end the iteration.
                                if (hit.Normal.z > 0.7f) done = true;
                                TwoWallAdjust(SafeNormal(delta), ref slide,
                                    hit.Normal, firstNormal, hit.Time);
                                hit = JumpMove(interp, pawn, slide, jumpHit);
                                // No fraction test guards this one.
                                if (hit.Normal.z > 0.7f) done = true;
                            }
                        }
                    }
                }

                count++;
                // Three emergency exits, one restore. The squared speed is
                // accumulated wider than float32 (x87) and compared against a
                // float32 constant; the summation order is the shipped one.
                double speedSquared = (double)velocity.z * velocity.z
                                    + (double)velocity.y * velocity.y;
                speedSquared += (double)velocity.x * velocity.x;
                if (UEInterpreter.ToInt((pawn.GetProp(PK.Region) as UEObject)?.GetProp(PK.ZoneNumber)) == 0
                    || count > 35
                    || speedSquared > 2500000f)
                {
                    JumpFarMove(interp, pawn, entry);
                    done = true;
                }
            }
        }

        // Every zone that shipped dereferences UNCONDITIONALLY is read only
        // through this. A missing zone must not silently mean "not water" /
        // "no pain": a missing Region would change the trajectory and the gate
        // verdict without any signal. Applies to the start of jumpLanding, the
        // reads AFTER the move, and both zones of the FindBestJump gate.
        UEObject ZoneOrStop(UEObject actor, string region, string where)
            => ZoneOf(actor, region) ?? throw new NotSupportedException(
                $"{where}: {region}.Zone is missing on {actor?.GetProp(PK.Name)}. "
                + "Shipped dereferences this zone unconditionally, so such a "
                + "state never occurs in the original, and \"no zone\" must not be "
                + "treated as a negative answer");

        // The two world calls jumpLanding makes, in one place each, so the
        // recorder cannot disagree with what was actually asked.
        ReachHit JumpMove(UEInterpreter interp, UEObject pawn, Vector3 delta, MoveCheck rec)
        {
            ReachHit hit = ReachMoveActor(interp, pawn, delta, rec);
            RecordJumpStep("move", delta, hit, pawn);
            return hit;
        }

        void JumpFarMove(UEInterpreter interp, UEObject pawn, Vector3 dest)
        {
            FarMove(interp, pawn, dest, bTest: true, bNoCheck: true);
            RecordJumpStep("farmove", dest,
                new ReachHit(1f, Vector3.zero, null), pawn);
        }

        // APawn::FindBestJump (99425-99479). Suggests a velocity, flies it, and
        // accepts the jump only when it closed real distance AND the pawn can
        // survive where it landed.
        //
        // The incoming Velocity argument is NOT the jump velocity: shipped
        // overwrites its Z with the pawn's JumpZ (0x103de5a3) and hands the whole
        // buffer to SuggestJumpVelocity, which overwrites X and Y too. Nothing
        // the caller put in it survives. walkReachable still computes one
        // (SafeNormal(flat) * GroundSpeed) and passes it, so the port keeps the
        // parameter and documents that it is dead rather than silently dropping
        // an argument the call site provides.
        internal int FindBestJump(UEInterpreter interp, UEObject self, Vector3 dest, Vector3 velocity,
            bool bRestoreToEntry)
        {
            Vector3 entry = UEInterpreter.AsVector3(self.GetProp(PK.Location));
            float seed = UEInterpreter.AsFloat(self.GetProp(PK.JumpZ));

            Vector3 suggested = SuggestJumpVelocity(self, dest, seed);
            // Flag 1: jumpLanding must NOT restore. The restore below is this
            // body's own, and it runs on a different condition.
            JumpLanding(interp, self, suggested, bRestoreToEntry: false);

            int result = 0;
            // Both gates read the zones the pawn ended up in, and they read two
            // DIFFERENT fields: pain from FootRegion, water from Region. The
            // shipped order is gate first, distances second.
            // Read eagerly where shipped short-circuits. Every one of the four is
            // a dictionary lookup with no side effect, so the ledger (C4) is
            // unchanged; the recorder needs all four to replay the gate.
            UEObject footZone = ZoneOrStop(self, "FootRegion", "FindBestJump gate");
            bool pain = UEInterpreter.ToBool(footZone.GetProp(PK.bPainZone));
            bool sameDamage = SameDamageType(footZone, self);
            bool canSwim = UEInterpreter.ToBool(self.GetProp(PK.bCanSwim));
            bool water = UEInterpreter.ToBool(
                ZoneOrStop(self, "Region", "FindBestJump gate").GetProp(PK.bWaterZone));
            JumpTraceForTest?.Add(JumpTraceStep.Gate(pain, sameDamage, canSwim, water));
            if ((!pain || sameDamage) && (canSwim || !water))
            {
                // Distances are 3D. The BEFORE one is rounded to float32 and
                // widened back (the decompile's `param_7 = (float)dVar9`), the
                // AFTER one stays double; the asymmetry is shipped.
                float before = (float)Math.Sqrt(Squared3(dest - entry));
                double after = Math.Sqrt(Squared3(
                    dest - UEInterpreter.AsVector3(self.GetProp(PK.Location))));
                // Strictly more than 8 units of progress, or the jump is refused.
                if ((double)before - after > 8.0) result = 1;
            }

            // Through the same single node jumpLanding uses, so the transcript
            // covers this restore too — it is a world call the shipped body
            // makes, and leaving it out of the record made the oracle see one
            // fewer request than the machine code issues.
            if (bRestoreToEntry) JumpFarMove(interp, self, entry);
            return result;
        }

        // The squared length appSqrt is fed: float32 components, wide products
        // and sums, in the shipped order (dz², dy², dx²).
        static double Squared3(Vector3 v)
            => (double)v.z * v.z + (double)v.y * v.y + (double)v.x * v.x;
    }
}
