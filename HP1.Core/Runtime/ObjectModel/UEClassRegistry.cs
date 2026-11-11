using System;
using HP1.UEScript;
using System.Collections.Generic;
using UELib;
using UELib.Core;

namespace HP1.Core
{
    // Pure package/object-model slice shared by every frontend. It intentionally
    // has no UnityEngine dependency.
    public sealed class UEClassRegistry
    {
        // The process-wide registry the Core object model resolves classes
        // through (UEObject.LoadDefaults and its decoders). Set once at
        // frontend bootstrap (UEScriptRuntime / the headless smoke) before
        // any defaults load.
        public static UEClassRegistry Shared;

        readonly Dictionary<string, UClass> _classByName =
            new Dictionary<string, UClass>(UEPropNameComparer.Instance);
        readonly Dictionary<int, UFunction> _functionByNativeIndex =
            new Dictionary<int, UFunction>();
        readonly List<UnrealPackage> _packages = new List<UnrealPackage>();
        // Package CATALOGUE by canonical name. It is the single owner of the "package loaded"
        // state; no loader may have its own answer to that question.
        readonly Dictionary<string, UnrealPackage> _packageByName =
            new Dictionary<string, UnrealPackage>(UEPropNameComparer.Instance);

        public IReadOnlyList<UnrealPackage> Packages => _packages;

