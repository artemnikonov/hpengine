// Comparison tests of Core physics against shipped HP1 1.53, no game files needed.
//
// Input is the same functions the actor tick calls (UELevelRuntime.TickActorBody -> PerformPhysics ->
// PhysFalling/PhysFlying/PhysProjectile/PhysMovingBrush; APawn::performPhysics -> DecrementMoveTimer;
// physWalking -> FinishWalkingVelocity; native MoveTo/MoveToward -> SetMoveTimer). Actors have no class
// (empty class registry): pawn/player/decoration branches do not run, the generic actor branch does.
// No geometry: PhysFalling moves through WalkMoveHook (Core fixture seam), the others through the real
// MoveActor without collision.
//
// Expectations follow the shipped body (Engine.dll 1.53 disasm, addresses at each test), computed INDEPENDENTLY
// by an exact x87 model (X87Model.cs: rational fractions, PC64/qword/dword rounding) — not by port code.
// Red mutations — redtest.sh.
using HP1.UEScript;
using System.Collections.Generic;
using Vector3 = HP1.UEScript.UEVector;

static class Program
{
    static int _fail, _pass;
    static readonly Dictionary<string, int> _failsByTest = new();

    static void Check(string test, bool ok, string detail)
    {
        if (ok) { _pass++; return; }
        _fail++;
        _failsByTest[test] = _failsByTest.TryGetValue(test, out int n) ? n + 1 : 1;
        if (_failsByTest[test] <= 3) System.Console.WriteLine($"FAIL {test}: {detail}");
    }

    static uint B(float f) => (uint)System.BitConverter.SingleToInt32Bits(f);
    static bool Same(float a, float b) => B(a) == B(b);
    static bool Same(Vector3 a, Vector3 b) => Same(a.x, b.x) && Same(a.y, b.y) && Same(a.z, b.z);
    static string S(Vector3 v) => $"({v.x:R}, {v.y:R}, {v.z:R}) [{B(v.x):x8} {B(v.y):x8} {B(v.z):x8}]";

    // Deterministic generator (xorshift64*), independent of the runtime version.
    static ulong _rng = 0x9E3779B97F4A7C15UL;
    static double Next()
    {
        _rng ^= _rng >> 12; _rng ^= _rng << 25; _rng ^= _rng >> 27;
        return ((_rng * 2685821657736338717UL) >> 11) * (1.0 / 9007199254740992.0);
    }
    static float U(double lo, double hi) => (float)(lo + (hi - lo) * Next());

    static Q F(float f) => Q.F(f);

    static UEInterpreter _interp;
    static UELevelRuntime _rt;
    static UEObject _zone;

    static UEObject NewActor(Vector3 loc, Vector3 vel)
    {
        var a = new UEObject();
        a.Props["Region"] = UEObject.MakeRegion(_zone, 1, 1);
        a.Props["Location"] = loc;
        a.Props["Velocity"] = vel;
        a.Props["Acceleration"] = Vector3.zero;
        a.Props["bMovable"] = true;
        a.Props["Rotation"] = new UERotator(0, 0, 0);
        return a;
    }

    static int Main()
    {
        HP1.Core.UEClassRegistry.Shared ??= new HP1.Core.UEClassRegistry();
        _interp = new UEInterpreter();
        _rt = new UELevelRuntime();
        // The zone is the world's LevelInfo: SetActorZone without BSP resolves the region to LevelInfo, and
        // an actor whose Region.Zone is LevelInfo stays in that zone without zone-change events.
        _zone = new UEObject();
        _zone.Props["ZoneGravity"] = new Vector3(0f, 0f, -950f);
        _zone.Props["ZoneVelocity"] = Vector3.zero;
        _zone.Props["ZoneTerminalVelocity"] = 1e6f;
        _zone.Props["ZoneFluidFriction"] = 0f;
        _interp.RestoreWorldLevelInfo(_zone);

        ModelSelfCheck();
        SetMoveTimer();
        DecrementMoveTimer();
        FinishWalkingVelocity();
        WalkingMoveVelocity();
        ProjectileTail();
        FlyingTail();
        FallingLandedVelocity();
        FallingLandedThreshold();
        FallingBounceRemaining();
        MovingBrushVelocity();
        MovingBrushZeroRate();
        InterpolationFinalVelocity();

        foreach (var kv in _failsByTest) if (kv.Value > 3) System.Console.WriteLine($"     {kv.Key}: total failures {kv.Value}");
        System.Console.WriteLine($"AuditPhysicsTests: passed {_pass}, failed {_fail}");
        return _fail == 0 ? 0 : 1;
    }

