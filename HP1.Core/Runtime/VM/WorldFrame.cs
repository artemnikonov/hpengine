// Typed world-state snapshot (plan stage 8, slice 1): a READ-ONLY projection
// of the Core's live actor state that a frontend (the Unity visual proxy)
// consumes to display the world — it never writes back (PLAN_core: the
// frontend is a replaceable visual proxy, gameplay truth stays in the Core).
// The snapshot is built directly from UEObject state, NOT by parsing the
// journal — the state digest is one TEXT rendering of these same snapshots
// (see HeadlessSmoke.EmitStateDigest), so the two can never drift.
//
// SCOPE (review #7): this is the GAMEPLAY projection — identity (stable
// ObjectId + base/owner ids), transform, motion, physics, region, state
// frame, timers, animation, touch. The full PLAN_core stage-8 frontend
// contract additionally needs: render identity (mesh/asset path, DrawScale,
// PrePivot, MultiSkins/material, DrawType, hidden/style/PolyFlags); Camera;
// movers/lights/particles/decals; and the audio/HUD command stream. Those
// land as later slices — stage 8 is NOT closed by this file alone.
using System.Collections.Generic;
using System.Linq;

namespace HP1.UEScript
{
    // Light state (stage-8 slice 4). A light actor drives dynamic/static
    // lighting from these — LightType LT_None (0) means "not a light". Type/
    // Effect are the ELightType/ELightEffect enums; WorldRadius is the SHIPPED
    // AActor::WorldLightRadius, max(DrawScale,1)*(LightRadius+1)*25 (proven
    // from the Engine.dll body — see WorldFrameProjector.WorldLightRadius; SE
    // UActor.h:516 omits the scale factor and loses to shipped). All bytes as
    // UE1 stores them.
    public readonly struct LightState
    {
        public readonly int Type;        // ELightType (0 = LT_None)
        public readonly int Effect;      // ELightEffect
        public readonly int Brightness;
        public readonly int Hue;
        public readonly int Saturation;
        public readonly int Radius;      // raw LightRadius byte
        public readonly float WorldRadius;
        // KnowWonder addition (review #10 item 4): the inner-radius plateau of
        // the falloff — authored on 316 Lev_Tut1 lights, consumed by the
        // frontend light sources (HP1DynamicLight/HP1Lighting/HP1LightSource).
        public readonly int RadiusInner;
        public readonly int Cone;        // spot outer cone: cos = 1 - Cone/256 (shipped spot filler, SurfLightmap.cs coneC)
        public readonly int Phase;       // pulse/flicker phase offset
        public readonly int Period;
        public readonly int VolumeBrightness;
        public readonly bool Corona;
        public readonly bool DynamicLight;
        public readonly bool SpecialLit;
        // ELightSource (KnowWonder, byte at AActor+0x1f4). NOT decoration: the renderer itself picks the
        // branch, and the branch changes mesh lighting.
        //
        //   Render.dll.c:16887   if (*(char *)(*piVar11 + 500) == '\x02') {
        //                            ... local_18 = 1.0;          // Lambert
        //                            ...                          // factor
        //                        }                                // FORCED to 1
        //   Render.dll.c:16900   else if (*(char *)(*piVar11 + 500) == '\x01') {
        //
        // 500 = 0x1f4, single-byte compare. 2 (LD_Ambient) is a fill: falloff is radial only, the
        // incidence angle is ignored. Tut1 has 17 such lights, indistinguishable from ordinary ones
        // without this byte, so a light registry built FROM THE FRAME needs it.
        public readonly int Source;
        // Per-actor light selection reads two more light bits (Render.dll.c:21399-21437):
        //   bStatic (light+0x28 bit 0x1) - the CAP branch: a non-static light is skipped
        //     when total>2, a static one when static>2;
        //   bDarkLight (light+0x1f0 bit 0x10) - final colour x (-1.0)
        //     (_DAT_10b386a4 from .rdata), subtractive light.
        public readonly bool Static;
        // bNoDelete - the SECOND half of the static/dynamic split.
        //
        // URender::SetupDynamics distributes a light over BSP leaves only if the following does NOT hold
        // `(bStatic || bNoDelete) && !bDynamicLight`
        // (Render.dll.c:11183-11185). bStatic alone is not enough for this condition, and substituting it
        // would be a guess: the campaign has a class with bNoDelete but no bStatic (Engine.LiftCenter), and
        // maps override the flag per actor.
        public readonly bool NoDelete;
        public readonly bool Dark;
        // bMovable (light+0x198 bit 0x20000; prop_offsets via --dumpprops): a NON-static light with this
        // bit is accepted by per-actor selection WITHOUT a visibility trace (disasm Render.dll 0x10b0a21f-0x10b0a22d:
        // `test [light+0x198], 0x20000; jne 0x10b0a349`). Campaign carriers: spell projectiles
        // (baseSpell and subclasses: LightType 1, bStatic False, bMovable True) and Hub4 FilchLantern.
        public readonly bool Movable;
        // PER-FRAME LIGHT-TYPE FACTOR (C3): the shipped FLightInfo dispatcher (Render.dll.c:19085,
        // table 0x10b38288 indexed by LightType) adjusts brightness before the [0,1] clamp - Pulse/SubtlePulse
        // by a sine of Level.TimeSeconds*35*65536/LightPeriod + LightPhase*256, Blink/Strobe zero it,
        // Flicker uses the frame's random table. Computed in Core (WorldFrameProjector.LightAnim); the
        // frontend multiplies.
        public readonly float Scale;
        // ZONE FOG (C4): VolumeRadius - volume radius (VolumeRadius+1)*25
        // (Render.dll.c:479 ComputeFromActor; LeafVolumetricLighting :4584),
        // VolumeFog - fog opacity, ramp alpha = VolumeFog/255
        // (:486 _DAT_10b38694). The frontend cannot build a fog map without them.
        public readonly int VolumeRadius;
        public readonly int VolumeFog;
        // LIGHT DIRECTION (D-68): AActor::GetViewRotation (Engine.dll.c:103977-104001: IsA(APawn) ? ViewRotation +0x33c
        // : Rotation +0x108). The spot filler FUN_10b05710 calls it (Render.dll.c:18492), and ComputeFromActor's
        // LightSource 1 branch inlines it (IsA [0x10b74734] = APawn::PrivateStaticClass, disasm 0x10b06e8e..0x10b06ea8).
        public readonly UERotator ViewRotation;
        public LightState(int type, int effect, int brightness, int hue, int saturation,
            int radius, float worldRadius, int radiusInner, int cone, int phase,
            int period, int volumeBrightness,
            bool corona, bool dynamicLight, bool specialLit, int source,
            bool bStatic, bool dark, bool noDelete, float scale = 1f,
            int volumeRadius = 0, int volumeFog = 0, bool movable = false, UERotator viewRotation = default)
        {
            VolumeRadius = volumeRadius; VolumeFog = volumeFog; Movable = movable; ViewRotation = viewRotation;
            Type = type; Effect = effect; Brightness = brightness; Hue = hue;
            Saturation = saturation; Radius = radius; WorldRadius = worldRadius;
            RadiusInner = radiusInner; Cone = cone; Phase = phase;
            Period = period; VolumeBrightness = volumeBrightness; Corona = corona;
            DynamicLight = dynamicLight; SpecialLit = specialLit; Source = source;
            Static = bStatic; Dark = dark; NoDelete = noDelete; Scale = scale;
        }
    }

    // Mover keyframe state (stage-8 slice 4). The mover's pose is already in
    // Location/Rotation; this is the interpolation state the proxy needs to
    // drive/sync a door's motion: which key it targets, how far along, and
    // whether it is moving. NumKeys 0 means "not a mover".
    public readonly struct MoverState
    {
        public readonly int KeyNum;
        public readonly int PrevKeyNum;
        public readonly int NumKeys;
        public readonly float PhysAlpha;
        public readonly bool Interpolating;
        // Brush model identity (review #10 item 5): the mover's own Model
        // export in the map package — the proxy binds its imported brush
        // geometry by this export index (the same identity collision uses,
        // HeadlessSmoke.BrushModelOf). -1 = the actor has no brush.
        public readonly int BrushExportIndex;
        public readonly string BrushName;
        // ABrush::ToWorld of this tick (UELevelRuntime.BrushToWorld: Location, Rotation, PrePivot, MainScale, PostScale):
        // the frontend places the brush polygons with it instead of reading the live actor. Zero when the actor has no brush.
        public readonly BrushCoords ToWorld;
        // Mover.bDynamicLightMover: mode-3 lighting (Core MoverMode3Lightmaps), not the zone layer.
        public readonly bool DynamicLightMover;
        public MoverState(int keyNum, int prevKeyNum, int numKeys, float physAlpha,
            bool interpolating, int brushExportIndex, string brushName, BrushCoords toWorld = default, bool dynamicLightMover = false)
        {
            KeyNum = keyNum; PrevKeyNum = prevKeyNum; NumKeys = numKeys;
            PhysAlpha = physAlpha; Interpolating = interpolating;
            BrushExportIndex = brushExportIndex; BrushName = brushName;
            ToWorld = toWorld; DynamicLightMover = dynamicLightMover;
        }
    }

    // One BSP surface - the whole FBspSurf (ULevel.cpp:280-296), in serialization order. References
    // are RAW (indices into Points/Vectors/LightMaps): the consumer needs the indices themselves to build
    // triangles and UV, so they are not resolved here.
    //
    // All fields are proven by a byte diff against two independent readers over all 41 maps.
    public readonly struct StaticSurface
    {
        public readonly string MaterialPath;  // full path Pkg.Group...Leaf, "None" if absent
        public readonly int PolyFlags;
        public readonly int PBase;            // index into Points - texture system origin
        public readonly int VNormal;          // index into Vectors - surface normal
        public readonly int VTextureU;        // index into Vectors - U axis
        public readonly int VTextureV;        // index into Vectors - V axis
        public readonly int LightMap;         // index into LightMaps, -1/0 if none
        public readonly int BrushPoly;
        public readonly int PanU;             // serialized int16
        public readonly int PanV;
        public readonly int BrushActor;       // polygon ownership by a mover brush
        public StaticSurface(string materialPath, int polyFlags, int pBase, int vNormal,
            int vTextureU, int vTextureV, int lightMap, int brushPoly, int panU, int panV,
            int brushActor)
        {
            MaterialPath = materialPath; PolyFlags = polyFlags; PBase = pBase;
            VNormal = vNormal; VTextureU = vTextureU; VTextureV = vTextureV;
            LightMap = lightMap; BrushPoly = brushPoly; PanU = panU; PanV = panV;
            BrushActor = brushActor;
        }
    }

    // A BSP node as a UNIT OF RENDER GEOMETRY: the polygon is a fan over NumVertices consecutive
    // vertex-pool entries starting at VertPool. Tree branching (Front/Back/Leaf) is NOT included:
    // tree traversal belongs to Core; the frontend only needs polygon topology and its surface.
    public readonly struct StaticNode
    {
        public readonly int Surf;
        public readonly int VertPool;
        public readonly int NumVertices;
        public readonly int NodeFlags;
        public readonly UEVector PlaneNormal;
        public readonly float PlaneDist;
        // FBspNode.iZone[0] (back side) and iZone[1] (front side) - the zones on either side of the node.
        // Used by the sky zone: shipped takes the zone of a PF_FakeBackdrop polygon and decides via its
        // ZoneActor->SkyZone which sky to draw (Render.dll.c:5994-6016, `Model->Zones[zone].ZoneActor`).
        public readonly int Zone0;
        public readonly int Zone1;
        // FBspNode.iPlane (+0x28) - the next COPLANAR node, -1 ends the chain.
        // OccludeBsp draws the node and then its chain via this field through a shared span buffer
        // (Render.dll.c:6735-6750): an earlier node of the chain occludes a later one (CoplanarOcclusion).
        public readonly int CoplanarNext;
        public StaticNode(int surf, int vertPool, int numVertices, int nodeFlags,
            UEVector planeNormal, float planeDist, int zone0 = 0, int zone1 = 0, int coplanarNext = -1)
        {
            Surf = surf; VertPool = vertPool; NumVertices = numVertices;
            NodeFlags = nodeFlags; PlaneNormal = planeNormal; PlaneDist = planeDist;
            Zone0 = zone0; Zone1 = zone1; CoplanarNext = coplanarNext;
        }
    }

    // FLightMapIndex (ULevel.cpp:325-343) - serialized pan/scale/clamp.
    // A surface lightmap cannot be addressed without them.
    public readonly struct StaticLightMap
    {
        public readonly int DataOffset;
        public readonly UEVector Pan;
        public readonly int UClamp;
        public readonly int VClamp;
        public readonly float UScale;
        public readonly float VScale;
        public readonly int LightActors;
        public StaticLightMap(int dataOffset, UEVector pan, int uClamp, int vClamp,
            float uScale, float vScale, int lightActors)
        {
            DataOffset = dataOffset; Pan = pan; UClamp = uClamp; VClamp = vClamp;
            UScale = uScale; VScale = vScale; LightActors = lightActors;
        }
    }

    // The level's entire static render geometry.
    //
    // SOLE PRODUCER - the already loaded Core model (HeadlessBspModel.BuildStaticGeometry). The `.unr`
    // is NOT read again: two readers of the same bytes are two owners that drift apart silently.
    public sealed class StaticGeometry
    {
        public readonly StaticSurface[] Surfaces;
        public readonly StaticNode[] Nodes;
        public readonly UEVector[] Points;
        public readonly UEVector[] Vectors;
        public readonly int[] VertPoolPoints;   // FVert.Vertex - index into Points
        public readonly int[] VertPoolSides;    // FVert.Side
        public readonly StaticLightMap[] LightMaps;
        // -- BSP LEAF LIGHT CHAIN --
        //
        // Shipped picks light candidates for an ACTOR not by distance but by the leaf the actor stands in
        // (Render.dll.c:21286-21300):
        //     iPermeating = Model->Leaves(Owner->Region.iLeaf).iPermeating
        //     for (n = 0; Model->Lights(iPermeating + n); ++n) candidate
        // The sublist is null-terminated and the pool is built by the lighting baker, so occlusion is
        // already accounted for. Radius selection does NOT replace it: a light can be in radius yet absent
        // from the leaf sublist (Tut1: 299 of 385 actors have such lights).
        //
        // Indices are kept RAW, as serialized; the frontend matches a light to a pool entry via
        // ActorSnapshot.ExportIndex.
        public readonly int[] LeafPermeating;   // per leaf: sublist start (-1 = none)
        public readonly int[] LightExports;     // Model->Lights pool, 0 = sublist end
        // UModel.Zones(i).ZoneActor - RAW export indices of zone actors (0 = the zone has no ZoneInfo,
        // shipped then uses Actors(0)). The frontend matches them with ActorSnapshot.ExportIndex to learn
        // which zones belong to a SkyZoneInfo: their geometry is drawn by the sky camera.
        public readonly int[] ZoneActorExports;
        // UModel.Leaves(i).iVolumetric - start of the leaf's null-terminated sublist of VOLUMETRIC lights
        // in LightExports; shipped reads it in URender::LeafVolumetricLighting (Render.dll.c:4535-4640)
        // for surface fog maps and vertex fog (C4).
        public readonly int[] LeafVolumetric;
        public StaticGeometry(StaticSurface[] surfaces, StaticNode[] nodes, UEVector[] points,
            UEVector[] vectors, int[] vertPoolPoints, int[] vertPoolSides,
            StaticLightMap[] lightMaps, int[] leafPermeating, int[] lightExports,
            int[] zoneActorExports = null, int[] leafVolumetric = null)
        {
            LeafVolumetric = leafVolumetric ?? System.Array.Empty<int>();
            Surfaces = surfaces; Nodes = nodes; Points = points; Vectors = vectors;
            VertPoolPoints = vertPoolPoints; VertPoolSides = vertPoolSides;
            LightMaps = lightMaps;
            LeafPermeating = leafPermeating; LightExports = lightExports;
            ZoneActorExports = zoneActorExports ?? System.Array.Empty<int>();
        }
    }

