// Affine view mapping and BSP portal with a warp zone (types shared by Core and HPEngine).
//
// Child frames (sky, mirrors, warp portals) are built by Core occlusion (UEOcclusion, port of URender::OccludeBsp and
// CreateChildFrame) and handed out in WorldFrame.Views (D-61): each frame is an Affine "frame world -> master-frame
// world" plus NearClip. The frontend does not compute them. Only batch tagging remains here: zones with an
// AWarpZoneInfo (Warp.ZoneSet), whose invisible portals become portal batches of the world mesh (WorldMesh.WarpPortals).
using System;
using System.Collections.Generic;

namespace HP1.UEScript
{
    // 3×4 rows: p' = (r0·p + r0.w, r1·p + r1.w, r2·p + r2.w).
    public struct Affine
    {
        public float M00, M01, M02, M03, M10, M11, M12, M13, M20, M21, M22, M23;
        public static Affine Identity => new() { M00 = 1, M11 = 1, M22 = 1 };

        public UEVector Apply(UEVector p) => new(M00 * p.x + M01 * p.y + M02 * p.z + M03, M10 * p.x + M11 * p.y + M12 * p.z + M13, M20 * p.x + M21 * p.y + M22 * p.z + M23);

        // Reflection about n·p + d = 0 (same as the renderer's ReflectRows).
        public static Affine Reflect(UEVector n, float d) => new()
        {
            M00 = 1 - 2 * n.x * n.x, M01 = -2 * n.x * n.y, M02 = -2 * n.x * n.z, M03 = -2 * n.x * d,
            M10 = -2 * n.y * n.x, M11 = 1 - 2 * n.y * n.y, M12 = -2 * n.y * n.z, M13 = -2 * n.y * d,
            M20 = -2 * n.z * n.x, M21 = -2 * n.z * n.y, M22 = 1 - 2 * n.z * n.z, M23 = -2 * n.z * d,
        };
    }

    // BSP portal with a warp zone on one side (WorldMesh.WarpPortals): surface, node plane, zones.
    public sealed class WarpPortal
    {
        public int Surf, Zone0, Zone1, PolyFlags;
        public UEVector Normal; public float Dist;   // node FPlane: N·p = Dist
    }

    public static class Warp
    {
        // Zones whose ZoneActor is an AWarpZoneInfo (by snapshot class; static per level).
        public static HashSet<int> ZoneSet(StaticGeometry g, WorldFrame f)
        {
            var set = new HashSet<int>();
            if (g.ZoneActorExports is null) return set;
            var warpExports = new HashSet<int>();
            foreach (ActorSnapshot a in f.Actors)
                if (a.Placed && a.ExportIndex > 0 && UEInterpreter.ClassIsA(HP1.Core.UEClassRegistry.Shared?.Find(a.Class), "WarpZoneInfo")) warpExports.Add(a.ExportIndex);
            for (int z = 0; z < g.ZoneActorExports.Length; z++)
                if (g.ZoneActorExports[z] > 0 && warpExports.Contains(g.ZoneActorExports[z])) set.Add(z);
            return set;
        }
    }
}
