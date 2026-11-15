// TEXTURES FROM THE USER'S PACKAGES AT RUNTIME.
//
// The build carries no original files, so texture pixels are read here from the user's installation. The rules are
// EXACTLY those of the editor exporter: the same packages (Textures, System, Maps: .utx/.u/.unr) in the same order (file
// name, ordinal; the first path wins), the same class tree (Super from System/*.u), the same states (EXPORTED /
// PROCEDURAL_NO_PIXELS / NO_MIPS / NON_P8 / NO_PALETTE); mip pixels come from the file by StorageOffset (UELib skips
// the bytes of legacy lazy arrays); bMasked and LODSet are package tags (LODSet without a tag — class default 1,
// Texture.uc:93).
//
// A package is found by the path's first segment (Pkg.Group.Name): candidates are files with that name in
// Textures/System/Maps in exporter order; the only shipped name collision is Editor.u / Editor.utx, and Editor.u comes
// first, as in the exporter.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UELib;
using UELib.Core;

namespace HP1.UEScript
{
    public sealed class UETextureSource
    {
        public sealed class TextureData
        {
            public string Path;
            public string State;               // EXPORTED / EXPORTED_RGBA / PROCEDURAL_NO_PIXELS / NO_MIPS / DXT1_FATAL / NON_P8_UNDEFINED / NO_PALETTE
            public int Width, Height;          // mip 0 (0 — no mips)
            public bool Masked;                // bMasked tag
            public int LodSet;                 // LODSet tag, else 1
            public int MipCount;               // number of mips in the package
            public byte[] Palette;             // 256×RGBA as in the package (EXPORTED; null for EXPORTED_RGBA)
            public (int U, int V, byte[] P8)[] Levels;   // mips with data, from 0; the chain stops at a mip without data (EXPORTED_RGBA: R,G,B,A bytes)
        }

        readonly string _root;
        List<string> _packages;                // exporter order
        HashSet<string> _textureClasses, _fractalClasses;
        readonly Dictionary<string, UnrealPackage> _open = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, TextureData> _cache = new(StringComparer.OrdinalIgnoreCase);

        public UETextureSource(string installRoot)
        {
            _root = installRoot ?? throw new ArgumentNullException(nameof(installRoot));
        }

        public IReadOnlyList<string> Packages
        {
            get
            {
                if (_packages is not null) return _packages;
                List<string> list = UEInstallPath.PackageFiles(_root, "Textures", "System", "Maps");
                _packages = list.Distinct(StringComparer.Ordinal)
                    .OrderBy(x => System.IO.Path.GetFileName(x), StringComparer.Ordinal).ToList();
                if (_packages.Count == 0) throw new InvalidOperationException($"textures: no packages under {_root}");
                return _packages;
            }
        }

        void EnsureClasses()
        {
            if (_textureClasses is not null) return;
            var super = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string pkgPath in UEInstallPath.PackageFiles(_root, "System").OrderBy(x => System.IO.Path.GetFileName(x), StringComparer.Ordinal))
            {
                UnrealPackage pkg = UnrealLoader.LoadPackage(pkgPath, FileAccess.Read);
                pkg.InitializePackage(UnrealPackage.InitFlags.All);
                foreach (var exp in pkg.Exports)
                {
                    if (exp.Object is not UClass c) continue;
                    string sup = c.Super?.Name;
                    if (!string.IsNullOrEmpty(sup)) super[exp.ObjectName.ToString()] = sup;
                }
            }
            HashSet<string> Derived(string root)
            {
                var r = new HashSet<string>(StringComparer.Ordinal);
                foreach (string name in super.Keys)
                {
                    string cur = name;
                    for (int hops = 0; hops <= super.Count; hops++)
                    {
                        if (cur == root) { r.Add(name); break; }
                        if (!super.TryGetValue(cur, out string next)) break;
                        cur = next;
                    }
                }
                return r;
            }
            _textureClasses = Derived("Texture");
            _fractalClasses = Derived("FractalTexture");
            if (!_textureClasses.Contains("Texture") || _fractalClasses.Count == 0)
                throw new InvalidOperationException("textures: class tree from System/*.u not built (no Texture or FractalTexture)");
        }

