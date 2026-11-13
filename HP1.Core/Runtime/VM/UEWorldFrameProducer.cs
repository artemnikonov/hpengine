// The shared WorldFrame producer: the one Core runtime object that owns frame
// building — the audio command buffer, the per-tick frame/camera/HUD caches,
// the Canvas object and the Canvas draw/measure machinery. Every frontend
// drives the SAME producer; none owns a second implementation. Frontends
// supply only their actor enumeration, the local player and (headless) the
// journal rendering of emitted audio.
using System;
using System.Collections.Generic;
using System.IO;
using HP1.Core;
using UELib;

namespace HP1.UEScript
{

    public sealed partial class UEWorldFrameProducer
    {
        // Viewport dims are FRONTEND state: the headless takes
        // the retail ini's windowed resolution as its viewport; a windowed
        // frontend supplies its ACTUAL surface each frame via SetViewport —
        // shipped likewise sizes the canvas from the live viewport, the ini is
        // only the startup default.
        int _viewportX;
        int _viewportY;
        readonly Hp1FontCatalog _fonts;

        // A plain viewport (headless, SetRes) has no interface scale: its canvas, world frame and screen are one size.
        public void SetViewport(int x, int y) { _viewportX = x; _viewportY = y; _screenX = x; _screenY = y; _canvasScale = 1; }
        // The window's pixel size before a step: Core picks the interface scale (NOT ORIGINAL, D-44 Interface Size:
        // UEConfig.UIScaleFor by the height and [HPEngine] UIScaleStep) and makes the canvas screen / scale. The scale
        // is read here once and travels with the frames built on that canvas (WorldFrame.CanvasScale).
        // Round UP: with an odd remainder (Retina 1257 / 2) rounding the canvas down gave 628·2 = 1256, and cutscene
        // bars drawn by script to the canvas edge left the bottom screen row open.
        public void SetScreen(int screenW, int screenH)
        {
            int scale = UEConfig.UIScaleFor(screenH, UEConfig.Global?.UIScaleStep ?? 0);
            SetViewport((screenW + scale - 1) / scale, (screenH + scale - 1) / scale);
            _canvasScale = scale;
            _screenX = screenW; _screenY = screenH;
        }
        int _canvasScale = 1, _screenX, _screenY;

        // THE WORLD FRAME IS THE WINDOW, NOT THE CANVAS. Shipped DrawWorld gets the viewport's FSceneNode (Engine.dll.c
        // 117935-117939), the same size as the canvas, i.e. the window pixels. D-44 scales only the interface: the canvas
        // is screen / scale, but OccludeBsp rasterises its span buffer at the frame size (UEOcclusion Raster, f.Y rows),
        // so a canvas-sized world frame (854x480 at 2560x1440) dropped polygons the window shows (campaign, 181 hp1drawlog
        // cameras: 400 level nodes + 27 mover polygons on 111 cameras; holes visible in the frame).
        // The console border (ConsolePreRender, canvas units) scales with the interface; without it the world frame is
        // exactly the window. Check: --worldframecheck.
        (int X, int Y) WorldFrameSize(int frameX, int frameY)
            => (frameX == _viewportX ? _screenX : Math.Min(frameX * _canvasScale, _screenX),
                frameY == _viewportY ? _screenY : Math.Min(frameY * _canvasScale, _screenY));
        public int CanvasScale => _canvasScale;
        // The canvas size is READ by probing the real window (HP1ViewportProbe), which checks
        // the link "applied window size == frame canvas".
        public int ViewportX => _viewportX;
        public int ViewportY => _viewportY;

        // RENDER DEVICE value for the bHighDetail gate (see WorldFrame).
        // Set from the install config by the same owner as the rest of the
        // world state; default `true` — that is what ALL hardware sections of
        // the shipped ini have (Glide/Metal/OpenGL/D3D), False only for the
        // software device. The value is not guessed: the real source is the
        // section named by Engine.Engine.GameRenderDevice.
        public bool HighDetailActors = true;

        // NOT ORIGINAL (Original / Enhanced visual mode, D-27/D-48/D-53/D-54): read HERE, once per frame at the
        // start of Build — the one owner — from [HPEngine] VisualMode only (UEConfig.VisualModeIsOriginal; no menu item,
        // the player always gets Enhanced). NOT from PlayerPawn.ObjectDetail: the shipped Options item "Object Detail"
        // writes it (FEOptionsPage.uc:589-622) and nothing reads it in 1.53. Enhanced turns on the
        // runtime-switchable visual improvements: newborn fire ramps in substeps (D-54, AdvanceNewborn) and, in the
        // frontend via WorldFrame.EnhancedVisuals, anisotropy (D-53) and trilinear mips (D-48). Original = shipped.
        public bool EnhancedVisuals = true;

        // Audio commands buffered since the last consumed tick. The frontend's
        // sound natives emit here (and render their own journal line).
        readonly List<AudioCommand> _audio = new();
        // HUD commands of the current drive.
        readonly List<HudCommand> _hud = new();
        HudCommand[] _hudCache = Array.Empty<HudCommand>();
        // HUD bytecode runs once: a frontend whose renderer calls myHUD.PostRender
        // itself sets this false; headless = on.
        public bool DriveHudEnabled = true;
        // VIEWPORT CONSOLE AND ITS CLOCK DELTA. The console half of
        // UGameEngine::Draw (Engine.dll.c:117791..): Console->PreRender(Frame)
        // (vtbl+0x5c, 0x1039fe59) before Canvas->Update and Actor.PreRender, and
        // Console->PostRender(Frame) + Console.eventPostRender(Canvas) after
        // Actor.PostRender. Inside UConsole::PreRender (Engine.dll.c:91467) the
        // console TICKS: eventTick((Viewport.CurrentTime -
        // Viewport.LastUpdateTime)·2^-32) — the VIEWPORT clock delta, not the level's
        // (no TimeDilation, and also while paused). Set by RunEngineFrame.
        public UEObject Console;
        public float ViewportDelta;
        public int ConsoleTickCount, ConsolePostRenderCount;
        // How many times the HUD BYTECODE actually ran. Counted HERE, because this is
        // where it runs: a frontend counter would measure redraws, and would
        // have no writer at all, making "at most one execution" trivially pass.
        // The invariant is exact: exactly one execution per engine frame, so compare
        // WITH EQUALITY, not "at most".
        public int HudDriveCount;
        // Decal attachments are read AFTER the Update leg of the same frame, so
        // what is supplied here is a snapshot function, not a list: a list assigned
        // in advance would lag exactly one frame.
        public Func<IReadOnlyList<DecalInstance>> DecalSnapshot;
        // Frame decals in DrawFrame (UELevelRuntime.DrawFrameDecals) and reset of pieces for a new frame.
        public Action<UESceneNode, int> DrawDecals;
        public Action BeginDecalFrame;

        // ── PARTICLE BOUNCE OFF THE WORLD (UParticle::Update, Engine.dll.c:93921-93978) ──
        // Two level queries owned by UELevelRuntime, which binds them here
        // before every frame: UModel::FastLineCheck (arguments as in
        // shipped — End, Start) and ULevel::SingleLineCheck (Start, End,
        // TraceFlags=6 = Movers|Level, no source and no extent). The producer does
        // not hold the level, hence hooks; with Elasticity > 0 and an unbound
        // hook — a loud STOP, not a skipped branch.
        // Owner vertices in the FRAME pose (GetFrame with unit coords,
        // DIST_OwnerMesh branch 23386-23402) — computed by Core, bound by the runtime.
        public Func<UEObject, UEVector[]> PosePoints;
        public Func<UEVector, UEVector, bool> ParticleFastLineCheckClear;
        public Func<UEVector, UEVector, ParticleLineHit> ParticleSingleLineCheck;
        // AWind::GetTotalWind(point) — owner UELevelRuntime.TotalWind.
        public Func<UEVector, UEVector> TotalWind;
        public readonly struct ParticleLineHit
        {
            public readonly bool World;        // hit on LevelInfo (world/BSP)
            public readonly UEVector Location; // HitLocation
            public readonly UEVector Normal;   // HitNormal
            public ParticleLineHit(bool world, UEVector location, UEVector normal)
            { World = world; Location = location; Normal = normal; }
        }
        // How many shadows got Update in this run. A fixture must be able to
        // say the branch RAN, not only that the verdict is green.
        public int ShadowUpdateCount;
        // Whether Render->DrawWorld ran in the last frame (frame gate and
        // Console.bNoDrawWorld) — observable for fixtures.
        public bool LastDrawWorld;
        // Inputs of the last frame's DrawWorld gate: frame size and bNoDrawWorld.
        public (int X, int Y, bool NoDrawWorld) LastDrawWorldGate;
        // Render FGlobalRandoms (Render.dll 0x10b6716c): lives for the whole process.
        public readonly UEGlobalRandoms GRandoms = new UEGlobalRandoms();
        // Frame state for lightmaps (SurfLightmap.FrameFx) — captured in
        // Build right after GRandoms.Tick; null while the frame has no LevelInfo.
        public SurfLightmap.FrameFx LightmapFx { get; private set; }
        // Index of the native that blocks the shadow leg (0 — it works).
        // A field, not only a log line: a human reads the line, while acceptance
        // must be able to check this STRUCTURALLY — otherwise "the debt is named"
        // stays an unfalsifiable promise.
        public int ShadowsBlockedByNative;
        // Full list of open dependencies of the production shadow route.
        // Empty — the route runs; non-empty — the leg does not work at all.
        public string[] ShadowRouteBlockers = System.Array.Empty<string>();
        readonly List<string> _shadowBlockers = new();

