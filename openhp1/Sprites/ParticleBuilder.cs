// Engine.ParticleFX particles: rasterization of the Core frame's ParticleInstance.
//
//   - style/Opacity/RenderPrimitive from the emitter snapshot; mode is PolyFlags
//     FUN_10b15090 (Render.dll.c:24688-24697): style flags | Texture.PolyFlags (SpriteMath);
//   - RenderPrimitive outside {0,1,2,3}: the emitter is NOT drawn;
//   - vertex colour FUN_10b16ab0; under PF_Modulated D3DDrv discards it, white;
//   - billboard Width×Length in the view plane, rotation SpinPos; corners and UV per Render.dll FUN_10b184d0;
//   - PPRIM_Liquid: first the droplet FUN_10b17ce0, then the same billboard.
using System;
using System.Collections.Generic;
using HP1.UEScript;

namespace OpenHP1.Sprites
{
    public sealed class ParticleBuilder
    {
        public const int STY_Modulated = 4;
        public const int PPRIM_Liquid = 2;

        // Droplet constants from Render.dll.
        static readonly float LiquidVolumeK = BitConverter.Int32BitsToSingle(0x3F860A8B); // 0x10b38984
        const float LiquidThree = 3.0f;                                                   // 0x10b385b4
        static readonly float LiquidPi = BitConverter.Int32BitsToSingle(0x40490FF9);      // 0x10b38980
        const float LiquidHalf = 0.5f;                                                    // 0x10b385ec
        const float LiquidSide = 1.5f;                                                    // 0x10b385c0

        readonly TextureSizeFn _size;
        readonly Func<string, int> _texFlags;

        public readonly List<SpriteDraw> Draws = new();
        public readonly SpriteReport Report = new();
        public int LiquidQuads;   // droplets per frame
        // AnimNext chain frame at draw time: Textures[0]->Update(Viewport->CurrentTime), then AnimCur (FUN_10b15090,
        // Render.dll.c:9703-9713) (Engine 0x103040f7; Update is UTexture vtable 0x1046df90 +0x7c). In game:
        // UETextureAnim.CurrentFrame, the same as BSP batches (SdlRenderer.AnimateWorld); flags and size from the frame.
        // Unset: chain head.
        public Func<string, string> FrameOf = static p => p;

        // texFlags: Texture.PolyFlags (as in SpriteBuilder). Frame particles exist only for emitters drawn by the Core
        // renderer (WorldFrame.Particles); there is no second visibility gate here (block 2).
        public ParticleBuilder(TextureSizeFn size, Func<string, int> texFlags)
        {
            SpriteMath.Require(size, nameof(size)); SpriteMath.Require(texFlags, nameof(texFlags));
            _size = size; _texFlags = texFlags;
        }

        // Vertex colour: Render.dll FUN_10b16ab0: a = Opacity·alpha; when a < 1 colour ×a, Style 4 also adds
        // +(1−a)·0.5; then clamp01, alpha 1. This is the Render colour; under PF_Modulated D3DDrv DrawTriangles puts
        // white (SpriteMath.WhiteVertex), so the Style 4 grey shift never reaches the screen.
        public static (float r, float g, float b) VertexColor(UEVector color, float alpha, float opacity, int style)
        {
            float a = opacity * alpha;
            float r = color.x, g = color.y, b = color.z;
            if (a < 1f)
            {
                r *= a; g *= a; b *= a;
                if (style == STY_Modulated) { float gray = (1f - a) * 0.5f; r += gray; g += gray; b += gray; }
            }
            return (Math.Clamp(r, 0f, 1f), Math.Clamp(g, 0f, 1f), Math.Clamp(b, 0f, 1f));
        }

        public static bool PrimitivePorted(int prim) => prim == 0 || prim == 1 || prim == 2 || prim == 3;

