// Static BSP world -> vertices and batches. Rule: a node with a surface and ≥3 vertices, without PF_Invisible; pieces
// from Core coplanar occlusion (CoplanarOcclusion); triangle fan; texture UV along the surface axes with Pan shift;
// lightmap UV is the texel in the tile (lightmap, node zone). Each batch keeps the index run of every node (Batch.Runs):
// a frame draws only the runs of the nodes Core's OccludeBsp drew in that view (WorldFrame.Drawn), Z-buffered, without
// culling of its own (D3DDrv CULLMODE NONE; OccludeBsp chose the side).
// Coordinates are UE world (the camera is built in UE, no axis conversion).
using HP1.UEScript;

namespace HPEngine
{
    public enum Blend { Opaque, Masked, Translucent, Modulated }

    public struct Vertex
    {
        public float X, Y, Z, U, V, U2, V2, R, G, B, U3, V3;   // RGB: actor vertex light; U3/V3: fog-atlas texel (0 = no fog)
        // Actor vertex fog: D3D specular byte / 255 (surfaces, movers, sprites: 0) and vertex alpha Opacity
        // (ActorOpacity.VertexAlpha; 1 for everything else). The shader reads A only for AlphaBlend batches (uvoff.w),
        // fog only for mesh batches with admitted PF_RenderFog (uvoff.z). 64 bytes.
        public float FogR, FogG, FogB, A;
    }

    public sealed class Batch
    {
        public string Material;
        public Blend Blend;
        public bool Unlit, NoSmooth;
        public bool Backdrop;   // PF_FakeBackdrop: window into the sky (WorldFrame.Drawn SkyWindows), depth only without a sky view
        // Clock panning (PF_AutoUPan/VPan/SmallWavy) at the polygon zone's speed; divisor is DrawScale·USize, as for the
        // base PanU in mesh UV (only matters at DrawScale ≠ 1: one campaign texture, HP_Sneak.jellybeans01).
        public int PanFlags; public float PanSpeedU, PanSpeedV, TexW, TexH;
        public int MirrorPlane = -1;
        public int WarpPortal = -1;   // invisible portal into a warp zone: index in WorldMesh.WarpPortals; colour from the view through it
        public int PolyFlags;     // surface PolyFlags | texture flags of the batch's first surface (key parts are equal)
        public bool AlphaTest;    // mask alpha test (RenderRules.AlphaTest: Masked and Masked+Modulated)
        public int Group;         // frame group (RenderRules.WorldGroup by surface | texture PolyFlags)
        // A Modulated batch with fog runs: its fog map is a separate ONE/INVSRCALPHA pass over the surface pass (D3DDrv
        // DrawComplexSurface, D3DDrv.dll.c:6258-6265); other batches fold it in the shader (equal for a surface pass that
        // replaces the frame; Translucent takes none).
        public bool FogPass;
        public bool SpecialLit;   // surface PF_SpecialLit, paired with bSpecialLit of a spawned light   // PF_Mirrored: plane index; colour from the reflected pass
        public readonly List<uint> Indices = new();
        // Per model node its index run in Indices (First relative to the batch); a node's pieces are one run.
        public readonly List<(int Node, int Surf, int First, int Count)> Runs = new();
        public readonly SortedSet<int> Surfs = new();   // model surfaces in the batch (DrawLog)
        public UEVector At;   // first vertex of the batch (DrawLog: where to look)
        public uint First, Count;   // in the shared index buffer
    }

    public sealed class WorldMesh
    {
        public const int PF_Invisible = 0x1, PF_Masked = 0x2, PF_Translucent = 0x4, PF_Modulated = 0x40,
            PF_FakeBackdrop = 0x80, PF_TwoSided = 0x100, PF_NoSmooth = 0x800, PF_Unlit = 0x400000,
            PF_Portal = 0x04000000, PF_Mirrored = 0x08000000;