        // TWO GATES THAT MUST BE CLOSED BEFORE THE ROUTE IS ENABLED.
        // These are NOT behaviour switches or settings: they are set to true
        // exactly when the corresponding work is done and proven, and each
        // removes its item from the blocker list above.
        //
        // SetupDynamics shadow selection (frame frustum before occlusion): ported
        // (UERenderVisibility), proven by executing shipped.
        // `const`, not a field: a field could be flipped from outside by one assignment
        // and the route would turn on WITHOUT the port. Changing the constant is a source
        // edit.
        public const bool ShadowVisibilityTraversalPorted = true;
        // Shadow presentation (URender::DrawDecals/ClipDecal, Render.dll.c:1455-1530,
        // 2905-3285):
        //   * the decal path does NOT read Opacity: the only read of +0x194 in
        //     DrawFrame (0x10b266b0) is the sprite one ([edi+0x98] = FDynamicSprite.Actor),
        //     ClipDecal reads only ScaleGlow (+0x17c) and AmbientGlow (+0x18c);
        //   * their light (ScaleGlow*0.5 + AmbientGlow/256, clamped 0..1) is not applied by
        //     D3DDrv::DrawTriangles for STY_Modulated — vertex colour 0xFFFFFFFF with
        //     PF_Modulated; SetBlending blend is DESTCOLOR/SRCCOLOR (2·src·dst);
        //   * Style 4 -> 0x40000040, 3 -> PF_Translucent, others -> 0 (DrawDecals).
        //   * ClipDecal clipping by "viewer + FSavedPoly edge" planes over the
        //     FDecal.Nodes nodes (empty — all surface nodes) is Core code
        //     (UEDecalClip, ported from disasm 0x10b27060), checked bit-exact against the real body
        //     under unicorn;
        //     the DrawDecals gate (SpanBased, Client.Decals, PolyFlags & 0x8000000)
        //     read from disasm 0x10b26390-0x10b263af. Pieces travel in the frame
        //     (DecalInstance.Pieces); the frontend only draws them.
        public const bool ShadowPresentationSemanticsResolved = true;
        bool _shadowDebtAnnounced;
        CameraSnapshot _cameraCache, _audioCamera;
        int _cacheTick = int.MinValue;
        bool _cacheValid;
        WorldFrame _frameCache;
        // Breakdown of the LAST Build by section, ms: frontends add it to their
        // fps line. The stopwatch costs ~20 ns per mark.
        public double LastCameraMs, LastHudMs, LastParticlesMs, LastAmbientMs, LastDecalsMs, LastProjectMs;
        static readonly double MsPerTick = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        // ALLOCATION MEASUREMENT (HP1_TICK_PROF): heap bytes per Core frame phase.
        public static readonly System.Collections.Generic.Dictionary<string, long> AllocBy = new();
        static long _allocMark;
        public static void AllocNote(string phase)
        {
            if (!UELevelRuntime.ProfileTicks) return;
            long now = System.GC.GetAllocatedBytesForCurrentThread();
            AllocBy.TryGetValue(phase, out long v); AllocBy[phase] = v + (now - _allocMark); _allocMark = now;
        }
        static long Section(ref double slot, long since)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            slot = (now - since) * MsPerTick;
            return now;
        }
        readonly WorldFrameProjector.ProjectScratch _projectScratch = new();   // snapshot buffers, see ProjectScratch
        UEObject _canvas;

        // [Galaxy.GalaxyAudioSubsystem] from the shipped Default.ini (AmbientFactor 0.7,
        // DopplerSpeed, ReverseStereo, UseSurround) — UEGalaxyConfig.
        public readonly UEGalaxyConfig Galaxy;

        public UEWorldFrameProducer(string systemDir, int viewportX, int viewportY)
        {
            SetViewport(viewportX, viewportY);
            _systemDir = systemDir;
            SpriteTextureSize = DefaultSpriteTextureSize;
            _fonts = new Hp1FontCatalog(systemDir);
            Galaxy = UEGalaxyConfig.Load(systemDir);
            Audio = new UEGalaxyAudio(new GalaxyWorld(this));
            UkParticleWalk = UEEdition.OfSystem(systemDir).Render == UEEdition.RenderSet.UK11;
        }

        // The UK11 Render.dll (an earlier build) draws particles without the density fade
        // and with a LOD cut in the walk (ParticleDrawerFade). Selected by the user's Render.dll; every other build runs
        // the US 1.53 bodies.
        internal bool UkParticleWalk;

        // UGalaxyAudioSubsystem — Core (channel pool, music; UEGalaxyAudio). Lives with the engine, like Engine->Audio:
        // a level change does not recreate it (LoadMap stops its channels: UEGalaxyAudio.SetViewportSame).
        public readonly UEGalaxyAudio Audio;
        // Runtime of the current level — source of the listener, actors and the view actor (set by BindFrameProducerHooks).
        public UELevelRuntime AudioRuntime;

        sealed class GalaxyWorld : IUEGalaxyWorld
        {
            readonly UEWorldFrameProducer _p;
            public GalaxyWorld(UEWorldFrameProducer p) { _p = p; }
            UELevelRuntime Rt => _p.AudioRuntime;
            public UEAudioListener Listener
            {
                get
                {
                    // PlaySoundW gates only on the viewport (Audio+0x7c, 0x10607786-0x1060778b) and reads the viewport
                    // actor; the frame camera is needed only by Update's mixing loop (UEAudioListener.Valid).
                    return UEGalaxyVoiceMix.Listener(Rt?.LocalPlayer, _p._audioCamera);
                }
            }
            public UEGalaxyConfig Galaxy => _p.Galaxy;
            public bool TryGetActor(int actorId, out UEVector location, out UEVector velocity)
            {
                location = default; velocity = default;
                UEObject a = actorId != 0 ? Rt?.FindActorById(actorId) : null;
                if (a is null || a.Destroyed) return false;
                location = UEValue.AsVector3(a.GetProp(PK.Location));
                velocity = UEValue.AsVector3(a.GetProp(PK.Velocity));
                return true;
            }
            // Core config state; until it is loaded — the shipped ini default (Default.ini:159).
            public float MusicMaster => Rt?.Config?.MusicMaster ?? 128f / 255f;
            public UEGalaxyAudio.SoundInfo? Sound(string path)
            {
                if (UESoundCatalog.Shared.Root is null) return null;
                var info = UESoundCatalog.Shared.LoadInfo(path);   // header only: the voice's stream decodes as it is fed
                return info is { } i ? new UEGalaxyAudio.SoundInfo(i.Frames, i.Rate, i.Channels) : null;
            }
            public UEGalaxyAudio.SoundInfo? Song(string path)
            {
                try
                {
                    UEMp2SoundStream st = UEMusicCatalog.Shared.OpenStream(path);
                    return new UEGalaxyAudio.SoundInfo(st.LoopSamples / Math.Max(1, st.Channels), st.SampleRate, st.Channels);
                }
                catch (Exception e)
                {
                    HP1.Core.UETrace.Warn($"[music] '{path}' not resolved: {e.Message}");
                    return null;
                }
            }
            public bool WriteMusicFinished() => Rt is not null && Rt.ReportMusicFinished(null);
            public void WriteSongFinishedFlag(bool finished) => Rt?.ReportSongFinishedFlag(finished);
            public void WriteMusicStarted() => Rt?.ReportMusicStarted();
            public void WriteViewportRestored() => Rt?.NoteAudioViewportRestored();
            public void NoteAudioChannelsLost() => _p.NoteAudioChannelsLost();
        }

