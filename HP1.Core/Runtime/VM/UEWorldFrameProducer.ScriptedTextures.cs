// SCRIPTEDTEXTURE TEXELS ARE CORE'S (CLAUDE.md §2). A ScriptedTexture's texels live here, from load to every frame:
//   load      UTexture::Serialize zeroes a bParametric texture's mips (UETextureSource state PARAMETRIC);
//   PostLoad  UScriptedTexture::PostLoad: a USize*VSize copy of SourceTexture's mip 0 (+0xcc) by the UBits/VBits shifts,
//             and Palette = SourceTexture.Palette (OldSourceTex +0xc8 is NULL at load);
//   Tick      UScriptedTexture::Tick, reached through UTexture::Update(Viewport->CurrentTime) once per viewport time
//             (UTexture::Lock / DrawFrame's Surf->Texture->Update): the copy over Mips(0), then
//             NotifyActor.RenderTexture(this) — the script draws with the natives 472-476 (ScriptedTexture.cs);
//   frame     the texels after Tick are WorldFrame.ScriptedTexels; the frontend uploads them and decides nothing.
// The schedule: DrawFrame updates the texture of every surface it draws (WorldFrame.DrawnTextures) between the occlusion
// walk and DrawWorld's RenderOverlays tail, inside the PreRender..PostRender fast-math window (pc24). A ScriptedTexture
// another draw reaches (mesh skin, sprite, particle, decal, canvas tile) is not ported: TextureUses reports it loudly.
// State lives as long as the producer: the copy and the palette never change after PostLoad, every Tick rewrites all
// USize*VSize texels from the copy, so a package reload would give the same state (the PaletteMap included).
using System;
using System.Collections.Generic;
using HP1.Core;

namespace HP1.UEScript
{
    public sealed partial class UEWorldFrameProducer
    {
        sealed class ScriptedState
        {
            public string Path;
            public UEResourceRef Ref;          // the texture as script sees it (RenderTexture's Tex, `Tex == stexX`)
            public ScriptedTexture Dest;       // USize, VSize, Palette (+0x2c), Mips(0), PaletteMap (+0xd0)
            public byte[] SourceCopy;          // +0xcc; null without a SourceTexture
            public double LastUpdate = double.NaN;   // UTexture.__LastUpdateTime
        }

        UETextureSource _scriptedSource;
        readonly Dictionary<string, ScriptedState> _scripted = new(StringComparer.OrdinalIgnoreCase);   // null: not scripted
        readonly Dictionary<string, ScriptedTexelSource> _texelSources = new(StringComparer.OrdinalIgnoreCase);
        List<ScriptedTexels> _scriptedFrame;
        public int RenderTextureEvents;   // NotifyActor.RenderTexture calls (checks: the branch executed)

