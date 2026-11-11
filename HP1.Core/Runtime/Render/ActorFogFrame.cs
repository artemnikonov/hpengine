// ACTOR VERTEX FOG (C4) — Core's (moved from the frontend 2026-10-29: one owner of the volumetric light records with
// LevelFogMaps). Inputs: static volumetric lights, the frame tree's camera bases and records, IsFogZone. The fog body is
// FogMap.VertexFog
// (FLightManager::LightAndFog, Render.dll.c:16990-17110: f·=2, a = min(1, f·VolumeFog/255),
// rgb = min(1, (1−a)·rgb + f·col), A = min(1, A + a)); density is the same FogMap.Density as for a surface
// fog map. Only wiring here: frame camera basis, light records by light ObjectId (a spawned light has no ExportIndex),
// record selection by the actor's sprite Volumetrics list (UESprite.VolLeafMark: SetupForActor's param_4 = sprite+0xb8,
// marked +0x50 at 0x10b0a45a..0x10b0a4c2, the fog list = the +0x50 records 0x10b0a583..0x10b0a595, sorted far first by
// appQsort at 0x10b0a5b1 — independent of LightRadius, the 3+3 cap and line of sight; PF_RenderFog only when the list is
// not empty, 0x10b0a47c), vertex to camera space and quantisation into the D3D specular byte.
// Dynamic volumetric lights reach a sprite through URender::LeafLights of the walked leaves (FilterLight's leaf half,
// UEOcclusion): the +0x14-flagged links LeafVolumetricLighting appended before the sprite's last PreRender
// (UESceneNode.VolumetricDynamic up to UESprite.VolLeafMark), one Stamp with the statics.
//
// UE world space: vertices arrive in the UE world as Core returns them (UELevelRuntime.EvaluatePosePoints).
//
// Records: a static leaf volumetric light, or a dynamic one THAT frame's walk appended (UESceneNode.VolumetricDynamic);
// colour FGetHSV(H, S, 255)·(Level.Brightness·Brightness·Scale/255)
// (ComputeFromActor scales every record by Level->Brightness, Render.dll.c:19274-19280).
// The DAT_10b658d8 flag (LightAndFog, Render.dll.c:17011-17058) is global in shipped and per actor here (`int outside`):
// with the flag set shipped first tests |d|²·R² − (d·v)² <= |d|²·r, the same inequality as the full test multiplied by
// |d|² > 0, so only rounding at the fog boundary (density -> 0) can differ — a presentation-only number within the §1
// tolerance. A frame without a camera keeps the previous basis; no frame is drawn without a camera and the next one resets.
using System;
using System.Collections.Generic;
using System.Threading;

namespace HP1.UEScript
{
    public sealed class ActorFogFrame
    {
        // ActorFogRecords: records of ALL volumetric lights of the frame by ObjectId.
        readonly Dictionary<int, FogMap.Light> _records = new();
        HashSet<int> _fogZones = new();

        // Frame camera basis: x — right, y — up, z — forward.
        public bool CameraBasisValid { get; private set; }
        public UEVector Cam, AxX, AxY, AxZ;
        public IReadOnlyDictionary<int, FogMap.Light> Records => _records;

        // "No fog" reasons are reported, as in the fog journal line. There is no "no mesh" reason
        // (FogSkipNoMesh) in OpenHP1.
        public int FoggedActors, FoggedVertices, SkipNotInZone /* not drawn as a sprite by that frame */, SkipNoBasis, SkipNoLights, SkipNoVolumetric;

        // Bind clears the basis and records. fogZones — LevelFogMaps.Zones; g — the level model (leaf volumetric chains).
        // Shipped LightAndFog does not read surface fog maps: actor fog does not depend on whether any surface has one.
        public void Bind(HashSet<int> fogZones, StaticGeometry g)
        {
            _records.Clear(); CameraBasisValid = false;
            _fogZones = fogZones ?? new HashSet<int>();
            _g = g;
        }
        StaticGeometry _g;

        // IsFogZone: an actor's PF_RenderFog goes by the actor's ZONE (ZoneNumber among the level's fog zones), not by
        // the actor's own bFogZone bit (ActorSnapshot.FogZone is always false for non-ZoneInfo).
        public bool IsFogZone(int zoneNumber) => _fogZones.Contains(zoneNumber);

        static float Dot(UEVector a, UEVector b) => a.x * b.x + a.y * b.y + a.z * b.z;

        // Per-frame setup — the part that feeds actors: frame camera basis and volumetric light records
        // BEFORE frustum culling (LightAndFog walks the ACTOR's list, the fog-map frustum does not apply
        // to it). Surface fog maps are built by FogScene (SdlFog.cs).
        // A child frame (mirror, portal, sky) has its own basis and records: the frame's Coords (ViewFrame.Coords), as
        // LightAndFog runs in the view space of the Frame DrawMesh is given.
        sealed class ViewBasis
        {
            public UEVector Cam, AxX, AxY, AxZ;
            public readonly Dictionary<int, FogMap.Light> Records = new();
            public readonly Dictionary<int, int> MarkOf = new();              // actor ObjectId -> UESprite.VolLeafMark
            public readonly Dictionary<int, (HashSet<int> Static, HashSet<int> Dynamic)> ByMark = new();
            public UESceneNode Node;
        }
        readonly ViewBasis _master = new();
        readonly List<ViewBasis> _views = new();

