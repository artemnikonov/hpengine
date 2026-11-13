// BSP queries (UModel bodies of Engine.dll): FindRegion/PointRegion, the x87 precision helpers,
// line checks (FastLineCheck, WorldLineCheck, TraceWorldLine/Box), PointCheck, node walks, surface
// and coplanar lookups. Box sweeps are in UEBspCollision.BoxSweep.cs. Fields live in UEBspCollision.cs.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UELib;
using UELib.Core;
using UELib.Types;
// Script vectors are the Core value type (UEVector).
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
public sealed partial class HeadlessBspModel
{
    public BspRegion FindRegion(Vector3 point)
    {
        if (_nodes.Length == 0) return BspRegion.None;
        int nodeIndex = 0;
        while (true)
        {
            Node node = _nodes[nodeIndex];
            Plane plane = _planes[nodeIndex];
            // PORT of shipped UModel::PointRegion (disasm Engine 0x1042c30e..0x1042c3c0): one side
            // per node, d >= 0 picks the front child, otherwise the back; child −1 ends the walk (with d == 0
            // and no front child shipped does NOT go back).
            // Distance is ((z·pZ + y·pY) + x·pX) − W on the x87 stack with a 64-bit mantissa (game logic,
            // appEnableFastMath(0)). A float expression in x, y, z order gave the opposite sign at five
            // mover points lying exactly on a plane in Lev_Tut3 (checked against the
            // real body under unicorn).
            bool front = PointRegionFront(point, (float)plane.X, (float)plane.Y, (float)plane.Z, (float)plane.W);
            float side = front ? 0f : -1f;
            int next = front ? node.Front : node.Back;
            if (next >= 0) { nodeIndex = next; continue; }
            // LEAF AND ZONE ARE TAKEN FROM THE SAME SIDE. Using `side >= 0 ? Leaf0 : Leaf1` (different side
            // indices for zone and leaf) was a defect, not a convention.
            //
            // Proven by EXECUTION, since the decompile does not determine this build's FBspNode layout
            // (Ghidra lost the types: the node index passes as float). Shipped `UModel::PointRegion`
            // (Engine.dll export ?PointRegion@UModel@@QBE...) was run under the emulator on the real Tut1
            // model; its back branch ends by reading `Nodes[last] + 0x38` (Engine.dll.c:114481), i.e.
            // iLeaf[0] on the BACK side, exactly like iZone[0] (+0x34) there.
            //
            // Measured: the shipped PointRegion and this body compared
            // over 1259 real Tut1 actor positions: zone 1259/1259, leaf 1259/1259 (the mixed-side form
            // matched only 39/1259 leaves).
            int zone = side >= 0f ? node.Zone1 : node.Zone0;
            int leaf = side >= 0f ? node.Leaf1 : node.Leaf0;
            int actorIndex = zone >= 0 && zone < _zoneActors.Length ? _zoneActors[zone] : 0;
            return new BspRegion(zone, leaf, actorIndex);
        }
    }

    // Sign of ((z·pZ + y·pY) + x·pX) − W under x87 rules with a 64-bit mantissa.
    static bool PointRegionFront(Vector3 p, float px, float py, float pz, float pw)
        => Pc64ChainSide((double)p.z * pz, (double)p.y * py, (double)p.x * px, pw) >= 0.0;   // NaN: C0, back

    // ── x87 plane chains of the collision bodies ─────────────────────────────────────────────────────
    // The bodies set no control word: precision is the caller's. Game logic runs PC64 (appEnableFastMath(0) in
    // appPlatformInit); the render window URender::PreRender (appEnableFastMath(1), Render 0x10b1eeb1) ..
    // PostRender (0x10b1efa9) runs PC24, where every x87 operation rounds to float (C# float in the same order).
    static bool Pc24 => UEWorldFrameProducer.FastMathWindow;

    static HP1.Core.X87Ext X87(double v) => HP1.Core.X87Ext.From(v);
    static HP1.Core.X87Ext R64(HP1.Core.X87Ext v) => HP1.Core.X87Ext.Round64(v);

    // PC64 register value of ((p0 + p1) + p2) − w, p_i float·float products (exact in double), as a double that
    // carries its sign (and NaN). The double chain errs by <= 3·2⁻⁵³ of the sum of magnitudes, the x87 one by
    // <= 3·2⁻⁶⁴: outside 1e-12 of it the signs agree, inside the chain is recomputed exactly at 64 bits.
    static bool Finite3(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);

    static double Pc64ChainSide(double p0, double p1, double p2, double w)
    {
        double d = ((p0 + p1) + p2) - w;
        if (!double.IsFinite(d) || Math.Abs(d) > (Math.Abs(p0) + Math.Abs(p1) + Math.Abs(p2) + Math.Abs(w)) * 1e-12)
            return d;
        return R64(R64(R64(X87(p0) + X87(p1)) + X87(p2)) - X87(w)).Sign;
    }

    // fstp dword of the same PC64 register value. The double chain plus the x87 chain err by < 1e-15 of the sum
    // of magnitudes; when both ends of that interval round to one nonzero float, so does the register (rounding is
    // monotone); otherwise exact (X87Ext carries the IEEE sign of an exact zero).
    static float Pc64ChainF(double p0, double p1, double p2, double w)
    {
        double d = ((p0 + p1) + p2) - w;
        if (!double.IsFinite(d)) return (float)d;     // non-finite operand: the value only reaches compares
        double e = (Math.Abs(p0) + Math.Abs(p1) + Math.Abs(p2) + Math.Abs(w)) * 1e-15;
        float lo = (float)(d - e), hi = (float)(d + e);
        if (lo == hi && lo != 0f) return lo;
        return R64(R64(R64(X87(p0) + X87(p1)) + X87(p2)) - X87(w)).ToFloat();
    }

