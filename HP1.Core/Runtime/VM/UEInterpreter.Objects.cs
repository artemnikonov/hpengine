// Objects and resources: texture proxies for resource references, class default bags and config
// reload, ObjectPathName and NewObject. Fields live in UEInterpreter.cs.
using System;
using System.Collections.Generic;
using UELib;
using UELib.Core;
using Tok = UELib.Core.UStruct.UByteCodeDecompiler;
using UELib.Tokens;  // CastToken enum (UnrealTokens.cs)
using HP1.Core;      // UETrace sinks + UEClassRegistry
// Script vectors ARE the Core value type: prop-bag storage, struct decode,
// literals and the As*-surface all use HP1.UEScript.UEVector.
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    public partial class UEInterpreter
    {
        static string TextureIdentity(UELib.Core.UObject exp)
            => HP1.Core.UEClassRegistry.PhysicalPath(exp.Package.FullPackageName) + "#" + exp.ExportTable.Index;

        // A reference that names its full object path ("Package.Group.Name") resolves by that path only.
        static bool HasFullPath(UEResourceRef r)
            => r.ObjectPath is { } p && p.Contains('.') && !p.StartsWith("export:", StringComparison.Ordinal);

        static string RefSpelling(object refVal) => refVal switch
        {
            string s => "s|" + s,
            UEResourceRef r => "r|" + r.Class + "|" + r.ObjectPath + "|" + r.Name + "|" + r.IdentityKey,
            _ => null,
        };

        static bool TryCachedProxy(object refVal, out UEObject proxy)
        {
            proxy = null;
            if (refVal is UELib.Core.UObject) return _proxyByRef.TryGetValue(refVal, out proxy);
            return RefSpelling(refVal) is string k && _proxyByRefSpelling.TryGetValue(k, out proxy);
        }

        static void RememberProxy(object refVal, UEObject proxy)
        {
            if (refVal is UELib.Core.UObject) _proxyByRef[refVal] = proxy;
            else if (RefSpelling(refVal) is string k) _proxyByRefSpelling[k] = proxy;
        }
        // The UE name of a resource reference (string / UEResourceRef / UELib
        // export). Verbatim HP1TextureBank.NameOf — inlined so the Core needs
        // no frontend bank for the pure name extraction.
        static string NameOfResourceRef(object texRef)
        {
            if (texRef == null) return null;
            if (texRef is string s) return s;
            if (texRef is UEResourceRef rr) return rr.Name;
            if (texRef is UELib.Core.UObject uo) return uo.Name?.ToString();
            return null;
        }

        // Texture size for Core owners that need it as a NUMBER, not as a
        // member-access context. The first such owner is AttachDecal: the shipped
        // decal radius is sqrt(DrawScale^2 * USize^2 * 0.5) (Engine.dll 0x10410f2c,
        // `Texture->USize` at offset +0x34). Goes through the same resolver as
        // `Tex.USize` from bytecode, so no second source of sizes appears in the tree.
        public static bool TryTextureSize(object refVal, out int uSize, out int vSize)
        {
            uSize = vSize = 0;
            if (TextureProxyFor(refVal) is not UEObject proxy) return false;
            uSize = ToInt(proxy.GetProp(PK.USize));
            vSize = ToInt(proxy.GetProp(PK.VSize));
            return uSize > 0 && vSize > 0;
        }

        static UELib.Core.UObject ResourceExportFromDisk(string objectPath, string className)
        {
            var at = UEClassRegistry.Shared?.ResolveResourceExport(objectPath, className);
            if (at is null || at.Value.PackagePath is null) return null;
            // Keyed by the PHYSICAL file: the same package through a symlinked directory is one parse.
            string key = UEClassRegistry.PhysicalPath(at.Value.PackagePath);
            if (!_resourcePackagesByPath.TryGetValue(key, out UELib.UnrealPackage pkg))
            {
                pkg = UELib.UnrealLoader.LoadPackage(at.Value.PackagePath, System.IO.FileAccess.Read);
                pkg.InitializePackage(UELib.UnrealPackage.InitFlags.All);
                _resourcePackagesByPath[key] = pkg;
            }
            return pkg.Exports[at.Value.ExportIndex - 1].Object;
        }

        // The proxy bytecode has already written to (`Tex.bMasked = true` in
        // FEBook.Created) — WITHOUT creating a new one. Shipped holds one UTexture:
        // a script write changes a bit in the same PolyFlags word (+0x80) that
        // UCanvas::DrawTile reads, so the tile flag reader must see the write
        // (UETextureFactsReader.PolyFlagsOfResource).
        public static UEObject ExistingTextureProxy(object refVal)
        {
            if (refVal is null || refVal is UEObject) return null;
            if (TryCachedProxy(refVal, out UEObject hit)) return hit;
            string name = NameOfResourceRef(refVal);
            if (string.IsNullOrEmpty(name) || name == "None") return null;
            var exp = TextureExportOf(refVal, name);
            if (exp is null || exp is UClass || exp.ExportTable is null) return null;
            return _texProxies.TryGetValue(TextureIdentity(exp), out UEObject p) ? p : null;
        }

        // Texture export by reference — shared path of TextureProxyFor and TryTextureDrawScale:
        // import -> export by full path (leaf as fallback), a path-bearing reference -> by that path only
        // (registry, otherwise the resource package from disk); a leaf-only reference -> by leaf.
        static UELib.Core.UObject TextureExportOf(object refVal, string name)
        {
            if (refVal is UEResourceRef full && HasFullPath(full))
                return UEClassRegistry.Shared.FindExport(full.ObjectPath)
                    ?? ResourceExportFromDisk(full.ObjectPath, full.Class);
            var exp = refVal as UELib.Core.UObject;
            if (exp is not null && exp.Table is UELib.UImportTableItem)
            {
                // A reference through another package's IMPORT table (HPMenu.BroomHud →
                // Texture'HPBase.Icons.TimerBarEmpty'): an import has neither tags nor
                // mips, they must be read from the EXPORT — found by full path, leaf
                // as fallback (otherwise all 9 Quidditch maps crash headless on the
                // first BroomHud frame).
                exp = UEClassRegistry.Shared.FindExport(exp.GetPath()) ?? UEClassRegistry.Shared.FindExport(name);
            }
            exp ??= UEClassRegistry.Shared.FindExport(name);
            // A resource package outside the script registry (DynamicLoadObject(
            // "MenuArt.MoonTitle1") in FEBook): ResolveDynamicLoad returned a ref
            // with the FULL path from the on-disk export table — the export is read
            // by the same exact rule (package, outer chain, name, class) as HUD
            // flags (UETextureFacts) and the render pass. Without it the startup
            // menu crashed headless on the first USize read.
            if (exp is null && refVal is UEResourceRef pathRef && pathRef.ObjectPath is not null)
                exp = ResourceExportFromDisk(pathRef.ObjectPath, pathRef.Class);
            return exp;
        }

        // Texture.DrawScale (+0x6c): the export tag, otherwise the class default (no object
        // creation — no ObjectId spent). Needed for decal U/V normalization (D3DDrv SetTexture).
        public static bool TryTextureDrawScale(object refVal, out float drawScale)
        {
            drawScale = 0f;
            string name = NameOfResourceRef(refVal);
            if (string.IsNullOrEmpty(name) || name == "None") return false;
            var exp = TextureExportOf(refVal, name);
            if (exp is null || exp is UClass) return false;
            exp.BeginDeserializing();
            if (exp.Properties != null)
                foreach (var dp in exp.Properties)
                    if (dp?.Name != null && dp.ArrayIndex == 0 && dp.Type == UELib.Types.PropertyType.FloatProperty
                        && dp.Name.ToString().Equals("DrawScale", StringComparison.OrdinalIgnoreCase)
                        && float.TryParse(dp.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v))
                    { drawScale = v; return true; }
            UClass cls = HP1.Core.UEClassRegistry.Shared.Find(exp.Class?.Name ?? "Texture") ?? HP1.Core.UEClassRegistry.Shared.Find("Texture");
            object d = cls is null ? null : UEObject.ClassDefaultScalar(cls, "DrawScale");
            if (d is null) return false;
            drawScale = AsFloat(d);
            return true;
        }

        static UEObject TextureProxyFor(object refVal)
        {
            if (refVal == null || refVal is UEObject) return null;
            // Only texture-family refs get a proxy. Every UE1 texture class name
            // ends in "Texture" (Texture/Fire/Ice/Water/Wave/Wet/Scripted);
            // class refs and other resource kinds (e.g. Class'Tut1Fred', sounds)
            // must fall through to the Accessed-None path exactly as before.
            if (refVal is UClass) return null;   // a class ref is never a texture
            string kind = refVal is UEResourceRef rr ? rr.Class
                        : (refVal as UELib.Core.UObject)?.Class?.Name;
            if (kind != null && !kind.EndsWith("Texture", StringComparison.OrdinalIgnoreCase))
                return null;
            string name = NameOfResourceRef(refVal);
            if (string.IsNullOrEmpty(name) || name == "None") return null;
            if (TryCachedProxy(refVal, out UEObject known)) return known;
            UEObject proxy = null;
            // Origin-of-truth path first: the texture EXPORT's serialized
            // USize/VSize int tags from the original package. Works with no
            // render backend (headless); the extracted PNG
            // is derived data of these same ints.
            var exp = TextureExportOf(refVal, name);
            if (exp is UClass) return null;  // name resolved to a class
            string identity = exp?.ExportTable is null ? null : TextureIdentity(exp);
            if (identity is not null && _texProxies.TryGetValue(identity, out UEObject same))
            {
                RememberProxy(refVal, same);
                return same;
            }
            if (exp != null)
            {
                exp.BeginDeserializing();
                int u = 0, v = 0;
                if (exp.Properties != null)
                    foreach (var dp in exp.Properties)
                    {
                        if (dp?.Name == null || dp.Type != UELib.Types.PropertyType.IntProperty) continue;
                        string pn = dp.Name.ToString();
                        if (pn.Equals("USize", StringComparison.OrdinalIgnoreCase)) int.TryParse(dp.Value, out u);
                        else if (pn.Equals("VSize", StringComparison.OrdinalIgnoreCase)) int.TryParse(dp.Value, out v);
                    }
                // The export may have no USize/VSize tags at all
                // (HPBase.Icons.TimerBarEmpty — BroomHud/baseHud): shipped
                // UTexture::Serialize takes the size from the FIRST mip (FMipmap
                // USize/VSize, UELib LegacyMipMap) — the same source as the
                // texture exporter (BspDiffProgram.DumpTextureMips).
                if ((u <= 0 || v <= 0) && exp is UELib.Engine.UTexture ut
                    && ut.Mips is not null && ut.Mips.Count > 0)
                {
                    u = ut.Mips[0].USize;
                    v = ut.Mips[0].VSize;
                }
                if (u > 0 && v > 0)
                {
                    proxy = new UEObject();
                    proxy.Props["USize"] = u;
                    proxy.Props["VSize"] = v;
                    proxy.Props["Name"]  = new UEName(name);
                }
            }
            if (proxy == null || identity is null)
                throw new NotSupportedException(
                    $"texture '{RefSpelling(refVal) ?? name}' unresolvable headless: no USize/VSize tags among registered package exports");
            _texProxies[identity] = proxy;
            RememberProxy(refVal, proxy);
            return proxy;
        }
        UEObject ClassDefaultsBag(UClass cls)
        {
            if (!_classDefaults.TryGetValue(cls, out var d))
            {
                d = new UEObject { Class = cls, Interp = this };
                d.LoadDefaults();
                _classDefaults[cls] = d;
            }
            return d;
        }
        // Tail of UObject::SaveConfig (Core.dll.c:63720-63760): for each loaded
        // class with IsChildOf(BaseClass) — LoadConfig(Defaults, 1).
        // The port's default objects are the `default.X` cache; those already
        // built are re-read (not-yet-built ones read the ini when built).
        public void ReloadConfigOfClassDefaults(UClass baseClass, UEConfig config)
        {
            foreach (var kv in _classDefaults)
                if (UEClassRegistry.Shared.IsChildOfClass(kv.Key, baseClass))
                    UEObjectConfig.LoadConfig(config, kv.Value, kv.Key, kv.Key, propagate: true, isInstance: false);
        }

        // Class default WITHOUT building the bag: a built bag holds the live
        // defaults (script may have written default.X; in shipped this is the same
        // class Defaults array), otherwise static parsing of class data.
        public object ClassDefaultVectorNoAlloc(UClass cls, string name)
        {
            if (cls == null || string.IsNullOrEmpty(name)) return null;
            return _classDefaults.TryGetValue(cls, out var d) ? d.GetProp(name) : UEObject.ClassDefaultVector(cls, name);
        }

        public object GetClassDefault(UClass cls, string name)
        {
            if (cls == null || string.IsNullOrEmpty(name)) return null;
            return ClassDefaultsBag(cls).GetProp(name);
        }

        // UE1 EX_Context on a CLASS value (UnScript.cpp EX_Context works on any
        // UObject, and a UClass's property block IS its instance-default block
        // plus the UObject intrinsics). Reached by Engine Pawn.TakeDamage —
        // `InstigatedBy.IsA(Class.Name) || self.IsA(InstigatedBy.Class.Name)`
        // (decompiled bytecode; the Context must not be null here). Model: the cached class-defaults
        // bag, seeded once with the intrinsic Name (the class object's own
        // FName — reads as the class name).
        public UEObject ClassContextObject(UClass cls)
        {
            if (cls == null) return null;
            var d = ClassDefaultsBag(cls);
            if (!d.Props.ContainsKey("Name")) d.Props["Name"] = new UEName(cls.Name);
            return d;
        }


        // ── UObject::execObjectToString (Core.dll.c:59379, body 0x10135470) ──
        //
        // None -> "None" (0x101b29a8); otherwise UObject::GetPathName(NULL)
        // (disasm 0x101596c0..0x1015975c): if Outer (+0x18) is not NULL —
        // Outer->GetPathName, then "." (0x101bf708), then GetName() (FName
        // +0x20). I.e. the full path along the Outer chain, with no special
        // status for the root package: a package has Outer = NULL and its name is
        // the first segment.
        //
        // Outer is a VM object (Props["Outer"], `new(Outer)`) or a package
        // (UEObject.OuterPath: the map package for an actor, "Transient" for `new`
        // without Outer). An object whose outer is NOT MODELLED (engine objects:
        // viewport, console, canvas — created by C++ with outers the port lacks)
        // gets no invented path: a loud warning and a string tagged STOP (§8)
        // that makes the divergence visible in the log.
        public static string ObjectPathName(object v)
        {
            switch (v)
            {
                case null: return "None";
                case UEObject o:
                {
                    object n = o.GetProp(PK.Name);
                    string name = n?.ToString();
                    string outer = o.GetProp(PK.Outer) is UEObject ou && !ReferenceEquals(ou, o)
                        ? ObjectPathName(ou) : o.OuterPath;
                    if (string.IsNullOrEmpty(name) || name == "None" || outer is null)
                    {
                        HP1.Core.UETrace.Warn($"ObjectToString: object path {o.Class?.Name}:{name ?? "?"} "
                            + "not modelled (no name or outer) — STOP, §8");
                        return $"<STOP:path:{o.Class?.Name}:{name ?? "?"}>";
                    }
                    return outer + "." + name;
                }
                case UELib.Core.UObject uo: return HP1.UEScript.Render.ShippedLayout.FullPath(uo);
                case UEResourceRef rr:
                    if (rr.ObjectPath is null || rr.ObjectPath.StartsWith("export:", StringComparison.Ordinal))
                    {
                        HP1.Core.UETrace.Warn($"ObjectToString: resource {rr} has no full path — STOP, §8");
                        return $"<STOP:path:{rr.Class}:{rr.Name}>";
                    }
                    return rr.ObjectPath;
                default: return v.ToString();
            }
        }

        // UE1 `new` operator (NewToken). Constructs a non-actor UObject of the
        // given class. Unlike SpawnActor this creates no frontend object and runs
        // NO actor spawn lifecycle — UWindow windows extend Object (not Actor), are
        // plain VM objects, and the framework calls BeginPlay()/Created() on them
        // itself (UWindowWindow.CreateWindow). Just instantiate + apply class
        // defaults, like the bare construction the HUD/cutscene drivers already do.
        public UEObject NewObject(UClass cls, UEObject outer, string objName)
        {
            if (cls == null) return null;
            // Cross-package declarations bind to import stubs. The original
            // linker resolves those to the real same-FName export even when the
            // real class is data-only and declares no functions.
            if (UEClassRegistry.IsImportStub(cls))
            {
                var real = UEClassRegistry.Shared.FindForStub(cls);
                if (real != null && !UEClassRegistry.IsImportStub(real)) cls = real;
            }
            // UObject::StaticAllocateObject (Core.dll.c:57713-57720): a class with
            // CLASS_Abstract (UClass+0x488 & 1) — LocalizeError("Abstract") to the
            // log and NULL, no object is created; StaticConstructObject on NULL
            // does not call the constructor and returns NULL.
            if ((UEObjectConfig.ClassFlags(cls) & 0x1u) != 0)
            {
                UETrace.Warn($"[VM] new: class {cls.Name} is abstract — None (StaticAllocateObject)");
                return null;
            }
            var obj = new UEObject { Class = cls, Interp = this };
            obj.LoadDefaults();
            if (outer != null) obj.Props["Outer"] = outer;
            // execNew (Core.dll.c:25126-25131): Outer == None -> package Transient.
            else obj.OuterPath = "Transient";
            if (!string.IsNullOrEmpty(objName) && objName != "None")
                obj.Props["Name"] = new UEName(objName);
            // Name None -> StaticConstructObject -> MakeUniqueObjectName(Outer,
            // Class) (Core.dll.c:41313): class-name base + ClassUnique++,
            // while StaticFindObject(NULL, Outer, name) finds an object in THIS
            // outer (Transient or a VM outer object).
            else
                obj.Props["Name"] = new UEName(UELevelRuntime.MintObjectName(cls.Name, () =>
                {
                    int number = ClassUnique.TryGetValue(cls, out int old) ? old : 0;
                    ClassUnique[cls] = number + 1;
                    return number;
                }, outer == null ? TransientObjectNames : _namesInOuter.GetValue(outer, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase))));
            if (obj.GetProp(PK.Name) is UEName minted)
                (outer == null ? TransientObjectNames
                    : _namesInOuter.GetValue(outer, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase))).Add(minted.ToString());
            // StaticConstructObject (Core.dll.c:57880-57883): an instance of a class
            // with CLASS_PerObjectConfig immediately reads ITS OWN section (object
            // name) from the Outer's package file, then the instance's LoadLocalized.
            if ((UEObjectConfig.ClassFlags(cls) & UEObjectConfig.CLASS_PerObjectConfig) != 0)
            {
                if (UEConfig.Global is { } gconfig)
                    UEObjectConfig.LoadConfig(gconfig, obj, cls, cls, propagate: false, isInstance: true);
                UEObjectConfig.LoadLocalized(obj, cls, propagate: false, isInstance: true,
                    outerName: outer?.GetProp(PK.Name)?.ToString(), objName: objName);
            }
            return obj;
        }
    }
}
