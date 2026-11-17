using System;
using System.Collections.Generic;

namespace HP1.UEScript
{
    // ACTOR LIGHT LISTS AND VERTEX FOG INPUTS OF A TICK — Core's.
    //
    // FLightManager::SetupForActor runs inside DrawFrame for every mesh the frame draws (Render.dll.c:21222-21455): the
    // light-source culling with its FMemCache pairs and fade counters, the LOS traces against the level model and the
    // zone ambient. Here once per tick, after the tick's DrawWorld, for the meshes Core drew (WorldFrame.MeshPoses: frame
    // actors in frame order, then third-person weapons — the order the frontend used): UEActorLighting.Gather and
    // UEActorLighting.Inputs on the viewport clock. Vertex fog: ActorFogFrame bound to the level's fog zones and static
    // volumetric lights, begun on the tick's occlusion tree. The frontend reads the results (Of) and only runs the
    // per-vertex bodies (UEActorLighting.LightPoints, ActorFogFrame.Apply).
    public sealed class UEActorLights
    {
        public sealed class Set
        {
            public readonly UEActorLighting.Exported[] Lights = new UEActorLighting.Exported[UEActorLighting.ExportCap];
            public int Count;
            public UEActorLighting.ActorInputs In;
        }

        public readonly UEActorLighting Lighting = new();
        public readonly ActorFogFrame Fog = new();
        readonly Dictionary<(int id, int view), Set> _sets = new();
        readonly List<Set> _pool = new();
        int _used;
        Func<UEVector, UEVector, bool> _lineClear;
        WorldFrame _frame;   // the frame Update runs on

        public UEActorLights(StaticGeometry g, HashSet<int> fogZones)
        {
            Lighting.SetLeafChain(g.LeafPermeating, g.LightExports);
            Fog.Bind(fogZones, g);
        }

        public bool ViewLit { get { HP1.Core.UECoreOwner.Check("UEActorLights.ViewLit"); return Lighting.ViewLitFrame; } }

        // The tick's frames (pre-order: master, then WorldFrame.Views) and the viewport actor, for TriangleOrder.
        readonly List<UESceneNode> _frames = new();
        UELevelRuntime _rt; int _viewerId;

        // DrawLodMesh's triangle order of a mesh drawn by frame `view` (−1 master, else WorldFrame.Views index) at the tick
        // pose `points`: accepted faces sorted (MeshTriangleOrder), or null when the actor's flags do not sort.
        public int[] TriangleOrder(in ActorSnapshot a, int view, int[] triWedge, int[] wedgeVertex, int[] triMaterial,
            int[] materialFlags, UEVector[] points)
        {
            // A plain UMesh: DrawMesh's UMesh branch decides its triangle list itself (MeshTriangleOrder.UMeshOrder), always.
            if (_rt?.FindActorById(a.ObjectId)?.GetProp(PK.Mesh) is UEResourceRef { Class: var mc } mr
                && mc.Equals("Mesh", StringComparison.OrdinalIgnoreCase))
            {
                if (view + 1 < 0 || view + 1 >= _frames.Count)
                    throw new InvalidOperationException($"TriangleOrder: frame {view} outside the tick's tree ({_frames.Count} frames)");
                return MeshTriangleOrder.UMeshOrder(_frames[view + 1], _rt.MeshCatalog.GetVertexMesh(mr.Name, mr.ObjectPath),
                    points, _rt.ReadSpanBased(), _viewerId != 0 && a.OwnerId == _viewerId);
            }
            int pf = MeshTriangleOrder.ActorPolyFlags(a.Style, a.Opacity);
            if ((pf & MeshTriangleOrder.PF_AlphaBlendWord) == 0) return null;
            if (view + 1 < 0 || view + 1 >= _frames.Count)
                throw new InvalidOperationException($"TriangleOrder: frame {view} outside the tick's tree ({_frames.Count} frames)");
            UEObject live = _rt?.FindActorById(a.ObjectId)
                ?? throw new InvalidOperationException($"TriangleOrder: actor {a.ObjectId} is not live");
            bool bias = UEValue.ToBool(live.GetProp("bTwoSidedBias"));
            float ch = UEValue.AsFloat(live.GetProp(PK.CollisionHeight));
            return MeshTriangleOrder.Order(_frames[view + 1], triWedge, wedgeVertex, triMaterial, materialFlags, points, pf,
                bias, ch, _viewerId != 0 && a.OwnerId == _viewerId);
        }
        // DrawLodMesh's two-sided depth bias of the same mesh in frame `view` (MeshTriangleOrder.TwoSidedDepthScale): per wedge
        // the factor of the frame-space point toward the frame origin, or null. A plain UMesh (DrawMesh's own branch) has none.
        public float[] TwoSidedDepthScale(in ActorSnapshot a, int view, int[] triWedge, int[] wedgeVertex, int[] triMaterial,
            int[] materialFlags, UEVector[] points)
        {
            UEObject live = _rt?.FindActorById(a.ObjectId)
                ?? throw new InvalidOperationException($"TwoSidedDepthScale: actor {a.ObjectId} is not live");
            if (live.GetProp(PK.Mesh) is UEResourceRef { Class: var mc } && mc.Equals("Mesh", StringComparison.OrdinalIgnoreCase)) return null;
            if (!UEValue.ToBool(live.GetProp("bTwoSidedBias"))) return null;
            int pf = MeshTriangleOrder.ActorPolyFlags(a.Style, a.Opacity);
            if ((pf & MeshTriangleOrder.PF_TwoSided) == 0 && (materialFlags is null || Array.TrueForAll(materialFlags, m => (m & MeshTriangleOrder.PF_TwoSided) == 0))) return null;
            if (view + 1 < 0 || view + 1 >= _frames.Count)
                throw new InvalidOperationException($"TwoSidedDepthScale: frame {view} outside the tick's tree ({_frames.Count} frames)");
            return MeshTriangleOrder.TwoSidedDepthScale(_frames[view + 1], triWedge, wedgeVertex, triMaterial, materialFlags, points, pf, true);
        }
        public void Clear() { _sets.Clear(); _used = 0; }
        // The SetupForActor result for the DrawMesh of frame `view` (−1 master, else WorldFrame.Views index).
        public bool TryGet(int objectId, int view, out Set s) { HP1.Core.UECoreOwner.Check("UEActorLights.TryGet"); return _sets.TryGetValue((objectId, view), out s); }
        // Test seam: a synthetic frame the tick did not draw (frontend tests of per-frame eyes) takes the list of `from`.
        public void ShareForTest(int objectId, int from, int to) => _sets[(objectId, to)] = _sets[(objectId, from)];

