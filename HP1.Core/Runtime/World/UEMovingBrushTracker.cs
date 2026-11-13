// FMovingBrushTracker: mover brush fragments in the level model (Engine.dll, UnDynBsp.cpp).
//
// In shipped, every mover's brush polygons are INSERTED into the level BSP (Level->Model->Nodes):
// world traces, point checks and FastLineCheck see the mover as level geometry, and
// ULevel::MultiLineCheck suppresses the "world vs mover" duplicate via the coincident-faces branch
// (UELevelRuntime.MultiLineCheck). Tracker bodies ported here from the Engine.dll disassembly:
//   constructor     FUN_1038cb80 (0x1038cb80)  - model counters, SetupActorBrush, ExpandNodes/
//                                                ExpandVerts, NodeMaps/VertUsed, Update(0);
//   destructor      FUN_1038cff0               - truncates tables to the constructor counters,
//                                                node links beyond them = INDEX_NONE;
//   SetupActorBrush FUN_1038d500               - surface/vectors/point per FPoly;
//   Update          FUN_1038c260               - brush list, ForceGroupFlush, Flush, insertion;
//   ForceGroupFlush FUN_1038e4a0, FlushActorBrush FUN_1038e680;
//   FilterPoly      FUN_1038e000 (0x1038e000-0x1038e3ab), AddPolyFragment FUN_1038da40
//                   (0x1038da40-0x1038dec7), NewThang FUN_1038f240, ExpandDb FUN_1038ee80/efc0;
//   and the Engine/Core bodies the tracker calls: ABrush::BuildCoords (Engine.dll.c:49283),
//   FPoly::Transform (0x10396770), FPoly::SplitWithPlaneFast (0x10395ee0), FPoly::SplitInHalf
//   (Engine.dll.c:112274), UModel::PrecomputeSphereFilter (0x1042c950/0x1042c890),
//   FSphere(FVector*,int) (Core 0x1014ee40), FVector::SafeNormal (Core 0x1014f4e0).
//
// x87 PRECISION IS SET BY THE CALLER. The constructor (LoadMap, SaveGame) runs in game logic:
// PC64; Update(mover) is called by the renderer (URender::SetupDynamics, Render 0x10b30501) inside the
// appEnableFastMath(1) window: PC24. Hence arithmetic is generic over the mode: the mode is the register
// value type, and every x87 register operation rounds its result (PC24: to float; PC64: at 64 bits, exact
// HP1.Core.X87Ext — a double register would round at 53 and differs, e.g. AddPolyFragment W); a store to a float
// cell is fstp dword (.F).
//
// Node ZoneMask: AddPolyFragment copies the anchor mask into the fragment (0x1038db4e) and ORs the fragment zones
// into the parent mask (0x1038dc75-0x1038dcb7); frame occlusion reads it (UEOcclusion, URender::OccludeBsp).
// NOT MODELLED (no reader in Core): fragment vertex iSide (-1).
using System;
using System.Collections.Generic;
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{

// Caller's x87 precision mode as the register value. L is a load (fld of a float or a float constant: exact),
// every operator one register operation with its rounding, SqrtQ appSqrt of the register (fstp qword, fsqrt),
// F fstp dword, Cmp the sign of (register − c) (fcom with a constant).
public interface IFpuMode<V> where V : struct, IFpuMode<V>
{
    static abstract V L(double v);
    static abstract V operator +(V a, V b);
    static abstract V operator -(V a, V b);
    static abstract V operator *(V a, V b);
    static abstract V operator /(V a, V b);
    static abstract V SqrtQ(V v);
    float F { get; }
    int Cmp(double c);
}

// PC64 (game logic, appEnableFastMath(0)): every operation rounds to nearest at 64 bits (X87Ext carries the IEEE sign of
// a zero: x + (−x) = +0, ±0 products and quotients by sign, as the CPU gives it).
public readonly struct FpuPc64 : IFpuMode<FpuPc64>
{
    readonly HP1.Core.X87Ext _v;
    FpuPc64(HP1.Core.X87Ext v) { _v = v; }
    public static FpuPc64 L(double v) => new(HP1.Core.X87Ext.From(v));
    public static FpuPc64 operator +(FpuPc64 a, FpuPc64 b) => new(HP1.Core.X87Ext.Round64(a._v + b._v));
    public static FpuPc64 operator -(FpuPc64 a, FpuPc64 b) => new(HP1.Core.X87Ext.Round64(a._v - b._v));
    public static FpuPc64 operator *(FpuPc64 a, FpuPc64 b) => new(HP1.Core.X87Ext.Round64(a._v * b._v));
    public static FpuPc64 operator /(FpuPc64 a, FpuPc64 b) => new(HP1.Core.X87Ext.Div64(a._v, b._v));
    public static FpuPc64 SqrtQ(FpuPc64 v) => new(HP1.Core.X87Ext.Sqrt64(HP1.Core.X87Ext.From(v._v.ToDouble())));
    public float F => _v.ToFloat();
    public int Cmp(double c) => (_v - HP1.Core.X87Ext.From(c)).Sign;
}

// PC24 (render window, appEnableFastMath(1)): every operation rounds to float; double carries the operands
// exactly, so one rounding of the double result is the x87 result (double >= 2·24+2 bits).
public readonly struct FpuPc24 : IFpuMode<FpuPc24>
{
    readonly double _v;
    FpuPc24(double v) => _v = v;
    public static FpuPc24 L(double v) => new(v);
    public static FpuPc24 operator +(FpuPc24 a, FpuPc24 b) => new((float)(a._v + b._v));
    public static FpuPc24 operator -(FpuPc24 a, FpuPc24 b) => new((float)(a._v - b._v));
    public static FpuPc24 operator *(FpuPc24 a, FpuPc24 b) => new((float)(a._v * b._v));
    public static FpuPc24 operator /(FpuPc24 a, FpuPc24 b) => new((float)(a._v / b._v));
    public static FpuPc24 SqrtQ(FpuPc24 v) => new((float)Math.Sqrt(v._v));
    public float F => (float)_v;
    public int Cmp(double c) => _v > c ? 1 : _v < c ? -1 : _v == c ? 0
        : throw new ArithmeticException("tracker: NaN register in a compare (x87 unordered) is not modelled");
}

public sealed partial class HeadlessBspModel
{
    // ── Tables the tracker grows (UModel: Surfs +0x98, Vectors +0x78, Points +0x88,
    // Verts +0x68, Nodes +0x58). Nodes and verts grow once (constructor), points one at a time
    // (NewThang), so points keep capacity separate from count.
    private int _pointNum;
    private BspSurfRecord[] _surfRec;
    private Vector3[] _vectors;
    // The model as loaded (before the first tracker): read by the render mirror (Raw*) and
    // static geometry, which need the file, not the live tree with fragments.
    private Node[] _loadedNodes;
    private Plane[] _loadedPlanes;
    private Vector3[] _loadedPoints;
    private int[] _loadedVertices;
    public int LoadedNodeCount { get; private set; }
    public int LiveNodeCount => _nodes.Length;
    public int LivePointCount => _pointNum;
    public MovingBrushTracker Tracker { get; private set; }

    public BspSurfRecord Surf(int i) => _surfRec[i];
    public int SurfCount => _surfRec.Length;
    public Vector3 Vector(int i) => _vectors[i];

    // Live tables: for checking the tracker against the shipped translation.
    public void LiveNode(int i, out float px, out float py, out float pz, out float pw,
        out int front, out int back, out int collision, out int surf, out int zone0, out int zone1,
        out int leaf0, out int leaf1, out int iPlane, out int vertPool, out int numVertices, out int nodeFlags, out int link)
    {
        Plane p = _planes[i]; Node n = _nodes[i];
        px = (float)p.X; py = (float)p.Y; pz = (float)p.Z; pw = (float)p.W;
        front = n.Front; back = n.Back; collision = n.Collision; surf = n.Surf;
        zone0 = n.Zone0; zone1 = n.Zone1; leaf0 = n.Leaf0; leaf1 = n.Leaf1;
        iPlane = n.Plane; vertPool = n.VertPool; numVertices = n.NumVertices; nodeFlags = n.NodeFlags; link = n.Link;
    }
    public Vector3 LivePoint(int i) => _points[i];
    public int LiveVertex(int i) => _vertices[i];
    public int LiveVertexCount => _vertices.Length;
    public int VectorCount => _vectors.Length;

    // Live tracker fragments with the given iSurf: the polygons shipped renders for the
    // tracker surface (FSavedPoly in DrawDecals). Order is by node index (draw order is not
    // modelled here; it does not affect fragment geometry).
    public IReadOnlyList<int> TrackerNodesOfSurf(int surf)
    {
        var list = new List<int>();
        if (Tracker is null) return list;
        for (int i = LoadedNodeCount; i < _nodes.Length; i++)
            if (Tracker.NodeOwner(i) is not null && _nodes[i].Surf == surf) list.Add(i);
        return list;
    }

    void InitRuntimeTables()
    {
        _pointNum = _points.Length;
        _surfRec = ParseTables.Surfs;
        _vectors = ParseTables.Vectors;
        LoadedNodeCount = _nodes.Length;
    }

    void SnapshotLoaded()
    {
        if (_loadedNodes is not null) return;
        _loadedNodes = (Node[])_nodes.Clone();
        _loadedPlanes = (Plane[])_planes.Clone();
        _loadedPoints = new Vector3[_pointNum];
        Array.Copy(_points, _loadedPoints, _pointNum);
        _loadedVertices = (int[])_vertices.Clone();
    }

    Node[] StaticNodes => _loadedNodes ?? _nodes;
    Plane[] StaticPlanes => _loadedPlanes ?? _planes;
    Vector3[] StaticPoints => _loadedPoints ?? _points;
    int[] StaticVertices => _loadedVertices ?? _vertices;

    // TArray::Add(1) with zeroing: index = previous count.
    int AddPoint(Vector3 v)
    {
        if (_pointNum == _points.Length) Array.Resize(ref _points, Math.Max(16, _points.Length * 2));
        _points[_pointNum] = v;
        return _pointNum++;
    }

    int AddVector(Vector3 v)
    {
        Array.Resize(ref _vectors, _vectors.Length + 1);
        _vectors[^1] = v;
        return _vectors.Length - 1;
    }

    // Brush model state that shipped keeps IN the brush model itself: iLink of each
    // FPoly (Brush->Polys, +0x104) and the head of the node chain (Brush +0xf8). One model per brush
    // (Actor.Brush): two actors sharing a brush share it, as in shipped.
    public sealed class TrackedBrushModel
    {
        public readonly PolyRecord[] Polys;
        public readonly int[] Link;
        public int FirstNode = -1;
        public TrackedBrushModel(IReadOnlyList<PolyRecord> polys)
        {
            Polys = new PolyRecord[polys.Count];
            for (int i = 0; i < Polys.Length; i++) Polys[i] = polys[i];
            Link = new int[Polys.Length];
        }
    }

    // What the tracker reads and writes on the brush actor (via the level runtime).
    public interface ITrackerHost
    {
        // Brush is tracked: Brush != None, IsA Brush, not bStatic (Engine.dll.c:237548-237553).
        bool IsTrackedBrush(object actor);
        bool GetAssimilated(object actor);                // Actor.bAssimilated (+0x28 bit 0x400)
        void SetAssimilated(object actor, bool value);
        void SaveCurrent(object actor);                   // SavedPos/SavedRot = Location/Rotation
        bool Moved(object actor);                         // SavedPos != Location || SavedRot != Rotation
        TrackedBrushModel ModelOf(object actor);
        BrushPlacement PlacementOf(object actor);
    }

    // ABrush fields from which BuildCoords and FPoly::Transform build the world polygon.
    public readonly struct BrushPlacement
    {
        public readonly Vector3 Location, PrePivot, MainScale, PostScale;
        public readonly float MainSheer, PostSheer;
        public readonly int Pitch, Yaw, Roll;
        public BrushPlacement(Vector3 location, int pitch, int yaw, int roll, Vector3 prePivot,
            Vector3 mainScale, float mainSheer, Vector3 postScale, float postSheer)
        {
            Location = location; Pitch = pitch; Yaw = yaw; Roll = roll; PrePivot = prePivot;
            MainScale = mainScale; MainSheer = mainSheer; PostScale = postScale; PostSheer = postSheer;
        }
    }

    public MovingBrushTracker CreateTracker()
    {
        if (Tracker is not null) throw new InvalidOperationException("GNewBrushTracker: model tracker already exists");
        SnapshotLoaded();
        return Tracker = new MovingBrushTracker(this);
    }

    // ~FMovingBrushTracker (FUN_1038cff0): Surfs/Points/Vectors/Nodes/Verts are truncated to the
    // counters captured by the constructor; remaining nodes with iFront/iBack/iPlane >= Num get INDEX_NONE.
    public void DeleteTracker()
    {
        MovingBrushTracker t = Tracker;
        if (t is null) return;
        Tracker = null;
        Array.Resize(ref _surfRec, t.Surfs0);
        Array.Resize(ref _surfFlags, t.Surfs0);
        Array.Resize(ref _surfMaterial, t.Surfs0);
        _pointNum = t.Points0;
        Array.Resize(ref _vectors, t.Vectors0);
        Array.Resize(ref _nodes, t.Nodes0);
        Array.Resize(ref _planes, t.Nodes0);
        if (_zoneMask is not null) Array.Resize(ref _zoneMask, t.Nodes0);
        Array.Resize(ref _vertices, t.Verts0);
        int num = _nodes.Length;
        for (int i = 0; i < num; i++)
        {
            ref Node n = ref _nodes[i];
            if (n.Front >= num) n.Front = -1;
            if (n.Back >= num) n.Back = -1;
            if (n.Plane >= num) n.Plane = -1;
        }
    }

    // Tracker's working FPoly (0x118 bytes in shipped): the fields read by the bodies above.
    public sealed class WorkPoly
    {
        public float Bx, By, Bz, Nx, Ny, Nz, Ux, Uy, Uz, Vx, Vy, Vz;
        public readonly float[] X = new float[20], Y = new float[20], Z = new float[20];
        public int N, PolyFlags;
        public void CopyFrom(WorkPoly o)
        {
            Bx = o.Bx; By = o.By; Bz = o.Bz; Nx = o.Nx; Ny = o.Ny; Nz = o.Nz;
            Ux = o.Ux; Uy = o.Uy; Uz = o.Uz; Vx = o.Vx; Vy = o.Vy; Vz = o.Vz;
            Array.Copy(o.X, X, 20); Array.Copy(o.Y, Y, 20); Array.Copy(o.Z, Z, 20);
            N = o.N; PolyFlags = o.PolyFlags;
        }
    }

    // UModel::PrecomputeSphereFilter (0x1042c950, recursion 0x1042c890): every visited node
    // clears NF_IsFront|NF_IsBack (0xc0), then by sphere side: fully behind -> 0x80 and
    // iBack, fully in front -> 0x40 and iFront, otherwise recurse into iBack and iFront. The summation
    // order differs between the entry and the recursion (disassembly).
    void PrecomputeSphereFilter<F>(float sx, float sy, float sz, float r) where F : struct, IFpuMode<F>
    {
        if (_nodes.Length == 0) return;
        SphereWalk<F>(0, sx, sy, sz, r, true);
    }

    void SphereWalk<F>(int i, float sx, float sy, float sz, float r, bool top) where F : struct, IFpuMode<F>
    {
        do
        {
            ref Node n = ref _nodes[i];
            n.NodeFlags &= 0x3f;
            Plane pl = _planes[i];
            float px = (float)pl.X, py = (float)pl.Y, pz = (float)pl.Z, pw = (float)pl.W;
            F d = top
                ? ((F.L(pz) * F.L(sz) + F.L(py) * F.L(sy)) + F.L(sx) * F.L(px)) - F.L(pw)
                : ((F.L(py) * F.L(sy) + F.L(pz) * F.L(sz)) + F.L(px) * F.L(sx)) - F.L(pw);
            if (d.Cmp(-(double)r) < 0) { n.NodeFlags |= 0x80; i = n.Back; }
            else if (d.Cmp(r) > 0) { n.NodeFlags |= 0x40; i = n.Front; }
            else
            {
                if (n.Back != -1) SphereWalk<F>(n.Back, sx, sy, sz, r, false);
                i = n.Front;
            }
        } while (i != -1);
    }

    public sealed class MovingBrushTracker
    {
        readonly HeadlessBspModel M;
        // Model counters on constructor entry: [8] Surfs, [9] Vectors, [10] Points, [0xb] Nodes,
        // [0xc] Verts (FUN_1038cb80, Engine.dll.c:237827-237834).
        internal readonly int Surfs0, Vectors0, Points0, Nodes0, Verts0;
        int _firstMaster;            // [7]  Points.Num after SetupActorBrush (NewThang base)
        int _nodeCursor;             // [5]  +0x14
        int _vertCursor;             // [6]  +0x18
        int _freePoint = -1;         // [0x10] +0x40
        object[] _nodeMapBrush;      // [0x14] NodeMaps: (brush, iParent) per node beyond Nodes0
        int[] _nodeMapParent;
        object[] _vertUsed;          // [0x17] VertUsed: brush per vertex beyond Verts0
        readonly List<int> _pointNext = new();  // [0x11] NewThang helper (next free)
        object _curBrush;            // +0x34
        float _curNx, _curNy, _curNz;// +0x08..+0x10 - current FPoly normal
        TrackedBrushModel _tailModel;// +0x38: chain tail - the brush head...
        int _tailNode = -1;          //        ...or iRenderBound of the last node
        bool _ready;

        public int Updates { get; private set; }   // port: Update calls so far (readers cache on it; no shipped field)
        public object NodeOwner(int node) => node >= Nodes0 && node - Nodes0 < _nodeMapBrush.Length ? _nodeMapBrush[node - Nodes0] : null;

        internal MovingBrushTracker(HeadlessBspModel m)
        {
            M = m;
            Surfs0 = m._surfRec.Length;
            Vectors0 = m._vectors.Length;
            Points0 = m._pointNum;
            Nodes0 = m._nodes.Length;
            Verts0 = m._vertices.Length;
        }

        // SetupActorBrush (FUN_1038d500, Engine.dll.c:238149-238304): per brush FPoly -
        // a surface (Surfs.Add, zeroed), vNormal/vTextureU/vTextureV (Vectors.Add) and pBase
        // (Points.Add); surface: Texture = FPoly.Texture, PolyFlags = FPoly.PolyFlags &
        // 0x3cffffff | (bSpecialLit ? PF_SpecialLit 0x100000 : 0), iLightMap = FPoly[+0x108]
        // (iBrushPoly), iBrushPoly = FPoly index, Pan from FPoly, Actor = brush; FPoly.iLink = iSurf.
        // Brush node chain head = INDEX_NONE. Actor fields (SavedPos/SavedRot/bAssimilated)
        // are written by the runtime.
        public void SetupActorBrush(TrackedBrushModel bm, bool specialLit)
        {
            if (_ready) throw new InvalidOperationException("SetupActorBrush after the constructor");
            bm.FirstNode = -1;
            int n = bm.Polys.Length;
            int s0 = M._surfRec.Length;
            Array.Resize(ref M._surfRec, s0 + n);
            Array.Resize(ref M._surfFlags, s0 + n);
            Array.Resize(ref M._surfMaterial, s0 + n);
            for (int i = 0; i < n; i++)
            {
                PolyRecord p = bm.Polys[i];
                int surf = s0 + i;
                int vNormal = M.AddVector(Vector3.zero);
                int vU = M.AddVector(Vector3.zero);
                int vV = M.AddVector(Vector3.zero);
                int pBase = M.AddPoint(Vector3.zero);
                int flags = (p.PolyFlags & 0x3cffffff) | (specialLit ? 0x100000 : 0);
                M._surfRec[surf] = new BspSurfRecord(0, flags, pBase, vNormal, vU, vV,
                    p.iBrushPoly, i, p.PanU, p.PanV, 0);
                M._surfFlags[surf] = flags;
                M._surfMaterial[surf] = p.Texture;
                bm.Link[i] = surf;
            }
        }

        // Constructor tail after SetupActorBrush (Engine.dll.c:237836-237872): NewThang base,
        // ExpandNodes (0x200 + 2*Num, at most 0x10000), ExpandVerts (0x200 + 2*Num), NodeMaps and
        // VertUsed zeroed over the whole growth.
        public void FinishConstruction()
        {
            if (_ready) throw new InvalidOperationException("tracker constructor called twice");
            _firstMaster = M._pointNum;
            int num = M._nodes.Length;
            int target = Math.Min(0x200 + num * 2, 0x10000);
            _nodeCursor = num;
            if (target > num)
            {
                Array.Resize(ref M._nodes, target);
                Array.Resize(ref M._planes, target);
                if (M._zoneMask is not null) Array.Resize(ref M._zoneMask, target);   // ExpandNodes: growth zeroed
            }
            int vnum = M._vertices.Length;
            long vt = Math.Min(0x200L + vnum * 2L, 0x7fffffffL);
            _vertCursor = vnum;
            if (vt > vnum) Array.Resize(ref M._vertices, (int)vt);
            _nodeMapBrush = new object[M._nodes.Length - Nodes0];
            _nodeMapParent = new int[M._nodes.Length - Nodes0];
            _vertUsed = new object[M._vertices.Length - Verts0];
            _ready = true;
        }

        // ── Update (FUN_1038c260) ───────────────────────────────────────────
        sealed class Link { public object B; public Link Next; }
        Link _list;

        void Push(ITrackerHost host, object b)
        {
            host.SetAssimilated(b, true);
            _list = new Link { B = b, Next = _list };
            host.SaveCurrent(b);
        }

        // only == null: Update(0) - every tracked brush in the level actor list.
        public void Update<F>(object only, IReadOnlyList<object> levelActors, ITrackerHost host) where F : struct, IFpuMode<F>
        {
            if (!_ready) throw new InvalidOperationException("Update before the tracker constructor");
            Updates++;
            _list = null;
            if (only is null)
            {
                for (int i = 0; i < levelActors.Count; i++)
                {
                    object a = levelActors[i];
                    if (a is null || !host.IsTrackedBrush(a)) continue;
                    if (!host.GetAssimilated(a)) Push(host, a);
                }
            }
            else if (host.Moved(only) && !host.GetAssimilated(only))
                Push(host, only);

            for (Link l = _list; l is not null; l = l.Next)
                for (int i = host.ModelOf(l.B).FirstNode; i != -1; i = M._nodes[i].Link)
                    ForceGroupFlush(host, i);
            for (Link l = _list; l is not null; l = l.Next) FlushActorBrush(host.ModelOf(l.B));
            for (Link l = _list; l is not null; l = l.Next) AddBrush<F>(host, l.B);
            _list = null;
        }

        // FUN_1038e4a0: mark 0x20 over the fragment subtree; a foreign fragment's brush goes to the list.
        void ForceGroupFlush(ITrackerHost host, int i)
        {
            ref Node n = ref M._nodes[i];
            if ((n.NodeFlags & 0x20) != 0) return;
            n.NodeFlags |= 0x20;
            int front = n.Front, back = n.Back, plane = n.Plane;
            if (front != -1) ForceGroupFlush(host, front);
            if (back != -1) ForceGroupFlush(host, back);
            if (plane != -1) ForceGroupFlush(host, plane);
            object b = _nodeMapBrush[i - Nodes0];
            if (b is not null && !host.GetAssimilated(b)) Push(host, b);
        }

        // FUN_1038e680: detach each brush node from its parent (iFront, else iBack, else iPlane),
        // points go to the free list, VertUsed and NodeMaps are zeroed.
        void FlushActorBrush(TrackedBrushModel bm)
        {
            for (int i = bm.FirstNode; i != -1;)
            {
                ref Node n = ref M._nodes[i];
                ref Node parent = ref M._nodes[_nodeMapParent[i - Nodes0]];
                if (parent.Front == i) parent.Front = -1;
                else if (parent.Back == i) parent.Back = -1;
                else if (parent.Plane == i) parent.Plane = -1;
                int nv = n.NumVertices & 0xff;
                for (int k = 0; k < nv; k++)
                {
                    int p = M._vertices[n.VertPool + k];
                    _pointNext[p - _firstMaster] = _freePoint;
                    _freePoint = p;
                }
                for (int k = 0; k < nv; k++) _vertUsed[k - Verts0 + n.VertPool] = null;
                _nodeMapBrush[i - Nodes0] = null;
                _nodeMapParent[i - Nodes0] = 0;
                i = n.Link;
            }
            bm.FirstNode = -1;
        }

        // Third pass of Update (Engine.dll.c:237625-237760): brush FPoly to world, sphere, insertion.
        void AddBrush<F>(ITrackerHost host, object brush) where F : struct, IFpuMode<F>
        {
            host.SetAssimilated(brush, false);
            _curBrush = brush;
            TrackedBrushModel bm = host.ModelOf(brush);
            BrushPlacement pl = host.PlacementOf(brush);
            BuildCoords<F>(pl, out float[] px, out float[] vx, out float orientation);
            var polys = new WorkPoly[bm.Polys.Length];
            int total = 0;
            for (int i = 0; i < polys.Length; i++) total += bm.Polys[i].Vertices.Length;
            var sx = new float[total]; var sy = new float[total]; var sz = new float[total];
            int k = 0;
            for (int i = 0; i < polys.Length; i++)
            {
                WorkPoly w = polys[i] = FromRecord(bm.Polys[i]);
                Transform<F>(w, px, vx, pl.PrePivot, pl.Location, orientation);
                for (int v = 0; v < w.N; v++) { sx[k] = w.X[v]; sy[k] = w.Y[v]; sz[k] = w.Z[v]; k++; }
            }
            Sphere<F>(sx, sy, sz, k, out float cx, out float cy, out float cz, out float radius);
            M.PrecomputeSphereFilter<F>(cx, cy, cz, radius);
            _tailModel = bm; _tailNode = -1;
            for (int i = 0; i < polys.Length; i++)
            {
                WorkPoly w = polys[i];
                int iSurf = bm.Link[i];
                BspSurfRecord s = M._surfRec[iSurf];
                _curNx = w.Nx; _curNy = w.Ny; _curNz = w.Nz;
                M._points[s.PBase] = new Vector3(w.Bx, w.By, w.Bz);
                M._vectors[s.VNormal] = new Vector3(w.Nx, w.Ny, w.Nz);
                M._vectors[s.VTextureU] = new Vector3(w.Ux, w.Uy, w.Uz);
                M._vectors[s.VTextureV] = new Vector3(w.Vx, w.Vy, w.Vz);
                if (M._nodes.Length > 0) FilterPoly<F>(iSurf, 0, -1, w, 1);
            }
            SetTail(-1);
            for (int i = bm.FirstNode; i != -1; i = M._nodes[i].Link)
            {
                if (!ReferenceEquals(_nodeMapBrush[i - Nodes0], brush))
                    throw new InvalidOperationException("UnDynBsp.cpp:0x157 NodeMaps(iNode).iOriginalTopNode != Actor");
                M._nodes[i].NodeFlags &= 0xdf;
            }
        }

        void SetTail(int value)
        {
            if (_tailNode == -1) _tailModel.FirstNode = value;
            else M._nodes[_tailNode].Link = value;
        }

        static WorkPoly FromRecord(PolyRecord p)
        {
            var w = new WorkPoly
            {
                Bx = p.Base.x, By = p.Base.y, Bz = p.Base.z, Nx = p.Normal.x, Ny = p.Normal.y, Nz = p.Normal.z,
                Ux = p.TextureU.x, Uy = p.TextureU.y, Uz = p.TextureU.z, Vx = p.TextureV.x, Vy = p.TextureV.y, Vz = p.TextureV.z,
                N = p.Vertices.Length, PolyFlags = p.PolyFlags,
            };
            for (int i = 0; i < w.N; i++) { w.X[i] = p.Vertices[i].x; w.Y[i] = p.Vertices[i].y; w.Z[i] = p.Vertices[i].z; }
            return w;
        }

        // ── FilterPoly (FUN_1038e000), arguments (iSurf, iNode, iCoplanarParent, Poly, Outside) ──
        void FilterPoly<F>(int iSurf, int iNode, int iCoplanar, WorkPoly poly, int outside) where F : struct, IFpuMode<F>
        {
            // Node fields are read from the model at the moment of use, as in shipped (ebx is a pointer
            // to the node): the half recursion (>= 14 vertices) and insertion of the front split part may
            // attach a fragment to this same node.
            while (true)
            {
                if (poly.N >= 14)
                {
                    var other = new WorkPoly();
                    SplitInHalf(poly, other);
                    FilterPoly<F>(iSurf, iNode, iCoplanar, other, outside);
                }
                int flags = M._nodes[iNode].NodeFlags;
                bool front;
                if ((flags & 0x40) != 0) front = true;
                else if ((flags & 0x80) != 0) front = false;
                else
                {
                    Plane pl = M._planes[iNode];
                    var fp = new WorkPoly();
                    var bp = new WorkPoly();
                    int r = SplitWithPlaneFast<F>(poly, (float)pl.X, (float)pl.Y, (float)pl.Z, (float)pl.W, fp, bp);
                    if (r == 1) front = true;
                    else if (r == 2) front = false;
                    else if (r == 0)
                    {
                        // 0x1038e1d5-0x1038e1fb: ((cur.z*N.z + cur.y*N.y) + N.x*cur.x) >= 0 - coplanar parent.
                        F d = (F.L(_curNz) * F.L((float)pl.Z) + F.L(_curNy) * F.L((float)pl.Y))
                              + F.L((float)pl.X) * F.L(_curNx);
                        if (d.Cmp(0.0) >= 0) iCoplanar = iNode;
                        front = true;
                    }
                    else if (r == 3)
                    {
                        int child = M._nodes[iNode].Front;
                        bool csgNow = IsCsg(iNode);
                        if (child != -1)
                            FilterPoly<F>(iSurf, child, iCoplanar, fp, outside != 0 || csgNow ? 1 : 0);
                        else if (outside != 0 || csgNow)
                            AddPolyFragment<F>(iSurf, iNode, iCoplanar, 1, fp);
                        poly = bp;
                        front = false;
                    }
                    else return;
                }
                bool csg = IsCsg(iNode);
                if (front)
                {
                    outside = outside != 0 || csg ? 1 : 0;
                    int next = M._nodes[iNode].Front;
                    if (next == -1)
                    {
                        if (outside != 0) AddPolyFragment<F>(iSurf, iNode, iCoplanar, 1, poly);
                        return;
                    }
                    iNode = next;
                }
                else
                {
                    outside = outside != 0 && !csg ? 1 : 0;
                    int next = M._nodes[iNode].Back;
                    if (next == -1)
                    {
                        if (outside != 0) AddPolyFragment<F>(iSurf, iNode, iCoplanar, 0, poly);
                        return;
                    }
                    iNode = next;
                }
            }
        }

        // NumVertices (byte +0x36) != 0 and (NodeFlags & 0x21) == 0: the FilterPoly check
        // (0x1038e207-0x1038e212), without ExtraNodeFlags.
        bool IsCsg(int i) => (M._nodes[i].NumVertices & 0xff) != 0 && (M._nodes[i].NodeFlags & 0x21) == 0;

        // ── AddPolyFragment (FUN_1038da40), arguments (iSurf, iParent, iCoplanar, isFront, Poly) ──
        void AddPolyFragment<F>(int iSurf, int iParent, int iCoplanar, int isFront, WorkPoly poly) where F : struct, IFpuMode<F>
        {
            int link = isFront;
            int anchor = iParent;
            bool otherParent = false;
            if (iCoplanar != -1)
            {
                otherParent = iParent != iCoplanar;
                link = 2;
                anchor = iCoplanar;
                while (M._nodes[anchor].Plane != -1) anchor = M._nodes[anchor].Plane;
            }
            // Free node: from the cursor to Nodes.Num, then from Nodes0 to the previous cursor.
            int num = M._nodes.Length, start = _nodeCursor, iNode = -1;
            for (; _nodeCursor < num; _nodeCursor++)
                if (_nodeMapBrush[_nodeCursor - Nodes0] is null) { iNode = _nodeCursor; break; }
            if (iNode == -1)
            {
                for (_nodeCursor = Nodes0; _nodeCursor < start; _nodeCursor++)
                    if (_nodeMapBrush[_nodeCursor - Nodes0] is null) { iNode = _nodeCursor; break; }
                if (iNode == -1) return;   // 0x1038daf7 -> 0x1038deb8: no room - fragment not inserted
            }
            _nodeMapBrush[iNode - Nodes0] = _curBrush;
            _nodeMapParent[iNode - Nodes0] = anchor;

            ref Node n = ref M._nodes[iNode];
            Node an = M._nodes[anchor];
            // 0x1038db4e..0x1038db5d: new node ZoneMask = copy of the anchor mask (iParent or the coplanar chain tail).
            M.SetZoneMask(iNode, M.ZoneMaskOf(anchor));
            n.Surf = iSurf;
            n.Collision = -1;
            n.Link = -1;
            n.NumVertices = poly.N & 0xff;
            n.Front = -1; n.Back = -1; n.Plane = -1;
            // W = (Base.y*N.y + Base.z*N.z) + Base.x*N.x, float store (0x1038db87-0x1038dbbb).
            float w = ((F.L(poly.By) * F.L(poly.Ny) + F.L(poly.Bz) * F.L(poly.Nz)) + F.L(poly.Bx) * F.L(poly.Nx)).F;
            M._planes[iNode] = new Plane(poly.Nx, poly.Ny, poly.Nz, w);
            int flags = (poly.PolyFlags & 0x8) != 0 ? 0x21 : 0x20;
            if ((poly.PolyFlags & 0x4000001) != 0) flags |= 0x4;
            if ((poly.PolyFlags & 0x2) != 0) flags |= 0x2;
            n.NodeFlags = flags;
            if (iCoplanar != -1 && !otherParent)
            {
                // 0x1038dc07-0x1038dc56: side by sign of (N.z*n.z + N.y*n.y) + N.x*n.x of the chain tail.
                // Form from disassembly: in campaign data the side is always 0, so the data cannot tell the form apart.
                F d = (F.L((float)M._planes[anchor].Z) * F.L(poly.Nz) + F.L((float)M._planes[anchor].Y) * F.L(poly.Ny))
                      + F.L((float)M._planes[anchor].X) * F.L(poly.Nx);
                int side = d.Cmp(0.0) < 0 ? 1 : 0;
                n.Zone0 = side == 0 ? an.Zone0 : an.Zone1;
                n.Leaf0 = side == 0 ? an.Leaf0 : an.Leaf1;
                n.Zone1 = side == 0 ? an.Zone1 : an.Zone0;
                n.Leaf1 = side == 0 ? an.Leaf1 : an.Leaf0;
            }
            else
            {
                Node par = M._nodes[iParent];
                int z = isFront == 0 ? par.Zone0 : par.Zone1;
                int lf = isFront == 0 ? par.Leaf0 : par.Leaf1;
                n.Zone0 = z; n.Zone1 = z; n.Leaf0 = lf; n.Leaf1 = lf;
            }
            // 0x1038dc75-0x1038dcb7: Nodes[iParent].ZoneMask |= __allshl(1, iZone[0]) | __allshl(1, iZone[1]).
            M.OrZoneMask(iParent, Shl64(n.Zone0 & 0xff) | Shl64(n.Zone1 & 0xff));

            // Vertex pool (0x1038dcba-0x1038dd71): a contiguous run of NumVertices free entries.
            int nv = poly.N;
            int pool = -1;
            int oldCursor = _vertCursor, run = 0, idx = _vertCursor - Verts0;
            int vnum = M._vertices.Length;
            while (nv + _vertCursor < vnum)
            {
                if (_vertUsed[idx] is null)
                {
                    if (++run >= nv) { MarkRun(idx, run); pool = _vertCursor - nv + 1; break; }
                }
                else run = 0;
                _vertCursor++; idx++;
            }
            if (pool == -1)
            {
                _vertCursor = Verts0; run = 0; idx = 0;
                while (nv + _vertCursor < oldCursor)
                {
                    if (_vertUsed[idx] is null)
                    {
                        if (++run >= nv) { MarkRun(idx, run); pool = _vertCursor - nv + 1; break; }
                    }
                    else run = 0;
                    _vertCursor++; idx++;
                }
            }
            n.VertPool = pool;
            if (pool == -1)
            {
                _nodeMapBrush[iNode - Nodes0] = null;
                _nodeMapParent[iNode - Nodes0] = 0;
                return;
            }
            for (int k = 0; k < nv; k++)
            {
                int p = NewThang();
                if (p == -1)
                {
                    for (int j = k - 1; j >= 0; j--)
                    {
                        int q = M._vertices[pool + j];
                        _pointNext[q - _firstMaster] = _freePoint;
                        _freePoint = q;
                    }
                    _nodeMapBrush[iNode - Nodes0] = null;
                    _nodeMapParent[iNode - Nodes0] = 0;
                    return;
                }
                M._vertices[pool + k] = p;
                M._points[p] = new Vector3(poly.X[k], poly.Y[k], poly.Z[k]);
            }
            ref Node a = ref M._nodes[anchor];
            if (link == 0) a.Back = iNode; else if (link == 1) a.Front = iNode; else a.Plane = iNode;
            SetTail(iNode);
            _tailNode = iNode;
        }

        // 0x1038dd00-0x1038dd13: the run is marked with the brush from the last vertex backwards.
        void MarkRun(int idx, int run)
        {
            for (; run > 0; run--) _vertUsed[idx--] = _curBrush;
        }

        // ── Arithmetic of the bodies the tracker calls ─────────────────────────────

        // FVector::TransformVectorBy, inlined mat-vec (disasm 0x103967b6-0x1039680a, same in
        // FCoords::operator*=): (x*A.x + y*A.y) + z*A.z in the register, float store.
        static float Dot3<F>(float x, float y, float z, float ax, float ay, float az) where F : struct, IFpuMode<F>
            => ((F.L(x) * F.L(ax) + F.L(y) * F.L(ay)) + F.L(z) * F.L(az)).F;

        // FCoords axes are rows X, Y, Z (9 numbers): this *= b (axes via TransformVectorBy).
        static void MulCoords<F>(float[] a, float[] b) where F : struct, IFpuMode<F>
        {
            for (int r = 0; r < 9; r += 3)
            {
                float x = a[r], y = a[r + 1], z = a[r + 2];
                a[r] = Dot3<F>(x, y, z, b[0], b[1], b[2]);
                a[r + 1] = Dot3<F>(x, y, z, b[3], b[4], b[5]);
                a[r + 2] = Dot3<F>(x, y, z, b[6], b[7], b[8]);
            }
        }

        static readonly float[] IdentityAxes = { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f };

        // FCoords::operator*=(FScale) (Core.dll.c:40001): axes via sheerCoords (identity at zero
        // snap), then component-wise *Scale. Non-zero snap is a loud failure, as in
        // BrushCoords.MulScale.
        static void MulScale<F>(float[] a, Vector3 s, float sheer, string who) where F : struct, IFpuMode<F>
        {
            if (BrushCoords.SnapSheer(sheer) != 0f) throw new NotSupportedException($"tracker: brush shear ({who}) not ported");
            MulCoords<F>(a, IdentityAxes);
            for (int r = 0; r < 9; r += 3) { a[r] *= s.x; a[r + 1] *= s.y; a[r + 2] *= s.z; }
        }

        // FCoords::operator/=(FScale) (Core.dll.c:51222): component-wise division first, then
        // sheerCoords (identity at zero snap).
        static void DivScale<F>(float[] a, Vector3 s, float sheer, string who) where F : struct, IFpuMode<F>
        {
            if (BrushCoords.SnapSheer(sheer) != 0f) throw new NotSupportedException($"tracker: brush shear ({who}) not ported");
            for (int r = 0; r < 9; r += 3) { a[r] /= s.x; a[r + 1] /= s.y; a[r + 2] /= s.z; }
            MulCoords<F>(a, IdentityAxes);
        }

        // *=(FRotator) (Core 0x1011d780): yaw, pitch, roll; /=(FRotator) (0x1011e190): roll, pitch,
        // yaw transposed; same matrices as BrushCoords.MulRotator/DivRotator.
        static void MulRotator<F>(float[] a, int pitch, int yaw, int roll) where F : struct, IFpuMode<F>
        {
            float sy = BrushCoords.SinAngle(yaw), cy = BrushCoords.CosAngle(yaw);
            MulCoords<F>(a, new[] { cy, sy, 0f, -sy, cy, 0f, 0f, 0f, 1f });
            float sp = BrushCoords.SinAngle(pitch), cp = BrushCoords.CosAngle(pitch);
            MulCoords<F>(a, new[] { cp, 0f, sp, 0f, 1f, 0f, -sp, 0f, cp });
            float sr = BrushCoords.SinAngle(roll), cr = BrushCoords.CosAngle(roll);
            MulCoords<F>(a, new[] { 1f, 0f, 0f, 0f, cr, -sr, 0f, sr, cr });
        }

        static void DivRotator<F>(float[] a, int pitch, int yaw, int roll) where F : struct, IFpuMode<F>
        {
            float sr = BrushCoords.SinAngle(roll), cr = BrushCoords.CosAngle(roll);
            MulCoords<F>(a, new[] { 1f, 0f, 0f, 0f, cr, sr, 0f, -sr, cr });
            float sp = BrushCoords.SinAngle(pitch), cp = BrushCoords.CosAngle(pitch);
            MulCoords<F>(a, new[] { cp, 0f, -sp, 0f, 1f, 0f, sp, 0f, cp });
            float sy = BrushCoords.SinAngle(yaw), cy = BrushCoords.CosAngle(yaw);
            MulCoords<F>(a, new[] { cy, -sy, 0f, sy, cy, 0f, 0f, 0f, 1f });
        }

        // FScale::Orientation (Core.dll.c:11001): sign of (z*y)*x.
        static float Orientation<F>(Vector3 s) where F : struct, IFpuMode<F>
        {
            int c = (F.L(s.z) * F.L(s.y) * F.L(s.x)).Cmp(0.0);
            return c > 0 ? 1f : c == 0 ? 0f : -1f;
        }

        // ABrush::BuildCoords(Coords, NULL) (Engine.dll.c:49283): PointXform = I *= PostScale *=
        // Rotation *= MainScale; VectorXform = transposed (I /= MainScale /= Rotation /=
        // PostScale). FPoly::Transform reads only the axes (neither Origin is needed). Returns the
        // sign of MainScale * PostScale.Orientation().
        internal static void BuildCoords<F>(BrushPlacement pl, out float[] point, out float[] vector, out float orientation)
            where F : struct, IFpuMode<F>
        {
            point = (float[])IdentityAxes.Clone();
            MulScale<F>(point, pl.PostScale, pl.PostSheer, "PostScale");
            MulRotator<F>(point, pl.Pitch, pl.Yaw, pl.Roll);
            MulScale<F>(point, pl.MainScale, pl.MainSheer, "MainScale");
            float[] t = (float[])IdentityAxes.Clone();
            DivScale<F>(t, pl.MainScale, pl.MainSheer, "MainScale");
            DivRotator<F>(t, pl.Pitch, pl.Yaw, pl.Roll);
            DivScale<F>(t, pl.PostScale, pl.PostSheer, "PostScale");
            vector = new[] { t[0], t[3], t[6], t[1], t[4], t[7], t[2], t[5], t[8] };
            orientation = Orientation<F>(pl.MainScale) * Orientation<F>(pl.PostScale);
        }

        // FPoly::Transform (0x10396770): TextureU/V via VectorXform; Base and vertices: (P - PrePivot)
        // in float, PointXform, + Location in float; negative orientation reverses vertex order;
        // normal = SafeNormal(VectorXform*N).
        internal static void Transform<F>(WorkPoly w, float[] p, float[] v, Vector3 pre, Vector3 post, float orientation)
            where F : struct, IFpuMode<F>
        {
            float ux = w.Ux, uy = w.Uy, uz = w.Uz;
            w.Ux = Dot3<F>(ux, uy, uz, v[0], v[1], v[2]); w.Uy = Dot3<F>(ux, uy, uz, v[3], v[4], v[5]); w.Uz = Dot3<F>(ux, uy, uz, v[6], v[7], v[8]);
            float vx = w.Vx, vy = w.Vy, vz = w.Vz;
            w.Vx = Dot3<F>(vx, vy, vz, v[0], v[1], v[2]); w.Vy = Dot3<F>(vx, vy, vz, v[3], v[4], v[5]); w.Vz = Dot3<F>(vx, vy, vz, v[6], v[7], v[8]);
            Point<F>(ref w.Bx, ref w.By, ref w.Bz, p, pre, post);
            for (int i = 0; i < w.N; i++) Point<F>(ref w.X[i], ref w.Y[i], ref w.Z[i], p, pre, post);
            if (orientation < 0f)
                for (int i = 0; i < w.N / 2; i++)
                {
                    int j = w.N - 1 - i;
                    (w.X[i], w.X[j]) = (w.X[j], w.X[i]);
                    (w.Y[i], w.Y[j]) = (w.Y[j], w.Y[i]);
                    (w.Z[i], w.Z[j]) = (w.Z[j], w.Z[i]);
                }
            float nx = w.Nx, ny = w.Ny, nz = w.Nz;
            SafeNormal<F>(Dot3<F>(nx, ny, nz, v[0], v[1], v[2]), Dot3<F>(nx, ny, nz, v[3], v[4], v[5]),
                Dot3<F>(nx, ny, nz, v[6], v[7], v[8]), out w.Nx, out w.Ny, out w.Nz);
        }

        static void Point<F>(ref float x, ref float y, ref float z, float[] p, Vector3 pre, Vector3 post) where F : struct, IFpuMode<F>
        {
            float dx = (F.L(x) - F.L(pre.x)).F, dy = (F.L(y) - F.L(pre.y)).F, dz = (F.L(z) - F.L(pre.z)).F;
            float tx = Dot3<F>(dx, dy, dz, p[0], p[1], p[2]);
            float ty = Dot3<F>(dx, dy, dz, p[3], p[4], p[5]);
            float tz = Dot3<F>(dx, dy, dz, p[6], p[7], p[8]);
            x = (F.L(tx) + F.L(post.x)).F; y = (F.L(ty) + F.L(post.y)).F; z = (F.L(tz) + F.L(post.z)).F;
        }

        // FVector::SafeNormal (Core 0x1014f4e0): (x^2 + y^2) + z^2 in the register; below 1e-8f ->
        // zero; otherwise 1.0f / appSqrt(sum), components stored as float.
        internal static void SafeNormal<F>(float x, float y, float z, out float ox, out float oy, out float oz) where F : struct, IFpuMode<F>
        {
            F sq = (F.L(x) * F.L(x) + F.L(y) * F.L(y)) + F.L(z) * F.L(z);
            if (sq.Cmp(9.99999993922529e-09) < 0) { ox = oy = oz = 0f; return; }
            F scale = F.L(1.0) / F.SqrtQ(sq);
            ox = (scale * F.L(x)).F; oy = (scale * F.L(y)).F; oz = (scale * F.L(z)).F;
        }

        // FSphere(const FVector*, int) (Core 0x1014ee40): center = middle of the points' FBox, radius =
        // max (dy^2 + dx^2) + dz^2 (float store on strict increase), then appSqrt * 1.001.
        internal static void Sphere<F>(float[] xs, float[] ys, float[] zs, int count,
            out float cx, out float cy, out float cz, out float radius) where F : struct, IFpuMode<F>
        {
            cx = cy = cz = radius = 0f;
            if (count == 0) return;
            float mnx = xs[0], mny = ys[0], mnz = zs[0], mxx = xs[0], mxy = ys[0], mxz = zs[0];
            for (int i = 1; i < count; i++)
            {
                if (mnx > xs[i]) mnx = xs[i]; if (mny > ys[i]) mny = ys[i]; if (mnz > zs[i]) mnz = zs[i];
                if (mxx < xs[i]) mxx = xs[i]; if (mxy < ys[i]) mxy = ys[i]; if (mxz < zs[i]) mxz = zs[i];
            }
            float sy = (F.L(mny) + F.L(mxy)).F;
            float sx = (F.L(mxx) + F.L(mnx)).F;
            cz = ((F.L(mnz) + F.L(mxz)) * F.L(0.5)).F;
            cy = (F.L(sy) * F.L(0.5)).F;
            cx = (F.L(sx) * F.L(0.5)).F;
            float w = 0f;
            for (int i = 0; i < count; i++)
            {
                F dx = F.L(cx) - F.L(xs[i]), dy = F.L(cy) - F.L(ys[i]), dz = F.L(cz) - F.L(zs[i]);
                F d = (dy * dy + dx * dx) + dz * dz;
                if (d.Cmp(w) > 0) w = d.F;
            }
            radius = (F.SqrtQ(F.L(w)) * F.L(1.0010000467300415)).F;
        }

        // FPoly::SplitWithPlaneFast (0x10395ee0): 0 coplanar, 1 front, 2 back, 3 split.
        internal static int SplitWithPlaneFast<F>(WorkPoly poly, float nx, float ny, float nz, float w,
            WorkPoly front, WorkPoly back) where F : struct, IFpuMode<F>
        {
            int n = poly.N;
            Span<int> side = stackalloc int[n];
            bool bFront = false, bBack = false;
            for (int i = 0; i < n; i++)
            {
                F d = ((F.L(nz) * F.L(poly.Z[i]) + F.L(nx) * F.L(poly.X[i])) + F.L(ny) * F.L(poly.Y[i])) - F.L(w);
                if (d.Cmp(0.0) < 0) { side[i] = 1; if (d.Cmp(-0.25) < 0) bBack = true; }
                else { side[i] = 0; if (d.Cmp(0.25) > 0) bFront = true; }
            }
            if (!bFront) return bBack ? 2 : 0;
            if (!bBack) return 1;
            int fc = 0, bc = 0;
            int prevSide = side[n - 1];
            float px = poly.X[n - 1], py = poly.Y[n - 1], pz = poly.Z[n - 1];
            for (int i = 0; i < n; i++)
            {
                float x = poly.X[i], y = poly.Y[i], z = poly.Z[i];
                int s = side[i];
                if (s == prevSide)
                {
                    if (s == 0) { front.X[fc] = x; front.Y[fc] = y; front.Z[fc] = z; fc++; }
                    else { back.X[bc] = x; back.Y[bc] = y; back.Z[bc] = z; bc++; }
                }
                else
                {
                    F dxr = F.L(x) - F.L(px);
                    float dxf = dxr.F, dyf = (F.L(y) - F.L(py)).F, dzf = (F.L(z) - F.L(pz)).F;
                    F num = F.L(w) - ((F.L(nx) * F.L(px) + F.L(nz) * F.L(pz)) + F.L(ny) * F.L(py));
                    F den = (F.L(nz) * F.L(dzf) + F.L(ny) * F.L(dyf)) + F.L(dxf) * F.L(nx);
                    float t = (num / den).F;
                    float ix = (dxr * F.L(t) + F.L(px)).F;
                    float iy = (F.L((F.L(dyf) * F.L(t)).F) + F.L(py)).F;
                    float iz = (F.L((F.L(dzf) * F.L(t)).F) + F.L(pz)).F;
                    back.X[bc] = ix; back.Y[bc] = iy; back.Z[bc] = iz; bc++;
                    front.X[fc] = ix; front.Y[fc] = iy; front.Z[fc] = iz; fc++;
                    if (prevSide == 0) { back.X[bc] = x; back.Y[bc] = y; back.Z[bc] = z; bc++; }
                    else { front.X[fc] = x; front.Y[fc] = y; front.Z[fc] = z; fc++; }
                }
                prevSide = s; px = x; py = y; pz = z;
            }
            front.N = fc; back.N = bc;
            front.Bx = back.Bx = poly.Bx; front.By = back.By = poly.By; front.Bz = back.Bz = poly.Bz;
            front.Nx = back.Nx = poly.Nx; front.Ny = back.Ny = poly.Ny; front.Nz = back.Nz = poly.Nz;
            front.PolyFlags = back.PolyFlags = poly.PolyFlags;
            return 3;
        }

        // FPoly::SplitInHalf (Engine.dll.c:112274): second half is a copy with vertices [n/2 .. n-1] and
        // vertex 0; both get PF_EdCut (0x80000000).
        internal static void SplitInHalf(WorkPoly poly, WorkPoly other)
        {
            int n = poly.N, half = n / 2;
            if (n < 4 || n > 16) throw new InvalidOperationException($"FPoly::SplitInHalf: {n} Vertices (GError)");
            other.CopyFrom(poly);
            other.N = n - half + 1;
            poly.N = half + 1;
            for (int i = 0; i < other.N - 1; i++) { other.X[i] = poly.X[half + i]; other.Y[i] = poly.Y[half + i]; other.Z[i] = poly.Z[half + i]; }
            other.X[other.N - 1] = poly.X[0]; other.Y[other.N - 1] = poly.Y[0]; other.Z[other.N - 1] = poly.Z[0];
            poly.PolyFlags |= unchecked((int)0x80000000);
            other.PolyFlags |= unchecked((int)0x80000000);
        }

        // NewThang (FUN_1038f240): point from the free list, otherwise Points.Add(1).
        int NewThang()
        {
            int p = _freePoint;
            if (p == -1)
            {
                p = M.AddPoint(Vector3.zero);
                _pointNext.Add(0);
            }
            else _freePoint = _pointNext[p - _firstMaster];
            _pointNext[p - _firstMaster] = 0;
            return p;
        }
    }
}
}
