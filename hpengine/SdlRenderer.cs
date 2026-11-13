// SDL_GPU: one vertex and one index buffer for the world, a texture per material, a lightmap atlas.
// Pipelines per UE1 blend mode (factors from RenderRules.Blend): 0 opaque, 1 masked (alpha cutout), 2 world translucent
// and 4 actor translucent (both ONE,1-SRC_COLOR), 3 modulated (DST_COLOR,SRC_COLOR), 5 depth only (sky window),
// 6 actor alpha blending for Opacity < 1 (SRC_ALPHA,1-SRC_ALPHA, no Z write), 7 a surface's fog map pass (ONE,1-SRC_ALPHA,
// no Z write); each in three culling variants.
using System.Runtime.InteropServices;
using HP1.UEScript;
using SDL;
using static SDL.SDL3;

namespace HPEngine
{
    sealed unsafe partial class Renderer
    {
        SDL_GPUDevice* _dev;
        public SDL_Window* Window;
        SDL_GPUTextureFormat _colorFmt;
        SDL_GPUShaderFormat _shaderFmt;
        public string Driver;
        // Mesh front face is clockwise on screen: shipped keeps a triangle when PF_TwoSided or (N·P0)·Frame->Mirror < 0,
        // N = (P0−P1)×(P2−P0) in frame space (DrawLodMesh, Render.dll.c:14669-14697), i.e. clockwise in y-down screen space
        // for Mirror = 1; the reflected pass (Mirror = −1) culls front faces. Selftest scene cull (cull-ccw, cull-none red).
        public bool HideWorld;
        const SDL_GPUFrontFace FrontFace = SDL_GPUFrontFace.SDL_GPU_FRONTFACE_CLOCKWISE;
        const int Modes = 8;   // pipeline modes (see header): 4 actor translucent, 5 depth only, 6 AlphaBlend, 7 fog map pass
        SDL_GPUGraphicsPipeline*[] _pipe = new SDL_GPUGraphicsPipeline*[3 * Modes];   // [cull·Modes + mode]: 0 none, 1 back, 2 front (reflected pass)
        SDL_GPUBuffer* _vb;
        uint[] _idx = Array.Empty<uint>();   // world indices (Batch.First/Runs), copied per view into _wib by Core's drawn set
        int _nodeCount;
        WorldMesh _mesh;   // its BackSlot table: back-side runs (WorldMesh.Select)
        SDL_GPUTexture* _atlas, _atlas2, _color, _depth, _white;   // _atlas2: mover atlas
        SDL_GPUSampler* _linear, _linearOriginal, _point, _lmSampler;
        // WorldFrame.EnhancedVisuals of the frame being drawn (Core decides; FrameDraws copies it): Original / Enhanced
        // world sampler (RenderRules.WorldSampling). Switches on the next frame, no restart.
        public bool EnhancedVisuals = true;
        SDL_GPUSampler* World => EnhancedVisuals ? _linear : _linearOriginal;
        // [HPEngine] Anisotropy (UEConfig.Anisotropy; 0 = off), before Create. NOT ORIGINAL (D-53).
        public int Anisotropy = 16;
        SDL_GPUTransferBuffer* _download;
        int _depthW, _depthH;
        public int Width, Height;
        readonly List<(Batch b, nint tex)> _draws = new();
        readonly Dictionary<string, nint> _textures = new(StringComparer.OrdinalIgnoreCase);
        public int TextureCount => _textures.Count;
        public readonly List<string> MissingTextures = new();
        int _atlasH;

        // offscreenWindow (--offscreen-window, judges and benches): a HIDDEN window for events and the viewport device, frames
        // drawn into the offscreen target (no swapchain, nothing shown).
        public bool Offscreen;
        public bool Create(bool window, int w, int h, bool offscreenWindow = false)
        {
            Width = w; Height = h;
            // SDL picks the backend (Metal / Vulkan / D3D12); blob format by what the device accepts.
            _dev = SDL_CreateGPUDevice(Shaders.Supported, false, (byte*)null);
            if (_dev == null) return false;
            Driver = SDL_GetGPUDeviceDriver(_dev);
            _shaderFmt = Shaders.Pick(SDL_GetGPUShaderFormats(_dev));
            if (_shaderFmt == 0) { Console.Error.WriteLine($"GPU {Driver}: no blob for any of the device's shader formats ({SDL_GetGPUShaderFormats(_dev)})"); return false; }
            Console.WriteLine($"GPU: {Driver}, shaders {Shaders.Name(_shaderFmt)}");
            if (window && offscreenWindow)
            {
                Window = SDL_CreateWindow(UELocalization.ViewportCaption(), w, h, SDL_WindowFlags.SDL_WINDOW_HIDDEN);
                if (Window == null) return false;
                Offscreen = true;
            }
            if (window && !offscreenWindow)
            {
                Window = SDL_CreateWindow(UELocalization.ViewportCaption(), w, h, SDL_WindowFlags.SDL_WINDOW_RESIZABLE | SDL_WindowFlags.SDL_WINDOW_HIGH_PIXEL_DENSITY);
                if (Window == null || !SDL_ClaimWindowForGPUDevice(_dev, Window)) return false;
                // Pacing is set by the window (HPEngineProgram.Window, --fps); present mode MAILBOX (no tearing and no
                // waiting for refresh), otherwise VSYNC.
                var pm = SDL_WindowSupportsGPUPresentMode(_dev, Window, SDL_GPUPresentMode.SDL_GPU_PRESENTMODE_MAILBOX)
                    ? SDL_GPUPresentMode.SDL_GPU_PRESENTMODE_MAILBOX : SDL_GPUPresentMode.SDL_GPU_PRESENTMODE_VSYNC;
                SDL_SetGPUSwapchainParameters(_dev, Window, SDL_GPUSwapchainComposition.SDL_GPU_SWAPCHAINCOMPOSITION_SDR, pm);
                _colorFmt = SDL_GetGPUSwapchainTextureFormat(_dev, Window);
            }
            else
            {
                _colorFmt = SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM;
                _color = Tex(_colorFmt, w, h, 1, SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_COLOR_TARGET | SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_SAMPLER);
                var tb = new SDL_GPUTransferBufferCreateInfo { usage = SDL_GPUTransferBufferUsage.SDL_GPU_TRANSFERBUFFERUSAGE_DOWNLOAD, size = (uint)(w * h * 4) };
                _download = SDL_CreateGPUTransferBuffer(_dev, &tb);
            }
            EnsureDepth(w, h);
            CreatePipelines();
            // World sampler, Enhanced (_linear) and Original (_linearOriginal) — RenderRules.WorldSampling: trilinear (D-48)
            // and anisotropy (D-53) are NOT ORIGINAL, Enhanced only; Original is D3DDrv (MIPFILTER POINT, no
            // MAXANISOTROPY). Only for 3D-world textures (BSP, movers, meshes, sprites, particles, decals, sky, fire). Not for _point
            // (PF_NoSmooth, D-13: on D3D12 the anisotropic filter is linear, the point look would be lost) and not for
            // _lmSampler (lightmap/fog atlases are low-frequency, excluded on purpose; the mirror also samples
            // its 1:1 screen-space target with it, where anisotropy has nothing to do).
            _linear = WorldSampler(true);
            EnhancedSamplerInfo = _lastSampler;
            _linearOriginal = WorldSampler(false);
            OriginalSamplerInfo = _lastSampler;
            _point = Sampler(SDL_GPUFilter.SDL_GPU_FILTER_NEAREST, SDL_GPUSamplerMipmapMode.SDL_GPU_SAMPLERMIPMAPMODE_NEAREST, SDL_GPUSamplerAddressMode.SDL_GPU_SAMPLERADDRESSMODE_REPEAT);
            _lmSampler = Sampler(SDL_GPUFilter.SDL_GPU_FILTER_LINEAR, SDL_GPUSamplerMipmapMode.SDL_GPU_SAMPLERMIPMAPMODE_NEAREST, SDL_GPUSamplerAddressMode.SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE);
            return _pipe[0] != null;
        }

        SDL_GPUTexture* Tex(SDL_GPUTextureFormat f, int w, int h, int levels, SDL_GPUTextureUsageFlags usage)
        {
            var ci = new SDL_GPUTextureCreateInfo
            {
                type = SDL_GPUTextureType.SDL_GPU_TEXTURETYPE_2D, format = f, usage = usage,
                width = (uint)w, height = (uint)h, layer_count_or_depth = 1, num_levels = (uint)levels,
                sample_count = SDL_GPUSampleCount.SDL_GPU_SAMPLECOUNT_1,
            };
            return SDL_CreateGPUTexture(_dev, &ci);
        }

        void EnsureDepth(int w, int h)
        {
            if (_depth != null && _depthW == w && _depthH == h) return;
            if (_depth != null) SDL_ReleaseGPUTexture(_dev, _depth);
            _depth = Tex(SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_D32_FLOAT, w, h, 1, SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_DEPTH_STENCIL_TARGET);
            _depthW = w; _depthH = h;
        }

