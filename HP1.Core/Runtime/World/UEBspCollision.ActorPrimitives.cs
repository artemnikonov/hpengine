// Actor collision primitives: cylinder, oriented-cylinder and box traces, the collision hash replica,
// primitive kinds, mesh hash fields and boxes, cylinder extents and FindSpot math.
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
// Shipped UPrimitive::LineCheck (Engine.dll.c:35132, VA 0x103fa760): the swept
// box/cylinder-vs-actor-cylinder line check behind every actor trace (each
// per-actor test FCollisionHash::ActorLineCheck runs via the primitive's vtable
// slot 0x5c). It Minkowski-expands the actor cylinder by the tracer's extent
// (R' = Extent.X + Radius, H' = Extent.Z + Height), AABB-rejects, clips the Z
// slab with top/bottom endcap normals, then solves a quadratic ray-vs-infinite-
// cylinder in XY. Axis-aligned actor cylinders only — creatures and pawns never
// rotate their collision cylinder; UOrientedCylinder re-bases into this body.
//
// x87 written from the disassembly 0x103fa79c..0x103fadae: the body sets no control word. Game logic runs
// PC64: a value stays in the register (64-bit mantissa) until an fst/fstp dword rounds it to float. Inside the
// render window (UEWorldFrameProducer.FastMathWindow: ActorShadow.Update and HUD script traces) PC24 rounds every
// operation to 24 bits. Judge: the real body under unicorn (both control
// words), compared bit for bit.
public static class ActorCylinderTrace
{
    static HP1.Core.X87Ext X(float v) => HP1.Core.X87Ext.From(v);
    static bool Lt(HP1.Core.X87Ext a, HP1.Core.X87Ext b) => a.CompareTo(b) < 0;

    // .rdata constants: 0x1047a77c (9.99999905e-09f), 0x10475a78 (1e-8f); 0x10473814 0.001f, 0x1047a780 -0.1f.
    static readonly float VerticalA = BitConverter.Int32BitsToSingle(0x322bcc76);
    static readonly float NormalMin = BitConverter.Int32BitsToSingle(0x322bcc77);

    // start/end = the tracer sweep, extent = its half-extents; cx/cy/cz = the
    // target actor's Location, zoff = its collision Z-offset (field 0x1d0, zero
    // for stock cylinders), radius/height = its CollisionRadius/CollisionHeight.
    public static ActorHit LineCheck(Vector3 start, Vector3 end, Vector3 extent,
        float cx, float cy, float cz, float zoff, float radius, float height)
    {
        // One register rounding: 64 bits, or 24 in the render window (sums and products of floats, quotients and roots
        // of floats never sit within 2^-64 of a 24-bit midpoint, so rounding at 64 first is innocuous there).
        bool pc24 = UEWorldFrameProducer.FastMathWindow;
        HP1.Core.X87Ext R(HP1.Core.X87Ext v) => pc24 ? X(HP1.Core.X87Ext.Round64(v).ToFloat()) : HP1.Core.X87Ext.Round64(v);
        // GetCylinderExtent(Owner) → (radius, radius, height) for a stock cylinder.
        HP1.Core.X87Ext sx = X(start.x), sy = X(start.y), sz = X(start.z), ex = X(end.x), ey = X(end.y), ez = X(end.z);
        float hp = R(X(extent.z) + X(height)).ToFloat();        // fstp [ebp-0x28]
        float ry = R(X(extent.y) + X(radius)).ToFloat();        // fstp [ebp+0xc]: Y uses Extent.Y
        HP1.Core.X87Ext rpReg = R(X(extent.x) + X(radius));
        float rp = rpReg.ToFloat();                             // fst [ebp-0x1c]: the register keeps rpReg
        // AABB reject (returns miss). 0x103fa7b6: rpReg + Loc.X stays in the register; the other three bounds
        // read the stored floats.
        HP1.Core.X87Ext bound = R(rpReg + X(cx));
        if (!Lt(sx, bound) && !Lt(ex, bound)) return default;
        bound = R(X(cx) - X(rp));
        if (!Lt(bound, sx) && !Lt(bound, ex)) return default;
        bound = R(X(ry) + X(cy));
        if (!Lt(sy, bound) && !Lt(ey, bound)) return default;
        bound = R(X(cy) - X(ry));
        if (!Lt(bound, sy) && !Lt(bound, ey)) return default;
        HP1.Core.X87Ext zc = R(X(zoff) + X(cz));                // 0x103fa8a7: zoff + Loc.Z in the register
        float top = R(zc + X(hp)).ToFloat();
        if (!(start.z < top) && !(end.z < top)) return default;
        float bottom = R(zc - X(hp)).ToFloat();
        if (!(bottom < start.z || bottom < end.z)) return default; // fully below → miss

        float tmin = 0f, tmax = 1f;   // [ebp-0x14] / [ebp-0x18]
        var normal = Vector3.zero;
        // Z slab: (plane − S.z) / (E.z − S.z) in the register, one fstp.
        float Slab(float plane) => HP1.Core.X87Ext.Div64(R(X(plane) - sz), R(ez - sz)).ToFloat();
        if (start.z < top) { if (top < end.z) tmax = Slab(top); }
        else if (end.z < top) { tmin = Slab(top); normal = new Vector3(0f, 0f, 1f); }
        if (bottom < start.z) { if (end.z < bottom) tmax = Slab(bottom); }
        else if (bottom < end.z) { tmin = Slab(bottom); normal = new Vector3(0f, 0f, -1f); }

        // XY quadratic 0x103fa9ff..0x103faa6a: rel and dir stay in the register (dir is also stored, fst);
        // a, b, c and disc are each stored once as float, and everything after reads those floats.
        HP1.Core.X87Ext relx = R(sx - X(cx)), rely = R(sy - X(cy));
        HP1.Core.X87Ext dxr = R(ex - sx), dyr = R(ey - sy);
        float dirx = dxr.ToFloat(), diry = dyr.ToFloat();
        float a = R(R(dyr * dyr) + R(dxr * dxr)).ToFloat();
        HP1.Core.X87Ext bs = R(R(dyr * rely) + R(dxr * relx));
        float b = R(bs + bs).ToFloat();
        float c = R(R(R(rely * rely) + R(relx * relx)) - R(X(rp) * X(rp))).ToFloat();
        float disc = R(R(X(b) * X(b)) - R(R(X(c) * X(a)) * X(4f))).ToFloat();

        if (!(c < 1f) || !(bottom < start.z) || !(start.z < top))   // 0x103faa6d reads the float c
        {
            if (disc < 0f) return default;
            bool blocked;
            if (!(a < VerticalA))
            {
                // sq = appSqrt(disc) (fst qword keeps the register); half = 0.5f / a; both times in the register.
                HP1.Core.X87Ext sq = R(HP1.Core.X87Ext.Sqrt64(HP1.Core.X87Ext.From((double)disc)));
                HP1.Core.X87Ext half = R(HP1.Core.X87Ext.Div64(X(0.5f), X(a)));
                HP1.Core.X87Ext texit = R(R(sq - X(b)) * half);
                if (Lt(texit, X(tmax))) tmax = texit.ToFloat();
                HP1.Core.X87Ext tenter = HP1.Core.X87Ext.From(0) - R(R(sq + X(b)) * half);
                if (!Lt(tenter, X(tmin)))
                {
                    tmin = tenter.ToFloat();   // fst: the register keeps tenter
                    // 0x103fac20..0x103facf0: hit point x/y = float(float(dir·t) + S); the normal is the point minus
                    // Location with z forced to 0 (0x103fac92), scaled by 1.0 (qword) / appSqrt (fstp qword).
                    float px = R(X(R(X(dirx) * tenter).ToFloat()) + sx).ToFloat();
                    float py = R(X(R(X(diry) * tenter).ToFloat()) + sy).ToFloat();
                    float nx = R(X(px) - X(cx)).ToFloat(), ny = R(X(py) - X(cy)).ToFloat();
                    HP1.Core.X87Ext m = R(R(X(nx) * X(nx)) + R(X(ny) * X(ny)));
                    if (!Lt(m, X(NormalMin)))
                    {
                        double s = R(HP1.Core.X87Ext.Sqrt64(HP1.Core.X87Ext.From(m.ToDouble()))).ToDouble();
                        HP1.Core.X87Ext inv = R(HP1.Core.X87Ext.Div64(HP1.Core.X87Ext.From(1.0), HP1.Core.X87Ext.From(s)));
                        nx = R(inv * X(nx)).ToFloat(); ny = R(inv * X(ny)).ToFloat();
                    }
                    normal = new Vector3(nx, ny, 0f);
                }
                blocked = tmin < tmax;
            }
            else
            {
                blocked = c < 0f; // vertical sweep, start inside the radius
            }
            if (!blocked) return default;
            // 0x103fad22..0x103fadae: t = tmin − 0.001f clamped to [0, 1] in the register; Time is an fst of it,
            // the location reads the register t.
            HP1.Core.X87Ext t = R(X(tmin) - X(0.001f));
            if (t.Sign < 0) t = X(0f); else if (!Lt(t, X(1f))) t = X(1f);
            float dyf = R(ey - sy).ToFloat(), dzf = R(ez - sz).ToFloat();
            var loc = new Vector3(R(X(R(X(dirx) * t).ToFloat()) + sx).ToFloat(),
                R(X(R(X(dyf) * t).ToFloat()) + sy).ToFloat(),
                R(R(t * X(dzf)) + sz).ToFloat());
            return new ActorHit(true, t.ToFloat(), loc, normal);
        }
        // Start already inside and moving toward the centre → penetration push-out (0x103faaa3..0x103fab7d):
        // (dir.x·rel.x + dir.y·rel.y) + 0·rel.z with the float dir and the register rel, against -0.1f.
        if (Lt(R(R(X(diry) * rely) + R(X(dirx) * relx)), X(-0.1f)))
            return new ActorHit(true, 0f, start, new Vector3(relx.ToFloat(), rely.ToFloat(), 0f).normalized);
        return default;
    }

}