        public void Build(WorldFrame frame)
        {
            Draws.Clear(); Report.Clear(); LiquidQuads = 0;
            if (frame == null) { Report.Add("no_frame"); return; }
            var list = frame.Particles;
            if (list == null || list.Count == 0) return;

            // Emitters in order of their first particle in the frame (emitter draw order is not an observed
            // original value here).
            var order = new List<int>();
            var seen = new HashSet<int>();
            for (int i = 0; i < list.Count; i++) if (seen.Add(list[i].EmitterId)) order.Add(list[i].EmitterId);
            var byId = new Dictionary<int, int>();
            for (int i = 0; i < frame.Actors.Count; i++) byId[frame.Actors[i].ObjectId] = i;

            foreach (int id in order)
            {
                // No emitter snapshot: not drawn.
                if (!byId.TryGetValue(id, out int ai)) { Report.Add("emitter_no_snapshot"); continue; }
                ActorSnapshot e = frame.Actors[ai];
                if (e.Destroyed) continue;
                int prim = e.Emitter.RenderPrimitive;
                if (!PrimitivePorted(prim)) { Report.Add("primitive_not_ported:" + prim); RenderUnhandled.Note("particle", "RenderPrimitive=" + prim, e.Name); continue; }
                int style = e.Style;
                if (!frame.Camera.Valid) { Report.Add("no_camera"); continue; }

                // Emitter texture: one texture per emitter, the path of the LAST particle. Core writes the emitter's
                // Textures[0] to all particles, so the paths coincide.
                string tex = null;
                for (int i = list.Count - 1; i >= 0; i--) if (list[i].EmitterId == id) { tex = list[i].TexturePath; break; }
                // Empty Textures[0] reproduces the original, not a port refusal: Render.dll FUN_10b15090
                // (0x10b150b8 reads +0x35c, 0x10b150c3 je -> epilogue 0x10b1581e) returns on NULL without
                // drawing, for any RenderPrimitive (disassembly, D-15).
                if (SpriteMath.NoTexture(tex)) { Report.Add("shipped_skip:texture_none"); continue; }
                tex = FrameOf(tex);   // flags (+0x80) and Lock come from AnimCur (Render.dll.c:9713-9738)
                if (!_size(tex, out int uSize, out int vSize)) { Report.Add("texture_unresolved:" + tex); continue; }

                int pf = SpriteMath.StyleFlags(style) | _texFlags(tex);
                RenderUnhandled.SpriteTexture("particle", _texFlags(tex), $"{e.Name} {tex}");
                bool white = SpriteMath.WhiteVertex(pf);
                // Droplet edge UV in fractions: (USize−1)/USize, (VSize−1)/VSize.
                float u1 = (uSize - 1f) / uSize, v1 = (vSize - 1f) / vSize;
                // DrawParticleSystem draws with the Frame it is given (FUN_10b16a10(..., Frame, ...)): every frame that drew
                // the emitter gets its own batch with that frame's axes (SpriteMath.ViewCamera).
                foreach (int view in SpriteMath.ViewsOf(frame, id))
                {
                    SpriteMath.ViewCamera(frame, view, out UEVector eye, out UEVector fwd, out UEVector right, out UEVector up);
                    var d = new SpriteDraw
                    {
                        Kind = SpriteKind.Particle, ActorId = id, Texture = tex, LatePass = e.LatePass,
                        Blend = SpriteMath.BlendOfFlags(pf), FrameView = view,
                    };
                    for (int i = 0; i < list.Count; i++)
                    {
                        ParticleInstance pi = list[i];
                        if (pi.EmitterId != id || (pi.View >= 0 && pi.View != view)) continue;
                        var (r, g, b) = white ? (1f, 1f, 1f) : VertexColor(pi.Color, pi.Alpha, e.Opacity, style);
                        if (prim == PPRIM_Liquid)
                            Liquid(d, in pi, eye, fwd, right, -up, u1, v1, r, g, b);
                        Billboard(d, in pi, right, up, u1, v1, r, g, b);
                    }
                    if (d.Indices.Count > 0) Draws.Add(d);
                }
            }
        }