        public readonly List<Vertex> Vertices = new();
        public readonly List<Batch> Batches = new();
        public int SkippedSpecial, SkippedMirror, PortalsAsSurface, SkippedNoMetrics, Nodes, WarpVisiblePortals;
        public int NodeCount;   // model nodes (Batch.Runs node index range: n front side, NodeCount + n back side)
        public int[] BackSlot = System.Array.Empty<int>();   // per node: its back-side run slot, -1 when it has none
        public bool[] SidesDiffer = System.Array.Empty<bool>();   // per node: iZone[0] != iZone[1]
        public int UnportedBackZone;   // drawn-from-behind nodes without a back run (see Select), cumulative
        // Reflagged runs (DrawnWorld.Reflagged): slot 2·NodeCount + front/back slot, built with the flags Core's rule
        // UEOcclusion.MirrorWithoutShine gives a PF_Mirrored|PF_Translucent surface; ReflagFlags[n] = those flags, 0 = none.
        public int[] ReflagFlags = System.Array.Empty<int>();
        public int UnknownReflag;   // reflagged nodes without a run for Core's flags (see Select), cumulative
        public int SlotCount => NodeCount * 4;
        public readonly List<int> PointOfVertex = new(), NodeOfVertex = new();
        public readonly List<FogSurf> FogSurfaces = new();
        public readonly List<(UEVector Point, UEVector Normal)> MirrorPlanes = new();
        public readonly Dictionary<int, int> MirrorPlaneOfSurf = new();   // mirrored surface -> its key (Core mirror child frame)
        public readonly List<WarpPortal> WarpPortals = new();
        public int[] SurfGroup = System.Array.Empty<int>();   // frame group of a surface (a decal is drawn in its surface's group)
        readonly Dictionary<int, FogSurf> _fogByRecord = new();   // fog tile per Core draw record (LevelFogMaps.FogRecord)   // vertex source (for movers)

        public delegate bool TextureInfo(string material, out float width, out float height, out int texPolyFlags);
        public delegate bool Placement(int lightMap, int zone, out int tileX, out int tileY);

        public delegate bool ZonePan(int zone, out float u, out float v);