    // Positive model control: its rounding is checked against Core X87Ext (another implementation of the same
    // semantics, checked in SelfTest x87ext_matches_bigint_model) on random inputs. Test expectations
    // are still computed by the model only.
    static void ModelSelfCheck()
    {
        for (int i = 0; i < 3000; i++)
        {
            float a = U(-5000, 5000), b = U(0.001, 900), c = U(0.001, 9);
            var xa = HP1.Core.X87Ext.From(a); var xb = HP1.Core.X87Ext.From(b); var xc = HP1.Core.X87Ext.From(c);
            float m1 = Q.R64(Q.R64(F(a) / F(b)) + F(c)).F32();
            float x1 = HP1.Core.X87Ext.Round64(HP1.Core.X87Ext.Div64(xa, xb) + xc).ToFloat();
            Check("Model.div-add", Same(m1, x1), $"a={a:R} b={b:R} c={c:R}: model {m1:R}, X87Ext {x1:R}");
            Q qs = Q.R64(F(b) * F(b) + F(c) * F(System.Math.Abs(a)));
            float m2 = Q.Sqrt64(qs).F32();
            var s2 = HP1.Core.X87Ext.Round64(xb * xb + xc * HP1.Core.X87Ext.From(System.Math.Abs(a)));
            float x2 = HP1.Core.X87Ext.Sqrt64(s2).ToFloat();
            Check("Model.sqrt", Same(m2, x2), $"b={b:R} c={c:R} a={a:R}: model {m2:R}, X87Ext {x2:R}");
        }
    }

    // ── APawn::setMoveTimer — disasm 0x103d9689-0x103d96bd: fmul DesiredSpeed; fcom 0.0 (equal -> 0.5);
    // fld dist; fmul 1.3f [0x10479e04]; fdiv st,st(1); fadd 1.0f; fstp — a single rounding to float.
    static void SetMoveTimer()
    {
        int differs = 0;
        for (int i = 0; i < 4000; i++)
        {
            int physics = new[] { 1, 2, 3, 4, 10, 0 }[i % 6];
            float speed = U(40, 900), ds = i % 97 == 0 ? 0f : U(0.05, 1.0), dist = U(0, 4000);
            var p = new UEObject();
            p.Props["Physics"] = (byte)physics;
            p.Props["GroundSpeed"] = speed; p.Props["AirSpeed"] = speed; p.Props["WaterSpeed"] = speed;
            p.Props["DesiredSpeed"] = ds;
            UEPawnMovement.SetMoveTimer(p, dist);
            float got = UEInterpreter.AsFloat(p.GetProp("MoveTimer"));
            float sp = physics == 0 ? 200f : speed;
            Q den = F(sp) * F(ds);
            float want = den.Sign == 0 ? 0.5f : Q.R64(Q.R64(F(dist) * F(1.3f) / den) + F(1f)).F32();
            float old = sp * ds == 0f ? 0.5f : (dist * 1.3f) / (sp * ds) + 1f;
            if (!Same(old, want)) differs++;
            Check("SetMoveTimer", Same(got, want), $"phys={physics} speed={sp:R} ds={ds:R} dist={dist:R}: expected {want:R}, got {got:R}");
        }
        Check("SetMoveTimer.corpus", differs > 0, "corpus does not distinguish the old formula (V9)");
    }