    // The static world contract (review #10 item 5): what the proxy binds
    // ONCE, not per frame — the map package (level BSP geometry import) that
    // mover BrushExportIndex values resolve into.
    public sealed class WorldStatic
    {
        public readonly string MapPath;
        // Static render geometry. null when the level model is not loaded (e.g. in the contract dump,
        // which builds WorldStatic from a path only): these values are pinned not by the dump SHA but by
        // the campaign oracle diff - the [bsp-oracle] gate step.
        public readonly StaticGeometry Geometry;
        public WorldStatic(string mapPath) : this(mapPath, null) { }
        public WorldStatic(string mapPath, StaticGeometry geometry)
        { MapPath = mapPath; Geometry = geometry; }
    }

    // Ambient sound state (review #10 item 2). The ORIGINAL ambient loop runs
    // in the audio subsystem, not in gameplay: each update it STARTS the
    // ambient of any actor whose WorldSoundRadius reaches the view actor and
    // STOPS a playing one that went out of range / changed sound / whose actor
    // died (UGalaxyAudioSubsystem::Update, Galaxy 0x106082fb..0x10608679; Core decides it in
    // UEWorldFrameProducer.AmbientPass / UEGalaxyVoiceMix — start volume SoundVolume·AmbientFactor/255, the
    // update volume doubled and light-modulated). The frame carries the LIVE raw props (scripts reassign
    // AmbientSound at runtime). SoundPath null = silent.
    public readonly struct AmbientState
    {
        public readonly string SoundPath;
        public readonly int SoundRadius;     // raw byte
        public readonly float WorldRadius;   // (SoundRadius+1)*25 (Galaxy StartAmbience 0x10608434, UEGalaxyVoiceMix.AmbientRadius)
        public readonly int SoundVolume;     // raw byte
        public readonly int SoundPitch;      // raw byte
        public AmbientState(string soundPath, int soundRadius, float worldRadius,
            int soundVolume, int soundPitch)
        {
            SoundPath = soundPath; SoundRadius = soundRadius; WorldRadius = worldRadius;
            SoundVolume = soundVolume; SoundPitch = soundPitch;
        }
    }

    // Music state (review #10 item 2), read from the local player pawn — the
    // original audio subsystem reads the viewport actor's Song/Transition
    // (Galaxy UpdateMusic start block, Galaxy.dll.c:7385-7451; ClientSetMusic writes these props).
    // Valid=false when there is no local player.
    public readonly struct MusicState
    {
        public readonly bool Valid;
        public readonly string SongPath;     // Music package ref, null = none
        public readonly int SongSection;
        public readonly int CdTrack;
        public readonly int Transition;      // EMusicTransition
        public readonly bool DontLoop;
        public readonly int PercentVolume;
        public MusicState(bool valid, string songPath, int songSection, int cdTrack, int transition,
            bool dontLoop, int percentVolume)
        {
            Valid = valid; SongPath = songPath; SongSection = songSection;
            CdTrack = cdTrack; Transition = transition; DontLoop = dontLoop;
            PercentVolume = percentVolume;
        }
    }

    // LISTENER ZONE REVERB - the structure Galaxy UpdateReverb (FUN_106095c4, tail of
    // UGalaxyAudioSubsystem::Update, 0x106095c4-0x1060976d) builds every Update and passes to
    // glxSetSampleReverb (FUN_1060ec80) only if it DIFFERS from the previous one (rep cmpsd of 27 words at this+0x70f).
    //   gate: UseReverb (this+0x44, config - checked by the consumer) && Zone != NULL
    //         && (Zone+0x2f8 & 1) bReverbZone; otherwise zeros (= reverb removed:
    //         FUN_1060ec80 drops the callback when MasterGain == 0.0);
    //   Zone - (Viewport->Actor->ViewTarget ?: Viewport->Actor) + 0xa0 = Actor.Region.Zone
    //         ([ebp-0x40], 0x106082fb-0x10608310);
    //   MasterGain = byte(+0x300) * (double)1/255 (0x1063a400) -> float;
    //   CutoffHz   = clamp(int(+0x304), 0, 0xac44) -> float;
    //   Delay[i]   = byte(+0x308+i) * 0.002f (0x1063a41c), clamp [0.001f, 0.34f] (0x1063a418/0x1063a414);
    //   Gain[i]    = byte(+0x30e+i) * (float)1/255 (0x1063a410), clamp [0.001f, 0.999f] (0x1063a418/0x1063a40c).
    // The clamps compare the EXACT x87 product (fcom before fstp dword), so the comparison here is in
    // double (8 bit x 24/53 bit product is exact) with a single rounding to float. (For byte inputs no
    // byte was found where clamping before/after rounding differs; the source form is kept anyway.)
    // Field offsets checked with --dumpprops ZoneInfo (audit/prop_offsets.py: bReverbZone 0x2f8/0x1,
    // MasterGain 0x300, CutoffHz 0x304, Delay[6] 0x308, Gain[6] 0x30e).
    // Equality is bitwise over all fields (like rep cmpsd); Valid is not one of the Galaxy words and
    // does not take part in the comparison.
    public readonly struct ReverbState
    {
        public readonly bool Valid;          // bReverbZone gate passed
        public readonly float MasterGain, CutoffHz;
        // Fields are public so the frame shape describes the whole words (WorldContract, version 7).
        public readonly float Delay0, Delay1, Delay2, Delay3, Delay4, Delay5, Gain0, Gain1, Gain2, Gain3, Gain4, Gain5;
        public float Delay(int i) => i switch { 0 => Delay0, 1 => Delay1, 2 => Delay2, 3 => Delay3, 4 => Delay4, 5 => Delay5, _ => throw new System.ArgumentOutOfRangeException(nameof(i)) };
        public float Gain(int i) => i switch { 0 => Gain0, 1 => Gain1, 2 => Gain2, 3 => Gain3, 4 => Gain4, 5 => Gain5, _ => throw new System.ArgumentOutOfRangeException(nameof(i)) };

        ReverbState(float mg, float fc, float[] d, float[] g)
        {
            Valid = true; MasterGain = mg; CutoffHz = fc;
            Delay0 = d[0]; Delay1 = d[1]; Delay2 = d[2]; Delay3 = d[3]; Delay4 = d[4]; Delay5 = d[5];
            Gain0 = g[0]; Gain1 = g[1]; Gain2 = g[2]; Gain3 = g[3]; Gain4 = g[4]; Gain5 = g[5];
        }

        // Loop body of UpdateReverb 0x10609619-0x10609707 over the zone bytes.
        public static ReverbState FromZoneBytes(byte masterGain, int cutoffHz, System.ReadOnlySpan<byte> delay, System.ReadOnlySpan<byte> gain)
        {
            if (delay.Length != 6 || gain.Length != 6)
                throw new System.ArgumentException("zone reverb: exactly 6 delays and 6 gains are required");
            var d = new float[6]; var g = new float[6];
            for (int i = 0; i < 6; i++)
            {
                d[i] = Clamp(delay[i] * (double)0.002f, 0.001f, 0.34f);
                g[i] = Clamp(gain[i] * (double)(1f / 255f), 0.001f, 0.999f);
            }
            // byte*(double)1/255 has 61 significant bits, double would round twice; exhaustive check over
            // all 256 bytes: (float)(b*(1.0/255)) equals the single rounding of the exact product (FromZoneBytes
            // check in SelfTest).
            return new ReverbState((float)(masterGain * (1.0 / 255)),
                (float)(cutoffHz < 0 ? 0 : cutoffHz >= 0xac44 ? 0xac44 : cutoffHz), d, g);
        }

        // fcom [min]; C0 -> min; fcom [max]; !C0 -> max (0x10609680-0x106096a6).
        static float Clamp(double x, float min, float max) => x < min ? min : x >= max ? max : (float)x;

        public bool SameWords(in ReverbState o)
        {
            static bool Eq(float a, float b) => System.BitConverter.SingleToInt32Bits(a) == System.BitConverter.SingleToInt32Bits(b);
            return Eq(MasterGain, o.MasterGain) && Eq(CutoffHz, o.CutoffHz)
                && Eq(Delay0, o.Delay0) && Eq(Delay1, o.Delay1) && Eq(Delay2, o.Delay2) && Eq(Delay3, o.Delay3) && Eq(Delay4, o.Delay4) && Eq(Delay5, o.Delay5)
                && Eq(Gain0, o.Gain0) && Eq(Gain1, o.Gain1) && Eq(Gain2, o.Gain2) && Eq(Gain3, o.Gain3) && Eq(Gain4, o.Gain4) && Eq(Gain5, o.Gain5);
        }
    }

    // A UE1 range property — the {Base, Rand} struct every ParticleFX
    // parameter uses (the native samples Base + FRand()*Rand per particle,
    // Engine.dll UnParticleFX.cpp GetParams).
    public readonly struct FloatRange
    {
        public readonly float Base;
        public readonly float Rand;
        public FloatRange(float @base, float rand) { Base = @base; Rand = rand; }
    }

    // Live emitter parameters: the ORIGINAL scripts mutate them at runtime (baseProps.uc:86 ParticlesPerSec/Lifetime,
    // TargetGlow.uc:7, Doxie1.uc:30), so the frame carries LIVE values, not class defaults. Valid=false for a
    // non-ParticleFX actor. Emission itself is simulated in Core (UEWorldFrameProducer) from the actor.
    // The emitter fields a consumer reads: Valid and RenderPrimitive (openhp1 ParticleBuilder/SdlActors),
    // TexturePaths (headless render ledger), ParticlesPerSec and Lifetime (selftest: the projection is LIVE, not the
    // class default). Every other AParticleFX property stays on the actor — nothing read it from the frame.
    public readonly struct ParticleState
    {
        public readonly bool Valid;
        public readonly FloatRange ParticlesPerSec;
        public readonly FloatRange Lifetime;
        public readonly int RenderPrimitive;
        public readonly IReadOnlyList<string> TexturePaths;

        public ParticleState(bool valid, FloatRange particlesPerSec, FloatRange lifetime, int renderPrimitive,
            IReadOnlyList<string> texturePaths)
        {
            Valid = valid; ParticlesPerSec = particlesPerSec; Lifetime = lifetime;
            RenderPrimitive = renderPrimitive; TexturePaths = texturePaths;
        }
    }