// Shipped UBox::LineCheck (Engine.dll.c:118460) delegates to the engine FBox
// line worker (FUN_103fc980). CollideType=2 actors use a local box with
// half-extents (CollisionRadius, CollisionWidth-or-Radius, CollisionHeight),
// transformed by the actor's full Rotation. The moving world AABB is first
// conservatively transformed into that local frame, then the worker performs
// a slab sweep with the shipped 0.001 entry-time backoff. This is the live
// Lev_Tut1 path for BlockAll/BlockPlayer and box-shaped Trigger/CutScene actors.
// UOrientedCylinder::PointCheck (export thunk 0x10303350 -> body 0x103FB7A0):
// transforms the point and the extent into the actor's LOCAL frame
// (AActor::ToLocal via vtable +0x98) and delegates to UPrimitive::PointCheck —
// the z slab plus the XY CIRCLE, confirmed against the real body (hit at radius
// 49.50, miss at 50.91 for R=40 extent=10, where a box test would still hit).
//
// ONLY the hit decision is ported, and deliberately so: the shipped body also
// writes a Location, but probing the real body shows it is independent of the
// query point (`ext=(25,5,7)` returns (-25,0,0) for points at 30 AND 45 along
// X), i.e. an offset rather than a position. Its meaning is not established, no
// consumer reads it — ActorEncroachmentOverlaps uses the boolean alone — so it
// is left unclaimed rather than invented.
public static class ActorOrientedCylinder
{
    // THE WIDTH IS REQUIRED HERE: not symmetry with the cylinder, but the disasm.
    //
    // The oracle corpus must write a non-zero CollisionWidth to +0x1d0: a corpus with zero width
    // only proves the `CollisionWidth == 0` case. With width in the corpus, the port WITHOUT it
    // gives 176 mismatches per 2000 queries.
    //
    // The body (0x103FB7A0) transforms the POINT into the owner's local frame and IMMEDIATELY
    // ADDS BACK its Location (`param_2[0x3f..0x41]`, i.e. 0xfc/0x100/0x104), then delegates to
    // UPrimitive::PointCheck with THE SAME Owner. So it compares
    //     fVar1 = (Owner+0x1d0 + Owner+0x104) - pz
    // where `pz` is the already re-based coordinate. The offset lies along the tilted cylinder's
    // LOCAL Z axis, not the world one.
    //
    // Operation order is kept verbatim (re-base, then subtract): in float this is NOT the same as
    // `width - lp.z`, and the question is decided exactly at the slab boundary.
    public static bool PointCheckHit(Vector3 point, Vector3 extent, Vector3 centre,
        float radius, float width, float height, UERotator rotation)
    {
        BrushCoords local = BrushCoords.Identity;
        local.DivRotator(rotation.Pitch, rotation.Yaw, rotation.Roll);
        Vector3 lp = local.TransformVector(point - centre);
        Vector3 le = new(
            Math.Abs(local.X.x) * extent.x + Math.Abs(local.X.y) * extent.y + Math.Abs(local.X.z) * extent.z,
            Math.Abs(local.Y.x) * extent.x + Math.Abs(local.Y.y) * extent.y + Math.Abs(local.Y.z) * extent.z,
            Math.Abs(local.Z.x) * extent.x + Math.Abs(local.Z.y) * extent.y + Math.Abs(local.Z.z) * extent.z);
        // Re-basing the point onto the owner's Location is a step of
        // UOrientedCylinder::PointCheck itself (Engine.dll.c:94547-94549).
        float px = lp.x + centre.x, py = lp.y + centre.y, pz = lp.z + centre.z;
        // UPrimitive::PointCheck (Engine.dll.c:24204-24210): z slab then XY
        // circle, both with the <= clear edges.
        float dx = centre.x - px, dy = centre.y - py;
        float dz = (width + centre.z) - pz;
        float hz = le.z + height;
        float rr = le.x + radius;
        return hz * hz > dz * dz && rr * rr > dx * dx + dy * dy;
    }
}