        // master — the tick's occlusion tree (frames in pre-order = master, then WorldFrame.Views); levelBrightness —
        // Level.Brightness.
        public void Begin(WorldFrame frame, UESceneNode master, float levelBrightness)
        {
            FoggedActors = FoggedVertices = SkipNotInZone = SkipNoBasis = SkipNoLights = SkipNoVolumetric = 0;
            if (frame == null || !frame.Camera.Valid || master is null) return;   // no basis reset
            var frames = new List<UESceneNode>();
            void Walk(UESceneNode f) { frames.Add(f); foreach (UESceneNode c in f.Children) Walk(c); }
            Walk(master);
            if (frames.Count != frame.Views.Count + 1)
                throw new InvalidOperationException($"actor fog: the occlusion tree has {frames.Count} frames, the WorldFrame {frame.Views.Count} views + master");
            CameraSnapshot cs = frame.Camera;
            BrushCoords mc = UESceneNode.MasterCoords(cs.Location, cs.Rotation);   // frame Coords: X right, Y down, Z forward
            Cam = cs.Location; AxX = mc.X; AxY = -mc.Y; AxZ = mc.Z;
            CameraBasisValid = true;
            LevelFogMaps.FillLightsByExport(frame.Actors, _lightsBy);
            _lightOf ??= LevelFogMaps.LightLookup(_lightsBy);
            _exportOf.Clear();
            foreach (ref readonly ActorSnapshot a in frame.ActorSpan) if (a.ExportIndex > 0) _exportOf[a.ObjectId] = a.ExportIndex;
            LevelFogMaps.FillObjectsByExport(frame.Actors, _objectOf);
            _master.Cam = Cam; _master.AxX = AxX; _master.AxY = AxY; _master.AxZ = AxZ;
            BuildRecords(frame, frames[0], levelBrightness, _master);
            _records.Clear(); foreach (var kv in _master.Records) _records[kv.Key] = kv.Value;
            _views.Clear();
            for (int i = 0; i < frame.Views.Count; i++)
            {
                ViewFrame v = frame.Views[i];
                var b = new ViewBasis { Cam = v.Coords.Origin, AxX = v.Coords.X, AxY = -v.Coords.Y, AxZ = v.Coords.Z };
                BuildRecords(frame, frames[i + 1], levelBrightness, b);
                _views.Add(b);
            }
        }
        // A third-person weapon is drawn with its pawn's FDynamicSprite (UEWorldFrameProducer.WeaponPawns): SetupForActor's
        // param_4 = that sprite's +0xb8, so the weapon takes the pawn's Volumetrics mark in every frame that drew the pawn.
        public void DrawnWithSpriteOf(int weapon, int pawn)
        {
            if (_master.MarkOf.TryGetValue(pawn, out int m)) _master.MarkOf[weapon] = m;
            foreach (ViewBasis vb in _views) if (vb.MarkOf.TryGetValue(pawn, out int mv)) vb.MarkOf[weapon] = mv;
        }

        Func<int, (bool, UEVector, int)> _lightOf;           // reads _lightsBy, refilled per Begin (allocation only)
        readonly Dictionary<int, (UEVector Location, int VolumeRadius)> _lightsBy = new();
        readonly Dictionary<int, int> _exportOf = new();   // ObjectId -> ExportIndex (static volumetric lights are by export)
        readonly Dictionary<int, int> _objectOf = new();             // ExportIndex -> ObjectId (the shared +0xec Stamp)

