// Core frame actors -> engine triangles.
//
// Visibility (URender::SetupDynamics, Render.dll.c:10840) is Core's: an actor is counted as visible only if a Core frame
// drew it this tick (WorldFrame.ActorViews); the frontend has no gate of its own.
// A mesh is drawn only if the Core renderer ran GetFrame for it this frame: vertices IN WORLD from WorldFrame.MeshPoses
// (UELevelRuntime.DrawMeshActor; SetupDynamics culling and the GetRenderBoundingBox box are Core's too), wedges/triangles/
// materials from the Core raw mesh (UEMeshSource.Skeletal). The frontend computes no vertices and writes nothing to Core.
// Slot texture: material -> TextureIndex -> Textures[]; MultiSkins[TextureIndex] wins; a slot ≠ 0 with a mesh texture
// takes it; otherwise Skin; otherwise the mesh texture; an empty slot uses Actor.Texture.
// Blend mode via ActorFx/ActorOpacity.Resolve: Opacity < 1 -> alpha blending (SRCALPHA, 1-SRCALPHA, no Z, palette with
// alpha) before everything else; Style 2 -> masked, 3 -> translucent (ONE, 1-SRC_COLOR), 4 -> modulated
// (DST_COLOR, SRC_COLOR); slot flags PF_Masked/PF_Translucent too.
// Vertex fog: Core ActorFogFrame (UELevelRuntime.ActorLights), specular byte into the vertex; admission by slot mode is decided by the renderer
// (SdlRenderer.FogAllowed). Fatness: Core ActorFatness.
//
// Light: Core UEActorLighting: per-actor culling from Location, zone ambient FGetHSV(ZoneAmbientHSB(Region.Zone)),
// AmbientGlow (255 = pulse 0.25+0.2·sin(8·t) on the viewport clock, UEActorLighting.Inputs), specular by SpecularGlow/Width. Vertex normal is
// the mean of face normals per mesh POINT, the DrawLodMesh rule (verified, D-41).
//
// Vertex meshes: ULodMesh::GetFrame in Core. Sprites, particles, shadows: hpengine/Sprites;
// third-person weapon: AddWeapon.
using HP1.UEScript;

namespace HPEngine
{
    // The first four match SpriteBlend numerically (HPEngineProgram.SpriteToDraw casts through int).
    public enum ActorBlend { Opaque, Masked, Translucent, Modulated, AlphaBlend }

    public sealed class ActorDraw
    {
        public string Texture;
        public ActorBlend Blend;
        public bool Unlit;
        public bool TwoSided = true;   // mesh slot with PF_TwoSided (or not a mesh): no culling
        public bool OpacityMasked;     // AlphaBlend: masked palette (ActorFxBlend.MaskedPalette; texture bMasked is the renderer's)
        // For mover surfaces: mode by the BSP rule (WorldMesh.Blend), lightmap from the mover atlas.
        public int WorldMode = -1;
        public bool Lightmapped, MoverAtlas;
        public int MirrorPlane = -1;   // mover mirror (PF_Mirrored): plane index in MoverScene.MirrorPlanes
        public int ActorId;   // the actor whose DrawActorSprite this is (WorldFrame.DrawOrder); movers: unused (world group)
        public int FrameView = HPEngine.Sprites.SpriteDraw.AnyView;   // the frame that draws the batch (WorldFrame.Views index, −1 = master); AnyView = every pass (movers)
        public string LogKind, LogOwner;   // DrawLog: batch kind (null = actor mesh) and owners   // a decal piece only in its own frame (DecalPiece.View)
        public int PolyFlags;          // mover surface flags (PolyFlags | texture flags), for RenderRules.MirrorSurfaceDrawn
        public float UvOffU, UvOffV;   // clock UV shift (mover surfaces)
        public TexLod Lod = TexLod.Client;   // Lock path for Texture Detail: mesh skin / particle / other (-1)
        public int Group = RenderRules.Actors;
        public bool AlphaTest;  // mask alpha test (RenderRules.AlphaTest): Masked, Masked+Modulated, mover batch by BSP flags
        public bool NoSmooth;   // actor PF_NoSmooth (bNoSmooth): point sampling (ActorSnapshot.NoSmooth)   // frame group (RenderRules: actor ActorGroup, mover/decal WorldGroup)
        public readonly List<Vertex> Vertices = new();
        public readonly List<uint> Indices = new();
    }