public static class ActorBoxTrace
{
    // THE BODY'S BOX IS THE PRIMITIVE'S ANSWER, NOT AN ACTOR FIELD. UBox::LineCheck/PointCheck
    // (0x103fe620/0x103fe590) obtain it via this->vtable[+0x60](Box, Owner, 0) (call [edx+0x60]
    // at 0x103fe67d/0x103fe5e2) and pass it to the worker. For UBox that is
    // GetCollisionBoundingBox 0x103fe130: local (±R, ±(W or R), ±H); UMesh/USkeletalMesh have their
    // own slot (0x103b84d0/0x1041b8b0): a local box from the mesh bounds
    // (CylinderExtent.MeshCollisionLocalBox). Checked by executing the real body with this = mesh.
    public static ActorHit LineCheck(Vector3 start, Vector3 end, Vector3 extent,
        Vector3 centre, float radius, float width, float height, UERotator rotation)
    {
        Vector3 half = new(radius, width != 0f ? width : radius, height);
        return LineCheck(start, end, extent, centre, -half, half, rotation);
    }

    public static ActorHit LineCheck(Vector3 start, Vector3 end, Vector3 extent,
        Vector3 centre, Vector3 boxLo, Vector3 boxHi, UERotator rotation)
    {
        BrushCoords local = BrushCoords.Identity;
        // AActor::ToLocal (Engine.dll 0x1031bac0), which FUN_103fc980 calls
        // through vtable +0x98, composes the inverse rotator coordinates.
        local.DivRotator(rotation.Pitch, rotation.Yaw, rotation.Roll);
        local.Origin = centre;
        // FVector::TransformPointBy inlined (0x103fca63-0x103fcac3, 0x103fcaf0-0x103fcb50): Start/End − Origin stay in
        // the register (no float store of the difference).
        Vector3 localStart = PointBy64(start, local);
        Vector3 localEnd = PointBy64(end, local);
        Vector3 localExtent = new(
            Math.Abs(local.X.x) * extent.x + Math.Abs(local.X.y) * extent.y + Math.Abs(local.X.z) * extent.z,
            Math.Abs(local.Y.x) * extent.x + Math.Abs(local.Y.y) * extent.y + Math.Abs(local.Y.z) * extent.z,
            Math.Abs(local.Z.x) * extent.x + Math.Abs(local.Z.y) * extent.y + Math.Abs(local.Z.z) * extent.z);

        // FUN_103fc980 first runs FUN_103fc1d0 at Start. A trace which begins
        // overlapped blocks only while moving deeper (the End penetration is
        // strictly greater); moving out is allowed. This also supplies the
        // closest-face normal for Time=0 contacts instead of a zero slab
        // normal. The 0.003 separations are the shipped helper's six guards.
        if (OverlapPenetration(localStart, localExtent, boxLo, boxHi,
                out float startPenetration, out Vector3 startNormal))
        {
            if (OverlapPenetration(localEnd, localExtent, boxLo, boxHi,
                    out float endPenetration, out _)
                && startPenetration < endPenetration)
            {
                Vector3 clamped = new(
                    Math.Clamp(localStart.x, boxLo.x, boxHi.x),
                    Math.Clamp(localStart.y, boxLo.y, boxHi.y),
                    Math.Clamp(localStart.z, boxLo.z, boxHi.z));
                return MakeHit(0f, clamped, startNormal, local);
            }
            return default;
        }

        // No start overlap: six-face boundary test (FUN_103fc980, disasm 0x103fce44-0x103fdbad;
        // Engine.dll.c 300152-300400). Expanded box Min' = Min − Extent, Max' = Max + Extent
        // (0x103fce51-0x103fceca, each component rounded to float). Time starts at 1.0 (0x103fce4a);
        // faces go in order −X, +X, −Y, +Y, −Z, +Z, and each passing face OVERWRITES
        // Time/Location/Normal, later faces comparing against the new Time. Hit means Time != 1.0
        // bitwise (0x103fdba6).
        Vector3 boxMax = new(boxHi.x + localExtent.x, boxHi.y + localExtent.y, boxHi.z + localExtent.z);
        Vector3 boxMin = new(boxLo.x - localExtent.x, boxLo.y - localExtent.y, boxLo.z - localExtent.z);
        float time = 1f;
        Vector3 localLocation = Vector3.zero, localNormal = Vector3.zero;
        // Face arguments: (a, b) are the operands of distance dist = a − b, (c, d) the operands of
        // Δ = c − d, then the face's outward normal.
        BoxFace(boxMin.x, localStart.x, localEnd.x, localStart.x, new Vector3(-1f, 0f, 0f),
            localStart, localEnd, localExtent, boxMin, boxMax, ref time, ref localLocation, ref localNormal);
        BoxFace(localStart.x, boxMax.x, localStart.x, localEnd.x, new Vector3(1f, 0f, 0f),
            localStart, localEnd, localExtent, boxMin, boxMax, ref time, ref localLocation, ref localNormal);
        BoxFace(boxMin.y, localStart.y, localEnd.y, localStart.y, new Vector3(0f, -1f, 0f),
            localStart, localEnd, localExtent, boxMin, boxMax, ref time, ref localLocation, ref localNormal);
        BoxFace(localStart.y, boxMax.y, localStart.y, localEnd.y, new Vector3(0f, 1f, 0f),
            localStart, localEnd, localExtent, boxMin, boxMax, ref time, ref localLocation, ref localNormal);
        BoxFace(boxMin.z, localStart.z, localEnd.z, localStart.z, new Vector3(0f, 0f, -1f),
            localStart, localEnd, localExtent, boxMin, boxMax, ref time, ref localLocation, ref localNormal);
        BoxFace(localStart.z, boxMax.z, localStart.z, localEnd.z, new Vector3(0f, 0f, 1f),
            localStart, localEnd, localExtent, boxMin, boxMax, ref time, ref localLocation, ref localNormal);
        if (time == 1f) return default;
        return MakeHit(time, localLocation, localNormal, local);
    }

