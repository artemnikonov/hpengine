// BSP SURFACE CLIPPING AND RASTER: port of Render.dll bodies (D-56, OccludeBsp occlusion).
//
//   URender::ClipBspSurf  0x10b1f8f0 (export 0x12e9): node vertices -> frame point cache (FTransform by Stamp:
//     transform to frame axes, outcodes XM 4 / XP 8 / YM 0x10 / YP 0x20 by sign bit, RZ = ProjZ / Z,
//     ScreenX/Y = X*RZ + FX15, Y*RZ + FY15, IntY = fistp(ScreenY - 0.5) when outcodes are 0); all outside one
//     side -> 0; otherwise Sutherland-Hodgman clipping against XM, XP, YM, YP (only the sides touched), then
//     against the frame NearClip (+0x24..+0x30, if W != 0). Sign is the float bit (-0 counts as "outside"), as shipped.
//   FUN_10b24f30 (raster): StartY/EndY = min/max IntY, MinX/MaxX = fistp(ScreenX - 0.5); outside [0, frame Y] ->
//     clamp IntY of ALL points and ScreenY = IntY (points are shared with the cache: the next surface sees the
//     change); span buffer set and BoxIsVisible(MinX, StartY, MaxX, EndY) false -> 0. Edges in 16.16:
//     dx = dX*65536/dY, X0 = fistp((IntY_top - ScreenY_top)*dx + ScreenX_top*65536 - 0.5), step = fistp(dx - 0.5);
//     per row X += step, then store X >> 16 (the edge's first row is already stepped); descending edge -> slot 1,
//     ascending -> 0.
// Render window is PC24: all arithmetic in float. Checked against the original body.
using System;
using System.Collections.Generic;
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    public sealed class UETransformPt
    {
        public float X, Y, Z;          // +0 point in frame axes
        public byte Flags;             // +0xc outcodes
        public float ScreenX, ScreenY; // +0x10, +0x14
        public int IntY;               // +0x18
        public float RZ;               // +0x1c
    }

    public sealed class UEBspRaster
    {
        public UESceneNode Frame;
        readonly Dictionary<int, UETransformPt> _cache = new();   // _PointCache by Stamp: new frame, new cache
        // Port (allocation only): the stamp's points come from a pool reset with the stamp (nothing holds a point of an
        // earlier stamp); ClipBspSurf's result is a scratch list valid until the next call.
        readonly List<UETransformPt> _pts = new();
        int _nPts;
        readonly List<UETransformPt> _l0 = new(), _l1 = new(), _l2 = new();
        float[] _d = Array.Empty<float>();
        public void NewStamp() { _cache.Clear(); _nPts = 0; }

        UETransformPt NewPt()
        {
            UETransformPt t;
            if (_nPts < _pts.Count) t = _pts[_nPts];
            else _pts.Add(t = new UETransformPt());
            _nPts++;
            t.X = t.Y = t.Z = 0f; t.Flags = 0; t.ScreenX = t.ScreenY = 0f; t.IntY = 0; t.RZ = 0f;
            return t;
        }

        // Float sign bit (-0 counts as "outside"). NaN here arises only from an operation (model inputs are finite),
        // and x87 yields the "real indefinite" 0xFFC00000 for it, sign 1; on ARM the default NaN is positive.
        static bool Neg(float v) => float.IsNaN(v) || BitConverter.SingleToInt32Bits(v) < 0;

        UETransformPt Project(Vector3 p)
        {
            BrushCoords c = Frame.Coords;
            float dx = p.x - c.Origin.x, dy = p.y - c.Origin.y, dz = p.z - c.Origin.z;
            UETransformPt t = NewPt();
            t.X = (dx * c.X.x + dy * c.X.y) + dz * c.X.z;
            t.Y = (dx * c.Y.x + dy * c.Y.y) + dz * c.Y.z;
            t.Z = (dx * c.Z.x + dy * c.Z.y) + dz * c.Z.z;
            float xm = t.Z * Frame.PrjXM + t.X, ym = t.Z * Frame.PrjYM + t.Y;
            float xp = t.Z * Frame.PrjXP - t.X, yp = t.Z * Frame.PrjYP - t.Y;
            t.Flags = (byte)((Neg(xm) ? 4 : 0) | (Neg(xp) ? 8 : 0) | (Neg(ym) ? 0x10 : 0) | (Neg(yp) ? 0x20 : 0));
            t.RZ = Frame.ProjZ / t.Z;
            if (t.Flags == 0)
            {
                t.ScreenX = t.X * t.RZ + Frame.FX15;
                t.ScreenY = t.Y * t.RZ + Frame.FY15;
                t.IntY = RoundEven(t.ScreenY - 0.5f);
            }
            return t;
        }

        // x87 fistp with RC=00: round to nearest even; NaN and out-of-int32 give the "integer indefinite" 0x80000000.
        static int RoundEven(float v)
        {
            double d = Math.Round((double)v, MidpointRounding.ToEven);
            return double.IsNaN(d) || d < int.MinValue || d > int.MaxValue ? int.MinValue : (int)d;
        }

        UETransformPt Lerp(UETransformPt a, UETransformPt b, float t)
        {
            UETransformPt n = NewPt();
            n.X = (b.X - a.X) * t + a.X;
            n.Y = (b.Y - a.Y) * t + a.Y;
            n.Z = (b.Z - a.Z) * t + a.Z;
            n.RZ = Frame.ProjZ / n.Z;
            n.ScreenX = n.RZ * n.X + Frame.FX15;
            n.ScreenY = n.RZ * n.Y + Frame.FY15;
            n.IntY = RoundEven(n.ScreenY - 0.5f);
            return n;
        }

        // Polygon clipping against one plane (shared body for all sides in shipped): dist[i] per point,
        // pass prev -> cur: prev "inside" (sign bit 0) -> output; differing signs -> intersection point.
        // side: 0 XM, 1 XP, 2 YM, 3 YP, 4 NearClip — the distances below are the shipped expressions per side.
        float Dist(UETransformPt t, int side) => side switch
        {
            0 => t.Z * Frame.PrjXM + t.X,
            1 => t.Z * Frame.PrjXP - t.X,
            2 => t.Z * Frame.PrjYM + t.Y,
            3 => t.Z * Frame.PrjYP - t.Y,
            // 0x10b2019e..0x10b201b3: ((N.z·Z + N.y·Y) + N.x·X) − W.
            _ => ((Frame.NearClipN.z * t.Z + Frame.NearClipN.y * t.Y) + Frame.NearClipN.x * t.X) - Frame.NearClipW,
        };

        List<UETransformPt> ClipBy(List<UETransformPt> pts, int side)
        {
            int n = pts.Count;
            if (_d.Length < n) _d = new float[n];
            float[] d = _d;
            for (int i = 0; i < n; i++) d[i] = Dist(pts[i], side);
            List<UETransformPt> outp = ReferenceEquals(pts, _l1) ? _l2 : _l1;
            outp.Clear();
            int prev = n - 1;
            for (int i = 0; i < n; i++)
            {
                if (!Neg(d[prev])) outp.Add(pts[prev]);
                if (Neg(d[prev]) != Neg(d[i]))
                {
                    float t = d[prev] / (d[prev] - d[i]);
                    outp.Add(Lerp(pts[prev], pts[i], t));
                }
                prev = i;
            }
            return outp;
        }

        // pointIndices = Points[Verts[iVertPool + k].pVertex], k = 0..NumVertices-1; point = model point.
        public List<UETransformPt> ClipBspSurf(IReadOnlyList<int> pointIndices, Func<int, Vector3> point)
        {
            byte and = 0x3c, or = 0;
            List<UETransformPt> pts = _l0;
            pts.Clear();
            for (int k = 0; k < pointIndices.Count; k++)
            {
                int pi = pointIndices[k];
                if (!_cache.TryGetValue(pi, out UETransformPt t)) _cache[pi] = t = Project(point(pi));
                pts.Add(t);
                and &= t.Flags; or |= t.Flags;
            }
            if (and != 0) return null;
            if (or != 0)
            {
                if ((or & 4) != 0) { pts = ClipBy(pts, 0); if (pts.Count == 0) return null; }
                if ((or & 8) != 0) { pts = ClipBy(pts, 1); if (pts.Count == 0) return null; }
                if ((or & 0x10) != 0) { pts = ClipBy(pts, 2); if (pts.Count == 0) return null; }
                if ((or & 0x20) != 0) { pts = ClipBy(pts, 3); if (pts.Count == 0) return null; }
            }
            if (Frame.NearClipW != 0f)
            {
                bool any = false;
                foreach (var t in pts) if (!(0f <= Dist(t, 4))) any = true;
                if (any) { pts = ClipBy(pts, 4); if (pts.Count == 0) return null; }
            }
            return pts;
        }

        public int StartY, EndY, MinX, MaxX;
        // Raster rows by absolute Y (shipped uses a global array: rows outside the polygon are untouched).
        public (int X0, int X1)[] Lines = new (int, int)[0];

        public bool Raster(List<UETransformPt> p, UESpanBuffer test, int frameY)
        {
            int n = p.Count;
            EndY = StartY = p[0].IntY;
            MaxX = MinX = RoundEven(p[0].ScreenX - 0.5f);
            for (int i = 1; i < n; i++)
            {
                int y = p[i].IntY;
                if (y < StartY) StartY = y; else if (y > EndY) EndY = y;
                int x = RoundEven(p[i].ScreenX - 0.5f);
                if (x < MinX) MinX = x; else if (x > MaxX) MaxX = x;
            }
            bool clamp = false;
            if (StartY < 0) { StartY = 0; clamp = true; }
            else if (EndY > frameY) { if (StartY >= frameY) StartY = frameY; clamp = true; }
            if (clamp)
            {
                if (EndY < 0) EndY = 0; else if (EndY >= frameY) EndY = frameY;
                foreach (var t in p)
                {
                    int y = t.IntY < 0 ? 0 : (t.IntY >= frameY ? frameY : t.IntY);
                    t.IntY = y;
                    t.ScreenY = y;
                }
            }
            if (test is not null && !test.BoxIsVisible(MinX, StartY, MaxX, EndY)) return false;
            if (Lines.Length < frameY + 2) Lines = new (int, int)[frameY + 2];
            UETransformPt prev = p[n - 1];
            for (int i = 0; i < n; i++)
            {
                UETransformPt cur = p[i];
                if (cur.IntY != prev.IntY)
                {
                    bool down = cur.IntY > prev.IntY;
                    UETransformPt bot = down ? cur : prev, top = down ? prev : cur;
                    float dx = (bot.ScreenX - top.ScreenX) * 65536f / (bot.ScreenY - top.ScreenY);
                    float x0 = ((float)top.IntY - top.ScreenY) * dx + top.ScreenX * 65536f;
                    int x = RoundEven(x0 - 0.5f), step = RoundEven(dx - 0.5f);
                    for (int y = top.IntY; y < bot.IntY; y++)
                    {
                        x += step;
                        if (down) Lines[y].X1 = x >> 16; else Lines[y].X0 = x >> 16;
                    }
                }
                prev = cur;
            }
            return true;
        }
    }
}
