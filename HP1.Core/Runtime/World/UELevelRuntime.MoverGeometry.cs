// MOVER GEOMETRY FOR DRAWING AND LIGHTMAPS (FPoly branch), computed in Core at load time.
//
// Rules:
//   * a mover's drawn polygon is the brush FPoly (brush->Polys); its lightmap is addressed by iBrushPoly, not
//     surf.iLightMap (−1 on mover surfaces, measured on three maps);
//   * the texel basis comes from the FPoly itself (Base, TextureU, TextureV, Normal) in the BASE position (pose rule
//     from SurrealEngine Render/RenderLight.cpp:20 — reconciliation debt, kept for 0.3.0 by the author's decision
//     2026-11-15, D-89 (5)): Location = BasePos+KeyPos[BrushRaytraceKey],
//     Rotation = BaseRot+KeyRot[…], transformed as ABrush::BuildCoords + FPoly::Transform do (Engine.dll.c:49283,
//     0x10396770): points (P−PrePivot)·MainScale·Rotation·PostScale + Location, TextureU/V/Normal through the
//     inverse-transpose (÷MainScale, Rotation, ÷PostScale), Normal renormalised. Shipped's slot-3 caller hands
//     SetupForSurf exactly that tracker surface (Points[pBase], Vectors[vTextureU/V/vNormal] of Level->Model, disasm
//     0x10b25c84..0x10b25d42), so PostScale is in the lightmap basis; the drawn UVs (P−Base)·TextureU do not change;
//   * MainScale is only ±1 per axis (mirroring keeps the texel formula); |s| ≠ 1 or a sheer — loud failure;
//   * all movers form one synthetic model: LightMap records offset into the shared LightBits, light
//     lists (Model->Lights) offset into the shared pool. From there the level pipeline
//     (LevelSurfLightmapRuntime) works without new math.
// Draw position — every frame: the brush-local point via BrushToWorldCoords(actor).
// Node zone (lightmap ambient seed) — the mover's zone at build time (zoneOf). Switching the layer by the CURRENT
// zone (layer sets) is NOT ported here — named debt.
// A brush without UPolys (polygons are the brush's BSP nodes) — loud failure.
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
        public Vector3[] LocalOfVector;      // brush vector (FPoly Normal/TextureU/TextureV) before the pose — mover mode 3
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
            var localVec = new List<Vector3>();
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
                Vector3 mainScale = BrushScaleOf(actor, "MainScale");
                if (Math.Abs(Math.Abs(mainScale.x) - 1f) > 1e-6f || Math.Abs(Math.Abs(mainScale.y) - 1f) > 1e-6f || Math.Abs(Math.Abs(mainScale.z) - 1f) > 1e-6f)
                { r.Problems.Add($"{name}: MainScale=({mainScale.x},{mainScale.y},{mainScale.z}) — |s|≠1 not ported"); continue; }
                // The base-pose basis below has no sheer (the tracker and BrushCoords apply it): refused, not drawn with a
                // wrong basis. No shipped brush shears (census of all 41 maps, v030 area C report).
                if (BrushCoords.SnapSheer(BrushSheerOf(actor, "MainScale")) != 0f || BrushCoords.SnapSheer(BrushSheerOf(actor, "PostScale")) != 0f)
                { r.Problems.Add($"{name}: sheer — the base-pose texel basis with a sheer is not ported"); continue; }

                List<HeadlessBspModel.PolyRecord> polys = HeadlessBspModel.ReadPolys(_brushMapPath, polysRef);
                StaticGeometry bg = model.BuildStaticGeometry();
                byte[] bits = model.ParseTables.LightBits ?? Array.Empty<byte>();
                int[] brushLights = model.ParseTables.Lights ?? Array.Empty<int>();

                int key = UEInterpreter.ToInt(actor.GetProp("BrushRaytraceKey"));
                Vector3 basePos = UEInterpreter.AsVector3(actor.GetProp(PK.BasePos));
                UERotator baseRot = UEInterpreter.AsRotator(actor.GetProp(PK.BaseRot));
                Vector3 keyPos = KeyVector(actor, "KeyPos", key);
                UERotator keyRot = KeyRotator(actor, "KeyRot", key);
                Vector3 prePivot = UEInterpreter.AsVector3(actor.GetProp(PK.PrePivot));
                Vector3 postScale = BrushScaleOf(actor, "PostScale");
                var axes = UEValue.GetAxes(new UERotator(baseRot.Pitch + keyRot.Pitch, baseRot.Yaw + keyRot.Yaw, baseRot.Roll + keyRot.Roll));
                Vector3 o = new Vector3(basePos.x + keyPos.x, basePos.y + keyPos.y, basePos.z + keyPos.z);
                // MainScale is ±1 here, so ÷MainScale = ·MainScale and Rotate serves both chains.
                Vector3 Rotate(Vector3 v)
                {
                    float x = v.x * mainScale.x, y = v.y * mainScale.y, z = v.z * mainScale.z;
                    return new Vector3(x * axes.X.x + y * axes.Y.x + z * axes.Z.x,
                                       x * axes.X.y + y * axes.Y.y + z * axes.Z.y,
                                       x * axes.X.z + y * axes.Y.z + z * axes.Z.z);
                }
                Vector3 Point(Vector3 p)
                {
                    Vector3 q = Rotate(new Vector3(p.x - prePivot.x, p.y - prePivot.y, p.z - prePivot.z));
                    return new Vector3(q.x * postScale.x + o.x, q.y * postScale.y + o.y, q.z * postScale.z + o.z);
                }
                Vector3 TexVector(Vector3 v)
                {
                    Vector3 q = Rotate(v);
                    return new Vector3(q.x / postScale.x, q.y / postScale.y, q.z / postScale.z);
                }
                Vector3 Normal(Vector3 v)
                {
                    Vector3 q = TexVector(v);
                    float len = MathF.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z);
                    return new Vector3(q.x / len, q.y / len, q.z / len);
                }

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
                    int pBase = points.Count; points.Add(Point(fp.Base)); local.Add(fp.Base);
                    int vN = vectors.Count; vectors.Add(Normal(fp.Normal)); localVec.Add(fp.Normal);
                    int vU = vectors.Count; vectors.Add(TexVector(fp.TextureU)); localVec.Add(fp.TextureU);
                    int vV = vectors.Count; vectors.Add(TexVector(fp.TextureV)); localVec.Add(fp.TextureV);
                    surfs.Add(new StaticSurface(fp.Texture?.Path ?? "None", SurfacePan.MoverSurfaceFlags(fp.PolyFlags, specialLit), pBase, vN, vU, vV,
                        hasLm ? fp.iBrushPoly + lmBase : -1, fp.iBrushPoly, fp.PanU, fp.PanV, 0));
                    int pool = vertPool.Count;
                    foreach (Vector3 v in fp.Vertices) { vertPool.Add(points.Count); points.Add(Point(v)); local.Add(v); }
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
            r.LocalOfVector = localVec.ToArray();
            r.MoverOfNode = moverOfNode.ToArray();
            return r;
        }
    }
}