        // Create info of the world samplers as actually created (after a refused-anisotropy fallback too): the GPU selftest
        // reads them, because how anisotropy picks the mip level is driver-defined and has no portable pixel check.
        public SDL_GPUSamplerCreateInfo EnhancedSamplerInfo, OriginalSamplerInfo;
        SDL_GPUSamplerCreateInfo _lastSampler;

        SDL_GPUSampler* WorldSampler(bool enhanced)
        {
            var (mipLinear, anisotropy) = RenderRules.WorldSampling(enhanced, Anisotropy);
            return Sampler(SDL_GPUFilter.SDL_GPU_FILTER_LINEAR,
                mipLinear ? SDL_GPUSamplerMipmapMode.SDL_GPU_SAMPLERMIPMAPMODE_LINEAR : SDL_GPUSamplerMipmapMode.SDL_GPU_SAMPLERMIPMAPMODE_NEAREST,
                SDL_GPUSamplerAddressMode.SDL_GPU_SAMPLERADDRESSMODE_REPEAT, anisotropy);
        }

        SDL_GPUSampler* Sampler(SDL_GPUFilter f, SDL_GPUSamplerMipmapMode mm, SDL_GPUSamplerAddressMode am, int anisotropy = 0)
        {
            var ci = new SDL_GPUSamplerCreateInfo
            {
                min_filter = f, mag_filter = f, mipmap_mode = mm, address_mode_u = am, address_mode_v = am, address_mode_w = am,
                min_lod = 0, max_lod = 1000, enable_anisotropy = anisotropy > 1, max_anisotropy = anisotropy,
            };
            var s = SDL_CreateGPUSampler(_dev, &ci);
            if (s != null) _lastSampler = ci;
            // SDL_CreateGPUDevice requires anisotropy by default (Vulkan samplerAnisotropy; Metal/D3D12 always),
            // so no refusal is expected; if there is one, it is loud and we go without anisotropy.
            if (s == null && anisotropy > 1)
            {
                Console.Error.WriteLine($"GPU {Driver}: sampler with anisotropy {anisotropy} not created ({SDL_GetError()}) — no anisotropy");
                return Sampler(f, mm, am);
            }
            return s;
        }

        SDL_GPUShader* Shader(string entry, SDL_GPUShaderStage stage, uint samplers, uint uniforms)
        {
            byte[] code = Shaders.Load("classic", stage == SDL_GPUShaderStage.SDL_GPU_SHADERSTAGE_VERTEX ? "vert" : "frag", _shaderFmt);
            byte[] ep = System.Text.Encoding.UTF8.GetBytes(entry + "\0");
            fixed (byte* c = code) fixed (byte* e = ep)
            {
                var ci = new SDL_GPUShaderCreateInfo
                {
                    code_size = (nuint)code.Length, code = c, entrypoint = e,
                    format = _shaderFmt, stage = stage,
                    num_samplers = samplers, num_uniform_buffers = uniforms,
                };
                var s = SDL_CreateGPUShader(_dev, &ci);
                if (s == null) Console.Error.WriteLine($"shader {entry}: {SDL_GetError()}");
                return s;
            }
        }

        void CreatePipelines()
        {
            var vs = Shader("vs_main", SDL_GPUShaderStage.SDL_GPU_SHADERSTAGE_VERTEX, 0, 1);
            var fs = Shader("fs_main", SDL_GPUShaderStage.SDL_GPU_SHADERSTAGE_FRAGMENT, 3, 1);
            if (vs == null || fs == null) return;
            // Layout: Vertex (SdlWorldMesh.cs) and VIn (classic.hlsl): location = TEXCOORDn, offsets in bytes.
            var attrs = stackalloc SDL_GPUVertexAttribute[7];
            attrs[0] = new SDL_GPUVertexAttribute { location = 0, buffer_slot = 0, format = SDL_GPUVertexElementFormat.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT3, offset = 0 };
            attrs[1] = new SDL_GPUVertexAttribute { location = 1, buffer_slot = 0, format = SDL_GPUVertexElementFormat.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT2, offset = 12 };
            attrs[2] = new SDL_GPUVertexAttribute { location = 2, buffer_slot = 0, format = SDL_GPUVertexElementFormat.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT2, offset = 20 };
            attrs[3] = new SDL_GPUVertexAttribute { location = 3, buffer_slot = 0, format = SDL_GPUVertexElementFormat.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT3, offset = 28 };
            attrs[4] = new SDL_GPUVertexAttribute { location = 4, buffer_slot = 0, format = SDL_GPUVertexElementFormat.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT2, offset = 40 };
            attrs[5] = new SDL_GPUVertexAttribute { location = 5, buffer_slot = 0, format = SDL_GPUVertexElementFormat.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT3, offset = 48 };
            attrs[6] = new SDL_GPUVertexAttribute { location = 6, buffer_slot = 0, format = SDL_GPUVertexElementFormat.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT, offset = 60 };
            var vbd = new SDL_GPUVertexBufferDescription { slot = 0, pitch = (uint)sizeof(Vertex), input_rate = SDL_GPUVertexInputRate.SDL_GPU_VERTEXINPUTRATE_VERTEX };
            for (int pi = 0; pi < 3 * Modes; pi++)
            {
                int mode = pi % Modes, cull = pi / Modes;
                var blend = new SDL_GPUColorTargetBlendState();
                if (mode == 5) { blend.enable_color_write_mask = true; blend.color_write_mask = 0; }
                else if (mode >= 2)
                {
                    blend.enable_blend = true;
                    blend.color_blend_op = blend.alpha_blend_op = SDL_GPUBlendOp.SDL_GPU_BLENDOP_ADD;
                    // Opacity < 1 (mode 6): PF_Highlighted|PF_Translucent (D3DDrv SetBlending 0x100093d3; ActorOpacity.Resolve).
                    var (bs, bd) = RenderRules.Blend(mode);
                    blend.src_color_blendfactor = Factor(bs); blend.dst_color_blendfactor = Factor(bd);
                    blend.src_alpha_blendfactor = SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ZERO; blend.dst_alpha_blendfactor = SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE;
                }
                var ctd = new SDL_GPUColorTargetDescription { format = _colorFmt, blend_state = blend };
                var ci = new SDL_GPUGraphicsPipelineCreateInfo
                {
                    vertex_shader = vs, fragment_shader = fs,
                    vertex_input_state = new SDL_GPUVertexInputState { vertex_buffer_descriptions = &vbd, num_vertex_buffers = 1, vertex_attributes = attrs, num_vertex_attributes = 7 },
                    primitive_type = SDL_GPUPrimitiveType.SDL_GPU_PRIMITIVETYPE_TRIANGLELIST,
                    rasterizer_state = new SDL_GPURasterizerState { fill_mode = SDL_GPUFillMode.SDL_GPU_FILLMODE_FILL, front_face = FrontFace,
                        cull_mode = cull == 0 ? SDL_GPUCullMode.SDL_GPU_CULLMODE_NONE : cull == 1 ? SDL_GPUCullMode.SDL_GPU_CULLMODE_BACK : SDL_GPUCullMode.SDL_GPU_CULLMODE_FRONT },
                    depth_stencil_state = new SDL_GPUDepthStencilState
                    {
                        enable_depth_test = true, enable_depth_write = mode < 2 || mode == 5,
                        compare_op = SDL_GPUCompareOp.SDL_GPU_COMPAREOP_LESS_OR_EQUAL,
                    },
                    target_info = new SDL_GPUGraphicsPipelineTargetInfo
                    {
                        color_target_descriptions = &ctd, num_color_targets = 1,
                        depth_stencil_format = SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_D32_FLOAT, has_depth_stencil_target = true,
                    },
                };
                _pipe[pi] = SDL_CreateGPUGraphicsPipeline(_dev, &ci);
                DrawLog.Pipe(pi, blend.enable_blend, D3DName(blend.src_color_blendfactor), D3DName(blend.dst_color_blendfactor),
                    ci.depth_stencil_state.enable_depth_write, !(blend.enable_color_write_mask && blend.color_write_mask == 0));
                if (_pipe[pi] == null) Console.Error.WriteLine($"pipeline {pi}: {SDL_GetError()}");
            }
        }

        static SDL_GPUBlendFactor Factor(int d3d) => d3d switch
        {
            RenderRules.ONE => SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE,
            RenderRules.SRCCOLOR => SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_SRC_COLOR,
            RenderRules.INVSRCCOLOR => SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_COLOR,
            RenderRules.SRCALPHA => SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_SRC_ALPHA,
            RenderRules.INVSRCALPHA => SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_ALPHA,
            RenderRules.DESTCOLOR => SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_DST_COLOR,
            _ => throw new ArgumentOutOfRangeException(nameof(d3d), d3d, "D3DBLEND without an SDL_GPU translation"),
        };

        // ---- upload ----

