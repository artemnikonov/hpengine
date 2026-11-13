// URender::ClipDecal (Render.dll 0x10b27060, thunk 0x10b01082; Render.dll.c:2908-3293) —
// clips an FDecal shadow record against a BSP node polygon: the decal rectangle is cut by
// "viewer + polygon edge" planes, the remainder gets texture U/V.
//
// Ported from the DISASSEMBLY (the decompile of the point-insert block is garbled: Ghidra
// confuses locals). x87 operation order is kept op-for-op because DrawWorld runs at 24-bit
// precision (appEnableFastMath(1)), and under PC24 every x87 op rounds to float, exactly like
// C# float arithmetic. Bit-exact check against the real body under unicorn.
//
// Ported output is what the frame reads: point count, world points (static array
// DAT_10b673e0) and U/V (FTransTexture +0x50/+0x54). The screen part (projection, outcodes,
// light +0x30..+0x4c) does not go into the Core frame.
using System;
using System.Collections.Generic;

namespace HP1.UEScript
{
    public static class UEDecalClip
    {
        public readonly struct Point
        {
            public readonly UEVector World; public readonly float U, V;
            public Point(UEVector world, float u, float v) { World = world; U = u; V = v; }
        }

        // Static buffers of the body: DAT_10b673e0 and DAT_10b6d498, 24 FVector each
        // (static constructor on first entry, 0x18 entries); distances in DAT_10b6fed8.
        const int MaxPoints = 24;

        // x87 after fcomp: "less" is C0, "less or equal" is C0|C3; NaN sets both.
        static bool Lt(float a, float b) => !(a >= b);
        static bool Le(float a, float b) => !(a > b);

        // FSavedPoly points (frame axes) to world: TransformPointBy(Uncoords) inlined in ClipDecal (0x10b27265..0x10b272f5,
        // PC24): d = p − U.Origin, then ((d.x·A.x + d.y·A.y) + d.z·A.z) along Uncoords axes X/Y/Z.
        public static List<UEVector> SavedPolyWorld(UESceneNode f, IReadOnlyList<UEVector> view)
        {
            BrushCoords u = f.Uncoords;
            var w = new List<UEVector>(view.Count);
            foreach (UEVector p in view)
            {
                float dx = p.x - u.Origin.x, dy = p.y - u.Origin.y, dz = p.z - u.Origin.z;
                w.Add(new UEVector((dx * u.X.x + dy * u.X.y) + dz * u.X.z,
                                   (dx * u.Y.x + dy * u.Y.y) + dz * u.Y.z,
                                   (dx * u.Z.x + dy * u.Z.y) + dz * u.Z.z));
            }
            return w;
        }