    // Plane distance stored once (fstp dword): ((a·A + b·B) + c·C) − W with the operands in shipped order.
    // A non-finite coordinate runs the chain in X87Ext, which carries a NaN's sign and payload as the FPU does (C#
    // float/double pick a different NaN of two and make a platform NaN from inf·0).
    static float PlaneDistF(float a, float pa, float b, float pb, float c, float pc, float pw, bool pc24)
    {
        if (!float.IsFinite(a) || !float.IsFinite(b) || !float.IsFinite(c))
        {
            HP1.Core.X87Ext R(HP1.Core.X87Ext v) => pc24 ? HP1.Core.X87Ext.Round24(v) : R64(v);
            static HP1.Core.X87Ext F(float v) => HP1.Core.X87Ext.From(v);
            return R(R(R(R(F(a) * F(pa)) + R(F(b) * F(pb))) + R(F(c) * F(pc))) - F(pw)).ToFloat();
        }
        return pc24 ? ((a * pa + b * pb) + c * pc) - pw
                : Pc64ChainF((double)a * pa, (double)b * pb, (double)c * pc, pw);
    }

    // fld dword; fadd/fsub dword; fstp dword of two floats: one rounding, as C# for finite operands; a NaN or inf
    // operand goes through X87Ext (which NaN of two survives is the x87's, not the C# one).
    static float AddF(float a, float b)
        => float.IsFinite(a) && float.IsFinite(b) ? a + b : (HP1.Core.X87Ext.From(a) + HP1.Core.X87Ext.From(b)).ToFloat();
    static float SubF(float a, float b)
        => float.IsFinite(a) && float.IsFinite(b) ? a - b : (HP1.Core.X87Ext.From(a) - HP1.Core.X87Ext.From(b)).ToFloat();

    // f32 of a PC64 register value with t = num / (d1 − d2) (num, d1, d2 floats; the difference and the quotient
    // round at 64 bits):
    //   lerp (FastLineCheck split): ((u − v)·t) + v, u − v, the product and the sum in the register;
    //   scale (LineCheck worker crossing): u·t, the caller adds near in float.
    // The double form errs by < 1e-15 of |product| + |v|; when that interval rounds to one nonzero float the
    // register does too (monotone rounding), otherwise exact. A zero product is exact in both forms and carries the
    // same IEEE sign; a zero sum of a nonzero product is +0.
    static float Pc64ScaleF(float u, float num, float d1, float d2) => Pc64LerpF(u, 0f, false, num, d1, d2);

    // The same register chain in X87Ext at PC64 or PC24 (each operation rounded to 24 bits): the form for a non-finite
    // operand, where the NaN that comes out is the one the FPU keeps.
    static float X87LerpF(float u, float v, bool lerp, float num, float d1, float d2, bool pc24)
    {
        HP1.Core.X87Ext R(HP1.Core.X87Ext x) => pc24 ? HP1.Core.X87Ext.Round24(x) : R64(x);
        static HP1.Core.X87Ext F(float x) => HP1.Core.X87Ext.From(x);
        HP1.Core.X87Ext t = R(HP1.Core.X87Ext.Div64(F(num), R(F(d1) - F(d2))));
        HP1.Core.X87Ext p = R((lerp ? R(F(u) - F(v)) : F(u)) * t);
        return (lerp ? R(p + F(v)) : p).ToFloat();
    }
    static float Pc64LerpF(float u, float v, bool lerp, float num, float d1, float d2)
    {
        double td = num / ((double)d1 - d2);
        double prod = (lerp ? (double)u - v : u) * td;
        double m = lerp ? prod + v : prod;
        if (!double.IsFinite(m)) return X87LerpF(u, v, lerp, num, d1, d2, false);   // NaN/inf: x87 propagation
        double e = (Math.Abs(prod) + (lerp ? Math.Abs((double)v) : 0.0)) * 1e-15;
        float lo = (float)(m - e), hi = (float)(m + e);
        if (lo == hi && lo != 0f) return lo;
        if (prod == 0.0) return (float)m;
        HP1.Core.X87Ext t = HP1.Core.X87Ext.Div64(X87(num), R64(X87(d1) - X87(d2)));
        HP1.Core.X87Ext p = R64((lerp ? R64(X87(u) - X87(v)) : X87(u)) * t);
        return (lerp ? R64(p + X87(v)) : p).ToFloat();
    }

    // Shipped UModel::FastLineCheck (Engine.dll FUN_10429300): a pure BSP CSG
    // solidity walk. Unlike a triangle any-hit, it consults only the
    // per-node CSG structure (NodeFlags NF_NotCsg, bit 0x01) and NEVER touches
    // surface PolyFlags or NumVertices. Returns true when the whole segment
    // lies in empty space (the shipped script FastTrace/CanSee "clear" result).
    // This is the original's line-of-sight primitive; it differs materially
    // from a plain ray model for start-in-solid and PF_NotSolid geometry
    // (validated against the Engine.dll emulator).
    public bool FastLineCheckClear(Vector3 from, Vector3 to)
    {
        if (_nodes.Length == 0) return RootOutside != 0;
        return FastLineNode(0, from, to, RootOutside & 1);
    }

    // Shipped UModel::LineCheck zero-extent worker (Engine.dll FUN_104294c0),
    // world case (owner coords == null). A node contributes solidity iff it is
    // CSG: NumVertices > 0 and (NodeFlags & mask) == 0, where mask is 0x21 plus
    // the caller's ExtraNodeFlags — 0 for the shipped script Trace (execTrace,
    // Engine.dll.c:156119), 6 for AttachDecal. On a hit the raw result is the
    // last plane-crossing point and the tree node whose plane was crossed into
    // solid.
    public bool WorldLineCheckRaw(Vector3 from, Vector3 to, out Vector3 hitLoc, out int hitItem)
        => WorldLineCheckRaw(from, to, null, 0, out hitLoc, out hitItem);

    public bool WorldLineCheckRaw(Vector3 from, Vector3 to, BrushCoords? coords,
        out Vector3 hitLoc, out int hitItem)
        => WorldLineCheckRaw(from, to, coords, 0, out hitLoc, out hitItem);

