// HPEngine window icon from the user's install: <install>/System/hp.ico (game files are never
// put into the package). Pure ICO parsing without SDL; SDL_SetWindowIcon is in HPEngineProgram.cs.
//
// ICO format (Win32 ICONDIR/ICONDIRENTRY): 6-byte header (0, type 1, entry count), 16-byte entries
// (width, height — 0 means 256, …, bits/pixel @6, size @8, offset @12). Entry data is a DIB without
// BITMAPFILEHEADER: BITMAPINFOHEADER (biHeight = 2·height: XOR image + AND mask), BGRA palette for
// 1/4/8 bit, rows bottom-up padded to 4 bytes, then a 1-bit AND mask (1 = transparent).
// hp.ico has no PNG entries (Vista+) — a loud refusal, not a guess.
using System;
using System.Buffers.Binary;

namespace HPEngine.Packaging
{
    public static class GameIcon
    {
        // Largest entry (by area, ties broken by more bits/pixel) -> RGBA8, top-down.
        public static (int w, int h, byte[] rgba) DecodeLargest(byte[] ico)
        {
            var s = ico.AsSpan();
            if (s.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(s) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(s[2..]) != 1)
                throw new FormatException("not an ICO: ICONDIR header");
            int n = BinaryPrimitives.ReadUInt16LittleEndian(s[4..]);
            if (n == 0) throw new FormatException("ICO without entries");
            int best = -1; long bestKey = -1;
            for (int i = 0; i < n; i++)
            {
                var e = s.Slice(6 + 16 * i, 16);
                int w = e[0] == 0 ? 256 : e[0], h = e[1] == 0 ? 256 : e[1];
                long key = (long)w * h * 64 + BinaryPrimitives.ReadUInt16LittleEndian(e[6..]);
                if (key > bestKey) { bestKey = key; best = i; }
            }
            var be = s.Slice(6 + 16 * best, 16);
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(be[8..]), off = (int)BinaryPrimitives.ReadUInt32LittleEndian(be[12..]);
            return DecodeDib(s.Slice(off, size));
        }

        static (int, int, byte[]) DecodeDib(ReadOnlySpan<byte> d)
        {
            if (d.Length >= 8 && d[0] == 0x89 && d[1] == (byte)'P') throw new NotSupportedException("PNG ICO entry not supported");
            int hdr = (int)BinaryPrimitives.ReadUInt32LittleEndian(d);
            int w = BinaryPrimitives.ReadInt32LittleEndian(d[4..]), h = BinaryPrimitives.ReadInt32LittleEndian(d[8..]) / 2;
            int bpp = BinaryPrimitives.ReadUInt16LittleEndian(d[14..]);
            int comp = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[16..]);
            int clrUsed = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[32..]);
            if (comp != 0) throw new NotSupportedException($"DIB compression {comp} not supported");
            if (bpp is not (1 or 4 or 8 or 24 or 32)) throw new NotSupportedException($"{bpp} bits/pixel not supported");
            int pal = bpp <= 8 ? (clrUsed != 0 ? clrUsed : 1 << bpp) : 0;
            int xorStride = (w * bpp + 31) / 32 * 4, andStride = (w + 31) / 32 * 4;
            var palette = d.Slice(hdr, pal * 4);
            var xor = d.Slice(hdr + pal * 4, xorStride * h);
            var and = d.Slice(hdr + pal * 4 + xorStride * h, andStride * h);
            var rgba = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                var row = xor.Slice((h - 1 - y) * xorStride, xorStride);   // bottom-up
                var mrow = and.Slice((h - 1 - y) * andStride, andStride);
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    byte b, g, r, a = 255;
                    if (bpp <= 8)
                    {
                        int bit = x * bpp, idx = (row[bit >> 3] >> (8 - bpp - (bit & 7))) & ((1 << bpp) - 1);
                        b = palette[idx * 4]; g = palette[idx * 4 + 1]; r = palette[idx * 4 + 2];
                    }
                    else
                    {
                        int p = x * (bpp / 8);
                        b = row[p]; g = row[p + 1]; r = row[p + 2];
                        if (bpp == 32) a = row[p + 3];
                    }
                    if (bpp < 32 && (mrow[x >> 3] >> (7 - (x & 7)) & 1) != 0) a = 0;   // at 32 bit, transparency is the alpha
                    rgba[o] = r; rgba[o + 1] = g; rgba[o + 2] = b; rgba[o + 3] = a;
                }
            }
            return (w, h, rgba);
        }
    }
}
