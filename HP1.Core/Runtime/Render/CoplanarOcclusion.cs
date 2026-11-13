// COPLANAR BSP NODES: THE EARLIER ONE OCCLUDES THE LATER (OccludeBsp span buffer).
//
// URender::OccludeBsp (Render.dll 0x10b01140) draws a tree node followed by its
// chain of coplanar nodes via FBspNode.iPlane (+0x28, LAB_10b24d1b,
// Render.dll.c:6735-6750). Each polygon is rasterised against its zone's span
// buffer (Render.dll.c:5900-5902: buffer = side zone & zone mask) and copied into
// the draw list only as visible spans; an opaque one also occludes them
// (CopyFromRasterUpdate, Render.dll.c:5952-5961):
//     (Flags & 0x47) == 0 || (Flags & 0x4000001) == 0x4000001 || (Flags & 0x8000000)
// A node with no visible spans is not drawn at all (Render.dll.c:5973-5975).
//
// A Z-buffer cannot do this: two polygons on one plane z-fight and the later
// one wins. Shipped data has such pairs (Lev3_Troll, nodes 6608/6609 of one
// brush polygon: the second has a lightmap without sources, which would show as
// a black wall patch by the troll's door). Here the later node loses the parts
// occluded by earlier chain nodes visible from the same side in the same zone. This is not per-frame buffer emulation: for nodes of ONE
// chain the outcome does not depend on the viewpoint — they lie on one plane
// and are visited in one order.
using System;
using System.Collections.Generic;

namespace HP1.UEScript
{
    public static class CoplanarOcclusion
    {
        const int PF_Invisible = 0x1, PF_TwoSided = 0x100, PF_Portal = 0x4000000, PF_Mirrored = 0x8000000;

        // "Point on line" threshold and the width below which a piece counts as
        // empty, in world units. This is computational precision, not a behaviour
        // parameter: shared vertices of neighbouring nodes differ by float noise
        // (−2048 vs −2048.0005, Lev3_Troll nodes 8/11), and a sliver thinner than
        // a tenth of a unit is invisible from any player distance.
        const double OnEdge = 1e-2;
        const double MinWidth = 0.1;

        public sealed class Stats
        {
            public int Chains, ChainNodes;
            public int PartlyCovered, FullyCovered;
            // Cases this port does not resolve; reported loudly.
            public int TwoSidedBackViewDiffers;   // two-sided later node: occluded differently from the back side
            public int InvisibleEarlierOverlaps;  // earlier PF_Invisible over a visible one: ShowFlags decides (Render.dll.c:5549)
        }

        public static bool UpdatesSpanBuffer(int flags)
            => (flags & 0x47) == 0 || (flags & 0x4000001) == 0x4000001 || (flags & PF_Mirrored) != 0;

        // Nodes whose earlier chain nodes occlude part of the polygon: the visible
        // convex pieces (empty list = fully occluded). Nodes without overlap are
        // absent from the dictionary. effectiveFlags(surf) = surface flags | texture flags.
        // back (optional): the same for a viewer on the back side of a node OccludeBsp draws from behind (PF_TwoSided |
        // PF_Portal, Render.dll.c:5879-5880).
        // nearBack (optional): the same for a one-sided node seen from behind with the eye within 1 unit of the plane
        // (`-1.0 <= dist`, :5879-5880): every chain node lies on that plane, so each earlier one is drawn too — a
        // same-facing one from behind (iZone[0]), an opposite-facing one from the front (iZone[1]).
        public static Dictionary<int, List<UEVector[]>> Compute(StaticGeometry g, Func<int, int> effectiveFlags, Stats stats = null,
            Dictionary<int, List<UEVector[]>> back = null, Dictionary<int, List<UEVector[]>> nearBack = null)
        {
            var result = new Dictionary<int, List<UEVector[]>>();
            StaticNode[] nodes = g.Nodes;
            var isNext = new bool[nodes.Length];
            foreach (StaticNode n in nodes)
                if (n.CoplanarNext >= 0 && n.CoplanarNext < nodes.Length) isNext[n.CoplanarNext] = true;

            var chain = new List<int>();
            var links = new List<ChainNode>();
            for (int head = 0; head < nodes.Length; head++)
            {
                if (isNext[head] || nodes[head].CoplanarNext < 0) continue;
                chain.Clear();
                for (int k = head; k >= 0 && k < nodes.Length && chain.Count <= nodes.Length; k = nodes[k].CoplanarNext)
                    chain.Add(k);
                if (stats != null) { stats.Chains++; stats.ChainNodes += chain.Count; }
                links.Clear();
                foreach (int k in chain)
                {
                    StaticNode n = nodes[k];
                    bool drawable = Drawable(g, k);
                    links.Add(new ChainNode(drawable ? Polygon(g, k) : null, n.PlaneNormal, n.Zone0, n.Zone1,
                        drawable ? effectiveFlags(n.Surf) : 0));
                }

                for (int jPos = 1; jPos < chain.Count; jPos++)
                {
                    List<UEVector[]> front = Later(links, jPos, stats);
                    if (front == null) continue;
                    result[chain[jPos]] = front;
                    if (stats != null) { if (front.Count == 0) stats.FullyCovered++; else stats.PartlyCovered++; }
                }
                if (back != null)
                    for (int jPos = 1; jPos < chain.Count; jPos++)
                    {
                        ChainNode nj = links[jPos];
                        if (nj.Poly == null || (nj.Flags & PF_Invisible) != 0 || (nj.Flags & (PF_TwoSided | PF_Portal)) == 0) continue;
                        List<UEVector[]> b = Visible(links, jPos, false, null);
                        if (b != null) back[chain[jPos]] = b;
                    }
                if (nearBack != null)
                    for (int jPos = 1; jPos < chain.Count; jPos++)
                    {
                        ChainNode nj = links[jPos];
                        if (nj.Poly == null || (nj.Flags & PF_Invisible) != 0 || (nj.Flags & (PF_TwoSided | PF_Portal)) != 0) continue;
                        List<UEVector[]> b = Visible(links, jPos, false, null, near: true);
                        if (b != null) nearBack[chain[jPos]] = b;
                    }
            }
            return result;
        }

