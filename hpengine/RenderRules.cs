// D3DDrv/Render.dll rules the SDL renderer turns into GPU state (SdlRenderer.cs etc.). Plain C# without a device,
// checked by the render audit tests.
namespace HPEngine
{
    static class RenderRules
    {
        // D3DBLEND (d3dtypes.h DirectX 7): 1 ZERO, 2 ONE, 3 SRCCOLOR, 4 INVSRCCOLOR, 5 SRCALPHA, 6 INVSRCALPHA, 9 DESTCOLOR.
        public const int ZERO = 1, ONE = 2, SRCCOLOR = 3, INVSRCCOLOR = 4, SRCALPHA = 5, INVSRCALPHA = 6, DESTCOLOR = 9;

        // Pipeline-mode blending (SdlRenderer.cs header): D3DDrv SetBlending FUN_100092d0 (D3DDrv.dll.c:806-830):
        // PF_Translucent -> ONE/INVSRCCOLOR, PF_Modulated -> DESTCOLOR/SRCCOLOR, PF_Highlighted|PF_Translucent ->
        // SRCALPHA/INVSRCALPHA. One path for BSP (DrawComplexSurface -> SetBlending), movers and actors, so a
        // translucent world (2) and a translucent actor (4) blend the same. (0, 0) means no blending.
        public static (int src, int dst) Blend(int mode) => mode switch
        {
            2 or 4 => (ONE, INVSRCCOLOR),
            3 => (DESTCOLOR, SRCCOLOR),
            6 => (SRCALPHA, INVSRCALPHA),
            // Fog map pass of a surface (DrawComplexSurface SetBlending(0x10000000), D3DDrv.dll.c:6258-6265):
            // PF_Highlighted -> ONE/INVSRCALPHA; no 0x80000000 (the mask 0x10000044 is set), so no Z write.
            7 => (ONE, INVSRCALPHA),
            // HP2 Lumos carrier (DrawnWorld.Lumos): SetBlending's (flags & 0x800040) == 0x800040 case ahead of HP1's chain —
            // ALPHABLEND, SRCBLEND INVSRCALPHA, DESTBLEND SRCALPHA [HP2 D3DDrv 0x10009315..0x10009349]; 8 without, 9 with Z
            // write (the PolyFlags argument's bit 31, [0x10009434..0x1000944f]). Live: audit/captures_hp2_2026-11-16/lumos.
            8 or 9 => (INVSRCALPHA, SRCALPHA),
            _ => (0, 0),
        };

        // Pipeline of an HP2 Lumos carrier record (DrawnWorld.Lumos, SdlRenderer.DrawLumos) from Core's PolyFlags argument:
        // SetBlending [HP2 D3DDrv 0x10009270] takes the Lumos case only when (flags & 0x800040) == 0x800040
        // [0x10009315..0x10009349] — 8, or 9 with the z-write bit 31; any other carrier word (e.g. 0x80900000, a surface with
        // only 0x800000) falls through to HP1's chain: Translucent 2, Modulated 3, Masked 1, else opaque 0 (the world batch
        // modes, WorldMesh Blend). Live: Quidditch_Intro WoodHatch drawn opaque, 0x80900000 (hp2_campaign_2026-11-17).
        public static bool LumosCase(int pf) => (pf & 0x800040) == 0x800040;
        public static int LumosPipe(int pf) => LumosCase(pf) ? (pf < 0 ? 9 : 8)
            : (pf & 0x4) != 0 ? 2 : (pf & 0x40) != 0 ? 3 : (pf & 0x2) != 0 ? 1 : 0;