    public bool WorldLineCheckRaw(Vector3 from, Vector3 to, BrushCoords? coords,
        int extraNodeFlags, out Vector3 hitLoc, out int hitItem)
    {
        // Bit 0x10 in ExtraNodeFlags is a LOUD REFUSAL, not a silent skip. In the worker it enables a
        // second, GLOBAL branch (on an empty leaf shipped writes DAT_105f7b54 = 1; on a solid one it
        // returns "clear" while that global is zero):
        // shipped writes `DAT_105f7b54 = 1`, and on a solid one returns "clear",
        // (Engine.dll.c:325988-325994). The global lives BETWEEN calls and is not modelled here. No
        // known caller passes this bit (Trace passes 0, AttachDecal 6), so the refusal is off the
        // path; but if it appears, silently computing the wrong thing is not allowed.
        if ((extraNodeFlags & 0x10) != 0)
            throw new NotSupportedException(
                $"UModel::LineCheck: ExtraNodeFlags=0x{extraNodeFlags:x} contains bit 0x10, "
                + "whose branch relies on the global DAT_105f7b54 and is not ported");
        hitLoc = to;
        hitItem = -1;
        if (_nodes.Length == 0) return false;
        return WorldLineNode(0, 0, to, from, RootOutside & 1, coords,
            DescentCsgMask(extraNodeFlags), StraddleCsgMask(extraNodeFlags),
            out hitLoc, out hitItem) == 0;
    }

    // Shipped UModel::LineCheck zero-extent path as a BspHit: runs the CSG
    // worker then applies LineCheck's float tail (Engine.dll.c:130428-130490) —
    // project the crossing point onto the ray, pull back 0.5 world units, clamp,
    // and orient the item node's plane against the trace direction. This is the
    // original's world line trace; it replaces a plain ray model for the
    // zero-extent script Trace path (validated 2807/2807 against the
    // Engine.dll emulator).
    public BspHit TraceWorldLine(Vector3 from, Vector3 to)
        => TraceWorldLine(from, to, null, 0);

    // Brush-model variant (Actor != null): the worker transforms each node
    // plane into the brush's ToWorld coords for the dists but records the RAW
    // model plane; the LineCheck tail (0x10429ec7) then maps that normal to
    // world as the axis combination before the shared pullback/orientation.
    public BspHit TraceWorldLine(Vector3 from, Vector3 to, BrushCoords? coords)
        => TraceWorldLine(from, to, coords, 0);

    // ExtraNodeFlags variant. Script Trace passes 0 (execTrace, Engine.dll.c:156119),
    // ADecal::execAttachDecal passes 6 (`push 6` at 0x10410a7b, Engine.dll.c:82899-82901), i.e.
    // NF_ShootThrough (0x2) and NF_NotVisBlocking (0x4): nodes with these bits stop being CSG-solid
    // and the decal ray passes through them.
    public BspHit TraceWorldLine(Vector3 from, Vector3 to, BrushCoords? coords, int extraNodeFlags)
    {
        if (!WorldLineCheckRaw(from, to, coords, extraNodeFlags, out Vector3 loc, out int item)
            || item < 0)
            return BspHit.None;
        Plane pl = _planes[item];
        var normal = new Vector3((float)pl.X, (float)pl.Y, (float)pl.Z);
        if (coords.HasValue) normal = coords.Value.NormalToWorld(normal);
        Vector3 delta = to - from;                                   // D, stored float (0x10429dc5..0x10429ddd)
        float time;
        Vector3 hitLoc, dir;
        if (delta.x == 0f && delta.y == 0f && delta.z == 0f)
        {
            // Start == End (no guard in shipped LineCheck): num/size² = 0/0 → NaN, 0.5/0 → +inf, NaN − inf → NaN, the
            // clamp's first fcom sets C0 → Time 0; Location = Start + 0·D; 1/dist = inf, inf·0 → NaN dir, the
            // orientation fcomp sets C0 → the normal flips.
            time = 0f;
            hitLoc = from + delta * 0f;
            return new BspHit(true, time, -normal, item, hitLoc);
        }
        if (Pc24)
        {
            // Render window: every x87 operation rounds to float, in disasm order. A NaN reaches Time, the clamp's
            // fcom sets C0 (Time 0) and the orientation fcomp sets C0 (flip); which NaN of two survives a C# float
            // operation here is not the x87's (the judge runs PC64 only).
            float size2 = delta.x * delta.x + delta.y * delta.y + delta.z * delta.z;
            time = ((loc.x - from.x) * delta.x + (loc.y - from.y) * delta.y + (loc.z - from.z) * delta.z) / size2;
            float dist = (float)Math.Sqrt(size2);
            time -= 0.5f / dist;
            if (!(time >= 0f)) time = 0f;
            else if (!(time < 1f)) time = 1f;
            hitLoc = from + delta * time;
            float inv = (float)(1.0 / dist);
            dir = new Vector3(inv * delta.x, inv * delta.y, inv * delta.z);
        }
        else
        {
            // PC64 (0x10429de0..0x10429ebe): Hz stays in the register, Hx/Hy are stored; size², num and their
            // quotient (stored as Time), dist = fsqrt(double size²) and Time − 0.5/dist stay in the register; the
            // clamped t is stored as Time and kept for Location = Start + f32(D·t). A NaN or inf runs the same chain
            // (X87Ext propagates it); the clamp is `fcom 0; test ah,1` (C0, NaN included: 0) then `fcom 1; test ah,1`.
            delta = new Vector3(SubF(to.x, from.x), SubF(to.y, from.y), SubF(to.z, from.z));
            HP1.Core.X87Ext s2 = UEVector.SizeSquared64(delta.x, delta.y, delta.z);
            HP1.Core.X87Ext hz = R64(X87(loc.z) - X87(from.z));
            HP1.Core.X87Ext num = R64(R64(R64(X87(SubF(loc.x, from.x)) * X87(delta.x)) + R64(X87(SubF(loc.y, from.y)) * X87(delta.y)))
                + R64(hz * X87(delta.z)));
            float time0 = HP1.Core.X87Ext.Div64(num, s2).ToFloat();
            double s2d = s2.ToDouble();
            HP1.Core.X87Ext t = R64(X87(time0) - HP1.Core.X87Ext.Div64(X87(0.5), HP1.Core.X87Ext.Sqrt64(X87(s2d))));
            if ((HP1.Core.X87Ext.Fcom(t, X87(0.0)) & HP1.Core.X87Ext.C0) != 0) t = X87(0.0);
            else if ((HP1.Core.X87Ext.Fcom(t, X87(1.0)) & HP1.Core.X87Ext.C0) == 0) t = X87(1.0);
            time = t.ToFloat();
            hitLoc = new Vector3(AddF(from.x, R64(t * X87(delta.x)).ToFloat()), AddF(from.y, R64(t * X87(delta.y)).ToFloat()),
                AddF(from.z, R64(t * X87(delta.z)).ToFloat()));
            // Orientation (0x10429f55..0x10429fc3): fsqrt again, fstp qword, 1.0 / dist in the register.
            HP1.Core.X87Ext inv = HP1.Core.X87Ext.Div64(X87(1.0), X87(HP1.Core.X87Ext.Sqrt64(X87(s2d)).ToDouble()));
            dir = new Vector3(R64(inv * X87(delta.x)).ToFloat(), R64(inv * X87(delta.y)).ToFloat(),
                R64(inv * X87(delta.z)).ToFloat());
        }
        // (−Dir | Normal) = ((z + y) + x), flip on C0 (below zero, NaN included): 0x10429fda..0x10429ffd.
        double facing = Pc24
            ? (-dir.z * normal.z + -dir.y * normal.y) + -dir.x * normal.x
            : Pc64ChainSide((double)-dir.z * normal.z, (double)-dir.y * normal.y, (double)-dir.x * normal.x, 0.0);
        if (!(facing >= 0.0)) normal = -normal;
        return new BspHit(true, time, normal, item, hitLoc);
    }

