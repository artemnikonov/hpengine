// PAWN TRAJECTORY SIMULATOR — port of Engine.dll 0x103e6310 (decompile Engine.dll.c:290342).
//
// Called from physWalking, bAutoJump branch (sites 0x103e96f2 / 0x103e977a): "where will
// the pawn land with this velocity". Up to 10 steps of min(remaining, 0.2) s;
// zone gravity minus its component along the normal of the LAST hit; each step
// traces a cylinder (SingleLineCheck, flags 7).
//
// Signature taken from the DISASM: Ghidra drops the 10th argument (maxTime) — at both
// sites it is `push 0x40000000` = 2.0 s (V11).
//
// Operation order and float rounding points follow the x87 disasm (values
// stored with fstp dword are rounded; those kept on the stack are not).
//
// Oracle: se_harness/bsp_diff/traj_oracle.py (the real body under unicorn, the world
// is an answer tape), validator validate_trajectory.py.
using System;
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        public struct TrajectoryHit
        {
            public Vector3 Location, Normal;
            public float Time;
        }

        // trace(start, end, extent) -> (Time, Normal): answer of ULevel::SingleLineCheck with
        // flags 7; surfaceFlags(extentMax) -> PolyFlags of the hit surface, or null
        // (Engine.dll 0x103ff1b0: FCheckResult -> FBspSurf* via FindCoplanarSurface).
        // Returns: true — time/steps exhausted; false — landed or snagged.
        public static bool SimulateTrajectory(Vector3 start, Vector3 vel, Vector3 extent, float maxTime,
            Vector3 zoneGravity, UERotator rotation, float maxMountHeight,
            Func<Vector3, Vector3, Vector3, (float Time, Vector3 Normal)> trace,
            Func<float, int?> surfaceFlags, out TrajectoryHit hit)
        {
            hit = default;
            float t = 0f;
            Vector3 n = Vector3.zero;
            for (int iter = 0; iter < 10; iter++)
            {
                if (!(t < maxTime)) break;
                float rem = maxTime - t;
                float dt = 0.2f > rem ? rem : 0.2f;

                double d = ((double)zoneGravity.z * n.z + (double)zoneGravity.y * n.y) + (double)zoneGravity.x * n.x;
                float cx = (float)(d * n.x), cy = (float)(d * n.y);
                double cz = d * n.z;
                float gx = zoneGravity.x - cx, gy = zoneGravity.y - cy, gz = (float)(zoneGravity.z - cz);
                double h = (double)dt * dt * 0.5;
                float ax = (float)(h * gx), ay = (float)(gy * h), az = (float)(gz * h);
                float mx = vel.x * dt, my = vel.y * dt, mz = vel.z * dt;
                float px = mx + start.x, py = start.y + my, pz = start.z + mz;
                Vector3 end = new Vector3(px + ax, py + ay, pz + az);

                (float time, Vector3 normal) = trace(start, end, extent);
                n = normal;
                double dtEff = (double)dt * time;
                t = (float)(dtEff + t);
                vel = new Vector3(vel.x + (float)(gx * dtEff), vel.y + (float)(gy * dtEff), vel.z + (float)(gz * dtEff));

                Vector3 pos;
                if (!(time < 1f)) { n = Vector3.zero; pos = end; }
                else
                {
                    double om = 1.0 - time;
                    pos = new Vector3((float)((double)end.x * time) + (float)(om * start.x),
                                      (float)((double)end.y * time) + (float)(start.y * om),
                                      (float)((double)end.z * time) + (float)(start.z * om));
                    if (n.z > 0.7f)
                    {
                        hit.Location = pos; hit.Normal = n; hit.Time = (float)((double)t / maxTime);
                        return false;
                    }
                    if (maxMountHeight > 0f && (double)n.z > -0.1)
                    {
                        Vector3 xAxis = UEValue.RotateVector(new Vector3(1f, 0f, 0f), rotation);
                        double facing = ((double)xAxis.z * n.z + (double)xAxis.y * n.y) + (double)xAxis.x * n.x;
                        if (facing < 0.0)
                        {
                            float ez = Math.Abs(extent.z), ey = Math.Abs(extent.y), ex = Math.Abs(extent.x);
                            float m = ex < ey ? ey : ex;
                            if (m < ez) m = ez;
                            int? flags = surfaceFlags(m);
                            if (flags is int pf && (pf & 0x1000) != 0)
                            {
                                hit.Location = pos; hit.Normal = n; hit.Time = (float)((double)t / maxTime);
                                return false;
                            }
                        }
                    }
                    double along = ((double)vel.y * n.y + (double)vel.z * n.z) + (double)vel.x * n.x;
                    vel = new Vector3(vel.x - (float)(along * n.x), vel.y - (float)(along * n.y), vel.z - (float)(along * n.z));
                }
                start = pos;
            }
            hit.Location = start; hit.Normal = n; hit.Time = 1f;
            return true;
        }

        // ── bAutoJump BRANCH of APawn::physWalking (Engine.dll.c:136196-136305; disasm
        //    0x103e9293-0x103e9854) ───────────────────────────────────────────────
        //
        // A walking player with bAutoJump ("Auto Jump" checkbox in the options menu ->
        // PlayerPawn.AutoJump) at a drop: (1) a point trace down by (MaxStep+2) from
        // foot level — no floor; (2) a trace BACK under the feet by 1.5·R against AccelDir,
        // 4 units below the feet — finds the ledge face looking forward (dot > 0.25);
        // (3) two 2 s trajectory simulations: just fall from a point past the edge, and
        // jump from the edge (Velocity.Z + JumpZ); the jump is chosen if it lands more
        // than 10 units higher. Then the pawn is moved to the edge (MoveActor),
        // the DoJump(1.0) event is called, and the unused share of the substep (Time of
        // the second trace · sub) returns to the remaining time.
        public enum AutoJumpExit { Continue, Stationary, Jump }

        public struct AutoJumpResult
        {
            public AutoJumpExit Exit;
            public Vector3 MoveDelta;     // MoveActor argument on Jump
            public float Remaining;
        }

        // trace(start, end, extent) -> (Clear, Time, Normal); clear = return of
        // SingleLineCheck (1 — nothing hit).
        public static AutoJumpResult AutoJumpGate(bool isPlayer, bool autoJump, bool stationary,
            Vector3 location, Vector3 velocity, UERotator rotation, float radius, float height, Vector3 cylExtent, float jumpZ,
            float maxMountHeight, Vector3 zoneGravity, Vector3 accelDir, Vector3 down, float sub, float remaining,
            Func<Vector3, Vector3, Vector3, (bool Clear, float Time, Vector3 Normal)> trace,
            Func<float, int?> surfaceFlags)
        {
            var res = new AutoJumpResult { Exit = AutoJumpExit.Continue, Remaining = remaining };
            if (!isPlayer || !autoJump) { if (stationary) res.Exit = AutoJumpExit.Stationary; return res; }
            float negCh = -height;
            float low = negCh - 4f;
            if (stationary) { res.Exit = AutoJumpExit.Stationary; return res; }

            Vector3 end1 = new Vector3(down.x + location.x, down.y + location.y, (float)((double)down.z + ((double)negCh + location.z)));
            if (!trace(location, end1, Vector3.zero).Clear) return res;

            double r15 = (double)radius * 1.5;
            Vector3 back = new Vector3((float)(location.x - r15 * accelDir.x), location.y - (float)(accelDir.y * r15),
                                       (float)(location.z - accelDir.z * r15));
            Vector3 start2 = new Vector3(location.x, location.y, low + location.z);
            Vector3 end2 = new Vector3(back.x, back.y, low + back.z);
            var t2 = trace(start2, end2, Vector3.zero);
            if (t2.Clear) return res;
            float dot = (float)(((double)accelDir.z * t2.Normal.z + (double)accelDir.y * t2.Normal.y) + (double)t2.Normal.x * accelDir.x);
            if (!(0.25f < dot)) return res;

            float time = t2.Time;
            double om = 1.0 - time;
            Vector3 edge = new Vector3((float)(om * location.x) + time * back.x, (float)(om * location.y) + time * back.y,
                                       (float)(om * location.z) + time * back.z);
            double k = ((double)radius * 1.5) / dot;
            Vector3 beyond = new Vector3(edge.x + (float)(k * accelDir.x), (float)(accelDir.y * k) + edge.y, (float)(accelDir.z * k) + edge.z);
            // Trajectory extent — uStack_9c of physWalking (GetCylinderExtent,
            // Engine.dll.c:135353), passed to FUN_103e6310 by pointer
            // (136283/136291); radius and height above are the raw fields 0x1cc/0x1d4.
            Vector3 extent = cylExtent;
            Func<Vector3, Vector3, Vector3, (float, Vector3)> simTrace = (s, e, x) => { var r = trace(s, e, x); return (r.Time, r.Normal); };
            SimulateTrajectory(beyond, velocity, extent, 2f, zoneGravity, rotation, maxMountHeight, simTrace, surfaceFlags, out TrajectoryHit fall);
            SimulateTrajectory(edge, new Vector3(velocity.x, velocity.y, jumpZ + velocity.z), extent, 2f, zoneGravity, rotation,
                maxMountHeight, simTrace, surfaceFlags, out TrajectoryHit jump);
            if (!(10f < jump.Location.z - fall.Location.z)) return res;

            res.Exit = AutoJumpExit.Jump;
            res.MoveDelta = new Vector3(edge.x - location.x, edge.y - location.y, edge.z - location.z);
            res.Remaining = (float)((double)time * sub + remaining);
            return res;
        }
    }
}