        // Frame groups in draw order: URender::DrawFrame with SpanBased = 0 (D3DDrv sets 0, D3DDrv.dll.c:279):
        // pass 1 is surfaces without (PolyFlags | Texture.PolyFlags) & 0x47 (Invisible|Masked|Translucent|Modulated),
        // pass 2 those with them (Render.dll.c:5927-5930, 5988 `(flags & 0x47 != 0) + 1`); after pass 1 actors with
        // bVar13 = 0 are drawn, after pass 2 those with bVar13 = 1. bVar13 is Core's (UESpriteSort.Pass, published as
        // ActorSnapshot.LatePass); the frontend only maps it to a group. Movers are brushes
        // in the surface passes; decals are drawn with their surface (ClipDecal inside the surface loop,
        // Render.dll.c:1491), i.e. in its pass, before the actors of that group.
        public const int OpaqueWorld = 0, Actors = 1, BlendedWorld = 2, LateActors = 3, Groups = 4;
        public const int PF_PassTwo = 0x47;
        public static int WorldGroup(int polyFlags) => (polyFlags & PF_PassTwo) != 0 ? BlendedWorld : OpaqueWorld;
        // Mirror (PF_Mirrored, ShinySurfaces = True in D3DDrv): OccludeBsp draws the reflection child frame, then
        // moves the surface to pass 0 (before pass 1) with Z write; without (flags & 0x47) it becomes
        // PF_Invisible (depth only), with them it is drawn with its texture and blending over the reflection
        // (Render.dll.c:6399, 6461-6482).
        public static int SurfaceGroup(int polyFlags, bool mirror) => mirror ? OpaqueWorld : WorldGroup(polyFlags);
        public static bool MirrorSurfaceDrawn(int polyFlags) => (polyFlags & PF_PassTwo) != 0;
        // A decal's group is its surface's: shipped draws the surface's decal list right after DrawComplexSurface, in the
        // same pass, with no map-surface test (URender::DrawFrame 0x10b26334..0x10b263a8). A map surface takes the level
        // mesh's group (mapGroups, SdlWorldMesh.SurfGroup); a mover brush tracker surface (past the map surfaces) takes it
        // from Core's flags in the record, as the mesh does (eff = Surf | Texture PolyFlags, mirror from the surface's
        // own flags; a tracker surface is never a backdrop).
        public const int PF_Mirrored = 0x08000000;
        public static int DecalGroup(int surf, int[] mapGroups, int surfPolyFlags, int surfTexturePolyFlags)
        {
            if (surf < 0) throw new System.ArgumentOutOfRangeException(nameof(surf), "decal without a surface");
            return surf < mapGroups.Length ? mapGroups[surf]
                : SurfaceGroup(surfPolyFlags | surfTexturePolyFlags, (surfPolyFlags & PF_Mirrored) != 0);
        }

        public static int ActorGroup(bool latePass) => latePass ? LateActors : Actors;
        public static bool IsActorGroup(int group) => group is Actors or LateActors;

        // World texture sampler (BSP, movers, meshes, sprites, particles, decals, sky, fire; not PF_NoSmooth, not
        // lightmap/fog) by WorldFrame.EnhancedVisuals (Original / Enhanced, [HPEngine] VisualMode, NOT ORIGINAL).
        // Original = D3DDrv: MAG/MINFILTER LINEAR (0x1000ae9b/0x1000aeaa), MIPFILTER = UseMipmapping ? (UseTrilinear ?
        // LINEAR : POINT) : NONE (0x1000aed9, per texture 0x1000a557; D3DDrv.dll.c:2661-2668) with the shipped
        // System/*/Default.ini UseTrilinear=False -> POINT; D3DTSS_MAXANISOTROPY is never set
        // -> no anisotropy. Enhanced (NOT ORIGINAL): trilinear mips (D-48) and [HPEngine] Anisotropy (D-53, 0 = off).
        public static (bool mipLinear, int anisotropy) WorldSampling(bool enhanced, int anisotropy)
            => enhanced ? (true, anisotropy) : (false, 0);

        // BSP node side (OccludeBsp, Render.dll.c:5875-5879: camera in front, OR dist >= -1.0, OR surface PF_TwoSided|PF_Portal)
        // is Core's: a node seen from its hidden side is absent from WorldFrame.Drawn. D3DDrv uses CULLMODE NONE, and so do
        // world and mover batches here.

        // Alpha test (ALPHATESTENABLE = PF_Masked bit, ALPHAREF 0x7f GREATER): D3DDrv SetBlending (D3DDrv.dll.c:787-791,
        // 873): the mask bit is cleared only under PF_Translucent, so Masked+Modulated is cut by the mask.
        public const int PF_Masked = 0x2, PF_Translucent = 0x4;
        public static bool AlphaTest(int polyFlags) => (polyFlags & PF_Masked) != 0 && (polyFlags & PF_Translucent) == 0;

        // Actor mesh fragment mode (classic.hlsl mode.x): 1 = texel only, 2 = texel × vertex light in gamma.
        // PF_Modulated: D3DDrv DrawTriangles puts diffuse 0xFFFFFFFF (D3DDrv.dll.c:1017-1020), light has no effect.
        // An Unlit mesh uses the same mode 2: vertex light = GUnlitColor (UEActorLighting.UnlitColor), D3DDrv MODULATE without x2.
        public static int ActorFrag(bool modulated) => modulated ? 1 : 2;
    }
}
