using System;
using System.Collections.Generic;

namespace HP1.UEScript
{
    // MOVER LIGHTING MODE 3 (`bDynamicLightMover`) — the slot-3 mover branch of
    // FLightManager::SetupForSurf (Render.dll.c:19876-19930, disasm
    // 0x10b07a52..0x10b07b4f) and the light categoriser thunk_FUN_10b097b0
    // (:7840-7880). What is and is not here, stated explicitly:
    //
    //   * HERE: selection from the actor's LEAF record (Model->Leaves[iLeaf].iPermeating ->
    //     Model->Lights, in pool order), face plane side
    //     (:19893: 0.0 < (L−Origin)·Z, Origin/Z — face basis in world space), match of the
    //     light's bSpecialLit with the MOVER's OWN bSpecialLit (:19897, `[esi+0x1f0]`
    //     — the mover, not the surface), record category and merge ORDER: first
    //     all category-0 records (loop :20433), then 1 and 2 (:20579) — list order
    //     within each; fill without a shadow map (NoShadowMask).
    //     Selection and texels proven by executing shipped under unicorn:
    //     validate_mover_light_branch.py (what is selected),
    //     validate_mover_mode3_fill.py (which texels) and
    //     validate_lightmap_effects.py (categories 1/2: frame effects, brightness
    //     types, Level.Brightness, LightType 0 rejection; fixtures
    //     lightmap_effects_match_shipped / lightmap_globallighting_matches_shipped).
    //   * HERE: the draw record's `+0x48` list = URender::SurfLights[iSurf]
    //     (DrawWorld, Render.dll.c:686-689) — the frame's dynamic lights, built by the SetupDynamics light gate and
    //     the BSP filter FUN_10b31c70 in Core's occlusion (UEOcclusion.SetupLight/FilterLight); in the mover branch
    //     each passes the bSpecialLit check against the mover and thunk(mover, light) — category 2, the mover
    //     itself rejected (0x10b07b2c-0x10b07b58) — MoverMode3Lightmaps. With no leaf (Region.iLeaf == -1, `cmp [esi+0xa4],-1;
    //     je 0x10b07bf3`) or Leaves.Num == 0 the branch is skipped ENTIRELY — no leaf, no +0x48,
    //     no +0x40: only the ambient seed remains (as in the port).
    //   * The `+0x40` list (volumetric lights) in the mover branch only marks records (+0x50) and
    //     adds category-3 records; neither reaches the lightmap (loops :20433/:20580), flag +0x50
    //     is read only by the fog block (:20006, local_8=7), which rebuilds it (:19938).
    //     So for the mover lightmap +0x40 is a no-op; surface fog is FogMap.
    //     Light colour (brightness type function, clamp, Level.Brightness) and frame effects are computed by
    //     SurfLightmap (LightColour / FillWave / Merge) from the given FrameFx.
    public static class MoverMode3
    {
        // Categoriser tables by LightEffect (0x10b38114 / 0x10b38118, 3 dwords
        // per effect; read from the Render.dll image under unicorn).
        // [0] — spatially dynamic effect, [1] — effect
        // with a changing shadow map (TorchWaver/FireWaver/WateryShimmer).
        // Either nonzero takes a static light out of category 0.
        static readonly byte[] EffectDynamic   = { 0, 0, 0, 0, 1, 1, 1, 1, 0, 1, 1, 0, 0, 0, 0, 0, 1, 0, 1, 0 };
        static readonly byte[] EffectShadowVar = { 0, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

        public struct LightIn
        {
            public int Export;
            public SurfLightmap.Light L;
            public bool Static, NoDelete, DynamicLight, SpecialLit;
            public float Scale;      // LightState.Scale (brightness animation by type)
        }

        // Record flag +0x84 (ComputeFromActor, R:19236): 0x10b38114[effect] —
        // the filler depends on the frame. SetupForSurf uses it to pick the
        // category-1 cache: 0x15 (mask) when nonzero, 0x16 (filler output) when zero.
        public static bool EffectIsDynamic(byte effect) => effect < EffectDynamic.Length && EffectDynamic[effect] != 0;

        // Frame light snapshot -> fill input. The single mapping of
        // frame fields to light fields.
        // Period/Phase — LightPeriod/LightPhase (+0x1e7/+0x1e8), read by the
        // SlowWave/Interference fillers; Scale — the frame's type function.
        public static LightIn FromSnapshot(in ActorSnapshot a)
        {
            LightState L = a.Light;
            return new LightIn
            {
                Export = a.ExportIndex,
                L = new SurfLightmap.Light
                {
                    Name = a.Name, Pos = a.Location, DrawScale = a.DrawScale,
                    // Spot / LightSource 1 direction: AActor::GetViewRotation (LightState.ViewRotation).
                    Pitch = L.ViewRotation.Pitch, Yaw = L.ViewRotation.Yaw, Roll = L.ViewRotation.Roll,
                    Type = (byte)L.Type, Effect = (byte)L.Effect, Brightness = (byte)L.Brightness,
                    Hue = (byte)L.Hue, Saturation = (byte)L.Saturation, Radius = (byte)L.Radius,
                    RadiusInner = (byte)L.RadiusInner, Cone = (byte)L.Cone,
                    DarkLight = L.Dark, Source = L.Source, BrightnessScale = L.Scale,
                    Period = (byte)L.Period, Phase = (byte)L.Phase,
                },
                Static = L.Static, NoDelete = L.NoDelete, DynamicLight = L.DynamicLight, SpecialLit = L.SpecialLit,
                Scale = L.Scale,
            };
        }

        // Record category for a light coming from the leaf (thunk(0, light),
        // :7840-7880): −1 — rejected (LightType 0 — `test al,al; je` on +0x1e0,
        // disasm 0x10b097ca-0x10b097d2 — or LightBrightness 0), 0 — static with
        // a shadow map (absent here), 1 — static without a map, 2 — dynamic,
        // 3 — LightEffect 15. noDynamicLights — UClient.NoDynamicLights, read by shipped as
        // [[Frame]->Viewport->Outer + 0x50] (Frame = [0x10b43314], disasm 0x10b0984d..0x10b0985c and
        // 0x10b09886..0x10b09895; UBoolProperty at 0x50, UClient::StaticConstructor Engine.dll.c:121964-121970):
        // set, a would-be category-1 light becomes category 0 and a would-be category-2 light is rejected.
        public static int Categorize(in LightIn l, bool noDynamicLights)
        {
            if (l.L.Type == 0) return -1;
            if (l.L.Brightness == 0) return -1;
            int effect = l.L.Effect;
            if (effect == 0xf) return 3;
            bool staticOrNoDelete = !l.DynamicLight && (l.Static || l.NoDelete);
            if (!staticOrNoDelete) return noDynamicLights ? -1 : 2;
            bool dyn = effect < EffectDynamic.Length && EffectDynamic[effect] != 0;
            bool shadowVar = effect < EffectShadowVar.Length && EffectShadowVar[effect] != 0;
            if (l.Static && l.L.Type == 1 && !dyn && !shadowVar) return 0;
            return noDynamicLights ? 0 : 1;
        }

        // Plane side: 0.0 < (Lx−Ox)·Zx + (Lz−Oz)·Zz + (Ly−Oy)·Zy — term order
        // as in :19893 (x, z, y); shipped computes on x87 with a 64-bit
        // mantissa, here double: differs only when |dot| ~ 1e-16·|L|.
        public static bool InFront(in UEVector lightPos, in UEVector origin, in UEVector normal)
        {
            double dx = (double)lightPos.x - origin.x, dy = (double)lightPos.y - origin.y, dz = (double)lightPos.z - origin.z;
            double d = (dx * normal.x + dz * normal.z) + dy * normal.y;
            return 0.0 < d;
        }

        // Selection for ONE face: leafLights indices in merge order
        // (category 0, then 1/2). Counters: rejected by side/bSpecialLit/
        // category 3 — for the log line.
        public static void Select(IReadOnlyList<LightIn> leafLights, in UEVector origin, in UEVector normal,
                                  bool moverSpecialLit, bool noDynamicLights, List<int> order,
                                  ref int rejectedSide, ref int rejectedSpecial, ref int skippedCat3, ref int rejectedDark,
                                  List<int> cats = null)
        {
            order.Clear(); cats?.Clear();
            int n = leafLights.Count;
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < n; i++)
                {
                    LightIn l = leafLights[i];
                    if (!InFront(in l.L.Pos, in origin, in normal)) { if (pass == 0) rejectedSide++; continue; }
                    if (l.SpecialLit != moverSpecialLit) { if (pass == 0) rejectedSpecial++; continue; }
                    int cat = Categorize(in l, noDynamicLights);
                    if (cat < 0) { if (pass == 0) rejectedDark++; continue; }
                    if (cat == 3) { if (pass == 0) skippedCat3++; continue; }
                    if ((pass == 0) != (cat == 0)) continue;
                    order.Add(i); cats?.Add(cat);
                }
        }