        void BuildRecords(WorldFrame frame, UESceneNode node, float levelBrightness, ViewBasis vb)
        {
            UEVector Cam = vb.Cam, AxX = vb.AxX, AxY = vb.AxY, AxZ = vb.AxZ;
            Dictionary<int, FogMap.Light> _records = vb.Records;
            vb.Node = node; vb.MarkOf.Clear(); vb.ByMark.Clear();
            foreach (UESprite sp in node.Sprites) if (sp.Actor is not null) vb.MarkOf[sp.Actor.ObjectId] = sp.VolLeafMark;
            // Records: what this frame's LeafVolumetricLighting created (leaf walk + static cull), as for surface fog maps.
            var vol = new HashSet<int>();
            HashSet<int> stat = _g is null ? new HashSet<int>() : LevelFogMaps.FrameStaticVolumetric(_g, node, _lightOf, -1, vol, _objectOf);   // _lightOf: this Begin's frame.Actors
            _records.Clear();
            ReadOnlySpan<ActorSnapshot> actors = frame.ActorSpan;
            for (int i = 0; i < actors.Length; i++)
            {
                ref readonly ActorSnapshot a = ref actors[i];
                if (a.Destroyed) continue;   // a bHidden light still SHINES
                LightState L = a.Light;
                bool isStatic = a.ExportIndex > 0 && stat.Contains(a.ExportIndex);
                if (L.Type == 0 || L.Brightness <= 0) continue;   // LightRadius is not read (LeafVolumetricLighting 0x10b22090..0x10b2236b reads only VolumeRadius +0x1eb)
                // A dynamic light is volumetric when this frame's walk appended its flagged LeafLights link.
                bool isDynamic = vol.Contains(a.ObjectId);
                if (!isStatic && !isDynamic) continue;
                UEVector rel = a.Location - Cam;
                float lx = Dot(rel, AxX), ly = Dot(rel, AxY), lz = Dot(rel, AxZ);
                if (L.VolumeRadius != 0 && L.VolumeBrightness != 0)
                {
                    // Colour and K from FLightInfo+0x3c (FogMap.FromSnapshot, Render.dll.c:19255-19280, :19678).
                    _records[a.ObjectId] = FogMap.FromSnapshot(in a, lx, ly, lz, levelBrightness);
                }
            }
        }

        // ApplyVertexFog for actor `actorId` drawn by frame `view` (WorldFrame.Views index, −1 = master); positions — mesh
        // vertices in the UE world. fogRgba — 4 floats per vertex: Fog.rgb and Fog.a (Fog.a only drives blending of lights
        // with each other; rgb goes into the frame — the D3D specular, see SpecularByte). Returns false — no fog (reason
        // counted); fogRgba is then zeroed. Thread-safe (counters are Interlocked; the per-mark cache is filled under a
        // lock) once Begin has run.
        public bool Apply(int actorId, ReadOnlySpan<UEVector> positions, Span<float> fogRgba, int view = -1)
        {
            fogRgba.Slice(0, positions.Length * 4).Clear();
            if (!CameraBasisValid) { Interlocked.Increment(ref SkipNoBasis); return false; }
            // A view the tick's occlusion tree does not have drew no sprite (Begin already checked the tree against the frame).
            ViewBasis vb = view < 0 ? _master : view < _views.Count ? _views[view] : null;
            if (vb is null) { Interlocked.Increment(ref SkipNotInZone); return false; }
            if (!vb.MarkOf.TryGetValue(actorId, out int mark) || mark < 0) { Interlocked.Increment(ref SkipNotInZone); return false; }
            (HashSet<int> Static, HashSet<int> Dynamic) sel;
            lock (vb.ByMark)
                if (!vb.ByMark.TryGetValue(mark, out sel))
                {
                    var dyn = new HashSet<int>();
                    var st = _g is null ? new HashSet<int>() : LevelFogMaps.FrameStaticVolumetric(_g, vb.Node, _lightOf, mark, dyn, _objectOf);
                    vb.ByMark[mark] = sel = (st, dyn);
                }
            var lights = new List<FogMap.Light>();
            foreach (var kv in vb.Records)
                if (sel.Dynamic.Contains(kv.Key) || (_exportOf.TryGetValue(kv.Key, out int ex) && sel.Static.Contains(ex))) lights.Add(kv.Value);
            if (lights.Count == 0) { Interlocked.Increment(ref SkipNoVolumetric); return false; }
            FogMap.SortFarFirst(lights);
            int n = positions.Length;
            if (n == 0) return false;   // an empty mesh counts as neither fogged nor skipped
            UEVector cam = vb.Cam, ax = vb.AxX, ay = vb.AxY, az = vb.AxZ;
            int outside = 0;   // per actor (shipped: global DAT_10b658d8)
            for (int v = 0; v < n; v++)
            {
                UEVector ue = positions[v];
                float rx = ue.x - cam.x, ry = ue.y - cam.y, rz = ue.z - cam.z;
                float tx = rx * ax.x + ry * ax.y + rz * ax.z;
                float ty = rx * ay.x + ry * ay.y + rz * ay.z;
                float tz = rx * az.x + ry * az.y + rz * az.z;
                float fr = 0f, fg = 0f, fb = 0f, fa = 0f;
                FogMap.VertexFog(lights, tx, ty, tz, ref outside, ref fr, ref fg, ref fb, ref fa);
                fogRgba[v * 4] = fr; fogRgba[v * 4 + 1] = fg; fogRgba[v * 4 + 2] = fb; fogRgba[v * 4 + 3] = fa;
            }
            Interlocked.Add(ref FoggedVertices, n);
            Interlocked.Increment(ref FoggedActors);
            return true;
        }

        // Vertex specular byte of D3DDrv DrawTriangles: ROUND(Fog·255) (to even), alpha 0xff. The shader
        // adds byte/255 in gamma space AFTER the texture is modulated by light.
        public static byte SpecularByte(float f) => (byte)(int)MathF.Round(f * 255f, MidpointRounding.ToEven);
    }
}