    // One face of the FUN_103fc980 boundary test (block −X: 0x103fceb9-0x103fd0c7; the other
    // five are the same sequence with other fields).
    // dist = Min' − Start for "−" faces, Start − Max' for "+" faces; delta is the same axis, Δ
    // pointing into the face, stored as float (fstp [ebp+0xc]). Comparisons run in x87 on
    // UNrounded values: a product of two floats is always exact in double, a difference of two
    // floats when their exponents are close (coordinates of one scale); otherwise double (53 bits)
    // and x87 (64 bits) may differ in the last bit of the comparison.
    private static void BoxFace(float distA, float distB, float deltaA, float deltaB, Vector3 n,
        Vector3 start, Vector3 end, Vector3 extent, Vector3 boxMin, Vector3 boxMax,
        ref float time, ref Vector3 location, ref Vector3 hitNormal)
    {
        float delta = deltaA - deltaB;                       // fstp [ebp+0xc]
        double distX87 = (double)distA - distB;              // fsub, in register
        float dist = (float)distX87;                         // fst [ebp+0x10]
        // `fcomp -0.003f; test ah,0x41; jne`: continue only when dist > −0.003.
        if (!(distX87 > (double)-0.003f)) return;
        // max(dist, 0): `fld 0; fcomp dist; test ah,1`: takes dist when 0 < dist.
        float clamped = 0f < dist ? dist : 0f;
        // `fld Δ; fmul Time; fxch; fcompp; test ah,1; je`: passes when clamped < Δ·Time.
        if (!((double)clamped < (double)delta * time)) return;
        float t = dist / delta;                              // fdiv, fst [ebp+0x10]
        if (t < 0f) t = 0f;
        // Point at t: End·t + Start·(1−t) (FVector::operator* ×2, operator+), shifted inward by
        // 0.001·N (FUN_103f2a40 with 0x3a83126f, operator-=).
        Vector3 p = PointAt(start, end, t);
        float px = p.x - 0.001f * n.x, py = p.y - 0.001f * n.y, pz = p.z - 0.001f * n.z;
        // Strictly inside the expanded box on all three axes.
        if (!(boxMin.x < px && px < boxMax.x && boxMin.y < py && py < boxMax.y
              && boxMin.z < pz && pz < boxMax.z))
            return;
        t -= 0.001f;                                         // fsub [0x10473814]
        if (t < 0f) t = 0f;
        time = t;                                            // [esi+0x24]
        // Location = point at the new t − N·(N|Extent); the face normal always.
        Vector3 loc = PointAt(start, end, t);
        float support = n.x * extent.x + n.y * extent.y + n.z * extent.z;
        location = new Vector3(loc.x - n.x * support, loc.y - n.y * support, loc.z - n.z * support);
        hitNormal = n;
    }

    private static Vector3 PointAt(Vector3 start, Vector3 end, float t)
    {
        float s = 1f - t;                                    // fld 1; fsub t; fstp
        return new Vector3(start.x * s + end.x * t, start.y * s + end.y * t, start.z * s + end.z * t);
    }

    // UBox::PointCheck (export thunk 0x1030466f -> body 0x103FE590, which reads
    // the box through GetCollisionBoundingBox and delegates to the worker
    // FUN_103fc1d0). Same LOCAL frame as LineCheck above: the point and the
    // extent go through AActor::ToLocal (the extent as the rotated AABB of its
    // own box), the overlap runs against the local half-extents, and the face
    // normal comes back out through the coords. Established against the real
    // body under the emulator — an earlier "world AABB" reading of it happened
    // to match the boundary probes at yaw 90, where the two models coincide,
    // but produced axis-aligned normals where shipped returns rotated ones.
    public static (bool Hit, Vector3 Location, Vector3 Normal) PointCheck(
        Vector3 point, Vector3 extent, Vector3 centre, float radius, float width,
        float height, UERotator rotation)
    {
        Vector3 half = new(radius, width != 0f ? width : radius, height);
        return PointCheck(point, extent, centre, -half, half, rotation);
    }

    public static (bool Hit, Vector3 Location, Vector3 Normal) PointCheck(
        Vector3 point, Vector3 extent, Vector3 centre, Vector3 boxLo, Vector3 boxHi,
        UERotator rotation)
    {
        BrushCoords local = BrushCoords.Identity;
        local.DivRotator(rotation.Pitch, rotation.Yaw, rotation.Roll);
        Vector3 localPoint = local.TransformVector(point - centre);
        Vector3 localExtent = new(
            Math.Abs(local.X.x) * extent.x + Math.Abs(local.X.y) * extent.y + Math.Abs(local.X.z) * extent.z,
            Math.Abs(local.Y.x) * extent.x + Math.Abs(local.Y.y) * extent.y + Math.Abs(local.Y.z) * extent.z,
            Math.Abs(local.Z.x) * extent.x + Math.Abs(local.Z.y) * extent.y + Math.Abs(local.Z.z) * extent.z);
        if (!OverlapPenetration(localPoint, localExtent, boxLo, boxHi, out _, out Vector3 localNormal))
            return (false, Vector3.zero, Vector3.zero);
        // The reported location is the point CLAMPED into the box, like the
        // overlap branch of LineCheck above — shipped's differs from the raw
        // point exactly along the hit normal for points outside a face.
        Vector3 clamped = new(
            Math.Clamp(localPoint.x, boxLo.x, boxHi.x),
            Math.Clamp(localPoint.y, boxLo.y, boxHi.y),
            Math.Clamp(localPoint.z, boxLo.z, boxHi.z));
        return (true, centre + ToWorldVector(clamped, local),
            ToWorldVector(localNormal, local));
    }

    private static bool OverlapPenetration(Vector3 point, Vector3 extent, Vector3 lo, Vector3 hi,
        out float penetration, out Vector3 normal)
    {
        float minX = point.x - extent.x, maxX = point.x + extent.x;
        float minY = point.y - extent.y, maxY = point.y + extent.y;
        float minZ = point.z - extent.z, maxZ = point.z + extent.z;
        if (maxX <= lo.x + 0.003f || hi.x - 0.003f <= minX
            || maxY <= lo.y + 0.003f || hi.y - 0.003f <= minY
            || maxZ <= lo.z + 0.003f || hi.z - 0.003f <= minZ)
        {
            penetration = 0f;
            normal = Vector3.zero;
            return false;
        }

        penetration = hi.z - minZ;
        normal = new Vector3(0f, 0f, 1f);
        Choose(maxZ - lo.z, new Vector3(0f, 0f, -1f), ref penetration, ref normal);
        Choose(hi.y - minY, new Vector3(0f, 1f, 0f), ref penetration, ref normal);
        Choose(maxY - lo.y, new Vector3(0f, -1f, 0f), ref penetration, ref normal);
        Choose(hi.x - minX, new Vector3(1f, 0f, 0f), ref penetration, ref normal);
        Choose(maxX - lo.x, new Vector3(-1f, 0f, 0f), ref penetration, ref normal);
        return true;
    }

    private static void Choose(float candidate, Vector3 candidateNormal,
        ref float penetration, ref Vector3 normal)
    {
        if (candidate >= penetration) return;
        penetration = candidate;
        normal = candidateNormal;
    }

