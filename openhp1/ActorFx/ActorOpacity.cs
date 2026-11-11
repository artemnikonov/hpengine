// Actor.Opacity < 1 AND THE BLEND MODE OF A MESH SLOT: render state, vertex alpha
// (`Opacity < 1 ? Opacity : 1`), shader output (`texel.a * opacity`) and the palette for translucent
// drawing (OpacityDraw).
//
// Shipped: URender::DrawLodMesh with Opacity < 1 adds 0x10000004
// (PF_Highlighted | PF_Translucent, Render.dll.c:14568) and writes vertex alpha = Opacity (:14874);
// D3DDrv SetBlending for 0x10000000 — SRCALPHA/INVSRCALPHA ahead of the translucent/modulated branches, the mask
// is removed (D3DDrv.dll.c:788-822). Branch order — D3DDrv SetBlending (D3DDrv.dll.c:803-824): alpha blend
// -> translucent (`& 4` is checked before `& 0x40`) -> modulated -> mask -> opaque. The Unity reference
// put modulated before translucent — a divergence from D3DDrv (D-6).
namespace OpenHP1.ActorFx
{
    public enum ActorFxBlendMode { Opaque, Masked, Translucent, Modulated, AlphaBlend }

    // Blend factors as D3DBLEND (D3D7 values: ZERO 1, ONE 2, SRCCOLOR 3, INVSRCCOLOR 4,
    // SRCALPHA 5, INVSRCALPHA 6, DESTCOLOR 9) — the OpenHP1 renderer has its own enums, mapped
    // by name, not by number.
    public enum ActorFxFactor { Zero = 1, One = 2, SrcColor = 3, InvSrcColor = 4, SrcAlpha = 5, InvSrcAlpha = 6, DestColor = 9 }

    public readonly struct ActorFxBlend
    {
        public readonly ActorFxBlendMode Mode;
        public readonly ActorFxFactor Src, Dst;
        public readonly bool ZWrite;
        public readonly bool AlphaTest;        // alpha test on: discard unless base.a > Cutoff
        public readonly float Cutoff;          // 127/255: D3DRS_ALPHAREF 0x7f, D3DCMP_GREATER (passes when a > Cutoff)
        public readonly bool TransparentQueue; // drawn after opaque geometry
        // The slot texture uses the palette alpha (OpacityDraw) instead of the regular one.
        public readonly bool OpacityPalette;
        // Masked palette: entry 0 = (0,0,0,0) (MaskedDraw / OpacityDraw with masked).
        public readonly bool MaskedPalette;

        public ActorFxBlend(ActorFxBlendMode mode, ActorFxFactor src, ActorFxFactor dst, bool zwrite, bool alphaTest,
            bool transparentQueue, bool opacityPalette, bool maskedPalette)
        {
            Mode = mode; Src = src; Dst = dst; ZWrite = zwrite; AlphaTest = alphaTest; Cutoff = alphaTest ? 127f / 255f : 0f;
            TransparentQueue = transparentQueue; OpacityPalette = opacityPalette; MaskedPalette = maskedPalette;
        }
    }

    public static class ActorOpacity
    {
        public const int PF_Masked = 0x2, PF_Translucent = 0x4, PF_Modulated = 0x40;
        const int StyleMasked = 2, StyleTranslucent = 3, StyleModulated = 4;   // Render.dll.c:9729-9737

        // StyleFlags — actor style bits; STY_Modulated carries its own bit separately.
        static int StyleFlags(int style) => style == StyleMasked ? PF_Masked : style == StyleTranslucent ? PF_Translucent : 0;

        // Resolve (render-state part). slotFlags — PolyFlags of the mesh slot material.
        public static ActorFxBlend Resolve(int style, int slotFlags, float opacity)
        {
            int render = slotFlags | StyleFlags(style);
            bool translucent = (render & PF_Translucent) != 0;
            // PF_Modulated comes from the style (FUN_10b338f0, Render.dll.c:39783) OR from the slot material flags: triangle
            // flags = material | actor flags (Render.dll.c:9380, 14616, 14933).
            bool modulated = style == StyleModulated || (slotFlags & PF_Modulated) != 0;
            // Core's flag word (MeshTriangleOrder.ActorPolyFlags: `fcomp 1.0`, C0 also for NaN) decides AlphaBlend.
            bool alphaBlend = (HP1.UEScript.MeshTriangleOrder.ActorPolyFlags(style, opacity) & 0x10000000) != 0;
            bool masked = (render & PF_Masked) != 0;
            if (alphaBlend)
                // texture — OpacityDraw(masked).
                return new ActorFxBlend(ActorFxBlendMode.AlphaBlend, ActorFxFactor.SrcAlpha, ActorFxFactor.InvSrcAlpha,
                    zwrite: false, alphaTest: false, transparentQueue: true, opacityPalette: true, maskedPalette: masked);
            if (translucent)
                return new ActorFxBlend(ActorFxBlendMode.Translucent, ActorFxFactor.One, ActorFxFactor.InvSrcColor,
                    false, false, true, false, false);
            if (modulated)
                // The slot mask under Modulated stays an alpha test (D3DDrv SetBlending removes it only under
                // Translucent, D3DDrv.dll.c:787-791): index 0 is cut out, texture — MaskedDraw.
                return new ActorFxBlend(ActorFxBlendMode.Modulated, ActorFxFactor.DestColor, ActorFxFactor.SrcColor,
                    false, masked, true, false, masked);
            if (masked)
                // texture — MaskedDraw (masked && !translucent && !modulated).
                return new ActorFxBlend(ActorFxBlendMode.Masked, ActorFxFactor.One, ActorFxFactor.Zero,
                    true, true, false, false, true);
            return new ActorFxBlend(ActorFxBlendMode.Opaque, ActorFxFactor.One, ActorFxFactor.Zero,
                true, false, false, false, false);
        }

        // Vertex alpha. Pixel: alpha = texel.a · VertexAlpha.
        public static float VertexAlpha(float opacity) => opacity < 1f ? opacity : 1f;

        // With opacityPalette: palette RGBA as is, alpha = the palette entry's alpha
        // (D3DDrv P8->8888, FUN_10002490); with masked, entry 0 = (0,0,0,0).
        // masked = slot mask (Resolve) OR the texture's own bMasked (`masked || cd.Masked`).
        // paletteRgba — 256 entries R,G,B,A.
        public static byte[] OpacityPalette(ReadOnlySpan<byte> paletteRgba, bool masked)
        {
            if (paletteRgba.Length < 256 * 4) throw new ArgumentException($"palette {paletteRgba.Length} bytes, 1024 required");
            var pal = paletteRgba.Slice(0, 256 * 4).ToArray();
            if (masked) { pal[0] = 0; pal[1] = 0; pal[2] = 0; pal[3] = 0; }
            return pal;
        }
    }
}
