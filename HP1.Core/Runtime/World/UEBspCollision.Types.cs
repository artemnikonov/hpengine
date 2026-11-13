// Value types of the BSP model and its queries: boxes, leaves, reach specs, hits, brush bounds,
// surface material references, parse tables, surface/lightmap records, BspHit and BspRegion.
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
// Serialized ConvexVolumeLeaf (ULevel.cpp Leaves loop): Zone/Permeating/
// Volumetric indices plus the VisibleZones bitmask.
public readonly struct BspBox
{
    public Vector3 Min { get; }
    public Vector3 Max { get; }
    public byte IsValid { get; }
    public BspBox(Vector3 Min, Vector3 Max, byte IsValid) { this.Min = Min; this.Max = Max; this.IsValid = IsValid; }
}

public readonly struct BspLeaf
{
    public int Zone { get; }
    public int Permeating { get; }
    public int Volumetric { get; }
    public ulong VisibleZones { get; }
    public BspLeaf(int Zone, int Permeating, int Volumetric, ulong VisibleZones)
    {
        this.Zone = Zone;
        this.Permeating = Permeating;
        this.Volumetric = Volumetric;
        this.VisibleZones = VisibleZones;
    }
}

// Direct serialized ULevel ReachSpec. Endpoints remain signed package/export
// indices until the complete actor graph has been constructed and can resolve
// them to stable UEObjects.
public readonly struct LevelReachSpecRef
{
    public int Distance { get; }
    public int StartExport { get; }
    public int EndExport { get; }
    public int CollisionRadius { get; }
    public int CollisionHeight { get; }
    public int ReachFlags { get; }
    public byte Pruned { get; }
    public LevelReachSpecRef(int Distance, int StartExport, int EndExport, int CollisionRadius, int CollisionHeight, int ReachFlags, byte Pruned)
    {
        this.Distance = Distance;
        this.StartExport = StartExport;
        this.EndExport = EndExport;
        this.CollisionRadius = CollisionRadius;
        this.CollisionHeight = CollisionHeight;
        this.ReachFlags = ReachFlags;
        this.Pruned = Pruned;
    }
}

public readonly struct ActorHit
{
    public bool Blocked { get; }
    public float Time { get; }
    public Vector3 Location { get; }
    public Vector3 Normal { get; }
    public ActorHit(bool Blocked, float Time, Vector3 Location, Vector3 Normal)
    {
        this.Blocked = Blocked;
        this.Time = Time;
        this.Location = Location;
        this.Normal = Normal;
    }
}

// Serialized UPrimitive header bounds of a Model export (a mover's brush):
// the model-space FBox and FSphere, straight from the .unr.
public readonly struct BrushBounds
{
    public Vector3 BoxMin { get; }
    public Vector3 BoxMax { get; }
    public Vector3 SphereCenter { get; }
    public float SphereRadius { get; }
    public BrushBounds(Vector3 BoxMin, Vector3 BoxMax, Vector3 SphereCenter, float SphereRadius)
    {
        this.BoxMin = BoxMin;
        this.BoxMax = BoxMax;
        this.SphereCenter = SphereCenter;
        this.SphereRadius = SphereRadius;
    }
}

// A BSP surface's material reference: texture name plus its source package and
// group for imports, or the local export index for map-embedded textures.
public sealed class SurfMaterialRef
{
    public string Name { get; }
    public string Group { get; }
    public string SourcePackage { get; }
    public int LocalExport { get; }
    // FULL outer chain, from the immediate outer to the package. Assembling the path as SourcePackage + Group +
    // Name LOSES intermediate levels: HPParticle.hp_fx.General.LogShadow would print as
    // HPParticle.General.LogShadow.
    //
    // Measured over all 41 maps: 6033 imports with an outer, 50 of them deeper than three levels.
    // The texture data reader (UETextureFactsReader.FindExport) matches the FULL outer chain and
    // the reference class, not just the immediate outer: the 41-map measurement found 21
    // group-less references where a name-only lookup picked a namesake.
    public string[] Outers { get; }

    // Class the reference is declared with in the import table (Texture, FireTexture,
    // WetTexture...). UE1 resolves an import by (class, name, outers); without the class a name
    // lookup takes a namesake of another class (see Editor..Bad).
    public string ClassName { get; }