        // A coplanar chain node as the rule reads it: polygon (null = not drawable: < 3 vertices or no surface), plane
        // normal, zones, effective flags (surface | texture).
        public readonly struct ChainNode
        {
            public readonly UEVector[] Poly;
            public readonly UEVector Normal;
            public readonly int Zone0, Zone1, Flags;
            public ChainNode(UEVector[] poly, UEVector normal, int zone0, int zone1, int flags)
            { Poly = poly; Normal = normal; Zone0 = zone0; Zone1 = zone1; Flags = flags; }
        }

        // Visible pieces of chain node jPos for a viewer on its front side (null = nothing occluded, empty = fully
        // occluded). Shared by the level model (Compute) and mover tracker fragments, which FilterPoly appends to the
        // TAIL of a level chain (AddPolyFragment, UEMovingBrushTracker.cs): every earlier node is drawn before them.
        public static List<UEVector[]> Later(List<ChainNode> chain, int jPos, Stats stats = null)
        {
            ChainNode nj = chain[jPos];
            if (nj.Poly == null || (nj.Flags & PF_Invisible) != 0) return null;
            List<UEVector[]> front = Visible(chain, jPos, true, stats);
            if (front != null && (nj.Flags & PF_TwoSided) != 0 && stats != null)
            {
                List<UEVector[]> back = Visible(chain, jPos, false, null);
                if (back == null || back.Count != front.Count) stats.TwoSidedBackViewDiffers++;
            }
            return front;
        }

        // Visible pieces of node chain[jPos] for a viewer on its front or
        // back side; null = nothing occluded.
        static List<UEVector[]> Visible(List<ChainNode> chain, int jPos, bool front, Stats stats, bool near = false)
        {
            ChainNode nj = chain[jPos];
            int zoneJ = front ? nj.Zone1 : nj.Zone0;
            var pieces = new List<UEVector[]> { nj.Poly };
            bool touched = false;
            for (int iPos = 0; iPos < jPos && pieces.Count > 0; iPos++)
            {
                ChainNode ni = chain[iPos];
                if (ni.Poly == null) continue;
                int fi = ni.Flags;
                // Side of the earlier node for the same viewer: same-facing → same side.
                bool sameFacing = Dot(ni.Normal, nj.Normal) > 0f;
                bool iFront = sameFacing == front;
                if (!iFront && !near && (fi & (PF_TwoSided | PF_Portal)) == 0) continue;   // Render.dll.c:5886-5887
                if ((iFront ? ni.Zone1 : ni.Zone0) != zoneJ) continue;           // the zone's own span buffer
                if (!UpdatesSpanBuffer(fi)) continue;
                UEVector[] q = ni.Poly;
                if ((fi & PF_Invisible) != 0)
                {
                    if (stats != null && Overlaps(pieces, q, nj.Normal)) stats.InvisibleEarlierOverlaps++;
                    continue;
                }
                var next = new List<UEVector[]>();
                foreach (UEVector[] p in pieces)
                {
                    List<UEVector[]> rest = Subtract(p, q, nj.Normal);
                    if (rest == null) next.Add(p);
                    else { next.AddRange(rest); touched = true; }
                }
                pieces = next;
            }
            return touched ? pieces : null;
        }

        static bool Overlaps(List<UEVector[]> pieces, UEVector[] q, UEVector n)
        {
            foreach (UEVector[] p in pieces) if (Subtract(p, q, n) != null) return true;
            return false;
        }

