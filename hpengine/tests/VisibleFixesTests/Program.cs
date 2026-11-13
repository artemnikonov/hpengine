// Tests of the fixes for player-visible divergences found by the coverage reconciliation, without game files.
//
// Input: the same Core functions that the VM and the game loop call:
//   SetPhysics      : UELevelRuntime.SetPhysics (native SetPhysics -> SetPhysicsActor, processLanded);
//   MoveSmooth      : UELevelRuntime.MoveSmooth (native MoveSmooth -> ExecMoveSmooth, wind tick, push);
//   role branches   : UELevelRuntime.TickActorBody (actor tick ULevel::Tick -> TickActor);
//   world order     : UELevelRuntime.BootstrapPlayWorld / BootstrapLoadedWorld (the port's LoadMap);
//   ZoneDist        : UELevelRuntime.BuildZoneDist (LoadMap record body) + the bootstrap's "no model" branch.
// Actors without a class (empty registry): the generic actor branch runs; script event bodies are replaced by
// UELifecycle.EventProbe, a Core test seam that sees every event dispatch before the body lookup.
//
// Expectations follow the shipped body (addresses at each test), computed in the test independently of port code.
// Red mutations: audit/visible_fixes_redtest.sh.
using HP1.UEScript;
using System;
using System.Collections.Generic;
using Vector3 = HP1.UEScript.UEVector;

static class Program
{
    static int _fail, _pass;

    static void Check(string test, bool ok, string detail)
    {
        if (ok) { _pass++; return; }
        _fail++;
        Console.WriteLine($"FAIL {test}: {detail}");
    }

    static string S(Vector3 v) => $"({v.x:R}, {v.y:R}, {v.z:R})";
    static Vector3 V(UEObject o, string p) => UEInterpreter.AsVector3(o.GetProp(p));
    static float F(UEObject o, string p) => UEInterpreter.AsFloat(o.GetProp(p));
    static int I(UEObject o, string p) => UEInterpreter.ToInt(o.GetProp(p));

    static int Main()
    {
        HP1.Core.UEClassRegistry.Shared ??= new HP1.Core.UEClassRegistry();
        // The machine name is a platform boundary: the source is set BEFORE the first level boot in the process
        // (appComputerName fills a static buffer once).
        UELevelRuntime.ComputerNameSource = () => "Ha-rry_PC 7é.local";

        SetPhysicsLivePhysics();
        MoveSmoothLiveRotation();
        TickRoleBranches();
        HighDetailAfterGameInfoAndComputerName();
        BrushTrackerAfterSpawnPlayActor();
        ZoneDistTable();

        Console.WriteLine($"VisibleFixesTests: passed {_pass}, failed {_fail}");
        return _fail == 0 ? 0 : 1;
    }

    static (UEInterpreter interp, UELevelRuntime rt, UEObject level) World()
    {
        var interp = new UEInterpreter();
        var rt = new UELevelRuntime();
        // LevelInfo is also the zone: SetActorZone without BSP resolves the region to LevelInfo.
        var level = new UEObject();
        level.Props["ZoneGravity"] = new Vector3(0f, 0f, -950f);
        level.Props["ZoneVelocity"] = Vector3.zero;
        level.Props["ZoneTerminalVelocity"] = 1e6f;
        level.Props["ZoneFluidFriction"] = 0f;
        level.Props["TimeDilation"] = 1f;
        level.Props["NetMode"] = (byte)0;
        interp.RestoreWorldLevelInfo(level);
        return (interp, rt, level);
    }

