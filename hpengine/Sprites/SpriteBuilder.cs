// DT_Sprite / DT_SpriteAnimOnce actor sprites: a quad in the frame's view plane.
//
// Rules:
//   - visibility: DrawType 1/7, then the shared visibility check;
//   - texture from the frame (full path); "None"/empty is not drawn;
//   - DT_SpriteAnimOnce: AnimNext chain frame by elapsed life fraction
//     (Render.dll.c:11970-12010);
//   - mode from PolyFlags = Texture.PolyFlags | style flags (SpriteMath.StyleFlags/BlendOfFlags); IceTexture —
//     Style 3/4 only, anything else is a loud refusal;
//   - quad size tex·DrawScale along the frame view axes, centred on Location;
//   - colour = clamp01(ScaleGlow); white under PF_Modulated (SpriteMath.WhiteVertex).
using System;
using System.Collections.Generic;
using HP1.UEScript;

namespace HPEngine.Sprites
{
    public sealed class SpriteBuilder
    {
        // Game EDrawType (Render.dll DrawActorSprite handles DT 1/7).
        public const int DT_Sprite = 1, DT_SpriteAnimOnce = 7;
        // EStyle.
        public const int STY_Translucent = 3, STY_Modulated = 4;

        readonly TextureSizeFn _size;
        readonly Func<string, int> _texFlags;
        readonly Func<string, bool> _isIce;
        readonly Func<string, int> _chainLength;
        readonly Func<string, int, string> _chainAt;
        readonly Func<string, float> _defaultLifeSpan;
        readonly HashSet<int> _drawn = new();

        public readonly List<SpriteDraw> Draws = new();
        public readonly SpriteReport Report = new();
        // AnimNext chain frame at draw time: UTexture::Get(Viewport->CurrentTime) = Update + AnimCur
        // (DrawActorSprite Render.dll.c:12024, Engine 0x103040f7; Update is UTexture vtable 0x1046df90 +0x7c). In game this is
        // UETextureAnim.CurrentFrame, same as for BSP batches (SdlRenderer.AnimateWorld); flags and size come from that frame.
        // Unset: the chain head.
        public Func<string, string> FrameOf = static p => p;

        // size        — USize/VSize of the drawn texture (for IceTexture its SourceTexture;
        //               the reference :141 and :169 used the source size);
        // texFlags    — Texture.PolyFlags (bInvisible/bMasked/bTransparent/bModulate/bTwoSided/bUnlit — UETextureMetrics);
        // isIce       — the texture is an IceTexture;
        // chainLength/chainAt — AnimNext chain (in game UETextureAnim.ChainLength/ChainAt);
        // defaultLifeSpan — class Default.LifeSpan (Render reads Class->Defaults+0x4c, :352-363).
        // Which sprites to draw is decided by the Core renderer (WorldFrame.SpriteActors: FDynamicSprite::Setup + occlusion).
        public SpriteBuilder(TextureSizeFn size, Func<string, int> texFlags, Func<string, bool> isIce,
            Func<string, int> chainLength, Func<string, int, string> chainAt,
            Func<string, float> defaultLifeSpan)
        {
            SpriteMath.Require(size, nameof(size)); SpriteMath.Require(texFlags, nameof(texFlags));
            SpriteMath.Require(isIce, nameof(isIce)); SpriteMath.Require(chainLength, nameof(chainLength));
            SpriteMath.Require(chainAt, nameof(chainAt)); SpriteMath.Require(defaultLifeSpan, nameof(defaultLifeSpan));
            _size = size; _texFlags = texFlags; _isIce = isIce; _chainLength = chainLength; _chainAt = chainAt;
            _defaultLifeSpan = defaultLifeSpan;
        }

        // DT_SpriteAnimOnce: n — chain length;
        // fraction = clamp(1 − LifeSpan/Default.LifeSpan, 0, 1), NaN → 0;
        // frame = clamp(ROUND(n·fraction − 0.5), 0, n−1). Rounding is to even.
        public static int AnimOnceFrame(int n, float lifeSpan, float defaultLifeSpan)
        {
            float fraction = 1f - lifeSpan / defaultLifeSpan;
            float clamped = 0f;
            if (0f <= fraction) clamped = fraction < 1f ? fraction : 1f;      // NaN → 0, like shipped fVar6
            int frame = (int)MathF.Round(n * clamped - 0.5f, MidpointRounding.ToEven);
            if (frame < 0) frame = 0; else if (frame > n - 1) frame = n - 1;
            return frame;
        }

        public static int FlagsOf(int style, int texFlags) => texFlags | SpriteMath.StyleFlags(style);

