// OS fonts of UConsole.CreateNativeFont (RU edition) — NOT ORIGINAL substitution (author decision 2026-11-03,
// HP1.Core/Fonts/README.md). The original creates a transient UFont with only a face name (+0x50) and a height (+0x5c)
// (WinDrv UWindowsViewport::CreateNativeFont: CreateFont(-Height, 0,0,0, 400, 0,0,0, charset, 0,0,
// NONANTIALIASED_QUALITY, 0, Face)), and UCanvas::WrappedPrint hands such a font to UViewport::DrawString, which
// measures and draws with GDI DrawText (DT_WORDBREAK). The port has no GDI: "MS Sans Serif" is served by the bundled
// Liberation Sans, rasterised here (unhinted outlines, aliased like NONANTIALIASED_QUALITY) into one glyph page that the
// frontend draws as a runtime texture. Glyph shapes, advances, line height and therefore wrap points differ from the
// Windows bitmap font sserife.fon. Any other face refuses loudly.
using System;
using System.Collections.Generic;
using System.IO;
using HP1.Core;

namespace HP1.UEScript
{
    // The minimum of a TrueType file the rasteriser needs: cmap (3,1) format 4, hmtx advances, glyf outlines
    // (simple and composite), head/hhea/OS/2 metrics.
    public sealed class UETrueType
    {
        readonly byte[] _b;
        readonly int _glyf, _loca, _hmtx, _cmap4, _numHMetrics;
        readonly bool _longLoca;
        public readonly int UnitsPerEm, WinAscent, WinDescent;

        public UETrueType(byte[] ttf)
        {
            _b = ttf;
            int n = U16(4);
            var tables = new Dictionary<string, int>();
            for (int i = 0; i < n; i++)
            {
                int r = 12 + 16 * i;
                tables[System.Text.Encoding.ASCII.GetString(ttf, r, 4)] = (int)U32(r + 8);
            }
            int T(string tag) => tables.TryGetValue(tag, out int o) ? o : throw new InvalidDataException($"TrueType: no '{tag}' table");
            int head = T("head");
            UnitsPerEm = U16(head + 18);
            _longLoca = S16(head + 50) == 1;
            _numHMetrics = U16(T("hhea") + 34);
            int os2 = T("OS/2");
            WinAscent = U16(os2 + 74); WinDescent = U16(os2 + 76);
            _glyf = T("glyf"); _loca = T("loca"); _hmtx = T("hmtx");
            int cmap = T("cmap");
            _cmap4 = -1;
            for (int i = 0, m = U16(cmap + 2); i < m; i++)
            {
                int r = cmap + 4 + 8 * i;
                if (U16(r) == 3 && U16(r + 2) == 1 && U16(cmap + (int)U32(r + 4)) == 4) { _cmap4 = cmap + (int)U32(r + 4); break; }
            }
            if (_cmap4 < 0) throw new InvalidDataException("TrueType: no (3,1) format 4 cmap");
        }

        int U16(int p) => (_b[p] << 8) | _b[p + 1];
        int S16(int p) => (short)U16(p);
        uint U32(int p) => (uint)((_b[p] << 24) | (_b[p + 1] << 16) | (_b[p + 2] << 8) | _b[p + 3]);

        // 0 = .notdef (no glyph for the character).
        public int GlyphIndex(int c)
        {
            int segX2 = U16(_cmap4 + 6), ends = _cmap4 + 14, starts = ends + segX2 + 2, deltas = starts + segX2, ranges = deltas + segX2;
            for (int s = 0; s < segX2; s += 2)
            {
                if (c > U16(ends + s)) continue;
                if (c < U16(starts + s)) return 0;
                int ro = U16(ranges + s);
                if (ro == 0) return (c + U16(deltas + s)) & 0xFFFF;
                int g = U16(ranges + s + ro + 2 * (c - U16(starts + s)));
                return g == 0 ? 0 : (g + U16(deltas + s)) & 0xFFFF;
            }
            return 0;
        }

        public int Advance(int g) => U16(_hmtx + 4 * Math.Min(g, _numHMetrics - 1));