    // ── AActor::setPhysics: disasm 0x103e5140..0x103e523b ─────────────────────────────────────────
    // Physics = New; based {0,1,5,7,10}: NewFloor ? (Base != NewFloor -> SetBase(NewFloor, 1)) : FindBase;
    // otherwise Base -> SetBase(NULL, 1). THEN 0x103e51c8: `mov al, byte [esi+0x30]` reads the LIVE Physics; 0 or 5 ->
    // Velocity (+0x12c) and Acceleration (+0x13c) = 0. BaseChange (the SetBase event) may change Physics
    // from script (UT Pawn.BaseChange: SetPhysics(PHYS_Falling)); then the new value decides.
    static void SetPhysicsLivePhysics()
    {
        var vel = new Vector3(10f, -20f, 30f);
        var acc = new Vector3(1f, 2f, 3f);
        UEObject Actor(int physics, UEObject baseActor)
        {
            var a = new UEObject();
            a.Props["Physics"] = (byte)physics;
            a.Props["Velocity"] = vel;
            a.Props["Acceleration"] = acc;
            a.Props["Base"] = baseActor;
            return a;
        }
        void WithBaseChange(UEObject who, int setPhysicsTo, Action body)
        {
            UELifecycle.EventProbe = (a, name, args) =>
            {
                if (name == "BaseChange" && ReferenceEquals(a, who)) a.Props["Physics"] = (byte)setPhysicsTo;
            };
            try { body(); } finally { UELifecycle.EventProbe = null; }
        }

        // 1. SetPhysics(PHYS_None, floor): BaseChange switches to PHYS_Falling -> NO zeroing.
        {
            var (interp, rt, _) = World();
            var floor = new UEObject();
            var a = Actor(2, null);
            WithBaseChange(a, 2, () => rt.SetPhysics(interp, a, 0, floor));
            Check("SetPhysics.none-then-falling", I(a, "Physics") == 2 && V(a, "Velocity") == vel && V(a, "Acceleration") == acc,
                $"BaseChange switched to Falling: shipped does not touch Velocity/Acceleration; Physics={I(a, "Physics")} V={S(V(a, "Velocity"))} A={S(V(a, "Acceleration"))}");
            Check("SetPhysics.none-then-falling.base", ReferenceEquals(a.GetProp("Base"), floor), "Base = NewFloor");
        }
        // 2. SetPhysics(PHYS_Falling) with a base: SetBase(NULL) -> BaseChange sets PHYS_None -> zeroing HAPPENS.
        {
            var (interp, rt, _) = World();
            var floor = new UEObject();
            var a = Actor(1, floor);
            WithBaseChange(a, 0, () => rt.SetPhysics(interp, a, 2, null));
            Check("SetPhysics.falling-then-none", I(a, "Physics") == 0 && V(a, "Velocity") == Vector3.zero && V(a, "Acceleration") == Vector3.zero,
                $"BaseChange switched to None: shipped zeroes; Physics={I(a, "Physics")} V={S(V(a, "Velocity"))} A={S(V(a, "Acceleration"))}");
            Check("SetPhysics.falling-then-none.base", a.GetProp("Base") is null, "Base cleared");
        }
        // 3. SetPhysics(PHYS_Walking, floor): BaseChange sets PHYS_Rotating (5) -> zeroing HAPPENS.
        {
            var (interp, rt, _) = World();
            var floor = new UEObject();
            var a = Actor(2, null);
            WithBaseChange(a, 5, () => rt.SetPhysics(interp, a, 1, floor));
            Check("SetPhysics.walking-then-rotating", V(a, "Velocity") == Vector3.zero && V(a, "Acceleration") == Vector3.zero,
                $"BaseChange switched to Rotating: zeroing by the live value; V={S(V(a, "Velocity"))}");
        }
        // Control without an event: argument = live value.
        {
            var (interp, rt, _) = World();
            var a = Actor(2, null);
            rt.SetPhysics(interp, a, 0, new UEObject());
            Check("SetPhysics.control-none", V(a, "Velocity") == Vector3.zero && V(a, "Acceleration") == Vector3.zero, "PHYS_None zeroes");
            var b = Actor(1, null);
            rt.SetPhysics(interp, b, 2, null);
            Check("SetPhysics.control-falling", V(b, "Velocity") == vel && V(b, "Acceleration") == acc, "PHYS_Falling does not zero");
            var c = Actor(2, null);
            rt.SetPhysics(interp, c, 2, null);
            Check("SetPhysics.control-unchanged", V(c, "Velocity") == vel && I(c, "Physics") == 2, "same Physics — exit without writes (0x103e5172)");
        }
    }