        public void Upload(WorldMesh mesh, LightmapAtlas atlas, UETextureSource src)
        {
            // Reload (level transition): previous buffers and atlases are freed; the per-path texture cache lives on.
            if (_vb != null) { SDL_ReleaseGPUBuffer(_dev, _vb); _vb = null; }
            if (_atlas != null) { SDL_ReleaseGPUTexture(_dev, _atlas); _atlas = null; }
            if (_atlas2 != null) { SDL_ReleaseGPUTexture(_dev, _atlas2); _atlas2 = null; }
            _draws.Clear();
            _dynICount = _dynVCount = 0;
            _levelMirrors = mesh.MirrorPlanes.Count;
            _portalOfSurf.Clear();
            for (int i = 0; i < mesh.WarpPortals.Count; i++) _portalOfSurf[mesh.WarpPortals[i].Surf] = i;
            var indices = new List<uint>();
            foreach (Batch b in mesh.Batches) { b.First = (uint)indices.Count; b.Count = (uint)b.Indices.Count; indices.AddRange(b.Indices); }
            Vertex[] verts = mesh.Vertices.ToArray();
            _idx = indices.ToArray();
            _nodeCount = mesh.NodeCount;
            _mesh = mesh;
            _vb = Buffer(SDL_GPUBufferUsageFlags.SDL_GPU_BUFFERUSAGE_VERTEX, MemoryMarshal.AsBytes(verts.AsSpan()));

            _src = src;
            if (_white == null)
            {
                _white = Tex(SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM, 1, 1, 1, SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_SAMPLER);
                UploadLevels(_white, new[] { (1, 1, new byte[] { 255, 0, 255, 255 }) });
            }
            _atlasH = atlas.Height;
            _atlas = Tex(SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM, LightmapAtlas.Width, atlas.Height, 1, SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_SAMPLER);
            UploadLevels(_atlas, new[] { (LightmapAtlas.Width, atlas.Height, atlas.Rgba) });

            foreach (Batch b in mesh.Batches)
            {
                _draws.Add((b, TextureFor(b.Material, b.AlphaTest ? TexDraw.Masked : TexDraw.Normal)));
            }
            // Order: opaque and masked, then translucent and modulated.
            HPEngine.Sprites.SpriteMath.OpaqueFirst(_draws, x => (int)x.b.Blend >= 2, new());
            _batches.Clear(); foreach (var d in _draws) _batches.Add(d.b);
            _uvOff = new float[_draws.Count * 2]; _animTex = new nint[_draws.Count];
        }

        // Masked drawing (PF_Masked): palette entry 0 = 0x00000000 on every mip (D3DDrv.dll.c:9879-9881,
        // 9980-9982). For a bMasked texture entry 0 is always so.
        // Translucent drawing (Opacity < 1, ActorOpacity.OpacityPalette): palette RGBA as is, alpha is the palette
        // entry's alpha (D3DDrv P8->8888, FUN_10002490); OpacityMasked (a masked slot or a bMasked texture):
        // entry 0 = (0,0,0,0).
        public enum TexDraw { Normal, Masked, Opacity, OpacityMasked }
        public IEnumerable<string> TextureKeys => _textures.Keys;
        internal static string TexKey(string path, TexDraw v) => v switch
        {
            TexDraw.Masked => path + "|masked", TexDraw.Opacity => path + "|opacity", TexDraw.OpacityMasked => path + "|opacity-masked", _ => path,
        };
        // ── TEXTURE DETAIL (options menu; formula and addresses in HP1.Core UETextureLod) ──
        // The draw path decides which LOD UTexture::Lock requests: Client (-1) for BSP and mover surfaces, sky, decals,
        // sprites and coronas (UCanvas::DrawTile); MeshSkin for mesh skins (Render's own LOD);
        // Zero for particles (Render 0x10b1514a). The first mip is part of the cache key, like LOD in Lock's CacheID
        // (Info.CacheID = Texture·2^32 + 0xe0 + LOD): a level change takes another texture, the previous ones stay.
        int _texDetail, _skinDetail;
        public int TextureDetailLevel => _texDetail;
        public int SkinDetailLevel => _skinDetail;
        public readonly HashSet<string> ShortChains = new(StringComparer.OrdinalIgnoreCase);

        public void ApplyTextureDetail(int textureDetail, int skinDetail)
        {
            if (textureDetail == _texDetail && skinDetail == _skinDetail) return;
            _texDetail = textureDetail; _skinDetail = skinDetail;
            for (int i = 0; i < _draws.Count; i++)
            {
                Batch b = _draws[i].b;
                _draws[i] = (b, TextureFor(b.Material, b.AlphaTest ? TexDraw.Masked : TexDraw.Normal));
            }
        }

        internal int FirstMip(string path, TexLod lod) => TextureDetail.FirstMip(_src?.Get(path), lod, _texDetail, _skinDetail);

        nint TextureFor(string path, TexDraw variant, TexLod lod = TexLod.Client)
        {
            int mip = FirstMip(path, lod);
            string key = mip == 0 ? TexKey(path, variant) : TexKey(path, variant) + "#mip" + mip;
            if (!_textures.TryGetValue(key, out nint t))
            {
                var pt = LoadProc(path);
                t = pt != null ? (nint)pt : (nint)LoadTexture(_src, path, variant, mip); _textures[key] = t;
            }
            return t;
        }

        SDL_GPUTexture* LoadTexture(UETextureSource src, string path, TexDraw variant, int firstMip = 0)
        {
            UETextureSource.TextureData d = src.Get(path);
            if (d is null || d.State != "EXPORTED" || d.Levels is null || d.Levels.Length == 0) { MissingTextures.Add($"{path} ({d?.State ?? "not found"})"); return _white; }
            if (firstMip >= d.Levels.Length)
            {
                // Lock cuts by the package's Mips.Num(), but the port's chain stops at a mip without data: loud,
                // and the last mip with data is taken.
                if (ShortChains.Add(path)) Console.Error.WriteLine($"[Texture] STOP: {path}: Lock gives mip {firstMip}, the port has data for {d.Levels.Length} of {d.MipCount}");
                firstMip = d.Levels.Length - 1;
            }
            var levels = ExpandP8(d, variant, firstMip);
            var tex = Tex(SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM, levels[0].Item1, levels[0].Item2, levels.Length, SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_SAMPLER);
            UploadLevels(tex, levels);
            return tex;
        }

        // P8 -> RGBA per draw variant (whole mips): palette entry 0 decided per use — D-78, NOT ORIGINAL.
        internal static (int, int, byte[])[] ExpandP8(UETextureSource.TextureData d, TexDraw variant, int firstMip = 0)
        {
            bool maskedDraw = variant == TexDraw.Masked;
            byte[] opal = variant is TexDraw.Opacity or TexDraw.OpacityMasked
                ? HPEngine.ActorFx.ActorOpacity.OpacityPalette(d.Palette, masked: variant == TexDraw.OpacityMasked || d.Masked) : null;
            var levels = new (int, int, byte[])[d.Levels.Length - firstMip];
            for (int l = 0; l < levels.Length; l++)
            {
                var (u, v, p8) = d.Levels[firstMip + l];
                var rgba = new byte[u * v * 4];
                if (opal != null)
                {
                    for (int i = 0; i < u * v; i++) opal.AsSpan(p8[i] * 4, 4).CopyTo(rgba.AsSpan(i * 4));
                    levels[l] = (u, v, rgba);
                    continue;
                }
                for (int i = 0; i < u * v; i++)
                {
                    int c = p8[i];
                    rgba[i * 4] = d.Palette[c * 4]; rgba[i * 4 + 1] = d.Palette[c * 4 + 1]; rgba[i * 4 + 2] = d.Palette[c * 4 + 2];
                    // D3DDrv rule: regular drawing A = 255, index 0 has A = 0 (under bMasked entry 0 = 0x00000000);
                    // masked drawing of a texture without bMasked uses the D3DDrv P8->8888 table (FUN_10002490):
                    // A = palette alpha, entry 0 = 0x00000000 (the alpha test >127 also cuts indices with palette alpha
                    // ≤127). Palette alpha is as in the original's MEMORY: for packages of version < 0x42 (GenFX, Detail,
                    // FireEng, GreatFire, Liquids, Palettes) it is 0xFF (UPalette::Serialize, UETextureSource);
                    // GenFX.Mask.* (cobweb) is bMasked, so only index 0 cuts it (memory of Lev4_Sneak2).
                    bool paletteAlpha = maskedDraw && !d.Masked;
                    if (c == 0 && (paletteAlpha || d.Masked)) { rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = 0; rgba[i * 4 + 3] = 0; }
                    else rgba[i * 4 + 3] = paletteAlpha ? d.Palette[c * 4 + 3] : c == 0 ? (byte)0 : (byte)255;
                }
                levels[l] = (u, v, rgba);
            }
            return levels;
        }