    // One actor's projected state. Value-typed (Core types only — no frontend
    // dependency); the frontend maps these onto its own scene objects.
    public readonly struct ActorSnapshot
    {
        // Stable identity (UEObject.ObjectId) — the proxy keys its scene object
        // on this, not on Name (names are reused by spawns). Base/Owner are
        // given as ids too (-1 when none) so the frontend can wire the actor
        // graph without name lookups.
        public readonly int ObjectId;
        // ACTOR IDENTITY IN THE MAP PACKAGE (0 for a spawned actor).
        //
        // Not for debugging: the BSP leaf light pool (StaticGeometry.LightExports) stores EXPORT indices as
        // serialized, and nothing else can match a frame light to a pool entry. ObjectId is runtime-only and
        // does not match the export (Tut1: 399 of 400 actors differ).
        public readonly int ExportIndex;
        public readonly int BaseId;
        public readonly int OwnerId;
        public readonly string Name;
        public readonly string Class;
        // PLACED level actor or RUNTIME SPAWN. Not a label: a placed actor's proxy is built by the scene
        // bake (WorldStatic data), a runtime spawn's by the proxy registry from this very frame. Derived
        // from ExportIndex, which exactly the actors that came from a .unr export have and no Spawn has.
        public readonly bool Placed;
        public readonly UEVector Location;
        public readonly UERotator Rotation;
        public readonly UEVector Velocity;
        public readonly UEVector Acceleration;
        public readonly int Physics;
        public readonly int ZoneNumber;
        public readonly int Leaf;
        // AZoneInfo.bFogZone (bit 0x2 of word +0x2cc): a surface fog map is built only in such a zone
        // (Render.dll.c:19936 SetupForSurf), a dynamic light is volumetric only from such a zone (:11227
        // SetupDynamics). Always false for non-ZoneInfo actors.
        public readonly bool FogZone;
        // AZoneInfo.TexUPanSpeed/TexVPanSpeed (+0x2e0/+0x2e4): clock multiplier for PF_AutoUPan/PF_AutoVPan
        // surfaces of this zone (URender::DrawFrame, Render.dll 0x10b2594e/0x10b25a0d; formula in Core
        // SurfacePan). 0 for non-ZoneInfo actors. Overridden on 7 of 41 maps (values 8, 4, 3.5, 2.5, 0.5,
        // 0.3, 0.1, -0.1, -0.5).
        public readonly float TexUPanSpeed;
        public readonly float TexVPanSpeed;
        public readonly string Base;        // base actor name, or "-"
        public readonly string Owner;       // owner actor name, or "-"
        public readonly string State;       // current state-frame function, or "-"
        public readonly string Latent;      // latent-run state name, or "-"
        public readonly float MoveTimer;
        public readonly string AnimSequence;
        public readonly float AnimFrame;
        public readonly float AnimRate;
        // Tween step: TweenRate = 1/tweenTime (the shipped PlayAnim family writes `this+0x5c = 1.0/tweenTime`,
        // Engine.dll.c:73800). A declared actor property (0x5c - right after AnimRate, before TweenAlpha), so
        // the projection here is MECHANICAL, exactly like AnimFrame/AnimRate.
        //
        // Why separate from TweenAlpha: Alpha is the already computed transition fraction, Rate is its
        // DURATION. The frontend cross-fade needs the duration; reading it live via GetProp would return
        // state from a DIFFERENT tick than the rest of the snapshot. Replacing it with TweenAlpha would not
        // be a rename but a different blend model, with its own source and proof; not done here.
        public readonly float TweenRate;
        // CORE DRIVES THIS ACTOR'S ROOT. Exactly the condition under which `UELevelRuntime` calls
        // `TickRootMovement` (UELevelRuntime.cs:606-615): authority role, `bAnimMove` and a mesh. Core then
        // converts ALL root translation into actor movement (the remainder also goes back into the actor
        // delta, RootMotion.cs:133-150), so the pose the frontend draws must come WITHOUT root offset -
        // otherwise the translation is applied twice.
        //
        // The flag travels in the frame instead of being derived from the class, so every `bAnimMove`
        // carrier (Harry, cauldrons, logs, boulders) is handled the same way.
        public readonly bool RootMotionDriven;
        // Shipped tween model (review #11 item 4): a new sequence starts at
        // AnimFrame=0 while TweenAlpha (actor +0x60) ramps the CACHED previous
        // pose out separately — the frontend blends its own last-rendered pose
        // against the new frame with weight 1-TweenAlpha. Without this the
        // skeletal transition snaps.
        public readonly float TweenAlpha;
        // ANIMATION CHANNELS (AnimChannel route). Shipped draws the owner as: base ApplyAnim, then each
        // AActor.AuxAnims (+0x6c) entry is applied over its bone's subtree (USkeletalMesh::GetFrame,
        // Engine.dll.c:142947-142951). `AuxAnims` - channel ObjectIds IN ARRAY ORDER; a channel is an
        // ordinary frame actor, its subtree is given by `AnimBone` (+0x64, RefSkeleton bone index), its
        // animation by its own AnimSequence/AnimFrame/TweenRate/TweenAlpha.
        public readonly int AnimBone;
        public readonly IReadOnlyList<int> AuxAnims;
        public readonly float TimerRate;
        public readonly float TimerCounter;
        public readonly float LifeSpan;
        public readonly IReadOnlyList<string> Touching;
        public readonly bool Destroyed;

        // ── Render identity (stage-8 slice 2) ──────────────────────────────
        // What the proxy needs to build the actor's visual: the full UE asset
        // path (path-first identity — the proxy resolves it through the mesh/
        // texture registries), transform modifiers and the render-flag state.
        public readonly int DrawType;        // EDrawType: 1=Sprite 2=Mesh 3=Brush ...
        public readonly string MeshPath;     // Mesh ref full UE path (DT_Mesh), or null
        public readonly string TexturePath;  // Actor.Texture full UE path (DT_Sprite), or null
        public readonly float DrawScale;
        public readonly int Style;           // ERenderStyle: 1 Normal, 2 Masked, 3 Translucent, 4 Modulated
        public readonly bool Hidden;
        public readonly bool Unlit;
        // Actor.bNoSmooth (+0x198 bit 0x2): FUN_10b338f0 sets PF_NoSmooth (0x800, Render.dll.c:39787-39789) on sprites and
        // meshes; D3DDrv SetBlending with 0x800 uses POINT MAG/MIN filtering (D3DDrv.dll.c:860-865, stage 0, 0x10/0x11 = 1).
        // Particles do not get the actor flag (FUN_10b15090 collects only style and texture flags).
        public readonly bool NoSmooth;
        public readonly bool MeshEnviroMap;
        // Empty mesh slot texture after Actor.Texture: Region.Zone.EnvironmentMap (+0xa0 -> +0x2dc), then
        // Level.EnvironmentMap (+0x7c -> +0x2dc) — URender::DrawLodMesh/DrawMesh, disassembly 0x10b11811-0x10b1183f.
        public readonly string ZoneEnvironmentMap, LevelEnvironmentMap;
        public readonly int AmbientGlow;
        public readonly float ScaleGlow;
        // Actor.Fatness (byte, default 128): Render.dll moves every mesh vertex along its normal by
        // Fatness/16 - 8 units (Render.dll.c:9328, 14851; 0.0625 and 8.0 - .rdata 0x10b387c0/0x10b38644).
        public readonly int Fatness;
        // Actor.SpriteProjForward (+0x190, Actor default 32): sprite depth for the z-test.
        // Render.dll FUN_10b30c70 (Render.dll.c:38114-38118, DT_Sprite branch):
        // Z = (Location - ViewOrigin)*ViewDir - SpriteProjForward, size from the projection of the real
        // Location; DrawActorSprite passes this Z to DrawIcon (disasm 0x10b32be0, [edi+8]), D3DDrv DrawTile
        // uses one rhw = 1/Z for the whole tile (D3DDrv.dll.c:5305).
        public readonly float SpriteProjForward;
        // Actor.Opacity (KnowWonder, default 1): URender::DrawLodMesh with Opacity < 1.0 adds flags
        // 0x10000004 and writes vertex alpha = Opacity (Render.dll.c:14568, 14874); D3DDrv blends 0x10000000
        // as SRCALPHA/INVSRCALPHA (D3DDrv.dll.c:822). Carriers: InvisibleHarry (0.15), ghosts NHNick (0.4),
        // BloodyBaron (0.3), Peeves fades.
        public readonly float Opacity;
        // URender::DrawFrame pass of this actor's sprite on a non-SpanBased device (D3DDrv): true = pass 2 (drawn after the
        // pass-2 surfaces), Core's UESpriteSort.Pass — the frontend groups the actor's draws by it and decides nothing.
        public readonly bool LatePass;
        // Mesh specular (Render.dll.c:21173-21178): C = (1 - SpecularWidth*1.5/255)^2,
        // K = 2*SpecularGlow/(1-C)^2. Maps set them on candles, benches, chests.
        public readonly float SpecularGlow;
        public readonly int SpecularWidth;
        public readonly string Skin;                    // single Skin texture path, or null
        public readonly IReadOnlyList<string> MultiSkins;  // per-slot texture paths

        public readonly LightState Light;
        public readonly MoverState Mover;
        public readonly AmbientState Ambient;
        public readonly ParticleState Emitter;   // Valid only for ParticleFX actors
        // Particle/decal (stage-8 slice 5) add NO extra fields. Both are
        // Class-driven: the proxy instantiates its emitter/decal by Class
        // (ClassIsA ParticleFX / Decal) and reads the static emitter
        // parameters from the class defaults; a decal's projected surface is
        // its Texture (already in TexturePath) at its transform. The emitter
        // LIFECYCLE is the actor's aliveness — AParticleFX self-destroys when
        // its emission budget is spent (shipped Engine.dll UnParticleFX.cpp
        // decompile, ported in HP1ParticleBridge) — so presence in the frame
        // IS the activity signal. (Actor.bParticles exists as a UE1 property
        // but is referenced by NEITHER shipped Engine.dll nor Render.dll —
        // projecting it as an "emitting" flag would be invented semantics.)
        //
        // ActorShadow decals are LIVE, driven by bytecode. `Engine.ActorShadow` (in `Engine.u`; the
        // `extracted/Scripts/ActorShadow.uc` file is an empty import stub) has a 347-byte BYTECODE `Update`
        // that calls the name native `AttachDecal(MaxShadowDist, Vector(Owner.Rotation))`; Tut1 has 29
        // live shadows. `Update` is called not by script but by Render.dll (0x10b304be), where the
        // `ADecal::eventUpdate` wrapper is INLINED (so the call site is missing from the decompile); the
        // parameter is zero there, i.e. shipped calls `Update(None)` (read from disassembly; both the body
        // and the export thunk were scanned).
        //
        // The request is owned by Core: `UELevelRuntime.AttachDecal`. The `Update` body begins with native
        // 257 BonePos (C3); fixture `decal_attach_request_is_built_by_core_bytecode` pins this state.

        public ActorSnapshot(int objectId, int baseId, int ownerId,
            string name, string @class, bool placed, UEVector location, UERotator rotation,
            UEVector velocity, UEVector acceleration, int physics, int zoneNumber, int leaf,
            string @base, string owner, string state, string latent, float moveTimer,
            string animSequence, float animFrame, float animRate, float tweenRate,
            bool rootMotionDriven,
            float tweenAlpha, float timerRate,
            float timerCounter, float lifeSpan, IReadOnlyList<string> touching, bool destroyed,
            int drawType, string meshPath, string texturePath, float drawScale,
            int style, bool hidden, bool unlit, bool meshEnviroMap, int ambientGlow, float scaleGlow,
            string skin, IReadOnlyList<string> multiSkins,
            LightState light, MoverState mover,
            AmbientState ambient, ParticleState emitter, int exportIndex,
            bool fogZone = false,
            int animBone = 0, IReadOnlyList<int> auxAnims = null,
            float texUPanSpeed = 0f, float texVPanSpeed = 0f, int fatness = 128, float opacity = 1f,
            float specularGlow = 1f, int specularWidth = 170, float spriteProjForward = 32f, bool noSmooth = false,
            string zoneEnvironmentMap = null, string levelEnvironmentMap = null, bool latePass = false)
        {
            ZoneEnvironmentMap = zoneEnvironmentMap; LevelEnvironmentMap = levelEnvironmentMap;
            Fatness = fatness; Opacity = opacity; LatePass = latePass; SpriteProjForward = spriteProjForward; NoSmooth = noSmooth;
            SpecularGlow = specularGlow; SpecularWidth = specularWidth;
            FogZone = fogZone;
            TexUPanSpeed = texUPanSpeed; TexVPanSpeed = texVPanSpeed;
            AnimBone = animBone; AuxAnims = auxAnims ?? System.Array.Empty<int>();
            ObjectId = objectId; BaseId = baseId; OwnerId = ownerId;
            Name = name; Class = @class; Placed = placed; Location = location; Rotation = rotation;
            Velocity = velocity; Acceleration = acceleration; Physics = physics;
            ZoneNumber = zoneNumber; Leaf = leaf; Base = @base; Owner = owner;
            State = state; Latent = latent; MoveTimer = moveTimer;
            AnimSequence = animSequence; AnimFrame = animFrame; AnimRate = animRate;
            TweenRate = tweenRate; RootMotionDriven = rootMotionDriven; TweenAlpha = tweenAlpha;
            TimerRate = timerRate; TimerCounter = timerCounter; LifeSpan = lifeSpan;
            Touching = touching; Destroyed = destroyed;
            DrawType = drawType; MeshPath = meshPath; TexturePath = texturePath;
            DrawScale = drawScale;
            Style = style; Hidden = hidden;
            Unlit = unlit; MeshEnviroMap = meshEnviroMap; AmbientGlow = ambientGlow;
            ScaleGlow = scaleGlow; Skin = skin; MultiSkins = multiSkins;
            Light = light; Mover = mover; Ambient = ambient; Emitter = emitter;
            ExportIndex = exportIndex;
        }
    }

    // The viewport camera (UGameEngine::Draw, Engine 0x1039fab1..0x1039fdd2):
    // the view starts at the player pawn (location/rotation/FOV), then the
    // pawn's PlayerCalcView(out ViewActor, out Location, out Rotation) may
    // redirect it to a ViewTarget (a cut/gameplay BaseCam). Valid=false when
    // there is no local player.
    public readonly struct CameraSnapshot
    {
        public readonly bool Valid;
        public readonly int ViewActorId;   // the actor the view resolves to (player or ViewTarget)
        public readonly UEVector Location;
        public readonly UERotator Rotation;
        public readonly float FovAngle;
        // Pawn.bBehindView (+0x25c bit 0x1) OF THE VIEWPORT ACTOR. The shipped acceptance predicate
        // (`URender::SetupDynamics`, Render.dll.c:10847) does not draw the view target:
        // `PortalDepth != 0 || bBehindView || actor != ViewTarget`. The flag is read from the viewport
        // player, not the tested actor, so it lives here and not in ActorSnapshot. Without it the member
        // would hide Harry himself: the game is third person, where bBehindView is true.
        public readonly bool BehindView;
        public CameraSnapshot(bool valid, int viewActorId, UEVector location,
            UERotator rotation, float fovAngle, bool behindView)
        {
            Valid = valid; ViewActorId = viewActorId; Location = location;
            Rotation = rotation; FovAngle = fovAngle; BehindView = behindView;
        }
    }

    // One audio command emitted this frame. Unlike the actor
    // snapshots, audio is a STREAM of events, not per-frame state. Play is the
    // Audio->PlaySound call from APawn::execClientHearSound, i.e. a sound that REACHED
    // the listener via PlaySound/PlayOwnedSound -> CheckHearSound (Core,
    // UELevelRuntime.SoundNatives.cs); both natives make the same device call,
    // so there is no separate Owned kind. Stop is StopSound (568). The
    // frontend plays/stops these; the SHA-pinned journal's audio_play/audio_stop
    // lines are the TEXT rendering of these same commands
    // (UELevelRuntime.FormatAudio), so the two cannot drift. Play carries the
    // full sound params; Stop only the slot.
    // Ambient kinds (review #11 item 3, PLAN:172 — CORE decides the loop):
    // the producer's ambient pass emits Start when the view actor enters an
    // ambient actor's WorldSoundRadius, Update every frame it stays (position
    // and live volume/pitch follow the actor), Stop when it leaves / dies /
    // changes sound — the SE StartAmbience/UpdateAmbience model with the
    // decisions IN CORE; the frontend only obeys commands.
    // Modify - port of AActor::execModifySound (Engine.dll.c:72205): not a new sound but an edit of an
    // ALREADY PLAYING channel with the same identity (actor, slot); the original addresses the channel
    // the same way - (Index*8 + Slot)*2, like PlaySound.
    public enum AudioKind { Play, Stop, AmbientStart, AmbientUpdate, AmbientStop, Modify }
    public readonly struct AudioCommand
    {
        public readonly AudioKind Kind;
        // Stable channel identity (review #10): a UE1 sound channel is
        // (actor, slot) — the frontend keys running channels on ActorId so a
        // moving owner can be followed, its channel stopped, and a destroyed
        // actor never confused with a later spawn reusing the Name (the Name
        // is carried for the journal rendering only).
        public readonly int ActorId;
        public readonly string ActorName;
        public readonly string SoundPath;
        public readonly int Slot;
        public readonly float Volume;    // Play only
        public readonly float Radius;    // Play only
        public readonly float Pitch;     // Play only
        public readonly bool NoOverride; // Play only
        public readonly UEVector Location;
        // Modify only: WHICH channel parameter is edited. Values from shipped data (Engine.ESoundParam):
        // 0=SOUND_Volume, 1=SOUND_Radius, 2=SOUND_Pitch. The value itself is in the matching field above;
        // the others mean nothing for Modify.
        public readonly int Param;
        // Play and AmbientStart: the sound data loop flag (UESoundRef.Looping, FSoundData +0x54).
        public readonly bool Looping;
        // AmbientUpdate only: the start-loop volume SoundVolume·AmbientFactor/255 (Galaxy 0x10608478-0x1060848c), with
        // which Galaxy re-acquires a channel for an ambient whose voice ended (Volume is the doubled update volume).
        public readonly float StartVolume;
        public AudioCommand(AudioKind kind, int actorId, string actorName, string soundPath,
            int slot, float volume, float radius, float pitch, bool noOverride, UEVector location,
            int param = 0, bool looping = false, float startVolume = 0f)
        {
            Looping = looping; StartVolume = startVolume;
            Kind = kind; ActorId = actorId; ActorName = actorName; SoundPath = soundPath;
            Slot = slot; Volume = volume; Radius = radius; Pitch = pitch;
            NoOverride = noOverride; Location = location; Param = param;
        }
    }

    // One HUD/Canvas draw command (review #10 item 6). The Core runs the HUD
    // bytecode (PLAN_core: Canvas execs live engine-side; the frontend only
    // rasterises): PlayerPawn.PreRender lazily spawns myHUD (shipped Engine.u
    // bytecode), PlayerPawn.PostRender -> myHUD.PostRender and the console's
    // PostRender emit these through the Canvas natives (UCanvas exec 464-470).
    // Tile carries the texture + UV window; Text carries the string + font.
    // X/Y are the canvas pen (CurX/CurY + origin) at emit time; Color is the
    // canvas DrawColor RGB; Style is ERenderStyle; Smooth mirrors !bNoSmooth.
    public enum HudKind { Tile, Text }

    // One glyph shipped draws with RenDev->DrawTile from FUN_10388b60 (Engine.dll 0x10388b60): Core has
    // already done text layout (FUN_10388530), CharRemap, font page, clipping to ClipX/ClipY
    // (DrawTextClipped) and to Frame->X/Y. The frontend takes the texture of the command font's page
    // Page and draws rect X,Y,XL,YL from window U,V,UL,VL of that page. Shipped XL/YL are exactly
    // UL/VL (glyph drawn 1:1), so there is no separate scale here.
    public readonly struct HudGlyph
    {
        public readonly int Page;
        public readonly float X;
        public readonly float Y;
        public readonly float XL;
        public readonly float YL;
        public readonly float U;
        public readonly float V;
        public readonly float UL;
        public readonly float VL;
        public HudGlyph(int page, float x, float y, float xl, float yl, float u, float v, float ul, float vl)
        {
            Page = page; X = x; Y = y; XL = xl; YL = yl; U = u; V = v; UL = ul; VL = vl;
        }
    }

    public readonly struct HudCommand
    {
        public readonly HudKind Kind;
        public readonly float X;
        public readonly float Y;
        public readonly float XL;         // Tile: width; Text: measured width when known
        public readonly float YL;
        public readonly float U;          // Tile only: texture window
        public readonly float V;
        public readonly float UL;
        public readonly float VL;
        public readonly string TexturePath;  // Tile
        public readonly string Text;         // Text
        public readonly string FontPath;     // Text: page-font path OR native "name:height"
        public readonly UEVector Color;      // DrawColor RGB
        public readonly int Style;
        public readonly bool Smooth;
        public readonly bool Clipped;        // the *Clipped exec variants
        // Text with a page font: glyphs in shipped draw order (empty - all clipped). null - native font:
        // measured and drawn by the OS device (vtbl+0x98), Core has no layout for it.
        public readonly HudGlyph[] Glyphs;
        // Tile: PolyFlags shipped passes to RenDev->DrawTile from UCanvas::DrawTile (Engine 0x10387630):
        // exec flags by Style/bNoSmooth | PF_TwoSided | (Texture->PolyFlags & PF_Masked). Text: glyph flags
        // (TextPolyFlags: by Style | 0x1000802) with which FUN_10388b60 calls RenDev->DrawTile. The frontend
        // takes the blend mode FROM HERE, not from Style.
        public readonly int PolyFlags;
        public HudCommand(HudKind kind, float x, float y, float xl, float yl,
            float u, float v, float ul, float vl, string texturePath, string text,
            string fontPath, UEVector color, int style, bool smooth, bool clipped,
            HudGlyph[] glyphs = null, int polyFlags = 0)
        {
            Kind = kind; X = x; Y = y; XL = xl; YL = yl; U = u; V = v; UL = ul; VL = vl;
            TexturePath = texturePath; Text = text; FontPath = fontPath;
            Color = color; Style = style; Smooth = smooth; Clipped = clipped;
            Glyphs = glyphs; PolyFlags = polyFlags;
        }
    }