        public static WorldMesh Build(StaticGeometry g, TextureInfo tex, Placement place, int atlasW, int atlasH, IReadOnlyDictionary<int, List<UEVector[]>> coplanarPieces = null, ZonePan zonePan = null, Func<int, int, int, int> fogOf = null, string defaultTexture = null, HashSet<int> warpZones = null, string carrier = "bsp", bool backZones = false, IReadOnlyDictionary<int, List<UEVector[]>> coplanarBackPieces = null, IReadOnlyDictionary<int, List<UEVector[]>> coplanarNearBackPieces = null)
        {
            var m = new WorldMesh { SurfGroup = new int[g.Surfaces.Length], NodeCount = g.Nodes.Length, BackSlot = new int[g.Nodes.Length], ReflagFlags = new int[g.Nodes.Length] };
            System.Array.Fill(m.BackSlot, -1);
            m.SidesDiffer = new bool[g.Nodes.Length];
            for (int i = 0; i < g.Nodes.Length; i++) m.SidesDiffer[i] = g.Nodes[i].Zone0 != g.Nodes[i].Zone1;
            var byKey = new Dictionary<(string, Blend, bool, bool, bool, bool, int, float, float, int, bool, bool, int), Batch>();
            // Coplanar occlusion is a LEVEL model rule (Core UELevelRuntime.LevelCoplanarPieces); the mover model has none.
            var coplanarCut = coplanarPieces ?? new Dictionary<int, List<UEVector[]>>();
            var coplanarBackCut = coplanarBackPieces ?? new Dictionary<int, List<UEVector[]>>();
            var coplanarNearBackCut = coplanarNearBackPieces ?? new Dictionary<int, List<UEVector[]>>();
            for (int n = 0; n < g.Nodes.Length; n++)
            {
                StaticNode node = g.Nodes[n];
                if (node.Surf < 0 || node.Surf >= g.Surfaces.Length || node.NumVertices < 3) continue;
                StaticSurface s = g.Surfaces[node.Surf];
                if ((s.PolyFlags & PF_Invisible) != 0)
                {
                    // Invisible portal with a warp zone on one side: the view through it (HP1.Core UEWarpView.cs) is the portal batch.
                    if ((s.PolyFlags & PF_Portal) != 0 && warpZones != null && (warpZones.Contains(node.Zone0) || warpZones.Contains(node.Zone1)))
                        m.AddWarpPortal(g, n, node, s, MaterialOf(s, defaultTexture));
                    continue;
                }
                bool backdrop = (s.PolyFlags & PF_FakeBackdrop) != 0;
                // Mirror (PF_Mirrored): reflected pass from the Core mirror child frame that lists this surface
                // (ViewFrame.Sources): one batch key per mirrored surface, no plane merging of our own (ViewTree).
                // A visible portal (PF_Portal without PF_Invisible) is drawn as a regular surface; an invisible portal at a warp
                // zone is the view through it (above, HP1.Core UEWarpView.cs). A visible portal at a warp zone (in the original,
                // a child plus the surface over it, Render.dll.c:6110-6131, 6307) is not ported: the campaign data has none
                // (render census); counted loudly.
                int mirror = -1;
                if (!backdrop && (s.PolyFlags & PF_Mirrored) != 0) { mirror = m.MirrorPlaneIndex(node.Surf, g.Points[s.PBase], g.Vectors[s.VNormal]); m.SkippedMirror++; }
                if (!backdrop && (s.PolyFlags & PF_Portal) != 0) m.PortalsAsSurface++;
                if ((s.PolyFlags & PF_Portal) != 0 && warpZones != null && (warpZones.Contains(node.Zone0) || warpZones.Contains(node.Zone1))) m.WarpVisiblePortals++;
                // Coplanar cut per side (Core LevelCoplanarPieces / LevelCoplanarBackPieces): absent = the whole polygon.
                // A one-sided node is drawn from behind only with the eye within 1 unit of its plane (Render.dll.c:5879-5880):
                // its back run uses the near-plane cut (Core LevelCoplanarNearBackPieces).
                bool backDrawable = backZones && LevelSurfLightmap.BackDrawable(s.PolyFlags);
                var backCut = backDrawable ? coplanarBackCut : coplanarNearBackCut;
                bool nearBack = backZones && !backDrawable && (node.Zone0 != node.Zone1 || backCut.ContainsKey(n));
                UEVector[][] frontPieces = coplanarCut.TryGetValue(n, out List<UEVector[]> visible) ? visible.ToArray() : new[] { CoplanarOcclusion.Polygon(g, n) };
                UEVector[][] backPieces = !backDrawable && !nearBack ? System.Array.Empty<UEVector[]>()
                    : backCut.TryGetValue(n, out List<UEVector[]> backVisible) ? backVisible.ToArray() : new[] { CoplanarOcclusion.Polygon(g, n) };
                if (frontPieces.Length == 0 && backPieces.Length == 0) continue;
                string mat = MaterialOf(s, defaultTexture);
                if (!tex(mat, out float uSize, out float vSize, out int tpf) || !(uSize > 0) || !(vSize > 0))
                { m.SkippedNoMetrics++; continue; }   // size is not invented: the polygon is not drawn, counted loudly
                int eff = s.PolyFlags | tpf;
                RenderUnhandled.Surface(carrier, eff, $"surf {node.Surf} {mat}");
                bool unlit = (eff & PF_Unlit) != 0;
                // A translucent mirror also gets runs with the flags its draw record takes without ShinySurfaces
                // (UEOcclusion.MirrorWithoutShine: no reflection, opaque pass 1); Core's DrawnWorld.Reflagged selects them.
                bool reflag = !backdrop && UEOcclusion.MirrorFallsBack(eff);
                if (reflag) m.ReflagFlags[n] = UEOcclusion.MirrorWithoutShine(eff);
                m.SurfGroup[node.Surf] = RenderRules.SurfaceGroup(eff, !backdrop && (s.PolyFlags & PF_Mirrored) != 0);
                // OccludeBsp lights a node with the zone of the side the eye is on (iZone[side], Render.dll.c:5872-5874): a node
                // it may draw from behind (LevelSurfLightmap.BackDrawable) whose sides are different zones gets a second run in
                // slot NodeCount + n with iZone[0]'s tile, pan speed and fog; Core's DrawnWorld.BackNodes selects it.
                bool backVariant = (backDrawable || nearBack) && backPieces.Length > 0
                    && (node.Zone0 != node.Zone1 || coplanarCut.ContainsKey(n) || backCut.ContainsKey(n));
                if (backVariant) m.BackSlot[n] = m.NodeCount + n;
                for (int alt = 0; alt < (reflag ? 2 : 1); alt++)
                for (int variant = 0; variant < (backVariant ? 2 : 1); variant++)
                {
                    int zone = variant == 0 ? node.Zone1 : node.Zone0, slot = (variant == 0 ? n : m.NodeCount + n) + alt * 2 * m.NodeCount;
                    int pf = alt == 0 ? eff : m.ReflagFlags[n];
                    int mirrorOf = alt == 0 ? mirror : -1;
                    int group = alt == 0 ? RenderRules.SurfaceGroup(eff, mirror >= 0) : RenderRules.OpaqueWorld;
                    Blend blend = (pf & PF_Translucent) != 0 ? Blend.Translucent
                        : (pf & PF_Modulated) != 0 ? Blend.Modulated
                        : (pf & PF_Masked) != 0 ? Blend.Masked : Blend.Opaque;
                    UEVector[][] pieces = variant == 0 ? frontPieces : backPieces;
                    if (pieces.Length == 0) continue;
                    bool whole = !(variant == 0 ? coplanarCut : backCut).ContainsKey(n);
                    bool haveLm = !unlit && s.LightMap >= 0 && s.LightMap < g.LightMaps.Length;
                    StaticLightMap lm = haveLm ? g.LightMaps[s.LightMap] : default;
                    int tx = 0, ty = 0;
                    if (haveLm && (lm.UClamp <= 0 || lm.VClamp <= 0 || !place(s.LightMap, zone, out tx, out ty))) haveLm = false;
                    int panFlags = s.PolyFlags & (SurfacePan.PF_AutoUPan | SurfacePan.PF_AutoVPan | SurfacePan.PF_SmallWavy);
                    float psu = 0, psv = 0;
                    if ((panFlags & (SurfacePan.PF_AutoUPan | SurfacePan.PF_AutoVPan)) != 0 && (zonePan is null || !zonePan(zone, out psu, out psv)))
                        throw new InvalidOperationException($"surface {node.Surf} pans with the clock, but the speed of zone {zone} is not resolved");
                    // Two-sidedness is no culling here (Core chose the side); it stays in the key so a batch carries one PolyFlags
                    // word (the flags the device gets, compared by the draw log).
                    var key = (mat, blend, !haveLm, (pf & PF_NoSmooth) != 0, (s.PolyFlags & (PF_TwoSided | PF_Portal)) != 0, backdrop, panFlags, psu, psv, mirrorOf, (s.PolyFlags & 0x100000) != 0, RenderRules.AlphaTest(pf), alt);
                    if (!byKey.TryGetValue(key, out Batch b))
                    {
                        b = new Batch { Material = mat, Blend = blend, Unlit = !haveLm, NoSmooth = key.Item4, Backdrop = backdrop,
                            PanFlags = panFlags, PanSpeedU = psu, PanSpeedV = psv, TexW = uSize, TexH = vSize, MirrorPlane = mirrorOf, SpecialLit = (s.PolyFlags & 0x100000) != 0,
                            Group = group, AlphaTest = key.Item12, PolyFlags = pf };
                        byKey[key] = b; m.Batches.Add(b);
                    }
                    if (b.Surfs.Count == 0 && pieces.Length > 0 && pieces[0].Length > 0) b.At = pieces[0][0];
                    b.Surfs.Add(node.Surf);
                    UEVector uVec = g.Vectors[s.VTextureU], vVec = g.Vectors[s.VTextureV], baseP = g.Points[s.PBase];
                    // D3DDrv DrawComplexSurface (D3DDrv.dll.c:5862-5867): tu = (dot(P − Base, XAxis) − Info.Pan.X)·UMult, MapCoords =
                    // (Points[pBase], Vectors[vTextureU], Vectors[vTextureV]) and Info.Pan.X = −(PanU + auto pan) (Render.dll.c:
                    // 1232, 1254-1262). Lightmap stage (D3DDrv.dll.c:6103-6111): tu = (U − LmPan.X + 0.5·UScale)·UMult (0x1001a68c = 0.5).
                    float uMult = 1f / uSize, vMult = 1f / vSize;   // size already multiplied by DrawScale
                    float uPan = Dot(uVec, baseP) - s.PanU, vPan = Dot(vVec, baseP) - s.PanV;
                    float lmUPan = 0, lmVPan = 0;
                    if (haveLm)
                    {
                        lmUPan = Dot(uVec, baseP) + lm.Pan.x - 0.5f * lm.UScale;
                        lmVPan = Dot(vVec, baseP) + lm.Pan.y - 0.5f * lm.VScale;
                    }
                    // Fog map: Core decides whether this run's draw record gets one and which (LevelFogMaps.FogRecord from the
                    // node, the drawn side's zone and the run's record flags); here only the atlas slot.
                    int fogRec = haveLm && fogOf != null ? fogOf(n, zone, pf) : -1;
                    bool fog = fogRec >= 0;
                    // The shader folds the fog map into the fragment (classic.hlsl mode 0), equal to D3DDrv's own ONE/INVSRCALPHA pass
                    // over the frame (D3DDrv.dll.c:6258-6265) when the surface replaces the frame; a Modulated one blends with the
                    // frame first, so its batch draws the fog pass separately (Batch.FogPass).
                    if (fog && blend == Blend.Modulated) b.FogPass = true;
                    int runFirst = b.Indices.Count;
                    foreach (UEVector[] piece in pieces)
                    {
                        uint first = (uint)m.Vertices.Count;
                        if (fog)
                        {
                            if (!m._fogByRecord.TryGetValue(fogRec, out FogSurf fsf))
                            {
                                fsf = new FogSurf { Record = fogRec, LightMap = s.LightMap, UClamp = lm.UClamp, VClamp = lm.VClamp };
                                m._fogByRecord[fogRec] = fsf; m.FogSurfaces.Add(fsf);
                            }
                            fsf.Pieces.Add(((int)first, piece.Length));
                        }
                        for (int pk = 0; pk < piece.Length; pk++)
                        {
                            UEVector p = piece[pk];
                            m.PointOfVertex.Add(whole ? g.VertPoolPoints[node.VertPool + pk] : -1);
                            m.NodeOfVertex.Add(n);
                            float u = Dot(uVec, p), v = Dot(vVec, p);
                            var vx = new Vertex { X = p.x, Y = p.y, Z = p.z, U = (u - uPan) * uMult, V = (v - vPan) * vMult, A = 1f };
                            if (haveLm)
                            {
                                float tu = (u - lmUPan) / lm.UScale, tv = (v - lmVPan) / lm.VScale;
                                vx.U2 = (tx + tu) / atlasW;
                                vx.V2 = (ty + tv) / atlasH;
                                if (fog) { vx.U3 = tu; vx.V3 = tv; }   // tile texel; FogScene places it in the fog atlas
                            }
                            m.Vertices.Add(vx);
                        }
                        for (int k = 1; k + 1 < piece.Length; k++) { b.Indices.Add(first); b.Indices.Add(first + (uint)k); b.Indices.Add(first + (uint)k + 1); }
                    }
                    b.Runs.Add((slot, node.Surf, runFirst, b.Indices.Count - runFirst));
                }
                m.Nodes++;
            }
            return m;
        }

