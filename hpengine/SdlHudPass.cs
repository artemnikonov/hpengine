// 2D-frame pass (HUD, menu, 4:3 sides, flash): hpengine/Hud wired into the SDL_GPU renderer. Shader
// hud.{vert,frag}.{msl,spv,dxil} (blob per device format, SdlShaders), pipeline per HudBlend (ADD, HudBlendStates
// factors), 32-byte vertex (pos px, uv, color), no depth and no culling; vertex uniform (W, H), fragment uniform
// (ShaderMode, AlphaClip, 0, 0). Textures: a regular package texture (A = 255, index 0 has A = 0, under bMasked
// entry 0 = (0,0,0,0), point filtering); a font page is RGB 255, A = max(r,g,b); no texture means white 1×1.
// Point sampling, REPEAT.
// Gamma ramp (Brightness, D3DDrv Flush 0x10002f42): DrawGamma, branch 3 of the same shader over a frame copy.
// Not wired: the loading screen during travel, coronas.
using System.Runtime.InteropServices;
using HP1.UEScript;
using HPEngine.Hud;
using SDL;
using static SDL.SDL3;

namespace HPEngine
{
    sealed unsafe partial class Renderer
    {
        SDL_GPUGraphicsPipeline*[] _hudPipe;
        SDL_GPUSampler* _hudSampler, _hudLinear;
        SDL_GPUTexture* _hudWhite;
        SDL_GPUBuffer* _hvb; uint _hvbCap;
        readonly Dictionary<string, nint> _hudTex = new(StringComparer.OrdinalIgnoreCase);
        readonly List<(int first, int count, HudBlend blend, bool clip, nint tex, bool smooth)> _hudRuns = new();
        float[] _hudVerts = Array.Empty<float>();
        int _hudVertCount;
        public int HudQuads, HudMissing;

        public (int W, int H) PixelSize()
        {
            if (Window == null || Offscreen) return (Width, Height);
            int w, h; SDL_GetWindowSizeInPixels(Window, &w, &h);
            return (w, h);
        }

