// PROCEDURAL TEXTURE INPUTS FROM THE USER'S PACKAGES.
//
// FireTexture and WetTexture carry no pixels in the package (FractalTexture descendants): the frontend
// needs the Core simulation input (FireTexture / WaterTexture). The export bodies live here; exports
// serialize the Core result.
//
// Common export rules: packages Textures/System/Maps (*.utx, *.u, *.unr) in file-name order,
// descendant classes by the System/*.u tree, a package is opened with type UTexture for all Texture
// descendants, the first path wins.
//
// FireTexture SPARKS: `UFireTexture::Serialize` (Fire.dll.c:4776) calls `UTexture::Serialize` first,
// so the Sparks array starts RIGHT AFTER the mip array: end of the last mip =
// StorageOffset+StorageSize + USize(4) VSize(4) UBits(1) VBits(1); an element is 8 bytes, the array is
// prefixed by a compact length index. The length must match NumSparks from the tags (a different read
// path) — a mismatch means a wrong offset and counts as a record refusal.
//
// WetTexture: sizes, UWaterTexture parameters (UWaterTexture::Init defaults, Fire.dll.c:4936-4947:
// DropType 1, WaveAmp 128, FX_Frequency 8, FX_Phase 8, FX_Amplitude 255, FX_Speed 255, FX_Radius 128,
// FX_Size 30, FX_Depth 255, FX_Time 30), drops (NumDrops × 8 bytes), UTexture cadence, source —
// path, sizes, palette, mip0 (P8). For 14 of 25 carriers the source is package genfluid.utx, which is
// NOT shipped (SourceMissing); they are unused in the campaign.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UELib;
using UELib.Core;

namespace HP1.UEScript
{
    public sealed class UEProceduralTextureData
    {
        public sealed class Fire
        {
            public string Path, Name;
            public int USize, VSize, RenderHeat, SparksLimit, PrimeCount, FxFrequency;
            public float MinFrameRate, MaxFrameRate;
            public bool Rising;
            public int MipZeroR, MipZeroG, MipZeroB, SparkCount;
            public byte[] Sparks;     // SparkCount × 8
            public byte[] Palette;    // 256 × RGB
        }

        public sealed class Wet
        {
            public string Path;
            public int W, H, UBits, VBits;
            public int DropType, WaveAmp, FxFrequency, FxPhase, FxAmplitude, FxSpeed, FxRadius, FxSize, FxDepth, FxTime;
            public int NumDrops;
            public byte[] Drops;      // NumDrops × 8
            public float MinFrameRate, MaxFrameRate;
            public int PrimeCount;
            public string SourcePath;                // "" — source not shipped
            public int SourceW, SourceH, SourceUBits, SourceVBits;
            public byte[] SourcePalette;             // 256 × RGB or empty
            public byte[] SourceMip0;                // null — no source
            public string SourceMissing;             // "" if present; else the reference path (or "None")
        }

        // IceTexture (Fire.dll UIceTexture): export parameters; a missing tag gets the
        // UIceTexture::Init value (Fire.dll.c:6442-6449: PanningStyle 0, HorizPanSpeed/VertPanSpeed 0x80,
        // Frequency 0x0b, Amplitude 0x2c, UPosition/VPosition 0, MoveIce 1) — same policy as
        // WetTexture (UWaterTexture::Init); Min/MaxFrameRate, TimeMethod — 0, as for Wet. Defaulted —
        // names of fields not taken from a tag (printed by the consumer).
        // Source/glass per UIceTexture::PostLoad (Fire.dll.c:6286-6378): ice < 8 on either axis —
        // no source; a smaller source is stretched by shift (x>>(UBits_ice−UBits_src)), a source larger
        // than the ice is dropped; glass of another size is dropped. Ice palette = source palette
        // (+0x2c, :6366-6368). A stretched source or no usable glass makes the ice
        // STATIC (LocalSource, IceLocalSource below).
        // OPEN: UIceTexture::Init (like UWaterTexture::Init for Wet) is called only when a texture is
        // CREATED, not on load; on load a missing tag = the class value (default memory + constructor
        // 0x10509eb0, which does not write these fields). Whether that matches Init is measured locally
        // (HP1_PROCDBG=1: the "[Ice]" class and missing-tag log lines).
        public sealed class Ice
        {
            public string Path;
            public int USize, VSize;
            public int PanningStyle, Frequency, Amplitude, HorizPanSpeed, VertPanSpeed, TimeMethod, PrimeCount;
            public float UPosition, VPosition, MasterCount, MinFrameRate, MaxFrameRate;
            public bool MoveIce;
            public string SourcePath, GlassPath;     // references from tags ("" — none)
            public byte[] SourceP8;                  // USize×VSize after PostLoad; null — no source
            public byte[] SourcePalette;             // 256 × RGB of the source palette
            public byte[] GlassP8;                   // USize×VSize; null — no glass / dropped
            public string Dropped;                   // why PostLoad dropped source/glass ("" — not dropped)
            public bool LocalSource;                 // +0x114 != 0: the ice shows a static SourceP8 (IceLocalSource)
            public readonly List<string> Defaulted = new();
        }

