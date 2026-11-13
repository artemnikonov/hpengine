using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HP1.UEScript
{
    // The collision-hash extent fields for a mesh actor, resolved from the
    // shipped .u packages.
    //
    // Core-owned for the same reason the actor graph is: HashBoxOf dispatches on
    // these values for every CollideType=3 actor, so a frontend that cannot
    // supply them gets an InitCollisionHash that throws; Core owns the reader so
    // no frontend needs its own reader and hook.
    public sealed class UEMeshBounds
    {
        readonly Dictionary<string, List<(UEPackageReader Package, PackageExport Export)>>
            _byName = new Dictionary<string, List<(UEPackageReader, PackageExport)>>(
                StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Hp1MeshHashFields> _cache =
            new Dictionary<string, Hp1MeshHashFields>(UEPropNameComparer.Instance);

        // Deterministic package order: the shipped resources are spread over
        // several .u files and an ambiguous leaf name is resolved by path, so the
        // scan order must not depend on the filesystem's enumeration order.
        public UEMeshBounds(string systemDir)
        {
            if (string.IsNullOrEmpty(systemDir) || !Directory.Exists(systemDir))
                throw new DirectoryNotFoundException("mesh bounds: no System dir " + systemDir);
            foreach (string path in Directory.EnumerateFiles(systemDir, "*.u")
                         .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var package = new UEPackageReader(path);
                foreach (PackageExport export in package.Exports)
                {
                    string cls = package.ClassName(export);
                    if (!cls.Equals("Mesh", StringComparison.OrdinalIgnoreCase)
                        && !cls.Equals("LodMesh", StringComparison.OrdinalIgnoreCase)
                        && !cls.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!_byName.TryGetValue(export.Name, out var matches))
                        _byName[export.Name] = matches =
                            new List<(UEPackageReader, PackageExport)>();
                    matches.Add((package, export));
                }
            }
        }

        // Ambiguous leaf names (the same mesh name ships from two packages)
        // resolve by the reference's FULL ObjectPath — its package segment picks
        // the export. A pathless ambiguous query stays loud rather than guessing
        // which package was meant.
        public Hp1MeshHashFields GetHashFields(string name, string objectPath = null)
        {
            string key = objectPath ?? name;
            if (_cache.TryGetValue(key, out Hp1MeshHashFields cached)) return cached;
            (UEPackageReader Package, PackageExport Export) hit = Resolve(name, objectPath);
            Hp1MeshHashFields fields = hit.Package.ReadMeshHashFields(hit.Export);
            _cache[key] = fields;
            return fields;
        }

        readonly Dictionary<string, bool> _skeletalKind =
            new Dictionary<string, bool>(UEPropNameComparer.Instance);

        // Export class: whether the mesh is skeletal. Needed by the bAlignBottom vertical —
        // the rule lives in the USkeletalMesh body and does not apply to Mesh/LodMesh.
        public bool IsSkeletal(string name, string objectPath = null)
        {
            string key = objectPath ?? name;
            if (_skeletalKind.TryGetValue(key, out bool cached)) return cached;
            (UEPackageReader Package, PackageExport Export) hit = Resolve(name, objectPath);
            bool skel = hit.Package.ClassName(hit.Export)
                .Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase);
            _skeletalKind[key] = skel;
            return skel;
        }

        // Name -> export, by package when the leaf name is ambiguous.
        (UEPackageReader Package, PackageExport Export) Resolve(string name, string objectPath)
        {
            if (!_byName.TryGetValue(name, out var matches) || matches.Count == 0)
                throw new KeyNotFoundException("shipped mesh not found: " + name);
            if (matches.Count == 1) return matches[0];
            string pkg = objectPath != null && objectPath.IndexOf('.') > 0
                ? objectPath.Substring(0, objectPath.IndexOf('.')) : null;
            var byPkg = pkg == null ? null : matches.Where(m =>
                string.Equals(m.Package.PackageName, pkg, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byPkg != null && byPkg.Count == 1) return byPkg[0];
            throw new InvalidDataException(
                $"ambiguous shipped mesh {name}: {matches.Count} exports (path='{objectPath}')");
        }

        readonly Dictionary<string, UERefBone[]> _skeletons =
            new Dictionary<string, UERefBone[]>(UEPropNameComparer.Instance);

        // The shipped RefSkeleton, same resolution rules as the extent fields.
        public UERefBone[] GetRefSkeleton(string name, string objectPath = null)
        {
            string key = objectPath ?? name;
            if (_skeletons.TryGetValue(key, out UERefBone[] cached)) return cached;
            (UEPackageReader Package, PackageExport Export) hit = Resolve(name, objectPath);
            UERefBone[] bones = hit.Package.ReadRefSkeleton(hit.Export);
            _skeletons[key] = bones;
            return bones;
        }
    }

    // One RefSkeleton entry. NumChildren is the SERIALIZED subtree span, which
    // is what the shipped anim-channel range test uses — it is not derivable
    // from a bone-name list, so no frontend may substitute its own.
    public sealed class UERefBone
    {
        public string Name;
        public int ParentIndex;
        public int NumChildren;
    }
}
