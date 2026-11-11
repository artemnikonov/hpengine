// Shadows and marks: decal pieces computed by Core (WorldFrame.Decals).
//
// A triangle fan per piece, points already in world space, U/V already in texture fractions; texture and style come
// from the snapshot of the decal actor ITSELF; an unresolved texture or style ≠ 4 means the shadow is not drawn
// (with a reason). Vertex colour is white: D3DDrv::DrawTriangles sets 0xFFFFFFFF under PF_Modulated.
using System;
using System.Collections.Generic;
using HP1.UEScript;

namespace OpenHP1.Sprites
{
    public sealed class DecalBuilder
    {
        public const int STY_Modulated = 4;

        readonly TextureSizeFn _size;

        public readonly List<SpriteDraw> Draws = new();
        public readonly SpriteReport Report = new();
        // AnimNext chain frame at draw time: UTexture::Get(Viewport->CurrentTime) = Update + AnimCur
        // (decals in URender::DrawFrame Render.dll.c:1468-1475, Engine 0x103040f7; Update is UTexture vtable
        // 0x1046df90 +0x7c). In game: UETextureAnim.CurrentFrame, the same as for BSP batches (SdlRenderer.AnimateWorld).
        // Unset: chain head.
        public Func<string, string> FrameOf = static p => p;

        // Which pieces to draw is decided by Core DrawFrame (DecalInstance.Pieces: the frame's drawn polygons, frame camera);
        // the original does not check the decal actor's hidden state (Render.dll.c:1455-1530), so there is no visibility gate here.
        public DecalBuilder(TextureSizeFn size)
        {
            SpriteMath.Require(size, nameof(size));
            _size = size;
        }

        public void Build(WorldFrame frame)
        {
            Draws.Clear(); Report.Clear();
            if (frame == null) { Report.Add("no_frame"); return; }
            var decals = frame.Decals;
            if (decals == null || decals.Count == 0) return;

            var order = new List<int>();
            var seen = new HashSet<int>();
            for (int i = 0; i < decals.Count; i++) if (seen.Add(decals[i].DecalId)) order.Add(decals[i].DecalId);
            var byId = new Dictionary<int, int>();
            for (int i = 0; i < frame.Actors.Count; i++) byId[frame.Actors[i].ObjectId] = i;

            foreach (int id in order)
            {
                // Without a decal-actor snapshot there is no VM to read the texture from in the frame builder,
                // so this is a loud refusal.
                if (!byId.TryGetValue(id, out int ai)) { Report.Add("no_snapshot"); continue; }
                ActorSnapshot a = frame.Actors[ai];
                if (a.Destroyed) continue;
                string tex = a.TexturePath;
                if (SpriteMath.NoTexture(tex)) { Report.Add("texture_unresolved:<none>"); continue; }
                tex = FrameOf(tex);
                if (!_size(tex, out _, out _)) { Report.Add("texture_unresolved:" + tex); continue; }
                if (a.Style != STY_Modulated) { Report.Add("style_not_ported:" + a.Style); RenderUnhandled.Note("decal", "Style=" + a.Style, $"{a.Name} ({a.Class})"); continue; }

                // Drawing per FDecal entry: a decal is drawn with ITS surface (ClipDecal in the surface loop,
                // Render.dll.c:1491); the renderer takes the frame group by Surf.
                int drawn = 0;
                for (int k = 0; k < decals.Count; k++)
                {
                    DecalInstance rec = decals[k];
                    if (rec.DecalId != id || rec.Pieces == null) continue;
                    // One SpriteDraw per piece frame: a piece is drawn only in its own view.
                    var byView = new Dictionary<int, SpriteDraw>();
                    foreach (DecalPiece pc in rec.Pieces)
                    {
                        if (pc.Points == null) continue;
                        if (!byView.TryGetValue(pc.View, out SpriteDraw d))
                            byView[pc.View] = d = new SpriteDraw { Kind = SpriteKind.Decal, ActorId = id, Texture = tex, Blend = SpriteBlend.Modulated, Surf = rec.Surf, SurfPolyFlags = rec.SurfPolyFlags, SurfTexturePolyFlags = rec.SurfTexturePolyFlags, FrameView = pc.View };
                        int b0 = d.Vertices.Count;
                        for (int i = 0; i < pc.Points.Length; i++)
                        {
                            UEVector p = pc.Points[i];
                            d.Vertices.Add(new SpriteVertex(p.x, p.y, p.z, pc.U[i], pc.V[i], 1f, 1f, 1f, 1f));
                        }
                        for (int i = 1; i + 1 < pc.Points.Length; i++) { d.Indices.Add(b0); d.Indices.Add(b0 + i); d.Indices.Add(b0 + i + 1); }
                    }
                    foreach (SpriteDraw d in byView.Values) if (d.Indices.Count > 0) { Draws.Add(d); drawn++; }
                }
                if (drawn == 0) Report.Add("no_decal_pieces");
            }
        }
    }
}