    // One live particle instance (review #11 item 2 / recorded decision):
    // Core owns the simulation; the frontend rasterises these and never
    // randomises or destroys anything.
    // One FDecal entry of a surface (Surf->Decals of the shipped level model) - the RESULT of
    // ADecal::execAttachDecal, run by the ported body over the renderer mirror model. One decal actor
    // can have several entries: MultiDecalLevel wrapping puts it on neighbouring surfaces. The frontend
    // only draws; it does no tracing or surface selection.
    public readonly struct DecalInstance
    {
        public readonly int DecalId;         // ActorSnapshot.ObjectId of the decal actor
        public readonly int OwnerId;         // shadow owner (0 - placed decal)
        public readonly int Surf;            // level model surface (past the map surfaces - a mover brush tracker surface)
        public readonly UEVector Normal;     // raw surface normal Vectors[vNormal] (UE space)
        public readonly UEVector Base;       // Points[surf.pBase]
        // FDecal vertices IN SHIPPED ORDER, RELATIVE TO Base: URender::ClipDecal
        // (Render.dll.c:3026-3034) draws V + Base + unit normal.
        public readonly UEVector V0, V1, V2, V3;
        // FDecal.Nodes - surface nodes the decal is drawn on (FUN_104127c0).
        public readonly int[] Nodes;
        // Pieces URender::DrawDecals draws this frame: the entry clipped by ClipDecal against each node's
        // polygon (from Nodes; empty - all surface nodes) for this frame's viewer. Empty if DrawDecals
        // draws no decals (SpanBased device, Client.Decals off, surface with PolyFlags & 0x8000000).
        public readonly DecalPiece[] Pieces;
        // The record's surface flags: Surf.PolyFlags and Surf.Texture.PolyFlags (+0x80). URender::DrawFrame draws the
        // decal list right after its surface (DrawComplexSurface 0x10b26334, the loop at 0x10b26390..0x10b263a8, no
        // iSurf < map-surfaces test): the decal takes its surface's pass — also for a mover brush tracker surface, whose
        // flags the frontend's level mesh does not have. SurfTexturePolyFlags is filled for a tracker surface only (0 on a
        // map surface, whose group the level mesh already has).
        public readonly int SurfPolyFlags, SurfTexturePolyFlags;

        // ONLY ATTACHMENT GEOMETRY HERE, AS A MATTER OF OWNERSHIP: texture and style travel in the decal
        // actor's own snapshot (ActorSnapshot.TexturePath/Style); duplicating them here would create a
        // second owner (C7).
        public DecalInstance(int decalId, int ownerId, int surf, UEVector normal,
            UEVector basePoint, UEVector v0, UEVector v1, UEVector v2, UEVector v3, int[] nodes, DecalPiece[] pieces,
            int surfPolyFlags = 0, int surfTexturePolyFlags = 0)
        {
            DecalId = decalId; OwnerId = ownerId; Surf = surf; Normal = normal;
            Base = basePoint; V0 = v0; V1 = v1; V2 = v2; V3 = v3; Nodes = nodes; Pieces = pieces;
            SurfPolyFlags = surfPolyFlags; SurfTexturePolyFlags = surfTexturePolyFlags;
        }
    }

    // A decal piece on one node polygon - the output of URender::ClipDecal: world points (V + base + N,
    // clipped by the "viewer + polygon edge" planes) and texture coordinates IN TEXTURE FRACTIONS: the
    // body yields texels (already divided by the actor DrawScale), the device divides by
    // USize*Texture.DrawScale (D3DDrv SetTexture + UTexture::Lock) - that division is done here.
    // View - the frame the piece was drawn in: index into WorldFrame.Views, -1 = master frame (ClipDecal
    // clips with that frame's camera, its points are in that frame's world).
    public readonly struct DecalPiece
    {
        public readonly int Node;
        public readonly UEVector[] Points;
        public readonly float[] U, V;
        public readonly int View;
        public DecalPiece(int node, UEVector[] points, float[] u, float[] v, int view)
        { Node = node; Points = points; U = u; V = v; View = view; }
    }

    public readonly struct ParticleInstance
    {
        public readonly int EmitterId;       // ActorSnapshot.ObjectId of the emitter
        public readonly UEVector Pos;        // UE space
        public readonly float Width;
        public readonly float Length;
        public readonly float Alpha;
        public readonly UEVector Color;      // 0..1 floats
        public readonly float SpinPos;       // radians
        public readonly string TexturePath;  // emitter Textures[0]
        // PPRIM_Liquid geometry (RenderPrimitive=2): Render.dll FUN_10b17ce0 reads the particle DripTimer
        // (+0x74) - hanging or falling drop - and FUN_10b15b50 builds a streak along the velocity (+0x18 Vel).
        // UE space.
        public readonly UEVector Vel;
        public readonly float DripTimer;
        // UParticle +0x80 SpinRate (SpinPos +0x7c += dt·SpinRate when DripTimer == 0, Engine disasm 0x103bfcce..0x103bfcda).
        // The billboard (Render.dll FUN_10b184d0, 0x10b18603) is NOT rotated when SpinRate == 0, whatever SpinPos holds.
        public readonly float SpinRate;
        // The view (frame index) of the DrawParticleSystem call that drew this particle (-1: every view of the emitter,
        // the reference render pass). Core's billboard pass decides per call which particles are drawn, with which Alpha.
        public readonly int View;
        public ParticleInstance(int emitterId, UEVector pos, float width, float length,
            float alpha, UEVector color, float spinPos, string texturePath,
            UEVector vel, float dripTimer, float spinRate = 0f, int view = -1)
        {
            EmitterId = emitterId; Pos = pos; Width = width; Length = length;
            Alpha = alpha; Color = color; SpinPos = spinPos; TexturePath = texturePath;
            Vel = vel; DripTimer = dripTimer; SpinRate = spinRate; View = view;
        }
    }

    // A whole-world snapshot at one tick.
    public sealed class WorldFrame
    {
        public readonly int Tick;
        public readonly string Label;
        // The list storage belongs to the frame producer and is reused every three frames (see
        // WorldFrameProjector.ProjectScratch): frames N-1 and N-2 stay intact while N is built; frame N-3's
        // list is overwritten. Copy a snapshot (a struct) for long-term storage.
        public readonly IReadOnlyList<ActorSnapshot> Actors;
        // The same snapshots as a span, for reading by ref readonly: an element through IReadOnlyList is a copy of
        // the whole struct (hundreds of bytes with references), and Core walks the list a dozen times per tick.
        public System.ReadOnlySpan<ActorSnapshot> ActorSpan => SnapshotSpan(Actors);
        public static System.ReadOnlySpan<ActorSnapshot> SnapshotSpan(IReadOnlyList<ActorSnapshot> actors) => actors switch
        {
            null => default,
            List<ActorSnapshot> l => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(l),
            ActorSnapshot[] a => a,
            _ => System.Linq.Enumerable.ToArray(actors),
        };
        public readonly CameraSnapshot Camera;
        public readonly IReadOnlyList<AudioCommand> Audio;   // commands since the last frame
        public readonly MusicState Music;
        public readonly IReadOnlyList<HudCommand> Hud;       // this tick's HUD draw list
        public readonly IReadOnlyList<ParticleInstance> Particles;
        // Attached decals of this frame (actor shadows and scorch marks).
        public readonly IReadOnlyList<DecalInstance> Decals;

        // ObjectId OF THE VIEWPORT ACTOR. 0 - no player.
        //
        // WHY IN THE FRAME. The frontend must know which frame actor is the player: its presentation is
        // owned by a separate rig, and it gets no ordinary actor proxy (otherwise the player would be
        // "driven like an NPC"). The special case is named by an IDENTITY from Core, not by class or name.
        public readonly int LocalPlayerId;

        // LEVEL AMBIENT - FROM THE FRAME.
        //
        // Taken from ULevel::Actors(0). This is NOT a choice between two similar actors: shipped does not
        // search LevelInfo by class but ASSERTS the slot - every `GetLevelInfo()` in the decompile is
        // `Actors(0)`, guarded by `Actors(0)->IsA(ALevelInfo::StaticClass())` (UnLevel.h:0xf7-0xf8).
        // Tut1 carries a SECOND LevelInfo (LevelInfo5, Actors(3)), and a class search would differ from
        // slot 0 - hence the slot.
        //
        // On Tut1 data both LevelInfos have the SAME ambient (Hue 170, Saturation 100, Brightness 20), so
        // the choice is unobservable on that map; it is still done correctly, as another map may differ.
        public readonly LevelAmbientState LevelAmbient;

        // -- SECOND HALF OF THE bHighDetail GATE - THE DEVICE VALUE --
        //
        // Shipped compares the actor's bHighDetail with the RENDER DEVICE value (Core decides it in SetupDynamics), so this
        // is frame state, not an actor property:
        //   Render.dll.c:10843 - the actor is accepted if
        //     (Actor+0x28 & 0x400000) == 0 || *(int*)(*(int*)(Frame+0) + 0x5c) + 100) != 0
        //   i.e. `!bHighDetail || RenDev->HighDetailActors`.
        //
        // THE FIELD NAME IS RESOLVED BY NATIVE CLASS REGISTRATION (Engine.dll.c:153242-153248): it registers
        // UBoolProperty "HighDetailActors" at offset 100 (=0x64) - exactly the one the gate reads. The
        // adjacent "Client" string is the property CATEGORY, not the owner: the values live in the render
        // device sections. (There is no "detailMode" field.)
        public readonly bool HighDetailActors;

        // -- LEVEL PAUSE - FRAME STATE --
        //
        // Single truth: LevelInfo.Pauser is non-empty (Engine.dll.c:262651); written by bytecode
        // GameInfo.SetPause, read by Core itself (TickActorsPass) and by this field for presentations.
        // Presentations must not read a frontend flag for pause (that would be a second owner);
        // HP1ConsoleRenderer.Active is UI/cursor state ONLY.
        public readonly bool Paused;

        // Full-screen flash/fade of the viewport actor - see ViewFlashState.
        public readonly ViewFlashState ViewFlash;

        // Render->DrawWorld gate (Engine.dll.c:117935-117939): non-null frame and !Console.bNoDrawWorld.
        // The menu sets bNoDrawWorld (WindowConsole.LaunchUWindow), and the world under the book is NOT
        // drawn - the book is on black.
        public readonly bool DrawWorld;

        // This frame's listener zone reverb (Galaxy UpdateReverb, see ReverbState).
        public readonly ReverbState Reverb;

        // This frame's occlusion child frames (sky, mirrors, warp) - see ViewFrame. The frontend does not build them.
        public readonly IReadOnlyList<ViewFrame> Views;

        // Sprite actors (DT_Sprite, DT_SpriteAnimOnce) that this frame's DrawFrame drew (the FDynamicSprite
        // passed Setup and occlusion in some frame of the tree), ObjectIds in draw order. The frontend draws no others.
        public readonly IReadOnlyList<int> SpriteActors;

        // Mesh vertices for which this frame's renderer ran GetFrame (see MeshPose), in draw order.
        public readonly IReadOnlyList<MeshPose> MeshPoses;

        // Which frames of the tree drew each actor (see ActorViewSets). The frontend draws an actor only in those passes.
        public readonly ActorViewSets ActorViews;

        // DrawActorSprite order of each frame of the tree (index 0 — master frame, i + 1 — Views[i]): ObjectIds of the
        // meshes (a third-person weapon right after its pawn), sprites and emitters that frame's DrawFrame drew, in the
        // sorted array's pass 1 then pass 2 order (UESpriteSort.NonSpanBasedOrder). The frontend draws a frame's actors in
        // exactly this sequence and never reorders them. null — no direct-path frame this tick.
        public readonly IReadOnlyList<int[]> DrawOrder;

        // Interface scale the canvas of this frame was built with (UEWorldFrameProducer.SetScreen: canvas = screen /
        // scale, D-44 Interface Size, not original): the frontend scales this frame's HUD commands by it and never
        // re-derives it — a menu change of the step mid-tick applies from the next frame's canvas.
        public readonly int CanvasScale;

        // Coronas of the master frame (UECoronas), drawn after the world and before the HUD.
        public readonly IReadOnlyList<CoronaInstance> Coronas;

        // World polygons each frame of the tree drew (see DrawnWorld): index 0 — master frame, i + 1 — Views[i]. null — no
        // OccludeBsp ran this tick (no camera, DrawWorld off): Core drew no world. The frontend draws BSP and mover
        // polygons only from here (author decision 2026-10-16).
        public readonly IReadOnlyList<DrawnWorld> Drawn;

        // Textures DrawFrame updated this tick (see DrawnWorld): AnimNext chains and procedural textures advance only these.
        public readonly IReadOnlyList<string> DrawnTextures;

        // Original / Enhanced (author decision 2026-10-15, NOT ORIGINAL): the Options item "Object Detail" as read by
        // Core this frame (UEWorldFrameProducer.EnhancedVisuals). true — the frontend applies the runtime-switchable
        // visual improvements (anisotropy D-53, trilinear mips D-48); false — the shipped D3DDrv sampling.
        public readonly bool EnhancedVisuals;

        // Galaxy channels after this frame's Update (UEGalaxyAudio, 16 entries by index; null - free): the frontend
        // plays voices from them and decides nothing. AudioResumeSerial - the song held on viewport loss (D-51).
        public readonly IReadOnlyList<UEGalaxyAudio.Playback> AudioChannels;
        public readonly int AudioResumeSerial;
        // K2: pool voices the frame's device calls freed before its Update, and the dt that Update advanced the audio clock by
        // (UEGalaxyAudio.LastCallStops / LastMixDt): the frontend stops them, mixes the frame's audio, then applies the table.
        public readonly IReadOnlyList<int> AudioCallStops;
        public readonly float AudioMixDt;

        public WorldFrame(int tick, string label, IReadOnlyList<ActorSnapshot> actors,
            CameraSnapshot camera, IReadOnlyList<AudioCommand> audio, MusicState music,
            IReadOnlyList<HudCommand> hud, IReadOnlyList<ParticleInstance> particles,
            int localPlayerId, LevelAmbientState levelAmbient = default,
            bool highDetailActors = true, IReadOnlyList<DecalInstance> decals = null,
            bool paused = false, ViewFlashState viewFlash = default, bool drawWorld = true,
            ReverbState reverb = default, IReadOnlyList<ViewFrame> views = null,
            IReadOnlyList<MeshPose> meshPoses = null, IReadOnlyList<int> spriteActors = null,
            IReadOnlyList<UEGalaxyAudio.Playback> audioChannels = null, int audioResumeSerial = 0,
            ActorViewSets actorViews = null, IReadOnlyList<CoronaInstance> coronas = null, bool enhancedVisuals = true,
            IReadOnlyList<DrawnWorld> drawn = null, IReadOnlyList<string> drawnTextures = null,
            IReadOnlyList<int> audioCallStops = null, float audioMixDt = 0f, IReadOnlyList<int[]> drawOrder = null,
            int canvasScale = 1)
        {
            DrawOrder = drawOrder; CanvasScale = canvasScale;
            AudioCallStops = audioCallStops ?? System.Array.Empty<int>(); AudioMixDt = audioMixDt;
            Drawn = drawn; DrawnTextures = drawnTextures ?? _noTextures;
            Coronas = coronas ?? _noCoronas; EnhancedVisuals = enhancedVisuals;
            AudioChannels = audioChannels ?? _noChannels; AudioResumeSerial = audioResumeSerial;
            ActorViews = actorViews ?? ActorViewSets.Empty;
            DrawWorld = drawWorld; Reverb = reverb; Views = views ?? _noViews; MeshPoses = meshPoses ?? _noPoses;
            SpriteActors = spriteActors ?? _noSprites;
            Tick = tick; Label = label; Actors = actors; Camera = camera; Audio = audio;
            Music = music; Hud = hud; Particles = particles; LocalPlayerId = localPlayerId;
            LevelAmbient = levelAmbient; HighDetailActors = highDetailActors;
            Decals = decals ?? _noDecals; Paused = paused; ViewFlash = viewFlash;
        }