        // Observation hook FOR FIXTURES ONLY — same technique as
        // UELifecycle.EventProbe. Observing a command through the built frame
        // would also measure the frame mechanics, making a product defect
        // indistinguishable from an observation defect. Production never sets it.
        public static Action<AudioCommand> AudioProbe;

        public void EmitAudio(in AudioCommand cmd)
        {
            _audio.Add(cmd);
            AudioProbe?.Invoke(cmd);
            Audio.Execute(cmd);   // Audio->PlaySound/StopSound/ModifySound — synchronous device call
        }

        // Galaxy Update ambient loop command — executed by the pool at emission time.
        void Ambient(in AudioCommand cmd) { _audio.Add(cmd); Audio.Execute(cmd); }

        // ── The Core-owned ambient loop ─────────────────
        // Galaxy StartAmbience/UpdateAmbience (Update 0x10608365-0x10608679) with
        // the DECISIONS in Core: per frame, an ambient actor whose
        // WorldSoundRadius reaches the view actor gets AmbientStart (PlaySound
        // with volume SoundVolume*AmbientFactor/255, pitch = SoundPitch/64,
        // slot = SLOT_Ambient(4)); every playing one — INCLUDING the one just
        // started, because the refresh loop runs right after the start loop in
        // the SAME Update — gets AmbientUpdate with the refreshed channel
        // Volume/Radius/Pitch (UEGalaxyVoiceMix.AmbientVolume: doubling and
        // modulation by actor light); leaving range / dying / changing sound gets
        // AmbientStop.
        readonly Dictionary<UEObject, string> _ambientPlaying = new();

        // The audio device lost its viewport (Galaxy SetViewport(NULL) silences all
        // channels, Galaxy.dll.c:5093-5140): no ambients are playing any more, and the
        // start loop of the next Update restarts them (the channel lookup by id will
        // not find them). Called in the frame sync window.
        public void NoteAudioChannelsLost() => _ambientPlaying.Clear();
        readonly float[] _audioRandomBase = new float[256];

        void AmbientPass(IEnumerable<UEObject> actors, UEObject player)
        {
            UEObject view = player?.GetProp(PK.ViewTarget) as UEObject ?? player;
            if (view is null) return;
            // Pause gate: shipped stops ambience while the
            // level is paused (LevelInfo.Pauser non-empty) or ShowFlags & 0x4800 is clear (Galaxy.dll.c:6936-6948).
            // Everything playing
            // gets AmbientStop; nothing starts or updates until unpause.
            // Galaxy 0x106082fb-0x10608354: ambience is on only if the VIEWPORT
            // ACTOR has ShowFlags & 0x4800 (`test ch,0x48`) and its Level.Pauser
            // is empty; otherwise there is no start, and the update loop silences playing
            // ambients (local_38 = 0 -> StopSound, 0x1060867f).
            int showFlags = UEValue.ToInt(player.GetProp(PK.ShowFlags));
            string pauser = (player.GetProp(PK.Level) as UEObject)?.GetProp(PK.Pauser)?.ToString();
            if ((showFlags & 0x4800) == 0 || !string.IsNullOrEmpty(pauser))
            {
                foreach (var kv in _ambientPlaying)
                    Ambient(new AudioCommand(AudioKind.AmbientStop, kv.Key.ObjectId,
                        kv.Key.GetProp(PK.Name)?.ToString(), kv.Value, 4, 0f, 0f, 0f, false,
                        UEValue.AsVector3(kv.Key.GetProp(PK.Location))));
                _ambientPlaying.Clear();
                return;
            }
            UEVector at = UEValue.AsVector3(view.GetProp(PK.Location));
            // ShowFlags bit 11 of the viewport actor — the realtime flag in GlobalLighting
            // (0x10608620-0x10608632).
            bool realtime = ((showFlags >> 11) & 1) != 0;
            var seen = new HashSet<UEObject>();
            foreach (var a in actors)
            {
                if (a is null || a.Destroyed) continue;
                string path = a.GetProp(PK.AmbientSound) is UEResourceRef rr
                    ? (rr.ObjectPath ?? rr.Name) : null;
                if (path is null) continue;
                float radius = UEGalaxyVoiceMix.AmbientRadius(a);
                UEVector loc = UEValue.AsVector3(a.GetProp(PK.Location));
                bool inRange = UEGalaxyVoiceMix.AmbientInRange(loc, at, radius);   // 0x106083a0-0x106083fa
                float pitch = UEGalaxyVoiceMix.AmbientPitch(a);
                bool playing = _ambientPlaying.TryGetValue(a, out string playingPath);
                if (!inRange) continue;
                // AmbientSound changed on a playing actor: the start looks up the channel
                // ONLY by id Index*16+8 (Galaxy 0x10608400-0x10608432) and finds it,
                // while the update loop — after ALL starts of this Update — compares the
                // channel's sound (+0x10) with the actor's AmbientSound (+0x94, 0x106085af)
                // and on mismatch StopSounds the channel (0x1060867f): not marked seen, so
                // the stop pass below silences it. The new sound starts in the NEXT Update.
                if (playing && playingPath != path) continue;
                if (!playing)
                {
                    // Looping is decided by the sound DATA, as for PlaySound: an ambient start is
                    // the same PlaySoundW (vtable+0x78, 0x106084ac -> 0x10607730), with no loop
                    // flag in the arguments; the USound::newSoundStream stream rewinds
                    // only with FSoundData +0x54 (Engine FUN_1037e210).
                    // A non-looping one ends, and the next Update starts it again.
                    object ambSound = a.GetProp(PK.AmbientSound);
                    bool looping = ambSound is UESoundRef sref ? sref.Looping
                        : UESoundCatalog.Shared.Root is not null
                          && (UESoundCatalog.Shared.Load(path, mayFail: true)?.Looping ?? false);
                    Ambient(new AudioCommand(AudioKind.AmbientStart, a.ObjectId,
                        a.GetProp(PK.Name)?.ToString(), path, 4,
                        UEGalaxyVoiceMix.AmbientStartVolume(a, Galaxy), radius, pitch, false, loc,
                        looping: looping));
                    _ambientPlaying[a] = path;
                }
                // Refresh of a playing channel (0x106085cb-0x10608676) — in the same
                // Update as the start: by mixing time the channel volume is already
                // recomputed.
                Ambient(new AudioCommand(AudioKind.AmbientUpdate, a.ObjectId,
                    a.GetProp(PK.Name)?.ToString(), path, 4,
                    UEGalaxyVoiceMix.AmbientVolume(a, Galaxy, realtime, _audioRandomBase),
                    radius, pitch, false, loc, startVolume: UEGalaxyVoiceMix.AmbientStartVolume(a, Galaxy)));
                seen.Add(a);
            }
            var stops = new List<UEObject>();
            foreach (var kv in _ambientPlaying)
                if (!seen.Contains(kv.Key)) stops.Add(kv.Key);
            foreach (var dead in stops)
            {
                Ambient(new AudioCommand(AudioKind.AmbientStop, dead.ObjectId,
                    dead.GetProp(PK.Name)?.ToString(), _ambientPlaying[dead], 4, 0f, 0f, 0f, false,
                    UEValue.AsVector3(dead.GetProp(PK.Location))));
                _ambientPlaying.Remove(dead);
            }
        }

        // Seam for the clipping fixture: on the Tut1 route both *Clipped commands
        // arrive with Clipped=0, so the clip arithmetic can be checked ONLY
        // synthetically. The fixture builds its own canvas, calls DrawTile directly and
        // reads the emitted commands.
        public IReadOnlyList<HudCommand> HudForTest => _hud;
        public void ClearHudForTest() => _hud.Clear();

        // Everything here is keyed on actors of a SPECIFIC level, so it
        // dies with it (called from UELevelRuntime.ResetWorld).
        //
        // Otherwise after travel the new level would come up on top of the old
        // state: live particles of removed emitters, ambient for removed ZoneInfos,
        // undelivered audio commands, the HUD cache and, most quietly, the frame cache —
        // `_cacheValid` on a matching tick number would return the OLD WorldFrame for the
        // new world. The Canvas goes too: it was created on the previous UEInterpreter.
        public void ResetForNewLevel()
        {
            _fx.Clear();
            _ambientPlaying.Clear();
            _audio.Clear();
            Audio.ClearWorld();
            _hud.Clear();
            _hudCache = Array.Empty<HudCommand>();
            _cameraCache = default;
            _frameCache = null;
            _cacheValid = false;
            _cacheTick = int.MinValue;
            _canvas = null;
        }

