// Corona tiles of the frame. Which lamps, how bright and in what colour is decided by Core (UECoronas, the
// URender::DrawFrame corona block) and arrives as WorldFrame.Coronas, projected (UECoronas.Project); here only the
// pixel scale and the screen tile (UCanvas::DrawIcon: full texture, PolyFlags 0x104 -> Translucent, no depth test).
using System;
using System.Collections.Generic;
using HP1.UEScript;

namespace HPEngine.Sprites
{
    public sealed class CoronaBuilder
    {
        public readonly List<SpriteDraw> Draws = new();
        public readonly SpriteReport Report = new();

        // screenW — frame width in pixels; Core projected the tiles in Frame->X units (CoronaInstance.X0..Y1).
        // lerpedCamera — the master camera of a display frame between two logic ticks (FramePacing: alpha < 1; the frame
        // owner passes it, null on a tick frame): it is interpolated, while
        // Core projected the tiles at the tick camera, so a tile would slide off its lamp by the camera's motion over the
        // missing fraction of a tick and jump back on the next tick (a judder only at a draw rate above 60). Such a frame
        // projects the same lamp (Location, DrawScale — Core's) with the same Core projection at the frame's camera; a
        // tick frame keeps Core's tiles as they are.
        public void Build(WorldFrame frame, int screenW, int screenH, CameraSnapshot? lerpedCamera = null)
        {
            Draws.Clear(); Report.Clear();
            if (frame == null || !frame.Camera.Valid) return;
            UESceneNode display = null;
            bool betweenTicks = lerpedCamera.HasValue;
            CameraSnapshot fc = lerpedCamera.GetValueOrDefault();
            foreach (CoronaInstance c in frame.Coronas)
            {
                CoronaInstance t = c;
                if (betweenTicks && c.FrameX > 0)
                {
                    display ??= UESceneNode.ProjectionMaster(c.FrameX, (int)MathF.Round((float)screenH * c.FrameX / screenW),
                        fc.Location, fc.Rotation, fc.FovAngle);
                    bool on = UECoronas.Project(display, c.Location, c.DrawScale, out float px0, out float py0, out float px1, out float py1);
                    t = new CoronaInstance(c.ActorId, c.Skin, c.Location, c.DrawScale, c.R, c.G, c.B, on, px0, py0, px1, py1, c.FrameX);
                }
                if (!t.OnScreen) { Report.Add("behind"); continue; }
                float k = (float)screenW / t.FrameX;   // frame units -> pixels (a uniform scale)
                var dr = new SpriteDraw
                {
                    Kind = SpriteKind.Corona, ActorId = t.ActorId, Texture = t.Skin,
                    Blend = SpriteBlend.Translucent, ScreenSpace = true,
                };
                float x0 = t.X0 * k, y0 = t.Y0 * k, x1 = t.X1 * k, y1 = t.Y1 * k;
                dr.Vertices.Add(new SpriteVertex(x0, y0, 0f, 0f, 0f, c.R, c.G, c.B, 1f));
                dr.Vertices.Add(new SpriteVertex(x1, y0, 0f, 1f, 0f, c.R, c.G, c.B, 1f));
                dr.Vertices.Add(new SpriteVertex(x1, y1, 0f, 1f, 1f, c.R, c.G, c.B, 1f));
                dr.Vertices.Add(new SpriteVertex(x0, y1, 0f, 0f, 1f, c.R, c.G, c.B, 1f));
                dr.Indices.AddRange(new[] { 0, 1, 2, 0, 2, 3 });
                Draws.Add(dr);
            }
        }
    }
}