        UETextureSource ScriptedSource
            => _scriptedSource ??= new UETextureSource(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_systemDir.TrimEnd('/', '\\'))));

        // A texture already known as a ScriptedTexture (its state); null — not one, or not met on a surface yet.
        ScriptedState Scripted(string path)
            => !string.IsNullOrEmpty(path) && _scripted.TryGetValue(path, out ScriptedState s) ? s : null;

        // The texture's state, made by its load and PostLoad at the first use; null — its class (the reference's, from the
        // level's import table) is not a ScriptedTexture.
        // Original: Engine.dll ?PostLoad@UScriptedTexture@@UAEXXZ [HP1 0x10417A60] [HP2 0x10426570]
        ScriptedState Scripted(string path, string cls)
        {
            if (string.IsNullOrEmpty(path) || path == "None") return null;
            if (_scripted.TryGetValue(path, out ScriptedState s)) return s;
            if (cls is null || !UEInterpreter.ClassIsA(UEClassRegistry.Shared?.Find(cls), "ScriptedTexture")) { _scripted[path] = null; return null; }
            UETextureSource.TextureData d = ScriptedSource.Get(path);
            if (d is null || !d.Scripted) { _scripted[path] = null; return null; }
            byte[] palette = (byte[])d.Palette.Clone();
            byte[] mip0 = new byte[d.Width * d.Height];   // UTexture::Serialize: zeroed (PARAMETRIC)
            s = new ScriptedState { Path = d.Path, Ref = new UEResourceRef("ScriptedTexture", d.Path.Substring(d.Path.LastIndexOf('.') + 1), d.Path) };
            if (d.SourcePath is not null)
            {
                // Lock/Unlock of the source (its mip 0 loaded), the copy (sar by the UBits/VBits differences, the hardware
                // masks the count by 31; shl by the source's UBits: 0x10426667..0x104266ad), then the palette.
                UETextureSource.TextureData src = ScriptedSource.Get(d.SourcePath);
                if (src is null || src.State != "EXPORTED" || src.Levels[0].P8 is null)
                    throw new NotSupportedException($"UScriptedTexture::PostLoad {path}: SourceTexture {d.SourcePath} is {src?.State ?? "not found"} — only a plain P8 source is ported");
                if (d.UBits < 0 || d.VBits < 0 || src.UBits < 0)
                    throw new NotSupportedException($"UScriptedTexture::PostLoad {path}: UBits/VBits untagged (this {d.UBits}/{d.VBits}, source {src.UBits}) — the shifts are unknown");
                byte[] sp = src.Levels[0].P8;
                int dx = d.UBits - src.UBits, dy = d.VBits - src.VBits, su = src.UBits;
                var copy = new byte[d.Width * d.Height];
                for (int y = 0; y < d.Height; y++)
                    for (int x = 0; x < d.Width; x++)
                        copy[d.Width * y + x] = sp[(x >> dx) + ((y >> dy) << su)];
                s.SourceCopy = copy;
                palette = (byte[])src.Palette.Clone();
            }
            s.Dest = new ScriptedTexture(d.Width, d.Height, palette, mip0);
            return _scripted[path] = s;
        }

        // Tick of every ScriptedTexture DrawFrame updated this frame, in DrawFrame's order. The class of a surface's texture is
        // its material reference's (SurfMaterialRef.ClassName), collected once per level model.
        HeadlessBspModel _surfClassesOf;
        readonly Dictionary<string, string> _surfClasses = new(StringComparer.OrdinalIgnoreCase);
        void TickScriptedTextures(UEInterpreter interp, IReadOnlyList<string> drawnTextures)
        {
            if (drawnTextures is null || _lastOcclusionWorld?.Bsp is not { } bsp) return;
            if (!ReferenceEquals(bsp, _surfClassesOf))
            {
                _surfClassesOf = bsp; _surfClasses.Clear();
                for (int i = 0; i < bsp.ParseTables.Surfs.Length; i++)
                    if (bsp.MaterialOfSurf(i) is { Path: string mp } m) _surfClasses[mp] = m.ClassName;
            }
            foreach (string path in drawnTextures)
                if (_surfClasses.TryGetValue(path, out string cls) && Scripted(path, cls) is ScriptedState s) Update(interp, s);
        }

        // UTexture::Update: an Update at the time it last ran returns at once; otherwise Tick.
        // Original: Engine.dll ?Tick@UScriptedTexture@@UAEXM@Z [HP1 0x10417C70] [HP2 0x10426780]
        void Update(UEInterpreter interp, ScriptedState s)
        {
            if (s.LastUpdate == _viewportClock) return;
            s.LastUpdate = _viewportClock;
            ScriptedTexture t = s.Dest;
            if (s.SourceCopy is not null) Buffer.BlockCopy(s.SourceCopy, 0, t.Mip0, 0, t.USize * t.VSize);
            // NotifyActor (+0xc0) is the texture object's property: the port's texture object is its proxy.
            if (UEInterpreter.ExistingTextureProxy(s.Ref)?.GetProp("NotifyActor") is UEObject notify)
            {
                RenderTextureEvents++;
                UELifecycle.CallEvent(interp, notify, "RenderTexture", s.Ref);
            }
            (_scriptedFrame ??= new List<ScriptedTexels>()).Add(new ScriptedTexels(s.Path, t.USize, t.VSize,
                (byte[])t.Palette.Clone(), (byte[])t.Mip0.Clone()));
        }

        // The frame's texels (WorldFrame.ScriptedTexels), taken once per frame.
        IReadOnlyList<ScriptedTexels> TakeScriptedFrame()
        {
            IReadOnlyList<ScriptedTexels> r = _scriptedFrame ?? (IReadOnlyList<ScriptedTexels>)Array.Empty<ScriptedTexels>();
            _scriptedFrame = null;
            return r;
        }

        // A texture use outside DrawFrame's surfaces (TextureUses): a ScriptedTexture there is not ported (no shipped use;
        // a ScriptedTexture is recognised once a surface drew it — the 8 shipped ones are only referenced by Entryhall_hub's
        // surfaces).
        void RefuseScriptedUse(string path, string use)
        {
            if (Scripted(path) is not null)
                throw new NotSupportedException($"ScriptedTexture {path} used by {use}: only DrawFrame's surface Update is ported (UEWorldFrameProducer.ScriptedTextures)");
        }

        // The natives' receiver: the texture object (its proxy) -> the state.
        ScriptedState ScriptedOf(UEObject self, string native)
            => (UEInterpreter.TexturePathOfProxy(self) is string p ? Scripted(p) : null)
               ?? throw new NotSupportedException($"ScriptedTexture.{native}: the receiver is not a loaded ScriptedTexture");

        // A source or font page texture as UTexture::Lock gives it to the natives (mip 0 and palette), one key per path
        // (the PaletteMap is keyed by the texture object).
        ScriptedTexelSource TexelSource(object texRef, string native)
        {
            string path = texRef as string ?? TexPathOf(texRef);
            if (string.IsNullOrEmpty(path)) return null;
            if (_texelSources.TryGetValue(path, out ScriptedTexelSource hit)) return hit;
            if (Scripted(path) is not null || TextureAnim.IsAnimated(path))
                throw new NotSupportedException($"ScriptedTexture.{native}: source {path} is animated or scripted — Get/Lock of it is not ported");
            UETextureSource.TextureData d = ScriptedSource.Get(path);
            if (d is null || d.State != "EXPORTED" || d.Levels[0].P8 is null)
                throw new NotSupportedException($"ScriptedTexture.{native}: source {path} is {d?.State ?? "not found"} — only a plain P8 texture is ported");
            return _texelSources[path] = new ScriptedTexelSource { Key = path, USize = d.Width, VSize = d.Height, Palette = d.Palette, Mip0 = d.Levels[0].P8 };
        }

        // ── natives 472 DrawText / 474 DrawColoredText (bodies: ScriptedTexture.DrawText; exec argument handling HP1
        //    0x10418480 / 0x10418F10; HP2 = HP1 under unicorn: tools/hp2/engine_exec_judge.py). 473 DrawTile and 475
        //    ReplaceTexture have no shipped caller and stay unregistered (UENativesCore). ──
        public void ScriptedDrawText(UEObject self, object[] a, bool colored)
        {
            ScriptedState s = ScriptedOf(self, colored ? "DrawColoredText" : "DrawText");
            object font = a.Length > 3 ? a[3] : null;
            if (font is null) { UETrace.Warn("DrawText: No font"); return; }
            if (UENativeFonts.Of(font) is not null)
                throw new NotSupportedException("ScriptedTexture text on a native font: idiv by CharactersPerPage 0 — the original faults");
            string fontPath = FontPathOf(font);
            Hp1FontMetrics m = _fonts.Get(fontPath);
            (byte, byte, byte)? color = null;
            if (colored)
            {
                var c = a.Length > 4 ? a[4] as UEObject : null;
                color = ((byte)UEValue.ToInt(c?.GetProp("R")), (byte)UEValue.ToInt(c?.GetProp("G")), (byte)UEValue.ToInt(c?.GetProp("B")));
            }
            s.Dest.DrawText(F(a[0]), F(a[1]), a[2]?.ToString() ?? "", m,
                page => TexelSource(_fonts.PageTexturePath(fontPath, page), "DrawText"), color, FastMathWindow);
        }

        static float F(object v) => UEValue.AsFloat(v);
    }
}