    public sealed class ActorStats
    {
        public int Lit, LightsAttached;
        public double PrepMs, LightMs, EmitMs;
        public int Culled, Weapons, InFogZone;
        public int Seen, Visible, Skeletal, VertexMesh, VertexDrawn, Sprite, Other, NoMesh, PoseFailed, Opacity, EmptySlot;
        // The tick's fog counters, copied from Core's ActorFogFrame at the end of ActorBuilder.Prepare; null: fog not wired.
        public (int Actors, int Vertices, int NotInZone, int NoBasis, int NoLights, int NoVolumetric)? Fog;
        public readonly SortedSet<string> Problems = new();
        public override string ToString() =>
            $"actors {Seen} (in a fog zone {InFogZone}), visible {Visible} (outside the frustum {Culled}): skeletal {Skeletal} (Opacity<1 {Opacity}), vertex {VertexDrawn}, third-person weapons {Weapons}; NOT drawn — vertex {VertexMesh}, "
            + $"sprites {Sprite}, other {Other}, without mesh {NoMesh}, pose refused {PoseFailed}, empty slots {EmptySlot}; "
            + $"light: lit {Lit}, sources on actors {LightsAttached}; "
            + (Fog is { } f ? $"fog: actors {f.Actors}, vertices {f.Vertices}, skips: not in zone {f.NotInZone}, no basis {f.NoBasis}, no sources {f.NoLights}, no volumetric {f.NoVolumetric}; " : "fog: not wired; ")
            + $"ms: pose+selection {PrepMs:F2}, light {LightMs:F2}, vertices {EmitMs:F2}";
    }

    public sealed class ActorBuilder
    {
        const int DT_Sprite = 1, DT_Mesh = 2, DT_Brush = 3, DT_SpriteAnimOnce = 7;   // DT_Brush: brushes (movers are MoverScene); 8: emitter (ParticleBuilder)   // game EDrawType (BspDiffProgram.cs DrawTypeNames; 4 = DT_RopeSprite)
        const int PF_TwoSided = 0x100;

        readonly UELevelRuntime _rt;
        readonly UEMeshSource _meshes;
        readonly Dictionary<string, (UERawSkeletalMesh raw, string[] slots)> _skel = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<(int, string, ActorBlend, bool, bool, bool, int, bool, bool, int), ActorDraw> _byKey = new();
        // Frames that drew the actor (WorldFrame.ActorViews). A posed mesh without membership is a broken Core contract: a
        // loud refusal (Sprites.SpriteMath.ViewsOf), not "every pass with the master camera" (§4).
        int[] ViewsOf(int id) => HPEngine.Sprites.SpriteMath.ViewsOf(_frame, id);
        // Light lists and vertex fog of the tick: Core's live objects (UELevelRuntime.ActorLights), held only inside Prepare
        // (Core idle); null outside it, so nothing run between ticks, while Core runs, can read them.
        UEActorLights _core;
        bool _viewLit;   // the tick's UEActorLights.ViewLit, taken in Prepare: Emit runs between ticks too, while Core runs
        ActorFogFrame Fog => _core?.Fog;
        public readonly List<ActorDraw> Draws = new();
        public ActorStats Stats = new();

        double _viewportSeconds;
        WorldFrame _frame;
        // Interpolation between logic ticks (FramePacing.cs): display fraction and poses per tick. The pose is computed in
        // Core once per tick (first render frame after the tick); other frames lerp the previous and the current one.
        public float Alpha = 1f;
        // AnimNext chain frame at draw time: UTexture::Get(Viewport->CurrentTime) = Update + AnimCur
        // (skins in DrawMesh/DrawLodMesh Render.dll.c:9252-9256, 14771-14776; Engine 0x103040f7; Update is UTexture
        // vtable 0x1046df90 +0x7c). In game: UETextureAnim.CurrentFrame, the same as BSP batches
        // (SdlRenderer.AnimateWorld). Unset: chain head.
        public Func<string, string> FrameOf = static p => p;
        readonly PoseHistory _poses = new();

        public ActorBuilder(UELevelRuntime rt)
        {
            _rt = rt;
            _meshes = rt.MeshSource;
        }


        // viewportSeconds: the viewport (wall) clock — the AmbientGlow 255 pulse reads Viewport->CurrentTime.
        public void Build(WorldFrame frame, double viewportSeconds)
        {
            Prepare(frame, viewportSeconds);
            EmitPrepared();
        }

        public void EmitPrepared()
        {
            long te = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (Job j in _jobs) Emit(j);
            Stats.EmitMs += Ms(te);
        }