    // ── APawn::performPhysics tail — disasm 0x103e5662-0x103e5690: MoveTimer = fld; fsub dt; fstp;
    // AvgPhysicsTime = fld APT; fmul 0.8f [0x10479e00]; fld dt; fmul 0.2f [0x104737f0]; faddp; fstp.
    static void DecrementMoveTimer()
    {
        var p = new UEObject();
        float apt = 0.1f, mt = 7.5f;
        p.Props["AvgPhysicsTime"] = apt; p.Props["MoveTimer"] = mt;
        int differs = 0;
        for (int i = 0; i < 3000; i++)
        {
            float dt = i < 1000 ? 0.02f : U(0.005, 0.1);
            float oldApt = (float)(dt * 0.2 + apt * 0.8);
            apt = Q.R64(F(apt) * F(0.8f) + F(dt) * F(0.2f)).F32();
            mt = (F(mt) - F(dt)).F32();
            if (!Same(oldApt, apt)) differs++;
            UEPawnMovement.DecrementMoveTimer(p, dt);
            float gotApt = UEInterpreter.AsFloat(p.GetProp("AvgPhysicsTime"));
            float gotMt = UEInterpreter.AsFloat(p.GetProp("MoveTimer"));
            Check("DecrementMoveTimer.AvgPhysicsTime", Same(gotApt, apt), $"step {i} dt={dt:R}: expected {apt:R}, got {gotApt:R}");
            Check("DecrementMoveTimer.MoveTimer", Same(gotMt, mt), $"step {i}: expected {mt:R}, got {gotMt:R}");
            // each step's input is the model value, so one miss does not drag all later steps
            p.Props["AvgPhysicsTime"] = apt; p.Props["MoveTimer"] = mt;
        }
        Check("DecrementMoveTimer.corpus", differs > 0, "corpus does not distinguish the old formula (V9)");
    }

    // ── physWalking tail — disasm 0x103ea36d-0x103ea4f8 (see the comment at FinishWalkingVelocity).
    static Vector3 WalkTailModel(Vector3 loc, Vector3 start, float dt, Vector3 n)
    {
        Q rv = Q.R64(F(1f) / F(dt));
        float dx = (F(loc.x) - F(start.x)).F32(), dy = (F(loc.y) - F(start.y)).F32(), dz = (F(loc.z) - F(start.z)).F32();
        float vx = Q.R64(F(dx) * rv).F32(), vy = Q.R64(F(dy) * rv).F32(), vz = Q.R64(F(dz) * rv).F32();
        Q s2 = Q.D53(Q.R64(Q.R64(F(vx) * F(vx) + F(vy) * F(vy)) + F(vz) * F(vz)));
        Q speed = Q.Sqrt64(s2);
        if (!(speed.Sign > 0) || !(n.z > 0f)) return new Vector3(vx, vy, vz);
        float speedF = speed.F32();
        Q num = Q.R64(Q.R64(F(n.y) * F(vy) + F(n.z) * F(vz)) + F(n.x) * F(vx));
        Q q = Q.R64(num / F(n.z));
        vz = Q.R64(F(vz) - q).F32();
        Q ns = Q.Sqrt64(Q.D53(Q.R64(Q.R64(F(vx) * F(vx) + F(vy) * F(vy)) + F(vz) * F(vz))));
        if (!(ns.Sign > 0)) return new Vector3(vx, vy, vz);
        float k = Q.R64(F(speedF) / ns).F32();
        return new Vector3((F(vx) * F(k)).F32(), (F(vy) * F(k)).F32(), (F(vz) * F(k)).F32());
    }

    static void FinishWalkingVelocity()
    {
        int differs = 0;
        for (int i = 0; i < 3000; i++)
        {
            var start = new Vector3(U(-6000, 6000), U(-6000, 6000), U(-2000, 2000));
            var loc = new Vector3(start.x + U(-15, 15), start.y + U(-15, 15), start.z + U(-3, 3));
            float dt = U(0.005, 0.1);
            float nx = U(-0.5, 0.5), ny = U(-0.5, 0.5);
            var n = i % 5 == 0 ? new Vector3(0f, 0f, 1f) : new Vector3(nx, ny, (float)System.Math.Sqrt(1.0 - nx * nx - ny * ny));
            var a = new UEObject();
            a.Props["Location"] = loc;
            UEPawnMovement.FinishWalkingVelocity(a, start, dt, n);
            Vector3 got = UEInterpreter.AsVector3(a.GetProp("Velocity"));
            Vector3 want = WalkTailModel(loc, start, dt, n);
            // naive float formula — only to check that the corpus distinguishes
            Vector3 ov = (loc - start) / dt; float osp = ov.magnitude;
            if (osp > 0f && n.z > 0f) { ov.z -= (n.x * ov.x + n.y * ov.y + n.z * ov.z) / n.z; float nsp = ov.magnitude; if (nsp > 0f) ov *= osp / nsp; }
            if (!Same(ov, want)) differs++;
            Check("FinishWalkingVelocity", Same(got, want), $"loc={S(loc)} start={S(start)} dt={dt:R} n={S(n)}: expected {S(want)}, got {S(got)}");
        }
        Check("FinishWalkingVelocity.corpus", differs > 0, "corpus does not distinguish the old formula (V9)");
    }

