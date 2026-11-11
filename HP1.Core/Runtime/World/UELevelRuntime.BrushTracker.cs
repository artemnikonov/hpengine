// FMovingBrushTracker — LEVEL RUNTIME side (brush actors); the tracker bodies live in the level model
// (HeadlessBspModel.MovingBrushTracker, UEMovingBrushTracker.cs).
//
// The tracker (Engine.dll: GNewBrushTracker 0x1038e810 -> constructor FUN_1038cb80,
// SetupActorBrush FUN_1038d500, Update FUN_1038c260, destructor FUN_1038cff0) keeps
// mover brush fragments in Level->Model->Nodes; world traces, PointCheck and
// FastLineCheck see a mover as level geometry (D-47).
// Writes to actors (C4):
//   constructor (Engine.dll.c:237883-237923): every Level.Actors actor with Brush (+0x168),
//     IsA ABrush and without bStatic (+0x28 bit 0) must be an AMover (else GError "Cast of %s
//     to %s failed"); SetupActorBrush (238149-238197): SavedPos = (-1,-1,-1),
//     SavedRot = (123,456,789), bAssimilated (+0x28 bit 0x400) cleared; then Update(0)
//     (237560-237576): for the same set SavedPos = Location, SavedRot = Rotation;
//   Update(mover) (237586-237618): if SavedPos != Location or SavedRot != Rotation
//     (float/int per component) — SavedPos = Location, SavedRot = Rotation; ForceGroupFlush
//     adds to the list the brushes whose fragments lie under the moved one's fragments
//     (they also get SavedPos/SavedRot = current);
//   bAssimilated is set and cleared inside one Update — externally it is always false;
//   the destructor does not touch actors (only the model).
// Where it is built: UGameEngine::LoadMap — for a non-Entry map, after rebuilding Actors
// (247517-247590) and AFTER SpawnPlayActor (disasm: MatchViewportsToActors 0x1039e56c,
// then appStricmp(package, "Entry") and GNewBrushTracker 0x1039e5a0 -> GLevel+0xfc at
// 0x1039e5ab; UELevelRuntime.SpawnPlayActorThenBrushTracker); and SaveGame
// (0x103a2616-0x103a2628). Who calls Update(mover): only URender::SetupDynamics
// (Render 0x10b30501-0x10b30511) — UERenderVisibility.ShadowUpdatePass, in the PC24 window.
// The constructor and Update(0) run in game logic — PC64.
using System;
using System.Collections.Generic;
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        public bool BrushTrackerLive => Bsp?.Tracker is not null;

        static bool TrackedBrush(UEObject a)
            => a is not null && !a.Destroyed && a.GetProp(PK.Brush) is not null
               && UEInterpreter.ClassIsA(a.Class, "Brush") && !UEInterpreter.ToBool(a.GetProp(PK.bStatic));

        // Tracker brush model state (polygon iLink, chain head) — per brush export.
        readonly Dictionary<int, HeadlessBspModel.TrackedBrushModel> _trackedBrushModels = new();
        TrackerHost _trackerHost;

        public HeadlessBspModel.TrackedBrushModel TrackedBrushModelOf(UEObject a)
        {
            var brush = (UEModelRef)a.GetProp(PK.Brush);
            if (!_trackedBrushModels.TryGetValue(brush.ExportIndex, out var bm))
            {
                int polysRef = BrushModelOf(a).ParseTables.PolysRef;
                if (polysRef <= 0)
                    throw new System.IO.InvalidDataException($"mover {a.GetProp(PK.Name)}: the brush has no UPolys (Polys={polysRef}) — nothing for the tracker to insert");
                _trackedBrushModels[brush.ExportIndex] = bm =
                    new HeadlessBspModel.TrackedBrushModel(HeadlessBspModel.ReadPolys(_brushMapPath, polysRef));
            }
            return bm;
        }

        sealed class TrackerHost : HeadlessBspModel.ITrackerHost
        {
            readonly UELevelRuntime _rt;
            public TrackerHost(UELevelRuntime rt) => _rt = rt;
            public bool IsTrackedBrush(object a) => TrackedBrush((UEObject)a);
            public bool GetAssimilated(object a) => UEInterpreter.ToBool(((UEObject)a).GetProp("bAssimilated"));
            public void SetAssimilated(object a, bool v) => ((UEObject)a).Props["bAssimilated"] = v;
            public void SaveCurrent(object a)
            {
                var o = (UEObject)a;
                o.Props["SavedPos"] = UEValue.AsVector3(o.GetProp(PK.Location));
                o.Props["SavedRot"] = o.GetProp(PK.Rotation) is UERotator r ? r : default;
            }
            public bool Moved(object a)
            {
                var o = (UEObject)a;
                Vector3 loc = UEValue.AsVector3(o.GetProp(PK.Location)), sp = UEValue.AsVector3(o.GetProp("SavedPos"));
                UERotator rot = o.GetProp(PK.Rotation) is UERotator r ? r : default;
                UERotator sr = o.GetProp("SavedRot") is UERotator s ? s : default;
                return sp.x != loc.x || sp.y != loc.y || sp.z != loc.z
                    || sr.Pitch != rot.Pitch || sr.Yaw != rot.Yaw || sr.Roll != rot.Roll;
            }
            public HeadlessBspModel.TrackedBrushModel ModelOf(object a) => _rt.TrackedBrushModelOf((UEObject)a);
            public HeadlessBspModel.BrushPlacement PlacementOf(object a)
            {
                var o = (UEObject)a;
                UERotator rot = UEInterpreter.AsRotator(o.GetProp(PK.Rotation));
                return new HeadlessBspModel.BrushPlacement(UEInterpreter.AsVector3(o.GetProp(PK.Location)),
                    rot.Pitch, rot.Yaw, rot.Roll, UEInterpreter.AsVector3(o.GetProp(PK.PrePivot)),
                    BrushScaleOf(o, "MainScale"), BrushSheerOf(o, "MainScale"),
                    BrushScaleOf(o, "PostScale"), BrushSheerOf(o, "PostScale"));
            }
        }

        IReadOnlyList<object> TrackerActors()
        {
            var list = new object[Actors.Count];
            for (int i = 0; i < list.Length; i++) list[i] = Actors[i];
            return list;
        }

        // GNewBrushTracker(Level) — constructor FUN_1038cb80, game logic (PC64).
        // Tracker surface -> mover and its brush FPoly number (SetupActorBrush: FPoly.iLink = iSurf). Port (D-61): the
        // frame uses it to name to the frontend the mover batch that produced a child frame.
        readonly Dictionary<int, (UEObject Mover, int Poly)> _trackerSurfOwner = new();
        // A brush shared by several movers: FPoly.iLink is overwritten by the last one, all fragments take its
        // surfaces — whose surface it is, is undefined (loud failure at the consumer).
        readonly HashSet<int> _trackerSurfShared = new();

        internal void CreateBrushTracker()
        {
            _trackerSurfOwner.Clear(); _trackerSurfShared.Clear();
            var brushUser = new Dictionary<HeadlessBspModel.TrackedBrushModel, int[]>();
            if (Bsp is null) throw new InvalidOperationException("GNewBrushTracker: the level has no model (Level->Model)");
            _trackerHost ??= new TrackerHost(this);
            HeadlessBspModel.MovingBrushTracker t = Bsp.CreateTracker();
            foreach (UEObject a in Actors)
            {
                if (!TrackedBrush(a)) continue;
                if (!UEInterpreter.ClassIsA(a.Class, "Mover"))
                    throw new InvalidOperationException($"GNewBrushTracker: Cast of {a.GetProp(PK.Name)} to Mover failed (GError, Engine.dll.c:237903-237906)");
                a.Props["SavedPos"] = new Vector3(-1f, -1f, -1f);
                a.Props["SavedRot"] = new UERotator(123, 456, 789);
                a.Props["bAssimilated"] = false;
                HeadlessBspModel.TrackedBrushModel bm = TrackedBrushModelOf(a);
                t.SetupActorBrush(bm, UEInterpreter.ToBool(a.GetProp(PK.bSpecialLit)));
                for (int i = 0; i < bm.Link.Length; i++) _trackerSurfOwner[bm.Link[i]] = (a, i);   // port (D-61): FPoly.iLink = iSurf
                if (brushUser.TryGetValue(bm, out int[] prior)) { _trackerSurfShared.UnionWith(prior); _trackerSurfShared.UnionWith(bm.Link); }
                brushUser[bm] = (int[])bm.Link.Clone();
            }
            t.FinishConstruction();
            t.Update<FpuPc64>(null, TrackerActors(), _trackerHost);
        }

        // delete Level->BrushTracker (~FMovingBrushTracker, FUN_1038cff0).
        internal void DeleteBrushTracker()
        {
            _trackerSurfOwner.Clear(); _trackerSurfShared.Clear();
            DropTrackerSurfDecals();
            Bsp?.DeleteTracker();
        }

        // Update(mover) from URender::SetupDynamics; true — the mover was repositioned (moved). Precision is the
        // caller's window: PC24 inside the frame (appEnableFastMath(1)), PC64 outside it.
        internal bool UpdateBrushTracker(UEObject mover)
        {
            HeadlessBspModel.MovingBrushTracker t = Bsp?.Tracker;
            if (t is null || !TrackedBrush(mover)) return false;
            if (!_trackerHost.Moved(mover)) return false;
            if (UEWorldFrameProducer.FastMathWindow) t.Update<FpuPc24>(mover, null, _trackerHost);
            else t.Update<FpuPc64>(mover, null, _trackerHost);
            return true;
        }

        // Mover brush polygons whose tracker fragments lie on a level coplanar chain (FilterPoly SP_Coplanar ->
        // AddPolyFragment appends the fragment to the chain TAIL): OccludeBsp draws every earlier chain node first and
        // the span buffer hides the fragment where they cover it (decal-mover probe: Lev_Tut3 node 8834 behind floor
        // 3356). A Z-buffer z-fights there instead (Lev2_HogFront hedge gates opened into the hedge). Same rule as the
        // level model (CoplanarOcclusion.Later). Result per (mover ObjectId, brush FPoly): the polygon's visible area
        // in BRUSH-LOCAL coordinates = its live fragments, a covered one replaced by its visible pieces; only polygons
        // with at least one covered fragment are listed. A mover whose tracker pose is not its current one (no
        // SetupDynamics since it moved) is not listed: its fragments do not describe where it is now.
        // The same object is returned while the tracker and the movers stand still (a reader may compare references).
        // The tick's result (RunEngineFrame, after the tick's DrawWorld): what the frontend draws.
        public Dictionary<(int MoverId, int Poly), List<Vector3[]>> MoverCoplanarOfTick { get; private set; }
        public Dictionary<(int MoverId, int Poly), List<Vector3[]>> MoverCoplanarPieces()
        {
            HeadlessBspModel m = Bsp;
            HeadlessBspModel.MovingBrushTracker t = m?.Tracker;
            if (t is null) return new Dictionary<(int, int), List<Vector3[]>>();
            var movedNow = new List<int>();
            foreach (UEObject a in Actors)
                if (TrackedBrush(a) && _trackerHost.Moved(a)) movedNow.Add(a.ObjectId);
            if (_cutCache is not null && ReferenceEquals(_cutTracker, t) && _cutUpdates == t.Updates && System.Linq.Enumerable.SequenceEqual(movedNow, _cutMoved))
                return _cutCache;
            var result = new Dictionary<(int, int), List<Vector3[]>>();
            _cutTracker = t; _cutUpdates = t.Updates; _cutMoved = movedNow; _cutCache = result;
            int loaded = m.LoadedNodeCount, live = m.LiveNodeCount;
            Dictionary<int, int> prev = _cutPrev;                 // reused (allocation only): rebuilt from scratch here
            prev.Clear();
            for (int i = 0; i < live; i++)
            {
                if (i >= loaded && t.NodeOwner(i) is null) continue;
                m.LiveNode(i, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out int next, out _, out _, out _, out _);
                if (next >= 0) prev[next] = i;
            }
            var touched = new Dictionary<(int, int), UEObject>();
            var fragments = new Dictionary<(int, int), List<(int Node, List<Vector3[]> Pieces)>>();
            var chain = new List<int>();
            var links = new List<CoplanarOcclusion.ChainNode>();
            for (int j = loaded; j < live; j++)
            {
                if (t.NodeOwner(j) is not UEObject mover) continue;
                m.LiveNode(j, out _, out _, out _, out _, out _, out _, out _, out int surf, out _, out _, out _, out _, out _, out _, out int nv, out _, out _);
                if ((nv & 0xff) < 3 || !_trackerSurfOwner.TryGetValue(surf, out var owner) || _trackerHost.Moved(mover)) continue;
                var key = (mover.ObjectId, owner.Poly);
                List<Vector3[]> pieces = null;
                if (prev.ContainsKey(j))
                {
                    chain.Clear();
                    for (int k = j; prev.TryGetValue(k, out int p) && chain.Count <= live; k = p) chain.Add(p);
                    chain.Reverse(); chain.Add(j);
                    links.Clear();
                    foreach (int k in chain) links.Add(ChainNodeOf(m, k));
                    pieces = CoplanarOcclusion.Later(links, chain.Count - 1);
                }
                if (!fragments.TryGetValue(key, out var list)) fragments[key] = list = new();
                list.Add((j, pieces));
                if (pieces is not null) touched[key] = mover;
            }
            foreach (var (key, list) in fragments)
            {
                if (!touched.TryGetValue(key, out UEObject mover)) continue;
                BrushCoords local = BrushToLocalCoords(mover);
                var outPieces = new List<Vector3[]>();
                foreach (var (node, pieces) in list)
                    foreach (Vector3[] w in pieces ?? new List<Vector3[]> { ChainNodeOf(m, node).Poly })
                    {
                        var l = new Vector3[w.Length];
                        for (int k = 0; k < w.Length; k++) l[k] = local.TransformPoint(w[k]);
                        outPieces.Add(l);
                    }
                result[key] = outPieces;
            }
            return result;
        }

        Dictionary<(int, int), List<Vector3[]>> _cutCache;
        HeadlessBspModel.MovingBrushTracker _cutTracker;
        int _cutUpdates;
        List<int> _cutMoved;
        readonly Dictionary<int, int> _cutPrev = new();

        CoplanarOcclusion.ChainNode ChainNodeOf(HeadlessBspModel m, int node)
        {
            m.LiveNode(node, out float px, out float py, out float pz, out _, out _, out _, out _, out int surf, out int z0, out int z1,
                out _, out _, out _, out int pool, out int nv, out _, out _);
            nv &= 0xff;
            if (nv < 3 || surf < 0 || surf >= m.SurfCount) return new CoplanarOcclusion.ChainNode(null, new Vector3(px, py, pz), z0, z1, 0);
            var poly = new Vector3[nv];
            for (int k = 0; k < nv; k++) poly[k] = m.LivePoint(m.LiveVertex(pool + k));
            return new CoplanarOcclusion.ChainNode(poly, new Vector3(px, py, pz), z0, z1, m.Surf(surf).PolyFlags | SurfTexturePolyFlags(surf));
        }

        // Client.ParticleDensity (UClient +0x9c, Engine.dll.c:121980-121985) — config property of the
        // client class, section [<ViewportManager>]; missing key — class default 0.
        internal static int ReadClientParticleDensity(UEConfig config)
        {
            string client = config?.ResolveSection(UEConfig.ScopeSystem, "Engine.Engine.ViewportManager")
                ?? throw new InvalidOperationException("ParticleDensity: the config does not name ViewportManager");
            string raw = config.Get(UEConfig.ScopeSystem, client, "ParticleDensity");
            if (raw is null) return 0;
            if (!int.TryParse(raw.Trim(), out int v))
                throw new InvalidOperationException($"ParticleDensity \"{raw}\" — not an integer");
            return v;
        }

        // LoadMap: `if (appStricmp(Level->URL.Map, "Entry")) Level->BrushTracker = GNewBrushTracker(Level)`.
        internal void CreateBrushTrackerUnlessEntry()
        {
            string map = System.IO.Path.GetFileNameWithoutExtension(LoadedMapPath ?? "");
            if (string.Equals(map, "Entry", StringComparison.OrdinalIgnoreCase)) return;
            CreateBrushTracker();
        }

    }
}
