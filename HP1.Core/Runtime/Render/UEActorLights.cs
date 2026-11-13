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
        public void Clear() { _sets.Clear(); _used = 0; }
        // The SetupForActor result for the DrawMesh of frame `view` (−1 master, else WorldFrame.Views index).
        public bool TryGet(int objectId, int view, out Set s) { HP1.Core.UECoreOwner.Check("UEActorLights.TryGet"); return _sets.TryGetValue((objectId, view), out s); }
        // Test seam: a synthetic frame the tick did not draw (frontend tests of per-frame eyes) takes the list of `from`.
        public void ShareForTest(int objectId, int from, int to) => _sets[(objectId, to)] = _sets[(objectId, from)];

        public void Update(UELevelRuntime rt, UEInterpreter interp, WorldFrame frame, UESceneNode master,
                           double viewportSeconds, float levelBrightness)
        {
            _sets.Clear(); _used = 0;
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
            foreach (UESceneNode node in post)
            {
                int view = viewOf[node];
                foreach (ref readonly ActorSnapshot a in frame.ActorSpan)
                {
                    if (a.Destroyed || !drawn.Contains(a.ObjectId)) continue;
                    if (!frame.ActorViews.SetOf.TryGetValue(a.ObjectId, out int si))
                        throw new InvalidOperationException($"actor lights: mesh {a.Name} drawn without frame membership (WorldFrame.ActorViews)");
                    if (Array.IndexOf(frame.ActorViews.Sets[si], view) >= 0) Add(rt, in a, view, node, viewportSeconds);
                }
            }
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
            s.Count = Lighting.Gather(a.ObjectId, a.Location, a.Leaf, a.Light.SpecialLit, a.Unlit, _lineClear, s.Lights, dispatched);
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