    // Shipped UModel::LineCheck EXTENT (swept box) path — the hull-box sweep the
    // player and walking NPCs use. Transcribed instruction-by-instruction from
    // Engine.dll machine code: LineCheck body 0x10429c80 (extent branch at
    // 0x1042a048), box setup 0x10427560, recursive sweep worker 0x1042a480,
    // plane clip 0x1042c050, FIntersectPlanes2 0x1042caf0, FBspNode::IsCsg
    // 0x1042cda0 — world model only (Actor == null, identity coords). Per leaf
    // it clips the segment against the hull planes Minkowski-expanded by the
    // extent, the six hull-box faces padded by 0.1 (the shipped code genuinely
    // SHRINKS the +X and +Y faces by 0.1 while padding the others outward —
    // fsub vs fadd at 0x1042b02d/0x1042b341), and bevel planes on silhouette
    // edges. Where the machine keeps a value on the x87 stack the port computes
    // in double; where it spills to a float slot (fstp dword) the port rounds —
    // that is the entire precision model. Validated against the real machine
    // code under the unicorn emulator.
    // BoxSweep leaf working planes (64 each): read strictly within _numPlanes, after being
    // written, so the set is taken from a per-thread pool without zeroing. A nested acquire (set
    // already handed out) gets a fresh one.
    internal sealed class SweepBuffers
    {
        public readonly float[] X = new float[64], Y = new float[64], Z = new float[64], W = new float[64];
        public readonly int[] Flags = new int[64], HullItem = new int[64];
        [ThreadStatic] static SweepBuffers t_free;
        public static SweepBuffers Acquire()
        {
            var b = t_free;
            if (b is null) return new SweepBuffers();
            t_free = null;
            return b;
        }
        public static void Release(SweepBuffers b) => t_free = b;
    }

    public BspHit TraceWorldBox(Vector3 from, Vector3 to, Vector3 extent)
        => TraceWorldBox(from, to, extent, null);

    // Brush-model variant (Actor != null): the caller passes the brush's
    // ToWorld coords; node and hull planes are transformed per node visit and
    // the hull-box face phase is skipped, exactly as the shipped worker does.
    public BspHit TraceWorldBox(Vector3 from, Vector3 to, Vector3 extent, BrushCoords? coords)
    {
        if (_nodes.Length == 0)
            return RootOutside == 0 ? new BspHit(true, 0f, new Vector3(0f, 0f, 1f), -1, from) : BspHit.None;
        var buffers = SweepBuffers.Acquire();
        var sweep = new BoxSweep(this, from, to, extent, coords, buffers);
        try { sweep.Visit(0, 0, RootOutside); }
        finally { SweepBuffers.Release(buffers); }
        if (!sweep.DidHit) return BspHit.None;
        // LineCheck tail (0x1042a141): pull the recorded entry time back by
        // 0.5/|delta| world units, clamp to [0,1]; a time that ROUNDS to 1.0f
        // reports clear (cmp eax, 0x3f800000 at 0x1042a22f).
        float t = LineCheckTailTime(from, to, sweep.ResultTime, Pc24);
        if (t == 1f) return BspHit.None;
        // Location = Start + f32(f32(End − Start)·Time) with the stored float Time (0x1042a17f..0x1042a229).
        return new BspHit(true, t, new Vector3(sweep.NormalX, sweep.NormalY, sweep.NormalZ), sweep.Item,
            from + (to - from) * t);
    }

    // 0x1042a0e2..0x1042a17c: dist = FVector::Size(End − Start) (Core 0x101196c0: (X·X + Y·Y) + Z·Z, fstp qword,
    // appSqrt, the caller's fstp dword 0x1042a10e); Time − 0.5f/dist in the register; `fcom 0; test ah,1` (0x1042a150):
    // C0 — below zero OR unordered (NaN) — gives 0, a hit; else `fcom 1; test ah,1`: C0 clear (not below 1) gives 1;
    // fstp dword. The same clamp as the zero-extent tail (WorldLineCheck). The body sets no control word: pc24 = the
    // render window, every operation in float.
    internal static float LineCheckTailTime(Vector3 from, Vector3 to, float resultTime, bool pc24)
    {
        float dx = to.x - from.x, dy = to.y - from.y, dz = to.z - from.z;
        // X87Ext carries NaN/±inf as the FPU does: dist 0 -> 0.5/0 = +inf -> Time − inf = −inf -> C0 -> 0. At PC24 the
        // quotient and the difference round to 24 bits in the register (its exponent is wider than a float's).
        HP1.Core.X87Ext t = pc24
            ? HP1.Core.X87Ext.Round24(X87(resultTime) - HP1.Core.X87Ext.Round24(HP1.Core.X87Ext.Div64(X87(0.5), X87(UEVector.Size24(dx, dy, dz)))))
            : R64(X87(resultTime) - HP1.Core.X87Ext.Div64(X87(0.5), X87(UEVector.Size64(dx, dy, dz))));
        if ((HP1.Core.X87Ext.Fcom(t, X87(0.0)) & HP1.Core.X87Ext.C0) != 0) return 0f;
        if ((HP1.Core.X87Ext.Fcom(t, X87(1.0)) & HP1.Core.X87Ext.C0) == 0) return 1f;
        return t.ToFloat();
    }

