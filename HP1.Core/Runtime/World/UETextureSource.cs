// TEXTURES FROM THE USER'S PACKAGES AT RUNTIME.
//
// The build carries no original files, so texture pixels are read here from the user's installation. The rules are
// EXACTLY those of the editor exporter (se_harness BspDiffRunner --exporttextures, the reference for
// HeadlessSmoke --texturesource): the same packages (Textures, System, Maps: .utx/.u/.unr) in the same order (file
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
            public string State;               // EXPORTED / PROCEDURAL_NO_PIXELS / NO_MIPS / NON_P8 / NO_PALETTE
            public int Width, Height;          // mip 0 (0 — no mips)
            public bool Masked;                // bMasked tag
            public int LodSet;                 // LODSet tag, else 1
            public int MipCount;               // number of mips in the package
            public byte[] Palette;             // 256×RGBA as in the package (EXPORTED)
            public (int U, int V, byte[] P8)[] Levels;   // mips with data, from 0; the chain stops at a mip without data
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
                var list = new List<string>();
                foreach (string sub in new[] { "Textures", "System", "Maps" })
                {
                    string dir = System.IO.Path.Combine(_root, sub);
                    if (!Directory.Exists(dir)) continue;
                    foreach (string pat in new[] { "*.utx", "*.u", "*.unr" })
                        list.AddRange(Directory.GetFiles(dir, pat));
                }
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
            string sysDir = System.IO.Path.Combine(_root, "System");
            foreach (string pkgPath in Directory.GetFiles(sysDir, "*.u").OrderBy(x => System.IO.Path.GetFileName(x), StringComparer.Ordinal))
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
            if (tex.Format != UELib.Engine.UBitmapMaterial.TextureFormat.P8) { d.State = "NON_P8"; return d; }
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
            // (tools/origprobe/datadump_*: GenFX.Palette44 — A = 0xFF).
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
                var files = Directory.GetFiles(System.IO.Path.Combine(_root, "System"), "*.u")
                    .Concat(Directory.Exists(System.IO.Path.Combine(_root, "Textures")) ? Directory.GetFiles(System.IO.Path.Combine(_root, "Textures"), "*.utx") : Array.Empty<string>())
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