        static readonly DecalInstance[] _noDecals = new DecalInstance[0];
        static readonly UEGalaxyAudio.Playback[] _noChannels = new UEGalaxyAudio.Playback[UEGalaxyAudio.EffectsChannels];
        static readonly ViewFrame[] _noViews = new ViewFrame[0];
        static readonly int[] _noSprites = new int[0];
        static readonly CoronaInstance[] _noCoronas = new CoronaInstance[0];
        static readonly string[] _noTextures = new string[0];
        static readonly MeshPose[] _noPoses = new MeshPose[0];
    }

    // -- CHILD FRAME (D-61) --
    //
    // A frame that Core occlusion (UEOcclusion - port of URender::OccludeBsp and CreateChildFrame, Render.dll 1.53)
    // spawned from a sky branch (PF_FakeBackdrop 0x80), mirror (PF_Mirrored) or warp portal (PF_Portal of AWarpZoneInfo).
    // Order - top-down tree traversal (parent before children; DrawFrame draws children before the parent).
    //   Parent  - parent index in Views, -1 = master frame;
    //   M       - frame world point -> master frame world point (master Coords^-1 o frame Coords; orthogonal axes);
    //   ClipN/D - the frame's NearClip in its world: n*p + d >= 0 is kept (ClipBspSurf: N*v - W >= 0 in frame axes);
    //             n = 0 - no NearClip (W = 0);
    //   Flip    - frame Mirror = -1 (reversed face winding);
    //   Coords  - the frame's own FSceneNode Coords (+0x34; CreateChildFrame: mirror MirrorByVector/ByPlane, warp WarpCoords,
    //             sky SkyZone): origin = the frame's eye, X right, Y down, Z forward, in the frame's world. Everything set up
    //             "with the frame it is given" (sprite tiles, particle billboards, mesh specular, PF_Environment UVs, actor
    //             vertex fog) takes the eye and axes from here, not from the master camera;
    //   Zone    - the frame's zone (CreateChildFrame iZone: the sky zone, the parent's for a mirror, OtherSideActor.iWarpZone
    //             for a warp); the frame judge compares it with the original's CHILD zone=;
    //   Sources - surfaces whose branch spawned the frame (CreateChildFrame merges identical frames): a level surface
    //             or a mover brush polygon inserted by the tracker (FMovingBrushTracker::SetupActorBrush).
    public readonly struct ViewSource
    {
        public readonly int Surf;        // level model surface; -1 - mover surface
        public readonly int MoverId;     // mover ObjectId; 0 - level surface
        public readonly int MoverPoly;   // mover brush FPoly number (UPolys)
        public ViewSource(int surf, int moverId, int moverPoly) { Surf = surf; MoverId = moverId; MoverPoly = moverPoly; }
    }

    public readonly struct ViewFrame
    {
        public const int KindSky = 1, KindMirror = 2, KindWarp = 3;
        public readonly int Parent, Kind, Recursion, Zone;
        public readonly Affine M;
        public readonly UEVector ClipN;
        public readonly float ClipD;
        public readonly bool Flip;
        public readonly ViewSource[] Sources;
        public readonly BrushCoords Coords;
        public ViewFrame(int parent, int kind, int recursion, in Affine m, UEVector clipN, float clipD, bool flip, ViewSource[] sources,
            in BrushCoords coords, int zone = 0)
        {
            Parent = parent; Kind = kind; Recursion = recursion; Zone = zone; M = m; ClipN = clipN; ClipD = clipD; Flip = flip; Sources = sources;
            Coords = coords;
        }
    }

    // -- WORLD POLYGONS DRAWN BY ONE FRAME OF THE TREE --
    //
    // URender::OccludeBsp keeps, for a device without SpanBased (D3DDrv), every BSP polygon that passed the span buffer as
    // an FSavedPoly (Render.dll.c:6602-6616, 6641-6656; UEOcclusion.Surface -> UESceneNode.DrawnPolys), and DrawFrame hands
    // exactly those to the device; the device Z-buffers them. A polygon that did not pass (covered by earlier polygons
    // of its zone, outside the frustum, culled by its side) is not drawn.
    //   Nodes      — level model nodes (FBspNode index), OccludeBsp order;
    //   Movers     — mover brush polygons (MoverId, MoverPoly) of which the tracker inserted at least one drawn fragment;
    //   SkyWindows — PF_FakeBackdrop level nodes whose sky branch spawned this frame's sky child (no FSavedPoly: the
    //                branch returns first); the sky frame is seen through them;
    //   BackNodes  — Nodes drawn from their back side (two-sided, portal, or the eye within 1 unit behind the plane).
    // DrawnTextures (WorldFrame): DrawFrame calls Surf->Texture->Update(Viewport->CurrentTime) (vtable +0x7c) for every
    // surface it draws, before the device sees it; a surface without a texture takes Level->DefaultTexture with no
    // Update (Render.dll 0x10b2587d..0x10b258c1). UTexture::Update steps the AnimNext chain and procedural textures
    // (UTexture::Lock calls the same slot only when the time changed, Engine.dll.c:97675-97678): a texture nobody draws
    // does not advance.
    public sealed class DrawnWorld
    {
        public static readonly DrawnWorld Empty = new(System.Array.Empty<int>(), System.Array.Empty<ViewSource>(), System.Array.Empty<int>());
        public readonly int[] Nodes;
        public readonly ViewSource[] Movers;
        public readonly int[] SkyWindows;
        // BackNodes — the Nodes drawn with the eye on their back side (OccludeBsp side 0): drawn with iZone[0] (lightmap
        // zone, fog zone, pan speed), Render.dll.c:5872-5874 pFVar15[uStack_40 + 0x34]; the rest with iZone[1].
        public readonly int[] BackNodes;
        // Reflagged — drawn polygons whose draw-record PolyFlags OccludeBsp changed from Surf | Texture flags, with the
        // flags the device gets: a translucent mirror with RenDev ShinySurfaces == 0 (0x10b23a8e..0x10b23ab8) has no child,
        // PF_Translucent cleared, 0x80000000 set and is drawn in the opaque pass 1. Node: level node, -1 for a mover polygon
        // (Mover.MoverId, Mover.MoverPoly).
        public readonly (int Node, ViewSource Mover, int PolyFlags)[] Reflagged;
        public DrawnWorld(int[] nodes, ViewSource[] movers, int[] skyWindows, int[] backNodes = null, (int, ViewSource, int)[] reflagged = null)
        {
            Nodes = nodes; Movers = movers; SkyWindows = skyWindows; BackNodes = backNodes ?? System.Array.Empty<int>();
            Reflagged = reflagged ?? System.Array.Empty<(int, ViewSource, int)>();
        }
    }

    // -- FRAME MESH VERTICES --
    //
    // World points produced by GetFrame of this frame's DrawActorSprite mesh branch
    // (UELevelRuntime.DrawMeshActor): for a skeletal mesh - via the full ApplyAnim header, for a vertex
    // mesh - ULodMesh::GetFrame. An actor without a pose was not drawn by the renderer this frame.
    // Weapon - a pawn's third-person weapon (ThirdPersonMesh in the weapon bone frame), ObjectId - the
    // weapon actor itself. The frontend does not compute vertices or call ApplyAnim.
    // MeshPath - the mesh GetFrame ran with (for a weapon - ThirdPersonMesh during the swap).
    // URender::DrawFrame draws each frame's own Frame->Sprite list (children before the parent, Render.dll.c:1011-1014):
    // an actor seen by a mirror but not by the master frame is drawn only in the mirror, and vice versa. Sets[i] lists the
    // view indices (WorldFrame.Views index, -1 = master) of one membership; SetOf maps ObjectId -> index into Sets.
    // An actor absent from SetOf was not drawn by any frame of this tick.
    public sealed class ActorViewSets
    {
        public static readonly ActorViewSets Empty = new(new Dictionary<int, int>(), System.Array.Empty<int[]>());
        public readonly IReadOnlyDictionary<int, int> SetOf;
        public readonly IReadOnlyList<int[]> Sets;
        public ActorViewSets(IReadOnlyDictionary<int, int> setOf, IReadOnlyList<int[]> sets) { SetOf = setOf; Sets = sets; }

        public static ActorViewSets From(IReadOnlyDictionary<int, List<int>> views)
            => From(views, System.Array.Empty<int[]>(), out _);

        // extra — further memberships (MeshLodPart: the frames of one LOD level of an actor); extraSet[k] is the index of
        // extra[k] in Sets.
        public static ActorViewSets From(IReadOnlyDictionary<int, List<int>> views, IReadOnlyList<int[]> extra, out int[] extraSet)
        {
            var setOf = new Dictionary<int, int>(views.Count);
            var sets = new List<int[]>();
            var byKey = new Dictionary<string, int>();
            int Intern(IEnumerable<int> members)
            {
                int[] v = members.Distinct().OrderBy(x => x).ToArray();
                string key = string.Join(",", v);
                if (!byKey.TryGetValue(key, out int i)) { i = sets.Count; byKey[key] = i; sets.Add(v); }
                return i;
            }
            foreach (var kv in views) setOf[kv.Key] = Intern(kv.Value);
            extraSet = new int[extra.Count];
            for (int k = 0; k < extra.Count; k++) extraSet[k] = Intern(extra[k]);
            return new ActorViewSets(setOf, sets);
        }
    }

    // LodParts: URender::DrawLodMesh and its GetFrame run per frame that draws the actor, each with its own LOD level
    // (UEMeshLod.VertsRequested over that frame's FDynamicSprite, then the GetFrame clamp) and its own pose (GetFrame is not
    // idempotent across draws: channel tween, vertex-cache count). One part per (level, pose): Verts — the drawn vertex
    // count (-1 = no LOD, the full mesh), ViewSet — index into ActorViews.Sets of the frames drawing it, Points — the
    // part's own pose (null = MeshPose.Points). null — the full mesh with Points in every frame of the actor.
    public readonly struct MeshPose
    {
        public readonly int ObjectId;
        public readonly bool Weapon;
        public readonly string MeshPath;
        public readonly UEVector[] Points;
        public readonly IReadOnlyList<MeshLodPart> LodParts;
        public MeshPose(int objectId, bool weapon, string meshPath, UEVector[] points, IReadOnlyList<MeshLodPart> lodParts = null)
        { ObjectId = objectId; Weapon = weapon; MeshPath = meshPath; Points = points; LodParts = lodParts; }
    }

    public readonly struct MeshLodPart
    {
        public readonly int Verts;
        public readonly int ViewSet;
        public readonly UEVector[] Points;   // this part's own GetFrame pose; null — MeshPose.Points
        public MeshLodPart(int verts, int viewSet, UEVector[] points = null) { Verts = verts; ViewSet = viewSet; Points = points; }
    }

    // One corona tile of the master frame (UECoronas: the DrawFrame corona block, Render.dll 0x10b26a8c..0x10b26e07),
    // in slot order. The frontend projects Location with the frame camera (Z > 1 only), draws Skin as a screen tile of
    // side FrameX * DrawScale * 0.8 centred on the projection, colour (R, G, B), PF_Translucent, no depth test.
    public readonly struct CoronaInstance
    {
        public readonly int ActorId;
        public readonly string Skin;
        public readonly UEVector Location;
        public readonly float DrawScale;
        public readonly float R, G, B;   // HueSat(LightHue, LightSaturation) * slot brightness
        // The DrawIcon tile in master-frame units (Frame->X × Frame->Y), projected by Core (UECoronas.Project); OnScreen false —
        // behind the camera (V.Z <= 1), not drawn. FrameX — Frame->X the tile was projected for.
        public readonly bool OnScreen;
        public readonly float X0, Y0, X1, Y1;
        public readonly int FrameX;
        public CoronaInstance(int actorId, string skin, UEVector location, float drawScale, float r, float g, float b,
            bool onScreen = false, float x0 = 0f, float y0 = 0f, float x1 = 0f, float y1 = 0f, int frameX = 0)
        {
            ActorId = actorId; Skin = skin; Location = location; DrawScale = drawScale; R = r; G = g; B = b;
            OnScreen = onScreen; X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; FrameX = frameX;
        }
    }

    // -- FULL-SCREEN FLASH/FADE - PLAYER FRAME STATE --
    //
    // What shipped draws: UGameEngine::Draw (Engine.dll.c:117867-117890) takes FlashFog (+0x4f8..+0x504)
    // from the viewport actor: scale = clamp01(W*0.5), rgb = clamp01(xyz) when Client.ScreenFlashes
    // (Client+0x44, UBoolProperty "ScreenFlashes", Default.ini: True); otherwise identity. D3D EndFlash
    // (D3DDrv.dll.c:2311) draws a ONE/SRCALPHA quad with alpha = min(2*scale, 1):
    // screen' = rgb + screen*W. The identity quad (rgb=0, W=1) is skipped.
    //
    // The state is computed by CORE, through shipped bytecode: setters ClientFadeIn/Out, ClientFlash,
    // ClientInstantFlash, ClientAdjustGlow and the integrator PlayerPawn.ViewFlash (Engine.u, 338 bytes).
    // The frontend does NO integration and never writes player Props: it draws exactly these four
    // numbers (C7: one owner of player state).
    public readonly struct ViewFlashState
    {
        public readonly bool Valid;          // a player exists
        public readonly float R, G, B, W;    // FlashFog as stored on the player (unclamped)
        public ViewFlashState(float r, float g, float b, float w)
        { Valid = true; R = r; G = g; B = b; W = w; }
        // Identity quad per the EndFlash rule: rgb=0 and scale=0.5 (W=1).
        public bool IsIdentity => R == 0f && G == 0f && B == 0f && W >= 1f;

        // The EndFlash quad: UGameEngine::Draw (Engine.dll.c:117867-117890) scale = clamp01(W·0.5), rgb = clamp01(FlashFog);
        // EndFlash (D3DDrv.dll.c:2311-2449) alpha = min(2·scale, 1). Not drawn: no player, or the identity quad (rgb = 0 and
        // alpha >= 1, checked AFTER the clamp). The one owner of the formula; the frontend draws the result.
        public bool EndFlash(out float r, out float g, out float b, out float alpha)
        {
            static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
            r = g = b = alpha = 0f;
            if (!Valid) return false;
            float scale = Clamp01(W * 0.5f);
            r = Clamp01(R); g = Clamp01(G); b = Clamp01(B); alpha = System.MathF.Min(2f * scale, 1f);
            return !(r == 0f && g == 0f && b == 0f && alpha >= 1f);
        }
    }

    // Level ambient as UE1 BYTES, not a finished colour: RGB conversion belongs to the frontend (it has
    // its own curve, checked against the native); the frame carries what is in the data.
    public readonly struct LevelAmbientState
    {
        public readonly bool Valid;
        public readonly int Hue, Saturation, Brightness;
        public LevelAmbientState(int hue, int saturation, int brightness)
        {
            Valid = true; Hue = hue; Saturation = saturation; Brightness = brightness;
        }
    }