        static float Dot(UEVector a, UEVector b) => a.x * b.x + a.y * b.y + a.z * b.z;

        // THE NODES ONE VIEW DRAWS (WorldFrame.Drawn of its Core frame): the runs of its level nodes and sky windows, per
        // batch, appended to dst from the batch indices (idx at Batch.First); range[at + b] = (first, count) in dst.
        // mask: scratch of SlotCount, left clear. surfs (DrawLog, optional): the drawn surfaces per batch. Returns the
        // drawn nodes the mesh has no slot for (a model mismatch, refused loudly by the caller).
        public static int Select(IReadOnlyList<Batch> batches, uint[] idx, DrawnWorld drawn, bool[] mask, List<uint> dst,
            (uint First, uint Count)[] range, int at, List<int>[] surfs = null, WorldMesh mesh = null)
        {
            int unknown = 0;
            int bound = mesh?.NodeCount ?? mask.Length;
            foreach (int n in drawn.Nodes) if ((uint)n < (uint)bound) mask[n] = true; else unknown++;
            // Drawn from behind: the back-side run instead of the front one. A node without one whose sides are different
            // zones (its back side fully covered in the static coplanar cut while Core's span buffer drew it) keeps its
            // front run and is counted (UnportedBackZone).
            if (mesh is not null)
                foreach (int n in drawn.BackNodes)
                {
                    if ((uint)n >= (uint)mesh.NodeCount) continue;
                    if (mesh.BackSlot[n] >= 0) { mask[n] = false; mask[mesh.BackSlot[n]] = true; }
                    else if (mesh.SidesDiffer[n]) mesh.UnportedBackZone++;
                }
            // Reflagged (Core's draw-record flags): the node's run built with exactly those flags, else counted (UnknownReflag).
            if (mesh is not null)
                foreach (var (n, _, pf) in drawn.Reflagged)
                {
                    if ((uint)n >= (uint)mesh.NodeCount) continue;
                    if (mesh.ReflagFlags[n] != pf || pf == 0) { mesh.UnknownReflag++; continue; }
                    int side = mask[n] ? n : mesh.BackSlot[n];
                    if (side < 0 || !mask[side]) continue;
                    mask[side] = false; mask[2 * mesh.NodeCount + side] = true;
                }
            foreach (int n in drawn.SkyWindows) if ((uint)n < (uint)mask.Length) mask[n] = true; else unknown++;
            for (int b = 0; b < batches.Count; b++)
            {
                Batch batch = batches[b];
                int start = dst.Count;
                List<int> ls = null;
                if (surfs != null) { ls = surfs[at + b] ??= new List<int>(); ls.Clear(); }
                foreach (var run in batch.Runs)
                {
                    if ((uint)run.Node >= (uint)mask.Length || !mask[run.Node]) continue;   // a back/reflag slot needs SlotCount
                    for (int k = 0; k < run.Count; k++) dst.Add(idx[(int)batch.First + run.First + k]);
                    if (ls != null && (ls.Count == 0 || ls[^1] != run.Surf)) ls.Add(run.Surf);
                }
                range[at + b] = ((uint)start, (uint)(dst.Count - start));
            }
            foreach (int n in drawn.Nodes) if ((uint)n < (uint)bound) mask[n] = false;
            foreach (int n in drawn.SkyWindows) if ((uint)n < (uint)mask.Length) mask[n] = false;
            if (mesh is not null) foreach (int n in drawn.BackNodes) if ((uint)n < (uint)mesh.NodeCount && mesh.BackSlot[n] >= 0) mask[mesh.BackSlot[n]] = false;
            if (mesh is not null)
                foreach (var (n, _, _) in drawn.Reflagged)
                    if ((uint)n < (uint)mesh.NodeCount) { mask[2 * mesh.NodeCount + n] = false; mask[3 * mesh.NodeCount + n] = false; }
            return unknown;
        }