        // Engine .rdata 0x104783e8: the ten priorities EmitParticles deals out in turn (0x103c351c `mov ecx, [4*eax +
        // 0x104783e8]`, eax = the emitter's CurrentPriorityTag).
        static readonly int[] PriorityTable = { 9, 3, 1, 7, 5, 0, 6, 2, 4, 8 };
        readonly Dictionary<UEObject, FxState> _fx = new();

        // Presentation MSVCRT stream (shipped appFrand/appRandRange — shipped uses the
        // shared CRT rand, the port a separate render stream).
        // Set by the frontend via UENativesCore.BindFrameProducer.
        public Func<float> Frand;
        public Func<int, int, int> RandRange;
        // MESH CATALOG FOR THE DIST_OwnerMesh BRANCH. Supplied by the runtime the same
        // way as HighDetailActors — from RunEngineFrame, so the decision has ONE owner,
        // not one per frontend. Read by the DIST_OwnerMesh branch below
        // (Render.dll.c 23386-23402).
        public Hp1SkeletalMeshCatalog MeshCatalog;
        // The float-range CRT sample — shipped appFrand(float,float)
        // (Core 0x10126170, called from Engine.dll.c:23527): rand()·C·(max−min) + min,
        // C = 1/32767 (Core 0x1017ddb0), 24-bit render precision; exactly ONE
        // rand() per call, unconditionally (min==max still consumes one).
        public Func<float, float, float> FrandRange;
        // Render-stream rand() for ported bodies (the same stream as Frand).
        public Func<int> RandInt;
        // Level render pass (RenderLevelPass) for the frame viewer; set by
        // UELevelRuntime.RunEngineFrame. null — no world.
        public Func<UEObject, ITranslatedRenderPass> RenderPassFor;
        // The pass that built the last frame (null — none). A fixture
        // must show that the compared branch ran.
        public ITranslatedRenderPass LastRenderPass { get; private set; }
        bool _noPassAnnounced;

        // The GMath sine TABLE (16384 entries over the full circle) the
        // shipped emission uses for the spread direction (23511; disasm
        // 0x103c2c6a-0x103c2d2e: `fld dword [GMath(0x10608ca0) + 4*idx + 0x8c]`,
        // idx = units>>2 & 0x3fff; +0x4000 units = a quarter turn = cosine).
        // The table itself is FGlobalMath::TrigFLOAT, built once by the Core.dll
        // ctor — BrushCoords.SinTab owns that copy; recomputing it in float
        // differed from it in 8997 of 16384 entries.
        const float DegToRad = 0.01745329f;          // shipped literal (23502)
        const float RadToUnits = 65536f * 0.15915494f; // rad → rotator units
        // The frontend's actor-destroy route (the render-side self-destroy
        // variant fires from Update when the budget is spent and no particles
        // remain — Engine.dll.c:106702 tail).
        public Action<UEObject> DestroyActor;

        // 0.003921569 (Engine .rdata, the FColor -> float factor of EmitParticles and UParticle::Update).
        const float ColorByteScale = 0.003921569f;

        // The default object of the emitter's PARENT class (see the ParentBlend notes).
        // Cached per class: shipped reads a ready object by pointer, i.e. the same
        // one for all emitters of the class, and rebuilding it every frame
        // would cost a defaults parse per call.
        readonly Dictionary<UELib.Core.UClass, UEObject> _parentDefaults = new(UERefComparer<UELib.Core.UClass>.Instance);
        SlotKind _slotKind; float _slotChaos, _slotSpin;
        // The render-path slot's current value (ChaosTimer, SpinPos bits as floats).
        internal float RenderSlotChaos, RenderSlotSpin;
        // See EmitParticles' first-particle slot: (index the new particle takes in the list, GetRenderBoundingBox path,
        // the port's slot value) -> the value the live original read instead, or null.
        internal Func<int, bool, (float, float), (float, float)?> FirstNewbornSlotForTest;
        // Validation seam: the owner-mesh vertices EmitParticles samples (DIST_OwnerMesh), as read.
        internal Action<UEObject, UEVector[]> OwnerMeshPointsForTest;
        // The GetRenderBoundingBox path is running (ParticleRenderBoundingBox -> RenderStepFx; the reference mirror's
        // Update bridge sets it for a call from the translated GetRenderBoundingBox, return address 0x103c1171).
        bool _grbbPath;
        bool _setupResidueKnown; float _setupResY, _setupResZ;
        // Diagnostics: first particles born from the render-path chain / the Setup residue / the bSystemRelative
        // residue; GetRenderBoundingBox-path Updates before any Setup BoundVisible of the session (session-start 0, 0).
        public int ChainParticleSlot, SetupResidueParticleSlot, SysRelParticleSlot, NoSetupResidueYet;

        // Third-person weapon ObjectId -> the pawn whose DrawActorSprite draws it (this tick). The weapon's DrawMesh gets the
        // PAWN's FDynamicSprite (DrawActorSprite 0x10b3306e: `mov edx, [ebp+0xc]` — its own sprite argument — pushed for
        // the weapon call 0x10b33085), so SetupForActor reads that sprite's +0xbc / +0xb8 (DrawMesh 0x10b0f3f7..0x10b0f41b).
        public readonly Dictionary<int, int> WeaponPawns = new();

        // SwARc Engine measurement tap: one UParticle::Update call (emitter, particle, dt,
        // damping factor, Accel = UpdateParticles' param_1, Wind = param_3, enter); null in production.
        public static Action<UEObject, Particle, float, float, UEVector, UEVector, bool> ParticleStepTap;

        // AParticleFX::Update(0) — the COMPLETE shipped body
        // (Engine.dll.c:106702..106825):
        //   dt = Age - ElapsedTime (render catches up to the game tick),
        //   ElapsedTime += dt ALWAYS;
        //   steady catch-up clamp (bSteadyState && Location ==
        //   LastUpdateLocation && dt>1/15):
        //     target = (pps.Base+max(pps.Rand,0))*(Life.Base+max(Life.Rand,0)),
        //     capped by ParticlesAlive; dt = min(dt, lifeMax,
        //     max((target-alive)/(pps.Base+min(pps.Rand,0)), 1/15)). The FIRST
        //     update fails the location gate (ctor-zeroed LastUpdateLocation)
        //     — the InitExecution prime burst passes UNCLAMPED and back-fills
        //     a whole lifetime window of aged particles;
        //   bShellOnly => the sim quantum is forced to 0.1; a plain emitter
        //     with dt<=0 RETURNS unsimulated (particles freeze on pause
        //   bShellOnly => the sim quantum is forced to 0.1; a plain emitter
        //     with dt<=0 RETURNS unsimulated (particles freeze on pause
        //     frames);
        //   UpdateParticles(dt) FIRST, then the budget gate:
        //     Emitted < Max (or Max==0): bEmit -> EmitParticles(dt), else
        //       LastEmitLocation = Location (tracking only);
        //     budget spent && no particles alive -> DestroyActor (the
        //       RENDER-side self-destroy variant) and return;
        //   final Location/Rotation snapshots into LastUpdate*.
        // AParticleFX::Update(0) (Engine ?Update@AParticleFX@@QAE_NM@Z, Engine.dll.c:106704). Return value as in
        // shipped: false only on the self-destruct path of a spent emitter, otherwise true.
        // Observer of entry into the Update(0) body (fixtures use it to separate their
        // part of the render RNG stream). Does not affect behaviour.
        public Action<UEObject> RenderStepFxEntered;
        // SetupDynamics order diagnostics: the actor whose shadow is about to get Update — before the call.
        public Action<UEObject> ShadowUpdateEntered;

        // Measurement tap for the SwARc judge harness: observes the
        // call (producer, emitter, enter, result at exit); null in production.
        public static Action<UEWorldFrameProducer, UEObject, bool, bool> ParticleUpdateTap;