    // ── AActor::moveSmooth: disasm 0x103e4c30..0x103e4ed3 ──────────────────────────────────────────
    // edi = &Rotation (`lea edi, [esi+0x108]`, 0x103e4c83); three words are copied BEFORE EACH MoveActor
    // (0x103e4c92 first move, 0x103e4e1c slide, 0x103e4e7b third move). HitWall (0x103e4df8) sits between
    // the first move and the slide: a rotation set by it is carried on by the slide, not rolled back.
    static void MoveSmoothLiveRotation()
    {
        var (interp, rt, level) = World();
        var wall = new UEObject();
        wall.Props["Location"] = new Vector3(60f, 0f, 0f);
        wall.Props["CollisionRadius"] = 20f; wall.Props["CollisionHeight"] = 40f;
        wall.Props["bCollideActors"] = true; wall.Props["bBlockActors"] = true; wall.Props["bBlockPlayers"] = true;
        wall.Props["bStatic"] = true;
        wall.Props["Region"] = UEObject.MakeRegion(level, -1, 0);
        var rot0 = new UERotator(0, 1000, 0);
        var rotWall = new UERotator(0, 30000, 0);
        UEObject Mover()
        {
            var m = new UEObject();
            m.Props["Location"] = Vector3.zero;
            m.Props["Rotation"] = rot0;
            m.Props["CollisionRadius"] = 5f; m.Props["CollisionHeight"] = 5f;
            m.Props["bCollideActors"] = true; m.Props["bBlockActors"] = true;
            m.Props["bCollideWorld"] = false; m.Props["bMovable"] = true;
            m.Props["Region"] = UEObject.MakeRegion(level, -1, 0);
            return m;
        }
        var mover = Mover();
        rt.SetActors(new[] { level, wall, mover });
        rt.InitCollisionHash();

        int hitWalls = 0;
        UELifecycle.EventProbe = (a, name, args) =>
        {
            if (name == "HitWall" && ReferenceEquals(a, mover)) { hitWalls++; a.Props["Rotation"] = rotWall; }
        };
        // Move into a wall at an angle: the slide along the wall is non-zero.
        try { rt.MoveSmooth(interp, mover, new Vector3(100f, 30f, 0f)); }
        finally { UELifecycle.EventProbe = null; }
        UERotator got = UEInterpreter.AsRotator(mover.GetProp("Rotation"));
        Check("MoveSmooth.premise-hitwall", hitWalls == 1, $"premise: exactly one HitWall (was {hitWalls})");
        Check("MoveSmooth.live-rotation", got.Pitch == rotWall.Pitch && got.Yaw == rotWall.Yaw && got.Roll == rotWall.Roll,
            $"the rotation from HitWall is kept by the slide: expected Yaw={rotWall.Yaw}, got Yaw={got.Yaw}");
        Check("MoveSmooth.slid", V(mover, "Location").y != 0f, $"premise: the slide moved the actor along the wall, loc={S(V(mover, "Location"))}");

        // Control: without a rotation in HitWall the rotation stays the original.
        var mover2 = Mover();
        rt.SetActors(new[] { level, wall, mover, mover2 });
        rt.HashAddActor(mover2);
        mover.Props["Location"] = new Vector3(0f, 500f, 0f);
        int hit2 = 0;
        UELifecycle.EventProbe = (a, name, args) => { if (name == "HitWall" && ReferenceEquals(a, mover2)) hit2++; };
        try { rt.MoveSmooth(interp, mover2, new Vector3(100f, 30f, 0f)); }
        finally { UELifecycle.EventProbe = null; }
        UERotator got2 = UEInterpreter.AsRotator(mover2.GetProp("Rotation"));
        Check("MoveSmooth.control", hit2 == 1 && got2.Yaw == rot0.Yaw, $"without rotation in HitWall Yaw={got2.Yaw}, HitWall={hit2}");

        // Corner: the slide along the first wall hits the second -> TwoWallAdjust and a THIRD move (0x103e4e7b).
        // The third move also carries the rotation from HitWall.
        {
            var (interp3, rt3, level3) = World();
            UEObject Wall(Vector3 at)
            {
                var w = new UEObject();
                w.Props["Location"] = at;
                w.Props["CollisionRadius"] = 20f; w.Props["CollisionHeight"] = 40f;
                w.Props["bCollideActors"] = true; w.Props["bBlockActors"] = true; w.Props["bBlockPlayers"] = true;
                w.Props["bStatic"] = true;
                w.Props["Region"] = UEObject.MakeRegion(level3, -1, 0);
                return w;
            }
            var w1 = Wall(new Vector3(60f, 0f, 0f));
            var w2 = Wall(new Vector3(30f, 45f, 0f));
            var m = Mover();
            m.Props["Region"] = UEObject.MakeRegion(level3, -1, 0);
            rt3.SetActors(new[] { level3, w1, w2, m });
            rt3.InitCollisionHash();
            int hits = 0;
            UELifecycle.EventProbe = (a, name, args) =>
            {
                if (name == "HitWall" && ReferenceEquals(a, m)) { hits++; a.Props["Rotation"] = rotWall; }
            };
            Vector3 before = V(m, "Location");
            try { rt3.MoveSmooth(interp3, m, new Vector3(100f, 30f, 0f)); }
            finally { UELifecycle.EventProbe = null; }
            UERotator g3 = UEInterpreter.AsRotator(m.GetProp("Rotation"));
            Console.WriteLine($"  corner: HitWall={hits} loc {S(before)} -> {S(V(m, "Location"))} Yaw={g3.Yaw}");
            Check("MoveSmooth.corner.live-rotation", hits == 1 && g3.Yaw == rotWall.Yaw,
                $"the third move carries the rotation from HitWall: Yaw={g3.Yaw}, HitWall={hits}");
        }
    }