    public static class WorldFrameProjector
    {
        static readonly string[] _noTouch = System.Array.Empty<string>();

        // Level ambient from SLOT 0, not by class search (UnLevel.h:0xf7-0xf8). A class mismatch in slot 0
        // is a world defect, not a reason to search further: shipped has an assert there, not a scan.
        public static LevelAmbientState ProjectLevelAmbient(UEObject actors0)
        {
            if (actors0 is null) return default;
            return new LevelAmbientState(
                UEValue.AsByte(actors0.GetProp(PK.AmbientHue)),
                UEValue.AsByte(actors0.GetProp(PK.AmbientSaturation)),
                UEValue.AsByte(actors0.GetProp(PK.AmbientBrightness)));
        }

        static string NameOf(object o) => (o as UEObject)?.GetProp(PK.Name)?.ToString() ?? "-";

        // AActor.AuxAnims -> channel ObjectIds in array order; destroyed actors are not included.
        static IReadOnlyList<int> AuxAnimIds(object auxAnims)
        {
            if (auxAnims is not System.Collections.IList list || list.Count == 0) return null;
            var ids = new List<int>(list.Count);
            foreach (object o in list)
                if (o is UEObject ch && !ch.Destroyed) ids.Add(ch.ObjectId);
            return ids.Count == 0 ? null : ids;
        }
        // Enum.ToString() is reflection plus a string per actor per frame; the names are cached once.
        static readonly Dictionary<LatentRunState, string> _latentNames = new();
        static string LatentName(LatentRunState st)
        {
            if (!_latentNames.TryGetValue(st, out string n)) _latentNames[st] = n = st.ToString();
            return n;
        }
        static int IdOf(object o) => (o as UEObject)?.ObjectId ?? -1;
        static readonly string[] _noSkins = System.Array.Empty<string>();

        // Full UE asset path of a resource reference (Mesh/Texture) — the
        // path-first identity the proxy resolves through its registries; null
        // if the field is empty/None.
        // A RESOURCE PATH is a pure function of the resource itself (its place in the package), so it is
        // computed ONCE per object.
        //
        // Why cache: UObject.GetPath() BUILDS a string by walking the outer chain, and projection calls it
        // up to 11 times per actor (Mesh, Texture, Skin and eight MultiSkins) EVERY frame - on the 2147
        // actors of Lev_Tut1b that was most of the frame garbage. Key - the object by reference; the result
        // does not depend on world state. The key is held WEAKLY: every map load and save load builds new resource
        // references, and a strong table kept every previous world's references and their path strings
        // (~0.3 MB per load on Lev_Tut1/Lev_Tut1b, audit/memory_growth.sh --travel). Reference identity, like before.
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, string> _assetPaths = new();

        // Public for Core owners that need a resource path outside actor projection (first one - the decal
        // snapshot): a second way to build the same path would be a second source of truth.
        public static string AssetPath(object refVal)
        {
            if (refVal is null) return null;
            if (_assetPaths.TryGetValue(refVal, out string cached)) return cached;
            string path = null;
            if (refVal is UEResourceRef rr)
                path = string.IsNullOrEmpty(rr.ObjectPath) ? rr.Name : rr.ObjectPath;
            else if (refVal is UELib.Core.UObject uo) path = uo.GetPath();
            else return null;              // not a resource - not cached
            _assetPaths.AddOrUpdate(refVal, path);
            return path;
        }

        // Project one live actor's state into a snapshot. Reads the SAME fields
        // the state digest renders (identity, transform, motion, physics,
        // region, base/owner, state frame, latents, timers, animation, touch).
        // outsideFrame: projection OUTSIDE frame building (headless text digest). Frame quantities that do
        // not exist without Render->PreRender (the FGlobalRandoms table for LT_Flicker) are then reported as
        // NaN "outside frame" instead of invented; a frame never gets such a projection.
        public static ActorSnapshot Project(UEObject a,
            bool outsideFrame = false)
        {
            var region = a.GetDeclared("Actor", "Region") as UEObject;
            var touchList = a.TouchList();
            List<string> touching = null;
            foreach (var t in touchList)
                if (t is not null && !t.Destroyed)
                    (touching ??= new List<string>()).Add(t.GetProp(PK.Name)?.ToString());

            return new ActorSnapshot(
                objectId:     a.ObjectId,
                baseId:       IdOf(a.GetProp(PK.Base)),
                ownerId:      IdOf(a.GetProp(PK.Owner)),
                name:         a.GetProp(PK.Name)?.ToString(),
                @class:       a.Class?.Name,
                placed:       a.GetProp(PK.ExportIndex) is not null,
                exportIndex:  UEValue.ToInt(a.GetProp(PK.ExportIndex)),
                location:     UEValue.AsVector3(a.GetProp(PK.Location)),
                rotation:     UEValue.AsRotator(a.GetProp(PK.Rotation)),
                velocity:     UEValue.AsVector3(a.GetProp(PK.Velocity)),
                acceleration: UEValue.AsVector3(a.GetProp(PK.Acceleration)),
                physics:      UEValue.ToInt(a.GetProp(PK.Physics)),
                zoneNumber:   UEValue.ToInt(region?.GetProp(PK.ZoneNumber)),
                leaf:         UEValue.ToInt(region?.GetProp(PK.iLeaf)),
                fogZone:      UEValue.ToBool(a.GetProp(PK.bFogZone)),
                texUPanSpeed: UEValue.AsFloat(a.GetProp(PK.TexUPanSpeed)),
                texVPanSpeed: UEValue.AsFloat(a.GetProp(PK.TexVPanSpeed)),
                animBone:     UEValue.ToInt(a.GetProp(PK.AnimBone)),
                auxAnims:     AuxAnimIds(a.GetProp(PK.AuxAnims)),
                @base:        NameOf(a.GetProp(PK.Base)),
                owner:        NameOf(a.GetProp(PK.Owner)),
                state:        a.StateFrame?.Func?.Name ?? "-",
                latent:       a.StateFrame is { } sfr ? LatentName(sfr.LatentState) : "-",
                moveTimer:    UEValue.AsFloat(a.GetProp(PK.MoveTimer)),
                animSequence: a.GetProp(PK.AnimSequence)?.ToString() ?? "-",
                animFrame:    UEValue.AsFloat(a.GetProp(PK.AnimFrame)),
                animRate:     UEValue.AsFloat(a.GetProp(PK.AnimRate)),
                tweenRate:    UEValue.AsFloat(a.GetProp(PK.TweenRate)),
                // THE SAME CONDITION AS THE TickRootMovement CALL - not a "similar" one.
                // Role_Authority = 4 (ENetRole), see UELevelRuntime.cs:606-615.
                rootMotionDriven: UEValue.ToInt(a.GetProp(PK.Role)) == 4
                                  && UEValue.ToBool(a.GetProp(PK.bAnimMove))
                                  && a.GetProp(PK.Mesh) is not null,
                tweenAlpha:   UEValue.AsFloat(a.GetProp(PK.TweenAlpha)),
                timerRate:    a.TimerRate,
                timerCounter: a.TimerCounter,
                lifeSpan:     UEValue.AsFloat(a.GetProp(PK.LifeSpan)),
                touching:     (IReadOnlyList<string>)touching ?? _noTouch,
                destroyed:    a.Destroyed,
                drawType:     UEValue.ToInt(a.GetProp(PK.DrawType)),
                meshPath:     AssetPath(a.GetProp(PK.Mesh)),
                // DT_Sprite/DT_SpriteAnimOnce render from Actor.Texture (URender::DrawActorSprite,
                // Render.dll.c:11928-12027) — the candles/torch-fire/lights the
                // frontend cannot build without it.
                texturePath:  AssetPath(a.GetProp(PK.Texture)),
                // DrawScale defaults to 1.0 in UE1; an actor that never set it
                // reads null → project the engine default, not 0.
                drawScale:    a.GetProp(PK.DrawScale) is { } ds ? UEValue.AsFloat(ds) : 1f,
                // Computed by the runtime (bAlignBottom vertical); a call without a runtime
                // (no mesh catalogue - nothing to compute) yields a zero term.
                style:        UEValue.ToInt(a.GetProp(PK.Style)),
                hidden:       UEValue.ToBool(a.GetProp(PK.bHidden)),
                unlit:        UEValue.ToBool(a.GetProp(PK.bUnlit)),
                meshEnviroMap: UEValue.ToBool(a.GetProp(PK.bMeshEnviroMap)),
                ambientGlow:  UEValue.ToInt(a.GetProp(PK.AmbientGlow)),
                scaleGlow:    a.GetProp(PK.ScaleGlow) is { } sg ? UEValue.AsFloat(sg) : 1f,
                fatness:      a.GetProp(PK.Fatness) is { } fat ? UEValue.ToInt(fat) : 128,
                opacity:      a.GetProp(PK.Opacity) is { } op ? UEValue.AsFloat(op) : 1f,
                specularGlow: a.GetProp(PK.SpecularGlow) is { } sgl ? UEValue.AsFloat(sgl) : 1f,
                specularWidth: a.GetProp(PK.SpecularWidth) is { } sw ? UEValue.ToInt(sw) : 170,
                spriteProjForward: a.GetProp(PK.SpriteProjForward) is { } spf ? UEValue.AsFloat(spf) : 32f,
                noSmooth:     UEValue.ToBool(a.GetProp(PK.bNoSmooth)),
                zoneEnvironmentMap:  AssetPath((region?.GetProp(PK.Zone) as UEObject)?.GetProp("EnvironmentMap")),
                levelEnvironmentMap: AssetPath((a.GetProp(PK.Level) as UEObject)?.GetProp("EnvironmentMap")),
                latePass:     UESpriteSort.Pass(a) == 2,
                skin:         AssetPath(a.GetProp(PK.Skin)),
                multiSkins:   ProjectMultiSkins(a.GetProp(PK.MultiSkins)),
                light:        ProjectLight(a, outsideFrame),
                mover:        ProjectMover(a),
                ambient:      ProjectAmbient(a),
                emitter:      ProjectEmitter(a));
        }

        // ── PER-ACTOR PROJECTION MEMO ───────────────────────────────────────
        //
        // Project reads ~90 values per actor per frame; most actors change none of them between frames. The memo
        // keeps the last snapshot with everything it was computed from and serves it while all of it is unchanged:
        //   * the actor's own property values — UEProps.Version (every write bumps it);
        //   * non-property actor state — Class, StateFrame/Func/LatentState, TimerRate/TimerCounter;
        //   * struct bags read through — Region, ParticlesPerSec/Lifetime, MainScale/PostScale (their own versions);
        //   * arrays mutated in place — Touching (+ each toucher's Destroyed and Name), AuxAnims (+ Destroyed),
        //     MultiSkins, Textures (elements by reference);
        //   * values of OTHER objects — Base/Owner Name, Region.Zone and Level EnvironmentMap (by reference);
        //   * time — light types whose scale moves with the frame clock or FGlobalRandoms (2, 3, 4, 5, 7) are
        //     never served from the memo.
        // Values are immutable boxes or references, so "same reference" is "same value". Project has no side
        // effect a hit could skip: TouchList's lazy array write happened on the miss (and bumped the version
        // recorded after it), AssetPath/LatentName/skin caches are pure memo tables. Render scheduling only: the
        // frame content is identical. HP1_FRAME_CACHE_VERIFY=1 re-projects every hit and compares field by field.
        static readonly int EnvironmentMapKey = UEPropKeys.Id("EnvironmentMap");
        static readonly int[] BagKeys =
        {
            UEPropKeys.Id("ParticlesPerSec"), UEPropKeys.Id("Lifetime"), UEPropKeys.Id("MainScale"), UEPropKeys.Id("PostScale"),
        };

        sealed class ProjMemo
        {
            public ActorSnapshot Snap;
            public int Version;
            public UELib.Core.UClass Class;
            public UEFrame State; public UELib.Core.UStruct Func; public LatentRunState Latent;
            public float TimerRate, TimerCounter;
            public UEObject Region; public int RegionVersion;
            public UEObject Zone; public object ZoneEnv;
            public UEObject Level; public object LevelEnv;
            public UEObject Base, Owner; public object BaseName, OwnerName;
            public UEObject[] Touch; public UEObject[] TouchElems; public object[] TouchNames; public bool[] TouchDead;
            public System.Collections.IList Aux; public object[] AuxElems; public bool[] AuxDead;
            public System.Collections.IList Skins; public object[] SkinElems;
            public System.Collections.IList Tex; public object[] TexElems;
            public readonly UEObject[] Bags = new UEObject[4]; public readonly int[] BagVersions = new int[4];
        }

        static bool TimeDependentLight(int type) => type == 2 || type == 3 || type == 4 || type == 5 || type == 7;
        static bool SameBits(float x, float y) => System.BitConverter.SingleToInt32Bits(x) == System.BitConverter.SingleToInt32Bits(y);
        static object NameRaw(UEObject o) => o?.GetProp(PK.Name);

        // Reuses the previous element array when the length matches: a miss must not allocate (memo arrays live
        // long, so fresh ones would be promoted and collected as gen1 garbage).
        static object[] Elems(System.Collections.IList l, object[] reuse)
        {
            if (l is null) return null;
            var e = reuse is not null && reuse.Length == l.Count ? reuse : new object[l.Count];
            for (int i = 0; i < e.Length; i++) e[i] = l[i];
            return e;
        }
        static bool SameElems(System.Collections.IList l, object[] e)
        {
            if (l is null) return e is null;
            if (e is null || l.Count != e.Length) return false;
            for (int i = 0; i < e.Length; i++) if (!ReferenceEquals(l[i], e[i])) return false;
            return true;
        }

        // Appends the actor's snapshot to `list` (one struct copy on a hit, instead of a return copy plus the Add copy).
        static void ProjectMemoInto(UEObject a, List<ActorSnapshot> list)
        {
            if (a.FrameProjMemo is ProjMemo m && MemoValid(a, m))
            {
                if (FrameCacheVerify.Enabled)
                    FrameCacheVerify.Hit("projection", FrameCacheVerify.Diff(Project(a), m.Snap, m.Snap.Name));
                list.Add(m.Snap);
                return;
            }
            if (FrameCacheVerify.Enabled) FrameCacheVerify.Miss("projection");
            list.Add(Project(a));
            ref readonly ActorSnapshot snap = ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list)[list.Count - 1];
            a.FrameProjMemo = TimeDependentLight(snap.Light.Type) ? null : Capture(a, in snap, a.FrameProjMemo as ProjMemo ?? new ProjMemo());
        }

        static ProjMemo Capture(UEObject a, in ActorSnapshot snap, ProjMemo m)
        {
            m.Snap = snap;
            m.Version = a.Props.Version;   // after Project: TouchList may have created the array
            m.Class = a.Class;
            m.State = a.StateFrame; m.Func = a.StateFrame?.Func; m.Latent = a.StateFrame?.LatentState ?? default;
            m.TimerRate = a.TimerRate; m.TimerCounter = a.TimerCounter;
            m.Region = a.GetDeclared("Actor", "Region") as UEObject;
            m.RegionVersion = m.Region?.Props.Version ?? 0;
            m.Zone = m.Region?.GetProp(PK.Zone) as UEObject;
            m.ZoneEnv = m.Zone?.GetProp(EnvironmentMapKey);
            m.Level = a.GetProp(PK.Level) as UEObject;
            m.LevelEnv = m.Level?.GetProp(EnvironmentMapKey);
            m.Base = a.GetProp(PK.Base) as UEObject; m.BaseName = NameRaw(m.Base);
            m.Owner = a.GetProp(PK.Owner) as UEObject; m.OwnerName = NameRaw(m.Owner);
            m.Touch = a.TouchList();
            int n = m.Touch.Length;
            if (m.TouchElems is null || m.TouchElems.Length != n)
            { m.TouchElems = new UEObject[n]; m.TouchNames = new object[n]; m.TouchDead = new bool[n]; }
            for (int i = 0; i < n; i++)
            {
                UEObject t = m.Touch[i];
                m.TouchElems[i] = t; m.TouchNames[i] = NameRaw(t); m.TouchDead[i] = t?.Destroyed ?? false;
            }
            m.Aux = a.GetProp(PK.AuxAnims) as System.Collections.IList;
            m.AuxElems = Elems(m.Aux, m.AuxElems);
            if (m.AuxElems is not null)
            {
                if (m.AuxDead is null || m.AuxDead.Length != m.AuxElems.Length) m.AuxDead = new bool[m.AuxElems.Length];
                for (int i = 0; i < m.AuxElems.Length; i++) m.AuxDead[i] = (m.AuxElems[i] as UEObject)?.Destroyed ?? false;
            }
            m.Skins = a.GetProp(PK.MultiSkins) as System.Collections.IList; m.SkinElems = Elems(m.Skins, m.SkinElems);
            m.Tex = a.GetProp(PK.Textures) as System.Collections.IList; m.TexElems = Elems(m.Tex, m.TexElems);
            for (int i = 0; i < BagKeys.Length; i++)
            {
                m.Bags[i] = a.GetProp(BagKeys[i]) as UEObject;
                m.BagVersions[i] = m.Bags[i]?.Props.Version ?? 0;
            }
            return m;
        }

