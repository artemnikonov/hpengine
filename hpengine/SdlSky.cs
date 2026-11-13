// Per-zone surface panning speeds. The sky frame (SkyZone of the window surface's zone, its Coords) and what it draws are
// Core occlusion's: WorldFrame.Views and WorldFrame.Drawn.
using HP1.Core;
using HP1.UEScript;

namespace HPEngine
{
    static class Sky
    {
        static bool IsA(string cls, string baseName)
        {
            if (string.IsNullOrEmpty(cls)) return false;
            var c = UEClassRegistry.Shared?.Find(cls);
            return c != null && UEInterpreter.ClassIsA(c, baseName);
        }

        // Per-zone surface panning speeds: the zone's ZoneActor (ZoneActorExports); for a zone without one,
        // LevelInfo (Actors(0)); a ZoneActor not in the frame is a refusal.
        public static WorldMesh.ZonePan PanSpeeds(StaticGeometry g, WorldFrame frame)
        {
            var byExport = new Dictionary<int, ActorSnapshot>();
            ActorSnapshot? levelInfo = null;
            foreach (ActorSnapshot a in frame.Actors)
            {
                if (!a.Placed || a.ExportIndex <= 0) continue;
                byExport[a.ExportIndex] = a;
                if (levelInfo is null && IsA(a.Class, "LevelInfo")) levelInfo = a;
            }
            int n = g.ZoneActorExports?.Length ?? 0;
            var u = new float[n]; var v = new float[n]; var ok = new bool[n];
            for (int z = 0; z < n; z++)
            {
                int ex = g.ZoneActorExports[z];
                ActorSnapshot? za = ex <= 0 ? levelInfo : byExport.TryGetValue(ex, out var x) ? x : null;
                if (za is null) continue;
                u[z] = za.Value.TexUPanSpeed; v[z] = za.Value.TexVPanSpeed; ok[z] = true;
            }
            return (int zone, out float pu, out float pv) =>
            {
                pu = pv = 0;
                if (zone < 0 || zone >= n || !ok[zone]) return false;
                pu = u[zone]; pv = v[zone]; return true;
            };
        }
    }
}
