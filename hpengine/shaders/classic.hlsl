// The only HPEngine shader source. SPIR-V (Vulkan), DXIL (D3D12) and MSL (Metal, via SPIRV-Cross from SPIR-V)
// are built from it by shaders/build.sh.
//
// Bindings follow the SDL_GPU convention (SDL_CreateGPUShader):
//   SPIR-V: vertex uniform — set 1; fragment: textures (combined image sampler) — set 2,
//           uniforms — set 3 (binding 0 = FU). DXIL: vertex uniform — b0 space1;
//           fragment: t/s — space2, uniforms — b0 space3; vertex inputs — TEXCOORD{location}.
//   MSL:    index = binding (spirv-cross --msl-decoration-binding): buffer(0), texture/sampler(0..2).
// texture/sampler pairs: base = 0, lightmap = 1, fog = 2. Vertex (SdlWorldMesh.Vertex): pos float3,
// uv float2, uv2 float2, light float3 (actor vertex light), uv3 float2 (fog-atlas texel),
// fog float3 (actor vertex fog, specular byte / 255), alpha float (actor Opacity) — 64 bytes.
//
// One-to-one port of the MSL in SdlShaders.cs: formulas, constants, order.
// dfdx/dfdy -> ddx/ddy, select(a, b, c) MSL -> select(c, b, a) HLSL 2021, discard_fragment -> discard.
//
// Classic BSP fragment (lightmap branch):
//   frame = saturate(texel · lightmap_byte / 63.75): D3DDrv uploads the RGBA7 lightmap shifted ×2 (FUN_100022c0)
//   and applies it as a second MODULATE2X stage (SetBlending 0x01000000) — 2·t·(2·l7)/255 = t·l7/63.75 (live original:
//   stage 1 COLOROP 5 = MODULATE2X, ARG1 TEXTURE, ARG2 CURRENT; stage 0 MODULATE with white vertex colour — hp1drawlog
//   detail=1). A divisor of 64 would be 0.4 % (≤ 1 byte) too dark.
// Texture and target are UNORM without sRGB: the texel is the authored byte, the output is the frame byte.
// Mip bias −0.5 — D3DTSS_MIPMAPLODBIAS 0xbf000000 (D3DDrv 0x1000ae32/0x1000af47).

struct VIn
{
    [[vk::location(0)]] float3 pos   : TEXCOORD0;
    [[vk::location(1)]] float2 uv    : TEXCOORD1;
    [[vk::location(2)]] float2 uv2   : TEXCOORD2;
    [[vk::location(3)]] float3 light : TEXCOORD3;
    [[vk::location(4)]] float2 uv3   : TEXCOORD4;
    [[vk::location(5)]] float3 fog   : TEXCOORD5;
    [[vk::location(6)]] float  alpha : TEXCOORD6;
};

struct VOut
{
    float4 pos : SV_Position;
    [[vk::location(0)]] float2 uv    : TEXCOORD0;
    [[vk::location(1)]] float2 uv2   : TEXCOORD1;
    [[vk::location(2)]] float3 light : TEXCOORD2;
    [[vk::location(3)]] float2 uv3   : TEXCOORD3;
    [[vk::location(4)]] float3 wpos  : TEXCOORD4;
    [[vk::location(5)]] float3 fog   : TEXCOORD5;
    [[vk::location(6)]] float  alpha : TEXCOORD6;
};

// Clip matrix rows (Camera.Rows).
[[vk::binding(0, 1)]] cbuffer VU : register(b0, space1) { float4 r0; float4 r1; float4 r2; float4 r3; };

// mode.x: 0 lightmap, 1 texel, 2 vertex light (gamma), 3 fog map pass, 4 mirror;
// mode.y: mask; mode.z/w: debug (HP1_DBG=red|tex|lm). uvoff.xy — clock-driven surface UV pan
// (SurfacePan), uvoff.z — surface bSpecialLit pair (mode 0) / actor mesh vertex-fog admission
// (mode 2: 1 — Opaque/Masked slot, 0 — Translucent/Modulated/AlphaBlend), uvoff.w — 1 for an
// AlphaBlend batch (Opacity < 1: alpha output t.a·alpha), else alpha 1. clip — clip plane of the reflected
// pass (0 — none). screen.xy — 1/target size; screen.z — 1: the fog map is drawn in its own pass (mode 3), not folded.
[[vk::binding(0, 3)]] cbuffer FU : register(b0, space3) { float4 mode; float4 uvoff; float4 clip; float4 screen; };

