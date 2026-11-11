using System;
using System.Collections.Generic;

namespace HP1.UEScript
{
    // PER-FRAME LIGHTMAPS OF MOVER SURFACES — owned by Core (moved from the frontend's MoverScene 2026-10-29).
    //
    // SetupForSurf takes a mover surface (BrushTracker->SurfIsDynamic, Render.dll.c:19845-19856) into one of two branches:
    //   * a mover without bDynamicLightMover — the LEVEL branch with the mover's brush model (+0x168): its iLightActors
    //     list, then the draw record's +0x48 list (URender::SurfLights of the tracker surface), every element through
    //     thunk(mover, light) (R:19867-19894). Here: LevelSurfLightmapRuntime over the mover geometry (UEMoverGeometry),
    //     the +0x48 lists translated from tracker surfaces (TrackedBrushModel.Link[FPoly] = iSurf) to geometry surfaces;
    //   * a mover with bDynamicLightMover — the mover branch (MoverMode3Lightmaps).
    // Zone layer: a lightmap's static tile follows the zone its polygon's fragments are drawn in (ZoneLayerTiles);
    // per-frame tiles belong to the load zone.
    // Tiles: one list per Core frame, in write order (level branch, zone layer, mode 3); the frontend copies them into
    // its mover atlas.
    public sealed class UEMoverLightmaps
    {
        readonly UELevelRuntime _rt;
        public readonly UEMoverGeometry Geo;
        readonly LevelSurfLightmapRuntime _level;
        public readonly MoverMode3Lightmaps Mode3;
        readonly int[] _trackerSurf;            // geometry surface -> tracker iSurf (-1: none)
        readonly bool[] _mode3Surf;             // geometry surface of a bDynamicLightMover mover
        public readonly List<LevelSurfLightmapRuntime.Tile> Tiles = new();
        public LevelSurfLightmapRuntime Level => _level;

        public UEMoverLightmaps(UELevelRuntime rt, WorldFrame frame)
        {
            _rt = rt;
            var zoneById = new Dictionary<int, int>();
            foreach (ref readonly ActorSnapshot a in frame.ActorSpan) zoneById[a.ObjectId] = a.ZoneNumber;
            Geo = rt.BuildMoverGeometry(o => zoneById.TryGetValue(o.ObjectId, out int z) ? z : 0);
            _level = new LevelSurfLightmapRuntime(Geo.Geometry, Geo.LightBits);
            Mode3 = new MoverMode3Lightmaps(rt, Geo, TrackerSurfOf);
            StaticGeometry g = Geo.Geometry;
            _trackerSurf = new int[g.Surfaces.Length];
            _mode3Surf = new bool[g.Surfaces.Length];
            Array.Fill(_trackerSurf, -1);
            for (int n = 0; n < g.Nodes.Length; n++)
            {
                int s = g.Nodes[n].Surf;
                if (s < 0 || s >= g.Surfaces.Length) continue;
                UEObject mover = Geo.Movers[Geo.MoverOfNode[n]].Actor;
                _trackerSurf[s] = TrackerSurfOf(mover, Geo.PolyOfSurf[s]);
                _mode3Surf[s] = UEInterpreter.ToBool(mover.GetProp("bDynamicLightMover"));
            }
        }

        // FPoly.iLink after SetupActorBrush: the tracker surface of a brush polygon (-1 when the mover is not tracked).
        int TrackerSurfOf(UEObject mover, int poly)
        {
            HeadlessBspModel.TrackedBrushModel bm = _rt.Bsp?.Tracker is null ? null : _rt.TrackedBrushModelOf(mover);
            return bm is not null && poly >= 0 && poly < bm.Link.Length ? bm.Link[poly] : -1;
        }

        // One Core frame. surfDyn — UEWorldFrameProducer.SurfDynamicLights (tracker iSurf -> light ObjectIds).
        public void Update(WorldFrame frame, SurfLightmap.FrameFx fx, Func<int, (int h, int s, int b)> zoneAmbient,
                           IReadOnlyDictionary<int, int[]> surfDyn)
        {
            Tiles.Clear();
            Dictionary<int, int[]> local = null;
            if (surfDyn is { Count: > 0 })
                for (int s = 0; s < _trackerSurf.Length; s++)
                    if (!_mode3Surf[s] && _trackerSurf[s] >= 0 && surfDyn.TryGetValue(_trackerSurf[s], out int[] ids))
                        (local ??= new Dictionary<int, int[]>())[s] = ids;
            _level.Update(frame, fx, zoneAmbient, local);
            Tiles.AddRange(_level.Tiles);
            Tiles.AddRange(ZoneLayerTiles(frame, fx, zoneAmbient));
            Tiles.AddRange(Mode3.Update(frame, fx, surfDyn));
        }

        // Zone layer: shipped seeds a mover lightmap from the zone of the node the fragment is DRAWN through
        // (SetupForSurf 0x10b079d7: draw record +0x34 = Zones[iZone[side]].ZoneActor; cache 0x18 key = iLightMap + zone
        // byte). Each brush polygon has its own lightmap (iBrushPoly), so a lightmap's zone is the front zone (iZone[1])
        // of its polygon's live tracker fragments. A lightmap whose fragments lie in zones of different ambient
        // (SpanningLightmaps) keeps its current tile: it needs per-fragment pieces (review plan A9, open).
        readonly Dictionary<int, Dictionary<int, LevelSurfLightmapRuntime.Tile>> _zoneTiles = new();
        Dictionary<int, int> _geoOfTracker;   // ZoneLayerTiles scratch (allocation only)
        readonly Dictionary<int, HashSet<int>> _zonesOf = new();
        readonly Stack<HashSet<int>> _zoneSetPool = new();
        readonly HashSet<(int, int, int)> _seeds = new();
        int[] _shownZone, _slotZone;   // per geometry lightmap: shown zone and the zone of its atlas slot (load zone)
        public int ZoneSwitches, SpanningLightmaps;
        public int ShownZone(int lightMap) => _shownZone is null ? -1 : _shownZone[lightMap];

