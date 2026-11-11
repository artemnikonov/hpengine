// Meshes and animations for the frontend, built from the user's installed packages.
//
// The data is built in memory over the Core catalog (Hp1SkeletalMeshCatalog, which already reads the installed
// packages for gameplay natives). Derived copies of original data cannot ship in the build. The headless exports
// (--export-raw-meshes, --exportanims, --export-vertex-meshes) serialize the result from here.
//
// Address = FULL object path (Package.Group.Name, like Actor.Mesh); there is no leaf-name lookup.
using System;
using System.Collections.Generic;
using System.Linq;

namespace HP1.UEScript
{
    // Raw skeletal mesh in frontend store units (field names match what HP1SkinnedBank reads).
    // Differences from the package:
    //   * wedgeU/V: FMeshWedge stores a byte 0..255, here byte/256 (DrawLodMesh scale 1/256, Render 0x10b3865c);
    //   * weightValue: FBoneWeight stores uint16, here a 0..1 fraction;
    //   * faces are stably sorted by material (store order);
    //   * the root is marked -1 (the package stores a self-reference, 0; the skinner accepts both);
    //   * rootConvention="game": reference pose exactly as in the package.
    public sealed class UERawSkeletalMesh
    {
        public string name, pkg, objectPath, rootConvention;
        public int numPoints; public float[] points;
        public int numWedges; public int[] wedgeVertex; public float[] wedgeU, wedgeV;
        public int numTris; public int[] triWedge, triMaterial;
        public int numBones; public string[] boneNames; public int[] boneParents;
        public float[] boneOrientation, bonePosition, boneScale, boneLength; public int[] boneFlags;
        public int numWeights; public int[] weightPointIndex; public float[] weightValue;
        public int[] weightIdxStart, weightIdxCount;
        public int numLocalPoints; public float[] localPoints;
        public string[] textures, texturePkgs; public int[] materialFlags, materialTexIdx;
        public float[] origin, scale; public int[] rotationOrigin;
        public int weaponBoneIdx; public float[] weaponAdjustOrigin, weaponAdjustAxis;
        public string animObject;          // UMesh -> Animation object (not in the store: it addresses the bank)

        // The same mesh with another face list (UEMeshSource.LodVariant); every other field is shared.
        internal UERawSkeletalMesh WithFaces(int[] tri, int[] material)
        {
            var c = (UERawSkeletalMesh)MemberwiseClone();
            c.numTris = material.Length; c.triWedge = tri; c.triMaterial = material;
            return c;
        }
    }

    // Animation bank in store basis: MIRROR X+Y (position (-x, -y, z), quaternion (-x, -y, z, w)),
    // which the frontend reader (HP1SkinnedBank.FlattenTracks) removes for every bank. Measured with
    // validate_animseqs.py: the whole store is in this basis; the raw basis was a frontend data defect
    // (the cauldron fell through the floor).
    public sealed class UEAnimBank
    {
        public string pkg, name;
        public string[] boneNames; public int[] boneParents;
        public UEAnimBankSequence[] sequences;
    }
    public sealed class UEAnimBankSequence
    {
        public string name, group; public float rate; public int startFrame, frameCount;
        public UEAnimBankTrack[] tracks; public UEAnimBankNotify[] notifications;
    }
    public sealed class UEAnimBankTrack { public int numQ, numP, numT; public double[] r, p; public int[] t; }
    public sealed class UEAnimBankNotify { public double time; public string func; }