    // ── AActor::Tick, role branches (LAB_103b3d7a): disasm 0x103b3d7a..0x103b4356 ───────────────────
    //   RemoteRole == 3 (0x103b3f85): Role == 4 -> copy ViewTarget (PlayerPawn) + ONLY Timer
    //     (LAB_103b4054); Role != 4 -> nothing (0x103b3f93 -> 0x103b4356);
    //   else Role < 2 (0x103b40c6 `cmp al,2; jb 0x103b433e`) -> performPhysics only when Physics == 2
    //     (0x103b433e `cmp byte [esi+0x30], 2`);
    //   else the generic branch: events, state, Timer, LifeSpan (Role >= 2), physics (Role != 3).
    // Actors without a class: the simulated pawn branch (Pawn && Role == 2) is not entered.
    static void TickRoleBranches()
    {
        const float dt = 0.05f;
        var vel0 = new Vector3(10f, 0f, 0f);
        (UEObject a, UELevelRuntime rt, UEInterpreter interp, List<string> events) Tick(int role, int remoteRole, int physics)
        {
            var (interp, rt, level) = World();
            var a = new UEObject();
            a.Props["Role"] = (byte)role;
            a.Props["RemoteRole"] = (byte)remoteRole;
            a.Props["Physics"] = (byte)physics;
            a.Props["Location"] = new Vector3(0f, 0f, 1000f);
            a.Props["Velocity"] = vel0;
            a.Props["Acceleration"] = Vector3.zero;
            a.Props["Rotation"] = new UERotator(0, 0, 0);
            a.Props["bMovable"] = true;
            a.Props["LifeSpan"] = 5f;
            a.Props["Region"] = UEObject.MakeRegion(level, 1, 1);
            a.TimerRate = 0.04f; a.TimerCounter = 0f; a.TimerLoop = true;
            rt.SetActors(new[] { level, a });
            var events = new List<string>();
            UELifecycle.EventProbe = (o, name, args) => { if (ReferenceEquals(o, a)) events.Add(name); };
            try { rt.TickActorBody(interp, a, dt); }
            finally { UELifecycle.EventProbe = null; }
            return (a, rt, interp, events);
        }
        bool Fell(UEObject a) => V(a, "Velocity").z < 0f;
        bool TimerRan(UEObject a, List<string> ev) => ev.Contains("Timer") && a.TimerCounter != 0f;
        bool LifeSpanTicked(UEObject a) => F(a, "LifeSpan") != 5f;

        // Control, generic branch (Role 4, RemoteRole 0): Timer, LifeSpan and falling physics run.
        {
            var (a, _, _, ev) = Tick(4, 0, 2);
            Check("Roles.authority.control", TimerRan(a, ev) && LifeSpanTicked(a) && Fell(a),
                $"Role 4/RemoteRole 0: Timer={TimerRan(a, ev)} LifeSpan={F(a, "LifeSpan"):R} V={S(V(a, "Velocity"))}");
        }
        // RemoteRole == ROLE_AutonomousProxy, Role == Authority: ONLY Timer.
        {
            var (a, _, _, ev) = Tick(4, 3, 2);
            Check("Roles.autonomous-remote.timer-only", TimerRan(a, ev) && !LifeSpanTicked(a) && !Fell(a) && !ev.Contains("Tick"),
                $"RemoteRole 3/Role 4: Timer={TimerRan(a, ev)} LifeSpan={F(a, "LifeSpan"):R} V={S(V(a, "Velocity"))} events=[{string.Join(",", ev)}]");
        }
        // RemoteRole == 3, Role != 4: nothing.
        foreach (int role in new[] { 3, 2 })
        {
            var (a, _, _, ev) = Tick(role, 3, 2);
            Check($"Roles.autonomous-remote.role{role}.nothing", !ev.Contains("Timer") && a.TimerCounter == 0f && !LifeSpanTicked(a) && !Fell(a),
                $"RemoteRole 3/Role {role}: Timer={ev.Contains("Timer")} counter={a.TimerCounter:R} LifeSpan={F(a, "LifeSpan"):R} V={S(V(a, "Velocity"))}");
        }
        // Role < 2 and Physics == PHYS_Falling: ONLY physics.
        foreach (int role in new[] { 1, 0 })
        {
            var (a, _, _, ev) = Tick(role, 0, 2);
            Check($"Roles.role{role}.falling-physics-only", Fell(a) && !ev.Contains("Timer") && a.TimerCounter == 0f && !LifeSpanTicked(a),
                $"Role {role}/PHYS_Falling: V={S(V(a, "Velocity"))} Timer={ev.Contains("Timer")} LifeSpan={F(a, "LifeSpan"):R}");
        }
        // Role < 2 and not falling: nothing (no physics, no timer).
        {
            var (a, _, _, ev) = Tick(1, 0, 4);
            Check("Roles.role1.flying-nothing", V(a, "Location") == new Vector3(0f, 0f, 1000f) && !ev.Contains("Timer") && a.TimerCounter == 0f,
                $"Role 1/PHYS_Flying: loc={S(V(a, "Location"))} Timer={ev.Contains("Timer")}");
        }
        // Role == 3 (AutonomousProxy) with RemoteRole != 3: generic branch; Timer and LifeSpan run, physics does not
        // (128728: performPhysics when Role != 3).
        {
            var (a, _, _, ev) = Tick(3, 0, 2);
            Check("Roles.role3.general-branch", TimerRan(a, ev) && LifeSpanTicked(a) && !Fell(a),
                $"Role 3/RemoteRole 0: Timer={TimerRan(a, ev)} LifeSpan={F(a, "LifeSpan"):R} V={S(V(a, "Velocity"))}");
        }
    }

