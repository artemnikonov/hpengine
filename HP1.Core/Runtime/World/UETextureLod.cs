using System;

namespace HP1.UEScript
{
    // WHICH MIP THE ORIGINAL DRAWS A TEXTURE FROM — the "Texture Detail" menu settings
    // (author decision: "as in the original HP1 1.53").
    //
    // Core owns the formula, the frontend only asks: which mip comes first for a texture with this LODSet and mip
    // count on this draw path. The frontend has no formula of its own.
    //
    // ── SOURCES (HP1 1.53 disasm; addresses — Engine.dll / Render.dll) ──────
    //
    // 1. UClient (Engine.dll.c:121794 StaticConstructor): enum `Details` = High(0), Medium(1), Low(2), "Ultra Low"(3)
    //    (:121814-121845, string 0x104d5cf0 — with a space); UByteProperty TextureDetail @0x7c and SkinDetail @0x80,
    //    Display, config (:121917-121932). These are elements [1] and [2] of the DWORD array TextureLODSet[8]
    //    @0x78..0x97: UClient copies move it as eight DWORDs (WinDrv 0x111059fb `rep movsd` ecx=8,
    //    0x11106622..0x11106673), followed by MinDesiredFrameRate @0x98. [0] and [3..7] have no property and are
    //    written by nobody: the class default is zeroed (UENativeClassDefaults, link 3), constructors do not touch them.
    //
    // 2. UTexture::Lock (body 0x10421220, export thunk 0x1030347c),
    //    0x1042132e..0x10421371:
    //        if (LOD == -1) {
    //            LOD = __Client ? Client->TextureLODSet[Texture->LODSet] : 0;
    //            if (RenDev && LOD < RenDev->RecommendedLOD) LOD = RenDev->RecommendedLOD;
    //        }
    //        Max = Mips.Num() - 1; if (Max > 3) Max = 3;
    //        if (LOD > Max) LOD = Max;
    //    No lower clamp. Mips below LOD are cut off entirely before reaching the device (Info.NumMips = Mips.Num() - LOD,
    //    Info.Mips[i] = Mips[LOD + i]); UScale = Mips[0].USize / Mips[LOD].USize · Scale, so world mapping does not
    //    change — only resolution does.
    //    RecommendedLOD = URenderDevice+0x30 (Engine.dll.c:153264-153290: DetailTextures @0x78, Description @0x38 —
    //    0x30 lies between DecompFormat @0x2c and Viewport @0x34). It is not declared as a property and is not in the
    //    ini; in D3DDrv all 14 `[reg+0x30]` accesses are device copies (0x10012525…0x10013314) and vertex/format fields
    //    (0x100063b4, 0x10008e7e, 0x10009e35, 0x1000ad8e). So it is 0, and the recommendedLod parameter below defaults
    //    to 0.
    //
    // 3. WHO CALLS Lock AND WITH WHICH LOD (all calls go through vtable +0x58; no module calls the export directly):
    //    - Render 0x10b25ba4/0x10b25c39/0x10b25c69 (BSP surface: texture, DetailTexture, MacroTexture), 0x10b2643d
    //      (decal), 0x10b0f3d0 and 0x10b118ea (mesh environment map), 0x10b1d9d1/0x10b1db15 (precache), Engine
    //      0x10387819 (UCanvas::DrawTile: HUD, sprites, coronas) — LOD = -1, i.e. the Client.TextureLODSet[LODSet] rule;
    //    - Render 0x10b0f333 and 0x10b117e6 (mesh skins, two mesh renderers) — own LOD, see MeshSkinRequest;
    //    - Render 0x10b1514a (AParticleFX particles), Engine 0x10388784 (UCanvas::WrappedPrint, font pages), Fire
    //      0x1050dc54/0x1050dc7d and 0x1050e594/0x1050e5c2/0x1050e5eb (Lock of WetTexture/IceTexture and their
    //      source) — LOD = 0, the setting does not apply.
    //
    // 4. Render locks a surface's DetailTexture ONLY when TextureDetail == High and not a portal
    //    (0x10b25bf7..0x10b25c1a: Client+0x7c == 0), but D3DDrv::DrawComplexSurface discards it right away if the
    //    device's `DetailTextures` is off or the surface has a FogMap (D3DDrv.dll.c:5756-5759, this+0x78), and the
    //    shipped Default.ini has DetailTextures=False under [D3DDrv.D3DRenderDevice]. The port does not draw detail
    //    textures at all — NOT part of this setting.
    public static class UETextureLod
    {
        // enum Details in index order (Engine.dll.c:121814-121845).
        public static readonly string[] DetailNames = { "High", "Medium", "Low", "Ultra Low" };

        // Number of TextureLODSet elements (0x78..0x97).
        public const int LodSetCount = 8;
        public const int LodSetWorld = 1;   // TextureDetail
        public const int LodSetSkin = 2;    // SkinDetail

        // Mip limit in UTexture::Lock (0x10421361: cmp ecx, 3).
        public const int MaxLod = 3;

