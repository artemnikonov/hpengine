// MESH TRIANGLE ORDER OF URender::DrawLodMesh (disasm addresses below). Core's: which faces of a mesh a frame draws and in which order, the frontend emits them in that order.
//
//   enabled   PolyFlags & 0x10000004, PolyFlags = FUN_10b338f0 (style 3 -> 4) | (Opacity < 1 or unordered -> 0x10000004,
//             0x10b1119d), RenDev->SpanBased = 0 on D3D;
//   accepted  face loop 0x10b11255..0x10b1168d over the LOD faces in face order: AND of the three vertex outcodes == 0 and
//             ((MatFlags | PolyFlags) & PF_TwoSided or ((N.y·V0.y + N.z·V0.z) + N.x·V0.x)·Frame->Mirror < 0 or unordered),
//             N = (V0 − V1)×(V2 − V0) in frame space, the x87 chain of Face (PC24 registers, N stored as float);
//   key       Owner != Viewport->Actor: fistp(f32(((z0 + z1) + bias) + z2) · 1000)), bias = CollisionHeight · −0.3 when
//             bTwoSidedBias and the face's material is TwoSided; Owner == Viewport->Actor: H = (0, −8, 0),
//             fistp(f32((SA·SC)·SB)), SA/SB/SC = |H − V0/1/2|² as (z² + y²) + x²; render window PC24 = float steps;
//             fistp is round-to-nearest-even, out of range -> 0x80000000;
//   sort      FUN_10b14e40 (Sort): keys descending, comparisons on the wrapped int32 difference, not stable.
// HP2 (DrawLodMesh [HP2 0x10b0f800], the same face loop 0x10b10b10..0x10b10f5d): the facing dot, the far key and the
// viewer-owned key in the RU EA order; the rest of the body is HP1's (tools/hp2/render_diff.py; judge
// tools/hp2/render_a_judge.py face).
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

        // Original: Render.dll ?DrawLodMesh@URender@@QAEXPAUFSceneNode@@PAUFDynamicSprite@@PAVAActor@@ABVFCoords@@K@Z [HP1 0x10B0FF00] [HP2 0x10B0F800]
        // Branch A (0x10b11598..0x10b115b6).
        // RU EA (UEEdition.RuOrder; RU Render 0x10b10e6f..0x10b10e78) and HP2 (0x10b10e62..0x10b10e6b): ((bias + z2) + z0) + z1.
        public static int KeyFar(float z0, float z1, float z2, float bias)
        {
            float t;
            if (UEEdition.RuOrder || UEGameProfile.Current.Game == UEEdition.Game.HP2) { t = bias + z2; t += z0; t += z1; }
            else { t = z0 + z1; t += bias; t += z2; }
            t *= 1000f;   // 0x10b387d8
            return Fistp(t);
        }

        // Original: Render.dll ?DrawLodMesh@URender@@QAEXPAUFSceneNode@@PAUFDynamicSprite@@PAVAActor@@ABVFCoords@@K@Z [HP1 0x10B0FF00] [HP2 0x10B0F800]
        // Branch B (0x10b115c1..0x10b11673): H = (0, −8, 0) ([ebp-0xb8..-0xb0] from 0x10b0ff86..0x10b0ffb0), (SA·SC)·SB.
        // RU EA (UEEdition.RuOrder; RU Render 0x10b10e98..0x10b10f3b, H at 0x10b0f89c..0x10b0f8b0) and HP2 (0x10b10e8b..0x10b10f43;
        // H at 0x10b0f88c..0x10b0f8a0): (SC·SB)·SA, each S the same (z² + y²) + x².
        public static int KeyViewerOwned(UEVector v0, UEVector v1, UEVector v2)
        {
            static float Sq(UEVector v) { float x = 0f - v.x, y = -8f - v.y, z = 0f - v.z; return (z * z + y * y) + x * x; }
            float sa = Sq(v0), sb = Sq(v1), sc = Sq(v2);
            float p;
            if (UEEdition.RuOrder || UEGameProfile.Current.Game == UEEdition.Game.HP2) { p = sc * sb; p *= sa; }
            else { p = sa * sc; p *= sb; }
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

        // Original: Render.dll ?DrawLodMesh@URender@@QAEXPAUFSceneNode@@PAUFDynamicSprite@@PAVAActor@@ABVFCoords@@K@Z [HP1 0x10B0FF00] [HP2 0x10B0F800]
        // Faces of `triWedge` (3 wedges per face, face order) that the frame f accepts, in draw order. points — the pose in
        // the world (by wedgeVertex); triMaterial/materialFlags — the face's material flags. bias — CollisionHeight·−0.3f
        // applied when bTwoSidedBias (0x10b112b1..0x10b112d9). Not enabled: null (the frontend keeps its own layout).
        public static int[] Order(UESceneNode f, int[] triWedge, int[] wedgeVertex, int[] triMaterial, int[] materialFlags,
            UEVector[] points, int actorPolyFlags, bool twoSidedBias, float collisionHeight, bool ownerIsViewer)
        {
            if ((actorPolyFlags & PF_AlphaBlendWord) == 0) return null;
            FramePoints(f, points, out float[] fx, out float[] fy, out float[] fz, out byte[] oc);
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
                if (!Face(fx, fy, fz, oc, i0, i1, i2, matFlags | actorPolyFlags, f.Mirror, bias, ownerIsViewer, out int k)) continue;
                face[n] = t;
                key[n] = k;
                n++;
            }
            Sort(face, key, n);
            Array.Resize(ref face, n);
            return face;
        }

        // Original: Render.dll ?DrawLodMesh@URender@@QAEXPAUFSceneNode@@PAUFDynamicSprite@@PAVAActor@@ABVFCoords@@K@Z [HP1 0x10B0FF00] [HP2 0x10B0F800]
        // The face loop's accepted count (TArray Num [ebp-0x30], HP1 0x10b11692..0x10b11697, HP2 0x10b10f62..0x10b10f67): is
        // there at least one face the frame f accepts (Face, sorted or not). Zero -> DrawLodMesh jumps over the texture loop
        // and FLightManager::SetupForActor to its tail (HP1 0x10b11ff9, HP2 0x10b118cc): the mesh is not lit and draws nothing.
        public static bool AnyAccepted(UESceneNode f, int[] triWedge, int[] wedgeVertex, int[] triMaterial, int[] materialFlags,
            UEVector[] points, int actorPolyFlags)
        {
            FramePoints(f, points, out float[] fx, out float[] fy, out float[] fz, out byte[] oc);
            for (int t = 0; t < triWedge.Length / 3; t++)
            {
                int m = triMaterial is not null && t < triMaterial.Length ? triMaterial[t] : 0;
                int matFlags = materialFlags is not null && m >= 0 && m < materialFlags.Length ? materialFlags[m] : 0;
                if (Face(fx, fy, fz, oc, wedgeVertex[triWedge[t * 3]], wedgeVertex[triWedge[t * 3 + 1]], wedgeVertex[triWedge[t * 3 + 2]],
                    matFlags | actorPolyFlags, f.Mirror, 0f, false, out _)) return true;
            }
            return false;
        }

        // The pose in frame space and the outcodes of the face loop.
        static void FramePoints(UESceneNode f, UEVector[] points, out float[] fx, out float[] fy, out float[] fz, out byte[] oc)
        {
            BrushCoords c = f.Coords;
            int np = points.Length;
            fx = new float[np]; fy = new float[np]; fz = new float[np]; oc = new byte[np];
            for (int i = 0; i < np; i++)
            {
                UEVector p = points[i];
                float dx = p.x - c.Origin.x, dy = p.y - c.Origin.y, dz = p.z - c.Origin.z;
                float x = (dx * c.X.x + dy * c.X.y) + dz * c.X.z, y = (dx * c.Y.x + dy * c.Y.y) + dz * c.Y.z, z = (dx * c.Z.x + dy * c.Z.y) + dz * c.Z.z;
                fx[i] = x; fy[i] = y; fz[i] = z;
                float xm = z * f.PrjXM + x, ym = z * f.PrjYM + y, xp = z * f.PrjXP - x, yp = z * f.PrjYP - y;
                oc[i] = (byte)((Neg(xm) ? 4 : 0) | (Neg(xp) ? 8 : 0) | (Neg(ym) ? 0x10 : 0) | (Neg(yp) ? 0x20 : 0));
            }
        }

        // Original: Render.dll ?DrawLodMesh@URender@@QAEXPAUFSceneNode@@PAUFDynamicSprite@@PAVAActor@@ABVFCoords@@K@Z [HP1 0x10B0FF00] [HP2 0x10B0F800]
        // The draw loop's two-sided depth bias (HP1 0x10b11e8a..0x10b11e99, HP2 0x10b1175d..0x10b1176c; the -0.001 select HP1
        // Render.dll.c:14946-14948): for every accepted face (the face loop above, sorted or not) whose flags (MatFlags |
        // PolyFlags) carry PF_TwoSided while bTwoSidedBias (Actor +0x1bc & 0x40), each of its three wedges' RZ (+0x1c) becomes
        // RZ / (-0.001f·RZ + 1.0f) in place (fld/fmul/fadd/fdivr/fstp, float steps). The wedges are copied from the points once
        // before the loop, so a wedge used by n such faces is biased n times; ScreenX/Y keep the unbiased RZ. Returns per wedge
        // Z'/Z = RZ/RZ' (1 for unbiased wedges): the frame-space point scaled toward the frame origin keeps its screen position
        // and takes the biased depth. null: nothing biased.
        // NOT ORIGINAL (DECISIONS 2026-11-16 "two-sided depth bias capped at 800 wide"): RZ = ProjZ/Z grows with the frame
        // width, so the original's bias does too (0.2% of the depth at 800 wide, 1.3% at 5120: Harry's TwoSided robe came
        // through his neck in the Lev_Tut1 Dumbledore cutscene). Wider than BiasRefFX the bias runs on the RZ of an
        // 800-wide frame of the same FOV; at FX <= 800 (every live-original capture) the body is unchanged.
        // ponytail: the final per-wedge value; a wedge shared by two materials whose batch (FUN_10b0cda0) flushes between
        // them would show the earlier partial value — no such wedge known, read FUN_10b0cda0's flush if a mesh needs it.
        public const float BiasRefFX = 800f;
        public static float[] TwoSidedDepthScale(UESceneNode f, int[] triWedge, int[] wedgeVertex, int[] triMaterial, int[] materialFlags,
            UEVector[] points, int actorPolyFlags, bool twoSidedBias)
        {
            if (!twoSidedBias) return null;
            int nf = triWedge.Length / 3;
            int[] uses = null;
            byte[] oc = null; float[] fz = null;
            for (int t = 0; t < nf; t++)
            {
                int m = triMaterial is not null && t < triMaterial.Length ? triMaterial[t] : 0;
                int matFlags = materialFlags is not null && m >= 0 && m < materialFlags.Length ? materialFlags[m] : 0;
                if (((matFlags | actorPolyFlags) & PF_TwoSided) == 0) continue;
                if (oc is null) FramePoints(f, points, out _, out _, out fz, out oc);
                int w0 = triWedge[t * 3], w1 = triWedge[t * 3 + 1], w2 = triWedge[t * 3 + 2];
                if ((oc[wedgeVertex[w0]] & oc[wedgeVertex[w1]] & oc[wedgeVertex[w2]]) != 0) continue;   // TwoSided: no facing test
                uses ??= new int[wedgeVertex.Length];
                uses[w0]++; uses[w1]++; uses[w2]++;
            }
            if (uses is null) return null;
            var scale = new float[uses.Length];
            for (int w = 0; w < uses.Length; w++)
            {
                scale[w] = 1f;
                if (uses[w] == 0) continue;
                float rz0 = f.ProjZ / fz[wedgeVertex[w]] * (f.FX > BiasRefFX ? BiasRefFX / f.FX : 1f), rz = rz0;
                for (int k = 0; k < uses[w]; k++) rz = rz / (-0.001f * rz + 1f);
                scale[w] = rz0 / rz;
            }
            return scale;
        }

        // Original: Render.dll ?DrawLodMesh@URender@@QAEXPAUFSceneNode@@PAUFDynamicSprite@@PAVAActor@@ABVFCoords@@K@Z [HP1 0x10B0FF00] [HP2 0x10B0F800]
        // One face of the face loop in frame space (HP1 0x10b11419..0x10b1167d, HP2 0x10b10cd4..0x10b10f4d): rejected when the
        // three outcodes share a bit, or when flags (MatFlags | PolyFlags) lack PF_TwoSided and (N·V0)·Mirror >= 0 (ordered: a
        // NaN side is kept), N = (V0 − V1)×(V2 − V0); otherwise accepted with its sort key.
        internal static bool Face(float[] fx, float[] fy, float[] fz, byte[] oc, int i0, int i1, int i2, int flags, float mirror,
            float bias, bool ownerIsViewer, out int key)
        {
            key = 0;
            if ((oc[i0] & oc[i1] & oc[i2]) != 0) return false;
            if ((flags & PF_TwoSided) == 0)
            {
                // PC24 registers, each operation rounded to 24 bits with the register's exponent (0x10b11419..0x10b1147c):
                // B = V2 − V0 and (V0 − V1).x stay on the stack, (V0 − V1).z and .y are stored as float ([ebp-0x18], [ebp-0x78]),
                // N is stored as float (nz [ebp-0x9c], ny [ebp-0x38], nx [ebp-0x78]).
                static double R(double d) => HP1.Core.X87Ext.Round24(d);
                double bz = R((double)fz[i2] - fz[i0]), by = R((double)fy[i2] - fy[i0]), bx = R((double)fx[i2] - fx[i0]);
                double az = (float)R((double)fz[i0] - fz[i1]), ay = (float)R((double)fy[i0] - fy[i1]), ax = R((double)fx[i0] - fx[i1]);
                double nz = (float)R(R(ax * by) - R(ay * bx)), ny = (float)R(R(az * bx) - R(ax * bz)), nx = (float)R(R(ay * bz) - R(az * by));
                // The facing sum on the stack (0x10b11507..0x10b11522): ((ny·y + nz·z) + nx·x)·Mirror. RU EA (UEEdition.RuOrder;
                // RU Render 0x10b10ddb..0x10b10df9, the same stores) and HP2 (0x10b10dce..0x10b10dea): ((nx·x + ny·y) + nz·z)·Mirror.
                double side = UEEdition.RuOrder || UEGameProfile.Current.Game == UEEdition.Game.HP2 ? R(R(R(nx * fx[i0]) + R(ny * fy[i0])) + R(nz * fz[i0]))
                    : R(R(R(ny * fy[i0]) + R(nz * fz[i0])) + R(nx * fx[i0]));
                // fcomp 0.0; fnstsw; test ah, 1 (0x10b11525..0x10b11530): C0 is set below zero and for unordered.
                if (R(side * mirror) >= 0) return false;
            }
            key = ownerIsViewer
                ? KeyViewerOwned(new UEVector(fx[i0], fy[i0], fz[i0]), new UEVector(fx[i1], fy[i1], fz[i1]), new UEVector(fx[i2], fy[i2], fz[i2]))
                : KeyFar(fz[i0], fz[i1], fz[i2], bias);
            return true;
        }

        // Original: Render.dll ?DrawMesh@URender@@QAEXPAUFSceneNode@@PAUFDynamicSprite@@PAVAActor@@ABVFCoords@@K@Z [HP1 0x10B0E920]
        // (the UMesh branch's triangle list: face loop 0x10b0ef8b..0x10b0f19c, sort 0x10b0f1e7..0x10b0f20d, draw loop
        // 0x10b0f87a..0x10b0fd9e). The faces of a plain UMesh that frame f draws, in draw order.
        //   accepted  AND of the three outcodes == 0 and either ((PolyFlags | DrawMesh flags) & 0x4101) != 0x4000 — PF_Invisible
        //             or PF_TwoSided (the DrawActorSprite word FUN_10b338f0 never carries them; DrawMesh ors in PF_Flat 0x4000)
        //             — or ((N.z·V0.z + N.y·V0.y) + V0.x·N.x)·Mirror < 0 or unordered, N = (V0 − V1)×(V2 − V0) in frame space
        //             (V0 − V1 x, y spilled to float) times rsqrt(((Nx² + Ny²) + Nz²) + 0.001) of the fast tables;
        //   key       Owner != Viewport->Actor: fistp(f32((z2 + z0) + z1)) — no ×1000, unlike DrawLodMesh (0x10b0f0d0..0x10b0f0ea);
        //             Owner == Viewport->Actor: H = (0, −8, 0), fistp(f32((SC·SB)·SA)), S = (dz² + dy²) + dx² of H − V
        //             (0x10b0f0ef..0x10b0f170);
        //   sort      only when RenDev->SpanBased (0x10b0f1ea): FUN_10b14c20, instruction for instruction FUN_10b14e40 (Sort);
        //   drawn     the accepted faces in that order except PF_Invisible (the weapon triangle, 0x10b0f8a6).
        // Loud: Textures.Num > 16 (appFailAssert, 0x10b0f219), a drawn TextureIndex outside Textures (DAT_10b66310 then holds
        // another mesh's slot, 0x10b0f8c0).
        public static int[] UMeshOrder(UESceneNode f, Hp1VertexMesh m, UEVector[] points, bool spanBased, bool ownerIsViewer)
            => UMeshOrder(f, m, points, spanBased, ownerIsViewer, out _);
        // accepted: the face loop's count [ebp-0x64] (PF_Invisible faces included); 0 -> DrawMesh returns before SetupForActor
        // [HP1 0x10b0f1b0..0x10b0f1b8 -> 0x10b0fe8d] [HP2 0x10b0eafd..0x10b0eb05 -> 0x10b0f795].
        public static int[] UMeshOrder(UESceneNode f, Hp1VertexMesh m, UEVector[] points, bool spanBased, bool ownerIsViewer, out int accepted)
        {
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
            int nt = m.TriPolyFlags.Length, n = 0;
            var face = new int[nt]; var key = new int[nt];
            for (int t = 0; t < nt; t++)
            {
                int i0 = m.TriVerts[t * 3], i1 = m.TriVerts[t * 3 + 1], i2 = m.TriVerts[t * 3 + 2];
                if ((oc[i0] & oc[i1] & oc[i2]) != 0) continue;
                if ((m.TriPolyFlags[t] & (PF_TwoSided | 1)) == 0)
                {
                    float bx = fx[i2] - fx[i0], by = fy[i2] - fy[i0], bz = fz[i2] - fz[i0];
                    float ax = fx[i0] - fx[i1], ay = fy[i0] - fy[i1], az = fz[i0] - fz[i1];
                    float nx = ay * bz - az * by, ny = az * bx - bz * ax, nz = by * ax - ay * bx;
                    float s = SurfLightmap.GMathRsqrt(((nx * nx + ny * ny) + nz * nz) + 0.001f);
                    nx *= s; ny *= s; nz *= s;
                    if (((nz * fz[i0] + ny * fy[i0]) + fx[i0] * nx) * f.Mirror >= 0f) continue;
                }
                face[n] = t;
                if (ownerIsViewer)
                {
                    static float Sq(float x, float y, float z) { float hx = 0f - x, hy = -8f - y, hz = 0f - z; return (hz * hz + hy * hy) + hx * hx; }
                    float sa = Sq(fx[i0], fy[i0], fz[i0]), sb = Sq(fx[i1], fy[i1], fz[i1]), sc = Sq(fx[i2], fy[i2], fz[i2]);
                    key[n] = Fistp((sc * sb) * sa);
                }
                else key[n] = Fistp((fz[i2] + fz[i0]) + fz[i1]);
                n++;
            }
            accepted = n;
            if (n > 0 && m.TexturePath.Length > 16)
                throw new System.IO.InvalidDataException($"DrawMesh UMesh branch: {m.Name} has {m.TexturePath.Length} textures (appFailAssert Mesh->Textures.Num() <= 16)");
            if (spanBased) Sort(face, key, n);
            var draw = new List<int>(n);
            for (int k = 0; k < n; k++)
            {
                int t = face[k];
                if ((m.TriPolyFlags[t] & 1) != 0) continue;
                int tex = m.TriTextureIndex[t];
                if (tex < 0 || tex >= m.TexturePath.Length)
                    throw new System.IO.InvalidDataException(
                        $"DrawMesh UMesh branch: triangle {t} of {m.Name} has TextureIndex {tex} outside Textures[{m.TexturePath.Length}] (the original reads another mesh's slot)");
                draw.Add(t);
            }
            return draw.ToArray();
        }

        static bool Neg(float v) => float.IsNaN(v) || BitConverter.SingleToInt32Bits(v) < 0;
    }
}