    // ── physWalking, step velocity with ZoneVelocity — disasm 0x103e6cfa-0x103e6dc0: threshold on the sum
    // of squares from the register; X, Y — float(ZV·25f), float(·dt), float(V + ·); Z — ZV·25f·dt + V in the register.
    static Vector3 WalkMoveVelModel(Vector3 v, Vector3 zv, float dt, out bool taken)
    {
        Q sum = Q.R64(Q.R64(F(zv.x) * F(zv.x) + F(zv.y) * F(zv.y)) + F(zv.z) * F(zv.z));
        taken = sum > F(90000f);
        if (!taken) return v;
        float tx = (F(zv.x) * F(25f)).F32(), ty = (F(zv.y) * F(25f)).F32();
        float x = (F(v.x) + F((F(tx) * F(dt)).F32())).F32();
        float y = (F(v.y) + F((F(ty) * F(dt)).F32())).F32();
        float z = Q.R64(Q.R64(Q.R64(F(zv.z) * F(25f)) * F(dt)) + F(v.z)).F32();
        return new Vector3(x, y, z);
    }

    static void WalkingMoveVelocity()
    {
        int differs = 0, boundary = 0;
        for (int i = 0; i < 4000; i++)
        {
            var v = RandomVel();
            Vector3 zv;
            float dt = U(0.005, 0.1);
            if (i % 4 == 3)
            {
                // threshold edge: |ZV|² near 90000, where the float sum of squares and the register sum diverge
                float zx = U(-250, 250), zy = U(-150, 150);
                float zz = (float)System.Math.Sqrt(90000.0 - (double)zx * zx - (double)zy * zy);
                int k = (int)(Next() * 7) - 3;
                for (int j = 0; j < System.Math.Abs(k); j++) zz = k > 0 ? System.MathF.BitIncrement(zz) : System.MathF.BitDecrement(zz);
                zv = new Vector3(zx, zy, zz);
            }
            else zv = new Vector3(U(-500, 500), U(-500, 500), U(-300, 300));
            var a = new UEObject();
            a.Props["Velocity"] = v;
            Vector3 got = UEPawnMovement.WalkingMoveVelocity(a, zv, dt);
            Vector3 want = WalkMoveVelModel(v, zv, dt, out bool taken);
            bool oldTaken = zv.sqrMagnitude > 90000f;
            if (oldTaken != taken) boundary++;
            Vector3 old = oldTaken ? v + zv * (25f * dt) : v;
            if (!Same(old, want)) differs++;
            Check("WalkingMoveVelocity", Same(got, want), $"v={S(v)} zv={S(zv)} dt={dt:R}: expected {S(want)}, got {S(got)}");
        }
        Check("WalkingMoveVelocity.corpus", differs > 0 && boundary > 0, $"corpus does not distinguish the old formula (V9): differences {differs}, at the threshold {boundary}");
    }

    // Velocity from displacement, one component in the x87 register (DisplacementVelocityX87 in the port).
    static Vector3 RegVelModel(Vector3 loc, Vector3 old, float dt, int regAxis)
    {
        Q rv = Q.R64(F(1f) / F(dt));
        float rvF = rv.F32();
        float C(int axis, float l, float o) => axis == regAxis
            ? Q.R64(Q.R64(F(l) - F(o)) * rv).F32()
            : (F((F(l) - F(o)).F32()) * F(rvF)).F32();
        return new Vector3(C(0, loc.x, old.x), C(1, loc.y, old.y), C(2, loc.z, old.z));
    }

