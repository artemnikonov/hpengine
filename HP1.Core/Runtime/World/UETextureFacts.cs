// TEXTURE data needed by native 285 TraceTexture, read from SHIPPED packages by ONE reader shared by all frontends
// (two independent resolvers of one answer diverge silently).
//
// WHAT IS READ AND HOW IT IS KNOWN.
//   * FootstepSound — a byte property of class Texture in the KnowWonder build
//     (`--dumpprops Texture`: 0x078 Texture.FootstepSound UByteProperty). A missing tag means the class default, 0 (stone).
//   * PolyFlags — NOT a separate property: in UE1 it is the same 32-bit word that holds the surface bool flags.
//     Proven by the same dump's layout: offset 0x080 holds EXACTLY 32 bools from bInvisible (bit 0x1) to bX7
//     (bit 0x80000000), and shipped execTraceTexture reads `texture + 0x80` (disasm 0x1037ad2c) and ORs it into the
//     out parameter with the surface PolyFlags (0x1037ad39).
//
// THE POSITIONAL BIT RULE IS VERIFIED AGAINST EXECUTABLE CODE, NOT GUESSED. UE1 assigns bools bits in declaration
// order within one word; for class Texture this gives bMasked=0x2, bTransparent=0x4, bModulate=0x40,
// bUnlit=0x400000. All four are confirmed by the shipped sprite renderer (Render.dll.c:9726-9738): for style 2/3/4
// it sets 0x400002 / 0x400004 / 0x400040, i.e. PF_Unlit|PF_Masked, PF_Unlit|PF_Translucent, PF_Unlit|PF_Modulated.
// The rule is checked at startup (PolyBits) and fails loudly if the class layout shifts.
using System;
using System.Collections.Generic;
using System.IO;
using UELib;
using UELib.Core;

namespace HP1.UEScript
{
    public sealed class UETextureFacts
    {
        // KnowWonder byte: 0 stone, 1 carpet, 2 wood, 3 rock, 5 water, 6 grass.
        public readonly byte FootstepSound;
        // Surface bool-flag word = UTexture::PolyFlags of the shipped build.
        public readonly int PolyFlags;

        public UETextureFacts(byte footstepSound, int polyFlags)
        {
            FootstepSound = footstepSound;
            PolyFlags = polyFlags;
        }
    }

    public sealed class UETextureFactsReader
    {
        private string _installRoot;
        private string _mapPath;
        private readonly Dictionary<string, UnrealPackage> _packages =
            new(UEPropNameComparer.Instance);
        private readonly Dictionary<string, UETextureFacts> _cache =
            new(UEPropNameComparer.Instance);
        private Dictionary<string, int> _polyBits;

        // Installation root (the one containing System/Maps/Textures/...) and the map path for textures embedded in
        // the map itself.
        //
        // A MAP CHANGE RESETS THE CACHES, by necessity: the key of a map-embedded texture is "<map>", so without a reset
        // the second level would read the FIRST level's package and answer with its texture. Reachable in a runtime
        // that loads levels one after another (the same defect class that makes LoadWorldGeometry refuse to load over
        // a live world).
        public void SetSource(string installRoot, string mapPath)
        {
            if (!string.Equals(_mapPath, mapPath, StringComparison.OrdinalIgnoreCase))
            {
                _packages.Clear();
                _cache.Clear();
            }
            _installRoot = installRoot;
            _mapPath = mapPath;
        }

        public bool HasSource => _installRoot != null || _mapPath != null;

        // The map the caches belong to right now. Public because "empty" and "points at THAT map" are different
        // claims, and the second is checkable only this way.
        public string MapSource => _mapPath;

        // How many textures are already parsed. Same purpose: an empty cache after teardown is a fact, not a hope.
        public int CachedCount => _cache.Count;

        // WORLD TEARDOWN CLEARS THIS TOO. SetSource resets caches on a map CHANGE, but between teardown and the next
        // map there is a window in which the reader would keep the packages and parsed textures of a map that no
        // longer exists (or, after a FAILED load, never existed).
        //
        // Class defaults and the PolyFlags bit layout are intentionally NOT cleared: they belong to the game's class
        // build, not to the level, and survive a map change in the original too.
        public void Reset()
        {
            _packages.Clear();
            _cache.Clear();
            _installRoot = null;
            _mapPath = null;
        }