        // `set ini:… TextureDetail <value>` in shipped ends in UByteProperty::ImportText (Core.dll.c:65128): ReadToken
        // (FUN_1016c5e0) — inside quotes up to the quote, else a run of [A-Za-z0-9_-] starting with a letter/digit,
        // else one character; then an FName lookup (case-insensitive) among the enum names. A number is NOT accepted
        // for an enum property: the digit check looks at the pointer AFTER the token. On failure the property is
        // unchanged (false here).
        public static bool TryParseDetail(string raw, out int level)
        {
            level = 0;
            if (raw is null) return false;
            string token = ReadToken(raw);
            if (token is null) return false;
            for (int i = 0; i < DetailNames.Length; i++)
                if (string.Equals(DetailNames[i], token, StringComparison.OrdinalIgnoreCase)) { level = i; return true; }
            return false;
        }

        // Frame value from a config string. null — the key is neither in the overlay nor in the ini: the answer is the
        // class default, byte 0 = High (the UENativeClassDefaults link chain 3-5 for 0x7c also holds for 0x80: CDO is
        // zeroed, UClient::UClient writes only 0x2c/0x30/0x34, WinDrv only copies). Shipped rejects an unrecognized
        // value (ImportText -> NULL, property unchanged): previous is kept, rejected = true.
        public static int Resolve(string raw, int previous, out bool rejected)
        {
            rejected = false;
            if (raw is null) return 0;
            if (TryParseDetail(raw, out int level)) return level;
            rejected = true;
            return previous;
        }

        static string ReadToken(string s)
        {
            if (s.Length == 0) return null;
            char c = s[0];
            if (c == '"')
            {
                // Escaping `\` does not occur in enum names; the closing quote is mandatory (otherwise Logf and
                // NULL).
                int end = s.IndexOf('"', 1);
                return end < 0 ? null : s.Substring(1, end - 1);
            }
            bool Word(char ch) => (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9');
            if (!Word(c)) return c.ToString();
            int n = 0;
            while (n < s.Length && (Word(s[n]) || s[n] == '_' || s[n] == '-')) n++;
            return s.Substring(0, n);
        }

        // Client.TextureLODSet[lodSet]: [1] = TextureDetail, [2] = SkinDetail, others — 0. A LODSet outside the array
        // would read neighbouring UClient fields (MinDesiredFrameRate…); campaign data has none (0/1/2) — loud.
        public static int ClientLodSet(int lodSet, int textureDetail, int skinDetail)
        {
            if ((uint)lodSet >= LodSetCount)
                throw new ArgumentOutOfRangeException(nameof(lodSet),
                    $"LODSet {lodSet} outside TextureLODSet[{LodSetCount}]: shipped would read a UClient field past the array");
            return lodSet == LodSetWorld ? textureDetail : lodSet == LodSetSkin ? skinDetail : 0;
        }

        // UTexture::Lock: first mip. requestedLod = -1 — the client rule.
        public static int LockFirstMip(int requestedLod, int lodSet, int mipCount,
                                       int textureDetail, int skinDetail, int recommendedLod = 0)
        {
            int lod = requestedLod;
            if (lod == -1)
            {
                lod = ClientLodSet(lodSet, textureDetail, skinDetail);
                if (lod < recommendedLod) lod = recommendedLod;
            }
            int max = mipCount - 1;
            if (max > MaxLod) max = MaxLod;
            if (lod > max) lod = max;
            return lod;
        }

        // D3DDrv limit on the side of an uploaded texture. Both device formats (8888 and 1555 — the only ones in the
        // +0x278 list: added only by 0x1000ad30, calls 0x1000ce01/0x1000ce7a with push 0x100, 0x100) get
        // Max = min(caps.MaxTextureWidth/Height, 256) (0x1000ad6d..0x1000ad8e); real hardware caps exceed 256, so 256
        // applies. UD3DRenderDevice::SetTexture (D3DDrv.dll.c, loop after LAB_10009b93) skips mips AFTER Lock while
        // USize > MaxW || VSize > MaxH; out of mips — GError. Mip i of a UE1 texture has side >> i. The campaign has
        // 19 textures of 512 (sky HP_T/HP_C, portrait, windows).
        public const int RenDevMaxTextureSize = 256;

        public static int RenDevFirstMip(int lockMip, int width, int height, int mipCount)
        {
            int mip = lockMip;
            while ((width >> mip) > RenDevMaxTextureSize || (height >> mip) > RenDevMaxTextureSize)
                if (++mip >= mipCount)
                    throw new InvalidOperationException(
                        $"texture {width}x{height}: none of the {mipCount} mips fits in {RenDevMaxTextureSize} (shipped — GError)");
            return mip;
        }

        // Mesh skins (Render 0x10b0f243..0x10b0f302; the second mesh renderer 0x10b1172b..0x10b117c6 — the same):
        // before the loop
        //     base = Client->TextureLODSet[1] - 1; if (base < RenDev->RecommendedLOD) base = RecommendedLOD;
        // then for each texture (after Update and AnimCur substitution)
        //     LOD = base; if (LOD > Client->TextureLODSet[Tex->LODSet]) LOD = TextureLODSet[Tex->LODSet];
        // and Lock with this LOD (not -1). The minimum is absent from the decompile — visible only in the disasm
        // (0x10b0f2f8 cmp/jle).
        public static int MeshSkinRequest(int lodSet, int textureDetail, int skinDetail, int recommendedLod = 0)
        {
            int b = textureDetail - 1;
            if (b < recommendedLod) b = recommendedLod;
            int cap = ClientLodSet(lodSet, textureDetail, skinDetail);
            return b > cap ? cap : b;
        }
    }
}