        // Billboard: Render.dll FUN_10b184d0 (Render.dll.c:25995-26200; drawer vtable 0x10b388f8 [1],
        // chosen in FUN_10b15090 when bit 0 of AParticleFX+0x3c8 = bShellOnly is clear, disassembly 0x10b159d2):
        // a Width×Length quad in the view plane, centre Pos, rotation SpinPos. Corners (offset in view axes x right,
        // y down; R(θ) = [[c,−s],[s,c]]) and UV in texels: (−w,−h) → (0, VSize−1), (+w,−h) → (USize−1, VSize−1),
        // (+w,+h) → (USize−1, 0), (−w,+h) → (0, 0). So the quad TOP carries V = VSize−1 (the particle texture is
        // flipped vertically relative to sprite/tile), and the edge is USize−1, not USize. D3DDrv divides U/V by
        // size (DrawTriangles, D3DDrv.dll.c:1007-1008), so in fractions u1 = (USize−1)/USize, v1 =
        // (VSize−1)/VSize. Checked against the live original (hp1drawlog detail=1, Lev_Tut1 PotFire08 64×64: corners
        // (0, 0.984375) … (0.984375, 0)). Rotation: here (x, y) is right/up, rx = x·c + y·s,
        // ry = −x·s + y·c; in "down" axes this is the same R(θ). SpinRate (+0x80) == 0 (fcomp 0x10b385bc, disasm 0x10b18603):
        // no rotation at all; otherwise the GMath sine table at the angle a = ROUND(SpinPos (+0x7c) · 65536 (0x10b385f4) ·
        // 1/2π (0x10b385f0)) (fistp, render window PC24): s = SinTab[(a>>2)&0x3fff], c = SinTab[((a+0x4000)>>2)&0x3fff]
        // (0x10b1861a..0x10b1866a; BrushCoords.SinAngle/CosAngle). The bShellOnly branch
        // (FUN_10b16c70, screen quad without rotation, V not flipped) is not ported, see README.
        static void Billboard(SpriteDraw d, in ParticleInstance pi, UEVector right, UEVector up, float u1, float v1, float r, float g, float b)
        {
            // SIGNED half sizes, not clamped at 0: the original takes Width·0.5 and Length·0.5 as is (particle +0x58,
            // +0x64, Render.dll.c:26037-26038). SizeEndScale < 0 (rings Ring1..Ring5: −1) takes the size through zero, and
            // for the second half of its life the particle is drawn as a |W|×|L| quad rotated by 180° (CULLMODE NONE).
            // Live original (hp1drawlog vtx=flare4, Lev_Tut2 Ring0, 25824 quads): widths evenly spread 0..max, 0.5 % zero.
            float hw = pi.Width * 0.5f, hh = pi.Length * 0.5f;
            float cs = 1f, sn = 0f;
            if (pi.SpinRate != 0f)
            {
                int a = SpinAngle(pi.SpinPos);
                cs = BrushCoords.CosAngle(a); sn = BrushCoords.SinAngle(a);
            }
            int b0 = d.Vertices.Count;
            Corner(d, pi.Pos, right, up, -hw, hh, cs, sn, 0f, v1, r, g, b);
            Corner(d, pi.Pos, right, up, hw, hh, cs, sn, u1, v1, r, g, b);
            Corner(d, pi.Pos, right, up, hw, -hh, cs, sn, u1, 0f, r, g, b);
            Corner(d, pi.Pos, right, up, -hw, -hh, cs, sn, 0f, 0f, r, g, b);
            d.Indices.AddRange(new[] { b0, b0 + 1, b0 + 2, b0, b0 + 2, b0 + 3 });
        }

        // fld SpinPos; fmul 65536f; fmul 0.15915494f (PC24: each product rounds to float); fistp (round to nearest even;
        // outside int range the x87 integer indefinite 0x80000000).
        public static int SpinAngle(float spinPos)
        {
            float t = spinPos * 65536f * 0.15915494f;
            return t >= -2147483648f && t < 2147483648f ? (int)MathF.Round(t, MidpointRounding.ToEven) : int.MinValue;
        }

        static void Corner(SpriteDraw d, UEVector c, UEVector right, UEVector up, float x, float y,
            float cs, float sn, float u, float v, float r, float g, float b)
        {
            // Clockwise rotation by SpinPos in the (right, up) basis.
            float rx = x * cs + y * sn;
            float ry = -x * sn + y * cs;
            UEVector p = c + right * rx + up * ry;
            d.Vertices.Add(new SpriteVertex(p.x, p.y, p.z, u, v, r, g, b, 1f));
        }