        public UETextureFacts Read(SurfMaterialRef material)
        {
            if (material is null) return null;
            // KEY — THE SAME IDENTITY THAT FindExport RESOLVES, not the truncated form
            // truncated form `Package.Group.Name.LocalExport`: without class and without
            // (without class and full outer chain), which would distinguish LESS than the lookup. The single identity
            // expression lives in SurfMaterialRef; both consumers take it from there.
            string key = material.IdentityKey;
            if (_cache.TryGetValue(key, out UETextureFacts cached)) return cached;

            UnrealPackage package = Package(material);
            UExportTableItem export = FindExport(package, material);
            var facts = FactsOfExport(export, out _);
            _cache[key] = facts;
            return facts;
        }

        // Export flag word and the AnimNext tag marker (read only by the HUD path).
        private UETextureFacts FactsOfExport(UExportTableItem export, out bool hasAnimNext)
        {
            hasAnimNext = false;
            UObject source = export.Object;
            source.Load<UObjectRecordStream>();

            // START FROM CLASS DEFAULTS, not zeros. The engine loads an object over its default, and a package carries
            // tags only for OVERRIDDEN fields: starting from zero would silently lose everything the class sets itself.
            // Defaults come from the same mechanism as for any actor (UEObject.LoadDefaults along the class chain) —
            // no second author of defaults.
            (byte footstep, int poly) = ClassDefaults(export.Class?.ObjectName.ToString());
            Dictionary<string, int> bits = PolyBits();
            if (source.Properties is not null)
                foreach (var tag in source.Properties)
                {
                    string name = tag?.Name.ToString();
                    if (name is null) continue;
                    if (name.Equals("FootstepSound", StringComparison.OrdinalIgnoreCase))
                    {
                        byte[] raw = UEActorGraphLoader.RawValue(source, name, tag.ArrayIndex);
                        if (raw is { Length: > 0 }) footstep = raw[0];
                    }
                    else if (bits.TryGetValue(name, out int bit))
                    {
                        // A tag also CLEARS a bit: `bMasked=False` over the class default means False, not
                        // "no tag".
                        if (tag.BoolValue == true) poly |= bit;
                        else poly &= ~bit;
                    }
                    else if (name.Equals("AnimNext", StringComparison.OrdinalIgnoreCase))
                        hasAnimNext = true;
                }

            return new UETextureFacts(footstep, poly);
        }

        // ── Texture->PolyFlags for UCanvas::DrawTile (Engine 0x10387630) ──
        //
        // The body passes the device `(Texture->PolyFlags & PF_Masked) | exec flags`
        // (disasm 0x1038781f-0x1038782e: `mov edx,[esi+0x80]; and edx,2; or edx, [ebp+0x54]`). Here — the flag word
        // of the texture the body passes to the device. That texture is Texture->AnimCur if non-null, after
        // Texture->Update(Viewport->CurrentTime) (0x103877c8-0x103877f5). Update of animated textures is not ported,
        // so a texture with AnimNext is a loud STOP, not first-frame flags.
        //
        // The resource reference is resolved EXACTLY (UEClassRegistry.ResolveResourceExport: package, full outer chain,
        // name, class), and the word by the same parse as TraceTexture (class defaults + tags). A Core runtime
        // texture (CreateTextureFromBMP) is a live object: its bool properties.
        private readonly Dictionary<string, (int Poly, bool Anim)> _resourceFlags =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, UnrealPackage> _packagesByPath =
            new(StringComparer.OrdinalIgnoreCase);