        static bool MemoValid(UEObject a, ProjMemo m)
        {
            if (m.Version != a.Props.Version || !ReferenceEquals(m.Class, a.Class)) return false;
            UEFrame sf = a.StateFrame;
            if (!ReferenceEquals(m.State, sf) || (sf is not null && (!ReferenceEquals(m.Func, sf.Func) || m.Latent != sf.LatentState)))
                return false;
            if (!SameBits(m.TimerRate, a.TimerRate) || !SameBits(m.TimerCounter, a.TimerCounter)) return false;
            if (m.Region is not null && m.Region.Props.Version != m.RegionVersion) return false;
            if (m.Zone is not null && !ReferenceEquals(m.ZoneEnv, m.Zone.GetProp(EnvironmentMapKey))) return false;
            if (m.Level is not null && !ReferenceEquals(m.LevelEnv, m.Level.GetProp(EnvironmentMapKey))) return false;
            if (!ReferenceEquals(m.BaseName, NameRaw(m.Base)) || !ReferenceEquals(m.OwnerName, NameRaw(m.Owner))) return false;
            for (int i = 0; i < m.TouchElems.Length; i++)
            {
                UEObject t = m.Touch[i];
                if (!ReferenceEquals(t, m.TouchElems[i])) return false;
                if (t is not null && (t.Destroyed != m.TouchDead[i] || !ReferenceEquals(NameRaw(t), m.TouchNames[i]))) return false;
            }
            if (!SameElems(m.Aux, m.AuxElems)) return false;
            if (m.AuxElems is not null)
                for (int i = 0; i < m.AuxElems.Length; i++)
                    if (((m.AuxElems[i] as UEObject)?.Destroyed ?? false) != m.AuxDead[i]) return false;
            if (!SameElems(m.Skins, m.SkinElems) || !SameElems(m.Tex, m.TexElems)) return false;
            for (int i = 0; i < m.Bags.Length; i++)
                if (m.Bags[i] is not null && m.Bags[i].Props.Version != m.BagVersions[i]) return false;
            return true;
        }

        static FloatRange Range(UEObject a, string prop)
            => a.GetProp(prop) is UEObject bag
                ? new FloatRange(UEValue.AsFloat(bag.GetProp(PK.Base)), UEValue.AsFloat(bag.GetProp(PK.Rand)))
                : default;

        static ParticleState ProjectEmitter(UEObject a)
        {
            if (!UEInterpreter.ClassIsA(a.Class, "ParticleFX")) return default;   // Valid=false
            string[] tex = null;   // one exact array (allocation only)
            if (a.GetProp(PK.Textures) is System.Collections.IList tl && tl.Count > 0)
            {
                tex = new string[tl.Count];
                for (int i = 0; i < tex.Length; i++) tex[i] = AssetPath(tl[i]);
            }
            return new ParticleState(
                valid:           true,
                particlesPerSec: Range(a, "ParticlesPerSec"),
                lifetime:        Range(a, "Lifetime"),
                renderPrimitive: UEValue.ToInt(a.GetProp(PK.RenderPrimitive)),
                texturePaths:    (IReadOnlyList<string>)tex ?? _noSkins);
        }

        static AmbientState ProjectAmbient(UEObject a)
        {
            int radius = UEValue.AsByte(a.GetProp(PK.SoundRadius));
            return new AmbientState(
                soundPath:   AssetPath(a.GetProp(PK.AmbientSound)),
                soundRadius: radius,
                // StartAmbience radius (SoundRadius+1)*25 — one body, UEGalaxyVoiceMix.AmbientRadius.
                worldRadius: UEGalaxyVoiceMix.AmbientRadius(a),
                soundVolume: UEValue.AsByte(a.GetProp(PK.SoundVolume)),
                soundPitch:  UEValue.AsByte(a.GetProp(PK.SoundPitch)));
        }

        // Music from the local player pawn. The shipped Engine.u
        // PlayerPawn.ClientSetMusic body is script, not a frontend native:
        // it writes Song/SongSection/CdTrack/Transition/bDontLoopSong verbatim.
        // Galaxy.dll UpdateMusic reads those fields plus PercentMusicVolume.
        public static MusicState ProjectMusic(UEObject player)
        {
            if (player is null) return default;   // Valid=false
            return new MusicState(true,
                songPath:    AssetPath(player.GetProp(PK.Song)),
                songSection: UEValue.AsByte(player.GetProp(PK.SongSection)),
                cdTrack:     UEValue.AsByte(player.GetProp(PK.CdTrack)),
                transition:  UEValue.AsByte(player.GetProp(PK.Transition)),
                dontLoop:    UEValue.ToBool(player.GetProp(PK.bDontLoopSong)),
                percentVolume: UEValue.AsByte(player.GetProp(PK.PercentMusicVolume)));
        }

        // -- Light type animation: Render.dll table 0x10b38288 (by LightType) --
        //   1 Steady/6 BackdropLight - unchanged (0x10b06380/0x10b06500: ret);
        //   2 Pulse      (0x10b06390): b *= SinTab[ROUND(t*2293760/max(P,1) + Ph*256)]*0.39 + 0.6;
        //   3 Blink      (0x10b06410): if (ROUND(t*2293760/(P+1) + Ph*256) & 1) b = 0;
        //   4 Flicker    (0x10b06470): r = Randoms[actor & 0xff]; b = r < 0.5 ? 0 : b*r;
        //   5 Strobe     (0x10b064b0): a global trigger flips on each new TimeSeconds value;
        //                              when set, b = 0;
        //   7 SubtlePulse(0x10b06510): like Pulse with 0.09/0.9;
        //   8/9 TexturePalette* (0x10b06590/0x10b066d0): colour from the texture palette -
        //                              NOT ported, factor 1 with a loud warning.
        // Constants read from the image: 2293760 = 35*65536 (0x10b38668), 0.39 (0x10b38664), 0.6 (0x10b385c8),
        // 0.09 (0x10b3866c), 0.9 (0x10b3863c), 0.5 (0x10b385ec). ROUND - fistp to nearest even; SinTab - GMath
        // (same as BrushCoords.SinAngle: index (a>>2)&0x3fff).
        // Randoms - the FGlobalRandoms Base table (0x10b6696c), which UEWorldFrameProducer fills with the
        // FGlobalRandoms::Tick body (UEGlobalRandoms) at this frame's Render->PreRender point.
        // KNOWN REMAINING DIVERGENCE: (1) the port's render appFrand is its own MsvcRandom stream, shipped
        // shares CRT rand() with the game RNG (see UENativesCore.BindFrameProducer); (2) the
        // shipped Flicker key is the low byte of the actor's heap ADDRESS (FUN_10b06810 passes a pointer),
        // the port uses ObjectId: shipped process heap addresses are not reproducible.
        internal static class LightAnim
        {
            static float _time;
            static float _strobeLast = float.NaN;
            static bool _strobeOn;
            static float[] _rand;
            static bool _paletteNoted;

            public static bool HasFrameRandoms => _rand is not null;

            public static void Begin(float levelTime, float[] randomBase)
            {
                _time = levelTime;
                StrobeAt(levelTime);
                _rand = randomBase;
            }

            // Strobe (0x10b064b0): statics 0x10b5f724/0x10b5f728 are SHARED by all type-function calls - both
            // renderer and Galaxy: the trigger flips on the first call with a new TimeSeconds.
            static bool StrobeAt(float levelTime)
            {
                if (levelTime != _strobeLast) { _strobeOn = !_strobeOn; _strobeLast = levelTime; }
                return _strobeOn;
            }

            static int RoundToInt(float v)
            {
                double r = System.Math.Round((double)v, System.MidpointRounding.ToEven);
                return r >= int.MaxValue || r <= int.MinValue ? int.MinValue : (int)r;   // fistp indefinite
            }

            static float Sin(int angle) => BrushCoords.SinAngle(angle);

            public static float ScaleFor(int type, int period, int phase, int actorKey, UEObject a)
            {
                switch (type)
                {
                    case 2:
                    {
                        int p = period == 0 ? 1 : period;
                        int arg = RoundToInt(_time * 2293760f / p + (phase << 8));
                        return Sin(arg) * 0.39f + 0.6f;
                    }
                    case 3:
                    {
                        int arg = RoundToInt(_time * 2293760f / (period + 1) + (phase << 8));
                        return (arg & 1) != 0 ? 0f : 1f;
                    }
                    case 4:
                    {
                        if (_rand is null)
                            throw new System.InvalidOperationException(
                                "Flicker: the frame has no FGlobalRandoms table (frame built without Render->PreRender)");
                        float r = _rand[actorKey & 0xff];
                        return r < 0.5f ? 0f : r;
                    }
                    case 5: return _strobeOn ? 0f : 1f;
                    case 7:
                    {
                        // SubtlePulse (0x10b06510): the sine argument goes through MSVCRT
                        // _ftol (call 0x10b352c4 -> [0x10b74814]) - TRUNCATION, not
                        // fistp as in Pulse (0x10b063d7).
                        int p = period < 1 ? 1 : period;
                        int arg = unchecked((int)UEGlobalRandoms.FtolLow(_time * 2293760f / p + (phase << 8)));
                        return Sin(arg) * 0.09f + 0.9f;
                    }
                    case 8:
                    case 9:
                        if (!_paletteNoted)
                        {
                            _paletteNoted = true;
                            HP1.Core.UETrace.Warn($"LightType {type} (TexturePalette*) on {a.GetProp(PK.Name)}: texture palette as light color is not ported — multiplier 1");
                        }
                        return 1f;
                    default: return 1f;
                }
            }

            // The same type functions called FROM GALAXY (UpdateAmbience 0x10608635: GRender->GlobalLighting
            // over the ambient volume). x87 is in PC64 there (Update runs before Render->PreRender), so
            // intermediates are double, with a single float store where the body does `fstp dword`:
            //   Pulse 0x10b063d1 (argument) and 0x10b06403 (result),
            //   Blink 0x10b06446, Flicker 0x10b064a0, SubtlePulse - the argument goes to _ftol from the
            //   register (0x10b06553), result 0x10b0657f.
            // Time - Actor.Level.TimeSeconds at the call (bodies read [actor+0x7c]+0x3b4); the Flicker table -
            // FGlobalRandoms.Base at the call: Galaxy Update runs BEFORE Render->PreRender (Engine.dll.c:117938
            // before 117946), i.e. it sees the table of the PREVIOUS GRandoms->Tick - passed in by the caller.
            // The Strobe trigger is shared (StrobeAt).
            // 8/9 also change brightness (0x10b066ba/0x10b06800) - not ported: refusal.
            internal static float ModulatePc64(int type, int period, int phase, int actorKey, float br,
                float time, float[] randomBase, UEObject a)
            {
                switch (type)
                {
                    case 0: return 0f;
                    case 2:
                    {
                        int p = period < 1 ? 1 : period;
                        float argF = (float)((double)time * 2293760.0 / p + (phase << 8));
                        return (float)(((double)Sin(RoundToInt(argF)) * 0.39f + 0.6f) * br);
                    }
                    case 3:
                    {
                        float argF = (float)((double)time * 2293760.0 / (period + 1) + (phase << 8));
                        return (RoundToInt(argF) & 1) != 0 ? 0f : br;
                    }
                    case 4:
                    {
                        if (randomBase is null)
                            throw new System.InvalidOperationException(
                                "Flicker: no FGlobalRandoms table (no Render->PreRender before this Update)");
                        float r = randomBase[actorKey & 0xff];
                        return r < 0.5f ? 0f : (float)((double)r * br);
                    }
                    case 5: return StrobeAt(time) ? 0f : br;
                    case 7:
                    {
                        int p = period < 1 ? 1 : period;
                        int arg = UELevelRuntime.Ftol((double)time * 2293760.0 / p + (phase << 8));
                        return (float)(((double)Sin(arg) * 0.09f + 0.9f) * br);
                    }
                    case 8:
                    case 9:
                        throw new System.InvalidOperationException(
                            $"{a?.GetProp(PK.Name)}: LightType {type} (TexturePalette, 0x10b06590/0x10b066d0) " +
                            "changes brightness by the texture palette — not ported; ambient volume is not computed from it");
                    default: return br;   // 1, 6 — `ret`
                }
            }
        }

        static LightState ProjectLight(UEObject a, bool outsideFrame = false)
        {
            int radius = UEValue.AsByte(a.GetProp(PK.LightRadius));
            int lightType = UEValue.AsByte(a.GetProp(PK.LightType));
            float scale = outsideFrame && lightType == 4 && !LightAnim.HasFrameRandoms
                ? float.NaN
                : LightAnim.ScaleFor(lightType, UEValue.AsByte(a.GetProp(PK.LightPeriod)),
                    UEValue.AsByte(a.GetProp(PK.LightPhase)), a.ObjectId, a);
            return new LightState(
                type:             UEValue.AsByte(a.GetProp(PK.LightType)),
                effect:           UEValue.AsByte(a.GetProp(PK.LightEffect)),
                brightness:       UEValue.AsByte(a.GetProp(PK.LightBrightness)),
                hue:              UEValue.AsByte(a.GetProp(PK.LightHue)),
                saturation:       UEValue.AsByte(a.GetProp(PK.LightSaturation)),
                radius:           radius,
                worldRadius:      WorldLightRadius(a, radius),
                radiusInner:      UEValue.AsByte(a.GetProp(PK.LightRadiusInner)),
                cone:             UEValue.AsByte(a.GetProp(PK.LightCone)),
                phase:            UEValue.AsByte(a.GetProp(PK.LightPhase)),
                period:           UEValue.AsByte(a.GetProp(PK.LightPeriod)),
                volumeBrightness: UEValue.AsByte(a.GetProp(PK.VolumeBrightness)),
                corona:           UEValue.ToBool(a.GetProp(PK.bCorona)),
                dynamicLight:     UEValue.ToBool(a.GetProp(PK.bDynamicLight)),
                specialLit:       UEValue.ToBool(a.GetProp(PK.bSpecialLit)),
                // ELightSource, byte at AActor+0x1f4 (Render.dll.c:16887/16900).
                source:           UEValue.AsByte(a.GetProp(PK.LightSource)),
                // Per-actor selection: cap branch and dark-light sign
                // (Render.dll.c:21399-21437 + the -1.0 constant from .rdata).
                bStatic:          UEValue.ToBool(a.GetProp(PK.bStatic)),
                dark:             UEValue.ToBool(a.GetProp(PK.bDarkLight)),
                // Second half of the static/dynamic split
                // (Render.dll.c:11183-11185).
                noDelete:         UEValue.ToBool(a.GetProp(PK.bNoDelete)),
                scale:            scale,
                volumeRadius:     UEValue.AsByte(a.GetProp(PK.VolumeRadius)),
                volumeFog:        UEValue.AsByte(a.GetProp(PK.VolumeFog)),
                // Per-actor selection: non-static with bMovable - no LOS (0x10b0a223).
                movable:          UEValue.ToBool(a.GetProp(PK.bMovable)),
                // AActor::GetViewRotation: the class chain holds APawn -> ViewRotation, else Rotation.
                viewRotation:     UEValue.AsRotator(a.GetProp(UEInterpreter.ClassIsA(a.Class, "Pawn") ? PK.ViewRotation : PK.Rotation)));
        }

