// Water kernel of UWaterTexture::CalculateWater (Fire.dll FUN_1050ee60 -> 0x10516550 for
// even WaterParity, 0x10516dc0 for odd; Fire.dll.c:2556-2568). The shipped kernel is
// self-modifying assembly the decompiler cannot read; the closed form below was
// derived by sensitivity probing (which input affects which output, table indices
// with identity RT/WT) and checked byte-for-byte against the real body under unicorn
// (se_harness/bsp_diff/wet_oracle.py, validate_wet_texture.py: kernel section — random
// fields and tables, sizes 8..256, both parities; plus a full WetTexture tick).
//
// src — two W/2 × H fields interleaved by rows. p = parity & 1:
// * the field of rows with parity p is updated (in place; only rows of the other
//   parity and the cell itself are read):
//     new = WT[0x200 + n(y-1,x0) + n(y-1,x1) + n(y+1,x0) + n(y+1,x1) − 2·old],
//     x0 = x − 1 + p, x1 = x0 + 1 (rows and columns wrap);
// * the W × H mip is built from the field that is NOT updated (rows r of parity 1 − p):
//     h_k = S(r, k+1) − S(r, k−1)                         -> mip row r − 1,
//     h_k = (S(r,k+1) − S(r,k−1) + S(r+2,k+1) − S(r+2,k−1)) >> 1  -> mip row r,
//   in the row pixel (2k + 2 − p) = RT[0x200 + 2·h_k], the next = RT[0x200 + h_k + h_{k+1}]
//   (pixel columns wrap by W). The >> 1 shift is arithmetic (sar).
namespace HP1.UEScript
{
    public static class WaterKernel
    {
        // mip — W×H (output: refraction table indices), src — SourceFields W/2×H,
        // rt — RenderTable (0x404), wt — WaterTable (0x600), parity — WaterParity.
        public static void Calculate(byte[] mip, byte[] src, byte[] rt, byte[] wt, int w, int h, int parity)
        {
            int w2 = w >> 1, p = parity & 1, wm = w - 1;
            System.Span<int> hs = w2 <= 512 ? stackalloc int[w2] : new int[w2];
            System.Span<int> hp = w2 <= 512 ? stackalloc int[w2] : new int[w2];
            for (int r = 1 - p; r < h; r += 2)
            {
                int a = r * w2, b = ((r + 2) % h) * w2;
                for (int k = 0; k < w2; k++)
                {
                    int kn = (k + 1) % w2, kp = (k - 1 + w2) % w2;
                    hs[k] = src[a + kn] - src[a + kp];
                    hp[k] = (hs[k] + src[b + kn] - src[b + kp]) >> 1;
                }
                int single = ((r - 1 + h) % h) * w, pair = r * w;
                for (int k = 0; k < w2; k++)
                {
                    int kn = (k + 1) % w2, x = (2 * k + 2 - p) & wm, x1 = (x + 1) & wm;
                    mip[single + x] = rt[0x200 + 2 * hs[k]]; mip[single + x1] = rt[0x200 + hs[k] + hs[kn]];
                    mip[pair + x] = rt[0x200 + 2 * hp[k]]; mip[pair + x1] = rt[0x200 + hp[k] + hp[kn]];
                }
            }
            for (int y = p; y < h; y += 2)
            {
                int c = y * w2, u = ((y - 1 + h) % h) * w2, d = ((y + 1) % h) * w2;
                for (int x = 0; x < w2; x++)
                {
                    int x0 = (x - 1 + p + w2) % w2, x1 = (x0 + 1) % w2;
                    src[c + x] = wt[0x200 + src[u + x0] + src[u + x1] + src[d + x0] + src[d + x1] - 2 * src[c + x]];
                }
            }
        }
    }
}