        public int PolyFlagsOfResource(object tex)
        {
            string path, cls;
            switch (tex)
            {
                case UEObject o when !o.IsStruct:
                    if (o.GetProp(PK.AnimNext) is not null)
                        throw new NotSupportedException(
                            $"UCanvas::DrawTile: texture {o.GetProp(PK.Name)} has AnimNext — "
                            + "flags are taken from AnimCur after UTexture::Update, which is not ported");
                    int live = 0;
                    foreach (var pair in PolyBits())
                        if (UEInterpreter.ToBool(o.GetProp(pair.Key))) live |= pair.Value;
                    return live;
                case UEResourceRef rr: path = rr.ObjectPath ?? rr.Name; cls = rr.Class; break;
                case UObject uo: path = uo.GetPath(); cls = uo.Class?.Name; break;
                default:
                    throw new NotSupportedException(
                        $"UCanvas::DrawTile: texture reference of unknown kind {tex?.GetType().Name}");
            }
            string key = (cls ?? "<?>") + "|" + path;
            if (!_resourceFlags.TryGetValue(key, out var hit))
            {
                var at = HP1.Core.UEClassRegistry.Shared.ResolveResourceExport(path, cls)
                    ?? throw new NotSupportedException(
                        $"UCanvas::DrawTile: texture {cls}'{path}' does not resolve to a package export");
                string file = HP1.Core.UEClassRegistry.PhysicalPath(at.PackagePath);
                if (!_packagesByPath.TryGetValue(file, out UnrealPackage pkg))
                {
                    pkg = UnrealLoader.LoadPackage(at.PackagePath, FileAccess.Read);
                    pkg.InitializePackage(UnrealPackage.InitFlags.All);
                    _packagesByPath[file] = pkg;
                }
                UETextureFacts facts = FactsOfExport(pkg.Exports[at.ExportIndex - 1], out bool anim);
                hit = (facts.PolyFlags, anim);
                _resourceFlags[key] = hit;
            }
            if (hit.Anim)
                throw new NotSupportedException(
                    $"UCanvas::DrawTile: texture {path} has AnimNext — flags are taken from "
                    + "AnimCur after UTexture::Update, which is not ported");
            // Bits WRITTEN by bytecode into this texture object, over the package word: FEBook.Created sets
            // LogoTexture1/2.bMasked = true, and shipped draws the logo masked (without it — black index-0 background
            // under the menu buttons). The proxy carries only written properties and sizes.
            int poly = hit.Poly;
            if (UEInterpreter.ExistingTextureProxy(tex) is UEObject written)
                foreach (var pair in PolyBits())
                    if (written.Props.TryGetValue(pair.Key, out object v))
                        poly = UEInterpreter.ToBool(v) ? poly | pair.Value : poly & ~pair.Value;
            return poly;
        }

        // Default FootstepSound and PolyFlags word for the export's CLASS. The class is taken from the export itself,
        // not assumed "Texture": packages contain subclasses (FireTexture, WetTexture, ScriptedTexture), and their
        // defaultproperties are original data too.
        private readonly Dictionary<string, (byte Footstep, int Poly)> _classDefaults =
            new(UEPropNameComparer.Instance);

        private (byte, int) ClassDefaults(string className)
        {
            className ??= "Texture";
            if (_classDefaults.TryGetValue(className, out var cached)) return cached;
            UClass cls = HP1.Core.UEClassRegistry.Shared.Find(className)
                ?? HP1.Core.UEClassRegistry.Shared.FindImportedClass(className)
                ?? throw new NotSupportedException(
                    $"TraceTexture: texture class {className} not found — "
                    + "nowhere to take defaults from");
            var probe = new UEObject { Class = cls };
            probe.LoadDefaults();
            byte footstep = (byte)UEInterpreter.ToInt(probe.GetProp(PK.FootstepSound));
            int poly = 0;
            foreach (var pair in PolyBits())
                if (UEInterpreter.ToBool(probe.GetProp(pair.Key))) poly |= pair.Value;
            var facts = (footstep, poly);
            _classDefaults[className] = facts;
            return facts;
        }

        // PACKAGE SEARCH ORDER COMES FROM THE GAME ITSELF: `System/0/Default.ini`, section [Core.System]. Texture
        // references are not only .utx: measured over 41 maps, surfaces reference objects in HPParticle.u, HProps.u,
        // HPBase.u and Engine.u, and a "<root>/Textures/<package>.utx" rule would answer "no package" for them
        // (37 references).
        internal static readonly string[] PackagePaths =
        {
            "System/{0}.u", "Maps/{0}.unr", "Textures/{0}.utx",
            "Sounds/{0}.uax", "Music/{0}.umx", "save/{0}.usa",
        };

        private UnrealPackage Package(SurfMaterialRef material)
        {
            string packageName = material.SourcePackage ?? "<map>";
            if (_packages.TryGetValue(packageName, out UnrealPackage cached)) return cached;
            string path = null;
            if (material.SourcePackage is null)
            {
                path = _mapPath ?? throw new NotSupportedException(
                    "TraceTexture: map path not set, nowhere to read the embedded texture from");
            }
            else
            {
                string root = _installRoot ?? throw new NotSupportedException(
                    "TraceTexture: install root not set");
                foreach (string pattern in PackagePaths)
                {
                    path = UEInstallPath.File(Path.Combine(root,
                        string.Format(pattern, material.SourcePackage)));
                    if (path is not null) break;
                }
                if (path is null)
                    throw new NotSupportedException(
                        $"TraceTexture: package {material.SourcePackage} not found on any "
                        + $"of the [Core.System] paths under {root}");
            }
            UnrealPackage package = UnrealLoader.LoadPackage(path, FileAccess.Read);
            package.InitializePackage(UnrealPackage.InitFlags.All);
            _packages[packageName] = package;
            return package;
        }

