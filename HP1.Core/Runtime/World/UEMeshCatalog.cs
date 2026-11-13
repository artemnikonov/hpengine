// MESH AND ANIMATION CATALOG, Core-owned.
//
// Single source of animation data. A frontend-side source would sample the player's CURRENT sequence, which
// switches later (on the visual update), so on the tick where a script called PlayAnim('climb32') root motion
// would still come from 'breath'.
//
// The catalog resolves data by the EXACT pair (mesh UEResourceRef, sequence name), regardless of what the
// frontend is showing at that moment. Gameplay animation data belongs to the engine.
//
// Reads the shipped package format: UMesh prefix, AnimSeqs, RefSkeleton and compressed AnalogTracks.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HP1.Core;
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    public sealed class Hp1SkeletalMeshCatalog
    {
        private readonly Dictionary<string, List<(PackageFile Package, PackageExport Export)>> _exports =
            new(UEPropNameComparer.Instance);
        private readonly Dictionary<string, Hp1SkeletalMesh> _loaded =
            new(UEPropNameComparer.Instance);

        private readonly Dictionary<string, List<(PackageFile Package, PackageExport Export)>> _meshExports =
            new(UEPropNameComparer.Instance);
        private readonly Dictionary<string, Hp1MeshHashFields> _loadedHashFields =
            new(UEPropNameComparer.Instance);

        // UAnimation objects (class Animation): the target of Actor.SkelAnim. AActor::
        // GetAnim and USkeletalMesh::ApplyAnim take sequences from SkelAnim
        // (+0x68), and from the mesh (DefaultAnimation, +0x24c) only when SkelAnim
        // is empty (Engine.dll.c:161866-161872, 142118-142121). Writer in the campaign:
        // TriggerChangeActorMesh (Lev5_Final, Lev2_fire1).
        private readonly Dictionary<string, List<(PackageFile Package, PackageExport Export)>> _animExports =
            new(UEPropNameComparer.Instance);
        private readonly Dictionary<string, IReadOnlyDictionary<string, Hp1AnimSequence>> _loadedAnims =
            new(UEPropNameComparer.Instance);

        // Every skeletal-mesh export name, for the reader cross-check.
        public IEnumerable<string> SkeletalNames => _exports.Keys;

        // Skeletal export list for TOOLS (metadata dump, frontend bank generator).
        // The tools themselves live in headless: they write files and serialize JSON,
        // which Core cannot do under the frontend profile.
        public IEnumerable<KeyValuePair<string, List<(PackageFile Package, PackageExport Export)>>>
            SkeletalExportsGrouped() => _exports;

        // Same for ANIMATION OBJECTS: the frontend animation store is checked by key
        // on the EXACT package+name pair, because
        // animation objects with the same name live in different packages.
        public IEnumerable<KeyValuePair<string, List<(PackageFile Package, PackageExport Export)>>>
            AnimExportsGrouped() => _animExports;

        // Same for VERTEX meshes (Mesh/LodMesh). The frontend needs their intrinsic fields
        // (Origin/RotOrigin/Scale) just as for skeletal ones: without them
        // 13 campaign classes (spell projectiles, `Tele2`) would be built with an
        // identity transform, unlike the editor route.
        public IEnumerable<KeyValuePair<string, List<(PackageFile Package, PackageExport Export)>>>
            VertexExportsGrouped() => _meshExports;

        // Resolve by EXACT mesh reference: both vertex (Mesh/LodMesh) and
        // skeletal meshes carry sequences, and the gameplay loop asks the same way.
        // A miss returns null; this is a legitimate case (the original data is
        // inconsistent with itself).
        public Hp1AnimSequence FindSequence(UEResourceRef meshRef, string sequenceName)
        {
            if (meshRef is null || string.IsNullOrEmpty(sequenceName)) return null;
            if (meshRef.Class.Equals("Mesh", StringComparison.OrdinalIgnoreCase)
                || meshRef.Class.Equals("LodMesh", StringComparison.OrdinalIgnoreCase))
                return GetMeshSequences(meshRef.Name, meshRef.ObjectPath)
                    .FirstOrDefault(s => string.Equals(s.Name, sequenceName,
                        StringComparison.OrdinalIgnoreCase));
            if (!meshRef.Class.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException(
                    $"animation for mesh class {meshRef.Class} (mesh={meshRef.Name} seq={sequenceName})");
            return Get(meshRef).FindSequence(sequenceName);
        }

        public Hp1SkeletalMeshCatalog(string systemDir)
        {
            // Render/gameplay resources are spread across HPModels, HProps, DProps,
            // level packages, etc. Discover the shipped package set itself instead of
            // maintaining a name allowlist; stable path sort keeps diagnostics fixed.
            foreach (string path in Directory.EnumerateFiles(systemDir, "*.u")
                         .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var package = new PackageFile(path);
                foreach (PackageExport export in package.Exports)
                {
                    string cls = package.ClassName(export);
                    if (cls.Equals("Mesh", StringComparison.OrdinalIgnoreCase)
                        || cls.Equals("LodMesh", StringComparison.OrdinalIgnoreCase)
                        || cls.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!_meshExports.TryGetValue(export.Name, out var meshMatches))
                            _meshExports[export.Name] = meshMatches = new();
                        meshMatches.Add((package, export));
                    }
                    if (cls.Equals("Animation", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!_animExports.TryGetValue(export.Name, out var animMatches))
                            _animExports[export.Name] = animMatches = new();
                        animMatches.Add((package, export));
                    }
                    if (!cls.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!_exports.TryGetValue(export.Name, out var matches))
                        _exports[export.Name] = matches = new();
                    matches.Add((package, export));
                }
            }
        }

        // The four field groups the collision-hash extent needs from a mesh
        // (UMesh::GetCollisionBoundingBox, Engine.dll.c:33473): the UPrimitive
        // BoundingBox plus Scale/Origin/RotOrigin out of the UMesh scalar tail.
        // Ambiguous leaf names (skbarrelMesh ships from two packages) resolve by
        // the reference's FULL ObjectPath (path-first identity): the
        // path's package segment picks the export. A pathless ambiguous query
        // stays loud.
        private (PackageFile Package, PackageExport Export) ResolveMeshExport(
            string name, string objectPath)
        {
            if (!_meshExports.TryGetValue(name, out var matches) || matches.Count == 0)
                throw new KeyNotFoundException("shipped mesh not found: " + name);
            if (matches.Count == 1) return matches[0];
            string pkg = objectPath is not null && objectPath.IndexOf('.') > 0
                ? objectPath.Substring(0, objectPath.IndexOf('.')) : null;
            if (pkg is not null)
            {
                var byPkg = matches.Where(m =>
                    string.Equals(m.Package.PackageName, pkg, StringComparison.OrdinalIgnoreCase)).ToList();
                if (byPkg.Count == 1) return byPkg[0];
            }
            throw new InvalidDataException(
                $"ambiguous shipped mesh {name}: {matches.Count} exports (path='{objectPath}')");
        }

        public Hp1MeshHashFields GetHashFields(string name, string objectPath = null)
        {
            string key = objectPath ?? name;
            if (_loadedHashFields.TryGetValue(key, out Hp1MeshHashFields cached)) return cached;
            var m = ResolveMeshExport(name, objectPath);
            Hp1MeshHashFields fields = m.Package.ReadMeshHashFields(m.Export);
            _loadedHashFields.Add(key, fields);
            return fields;
        }

        // Vertex-mesh (Mesh/LodMesh) sequence metadata by mesh name.
        private readonly Dictionary<string, List<Hp1AnimSequence>> _meshSeqs =
            new(UEPropNameComparer.Instance);
        public List<Hp1AnimSequence> GetMeshSequences(string name, string objectPath = null)
        {
            string key = objectPath ?? name;
            if (_meshSeqs.TryGetValue(key, out List<Hp1AnimSequence> cached)) return cached;
            var m = ResolveMeshExport(name, objectPath);
            List<Hp1AnimSequence> seqs = m.Package.ReadMeshAnimSeqs(m.Export);
            _meshSeqs.Add(key, seqs);
            return seqs;
        }

        // Vertex mesh (Mesh/LodMesh) by exact reference: input to the DrawLodMesh branch
        // of the drawn actor (SpecialVerts, UELevelRuntime.RenderMeshFrame).
        private readonly Dictionary<string, Hp1VertexMesh> _vertexMeshes = new(UEPropNameComparer.Instance);
        public Hp1VertexMesh GetVertexMesh(string name, string objectPath = null)
        {
            string key = objectPath ?? name;
            if (_vertexMeshes.TryGetValue(key, out Hp1VertexMesh cached)) return cached;
            var m = ResolveMeshExport(name, objectPath);
            Hp1VertexMesh vm = m.Package.ReadVertexMesh(m.Export);
            _vertexMeshes.Add(key, vm);
            return vm;
        }

        // Sequences of an animation object by EXACT reference (the path disambiguates
        // the name, as for meshes). A miss is loud: the reference came from a package.
        // Lazy loading is shared by the Core tick and the frontend (skinner), and
        // the tick may run on its own thread: dictionaries and package reads are under one
        // lock (nearly free without contention).
        readonly object _gate = new object();

        public IReadOnlyDictionary<string, Hp1AnimSequence> GetAnimation(string name, string objectPath = null)
        {
            lock (_gate) return GetAnimationLocked(name, objectPath);
        }

        IReadOnlyDictionary<string, Hp1AnimSequence> GetAnimationLocked(string name, string objectPath)
        {
            string key = objectPath ?? name;
            if (_loadedAnims.TryGetValue(key, out var cached)) return cached;
            if (!_animExports.TryGetValue(name, out var matches) || matches.Count == 0)
                throw new KeyNotFoundException("shipped Animation not found: " + name);
            (PackageFile Package, PackageExport Export) m;
            if (matches.Count == 1) m = matches[0];
            else
            {
                string pkg = objectPath is not null && objectPath.IndexOf('.') > 0
                    ? objectPath.Substring(0, objectPath.IndexOf('.')) : null;
                var byPkg = pkg is null ? null : matches.Where(x =>
                    string.Equals(x.Package.PackageName, pkg, StringComparison.OrdinalIgnoreCase)).ToList();
                if (byPkg is not { Count: 1 })
                    throw new InvalidDataException(
                        $"ambiguous shipped Animation {name}: {matches.Count} exports (path='{objectPath}')");
                m = byPkg[0];
            }
            IReadOnlyDictionary<string, Hp1AnimSequence> seqs = m.Package.ReadAnimation(m.Export);
            _loadedAnims.Add(key, seqs);
            return seqs;
        }

        // FIXTURE SEAM (headless build only, InternalsVisibleTo): synthetic
        // mesh and animation object from the oracle corpus.
        internal void InjectForTest(string key, Hp1SkeletalMesh mesh) { lock (_gate) { _loaded[key] = mesh; _lastMesh = null; _epoch++; } }
        internal void InjectAnimationForTest(string key, IReadOnlyDictionary<string, Hp1AnimSequence> seqs)
        { lock (_gate) { _loadedAnims[key] = seqs; _epoch++; } }

        // RESOLUTION MEMO ON THE REFERENCE. A reference's Name/ObjectPath are readonly and the dictionaries only
        // grow (an entry is never replaced, except by the fixture seam above, which bumps the epoch), so the
        // mesh/animation a reference resolves to never changes: the result is kept on the reference itself and the
        // lock plus the case-insensitive path hash are paid once per reference, not several times per actor per
        // frame. The same lookup as Get(name, path) — exact FName semantics are those of the dictionaries.
        // A miss throws exactly as before (nothing is memoised). HP1_FRAME_CACHE_VERIFY=1 re-resolves every hit.
        int _epoch;
        sealed class Memo
        {
            public readonly Hp1SkeletalMeshCatalog Owner; public readonly int Epoch; public readonly object Value;
            public Memo(Hp1SkeletalMeshCatalog owner, int epoch, object value) { Owner = owner; Epoch = epoch; Value = value; }
        }

        public Hp1SkeletalMesh Get(UEResourceRef r)
        {
            if (r.MeshMemo is Memo m && ReferenceEquals(m.Owner, this) && m.Epoch == System.Threading.Volatile.Read(ref _epoch))
            {
                if (FrameCacheVerify.Enabled)
                    FrameCacheVerify.Hit("mesh_ref", ReferenceEquals(m.Value, Get(r.Name, r.ObjectPath)) ? null : r.ToString());
                return (Hp1SkeletalMesh)m.Value;
            }
            lock (_gate)
            {
                Hp1SkeletalMesh mesh = GetLocked(r.Name, r.ObjectPath);
                r.MeshMemo = new Memo(this, _epoch, mesh);
                return mesh;
            }
        }

        public IReadOnlyDictionary<string, Hp1AnimSequence> GetAnimation(UEResourceRef r)
        {
            if (r.AnimMemo is Memo m && ReferenceEquals(m.Owner, this) && m.Epoch == System.Threading.Volatile.Read(ref _epoch))
            {
                if (FrameCacheVerify.Enabled)
                    FrameCacheVerify.Hit("anim_ref", ReferenceEquals(m.Value, GetAnimation(r.Name, r.ObjectPath)) ? null : r.ToString());
                return (IReadOnlyDictionary<string, Hp1AnimSequence>)m.Value;
            }
            lock (_gate)
            {
                var seqs = GetAnimationLocked(r.Name, r.ObjectPath);
                r.AnimMemo = new Memo(this, _epoch, seqs);
                return seqs;
            }
        }

        public Hp1SkeletalMesh Get(string name, string objectPath = null)
        {
            lock (_gate) return GetLocked(name, objectPath);
        }

        // Last hit by REFERENCE to the same strings: callers pass the strings of the same
        // UEResourceRef in a row (the frame calls Get several times per actor), so the dictionary lookup is skipped;
        // the result is the same (the key is the same string). A speedup, no change in semantics.
        string _lastName, _lastPath;
        Hp1SkeletalMesh _lastMesh;

        Hp1SkeletalMesh GetLocked(string name, string objectPath)
        {
            if (_lastMesh is not null && ReferenceEquals(name, _lastName) && ReferenceEquals(objectPath, _lastPath)) return _lastMesh;
            Hp1SkeletalMesh found = GetLockedSlow(name, objectPath);
            _lastName = name; _lastPath = objectPath; _lastMesh = found;
            return found;
        }

        Hp1SkeletalMesh GetLockedSlow(string name, string objectPath)
        {
            string key = objectPath ?? name;
            if (_loaded.TryGetValue(key, out Hp1SkeletalMesh mesh)) return mesh;
            if (!_exports.TryGetValue(name, out var matches) || matches.Count == 0)
                throw new KeyNotFoundException("shipped SkeletalMesh not found: " + name);
            (PackageFile Package, PackageExport Export) m;
            if (matches.Count == 1) m = matches[0];
            else
            {
                // Ambiguous leaf → disambiguate by the reference's full path
                // package (path-first), same as ResolveMeshExport.
                string pkg = objectPath is not null && objectPath.IndexOf('.') > 0
                    ? objectPath.Substring(0, objectPath.IndexOf('.')) : null;
                var byPkg = pkg is null ? null : matches.Where(x =>
                    string.Equals(x.Package.PackageName, pkg, StringComparison.OrdinalIgnoreCase)).ToList();
                if (byPkg is not { Count: 1 })
                    throw new InvalidDataException(
                        $"ambiguous shipped SkeletalMesh {name}: {matches.Count} exports (path='{objectPath}')");
                m = byPkg[0];
            }
            mesh = m.Package.ReadSkeletalMesh(m.Export);
            _loaded.Add(key, mesh);
            return mesh;
        }

        // Validation dump: every SkeletalMesh's sequence metadata (the fields the
        // gameplay kernel consumes for anim timing) as THIS parser reads them, one
        // JSON line per mesh, for cross-checking against an independent extraction.
        // (DumpAnimSequences and ExportAnimBanks live in headless: they are TOOLS
        //  (metadata dump for cross-validation and the frontend animation bank generator),
        //  unrelated to gameplay, and would pull System.Text.Json into Core, which the frontend
        //  profile lacks (netstandard2.1 + C#9). The catalog only READS shipped data.)

        public sealed class PackageFile : UEPackageReader
        {
            public PackageFile(string path) : base(path) { }

            static int[] U16Array(RawReader r, string label)
            {
                var a = new int[CheckedCount(r.CompactIndex(), label)];
                for (int i = 0; i < a.Length; i++) a[i] = r.UInt16();
                return a;
            }






            // The AnimSeqs metadata of a vertex-animated UMesh/ULodMesh — the
            // same serialize walk ReadMeshHashFields SKIPS through, here READ:
            // per sequence FName SeqName, FName Group, StartFrame, NumFrames,
            // notify array (time float + FName function), Rate float. Timing
            // metadata only (the headless anim model needs NumFrames/Rate/
            // notifies for bAnimFinished + notify dispatch); vertex frames are
            // render-side and stay unread. Reached by stage-7 casts: the wand and
            // the spell FX are LodMesh actors playing anims.
            public List<Hp1AnimSequence> ReadMeshAnimSeqs(PackageExport export)
            {
                var r = new RawReader(_data) { Position = export.Offset };
                SkipTaggedProperties(r);
                if (_version <= 61)
                    throw new NotSupportedException("mesh anim seqs: pre-62 package");
                r.Skip(25 + 16);         // UPrimitive box + sphere
                r.Position = r.Int32();  // verts (lazy, absolute skip offset)
                r.Position = r.Int32();  // tris
                int animSeqs = CheckedCount(r.CompactIndex(), "anim sequences");
                var seqs = new List<Hp1AnimSequence>(animSeqs);
                for (int i = 0; i < animSeqs; i++)
                {
                    string name = Name(r.CompactIndex());
                    string group = Name(r.CompactIndex());
                    int startFrame = r.Int32();
                    int numFrames = r.Int32();
                    int notifies = CheckedCount(r.CompactIndex(), "anim notifies");
                    var notes = new Hp1AnimNotify[notifies];
                    for (int j = 0; j < notifies; j++)
                        notes[j] = new Hp1AnimNotify(r.Single(), Name(r.CompactIndex()));
                    float rate = r.Single();
                    seqs.Add(new Hp1AnimSequence(name, group, startFrame, numFrames, rate,
                        notes, Movement: null));
                }
                return seqs;
            }

            // ── WHOLE VERTEX MESH (Mesh/LodMesh) ────────────────────────
            //
            // WHY. Shipped has its OWN draw branch for ULodMesh: it takes the
            // sequence by the actor's `AnimSequence` and morphs vertices between two
            // pool frames at offset `(StartFrame + frame) * FrameVerts`
            // (ULodMesh::GetFrame, Engine 0x1035e460). Without it the mesh shows frame 0 forever.
            //
            // Campaign data: `HPBase.BioGelm` has 56 frames and 7 sequences, `HPBase.Tele2` 100 frames.
            // Both are spawned from class defaults (`spellEcto`, `transHitEffect`), i.e. on every cast.
            //
            // VERTEX UNPACKING from source: the pool stores int32, three signed
            // fields of 11/11/10 bits (ULodMesh::GetFrame sar 0x15/0x15/0x16 at 0x1035e8b4/8c2/8d1).
            public Hp1VertexMesh ReadVertexMesh(PackageExport export)
            {
                var r = new RawReader(_data) { Position = export.Offset };
                SkipTaggedProperties(r);
                r.Skip(25 + (_version > 61 ? 16 : 12));

                int vertsEnd = _version > 61 ? r.Int32() : 0;
                int vertCount = CheckedCount(r.CompactIndex(), "mesh verts");
                var verts = new Vector3[vertCount];
                for (int i = 0; i < vertCount; i++)
                {
                    int packed = r.Int32();
                    verts[i] = new Vector3((packed << 21) >> 21,
                                           (packed << 10) >> 21,
                                           packed >> 22);
                }
                if (_version > 61 && r.Position != vertsEnd)
                    throw new InvalidDataException(
                        $"{export.Name}: the vertex pool ended where the lazy array did not promise");

                if (_version > 61) r.Position = r.Int32();
                else r.Skip(CheckedCount(r.CompactIndex(), "tris") * 20);
                int animSeqs = CheckedCount(r.CompactIndex(), "anim sequences");
                for (int i = 0; i < animSeqs; i++)
                {
                    r.CompactIndex(); r.CompactIndex(); r.Skip(8);
                    int notifies = CheckedCount(r.CompactIndex(), "anim notifies");
                    for (int j = 0; j < notifies; j++) { r.Skip(4); r.CompactIndex(); }
                    r.Skip(4);
                }
                // VERTEX CONNECTIVITY IS READ, NOT SKIPPED.
                //
                // `Connects` (an int pair per frame vertex: how many triangles use it
                // and where they start in the list) and `VertLinks` (the list itself) feed
                // shipped's vertex normal computation: the mesh body reads `[mesh+0x9c]`
                // as 8-byte records and `[mesh+0xcc]` as 4-byte records (Render.dll
                // 0x10b0f49c and 0x10b0f4ae, read from the DISASSEMBLY of that loop, not
                // inferred from field order).
                //
                // Same read layout as the vertex pool above: a lazy array has the absolute end offset
                // first, then the count, then the records, and the end is CHECKED against the
                // promised one, otherwise a one-record shift would pass silently.
                int connectsEnd = _version > 61 ? r.Int32() : 0;
                int connectCount = CheckedCount(r.CompactIndex(), "connects");
                var connectNumTris = new int[connectCount];
                var connectOffset = new int[connectCount];
                for (int i = 0; i < connectCount; i++)
                {
                    connectNumTris[i] = r.Int32();
                    connectOffset[i] = r.Int32();
                }
                if (_version > 61 && r.Position != connectsEnd)
                    throw new InvalidDataException(
                        $"{export.Name}: the vertex connectivity ended where the lazy array did not promise");
                // Second BoundingBox (+0x28) after Connects: same as in the skeletal parse below.
                var primitiveBox = new Hp1Box(new Vector3(r.Single(), r.Single(), r.Single()),
                    new Vector3(r.Single(), r.Single(), r.Single()), r.Byte() != 0);
                r.Skip(_version > 61 ? 16 : 12);
                int linksEnd = _version > 61 ? r.Int32() : 0;
                int linkCount = CheckedCount(r.CompactIndex(), "vertex links");
                var vertLinks = new int[linkCount];
                for (int i = 0; i < linkCount; i++) vertLinks[i] = r.Int32();
                if (_version > 61 && r.Position != linksEnd)
                    throw new InvalidDataException(
                        $"{export.Name}: the link list ended where the lazy array did not promise");
                int textures = CheckedCount(r.CompactIndex(), "textures");
                var texPath = new string[textures];
                for (int i = 0; i < textures; i++) texPath[i] = ObjectFullPath(r.CompactIndex());
                // UMesh::BoundingBoxes (+0xa8) per frame: read by UMesh::GetRenderBoundingBox.
                var boundingBoxes = new Hp1Box[CheckedCount(r.CompactIndex(), "bounding boxes")];
                for (int i = 0; i < boundingBoxes.Length; i++)
                    boundingBoxes[i] = new Hp1Box(new Vector3(r.Single(), r.Single(), r.Single()),
                        new Vector3(r.Single(), r.Single(), r.Single()), r.Byte() != 0);
                r.Skip(CheckedCount(r.CompactIndex(), "bounding spheres")
                       * (_version > 61 ? 16 : 12));
                int frameVerts = r.Int32();
                int animFrames = r.Int32();
                r.Skip(8);                                  // AndFlags, OrFlags
                var scale = new Vector3(r.Single(), r.Single(), r.Single());
                var origin = new Vector3(r.Single(), r.Single(), r.Single());
                int rotPitch = r.Int32(), rotYaw = r.Int32(), rotRoll = r.Int32();
                r.Skip(8);
                if (_version == 65) r.Skip(4);
                else if (_version >= 66) r.Skip(CheckedCount(r.CompactIndex(), "texture LOD") * 4);

                // ULodMesh::Serialize (Engine.dll.c:138685-138689): CollapsePointThus (+0x12c), FaceLevel (+0x138),
                // Faces, CollapseWedgeThus (+0x150), Wedges — read by URender::DrawLodMesh's LOD collapse (UEMeshLod).
                int[] collapsePoints = U16Array(r, "collapse points");
                int[] faceLevel = U16Array(r, "face levels");
                int faceCount = CheckedCount(r.CompactIndex(), "faces");
                var faceWedges = new int[faceCount * 3];
                var faceMaterial = new int[faceCount];
                for (int i = 0; i < faceCount; i++)
                {
                    faceWedges[i * 3] = r.UInt16();
                    faceWedges[i * 3 + 1] = r.UInt16();
                    faceWedges[i * 3 + 2] = r.UInt16();
                    faceMaterial[i] = r.UInt16();
                }
                int[] collapseWedges = U16Array(r, "collapse wedges");
                int wedgeCount = CheckedCount(r.CompactIndex(), "wedges");
                var wedgePoint = new int[wedgeCount];
                var wedgeU = new byte[wedgeCount];
                var wedgeV = new byte[wedgeCount];
                for (int i = 0; i < wedgeCount; i++)
                {
                    wedgePoint[i] = r.UInt16();
                    wedgeU[i] = r.Byte(); wedgeV[i] = r.Byte();
                }
                int materialCount = CheckedCount(r.CompactIndex(), "materials");
                var materialFlags = new int[materialCount];
                var materialTexIdx = new int[materialCount];
                for (int i = 0; i < materialCount; i++)
                {
                    materialFlags[i] = checked((int)r.UInt32());
                    materialTexIdx[i] = r.Int32();
                }
                // ULodMesh TAIL: SPECIAL FACES AND LEVEL-OF-DETAIL NUMBERS.
                //
                // Order and contents from `ULodMesh::Serialize`
                // (Engine.dll.c:138655): after materials come SpecialFaces,
                // then ModelVerts, SpecialVerts, MeshScaleMax, LODHysteresis,
                // LODStrength, LODMinVerts, LODMorph, LODZDisplace,
                // ReMapAnimVerts and OldFrameVerts.
                //
                // WHY THESE. `URender::DrawLodMesh` at 0x10b0ffd6 reads
                // `LODStrength` (object offset +0x18c, assigned by the same
                // serialize body) and when it is zero SKIPS the whole level-of-detail
                // selection block. Without these numbers the LodMesh branch runs idle.
                int specialFaceCount = CheckedCount(r.CompactIndex(), "special faces");
                var specialFaceWedges = new int[specialFaceCount * 3];
                var specialFaceMaterial = new int[specialFaceCount];
                for (int i = 0; i < specialFaceCount; i++)
                {
                    specialFaceWedges[i * 3] = r.UInt16();
                    specialFaceWedges[i * 3 + 1] = r.UInt16();
                    specialFaceWedges[i * 3 + 2] = r.UInt16();
                    specialFaceMaterial[i] = r.UInt16();
                }
                int modelVerts = r.Int32();
                int specialVerts = r.Int32();
                float meshScaleMax = r.Single();
                float lodHysteresis = r.Single();
                float lodStrength = r.Single();
                // On load ULodMesh::Serialize (Engine.dll.c:138717-138722) clamps
                // LODMinVerts from above by FrameVerts: `LODMinVerts = Min(LODMinVerts, FrameVerts)`.
                // In the original's memory HPBase.spellProj (FrameVerts 9, file value 10) has 9.
                int lodMinVerts = Math.Min(r.Int32(), frameVerts);
                float lodMorph = r.Single();
                float lodZDisplace = r.Single();
                int remapCount = CheckedCount(r.CompactIndex(), "remap anim verts");
                var remapAnimVerts = new int[remapCount];
                for (int i = 0; i < remapCount; i++) remapAnimVerts[i] = r.UInt16();
                int oldFrameVerts = r.Int32();
                return new Hp1VertexMesh(export.Name, verts, frameVerts, animFrames)
                {
                    WedgePoint = wedgePoint, WedgeU = wedgeU, WedgeV = wedgeV,
                    FaceWedges = faceWedges, FaceMaterial = faceMaterial,
                    MaterialFlags = materialFlags, MaterialTextureIndex = materialTexIdx,
                    TexturePath = texPath, Scale = scale, Origin = origin,
                    PrimitiveBox = primitiveBox, BoundingBoxes = boundingBoxes,
                    RotPitch = rotPitch, RotYaw = rotYaw, RotRoll = rotRoll,
                    ConnectNumTris = connectNumTris, ConnectOffset = connectOffset,
                    VertLinks = vertLinks,
                    SpecialFaceWedges = specialFaceWedges,
                    SpecialFaceMaterial = specialFaceMaterial,
                    ModelVerts = modelVerts, SpecialVerts = specialVerts,
                    Lod = new Hp1MeshLod(collapsePoints.Length, faceLevel, collapseWedges, lodStrength, lodMinVerts, frameVerts),
                    MeshScaleMax = meshScaleMax, LodHysteresis = lodHysteresis,
                    LodStrength = lodStrength, LodMinVerts = lodMinVerts,
                    LodMorph = lodMorph, LodZDisplace = lodZDisplace,
                    RemapAnimVerts = remapAnimVerts, OldFrameVerts = oldFrameVerts,
                };
            }

            public Hp1SkeletalMesh ReadSkeletalMesh(PackageExport export)
            {
                var r = new RawReader(_data) { Position = export.Offset };
                SkipTaggedProperties(r);

                // UPrimitive: BoundingBox + BoundingSphere.
                r.Skip(25 + (_version > 61 ? 16 : 12));

                // UMesh lazy arrays carry absolute package offsets in HP1 packages.
                if (_version > 61) r.Position = r.Int32();
                else r.Skip(CheckedCount(r.CompactIndex(), "verts") * 4);

                // The UMesh-LEVEL triangle array is skipped: for a skeletal mesh it is
                // EMPTY (measured: tris=0 with 18 faces), and the vtable slots resolve not to
                // UMesh bodies but to ULodMesh overrides; see below, that is the real source.
                if (_version > 61) r.Position = r.Int32();
                else r.Skip(CheckedCount(r.CompactIndex(), "tris") * 20);

                int animSeqs = CheckedCount(r.CompactIndex(), "anim sequences");
                for (int i = 0; i < animSeqs; i++)
                {
                    r.CompactIndex(); r.CompactIndex(); r.Skip(8);
                    int notifies = CheckedCount(r.CompactIndex(), "anim notifies");
                    for (int j = 0; j < notifies; j++) { r.Skip(4); r.CompactIndex(); }
                    r.Skip(4);
                }
                if (_version > 61) r.Position = r.Int32();
                else r.Skip(CheckedCount(r.CompactIndex(), "connects") * 8);
                // UMesh::Serialize writes BoundingBox (+0x28) a SECOND time after
                // Connects (Engine.dll.c UMesh::Serialize); that value is what stays in memory.
                var primitiveBox = new Hp1Box(new Vector3(r.Single(), r.Single(), r.Single()),
                    new Vector3(r.Single(), r.Single(), r.Single()), r.Byte() != 0);
                r.Skip(_version > 61 ? 16 : 12);
                if (_version > 61) r.Position = r.Int32();
                else r.Skip(CheckedCount(r.CompactIndex(), "vertex links") * 4);
                // TEXTURE REFERENCES ARE KEPT. The material slot indexes this
                // table, and without it there is no way to know what to paint the
                // submesh with.
                int textures = CheckedCount(r.CompactIndex(), "textures");
                var textureLeaf = new string[textures];
                var textureOuter = new string[textures];
                var texturePath = new string[textures];
                for (int i = 0; i < textures; i++)
                {
                    int objectRef = r.CompactIndex();
                    (string leaf, string outer) = ObjectLeafAndOuter(objectRef);
                    textureLeaf[i] = leaf; textureOuter[i] = outer;
                    // FULL PATH = EXACT IDENTITY. The leaf is ambiguous in the campaign
                    // (`station`, `snitch`, `window2` live in different packages with
                    // different bMasked), and a (package, leaf) pair would require guessing
                    // whether "package" means the root or the group.
                    texturePath[i] = ObjectFullPath(objectRef);
                }
                int boundingBoxCount = CheckedCount(r.CompactIndex(), "bounding boxes");
                float boundMinX = 0, boundMinY = 0, boundMinZ = 0;
                float boundMaxX = 0, boundMaxY = 0, boundMaxZ = 0;
                // UMesh::BoundingBoxes (+0xa8, TArray<FBox>): read by
                // USkeletalMesh::GetRenderBoundingBox (FUN_1041b500) by the SkelAnim
                // sequence index.
                var boundingBoxes = new Hp1Box[boundingBoxCount];
                for (int i = 0; i < boundingBoxCount; i++)
                {
                    float x0 = r.Single(), y0 = r.Single(), z0 = r.Single();
                    float x1 = r.Single(), y1 = r.Single(), z1 = r.Single();
                    bool valid = r.Byte() != 0; // FBox::IsValid
                    boundingBoxes[i] = new Hp1Box(new Vector3(x0, y0, z0), new Vector3(x1, y1, z1), valid);
                    boundMinX += x0; boundMinY += y0; boundMinZ += z0;
                    boundMaxX += x1; boundMaxY += y1; boundMaxZ += z1;
                }
                Vector3 renderExtent = Vector3.zero;
                if (boundingBoxCount > 0)
                {
                    // USkeletalMesh::Serialize (Engine.dll.c:96413-96448)
                    // accumulates the six FBox coordinates, stores their
                    // averages in SkeletalMeshSkeleton (+0x1f8), and
                    // execGetRenderExtent returns Max-Min from that box.
                    // Disasm 0x1041a9cf..0x1041aa1d: sums are fstp dword at every
                    // step (as here), while 1/N (fild N; fdivr 1.0) stays in the
                    // x87 PC64 register and is multiplied by the sum with ONE rounding
                    // to float.
                    X87Ext inverseCount = X87Ext.Div64(X87Ext.From(1f), X87Ext.From(boundingBoxCount));
                    float Avg(float sum) => X87Ext.Round64(inverseCount * X87Ext.From(sum)).ToFloat();
                    renderExtent = new Vector3(
                        Avg(boundMaxX) - Avg(boundMinX),
                        Avg(boundMaxY) - Avg(boundMinY),
                        Avg(boundMaxZ) - Avg(boundMinZ));
                }
                r.Skip(CheckedCount(r.CompactIndex(), "bounding spheres") * (_version > 61 ? 16 : 12));
                // UMesh scalar tail. Root movement consumes the same Scale and
                // RotOrigin as USkeletalMesh::GetMeshCoords; retain them from the
                // shipped mesh rather than borrowing imported mesh data.
                int skelFrameVerts = r.Int32();              // FrameVerts (+0xf0)
                r.Skip(12);                                  // AnimFrames, AndFlags, OrFlags
                var meshScale = new Vector3(r.Single(), r.Single(), r.Single());
                var meshOrigin = new Vector3(r.Single(), r.Single(), r.Single());
                int meshRotPitch = r.Int32(), meshRotYaw = r.Int32(), meshRotRoll = r.Int32();
                r.Skip(8);
                if (_version == 65) r.Skip(4);
                else if (_version >= 66) r.Skip(CheckedCount(r.CompactIndex(), "texture LOD") * 4);

                // ULodMesh arrays and scalar tail.
                // ULodMesh::Serialize (Engine.dll.c:138685-138689): CollapsePointThus (+0x12c), FaceLevel (+0x138),
                // Faces, CollapseWedgeThus (+0x150), Wedges — read by URender::DrawLodMesh's LOD collapse (UEMeshLod).
                int[] collapsePoints = U16Array(r, "collapse points");
                int[] faceLevel = U16Array(r, "face levels");
                // ── FACES AND WEDGES: THE REAL TRIANGLE SOURCE ────────
                //
                // `ULodMesh::GetTriVerts` (Engine.dll.c:120423) walks TWO
                // tables: a face (`+0x144`, stride 8) gives three `uint16` wedge
                // indices at 0/2/4, and a wedge (`+0x15c`, stride 4) gives a `uint16`
                // point index. The count is `ULodMesh::GetNumTris` (`+0x148`,
                // body 60913), i.e. the number of FACES.
                //
                // The `UMesh` triangle array is empty for skeletal meshes (tris=0
                // while there are 18 faces), so the slots are not UMesh bodies.
                int faceCount = CheckedCount(r.CompactIndex(), "faces");
                var faceWedges = new int[faceCount * 3];
                var faceMaterial = new int[faceCount];
                for (int i = 0; i < faceCount; i++)
                {
                    faceWedges[i * 3] = r.UInt16();
                    faceWedges[i * 3 + 1] = r.UInt16();
                    faceWedges[i * 3 + 2] = r.UInt16();
                    // MATERIAL INDEX IS READ. It splits the mesh into submeshes;
                    // without it a multi-skin model is painted with one slot.
                    faceMaterial[i] = r.UInt16();
                }
                int[] collapseWedges = U16Array(r, "collapse wedges");
                int wedgeCount = CheckedCount(r.CompactIndex(), "wedges");
                var wedgePoint = new int[wedgeCount];
                var wedgeU = new byte[wedgeCount];
                var wedgeV = new byte[wedgeCount];
                for (int i = 0; i < wedgeCount; i++)
                {
                    wedgePoint[i] = r.UInt16();
                    // FMeshWedge: BYTE U, V. Read, not skipped: the mesh's only
                    // source of UV mapping.
                    wedgeU[i] = r.Byte(); wedgeV[i] = r.Byte();
                }
                // Point indices per triangle: what GetTriVerts returns.
                var triVerts = new int[faceWedges.Length];
                for (int i = 0; i < faceWedges.Length; i++)
                {
                    int w = faceWedges[i];
                    if ((uint)w >= (uint)wedgeCount)
                        throw new InvalidDataException(
                            $"{export.Name}: wedge {w} outside the table of {wedgeCount}");
                    triVerts[i] = wedgePoint[w];
                }
                // FMeshMaterial: DWORD PolyFlags, INT TextureIndex. Slot flags
                // decide submesh blending, the index which texture to use.
                int materialCount = CheckedCount(r.CompactIndex(), "materials");
                var materialFlags = new int[materialCount];
                var materialTexIdx = new int[materialCount];
                for (int i = 0; i < materialCount; i++)
                {
                    materialFlags[i] = checked((int)r.UInt32());
                    materialTexIdx[i] = r.Int32();
                }
                // SpecialFaces (+0x174) and SpecialVerts (+0x184): the ULodMesh tail
                // (ULodMesh::Serialize, Engine.dll.c:138655), read by the weapon-triangle
                // branch of URender::DrawLodMesh (Render.dll.c:14256-14323); with
                // SpecialVerts == 0 it does not run. Stream size: 8*N + 32 bytes.
                int skelSpecialFaces = CheckedCount(r.CompactIndex(), "special faces");
                r.Skip(8 * skelSpecialFaces);
                int skelModelVerts = r.Int32();              // ModelVerts (+0x180)
                int skelSpecialVerts = r.Int32();            // SpecialVerts (+0x184)
                r.Skip(8);                                   // MeshScaleMax, LODHysteresis
                float skelLodStrength = r.Single();          // LODStrength (+0x18c)
                // ULodMesh::Serialize on load (Engine.dll.c:138718-138722): LODMinVerts = Min(LODMinVerts, FrameVerts).
                int skelLodMinVerts = Math.Min(r.Int32(), skelFrameVerts);   // LODMinVerts (+0x190)
                r.Skip(8);                                   // LODMorph, LODZDisplace
                // RemapAnimVerts: mapping of LOD vertices to animation vertices.
                // Read, not skipped: for 13 meshes the frontend store addresses
                // points differently from the ULodMesh wedge table, and this
                // table is the only candidate explanation from source.
                int remapCount = CheckedCount(r.CompactIndex(), "remap anim verts");
                var remapAnimVerts = new int[remapCount];
                for (int i = 0; i < remapCount; i++) remapAnimVerts[i] = r.UInt16();
                r.Skip(4);

                // ── USkeletalMesh EXTENDED WEDGES ────────────────────────
                // FMeshExtWedge: WORD iVertex, WORD Flags, FLOAT U, V. This is a
                // SEPARATE wedge table of the skeletal mesh; for some meshes it
                // differs from the ULodMesh byte table, and which one feeds
                // drawing is decided by measurement, not assumption.
                int extWedgeCount = CheckedCount(r.CompactIndex(), "extended wedges");
                var extWedgePoint = new int[extWedgeCount];
                var extWedgeU = new float[extWedgeCount];
                var extWedgeV = new float[extWedgeCount];
                for (int i = 0; i < extWedgeCount; i++)
                {
                    extWedgePoint[i] = r.UInt16();
                    r.Skip(2);                       // Flags
                    extWedgeU[i] = r.Single();
                    extWedgeV[i] = r.Single();
                }

                // ── SKELETAL POINTS ARE READ ────────────────────────────────
                // Source vertex positions that `USkeletalMesh::GetFrame`
                // (29125) skins by bones into the buffer from which the
                // DIST_OwnerMesh branch takes triangle vertices (stride 12 = FVector).
                int pointCount = CheckedCount(r.CompactIndex(), "skeletal points");
                var points = new Vector3[pointCount];
                for (int i = 0; i < pointCount; i++)
                    points[i] = new Vector3(r.Single(), r.Single(), r.Single());
                int boneCount = CheckedCount(r.CompactIndex(), "reference skeleton");
                var bones = new Hp1Bone[boneCount];
                for (int i = 0; i < boneCount; i++)
                {
                    string name = Name(r.CompactIndex());
                    uint boneFlags = r.UInt32();
                    // BONE REFERENCE POSE IS READ, NOT SKIPPED: from it
                    // `USkeletalMesh::GetFrame` (29125) builds FCoords per bone
                    // (`FCoords(FPlace)` from the frame header, the reference pose
                    // gives the initial placement). Field layout: 16+12+4+12 bytes.
                    float qx = r.Single(), qy = r.Single(), qz = r.Single(), qw = r.Single();
                    var pos = new Vector3(r.Single(), r.Single(), r.Single());
                    float boneLength = r.Single();
                    var boneScale = new Vector3(r.Single(), r.Single(), r.Single());
                    int numChildren = checked((int)r.UInt32());
                    int parent = checked((int)r.UInt32());
                    bones[i] = new Hp1Bone(name, parent, numChildren)
                        { OrientX = qx, OrientY = qy, OrientZ = qz, OrientW = qw,
                          Position = pos, Length = boneLength, Scale = boneScale,
                          Flags = boneFlags };
                }

                // ── BONE WEIGHTS ────────────────────────────────────────────
                // FBoneWeightIndex (8 bytes: WeightIndex, Number, DetailA/B) and
                // FBoneWeight (4 bytes: PointIndex, BoneWeight). Required by
                // skinning in GetFrame.
                int wIdxCount = CheckedCount(r.CompactIndex(), "bone weight indices");
                var weightStart = new int[wIdxCount];
                var weightNumber = new int[wIdxCount];
                for (int i = 0; i < wIdxCount; i++)
                {
                    weightStart[i] = r.UInt16();
                    weightNumber[i] = r.UInt16();
                    r.Skip(4);                       // DetailA/DetailB
                }
                int wCount = CheckedCount(r.CompactIndex(), "bone weights");
                var weightPoint = new int[wCount];
                var weightValue = new int[wCount];
                for (int i = 0; i < wCount; i++)
                {
                    weightPoint[i] = r.UInt16();
                    weightValue[i] = r.UInt16();
                }
                // ── POINTS IN THE BONE'S LOCAL FRAME ──────────────────────────
                // The last table needed by skinning in USkeletalMesh::GetFrame:
                // vertex positions expressed in their bone's frame (stride 12 = FVector).
                int localCount = CheckedCount(r.CompactIndex(), "local points");
                var localPoints = new Vector3[localCount];
                for (int i = 0; i < localCount; i++)
                    localPoints[i] = new Vector3(r.Single(), r.Single(), r.Single());
                r.UInt32(); // SkeletalDepth
                int defaultAnimation = r.CompactIndex();
                // ── WEAPON ATTACHMENT ─────────────────────────────────────
                // WeaponBoneIndex and WeaponAdjust (FCoords: Origin + three axes)
                // follow DefaultAnimation. The frontend store carries them
                // (`weaponBoneIdx`, `weaponAdjustOrigin`, `weaponAdjustAxis`), and
                // the skinner builds the wand attachment point from them.
                int weaponBone = unchecked((int)r.UInt32());
                var weaponOrigin = new Vector3(r.Single(), r.Single(), r.Single());
                var weaponAxis = new float[9];
                for (int i = 0; i < 9; i++) weaponAxis[i] = r.Single();
                IReadOnlyDictionary<string, Hp1AnimSequence> sequences =
                    new Dictionary<string, Hp1AnimSequence>(UEPropNameComparer.Instance);
                string animObject = null;
                if (defaultAnimation > 0)
                {
                    if (defaultAnimation > Exports.Length)
                        throw new InvalidDataException($"invalid DefaultAnimation ref {defaultAnimation} on {export.Name}");
                    PackageExport animationExport = Exports[defaultAnimation - 1];
                    if (!ClassName(animationExport).Equals("Animation", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"{export.Name} DefaultAnimation is {ClassName(animationExport)}");
                    sequences = ReadAnimationSequences(animationExport);
                    animObject = animationExport.Name;
                }
                else if (defaultAnimation < 0)
                    throw new NotSupportedException($"cross-package DefaultAnimation on {export.Name}");
                return new Hp1SkeletalMesh(export.Name, bones, sequences,
                    meshScale, meshOrigin, meshRotPitch, meshRotYaw, meshRotRoll,
                    renderExtent)
                    { AnimObject = animObject, Points = points, TriVerts = triVerts,
                      WeightStart = weightStart, WeightNumber = weightNumber,
                      WeightPoint = weightPoint, WeightValue = weightValue,
                      LocalPoints = localPoints,
                      WedgePoint = wedgePoint, WedgeU = wedgeU, WedgeV = wedgeV,
                      FaceWedges = faceWedges, FaceMaterial = faceMaterial,
                      MaterialFlags = materialFlags, MaterialTextureIndex = materialTexIdx,
                      TextureLeaf = textureLeaf, TextureOuter = textureOuter,
                      TexturePath = texturePath,
                      RemapAnimVerts = remapAnimVerts,
                      ExtWedgePoint = extWedgePoint, ExtWedgeU = extWedgeU,
                      ExtWedgeV = extWedgeV,
                      WeaponBoneIndex = weaponBone, WeaponAdjustOrigin = weaponOrigin,
                      WeaponAdjustAxis = weaponAxis,
                      SpecialFaceCount = skelSpecialFaces, SpecialVerts = skelSpecialVerts,
                      ModelVerts = skelModelVerts,
                      Lod = new Hp1MeshLod(collapsePoints.Length, faceLevel, collapseWedges, skelLodStrength, skelLodMinVerts, skelFrameVerts),
                      PrimitiveBox = primitiveBox, BoundingBoxes = boundingBoxes };
            }

            // An animation object by itself: input of Actor.SkelAnim (see the catalog's
            // GetAnimation); same parse as the mesh's DefaultAnimation.
            public IReadOnlyDictionary<string, Hp1AnimSequence> ReadAnimation(PackageExport export)
                => ReadAnimationSequences(export);

            private IReadOnlyDictionary<string, Hp1AnimSequence> ReadAnimationSequences(PackageExport export)
            {
                var r = new RawReader(_data) { Position = export.Offset };
                SkipTaggedProperties(r);

                int refBoneCount = CheckedCount(r.CompactIndex(), "animation ref bones");
                var refBones = new Hp1AnimBone[refBoneCount];
                for (int i = 0; i < refBoneCount; i++)
                    refBones[i] = new Hp1AnimBone(Name(r.CompactIndex()), r.UInt32(), r.Int32());

                int moveCount = CheckedCount(r.CompactIndex(), "animation moves");
                var moves = new Hp1AnimMoveDescriptor[moveCount];
                int quaternionOffset = 0, positionOffset = 0, timeOffset = 0;
                for (int i = 0; i < moveCount; i++)
                {
                    var rootSpeed = new Vector3(r.Single(), r.Single(), r.Single());
                    float trackTime = r.Single();
                    uint startBone = r.UInt32();
                    uint flags = r.UInt32();
                    int boneIndexCount = CheckedCount(r.CompactIndex(), "animation bone indices");
                    var boneIndices = new uint[boneIndexCount];
                    for (int j = 0; j < boneIndexCount; j++) boneIndices[j] = r.UInt32();
                    int tracks = CheckedCount(r.CompactIndex(), "animation tracks");
                    var descriptors = new Hp1AnimTrackDescriptor[tracks];
                    for (int j = 0; j < tracks; j++)
                    {
                        uint trackFlags = r.UInt32();
                        int quaternionCount = CheckedCount(r.CompactIndex(), "compressed quaternion count");
                        int positionCount = CheckedCount(r.CompactIndex(), "compressed position count");
                        int keyTimeCount = CheckedCount(r.CompactIndex(), "key-time count");
                        float positionScale = r.Single();
                        float timeScale = r.Single();
                        descriptors[j] = new Hp1AnimTrackDescriptor(trackFlags,
                            quaternionOffset, quaternionCount, positionOffset, positionCount,
                            timeOffset, keyTimeCount, positionScale, timeScale);
                        quaternionOffset = checked(quaternionOffset + quaternionCount);
                        positionOffset = checked(positionOffset + positionCount);
                        timeOffset = checked(timeOffset + keyTimeCount);
                    }
                    moves[i] = new Hp1AnimMoveDescriptor(rootSpeed, trackTime, startBone,
                        flags, boneIndices, descriptors);
                }

                int sequenceCount = CheckedCount(r.CompactIndex(), "animation sequences");
                var sequenceHeaders = new Hp1AnimSequenceHeader[sequenceCount];
                for (int i = 0; i < sequenceCount; i++)
                {
                    string name = Name(r.CompactIndex());
                    string group = Name(r.CompactIndex());
                    int startFrame = r.Int32();
                    int numFrames = r.Int32();
                    int notifyCount = CheckedCount(r.CompactIndex(), "animation notifies");
                    var notifies = new Hp1AnimNotify[notifyCount];
                    for (int j = 0; j < notifyCount; j++)
                        notifies[j] = new Hp1AnimNotify(r.Single(), Name(r.CompactIndex()));
                    float rate = r.Single();
                    sequenceHeaders[i] = new Hp1AnimSequenceHeader(
                        name, group, startFrame, numFrames, rate, notifies);
                }

                // UAnimation::Serialize stores the three compressed pools after
                // Moves and Sequences (Engine.dll.c:45383-45410). On load it wires
                // each MotionChunk track to consecutive 6-byte FAnimVec quaternion/
                // position entries and one-byte delta-time entries.
                int quaternionPoolCount = CheckedCount(r.CompactIndex(), "animation quaternion pool");
                var quaternionPool = new short[checked(quaternionPoolCount * 3)];
                for (int i = 0; i < quaternionPool.Length; i++) quaternionPool[i] = r.Int16();
                int positionPoolCount = CheckedCount(r.CompactIndex(), "animation position pool");
                var positionPool = new short[checked(positionPoolCount * 3)];
                for (int i = 0; i < positionPool.Length; i++) positionPool[i] = r.Int16();
                int timePoolCount = CheckedCount(r.CompactIndex(), "animation time pool");
                var timePool = new byte[timePoolCount];
                for (int i = 0; i < timePool.Length; i++) timePool[i] = r.Byte();

                if (quaternionPoolCount != quaternionOffset
                    || positionPoolCount != positionOffset || timePoolCount != timeOffset)
                    throw new InvalidDataException(
                        $"{export.Name} compressed pool totals do not match tracks: "
                        + $"q={quaternionPoolCount}/{quaternionOffset} "
                        + $"p={positionPoolCount}/{positionOffset} t={timePoolCount}/{timeOffset}");
                if (sequenceCount != moveCount)
                    throw new InvalidDataException(
                        $"{export.Name} sequence/movement count mismatch {sequenceCount}/{moveCount}");

                var result = new Dictionary<string, Hp1AnimSequence>(UEPropNameComparer.Instance);
                for (int i = 0; i < sequenceCount; i++)
                {
                    Hp1AnimSequenceHeader h = sequenceHeaders[i];
                    Hp1AnimMoveDescriptor m = moves[i];
                    var tracks = new Hp1AnimTrack[m.Tracks.Length];
                    for (int j = 0; j < tracks.Length; j++)
                    {
                        Hp1AnimTrackDescriptor t = m.Tracks[j];
                        var q = new short[checked(t.QuaternionCount * 3)];
                        Array.Copy(quaternionPool, checked(t.QuaternionOffset * 3), q, 0, q.Length);
                        var p = new short[checked(t.PositionCount * 3)];
                        Array.Copy(positionPool, checked(t.PositionOffset * 3), p, 0, p.Length);
                        var times = new byte[t.TimeCount];
                        Array.Copy(timePool, t.TimeOffset, times, 0, times.Length);
                        tracks[j] = new Hp1AnimTrack(t.Flags, q, p, times,
                            t.PositionScale, t.TimeScale);
                    }
                    var movement = new Hp1AnimMove(m.RootSpeed, m.TrackTime, m.StartBone,
                        m.Flags, m.BoneIndices, tracks, refBones);
                    if (!result.TryAdd(h.Name, new Hp1AnimSequence(
                        h.Name, h.Group, h.StartFrame, h.NumFrames, h.Rate, h.Notifies, movement) { Index = i }))
                        throw new InvalidDataException($"duplicate animation sequence {export.Name}.{h.Name}");
                }
                return result;
            }





        }

        private sealed record Hp1AnimSequenceHeader(string Name, string Group,
            int StartFrame, int NumFrames, float Rate, Hp1AnimNotify[] Notifies);
        private sealed record Hp1AnimMoveDescriptor(Vector3 RootSpeed, float TrackTime,
            uint StartBone, uint Flags, uint[] BoneIndices, Hp1AnimTrackDescriptor[] Tracks);
        private sealed record Hp1AnimTrackDescriptor(uint Flags,
            int QuaternionOffset, int QuaternionCount,
            int PositionOffset, int PositionCount,
            int TimeOffset, int TimeCount, float PositionScale, float TimeScale);

    }


    // Vertex mesh (Mesh/LodMesh): frame pool and face layout.
    public sealed record Hp1VertexMesh(string Name, Vector3[] Verts, int FrameVerts, int AnimFrames)
    {
        public int[] WedgePoint { get; init; } = System.Array.Empty<int>();
        public byte[] WedgeU { get; init; } = System.Array.Empty<byte>();
        public byte[] WedgeV { get; init; } = System.Array.Empty<byte>();
        public int[] FaceWedges { get; init; } = System.Array.Empty<int>();
        public int[] FaceMaterial { get; init; } = System.Array.Empty<int>();
        public int[] MaterialFlags { get; init; } = System.Array.Empty<int>();
        public int[] MaterialTextureIndex { get; init; } = System.Array.Empty<int>();
        public string[] TexturePath { get; init; } = System.Array.Empty<string>();
        // Vertex connectivity: per frame vertex, how many triangles use it
        // and where in the `VertLinks` list they start.
        public int[] ConnectNumTris { get; init; } = System.Array.Empty<int>();
        public int[] ConnectOffset { get; init; } = System.Array.Empty<int>();
        public int[] VertLinks { get; init; } = System.Array.Empty<int>();
        // ULodMesh tail (ULodMesh::Serialize, Engine.dll.c:138655): special
        // faces and level-of-detail selection numbers. `LodStrength` gates the whole
        // LOD block in shipped (URender::DrawLodMesh, 0x10b0ffd6).
        public int[] SpecialFaceWedges { get; init; } = System.Array.Empty<int>();
        public int[] SpecialFaceMaterial { get; init; } = System.Array.Empty<int>();
        public int ModelVerts { get; init; }
        public int SpecialVerts { get; init; }
        public Hp1MeshLod Lod { get; init; }
        public float MeshScaleMax { get; init; }
        public float LodHysteresis { get; init; }
        public float LodStrength { get; init; }
        public int LodMinVerts { get; init; }
        public float LodMorph { get; init; }
        public float LodZDisplace { get; init; }
        public int[] RemapAnimVerts { get; init; } = System.Array.Empty<int>();
        public int OldFrameVerts { get; init; }
        public Vector3 Scale { get; init; }
        public Vector3 Origin { get; init; }
        public int RotPitch { get; init; }
        public int RotYaw { get; init; }
        public int RotRoll { get; init; }
        // UPrimitive::BoundingBox (+0x28) and UMesh::BoundingBoxes (+0xa8): input of UMesh::GetRenderBoundingBox.
        public Hp1Box PrimitiveBox { get; init; }
        public Hp1Box[] BoundingBoxes { get; init; } = System.Array.Empty<Hp1Box>();
    }
    public sealed record Hp1Bone(string Name, int ParentIndex, int NumChildren)
    {
        // Bone reference pose from RefSkeleton: input for building FCoords in
        // USkeletalMesh::GetFrame (Engine.dll.c:29125).
        public float OrientX { get; init; }
        public float OrientY { get; init; }
        public float OrientZ { get; init; }
        public float OrientW { get; init; }
        public Vector3 Position { get; init; }
        public float Length { get; init; }
        public Vector3 Scale { get; init; }
        // Bone flags from RefSkeleton: the frontend store carries them, and they
        // must not be invented on export.
        public uint Flags { get; init; }
    }

    // Hp1MeshHashFields (the UMesh fields feeding the collision-hash extent box,
    // see ReadMeshHashFields) moved to Core UEBspCollision with HashBoxes (layer C).

    // UEModelRef (the mover-Brush model reference) moved to Core UEValueTypes —
    // the WorldFrame projection needs the typed identity.

    // Shipped FBox: Min, Max, IsValid (25 bytes serialized).
    public readonly struct Hp1Box
    {
        public readonly Vector3 Min, Max;
        public readonly bool IsValid;
        public Hp1Box(Vector3 min, Vector3 max, bool isValid) { Min = min; Max = max; IsValid = isValid; }
    }

    // ULodMesh level-of-detail data (ULodMesh::Serialize): the CollapsePointThus count (the DrawLodMesh gate reads only
    // Num, +0x130), FaceLevel per face, CollapseWedgeThus per wedge, LODStrength (+0x18c), LODMinVerts (+0x190, clamped).
    public sealed record Hp1MeshLod(int CollapsePointCount, int[] FaceLevel, int[] CollapseWedge, float Strength, int MinVerts, int FrameVerts);

    public sealed record Hp1SkeletalMesh(
        string Name, Hp1Bone[] Bones, IReadOnlyDictionary<string, Hp1AnimSequence> Sequences,
        Vector3 Scale, Vector3 Origin, int RotPitch, int RotYaw, int RotRoll,
        Vector3 RenderExtent)
    {
        // The DefaultAnimation export's object name (join key for the anim
        // metadata cross-validation); null when the mesh has no animation object.
        public string AnimObject { get; init; }

        // UPrimitive::BoundingBox (+0x28) and UMesh::BoundingBoxes (+0xa8):
        // input of USkeletalMesh::GetRenderBoundingBox.
        public Hp1Box PrimitiveBox { get; init; }
        public Hp1Box[] BoundingBoxes { get; init; } = System.Array.Empty<Hp1Box>();

        // ── GEOMETRY FOR THE DIST_OwnerMesh BRANCH ──────────────────────────────
        //
        // `Points` are the source vertex positions (USkeletalMesh Points), `TriVerts` are
        // index triples, exactly those shipped returns via `GetTriVerts`
        // (stride 0x14, three uint16 at 0/2/4).
        //
        // These are SOURCE points, not a pose. The branch takes vertices from the buffer
        // filled by `GetFrame`, i.e. after ApplyAnim and bone skinning;
        // a consumer must not treat `Points` as a posed result.
        public Vector3[] Points { get; init; } = System.Array.Empty<Vector3>();
        public int[] TriVerts { get; init; } = System.Array.Empty<int>();

        // ── LAYOUT FOR THE RAW FRONTEND STORE EXPORT ──────────────────
        //
        // Wedges, faces, materials and texture references. The raw frontend store cannot
        // be built without them: 13 SkeletalMesh class paths are missing from it entirely,
        // while the package carries them.
        public int[] WedgePoint { get; init; } = System.Array.Empty<int>();
        public byte[] WedgeU { get; init; } = System.Array.Empty<byte>();
        public byte[] WedgeV { get; init; } = System.Array.Empty<byte>();
        public int[] FaceWedges { get; init; } = System.Array.Empty<int>();
        public int[] FaceMaterial { get; init; } = System.Array.Empty<int>();
        public int[] MaterialFlags { get; init; } = System.Array.Empty<int>();
        public int[] MaterialTextureIndex { get; init; } = System.Array.Empty<int>();
        public string[] TextureLeaf { get; init; } = System.Array.Empty<string>();
        public string[] TextureOuter { get; init; } = System.Array.Empty<string>();
        public string[] TexturePath { get; init; } = System.Array.Empty<string>();
        public int[] RemapAnimVerts { get; init; } = System.Array.Empty<int>();
        public int[] ExtWedgePoint { get; init; } = System.Array.Empty<int>();
        public float[] ExtWedgeU { get; init; } = System.Array.Empty<float>();
        public float[] ExtWedgeV { get; init; } = System.Array.Empty<float>();
        public int WeaponBoneIndex { get; init; } = -1;
        public Vector3 WeaponAdjustOrigin { get; init; }
        public float[] WeaponAdjustAxis { get; init; } = System.Array.Empty<float>();
        // ULodMesh tail of a skeletal mesh: special face count and SpecialVerts
        // (DrawLodMesh weapon-triangle gate).
        public int SpecialFaceCount { get; init; }
        public int SpecialVerts { get; init; }
        public int ModelVerts { get; init; }
        public Hp1MeshLod Lod { get; init; }

        // Bone weights: per bone the start and count of records, per record the point and
        // weight (FBoneWeightIndex / FBoneWeight).
        public int[] WeightStart { get; init; } = System.Array.Empty<int>();
        public int[] WeightNumber { get; init; } = System.Array.Empty<int>();
        public int[] WeightPoint { get; init; } = System.Array.Empty<int>();
        public int[] WeightValue { get; init; } = System.Array.Empty<int>();

        // ── REFERENCE SKELETON POSE: VERTICES FROM BONES ────────────────────────
        //
        // Port of the rule `USkeletalMesh::GetFrame` (Engine.dll.c:29125) applies to
        // every bone: `FCoords` from the bone placement, divided by the PARENT
        // coordinates down the hierarchy, then vertices from the bone's local frame.
        //
        // The rule is MEASURED on shipped data: the package carries both `Points` and
        // `LocalPoints`, so applying the coordinates to the local points MUST reproduce
        // the world ones. Of four variants on a 75-bone mesh (492 vertices) exactly one
        // matches: conjugate quaternion and order `M_local * M_parent`; the other three
        // miss on all 492 vertices.
        //
        // This is the REFERENCE pose, not a frame pose: the result is valid for an actor
        // WITHOUT a playing animation; the caller must check that and stop loudly otherwise.
        // POSE AT A TRACK KEY: oracle for "where is the bottom of the mesh".
        //
        // Computed EXACTLY AT THE KEY, without interpolation: shipped quaternion
        // interpolation is not analysed separately, and it is not needed for
        // geometry placement; real frames suffice. Same composition as the binding
        // (`QuatBasis` of the CONJUGATE quaternion), with the animation key in place of the bone.
        //
        // Loud limitation: single-bone mesh only. This probe does not assemble a multi-bone
        // chain and never returns a silently approximate answer.
        public Vector3[] ComputePosePointsAtKey(Hp1AnimTrack track, int quatKey,
            int positionKey, bool applyRootPosition)
        {
            if (Bones.Length != 1)
                throw new NotSupportedException(
                    $"{Name}: the pose probe is built only for a single-skeleton mesh, bones {Bones.Length}");
            if (track.QuaternionCount <= quatKey || quatKey < 0)
                throw new ArgumentOutOfRangeException(nameof(quatKey),
                    $"{Name}: rotation keys {track.QuaternionCount}, requested {quatKey}");
            track.DecodeQuaternion(quatKey, out float qx, out float qy, out float qz, out float qw);
            float[] basis = QuatBasis(-qx, -qy, -qz, qw);
            Vector3 origin = applyRootPosition && track.PositionCount > positionKey && positionKey >= 0
                ? track.DecodePosition(positionKey)
                : new Vector3(0f, 0f, 0f);
            var result = new Vector3[Points.Length];
            int start = WeightStart.Length > 0 ? WeightStart[0] : 0;
            int count = WeightNumber.Length > 0 ? WeightNumber[0] : 0;
            for (int k = start; k < start + count && k < LocalPoints.Length; k++)
            {
                int point = WeightPoint[k];
                if ((uint)point >= (uint)result.Length) continue;
                result[point] = Transform(basis, origin, LocalPoints[k]);
            }
            return result;
        }

        public Vector3[] ComputeReferencePosePoints()
        {
            var basis = new float[Bones.Length][];      // 9 numbers per bone
            var origin = new Vector3[Bones.Length];
            var result = new Vector3[Points.Length];
            for (int i = 0; i < Bones.Length; i++)
            {
                Hp1Bone b = Bones[i];
                float[] local = QuatBasis(-b.OrientX, -b.OrientY, -b.OrientZ, b.OrientW);
                if (i == 0 || b.ParentIndex == i)
                {
                    basis[i] = local;
                    origin[i] = b.Position;
                }
                else
                {
                    float[] pm = basis[b.ParentIndex];
                    basis[i] = Multiply(local, pm);
                    origin[i] = Transform(pm, origin[b.ParentIndex], b.Position);
                }
            }
            return SkinPoints(this, basis, i => origin[i]);
        }

        // VERTICES FROM BONE FRAMES: USkeletalMesh::GetFrame (Engine.dll.c, vertex loop after the bone chain):
        //   * one influence group (+0x1d8 == 1): point = bone frame(LocalPoint), point count = min(count, NumPoints);
        //   * otherwise a WEIGHTED SUM: acc[point] += w * bone frame(LocalPoint), w = (float)ushort * 1.5259022e-05
        //     (= 1/65535), iterating bone groups and their weights, leaving a group at point >= NumPoints.
        // Assigning instead of summing (last bone wins) is nearly invisible on meshes with 1-3 influences but
        // crushes the chest of Neville's skeleton (Fred/George, up to 13 influences per point).
        public static Vector3[] SkinPoints(Hp1SkeletalMesh m, float[][] basis, Func<int, Vector3> origin)
        {
            var result = new Vector3[m.Points.Length];
            int groups = m.WeightStart.Length;
            if (groups == 1)
            {
                int count = Math.Min(m.WeightNumber.Length > 0 ? m.WeightNumber[0] : 0, m.Points.Length);
                for (int k = m.WeightStart[0]; k < m.WeightStart[0] + count && k < m.LocalPoints.Length; k++)
                {
                    int point = m.WeightPoint[k];
                    if ((uint)point < (uint)result.Length) result[point] = Transform(basis[0], origin(0), m.LocalPoints[k]);
                }
                return result;
            }
            for (int i = 0; i < groups && i < basis.Length; i++)
            {
                int start = m.WeightStart[i], count = i < m.WeightNumber.Length ? m.WeightNumber[i] : 0;
                Vector3 o = origin(i);
                for (int k = start; k < start + count && k < m.LocalPoints.Length; k++)
                {
                    int point = m.WeightPoint[k];
                    if (point >= result.Length) break;
                    float w = m.WeightValue[k] * 1.5259022e-05f;
                    Vector3 t = Transform(basis[i], o, m.LocalPoints[k]);
                    result[point] = new Vector3(result[point].x + w * t.x, result[point].y + w * t.y, result[point].z + w * t.z);
                }
            }
            return result;
        }

        static float[] QuatBasis(float x, float y, float z, float w) => new[]
        {
            1 - 2 * (y * y + z * z), 2 * (x * y + w * z),     2 * (x * z - w * y),
            2 * (x * y - w * z),     1 - 2 * (x * x + z * z), 2 * (y * z + w * x),
            2 * (x * z + w * y),     2 * (y * z - w * x),     1 - 2 * (x * x + y * y),
        };

        static float[] Multiply(float[] a, float[] b)
        {
            var m = new float[9];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    m[i * 3 + j] = a[i * 3] * b[j] + a[i * 3 + 1] * b[3 + j] + a[i * 3 + 2] * b[6 + j];
            return m;
        }

        // Row vector times matrix, as measured: v * M + O.
        static Vector3 Transform(float[] m, Vector3 o, Vector3 v) => new Vector3(
            m[0] * v.x + m[3] * v.y + m[6] * v.z + o.x,
            m[1] * v.x + m[4] * v.y + m[7] * v.z + o.y,
            m[2] * v.x + m[5] * v.y + m[8] * v.z + o.z);

        // Vertices in their bone's frame (USkeletalMesh LocalPoints).
        public Vector3[] LocalPoints { get; init; } = System.Array.Empty<Vector3>();

        public int FindBone(string name)
        {
            for (int i = 0; i < Bones.Length; i++)
                if (Bones[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        public Hp1AnimSequence FindSequence(string name)
            => name is not null && Sequences.TryGetValue(name, out Hp1AnimSequence sequence) ? sequence : null;
    }
    public sealed record Hp1AnimSequence(
        string Name, string Group, int StartFrame, int NumFrames, float Rate,
        Hp1AnimNotify[] Notifies, Hp1AnimMove Movement)
    {
        // Index in the animation object's Sequences array (UAnimation +0x64, stride 0x20):
        // FUN_1041b500 uses it to pick the UMesh::BoundingBoxes box. -1 for
        // vertex mesh sequences.
        public int Index { get; init; } = -1;
    }
    public sealed record Hp1AnimNotify(float Time, string Function);
    public sealed record Hp1AnimBone(string Name, uint Flags, int ParentIndex);
    public sealed record Hp1AnimMove(Vector3 RootSpeed, float TrackTime,
        uint StartBone, uint Flags, uint[] BoneIndices, Hp1AnimTrack[] Tracks,
        Hp1AnimBone[] RefBones);
    public sealed record Hp1AnimTrack(uint Flags, short[] QuaternionKeys,
        short[] PositionKeys, byte[] KeyTimes, float PositionScale, float TimeScale)
    {
        public int QuaternionCount => QuaternionKeys.Length / 3;
        public int PositionCount => PositionKeys.Length / 3;

        // COMPRESSED QUATERNION UNPACKING: 1:1 `FAnimVec::Quat`
        // (Engine.dll.c:61416-61441). A key takes 6 bytes (three shorts), and
        // each component is the SINE of the integer key, not a division
        // by the range:
        //     x = sin(k0*C), y = sin(k1*C), z = sin(k2*C)
        //     w = sqrt(max(0, 1 - x^2 - y^2 - z^2))
        // C was read as BYTES from `.data` of shipped Engine.dll at
        // VA 0x104db9b8 (ImageBase 0x10300000 -> file offset 0x1db9b8, bytes
        // 6d 11 49 38): float32 4.793836342287e-05, i.e. (pi/2)/32767.
        // The similar-looking (pi/2)/32768 = 4.79369e-05 is REJECTED by measurement:
        // it differs in the seventh digit.
        public const float QuatKeyScale = 4.793836342287e-05f;

        // appSqrt argument in FQuat::FQuat: exact 64-bit register chain,
        // then fstp qword (53 bits). A sum of float squares is exact in 64 bits
        // almost always, in double it is not, hence X87Ext rather than double.
        public static double QuatWSquare(float x, float y, float z)
        {
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
            {
                double d = ((1.0 - (double)x * x) - (double)y * y) - (double)z * z;
                return 0.0 < d || double.IsNaN(d) ? d : 0.0;
            }
            HP1.Core.X87Ext X(float v) => HP1.Core.X87Ext.From(v);
            HP1.Core.X87Ext R(HP1.Core.X87Ext v) => HP1.Core.X87Ext.Round64(v);
            HP1.Core.X87Ext t = R(R(R(HP1.Core.X87Ext.From(1) - X(x) * X(x)) - X(y) * X(y)) - X(z) * X(z));
            return t.Sign > 0 ? t.ToDouble() : 0.0;
        }

        // Keys are immutable package data, so unpacking is done once per track
        // (the exact x87 chain via X87Ext cost ~0.8 ms per frame when unpacked on every call;
        // measured over 1000 headless Tut1 frames).
        float[] _decoded, _decoded24;

        // FAnimVec::Quat sets no control word: GetFrame's full ApplyAnim and GetRenderBoundingBox's root-only one run
        // inside the render window (UEWorldFrameProducer.FastMathWindow), where the chain is PC24 (DecodeAll24).
        public void DecodeQuaternion(int key, out float x, out float y, out float z, out float w)
        {
            float[] d = UEWorldFrameProducer.FastMathWindow ? _decoded24 ??= DecodeAll24(QuaternionKeys)
                : _decoded ??= DecodeAll(QuaternionKeys);
            int i = checked(key * 4);
            x = d[i]; y = d[i + 1]; z = d[i + 2]; w = d[i + 3];
        }

        static float[] DecodeAll(short[] keys)
        {
            var d = new float[keys.Length / 3 * 4];
            for (int k = 0, o = 0; k + 2 < keys.Length; k += 3, o += 4)
            {
                // Inline fsin (FAnimVec::Quat, Engine 0x1041a120): UEX87Transcendental (D-71), fstp dword.
                float x = UEX87Transcendental.Fsin(X87Ext.From(keys[k] * (double)QuatKeyScale)).ToFloat();
                float y = UEX87Transcendental.Fsin(X87Ext.From(keys[k + 1] * (double)QuatKeyScale)).ToFloat();
                float z = UEX87Transcendental.Fsin(X87Ext.From(keys[k + 2] * (double)QuatKeyScale)).ToFloat();
                // W: FQuat::FQuat(x,y,z) (Core 0x10120310, called by ApplyAnim,
                // Engine.dll.c:142231); the same body is inlined in FAnimVec::Quat (Engine
                // 0x1041a16a..0x1041a1b9): ((1 - x^2) - y^2) - z^2 in the x87 register (PC64,
                // `fsubr; fsubp; fsubp` without stores), zero when NOT 0 < t (`fcomp; test
                // ah,1`: NaN passes through), fstp qword, appSqrt, fstp dword.
                // appSqrt (Core 0x10126100) is `fld qword; fsqrt; ret`: root in the PC64 register
                // (64 bits), to float only via fstp dword. Math.Sqrt rounds the root
                // to 53 bits, but for a square root double rounding p -> 24 is harmless when p >= 2*24+2
                // (both 53 and 64): both give the correctly rounded float root. A search over 2e7
                // random keys (Math.Sqrt vs X87Ext.Sqrt64) found 0 differences, so Math.Sqrt is kept.
                double t = QuatWSquare(x, y, z);
                d[o] = x; d[o + 1] = y; d[o + 2] = z;
                d[o + 3] = (float)Math.Sqrt(t);
            }
            return d;
        }

        // FAnimVec::Quat (Engine 0x1041a120..0x1041a1b9) at PC24: fild key; fmul dword C rounds the argument to 24 bits
        // before fsin (fsin itself is not rounded by the precision control); w: x·x, 1 − ·, − y·y, − z·z each round to
        // 24 bits; zero unless 0 < t (NaN passes); appSqrt's fsqrt rounds to 24 bits. Checked against the original at PC24.
        static float[] DecodeAll24(short[] keys)
        {
            var d = new float[keys.Length / 3 * 4];
            for (int k = 0, o = 0; k + 2 < keys.Length; k += 3, o += 4)
            {
                float x = UEX87Transcendental.Fsin(X87Ext.From(keys[k] * QuatKeyScale)).ToFloat();
                float y = UEX87Transcendental.Fsin(X87Ext.From(keys[k + 1] * QuatKeyScale)).ToFloat();
                float z = UEX87Transcendental.Fsin(X87Ext.From(keys[k + 2] * QuatKeyScale)).ToFloat();
                float t = ((1f - x * x) - y * y) - z * z;
                if (!(0f < t) && !float.IsNaN(t)) t = 0f;
                d[o] = x; d[o + 1] = y; d[o + 2] = z;
                d[o + 3] = MathF.Sqrt(t);
            }
            return d;
        }

        // Key position: the same multiplier as the root sampler
        // (PositionScale/32767; Engine.dll.c multiplier 3.051851e-05 = 1/32767
        // is applied to the ALREADY scaled field; same form as the
        // SampleTrack track sampling in UELevelRuntime.SkeletalPose).
        // Every ApplyAnim decode site (e.g. 0x1041c14f..0x1041c186): fld PositionScale; fmul 1/32767f (0x1047b438) — the
        // scale stays in the register; fild key; fmul; fstp dword. The body sets no control word: PC24 inside the render
        // window (UEWorldFrameProducer.FastMathWindow), PC64 for game-logic callers (BonePos, PHYS_Trailer, root motion).
        public Vector3 DecodePosition(int key)
        {
            int i = checked(key * 3);
            bool pc24 = UEWorldFrameProducer.FastMathWindow;
            return new Vector3(DecodeCoord(PositionKeys[i], PositionScale, pc24), DecodeCoord(PositionKeys[i + 1], PositionScale, pc24),
                DecodeCoord(PositionKeys[i + 2], PositionScale, pc24));
        }

        internal static float DecodeCoord(short key, float positionScale, bool pc24)
        {
            if (pc24 || !float.IsFinite(positionScale)) return key * (positionScale * (1f / 32767f));
            return X87Ext.Round64(X87Ext.From((int)key) * (X87Ext.From(positionScale) * X87Ext.From(1f / 32767f))).ToFloat();
        }
    }
}
