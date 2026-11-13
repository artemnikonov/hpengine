// A ready 2D-frame batch: screen rectangle, texture window, vertex colour and blend mode. The Hud module does not
// call the GPU: the render backend draws these quads in list order.
namespace HPEngine.Hud
{
    // Quad blend mode. Factor pairs (src, dst) are the same for colour and alpha.
    public enum HudBlend
    {
        // CanvasTile without mask: One / Zero (alphaBlend=false).
        Opaque,
        // CanvasTile with PF_Masked: SrcAlpha / OneMinusSrcAlpha.
        AlphaBlend,
        // PF_Translucent (4): ParticleAdd, One / OneMinusSrcColor (D3DDrv.dll.c:815-820).
        Translucent,
        // PF_Modulated (0x40): DstColor / SrcColor.
        Modulated,
        // Full-screen EndFlash quad: One / SrcAlpha.
        Flash,
    }

    // Origin of the quad, for logs and tests; does not affect drawing.
    public enum HudQuadSource { Fill, Tile, Glyph, Flash, Gamma }

    // Blend factors named as SDL_GPUBlendFactor (1:1).
    public enum HudBlendFactor { Zero, One, SrcColor, OneMinusSrcColor, DstColor, SrcAlpha, OneMinusSrcAlpha }

    public static class HudBlendStates
    {
        // (src, dst) for colour and for alpha; the operation is add (there is no other).
        public static (HudBlendFactor Src, HudBlendFactor Dst) Factors(HudBlend b) => b switch
        {
            HudBlend.Opaque => (HudBlendFactor.One, HudBlendFactor.Zero),
            HudBlend.AlphaBlend => (HudBlendFactor.SrcAlpha, HudBlendFactor.OneMinusSrcAlpha),
            HudBlend.Translucent => (HudBlendFactor.One, HudBlendFactor.OneMinusSrcColor),
            HudBlend.Modulated => (HudBlendFactor.DstColor, HudBlendFactor.SrcColor),
            HudBlend.Flash => (HudBlendFactor.One, HudBlendFactor.SrcAlpha),
            _ => throw new System.ArgumentOutOfRangeException(nameof(b), b, "unknown HUD blend mode"),
        };

        // Fragment shader branch of hud.hlsl (uniform mode.x): 0 = CanvasTile, 1 = ParticleAdd, 2 = SpriteModulate.
        // Flash is drawn by the CanvasTile branch over a white texture: saturate(1·rgb), 1·a, exactly the quad colour.
        public static int ShaderMode(HudBlend b) => b switch
        {
            HudBlend.Opaque or HudBlend.AlphaBlend or HudBlend.Flash => 0,
            HudBlend.Translucent => 1,
            HudBlend.Modulated => 2,
            _ => throw new System.ArgumentOutOfRangeException(nameof(b), b, "unknown HUD blend mode"),
        };
    }

    public readonly struct HudQuad
    {
        // Screen pixels, origin top-left, Y down (as the UE1 canvas).
        public readonly float X0, Y0, X1, Y1;
        // Texture window in fractions of the LOGICAL size (USize/VSize of mip 0), origin at the top-left texel:
        // V0 is the quad's top edge, V1 the bottom (UE row 0 is uploaded first, SdlRenderer.UploadLevels).
        public readonly float U0, V0, U1, V1;
        // Vertex colour 0..1.
        public readonly float R, G, B, A;
        public readonly HudBlend Blend;
        // Alpha test (discard when a < 0.5) only on the modulated path.
        public readonly bool AlphaClip;
        // UE texture path (font page for a glyph); null means white 1×1.
        public readonly string Texture;
        public readonly HudQuadSource Source;
        // Index of the HudCommand in frame.Hud; -1 means the quad does not come from a command.
        public readonly int Command;
        // Bilinear sampling. 3D-world tiles drawn through this pass (coronas) sample as D3DDrv DrawTile: MAG/MINFILTER
        // LINEAR (0x1000ae9b/0x1000aeaa) unless PF_NoSmooth; UI quads keep the nearest sampler.
        public readonly bool Smooth;

        public HudQuad(float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1,
            float r, float g, float b, float a, HudBlend blend, bool alphaClip, string texture,
            HudQuadSource source, int command, bool smooth = false)
        {
            X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; U0 = u0; V0 = v0; U1 = u1; V1 = v1;
            R = r; G = g; B = b; A = a; Blend = blend; AlphaClip = alphaClip; Texture = texture;
            Source = source; Command = command; Smooth = smooth;
        }

        public override string ToString() =>
            $"{Source}#{Command} [{X0},{Y0}..{X1},{Y1}] uv[{U0},{V0}..{U1},{V1}] rgba({R},{G},{B},{A}) {Blend}{(AlphaClip ? "+clip" : "")} {Texture ?? "<white>"}";
    }
}
