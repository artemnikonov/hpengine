// --gpu: runs hud.hlsl (SPIR-V blob from Hud/shaders) on an SDL_GPU device (headless:
// Vulkan lavapipe, SDL_VIDEO_DRIVER=offscreen). HudBatcher builds quads from synthetic
// commands; the frame is downloaded and compared with bytes computed HERE from the reference formulas
// (HP1CanvasTile/HP1ParticleAdd/HP1SpriteModulate/HP1ViewFlash/HP1GammaRamp of the former Unity frontend, gamma target)
// and the reference `Blend` rules — not from the frame itself. Tolerance 1 byte (float -> UNORM8).
//
// Target 64×64 R8G8B8A8_UNORM, background D. Cells 16×16, textures 2×2 (texel = 8×8 pixels,
// point sampling); texel centres are checked (+4 from the texel edge), so neither filtering
// nor edge rasterisation enters the comparison. Distinct 2×2 texels and the cell position in the
// top screen row catch a V flip and the clip Y sign.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HP1.UEScript;
using HPEngine.Hud;
using SDL;
using static SDL.SDL3;

static unsafe class GpuTest
{
    const int N = 64, Tol = 1;
    static readonly byte[] D = { 60, 120, 200, 255 };

    static SDL_GPUDevice* _dev;
    static SDL_GPUTexture* _target;
    static SDL_GPUTransferBuffer* _download;
    static SDL_GPUSampler* _point;
    static readonly Dictionary<HudBlend, nint> _pipes = new();
    static readonly Dictionary<string, nint> _tex = new();
    static int _fail, _pass;

    // 2×2 textures, row 0 is the top (UE row load order).
    static readonly Dictionary<string, byte[][]> Texels = new()
    {
        ["T.Opaque"] = new[] { new byte[] { 200, 100, 50, 255 }, new byte[] { 10, 250, 128, 255 }, new byte[] { 255, 255, 255, 255 }, new byte[] { 0, 64, 192, 255 } },
        ["T.Alpha"]  = new[] { new byte[] { 250, 20, 90, 255 }, new byte[] { 0, 0, 0, 0 }, new byte[] { 200, 200, 40, 128 }, new byte[] { 30, 60, 90, 255 } },
        ["T.Add"]    = new[] { new byte[] { 100, 50, 200, 255 }, new byte[] { 0, 0, 0, 255 }, new byte[] { 255, 128, 64, 255 }, new byte[] { 20, 40, 60, 255 } },
        ["T.Mod"]    = new[] { new byte[] { 128, 128, 128, 255 }, new byte[] { 200, 50, 100, 0 }, new byte[] { 64, 200, 255, 255 }, new byte[] { 0, 255, 32, 255 } },
        ["T.Frame"]  = new[] { new byte[] { 0, 128, 255, 255 }, new byte[] { 1, 64, 200, 255 }, new byte[] { 17, 100, 240, 255 }, new byte[] { 254, 33, 77, 255 } },
    };