        public void Update(UELevelRuntime rt, UEInterpreter interp, WorldFrame frame, UESceneNode master,
                           double viewportSeconds, float levelBrightness)
        {
            _sets.Clear(); _used = 0; _frame = frame;
            _frames.Clear(); _rt = rt; _viewerId = rt.PlayerPawn?.ObjectId ?? 0;
            if (master is not null) { void Walk(UESceneNode f) { _frames.Add(f); foreach (UESceneNode c in f.Children) Walk(c); } Walk(master); }
            _lineClear ??= UEActorLighting.BspLineClear(rt, interp);
            Lighting.BeginFrame(frame, viewportSeconds, UEActorLighting.ViewLit(rt), levelBrightness);
            Fog.Begin(frame, master, levelBrightness);
            if (rt.FrameProducer is { } fp) foreach (var (weapon, pawn) in fp.WeaponPawns) Fog.DrawnWithSpriteOf(weapon, pawn);
            // URender::DrawMesh calls FLightManager::SetupForActor for every DrawMesh: once per frame of the tree that draws
            // the actor, in DrawFrame order (children before the parent, Render.dll.c:1011-1014). The fade-in rate is the
            // viewport's frame time (Render.dll.c:21210-21212), the same for every call of the tick, so an actor drawn by k
            // frames advances its pair counters k times; each frame draws with the list of its own call.
            var drawn = new HashSet<int>();
            foreach (MeshPose mp in frame.MeshPoses) drawn.Add(mp.ObjectId);
            var viewOf = new Dictionary<UESceneNode, int>();
            for (int i = 0; i < _frames.Count; i++) viewOf[_frames[i]] = i - 1;
            var post = new List<UESceneNode>();
            void Post(UESceneNode f) { foreach (UESceneNode c in f.Children) Post(c); post.Add(f); }
            if (master is not null) Post(master);
            // A third-person weapon's DrawMesh comes right after its pawn's, inside the pawn's DrawActorSprite (HP1 Render
            // 0x10b32ea1 the actor, 0x10b33085 the weapon): its SetupForActor call follows the pawn's.
            Dictionary<int, int> weaponOf = rt.FrameProducer?.WeaponPawns;
            ReadOnlySpan<ActorSnapshot> span = frame.ActorSpan;
            foreach (UESceneNode node in post)
            {
                int view = viewOf[node];
                for (int i = 0; i < span.Length; i++)
                {
                    ref readonly ActorSnapshot a = ref span[i];
                    if (weaponOf is not null && weaponOf.ContainsKey(a.ObjectId)) continue;
                    AddIfDrawn(in a);
                    if (weaponOf is null || weaponOf.Count == 0) continue;
                    for (int w = 0; w < span.Length; w++)
                        if (weaponOf.TryGetValue(span[w].ObjectId, out int pawn) && pawn == a.ObjectId) AddIfDrawn(in span[w]);
                }
                void AddIfDrawn(in ActorSnapshot a)
                {
                    if (a.Destroyed || !drawn.Contains(a.ObjectId)) return;
                    if (!frame.ActorViews.SetOf.TryGetValue(a.ObjectId, out int si))
                        throw new InvalidOperationException($"actor lights: mesh {a.Name} drawn without frame membership (WorldFrame.ActorViews)");
                    if (Array.IndexOf(frame.ActorViews.Sets[si], view) >= 0) Add(rt, in a, view, node, viewportSeconds);
                }
            }
            // The tags SetupForActor wrote on the light actors this frame (UEActorLighting.TagsTaken), as ints on the actors.
            foreach (var (id, t) in Lighting.TagsTaken)
                if (rt.FindActorById(id) is UEObject light)
                {
                    light.Props["LightingTag"] = t.Score;
                    light.Props["ExtraTag"] = t.Stamp;
                    light.Props["SpecialTag"] = t.Counter;
                }
            Lighting.TagsTaken.Clear();
        }

