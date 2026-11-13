// Frame procedural textures (hpengine/ProcTex): fire and water are live GPU textures TextureFor returns instead of the
// static mip. Frame: SetActors/AnimateWorld (TextureFor creates and resolves) -> TickProc (mark drawn, TickAll, upload
// changed). Sampling: the world sampler (Repeat): D3DDrv sets D3DTSS_ADDRESS = WRAP once for stages 0-2
// (0x1000ae41/0x1000af56/0x1000b029/0x1000b13f) and never clamps; one mip.
// A world surface counts as drawn when its texture is in Core's WorldFrame.DrawnTextures (DrawFrame updates the texture of
// every surface it draws, Render.dll 0x10b2587d..0x10b25898); actor/mover/sprite batches are drawn only when Core lists them.
using HPEngine.ProcTex;
using SDL;
using static SDL.SDL3;

namespace HPEngine
{
    sealed unsafe partial class Renderer
    {
        public ProcTextures Proc;
        // Ice (Core IceTexture): one instance per full path (state lives in the UObject).
        // Animates only if both source and glass exist (RenderIce, Fire.dll.c: `+0xd8 && +0xdc`) and the source has the
        // same size; LocalSource is a still source; without a source, the texture's own mip from the package.
        public Dictionary<string, HP1.UEScript.UEProceduralTextureData.Ice> IceDefs;
        readonly Dictionary<nint, (HP1.UEScript.IceTexture ice, byte[] pal, byte[] rgba)> _ice = new();
        int _procFrame; double _procNow;
        readonly Dictionary<nint, (ProcTextures.Entry e, bool fire, int uploaded)> _proc = new();

        SDL_GPUTexture* LoadProc(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            // LocalSource (UIceTexture::PostLoad +0x114, check A6): stretched source or no usable glass: the original
            // shows the still source in the ice mip, blits are not executed.
            if (IceDefs != null && IceDefs.TryGetValue(path, out var idef) && idef.SourceP8 != null && (idef.GlassP8 != null || idef.LocalSource))
            {
                var ice = new HP1.UEScript.IceTexture(idef.USize, idef.VSize, idef.SourceP8, idef.GlassP8, idef.PanningStyle,
                    idef.Frequency, idef.Amplitude, idef.HorizPanSpeed, idef.VertPanSpeed, idef.UPosition, idef.VPosition,
                    idef.MasterCount, idef.MoveIce, idef.TimeMethod, idef.MinFrameRate, idef.MaxFrameRate, (byte)Math.Clamp(idef.PrimeCount, 0, 255),
                    idef.LocalSource);
                ice.Update(_procNow, _procFrame);
                var rgba = new byte[idef.USize * idef.VSize * 4];
                ice.WriteRgba(idef.SourcePalette, rgba);
                var itex = Tex(SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM, idef.USize, idef.VSize, 1, SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_SAMPLER);
                UploadLevels(itex, new[] { (idef.USize, idef.VSize, rgba) });
                _ice[(nint)itex] = (ice, idef.SourcePalette, rgba);
                return itex;
            }
            if (Proc == null) return null;
            bool fire = false;
            ProcTextures.Entry e = null;
            if (Proc.IsWetCarrier(path)) e = Proc.Wet(path, _procFrame, _procNow);
            else if (Proc.IsFire(path)) { e = Proc.Fire(path, _procFrame, _procNow); fire = true; }
            if (e == null) return null;
            var tex = Tex(SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM, e.Width, e.Height, 1, SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_SAMPLER);
            UploadLevels(tex, new[] { (e.Width, e.Height, e.Rgba) });
            _proc[(nint)tex] = (e, fire, e.Version);
            return tex;
        }

        void MarkProc(nint tex)
        {
            if (!_proc.TryGetValue(tex, out var p)) return;
            if (p.fire) Proc.MarkFireUsed(p.e.Key, _procFrame); else Proc.MarkWetUsed(p.e.Key, _procFrame);
        }

        // nowSeconds: viewport wall clock (UViewport::Lock -> appSeconds); frame: render frame number.
        public void TickProc(int frame, double nowSeconds)
        {
            _procFrame = frame; _procNow = nowSeconds;
            // Ice: Update with the same viewport wall clock and frame number; only for those drawn this frame.
            if (_ice.Count > 0)
            {
                var drawn = new HashSet<nint>();
                for (int i = 0; i < _draws.Count; i++) if (_drawnTex.Contains(_draws[i].b.Material)) drawn.Add(_animTex.Length > i && _animTex[i] != 0 ? _animTex[i] : _draws[i].tex);
                foreach (var d in _actorDraws) drawn.Add(d.tex);
                foreach (var kv in _ice)
                    if (drawn.Contains(kv.Key) && kv.Value.ice.Update(nowSeconds, frame))
                    {
                        kv.Value.ice.WriteRgba(kv.Value.pal, kv.Value.rgba);
                        UploadLevels((SDL_GPUTexture*)kv.Key, new[] { (kv.Value.ice.USize, kv.Value.ice.VSize, kv.Value.rgba) });
                    }
            }
            if (Proc == null || _proc.Count == 0) return;
            for (int i = 0; i < _draws.Count; i++) if (_drawnTex.Contains(_draws[i].b.Material)) MarkProc(_animTex.Length > i && _animTex[i] != 0 ? _animTex[i] : _draws[i].tex);
            foreach (var d in _actorDraws) MarkProc(d.tex);
            Proc.TickAll(frame, nowSeconds);
            foreach (var kv in _proc.ToArray())
            {
                var (e, fire, up) = kv.Value;
                if (e.Version == up) continue;
                UploadLevels((SDL_GPUTexture*)kv.Key, new[] { (e.Width, e.Height, e.Rgba) });
                _proc[kv.Key] = (e, fire, e.Version);
            }
        }
    }
}