        List<LevelSurfLightmapRuntime.Tile> ZoneLayerTiles(WorldFrame frame, SurfLightmap.FrameFx fx, Func<int, (int h, int s, int b)> zoneAmbient)
        {
            var outTiles = new List<LevelSurfLightmapRuntime.Tile>();
            StaticGeometry g = Geo.Geometry;
            if (_shownZone is null)
            {
                _shownZone = new int[g.LightMaps.Length];
                foreach (StaticNode n in g.Nodes)
                    if (n.Surf >= 0 && g.Surfaces[n.Surf].LightMap >= 0) _shownZone[g.Surfaces[n.Surf].LightMap] = n.Zone1;
                _slotZone = (int[])_shownZone.Clone();
            }
            HeadlessBspModel m = _rt.Bsp;
            if (m?.Tracker is null) return outTiles;
            // _trackerSurf/_mode3Surf are fixed at construction, so the tracker -> geometry surface map is built once.
            // zonesOf, its sets and seeds are reused per frame: Clear keeps insertion-order enumeration (no removals),
            // so the walk below sees the same lightmaps and zones in the same order as with fresh containers.
            if (_geoOfTracker is null)
            {
                _geoOfTracker = new Dictionary<int, int>();
                for (int s = 0; s < _trackerSurf.Length; s++) if (_trackerSurf[s] >= 0 && !_mode3Surf[s]) _geoOfTracker[_trackerSurf[s]] = s;
            }
            Dictionary<int, int> geoOfTracker = _geoOfTracker;
            var zonesOf = _zonesOf;
            foreach (HashSet<int> used in zonesOf.Values) { used.Clear(); _zoneSetPool.Push(used); }
            zonesOf.Clear();
            for (int i = m.LoadedNodeCount; i < m.LiveNodeCount; i++)
            {
                if (m.Tracker.NodeOwner(i) is null) continue;
                m.LiveNode(i, out _, out _, out _, out _, out _, out _, out _, out int ts, out _, out int z1, out _, out _, out _, out _, out _, out _, out _);
                if (!geoOfTracker.TryGetValue(ts, out int gs)) continue;
                int lm = g.Surfaces[gs].LightMap;
                if (lm < 0) continue;
                if (!zonesOf.TryGetValue(lm, out var set)) zonesOf[lm] = set = _zoneSetPool.Count > 0 ? _zoneSetPool.Pop() : new HashSet<int>();
                set.Add(z1);
            }
            SpanningLightmaps = 0;
            var seeds = _seeds;
            foreach (var (lm, zones) in zonesOf)
            {
                seeds.Clear();
                foreach (int z in zones) seeds.Add(zoneAmbient(z));
                if (seeds.Count > 1) { SpanningLightmaps++; continue; }
                int zone = -1;
                foreach (int z in zones) { zone = z; break; }
                if (zone == _shownZone[lm]) continue;
                _shownZone[lm] = zone; ZoneSwitches++;
                if (TilesForZone(zone, frame, fx, zoneAmbient).TryGetValue(lm, out var t))
                    outTiles.Add(new LevelSurfLightmapRuntime.Tile { LightMap = t.LightMap, Zone = _slotZone[lm], Width = t.Width, Height = t.Height, Rgb = t.Rgb, Frame = t.Frame });
            }
            return outTiles;
        }

        Dictionary<int, LevelSurfLightmapRuntime.Tile> TilesForZone(int zone, WorldFrame frame, SurfLightmap.FrameFx fx, Func<int, (int h, int s, int b)> zoneAmbient)
        {
            if (_zoneTiles.TryGetValue(zone, out var hit)) return hit;
            UEMoverGeometry g = _rt.BuildMoverGeometry(_ => zone);
            var run = new LevelSurfLightmapRuntime(g.Geometry, g.LightBits);
            run.Update(frame, fx, zoneAmbient);
            var map = new Dictionary<int, LevelSurfLightmapRuntime.Tile>();
            foreach (var t in run.Tiles) map[t.LightMap] = t;
            _zoneTiles[zone] = map;
            return map;
        }
    }

    public sealed partial class UELevelRuntime
    {
        UEMoverLightmaps _moverLightmaps;
        WorldStatic _moverLightmapsWorld;

        // Mover lightmaps of the CURRENT world (built on the first frame of a world, like SurfaceLightmaps).
        public UEMoverLightmaps MoverLightmaps
        {
            get
            {
                WorldStatic w = LatestWorldStatic;
                if (w?.Geometry is null || LatestFrame is null || Bsp is null) return null;
                if (_moverLightmaps is not null && ReferenceEquals(_moverLightmapsWorld, w)) return _moverLightmaps;
                _moverLightmaps = new UEMoverLightmaps(this, LatestFrame);
                _moverLightmapsWorld = w;
                return _moverLightmaps;
            }
        }
    }
}