        bool EnsureHud()
        {
            if (_hudPipe != null) return true;
            var vs = ShaderFrom(Shaders.Load("hud", "vert", _shaderFmt), "vs_main", SDL_GPUShaderStage.SDL_GPU_SHADERSTAGE_VERTEX, 0, 1);
            var fs = ShaderFrom(Shaders.Load("hud", "frag", _shaderFmt), "fs_main", SDL_GPUShaderStage.SDL_GPU_SHADERSTAGE_FRAGMENT, 1, 1);
            if (vs == null || fs == null) return false;
            var attrs = stackalloc SDL_GPUVertexAttribute[3];
            attrs[0] = new SDL_GPUVertexAttribute { location = 0, format = SDL_GPUVertexElementFormat.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT2, offset = 0 };
            attrs[1] = new SDL_GPUVertexAttribute { location = 1, format = SDL_GPUVertexElementFormat.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT2, offset = 8 };
            attrs[2] = new SDL_GPUVertexAttribute { location = 2, format = SDL_GPUVertexElementFormat.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT4, offset = 16 };
            var vbd = new SDL_GPUVertexBufferDescription { slot = 0, pitch = 32, input_rate = SDL_GPUVertexInputRate.SDL_GPU_VERTEXINPUTRATE_VERTEX };
            var blends = (HudBlend[])Enum.GetValues(typeof(HudBlend));
            _hudPipe = new SDL_GPUGraphicsPipeline*[blends.Length];
            foreach (HudBlend hb in blends)
            {
                var (src, dst) = HudBlendStates.Factors(hb);
                var blend = new SDL_GPUColorTargetBlendState
                {
                    enable_blend = true, color_blend_op = SDL_GPUBlendOp.SDL_GPU_BLENDOP_ADD, alpha_blend_op = SDL_GPUBlendOp.SDL_GPU_BLENDOP_ADD,
                    src_color_blendfactor = F(src), dst_color_blendfactor = F(dst), src_alpha_blendfactor = F(src), dst_alpha_blendfactor = F(dst),
                };
                var ctd = new SDL_GPUColorTargetDescription { format = _colorFmt, blend_state = blend };
                var ci = new SDL_GPUGraphicsPipelineCreateInfo
                {
                    vertex_shader = vs, fragment_shader = fs,
                    vertex_input_state = new SDL_GPUVertexInputState { vertex_buffer_descriptions = &vbd, num_vertex_buffers = 1, vertex_attributes = attrs, num_vertex_attributes = 3 },
                    primitive_type = SDL_GPUPrimitiveType.SDL_GPU_PRIMITIVETYPE_TRIANGLELIST,
                    rasterizer_state = new SDL_GPURasterizerState { fill_mode = SDL_GPUFillMode.SDL_GPU_FILLMODE_FILL, cull_mode = SDL_GPUCullMode.SDL_GPU_CULLMODE_NONE },
                    target_info = new SDL_GPUGraphicsPipelineTargetInfo { color_target_descriptions = &ctd, num_color_targets = 1 },
                };
                _hudPipe[(int)hb] = SDL_CreateGPUGraphicsPipeline(_dev, &ci);
                DrawLog.HudPipe((int)hb, D3DName(F(src)), D3DName(F(dst)));   // no depth state: the HUD pass has no depth target
                if (_hudPipe[(int)hb] == null) Console.Error.WriteLine($"hud pipeline {hb}: {SDL_GetError()}");
            }
            var si = new SDL_GPUSamplerCreateInfo
            {
                min_filter = SDL_GPUFilter.SDL_GPU_FILTER_NEAREST, mag_filter = SDL_GPUFilter.SDL_GPU_FILTER_NEAREST,
                mipmap_mode = SDL_GPUSamplerMipmapMode.SDL_GPU_SAMPLERMIPMAPMODE_NEAREST,
                address_mode_u = SDL_GPUSamplerAddressMode.SDL_GPU_SAMPLERADDRESSMODE_REPEAT, address_mode_v = SDL_GPUSamplerAddressMode.SDL_GPU_SAMPLERADDRESSMODE_REPEAT,
                address_mode_w = SDL_GPUSamplerAddressMode.SDL_GPU_SAMPLERADDRESSMODE_REPEAT, max_lod = 1000,
            };
            _hudSampler = SDL_CreateGPUSampler(_dev, &si);
            // D3DDrv DrawTile: MAG/MINFILTER LINEAR, MIPFILTER POINT (corona tiles, HudQuad.Smooth).
            si.min_filter = si.mag_filter = SDL_GPUFilter.SDL_GPU_FILTER_LINEAR;
            _hudLinear = SDL_CreateGPUSampler(_dev, &si);
            _hudWhite = Tex(SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM, 1, 1, 1, SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_SAMPLER);
            UploadLevels(_hudWhite, new[] { (1, 1, new byte[] { 255, 255, 255, 255 }) });
            return true;
        }

        static SDL_GPUBlendFactor F(HudBlendFactor f) => f switch
        {
            HudBlendFactor.Zero => SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ZERO,
            HudBlendFactor.One => SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE,
            HudBlendFactor.SrcColor => SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_SRC_COLOR,
            HudBlendFactor.OneMinusSrcColor => SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_COLOR,
            HudBlendFactor.DstColor => SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_DST_COLOR,
            HudBlendFactor.SrcAlpha => SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_SRC_ALPHA,
            HudBlendFactor.OneMinusSrcAlpha => SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_ALPHA,
            _ => throw new ArgumentOutOfRangeException(nameof(f)),
        };

        SDL_GPUShader* ShaderFrom(byte[] code, string entry, SDL_GPUShaderStage stage, uint samplers, uint uniforms)
        {
            byte[] ep = System.Text.Encoding.UTF8.GetBytes(entry + "\0");
            fixed (byte* c = code) fixed (byte* e = ep)
            {
                var ci = new SDL_GPUShaderCreateInfo { code_size = (nuint)code.Length, code = c, entrypoint = e,
                    format = _shaderFmt, stage = stage, num_samplers = samplers, num_uniform_buffers = uniforms };
                var sh = SDL_CreateGPUShader(_dev, &ci);
                if (sh == null) Console.Error.WriteLine($"hud shader {entry}: {SDL_GetError()}");
                return sh;
            }
        }

