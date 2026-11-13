// FRONTEND CONTRACT: field manifest and its coverage.
//
// WHY. Without it the contract would be described only by field comments and
// checked only insofar as a field happened to land in some journal. Measured:
// the per-actor digest renders 21 of 43 ActorSnapshot fields, and the frame
// journal only COUNTERS (actors=/particles=/audio=/hud=/cam=). The whole
// presentation half — render identity, LightState, MoverState, AmbientState,
// ParticleState, HudCommand, ParticleInstance — needs its own coverage: a swap of
// two fields, a dropped field or a wrong value would otherwise be
// invisible.
//
// TWO PARTS, BOTH MANDATORY.
//
// 1. MANIFEST (Fields below) — for every field: who produces it, from which
//    shipped source, who consumes it, gameplay or presentation.
//    Checked against the types by REFLECTION as an EXACT SET in both directions: a new
//    field without an entry fails the check, and so does a removed field with an entry.
//
// 2. RENDER (Describe below) — prints the VALUE of every field with its name.
//    It also walks the same types by REFLECTION, so it cannot lag behind the
//    contract: a new field starts printing by itself. The SHA of this
//    output is pinned, and three different defects are each distinguishable:
//      swapped fields   — labels travel with values, the pair changes;
//      dropped field    — its label disappears;
//      wrong value      — the text after the label changes.
//
// BOUNDARY, STATED OUT LOUD: the manifest describes FIELDS, not the completeness of the
// contract itself. It does not claim these fields suffice to render a level —
// what the contract still lacks is listed in WorldStatic.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace HP1.UEScript
{
    // A field is part of game truth or part of presentation. The distinction is not
    // cosmetic: a gameplay field must match byte-for-byte across frontends,
    // a presentation field must only not diverge from its source.
    public enum ContractFieldKind { Gameplay, Presentation }

    // What happens to the field TODAY, not what is intended.
    //
    //   Consumed       — the field's VALUE is read by a live consumer and affects
    //                    its output. Requires a named read site.
    //   DiagnosticOnly — the field reaches the frontend but is consumed only as
    //                    diagnostics: a counter, a list length, a journal line.
    //                    The number of actors is NOT consumption of their fields.
    //   Unconsumed     — no consumer. NOT a defect by itself, but not
    //                    "the contract works" either: the field is merely published.
    public enum ContractFieldStatus { Unconsumed, DiagnosticOnly, Consumed }

    public sealed class ContractField
    {
        public readonly ContractFieldKind Kind;
        public readonly string Producer;         // who fills it
        public readonly string Source;           // shipped source of the value
        public readonly string ConsumerTarget;   // who SHOULD read it by design
        public readonly string ConsumerNow;      // who reads it NOW ("—" if nobody)
        public readonly ContractFieldStatus Status;

        // The DEFAULT is Unconsumed, deliberately: naming a single intended
        // "consumer" would make the manifest record an INTENT that reads as a fact.
        // A field counts as consumed only when the consumer is named EXPLICITLY,
        // together with the read site.
        public ContractField(ContractFieldKind kind, string producer, string source,
            string consumerTarget)
            : this(kind, producer, source, consumerTarget,
                   ContractFieldStatus.Unconsumed, "—") { }

        public ContractField(ContractFieldKind kind, string producer, string source,
            string consumerTarget, ContractFieldStatus status, string consumerNow)
        {
            Kind = kind; Producer = producer; Source = source;
            ConsumerTarget = consumerTarget; Status = status; ConsumerNow = consumerNow;
        }
    }

    public static class WorldContract
    {
        // CONTRACT VERSION. Explicit and checkable: a frontend built for a different one
        // must find out by itself, not through diverging fields. Bumped when the SHAPE
        // changes: fields added or their meaning changed.
        //   3  — type DecalInstance (decal attachments computed by Core; without the type in Types
        //        its FIELDS would not enter the live set).
        //   4  — HudCommand.Glyphs and type HudGlyph: text layout (FUN_10388530) and glyph clipping in Core.
        //   5  — HudCommand.PolyFlags: tile/glyph blending decided by Core from shipped flags (UCanvas::DrawTile).
        //   6  — ActorSnapshot.Fatness/Opacity/SpecularGlow/SpecularWidth, AudioCommand.Looping,
        //        ParticleInstance.Vel/DripTimer (PPRIM_Liquid drops).
        //   7  — WorldFrame.Reverb and type ReverbState (Galaxy UpdateReverb words).
        //   8  — LightState.Movable (source bMovable; per-actor selection, D-41).
        //   10 — WorldFrame.MeshPoses and type MeshPose (mesh vertices from Core's render GetFrame):
        //        the frontend no longer calls ApplyAnim/EvaluatePosePoints, it draws meshes only with the frame pose.
        //   11 — WorldFrame.Views and types ViewFrame/ViewSource/Affine (Core occlusion child frames, D-61)
        //        replace WorldFrame.WarpZones/WarpZoneState; ActorSnapshot.SkyZoneId removed (Core picks the sky).
        //   12 — WorldFrame.SpriteActors (sprites drawn by Core DrawFrame: sprite FDynamicSprite::Setup +
        //        occlusion); the frontend does not select sprites itself.
        //   13 — DecalPiece.View (frame of a decal piece): pieces are clipped by DrawFrame of each frame in the tree.
        //   14 — WorldFrame.AudioChannels/AudioResumeSerial and type UEGalaxyAudio.Playback: the Galaxy channel pool
        //        and music are Core (UEGalaxyAudio); the frontend plays voices from the snapshot and never writes to Core.
        //   15 — WorldFrame.ActorViews (frames that drew each actor), WorldFrame.Coronas and type CoronaInstance: the
        //        DrawFrame corona block (table, decay, candidates, line check, colour) is Core (UECoronas); the
        //        frontend only projects and draws the tiles.
        //   16 — WorldFrame.EnhancedVisuals: the Original / Enhanced switch (not original) is read by Core from
        //        [HPEngine] VisualMode each frame; the frontend picks its world sampler by it.
        //   17 — MeshPose.LodParts and type MeshLodPart: in Original visuals Core picks the LOD level of every mesh draw
        //        (URender::DrawLodMesh, UEMeshLod, D-62); the frontend draws the collapsed faces Core names.
        //   18 — batch E: MoverState.ToWorld/DynamicLightMover, ActorSnapshot.ZoneEnvironmentMap/LevelEnvironmentMap (mover
        //        polygons and the empty-slot texture from the frame, not from live Core objects); ParticleInstance.SpinRate
        //        (the billboard rotates only when SpinRate != 0, Render.dll 0x10b18603); ViewFrame.Coords (each child frame's
        //        own Coords: per-view sprites, particles, mesh specular, environment UVs, actor vertex fog).
        //   19 — ParticleState trimmed to what a consumer reads (Valid, RenderPrimitive, TexturePaths, ParticlesPerSec,
        //        Lifetime); its 21 other fields and type ColorRange removed.
        //   20 — WorldFrame.Drawn and type DrawnWorld (the BSP/mover polygons each occlusion frame drew, FSavedPoly), and
        //        WorldFrame.DrawnTextures (textures DrawFrame updated): the frontend draws world polygons only from them and
        //        advances texture animation / procedural textures only for drawn textures.
        //   21 — CoronaInstance.OnScreen/X0/Y0/X1/Y1/FrameX: the corona tile projected by Core (UECoronas.Project; the
        //        frontend only scales to pixels).
        //   22 — 14 ActorSnapshot fields no reader used removed (PrePivot, MeshAlignShiftZ, the collision cylinder/shape/
        //        blocking flags, OnlyOwnerSee/OwnerNoSee/HighDetail, VisibilityRadius/Height): Core decides them on the
        //        live actor; the frame copies were unused leftovers.
        //   23 — AudioCommand.StartVolume: an ambient restart takes its channel with the start-loop volume (Galaxy
        //        0x10608478-0x1060848c), not the doubled update volume.
        //   24 — LightState.ViewRotation: the light direction is AActor::GetViewRotation (a Pawn's ViewRotation), not
        //        Rotation (spot filler Render.dll.c:18492, LightSource 1 0x10b06e8e..0x10b06ea8; D-68).
        //   25 — MeshLodPart.Points: GetFrame (and DrawLodMesh) runs per draw — every frame that draws a mesh — and a
        //        part carries its own pose when its draws' pose differs; parts also exist in Enhanced for that case.
        //   26 — ParticleInstance.View: one entry per DrawParticleSystem call, the frame (view) that drew it (the Render.dll
        //        billboard pass FUN_10b184d0 in Core, b6ce4281).
        //   27 — fields added without a bump, recorded here: DrawnWorld.Reflagged (D-67, a translucent mirror without
        //        ShinySurfaces drawn opaque in pass 1); ViewFrame.Zone (diagnostic, DrawLog VIEW zone);
        //        WorldFrame.AudioCallStops / AudioMixDt (K2: a device call's StopSound silences the voice for the frame's own
        //        audio, f67804aa / 9fb48e2e).
        //   28 — ActorSnapshot.LatePass: the DrawFrame sprite pass (1/2) of each actor is Core's (UESpriteSort.Pass); the
        //        frontend groups by it instead of re-deciding it from Style/Opacity (D-79 (c)).
        //   29 — WorldFrame.DrawOrder: the DrawActorSprite order of each frame (sorted array, pass 1 then 2) is Core's; the
        //        frontend draws actors in it and no longer reorders them (opaque-first, per-texture batches across actors).
        //   30 — WorldFrame.CanvasScale: the interface scale is Core's (UEWorldFrameProducer.SetScreen picks it with the
        //        canvas); the frontend scales the frame's HUD by it instead of re-reading [HPEngine] UIScaleStep after the tick.
        //   31 — DecalInstance.SurfPolyFlags / SurfTexturePolyFlags: the flags of a decal's mover tracker surface, so the
        //        frontend groups it by Core's surface (RenderRules.DecalGroup, 90abdf4b).
        public const int Version = 31;

        // Contract types. The order is fixed — it is also the print order.
        public static readonly Type[] Types =
        {
            typeof(WorldStatic), typeof(WorldFrame), typeof(ActorSnapshot),
            typeof(CameraSnapshot), typeof(MusicState), typeof(AudioCommand),
            typeof(HudCommand), typeof(HudGlyph), typeof(ParticleInstance), typeof(DecalInstance),
            typeof(LightState),
            typeof(LevelAmbientState),
            typeof(ReverbState),
            typeof(ViewFrame), typeof(ViewSource), typeof(Affine), typeof(DrawnWorld),
            typeof(MeshPose), typeof(MeshLodPart),
            typeof(CoronaInstance),
            typeof(ViewFlashState),
            typeof(MoverState), typeof(AmbientState), typeof(ParticleState),
            typeof(FloatRange),
            typeof(StaticGeometry), typeof(StaticSurface), typeof(StaticNode),
            typeof(StaticLightMap),
        };

        // The live field set — from the types themselves, not from a list next to them.
        public static SortedSet<string> LiveFields()
        {
            var found = new SortedSet<string>(StringComparer.Ordinal);
            foreach (Type t in Types)
                foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                    found.Add(t.Name + "." + f.Name);
            return found;
        }

        // Fingerprint of the contract SHAPE: the set of live fields plus their manifest
        // Kind/Status/ConsumerNow. Not values — those are pinned by the SHA of the
        // `--contract` dump; this answers a different question: "against which
        // contract did acceptance run".
        //
        // The ONLY owner of the formula: Core computes it and every frontend CALLS it,
        // so every comparison uses the same formula.
        public static string Fingerprint()
        {
            var sb = new StringBuilder();
            foreach (string f in LiveFields())
            {
                sb.Append(f);
                if (Fields.TryGetValue(f, out ContractField fld))
                    sb.Append('|').Append(fld.Kind).Append('|').Append(fld.Status)
                      .Append('|').Append(fld.ConsumerNow);
                sb.Append('\n');
            }
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                var hex = new StringBuilder(h.Length * 2);
                foreach (byte b in h) hex.Append(b.ToString("x2"));
                return hex.ToString();
            }
        }

        // ── MANIFEST: one entry for EVERY contract field ────────────────────
        //
        // Kind separates game truth from presentation. It is not a label: a
        // gameplay field's value must match across frontends because the
        // simulation depends on it; a presentation field must only not
        // diverge from its source.
        //
        // Producer/Source/Consumer come from the sources ALREADY documented in
        // WorldFrame.cs, not invented. Where the source is a shipped formula,
        // it is named in full.
        //
        // The set is checked against the types by REFLECTION as an exact set in both
        // directions (checked by the contract tests).
        public static readonly IReadOnlyDictionary<string, ContractField> Fields =
            new Dictionary<string, ContractField>(StringComparer.Ordinal)
        {
            { "WorldStatic.MapPath", new ContractField(ContractFieldKind.Presentation,
                "UELevelRuntime.LoadWorld", "map name from the travel URL (ULevel::LoadMap)", "HP1LevelPresentation") },

            // ── STATIC RENDER GEOMETRY ─────────────
            //
            // Status is set by the LIVE read site, not by intent: the frontend (HPEngineProgram.LoadScene,
            // SdlWorldMesh.Build) and Core (lightmaps, fog, coplanar cut) read these fields. The values are pinned not by the contract dump SHA (there
            // WorldStatic is built from one path and Geometry = null) but by the
            // campaign oracle diff: step [bsp-oracle] compares them byte-for-byte
            // with two independent readers across all 41 maps.
            { "WorldStatic.Geometry", new ContractField(ContractFieldKind.Presentation,
                "HeadlessBspModel.BuildStaticGeometry", "level UModel (ULevel.cpp Serialize)", "hpengine HPEngineProgram.LoadScene, HPEngineProgram.AfterTick; Core UEActorLights.UpdateActorLights, MoverMode3Lightmaps.Update, UELevelRuntime.ChildFrames, UELevelRuntime.BindFrameProducerHooks, UELevelRuntime.MoverGeometry.BuildMoverGeometry, UELevelRuntime.MoverLightmaps.Update, UELevelRuntime.SurfLightmaps, UELevelRuntime.SurfLightmaps.UpdateSurfaceLightmaps",
                ContractFieldStatus.Consumed, "hpengine HPEngineProgram.LoadScene, HPEngineProgram.AfterTick; Core UEActorLights.UpdateActorLights, MoverMode3Lightmaps.Update, UELevelRuntime.ChildFrames, UELevelRuntime.BindFrameProducerHooks, UELevelRuntime.MoverGeometry.BuildMoverGeometry, UELevelRuntime.MoverLightmaps.Update, UELevelRuntime.SurfLightmaps, UELevelRuntime.SurfLightmaps.UpdateSurfaceLightmaps") },
            { "StaticGeometry.Surfaces", new ContractField(ContractFieldKind.Presentation,
                "HeadlessBspModel.BuildStaticGeometry", "UModel.Surfs (FBspSurf)", "hpengine SdlWorldMesh.Build; Core CoplanarOcclusion.Drawable, LevelSurfLightmap.ZonesOfLightMaps, LevelFogMaps, MoverMode3Lightmaps, LevelSurfLightmap.ClearTiles, LevelSurfLightmap.UpdateLights, UELevelRuntime.MoverLightmaps, UELevelRuntime.ChildFrames",
                ContractFieldStatus.Consumed, "hpengine SdlWorldMesh.Build; Core CoplanarOcclusion.Drawable, LevelSurfLightmap.ZonesOfLightMaps, LevelFogMaps, MoverMode3Lightmaps, LevelSurfLightmap.ClearTiles, LevelSurfLightmap.UpdateLights, UELevelRuntime.MoverLightmaps, UELevelRuntime.ChildFrames") },
            { "StaticGeometry.Nodes", new ContractField(ContractFieldKind.Presentation,
                "HeadlessBspModel.BuildStaticGeometry", "UModel.Nodes (FBspNode)", "hpengine SdlMovers.MirrorPlaneOf, SdlWorldMesh.Build, SdlMovers.Build; Core CoplanarOcclusion.Compute, CoplanarOcclusion.Drawable, CoplanarOcclusion.Polygon, LevelSurfLightmap.ZonesOfLightMaps, LevelFogMaps, MoverMode3Lightmaps, UELevelRuntime.MoverLightmaps",
                ContractFieldStatus.Consumed, "hpengine SdlMovers.MirrorPlaneOf, SdlWorldMesh.Build, SdlMovers.Build; Core CoplanarOcclusion.Compute, CoplanarOcclusion.Drawable, CoplanarOcclusion.Polygon, LevelSurfLightmap.ZonesOfLightMaps, LevelFogMaps, MoverMode3Lightmaps, UELevelRuntime.MoverLightmaps") },
            { "StaticGeometry.Points", new ContractField(ContractFieldKind.Presentation,
                "HeadlessBspModel.BuildStaticGeometry", "UModel.Points (FVector)", "hpengine SdlWorldMesh.Build; Core CoplanarOcclusion.Polygon, LevelFogMaps, LevelSurfLightmap.SurfaceOf",
                ContractFieldStatus.Consumed, "hpengine SdlWorldMesh.Build; Core CoplanarOcclusion.Polygon, LevelFogMaps, LevelSurfLightmap.SurfaceOf") },
            { "StaticGeometry.Vectors", new ContractField(ContractFieldKind.Presentation,
                "HeadlessBspModel.BuildStaticGeometry", "UModel.Vectors (FVector)", "hpengine SdlWorldMesh.Build; Core LevelFogMaps, LevelSurfLightmap.SurfaceOf",
                ContractFieldStatus.Consumed, "hpengine SdlWorldMesh.Build; Core LevelFogMaps, LevelSurfLightmap.SurfaceOf") },
            { "StaticGeometry.VertPoolPoints", new ContractField(ContractFieldKind.Presentation,
                "HeadlessBspModel.BuildStaticGeometry", "UModel.Verts.Vertex (FVert)", "hpengine SdlWorldMesh.Build; Core CoplanarOcclusion.Polygon",
                ContractFieldStatus.Consumed, "hpengine SdlWorldMesh.Build; Core CoplanarOcclusion.Polygon") },
            { "StaticGeometry.VertPoolSides", new ContractField(ContractFieldKind.Presentation,
                "HeadlessBspModel.BuildStaticGeometry", "UModel.Verts.Side (FVert)", "no reader (review 2026-10-30)",
                ContractFieldStatus.Unconsumed, "—") },
            { "StaticGeometry.LightMaps", new ContractField(ContractFieldKind.Presentation,
                "HeadlessBspModel.BuildStaticGeometry", "UModel.LightMap (FLightMapIndex)", "hpengine SdlWorldMesh.Key, SdlWorldMesh.Build; Core LevelSurfLightmap.ZonesOfLightMaps, LevelFogMaps, MoverMode3Lightmaps, LevelSurfLightmap.MapOf, LevelSurfLightmap.UpdateLights, UELevelRuntime.MoverGeometry.BuildMoverGeometry",
                ContractFieldStatus.Consumed, "hpengine SdlWorldMesh.Key, SdlWorldMesh.Build; Core LevelSurfLightmap.ZonesOfLightMaps, LevelFogMaps, MoverMode3Lightmaps, LevelSurfLightmap.MapOf, LevelSurfLightmap.UpdateLights, UELevelRuntime.MoverGeometry.BuildMoverGeometry") },
            { "StaticGeometry.LeafPermeating", new ContractField(ContractFieldKind.Presentation,
                "HeadlessBspModel.BuildStaticGeometry", "UModel.Leaves(i).iPermeating — start of the leaf's null-terminated light sublist; THIS is what shipped FLightManager::SetupForActor takes (Render.dll.c:21286-21300), not a radius selection", "Core UEActorLights, UECoronas.Step, MoverMode3Lightmaps.Update",
                ContractFieldStatus.Consumed, "Core UEActorLights, UECoronas.Step, MoverMode3Lightmaps.Update") },
            { "StaticGeometry.LeafVolumetric", new ContractField(ContractFieldKind.Presentation,
                "HeadlessBspModel.BuildStaticGeometry", "UModel.Leaves(i).iVolumetric — start of the leaf's null-terminated VOLUMETRIC light sublist in LightExports; read by URender::LeafVolumetricLighting (Render.dll.c:4584) for fog maps (C4)", "HP1FogMaps") },
            { "StaticGeometry.ZoneActorExports", new ContractField(ContractFieldKind.Presentation,
                "HeadlessBspModel.BuildStaticGeometry", "UModel.Zones(i).ZoneActor — raw export indices of zone actors; the frontend recognizes SkyZoneInfo zones by them", "hpengine SdlSky.PanSpeeds; Core LevelFogMaps.Zones, UEWarpView.ZoneSet, UELevelRuntime.MoverGeometry.BuildMoverGeometry",
                ContractFieldStatus.Consumed, "hpengine SdlSky.PanSpeeds; Core LevelFogMaps.Zones, UEWarpView.ZoneSet, UELevelRuntime.MoverGeometry.BuildMoverGeometry") },
            { "StaticGeometry.LightExports", new ContractField(ContractFieldKind.Presentation,
                "HeadlessBspModel.BuildStaticGeometry", "UModel.Lights — pool of light references as RAW export indices; 0 terminates a leaf sublist", "Core UEActorLights, UECoronas.Step, LevelSurfLightmap.MapOf, LevelFogMaps.FrameStaticVolumetric, MoverMode3Lightmaps.Update",
                ContractFieldStatus.Consumed, "Core UEActorLights, UECoronas.Step, LevelSurfLightmap.MapOf, LevelFogMaps.FrameStaticVolumetric, MoverMode3Lightmaps.Update") },

            { "StaticSurface.MaterialPath", new ContractField(ContractFieldKind.Presentation,
                "HeadlessBspModel.BuildStaticGeometry", "FBspSurf.Material (full Outer chain of the import)", "hpengine SdlWorldMesh.MaterialOf",
                ContractFieldStatus.Consumed, "hpengine SdlWorldMesh.MaterialOf") },
            { "StaticSurface.PolyFlags", new ContractField(ContractFieldKind.Presentation,
                "HeadlessBspModel.BuildStaticGeometry", "FBspSurf.PolyFlags (EPolyFlags)", "hpengine SdlWorldMesh.Build, SdlWorldMesh.Select; Core LevelSurfLightmap.ZonesOfLightMaps, LevelFogMaps, UELevelRuntime.ChildFrames",
                ContractFieldStatus.Consumed, "hpengine SdlWorldMesh.Build, SdlWorldMesh.Select; Core LevelSurfLightmap.ZonesOfLightMaps, LevelFogMaps, UELevelRuntime.ChildFrames") },
            { "StaticSurface.PBase", new ContractField(ContractFieldKind.Presentation,
                "HeadlessBspModel.BuildStaticGeometry", "FBspSurf.pBase (index into Points)", "hpengine SdlWorldMesh.Build; Core LevelFogMaps, MoverMode3Lightmaps, LevelSurfLightmap.SurfaceOf",
                ContractFieldStatus.Consumed, "hpengine SdlWorldMesh.Build; Core LevelFogMaps, MoverMode3Lightmaps, LevelSurfLightmap.SurfaceOf") },
            { "StaticSurface.VNormal", new ContractField(ContractFieldKind.Presentation,
                "HeadlessBspModel.BuildStaticGeometry", "FBspSurf.vNormal (index into Vectors)", "hpengine SdlWorldMesh.Build; Core LevelFogMaps, MoverMode3Lightmaps, LevelSurfLightmap.SurfaceOf",
                ContractFieldStatus.Consumed, "hpengine SdlWorldMesh.Build; Core LevelFogMaps, MoverMode3Lightmaps, LevelSurfLightmap.SurfaceOf") },
            { "StaticSurface.VTextureU", new ContractField(ContractFieldKind.Presentation,
                "HeadlessBspModel.BuildStaticGeometry", "FBspSurf.vTextureU (index into Vectors)", "hpengine SdlWorldMesh.Build; Core LevelFogMaps, MoverMode3Lightmaps, LevelSurfLightmap.SurfaceOf",
                ContractFieldStatus.Consumed, "hpengine SdlWorldMesh.Build; Core LevelFogMaps, MoverMode3Lightmaps, LevelSurfLightmap.SurfaceOf") },
            { "StaticSurface.VTextureV", new ContractField(ContractFieldKind.Presentation,
                "HeadlessBspModel.BuildStaticGeometry", "FBspSurf.vTextureV (index into Vectors)", "hpengine SdlWorldMesh.Build; Core LevelFogMaps, MoverMode3Lightmaps, LevelSurfLightmap.SurfaceOf",
                ContractFieldStatus.Consumed, "hpengine SdlWorldMesh.Build; Core LevelFogMaps, MoverMode3Lightmaps, LevelSurfLightmap.SurfaceOf") },
            { "StaticSurface.LightMap", new ContractField(ContractFieldKind.Presentation,
                "HeadlessBspModel.BuildStaticGeometry", "FBspSurf.iLightMap", "hpengine SdlWorldMesh.Build; Core LevelSurfLightmap.ZonesOfLightMaps, LevelFogMaps, MoverMode3Lightmaps, LevelSurfLightmap.ClearTiles",
                ContractFieldStatus.Consumed, "hpengine SdlWorldMesh.Build; Core LevelSurfLightmap.ZonesOfLightMaps, LevelFogMaps, MoverMode3Lightmaps, LevelSurfLightmap.ClearTiles") },
            { "StaticSurface.BrushPoly", new ContractField(ContractFieldKind.Presentation,
                "HeadlessBspModel.BuildStaticGeometry", "FBspSurf.iBrushPoly", "no reader (review 2026-10-30)",
                ContractFieldStatus.Unconsumed, "—") },
            { "StaticSurface.PanU", new ContractField(ContractFieldKind.Presentation,
                "HeadlessBspModel.BuildStaticGeometry", "FBspSurf.PanU (serialized int16)", "hpengine SdlWorldMesh.Build",
                ContractFieldStatus.Consumed, "hpengine SdlWorldMesh.Build") },
            { "StaticSurface.PanV", new ContractField(ContractFieldKind.Presentation,
                "HeadlessBspModel.BuildStaticGeometry", "FBspSurf.PanV (serialized int16)", "hpengine SdlWorldMesh.Build",
                ContractFieldStatus.Consumed, "hpengine SdlWorldMesh.Build") },
            { "StaticSurface.BrushActor", new ContractField(ContractFieldKind.Presentation,
                "HeadlessBspModel.BuildStaticGeometry", "FBspSurf.Actor (mover brush the polygon belongs to)", "no reader (review 2026-10-30)",
                ContractFieldStatus.Unconsumed, "—") },

            { "StaticNode.Surf", new ContractField(ContractFieldKind.Presentation,
                "HeadlessBspModel.BuildStaticGeometry", "FBspNode.iSurf", "hpengine SdlMovers.MirrorPlaneOf, SdlWorldMesh.Build, SdlWorldMesh.Select, SdlMovers.Build; Core CoplanarOcclusion.Compute, CoplanarOcclusion.Drawable, LevelSurfLightmap.ZonesOfLightMaps, LevelFogMaps, MoverMode3Lightmaps, UELevelRuntime.MoverLightmaps",
                ContractFieldStatus.Consumed, "hpengine SdlMovers.MirrorPlaneOf, SdlWorldMesh.Build, SdlWorldMesh.Select, SdlMovers.Build; Core CoplanarOcclusion.Compute, CoplanarOcclusion.Drawable, LevelSurfLightmap.ZonesOfLightMaps, LevelFogMaps, MoverMode3Lightmaps, UELevelRuntime.MoverLightmaps") },
            { "StaticNode.VertPool", new ContractField(ContractFieldKind.Presentation,
                "HeadlessBspModel.BuildStaticGeometry", "FBspNode.iVertPool", "hpengine SdlWorldMesh.Build; Core CoplanarOcclusion.Polygon",
                ContractFieldStatus.Consumed, "hpengine SdlWorldMesh.Build; Core CoplanarOcclusion.Polygon") },
            { "StaticNode.NumVertices", new ContractField(ContractFieldKind.Presentation,
                "HeadlessBspModel.BuildStaticGeometry", "FBspNode.NumVertices", "hpengine SdlWorldMesh.Build; Core CoplanarOcclusion.Drawable, CoplanarOcclusion.Polygon, LevelSurfLightmap.ZonesOfLightMaps, LevelFogMaps",
                ContractFieldStatus.Consumed, "hpengine SdlWorldMesh.Build; Core CoplanarOcclusion.Drawable, CoplanarOcclusion.Polygon, LevelSurfLightmap.ZonesOfLightMaps, LevelFogMaps") },
            { "StaticNode.NodeFlags", new ContractField(ContractFieldKind.Presentation,
                "HeadlessBspModel.BuildStaticGeometry", "FBspNode.NodeFlags (EBspNodeFlags)", "diagnostic: diagnostic only: runtime_poc HeadlessSmoke",
                ContractFieldStatus.DiagnosticOnly, "diagnostic only: runtime_poc HeadlessSmoke") },
            { "StaticNode.Zone0", new ContractField(ContractFieldKind.Presentation,
                "HeadlessBspModel.BuildStaticGeometry", "FBspNode.iZone[0] — zone on the node's back side", "hpengine SdlWorldMesh.Build, SdlWorldMesh.Select; Core CoplanarOcclusion.Compute",
                ContractFieldStatus.Consumed, "hpengine SdlWorldMesh.Build, SdlWorldMesh.Select; Core CoplanarOcclusion.Compute") },
            { "StaticNode.Zone1", new ContractField(ContractFieldKind.Presentation,
                "HeadlessBspModel.BuildStaticGeometry", "FBspNode.iZone[1] — zone on the node's front side; shipped takes the zone of a PF_FakeBackdrop polygon to choose the SkyZone (Render.dll.c:5994-6016)", "hpengine SdlWorldMesh.Build, SdlWorldMesh.Select; Core CoplanarOcclusion.Compute, LevelSurfLightmap.ZonesOfLightMaps, LevelFogMaps",
                ContractFieldStatus.Consumed, "hpengine SdlWorldMesh.Build, SdlWorldMesh.Select; Core CoplanarOcclusion.Compute, LevelSurfLightmap.ZonesOfLightMaps, LevelFogMaps") },
            { "StaticNode.PlaneNormal", new ContractField(ContractFieldKind.Presentation,
                "HeadlessBspModel.BuildStaticGeometry", "FBspNode.Plane (FPlane, XYZ)", "hpengine SdlWorldMesh.Select; Core CoplanarOcclusion.Compute",
                ContractFieldStatus.Consumed, "hpengine SdlWorldMesh.Select; Core CoplanarOcclusion.Compute") },
            { "StaticNode.PlaneDist", new ContractField(ContractFieldKind.Presentation,
                "HeadlessBspModel.BuildStaticGeometry", "FBspNode.Plane.W", "hpengine SdlWorldMesh.Select",
                ContractFieldStatus.Consumed, "hpengine SdlWorldMesh.Select") },
            { "StaticNode.CoplanarNext", new ContractField(ContractFieldKind.Presentation,
                "HeadlessBspModel.BuildStaticGeometry", "FBspNode.iPlane (+0x28) — OccludeBsp coplanar node chain (Render.dll.c:6735)", "Core CoplanarOcclusion.Compute",
                ContractFieldStatus.Consumed, "Core CoplanarOcclusion.Compute") },

            { "StaticLightMap.DataOffset", new ContractField(ContractFieldKind.Presentation,
                "HeadlessBspModel.BuildStaticGeometry", "FLightMapIndex.DataOffset", "Core LevelSurfLightmap.ShadowOffset, UELevelRuntime.MoverGeometry.BuildMoverGeometry",
                ContractFieldStatus.Consumed, "Core LevelSurfLightmap.ShadowOffset, UELevelRuntime.MoverGeometry.BuildMoverGeometry") },
            { "StaticLightMap.Pan", new ContractField(ContractFieldKind.Presentation,
                "HeadlessBspModel.BuildStaticGeometry", "FLightMapIndex.Pan (FVector)", "hpengine SdlWorldMesh.Build; Core LevelFogMaps, LevelSurfLightmap.MapOf, UELevelRuntime.MoverGeometry.BuildMoverGeometry",
                ContractFieldStatus.Consumed, "hpengine SdlWorldMesh.Build; Core LevelFogMaps, LevelSurfLightmap.MapOf, UELevelRuntime.MoverGeometry.BuildMoverGeometry") },
            { "StaticLightMap.UClamp", new ContractField(ContractFieldKind.Presentation,
                "HeadlessBspModel.BuildStaticGeometry", "FLightMapIndex.UClamp", "hpengine SdlWorldMesh.Key, SdlWorldMesh.Build; Core LevelFogMaps, MoverMode3Lightmaps, LevelSurfLightmap.ShadowOffset, LevelSurfLightmap.MapOf, LevelSurfLightmap.UpdateLights, UELevelRuntime.MoverGeometry.BuildMoverGeometry",
                ContractFieldStatus.Consumed, "hpengine SdlWorldMesh.Key, SdlWorldMesh.Build; Core LevelFogMaps, MoverMode3Lightmaps, LevelSurfLightmap.ShadowOffset, LevelSurfLightmap.MapOf, LevelSurfLightmap.UpdateLights, UELevelRuntime.MoverGeometry.BuildMoverGeometry") },
            { "StaticLightMap.VClamp", new ContractField(ContractFieldKind.Presentation,
                "HeadlessBspModel.BuildStaticGeometry", "FLightMapIndex.VClamp", "hpengine SdlWorldMesh.Key, SdlWorldMesh.Build; Core LevelFogMaps, MoverMode3Lightmaps, LevelSurfLightmap.ShadowOffset, LevelSurfLightmap.MapOf, LevelSurfLightmap.UpdateLights, UELevelRuntime.MoverGeometry.BuildMoverGeometry",
                ContractFieldStatus.Consumed, "hpengine SdlWorldMesh.Key, SdlWorldMesh.Build; Core LevelFogMaps, MoverMode3Lightmaps, LevelSurfLightmap.ShadowOffset, LevelSurfLightmap.MapOf, LevelSurfLightmap.UpdateLights, UELevelRuntime.MoverGeometry.BuildMoverGeometry") },
            { "StaticLightMap.UScale", new ContractField(ContractFieldKind.Presentation,
                "HeadlessBspModel.BuildStaticGeometry", "FLightMapIndex.UScale", "hpengine SdlWorldMesh.Build; Core LevelFogMaps, LevelSurfLightmap.MapOf, UELevelRuntime.MoverGeometry.BuildMoverGeometry",
                ContractFieldStatus.Consumed, "hpengine SdlWorldMesh.Build; Core LevelFogMaps, LevelSurfLightmap.MapOf, UELevelRuntime.MoverGeometry.BuildMoverGeometry") },
            { "StaticLightMap.VScale", new ContractField(ContractFieldKind.Presentation,
                "HeadlessBspModel.BuildStaticGeometry", "FLightMapIndex.VScale", "hpengine SdlWorldMesh.Build; Core LevelFogMaps, LevelSurfLightmap.MapOf, UELevelRuntime.MoverGeometry.BuildMoverGeometry",
                ContractFieldStatus.Consumed, "hpengine SdlWorldMesh.Build; Core LevelFogMaps, LevelSurfLightmap.MapOf, UELevelRuntime.MoverGeometry.BuildMoverGeometry") },
            { "StaticLightMap.LightActors", new ContractField(ContractFieldKind.Presentation,
                "HeadlessBspModel.BuildStaticGeometry", "FLightMapIndex.LightActors", "Core LevelSurfLightmap.MapOf, UELevelRuntime.MoverGeometry.BuildMoverGeometry",
                ContractFieldStatus.Consumed, "Core LevelSurfLightmap.MapOf, UELevelRuntime.MoverGeometry.BuildMoverGeometry") },
            { "WorldFrame.Tick", new ContractField(ContractFieldKind.Gameplay,
                "UEWorldFrameProducer.Build", "UGameEngine::Draw (engine frame)", "HP1FrameConsumer",
                ContractFieldStatus.DiagnosticOnly, "HP1FrameConsumer (counters/list lengths only)") },
            { "WorldFrame.Label", new ContractField(ContractFieldKind.Gameplay,
                "UEWorldFrameProducer.Build", "UGameEngine::Draw (engine frame)", "HP1FrameConsumer",
                ContractFieldStatus.DiagnosticOnly, "HP1FrameConsumer (counters/list lengths only)") },
            { "WorldFrame.Actors", new ContractField(ContractFieldKind.Gameplay,
                "UEWorldFrameProducer.Build", "UGameEngine::Draw (engine frame)", "hpengine SdlActors.Build, SpriteBuilder.Build, SdlSky.PanSpeeds, ParticleBuilder.Build, SdlMovers.MirrorPlaneOf, DecalBuilder.Build, SdlMovers.Build; Core UEActorLighting.BeginFrame, ActorFogFrame.Begin, UEActorLights.Update, MoverMode3Lightmaps.Update, LevelSurfLightmap.Update, LevelFogMaps.Zones, UEWarpView.ZoneSet, UELevelRuntime.MoverLightmaps, UELevelRuntime.MoverLightmaps.Update, UELevelRuntime.SurfLightmaps.UpdateFogMaps",
                ContractFieldStatus.Consumed, "hpengine SdlActors.Build, SpriteBuilder.Build, SdlSky.PanSpeeds, ParticleBuilder.Build, SdlMovers.MirrorPlaneOf, DecalBuilder.Build, SdlMovers.Build; Core UEActorLighting.BeginFrame, ActorFogFrame.Begin, UEActorLights.Update, MoverMode3Lightmaps.Update, LevelSurfLightmap.Update, LevelFogMaps.Zones, UEWarpView.ZoneSet, UELevelRuntime.MoverLightmaps, UELevelRuntime.MoverLightmaps.Update, UELevelRuntime.SurfLightmaps.UpdateFogMaps") },
            { "WorldFrame.Camera", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.Build", "UGameEngine::Draw (engine frame)", "HP1FrameConsumer",
                ContractFieldStatus.Consumed, "HP1CutSceneCameraBridge.LateUpdate (render camera pose)") },
            { "WorldFrame.Audio", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.Build", "UGameEngine::Draw (engine frame)", "HP1FrameConsumer",
                ContractFieldStatus.Consumed, "HP1AudioExecutor.Consume (command execution)") },
            { "WorldFrame.Music", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.Build", "UGameEngine::Draw (engine frame)", "HP1FrameConsumer",
                ContractFieldStatus.DiagnosticOnly, "HP1FrameConsumer (counters/list lengths only)") },
            { "WorldFrame.Hud", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.Build", "UGameEngine::Draw (engine frame)", "HP1FrameConsumer",
                ContractFieldStatus.Consumed, "HP1HudRender.Rasterize") },
            { "WorldFrame.Particles", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.Build", "UGameEngine::Draw (engine frame)", "HP1FrameConsumer",
                ContractFieldStatus.Consumed, "HP1ParticleBridge.PushFrameInstances (rasterization)") },
            { "WorldFrame.Decals", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.Build", "Surf->Decals of the shipped level model: FDecal records created by ADecal::execAttachDecal (Engine.dll 0x10410770, body ported over the render mirror); the frontend only draws. The frame shadow leg stays closed until the presentation is sorted out (ShadowRouteBlockers)", "hpengine DecalBuilder.Build",
                ContractFieldStatus.Consumed, "hpengine DecalBuilder.Build") },
            // ── DecalInstance: AttachDecal result computed by Core ──────
            //
            // Decal PRESENTATION (texture, style) is NOT here: it travels in
            // the snapshot of the decal actor itself (ActorSnapshot.TexturePath/Style), and
            // duplicating it here would create a second owner.
            { "DecalInstance.DecalId", new ContractField(ContractFieldKind.Presentation,
                "UELevelRuntime.SnapshotDecals (Surf->Decals of the render mirror model)", "FDecal.Actor (+0x30), ADecal::execAttachDecal (Engine.dll 0x10410770)", "hpengine DecalBuilder.Build",
                ContractFieldStatus.Consumed, "hpengine DecalBuilder.Build") },
            { "DecalInstance.OwnerId", new ContractField(ContractFieldKind.Presentation,
                "UELevelRuntime.SnapshotDecals (Surf->Decals of the render mirror model)", "Decal.Owner — shadow owner (0 for a placed decal)",
                "HP1ActorShadow") },
            { "DecalInstance.Surf", new ContractField(ContractFieldKind.Presentation,
                "UELevelRuntime.SnapshotDecals (Surf->Decals of the render mirror model)", "index of the level-model surface whose Decals holds the record", "—") },
            { "DecalInstance.SurfPolyFlags", new ContractField(ContractFieldKind.Presentation,
                "UELevelRuntime.SnapshotDecals (Surf->Decals of the render mirror model)", "Surf.PolyFlags of the record's surface; the decal list is drawn right after its surface in that pass (URender::DrawFrame 0x10b26334..0x10b263a8)", "hpengine HPEngineProgram.TrackerDecalGroup",
                ContractFieldStatus.Consumed, "hpengine HPEngineProgram.TrackerDecalGroup (frame group of a decal on a mover brush tracker surface)") },
            { "DecalInstance.SurfTexturePolyFlags", new ContractField(ContractFieldKind.Presentation,
                "UELevelRuntime.SnapshotDecals (Surf->Decals of the render mirror model)", "Surf.Texture.PolyFlags (+0x80) of the record's surface (UELevelRuntime.SurfTexturePolyFlags)", "hpengine HPEngineProgram.TrackerDecalGroup",
                ContractFieldStatus.Consumed, "hpengine HPEngineProgram.TrackerDecalGroup (frame group of a decal on a mover brush tracker surface)") },
            { "DecalInstance.Normal", new ContractField(ContractFieldKind.Presentation,
                "UELevelRuntime.SnapshotDecals (Surf->Decals of the render mirror model)", "Vectors[surf.vNormal]; URender::ClipDecal adds it as a unit vector (Render.dll.c:3012-3034)",
                "HP1ActorShadow",
                ContractFieldStatus.Consumed, "HP1ActorShadow.BuildFromCoreDecals (lift above the surface)") },
            { "DecalInstance.Base", new ContractField(ContractFieldKind.Presentation,
                "UELevelRuntime.SnapshotDecals (Surf->Decals of the render mirror model)", "Points[surf.pBase]; FDecal vertices are stored relative to it (URender::ClipDecal)", "HP1ActorShadow",
                ContractFieldStatus.Consumed, "HP1ActorShadow.BuildFromCoreDecals (vertices to world)") },
            { "DecalInstance.V0", new ContractField(ContractFieldKind.Presentation,
                "UELevelRuntime.SnapshotDecals (Surf->Decals of the render mirror model)", "FDecal.Vertices[0] relative to Base", "HP1ActorShadow",
                ContractFieldStatus.Consumed, "HP1ActorShadow.BuildFromCoreDecals (mesh vertex)") },
            { "DecalInstance.V1", new ContractField(ContractFieldKind.Presentation,
                "UELevelRuntime.SnapshotDecals (Surf->Decals of the render mirror model)", "FDecal.Vertices[1] relative to Base", "HP1ActorShadow",
                ContractFieldStatus.Consumed, "HP1ActorShadow.BuildFromCoreDecals (mesh vertex)") },
            { "DecalInstance.V2", new ContractField(ContractFieldKind.Presentation,
                "UELevelRuntime.SnapshotDecals (Surf->Decals of the render mirror model)", "FDecal.Vertices[2] relative to Base", "HP1ActorShadow",
                ContractFieldStatus.Consumed, "HP1ActorShadow.BuildFromCoreDecals (mesh vertex)") },
            { "DecalInstance.V3", new ContractField(ContractFieldKind.Presentation,
                "UELevelRuntime.SnapshotDecals (Surf->Decals of the render mirror model)", "FDecal.Vertices[3] relative to Base", "HP1ActorShadow",
                ContractFieldStatus.Consumed, "HP1ActorShadow.BuildFromCoreDecals (mesh vertex)") },
            { "DecalInstance.Pieces", new ContractField(ContractFieldKind.Presentation,
                "UELevelRuntime.DrawFrameDecals (DrawFrame) -> SnapshotDecals", "URender::DrawFrame, surface decal loop (Render.dll.c:1455-1530) + ClipDecal (Render 0x10b27060; Core UEDecalClip): pieces over the frame's drawn polygons (FSavedPoly) with this frame's camera, world points, U/V in texture fractions, piece frame (DecalPiece.View: index into Views, −1 — master)", "hpengine DecalBuilder",
                ContractFieldStatus.Consumed, "hpengine DecalBuilder (a piece is drawn only in its own view)") },
            { "DecalInstance.Nodes", new ContractField(ContractFieldKind.Presentation,
                "UELevelRuntime.SnapshotDecals (Surf->Decals of the render mirror model)", "FDecal.Nodes (+0x34, FUN_104127c0) — nodes the decal is drawn on", "—") },
            { "ActorSnapshot.ObjectId", new ContractField(ContractFieldKind.Gameplay,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "hpengine SdlActors.Build, SpriteBuilder.Build, SdlActors.Reemit, ParticleBuilder.Build, SdlMovers.MirrorPlaneOf, DecalBuilder.Build, SdlMovers.Build; Core UEActorLighting.BeginFrame, ActorFogFrame.Begin, UEActorLights.TriangleOrder, UEActorLights.Update, MoverMode3Lightmaps.Update, LevelSurfLightmap.Update, LevelFogMaps.Update, UELevelRuntime.MoverLightmaps, UELevelRuntime.MoverLightmaps.Update",
                ContractFieldStatus.Consumed, "hpengine SdlActors.Build, SpriteBuilder.Build, SdlActors.Reemit, ParticleBuilder.Build, SdlMovers.MirrorPlaneOf, DecalBuilder.Build, SdlMovers.Build; Core UEActorLighting.BeginFrame, ActorFogFrame.Begin, UEActorLights.TriangleOrder, UEActorLights.Update, MoverMode3Lightmaps.Update, LevelSurfLightmap.Update, LevelFogMaps.Update, UELevelRuntime.MoverLightmaps, UELevelRuntime.MoverLightmaps.Update") },
            { "ActorSnapshot.ExportIndex", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "actor index in the map package (0 for a spawned one); the leaf source pool stores EXACTLY export indices, and nothing else can match a frame source to a pool entry — ObjectId is runtime and does not match the export", "hpengine SdlSky.PanSpeeds; Core UEActorLighting.BeginFrame, ActorFogFrame.Begin, MoverMode3.FromSnapshot, LevelFogMaps.LightsByExport, MoverMode3Lightmaps.Update, LevelSurfLightmap.Update, LevelFogMaps.Zones, LevelFogMaps.Update, UEWarpView.ZoneSet",
                ContractFieldStatus.Consumed, "hpengine SdlSky.PanSpeeds; Core UEActorLighting.BeginFrame, ActorFogFrame.Begin, MoverMode3.FromSnapshot, LevelFogMaps.LightsByExport, MoverMode3Lightmaps.Update, LevelSurfLightmap.Update, LevelFogMaps.Zones, LevelFogMaps.Update, UEWarpView.ZoneSet") },
            { "ActorSnapshot.BaseId", new ContractField(ContractFieldKind.Gameplay,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "no reader (review 2026-10-30)",
                ContractFieldStatus.Unconsumed, "—") },
            { "ActorSnapshot.OwnerId", new ContractField(ContractFieldKind.Gameplay,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "Core UEActorLights.TriangleOrder",
                ContractFieldStatus.Consumed, "Core UEActorLights.TriangleOrder") },
            { "ActorSnapshot.Name", new ContractField(ContractFieldKind.Gameplay,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "hpengine SdlActors.Build, SpriteBuilder.Build, SdlActors.Reemit, ParticleBuilder.Build, SdlMovers.MirrorPlaneOf, DecalBuilder.Build, SdlActors.Fatten; Core UEActorLights.Update, MoverMode3.FromSnapshot",
                ContractFieldStatus.Consumed, "hpengine SdlActors.Build, SpriteBuilder.Build, SdlActors.Reemit, ParticleBuilder.Build, SdlMovers.MirrorPlaneOf, DecalBuilder.Build, SdlActors.Fatten; Core UEActorLights.Update, MoverMode3.FromSnapshot") },
            { "ActorSnapshot.Class", new ContractField(ContractFieldKind.Gameplay,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "hpengine SdlActors.Build, SpriteBuilder.Build, SdlSky.PanSpeeds, DecalBuilder.Build; Core UEWarpView.ZoneSet",
                ContractFieldStatus.Consumed, "hpengine SdlActors.Build, SpriteBuilder.Build, SdlSky.PanSpeeds, DecalBuilder.Build; Core UEWarpView.ZoneSet") },
            { "ActorSnapshot.Placed", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "presence of ExportIndex — placed actors have a .unr export, no Spawn has one", "hpengine SdlSky.PanSpeeds; Core LevelFogMaps.Zones, UEWarpView.ZoneSet",
                ContractFieldStatus.Consumed, "hpengine SdlSky.PanSpeeds; Core LevelFogMaps.Zones, UEWarpView.ZoneSet") },
            { "ActorSnapshot.Location", new ContractField(ContractFieldKind.Gameplay,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "hpengine SpriteBuilder.Build, SdlActors.Reemit; Core UEActorLighting.BeginFrame, ActorFogFrame.Begin, UEActorLights.Update, MoverMode3.FromSnapshot, LevelFogMaps.LightsByExport, MoverMode3Lightmaps.Update, LevelFogMaps.Update",
                ContractFieldStatus.Consumed, "hpengine SpriteBuilder.Build, SdlActors.Reemit; Core UEActorLighting.BeginFrame, ActorFogFrame.Begin, UEActorLights.Update, MoverMode3.FromSnapshot, LevelFogMaps.LightsByExport, MoverMode3Lightmaps.Update, LevelFogMaps.Update") },
            { "ActorSnapshot.Rotation", new ContractField(ContractFieldKind.Gameplay,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "Core MoverMode3Lightmaps.Update",
                ContractFieldStatus.Consumed, "Core MoverMode3Lightmaps.Update") },
            { "ActorSnapshot.Velocity", new ContractField(ContractFieldKind.Gameplay,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "diagnostic: diagnostic only: runtime_poc HeadlessSmoke, runtime_poc SelfTest",
                ContractFieldStatus.DiagnosticOnly, "diagnostic only: runtime_poc HeadlessSmoke, runtime_poc SelfTest") },
            { "ActorSnapshot.Acceleration", new ContractField(ContractFieldKind.Gameplay,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "diagnostic: diagnostic only: runtime_poc HeadlessSmoke",
                ContractFieldStatus.DiagnosticOnly, "diagnostic only: runtime_poc HeadlessSmoke") },
            { "ActorSnapshot.Physics", new ContractField(ContractFieldKind.Gameplay,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "diagnostic: diagnostic only: runtime_poc HeadlessSmoke, runtime_poc SelfTest",
                ContractFieldStatus.DiagnosticOnly, "diagnostic only: runtime_poc HeadlessSmoke, runtime_poc SelfTest") },
            { "ActorSnapshot.ZoneNumber", new ContractField(ContractFieldKind.Gameplay,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "hpengine SdlActors.Build, SdlActors.Reemit; Core UEActorLights.Update, UEActorLighting.UnlitFlag, MoverMode3Lightmaps.Update, UELevelRuntime.MoverLightmaps, UELevelRuntime.MoverLightmaps.Update",
                ContractFieldStatus.Consumed, "hpengine SdlActors.Build, SdlActors.Reemit; Core UEActorLights.Update, UEActorLighting.UnlitFlag, MoverMode3Lightmaps.Update, UELevelRuntime.MoverLightmaps, UELevelRuntime.MoverLightmaps.Update") },
            { "ActorSnapshot.AnimBone", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "AActor.AnimBone (+0x64): for an animation channel — root bone index of the subtree it writes (ApplyAnim, Engine.dll.c:142138-142141)", "HP1CustomSkinner",
                ContractFieldStatus.Consumed, "HP1CustomSkinner (channels layered onto the owner's pose)") },
            { "ActorSnapshot.AuxAnims", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "AActor.AuxAnims (+0x6c): the owner's animation channels in array order — the layering order in USkeletalMesh::GetFrame (Engine.dll.c:142947-142951)", "HP1CustomSkinner",
                ContractFieldStatus.Consumed, "HP1CustomSkinner (channels layered onto the owner's pose)") },
            { "ActorSnapshot.FogZone", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "AZoneInfo.bFogZone (bit 0x2 of word +0x2cc): a surface fog map is built only in such a zone (Render.dll.c:19936), a dynamic light is volumetric only from it (:11227)", "HP1FogMaps") },
            { "ActorSnapshot.TexUPanSpeed", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "AZoneInfo.TexUPanSpeed (+0x2e0): clock multiplier for PF_AutoUPan surfaces of the zone (URender::DrawFrame, Render.dll 0x10b2594e; formula in Core SurfacePan)", "HP1WorldMaterialBank, HP1MoverSurfaceState",
                ContractFieldStatus.Consumed, "HP1WorldMaterialBank (static world pan), HP1MoverSurfaceState (mover surface pan)") },
            { "ActorSnapshot.TexVPanSpeed", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "AZoneInfo.TexVPanSpeed (+0x2e4): clock multiplier for PF_AutoVPan surfaces of the zone (URender::DrawFrame, Render.dll 0x10b25a0d; formula in Core SurfacePan)", "HP1WorldMaterialBank, HP1MoverSurfaceState",
                ContractFieldStatus.Consumed, "HP1WorldMaterialBank (static world pan), HP1MoverSurfaceState (mover surface pan)") },
            { "ActorSnapshot.Leaf", new ContractField(ContractFieldKind.Gameplay,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "Core UEActorLights.Update, MoverMode3Lightmaps.Update",
                ContractFieldStatus.Consumed, "Core UEActorLights.Update, MoverMode3Lightmaps.Update") },
            { "ActorSnapshot.Base", new ContractField(ContractFieldKind.Gameplay,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "diagnostic: diagnostic only: hpengine DrawLog, runtime_poc HeadlessSmoke",
                ContractFieldStatus.DiagnosticOnly, "diagnostic only: hpengine DrawLog, runtime_poc HeadlessSmoke") },
            { "ActorSnapshot.Owner", new ContractField(ContractFieldKind.Gameplay,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "diagnostic: diagnostic only: hpengine DrawLog, runtime_poc HeadlessSmoke",
                ContractFieldStatus.DiagnosticOnly, "diagnostic only: hpengine DrawLog, runtime_poc HeadlessSmoke") },
            { "ActorSnapshot.State", new ContractField(ContractFieldKind.Gameplay,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "diagnostic: diagnostic only: runtime_poc HeadlessSmoke",
                ContractFieldStatus.DiagnosticOnly, "diagnostic only: runtime_poc HeadlessSmoke") },
            { "ActorSnapshot.Latent", new ContractField(ContractFieldKind.Gameplay,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "diagnostic: diagnostic only: runtime_poc HeadlessSmoke",
                ContractFieldStatus.DiagnosticOnly, "diagnostic only: runtime_poc HeadlessSmoke") },
            { "ActorSnapshot.MoveTimer", new ContractField(ContractFieldKind.Gameplay,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "diagnostic: diagnostic only: runtime_poc HeadlessSmoke",
                ContractFieldStatus.DiagnosticOnly, "diagnostic only: runtime_poc HeadlessSmoke") },
            { "ActorSnapshot.AnimSequence", new ContractField(ContractFieldKind.Gameplay,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "diagnostic: diagnostic only: hpengine DrawLog, runtime_poc HeadlessSmoke",
                ContractFieldStatus.DiagnosticOnly, "diagnostic only: hpengine DrawLog, runtime_poc HeadlessSmoke") },
            { "ActorSnapshot.AnimFrame", new ContractField(ContractFieldKind.Gameplay,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "diagnostic: diagnostic only: hpengine DrawLog, runtime_poc HeadlessSmoke",
                ContractFieldStatus.DiagnosticOnly, "diagnostic only: hpengine DrawLog, runtime_poc HeadlessSmoke") },
            { "ActorSnapshot.AnimRate", new ContractField(ContractFieldKind.Gameplay,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "diagnostic: diagnostic only: runtime_poc HeadlessSmoke",
                ContractFieldStatus.DiagnosticOnly, "diagnostic only: runtime_poc HeadlessSmoke") },
            { "ActorSnapshot.TweenRate", new ContractField(ContractFieldKind.Gameplay,
                "WorldFrameProjector.Project", "Actor.TweenRate (0x5c); shipped PlayAnim writes 1/tweenTime, Engine.dll.c:73800", "frontend skinner (cross-fade duration)",
                ContractFieldStatus.Consumed, "HP1CustomSkinner.ShowCoreAnimState (tweenTime = 1/TweenRate)") },
            { "ActorSnapshot.RootMotionDriven", new ContractField(ContractFieldKind.Gameplay,
                "WorldFrameProjector.Project", "same condition as the TickRootMovement call: Role=Authority && bAnimMove && Mesh (UELevelRuntime.cs:606-615)", "diagnostic: diagnostic only: runtime_poc SelfTest",
                ContractFieldStatus.DiagnosticOnly, "diagnostic only: runtime_poc SelfTest") },
            { "ActorSnapshot.TweenAlpha", new ContractField(ContractFieldKind.Gameplay,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "frontend skinner (transition fraction)") },
            { "ActorSnapshot.TimerRate", new ContractField(ContractFieldKind.Gameplay,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "diagnostic: diagnostic only: runtime_poc HeadlessSmoke",
                ContractFieldStatus.DiagnosticOnly, "diagnostic only: runtime_poc HeadlessSmoke") },
            { "ActorSnapshot.TimerCounter", new ContractField(ContractFieldKind.Gameplay,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "diagnostic: diagnostic only: runtime_poc HeadlessSmoke",
                ContractFieldStatus.DiagnosticOnly, "diagnostic only: runtime_poc HeadlessSmoke") },
            { "ActorSnapshot.LifeSpan", new ContractField(ContractFieldKind.Gameplay,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "hpengine SpriteBuilder.Build",
                ContractFieldStatus.Consumed, "hpengine SpriteBuilder.Build") },
            { "ActorSnapshot.Touching", new ContractField(ContractFieldKind.Gameplay,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "diagnostic: diagnostic only: runtime_poc HeadlessSmoke",
                ContractFieldStatus.DiagnosticOnly, "diagnostic only: runtime_poc HeadlessSmoke") },
            { "ActorSnapshot.Destroyed", new ContractField(ContractFieldKind.Gameplay,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "hpengine SdlActors.Build, SpriteBuilder.Build, ParticleBuilder.Build, DecalBuilder.Build, SdlMovers.Build; Core UEActorLighting.BeginFrame, ActorFogFrame.Begin, UEActorLights.Update, LevelSurfLightmap.Update, LevelFogMaps.Update",
                ContractFieldStatus.Consumed, "hpengine SdlActors.Build, SpriteBuilder.Build, ParticleBuilder.Build, DecalBuilder.Build, SdlMovers.Build; Core UEActorLighting.BeginFrame, ActorFogFrame.Begin, UEActorLights.Update, LevelSurfLightmap.Update, LevelFogMaps.Update") },
            { "ActorSnapshot.DrawType", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "hpengine SdlActors.Build, SpriteBuilder.Build",
                ContractFieldStatus.Consumed, "hpengine SdlActors.Build, SpriteBuilder.Build") },
            { "ActorSnapshot.MeshPath", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "hpengine SdlActors.Fatten",
                ContractFieldStatus.Consumed, "hpengine SdlActors.Fatten") },
            { "ActorSnapshot.TexturePath", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "hpengine SpriteBuilder.Build, DecalBuilder.Build, SdlActors.SlotTexture",
                ContractFieldStatus.Consumed, "hpengine SpriteBuilder.Build, DecalBuilder.Build, SdlActors.SlotTexture") },
            { "ActorSnapshot.DrawScale", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "hpengine SpriteBuilder.Build; Core MoverMode3.FromSnapshot",
                ContractFieldStatus.Consumed, "hpengine SpriteBuilder.Build; Core MoverMode3.FromSnapshot") },
            { "ActorSnapshot.Style", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "hpengine SpriteBuilder.Build, ParticleBuilder.Build, DecalBuilder.Build, SdlActors.Fatten; Core UEActorLights.TriangleOrder",
                ContractFieldStatus.Consumed, "hpengine SpriteBuilder.Build, ParticleBuilder.Build, DecalBuilder.Build, SdlActors.Fatten; Core UEActorLights.TriangleOrder") },
            { "ActorSnapshot.Hidden", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "diagnostic: diagnostic only: runtime_poc HeadlessSmoke, runtime_poc SelfTest",
                ContractFieldStatus.DiagnosticOnly, "diagnostic only: runtime_poc HeadlessSmoke, runtime_poc SelfTest") },
            { "ActorSnapshot.Unlit", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "hpengine SdlActors.Reemit, SdlActors.Fatten; Core UEActorLights.Update, UEActorLighting.Inputs, UEActorLighting.UnlitFlag",
                ContractFieldStatus.Consumed, "hpengine SdlActors.Reemit, SdlActors.Fatten; Core UEActorLights.Update, UEActorLighting.Inputs, UEActorLighting.UnlitFlag") },
            { "ActorSnapshot.MeshEnviroMap", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "hpengine SdlActors.Fatten",
                ContractFieldStatus.Consumed, "hpengine SdlActors.Fatten") },
            { "ActorSnapshot.AmbientGlow", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "Core UEActorLighting.Inputs, UEActorLighting.UnlitColor",
                ContractFieldStatus.Consumed, "Core UEActorLighting.Inputs, UEActorLighting.UnlitColor") },
            { "ActorSnapshot.ScaleGlow", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "hpengine SpriteBuilder.Build; Core UEActorLighting.Inputs, UEActorLighting.UnlitColor, UEActorLighting.LightPoints",
                ContractFieldStatus.Consumed, "hpengine SpriteBuilder.Build; Core UEActorLighting.Inputs, UEActorLighting.UnlitColor, UEActorLighting.LightPoints") },
            { "ActorSnapshot.Fatness", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "AActor.Fatness; Render.dll DrawLodMesh: vertex += normal·(Fatness/16−8) (Render.dll.c:9328)", "hpengine SdlActors.Fatten",
                ContractFieldStatus.Consumed, "hpengine SdlActors.Fatten") },
            { "ActorSnapshot.Opacity", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "AActor.Opacity; DrawLodMesh: Opacity < 1 — flags 0x10000004, vertex alpha = Opacity (Render.dll.c:14874)", "hpengine SpriteBuilder.Build, SdlActors.Reemit, ParticleBuilder.Build, SdlActors.Fatten; Core UEActorLights.TriangleOrder",
                ContractFieldStatus.Consumed, "hpengine SpriteBuilder.Build, SdlActors.Reemit, ParticleBuilder.Build, SdlActors.Fatten; Core UEActorLights.TriangleOrder") },
            { "ActorSnapshot.LatePass", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "URender::DrawFrame sprite pass 2: Style == 3 || !(Opacity >= 1.0) (0x10b2666a..0x10b26734), Core UESpriteSort.Pass", "hpengine SdlActors.MaterialDraw, SpriteBuilder.Build, ParticleBuilder.Build",
                ContractFieldStatus.Consumed, "hpengine SdlActors.MaterialDraw, SpriteBuilder.Build, ParticleBuilder.Build") },
            { "ActorSnapshot.SpecularGlow", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "AActor.SpecularGlow; K = 2·Glow/(1−C)² (Render.dll.c:21173-21178)", "Core UEActorLighting.Inputs",
                ContractFieldStatus.Consumed, "Core UEActorLighting.Inputs") },
            { "ActorSnapshot.SpecularWidth", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "AActor.SpecularWidth; C = (1−Width·1.5/255)² (Render.dll.c:21173)", "Core UEActorLighting.Inputs",
                ContractFieldStatus.Consumed, "Core UEActorLighting.Inputs") },
            { "ActorSnapshot.SpriteProjForward", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "AActor.SpriteProjForward; sprite tile depth Z = depth − SpriteProjForward (Render.dll FUN_10b30c70, DrawActorSprite 0x10b32be0)", "hpengine SpriteBuilder.Build",
                ContractFieldStatus.Consumed, "hpengine SpriteBuilder.Build") },
            { "ActorSnapshot.ZoneEnvironmentMap", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "Actor.Region.Zone.EnvironmentMap: empty mesh slot texture (Render.dll disasm 0x10b11811-0x10b1183f)", "hpengine ActorBuilder",
                ContractFieldStatus.Consumed, "hpengine ActorBuilder.FallbackTexture") },
            { "ActorSnapshot.LevelEnvironmentMap", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "Actor.Level.EnvironmentMap: empty mesh slot texture (Render.dll disasm 0x10b11811-0x10b1183f)", "hpengine ActorBuilder",
                ContractFieldStatus.Consumed, "hpengine ActorBuilder.FallbackTexture") },
            { "ActorSnapshot.NoSmooth", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "AActor.bNoSmooth (+0x198 bit 0x2) -> PF_NoSmooth 0x800 (Render.dll FUN_10b338f0, Render.dll.c:39787); D3DDrv SetBlending — POINT filter (D3DDrv.dll.c:860-865)", "hpengine SpriteBuilder.Build, SdlActors.Fatten",
                ContractFieldStatus.Consumed, "hpengine SpriteBuilder.Build, SdlActors.Fatten") },
            { "ActorSnapshot.Skin", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "hpengine SdlActors.SlotTexture",
                ContractFieldStatus.Consumed, "hpengine SdlActors.SlotTexture") },
            { "ActorSnapshot.MultiSkins", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "hpengine SdlActors.SlotTexture",
                ContractFieldStatus.Consumed, "hpengine SdlActors.SlotTexture") },
            // The collision cylinder is a PRESENTATION metric of the frontend.
            // Game collision is owned by Core; the proxy factory needs these three values
            // to build a capsule, and must take them from the frame, not via live GetProp.
            // Viewport actor identity. The frontend uses it to know which
            // frame actor gets NO ordinary proxy: its presentation is
            // owned by the separate player rig.
            { "WorldFrame.LocalPlayerId", new ContractField(ContractFieldKind.Gameplay,
                "WorldFrameProjector.ProjectWorld", "UELevelRuntime.LocalPlayer", "diagnostic: diagnostic only: runtime_poc HeadlessSmoke",
                ContractFieldStatus.DiagnosticOnly, "diagnostic only: runtime_poc HeadlessSmoke") },
            // Blocking bit pair. Distinguishes an obstacle from a touch volume:
            // shipped picks the flag separately for each side of the pair
            // (AActor::IsBlockedBy, Engine.dll.c:21459-21545).
            // Per-frame visibility gates of the shipped predicate.
            { "ActorSnapshot.Light", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "Core UEActorLighting.BeginFrame, ActorFogFrame.Begin, UEActorLights.Update, MoverMode3.FromSnapshot, LevelFogMaps.LightsByExport, MoverMode3Lightmaps.Update, LevelFogMaps.Update",
                ContractFieldStatus.Consumed, "Core UEActorLighting.BeginFrame, ActorFogFrame.Begin, UEActorLights.Update, MoverMode3.FromSnapshot, LevelFogMaps.LightsByExport, MoverMode3Lightmaps.Update, LevelFogMaps.Update") },
            { "ActorSnapshot.Mover", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "hpengine SdlMovers.Build; Core UELevelRuntime.MoverLightmaps.Update",
                ContractFieldStatus.Consumed, "hpengine SdlMovers.Build; Core UELevelRuntime.MoverLightmaps.Update") },
            { "ActorSnapshot.Ambient", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "diagnostic: diagnostic only: runtime_poc HeadlessSmoke, runtime_poc SelfTest",
                ContractFieldStatus.DiagnosticOnly, "diagnostic only: runtime_poc HeadlessSmoke, runtime_poc SelfTest") },
            { "ActorSnapshot.Emitter", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "UE1 actor properties (AActor)", "hpengine SdlActors.Build, ParticleBuilder.Build",
                ContractFieldStatus.Consumed, "hpengine SdlActors.Build, ParticleBuilder.Build") },
            { "CameraSnapshot.Valid", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.Build", "UGameEngine::Draw 0x1039fa40 (PlayerCalcView)", "frontend camera",
                ContractFieldStatus.Consumed, "HP1CutSceneCameraBridge.LateUpdate (frame gate)") },
            { "CameraSnapshot.ViewActorId", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.Build", "UGameEngine::Draw 0x1039fa40 (PlayerCalcView)", "frontend camera") },
            { "CameraSnapshot.Location", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.Build", "UGameEngine::Draw 0x1039fa40 (PlayerCalcView)", "frontend camera",
                ContractFieldStatus.Consumed, "HP1CutSceneCameraBridge.LateUpdate") },
            { "CameraSnapshot.Rotation", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.Build", "UGameEngine::Draw 0x1039fa40 (PlayerCalcView)", "frontend camera",
                ContractFieldStatus.Consumed, "HP1CutSceneCameraBridge.LateUpdate") },
            { "CameraSnapshot.FovAngle", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.Build", "UGameEngine::Draw 0x1039fa40 (PlayerCalcView)", "frontend camera",
                ContractFieldStatus.Consumed, "HP1CutSceneCameraBridge.ApplyFov") },
            { "CameraSnapshot.BehindView", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.Build", "Pawn.bBehindView (+0x25c bit 0x1) of the viewport actor", "frontend camera",
                ContractFieldStatus.Consumed, "HP1ActorVisibility.Evaluate (the view target is not drawn in first person)") },
            { "MusicState.Valid", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.Build", "Galaxy.dll.c:7312-7466", "HP1MusicManager",
                ContractFieldStatus.Consumed, "HP1MusicManager.Consume") },
            { "MusicState.SongPath", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.Build", "Engine.u PlayerPawn.ClientSetMusic + Galaxy.dll.c:7392", "HP1MusicManager",
                ContractFieldStatus.Consumed, "HP1MusicManager.Consume") },
            { "MusicState.SongSection", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.Build", "Engine.u PlayerPawn.ClientSetMusic + Galaxy.dll.c:7394", "HP1MusicManager",
                ContractFieldStatus.Consumed, "HP1MusicManager.Consume") },
            { "MusicState.CdTrack", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.Build", "Engine.u PlayerPawn.ClientSetMusic + Galaxy.dll.c:7395", "HP1MusicManager",
                ContractFieldStatus.Consumed, "HP1MusicManager.Consume") },
            { "MusicState.Transition", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.Build", "Galaxy.dll.c:7312-7466", "HP1MusicManager",
                ContractFieldStatus.Consumed, "HP1MusicManager.Consume") },
            { "MusicState.DontLoop", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.Build", "Engine.u PlayerPawn.ClientSetMusic + Galaxy.dll.c:7408", "HP1MusicManager",
                ContractFieldStatus.Consumed, "HP1MusicManager.Consume") },
            { "MusicState.PercentVolume", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.Build", "Engine.u PlayerPawn.ClientSetPercentMusicVolume + Galaxy.dll.c:7250", "HP1MusicManager",
                ContractFieldStatus.Consumed, "HP1MusicManager.Consume") },
            { "AudioCommand.Kind", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.Build", "PlaySound (264) / PlayOwnedSound / StopSound (568) + Galaxy StartAmbience 0x10608434", "frontend audio layer",
                ContractFieldStatus.Consumed, "HP1AudioExecutor (parsing and executing the command)") },
            { "AudioCommand.ActorId", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.Build", "PlaySound (264) / PlayOwnedSound / StopSound (568) + Galaxy StartAmbience 0x10608434", "frontend audio layer",
                ContractFieldStatus.Consumed, "HP1AudioExecutor (parsing and executing the command)") },
            { "AudioCommand.ActorName", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.Build", "PlaySound (264) / PlayOwnedSound / StopSound (568) + Galaxy StartAmbience 0x10608434", "frontend audio layer",
                ContractFieldStatus.DiagnosticOnly, "HP1FrameConsumer (journal line)") },
            { "AudioCommand.SoundPath", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.Build", "PlaySound (264) / PlayOwnedSound / StopSound (568) + Galaxy StartAmbience 0x10608434", "frontend audio layer",
                ContractFieldStatus.Consumed, "HP1AudioExecutor (parsing and executing the command)") },
            { "AudioCommand.Slot", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.Build", "PlaySound (264) / PlayOwnedSound / StopSound (568) + Galaxy StartAmbience 0x10608434", "frontend audio layer",
                ContractFieldStatus.Consumed, "HP1AudioExecutor (parsing and executing the command)") },
            { "AudioCommand.Volume", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.Build", "PlaySound (264) / PlayOwnedSound / StopSound (568) + Galaxy StartAmbience 0x10608434", "frontend audio layer",
                ContractFieldStatus.Consumed, "HP1AudioExecutor (parsing and executing the command)") },
            { "AudioCommand.Radius", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.Build", "PlaySound (264) / PlayOwnedSound / StopSound (568) + Galaxy StartAmbience 0x10608434", "frontend audio layer",
                ContractFieldStatus.Consumed, "HP1AudioExecutor (parsing and executing the command)") },
            { "AudioCommand.Looping", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.Build", "sound loop flag: FSoundData +0x54 (v76 tail; version < 0x46 — WAV smpl loops)", "frontend audio layer",
                ContractFieldStatus.Consumed, "HP1AudioExecutor (channel loop)") },
            { "AudioCommand.Pitch", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.Build", "PlaySound (264) / PlayOwnedSound / StopSound (568) + Galaxy StartAmbience 0x10608434", "frontend audio layer",
                ContractFieldStatus.Consumed, "HP1AudioExecutor (parsing and executing the command)") },
            { "AudioCommand.NoOverride", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.Build", "PlaySound (264) / PlayOwnedSound / StopSound (568) + Galaxy StartAmbience 0x10608434", "frontend audio layer",
                ContractFieldStatus.Consumed, "HP1AudioExecutor (parsing and executing the command)") },
            { "AudioCommand.StartVolume", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.Build", "Galaxy StartAmbience 0x10608478-0x1060848c: SoundVolume*AmbientFactor/255", "Core audio pool",
                ContractFieldStatus.Consumed, "UEGalaxyAudio.ExecuteAmbientUpdate (channel re-acquired by the start loop)") },
            { "AudioCommand.Param", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.Build", "ModifySound (567), Engine.u ESoundParam: 0=Volume, 1=Radius, 2=Pitch", "frontend audio layer",
                ContractFieldStatus.Consumed, "HP1AudioExecutor.ExecuteModify (which channel parameter is modified)") },
            { "AudioCommand.Location", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.Build", "PlaySound (264) / PlayOwnedSound / StopSound (568) + Galaxy StartAmbience 0x10608434", "frontend audio layer",
                ContractFieldStatus.Consumed, "HP1AudioExecutor (parsing and executing the command)") },
            { "HudCommand.Kind", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.DriveHud", "UCanvas exec 464-470 from HPHud.PostRender", "HUD rasterizer",
                ContractFieldStatus.Consumed, "HP1HudRender.Rasterize (tile/text rasterization)") },
            { "HudCommand.X", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.DriveHud", "UCanvas exec 464-470 from HPHud.PostRender", "HUD rasterizer",
                ContractFieldStatus.Consumed, "HP1HudRender.Rasterize (tile/text rasterization)") },
            { "HudCommand.Y", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.DriveHud", "UCanvas exec 464-470 from HPHud.PostRender", "HUD rasterizer",
                ContractFieldStatus.Consumed, "HP1HudRender.Rasterize (tile/text rasterization)") },
            { "HudCommand.XL", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.DriveHud", "UCanvas exec 464-470 from HPHud.PostRender", "HUD rasterizer",
                ContractFieldStatus.Consumed, "HP1HudRender.Rasterize (tile/text rasterization)") },
            { "HudCommand.YL", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.DriveHud", "UCanvas exec 464-470 from HPHud.PostRender", "HUD rasterizer",
                ContractFieldStatus.Consumed, "HP1HudRender.Rasterize (tile/text rasterization)") },
            { "HudCommand.U", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.DriveHud", "UCanvas exec 464-470 from HPHud.PostRender", "HUD rasterizer",
                ContractFieldStatus.Consumed, "HP1HudRender.Rasterize (tile/text rasterization)") },
            { "HudCommand.V", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.DriveHud", "UCanvas exec 464-470 from HPHud.PostRender", "HUD rasterizer",
                ContractFieldStatus.Consumed, "HP1HudRender.Rasterize (tile/text rasterization)") },
            { "HudCommand.UL", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.DriveHud", "UCanvas exec 464-470 from HPHud.PostRender", "HUD rasterizer",
                ContractFieldStatus.Consumed, "HP1HudRender.Rasterize (tile/text rasterization)") },
            { "HudCommand.VL", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.DriveHud", "UCanvas exec 464-470 from HPHud.PostRender", "HUD rasterizer",
                ContractFieldStatus.Consumed, "HP1HudRender.Rasterize (tile/text rasterization)") },
            { "HudCommand.TexturePath", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.DriveHud", "UCanvas exec 464-470 from HPHud.PostRender", "HUD rasterizer",
                ContractFieldStatus.Consumed, "HP1HudRender.Rasterize (tile/text rasterization)") },
            { "HudCommand.Text", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.DriveHud", "UCanvas exec 464-470 from HPHud.PostRender", "HUD rasterizer",
                ContractFieldStatus.Consumed, "HP1HudRender.Rasterize (tile/text rasterization)") },
            { "HudCommand.FontPath", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.DriveHud", "UCanvas exec 464-470 from HPHud.PostRender", "HUD rasterizer",
                ContractFieldStatus.Consumed, "HP1HudRender.Rasterize (tile/text rasterization)") },
            { "HudCommand.Color", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.DriveHud", "UCanvas exec 464-470 from HPHud.PostRender", "HUD rasterizer",
                ContractFieldStatus.Consumed, "HP1HudRender.Rasterize (tile/text rasterization)") },
            // Style: HUD blending is decided by PolyFlags (below); the rasteriser no longer
            // reads Style.
            { "HudCommand.Style", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.DriveHud", "UCanvas exec 464-470 from HPHud.PostRender", "HUD rasterizer") },
            { "HudCommand.Smooth", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.DriveHud", "UCanvas exec 464-470 from HPHud.PostRender", "HUD rasterizer") },
            { "HudCommand.Clipped", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.DriveHud", "UCanvas exec 464-470 from HPHud.PostRender", "HUD rasterizer") },
            { "HudCommand.PolyFlags", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.EmitTile / DriveHud texts",
                "tile: exec 466/468 (0x1038971d, 0x1038a0b7) | Texture->PolyFlags&PF_Masked (0x1038781f); text: WrappedPrint 0x10387d54, DrawTextClipped 0x1038a3f1", "HUD rasterizer",
                ContractFieldStatus.Consumed, "HP1HudRender.DrawWithFlags (tile and glyph blending)") },
            { "HudCommand.Glyphs", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.DriveHud", "FUN_10388530 (Engine.dll 0x10388530): text layout for a paged font", "HUD rasterizer",
                ContractFieldStatus.Consumed, "HP1HudRender.Rasterize (glyph rasterization)") },
            { "HudGlyph.Page", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.DriveHud", "FUN_10388b60 (Engine.dll 0x10388b60): RenDev->DrawTile of a glyph after clipping to the frame", "HUD rasterizer",
                ContractFieldStatus.Consumed, "HP1HudRender.Rasterize (glyph rasterization)") },
            { "HudGlyph.X", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.DriveHud", "FUN_10388b60 (Engine.dll 0x10388b60): RenDev->DrawTile of a glyph after clipping to the frame", "HUD rasterizer",
                ContractFieldStatus.Consumed, "HP1HudRender.Rasterize (glyph rasterization)") },
            { "HudGlyph.Y", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.DriveHud", "FUN_10388b60 (Engine.dll 0x10388b60): RenDev->DrawTile of a glyph after clipping to the frame", "HUD rasterizer",
                ContractFieldStatus.Consumed, "HP1HudRender.Rasterize (glyph rasterization)") },
            { "HudGlyph.XL", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.DriveHud", "FUN_10388b60 (Engine.dll 0x10388b60): RenDev->DrawTile of a glyph after clipping to the frame", "HUD rasterizer",
                ContractFieldStatus.Consumed, "HP1HudRender.Rasterize (glyph rasterization)") },
            { "HudGlyph.YL", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.DriveHud", "FUN_10388b60 (Engine.dll 0x10388b60): RenDev->DrawTile of a glyph after clipping to the frame", "HUD rasterizer",
                ContractFieldStatus.Consumed, "HP1HudRender.Rasterize (glyph rasterization)") },
            { "HudGlyph.U", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.DriveHud", "FUN_10388b60 (Engine.dll 0x10388b60): RenDev->DrawTile of a glyph after clipping to the frame", "HUD rasterizer",
                ContractFieldStatus.Consumed, "HP1HudRender.Rasterize (glyph rasterization)") },
            { "HudGlyph.V", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.DriveHud", "FUN_10388b60 (Engine.dll 0x10388b60): RenDev->DrawTile of a glyph after clipping to the frame", "HUD rasterizer",
                ContractFieldStatus.Consumed, "HP1HudRender.Rasterize (glyph rasterization)") },
            { "HudGlyph.UL", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.DriveHud", "FUN_10388b60 (Engine.dll 0x10388b60): RenDev->DrawTile of a glyph after clipping to the frame", "HUD rasterizer",
                ContractFieldStatus.Consumed, "HP1HudRender.Rasterize (glyph rasterization)") },
            { "HudGlyph.VL", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.DriveHud", "FUN_10388b60 (Engine.dll 0x10388b60): RenDev->DrawTile of a glyph after clipping to the frame", "HUD rasterizer",
                ContractFieldStatus.Consumed, "HP1HudRender.Rasterize (glyph rasterization)") },
            { "ParticleInstance.EmitterId", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer (AParticleFX::Update)", "Engine.dll UnParticleFX.cpp", "HP1ParticleBridge",
                ContractFieldStatus.Consumed, "HP1ParticleBridge.PushFrameInstances") },
            { "ParticleInstance.Pos", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer (AParticleFX::Update)", "Engine.dll UnParticleFX.cpp", "HP1ParticleBridge",
                ContractFieldStatus.Consumed, "HP1ParticleBridge.PushFrameInstances") },
            { "ParticleInstance.Width", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer (AParticleFX::Update)", "Engine.dll UnParticleFX.cpp", "HP1ParticleBridge",
                ContractFieldStatus.Consumed, "HP1ParticleBridge.PushFrameInstances") },
            { "ParticleInstance.Length", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer (AParticleFX::Update)", "Engine.dll UnParticleFX.cpp", "HP1ParticleBridge",
                ContractFieldStatus.Consumed, "HP1ParticleBridge.PushFrameInstances") },
            { "ParticleInstance.Alpha", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer (AParticleFX::Update)", "Engine.dll UnParticleFX.cpp", "HP1ParticleBridge",
                ContractFieldStatus.Consumed, "HP1ParticleBridge.PushFrameInstances") },
            { "ParticleInstance.Color", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer (AParticleFX::Update)", "Engine.dll UnParticleFX.cpp", "HP1ParticleBridge",
                ContractFieldStatus.Consumed, "HP1ParticleBridge.PushFrameInstances") },
            { "ParticleInstance.SpinPos", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer (AParticleFX::Update)", "Engine.dll UnParticleFX.cpp", "HP1ParticleBridge",
                ContractFieldStatus.Consumed, "HP1ParticleBridge.PushFrameInstances") },
            { "ParticleInstance.SpinRate", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer (AParticleFX::Update)", "UParticle +0x80 (Engine disasm 0x103bfcd1)", "hpengine ParticleBuilder",
                ContractFieldStatus.Consumed, "hpengine ParticleBuilder.Billboard (rotation gate, Render.dll 0x10b18603)") },
            { "ParticleInstance.TexturePath", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer (AParticleFX::Update)", "Engine.dll UnParticleFX.cpp", "HP1ParticleBridge",
                ContractFieldStatus.Consumed, "HP1ParticleBridge.PushFrameInstances") },
            { "ParticleInstance.Vel", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer (AParticleFX::Update)", "Render.dll FUN_10b15b50: drip streak along the particle velocity (+0x18)", "HP1ParticleBridge",
                ContractFieldStatus.Consumed, "HP1ParticleBridge.BuildLiquidQuad (PPRIM_Liquid)") },
            { "ParticleInstance.View", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.ParticleDrawerFade (one entry per DrawParticleSystem call)", "Render.dll FUN_10b184d0: the billboard pass of the call in that frame decides which particles are drawn, with which Alpha", "hpengine ParticleBuilder",
                ContractFieldStatus.Consumed, "hpengine ParticleBuilder.Build (draws the particle only in its call's view)") },
            { "ParticleInstance.DripTimer", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer (AParticleFX::Update)", "Render.dll FUN_10b17ce0: particle DripTimer (+0x74) > 0 — hanging drip, otherwise FUN_10b15b50", "HP1ParticleBridge",
                ContractFieldStatus.Consumed, "HP1ParticleBridge.BuildLiquidQuad (PPRIM_Liquid)") },
            { "LightState.Type", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "UE1 ALight properties; WorldRadius = shipped AActor::WorldLightRadius = max(DrawScale,1)*(LightRadius+1)*25 (Engine.dll 0x1031ba10; SE UActor.h:516 without the multiplier — loses to shipped)", "HP1DynamicLight/HP1Lighting/HP1LightSource",
                ContractFieldStatus.Consumed, "HP1DynamicLight.LateUpdate (LT_None filter)") },
            { "WorldFrame.HighDetailActors", new ContractField(ContractFieldKind.Presentation,
                "UELevelRuntime.ReadHighDetailActors", "second half of the bHighDetail gate: RenDev->HighDetailActors (Render.dll.c:10843; name and offset 100 from the native class registration, Engine.dll.c:153242-153248). The device is named by the ini — Engine.Engine.GameRenderDevice; the assigned SoftDrv has False, hardware devices have True", "HP1ActorVisibility",
                ContractFieldStatus.Consumed, "HP1ActorVisibility.Evaluate (bHighDetail gate)") },
            { "WorldFrame.Paused", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.ProjectWorld", "LevelInfo.Pauser non-empty (Engine.dll.c:262651) from slot 0; written by GameInfo.SetPause bytecode (menu pause: FEBook.OpenBook -> LaunchUWindow -> SetPause); Core itself makes the gameplay decision (TickActorsPass), the field is for presentations that shipped freezes inside the actor tick", "diagnostic: diagnostic only: runtime_poc HeadlessSmoke",
                ContractFieldStatus.DiagnosticOnly, "diagnostic only: runtime_poc HeadlessSmoke") },
            { "WorldFrame.DrawWorld", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.Build", "Render->DrawWorld gate (Engine.dll.c:117935-117939): frame non-null and !Console.bNoDrawWorld; bNoDrawWorld is set by WindowConsole.LaunchUWindow (menu) — the world under the book is not drawn", "hpengine TravelFrameHold.Update, HudBatcher.Build",
                ContractFieldStatus.Consumed, "hpengine TravelFrameHold.Update, HudBatcher.Build") },
            { "WorldFrame.ViewFlash", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.ProjectViewFlash", "PlayerPawn.FlashFog of the viewport actor — the state is computed by shipped bytecode in Core (ClientFadeIn/Out, ClientFlash, ClientInstantFlash, ClientAdjustGlow, the PlayerPawn.ViewFlash integrator); read by UGameEngine::Draw (Engine.dll.c:117867-117890)", "HP1ScreenFade",
                ContractFieldStatus.Consumed, "HP1ScreenFade.OnGUI (EndFlash quad)") },
            { "ViewFlashState.Valid", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.ProjectViewFlash", "viewport actor present", "HP1ScreenFade",
                ContractFieldStatus.Consumed, "HP1ScreenFade.OnGUI (frame gate)") },
            { "ViewFlashState.R", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.ProjectViewFlash", "FlashFog.X (+0x4f8) -> clamp01 -> EndFlash quad color (D3DDrv.dll.c:2311, ONE/SRCALPHA)", "HP1ScreenFade",
                ContractFieldStatus.Consumed, "HP1ScreenFade.OnGUI") },
            { "ViewFlashState.G", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.ProjectViewFlash", "FlashFog.Y (+0x4fc)", "HP1ScreenFade",
                ContractFieldStatus.Consumed, "HP1ScreenFade.OnGUI") },
            { "ViewFlashState.B", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.ProjectViewFlash", "FlashFog.Z (+0x500)", "HP1ScreenFade",
                ContractFieldStatus.Consumed, "HP1ScreenFade.OnGUI") },
            { "ViewFlashState.W", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.ProjectViewFlash", "FlashFog.W (+0x504): devFlashScale = clamp01(W·0.5), quad alpha = min(2·scale,1) (Engine.dll.c:117870; D3DDrv.dll.c:2340)", "HP1ScreenFade",
                ContractFieldStatus.Consumed, "HP1ScreenFade.OnGUI") },
            { "WorldFrame.Reverb", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.ProjectReverb", "Galaxy UpdateReverb FUN_106095c4 (0x106095c4-0x1060976d): zone (ViewTarget ?: viewport actor).Region.Zone, bReverbZone gate", "AudioSystem.UpdateReverb",
                ContractFieldStatus.Consumed, "AudioSystem.UpdateReverb (hpengine)") },
            { "WorldFrame.AudioChannels", new ContractField(ContractFieldKind.Presentation,
                "UEGalaxyAudio.Snapshot (UEWorldFrameProducer.Build)", "UGalaxyAudioSubsystem: 16-channel table (PlaySoundW Galaxy.dll.c:1728-1871), Update 0x106088f4 — Volume/Pan/Frequency, end of data", "hpengine AudioSystem.Sync",
                ContractFieldStatus.Consumed, "hpengine AudioSystem.Sync (voices by Serial)") },
            { "WorldFrame.AudioResumeSerial", new ContractField(ContractFieldKind.Presentation,
                "UEGalaxyAudio.ResumeSerial", "author improvement D-51 (not original): song held while the viewport is lost", "hpengine AudioSystem.Sync",
                ContractFieldStatus.Consumed, "hpengine AudioSystem.Sync (the song stream is not closed)") },
            { "WorldFrame.AudioCallStops", new ContractField(ContractFieldKind.Presentation,
                "UEGalaxyAudio.LastCallStops (UEWorldFrameProducer.Build)", "K2 (author decision 2026-11-06): pool voices freed by the frame's device calls (StopSound) before Galaxy Update 0x106088f4", "hpengine AudioSystem.ApplyFrame",
                ContractFieldStatus.Consumed, "hpengine AudioSystem.ApplyFrame (stops before the frame's mix)") },
            { "WorldFrame.AudioMixDt", new ContractField(ContractFieldKind.Presentation,
                "UEGalaxyAudio.LastMixDt (UEWorldFrameProducer.Build)", "K2: the dt the frame's Galaxy Update advanced the audio clock by", "hpengine AudioSystem.ApplyFrame",
                ContractFieldStatus.Consumed, "hpengine AudioSystem.ApplyFrame (mix length of the frame)") },
            { "WorldFrame.SpriteActors", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.DirectRenderStep (DrawFrame)", "FDynamicSprite::Setup, sprite branch (Render.dll 0x10b30e4b..0x10b3111d) + OccludeBsp/PreRender, DrawFrame -> DrawActorSprite", "hpengine SpriteBuilder",
                ContractFieldStatus.Consumed, "hpengine SpriteBuilder.Build (only the named sprites are drawn)") },
            { "WorldFrame.DrawOrder", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.DirectRenderStep (DrawFrame)", "URender::DrawFrame sorted sprite array, pass 1 then pass 2 (UESpriteSort, Render.dll 0x10b2666a..0x10b26734): DrawActorSprite order per frame", "hpengine HPEngineProgram.FrameDraws",
                ContractFieldStatus.Consumed, "hpengine DrawSequence.Order (actor draws of a frame in exactly this order)") },
            { "WorldFrame.CanvasScale", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.SetScreen (canvas = screen / scale before the step)", "NOT ORIGINAL (D-44 Interface Size, author 2026-11-08): UEConfig.UIScaleFor(H, [HPEngine] UIScaleStep)", "hpengine HudBatcher.Build",
                ContractFieldStatus.Consumed, "hpengine HudBatcher.Build (the frame's HUD commands scaled by it)") },
            { "WorldFrame.ActorViews", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.DirectRenderStep (DrawFrame)", "each frame of the tree draws its own Frame->Sprite list (Render.dll.c:1011-1014): frames that drew each actor", "hpengine SdlRenderer/SdlActors",
                ContractFieldStatus.Consumed, "hpengine SdlRenderer.DrawActors (actor batch only in passes of its view set)") },
            { "WorldFrame.EnhancedVisuals", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.Build (UEConfig.VisualModeIsOriginal)", "NOT ORIGINAL (author decisions 2026-10-15, 2026-11-08): [HPEngine] VisualMode != Original (no menu item; the player always gets Enhanced, Original only from the ini or HPEngine --visual); Enhanced = runtime-switchable visual improvements (D-53 anisotropy, D-48 trilinear mips; D-54 in Core)", "hpengine Renderer",
                ContractFieldStatus.Consumed, "hpengine Renderer.WorldSampler (anisotropic trilinear vs shipped bilinear with point mips)") },
            { "WorldFrame.Coronas", new ContractField(ContractFieldKind.Presentation,
                "UECoronas.Step (UEWorldFrameProducer.DirectRenderStep, DrawFrame)", "master frame corona tiles in slot order (Render.dll 0x10b26a8c..0x10b26e07)", "hpengine CoronaBuilder",
                ContractFieldStatus.Consumed, "hpengine CoronaBuilder.Build (projection + tile)") },
            { "CoronaInstance.ActorId", new ContractField(ContractFieldKind.Presentation,
                "UECoronas.Step (UEWorldFrameProducer.DirectRenderStep, DrawFrame)", "slot actor (+0 of the 0xc-byte slot)", "hpengine CoronaBuilder",
                ContractFieldStatus.Consumed, "hpengine CoronaBuilder.Build") },
            { "CoronaInstance.Skin", new ContractField(ContractFieldKind.Presentation,
                "UECoronas.Step (UEWorldFrameProducer.DirectRenderStep, DrawFrame)", "Skin (+0x160), the DrawIcon texture", "hpengine CoronaBuilder",
                ContractFieldStatus.Consumed, "hpengine CoronaBuilder.Build") },
            { "CoronaInstance.Location", new ContractField(ContractFieldKind.Presentation,
                "UECoronas.Step (UEWorldFrameProducer.DirectRenderStep, DrawFrame)", "Location (+0xfc), projected by Frame->Coords", "hpengine CoronaBuilder",
                ContractFieldStatus.Consumed, "hpengine CoronaBuilder.Build") },
            { "CoronaInstance.DrawScale", new ContractField(ContractFieldKind.Presentation,
                "UECoronas.Step (UEWorldFrameProducer.DirectRenderStep, DrawFrame)", "DrawScale (+0x16c): side = Frame->X * DrawScale * 0.8 (0x10b38fd0)", "hpengine CoronaBuilder",
                ContractFieldStatus.Consumed, "hpengine CoronaBuilder.Build") },
            { "CoronaInstance.OnScreen", new ContractField(ContractFieldKind.Presentation,
                "UECoronas.Project (UECoronas.Step, DrawFrame)", "V.Z > 1 (fcomp 0x10b385b0): the tile is drawn", "hpengine CoronaBuilder",
                ContractFieldStatus.Consumed, "hpengine CoronaBuilder.Build") },
            { "CoronaInstance.X0", new ContractField(ContractFieldKind.Presentation,
                "UECoronas.Project (UECoronas.Step, DrawFrame)", "tile left, Frame->X units: centre V.X·Proj.Z/V.Z + FX2 − size/2 (0x10b26b03..0x10b26d71)", "hpengine CoronaBuilder",
                ContractFieldStatus.Consumed, "hpengine CoronaBuilder.Build") },
            { "CoronaInstance.Y0", new ContractField(ContractFieldKind.Presentation,
                "UECoronas.Project (UECoronas.Step, DrawFrame)", "tile top: V.Y·Proj.Z/V.Z + FY2 − size/2", "hpengine CoronaBuilder",
                ContractFieldStatus.Consumed, "hpengine CoronaBuilder.Build") },
            { "CoronaInstance.X1", new ContractField(ContractFieldKind.Presentation,
                "UECoronas.Project (UECoronas.Step, DrawFrame)", "tile right: X0 + size", "hpengine CoronaBuilder",
                ContractFieldStatus.Consumed, "hpengine CoronaBuilder.Build") },
            { "CoronaInstance.Y1", new ContractField(ContractFieldKind.Presentation,
                "UECoronas.Project (UECoronas.Step, DrawFrame)", "tile bottom: Y0 + size", "hpengine CoronaBuilder",
                ContractFieldStatus.Consumed, "hpengine CoronaBuilder.Build") },
            { "CoronaInstance.FrameX", new ContractField(ContractFieldKind.Presentation,
                "UECoronas.Project (UECoronas.Step, DrawFrame)", "Frame->X the tile was projected for (pixel scale = screen width / FrameX)", "hpengine CoronaBuilder",
                ContractFieldStatus.Consumed, "hpengine CoronaBuilder.Build") },
            { "CoronaInstance.R", new ContractField(ContractFieldKind.Presentation,
                "UECoronas.Step (UEWorldFrameProducer.DirectRenderStep, DrawFrame)", "HueSat(LightHue, LightSaturation).r * slot brightness (thunk_FUN_10b2aab0)", "hpengine CoronaBuilder",
                ContractFieldStatus.Consumed, "hpengine CoronaBuilder.Build") },
            { "CoronaInstance.G", new ContractField(ContractFieldKind.Presentation,
                "UECoronas.Step (UEWorldFrameProducer.DirectRenderStep, DrawFrame)", "HueSat(...).g * slot brightness", "hpengine CoronaBuilder",
                ContractFieldStatus.Consumed, "hpengine CoronaBuilder.Build") },
            { "CoronaInstance.B", new ContractField(ContractFieldKind.Presentation,
                "UECoronas.Step (UEWorldFrameProducer.DirectRenderStep, DrawFrame)", "HueSat(...).b * slot brightness", "hpengine CoronaBuilder",
                ContractFieldStatus.Consumed, "hpengine CoronaBuilder.Build") },
            { "WorldFrame.Views", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.ProjectViews", "UEOcclusion child frames (URender::OccludeBsp: sky 0x10b236a6, mirror 0x10b23abd, warp 0x10b23eb8; CreateChildFrame 0x10b20790)", "hpengine Renderer.SetViews",
                ContractFieldStatus.Consumed, "hpengine Renderer.BuildViews (child views: sky, mirrors, warp)") },
            { "WorldFrame.MeshPoses", new ContractField(ContractFieldKind.Presentation,
                "UELevelRuntime.DrawMeshActor (UEWorldFrameProducer.DirectRenderStep)", "GetFrame of the mesh branch of URender::DrawActorSprite (Render.dll 0x10b32e4a-0x10b32ea1; third-person weapon 0x10b32f85-0x10b330ba)", "hpengine ActorBuilder",
                ContractFieldStatus.Consumed, "hpengine ActorBuilder.Build (a mesh is drawn only with the frame's pose)") },
            { "MeshPose.ObjectId", new ContractField(ContractFieldKind.Presentation,
                "UELevelRuntime.DrawMeshActor", "the actor GetFrame was run for (for a weapon — the weapon actor itself)", "hpengine ActorBuilder",
                ContractFieldStatus.Consumed, "hpengine ActorBuilder.Build / AddWeapon") },
            { "MeshPose.Weapon", new ContractField(ContractFieldKind.Presentation,
                "UELevelRuntime.DrawMeshActor", "third-person weapon branch of DrawActorSprite (Render.dll 0x10b32f85-0x10b330ba)", "hpengine ActorBuilder",
                ContractFieldStatus.Consumed, "hpengine ActorBuilder.Build / AddWeapon") },
            { "MeshPose.MeshPath", new ContractField(ContractFieldKind.Presentation,
                "UELevelRuntime.DrawMeshActor", "Mesh during GetFrame (for a weapon — ThirdPersonMesh for the duration of the swap, 0x10b32fc1-0x10b32ff3)", "hpengine ActorBuilder",
                ContractFieldStatus.Consumed, "hpengine ActorBuilder (raw mesh: wedges, materials)") },
            { "MeshPose.Points", new ContractField(ContractFieldKind.Presentation,
                "UELevelRuntime.DrawMeshActor", "USkeletalMesh::GetFrame (Engine 0x1041df50: ApplyAnim + skinning) / ULodMesh::GetFrame (Engine 0x1035e460); for a weapon — in the weapon bone's frame (Render.dll.c:12072-12120)", "hpengine ActorBuilder",
                ContractFieldStatus.Consumed, "hpengine ActorBuilder.AddMesh (mesh vertices)") },
            { "MeshPose.LodParts", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.MeshDrawParts (DirectRenderStep)", "LOD level and pose per draw of URender::DrawLodMesh (Render.dll 0x10b0ffcd-0x10b1011d, UEMeshLod.VertsRequested, then GetFrame vtable +0xa4 0x10b10270); null = full mesh, one pose (always in Enhanced unless poses differ, D-62 improvement)", "hpengine ActorBuilder",
                ContractFieldStatus.Consumed, "hpengine ActorBuilder.AddPosed (one job per part)") },
            { "MeshLodPart.Verts", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.MeshDrawParts", "drawn vertex count after the GetFrame clamp / tween cap (-1 = no LOD); faces UEMeshLod.CollapsedFaces (Render.dll.c:14590-14640)", "hpengine ActorBuilder",
                ContractFieldStatus.Consumed, "hpengine ActorBuilder.AddPosed (UEMeshSource.LodVariant)") },
            { "MeshLodPart.ViewSet", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.MeshDrawParts", "index into WorldFrame.ActorViews.Sets: the frames drawing this level", "hpengine ActorBuilder",
                ContractFieldStatus.Consumed, "hpengine ActorBuilder.AddMesh (the part's frames; one batch per frame)") },
            { "MeshLodPart.Points", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.MeshDrawParts", "the part's own GetFrame vertices when its draws' pose differs from MeshPose.Points (GetFrame per draw: ApplyAnim channel tween, ULodMesh vertex-cache refill 0x1035e888..0x1035e900); null = MeshPose.Points", "hpengine ActorBuilder",
                ContractFieldStatus.Consumed, "hpengine ActorBuilder.AddMesh (own pose, not interpolated)") },
            { "ViewFrame.Parent", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.ProjectViews", "frame parent (FSceneNode +8) — index into Views, −1 — master frame", "hpengine Renderer.BuildViews",
                ContractFieldStatus.Consumed, "hpengine Renderer.BuildViews (view tree)") },
            { "ViewFrame.Kind", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.ProjectViews", "OccludeBsp branch that spawned the frame: sky (PF_FakeBackdrop 0x80), mirror (PF_Mirrored), warp (PF_Portal of AWarpZoneInfo)", "hpengine Renderer.BuildViews",
                ContractFieldStatus.Consumed, "hpengine Renderer.BuildViews (sky/mirror/portal batches)") },
            { "ViewFrame.Recursion", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.ProjectViews", "FSceneNode.Recursion (+0x1c)", "— (Core decides the < 3 limit)",
                ContractFieldStatus.Unconsumed, "—") },
            { "ViewFrame.M", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.ProjectViews", "master frame Coords⁻¹ ∘ frame Coords (+0x34): frame world point -> master frame world point", "hpengine Renderer.BuildViews",
                ContractFieldStatus.Consumed, "hpengine Renderer.DrawScene (view camera rows: rows · M)") },
            { "ViewFrame.ClipN", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.ProjectViews", "frame NearClip (+0x24, frame axes) in the frame's world: normal; 0 — no NearClip", "hpengine Renderer.BuildViews",
                ContractFieldStatus.Consumed, "hpengine shader (view clipping)") },
            { "ViewFrame.ClipD", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.ProjectViews", "frame NearClip in the frame's world: n·p + d ≥ 0 is kept (ClipBspSurf 0x10b2019e)", "hpengine Renderer.BuildViews",
                ContractFieldStatus.Consumed, "hpengine shader (view clipping)") },
            { "ViewFrame.Flip", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.ProjectViews", "FSceneNode.Mirror (+0x20) = −1", "hpengine Renderer.BuildViews",
                ContractFieldStatus.Consumed, "hpengine Renderer (face traversal of the mirrored view)") },
            { "WorldFrame.Drawn", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.ProjectViews (DrawnOf)", "UESceneNode.DrawnPolys of each frame of the tree: FSavedPoly of URender::OccludeBsp for a non-SpanBased device (Render.dll.c:6602-6616, 6641-6656); index 0 master, i + 1 Views[i]; null — no OccludeBsp this tick", "hpengine Renderer / MoverScene",
                ContractFieldStatus.Consumed, "hpengine WorldMesh.Select (level nodes per view), MoverScene.EmitViews (mover polygons per view)") },
            { "WorldFrame.DrawnTextures", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.DrawnTexturesOf", "Surf->Texture of every drawn surface: DrawFrame calls Texture->Update(Viewport->CurrentTime) for each (Render.dll 0x10b2587d..0x10b25898); no texture — DefaultTexture without Update (0x10b258af..0x10b258c1)", "hpengine Renderer",
                ContractFieldStatus.Consumed, "hpengine Renderer.AnimateWorld / TickProc (AnimNext and procedural textures advance only when drawn)") },
            { "DrawnWorld.Nodes", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.DrawnOf", "FSavedPoly.iNode of level-model surfaces, OccludeBsp order", "hpengine WorldMesh.Select",
                ContractFieldStatus.Consumed, "hpengine WorldMesh.Select (index runs of the view)") },
            { "DrawnWorld.Movers", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.DrawnOf", "FSavedPoly of tracker surfaces (FMovingBrushTracker fragments) as (mover, brush FPoly), once per polygon", "hpengine MoverScene",
                ContractFieldStatus.Consumed, "hpengine MoverScene.EmitViews (mover triangles of the view)") },
            { "DrawnWorld.BackNodes", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.DrawnOf", "FSavedPoly nodes drawn with the eye on their back side (OccludeBsp side 0, Render.dll.c:5868-5874): lit with iZone[0]", "hpengine WorldMesh.Select, Interp.Drawn",
                ContractFieldStatus.Consumed, "hpengine WorldMesh.Select (the node's back run: iZone[0] tile, pan, fog)") },
            { "DrawnWorld.Reflagged", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.DrawnOf", "drawn polygons whose draw-record PolyFlags OccludeBsp changed: PF_Mirrored|PF_Translucent with RenDev ShinySurfaces == 0 -> PolyFlags & ~PF_Translucent | 0x80000000, pass 1 (Render 0x10b23a8e..0x10b23ab8)", "hpengine WorldMesh.Select, MoverScene.EmitViews, Interp.Drawn",
                ContractFieldStatus.Consumed, "hpengine WorldMesh.Select / MoverScene.EmitViews (the polygon's reflagged run: blending, opaque pass, no reflection)") },
            { "DrawnWorld.SkyWindows", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.DrawnOf", "PF_FakeBackdrop nodes whose sky branch spawned the frame's sky child (OccludeBsp returns before FSavedPoly, UEOcclusion.Surface)", "hpengine WorldMesh.Select",
                ContractFieldStatus.Consumed, "hpengine Renderer.DrawWorld (the window shows the sky view)") },
            { "ViewFrame.Zone", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.ProjectViews", "the child frame's zone: CreateChildFrame iZone (sky zone; the parent's for a mirror; OtherSideActor.iWarpZone for a warp)", "diagnostic: hpengine DrawLog (VIEW zone=, judged against the original's CHILD zone=)",
                ContractFieldStatus.DiagnosticOnly, "diagnostic only: hpengine DrawLog (SdlRenderer.LogView)") },
            { "ViewFrame.Coords", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.ProjectViews", "FSceneNode.Coords (+0x34) of the child frame (CreateChildFrame FUN_10b20790)", "hpengine per-view setup",
                ContractFieldStatus.Consumed, "hpengine SpriteMath.ViewCamera / ActorBuilder / ActorFogFrame (eye and axes of the view)") },
            { "ViewFrame.Sources", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.ProjectViews", "surfaces whose branch spawned the frame (port: CreateChildFrame merges frames; sky and mirror have iSurf = 0)", "hpengine Renderer.BuildViews",
                ContractFieldStatus.Consumed, "hpengine Renderer.BuildViews (which batch takes the view target)") },
            { "ViewSource.Surf", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.ProjectViews", "level-model surface; −1 — mover surface", "hpengine Renderer.BuildViews",
                ContractFieldStatus.Consumed, "hpengine Renderer.BuildViews (WorldMesh.MirrorPlaneOfSurf / WarpPortals)") },
            { "ViewSource.MoverId", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.ProjectViews", "mover of a tracker surface (FMovingBrushTracker::SetupActorBrush, FPoly.iLink = iSurf), ObjectId; 0 — level", "hpengine Renderer.BuildViews",
                ContractFieldStatus.Consumed, "hpengine MoverScene.MirrorPlaneOf") },
            { "ViewSource.MoverPoly", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.ProjectViews", "FPoly number in the mover brush (UPolys)", "hpengine Renderer.BuildViews",
                ContractFieldStatus.Consumed, "hpengine MoverScene.MirrorPlaneOf") },
            { "Affine.M00", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.ProjectViews", "row 0 column 0 element (p' = rows · (p, 1))", "hpengine Renderer.BuildViews",
                ContractFieldStatus.Consumed, "hpengine Renderer.RowsOf") },
            { "Affine.M01", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.ProjectViews", "row 0 column 1 element (p' = rows · (p, 1))", "hpengine Renderer.BuildViews",
                ContractFieldStatus.Consumed, "hpengine Renderer.RowsOf") },
            { "Affine.M02", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.ProjectViews", "row 0 column 2 element (p' = rows · (p, 1))", "hpengine Renderer.BuildViews",
                ContractFieldStatus.Consumed, "hpengine Renderer.RowsOf") },
            { "Affine.M03", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.ProjectViews", "row 0 column 3 element (p' = rows · (p, 1))", "hpengine Renderer.BuildViews",
                ContractFieldStatus.Consumed, "hpengine Renderer.RowsOf") },
            { "Affine.M10", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.ProjectViews", "row 1 column 0 element (p' = rows · (p, 1))", "hpengine Renderer.BuildViews",
                ContractFieldStatus.Consumed, "hpengine Renderer.RowsOf") },
            { "Affine.M11", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.ProjectViews", "row 1 column 1 element (p' = rows · (p, 1))", "hpengine Renderer.BuildViews",
                ContractFieldStatus.Consumed, "hpengine Renderer.RowsOf") },
            { "Affine.M12", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.ProjectViews", "row 1 column 2 element (p' = rows · (p, 1))", "hpengine Renderer.BuildViews",
                ContractFieldStatus.Consumed, "hpengine Renderer.RowsOf") },
            { "Affine.M13", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.ProjectViews", "row 1 column 3 element (p' = rows · (p, 1))", "hpengine Renderer.BuildViews",
                ContractFieldStatus.Consumed, "hpengine Renderer.RowsOf") },
            { "Affine.M20", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.ProjectViews", "row 2 column 0 element (p' = rows · (p, 1))", "hpengine Renderer.BuildViews",
                ContractFieldStatus.Consumed, "hpengine Renderer.RowsOf") },
            { "Affine.M21", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.ProjectViews", "row 2 column 1 element (p' = rows · (p, 1))", "hpengine Renderer.BuildViews",
                ContractFieldStatus.Consumed, "hpengine Renderer.RowsOf") },
            { "Affine.M22", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.ProjectViews", "row 2 column 2 element (p' = rows · (p, 1))", "hpengine Renderer.BuildViews",
                ContractFieldStatus.Consumed, "hpengine Renderer.RowsOf") },
            { "Affine.M23", new ContractField(ContractFieldKind.Presentation,
                "UEWorldFrameProducer.ProjectViews", "row 2 column 3 element (p' = rows · (p, 1))", "hpengine Renderer.BuildViews",
                ContractFieldStatus.Consumed, "hpengine Renderer.RowsOf") },
            { "ReverbState.Valid", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.ProjectReverb", "bReverbZone gate (ZoneInfo+0x2f8 & 1); not part of the Galaxy words", "AudioSystem.UpdateReverb",
                ContractFieldStatus.Consumed, "AudioSystem.UpdateReverb (hpengine)") },
            { "ReverbState.MasterGain", new ContractField(ContractFieldKind.Presentation,
                "ReverbState.FromZoneBytes", "byte(ZoneInfo+0x300) · (double)1/255 (0x1063a400)", "ZoneReverb.Set",
                ContractFieldStatus.Consumed, "AudioMixer.Reverb.Set (hpengine)") },
            { "ReverbState.CutoffHz", new ContractField(ContractFieldKind.Presentation,
                "ReverbState.FromZoneBytes", "clamp(int(ZoneInfo+0x304), 0, 0xac44)", "ZoneReverb.Set",
                ContractFieldStatus.Consumed, "AudioMixer.Reverb.Set (hpengine)") },
            { "ReverbState.Delay0", new ContractField(ContractFieldKind.Presentation,
                "ReverbState.FromZoneBytes", "byte(ZoneInfo+0x308+0) · 0.002f, clamp [0.001, 0.34]", "ZoneReverb.Set",
                ContractFieldStatus.Consumed, "AudioMixer.Reverb.Set (hpengine)") },
            { "ReverbState.Delay1", new ContractField(ContractFieldKind.Presentation,
                "ReverbState.FromZoneBytes", "byte(ZoneInfo+0x308+1) · 0.002f, clamp [0.001, 0.34]", "ZoneReverb.Set",
                ContractFieldStatus.Consumed, "AudioMixer.Reverb.Set (hpengine)") },
            { "ReverbState.Delay2", new ContractField(ContractFieldKind.Presentation,
                "ReverbState.FromZoneBytes", "byte(ZoneInfo+0x308+2) · 0.002f, clamp [0.001, 0.34]", "ZoneReverb.Set",
                ContractFieldStatus.Consumed, "AudioMixer.Reverb.Set (hpengine)") },
            { "ReverbState.Delay3", new ContractField(ContractFieldKind.Presentation,
                "ReverbState.FromZoneBytes", "byte(ZoneInfo+0x308+3) · 0.002f, clamp [0.001, 0.34]", "ZoneReverb.Set",
                ContractFieldStatus.Consumed, "AudioMixer.Reverb.Set (hpengine)") },
            { "ReverbState.Delay4", new ContractField(ContractFieldKind.Presentation,
                "ReverbState.FromZoneBytes", "byte(ZoneInfo+0x308+4) · 0.002f, clamp [0.001, 0.34]", "ZoneReverb.Set",
                ContractFieldStatus.Consumed, "AudioMixer.Reverb.Set (hpengine)") },
            { "ReverbState.Delay5", new ContractField(ContractFieldKind.Presentation,
                "ReverbState.FromZoneBytes", "byte(ZoneInfo+0x308+5) · 0.002f, clamp [0.001, 0.34]", "ZoneReverb.Set",
                ContractFieldStatus.Consumed, "AudioMixer.Reverb.Set (hpengine)") },
            { "ReverbState.Gain0", new ContractField(ContractFieldKind.Presentation,
                "ReverbState.FromZoneBytes", "byte(ZoneInfo+0x30e+0) · (float)1/255, clamp [0.001, 0.999]", "ZoneReverb.Set",
                ContractFieldStatus.Consumed, "AudioMixer.Reverb.Set (hpengine)") },
            { "ReverbState.Gain1", new ContractField(ContractFieldKind.Presentation,
                "ReverbState.FromZoneBytes", "byte(ZoneInfo+0x30e+1) · (float)1/255, clamp [0.001, 0.999]", "ZoneReverb.Set",
                ContractFieldStatus.Consumed, "AudioMixer.Reverb.Set (hpengine)") },
            { "ReverbState.Gain2", new ContractField(ContractFieldKind.Presentation,
                "ReverbState.FromZoneBytes", "byte(ZoneInfo+0x30e+2) · (float)1/255, clamp [0.001, 0.999]", "ZoneReverb.Set",
                ContractFieldStatus.Consumed, "AudioMixer.Reverb.Set (hpengine)") },
            { "ReverbState.Gain3", new ContractField(ContractFieldKind.Presentation,
                "ReverbState.FromZoneBytes", "byte(ZoneInfo+0x30e+3) · (float)1/255, clamp [0.001, 0.999]", "ZoneReverb.Set",
                ContractFieldStatus.Consumed, "AudioMixer.Reverb.Set (hpengine)") },
            { "ReverbState.Gain4", new ContractField(ContractFieldKind.Presentation,
                "ReverbState.FromZoneBytes", "byte(ZoneInfo+0x30e+4) · (float)1/255, clamp [0.001, 0.999]", "ZoneReverb.Set",
                ContractFieldStatus.Consumed, "AudioMixer.Reverb.Set (hpengine)") },
            { "ReverbState.Gain5", new ContractField(ContractFieldKind.Presentation,
                "ReverbState.FromZoneBytes", "byte(ZoneInfo+0x30e+5) · (float)1/255, clamp [0.001, 0.999]", "ZoneReverb.Set",
                ContractFieldStatus.Consumed, "AudioMixer.Reverb.Set (hpengine)") },
            { "WorldFrame.LevelAmbient", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.ProjectLevelAmbient", "level ambient from SLOT 0 (UnLevel.h:0xf7-0xf8)", "Core UEActorLighting.BeginFrame",
                ContractFieldStatus.Consumed, "Core UEActorLighting.BeginFrame") },
            { "LevelAmbientState.Hue", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.ProjectLevelAmbient", "ZoneInfo.AmbientHue of ULevel::Actors(0); shipped ASSERTS the slot rather than searching by class (UnLevel.h:0xf7-0xf8), and Tut1 carries a second LevelInfo (LevelInfo5, Actors(3)) with the same ambient — measured 170/100/20 for both", "Core UEActorLighting.BeginFrame",
                ContractFieldStatus.Consumed, "Core UEActorLighting.BeginFrame") },
            { "LevelAmbientState.Saturation", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.ProjectLevelAmbient", "ZoneInfo.AmbientSaturation of ULevel::Actors(0) (UnLevel.h:0xf7-0xf8)", "Core UEActorLighting.BeginFrame",
                ContractFieldStatus.Consumed, "Core UEActorLighting.BeginFrame") },
            { "LevelAmbientState.Brightness", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.ProjectLevelAmbient", "ZoneInfo.AmbientBrightness of ULevel::Actors(0) (UnLevel.h:0xf7-0xf8)", "Core UEActorLighting.BeginFrame",
                ContractFieldStatus.Consumed, "Core UEActorLighting.BeginFrame") },
            { "LevelAmbientState.Valid", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.ProjectLevelAmbient", "world is empty -> nowhere to take ambient from", "Core UEActorLighting.BeginFrame",
                ContractFieldStatus.Consumed, "Core UEActorLighting.BeginFrame") },
            { "LightState.Scale", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.ProjectLight", "per-frame light type multiplier: Render.dll table 0x10b38288 by LightType — Pulse/SubtlePulse sine of TimeSeconds·35·65536/LightPeriod + LightPhase·256 (0x10b06390/0x10b06510), Blink/Strobe dim (0x10b06410/0x10b064b0), Flicker — random per-frame table (0x10b06470); Steady/Backdrop = 1", "Core UEActorLighting.BeginFrame, ActorFogFrame.Begin, MoverMode3.FromSnapshot, LevelFogMaps.Update",
                ContractFieldStatus.Consumed, "Core UEActorLighting.BeginFrame, ActorFogFrame.Begin, MoverMode3.FromSnapshot, LevelFogMaps.Update") },
            { "LightState.NoDelete", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "bNoDelete — second half of the static/dynamic split: URender::SetupDynamics distributes a light over BSP leaves only if NOT `(bStatic || bNoDelete) && !bDynamicLight` (Render.dll.c:11183-11185)", "Core UEActorLighting.BeginFrame, MoverMode3.FromSnapshot",
                ContractFieldStatus.Consumed, "Core UEActorLighting.BeginFrame, MoverMode3.FromSnapshot") },
            { "LightState.Source", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "ELightSource, byte at AActor+0x1f4: Render.dll.c:16887 compares it with 2 (LD_Ambient — Lambert factor FORCED to 1.0, flood) and :16900 with 1", "Core UEActorLighting.BeginFrame, ActorFogFrame.Begin, MoverMode3.FromSnapshot, LevelFogMaps.Update",
                ContractFieldStatus.Consumed, "Core UEActorLighting.BeginFrame, ActorFogFrame.Begin, MoverMode3.FromSnapshot, LevelFogMaps.Update") },
            { "LightState.Effect", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "UE1 ALight properties; WorldRadius = shipped AActor::WorldLightRadius = max(DrawScale,1)*(LightRadius+1)*25 (Engine.dll 0x1031ba10; SE UActor.h:516 without the multiplier — loses to shipped)", "HP1DynamicLight/HP1Lighting/HP1LightSource",
                ContractFieldStatus.Consumed, "HP1DynamicLight.LateUpdate (LE_NonIncidence)") },
            { "LightState.Brightness", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "UE1 ALight properties; WorldRadius = shipped AActor::WorldLightRadius = max(DrawScale,1)*(LightRadius+1)*25 (Engine.dll 0x1031ba10; SE UActor.h:516 without the multiplier — loses to shipped)", "HP1DynamicLight/HP1Lighting/HP1LightSource",
                ContractFieldStatus.Consumed, "HP1DynamicLight.LateUpdate (color/filter)") },
            { "LightState.Hue", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "UE1 ALight properties; WorldRadius = shipped AActor::WorldLightRadius = max(DrawScale,1)*(LightRadius+1)*25 (Engine.dll 0x1031ba10; SE UActor.h:516 without the multiplier — loses to shipped)", "HP1DynamicLight/HP1Lighting/HP1LightSource",
                ContractFieldStatus.Consumed, "HP1DynamicLight.LateUpdate (color)") },
            { "LightState.Saturation", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "UE1 ALight properties; WorldRadius = shipped AActor::WorldLightRadius = max(DrawScale,1)*(LightRadius+1)*25 (Engine.dll 0x1031ba10; SE UActor.h:516 without the multiplier — loses to shipped)", "HP1DynamicLight/HP1Lighting/HP1LightSource",
                ContractFieldStatus.Consumed, "HP1DynamicLight.LateUpdate (color)") },
            { "LightState.Radius", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "UE1 ALight properties; WorldRadius = shipped AActor::WorldLightRadius = max(DrawScale,1)*(LightRadius+1)*25 (Engine.dll 0x1031ba10; SE UActor.h:516 without the multiplier — loses to shipped)", "HP1DynamicLight/HP1Lighting/HP1LightSource") },
            { "LightState.WorldRadius", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "UE1 ALight properties; WorldRadius = shipped AActor::WorldLightRadius = max(DrawScale,1)*(LightRadius+1)*25 (Engine.dll 0x1031ba10; SE UActor.h:516 without the multiplier — loses to shipped)", "HP1DynamicLight/HP1Lighting/HP1LightSource",
                ContractFieldStatus.Consumed, "HP1DynamicLight.LateUpdate (light radius)") },
            { "LightState.RadiusInner", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "UE1 ALight properties; WorldRadius = shipped AActor::WorldLightRadius = max(DrawScale,1)*(LightRadius+1)*25 (Engine.dll 0x1031ba10; SE UActor.h:516 without the multiplier — loses to shipped)", "HP1DynamicLight/HP1Lighting/HP1LightSource",
                ContractFieldStatus.Consumed, "HP1DynamicLight.LateUpdate (falloff plateau)") },
            { "LightState.Cone", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "UE1 ALight properties; WorldRadius = shipped AActor::WorldLightRadius = max(DrawScale,1)*(LightRadius+1)*25 (Engine.dll 0x1031ba10; SE UActor.h:516 without the multiplier — loses to shipped)", "HP1DynamicLight/HP1Lighting/HP1LightSource") },
            { "LightState.Phase", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "UE1 ALight properties; WorldRadius = shipped AActor::WorldLightRadius = max(DrawScale,1)*(LightRadius+1)*25 (Engine.dll 0x1031ba10; SE UActor.h:516 without the multiplier — loses to shipped)", "HP1DynamicLight/HP1Lighting/HP1LightSource") },
            { "LightState.Period", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "UE1 ALight properties; WorldRadius = shipped AActor::WorldLightRadius = max(DrawScale,1)*(LightRadius+1)*25 (Engine.dll 0x1031ba10; SE UActor.h:516 without the multiplier — loses to shipped)", "HP1DynamicLight/HP1Lighting/HP1LightSource") },
            { "LightState.VolumeBrightness", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "UE1 ALight properties; WorldRadius = shipped AActor::WorldLightRadius = max(DrawScale,1)*(LightRadius+1)*25 (Engine.dll 0x1031ba10; SE UActor.h:516 without the multiplier — loses to shipped)", "HP1DynamicLight/HP1Lighting/HP1LightSource") },
            { "LightState.VolumeRadius", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "light VolumeRadius: volumetric fog radius (VolumeRadius+1)·25 — Render.dll.c:479 (ComputeFromActor), :4584 (LeafVolumetricLighting), :11190 (SetupDynamics)", "HP1FogMaps") },
            { "LightState.VolumeFog", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "light VolumeFog: fog ramp alpha = VolumeFog/255 (Render.dll.c:486, _DAT_10b38694) and vertex fog alpha (:17071)", "HP1FogMaps") },
            { "LightState.Corona", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "UE1 ALight properties; WorldRadius = shipped AActor::WorldLightRadius = max(DrawScale,1)*(LightRadius+1)*25 (Engine.dll 0x1031ba10; SE UActor.h:516 without the multiplier — loses to shipped)", "HP1DynamicLight/HP1Lighting/HP1LightSource") },
            { "LightState.DynamicLight", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "UE1 ALight properties; WorldRadius = shipped AActor::WorldLightRadius = max(DrawScale,1)*(LightRadius+1)*25 (Engine.dll 0x1031ba10; SE UActor.h:516 without the multiplier — loses to shipped)", "HP1DynamicLight/HP1Lighting/HP1LightSource") },
            { "LightState.SpecialLit", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "UE1 ALight properties; WorldRadius = shipped AActor::WorldLightRadius = max(DrawScale,1)*(LightRadius+1)*25 (Engine.dll 0x1031ba10; SE UActor.h:516 without the multiplier — loses to shipped)", "HP1DynamicLight/HP1Lighting/HP1LightSource") },
            { "LightState.Static", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "light bStatic — the fork of the per-actor selection CAP: a non-static is skipped at total>2, a static at static>2 (Render.dll.c:21399-21403 + 21435-21437)", "HP1Lighting",
                ContractFieldStatus.Consumed, "HP1Lighting.GatherActorLights (selection cap)") },
            { "LightState.ViewRotation", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.ProjectLight", "AActor::GetViewRotation of the light (Engine.dll.c:103977-104001: IsA(APawn) ? ViewRotation +0x33c : Rotation +0x108) — the direction of the spot filler (Render.dll.c:18492) and of LightSource 1 (disasm 0x10b06e8e..0x10b06ea8)", "Core MoverMode3.FromSnapshot",
                ContractFieldStatus.Consumed, "Core MoverMode3.FromSnapshot") },
            { "LightState.Movable", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.ProjectLight", "bMovable (light+0x198 bit 0x20000): a non-static light with this bit is accepted by per-actor selection without a visibility trace and without lighting a fresh cache to full (disasm Render.dll 0x10b0a21f-0x10b0a22d -> 0x10b0a349, bypassing 0x10b0a338)", "UEActorLighting",
                ContractFieldStatus.Consumed, "UEActorLighting.Gather (accept without LOS)") },
            { "LightState.Dark", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "bDarkLight (light+0x1f0 bit 0x10): final color × (−1.0) (_DAT_10b386a4=.rdata −1.0, Render.dll.c:21374-21379) — subtractive light", "Core UEActorLighting.BeginFrame, MoverMode3.FromSnapshot",
                ContractFieldStatus.Consumed, "Core UEActorLighting.BeginFrame, MoverMode3.FromSnapshot") },
            { "MoverState.KeyNum", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "Mover interpolation state (KeyNum/PhysAlpha/bInterpolating)", "door proxy") },
            { "MoverState.PrevKeyNum", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "Mover interpolation state (KeyNum/PhysAlpha/bInterpolating)", "door proxy") },
            { "MoverState.NumKeys", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "Mover interpolation state (KeyNum/PhysAlpha/bInterpolating)", "door proxy") },
            { "MoverState.PhysAlpha", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "Mover interpolation state (KeyNum/PhysAlpha/bInterpolating)", "door proxy") },
            { "MoverState.Interpolating", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "Mover interpolation state (KeyNum/PhysAlpha/bInterpolating)", "door proxy") },
            { "MoverState.BrushExportIndex", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "Mover interpolation state (KeyNum/PhysAlpha/bInterpolating)", "door proxy") },
            { "MoverState.BrushName", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "Mover interpolation state (KeyNum/PhysAlpha/bInterpolating)", "door proxy") },
            { "MoverState.ToWorld", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project (UELevelRuntime.BrushToWorld)", "ABrush::ToWorld from Location/Rotation/PrePivot/MainScale/PostScale (validate_brush.py layer 1)", "hpengine MoverScene",
                ContractFieldStatus.Consumed, "hpengine MoverScene.Build (mover polygon vertices)") },
            { "MoverState.DynamicLightMover", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "Mover.bDynamicLightMover (mode-3 lighting, MoverMode3Lightmaps)", "hpengine MoverScene",
                ContractFieldStatus.Consumed, "hpengine MoverScene.ZoneLayerTiles (zone layer skips mode-3 movers)") },
            { "AmbientState.SoundPath", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "actor AmbientSound/SoundRadius/Volume/Pitch; WorldRadius = (SoundRadius+1)*25 (AActor::WorldSoundRadius, Engine.dll.c:159116)", "frontend audio layer") },
            { "AmbientState.SoundRadius", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "actor AmbientSound/SoundRadius/Volume/Pitch; WorldRadius = (SoundRadius+1)*25 (AActor::WorldSoundRadius, Engine.dll.c:159116)", "frontend audio layer") },
            { "AmbientState.WorldRadius", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "actor AmbientSound/SoundRadius/Volume/Pitch; WorldRadius = (SoundRadius+1)*25 (AActor::WorldSoundRadius, Engine.dll.c:159116)", "frontend audio layer") },
            { "AmbientState.SoundVolume", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "actor AmbientSound/SoundRadius/Volume/Pitch; WorldRadius = (SoundRadius+1)*25 (AActor::WorldSoundRadius, Engine.dll.c:159116)", "frontend audio layer") },
            { "AmbientState.SoundPitch", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "actor AmbientSound/SoundRadius/Volume/Pitch; WorldRadius = (SoundRadius+1)*25 (AActor::WorldSoundRadius, Engine.dll.c:159116)", "frontend audio layer") },
            { "ParticleState.Valid", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "LIVE properties of the AParticleFX emitter (defaultproperties are only their initial value; EnableEmission/script writes change them at runtime)", "HP1ParticleBridge") },
            { "ParticleState.ParticlesPerSec", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "LIVE properties of the AParticleFX emitter (defaultproperties are only their initial value; EnableEmission/script writes change them at runtime)", "HP1ParticleBridge") },
            { "ParticleState.Lifetime", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "LIVE properties of the AParticleFX emitter (defaultproperties are only their initial value; EnableEmission/script writes change them at runtime)", "HP1ParticleBridge") },
            { "ParticleState.RenderPrimitive", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "LIVE properties of the AParticleFX emitter (defaultproperties are only their initial value; EnableEmission/script writes change them at runtime)", "HP1ParticleBridge") },
            { "ParticleState.TexturePaths", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "LIVE properties of the AParticleFX emitter (defaultproperties are only their initial value; EnableEmission/script writes change them at runtime)", "HP1ParticleBridge") },
            { "FloatRange.Base", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "HPParticle range struct (Base/Rand)", "HP1ParticleBridge") },
            { "FloatRange.Rand", new ContractField(ContractFieldKind.Presentation,
                "WorldFrameProjector.Project", "HPParticle range struct (Base/Rand)", "HP1ParticleBridge") },
        };

        // ── Printing values: also by reflection, so it cannot lag behind the type ──
        static string Scalar(object v)
        {
            if (v is null) return "-";
            if (v is float f) return f.ToString("R", CultureInfo.InvariantCulture);
            if (v is double d) return d.ToString("R", CultureInfo.InvariantCulture);
            if (v is bool b) return b ? "1" : "0";
            if (v is string s) return s.Length == 0 ? "\"\"" : s;
            if (v is UEVector vec) return Scalar(vec.x) + "," + Scalar(vec.y) + "," + Scalar(vec.z);
            if (v is UERotator r) return r.Pitch + "," + r.Yaw + "," + r.Roll;
            if (v is BrushCoords c) return Scalar(c.Origin) + ";" + Scalar(c.X) + ";" + Scalar(c.Y) + ";" + Scalar(c.Z);
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        static bool IsContractType(Type t)
        {
            foreach (Type k in Types) if (k == t) return true;
            return false;
        }

        // One contract entry: prints EVERY field with its name, nested
        // contract types recursively, lists with length and elements (otherwise
        // a lost element would be indistinguishable from an empty list).
        public static void Describe(StringBuilder into, string prefix, object value, Type declared)
        {
            if (value is null) { into.Append(prefix).Append("=<null>\n"); return; }
            Type t = declared ?? value.GetType();
            if (!IsContractType(t)) { into.Append(prefix).Append('=').Append(Scalar(value)).Append('\n'); return; }
            foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object fv = f.GetValue(value);
                string name = prefix + "." + f.Name;
                if (fv is string || fv is null) { into.Append(name).Append('=').Append(Scalar(fv)).Append('\n'); }
                else if (fv is IEnumerable list && !(fv is string))
                {
                    int n = 0;
                    var body = new StringBuilder();
                    foreach (object e in list)
                    {
                        if (e is null || IsContractType(e.GetType()))
                            Describe(body, name + "[" + n + "]", e, e?.GetType());
                        else body.Append(name).Append('[').Append(n).Append("]=").Append(Scalar(e)).Append('\n');
                        n++;
                    }
                    into.Append(name).Append(".count=").Append(n).Append('\n').Append(body);
                }
                else if (IsContractType(f.FieldType)) Describe(into, name, fv, f.FieldType);
                else into.Append(name).Append('=').Append(Scalar(fv)).Append('\n');
            }
        }
    }
}