    // The worker's hit back to the world, both branches (0x103fcbc9-0x103fcd5d, Engine.dll.c 300101-300146): shipped
    // builds the inverse FCoords — axes transposed (FVector ctors), Origin = −(Origin.TransformVectorBy(local)) (Core
    // TransformVectorBy, unary operator-) — then Location = TransformPointBy(inverse) and Normal = TransformVectorBy(inverse),
    // both inlined. Not centre + Rᵀ·v: that is the same algebra with other roundings (1 ulp on rotated boxes, E1).
    private static ActorHit MakeHit(float time, Vector3 localLocation, Vector3 localNormal,
        in BrushCoords local)
    {
        Vector3 o = VectorBy64(local.Origin, local);
        BrushCoords inv = new()
        {
            Origin = new Vector3(-o.x, -o.y, -o.z),
            X = new Vector3(local.X.x, local.Y.x, local.Z.x),
            Y = new Vector3(local.X.y, local.Y.y, local.Z.y),
            Z = new Vector3(local.X.z, local.Y.z, local.Z.z),
        };
        return new ActorHit(true, time, PointBy64(localLocation, inv), VectorBy64(localNormal, inv));
    }

    private static Vector3 ToWorldVector(Vector3 v, in BrushCoords local)
    {
        return new Vector3(
            v.x * local.X.x + v.y * local.Y.x + v.z * local.Z.x,
            v.x * local.X.y + v.y * local.Y.y + v.z * local.Z.y,
            v.x * local.X.z + v.y * local.Y.z + v.z * local.Z.z);
    }

    // PC64 register chains of TransformPointBy / TransformVectorBy (game logic: appEnableFastMath(0)): each difference,
    // product and sum rounds at 64 bits, ((x·A.x + y·A.y) + z·A.z), one fstp dword per component.
    static HP1.Core.X87Ext X87(float v) => HP1.Core.X87Ext.From(v);
    static float Dot64(HP1.Core.X87Ext x, HP1.Core.X87Ext y, HP1.Core.X87Ext z, Vector3 a)
    {
        static HP1.Core.X87Ext R(HP1.Core.X87Ext v) => HP1.Core.X87Ext.Round64(v);
        return R(R(R(x * X87(a.x)) + R(y * X87(a.y))) + R(z * X87(a.z))).ToFloat();
    }
    static Vector3 PointBy64(Vector3 p, in BrushCoords c)
    {
        var dx = HP1.Core.X87Ext.Round64(X87(p.x) - X87(c.Origin.x));
        var dy = HP1.Core.X87Ext.Round64(X87(p.y) - X87(c.Origin.y));
        var dz = HP1.Core.X87Ext.Round64(X87(p.z) - X87(c.Origin.z));
        return new Vector3(Dot64(dx, dy, dz, c.X), Dot64(dx, dy, dz, c.Y), Dot64(dx, dy, dz, c.Z));
    }
    static Vector3 VectorBy64(Vector3 v, in BrushCoords c)
        => new(Dot64(X87(v.x), X87(v.y), X87(v.z), c.X), Dot64(X87(v.x), X87(v.y), X87(v.z), c.Y),
               Dot64(X87(v.x), X87(v.y), X87(v.z), c.Z));
}

// FCollisionHash replica (Engine.dll FCollisionHash, UnActCol.cpp): the exact
// candidate ORDER machinery of the shipped collision hash, minus the random
// bucket tables. Shipped buckets = HashX[ix]^HashY[iy]^HashZ[iz] where the
// tables are appRand()-shuffled permutations (ctor, Engine.dll.c:70909) —
// runtime-random, so no ORDER-visible behavior can depend on them: chain
// membership is filtered by the exact packed cell id, so keying chains by the
// (ix,iy,iz) triple is order-equivalent (the only shipped deviation is a
// bucket collision between two ALIASING packed ids — indices ≥ 0x100, i.e.
// |coord| beyond ~32k, unreachable in real maps and geometry-filtered anyway).
// Order-relevant semantics, all from the shipped bodies:
//   AddActor (49171): one link PREPENDED per extent cell, x outer / y middle
//     / z inner; skips bDeleteMe actors.
//   RemoveActor (7832): unlinks the actor from the extent cells at its
//     CURRENT location (remaining links keep chain order).
//   ActorEncroachmentCheck (12460): visits the query extent cells x outer /
//     y middle / z inner, walks each chain head-first, and dedups actors via
//     CollisionTag — the tag is set on FIRST sight, before any filtering.
// Cells: index = clamp(ROUND((v+32768)/256), 0, 0x3fff) per axis
// (GetHashIndices, 11660; fistp = round-half-to-even).
public sealed class CollisionHashReplica
{
    private sealed class Link
    {
        public int Handle;
        public Link Next;
    }

    private readonly Dictionary<long, Link> _cells = new();
    private readonly Dictionary<int, int> _visit = new();
    private readonly Dictionary<int, int> _linkCount = new();
    private int _tag;

    public static int CellIndex(float v)
    {
        // fadd/fmul -> fstp dword -> fistp (disasm 0x10364036-0x1036404b): the quotient is first
        // rounded to float and only then to an integer.
        int i = (int)Math.Round((float)(((double)v + 32768.0) * 0.00390625), MidpointRounding.ToEven);
        return i < 0 ? 0 : i > 0x3fff ? 0x3fff : i;
    }

    private static long Key(int ix, int iy, int iz) => ((long)iz << 28) | ((long)iy << 14) | (uint)ix;

    public int LinkCount(int handle) => _linkCount.TryGetValue(handle, out int n) ? n : 0;

    public void Add(int handle, Vector3 boxMin, Vector3 boxMax)
    {
        int x0 = CellIndex(boxMin.x), x1 = CellIndex(boxMax.x);
        int y0 = CellIndex(boxMin.y), y1 = CellIndex(boxMax.y);
        int z0 = CellIndex(boxMin.z), z1 = CellIndex(boxMax.z);
        for (int ix = x0; ix <= x1; ix++)
            for (int iy = y0; iy <= y1; iy++)
                for (int iz = z0; iz <= z1; iz++)
                {
                    long key = Key(ix, iy, iz);
                    _cells.TryGetValue(key, out Link head);
                    _cells[key] = new Link { Handle = handle, Next = head };
                    _linkCount.TryGetValue(handle, out int n);
                    _linkCount[handle] = n + 1;
                }
    }

    public void Remove(int handle, Vector3 boxMin, Vector3 boxMax)
    {
        int x0 = CellIndex(boxMin.x), x1 = CellIndex(boxMax.x);
        int y0 = CellIndex(boxMin.y), y1 = CellIndex(boxMax.y);
        int z0 = CellIndex(boxMin.z), z1 = CellIndex(boxMax.z);
        for (int ix = x0; ix <= x1; ix++)
            for (int iy = y0; iy <= y1; iy++)
                for (int iz = z0; iz <= z1; iz++)
                {
                    long key = Key(ix, iy, iz);
                    if (!_cells.TryGetValue(key, out Link head)) continue;
                    Link prev = null;
                    for (Link link = head; link is not null; link = link.Next)
                    {
                        if (link.Handle != handle) { prev = link; continue; }
                        if (prev is null) head = link.Next; else prev.Next = link.Next;
                        _linkCount[handle] = _linkCount[handle] - 1;
                    }
                    if (head is null) _cells.Remove(key); else _cells[key] = head;
                }
    }