        // ── The billboard drawer's density fade (Render.dll FUN_10b184d0: slot 1 of the FParticleDrawer vtable 0x10b388f8,
        // the last pass of DrawParticleSystem for RenderPrimitive 0..3 without bShellOnly, switch 0x10b15919) ─────────
        // Per particle of the walk (p0 twice, FUN_10b15090 First/Next) the drawer projects the centre into the frame:
        // camera point (P − Origin)·axes (0x10b184fd..0x10b1855d), culled when Z < 1 (0x10b18576..0x10b18584: C0, NaN
        // too) or when the quad with half sizes W/2, L/2 (rotated by SpinPos when SpinRate != 0: |cos|, |sin| extents) lies
        // wholly outside one frustum side (sign bit of (Prj·Z ± X) + extent, 0x7fffffff < bits). Otherwise area =
        // ((Length·Width)·s)·s, s = ProjZ / Z; x = (Priority + 1)·area·0.25; budget = FY·FX·URender[+0x5c]·0.5 (the
        // drawer ctor FUN_10b16a10 and DrawParticleSystem 0x10b159f9..0x10b15a20; URender+0x5c = 1.0: URender::Init, the
        // FinishMasterFrame clamp to [0.01, 1]). x > budget: Alpha = min(Alpha, (2·budget − x) / budget)
        // (0x10b18c06..0x10b18c2e, a GAME write: the particle keeps it), and Alpha <= 0 culls it (0x10b18c31..0x10b18c4d).
        // Render window PC24: every operation rounds to float. A NULL Textures[0] returns before the walk (0x10b150c3).
        // The frame draws each emitter per draw call, with the particles this pass kept and their Alpha at the call.
        public readonly List<(UEObject Fx, int View, List<(Particle P, float Alpha)> Kept)> ParticleDraws = new();

        // Measurement tap for the SwARc judge harness: the billboard pass of one draw call, observed at
        // entry and exit (ParticleDraws[^1] holds its result); null in production.
        public static Action<UEWorldFrameProducer, UEObject, UESceneNode, bool> ParticleDrawTap;

        // The WHOLE frame is cached per EXPLICIT core tick (
        // keyed on the caller's tick sequence, NOT Level.TimeSeconds: two
        // different ticks with an unchanged clock, e.g. pause or special
        // update modes, must produce fresh frames): a repeat same-tick read
        // returns the SAME frame object — including its Audio list, which
        // would otherwise be drained by the first read and silently lost to a
        // consumer that reads twice. The buffer drains only on a tick change.
        int _lastViewActorId = int.MinValue;

        // The appEnableFastMath(1) window opened inside BuildFrame is closed even when the frame throws: a flag left on would
        // run the next tick's logic arithmetic in PC24.
        public WorldFrame Build(int tick, string label,
            IEnumerable<UEObject> actors, UEInterpreter interp, UEObject player)
        {
            try { return BuildFrame(tick, label, actors, interp, player); }
            finally { FastMathWindow = false; }
        }