    public readonly struct BspPointHit
    {
        public bool Hit { get; }
        public Vector3 Location { get; }
        public Vector3 Normal { get; }
        public int Item { get; }
        public BspPointHit(bool Hit, Vector3 Location, Vector3 Normal, int Item)
        {
            this.Hit = Hit;
            this.Location = Location;
            this.Normal = Normal;
            this.Item = Item;
        }
    }

    // Shipped UModel::PointCheck (body 0x104271d0): is the box centered at
    // `point` (or the point itself for zero extent) inside this model's
    // solids? Zero extent runs the point-solidity walk inlined in the
    // dispatcher; a real extent runs the point worker (0x10427630) — hull
    // planes Minkowski-expanded by the extent, best (shallowest) penetration
    // recorded with the 1.02-scaled push-out location. `coords` = the brush
    // actor's ToWorld coords (Actor != null form), null = world model.
    // This is the primitive behind spawn placement (FindSpot direct branch)
    // and mover encroachment (FCollisionHash::ActorEncroachmentCheck).
    public BspPointHit PointCheck(Vector3 point, Vector3 extent, BrushCoords? coords)
    {
        if (_nodes.Length == 0)
            return new BspPointHit(RootOutside == 0, point, Vector3.zero, -1);
        if (extent.x == 0f && extent.y == 0f && extent.z == 0f)
        {
            // Point walk (0x1042732e): per-node transformed plane when the
            // model is a brush; side/outside updates are the CSG solidity
            // rules; Item = 2*lastNode + side.
            int iNode = 0, side = 0, prev = 0, outside = RootOutside & 1;
            while (iNode != -1)
            {
                Node node = _nodes[iNode];
                Plane pl = _planes[iNode];
                float px = (float)pl.X, py = (float)pl.Y, pz = (float)pl.Z, pw = (float)pl.W;
                if (coords.HasValue)
                    (px, py, pz, pw) = coords.Value.TransformNodePlanePoint(px, py, pz, pw);
                // ((z·Z + y·Y) + x·X) − W in the register, fcomp 0, back on C0|C3 (NaN included):
                // disasm 0x104273dd..0x10427401. PC64 or PC24 by the caller's window.
                double dist = Pc24
                    ? ((point.z * pz + point.y * py) + point.x * px) - pw
                    : Pc64ChainSide((double)point.z * pz, (double)point.y * py, (double)point.x * px, pw);
                bool csg = node.NumVertices != 0 && (node.NodeFlags & 0x21) == 0;
                prev = iNode;
                if (!(dist > 0.0))
                {
                    side = 0;
                    outside = outside != 0 && !csg ? 1 : 0;
                    iNode = node.Back;
                }
                else
                {
                    side = 1;
                    outside = outside == 0 && !csg ? 0 : 1;
                    iNode = node.Front;
                }
            }
            return new BspPointHit(outside == 0, point, Vector3.zero, prev * 2 + side);
        }
        var w = new PointBox(this, point, extent, coords);
        int clear = w.Visit(0, 0, RootOutside & 1);
        return new BspPointHit(clear == 0, w.ResultLocation, w.ResultNormal, w.ResultItem);
    }

    // FBspNode::IsCsg(ExtraFlags) — Engine.dll 0x1042cda0 (thunk_FUN_1042cda0,
    // Engine.dll.c:14028): NumVertices != 0 && ((ExtraFlags | 0x21) & NodeFlags) == 0.
    // 0x21 = NF_NotCsg (0x01) | NF_IsNew (0x20) is always in the mask; the caller's ExtraNodeFlags
    // are ADDED to it. The mask arrives precomputed because the worker has TWO different ones (see
    // WorldLineNode).
    private static bool IsCsgSolid(Node node, int mask)
        => node.NumVertices != 0 && (node.NodeFlags & mask) == 0;

    // Descent mask: the worker INLINES IsCsg into both descending branches and computes it as
    // `((byte)ExtraNodeFlags & 0xef | 0x21)` (Engine.dll.c:326007 and 326017): bit 0x10 is CUT OUT.
    private static int DescentCsgMask(int extraNodeFlags) => (extraNodeFlags & 0xef) | 0x21;

    // Straddle mask: there the worker calls the REAL FBspNode::IsCsg with the RAW ExtraNodeFlags
    // (four calls to thunk_FUN_1042cda0), i.e. without `& 0xef`. For ExtraNodeFlags 0 and 6 both
    // masks agree; they differ exactly on bit 0x10, which is refused here (see WorldLineCheckRaw).
    private static int StraddleCsgMask(int extraNodeFlags) => (extraNodeFlags | 0x21) & 0xff;

