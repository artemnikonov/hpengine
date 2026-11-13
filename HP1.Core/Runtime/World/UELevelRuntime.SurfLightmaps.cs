using System;
using System.Collections.Generic;

namespace HP1.UEScript
{
    // PER-FRAME LIGHTMAPS OF LEVEL SURFACES — owned by Core.
    // The body is LevelSurfLightmap / LevelSurfLightmapRuntime (the shipped
    // SetupForSurf rule and its addresses are in the LevelSurfLightmap.cs header).
    // Here only the world binding: model, LightBits and zone ambient from live actors.
    public sealed partial class UELevelRuntime
    {
        LevelSurfLightmapRuntime _surfLightmaps;
        WorldStatic _surfLightmapsWorld;

        // Per-frame lightmap object of the CURRENT world; null = no world. A new world
        // (another WorldStatic instance) gets a new object: the previous map's caches
        // do not survive the transition.
        public LevelSurfLightmapRuntime SurfaceLightmaps
        {
            get
            {
                HP1.Core.UECoreOwner.Check("UELevelRuntime.SurfaceLightmaps");
                WorldStatic w = LatestWorldStatic;
                if (w?.Geometry is null || Bsp?.ParseTables?.LightBits is null) return null;
                if (_surfLightmaps is not null && ReferenceEquals(_surfLightmapsWorld, w)) return _surfLightmaps;
                _surfLightmaps = new LevelSurfLightmapRuntime(w.Geometry, Bsp.ParseTables.LightBits);
                _surfLightmapsWorld = w;
                return _surfLightmaps;
            }
        }

        // One frame of per-frame lightmaps: takes the latest Core frame and the
        // render frame state. Tiles are in SurfaceLightmaps.Tiles.
        // A repeated call on the SAME Core frame rebuilds nothing (no tiles):
        // the result is a function of the frame, while the frontend calls at its own rate.
        WorldFrame _surfLightmapsFrame;
        // Whether RunEngineFrame computes the lightmaps (set by the frontend that draws them).
        public bool ComputeSurfaceLightmapsInFrame;
        public LevelSurfLightmapRuntime UpdateSurfaceLightmaps()
        {
            LevelSurfLightmapRuntime r = BeginSurfaceLightmaps(out Action level);
            if (level is null) return r;
            level();
            UpdateMoverSurfaceLightmaps();
            return r;
        }

        // UpdateSurfaceLightmaps in two parts for RunEngineFrame. `level` (null: nothing to build this frame) is the level
        // runtime's Update: it writes only that runtime and reads only the Core frame, the producer's lightmap inputs of the
        // frame and zone actors' ambient bytes (UEProps reads are lock-free), so it may run on a worker while the mover,
        // fog and actor-light passes run on the caller's thread; none of those writes what it reads.
        internal LevelSurfLightmapRuntime BeginSurfaceLightmaps(out Action level)
        {
            level = null;
            LevelSurfLightmapRuntime r = SurfaceLightmaps;
            if (r is null) return null;
            if (ReferenceEquals(_surfLightmapsFrame, LatestFrame)) { r.ClearTiles(); _moverLightmaps?.Tiles.Clear(); return r; }
            _surfLightmapsFrame = LatestFrame;
            if (FrameProducer?.SurfDynamicLightsRefusal is string refusal && !r.Refusals.Contains(refusal)) r.Refusals.Add(refusal);
            WorldFrame frame = LatestFrame;
            SurfLightmap.FrameFx fx = FrameProducer?.LightmapFx;
            IReadOnlyDictionary<int, int[]> dyn = FrameProducer?.SurfDynamicLights;
            level = () => r.Update(frame, fx, ZoneAmbientHSB, dyn);
            if (FrameCacheVerify.Enabled) _verifySerial = () => VerifySurfaceLightmaps(r, frame, fx, dyn);
            return r;
        }

