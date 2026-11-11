// FRAME OCCLUSION — port of URender::OccludeBsp, FDynamicSprite and CreateChildFrame (Render.dll 1.53) into Core (D-56).
//
// Decides: which sprites (meshes, emitters) enter the frame list Frame->Sprite (+0xa4), i.e. for whom DrawFrame
// runs GetFrame/ApplyAnim and Update(0); which child frames (sky, mirrors, warp portals) the walk spawns; the
// NodeFlags 0x10/0x08 bits of model nodes and zone LastRenderTime (render-influence ledger, game rows: 0x10b22c79,
// 0x10b22d8e, 0x10b2354a, 0x10b24ae6, 0x10b227ff, 0x10b2468d). Surface pixels (FSurfaceInfo, Draw lists,
// leaf/surface lighting) are not ported — they do not affect sprite visibility.
//
// Bodies (Render.dll addresses; the disasm is the source, the decompile reorders branches in places):
//   URender::OccludeBsp       0x10b22480..0x10b24f30 (export 0x1140)
//   FDynamicSprite::ctor      0x10b30790 (sprite corners in world, raster rows, item 0x10b395e4 into node 0)
//   sprite item Filter        0x10b31160 (vtable 0x10b395e4 slot 0): split by the node plane
//   final item PreRender      0x10b319a0 (vtable 0x10b39600 slot 1): CopyFromRaster against the zone span buffer
//   URender::CreateChildFrame 0x10b20790 (export 0x1190)
//   FSceneNode::ComputeRenderSize Engine 0x10383390 (for a child — inside the render window, PC24)
// The render window is x87 PC24 (URender::PreRender -> appEnableFastMath(1)): all arithmetic below is float.
//
// NOT TRACKED LIKE shipped (stated loudly):
//   * shipped does not write the filter item's sort key (+8/+0xc) — the final item insertion comparison
//     (0x10b315..: `key <= item.+0xc + item.+8`) reads GDynMem memory of earlier allocations. The port compares only
//     against final items. This affects only the PreRender call order within one leaf; the frame list is still
//     sorted by key, so only the order of sprites with EQUAL keys can differ.
//   * lighting: light items (vtable 0x10b395c8, slot 0 = FUN_10b31c70) ARE walked — both halves of FilterLight: the
//     surface half (URender::SurfLights, the draw record's +0x48 list) and the leaf half (URender::LeafLights, items'
//     +0x14 volumetric / +0x18 leaf flags): LeafVolumetricLighting appends a leaf's flagged links to the walk's volumetric
//     list (UESceneNode.VolumetricDynamic), DrawFrame gives a sprite its actor leaf's list (UESprite.LeafLights).
//   * DrawSurf (+0x98 frame lists) are not built: of their registration only clearing node bit 0x08 and the
//     emptied-zone check affect visibility — those are ported.
using System;
using System.Collections.Generic;
using System.Linq;
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    // FDynamicSprite (UnSprite.cpp): a mesh or emitter sprite in the frame.
    public sealed class UESprite
    {
        public UEObject Actor;                           // +0x98
        public float Z, Zc;                              // +8 (MinZ), +0xc (MaxZ) — from FScreenBounds Setup
        public int X1, Y1, X2, Y2;                       // +0x9c..+0xa8
        // Inline four-element buffers (not arrays): one object per sprite instead of four (allocation only).
        public Four<Vector3> P;                          // rectangle corners in world: +0x18 + k·0x20
        public Four<float> SX, SY;                       // corner screen position: +0x28/+0x2c + k·0x20
        [System.Runtime.CompilerServices.InlineArray(4)]
        public struct Four<T> { T _e0; }
        public UESpanBuffer Span;                        // +0x10
        // +0xb8 Volumetrics: FinalItem::PreRender (0x10b31b71..0x10b31c05) appends, after the first successful CopyFromRaster
        // and after every successful merge, every flagged entry of the walk's volumetric list (URender+0x30) it does not
        // hold yet. That list only grows during the walk (LeafVolumetricLighting per leaf, reset at OccludeBsp start), so
        // the sprite's list = the list at its last successful PreRender: the frame's first VolLeafMark volumetric leaves.
        public int VolLeafMark = -1;
        // +0xbc: URender::LeafLights[Actor->Region.iLeaf] after the frame's OccludeBsp (DrawFrame, Render.dll.c:690-696, when
        // the model has leaves): light ObjectIds in list order — the dynamic lights SetupForActor considers (:21346-21372).
        public int[] LeafLights = Array.Empty<int>();
        internal float Key => Zc + Z;                    // fld [+0xc]; fadd [+8]
    }

    // Dynamics item raster rows (shipped: {Y0, Y1, FRasterSpan[Y1-Y0]}).
    internal sealed class UESpanRows
    {
        public int Y0, Y1;
        public (int X0, int X1)[] R;
        public UESpanRows(int y0, int y1, int capacity) { Y0 = y0; Y1 = y1; R = new (int, int)[Math.Max(0, capacity)]; }
        // Pool reuse (port, allocation only): the first `capacity` rows read as a new array's.
        public void Reset(int y0, int y1, int capacity)
        {
            Y0 = y0; Y1 = y1; capacity = Math.Max(0, capacity);
            if (R.Length < capacity) R = new (int, int)[capacity]; else Array.Clear(R, 0, capacity);
        }
        public ref (int X0, int X1) Row(int y) => ref R[y - Y0];
    }

    // URender::DynamicsCache item: filter (0x10b395e4) or final (0x10b39600).
    internal sealed class UEDynItem
    {
        public UEDynItem Next;
        public bool Final;
        public UESprite Sprite;
        public UESpanRows Rows;
        public UEDynLight Light;                         // light item (vtable 0x10b395c8): +0x10 the light actor
        public bool Vol;                                 // light item +0x14: volumetric (SetupDynamics, narrowed per child)
        public bool Leafed;                              // light item +0x18: already linked into a leaf on this path
    }

    // Light actor of a light item: what FUN_10b31c70 reads from it.
    public sealed class UEDynLight
    {
        public int ObjectId;
        public Vector3 Pos;                              // +0xfc
        public float Radius;                             // vtbl +0x70 WorldLightRadius
        public bool SpecialLit;                          // +0x1f0 bit 0
        public int VolumeRadius;                         // +0x1eb
    }

    // World read by the walk: model, zones, viewer and device flags, Generate event.
    public sealed class UEOcclusionWorld
    {
        public HeadlessBspModel Bsp;
        public Func<int, UEObject> ZoneActor;            // Model.Zones[z].ZoneActor (None -> null)
        public UEObject LevelInfo;                       // Level->Actors[0]
        public object Level;                             // FSceneNode.Level (+4) — compared for children
        public int ShowFlags, RendMap;
        public Func<bool> SpanBased;                     // RenDev +0x48 — read in the mirror and warp branches and by the DrawFrame sprite sort (D-79)
        public Func<bool> Shiny;                         // RenDev.ShinySurfaces (+0x5c) — read only for a translucent mirror
        public Func<int, int> TexturePolyFlags;          // iSurf -> Surf->Texture->PolyFlags (+0x80); no texture — 0
        public Action<UEObject> Generate;                // WarpZoneInfo: ProcessEvent(Generate)
        public float TimeSeconds;                        // LevelInfo +0x3b4
        public Func<int, ViewSource> SourceOf;           // port (D-61): iSurf -> level surface or mover polygon
        public Func<int, int, ViewSource> PolyOf;        // port (frame export): (iSurf, iNode) -> level surface or the mover polygon of the fragment's owner
    }

    public sealed class UEOcclusion
    {

        // DAT_10b719b4: URender::DrawWorld counter (0x10b27ba0: ++ at entry). A node without bit 0x10 tests its bound
        // only when (iNode ^ counter) & 0xf == 0.
        public int DrawWorldCounter;

        UEDynItem[] _cache = Array.Empty<UEDynItem>();    // DynamicsCache: two lists per node
        readonly List<int> _touched = new();
        // URender::SurfLights (iSurf -> FActorLink list, newest first) and DynLightSurfs/NumDynLightSurfs of the frame
        // being walked; OccludeBsp hands them to the frame (UESceneNode.SurfLights) and clears them (OccludeFrame,
        // Render.dll.c:672, 705-708).
        readonly Dictionary<int, List<int>> _surfLights = new();
        // URender::LeafLights (iLeaf -> FVolActorLink list, newest first: light ObjectId and the link's +0x14 flag) of the
        // frame being walked; cleared with DynLightLeaves at the frame's end (Render.dll.c:711-713).
        readonly Dictionary<int, List<(int Id, bool Vol)>> _leafLights = new();
        public int LightItems, LightFilters, SurfLightLinks;
        readonly UEBspRaster _raster = new();
        // Port (allocation only, render scheduling): span nodes/buffers of the frame tree (reset at a master frame's walk)
        // and the DynamicsCache items and rows of one frame (reset by ClearCache, which empties every list holding them).
        readonly UESpanArena _arena = new();
        readonly UESpanBuffer[] _zoneSpans = new UESpanBuffer[64];
        readonly List<UEDynItem> _items = new();
        readonly List<UESpanRows> _rowsPool = new();
        int _nItems, _nRows;
        int[][] _surfVerts = Array.Empty<int[]>();
        (int, int)[] _surfRows = Array.Empty<(int, int)>();
        HeadlessBspModel _pointModel;
        Func<int, Vector3> _pointOf;
        bool? _spanBased;

        UEDynItem NewItem()
        {
            UEDynItem it;
            if (_nItems < _items.Count) it = _items[_nItems];
            else _items.Add(it = new UEDynItem());
            _nItems++;
            it.Next = null; it.Final = false; it.Sprite = null; it.Rows = null; it.Light = null; it.Vol = false; it.Leafed = false;
            return it;
        }

        // Port (allocation only): sprites pooled per DrawWorld (DrawWorldCounter), two banks by its parity — the previous
        // tree stays readable (LastMasterFrame consumers) while the next one is built. A reused sprite is in the state
        // `new` gives; CreateSprite writes every other field.
        readonly List<UESprite>[] _spriteBanks = { new(), new() };
        int _spriteBankOf = int.MinValue, _nSprites;
        UESprite NewSprite()
        {
            if (_spriteBankOf != DrawWorldCounter) { _spriteBankOf = DrawWorldCounter; _nSprites = 0; }
            List<UESprite> bank = _spriteBanks[DrawWorldCounter & 1];
            UESprite s;
            if (_nSprites < bank.Count) { s = bank[_nSprites]; s.Span = null; s.VolLeafMark = -1; s.LeafLights = Array.Empty<int>(); }
            else bank.Add(s = new UESprite());
            _nSprites++;
            return s;
        }

        // Port (allocation only): the FSavedPoly point and edge-plane arrays (UESceneNode.DrawnPolys/DrawnPolyVolumetrics)
        // pooled per DrawWorld in two banks by DrawWorldCounter parity, like the sprites — the previous tree's arrays stay
        // intact while the next is built. Every entry of a handed-out array is written before the tree is read.
        sealed class PolyBank
        {
            sealed class Bucket { public readonly List<Vector3[]> L = new(); public int N; }
            readonly Dictionary<int, Bucket> _byLength = new();
            public readonly Func<int, Vector3[]> Take;
            public PolyBank() => Take = TakeArray;
            public void Reset() { foreach (Bucket b in _byLength.Values) b.N = 0; }
            Vector3[] TakeArray(int n)
            {
                if (n == 0) return Array.Empty<Vector3>();
                if (!_byLength.TryGetValue(n, out Bucket b)) _byLength[n] = b = new Bucket();
                if (b.N < b.L.Count) return b.L[b.N++];
                var a = new Vector3[n]; b.L.Add(a); b.N++;
                return a;
            }
        }
        readonly PolyBank[] _polyBanks = { new(), new() };
        int _polyBankOf = int.MinValue;
        PolyBank PolyBankNow()
        {
            PolyBank bank = _polyBanks[DrawWorldCounter & 1];
            if (_polyBankOf != DrawWorldCounter) { _polyBankOf = DrawWorldCounter; bank.Reset(); }
            return bank;
        }

        UESpanRows NewRows(int y0, int y1, int capacity)
        {
            if (_nRows < _rowsPool.Count) { UESpanRows r = _rowsPool[_nRows++]; r.Reset(y0, y1, capacity); return r; }
            var n = new UESpanRows(y0, y1, capacity);
            _rowsPool.Add(n); _nRows++;
            return n;
        }

        // RenDev.SpanBased (+0x48): a device flag, constant during a walk — read once per walk.
        bool SpanBased(UEOcclusionWorld w)
        {
            _spanBased ??= w.SpanBased();
            return _spanBased.Value;
        }

        // Branches of the last pass (the judge requires the compared branch to have run).
        public int Frames, NodesEntered, BoxTests, BoxCulled, Surfaces, SurfacesOccluded, Portals, Mirrors, Skies, Warps,
            Filters, Splits, PreRenders, SpritesListed, ZonesEmptied;

        void EnsureCache(int nodes)
        {
            if (_cache.Length < nodes * 2) _cache = new UEDynItem[nodes * 2];
        }

        void Push(int slot, UEDynItem it)
        {
            if (_cache[slot] is null) _touched.Add(slot);
            it.Next = _cache[slot];
            _cache[slot] = it;
        }

        // OccludeFrame (Render.dll.c:699-702): after the frame's OccludeBsp all DynamicsCache lists are empty.
        public void ClearCache()
        {
            foreach (int s in _touched) _cache[s] = null;
            _touched.Clear();
            _nItems = 0; _nRows = 0;
            // The lists go back to pools emptied (allocation only; nothing outside holds them: OccludeBsp copies them out).
            foreach (List<int> l in _surfLights.Values) { l.Clear(); _surfListPool.Push(l); }
            foreach (var l in _leafLights.Values) { l.Clear(); _leafListPool.Push(l); }
            _surfLights.Clear();
            _leafLights.Clear();
        }
        readonly Stack<List<int>> _surfListPool = new();
        readonly Stack<List<(int Id, bool Vol)>> _leafListPool = new();

        // ── URender::SetupDynamics light gate (0x10b3051a..0x10b306f5) ────────────────────────────────────
        // LightType (+0x1e0) != 0; not (bStatic|bNoDelete, +0x28 & 5) without bDynamicLight (+0x29 & 0x20);
        // LightBrightness (+0x1e2) != 0; LightRadius (+0x1e5) != 0. Radius r = WorldLightRadius, raised to
        // (VolumeRadius+1)·25 (0x10b38690) when that is larger (fcom; C0 -> take it); dropped when below any of the
        // frame's 4 side planes by more than r: ((L.z·P.z + L.y·P.y) + P.x·L.x) − W < −r (test ah,0x41). The item
        // goes to the head of node 0's list (*_DynamicsCache).
        public bool SetupLight(UESceneNode f, UEObject a, int nodeCount)
        {
            int type = UEValue.AsByte(a.GetProp(PK.LightType));
            if (type == 0) return false;
            if ((UEValue.ToBool(a.GetProp(PK.bStatic)) || UEValue.ToBool(a.GetProp(PK.bNoDelete))) && !UEValue.ToBool(a.GetProp(PK.bDynamicLight)))
                return false;
            if (UEValue.AsByte(a.GetProp(PK.LightBrightness)) == 0) return false;
            int radius = UEValue.AsByte(a.GetProp(PK.LightRadius));
            if (radius == 0) return false;
            float drawScale = a.GetProp(PK.DrawScale) is { } ds ? UEValue.AsFloat(ds) : 1f;
            float lr = WorldFrameProjector.WorldLightRadius(drawScale, radius);
            float vr = FogMap.WorldVolumetricRadius(UEValue.AsByte(a.GetProp(PK.VolumeRadius)));   // inlined: fild, fmul 25.0
            float r = lr < vr ? vr : lr;
            Vector3 l = UEValue.AsVector3(a.GetProp(PK.Location));
            for (int k = 0; k < 4; k++)
            {
                (Vector3 n, float pw) = f.ViewPlanes[k];
                float d = ((l.z * n.z + l.y * n.y) + n.x * l.x) - pw;
                if (-r > d) return false;
            }
            // Item +0x14, the volumetric flag (0x10b305df..0x10b30669): the light's zone (Region.Zone +0xa0) is a bFogZone
            // (+0x2cc bit 2) and VolumeRadius (+0x1eb), VolumeBrightness (+0x1ea) are non-zero, and the light is not below
            // any side plane by more than (VolumeRadius+1)·25 (fild, fmul −25.0 at 0x10b38fcc; drop when d < −rv).
            UEObject zone = (a.GetDeclared("Actor", "Region") as UEObject)?.GetProp(PK.Zone) as UEObject;
            int vrad = UEValue.AsByte(a.GetProp(PK.VolumeRadius));
            bool vol = zone is not null && UEValue.ToBool(zone.GetProp(PK.bFogZone)) && vrad != 0
                       && UEValue.AsByte(a.GetProp(PK.VolumeBrightness)) != 0;
            if (vol)
            {
                float rv = FogMap.WorldVolumetricRadius(vrad);
                for (int k = 0; k < 4 && vol; k++)
                {
                    (Vector3 n, float pw) = f.ViewPlanes[k];
                    if (((l.z * n.z + l.y * n.y) + n.x * l.x) - pw < -rv) vol = false;
                }
            }
            EnsureCache(nodeCount);
            LightItems++;
            UEDynItem li = NewItem();
            li.Light = new UEDynLight { ObjectId = a.ObjectId, Pos = l, Radius = lr, SpecialLit = UEValue.ToBool(a.GetProp(PK.bSpecialLit)), VolumeRadius = vrad };
            li.Vol = vol;
            Push(0, li);
            return true;
        }

        // Light items of a DynamicsCache list in list order (checks: SetupLight against the shipped SetupDynamics).
        internal List<int> LightsIn(int slot)
        {
            var r = new List<int>();
            if (slot < _cache.Length) for (UEDynItem it = _cache[slot]; it is not null; it = it.Next) if (it.Light is not null) r.Add(it.Light.ObjectId);
            return r;
        }

        // Check seam (validate_light_filter.py): the given light items pushed to node 0 in order, then every node in
        // pre-order (node, front subtree, back subtree) filters its list-0 light items head first — the same drive the
        // oracle gives the shipped FUN_10b31c70. Returns SurfLights (iSurf -> list indices into `lights`, list order).
        // leaves: LeafLights (iLeaf -> (list index, link +0x14) in list order); vol: the seeds' +0x14 flags (null — all 0).
        internal SortedDictionary<int, int[]> FilterLightsForCheck(HeadlessBspModel m, IReadOnlyList<UEDynLight> lights,
            out SortedDictionary<int, (int Light, bool Vol)[]> leaves, IReadOnlyList<bool> vol = null)
        {
            ClearCache();
            EnsureCache(m.LiveNodeCount);
            for (int i = 0; i < lights.Count; i++) Push(0, new UEDynItem { Light = lights[i], Vol = vol?[i] == true });
            var stack = new Stack<int>();
            stack.Push(0);
            while (stack.Count > 0)
            {
                int n = stack.Pop();
                for (UEDynItem it = _cache[n * 2]; it is not null; it = it.Next) if (it.Light is not null) FilterLight(m, it, n);
                m.RenderNode(n, out _, out _, out _, out _, out _, out _, out int back, out int front, out _, out _, out _, out _, out _, out _);
                if (back != -1) stack.Push(back);
                if (front != -1) stack.Push(front);
            }
            var r = new SortedDictionary<int, int[]>();
            foreach (var kv in _surfLights)
                r[kv.Key] = kv.Value.Select(id => lights.ToList().FindIndex(l => l.ObjectId == id)).ToArray();
            leaves = new SortedDictionary<int, (int, bool)[]>();
            foreach (var kv in _leafLights)
                leaves[kv.Key] = kv.Value.Select(x => (lights.ToList().FindIndex(l => l.ObjectId == x.Id), x.Vol)).ToArray();
            ClearCache();
            return r;
        }

        // ── light item Filter (FUN_10b31c70, disasm 0x10b31c70..0x10b32273) ───────────────────────────────
        // dist = ((L.z·N.z + L.y·N.y) + N.x·L.x) − W stored as float; r = WorldLightRadius; rv = (VolumeRadius+1)·25.
        // Leaf half (0x10b31cfc..0x10b31dc3 front, 0x10b320a3..0x10b321b3 back): on a side the light reaches, an item
        //   without +0x18 whose node has a leaf there (iLeaf[1] +0x3c front, iLeaf[0] +0x38 back) links the light at the
        //   head of LeafLights[leaf] (a new leaf joins DynLightLeaves), the link's +0x14 = item +0x14 && dist > −rv
        //   (front, fmul −25.0 0x10b38fcc) / dist < rv (back, 25.0 0x10b38690); the child copy on that side takes the
        //   same +0x14 and +0x18 = 1 when linked there (else the item's).
        //   −r < dist: a copy goes to the front child's list; dist >= r ends here; else every node of the coplanar
        //              chain (the node, then iPlane links) adds the light to SurfLights[iSurf] when the surface has a
        //              lightmap (UModel::GetLightMapIndex: iSurf != -1, Surf.iLightMap != -1, LightMap.Num != 0), fewer
        //              than 0x800 surfaces carry lights, the surface's PF_SpecialLit (0x100000) equals the light's
        //              bSpecialLit, and the light is not in the list yet (head insertion; a new surface joins DynLightSurfs);
        //   dist < r:  a copy goes to the back child's list.
        void FilterLight(HeadlessBspModel m, UEDynItem it, int iNode)
        {
            LightFilters++;
            UEDynLight L = it.Light;
            m.RenderNode(iNode, out float nx, out float ny, out float nz, out float nw, out _, out _, out int iBack,
                out int iFront, out _, out _, out _, out _, out _, out _);
            float dist = ((L.Pos.z * nz + L.Pos.y * ny) + nx * L.Pos.x) - nw;
            float r = L.Radius;
            m.RenderNodeLeaves(iNode, out int leaf0, out int leaf1);
            float vr1 = L.VolumeRadius + 1;
            if (-r < dist)
            {
                bool frontVol = it.Vol && !(dist <= vr1 * -25f);
                bool leafed = it.Leafed;
                if (!it.Leafed && leaf1 != -1) { LinkLeaf(leaf1, L.ObjectId, frontVol); leafed = true; }
                if (iFront != -1) { UEDynItem c = NewItem(); c.Light = L; c.Vol = frontVol; c.Leafed = leafed; Push(iFront * 2, c); }
                if (!(dist < r)) return;
                for (int n = iNode; n != -1;)
                {
                    m.RenderNode(n, out _, out _, out _, out _, out _, out int iSurf, out _, out _, out int iPlane,
                        out _, out _, out _, out _, out _);
                    if (m.SurfHasLightMapIndex(iSurf) && _surfLights.Count < 0x800
                        && ((m.SurfPolyFlagsOf(iSurf) & 0x100000) != 0) == L.SpecialLit)
                    {
                        if (!_surfLights.TryGetValue(iSurf, out List<int> list)) _surfLights[iSurf] = list = _surfListPool.Count > 0 ? _surfListPool.Pop() : new List<int>();
                        if (!list.Contains(L.ObjectId)) { list.Insert(0, L.ObjectId); SurfLightLinks++; }
                    }
                    n = iPlane;
                }
            }
            if (dist < r)
            {
                bool backVol = it.Vol && !(vr1 * 25f <= dist);
                bool leafed = it.Leafed;
                if (!it.Leafed && leaf0 != -1) { LinkLeaf(leaf0, L.ObjectId, backVol); leafed = true; }
                if (iBack != -1) { UEDynItem c = NewItem(); c.Light = L; c.Vol = backVol; c.Leafed = leafed; Push(iBack * 2, c); }
            }
        }

        void LinkLeaf(int leaf, int id, bool vol)
        {
            if (!_leafLights.TryGetValue(leaf, out var list)) _leafLights[leaf] = list = _leafListPool.Count > 0 ? _leafListPool.Pop() : new List<(int, bool)>();
            list.Insert(0, (id, vol));
        }

        // LeafVolumetricLighting's dynamic half (Render.dll.c:4642-4680): after the leaf's static chain, every link of
        // LeafLights[leaf] with +0x14 joins the walk's volumetric list (URender+0x30) — once per light per frame by the
        // actor's +0xec Stamp, shared with the static chain (the consumer applies it in walk order).
        void LeafDynamics(UESceneNode f, int leaf)
        {
            f.VolumetricLeaves.Add(leaf);
            if (!_leafLights.TryGetValue(leaf, out var list)) return;
            foreach (var (id, vol) in list) if (vol) f.VolumetricDynamic.Add((f.VolumetricLeaves.Count - 1, id));
        }

        // ── FDynamicSprite::FDynamicSprite (0x10b30790) after a successful Setup ───────────────────────────
        // Corners — the screen rectangle at depth Z, back to world via the frame Uncoords; rows Y1..Y2 with [X1, X2);
        // the filter item (0x10b395e4) goes to the head of node 0's list.
        public UESprite CreateSprite(UESceneNode f, UEObject actor, in UERenderVisibility.ScreenBounds sb, int nodeCount)
        {
            EnsureCache(nodeCount);
            UESprite s = NewSprite();
            s.Actor = actor;
            s.X1 = RoundEven(sb.X1); s.Y1 = RoundEven(sb.Y1); s.X2 = RoundEven(sb.X2); s.Y2 = RoundEven(sb.Y2);
            s.Z = sb.Z1; s.Zc = sb.Z2;
            float x1 = s.X1, y1 = s.Y1, x2 = s.X2, y2 = s.Y2;
            float k = f.RProjZ * s.Z;
            float ax = (x1 - f.FX2) * k, bx = (x2 - f.FX2) * k;
            float ay = (y1 - f.FY2) * k, by = (y2 - f.FY2) * k;
            s.P[0] = Unproject(f, ax, ay, s.Z); s.SX[0] = x1; s.SY[0] = y1;
            s.P[1] = Unproject(f, bx, ay, s.Z); s.SX[1] = x2; s.SY[1] = y1;
            s.P[2] = Unproject(f, bx, by, s.Z); s.SX[2] = x2; s.SY[2] = y2;
            s.P[3] = Unproject(f, ax, by, s.Z); s.SX[3] = x1; s.SY[3] = y2;
            if (s.Y1 < 0 || s.Y2 > f.Y || s.Y2 <= s.Y1)
                throw new InvalidOperationException($"FDynamicSprite: assert Y1>=0 && Y2<=Frame->Y && Y1<Y2 (UnSprite.cpp 0xe6-0xe8), {s.Y1}..{s.Y2}");
            UESpanRows rows = NewRows(s.Y1, s.Y2, s.Y2 - s.Y1);
            for (int y = s.Y1; y < s.Y2; y++) rows.Row(y) = (s.X1, s.X2);
            UEDynItem si = NewItem(); si.Sprite = s; si.Rows = rows;
            Push(0, si);
            return s;
        }

        // TransformPointBy(Uncoords) inlined (0x10b307f8..): (v − U.Origin), then ((dz·A.z + dy·A.y) + dx·A.x).
        static Vector3 Unproject(UESceneNode f, float vx, float vy, float vz)
        {
            BrushCoords u = f.Uncoords;
            float dx = vx - u.Origin.x, dy = vy - u.Origin.y, dz = vz - u.Origin.z;
            return new Vector3((dz * u.X.z + dy * u.X.y) + dx * u.X.x,
                               (dz * u.Y.z + dy * u.Y.y) + dx * u.Y.x,
                               (dz * u.Z.z + dy * u.Z.y) + dx * u.Z.x);
        }

        // ── sprite item Filter (FUN_10b31160) ──────────────────────────────────────────────────────────────
        void Filter(HeadlessBspModel m, UEDynItem it, int iNode, int outside)
        {
            Filters++;
            m.RenderNode(iNode, out float nx, out float ny, out float nz, out float nw, out _, out _, out int iBack,
                out int iFront, out _, out _, out _, out _, out int numVerts, out _);
            UESprite s = it.Sprite;
            Span<float> d = stackalloc float[4];
            int front = 0, back = 0;
            for (int k = 0; k < 4; k++)
            {
                Vector3 p = s.P[k];
                d[k] = ((p.x * nx + p.z * nz) + ny * p.y) - nw;           // 0x10b311a1..b5
                if ((double)d[k] > 0.01) front++;                         // fcom qword 0.01; test ah,0x41
                if (!((double)d[k] >= -0.01)) back++;                     // fcomp qword -0.01; test ah,1 (unordered — yes)
            }
            UESpanRows src = it.Rows, fr = src, bk = src;
            if (front != 0 && back != 0)
            {
                int n = 0;
                float ix0 = 0, iy0 = 0, ix1 = 0, iy1 = 0;
                int prev = 3;
                for (int k = 0; k < 4; k++)
                {
                    if (!(d[prev] * d[k] >= 0f))                          // fcomp qword 0.0; test ah,1
                    {
                        float t = d[prev] / (d[prev] - d[k]);
                        float x = (s.SX[k] - s.SX[prev]) * t + s.SX[prev];
                        float y = (s.SY[k] - s.SY[prev]) * t + s.SY[prev];
                        if (n == 0) { ix0 = x; iy0 = y; }
                        else if (n == 1) { ix1 = x; iy1 = y; }
                        else throw new NotSupportedException($"FDynamicSprite Filter: edge intersection #{n + 1} with the plane of node {iNode} — shipped writes past its two-entry buffer");
                        n++;
                    }
                    prev = k;
                }
                if (n >= 2)
                {
                    if (iy0 > iy1) { (ix0, ix1) = (ix1, ix0); (iy0, iy1) = (iy1, iy0); }   // test ah,0x41: only "greater"
                    int yTop = RoundEven(iy0 - 0.5f); if (yTop < src.Y0) yTop = src.Y0;
                    int yBot = RoundEven(iy1 - 0.5f); if (yBot > src.Y1) yBot = src.Y1;
                    if (yTop <= yBot)
                    {
                        Splits++;
                        int cap = src.Y1 - src.Y0;
                        UESpanRows a = NewRows(yTop, yBot, cap);
                        UESpanRows b = NewRows(yTop, yBot, cap);
                        UESpanRows top = null, bot = null;
                        if (yTop > src.Y0) top = d[0] >= 0f ? a : b;
                        if (yBot < src.Y1) bot = d[2] >= 0f ? a : b;
                        UESpanRows left, right;
                        if (!(ix1 >= ix0)) { if (d[0] >= 0f) { left = a; right = b; } else { left = b; right = a; } }
                        else { if (d[1] >= 0f) { left = b; right = a; } else { left = a; right = b; } }
                        // Bounds of both — before writing rows (shipped writes rows by the final Y0).
                        if (top is not null) top.Y0 = src.Y0;
                        if (bot is not null) bot.Y1 = src.Y1;
                        if (top is not null) for (int y = src.Y0; y < yTop; y++) top.Row(y) = src.Row(y);
                        if (bot is not null) for (int y = yBot; y < src.Y1; y++) bot.Row(y) = src.Row(y);
                        if (yBot != yTop)
                        {
                            // 0x10b3150b..0x10b315ea: 16.16 without −0.5 and without pre-step, row starts at yTop + 1.0.
                            float dx = ((ix1 - ix0) * 65536f) / (iy1 - iy0);
                            int step = RoundEven(dx);
                            float xf = ((((float)yTop + 1f) - iy0) * dx) + ix0 * 65536f;
                            int x = RoundEven(xf);
                            for (int y = yTop; y < yBot; y++)
                            {
                                (int X0, int X1) row = src.Row(y);
                                ref var l = ref left.Row(y); ref var r = ref right.Row(y);
                                l = row; r = row;
                                int xs = x >> 16;
                                if (xs < l.X1) l.X1 = xs;
                                if (xs > r.X0) r.X0 = xs;
                                x += step;
                            }
                        }
                        fr = a.Y1 > a.Y0 ? a : null;
                        bk = b.Y1 > b.Y0 ? b : null;
                    }
                }
            }
            m.RenderNodeFlags(iNode, out int nodeFlags);
            bool csg = numVerts != 0 && (nodeFlags & 0x21) == 0;
            if (front != 0 && fr is not null)
            {
                if (iFront == -1) { if (outside != 0 || csg) InsertFinal(iNode * 2, s, fr); }
                else { UEDynItem c = NewItem(); c.Sprite = s; c.Rows = fr; Push(iFront * 2, c); }
            }
            if (back != 0 && bk is not null)
            {
                if (iBack == -1) { if (outside != 0 && !csg) InsertFinal(iNode * 2 + 1, s, bk); }
                else { UEDynItem c = NewItem(); c.Sprite = s; c.Rows = bk; Push(iBack * 2, c); }
            }
        }

        // A final item goes into the leaf list by ascending key: before the first with key <= its key (see header).
        void InsertFinal(int slot, UESprite s, UESpanRows rows)
        {
            UEDynItem it = NewItem(); it.Final = true; it.Sprite = s; it.Rows = rows;
            float key = s.Key;
            UEDynItem prev = null, cur = _cache[slot];
            for (; cur is not null; prev = cur, cur = cur.Next)
                if (cur.Final && key <= cur.Sprite.Key) break;
            if (_cache[slot] is null) _touched.Add(slot);
            it.Next = cur;
            if (prev is null) _cache[slot] = it; else prev.Next = it;
        }

        // ── final item PreRender (0x10b319a0) ───────────────────────────────────────────────────────────────
        void PreRender(UEDynItem it, UESceneNode f, UESpanBuffer zone)
        {
            if (!it.Final) return;                                // filter slot 1 — 0x10b34a40 `ret 0x14`
            PreRenders++;
            UESprite s = it.Sprite;
            UESpanRows r = it.Rows;
            if (s.Span is null)
            {
                UESpanBuffer buf = zone.Sibling();
                buf.AllocIndex(r.Y0, r.Y1);
                s.Span = buf;
                if (!buf.CopyFromRaster(zone, r.Y0, r.Y1, r.R)) { s.Span = null; return; }
                s.VolLeafMark = f.VolumetricLeaves.Count;
                // Frame->Sprite by descending key: advance while the neighbour's key is greater (fcomp; test ah,0x41).
                float key = s.Key;
                int i = 0;
                while (i < f.Sprites.Count && f.Sprites[i].Key > key) i++;
                f.Sprites.Insert(i, s);
                SpritesListed++;
            }
            else
            {
                UESpanBuffer tmp = zone.Sibling();
                tmp.AllocIndex(r.Y0, r.Y1);
                if (tmp.CopyFromRaster(zone, r.Y0, r.Y1, r.R)) { s.Span.MergeWith(tmp); s.VolLeafMark = f.VolumetricLeaves.Count; }
            }
        }

        static int RoundEven(float v)
        {
            double d = Math.Round((double)v, MidpointRounding.ToEven);
            return double.IsNaN(d) || d < int.MinValue || d > int.MaxValue ? int.MinValue : (int)d;
        }

        static ulong Bit(int zone) => zone < 64 ? 1UL << zone : 0UL;   // __allshl(1, zone)

        // ── URender::OccludeBsp ─────────────────────────────────────────────────────────────────────────
        // OccludeFrame (Render.dll.c:684-689): after OccludeBsp every draw record of the frame takes SurfLights[iSurf] as
        // its +0x48 list — here the frame keeps the non-empty lists (UESceneNode.SurfLights).
        public void OccludeBsp(UESceneNode f, UEOcclusionWorld w)
        {
            f.VolumetricLeaves.Clear();
            f.VolumetricDynamic.Clear();
            Walk(f, w);
            f.SurfLights.Clear();
            foreach (var kv in _surfLights) f.SurfLights[kv.Key] = kv.Value.ToArray();
            // DrawFrame after OccludeBsp (Render.dll.c:690-696): with Model->Leaves.Num != 0 every sprite of the frame whose
            // actor has Region.iLeaf != -1 takes LeafLights[iLeaf] (+0xbc).
            if ((w.Bsp?.ParseTables?.Leaves?.Length ?? 0) != 0)
                foreach (UESprite sp in f.Sprites)
                {
                    UEObject region = sp.Actor?.GetDeclared("Actor", "Region") as UEObject;
                    int leaf = region is null ? -1 : UEValue.ToInt(region.GetProp(PK.iLeaf));
                    sp.LeafLights = leaf != -1 && _leafLights.TryGetValue(leaf, out var list) ? list.Select(x => x.Id).ToArray() : Array.Empty<int>();
                }
        }

        // OccludeBsp's iStack_a8 (0x10b226c5..0x10b226e8, Render.dll.c:5536-5543): RenDev->SupportsFogMaps (+0x50; D3DDrv sets 1,
        // D3DDrv.dll.c:278) && RenDev->VolumetricLighting (+0x58, ini) && Viewport->Actor->Region.Zone (+0xa0) bFogZone
        // (+0x2cc bit 2). Set by the producer per tick.
        public bool LeafVolumetricLighting;

        void Walk(UESceneNode f, UEOcclusionWorld w)
        {
            HeadlessBspModel m = w.Bsp;
            int nodeCount = m?.LiveNodeCount ?? 0;
            if (nodeCount > 0x10000) throw new InvalidOperationException("OccludeBsp: assert Frame->Level->Model->Nodes.Num() <= 0x10000");
            if (nodeCount == 0) return;
            Frames++;
            EnsureCache(nodeCount);
            if (f.Parent is null) _arena.Reset();             // a new frame tree: the previous tree's spans are dead
            _spanBased = null;
            _raster.Frame = f;
            _raster.NewStamp();                                   // Stamp++
            Vector3 o = f.Coords.Origin;
            // APlayerPawn::GetViewZone (Engine 0x103796c0) returns the zone as is; IsSurfVisible — true.
            int frameZone = f.Zone & 0xff;
            byte zoneMaskByte = (byte)(frameZone != 0 ? 0xff : 0);
            var active = new List<int> { frameZone };
            ulong activeMask = Bit(frameZone);
            int extraPolyFlags = 0;                               // UViewport::ExtraPolyFlags (+0x90) — 0 in game
            int showMask = (int)(((uint)w.ShowFlags >> 11) | 0xfffffffe);
            m.SetZoneLastRenderTime(frameZone, w.TimeSeconds);
            // Camera in a warp zone with an other side: the whole frame is a child through the warp (0x10b227f0..0x10b22ad5).
            UEObject frameZoneActor = w.ZoneActor(frameZone);
            if (frameZoneActor is not null && !frameZoneActor.Destroyed && UEInterpreter.ClassIsA(frameZoneActor.Class, "WarpZoneInfo")
                && frameZoneActor.GetProp(PK.OtherSideActor) is UEObject && OtherSideLevel(frameZoneActor) is not null)
                throw new NotSupportedException($"OccludeBsp: camera in warp zone {frameZoneActor.GetProp(PK.Name)} with another side — a frame entirely through warp (0x10b227f0..0x10b22ad5) is not ported");

            UESpanBuffer[] zoneSpans = _zoneSpans;
            for (int z = 0; z < 64; z++) { zoneSpans[z] = _arena.Buffer(); zoneSpans[z].AllocIndex(0, 0); }
            // 32-byte copy of the frame FSpanBuffer: shared row-head array and shared nodes.
            UESpanBuffer fs = f.SpanBuffer;
            UESpanBuffer fz = zoneSpans[frameZone];
            fz.StartY = fs.StartY; fz.EndY = fs.EndY; fz.ValidLines = fs.ValidLines; fz.Index = fs.Index;

            // Walk stack: frame — {iNode, iFar, farOutside, outside, flag, prev}.
            var stack = new List<StackFrame> { new StackFrame() };
            int top = 0;
            int iNode = 0;
            int outside = m.RootOutside;
            int mode = 0;                                         // 0 — node entry, 1 — the node itself after the near subtree
            while (true)
            {
                StackFrame sf = stack[top];
                bool process = mode == 1;
                if (mode == 0)
                {
                    NodesEntered++;
                    m.RenderNode(iNode, out float nx, out float ny, out float nz, out float nw, out ulong zmask, out int iSurf,
                        out int iBack, out int iFront, out _, out int renderBound, out _, out _, out int numVerts, out _);
                    if (zoneMaskByte != 0 && (zmask & activeMask) == 0) goto Pop;
                    m.RenderNodeFlags(iNode, out int flags);
                    if (renderBound != -1 && !m.SurfIsDynamic(iSurf)
                        && ((flags & 0x10) != 0 || ((iNode ^ DrawWorldCounter) & 0xf) == 0))
                    {
                        BoxTests++;
                        m.SetRenderNodeFlags(iNode, flags & 0xef);
                        BspBox bx = m.RenderBox(renderBound);
                        bool vis = UERenderVisibility.BoundVisible(f, UERenderVisibility.BoxCoords(bx.Min, bx.Max),
                            zoneMaskByte != 0 ? null : zoneSpans[0], out UERenderVisibility.ScreenBounds sb);
                        if (vis && zoneMaskByte != 0)
                        {
                            int x1 = RoundEven(sb.X1), y1 = RoundEven(sb.Y1), x2 = RoundEven(sb.X2), y2 = RoundEven(sb.Y2);
                            int i = 0;
                            for (; i < active.Count; i++)
                                if ((Bit(active[i]) & zmask) != 0 && zoneSpans[active[i]].BoxIsVisible(x1, y1, x2, y2)) break;
                            vis = i < active.Count;
                        }
                        if (!vis)
                        {
                            BoxCulled++;
                            m.RenderNodeFlags(iNode, out int fl);
                            m.SetRenderNodeFlags(iNode, fl | 0x10);
                            goto Pop;
                        }
                    }
                    // Filter all items of the node (the list may grow as final items are inserted).
                    for (UEDynItem it = _cache[iNode * 2]; it is not null; it = it.Next)
                        if (it.Light is not null) FilterLight(m, it, iNode);
                        else if (!it.Final) Filter(m, it, iNode, outside);
                    m.RenderNodeFlags(iNode, out flags);
                    int side = ((o.z * nz + o.y * ny) + o.x * nx) - nw > 0f ? 1 : 0;
                    bool csg = numVerts != 0 && (flags & 0x21) == 0;
                    int backOut = outside != 0 && !csg ? 1 : 0;   // back child: outside && !csg
                    int frontOut = outside != 0 || csg ? 1 : 0;   // front child: outside || csg
                    sf.Far = side == 1 ? iBack : iFront;
                    sf.FarOutside = side == 1 ? backOut : frontOut;
                    int near = side == 1 ? iFront : iBack;
                    if (near != -1)
                    {
                        sf.Node = iNode; sf.Outside = outside; sf.Flag = 1;
                        top++;
                        if (top == stack.Count) stack.Add(new StackFrame());
                        stack[top].Prev = top - 1;
                        iNode = near;
                        outside = side == 1 ? frontOut : backOut;
                        mode = 0;
                        continue;
                    }
                    process = true;
                }
                if (process)
                {
                    if (!ProcessNode(f, w, m, iNode, outside, zoneSpans, active, ref activeMask, zoneMaskByte,
                        extraPolyFlags, showMask, out bool popNow))
                        break;                                    // all zones emptied — end of walk
                    if (!popNow && sf.Far != -1)
                    {
                        iNode = sf.Far; outside = sf.FarOutside; mode = 0;
                        continue;
                    }
                }
            Pop:
                if (top == 0) break;
                top--;
                sf = stack[top];
                mode = sf.Flag; iNode = sf.Node; outside = sf.Outside;
                if (mode != 0 && mode != 1) goto Pop;
            }
        }

        sealed class StackFrame { public int Node, Far = -1, FarOutside, Outside, Flag, Prev; }

        // Node after the near subtree (0x10b22f85 .. 0x10b24e4a): near leaf PreRender, frustum culling,
        // node and coplanar surfaces, far leaf PreRender. popNow — the branch went to Pop past the far
        // child; false — walk finished (all zones emptied).
        bool ProcessNode(UESceneNode f, UEOcclusionWorld w, HeadlessBspModel m, int iNode, int outside,
            UESpanBuffer[] zoneSpans, List<int> active, ref ulong activeMask, byte zoneMaskByte, int extraPolyFlags,
            int showMask, out bool popNow)
        {
            popNow = true;
            Vector3 o = f.Coords.Origin;
            m.RenderNode(iNode, out float nx, out float ny, out float nz, out float nw, out ulong zmask, out _, out _, out _,
                out int iPlane, out _, out int zone0, out int zone1, out int numVerts, out _);
            if (zoneMaskByte != 0 && (zmask & activeMask) == 0) return true;
            m.RenderNodeFlags(iNode, out int flags);
            float dist = ((o.z * nz + o.y * ny) + o.x * nx) - nw;
            int side = dist > 0f ? 1 : 0;
            int nearZone = (side == 0 ? zone0 : zone1) & zoneMaskByte;
            // 0x10b23011..0x10b2302a: iLeaf[side] (+0x38) to LeafVolumetricLighting when the walk's gate is on.
            if (LeafVolumetricLighting)
            {
                m.RenderNodeLeaves(iNode, out int l0, out int l1);
                int leaf = side == 0 ? l0 : l1;
                if (leaf != -1) LeafDynamics(f, leaf);
            }
            bool csg = numVerts != 0 && (flags & 0x21) == 0;
            if (zoneSpans[nearZone].ValidLines != 0)
            {
                bool leafOutside = side == 1 ? (outside != 0 || csg) : (outside != 0 && !csg);
                if (leafOutside)                                  // LeakCheck (URender+0x34) = 0
                    for (UEDynItem it = _cache[iNode * 2 + 1 - side]; it is not null; it = it.Next)
                        PreRender(it, f, zoneSpans[nearZone]);
            }
            // 0x10b230bc: all four side rays of the frustum are on the viewer's side — nothing beyond the node is visible.
            float sign = side != 0 ? 1f : -1f;
            bool allNear = true;
            for (int k = 0; k < 4 && allNear; k++)
            {
                Vector3 v = f.ViewSides[k];
                if (!(((v.z * nz + v.y * ny) + nx * v.x) * sign > 0f)) allNear = false;   // 0x10b230ce..0x10b23184
            }
            if (allNear) return true;

            int fNode = iNode;
            int sNode = iNode;
            int sSide = side;
            float sDist = dist;
            while (true)
            {
                if (!Surface(f, w, m, sNode, sSide, sDist, zoneSpans, active, ref activeMask, zoneMaskByte, extraPolyFlags, showMask))
                    return false;
                m.RenderNode(sNode, out _, out _, out _, out _, out _, out _, out _, out _, out int nextPlane, out _, out _, out _, out _, out _);
                if (nextPlane == -1) break;
                sNode = nextPlane;
                m.RenderNode(sNode, out float px, out float py, out float pz, out float pw, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _);
                sDist = ((o.z * pz + o.y * py) + o.x * px) - pw;
                sSide = sDist > 0f ? 1 : 0;
            }
            // Far leaf of node fNode (0x10b24d47..0x10b24e3b).
            m.RenderNode(fNode, out nx, out ny, out nz, out nw, out _, out _, out _, out _, out _, out _, out zone0, out zone1, out numVerts, out _);
            m.RenderNodeFlags(fNode, out flags);
            int s2 = ((o.z * nz + o.y * ny) + o.x * nx) - nw > 0f ? 1 : 0;
            int farZone = (s2 == 1 ? zone0 : zone1) & zoneMaskByte;
            // Render.dll.c:6759-6761 (call 0x10b24dae): iLeaf[1 − s2] to LeafVolumetricLighting.
            if (LeafVolumetricLighting)
            {
                m.RenderNodeLeaves(fNode, out int l0, out int l1);
                int leaf = s2 == 1 ? l0 : l1;
                if (leaf != -1) LeafDynamics(f, leaf);
            }
            csg = numVerts != 0 && (flags & 0x21) == 0;
            if (zoneSpans[farZone].ValidLines != 0)
            {
                bool leafOutside = s2 == 1 ? (outside != 0 && !csg) : (outside != 0 || csg);
                if (leafOutside)
                    for (UEDynItem it = _cache[fNode * 2 + s2]; it is not null; it = it.Next)
                        PreRender(it, f, zoneSpans[farZone]);
            }
            popNow = false;
            return true;
        }

        // Node surface (0x10b23190 .. LAB_10b24d1b). false — all zones emptied (LAB_10b22da3).
        bool Surface(UESceneNode f, UEOcclusionWorld w, HeadlessBspModel m, int iNode, int side, float dist,
            UESpanBuffer[] zoneSpans, List<int> active, ref ulong activeMask, byte zoneMaskByte, int extraPolyFlags, int showMask)
        {
            m.RenderNode(iNode, out float nx, out float ny, out float nz, out float nw, out _, out int iSurf, out _, out _,
                out _, out _, out int zone0, out int zone1, out int numVerts, out int vertPool);
            int pf = m.SurfPolyFlagsOf(iSurf) | extraPolyFlags;
            if (!((side != 0 || -1f <= dist || (pf & 0x4000100) != 0) && ((pf & 0x4000000) == 0 || zoneMaskByte != 0)))
                return true;
            int nearZone = (side == 0 ? zone0 : zone1);
            int zone = nearZone & zoneMaskByte;
            UESpanBuffer span = zoneSpans[zone];
            if (!(span.ValidLines > 0)) return true;
            Surfaces++;
            // One scratch array per vertex count (ClipBspSurf reads its Count): allocation only.
            if (_surfVerts.Length <= numVerts) Array.Resize(ref _surfVerts, numVerts + 1);
            int[] idx = _surfVerts[numVerts] ??= new int[numVerts];
            for (int k = 0; k < numVerts; k++) idx[k] = m.RenderVertexPoint(vertPool, k);
            if (!ReferenceEquals(_pointModel, m)) { _pointModel = m; _pointOf = m.Point; }
            List<UETransformPt> pts = _raster.ClipBspSurf(idx, _pointOf);
            if (pts is null || pts.Count == 0) return true;
            bool twoSidedBack = side == 0 && (pf & 0x4000100) != 0;
            if ((BitConverter.SingleToInt32Bits(f.Mirror) == unchecked((int)0xbf800000)) != twoSidedBack) pts.Reverse();
            m.RenderNodeFlags(iNode, out int nflags);
            if (!_raster.Raster(pts, (nflags & 8) != 0 ? span : null, f.Y)) return true;
            pf |= w.TexturePolyFlags(iSurf);
            pf &= showMask;
            int solidBits = pf & 0x47;
            UESpanBuffer ds = span.Sibling();
            ds.AllocIndex(_raster.StartY, _raster.EndY);
            if (_surfRows.Length < _raster.EndY - _raster.StartY) _surfRows = new (int, int)[_raster.EndY - _raster.StartY];
            (int, int)[] rows = _surfRows;
            for (int y = _raster.StartY; y < _raster.EndY; y++) rows[y - _raster.StartY] = _raster.Lines[y];
            bool any = solidBits == 0 || (pf & 0x4000001) == 0x4000001 || (pf & 0x8000000) != 0
                ? ds.CopyFromRasterUpdate(span, _raster.StartY, _raster.EndY, rows)
                : ds.CopyFromRaster(span, _raster.StartY, _raster.EndY, rows);
            if (!any)
            {
                SurfacesOccluded++;
                m.SetRenderNodeFlags(iNode, nflags | 8);
                return true;
            }
            var bounds = new UERenderVisibility.ScreenBounds { X1 = _raster.MinX, Y1 = _raster.StartY, X2 = _raster.MaxX, Y2 = _raster.EndY };
            bool draw;
            if ((pf & 0x80) != 0 && SkyZoneOf(w, zone) is UEObject sky && f.Recursion < 3 && (w.ShowFlags & 0x800) != 0)
            {
                Skies++;
                Tag(CreateChildFrame(f, ds, w.Level, 0, ZoneNumberOf(sky), f.Mirror, f.NearClipN, f.NearClipW, SkyCoords(f, sky), bounds),
                    UESceneNode.KindSky, iSurf);
                f.SkyWindows.Add((iSurf, iNode));   // port (frame export): the window that shows the sky frame
                return true;
            }
            if ((pf & 0x8000000) != 0 && f.Recursion < 3 && (w.ShowFlags & 0x800) != 0)
            {
                if ((pf & 4) == 0 || w.Shiny())
                {
                    Mirrors++;
                    Tag(MirrorFrame(f, ds, w, zone, nx, ny, nz, nw, bounds), UESceneNode.KindMirror, iSurf);
                    if (solidBits == 0 && SpanBased(w)) return true;
                }
                else
                {
                    // A translucent mirror on a device without ShinySurfaces (RenDev +0x5c == 0, 0x10b23a8e..0x10b23ab8): no
                    // child, the draw record takes PolyFlags & ~PF_Translucent | 0x80000000 and pass list 1 (opaque).
                    pf = MirrorWithoutShine(pf);
                    f.Reflagged.Add((iSurf, iNode, pf));
                }
                draw = true;
            }
            else if ((pf & 0x4000000) == 0 || (w.RendMap == 2 && solidBits == 0))
            {
                if ((pf & 1) != 0) return true;
                draw = true;
            }
            else
            {
                Portals++;
                int other = (side == 0 ? zone1 : zone0) & zoneMaskByte;
                bool visible = (pf & 1) == 0;
                if (other != 0 && solidBits != 0)
                {
                    UEObject za = w.ZoneActor(other);
                    bool warped = false;
                    if (za is not null && !za.Destroyed && UEInterpreter.ClassIsA(za.Class, "WarpZoneInfo") && f.Recursion < 3)
                    {
                        if (OtherSideLevel(za) is null || za.GetProp(PK.OtherSideActor) is not UEObject)
                            w.Generate(za);
                        if (OtherSideLevel(za) is object osl && za.GetProp(PK.OtherSideActor) is UEObject osa)
                        {
                            Warps++;
                            Tag(WarpFrame(f, ds, za, osa, osl, iSurf, side, nx, ny, nz, nw), UESceneNode.KindWarp, iSurf);
                            warped = true;
                            if ((pf & 1) != 0 && SpanBased(w)) return true;
                        }
                    }
                    if (!warped)
                    {
                        if ((activeMask & Bit(other)) == 0) { activeMask |= Bit(other); active.Add(other); }
                        zoneSpans[other].MergeWith(ds);
                        m.SetZoneLastRenderTime(other, w.TimeSeconds);
                        if (!visible) return true;
                    }
                }
                else if (!visible) return true;
                draw = true;
            }
            if (!draw) return true;
            // FSavedPoly (Render.dll.c:6602-6616, 6641-6656): only for a device without SpanBased (RenDev +0x48 == 0) — raster points.
            if (!SpanBased(w))
            {
                PolyBank bank = PolyBankNow();
                var view = bank.Take(pts.Count);
                for (int k = 0; k < view.Length; k++) view[k] = new Vector3(pts[k].X, pts[k].Y, pts[k].Z);
                f.DrawnPolys.Add((iSurf, iNode, view, side == 0));
                f.DrawnPolyVolumetrics.Add((f.VolumetricLeaves.Count, EdgePlanes(pts, bank.Take)));
            }
            // DrawSurf registration (0x10b246d0..): of it only clearing bit 0x08 and the empty zone affect visibility.
            m.RenderNodeFlags(iNode, out nflags);
            m.SetRenderNodeFlags(iNode, nflags & 0xf7);
            if (span.ValidLines < 1)
            {
                ZonesEmptied++;
                for (int i = active.Count - 1; i >= 0; i--) if (active[i] == zone) active.RemoveAt(i);   // RemoveAll without a closure
                activeMask &= ~Bit(zone);
                if (active.Count == 0) return false;
            }
            return true;
        }

        // OccludeBsp, PF_Mirrored|PF_Translucent with RenDev ShinySurfaces == 0 (0x10b23aa1..0x10b23ab5): `and ecx, -5;
        // or ecx, 0x80000000`, [ebp-0x54] = 1 (the record goes to pass list +0x9c, drawn with the opaque pass 1).
        public static bool MirrorFallsBack(int pf) => (pf & 0x8000004) == 0x8000004;

        // Edge planes of a registered polygon's eye pyramid (OccludeBsp 0x10b2472e..0x10b24836, x87 PC24): for each point
        // `cur` with its predecessor `prev` (the first pairs with the last), skipped when prev.IntY == -1 (`cmp [ecx+0x18],-1`):
        // n = cur × prev ((prev.z·cur.y − cur.z·prev.y, prev.x·cur.z − cur.x·prev.z, cur.x·prev.y − prev.x·cur.y)), scaled by
        // the GMath rsqrt tables (0x10b41f0c/0x10b4bc1c) of (x² + y²) + z².
        public static Vector3[] EdgePlanes(List<UETransformPt> pts) => EdgePlanes(pts, n => new Vector3[n]);
        // alloc(n): an array of n entries, every one written below.
        static Vector3[] EdgePlanes(List<UETransformPt> pts, Func<int, Vector3[]> alloc)
        {
            int count = 0;
            for (int i = 0; i < pts.Count; i++) if (pts[i == 0 ? pts.Count - 1 : i - 1].IntY != -1) count++;
            var e = alloc(count);
            int j = 0;
            UETransformPt prev = pts[pts.Count - 1];
            foreach (UETransformPt cur in pts)
            {
                if (prev.IntY != -1)
                {
                    float nx = prev.Z * cur.Y - cur.Z * prev.Y;
                    float ny = prev.X * cur.Z - cur.X * prev.Z;
                    float nz = cur.X * prev.Y - prev.X * cur.Y;
                    float k = SurfLightmap.GMathRsqrt((nx * nx + ny * ny) + nz * nz);
                    e[j++] = new Vector3(k * nx, k * ny, k * nz);
                }
                prev = cur;
            }
            return e;
        }

        // The record's per-light test (0x10b2491f..0x10b2496b, 0x10b24c26..0x10b24c74): a light record (frame-axes position P)
        // is dropped when (VolumeRadius+1)·25 < (P.z·n.z + P.y·n.y) + n.x·P.x for any edge plane (fcompp; test ah,1 — also unordered).
        public static bool PassesEdges(Vector3[] edges, Vector3 p, int volumeRadius)
        {
            float r = FogMap.WorldVolumetricRadius(volumeRadius);   // fild (VolumeRadius+1); fmul 25.0 (0x10b38690)
            foreach (Vector3 n in edges)
                if (!(r >= (p.z * n.z + p.y * n.y) + n.x * p.x)) return false;
            return true;
        }
        public static int MirrorWithoutShine(int pf) => (pf & ~4) | unchecked((int)0x80000000);

        // Port (D-61): the branch and surface that spawned the frame (UESceneNode.Kind/SourceSurfs). CreateChildFrame merging
        // frames of different branches (sky and mirror: both iSurf = 0, same NearClip and zone) — not encountered; loud.
        static void Tag(UESceneNode c, int kind, int iSurf)
        {
            if (c.Kind != UESceneNode.KindMaster && c.Kind != kind)
                throw new NotSupportedException($"CreateChildFrame merged frames of different branches ({c.Kind} and {kind}, surface {iSurf})");
            c.Kind = kind;
            if (!c.SourceSurfs.Contains(iSurf)) c.SourceSurfs.Add(iSurf);
        }

        static object OtherSideLevel(UEObject warpZone) => warpZone.GetDeclared("WarpZoneInfo", "OtherSideLevel");

        static UEObject SkyZoneOf(UEOcclusionWorld w, int zone)
        {
            UEObject za = w.ZoneActor(zone) ?? w.LevelInfo
                ?? throw new InvalidOperationException("OccludeBsp: assert Actors(0) (LevelInfo) — the zone has no actor");
            return za.GetDeclared("ZoneInfo", "SkyZone") as UEObject;
        }

        static int ZoneNumberOf(UEObject a)
            => (a.GetDeclared("Actor", "Region") as UEObject)?.GetProp(PK.ZoneNumber) is object z ? UEValue.ToInt(z) & 0xff : 0;

        // ── Sky coordinates (0x10b236a6..0x10b239c0) ────────────────────────────────────────────────────────
        static BrushCoords SkyCoords(UESceneNode f, UEObject sky)
        {
            BrushCoords c = f.Coords;
            c.Origin = new Vector3(c.Origin.x - f.Coords.Origin.x, c.Origin.y - f.Coords.Origin.y, c.Origin.z - f.Coords.Origin.z);
            UERotator r = UEValue.AsRotator(sky.GetProp(PK.Rotation));
            c = DivRotator24(c, r.Pitch, r.Yaw, r.Roll);                      // 0x10b236f3..0x10b23969
            Vector3 l = UEValue.AsVector3(sky.GetProp(PK.Location));
            c.Origin = new Vector3(c.Origin.x + l.x, c.Origin.y + l.y, c.Origin.z + l.z);
            return c;
        }

        // FCoords::operator/=(FRotator) in PC24: roll, pitch, yaw — BrushCoords.DivRotator matrices, product MulAssign24.
        internal static BrushCoords DivRotator24(BrushCoords c, int pitch, int yaw, int roll)
        {
            float sr = BrushCoords.SinAngle(roll), cr = BrushCoords.CosAngle(roll);
            c = MulAssign24(c, new BrushCoords { Origin = Vector3.zero, X = new Vector3(1f, 0f, 0f), Y = new Vector3(0f, cr, sr), Z = new Vector3(0f, -sr, cr) });
            float sp = BrushCoords.SinAngle(pitch), cp = BrushCoords.CosAngle(pitch);
            c = MulAssign24(c, new BrushCoords { Origin = Vector3.zero, X = new Vector3(cp, 0f, -sp), Y = new Vector3(0f, 1f, 0f), Z = new Vector3(sp, 0f, cp) });
            float sy = BrushCoords.SinAngle(yaw), cy = BrushCoords.CosAngle(yaw);
            return MulAssign24(c, new BrushCoords { Origin = Vector3.zero, X = new Vector3(cy, -sy, 0f), Y = new Vector3(sy, cy, 0f), Z = new Vector3(0f, 0f, 1f) });
        }

        // FCoords::operator*=(FCoords) (Core 0x1011d1c0) in PC24: Origin — TransformPointBy, axes — TransformVectorBy.
        internal static BrushCoords MulAssign24(in BrushCoords c, in BrushCoords t) => new()
        {
            Origin = PointBy(c.Origin, t), X = VectorBy(c.X, t), Y = VectorBy(c.Y, t), Z = VectorBy(c.Z, t),
        };

        // FVector::TransformVectorBy (Core 0x1011c6f0) in PC24: ((v.x·A.x + v.y·A.y) + v.z·A.z).
        static Vector3 VectorBy(Vector3 v, in BrushCoords t) => new(
            (v.x * t.X.x + v.y * t.X.y) + v.z * t.X.z,
            (v.x * t.Y.x + v.y * t.Y.y) + v.z * t.Y.z,
            (v.x * t.Z.x + v.y * t.Z.y) + v.z * t.Z.z);

        static Vector3 PointBy(Vector3 v, in BrushCoords t)
            => VectorBy(new Vector3(v.x - t.Origin.x, v.y - t.Origin.y, v.z - t.Origin.z), t);

        // operator| (Core 0x10119190): (b.z·a.z + b.y·a.y) + b.x·a.x, a — this.
        static float Dot(Vector3 a, Vector3 b) => (b.z * a.z + b.y * a.y) + b.x * a.x;

        // ── Mirror (0x10b23abd..0x10b23d44) ────────────────────────────────────────────────────────────────
        UESceneNode MirrorFrame(UESceneNode f, UESpanBuffer ds, UEOcclusionWorld w, int zone, float nx, float ny, float nz, float nw,
            in UERenderVisibility.ScreenBounds bounds)
        {
            BrushCoords c = f.Coords;
            var n = new Vector3(nx, ny, nz);
            Vector3 Reflect(Vector3 v)                            // v − N·(2·(v|N)): operator|, *, − (Core, PC24)
            {
                float d = Dot(v, n); d = d + d;
                return new Vector3(v.x - n.x * d, v.y - n.y * d, v.z - n.z * d);
            }
            Vector3 z = Reflect(c.Z), y = Reflect(c.Y);
            // MirrorByVector (Core 0x1011c7b0): d = (n.y·v.y + n.x·v.x) + v.z·n.z.
            float dx = (n.y * c.X.y + n.x * c.X.x) + c.X.z * n.z; dx = dx + dx;
            var x = new Vector3(c.X.x - dx * n.x, c.X.y - dx * n.y, c.X.z - dx * n.z);
            // MirrorByPlane (Core 0x1011c850): d = ((p.y·v.y + p.x·v.x) + v.z·p.z) − W.
            Vector3 ov = c.Origin;
            float dp = ((n.y * ov.y + n.x * ov.x) + ov.z * n.z) - nw; dp = dp + dp;
            var org = new Vector3(ov.x - dp * n.x, ov.y - dp * n.y, ov.z - dp * n.z);
            var child = new BrushCoords { Origin = org, X = x, Y = y, Z = z };
            // NearClip: node plane in frame axes (0x10b23bd6..0x10b23c87), then all four with sign flipped.
            float cz = (c.Z.z * nz + c.Z.y * ny) + nx * c.Z.x;
            float cy = (c.Y.z * nz + c.Y.y * ny) + nx * c.Y.x;
            float cx = (c.X.z * nz + c.X.y * ny) + nx * c.X.x;
            var nv = new Vector3(cx, cy, cz);
            Vector3 to = VectorBy(c.Origin, c);
            float cw = nw - Dot(to, nv);
            return CreateChildFrame(f, ds, w.Level, 0, zone, -f.Mirror, new Vector3(-cx, -cy, -cz), -cw, child, bounds);
        }

        // ── Warp portal (0x10b23eb8..0x10b24436) ────────────────────────────────────────────────────────────
        UESceneNode WarpFrame(UESceneNode f, UESpanBuffer ds, UEObject za, UEObject osa, object osl, int iSurf, int side,
            float nx, float ny, float nz, float nw)
        {
            float px = nx, py = ny, pz = nz, pw = nw;
            if (side != 0) { px = -px; py = -py; pz = -pz; pw = -pw; }
            BrushCoords owc = WarpCoordsOf(osa), wc = WarpCoordsOf(za);
            // Transpose(OtherSide.WarpCoords): −(O·axes) (TransformVectorBy order), axes transposed.
            Vector3 t = VectorBy(owc.Origin, owc);
            var oti = new BrushCoords
            {
                Origin = new Vector3(-t.x, -t.y, -t.z),
                X = new Vector3(owc.X.x, owc.Y.x, owc.Z.x),
                Y = new Vector3(owc.X.y, owc.Y.y, owc.Z.y),
                Z = new Vector3(owc.X.z, owc.Y.z, owc.Z.z),
            };
            BrushCoords c = f.Coords;
            var c1 = new BrushCoords { Origin = PointBy(c.Origin, wc), X = VectorBy(c.X, wc), Y = VectorBy(c.Y, wc), Z = VectorBy(c.Z, wc) };
            var child = new BrushCoords { Origin = PointBy(c1.Origin, oti), X = VectorBy(c1.X, oti), Y = VectorBy(c1.Y, oti), Z = VectorBy(c1.Z, oti) };
            // NearClip (0x10b24336..0x10b243dc): normal in frame axes, order (A.y·p.y + A.z·p.z) + p.x·A.x.
            float cz = (c.Z.y * py + c.Z.z * pz) + c.Z.x * px;
            float cy = (c.Y.y * py + c.Y.z * pz) + px * c.Y.x;
            float cx = (c.X.y * py + c.X.z * pz) + px * c.X.x;
            var nv = new Vector3(cx, cy, cz);
            Vector3 to = VectorBy(c.Origin, c);
            float cw = pw - Dot(to, nv);
            int zone = UEValue.ToInt(osa.GetProp(PK.iWarpZone)) & 0xff;
            return CreateChildFrame(f, ds, osl, iSurf, zone, f.Mirror, nv, cw, child, null);
        }

        static BrushCoords WarpCoordsOf(UEObject wz)
        {
            var wcs = wz.GetProp(PK.WarpCoords) as UEObject
                ?? throw new InvalidOperationException($"OccludeBsp: warp zone {wz.GetProp(PK.Name)} has no WarpCoords");
            return new BrushCoords
            {
                Origin = UEValue.AsVector3(wcs.GetProp(PK.Origin)), X = UEValue.AsVector3(wcs.GetProp(PK.XAxis)),
                Y = UEValue.AsVector3(wcs.GetProp(PK.YAxis)), Z = UEValue.AsVector3(wcs.GetProp(PK.ZAxis)),
            };
        }

        // ── URender::CreateChildFrame (FUN_10b20790) ────────────────────────────────────────────────────
        // A child with the same Level, iSurf, parent, NearClip and zone is the same frame: span buffers merge, Prj
        // expands by screen bounds; otherwise a new frame (copy of the parent, own Coords/Uncoords, ComputeRenderSize
        // in PC24, Prj by bounds, span = AllocIndex(0,0) + MergeWith) — at the head of the children list.
        public static UESceneNode CreateChildFrame(UESceneNode parent, UESpanBuffer span, object level, int iSurf, int zone,
            float mirror, Vector3 clipN, float clipW, in BrushCoords coords, UERenderVisibility.ScreenBounds? bounds)
        {
            foreach (UESceneNode c in parent.Children)
            {
                if (!ReferenceEquals(c.Level, level) || c.ISurf != iSurf || c.NearClipN.x != clipN.x || c.NearClipN.y != clipN.y
                    || c.NearClipN.z != clipN.z || c.NearClipW != clipW || c.Zone != zone) continue;
                c.SpanBuffer.MergeWith(span);
                if (bounds is UERenderVisibility.ScreenBounds b)
                {
                    float v = (b.X1 - c.FX2) * -c.RProjZ; if (!(v <= c.PrjXM)) c.PrjXM = v;
                    v = (b.X2 - c.FX2) * c.RProjZ; if (!(v <= c.PrjXP)) c.PrjXP = v;
                    v = (b.Y1 - c.FY2) * -c.RProjZ; if (!(v <= c.PrjYM)) c.PrjYM = v;
                    v = (b.Y2 - c.FY2) * c.RProjZ; if (!(v <= c.PrjYP)) c.PrjYP = v;
                }
                return c;
            }
            UESceneNode f = parent.CreateChild(coords, level, iSurf, zone, mirror, clipN, clipW);
            if (bounds is UERenderVisibility.ScreenBounds bb)
            {
                f.PrjXM = (bb.X1 - f.FX2) * -f.RProjZ;
                f.PrjXP = (bb.X2 - f.FX2) * f.RProjZ;
                f.PrjYM = (bb.Y1 - f.FY2) * -f.RProjZ;
                f.PrjYP = (bb.Y2 - f.FY2) * f.RProjZ;
            }
            f.SpanBuffer = span.Sibling();
            f.SpanBuffer.AllocIndex(0, 0);
            f.SpanBuffer.MergeWith(span);
            return f;
        }
    }

    // Occlusion judge (D-56): the frame tree, node bits and zone LastRenderTime as lines — the same format is written
    // by the Render.dll translation (RenderLevelPass.FrameTree); comparison — tools/frame_tree_compare.py.
    public static class UEOcclusionDump
    {
        static string H(params float[] v)
        {
            var sb = new System.Text.StringBuilder();
            foreach (float f in v) sb.Append(BitConverter.SingleToInt32Bits(f).ToString("x8"));
            return sb.ToString();
        }
        static float[] C(in BrushCoords c) => new[] { c.Origin.x, c.Origin.y, c.Origin.z, c.X.x, c.X.y, c.X.z, c.Y.x, c.Y.y, c.Y.z, c.Z.x, c.Z.y, c.Z.z };

        public static List<string> Tree(UESceneNode master, HeadlessBspModel m)
        {
            var o = new List<string>();
            void Frame(UESceneNode f, string path)
            {
                var cu = new List<float>(C(f.Coords)); cu.AddRange(C(f.Uncoords));
                var vs = new List<float>();
                foreach (Vector3 v in f.ViewSides) { vs.Add(v.x); vs.Add(v.y); vs.Add(v.z); }
                o.Add($"F {path} r{f.Recursion} z{f.Zone} s{f.ISurf} m{BitConverter.SingleToInt32Bits(f.Mirror):x8} c{H(cu.ToArray())} "
                    + $"p{H(f.PrjXM, f.PrjYM, f.PrjXP, f.PrjYP)} n{H(f.NearClipN.x, f.NearClipN.y, f.NearClipN.z, f.NearClipW)} v{H(vs.ToArray())}");
                var sp = new System.Text.StringBuilder($"S {path}");
                foreach (UESprite s in f.Sprites)
                {
                    int dt = UEValue.ToInt(s.Actor.GetProp(PK.DrawType));
                    if (dt is not (1 or 2 or 7 or 8)) continue;
                    sp.Append(' ').Append(s.Actor.ObjectId).Append(':').Append(s.Actor.GetProp(PK.Name))
                        .Append('@').Append(s.X1).Append(',').Append(s.Y1).Append(',').Append(s.X2).Append(',').Append(s.Y2)
                        .Append(',').Append(BitConverter.SingleToInt32Bits(s.Z).ToString("x8")).Append(',').Append(BitConverter.SingleToInt32Bits(s.Zc).ToString("x8"));
                }
                o.Add(sp.ToString());
                for (int k = 0; k < f.Children.Count; k++) Frame(f.Children[k], path + "." + k);
            }
            Frame(master, "0");
            int num = m.LiveNodeCount;
            ulong h = 0xcbf29ce484222325UL;
            for (int i = 0; i < num; i++) { m.RenderNodeFlags(i, out int fl); h = (h ^ (ulong)(fl & 0x18)) * 0x100000001b3UL; }
            o.Add($"N {num} {h:x16}");
            var b = new System.Text.StringBuilder("B");
            for (int i = 0; i < num; i++) { m.RenderNodeFlags(i, out int fl); if ((fl & 0x18) != 0) b.Append(' ').Append(i).Append(':').Append(fl & 0x18); }
            o.Add(b.ToString());
            var z = new System.Text.StringBuilder("Z");
            for (int i = 0; i < 64; i++) z.Append(' ').Append(BitConverter.SingleToInt32Bits(m.ZoneLastRenderTime[i]).ToString("x8"));
            o.Add(z.ToString());
            return o;
        }
    }

    public sealed partial class HeadlessBspModel
    {
        // FBspNode.ZoneMask (+0x10): map nodes — from the package; tracker fragments — OR of their children's zones (AddPolyFragment).
        private ulong[] _zoneMask;
        // FZoneProperties.LastRenderTime (Model->Zones[z] +0x108): written by OccludeBsp (0x10b227ff, 0x10b2468d).
        public readonly float[] ZoneLastRenderTime = new float[64];

        ulong[] ZoneMasks()
        {
            if (_zoneMask is null)
            {
                _zoneMask = new ulong[_nodes.Length];
                ulong[] src = ParseTables?.NodeZoneMask ?? throw new InvalidOperationException("OccludeBsp: the model has no node ZoneMask");
                Array.Copy(src, _zoneMask, Math.Min(src.Length, LoadedNodeCount));
            }
            else if (_zoneMask.Length < _nodes.Length) Array.Resize(ref _zoneMask, _nodes.Length);
            return _zoneMask;
        }

        internal void OrZoneMask(int node, ulong bits) => ZoneMasks()[node] |= bits;
        internal void SetZoneMask(int node, ulong bits) => ZoneMasks()[node] = bits;
        internal ulong ZoneMaskOf(int node) => ZoneMasks()[node];
        internal static ulong Shl64(int n) => n < 64 ? 1UL << n : 0UL;
        internal void SetZoneLastRenderTime(int zone, float t) { if (zone >= 0 && zone < 64) ZoneLastRenderTime[zone] = t; }

        // FBspNode fields read by the walk (plane as float, as in shipped memory).
        internal void RenderNode(int i, out float nx, out float ny, out float nz, out float w, out ulong zoneMask, out int surf,
            out int back, out int front, out int iPlane, out int renderBound, out int zone0, out int zone1, out int numVerts, out int vertPool)
        {
            Plane p = _planes[i]; Node n = _nodes[i];
            nx = (float)p.X; ny = (float)p.Y; nz = (float)p.Z; w = (float)p.W;
            zoneMask = ZoneMasks()[i];
            surf = n.Surf; back = n.Back; front = n.Front; iPlane = n.Plane;
            // iRenderBound (+0x30): for a map node — from the package; for a tracker fragment it is a brush chain link, but the
            // walk never reaches it — a fragment's surface is dynamic (SurfIsDynamic).
            renderBound = i < LoadedNodeCount ? ParseTables.NodeRenderBound[i] : n.Link;
            zone0 = n.Zone0; zone1 = n.Zone1; numVerts = n.NumVertices & 0xff; vertPool = n.VertPool;
        }

        internal void RenderNodeFlags(int i, out int flags) => flags = _nodes[i].NodeFlags & 0xff;
        internal void RenderNodeLeaves(int i, out int leaf0, out int leaf1) { leaf0 = _nodes[i].Leaf0; leaf1 = _nodes[i].Leaf1; }
        internal void SetRenderNodeFlags(int i, int flags) => _nodes[i].NodeFlags = flags & 0xff;
        internal int RenderVertexPoint(int vertPool, int k) => _vertices[vertPool + k];
        internal int SurfPolyFlagsOf(int surf) => surf >= 0 && surf < _surfFlags.Length ? _surfFlags[surf] : 0;
        // UModel::GetLightMapIndex != NULL (Engine.dll.c:153191): iSurf != -1, Surfs[iSurf].iLightMap != -1, LightMap.Num != 0.
        internal bool SurfHasLightMapIndex(int surf)
            => surf != -1 && _surfRec[surf].LightMap != -1 && ParseTables.LightMaps.Length != 0;
        internal BspBox RenderBox(int i) => ParseTables.Bounds[i];
        // FMovingBrushTracker::SurfIsDynamic (Engine 0x1038cb50): iSurf >= number of surfaces before brushes.
        internal bool SurfIsDynamic(int surf) => Tracker is not null && surf >= Tracker.Surfs0;
    }
}