    // PRECISION (disasm 0x104294fc..0x10429839). Each side distance is one x87 chain stored once (fstp dword):
    // world branch 0x10429692..0x104296de ((z·Z + x·X) + y·Y) − W, brush branch 0x10429592..0x1042968c
    // ((z·Z + y·Y) + x·X) − W over the transformed plane. The straddle keeps far − near and
    // t = near / (far − near) in the register (0x104297f4..0x10429803) and stores dir·t per component
    // (0x10429815..0x10429836) before the float add of near. PC64 or PC24 by the caller's window (Pc24).
    // Compares (fcomp + test ah): front descent leaves on C0|C3, i.e. also on NaN (0x104296e2..0x10429702);
    // the straddle test needs "not below 0.001" (C0 clear, NaN below); the straddle near side is the back one on
    // C0|C3, NaN included (0x10429891..0x104298ae).
    private int WorldLineNode(int iNode, int iHitNode, Vector3 far, Vector3 near, int outside,
        BrushCoords? coords, int descentMask, int straddleMask, out Vector3 hitLoc, out int hitItem)
    {
        bool pc24 = Pc24;
        hitLoc = near;
        hitItem = iHitNode;
        while (true)
        {
            Node node = default;
            int cur = 0;
            float nearSide = 0f, farSide = 0f;
            while (true) // front descent
            {
                if (iNode < 0)
                {
                    if (outside != 0) return outside;      // reached empty leaf: clear
                    hitLoc = near; hitItem = iHitNode; return 0; // solid leaf: hit
                }
                cur = iNode;
                node = _nodes[iNode];
                Plane pl = _planes[iNode];
                float px = (float)pl.X, py = (float)pl.Y, pz = (float)pl.Z, pw = (float)pl.W;
                if (coords.HasValue)
                {
                    // Brush trace (Engine.dll.c:325974): per-node transformed
                    // plane; the recorded hit keeps the raw node index.
                    (px, py, pz, pw) = coords.Value.TransformNodePlaneLine(px, py, pz, pw);
                    nearSide = PlaneDistF(near.z, pz, near.y, py, near.x, px, pw, pc24);
                    farSide = PlaneDistF(far.z, pz, far.y, py, far.x, px, pw, pc24);
                }
                else
                {
                    nearSide = PlaneDistF(near.z, pz, near.x, px, near.y, py, pw, pc24);
                    farSide = PlaneDistF(far.z, pz, far.x, px, far.y, py, pw, pc24);
                }
                if (!(nearSide > -0.001f) || !(farSide > -0.001f)) break;
                outside |= IsCsgSolid(node, descentMask) ? 1 : 0;
                iNode = node.Front;
            }
            if (0.001f <= nearSide || 0.001f <= farSide) // straddle
            {
                Vector3 dir = new(SubF(near.x, far.x), SubF(near.y, far.y), SubF(near.z, far.z));
                Vector3 crossing;
                if (pc24 && Finite3(dir) && Finite3(near) && float.IsFinite(nearSide) && float.IsFinite(farSide))
                {
                    float t = nearSide / (farSide - nearSide);
                    crossing = new Vector3(dir.x * t + near.x, dir.y * t + near.y, dir.z * t + near.z);
                }
                else if (pc24)
                    crossing = new Vector3(AddF(X87LerpF(dir.x, 0f, false, nearSide, farSide, nearSide, true), near.x),
                        AddF(X87LerpF(dir.y, 0f, false, nearSide, farSide, nearSide, true), near.y),
                        AddF(X87LerpF(dir.z, 0f, false, nearSide, farSide, nearSide, true), near.z));
                else
                    crossing = new Vector3(AddF(Pc64ScaleF(dir.x, nearSide, farSide, nearSide), near.x),
                        AddF(Pc64ScaleF(dir.y, nearSide, farSide, nearSide), near.y),
                        AddF(Pc64ScaleF(dir.z, nearSide, farSide, nearSide), near.z));
                bool solid = IsCsgSolid(node, straddleMask);
                int iVar8, subOutside;
                if (!(nearSide > 0f))
                {
                    iVar8 = 0;
                    subOutside = outside == 0 || solid ? 0 : 1;
                }
                else
                {
                    iVar8 = 1;
                    subOutside = outside == 0 && !solid ? 0 : 1;
                }
                int nearChild = iVar8 == 1 ? node.Front : node.Back;
                if (WorldLineNode(nearChild, iHitNode, crossing, near, subOutside, coords,
                        descentMask, straddleMask, out hitLoc, out hitItem) == 0)
                    return 0;
                outside = iVar8 == 1
                    ? (outside != 0 && !solid ? 1 : 0)
                    : (outside == 0 && !solid ? 0 : 1);
                iNode = iVar8 == 1 ? node.Back : node.Front;
                near = crossing;
                iHitNode = cur;
                continue;
            }
            // pure back descent
            outside &= IsCsgSolid(node, descentMask) ? 0 : 1;
            iNode = node.Back;
        }
    }

    // PRECISION (disasm 0x10429324..0x10429403): each side is one x87 chain ((z·Z + y·Y) + x·X) − W stored once
    // (fstp dword), far first; the side is the SIGN BIT of the stored float (`test eax,eax; setge`), so −0 is back.
    // The split keeps t = far / (far − near) and (near − far)·t + far in the register, one store per component.
    // PC64 or PC24 by the caller's window (Pc24). A NaN side keeps the sign bit x87 NaN propagation gives it
    // (PlaneDistF/X87Ext): an endpoint of indefinites (0xFFC00000) is a negative NaN, back.
    private bool FastLineNode(int nodeIndex, Vector3 near, Vector3 far, int outside)
    {
        bool pc24 = Pc24;
        while (nodeIndex >= 0)
        {
            Node node = _nodes[nodeIndex];
            Plane plane = _planes[nodeIndex];
            int notCsg = node.NodeFlags & 1;
            float px = (float)plane.X, py = (float)plane.Y, pz = (float)plane.Z, pw = (float)plane.W;
            float farSide = PlaneDistF(far.z, pz, far.y, py, far.x, px, pw, pc24);
            float nearSide = PlaneDistF(near.z, pz, near.y, py, near.x, px, pw, pc24);
            int nearFront = BitConverter.SingleToInt32Bits(nearSide) >= 0 ? 1 : 0;
            int farFront = BitConverter.SingleToInt32Bits(farSide) >= 0 ? 1 : 0;
            if (farFront != nearFront)
            {
                Vector3 mid;
                if (pc24 && !(Finite3(near) && Finite3(far) && float.IsFinite(farSide) && float.IsFinite(nearSide)))
                    mid = new Vector3(
                        X87LerpF(near.x, far.x, true, farSide, farSide, nearSide, true),
                        X87LerpF(near.y, far.y, true, farSide, farSide, nearSide, true),
                        X87LerpF(near.z, far.z, true, farSide, farSide, nearSide, true));
                else if (pc24)
                {
                    float t = farSide / (farSide - nearSide);
                    mid = new Vector3(
                        (near.x - far.x) * t + far.x,
                        (near.y - far.y) * t + far.y,
                        (near.z - far.z) * t + far.z);
                }
                else
                    mid = new Vector3(
                        Pc64LerpF(near.x, far.x, true, farSide, farSide, nearSide),
                        Pc64LerpF(near.y, far.y, true, farSide, farSide, nearSide),
                        Pc64LerpF(near.z, far.z, true, farSide, farSide, nearSide));
                int nearChild = nearFront != 0 ? node.Front : node.Back;
                if (!FastLineNode(nearChild, mid, near, ((nearFront ^ outside) & notCsg) ^ nearFront))
                    return false;
                near = mid;
            }
            outside = ((farFront ^ outside) & notCsg) ^ farFront;
            nodeIndex = farFront != 0 ? node.Front : node.Back;
        }
        return outside != 0;
    }