        // Portal per surface (CreateChildFrame merges children with the same iSurf/zone/NearClip, FUN_10b20790): one batch
        // per surface, opaque (a portal node is processed from both sides, Render.dll.c:5878),
        // in pass 0 (after the child the original moves the surface to pass 0, like a mirror: 0x10b2443c).
        void AddWarpPortal(StaticGeometry g, int n, StaticNode node, StaticSurface s, string mat)
        {
            int pi = WarpPortals.FindIndex(p => p.Surf == node.Surf);
            if (pi < 0)
            {
                pi = WarpPortals.Count;
                WarpPortals.Add(new WarpPortal { Surf = node.Surf, Zone0 = node.Zone0, Zone1 = node.Zone1, PolyFlags = s.PolyFlags, Normal = node.PlaneNormal, Dist = node.PlaneDist });
                Batches.Add(new Batch { Material = mat, Blend = Blend.Opaque, Unlit = true, WarpPortal = pi, PolyFlags = s.PolyFlags, Group = RenderRules.OpaqueWorld });
            }
            Batch b = Batches.Find(x => x.WarpPortal == pi);
            UEVector[] poly = CoplanarOcclusion.Polygon(g, n);
            uint first = (uint)Vertices.Count;
            int runFirst = b.Indices.Count;
            foreach (UEVector p in poly)
            {
                PointOfVertex.Add(-1); NodeOfVertex.Add(n);
                Vertices.Add(new Vertex { X = p.x, Y = p.y, Z = p.z, A = 1f });
            }
            for (int k = 1; k + 1 < poly.Length; k++) { b.Indices.Add(first); b.Indices.Add(first + (uint)k); b.Indices.Add(first + (uint)k + 1); }
            b.Runs.Add((n, node.Surf, runFirst, b.Indices.Count - runFirst));
        }