        SDL_GPUBuffer* Buffer(SDL_GPUBufferUsageFlags usage, ReadOnlySpan<byte> data)
        {
            var ci = new SDL_GPUBufferCreateInfo { usage = usage, size = (uint)data.Length };
            var buf = SDL_CreateGPUBuffer(_dev, &ci);
            var tci = new SDL_GPUTransferBufferCreateInfo { usage = SDL_GPUTransferBufferUsage.SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD, size = (uint)data.Length };
            var tb = SDL_CreateGPUTransferBuffer(_dev, &tci);
            data.CopyTo(new Span<byte>((void*)SDL_MapGPUTransferBuffer(_dev, tb, false), data.Length));
            SDL_UnmapGPUTransferBuffer(_dev, tb);
            var cmd = SDL_AcquireGPUCommandBuffer(_dev);
            var cp = SDL_BeginGPUCopyPass(cmd);
            var srcLoc = new SDL_GPUTransferBufferLocation { transfer_buffer = tb };
            var dst = new SDL_GPUBufferRegion { buffer = buf, size = (uint)data.Length };
            SDL_UploadToGPUBuffer(cp, &srcLoc, &dst, false);
            SDL_EndGPUCopyPass(cp);
            SDL_SubmitGPUCommandBuffer(cmd);
            SDL_ReleaseGPUTransferBuffer(_dev, tb);
            return buf;
        }

        void UploadLevels(SDL_GPUTexture* tex, (int w, int h, byte[] rgba)[] levels, int x = 0, int y = 0)
        {
            int total = 0;
            foreach (var l in levels) total += l.rgba.Length;
            var tci = new SDL_GPUTransferBufferCreateInfo { usage = SDL_GPUTransferBufferUsage.SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD, size = (uint)total };
            var tb = SDL_CreateGPUTransferBuffer(_dev, &tci);
            byte* map = (byte*)SDL_MapGPUTransferBuffer(_dev, tb, false);
            int off = 0;
            foreach (var l in levels) { l.rgba.CopyTo(new Span<byte>(map + off, l.rgba.Length)); off += l.rgba.Length; }
            SDL_UnmapGPUTransferBuffer(_dev, tb);
            var cmd = SDL_AcquireGPUCommandBuffer(_dev);
            var cp = SDL_BeginGPUCopyPass(cmd);
            off = 0;
            for (int i = 0; i < levels.Length; i++)
            {
                var info = new SDL_GPUTextureTransferInfo { transfer_buffer = tb, offset = (uint)off, pixels_per_row = (uint)levels[i].w, rows_per_layer = (uint)levels[i].h };
                var reg = new SDL_GPUTextureRegion { texture = tex, mip_level = (uint)i, x = (uint)x, y = (uint)y, w = (uint)levels[i].w, h = (uint)levels[i].h, d = 1 };
                SDL_UploadToGPUTexture(cp, &info, &reg, false);
                off += levels[i].rgba.Length;
            }
            SDL_EndGPUCopyPass(cp);
            SDL_SubmitGPUCommandBuffer(cmd);
            SDL_ReleaseGPUTransferBuffer(_dev, tb);
        }

        // Fog atlases (FogScene): one per Core frame (index 0 master, i + 1 WorldFrame.Views[i]) — a surface's fog map is
        // built with the frame that draws it (Core LevelFogMaps); a view samples its own frame's atlas.
        int _fogW, _fogH;
        readonly List<nint> _fogs = new();
        public void CreateFogAtlas(FogScene fog)
        {
            foreach (nint t in _fogs) SDL_ReleaseGPUTexture(_dev, (SDL_GPUTexture*)t);
            _fogs.Clear();
            _fogW = FogScene.W; _fogH = fog.H;
        }
        SDL_GPUTexture* FogTex(int frame)
        {
            while (_fogs.Count <= frame)
            {
                var t = Tex(SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM, _fogW, _fogH, 1, SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_SAMPLER);
                UploadLevels(t, new[] { (_fogW, _fogH, new byte[_fogW * _fogH * 4]) });
                _fogs.Add((nint)t);
            }
            return (SDL_GPUTexture*)_fogs[frame];
        }
        // The atlas of the frame the current view draws.
        SDL_GPUTexture* _fog => FogTex((_curView < _frameOfView.Count ? _frameOfView[_curView] : -1) + 1);
        public void UploadFog(FogScene fog)
        {
            if (fog.Surfaces.Count == 0) return;
            for (int f = 0; f < fog.Atlases.Count; f++) UploadLevels(FogTex(f), new[] { (_fogW, _fogH, fog.Atlases[f]) });
        }

        public void CreateMoverAtlas(LightmapAtlas atlas)
        {
            _atlas2 = Tex(SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM, LightmapAtlas.Width, atlas.Height, 1, SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_SAMPLER);
            UploadLevels(_atlas2, new[] { (LightmapAtlas.Width, atlas.Height, atlas.Rgba) });
        }

        // Core frame tiles -> atlas (CPU copy + region upload in one copy pass).
        public int TilesLastFrame;
        public void UpdateTiles(LightmapAtlas atlas, LevelSurfLightmapRuntime lm, bool moverAtlas = false)
            => UpdateTiles(atlas, lm?.Tiles, moverAtlas);

        public void UpdateTiles(LightmapAtlas atlas, IReadOnlyList<LevelSurfLightmapRuntime.Tile> list, bool moverAtlas)
        {
            TilesLastFrame = 0;
            if (list is null || list.Count == 0) return;
            var regions = new List<(int x, int y, int w, int h)>();
            int total = 0;
            foreach (var t in list)
                if (atlas.Write(t, out int x0, out int y0, out int w, out int h)) { regions.Add((x0, y0, w, h)); total += w * h * 4; }
            if (total == 0) return;
            TilesLastFrame = regions.Count;
            var tci = new SDL_GPUTransferBufferCreateInfo { usage = SDL_GPUTransferBufferUsage.SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD, size = (uint)total };
            var tb = SDL_CreateGPUTransferBuffer(_dev, &tci);
            byte* map = (byte*)SDL_MapGPUTransferBuffer(_dev, tb, false);
            int off = 0;
            foreach (var r in regions)
                for (int row = 0; row < r.h; row++)
                {
                    new ReadOnlySpan<byte>(atlas.Rgba, ((r.y + row) * LightmapAtlas.Width + r.x) * 4, r.w * 4).CopyTo(new Span<byte>(map + off, r.w * 4));
                    off += r.w * 4;
                }
            SDL_UnmapGPUTransferBuffer(_dev, tb);
            var cmd = SDL_AcquireGPUCommandBuffer(_dev);
            var cp = SDL_BeginGPUCopyPass(cmd);
            off = 0;
            foreach (var r in regions)
            {
                var info = new SDL_GPUTextureTransferInfo { transfer_buffer = tb, offset = (uint)off, pixels_per_row = (uint)r.w, rows_per_layer = (uint)r.h };
                var reg = new SDL_GPUTextureRegion { texture = moverAtlas ? _atlas2 : _atlas, x = (uint)r.x, y = (uint)r.y, w = (uint)r.w, h = (uint)r.h, d = 1 };
                SDL_UploadToGPUTexture(cp, &info, &reg, false);
                off += r.w * r.h * 4;
            }
            SDL_EndGPUCopyPass(cp);
            SDL_SubmitGPUCommandBuffer(cmd);
            SDL_ReleaseGPUTransferBuffer(_dev, tb);
        }

        // ---- actors (dynamic frame geometry) ----

        SDL_GPUBuffer* _dvb, _dib;
        uint _dvbCap, _dibCap;
        // frag: fragment mode (classic.hlsl mode.x); fog: vertex fog admission (uvoff.z).
        readonly List<(uint first, uint count, nint tex, int mode, int frag, bool mover, float ou, float ov, bool cull, bool fog, int group, bool noSmooth, bool alphaTest, int mirror, int polyFlags, int frameView)> _actorDraws = new();
        Vertex[] _dynV = Array.Empty<Vertex>();
        uint[] _dynI = Array.Empty<uint>();
        UETextureSource _src;

        static int ModeOf(ActorBlend b) => b switch
        {
            ActorBlend.Masked => 1, ActorBlend.Modulated => 3, ActorBlend.Translucent => 4, ActorBlend.AlphaBlend => 6, _ => 0,
        };

        // Mesh slot PF_RenderFog after D3DDrv normalization (AdjustPolyFlags/SetBlending 0x10009280/0x100092d0):
        // kept only without Translucent/Modulated; Opacity < 1 adds Translucent (0x10000004), so vertex fog lies only
        // on Opaque/Masked.
        static bool FogAllowed(ActorBlend b) => b == ActorBlend.Opaque || b == ActorBlend.Masked;

        static TexDraw TexDrawOf(ActorDraw d)
        {
            if (d.WorldMode >= 0) return d.AlphaTest ? TexDraw.Masked : TexDraw.Normal;
            if (d.AlphaTest) return TexDraw.Masked;
            return d.Blend switch
            {
                ActorBlend.AlphaBlend => d.OpacityMasked ? TexDraw.OpacityMasked : TexDraw.Opacity,
                _ => TexDraw.Normal,
            };
        }