        // Contours of (x, y, onCurve) in font units, y up.
        public List<List<(double X, double Y, bool On)>> Outline(int g)
        {
            var result = new List<List<(double, double, bool)>>();
            AddOutline(g, 1, 0, 0, 1, 0, 0, result, 0);
            return result;
        }

        void AddOutline(int g, double a, double b, double c, double d, double dx, double dy,
                        List<List<(double, double, bool)>> into, int depth)
        {
            if (depth > 8) throw new InvalidDataException("TrueType: composite glyph nesting too deep");
            int start = _longLoca ? (int)U32(_loca + 4 * g) : 2 * U16(_loca + 2 * g);
            int end = _longLoca ? (int)U32(_loca + 4 * g + 4) : 2 * U16(_loca + 2 * g + 2);
            if (end <= start) return;                                   // empty glyph (space)
            int p = _glyf + start;
            int contours = S16(p);
            if (contours < 0)
            {
                int q = p + 10;
                while (true)
                {
                    int flags = U16(q), sub = U16(q + 2); q += 4;
                    double ex, ey;
                    if ((flags & 1) != 0) { ex = S16(q); ey = S16(q + 2); q += 4; }
                    else { ex = (sbyte)_b[q]; ey = (sbyte)_b[q + 1]; q += 2; }
                    if ((flags & 2) == 0) throw new NotSupportedException("TrueType: composite point matching not supported");
                    double sa = 1, sb = 0, sc = 0, sd = 1;
                    if ((flags & 8) != 0) { sa = sd = S16(q) / 16384.0; q += 2; }
                    else if ((flags & 0x40) != 0) { sa = S16(q) / 16384.0; sd = S16(q + 2) / 16384.0; q += 4; }
                    else if ((flags & 0x80) != 0) { sa = S16(q) / 16384.0; sb = S16(q + 2) / 16384.0; sc = S16(q + 4) / 16384.0; sd = S16(q + 6) / 16384.0; q += 8; }
                    // child point (x, y) -> parent: (sa·x + sc·y + ex, sb·x + sd·y + ey), then this glyph's transform.
                    AddOutline(sub, a * sa + c * sb, b * sa + d * sb, a * sc + c * sd, b * sc + d * sd,
                        a * ex + c * ey + dx, b * ex + d * ey + dy, into, depth + 1);
                    if ((flags & 0x20) == 0) break;
                }
                return;
            }
            int endPts = p + 10;
            int nPts = U16(endPts + 2 * (contours - 1)) + 1;
            int ins = endPts + 2 * contours;
            int f = ins + 2 + U16(ins);
            var fl = new byte[nPts];
            for (int i = 0; i < nPts;)
            {
                byte v = _b[f++];
                fl[i++] = v;
                if ((v & 8) != 0) for (int r = _b[f++]; r > 0; r--) fl[i++] = v;
            }
            var xs = new int[nPts]; var ys = new int[nPts];
            int acc = 0;
            for (int i = 0; i < nPts; i++)
            {
                if ((fl[i] & 2) != 0) { acc += (fl[i] & 0x10) != 0 ? _b[f] : -_b[f]; f++; }
                else if ((fl[i] & 0x10) == 0) { acc += S16(f); f += 2; }
                xs[i] = acc;
            }
            acc = 0;
            for (int i = 0; i < nPts; i++)
            {
                if ((fl[i] & 4) != 0) { acc += (fl[i] & 0x20) != 0 ? _b[f] : -_b[f]; f++; }
                else if ((fl[i] & 0x20) == 0) { acc += S16(f); f += 2; }
                ys[i] = acc;
            }
            int first = 0;
            for (int k = 0; k < contours; k++)
            {
                int last = U16(endPts + 2 * k);
                var contour = new List<(double, double, bool)>();
                for (int i = first; i <= last; i++)
                    contour.Add((a * xs[i] + c * ys[i] + dx, b * xs[i] + d * ys[i] + dy, (fl[i] & 1) != 0));
                if (contour.Count > 0) into.Add(contour);
                first = last + 1;
            }
        }
    }