        // A surface without a texture uses the level's default texture: URender::DrawFrame takes
        // Viewport->Actor->Level->DefaultTexture (+0x454) when Surf.Texture == NULL (Render.dll.c:1137-1139, 1328).
        static string MaterialOf(StaticSurface s, string defaultTexture)
            => s.MaterialPath == "None" && defaultTexture != null ? defaultTexture : s.MaterialPath;

        int MirrorPlaneIndex(int surf, UEVector point, UEVector normal)
        {
            if (MirrorPlaneOfSurf.TryGetValue(surf, out int i)) return i;
            MirrorPlanes.Add((point, normal));
            return MirrorPlaneOfSurf[surf] = MirrorPlanes.Count - 1;
        }
    }

    // Lightmap atlas: layout by (lightmap, zone) with a 1 px border, and Core tile bytes with edge repeat in the border.
    public sealed class LightmapAtlas
    {
        public const int Width = 1024;
        public int Height;
        public byte[] Rgba;
        readonly Dictionary<long, (int x, int y)> _tile = new();
        static long Key(int lm, int zone) => ((long)lm << 32) | (uint)zone;

        // nearBack: also the slots of the (lightmap, iZone[0]) tiles Core adds when a one-sided zone-boundary node is drawn
        // from behind (LevelSurfLightmap.ZonesOfLightMaps nearBack; the level model).
        public LightmapAtlas(StaticGeometry g, bool nearBack = false)
        {
            int[][] zones = LevelSurfLightmap.ZonesOfLightMaps(g, nearBack);
            var cells = new List<(int lm, int zone, int zi, int w, int h)>();
            for (int i = 0; i < g.LightMaps.Length; i++)
            {
                int w = g.LightMaps[i].UClamp, h = g.LightMaps[i].VClamp;
                if (w <= 0 || h <= 0) continue;
                if (zones[i].Length == 0) cells.Add((i, -1, 0, w, h));
                else for (int z = 0; z < zones[i].Length; z++) cells.Add((i, zones[i][z], z, w, h));
            }
            cells.Sort((a, b) => { int c = b.h.CompareTo(a.h); if (c != 0) return c; c = a.lm.CompareTo(b.lm); return c != 0 ? c : a.zi.CompareTo(b.zi); });
            int x = 0, y = 0, shelf = 0;
            foreach (var c in cells)
            {
                int cw = c.w + 2, ch = c.h + 2;
                if (x + cw > Width) { x = 0; y += shelf; shelf = 0; }
                _tile[Key(c.lm, c.zone)] = (x + 1, y + 1);
                x += cw; shelf = Math.Max(shelf, ch);
            }
            int used = y + shelf, ph = 1;
            while (ph < used) ph <<= 1;
            Height = ph;
            Rgba = new byte[Width * Height * 4];
            for (int i = 3; i < Rgba.Length; i += 4) Rgba[i] = 255;
        }