        // normal — Vectors[Surf.vNormal], basePt — Points[Surf.pBase]; v0..v3 — FDecal.Vertices
        // (relative to pBase); drawScale is Decal.Actor +0x16c; polygon is the node points in world space;
        // frame: viewer position in viewer, Uncoords identity (as the Core frame builds it).
        public static int Clip(UEVector normal, UEVector basePt, UEVector v0, UEVector v1, UEVector v2, UEVector v3,
                               float drawScale, UEVector viewer, IReadOnlyList<UEVector> polygon, List<Point> result)
        {
            result.Clear();
            int np = polygon.Count;
            if (np > 64) throw new ArgumentException($"ClipDecal: polygon of {np} points");

            // 0x10b2717e-0x10b271e5: surface normal, normalized when |n|² >= 1e-8.
            float nx = normal.x, ny = normal.y, nz = normal.z;
            float s = (nx * nx + ny * ny) + nz * nz;
            if (!Lt(s, 9.99999993922529e-09f))
            {
                float inv = 1.0f / (float)Math.Sqrt(s);
                nx *= inv; ny *= inv; nz *= inv;
            }
            // 0x10b271e7-0x10b2723f: the four decal corners, raised along the normal.
            Span<float> px = stackalloc float[MaxPoints], py = stackalloc float[MaxPoints], pz = stackalloc float[MaxPoints];
            Span<float> dist = stackalloc float[MaxPoints];
            UEVector V(int i) => i == 0 ? v0 : i == 1 ? v1 : i == 2 ? v2 : v3;
            int count = 4;
            for (int i = 0; i < 4; i++)
            {
                UEVector d = V(i);
                px[i] = (d.x + basePt.x) + nx; py[i] = (d.y + basePt.y) + ny; pz[i] = (d.z + basePt.z) + nz;
            }
            // 0x10b27241-0x10b272f0: polygon points through Uncoords (identity, origin 0).
            Span<float> qx = stackalloc float[np], qy = stackalloc float[np], qz = stackalloc float[np];
            for (int i = 0; i < np; i++)
            {
                float dx = polygon[i].x - 0f, dy = polygon[i].y - 0f, dz = polygon[i].z - 0f;
                qx[i] = (dx * 1f + dy * 0f) + dz * 0f;
                qy[i] = (dx * 0f + dy * 1f) + dz * 0f;
                qz[i] = (dx * 0f + dy * 0f) + dz * 1f;
            }
            // 0x10b272f5-0x10b27612: plane through the viewer and the edge (prev -> cur).
            for (int cur = 0, prev = np - 1; cur < np; prev = cur, cur++)
            {
                float wz = viewer.z - qz[cur], wy = viewer.y - qy[cur], wx = viewer.x - qx[cur];
                float ez = qz[prev] - qz[cur], ey = qy[prev] - qy[cur], ex = qx[prev] - qx[cur];
                float Nz = ex * wy - ey * wx;
                float Ny = ez * wx - ex * wz;
                float Nx = wz * ey - ez * wy;
                float D = (Nx * qx[cur] + qy[cur] * Ny) + qz[cur] * Nz;
                for (int i = 0; i < count; i++)
                    dist[i] = ((Nz * pz[i] + Ny * py[i]) + Nx * px[i]) - D;
                // 0x10b273fb-0x10b2756f: intersection point on edges with a sign change.
                for (int k = 0; k < count; k++)
                {
                    int n = k + 1 == count ? 0 : k + 1;
                    bool cross = (!Le(dist[k], 0f) && Lt(dist[n], 0f)) || (Lt(dist[k], 0f) && !Le(dist[n], 0f));
                    if (!cross) continue;
                    float tx = px[n] - px[k], ty = py[n] - py[k], tz = pz[n] - pz[k];
                    float denom = (tz * Nz + ty * Ny) + tx * Nx;                  // FVector::operator| (Core 0x10119190)
                    float t = (D - ((Nz * pz[k] + Ny * py[k]) + Nx * px[k])) / denom;
                    float ix = px[k] + tx * t, iy = py[k] + ty * t, iz = pz[k] + tz * t;
                    if (count >= MaxPoints)
                        throw new InvalidOperationException($"ClipDecal: more than {MaxPoints} points — shipped would overrun the static buffer DAT_10b673e0");
                    for (int m = count; m > k + 1; m--) { dist[m] = dist[m - 1]; px[m] = px[m - 1]; py[m] = py[m - 1]; pz[m] = pz[m - 1]; }
                    dist[k + 1] = 0f; px[k + 1] = ix; py[k + 1] = iy; pz[k + 1] = iz;
                    count++;
                    k++;                                                           // the inserted point is not tested
                }
                // 0x10b27574-0x10b275e7: points on the negative side are removed.
                for (int i = 0; i < count; i++)
                {
                    if (!Lt(dist[i], 0f)) continue;
                    for (int m = i; m < count - 1; m++) { dist[m] = dist[m + 1]; px[m] = px[m + 1]; py[m] = py[m + 1]; pz[m] = pz[m + 1]; }
                    count--; i--;
                }
                if (count == 0) return 0;
            }
            // 0x10b278ed-0x10b27ab1: U along V1−V0, V along V3−V0, in DrawScale units.
            for (int i = 0; i < count; i++)
            {
                float ax = v1.x - v0.x, ay = v1.y - v0.y, az = v1.z - v0.z;
                float inv1 = 1.0f / (float)Math.Sqrt((ax * ax + ay * ay) + az * az);
                float ux = inv1 * ax, uy = inv1 * ay, uz = inv1 * az;
                float rx = (px[i] - basePt.x) - v0.x, ry = (py[i] - basePt.y) - v0.y, rz = (pz[i] - basePt.z) - v0.z;
                float u = ((rx * ux + ry * uy) + rz * uz) / drawScale;
                float bx = v3.x - v0.x, by = v3.y - v0.y, bz = v3.z - v0.z;
                float inv3 = 1.0f / (float)Math.Sqrt((bx * bx + by * by) + bz * bz);
                float vx = inv3 * bx, vy = inv3 * by, vz = inv3 * bz;
                float v = ((rz * vz + ry * vy) + vx * rx) / drawScale;
                result.Add(new Point(new UEVector(px[i], py[i], pz[i]), u, v));
            }
            return count;
        }
    }
}