        // Always mip 0. The original: canvas tiles (UCanvas::DrawTile) lock the texture with LOD -1 and on Medium/Low
        // take the mip by TextureDetail (Engine 0x10387819; HP1.Core UETextureLod), fonts (WrappedPrint) LOD 0.
        // NOT ported for HUD/menu: "UI/pictures enhanced everywhere" conflicts with
        // "Texture Detail as the original".
        nint HudTexture(string path, HudQuadSource source)
        {
            if (string.IsNullOrEmpty(path)) return (nint)_hudWhite;
            // Runtime texture (CreateTextureFromBMP, SavePics): pixels from the Core registry; version is in the key:
            // the script recreates the object under the same name (slot pictures on every UpdateSlots).
            // Glyph pages of native fonts (UENativeFonts) are runtime textures too: alpha by the glyph rule below.
            if (UERuntimeTextures.TryGet(path, out var rt))
            {
                bool glyph = source == HudQuadSource.Glyph;
                string rkey = (glyph ? "rtfont|" : "rt|") + path + "|" + rt.Version;
                if (_hudTex.TryGetValue(rkey, out nint rtex)) return rtex;
                var px = new byte[rt.USize * rt.VSize * 4];
                for (int i = 0; i < rt.USize * rt.VSize; i++)
                {
                    int c = rt.Indices[i] * 4;
                    if (glyph)
                    {
                        px[i * 4] = px[i * 4 + 1] = px[i * 4 + 2] = 255;
                        px[i * 4 + 3] = Math.Max(rt.PaletteRGBA[c], Math.Max(rt.PaletteRGBA[c + 1], rt.PaletteRGBA[c + 2]));
                        continue;
                    }
                    bool zero = rt.Indices[i] == 0;
                    px[i * 4] = zero && rt.Masked ? (byte)0 : rt.PaletteRGBA[c];
                    px[i * 4 + 1] = zero && rt.Masked ? (byte)0 : rt.PaletteRGBA[c + 1];
                    px[i * 4 + 2] = zero && rt.Masked ? (byte)0 : rt.PaletteRGBA[c + 2];
                    px[i * 4 + 3] = zero ? (byte)0 : (byte)255;   // same rule as for package textures below
                }
                var gt = Tex(SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM, rt.USize, rt.VSize, 1, SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_SAMPLER);
                UploadLevels(gt, new[] { (rt.USize, rt.VSize, px) });
                _hudTex[rkey] = (nint)gt;
                return (nint)gt;
            }
            string key = (source == HudQuadSource.Glyph ? "font|" : "hud|") + path;
            if (_hudTex.TryGetValue(key, out nint t)) return t;
            UETextureSource.TextureData d = _src.Get(path);
            if (d is null || d.State != "EXPORTED" || d.Levels is null || d.Levels.Length == 0) { HudMissing++; _hudTex[key] = (nint)_hudWhite; return (nint)_hudWhite; }
            var (u, v, p8) = d.Levels[0];
            var rgba = new byte[u * v * 4];
            for (int i = 0; i < u * v; i++)
            {
                int c = p8[i] * 4;
                if (source == HudQuadSource.Glyph)
                {
                    rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = 255;
                    rgba[i * 4 + 3] = Math.Max(d.Palette[c], Math.Max(d.Palette[c + 1], d.Palette[c + 2]));
                }
                else
                {
                    bool zero = p8[i] == 0;
                    rgba[i * 4] = zero && d.Masked ? (byte)0 : d.Palette[c]; rgba[i * 4 + 1] = zero && d.Masked ? (byte)0 : d.Palette[c + 1];
                    rgba[i * 4 + 2] = zero && d.Masked ? (byte)0 : d.Palette[c + 2]; rgba[i * 4 + 3] = zero ? (byte)0 : (byte)255;
                }
            }
            var tex = Tex(SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM, u, v, 1, SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_SAMPLER);
            UploadLevels(tex, new[] { (u, v, rgba) });
            _hudTex[key] = (nint)tex;
            return (nint)tex;
        }

