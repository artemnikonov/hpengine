// ScriptedTexture — Engine.dll UScriptedTexture drawing natives in Core: the P8 destination (mip 0, USize x VSize,
// the texture's own palette) and the per-texture PaletteMap (+0xd0: TMap<UTexture*, TArray<BYTE>>, source palette
// index -> nearest destination index, built once per source texture and never rebuilt).
//
// Bodies (Engine.dll US 1.53, decompile together with the disassembly of the same range):
//   DrawTile            0x10417e50-0x10418325 (the blitter every native below ends in)
//   UPalette::BestMatch 0x10423df0-0x10423ebd
//   execDrawText        0x10418480 (glyph loop 0x104185b3-0x1041873e)
//   execDrawColoredText 0x10418f10 (the same loop; BestMatch of the colour first, DrawTile with the colour fill)
//   execTextSize        0x10419410 (0x10419553-0x10419610)
//   execReplaceTexture  0x10418930
//   execDrawTile        0x10418b80 (DrawTile(X,Y,XL,YL,U,V,UL,VL, Tex->Get(Time), Info, bMasked, 0, 0), 0x10418d78)
//
// Inputs the callers resolve: the source texture is what UTexture::Get(Time) returns (AnimCur, else itself) and its
// texels are the mip 0 its Lock gives (Info.Mips[0]->DataPtr, 0x10417e64); a font page is the page texture itself
// (execDrawText passes Pages[i].Texture, no Get). Lock/Unlock/Update have no effect on these texels for a texture
// without animation; an animated source is the caller's STOP.
//
// Precision: the x87 chain of DrawTile rounds by the caller's window (PC24 inside PreRender..PostRender, where
// UScriptedTexture::Tick raises RenderTexture; PC64 elsewhere) — `pc24`. Every value that reaches fistp was stored to
// a float first (disasm below), so only the stored floats depend on the precision.
using System;
using System.Collections.Generic;
using HP1.Core;

namespace HP1.UEScript
{
    // A P8 texture as the original bodies read it: USize (+0x34), VSize (+0x38), Palette (+0x2c, 256 FColor = R,G,B,A
    // bytes) and the mip 0 bytes its Lock returns. Key: the object identity (TMap key = the UTexture pointer).
    public sealed class ScriptedTexelSource
    {
        public object Key;
        public int USize, VSize;
        public byte[] Palette;
        public byte[] Mip0;
    }

    public sealed class ScriptedTexture
    {
        public readonly int USize, VSize;
        public byte[] Palette;                         // +0x2c (PostLoad may replace it with the source's)
        public readonly byte[] Mip0;                   // Mips(0).DataPtr (+0xa4 -> +0x1c), USize*VSize
        readonly Dictionary<object, byte[]> _paletteMap = new(ReferenceEqualityComparer.Instance);   // +0xd0

        public ScriptedTexture(int uSize, int vSize, byte[] palette, byte[] mip0)
        {
            if (uSize <= 0 || vSize <= 0 || mip0 is null || mip0.Length < uSize * vSize)
                throw new ArgumentException($"ScriptedTexture: mip 0 {mip0?.Length.ToString() ?? "null"} bytes for {uSize}x{vSize}");
            if (palette is null || palette.Length < 1024)
                throw new ArgumentException("ScriptedTexture: no palette (DrawTile's BestMatch reads Palette->Colors)");
            USize = uSize; VSize = vSize; Palette = palette; Mip0 = mip0;
        }

        // Original: Engine.dll ?BestMatch@UPalette@@QAEEVFColor@@H@Z [HP1 0x10423DF0]
        // Weighted distance 4*dR^2 + 8*dG^2 + dB^2 (lea ecx,[eax+2*esi]; lea ecx,[eax+4*ecx]), first strict minimum
        // from First; an entry is skipped without computing the rest when dG^2 >= (best+7)>>3 (0x10423e51, jge).
        // First > 255 returns First (0x10423e38).
        public static byte BestMatch(byte[] palette, byte r, byte g, byte b, int first)
        {
            if (first >= 0x100) return (byte)first;
            int bound = int.MaxValue, bestD = int.MaxValue, best = first;
            for (int i = first; i < 0x100; i++)
            {
                int dg = palette[4 * i + 1] - g;
                dg *= dg;
                if (dg >= bound) continue;
                int dr = palette[4 * i] - r, db = palette[4 * i + 2] - b;
                int d = db * db + 4 * (dr * dr + 2 * dg);
                if (d < bestD) { best = i; bound = (d + 7) >> 3; bestD = d; }
            }
            return (byte)best;
        }

