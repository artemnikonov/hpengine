// The box-extent BSP sweep (BoxSweep, FBoxLineCheckInfo) and the box point check (PointBox).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UELib;
using UELib.Core;
using UELib.Types;
// Script vectors are the Core value type (HP1.UEScript.UEVector).
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    public sealed partial class HeadlessBspModel
    {
        // FBoxLineCheckInfo equivalent: the 0x640-byte stack struct the shipped
        // LineCheck threads through the sweep (offsets in comments are into it).
        private sealed class BoxSweep
        {
            private const float ExtentScale = 1.1f; // 0x10478854, node-descent pad
            private const float BoxPad = 0.1f;      // 0x104737fc, hull-box face pad
            private const float ParallelEps = 1e-5f;      // 0x1047c674/78
            private const float IntersectEps = 1e-6f;     // 0x1047c6a8, FIntersectPlanes2

            private readonly HeadlessBspModel _m;
            private readonly float _extX, _extY, _extZ;   // +0x3c extent
            private readonly float _sx, _sy, _sz;         // +0x590 start
            private readonly float _ex, _ey, _ez;         // +0x584 end
            private readonly float[] _px, _py, _pz, _pw;  // +0x80 leaf planes
            private readonly int[] _flags;                // +0x480 axis sign classes
            private readonly int[] _hullItem;
            private int _numPlanes;                       // +0x4c
            private float _bnX, _bnY, _bnZ;               // +0x50 leaf best normal
            private float _t0, _t1;                       // +0x78 entry / +0x7c exit
            private int _leafItem;                        // +0x5b0

            public float ResultTime = 2f;                 // FCheckResult.Time seed
            public float NormalX, NormalY, NormalZ;
            public int Item = -1;
            public bool DidHit;                           // +0x5ac

            private readonly bool _hasCoords;                 // +0x8 Actor != null
            private readonly BrushCoords _coords;             // +0xc ToWorld coords

            public BoxSweep(HeadlessBspModel m, Vector3 from, Vector3 to, Vector3 extent,
                BrushCoords? coords, SweepBuffers buffers)
            {
                _m = m;
                _px = buffers.X; _py = buffers.Y; _pz = buffers.Z; _pw = buffers.W;
                _flags = buffers.Flags; _hullItem = buffers.HullItem;
                _extX = extent.x; _extY = extent.y; _extZ = extent.z;
                _sx = from.x; _sy = from.y; _sz = from.z;
                _ex = to.x; _ey = to.y; _ez = to.z;
                _hasCoords = coords.HasValue;
                _coords = coords ?? default;
            }

            private static bool IsCsg0(Node n) => n.NumVertices != 0 && (n.NodeFlags & 0x21) == 0;

            // Sweep worker FUN_1042a480: recursive near child, iterative far child.
            // Outside propagation uses IsCsg with ExtraNodeFlags == 0 on BOTH sides
            // (the worker never consults the struct's ExtraNodeFlags field).
            public void Visit(int iParent, int iNode, int outside)
            {
                while (true)
                {
                    if (iNode == -1) { Leaf(iParent, outside); return; }
                    Node node = _m._nodes[iNode];
                    Plane pl = _m._planes[iNode];
                    double px = pl.X, py = pl.Y, pz = pl.Z, pw = pl.W;
                    if (_hasCoords)
                    {
                        // Brush trace (0x1042a4bc): the node plane is rebuilt in
                        // the brush's ToWorld coords before the dists.
                        (float tx, float ty, float tz, float tw) = _coords
                            .TransformNodePlaneSweep((float)px, (float)py, (float)pz, (float)pw);
                        px = tx; py = ty; pz = tz; pw = tw;
                    }
                    // Dists and pushout stay on the x87 stack (never rounded to
                    // float); dist order is x-first here, unlike the leaf clips.
                    double d0 = ((px * _sx + py * _sy) + pz * _sz) - pw;
                    double d1 = ((px * _ex + py * _ey) + pz * _ez) - pw;
                    // ext.x*1.1 stays on the FPU; ext.y/z spill to float (0x1042a5e8).
                    double sx1 = (double)_extX * ExtentScale;
                    float sy1 = (float)((double)_extY * ExtentScale);
                    float sz1 = (float)((double)_extZ * ExtentScale);
                    double push = Math.Abs(sx1 * px) + (Math.Abs((double)sz1 * pz) + Math.Abs((double)sy1 * py));
                    // fcomp + test ah (0x1042a640..0x1042a697): back on C0|C3, NaN included; front on "not below"
                    // (C0 clear, NaN below); side 0 on C0 (d0 below d1, NaN included).
                    bool backNeeded = !(d0 > push) || !(d1 > push);
                    bool frontNeeded = d0 >= -push || d1 >= -push;
                    int side = !(d0 >= d1) ? 0 : 1; // 1 = start-side front, visit front first
                    if (side == 1 ? frontNeeded : backNeeded)
                    {
                        int childOutside = side == 1
                            ? (outside != 0 || IsCsg0(node) ? 1 : 0)
                            : (outside != 0 && !IsCsg0(node) ? 1 : 0);
                        Visit(iNode, side == 1 ? node.Front : node.Back, childOutside);
                    }
                    if (!(side == 1 ? backNeeded : frontNeeded)) return;
                    outside = side == 1
                        ? (outside != 0 && !IsCsg0(node) ? 1 : 0)
                        : (outside != 0 || IsCsg0(node) ? 1 : 0);
                    iParent = iNode;
                    iNode = side == 1 ? node.Back : node.Front;
                }
            }

            // Solid-leaf processing (0x1042a791): collect the hull planes (flip bit
            // 0x40000000, 64 cap), clip against them, the six padded hull-box faces,
            // then the silhouette-edge bevel planes; record if the clipped span
            // still beats the best hit so far.
            private void Leaf(int iParent, int outside)
            {
                if (outside != 0) return;
                int bound = _m._nodes[iParent].Collision;
                if (bound == -1) return;
                int[] hulls = _m._leafHulls;
                _numPlanes = 0;
                while (true)
                {
                    if (bound + _numPlanes >= hulls.Length)
                        throw new InvalidDataException("leaf hull overrun at " + bound);
                    int raw = hulls[bound + _numPlanes];
                    if (raw == -1 || _numPlanes >= 64) break;
                    int planeNode = raw & 0x3fffffff;
                    if (planeNode >= _m._planes.Length)
                        throw new InvalidDataException("leaf hull node " + planeNode);
                    Plane hp = _m._planes[planeNode];
                    float x = (float)hp.X, y = (float)hp.Y, z = (float)hp.Z, w = (float)hp.W;
                    // Brush trace: TransformPlaneByOrtho on the copy BEFORE the
                    // flip bit is applied (0x1042a82b vs 0x1042a852).
                    if (_hasCoords) (x, y, z, w) = _coords.TransformPlane(x, y, z, w);
                    if ((raw & 0x40000000) != 0) { x = -x; y = -y; z = -z; w = -w; }
                    int k = _numPlanes;
                    _px[k] = x; _py[k] = y; _pz[k] = z; _pw[k] = w;
                    _hullItem[k] = raw & ~0x40000000;
                    // Axis sign classes (0x1042a880): <0 / ==0 / >0 per component.
                    _flags[k] = (x < 0f ? 1 : x > 0f ? 2 : 0)
                              | (y < 0f ? 4 : y > 0f ? 8 : 0)
                              | (z < 0f ? 0x10 : z > 0f ? 0x20 : 0);
                    _numPlanes++;
                }
                _t0 = -1f;
                _t1 = ResultTime; // later leaves must beat the recorded hit
                _bnX = 0f; _bnY = 0f; _bnZ = 0f;
                _leafItem = iParent;

                for (int i = 0; i < _numPlanes; i++)
                    if (!Clip(_px[i], _py[i], _pz[i], _pw[i], _hullItem[i])) return;

                // Hull-box faces, in shipped order and with the shipped W quirk:
                // -Z/-X/-Y/+Z padded outward by 0.1, +X/+Y shrunk inward by 0.1.
                // A brush trace jumps straight to the bevels (test [esi+8] at
                // 0x1042ab72, jne 0x1042b4c6) — the bbox is never even read.
                if (!_hasCoords)
                {
                    int box = bound + _numPlanes + 1;
                    if (box + 6 > hulls.Length) throw new InvalidDataException("truncated leaf hull bbox");
                    float minX = FloatBits(hulls[box]), minY = FloatBits(hulls[box + 1]), minZ = FloatBits(hulls[box + 2]);
                    float maxX = FloatBits(hulls[box + 3]), maxY = FloatBits(hulls[box + 4]), maxZ = FloatBits(hulls[box + 5]);
                    if (!Clip(0f, 0f, -1f, (float)((double)BoxPad - minZ), -1)) return;
                    if (!Clip(0f, 0f, 1f, (float)((double)maxZ + BoxPad), -1)) return;
                    if (!Clip(-1f, 0f, 0f, (float)((double)BoxPad - minX), -1)) return;
                    if (!Clip(1f, 0f, 0f, (float)((double)maxX - BoxPad), -1)) return;
                    if (!Clip(0f, -1f, 0f, (float)((double)BoxPad - minY), -1)) return;
                    if (!Clip(0f, 1f, 0f, (float)((double)maxY - BoxPad), -1)) return;
                }

                for (int i = 0; i < _numPlanes; i++)
                    for (int j = 0; j < i; j++)
                    {
                        int comb = _flags[i] | _flags[j];
                        if ((comb & 0x03) == 0x03 && !BevelClip(i, j, 1f, 0f, 0f)) return;
                        if ((comb & 0x0c) == 0x0c && !BevelClip(i, j, 0f, 1f, 0f)) return;
                        if ((comb & 0x30) == 0x30 && !BevelClip(i, j, 0f, 0f, 1f)) return;
                    }

                // Record (0x1042ba3d): strict T0 > -1, T0 < T1, T1 > 0.
                if (_t0 > -1f && _t0 < _t1 && _t1 > 0f)
                {
                    ResultTime = _t0;
                    NormalX = _bnX; NormalY = _bnY; NormalZ = _bnZ;
                    Item = _leafItem;
                    DidHit = true;
                }
            }

            // FBoxLineCheckInfo::ClipTo (0x1042c050, also inlined for the hull and
            // box planes): clip the segment against the plane Minkowski-expanded by
            // the extent. Dist0/Dist1/PushOut/denominator are float slots (fstp
            // dword); the quotient stays on the FPU and is compared/stored against
            // the float T0/T1. Returns false when the span is gone (early-out) or
            // the segment lies fully outside a parallel plane.
            private bool Clip(float px, float py, float pz, float pw, int item)
            {
                float push = (float)((Math.Abs((double)_extZ * pz) + Math.Abs((double)_extY * py))
                                     + Math.Abs((double)_extX * px));
                float d0 = (float)((((double)pz * _sz + (double)py * _sy) + (double)px * _sx) - pw);
                float d1 = (float)((((double)pz * _ez + (double)py * _ey) + (double)px * _ex) - pw);
                double adjusted = (double)d0 - push;
                if (d1 < d0 && adjusted >= -(double)push && adjusted < 0.0) adjusted = 0.0;
                float denom = d0 - d1;
                double t = adjusted / denom;
                if (denom < -ParallelEps)
                {
                    if (t < _t1) _t1 = (float)t;
                }
                else if (denom <= ParallelEps)
                {
                    if (d0 > push && d1 > push) return false;
                }
                else if (t > _t0)
                {
                    _t0 = (float)t;
                    _bnX = px; _bnY = py; _bnZ = pz;
                    if (item >= 0) _leafItem = item;
                }
                return _t0 < _t1;
            }

            // Silhouette-edge bevel plane between hull planes i and j along a box
            // axis (0x1042b515): the two planes face opposite ways on that axis;
            // clip against the plane through their intersection line whose normal
            // is axis x line, oriented with plane i.
            private bool BevelClip(int i, int j, float ax, float ay, float az)
            {
                float p1x = _px[i], p1y = _py[i], p1z = _pz[i], p1w = _pw[i];
                float p2x = _px[j], p2y = _py[j], p2z = _pz[j], p2w = _pw[j];
                // cross(axis, Ni) . cross(axis, Nj) must exceed 0.001 (0x10473814).
                float c1x = (float)((double)ay * p1z - (double)p1y * az);
                float c1y = (float)((double)az * p1x - (double)ax * p1z);
                float c1z = (float)((double)p1y * ax - (double)ay * p1x);
                float c2x = (float)((double)ay * p2z - (double)p2y * az);
                float c2y = (float)((double)az * p2x - (double)ax * p2z);
                float c2z = (float)((double)p2y * ax - (double)ay * p2x);
                double dot = ((double)c1z * c2z + (double)c1y * c2y) + (double)c1x * c2x;
                if (!(dot > (double)0.001f)) return true;

                // FIntersectPlanes2 (0x1042caf0): line direction = N1 x N2 (float
                // components); a point on the line from the plane W's; the machine
                // keeps the z-terms and 1/|dir|^2 chain in x87 while y/x spill.
                float dirX = (float)((double)p1y * p2z - (double)p1z * p2y);
                float dirY = (float)((double)p1z * p2x - (double)p2z * p1x);
                float dirZ = (float)((double)p1x * p2y - (double)p1y * p2x);
                double sizeSq = ((double)dirX * dirX + (double)dirY * dirY) + (double)dirZ * dirZ;
                if (sizeSq < (double)IntersectEps)
                    // Shipped zeroes point+direction and marches on into x87
                    // NaN-poisoned compares (unordered fcom reads as less-than) —
                    // unreachable for real hull data because the 0.001 dot guard
                    // above already rejects near-parallel pairs. Loud, not silent.
                    throw new InvalidDataException(
                        $"degenerate bevel pair (|N1xN2|^2={sizeSq:G9}) — shipped NaN path reached");
                double v1z = (double)p1y * dirX - (double)p1x * dirY; // (dir x N1).z, kept on FPU
                float v1y = (float)((double)p1x * dirZ - (double)dirX * p1z);
                float v1x = (float)((double)p1z * dirY - (double)p1y * dirZ);
                double z1 = v1z * p2w;
                float y1 = (float)((double)v1y * p2w);
                float x1 = (float)((double)v1x * p2w);
                double v2z = (double)p2x * dirY - (double)dirX * p2y; // (N2 x dir).z
                float v2y = (float)((double)dirX * p2z - (double)p2x * dirZ);
                float v2x = (float)((double)dirZ * p2y - (double)p2z * dirY);
                double z2 = v2z * p1w;
                float y2 = (float)((double)v2y * p1w);
                float x2 = (float)((double)v2x * p1w);
                double znum = z1 + z2;
                float ynum = (float)((double)y2 + y1);
                float xnum = (float)((double)x2 + x1);
                float invSq = (float)(1.0 / sizeSq);
                float pointZ = (float)(znum * invSq);
                float pointY = (float)((double)ynum * invSq);
                float pointX = (float)((double)xnum * invSq);
                // FVector::Normalize on the direction. Its 1e-8 small-number guard
                // (Core.dll 0x1017d72c) is subsumed by the 1e-6 guard above, so the
                // normalize always fires here.
                double scale = 1.0 / Math.Sqrt(sizeSq);
                dirX = (float)(scale * dirX); dirY = (float)(scale * dirY); dirZ = (float)(scale * dirZ);

                // Bevel normal = UnsafeNormal(axis x line), flipped along plane i.
                float bx = (float)((double)ay * dirZ - (double)dirY * az);
                float by = (float)((double)az * dirX - (double)ax * dirZ);
                float bz = (float)((double)dirY * ax - (double)ay * dirX);
                double m2 = ((double)bx * bx + (double)by * by) + (double)bz * bz;
                double bscale = 1.0 / Math.Sqrt(m2);
                bx = (float)(bscale * bx); by = (float)(bscale * by); bz = (float)(bscale * bz);
                if (((double)p1z * bz + (double)p1y * by) + (double)p1x * bx < 0.0)
                { bx = -bx; by = -by; bz = -bz; }
                // FPlane(point, normal): W = ((ny*py + nz*pz) + nx*px), float store.
                float w = (float)(((double)by * pointY + (double)bz * pointZ) + (double)bx * pointX);
                return Clip(bx, by, bz, w, -1);
            }
        }

        // Shipped point worker FUN_10427630, the PointCheck counterpart of the box sweep: node descent recurses the FRONT
        // child and iterates the BACK child with the 1.1-scaled push-out; a reached solid leaf clips the point against the
        // extent-expanded hull planes, the six padded hull-box faces (world only: a brush skips them like the sweep) and the
        // silhouette bevels. The shallowest positive penetration across all visited leaves is recorded with Location =
        // point + (1.02·plane)·(push-out − dist).
        //
        // PRECISION, from the disassembly (every value below is the x87 register at PC64 unless stored): node side push-out
        // and dist are stored floats and compared as floats; a leaf plane's push-out is stored, its PlaneDot (Core 0x1011a470:
        // ((p.z·Z + p.y·Y) + p.x·X) − W) is compared with 0 in the register and stored for the other compares; the bevel
        // clip FUN_10428fb0 keeps both in the register. A double chain stands in for none of them: a double sum differs from
        // the register's in the last bit of the stored float (62 of 6657 brush point checks, physics_judge 2026-11-16).
        // PC64 only: the callers are game logic (FindSpot's probes, CheckEncroachment's brush test), outside the render window.
        // Original: Engine.dll not exported: FUN_10427630 [HP1 0x10427630] (the extent worker of
        //   ?PointCheck@UModel@@UAEHAAUFCheckResult@@PAVAActor@@VFVector@@2K@Z [HP1 0x104271d0], called at 0x104272d6)
        private sealed class PointBox
        {
            private const float ExtentScale = 1.1f;   // 0x10478854, node-descent pad
            private const float BoxPad = 0.1f;        // 0x104737fc, hull-box face pad
            private const float PushScale = 1.02f;    // 0x3f828f5c (inline clip) / 0x1047c66c (bevel clip)
            private const float BevelDot = 0.001f;    // 0x10473814

            private readonly HeadlessBspModel _m;
            private readonly float _px, _py, _pz;         // info+0x584 test point
            private readonly float _ex, _ey, _ez;         // info+0x3c extent
            private readonly bool _hasCoords;
            private readonly BrushCoords _coords;
            private readonly float[] _hx = new float[64], _hy = new float[64],
                _hz = new float[64], _hw = new float[64];
            private readonly int[] _flags = new int[64];
            private readonly int[] _hullItem = new int[64];
            private int _numPlanes;
            private float _bestDist = 100000f;            // info+0x590, 0x47c35000 (the dispatcher, 0x104272cc)

            public Vector3 ResultLocation;
            public Vector3 ResultNormal;
            public int ResultItem = -1;

            public PointBox(HeadlessBspModel m, Vector3 point, Vector3 extent, BrushCoords? coords)
            {
                _m = m;
                _px = point.x; _py = point.y; _pz = point.z;
                _ex = extent.x; _ey = extent.y; _ez = extent.z;
                _hasCoords = coords.HasValue;
                _coords = coords ?? default;
                ResultLocation = point;
            }

            static HP1.Core.X87Ext F(float v) => HP1.Core.X87Ext.From(v);
            static HP1.Core.X87Ext R(HP1.Core.X87Ext v) => HP1.Core.X87Ext.Round64(v);
            // `fcom b` with st0 = a: C0 (below or unordered), C0|C3 (also equal) as `test ah, 1` / `test ah, 0x41` read them.
            static bool Below(HP1.Core.X87Ext a, HP1.Core.X87Ext b) => (HP1.Core.X87Ext.Fcom(a, b) & HP1.Core.X87Ext.C0) != 0;
            static bool NotAbove(HP1.Core.X87Ext a, HP1.Core.X87Ext b)
                => (HP1.Core.X87Ext.Fcom(a, b) & (HP1.Core.X87Ext.C0 | HP1.Core.X87Ext.C3)) != 0;
            static readonly HP1.Core.X87Ext Zero = HP1.Core.X87Ext.From(0f);

            // Push-out (|e.z·Z| + |e.y·Y|) + |e.x·X| (fmul: a float product is exact in the register; `fcom 0; test ah,1`;
            // fchs; 0x104277a4 / 0x10427af3 / 0x1042902a), stored: the register chain's float (Pc64ChainF). The sign of a zero
            // term is not seen: the push-out only reaches compares and push − dist.
            static float PushOutF(float ex, float ey, float ez, float x, float y, float z)
                => Pc64ChainF(Math.Abs((double)ez * z), Math.Abs((double)ey * y), Math.Abs((double)ex * x), 0.0);
            static HP1.Core.X87Ext PushOut(float ex, float ey, float ez, float x, float y, float z)
                => R(R(Abs(F(ez) * F(z)) + Abs(F(ey) * F(y))) + Abs(F(ex) * F(x)));
            static HP1.Core.X87Ext Abs(HP1.Core.X87Ext p) => Below(p, Zero) ? -p : p;
            // FPlane::PlaneDot (Core 0x1011a470) of the test point: ((p.z·Z + p.y·Y) + p.x·X) − W. The node side runs the same
            // sums ((Y·p.y + Z·p.z) + X·p.x) − W (0x104277b0..0x104277d2).
            HP1.Core.X87Ext PlaneDot(float x, float y, float z, float w)
                => R(R(R(F(_pz) * F(z) + F(_py) * F(y)) + F(_px) * F(x)) - F(w));

            // Returns 0 when any visited solid leaf contains the expanded point
            // (the shipped hit convention); nonzero = clear.
            public int Visit(int iParent, int iNode, int outside)
            {
                int result = 1;                            // [esp+0x48] accumulator
                while (iNode != -1)
                {
                    Node node = _m._nodes[iNode];
                    Plane pl = _m._planes[iNode];
                    float px = (float)pl.X, py = (float)pl.Y, pz = (float)pl.Z, pw = (float)pl.W;
                    if (_hasCoords)
                        (px, py, pz, pw) = _coords.TransformNodePlaneSweep(px, py, pz, pw);   // 0x1042766c..0x104276f6
                                                                                              // FVector(ext·1.1) (0x104276fa..0x10427745): each product a float argument; push-out stored (0x104277a8),
                                                                                              // ((Y·p.y + Z·p.z) + X·p.x) − W stored (0x104277b0..0x104277d6).
                    float push = PushOutF(_ex * ExtentScale, _ey * ExtentScale, _ez * ExtentScale, px, py, pz);
                    float dist = Pc64ChainF((double)_pz * pz, (double)_py * py, (double)_px * px, pw);
                    bool csg = node.NumVertices != 0 && (node.NodeFlags & 0x21) == 0;
                    if (!(-push >= dist))                  // fcomp: front on C0, NaN included (0x104277da..0x104277e9)
                    {
                        int frontOutside = outside != 0 || csg ? 1 : 0;
                        if (Visit(iNode, node.Front, frontOutside) == 0) result = 0;
                    }
                    outside = outside != 0 && !csg ? 1 : 0;
                    iParent = iNode;
                    iNode = node.Back;
                    if (dist > push) return result;        // C0|C3 clear (0x10427863..0x10427870)
                }
                if (outside != 0) return result;

                int bound = _m._nodes[iParent].Collision;
                if (bound == -1) return result;
                int[] hulls = _m._leafHulls;
                _numPlanes = 0;
                while (true)
                {
                    if (bound + _numPlanes >= hulls.Length)
                        throw new InvalidDataException("point leaf hull overrun at " + bound);
                    int raw = hulls[bound + _numPlanes];
                    if (raw == -1 || _numPlanes >= 64) break;
                    int planeNode = raw & 0x3fffffff;
                    if (planeNode >= _m._planes.Length)
                        throw new InvalidDataException("point leaf hull node " + planeNode);
                    Plane hp = _m._planes[planeNode];
                    float x = (float)hp.X, y = (float)hp.Y, z = (float)hp.Z, w = (float)hp.W;
                    if (_hasCoords) (x, y, z, w) = _coords.TransformPlane(x, y, z, w);    // TransformPlaneByOrtho, 0x1042791f
                    if ((raw & 0x40000000) != 0) { x = -x; y = -y; z = -z; w = -w; }    // FPlane::Flip, 0x10427956
                    int k = _numPlanes;
                    _hx[k] = x; _hy[k] = y; _hz[k] = z; _hw[k] = w;
                    _hullItem[k] = raw & ~0x40000000;
                    _flags[k] = (x < 0f ? 1 : x > 0f ? 2 : 0)
                              | (y < 0f ? 4 : y > 0f ? 8 : 0)
                              | (z < 0f ? 0x10 : z > 0f ? 0x20 : 0);
                    _numPlanes++;
                }

                for (int i = 0; i < _numPlanes; i++)
                    if (!Clip(_hx[i], _hy[i], _hz[i], _hw[i], _hullItem[i])) return result;

                if (!_hasCoords)
                {
                    int box = bound + _numPlanes + 1;
                    if (box + 6 > hulls.Length) throw new InvalidDataException("truncated point leaf bbox");
                    float minX = FloatBits(hulls[box]), minY = FloatBits(hulls[box + 1]), minZ = FloatBits(hulls[box + 2]);
                    float maxX = FloatBits(hulls[box + 3]), maxY = FloatBits(hulls[box + 4]), maxZ = FloatBits(hulls[box + 5]);
                    if (!Clip(0f, 0f, -1f, (float)((double)BoxPad - minZ), -1)) return result;
                    if (!Clip(0f, 0f, 1f, (float)((double)maxZ + BoxPad), -1)) return result;
                    if (!Clip(-1f, 0f, 0f, (float)((double)BoxPad - minX), -1)) return result;
                    if (!Clip(1f, 0f, 0f, (float)((double)maxX - BoxPad), -1)) return result;
                    if (!Clip(0f, -1f, 0f, (float)((double)BoxPad - minY), -1)) return result;
                    if (!Clip(0f, 1f, 0f, (float)((double)maxY - BoxPad), -1)) return result;
                }

                for (int i = 0; i < _numPlanes; i++)
                    for (int j = 0; j < i; j++)
                    {
                        int comb = _flags[i] | _flags[j];
                        if ((comb & 0x03) == 0x03 && !BevelClip(i, j, 0)) return result;
                        if ((comb & 0x0c) == 0x0c && !BevelClip(i, j, 1)) return result;
                        if ((comb & 0x30) == 0x30 && !BevelClip(i, j, 2)) return result;
                    }
                return 0; // survived every plane: the expanded point is inside
            }

            // The worker's inline clip of a hull plane (0x10427a71..0x10427bfd) and of a box face (0x10427c0a..): push-out
            // stored, PlaneDot in the register for `> 0` then stored; dist below the best and below the push-out records
            // scale = push − dist (floats), Location = point + f32(f32(1.02·N)·scale) (operator*(float, FPlane) 0x1042ca80,
            // FPlane::operator*(float), FVector::operator+), Normal, Item; dist not below the push-out rejects.
            private bool Clip(float x, float y, float z, float w, int item)
            {
                float push = PushOutF(_ex, _ey, _ez, x, y, z);
                double p0 = (double)_pz * z, p1 = (double)_py * y, p2 = (double)_px * x;
                float dist = Pc64ChainF(p0, p1, p2, w);
                if (Pc64ChainSide(p0, p1, p2, w) > 0.0 && !(dist >= _bestDist))   // `fcomp 0` of the register (0x10427b12)
                {
                    if (dist >= push) return false;
                    float scale = push - dist;
                    _bestDist = dist;
                    ResultLocation = new Vector3(_px + PushScale * x * scale, _py + PushScale * y * scale, _pz + PushScale * z * scale);
                    ResultNormal = new Vector3(x, y, z);
                    ResultItem = item;
                }
                return !(dist >= push);
            }

            // The bevel clip FUN_10428fb0: push-out and dist stay in the register (`fcom bestDist`, `fcom st(1)`); best =
            // fst dist; scale = f32(push − dist); X·1.02 stays in the register through ·scale and + p.x (0x10429086..0x104290d7),
            // Y·1.02 and Z·1.02 are stored, then ·scale and + p stored per component.
            // Original: Engine.dll not exported: FUN_10428fb0 [HP1 0x10428fb0] (called from FUN_10427630 through 0x103039bd)
            private bool BevelPlaneClip(float x, float y, float z, float w)
            {
                HP1.Core.X87Ext push = PushOut(_ex, _ey, _ez, x, y, z);
                HP1.Core.X87Ext dist = PlaneDot(x, y, z, w);
                if (!NotAbove(dist, Zero) && Below(dist, F(_bestDist)))
                {
                    if (!Below(dist, push)) return false;
                    _bestDist = dist.ToFloat();
                    float scale = R(push - dist).ToFloat();
                    float lx = R(R(F(x) * F(PushScale) * F(scale)) + F(_px)).ToFloat();
                    ResultLocation = new Vector3(lx, _py + PushScale * y * scale, _pz + PushScale * z * scale);
                    ResultNormal = new Vector3(x, y, z);
                    ResultItem = -1;
                }
                return Below(dist, push);
            }

            // Silhouette bevel between hull planes i and j along box axis `axis` (0x10428549..0x10428a31): (A ^ P_i) | (A ^ P_j)
            // above 0.001 (X: the cross products inline as (0, 0 − P.z, P.y − 0) and the dot (x + y) + z, 0x10428565..;
            // Y, Z: FVector::operator^ and operator| (z + y) + x); FIntersectPlanes2(I, D, P_i, P_j) (its return value unread);
            // N = (A ^ D).UnsafeNormal(), N *= −1 when P_i | N is below 0 (C0, NaN included); FPlane(I, N); the bevel clip.
            private bool BevelClip(int i, int j, int axis)
            {
                float p1x = _hx[i], p1y = _hy[i], p1z = _hz[i], p1w = _hw[i];
                float p2x = _hx[j], p2y = _hy[j], p2z = _hz[j], p2w = _hw[j];
                float ax = axis == 0 ? 1f : 0f, ay = axis == 1 ? 1f : 0f, az = axis == 2 ? 1f : 0f;
                HP1.Core.X87Ext dot;
                if (axis == 0)
                {
                    float c1y = 0f - p1z, c1z = p1y - 0f, c2y = 0f - p2z, c2z = p2y - 0f;
                    dot = R(R(F(0f) * F(0f) + F(c2y) * F(c1y)) + F(c2z) * F(c1z));
                }
                else
                {
                    (float c1x, float c1y, float c1z) = Cross(ax, ay, az, p1x, p1y, p1z);
                    (float c2x, float c2y, float c2z) = Cross(ax, ay, az, p2x, p2y, p2z);
                    dot = Dot(c1x, c1y, c1z, c2x, c2y, c2z);
                }
                if (NotAbove(dot, F(BevelDot))) return true;   // `fcomp 0.001; test ah,0x41; jne` (0x104285f3 / ..7a3 / ..952)

                IntersectPlanes(p1x, p1y, p1z, p1w, p2x, p2y, p2z, p2w, out float ix, out float iy, out float iz,
                    out float dx, out float dy, out float dz);
                (float cx, float cy, float cz) = Cross(ax, ay, az, dx, dy, dz);
                (float nx, float ny, float nz) = UnsafeNormal(cx, cy, cz);
                if (Below(Dot(p1x, p1y, p1z, nx, ny, nz), Zero))
                {
                    // FVector::operator*=(-1.f) (Core 0x10119580): a NaN keeps its bits, a zero flips its sign.
                    nx = (F(nx) * F(-1f)).ToFloat(); ny = (F(ny) * F(-1f)).ToFloat(); nz = (F(nz) * F(-1f)).ToFloat();
                }
                // FPlane(FVector Base, const FVector& Normal) (Core 0x1011a260): W = (B.y·N.y + B.z·N.z) + B.x·N.x.
                float w = R(R(F(iy) * F(ny) + F(iz) * F(nz)) + F(ix) * F(nx)).ToFloat();
                return BevelPlaneClip(nx, ny, nz, w);
            }

            // FVector::operator^ (Core 0x10119110), this = a: (a.y·b.z − b.y·a.z, a.z·b.x − a.x·b.z, b.y·a.x − a.y·b.x).
            static (float, float, float) Cross(float ax, float ay, float az, float bx, float by, float bz)
                => (R(F(ay) * F(bz) - F(by) * F(az)).ToFloat(), R(F(az) * F(bx) - F(ax) * F(bz)).ToFloat(),
                    R(F(by) * F(ax) - F(ay) * F(bx)).ToFloat());

            // FVector::operator| (Core 0x10119190): (b.z·a.z + b.y·a.y) + b.x·a.x.
            static HP1.Core.X87Ext Dot(float ax, float ay, float az, float bx, float by, float bz)
                => R(R(F(bz) * F(az) + F(by) * F(ay)) + F(bx) * F(ax));

            // FVector::UnsafeNormal (Core 0x10119ae0): (x·x + y·y) + z·z stored as a double, appSqrt (fsqrt), 1.0 / it in the
            // register, each component f32(scale·c).
            static (float, float, float) UnsafeNormal(float x, float y, float z)
            {
                HP1.Core.X87Ext k = InvSize(SizeSquared(x, y, z));
                return (R(k * F(x)).ToFloat(), R(k * F(y)).ToFloat(), R(k * F(z)).ToFloat());
            }

            static HP1.Core.X87Ext SizeSquared(float x, float y, float z) => R(R(F(x) * F(x) + F(y) * F(y)) + F(z) * F(z));
            static HP1.Core.X87Ext InvSize(HP1.Core.X87Ext sizeSquared)
                => HP1.Core.X87Ext.Div64(HP1.Core.X87Ext.From(1.0), HP1.Core.X87Ext.Sqrt64(HP1.Core.X87Ext.From(sizeSquared.ToDouble())));

            // FIntersectPlanes2: D = P1 ^ P2 stored (z, y, x computed in that order); I = ((D ^ P1)·P2.W + (P2 ^ D)·P1.W) /
            // |D|² — the z terms stay in the register, y and x are
            // stored — and D.Normalize() (Core 0x101198d0: |D|² stored as a double, appSqrt, 1.0 / it, f32(scale·c)).
            // Original: Engine.dll not exported: FUN_1042caf0 [HP1 0x1042caf0] (called from FUN_10427630 at 0x10428616 through
            //   0x10304bd8; the box sweep's bevels call it too)
            static void IntersectPlanes(float p1x, float p1y, float p1z, float p1w, float p2x, float p2y, float p2z, float p2w,
                out float ix, out float iy, out float iz, out float dx, out float dy, out float dz)
            {
                HP1.Core.X87Ext vz = R(F(p1x) * F(p2y) - F(p1y) * F(p2x));
                HP1.Core.X87Ext vy = R(F(p1z) * F(p2x) - F(p2z) * F(p1x));
                dx = R(F(p1y) * F(p2z) - F(p1z) * F(p2y)).ToFloat();
                dy = vy.ToFloat(); dz = vz.ToFloat();
                HP1.Core.X87Ext s2 = SizeSquared(dx, dy, dz);
                // Below 1e-6 (0x1047c6a8; NaN included) the original zeroes I and D and goes on: N = UnsafeNormal(0) is the
                // indefinite NaN and the bevel clip neither records nor rejects. Two near-identical planes whose axis component
                // straddles 0 pass the flags and dot tests and get here; no corpus reaches it: a loud stop.
                if (Below(s2, F(1e-6f)))
                    throw new InvalidDataException($"degenerate point bevel pair (|N1xN2|^2={s2.ToDouble():G9}): shipped NaN path reached");
                HP1.Core.X87Ext z1 = R(R(F(p1y) * F(dx) - F(p1x) * F(dy)) * F(p2w));
                float y1 = R(F(p1x) * F(dz) - F(dx) * F(p1z)).ToFloat() * p2w;
                float x1 = R(F(p1z) * F(dy) - F(p1y) * F(dz)).ToFloat() * p2w;
                HP1.Core.X87Ext z2 = R(R(F(p2x) * F(dy) - F(dx) * F(p2y)) * F(p1w));
                float y2 = R(F(dx) * F(p2z) - F(p2x) * F(dz)).ToFloat() * p1w;
                float x2 = R(F(dz) * F(p2y) - F(p2z) * F(dy)).ToFloat() * p1w;
                HP1.Core.X87Ext znum = R(z1 + z2);
                float ynum = y2 + y1, xnum = x2 + x1;
                float inv = HP1.Core.X87Ext.Div64(F(1f), s2).ToFloat();
                iz = R(znum * F(inv)).ToFloat();
                iy = ynum * inv;
                ix = xnum * inv;
                HP1.Core.X87Ext k = InvSize(s2);           // Normalize's 1e-8 guard (Core 0x1017d72c) is below 1e-6: it normalizes
                dx = R(k * F(dx)).ToFloat(); dy = R(k * F(dy)).ToFloat(); dz = R(k * F(dz)).ToFloat();
            }
        }
    }
}