        // Frame quads (list order) -> vertices and runs of one state.
        public void SetHud(IReadOnlyList<HudQuad> quads)
        {
            _hudRuns.Clear(); _hudVertCount = 0;
            if (!EnsureHud()) return;
            int n = quads.Count;
            HudQuads = n;
            if (_hudVerts.Length < n * 6 * 8) _hudVerts = new float[n * 6 * 8 * 2];
            for (int i = 0; i < n; i++)
            {
                HudQuad q = quads[i];
                nint tex = HudTexture(q.Texture, q.Source);
                int first = _hudVertCount;
                Put(q.X0, q.Y0, q.U0, q.V0, q); Put(q.X1, q.Y0, q.U1, q.V0, q); Put(q.X1, q.Y1, q.U1, q.V1, q);
                Put(q.X0, q.Y0, q.U0, q.V0, q); Put(q.X1, q.Y1, q.U1, q.V1, q); Put(q.X0, q.Y1, q.U0, q.V1, q);
                if (_hudRuns.Count > 0)
                {
                    var last = _hudRuns[^1];
                    if (last.blend == q.Blend && last.clip == q.AlphaClip && last.tex == tex && last.smooth == q.Smooth) { _hudRuns[^1] = (last.first, last.count + 6, last.blend, last.clip, last.tex, last.smooth); continue; }
                }
                _hudRuns.Add((first, 6, q.Blend, q.AlphaClip, tex, q.Smooth));
            }
        }

        void Put(float x, float y, float u, float v, in HudQuad q)
        {
            int o = _hudVertCount * 8;
            _hudVerts[o] = x; _hudVerts[o + 1] = y; _hudVerts[o + 2] = u; _hudVerts[o + 3] = v;
            _hudVerts[o + 4] = q.R; _hudVerts[o + 5] = q.G; _hudVerts[o + 6] = q.B; _hudVerts[o + 7] = q.A;
            _hudVertCount++;
        }

        void UploadHud(SDL_GPUCommandBuffer* cmd)
        {
            if (_hudVertCount == 0) return;
            uint size = (uint)(_hudVertCount * 32);
            EnsureDyn(ref _hvb, ref _hvbCap, size, SDL_GPUBufferUsageFlags.SDL_GPU_BUFFERUSAGE_VERTEX);
            var tci = new SDL_GPUTransferBufferCreateInfo { usage = SDL_GPUTransferBufferUsage.SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD, size = size };
            var tb = SDL_CreateGPUTransferBuffer(_dev, &tci);
            fixed (float* v = _hudVerts) System.Buffer.MemoryCopy(v, (void*)SDL_MapGPUTransferBuffer(_dev, tb, false), size, size);
            SDL_UnmapGPUTransferBuffer(_dev, tb);
            var cp = SDL_BeginGPUCopyPass(cmd);
            var s0 = new SDL_GPUTransferBufferLocation { transfer_buffer = tb };
            var d0 = new SDL_GPUBufferRegion { buffer = _hvb, size = size };
            SDL_UploadToGPUBuffer(cp, &s0, &d0, true);
            SDL_EndGPUCopyPass(cp);
            SDL_ReleaseGPUTransferBuffer(_dev, tb);
        }

        // ── Gamma ramp (Brightness) ─────────────────────────────────────────────────────────────────
        // The frame is drawn into _gammaSrc, then one quad over the whole target: hud.hlsl branch 3,
        // c' = round(pow(c, e)·65535)/65535, the D3DDrv ramp (ScreenEffects.GammaExponent) per channel.
        SDL_GPUTexture* _gammaSrc; int _gammaW, _gammaH;
        SDL_GPUBuffer* _gammaVb;
        bool _gammaFailSaid;

