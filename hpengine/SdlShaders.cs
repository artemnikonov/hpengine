// Shaders: blobs from shaders/classic.hlsl (shaders/build.sh) and Hud/shaders/hud.hlsl (Hud/shaders/build.sh),
// embedded in the assembly: SPIR-V (Vulkan), DXIL (D3D12), MSL (Metal). The device picks the format (Renderer.Create).
// The fragment formula and bindings are described in classic.hlsl itself.
using System.Reflection;
using SDL;

namespace HPEngine
{
    static class Shaders
    {
        public const SDL_GPUShaderFormat Supported = SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_SPIRV
            | SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_DXIL | SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_MSL;

        // First device-supported format with a blob; 0 means none.
        public static SDL_GPUShaderFormat Pick(SDL_GPUShaderFormat device)
        {
            foreach (var f in new[] { SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_SPIRV, SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_DXIL, SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_MSL })
                if ((device & f) != 0) return f;
            return 0;
        }

        // set: "classic" (shaders/) | "hud" (Hud/shaders/); stage: "vert" | "frag".
        // For MSL: source text (SDL compiles it itself).
        public static byte[] Load(string set, string stage, SDL_GPUShaderFormat fmt)
        {
            string ext = fmt switch
            {
                SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_SPIRV => "spv",
                SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_DXIL => "dxil",
                SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_MSL => "msl",
                _ => throw new ArgumentException($"shader format {fmt} is not built"),
            };
            string name = $"HPEngine.shaders.{set}.{stage}.{ext}";
            using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"no embedded blob {name} (shaders/build.sh)");
            var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }

        public static string Name(SDL_GPUShaderFormat fmt) => fmt switch
        {
            SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_SPIRV => "SPIR-V",
            SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_DXIL => "DXIL",
            SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_MSL => "MSL",
            _ => fmt.ToString(),
        };
    }
}