        // Whether the registry knows a package with this name. Needed for an honest
        // DynamicLoadObject answer: "no object" and "package not loaded" are different facts,
        // and the second must not pose as the first (the script registry does not load
        // resource .utx/.uax, while the original reads them).
        public bool KnowsPackage(string packageName)
        {
            if (string.IsNullOrEmpty(packageName)) return false;
            foreach (var p in _packages)
                if (string.Equals(p.PackageName, packageName, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        // Canonical package name = file name without extension, as UELib computes it
        // (UnrealPackage.PackageName). A separate method because the catalogue must answer
        // both for a PATH (before the file is read) and for a parsed package, with the same key.
        public static string CanonicalName(string pathOrName)
            => string.IsNullOrEmpty(pathOrName) ? null
             : System.IO.Path.GetFileNameWithoutExtension(pathOrName);

        public bool HasPackage(string name)
            => !string.IsNullOrEmpty(name)
            && _packageByName.ContainsKey(CanonicalName(name));

        public UnrealPackage FindPackage(string name)
            => string.IsNullOrEmpty(name) ? null
             : (_packageByName.TryGetValue(CanonicalName(name), out var p) ? p : null);

        // Intrinsic engine classes (UViewport, ...) are registered by C++ in
        // shipped (IMPLEMENT_CLASS) and never serialized as UClass exports —
        // their on-disk witness is the IMPORT tables of the script packages
        // that reference them (e.g. HPMenu.u imports Engine.Viewport for
        // Viewport.WindowsMouseX and PlayerPawn.PreRender's Viewport(...)
        // cast). UELib materialises an imported class lazily; expose it so
        // an intrinsic-classed runtime object (the headless viewport) can
        // carry the class the shipped dynamic-cast bytecode expects.
        public UClass FindImportedClass(string name)
        {
            foreach (var package in _packages)
            {
                if (package?.Imports == null) continue;
                foreach (var import in package.Imports)
                {
                    if (!string.Equals(import.ObjectName.ToString(), name,
                            StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.Equals(import.ClassName.ToString(), "Class",
                            StringComparison.OrdinalIgnoreCase)) continue;
                    if (import.Object is UClass cls) return cls;
                }
            }
            return null;
        }

        // ── THE ONLY PACKAGE LOAD ENTRY ─────────────────────────────────────
        //
        // The catalogue is consulted BEFORE the file is read: a repeated `LoadPackage` +
        // `InitializePackage` builds a SECOND full object graph of the same package (measured:
        // 12 -> 24 -> 36 packages over three world loads; the registry never shrinks). Besides
        // memory, that is a silent identity split: `Find` returned the class from the LAST
        // instance while already built UEObjects held the class from the FIRST.
        //
        // A name match alone is NOT sufficient: the name is only the catalogue key. A different
        // PATH under the same name means different files, and silently picking one is
        // forbidden: loud STOP. The GUID is checked where the package is already parsed
        // (RegisterPackage): before reading the file there is no GUID, and reading it to check
        // would bring back exactly the work this method removes.
        // The same file reached through a symlinked directory (an install behind a link) is the same file: compare the
        // physical path, every link component resolved, not the spelling.
        // Memo by spelling: a link is not re-pointed while the process runs.
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _physical =
            new(StringComparer.Ordinal);

        public static string PhysicalPath(string path)
            => _physical.GetOrAdd(System.IO.Path.GetFullPath(path), p => ResolvePhysical(p, 0));

        static string ResolvePhysical(string path, int depth)
        {
            string full = System.IO.Path.GetFullPath(path);
            string root = System.IO.Path.GetPathRoot(full) ?? "";
            string cur = root;
            foreach (string part in full.Substring(root.Length).Split(System.IO.Path.DirectorySeparatorChar,
                         StringSplitOptions.RemoveEmptyEntries))
            {
                cur = System.IO.Path.Combine(cur, part);
                System.IO.FileSystemInfo info = System.IO.Directory.Exists(cur) ? new System.IO.DirectoryInfo(cur)
                    : new System.IO.FileInfo(cur);
                if (info.Exists && info.LinkTarget is not null && depth < 32
                    && info.ResolveLinkTarget(true) is System.IO.FileSystemInfo target)
                    cur = ResolvePhysical(target.FullName, depth + 1);
            }
            return cur;
        }

        public UnrealPackage GetOrLoadPackage(string path,
            UnrealPackage.InitFlags flags = UnrealPackage.InitFlags.All)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));
            string name = CanonicalName(path);
            if (_packageByName.TryGetValue(name, out UnrealPackage existing))
            {
                string had = existing.FullPackageName, want = System.IO.Path.GetFullPath(path);
                if (!string.Equals(PhysicalPath(had), PhysicalPath(want),
                        StringComparison.OrdinalIgnoreCase))
                    throw new System.IO.InvalidDataException(
                        $"package name collision: '{name}' is already loaded from '{had}', "
                        + $"refusing to also load '{want}' — two different files cannot share "
                        + "one linker name");
                return existing;                       // the file is NOT reopened
            }
            var package = UnrealLoader.LoadPackage(path, System.IO.FileAccess.Read);
            package.InitializePackage(flags);
            RegisterPackage(package);
            return package;
        }

        // Idempotent by CATALOGUE, not by instance identity (a reference comparison let a
        // second parse of the same file through).
        //
        // The FIRST registered wins: its classes are already handed out to live objects. A
        // second instance with the same name is accepted as a duplicate ONLY if GUID and path
        // match; a mismatch in either means two different packages under one linker name,
        // which is a STOP, not a silent choice.
        //
        // Returns true if the package was actually accepted.
        public bool RegisterPackage(UnrealPackage package)
        {
            if (package == null || _packages.Contains(package)) return false;
            string name = CanonicalName(package.PackageName);
            if (_packageByName.TryGetValue(name, out UnrealPackage had))
            {
                if (!string.Equals(had.GUID, package.GUID, StringComparison.OrdinalIgnoreCase))
                    throw new System.IO.InvalidDataException(
                        $"package identity conflict: '{name}' already registered with GUID "
                        + $"{had.GUID} from '{had.FullPackageName}', now offered GUID "
                        + $"{package.GUID} from '{package.FullPackageName}'");
                if (!string.Equals(had.FullPackageName, package.FullPackageName,
                        StringComparison.OrdinalIgnoreCase))
                    throw new System.IO.InvalidDataException(
                        $"package path conflict: '{name}' (GUID {had.GUID}) already registered "
                        + $"from '{had.FullPackageName}', now offered from "
                        + $"'{package.FullPackageName}'");
                return false;                          // same package, already catalogued
            }
            _packageByName[name] = package;
            _packages.Add(package);
            // A new package may resolve an import that did not resolve before.
            _childCache.Clear();
            foreach (var export in package.Exports)
            {
                if (!(export.Object is UClass cls)) continue;

                // A cross-package `class X;` declaration is an import stub:
                // unlike a real exported subclass it has no Super. Data-only
                // real classes may legitimately declare zero functions.
                if (_classByName.TryGetValue(cls.Name, out UClass previous)
                    && !IsImportStub(previous) && IsImportStub(cls))
                {
                    // Keep the already registered real export.
                }
                else
                {
                    _classByName[cls.Name] = cls;
                }

                foreach (UFunction function in cls.EnumerateFields<UFunction>())
                {
                    int index = function.NativeToken;
                    if (index > 0 && !_functionByNativeIndex.ContainsKey(index))
                        _functionByNativeIndex[index] = function;
                }
            }
            return true;
        }

        public UClass Find(string name)
            => string.IsNullOrEmpty(name) ? null
             : (_classByName.TryGetValue(name, out UClass cls) ? cls : null);

        // Class by PACKAGE and name, as the linker resolves an import (ULinkerLoad by Outer
        // package and name). A short name is ambiguous: HagridHutRock, LevShadow,
        // SavePointFloatBook exist both in HPModels (Actor) and in HProps (HProps); Find(name)
        // returns the last registered. Package not catalogued or class not in it: short lookup.
        public UClass Find(string packageName, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (!string.IsNullOrEmpty(packageName)
                && _packageByName.TryGetValue(packageName, out UnrealPackage package))
                foreach (var export in package.Exports)
                    if (export.Object is UClass cls && !IsImportStub(cls)
                        && string.Equals(cls.Name, name, StringComparison.OrdinalIgnoreCase))
                        return cls;
            return Find(name);
        }

        // Import package = root of its Outer chain (ULinkerLoad::CreateImport).
        public static string ImportPackageName(UnrealPackage package, UELib.UImportTableItem import)
        {
            string pkg = null;
            int outer = (int)import.OuterIndex;
            while (outer < 0 && -outer - 1 < package.Imports.Count)
            {
                var o = package.Imports[-outer - 1];
                pkg = o.ObjectName.ToString();
                outer = (int)o.OuterIndex;
            }
            return pkg;
        }

        // Import stub -> real class of the SAME package (the stub's Outer).
        public UClass FindForStub(UClass stub)
            => stub is null ? null : Find(stub.Outer is UClass ? null : stub.Outer?.Name?.ToString(), stub.Name);

        // ── ENUM VALUE NAMES BY INDEX ───────────────────────────────────────
        //
        // Needed where shipped reads an engine table: `UInput::GetKeyName`
        // (Engine.dll.c:72765) takes a key name as `EInputKey->Names(key)`, and
        // `UInput::StaticConstructor` (:251065) builds Aliases and RawKeys the same way,
        // iterating the same enum 0..0xFE. So the key name table IS package data, not a list
        // one may make up.
        //
        // Returns null if no enum with that name is in the catalogue: the caller decides
        // what to do next, not this method.
        //
        // `outer` names the declaring class when the name is not unique: Engine.u declares EInputKey TWICE, in Actor
        // (240..243 = IK_JoyPovUp/Down/Left/Right) and in Console (240..243 = IK_UnknownF0..F3). UInput::UInput takes
        // Actor's: StaticFindObjectChecked(UEnum, AActor::StaticClass(), "EInputKey") (Engine.dll.c:84290-84292).
        readonly Dictionary<string, IReadOnlyList<string>> _enumNames =
            new Dictionary<string, IReadOnlyList<string>>(UEPropNameComparer.Instance);

        public IReadOnlyList<string> EnumNames(string name, string outer = null)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string cacheKey = outer is null ? name : outer + "." + name;
            if (_enumNames.TryGetValue(cacheKey, out IReadOnlyList<string> cached)) return cached;
            foreach (UnrealPackage package in _packages)
                foreach (var export in package.Exports)
                {
                    if (export.Object is not UEnum found) continue;
                    if (!string.Equals(found.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (outer is not null && !string.Equals(found.Outer?.Name, outer, StringComparison.OrdinalIgnoreCase)) continue;
                    var names = new List<string>();
                    foreach (var value in found.Names) names.Add(value.ToString());
                    _enumNames[cacheKey] = names;
                    return names;
                }
            return null;
        }

        public UFunction FindByNativeIndex(int index)
            => _functionByNativeIndex.TryGetValue(index, out UFunction function)
                ? function : null;

        // Stitch the class inheritance chain across HP1's package split: a
        // cross-package Super may resolve to an import STUB (no body). Replace
        // it with the real exported class of the same FName when one exists,
        // otherwise keep the direct Super. This is the single hop the VM's
        // parent-walks use (defaults loading, IsA, state/field lookup).
        public UClass CanonicalSuper(UClass cls)
        {
            if (cls?.Super is not UClass sup) return null;
            if (IsImportStub(sup))
            {
                UClass canon = FindForStub(sup);
                if (canon != null && !ReferenceEquals(canon, sup) && !IsImportStub(canon))
                    return canon;
            }
            return sup;
        }

        // UObject::IsA — true if cls or any canonical ancestor matches
        // targetName (case-insensitive). Name-based IsA is exactly UObject::execIsA
        // (native 303, Core.dll.c:44651): comparing the FName index along the SuperField chain
        // (FName, case-insensitive). DynamicCast/MetaCast go by class identity (IsChildOfClass).
        // The result is a function of (class, name) and does not change after packages load;
        // the tick asks it for every actor (ParticleFX, PlayerPawn, BaseCam...), so it is cached.
        readonly Dictionary<UClass, Dictionary<string, bool>> _isACache = new(HP1.UEScript.UERefComparer<UClass>.Instance);

        // UClass::IsChildOf by class IDENTITY (Core.dll execDynamicCast
        // 0x10134330..0x10134398: walk Class/SuperField, `cmp eax, esi` on the pointer at
        // 0x1013436c; execClassIsChildOf 0x10141b50 likewise).
        // Import stubs are canonicalised by the (package, name) pair: HPModels.LevShadow and
        // HProps.LevShadow are DIFFERENT classes; matching names mean nothing. Name-based
        // IsA(name) (ClassIsA below) is a separate semantics.
        // The answer for a class pair does not change while the catalogue is the same: the
        // chain walk with import canonicalisation allocated name strings at every step
        // (AllActors iterators call this per actor). Reset in RegisterPackage.
        readonly Dictionary<UClass, Dictionary<UClass, bool>> _childCache = new(HP1.UEScript.UERefComparer<UClass>.Instance);

        public bool IsChildOfClass(UClass test, UClass parent)
        {
            if (test is null || parent is null) return false;
            if (!_childCache.TryGetValue(test, out var byParent))
                _childCache[test] = byParent = new Dictionary<UClass, bool>(HP1.UEScript.UERefComparer<UClass>.Instance);
            if (byParent.TryGetValue(parent, out bool known)) return known;
            return byParent[parent] = IsChildOfClassUncached(test, parent);
        }

        bool IsChildOfClassUncached(UClass test, UClass parent)
        {
            UClass Canon(UClass c) => IsImportStub(c) ? (FindForStub(c) ?? c) : c;
            UClass target = Canon(parent);
            for (UClass c = Canon(test); c != null; c = CanonicalSuper(c))
                if (ReferenceEquals(Canon(c), target)) return true;
            return false;
        }

        public bool ClassIsA(UClass cls, string targetName)
        {
            if (string.IsNullOrEmpty(targetName) || cls is null) return false;
            int h = (System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(cls) * 31
                     + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(targetName)) & (IsAMemoSize - 1);
            ref IsAHit m = ref _isAMemo[h];
            if (ReferenceEquals(m.C, cls) && ReferenceEquals(m.T, targetName)) return m.R;
            bool r = ClassIsASlow(cls, targetName);
            m = new IsAHit { C = cls, T = targetName, R = r };
            return r;
        }

        // Port (CPU only): direct-mapped memo in front of _isACache by reference identity of the class and the target string
        // (call sites pass literals). Both caches are never invalidated (a class's ancestry does not change); single-threaded
        // like _isACache.
        struct IsAHit { public UClass C; public string T; public bool R; }
        const int IsAMemoSize = 1024;
        readonly IsAHit[] _isAMemo = new IsAHit[IsAMemoSize];

        bool ClassIsASlow(UClass cls, string targetName)
        {
            if (!_isACache.TryGetValue(cls, out var byName))
                _isACache[cls] = byName = new Dictionary<string, bool>(UEPropNameComparer.Instance);
            if (byName.TryGetValue(targetName, out bool known)) return known;
            bool result = false;
            for (UClass c = cls; c != null; c = CanonicalSuper(c))
                if (string.Equals(c.Name, targetName, StringComparison.OrdinalIgnoreCase))
                { result = true; break; }
            byName[targetName] = result;
            return result;
        }

        // Find a NON-class export object by name across all registered packages
        // (RegisterPackage only caches UClass exports). Used to resolve data
        // objects like the native UGesture instances ('FlipPattern' etc.) whose
        // serialized Points feed the gesture-lesson scorer. Accessing e.Object
        // makes UELib deserialize the object (and its Properties).
        //
        // A query WITH DOTS is a FULL path ('HPParticle.hp_fx.General.les_spellbackgrnd'):
        // it is compared by canonical identity PackageName + '.' + GetPath() (UELib GetPath
        // builds only the Outer chain, without the package name). There is no fallback from a
        // full path to the leaf: leaves of the two catalogues have 110 collisions (see the
        // measurement in HP1TextureBank), so a leaf would pick a foreign object.
        // ULinkerLoad::FindExportIndex with OuterIndex = INDEX_NONE (-1), as StaticLoadObject
        // calls it via ULinkerLoad::Create (Core 0x1014a2f0 -> 0x1014a120; the outer compare is
        // skipped at -1, 0x1014a1cb..1d8): the export is looked up in the package by NAME and
        // EXACT class name, the path group does not participate. Returns all matches IN TABLE
        // ORDER; shipped returns the first in the hash chain = the last by index (see
        // VerifyImportPath).
        public List<UObject> FindExportsByNameAndClass(string packageName, string leaf, string className)
        {
            var hits = new List<UObject>();
            var pkg = FindPackage(packageName);
            if (pkg is null || string.IsNullOrEmpty(leaf)) return hits;
            foreach (var e in pkg.Exports)
            {
                if (!string.Equals(e.ObjectName.ToString(), leaf, StringComparison.OrdinalIgnoreCase)) continue;
                string cls = e.Class?.ObjectName.ToString() ?? "Class";
                if (className is not null && !string.Equals(cls, className, StringComparison.OrdinalIgnoreCase)) continue;
                var o = e.Object;
                if (o != null) hits.Add(o);
            }
            return hits;
        }

        // THE SAME RULE FOR A RESOURCE PACKAGE (.utx/.uax/.umx/...) that the script registry
        // does not load: the export table is read from disk once, a match is NAME + EXACT class,
        // the group does not participate (FindExportIndex with OuterIndex=-1). Install root =
        // parent of the directory of any loaded .u (System/). Returns full match paths
        // "Package.Group....Name", built along the outer chain of the export table.
        readonly Dictionary<string, UnrealPackage> _resourcePackages =
            new Dictionary<string, UnrealPackage>(StringComparer.OrdinalIgnoreCase);
        // Order is appFindPackageFile (Core 0x10153f40): GSys->Paths in the order of
        // System/0/Default.ini [Core.System] (System, Maps, Textures, Sounds, Music), like
        // UETextureFacts.PackagePaths. Distinguishable on "Editor": System/Editor.u and
        // Textures/Editor.utx; shipped takes Editor.u.
        static readonly string[] ResourcePackagePaths =
            { "System/{0}.u", "Maps/{0}.unr", "Textures/{0}.utx", "Sounds/{0}.uax", "Music/{0}.umx" };

        public List<string> FindResourceExportPaths(string packageName, string leaf, string className)
        {
            var hits = new List<string>();
            if (string.IsNullOrEmpty(packageName) || string.IsNullOrEmpty(leaf) || _packages.Count == 0) return hits;
            UnrealPackage pkg = ResourceExportTable(packageName);
            if (pkg is null) return hits;
            foreach (var e in pkg.Exports)
            {
                if (!string.Equals(e.ObjectName.ToString(), leaf, StringComparison.OrdinalIgnoreCase)) continue;
                string cls = e.Class?.ObjectName.ToString() ?? "Class";
                if (className is not null && !string.Equals(cls, className, StringComparison.OrdinalIgnoreCase)) continue;
                var parts = new List<string> { e.ObjectName.ToString() };
                for (UObjectTableItem o = e.Outer; o is not null; o = o.Outer) parts.Insert(0, o.ObjectName.ToString());
                hits.Add(packageName + "." + string.Join(".", parts));
            }
            return hits;
        }

        // EXACT resource resolution by full path "Package.Group....Name" and class: package,
        // the whole outer chain of the export table, export name and class must all match. No
        // "first by leaf" or other heuristics: no unique match means null. Returns the package
        // file path and the 1-based export index.
        public (string PackagePath, int ExportIndex)? ResolveResourceExport(string objectPath, string className)
        {
            if (string.IsNullOrEmpty(objectPath) || _packages.Count == 0) return null;
            string[] parts = objectPath.Split('.');
            if (parts.Length < 2) return null;
            UnrealPackage pkg = ResourceExportTable(parts[0]);
            if (pkg is null) return null;
            int found = 0, hits = 0;
            for (int i = 0; i < pkg.Exports.Count; i++)
            {
                var e = pkg.Exports[i];
                string cls = e.Class?.ObjectName.ToString() ?? "Class";
                if (className is not null && !string.Equals(cls, className, StringComparison.OrdinalIgnoreCase)) continue;
                var chain = new List<string> { e.ObjectName.ToString() };
                for (UObjectTableItem o = e.Outer; o is not null; o = o.Outer) chain.Insert(0, o.ObjectName.ToString());
                if (chain.Count != parts.Length - 1) continue;
                bool same = true;
                for (int k = 0; k < chain.Count && same; k++)
                    same = string.Equals(chain[k], parts[k + 1], StringComparison.OrdinalIgnoreCase);
                if (same) { found = i + 1; hits++; }
            }
            if (hits != 1) return null;
            return (ResourcePackageFile(parts[0]), found);
        }

        string ResourcePackageFile(string packageName)
        {
            string systemDir = System.IO.Path.GetDirectoryName(_packages[0].FullPackageName);
            string root = systemDir is null ? null : System.IO.Path.GetDirectoryName(systemDir);
            if (root is null) return null;
            foreach (string pattern in ResourcePackagePaths)
            {
                string candidate = HP1.UEScript.UEInstallPath.File(System.IO.Path.Combine(root, string.Format(pattern, packageName)));
                if (candidate is not null) return candidate;
            }
            return null;
        }

        // Export table of a resource package from disk (once per package).
        // Resource package found on disk via the search paths (its table is readable).
        public bool HasResourcePackage(string packageName)
            => !string.IsNullOrEmpty(packageName) && _packages.Count > 0 && ResourceExportTable(packageName) is not null;

        UnrealPackage ResourceExportTable(string packageName)
        {
            if (_resourcePackages.TryGetValue(packageName, out UnrealPackage pkg)) return pkg;
            string systemDir = System.IO.Path.GetDirectoryName(_packages[0].FullPackageName);
            string root = systemDir is null ? null : System.IO.Path.GetDirectoryName(systemDir);
            string path = null;
            if (root is not null)
                foreach (string pattern in ResourcePackagePaths)
                {
                    path = HP1.UEScript.UEInstallPath.File(System.IO.Path.Combine(root, string.Format(pattern, packageName)));
                    if (path is not null) break;
                }
            // Tables only (UELib reads them in LoadPackage): objects are not materialised;
            // the registry need not know the import classes of a resource package.
            if (path is not null) pkg = UnrealLoader.LoadPackage(path, System.IO.FileAccess.Read);
            _resourcePackages[packageName] = pkg;
            return pkg;
        }

        // ── ULinkerLoad::VerifyImport: WHICH EXPORT ANSWERS AN IMPORT ─────────
        //
        // ONE BODY for all import readers (map, binary defaults, text defaults, mesh textures).
        // An import path is not necessarily the path of an existing object: shipped
        // (Core.dll.c:111780-111880, FUN_10149590) searches the export table of the source
        // package along the hash chain (object name, class name, class package) and accepts an
        // export if
        //   * object name and class name match (class package if the caller knows it; the
        //     UnrealI->UnrealShare alias 0x4c/0x50 does not affect HP1);
        //   * the outer import is RESOLVED (SourceIndex != -1):
        //       Export.OuterIndex == SourceIndex + 1  OR  Export.OuterIndex == 0;
        //     the outer is the root package (SourceIndex == -1):
        //       Export.OuterIndex == 0.
        //     So a TOP-level export passes for any outer.
        // The first match in the hash chain wins. The chain is built by inserting at the HEAD
        // in increasing export index (Core.dll.c:29644-29688:
        // `Export.HashNext = Hash[h]; Hash[h] = i`), so among several matches the export with
        // the LARGEST index wins. Class `Mesh` not found: retry with `LodMesh` (111866-111871).
        // An export without RF_Public (flag 4) is rejected by shipped with FailedImportPrivate;
        // here null.
        //
        // Measured: HPBase.u imports HPEdit.Icons.Icons.station/flash, while HPEdit.utx holds
        // Icons.station; the live original 1.53 yields HPEdit.Icons.station
        // (baseStation/SneakBaseStation/TriggeredViewFlash, 85 actors). Lev3_Troll imports
        // HPSounds.Hub3_sfx.Hub5_sfx...., while group Hub5_sfx sits at the top level of HPSounds.
        //
        // chain is from the root: [0] root package, then (name, class, class package | null).
        // Returns the path of the FOUND export; null if the package is unknown or the chain did
        // not resolve (the consumer refuses loudly).
        public string VerifyImportPath(IReadOnlyList<(string Name, string ClassName, string ClassPackage)> chain)
        {
            if (chain is null || chain.Count < 2 || _packages.Count == 0) return null;
            string root = chain[0].Name;
            UnrealPackage source = FindPackage(root) ?? ResourceExportTable(root);
            if (source?.Exports is null) return null;
            int sourceIndex = -1;                       // SourceIndex of the outer import
            for (int level = 1; level < chain.Count; level++)
            {
                var want = chain[level];
                UExportTableItem hit = FindImportExport(source, want.Name, want.ClassName, want.ClassPackage, sourceIndex, root);
                if (hit is null && string.Equals(want.ClassName, "Mesh", StringComparison.OrdinalIgnoreCase))
                    hit = FindImportExport(source, want.Name, "LodMesh", want.ClassPackage, sourceIndex, root);
                if (hit is null || (hit.ObjectFlags & 0x4) == 0) return null;
                sourceIndex = hit.Index;
            }
            var parts = new List<string>();
            for (UObjectTableItem o = source.Exports[sourceIndex]; o is not null; o = o.Outer)
                parts.Insert(0, o.ObjectName.ToString());
            return root + "." + string.Join(".", parts);
        }

        static UExportTableItem FindImportExport(UnrealPackage source, string name, string className,
            string classPackage, int outerSourceIndex, string sourcePackage)
        {
            UExportTableItem best = null;
            foreach (var e in source.Exports)
            {
                if (!string.Equals(e.ObjectName?.ToString(), name, StringComparison.OrdinalIgnoreCase)) continue;
                string cls = e.Class?.ObjectName.ToString() ?? "Class";
                if (!string.Equals(cls, className, StringComparison.OrdinalIgnoreCase)) continue;
                if (classPackage is not null
                    && !string.Equals(ExportClassPackage(e, sourcePackage), classPackage, StringComparison.OrdinalIgnoreCase))
                    continue;
                int outer = (int)e.OuterIndex;
                bool outerOk = outer == 0 || (outerSourceIndex != -1 && outer == outerSourceIndex + 1);
                if (!outerOk) continue;
                if (best is null || e.Index > best.Index) best = e;   // head of the hash chain
            }
            return best;
        }

        // Class package of an export as shipped computes it (29645-29656): class import ->
        // root of its outer chain, class export -> the package itself, no class (a UClass) -> Core.
        static string ExportClassPackage(UExportTableItem e, string sourcePackage)
        {
            UObjectTableItem c = e.Class;
            if (c is null) return "Core";
            if (c is UExportTableItem) return sourcePackage;
            while (c.Outer is not null) c = c.Outer;
            return c.ObjectName.ToString();
        }

        public UObject FindExport(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            int lastDot = name.LastIndexOf('.');
            string leaf = lastDot >= 0 ? name.Substring(lastDot + 1) : name;
            foreach (var pkg in _packages)
                foreach (var e in pkg.Exports)
                {
                    // The leaf is compared BY TABLE, before materialising the object:
                    // e.Object deserialises the export; doing that for every object of every
                    // package costs multiples of memory and time.
                    if (!string.Equals(e.ObjectName.ToString(), leaf, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (lastDot >= 0 && !string.Equals(
                            pkg.PackageName + "." + e.GetPath(), name,
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    var o = e.Object;
                    if (o != null) return o;
                }
            return null;
        }

        // The REAL Object is ONLY Core.Object. `class Object;` in Engine.u/HPBase.u/... is also
        // a Super-less class named Object; treating every class named Object as the hierarchy
        // root made CanonicalSuper(Actor) stop at an import stub, so all Core.u Object script
        // bodies (PlaneV, PlaneC, PlaneF, Plane operators) were unreachable by virtual dispatch
        // ("Virtual function 'PlaneV' not found on Harry"). Natives were unaffected (they go
        // by index).
        // ── THE PORT'S CLASS LOAD ORDER (DrawFrame sprite sort key, UESpriteSort; author decision A 2026-11-03) ──
        //
        // The original keys the sort by the UClass address — heap order, run-dependent. The port ranks a heap class by
        // the moment it first materialises defaults (UEObject.LoadDefaults: map actors in Level.Actors order, then spawns),
        // and right after it the classes named by its own defaultproperties class literals, depth first (the original's
        // linker creates them while loading the class: PadLock.attachedParticleClass = Class'HPParticle.Lock').
        // ponytail: classes the original creates from BYTECODE references at load are ranked only when first
        // materialised here; matches the Tut1/Tut3 captures, add the bytecode walk if a capture disagrees.
        // RF_Native classes (static objects in a DLL) take no rank: they sort after every heap class.
        readonly Dictionary<string, int> _loadRank = new(UEPropNameComparer.Instance);
        public IReadOnlyDictionary<string, int> LoadRanks => _loadRank;
        public int ClassLoadRank(UClass cls) => cls is not null && _loadRank.TryGetValue(cls.Name, out int r) ? r : -1;

        public void NoteClassLoaded(UClass cls)
        {
            UClass c = Canonical(cls);
            if (c is null || _loadRank.ContainsKey(c.Name) || IsNativeClass(c)) return;
            _loadRank[c.Name] = _loadRank.Count;
            foreach (UClass r in DeclaredClassRefs(c)) NoteClassLoaded(r);
            foreach (UClass r in UEObject.DefaultClassRefs(c)) NoteClassLoaded(r);
        }

        // Classes named by the class's own declared variables (UObjectProperty::PropertyClass, UClassProperty::MetaClass) in
        // field order: UStruct::Serialize loads them before the defaults (SpellLearnTrigger.LessonBlackboard ->
        // SpellBlackboard: live GObjObjects index 7927 -> 8095, Tut1Quirrell 9405).
        static IEnumerable<UClass> DeclaredClassRefs(UClass c)
        {
            foreach (var p in c.EnumerateFields<UELib.Core.UProperty>())
            {
                if (p is UELib.Core.UObjectProperty op && op.Object is UClass pc) yield return pc;
                if (p is UELib.Core.UClassProperty cp && cp.MetaClass is UClass mc) yield return mc;
            }
        }

        UClass Canonical(UClass c)
        {
            if (c is null || !IsImportStub(c)) return c;
            UClass real = Find(c.Name);
            return real is not null && !IsImportStub(real) ? real : c;
        }

        // RF_Native (0x04000000) on the class export: a C++ static UClass. Agrees with the live address split for 350 of
        // 358 classes; the 8 others are Fire/Editor classes whose DLLs loaded below 0x10000000 (no actor classes).
        public bool IsNativeClass(UClass cls)
            => Canonical(cls)?.ExportTable is { } e && ((ulong)e.ObjectFlags & 0x04000000UL) != 0;

        // int32 sort key: heap rank * sizeof(UClass) (0x4fc), native classes the address of
        // ?PrivateStaticClass@A<Name>@@0VUClass@@A in Engine.dll (preferred image base, as in the live captures).
        public int SpriteSortKey(UClass cls)
        {
            UClass c = Canonical(cls);
            if (IsNativeClass(c)) return NativeClassAddress(c);
            int r = ClassLoadRank(c);
            if (r < 0) throw new NotSupportedException($"DrawFrame sprite sort: class {c?.Name} has no load rank (its defaults were never materialised)");
            return checked(r * 0x4fc);
        }

        Dictionary<string, uint> _engineExports;
        int NativeClassAddress(UClass c)
        {
            if (_engineExports is null)
            {
                string dir = FindPackage("Engine")?.PackageDirectory
                    ?? throw new InvalidOperationException("DrawFrame sprite sort: Engine package not loaded (Engine.dll location)");
                _engineExports = PeExports(System.IO.Path.Combine(dir, "Engine.dll"));
            }
            string sym = "?PrivateStaticClass@A" + c.Name + "@@0VUClass@@A";
            if (_engineExports.TryGetValue(sym, out uint addr)) return unchecked((int)addr);
            throw new NotSupportedException($"DrawFrame sprite sort: native class {c.Name} has no {sym} in Engine.dll");
        }

        // PE export table: name -> ImageBase + RVA.
        static Dictionary<string, uint> PeExports(string path)
        {
            byte[] b = System.IO.File.ReadAllBytes(path);
            int pe = BitConverter.ToInt32(b, 0x3c);
            int nsec = BitConverter.ToUInt16(b, pe + 6), optSize = BitConverter.ToUInt16(b, pe + 20), opt = pe + 24;
            uint imageBase = BitConverter.ToUInt32(b, opt + 28), expRva = BitConverter.ToUInt32(b, opt + 96);
            int sec = opt + optSize;
            int Off(uint rva)
            {
                for (int k = 0; k < nsec; k++)
                {
                    int h = sec + 40 * k;
                    uint va = BitConverter.ToUInt32(b, h + 12), size = Math.Max(BitConverter.ToUInt32(b, h + 8), BitConverter.ToUInt32(b, h + 16));
                    if (rva >= va && rva < va + size) return (int)(rva - va + BitConverter.ToUInt32(b, h + 20));
                }
                throw new System.IO.InvalidDataException($"{path}: RVA 0x{rva:x} outside the sections");
            }
            int ed = Off(expRva);
            int nNames = BitConverter.ToInt32(b, ed + 24);
            int funcs = Off(BitConverter.ToUInt32(b, ed + 28)), names = Off(BitConverter.ToUInt32(b, ed + 32)), ords = Off(BitConverter.ToUInt32(b, ed + 36));
            var r = new Dictionary<string, uint>(StringComparer.Ordinal);
            for (int k = 0; k < nNames; k++)
            {
                int no = Off(BitConverter.ToUInt32(b, names + 4 * k)), e = no;
                while (b[e] != 0) e++;
                ushort ord = BitConverter.ToUInt16(b, ords + 2 * k);
                r[System.Text.Encoding.ASCII.GetString(b, no, e - no)] = imageBase + BitConverter.ToUInt32(b, funcs + 4 * ord);
            }
            return r;
        }

        public static bool IsImportStub(UClass cls)
            => cls != null && cls.Super == null
            && !(string.Equals(cls.Name, "Object", StringComparison.OrdinalIgnoreCase)
                 && string.Equals(cls.Package?.PackageName, "Core", StringComparison.OrdinalIgnoreCase));
    }
}