    public readonly struct UENativeGlyph
    {
        public readonly int U, V, W, H, OffX, OffY, Advance;   // OffY from the line top
        public UENativeGlyph(int u, int v, int w, int h, int ox, int oy, int adv)
        { U = u; V = v; W = w; H = h; OffX = ox; OffY = oy; Advance = adv; }
    }

    public sealed class UENativeFont
    {
        public const string SubstitutedFace = "MS Sans Serif";
        const int PageWidth = 512;

        public readonly string Name, Face, PagePath;
        public readonly int Height, Ascent, LineHeight;
        readonly Dictionary<int, UENativeGlyph> _glyphs = new();
        readonly UENativeGlyph _notdef;

        // The characters rasterised into the page: Latin-1, Latin Extended-A, Cyrillic, general punctuation, № and ™.
        static IEnumerable<int> Repertoire()
        {
            for (int c = 0x20; c < 0x180; c++) yield return c;
            for (int c = 0x400; c < 0x460; c++) yield return c;
            for (int c = 0x2010; c < 0x2040; c++) yield return c;
            yield return 0x2116; yield return 0x2122;
        }

        internal UENativeFont(string name, string face, int height, UETrueType tt)
        {
            Name = name; Face = face; Height = height;
            PagePath = "Transient." + name + "Page";
            double s = (double)height / tt.UnitsPerEm;
            Ascent = (int)Math.Round(tt.WinAscent * s);
            LineHeight = Ascent + (int)Math.Round(tt.WinDescent * s);
            var bitmaps = new List<(int Key, bool[] Px, int W, int H, int Ox, int Oy, int Adv)>();
            void Add(int key, int g)
            {
                var (px, w, h, ox, oy) = Rasterize(tt.Outline(g), s);
                bitmaps.Add((key, px, w, h, ox, oy + Ascent, (int)Math.Round(tt.Advance(g) * s)));
            }
            Add(-1, 0);
            foreach (int c in Repertoire())
            {
                int g = tt.GlyphIndex(c);
                if (g != 0) Add(c, g);
            }
            // Shelf packing in repertoire order, 1 texel gap.
            int x = 0, y = 0, shelf = 0;
            var placed = new (int U, int V)[bitmaps.Count];
            for (int i = 0; i < bitmaps.Count; i++)
            {
                var bm = bitmaps[i];
                if (x + bm.W > PageWidth) { x = 0; y += shelf + 1; shelf = 0; }
                placed[i] = (x, y);
                x += bm.W + 1; shelf = Math.Max(shelf, bm.H);
            }
            int pageH = 1;
            while (pageH < y + shelf) pageH <<= 1;
            var idx = new byte[PageWidth * pageH];
            for (int i = 0; i < bitmaps.Count; i++)
            {
                var bm = bitmaps[i];
                for (int r = 0; r < bm.H; r++)
                    for (int c = 0; c < bm.W; c++)
                        if (bm.Px[r * bm.W + c]) idx[(placed[i].V + r) * PageWidth + placed[i].U + c] = 1;
                var gl = new UENativeGlyph(placed[i].U, placed[i].V, bm.W, bm.H, bm.Ox, bm.Oy, bm.Adv);
                if (bm.Key < 0) _notdef = gl; else _glyphs[bm.Key] = gl;
            }
            // Index 1 white, 0 black: the frontend's glyph rule takes alpha = max(R, G, B) of the palette entry.
            var pal = new byte[256 * 4];
            pal[3] = 255; pal[4] = pal[5] = pal[6] = pal[7] = 255;
            UERuntimeTextures.Publish(PagePath, PageWidth, pageH, idx, pal, masked: false);
        }

        // A character without a glyph draws the font's .notdef box, as GDI does with the default character.
        public UENativeGlyph Glyph(char c) => _glyphs.TryGetValue(c, out var g) ? g : _notdef;

        public int Width(string s)
        {
            int w = 0;
            foreach (char c in s) w += Glyph(c).Advance;
            return w;
        }