    // Node polygon: centroid of its vertices and the normal of its plane.
    //
    // DATA, not a decision: same category as the public SurfFlags(node) and SurfaceMaterial(node).
    // Lets a climb scenario reach a REAL climbable wall of the map rather than an invented
    // coordinate: the wall position is a property of the level and must come from the level.
    public bool TryNodePolygon(int node, out Vector3 centroid, out Vector3 normal)
    {
        centroid = default; normal = default;
        if (node < 0 || node >= _nodes.Length) return false;
        Node n = _nodes[node];
        // The plane is taken from THE NODE ITSELF. `Node.Plane` is not an index into `_planes` but
        // iPlane, the link to the next COPLANAR node (proven by execution: the shipped
        // FindCoplanarSurface walk converges on this link). Using `_planes[n.Plane]`
        // with an `n.Plane < 0` reject would drop every node without a coplanar neighbour (4313 of 7344
        // nodes on Tut1).
        if (n.NumVertices <= 0) return false;
        double cx = 0, cy = 0, cz = 0;
        for (int i = 0; i < n.NumVertices; i++)
        {
            int vi = n.VertPool + i;
            if (vi < 0 || vi >= _vertices.Length) return false;
            Vector3 p = _points[_vertices[vi]];
            cx += p.x; cy += p.y; cz += p.z;
        }
        centroid = new Vector3((float)(cx / n.NumVertices), (float)(cy / n.NumVertices),
                               (float)(cz / n.NumVertices));
        Plane pl = _planes[node];
        normal = new Vector3((float)pl.X, (float)pl.Y, (float)pl.Z);
        return true;
    }

    public int SurfFlags(int node)
    {
        if (node < 0 || node >= _nodes.Length) return 0;
        int surf = _nodes[node].Surf;
        return surf >= 0 && surf < _surfFlags.Length ? _surfFlags[surf] : 0;
    }

    // DATA, not a decision: same category as SurfFlags(node) above. The node's iSurf and a point
    // by index; AttachDecal needs both: it takes the surface from the node and the surface base
    // point from Points.
    public int NodeSurf(int node) => _nodes[node].Surf;

    public Vector3 Point(int index) => _points[index];

    // Vertex count of the node polygon and the link to the next COPLANAR node: exactly the two
    // fields FindCoplanarSurface branches on. Public so the campaign measurement asks the model
    // for them instead of reflecting into private fields.
    public int NodeVertexCount(int node) => _nodes[node].NumVertices & 0xff;

    public int NodePlaneChain(int node) => _nodes[node].Plane;

    // Vertex k of the node polygon: Points[Verts[iVertPool + k].pVertex]. The decal binding
    // FDecal.Nodes (FUN_104127c0) is built from it, and ClipDecal cuts the piece by it.
    public Vector3 NodeVertex(int node, int k) => _points[_vertices[_nodes[node].VertPool + k]];

    // The node's EBspNodeFlags: ExtraNodeFlags uses them to decide whether the node is CSG-solid.
    public int NodeFlags(int node) => _nodes[node].NodeFlags & 0xff;

    // UModel::FindCoplanarSurface (Engine.dll 0x10412510; name from the unwind string of its own
    // body, Engine.dll.c Catch_10412708). Walks the chain of COPLANAR nodes (iPlane) and returns
    // the iSurf of the first node whose POLYGON contains the point, or -1 when the chain ends.
    //
    // TAIL RECURSION IS UNROLLED INTO A LOOP, faithfully: shipped calls itself as its LAST action
    // and returns eax directly (0x104126db-0x104126f3). The sixth argument (a depth counter) is
    // never read per the DISASSEMBLY: the only access to [ebp+0x1c] is `inc ecx; push ecx`.
    // Shipped has no depth limit (V11: negative checked against disasm, not the decompile).
    //
    // PRECISION IS THE CALLER'S WINDOW. The body does not set the control word; its only callers are
    // the two calls in execAttachDecal (0x10410bfa/0x104118dd), and the only live route to the native
    // is ActorShadow.Update from the actor draw (Render.dll 0x10b304be), between URender::PreRender
    // (appEnableFastMath(1) at 0x10b1eeb1 -> PC24) and PostRender (appEnableFastMath(0) at 0x10b1efa9);
    // Render.dll has no other appEnableFastMath call. Under PC24 every x87 op rounds to 24 bits, i.e.
    // exactly C# float, in disasm operation order: two components of the edge side normal are stored
    // (`fstp dword` 0x104125f0/0x10412608), the third stays on the stack — under PC24 the same value.
    // The game-tick route (Decal.AttachToSurface, PC64) is not taken by the campaign (no placed or
    // spawned Decal/Scorch; AttachDecalNative refuses it loudly), so no PC64 copy is kept.
    // Judge: the shipped body under unicorn with FPCW = PC24.
    public int FindCoplanarSurface(int iNode, Vector3 point)
    {
        while (true)
        {
            if (iNode == -1) return -1;
            Node node = _nodes[iNode];
            int count = node.NumVertices & 0xff;
            if (count != 0)
            {
                BspSurfRecord surf = _surfRec[node.Surf];
                Vector3 normal = _vectors[surf.VNormal];
                Vector3 previous = _points[_vertices[node.VertPool + count - 1]];
                float last = 0f;
                bool inside = true;
                for (int i = 0; i < count; i++)
                {
                    Vector3 current = _points[_vertices[node.VertPool + i]];
                    float dx = current.x - previous.x, dy = current.y - previous.y, dz = current.z - previous.z;
                    float sz = dy * normal.x - dx * normal.y;
                    float sy = dx * normal.z - dz * normal.x;
                    float sx = dz * normal.y - dy * normal.z;
                    float origin = sx * current.x + (current.y * sy + current.z * sz);
                    float probe = sx * point.x + (point.y * sy + point.z * sz);
                    float side = probe - origin;
                    if ((side < 0f && last > 0f) || (side > 0f && last < 0f)) { inside = false; break; }
                    previous = current;
                    last = side;
                }
                if (inside) return node.Surf;
            }
            iNode = node.Plane;
        }
    }