        // THREADS. The frontend's instance opens and deserializes packages here on the window thread while the Core thread
        // may do the same inside its tick (hpengine/CoreThread.cs; UETextureMetrics and UETextureAnim likewise). The
        // UnrealPackage instances are separate; the statics of Eliot.UELib.dll were listed from its IL (every static
        // field with every method that loads or stores it). Package loading and BeginDeserializing reach:
        //   UnrealEncoding.s_ANSI — lazy, the same value from either thread;
        //   UnrealConfig.*, UnrealPackage.Override*Version, LibServices.LogService — read only (no store in the DLL, none in the port);
        //   UDecompilingState.s_inlinedSubObjects — a plain Dictionary: UDefaultProperty's ObjectProperty branch looks every
        //     object reference up in it and ADDS only a reference to the owner's own subobject (Outer == the object
        //     being read), which it then decompiles (UDecompilingState.Tabs, FieldToken.LastField: stored only by the
        //     decompiler, which nothing else in the port calls).
        // No shipped object takes the adding branch: the 242 packages of the US install (System, Maps, Textures, Sounds,
        // Music: 122135 objects, 50782 with properties, 80145 object-reference tags; 16 objects UELib fails to read)
        // deserialized in one process leave the dictionary empty, so both threads only read an empty dictionary.
        // A package with such a subobject (a mod) would make it a concurrent write; then BeginDeserializing needs one
        // lock on both sides.
        UnrealPackage Open(string pkgPath)
        {
            if (_open.TryGetValue(pkgPath, out var pkg)) return pkg;
            EnsureClasses();
            pkg = UnrealLoader.LoadPackage(pkgPath, FileAccess.Read);
            foreach (string tc in _textureClasses) pkg.AddClassType(tc, typeof(UELib.Engine.UTexture));
            pkg.InitializePackage(UnrealPackage.InitFlags.All);
            _open[pkgPath] = pkg;
            return pkg;
        }

        // Full object path: package name, the whole outer chain, name (Core's material path form).
        public static string FullPathOf(string pkgName, UExportTableItem exp) => FullPath(pkgName, exp);

        static string FullPath(string pkgName, UExportTableItem exp)
        {
            var chain = new List<string>();
            for (var outer = exp.Outer; outer != null; outer = outer.Outer) chain.Add(outer.ObjectName.ToString());
            chain.Reverse();
            var sb = new StringBuilder(pkgName);
            foreach (string c in chain) sb.Append('.').Append(c);
            return sb.Append('.').Append(exp.ObjectName.ToString()).ToString();
        }

        // Store file name the exporter wrote (for comparison and frontend cache keys).
        public static string FileKey(string uePath)
        {
            var sb = new StringBuilder(uePath.Length);
            foreach (char c in uePath)
                sb.Append(c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|' ? '_' : c);
            return sb.ToString();
        }

        static byte[] ReadBulk(string pkgPath, long offset, long size)
        {
            if (offset <= 0 || size <= 0 || size > int.MaxValue) return null;
            using var fs = new FileStream(pkgPath, FileMode.Open, FileAccess.Read);
            if (offset + size > fs.Length) return null;
            fs.Position = offset;
            var buf = new byte[size];
            int read = 0;
            while (read < buf.Length)
            {
                int n = fs.Read(buf, read, buf.Length - read);
                if (n <= 0) break;
                read += n;
            }
            return read == buf.Length ? buf : null;
        }

        // Texture by full UE path; null — no package with that name has the path.
        public TextureData Get(string uePath)
        {
            if (string.IsNullOrEmpty(uePath)) return null;
            if (_cache.TryGetValue(uePath, out var hit)) return hit;
            int dot = uePath.IndexOf('.');
            string pkgName = dot > 0 ? uePath.Substring(0, dot) : uePath;
            TextureData found = null;
            foreach (string pkgPath in Packages)
            {
                if (!string.Equals(System.IO.Path.GetFileNameWithoutExtension(pkgPath), pkgName, StringComparison.OrdinalIgnoreCase)) continue;
                found = FindIn(pkgPath, uePath);
                if (found is not null) break;
            }
            _cache[uePath] = found;
            return found;
        }

        TextureData FindIn(string pkgPath, string uePath)
        {
            UnrealPackage pkg = Open(pkgPath);
            string pkgName = System.IO.Path.GetFileNameWithoutExtension(pkgPath);
            foreach (var exp in pkg.Exports)
            {
                string cls = exp.ClassName?.ToString() ?? "Class";
                if (!_textureClasses.Contains(cls)) continue;
                if (!string.Equals(FullPath(pkgName, exp), uePath, StringComparison.OrdinalIgnoreCase)) continue;
                return Read(pkgPath, pkgName, exp, cls);
            }
            return null;
        }

        // Paths of all shipped textures in exporter order, without reading content — for the mesh "package + leaf"
        // index and "does the store know this path" queries.
        List<string> _paths;
        public IReadOnlyList<string> Paths()
        {
            if (_paths is not null) return _paths;
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string pkgPath in Packages)
            {
                UnrealPackage pkg = Open(pkgPath);
                string pkgName = System.IO.Path.GetFileNameWithoutExtension(pkgPath);
                foreach (var exp in pkg.Exports)
                    if (_textureClasses.Contains(exp.ClassName?.ToString() ?? "Class"))
                    {
                        string path = FullPath(pkgName, exp);
                        if (seen.Add(path)) list.Add(path);
                    }
            }
            return _paths = list;
        }