        // PPRIM_Liquid: FUN_10b17ce0 (Render.dll.c:25832-25998) and FUN_10b15b50
        // (Render.dll.c:25152-25351).
        // Axes: right = GetAxes(Rotation).Y, down = −GetAxes(Rotation).Z.
        void Liquid(SpriteDraw d, in ParticleInstance pi, UEVector camLoc, UEVector fwd, UEVector right, UEVector down,
            float u1, float v1, float r, float g, float b)
        {
            UEVector p = pi.Pos;
            int b0 = d.Vertices.Count;
            if (pi.DripTimer > 0f)
            {
                // Hanging droplet: (0,0), (+W/2, 1.5·L/2), (0, L), (−W/2, 1.5·L/2).
                float hw = pi.Width * LiquidHalf, hl = pi.Length * LiquidHalf;
                if (!Finite(hw) || !Finite(hl)) return;
                PutL(d, p, right, down, 0f, 0f, 0f, v1, r, g, b);
                PutL(d, p, right, down, hw, hl * LiquidSide, u1, v1, r, g, b);
                PutL(d, p, right, down, 0f, hl + hl, u1, 0f, r, g, b);
                PutL(d, p, right, down, -hw, hl * LiquidSide, 0f, 0f, r, g, b);
            }
            else
            {
                // Flying droplet: volume W³·k, tail along velocity.
                float w = pi.Width;
                float vol = w * w * w * LiquidVolumeK;
                UEVector vel = pi.Vel;
                float s = MathF.Sqrt(MathF.Sqrt(vel.x * vel.x + vel.y * vel.y + vel.z * vel.z));
                UEVector tail = vel.normalized * (s * pi.Length);
                float tl = MathF.Sqrt(tail.x * tail.x + tail.y * tail.y + tail.z * tail.z);
                float rad = MathF.Sqrt(vol * LiquidThree / ((tl + w) * LiquidPi));
                UEVector dh = p - camLoc, dt = (p - tail) - camLoc;
                float hx = UEVector.Dot(dh, right), hy = UEVector.Dot(dh, down), hz = UEVector.Dot(dh, fwd);
                float tx = UEVector.Dot(dt, right), ty = UEVector.Dot(dt, down), tz = UEVector.Dot(dt, fwd);
                if (hz <= 0f) return;
                float dx = tx - hx, dy = ty - hy, dz = tz - hz;
                float sp = MathF.Sqrt(dx * dx + dy * dy);
                float len = rad < sp ? (sp - rad) * MathF.Sqrt(sp * sp / (dx * dx + dy * dy + dz * dz)) + rad : rad;
                float ex = tx / tz - hx / hz, ey = ty / tz - hy / hz;
                float l2 = ex * ex + ey * ey;
                float inv = l2 > 0f ? 1f / MathF.Sqrt(l2) : 0f;
                ex *= inv; ey *= inv;
                if (!Finite(rad) || !Finite(len) || !Finite(ex) || !Finite(ey)) return;
                PutL(d, p, right, down, -ey * rad, ex * rad, 0f, 0f, r, g, b);
                PutL(d, p, right, down, -ex * rad, -ey * rad, 0f, v1, r, g, b);
                PutL(d, p, right, down, ey * rad, -ex * rad, u1, v1, r, g, b);
                PutL(d, p, right, down, ex * len, ey * len, u1, 0f, r, g, b);
            }
            // Fan per FUN_10b15090: (0,1,2), (0,2,3).
            d.Indices.AddRange(new[] { b0, b0 + 1, b0 + 2, b0, b0 + 2, b0 + 3 });
            LiquidQuads++;
        }

        static void PutL(SpriteDraw d, UEVector p, UEVector right, UEVector down, float ox, float oy,
            float u, float v, float r, float g, float b)
        {
            UEVector w = p + right * ox + down * oy;
            d.Vertices.Add(new SpriteVertex(w.x, w.y, w.z, u, v, r, g, b, 1f));
        }

        static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
    }
}
