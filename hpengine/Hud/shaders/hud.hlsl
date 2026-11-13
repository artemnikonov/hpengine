// HPEngine 2D-frame shader (HUD, menu, loading screen, EndFlash flash, gamma ramp).
// Single source; SPIR-V (Vulkan), DXIL (D3D12) and MSL (Metal, via SPIRV-Cross from SPIR-V) are built by
// Hud/shaders/build.sh, same convention as shaders/classic.hlsl.
//
// Bindings per the SDL_GPU convention (SDL_CreateGPUShader):
//   SPIR-V: vertex uniform set 1; fragment: texture (combined image sampler) set 2, uniform set 3.
//           DXIL: vertex uniform b0 space1; fragment: t0/s0 space2, uniform b0 space3;
//           vertex inputs use semantic TEXCOORD{location}.
//   MSL:    index = binding (spirv-cross --msl-decoration-binding): buffer(0), texture/sampler(0).
// Vertex (stride 32 bytes): pos float2 in screen pixels (0,0 top-left, Y down),
// uv float2 in texture fractions (0,0 = first uploaded row = top), color float4 = vertex colour.
// Vertex uniform: screen = (width, height, 0, 0) of the target in pixels.
// Fragment uniform: mode.x = branch, mode.y = alpha test (1/0), mode.z = gamma exponent.
//
// Target and textures are UNORM without sRGB: a texel is the authored byte, output is the frame byte, so all
// formulas are in gamma space:
//   branch 0, CanvasTile: saturate(t·color) in bytes; alpha t.a·color.a.
//       The same branch over a white texture draws the FillBlack fill and the flash quad
//       (output = quad colour: saturate(1·rgb), 1·a).
//   branch 1, ParticleAdd (PF_Translucent): (t.rgb·color.rgb, 1).
//   branch 2, SpriteModulate (PF_Modulated): alpha test clip(t.a − 0.5) under PF_Masked, output (t.rgb, 1).
//       In a gamma target DstColor/SrcColor blending itself yields 2·s·d, so no extra multiplier.
//   branch 3, GammaRamp: c = round(pow(saturate(c), e)·65535)/65535, alpha 1. The frame is read as a texture
//       with the same top, no flip needed.
// Blending (factor pairs) is pipeline state, set by HudBlendStates.Factors.

struct VIn
{
    [[vk::location(0)]] float2 pos   : TEXCOORD0;
    [[vk::location(1)]] float2 uv    : TEXCOORD1;
    [[vk::location(2)]] float4 color : TEXCOORD2;
};

struct VOut
{
    float4 pos : SV_Position;
    [[vk::location(0)]] float2 uv    : TEXCOORD0;
    [[vk::location(1)]] float4 color : TEXCOORD1;
};

[[vk::binding(0, 1)]] cbuffer VU : register(b0, space1) { float4 screen; };
[[vk::binding(0, 3)]] cbuffer FU : register(b0, space3) { float4 mode; };

[[vk::combinedImageSampler]] [[vk::binding(0, 2)]] Texture2D<float4> tex : register(t0, space2);
[[vk::combinedImageSampler]] [[vk::binding(0, 2)]] SamplerState smp : register(s0, space2);

VOut vs_main(VIn i)
{
    VOut o;
    // Pixels -> SDL_GPU NDC (+Y up): top-left of the screen is (−1, +1).
    o.pos = float4(i.pos.x / screen.x * 2.0 - 1.0, 1.0 - i.pos.y / screen.y * 2.0, 0.0, 1.0);
    o.uv = i.uv;
    o.color = i.color;
    return o;
}

float4 fs_main(VOut i) : SV_Target0
{
    float4 t = tex.Sample(smp, i.uv);
    if (mode.x > 2.5)
    {
        float3 c = round(pow(saturate(t.rgb), mode.z) * 65535.0) / 65535.0;
        return float4(c, 1.0);
    }
    if (mode.x > 1.5)
    {
        if (mode.y > 0.5) clip(t.a - 0.5);
        return float4(t.rgb, 1.0);
    }
    if (mode.x > 0.5)
        return float4(t.rgb * i.color.rgb, 1.0);
    return float4(saturate(t.rgb * i.color.rgb), t.a * i.color.a);
}