        // All shipped textures in exporter order (the first path wins) — for comparison.
        public IEnumerable<TextureData> All()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string pkgPath in Packages)
            {
                UnrealPackage pkg = Open(pkgPath);
                string pkgName = System.IO.Path.GetFileNameWithoutExtension(pkgPath);
                foreach (var exp in pkg.Exports)
                {
                    string cls = exp.ClassName?.ToString() ?? "Class";
                    if (!_textureClasses.Contains(cls)) continue;
                    string path = FullPath(pkgName, exp);
                    if (!seen.Add(path)) continue;
                    yield return Read(pkgPath, pkgName, exp, cls);
                }
            }
        }

        TextureData Read(string pkgPath, string pkgName, UExportTableItem exp, string cls)
        {
            string path = FullPath(pkgName, exp);
            if (exp.Object is not UELib.Engine.UTexture tex)
                throw new InvalidDataException($"texture {path}: object of class {cls} not read as UTexture");
            tex.BeginDeserializing();
            string lod = tex.Properties?.Find("LODSet")?.Value;
            var d = new TextureData
            {
                Path = path,
                MipCount = tex.Mips?.Count ?? 0,
                LodSet = lod is null ? 1 : int.Parse(lod),
            };
            if (tex.Mips == null || tex.Mips.Count == 0) { d.State = "NO_MIPS"; return d; }
            if (tex.Format != UELib.Engine.UBitmapMaterial.TextureFormat.P8) return ReadDirect(pkgPath, tex, d);
            if (tex.Palette == null) { d.State = "NO_PALETTE"; return d; }
            tex.Palette.BeginDeserializing();
            if (tex.Palette.Colors == null || tex.Palette.Colors.Count < 256) { d.State = "NO_PALETTE"; return d; }
            var mip = tex.Mips[0];
            d.Width = mip.USize; d.Height = mip.VSize;
            byte[] idx = ReadBulk(pkgPath, mip.Data.StorageOffset, mip.Data.StorageSize);
            if (idx == null || idx.Length < d.Width * d.Height)
            {
                // A FractalTexture descendant draws itself (UFractalTexture::PostLoad/Prime): the package has no
                // pixels by design of the class.
                if (_fractalClasses.Contains(cls)) { d.State = "PROCEDURAL_NO_PIXELS"; return d; }
                throw new InvalidDataException($"texture {path}: mip 0 {d.Width}x{d.Height}, read {idx?.Length ?? 0} bytes at offset {mip.Data.StorageOffset}");
            }
            d.State = "EXPORTED";
            d.Masked = string.Equals(tex.Properties?.Find("bMasked")?.Value, "true", StringComparison.OrdinalIgnoreCase);
            d.Palette = new byte[1024];
            // UPalette::Serialize (Engine.dll.c:133894): for a package of version < 0x42 the alpha of EVERY colour is
            // set to 0xFF on load (`Colors(i).A = 0xff`). Shipped has six such packages of version 61 (Detail, FireEng,
            // GenFX, GreatFire, Liquids, Palettes) — their file stores A = 0. Verified against live original memory
            // (GenFX.Palette44 — A = 0xFF).
            bool alphaFF = tex.Palette.Package.Version < 0x42;
            for (int i = 0; i < 256; i++)
            {
                var c = tex.Palette.Colors[i];
                d.Palette[i * 4] = c.R; d.Palette[i * 4 + 1] = c.G; d.Palette[i * 4 + 2] = c.B; d.Palette[i * 4 + 3] = alphaFF ? (byte)0xff : c.A;
            }
            var levels = new List<(int, int, byte[])> { (d.Width, d.Height, idx) };
            for (int k = 1; k < tex.Mips.Count; k++)
            {
                var mk = tex.Mips[k];
                byte[] dk = ReadBulk(pkgPath, mk.Data.StorageOffset, mk.Data.StorageSize);
                if (dk == null || dk.Length < mk.USize * mk.VSize) break;   // the chain stops, as in the exporter
                levels.Add((mk.USize, mk.VSize, dk));
            }
            d.Levels = levels.ToArray();
            return d;
        }

        // NON-P8 TEXTURE AS D3DDRV UPLOADS IT. UTexture::Lock (Engine 0x10421220) hands the device Format (+0x28) and
        // Mips (+0xa4); CompMips (test 0x10421261) are taken only when RenDev+0x68 (SupportsTC) is set, and D3DDrv
        // never sets it (its only RecognizePixelFormat calls, 0x1000ce01/0x1000ce7a, are 8888 into +0x1b0 and 1555 into
        // +0x214). The port's device is the 8888 one (+0x1b0 set: a 32-bit mode), as its P8 path (FUN_10002490).
        // UD3DRenderDevice::SetTexture (0x10009a80) picks the pool by Format (0x10009b27):
        //   3 (DXT1) -> pool +0x27c on pixel format +0xe8, never recognized: MaxU/MaxV are 0, no mip fits and
        //     GError->Logf("D3D Driver: Encountered oversize texture without sufficient mipmaps") (0x10009f3d) —
        //     the original stops with a fatal error. State DXT1_FATAL.
        //   1, 2, 4, 5 and any other non-zero value -> pool +0x284 (8888) whose converter 0x100022c0 writes, for
        //     UClamp x VClamp texels of EVERY mip, dword(dst) = dword(src) << 1 into A8R8G8B8 (bytes B,G,R,A), ignoring
        //     PolyFlags (no masked/opacity variant). It reads 4 bytes per texel and the clamp of the FIRST mip, so it is
        //     defined only for one mip of >= U*V*4 bytes; Format 1 is written with one call (0x1000a0a2), so a surface
        //     larger than the mip (U or V below the minimum at +0x320/+0x324 — max(device caps, 4), SetRes
        //     0x1000d800 — or an aspect beyond the device's +0x334) keeps undefined texels. The port takes exactly
        //     the defined case: one mip, square, 4 <= U, >= U*V*4 bytes; anything else is NON_P8_UNDEFINED (a loud
        //     STOP, not a guess; the strict rule is kept by the author's decision 2026-11-15). Formats of 1 or 2 bytes per texel (RGB16, L8, DXT3/5 of later engines) fail the
        //     size test: the converter would read past the mip.
        // Levels then hold R,G,B,A bytes ready for upload (State EXPORTED_RGBA, Palette null).
        TextureData ReadDirect(string pkgPath, UELib.Engine.UTexture tex, TextureData d)
        {
            int format = (int)tex.Format;
            var mip = tex.Mips[0];
            d.Width = mip.USize; d.Height = mip.VSize;
            if (format == 3) { d.State = "DXT1_FATAL"; return d; }
            byte[] src = tex.Mips.Count == 1 ? ReadBulk(pkgPath, mip.Data.StorageOffset, mip.Data.StorageSize) : null;
            byte[] rgba = src is null ? null : D3DConvert8888(src, mip.USize, mip.VSize);
            if (rgba is null) { d.State = "NON_P8_UNDEFINED"; return d; }
            d.State = "EXPORTED_RGBA";
            d.Masked = string.Equals(tex.Properties?.Find("bMasked")?.Value, "true", StringComparison.OrdinalIgnoreCase);
            d.Levels = new[] { (mip.USize, mip.VSize, rgba) };
            return d;
        }

        // D3DDrv 0x100022c0 (8888 pool, non-P8): dst = src << 1 per dword, A8R8G8B8 -> R,G,B,A bytes. Null outside the
        // defined case (ReadDirect).
        public static byte[] D3DConvert8888(byte[] src, int u, int v)
        {
            if (u != v || u < 4 || src.Length < u * v * 4) return null;
            var rgba = new byte[u * v * 4];
            for (int i = 0; i < u * v; i++)
            {
                uint d = BitConverter.ToUInt32(src, i * 4) << 1;
                rgba[i * 4] = (byte)(d >> 16); rgba[i * 4 + 1] = (byte)(d >> 8); rgba[i * 4 + 2] = (byte)d; rgba[i * 4 + 3] = (byte)(d >> 24);
            }
            return rgba;
        }

        // ── FONTS (UFont) ─────────────────────────────────────────────────────
        // Glyphs (StartU/StartV/USize/VSize, page, VerticalOffset), Kerning and pages (the font's UTexture). A font is
        // addressed by LEAF name (as the HUD looks it up); packages — System/*.u and Textures/*.utx. Stubs whose
        // characters cannot be read (HPMenu.u: BigInkFont/SmallInkFont/TinyInkFont) do not count; two readable fonts
        // under one leaf are a loud refusal, not a choice.
        public sealed class FontData
        {
            public string Name, Path;
            public int Kerning;
            public string[] PagePaths;
            public (int Char, int Page, int U, int V, int W, int H, int VO)[] Glyphs;
        }

        Dictionary<string, FontData> _fonts;
        public FontData GetFont(string leaf)
        {
            if (_fonts is null)
            {
                _fonts = new Dictionary<string, FontData>(StringComparer.OrdinalIgnoreCase);
                var files = UEInstallPath.PackageFiles(_root, "System", "Textures")
                    .OrderBy(x => System.IO.Path.GetFileName(x), StringComparer.Ordinal);
                foreach (string f in files)
                {
                    UnrealPackage pkg = Open(f);
                    string pkgName = System.IO.Path.GetFileNameWithoutExtension(f);
                    foreach (var exp in pkg.Exports)
                    {
                        if (exp.Object is not UELib.Engine.UFont font) continue;
                        font.BeginDeserializing();
                        // A stub (characters not read) is not a font; zero characters is a genuine empty font of the
                        // US build (Asian/Jap/Thai/System*): returned empty.
                        if (font.Characters == null || font.Textures == null) continue;
                        string name = exp.ObjectName.ToString();
                        if (_fonts.ContainsKey(name))
                            throw new InvalidDataException($"font {name}: two parsed fonts under one name ({_fonts[name].Path} and {FullPath(pkgName, exp)})");
                        var d = new FontData { Name = name, Path = FullPath(pkgName, exp), Kerning = font.Kerning };
                        d.PagePaths = new string[font.Textures.Count];
                        for (int p = 0; p < font.Textures.Count; p++) d.PagePaths[p] = font.Textures[p]?.GetPath();
                        d.Glyphs = new (int, int, int, int, int, int, int)[font.Characters.Count];
                        for (int i = 0; i < font.Characters.Count; i++)
                        {
                            var ch = font.Characters[i];
                            d.Glyphs[i] = (i, ch.TextureIndex, ch.StartU, ch.StartV, ch.USize, ch.VSize, ch.VerticalOffset);
                        }
                        _fonts[name] = d;
                    }
                }
            }
            return _fonts.TryGetValue(leaf, out var hit) ? hit : null;
        }

        // The exporter's classic store: "HP1C", version 1, masked, palette, mips — for comparison.
        public static byte[] ClassicBytes(TextureData d)
        {
            using var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(Encoding.ASCII.GetBytes("HP1C"));
                bw.Write(1);
                bw.Write(d.Masked ? 1 : 0);
                bw.Write(d.Palette);
                bw.Write(d.Levels.Length);
                foreach (var (u, v, p8) in d.Levels) { bw.Write(u); bw.Write(v); bw.Write(p8, 0, u * v); }
            }
            return ms.ToArray();
        }

        // Exporter manifest row (texture_assets.tsv) — for comparison.
        public static string ManifestLine(TextureData d)
            => d.State == "EXPORTED"
                ? $"{d.Path}\tEXPORTED\t{FileKey(d.Path)}.png\t{d.Width}\t{d.Height}\t{(d.Masked ? "true" : "false")}"
                : d.State == "PROCEDURAL_NO_PIXELS"
                    ? $"{d.Path}\tPROCEDURAL_NO_PIXELS\t-\t{d.Width}\t{d.Height}\t-"
                    : $"{d.Path}\t{d.State}\t-\t0\t0\t-";
    }
}