        // Fill one face: ambient seed, then lights in `order`, no shadow map.
        // fx — frame state (SurfLightmap.FrameFx); without it frame effects
        // refuse and Level.Brightness is not applied (see
        // SurfLightmap.LightColour). Returns the number of lights that touched the lightmap.
        public static int FillSurface(UEVector[] acc, in SurfLightmap.Surface s, in SurfLightmap.Map m,
                                      UEVector seed, IReadOnlyList<LightIn> leafLights, List<int> order,
                                      SurfLightmap.FrameFx fx = null)
        {
            for (int t = 0; t < acc.Length; t++) acc[t] = seed;
            var basis = SurfLightmap.ComputeBasis(in s, in m);
            if (basis.Degenerate) return 0;
            int touched = 0;
            for (int k = 0; k < order.Count; k++)
            {
                LightIn l = leafLights[order[k]];
                SurfLightmap.Light L = l.L;
                L.BrightnessScale = l.Scale;
                if (SurfLightmap.FillLight(acc, in s, in m, in basis, in L, null, fx)) touched++;
            }
            return touched;
        }

        // World brush face in the mover's current pose — ABrush::ToWorld
        // (Engine.dll.c:60413): UnitCoords, Origin −= Location, *= PostScale
        // (+0x284), *= Rotation (+0x108), *= MainScale with SheerRate shear
        // (+0x27c, quantum 0.05/0.55/0.65) along SheerAxis (+0x280), Origin += PrePivot.
        // Body — BrushCoords.ToWorld (same as brush collision,
        // UELevelRuntime.BrushToWorldCoords); point — TransformPointBy, vector —
        // TransformVectorBy of those coords. A nonzero shear after quantisation is
        // rejected loudly by BrushCoords.MulScale (NotSupportedException): the shear
        // matrix is not ported and must not be silently skipped (§8).
        public struct Pose
        {
            public BrushCoords Coords;
        }
        public static Pose MakePose(UEVector location, UERotator rotation, UEVector prePivot,
                                    UEVector mainScale, float mainSheer, UEVector postScale, float postSheer)
            => new Pose
            {
                Coords = BrushCoords.ToWorld(location, rotation.Pitch, rotation.Yaw, rotation.Roll, prePivot,
                                             mainScale, mainSheer, postScale, postSheer),
            };
        public static UEVector WorldPoint(in Pose p, UEVector local) => p.Coords.TransformPoint(local);
        public static UEVector WorldVector(in Pose p, UEVector local) => p.Coords.TransformVector(local);
    }
}