        // AActor::WorldLightRadius - PER SHIPPED, not per SE.
        //
        // SE (UActor.h:516) computes (LightRadius+1)*25 WITHOUT the scale factor. The shipped body
        // multiplies: export ?WorldLightRadius@AActor@@UBEMXZ (ordinal 2057) is a thunk to 0x1031ba10:
        //     fld  [ecx+0x16c]        ; DrawScale
        //     fcom [0x1046dd9c]       ; 1.0f
        //     ...  fld [0x1046dd9c]   ; DrawScale < 1 -> 1.0
        //     mov  al,[ecx+0x1e5]; inc eax; fimul   ; *(LightRadius+1)
        //     fmul [0x1046e2ac]       ; *25.0f
        // Offsets resolved by the layout oracle (audit/prop_offsets.py):
        // 0x16c = Actor.DrawScale, 0x1e5 = Actor.LightRadius.
        //
        // CONTROL: the neighbouring AActor::WorldSoundRadius (0x1031b9b0) has NO factor at all -
        // (SoundRadius+1)*25 - so the factor here is neither a decompiler artefact nor a shared template.
        //
        // Shipped wins on divergence, so Core owns the formula and the frontend consumes the finished
        // LightState.WorldRadius.
        static float WorldLightRadius(UEObject a, int radius)
        {
            // DrawScale defaults to 1.0 in UE1: an actor that never set it reads as null - project the engine
            // default, not 0 (otherwise max(0,1) would give the right answer by accident, not by source).
            float drawScale = a.GetProp(PK.DrawScale) is { } ds ? UEValue.AsFloat(ds) : 1f;
            return WorldLightRadius(drawScale, radius);
        }

        // The ONE body (SurfLightmap, MoverMode3, UEOcclusion and actor lighting call it too).
        // Disasm 0x1031ba11..0x1031ba3d: fcom against 1.0f - "less or unordered" (NaN) -> 1.0f; fimul
        // (LightRadius+1), fmul 25.0f. Every game caller runs inside the render window (SetupDynamics, FUN_10b31c70,
        // the SetupForSurf/SetupForActor fillers), where x87 is PC24: EACH operation rounds to float. Judged by the
        // Engine body under unicorn at PC24 (SelfTest world_light_radius_pc24_matches_engine: the single-rounding
        // form differed in 309 of 2560 DrawScale x radius cases).
        public static float WorldLightRadius(float drawScale, int radius)
        {
            float scale = !(drawScale >= 1f) ? 1f : drawScale;
            float m = scale * (radius + 1);
            return m * 25f;
        }

        static MoverState ProjectMover(UEObject a)
        {
            var brush = a.GetProp(PK.Brush) as UEModelRef;
            return new MoverState(
                keyNum:        UEValue.AsByte(a.GetProp(PK.KeyNum)),
                prevKeyNum:    UEValue.AsByte(a.GetProp(PK.PrevKeyNum)),
                numKeys:       UEValue.AsByte(a.GetProp(PK.NumKeys)),
                physAlpha:     UEValue.AsFloat(a.GetProp(PK.PhysAlpha)),
                interpolating: UEValue.ToBool(a.GetProp(PK.bInterpolating)),
                brushExportIndex: brush?.ExportIndex ?? -1,
                brushName:        brush?.Name,
                toWorld:          brush is null ? default : UELevelRuntime.BrushToWorld(a),
                dynamicLightMover: UEValue.ToBool(a.GetProp("bDynamicLightMover")));
        }

        // MultiSkins[8] texture array → per-slot asset paths (null for empty
        // slots). UE1 stores it as a fixed 8-element array; the prop bag holds
        // an IList (declared static arrays are preallocated to ArrayDim by
        // LoadDefaults, so a placed actor projects all 8 slots, empty ones
        // null). Only an object without the declaration (struct bags,
        // synthetic test objects) projects the empty list.
        //
        // The skin path list is cached per actor MultiSkins array: it rarely changes, and a new List per
        // actor per frame produced ~200 KB of garbage per tick. A built list is never mutated - any element
        // change builds a new one, so frames the frontend is still reading are unaffected.
        sealed class SkinCache { public object[] Elems; public IReadOnlyList<string> Paths; }
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<System.Collections.IList, SkinCache> _skinCache = new();
        static IReadOnlyList<string> ProjectMultiSkins(object v)
        {
            if (v is not System.Collections.IList list || list.Count == 0) return _noSkins;
            int n = list.Count;
            SkinCache c = _skinCache.GetValue(list, _ => new SkinCache());
            if (c.Paths is not null && c.Elems.Length == n)
            {
                bool same = true;
                for (int i = 0; i < n; i++) if (!ReferenceEquals(c.Elems[i], list[i])) { same = false; break; }
                if (same) return c.Paths;
            }
            var elems = new object[n];
            var skins = new string[n];
            for (int i = 0; i < n; i++) { elems[i] = list[i]; skins[i] = AssetPath(elems[i]); }
            c.Elems = elems; c.Paths = skins;
            return skins;
        }

        static readonly AudioCommand[] _noAudio = System.Array.Empty<AudioCommand>();

        // Project a whole actor list into a frame (live, non-destroyed actors),
        // the viewport camera, and the audio commands buffered since the last
        // frame (drained by the caller). The camera comes PRECOMPUTED from the
        // caller (review #10 P2): PlayerCalcView is script, so the frame
        // builder evaluates it once per core tick and passes the snapshot in —
        // this projector stays free of script execution. (Music is pure prop
        // reads — no script — so it is projected here.)
        static readonly HudCommand[] _noHud = System.Array.Empty<HudCommand>();
        static readonly ParticleInstance[] _noParticles = System.Array.Empty<ParticleInstance>();

        // SNAPSHOT STORAGE IS REUSED. A list of ~1300 snapshots of a few hundred bytes each is a megabyte
        // EVERY frame (LOH under .NET, a collection per frame under Mono/Boehm). The owner allocates
        // ProjectScratch ONCE and passes it here: three buffers in rotation, so frames N-1 and N-2 stay
        // intact while N is built, and frame N-3's storage IS OVERWRITTEN. This is the WorldFrame.Actors
        // contract: a consumer reads the current frame (and may compare with previous ones) but does not
        // keep the list longer than three frames - a snapshot needed longer is copied (ActorSnapshot is a
        // struct). Contract pin: SelfTest `worldframe_actor_storage_double_buffered`.
        public sealed class ProjectScratch
        {
            // THREE buffers (frame preparation on a separate thread): Core builds N while the frontend still
            // reads N-1, and N-2 may hold the frame being drawn; N-3 is overwritten.
            internal readonly List<ActorSnapshot>[] Buffers = { new List<ActorSnapshot>(), new List<ActorSnapshot>(), new List<ActorSnapshot>() };
            internal int Index;
        }

        static int _projectStamp;

        public static WorldFrame ProjectWorld(int tick, string label,
            IEnumerable<UEObject> actors, CameraSnapshot camera, UEObject player,
            MusicState music, IReadOnlyList<AudioCommand> audio = null,
            IReadOnlyList<HudCommand> hud = null,
            IReadOnlyList<ParticleInstance> particles = null,
            bool highDetailActors = true,
            IReadOnlyList<DecalInstance> decals = null,
            ProjectScratch scratch = null,
            float[] randomBase = null,
            bool drawWorld = true,
            IReadOnlyList<MeshPose> meshPoses = null, IReadOnlyList<ViewFrame> views = null, IReadOnlyList<int> spriteActors = null,
            IReadOnlyList<UEGalaxyAudio.Playback> audioChannels = null, int audioResumeSerial = 0, ActorViewSets actorViews = null,
            IReadOnlyList<CoronaInstance> coronas = null, bool enhancedVisuals = true,
            IReadOnlyList<DrawnWorld> drawn = null, IReadOnlyList<string> drawnTextures = null,
            IReadOnlyList<int> audioCallStops = null, float audioMixDt = 0f, IReadOnlyList<int[]> drawOrder = null,
            int canvasScale = 1)
        {
            // Capacity for the whole list up front: otherwise List doubled from 4 to 2048+ every frame,
            // discarding the previous snapshot array on EACH doubling. Pure growth arithmetic, no behaviour
            // change. With owner buffers (scratch) no array is allocated at all - see ProjectScratch.
            int count = actors is ICollection<UEObject> known ? known.Count : 0;
            List<ActorSnapshot> list;
            if (scratch is null)
            {
                list = new List<ActorSnapshot>(count);
            }
            else
            {
                scratch.Index = (scratch.Index + 1) % scratch.Buffers.Length;
                list = scratch.Buffers[scratch.Index];
                list.Clear();
                if (list.Capacity < count) list.Capacity = count;
            }
            // "Already projected in this walk" is a per-object stamp of this call (a HashSet of ~2k actors per tick
            // before; same membership by reference).
            int stamp = System.Threading.Interlocked.Increment(ref _projectStamp);
            LightAnim.Begin(UEValue.AsFloat((player?.GetProp(PK.Level) as UEObject)?.GetProp(PK.TimeSeconds)), randomBase);
            // SLOT 0 is the level ambient source. The FIRST enumerated actor is remembered, not one found by
            // class: shipped asserts the slot (`Actors(0)->IsA(ALevelInfo)`, UnLevel.h:0xf7-0xf8), it does not search.
            UEObject actors0 = null;
            // ONE SNAPSHOT PER ACTOR. The serialized `ULevel::Actors` of five shipped maps (Lev2_Fire2,
            // Lev2_HogFront, Lev3_Quid2, Lev3_Troll, Lev_Tut3b) holds the same actor in TWO slots, and Core
            // keeps the list as is (see UEActorGraphLoader). The shipped renderer walks the array and draws such
            // an actor twice in the same place - observably identical to one draw - while the frontend proxy
            // registry is keyed by actor identity and would take the second snapshot for a second actor. So the
            // frame carries the actor once. UEObject does not override Equals - HashSet compares by reference
            // (ReferenceEqualityComparer is missing from Unity's netstandard2.1).
            foreach (var a in actors)
                if (a is not null && !a.Destroyed)
                {
                    actors0 ??= a;
                    if (a.FrameProjStamp == stamp) continue;
                    a.FrameProjStamp = stamp;
                    ProjectMemoInto(a, list);
                }
            return new WorldFrame(tick, label, list, camera,
                audio ?? _noAudio, music, hud ?? _noHud,
                particles ?? _noParticles,
                player is { Destroyed: false } ? player.ObjectId : 0,
                ProjectLevelAmbient(actors0), highDetailActors, decals,
                // Pause - from the same slot 0 (LevelInfo) as ambient: shipped asserts the slot, it does not
                // search by class (UnLevel.h:0xf7-0xf8).
                paused: !string.IsNullOrEmpty(actors0?.GetProp(PK.Pauser)?.ToString()),
                viewFlash: ProjectViewFlash(player), drawWorld: drawWorld,
                reverb: ProjectReverb(player), views: views, meshPoses: meshPoses, spriteActors: spriteActors,
                audioChannels: audioChannels, audioResumeSerial: audioResumeSerial, actorViews: actorViews, coronas: coronas,
                enhancedVisuals: enhancedVisuals, drawn: drawn, drawnTextures: drawnTextures,
                audioCallStops: audioCallStops, audioMixDt: audioMixDt, drawOrder: drawOrder, canvasScale: canvasScale);
        }

        // Galaxy UpdateReverb (FUN_106095c4): the zone is Region.Zone (+0xa0) of the viewport actor's
        // ViewTarget or of the actor itself (0x106082fb-0x10608310, the same viewer as UEGalaxyVoiceMix.Listener).
        // The UseReverb gate is on the consumer side (audio subsystem config).
        public static ReverbState ProjectReverb(UEObject viewportActor)
        {
            if (viewportActor is null) return default;
            UEObject viewer = viewportActor.GetProp(PK.ViewTarget) as UEObject ?? viewportActor;
            return ProjectZoneReverb((viewer.GetDeclared("Actor", "Region") as UEObject)?.GetProp(PK.Zone) as UEObject);
        }

        // Structure words for one zone (ZoneInfo or subclass). bReverbZone without loaded Delay/Gain is a
        // load defect, not "no reverb": fail loudly.
        public static ReverbState ProjectZoneReverb(UEObject zone)
        {
            if (zone is null || !UEValue.ToBool(zone.GetDeclared("ZoneInfo", "bReverbZone"))) return default;
            if (zone.GetDeclared("ZoneInfo", "Delay") is not System.Collections.IList d || d.Count != 6
                || zone.GetDeclared("ZoneInfo", "Gain") is not System.Collections.IList g || g.Count != 6
                || zone.GetDeclared("ZoneInfo", "MasterGain") is null || zone.GetDeclared("ZoneInfo", "CutoffHz") is null)
                throw new System.InvalidOperationException(
                    $"zone reverb {zone.GetProp(PK.Name)}: bReverbZone, but MasterGain/CutoffHz/Delay[6]/Gain[6] are not loaded (UpdateReverb +0x300..+0x313)");
            System.Span<byte> db = stackalloc byte[6], gb = stackalloc byte[6];
            for (int i = 0; i < 6; i++) { db[i] = UEValue.AsByte(d[i]); gb[i] = UEValue.AsByte(g[i]); }
            return ReverbState.FromZoneBytes(UEValue.AsByte(zone.GetDeclared("ZoneInfo", "MasterGain")),
                UEValue.ToInt(zone.GetDeclared("ZoneInfo", "CutoffHz")), db, gb);
        }

        // The viewport actor's FlashFog as stored in Props (UEPlane; the consumer clamps per the
        // UGameEngine::Draw rule). No player - Valid=false.
        public static ViewFlashState ProjectViewFlash(UEObject player)
        {
            if (player is null || player.Destroyed) return default;
            UEPlane p = UEValue.AsPlane(player.GetProp(PK.FlashFog));
            return new ViewFlashState(p.X, p.Y, p.Z, p.W);
        }

        // UGameEngine::Draw 0x1039fab6..0x1039fb07 — the view: pre-fill from the player pawn
        // (location/rotation/FovAngle), then run PlayerCalcView(out ViewActor,
        // out CameraLocation, out CameraRotation); the pawn's override (HP1
        // Harry.uc:1108: redirect to ViewTarget when set) flows back through
        // the out-params. FOV is the pawn's FovAngle (SE reads it from the
        // viewport actor, PlayerCalcView never touches it). Read-only — no
        // world mutation, no journal writes.
        public static CameraSnapshot ProjectCamera(UEInterpreter interp, UEObject player)
        {
            if (player is null) return default;   // Valid=false
            object viewActor = player;
            object loc = UEValue.AsVector3(player.GetProp(PK.Location));
            object rot = UEValue.AsRotator(player.GetProp(PK.Rotation));
            float fov = UEValue.AsFloat(player.GetProp(PK.FOVAngle));
            var fn = interp?.FindEventFunction(player, "PlayerCalcView");
            if (fn is not null)
            {
                var args = new[] { viewActor, loc, rot };
                interp.Invoke(player, fn, args);   // out-params written back into args
                viewActor = args[0];
                loc = args[1];
                rot = args[2];
            }
            return new CameraSnapshot(true, IdOf(viewActor),
                UEValue.AsVector3(loc), UEValue.AsRotator(rot), fov,
                UEValue.ToBool(player.GetProp(PK.bBehindView)));
        }
    }
}