        public bool Place(int lm, int zone, out int tx, out int ty)
        {
            tx = ty = 0;
            if (!_tile.TryGetValue(Key(lm, zone), out var t)) return false;
            tx = t.x; ty = t.y; return true;
        }

        // Core tile (row j from the top) + edge-repeat border. Returns the region to upload.
        public bool Write(LevelSurfLightmapRuntime.Tile t, out int x0, out int y0, out int w, out int h)
        {
            x0 = y0 = w = h = 0;
            if (!Place(t.LightMap, t.Zone, out int tx, out int ty)) return false;
            w = t.Width + 2; h = t.Height + 2; x0 = tx - 1; y0 = ty - 1;
            for (int r = 0; r < h; r++)
            {
                int j = Math.Clamp(r - 1, 0, t.Height - 1);
                for (int c = 0; c < w; c++)
                {
                    int i = Math.Clamp(c - 1, 0, t.Width - 1);
                    int src = (j * t.Width + i) * 3, dst = ((y0 + r) * Width + x0 + c) * 4;
                    Rgba[dst] = t.Rgb[src]; Rgba[dst + 1] = t.Rgb[src + 1]; Rgba[dst + 2] = t.Rgb[src + 2]; Rgba[dst + 3] = 255;
                }
            }
            return true;
        }
    }
}
