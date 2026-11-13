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

    // Shipped point worker FUN_10427630: the PointCheck counterpart of the
    // box sweep. Node descent recurses the FRONT child and iterates the BACK
    // child with the 1.1-scaled pushout; a reached solid leaf clips the point
    // against the extent-expanded hull planes, the six padded hull-box faces
    // (world only — a brush skips them like the sweep), and the silhouette
    // bevels. The shallowest positive penetration across all visited leaves
    // is recorded with location = point + n*(pushout-dist)*1.02.
    private sealed class PointBox
    {
        private const float ExtentScale = 1.1f;  // node-descent pad
        private const float BoxPad = 0.1f;       // hull-box face pad
        private const float PushScale = 1.02f;   // 0x3f828f5c push-out inflation

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
        private float _bestDist = 100000f;            // info+0x590, 0x47c35000

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

        // Returns 0 when any visited solid leaf contains the expanded point
        // (the shipped hit convention); nonzero = clear.
        public int Visit(int iParent, int iNode, int outside)
        {
            int result = 1;                            // uStack_1d4 accumulator
            while (iNode != -1)
            {
                Node node = _m._nodes[iNode];
                Plane pl = _m._planes[iNode];
                float px = (float)pl.X, py = (float)pl.Y, pz = (float)pl.Z, pw = (float)pl.W;
                if (_hasCoords)
                    (px, py, pz, pw) = _coords.TransformNodePlaneSweep(px, py, pz, pw);
                // ext*1.1 components go through an FVector ctor — float each.
                float e1 = (float)((double)_ex * ExtentScale);
                float e2 = (float)((double)_ey * ExtentScale);
                float e3 = (float)((double)_ez * ExtentScale);
                double push = Math.Abs((double)e3 * pz) + Math.Abs((double)e2 * py)
                            + Math.Abs((double)e1 * px);
                // dist chain order (x + z) + y, spilled to a float slot.
                float dist = (float)(((double)px * _px + (double)pz * _pz) + (double)py * _py - pw);
                bool csg = node.NumVertices != 0 && (node.NodeFlags & 0x21) == 0;
                if (-push < dist)
                {
                    int frontOutside = outside != 0 || csg ? 1 : 0;
                    if (Visit(iNode, node.Front, frontOutside) == 0) result = 0;
                }
                outside = outside != 0 && !csg ? 1 : 0;
                iParent = iNode;
                iNode = node.Back;
                if (push < dist) return result;
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
                if (_hasCoords) (x, y, z, w) = _coords.TransformPlane(x, y, z, w);
                if ((raw & 0x40000000) != 0) { x = -x; y = -y; z = -z; w = -w; }
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
                    if ((comb & 0x03) == 0x03 && !BevelClip(i, j, 1f, 0f, 0f)) return result;
                    if ((comb & 0x0c) == 0x0c && !BevelClip(i, j, 0f, 1f, 0f)) return result;
                    if ((comb & 0x30) == 0x30 && !BevelClip(i, j, 0f, 0f, 1f)) return result;
                }
            return 0; // survived every plane: the expanded point is inside
        }

        // Per-plane point clip (inlined in the shipped worker; also the shared
        // helper FUN_10428fb0 for bevels): pushout WITHOUT the 1.1 scale,
        // record when 0 < dist < bestDist, reject when pushout <= dist.
        private bool Clip(float px, float py, float pz, float pw, int item)
        {
            double push = Math.Abs((double)_ez * pz) + Math.Abs((double)_ey * py)
                        + Math.Abs((double)_ex * px);
            double dist = ((double)px * _px + (double)py * _py) + (double)pz * _pz - pw;
            if (0.0 < dist && dist < _bestDist)
            {
                if (push <= dist) return false;
                _bestDist = (float)dist;
                // Location = point + n * ((pushout - dist) * 1.02): the plane
                // is scaled by (pushout - dist) then by 1.02, floats each.
                float scale = (float)(push - dist);
                float dx = (float)((float)(px * scale) * PushScale);
                float dy = (float)((float)(py * scale) * PushScale);
                float dz = (float)((float)(pz * scale) * PushScale);
                ResultLocation = new Vector3(_px + dx, _py + dy, _pz + dz);
                ResultNormal = new Vector3(px, py, pz);
                ResultItem = item;
            }
            return !(push <= dist);
        }

        // Silhouette bevel between hull planes i and j along a box axis: same
        // gating as the sweep (cross-dot > 0.001, FIntersectPlanes2), bevel
        // normal = UnsafeNormal(axis x line dir), oriented along plane i,
        // then the shared point clip with item -1.
        private bool BevelClip(int i, int j, float ax, float ay, float az)
        {
            float p1x = _hx[i], p1y = _hy[i], p1z = _hz[i], p1w = _hw[i];
            float p2x = _hx[j], p2y = _hy[j], p2z = _hz[j], p2w = _hw[j];
            float c1x = (float)((double)ay * p1z - (double)p1y * az);
            float c1y = (float)((double)az * p1x - (double)ax * p1z);
            float c1z = (float)((double)p1y * ax - (double)ay * p1x);
            float c2x = (float)((double)ay * p2z - (double)p2y * az);
            float c2y = (float)((double)az * p2x - (double)ax * p2z);
            float c2z = (float)((double)p2y * ax - (double)ay * p2x);
            double dot = ((double)c1z * c2z + (double)c1y * c2y) + (double)c1x * c2x;
            if (!(dot > (double)0.001f)) return true;

            // FIntersectPlanes2 (0x1042caf0) — same transcription as the sweep.
            float dirX = (float)((double)p1y * p2z - (double)p1z * p2y);
            float dirY = (float)((double)p1z * p2x - (double)p2z * p1x);
            float dirZ = (float)((double)p1x * p2y - (double)p1y * p2x);
            double sizeSq = ((double)dirX * dirX + (double)dirY * dirY) + (double)dirZ * dirZ;
            if (sizeSq < (double)1e-6f)
                throw new InvalidDataException(
                    $"degenerate point bevel pair (|N1xN2|^2={sizeSq:G9}) — shipped NaN path reached");
            double v1z = (double)p1y * dirX - (double)p1x * dirY;
            float v1y = (float)((double)p1x * dirZ - (double)dirX * p1z);
            float v1x = (float)((double)p1z * dirY - (double)p1y * dirZ);
            double z1 = v1z * p2w;
            float y1 = (float)((double)v1y * p2w);
            float x1 = (float)((double)v1x * p2w);
            double v2z = (double)p2x * dirY - (double)dirX * p2y;
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

            // Bevel normal = UnsafeNormal(axis x line dir) — the point worker
            // crosses the RAW (unnormalized) intersection direction.
            float bx = (float)((double)ay * dirZ - (double)dirY * az);
            float by = (float)((double)az * dirX - (double)ax * dirZ);
            float bz = (float)((double)dirY * ax - (double)ay * dirX);
            double m2 = ((double)bx * bx + (double)by * by) + (double)bz * bz;
            double bscale = 1.0 / Math.Sqrt(m2);
            bx = (float)(bscale * bx); by = (float)(bscale * by); bz = (float)(bscale * bz);
            if (((double)p1z * bz + (double)p1y * by) + (double)p1x * bx < 0.0)
            { bx = -bx; by = -by; bz = -bz; }
            float w = (float)(((double)by * pointY + (double)bz * pointZ) + (double)bx * pointX);
            return Clip(bx, by, bz, w, -1);
        }
    }
}
}