        // HP1_FRAME_CACHE_VERIFY=1: after the join, a second runtime of the same world runs the same Update on the frame
        // thread with no other pass beside it; its tiles must equal the worker's byte for byte (a race or a buffer
        // shared between threads shows up here).
        Action _verifySerial;
        LevelSurfLightmapRuntime _surfSerial, _surfSerialOf;
        internal void VerifySurfaceLightmapsSerial() { Action v = _verifySerial; _verifySerial = null; v?.Invoke(); }
        void VerifySurfaceLightmaps(LevelSurfLightmapRuntime r, WorldFrame frame, SurfLightmap.FrameFx fx, IReadOnlyDictionary<int, int[]> dyn)
        {
            if (!ReferenceEquals(_surfSerialOf, r)) { _surfSerial = new LevelSurfLightmapRuntime(LatestWorldStatic.Geometry, Bsp.ParseTables.LightBits); _surfSerialOf = r; }
            _surfSerial.Update(frame, fx, ZoneAmbientHSB, dyn);
            IReadOnlyList<LevelSurfLightmapRuntime.Tile> a = _surfSerial.Tiles, b = r.Tiles;
            // One hit per compared tile (hits=0: no tile was built, nothing was compared).
            if (a.Count != b.Count) { FrameCacheVerify.Hit("surface_lightmaps_worker", $"frame {EngineFrameCount}: {a.Count} serial tiles vs {b.Count}"); return; }
            for (int i = 0; i < a.Count; i++)
                FrameCacheVerify.Hit("surface_lightmaps_worker",
                    a[i].LightMap != b[i].LightMap || a[i].Zone != b[i].Zone || a[i].Width != b[i].Width || a[i].Height != b[i].Height
                    || a[i].Frame != b[i].Frame || !a[i].Rgb.AsSpan().SequenceEqual(b[i].Rgb)
                        ? $"frame {EngineFrameCount}: tile {i} (lightmap {b[i].LightMap} zone {b[i].Zone}) differs from the serial build" : null);
        }

        internal void UpdateMoverSurfaceLightmaps()
            => MoverLightmaps?.Update(LatestFrame, FrameProducer?.LightmapFx, ZoneAmbientHSB, FrameProducer?.SurfDynamicLights);

        // Zone fog maps of the CURRENT world, per frame of the tick's occlusion tree (LevelFogMaps); built with the
        // per-frame lightmaps (ComputeSurfaceLightmapsInFrame). A new world gets a new object.
        LevelFogMaps _fogMaps;
        WorldStatic _fogMapsWorld;
        public LevelFogMaps FogMaps
        {
            get
            {
                HP1.Core.UECoreOwner.Check("UELevelRuntime.FogMaps");
                WorldStatic w = LatestWorldStatic;
                if (w?.Geometry is null || LatestFrame is null) return null;
                if (_fogMaps is not null && ReferenceEquals(_fogMapsWorld, w)) return _fogMaps;
                _fogMaps = new LevelFogMaps(w.Geometry, SurfTexturePolyFlags, LevelFogMaps.Zones(w.Geometry, LatestFrame));
                _fogMapsWorld = w;
                return _fogMaps;
            }
        }

        // After the tick's DrawWorld: the frames that drew this tick (no occlusion -> no tiles).
        internal void UpdateFogMaps()
        {
            LevelFogMaps fm = FogMaps;
            if (fm is null) return;
            if (LatestFrame.Drawn is null) { fm.Update(null, null, 0f); return; }
            fm.Update(FrameProducer.LastMasterFrame, LatestFrame.Actors, FrameProducer.LightmapFx?.LevelBrightness
                ?? throw new InvalidOperationException("fog maps: the frame drew but has no Level.Brightness (LightmapFx)"));
        }

        // Zone ambient for seeding the lightmap: bytes of the zone actor
        // Model->Zones[zone].ZoneActor, or Actors(0) (LevelInfo) without one:
        // the shipped fallback in the BSP walk (Render.dll.c:5996-6000). Reads
        // +0x2d0/+0x2d1/+0x2d2 (AmbientBrightness/Hue/Saturation) — R:20385-20392.
        public (int h, int s, int b) ZoneAmbientHSB(int zone)
        {
            int[] zoneActors = Bsp?.RawZoneActors;   // same array as StaticGeometry.ZoneActorExports
            int export = zoneActors is not null && zone >= 0 && zone < zoneActors.Length ? zoneActors[zone] : 0;
            UEObject z = null;
            if (export > 0) ActorsByExport.TryGetValue(export, out z);
            if (z is null) z = Actors.Count > 0 ? Actors[0] : null;
            if (z is null) throw new InvalidOperationException($"zone ambient {zone}: neither a zone actor nor Actors(0)");
            return (UEValue.AsByte(z.GetProp(PK.AmbientHue)), UEValue.AsByte(z.GetProp(PK.AmbientSaturation)),
                    UEValue.AsByte(z.GetProp(PK.AmbientBrightness)));
        }
    }
}