        bool EnsureGamma(int w, int h)
        {
            if (!EnsureHud()) { if (!_gammaFailSaid) Console.Error.WriteLine("[Gamma] no HUD pipeline — brightness (Brightness) is not applied"); _gammaFailSaid = true; return false; }
            if (_gammaVb == null)
            {
                // Vertices in target fractions (screen uniform = 1×1): pos, uv, colour.
                float[] v = { 0, 0, 0, 0, 1, 1, 1, 1,  1, 0, 1, 0, 1, 1, 1, 1,  1, 1, 1, 1, 1, 1, 1, 1,
                              0, 0, 0, 0, 1, 1, 1, 1,  1, 1, 1, 1, 1, 1, 1, 1,  0, 1, 0, 1, 1, 1, 1, 1 };
                _gammaVb = Buffer(SDL_GPUBufferUsageFlags.SDL_GPU_BUFFERUSAGE_VERTEX, MemoryMarshal.AsBytes(v.AsSpan()));
            }
            if (_gammaSrc == null || _gammaW != w || _gammaH != h)
            {
                if (_gammaSrc != null) SDL_ReleaseGPUTexture(_dev, _gammaSrc);
                _gammaSrc = Tex(_colorFmt, w, h, 1, SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_COLOR_TARGET | SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_SAMPLER);
                _gammaW = w; _gammaH = h;
            }
            return _gammaSrc != null && _gammaVb != null;
        }

        void DrawGamma(SDL_GPUCommandBuffer* cmd, SDL_GPUTexture* target)
        {
            var color = new SDL_GPUColorTargetInfo { texture = target, load_op = SDL_GPULoadOp.SDL_GPU_LOADOP_DONT_CARE, store_op = SDL_GPUStoreOp.SDL_GPU_STOREOP_STORE };
            var vu = stackalloc float[4]; vu[0] = 1; vu[1] = 1;
            SDL_PushGPUVertexUniformData(cmd, 0, (nint)vu, 16);
            var pass = SDL_BeginGPURenderPass(cmd, &color, 1, null);
            var vb = new SDL_GPUBufferBinding { buffer = _gammaVb };
            SDL_BindGPUVertexBuffers(pass, 0, &vb, 1);
            SDL_BindGPUGraphicsPipeline(pass, _hudPipe[(int)HudBlend.Opaque]);
            var fu = stackalloc float[4]; fu[0] = 3; fu[1] = 0; fu[2] = GammaExponent; fu[3] = 0;
            SDL_PushGPUFragmentUniformData(cmd, 0, (nint)fu, 16);
            var sb = new SDL_GPUTextureSamplerBinding { texture = _gammaSrc, sampler = _hudSampler };
            SDL_BindGPUFragmentSamplers(pass, 0, &sb, 1);
            SDL_DrawGPUPrimitives(pass, 6, 1, 0, 0);
            SDL_EndGPURenderPass(pass);
        }

        void DrawHud(SDL_GPUCommandBuffer* cmd, SDL_GPUTexture* target, int w, int h)
        {
            if (_hudVertCount == 0) return;
            var color = new SDL_GPUColorTargetInfo { texture = target, load_op = SDL_GPULoadOp.SDL_GPU_LOADOP_LOAD, store_op = SDL_GPUStoreOp.SDL_GPU_STOREOP_STORE };
            var vu = stackalloc float[4]; vu[0] = w; vu[1] = h;
            SDL_PushGPUVertexUniformData(cmd, 0, (nint)vu, 16);
            var pass = SDL_BeginGPURenderPass(cmd, &color, 1, null);
            var vb = new SDL_GPUBufferBinding { buffer = _hvb };
            SDL_BindGPUVertexBuffers(pass, 0, &vb, 1);
            var fu = stackalloc float[4];
            var sb = stackalloc SDL_GPUTextureSamplerBinding[1];
            foreach (var run in _hudRuns)
            {
                SDL_BindGPUGraphicsPipeline(pass, _hudPipe[(int)run.blend]);
                fu[0] = HudBlendStates.ShaderMode(run.blend); fu[1] = run.clip ? 1 : 0; fu[2] = fu[3] = 0;
                SDL_PushGPUFragmentUniformData(cmd, 0, (nint)fu, 16);
                sb[0] = new SDL_GPUTextureSamplerBinding { texture = (SDL_GPUTexture*)run.tex, sampler = run.smooth ? _hudLinear : _hudSampler };
                SDL_BindGPUFragmentSamplers(pass, 0, sb, 1);
                SDL_DrawGPUPrimitives(pass, (uint)run.count, 1, (uint)run.first, 0);
            }
            SDL_EndGPURenderPass(pass);
        }
    }
}
