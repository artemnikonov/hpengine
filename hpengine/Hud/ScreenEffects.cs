// FRAME SCREEN EFFECTS: flash/fade (EndFlash) and the device gamma ramp.
//
// Gamma ramp per the D3DDrv disassembly (UD3DRenderDevice::Flush, see GammaExponent); the pass is Renderer.Draw.
// Flash state is computed by Core with the original bytecode (PlayerPawn.ViewFlash and the setters
// ClientFlash/ClientFadeIn/...) and handed over in the frame as WorldFrame.ViewFlash. Here it only becomes a quad.
using HP1.UEScript;

namespace HPEngine.Hud
{
    // Colour and alpha of the full-screen EndFlash quad. Draw = false means the quad is not drawn.
    public readonly struct FlashOverlay
    {
        public readonly bool Draw;
        public readonly float R, G, B, A;
        public FlashOverlay(bool draw, float r, float g, float b, float a) { Draw = draw; R = r; G = g; B = b; A = a; }
    }

    public static class ScreenEffects
    {
        // The quad colour and alpha are Core's (ViewFlashState.EndFlash: UGameEngine::Draw clamps + EndFlash alpha).
        public static FlashOverlay ViewFlash(in ViewFlashState vf)
            => vf.EndFlash(out float r, out float g, out float b, out float a) ? new FlashOverlay(true, r, g, b, a) : default;

        // Full-screen flash quad: white texture, vertex colour = (rgb, alpha), blend One/SrcAlpha ⇒
        // screen' = rgb + screen·alpha. Place in the frame: Overlay. null means do not draw.
        // 2D-frame order of UGameEngine::Draw (Engine.dll.c:117947-117956): Render->DrawWorld (vtbl +0x74; world and
        // coronas), then RenDev vtbl +0xb0 = UD3DRenderDevice::EndFlash (table 0x1001a010, slot +0xb0 =
        // 0x10001087), then eventPostRender (HUD, menu, console). The flash lies over world and coronas, HUD over it.
        // Corona tile (UCanvas::DrawIcon, PolyFlags 0x104 -> Translucent, no depth) as a quad of this pass. Bilinear unless
        // PF_NoSmooth, as D3DDrv DrawTile (MAG/MINFILTER LINEAR at 0x1000ae9b/0x1000aeaa, POINT only for PF_NoSmooth).
        public static HudQuad CoronaQuad(float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1,
            float r, float g, float b, string texture, bool noSmooth)
            => new HudQuad(x0, y0, x1, y1, u0, v0, u1, v1, r, g, b, 1f, HudBlend.Translucent, false, texture,
                HudQuadSource.Tile, -1, smooth: !noSmooth);

        public static void Overlay(List<HudQuad> dst, IEnumerable<HudQuad> coronas, HudQuad? flash, IReadOnlyList<HudQuad> hud)
        {
            dst.Clear();
            dst.AddRange(coronas);
            if (flash.HasValue) dst.Add(flash.Value);
            dst.AddRange(hud);
        }

        public static HudQuad? FlashQuad(in ViewFlashState vf, int screenW, int screenH)
        {
            FlashOverlay o = ViewFlash(vf);
            if (!o.Draw) return null;
            return new HudQuad(0, 0, screenW, screenH, 0f, 0f, 1f, 1f, o.R, o.G, o.B, o.A,
                HudBlend.Flash, false, null, HudQuadSource.Flash, -1);
        }

        // D3DDrv GAMMA RAMP: menu Brightness (FEOptionsPage.BrightnessChanged: `set ini:… Brightness
        // <slider/10>` + `FLUSH`). UD3DRenderDevice::Flush, D3DDrv disassembly 0x10002f42..0x10002fd6:
        //   if a gamma control exists ([this+0x9a8], created under DDCAPS2_PRIMARYGAMMA, 0x1000d669..0x1000d695;
        //   UseGammaCorrection @0x9bc is only copied in the disassembly, it is not a condition):
        //   d = (float)(Viewport->Client->Brightness(+0x70) · 2.5f [0x1001a474]);
        //   for i = 0..255: ramp[i] = fistp((float)(pow(i · (1/255)f [0x1001a46c], 1.0 [0x1001a470] / d)
        //                                          · 65535.0 [0x1001a460]));
        //   one ramp for R/G/B -> IDirectDrawGammaControl::SetGammaRamp (vtbl +0x10). Affects the whole output
        //   frame (world, HUD, menu). The original never sees Brightness ≤ 0 (slider 2..10 / 10).
        public static float GammaExponent(float brightness)
        {
            if (!(brightness > 0f)) return 1f;
            float d = brightness * 2.5f;
            return (float)(1.0 / d);
        }

        // The gamma pass is needed if the exponent is not identity (retail 0.4 -> 1/(0.4·2.5) = 1).
        public static bool GammaActive(float brightness)
            => MathF.Abs(GammaExponent(brightness) - 1f) >= 1e-6f;

        // 16-bit device ramp by the same chain (fistp rounds to even). The exponent is double here, as in the
        // original (fdiv in x87, fstp qword); the pass shader (Hud/shaders branch 3) computes the same
        // pow(c, e)·65535 in float32 over the frame's 8-bit texel.
        public static ushort[] GammaRamp16(float brightness)
        {
            double e = brightness > 0f ? 1.0 / (float)(brightness * 2.5f) : 1.0;
            var ramp = new ushort[256];
            for (int i = 0; i < 256; i++)
                ramp[i] = (ushort)MathF.Round((float)(Math.Pow(i * (double)(1f / 255f), e) * 65535.0), MidpointRounding.ToEven);
            return ramp;
        }
    }
}