        // Collect vertices/indices of all actor batches into the shared buffer (upload happens in the frame).
        public void SetActors(List<ActorDraw> draws)
        {
            _actorDraws.Clear(); _logSrc.Clear();
            int nv = 0, ni = 0;
            foreach (var d in draws) { nv += d.Vertices.Count; ni += d.Indices.Count; }
            if (_dynV.Length < nv) _dynV = new Vertex[Math.Max(nv, _dynV.Length * 2)];
            if (_dynI.Length < ni) _dynI = new uint[Math.Max(ni, _dynI.Length * 2)];
            int ov = 0, oi = 0;
            foreach (var d in draws)
            {
                if (d.Indices.Count == 0) continue;
                nint t = TextureFor(d.Texture, TexDrawOf(d), d.Lod);
                d.Vertices.CopyTo(_dynV, ov);
                for (int k = 0; k < d.Indices.Count; k++) _dynI[oi + k] = d.Indices[k] + (uint)ov;
                // Mover surface: mode and fragment by the BSP rule (lightmap from the mover atlas);
                // (lightmapped under any blend, as DrawWorld); actor mesh: actor style, texel × vertex light in gamma (unlit light is
                // GUnlitColor); Modulated is texel only:
                // D3DDrv DrawTriangles puts a white vertex colour under PF_Modulated (D3DDrv.dll.c:1017-1020).
                int mode = d.WorldMode >= 0 ? d.WorldMode : ModeOf(d.Blend);
                int frag = d.WorldMode >= 0 ? (d.Lightmapped ? 0 : 1) : RenderRules.ActorFrag(d.Blend == ActorBlend.Modulated);
                bool fog = d.WorldMode < 0 && FogAllowed(d.Blend);
                if (DrawLog.Path != null) _logSrc[(uint)oi] = d;
                _actorDraws.Add(((uint)oi, (uint)d.Indices.Count, t, mode, frag, d.MoverAtlas, d.UvOffU, d.UvOffV, !d.TwoSided && d.WorldMode < 0, fog, d.Group, d.NoSmooth, d.AlphaTest,
                    d.MirrorPlane >= 0 ? _levelMirrors + d.MirrorPlane : -1, d.PolyFlags, d.FrameView));
                ov += d.Vertices.Count; oi += d.Indices.Count;
            }
            _dynVCount = ov; _dynICount = oi;
            // Actor groups: no reordering, the list is in Core's DrawActorSprite order (SpriteMath.InCoreOrder). World
            // groups (movers, decals): opaque and masked before blended, as before fix/actor-order-core (the source-derived
            // mover/pass-2 order is open, not decided here). Groups are drawn separately, so only the in-group order counts.
            HPEngine.Sprites.SpriteMath.OpaqueFirst(_actorDraws, x => x.mode >= 2 && !RenderRules.IsActorGroup(x.group), _actorScratch);
        }
        // Blend factor name in D3D terms (the draw log is compared with the original D3DDrv).
        static string D3DName(SDL_GPUBlendFactor f) => f switch
        {
            SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ZERO => "ZERO",
            SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE => "ONE",
            SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_SRC_COLOR => "SRCCOLOR",
            SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_COLOR => "INVSRCCOLOR",
            SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_SRC_ALPHA => "SRCALPHA",
            SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_ALPHA => "INVSRCALPHA",
            SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_DST_COLOR => "DESTCOLOR",
            _ => f.ToString(),
        };
        readonly List<(uint first, uint count, nint tex, int mode, int frag, bool mover, float ou, float ov, bool cull, bool fog, int group, bool noSmooth, bool alphaTest, int mirror, int polyFlags, int frameView)> _actorScratch = new();
        int _dynVCount, _dynICount;
        readonly Dictionary<uint, ActorDraw> _logSrc = new();   // DrawLog: batch by index offset
        List<(float, float, float, float, float, float)> DynVerts(uint first, uint count)
        {
            var l = new List<(float, float, float, float, float, float)>((int)count);
            for (uint k = first; k < first + count; k++) { Vertex v = _dynV[_dynI[k]]; l.Add((v.X, v.Y, v.Z, v.R, v.G, v.B)); }
            return l;
        }

        void EnsureDyn(ref SDL_GPUBuffer* buf, ref uint cap, uint need, SDL_GPUBufferUsageFlags usage)
        {
            if (need <= cap && buf != null) return;
            if (buf != null) SDL_ReleaseGPUBuffer(_dev, buf);
            cap = Math.Max(need, cap * 2);
            var ci = new SDL_GPUBufferCreateInfo { usage = usage, size = cap };
            buf = SDL_CreateGPUBuffer(_dev, &ci);
        }

        void UploadActors(SDL_GPUCommandBuffer* cmd)
        {
            if (_dynICount == 0) return;
            uint vb = (uint)(_dynVCount * sizeof(Vertex)), ib = (uint)(_dynICount * 4);
            EnsureDyn(ref _dvb, ref _dvbCap, vb, SDL_GPUBufferUsageFlags.SDL_GPU_BUFFERUSAGE_VERTEX);
            EnsureDyn(ref _dib, ref _dibCap, ib, SDL_GPUBufferUsageFlags.SDL_GPU_BUFFERUSAGE_INDEX);
            var tci = new SDL_GPUTransferBufferCreateInfo { usage = SDL_GPUTransferBufferUsage.SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD, size = vb + ib };
            var tb = SDL_CreateGPUTransferBuffer(_dev, &tci);
            byte* map = (byte*)SDL_MapGPUTransferBuffer(_dev, tb, false);
            fixed (Vertex* v = _dynV) System.Buffer.MemoryCopy(v, map, vb, vb);
            fixed (uint* i = _dynI) System.Buffer.MemoryCopy(i, map + vb, ib, ib);
            SDL_UnmapGPUTransferBuffer(_dev, tb);
            var cp = SDL_BeginGPUCopyPass(cmd);
            var s0 = new SDL_GPUTransferBufferLocation { transfer_buffer = tb };
            var d0 = new SDL_GPUBufferRegion { buffer = _dvb, size = vb };
            SDL_UploadToGPUBuffer(cp, &s0, &d0, true);
            var s1 = new SDL_GPUTransferBufferLocation { transfer_buffer = tb, offset = vb };
            var d1 = new SDL_GPUBufferRegion { buffer = _dib, size = ib };
            SDL_UploadToGPUBuffer(cp, &s1, &d1, true);
            SDL_EndGPUCopyPass(cp);
            SDL_ReleaseGPUTransferBuffer(_dev, tb);
        }

        // ---- world surface animation ----
        float[] _uvOff = Array.Empty<float>();
        nint[] _animTex = Array.Empty<nint>();
        public int PannedBatches, AnimatedBatches;

        // wallSeconds: wall clock (same as TickProc); the AnimNext chain frame follows it (UTexture::Update). Only textures
        // Core's DrawFrame updated this frame advance (SetDrawn: WorldFrame.DrawnTextures); an undrawn chain holds.
        public void AnimateWorld(float levelTime, UETextureAnim anim, double wallSeconds)
        {
            if (_uvOff.Length != _draws.Count * 2) { _uvOff = new float[_draws.Count * 2]; _animTex = new nint[_draws.Count]; }
            anim?.BeginFrame(wallSeconds);
            PannedBatches = AnimatedBatches = 0;
            for (int i = 0; i < _draws.Count; i++)
            {
                Batch b = _draws[i].b;
                if (anim != null && _drawnTex.Contains(b.Material) && anim.IsAnimated(b.Material))
                {
                    string frame = anim.CurrentFrame(b.Material);
                    _animTex[i] = TextureFor(frame, b.AlphaTest ? TexDraw.Masked : TexDraw.Normal); AnimatedBatches++;
                }
                if (b.PanFlags != 0)
                {
                    SurfacePan.Pan(b.PanFlags, 0, 0, levelTime, b.PanSpeedU, b.PanSpeedV, out float pu, out float pv);
                    _uvOff[i * 2] = pu / b.TexW; _uvOff[i * 2 + 1] = pv / b.TexH; PannedBatches++;
                }
            }
        }

        // ---- frame ----

        static readonly string Dbg = Environment.GetEnvironmentVariable("HP1_DBG");
        readonly float[] _rows = new float[16];

        float[] _clip = new float[4];
        public int MirrorPasses, WarpPasses, SkyPasses;
        // Views whose target at least one batch used (mirror, portal, sky window, mover mirror): snapshot/probe.
        readonly HashSet<int> _sampledViews = new();
        public int ViewsSampled => _sampledViews.Count;
        int _levelMirrors;
        // World batches: portal per level surface (WorldMesh.WarpPortals), mirror by plane index
        // (level: WorldMesh.MirrorPlanes, followed by mover mirrors: _levelMirrors + MoverScene).
        readonly Dictionary<int, int> _portalOfSurf = new();

        // CHILD VIEWS: ViewTree (Core occlusion child frames, D-61). DrawFrame draws children before the parent
        // (Render.dll.c:1011-1014); here each into its own target, and the batch of the surface that spawned the frame
        // takes the target colour in screen coordinates. The sky frame draws only sky zones and takes no children.
        const int KindMirror = ViewTree.KindMirror, KindWarp = ViewTree.KindWarp, KindSky = ViewTree.KindSky;
        readonly ViewTree _tree = new();
        List<ViewTree.View> _views => _tree.Views;
        List<int> _frameOfView => _tree.FrameOf;
        IReadOnlyList<ViewFrame> _frameViews = Array.Empty<ViewFrame>();
        Func<ViewSource, int> _mirrorKey = _ => -1;
        SDL_GPUTexture*[] _viewRt = new SDL_GPUTexture*[0];
        int _viewW, _viewH, _curView;
        readonly HashSet<string> _viewRefusals = new();
        readonly float[] _viewRows = new float[16];