    // ── UGameEngine::LoadMap: InitGameInfo -> bHighDetailMode; ComputerName ────────────────────────
    // SpawnActor(GameInfo) 0x1039da73 comes BEFORE `Info+0x438 |= 8` (0x1039db7e..0x1039dbb8): the GameInfo
    // spawn events see the map's bHighDetailMode. Fixture without classes: SpawnGameInfo finds no game class
    // and throws; at that point shipped has NOT yet written bHighDetailMode.
    // ComputerName (246880..246917) is written before InitGameInfo: by the refusal it is already written.
    static void HighDetailAfterGameInfoAndComputerName()
    {
        var (interp, rt, level) = World();
        var log = new List<string>();
        rt.Journal = log.Add;
        level.Props["bHighDetailMode"] = false;
        rt.SetActors(new[] { level });
        Exception thrown = null;
        try { rt.BootstrapPlayWorld(interp, new UELevelRuntime.PlayWorldConfig()); }
        catch (Exception e) { thrown = e; }
        Check("HighDetail.premise-spawn-failed", thrown is System.IO.InvalidDataException && thrown.Message.Contains("SpawnGameInfo"),
            $"premise: SpawnGameInfo refused (got {thrown?.GetType().Name}: {thrown?.Message})");
        Check("HighDetail.after-gameinfo", UEInterpreter.ToBool(level.GetProp("bHighDetailMode")) == false,
            $"bHighDetailMode written BEFORE the GameInfo spawn (={level.GetProp("bHighDetailMode")}); shipped writes it after (0x1039db7e)");
        // appComputerName: only [A-Za-z0-9] of the machine name (Core appComputerName, 0x61..0x7a / 0x41..0x5a / 0x30..0x39).
        Check("ComputerName.loadmap", level.GetProp("ComputerName") as string == "HarryPC7local",
            $"LevelInfo.ComputerName = \"{level.GetProp("ComputerName")}\", expected \"HarryPC7local\"");
        // ZoneDist without a model: nothing set, the refusal is named in the log.
        Check("ZoneDist.no-model", rt.ZoneDist is null && log.Contains("zone_dist model=none"),
            $"without a model ZoneDist={(rt.ZoneDist is null ? "null" : "set")}, zone_dist log: {log.Find(l => l.StartsWith("zone_dist")) ?? "none"}");
    }

