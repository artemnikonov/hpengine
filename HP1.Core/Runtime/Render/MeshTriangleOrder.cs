// MESH TRIANGLE ORDER OF URender::DrawLodMesh (review plan C3; spec audit/C3_MESH_TRIANGLE_SORT_SPEC.md, disasm addresses
// there). Core's: which faces of a mesh a frame draws and in which order, the frontend emits them in that order.
//
//   enabled   PolyFlags & 0x10000004, PolyFlags = FUN_10b338f0 (style 3 -> 4) | (Opacity < 1 or unordered -> 0x10000004,
//             0x10b1119d), RenDev->SpanBased = 0 on D3D;
//   accepted  face loop 0x10b11255..0x10b1168d over the LOD faces in face order: AND of the three vertex outcodes == 0 and
//             ((MatFlags | PolyFlags) & PF_TwoSided or (N·V0)·Frame->Mirror < 0), N = (V0 − V1)×(V2 − V0) in frame space;
//   key       Owner != Viewport->Actor: fistp(f32(((z0 + z1) + bias) + z2) · 1000)), bias = CollisionHeight · −0.3 when
//             bTwoSidedBias and the face's material is TwoSided; Owner == Viewport->Actor: H = (0, −8, 0),
//             fistp(f32((SA·SC)·SB)), SA/SB/SC = |H − V0/1/2|² as (z² + y²) + x²; render window PC24 = float steps;
//             fistp is round-to-nearest-even, out of range -> 0x80000000;
//   sort      FUN_10b14e40 (Sort): keys descending, comparisons on the wrapped int32 difference, not stable.
// Frame points are the Core pose in the frame's Coords (UEBspRaster.Project form); shipped transforms the mesh straight
// into the frame (GetFrame with the actor Coords), so a key may differ by rounding — the order then differs only between
// faces whose depth sums differ by < 0.001 (or tie), i.e. coplanar overlaps.
using System;
using System.Collections.Generic;

namespace HP1.UEScript
{
    public static class MeshTriangleOrder
    {
        public const int PF_Translucent = 0x4, PF_TwoSided = 0x100, PF_AlphaBlendWord = 0x10000004;
        const int StyleTranslucent = 3;

        // DrawLodMesh's sort condition from the actor (FUN_10b338f0 style bit and the Opacity flag); C0 of `fcomp 1.0`
        // is set for an unordered compare too, hence !(opacity >= 1).
        public static int ActorPolyFlags(int style, float opacity)
            => (style == StyleTranslucent ? PF_Translucent : 0) | (!(opacity >= 1f) ? PF_AlphaBlendWord : 0);
        public static bool Enabled(int style, float opacity) => (ActorPolyFlags(style, opacity) & PF_AlphaBlendWord) != 0;

        public static int Fistp(float v)
        {
            if (float.IsNaN(v)) return int.MinValue;
            double r = Math.Round((double)v, MidpointRounding.ToEven);
            return r >= int.MinValue && r <= int.MaxValue ? (int)r : int.MinValue;
        }

        // Branch A (0x10b11598..0x10b115b6).
        // RU EA (UEEdition.RuOrder; RU Render 0x10b10e6f..0x10b10e78): ((bias + z2) + z0) + z1.
        public static int KeyFar(float z0, float z1, float z2, float bias)
        {
            float t;
            if (UEEdition.RuOrder) { t = bias + z2; t += z0; t += z1; }
            else { t = z0 + z1; t += bias; t += z2; }
            t *= 1000f;   // 0x10b387d8
            return Fistp(t);
        }

        // Branch B (0x10b115c1..0x10b11673): H = (0, −8, 0) (fStack_bc/b8/b4 at 0x10b10101..0x10b10105).
        public static int KeyViewerOwned(UEVector v0, UEVector v1, UEVector v2)
        {
            static float Sq(UEVector v) { float x = 0f - v.x, y = -8f - v.y, z = 0f - v.z; return (z * z + y * y) + x * x; }
            float sa = Sq(v0), sb = Sq(v1), sc = Sq(v2);
            float p = sa * sc;
            p *= sb;
            return Fistp(p);
        }

        static int Sub32(int a, int b) => unchecked(a - b);

        // FUN_10b14e40 (0x10b14e40..0x10b14fe2): in-place over (face, key) pairs, keys descending.
        public static void Sort(int[] face, int[] key, int n)
        {
            if (n < 2) return;
            var stLo = new int[32]; var stHi = new int[32];
            stLo[0] = 0; stHi[0] = n - 1;
            int t = 0;
            void Swap(int i, int j) { (face[i], face[j]) = (face[j], face[i]); (key[i], key[j]) = (key[j], key[i]); }
            while (true)
            {
                int lo = stLo[t], hi = stHi[t];
                while (true)
                {
                    if (hi - lo + 1 <= 8)
                    {
                        while (hi > lo)
                        {
                            int best = lo;
                            for (int j = lo + 1; j <= hi; j++) if (Sub32(key[best], key[j]) > 0) best = j;
                            Swap(best, hi);
                            hi--;
                        }
                        break;
                    }
                    int mid = (hi - lo + 1) / 2;
                    Swap(lo, lo + mid);
                    int pk = key[lo];
                    int a = lo, b = hi + 1;
                    while (true)
                    {
                        a++;
                        while (a <= hi && !(Sub32(pk, key[a]) > 0)) a++;
                        b--;
                        while (b > lo && !(Sub32(pk, key[b]) < 0)) b--;
                        if (a > b) break;
                        Swap(a, b);
                    }
                    Swap(lo, b);
                    int left = b - lo - 1, right = hi - a;
                    if (left < right)
                    {
                        if (hi > a) { stLo[t] = a; stHi[t] = hi; t++; }
                        if (lo + 1 >= b) break;
                        hi = b - 1;
                    }
                    else
                    {
                        if (lo + 1 < b) { stLo[t] = lo; stHi[t] = b - 1; t++; }
                        if (hi <= a) break;
                        lo = a;
                    }
                }
                t--;
                if (t < 0) return;
            }
        }