    static Vector3 RandomLoc() => new(U(-5000, 5000), U(-5000, 5000), U(-1500, 1500));
    static float Sgn() => Next() < 0.5 ? -1f : 1f;
    static Vector3 RandomVel() => new(U(30, 900) * Sgn(), U(30, 900) * Sgn(), U(30, 900) * Sgn());
    static Vector3 StepModel(Vector3 l0, Vector3 v0, float dt) => new(
        (F(l0.x) + F((F(v0.x) * F(dt)).F32())).F32(), (F(l0.y) + F((F(v0.y) * F(dt)).F32())).F32(),
        (F(l0.z) + F((F(v0.z) * F(dt)).F32())).F32());

    // ── physProjectile tail — disasm 0x103f2e5f-0x103f2ec4: X in the register, Y/Z through float.
    static void ProjectileTail()
    {
        int differs = 0;
        for (int i = 0; i < 1500; i++)
        {
            Vector3 l0 = RandomLoc(), v0 = RandomVel();
            float dt = U(0.005, 0.1);
            var a = NewActor(l0, v0);
            _rt.PhysProjectile(_interp, a, dt);
            Vector3 l1 = UEInterpreter.AsVector3(a.GetProp("Location"));
            Vector3 moved = StepModel(l0, v0, dt);
            Check("PhysProjectile.move", Same(l1, moved), $"move without collisions: expected {S(moved)}, got {S(l1)}");
            Vector3 want = RegVelModel(l1, l0, dt, 0);
            if (!Same((l1 - l0) / dt, want)) differs++;
            Vector3 got = UEInterpreter.AsVector3(a.GetProp("Velocity"));
            Check("PhysProjectile.tail", Same(got, want), $"l0={S(l0)} l1={S(l1)} dt={dt:R}: expected {S(want)}, got {S(got)}");
        }
        Check("PhysProjectile.corpus", differs > 0, "corpus does not distinguish the old formula (V9)");
    }

    // ── APawn::physFlying tail — disasm 0x103f19b3-0x103f1a16: X in the register, Y/Z through float.
    static void FlyingTail()
    {
        int differs = 0;
        for (int i = 0; i < 1500; i++)
        {
            Vector3 l0 = RandomLoc(), v0 = RandomVel();
            float dt = U(0.005, 0.1);
            var a = NewActor(l0, v0);
            a.Props["AirSpeed"] = 1e6f; a.Props["AccelRate"] = 0f; a.Props["DesiredSpeed"] = 1f;
            _rt.PhysFlying(_interp, a, dt);
            Vector3 l1 = UEInterpreter.AsVector3(a.GetProp("Location"));
            // calcVelocity (bFluid: friction max(Friction, 1)) changes V before the move — the expectation is computed
            // from the observed displacement; the tail does not depend on the calcVelocity formula.
            Vector3 want = RegVelModel(l1, l0, dt, 0);
            if (!Same((l1 - l0) / dt, want)) differs++;
            Vector3 got = UEInterpreter.AsVector3(a.GetProp("Velocity"));
            Check("PhysFlying.tail", Same(got, want), $"l0={S(l0)} l1={S(l1)} dt={dt:R}: expected {S(want)}, got {S(got)}");
        }
        Check("PhysFlying.corpus", differs > 0, "corpus does not distinguish the old formula (V9)");
    }

    // Fixture seam: a WalkMoveHook move shifts Location by delta·f and answers (f, normal, None).
    static UEObject _hookActor;
    static readonly List<(float f, Vector3 n)> _hookScript = new();
    static readonly List<Vector3> _hookDeltas = new();
    static void ArmHook(UEObject actor, params (float f, Vector3 n)[] script)
    {
        _hookActor = actor; _hookScript.Clear(); _hookScript.AddRange(script); _hookDeltas.Clear();
        _rt.WalkMoveHook = (interp, pawn, delta) =>
        {
            if (!ReferenceEquals(pawn, _hookActor)) throw new System.InvalidOperationException("move of a foreign actor");
            int k = _hookDeltas.Count;
            _hookDeltas.Add(delta);
            (float f, Vector3 n) = k < _hookScript.Count ? _hookScript[k] : (1f, Vector3.zero);
            Vector3 l = UEInterpreter.AsVector3(pawn.GetProp("Location"));
            pawn.Props["Location"] = new Vector3(l.x + delta.x * f, l.y + delta.y * f, l.z + delta.z * f);
            return (f, n, null);
        };
    }