    // Vertex mesh (Mesh/LodMesh). Pool vertices are in package units (unpacked 11/11/10);
    // the consumer applies scale; wedgeU/V is a 0..1 fraction.
    public sealed class UERawVertexMesh
    {
        public string name, pkg, objectPath;
        public int frameVerts, animFrames, numVerts; public float[] verts;
        public int numWedges; public int[] wedgeVertex; public float[] wedgeU, wedgeV;
        public int numTris; public int[] triWedge, triMaterial, materialFlags, materialTexIdx;
        public string[] textures;
        public int[] connectNumTris, connectOffset, vertLinks, specialFaceWedges, specialFaceMaterial;
        public int modelVerts, specialVerts;
        public float meshScaleMax, lodHysteresis, lodStrength, lodMorph, lodZDisplace;
        public int lodMinVerts; public int[] remapAnimVerts; public int oldFrameVerts;
        public float[] origin, scale; public int[] rotationOrigin;
        public string[] seqNames; public int[] seqStart, seqFrames; public float[] seqRate;
    }

    public sealed class UEMeshSource
    {
        public sealed class Entry
        {
            public string Path, Class, Package, Leaf;
            internal Hp1SkeletalMeshCatalog.PackageFile File;
            internal PackageExport Export;
        }

        readonly Hp1SkeletalMeshCatalog _catalog;
        readonly Dictionary<string, Entry> _byPath = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, UERawSkeletalMesh> _raw = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, UERawVertexMesh> _vertex = new(StringComparer.OrdinalIgnoreCase);
        readonly object _gate = new object();

        public UEMeshSource(Hp1SkeletalMeshCatalog catalog)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            foreach (var grp in catalog.SkeletalExportsGrouped().Concat(catalog.VertexExportsGrouped()))
                foreach (var (pkg, export) in grp.Value)
                {
                    int idx = Array.IndexOf(pkg.Exports, export);
                    string full = idx >= 0 ? pkg.ObjectPath(idx) : pkg.PackageName + "." + export.Name;
                    if (_byPath.ContainsKey(full)) continue;       // skeletal and vertex registries share paths
                    _byPath[full] = new Entry
                    {
                        Path = full, Class = pkg.ClassName(export), Package = pkg.PackageName,
                        Leaf = export.Name, File = pkg, Export = export,
                    };
                }
        }

        public IEnumerable<Entry> All => _byPath.Values;

        public Entry Find(string path) => path != null && _byPath.TryGetValue(path, out Entry e) ? e : null;