        // The part of Build that reads Core (the tick's light lists, vertex fog, triangle order, mesh source): poses, culling,
        // light. Run while Core is idle (HPEngineProgram.TakeTick); the vertices follow with Build's emit or Reemit.
        public void Prepare(WorldFrame frame, double viewportSeconds)
        {
            _frame = frame;
            _viewportSeconds = viewportSeconds;
            _core = _rt.ActorLights;
            _viewLit = _core?.ViewLit ?? false;
            _jobs.Clear();
            long tprep = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_ordered.Count > 0) { Draws.RemoveAll(_ordered.Contains); _ordered.Clear(); }   // last tick's ordered runs
            // Batches are per actor (actors come and go): the ones last frame did not fill are dropped.
            _stale.Clear();
            foreach (var kv in _byKey) if (kv.Value.Indices.Count == 0) _stale.Add(kv.Key);
            foreach (var k in _stale) _byKey.Remove(k);
            if (_stale.Count > 0) Draws.RemoveAll(d => d.Indices.Count == 0);
            foreach (var d in Draws) { d.Vertices.Clear(); d.Indices.Clear(); d.LogOwner = null; }
            Stats = new ActorStats();
            _framePoses.Clear();
            foreach (MeshPose mp in frame.MeshPoses) if (!mp.Weapon) _framePoses[mp.ObjectId] = mp;
            for (int i = 0; i < frame.Actors.Count; i++)
            {
                ActorSnapshot a = frame.Actors[i];
                if (a.Destroyed) continue;
                Stats.Seen++;
                // Actor PF_RenderFog by the actor's zone (ActorFogFrame.IsFogZone), not by the actor's own bFogZone bit
                // (always false for non-ZoneInfo; see ActorFx/README).
                if (Fog != null && Fog.IsFogZone(a.ZoneNumber)) Stats.InFogZone++;
                if (a.DrawType == DT_Mesh)
                {
                    // A mesh without a pose in the frame was not drawn by the Core renderer this frame (culling is Core's).
                    if (!_framePoses.TryGetValue(a.ObjectId, out MeshPose mp)) { Stats.Culled++; continue; }
                    Stats.Visible++;
                    if (Skeletal(mp.MeshPath) is { } sk) { AddPosed(a, mp, sk.raw, sk.slots, ref Stats.Skeletal); continue; }
                    if (Vertex(mp.MeshPath) is { } vx) { AddPosed(a, mp, vx.raw, vx.slots, ref Stats.VertexDrawn); continue; }
                    Stats.VertexMesh++;
                    RenderUnhandled.Note("mesh", "vertex-mesh-not-drawn", mp.MeshPath);
                    continue;
                }
                if (!frame.ActorViews.SetOf.ContainsKey(a.ObjectId)) continue;
                Stats.Visible++;
                if (a.DrawType == DT_Sprite || a.DrawType == DT_SpriteAnimOnce) Stats.Sprite++;
                else if (a.DrawType != 0) { Stats.Other++; if (a.DrawType != DT_Brush && !a.Emitter.Valid) RenderUnhandled.Note("actor", "DrawType=" + a.DrawType, $"{a.Name} ({a.Class})"); }
            }
            foreach (MeshPose mp in frame.MeshPoses) if (mp.Weapon) AddWeapon(frame, mp);
            Stats.PrepMs += Ms(tprep);
            long tl = System.Diagnostics.Stopwatch.GetTimestamp();
            System.Threading.Tasks.Parallel.For(0, _jobs.Count, i => ComputeLight(_jobs[i]));
            Stats.LightMs += Ms(tl);
            if (Fog is { } fog) Stats.Fog = (fog.FoggedActors, fog.FoggedVertices, fog.SkipNotInZone, fog.SkipNoBasis, fog.SkipNoLights, fog.SkipNoVolumetric);
            _core = null;
        }

        // THIRD-PERSON WEAPON (URender::DrawActorSprite, Render.dll 0x10b32f85-0x10b330ba; body is Core
        // UELevelRuntime.DrawMeshActor, branch 3): ThirdPersonMesh vertices are already in the pawn's weapon bone frame
        // (MeshPose.Weapon). Materials and light come from the weapon actor's own snapshot.
        readonly Dictionary<int, MeshPose> _framePoses = new();
        void AddWeapon(WorldFrame frame, in MeshPose mp)
        {
            ActorSnapshot? ws = null;
            foreach (ActorSnapshot x in frame.Actors) if (x.ObjectId == mp.ObjectId) { ws = x; break; }
            if (ws is null) { Stats.Problems.Add($"weapon {mp.ObjectId} is not in the frame"); return; }
            if (Skeletal(mp.MeshPath) is not { } sk) { Stats.Problems.Add($"weapon {ws.Value.Name}: {mp.MeshPath} is not a skeletal mesh"); return; }
            AddPosed(ws.Value, mp, sk.raw, sk.slots, ref Stats.Weapons);
        }

