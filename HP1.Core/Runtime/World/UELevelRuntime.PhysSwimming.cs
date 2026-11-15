// PHYS_Swimming: APawn::physSwimming (0x103f20a0), APawn::Swim (0x103f1c10), APawn::startSwimming (0x103f0ea0).
// Written from the disassembly (disasm/Engine.asm); the decompile (Engine.dll.c 294600-295360) loses the
// Iterations increment and the register/float split. Game logic runs at PC64: every register operation is
// X87Ext.Round64, every `fstp dword` is ToFloat.
using System;
using System.IO;
using X87Ext = HP1.Core.X87Ext;
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        static X87Ext SwX(float v) => X87Ext.From(v);
        static X87Ext SwR(X87Ext v) => X87Ext.Round64(v);
        // fcomp + `test ah, 0x40; je`: C3 is set by equal AND by unordered.
        static bool SwEq(float a, float b) => (X87Ext.Fcom(SwX(a), SwX(b)) & X87Ext.C3) != 0;
        // fstp dword of a register result.
        static float SwF(X87Ext v) => X87Ext.Round64(v).ToFloat();
        static bool SwVecEq(Vector3 a, Vector3 b) => SwEq(a.x, b.x) && SwEq(a.y, b.y) && SwEq(a.z, b.z);
        // appSqrt of a register sum: fstp qword, fsqrt (kept in the register).
        static X87Ext SwSqrt(X87Ext sum) => X87Ext.Sqrt64(X87Ext.From(sum.ToDouble()));
        // (dx·dx + dy·dy) + dz·dz of register differences a − b (startSwimming 0x103f0f3c.., Swim 0x103f1d1f..).
        static X87Ext SwDistSq(Vector3 a, Vector3 b)
        {
            X87Ext dx = SwR(SwX(a.x) - SwX(b.x)), dy = SwR(SwX(a.y) - SwX(b.y)), dz = SwR(SwX(a.z) - SwX(b.z));
            return SwR(SwR(SwR(dx * dx) + SwR(dy * dy)) + SwR(dz * dz));
        }
        static bool SwWater(UEObject actor, string region)
            => UEInterpreter.ToBool(((actor.GetProp(region) as UEObject)?.GetProp(PK.Zone) as UEObject)?.GetProp(PK.bWaterZone));
        void SwMove(UEInterpreter interp, UEObject actor, Vector3 delta, MoveCheck hit)
            => MoveActor(interp, actor, delta, UEInterpreter.AsRotator(actor.GetProp(PK.Rotation)),
                bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false, hit);

        // APawn::Swim 0x103f1c10: move; if the move left the water (Region.Zone read after it), back to the water line
        // and return the fraction of Delta that was outside: float(|end − Loc|) / |Delta| (|Delta| summed (y² + z²) + x²).
        float Swim(UEInterpreter interp, UEObject actor, Vector3 delta, MoveCheck hit)
        {
            Vector3 start = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
            SwMove(interp, actor, delta, hit);
            if (SwWater(actor, "Region")) return 0f;
            Vector3 loc = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
            Vector3 end = loc;
            FindWaterLine(actor, start, ref end);
            if (SwVecEq(end, loc)) return 0f;
            float outside = SwSqrt(SwDistSq(end, loc)).ToFloat();
            X87Ext size = SwSqrt(SwR(SwR(SwR(SwX(delta.y) * SwX(delta.y)) + SwR(SwX(delta.z) * SwX(delta.z)))
                + SwR(SwX(delta.x) * SwX(delta.x))));
            float ret = X87Ext.Div64(SwX(outside), size).ToFloat();
            SwMove(interp, actor, new Vector3(SwF(SwX(end.x) - SwX(loc.x)), SwF(SwX(end.y) - SwX(loc.y)),
                SwF(SwX(end.z) - SwX(loc.z))), hit);
            return ret;
        }

        // APawn::startSwimming 0x103f0ea0 (callers: physWalking 0x103e927b/0x103e9ed5, physFalling 0x103efede).
        void StartSwimming(UEInterpreter interp, UEObject actor, Vector3 oldVelocity, float timeTick, float remaining,
            int iterations)
        {
            if (DetailedJournal || ReferenceEquals(actor, LocalPlayer))
                Journal?.Invoke($"swim_start actor={actor.GetProp(PK.Name)} location={actor.GetProp(PK.Location)} remaining={remaining:R}");
            Vector3 loc = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
            Vector3 oldLoc = UEInterpreter.AsVector3(actor.GetProp(PK.OldLocation));
            Vector3 end = loc;
            FindWaterLine(actor, oldLoc, ref end);
            float waterTime = 0f;
            if (!SwVecEq(end, loc))
            {
                waterTime = SwSqrt(SwDistSq(end, loc)).ToFloat();
                X87Ext travelled = SwSqrt(SwDistSq(loc, oldLoc));
                waterTime = X87Ext.Div64(SwR(SwX(waterTime) * SwX(timeTick)), travelled).ToFloat();
                remaining = SwR(SwX(waterTime) + SwX(remaining)).ToFloat();
                SwMove(interp, actor, new Vector3(SwF(SwX(end.x) - SwX(loc.x)), SwF(SwX(end.y) - SwX(loc.y)),
                    SwF(SwX(end.z) - SwX(loc.z))), new MoveCheck());
            }
            // 0x103f1070: `test [+0x1f8], 0x11` — bBounce (0x1) and bJustTeleported (0x10).
            if (!UEInterpreter.ToBool(actor.GetProp(PK.bBounce)) && !UEInterpreter.ToBool(actor.GetProp(PK.bJustTeleported)))
            {
                loc = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
                oldLoc = UEInterpreter.AsVector3(actor.GetProp(PK.OldLocation));
                // (Location − OldLocation) stored as float, times 1/(timeTick − waterTime) kept in the register.
                X87Ext inv = X87Ext.Div64(SwX(1f), SwR(SwX(timeTick) - SwX(waterTime)));
                float vx = SwR(SwX(SwF(SwX(loc.x) - SwX(oldLoc.x))) * inv).ToFloat();
                float vy = SwR(SwX(SwF(SwX(loc.y) - SwX(oldLoc.y))) * inv).ToFloat();
                float vz = SwR(SwX(SwF(SwX(loc.z) - SwX(oldLoc.z))) * inv).ToFloat();
                // 2·V (fadd st, st: float), then − OldVelocity, per component.
                var v = new Vector3(SwF(SwX(vx + vx) - SwX(oldVelocity.x)), SwF(SwX(vy + vy) - SwX(oldVelocity.y)),
                    SwF(SwX(vz + vz) - SwX(oldVelocity.z)));
                // (x² + y²) + z² > 1.6e7 (`test ah, 0x41; jne`): SafeNormal · 4000 (FVector::operator*=).
                if ((X87Ext.Fcom(Vector3.SizeSquared64(v.x, v.y, v.z), SwX(16000000f)) & 0x41) == 0)
                {
                    Vector3 n = v.normalized;
                    v = new Vector3(n.x * 4000f, n.y * 4000f, n.z * 4000f);
                }
                actor.Props["Velocity"] = v;
            }
            Vector3 vel = UEInterpreter.AsVector3(actor.GetProp(PK.Velocity));
            // -160 < Vz < 0: Vz = −80 − Size2D·0.7f (0x103f11ce..0x103f122c).
            if (vel.z > -160f && vel.z < 0f)
            {
                vel.z = SwR(SwX(-80f) - SwR(Vector3.Size64Ext(vel.x, vel.y, 0f) * SwX(0.7f))).ToFloat();
                actor.Props["Velocity"] = vel;
            }
            if (remaining > 0.01f)
                PhysSwimming(interp, actor, remaining, iterations);
        }

        // physFalling's substep velocity in water, for a bobbing Decoration and for a PlayerPawn with wet feet; null =
        // the dry formula (OldVelocity + (G + A)·0.5·dt). The first choice (0x103eef17..0x103ef472) keeps some Z steps
        // in the register; the apex repeat (0x103ef58e..0x103efc05, apex = true, dt = the first part) stores every step
        // except the water factors. Zone and Acceleration are read live.
        Vector3? FallingFluidVelocity(UEObject actor, Vector3 old, float dt, bool apex)
        {
            UEObject zone = (actor.GetProp(PK.Region) as UEObject)?.GetProp(PK.Zone) as UEObject;
            Vector3 g = UEInterpreter.AsVector3(zone?.GetProp(PK.ZoneGravity));
            Vector3 a = UEInterpreter.AsVector3(actor.GetProp(PK.Acceleration));
            X87Ext t = SwX(dt);
            if (UEInterpreter.ToBool(zone?.GetProp(PK.bWaterZone)))
            {
                // 0x103ef264 / 0x103efa09: k = 1 − Buoyancy/(1 < Mass ? Mass : 1) and f = 1 − (dt·Friction)·2 in the
                // register; ((k·G + A)·0.5)·dt + f·Old, every vector step stored as float.
                float mass = UEInterpreter.AsFloat(actor.GetProp(PK.Mass));
                X87Ext m = (X87Ext.Fcom(SwX(1f), SwX(mass)) & X87Ext.C0) != 0 ? SwX(mass) : SwX(1f);
                X87Ext k = SwR(SwX(1f) - X87Ext.Div64(SwX(UEInterpreter.AsFloat(actor.GetProp(PK.Buoyancy))), m));
                X87Ext td = SwR(t * SwX(UEInterpreter.AsFloat(zone.GetProp(PK.ZoneFluidFriction))));
                X87Ext f = SwR(SwX(1f) - SwR(td + td));
                float W(float gc, float ac, float oc)
                    => SwF(SwX(SwF(SwX(SwF(SwX(SwF(SwX(SwF(k * SwX(gc))) + SwX(ac))) * SwX(0.5f))) * t)) + SwX(SwF(f * SwX(oc))));
                return new Vector3(W(g.x, a.x, old.x), W(g.y, a.y, old.y), W(g.z, a.z, old.z));
            }
            // Float chain ((G·s + A)·0.5)·dt + Old (s = 0.5 bobbing, 1 wet feet, v = Old or f·Old).
            float Fl(float gc, float s, float ac, float oc)
                => SwF(SwX(SwF(SwX(SwF(SwX(SwF(SwX(SwF(SwX(gc) * SwX(s))) + SwX(ac))) * SwX(0.5f))) * t)) + SwX(oc));
            if (UEInterpreter.ClassIsA(actor.Class, "Decoration") && UEInterpreter.ToBool(actor.GetDeclared("Decoration", "bBobbing")))
            {
                // 0x103eef5c / 0x103ef5d7: half gravity. Z: the first choice keeps it in the register to the end, the
                // apex stores it after ·0.5.
                X87Ext hz = SwR(SwR(SwR(SwX(g.z) * SwX(0.5f)) + SwX(a.z)) * SwX(0.5f));
                float z = apex ? SwF(SwX(SwF(SwX(SwF(hz)) * t)) + SwX(old.z)) : SwF(SwR(hz * t) + SwX(old.z));
                return new Vector3(Fl(g.x, 0.5f, a.x, old.x), Fl(g.y, 0.5f, a.y, old.y), z);
            }
            UEObject foot = (actor.GetProp(PK.FootRegion) as UEObject)?.GetProp(PK.Zone) as UEObject;
            if (UEInterpreter.ClassIsA(actor.Class, "PlayerPawn") && UEInterpreter.ToBool(foot?.GetProp(PK.bWaterZone))
                && !(old.z >= 0f))
            {
                // 0x103ef06d / 0x103ef736: f = 1 − dt·FootRegion.Zone.ZoneFluidFriction (register), Old·f stored.
                X87Ext f = SwR(SwX(1f) - SwR(t * SwX(UEInterpreter.AsFloat(foot.GetProp(PK.ZoneFluidFriction)))));
                float vx = SwF(f * SwX(old.x)), vy = SwF(f * SwX(old.y)), vz = SwF(f * SwX(old.z));
                float z = apex ? Fl(g.z, 1f, a.z, vz)
                    : SwF(SwX(vz) + SwR(SwR(SwR(SwX(g.z) + SwX(a.z)) * SwX(0.5f)) * t));
                return new Vector3(Fl(g.x, 1f, a.x, vx), Fl(g.y, 1f, a.y, vy), z);
            }
            return null;
        }

        // APawn::physSwimming 0x103f20a0.
        public void PhysSwimming(UEInterpreter interp, UEObject actor, float dt, int iterations)
        {
            UEObject zone = (actor.GetProp(PK.Region) as UEObject)?.GetProp(PK.Zone) as UEObject
                ?? throw new InvalidDataException($"physSwimming missing zone actor={actor.GetProp(PK.Name)}");
            // Head out of water and rising faster than 100: Vz = (1 − dt)·Vz.
            Vector3 vel = UEInterpreter.AsVector3(actor.GetProp(PK.Velocity));
            if (!SwWater(actor, "HeadRegion") && vel.z > 100f)
            {
                vel.z = SwR(SwR(SwX(1f) - SwX(dt)) * SwX(vel.z)).ToFloat();
                actor.Props["Velocity"] = vel;
            }
            iterations++;   // 0x103f210b (missing in the decompile)
            actor.Props["OldLocation"] = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
            actor.Props["bJustTeleported"] = false;
            // Acceleration.IsZero (`fcomp 0; test ah, 0x40`: unordered counts as zero) keeps the raw vector.
            Vector3 accel = UEInterpreter.AsVector3(actor.GetProp(PK.Acceleration));
            Vector3 accelDir = SwEq(accel.x, 0f) && SwEq(accel.y, 0f) && SwEq(accel.z, 0f) ? accel : accel.normalized;
            UEPawnMovement.CalcVelocity(actor, accelDir, dt, UEInterpreter.AsFloat(actor.GetProp(PK.WaterSpeed)),
                UEInterpreter.AsFloat(zone.GetProp(PK.ZoneFluidFriction)), bFluid: true, bBrake: false, bBuoyant: true);
            float velZ = UEInterpreter.AsVector3(actor.GetProp(PK.Velocity)).z;   // [ebp-0x58]

            // Zone flow: PlayerPawn, or ZoneVelocity² > 90000 (ordered). Z·25 stays in the register, X·25 and Y·25 are
            // stored first (0x103f2243..0x103f228c).
            zone = (actor.GetProp(PK.Region) as UEObject)?.GetProp(PK.Zone) as UEObject;
            Vector3 zv = UEInterpreter.AsVector3(zone.GetProp(PK.ZoneVelocity));
            float fx = 0f, fy = 0f, fz = 0f;
            if (UEInterpreter.ClassIsA(actor.Class, "PlayerPawn")
                || (X87Ext.Fcom(Vector3.SizeSquared64(zv.x, zv.y, zv.z), SwX(90000f)) & 0x41) == 0)
            {
                fx = SwR(SwX(SwR(SwX(zv.x) * SwX(25f)).ToFloat()) * SwX(dt)).ToFloat();
                fy = SwR(SwX(SwR(SwX(zv.y) * SwX(25f)).ToFloat()) * SwX(dt)).ToFloat();
                fz = SwR(SwR(SwX(zv.z) * SwX(25f)) * SwX(dt)).ToFloat();
            }
            // Delta = (Velocity + flow)·dt: X and Y sums stored as float, Z sum kept in the register.
            vel = UEInterpreter.AsVector3(actor.GetProp(PK.Velocity));
            var delta = new Vector3(
                SwR(SwX(SwR(SwX(fx) + SwX(vel.x)).ToFloat()) * SwX(dt)).ToFloat(),
                SwR(SwX(SwR(SwX(fy) + SwX(vel.y)).ToFloat()) * SwX(dt)).ToFloat(),
                SwR(SwR(SwX(fz) + SwX(vel.z)) * SwX(dt)).ToFloat());
            var hit = new MoveCheck();
            float remaining = SwR(SwX(Swim(interp, actor, delta, hit)) * SwX(dt)).ToFloat();
            if (!(hit.Time >= 1f))   // `fcomp 1.0; test ah, 1`: below or unordered
            {
                zone = (actor.GetProp(PK.Region) as UEObject)?.GetProp(PK.Zone) as UEObject;
                Vector3 gravDir = new(0f, 0f, UEInterpreter.AsVector3(zone?.GetProp(PK.ZoneGravity)).z > 0f ? 1f : -1f);
                Vector3 desiredDir = delta.normalized;
                Vector3 vn = UEInterpreter.AsVector3(actor.GetProp(PK.Velocity)).normalized;
                // (gx·vx + vz·gz) + gy·vy in the register.
                X87Ext vertical = SwR(SwR(SwR(SwX(gravDir.x) * SwX(vn.x)) + SwR(SwX(vn.z) * SwX(gravDir.z)))
                    + SwR(SwX(gravDir.y) * SwX(vn.y)));
                float nz = hit.Normal.z;
                bool stepUp = !(Math.Abs(nz) >= 0.2f)
                    && (X87Ext.Fcom(vertical, SwX(0.5f)) & X87Ext.C0) != 0
                    && (X87Ext.Fcom(vertical, SwX(-0.2f)) & 0x41) == 0;
                if (stepUp)
                {
                    float savedZ = UEInterpreter.AsVector3(actor.GetProp(PK.Location)).z;
                    float f = SwF(SwX(1f) - SwX(hit.Time));
                    StepUp(interp, actor, gravDir, desiredDir, new Vector3(delta.x * f, delta.y * f, delta.z * f), hit);
                    Vector3 old = UEInterpreter.AsVector3(actor.GetProp(PK.OldLocation));
                    old.z = SwR(SwR(SwX(old.z) - SwX(savedZ)) + SwX(UEInterpreter.AsVector3(actor.GetProp(PK.Location)).z)).ToFloat();
                    actor.Props["OldLocation"] = old;
                }
                else
                {
                    ProcessHitWall(interp, actor, hit.Normal, hit.Actor);
                    Vector3 n = hit.Normal;   // OldHitNormal [ebp-0x90]
                    // 0x103f2500..0x103f25b7: the slide; X stays in the register through the Delta·Adjusted test.
                    X87Ext dot = SwR(SwR(SwR(SwX(delta.y) * SwX(n.y)) + SwR(SwX(n.x) * SwX(delta.x))) + SwR(SwX(delta.z) * SwX(n.z)));
                    float pz = SwR(dot * SwX(n.z)).ToFloat();
                    float py = SwR(SwX(n.y) * dot).ToFloat();
                    X87Ext px = SwR(dot * SwX(n.x));
                    float sz = SwF(SwX(delta.z) - SwX(pz));
                    float sy = SwF(SwX(delta.y) - SwX(py));
                    X87Ext sx = SwR(SwX(delta.x) - px);
                    X87Ext oneMinus = SwR(SwX(1f) - SwX(hit.Time));
                    float az = SwR(SwX(sz) * oneMinus).ToFloat();
                    float ay = SwR(SwX(sy) * oneMinus).ToFloat();
                    X87Ext axR = SwR(sx * oneMinus);
                    var adjusted = new Vector3(axR.ToFloat(), ay, az);
                    X87Ext along = SwR(SwR(SwR(SwX(delta.y) * SwX(ay)) + SwR(SwX(delta.z) * SwX(az))) + SwR(axR * SwX(delta.x)));
                    if ((X87Ext.Fcom(along, SwX(0f)) & X87Ext.C0) == 0)
                    {
                        float r = Swim(interp, actor, adjusted, hit);
                        remaining = SwR(SwR(SwR(SwX(1f) - SwX(hit.Time)) * SwX(r)) * SwX(remaining)).ToFloat();
                        if (!(hit.Time >= 1f))
                        {
                            ProcessHitWall(interp, actor, hit.Normal, hit.Actor);
                            TwoWallAdjust(desiredDir, ref adjusted, hit.Normal, n, hit.Time);
                            r = Swim(interp, actor, adjusted, hit);
                            remaining = SwR(SwR(SwR(SwX(1f) - SwX(hit.Time)) * SwX(r)) * SwX(remaining)).ToFloat();
                        }
                    }
                }
            }

            // Tail 0x103f2675..0x103f2723: Velocity from the displacement over (dt − remaining); a Vz changed by script
            // during the moves (against [ebp-0x58], C3 = equal or unordered) is kept.
            if (!UEInterpreter.ToBool(actor.GetProp(PK.bJustTeleported)) && !(remaining >= dt))
            {
                Vector3 now = UEInterpreter.AsVector3(actor.GetProp(PK.Velocity));
                bool keepZ = !SwEq(velZ, now.z);
                Vector3 loc = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
                Vector3 old = UEInterpreter.AsVector3(actor.GetProp(PK.OldLocation));
                X87Ext dz = SwR(SwX(loc.z) - SwX(old.z));
                float dy = SwF(SwX(loc.y) - SwX(old.y));
                float dx = SwF(SwX(loc.x) - SwX(old.x));
                X87Ext inv = X87Ext.Div64(SwX(1f), SwR(SwX(dt) - SwX(remaining)));
                float invF = inv.ToFloat();
                var v = new Vector3(SwR(SwX(invF) * SwX(dx)).ToFloat(), SwR(SwX(invF) * SwX(dy)).ToFloat(), SwR(dz * inv).ToFloat());
                if (keepZ) v.z = now.z;
                actor.Props["Velocity"] = v;
            }
            if (!SwWater(actor, "Region"))
            {
                if (UEInterpreter.ToInt(actor.GetProp(PK.Physics)) == 3)
                    SetPhysics(interp, actor, 2, null);
                // Leaving the water upward: 0 < Vz < 160 -> Vz = Size2D·0.4f + 40.
                Vector3 v = UEInterpreter.AsVector3(actor.GetProp(PK.Velocity));
                if (!(v.z >= 160f) && v.z > 0f)
                {
                    v.z = SwR(SwR(Vector3.Size64Ext(v.x, v.y, 0f) * SwX(0.4f)) + SwX(40f)).ToFloat();
                    actor.Props["Velocity"] = v;
                }
            }
            if (DetailedJournal || ReferenceEquals(actor, LocalPlayer))
                Journal?.Invoke($"swimming_move actor={actor.GetProp(PK.Name)} to={actor.GetProp(PK.Location)}"
                    + $" velocity={actor.GetProp(PK.Velocity)} physics={actor.GetProp(PK.Physics)} remaining={remaining:R}");
            // Tail 0x103f27ad..0x103f2810 (disassembly): only PHYS_Falling -> physFalling and PHYS_Flying -> physFlying
            // continue the leftover time; any other mode returns (no startNewPhysics in this body).
            if (remaining > 0.01f)
            {
                int phys = UEInterpreter.ToInt(actor.GetProp(PK.Physics));
                if (phys == 2) PhysFalling(interp, actor, remaining, iterations);
                else if (phys == 4) PhysFlying(interp, actor, remaining);
            }
        }
    }
}