        // Display frame: child frames and mirror plane index per surface (level or mover polygon).
        public void SetViews(IReadOnlyList<ViewFrame> views, Func<ViewSource, int> mirrorKey)
        {
            _frameViews = views ?? Array.Empty<ViewFrame>();
            _mirrorKey = mirrorKey;
        }

        // WORLD POLYGONS FROM CORE: each view draws only the level nodes Core's OccludeBsp drew
        // in its frame (WorldFrame.Drawn: index = Core frame + 1; null — no world this frame) and its sky windows; the GPU
        // depth test is D3DDrv's Z-buffer. drawnTextures: the textures DrawFrame updated (AnimateWorld, TickProc).
        IReadOnlyList<DrawnWorld> _drawn;
        readonly HashSet<string> _drawnTex = new(StringComparer.OrdinalIgnoreCase);
        public void SetDrawn(IReadOnlyList<DrawnWorld> drawn, IReadOnlyList<string> drawnTextures)
        {
            _drawn = drawn;
            _drawnTex.Clear();
            if (drawnTextures != null) foreach (string t in drawnTextures) _drawnTex.Add(t);
        }
        DrawnWorld DrawnOfView(int vi)
        {
            int fi = (vi < _frameOfView.Count ? _frameOfView[vi] : -1) + 1;
            return _drawn != null && fi < _drawn.Count ? _drawn[fi] : DrawnWorld.Empty;
        }

        // Per view the world index ranges in _wib: _range[vi · draws + di] (WorldMesh.Select).
        readonly List<Batch> _batches = new();   // _draws order
        SDL_GPUBuffer* _wib; uint _wibCap;
        readonly List<uint> _wIdx = new();
        (uint first, uint count)[] _range = Array.Empty<(uint, uint)>();
        bool[] _mask = Array.Empty<bool>();
        List<int>[] _logSurfs = Array.Empty<List<int>>();   // DrawLog: drawn surfaces per (view, batch)

        void BuildWorldIndices()
        {
            int nd = _draws.Count, nv = _views.Count;
            if (_range.Length < nv * nd) _range = new (uint, uint)[nv * nd];
            if (_mask.Length != _nodeCount * 4) _mask = new bool[_nodeCount * 4];
            if (DrawLog.On && _logSurfs.Length < nv * nd) _logSurfs = new List<int>[nv * nd];
            _wIdx.Clear();
            for (int vi = 0; vi < nv; vi++)
            {
                int unportedBefore = _mesh?.UnportedBackZone ?? 0, reflagBefore = _mesh?.UnknownReflag ?? 0;
                if (WorldMesh.Select(_batches, _idx, DrawnOfView(vi), _mask, _wIdx, _range, vi * nd, DrawLog.On ? _logSurfs : null, _mesh) > 0)
                    Refuse($"view {vi}: Core drew level nodes outside the mesh's {_nodeCount} nodes (model mismatch) — not drawn");
                if ((_mesh?.UnknownReflag ?? 0) > reflagBefore)
                    Refuse("Core reflagged a level node with flags the mesh has no run for — drawn with its surface flags");
                if ((_mesh?.UnportedBackZone ?? 0) > unportedBefore)
                    Refuse("a node on a zone boundary drawn from behind has no back run (back side fully covered in the coplanar cut) — lit with its front zone");
            }
        }

        void UploadWorldIndices(SDL_GPUCommandBuffer* cmd)
        {
            if (_wIdx.Count == 0) return;
            uint ib = (uint)(_wIdx.Count * 4);
            EnsureDyn(ref _wib, ref _wibCap, ib, SDL_GPUBufferUsageFlags.SDL_GPU_BUFFERUSAGE_INDEX);
            var tci = new SDL_GPUTransferBufferCreateInfo { usage = SDL_GPUTransferBufferUsage.SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD, size = ib };
            var tb = SDL_CreateGPUTransferBuffer(_dev, &tci);
            byte* map = (byte*)SDL_MapGPUTransferBuffer(_dev, tb, false);
            fixed (uint* i = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_wIdx)) System.Buffer.MemoryCopy(i, map, ib, ib);
            SDL_UnmapGPUTransferBuffer(_dev, tb);
            var cp = SDL_BeginGPUCopyPass(cmd);
            var s0 = new SDL_GPUTransferBufferLocation { transfer_buffer = tb };
            var d0 = new SDL_GPUBufferRegion { buffer = _wib, size = ib };
            SDL_UploadToGPUBuffer(cp, &s0, &d0, true);
            SDL_EndGPUCopyPass(cp);
            SDL_ReleaseGPUTransferBuffer(_dev, tb);
        }

        int ChildOf(int kind, int key) => _tree.ChildOf(_curView, kind, key);

        // rows · M (M is the affine map from view world to root world).
        static void RowsOf(float[] root, in Affine m, float[] o)
        {
            for (int r = 0; r < 4; r++)
            {
                float a = root[r * 4], b = root[r * 4 + 1], c = root[r * 4 + 2], d = root[r * 4 + 3];
                o[r * 4] = a * m.M00 + b * m.M10 + c * m.M20;
                o[r * 4 + 1] = a * m.M01 + b * m.M11 + c * m.M21;
                o[r * 4 + 2] = a * m.M02 + b * m.M12 + c * m.M22;
                o[r * 4 + 3] = a * m.M03 + b * m.M13 + c * m.M23 + d;
            }
        }

        void Refuse(string why) { if (_viewRefusals.Add(why)) Console.Error.WriteLine($"[Views] STOP: {why}"); }

        void BuildViews() => _tree.Build(_frameViews, surf => _portalOfSurf.TryGetValue(surf, out int pi) ? pi : -1, _mirrorKey, Refuse);

        // Target per child view (view index; the root has no target).
        void EnsureViewRts(int w, int h)
        {
            if (_viewW != w || _viewH != h)
            {
                foreach (var t in _viewRt) if (t != null) SDL_ReleaseGPUTexture(_dev, t);
                _viewRt = new SDL_GPUTexture*[0];
                _viewW = w; _viewH = h;
            }
            if (_viewRt.Length >= _views.Count) return;
            var rt = new SDL_GPUTexture*[_views.Count];
            for (int i = 0; i < _viewRt.Length; i++) rt[i] = _viewRt[i];
            for (int i = Math.Max(1, _viewRt.Length); i < rt.Length; i++)
                rt[i] = Tex(_colorFmt, w, h, 1, SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_COLOR_TARGET | SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_SAMPLER);
            _viewRt = rt;
        }


        // Brightness: D3DDrv gamma-ramp exponent (ScreenEffects.GammaExponent from UClient.Brightness). If not 1, the
        // frame (world and 2D) is drawn into an intermediate target and output to target by the gamma pass (SdlHudPass.DrawGamma).
        public float GammaExponent = 1f;

        void Draw(SDL_GPUCommandBuffer* cmd, SDL_GPUTexture* target, int w, int h, Camera cam)
        {
            if (MathF.Abs(GammaExponent - 1f) < 1e-6f || !EnsureGamma(w, h)) { DrawScene(cmd, target, w, h, cam); return; }
            DrawScene(cmd, _gammaSrc, w, h, cam);
            DrawGamma(cmd, target);
        }