        void Add(UELevelRuntime rt, in ActorSnapshot a, int view, UESceneNode node, double viewportSeconds)
        {
            Set s = _used < _pool.Count ? _pool[_used] : null;
            if (s is null) { s = new Set(); _pool.Add(s); }
            _used++;
            s.In = UEActorLighting.Inputs(a, rt.ZoneAmbientHSB(a.ZoneNumber), viewportSeconds);
            // Unlit actors and actors in leaf -1 get no list: SetupForActor gates (UEActorLighting.Gather).
            // Dispatched dynamics: the frame's sprite of the actor carries LeafLights[iLeaf] (UESprite.LeafLights). A
            // third-person weapon is drawn with its pawn's sprite (UEWorldFrameProducer.WeaponPawns).
            int[] dispatched = Array.Empty<int>();
            int id = rt.FrameProducer is { } fp && fp.WeaponPawns.TryGetValue(a.ObjectId, out int pawn) ? pawn : a.ObjectId;
            foreach (UESprite sp in node.Sprites) if (sp.Actor?.ObjectId == id) { dispatched = sp.LeafLights; break; }
            // SetupForActor's actor: HP1 DrawMesh passes its own Actor argument ([ebp+0x10], HP1 0x10b0f413) — the weapon;
            // HP2 DrawMesh passes the sprite's actor (Sprite+0x98, HP2 0x10b111ff) — the pawn, so a third-person weapon's
            // call runs on the pawn (its cache, location, leaf and flags; SwARc host: both calls on Harry's Index).
            ref readonly ActorSnapshot lit = ref a;
            if (id != a.ObjectId && UEGameProfile.Current.Game == UEEdition.Game.HP2)
                foreach (ref readonly ActorSnapshot o in _frame.ActorSpan)
                    if (o.ObjectId == id) { lit = ref o; break; }
            s.Count = Lighting.Gather(lit.ObjectId, lit.Location, lit.Leaf, lit.Light.SpecialLit, lit.Unlit, _lineClear, s.Lights, dispatched);
            _sets[(a.ObjectId, view)] = s;
        }
    }

    public sealed partial class UELevelRuntime
    {
        UEActorLights _actorLights;
        WorldStatic _actorLightsWorld;

        // Actor light lists of the CURRENT world (per tick, with the per-frame surface lightmaps).
        public UEActorLights ActorLights => ActorLightsChecked();
        UEActorLights ActorLightsChecked() { HP1.Core.UECoreOwner.Check("UELevelRuntime.ActorLights"); return _actorLights is not null && ReferenceEquals(_actorLightsWorld, LatestWorldStatic) ? _actorLights : null; }

        internal void UpdateActorLights(UEInterpreter interp)
        {
            WorldStatic w = LatestWorldStatic;
            if (w?.Geometry is null || LatestFrame is null || interp is null) return;
            if (_actorLights is null || !ReferenceEquals(_actorLightsWorld, w))
            {
                LevelFogMaps fm = FogMaps;
                _actorLights = new UEActorLights(w.Geometry, LevelFogMaps.Zones(w.Geometry, LatestFrame));
                _actorLightsWorld = w;
            }
            // A frame that did not draw the world (no LightmapFx) draws no meshes: no lists.
            if (FrameProducer?.LightmapFx is not SurfLightmap.FrameFx fx) { _actorLights.Clear(); return; }
            _actorLights.Update(this, interp, LatestFrame, FrameProducer.LastMasterFrame, FrameProducer.ViewportClock, fx.LevelBrightness);
        }
    }
}
