using System;
using System.Collections.Generic;
using System.IO;
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    // The Unreal package FORMAT primitive: header, name/export/import tables,
    // the tagged-property skip, and the UMesh walk that yields the collision
    // hash's extent fields.
    //
    // Core-owned because collision needs mesh bounds; feeding them in through a
    // frontend hook would put identity data outside the kernel that dispatches on it.
    //
    // Only the FORMAT lives here, not the skeletal-animation model. A frontend
    // that also wants bones, sequences or render meshes derives from this and
    // adds those readers over the same parsed tables, so there is exactly ONE
    // parser for this byte layout rather than a Core copy and a frontend copy
    // drifting apart.
    public class UEPackageReader
    {
        protected readonly byte[] _data;
        readonly string[] _names;
        readonly PackageImport[] _imports;
        public readonly PackageExport[] Exports;
        protected readonly int _version;
        public readonly string PackageName;

        public UEPackageReader(string path)
        {
            PackageName = Path.GetFileNameWithoutExtension(path);
            _data = File.ReadAllBytes(path);
            var r = new RawReader(_data);
            if (r.UInt32() != 0x9e2a83c1) throw new InvalidDataException("not an Unreal package: " + path);
            _version = r.UInt16();
            r.UInt16(); // licensee version
            r.UInt32(); // package flags
            int nameCount = CheckedCount(r.Int32(), "names");
            int nameOffset = r.Int32();
            int exportCount = CheckedCount(r.Int32(), "exports");
            int exportOffset = r.Int32();
            int importCount = CheckedCount(r.Int32(), "imports");
            int importOffset = r.Int32();

            r.Position = nameOffset;
            _names = new string[nameCount];
            // THE NAME TABLE DEPENDS ON THE PACKAGE VERSION. From version 64 a name is written with a
            // length prefix (FString), before that as a NUL-terminated ANSI string. All HP1 content
            // is version 76, but the shipped data also holds four stock version-61 packages
            // (FireEng, GenFX, GreatFire, Palettes); without this branch they silently drop out of
            // any "all packages" walk.
            for (int i = 0; i < nameCount; i++)
            { _names[i] = _version < 64 ? r.CString() : r.FString(); r.UInt32(); }

            r.Position = exportOffset;
            Exports = new PackageExport[exportCount];
            for (int i = 0; i < exportCount; i++)
            {
                int cls = r.CompactIndex();
                r.CompactIndex(); // super
                int outer = r.Int32();
                string name = Name(r.CompactIndex());
                uint flags = r.UInt32();
                int size = r.CompactIndex();
                int offset = size > 0 ? r.CompactIndex() : 0;
                Exports[i] = new PackageExport(cls, name, size, offset, flags, outer);
            }

            r.Position = importOffset;
            _imports = new PackageImport[importCount];
            for (int i = 0; i < importCount; i++)
            {
                string classPackage = Name(r.CompactIndex());
                string className = Name(r.CompactIndex());
                // THE OUTER REFERENCE IS READ, NOT DISCARDED. Without it an import has only the name
                // LEAF, and leaves are ambiguous in the campaign — a leaf cannot name the package of
                // a slot texture.
                int importOuter = r.Int32();
                _imports[i] = new PackageImport(Name(r.CompactIndex()), importOuter, className, classPackage);
            }
        }

        public string ClassName(PackageExport export)
        {
            if (export.ClassIndex < 0 && -export.ClassIndex - 1 < _imports.Length)
                return _imports[-export.ClassIndex - 1].Name;
            if (export.ClassIndex > 0 && export.ClassIndex - 1 < Exports.Length)
                return Exports[export.ClassIndex - 1].Name;
            return "None";
        }

        // FULL OBJECT PATH: `Package.Group.Name` — the Outer chain bottom-up,
        // (full object path `Package.Group.Name`: the Outer chain bottom-up, rooted at the
        // package file name). In UE1 object identity is the path: the same leaf (`station`,
        // `snitch`, `window2`) occurs in different packages with DIFFERENT bMasked, so a table
        // keyed by leaf merges different objects.
        public string ObjectPath(int exportIndex)
        {
            if (exportIndex < 0 || exportIndex >= Exports.Length) return null;
            string path = Exports[exportIndex].Name;
            int outer = Exports[exportIndex].OuterIndex;
            // The chain is finite by construction, but a cycle in corrupt data must not hang the
            // reader — depth is bounded by the table length.
            for (int guard = 0; outer != 0 && guard <= Exports.Length; guard++)
            {
                if (outer > 0)
                {
                    if (outer - 1 >= Exports.Length) break;
                    path = Exports[outer - 1].Name + "." + path;
                    outer = Exports[outer - 1].OuterIndex;
                }
                else
                {
                    if (-outer - 1 >= _imports.Length) break;
                    path = _imports[-outer - 1].Name + "." + path;
                    break;                      // an import's Outer chain is not read here
                }
            }
            // The root is the package file name; if the Outer chain already produced it
            // (object under an import package), it is not prepended twice.
            if (!string.Equals(path, PackageName, StringComparison.OrdinalIgnoreCase)
                && !path.StartsWith(PackageName + ".", StringComparison.OrdinalIgnoreCase))
                path = PackageName + "." + path;
            return path;
        }

        // LEAF AND NEAREST OUTER OF AN OBJECT REFERENCE (positive — export, negative — import,
        // zero — None).
        //
        // WHY A PAIR. The frontend mesh store keeps a slot texture as (leaf, package) and
        // restores the full path from the registry. A leaf alone is ambiguous in the campaign
        // (`station`, `snitch`, `window2` live in different packages with different bMasked), so
        // the nearest Outer is half of the identity, not decoration.
        public (string Leaf, string Outer) ObjectLeafAndOuter(int objectRef)
        {
            if (objectRef > 0 && objectRef - 1 < Exports.Length)
            {
                PackageExport e = Exports[objectRef - 1];
                return (e.Name, OuterNameOf(e.OuterIndex));
            }
            if (objectRef < 0 && -objectRef - 1 < _imports.Length)
            {
                PackageImport im = _imports[-objectRef - 1];
                return (im.Name, OuterNameOf(im.OuterIndex));
            }
            return (null, null);
        }

        // FULL PATH OF AN OBJECT REFERENCE, imports included. For an export it is `ObjectPath`;
        // for an import the Outer chain is unwound to the root — the import root is the name of
        // the FOREIGN package, unobtainable without unwinding (the table holds only the leaf).
        public string ObjectFullPath(int objectRef)
        {
            if (objectRef > 0) return ObjectPath(objectRef - 1);
            if (objectRef == 0) return null;
            int idx = -objectRef - 1;
            if (idx >= _imports.Length) return null;
            // Import chain up to the root; an import nested in an EXPORT of this package does
            // not occur in the campaign — such a path is built as is, without VerifyImport.
            var chain = new List<(string Name, string ClassName, string ClassPackage)>();
            string path = _imports[idx].Name;
            bool allImports = true;
            chain.Add((_imports[idx].Name, _imports[idx].ClassName, _imports[idx].ClassPackage));
            int outer = _imports[idx].OuterIndex;
            for (int guard = 0; outer != 0 && guard <= _imports.Length + Exports.Length; guard++)
            {
                if (outer > 0)
                {
                    if (outer - 1 >= Exports.Length) break;
                    allImports = false;
                    path = Exports[outer - 1].Name + "." + path;
                    outer = Exports[outer - 1].OuterIndex;
                }
                else
                {
                    if (-outer - 1 >= _imports.Length) break;
                    PackageImport im = _imports[-outer - 1];
                    path = im.Name + "." + path;
                    chain.Add((im.Name, im.ClassName, im.ClassPackage));
                    outer = im.OuterIndex;
                }
            }
            if (!allImports) return path;
            chain.Reverse();
            return HP1.Core.UEClassRegistry.Shared?.VerifyImportPath(chain) ?? path;
        }

        string OuterNameOf(int outer)
        {
            if (outer > 0 && outer - 1 < Exports.Length) return Exports[outer - 1].Name;
            if (outer < 0 && -outer - 1 < _imports.Length) return _imports[-outer - 1].Name;
            return null;
        }

        // The UMesh walk up to the scalar tail, capturing what the collision
        // hash extent needs (UMesh::GetCollisionBoundingBox reads BoundingBox
        // +0x28, Scale +0x100, Origin +0x10c, RotOrigin +0x118). The walk is
        // the same validated chain ReadSkeletalMesh uses; the mid-stream
        // BoundingBox/Sphere DUPLICATE (UMesh legacy re-serialization) must
        // byte-match the UPrimitive header — a built-in checksum that fails
        // loudly on any walk drift.
        public Hp1MeshHashFields ReadMeshHashFields(PackageExport export)
            => ReadMeshPrefix(new RawReader(_data) { Position = export.Offset }, export);

        // The UMesh prefix, shared by the hash-extent fields and the RefSkeleton
        // read: on return the reader sits just past the UMesh scalar tail. One
        // parse, so the two readers cannot drift apart.
        // ── LodMesh DRAW BRANCH: WHAT THE MESH HAS ──────────────────────────
        //
        // Shipped builds a LodMesh frame in ULodMesh::GetFrame (Engine.dll.c:57172; disasm
        // 0x1035eb4b): without a resolved sequence it takes vertex pool offset 0; a resolved one
        // gives per-frame vertex animation at offset `(StartFrame + frame) * FrameVerts`.
        //
        // Core only reads data here; drawing is the frontend's (HP1VertexAnimator), whose
        // behaviour in this tree is not verified. Deciding whether there is a defect needs DATA:
        // how many frames the sequences have and their names. This pass delivers them — with
        // the same UMesh prefix walker the hash fields use (no second copy of the layout).
        public readonly struct UEMeshAnimSeq
        {
            public readonly string Name, Group;
            public readonly int StartFrame, NumFrames;
            public readonly float Rate;
            public UEMeshAnimSeq(string name, string group, int startFrame, int numFrames, float rate)
            { Name = name; Group = group; StartFrame = startFrame; NumFrames = numFrames; Rate = rate; }
        }

        public readonly struct UEMeshAnimInfo
        {
            public readonly UEMeshAnimSeq[] Sequences;
            public readonly int FrameVerts, AnimFrames;
            public UEMeshAnimInfo(UEMeshAnimSeq[] sequences, int frameVerts, int animFrames)
            { Sequences = sequences; FrameVerts = frameVerts; AnimFrames = animFrames; }
        }

        public UEMeshAnimInfo ReadMeshAnimInfo(PackageExport export)
        {
            var r = new RawReader(_data) { Position = export.Offset };
            SkipTaggedProperties(r);
            if (_version <= 61) throw new NotSupportedException("mesh anim info: pre-62 package");
            int headerAt = r.Position;
            r.Skip(12 + 12 + 1 + 16);
            int headerEnd = r.Position;
            r.Position = r.Int32();   // verts
            r.Position = r.Int32();   // tris
            int animSeqs = CheckedCount(r.CompactIndex(), "anim sequences");
            var seqs = new UEMeshAnimSeq[animSeqs];
            for (int i = 0; i < animSeqs; i++)
            {
                string name = Name(r.CompactIndex());
                string group = Name(r.CompactIndex());
                int startFrame = r.Int32();
                int numFrames = r.Int32();
                int notifies = CheckedCount(r.CompactIndex(), "anim notifies");
                for (int j = 0; j < notifies; j++) { r.Skip(4); r.CompactIndex(); }
                float rate = r.Single();
                seqs[i] = new UEMeshAnimSeq(name, group, startFrame, numFrames, rate);
            }
            r.Position = r.Int32();   // connects
            r.Skip(headerEnd - headerAt);
            r.Position = r.Int32();   // vertex links
            int textures = CheckedCount(r.CompactIndex(), "textures");
            for (int i = 0; i < textures; i++) r.CompactIndex();
            r.Skip(CheckedCount(r.CompactIndex(), "bounding boxes") * 25);
            r.Skip(CheckedCount(r.CompactIndex(), "bounding spheres") * 16);
            // The UMesh scalar tail starts with FrameVerts and AnimFrames — exactly the two
            // numbers DrawLodMesh addresses a frame by.
            int frameVerts = r.Int32();
            int animFrames = r.Int32();
            return new UEMeshAnimInfo(seqs, frameVerts, animFrames);
        }

        Hp1MeshHashFields ReadMeshPrefix(RawReader r, PackageExport export)
        {
            SkipTaggedProperties(r);
            if (_version <= 61)
                throw new NotSupportedException("mesh hash fields: pre-62 package");

            int headerAt = r.Position;
            var boundMin = new Vector3(r.Single(), r.Single(), r.Single());
            var boundMax = new Vector3(r.Single(), r.Single(), r.Single());
            r.Skip(1 + 16); // box valid byte + sphere
            int headerEnd = r.Position;

            r.Position = r.Int32();  // verts (lazy, absolute skip offset)
            r.Position = r.Int32();  // tris
            int animSeqs = CheckedCount(r.CompactIndex(), "anim sequences");
            for (int i = 0; i < animSeqs; i++)
            {
                r.CompactIndex(); r.CompactIndex(); r.Skip(8);
                int notifies = CheckedCount(r.CompactIndex(), "anim notifies");
                for (int j = 0; j < notifies; j++) { r.Skip(4); r.CompactIndex(); }
                r.Skip(4);
            }
            r.Position = r.Int32();  // connects

            int dupAt = r.Position;
            for (int i = 0; i < headerEnd - headerAt; i++)
                if (_data[headerAt + i] != _data[dupAt + i])
                    throw new InvalidDataException(
                        $"mesh hash fields: duplicate bound mismatch at +{i} ({export.Name})");
            r.Skip(headerEnd - headerAt);

            r.Position = r.Int32();  // vertex links (lazy)
            int textures = CheckedCount(r.CompactIndex(), "textures");
            for (int i = 0; i < textures; i++) r.CompactIndex();
            r.Skip(CheckedCount(r.CompactIndex(), "bounding boxes") * 25);
            r.Skip(CheckedCount(r.CompactIndex(), "bounding spheres") * 16);
            // Scalar tail (UMesh::Serialize, this+0xf0..+0x128): dwords 4-6 =
            // Scale, 7-9 = Origin, 10-12 = RotOrigin.
            r.Skip(16);
            var scale = new Vector3(r.Single(), r.Single(), r.Single());
            var origin = new Vector3(r.Single(), r.Single(), r.Single());
            int rotPitch = r.Int32(), rotYaw = r.Int32(), rotRoll = r.Int32();
            return new Hp1MeshHashFields(boundMin, boundMax, scale, origin, rotPitch, rotYaw, rotRoll);
        }

        // Mesh geometry array counts.
        //
        // WHY. The frontend mesh store is named by name LEAF, and 11 leaves belong to DIFFERENT
        // meshes with different bytes. Proving WHICH package a file on disk came from needs
        // values that differ between candidates — point, wedge, face and bone counts. They are
        // read by THE SAME pass as RefSkeleton: there must be no second copy of the layout.
        public readonly struct UEMeshGeoCounts
        {
            public readonly int Faces, Wedges, Materials, ExtWedges, Points, Bones;
            // First and last point of the skeletal array. Counts are not enough: measured,
            // 10 colliding leaves have IDENTICAL topology but different bytes — the coordinates
            // themselves differ.
            public readonly Vector3 FirstPoint, LastPoint;
            // Checksum of ALL point coordinates. Two points are too few: they also match for
            // colliding leaves, so "the geometry is identical" would rest on two numbers.
            public readonly uint PointsHash;
            public UEMeshGeoCounts(int faces, int wedges, int materials,
                int extWedges, int points, int bones, Vector3 firstPoint, Vector3 lastPoint,
                uint pointsHash)
            {
                Faces = faces; Wedges = wedges; Materials = materials;
                ExtWedges = extWedges; Points = points; Bones = bones;
                FirstPoint = firstPoint; LastPoint = lastPoint; PointsHash = pointsHash;
            }
        }

        public UEMeshGeoCounts ReadMeshGeoCounts(PackageExport export)
        {
            var r = new RawReader(_data) { Position = export.Offset };
            var c = WalkToRefSkeleton(r, export);
            int bones = CheckedCount(r.CompactIndex(), "reference skeleton");
            return new UEMeshGeoCounts(c.Faces, c.Wedges, c.Materials,
                c.ExtWedges, c.Points, bones, c.FirstPoint, c.LastPoint, c.PointsHash);
        }

        // The ONLY pass over the ULodMesh/USkeletalMesh layout up to RefSkeleton. Both
        // ReadRefSkeleton and ReadMeshGeoCounts use it, so the array sequence has one author.
        UEMeshGeoCounts WalkToRefSkeleton(RawReader r, PackageExport export)
        {
            ReadMeshPrefix(r, export);
            r.Skip(8);
            if (_version == 65) r.Skip(4);
            else if (_version >= 66) r.Skip(CheckedCount(r.CompactIndex(), "texture LOD") * 4);

            // ULodMesh arrays and scalar tail.
            SkipArray(r, 2, "collapse points");
            SkipArray(r, 2, "face levels");
            int faces = CountedSkip(r, 8, "faces");
            SkipArray(r, 2, "collapse wedges");
            int wedges = CountedSkip(r, 4, "wedges");
            int materials = CountedSkip(r, 8, "materials");
            SkipArray(r, 8, "special faces");
            r.Skip(32);
            SkipArray(r, 2, "remap anim verts");
            r.Skip(4);

            // USkeletalMesh begins here: ExtWedges, Points, RefSkeleton.
            int extWedges = CountedSkip(r, 12, "extended wedges");
            // Points are READ, not skipped: first and last are needed to tell apart two meshes
            // with identical topology. The position after the array is the same a skip would give.
            int points = CheckedCount(r.CompactIndex(), "skeletal points");
            Vector3 first = Vector3.zero, last = Vector3.zero;
            uint hash = 2166136261u;   // FNV-1a
            for (int i = 0; i < points; i++)
            {
                var p = new Vector3(r.Single(), r.Single(), r.Single());
                if (i == 0) first = p;
                if (i == points - 1) last = p;
                foreach (float v in new[] { p.x, p.y, p.z })
                    foreach (byte b in BitConverter.GetBytes(v))
                    { hash ^= b; hash *= 16777619u; }
            }
            return new UEMeshGeoCounts(faces, wedges, materials, extWedges, points, 0,
                first, last, hash);
        }

        static int CountedSkip(RawReader r, int elementSize, string label)
        {
            int n = CheckedCount(r.CompactIndex(), label);
            r.Skip(checked(n * elementSize));
            return n;
        }

        // USkeletalMesh RefSkeleton. NumChildren is the serialized subtree span
        // AActor::CreateAnimChannel range-tests against (Engine.dll.c:27809-
        // 27874), so it is read from the shipped package, never reconstructed
        // from a frontend's imported bone list.
        public UERefBone[] ReadRefSkeleton(PackageExport export)
        {
            var r = new RawReader(_data) { Position = export.Offset };
            WalkToRefSkeleton(r, export);
            int boneCount = CheckedCount(r.CompactIndex(), "reference skeleton");
            var bones = new UERefBone[boneCount];
            for (int i = 0; i < boneCount; i++)
            {
                string name = Name(r.CompactIndex());
                r.UInt32(); // flags
                r.Skip(16 + 12 + 4 + 12);
                int numChildren = checked((int)r.UInt32());
                int parent = checked((int)r.UInt32());
                bones[i] = new UERefBone { Name = name, ParentIndex = parent, NumChildren = numChildren };
            }
            return bones;
        }

        // ULevel::Actors, the array the shipped engine actually walks.
        //
        // Every BeginPlay-phase sweep in UGameEngine::LoadMap (247428..247464)
        // and every `Actors(i)` assert in the decompile (`Actors(0)->IsA(
        // ALevelInfo::StaticClass())`, UnLevel.h:0xf7-0xf8) indexes THIS array,
        // not the package's export table. The two are different orders, and the
        // difference is player-visible: PawnList is built by AddPawn during
        // PreBeginPlay (execAddPawn, Engine.dll.c:46972..47005) inserting at the
        // HEAD, so the sweep order decides which pawn GameInfo.Login reuses.
        //
        // Layout is ULevelBase::Load — tagged properties, then ArrayNum and
        // ArrayMax as two int32 (TTransArray, not a compact-index TArray), then
        // ArrayNum object references. Returns 1-based export indices to match
        // ActorEntry.ExportIndex; 0 is a null slot (UE1 keeps holes in Actors).
        public int[] ReadLevelActorOrder(PackageExport levelExport)
        {
            var r = new RawReader(_data) { Position = levelExport.Offset };
            SkipTaggedProperties(r);
            int count = CheckedCount(r.Int32(), "level actors");
            int max = r.Int32();
            if (max < count)
                throw new InvalidDataException($"ULevel::Actors: ArrayMax {max} < ArrayNum {count}");
            var order = new int[count];
            for (int i = 0; i < count; i++)
            {
                int objIndex = r.CompactIndex();
                if (objIndex < 0)
                    throw new InvalidDataException(
                        $"ULevel::Actors[{i}] references an IMPORT ({objIndex}) — actors are level-local");
                if (objIndex > Exports.Length)
                    throw new InvalidDataException(
                        $"ULevel::Actors[{i}] export index {objIndex} out of range ({Exports.Length})");
                order[i] = objIndex;
            }
            return order;
        }

        // ── READING walker over instance tags ────────────────────────────────
        //
        // Exists because the patched UELib tag parser SILENTLY LOSES some entries
        // ("PropertyTag value size error: Expected N, Actual 0" family; e.g. KeyPos(1..) of
        // Lev_Tut1b movers: KeyRot(1) is listed, KeyPos is missing entirely, so doors would
        // interpolate 0→0 and not move). The byte layout is THE SAME as SkipTaggedProperties
        // below (one format, one authority); the only difference is that values are returned
        // to the caller instead of skipped.
        public readonly struct RawPropertyTag
        {
            public readonly string Name; public readonly int Type;
            public readonly string StructName; public readonly int ArrayIndex;
            public readonly byte[] Value;        // bool: empty, the value is in BoolValue
            public readonly bool BoolValue;
            public RawPropertyTag(string name, int type, string structName,
                int arrayIndex, byte[] value, bool boolValue)
            { Name = name; Type = type; StructName = structName;
              ArrayIndex = arrayIndex; Value = value; BoolValue = boolValue; }
        }

        // Actor header before properties: an object with RF_HasStack (0x02000000) serializes a
        // stack frame (UObject::Serialize, Core.dll.c:14393, export 0x1811; flag — ObjectFlags
        // +0x1c & 0x2000000): Node and StateNode — object references (compact index), ProbeMask —
        // 8 bytes, LatentAction — 4 bytes, and ONLY when Node != 0 — Offset as a compact index
        // (-1 = no code). Tags follow only after it (D-14 WORLD).
        const uint RF_HasStack = 0x02000000;

        public System.Collections.Generic.List<RawPropertyTag> ReadInstanceProperties(PackageExport export)
        {
            var r = new RawReader(_data) { Position = export.Offset };
            if ((export.Flags & RF_HasStack) != 0)
            {
                int node = r.CompactIndex();
                r.CompactIndex();          // StateNode
                r.Skip(8);                 // ProbeMask
                r.Skip(4);                 // LatentAction
                if (node != 0) r.CompactIndex();   // Offset
            }
            var result = new System.Collections.Generic.List<RawPropertyTag>();
            for (int guard = 0; guard < 10000; guard++)
            {
                string name = Name(r.CompactIndex());
                if (name.Equals("None", StringComparison.OrdinalIgnoreCase)) return result;
                byte info = r.Byte();
                int type = info & 0x0f;
                int sizeCode = (info >> 4) & 7;
                string structName = null;
                if (type == 10) structName = Name(r.CompactIndex());
                int size = sizeCode switch
                {
                    0 => 1, 1 => 2, 2 => 4, 3 => 12, 4 => 16,
                    5 => r.Byte(), 6 => r.UInt16(), 7 => checked((int)r.UInt32()),
                    _ => throw new InvalidDataException("invalid property size code")
                };
                if (type == 3)   // bool: the value is bit 0x80 of the info byte
                {
                    result.Add(new RawPropertyTag(name, type, null, 0,
                        Array.Empty<byte>(), (info & 0x80) != 0));
                    continue;
                }
                int arrayIndex = 0;
                if ((info & 0x80) != 0) arrayIndex = ReadArrayIndex(r);
                var value = new byte[size];
                for (int i = 0; i < size; i++) value[i] = r.Byte();
                result.Add(new RawPropertyTag(name, type, structName, arrayIndex, value, false));
            }
            throw new InvalidDataException("property list runaway");
        }

        // Reading counterpart of SkipArrayIndex below — same widths (FPropertyTag operator<<,
        // Core 0x1012de40): 0x00..0x7f = 1 byte; 0x80..0xbf — two,
        // high bits in the first; 0xc0..0xff — four.
        protected static int ReadArrayIndex(RawReader r)
        {
            byte b = r.Byte();
            if ((b & 0x80) == 0) return b;
            if ((b & 0xc0) == 0xc0)
            {
                int v = b & 0x3f;
                v = (v << 8) | r.Byte();
                v = (v << 8) | r.Byte();
                v = (v << 8) | r.Byte();
                return v;
            }
            return ((b & 0x3f) << 8) | r.Byte();
        }

        protected void SkipTaggedProperties(RawReader r)
        {
            for (int guard = 0; guard < 10000; guard++)
            {
                string name = Name(r.CompactIndex());
                if (name.Equals("None", StringComparison.OrdinalIgnoreCase)) return;
                byte info = r.Byte();
                int type = info & 0x0f;
                int sizeCode = (info >> 4) & 7;
                if (type == 10) r.CompactIndex(); // struct type name
                int size = sizeCode switch
                {
                    0 => 1, 1 => 2, 2 => 4, 3 => 12, 4 => 16,
                    5 => r.Byte(), 6 => r.UInt16(), 7 => checked((int)r.UInt32()),
                    _ => throw new InvalidDataException("invalid property size code")
                };
                if (type == 3) continue; // bool value is in the tag
                if ((info & 0x80) != 0) SkipArrayIndex(r);
                r.Skip(size);
            }
            throw new InvalidDataException("property list runaway");
        }

        // UE1's array-index width is chosen by the TOP BITS of the first byte:
        // 0x00..0x7f = 1 byte, 0x80..0xbf = 2 bytes, 0xc0..0xff = 4 bytes. The
        // two-byte form is 0x80 | (index >> 8), the four-byte form
        // 0xc0 | (index >> 24).
        //
        // This had the two widths REVERSED — it consumed 4 bytes for the 0x80
        // form and 2 for the 0xc0 form, i.e. it desynchronised the property
        // stream for any static array index >= 128. Carried in verbatim from the
        // frontend when the reader moved, so it predates the move; no shipped
        // asset in Tut1 reaches an index that large, which is why 14545 pinned
        // instance properties never caught it.
        protected static void SkipArrayIndex(RawReader r)
        {
            byte b = r.Byte();
            if ((b & 0x80) == 0) return;
            r.Byte();
            if ((b & 0xc0) == 0xc0) r.Skip(2);
        }

        protected static void SkipArray(RawReader r, int elementSize, string label)
            => r.Skip(checked(CheckedCount(r.CompactIndex(), label) * elementSize));

        protected string Name(int index)
            => index >= 0 && index < _names.Length ? _names[index]
                : throw new InvalidDataException("invalid name index: " + index);

        protected static int CheckedCount(int value, string label)
        {
            if (value < 0 || value > 10_000_000) throw new InvalidDataException($"invalid {label} count: {value}");
            return value;
        }
    }

    // Plain classes, not positional records.
    public sealed class PackageExport
    {
        public readonly int ClassIndex; public readonly string Name;
        public readonly int Size; public readonly int Offset;
        // UObject::ObjectFlags as serialized. The load-context bits decide
        // whether the running game creates this export at all.
        public readonly uint Flags;
        // UExport::PackageIndex (Outer) — the owner object: >0 export (index−1),
        // <0 import (−index−1), 0 — package root. Without it an object name is NOT its
        // identity: `station` occurs in different packages/groups with different bMasked.
        public readonly int OuterIndex;
        public PackageExport(int classIndex, string name, int size, int offset, uint flags = 0, int outerIndex = 0)
        { ClassIndex = classIndex; Name = name; Size = size; Offset = offset; Flags = flags; OuterIndex = outerIndex; }
    }

    public sealed class PackageImport
    {
        public readonly string Name;
        public readonly int OuterIndex;
        public readonly string ClassName;
        public readonly string ClassPackage;
        public PackageImport(string name, int outerIndex = 0, string className = null, string classPackage = null)
        { Name = name; OuterIndex = outerIndex; ClassName = className; ClassPackage = classPackage; }
    }

    public sealed class RawReader
    {
        static readonly System.Text.Encoding Latin1 = System.Text.Encoding.GetEncoding(28591);

        private readonly byte[] _data;
        private int _position;
        public RawReader(byte[] data) => _data = data;
        public int Position
        {
            get => _position;
            set { if (value < 0 || value > _data.Length) throw new EndOfStreamException(); _position = value; }
        }
        public void Skip(int count) => Position = checked(Position + count);
        public byte Byte() { Require(1); return _data[_position++]; }
        public ushort UInt16() { Require(2); ushort v = BitConverter.ToUInt16(_data, _position); _position += 2; return v; }
        public uint UInt32() { Require(4); uint v = BitConverter.ToUInt32(_data, _position); _position += 4; return v; }
        public short Int16() { Require(2); short v = BitConverter.ToInt16(_data, _position); _position += 2; return v; }
        public int Int32() { Require(4); int v = BitConverter.ToInt32(_data, _position); _position += 4; return v; }
        public float Single() { Require(4); float v = BitConverter.ToSingle(_data, _position); _position += 4; return v; }
        public int CompactIndex()
        {
            int first = Byte();
            bool negative = (first & 0x80) != 0;
            int value = first & 0x3f;
            if ((first & 0x40) != 0)
            {
                int shift = 6;
                for (int i = 0; i < 4; i++)
                {
                    int next = Byte();
                    value |= (next & 0x7f) << shift;
                    if ((next & 0x80) == 0) break;
                    shift += 7;
                }
            }
            return negative ? -value : value;
        }
        // Package name of version < 64: ANSI bytes up to NUL. A separate method, not a branch
        // inside FString: FString reads ITS OWN format and is used in property parsing, where
        // the version is irrelevant.
        public string CString()
        {
            int start = _position;
            while (_position < _data.Length && _data[_position] != 0) _position++;
            if (_position >= _data.Length) throw new EndOfStreamException();
            string value = System.Text.Encoding.GetEncoding(28591)
                .GetString(_data, start, _position - start);
            _position++;                                   // NUL byte
            return value;
        }

        public string FString()
        {
            // operator<<(FArchive&, FString&) (Core 0x10150830): |SaveNum| elements
            // (bytes or UTF-16 words), the position advances over all of them; the string is a
            // TCHAR array, i.e. up to the FIRST NUL (the name-table FNameEntry 0x10157cd0 is
            // copied with appStrcpy — also up to NUL).
            int length = CompactIndex();
            if (length == 0) return "";
            string value;
            if (length < 0)
            {
                int byteLength = checked(-length * 2);
                Require(byteLength);
                value = System.Text.Encoding.Unicode.GetString(_data, _position, byteLength);
                _position += byteLength;
            }
            else
            {
                Require(length);
                // Encoding.Latin1 is .NET 5+; the frontend profile is netstandard2.1.
                // Codepage 28591 IS ISO-8859-1, i.e. the same encoding by its number,
                // so the decoded bytes are identical — not a substitution.
                value = Latin1.GetString(_data, _position, length);
                _position += length;
            }
            int nul = value.IndexOf('\0');
            return nul < 0 ? value : value.Substring(0, nul);
        }
        private void Require(int count)
        {
            if (count < 0 || _position > _data.Length - count) throw new EndOfStreamException();
        }
    }
}