    public SurfMaterialRef(string Name, string Group, string SourcePackage, int LocalExport)
        : this(Name, Group, SourcePackage, LocalExport, null, null) { }
    public SurfMaterialRef(string Name, string Group, string SourcePackage, int LocalExport,
        string[] Outers)
        : this(Name, Group, SourcePackage, LocalExport, Outers, null) { }
    public SurfMaterialRef(string Name, string Group, string SourcePackage, int LocalExport,
        string[] Outers, string ClassName)
    {
        this.Name = Name;
        this.Group = Group;
        this.SourcePackage = SourcePackage;
        this.LocalExport = LocalExport;
        this.Outers = Outers;
        this.ClassName = ClassName;
    }

    // OUTER CHAIN THE REFERENCE RESOLVES BY: from the immediate outer to the package, WITHOUT the
    // package itself. Single expression shared by export lookup (UETextureFactsReader.FindExport)
    // and cache keys, so the two cannot diverge on a reference where only one of Outers/Group is
    // filled.
    public string[] OuterChain => Outers is { Length: > 0 }
        ? Outers[..^1]
        : (Group is null ? System.Array.Empty<string>() : new[] { Group });

    // IDENTITY KEY: EXACTLY what the reference resolves by, nothing more.
    //
    // Texture data caches must not key on the truncated `Package.Group.Name.LocalExport` form,
    // since export lookup matches
    // (package, CLASS, name, FULL outer chain). A key diverging from
    // (package, CLASS, name, FULL outer chain). A key diverging from the declared identity is a
    // defect that is real in UE1 data:
    // exists: `Editor..Bad` is BOTH a palette AND a texture, i.e. a name with
    // (`Editor..Bad` is BOTH a palette and a texture: a name with an empty chain is told apart only
    // by class).
    //
    // A local export resolves itself: FindExport returns the exact table row and neither class
    // nor chain is consulted. The native-object key `Path` would lose it: for an embedded texture
    // Path is "export:Leaf", and a package may hold any number of equal leaf names with
    // different outers.
    public string IdentityKey => LocalExport > 0
        ? $"{SourcePackage ?? "<map>"}#{LocalExport}"
        : $"{SourcePackage ?? "<map>"}|{ClassName ?? "<?>"}|"
          + string.Join(".", OuterChain) + "|" + Name;

    // Canonical object path: Package.Group...Group.Leaf for an import, export:Leaf for a texture
    // embedded in the map itself. FOR HUMANS: diagnostics and messages; identity is IdentityKey
    // above.
    // Built once (allocation only: the class is immutable; the per-tick DrawnTexturesOf asked for it per drawn polygon).
    public string Path => _path ??= BuildPath();
    string _path;
    string BuildPath()
    {
        if (LocalExport > 0) return "export:" + Name;
        if (Outers == null || Outers.Length == 0) return Name;
        var sb = new System.Text.StringBuilder();
        for (int i = Outers.Length - 1; i >= 0; i--) sb.Append(Outers[i]).Append('.');
        return sb.Append(Name).ToString();
    }
}

// Parse-only retention for the independent parser diff: sections the runtime
// queries never touch but the .unr serializes through the same reader.
public sealed class BspParseTables
{
    public int VectorCount { get; }
    public int NumSharedSides { get; }
    public ulong[] ZoneConnectivity { get; }
    public ulong[] ZoneVisibility { get; }
    public BspLeaf[] Leaves { get; }
    // Fields collision does not need but static render geometry cannot be built without; the
    // oracle diff compares them byte for byte (a skipped byte and a correctly read one look the same
    // as long as the tail lines up).
    public Vector3[] Vectors { get; }
    public BspSurfRecord[] Surfs { get; }
    public int[] VertSides { get; }
    public BspLightMapIndex[] LightMaps { get; }
    public byte[] LightBits { get; }
    public int[] Lights { get; }
    // Raw UModel.Polys reference (positive: an export of the same map).
    public int PolysRef { get; init; }
    // Render visibility inputs (URender::OccludeBsp): per node ZoneMask and iRenderBound, plus the
    // Bounds table (FBox, 0x1c in shipped memory).
    public ulong[] NodeZoneMask { get; init; }
    public int[] NodeRenderBound { get; init; }
    public BspBox[] Bounds { get; init; }
    public BspParseTables(int VectorCount, int NumSharedSides,
        ulong[] ZoneConnectivity, ulong[] ZoneVisibility, BspLeaf[] Leaves,
        Vector3[] Vectors, BspSurfRecord[] Surfs, int[] VertSides,
        BspLightMapIndex[] LightMaps, byte[] LightBits, int[] Lights)
    {
        this.VectorCount = VectorCount;
        this.NumSharedSides = NumSharedSides;
        this.ZoneConnectivity = ZoneConnectivity;
        this.ZoneVisibility = ZoneVisibility;
        this.Leaves = Leaves;
        this.Vectors = Vectors;
        this.Surfs = Surfs;
        this.VertSides = VertSides;
        this.LightMaps = LightMaps;
        this.LightBits = LightBits;
        this.Lights = Lights;
    }
}

