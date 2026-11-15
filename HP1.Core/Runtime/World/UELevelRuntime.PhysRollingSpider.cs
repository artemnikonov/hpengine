// PHYS_Rolling: AActor::physRolling (0x103f3040). PHYS_Spider: APawn::physSpider (0x103f4a00), APawn::findNewFloor
// (0x103f43e0). Written from the disassembly (disasm/Engine.asm); the decompile (Engine.dll.c 158544, 76542, 42691) only
// for orientation. Game logic runs at PC64: every register operation is X87Ext.Round64 (SwR), every `fstp dword` ToFloat
// (SwF); the helpers are PhysSwimming's. Both bodies fire HitWall and Falling through ProcessEvent directly (no
// processHitWall).
using X87Ext = HP1.Core.X87Ext;
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        // `fcomp b; test ah, 1` set: below or unordered.
        static bool RsLt(X87Ext a, X87Ext b) => (X87Ext.Fcom(a, b) & X87Ext.C0) != 0;
        // `fcomp b; test ah, 0x41; je`: above and ordered.
        static bool RsGt(X87Ext a, X87Ext b) => (X87Ext.Fcom(a, b) & 0x41) == 0;
        // `fcomp b; test ah, 0x40` set: equal or unordered.
        static bool RsEq(X87Ext a, X87Ext b) => (X87Ext.Fcom(a, b) & X87Ext.C3) != 0;
        // `fcom 0; (test ah,1; je) fchs; fcomp 0.0001f; test ah, 1`: |v| below 0.0001f or unordered.
        static bool RsTiny(X87Ext v) => RsLt(RsLt(v, SwX(0f)) ? -v : v, SwX(0.0001f));
        static bool RsTiny(Vector3 d) => RsTiny(SwX(d.x)) && RsTiny(SwX(d.y)) && RsTiny(SwX(d.z));
        static UEObject RsZone(UEObject actor)
            => (actor.GetProp(PK.Region) as UEObject)?.GetProp(PK.Zone) as UEObject
               ?? throw new System.IO.InvalidDataException($"physRolling/physSpider missing zone actor={actor.GetProp(PK.Name)}");
        void RsMove(UEInterpreter interp, UEObject actor, Vector3 delta, UERotator rot, MoveCheck hit)
            => MoveActor(interp, actor, delta, rot, bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false, hit);
        UERotator RsRot(UEObject actor, int key) => UEInterpreter.AsRotator(actor.GetProp(key));
        void RsEvent(UEInterpreter interp, UEObject actor, string name)
            => Guard(() => UELifecycle.CallEvent(interp, actor, name), name);
        void RsHitWall(UEInterpreter interp, UEObject actor, MoveCheck hit)
        {
            Vector3 n = hit.Normal; UEObject wall = hit.Actor;
            Guard(() => UELifecycle.CallEvent(interp, actor, "HitWall", n, wall), "HitWall");
        }
        static Vector3 RsLoc(UEObject actor) => UEInterpreter.AsVector3(actor.GetProp(PK.Location));
        static Vector3 RsVel(UEObject actor) => UEInterpreter.AsVector3(actor.GetProp(PK.Velocity));
        static bool RsTeleported(UEObject actor) => UEInterpreter.ToBool(actor.GetProp(PK.bJustTeleported));
        static int RsPhysics(UEObject actor) => UEInterpreter.ToInt(actor.GetProp(PK.Physics));
        // (Location − OldLocation) per component as float, times 1/(dt − remaining) kept in the register;
        // (registerZ, physRolling 0x103f3cc7..) keeps the Z difference in the register and the X/Y products take the float copy of
        // the reciprocal; physSpider 0x103f5955.. stores all three differences.
        static Vector3 RsFallVelocity(UEObject actor, float dt, float remaining, bool registerZ)
        {
            Vector3 l = RsLoc(actor), o = UEInterpreter.AsVector3(actor.GetProp(PK.OldLocation));
            X87Ext inv = X87Ext.Div64(SwX(1f), SwR(SwX(dt) - SwX(remaining)));
            if (registerZ)
            {
                // fst dword [ebp+8]: X and Y take the float copy of 1/(dt − remaining), Z the register.
                float invF = inv.ToFloat();
                return new Vector3(SwF(SwX(invF) * SwX(SwF(SwX(l.x) - SwX(o.x)))),
                    SwF(SwX(invF) * SwX(SwF(SwX(l.y) - SwX(o.y)))), SwF(inv * SwR(SwX(l.z) - SwX(o.z))));
            }
            return new Vector3(SwF(SwX(SwF(SwX(l.x) - SwX(o.x))) * inv), SwF(SwX(SwF(SwX(l.y) - SwX(o.y))) * inv),
                SwF(SwX(SwF(SwX(l.z) - SwX(o.z))) * inv));
        }

        // AActor::physRolling 0x103f3040 (AActor/APawn performPhysics case 7).
        public void PhysRolling(UEInterpreter interp, UEObject actor, float dt, int iterations)
        {
            UEObject zone = RsZone(actor);
            Vector3 vel = RsVel(actor);
            Vector3 dir = vel.normalized;
            Vector3 accDir = UEInterpreter.AsVector3(actor.GetProp(PK.Acceleration)).normalized;
            X87Ext size = Vector3.Size64Ext(vel.x, vel.y, vel.z);
            // 0x103f30e6..0x103f316c: V −= |V|·(dir − accDir)·dt·GroundFriction; Z of the difference in the register,
            // X of the |V| product in the register through ·dt·GF.
            X87Ext t = SwX(dt), gf = SwX(UEInterpreter.AsFloat(zone.GetProp(PK.ZoneGroundFriction)));
            float tdy = SwF(SwX(dir.y) - SwX(accDir.y)), tdx = SwF(SwX(dir.x) - SwX(accDir.x));
            float tz = SwF(SwX(SwF(SwX(SwF(SwR(SwX(dir.z) - SwX(accDir.z)) * size)) * t)) * gf);
            float ty = SwF(SwX(SwF(SwX(SwF(SwX(tdy) * size)) * t)) * gf);
            float tx = SwF(SwR(SwR(size * SwX(tdx)) * t) * gf);
            vel = new Vector3(SwF(SwX(vel.x) - SwX(tx)), SwF(SwX(vel.y) - SwX(ty)), SwF(SwX(vel.z) - SwX(tz)));
            actor.Props["Velocity"] = vel;
            // 0x103f316f..0x103f31d9: V = (1 − dt·FluidFriction)·V + dt·A; dt·A.z in the register.
            Vector3 a = UEInterpreter.AsVector3(actor.GetProp(PK.Acceleration));
            X87Ext k = SwR(SwX(1f) - SwR(t * SwX(UEInterpreter.AsFloat(zone.GetProp(PK.ZoneFluidFriction)))));
            vel = new Vector3(SwF(SwX(SwF(k * SwX(vel.x))) + SwX(SwF(t * SwX(a.x)))),
                SwF(SwX(SwF(k * SwX(vel.y))) + SwX(SwF(t * SwX(a.y)))), SwF(SwR(t * SwX(a.z)) + SwX(SwF(k * SwX(vel.z)))));
            actor.Props["Velocity"] = vel;
            Vector3 zv = UEInterpreter.AsVector3(zone.GetProp(PK.ZoneVelocity));
            var total = new Vector3(SwF(SwX(zv.x) + SwX(vel.x)), SwF(SwX(zv.y) + SwX(vel.y)), SwF(SwX(zv.z) + SwX(vel.z)));
            actor.Props["OldLocation"] = RsLoc(actor);
            actor.Props["bJustTeleported"] = false;
            // 0x103f3263: ZoneGravity.Z > 0 (ordered) -> +16, else -16.
            float gz = UEInterpreter.AsVector3(zone.GetProp(PK.ZoneGravity)).z;
            var down = new Vector3(SwF(SwX(0f) * SwX(16f)), SwF(SwX(0f) * SwX(16f)),
                SwF(SwX(RsGt(SwX(gz), SwX(0f)) ? 1f : -1f) * SwX(16f)));
            var hit = new MoveCheck();
            float remaining = dt;
            int bounces = 0;   // [ebp-0x38]
            while (true)
            {
                if (!RsGt(SwX(remaining), SwX(0f)) || iterations >= 8)
                {
                    // 0x103f3e1d: Velocity = (Location − OldLocation)/dt unless teleported.
                    if (!RsTeleported(actor))
                    {
                        Vector3 l = RsLoc(actor), o = UEInterpreter.AsVector3(actor.GetProp(PK.OldLocation));
                        actor.Props["Velocity"] = DivByFloat(new Vector3(SwF(SwX(l.x) - SwX(o.x)), SwF(SwX(l.y) - SwX(o.y)), SwF(SwX(l.z) - SwX(o.z))), dt);
                    }
                    return;
                }
                iterations++;
                float tick = remaining;
                if (RsGt(SwX(remaining), SwX(0.1f)))
                {
                    X87Ext half = SwR(SwX(remaining) * SwX(0.5f));
                    tick = RsGt(SwX(0.1f), half) ? half.ToFloat() : 0.1f;
                }
                remaining = SwF(SwX(remaining) - SwX(tick));
                X87Ext dxR = SwR(SwX(total.x) * SwX(tick));
                var delta = new Vector3(dxR.ToFloat(), SwF(SwX(total.y) * SwX(tick)), SwF(SwX(total.z) * SwX(tick)));
                Vector3 start = RsLoc(actor);
                // 0x103f33a2: the X test reads the register product, Y and Z the stored floats.
                if (!(RsTiny(dxR) && RsTiny(SwX(delta.y)) && RsTiny(SwX(delta.z))))
                {
                    RsMove(interp, actor, delta, RsRot(actor, PK.Rotation), hit);
                    if (RsLt(SwX(hit.Time), SwX(1f)))
                    {
                        RsHitWall(interp, actor, hit);
                        if (UEInterpreter.ToBool(actor.GetProp(PK.bBounce)))
                        {
                            if (bounces < 2)
                                remaining = SwF(SwR(SwR(SwX(1f) - SwX(hit.Time)) * SwX(tick)) + SwX(remaining));
                            bounces++;
                        }
                        else
                        {
                            // 0x103f3516..0x103f37be: slide along the wall, a second wall through TwoWallAdjust.
                            Vector3 n = hit.Normal;
                            X87Ext dot = SwR(SwR(SwR(SwX(delta.z) * SwX(n.z)) + SwR(SwX(delta.y) * SwX(n.y)))
                                + SwR(SwX(n.x) * SwX(delta.x)));
                            float sx = SwF(SwX(delta.x) - SwX(SwF(SwX(n.x) * dot)));
                            float sy = SwF(SwX(delta.y) - SwX(SwF(SwX(n.y) * dot)));
                            float sz = SwF(SwX(delta.z) - SwX(SwF(dot * SwX(n.z))));
                            X87Ext om = SwR(SwX(1f) - SwX(hit.Time));
                            var adj = new Vector3(SwF(SwX(sx) * om), SwF(SwX(sy) * om), SwF(SwX(sz) * om));
                            X87Ext along = SwR(SwR(SwR(SwX(adj.z) * SwX(delta.z)) + SwR(SwX(adj.y) * SwX(delta.y)))
                                + SwR(SwX(delta.x) * SwX(adj.x)));
                            if (!RsLt(along, SwX(0f)))
                            {
                                RsMove(interp, actor, adj, RsRot(actor, PK.Rotation), hit);
                                if (RsLt(SwX(hit.Time), SwX(1f)))
                                {
                                    RsHitWall(interp, actor, hit);
                                    TwoWallAdjust(total.normalized, ref adj, hit.Normal, n, hit.Time);
                                    RsMove(interp, actor, adj, RsRot(actor, PK.Rotation), hit);
                                }
                            }
                        }
                    }
                }
                // 0x103f37c4: the floor probe 16 uu along gravity.
                RsMove(interp, actor, down, RsRot(actor, PK.Rotation), hit);
                float downTime = hit.Time, floorZ = hit.Normal.z;
                zone = RsZone(actor);
                float groundF = UEInterpreter.AsFloat(zone.GetProp(PK.ZoneGroundFriction));
                if (RsLt(SwX(downTime), SwX(1f)) && RsLt(SwX(floorZ), SwX(1f))
                    && RsLt(SwR(SwX(floorZ) * SwX(groundF)), SwX(3.3f)))
                {
                    // 0x103f386f..0x103f3a8d: on a slope, slide by gravity·dt²/(2·max(GF, 0.5)) along the floor.
                    X87Ext g = RsLt(SwX(0.5f), SwX(groundF)) ? SwX(groundF) : SwX(0.5f);
                    Vector3 grav = UEInterpreter.AsVector3(zone.GetProp(PK.ZoneGravity));
                    float gxf = SwF(t * SwX(grav.x)), gyf = SwF(t * SwX(grav.y)), gzf = SwF(t * SwX(grav.z));
                    X87Ext inv = X87Ext.Div64(SwX(1f), SwR(g + g));
                    var s = new Vector3(SwF(SwX(SwF(inv * SwX(gxf))) * t), SwF(SwX(SwF(inv * SwX(gyf))) * t),
                        SwF(SwX(SwF(inv * SwX(gzf))) * t));
                    Vector3 n = hit.Normal;
                    X87Ext dot = SwR(SwR(SwR(SwX(s.z) * SwX(n.z)) + SwR(SwX(s.y) * SwX(n.y))) + SwR(SwX(s.x) * SwX(n.x)));
                    var slide = new Vector3(SwF(SwX(s.x) - SwX(SwF(dot * SwX(n.x)))), SwF(SwX(s.y) - SwX(SwF(SwX(n.y) * dot))),
                        SwF(SwX(s.z) - SwX(SwF(dot * SwX(n.z)))));
                    X87Ext along = SwR(SwR(SwR(SwX(slide.z) * SwX(s.z)) + SwR(SwX(slide.y) * SwX(s.y)))
                        + SwR(SwX(s.x) * SwX(slide.x)));
                    if (!RsLt(along, SwX(0f)))
                    {
                        RsMove(interp, actor, slide, RsRot(actor, PK.Rotation), hit);
                        if (RsLt(SwX(floorZ), SwX(hit.Normal.z))) floorZ = hit.Normal.z;
                    }
                }
                if (!RsEq(SwX(downTime), SwX(1f)) && !RsLt(SwX(floorZ), SwX(0.7f)))
                {
                    if (!ReferenceEquals(hit.Actor, actor.GetProp(PK.Base) as UEObject))
                        SetBase(interp, actor, hit.Actor, true);
                    continue;
                }
                // 0x103f3ad3: no floor (or too steep): back up the probe, give back the unmoved part of the tick, fall.
                var probe = new Vector3(SwF(SwX(down.x) * SwX(downTime)), SwF(SwX(down.y) * SwX(downTime)),
                    SwF(SwX(down.z) * SwX(downTime)));
                RsMove(interp, actor, new Vector3(SwF(SwX(probe.x) * SwX(-1f)), SwF(SwX(probe.y) * SwX(-1f)),
                    SwF(SwX(probe.z) * SwX(-1f))), RsRot(actor, PK.Rotation), hit);
                float deltaSize = Vector3.Size64(delta.x, delta.y, delta.z);
                Vector3 now = RsLoc(actor);
                float mx = SwF(SwX(now.x) - SwX(start.x)), my = SwF(SwX(now.y) - SwX(start.y));
                X87Ext ratio = X87Ext.Div64(SwSqrt(SwR(SwR(SwX(my) * SwX(my)) + SwR(SwX(mx) * SwX(mx)))), SwX(deltaSize));
                if (!RsGt(SwX(1f), ratio)) ratio = SwX(1f);
                remaining = SwF(SwR(SwR(SwX(1f) - ratio) * SwX(tick)) + SwX(remaining));
                RsEvent(interp, actor, "Falling");
                if (RsPhysics(actor) == 7) SetPhysics(interp, actor, 2, null);
                if (RsPhysics(actor) == 2)
                {
                    if (!RsTeleported(actor) && RsGt(SwX(dt), SwX(remaining)))
                        actor.Props["Velocity"] = RsFallVelocity(actor, dt, remaining, registerZ: true);
                    Vector3 v = RsVel(actor);
                    v.z = 0f;
                    actor.Props["Velocity"] = v;
                    if (RsGt(SwX(remaining), SwX(0.005f))) PhysFalling(interp, actor, remaining, iterations);
                    return;
                }
                RsMove(interp, actor, new Vector3(SwF(SwX(total.x) * SwX(remaining)), SwF(SwX(total.y) * SwX(remaining)),
                    SwF(SwX(total.z) * SwX(remaining))), RsRot(actor, PK.Rotation), hit);
            }
        }

        // APawn::findNewFloor 0x103f43e0: five probes MaxStepHeight away (−Z, −Y, +Y, −X, +X, each End = Location −
        // MaxStepHeight·Dir, TraceFlags 6, no source actor); the first hit becomes Floor. None: Falling, PHYS_Spider ->
        // PHYS_Falling, and a falling pawn continues physFalling with the remaining time.
        static readonly Vector3[] FloorProbeDirs =
            { new(0f, 0f, 1f), new(0f, 1f, 0f), new(0f, -1f, 0f), new(1f, 0f, 0f), new(-1f, 0f, 0f) };
        bool FindNewFloor(UEInterpreter interp, UEObject actor, Vector3 oldLocation, float deltaTime, float remaining,
            int iterations)
        {
            var hit = new MoveCheck();
            foreach (Vector3 d in FloorProbeDirs)
            {
                Vector3 ext = GetCylinderExtent(actor);
                float msh = UEInterpreter.AsFloat(actor.GetProp(PK.MaxStepHeight));
                Vector3 l = RsLoc(actor);
                var end = new Vector3(SwF(SwX(l.x) - SwX(SwF(SwX(msh) * SwX(d.x)))), SwF(SwX(l.y) - SwX(SwF(SwX(msh) * SwX(d.y)))),
                    SwF(SwX(l.z) - SwX(SwF(SwX(msh) * SwX(d.z)))));
                hit.FromTrace(SingleLineCheck(interp, null, null, l, end, ext, 6));
                if (RsLt(SwX(hit.Time), SwX(1f)))
                {
                    actor.Props["Floor"] = hit.Normal;
                    return true;
                }
            }
            RsEvent(interp, actor, "Falling");
            if (RsPhysics(actor) == 10) SetPhysics(interp, actor, 2, null);
            if (RsPhysics(actor) == 2)
            {
                float vz = RsVel(actor).z;
                if (!RsTeleported(actor) && RsGt(SwX(deltaTime), SwX(remaining)))
                {
                    Vector3 l = RsLoc(actor);
                    actor.Props["Velocity"] = DivByFloat(new Vector3(SwF(SwX(l.x) - SwX(oldLocation.x)),
                        SwF(SwX(l.y) - SwX(oldLocation.y)), SwF(SwX(l.z) - SwX(oldLocation.z))), SwF(SwX(deltaTime) - SwX(remaining)));
                }
                Vector3 v = RsVel(actor);
                v.z = vz;
                actor.Props["Velocity"] = v;
                if (RsGt(SwX(remaining), SwX(0.005f))) PhysFalling(interp, actor, remaining, iterations);
            }
            return false;
        }

        // −Floor·(MaxStepHeight + 4): the floor stick move (the sum in the register).
        static Vector3 SpiderFloorDown(UEObject actor, out Vector3 negFloor)
        {
            Vector3 f = UEInterpreter.AsVector3(actor.GetProp(PK.Floor));
            negFloor = new Vector3(SwF(SwX(f.x) * SwX(-1f)), SwF(SwX(f.y) * SwX(-1f)), SwF(SwX(f.z) * SwX(-1f)));
            X87Ext m = SwR(SwX(UEInterpreter.AsFloat(actor.GetProp(PK.MaxStepHeight))) + SwX(4f));
            return new Vector3(SwF(SwX(negFloor.x) * m), SwF(SwX(negFloor.y) * m), SwF(SwX(negFloor.z) * m));
        }

        // APawn::physSpider 0x103f4a00 (APawn::performPhysics case 10).
        public void PhysSpider(UEInterpreter interp, UEObject actor, float dt, int iterations)
        {
            UEObject zone = RsZone(actor);
            X87Ext t = SwX(dt);
            Vector3 a = UEInterpreter.AsVector3(actor.GetProp(PK.Acceleration));
            Vector3 v0 = RsVel(actor);
            Vector3 accDir;   // [ebp-0x3c]
            if (RsEq(SwX(a.x), SwX(0f)) && RsEq(SwX(a.y), SwX(0f)) && RsEq(SwX(a.z), SwX(0f)))
            {
                // 0x103f4a72: V −= 2V·dt·GF (Z in the register); reversed -> V = Acceleration.
                accDir = a;
                X87Ext gf = SwX(UEInterpreter.AsFloat(zone.GetProp(PK.ZoneGroundFriction)));
                float y = SwF(SwX(SwF(SwX(SwF(SwX(v0.y) + SwX(v0.y))) * t)) * gf);
                float x = SwF(SwX(SwF(SwX(SwF(SwX(v0.x) + SwX(v0.x))) * t)) * gf);
                X87Ext z = SwR(SwR(SwR(SwX(v0.z) + SwX(v0.z)) * t) * gf);
                var v = new Vector3(SwF(SwX(v0.x) - SwX(x)), SwF(SwX(v0.y) - SwX(y)), SwF(SwX(v0.z) - z));
                actor.Props["Velocity"] = v;
                X87Ext dot = SwR(SwR(SwR(SwX(v0.y) * SwX(v.y)) + SwR(SwX(v0.z) * SwX(v.z))) + SwR(SwX(v0.x) * SwX(v.x)));
                if (RsLt(dot, SwX(0f))) actor.Props["Velocity"] = a;
            }
            else
            {
                // 0x103f4b55: clamp Acceleration to AccelRate, turn V toward it: V −= (V − accDir·|V|)·dt·GF.
                accDir = a.normalized;
                X87Ext size = Vector3.Size64Ext(v0.x, v0.y, v0.z);
                float rate = UEInterpreter.AsFloat(actor.GetProp(PK.AccelRate));
                if (RsLt(SwR(SwX(rate) * SwX(rate)), Vector3.SizeSquared64(a.x, a.y, a.z)))
                    actor.Props["Acceleration"] = new Vector3(SwF(SwX(accDir.x) * SwX(rate)),
                        SwF(SwX(accDir.y) * SwX(rate)), SwF(SwX(accDir.z) * SwX(rate)));
                X87Ext gf = SwX(UEInterpreter.AsFloat(zone.GetProp(PK.ZoneGroundFriction)));
                float y = SwF(SwX(SwF(SwX(SwF(SwX(v0.y) - SwX(SwF(SwX(accDir.y) * size)))) * t)) * gf);
                float x = SwF(SwX(SwF(SwX(SwF(SwX(v0.x) - SwX(SwF(SwX(accDir.x) * size)))) * t)) * gf);
                X87Ext z = SwR(SwR(SwR(SwX(v0.z) - SwR(SwX(accDir.z) * size)) * t) * gf);
                actor.Props["Velocity"] = new Vector3(SwF(SwX(v0.x) - SwX(x)), SwF(SwX(v0.y) - SwX(y)), SwF(SwX(v0.z) - z));
            }
            // 0x103f4cae: V += dt·A (Z in the register), clamped to GroundSpeed·DesiredSpeed.
            a = UEInterpreter.AsVector3(actor.GetProp(PK.Acceleration));
            Vector3 vel = RsVel(actor);
            vel = new Vector3(SwF(SwX(SwF(t * SwX(a.x))) + SwX(vel.x)), SwF(SwX(SwF(t * SwX(a.y))) + SwX(vel.y)),
                SwF(SwR(t * SwX(a.z)) + SwX(vel.z)));
            actor.Props["Velocity"] = vel;
            float maxSpeed = SwF(SwX(UEInterpreter.AsFloat(actor.GetProp(PK.GroundSpeed)))
                * SwX(UEInterpreter.AsFloat(actor.GetProp(PK.DesiredSpeed))));
            iterations++;
            if (RsLt(SwR(SwX(maxSpeed) * SwX(maxSpeed)),
                    SwR(SwR(SwR(SwX(vel.y) * SwX(vel.y)) + SwR(SwX(vel.z) * SwX(vel.z))) + SwR(SwX(vel.x) * SwX(vel.x)))))
            {
                Vector3 n = vel.normalized;
                vel = new Vector3(SwF(SwX(maxSpeed) * SwX(n.x)), SwF(SwX(maxSpeed) * SwX(n.y)), SwF(SwX(maxSpeed) * SwX(n.z)));
                actor.Props["Velocity"] = vel;
            }
            // 0x103f4d67: zone flow only when ZoneVelocity² > 90000 (ordered).
            zone = RsZone(actor);
            Vector3 zv = UEInterpreter.AsVector3(zone.GetProp(PK.ZoneVelocity));
            Vector3 flow = RsGt(Vector3.SizeSquared64(zv.x, zv.y, zv.z), SwX(90000f)) ? zv : Vector3.zero;
            // 0x103f4ddf: |V + flow| with X in the register times its float copy; dir = (V + flow)·(1/|..|), the float
            // copy of the reciprocal for X and Y, the register for Z.
            float tz = SwF(SwX(flow.z) + SwX(vel.z)), ty = SwF(SwX(flow.y) + SwX(vel.y));
            X87Ext txR = SwR(SwX(flow.x) + SwX(vel.x));
            float tx = txR.ToFloat();
            float size2 = SwSqrt(SwR(SwR(SwR(txR * SwX(tx)) + SwR(SwX(ty) * SwX(ty))) + SwR(SwX(tz) * SwX(tz)))).ToFloat();
            X87Ext inv = X87Ext.Div64(SwX(1f), SwX(size2));
            float invF = inv.ToFloat();
            var dir = new Vector3(SwF(SwX(invF) * SwX(tx)), SwF(SwX(invF) * SwX(ty)), SwF(inv * SwX(tz)));
            Vector3 floor = UEInterpreter.AsVector3(actor.GetProp(PK.Floor));
            bool floorZero = RsTiny(floor);
            var hit = new MoveCheck();
            Vector3 floorDown = SpiderFloorDown(actor, out Vector3 negFloor);
            actor.Props["DesiredRotation"] = RsRot(actor, PK.Rotation);
            bool found = false;
            if (!floorZero)
            {
                // 0x103f4fc0: trace to the old floor (no source actor, TraceFlags 6).
                Vector3 l = RsLoc(actor);
                var end = new Vector3(SwF(SwX(floorDown.x) + SwX(l.x)), SwF(SwX(floorDown.y) + SwX(l.y)),
                    SwF(SwX(floorDown.z) + SwX(l.z)));
                hit.FromTrace(SingleLineCheck(interp, null, null, l, end, GetCylinderExtent(actor), 6));
                found = !RsEq(SwX(hit.Time), SwX(1f));
            }
            if (!found)
            {
                if (!FindNewFloor(interp, actor, RsLoc(actor), dt, dt, iterations)) return;
                floorDown = SpiderFloorDown(actor, out negFloor);
            }
            // 0x103f511d: DesiredRotation = Floor.Rotation() with Pitch − 0x4000, Roll 0.
            floor = UEInterpreter.AsVector3(actor.GetProp(PK.Floor));
            UERotator fr = UEValue.RotatorFromVector(floor);
            actor.Props["DesiredRotation"] = new UERotator(unchecked(fr.Pitch + -0x4000), fr.Yaw, 0);
            Vector3 dirOrig = dir;   // [ebp-0x30]
            X87Ext dotAF = SwR(SwR(SwR(SwX(accDir.y) * SwX(floor.y)) + SwR(SwX(accDir.z) * SwX(floor.z)))
                + SwR(SwX(accDir.x) * SwX(floor.x)));
            // 0x103f5181: Floor.Z < 0.6 and accDir·Floor > 0.9 (both against doubles): let go of the floor.
            if (RsLt(SwX(floor.z), X87Ext.From(0.6)) && RsGt(dotAF, X87Ext.From(0.9)))
            {
                actor.Props["Floor"] = Vector3.zero;
                RsEvent(interp, actor, "Falling");
                SetPhysics(interp, actor, 2, null);
                PhysFalling(interp, actor, dt, iterations);
                return;
            }
            // 0x103f51fe: dir projected onto the floor plane and renormalized; one speed |V + flow| along it.
            X87Ext dd = SwR(SwR(SwR(SwX(dir.z) * SwX(floor.z)) + SwR(SwX(dir.y) * SwX(floor.y))) + SwR(SwX(dir.x) * SwX(floor.x)));
            dir = new Vector3(SwF(SwX(dir.x) - SwX(SwF(dd * SwX(floor.x)))), SwF(SwX(dir.y) - SwX(SwF(dd * SwX(floor.y)))),
                SwF(SwX(dir.z) - SwX(SwF(dd * SwX(floor.z))))).normalized;
            actor.Props["OldLocation"] = RsLoc(actor);
            actor.Props["bJustTeleported"] = false;
            float remaining = dt;
            var spiderVel = new Vector3(SwF(SwX(dir.x) * SwX(size2)), SwF(SwX(dir.y) * SwX(size2)), SwF(SwX(dir.z) * SwX(size2)));
            while (RsGt(SwX(remaining), SwX(0f)) && iterations < 8)
            {
                iterations++;
                X87Ext tick = SwX(remaining);
                if (RsGt(SwX(remaining), SwX(0.05f)))
                {
                    tick = SwR(SwX(remaining) * SwX(0.5f));
                    if (!RsGt(SwX(0.05f), tick)) tick = SwX(0.05f);
                }
                remaining = SwF(SwX(remaining) - tick);
                var delta = new Vector3(SwF(SwX(spiderVel.x) * tick), SwF(SwX(spiderVel.y) * tick), SwF(SwX(spiderVel.z) * tick));
                if (!RsTiny(delta))
                {
                    RsMove(interp, actor, delta, RsRot(actor, PK.DesiredRotation), hit);
                    if (RsLt(SwX(hit.Time), SwX(1f)))
                    {
                        Vector3 n = hit.Normal;
                        floor = UEInterpreter.AsVector3(actor.GetProp(PK.Floor));
                        bool facing = !RsLt(SwR(SwR(SwR(SwX(dirOrig.x) * SwX(n.x)) + SwR(SwX(dirOrig.z) * SwX(n.z)))
                            + SwR(SwX(n.y) * SwX(dirOrig.y))), SwX(0f));
                        X87Ext dirFloor = SwR(SwR(SwR(SwX(dirOrig.z) * SwX(floor.z)) + SwR(SwX(dirOrig.y) * SwX(floor.y)))
                            + SwR(SwX(dirOrig.x) * SwX(floor.x)));
                        bool newFloor = false;
                        if (RsLt(SwX(n.z), SwX(0f)))
                        {
                            // 0x103f5873: a ceiling-facing hit.
                            if (!facing) RsHitWall(interp, actor, hit);
                            else if (RsGt(dirFloor, X87Ext.From(0.7)))
                            {
                                RsEvent(interp, actor, "Falling");
                                if (RsPhysics(actor) == 10) SetPhysics(interp, actor, 2, null);
                                if (RsPhysics(actor) == 2)
                                {
                                    float vz = RsVel(actor).z;
                                    if (!RsTeleported(actor) && RsGt(t, SwX(remaining)))
                                        actor.Props["Velocity"] = RsFallVelocity(actor, dt, remaining, registerZ: false);
                                    Vector3 v = RsVel(actor);
                                    v.z = vz;
                                    actor.Props["Velocity"] = v;
                                    if (RsGt(SwX(remaining), SwX(0.005f))) PhysFalling(interp, actor, remaining, iterations);
                                    return;
                                }
                            }
                        }
                        else if (facing || !RsLt(dirFloor, SwX(0f)))
                        {
                            // 0x103f5536: the hit becomes the floor; HitWall only when SafeNormal(N + Floor)·dir > 0.9.
                            Vector3 n2 = new Vector3(SwF(SwX(n.x) + SwX(floor.x)), SwF(SwX(n.y) + SwX(floor.y)),
                                SwF(SwX(n.z) + SwX(floor.z))).normalized;
                            if (RsGt(SwR(SwR(SwR(SwX(n2.x) * SwX(dirOrig.x)) + SwR(SwX(n2.z) * SwX(dirOrig.z)))
                                    + SwR(SwX(n2.y) * SwX(dirOrig.y))), X87Ext.From(0.9)))
                                RsHitWall(interp, actor, hit);
                            newFloor = true;
                        }
                        else
                        {
                            RsHitWall(interp, actor, hit);
                            newFloor = true;
                        }
                        if (newFloor)
                        {
                            actor.Props["Floor"] = hit.Normal;
                            floorDown = SpiderFloorDown(actor, out negFloor);
                        }
                        // 0x103f56bc: stepUp along −Floor with the rest of the substep.
                        X87Ext om = SwR(SwX(1f) - SwX(hit.Time));
                        StepUp(interp, actor, negFloor, delta.normalized,
                            new Vector3(SwF(om * SwX(delta.x)), SwF(SwX(delta.y) * om), SwF(SwX(delta.z) * om)), hit);
                        if (RsPhysics(actor) == 2)
                        {
                            if (RsGt(SwX(remaining), SwX(0.005f))) PhysFalling(interp, actor, remaining, iterations);
                            return;
                        }
                    }
                }
                // 0x103f5758: stick to the floor.
                RsMove(interp, actor, floorDown, RsRot(actor, PK.Rotation), hit);
                if (RsEq(SwX(hit.Time), SwX(1f)))
                {
                    if (!FindNewFloor(interp, actor, UEInterpreter.AsVector3(actor.GetProp(PK.OldLocation)), dt, remaining,
                            iterations)) return;
                    // operator*(float) with MaxStepHeight + 4 stored as float.
                    Vector3 f = UEInterpreter.AsVector3(actor.GetProp(PK.Floor));
                    negFloor = new Vector3(SwF(SwX(f.x) * SwX(-1f)), SwF(SwX(f.y) * SwX(-1f)), SwF(SwX(f.z) * SwX(-1f)));
                    float m = SwF(SwX(UEInterpreter.AsFloat(actor.GetProp(PK.MaxStepHeight))) + SwX(4f));
                    floorDown = new Vector3(SwF(SwX(negFloor.x) * SwX(m)), SwF(SwX(negFloor.y) * SwX(m)), SwF(SwX(negFloor.z) * SwX(m)));
                }
                else
                {
                    actor.Props["Floor"] = hit.Normal;
                    if (!ReferenceEquals(hit.Actor, actor.GetProp(PK.Base) as UEObject)
                        && !UEInterpreter.ClassIsA(hit.Actor.Class, "Pawn"))
                        SetBase(interp, actor, hit.Actor, true);
                }
            }
            if (!RsTeleported(actor))
            {
                Vector3 l = RsLoc(actor), o = UEInterpreter.AsVector3(actor.GetProp(PK.OldLocation));
                actor.Props["Velocity"] = DivByFloat(new Vector3(SwF(SwX(l.x) - SwX(o.x)), SwF(SwX(l.y) - SwX(o.y)), SwF(SwX(l.z) - SwX(o.z))), dt);
            }
        }
    }
}
