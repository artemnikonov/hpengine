// Reading the level's UModel from the .unr: model export resolution, bounds, reach specs, polys,
// the model export parser, surface material references and the binary Reader.
// Fields of HeadlessBspModel live in UEBspCollision.cs.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UELib;
using UELib.Core;
using UELib.Types;
// Script vectors are the Core value type (UEVector).
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
public sealed partial class HeadlessBspModel
{
    // Serialized UPrimitive bounds of every Model export in the map package:
    // properties terminator (None), FBox {Min, Max, IsValid}, FSphere {xyz, W}.
    // Same header the level-model parse skips (Load below, "25 + 16"). Keyed by
    // 1-based export index — the value a mover's Brush ObjectProperty carries.
    // Parse the Level export up to its Model reference. Layout per
    // ULevelBase::Load + ULevel::Load (as in SurrealEngine ULevel.cpp:33/86): properties
    // terminator, Actors (int32 count, int32 max, count object indices), FURL
    // (Protocol/Host/Map/Portal strings, Options count+strings, Port int32,
    // trailing int32), then the Model object index.
    private static UExportTableItem ResolveLevelModel(UnrealPackage package, byte[] packageBytes)
    {
        UExportTableItem level = null;
        foreach (UExportTableItem export in package.Exports)
            if (string.Equals(export.Class?.ObjectName?.ToString(), "Level", StringComparison.OrdinalIgnoreCase))
            {
                if (level is not null) throw new InvalidDataException("multiple Level exports");
                level = export;
            }
        if (level is null) throw new InvalidDataException("Level export missing");
        if (level.SerialOffset < 0 || level.SerialSize <= 0
            || level.SerialOffset > packageBytes.Length - level.SerialSize)
            throw new InvalidDataException("Level export range");
        var r = new Reader(packageBytes.AsSpan(level.SerialOffset, level.SerialSize).ToArray());
        const ulong RfHasStack = 0x02000000UL;
        if (((ulong)level.ObjectFlags & RfHasStack) != 0)
        {
            int node = r.Index();
            r.Index();
            r.Skip(12);
            if (node != 0) r.Index();
        }
        string terminator = PackageName(package, r.Index());
        if (!terminator.Equals("None", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Level property terminator: " + terminator);
        int actorCount = r.Int32();
        r.Int32(); // max
        for (int i = 0; i < actorCount; i++) r.Index();
        for (int i = 0; i < 4; i++) r.UString();     // Protocol/Host/Map/Portal
        int options = CheckedCount(r.Index(), "URL options");
        for (int i = 0; i < options; i++) r.UString();
        r.Skip(8); // Port + trailing int32
        int modelIndex = r.Index();
        if (modelIndex <= 0 || modelIndex - 1 >= package.Exports.Count)
            throw new InvalidDataException("ULevel.Model reference " + modelIndex);
        UExportTableItem model = package.Exports[modelIndex - 1];
        if (!string.Equals(model.Class?.ObjectName?.ToString(), "Model", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ULevel.Model is not a Model: " + model.ObjectName);
        return model;
    }
    private static (UnrealPackage Package, byte[] Bytes) PackageOf(string mapPath)
    {
        string key = HP1.Core.UEClassRegistry.PhysicalPath(mapPath);   // a symlinked spelling is the same file
        if (_packageCache.TryGetValue(key, out var cached)) return cached;
        UnrealPackage package = UnrealLoader.LoadPackage(mapPath, FileAccess.Read);
        package.InitializePackage(UnrealPackage.InitFlags.All);
        byte[] bytes = File.ReadAllBytes(mapPath);
        return _packageCache[key] = (package, bytes);
    }

    public static Dictionary<int, BrushBounds> LoadModelBounds(string mapPath)
    {
        (UnrealPackage package, byte[] packageBytes) = PackageOf(mapPath);
        var result = new Dictionary<int, BrushBounds>();
        for (int i = 0; i < package.Exports.Count; i++)
        {
            UExportTableItem export = package.Exports[i];
            if (!string.Equals(export.Class?.ObjectName?.ToString(), "Model", StringComparison.OrdinalIgnoreCase))
                continue;
            if (export.SerialOffset < 0 || export.SerialSize < 42
                || export.SerialOffset > packageBytes.Length - export.SerialSize)
                throw new InvalidDataException("Model export range " + export.ObjectName);
            var r = new Reader(packageBytes.AsSpan(export.SerialOffset, export.SerialSize).ToArray());
            // Some brush Models carry RF_HasStack: a serialized FStateFrame
            // precedes the property terminator (Node, StateNode, 8-byte
            // ProbeMask, 4-byte LatentAction, Offset when Node != None).
            const ulong RfHasStack = 0x02000000UL;
            if (((ulong)export.ObjectFlags & RfHasStack) != 0)
            {
                int node = r.Index();
                r.Index();  // StateNode
                r.Skip(12); // ProbeMask + LatentAction
                if (node != 0) r.Index(); // Offset
            }
            string terminator = PackageName(package, r.Index());
            if (!terminator.Equals("None", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Model {export.ObjectName} property terminator: {terminator}");
            var boxMin = new Vector3(r.Single(), r.Single(), r.Single());
            var boxMax = new Vector3(r.Single(), r.Single(), r.Single());
            r.Skip(1); // FBox.IsValid
            var center = new Vector3(r.Single(), r.Single(), r.Single());
            float radius = r.Single();
            result[i + 1] = new BrushBounds(boxMin, boxMax, center, radius);
        }
        return result;
    }

    public static HeadlessBspModel Load(string mapPath)
    {
        (UnrealPackage package, byte[] packageBytes) = PackageOf(mapPath);
        // The level's collision model comes from the serialized ULevel.Model
        // reference (ULevelBase: Actors array + FURL, then Model),
        // not from a size heuristic over Model exports.
        UExportTableItem model = ResolveLevelModel(package, packageBytes);
        return ParseModelExport(package, packageBytes, model);
    }

    // ULevel::Load immediately follows the Model reference with the serialized
    // ReachSpecs TArray (as in SurrealEngine ULevel.cpp:86-108; shipped ULevel keeps
    // the 0x1c-byte runtime entries at +0x8c).  FindPath(553) consumes these
    // original entries through each NavigationPoint.Paths[16].  Reading them
    // here keeps the gameplay graph sourced from the level's own .unr data.
    public static IReadOnlyList<LevelReachSpecRef> LoadReachSpecs(string mapPath)
    {
        (UnrealPackage package, byte[] packageBytes) = PackageOf(mapPath);
        UExportTableItem level = null;
        foreach (UExportTableItem export in package.Exports)
            if (string.Equals(export.Class?.ObjectName?.ToString(), "Level", StringComparison.OrdinalIgnoreCase))
            {
                if (level is not null) throw new InvalidDataException("multiple Level exports");
                level = export;
            }
        if (level is null) throw new InvalidDataException("Level export missing");
        if (level.SerialOffset < 0 || level.SerialSize <= 0
            || level.SerialOffset > packageBytes.Length - level.SerialSize)
            throw new InvalidDataException("Level export range");

        var r = new Reader(packageBytes.AsSpan(level.SerialOffset, level.SerialSize).ToArray());
        const ulong RfHasStack = 0x02000000UL;
        if (((ulong)level.ObjectFlags & RfHasStack) != 0)
        {
            int node = r.Index();
            r.Index();
            r.Skip(12);
            if (node != 0) r.Index();
        }
        string terminator = PackageName(package, r.Index());
        if (!terminator.Equals("None", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Level property terminator: " + terminator);

        int actorCount = CheckedCount(r.Int32(), "level actors");
        int actorMax = r.Int32();
        if (actorMax < actorCount)
            throw new InvalidDataException($"level actor max {actorMax} < count {actorCount}");
        for (int i = 0; i < actorCount; i++) r.Index();
        for (int i = 0; i < 4; i++) r.UString();
        int options = CheckedCount(r.Index(), "URL options");
        for (int i = 0; i < options; i++) r.UString();
        r.Skip(8);

        int modelIndex = r.Index();
        if (modelIndex <= 0 || modelIndex - 1 >= package.Exports.Count
            || !string.Equals(package.Exports[modelIndex - 1].Class?.ObjectName?.ToString(),
                "Model", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ULevel.Model reference " + modelIndex);

        int count = CheckedCount(r.Index(), "reach specs");
        var specs = new LevelReachSpecRef[count];
        for (int i = 0; i < count; i++)
        {
            int distance = r.Int32();
            int start = r.Index();
            int end = r.Index();
            int radius = r.Int32();
            int height = r.Int32();
            int flags = r.Int32();
            byte pruned = r.Byte();
            if (start <= 0 || start - 1 >= package.Exports.Count
                || end <= 0 || end - 1 >= package.Exports.Count)
                throw new InvalidDataException($"reach spec {i} endpoints {start}/{end}");
            specs[i] = new LevelReachSpecRef(distance, start, end, radius, height, flags, pruned);
        }

        // Shipped ULevel::Serialize continues after ReachSpecs (Engine.dll.c
        // 10155-10215): the saved 32.32 clock converted to float, one object
        // reference at +0x100, sixteen object references at +0x9c..+0xd8, and
        // for package version >=63 the TravelInfo TMap<string,string>. SurrealEngine's
        // loader stops at ReachSpecs, so use the shipped layout and
        // consume the tail rather than silently accepting a partial Level.
        r.Single();
        r.Index();
        for (int i = 0; i < 16; i++) r.Index();
        int travelCount = CheckedCount(r.Index(), "travel info");
        for (int i = 0; i < travelCount; i++)
        {
            r.UString();
            r.UString();
        }
        if (r.Remaining != 0)
            throw new InvalidDataException(
                $"Level export has {r.Remaining} unread bytes after ReachSpecs: {r.RemainingHex()}");
        return specs;
    }

    // A mover's Brush UModel (any Model export, 1-based index): the same
    // serialized layout as the level model, traced with the brush actor's
    // ToWorld coords by the coords-aware LineChecks.
    // Raw bytes of a map export by its number (1-based, like an object reference). Used by probes
    // that read an object INDEPENDENTLY of the model reader, e.g. a mover brush's UPolys
    // (UModel.PolysRef).
    // Whether the export carries a serialized FStateFrame (RF_HasStack): then the object body starts
    // AFTER it (Node, StateNode, ProbeMask int64, LatentAction int32, Offset when Node != None), as
    // for brush models below.
    public static bool ExportHasStack(string mapPath, int exportIndex)
    {
        (UnrealPackage package, _) = PackageOf(mapPath);
        if (exportIndex <= 0 || exportIndex - 1 >= package.Exports.Count)
            throw new InvalidDataException("export " + exportIndex);
        return ((ulong)package.Exports[exportIndex - 1].ObjectFlags & 0x02000000UL) != 0;
    }

    public static byte[] ExportBytes(string mapPath, int exportIndex)
    {
        (UnrealPackage package, byte[] packageBytes) = PackageOf(mapPath);
        if (exportIndex <= 0 || exportIndex - 1 >= package.Exports.Count)
            throw new InvalidDataException("export " + exportIndex);
        UExportTableItem export = package.Exports[exportIndex - 1];
        if (export.SerialOffset < 0 || export.SerialSize < 0
            || export.SerialOffset > packageBytes.Length - export.SerialSize)
            throw new InvalidDataException("export range " + exportIndex);
        return packageBytes.AsSpan(export.SerialOffset, export.SerialSize).ToArray();
    }

    // FPoly from a UPolys export (format v76, same as --probe-moverflags):
    // NumVertices (compact), Base/Normal/TextureU/TextureV, Vertex[n], PolyFlags,
    // Actor/Texture/ItemName/iLink/iBrushPoly (compact), PanU, PanV (int16).
    // The parse must consume the export to the last byte, otherwise it fails.
    public sealed class PolyRecord
    {
        public Vector3 Base, Normal, TextureU, TextureV;
        public Vector3[] Vertices;
        public int PolyFlags, Actor, iLink, iBrushPoly;
        public SurfMaterialRef Texture;
        public string ItemName;
        public short PanU, PanV;
    }

    public static List<PolyRecord> ReadPolys(string mapPath, int polysExport)
    {
        (UnrealPackage package, _) = PackageOf(mapPath);
        byte[] raw = ExportBytes(mapPath, polysExport);
        var r = new Reader(raw);
        if (ExportHasStack(mapPath, polysExport))
        {
            int node = r.Index(); r.Index(); r.Skip(12);
            if (node != 0) r.Index();
        }
        r.Index();                                 // property list: None
        int num = r.Int32(); r.Int32();            // Num, Max
        var list = new List<PolyRecord>(num);
        Vector3 V() => new Vector3(r.Single(), r.Single(), r.Single());
        for (int k = 0; k < num; k++)
        {
            int nv = r.Index();
            if (nv < 0 || nv > 16) throw new InvalidDataException($"UPolys {polysExport}: NumVertices {nv}");
            var p = new PolyRecord { Base = V(), Normal = V(), TextureU = V(), TextureV = V(), Vertices = new Vector3[nv] };
            for (int v = 0; v < nv; v++) p.Vertices[v] = V();
            // operator<<(FArchive&, FPoly&) on load (ArIsLoading, Ar+0x10) clears bit 0x10000000 of
            // PolyFlags (FPoly+0xf0) after reading the record: Engine.dll.c:115984-115986 (0x103bb142), the
            // same mask as FBspSurf below. The record is otherwise unchanged.
            p.PolyFlags = r.Int32() & ~0x10000000;
            p.Actor = r.Index();
            p.Texture = ResolveMaterialRef(package, r.Index());
            int nameIndex = r.Index();
            p.ItemName = nameIndex >= 0 && nameIndex < package.Names.Count ? package.Names[nameIndex].Name : throw new InvalidDataException($"UPolys {polysExport}: name {nameIndex}");
            p.iLink = r.Index(); p.iBrushPoly = r.Index();
            p.PanU = r.Int16(); p.PanV = r.Int16();
            list.Add(p);
        }
        if (r.Remaining != 0) throw new InvalidDataException($"UPolys {polysExport}: {r.Remaining} bytes not read");
        return list;
    }

    public static HeadlessBspModel LoadModelExport(string mapPath, int exportIndex)
    {
        (UnrealPackage package, byte[] packageBytes) = PackageOf(mapPath);
        if (exportIndex <= 0 || exportIndex - 1 >= package.Exports.Count)
            throw new InvalidDataException("brush model export " + exportIndex);
        UExportTableItem export = package.Exports[exportIndex - 1];
        if (!string.Equals(export.Class?.ObjectName?.ToString(), "Model", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"export {exportIndex} is not a Model: {export.ObjectName}");
        return ParseModelExport(package, packageBytes, export);
    }

    private static HeadlessBspModel ParseModelExport(UnrealPackage package, byte[] packageBytes,
        UExportTableItem model)
    {
        if (model.SerialOffset < 0 || model.SerialSize < 0
            || model.SerialOffset > packageBytes.Length - model.SerialSize)
            throw new InvalidDataException("Model export range");
        byte[] data = packageBytes.AsSpan(model.SerialOffset, model.SerialSize).ToArray();
        var r = new Reader(data);

        // Brush models can carry RF_HasStack: skip the serialized FStateFrame
        // (Node, StateNode, ProbeMask int64, LatentAction int32, Offset when
        // Node != None) — same layout LoadModelBounds skips.
        const ulong RfHasStack = 0x02000000UL;
        if (((ulong)model.ObjectFlags & RfHasStack) != 0)
        {
            int node = r.Index();
            r.Index();  // StateNode
            r.Skip(12); // ProbeMask + LatentAction
            if (node != 0) r.Index(); // Offset
        }
        string terminator = PackageName(package, r.Index());
        if (!terminator.Equals("None", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Model property terminator: " + terminator);

        r.Skip(25 + 16); // UPrimitive bounds + sphere, HP1 version 76
        // Vectors: the DIRECTION table referenced by surf.vNormal / vTextureU / vTextureV. Tracing
        // only needs node planes, but without this table every surface's normal and texture axes are
        // dangling indices and no render consumer can be built.
        int vectorCount = CheckedCount(r.Index(), "vectors");
        var vectors = new Vector3[vectorCount];
        for (int i = 0; i < vectorCount; i++)
            vectors[i] = new Vector3(r.Single(), r.Single(), r.Single());
        int pointCount = CheckedCount(r.Index(), "points");
        var points = new Vector3[pointCount];
        for (int i = 0; i < pointCount; i++)
            points[i] = new Vector3(r.Single(), r.Single(), r.Single());

        int nodeCount = CheckedCount(r.Index(), "nodes");
        var planes = new Plane[nodeCount];
        var nodes = new Node[nodeCount];
        // ZoneMask and iRenderBound are render visibility inputs (URender::OccludeBsp: node zone mask
        // and its render box). Collision does not use them.
        var nodeZoneMask = new ulong[nodeCount];
        var nodeRenderBound = new int[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            planes[i] = new Plane(r.Single(), r.Single(), r.Single(), r.Single());
            nodeZoneMask[i] = r.UInt64();   // ZoneMask
            // operator<<(FArchive&, FBspNode&) on load clears the high NodeFlags bits (NF_IsNew 0x20,
            // NF_IsFront 0x40, NF_IsBack 0x80: editor and FMovingBrushTracker scratch bits): disasm Engine
            // 0x103bb319-0x103bb323 `test [Ar+0x10]; and byte ptr [Node+0x37], 0x1f`. Campaign data does
            // contain them (1136 nodes, e.g. Lev_Tut2: 29).
            int nodeFlags = r.Byte() & 0x1f;
            int vertPool = r.Index();
            int surf = r.Index();
            int back = r.Index();      // serialized Back before Front
            int front = r.Index();
            int planeLink = r.Index(); // iPlane: coplanar polygon chain
            int collision = r.Index();
            nodeRenderBound[i] = r.Index();   // iRenderBound
            // iZone[0..1] are read as a BYTE (0x103bb2da/0x103bb2ea, `push 1`), not a compact index; for
            // zones 0..63 the bytes are identical.
            int zone0 = r.Byte();
            int zone1 = r.Byte();
            int numVertices = r.Byte();
            int leaf0 = r.Int32();
            int leaf1 = r.Int32();
            nodes[i] = new Node(front, back, collision, surf, zone0, zone1, leaf0, leaf1,
                planeLink, vertPool, numVertices, nodeFlags);
        }

        int surfCount = CheckedCount(r.Index(), "surfaces");
        var surfFlags = new int[surfCount];
        var surfMaterial = new SurfMaterialRef[surfCount];
        var surfs = new BspSurfRecord[surfCount];
        for (int i = 0; i < surfCount; i++)
        {
            // FBspSurf (v76): Texture, PolyFlags, pBase, vNormal, vTextureU,
            // vTextureV, iLightMap, iBrushPoly (6 compact indices), PanU/PanV
            // (two int16), Actor (object ref). The earlier "7 indices +
            // 4-byte skip" reading consumed identical bytes on the level
            // model only because every level surf's Actor is a 2-byte index;
            // brush models (Actor None/1-byte) exposed the misalignment.
            //
            // The six indices, PanU/PanV and Actor are retained in full (collision needs only PolyFlags
            // and material): without pBase/vNormal/vTextureU/vTextureV no texture coordinate can be built,
            // without iLightMap no lightmap binding, and without Actor no polygon-to-mover-brush ownership.
            int material = r.Index();          // BspSurface.Material (Texture ref)
            surfMaterial[i] = ResolveMaterialRef(package, material);
            // operator<<(FArchive&, FBspSurf&) on load clears bit 0x10000000
            // (Engine 0x103baee4 `and eax, 0xefffffff`; FPoly — 0x103bb142).
            // The bit never occurs in campaign data (109281 surfaces, 41 maps); kept to match the source.
            surfFlags[i] = r.Int32() & ~0x10000000;
            int pBase = r.Index();
            int vNormal = r.Index();
            int vTextureU = r.Index();
            int vTextureV = r.Index();
            int iLightMap = r.Index();
            int iBrushPoly = r.Index();
            short panU = r.Int16();
            short panV = r.Int16();
            int brushActor = r.Index();
            surfs[i] = new BspSurfRecord(material, surfFlags[i], pBase, vNormal,
                vTextureU, vTextureV, iLightMap, iBrushPoly, panU, panV, brushActor);
        }

        int verts = CheckedCount(r.Index(), "verts");
        var vertices = new int[verts];
        var vertSides = new int[verts];
        // Side is the second half of FVert. Retained too: it names the edge's neighbour node;
        // without it the polygon topology is incomplete.
        for (int i = 0; i < verts; i++) { vertices[i] = r.Index(); vertSides[i] = r.Index(); }
        int numSharedSides = r.Int32();
        int zones = CheckedCount(r.Int32(), "zones");
        var zoneActors = new int[zones];
        var zoneConnectivity = new ulong[zones];
        var zoneVisibility = new ulong[zones];
        for (int i = 0; i < zones; i++)
        {
            zoneActors[i] = r.Index();
            zoneConnectivity[i] = r.UInt64();
            zoneVisibility[i] = r.UInt64();
        }
        // The model's reference to its UPolys. Collision does not need it, but for a MOVER BRUSH the
        // Polys are what shipped draws (SurrealEngine too: VisibleBrush walks brush->Polys and reads
        // FPoly.PolyFlags), so it is retained.
        int polysRef = r.Index();

        // FLightMapIndex (ULevel.cpp:325-343): DataOffset, Pan (FVector),
        // UClamp/VClamp (compact), UScale/VScale, LightActors. Collision does not need light; retained
        // for the render consumer: without DataOffset/Clamp/Scale a surface lightmap cannot be addressed,
        // and Pan sets its position in the world.
        int lightMaps = CheckedCount(r.Index(), "light maps");
        var lightMapIndices = new BspLightMapIndex[lightMaps];
        for (int i = 0; i < lightMaps; i++)
        {
            int dataOffset = r.Int32();
            var pan = new Vector3(r.Single(), r.Single(), r.Single());
            int uClamp = r.Index();
            int vClamp = r.Index();
            float uScale = r.Single();
            float vScale = r.Single();
            int lightActors = r.Int32();
            lightMapIndices[i] = new BspLightMapIndex(dataOffset, pan, uClamp, vClamp,
                uScale, vScale, lightActors);
        }
        int lightBitCount = CheckedCount(r.Index(), "light bits");
        var lightBits = new byte[lightBitCount];
        for (int i = 0; i < lightBitCount; i++) lightBits[i] = r.Byte();
        // FBox (Min, Max, IsValid byte): node render boxes (iRenderBound).
        int boundCount = CheckedCount(r.Index(), "bounds");
        var bounds = new BspBox[boundCount];
        for (int i = 0; i < boundCount; i++)
            bounds[i] = new BspBox(new Vector3(r.Single(), r.Single(), r.Single()),
                new Vector3(r.Single(), r.Single(), r.Single()), r.Byte());

        int hullCount = CheckedCount(r.Index(), "leaf hulls");
        var hulls = new int[hullCount];
        for (int i = 0; i < hullCount; i++) hulls[i] = r.Int32();

        // Tail order per the serialized UModel: Leaves, Lights, RootOutside.
        int leafCount = CheckedCount(r.Index(), "leaves");
        var leaves = new BspLeaf[leafCount];
        for (int i = 0; i < leafCount; i++)
            leaves[i] = new BspLeaf(r.Index(), r.Index(), r.Index(), r.UInt64());
        int lightCount = CheckedCount(r.Index(), "lights");
        var lights = new int[lightCount];
        for (int i = 0; i < lightCount; i++) lights[i] = r.Index();
        int rootOutside = r.Int32();
        r.Int32(); // Linked (final UModel field, ULevel.cpp:392)

        // Independent parser self-check: the parse MUST
        // consume exactly the serialized Model export — every field read, ending
        // at SerialSize. Any wrong offset/index/count anywhere leaves the reader
        // mis-aligned and the residual non-zero, catching the whole class of
        // parser bugs without a second reader. (SurrealEngine's own loader can't
        // serve as that reader — it doesn't support HP1's v76 package/class format.)
        if (r.Remaining != 0)
            throw new InvalidDataException(
                $"Model parse did not consume the full export: {r.Remaining} bytes left of {model.SerialSize}");

        var parseTables = new BspParseTables(vectorCount, numSharedSides,
            zoneConnectivity, zoneVisibility, leaves, vectors, surfs, vertSides,
            lightMapIndices, lightBits, lights)
        {
            PolysRef = polysRef, NodeZoneMask = nodeZoneMask,
            NodeRenderBound = nodeRenderBound, Bounds = bounds,
        };
        return new HeadlessBspModel(planes, nodes, hulls, surfFlags, surfMaterial, zoneActors, points, vertices,
            rootOutside, parseTables);
    }

    // Walk an object reference to a usable texture source: a positive index is
    // a map-embedded texture export; a negative one is an import whose outer
    // chain ends at the source package (the .utx file) with the group between.
    // Public: the mover polygon extractor (FPoly.Texture) uses the same reference resolution: a
    // mover brush and a BSP surface share one texture identity; there must be no second way to
    // name it.
    public static SurfMaterialRef ResolveMaterialRef(UnrealPackage package, int index)
    {
        if (index == 0) return null;
        if (index > 0)
        {
            if (index - 1 >= package.Exports.Count)
                throw new InvalidDataException("surface material export " + index);
            return new SurfMaterialRef(package.Exports[index - 1].ObjectName.ToString(), null, null, index);
        }
        if (-index - 1 >= package.Imports.Count)
            throw new InvalidDataException("surface material import " + index);
        var import = package.Imports[-index - 1];
        string name = import.ObjectName.ToString();
        var outers = new List<string>();
        int outer = (int)import.OuterIndex;
        while (outer < 0)
        {
            if (-outer - 1 >= package.Imports.Count)
                throw new InvalidDataException("surface material outer " + outer);
            var o = package.Imports[-outer - 1];
            outers.Add(o.ObjectName.ToString());
            outer = (int)o.OuterIndex;
        }
        if (outers.Count == 0)
            throw new InvalidDataException("surface material import without a package: " + name);
        string sourcePackage = outers[^1];
        string group = outers.Count > 1 ? outers[0] : null;
        // The reference CLASS is PART OF THE IDENTITY, not decoration. UE1 resolves an import by
        // (class, name, outer chain); a name-only lookup takes the first namesake found. Found by
        // MEASUREMENT over the whole map (`--dump-texfacts Lev_Tut1`): the reference `Editor..Bad` has
        // no group, and Editor.utx has several exports with that name.
        return new SurfMaterialRef(name, group, sourcePackage, 0, outers.ToArray(),
            import.ClassName.ToString());
    }

    private static float FloatBits(int bits) => BitConverter.Int32BitsToSingle(bits);
    private static int CheckedCount(int count, string label)
    { if (count < 0 || count > 10_000_000) throw new InvalidDataException($"invalid {label}: {count}"); return count; }
    private static string PackageName(UnrealPackage package, int index)
        => index >= 0 && index < package.Names.Count ? package.Names[index].Name.ToString()
            : throw new InvalidDataException("invalid package name index " + index);

    private sealed class Reader
    {
        private readonly byte[] _data; private int _position;
        public Reader(byte[] data) => _data = data ?? throw new ArgumentNullException(nameof(data));
        public void Skip(int count) { if (count < 0 || _position > _data.Length - count) throw new EndOfStreamException(); _position += count; }
        public int Int32() { Need(4); int v = BitConverter.ToInt32(_data, _position); _position += 4; return v; }
        public short Int16() { Need(2); short v = BitConverter.ToInt16(_data, _position); _position += 2; return v; }
        public float Single() { Need(4); float v = BitConverter.ToSingle(_data, _position); _position += 4; return v; }
        public ulong UInt64() { Need(8); ulong v = BitConverter.ToUInt64(_data, _position); _position += 8; return v; }
        public int Index()
        {
            int first = Byte(); bool negative = (first & 0x80) != 0; int value = first & 0x3f;
            if ((first & 0x40) != 0) { int shift = 6; for (int i = 0; i < 4; i++) { int b = Byte(); value |= (b & 0x7f) << shift; if ((b & 0x80) == 0) break; shift += 7; } }
            return negative ? -value : value;
        }
        public byte Byte() { Need(1); return _data[_position++]; }
        // UE1 serialized string: compact length (including the NUL) + bytes.
        public void UString() { int len = Index(); if (len < 0) throw new InvalidDataException("negative string length"); Skip(len); }
        public int Remaining => _data.Length - _position;
        // Convert.ToHexString is .NET 5+; HP1.Core targets the
        // netstandard2.1 profile, so spell the uppercase hex out by hand.
        public string RemainingHex()
        {
            var sb = new System.Text.StringBuilder((_data.Length - _position) * 2);
            for (int i = _position; i < _data.Length; i++) sb.Append(_data[i].ToString("X2"));
            return sb.ToString();
        }
        private void Need(int count) { if (_position > _data.Length - count) throw new EndOfStreamException(); }
    }
}
}
