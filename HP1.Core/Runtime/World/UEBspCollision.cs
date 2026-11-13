using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UELib;
using UELib.Core;
using UELib.Types;
// Script vectors are the Core value type.
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{

// Direct HP1 UModel reader plus the shipped UModel/UPrimitive collision bodies (Engine.dll). It
// consumes the shipped .unr export; no extracted JSON enters
// the gameplay query.
public sealed partial class HeadlessBspModel
{
    // Nodes, planes, surfaces, points and vertices are grown by the mover brush tracker
    // (UEMovingBrushTracker.cs), as shipped FMovingBrushTracker grows UModel.
    private Plane[] _planes;
    private Node[] _nodes;
    private readonly int[] _leafHulls;
    private int[] _surfFlags;
    private SurfMaterialRef[] _surfMaterial;
    private readonly int[] _zoneActors;
    private Vector3[] _points;
    private int[] _vertices;

    private const int NfNotVisBlocking = 4; // EBspNodeFlags, ULevel.h
    private const int PfNotSolid = 0x8;     // EPolyFlags, ULevel.h

    private HeadlessBspModel(Plane[] planes, Node[] nodes, int[] leafHulls, int[] surfFlags,
        SurfMaterialRef[] surfMaterial, int[] zoneActors, Vector3[] points, int[] vertices, int rootOutside,
        BspParseTables parseTables)
    {
        _planes = planes;
        _nodes = nodes;
        _leafHulls = leafHulls;
        _surfFlags = surfFlags;
        _surfMaterial = surfMaterial;
        _zoneActors = zoneActors;
        _points = points;
        _vertices = vertices;
        RootOutside = rootOutside;
        ParseTables = parseTables;
        InitRuntimeTables();
    }

    // Reference to the material (Texture) on the surface a hit node belongs
    // to, or null: object name plus the import's outer chain (group + source
    // package) or the local export index for map-embedded textures. Used by
    // TraceTexture to read the texture's serialized FootstepSound.
    public SurfMaterialRef SurfaceMaterial(int node)
    {
        if (node < 0 || node >= _nodes.Length) return null;
        int surf = _nodes[node].Surf;
        return surf >= 0 && surf < _surfMaterial.Length ? _surfMaterial[surf] : null;
    }

    // Same material, but by SURFACE index rather than node. AttachDecal returns the texture of
    // the surface CHOSEN by FindCoplanarSurface (`*param_3 = *local_a0`, local_a0 = &Surfs[iSurf]),
    // which may differ from the surface of the node the trace hit.
    public SurfMaterialRef MaterialOfSurf(int surf)
        => surf >= 0 && surf < _surfMaterial.Length ? _surfMaterial[surf] : null;

    // Full retained parse of the serialized UModel sections that gameplay does
    // not consume, kept so an independent loader can be diffed against the parser
    // field by field.
    public BspParseTables ParseTables { get; }

    // Projection of static render geometry into contract types.
    //
    // SOLE PRODUCER of these values. The source is the ALREADY LOADED model; the `.unr` is not
    // re-read: a second reader of the same bytes is a second owner that drifts silently.
    //
    // NO new arithmetic here: every field is the value parsed from the file and proven by a
    // byte diff against two independent readers on all 41 maps. Builds polygon topology (Surf,
    // VertPool, NumVertices) and the plane, but NOT the tree walk: branching is Core's job.
    public HP1.UEScript.StaticGeometry BuildStaticGeometry()
    {
        var surfs = new HP1.UEScript.StaticSurface[ParseTables.Surfs.Length];
        for (int i = 0; i < surfs.Length; i++)
        {
            BspSurfRecord s = ParseTables.Surfs[i];
            SurfMaterialRef m = _surfMaterial[i];
            surfs[i] = new HP1.UEScript.StaticSurface(m == null ? "None" : m.Path,
                s.PolyFlags, s.PBase, s.VNormal, s.VTextureU, s.VTextureV,
                s.LightMap, s.BrushPoly, s.PanU, s.PanV, s.BrushActor);
        }
        Node[] staticNodes = StaticNodes;
        Plane[] staticPlanes = StaticPlanes;
        var nodes = new HP1.UEScript.StaticNode[LoadedNodeCount];
        for (int i = 0; i < nodes.Length; i++)
        {
            Node n = staticNodes[i];
            Plane p = staticPlanes[i];
            // Planes are widened to double at parse time; widening is exact, so narrowing back
            // returns the serialized bits.
            nodes[i] = new HP1.UEScript.StaticNode(n.Surf, n.VertPool, n.NumVertices,
                n.NodeFlags,
                new Vector3((float)p.X, (float)p.Y, (float)p.Z), (float)p.W,
                n.Zone0, n.Zone1, n.Plane);
        }
        var lightMaps = new HP1.UEScript.StaticLightMap[ParseTables.LightMaps.Length];
        for (int i = 0; i < lightMaps.Length; i++)
        {
            BspLightMapIndex l = ParseTables.LightMaps[i];
            lightMaps[i] = new HP1.UEScript.StaticLightMap(l.DataOffset, l.Pan,
                l.UClamp, l.VClamp, l.UScale, l.VScale, l.LightActors);
        }
        // Leaf light-source chain, read exactly as shipped FLightManager::SetupForActor reads it:
        // the leaf's iPermeating and the Model->Lights pool.
        var leafPermeating = new int[ParseTables.Leaves.Length];
        var leafVolumetric = new int[ParseTables.Leaves.Length];
        for (int i = 0; i < leafPermeating.Length; i++)
        {
            leafPermeating[i] = ParseTables.Leaves[i].Permeating;
            // iVolumetric has the same form (start of a sublist in Model->Lights); its reader is
            // URender::LeafVolumetricLighting (Render.dll.c:4584).
            leafVolumetric[i] = ParseTables.Leaves[i].Volumetric;
        }
        return new HP1.UEScript.StaticGeometry(surfs, nodes, RawPoints,
            ParseTables.Vectors, StaticVertices, ParseTables.VertSides, lightMaps,
            leafPermeating, ParseTables.Lights, (int[])_zoneActors.Clone(), leafVolumetric);
    }

    // UModel::RootOutside: whether space outside the root node is empty. The
    // shipped Engine.dll seeds its zero-extent LineCheck solidity walk with it.
    public int RootOutside { get; }

    // MAP nodes (as loaded). Mover tracker fragments live after them (LiveNodeCount).
    public int NodeCount => LoadedNodeCount;

    // RAW TABLES FOR THE RENDER MIRROR: the translated
    // Render.dll bodies read UModel in the shipped layout, and the mirror writes it from the same
    // parsed data as collision, so no second package reader appears.
    public void RawNode(int i, out float px, out float py, out float pz, out float pw,
        out int front, out int back, out int collision, out int surf, out int zone0, out int zone1,
        out int leaf0, out int leaf1, out int iPlane, out int vertPool, out int numVertices, out int nodeFlags)
    {
        Plane p = StaticPlanes[i]; Node n = StaticNodes[i];
        px = (float)p.X; py = (float)p.Y; pz = (float)p.Z; pw = (float)p.W;
        front = n.Front; back = n.Back; collision = n.Collision; surf = n.Surf;
        zone0 = n.Zone0; zone1 = n.Zone1; leaf0 = n.Leaf0; leaf1 = n.Leaf1;
        iPlane = n.Plane; vertPool = n.VertPool; numVertices = n.NumVertices; nodeFlags = n.NodeFlags;
    }
    public Vector3[] RawPoints => StaticPoints;
    public int[] RawVertPool => StaticVertices;
    public int[] RawLeafHulls => _leafHulls;
    public int[] RawZoneActors => _zoneActors;
    public int LeafHullValueCount => _leafHulls.Length;

    // One UELib load per map path, shared by every model entry point: repeat
    // UnrealLoader.LoadPackage calls print package headers to stdout (journal
    // noise) and re-read the whole file.
    private static readonly Dictionary<string, (UnrealPackage Package, byte[] Bytes)> _packageCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly struct Plane
    {
        public double X { get; }
        public double Y { get; }
        public double Z { get; }
        public double W { get; }
        public Plane(double X, double Y, double Z, double W)
        {
            this.X = X;
            this.Y = Y;
            this.Z = Z;
            this.W = W;
        }
    }
    private struct Node
    {
        public int Front { get; set; }
        public int Back { get; set; }
        public int Collision { get; set; }
        public int Surf { get; set; }
        public int Zone0 { get; set; }
        public int Zone1 { get; set; }
        public int Leaf0 { get; set; }
        public int Leaf1 { get; set; }
        public int Plane { get; set; }
        public int VertPool { get; set; }
        public int NumVertices { get; set; }
        public int NodeFlags { get; set; }
        // iRenderBound (+0x30): for a tracker fragment this is the brush node chain link
        // (FMovingBrushTracker keeps the next node there); Core does not read it for map nodes.
        public int Link { get; set; }
        public Node(int Front, int Back, int Collision, int Surf, int Zone0, int Zone1, int Leaf0, int Leaf1, int Plane, int VertPool, int NumVertices, int NodeFlags)
        {
            this.Front = Front;
            this.Back = Back;
            this.Collision = Collision;
            this.Surf = Surf;
            this.Zone0 = Zone0;
            this.Zone1 = Zone1;
            this.Leaf0 = Leaf0;
            this.Leaf1 = Leaf1;
            this.Plane = Plane;
            this.VertPool = VertPool;
            this.NumVertices = NumVertices;
            this.NodeFlags = NodeFlags;
        }
    }
}
}