    // FCollisionHash::RemoveActor's bSweepAll=TRUE branch: walk EVERY bucket and
    // unlink every link for this handle, regardless of where the actor's current
    // extent says it should be. Shipped sweeps all 0x4000 buckets; the replica's
    // cell dictionary IS that bucket set, so "all cells" is the same walk.
    //
    // This variant exists because the extent-based Remove above can only find
    // links the CURRENT extent covers — an actor moved without a rehash leaves
    // links behind, and shipped's destroy path cleans them up silently rather
    // than failing. Used by destroy and by tearing the whole hash down.
    public void RemoveAll(int handle)
    {
        // Iterate a key SNAPSHOT: the loop both rewrites and drops cells, and
        // mutating the dictionary under its own enumerator is not portable
        // across the runtimes this package targets (netstandard2.1).
        var keys = new List<long>(_cells.Keys);
        foreach (long key in keys)
        {
            if (!_cells.TryGetValue(key, out Link head)) continue;
            Link prev = null;
            for (Link link = head; link is not null; link = link.Next)
            {
                if (link.Handle != handle) { prev = link; continue; }
                if (prev is null) head = link.Next; else prev.Next = link.Next;
                _linkCount[handle] = _linkCount[handle] - 1;
            }
            if (head is null) _cells.Remove(key); else _cells[key] = head;
        }
    }

    // The encroachment cell walk: ordered, first-visit-deduped handles. The
    // caller applies the shipped per-candidate filters (brush/static skip,
    // self skip, geometry) IN this order — they drop entries, never reorder.
    public List<int> EncroachmentCandidates(Vector3 boxMin, Vector3 boxMax)
    {
        int x0 = CellIndex(boxMin.x), x1 = CellIndex(boxMax.x);
        int y0 = CellIndex(boxMin.y), y1 = CellIndex(boxMax.y);
        int z0 = CellIndex(boxMin.z), z1 = CellIndex(boxMax.z);
        _tag++;
        var ordered = new List<int>();
        for (int ix = x0; ix <= x1; ix++)
            for (int iy = y0; iy <= y1; iy++)
                for (int iz = z0; iz <= z1; iz++)
                {
                    if (!_cells.TryGetValue(Key(ix, iy, iz), out Link head)) continue;
                    for (Link link = head; link is not null; link = link.Next)
                    {
                        if (_visit.TryGetValue(link.Handle, out int seen) && seen == _tag) continue;
                        _visit[link.Handle] = _tag;
                        ordered.Add(link.Handle);
                    }
                }
        return ordered;
    }
}

// Which shipped UPrimitive an actor collides with — the faithful model of the
// VIRTUAL GetPrimitive. Every collision consumer resolves IDENTITY here (hash
// extent box, the MoveActor sweep, both trace paths, encroachment); the
// ad-hoc tests ("Brush != null", "IsA(Mover)", "CollideType == 3")
// would disagree with each other and with shipped.
// Operations: UBox::PointCheck (ActorBoxTrace.PointCheck) and
// UOrientedCylinder::PointCheck (ActorOrientedCylinder.PointCheckHit) are
// ported and used by the encroachment path (UELevelRuntime, ocyl_point_oracle).
//   ABrush::GetPrimitive (Engine.dll.c:34623-34627): a non-NULL Brush wins
//     REGARDLESS of CollideType; only a NULL Brush falls back to AActor's.
//   AActor::GetPrimitive (113093-113104): CollideType 3 prefers Mesh, THEN
//     Brush; otherwise (and when CollideType 3 has neither) it returns the
//     engine singleton — 1 = UOrientedCylinder, 2 = UBox, anything else the
//     plain UPrimitive cylinder. Note CollideType 3 without mesh/brush lands
//     on the cylinder, because 3 is neither 1 nor 2.
public enum UEPrimitiveKind { Cylinder, OrientedCylinder, Box, Mesh, Brush }

public static class UEPrimitive
{
    public static UEPrimitiveKind Resolve(UEObject actor)
    {
        // ABrush::GetPrimitive overrides the whole CollideType dispatch.
        if (UEBlocking.IsBrush(actor)) return UEPrimitiveKind.Brush;
        int collideType = UEInterpreter.ToInt(actor.GetProp(PK.CollideType));
        if (collideType == 3)
        {
            if (actor.GetProp(PK.Mesh) is UEResourceRef) return UEPrimitiveKind.Mesh;
            if (actor.GetProp(PK.Brush) is not null) return UEPrimitiveKind.Brush;
        }
        if (collideType == 1) return UEPrimitiveKind.OrientedCylinder;
        if (collideType == 2) return UEPrimitiveKind.Box;
        return UEPrimitiveKind.Cylinder;
    }
}

// The UMesh fields feeding the collision-hash extent box (read from the .u by
// the frontend's package layer, consumed by HashBoxes.Mesh). Plain class, not
// a positional record: the netstandard2.1 profile lacks IsExternalInit;
// ToString keeps the record rendering byte-for-byte.
public sealed class Hp1MeshHashFields
{
    public Vector3 BoundMin { get; }
    public Vector3 BoundMax { get; }
    public Vector3 Scale { get; }
    public Vector3 Origin { get; }
    public int RotPitch { get; }
    public int RotYaw { get; }
    public int RotRoll { get; }

    public Hp1MeshHashFields(Vector3 BoundMin, Vector3 BoundMax, Vector3 Scale,
        Vector3 Origin, int RotPitch, int RotYaw, int RotRoll)
    {
        this.BoundMin = BoundMin;
        this.BoundMax = BoundMax;
        this.Scale = Scale;
        this.Origin = Origin;
        this.RotPitch = RotPitch;
        this.RotYaw = RotYaw;
        this.RotRoll = RotRoll;
    }

    public override string ToString()
        => $"Hp1MeshHashFields {{ BoundMin = {BoundMin}, BoundMax = {BoundMax}, "
            + $"Scale = {Scale}, Origin = {Origin}, RotPitch = {RotPitch}, "
            + $"RotYaw = {RotYaw}, RotRoll = {RotRoll} }}";
}

// The hash-extent bounding boxes (GetActorExtent → GetPrimitive vtable +0xa4
// → GetCollisionBoundingBox vtable +0x60), per shipped primitive:
//   UPrimitive (24136-ish, body 92136): the cylinder AABB — every actor whose
//     CollideType (+0x1d8) is not 3-with-mesh/brush, including the CT 1/2
//     UOrientedCylinder/UBox engine singletons (neither overrides the box).
//   UModel (Ghidra's "UBoxPrim", 14613): the serialized brush FBox corners
//     through the actor's ToWorld coords (vtable +0x9c), re-AABBed.
//   UMesh (33473): (Bound − MeshOrigin) · DrawScale·MeshScale per axis, then
//     FBox::TransformBy((GMath.Unit /= RotOrigin /= Rotation, Origin =
//     Location + PrePivot).Transpose()).
public static class HashBoxes
{
    public static (Vector3 Min, Vector3 Max) Cylinder(Vector3 location, float radius, float height, float zoff)
    {
        float z = location.z + zoff;
        return (new Vector3(location.x - radius, location.y - radius, z - height),
                new Vector3(location.x + radius, location.y + radius, z + height));
    }