        // LOD (Original visuals, D-62): Core's MeshPose.LodParts — per part the faces DrawLodMesh draws at that level
        // (UEMeshSource.LodVariant) in that part's frames, with the part's own GetFrame pose when Core gives one (drawn as
        // is, not interpolated: the pose history is per actor); no parts — the full mesh in the actor's frames.
        void AddPosed(in ActorSnapshot a, in MeshPose mp, UERawSkeletalMesh raw, string[] slots, ref int drawnCounter)
        {
            if (mp.LodParts is not { Count: > 0 } parts) { AddMesh(a, raw, slots, mp.Points, ref drawnCounter, -1); return; }
            int extra = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                UERawSkeletalMesh lod = parts[i].Verts < 0 ? raw : _meshes.LodVariant(mp.MeshPath, raw, parts[i].Verts);
                if (i == 0) AddMesh(a, lod, slots, mp.Points, ref drawnCounter, parts[i].ViewSet, own: parts[i].Points);
                else AddMesh(a, lod, slots, mp.Points, ref extra, parts[i].ViewSet, copyPoints: true, own: parts[i].Points);
            }
        }

        // Frame between logic ticks (FramePacing.cs): visibility, light and culling come from the tick (Build), here only
        // vertices from the interpolated pose. Light and normals from the tick (the difference over a tick fraction is invisible).
        public void Reemit(float alpha)
        {
            Alpha = alpha;
            foreach (var d in Draws) { d.Vertices.Clear(); d.Indices.Clear(); d.LogOwner = null; }
            foreach (Job j in _jobs) { j.Pts = j.Own ?? _poses.Get(j.A.ObjectId, alpha); Fatten(j); Emit(j); }
        }

        (UERawSkeletalMesh raw, string[] slots)? Skeletal(string path)
        {
            if (_skel.TryGetValue(path, out var hit)) return hit.raw is null ? null : hit;
            UERawSkeletalMesh raw = _meshes.Skeletal(path);
            var e = (raw, raw is null ? null : _meshes.SlotTextures(path));
            _skel[path] = e;
            return raw is null ? null : e;
        }

        // VERTEX MESH (ULodMesh): frame points from the Core body ULodMesh::GetFrame (UELevelRuntime.VertexMeshFrame),
        // triangles/wedges/materials are the same fields as skeletal, so the common path follows (light, fog, emit).
        // Special faces (SpecialFaces, the weapon point) are not drawn by the original. No LOD selection: all FrameVerts.
        readonly Dictionary<string, (UERawSkeletalMesh raw, string[] slots)> _vert = new(StringComparer.OrdinalIgnoreCase);
        (UERawSkeletalMesh raw, string[] slots)? Vertex(string path)
        {
            if (_vert.TryGetValue(path, out var hit)) return hit.raw is null ? null : hit;
            UERawVertexMesh v = _meshes.Vertex(path);
            UERawSkeletalMesh raw = null;
            if (v != null)
            {
                raw = new UERawSkeletalMesh { name = v.name, pkg = v.pkg, objectPath = v.objectPath, numPoints = v.frameVerts,
                    numWedges = v.numWedges, wedgeVertex = v.wedgeVertex, wedgeU = v.wedgeU, wedgeV = v.wedgeV,
                    numTris = v.numTris, triWedge = v.triWedge, triMaterial = v.triMaterial,
                    materialFlags = v.materialFlags, materialTexIdx = v.materialTexIdx, textures = v.textures };
                foreach (int pi in raw.wedgeVertex)
                    if (pi < 0 || pi >= raw.numPoints)
                    { Stats.Problems.Add($"vertex mesh {path}: wedge to point {pi} outside FrameVerts {raw.numPoints}"); raw = null; break; }
            }
            var e = (raw, raw is null ? null : _meshes.SlotTextures(path));
            _vert[path] = e;
            return raw is null ? null : e;
        }

        void AddMesh(in ActorSnapshot a, UERawSkeletalMesh raw, string[] slotTable, UEVector[] pts, ref int drawnCounter, int viewSet,
            bool copyPoints = false, UEVector[] own = null)
        {
            if (pts.Length != raw.numPoints)
            { Stats.PoseFailed++; Stats.Problems.Add($"pose {a.Name}: {pts.Length} points, the mesh has {raw.numPoints}"); return; }
            if (!_poses.Has(a.ObjectId, _frame.Tick)) _poses.Store(a.ObjectId, _frame.Tick, pts, a.Location);
            if (a.Opacity < 1f) Stats.Opacity++;
            drawnCounter++;
            UEVector[] posed = own ?? _poses.Get(a.ObjectId, Alpha);
            Job job = Prepare(a, raw, slotTable, copyPoints ? (UEVector[])posed.Clone() : posed);   // a second part: Fatness shifts its own copy
            job.Tick = own ?? pts;   // the tick pose: Core's triangle order is decided on it (UEActorLights.TriangleOrder)
            job.Own = own;
            // The frames this part is drawn in: the LOD part's set (MeshLodPart.ViewSet, WorldFrame.ActorViews.Sets index),
            // otherwise the actor's own frames; one output per frame (per-view eye, see Job.Out).
            if (viewSet >= 0) job.ViewIds = _frame.ActorViews.Sets[viewSet];
            _jobs.Add(job);
        }

        // Per-actor work: the sequential part (pose, light-source culling) is already done.
        sealed class Job
        {
            public ActorSnapshot A; public UERawSkeletalMesh Raw; public string[] Slots; public UEVector[] Pts, Tick;
            public UEVector[] Own;   // the LOD part's own Core pose (MeshLodPart.Points), drawn without interpolation
            public UEVector[] Norm = Array.Empty<UEVector>();
            // One output per frame that drew the actor (WorldFrame.ActorViews): DrawMesh runs with that Frame, so the
            // specular eye, the PF_Environment reflection and the vertex fog are the frame's (SpriteMath.ViewCamera).
            public int[] ViewIds = Array.Empty<int>();
            public readonly List<ViewOut> Out = new();
            // Drawn points: Pts shifted by Fatness along the tick normals (Draw = Pts when Fatness == 128). Pts is the Core
            // pose (or its interpolation) and is never written: the pose history keeps plain points.
            public UEVector[] Fat = Array.Empty<UEVector>(); public UEVector[] Draw;
        }
        sealed class ViewOut
        {
            public int View; public UEVector Eye;
            // Core's SetupForActor result for this frame's DrawMesh (UEActorLights, one call per frame): inputs and the light
            // list (unlit / leaf -1: empty; vertex fog of such an actor runs without sources, as in the original).
            public UEActorLighting.Exported[] Lights = new UEActorLighting.Exported[UEActorLighting.ExportCap];
            public int Count; public UEActorLighting.ActorInputs In;
            public int[] Order;   // Core's DrawLodMesh triangle order for this frame (null: the material layout)
            public readonly List<ActorDraw> Runs = new();   // the ordered path's draws of this tick, reused by Reemit
            public float[] Light = Array.Empty<float>();
            public float[] Fog = Array.Empty<float>(); public bool Fogged;   // 4 floats per point (ActorFogFrame.Apply)
        }
        readonly List<Job> _jobs = new();
        readonly List<Job> _pool = new();

        Job Prepare(in ActorSnapshot a, UERawSkeletalMesh raw, string[] slotTable, UEVector[] pts)
        {
            Job j = _jobs.Count < _pool.Count ? _pool[_jobs.Count] : null;
            if (j is null) { j = new Job(); _pool.Add(j); }
            j.A = a; j.Raw = raw; j.Slots = slotTable; j.Pts = pts; j.ViewIds = ViewsOf(a.ObjectId);
            if (_core is null) throw new InvalidOperationException($"actor {a.Name}: drawn mesh without Core's light lists (UELevelRuntime.ActorLights)");
            if (!a.Unlit) Stats.Lit++;
            return j;
        }

        // Normals and vertex light are independent per actor and computed in parallel.
        void ComputeLight(Job j)
        {
            int np = j.Pts.Length;
            ActorSnapshot a = j.A;
            // Normals and light: Core bodies (UEActorLighting.PointNormals/LightPoints).
            if (j.Norm.Length < np) j.Norm = new UEVector[np];
            UEVector[] N = j.Norm; UEVector[] pts = j.Pts;
            UEActorLighting.PointNormals(j.Raw, pts, np, N);
            // Actor.Fatness (hpengine/ActorFx): shift along the normal BEFORE light and clipping (Render.dll.c:9328/14851),
            // unlit included. Points are the frame pose (WorldFrame.MeshPoses).
            if (Fatten(j)) Interlocked.Increment(ref _fatActors);
            while (j.Out.Count < j.ViewIds.Length) j.Out.Add(new ViewOut());
            for (int k = 0; k < j.ViewIds.Length; k++)
            {
                ViewOut o = j.Out[k];
                o.View = j.ViewIds[k];
                int view = o.View == HPEngine.Sprites.SpriteDraw.AnyView ? -1 : o.View;
                HPEngine.Sprites.SpriteMath.ViewCamera(_frame, view, out o.Eye, out _, out _, out _);
                // Vertex fog over points WITHOUT the Fatness shift (fog is per mesh vertex, the shift comes later in the
                // shader; see ActorFx/README); sources are this actor's light-culling list; basis of this view's frame.
                if (o.Fog.Length < np * 4) o.Fog = new float[np * 4];
                if (!_core.TryGet(a.ObjectId, view, out UEActorLights.Set set))
                    throw new InvalidOperationException($"actor {a.Name}: frame {view} has no Core light list (UELevelRuntime.ActorLights)");
                o.In = set.In; o.Count = set.Count; Array.Copy(set.Lights, o.Lights, set.Count);
                if (!a.Unlit) Interlocked.Add(ref Stats.LightsAttached, o.Count);
                o.Fogged = Fog != null && Fog.Apply(a.ObjectId, pts.AsSpan(0, np), o.Fog, view);
                // Specular (light manager slot 6, FUN_10b02b10: the vertex in the Frame's view space, Render.dll.c:16923-16935)
                // from this view's eye.
                if (o.Light.Length < np * 3) o.Light = new float[np * 3];
                UEActorLighting.LightPoints(a, o.In, j.Draw, N, np, o.Lights, o.Count, o.Eye, o.Light, UEActorLighting.UnlitFlag(a, _core.ViewLit));
                o.Order = _core.TriangleOrder(a, view, j.Raw.triWedge, j.Raw.wedgeVertex, j.Raw.triMaterial, j.Raw.materialFlags, j.Tick);
                o.Runs.Clear();
            }
        }

        // j.Draw = j.Pts + N·Offset(Fatness) into the job's own buffer, the same for the tick frame (Build) and the frames
        // between ticks (Reemit, tick normals). true if shifted.
        static bool Fatten(Job j)
        {
            int np = j.Pts.Length;
            if (ActorFatness.Offset(j.A.Fatness) == 0f) { j.Draw = j.Pts; return false; }
            if (j.Fat.Length != np) j.Fat = new UEVector[np];
            Array.Copy(j.Pts, j.Fat, np);
            ActorFatness.Apply(j.Fat.AsSpan(0, np), j.Norm.AsSpan(0, np), j.A.Fatness);
            j.Draw = j.Fat;
            return true;
        }
        int _fatActors;
        public int FatActors => _fatActors;

        void Emit(Job j)
        {
            for (int k = 0; k < j.ViewIds.Length; k++) Emit(j, j.Out[k]);
        }

        void Emit(Job j, ViewOut o)
        {
            if (o.Order != null) { EmitOrdered(j, o); return; }
            UERawSkeletalMesh raw = j.Raw;
            float alpha = HPEngine.ActorFx.ActorOpacity.VertexAlpha(j.A.Opacity);
            foreach (MatPart part in Layout(raw))
            {
                ActorDraw d = MaterialDraw(j, o, part.MatIdx, part.TexIdx, part.Flags, shared: true);
                if (d is null) continue;
                uint baseV = (uint)d.Vertices.Count;
                foreach (int w in part.Wedges) d.Vertices.Add(MakeVertex(j, o, w, alpha));
                foreach (int li in part.Indices) d.Indices.Add(baseV + (uint)li);
            }
        }

        // The draw of one mesh material: texture (slot, PF_Environment fallback), blend state; shared — the scene batch of
        // the same key (DrawFor), otherwise a new draw appended in order. null: an empty slot.
        ActorDraw MaterialDraw(Job j, ViewOut o, int matIdx, int texIdx, int flags, bool shared)
        {
            ActorSnapshot a = j.A; string[] slotTable = j.Slots;
            string meshTex = slotTable != null && texIdx >= 0 && texIdx < slotTable.Length && !string.IsNullOrEmpty(slotTable[texIdx]) ? slotTable[texIdx] : null;
            // bMeshEnviroMap -> PF_Environment (FUN_10b338f0, Render.dll.c:39793) on all triangles: the texture is the
            // same 0x10b65980 as for an empty slot (Render.dll.c:14933-14935), UV and light via EnviroMapping.Of.
            bool env = a.MeshEnviroMap;
            string tex = env ? FallbackTexture(a, slotTable) : SlotTexture(a, meshTex, texIdx) ?? FallbackTexture(a, slotTable);
            if (tex is null) { Stats.EmptySlot++; return null; }
            tex = FrameOf(tex);
            var fx = HPEngine.ActorFx.ActorOpacity.Resolve(a.Style, flags, a.Opacity);
            if ((flags & ~RenderUnhandled.MeshMaterialHandled) != 0) RenderUnhandled.MeshMaterial(flags, $"{a.MeshPath} mat{matIdx}");
            bool opacityMasked = fx.Mode == HPEngine.ActorFx.ActorFxBlendMode.AlphaBlend && fx.MaskedPalette;
            ActorDraw d = shared
                ? DrawFor(a.ObjectId, tex, BlendOf(fx.Mode), a.Unlit, (flags & PF_TwoSided) != 0, opacityMasked, RenderRules.ActorGroup(a.LatePass), a.NoSmooth, fx.AlphaTest, o.View)
                : NewDraw(a.ObjectId, tex, BlendOf(fx.Mode), a.Unlit, (flags & PF_TwoSided) != 0, opacityMasked, RenderRules.ActorGroup(a.LatePass), a.NoSmooth, fx.AlphaTest, o.View);
            if (DrawLog.Path != null && (d.LogOwner is null || !d.LogOwner.Split(',').Contains(a.Name))) d.LogOwner = d.LogOwner is null ? a.Name : d.LogOwner + "," + a.Name;
            return d;
        }

        Vertex MakeVertex(Job j, ViewOut o, int w, float alpha)
        {
            ActorSnapshot a = j.A; UERawSkeletalMesh raw = j.Raw; UEVector[] pts = j.Draw; float[] light = o.Light;
            float[] fog = o.Fogged ? o.Fog : null;
            int pi = raw.wedgeVertex[w];
            UEVector p = pts[pi];
            var vx = new Vertex { X = p.x, Y = p.y, Z = p.z, U = raw.wedgeU[w], V = raw.wedgeV[w],
                R = light[pi * 3], G = light[pi * 3 + 1], B = light[pi * 3 + 2], A = alpha };
            if (a.MeshEnviroMap)
            {
                var (eu, ev, el) = EnviroMapping.Of(p, j.Norm[pi], o.Eye);
                vx.U = eu; vx.V = ev;
                // Light is written before the PF_Unlit branch (0x400000), which overrides it (FUN_10b0d3c0, 0x10b0d62e).
                if (!UEActorLighting.UnlitFlag(a, _viewLit)) vx.R = vx.G = vx.B = el;
            }
            if (fog != null)
            {
                // D3DDrv DrawTriangles: specular = ROUND(Fog.rgb·255); Fog.a does not reach the frame.
                vx.FogR = ActorFogFrame.SpecularByte(fog[pi * 4]) / 255f;
                vx.FogG = ActorFogFrame.SpecularByte(fog[pi * 4 + 1]) / 255f;
                vx.FogB = ActorFogFrame.SpecularByte(fog[pi * 4 + 2]) / 255f;
            }
            return vx;
        }

        // Core's triangle order (DrawLodMesh sort): the accepted faces in that order, cut into runs of consecutive faces of
        // one material, each run its own draw in sequence (FUN_10b0d3c0 flushes on a texture/flags change; batches never
        // span actors) — not merged into the shared per-texture batches.
        void EmitOrdered(Job j, ViewOut o)
        {
            ActorSnapshot a = j.A; UERawSkeletalMesh raw = j.Raw;
            float alpha = HPEngine.ActorFx.ActorOpacity.VertexAlpha(a.Opacity);
            ActorDraw d = null; int runMat = int.MinValue, run = 0;
            foreach (int t in o.Order)
            {
                int mi = raw.triMaterial != null && t < raw.triMaterial.Length ? raw.triMaterial[t] : 0;
                if (mi != runMat || d is null)
                {
                    runMat = mi;
                    int texIdx = raw.materialTexIdx != null && mi >= 0 && mi < raw.materialTexIdx.Length ? raw.materialTexIdx[mi] : mi;
                    int flags = raw.materialFlags != null && mi >= 0 && mi < raw.materialFlags.Length ? raw.materialFlags[mi] : 0;
                    // Build makes the run's draw; Reemit (frames between ticks, the same order) refills it.
                    if (run < o.Runs.Count) d = o.Runs[run];
                    else { d = MaterialDraw(j, o, mi, texIdx, flags, shared: false); o.Runs.Add(d); }
                    run++;
                    if (d is null) continue;
                }
                uint baseV = (uint)d.Vertices.Count;
                for (int k = 0; k < 3; k++) d.Vertices.Add(MakeVertex(j, o, raw.triWedge[t * 3 + k], alpha));
                d.Indices.Add(baseV); d.Indices.Add(baseV + 1); d.Indices.Add(baseV + 2);
            }
        }

        // Mesh layout by material (in material-number order):
        // unique material wedges and local triangle indices, once per mesh.
        sealed class MatPart { public int MatIdx, TexIdx, Flags; public int[] Wedges, Indices; }
        readonly Dictionary<UERawSkeletalMesh, List<MatPart>> _layout = new();

        List<MatPart> Layout(UERawSkeletalMesh raw)
        {
            if (_layout.TryGetValue(raw, out var parts)) return parts;
            var byMat = new SortedDictionary<int, List<int>>();
            for (int t = 0; t < raw.numTris; t++)
            {
                int mi = raw.triMaterial != null && t < raw.triMaterial.Length ? raw.triMaterial[t] : 0;
                if (!byMat.TryGetValue(mi, out var l)) byMat[mi] = l = new List<int>();
                l.Add(t);
            }
            parts = new List<MatPart>();
            foreach (var kv in byMat)
            {
                int matIdx = kv.Key;
                var local = new Dictionary<int, int>();
                var wedges = new List<int>();
                var idx = new List<int>();
                foreach (int t in kv.Value)
                    for (int k = 0; k < 3; k++)
                    {
                        int w = raw.triWedge[t * 3 + k];
                        if (!local.TryGetValue(w, out int li)) { li = wedges.Count; local[w] = li; wedges.Add(w); }
                        idx.Add(li);
                    }
                parts.Add(new MatPart
                {
                    MatIdx = matIdx,
                    TexIdx = raw.materialTexIdx != null && matIdx >= 0 && matIdx < raw.materialTexIdx.Length ? raw.materialTexIdx[matIdx] : matIdx,
                    Flags = raw.materialFlags != null && matIdx >= 0 && matIdx < raw.materialFlags.Length ? raw.materialFlags[matIdx] : 0,
                    Wedges = wedges.ToArray(), Indices = idx.ToArray(),
                });
            }
            _layout[raw] = parts;
            return parts;
        }

        static double Ms(long t0) => (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        // The batch key distinguishes AlphaBlend and the masked palette; a batch never spans actors (FUN_10b0d3c0 flushes
        // per DrawActorSprite), so the frame can draw actors in Core's order (WorldFrame.DrawOrder).
        // frameView: the one frame this batch is drawn in (WorldFrame.Views index, −1 = master, AnyView = every pass).
        ActorDraw NewDraw(int actorId, string tex, ActorBlend blend, bool unlit, bool twoSided, bool opacityMasked, int group, bool noSmooth, bool alphaTest, int frameView)
        {
            var d = new ActorDraw { ActorId = actorId, Texture = tex, Blend = blend, Unlit = unlit, TwoSided = twoSided, OpacityMasked = opacityMasked, Lod = TexLod.MeshSkin, Group = group, NoSmooth = noSmooth, AlphaTest = alphaTest, FrameView = frameView };
            Draws.Add(d); _ordered.Add(d);
            return d;
        }
        readonly HashSet<ActorDraw> _ordered = new();
        readonly List<(int, string, ActorBlend, bool, bool, bool, int, bool, bool, int)> _stale = new();

        ActorDraw DrawFor(int actorId, string tex, ActorBlend blend, bool unlit, bool twoSided, bool opacityMasked, int group, bool noSmooth, bool alphaTest, int frameView)
        {
            var key = (actorId, tex, blend, unlit, twoSided, opacityMasked, group, noSmooth, alphaTest, frameView);
            if (!_byKey.TryGetValue(key, out var d)) { d = new ActorDraw { ActorId = actorId, Texture = tex, Blend = blend, Unlit = unlit, TwoSided = twoSided, OpacityMasked = opacityMasked, Lod = TexLod.MeshSkin, Group = group, NoSmooth = noSmooth, AlphaTest = alphaTest, FrameView = frameView }; _byKey[key] = d; Draws.Add(d); }
            return d;
        }

        static ActorBlend BlendOf(HPEngine.ActorFx.ActorFxBlendMode m) => m switch
        {
            HPEngine.ActorFx.ActorFxBlendMode.Masked => ActorBlend.Masked,
            HPEngine.ActorFx.ActorFxBlendMode.Translucent => ActorBlend.Translucent,
            HPEngine.ActorFx.ActorFxBlendMode.Modulated => ActorBlend.Modulated,
            HPEngine.ActorFx.ActorFxBlendMode.AlphaBlend => ActorBlend.AlphaBlend,
            _ => ActorBlend.Opaque,
        };

        static string SlotTexture(in ActorSnapshot a, string meshTexture, int count)
        {
            if (a.MultiSkins != null && count >= 0 && count < a.MultiSkins.Count
                && !string.IsNullOrEmpty(a.MultiSkins[count]) && a.MultiSkins[count] != "None")
                return a.MultiSkins[count];
            bool meshHas = !string.IsNullOrEmpty(meshTexture);
            if (count != 0 && meshHas) return meshTexture;
            if (!string.IsNullOrEmpty(a.Skin) && a.Skin != "None") return a.Skin;
            return meshHas ? meshTexture : null;
        }

        // Empty material slot: environment texture of DrawLodMesh/DrawMesh (Render.dll 0x10b1174b-0x10b1183f; same block
        // in DrawMesh, Render.dll.c:9240-9268). Initial value is the last non-null GetTexture(i, Owner) over
        // i < Textures.Num (call [vtbl+0x88], push Owner, push i); overridden by Actor.Texture (+0x15c), else
        // Region.Zone.EnvironmentMap (+0xa0 -> +0x2dc), else Level.EnvironmentMap (+0x7c -> +0x2dc). A triangle with
        // an empty slot is drawn with this texture (0x10b65980, Render.dll.c:14934); if there is none the mesh is not drawn.
        string FallbackTexture(in ActorSnapshot a, string[] slotTable)
        {
            string last = null;
            if (slotTable != null)
                for (int i = 0; i < slotTable.Length; i++)
                    last = SlotTexture(a, string.IsNullOrEmpty(slotTable[i]) ? null : slotTable[i], i) ?? last;
            return RenderRules.MeshFallbackTexture(a.TexturePath, a.ZoneEnvironmentMap, a.LevelEnvironmentMap, last);
        }
    }
}