    // ── physFalling, landing — disasm 0x103efff4-0x103f00ce: T·dt and 1/(T·dt) in the register, d — float.
    // The actor's Physics is not PHYS_Falling, so processLanded does not zero the velocity (branch Physics == 2).
    static void FallingLandedVelocity()
    {
        int differs = 0;
        var floor = new Vector3(0f, 0f, 1f);
        for (int i = 0; i < 1500; i++)
        {
            Vector3 l0 = RandomLoc();
            var v0 = new Vector3(U(-500, 500), U(-500, 500), U(-900, -200));
            float dt = U(0.02, 0.1);
            float f1 = U(0.15, 0.98);
            var a = NewActor(l0, v0);
            a.Props["Physics"] = (byte)0;
            ArmHook(a, (f1, floor));
            _rt.PhysFalling(_interp, a, dt);
            _rt.WalkMoveHook = null;
            Vector3 l1 = UEInterpreter.AsVector3(a.GetProp("Location"));
            Q p = F(f1) * F(dt);
            Check("PhysFalling.landed.gate", f1 > 0.1f && p > F(0.003f), "landing threshold not passed — the corpus does not check the line");
            Q rv = Q.R64(F(1f) / Q.R64(p));
            float Lv(float l, float o) => Q.R64(F((F(l) - F(o)).F32()) * rv).F32();
            var want = new Vector3(Lv(l1.x, l0.x), Lv(l1.y, l0.y), Lv(l1.z, l0.z));
            if (!Same((l1 - l0) / (f1 * dt), want)) differs++;
            Vector3 got = UEInterpreter.AsVector3(a.GetProp("Velocity"));
            Check("PhysFalling.landed", Same(got, want), $"l0={S(l0)} l1={S(l1)} dt={dt:R} T={f1:R}: expected {S(want)}, got {S(got)}");
        }
        Check("PhysFalling.landed.corpus", differs > 0, "corpus does not distinguish the old formula (V9)");
    }

    // ── physFalling, landing threshold — disasm 0x103f0023-0x103f0034: `fld T; fmul dt; fcomp 0.003f` —
    // the product is compared from the register, without rounding to float. Pairs (T, dt) are chosen so that
    // exact T·dt > 0.003f while rounded to float it is exactly 0.003f: shipped recomputes the velocity.
    static void FallingLandedThreshold()
    {
        int found = 0;
        var floor = new Vector3(0f, 0f, 1f);
        for (int i = 0; i < 20000 && found < 40; i++)
        {
            float t = U(0.15, 0.98);
            float dt = (float)(0.003f / (double)t);
            for (int step = -2; step <= 2; step++)
            {
                float d = System.MathF.BitIncrement(dt);
                d = step switch { -2 => System.MathF.BitDecrement(System.MathF.BitDecrement(dt)), -1 => System.MathF.BitDecrement(dt), 0 => dt, 1 => d, _ => System.MathF.BitIncrement(d) };
                Q p = F(t) * F(d);
                if (!(p > F(0.003f)) || p.F32() > 0.003f) continue;
                found++;
                Vector3 l0 = RandomLoc(), v0 = new Vector3(U(-500, 500), U(-500, 500), U(-900, -200));
                var a = NewActor(l0, v0);
                a.Props["Physics"] = (byte)0;
                ArmHook(a, (t, floor));
                _rt.PhysFalling(_interp, a, d);
                _rt.WalkMoveHook = null;
                Vector3 l1 = UEInterpreter.AsVector3(a.GetProp("Location"));
                Q rv = Q.R64(F(1f) / Q.R64(p));
                float Lv(float l, float o) => Q.R64(F((F(l) - F(o)).F32()) * rv).F32();
                var want = new Vector3(Lv(l1.x, l0.x), Lv(l1.y, l0.y), Lv(l1.z, l0.z));
                Vector3 got = UEInterpreter.AsVector3(a.GetProp("Velocity"));
                Check("PhysFalling.landed.threshold", Same(got, want), $"T={t:R} dt={d:R}: velocity not recomputed from the displacement: expected {S(want)}, got {S(got)}");
                break;
            }
        }
        Check("PhysFalling.landed.threshold.corpus", found >= 10, $"pairs at the threshold boundary found {found}");
    }

