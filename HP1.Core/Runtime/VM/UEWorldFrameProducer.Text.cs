// Fonts and text on the canvas: the font metrics/catalog types (Hp1Font*), DrawFontText, DrawText,
// DrawTextClipped, TextSize/StrLen, the native-font string and WrappedPrint.
// Fields of UEWorldFrameProducer live in UEWorldFrameProducer.cs.
using System;
using System.Collections.Generic;
using System.IO;
using HP1.Core;
using UELib;

namespace HP1.UEScript
{
    // A paged font EXACTLY in the shipped UFont layout (Engine.dll.c:59195,
    // UFont::Serialize): Pages (+0x2c, FFontPage {UTexture* Texture; TArray<
    // FFontCharacter> Characters}), CharactersPerPage (+0x28), and for package
    // version >= 0x45 — CharRemap (+0x38, TMap<TCHAR,TCHAR>) and IsRemapped
    // (+0x4c). The text bodies (WrappedPrint 0x10387e68, FUN_10388530) look up a glyph
    // exactly like this: page = c / CharactersPerPage, index = c - page·CPP, so the
    // model keeps pages, not an array flattened by character code.
    //
    // Native fonts (empty Pages and CPP == 0: name +0x50 and height +0x5c) are measured
    // and drawn by the OS device — they cannot be rebuilt from package data, and the
    // bodies stop loudly (see WrappedPrint).
    public readonly struct Hp1FontChar
    {
        public readonly int StartU, StartV, USize, VSize;
        public Hp1FontChar(int u, int v, int us, int vs) { StartU = u; StartV = v; USize = us; VSize = vs; }
    }

    public sealed class Hp1FontPage
    {
        public bool HasTexture;           // FFontPage.Texture != NULL
        public Hp1FontChar[] Characters;
    }

    public sealed class Hp1FontMetrics
    {
        public string Path;
        public int CharactersPerPage;
        public Hp1FontPage[] Pages;
        public Dictionary<int, int> CharRemap;   // key -> value (TMap::Find)
        public bool IsRemapped;

        // Character remap: with IsRemapped — TMap::Find, a miss gives ' '
        // (WrappedPrint 0x10387e71-0x10387e8b; FUN_10388530 0x103885e3-0x1038861f).
        public int Remap(int c)
            => !IsRemapped ? c : CharRemap.TryGetValue(c, out int v) ? v : 0x20;

        // Shipped page/index: idiv by CharactersPerPage. Returns the page,
        // or null if there is no page or the index is beyond its Characters.Num.
        public bool TryChar(int c, out int page, out Hp1FontChar ch)
        {
            page = c / CharactersPerPage;
            int idx = c - CharactersPerPage * page;
            ch = default;
            if (page >= Pages.Length || idx >= Pages[page].Characters.Length) return false;
            ch = Pages[page].Characters[idx];
            return true;
        }
    }

    public sealed class Hp1FontCatalog
    {
        readonly string _systemDir;
        readonly Dictionary<string, Hp1FontMetrics> _cache = new(UEPropNameComparer.Instance);
        readonly Dictionary<string, (UnrealPackage Pkg, byte[] Bytes)> _packages = new(UEPropNameComparer.Instance);
        public Hp1FontCatalog(string systemDir) { _systemDir = systemDir; }