        void DrawScene(SDL_GPUCommandBuffer* cmd, SDL_GPUTexture* target, int w, int h, Camera cam)
        {
            EnsureDepth(w, h);
            UploadActors(cmd);
            UploadHud(cmd);
            if (HideWorld)   // loading screen / travel hold: black + 2D
            {
                var bp = BeginPass(cmd, target, clearColor: true);
                SDL_EndGPURenderPass(bp);
                DrawHud(cmd, target, w, h);
                return;
            }
            var fu = stackalloc float[16];
            var samplers = stackalloc SDL_GPUTextureSamplerBinding[3];
            cam.Rows(w, h, _rows);
            // Child views (mirrors, warp portals, sky) before the parent, each into its own target: view camera (rows · M),
            // view NearClip clipping. The sky view is the sky-zone geometry (Render.dll.c:6047-6094); it reaches the parent
            // frame only through PF_FakeBackdrop windows (DrawWorld: the window takes the sky target colour in screen
            // coordinates, like a mirror). Everything else is the UGameEngine::Draw clear (ScreenClear FPlane(0,0,0,0),
            // Engine 0x1039fd44): BSP T-junction cracks are black, not sky.
            BuildViews();
            EnsureViewRts(w, h);
            BuildWorldIndices();
            UploadWorldIndices(cmd);
            for (int vi = 0; vi < _views.Count; vi++) FogTex(_frameOfView[vi] + 1);   // every view's atlas exists before the passes
            MirrorPasses = WarpPasses = SkyPasses = 0;
            _sampledViews.Clear();
            for (int vi = _views.Count - 1; vi >= 1; vi--)
            {
                ViewTree.View v = _views[vi];
                RowsOf(_rows, v.M, _viewRows);
                fixed (float* rr = _viewRows) SDL_PushGPUVertexUniformData(cmd, 0, (nint)rr, 64);
                _clip[0] = v.ClipN.x; _clip[1] = v.ClipN.y; _clip[2] = v.ClipN.z; _clip[3] = v.ClipD;
                _curView = vi;
                DrawLog.Pass = ViewName(vi);
                if (DrawLog.On) LogView(vi, cam);
                var mp = BeginPass(cmd, _viewRt[vi], clearColor: true);
                DrawGroups(cmd, mp, fu, samplers);
                SDL_EndGPURenderPass(mp);
                if (v.Kind == KindMirror) MirrorPasses++; else if (v.Kind == KindWarp) WarpPasses++; else SkyPasses++;
            }
            _curView = 0;
            _clip[0] = _clip[1] = _clip[2] = _clip[3] = 0;
            // Level: sky windows, mirrors, portals from their child views.
            fixed (float* rows = _rows) SDL_PushGPUVertexUniformData(cmd, 0, (nint)rows, 64);
            DrawLog.Pass = "main";
            var main = BeginPass(cmd, target, clearColor: true);
            _screenW = w; _screenH = h;
            DrawGroups(cmd, main, fu, samplers);
            SDL_EndGPURenderPass(main);
            DrawHud(cmd, target, w, h);   // 2D frame over the world (Hud)
        }
        int _screenW, _screenH;

        // Draw-log pass of a view: the chain from the root (mirror<view>, warp<iSurf>, sky; nested through '/'), like the
        // original's CHILD chain the judge rebuilds (drawlog_compare.py). iSurf — the surface that created the Core frame.
        string ViewName(int vi)
        {
            if (vi == 0) return "main";
            ViewTree.View v = _views[vi];
            string seg = v.Kind == KindMirror ? "mirror" + vi : v.Kind == KindWarp ? "warp" + WarpSurf(vi) : "sky";
            return v.Parent == 0 ? seg : ViewName(v.Parent) + "/" + seg;
        }
        int WarpSurf(int vi) => _frameViews[_frameOfView[vi]].Sources is { Length: > 0 } s ? s[0].Surf : -1;

        // VIEW: the Core frame of a view as the original's CHILD (kind, iSurf, zone, the parent frame's eye); MIRROR — the plane.
        void LogView(int vi, Camera cam)
        {
            ViewTree.View v = _views[vi];
            ViewFrame fv = _frameViews[_frameOfView[vi]];
            var pc = v.Parent == 0 ? new UEVector(cam.X, cam.Y, cam.Z) : _frameViews[_frameOfView[v.Parent]].Coords.Origin;
            DrawLog.View(ViewName(vi), ViewName(v.Parent), v.Kind == KindMirror ? "mirror" : v.Kind == KindWarp ? "warp" : "sky",
                v.Kind == KindWarp ? WarpSurf(vi) : 0, fv.Zone, pc, fv.Coords.Origin);
            if (v.Kind == KindMirror) DrawLog.MirrorPlane(vi, ViewName(vi), v.ClipN, v.ClipD);
        }

        void SetClip(float* fu) { fu[8] = _clip[0]; fu[9] = _clip[1]; fu[10] = _clip[2]; fu[11] = _clip[3]; fu[12] = _screenW > 0 ? 1f / _screenW : 0; fu[13] = _screenH > 0 ? 1f / _screenH : 0; fu[14] = fu[15] = 0; }

        // Frame groups in order (RenderRules.OpaqueWorld..LateActors): the group's world, then the group's dynamic batches
        // (movers and decals in world groups, actors in their own).
        void DrawGroups(SDL_GPUCommandBuffer* cmd, SDL_GPURenderPass* pass, float* fu, SDL_GPUTextureSamplerBinding* samplers)
        {
            for (int g = 0; g < RenderRules.Groups; g++)
            {
                DrawWorld(cmd, pass, fu, samplers, g);
                DrawActors(cmd, pass, fu, samplers, g);
            }
        }

        void DrawActors(SDL_GPUCommandBuffer* cmd, SDL_GPURenderPass* pass, float* fu, SDL_GPUTextureSamplerBinding* samplers, int group)
        {
            bool mirrored = _views.Count > _curView && _views[_curView].Flip;
            if (_dynICount <= 0) return;
            var avb = new SDL_GPUBufferBinding { buffer = _dvb };
            var aib = new SDL_GPUBufferBinding { buffer = _dib };
            SDL_BindGPUVertexBuffers(pass, 0, &avb, 1);
            SDL_BindGPUIndexBuffer(pass, &aib, SDL_GPUIndexElementSize.SDL_GPU_INDEXELEMENTSIZE_32BIT);
            int bound = -1;
            int frameOfView = _curView < _frameOfView.Count ? _frameOfView[_curView] : -1;
            foreach (var d in _actorDraws)
            {
                if (d.group != group) continue;
                if (d.frameView != HPEngine.Sprites.SpriteDraw.AnyView && d.frameView != frameOfView) continue;   // batch of another frame
                int child = d.mirror >= 0 ? ChildOf(KindMirror, d.mirror) : -1;
                if (child >= 0)
                {
                    // Mover mirror, like a level mirror (DrawWorld): reflection with the opaque pipeline, then, if
                    // RenderRules.MirrorSurfaceDrawn, the surface itself with its texture and mode (below, regular path).
                    // Without a child view (Recursion limit, not visible): a regular surface.
                    int ridx = (d.cull ? (mirrored ? 2 : 1) : 0) * Modes;
                    if (ridx != bound) { SDL_BindGPUGraphicsPipeline(pass, _pipe[ridx]); bound = ridx; }
                    fu[0] = 4; fu[1] = 0; fu[2] = Dbg == "red" || (Dbg == "movers" && d.mover) ? 1 : 0; fu[3] = 0; fu[4] = d.ou; fu[5] = d.ov; fu[6] = 0; fu[7] = 0;
                    SetClip(fu);
                    SDL_PushGPUFragmentUniformData(cmd, 0, (nint)fu, 64);
                    _sampledViews.Add(child);
                    samplers[0] = new SDL_GPUTextureSamplerBinding { texture = _viewRt[child], sampler = _lmSampler };
                    samplers[1] = new SDL_GPUTextureSamplerBinding { texture = _atlas2 != null ? _atlas2 : _atlas, sampler = _lmSampler };
                    samplers[2] = new SDL_GPUTextureSamplerBinding { texture = _fog, sampler = _lmSampler };
                    SDL_BindGPUFragmentSamplers(pass, 0, samplers, 3);
                    if (DrawLog.On) DrawLog.Draw("MIRRORREFL", d.group, ridx, false, d.polyFlags, "mirror" + child, _logSrc.GetValueOrDefault(d.first)?.LogOwner, null, d.count);
                    SDL_DrawGPUIndexedPrimitives(pass, d.count, 1, d.first, 0, 0);
                    if (!RenderRules.MirrorSurfaceDrawn(d.polyFlags)) continue;
                }
                // Mesh slot without PF_TwoSided: back-face culling (render = slot flags | style, Render.dll.c:9738).
                // The reflected pass flips winding, so front faces are culled.
                int pidx = (d.cull ? (mirrored ? 2 : 1) : 0) * Modes + d.mode;
                if (pidx != bound) { SDL_BindGPUGraphicsPipeline(pass, _pipe[pidx]); bound = pidx; }
                fu[0] = d.frag; fu[1] = d.alphaTest ? 1 : 0; fu[2] = Dbg == "red" || (Dbg == "movers" && d.mover) ? 1 : 0; fu[3] = 0; fu[4] = d.ou; fu[5] = d.ov;
                fu[6] = d.fog ? 1 : 0; fu[7] = d.mode == 6 ? 1 : 0;
                SetClip(fu);
                SDL_PushGPUFragmentUniformData(cmd, 0, (nint)fu, 64);
                samplers[0] = new SDL_GPUTextureSamplerBinding { texture = (SDL_GPUTexture*)d.tex, sampler = d.noSmooth ? _point : World };
                samplers[1] = new SDL_GPUTextureSamplerBinding { texture = _atlas2 != null ? _atlas2 : _atlas, sampler = _lmSampler };
                samplers[2] = new SDL_GPUTextureSamplerBinding { texture = _fog, sampler = _lmSampler };
                SDL_BindGPUFragmentSamplers(pass, 0, samplers, 3);
                if (DrawLog.On && _logSrc.GetValueOrDefault(d.first) is { } ld) DrawLog.Draw(ld.LogKind ?? "MESH", d.group, pidx, d.alphaTest, d.polyFlags, ld.Texture, ld.LogOwner, null, d.count, DrawLog.Geom(_curView == 0 ? _rows : _viewRows, DynVerts(d.first, d.count), pidx / Modes));
                SDL_DrawGPUIndexedPrimitives(pass, d.count, 1, d.first, 0, 0);
            }
        }