        // DrawText line breaking with DT_WORDBREAK and without DT_NOPREFIX (formats 0x410 / 0x10 / 0x11 of WinDrv
        // DrawString): '&' prefixes are removed ("&&" -> "&"; the underline of the mnemonic is not drawn — ponytail:
        // no shipped RU string measured for it), CR/LF break lines, words wrap greedily at spaces within `width`, the
        // breaking space is not part of either line, and a word wider than the rectangle stays whole on its own line.
        public List<string> Lines(string text, int width)
        {
            var plain = new System.Text.StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '&') { if (i + 1 < text.Length && text[i + 1] == '&') { plain.Append('&'); i++; } continue; }
                plain.Append(c);
            }
            var lines = new List<string>();
            foreach (string para in plain.ToString().Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string[] words = para.Split(' ');
                string line = words[0];
                for (int k = 1; k < words.Length; k++)
                {
                    string candidate = line + " " + words[k];
                    if (Width(candidate) <= width) line = candidate;
                    else { lines.Add(line); line = words[k]; }
                }
                lines.Add(line);
            }
            return lines;
        }

        // Scanline fill at pixel centres, nonzero winding (aliased, NONANTIALIASED_QUALITY). Quadratic segments are
        // flattened into 8 lines. Returns the bitmap and its offset from the pen (x) and from the baseline (y down).
        static (bool[] Px, int W, int H, int Ox, int Oy) Rasterize(List<List<(double X, double Y, bool On)>> contours, double s)
        {
            var edges = new List<(double X0, double Y0, double X1, double Y1)>();
            foreach (var ct in contours)
            {
                int n = ct.Count;
                // Start at an on-curve point (or the midpoint of the first two off-curve points).
                (double X, double Y) start;
                int k0 = ct.FindIndex(p => p.On);
                if (k0 < 0) { start = ((ct[0].X + ct[1 % n].X) / 2, (ct[0].Y + ct[1 % n].Y) / 2); k0 = 0; }
                else start = (ct[k0].X, ct[k0].Y);
                (double X, double Y) cur = start;
                (double X, double Y)? ctrl = null;
                for (int i = 1; i <= n; i++)
                {
                    var p = ct[(k0 + i) % n];
                    if (p.On)
                    {
                        if (ctrl is { } q) Quad(edges, cur, q, (p.X, p.Y)); else edges.Add((cur.X, cur.Y, p.X, p.Y));
                        cur = (p.X, p.Y); ctrl = null;
                    }
                    else if (ctrl is { } q)
                    {
                        var mid = ((q.X + p.X) / 2, (q.Y + p.Y) / 2);
                        Quad(edges, cur, q, mid);
                        cur = mid; ctrl = (p.X, p.Y);
                    }
                    else ctrl = (p.X, p.Y);
                }
                if (ctrl is { } last) Quad(edges, cur, last, start);
                else if (cur != start) edges.Add((cur.X, cur.Y, start.X, start.Y));
            }
            if (edges.Count == 0) return (Array.Empty<bool>(), 0, 0, 0, 0);
            // Device space: x right, y down from the baseline.
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            for (int i = 0; i < edges.Count; i++)
            {
                var e = edges[i];
                e = (e.X0 * s, -e.Y0 * s, e.X1 * s, -e.Y1 * s);
                edges[i] = e;
                minX = Math.Min(minX, Math.Min(e.X0, e.X1)); maxX = Math.Max(maxX, Math.Max(e.X0, e.X1));
                minY = Math.Min(minY, Math.Min(e.Y0, e.Y1)); maxY = Math.Max(maxY, Math.Max(e.Y0, e.Y1));
            }
            int ox = (int)Math.Floor(minX), oy = (int)Math.Floor(minY);
            int w = (int)Math.Ceiling(maxX) - ox, h = (int)Math.Ceiling(maxY) - oy;
            var px = new bool[w * h];
            var cross = new List<(double X, int Dir)>();
            for (int r = 0; r < h; r++)
            {
                double yc = oy + r + 0.5;
                cross.Clear();
                foreach (var e in edges)
                {
                    if (e.Y0 == e.Y1) continue;
                    bool down = e.Y1 > e.Y0;
                    double ya = down ? e.Y0 : e.Y1, yb = down ? e.Y1 : e.Y0;
                    if (yc < ya || yc >= yb) continue;
                    cross.Add((e.X0 + (yc - e.Y0) * (e.X1 - e.X0) / (e.Y1 - e.Y0), down ? 1 : -1));
                }
                cross.Sort((p, q) => p.X.CompareTo(q.X));
                int wind = 0, ci = 0;
                for (int c = 0; c < w; c++)
                {
                    double xc = ox + c + 0.5;
                    while (ci < cross.Count && cross[ci].X <= xc) wind += cross[ci++].Dir;
                    if (wind != 0) px[r * w + c] = true;
                }
            }
            return (px, w, h, ox, oy);
        }

        static void Quad(List<(double, double, double, double)> edges, (double X, double Y) a, (double X, double Y) b, (double X, double Y) c)
        {
            const int Steps = 8;
            (double X, double Y) prev = a;
            for (int i = 1; i <= Steps; i++)
            {
                double t = (double)i / Steps, u = 1 - t;
                var p = (u * u * a.X + 2 * u * t * b.X + t * t * c.X, u * u * a.Y + 2 * u * t * b.Y + t * t * c.Y);
                edges.Add((prev.X, prev.Y, p.Item1, p.Item2));
                prev = p;
            }
        }
    }

    // Process registry of native fonts. A UFont in the transient package in the original (UObject::GetTransientPackage,
    // never saved); here the VM object carries the name by which the canvas natives and the frontend find the font.
    public static class UENativeFonts
    {
        static readonly Dictionary<string, UENativeFont> _byName = new(StringComparer.OrdinalIgnoreCase);
        static UETrueType _tt;

        static UETrueType Substitute()
        {
            if (_tt != null) return _tt;
            using var s = typeof(UENativeFonts).Assembly.GetManifestResourceStream("HP1.Core.Fonts.LiberationSans-Regular.ttf")
                ?? throw new InvalidOperationException("bundled font HP1.Core.Fonts.LiberationSans-Regular.ttf missing from HP1.Core");
            var ms = new MemoryStream();
            s.CopyTo(ms);
            return _tt = new UETrueType(ms.ToArray());
        }

        // UConsole.CreateNativeFont(FontName, Height). Every call makes a new font object (the original constructs a
        // new UFont per call); the glyph page is built once per (face, height).
        public static UEResourceRef Create(string face, int height)
        {
            if (!string.Equals(face, UENativeFont.SubstitutedFace, StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException(
                    $"CreateNativeFont('{face}', {height}): the port substitutes only '{UENativeFont.SubstitutedFace}' "
                    + "(RU edition, bundled Liberation Sans, author decision 2026-11-03); other OS fonts are not supported");
            if (height <= 0)
                throw new NotSupportedException($"CreateNativeFont('{face}', {height}): a non-positive height is not ported");
            string name = "NativeFont" + height;
            UENativeFont font;
            lock (_byName)
            {
                if (!_byName.TryGetValue(name, out font))
                {
                    font = new UENativeFont(name, face, height, Substitute());
                    _byName[name] = font;
                    UETrace.Info($"[Font] CreateNativeFont('{face}', {height}) -> bundled Liberation Sans (NOT ORIGINAL), line {font.LineHeight}");
                }
            }
            // A Font is a native class without script state: the VM carries it as a resource reference, the form a
            // Font literal has on the defaults path.
            return new UEResourceRef("Font", name, "Transient." + name);
        }

        // The native font behind a Canvas.Font value (a VM object made by Create), or null.
        public static UENativeFont Of(object font)
        {
            if (font is not UEResourceRef r || r.ObjectPath is null || !r.ObjectPath.StartsWith("Transient.", StringComparison.Ordinal)) return null;
            lock (_byName) return _byName.TryGetValue(r.Name, out var f) ? f : null;
        }

        // Frontend: glyph pages of a font by its leaf name (HudCommand.FontPath), or null.
        public static string[] PagePaths(string name)
        {
            lock (_byName) return _byName.TryGetValue(name ?? "", out var f) ? new[] { f.PagePath } : null;
        }
    }
}