// Full FBspSurf, exactly in serialization order (ULevel.cpp:280-296). References are kept as RAW
// indices: resolving them here would decide for the consumer, and the oracle diff must compare
// exactly what is in the file.
public sealed class BspSurfRecord
{
    public int Material { get; }
    public int PolyFlags { get; }
    public int PBase { get; }
    public int VNormal { get; }
    public int VTextureU { get; }
    public int VTextureV { get; }
    public int LightMap { get; }
    public int BrushPoly { get; }
    public short PanU { get; }
    public short PanV { get; }
    public int BrushActor { get; }
    public BspSurfRecord(int Material, int PolyFlags, int PBase, int VNormal,
        int VTextureU, int VTextureV, int LightMap, int BrushPoly,
        short PanU, short PanV, int BrushActor)
    {
        this.Material = Material;
        this.PolyFlags = PolyFlags;
        this.PBase = PBase;
        this.VNormal = VNormal;
        this.VTextureU = VTextureU;
        this.VTextureV = VTextureV;
        this.LightMap = LightMap;
        this.BrushPoly = BrushPoly;
        this.PanU = PanU;
        this.PanV = PanV;
        this.BrushActor = BrushActor;
    }
}

// FLightMapIndex (ULevel.cpp:325-343).
public sealed class BspLightMapIndex
{
    public int DataOffset { get; }
    public Vector3 Pan { get; }
    public int UClamp { get; }
    public int VClamp { get; }
    public float UScale { get; }
    public float VScale { get; }
    public int LightActors { get; }
    public BspLightMapIndex(int DataOffset, Vector3 Pan, int UClamp, int VClamp,
        float UScale, float VScale, int LightActors)
    {
        this.DataOffset = DataOffset;
        this.Pan = Pan;
        this.UClamp = UClamp;
        this.VClamp = VClamp;
        this.UScale = UScale;
        this.VScale = VScale;
        this.LightActors = LightActors;
    }
}

public readonly struct BspHit
{
    public bool Blocked { get; }
    public float Fraction { get; }
    public Vector3 Normal { get; }
    public int Node { get; }
    // FCheckResult.Location as the LineCheck tail writes it (zero extent: Start + f32(D·t) with t in the register;
    // extent: Start + f32(D·Time)), not a value rebuilt by the caller from the float Fraction.
    public Vector3 Location { get; }
    public BspHit(bool Blocked, float Fraction, Vector3 Normal, int Node, Vector3 Location)
    {
        this.Blocked = Blocked;
        this.Fraction = Fraction;
        this.Normal = Normal;
        this.Node = Node;
        this.Location = Location;
    }
    public static BspHit None => new(false, 1f, Vector3.zero, -1, Vector3.zero);
}

public readonly struct BspRegion
{
    public int ZoneNumber { get; }
    public int Leaf { get; }
    public int ZoneActorIndex { get; }
    public BspRegion(int ZoneNumber, int Leaf, int ZoneActorIndex)
    {
        this.ZoneNumber = ZoneNumber;
        this.Leaf = Leaf;
        this.ZoneActorIndex = ZoneActorIndex;
    }
    public static BspRegion None => new(0, -1, 0);
}
}