        SDL_GPURenderPass* BeginPass(SDL_GPUCommandBuffer* cmd, SDL_GPUTexture* target, bool clearColor)
        {
            var color = new SDL_GPUColorTargetInfo
            {
                texture = target, clear_color = new SDL_FColor { r = 0, g = 0, b = 0, a = 1 },
                load_op = clearColor ? SDL_GPULoadOp.SDL_GPU_LOADOP_CLEAR : SDL_GPULoadOp.SDL_GPU_LOADOP_LOAD,
                store_op = SDL_GPUStoreOp.SDL_GPU_STOREOP_STORE,
            };
            var depth = new SDL_GPUDepthStencilTargetInfo
            {
                texture = _depth, clear_depth = 1, load_op = SDL_GPULoadOp.SDL_GPU_LOADOP_CLEAR, store_op = SDL_GPUStoreOp.SDL_GPU_STOREOP_DONT_CARE,
                stencil_load_op = SDL_GPULoadOp.SDL_GPU_LOADOP_DONT_CARE, stencil_store_op = SDL_GPUStoreOp.SDL_GPU_STOREOP_DONT_CARE,
            };
            return SDL_BeginGPURenderPass(cmd, &color, 1, &depth);
        }

        // The view's world batches of one frame group: only Core's drawn nodes (BuildWorldIndices), no culling.
        void DrawWorld(SDL_GPUCommandBuffer* cmd, SDL_GPURenderPass* pass, float* fu, SDL_GPUTextureSamplerBinding* samplers, int group)
        {
            if (_wIdx.Count == 0) return;
            var vb = new SDL_GPUBufferBinding { buffer = _vb };
            var ib = new SDL_GPUBufferBinding { buffer = _wib };
            SDL_BindGPUVertexBuffers(pass, 0, &vb, 1);
            SDL_BindGPUIndexBuffer(pass, &ib, SDL_GPUIndexElementSize.SDL_GPU_INDEXELEMENTSIZE_32BIT);
            int bound = -1;
            for (int di = 0; di < _draws.Count; di++)
            {
                var (b, tex0) = _draws[di];
                nint tex = _animTex[di] != 0 ? _animTex[di] : tex0;
                if (b.Group != group) continue;
                var (first, count) = _range[_curView * _draws.Count + di];
                if (count == 0) continue;
                // Warp portal: only with a child view, its target colour with Z write (PF_Invisible|PF_Occlude in D3DDrv is
                // depth only over the child's colour); without a child the invisible surface is not drawn.
                // Sky window: the sky view's colour; without one (no sky, Recursion limit) depth only (mode 5).
                int child = b.WarpPortal >= 0 ? ChildOf(KindWarp, b.WarpPortal) : b.MirrorPlane >= 0 ? ChildOf(KindMirror, b.MirrorPlane)
                    : b.Backdrop ? ChildOf(KindSky, 0) : -1;
                if (b.WarpPortal >= 0 && child < 0) continue;
                // Mirror: reflection first (opaque pipeline, Z write), then, if the surface has
                // Masked/Translucent/Modulated, the surface itself with its texture and blending (RenderRules.MirrorSurfaceDrawn).
                // Without a child view (Recursion limit, not visible): a regular surface.
                for (int step = child >= 0 ? 0 : 1; step < 2; step++)
                {
                    bool refl = step == 0;
                    if (!refl && child >= 0 && (b.WarpPortal >= 0 || b.Backdrop || !RenderRules.MirrorSurfaceDrawn(b.PolyFlags))) break;
                    if (refl) _sampledViews.Add(child);
                    nint t = refl ? (nint)_viewRt[child] : tex;
                    int mode = refl ? 0 : b.Backdrop ? 5 : (int)b.Blend;
                    int pidx = mode;   // CULLMODE NONE (D3DDrv): OccludeBsp chose the side
                    if (pidx != bound) { SDL_BindGPUGraphicsPipeline(pass, _pipe[pidx]); bound = pidx; }
                    // Lightmap whenever Core gave the surface one, any blend: Render DrawFrame gates SetupForSurf on iLightMap/RendMap/
                    // Model/frame only (disasm 0x10b25d44..0x10b25da0) and D3DDrv DrawComplexSurface takes the multitexture pass
                    // SetBlending(PolyFlags | 0x1000000) (texture stage 1 MODULATE2X with the lightmap) for any PolyFlags (D3DDrv.dll.c:5775-5776, 6135-6137).
                    fu[0] = refl ? 4 : b.Unlit ? 1 : 0; fu[1] = !refl && b.AlphaTest ? 1 : 0; fu[2] = Dbg == "red" ? 1 : Dbg == "tex" ? -1 : 0; fu[3] = Dbg == "lm" ? 1 : 0;
                    fu[4] = _uvOff[di * 2]; fu[5] = _uvOff[di * 2 + 1]; fu[6] = b.SpecialLit ? 1 : 0; fu[7] = 0;
                    SetClip(fu);
                    bool fogPass = !refl && b.FogPass;
                    if (fogPass) fu[14] = 1;   // the fog map is not folded: its own pass below
                    SDL_PushGPUFragmentUniformData(cmd, 0, (nint)fu, 64);
                    samplers[0] = new SDL_GPUTextureSamplerBinding { texture = (SDL_GPUTexture*)t, sampler = refl ? _lmSampler : b.NoSmooth ? _point : World };
                    samplers[1] = new SDL_GPUTextureSamplerBinding { texture = _atlas, sampler = _lmSampler };
                    samplers[2] = new SDL_GPUTextureSamplerBinding { texture = _fog, sampler = _lmSampler };
                    SDL_BindGPUFragmentSamplers(pass, 0, samplers, 3);
                    if (DrawLog.On) DrawLog.Draw(!refl ? "WORLD" : b.WarpPortal >= 0 ? "WARPREFL" : "MIRRORREFL", b.Group, pidx, !refl && b.AlphaTest, b.PolyFlags,
                        !refl ? b.Material : b.WarpPortal >= 0 ? "warp" + WarpSurf(child) : "mirror" + child, $"{b.At.x:F0},{b.At.y:F0},{b.At.z:F0}", _logSurfs[_curView * _draws.Count + di], count);
                    SDL_DrawGPUIndexedPrimitives(pass, count, 1, first, 0, 0);
                    if (!fogPass) continue;
                    // The surface's fog map pass right after it (DrawComplexSurface, D3DDrv.dll.c:6258-6265): ONE/INVSRCALPHA
                    // over what the surface pass wrote; no alpha test (SetBlending(0x10000000) has no PF_Masked).
                    SDL_BindGPUGraphicsPipeline(pass, _pipe[7]); bound = 7;
                    fu[0] = 3; fu[1] = 0;
                    SDL_PushGPUFragmentUniformData(cmd, 0, (nint)fu, 64);
                    if (DrawLog.On) DrawLog.Draw("WORLDFOG", b.Group, 7, false, 0x10000000, b.Material, $"{b.At.x:F0},{b.At.y:F0},{b.At.z:F0}", _logSurfs[_curView * _draws.Count + di], count);
                    SDL_DrawGPUIndexedPrimitives(pass, count, 1, first, 0, 0);
                }
            }
        }

        public void RenderOffscreen(Camera cam, out byte[] rgba, bool download = true)
        {
            rgba = null;
            var cmd = SDL_AcquireGPUCommandBuffer(_dev);
            Draw(cmd, _color, Width, Height, cam);
            if (download)
            {
                var cp = SDL_BeginGPUCopyPass(cmd);
                var reg = new SDL_GPUTextureRegion { texture = _color, w = (uint)Width, h = (uint)Height, d = 1 };
                var info = new SDL_GPUTextureTransferInfo { transfer_buffer = _download };
                SDL_DownloadFromGPUTexture(cp, &reg, &info);
                SDL_EndGPUCopyPass(cp);
            }
            var fence = SDL_SubmitGPUCommandBufferAndAcquireFence(cmd);
            SDL_WaitForGPUFences(_dev, true, &fence, 1);
            SDL_ReleaseGPUFence(_dev, fence);
            if (!download) return;
            rgba = new byte[Width * Height * 4];
            new ReadOnlySpan<byte>((void*)SDL_MapGPUTransferBuffer(_dev, _download, false), rgba.Length).CopyTo(rgba);
            SDL_UnmapGPUTransferBuffer(_dev, _download);
        }

        public void RenderToWindow(Camera cam)
        {
            if (Offscreen) { RenderOffscreen(cam, out _, download: false); return; }
            var cmd = SDL_AcquireGPUCommandBuffer(_dev);
            SDL_GPUTexture* sc; uint w, h;
            if (!SDL_WaitAndAcquireGPUSwapchainTexture(cmd, Window, &sc, &w, &h) || sc == null) { SDL_SubmitGPUCommandBuffer(cmd); return; }
            Draw(cmd, sc, (int)w, (int)h, cam);
            SDL_SubmitGPUCommandBuffer(cmd);
        }
    }
}