        WorldFrame BuildFrame(int tick, string label,
            IEnumerable<UEObject> actors, UEInterpreter interp, UEObject player)
        {
            if (_cacheValid && tick == _cacheTick)
                return _frameCache;
            AllocNote("frame.before");
            long secT = System.Diagnostics.Stopwatch.GetTimestamp();
            DirectChildFrames = ChildShadowUpdates = 0;
            EnhancedVisuals = UEConfig.Global?.VisualModeIsOriginal != true;   // before the particle step of this frame (AdvanceNewborn)
            _cameraCache = WorldFrameProjector.ProjectCamera(interp, player);
            // Probe (not the game): the render camera is overridden, like the original's hp1drawlog probe (CAMERA
            // override=1) — the frame, occlusion and its child frames are built for it; world logic never reads the camera.
            _audioCamera = _cameraCache;   // Galaxy listener — the viewport camera, not the probe override
            if (ProbeCamera is (UEVector loc, UERotator rot) && _cameraCache.Valid)
                _cameraCache = new CameraSnapshot(true, _cameraCache.ViewActorId, loc, rot, _cameraCache.FovAngle, _cameraCache.BehindView);
            // URender::CreateMasterFrame -> ComputeRenderSize reads the viewport actor's
            // FovAngle RIGHT AFTER PlayerCalcView and before Actor.PreRender
            // (Engine.dll.c:117900-117920).
            float frameFov = player is null ? 0f : UEValue.AsFloat(player.GetProp(PK.FovAngle));
            float viewFov = HorPlusFov(frameFov);
            if (viewFov != frameFov)
            {
                frameFov = viewFov;
                _cameraCache = new CameraSnapshot(_cameraCache.Valid, _cameraCache.ViewActorId, _cameraCache.Location,
                    _cameraCache.Rotation, viewFov, _cameraCache.BehindView);
            }
            secT = Section(ref LastCameraMs, secT);
            AllocNote("frame.camera");
            // A CHANGE OF VIEW OWNER IS A ROUTE EVENT. A cutscene taking the camera
            // and returning it to the player is exactly what the player feels as "control
            // taken and given back", and the journal must show it without
            // guessing. The value comes from the real PlayerCalcView
            // (Harry.uc:1115 `ViewActor = ViewTarget`), not inferred here.
            if (_cameraCache.ViewActorId != _lastViewActorId)
            {
                _lastViewActorId = _cameraCache.ViewActorId;
                HP1.Core.UEPlaythroughLog.Always("view",
                    $"view owner -> id={_cameraCache.ViewActorId}");
            }
            // HUD single-execution ownership: the HUD bytecode must run ONCE per
            // frame. A frontend still driving its own canvas renderer (one that
            // calls myHUD.PostRender itself) disables this drive until it
            // renders frame.Hud — otherwise widget state (e.g. the baseHudItem
            // xOffset slide) advances twice per frame. Headless keeps it on.
            // ── Render->PreRender(Frame) (Engine.dll.c:117933, Render 0x10b01375) ──
            // After audio and before Console.PreRender: appEnableFastMath(1) and
            // GRandoms->Tick(Level.TimeSeconds) — 256 or 512 appFrand on
            // EVERY frame, before the HUD and before the world.
            UEObject frameLevel = player?.GetProp(PK.Level) as UEObject;
            // The table Galaxy Update of this frame will see: it runs BEFORE
            // Render->PreRender (Engine.dll.c:117938 before 117946), i.e. before Tick.
            // Needed by the Flicker type function in a glowing ambient's volume.
            // Before the first Tick the table is zeros, like the static 0x10b6696c
            // (Render.dll .data beyond the raw data, zero at load).
            Array.Copy(GRandoms.Base, _audioRandomBase, _audioRandomBase.Length);
            if (frameLevel is not null)
            {
                float frameTime = UEValue.AsFloat(frameLevel.GetProp(PK.TimeSeconds));
                GRandoms.Tick(frameTime,
                    Frand ?? throw new InvalidOperationException("frame: render appFrand stream not assigned"));
                // Frame state read by SetupForSurf when building
                // lightmaps inside DrawWorld (after this Tick): the
                // FGlobalRandoms tables, Actors(0).TimeSeconds (DAT_10b4c820 +0x3b4) and
                // the source's Level->Brightness (+0x7c -> +0x44c, R:19276; the Level
                // of any actor is the LevelInfo of slot 0). A snapshot, not a reference:
                // the tables tick next frame.
                LightmapFx = new SurfLightmap.FrameFx
                {
                    RandomBase = (float[])GRandoms.Base.Clone(),
                    RandomRand = (float[])GRandoms.Rand.Clone(),
                    TimeSeconds = frameTime,
                    LevelBrightness = UEValue.AsFloat(frameLevel.GetProp(PK.Brightness)),
                    NoDynamicLights = UEViewportStartup.ClientNoDynamicLights(AudioRuntime?.Config),
                };
            }
            else LightmapFx = null;
            // Shipped render over the level mirror (RenderLevelPass): CreateMasterFrame
            // and Render->PreRender — here, before the script PreRender and the HUD.
            // The appEnableFastMath(1) window (Render.dll.c:15787, URender::PreRender) ...
            // appEnableFastMath(0) (28920, URender::PostRender after Console.PostRender):
            // HUD/console scripts and ActorShadow.Update → execAttachDecal run in 24 bits.
            FastMathWindow = true;
            // Render pass over the mirror (Render.dll port) — reference comparison mode only;
            // the production frame path does not build it.
            ITranslatedRenderPass pass = UseTranslatedRenderPass && player is not null && _cameraCache.Valid ? RenderPassFor?.Invoke(player) : null;
            if (UseTranslatedRenderPass && player is not null && _cameraCache.Valid && pass is null) AnnounceNoRenderPass();
            LastRenderPass = pass;
            if (UseTranslatedRenderPass) pass?.BeginFrame(player, _cameraCache.Location, _cameraCache.Rotation, _viewportX, _viewportY,
                viewFov != UEValue.AsFloat(player?.GetProp(PK.FovAngle)) ? viewFov : float.NaN);
            int frameX = _viewportX, frameY = _viewportY;
            if (DriveHudEnabled) { HudDriveCount++; (frameX, frameY) = HudPreRender(interp, player); }
            else _hudCache = Array.Empty<HudCommand>();
            secT = Section(ref LastHudMs, secT);
            AllocNote("frame.hud");
            // ── Render->DrawWorld(Frame) ────────────────────────────────────
            // Shipped gate (Engine.dll.c:117935-117939): a non-empty frame and
            // Console->GetDrawWorld() = !bNoDrawWorld (0x1038ba90).
            bool drawWorld = frameX > 0 && frameY > 0
                && (Console is null || !UEValue.ToBool(Console.GetProp(PK.bNoDrawWorld)));
            LastDrawWorld = drawWorld;
            LastDrawWorldGate = (frameX, frameY, Console is not null && UEValue.ToBool(Console.GetProp(PK.bNoDrawWorld)));
            // ── PARTICLES: ONLY DRAWN EMITTERS, IN DRAW ORDER ─────
            // Shipped simulates particles in the renderer: DrawActorSprite ->
            // DrawParticleSystem -> AParticleFX::Update(0) for emitters that
            // OccludeFrame put into the frame's sprite list, in DrawFrame order
            // (RenderDrawOrder), plus GetRenderBoundingBox when an emitter moves.
            // An invisible STATIONARY emitter is frozen (a moved one is updated by the box —
            // in the direct path too, DirectRenderStep).
            _shadowBlockers.Clear();
            if (interp?.Natives?.GetByIndex(257) is null)
                _shadowBlockers.Add("native 257 BonePos not ported (C3): the Update body starts with it");
            if (!ShadowVisibilityTraversalPorted)
                _shadowBlockers.Add("shadow selection of URender::SetupDynamics not ported: shipped calls "
                    + "Update on actors that passed the filters and BoundVisible(…, NULL), in Level.Actors order "
                    + "(Render.dll 0x10b2fb30-0x10b304e9)");
            if (!ShadowPresentationSemanticsResolved)
                _shadowBlockers.Add("shadow presentation not ported: the frontend has no clipping of the decal to the polygons of "
                    + "FDecal.Nodes (URender::ClipDecal, viewer+edge planes of FSavedPoly)");
            // ── TWO DECAL-LAYER GAPS ──────────────
            //
            // They must be listed: for a campaign shadow `AttachDecal` returns a
            // SUCCESSFUL one-sided attachment and only writes a journal line, so from
            // outside the gap is indistinguishable from a port. A leg enabled with such
            // a decal layer would draw a plausible wrong shadow — exactly what the
            // STOP rule forbids. The owner of both facts is the decal layer itself.
            if (!UELevelRuntime.DecalWrapPorted)
                _shadowBlockers.Add("decal wrap onto neighbouring surfaces (MultiDecalLevel) "
                    + "not ported: AttachDecal puts the shadow only on the chosen surface "
                    + "and still returns SUCCESS");
            if (!UELevelRuntime.SurfDecalListPorted)
                _shadowBlockers.Add("Surf->Decals traversal not ported: shipped keeps the attachment "
                    + "on the surface side too, the port only in Decal.SurfList");
            ShadowsBlockedByNative = _shadowBlockers.Count == 0 ? 0 : 257;
            ShadowRouteBlockers = _shadowBlockers.ToArray();
            var particles = new List<ParticleInstance>();
            bool directDynamics = false;   // SetupDynamics (brushes, shadows) already run by the direct frame path
            _meshPoses = new List<MeshPose>();   // a frame without the direct path has no poses (not stale ones)
            _spriteActors = new List<int>();      // and no sprites
            _actorViews = new Dictionary<int, List<int>>();
            _drawOrder = null;                    // and no draw order
            _actorViewSets = null;
            _coronas = null;                      // and without coronas
            _viewportClock += ViewportDelta;      // UECoronas clock (shipped: wall time at each run of the block)
            BeginDecalFrame?.Invoke();             // and no decal pieces
            _views = null;                        // and no child frames
            LastMasterFrame = null;               // and no frame tree (a tick without OccludeBsp: menu, bNoDrawWorld)
            _drawn = null; _drawnTextures = null;   // and no world polygons
            _surfDyn = null; SurfDynamicLightsRefusal = null;   // and no +0x48 lists
            if (player is not null && _cameraCache.Valid && drawWorld && Frand is not null
                && (!UseTranslatedRenderPass || pass is not null))
            {
                IEnumerable<UEObject> drawn;
                if (UseTranslatedRenderPass)
                {
                    // The port runs ITS OWN brush tracker and returns the mover's SavedPos/SavedRot to Core; Core's
                    // tracker would then see the mover as static and Core's model (collision, occlusion) would lag.
                    // The marks are restored before the Core pass — both trackers get the same Update.
                    var saved = new List<(UEObject, object, object)>();
                    foreach (UEObject a in actors)
                        if (a is not null && a.Props.TryGetValue("SavedPos", out object sp)) saved.Add((a, sp, a.Props.TryGetValue("SavedRot", out object sr) ? sr : null));
                    pass.DrawWorld(player); drawn = pass.DrawnParticles;
                    foreach (var (a, sp, sr) in saved) { a.Props["SavedPos"] = sp; a.Props["SavedRot"] = sr; }
                }
                else { var (wx, wy) = WorldFrameSize(frameX, frameY); drawn = DirectRenderStep(player, interp, wx, wy, frameFov, actors, _shadowBlockers.Count == 0); directDynamics = true; }
                // Direct path: one entry per DrawParticleSystem call, the particles its billboard pass kept with their Alpha
                // at that call, drawn in that call's view (ParticleDrawerFade).
                foreach (var (a, view, kept) in UseTranslatedRenderPass ? new() : ParticleDraws)
                {
                    if (a.Destroyed) continue;
                    string tex = TexPathOf(((System.Collections.IList)a.GetProp(PK.Textures))[0]);
                    foreach (var (pt, alpha) in kept)
                        particles.Add(new ParticleInstance(a.ObjectId, pt.Pos, pt.Width, pt.Length, alpha, pt.Color, pt.SpinPos, tex,
                            pt.Vel, pt.DripTimer, pt.SpinRate, view));
                }
                var seen = new HashSet<UEObject>();
                foreach (UEObject a in UseTranslatedRenderPass ? drawn : Array.Empty<UEObject>())
                {
                    if (!seen.Add(a)) continue;
                    FxState s = FxStateOf(a);
                    if (s.Particles.Count == 0) continue;
                    string tex = a.GetProp(PK.Textures) is System.Collections.IList tl && tl.Count > 0
                        ? TexPathOf(tl[0]) : null;
                    // DrawParticleSystem's pass FUN_10b15090 walks the list with the same virtual First/Next as
                    // UpdateParticles ([[fx+0x378]] +0x70 / +0x74 at 0x10b1534b / 0x10b1537b; Engine 0x103c1df1 /
                    // 0x103c2050): cur = First(); while (cur) { next = Next(); draw(cur, next); cur = next; }
                    // (0x10b15425..0x10b15435, back edge 0x10b1575e..0x10b15766). The first Next() does not advance
                    // (the flag +0x80 is clear after every full walk, 0x103c7383), so particle 0 is drawn twice:
                    // (p0, p0), (p0, p1), (p1, p2), ... The frame carries that draw sequence.
                    for (int v = s.Particles.Count > 0 ? -1 : 0; v < s.Particles.Count; v++)
                    {
                        var pt = s.Particles[v < 0 ? 0 : v];
                        particles.Add(new ParticleInstance(a.ObjectId, pt.Pos,
                            pt.Width, pt.Length, pt.Alpha, pt.Color, pt.SpinPos, tex,
                            pt.Vel, pt.DripTimer, pt.SpinRate));
                    }
                }
            }
            if (UseTranslatedRenderPass) pass?.EndFrame();
            // The pawn weapon point (WeaponLoc/WeaponRot) is written by the mesh branch of
            // DrawActorSprite inside pass.DrawWorld — only for drawn pawns and
            // in DrawFrame order (RenderLevelPass.DrawMeshSprite).
            // RenderStep: actor shadows. Shipped updates them
            // IN THE RENDERER: URender::SetupDynamics (Render.dll FUN_10b2fb30), for an
            // actor with a non-empty `Shadow`, inline-calls `ADecal::eventUpdate` with a
            // parameter hard-wired to zero (`mov dword ptr [ebp-0x18], 0`), i.e.
            // `Update(None)` (0x10b304be..0x10b304e9; the decompile lacks this site,
            // the wrapper is inlined).
            //
            // COMPOSITION AND ORDER (read from disasm and proven by
            // execution). SetupDynamics is called in DrawWorld
            // BEFORE OccludeBsp (Render.dll 0x10b253ba -> 0x10b253c2), walks
            // Level.Actors by index, and the shadow box is tested with
            // `BoundVisible(Frame, Box, SpanBuffer = NULL, ...)` — frustum only.
            // This selection is ported in UERenderVisibility and checked against the real
            // Engine/Render/Core bodies under unicorn
            // ── ROUTE READINESS — A GATE, NOT A LABEL ─────────────────────
            //
            // The blockers are a LIST, and the leg runs only when it is EMPTY.
            // Each item is a separate open dependency, and closing one of them
            // enables nothing by itself (a diagnostic label describing a violation
            // would not block it). BonePos (native 257) is registered in both frontends,
            // so the first condition is FALSE in production; it stays as a gate in case
            // the registration is removed.
            secT = Section(ref LastParticlesMs, secT);
            AllocNote("frame.particles");
            if (_shadowBlockers.Count != 0)
            {
                if (!_shadowDebtAnnounced)
                {
                    _shadowDebtAnnounced = true;
                    HP1.Core.UEPlaythroughLog.Always("decal",
                        "actor shadows are NOT updated, open dependencies "
                        + _shadowBlockers.Count + ": " + string.Join(" | ", _shadowBlockers));
                }
            }
            // URender::DrawWorld -> SetupDynamics(Frame, Exclude) (Render.dll
            // 0x10b2537f..0x10b253ba): Exclude = the view target, unless
            // bBehindView, for the master view frame (ViewTarget, otherwise the viewer itself).
            // Selection and call — UERenderVisibility, proven by executing shipped
            // (checked against the original DLL under unicorn). The same pass puts
            // mover brush fragments into the level model (Level->BrushTracker->Update) —
            // it runs even when shadows are blocked by decal-layer gaps: the tracker does not depend on them.
            if (!directDynamics) DirectMovers = 0;
            if (drawWorld && player is not null && _cameraCache.Valid && !directDynamics)
            {
                // A frame without the direct path (the Render.dll port draws itself, or there is no particle rand()
                // stream): SetupDynamics (brushes, shadows) and OccludeBsp (warp-zone Generate event, node bits, children)
                // are run by Core in the same walk, without sprite Setup — the port does that for the reference.
                var (wx, wy) = WorldFrameSize(frameX, frameY);
                UESceneNode occluded = OccludeWorld(player, interp, wx, wy, frameFov, actors, _shadowBlockers.Count == 0, sprites: false);
                _views = ProjectViews(occluded, _lastOcclusionWorld.SourceOf, null, _drawn = new List<DrawnWorld>(), _lastOcclusionWorld.PolyOf);
                _drawnTextures = DrawnTexturesOf(occluded, _lastOcclusionWorld);
                CollectSurfDynamicLights(occluded, _lastOcclusionWorld);
            }
            // C9 measurement (investigation, off by default): actor render state before the overlays/HUD vs after.
            if (OverlayProbe) _overlayPre = OverlayFingerprints(actors);
            // Tail of URender::DrawWorld (Render.dll.c:2661-2677): after DrawFrame —
            // the RenderOverlays(Viewport.Canvas) event to the recipient `bBehindView ? None :
            // (ViewTarget ?? Actor)` with !GIsEditor and ShowFlags & 8, via
            // ProcessEvent. (GUglyHackFlags|=1 for the call is read only by
            // Canvas.DrawActor, which is a loud STOP in the port.)
            if (DriveHudEnabled && player is not null && _cameraCache.Valid)
            {
                UEObject overlay = UEValue.ToBool(player.GetProp(PK.bBehindView)) ? null
                    : (player.GetProp(PK.ViewTarget) as UEObject ?? player);
                if (overlay is not null && (UEValue.ToInt(player.GetProp(PK.ShowFlags)) & 8) != 0)
                {
                    RenderOverlaysCount++;
                    UELifecycle.CallEvent(interp, overlay, "RenderOverlays", _canvas);
                }
            }
            if (DriveHudEnabled) HudPostRender(interp, player);
            if (OverlayProbe)
                foreach (var kv in OverlayFingerprints(actors))
                    if (!_overlayPre.TryGetValue(kv.Key, out var pre) || pre.fp != kv.Value.fp) OverlayChanged.Add(kv.Value.what);
            FastMathWindow = false;
            UEGalaxyAudio.JudgeTap?.Invoke(Audio, "update-begin", (actors, player, new GalaxyWorld(this).Listener, ViewportDelta, AudioRuntime));
            AmbientPass(actors, player);
            // Rest of UGalaxyAudioSubsystem::Update — channel loop, bSongFinished, music block (UEGalaxyAudio).
            // The frame's music request as the script left it (before this Update's Galaxy start block).
            MusicState music = WorldFrameProjector.ProjectMusic(player);
            UEObject levelInfo = null;
            foreach (UEObject a0 in actors) { levelInfo = a0; break; }
            Audio.Update(music, ViewportDelta, !string.IsNullOrEmpty(levelInfo?.GetProp(PK.Pauser)?.ToString()));
            UEGalaxyAudio.JudgeTap?.Invoke(Audio, "update-end", null);
            secT = Section(ref LastAmbientMs, secT);
            AllocNote("frame.ambient");
            // A music request (Transition != 0) is acknowledged only by the Galaxy start block (Transition = 0,
            // Galaxy.dll.c:7385-7451) — run by Audio.Update above (UEGalaxyAudio).
            _cacheTick = tick;
            _cacheValid = true;
            AudioCommand[] audio = _audio.ToArray();
            _audio.Clear();
            IReadOnlyList<DecalInstance> decals = DecalSnapshot?.Invoke();
            secT = Section(ref LastDecalsMs, secT);
            AllocNote("frame.decals");
            long projT0 = System.Diagnostics.Stopwatch.GetTimestamp();
            _frameCache = WorldFrameProjector.ProjectWorld(tick, label, actors,
                _cameraCache, player, music, audio, _hudCache, particles,
                HighDetailActors, decals, _projectScratch,
                frameLevel is null ? null : GRandoms.Base, drawWorld,
                UseTranslatedRenderPass ? null : _meshPoses, UseTranslatedRenderPass ? null : _views,
                UseTranslatedRenderPass ? null : _spriteActors, Audio.Snapshot(), Audio.ResumeSerial,
                UseTranslatedRenderPass ? null : _actorViewSets ?? ActorViewSets.From(_actorViews), _coronas, EnhancedVisuals,
                UseTranslatedRenderPass ? null : _drawn, UseTranslatedRenderPass ? null : _drawnTextures,
                Audio.LastCallStops, Audio.LastMixDt, UseTranslatedRenderPass ? null : _drawOrder, _canvasScale);
            long projDt = System.Diagnostics.Stopwatch.GetTimestamp() - projT0;
            AllocNote("frame.project");
            LastProjectMs = projDt * MsPerTick;
            if (UELevelRuntime.ProfileTicks) UELevelRuntime.ProfileProjectTicks += projDt;
            return _frameCache;
        }