        public Hp1FontMetrics Get(string path)
        {
            if (_cache.TryGetValue(path, out var hit)) return hit;
            int dot = path.IndexOf('.');
            if (dot <= 0) throw new InvalidDataException($"font path has no package: '{path}'");
            string pkgName = path.Substring(0, dot);
            string fontName = path.Substring(path.LastIndexOf('.') + 1);
            if (!_packages.TryGetValue(pkgName, out var loaded))
            {
                // The package is searched in THE GAME'S OWN PATH ORDER ([Core.System] Paths,
                // the shared list UETextureFactsReader.PackagePaths), not only System/*.u:
                // the UWindow fonts live in Textures/UWindowFonts.utx.
                string root = Path.GetDirectoryName(Path.GetFullPath(_systemDir.TrimEnd('/', '\\')));
                string pkgPath = null;
                foreach (string pattern in UETextureFactsReader.PackagePaths)
                {
                    pkgPath = UEInstallPath.File(Path.Combine(root, string.Format(pattern, pkgName)));
                    if (pkgPath is not null) break;
                }
                if (pkgPath is null)
                    throw new FileNotFoundException($"font package missing: {pkgName} (searched [Core.System] Paths from {root})");
                // Only the name/export tables (read by LoadPackage) and the raw bytes are used: the font body is
                // parsed from the bytes below, so the package objects are not constructed (perf: a full
                // InitializePackage of HPBase.u cost ~30 ms / 20 MB on the first HUD text).
                var pkg = UnrealLoader.LoadPackage(pkgPath, FileAccess.Read);
                loaded = (pkg, File.ReadAllBytes(pkgPath));
                _packages[pkgName] = loaded;
            }
            UELib.UExportTableItem export = null;
            foreach (var e in loaded.Pkg.Exports)
            {
                if (!string.Equals(e.ObjectName.ToString(), fontName, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(e.Class?.ObjectName.ToString(), "Font", StringComparison.OrdinalIgnoreCase)) continue;
                export = e;
                break;
            }
            if (export is null)
                throw new InvalidDataException($"shipped font not found: {path}");
            var metrics = ReadSerialized(path, loaded.Pkg, loaded.Bytes, export);
            _cache[path] = metrics;
            return metrics;
        }

        // UFont::Serialize body (Engine.dll.c:59215-59273) over the export bytes:
        // object properties (shipped fonts have only the None terminator), Pages,
        // CharactersPerPage (a power of two — assert UnFont.cpp:0x1c), and for
        // version >= 0x45 CharRemap + IsRemapped, otherwise IsRemapped = 0. UELib
        // does not keep CharactersPerPage and reads the native font tail in a
        // different order, so the layout is read from the bytes in shipped order.
        static Hp1FontMetrics ReadSerialized(string path, UnrealPackage pkg, byte[] b, UELib.UExportTableItem e)
        {
            int p = e.SerialOffset, end = e.SerialOffset + e.SerialSize;
            int Compact()
            {
                byte b0 = b[p++];
                int v = b0 & 0x3f;
                if ((b0 & 0x40) != 0)
                {
                    int shift = 6;
                    while (true)
                    {
                        byte x = b[p++];
                        v |= (x & 0x7f) << shift;
                        shift += 7;
                        if ((x & 0x80) == 0) break;
                    }
                }
                return (b0 & 0x80) != 0 ? -v : v;
            }
            int Int() { int v = BitConverter.ToInt32(b, p); p += 4; return v; }
            int none = Compact();
            if (none < 0 || none >= pkg.Names.Count
                || !string.Equals(pkg.Names[none].Name, "None", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"font {path}: export properties are not empty — parsing of font property tags is not ported");
            int pageCount = Compact();
            var pages = new Hp1FontPage[pageCount];
            for (int i = 0; i < pageCount; i++)
            {
                int tex = Compact();
                int n = Compact();
                var chars = new Hp1FontChar[n];
                for (int j = 0; j < n; j++)
                    chars[j] = new Hp1FontChar(Int(), Int(), Int(), Int());
                pages[i] = new Hp1FontPage { HasTexture = tex != 0, Characters = chars };
            }
            int cpp = Int();
            if ((cpp & (cpp - 1)) != 0)
                throw new InvalidDataException($"font {path}: Assertion failed: !(CharactersPerPage&(CharactersPerPage-1)) [UnFont.cpp:28]");
            var remap = new Dictionary<int, int>();
            bool isRemapped = false;
            if (pkg.Version >= 0x45)
            {
                // TMap::Rehash inserts pairs in ascending index order at the chain HEAD,
                // and Find returns the last inserted pair with that key —
                // so a later pair overrides an earlier one.
                int n = Compact();
                for (int i = 0; i < n; i++)
                {
                    int k = BitConverter.ToUInt16(b, p), v = BitConverter.ToUInt16(b, p + 2);
                    p += 4;
                    remap[k] = v;
                }
                isRemapped = Int() != 0;
            }
            if (pageCount == 0 && cpp == 0)
                throw new InvalidDataException(
                    $"font {path} has no glyph pages (a serialized native font) — "
                    + "measurement needs the shipped-OS glyph-width oracle");
            if (p != end)
                throw new InvalidDataException($"font {path}: parse did not match the end of the export ({p} vs {end})");
            return new Hp1FontMetrics
            {
                Path = path, CharactersPerPage = cpp, Pages = pages,
                CharRemap = remap, IsRemapped = isRemapped,
            };
        }
    }

    public sealed partial class UEWorldFrameProducer
    {
        // Polygon flags by canvas Style — the same table in WrappedPrint
        // (0x10387d54-0x10387d79) and execDrawTextClipped (0x1038a3f1-0x1038a417):
        // STY_None -> PF_Invisible (1), STY_Translucent -> 4, STY_Modulated -> 0x40,
        // others -> 0; always | 0x1000802 (PF_Masked | PF_NoSmooth | 0x01000000).
        // A glyph goes to RenDev->DrawTile with THESE flags unchanged: FUN_10388b60
        // passes its flags argument last (0x10388c75-0x10388c79) and adds no flags of the
        // font page texture. The same flags travel in the text command
        // (HudCommand.PolyFlags) — the frontend takes the glyph blend mode
        // from there. The canvas bNoSmooth is not read for text: 0x800 is always set.
        static int TextPolyFlags(int style)
            => (style == 0 ? 1 : style == 3 ? 4 : style == 4 ? 0x40 : 0) | 0x1000802;

        // ── FUN_10388b60 (Engine.dll 0x10388b60-0x10388d0c): glyph to the device ──
        //
        // Integer clipping by Frame->X/Y (+0xa8/+0xac) and a call to
        // RenDev->DrawTile(Frame, Info, X, Y, UL, VL, U, V, UL, VL, 0, Z, ...).
        // Shipped takes the on-screen width and height from UL/VL (0x10388c6e-0x10388cf4),
        // and the right/bottom cut reduces ONLY UL/VL — W/H are not read
        // after it. PF_Invisible (flag 1) draws nothing.
        void GlyphToDevice(List<HudGlyph> into, UEObject canvas, int flags, int page,
                           int x, int y, int w, int h, int u, int v, int ul, int vl)
        {
            if ((flags & 1) != 0) return;                                   // 0x10388b71
            CanvasFrame fr = FrameOf(canvas);                               // Canvas+0x7c, dereferenced after the flag
            int xw = x + w;
            if (xw <= 0) return;                                            // 0x10388b89
            int yh = y + h;
            if (yh <= 0) return;                                            // 0x10388b9c
            if (x >= fr.X || y >= fr.Y) return;                             // 0x10388ba8/0x10388bb4
            if (w <= 0 || h <= 0) return;                                   // 0x10388bc0/0x10388bc8
            if (x < 0)                                                      // 0x10388bd0-0x10388bf8
            {
                int d = x * ul / w;
                ul += d; u -= d; x = 0; w = xw;
            }
            if (y < 0)                                                      // 0x10388bfc-0x10388c26
            {
                int d = y * vl / h;
                v -= d; vl += d; y = 0; h = yh;
            }
            int roomX = fr.X - x;                                           // 0x10388c2a-0x10388c48
            if (w > roomX) ul += (roomX - w) * ul / w;
            int roomY = fr.Y - y;                                           // 0x10388c4c-0x10388c66
            if (h > roomY) vl += (roomY - h) * vl / h;
            into.Add(new HudGlyph(page, x, y, ul, vl, u, v, ul, vl));
        }

        // ── FUN_10388530 (Engine.dll 0x10388530-0x10388a13): string output ──
        //
        // Its native-font branch (0x10388552-0x103885ac, Viewport->DrawString) is taken by the caller before the
        // metrics are looked up (DrawTextClipped -> NativeDrawString); this body is the bitmap-font path.
        //
        // Shared text helper: called by WrappedPrint (line by line,
        // clip = 0, hotkey = 0; 0x10388113) and execDrawTextClipped (clip = 1,
        // hotkey = bCheckHotKey; 0x1038a4ad). Returns the sum of pen advances
        // (local_2ac). X/Y are RELATIVE to the canvas (without OrgX/OrgY): comparisons with
        // ClipX/ClipY are in these coordinates, Org is added only when
        // passing the glyph to the device (0x10388957/0x10388967).
        //
        // Glyph lookup (0x103885e3-0x103887bd): the character is remapped by CharRemap
        // (miss -> ' '); page = c / CharactersPerPage; no page, no page
        // texture or index beyond Characters.Num — the character is SKIPPED
        // without advancing; USize == 0 for 'a'..'z' — retry with c - 0x20 (already
        // remapped, no second remap).
        //
        // Hotkey (0x1038862b-0x103886f7): '&' followed by a non-'&' draws the
        // NEXT character without advancing and remembers its USize; on the next
        // step '_' is drawn instead of the character with width min(remembered, USize of '_'),
        // and the pen advances by the remembered width. "&" at the end of the string stops output.
        internal int DrawFontText(int flags, UEObject canvas, Hp1FontMetrics m, int x, int y,
                         string text, bool clip, bool hotkey, List<HudGlyph> glyphs)
        {
            char Ch(int k) => k < text.Length ? text[k] : '\0';
            if (Ch(0) == '\0') return 0;                                    // 0x1038853d
            float orgX = CanvasF(canvas, "OrgX"), orgY = CanvasF(canvas, "OrgY");
            float spaceX = CanvasF(canvas, "SpaceX");
            float clipX = CanvasF(canvas, "ClipX"), clipY = CanvasF(canvas, "ClipY");
            int acc = 0;                                                    // [esp+0x1c]
            bool prevAmp = false;                                           // [esp+0x18]
            int savedW = 0;                                                 // [esp+0x28]
            int pos = 0;
            do
            {
                bool amp = false;                                           // [esp+0x34]
                int c = m.Remap(Ch(pos));
                if (hotkey)
                {
                    if (prevAmp) c = m.Remap('_');                          // 0x1038863a-0x10388687
                    if (Ch(pos) == '&')                                     // 0x1038868d
                    {
                        char next = Ch(pos + 1);
                        if (next == '\0') break;                            // 0x1038869b -> 0x103889e7
                        if (next != '&')
                        {
                            amp = true;                                     // 0x103886a9
                            c = m.Remap(next);
                        }
                    }
                }
                while (true)
                {
                    int page = c / m.CharactersPerPage;                     // 0x10388703
                    if (page >= m.Pages.Length) break;                      // 0x10388705 / 0x103887b4
                    Hp1FontPage pg = m.Pages[page];
                    if (!pg.HasTexture) break;                              // 0x1038871c
                    int idx = c - m.CharactersPerPage * page;
                    if (idx >= pg.Characters.Length) break;                 // 0x1038872e
                    Hp1FontChar ch = pg.Characters[idx];
                    if (ch.USize == 0 && c >= 0x61 && c <= 0x7a)            // 0x1038878f-0x1038879e
                    {
                        c -= 0x20;                                          // 0x103887a7
                        continue;
                    }
                    // 0x103887d6-0x1038881a: W = prevAmp ? min(savedW, USize) : USize.
                    int w = prevAmp ? Math.Min(savedW, ch.USize) : ch.USize;
                    int advW = w;                                           // [esp+0x14]
                    int h = ch.VSize, u = ch.StartU, v = ch.StartV;
                    int gx = acc + x, gy = y;
                    bool draw = true;
                    if (clip)                                               // 0x10388824
                    {
                        // Comparisons — x87 over an integer (fild) and a float canvas field.
                        if (gx + w <= 0) draw = false;                      // 0x10388830
                        else if ((float)gx > clipX) draw = false;           // 0x1038884e
                        else if (gy + h <= 0) draw = false;                 // 0x1038885c
                        else if ((float)gy > clipY) draw = false;           // 0x10388872
                        else
                        {
                            if (gx < 0) { u -= gx; w = gx + w; gx = 0; }   // 0x10388880-0x103888a1
                            if (gy < 0) { v -= gy; h = gy + h; gy = 0; }   // 0x103888a5-0x103888c4
                            // Shipped render window is PC24: the difference is rounded to float.
                            if ((float)(gx + w) > clipX)                    // 0x103888c8-0x103888e8
                                w = UELevelRuntime.Ftol((float)((double)clipX - gx));
                            if ((float)(gy + h) > clipY)                    // 0x103888ea-0x1038890f
                                h = UELevelRuntime.Ftol((float)((double)clipY - gy));
                        }
                    }
                    if (draw)                                               // 0x1038891a-0x1038897e
                        GlyphToDevice(glyphs, canvas, flags, page,
                            UELevelRuntime.Ftol((float)((double)gx + orgX)),
                            UELevelRuntime.Ftol((float)((double)gy + orgY)),
                            w, h, u, v, w, h);
                    // 0x10388993-0x103889c4: pen advance.
                    if (prevAmp) advW = savedW;
                    if (!amp) acc += UELevelRuntime.Ftol((float)((double)advW + spaceX));
                    else savedW = ch.USize;
                    prevAmp = amp;
                    break;
                }
                pos++;                                                      // 0x103889cf-0x103889e1
            } while (Ch(pos) != '\0');
            return acc;
        }

        // ── UCanvas::execDrawTextClipped (0x1038a2f0; Engine.dll.c:58026) ──
        //
        // No WrappedPrint and no wrapping: "No font" -> log and return; Large/BigFont
        // with language != INT WRITE Canvas.Font = MedFont (0x1038a3ce — unlike
        // WrappedPrint, where a local parameter is substituted); check(Font)
        // UnCanvas.cpp:0x2e1; flags by Style; the helper with clip = 1 and hotkey =
        // bCheckHotKey from ftol(CurX), ftol(CurY). The pen is not touched.
        public void DrawTextClipped(UEObject canvas, string text, bool checkHotKey)
        {
            object font = canvas.GetProp(PK.Font);
            if (font is null)
            {
                HP1.Core.UETrace.Warn("DrawTextClipped: No font");   // string 0x104d5ed8
                return;
            }
            if (SameFont(font, canvas.GetProp(PK.LargeFont)) || SameFont(font, canvas.GetProp(PK.BigFont)))
            {
                if (!string.Equals(UELocalization.Language, "INT", StringComparison.OrdinalIgnoreCase))
                {
                    font = canvas.GetProp(PK.MedFont);
                    canvas.Props["Font"] = font;
                }
            }
            if (font is null)
                throw new InvalidOperationException("Assertion failed: Font [UnCanvas.cpp:737] (UCanvas::execDrawTextClipped)");
            int style = UEValue.ToInt(canvas.GetProp(PK.Style));
            int flags = TextPolyFlags(style);
            int x = UELevelRuntime.Ftol(CanvasF(canvas, "CurX"));
            int y = UELevelRuntime.Ftol(CanvasF(canvas, "CurY"));
            float orgX = CanvasF(canvas, "OrgX"), orgY = CanvasF(canvas, "OrgY");
            UEVector color = DrawColorOf(canvas);
            bool smooth = !UEValue.ToBool(canvas.GetProp(PK.bNoSmooth));
            string fontPath = FontPathOf(font);
            // FUN_10388530 0x10388552-0x103885ac: a native font (+0x54 != 0) goes to Viewport->DrawString(flags, Font,
            // X, Y, Text, Color) — clip and hotkey are not passed; the pen is not touched.
            if (UENativeFonts.Of(font) is UENativeFont nf)
            {
                NativeDrawString(canvas, flags, style, nf, ref x, ref y, text);
                return;
            }
            var glyphs = new List<HudGlyph>();
            DrawFontText(flags, canvas, _fonts.Get(fontPath), x, y, text, clip: true, hotkey: checkHotKey, glyphs);
            _hud.Add(new HudCommand(HudKind.Text, x + orgX, y + orgY,
                0f, 0f, 0f, 0f, 0f, 0f, null, text, fontPath, color, style, smooth, true,
                glyphs.ToArray(), polyFlags: flags));
        }

        // ── UCanvas::execDrawText (0x10389350; Engine.dll.c:43790) ──
        //
        // Shipped order: "No font" → log and return (0x103893cc); Style ==
        // STY_None → WrappedPrint is NOT called, XL = YL = 0 (0x10389429-0x10389434),
        // i.e. only CR moves the pen. Tail 0x10389465-0x10389498:
        // CurX += XL; CurYL = max(CurYL, YL); with CR — CurY += CurYL (already
        // the new one), CurX = 0, CurYL = 0. There is NO "CurY -= YL" compensation: CurY was
        // already moved by WrappedPrint itself for EVERY line, including the last, and CR
        // moves it once more.
        public void DrawText(UEObject canvas, string text, bool cr)
        {
            object font = canvas.GetProp(PK.Font);
            if (font is null)
            {
                HP1.Core.UETrace.Warn("DrawText: No font");   // shipped log text
                return;
            }
            int style = UEValue.ToInt(canvas.GetProp(PK.Style));
            int xl = 0, yl = 0;
            if (style != 0)                                      // 0x10389429-0x10389434
            {
                bool center = UEValue.ToBool(canvas.GetProp(PK.bCenter));
                WrappedPrint(canvas, style, ref xl, ref yl, font, center, text, emit: true);
            }
            // 0x10389465-0x10389498
            canvas.Props["CurX"] = (float)((double)xl + CanvasF(canvas, "CurX"));
            float curYL = CanvasF(canvas, "CurYL");
            // fld CurYL; fcomp st(1)=YL; test ah, 1 (0x10389471-0x1038947e): YL is taken
            // when CurYL < YL and on NaN ("unordered" sets C0).
            if (!(curYL >= yl)) curYL = yl;
            canvas.Props["CurYL"] = curYL;
            if (cr)
            {
                canvas.Props["CurY"] = curYL + CanvasF(canvas, "CurY");
                canvas.Props["CurX"] = 0f;
                canvas.Props["CurYL"] = 0f;
            }
        }

        // ── UCanvas::execTextSize (0x1038a640; Engine.dll.c:32995-33004) ──
        //
        // Without a font — log "TextSize: No font" and return WITHOUT writing XL/YL
        // (0x1038a6f5-0x1038a730: the out parameters stay as they were; the caller
        // gets false and writes nothing). ClipX/CurX/CurY are saved THROUGH
        // ftol (0x1038a75f-0x1038a77d), then CurX = CurY = 0, ClipX = 32767.0
        // (0x46fffe00), WrappedPrint with STY_None and no centring, and the fields
        // are restored FROM THE INTEGERS (0x1038a7b1-0x1038a7c9) — the fractional part
        // of the pen is lost after TextSize.
        public bool TextSize(UEObject canvas, string text, out float outXL, out float outYL)
        {
            outXL = 0f; outYL = 0f;
            object font = canvas.GetProp(PK.Font);
            if (font is null)
            {
                HP1.Core.UETrace.Warn("TextSize: No font");   // shipped log text
                return false;
            }
            int clipX = UELevelRuntime.Ftol(CanvasF(canvas, "ClipX"));
            int curX = UELevelRuntime.Ftol(CanvasF(canvas, "CurX"));
            int curY = UELevelRuntime.Ftol(CanvasF(canvas, "CurY"));
            canvas.Props["CurX"] = 0f;
            canvas.Props["CurY"] = 0f;
            canvas.Props["ClipX"] = 32767f;
            int xl = 0, yl = 0;
            WrappedPrint(canvas, 0, ref xl, ref yl, font, false, text, emit: false);
            canvas.Props["CurY"] = (float)curY;
            canvas.Props["CurX"] = (float)curX;
            canvas.Props["ClipX"] = (float)clipX;
            outXL = xl; outYL = yl;
            return true;
        }

        // ── UCanvas::execStrLen (0x10389050; Engine.dll.c:47208-47229) ──
        //
        // CurX/CurY are saved through ftol (0x1038911e-0x10389131), zeroed,
        // and WrappedStrLenf (vtbl+0x6c, 0x10388d80) = WrappedPrint(
        // STY_None, XL, YL, Font, Center = 0, Text) is called with the LIVE ClipX — StrLen
        // wraps lines at the current width. Restored from the integers
        // (0x10389161-0x10389173). StrLen has no "No font" check: a null
        // font hits the assert inside WrappedPrint.
        public (float, float) StrLen(UEObject canvas, string text)
        {
            int curX = UELevelRuntime.Ftol(CanvasF(canvas, "CurX"));
            int curY = UELevelRuntime.Ftol(CanvasF(canvas, "CurY"));
            canvas.Props["CurX"] = 0f;
            canvas.Props["CurY"] = 0f;
            int xl = 0, yl = 0;
            WrappedPrint(canvas, 0, ref xl, ref yl, canvas.GetProp(PK.Font), false, text, emit: false);
            canvas.Props["CurY"] = (float)curY;
            canvas.Props["CurX"] = (float)curX;
            return (xl, yl);
        }

        // Glyph measure inside WrappedPrint (0x10387e68-0x10387f53) — NOT the same as
        // in the draw helper: the character is remapped by CharRemap; page/index
        // by CharactersPerPage; a found glyph with USize != 0 is the answer; otherwise (no
        // page, index outside the page, USize == 0) for an ORIGINAL character
        // 'a'..'z' the glyph of (original - 0x20) is taken, again through CharRemap, and its
        // size is accepted as-is. The page texture is not checked here.
        // If a glyph is found with USize == 0 and the character is not lowercase — W = 0, H = VSize.
        static (int U, int V) MeasureGlyph(Hp1FontMetrics m, char ch)
        {
            int w = 0, h = 0;                                                // [ebp-0x18], edi
            if (m.TryChar(m.Remap(ch), out _, out Hp1FontChar g))
            {
                w = g.USize; h = g.VSize;
                if (w != 0) return (w, h);                                   // 0x10387ed0
            }
            if (ch >= 0x61 && ch <= 0x7a)                                    // 0x10387edd-0x10387ee7
            {
                w = 0; h = 0;                                                // 0x10387eef-0x10387ef7
                if (m.TryChar(m.Remap(ch - 0x20), out _, out Hp1FontChar up))
                { w = up.USize; h = up.VSize; }
            }
            return (w, h);
        }

        // ── UWindowsViewport::DrawString (WinDrv), reached from the native-font branches of WrappedPrint
        // (0x10387d8a-0x10387df5) and FUN_10388530 (0x10388552-0x103885ac) ──
        //
        // WrappedPrint: X = ftol(CurX), Y = ftol(CurY), flags | 0x100 with bCenter; after the call XL = ftol(float(X) -
        // CurX), YL = ftol(float(Y) - CurY), CurX = float(X), CurY = float(Y) — the pen moves right AND down by the
        // text box, unlike the bitmap branch. FUN_10388530 (DrawTextClipped): the same call, pen untouched.
        // WinDrv (US 0x1110a0e4-0x1110a20d; RU differs only in font creation, ANSI conversion and an error log):
        // DrawText(DT_CALCRECT|DT_WORDBREAK) in (0, 0, ftol(ClipX), ftol(ClipY)) gives the box w × h; X += w, Y += h;
        // unless flags & 1 (STY_None) the text is drawn in (ftol(X - w + OrgX), ftol(Y - h + OrgY), ftol(X + OrgX),
        // ftol(Y + OrgY)), with 0x100 shifted left by (right - left) / 2 and DT_CENTER; DrawText clips to that box
        // (no DT_NOCLIP). GDI pixels are opaque text colour: the command is PF_Masked with the font's aliased coverage.
        // Layout and glyphs come from the substitute font (UENativeFont, NOT ORIGINAL).
        void NativeDrawString(UEObject canvas, int flags, int style, UENativeFont nf, ref int x, ref int y, string text)
        {
            int nul = text.IndexOf('\0');
            if (nul >= 0) text = text.Substring(0, nul);
            List<string> lines = nf.Lines(text, UELevelRuntime.Ftol(CanvasF(canvas, "ClipX")));
            int w = 0;
            foreach (string l in lines) w = Math.Max(w, nf.Width(l));
            int h = lines.Count * nf.LineHeight;
            x += w; y += h;
            if ((flags & 1) != 0) return;
            float orgX = CanvasF(canvas, "OrgX"), orgY = CanvasF(canvas, "OrgY");
            int left = UELevelRuntime.Ftol((float)((double)(x - w) + orgX)), right = UELevelRuntime.Ftol((float)((double)x + orgX));
            int top = UELevelRuntime.Ftol((float)((double)(y - h) + orgY)), bottom = UELevelRuntime.Ftol((float)((double)y + orgY));
            bool center = (flags & 0x100) != 0;
            if (center) { int d = (right - left) / 2; left -= d; right -= d; }
            var glyphs = new List<HudGlyph>();
            for (int i = 0; i < lines.Count; i++)
            {
                int pen = center ? left + (right - left - nf.Width(lines[i])) / 2 : left;
                int lineTop = top + i * nf.LineHeight;
                foreach (char c in lines[i])
                {
                    UENativeGlyph g = nf.Glyph(c);
                    int gx = pen + g.OffX, gy = lineTop + g.OffY, u = g.U, v = g.V, gw = g.W, gh = g.H;
                    pen += g.Advance;
                    // Clipped to the DrawText box.
                    if (gx < left) { u += left - gx; gw -= left - gx; gx = left; }
                    if (gy < top) { v += top - gy; gh -= top - gy; gy = top; }
                    gw = Math.Min(gw, right - gx); gh = Math.Min(gh, bottom - gy);
                    if (gw > 0 && gh > 0) glyphs.Add(new HudGlyph(0, gx, gy, gw, gh, u, v, gw, gh));
                }
            }
            _hud.Add(new HudCommand(HudKind.Text, left, top, right - left, bottom - top, 0f, 0f, 0f, 0f, null, text,
                nf.Name, DrawColorOf(canvas), style, !UEValue.ToBool(canvas.GetProp(PK.bNoSmooth)), false,
                glyphs.ToArray(), polyFlags: 0x1000802));
        }

        static bool SameFont(object a, object b)
            => a is not null && b is not null
               && (ReferenceEquals(a, b)
                   || string.Equals(FontPathOf(a), FontPathOf(b), StringComparison.OrdinalIgnoreCase));

        // ── UCanvas::WrappedPrint (0x10387c50-0x10388359; Engine.dll.c:157936) ──
        //
        // The native-font branch (0x10387d80-0x10387e08) leaves the pen where Viewport->DrawString moved it (CurX/CurY
        // written back, XL/YL its offsets). xl/yl — shipped's output INT&. emit — whether the caller draws (in shipped
        // this is Style != STY_None, 0x10387ff5).
        internal void WrappedPrint(UEObject canvas, int style, ref int xl, ref int yl,
                          object font, bool center, string text, bool emit)
        {
            // A shipped string is a TCHAR*, ending at the first NUL.
            char Ch(int k) => k < text.Length ? text[k] : '\0';

            // 0x10387c7b-0x10387caa: ClipX < 0 || ClipY < 0 || *Text == 0 →
            // XL = YL = 0 (0x10388337) and return; the pen is not touched. Comparison —
            // fcomp 0.0; test ah, 1: C0 is also set for "unordered", so NaN
            // in ClipX/ClipY also returns.
            if (!(CanvasF(canvas, "ClipX") >= 0f) || !(CanvasF(canvas, "ClipY") >= 0f) || Ch(0) == '\0')
            {
                xl = 0; yl = 0;
                return;
            }
            // 0x10387cb0-0x10387cde: Font == LargeFont || Font == BigFont, and
            // appStricmp(GetLanguage(), "INT") != 0 → Font = MedFont. The language is
            // GLanguage (UELocalization.Language), set by appInit from
            // [Engine.Engine] Language; shipped has int.
            if (SameFont(font, canvas.GetProp(PK.LargeFont)) || SameFont(font, canvas.GetProp(PK.BigFont)))
            {
                if (!string.Equals(UELocalization.Language, "INT", StringComparison.OrdinalIgnoreCase))
                    font = canvas.GetProp(PK.MedFont);
            }
            // 0x10387ce0-0x10387cf3: check(Font), UnCanvas.cpp:356.
            if (font is null)
                throw new InvalidOperationException("Assertion failed: Font [UnCanvas.cpp:356] (UCanvas::WrappedPrint)");
            // 0x10387d80-0x10387e08: a native font (name length +0x54 != 0) goes to Viewport->DrawString.
            if (UENativeFonts.Of(font) is UENativeFont nf)
            {
                float curX = CanvasF(canvas, "CurX"), curY = CanvasF(canvas, "CurY");
                int x = UELevelRuntime.Ftol(curX), y = UELevelRuntime.Ftol(curY);
                NativeDrawString(canvas, TextPolyFlags(style) | (center ? 0x100 : 0), style, nf, ref x, ref y, text);
                xl = UELevelRuntime.Ftol((float)((double)(float)x - curX));
                yl = UELevelRuntime.Ftol((float)((double)(float)y - curY));
                canvas.Props["CurX"] = (float)x;
                canvas.Props["CurY"] = (float)y;
                return;
            }

            float orgX = CanvasF(canvas, "OrgX"), orgY = CanvasF(canvas, "OrgY");
            UEVector color = DrawColorOf(canvas);
            bool smooth = !UEValue.ToBool(canvas.GetProp(PK.bNoSmooth));
            string fontPath = FontPathOf(font);
            int flags = TextPolyFlags(style);                        // 0x10387d54-0x10387d85
            Hp1FontMetrics m = _fonts.Get(fontPath);
            xl = 0; yl = 0;                                          // 0x10387e09-0x10387e17
            float savedCurX = CanvasF(canvas, "CurX");               // 0x10387e1d-0x10387e23
            int pos = 0;
            do
            {
                // 0x10387e26-0x10387e49: line X = bCenter ? 0 : ftol(CurX).
                int x = center ? 0 : UELevelRuntime.Ftol(CanvasF(canvas, "CurX"));
                int cutX = 0, lineH = 0, cutH = 0, cutIdx = 0;
                bool boundary = false;                               // [ebp-0x1c]
                float spaceX = CanvasF(canvas, "SpaceX"), spaceY = CanvasF(canvas, "SpaceY");
                float clipX = CanvasF(canvas, "ClipX");
                int i = 0;
                while (true)
                {
                    char c = Ch(pos + i);
                    if (c == '\0' || c == '\n') break;               // 0x10387e55-0x10387e62
                    var g = MeasureGlyph(m, c);
                    // 0x10387f56-0x10387f6c: fild W; fadd SpaceX; call _ftol — no fstp,
                    // but the register is in the PC24 render window: the sum is rounded to 24 bits BEFORE
                    // truncation (the same step as the pen advance in FUN_10388530
                    // 0x103889ab). Exact via double: |W| < 2^24, the int+float sum
                    // fits in 53 bits.
                    x += UELevelRuntime.Ftol((float)((double)g.U + spaceX));
                    int h = UELevelRuntime.Ftol(spaceY) + g.V;       // 0x10387f67-0x10387f77
                    if (lineH < h) lineH = h;                        // 0x10387f79-0x10387f7d
                    if (!((double)x <= clipX)) break;                // 0x10387f80-0x10387f8b
                    i++;
                    char n = Ch(pos + i);                            // 0x10387f8d-0x10387fa4
                    bool atBoundary = n == ' ' || n == '\n' || n == '\0';
                    // 0x10387fa6-0x10387fad: until a word boundary is met,
                    // the cut point moves on EVERY character — the first
                    // over-long word is cut by characters.
                    if (!atBoundary && boundary) continue;
                    cutX = x; cutIdx = i; cutH = lineH;              // 0x10387fb3-0x10387fc4
                    boundary = boundary || atBoundary;               // 0x10387fc7-0x10387fdc
                }
                // 0x10387fe8-0x10387fef: nothing to cut → return (XL/YL as-is).
                // A '\n' at the start of a line also lands here: shipped does NOT print
                // text after a line feed.
                if (cutIdx == 0) return;
                if (emit)
                {
                    // 0x10388003-0x10388033: a line is output only if it
                    // crosses the frame vertically: OrgY+CurY < Frame->Y and
                    // OrgY+CurY+cutH > 0. The sum is x87 in the render window (PC24),
                    // i.e. rounded to float; Frame->Y is the canvas frame integer.
                    float top = (float)((double)orgY + CanvasF(canvas, "CurY"));
                    if (top < FrameOf(canvas).Y && (float)((double)cutH + top) > 0f)
                    {
                        // 0x10388094-0x103880ca: X = bCenter ? ftol(CurX - cutX/2)
                        // : ftol(CurX) — centred on the PEN, not on ClipX. cutX/2 is
                        // cdq; sub; sar (truncation toward zero); fild; fsubr CurX; _ftol —
                        // the difference in the PC24 register is rounded to 24 bits before truncation.
                        float cx = CanvasF(canvas, "CurX");
                        int drawX = center ? UELevelRuntime.Ftol((float)((double)cx - cutX / 2)) : UELevelRuntime.Ftol(cx);
                        int drawY = UELevelRuntime.Ftol(CanvasF(canvas, "CurY"));   // 0x103880ff
                        int x0 = center ? 0 : UELevelRuntime.Ftol(cx);
                        string line = text.Substring(pos, cutIdx);
                        var glyphs = new List<HudGlyph>();
                        // 0x10388113: line helper, clip = 0, hotkey = 0.
                        int drawn = DrawFontText(flags, canvas, m, drawX, drawY, line,
                            clip: false, hotkey: false, glyphs);
                        // 0x103881a7-0x103881b3: CurX = X + width of the output
                        // (overwritten below at 0x10388291 — kept for ordering).
                        canvas.Props["CurX"] = (float)(drawX + drawn);
                        _hud.Add(new HudCommand(HudKind.Text, drawX + orgX, drawY + orgY,
                            cutX - x0, cutH, 0f, 0f, 0f, 0f, null, line, fontPath,
                            color, style, smooth, false, glyphs.ToArray(), polyFlags: flags));
                    }
                }
                // 0x10388291-0x103882ca: after EVERY line, including the last.
                canvas.Props["CurX"] = center ? savedCurX : 0f;
                canvas.Props["CurY"] = (float)((double)cutH + CanvasF(canvas, "CurY"));
                yl += cutH;
                if (xl < cutX) xl = cutX;
                // 0x103882cc-0x103882e3: skip ALL spaces; continue until NUL.
                pos += cutIdx;
                while (Ch(pos) == ' ') pos++;
            } while (Ch(pos) != '\0');
        }
    }
}
