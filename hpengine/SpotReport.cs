// NOT ORIGINAL — "report this spot" (F12, unbound in HP1's and HP2's DefUser.ini): the player presses it at a visual problem
// and the run log gets one block describing the frame, plus the frontend's own screenshot next to the log.
// Diagnostic only: it reads the published WorldFrame (Core's frame data) and the frontend's batches; Core's live objects are
// read (never written) only for the brush tracker pose (SavedPos/SavedRot) and only while the Core thread is idle (§2).
// Per object the block names the stage that decided it:
//   movers — every mover of the level; for each its brush polygons, how many Core's occlusion drew in any view of the tree (tracker
//            fragments drawn: frustum, zone/portal and span-buffer decisions are all inside OccludeBsp), the coplanar-cut
//            pieces, and how many triangles the frontend emitted for it (MoverScene.Draws);
//   actors — every actor within Radius of the camera or based on a mover: hidden flag, DrawType, whether Core's DrawFrame
//            drew it (WorldFrame.ActorViews / SpriteActors) and whether the frontend emitted a batch for it (the frame's
//            whole actor batch list: meshes, sprites, particles, decals; an emitter with no live particle has none).
using System.Globalization;
using System.Text;
using HP1.UEScript;

namespace HPEngine
{
    static class SpotReport
    {
        public const float Radius = 1500f;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static string V(UEVector v) => string.Format(Inv, "{0:0.##},{1:0.##},{2:0.##}", v.x, v.y, v.z);

        public static string Build(WorldFrame frame, in Camera cam, string frameCamera, int viewActorId, int width, int height, bool enhanced, string map, int tick,
            float levelTime, MoverScene movers, IEnumerable<ActorDraw> actorDraws, Func<int, UEObject> liveMover)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[Spot] ==== report: map {map} tick {tick} level time {levelTime.ToString("0.###", Inv)} s, {width}x{height}, visual {(enhanced ? "Enhanced" : "Original")}");
            sb.AppendLine(string.Format(Inv, "[Spot] camera {0:0.##},{1:0.##},{2:0.##} rot pitch {3:0.##} yaw {4:0.##} roll {5:0.##} deg, fov {6:0.##}; frame camera {7}",
                cam.X, cam.Y, cam.Z, cam.Pitch, cam.Yaw, cam.Roll, cam.Fov, frameCamera));
            var byId = new Dictionary<int, ActorSnapshot>();
            foreach (ActorSnapshot a in frame.Actors) byId[a.ObjectId] = a;
            if (byId.TryGetValue(viewActorId, out ActorSnapshot viewer))
                sb.AppendLine($"[Spot] view actor {viewer.Name} at {V(viewer.Location)} base {viewer.Base} zone {viewer.ZoneNumber} leaf {viewer.Leaf}");
            var camLoc = new UEVector(cam.X, cam.Y, cam.Z);
            float Dist(UEVector p) { float dx = p.x - camLoc.x, dy = p.y - camLoc.y, dz = p.z - camLoc.z; return MathF.Sqrt(dx * dx + dy * dy + dz * dz); }

            // Movers: Core's drawn polygons (every view), cut pieces, frontend triangles.
            var drawnPolys = new Dictionary<int, HashSet<int>>();
            if (frame.Drawn is not null)
                foreach (DrawnWorld dw in frame.Drawn)
                    foreach (ViewSource m in dw.Movers)
                    {
                        if (!drawnPolys.TryGetValue(m.MoverId, out var set)) drawnPolys[m.MoverId] = set = new HashSet<int>();
                        set.Add(m.MoverPoly);
                    }
            if (movers is not null)
            {
                foreach (UEMoverGeometry.Mover mv in movers.Geo.Movers)
                {
                    int id = mv.Actor.ObjectId;
                    if (!byId.TryGetValue(id, out ActorSnapshot a)) { sb.AppendLine($"[Spot] mover id {id}: not in the frame"); continue; }
                    int polys = movers.PolyCount(mv);
                    int core = drawnPolys.TryGetValue(id, out var ds) ? ds.Count : 0;
                    int tris = movers.EmittedTriangles(id), cut = movers.CutPolygons(id);
                    string pose = "-";
                    if (liveMover?.Invoke(id) is UEObject o)
                        pose = $"SavedPos {V(UEValue.AsVector3(o.GetProp("SavedPos")))} SavedRot {(o.GetProp("SavedRot") is UERotator r ? $"{r.Pitch},{r.Yaw},{r.Roll}" : "-")}";
                    string stage = core == 0 ? "DROPPED by Core occlusion (no tracker fragment drawn: frustum / zone-portal / span buffer)"
                        : tris == 0 ? "DROPPED by the frontend (Core drew polygons, no triangle emitted)" : "drawn";
                    sb.AppendLine($"[Spot] mover {a.Name} at {V(a.Location)} rot {a.Rotation.Pitch},{a.Rotation.Yaw},{a.Rotation.Roll} zone {a.ZoneNumber} leaf {a.Leaf} "
                        + $"dist {Dist(a.Location).ToString("0", Inv)} hidden {a.Hidden} state {a.State} | tracker {pose} | "
                        + $"polys {polys}, Core drew {core} [{(ds is null ? "" : string.Join(",", ds.OrderBy(x => x)))}], cut {cut}, frontend tris {tris} -> {stage}");
                }
            }

            // Actors near the camera or in its cone.
            var emitted = new HashSet<int>();
            foreach (ActorDraw d in actorDraws) emitted.Add(d.ActorId);
            var moverNames = new HashSet<string>();
            foreach (ActorSnapshot m in frame.Actors) if (m.DrawType == 3 && m.Mover.NumKeys > 0) moverNames.Add(m.Name);
            var sprites = new HashSet<int>(frame.SpriteActors ?? Array.Empty<int>());
            foreach (ActorSnapshot a in frame.Actors)
            {
                if (a.Destroyed || a.DrawType == 3 || a.DrawType == 0) continue;   // brushes above; DT_None
                bool onMover = a.Base != "-" && moverNames.Contains(a.Base);   // e.g. a chest attached to a lowering stand
                if (!(Dist(a.Location) <= Radius || onMover)) continue;
                if (a.Hidden && !onMover) continue;   // editor-only icons (lights, path nodes): noise
                bool coreDrew = frame.ActorViews.SetOf.ContainsKey(a.ObjectId) || sprites.Contains(a.ObjectId);
                bool front = emitted.Contains(a.ObjectId);
                string stage = a.Hidden ? "hidden (bHidden)" : !coreDrew ? "not drawn by Core (SetupDynamics / occlusion / not in view)"
                    : !front ? "DROPPED by the frontend (Core drew it, no batch)" : "drawn";
                sb.AppendLine($"[Spot] actor {a.Name} ({a.Class}) at {V(a.Location)} base {a.Base} zone {a.ZoneNumber} leaf {a.Leaf} dist {Dist(a.Location).ToString("0", Inv)}"
                    + $" drawtype {a.DrawType} mesh {a.MeshPath ?? "-"} -> {stage}");
            }
            sb.Append("[Spot] ==== end");
            return sb.ToString();
        }
    }
}