    // ── LoadMap: MatchViewportsToActors -> GNewBrushTracker (disasm 0x1039e56c, then 0x1039e5a0) ─────
    // Fixture: a loaded world without Level.Game; SpawnPlayActor refuses right away. Shipped builds the
    // tracker AFTER the player enters, so at the refusal it does not exist yet. The same method
    // (SpawnPlayActorThenBrushTracker) is called by BootstrapPlayWorld too.
    static void BrushTrackerAfterSpawnPlayActor()
    {
        var (interp, rt, level) = World();
        rt.SetActors(new[] { level });
        Exception thrown = null;
        try { rt.BootstrapLoadedWorld(interp, new UELevelRuntime.PlayWorldConfig()); }
        catch (Exception e) { thrown = e; }
        Check("BrushTracker.premise-spawnplayactor-failed", thrown is InvalidOperationException && thrown.Message.Contains("SpawnPlayActor"),
            $"premise: SpawnPlayActor refused (got {thrown?.GetType().Name}: {thrown?.Message})");
        Check("BrushTracker.after-spawnplayactor", !rt.BrushTrackerLive,
            "the brush tracker is built BEFORE SpawnPlayActor; shipped builds it after (0x1039e56c -> 0x1039e5a0)");
        Check("ComputerName.loadgame", level.GetProp("ComputerName") as string == "HarryPC7local",
            $"the LoadMap save branch also writes ComputerName: \"{level.GetProp("ComputerName")}\"");
    }

    // ── ULevel.ZoneDist: LoadMap 247117..247188, disasm 0x1039d6d4..0x1039d853 ────────────────────
    // Independent model: boolean matrices. P1 = connectivity (P1[i][j] = bit j of Zones[i].Connectivity);
    // P(n+1) = P(n)·P(n) (boolean product: next[j] = OR conn[k] over k in conn[j]); dist[i][j] =
    // the smallest n in 1..63 with P(n)[i][j], diagonal 0, otherwise 255.
    static byte[] ZoneDistModel(ulong[] connectivity)
    {
        var p = new bool[64, 64];
        for (int i = 0; i < connectivity.Length; i++)
            for (int j = 0; j < 64; j++)
                p[i, j] = ((connectivity[i] >> j) & 1UL) == 1UL;
        var d = new byte[64 * 64];
        for (int i = 0; i < 64; i++) for (int j = 0; j < 64; j++) d[i * 64 + j] = i == j ? (byte)0 : (byte)255;
        for (int n = 1; n <= 63; n++)
        {
            for (int i = 0; i < 64; i++)
                for (int j = 0; j < 64; j++)
                    if (i != j && d[i * 64 + j] == 255 && p[i, j]) d[i * 64 + j] = (byte)n;
            var q = new bool[64, 64];
            for (int i = 0; i < 64; i++)
                for (int k = 0; k < 64; k++)
                    if (p[i, k])
                        for (int j = 0; j < 64; j++)
                            if (p[k, j]) q[i, j] = true;
            p = q;
        }
        return d;
    }

