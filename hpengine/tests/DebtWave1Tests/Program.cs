// Wave-1 debt tests (D-14 PHYSICS), no game files needed.
//
// Checking the three "not verified" places against the disasm (Engine.asm) found NO player-visible
// divergences: no Core changes. The tests PIN the verified shipped semantics so it cannot be broken
// silently (red mutations — audit/debt_wave1_redtest_core.sh):
//   decoration tumble — UELevelRuntime.ProcessLanded (called by processLanded of falling/projectile physics);
//   ledge machinery   — UELevelRuntime.PhysWalking (PHYS_Walking tick), NPC pawn branches.
// Interpolation point bodies are not covered (entry only through PhysInterpolating with a manager, the
// UpdateCamera events and MoveActor) — marked "form from the source, no proof here".
//
// Geometry comes from the Core seams LineTraceHook/WalkMoveHook: the test answers EVERY trace in order and
// records it (start, end, extent, flags). The expected probe order, their ends, extents and property
// writes follow the shipped body (addresses at each test) and are computed in the test independently of port code.
// Classes are synthetic named UClasses (ClassIsA compares names along the Super chain).
using HP1.UEScript;
using UELib;
using UELib.Core;
using Vector3 = HP1.UEScript.UEVector;
using LineCheckHit = HP1.UEScript.UELevelRuntime.LineCheckHit;

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
    static bool Eq(Vector3 a, Vector3 b) => a.x == b.x && a.y == b.y && a.z == b.z;
    static Vector3 V(UEObject o, string p) => UEInterpreter.AsVector3(o.GetProp(p));
    static float F(UEObject o, string p) => UEInterpreter.AsFloat(o.GetProp(p));
    static int I(UEObject o, string p) => UEInterpreter.ToInt(o.GetProp(p));

    static int Main()
    {
        HP1.Core.UEClassRegistry.Shared ??= new HP1.Core.UEClassRegistry();

        DecorationTumble();
        LedgeAvoid();
        LedgeSidestep();
        LedgeDetection();

        Console.WriteLine($"DebtWave1Tests: passed {_pass}, failed {_fail}");
        return _fail == 0 ? 0 : 1;
    }

    static UClass Class(string name, UClass super = null)
    {
        var c = new UClass { Name = new UName(name) };
        if (super is not null) c.Super = super;
        return c;
    }

    static readonly UClass ActorClass = Class("Actor");
    static readonly UClass DecorationClass = Class("Decoration", ActorClass);
    static readonly UClass PawnClass = Class("Pawn", ActorClass);

    static (UEInterpreter interp, UELevelRuntime rt, UEObject level) World()
    {
        var interp = new UEInterpreter();
        var rt = new UELevelRuntime();
        var level = new UEObject();
        level.Props["TimeDilation"] = 1f;
        level.Props["NetMode"] = (byte)0;
        interp.RestoreWorldLevelInfo(level);
        return (interp, rt, level);
    }

    record struct Probe(Vector3 Start, Vector3 End, Vector3 Extent, int Flags);

    static readonly LineCheckHit Clear = new(false, 1f, Vector3.zero, Vector3.zero, null, -1);
    static LineCheckHit Hit(float time, Vector3 normal) => new(true, time, Vector3.zero, normal, null, -1);

    // Trace answers in order; an extra trace fails the test (probe order is part of the comparison).
    static List<Probe> Script(UELevelRuntime rt, string test, params LineCheckHit[] answers)
    {
        var log = new List<Probe>();
        rt.LineTraceHook = (_, _, s, e, x, f) =>
        {
            log.Add(new Probe(s, e, x, f));
            if (log.Count > answers.Length)
            {
                Check(test + ".extra-trace", false, $"trace #{log.Count} beyond the scenario: {S(s)} -> {S(e)} ext {S(x)} flags {f}");
                return Clear;
            }
            return answers[log.Count - 1];
        };
        return log;
    }

    static void ProbeIs(string test, List<Probe> log, int i, Vector3 start, Vector3 end, Vector3 ext, int flags)
    {
        if (i >= log.Count) { Check(test, false, $"trace #{i + 1} missing (total {log.Count})"); return; }
        Probe p = log[i];
        Check(test, Eq(p.Start, start) && Eq(p.End, end) && Eq(p.Extent, ext) && p.Flags == flags,
            $"trace #{i + 1}: {S(p.Start)} -> {S(p.End)} ext {S(p.Extent)} flags {p.Flags}; "
            + $"expected {S(start)} -> {S(end)} ext {S(ext)} flags {flags}");
    }

    // ── AActor::processLanded, ADecoration branch (Engine.dll.c:33909-34110; disasm 0x103ed415-0x103ee159) ──
    // numLandings (+0x268) < 5. Centre probe: Start = (L.x−0, L.y−0, L.z − CH·0.8), End = (L.x, L.y,
    // L.z − (CH+CR+8)), flags 7, extent 0; `cmp [Hit.Actor], 0; jne` (0x103ed692) — support under the centre ->
    // no tumble. Otherwise e = GetCylinderExtent (0x103ed6a9), corner extent (e.x·0.5, e.y·0.5, e.z·0.5+e.z·0.5);
    // four corners in order (+,+) 0x103ed964, (−,+) 0x103edb74, (−,−) 0x103edd82, (+,−) 0x103edf90:
    // Start (L.x ± CR·0.5, L.y ± CR·0.5, L.z+0), End — the same with z−8. SingleLineCheck answers 1 when the path
    // is CLEAR. Condition (0x103edfb1-0x103edfd0): t1+t2+t3+t4 > 1 && t1+t3 != 0 && t2+t4 != 0 ->
    // numLandings+1 (0x103edfeb), Velocity = (((t1+t4)−t2)−t3)·k, (((t1+t2)−t3)−t4)·k, 0.5·k),
    // k = 2·(−V.z < 30 ? 30 : (−V.z < CR+30 ? −V.z : CR+30)) (0x103ee03d-0x103ee0cb), and return WITHOUT Landed.
    static void DecorationTumble()
    {
        const float r = 20f, h = 30f;
        Vector3 loc = new(100f, 200f, 300f);
        UEObject Deco(float vz, int landings)
        {
            var a = new UEObject { Class = DecorationClass };
            a.Props["Location"] = loc;
            a.Props["CollisionRadius"] = r;
            a.Props["CollisionHeight"] = h;
            a.Props["Velocity"] = new Vector3(0f, 0f, vz);
            a.Props["numLandings"] = landings;
            a.Props["Physics"] = (byte)0;
            return a;
        }
        Vector3 ext = new(r * 0.5f, r * 0.5f, h * 0.5f + h * 0.5f);
        Vector3 C(float sx, float sy, float dz) => new(r * sx + loc.x, r * sy + loc.y, loc.z + dz);
        void Corners(string t, List<Probe> log)
        {
            ProbeIs(t + ".centre", log, 0, new Vector3(loc.x - 0f, loc.y - 0f, loc.z - h * 0.8f),
                new Vector3(loc.x - 0f, loc.y - 0f, loc.z - (h + r + 8f)), Vector3.zero, 7);
            ProbeIs(t + ".corner1", log, 1, C(0.5f, 0.5f, 0f), C(0.5f, 0.5f, -8f), ext, 7);
            ProbeIs(t + ".corner2", log, 2, C(-0.5f, 0.5f, 0f), C(-0.5f, 0.5f, -8f), ext, 7);
            ProbeIs(t + ".corner3", log, 3, C(-0.5f, -0.5f, 0f), C(-0.5f, -0.5f, -8f), ext, 7);
            ProbeIs(t + ".corner4", log, 4, C(0.5f, -0.5f, 0f), C(0.5f, -0.5f, -8f), ext, 7);
        }
        static float K(float vz, float cr)
        {
            float nv = vz * -1f, lim = cr + 30f;
            float c = 30f <= nv ? (nv < lim ? nv : lim) : 30f;
            return c * 2f;
        }
        LineCheckHit Corner(int free) => free == 1 ? Clear : Hit(0.5f, new Vector3(0f, 0f, 1f));

        // Sets of free corners (t1..t4) and the outcome.
        var cases = new (string name, int t1, int t2, int t3, int t4, float vz, bool tumble)[]
        {
            ("front-pair", 1, 1, 0, 0, -40f, true),     // t1+t3=1, t2+t4=1
            ("three-free", 1, 1, 1, 0, -10f, true),     // k: −V.z < 30 -> 30
            ("all-free", 1, 1, 1, 1, -500f, true),      // k: CR+30
            ("diag13", 1, 0, 1, 0, -40f, false),        // t2+t4 = 0
            ("diag24", 0, 1, 0, 1, -40f, false),        // t1+t3 = 0
            ("one-free", 0, 0, 0, 1, -40f, false),      // sum <= 1
        };
        foreach (var c in cases)
        {
            string t = "Tumble." + c.name;
            var (interp, rt, level) = World();
            var a = Deco(c.vz, 2);
            var log = Script(rt, t, Clear, Corner(c.t1), Corner(c.t2), Corner(c.t3), Corner(c.t4));
            var events = new List<string>();
            UELifecycle.EventProbe = (o, n, _) => { if (ReferenceEquals(o, a)) events.Add(n); };
            try { rt.ProcessLanded(interp, a, new Vector3(0f, 0f, 1f), level); }
            catch (Exception ex) { Check(t + ".exception", false, ex.GetType().Name + ": " + ex.Message); }
            finally { UELifecycle.EventProbe = null; rt.LineTraceHook = null; }
            Check(t + ".probes", log.Count == 5, $"probes {log.Count}, expected 5");
            Corners(t, log);
            if (c.tumble)
            {
                float k = K(c.vz, r);
                var expV = new Vector3((float)(((c.t1 + c.t4) - c.t2) - c.t3) * k,
                    (float)(((c.t1 + c.t2) - c.t3) - c.t4) * k, k * 0.5f);
                Check(t + ".velocity", Eq(V(a, "Velocity"), expV), $"Velocity {S(V(a, "Velocity"))}, expected {S(expV)}");
                Check(t + ".landings", I(a, "numLandings") == 3, $"numLandings {I(a, "numLandings")}, expected 3");
                Check(t + ".no-landed", !events.Contains("Landed"), "the roll exits before the Landed event");
            }
            else
            {
                Check(t + ".velocity", Eq(V(a, "Velocity"), new Vector3(0f, 0f, c.vz)), $"no roll; Velocity {S(V(a, "Velocity"))}");
                Check(t + ".landings", I(a, "numLandings") == 0, $"without a roll numLandings is reset; {I(a, "numLandings")}");
                Check(t + ".landed", events.Contains("Landed"), "without a roll — Landed event");
            }
        }
        // Support under the centre: only the centre probe, no tumble.
        {
            const string t = "Tumble.centre-supported";
            var (interp, rt, level) = World();
            var a = Deco(-40f, 2);
            var log = Script(rt, t, Hit(0.5f, new Vector3(0f, 0f, 1f)));
            try { rt.ProcessLanded(interp, a, new Vector3(0f, 0f, 1f), level); }
            catch (Exception ex) { Check(t + ".exception", false, ex.GetType().Name + ": " + ex.Message); }
            finally { rt.LineTraceHook = null; }
            Check(t + ".probes", log.Count == 1, $"probes {log.Count}, expected 1 (center)");
            Check(t + ".landings", I(a, "numLandings") == 0, $"numLandings {I(a, "numLandings")}");
        }
        // numLandings >= 5: no probes, the counter is reset.
        {
            const string t = "Tumble.landings-cap";
            var (interp, rt, level) = World();
            var a = Deco(-40f, 5);
            var log = Script(rt, t);
            try { rt.ProcessLanded(interp, a, new Vector3(0f, 0f, 1f), level); }
            catch (Exception ex) { Check(t + ".exception", false, ex.GetType().Name + ": " + ex.Message); }
            finally { rt.LineTraceHook = null; }
            Check(t + ".probes", log.Count == 0, $"probes {log.Count}, expected 0");
            Check(t + ".landings", I(a, "numLandings") == 0, $"numLandings {I(a, "numLandings")}");
        }
    }

    // ── APawn::physWalking, NPC pawn movement substep (Engine.dll.c:135442-136195; disasm 0x103e71d1-0x103e9293:
    // 18 SingleLineCheck calls (vtable +0xc4), 2 MoveActor (+0x8c), one appFrand 0x103e8328, events
    // MayFall 0x103e8006 and Falling 0x103e9124). Scenario: pawn without a zone (gravity 0 -> sign −1), friction
    // 0, Acceleration (AR,0,0) — calcVelocity gives (AR·dt,0,0), clamped to GroundSpeed·DesiredSpeed = 400 ->
    // Velocity (400,0,0); dt 0.04 <= 0.05 — one substep, delta = (400·0.04, 0, 0).
    // After the move — a step down (flags 7): answer "floor of the current Base within the hover band" — nothing moves.
    const float R = 20f, H = 40f, MaxStep = 25f, Gs = 400f, Ds = 1f, Dt = 0.04f, Mt = 5f;
    static readonly Vector3 Loc = new(1000f, 2000f, 3000f);
    static readonly Vector3 Cyl = new(R, R, H);
    static readonly Vector3 Up = new(0f, 0f, 1f);
    static readonly LineCheckHit Hover = new(true, 2f / (MaxStep + 2f), Vector3.zero, Up, null, -1);

    static UEObject Npc(UEObject level, bool avoidLedges, bool stopAtLedges)
    {
        var a = new UEObject { Class = PawnClass };
        a.Props["Location"] = Loc;
        a.Props["Velocity"] = Vector3.zero;
        a.Props["Acceleration"] = new Vector3(100000f, 0f, 0f);
        a.Props["AccelRate"] = 100000f;
        a.Props["GroundSpeed"] = Gs;
        a.Props["DesiredSpeed"] = Ds;
        a.Props["CollisionRadius"] = R;
        a.Props["CollisionHeight"] = H;
        a.Props["MaxStepHeight"] = MaxStep;
        a.Props["MinHitWall"] = -1f;
        a.Props["MoveTimer"] = Mt;
        a.Props["bAvoidLedges"] = avoidLedges;
        a.Props["bStopAtLedges"] = stopAtLedges;
        a.Props["bCanJump"] = false;
        a.Props["bMovable"] = true;
        a.Props["Physics"] = (byte)1;
        a.Props["Base"] = level;
        return a;
    }

    static List<Vector3> Moves(UELevelRuntime rt)
    {
        var moves = new List<Vector3>();
        rt.WalkMoveHook = (_, pawn, d) =>
        {
            moves.Add(d);
            Vector3 l = V(pawn, "Location");
            pawn.Props["Location"] = new Vector3(l.x + d.x, l.y + d.y, l.z + d.z);
            return (1f, Vector3.zero, null);
        };
        return moves;
    }

    // Common head: A, B, C (ledge found). Returns (delta, lookAhead, Aend).
    static (Vector3 delta, Vector3 la, Vector3 aEnd) Head(string t, List<Probe> log, bool avoidLedges)
    {
        var delta = new Vector3(Gs * Dt, 0f, 0f);
        // 0x103e70f5-…: fStack_fc = accelDir·CR, without bAvoidLedges (0x25c & 0x400000) — ·0.5.
        float laX = avoidLedges ? 1f * R : 1f * R * 0.5f;
        var la = new Vector3(laX, 0f, 0f);
        // A (0x103e729b): End = (delta + L) + la, Start = L, extent 0, flags 6.
        var aEnd = new Vector3((delta.x + Loc.x) + la.x, (delta.y + Loc.y) + la.y, (delta.z + Loc.z) + la.z);
        ProbeIs(t + ".A", log, 0, Loc, aEnd, Vector3.zero, 6);
        // B (0x103e73ee): depth max(CH+CR+|delta|+4, CH+MaxStep+4) down from the end of A.
        float depth = Math.Max(H + R + delta.x + 4f, H + MaxStep + 4f);
        ProbeIs(t + ".B", log, 1, aEnd, new Vector3(aEnd.x, aEnd.y, -depth + aEnd.z), Vector3.zero, 6);
        return (delta, la, aEnd);
    }

    // bAvoidLedges, D hits (0x103e7ae0-0x103e7936): adjust = (N.x, N.y, 0)·(GS·DS·sub·−1);
    // MoveTimer −0.25 (fsub dword 0x10474eac, 0x103e7924) or −1 with bStopAtLedges (0x103e7915).
    // Then (!bCanJump, not a "wall") L/M/N: L hits -> N does not run; the move is adjust.
    static void LedgeAvoid()
    {
        foreach (bool stop in new[] { false, true })
        {
            string t = "Ledge.avoid" + (stop ? ".stop" : "");
            var (interp, rt, level) = World();
            var a = Npc(level, avoidLedges: true, stopAtLedges: stop);
            var dN = new Vector3(-0.6f, 0.8f, 0f);
            var lN = new Vector3(-1f, 0f, 0f);
            var log = Script(rt, t, Clear, Clear, Clear, Hit(0.5f, dN), Hit(0.5f, lN), Hover);
            var moves = Moves(rt);
            try { rt.PhysWalking(interp, a, Dt); }
            catch (Exception ex) { Check(t + ".exception", false, ex.GetType().Name + ": " + ex.Message); }
            finally { rt.LineTraceHook = null; rt.WalkMoveHook = null; }
            var (delta, la, aEnd) = Head(t, log, true);
            float thick = Math.Min(R * 0.5f, 14f);
            ProbeIs(t + ".C", log, 2, aEnd, new Vector3(aEnd.x, aEnd.y, -(MaxStep + 4f) + aEnd.z), new Vector3(thick, thick, H), 6);
            // D (0x103e77fc): Start = (L + accelDir·|delta| + la) + g·(CH+6); End = (g·(CH+6) + L) − accelDir·2·CR.
            var edge = new Vector3(delta.x * 1f + Loc.x + la.x, Loc.y, Loc.z);
            ProbeIs(t + ".D", log, 3, new Vector3(edge.x, edge.y, edge.z + -(H + 6f)),
                new Vector3(Loc.x - (R + R), Loc.y, -(H + 6f) + Loc.z), Vector3.zero, 6);
            float k = Gs * Ds * Dt * -1f;
            var adjust = new Vector3(dN.x * k, dN.y * k, 0f);
            // L (0x103e8409): End = (adjust + L) + la, Start = L, cylinder extent.
            ProbeIs(t + ".L", log, 4, Loc, new Vector3((adjust.x + Loc.x) + la.x, (adjust.y + Loc.y) + la.y, (adjust.z + Loc.z) + la.z), Cyl, 6);
            Check(t + ".probes", log.Count == 6, $"traces {log.Count}, expected 6 (A B C D L + step down)");
            Check(t + ".move", moves.Count == 1 && Eq(moves[0], adjust),
                $"move {(moves.Count > 0 ? S(moves[0]) : "none")}, expected {S(adjust)}");
            float expMt = stop ? -1f : Mt - 0.25f;
            Check(t + ".MoveTimer", F(a, "MoveTimer") == expMt, $"MoveTimer {F(a, "MoveTimer"):R}, expected {expMt:R}");
        }
    }

    // Without bAvoidLedges, D misses: sidestep (0x103e7b74-0x103e7c0c): perp = (deltaDir.y, −deltaDir.x, 0),
    // side = perp·(min(DS,0.8)·GS·sub), written to adjust BEFORE E; E (L -> L+side, cylinder) clear and F
    // (L+side -> +g·(MaxStep+6)) clear -> adjust = −side, the "tried" flag is NOT set; otherwise the flag
    // is set, adjust = side. "Wall" -> I/J/K (0x103e811d, 0x103e81c3): I clear, J clear ->
    // appFrand: sub+sub <= frand -> MoveTimer −0.1 (0x103e834c), else −1 (0x103e8340). L/M/N — only
    // without the "tried" flag.
    static void LedgeSidestep()
    {
        var cases = new (string name, bool eHit, float frand)[]
        {
            ("flip", false, 0.5f),
            ("keep", true, 0.5f),
            ("jitter-stop", false, 0.05f),
        };
        foreach (var c in cases)
        {
            string t = "Ledge.side." + c.name;
            var (interp, rt, level) = World();
            var a = Npc(level, avoidLedges: false, stopAtLedges: false);
            rt.FrandHook = () => HP1.Core.X87Ext.From(c.frand);
            var blocked = Hit(0.5f, new Vector3(-1f, 0f, 0f));
            var answers = c.eHit
                ? new[] { Clear, Clear, Clear, Clear, blocked, Clear, Clear, Hover }
                : new[] { Clear, Clear, Clear, Clear, Clear, Clear, Clear, Clear, blocked, Hover };
            var log = Script(rt, t, answers);
            var moves = Moves(rt);
            try { rt.PhysWalking(interp, a, Dt); }
            catch (Exception ex) { Check(t + ".exception", false, ex.GetType().Name + ": " + ex.Message); }
            finally { rt.LineTraceHook = null; rt.WalkMoveHook = null; rt.FrandHook = null; }
            var (delta, la, aEnd) = Head(t, log, false);
            // 0x103e7b30..0x103e7be8: min(DS, 0.8f)·GS·sub stays in the register (PC64), rounded to float once.
            var X = (Func<float, HP1.Core.X87Ext>)HP1.Core.X87Ext.From;
            float s = HP1.Core.X87Ext.Round64(HP1.Core.X87Ext.Round64(X(Math.Min(Ds, 0.8f)) * X(Gs)) * X(Dt)).ToFloat();
            var side = new Vector3(s * 0f, (1f * -1f) * s, 0f);
            Vector3 down = new(0f, 0f, -(MaxStep + 6f));
            ProbeIs(t + ".E", log, 4, Loc, new Vector3(side.x + Loc.x, side.y + Loc.y, Loc.z + 0f), Cyl, 6);
            int i = 5;
            if (!c.eHit)
            {
                var eEnd = new Vector3(side.x + Loc.x, side.y + Loc.y, Loc.z + 0f);
                ProbeIs(t + ".F", log, i++, eEnd, new Vector3(down.x + eEnd.x, down.y + eEnd.y, down.z + eEnd.z), Cyl, 6);
            }
            // I: End = deltaDir·(|delta|+CR) + L.
            var iEnd = new Vector3(1f * (delta.x + R) + Loc.x, Loc.y, Loc.z);
            ProbeIs(t + ".I", log, i++, Loc, iEnd, Cyl, 6);
            ProbeIs(t + ".J", log, i++, iEnd, new Vector3(iEnd.x, iEnd.y, down.z + iEnd.z), Cyl, 6);
            Vector3 adjust = c.eHit ? side : new Vector3(-side.x, -side.y, -side.z);
            if (!c.eHit)
                ProbeIs(t + ".L", log, i++, Loc, new Vector3((adjust.x + Loc.x) + la.x, (adjust.y + Loc.y) + la.y, (adjust.z + Loc.z) + la.z), Cyl, 6);
            Check(t + ".probes", log.Count == i + 1, $"traces {log.Count}, expected {i + 1}");
            Check(t + ".move", moves.Count == 1 && Eq(moves[0], adjust),
                $"move {(moves.Count > 0 ? S(moves[0]) : "none")}, expected {S(adjust)}");
            float expMt = Dt + Dt <= c.frand ? Mt - 0.1f : -1f;
            Check(t + ".MoveTimer", F(a, "MoveTimer") == expMt, $"MoveTimer {F(a, "MoveTimer"):R}, expected {expMt:R}");
        }
    }

    // A ledge is declared only when C is clear (0x103e7423-0x103e7431, 0x103e75ac je 0x103e86a0): floor close
    // under the end of A (B hits, shallow normal, within reach) -> neither C nor D…N; B misses, C hits ->
    // no ledge. The move is the original delta. MoveTimer untouched.
    static void LedgeDetection()
    {
        foreach (bool bMiss in new[] { false, true })
        {
            string t = "Ledge.none" + (bMiss ? ".C-hit" : ".B-floor");
            var (interp, rt, level) = World();
            var a = Npc(level, avoidLedges: false, stopAtLedges: false);
            var floor = Hit(0.5f, Up);
            var log = bMiss ? Script(rt, t, Clear, Clear, floor, Hover) : Script(rt, t, Clear, floor, Hover);
            var moves = Moves(rt);
            try { rt.PhysWalking(interp, a, Dt); }
            catch (Exception ex) { Check(t + ".exception", false, ex.GetType().Name + ": " + ex.Message); }
            finally { rt.LineTraceHook = null; rt.WalkMoveHook = null; }
            var (delta, _, _) = Head(t, log, false);
            int expected = bMiss ? 4 : 3;
            Check(t + ".probes", log.Count == expected, $"traces {log.Count}, expected {expected}");
            Check(t + ".move", moves.Count == 1 && Eq(moves[0], delta),
                $"move {(moves.Count > 0 ? S(moves[0]) : "none")}, expected {S(delta)}");
            Check(t + ".MoveTimer", F(a, "MoveTimer") == Mt, $"MoveTimer {F(a, "MoveTimer"):R}");
        }
    }
}