    // FBox::TransformBy(FCoords) (Core.dll.c:67325): the 8 corners through
    // TransformPointBy, min/max accumulated from the first corner.
    public static (Vector3 Min, Vector3 Max) TransformedBox(Vector3 boxMin, Vector3 boxMax, in BrushCoords coords)
    {
        Vector3 min = default, max = default;
        bool first = true;
        for (int cx = 0; cx < 2; cx++)
            for (int cy = 0; cy < 2; cy++)
                for (int cz = 0; cz < 2; cz++)
                {
                    var corner = new Vector3(cx == 0 ? boxMin.x : boxMax.x,
                        cy == 0 ? boxMin.y : boxMax.y, cz == 0 ? boxMin.z : boxMax.z);
                    Vector3 p = coords.TransformPoint(corner);
                    if (first) { min = p; max = p; first = false; continue; }
                    min = new Vector3(Math.Min(min.x, p.x), Math.Min(min.y, p.y), Math.Min(min.z, p.z));
                    max = new Vector3(Math.Max(max.x, p.x), Math.Max(max.y, p.y), Math.Max(max.z, p.z));
                }
        return (min, max);
    }

    // FCoords::Transpose (Core): axes become columns, the origin becomes
    // -Origin.TransformVectorBy(this) — UMesh::GetCollisionBoundingBox builds
    // exactly this inline (0x103bb0e0 tail) before FBox::TransformBy.
    public static BrushCoords Transpose(in BrushCoords c)
    {
        Vector3 o = c.TransformVector(c.Origin);
        return new BrushCoords
        {
            Origin = new Vector3(-o.x, -o.y, -o.z),
            X = new Vector3(c.X.x, c.Y.x, c.Z.x),
            Y = new Vector3(c.X.y, c.Y.y, c.Z.y),
            Z = new Vector3(c.X.z, c.Y.z, c.Z.z),
        };
    }

    // The base AActor::ToWorld (body 0x1031be20, non-brush actors): the GMath
    // identity translated by -Location and rotated by the FULL Rotation
    // (pitch/yaw/roll from the sin table; the body reads +0xfc..+0x110 and
    // nothing else — no PrePivot). UOrientedCylinder/UBox::
    // GetCollisionBoundingBox push their local ±(R, W, H) box corners through
    // these coords (checked by running the real bodies against this).
    public static (Vector3 Min, Vector3 Max) OrientedLocalBox(float rx, float ry, float h,
        Vector3 location, int pitch, int yaw, int roll)
    {
        BrushCoords c = BrushCoords.Identity;
        c.Origin -= location;
        c.MulRotator(pitch, yaw, roll);
        return TransformedBox(new Vector3(-rx, -ry, -h), new Vector3(rx, ry, h), c);
    }

    public static (Vector3 Min, Vector3 Max) Mesh(Vector3 boundMin, Vector3 boundMax,
        Vector3 meshScale, Vector3 meshOrigin, int rotPitch, int rotYaw, int rotRoll,
        float drawScale, Vector3 location, Vector3 prePivot, int pitch, int yaw, int roll)
    {
        float sx = drawScale * meshScale.x, sy = drawScale * meshScale.y, sz = drawScale * meshScale.z;
        var localMin = new Vector3((boundMin.x - meshOrigin.x) * sx,
            (boundMin.y - meshOrigin.y) * sy, (boundMin.z - meshOrigin.z) * sz);
        var localMax = new Vector3((boundMax.x - meshOrigin.x) * sx,
            (boundMax.y - meshOrigin.y) * sy, (boundMax.z - meshOrigin.z) * sz);
        BrushCoords c = BrushCoords.Identity;
        c.DivRotator(rotPitch, rotYaw, rotRoll);
        c.DivRotator(pitch, yaw, roll);
        c.Origin = location + prePivot;
        return TransformedBox(localMin, localMax, Transpose(c));
    }
}

// AActor::GetCylinderExtent (body 0x1037a900, thunk 0x103028d3; Engine.dll.c:67016-67062):
// the ACTOR EXTENT for probes, FindSpot and encroachment candidates. Non-virtual, with its own
// dispatch:
//   CollideType 3 and Mesh (+0x164) -> Mesh->vtable[+0x64] = GetCollisionExtent;
//   CollideType 3, Mesh NULL, Brush (+0x168) -> Brush->vtable[+0x64];
//   CollideType 2 -> (R, W != 0 ? W : R, H)   (W = CollisionWidth +0x1d0);
//   otherwise (including CT 3 with neither mesh nor brush) -> (R, R, H).
// Slot +0x64 per the shipped image vtable: UMesh/ULodMesh/USkeletalMesh ->
// UMesh::GetCollisionExtent 0x103b8820; UModel -> "UBoxPrim"::GetCollisionExtent 0x103fea90
// (the slots were checked against the shipped image).
// Both bodies take componentwise max(−Min, Max) of the LOCAL box:
//   UMesh: box = GetCollisionBoundingBox(Owner, bVisual=0) (vtable +0x60, `push 0` @0x103b882b),
//     the branch without rotation and Location; UMesh 0x103b84d0 and USkeletalMesh 0x1041b8b0
//     are the same machine code (Bound − Origin)·DrawScale·Scale, see MeshLocalBox;
//   UModel: box = the model's own UPrimitive::BoundingBox (+0x28..+0x3c) untransformed
//     (0x103fea93-0x103feae4).
// The max choice is `fcom` with a C0 test: when −Min < Max (or unordered) Max is taken,
// otherwise −Min.
public static class CylinderExtent
{
    static float Pick(float min, float max)
    {
        float neg = -min;
        return !(neg >= max) ? max : neg;
    }

    public static Vector3 FromLocalBox(Vector3 min, Vector3 max)
        => new(Pick(min.x, max.x), Pick(min.y, max.y), Pick(min.z, max.z));