        public void Build(WorldFrame frame)
        {
            Draws.Clear(); Report.Clear();
            if (frame == null) { Report.Add("no_frame"); return; }
            _drawn.Clear();
            foreach (int id in frame.SpriteActors) _drawn.Add(id);
            for (int i = 0; i < frame.Actors.Count; i++)
            {
                ActorSnapshot a = frame.Actors[i];
                if (a.Destroyed) continue;
                if (a.DrawType != DT_Sprite && a.DrawType != DT_SpriteAnimOnce) continue;
                if (!_drawn.Contains(a.ObjectId)) continue;
                string name = a.TexturePath;
                if (SpriteMath.NoTexture(name)) continue;
                if (a.DrawType == DT_SpriteAnimOnce)
                {
                    int n = _chainLength(name);
                    name = _chainAt(name, AnimOnceFrame(n, a.LifeSpan, _defaultLifeSpan(a.Class)));
                }
                // DT_Sprite: Get(Time) walks the chain. DT_SpriteAnimOnce: the frame's AnimNext/AnimCur are zeroed during Get
                // (Render.dll.c:12002-12005), so the life-fraction frame itself is drawn.
                else name = FrameOf(name);
                bool ice = _isIce(name);
                if (!_size(name, out int uSize, out int vSize)) { Report.Add("texture_unresolved:" + name); continue; }
                int style = a.Style;
                if (ice && style != STY_Modulated && style != STY_Translucent)
                { Report.Add("ice_style_not_ported:" + style); continue; }
                // Frame view. A pure function has no previous frame, so no camera is a loud refusal,
                // not an invented orientation.
                if (!frame.Camera.Valid) { Report.Add("no_camera"); continue; }
                int pf = FlagsOf(style, _texFlags(name));
                RenderUnhandled.SpriteTexture("sprite", _texFlags(name), $"{a.Name} {name}");
                // Tile colour is ScaleGlow only: DrawActorSprite (0x10b32850..0x10b338ac) reads actor +0x17c
                // (0x10b3299c, 0x10b329c6) and never reads AmbientGlow (+0x18c), neither itself nor in FUN_10b338f0
                // (disasm); DrawIcon (Canvas vtable +0x64, 0x10b32c1c) receives (1,1,1)·ScaleGlow clamped above at 1. The
                // multiply is skipped when ScaleGlow == 1.0 OR unordered (fcomp; jne at 0x10b329c4): a NaN keeps 1. A negative
                // colour reaches the device, whose DrawTile stores a negative channel as byte 0 (0x10006176..0x10006265):
                // the lower clamp at 0 is that device step.
                float sg = float.IsNaN(a.ScaleGlow) ? 1f : a.ScaleGlow;
                float glow = SpriteMath.WhiteVertex(pf) ? 1f : Math.Clamp(sg, 0f, 1f);
                // Every frame that drew the actor draws its own tile, set up with that frame's Coords (SpriteMath.ViewCamera).
                foreach (int view in SpriteMath.ViewsOf(frame, a.ObjectId))
                {
                    SpriteMath.ViewCamera(frame, view, out UEVector eye, out UEVector fwd, out UEVector right, out UEVector up);

                    float ds = a.DrawScale;
                    float hw = uSize * ds * 0.5f, hh = vSize * ds * 0.5f;
                    // Tile depth: Z = depth − SpriteProjForward (Render.dll.c:38114-38118); screen size comes from the
                    // real Location (Persp +0xb4, :38123). The same screen rectangle at depth Z is given by the point of
                    // the camera→Location ray at depth Z with sizes ×Z/depth.
                    // Z ≤ 0 (sprite nearer than SpriteProjForward): shipped DrawTile outcome unknown, reported loudly.
                    UEVector c = a.Location;
                    float depth = UEVector.Dot(c - eye, fwd), z = depth - a.SpriteProjForward;
                    if (depth > 0f && z > 0f)
                    {
                        float k = z / depth;
                        c = eye + (c - eye) * k; hw *= k; hh *= k;
                    }
                    else if (a.SpriteProjForward != 0f) Report.Add("projforward_z_not_positive");
                    var d = new SpriteDraw
                    {
                        Kind = SpriteKind.Sprite, ActorId = a.ObjectId, Texture = name, LatePass = a.LatePass, NoSmooth = a.NoSmooth,
                        Blend = SpriteMath.BlendOfFlags(pf), FrameView = view,
                    };
                    // Corners: top-left, top-right, bottom-right, bottom-left (UV V down).
                    Put(d, c - right * hw + up * hh, 0f, 0f, glow);
                    Put(d, c + right * hw + up * hh, 1f, 0f, glow);
                    Put(d, c + right * hw - up * hh, 1f, 1f, glow);
                    Put(d, c - right * hw - up * hh, 0f, 1f, glow);
                    d.Indices.AddRange(new[] { 0, 1, 2, 0, 2, 3 });
                    Draws.Add(d);
                }
            }
        }

        static void Put(SpriteDraw d, UEVector p, float u, float v, float glow)
            => d.Vertices.Add(new SpriteVertex(p.x, p.y, p.z, u, v, glow, glow, glow, 1f));
    }
}