// Spawned lights on BSP surfaces (C-1): pos.xyz+R, rgb+inner,
// x=nonIncidence y=bSpecialLit.

[[vk::combinedImageSampler]] [[vk::binding(0, 2)]] Texture2D<float4> base : register(t0, space2);
[[vk::combinedImageSampler]] [[vk::binding(0, 2)]] SamplerState sb : register(s0, space2);
[[vk::combinedImageSampler]] [[vk::binding(1, 2)]] Texture2D<float4> lm : register(t1, space2);
[[vk::combinedImageSampler]] [[vk::binding(1, 2)]] SamplerState sl : register(s1, space2);
[[vk::combinedImageSampler]] [[vk::binding(2, 2)]] Texture2D<float4> fogt : register(t2, space2);
[[vk::combinedImageSampler]] [[vk::binding(2, 2)]] SamplerState sf : register(s2, space2);

VOut vs_main(VIn i)
{
    float4 p = float4(i.pos, 1.0);
    VOut o;
    o.pos = float4(dot(r0, p), dot(r1, p), dot(r2, p), dot(r3, p));
    o.uv = i.uv; o.uv2 = i.uv2; o.light = i.light; o.uv3 = i.uv3; o.wpos = i.pos;
    o.fog = i.fog;
    o.alpha = i.alpha;
    return o;
}

float4 fs_main(VOut i) : SV_Target0
{
    if (any(clip != 0.0) && dot(clip.xyz, i.wpos) + clip.w < 0.0) discard;
    // Mirror: colour of the reflected pass in screen coordinates; the surface texture is not drawn.
    if (mode.x > 3.5) return float4(base.Sample(sb, i.pos.xy * screen.xy).rgb, 1.0);
    // Fog map pass: D3DDrv DrawComplexSurface SetBlending(0x10000000) = ONE/INVSRCALPHA with the fog map in stage 0
    // (D3DDrv.dll.c:6258-6265), over the frame the surface pass wrote; the pipeline blends.
    if (mode.x > 2.5) return fogt.Sample(sf, i.uv3);
    if (mode.z > 0.5) return float4(1.0, 0.0, 0.0, 1.0);
    if (mode.z < -0.5) return float4(base.SampleBias(sb, i.uv, -0.5).rgb, 1.0);
    if (mode.w > 0.5) return float4(lm.Sample(sl, i.uv2).rgb * 2.0, 1.0);
    float4 t = base.SampleBias(sb, i.uv + uvoff.xy, -0.5);
    // Alpha test: D3DRS_ALPHAREF 0x7f with D3DCMP_GREATER (RenderRules.AlphaTest) — the texel passes when a > 127/255.
    if (mode.y > 0.5 && !(t.a > 127.0 / 255.0)) discard;
    float3 c = t.rgb;
    // Actor mesh (gain = 1): lit — in gamma, saturate(texel · light).
    // Vertex fog (C4, D3D specular) is added in gamma AFTER light modulation, without (1 − Fog.a) and
    // without gain; only for slots with PF_RenderFog admitted — D3DDrv SetBlending/AdjustPolyFlags drop it
    // under Translucent/Modulated, and Opacity < 1 carries Translucent. Alpha: base.a · Opacity.
    float fa = uvoff.w > 0.5 ? t.a * i.alpha : 1.0;
    float3 vfog = i.fog * uvoff.z;
    if (mode.x > 1.5) return float4(saturate(c * i.light + vfog), fa);
    if (mode.x < 0.5)
    {
        float3 l = lm.Sample(sl, i.uv2).rgb * 255.0;
        // Zone fog over the lit surface (outside fog zones uv3 = 0 — an empty texel, no-op).
        float4 fg = fogt.Sample(sf, i.uv3);
        // Dynamic lights are already in the lightmap (Core: the draw record's +0x48 records, LevelSurfLightmap).
        float3 lit = c * l / 63.75;
        // Fog map is a separate D3DDrv pass SetBlending(0x10000000) = ONE/INVSRCALPHA over the already written
        // (clamped) frame (D3DDrv.dll.c:6260-6265): base·lightmap is clamped BEFORE fog.
        lit = saturate(lit);
        if (screen.z < 0.5 && any(fg != 0.0)) lit = lit * (1.0 - fg.a) + fg.rgb;
        c = saturate(lit);
    }
    return float4(c, 1.0);
}