        static X87Ext R(X87Ext v, bool pc24) => pc24 ? X87Ext.Round24(v) : X87Ext.Round64(v);
        static int Fistp(float v) => MeshTriangleOrder.Fistp(v);

        // Original: Engine.dll ?DrawTile@UScriptedTexture@@QAEXMMMMMMMMPAVUTexture@@PAUFTextureInfo@@HHE@Z [HP1 0x10417E50]
        // masked = param 11, colorFill = param 12 (the texel becomes `color` where the source is non-zero).
        public void DrawTile(float x, float y, float xl, float yl, float u, float v, float ul, float vl,
                             ScriptedTexelSource src, bool masked, bool colorFill, byte color, bool pc24)
        {
            // 0x10417e65-0x10417eca: fld X; fadd XL; fst [t]; fcomp 0.0 -> exit below zero (NaN exits too); the same
            // for Y; fild USize; fcomp X -> exit when USize < X; VSize/Y.
            X87Ext ex = R(X87Ext.From(x) + X87Ext.From(xl), pc24);
            if (ex.IsNaN || ex.Sign < 0) return;
            float xEnd = ex.ToFloat();
            X87Ext ey = R(X87Ext.From(y) + X87Ext.From(yl), pc24);
            if (ey.IsNaN || ey.Sign < 0) return;
            float yEnd = ey.ToFloat();
            if (!(x <= USize) || !(y <= VSize)) return;

            // 0x10417ecc-0x1041800b: the palette map (only without the colour fill).
            byte[] map = null;
            if (!colorFill)
            {
                if (!_paletteMap.TryGetValue(src.Key, out map))
                {
                    map = new byte[0x100];
                    for (int i = 0; i < 0x100; i++)
                        map[i] = BestMatch(Palette, src.Palette[4 * i], src.Palette[4 * i + 1], src.Palette[4 * i + 2], 0);
                    _paletteMap[src.Key] = map;
                }
            }

            // 0x10418013-0x104180b8: integer bounds; x0/y0 = max(fistp(X), 0) clamped to Size-1, x1/y1 = fistp of the
            // stored X+XL / Y+YL clamped to Size-1 (the last row and column are never written).
            int x0 = Math.Min(Math.Max(Fistp(x), 0), USize - 1);
            float fu = (pc24 ? X87Ext.Div24(X87Ext.From(ul), X87Ext.From(xl)) : X87Ext.Div64(X87Ext.From(ul), X87Ext.From(xl))).ToFloat();
            float u0 = R(R(R(X87Ext.From(x0) - X87Ext.From(x), pc24) * X87Ext.From(fu), pc24) + X87Ext.From(u), pc24).ToFloat();
            int x1 = Math.Min(Fistp(xEnd), USize - 1);
            int y0 = Math.Min(Math.Max(Fistp(y), 0), VSize - 1);
            float fv = (pc24 ? X87Ext.Div24(X87Ext.From(vl), X87Ext.From(yl)) : X87Ext.Div64(X87Ext.From(vl), X87Ext.From(yl))).ToFloat();
            float v0 = R(R(R(X87Ext.From(y0) - X87Ext.From(y), pc24) * X87Ext.From(fv), pc24) + X87Ext.From(v), pc24).ToFloat();
            int y1 = Math.Min(Fistp(yEnd), VSize - 1);

            // 0x104180ba-0x1041811f: 13.19 fixed point; each fmul 524288 is stored to a float before fistp.
            int du = Fistp(fu * 524288f), dv = Fistp(fv * 524288f);
            int uStart = Fistp(u0 * 524288f) - du, vAcc = Fistp(v0 * 524288f) - dv;
            int uMask = src.USize - 1, vMask = src.VSize - 1, srcU = src.USize;
            byte[] s = src.Mip0;
            if (y1 <= y0) return;
            for (int row = y0, dst = USize * y0; row < y1; row++, dst += USize)
            {
                vAcc += dv;
                int rowBase = ((vAcc >> 19) & vMask) * srcU;
                int uAcc = uStart;
                for (int col = x0; col < x1; col++)
                {
                    uAcc += du;
                    byte t = s[((uAcc >> 19) & uMask) + rowBase];
                    if (!masked)                                                   // 0x1041820b
                        Mip0[dst + col] = map is not null ? map[t] : throw new NotSupportedException(
                            "UScriptedTexture::DrawTile: colour fill without the mask reads a NULL palette map (0x10418259) — the original faults; no caller passes it");
                    else if (t != 0) Mip0[dst + col] = colorFill ? color : map[t];   // 0x10418299 / 0x10418152
                }
            }
        }

