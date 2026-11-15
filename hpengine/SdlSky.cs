// Per-zone surface panning speeds. The sky frame (SkyZone of the window surface's zone, its Coords) and what it draws are
// Core occlusion's: WorldFrame.Views and WorldFrame.Drawn.
using HP1.Core;
using HP1.UEScript;

namespace HPEngine
{
    static class Sky
    {
        // Per-zone surface panning speeds, resolved by Core (SurfacePan.ZoneSpeeds: the zone's ZoneActor, the LevelInfo
        // for a zone without one); false for a zone Core does not resolve.
        public static WorldMesh.ZonePan PanSpeeds(StaticGeometry g, WorldFrame frame)
        {
            bool[] ok = SurfacePan.ZoneSpeeds(g, frame, out float[] u, out float[] v);
            int n = ok.Length;
            return (int zone, out float pu, out float pv) =>
            {
                pu = pv = 0;
                if (zone < 0 || zone >= n || !ok[zone]) return false;
                pu = u[zone]; pv = v[zone]; return true;
            };
        }
    }
}