        public int RenderOverlaysCount;

        // C9 probe: an actor whose render-relevant state the overlays/HUD PostRender changed after DrawWorld (shipped draws
        // the world before them; the port's frame snapshot is taken after them).
        public static bool OverlayProbe;
        public readonly List<string> OverlayChanged = new();
        Dictionary<int, (long fp, string what)> _overlayPre = new();

        // LOADING SCREEN — UGameEngine::LoadMap 0x1039cd1d..0x1039cd9f:
        // Viewport->Lock, Console.DrawLevelInfo(Viewport.Canvas, URL), Unlock(1).
        // Canvas->Update (and Reset with it) is NOT called here — the canvas remains from
        // the last frame; there is no world in the frame. The canvas is created only if it
        // did not exist yet (headless without HUD).
        public bool LevelInfo43 { get; internal set; }   // loading screen as a 4:3 picture centred on a wide screen (SetWideScreen)
        public int LevelInfoOffsetX;      // offset of the last loading screen (canvas), for the frontend bars

        // The canvas frame the player's HUD saw (Actor.PostRender), and the canvas itself —
        // for the console shrink/restore fixture.
        public (int X, int Y) LastPlayerHudFrame;

        // ── CONSOLE REGION: 4:3 MENU ON A WIDE SCREEN (NOT ORIGINAL) ──
        //
        // NOT SHIPPED. The original supported only 4:3, and HP1 UWindow scales the
        // menu by canvas width (GUIScale = 1, the 640x480 book stretches to
        // Canvas.ClipX) — on a wide screen the bottom of the menu goes off-screen. So
        // the menu is 4:3, centred with black sides. The frontend sets the region
        // width (height·4/3) while the menu is open; the console then sees a frame of that
        // width (its scripts lay out UWindow themselves), and its commands are shifted
        // to the centre. A region not narrower than the screen, or 0 — shipped behaviour unchanged.
        // ── DIRECT FRAME PATH INSTEAD OF A Render.dll PORT (render scheduling need not copy the original) ──
        //
        // A full Render.dll port over a world mirror (BeginFrame/DrawWorld) would decide which
        // emitters are simulated and which meshes get a pose, at ~25 ms per frame.
        // By the "what the player sees" precision boundary, Core decides instead: the master
        // frame visibility pyramid (UESceneNode/BoundVisible, shipped port), without
        // wall culling and in actor-list order. Order and composition do not
        // match the original bit-for-bit (different rand() use), what is visible is the same:
        // an emitter in the frame lives, a mesh in the frame is animated. Meshes are taken with a
        // radius margin: an extra one costs CPU, a missed one would freeze its animation.
        // The port stays the reference for fixtures: UseTranslatedRenderPass = true.
        public bool UseTranslatedRenderPass;
        // The URender::PreRender..PostRender window (appEnableFastMath(1)/(0), Render.dll.c 15787/28920):
        // inside — PC24 (ActorShadow.Update -> execAttachDecal), outside — game logic in PC64.
        [ThreadStatic] public static bool FastMathWindow;
        // Runtime delegates for the direct frame path: brush tracker Update (mover moved —
        // true) and Client.ParticleDensity (UClient +0x9c, read live from config).
        public Func<UEObject, bool> UpdateMovingBrush;
        public Func<int> ParticleDensityNow;
        public int DirectMeshes, DirectEmitters, DirectMovers;
        // Meshes for which the direct frame path ran the mesh branch (GetFrame), in
        // actor-list order; a third-person weapon is a separate entry after the pawn.
        public readonly List<UEObject> DrawnMeshes = new();
        // Vertices of these meshes (WorldFrame.MeshPoses): a new list per frame — the frame goes to the frontend whole.
        List<MeshPose> _meshPoses = new();
        // DrawActorSprite mesh branch (UELevelRuntime.DrawMeshActor): actor, identity, Frame->Coords, list, frame vertices,
        // the draw's LOD request.
        public Action<UEObject, bool, BrushCoords, List<UEObject>, List<MeshPose>, Func<UEObject, int>> DrawMesh;
        // AActor::GetRenderBoundingBox of a mesh actor (FDynamicSprite::Setup, slot +0xa0) — Core body.
        public Func<UEObject, BrushCoords> MeshRenderBoundingBox;

