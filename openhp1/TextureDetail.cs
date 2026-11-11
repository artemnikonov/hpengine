// Frontend Texture Detail: draw path -> LOD requested from UTexture::Lock -> first mip.
// Formula and addresses live in HP1.Core UETextureLod; here only the path choice (no SDL; covered by TextureDetailTests).
using HP1.UEScript;

namespace OpenHP1
{
    // Client — LOD -1 (rule Client.TextureLODSet[LODSet]): BSP and mover surfaces, sky, decals
    // (Render 0x10b25ba4, 0x10b2643d), sprites and coronas (UCanvas::DrawTile, Engine 0x10387819);
    // MeshSkin — mesh skins (Render 0x10b0f333, 0x10b117e6); Zero — particles (Render 0x10b1514a).
    public enum TexLod { Client, MeshSkin, Zero }

    public static class TextureDetail
    {
        // First mip. Procedural and pixel-less textures give 0 (single mip; Wet/Ice request 0, Fire 0x1050dc54..).
        public static int FirstMip(UETextureSource.TextureData d, TexLod lod, int textureDetail, int skinDetail)
        {
            if (d is null || d.State != "EXPORTED") return 0;
            int req = lod switch
            {
                TexLod.Zero => 0,
                TexLod.MeshSkin => UETextureLod.MeshSkinRequest(d.LodSet, textureDetail, skinDetail),
                _ => -1,
            };
            int mip = UETextureLod.LockFirstMip(req, d.LodSet, d.MipCount, textureDetail, skinDetail);
            return UETextureLod.RenDevFirstMip(mip, d.Width, d.Height, d.MipCount);
        }

        public static TexLod OfSprite(OpenHP1.Sprites.SpriteKind kind)
            => kind == OpenHP1.Sprites.SpriteKind.Particle ? TexLod.Zero : TexLod.Client;
    }
}
