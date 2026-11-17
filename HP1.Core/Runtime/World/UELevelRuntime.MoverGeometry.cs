// MOVER GEOMETRY FOR DRAWING (FPoly branch), computed in Core at load time.
//
// Rules:
//   * a mover's drawn polygon is the brush FPoly (brush->Polys); its lightmap is addressed by iBrushPoly, not
//     surf.iLightMap (−1 on mover surfaces, measured on three maps);
//   * world points and vectors are the FPoly through ABrush::BuildCoords + FPoly::Transform (Engine.dll.c:49283, 0x10396770;
//     the tracker's own bodies, HeadlessBspModel.MovingBrushTracker, judged by validate_brush.py layer 1c) at the mover's
//     placement when the geometry is built — any MainScale and PostScale (a sheer is refused, below). The drawn UVs (P−Base)·TextureU do not
//     depend on that placement (TextureU/V go through VectorXform, the inverse-transpose of PointXform), so it is only the
//     frame the UVs are computed in; vertex order stays the FPoly's (Transform reverses it for a negative orientation);
//   * the lightmap is NOT lit here: SetupForSurf lights the tracker surface of the frame (UEMoverLightmaps.TrackerFace);
//   * all movers form one synthetic model: LightMap records offset into the shared LightBits, light
//     lists (Model->Lights) offset into the shared pool (UEMoverLightmaps reads them per lightmap).
// Draw position — every frame: the brush-local point via the frame's MoverState.ToWorld.
// Node zone — the mover's zone at build time (zoneOf): the frontend atlas slot of the lightmap, not a lighting input.
// A brush without UPolys (polygons are the brush's BSP nodes) — loud failure: the original faults there
// (SetupActorBrush reads Brush->Polys->Num with no null test, Engine 0x1038d601/0x1038d604).
using System;
using System.Collections.Generic;
using System.Linq;
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    public sealed class UEMoverGeometry
    {
        public sealed class Mover
        {
            public UEObject Actor;
            public int FirstNode, NodeCount;
            public int FirstLightMap, LightMapCount;   // the mover's lightmap records in the shared model
        }

        public StaticGeometry Geometry;
        public byte[] LightBits;
        public Vector3[] LocalOfPoint;       // brush point (for node vertices); unused for other points
        public int[] MoverOfNode;            // node -> index in Movers
        public readonly List<Mover> Movers = new();
        public readonly List<string> Problems = new();
        // Surface -> FPoly number of its mover's brush (UPolys; the same number as the tracker surface: SetupActorBrush).
        public readonly List<int> PolyOfSurf = new();
        public int PolysWithLightmap, PolysWithoutLightmap;
    }

    public sealed partial class UELevelRuntime
    {
        public UEMoverGeometry BuildMoverGeometry(Func<UEObject, int> zoneOf)
        {
            var r = new UEMoverGeometry();
            var points = new List<Vector3>();
            var local = new List<Vector3>();
            var vectors = new List<Vector3>();
            var vertPool = new List<int>();
            var surfs = new List<StaticSurface>();
            var nodes = new List<StaticNode>();
            var lightMaps = new List<StaticLightMap>();
            var lightBits = new List<byte>();
            var lights = new List<int>();
            var moverOfNode = new List<int>();

            // Order = map export index: it determines the lightmap record number and the atlas
            // layout. Spawned movers (no export) come after placed ones, in level order.
            var movers = new List<UEObject>();
            foreach (UEObject actor in Actors)
                if (actor is not null && !actor.Destroyed && UEInterpreter.ClassIsA(actor.Class, "Mover")) movers.Add(actor);
            int ExportOf(UEObject a) { int e = UEInterpreter.ToInt(a.GetProp(PK.ExportIndex)); return e > 0 ? e : int.MaxValue; }
            movers = movers.Select((a, i) => (a, i)).OrderBy(t => ExportOf(t.a)).ThenBy(t => t.i).Select(t => t.a).ToList();
            foreach (UEObject actor in movers)
            {
                string name = actor.GetProp(PK.Name)?.ToString();
                if (actor.GetProp(PK.Brush) is not UEModelRef) { r.Problems.Add($"{name}: the mover has no brush (Brush)"); continue; }
                HeadlessBspModel model = BrushModelOf(actor);
                int polysRef = model.ParseTables.PolysRef;
                if (polysRef <= 0) { r.Problems.Add($"{name}: the brush has no UPolys — the brush node branch is not ported"); continue; }
                // A sheer: shipped DRAWS the tracker fragments (BuildCoords + FPoly::Transform: Location added after the
                // transform), the frontend places the polygons with MoverState.ToWorld = ABrush::ToWorld, which moves the
                // Location through the sheer (FCoords *= FScale moves Origin like an axis): Lev_Tut1 Mover40 with a 0.3
                // PostScale sheer 1634 units apart, equal at Location 0 (SelfTest mover_sheer_refused_drawn_at_toworld).
                // Refused, not drawn misplaced; no shipped brush shears (census of all 41 maps, v030 area C report).
                if (BrushCoords.SnapSheer(BrushSheerOf(actor, "MainScale")) != 0f || BrushCoords.SnapSheer(BrushSheerOf(actor, "PostScale")) != 0f)
                { r.Problems.Add($"{name}: sheer — drawn at ABrush::ToWorld, the original draws FPoly::Transform: placement not ported"); continue; }

                List<HeadlessBspModel.PolyRecord> polys = HeadlessBspModel.ReadPolys(_brushMapPath, polysRef);
                StaticGeometry bg = model.BuildStaticGeometry();
                byte[] bits = model.ParseTables.LightBits ?? Array.Empty<byte>();
                int[] brushLights = model.ParseTables.Lights ?? Array.Empty<int>();
                HeadlessBspModel.BrushPlacement pl = BrushPlacementOf(actor);
                HeadlessBspModel.MovingBrushTracker.BuildCoords<FpuPc64>(pl, out float[] px, out float[] vx, out float orientation);

                int lmBase = lightMaps.Count, bitsBase = lightBits.Count, lightBase = lights.Count;
                foreach (StaticLightMap lm in bg.LightMaps)
                    lightMaps.Add(new StaticLightMap(lm.DataOffset + bitsBase, lm.Pan, lm.UClamp, lm.VClamp, lm.UScale, lm.VScale,
                        lm.LightActors < 0 ? -1 : lm.LightActors + lightBase));
                lightBits.AddRange(bits);
                lights.AddRange(brushLights);

                int zone = zoneOf(actor);
                // Surface flags — as the FBspSurf the tracker puts into the level model
                // (FMovingBrushTracker::SetupActorBrush, Engine.dll 0x1038d8a1..0x1038d8c1): FPoly.PolyFlags &
                // 0x3cffffff, plus PF_SpecialLit when the mover has bSpecialLit (Actor+0x1f0 bit 0) — read by drawing
                // drawing.
                bool specialLit = UEInterpreter.ToBool(actor.GetProp(PK.bSpecialLit));
                var m = new UEMoverGeometry.Mover { Actor = actor, FirstNode = nodes.Count, FirstLightMap = lmBase, LightMapCount = bg.LightMaps.Length };
                int polyIndex = -1;
                foreach (HeadlessBspModel.PolyRecord fp in polys)
                {
                    polyIndex++;
                    if (fp.Vertices is null || fp.Vertices.Length < 3) continue;
                    r.PolyOfSurf.Add(polyIndex);
                    bool hasLm = fp.iBrushPoly >= 0 && fp.iBrushPoly < bg.LightMaps.Length;
                    if (hasLm) r.PolysWithLightmap++; else r.PolysWithoutLightmap++;
                    var w = new HeadlessBspModel.WorkPoly
                    {
                        Bx = fp.Base.x,
                        By = fp.Base.y,
                        Bz = fp.Base.z,
                        Nx = fp.Normal.x,
                        Ny = fp.Normal.y,
                        Nz = fp.Normal.z,
                        Ux = fp.TextureU.x,
                        Uy = fp.TextureU.y,
                        Uz = fp.TextureU.z,
                        Vx = fp.TextureV.x,
                        Vy = fp.TextureV.y,
                        Vz = fp.TextureV.z,
                        N = fp.Vertices.Length,
                    };
                    for (int k = 0; k < w.N; k++) { w.X[k] = fp.Vertices[k].x; w.Y[k] = fp.Vertices[k].y; w.Z[k] = fp.Vertices[k].z; }
                    HeadlessBspModel.MovingBrushTracker.Transform<FpuPc64>(w, px, vx, pl.PrePivot, pl.Location, orientation);
                    int pBase = points.Count; points.Add(new Vector3(w.Bx, w.By, w.Bz)); local.Add(fp.Base);
                    int vN = vectors.Count; vectors.Add(new Vector3(w.Nx, w.Ny, w.Nz));
                    int vU = vectors.Count; vectors.Add(new Vector3(w.Ux, w.Uy, w.Uz));
                    int vV = vectors.Count; vectors.Add(new Vector3(w.Vx, w.Vy, w.Vz));
                    surfs.Add(new StaticSurface(fp.Texture?.Path ?? "None", SurfacePan.MoverSurfaceFlags(fp.PolyFlags, specialLit), pBase, vN, vU, vV,
                        hasLm ? fp.iBrushPoly + lmBase : -1, fp.iBrushPoly, fp.PanU, fp.PanV, 0));
                    int pool = vertPool.Count;
                    for (int k = 0; k < w.N; k++)
                    {
                        int t = orientation < 0f ? w.N - 1 - k : k;   // Transform reversed them: back to the FPoly's order
                        vertPool.Add(points.Count); points.Add(new Vector3(w.X[t], w.Y[t], w.Z[t])); local.Add(fp.Vertices[k]);
                    }
                    Vector3 n = vectors[vN];
                    Vector3 p0 = points[vertPool[pool]];
                    nodes.Add(new StaticNode(surfs.Count - 1, pool, fp.Vertices.Length, 0, n, n.x * p0.x + n.y * p0.y + n.z * p0.z, zone, zone));
                    moverOfNode.Add(r.Movers.Count);
                }
                m.NodeCount = nodes.Count - m.FirstNode;
                r.Movers.Add(m);
            }

            r.Geometry = new StaticGeometry(surfs.ToArray(), nodes.ToArray(), points.ToArray(), vectors.ToArray(),
                vertPool.ToArray(), new int[vertPool.Count], lightMaps.ToArray(), null, lights.ToArray(),
                LatestWorldStatic?.Geometry?.ZoneActorExports);
            r.LightBits = lightBits.ToArray();
            r.LocalOfPoint = local.ToArray();
            r.MoverOfNode = moverOfNode.ToArray();
            return r;
        }
    }
}