        // Frame occlusion (UEOcclusion — URender::OccludeBsp): the walk world comes from the runtime (model,
        // zones, viewer and device flags, warp-zone Generate event; the interpreter executes it).
        public Func<UEInterpreter, UEObject, UEOcclusionWorld> OcclusionWorldFor;
        public readonly UEOcclusion Occlusion = new();
        public (UEVector Location, UERotator Rotation)? ProbeCamera;   // probes only (HPEngine --shot --cam)
        // Frame tree of THIS tick's OccludeBsp (master and children with sprite lists; WorldFrame.Views is its projection);
        // null when the tick ran none (DrawWorld gate closed, e.g. the menu book's bNoDrawWorld) — never the previous tick's.
        public UESceneNode LastMasterFrame;
        readonly string _systemDir;
        UETextureMetrics _spriteTextures;
        // Sprite texture USize/VSize (Texture +0x34/+0x38) — from the install packages (root above System). null — not found.
        public Func<string, (int USize, int VSize)?> SpriteTextureSize;
        UEOcclusionWorld _lastOcclusionWorld;
        List<int> _spriteActors = new();
        // DrawFrame corona block (UECoronas): table, clock and gate inputs. The table lives as long as the producer,
        // like the unflushed GCache item 0x27.
        public readonly UECoronas CoronaTable = new();
        public bool RenderDeviceCoronas = true;
        public bool RenderDeviceVolumetricLighting = true;   // RenDev->VolumetricLighting (+0x58); bare runtime: gate open              // RenDev->Coronas (+0x60); bare runtime: no device, gate open
        public StaticGeometry CoronaLevel;                    // viewer level model: Model->Leaves / Model->Lights
        public IReadOnlyDictionary<int, UEObject> CoronaActorsByExport;
        public Func<UEVector, UEVector, bool> CoronaLineClear;   // (start, end): SingleLineCheck(TraceFlags 6) found nothing
        public Func<UEVector, UEVector, bool> CoronaPawnClear;   // NOT ORIGINAL (Enhanced): no visible pawn on the ray (UELevelRuntime.CoronaPawnClear)
        Func<UEVector, UEVector, bool> _coronaEnhancedClear;
        double _viewportClock;
        CoronaInstance[] _coronas;
        // ObjectId -> view indices of the frames whose DrawFrame drew the actor (WorldFrame.ActorViews).
        Dictionary<int, List<int>> _actorViews = new();
        // WorldFrame.DrawOrder: per frame of the tree (index view + 1) the actors in DrawActorSprite order.
        int[][] _drawOrder;
        ActorViewSets _actorViewSets;   // with the LOD memberships (MeshLodPart), built by DirectRenderStep
        // MESH LOD (Original visual mode only, D-62): mesh LOD data by the Mesh reference (the DrawLodMesh request per draw, DirectRenderStep).
        public Func<object, (Hp1MeshLod Lod, int ModelVerts, int SpecialVerts, bool Skeletal)> MeshLodOf;
        public int DirectSprites;   // sprites (DT_Sprite/DT_SpriteAnimOnce) drawn by the direct-path frame
        List<ViewFrame> _views;
        List<DrawnWorld> _drawn;

        // THE +0x48 LIST EACH LEVEL SURFACE'S LIGHTMAP IS BUILT WITH THIS TICK (LevelSurfLightmap header). Every frame
        // of the tree hands its draw records SurfLights[iSurf] (UEOcclusion.OccludeBsp -> UESceneNode.SurfLights), and
        // the lightmap result 0x31 is reused within the viewport frame: the first frame in DrawFrame order (children
        // before the parent, Render.dll.c:1011-1014) that draws the surface decides. A drawn surface is one of the
        // frame's FSavedPoly (DrawnPolys) — kept only for a non-SpanBased device; on a SpanBased one (SoftDrv) the port
        // has no draw record list, so a light that reached a surface is a refusal (SurfDynamicLightsRefusal), not a guess.
        Dictionary<int, int[]> _surfDyn;
        public string SurfDynamicLightsRefusal;
        readonly HashSet<int> _surfDynSeen = new();
        List<string> _drawnTextures;
        public int DirectChildFrames, DirectChildEmitters, DirectChildMeshes, ChildShadowUpdates;

        // OccludeFrame of the master frame and all children (Render.dll.c:603-718). sprites — sprite Setup (FDynamicSprite::Setup
        // and constructor) in every frame; without them — only brushes, shadows and the walk itself (Generate, node bits, children).
        readonly UESceneNodeBanks _frameBanks = new();

        // ── HOR+: WIDE-SCREEN HORIZONTAL (NOT ORIGINAL) ──
        //
        // NOT SHIPPED. UE1 keeps FovAngle horizontal at any aspect
        // (URender::ComputeRenderSize), so on a screen wider than 4:3 the vertical is cut.
        // Hor+ keeps the vertical as for 4:3 with the same FovAngle, the horizontal
        // wider. ALL frame consumers get the effective FOV — the camera snapshot, the
        // shadow-visibility master frame and the viewport actor's FovAngle in the
        // Render.dll port mirror (CreateMasterFrame). So visibility (and with it
        // particle simulation and appFrand use) follows the widened view — a
        // deliberate divergence from 4:3, including its effect on logic. Off, or a screen not wider than 4:3 —
        // FovAngle unchanged.
        // Set by the host through SetWideScreen (the frontend writes no Core field).
        public bool HorPlus { get; internal set; }

        int _consoleRegionX;

        // Texture flag word (UTexture +0x80) for a HUD resource reference; set by the
        // world when the producer is handed out (UELevelRuntime.FrameProducer -> UETextureFactsReader).
        public Func<object, int> TexturePolyFlags;

        const int PF_Masked = 0x2, PF_Translucent = 0x4, PF_Modulated = 0x40,
                  PF_TwoSided = 0x100, PF_NoSmooth = 0x800;
        readonly System.Runtime.CompilerServices.ConditionalWeakTable<UEObject, CanvasFrame> _canvasFrames = new();
        CanvasFrame _masterFrame;
    }
}