        // REFERENCE IDENTITY — (class, name, FULL outer chain), as UE1 import resolution does. Checking only the
        // IMMEDIATE outer would take any namesake for a groupless reference (`HP_Quidditch..QuidTrees1`; 21 such
        // references over 41 maps). No heuristics: if the chain does not match, it is the wrong export.
        private static UExportTableItem FindExport(UnrealPackage package, SurfMaterialRef material)
        {
            if (material.LocalExport > 0)
            {
                if (material.LocalExport - 1 >= package.Exports.Count)
                    throw new NotSupportedException(
                        $"TraceTexture: texture export {material.LocalExport} outside the table");
                return package.Exports[material.LocalExport - 1];
            }
            // Outers go from the immediate one to the package; everything except the last (the package itself) is
            // inside the package. The expression lives in SurfMaterialRef.OuterChain: it is used BOTH for the export
            // lookup AND for the cache key — otherwise the cache would distinguish something other than the lookup.
            string[] want = material.OuterChain;
            UExportTableItem match = null;
            foreach (UExportTableItem export in package.Exports)
            {
                if (!export.ObjectName.ToString().Equals(material.Name,
                        StringComparison.OrdinalIgnoreCase)) continue;
                // The class is part of the identity. Without it a name lookup takes a namesake of another class:
                // for `Editor..Bad` that is a palette instead of a texture (found by `--dump-texfacts`).
                if (material.ClassName is not null
                    && !material.ClassName.Equals(export.Class?.ObjectName.ToString(),
                        StringComparison.OrdinalIgnoreCase)) continue;
                if (!OuterChainMatches(package, export, want)) continue;
                if (match is not null)
                    throw new NotSupportedException(
                        $"TraceTexture: reference is ambiguous even by the full chain: "
                        + $"{material.Path}");
                match = export;
            }
            return match ?? throw new NotSupportedException(
                $"TraceTexture: texture export missing: {material.Path}");
        }

        private static bool OuterChainMatches(UnrealPackage package, UExportTableItem export,
            string[] want)
        {
            int i = 0;
            for (int outer = (int)export.OuterIndex; outer > 0; i++)
            {
                if (outer - 1 >= package.Exports.Count) return false;
                UExportTableItem o = package.Exports[outer - 1];
                if (i >= want.Length
                    || !want[i].Equals(o.ObjectName.ToString(), StringComparison.OrdinalIgnoreCase))
                    return false;
                outer = (int)o.OuterIndex;
            }
            return i == want.Length;
        }

        // bool property name -> its bit in the PolyFlags word (offset 0x80). Bits are assigned in declaration order
        // within the Texture class ITSELF; the first non-bool property closes the word.
        private Dictionary<string, int> PolyBits()
        {
            if (_polyBits is not null) return _polyBits;
            UClass texture = HP1.Core.UEClassRegistry.Shared.Find("Texture")
                ?? HP1.Core.UEClassRegistry.Shared.FindImportedClass("Texture")
                ?? throw new NotSupportedException(
                    "TraceTexture: class Texture not found — nowhere to take the PolyFlags layout from");
            var bits = new Dictionary<string, int>(UEPropNameComparer.Instance);
            int bit = 0;
            bool closed = false;
            foreach (UProperty pr in texture.EnumerateFields<UProperty>())
            {
                if (pr is UBoolProperty)
                {
                    if (closed) break;              // the PolyFlags word is already closed
                    bits[pr.Name] = 1 << bit;
                    if (++bit == 32) closed = true;
                }
                else if (bit > 0) closed = true;    // a non-bool closes the word
            }
            // LAYOUT PIN (verified against the shipped renderer's executable code,
            // Render.dll.c:9726-9738 — sprite styles 2/3/4 give 0x400002, 0x400004, 0x400040).
            foreach ((string name, int want) in new[]
                     { ("bMasked", 0x2), ("bTransparent", 0x4),
                       ("bModulate", 0x40), ("bUnlit", 0x400000) })
                if (!bits.TryGetValue(name, out int got) || got != want)
                    throw new NotSupportedException(
                        $"TraceTexture: PolyFlags layout diverged from shipped: "
                        + $"{name} = 0x{(bits.TryGetValue(name, out int g) ? g : 0):x}, "
                        + $"expected 0x{want:x}");
            _polyBits = bits;
            return bits;
        }
    }
}