        static bool Drawable(StaticGeometry g, int node)
        {
            StaticNode n = g.Nodes[node];
            return n.NumVertices >= 3 && n.Surf >= 0 && n.Surf < g.Surfaces.Length;
        }

        public static UEVector[] Polygon(StaticGeometry g, int node)
        {
            StaticNode n = g.Nodes[node];
            var r = new UEVector[n.NumVertices];
            for (int k = 0; k < r.Length; k++) r[k] = g.Points[g.VertPoolPoints[n.VertPool + k]];
            return r;
        }

        // P \ Q for convex P and Q on one plane (normal n): pieces of P outside Q;
        // null = no intersection (P intact), empty = P inside Q.
        public static List<UEVector[]> Subtract(UEVector[] p, UEVector[] q, UEVector n)
        {
            double cx = 0, cy = 0, cz = 0;
            foreach (UEVector v in q) { cx += v.x; cy += v.y; cz += v.z; }
            cx /= q.Length; cy /= q.Length; cz /= q.Length;
            var outside = new List<UEVector[]>();
            List<UEVector> inside = new List<UEVector>(p);
            for (int e = 0; e < q.Length; e++)
            {
                UEVector a = q[e], b = q[(e + 1) % q.Length];
                double dx = b.x - a.x, dy = b.y - a.y, dz = b.z - a.z;
                // Edge normal in the plane: n × (b − a), pointing away from Q's centre.
                double ex = n.y * dz - n.z * dy, ey = n.z * dx - n.x * dz, ez = n.x * dy - n.y * dx;
                double len = Math.Sqrt(ex * ex + ey * ey + ez * ez);
                if (len < 1e-9) continue;
                ex /= len; ey /= len; ez /= len;
                if ((cx - a.x) * ex + (cy - a.y) * ey + (cz - a.z) * ez > 0) { ex = -ex; ey = -ey; ez = -ez; }
                double w = a.x * ex + a.y * ey + a.z * ez;
                Split(inside, ex, ey, ez, w, out List<UEVector> outPart, out List<UEVector> inPart);
                if (inPart == null) return null;
                if (outPart != null) outside.Add(outPart.ToArray());
                inside = inPart;
            }
            return outside;
        }

        // Cut a convex polygon by line (e, w): out — d > 0, in — d < 0;
        // points "on the line" go to both parts. A piece narrower than MinWidth → null.
        static void Split(List<UEVector> poly, double ex, double ey, double ez, double w,
            out List<UEVector> outPart, out List<UEVector> inPart)
        {
            var o = new List<UEVector>(); var i = new List<UEVector>();
            int count = poly.Count;
            for (int k = 0; k < count; k++)
            {
                UEVector a = poly[k], b = poly[(k + 1) % count];
                double da = a.x * ex + a.y * ey + a.z * ez - w;
                double db = b.x * ex + b.y * ey + b.z * ez - w;
                if (da >= -OnEdge) o.Add(a);
                if (da <= OnEdge) i.Add(a);
                if ((da > OnEdge && db < -OnEdge) || (da < -OnEdge && db > OnEdge))
                {
                    double t = da / (da - db);
                    var x = new UEVector((float)(a.x + (b.x - a.x) * t), (float)(a.y + (b.y - a.y) * t),
                        (float)(a.z + (b.z - a.z) * t));
                    o.Add(x); i.Add(x);
                }
            }
            outPart = Solid(o) ? o : null;
            inPart = Solid(i) ? i : null;
        }

        // Piece width: 2·area / longest edge.
        static bool Solid(List<UEVector> p)
        {
            double area = Area(p), longest = 0;
            for (int k = 0; k < p.Count; k++)
            {
                UEVector a = p[k], b = p[(k + 1) % p.Count];
                double dx = b.x - a.x, dy = b.y - a.y, dz = b.z - a.z;
                longest = Math.Max(longest, Math.Sqrt(dx * dx + dy * dy + dz * dz));
            }
            return longest > 0 && 2 * area / longest > MinWidth;
        }

        static double Area(List<UEVector> p)
        {
            if (p.Count < 3) return 0;
            double sx = 0, sy = 0, sz = 0;
            for (int k = 1; k + 1 < p.Count; k++)
            {
                double ax = p[k].x - p[0].x, ay = p[k].y - p[0].y, az = p[k].z - p[0].z;
                double bx = p[k + 1].x - p[0].x, by = p[k + 1].y - p[0].y, bz = p[k + 1].z - p[0].z;
                sx += ay * bz - az * by; sy += az * bx - ax * bz; sz += ax * by - ay * bx;
            }
            return 0.5 * Math.Sqrt(sx * sx + sy * sy + sz * sz);
        }

        static float Dot(UEVector a, UEVector b) => a.x * b.x + a.y * b.y + a.z * b.z;
    }
}