    // ── physFalling, bBounce branch — disasm 0x103effc6-0x103effd5: remaining = fld 1.0; fsub T; fmul dt;
    // fadd remaining; fstp — a single rounding. Zero gravity, velocity (1,0,0): the second substep runs
    // at the same velocity, and its X move equals the substep length, i.e. the returned time.
    static void FallingBounceRemaining()
    {
        int differs = 0;
        _zone.Props["ZoneGravity"] = Vector3.zero;
        try
        {
            for (int i = 0; i < 2000; i++)
            {
                float f1 = i % 3 == 0 ? U(0.001, 0.05) : U(0.05, 0.6);
                var a = NewActor(RandomLoc(), new Vector3(1f, 0f, 0f));
                a.Props["Physics"] = (byte)2;
                a.Props["bBounce"] = true;
                ArmHook(a, (f1, new Vector3(-1f, 0f, 0f)), (1f, Vector3.zero));
                _rt.PhysFalling(_interp, a, 0.1f);
                _rt.WalkMoveHook = null;
                Check("PhysFalling.bounce.moves", _hookDeltas.Count == 2, $"moves {_hookDeltas.Count}, expected 2");
                if (_hookDeltas.Count < 2) continue;
                float want = Q.R64(Q.R64((F(1f) - F(f1)) * F(0.1f)) + F(0f)).F32();
                float old = (1f - f1) * 0.1f;
                if (!Same(old, want)) differs++;
                float got = _hookDeltas[1].x;
                Check("PhysFalling.bounce.remaining", Same(got, want), $"T={f1:R}: second substep expected {want:R}, got {got:R}");
            }
        }
        finally { _zone.Props["ZoneGravity"] = new Vector3(0f, 0f, -950f); }
        Check("PhysFalling.bounce.corpus", differs > 0, "corpus does not distinguish the old formula (V9)");
    }

    static UEObject NewMover(Vector3 loc, Vector3 basePos, Vector3 key, float physRate, float alpha)
    {
        var m = NewActor(loc, Vector3.zero);
        m.Props["bInterpolating"] = true;
        m.Props["PhysRate"] = physRate;
        m.Props["PhysAlpha"] = alpha;
        m.Props["KeyNum"] = (byte)1;
        m.Props["MoverGlideType"] = (byte)0;
        m.Props["BasePos"] = basePos;
        m.Props["OldPos"] = loc;
        m.Props["BaseRot"] = new UERotator(0, 0, 0);
        m.Props["OldRot"] = new UERotator(0, 0, 0);
        var kp = new List<object>(); var kr = new List<object>();
        for (int k = 0; k < 8; k++) { kp.Add(k == 1 ? key : Vector3.zero); kr.Add(new UERotator(0, 0, 0)); }
        m.Props["KeyPos"] = kp; m.Props["KeyRot"] = kr;
        return m;
    }

    // ── AActor::performPhysics, PHYS_MovingBrush — disasm 0x103e5351-0x103e539e: Z in the register, X/Y through float.
    static void MovingBrushVelocity()
    {
        int differs = 0;
        for (int i = 0; i < 1500; i++)
        {
            Vector3 l0 = RandomLoc();
            var basePos = new Vector3(U(-3000, 3000), U(-3000, 3000), U(-800, 800));
            var key = new Vector3(U(-300, 300), U(-300, 300), U(-300, 300));
            float dt = U(0.005, 0.1);
            var m = NewMover(l0, basePos, key, U(0.3, 2.0), U(0, 0.8));
            _rt.PhysMovingBrush(_interp, m, dt);
            Vector3 l1 = UEInterpreter.AsVector3(m.GetProp("Location"));
            Vector3 want = RegVelModel(l1, l0, dt, 2);
            if (!Same((l1 - l0) / dt, want)) differs++;
            Vector3 got = UEInterpreter.AsVector3(m.GetProp("Velocity"));
            Check("PhysMovingBrush.velocity", Same(got, want), $"l0={S(l0)} l1={S(l1)} dt={dt:R}: expected {S(want)}, got {S(got)}");
        }
        Check("PhysMovingBrush.corpus", differs > 0, "corpus does not distinguish the old formula (V9)");
    }

