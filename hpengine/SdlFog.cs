// ZONE FOG ON SURFACES (C4): the fog maps are Core's (HP1.Core LevelFogMaps — which surfaces, which lights, the frame's
// camera and frustum, colour, FogMap.Build), one set per frame of the occlusion tree, one map per draw record. Here only
// the atlas: layout of the records Core builds fog maps for (shelves of 512 px, gap 2, 1 px border by edge repeat), one atlas per Core frame,
// bytes ×2 (D3DDrv RGBA7 converter, 0x100022c0), BGRA -> RGBA. Shader: light·(1 − a) + rgb (D3DDrv :6260-6300).
// Actor vertex fog is a separate pass: Core ActorFogFrame (HP1.Core/Runtime/Render, LightAndFog over FogMap.VertexFog).
using HP1.UEScript;

namespace HPEngine
{
    public sealed class FogSurf
    {
        public int Record, LightMap, UClamp, VClamp, TileX, TileY;   // Record: Core's LevelFogMaps.Records key
        public readonly List<(int first, int count)> Pieces = new();   // vertices of pieces reading this tile
    }

    public sealed class FogScene
    {
        public const int W = 512;
        public int H = 1;
        public readonly List<FogSurf> Surfaces;
        // Atlas per Core frame of the last Fill (index 0 master, i + 1 WorldFrame.Views[i]); a tile Core did not rebuild
        // this tick (the surface was not drawn by that frame) keeps its last bytes.
        public readonly List<byte[]> Atlases = new();

        // Layout: shelves by descending VClamp, start (2,2), gap 2; vertex UV3 -> atlas.
        public FogScene(WorldMesh mesh)
        {
            Surfaces = mesh.FogSurfaces;
            var order = Enumerable.Range(0, Surfaces.Count).OrderByDescending(i => Surfaces[i].VClamp).ToList();
            int x = 2, y = 2, shelf = 0;
            foreach (int i in order)
            {
                int w = Surfaces[i].UClamp, h = Surfaces[i].VClamp;
                if (x + w + 2 > W) { x = 2; y += shelf + 2; shelf = 0; }
                Surfaces[i].TileX = x; Surfaces[i].TileY = y;
                x += w + 2; if (h > shelf) shelf = h;
            }
            int used = y + shelf + 2;
            while (H < used) H <<= 1;
            foreach (FogSurf s in Surfaces)
                foreach (var (first, count) in s.Pieces)
                    for (int k = 0; k < count; k++)
                    {
                        Vertex v = mesh.Vertices[first + k];
                        v.U3 = (s.TileX + v.U3) / W; v.V3 = (s.TileY + v.V3) / H;
                        mesh.Vertices[first + k] = v;
                    }
        }

        // Core's tiles of this tick into the per-frame atlases.
        public void Fill(LevelFogMaps fm)
        {
            if (fm is null || Surfaces.Count == 0) return;
            for (int f = 0; f < fm.Tiles.Count; f++)
            {
                if (Atlases.Count <= f) Atlases.Add(new byte[W * H * 4]);
                byte[] rgba = Atlases[f];
                foreach (FogSurf s in Surfaces)
                {
                    if (!fm.Tiles[f].TryGetValue(s.Record, out byte[] src) || src is null) continue;
                    int w = s.UClamp, h = s.VClamp;
                    for (int yy = -1; yy <= h; yy++)
                    {
                        int sy = Math.Clamp(yy, 0, h - 1), ay2 = s.TileY + yy;
                        for (int xx = -1; xx <= w; xx++)
                        {
                            int sx = Math.Clamp(xx, 0, w - 1), ax2 = s.TileX + xx;
                            int o = (sy * w + sx) * 4, d = (ay2 * W + ax2) * 4;
                            rgba[d] = (byte)(src[o + 2] * 2); rgba[d + 1] = (byte)(src[o + 1] * 2);
                            rgba[d + 2] = (byte)(src[o] * 2); rgba[d + 3] = (byte)(src[o + 3] * 2);
                        }
                    }
                }
            }
        }
    }
}