    static void ZoneDistTable()
    {
        ulong B(params int[] bits) { ulong m = 0; foreach (int b in bits) m |= 1UL << b; return m; }

        // Chain 0-1-2-3-4 with the self bit: step n gives reachability within 2^(n-1) edges, so the zone
        // 4 edges away gets 3, not 4 (separates the shipped body from a BFS edge count, V9).
        var chain = new[] { B(0, 1), B(0, 1, 2), B(1, 2, 3), B(2, 3, 4), B(3, 4) };
        byte[] got = UELevelRuntime.BuildZoneDist(chain);
        byte[] want = ZoneDistModel(chain);
        Check("ZoneDist.chain.model", Same(got, want, out string where), "diverges from the model: " + where);
        Check("ZoneDist.chain.not-hop-count", got[0 * 64 + 4] == 3 && got[0 * 64 + 3] == 3 && got[0 * 64 + 2] == 2 && got[0 * 64 + 1] == 1,
            $"dist(0,1..4) = {got[1]},{got[2]},{got[3]},{got[4]}; expected 1,2,3,3");
        Check("ZoneDist.unused-zones", got[10 * 64 + 10] == 0 && got[10 * 64 + 11] == 255 && got[0 * 64 + 10] == 255,
            "zones beyond NumZones: diagonal 0, the rest 255");

        // Directedness: the row is the source. Zones[0] sees 1, Zones[1] does not see 0.
        var directed = new[] { B(0, 1), B(1) };
        byte[] gd = UELevelRuntime.BuildZoneDist(directed);
        Check("ZoneDist.directed", gd[0 * 64 + 1] == 1 && gd[1 * 64 + 0] == 255,
            $"dist(0,1)={gd[1]} dist(1,0)={gd[64]}; expected 1 and 255 (index i*64+j, 0x1039d75d)");

        // Without the self bit: squaring loses odd steps; the same model must still match.
        var noSelf = new[] { B(1), B(2), B(3), B(0) };
        Check("ZoneDist.no-self.model", Same(UELevelRuntime.BuildZoneDist(noSelf), ZoneDistModel(noSelf), out where), "without the self bit: " + where);

        // Random corpora (deterministic xorshift), up to 64 zones.
        ulong s = 0x9E3779B97F4A7C15UL;
        ulong Next() { s ^= s >> 12; s ^= s << 25; s ^= s >> 27; return s * 2685821657736338717UL; }
        for (int c = 0; c < 40; c++)
        {
            int nz = 1 + (int)(Next() % 64);
            var conn = new ulong[nz];
            ulong mask = nz == 64 ? ulong.MaxValue : (1UL << nz) - 1;
            for (int i = 0; i < nz; i++)
            {
                ulong r = Next() & Next() & Next();   // sparse graph
                conn[i] = (r & mask) | (c % 2 == 0 ? 1UL << i : 0);
            }
            Check($"ZoneDist.random{c}", Same(UELevelRuntime.BuildZoneDist(conn), ZoneDistModel(conn), out where), $"nz={nz}: " + where);
        }

        // A loaded zone referencing an unloaded one: the sign on which the bootstrap refuses to build.
        Check("ZoneDist.unloaded-ref", UELevelRuntime.ZoneConnectivityReferencesUnloadedZone(new[] { B(0, 1), B(1, 5) }) == 1
                                       && UELevelRuntime.ZoneConnectivityReferencesUnloadedZone(chain) == -1,
            "a zone reference >= NumZones is detected");
    }

    static bool Same(byte[] a, byte[] b, out string where)
    {
        for (int i = 0; i < 64 * 64; i++)
            if (a[i] != b[i]) { where = $"[{i / 64},{i % 64}] port {a[i]}, model {b[i]}"; return false; }
        where = "";
        return true;
    }
}