    // FCheckResult::BspNode(MaxExtent) (Engine.dll 0x103ff1b0, worker
    // 0x103febd0): an extent LineCheck records a collision-hull plane in Item,
    // which is not necessarily the polygon node whose surface owns that plane.
    // Walk the node's iPlane chain and select the normal-aligned polygon which
    // contains Hit.Location (within MaxExtent). APawn::Mount then reads that
    // polygon's PF_SpecialPoly bit; consulting SurfFlags(Item) directly can
    // therefore attribute a bevel/hull hit to the wrong authored surface.
    public int? HitSurfaceFlags(int item, Vector3 hitLocation, Vector3 hitNormal,
        float tolerance, BrushCoords? toLocal)
    {
        int node = BspNode(item, hitLocation, hitNormal, tolerance, toLocal);
        return node >= 0 ? SurfFlags(node) : null;
    }

    // The FCheckResult::BspNode choice itself: -1 means no node (Item = INDEX_NONE, or with
    // tolerance != 0 no polygon in the chain contains the point). FCheckResult::GetSurf
    // (0x103ff1b0) reads it too: the TraceTexture surface comes FROM HERE, not from the node whose
    // plane the trace crossed.
    public int BspNode(int item, Vector3 hitLocation, Vector3 hitNormal,
        float tolerance, BrushCoords? toLocal)
    {
        if (item < 0 || item >= _nodes.Length) return -1;
        if (toLocal.HasValue)
        {
            hitLocation = toLocal.Value.TransformPoint(hitLocation);
            hitNormal = toLocal.Value.TransformVector(hitNormal);
        }

        int lastNormalAligned = -1;
        for (int nodeIndex = item; nodeIndex != -1;)
        {
            if (nodeIndex < 0 || nodeIndex >= _nodes.Length)
                throw new InvalidDataException($"BspNode iPlane out of range: {nodeIndex}");
            Node node = _nodes[nodeIndex];
            Plane plane = _planes[nodeIndex];
            float nx = (float)plane.X, ny = (float)plane.Y, nz = (float)plane.Z;
            float alignment = (float)(((double)hitNormal.x * nx
                + (double)hitNormal.y * ny) + (double)hitNormal.z * nz);
            if (node.NumVertices != 0 && alignment >= 0.99f)
            {
                lastNormalAligned = nodeIndex;
                if (ContainsHit(node, nx, ny, nz, hitLocation, tolerance))
                    return nodeIndex;
            }
            if (node.Plane == nodeIndex)
                throw new InvalidDataException($"BspNode iPlane self-cycle: {nodeIndex}");
            nodeIndex = node.Plane;
        }

        // The shipped helper returns the last normal-aligned node only for a
        // zero tolerance. Mount passes max(abs(cylinder extent)) > 0, but keep
        // the helper's complete contract for direct regression tests.
        return tolerance == 0f ? lastNormalAligned : -1;
    }

    private bool ContainsHit(Node node, float nx, float ny, float nz,
        Vector3 hit, float tolerance)
    {
        int count = node.NumVertices;
        int first = node.VertPool;
        if (first < 0 || count < 0 || first > _vertices.Length - count)
            throw new InvalidDataException($"BspNode vertex pool range: {first}+{count}");
        int previousPoint = _vertices[first + count - 1];
        if (previousPoint < 0 || previousPoint >= _points.Length)
            throw new InvalidDataException($"BspNode point out of range: {previousPoint}");
        Vector3 previous = _points[previousPoint];
        float winding = 1f;

        for (int i = 0; i < count; i++)
        {
            int pointIndex = _vertices[first + i];
            if (pointIndex < 0 || pointIndex >= _points.Length)
                throw new InvalidDataException($"BspNode point out of range: {pointIndex}");
            Vector3 current = _points[pointIndex];
            float dx = current.x - previous.x;
            float dy = current.y - previous.y;
            float dz = current.z - previous.z;
            // Normal x edge, in the exact component order visible in
            // FUN_103febd0 (Engine.dll.c:300999-301040).
            float ex = (float)((double)ny * dz - (double)nz * dy);
            float ey = (float)((double)nz * dx - (double)nx * dz);
            float ez = (float)((double)nx * dy - (double)ny * dx);
            float size2 = (float)(((double)ex * ex + (double)ey * ey) + (double)ez * ez);
            if (size2 >= 1e-8f)
            {
                double inv = 1.0 / Math.Sqrt(size2);
                ex = (float)(ex * inv); ey = (float)(ey * inv); ez = (float)(ez * inv);
            }
            float side = (float)((((double)hit.x - current.x) * ex
                + ((double)hit.y - current.y) * ey)
                + ((double)hit.z - current.z) * ez);
            if ((double)side * winding < -(double)tolerance)
            {
                if (i != 0) return false;
                winding = -1f;
            }
            previous = current;
        }
        return true;
    }
}
}