        // Faces of `triWedge` (3 wedges per face, face order) that the frame f accepts, in draw order. points — the pose in
        // the world (by wedgeVertex); triMaterial/materialFlags — the face's material flags. bias — CollisionHeight·−0.3f
        // applied when bTwoSidedBias (0x10b112b1..0x10b112d9). Not enabled: null (the frontend keeps its own layout).
        public static int[] Order(UESceneNode f, int[] triWedge, int[] wedgeVertex, int[] triMaterial, int[] materialFlags,
            UEVector[] points, int actorPolyFlags, bool twoSidedBias, float collisionHeight, bool ownerIsViewer)
        {
            if ((actorPolyFlags & PF_AlphaBlendWord) == 0) return null;
            BrushCoords c = f.Coords;
            int np = points.Length;
            var fx = new float[np]; var fy = new float[np]; var fz = new float[np]; var oc = new byte[np];
            for (int i = 0; i < np; i++)
            {
                UEVector p = points[i];
                float dx = p.x - c.Origin.x, dy = p.y - c.Origin.y, dz = p.z - c.Origin.z;
                float x = (dx * c.X.x + dy * c.X.y) + dz * c.X.z, y = (dx * c.Y.x + dy * c.Y.y) + dz * c.Y.z, z = (dx * c.Z.x + dy * c.Z.y) + dz * c.Z.z;
                fx[i] = x; fy[i] = y; fz[i] = z;
                float xm = z * f.PrjXM + x, ym = z * f.PrjYM + y, xp = z * f.PrjXP - x, yp = z * f.PrjYP - y;
                oc[i] = (byte)((Neg(xm) ? 4 : 0) | (Neg(xp) ? 8 : 0) | (Neg(ym) ? 0x10 : 0) | (Neg(yp) ? 0x20 : 0));
            }
            int nf = triWedge.Length / 3;
            var face = new int[nf]; var key = new int[nf]; int n = 0;
            int lastMat = int.MinValue; float bias = 0f; int matFlags = 0;
            float bTerm = collisionHeight * -0.3f;   // 0x10b387dc = 0xbe99999a
            for (int t = 0; t < nf; t++)
            {
                int m = triMaterial is not null && t < triMaterial.Length ? triMaterial[t] : 0;
                if (m != lastMat)
                {
                    lastMat = m;
                    matFlags = materialFlags is not null && m >= 0 && m < materialFlags.Length ? materialFlags[m] : 0;
                    bias = twoSidedBias && ((matFlags | actorPolyFlags) & PF_TwoSided) != 0 ? bTerm : 0f;
                }
                int i0 = wedgeVertex[triWedge[t * 3]], i1 = wedgeVertex[triWedge[t * 3 + 1]], i2 = wedgeVertex[triWedge[t * 3 + 2]];
                if ((oc[i0] & oc[i1] & oc[i2]) != 0) continue;
                if (((matFlags | actorPolyFlags) & PF_TwoSided) == 0)
                {
                    float ez = fz[i0] - fz[i1];
                    float nz = (fx[i0] - fx[i1]) * (fy[i2] - fy[i0]) - (fy[i0] - fy[i1]) * (fx[i2] - fx[i0]);
                    float ny = ez * (fx[i2] - fx[i0]) - (fx[i0] - fx[i1]) * (fz[i2] - fz[i0]);
                    float nx = (fy[i0] - fy[i1]) * (fz[i2] - fz[i0]) - ez * (fy[i2] - fy[i0]);
                    // RU EA (UEEdition.RuOrder; RU Render 0x10b10ddb..0x10b10df7): (ny·fy + nx·fx) + nz·fz.
                    float side = UEEdition.RuOrder ? (ny * fy[i0] + nx * fx[i0]) + nz * fz[i0]
                        : (nx * fx[i0] + nz * fz[i0]) + ny * fy[i0];
                    if (!(side * f.Mirror < 0f)) continue;
                }
                face[n] = t;
                key[n] = ownerIsViewer
                    ? KeyViewerOwned(new UEVector(fx[i0], fy[i0], fz[i0]), new UEVector(fx[i1], fy[i1], fz[i1]), new UEVector(fx[i2], fy[i2], fz[i2]))
                    : KeyFar(fz[i0], fz[i1], fz[i2], bias);
                n++;
            }
            Sort(face, key, n);
            Array.Resize(ref face, n);
            return face;
        }

        static bool Neg(float v) => float.IsNaN(v) || BitConverter.SingleToInt32Bits(v) < 0;
    }
}