        // Original: Engine.dll ?execDrawText@UScriptedTexture@@QAEXAAUFFrame@@QAX@Z [HP1 0x10418480]
        // Original: Engine.dll ?execDrawColoredText@UScriptedTexture@@QAEXAAUFFrame@@QAX@Z [HP1 0x10418F10]
        // Glyph loop 0x104185b3-0x1041873e: remap (miss -> ' '), page = c / CharactersPerPage (idiv), skipped when
        // page >= Pages.Num, the page has no texture or the index >= Characters.Num; the page is locked when it
        // changes (the previous one unlocked first) and unlocked after the loop; DrawTile(pen + fistp(X), fistp(Y),
        // USize, VSize, StartU, StartV, USize, VSize, page texture, masked, fill); pen += USize. No kerning.
        // `color` null = DrawText (palette map), else DrawColoredText (BestMatch of the colour before the loop).
        public void DrawText(float x, float y, string text, Hp1FontMetrics font, Func<int, ScriptedTexelSource> lockPage,
                             (byte R, byte G, byte B)? color, bool pc24)
        {
            byte fill = color is { } c ? BestMatch(Palette, c.R, c.G, c.B, 0) : (byte)0;
            int ix = Fistp(x), iy = Fistp(y);
            int pen = 0, locked = -1;
            ScriptedTexelSource page = null;
            foreach (char ch in text)
            {
                if (ch == '\0') break;                                            // TCHAR loop ends at the terminator
                int code = font.Remap(ch);
                if (font.CharactersPerPage == 0)
                    throw new DivideByZeroException("UScriptedTexture text: CharactersPerPage 0 (idiv 0x104185fa) — the original faults");
                int p = code / font.CharactersPerPage;
                if (p >= font.Pages.Length || !font.Pages[p].HasTexture) continue;
                int idx = code - font.CharactersPerPage * p;
                if (idx >= font.Pages[p].Characters.Length) continue;
                if (p != locked) { locked = p; page = lockPage(p); }
                Hp1FontChar g = font.Pages[p].Characters[idx];
                DrawTile(pen + ix, iy, g.USize, g.VSize, g.StartU, g.StartV, g.USize, g.VSize, page,
                         masked: true, colorFill: color is not null, fill, pc24);
                pen += g.USize;
            }
        }

        // Original: Engine.dll ?execTextSize@UScriptedTexture@@QAEXAAUFFrame@@QAX@Z [HP1 0x10419410]
        // XL = sum of USize, YL = max VSize (from 0) over the glyphs found (no page-texture check, 0x104195b9-0x104195d8);
        // a glyph not found adds 0.
        public static void TextSize(string text, Hp1FontMetrics font, out float xl, out float yl)
        {
            int w = 0, h = 0;
            foreach (char ch in text)
            {
                if (ch == '\0') break;
                int code = font.Remap(ch);
                if (font.CharactersPerPage == 0)
                    throw new DivideByZeroException("UScriptedTexture::execTextSize: CharactersPerPage 0 (idiv 0x104195af) — the original faults");
                int p = code / font.CharactersPerPage, idx = code - font.CharactersPerPage * p;
                int gw = 0, gh = 0;
                if (p < font.Pages.Length && idx < font.Pages[p].Characters.Length)
                { gw = font.Pages[p].Characters[idx].USize; gh = font.Pages[p].Characters[idx].VSize; }
                w += gw;
                if (h < gh) h = gh;
            }
            xl = w; yl = h;
        }

        // Original: Engine.dll ?execReplaceTexture@UScriptedTexture@@QAEXAAUFFrame@@QAX@Z [HP1 0x10418930]
        // Same USize and VSize: the source's mip 0 bytes over this mip 0 (rep movsd, USize*VSize), no palette
        // change; otherwise a log line and nothing. Returns false for the log branch.
        public bool ReplaceTexture(ScriptedTexelSource src)
        {
            if (src is null || src.USize != USize || src.VSize != VSize) return false;
            Buffer.BlockCopy(src.Mip0, 0, Mip0, 0, USize * VSize);
            return true;
        }
    }
}