    // UMesh::GetCollisionBoundingBox, branch bVisual=0 (0x103b84e1-0x103b85c8; USkeletalMesh
    // 0x1041b8fc-0x1041b9c0 is the same code). Precision per disasm:
    //   sx = fstp dword(DS·Scale.x), sy = fstp dword(DS·Scale.y), while
    //   sz = DS·Scale.z STAYS on the x87 stack (fmul st(1) without a store);
    //   Bound.x − Origin.x feeds fmul without a store, while .y/.z are stored to float before
    //   the multiply; every product is stored to float.
    // The mesh's GetCollisionBoundingBox(Owner, bWorld = 0) is the box that UBox::LineCheck/PointCheck
    // gets from a mesh primitive. Operation order differs per body (x87, one float store per result):
    //   USkeletalMesh 0x1041b8b0 (Engine.dll.c:34313-34324): (B − Origin)·DrawScale·Scale;
    //   UMesh 0x103b84d0: s = DrawScale·Scale (as float), then (B − Origin)·s.
    public static (Vector3 Min, Vector3 Max) MeshCollisionLocalBox(Vector3 boundMin, Vector3 boundMax,
        Vector3 scale, Vector3 origin, float drawScale, bool skeletal)
    {
        if (skeletal)
        {
            Vector3 C(Vector3 b) => new(
                (float)(((double)b.x - origin.x) * drawScale * scale.x),
                (float)(((double)b.y - origin.y) * drawScale * scale.y),
                (float)(((double)b.z - origin.z) * drawScale * scale.z));
            return (C(boundMin), C(boundMax));
        }
        float ax = drawScale * scale.x, ay = drawScale * scale.y, az = drawScale * scale.z;
        Vector3 M(Vector3 b) => new(
            (float)(((double)b.x - origin.x) * ax),
            (float)(((double)b.y - origin.y) * ay),
            (float)(((double)b.z - origin.z) * az));
        return (M(boundMin), M(boundMax));
    }

    public static (Vector3 Min, Vector3 Max) MeshLocalBox(Vector3 boundMin, Vector3 boundMax,
        Vector3 scale, Vector3 origin, float drawScale)
    {
        float sx = (float)((double)drawScale * scale.x);
        float sy = (float)((double)drawScale * scale.y);
        double sz = (double)drawScale * scale.z;
        Vector3 Corner(Vector3 b) => new(
            (float)(((double)b.x - origin.x) * sx),
            (float)((double)(float)((double)b.y - origin.y) * sy),
            (float)((double)(float)((double)b.z - origin.z) * sz));
        return (Corner(boundMin), Corner(boundMax));
    }
}

// ULevel::FindSpot (vtable 0xb4, body 0x103a9690) + the AdjustSpot push math
// (vtable 0xb8, body 0x103a9570) — the spawn/teleport "slide out of solid"
// orchestration, ported from the Engine.dll disasm (RE notes:
// checked by running the
// REAL bodies with scripted probe stubs against this class). The probes are
// injected: pointFree = SinglePointCheck==1 (world-only at both shipped call
// sites — bActors=0), adjustSpot = AdjustSpot (probe + push). Doubles mirror
// the x87-extended intermediates; (float) casts sit exactly where the shipped
// code spills to a dword slot.
public static class FindSpotMath
{
    public delegate void AdjustProc(ref Vector3 spot, Vector3 tryPoint, float adjust);

    // AdjustSpot push (0x103a95d3-0x103a963a), applied when Hit.Time < 1.0f:
    // spot += Normal * Adjust * (1.05f - Time). X/Y round (1.05-T)*N and then
    // *Adjust to float at each step (fstp dword); the Z chain stays on the
    // x87 stack and rounds ONCE at the final add.
    public static Vector3 AdjustSpotPush(Vector3 spot, float hitTime, Vector3 hitNormal, float adjust)
    {
        double c = 1.05f - (double)hitTime;
        float tx = (float)(c * hitNormal.x);
        float ty = (float)(c * hitNormal.y);
        float ux = (float)(tx * (double)adjust);
        float uy = (float)(ty * (double)adjust);
        return new Vector3(
            (float)(ux + (double)spot.x),
            (float)(uy + (double)spot.y),
            (float)(c * hitNormal.z * adjust + spot.z));
    }

    // FindSpot orchestration. Returns true = spot found (location updated to
    // the possibly-nudged spot); false = will not fit. tryCurrent is the 4th
    // shipped arg: SpawnActor passes 1 (direct probe first), FarMoveActor 0.
    public static bool FindSpot(Vector3 extent, ref Vector3 location, bool tryCurrent,
        Func<Vector3, bool> pointFree, AdjustProc adjustSpot)
    {
        if (extent.x == 0f && extent.y == 0f && extent.z == 0f)
            return pointFree(location);
        if (tryCurrent && pointFree(location))
            return true;

        Vector3 spot = location;
        // size = appSqrt((Ez²+Ey²)+Ex²) — appSqrt is a bare fsqrt; the +2.0f
        // add runs on the still-extended root before the float store
        // (0x103a98b5-0x103a98cc).
        double size = Math.Sqrt(((double)extent.z * extent.z + (double)extent.y * extent.y)
            + (double)extent.x * extent.x);
        float diagAdjust = (float)(size + 2.0f);

        for (int i = -1; i < 2; i += 2)
        {
            // X/Y spill i*E to float before the add (fstp @0x103a98f2 /
            // 0x103a993e); Z keeps the product extended — one rounding
            // (fmul @0x103a9977, no spill). spot is re-read after every call:
            // AdjustSpot mutates it in place.
            adjustSpot(ref spot, new Vector3(
                (float)((float)(i * (double)extent.x) + (double)spot.x), spot.y, spot.z), extent.x);
            adjustSpot(ref spot, new Vector3(
                spot.x, (float)((float)(i * (double)extent.y) + (double)spot.y), spot.z), extent.y);
            adjustSpot(ref spot, new Vector3(
                spot.x, spot.y, (float)(i * (double)extent.z + spot.z)), extent.z);
        }
        if (pointFree(spot))
        {
            location = spot;
            return true;
        }

        // Corner diagonals (0x103a9ac3-0x103a9af2): a separate compilation
        // site — ALL THREE components are extended products with a single
        // rounding. x sign outer, z sign inner; Adjust = float(size + 2.0f).
        for (int i = -1; i < 2; i += 2)
            for (int j = -1; j < 2; j += 2)
                for (int k = -1; k < 2; k += 2)
                    adjustSpot(ref spot, new Vector3(
                        (float)(i * (double)extent.x + spot.x),
                        (float)(spot.y + j * (double)extent.y),
                        (float)(spot.z + k * (double)extent.z)), diagAdjust);

        // Distance gate (0x103a9b42-0x103a9ba7): per-component float subs,
        // SizeSquared = (x²+y²)+z² extended with one rounding each, then
        // ext2 * 1.5 (DOUBLE constant @0x104770e0, extended mul) vs dist2.
        float dx = (float)((double)spot.x - location.x);
        float dy = (float)((double)spot.y - location.y);
        float dz = (float)((double)spot.z - location.z);
        float dist2 = (float)(((double)dx * dx + (double)dy * dy) + (double)dz * dz);
        float ext2 = (float)(((double)extent.x * extent.x + (double)extent.y * extent.y)
            + (double)extent.z * extent.z);
        if ((double)ext2 * 1.5 < dist2)
            return false;
        if (pointFree(spot))
        {
            location = spot;
            return true;
        }
        return false;
    }
}
}