    // ── physMovingBrush has no PhysRate gate — disasm 0x10406261-0x104065c3: the loop head checks only
    // bInterpolating and dt > 0. PhysRate = 0, PhysAlpha = 1: the mover snaps to the key pose, bInterpolating
    // is cleared (alpha ≥ 1, full move).
    static void MovingBrushZeroRate()
    {
        var l0 = new Vector3(100f, 200f, 300f);
        var basePos = new Vector3(1000f, -50f, 20f);
        var key = new Vector3(0f, 128f, 0f);
        var m = NewMover(l0, basePos, key, 0f, 1f);
        _rt.PhysMovingBrush(_interp, m, 0.05f);
        Vector3 l1 = UEInterpreter.AsVector3(m.GetProp("Location"));
        var target = new Vector3(1000f, 78f, 20f);   // BasePos + KeyPos[1], alpha 1 (linear)
        Check("PhysMovingBrush.zeroRate.pose", Same(l1, target), $"expected the key pose {S(target)}, got {S(l1)}");
        Check("PhysMovingBrush.zeroRate.reached", !UEInterpreter.ToBool(m.GetProp("bInterpolating")),
            "bInterpolating not cleared: key not reached");
    }

    // ── AInterpolationManager::performPhysics, final — disasm 0x103f9142-0x103f9183: FVector(L − start),
    // then `fld 1.0; fdiv dt` — 1/dt in the register for all three components, each product is fstp
    // dword. This is equivalent to division in float (see the comment at DivByFloat), unlike rounding 1/dt
    // to float and multiplying in float.
    static void InterpolationFinalVelocity()
    {
        int differs = 0;
        for (int i = 0; i < 1500; i++)
        {
            Vector3 l0 = RandomLoc();
            var owner = NewActor(l0, Vector3.zero);
            owner.Props["bInterpolating"] = true;
            owner.Props["IPSpeed"] = 0f;
            var pt = new UEObject();
            pt.Props["Location"] = new Vector3(l0.x + U(-900, 900), l0.y + U(-900, 900), l0.z + U(-300, 300));
            pt.Props["EndControlPoint"] = new Vector3(U(-200, 200), U(-200, 200), U(-50, 50));
            pt.Props["StartControlPoint"] = new Vector3(U(50, 200), U(50, 200), U(5, 50));
            pt.Props["bFaceMoveDirection"] = true;
            pt.Props["bNewRotationSmoothing"] = true;
            pt.Props["Rotation"] = new UERotator(0, 0, 0);
            pt.Props["DesiredSpeed"] = 0f;
            var mgr = new UEObject();
            mgr.Props["Owner"] = owner;
            mgr.Props["Dest"] = pt;
            mgr.Props["PhysRate"] = U(0.2, 1.5);
            mgr.Props["PhysAlpha"] = U(0.05, 0.5);
            mgr.Props["RemainingPause"] = 0f;
            float dt = U(0.005, 0.1);
            _rt.PhysInterpolating(_interp, mgr, dt);
            Vector3 l1 = UEInterpreter.AsVector3(owner.GetProp("Location"));
            Check("PhysInterpolating.moved", !Same(l1, l0), "the owner did not move — the corpus does not check the line");
            Q rv = Q.R64(F(1f) / F(dt));
            float Iv(float l, float o) => Q.R64(F((F(l) - F(o)).F32()) * rv).F32();
            var want = new Vector3(Iv(l1.x, l0.x), Iv(l1.y, l0.y), Iv(l1.z, l0.z));
            float inv = 1f / dt;
            if (!Same(new Vector3((l1.x - l0.x) * inv, (l1.y - l0.y) * inv, (l1.z - l0.z) * inv), want)) differs++;
            Vector3 got = UEInterpreter.AsVector3(owner.GetProp("Velocity"));
            Check("PhysInterpolating.velocity", Same(got, want), $"l0={S(l0)} l1={S(l1)} dt={dt:R}: expected {S(want)}, got {S(got)}");
        }
        Check("PhysInterpolating.corpus", differs > 0, "corpus does not distinguish the old formula (V9)");
    }
}