        public UERawSkeletalMesh Skeletal(string path)
        {
            lock (_gate)
            {
                if (_raw.TryGetValue(path ?? "", out var cached)) return cached;
                Entry e = Find(path);
                if (e == null || !e.Class.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase)) return null;
                Hp1SkeletalMesh m = _catalog.Get(e.Leaf, e.Path);
                UERawSkeletalMesh r = m == null ? null : BuildRaw(e.Path, e.Package, e.Leaf, m);
                _raw[path] = r;
                return r;
            }
        }

        public UERawVertexMesh Vertex(string path)
        {
            lock (_gate)
            {
                if (_vertex.TryGetValue(path ?? "", out var cached)) return cached;
                Entry e = Find(path);
                if (e == null || !(e.Class.Equals("Mesh", StringComparison.OrdinalIgnoreCase)
                                   || e.Class.Equals("LodMesh", StringComparison.OrdinalIgnoreCase))) return null;
                Hp1VertexMesh m = _catalog.GetVertexMesh(e.Leaf, e.Path);
                var info = e.File.ReadMeshAnimInfo(e.Export);
                UERawVertexMesh r = m == null ? null : BuildVertex(e.Path, e.Package, e.Leaf, m, info);
                _vertex[path] = r;
                return r;
            }
        }

        // ORIGINAL VISUALS (D-62): the faces URender::DrawLodMesh draws at the LOD level `verts` (WorldFrame MeshPose.LodParts,
        // UEMeshLod.CollapsedFaces) over the frontend mesh `raw` of `path` (skeletal, or a vertex mesh in the same shape): kept
        // faces in source order, wedges collapsed. Cached per (raw, verts).
        readonly Dictionary<(UERawSkeletalMesh, int), UERawSkeletalMesh> _lod = new();
        public UERawSkeletalMesh LodVariant(string path, UERawSkeletalMesh raw, int verts)
        {
            lock (_gate)
            {
                if (_lod.TryGetValue((raw, verts), out var hit)) return hit;
                Entry e = Find(path) ?? throw new InvalidOperationException("LodVariant: no mesh " + path);
                Hp1MeshLod lod; int[] faceWedges, faceMaterial, wedgePoint;
                if (e.Class.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
                {
                    Hp1SkeletalMesh m = _catalog.Get(e.Leaf, e.Path);
                    (lod, faceWedges, faceMaterial, wedgePoint) = (m.Lod, m.FaceWedges, m.FaceMaterial, m.WedgePoint);
                }
                else
                {
                    Hp1VertexMesh m = _catalog.GetVertexMesh(e.Leaf, e.Path);
                    (lod, faceWedges, faceMaterial, wedgePoint) = (m.Lod, m.FaceWedges, m.FaceMaterial, m.WedgePoint);
                }
                var kept = new List<int>();
                int[] tri = UEMeshLod.CollapsedFaces(lod, faceWedges, wedgePoint, verts, kept);
                return _lod[(raw, verts)] = raw.WithFaces(tri, kept.ConvertAll(f => faceMaterial[f]).ToArray());
            }
        }

        // Mesh Textures[] table (UMesh::GetTexture by material TextureIndex), full paths;
        // an empty string is a hole in the table (data, not an omission).
        public string[] SlotTextures(string path)
        {
            lock (_gate)
            {
                Entry e = Find(path);
                if (e == null) return null;
                if (e.Class.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
                    return _catalog.Get(e.Leaf, e.Path)?.TexturePath;
                return _catalog.GetVertexMesh(e.Leaf, e.Path)?.TexturePath;
            }
        }

        // ── export bodies (moved unchanged from HeadlessSmoke) ─────────

        public static UERawSkeletalMesh BuildRaw(string objectPath, string pkgName, string leaf, Hp1SkeletalMesh m)
        {
            int nb = m.Bones.Length;
            var r = new UERawSkeletalMesh
            {
                name = leaf, pkg = pkgName, objectPath = objectPath, rootConvention = "game",
                animObject = m.AnimObject,
                numBones = nb, boneNames = new string[nb], boneParents = new int[nb],
                boneOrientation = new float[nb * 4], bonePosition = new float[nb * 3],
                boneScale = new float[nb * 3], boneLength = new float[nb], boneFlags = new int[nb],
            };
            for (int i = 0; i < nb; i++)
            {
                Hp1Bone b = m.Bones[i];
                r.boneNames[i] = b.Name;
                r.boneParents[i] = b.ParentIndex;
                r.boneOrientation[i * 4] = b.OrientX; r.boneOrientation[i * 4 + 1] = b.OrientY;
                r.boneOrientation[i * 4 + 2] = b.OrientZ; r.boneOrientation[i * 4 + 3] = b.OrientW;
                r.bonePosition[i * 3] = b.Position.x; r.bonePosition[i * 3 + 1] = b.Position.y; r.bonePosition[i * 3 + 2] = b.Position.z;
                r.boneScale[i * 3] = b.Scale.x; r.boneScale[i * 3 + 1] = b.Scale.y; r.boneScale[i * 3 + 2] = b.Scale.z;
                r.boneLength[i] = b.Length;
                r.boneFlags[i] = unchecked((int)b.Flags);
            }
            // Root is -1, as in the rest of the store (the package stores 0, a self-reference).
            if (nb > 0) r.boneParents[0] = -1;
            // Faces: stable sort by material (store order; the face set is the same).
            int triCount = m.FaceMaterial.Length;
            int[] order = Enumerable.Range(0, triCount).OrderBy(i => m.FaceMaterial[i]).ThenBy(i => i).ToArray();
            r.numTris = triCount;
            r.triWedge = new int[triCount * 3];
            r.triMaterial = new int[triCount];
            for (int t = 0; t < triCount; t++)
            {
                int src = order[t];
                r.triWedge[t * 3] = m.FaceWedges[src * 3];
                r.triWedge[t * 3 + 1] = m.FaceWedges[src * 3 + 1];
                r.triWedge[t * 3 + 2] = m.FaceWedges[src * 3 + 2];
                r.triMaterial[t] = m.FaceMaterial[src];
            }
            r.numPoints = m.Points.Length;
            r.points = m.Points.SelectMany(v => new[] { v.x, v.y, v.z }).ToArray();
            r.numWedges = m.WedgePoint.Length;
            r.wedgeVertex = m.WedgePoint.ToArray();
            r.wedgeU = m.WedgeU.Select(b => b / 256f).ToArray();
            r.wedgeV = m.WedgeV.Select(b => b / 256f).ToArray();
            r.numWeights = m.WeightPoint.Length;
            r.weightPointIndex = m.WeightPoint.ToArray();
            r.weightValue = m.WeightValue.Select(w => w / 65535f).ToArray();
            r.weightIdxStart = m.WeightStart.ToArray();
            r.weightIdxCount = m.WeightNumber.ToArray();
            r.numLocalPoints = m.LocalPoints.Length;
            r.localPoints = m.LocalPoints.SelectMany(v => new[] { v.x, v.y, v.z }).ToArray();
            r.textures = m.TexturePath.ToArray();
            r.texturePkgs = m.TextureOuter.ToArray();
            r.materialFlags = m.MaterialFlags.ToArray();
            r.materialTexIdx = m.MaterialTextureIndex.ToArray();
            r.origin = new[] { m.Origin.x, m.Origin.y, m.Origin.z };
            r.scale = new[] { m.Scale.x, m.Scale.y, m.Scale.z };
            r.rotationOrigin = new[] { m.RotPitch, m.RotYaw, m.RotRoll };
            r.weaponBoneIdx = m.WeaponBoneIndex;
            r.weaponAdjustOrigin = new[] { m.WeaponAdjustOrigin.x, m.WeaponAdjustOrigin.y, m.WeaponAdjustOrigin.z };
            r.weaponAdjustAxis = m.WeaponAdjustAxis.ToArray();
            return r;
        }

        // Keys: shipped FAnimVec::Quat (Engine.dll.c:61414): component = sin(short*(pi/2)/32767),
        // w = sqrt(max(0, 1-x^2-y^2-z^2)); position = short*PositionScale/32767; times are raw bytes.
        // Arithmetic is double, as in the --exportanims export (byte-identical). The float variant
        // (UEAnimTrack.DecodeQuaternion) deviates from the store twice as much (up to 8e-3 vs 2.5e-4 units),
        // so the export body is kept; x87 precision of the original is not verified here.
        public static UEAnimBank BuildAnimBank(string pkgName, Hp1SkeletalMesh mesh)
        {
            const double K = (Math.PI / 2) / 32767.0;
            Hp1AnimBone[] refBones = mesh.Sequences.Values.First().Movement.RefBones;
            var seqs = mesh.Sequences.Values
                .OrderBy(q => q.Name, StringComparer.OrdinalIgnoreCase)
                .Select(q => new UEAnimBankSequence
                {
                    name = q.Name, group = q.Group, rate = q.Rate,
                    startFrame = q.StartFrame, frameCount = q.NumFrames,
                    tracks = q.Movement.Tracks.Select(t =>
                    {
                        var r = new double[checked(t.QuaternionKeys.Length / 3 * 4)];
                        for (int i = 0, o = 0; i < t.QuaternionKeys.Length; i += 3, o += 4)
                        {
                            double x = Math.Sin(t.QuaternionKeys[i] * K);
                            double y = Math.Sin(t.QuaternionKeys[i + 1] * K);
                            double z = Math.Sin(t.QuaternionKeys[i + 2] * K);
                            // Store X+Y mirror: (-x, -y, z, w).
                            r[o] = -x + 0.0; r[o + 1] = -y + 0.0; r[o + 2] = z;
                            r[o + 3] = Math.Sqrt(Math.Max(0.0, 1.0 - x * x - y * y - z * z));
                        }
                        double scale = t.PositionScale / 32767.0;
                        var p = new double[t.PositionKeys.Length];
                        // Store X+Y mirror: (-x, -y, z); -0.0 is not written.
                        for (int i = 0; i < p.Length; i++)
                            p[i] = (i % 3 == 2 ? 1.0 : -1.0) * t.PositionKeys[i] * scale + 0.0;
                        return new UEAnimBankTrack
                        {
                            numQ = t.QuaternionKeys.Length / 3, numP = t.PositionKeys.Length / 3,
                            numT = t.KeyTimes.Length, r = r, p = p,
                            t = t.KeyTimes.Select(b => (int)b).ToArray(),
                        };
                    }).ToArray(),
                    notifications = q.Notifies.Select(n => new UEAnimBankNotify { time = n.Time, func = n.Function }).ToArray(),
                }).ToArray();
            return new UEAnimBank
            {
                pkg = pkgName, name = mesh.AnimObject,
                boneNames = refBones.Select(b => b.Name).ToArray(),
                boneParents = refBones.Select(b => b.ParentIndex).ToArray(),
                sequences = seqs,
            };
        }

        public static UERawVertexMesh BuildVertex(string objectPath, string pkgName, string leaf,
            Hp1VertexMesh m, UEPackageReader.UEMeshAnimInfo info)
        {
            return new UERawVertexMesh
            {
                name = leaf, pkg = pkgName, objectPath = objectPath,
                frameVerts = m.FrameVerts, animFrames = m.AnimFrames, numVerts = m.Verts.Length,
                verts = m.Verts.SelectMany(v => new[] { v.x, v.y, v.z }).ToArray(),
                numWedges = m.WedgePoint.Length, wedgeVertex = m.WedgePoint.ToArray(),
                wedgeU = m.WedgeU.Select(b => b / 256f).ToArray(),
                wedgeV = m.WedgeV.Select(b => b / 256f).ToArray(),
                numTris = m.FaceMaterial.Length, triWedge = m.FaceWedges.ToArray(), triMaterial = m.FaceMaterial.ToArray(),
                materialFlags = m.MaterialFlags.ToArray(), materialTexIdx = m.MaterialTextureIndex.ToArray(),
                textures = m.TexturePath.ToArray(),
                connectNumTris = m.ConnectNumTris.ToArray(), connectOffset = m.ConnectOffset.ToArray(),
                vertLinks = m.VertLinks.ToArray(),
                specialFaceWedges = m.SpecialFaceWedges.ToArray(), specialFaceMaterial = m.SpecialFaceMaterial.ToArray(),
                modelVerts = m.ModelVerts, specialVerts = m.SpecialVerts,
                meshScaleMax = m.MeshScaleMax, lodHysteresis = m.LodHysteresis, lodStrength = m.LodStrength,
                lodMinVerts = m.LodMinVerts, lodMorph = m.LodMorph, lodZDisplace = m.LodZDisplace,
                remapAnimVerts = m.RemapAnimVerts.ToArray(), oldFrameVerts = m.OldFrameVerts,
                origin = new[] { m.Origin.x, m.Origin.y, m.Origin.z },
                scale = new[] { m.Scale.x, m.Scale.y, m.Scale.z },
                rotationOrigin = new[] { m.RotPitch, m.RotYaw, m.RotRoll },
                seqNames = info.Sequences.Select(q => q.Name).ToArray(),
                seqStart = info.Sequences.Select(q => q.StartFrame).ToArray(),
                seqFrames = info.Sequences.Select(q => q.NumFrames).ToArray(),
                seqRate = info.Sequences.Select(q => q.Rate).ToArray(),
            };
        }
    }
}
