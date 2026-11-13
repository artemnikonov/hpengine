// MESH LEVEL OF DETAIL — port of the LOD selection and collapse of URender::DrawLodMesh (Render.dll 1.53, D-62).
//
// Selection, disasm 0x10b0ffcd..0x10b1011d (decompile Render.dll.c:14110-14146), in the render window (x87 PC24: every
// step below is a float operation):
//   LOD enabled Mesh->LODStrength (+0x18c) != 0 and Mesh->CollapsePointThus.Num (+0x130) != 0, otherwise no LOD;
//   lod       = Owner->LODBias (+0x78) / (Render->GlobalShapeLODAdjust (+0x50) * Render->GlobalShapeLOD (+0x4c) * LODStrength);
//   area      = (Sprite->Y2 - Sprite->Y1 + 1) * (Sprite->X2 - Sprite->X1 + 1)        (FDynamicSprite +0x9c..+0xa8, int);
//   r1        = area * 0.04f (.rdata 0x10b387e0) / (float)ModelVerts (+0x180);
//   r2        = appSqrt(area) / Frame->FX (+0xb8) * 4.0 (.rdata 0x10b38600);
//   verts     = (ModelVerts - LODMinVerts (+0x190)) * (max(r1, r2) * lod) + LODMinVerts  (r1 when r1 >= r2, else r2);
//   ShapeLODMode (+0x54) == 0: verts = ShapeLODFix (+0x58) == 0.0 (.rdata 0x10b385bc) ? ModelVerts : ModelVerts * ShapeLODFix;
//   n         = Min(fistp(verts) (round to nearest even), ModelVerts); n >= ModelVerts -> no LOD.
// URender::Init (Render.dll.c:15206-15212) sets GlobalShapeLOD = GlobalShapeLODAdjust = ShapeLODFix = 1.0 and ShapeLODMode = 1;
// the only other writers are the copy constructor / operator= and the console commands MLOD / MLMODE / MLFIX
// (URender::Exec, Render.dll.c:29186-29260; GlobalShapeLODAdjust has no command). Those commands are not ported.
// URender::PostRender rewrites GlobalShapeLODAdjust every frame; the port does not (decided improvement, see the constant).
// GetFrame then clamps the request through its LODRequest reference (0x10b10255): USkeletalMesh::GetFrame
// (Engine.dll.c:29208) n = Min(n, FrameVerts); ULodMesh::GetFrame (Engine.dll.c:57244) n = Min(SpecialVerts + n, FrameVerts)
// - SpecialVerts (SpecialVerts > 0 is refused by UELevelRuntime.RenderMeshFrame).
//
// Collapse, the face loop (Render.dll.c:14590-14640, disasm from 0x10b10a00): a face is drawn only when
// FaceLevel[face] <= n; with LOD each of its wedges w is replaced while Wedges[w].iVertex >= n by
// CollapseWedgeThus[w] — the final wedge gives both the point and the UV. Without LOD the face's own wedges are drawn.
using System;
using System.Collections.Generic;

namespace HP1.UEScript
{
    public static class UEMeshLod
    {
        // URender::Init values (see the header).
        // GlobalShapeLODAdjust: NOT ORIGINAL: shipped URender::PostRender (Render.dll FUN_10b1ef80) raises it per frame below
        // MinDesiredFrameRate fps; the port keeps the Init value 1.0.
        public const float GlobalShapeLOD = 1f, GlobalShapeLODAdjust = 1f, ShapeLODFix = 1f;
        public const int ShapeLODMode = 1;
        const float AreaScale = 0.04f;                     // 0x10b387e0 = 0x3d23d70a
        const double SizeScale = 4.0;                      // 0x10b38600

        // Requested vertex count after the GetFrame clamp, or -1 when this draw has no LOD (the full mesh).
        public static int VertsRequested(Hp1MeshLod lod, int modelVerts, int specialVerts, bool skeletal, float lodBias,
            int x1, int y1, int x2, int y2, float frameFX,
            float globalShapeLOD = GlobalShapeLOD, float adjust = GlobalShapeLODAdjust, int mode = ShapeLODMode, float fix = ShapeLODFix)
        {
            if (lod.Strength == 0f || lod.CollapsePointCount == 0) return -1;
            float meshLod = lodBias / (adjust * globalShapeLOD * lod.Strength);
            int area = unchecked((y2 - y1 + 1) * (x2 - x1 + 1));
            float mv = modelVerts;
            float r1 = (float)area * AreaScale / mv;
            float r2 = (float)((double)MathF.Sqrt(area) / frameFX * SizeScale);
            float r = r1 >= r2 ? r1 : r2;
            float verts = (float)(modelVerts - lod.MinVerts) * (r * meshLod) + lod.MinVerts;
            if (mode == 0) verts = fix == 0f ? mv : mv * fix;
            int n = Fistp(verts);
            if (n > modelVerts) n = modelVerts;
            if (n >= modelVerts) return -1;
            n = skeletal ? Math.Min(n, lod.FrameVerts) : Math.Min(specialVerts + n, lod.FrameVerts) - specialVerts;
            return n;
        }

        // fistp under the default round-to-nearest; out of range -> the integer indefinite 0x80000000.
        static int Fistp(float v)
        {
            double r = Math.Round(v, MidpointRounding.ToEven);
            return r >= int.MinValue && r <= int.MaxValue ? (int)r : int.MinValue;
        }

        // Faces drawn at n (source face order): the three wedges of each kept face after the collapse; face indices in kept.
        public static int[] CollapsedFaces(Hp1MeshLod lod, int[] faceWedges, int[] wedgePoint, int n, List<int> kept)
        {
            var tri = new List<int>(faceWedges.Length);
            for (int f = 0; f < lod.FaceLevel.Length; f++)
            {
                if (lod.FaceLevel[f] > n) continue;
                kept.Add(f);
                for (int k = 0; k < 3; k++)
                {
                    int w = faceWedges[f * 3 + k];
                    for (int steps = 0; wedgePoint[w] >= n; steps++)
                    {
                        if (steps > wedgePoint.Length)
                            throw new InvalidOperationException($"DrawLodMesh collapse: wedge chain from {faceWedges[f * 3 + k]} does not reach a point below {n}");
                        w = lod.CollapseWedge[w];
                    }
                    tri.Add(w);
                }
            }
            return tri.ToArray();
        }
    }
}