        readonly string _root;
        List<string> _packages;
        Dictionary<string, string> _super;
        readonly Dictionary<string, UnrealPackage> _open = new(StringComparer.Ordinal);
        readonly HashSet<UObject> _deserialized = new();
        public readonly List<string> Problems = new();   // record and package refusals, one per line

        public UEProceduralTextureData(string installRoot)
        {
            _root = installRoot ?? throw new ArgumentNullException(nameof(installRoot));
        }

        List<string> Packages
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
                return _packages = list.Distinct(StringComparer.Ordinal)
                    .OrderBy(x => System.IO.Path.GetFileName(x), StringComparer.Ordinal).ToList();
            }
        }

        // Class inheritance table of System/*.u (export rule DerivedFromCore).
        Dictionary<string, string> Super
        {
            get
            {
                if (_super is not null) return _super;
                var super = new Dictionary<string, string>(StringComparer.Ordinal);
                string sysDir = System.IO.Path.Combine(_root, "System");
                if (!Directory.Exists(sysDir)) throw new InvalidDataException($"procedural textures: no directory {sysDir}");
                foreach (string pkgPath in Directory.GetFiles(sysDir, "*.u").OrderBy(x => System.IO.Path.GetFileName(x), StringComparer.Ordinal))
                {
                    var pkg = UnrealLoader.LoadPackage(pkgPath, FileAccess.Read);
                    pkg.InitializePackage(UnrealPackage.InitFlags.All);
                    foreach (var exp in pkg.Exports)
                    {
                        if (exp.Object is not UClass c) continue;
                        string sup = c.Super?.Name;
                        if (!string.IsNullOrEmpty(sup)) super[exp.ObjectName.ToString()] = sup;
                    }
                }
                return _super = super;
            }
        }

        HashSet<string> Derived(string root)
        {
            var super = Super;
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (string name in super.Keys)
            {
                string cur = name;
                for (int hops = 0; hops <= super.Count; hops++)
                {
                    if (cur == root) { result.Add(name); break; }
                    if (!super.TryGetValue(cur, out string next)) break;
                    cur = next;
                }
            }
            if (super.ContainsKey(root) || super.ContainsValue(root)) result.Add(root);
            return result;
        }

        UnrealPackage Open(string pkgPath, HashSet<string> textureClasses)
        {
            if (_open.TryGetValue(pkgPath, out var pkg)) return pkg;
            pkg = UnrealLoader.LoadPackage(pkgPath, FileAccess.Read);
            foreach (string tc in textureClasses) pkg.AddClassType(tc, typeof(UELib.Engine.UTexture));
            pkg.InitializePackage(UnrealPackage.InitFlags.All);
            _open[pkgPath] = pkg;
            return pkg;
        }

        UELib.Engine.UTexture Tex(UExportTableItem exp)
        {
            if (exp.Object is not UELib.Engine.UTexture tex) return null;
            if (_deserialized.Add(tex)) tex.BeginDeserializing();
            return tex;
        }

        // ── FireTexture ──────────────────────────────────────────────────────────

        public List<Fire> FireTextures()
        {
            HashSet<string> textureClasses = Derived("Texture");
            HashSet<string> fireClasses = Derived("FireTexture");
            if (fireClasses.Count == 0) throw new InvalidDataException("procedural textures: no FireTexture class in the class tree");
            var result = new List<Fire>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string pkgPath in Packages)
            {
                UnrealPackage pkg = Open(pkgPath, textureClasses);   // a package failure throws
                string pkgName = System.IO.Path.GetFileNameWithoutExtension(pkgPath);
                foreach (var exp in pkg.Exports)
                {
                    string cls = exp.ClassName?.ToString() ?? "Class";
                    if (!fireClasses.Contains(cls)) continue;
                    string path = UETextureSource.FullPathOf(pkgName, exp);
                    if (!seen.Add(path)) continue;
                    var tex = Tex(exp);
                    if (tex is null) continue;
                    if (tex.Mips == null || tex.Mips.Count == 0) continue;
                    if (tex.Palette == null) { Problems.Add($"FIRE NO PALETTE {path}"); continue; }
                    if (_deserialized.Add(tex.Palette)) tex.Palette.BeginDeserializing();
                    if (tex.Palette.Colors == null || tex.Palette.Colors.Count < 256) { Problems.Add($"FIRE NO PALETTE {path}"); continue; }

                    var last = tex.Mips[tex.Mips.Count - 1];
                    long pos = last.Data.StorageOffset + last.Data.StorageSize + 4 + 4 + 1 + 1;
                    int numSparks = PropInt(tex, "NumSparks", 0);
                    byte[] sparks = ReadSparks(pkgPath, pos, out int parsedCount);
                    if (sparks == null || parsedCount != numSparks)
                    {
                        Problems.Add($"FRACTAL MISMATCH {path}: NumSparks={numSparks}, {parsedCount} in the stream — the spark array offset is wrong");
                        continue;
                    }
                    var pal = new byte[768];
                    for (int i = 0; i < 256; i++)
                    {
                        var c = tex.Palette.Colors[i];
                        pal[i * 3] = c.R; pal[i * 3 + 1] = c.G; pal[i * 3 + 2] = c.B;
                    }
                    int dot = path.LastIndexOf('.');
                    result.Add(new Fire
                    {
                        Path = path, Name = dot >= 0 ? path.Substring(dot + 1) : path,
                        USize = PropInt(tex, "USize", 0), VSize = PropInt(tex, "VSize", 0),
                        RenderHeat = PropInt(tex, "RenderHeat", 0), SparksLimit = PropInt(tex, "SparksLimit", 0),
                        PrimeCount = PropInt(tex, "PrimeCount", 0),
                        // FX_Frequency (+0xe4) is the only FX byte RedrawSparks reads
                        // (AuxPhase += FX_Frequency, 0x105025ea); without a tag — class default 0.
                        FxFrequency = PropInt(tex, "FX_Frequency", 0),
                        MinFrameRate = PropFloat(tex, "MinFrameRate", 0f), MaxFrameRate = PropFloat(tex, "MaxFrameRate", 0f),
                        Rising = PropBool(tex, "bRising"),
                        MipZeroR = PropColour(tex, "MipZero", 0), MipZeroG = PropColour(tex, "MipZero", 1), MipZeroB = PropColour(tex, "MipZero", 2),
                        SparkCount = parsedCount, Sparks = sparks, Palette = pal,
                    });
                }
            }
            return result;
        }

        // ── WetTexture ───────────────────────────────────────────────────────────

        public List<Wet> WetTextures()
        {
            HashSet<string> textureClasses = Derived("Texture");
            HashSet<string> wetClasses = Derived("WetTexture");
            if (wetClasses.Count == 0) throw new InvalidDataException("procedural textures: no WetTexture class in the class tree");
            // index of all textures by full path (WetTexture sources live in other packages)
            var index = new Dictionary<string, (string pkgPath, int expIdx)>(StringComparer.OrdinalIgnoreCase);
            var wets = new List<(string pkgPath, int expIdx)>();
            foreach (string pp in Packages)
            {
                UnrealPackage pkg;
                try { pkg = Open(pp, textureClasses); }
                catch (Exception e) { Problems.Add($"SKIP {System.IO.Path.GetFileName(pp)}: {e.Message}"); continue; }
                string pn = System.IO.Path.GetFileNameWithoutExtension(pp);
                for (int i = 0; i < pkg.Exports.Count; i++)
                {
                    var exp = pkg.Exports[i];
                    string cls = exp.ClassName?.ToString() ?? "Class";
                    if (!textureClasses.Contains(cls)) continue;
                    string path = UETextureSource.FullPathOf(pn, exp);
                    if (!index.ContainsKey(path)) index[path] = (pp, i);
                    if (wetClasses.Contains(cls)) wets.Add((pp, i));
                }
            }
            var result = new List<Wet>();
            foreach (var (pp, i) in wets)
            {
                UnrealPackage pkg = _open[pp];
                string pn = System.IO.Path.GetFileNameWithoutExtension(pp);
                var tex = Tex(pkg.Exports[i]);
                if (tex == null || tex.Mips == null || tex.Mips.Count == 0) continue;
                string path = UETextureSource.FullPathOf(pn, pkg.Exports[i]);
                int w = tex.Mips[0].USize, h = tex.Mips[0].VSize;
                int numDrops = PropInt(tex, "NumDrops", 0);
                var drops = new byte[numDrops * 8];
                for (int k = 0; k < numDrops; k++)
                {
                    byte[] r = Raw(tex, "Drops", k);
                    if (r == null || r.Length != 8)
                        throw new InvalidDataException($"WET DROPS {path}: Drops[{k}] missing from the tags with NumDrops={numDrops}");
                    Array.Copy(r, 0, drops, k * 8, 8);
                }
                string src = null;
                foreach (var kv in tex.GetRawPropertyValueBytesAll("SourceTexture")) { src = RefPath(pkg, pn, Cidx(kv.Value)); break; }
                var rec = new Wet
                {
                    Path = path, W = w, H = h, UBits = Log2(w), VBits = Log2(h),
                    DropType = PropInt(tex, "DropType", 1), WaveAmp = PropInt(tex, "WaveAmp", 128),
                    FxFrequency = PropInt(tex, "FX_Frequency", 8), FxPhase = PropInt(tex, "FX_Phase", 8),
                    FxAmplitude = PropInt(tex, "FX_Amplitude", 255), FxSpeed = PropInt(tex, "FX_Speed", 255),
                    FxRadius = PropInt(tex, "FX_Radius", 128), FxSize = PropInt(tex, "FX_Size", 30),
                    FxDepth = PropInt(tex, "FX_Depth", 255), FxTime = PropInt(tex, "FX_Time", 30),
                    NumDrops = numDrops, Drops = drops,
                    MinFrameRate = PropFloat(tex, "MinFrameRate", 0f), MaxFrameRate = PropFloat(tex, "MaxFrameRate", 0f),
                    PrimeCount = PropInt(tex, "PrimeCount", 0),
                };
                if (src != null && index.TryGetValue(src, out var si))
                {
                    var stex = Tex(_open[si.pkgPath].Exports[si.expIdx]);
                    if (stex?.Mips == null || stex.Mips.Count == 0 || stex.Palette == null)
                        throw new InvalidDataException($"WET SOURCE {path}: source {src} has no mips/palette");
                    if (_deserialized.Add(stex.Palette)) stex.Palette.BeginDeserializing();
                    var m0 = stex.Mips[0];
                    byte[] pix = FileBytes(si.pkgPath, m0.Data.StorageOffset, m0.Data.StorageSize);
                    if (pix.Length != m0.USize * m0.VSize)
                        throw new InvalidDataException($"WET SOURCE {path}: mip0 {src} {pix.Length} != {m0.USize}x{m0.VSize}");
                    var pal = new byte[768];
                    for (int k = 0; k < 256; k++) { var c = stex.Palette.Colors[k]; pal[k * 3] = c.R; pal[k * 3 + 1] = c.G; pal[k * 3 + 2] = c.B; }
                    rec.SourcePath = src; rec.SourceW = m0.USize; rec.SourceH = m0.VSize;
                    rec.SourceUBits = Log2(m0.USize); rec.SourceVBits = Log2(m0.VSize);
                    rec.SourcePalette = pal; rec.SourceMip0 = pix; rec.SourceMissing = "";
                }
                else
                {
                    rec.SourcePath = ""; rec.SourcePalette = Array.Empty<byte>(); rec.SourceMip0 = null;
                    rec.SourceMissing = src ?? "None";
                }
                result.Add(rec);
            }
            return result;
        }

        // UIceTexture::PostLoad (disasm 0x10509fca-0x1050a0bb): with a source of a DIFFERENT size than
        // the ice (0x10509fca-0x10509fe3) or without usable glass (no glass, or glass of another size
        // that was dropped, 0x10509fea-0x1050a00c) the resampled source is written into the ice's own mip 0
        // and +0x114 = its address (0x1050a058-0x1050a061); BlitTexIce/BlitIceTex exit immediately when
        // +0x114 != 0 (0x10505ec4, 0x10506256). A source larger than the ice was dropped earlier
        // (0x1050a052 -> 0x1050a0bb) and never gets here. Otherwise +0x114 = 0 (0x1050a023), ice animates.
        public static bool IceLocalSource(int iceW, int iceH, int srcW, int srcH, bool glassUsable)
            => srcW != iceW || srcH != iceH || !glassUsable;

        public List<Ice> IceTextures()
        {
            HashSet<string> textureClasses = Derived("Texture");
            HashSet<string> iceClasses = Derived("IceTexture");
            if (iceClasses.Count == 0) throw new InvalidDataException("procedural textures: no IceTexture class in the class tree");
            var index = new Dictionary<string, (string pkgPath, int expIdx)>(StringComparer.OrdinalIgnoreCase);
            var ices = new List<(string pkgPath, int expIdx)>();
            foreach (string pp in Packages)
            {
                UnrealPackage pkg;
                try { pkg = Open(pp, textureClasses); }
                catch (Exception e) { Problems.Add($"SKIP {System.IO.Path.GetFileName(pp)}: {e.Message}"); continue; }
                string pn = System.IO.Path.GetFileNameWithoutExtension(pp);
                for (int i = 0; i < pkg.Exports.Count; i++)
                {
                    var exp = pkg.Exports[i];
                    string cls = exp.ClassName?.ToString() ?? "Class";
                    if (!textureClasses.Contains(cls)) continue;
                    string path = UETextureSource.FullPathOf(pn, exp);
                    if (!index.ContainsKey(path)) index[path] = (pp, i);
                    if (iceClasses.Contains(cls)) ices.Add((pp, i));
                }
            }
            (int w, int h, int ub, int vb, byte[] p8, byte[] pal) LoadP8(string refPath, string owner, string role)
            {
                if (!index.TryGetValue(refPath, out var si)) { Problems.Add($"ICE {owner}: {role} {refPath} not found in the packages"); return default; }
                var t = Tex(_open[si.pkgPath].Exports[si.expIdx]);
                if (t?.Mips == null || t.Mips.Count == 0 || t.Palette == null) { Problems.Add($"ICE {owner}: {role} {refPath} has no mips/palette"); return default; }
                if (_deserialized.Add(t.Palette)) t.Palette.BeginDeserializing();
                var m0 = t.Mips[0];
                byte[] pix = FileBytes(si.pkgPath, m0.Data.StorageOffset, m0.Data.StorageSize);
                if (pix.Length != m0.USize * m0.VSize) throw new InvalidDataException($"ICE {owner}: mip0 {refPath} {pix.Length} != {m0.USize}x{m0.VSize}");
                var pal = new byte[768];
                for (int k = 0; k < 256; k++) { var c = t.Palette.Colors[k]; pal[k * 3] = c.R; pal[k * 3 + 1] = c.G; pal[k * 3 + 2] = c.B; }
                return (m0.USize, m0.VSize, Log2(m0.USize), Log2(m0.VSize), pix, pal);
            }
            var result = new List<Ice>();
            foreach (var (pp, i) in ices)
            {
                UnrealPackage pkg = _open[pp];
                string pn = System.IO.Path.GetFileNameWithoutExtension(pp);
                var tex = Tex(pkg.Exports[i]);
                if (tex == null || tex.Mips == null || tex.Mips.Count == 0) continue;
                string path = UETextureSource.FullPathOf(pn, pkg.Exports[i]);
                var rec = new Ice { Path = path, USize = tex.Mips[0].USize, VSize = tex.Mips[0].VSize, Dropped = "" };
                int I(string n, int init) { if (PropValue(tex, n) is { Length: > 0 }) return PropInt(tex, n, init); rec.Defaulted.Add(n); return init; }
                float F(string n, float init) { if (PropValue(tex, n) is { Length: > 0 }) return PropFloat(tex, n, init); rec.Defaulted.Add(n); return init; }
                rec.PanningStyle = I("PanningStyle", 0); rec.HorizPanSpeed = I("HorizPanSpeed", 0x80); rec.VertPanSpeed = I("VertPanSpeed", 0x80);
                rec.Frequency = I("Frequency", 0x0b); rec.Amplitude = I("Amplitude", 0x2c);
                rec.UPosition = F("UPosition", 0f); rec.VPosition = F("VPosition", 0f); rec.MasterCount = F("MasterCount", 0f);
                rec.TimeMethod = I("TimeMethod", 0); rec.MinFrameRate = F("MinFrameRate", 0f); rec.MaxFrameRate = F("MaxFrameRate", 0f);
                rec.PrimeCount = I("PrimeCount", 0);
                if (PropValue(tex, "MoveIce") is { Length: > 0 }) rec.MoveIce = PropBool(tex, "MoveIce"); else { rec.MoveIce = true; rec.Defaulted.Add("MoveIce"); }
                rec.SourcePath = ""; rec.GlassPath = "";
                foreach (var kv in tex.GetRawPropertyValueBytesAll("SourceTexture")) { rec.SourcePath = RefPath(pkg, pn, Cidx(kv.Value)) ?? ""; break; }
                foreach (var kv in tex.GetRawPropertyValueBytesAll("GlassTexture")) { rec.GlassPath = RefPath(pkg, pn, Cidx(kv.Value)) ?? ""; break; }
                int iub = Log2(rec.USize), ivb = Log2(rec.VSize), srcW = 0, srcH = 0;
                if (rec.SourcePath.Length > 0)
                {
                    if (rec.USize < 8 || rec.VSize < 8) rec.Dropped += "source: ice smaller than 8 (PostLoad :6327-6330); ";
                    else
                    {
                        var src = LoadP8(rec.SourcePath, path, "source");
                        srcW = src.w; srcH = src.h;
                        if (src.p8 != null)
                        {
                            int du = iub - src.ub, dv = ivb - src.vb;
                            if (du < 0 || dv < 0) rec.Dropped += $"source {src.w}x{src.h} larger than the ice (PostLoad :6346-6348); ";
                            else
                            {
                                // PostLoad :6352-6361: dst[y*USize+x] = src[(x>>du) + ((y>>dv) << src.UBits)].
                                var p8 = new byte[rec.USize * rec.VSize];
                                for (int y = 0; y < rec.VSize; y++)
                                    for (int x = 0; x < rec.USize; x++)
                                        p8[y * rec.USize + x] = src.p8[(x >> du) + ((y >> dv) << src.ub)];
                                rec.SourceP8 = p8; rec.SourcePalette = src.pal;
                            }
                        }
                    }
                }
                if (rec.GlassPath.Length > 0 && rec.SourceP8 != null)
                {
                    var gl = LoadP8(rec.GlassPath, path, "glass");
                    if (gl.p8 != null)
                    {
                        if (gl.w != rec.USize || gl.h != rec.VSize) rec.Dropped += $"glass {gl.w}x{gl.h} != {rec.USize}x{rec.VSize} (PostLoad :6337-6343); ";
                        else rec.GlassP8 = gl.p8;
                    }
                }
                rec.LocalSource = rec.SourceP8 != null && IceLocalSource(rec.USize, rec.VSize, srcW, srcH, rec.GlassP8 != null);
                result.Add(rec);
            }
            return result;
        }

        // ── Reading tags and bytes (export rules) ────────────────────────────

        static string PropValue(UObject o, string name)
        {
            if (o.Properties == null) return null;
            foreach (var p in o.Properties)
                if (string.Equals(p?.Name.ToString(), name, StringComparison.Ordinal))
                { try { return p.Value; } catch { return null; } }
            return null;
        }
        static int PropInt(UObject o, string name, int fallback)
            => int.TryParse(PropValue(o, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;
        static float PropFloat(UObject o, string name, float fallback)
            => float.TryParse(PropValue(o, name), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;
        static bool PropBool(UObject o, string name)
            => string.Equals(PropValue(o, name), "true", StringComparison.OrdinalIgnoreCase);
        // MipZero is an FColor; UELib returns it as a string "(R=..,G=..,B=..,A=..)".
        static int PropColour(UObject o, string name, int component)
        {
            string v = PropValue(o, name);
            if (string.IsNullOrEmpty(v)) return 0;
            char key = component == 0 ? 'R' : component == 1 ? 'G' : 'B';
            int i = v.IndexOf(key + "=", StringComparison.Ordinal);
            if (i < 0) return 0;
            i += 2;
            int j = i;
            while (j < v.Length && (char.IsDigit(v[j]) || v[j] == '-')) j++;
            return int.TryParse(v.Substring(i, j - i), out int n) ? n : 0;
        }
        static byte[] Raw(UObject o, string name, int arrayIndex)
        {
            foreach (var kv in o.GetRawPropertyValueBytesAll(name)) if (kv.Key == arrayIndex || (arrayIndex == 0 && kv.Key < 0)) return kv.Value;
            return null;
        }
        static int Log2(int v) { int b = 0; while ((1 << b) < v) b++; return b; }
        static int Cidx(byte[] b)
        {
            int p = 0; int b0 = b[p++]; bool neg = (b0 & 0x80) != 0; int v = b0 & 0x3f;
            if ((b0 & 0x40) != 0) { int sh = 6; for (int i = 0; i < 4; i++) { int bb = b[p++]; v |= (bb & 0x7f) << sh; sh += 7; if ((bb & 0x80) == 0) break; } }
            return neg ? -v : v;
        }
        static string RefPath(UnrealPackage pkg, string pkgName, int idx)
        {
            if (idx > 0) return UETextureSource.FullPathOf(pkgName, pkg.Exports[idx - 1]);
            if (idx < 0) { var chain = new List<string>(); for (UObjectTableItem o = pkg.Imports[-idx - 1]; o != null; o = o.Outer) chain.Add(o.ObjectName.ToString()); chain.Reverse(); return string.Join(".", chain); }
            return null;
        }
        static byte[] FileBytes(string path, long off, long size)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            fs.Position = off; var b = new byte[size]; int rd = 0;
            while (rd < b.Length) { int n = fs.Read(b, rd, b.Length - rd); if (n <= 0) break; rd += n; }
            return b;
        }
        static int ReadCompactIndex(Stream s)
        {
            int b0 = s.ReadByte();
            if (b0 < 0) return -1;
            bool neg = (b0 & 0x80) != 0;
            int value = b0 & 0x3F;
            if ((b0 & 0x40) != 0)
            {
                int shift = 6;
                for (int i = 0; i < 4; i++)
                {
                    int bn = s.ReadByte();
                    if (bn < 0) return -1;
                    value |= (bn & 0x7F) << shift;
                    shift += 7;
                    if ((bn & 0x80) == 0) break;
                }
            }
            return neg ? -value : value;
        }
        static byte[] ReadSparks(string pkgPath, long pos, out int count)
        {
            count = -1;
            using var fs = new FileStream(pkgPath, FileMode.Open, FileAccess.Read);
            if (pos < 0 || pos >= fs.Length) return null;
            fs.Position = pos;
            count = ReadCompactIndex(fs);
            if (count < 0 || count > 1_000_000) return null;
            long need = (long)count * 8;
            if (fs.Position + need > fs.Length) return null;
            var buf = new byte[need];
            int read = 0;
            while (read < buf.Length)
            {
                int n = fs.Read(buf, read, buf.Length - read);
                if (n <= 0) break;
                read += n;
            }
            return read == buf.Length ? buf : null;
        }
    }
}