    public static int Run()
    {
        // The hint yields to the SDL_VIDEO_DRIVER environment variable (then SetHint = false) — not a refusal.
        SDL_SetHint("SDL_VIDEO_DRIVER", "offscreen");
        if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO))
            return Refuse($"SDL_Init: {SDL_GetError()}");
        _dev = SDL_CreateGPUDevice(SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_SPIRV, false, "vulkan");
        if (_dev == null) return Refuse($"SDL_CreateGPUDevice(SPIR-V, vulkan): {SDL_GetError()}");
        Console.WriteLine($"GPU: driver {SDL_GetGPUDeviceDriver(_dev)}");
        string dir = Path.Combine(SrcDir(), "..", "..", "Hud", "shaders");
        var vs = Shader(Path.Combine(dir, "hud.vert.spv"), "vs_main", SDL_GPUShaderStage.SDL_GPU_SHADERSTAGE_VERTEX, 0);
        var fs = Shader(Path.Combine(dir, "hud.frag.spv"), "fs_main", SDL_GPUShaderStage.SDL_GPU_SHADERSTAGE_FRAGMENT, 1);
        if (vs == null || fs == null) return Refuse($"shader: {SDL_GetError()}");
        foreach (HudBlend b in Enum.GetValues<HudBlend>())
        {
            nint p = (nint)Pipeline(vs, fs, b);
            if (p == 0) return Refuse($"pipeline {b}: {SDL_GetError()}");
            _pipes[b] = p;
        }
        _target = Tex(N, N, SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_COLOR_TARGET);
        var tci = new SDL_GPUTransferBufferCreateInfo { usage = SDL_GPUTransferBufferUsage.SDL_GPU_TRANSFERBUFFERUSAGE_DOWNLOAD, size = N * N * 4 };
        _download = SDL_CreateGPUTransferBuffer(_dev, &tci);
        var sci = new SDL_GPUSamplerCreateInfo
        {
            min_filter = SDL_GPUFilter.SDL_GPU_FILTER_NEAREST, mag_filter = SDL_GPUFilter.SDL_GPU_FILTER_NEAREST,
            mipmap_mode = SDL_GPUSamplerMipmapMode.SDL_GPU_SAMPLERMIPMAPMODE_NEAREST,
            address_mode_u = SDL_GPUSamplerAddressMode.SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE,
            address_mode_v = SDL_GPUSamplerAddressMode.SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE,
            address_mode_w = SDL_GPUSamplerAddressMode.SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE,
        };
        _point = SDL_CreateGPUSampler(_dev, &sci);
        foreach (var (name, t) in Texels)
        {
            var tex = Tex(2, 2, SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_SAMPLER);
            Upload(tex, t.SelectMany(x => x).ToArray(), 2, 2);
            _tex[name] = (nint)tex;
        }
        var white = Tex(1, 1, SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_SAMPLER);
        Upload(white, new byte[] { 255, 255, 255, 255 }, 1, 1);
        _tex["<white>"] = (nint)white;

        CellsScene();
        FlashScene();
        GammaScene();

        Console.WriteLine($"HudGpu: {_pass} checks passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    static int Refuse(string why)
    {
        Console.WriteLine($"HudGpu REFUSED: {why}");
        return 2;
    }

    static string SrcDir([CallerFilePath] string p = "") => Path.GetDirectoryName(p);

    // ── scenes ──────────────────────────────────────────────────────────────

    static HudCommand Tile(float x, string tex, int pf, float r, float g, float b)
        => new HudCommand(HudKind.Tile, x, 0, 16, 16, 0, 0, 2, 2, tex, null, null, new UEVector(r, g, b), 1, false, false, null, pf);

    static void CellsScene()
    {
        var hb = new HudBatcher(p => p.StartsWith("T.") ? (2, 2) : null, _ => null);
        var frame = new WorldFrame(1, "gpu", Array.Empty<ActorSnapshot>(), default, Array.Empty<AudioCommand>(), default,
            new[]
            {
                Tile(0, "T.Opaque", 0, 128, 255, 51),
                Tile(16, "T.Alpha", HudBatcher.PF_Masked, 255, 128, 255),
                Tile(32, "T.Add", HudBatcher.PF_Translucent, 255, 128, 255),
                Tile(48, "T.Mod", HudBatcher.PF_Modulated | HudBatcher.PF_Masked, 1, 2, 3),
            }, Array.Empty<ParticleInstance>(), 0);
        var quads = hb.Build(frame, N, N, 0);
        Check("cells-quads", quads.Count == 4, $"quads {quads.Count}");
        byte[] px = Render(quads, 1f);

        for (int k = 0; k < 4; k++)
        {
            int ox = k * 16;
            string name = new[] { "T.Opaque", "T.Alpha", "T.Add", "T.Mod" }[k];
            double[] tint = k switch
            {
                0 => new[] { 128 / 255.0, 1.0, 51 / 255.0 },
                1 => new[] { 1.0, 128 / 255.0, 1.0 },
                2 => new[] { 1.0, 128 / 255.0, 1.0 },
                _ => new[] { 1.0, 1.0, 1.0 },         // modulation: white vertex colour (HudBatcher)
            };
            for (int ty = 0; ty < 2; ty++)
                for (int tx = 0; tx < 2; tx++)
                {
                    byte[] t = Texels[name][ty * 2 + tx];
                    double[] e = new double[3];
                    double ta = t[3] / 255.0;
                    for (int c = 0; c < 3; c++)
                    {
                        double tc = t[c] / 255.0, d = D[c] / 255.0;
                        e[c] = k switch
                        {
                            // CanvasTile, One/Zero: saturate(t·c).
                            0 => Sat(tc * tint[c]),
                            // CanvasTile, SrcAlpha/OneMinusSrcAlpha: s·a + d·(1−a), a = t.a·1.
                            1 => Sat(Sat(tc * tint[c]) * ta + d * (1 - ta)),
                            // ParticleAdd, One/OneMinusSrcColor: s + d·(1−s), s = t·c.
                            2 => Sat(tc * tint[c] + d * (1 - tc * tint[c])),
                            // SpriteModulate, DstColor/SrcColor: 2·s·d; under the mask (a<0.5) — background.
                            _ => ta < 0.5 ? d : Sat(2 * tc * d),
                        };
                    }
                    Pixel($"cell-{name}-texel{tx}{ty}", px, ox + tx * 8 + 4, ty * 8 + 4, e);
                }
        }
        // Outside the quads — background (cells in the TOP screen row: clip Y sign).
        Pixel("cells-background-below", px, 8, 40, new[] { D[0] / 255.0, D[1] / 255.0, D[2] / 255.0 });
    }

    // EndFlash: One/SrcAlpha ⇒ rgb + d·a (HP1ViewFlash.shader).
    static void FlashScene()
    {
        var vf = new ViewFlashState(0.2f, 1.7f, -0.5f, 0.6f);  // rgb (0.2, 1, 0), scale 0.3 -> a 0.6
        HudQuad? q = ScreenEffects.FlashQuad(vf, N, N);
        Check("flash-quad", q.HasValue);
        if (!q.HasValue) return;
        byte[] px = Render(new[] { q.Value }, 1f);
        double a = 0.6;
        var e = new[] { Sat(0.2 + D[0] / 255.0 * a), Sat(1.0 + D[1] / 255.0 * a), Sat(0.0 + D[2] / 255.0 * a) };
        Pixel("flash-center", px, 32, 32, e);
        Pixel("flash-corner", px, 1, 62, e);
    }

    // Gamma ramp: the same fragment shader, branch 3, over a frame copy (here T.Frame over the whole screen).
    static void GammaScene()
    {
        float e = ScreenEffects.GammaExponent(0.5f);   // 0.8
        var quad = new HudQuad(0, 0, N, N, 0, 0, 1, 1, 1, 1, 1, 1, HudBlend.Opaque, false, "T.Frame", HudQuadSource.Gamma, -1);
        byte[] px = Render(new[] { quad }, e, gamma: true);
        for (int ty = 0; ty < 2; ty++)
            for (int tx = 0; tx < 2; tx++)
            {
                byte[] t = Texels["T.Frame"][ty * 2 + tx];
                var exp = new double[3];
                for (int c = 0; c < 3; c++) exp[c] = Math.Round(Math.Pow(t[c] / 255.0, 0.8) * 65535) / 65535;
                Pixel($"gamma-texel{tx}{ty}", px, tx * 32 + 16, ty * 32 + 16, exp);
            }
    }

    static double Sat(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; return; }
        _fail++;
        Console.WriteLine($"FAIL {name}{(detail.Length > 0 ? ": " + detail : "")}");
    }

    static void Pixel(string name, byte[] px, int x, int y, double[] e)
    {
        int o = (y * N + x) * 4;
        bool ok = true;
        var exp = new int[3];
        for (int c = 0; c < 3; c++)
        {
            exp[c] = (int)Math.Round(e[c] * 255);
            ok &= Math.Abs(px[o + c] - exp[c]) <= Tol;
        }
        Check(name, ok, $"({x},{y}) expected ({exp[0]},{exp[1]},{exp[2]}), got ({px[o]},{px[o + 1]},{px[o + 2]})");
    }

    // ── SDL_GPU ─────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    struct Vtx { public float X, Y, U, V, R, G, B, A; }

    static byte[] Render(IReadOnlyList<HudQuad> quads, float exponent, bool gamma = false)
    {
        var verts = new Vtx[quads.Count * 6];
        for (int i = 0; i < quads.Count; i++)
        {
            HudQuad q = quads[i];
            Vtx V(float x, float y, float u, float v) => new() { X = x, Y = y, U = u, V = v, R = q.R, G = q.G, B = q.B, A = q.A };
            Vtx a = V(q.X0, q.Y0, q.U0, q.V0), b = V(q.X1, q.Y0, q.U1, q.V0), c = V(q.X1, q.Y1, q.U1, q.V1), d = V(q.X0, q.Y1, q.U0, q.V1);
            verts[i * 6] = a; verts[i * 6 + 1] = b; verts[i * 6 + 2] = c; verts[i * 6 + 3] = a; verts[i * 6 + 4] = c; verts[i * 6 + 5] = d;
        }
        byte[] vbytes = MemoryMarshal.AsBytes(verts.AsSpan()).ToArray();
        var bci = new SDL_GPUBufferCreateInfo { usage = SDL_GPUBufferUsageFlags.SDL_GPU_BUFFERUSAGE_VERTEX, size = (uint)vbytes.Length };
        var vb = SDL_CreateGPUBuffer(_dev, &bci);
        var tci = new SDL_GPUTransferBufferCreateInfo { usage = SDL_GPUTransferBufferUsage.SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD, size = (uint)vbytes.Length };
        var tb = SDL_CreateGPUTransferBuffer(_dev, &tci);
        vbytes.CopyTo(new Span<byte>((void*)SDL_MapGPUTransferBuffer(_dev, tb, false), vbytes.Length));
        SDL_UnmapGPUTransferBuffer(_dev, tb);

        var cmd = SDL_AcquireGPUCommandBuffer(_dev);
        var cp = SDL_BeginGPUCopyPass(cmd);
        var srcLoc = new SDL_GPUTransferBufferLocation { transfer_buffer = tb };
        var dst = new SDL_GPUBufferRegion { buffer = vb, size = (uint)vbytes.Length };
        SDL_UploadToGPUBuffer(cp, &srcLoc, &dst, false);
        SDL_EndGPUCopyPass(cp);

        var color = new SDL_GPUColorTargetInfo
        {
            texture = _target, clear_color = new SDL_FColor { r = D[0] / 255f, g = D[1] / 255f, b = D[2] / 255f, a = 1 },
            load_op = SDL_GPULoadOp.SDL_GPU_LOADOP_CLEAR, store_op = SDL_GPUStoreOp.SDL_GPU_STOREOP_STORE,
        };
        float* vu = stackalloc float[4] { N, N, 0, 0 };
        SDL_PushGPUVertexUniformData(cmd, 0, (nint)vu, 16);
        var pass = SDL_BeginGPURenderPass(cmd, &color, 1, null);
        var vbb = new SDL_GPUBufferBinding { buffer = vb };
        SDL_BindGPUVertexBuffers(pass, 0, &vbb, 1);
        float* fu = stackalloc float[4];
        for (int i = 0; i < quads.Count; i++)
        {
            HudQuad q = quads[i];
            SDL_BindGPUGraphicsPipeline(pass, (SDL_GPUGraphicsPipeline*)_pipes[q.Blend]);
            fu[0] = gamma ? 3 : HudBlendStates.ShaderMode(q.Blend); fu[1] = q.AlphaClip ? 1 : 0; fu[2] = exponent; fu[3] = 0;
            SDL_PushGPUFragmentUniformData(cmd, 0, (nint)fu, 16);
            var sb = new SDL_GPUTextureSamplerBinding { texture = (SDL_GPUTexture*)_tex[q.Texture ?? "<white>"], sampler = _point };
            SDL_BindGPUFragmentSamplers(pass, 0, &sb, 1);
            SDL_DrawGPUPrimitives(pass, 6, 1, (uint)(i * 6), 0);
        }
        SDL_EndGPURenderPass(pass);

        cp = SDL_BeginGPUCopyPass(cmd);
        var reg = new SDL_GPUTextureRegion { texture = _target, w = N, h = N, d = 1 };
        var info = new SDL_GPUTextureTransferInfo { transfer_buffer = _download };
        SDL_DownloadFromGPUTexture(cp, &reg, &info);
        SDL_EndGPUCopyPass(cp);
        var fence = SDL_SubmitGPUCommandBufferAndAcquireFence(cmd);
        SDL_WaitForGPUFences(_dev, true, &fence, 1);
        SDL_ReleaseGPUFence(_dev, fence);
        var px = new byte[N * N * 4];
        new ReadOnlySpan<byte>((void*)SDL_MapGPUTransferBuffer(_dev, _download, false), px.Length).CopyTo(px);
        SDL_UnmapGPUTransferBuffer(_dev, _download);
        SDL_ReleaseGPUTransferBuffer(_dev, tb);
        SDL_ReleaseGPUBuffer(_dev, vb);
        return px;
    }

    static SDL_GPUTexture* Tex(int w, int h, SDL_GPUTextureUsageFlags usage)
    {
        var ci = new SDL_GPUTextureCreateInfo
        {
            type = SDL_GPUTextureType.SDL_GPU_TEXTURETYPE_2D, format = SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM,
            usage = usage, width = (uint)w, height = (uint)h, layer_count_or_depth = 1, num_levels = 1,
            sample_count = SDL_GPUSampleCount.SDL_GPU_SAMPLECOUNT_1,
        };
        return SDL_CreateGPUTexture(_dev, &ci);
    }

    static void Upload(SDL_GPUTexture* tex, byte[] rgba, int w, int h)
    {
        var tci = new SDL_GPUTransferBufferCreateInfo { usage = SDL_GPUTransferBufferUsage.SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD, size = (uint)rgba.Length };
        var tb = SDL_CreateGPUTransferBuffer(_dev, &tci);
        rgba.CopyTo(new Span<byte>((void*)SDL_MapGPUTransferBuffer(_dev, tb, false), rgba.Length));
        SDL_UnmapGPUTransferBuffer(_dev, tb);
        var cmd = SDL_AcquireGPUCommandBuffer(_dev);
        var cp = SDL_BeginGPUCopyPass(cmd);
        var info = new SDL_GPUTextureTransferInfo { transfer_buffer = tb, pixels_per_row = (uint)w, rows_per_layer = (uint)h };
        var reg = new SDL_GPUTextureRegion { texture = tex, w = (uint)w, h = (uint)h, d = 1 };
        SDL_UploadToGPUTexture(cp, &info, &reg, false);
        SDL_EndGPUCopyPass(cp);
        SDL_SubmitGPUCommandBuffer(cmd);
        SDL_ReleaseGPUTransferBuffer(_dev, tb);
    }

    static SDL_GPUShader* Shader(string path, string entry, SDL_GPUShaderStage stage, uint samplers)
    {
        byte[] code = File.ReadAllBytes(path);
        byte[] ep = System.Text.Encoding.UTF8.GetBytes(entry + "\0");
        fixed (byte* c = code) fixed (byte* e = ep)
        {
            var ci = new SDL_GPUShaderCreateInfo
            {
                code_size = (nuint)code.Length, code = c, entrypoint = e,
                format = SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_SPIRV, stage = stage,
                num_samplers = samplers, num_uniform_buffers = 1,
            };
            return SDL_CreateGPUShader(_dev, &ci);
        }
    }

    static SDL_GPUBlendFactor Factor(HudBlendFactor f) => f switch
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

    static SDL_GPUGraphicsPipeline* Pipeline(SDL_GPUShader* vs, SDL_GPUShader* fs, HudBlend b)
    {
        var attrs = stackalloc SDL_GPUVertexAttribute[3];
        attrs[0] = new SDL_GPUVertexAttribute { location = 0, buffer_slot = 0, format = SDL_GPUVertexElementFormat.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT2, offset = 0 };
        attrs[1] = new SDL_GPUVertexAttribute { location = 1, buffer_slot = 0, format = SDL_GPUVertexElementFormat.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT2, offset = 8 };
        attrs[2] = new SDL_GPUVertexAttribute { location = 2, buffer_slot = 0, format = SDL_GPUVertexElementFormat.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT4, offset = 16 };
        var vbd = new SDL_GPUVertexBufferDescription { slot = 0, pitch = 32, input_rate = SDL_GPUVertexInputRate.SDL_GPU_VERTEXINPUTRATE_VERTEX };
        var (src, dst) = HudBlendStates.Factors(b);
        var blend = new SDL_GPUColorTargetBlendState
        {
            enable_blend = true,
            color_blend_op = SDL_GPUBlendOp.SDL_GPU_BLENDOP_ADD, alpha_blend_op = SDL_GPUBlendOp.SDL_GPU_BLENDOP_ADD,
            src_color_blendfactor = Factor(src), dst_color_blendfactor = Factor(dst),
            src_alpha_blendfactor = Factor(src), dst_alpha_blendfactor = Factor(dst),
        };
        var ctd = new SDL_GPUColorTargetDescription { format = SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM, blend_state = blend };
        var ci = new SDL_GPUGraphicsPipelineCreateInfo
        {
            vertex_shader = vs, fragment_shader = fs,
            vertex_input_state = new SDL_GPUVertexInputState { vertex_buffer_descriptions = &vbd, num_vertex_buffers = 1, vertex_attributes = attrs, num_vertex_attributes = 3 },
            primitive_type = SDL_GPUPrimitiveType.SDL_GPU_PRIMITIVETYPE_TRIANGLELIST,
            rasterizer_state = new SDL_GPURasterizerState { fill_mode = SDL_GPUFillMode.SDL_GPU_FILLMODE_FILL, cull_mode = SDL_GPUCullMode.SDL_GPU_CULLMODE_NONE },
            target_info = new SDL_GPUGraphicsPipelineTargetInfo { color_target_descriptions = &ctd, num_color_targets = 1 },
        };
        return SDL_CreateGPUGraphicsPipeline(_dev, &ci);
    }
}
