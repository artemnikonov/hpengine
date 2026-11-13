// Mesh PF_Environment UV and light — Core's (the frontend calls it per vertex). World-space form of the view-space body: the same values up to rounding (within render tolerance).
using System;

namespace HP1.UEScript
{
    public static class EnviroMapping
    {
        // Mesh PF_Environment: FUN_10b0d3c0 (Render.dll 0x10b0d3f9-0x10b0d621, disassembly): vertex point P and normal N
        // in view coordinates; D = P/|P| (1.0 .rdata 0x10b38678, appSqrt), R = D - N·2(D·N), T = R.TransformVectorBy(
        // Frame->Uncoords, FSceneNode+0x64), i.e. the reflected view ray IN WORLD space (computed directly in world here).
        // U = (T.x + 1)·UScale·128, V = (T.y + 1)·VScale·128 (1.0 0x10b385b0, 128 0x10b387bc; scale 0x10b65a00 =
        // USize·UScale/256), i.e. in texture fractions (T.x+1)/2, (T.y+1)/2. Vertex light (+0x30..+0x38) =
        // pow(max(T.z, 0), 0.25) (0.0 0x10b385bc, 0x3fd00000 = 0.25, appPow), +0x3c = 0. The sign of N does not affect R.
        public static (float u, float v, float light) Of(UEVector p, UEVector n, UEVector eye)
        {
            float dx = p.x - eye.x, dy = p.y - eye.y, dz = p.z - eye.z;
            float inv = (float)(1.0 / Math.Sqrt(dx * dx + dy * dy + dz * dz));
            dx *= inv; dy *= inv; dz *= inv;
            float k = 2f * (dx * n.x + dy * n.y + dz * n.z);
            float tx = dx - n.x * k, ty = dy - n.y * k, tz = dz - n.z * k;
            return ((tx + 1f) * 0.5f, (ty + 1f) * 0.5f, (float)Math.Pow(Math.Max(tz, 0f), 0.25));
        }
    }
}
