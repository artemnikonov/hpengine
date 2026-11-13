// TEXTURE METRICS FROM THE USER'S PACKAGES.
//
// A surface's diffuse UV is normalized by (DrawScale · texture size), and the render state mixes in the texture's own
// flags. The validation tooling serializes this Core result as a table.
//
// Exporter rules: packages Textures/System/Maps (*.utx, *.u, *.unr) in file-name order (ordinal); a texture object is
// a class whose name ends in "Texture"; the first path wins. Values are UELib tag strings; a missing tag is "-" (the
// class default; an explicit zero is distinct from it).
//
// Lookup by path (Find/TryGet) reads only packages named by the path's first segment, in the same order — over the
// campaign this equals the table: no shipped paths differ only by case.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UELib;
using UELib.Core;

namespace HP1.UEScript
{
    public sealed class UETextureMetrics
    {
        public static readonly string[] Wanted =
        {
            "USize", "VSize", "DrawScale",
            "bInvisible", "bMasked", "bTransparent", "bModulate", "bTwoSided", "bUnlit",
        };
        // PolyFlags bits in the order of the bool fields Wanted[3..8] (UTexture.h:247-265 → PF_*).
        static readonly int[] FlagBits = { 0x1, 0x2, 0x4, 0x40, 0x100, 0x400000 };
        // bNoSmooth (12th Texture bool in Engine.u -> PF_NoSmooth 0x800; D3DDrv.dll.c:860-865 selects the POINT
        // filter by it) is NOT read here — no effect on shipped data (UELib over Textures/System/Maps: 3733 textures,
        // 0 bNoSmooth tags; Texture and Fire.u subclass defaults do not set it; scripts write only
        // Actor/Canvas.bNoSmooth). If a carrier appears, move it into FlagBits.

        public sealed class Row
        {
            public string Path, Class;
            public string[] Values;    // per Wanted; "-" — no tag
            public string Error;       // object parse failure (the export writes "<error:…>" in USize)
        }

        readonly string _root;
        List<string> _packages;
        readonly Dictionary<string, UnrealPackage> _open = new(StringComparer.Ordinal);
        readonly Dictionary<string, Row> _byPath = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> SkippedPackages = new();

        public UETextureMetrics(string installRoot)
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
                return _packages;
            }
        }

        UnrealPackage Open(string pkgPath)
        {
            if (_open.TryGetValue(pkgPath, out var pkg)) return pkg;
            try
            {
                pkg = UnrealLoader.LoadPackage(pkgPath, FileAccess.Read);
                pkg.InitializePackage(UnrealPackage.InitFlags.All);
            }
            catch (Exception e)
            {
                // A skipped package narrows coverage — it is declared, not silently lost.
                SkippedPackages.Add($"{System.IO.Path.GetFileName(pkgPath)}: {e.Message}");
                pkg = null;
            }
            _open[pkgPath] = pkg;
            return pkg;
        }

        static bool IsTexture(UExportTableItem exp)
            => (exp.ClassName?.ToString() ?? "Class").EndsWith("Texture", StringComparison.Ordinal);

        // All shipped textures in export order (the first path wins).
        public IEnumerable<Row> All()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string pkgPath in Packages)
            {
                UnrealPackage pkg = Open(pkgPath);
                if (pkg is null) continue;
                string pkgName = System.IO.Path.GetFileNameWithoutExtension(pkgPath);
                foreach (var exp in pkg.Exports)
                {
                    if (!IsTexture(exp)) continue;
                    string path = UETextureSource.FullPathOf(pkgName, exp);
                    if (!seen.Add(path)) continue;
                    yield return RowOf(path, exp);
                }
            }
        }

        // Texture row by full UE path; null — no installation package has the path.
        public Row Find(string uePath)
        {
            if (string.IsNullOrEmpty(uePath)) return null;
            if (_byPath.TryGetValue(uePath, out Row hit)) return hit;
            int dot = uePath.IndexOf('.');
            string pkgName = dot > 0 ? uePath.Substring(0, dot) : uePath;
            Row found = null;
            foreach (string pkgPath in Packages)
            {
                if (!string.Equals(System.IO.Path.GetFileNameWithoutExtension(pkgPath), pkgName, StringComparison.OrdinalIgnoreCase)) continue;
                UnrealPackage pkg = Open(pkgPath);
                if (pkg is null) continue;
                string name = System.IO.Path.GetFileNameWithoutExtension(pkgPath);
                foreach (var exp in pkg.Exports)
                {
                    if (!IsTexture(exp)) continue;
                    string path = UETextureSource.FullPathOf(name, exp);
                    if (!string.Equals(path, uePath, StringComparison.OrdinalIgnoreCase)) continue;
                    found = RowOf(path, exp);
                    break;
                }
                if (found is not null) break;
            }
            _byPath[uePath] = found;
            return found;
        }

        static Row RowOf(string path, UExportTableItem exp)
        {
            var row = new Row { Path = path, Class = exp.ClassName?.ToString() ?? "Class", Values = new string[Wanted.Length] };
            for (int i = 0; i < row.Values.Length; i++) row.Values[i] = "-";
            try
            {
                if (exp.Object is UObject obj)
                {
                    obj.BeginDeserializing();
                    if (obj.Properties != null)
                        foreach (var dp in obj.Properties)
                        {
                            string name = dp?.Name.ToString();
                            int k = name == null ? -1 : Array.IndexOf(Wanted, name);
                            if (k < 0) continue;
                            string v;
                            try { v = dp.Value; } catch { v = "<decode-error>"; }
                            row.Values[k] = v;
                        }
                }
            }
            catch (Exception e)
            {
                row.Values[0] = "<error:" + e.GetType().Name + ">";
                row.Error = e.GetType().Name;
            }
            return row;
        }

        // Table row in export format: path, class, then Wanted.
        public static string Format(Row r)
        {
            var sb = new StringBuilder(r.Path).Append('\t').Append(r.Class);
            foreach (string v in r.Values) sb.Append('\t').Append(v);
            return sb.ToString();
        }

        // Metrics for the static-world consumer. Rule: a non-numeric value is 0 for a size and 1 for DrawScale; a flag
        // is set only on "true"; valid means both sizes > 0 and DrawScale ≠ 0.
        public bool TryGet(string uePath, out float uSize, out float vSize, out float drawScale, out int texPolyFlags)
        {
            uSize = vSize = drawScale = 0f;
            texPolyFlags = 0;
            Row r = Find(uePath);
            if (r is null) return false;
            uSize = ParseF(r.Values[0], 0f);
            vSize = ParseF(r.Values[1], 0f);
            drawScale = ParseF(r.Values[2], 1f);
            for (int i = 0; i < FlagBits.Length; i++)
                if (r.Values[3 + i] == "true") texPolyFlags |= FlagBits[i];
            return uSize > 0f && vSize > 0f && drawScale != 0f;
        }

        static float ParseF(string s, float fallback)
            => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;
    }
}
