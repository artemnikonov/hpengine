// The SHARED WorldFrame producer (review #11 item 1): the one Core runtime
// object that owns frame building — the audio command buffer, the per-tick
// frame/camera/HUD caches, the Canvas object and the Canvas draw/measure
// machinery. Both frontends drive the SAME producer (the headless smoke and
// the Unity runtime register it into their natives); neither owns a second
// implementation. Frontends supply only their actor enumeration, the local
// player and (headless) the journal rendering of emitted audio.
using System;
using System.Collections.Generic;
using System.IO;
using HP1.Core;
using UELib;

namespace HP1.UEScript
{
    // A paged font EXACTLY in the shipped UFont layout (Engine.dll.c:59195,
    // UFont::Serialize): Pages (+0x2c, FFontPage {UTexture* Texture; TArray<
    // FFontCharacter> Characters}), CharactersPerPage (+0x28), and for package
    // version >= 0x45 — CharRemap (+0x38, TMap<TCHAR,TCHAR>) and IsRemapped
    // (+0x4c). The text bodies (WrappedPrint 0x10387e68, FUN_10388530) look up a glyph
    // exactly like this: page = c / CharactersPerPage, index = c - page·CPP, so the
    // model keeps pages, not an array flattened by character code.
    //
    // Native fonts (empty Pages and CPP == 0: name +0x50 and height +0x5c) are measured
    // and drawn by the OS device — they cannot be rebuilt from package data, and the
    // bodies stop loudly (see WrappedPrint).
    public readonly struct Hp1FontChar
    {
        public readonly int StartU, StartV, USize, VSize;
        public Hp1FontChar(int u, int v, int us, int vs) { StartU = u; StartV = v; USize = us; VSize = vs; }
    }

    public sealed class Hp1FontPage
    {
        public bool HasTexture;           // FFontPage.Texture != NULL
        public Hp1FontChar[] Characters;
    }

    public sealed class Hp1FontMetrics
    {
        public string Path;
        public int CharactersPerPage;
        public Hp1FontPage[] Pages;
        public Dictionary<int, int> CharRemap;   // key -> value (TMap::Find)
        public bool IsRemapped;

        // Character remap: with IsRemapped — TMap::Find, a miss gives ' '
        // (WrappedPrint 0x10387e71-0x10387e8b; FUN_10388530 0x103885e3-0x1038861f).
        public int Remap(int c)
            => !IsRemapped ? c : CharRemap.TryGetValue(c, out int v) ? v : 0x20;

        // Shipped page/index: idiv by CharactersPerPage. Returns the page,
        // or null if there is no page or the index is beyond its Characters.Num.
        public bool TryChar(int c, out int page, out Hp1FontChar ch)
        {
            page = c / CharactersPerPage;
            int idx = c - CharactersPerPage * page;
            ch = default;
            if (page >= Pages.Length || idx >= Pages[page].Characters.Length) return false;
            ch = Pages[page].Characters[idx];
            return true;
        }
    }

    public sealed class Hp1FontCatalog
    {
        readonly string _systemDir;
        readonly Dictionary<string, Hp1FontMetrics> _cache = new(UEPropNameComparer.Instance);
        readonly Dictionary<string, (UnrealPackage Pkg, byte[] Bytes)> _packages = new(UEPropNameComparer.Instance);
        public Hp1FontCatalog(string systemDir) { _systemDir = systemDir; }

        public Hp1FontMetrics Get(string path)
        {
            if (_cache.TryGetValue(path, out var hit)) return hit;
            int dot = path.IndexOf('.');
            if (dot <= 0) throw new InvalidDataException($"font path has no package: '{path}'");
            string pkgName = path.Substring(0, dot);
            string fontName = path.Substring(path.LastIndexOf('.') + 1);
            if (!_packages.TryGetValue(pkgName, out var loaded))
            {
                // The package is searched in THE GAME'S OWN PATH ORDER ([Core.System] Paths,
                // the shared list UETextureFactsReader.PackagePaths), not only System/*.u:
                // the UWindow fonts live in Textures/UWindowFonts.utx.
                string root = Path.GetDirectoryName(Path.GetFullPath(_systemDir.TrimEnd('/', '\\')));
                string pkgPath = null;
                foreach (string pattern in UETextureFactsReader.PackagePaths)
                {
                    pkgPath = UEInstallPath.File(Path.Combine(root, string.Format(pattern, pkgName)));
                    if (pkgPath is not null) break;
                }
                if (pkgPath is null)
                    throw new FileNotFoundException($"font package missing: {pkgName} (searched [Core.System] Paths from {root})");
                // Only the name/export tables (read by LoadPackage) and the raw bytes are used: the font body is
                // parsed from the bytes below, so the package objects are not constructed (perf: a full
                // InitializePackage of HPBase.u cost ~30 ms / 20 MB on the first HUD text).
                var pkg = UnrealLoader.LoadPackage(pkgPath, FileAccess.Read);
                loaded = (pkg, File.ReadAllBytes(pkgPath));
                _packages[pkgName] = loaded;
            }
            UELib.UExportTableItem export = null;
            foreach (var e in loaded.Pkg.Exports)
            {
                if (!string.Equals(e.ObjectName.ToString(), fontName, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(e.Class?.ObjectName.ToString(), "Font", StringComparison.OrdinalIgnoreCase)) continue;
                export = e;
                break;
            }
            if (export is null)
                throw new InvalidDataException($"shipped font not found: {path}");
            var metrics = ReadSerialized(path, loaded.Pkg, loaded.Bytes, export);
            _cache[path] = metrics;
            return metrics;
        }

        // UFont::Serialize body (Engine.dll.c:59215-59273) over the export bytes:
        // object properties (shipped fonts have only the None terminator), Pages,
        // CharactersPerPage (a power of two — assert UnFont.cpp:0x1c), and for
        // version >= 0x45 CharRemap + IsRemapped, otherwise IsRemapped = 0. UELib
        // does not keep CharactersPerPage and reads the native font tail in a
        // different order, so the layout is read from the bytes in shipped order.
        static Hp1FontMetrics ReadSerialized(string path, UnrealPackage pkg, byte[] b, UELib.UExportTableItem e)
        {
            int p = e.SerialOffset, end = e.SerialOffset + e.SerialSize;
            int Compact()
            {
                byte b0 = b[p++];
                int v = b0 & 0x3f;
                if ((b0 & 0x40) != 0)
                {
                    int shift = 6;
                    while (true)
                    {
                        byte x = b[p++];
                        v |= (x & 0x7f) << shift;
                        shift += 7;
                        if ((x & 0x80) == 0) break;
                    }
                }
                return (b0 & 0x80) != 0 ? -v : v;
            }
            int Int() { int v = BitConverter.ToInt32(b, p); p += 4; return v; }
            int none = Compact();
            if (none < 0 || none >= pkg.Names.Count
                || !string.Equals(pkg.Names[none].Name, "None", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"font {path}: export properties are not empty — parsing of font property tags is not ported");
            int pageCount = Compact();
            var pages = new Hp1FontPage[pageCount];
            for (int i = 0; i < pageCount; i++)
            {
                int tex = Compact();
                int n = Compact();
                var chars = new Hp1FontChar[n];
                for (int j = 0; j < n; j++)
                    chars[j] = new Hp1FontChar(Int(), Int(), Int(), Int());
                pages[i] = new Hp1FontPage { HasTexture = tex != 0, Characters = chars };
            }
            int cpp = Int();
            if ((cpp & (cpp - 1)) != 0)
                throw new InvalidDataException($"font {path}: Assertion failed: !(CharactersPerPage&(CharactersPerPage-1)) [UnFont.cpp:28]");
            var remap = new Dictionary<int, int>();
            bool isRemapped = false;
            if (pkg.Version >= 0x45)
            {
                // TMap::Rehash inserts pairs in ascending index order at the chain HEAD,
                // and Find returns the last inserted pair with that key —
                // so a later pair overrides an earlier one.
                int n = Compact();
                for (int i = 0; i < n; i++)
                {
                    int k = BitConverter.ToUInt16(b, p), v = BitConverter.ToUInt16(b, p + 2);
                    p += 4;
                    remap[k] = v;
                }
                isRemapped = Int() != 0;
            }
            if (pageCount == 0 && cpp == 0)
                throw new InvalidDataException(
                    $"font {path} has no glyph pages (a serialized native font) — "
                    + "measurement needs the shipped-OS glyph-width oracle");
            if (p != end)
                throw new InvalidDataException($"font {path}: parse did not match the end of the export ({p} vs {end})");
            return new Hp1FontMetrics
            {
                Path = path, CharactersPerPage = cpp, Pages = pages,
                CharRemap = remap, IsRemapped = isRemapped,
            };
        }
    }

    public sealed class UEWorldFrameProducer
    {
        // Viewport dims are FRONTEND state (review #12 P2): the headless takes
        // the retail ini's windowed resolution as its viewport; a windowed
        // frontend supplies its ACTUAL surface each frame via SetViewport —
        // shipped likewise sizes the canvas from the live viewport, the ini is
        // only the startup default.
        int _viewportX;
        int _viewportY;
        readonly Hp1FontCatalog _fonts;

        // A plain viewport (headless, SetRes) has no interface scale: its canvas, world frame and screen are one size.
        public void SetViewport(int x, int y) { _viewportX = x; _viewportY = y; _screenX = x; _screenY = y; _canvasScale = 1; }
        // The window's pixel size before a step: Core picks the interface scale (D-44 Interface Size, not original:
        // UEConfig.UIScaleFor by the height and [OpenHP1] UIScaleStep) and makes the canvas screen / scale. The scale
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
        // cameras: 400 level nodes + 27 mover polygons on 111 cameras, runtime_poc --rescensus; holes visible in the frame).
        // The console border (ConsolePreRender, canvas units) scales with the interface; without it the world frame is
        // exactly the window. Check: --worldframecheck, audit/world_frame_size_redtest.sh.
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

        // ORIGINAL / ENHANCED (author decisions 2026-10-15, 2026-11-08, NOT ORIGINAL): read HERE, once per frame at the
        // start of Build — the one owner — from [OpenHP1] VisualMode only (UEConfig.VisualModeIsOriginal; no menu item,
        // the player always gets Enhanced). NOT from PlayerPawn.ObjectDetail: the shipped Options item "Object Detail"
        // writes it (FEOptionsPage.uc:589-622) and nothing reads it in 1.53 (D-27). Enhanced turns on the
        // runtime-switchable visual improvements: newborn fire ramps in substeps (D-54, AdvanceNewborn) and, in the
        // frontend via WorldFrame.EnhancedVisuals, anisotropy (D-53) and trilinear mips (D-48). Original = shipped.
        public bool EnhancedVisuals = true;

        // Audio commands buffered since the last consumed tick. The frontend's
        // sound natives emit here (and render their own journal line).
        readonly List<AudioCommand> _audio = new();
        // HUD commands of the current drive.
        readonly List<HudCommand> _hud = new();
        HudCommand[] _hudCache = Array.Empty<HudCommand>();
        // Review #13 item 4: HUD-bytecode single execution. A frontend whose
        // LEGACY renderer still calls myHUD.PostRender itself sets this false
        // (the proxy flips it back when it renders frame.Hud); headless = on.
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
        // edit, i.e. a red mutation (audit/shadow_route_redtest.sh).
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
        //     under unicorn (se_harness/bsp_diff/validate_decal_clip.py);
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

        // The UK11 Render.dll (earlier build, audit/editions_uk_2026-11-08.md) draws particles without the density fade
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

        // ── Native emitter state (review #11 item 2, recorded decision) ─────
        // Core owns the particle system. The GAME-TICK half (below) —
        // AParticleFX::Tick (Engine.dll.c:14138) accumulates Age and fires the
        // emission-budget self-destroy; it consumes NO RNG. The RENDER-side
        // half — the deterministic RenderStep after the camera calls
        // Update(0)→EmitParticles per shipped render order, where emission
        // consumes the render-side MsvcRandom (shipped: appFrand from the one
        // CRT rand stream; the port gives presentation its own stream —
        // see UENativesCore.BindFrameProducer).
        // One live particle — the UParticle field set (offsets in the shipped
        // object; the bridge's decompile-anchored UPart). Held in the C#
        // container that models the transient ParticleList (+0x378).
        public sealed class Particle
        {
            public UEVector OldPos, Pos, Vel;
            public float Age, Lifetime;          // Lifetime 0 = immortal
            public float Alpha, AlphaMax, AlphaDelta;
            public UEVector Color, ColorDelta;
            public float Width, WidthMax, WidthDelta;
            public float Length, LengthMax, LengthDelta;
            public float DripTimer, ChaosTimer, SpinPos, SpinRate;
            // UParticle +0x88: the draw priority EmitParticles gives each born particle (PriorityTable below); read by the
            // Render.dll particle drawer's density fade.
            public int Priority;
        }

        // Engine .rdata 0x104783e8: the ten priorities EmitParticles deals out in turn (0x103c351c `mov ecx, [4*eax +
        // 0x104783e8]`, eax = the emitter's CurrentPriorityTag).
        static readonly int[] PriorityTable = { 9, 3, 1, 7, 5, 0, 6, 2, 4, 8 };

        // NATIVE-ONLY emitter state. Every DECLARED ParticleFX var — Age,
        // EmitDelay, EmissionResidue, ParticlesEmitted, ParticlesAlive,
        // ElapsedTime, LastUpdate*/LastEmitLocation — is SCRIPT-VISIBLE and
        // lives in the prop bag (review #12 follow-up: the phase-1 FxState
        // holding Age was wrong). What remains native is the particle
        // container itself (the transient ParticleList object's contents).
        public sealed class FxState
        {
            public readonly List<Particle> Particles = new();
            // Container bounds (UParticleList +0x54..+0x6c: Min, Max, IsValid) —
            // read by AParticleFX::GetRenderBoundingBox for emitter visibility.
            public UEVector BoundsMin, BoundsMax;
            public bool BoundsValid;
        }

        // UParticleList slot +0x94 (Engine FUN_103c7630), called at the end of
        // AParticleFX::Update after simulation: the box is reset, then for each
        // particle FBox += Location ∓ half, half = 0.5·max(Length, Width) (on
        // equality — Width; particle fields +0xc Location, +0x58 Width, +0x64
        // Length — layout of AParticleFX::AddParticle). Render arithmetic is float
        // (24-bit x87 precision); min/max do not depend on particle order.
        public static void UpdateListBounds(FxState s)
        {
            s.BoundsValid = false;
            s.BoundsMin = default; s.BoundsMax = default;
            foreach (Particle p in s.Particles)
            {
                float size = p.Length <= p.Width ? p.Width : p.Length;
                float half = size * 0.5f;
                Add(new UEVector(p.Pos.x - half, p.Pos.y - half, p.Pos.z - half));
                Add(new UEVector(p.Pos.x + half, p.Pos.y + half, p.Pos.z + half));
            }

            void Add(UEVector v)
            {
                if (!s.BoundsValid) { s.BoundsValid = true; s.BoundsMin = v; s.BoundsMax = v; return; }
                s.BoundsMin = new UEVector(v.x < s.BoundsMin.x ? v.x : s.BoundsMin.x, v.y < s.BoundsMin.y ? v.y : s.BoundsMin.y, v.z < s.BoundsMin.z ? v.z : s.BoundsMin.z);
                s.BoundsMax = new UEVector(s.BoundsMax.x < v.x ? v.x : s.BoundsMax.x, s.BoundsMax.y < v.y ? v.y : s.BoundsMax.y, s.BoundsMax.z < v.z ? v.z : s.BoundsMax.z);
            }
        }
        readonly Dictionary<UEObject, FxState> _fx = new();

        public FxState FxStateOf(UEObject fx)
        {
            if (!_fx.TryGetValue(fx, out var s)) _fx[fx] = s = new FxState();
            return s;
        }

        // The one-time script-init pass (review #13 item 3) — two shipped
        // sources composed:
        //   the C++ ctor AParticleFX::AParticleFX (Engine.dll.c:161059)
        //   builds the class-default block: LOD(+0x300)=1.0 (NOTHING in
        //   Engine/Render ever writes it afterwards — searched, the renderer
        //   does not set it — so emission always sees the loaded value),
        //   bUpdate(+0x2f8 bit0)=true, Age/ElapsedTime/container zeroed;
        //   AParticleFX::InitExecution (Engine.dll.c:75566) then FORCES
        //   bSteadyState = (ParticlesMax==0 && Distribution != 1 (the
        //   pattern/path distribution)), and when it holds and
        //   (bPrime || Level.TimeSeconds==0) raises Age to the full lifetime
        //   (Lifetime.Base + max(Lifetime.Rand,0)) — the PRIME BURST then
        //   happens on the first render Update: dt = Age-0 passes the steady
        //   clamp (the Location==LastUpdateLocation gate fails against the
        //   ctor-zeroed LastUpdateLocation) and emission back-fills a whole
        //   lifetime window of aged particles. ElapsedTime resets to 0.
        //
        // Called from the ENGINE's InitExecution passes — the loaded-level sweep
        // (Engine.dll.c:247376-247381) and SpawnActor (20258) — never lazily off
        // the first render. It used to be lazy, and that made a no-render run and
        // a rendering run start from different actor state: exactly the class of
        // difference the render-integrated gate step exists to catch.
        public static void ParticleFxInitExecution(UEObject fx)
        {
            // The AParticleFX ctor (161064-161069: Age/ElapsedTime/container zeroed,
            // bUpdate|=1, LOD=1) is ported where construction lives —
            // UEObject.LoadDefaults, AFTER the defaults copy (ctor-after-defaults
            // order proven by Core.dll.c:14176-14179). The guards below exist ONLY for
            // raw fixture UEObjects that skipped LoadDefaults; after it both are always non-null.
            if (fx.GetProp(PK.LOD) is null) fx.Props["LOD"] = 1f;   // ctor 161073
            // ctor 161071: +0x2f8 bit0 (bUpdate) defaults TRUE — without it
            // every ramp (alpha/color/size) freezes (93833 gate).
            if (fx.GetProp(PK.bUpdate) is null) fx.Props["bUpdate"] = true;
            int particlesMax = UEValue.ToInt(fx.GetProp(PK.ParticlesMax));
            int distribution = UEValue.ToInt(fx.GetProp(PK.Distribution));
            bool steady = particlesMax == 0 && distribution != 1;
            fx.Props["bSteadyState"] = steady;
            bool prime = UEValue.ToBool(fx.GetProp(PK.bPrime));
            float timeSeconds = fx.GetProp(PK.Level) is UEObject li
                ? UEValue.AsFloat(li.GetProp(PK.TimeSeconds)) : 0f;
            if (steady && (prime || timeSeconds == 0f))
            {
                float lifeMax = RangeBase(fx, "Lifetime") + RangeRandPos(fx, "Lifetime");
                if (UEValue.AsFloat(fx.GetProp(PK.Age)) < lifeMax)
                    fx.Props["Age"] = lifeMax;
            }
            fx.Props["ElapsedTime"] = 0f;
        }

        static float RangeBase(UEObject o, string p)
            => o.GetProp(p) is UEObject bag ? UEValue.AsFloat(bag.GetProp(PK.Base)) : 0f;
        static float RangeRandPos(UEObject o, string p)
        {
            float r = o.GetProp(p) is UEObject bag ? UEValue.AsFloat(bag.GetProp(PK.Rand)) : 0f;
            return r < 0f ? 0f : r;
        }

        // AParticleFX::Tick prelude (Engine.dll.c:14138) — runs in the game
        // tick BEFORE the plain AActor::Tick body, exactly as the shipped
        // virtual does (it calls AActor::Tick at its end). No RNG. GIsEditor=0
        // headless, so the editor gates collapse:
        //   activity: the ParentBlend system's alive count >= 1, else
        //             ParticlesPerSec.Base + max(Rand,0) > 0;
        //   Age += dt;
        //   self-destroy: ParticlesMax>0 && pps.Base>0 &&
        //     ParticlesMax/pps.Base + Lifetime.Base + max(Lifetime.Rand,0)
        //       < Age(new)  → DestroyActor(this, 0).
        public void TickParticleFx(UEObject fx, float dt, Action<UEObject> destroy)
        {
            // Activity gate (review #12 P1): this+0x378 in the shipped body is
            // the emitter's OWN internal live-particle container (its virtual
            // +0x88 returns the live count) — NOT the ParentBlend property.
            // An emitter with pps=0 but particles still alive keeps aging.
            bool active;
            if (FxStateOf(fx).Particles.Count >= 1)
                active = true;
            else
                active = RangeBase(fx, "ParticlesPerSec") + RangeRandPos(fx, "ParticlesPerSec") > 0f;
            if (!active) return;
            // Age (+0x3a8) is a DECLARED script-visible var — prop bag.
            float age = UEValue.AsFloat(fx.GetProp(PK.Age)) + dt;
            fx.Props["Age"] = age;
            int particlesMax = UEValue.ToInt(fx.GetProp(PK.ParticlesMax));
            float ppsBase = RangeBase(fx, "ParticlesPerSec");
            if (particlesMax > 0 && ppsBase > 0f)
            {
                // Game tick, x87 PC64 (0x103c3d43..0x103c3d59): `fild Max; fdiv ppsBase; fadd RandPos; fadd Base` in
                // the register, `fcompp` against the stored float Age — ((M/p) + R) + B, unrounded to float.
                X87Ext sum = X87Ext.Round64(X87Ext.Round64(
                    X87Ext.Div64(X87Ext.From(particlesMax), X87Ext.From(ppsBase))
                    + X87Ext.From(RangeRandPos(fx, "Lifetime"))) + X87Ext.From(RangeBase(fx, "Lifetime")));
                if (X87Ext.From(age).CompareTo(sum) > 0)
                    destroy(fx);
            }
        }

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
        void AnnounceNoRenderPass()
        {
            if (_noPassAnnounced) return;
            _noPassAnnounced = true;
            HP1.Core.UEPlaythroughLog.Always("render",
                "render pass not bound: particles in the frame are not simulated (shipped simulates only drawn ones)");
        }

        // The GMath sine TABLE (16384 entries over the full circle) the
        // shipped emission uses for the spread direction (23511; disasm
        // 0x103c2c6a-0x103c2d2e: `fld dword [GMath(0x10608ca0) + 4*idx + 0x8c]`,
        // idx = units>>2 & 0x3fff; +0x4000 units = a quarter turn = cosine).
        // The table itself is FGlobalMath::TrigFLOAT, built once by the Core.dll
        // ctor — BrushCoords.SinTab owns that copy; recomputing it in float
        // differed from it in 8997 of 16384 entries.
        const float DegToRad = 0.01745329f;          // shipped literal (23502)
        const float RadToUnits = 65536f * 0.15915494f; // rad → rotator units
        static int AngleUnits(float rad) => (int)MathF.Round(rad * RadToUnits);
        internal static float GTabSin(int units) => BrushCoords.SinAngle(units);
        // The frontend's actor-destroy route (the render-side self-destroy
        // variant fires from Update when the budget is spent and no particles
        // remain — Engine.dll.c:106702 tail).
        public Action<UEObject> DestroyActor;

        // ── The RENDER-side particle half (recorded decision, phases 2+) ───
        // Shipped: the renderer calls AParticleFX::Update(0) per drawn
        // particle system (Render.dll.c DrawParticleSystem); Update computes
        // dt = Age - ElapsedTime (the render catches up to the game tick),
        // then EmitParticles/UpdateParticles run — emission consumes the
        // render appFrand stream in the exact order of the banked RE map.
        // The port runs the same traversal: DrawFrame steps a ParticleFX
        // (RenderStepFx) when its sprite is drawn, once per actor per frame, so a
        // culled emitter is not updated, as in shipped. LOD (+0x300) keeps the
        // ctor value 1.0 or a loaded override: Render.dll has no operand at
        // +0x300 at all (disasm scan), so nothing in the renderer writes it.
        static float PropF(UEObject o, string p) => UEValue.AsFloat(o.GetProp(p));

        float SampleRange(UEObject src, string prop)
        {
            // GetParams quirk (Engine.dll.c:30726): appFrand is consumed ONLY
            // when Rand != 0; Rand == 0 returns Base with NO call.
            if (src.GetProp(prop) is not UEObject bag) return 0f;
            float b = UEValue.AsFloat(bag.GetProp(PK.Base));
            float r = UEValue.AsFloat(bag.GetProp(PK.Rand));
            if (r == 0f) return b;
            return Frand() * r + b;
        }

        // 0.003921569 (Engine .rdata, the FColor -> float factor of EmitParticles and UParticle::Update).
        const float ColorByteScale = 0.003921569f;

        UEVector SampleColor(UEObject src, string prop)
        {
            // ColorParams {Base:{R,G,B}, Rand:{R,G,B}} — one conditional
            // appFrand for the whole color (byte components / 255).
            if (src.GetProp(prop) is not UEObject bag) return default;
            var bs = bag.GetProp(PK.Base) as UEObject;
            var rn = bag.GetProp(PK.Rand) as UEObject;
            float br = UEValue.AsByte(bs?.GetProp(PK.R)), bg = UEValue.AsByte(bs?.GetProp(PK.G)), bb = UEValue.AsByte(bs?.GetProp(PK.B));
            float rr = UEValue.AsByte(rn?.GetProp(PK.R)), rg = UEValue.AsByte(rn?.GetProp(PK.G)), rb = UEValue.AsByte(rn?.GetProp(PK.B));
            // The "no random" test compares the whole FColor dword, alpha included (`cmp [esi+0x2b4], ebx` 0x103c554c,
            // `[esi+0x2bc]` 0x103c5704).
            // The sample stays an FColor (bytes) in FParams; EmitParticles converts it (ColorByteScale below).
            if (rr == 0f && rg == 0f && rb == 0f && UEValue.AsByte(rn?.GetProp("A")) == 0)
                return new UEVector(br, bg, bb);
            // AParticleFX::GetParams (Engine.dll.c:30836-30945): channel addend —
            // ROUND(Rand·f), clamped to 0..255, the sum with Base saturates at 255.
            // The original particle colour never exceeds 1.
            float f = Frand();
            static float Sat(float rand, float bas, float f)
            {
                int add = (int)MathF.Round(rand * f);   // fistp: round to nearest even
                if (add < 0) add = 0; else if (add > 254) add = 255;
                int sum = (int)bas + add;
                if (sum > 254) sum = 255;
                return sum;
            }
            return new UEVector(Sat(rr, br, f), Sat(rg, bg, f), Sat(rb, bb, f));
        }

        // The per-particle FParams sample (GetParams, Engine.dll.c:30726). SpreadW, SpreadH, SourceW, SourceD, SourceH,
        // Speed, Lifetime, AlphaStart, AlphaEnd in the shipped order (disasm 0x103c5315..0x103c5417 reads actor
        // +0x28c, +0x294, +0x264, +0x274, +0x26c), then SpinRate (+0x2e8, 0x103c54e6) and DripTime (+0x2f0, 0x103c5519)
        // BEFORE ColorStart (+0x2b4, 0x103c554c) / ColorEnd (+0x2bc, 0x103c5704), then the size (Width/Length share one
        // appFrand, 0x103c58c1), SizeEndScale (+0x2e0, 0x103c58ea) last. The order decides which draw lands in which
        // field — the particle colour is visible (live batch 9 particle dumps: Color.y off by up to 28/255 with the
        // former Color-first order).
        struct FParams
        {
            public float SpreadW, SpreadH, SourceW, SourceD, SourceH;
            public float Speed, Lifetime, AlphaStart, AlphaEnd;
            public UEVector ColorStart, ColorEnd;
            public float Width, Length, SizeEndScale, SpinRate, DripTime;
        }

        FParams GetParams(UEObject src)
        {
            FParams p;
            p.SpreadW = SampleRange(src, "AngularSpreadWidth");
            p.SpreadH = SampleRange(src, "AngularSpreadHeight");
            p.SourceW = SampleRange(src, "SourceWidth");
            p.SourceD = SampleRange(src, "SourceDepth");
            p.SourceH = SampleRange(src, "SourceHeight");
            p.Speed = SampleRange(src, "Speed");
            p.Lifetime = SampleRange(src, "Lifetime");
            p.AlphaStart = SampleRange(src, "AlphaStart");
            p.AlphaEnd = SampleRange(src, "AlphaEnd");
            p.SpinRate = SampleRange(src, "SpinRate");
            p.DripTime = SampleRange(src, "DripTime");
            p.ColorStart = SampleColor(src, "ColorStart");
            p.ColorEnd = SampleColor(src, "ColorEnd");
            // Width/Length: ONE shared UNCONDITIONAL appFrand (30870 tail).
            float f = Frand();
            float wb = 0f, wr = 0f, lb = 0f, lr = 0f;
            if (src.GetProp(PK.SizeWidth) is UEObject wbag)
            { wb = UEValue.AsFloat(wbag.GetProp(PK.Base)); wr = UEValue.AsFloat(wbag.GetProp(PK.Rand)); }
            if (src.GetProp(PK.SizeLength) is UEObject lbag)
            { lb = UEValue.AsFloat(lbag.GetProp(PK.Base)); lr = UEValue.AsFloat(lbag.GetProp(PK.Rand)); }
            p.Width = f * wr + wb;
            p.Length = f * lr + lb;
            p.SizeEndScale = SampleRange(src, "SizeEndScale");
            return p;
        }

        // AParticleFX::EmitParticles (Engine.dll.c:23044) — the banked RE map.
        // Default, Pattern and OwnerMesh distributions are ported; ColorPalette
        // and a fractional ParentBlend STOP loudly (no carriers in the campaign).
        void EmitParticles(UEObject fx, FxState s, float dt)
        {
            float emitDelay = PropF(fx, "EmitDelay") + dt;
            fx.Props["EmitDelay"] = emitDelay;
            UEVector loc = UEValue.AsVector3(fx.GetProp(PK.Location));
            // pps sample — UNCONDITIONAL appFrand (23245).
            float pps = Frand() * RangeRandRaw(fx, "ParticlesPerSec") + RangeBase(fx, "ParticlesPerSec");
            // ── ParentBlend (23246 + AParticleFX::GetSysParams, 17069) ─────
            //
            // The second parameter source is the PARENT CLASS DEFAULT.
            // Shipped chain: emitter->Class (UObject+0x24) -> SuperField
            // (UStruct+0x28) -> +0x4dc. The middle link is PROVEN, not inferred:
            // the UStruct::IsChildOf body (Core.dll.c) walks ancestors
            // by exactly this offset — `this = *(UStruct**)(this + 0x28)`.
            //
            // The two forks are DIFFERENT and must not be merged:
            //   * the pps counter is blended when ParentBlend != 0 (23246), and the
            //     SECOND appFrand is spent unconditionally, even with blend outside [0,1];
            //   * the particle parameter source (GetSysParams) is chosen by
            //     `<= 0` -> the emitter itself, `>= 1` -> the parent default, in between ->
            //     a blended copy of the whole property block.
            float parentBlend = PropF(fx, "ParentBlend");
            UEObject sysParams = fx;
            if (parentBlend != 0f)
            {
                UEObject parentDef = ParentClassDefaults(fx);
                float pf = Frand();
                float parentPps = pf * RangeRandRaw(parentDef, "ParticlesPerSec")
                                + RangeBase(parentDef, "ParticlesPerSec");
                pps = (parentPps - pps) * parentBlend + pps;
                if (parentBlend > 0f)
                {
                    // FRACTIONAL BLENDING IS NOT PORTED — MEASURED, NOT DEFERRED
                    // BLINDLY: across the whole campaign ParentBlend takes
                    // only 0 or 1 (SpellLearnTrigger.uc:710-723 switches
                    // two systems, ParticleFadeFX.uc:8-14 likewise, and
                    // BroomHarry.uc:273 uses Min(Speed/200, 1), an integer).
                    // Nobody sets intermediate values.
                    if (parentBlend < 1f)
                        throw new NotSupportedException(
                            $"ParticleFX ParentBlend={parentBlend} ({fx.Class?.Name}) — fractional "
                            + "blending copies the whole property block (0x3cc) and lerps ~22 fields "
                            + "plus colors (17096-17535); the campaign has no such value");
                    sysParams = parentDef;
                }
            }
            int distribution = UEValue.ToInt(fx.GetProp(PK.Distribution));
            // ── DIST_Pattern / DIST_Path (23252-23281) ────────────────────
            //
            // With Distribution==1 ParticlesPerSec is REINTERPRETED as a STEP, i.e.
            // how many length units per particle — the counter is obtained by
            // DIVIDING the length by it (23278), not by multiplying by time
            // as in the other modes.
            //
            // Pattern is a Gesture (an object with a Points array; set from
            // script: SpellLearnFX.DrawSpell "Sparkle.Pattern = Gesture",
            // SpellLearnTrigger, Target). The meaning of Period comes from there too:
            // DrawState.Tick drives Period.Base as PROGRESS along the gesture in [0,1],
            // and Period.Rand as the progress increment FOR THIS FRAME.
            //
            // THE FORK IS ON THE `Pattern` OBJECT ITSELF (23253 tests the
            // pointer), NOT on its array: an object without usable points is
            // not "a path without a pattern", and routing it to another sub-path would
            // silently diverge from shipped. That case stops out loud.
            //
            // THE PATTERN HAS TWO ROLES, AND ONLY ONE DEPENDS ON Distribution (disasm
            // EmitParticles 0x103c2170): COUNT by pattern length — with
            // Distribution == 1 (0x103c2272/0x103c22cd); SPAWN POINT — with
            // any Distribution ≠ 2 (the mesh branch) if Pattern is non-empty
            // (0x103c2819 `cmp Distribution,2; jne 0x103c2a3e` -> 0x103c2a3e
            // `mov esi,[ebx+0x338]; test esi,esi`). Target.DrawSpellFX sets
            // Pattern = the spell gesture on Les_SpellShape/SpellShape_* effects
            // (Distribution = 0): in shipped particles follow the symbol outline
            // stretched to DrawScale = target size.
            object patternRaw = fx.GetProp(PK.Pattern);
            var patternObj = patternRaw as UEObject;
            if (patternRaw is not null && patternObj is null)
                throw new NotSupportedException(
                    $"ParticleFX DIST_Pattern ({fx.Class?.Name}): Pattern holds "
                    + $"{patternRaw.GetType().Name}, not an object with a Points array");
            var patternPoints = patternObj?.GetProp(PK.Points) as System.Collections.IList;
            if (patternObj is not null && (patternPoints is null || patternPoints.Count < 2))
                throw new NotSupportedException(
                    $"ParticleFX DIST_Pattern ({fx.Class?.Name}): Pattern has "
                    + $"{patternPoints?.Count.ToString() ?? "no array"} points. The shipped body "
                    + "indexes points[idx+1] without a lower bound check and with <2 points reads foreign "
                    + "memory; the script (SpellLearnFX.DrawSpell) rejects such a gesture itself");
            // ── DIST_OwnerMesh (23386-23402): preparation once per call ──
            //
            // Entry: Distribution==2 AND an Owner exists AND it has a Mesh. Vertices
            // come from GetFrame with UNIT coords (GMath+0x18) — and
            // come out IN WORLD space: the GetFrame root divides the actor's mesh coords from
            // the ApplyAnim frame header (+0x20 = GetMeshCoords) by the
            // inverse of the input ones (UELevelRuntime.EvaluatePosePoints, disasm there).
            UEVector[] meshPoints = null;
            int[] meshTris = null;
            if (distribution == 2)
            {
                var owner = fx.GetProp(PK.Owner) as UEObject;
                var meshRef = owner?.GetProp(PK.Mesh) as UEResourceRef;
                if (owner is null || meshRef is null)
                    throw new NotSupportedException(
                        $"ParticleFX DIST_OwnerMesh ({fx.Class?.Name}): no Owner or it has no Mesh — "
                        + "shipped does not emit from the mesh in this case, and the port must see it");
                if (MeshCatalog is null)
                    throw new NotSupportedException(
                        $"ParticleFX DIST_OwnerMesh ({fx.Class?.Name}): mesh catalog not supplied to the producer");
                // FRAME POSE — from Core (UELevelRuntime.SkeletalPose, port of
                // ApplyAnim + GetFrame): GetFrame with unit coords gives
                // world-space vertices. Without the runtime hook (the producer
                // does not hold the level) — a LOUD STOP, not a silent reference pose.
                object seq = owner.GetProp(PK.AnimSequence);
                string seqName = seq?.ToString();
                Hp1SkeletalMesh sk = MeshCatalog.Get(meshRef);
                if (PosePoints is null)
                    throw new NotSupportedException(
                        $"ParticleFX DIST_OwnerMesh ({fx.Class?.Name}): owner pose not bound to the producer "
                        + $"(AnimSequence='{seqName}')");
                meshPoints = PosePoints(owner);
                OwnerMeshPointsForTest?.Invoke(fx, meshPoints);
                meshTris = sk.TriVerts;
                if (meshTris.Length == 0)
                    throw new NotSupportedException(
                        $"ParticleFX DIST_OwnerMesh ({fx.Class?.Name}): mesh {meshRef.Name} has no faces");
            }
            float count;
            if (distribution == 1)
            {
                // The LENGTH over which particles are laid out differs between
                // the two sub-paths (23253-23279). Both end with a division by pps.
                if (patternObj is null)
                {
                    // Without Pattern — the emitter's path length over the frame (23256).
                    UEVector le = UEValue.AsVector3(fx.GetProp(PK.LastEmitLocation));
                    float px = le.x - loc.x, py = le.y - loc.y, pz = le.z - loc.z;
                    count = (float)Math.Sqrt(px * px + py * py + pz * pz);
                }
                else
                {
                    // With Pattern — an estimate of the gesture arc length DRAWN this frame:
                    // the length of ONE segment (taken at the MIDDLE of the window: shipped
                    // substitutes the literal 0.5 where a particle's
                    // fraction would go — .rdata 0x1046e9c0), times the number of
                    // segments, DrawScale and the frame share Period.Rand.
                    float periodRand0 = RangeRandRaw(fx, "Period");
                    int np0 = patternPoints.Count;
                    int idx0 = PatternSegment(0.5f, RangeBase(fx, "Period"), periodRand0, np0, out _);
                    UEVector c0 = UEValue.AsVector3(patternPoints[idx0]);
                    UEVector n0 = UEValue.AsVector3(patternPoints[idx0 + 1]);
                    float sx = c0.x - n0.x, sy = c0.y - n0.y, sz = c0.z - n0.z;
                    float segLen = (float)Math.Sqrt(sx * sx + sy * sy + sz * sz);
                    // Multiplication order as in the body (0x103c2367-0x103c2372):
                    // first (np-1)·DrawScale, then by the length, then by the share.
                    count = segLen * ((np0 - 1) * PropF(fx, "DrawScale")) * periodRand0;
                }
                count /= pps;
            }
            else
            {
                // count = clamp(LOD*3, 0.1..1) * pps * EmitDelay (+ residue).
                float lod = PropF(fx, "LOD");
                float lodF = lod * 3f;
                count = lodF >= 0.1f
                    ? (lodF >= 1f ? 1f : lodF) * pps * emitDelay
                    : pps * 0.1f * emitDelay;
            }
            count += PropF(fx, "EmissionResidue");
            float windowStart = -emitDelay;
            int emitted = UEValue.ToInt(fx.GetProp(PK.ParticlesEmitted));
            int particlesMax = UEValue.ToInt(fx.GetProp(PK.ParticlesMax));
            if (particlesMax > 0)
            {
                float remaining = particlesMax - emitted;
                if (remaining <= count)
                {
                    emitDelay = (remaining / count) * emitDelay;
                    fx.Props["EmitDelay"] = emitDelay;
                    count = remaining;
                }
            }
            float lifeCap = RangeBase(fx, "Lifetime") + RangeRandPos(fx, "Lifetime");
            if (windowStart < -lifeCap)
            {
                float trimmed = lifeCap + windowStart + emitDelay;
                count = (trimmed / emitDelay) * count;
                fx.Props["EmitDelay"] = emitDelay = trimmed;
                windowStart = -lifeCap;
            }
            // ftol (23412 `iStack_6c = ftol()`) — truncation TOWARD ZERO. Inside the
            // range it is an ordinary cast; OUTSIDE it x87 yields the "integer
            // indefinite", and in either form (0 for the qword variant,
            // 0x80000000 for dword) the very next check `0 < n` is false — i.e.
            // shipped spawns NO particles. C# SATURATES the same input to
            // int.MaxValue, which would give a two-billion-iteration loop:
            // a divergence from the original, not a guard against it.
            //
            // The input is reachable because of the division by pps (with Distribution==1
            // ParticlesPerSec is the DENOMINATOR). Zero never occurs there in the campaign —
            // MEASURED: the base class default ParticlesPerSec.Base = 3, and none of the
            // five classes overrides it (the pattern fixture prints the measurement).
            int n = float.IsNaN(count) || count >= 2147483648f || count <= -2147483648f
                ? 0 : (int)count;
            fx.Props["EmissionResidue"] = count - n;
            if (n <= 0) return;
            int aliveLimit = UEValue.ToInt(fx.GetProp(PK.ParticlesAlive));
            if (aliveLimit > 0 && s.Particles.Count + n > aliveLimit)
            {
                if (aliveLimit < n) n = aliveLimit;
                int evict = s.Particles.Count - aliveLimit + n;
                // Shipped evicts the OLDEST via the container virtuals.
                if (evict > 0) s.Particles.RemoveRange(0, Math.Min(evict, s.Particles.Count));
            }
            bool velRelative = (UEValue.ToInt(fx.GetProp(PK.bVelocityRelative)) != 0)
                || UEValue.ToBool(fx.GetProp(PK.bVelocityRelative));
            UEVector ownerVel = velRelative && fx.GetProp(PK.Owner) is UEObject own
                ? UEValue.AsVector3(own.GetProp(PK.Velocity)) : default;
            int renderPrimitive = UEValue.ToInt(fx.GetProp(PK.RenderPrimitive));
            if (fx.GetProp(PK.ColorPalette) is not null)
                throw new NotSupportedException(
                    $"ParticleFX ColorPalette reached ({fx.Class?.Name}) — the palette ramp is not ported yet");
            var rot = UEValue.AsRotator(fx.GetProp(PK.Rotation));
            // The emitter coords (0x103c249a..0x103c2519: GMath.UnitCoords, Origin -= Location, FCoords::operator*=(Rotation)
            // through the Core import [0x10608cd0], render window PC24; kept at ebp-0x248): the spread direction
            // (0x103c2d6e..0x103c2dc2) and the volume offset (0x103c2e94..) are FVector::TransformVectorBy on THESE axes,
            // (x·C.X.x + y·C.X.y) + z·C.X.z per component.
            // Not GetAxes (UnitCoords / R, transposed): the same rotation by other roundings (Vel 1 ulp, Lev5_FlyKeys).
            BrushCoords emitCoords = BrushCoords.Identity;
            emitCoords.MulRotator(rot.Pitch, rot.Yaw, rot.Roll);
            // UParticle +0x78 ChaosTimer and +0x7c SpinPos of the stack particle (ebp-0x17c): neither the ctor
            // (0x103bfbb0: +0..+0x24, +0x84) nor EmitParticles writes them (SpinPos only for RenderPrimitive 3,
            // 0x103c31f6..0x103c321b), so each particle inherits the previous one's values after its catch-up
            // (gated-out particles leave the slot as it was). The first particle reads ebp-0x104/-0x100 as the caller
            // left them: EmitParticles and UpdateParticles are called from the same esp (AParticleFX::Update
            // 0x103c4098 / 0x103c411e), so the slot is what the caller path and UpdateParticles' callees left there
            // (_slot*, UpdateFxParticles): per path, D-73. On the render path every particle's values after its catch-up
            // stay at that absolute address for the next render-path call (RenderSlotChaos/Spin).
            bool carryKnown = false;
            float carryChaos = 0f, carrySpin = 0f;
            for (int i = 0; i < n; i++)
            {
                // The parameter source is the GetSysParams result, not always the
                // emitter itself: shipped calls it ONCE before the loop and feeds it to
                // GetParams for every particle. Other fields (LOD, Rotation,
                // Location) are still read from `fx`, as in the body.
                FParams fp = GetParams(sysParams);
                // fraction: with Distribution==1 it is DETERMINISTIC — (i+1)/count
                // (23407-23415), and the appFrand at the loop head is NOT SPENT AT ALL.
                // That is the only difference of this mode's RNG stream from the normal one:
                // spread and volume below consume it the same way (unlike the mesh
                // branch, which REPLACES the two spread calls). Divided by the
                // FRACTIONAL count — the same one n was taken from, before truncation.
                float fraction = distribution == 1
                    ? (count <= 0f ? 0f : (i + 1) / count)
                    : Frand();
                var p = new Particle();
                float birthOffset = -windowStart - fraction * emitDelay;
                // Birth gate (disasm 0x103c27e6..0x103c2813): `fcomp [ebp-0xc4]` (the sampled Lifetime, FParams+0x24);
                // `test ah,1; je 0x103c353b` — a birth offset NOT below its Lifetime (NaN passes) skips the particle
                // entirely: no mesh/spread/volume/SpinPos draws, no catch-up, no AddParticle (vtable+0x78 at
                // 0x103c3538). GetParams and the fraction draw above are already spent; ParticlesEmitted still counts n.
                if (birthOffset >= fp.Lifetime) continue;
                // Direction (23502): TWO appFrand(min,max) spread half-angles in
                // DEGREES→rad (·0.01745329), quantized to rotator units and
                // pushed through the GMath sine table; the local direction is
                // (cosYaw·cosPitch, sinYaw·cosPitch, sinPitch) — the rotator→
                // vector convention — rotated by the emitter's axes.
                // ── DIST_OwnerMesh: mesh sampling REPLACES the spread ─────────
                // RNG order read from the whole fork 23431-23530:
                // appRandRange(triangle) -> two appFrand (barycentric), and that is
                // INSTEAD of the two spread appFrands, not in addition. The three volume
                // calls below are the common tail of both branches.
                UEVector meshBase = default, meshNormal = default;
                if (meshPoints is not null)
                {
                    int tri = RandRange(0, meshTris.Length / 3 - 1);
                    // WHICH SLOT IS WHICH — from the disassembly, not the decompile
                    // (Ghidra's iStack_294/298/29c are ebp-0x290/-0x294/-0x298):
                    // GetTriVerts writes out[0..2] at ebp-0x298 (0x103c2859);
                    // f2*f1 multiplies out[2] (0x103c2887), (1-f2)*f1 out[1]
                    // (0x103c288d), 1-f1 out[0] (0x103c28a1); the normal is
                    // (out[1]-out[0]) x (out[2]-out[0]) (0x103c2971..0x103c29e2).
                    // So v0 below is out[2] and v2 is out[0]. Reading them the
                    // other way round flipped every face normal (the sparks flew
                    // INTO the mesh) and moved the 1-f1 weight to the wrong corner
                    // (Lev_Tut3 Lock0 cloud ~20% tighter than the original's).
                    UEVector v0 = meshPoints[meshTris[tri * 3 + 2]];
                    UEVector v1 = meshPoints[meshTris[tri * 3 + 1]];
                    UEVector v2 = meshPoints[meshTris[tri * 3]];
                    float f1 = Frand();
                    float f2 = Frand();
                    float w0 = f2 * f1, w1 = (1f - f2) * f1, w2 = 1f - f1;
                    meshBase = new UEVector(
                        w2 * v2.x + w1 * v1.x + w0 * v0.x,
                        w2 * v2.y + w1 * v1.y + w0 * v0.y,
                        w2 * v2.z + w1 * v1.z + w0 * v0.z);
                    // Face normal: cross(v1-v2, v0-v2), then SafeNormal.
                    float e0x = v0.x - v2.x, e0y = v0.y - v2.y, e0z = v0.z - v2.z;
                    float e1x = v1.x - v2.x, e1y = v1.y - v2.y, e1z = v1.z - v2.z;
                    // SafeNormal with SHIPPED semantics explicitly: below the threshold
                    // it returns ZERO, not normalised noise. Bare
                    // `v.normalized` copies in the tree are known to be wrong and must not
                    // be used here.
                    float nx = e1y * e0z - e1z * e0y;
                    float ny = e1z * e0x - e1x * e0z;
                    float nz = e1x * e0y - e1y * e0x;
                    // Core FVector::SafeNormal (0x1014f4e0): (x² + y²) + z², below [0x1017d72c] zero; otherwise
                    // scale = 1 / appSqrt(sum) (fdivr 0x1014f557) and each component is MULTIPLIED by it (0x1014f566..
                    // 0x1014f581) — not divided by the root.
                    float sq = nx * nx + ny * ny + nz * nz;
                    float invLen = sq < 1e-8f ? 0f : 1f / (float)Math.Sqrt(sq);
                    meshNormal = sq < 1e-8f ? default
                        : new UEVector(invLen * nx, invLen * ny, invLen * nz);
                }
                // ── DIST_Pattern: the base point is a GESTURE POINT (23470-23496) ──
                //
                // The branch lives in THE SAME else as the normal base, so it does NOT
                // cancel the spread below; it does not touch the RNG at all.
                // Position along the gesture: x = (fraction·Period.Rand + Period.Base)
                // ·(N−1) — frame progress plus the particle's share within the frame;
                // the integer part selects the segment, the fraction — linear interpolation
                // between its ends.
                UEVector patternLocal = default;
                if (patternPoints is not null)
                {
                    int np = patternPoints.Count;
                    int idx = PatternSegment(fraction, RangeBase(fx, "Period"),
                                             RangeRandRaw(fx, "Period"), np, out float f);
                    UEVector cur = UEValue.AsVector3(patternPoints[idx]);
                    UEVector nxt = UEValue.AsVector3(patternPoints[idx + 1]);
                    float lerpX = (1f - f) * cur.x + f * nxt.x;
                    float lerpY = (1f - f) * cur.y + f * nxt.y;
                    // The gesture is normalised to [0,1]²: it is centred by subtracting
                    // 0.5, scaled by DrawScale and placed in the Y-Z PLANE
                    // of the emitter's local axes — X exactly zero, and the second
                    // gesture coordinate inverted (screen order
                    // top to bottom): {0, S·(x−0.5), S·(0.5−y)}.
                    float scale = PropF(fx, "DrawScale");
                    patternLocal = new UEVector(0f, scale * (lerpX - 0.5f), (0.5f - lerpY) * scale);
                }
                float yawRad = meshPoints is not null ? 0f
                    : FrandRange(fp.SpreadW * -DegToRad, fp.SpreadW * DegToRad);
                float pitchRad = meshPoints is not null ? 0f
                    : FrandRange(fp.SpreadH * -DegToRad, fp.SpreadH * DegToRad);
                int yawU = AngleUnits(yawRad), pitchU = AngleUnits(pitchRad);
                float cosPitch = GTabSin(pitchU + 0x4000);
                float lx = GTabSin(yawU + 0x4000) * cosPitch;
                float ly = GTabSin(yawU) * cosPitch;
                float lz = GTabSin(pitchU);
                // In the mesh branch the direction is the FACE NORMAL (23455-23468), not
                // the rotated spread: shipped puts SafeNormal(cross) straight into the
                // same direction slot.
                UEVector dir = meshPoints is not null ? meshNormal : new UEVector(
                    (lx * emitCoords.X.x + ly * emitCoords.X.y) + lz * emitCoords.X.z,
                    (lx * emitCoords.Y.x + ly * emitCoords.Y.y) + lz * emitCoords.Y.z,
                    (lx * emitCoords.Z.x + ly * emitCoords.Z.y) + lz * emitCoords.Z.z);
                // Source volume: THREE appFrand(−size/2, +size/2) — the sampled
                // SourceW/D/H are BOX SIZES and the offsets are CENTRED. From the
                // disassembly (FParams at ebp−0xe8, GetParams 0x103c52f0 stores
                // SourceWidth +4, SourceHeight +8, SourceDepth +0xc): the draws
                // are Height (0x103c2ddd, [ebp−0xe0]), Width (0x103c2e09,
                // [ebp−0xe4]), Depth (0x103c2e35, [ebp−0xdc]); the vector at
                // ebp−0x7c is stored (Depth, Width, Height) (0x103c2e5e..
                // 0x103c2e7f) and transformed by the emitter coords, so
                // Depth→X (forward), Width→Y, Height→Z.
                float offH = FrandRange(fp.SourceH * -0.5f, fp.SourceH * 0.5f);
                float offW = FrandRange(fp.SourceW * -0.5f, fp.SourceW * 0.5f);
                float offD = FrandRange(fp.SourceD * -0.5f, fp.SourceD * 0.5f);
                // Position (23611): base + rotated box + (LastEmitLocation −
                // Location)·(1−fraction): newborns (fraction→1) at the CURRENT
                // location, window-old ones back along the emitter's path
                // (zero on the first update — the 106729 prelude seeds
                // LastEmitLocation = Location before any emission).
                UEVector lastEmit = UEValue.AsVector3(fx.GetProp(PK.LastEmitLocation));
                float back = 1f - fraction;
                // BASE POINT: normally the emitter Location (23472-23474), in the
                // mesh branch the sampled surface point (23461-23463),
                // and in the gesture branch the pattern point MOVED INTO THE WORLD. Shipped
                // does the latter via FVector::TransformPointBy over the
                // coords "GMath.UnitCoords with Origin=−Location, rotated
                // by Rotation" (23484-23496).
                //
                // WHAT THAT POINT IS WAS MEASURED BY EXECUTION, NOT DERIVED FROM
                // CONVENTIONS: the real Core.dll bodies (FCoords::operator*=
                // ord 501 and TransformPointBy ord 1365) run under unicorn with
                // FPCW=0x037F on 6 (Location, Rotation) pairs; exactly the
                // variant "Location + local vector along the emitter axes" matched
                // (worst difference 6.7e-05 = float noise vs table
                // sines), while the inverse convention misses by 10
                // units. So it is THE SAME technique as already used below for the
                // volume offsets, and the oracle confirmed that too.
                // (Not judged: no DIST_Pattern sample; keeps the GetAxes form the unicorn check above measured.)
                var (axX, axY, axZ) = patternPoints is not null ? UEValue.GetAxes(rot) : default;
                UEVector emitBase = meshPoints is not null ? meshBase
                    : patternPoints is not null
                        ? new UEVector(
                            loc.x + axY.x * patternLocal.y + axZ.x * patternLocal.z,
                            loc.y + axY.y * patternLocal.y + axZ.y * patternLocal.z,
                            loc.z + axY.z * patternLocal.y + axZ.z * patternLocal.z)
                        : loc;
                // Order (render window PC24, every op rounds to float): the volume offset (Depth, Width, Height) goes
                // through the coords FIRST — (D·a + W·b) + H·c per component (0x103c2e94..0x103c2ee8) — then
                // offset + base (0x103c2eeb..0x103c2f2f), then + (1 − fraction)·(LastEmitLocation − Location)
                // (0x103c2f35..0x103c2f7c).
                UEVector vol = new UEVector(
                    (offD * emitCoords.X.x + offW * emitCoords.X.y) + offH * emitCoords.X.z,
                    (offD * emitCoords.Y.x + offW * emitCoords.Y.y) + offH * emitCoords.Y.z,
                    (offD * emitCoords.Z.x + offW * emitCoords.Z.y) + offH * emitCoords.Z.z);
                p.Pos = new UEVector(
                    (vol.x + emitBase.x) + back * (lastEmit.x - loc.x),
                    (emitBase.y + vol.y) + back * (lastEmit.y - loc.y),
                    (emitBase.z + vol.z) + back * (lastEmit.z - loc.z));
                p.Vel = new UEVector(dir.x * fp.Speed + ownerVel.x,
                                     dir.y * fp.Speed + ownerVel.y,
                                     dir.z * fp.Speed + ownerVel.z);
                p.OldPos = p.Pos;
                p.Lifetime = fp.Lifetime;
                p.Alpha = PropF(fx, "AlphaGrowPeriod") > 0f ? 0f : fp.AlphaStart;
                p.AlphaMax = fp.AlphaStart;
                p.AlphaDelta = fp.Lifetime > PropF(fx, "AlphaDelay")
                    ? (fp.AlphaEnd - fp.AlphaStart) / (fp.Lifetime - PropF(fx, "AlphaDelay"))
                    : 0f;
                // FColor -> FPlane (Engine.dll.c:23579..23585): byte · 0.003921569 (a float multiply, not /255); the
                // delta takes the INTEGER byte difference (end − start) · 0.003921569, then / (Lifetime − ColorDelay)
                // (23614..23620).
                p.Color = new UEVector(fp.ColorStart.x * ColorByteScale, fp.ColorStart.y * ColorByteScale, fp.ColorStart.z * ColorByteScale);
                float colorSpan = fp.Lifetime - PropF(fx, "ColorDelay");
                p.ColorDelta = fp.Lifetime > PropF(fx, "ColorDelay")
                    ? new UEVector((fp.ColorEnd.x - fp.ColorStart.x) * ColorByteScale / colorSpan,
                                   (fp.ColorEnd.y - fp.ColorStart.y) * ColorByteScale / colorSpan,
                                   (fp.ColorEnd.z - fp.ColorStart.z) * ColorByteScale / colorSpan)
                    : default;
                p.Width = fp.Width; p.WidthMax = fp.Width;
                p.Length = fp.Length; p.LengthMax = fp.Length;
                // SizeGrowPeriod > 0 (0x103c2f82..0x103c2fad): Width (+0x58) and Length (+0x64) start at 0; the Max
                // fields keep the sample.
                if (PropF(fx, "SizeGrowPeriod") > 0f) { p.Width = 0f; p.Length = 0f; }
                if (fp.DripTime > 0f || fp.DripTime < 0f)
                {
                    // DripTime != 0 (`test ah,0x40; je 0x103c304f`, 0x103c304f..0x103c3081): the deltas spread the
                    // CURRENT size (already 0 after SizeGrowPeriod) over the drip, then the size zeroes.
                    p.WidthDelta = p.Width / fp.DripTime; p.Width = 0f;
                    p.LengthDelta = p.Length / fp.DripTime; p.Length = 0f;
                }
                else
                {
                    float endScale = fp.SizeEndScale;
                    float sizeSpan = fp.Lifetime - PropF(fx, "SizeDelay");
                    p.WidthDelta = sizeSpan > 0f ? (fp.Width * endScale - fp.Width) / sizeSpan : 0f;
                    p.LengthDelta = sizeSpan > 0f ? (fp.Length * endScale - fp.Length) / sizeSpan : 0f;
                }
                p.DripTimer = fp.DripTime;
                // D-73: the first particle of a call reads the slot (_slotKind): the bSystemRelative YAxis residue, the
                // Setup BoundVisible residue (GetRenderBoundingBox path) or the chain (render path).
                if (!carryKnown)
                {
                    carryChaos = _slotChaos; carrySpin = _slotSpin;
                    // Validation seam only (headless, InternalsVisibleTo): the live comparison supplies the value the
                    // original's first particle read where the original's slot did not come from game code (D-73: the
                    // render device path overwrote it — a driver value, not emulated by the author's decision).
                    if (FirstNewbornSlotForTest?.Invoke(s.Particles.Count, _grbbPath, (carryChaos, carrySpin)) is var (rc, rs))
                    { carryChaos = rc; carrySpin = rs; }
                    if (_slotKind == SlotKind.Chain) ChainParticleSlot++;
                    else if (_slotKind == SlotKind.SetupBoundVisible) SetupResidueParticleSlot++;
                    else if (_slotKind == SlotKind.SysRel) SysRelParticleSlot++;
                }
                p.ChaosTimer = carryChaos;
                p.SpinRate = fp.SpinRate;
                // RenderPrimitive 3: random spin phase (appFrand * 2π, 23598).
                p.SpinPos = renderPrimitive == 3 ? Frand() * 6.2831855f : carrySpin;
                // Newborn back-integration (23644) runs AFTER the particle is
                // FULLY initialized (Lifetime, ramp deltas, DripTimer,
                // SpinRate all set — review #14 P0: calling it earlier
                // advanced position/Age on a half-built particle): a particle
                // born mid-window is caught up to "now" by its birth offset
                // through the same substep loop as the update pass.
                if (birthOffset > 0f) AdvanceNewborn(fx, p, birthOffset);
                carryKnown = true; carryChaos = p.ChaosTimer; carrySpin = p.SpinPos;
                if (!_grbbPath) { RenderSlotChaos = p.ChaosTimer; RenderSlotSpin = p.SpinPos; }
                // After the catch-up, before AddParticle (0x103c34f8..0x103c3529): CurrentPriorityTag (+0x3c4) += 1, wrapped
                // to 0 above 9 (`cmp eax, 9; jle`), and the particle takes PriorityTable[CurrentPriorityTag].
                int tag = UEValue.ToInt(fx.GetProp(PK.CurrentPriorityTag)) + 1;
                if (tag > 9) tag = 0;
                fx.Props["CurrentPriorityTag"] = tag;
                p.Priority = PriorityTable[tag];
                s.Particles.Add(p);
            }
            // ParticlesEmitted += n (0x103c3567..0x103c3575) — gated-out particles count too.
            fx.Props["ParticlesEmitted"] = emitted + n;
            fx.Props["EmitDelay"] = 0f;
            fx.Props["LastEmitLocation"] = loc;
        }

        static float RangeRandRaw(UEObject o, string p)
            => o.GetProp(p) is UEObject bag ? UEValue.AsFloat(bag.GetProp(PK.Rand)) : 0f;

        // The default object of the emitter's PARENT class (see the ParentBlend notes).
        // Cached per class: shipped reads a ready object by pointer, i.e. the same
        // one for all emitters of the class, and rebuilding it every frame
        // would cost a defaults parse per call.
        readonly Dictionary<UELib.Core.UClass, UEObject> _parentDefaults = new(UERefComparer<UELib.Core.UClass>.Instance);

        internal UEObject ParentClassDefaultsForTest(UEObject fx) => ParentClassDefaults(fx);

        UEObject ParentClassDefaults(UEObject fx)
        {
            UELib.Core.UClass cls = fx.Class
                ?? throw new NotSupportedException("ParticleFX ParentBlend: the emitter has no class");
            if (_parentDefaults.TryGetValue(cls, out UEObject cached)) return cached;
            UELib.Core.UClass super = HP1.Core.UEClassRegistry.Shared.CanonicalSuper(cls)
                ?? throw new NotSupportedException(
                    $"ParticleFX ParentBlend ({cls.Name}): the class has no parent, while shipped "
                    + "dereferences SuperField unconditionally");
            var def = new UEObject { Class = super, Interp = fx.Interp };
            def.LoadDefaults();
            _parentDefaults[cls] = def;
            return def;
        }

        // Gesture segment selection for DIST_Pattern — the same expression in two
        // places of the shipped body: in the particle count (0x103c22de, with the literal
        // 0.5 in place of fraction) and per particle (0x103c2a4c).
        //
        // Truncation is `ftol`, i.e. TOWARD ZERO, like a C# int cast. Shipped has an
        // upper bound (min with N−2) but NO LOWER one: with negative
        // progress the body indexes memory before the array. The port stops
        // loudly — silently returning point 0 would replace the original's
        // undefined behaviour with something plausible.
        static int PatternSegment(float fraction, float periodBase, float periodRand,
                                  int numPoints, out float frac)
        {
            float x = (fraction * periodRand + periodBase) * (numPoints - 1);
            int idx = (int)x;
            if (idx > numPoints - 2) idx = numPoints - 2;
            if (idx < 0)
                throw new NotSupportedException(
                    $"ParticleFX DIST_Pattern: gesture segment {idx} < 0 (Period progress "
                    + $"{periodBase}+{fraction}·{periodRand}, points {numPoints}). Shipped sets no lower "
                    + "bound and reads memory before the array — the port does not substitute this");
            frac = x - idx;
            return idx;
        }

        // AParticleFX::UpdateParticles (77407) + UParticle::Update (93564) —
        // integration per the bridge's decompile port; the chaos kick's THREE
        // sequential appFrand components (93564+) STOP loudly until verified
        // against the shipped body in context.
        // The stack slot EmitParticles reads as the first particle's +0x78/+0x7c (ebp-0x104/-0x100, the same address in
        // UpdateParticles' frame). The only callee of UpdateParticles that writes it deterministically is UParticle::Update
        // in its bSystemRelative branch: rep movsd of `UnitCoords *= Rotation` to its ebp-0xac (0x103bffa7..0x103bffb2;
        // its ebp is the caller's ebp-0x74), so the slot pair = YAxis.Y, YAxis.Z. appExp (Core 0x10126060, called per
        // substep when Damping > 0, 0x103c2005) is register-only (fldl2e .. fscale, ret): it writes nothing there; no
        // other callee changed the slot in the live probes (hp1slot3 call sites, hp1slot4 CHG records).
        //
        // What the slot holds at AParticleFX::Update entry depends on the CALLER PATH (live original, D-73 probe
        // hp1slot2: dead-stack snapshot at Update entry + ring of BoundVisible calls):
        //   * GetRenderBoundingBox path (FDynamicSprite::Setup -> AParticleFX::GetRenderBoundingBox 0x103c116c ->
        //     Update(0), emitter shifted): the slot lies in the frame Core FCoords::Inverse used under the PREVIOUS
        //     Setup's URender::BoundVisible (Render 0x10b213ad, esp = slot + 564): (Inverse(box).YAxis.Y, .Z) of the box
        //     that Setup passed — game code, 33/33 first particles bit-exact (Lev2_Fire2 SnailTrailFX, Lev_Tut2
        //     BroomTrail_02). Modelled: _setupResidue, written at every Setup BoundVisible call.
        //   * render path (DrawParticleSystem -> Update(0), live hp1slot4, audit/captures_2026-11-01_r4): the slot is
        //     DrawParticleSystem esp - 0x1c0, the same absolute address for every render-path call, and holds the last
        //     value written there. Game code writes it in EmitParticles (every born particle's ChaosTimer/SpinPos after its
        //     catch-up, 0x103c34e5 / AddParticle 0x103c3538; a call with no born particle writes nothing) and in
        //     UpdateParticles' bSystemRelative branch (below) — so a call chains from the previous render-path call,
        //     across emitters and frames (Lev_Tut3 186, Lev_Tut1 243 first particles bit-exact). Between calls the render
        //     device path (D3DDrv/wined3d under DrawDecals / DrawComplexSurface) may overwrite it with a wined3d return
        //     address or heap pointers: platform code, NOT emulated (author decision 2026-11-01 "always chain", D-73).
        //     appExp (Core 0x10126060, Damping > 0) is register-only; its return address lands at UpdateParticles'
        //     ebp-0x60, not at the slot. Session start: 0, 0 (no game write yet; the original holds a loader/platform
        //     residue there — D-73).
        enum SlotKind { Chain, SetupBoundVisible, SysRel, SessionStart }
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
        internal bool GrbbPath { set => _grbbPath = value; }
        bool _setupResidueKnown; float _setupResY, _setupResZ;
        internal (bool Known, float Y, float Z) SetupResidue => (_setupResidueKnown, _setupResY, _setupResZ);
        // Diagnostics (D-73): first particles born from the render-path chain / the Setup residue / the bSystemRelative
        // residue; GetRenderBoundingBox-path Updates before any Setup BoundVisible of the session (session-start 0, 0).
        public int ChainParticleSlot, SetupResidueParticleSlot, SysRelParticleSlot, NoSetupResidueYet;

        // Third-person weapon ObjectId -> the pawn whose DrawActorSprite draws it (this tick). The weapon's DrawMesh gets the
        // PAWN's FDynamicSprite (DrawActorSprite 0x10b3306e: `mov edx, [ebp+0xc]` — its own sprite argument — pushed for
        // the weapon call 0x10b33085), so SetupForActor reads that sprite's +0xbc / +0xb8 (DrawMesh 0x10b0f3f7..0x10b0f41b).
        public readonly Dictionary<int, int> WeaponPawns = new();

        // URender::BoundVisible of FDynamicSprite::Setup: Core FCoords::Inverse(box) runs at the depth of the emitter
        // slot (see above) and leaves its result's YAxis.Y/Z there.
        internal void SetupBoundVisibleResidue(in BrushCoords box)
        {
            BrushCoords inv = UERenderVisibility.Inverse(box);
            _setupResidueKnown = true; _setupResY = inv.Y.y; _setupResZ = inv.Y.z;
        }

        // Damping factor of a substep (Engine 0x103c1ff4..0x103c200e, render window PC24): fld dword step; fmul dword
        // Damping (rounded to float); fchs; fstp qword; call appExp; fst qword; fstp dword.
        internal static float DampFactor(float step, float damping) => UEX87Transcendental.AppExp(-(step * damping), pc24: true).ToFloat();

        void UpdateFxParticles(UEObject fx, FxState s, float dt)
        {
            (_slotKind, _slotChaos, _slotSpin) = EntrySlot();
            if (_slotKind == SlotKind.SessionStart) NoSetupResidueYet++;
            float lifeCap = RangeBase(fx, "Lifetime") + RangeRandPos(fx, "Lifetime");
            if (lifeCap > 0f && dt > lifeCap) { s.Particles.Clear(); return; }
            for (int i = s.Particles.Count - 1; i >= 0; i--)
                if (s.Particles[i].Lifetime > 0f && s.Particles[i].Lifetime <= dt + s.Particles[i].Age)
                    s.Particles.RemoveAt(i);
            if (s.Particles.Count == 0) return;
            FxStepCtx ctx = BuildStepCtx(fx);
            // Engine.dll .rdata 0x10478410 = 0x3d888a48 = 0.06667f (fcomp at 0x103c400c) — NOT 1/15 (0x3d888889).
            const float SubstepMax = 0.06667f;
            for (float rem = dt; rem > 0f; )
            {
                float step = (rem <= SubstepMax || !ctx.NeedSub) ? rem : SubstepMax;
                float dampF = ctx.Damping <= 0f ? 1f : DampFactor(step, ctx.Damping);
                // The list iterator visits the FIRST particle twice (Engine UParticleList First 0x103c7330: cur =
                // sentinel.next, the advance flag +0x80 left as it is; Next 0x103c7360: advances only when the flag is
                // set, then sets it). Every iteration of the list runs to its end, which clears the flag (0x103c7383),
                // so each First() starts with it clear: particle 0 is updated twice per substep (live batch 9: its Age
                // grows by 2·dt, the others' by dt).
                for (int v = -1; v < s.Particles.Count; v++)
                {
                    int i = v < 0 ? 0 : v;
                    var p = s.Particles[i];
                    if (p.Lifetime > 0f && p.Lifetime <= step + p.Age) continue;
                    StepParticle(p, step, dampF, ctx, fx);
                    // The bSystemRelative branch ran (DripTimer 0 after the drip hold, 93717).
                    if (ctx.SysRel && p.DripTimer == 0f && _slotKind != SlotKind.SysRel)
                    {
                        (_slotChaos, _slotSpin) = UnitCoordsMulRotYAxisYZPc24(UEValue.AsRotator(fx.GetProp(PK.Rotation)));
                        _slotKind = SlotKind.SysRel;
                        if (!_grbbPath) { RenderSlotChaos = _slotChaos; RenderSlotSpin = _slotSpin; }
                    }
                }
                rem -= step;
            }
        }

        // The slot pair (ChaosTimer, SpinPos) as AParticleFX::Update finds it at entry, per caller path (see above).
        (SlotKind Kind, float Chaos, float Spin) EntrySlot()
            => _grbbPath && _setupResidueKnown ? (SlotKind.SetupBoundVisible, _setupResY, _setupResZ)
             : _grbbPath ? (SlotKind.SessionStart, 0f, 0f)
             : (SlotKind.Chain, RenderSlotChaos, RenderSlotSpin);
        // Validation seam (engine judge): the entry slot values and whether this is the GetRenderBoundingBox path.
        internal (float Chaos, float Spin, bool Grbb) EntrySlotForTest() { var e = EntrySlot(); return (e.Chaos, e.Spin, _grbbPath); }

        // YAxis.Y, YAxis.Z of `GMath.UnitCoords *= R` (Core FCoords::operator*=(FRotator), yaw, pitch, roll elementary
        // coords, each axis = TransformVectorBy (x·C.X + y·C.Y) + z·C.Z — BrushCoords.MulRotator) at PC24, the render
        // window AParticleFX::Update runs in: every fmul/fadd rounds to float, in the body's order.
        internal static (float Y, float Z) UnitCoordsMulRotYAxisYZPc24(UERotator r)
        {
            float sy = BrushCoords.SinAngle(r.Yaw), cy = BrushCoords.CosAngle(r.Yaw);
            float sp = BrushCoords.SinAngle(r.Pitch), cp = BrushCoords.CosAngle(r.Pitch);
            float sr = BrushCoords.SinAngle(r.Roll), cr = BrushCoords.CosAngle(r.Roll);
            // Y = (0,1,0) after yaw: (sy, cy, 0).
            float y0 = (0f * cy + 1f * sy) + 0f * 0f, y1 = (0f * -sy + 1f * cy) + 0f * 0f, y2 = (0f * 0f + 1f * 0f) + 0f * 1f;
            // pitch: X=(cp,0,sp) Y=(0,1,0) Z=(-sp,0,cp).
            float p0 = (y0 * cp + y1 * 0f) + y2 * sp, p1 = (y0 * 0f + y1 * 1f) + y2 * 0f, p2 = (y0 * -sp + y1 * 0f) + y2 * cp;
            // roll: Y=(0,cr,-sr) Z=(0,sr,cr).
            return ((p0 * 0f + p1 * cr) + p2 * -sr, (p0 * 0f + p1 * sr) + p2 * cr);
        }

        // The per-step context of UParticle::Update — built once per
        // UpdateFxParticles/AdvanceNewborn call from the emitter's props.
        struct FxStepCtx
        {
            public UEVector Accel, Attraction, EmitterLoc;
            public float Damping;
            public bool NeedSub;
            public bool BUpdate, SysRel;
            public float AlphaGrow, AlphaDelay, ColorDelay, SizeGrow, SizeDelay;
            public float Chaos, ChaosDelay;
            public float Elasticity;
            public float WindModifier; public bool WindPerParticle; public UEVector Wind;
            public int Prim;
            public bool HasPalette;
        }

        FxStepCtx BuildStepCtx(UEObject fx)
        {
            var c = new FxStepCtx();
            // Acceleration (UpdateParticles head, 77441-77451): accel =
            // GravityModifier · Region.Zone->ZoneGravity + emitter Gravity —
            // the zone gravity comes from the actor's CURRENT zone, falling
            // back to Level when the Region is unset (review #15 P1: a
            // missing Region must NOT read as zero gravity — shipped asserts
            // when BOTH are null, so an actor with neither is loud here too).
            UEObject zone = (fx.GetProp(PK.Region) as UEObject)?.GetProp(PK.Zone) as UEObject
                ?? fx.GetProp(PK.Level) as UEObject
                ?? throw new InvalidOperationException(
                    $"ParticleFX {fx.GetProp(PK.Name)}: no Region.Zone and no Level — shipped UpdateParticles asserts here (77448)");
            UEVector zoneG = UEValue.AsVector3(zone.GetProp(PK.ZoneGravity));
            float gravityMod = PropF(fx, "GravityModifier");
            UEVector gravity = UEValue.AsVector3(fx.GetProp(PK.Gravity));
            c.Accel = new UEVector(zoneG.x * gravityMod + gravity.x,
                                   zoneG.y * gravityMod + gravity.y,
                                   zoneG.z * gravityMod + gravity.z);
            c.Attraction = UEValue.AsVector3(fx.GetProp(PK.Attraction));
            c.Damping = PropF(fx, "Damping");
            c.EmitterLoc = UEValue.AsVector3(fx.GetProp(PK.Location));
            c.NeedSub = c.Attraction.x != 0f || c.Attraction.y != 0f || c.Attraction.z != 0f;
            c.BUpdate = UEValue.ToBool(fx.GetProp(PK.bUpdate));
            c.SysRel = UEValue.ToBool(fx.GetProp(PK.bSystemRelative));
            c.AlphaGrow = PropF(fx, "AlphaGrowPeriod");
            c.AlphaDelay = PropF(fx, "AlphaDelay");
            c.ColorDelay = PropF(fx, "ColorDelay");
            c.SizeGrow = PropF(fx, "SizeGrowPeriod");
            c.SizeDelay = PropF(fx, "SizeDelay");
            c.Chaos = PropF(fx, "Chaos");
            c.ChaosDelay = PropF(fx, "ChaosDelay");
            c.Prim = UEValue.ToInt(fx.GetProp(PK.RenderPrimitive));
            c.HasPalette = fx.GetProp(PK.ColorPalette) is not null;
            // WIND (Engine.dll.c:23372 / 77473; call arguments taken from disasm
            // 0x103c1f02-0x103c1f0e): when `Damping * WindModifier > 0` the emitter
            // takes GetTotalWind at ITS Location once per update; with
            // bWindPerParticle the wind is taken at each particle's position (93681).
            c.WindModifier = PropF(fx, "WindModifier");
            c.WindPerParticle = UEValue.ToBool(fx.GetProp(PK.bWindPerParticle));
            c.Wind = default;
            if (c.Damping * c.WindModifier > 0f || c.WindPerParticle)
            {
                if (TotalWind is null)
                    throw new NotSupportedException(
                        $"ParticleFX wind reached ({fx.Class?.Name}) — wind source (UELevelRuntime.TotalWind) not bound to the producer");
                if (c.Damping * c.WindModifier > 0f)
                {
                    UEVector w = TotalWind(c.EmitterLoc);
                    c.Wind = new UEVector(c.WindModifier * w.x, c.WindModifier * w.y, c.WindModifier * w.z);
                }
            }
            // Elasticity (+0x320 per prop_offsets): shipped checks `0.0 < Elasticity`
            // on every particle step (93921) — read into the context once.
            c.Elasticity = PropF(fx, "Elasticity");
            if (c.Elasticity > 0f && (ParticleFastLineCheckClear is null || ParticleSingleLineCheck is null))
                throw new NotSupportedException(
                    $"ParticleFX Elasticity reached ({fx.Class?.Name}) — level queries (FastLineCheck/SingleLineCheck) not bound to the producer");
            // Substep 0.06667 — Attraction != 0 OR 0.0001 < Elasticity (Engine.dll.c:77483-77484 and
            // 23654-23655, `this + 800` = +0x320).
            c.NeedSub |= c.Elasticity > 0.0001f;
            return c;
        }

        // One UParticle::Update step (93564-93978) — the shipped body minus
        // the DOCUMENTED STOP boundaries (Elasticity bounce, per-particle
        // wind, ColorPalette ramp — loud in BuildStepCtx/emission, not
        // silently skipped). Shared by the update pass and the newborn
        // back-integration (23644).
        // alphaColor = false — for the newborn catch-up (improvement D-54, AdvanceNewborn): it computes the alpha
        // and colour ramps itself in substeps; the size ramp stays shipped (size feeds the particle box -> visibility).
        void StepParticle(Particle p, float step, float dampF, in FxStepCtx c, UEObject fx, bool alphaColor = true)
        {
            ParticleStepTap?.Invoke(fx, p, step, dampF, c.Accel, c.Wind, true);
            StepParticleBody(p, step, dampF, c, fx, alphaColor);
            ParticleStepTap?.Invoke(fx, p, step, dampF, c.Accel, c.Wind, false);
        }

        // SwARc Engine measurement tap (engine_judge.py pfx steps): one UParticle::Update call (emitter, particle, dt,
        // damping factor, Accel = UpdateParticles' param_1, Wind = param_3, enter); null in production.
        public static Action<UEObject, Particle, float, float, UEVector, UEVector, bool> ParticleStepTap;

        void StepParticleBody(Particle p, float step, float dampF, in FxStepCtx c, UEObject fx, bool alphaColor)
        {
            float pdt = step;
            // Drip hold (93588): the particle hangs while DripTimer runs; on
            // release the step remainder applies and the size deltas zero.
            if (p.DripTimer > 0f)
            {
                p.DripTimer -= pdt;
                if (p.DripTimer <= 0f)
                {
                    pdt = -p.DripTimer;
                    p.DripTimer = 0f;
                    p.WidthDelta = 0f;
                    p.LengthDelta = 0f;
                }
            }
            p.OldPos = p.Pos;
            if (p.DripTimer == 0f)
            {
                p.SpinPos += pdt * p.SpinRate;
                if (dampF >= 1f)
                {
                    // Undamped (93605): pos += (v + a·dt/2)·dt; v += a·dt.
                    p.Pos = new UEVector(
                        p.Pos.x + (p.Vel.x + c.Accel.x * (pdt * 0.5f)) * pdt,
                        p.Pos.y + (p.Vel.y + c.Accel.y * (pdt * 0.5f)) * pdt,
                        p.Pos.z + (p.Vel.z + c.Accel.z * (pdt * 0.5f)) * pdt);
                    p.Vel = new UEVector(p.Vel.x + c.Accel.x * pdt,
                                         p.Vel.y + c.Accel.y * pdt,
                                         p.Vel.z + c.Accel.z * pdt);
                }
                else
                {
                    // Damped analytic (93622): terminal = a/D + wind;
                    // pos += terminal·dt + (terminal−v)·(dampF−1)/D;
                    // v = terminal + (v−terminal)·dampF.
                    // terminal = Accel/Damping + wind (93676-93693): the emitter's
                    // common wind or, with bWindPerParticle, the wind at the particle position.
                    float invD = 1f / c.Damping;
                    UEVector wind = c.Wind;
                    if (c.WindPerParticle)
                    {
                        UEVector pw = TotalWind(p.Pos);
                        wind = new UEVector(c.WindModifier * pw.x, c.WindModifier * pw.y, c.WindModifier * pw.z);
                    }
                    UEVector terminal = new UEVector(invD * c.Accel.x + wind.x, invD * c.Accel.y + wind.y, invD * c.Accel.z + wind.z);
                    float k = (dampF - 1f) / c.Damping;
                    // Order (0x103bfdd1..0x103bfe0f, render window PC24 = per-op float): (t·dt + (t−v)·k) + pos.
                    p.Pos = new UEVector(
                        (terminal.x * pdt + (terminal.x - p.Vel.x) * k) + p.Pos.x,
                        (terminal.y * pdt + (terminal.y - p.Vel.y) * k) + p.Pos.y,
                        (terminal.z * pdt + (terminal.z - p.Vel.z) * k) + p.Pos.z);
                    p.Vel = new UEVector(
                        terminal.x + (p.Vel.x - terminal.x) * dampF,
                        terminal.y + (p.Vel.y - terminal.y) * dampF,
                        terminal.z + (p.Vel.z - terminal.z) * dampF);
                }
                // bSystemRelative (93717, +0x2f8 bit2): the particle FOLLOWS
                // the emitter's frame — subtract the emitter's movement since
                // the last update, then re-anchor its offset from the OLD
                // rotation's local frame into the NEW rotation's frame.
                if (c.SysRel)
                {
                    UEVector lastLoc = UEValue.AsVector3(fx.GetProp(PK.LastUpdateLocation));
                    var lastRot = UEValue.AsRotator(fx.GetProp(PK.LastUpdateRotation));
                    var curRot = UEValue.AsRotator(fx.GetProp(PK.Rotation));
                    p.Pos = new UEVector(p.Pos.x - (lastLoc.x - c.EmitterLoc.x),
                                         p.Pos.y - (lastLoc.y - c.EmitterLoc.y),
                                         p.Pos.z - (lastLoc.z - c.EmitterLoc.z));
                    UEVector rel = new UEVector(p.Pos.x - c.EmitterLoc.x,
                                                p.Pos.y - c.EmitterLoc.y,
                                                p.Pos.z - c.EmitterLoc.z);
                    var (oX, oY, oZ) = UEValue.GetAxes(lastRot);
                    float lx = rel.x * oX.x + rel.y * oX.y + rel.z * oX.z;
                    float ly = rel.x * oY.x + rel.y * oY.y + rel.z * oY.z;
                    float lz = rel.x * oZ.x + rel.y * oZ.y + rel.z * oZ.z;
                    var (nX, nY, nZ) = UEValue.GetAxes(curRot);
                    p.Pos = new UEVector(
                        c.EmitterLoc.x + nX.x * lx + nY.x * ly + nZ.x * lz,
                        c.EmitterLoc.y + nX.y * lx + nY.y * ly + nZ.y * lz,
                        c.EmitterLoc.z + nX.z * lx + nY.z * ly + nZ.z * lz);
                }
                // Attraction (93780): v += (emitter − p) ⊙ A · dt; negative
                // components repel.
                if (c.NeedSub)
                {
                    // X multiplies dt FIRST (0x103c01d3..0x103c01ee: d.x·dt·A.x), Y/Z multiply A first (0x103c01f1..).
                    p.Vel = new UEVector(
                        p.Vel.x + (c.EmitterLoc.x - p.Pos.x) * pdt * c.Attraction.x,
                        p.Vel.y + (c.EmitterLoc.y - p.Pos.y) * c.Attraction.y * pdt,
                        p.Vel.z + (c.EmitterLoc.z - p.Pos.z) * c.Attraction.z * pdt);
                }
                // Chaos (93795): the timer runs down every step; with Chaos
                // set and the timer spent, kick v by a normalized random
                // vector (THREE appFrand, components 2f−1, normalized when
                // len² ≥ 1e-8) times Chaos; timer reloads to ChaosDelay.
                if (p.ChaosTimer != 0f)
                    p.ChaosTimer = p.ChaosTimer <= pdt ? 0f : p.ChaosTimer - pdt;
                if (c.Chaos != 0f && p.ChaosTimer <= 0f)
                {
                    float r1 = Frand() * 2f - 1f;
                    float r2 = Frand() * 2f - 1f;
                    float r3 = Frand() * 2f - 1f;
                    // Sum order from the disasm (0x103c02a7..0x103c02b9): (r3² + r2²) + r1² — the third draw first.
                    float len2 = (r3 * r3 + r2 * r2) + r1 * r1;
                    if (len2 >= 1e-08f)
                    {
                        float inv = 1f / MathF.Sqrt(len2);
                        r1 *= inv; r2 *= inv; r3 *= inv;
                    }
                    // (93822, review #15 P0): the kick FVector is built as
                    // (third, second, first) — X = r3, Y = r2, Z = r1.
                    p.Vel = new UEVector(p.Vel.x + r3 * c.Chaos,
                                         p.Vel.y + r2 * c.Chaos,
                                         p.Vel.z + r1 * c.Chaos);
                    p.ChaosTimer = c.ChaosDelay;
                }
            }
            Ramps(p, pdt, c, p.Age, p.DripTimer, alphaColor, size: true);
            // World bounce (LAB_103c064e, 93921-93975): with Elasticity > 0 and a
            // BLOCKED UModel::FastLineCheck(End=OldPos(+0), Start=Pos(+0xc))
            // comes ULevel::SingleLineCheck(Source=0, End=Pos, Start=OldPos,
            // TraceFlags=6, Extent=0). A hit on LevelInfo reflects the velocity
            // about the normal, v = (v − 2(v·N)N)·Elasticity, and the particle moves
            // to HitLocation; otherwise (a mover or nothing) the velocity is zeroed and
            // the particle returns to OldPos.
            if (c.Elasticity > 0f && !ParticleFastLineCheckClear(p.OldPos, p.Pos))
            {
                ParticleLineHit hit = ParticleSingleLineCheck(p.OldPos, p.Pos);
                if (hit.World)
                {
                    UEVector n = hit.Normal;
                    float vn = p.Vel.x * n.x + p.Vel.y * n.y + p.Vel.z * n.z;
                    // FVector((N.x+N.x)·vn, (N.y+N.y)·vn, (N.z+N.z)·vn) — as in the decompile.
                    UEVector twice = new UEVector((n.x + n.x) * vn, (n.y + n.y) * vn, (n.z + n.z) * vn);
                    p.Vel = new UEVector((p.Vel.x - twice.x) * c.Elasticity,
                                         (p.Vel.y - twice.y) * c.Elasticity,
                                         (p.Vel.z - twice.z) * c.Elasticity);
                    p.Pos = hit.Location;
                }
                else
                {
                    p.Vel = new UEVector(0f, 0f, 0f);
                    p.Pos = p.OldPos;
                }
            }
            // The shipped body's very last operation (93976).
            p.Age += pdt;
        }

        // Alpha/colour/size ramps of UParticle::Update (93833-93920). age/drip — the particle's Age and DripTimer at
        // this step: in shipped the particle's own fields, in the newborn catch-up (improvement D-54) — substep counts.
        void Ramps(Particle p, float pdt, in FxStepCtx c, float age, float drip, bool alphaColor, bool size)
        {
            // Ramps — ALL gated on bUpdate (93833: bit0 of +0x2f8, ctor
            // default TRUE; without it alpha/color/size stay frozen). They
            // compare against the OLD Age — the shipped body advances Age as
            // its very LAST operation (93976, review #15 P0): a newborn's
            // first step with AlphaDelay=0 does NOT pass the strict
            // Age > AlphaDelay yet.
            if (!c.BUpdate) return;
            if (alphaColor)
            {
                // Alpha (93834): grow toward AlphaMax over
                // AlphaGrowPeriod·Lifetime, else fade by AlphaDelta after
                // AlphaDelay; snap to 0 below 0.001.
                if (age < c.AlphaGrow * p.Lifetime)
                {
                    float a = p.Alpha + pdt * p.AlphaMax / (c.AlphaGrow * p.Lifetime);
                    p.Alpha = a < p.AlphaMax ? a : p.AlphaMax;
                }
                else if (age > c.AlphaDelay)
                    p.Alpha += p.AlphaDelta * pdt;
                if (p.Alpha < 0.001f) p.Alpha = 0f;
                // Color (93853): the linear delta applies only after
                // ColorDelay and only without a palette (palette STOPs at
                // emission).
                if (!c.HasPalette
                    && (p.ColorDelta.x != 0f || p.ColorDelta.y != 0f || p.ColorDelta.z != 0f)
                    && age > c.ColorDelay)
                    p.Color = new UEVector(p.Color.x + p.ColorDelta.x * pdt,
                                           p.Color.y + p.ColorDelta.y * pdt,
                                           p.Color.z + p.ColorDelta.z * pdt);
            }
            // Size (93899): PPRIM_Liquid(2) only sizes while dripping;
            // grow toward WidthMax/LengthMax over SizeGrowPeriod·Lifetime,
            // else the EndScale slope after SizeDelay.
            if (size && (c.Prim != 2 || drip > 0f))
            {
                if (age < c.SizeGrow * p.Lifetime)
                {
                    float scale = pdt / (c.SizeGrow * p.Lifetime);
                    float w = p.Width + scale * p.WidthMax;
                    float l = p.Length + scale * p.LengthMax;
                    p.Width = w < p.WidthMax ? w : p.WidthMax;
                    p.Length = l < p.LengthMax ? l : p.LengthMax;
                }
                else if (age > c.SizeDelay)
                {
                    p.Width += p.WidthDelta * pdt;
                    p.Length += p.LengthDelta * pdt;
                }
            }
        }

        // The shipped emit-tail catch-up loop (23644): same physics context as
        // the update pass, 0.06667-substep, per-step damping exponent.
        //
        // AUTHOR'S IMPROVEMENT, not original (D-54). Without Attraction/Elasticity shipped catches a newborn up
        // in ONE step, and the ramps are evaluated against the OLD Age = 0 (93834): particles seeded over a long
        // window (emitter came back into the frame, priming) are drawn as newborns — a torch flares. Here the
        // shipped step runs as-is (position, velocity, chaos and appFrand use, bSystemRelative, DripTimer,
        // Age, size — the Lifetime of later particles depends on them, removal, particle box -> emitter
        // visibility and self-destruction of a spent emitter), while the ALPHA AND COLOUR ramps run in 0.06667
        // substeps from the initial state; they affect neither the box nor the game. Substepping the whole
        // UParticle::Update instead shifts FireSeedSmoke self-destruction on Lev2_fire1 (chaos on every substep,
        // box from size and float-ulp positions). Only under Enhanced (EnhancedVisuals); Original runs the shipped
        // single step with the ramps against Age = 0.
        void AdvanceNewborn(UEObject fx, Particle p, float dt)
        {
            FxStepCtx ctx = BuildStepCtx(fx);
            bool improve = EnhancedVisuals && !ctx.NeedSub && dt > 0.06667f;
            float age0 = p.Age, drip0 = p.DripTimer;
            for (float rem = dt; rem > 0f; )
            {
                float step = (rem <= 0.06667f || !ctx.NeedSub) ? rem : 0.06667f;
                float dampF = ctx.Damping <= 0f ? 1f : DampFactor(step, ctx.Damping);
                StepParticle(p, step, dampF, ctx, fx, alphaColor: !improve);
                rem -= step;
            }
            if (!improve) return;
            for (float rem = dt, age = age0, drip = drip0; rem > 0f; )
            {
                float step = rem <= 0.06667f ? rem : 0.06667f;
                float pdt = step;
                if (drip > 0f)
                {
                    drip -= pdt;
                    if (drip <= 0f) { pdt = -drip; drip = 0f; }
                }
                Ramps(p, pdt, ctx, age, drip, alphaColor: true, size: false);
                age += pdt;
                rem -= step;
            }
        }

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
        // SetupDynamics order diagnostics (D-58): the actor whose shadow is about to get Update — before the call.
        public Action<UEObject> ShadowUpdateEntered;

        // SwARc Engine measurement tap (runtime_poc/HeadlessEngineJudgeDump, tools/swarc_oracle/engine_judge.py): observes the
        // call (producer, emitter, enter, result at exit); null in production.
        public static Action<UEWorldFrameProducer, UEObject, bool, bool> ParticleUpdateTap;

        public bool RenderStepFx(UEObject fx)
        {
            ParticleUpdateTap?.Invoke(this, fx, true, false);
            bool ok = RenderStepFxBody(fx);
            ParticleUpdateTap?.Invoke(this, fx, false, ok);
            return ok;
        }

        bool RenderStepFxBody(UEObject fx)
        {
            RenderStepFxEntered?.Invoke(fx);
            // Update head (0x103c3e05..0x103c3e12): a null particle list returns false before any write. A destroyed emitter
            // has it freed (AParticleFX::Destroy, +0x378 = 0 at 0x103c1cdd) — the case of an emitter destroyed earlier in
            // the same frame tree and drawn again by a child frame (DrawActorSprite has no bDeleteMe test).
            if (fx.Destroyed) return false;
            FxState s = FxStateOf(fx);
            float age = PropF(fx, "Age");
            float elapsed = PropF(fx, "ElapsedTime");
            UEVector loc = UEValue.AsVector3(fx.GetProp(PK.Location));
            // First-update prelude (106729-106740, review #14 P0: the earlier
            // "ctor-zeroed quirk" reading came from starting the decompile
            // read BELOW this block): ElapsedTime==0 seeds Location into
            // LastUpdateLocation AND LastEmitLocation, Rotation into
            // LastUpdateRotation — so the first emit back-dates along a ZERO
            // delta and the prime burst still passes the steady clamp via the
            // (target−alive)/ppsMin==lifeMax arithmetic, not a failed gate.
            if (elapsed == 0f)
            {
                fx.Props["LastUpdateLocation"] = loc;
                fx.Props["LastEmitLocation"] = loc;
                fx.Props["LastUpdateRotation"] = UEValue.AsRotator(fx.GetProp(PK.Rotation));
            }
            // Update(0) head (106743): dt = Age − ElapsedTime, and
            // ElapsedTime += dt ALWAYS — no clamp.
            float dt = age - elapsed;
            fx.Props["ElapsedTime"] = elapsed + dt;
            bool shellOnly = UEValue.ToBool(fx.GetProp(PK.bShellOnly));
            if (UEValue.ToBool(fx.GetProp(PK.bSteadyState)) && dt > 0.06667f
                && UEValue.AsVector3(fx.GetProp(PK.LastUpdateLocation)) == loc)
            {
                float lifeMax = RangeBase(fx, "Lifetime") + RangeRandPos(fx, "Lifetime");
                float target = (RangeBase(fx, "ParticlesPerSec") + RangeRandPos(fx, "ParticlesPerSec")) * lifeMax;
                int aliveCap = UEValue.ToInt(fx.GetProp(PK.ParticlesAlive));
                if (aliveCap > 0 && aliveCap < target) target = aliveCap;
                float ppsRand = RangeRandRaw(fx, "ParticlesPerSec");
                float ppsMin = RangeBase(fx, "ParticlesPerSec") + (ppsRand < 0f ? ppsRand : 0f);
                // Unguarded division (0x103c4003..0x103c401b): ppsMin 0 gives ±inf (0/0 NaN); `fcom 0.06667;
                // test ah,1` replaces anything below OR unordered with 0.06667, +inf leaves dt uncapped.
                float catchUp = (target - s.Particles.Count) / ppsMin;
                if (!(catchUp >= 0.06667f)) catchUp = 0.06667f;
                if (lifeMax < dt) dt = lifeMax;
                if (catchUp < dt) dt = catchUp;
            }
            // (106782): dt>0 && !bShellOnly → simulate dt; dt<=0 &&
            // !bShellOnly → return WITHOUT simulating (particles freeze on
            // pause frames); ONLY bShellOnly forces the 0.1 shell quantum.
            // LOD is NOT touched here: the ctor default 1.0 (or a loaded
            // override) is what shipped emission reads — nothing in
            // Engine/Render writes +0x300 at runtime (searched, review #13
            // item 2; ParticleFxInitExecution ports the ctor default).
            if (shellOnly) dt = 0.1f;
            else if (dt <= 0f) return true;
            UpdateFxParticles(fx, s, dt);
            int emitted = UEValue.ToInt(fx.GetProp(PK.ParticlesEmitted));
            int particlesMax = UEValue.ToInt(fx.GetProp(PK.ParticlesMax));
            if (particlesMax == 0 || emitted < particlesMax)
            {
                // bEmit gate (+0x3c8 bit1): absent -> class default (true for
                // the shipped ParticleFX defaults; scripts toggle via
                // EnableEmission).
                bool emit = fx.GetProp(PK.bEmit) is { } be ? UEValue.ToBool(be) : true;
                if (emit) EmitParticles(fx, s, dt);
                else fx.Props["LastEmitLocation"] = loc;
            }
            else if (s.Particles.Count == 0)
            {
                DestroyActor?.Invoke(fx);
                return false;
            }
            UpdateListBounds(s);      // list +0x94 (106809)
            fx.Props["LastUpdateLocation"] = loc;
            fx.Props["LastUpdateRotation"] = UEValue.AsRotator(fx.GetProp(PK.Rotation));
            return true;
        }

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

        // SwARc measurement tap (runtime_poc/HeadlessEngineJudgeDump): the billboard pass of one draw call, observed at
        // entry and exit (ParticleDraws[^1] holds its result); null in production.
        public static Action<UEWorldFrameProducer, UEObject, UESceneNode, bool> ParticleDrawTap;

        void ParticleDrawerFade(UEObject fx, UESceneNode f, int view)
        {
            if (fx.GetProp(PK.Textures) is not System.Collections.IList tl || tl.Count == 0 || tl[0] is null) return;
            ParticleDrawTap?.Invoke(this, fx, f, true);
            FxState s = FxStateOf(fx);
            var kept = new List<(Particle, float)>();
            int prim = UEValue.ToInt(fx.GetProp(PK.RenderPrimitive));
            bool billboard = prim <= 3 && !UEValue.ToBool(fx.GetProp(PK.bShellOnly));
            float budget = ((f.FY * f.FX) * 1f) * 0.5f;
            bool uk = UkParticleWalk;
            int distribution = uk ? UEValue.ToInt(fx.GetProp(PK.Distribution)) : 0;
            float lod = uk ? PropF(fx, "LOD") : 0f;
            for (int v = s.Particles.Count > 0 ? -1 : 0; v < s.Particles.Count; v++)
            {
                Particle p = s.Particles[v < 0 ? 0 : v];
                if (uk && UkWalkCut(distribution, lod, p.Priority)) continue;
                if (!billboard || BillboardKeep(f, p, budget, fade: !uk)) kept.Add((p, p.Alpha));
            }
            ParticleDraws.Add((fx, view, kept));
            ParticleDrawTap?.Invoke(this, fx, f, false);
        }

        // UK11 Render.dll walk FUN_10b15000 (US FUN_10b15090), 0x10b1531c..0x10b15341, before the drawer is called: unless
        // Distribution (+0x334) == 1, `fld LOD(+0x300); fmul 9.0 (0x10b388d0); fild Priority(+0x88); fcompp; test ah,
        // 0x41; je next` — the particle is not drawn when Priority > LOD·9 (ordered; NaN draws). US 1.53 has no such cut.
        // Judged against those Fix bytes under unicorn (engine_judge.py, ENGJUDGE_BUILD=uk11, kind pfxlod).
        internal static bool UkWalkCut(int distribution, float lod, int priority) => distribution != 1 && (float)priority > lod * 9f;

        // fade: the US 1.53 density fade (below). The UK11 drawer FUN_10b18390 ends at the outcode return 0x10b18a7f: the
        // same culling, no budget, no Alpha write (its drawer object has no budget field: DrawParticleSystem 0x10b157c0).
        internal static bool BillboardKeep(UESceneNode f, Particle p, float budget, bool fade = true)
        {
            BrushCoords c = f.Coords;
            float dx = p.Pos.x - c.Origin.x, dy = p.Pos.y - c.Origin.y, dz = p.Pos.z - c.Origin.z;
            float cx = (dx * c.X.x + dy * c.X.y) + dz * c.X.z;
            float cy = (dx * c.Y.x + dy * c.Y.y) + dz * c.Y.z;
            float cz = (dx * c.Z.x + dy * c.Z.y) + dz * c.Z.z;
            if (!(cz >= 1f)) return false;
            float sc = f.ProjZ / cz;
            float hw = p.Width * 0.5f, hl = p.Length * 0.5f;
            float ex = hw, ey = hl;
            if (p.SpinRate < 0f || p.SpinRate > 0f)   // `fcomp 0.0; test ah, 0x40; jne` (0x10b18603..0x10b18614): equal or NaN unrotated
            {
                float t = p.SpinPos * 65536f * 0.15915494f;
                int a = t >= -2147483648f && t < 2147483648f ? (int)MathF.Round(t, MidpointRounding.ToEven) : int.MinValue;
                float co = BrushCoords.CosAngle(a), si = BrushCoords.SinAngle(a);
                if (co < 0f) co = -co;
                if (si < 0f) si = -si;
                ex = co * hw + si * hl;
                ey = si * hw + co * hl;
            }
            static bool Neg(float v) => BitConverter.SingleToInt32Bits(v) < 0;
            if (Neg((f.PrjYP * cz - cy) + ey) || Neg((f.PrjYM * cz + cy) + ey) || Neg((f.PrjXP * cz - cx) + ex) || Neg((f.PrjXM * cz + cx) + ex))
                return false;
            if (!fade) return true;
            float area = ((p.Length * p.Width) * sc) * sc;
            float x = ((float)(p.Priority + 1) * area) * 0.25f;
            if (x > budget)
            {
                float fa = ((budget + budget) - x) / budget;
                if (p.Alpha > fa) p.Alpha = fa;
                if (!(p.Alpha > 0f)) return false;
            }
            return true;
        }

        // The WHOLE frame is cached per EXPLICIT core tick (review #12 P1 —
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
            // frame. A frontend still driving a legacy canvas renderer (one that
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
            // (se_harness/bsp_diff/validate_render_visibility.py).
            // ── ROUTE READINESS — A GATE, NOT A LABEL ─────────────────────
            //
            // The blockers are a LIST, and the leg runs only when it is EMPTY.
            // Each item is a separate open dependency, and closing one of them
            // enables nothing by itself (a diagnostic label describing a violation
            // would not block it). BonePos (native 257) is registered in both frontends,
            // so the first condition is FALSE in production; it stays as a gate in case
            // the registration is removed (audit/shadow_route_redtest.sh, step [5]).
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
            // (se_harness/bsp_diff/validate_render_visibility.py). The same pass puts
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

        // ── The Core-owned HUD drive ────────────────────
        // Faithful order per shipped UGameEngine::Draw, once per core tick:
        // Console->PreRender, then UCanvas::Update (eventReset, then the frame
        // dims), then the view actor's PreRender — Engine.u
        // PlayerPawn.PreRender LAZILY SPAWNS myHUD — then the view actor's
        // PostRender drives myHUD.PostRender through the Canvas natives, then
        // Console->PostRender and Console.PostRender (ConsolePostRender).
        // TWO HALVES OF ONE UGameEngine::Draw (Engine.dll.c:117794): between
        // `Actor.PreRender(Canvas)` and `Actor.PostRender(Canvas)` shipped draws
        // the world — `Render->DrawWorld(Frame)`, and with it SetupDynamics (shadows), particles
        // and the weapon point; the event order and RNG use depend on this split.
        // Returns the frame size after the console border (for FSceneNode).
        (int X, int Y) HudPreRender(UEInterpreter interp, UEObject player)
        {
            _hud.Clear();
            if (interp is null || player is null)
                return (_viewportX, _viewportY);
            if (_canvas is null)
            {
                var canvasClass = HP1.Core.UEClassRegistry.Shared.Find("Canvas")
                    ?? throw new InvalidDataException("shipped Engine.Canvas class missing");
                _canvas = new UEObject { Class = canvasClass, Interp = interp };
                _canvas.LoadDefaults();
                _canvas.Props["Name"] = new UEName("HeadlessCanvas");
            }
            BindCanvasViewport();
            // FSceneNode frame: X/Y — viewport size (+0xa8/+0xac), XB/YB — origin
            // (+0xb0/+0xb4). The console may shrink it with a border (PreRender) and restore it
            // (PostRender); Canvas->Update takes the already shrunk frame. Before Update the canvas
            // holds last frame's values (Console->PreRender runs earlier).
            int frameX = _viewportX, frameY = _viewportY;
            ConsolePreRender(interp, ref frameX, ref frameY);
            // UCanvas::Update 0x10388f60: FIRST eventReset (0x10388fad), then
            // Frame, ClipX = (float)Frame.X, SizeX, ClipY, SizeY.
            var reset = interp.FindEventFunction(_canvas, "Reset");
            if (reset is not null) interp.Invoke(_canvas, reset, Array.Empty<object>());
            _masterFrame = new CanvasFrame { X = frameX, Y = frameY };
            _canvasFrames.Remove(_canvas);
            _canvasFrames.Add(_canvas, _masterFrame);
            _canvas.Props["ClipX"] = (float)frameX;
            _canvas.Props["SizeX"] = frameX;
            _canvas.Props["ClipY"] = (float)frameY;
            _canvas.Props["SizeY"] = frameY;
            var pre = interp.FindEventFunction(player, "PreRender");
            if (pre is not null) interp.Invoke(player, pre, new object[] { _canvas });
            return (frameX, frameY);
        }

        public int RenderOverlaysCount;

        // C9 probe: an actor whose render-relevant state the overlays/HUD PostRender changed after DrawWorld (shipped draws
        // the world before them; the port's frame snapshot is taken after them).
        public static bool OverlayProbe;
        public readonly List<string> OverlayChanged = new();
        Dictionary<int, (long fp, string what)> _overlayPre = new();
        static Dictionary<int, (long fp, string what)> OverlayFingerprints(IEnumerable<UEObject> actors)
        {
            var d = new Dictionary<int, (long, string)>();
            foreach (UEObject a in actors)
            {
                if (a is null) continue;
                var h = new System.HashCode();
                string[] keys = { "Location", "Rotation", "bHidden", "DrawScale", "DrawType", "Mesh", "Texture", "Skin", "Style",
                    "Opacity", "LightType", "LightBrightness", "LightHue", "LightSaturation", "LightRadius", "AnimSequence", "AnimFrame",
                    "ScaleGlow", "AmbientGlow", "bUnlit", "Fatness" };
                foreach (string k in keys) h.Add(a.GetProp(k)?.ToString());
                h.Add(a.Destroyed);
                d[a.ObjectId] = (h.ToHashCode(), a.Class?.Name + "#" + a.ObjectId);
            }
            return d;
        }

        // LOADING SCREEN — UGameEngine::LoadMap 0x1039cd1d..0x1039cd9f:
        // Viewport->Lock, Console.DrawLevelInfo(Viewport.Canvas, URL), Unlock(1).
        // Canvas->Update (and Reset with it) is NOT called here — the canvas remains from
        // the last frame; there is no world in the frame. The canvas is created only if it
        // did not exist yet (headless without HUD).
        public bool LevelInfo43 { get; internal set; }   // loading screen as a 4:3 picture centred on a wide screen (SetWideScreen)
        public int LevelInfoOffsetX;      // offset of the last loading screen (canvas), for the frontend bars
        public WorldFrame BuildLevelInfoFrame(UEInterpreter interp, int tick, string text)
        {
            _hud.Clear();
            UEObject c = Console;
            if (c is not null)
            {
                if (_canvas is null)
                {
                    var canvasClass = HP1.Core.UEClassRegistry.Shared.Find("Canvas")
                        ?? throw new InvalidDataException("shipped Engine.Canvas class missing");
                    _canvas = new UEObject { Class = canvasClass, Interp = interp };
                    _canvas.LoadDefaults();
                    _canvas.Props["Name"] = new UEName("HeadlessCanvas");
                    BindCanvasViewport();
                    _canvas.Props["SizeX"] = _viewportX; _canvas.Props["SizeY"] = _viewportY;
                    _canvas.Props["ClipX"] = (float)_viewportX; _canvas.Props["ClipY"] = (float)_viewportY;
                    _masterFrame = new CanvasFrame { X = _viewportX, Y = _viewportY };
                    _canvasFrames.Remove(_canvas);
                    _canvasFrames.Add(_canvas, _masterFrame);
                }
                // No Canvas->Update here: the vtbl+0xb0 call before DrawLevelInfo (0x1039cd28-0x1039cd2d) is on Viewport+0x5c,
                // the RenDev (UViewport::Lock asserts "RenDev" on +0x5c, Engine.dll.c:60666); the Canvas is +0x54
                // (0x1039cd3a, DrawLevelInfo's argument). The canvas keeps the state of the last Draw's PostRender.
                // FULL-BRIGHTNESS LOADING SCREEN (author decision 2026-11-07, NOT ORIGINAL; supersedes 2026-10-29 "as
                // shipped"): DrawLevelInfo draws the parchment with the canvas DrawColor it finds — whatever the last
                // Draw's PostRender set last (Canvas.Reset gives 127 at the start of a Draw; after a load from the menu
                // RenderUWindow set 255 before its windows drew, HPConsole.uc:731-733) — times D3DDrv stage 0 MODULATE 1x
                // (D3DDrv.dll.c:2656): at 127, half brightness. The shipped value per route is not measured. Core always
                // gives DrawLevelInfo the RenderUWindow colour 255; the script then sets the text colour itself
                // (HPConsole.uc:848-850).
                if (_canvas.GetProp(PK.DrawColor) is UEObject dc)
                    foreach (string k in new[] { "R", "G", "B" }) dc.Props[k] = (byte)255;
                // 4:3 REGION (author decision, not original): the loading screen, like the menu and pause, is a 4:3
                // picture centred on a wide screen (shipped has no region — FrameX is the whole viewport). The frontend
                // turns on LevelInfo43 and places bars by LevelInfoOffsetX. FrameX is always set: otherwise DrawLevelInfo
                // would take the width of the last console frame (the menu region) and draw the picture at the left edge.
                int frameX = LevelInfo43 && (long)_viewportX * 3 > (long)_viewportY * 4 ? _viewportY * 4 / 3 : _viewportX;
                LevelInfoOffsetX = (_viewportX - frameX) / 2;
                c.Props["FrameX"] = (float)frameX;
                c.Props["FrameY"] = (float)_viewportY;
                object sizeX = _canvas.GetProp(PK.SizeX), clipX = _canvas.GetProp(PK.ClipX);
                _canvas.Props["SizeX"] = frameX;
                _canvas.Props["ClipX"] = (float)frameX;
                var draw = interp.FindEventFunction(c, "DrawLevelInfo")
                    ?? throw new InvalidOperationException(
                        $"LoadMap: console {c.Class?.Name} has no DrawLevelInfo event (FindFunctionChecked)");
                interp.Invoke(c, draw, new object[] { _canvas, text ?? "" });
                _canvas.Props["SizeX"] = sizeX; _canvas.Props["ClipX"] = clipX;
                if (LevelInfoOffsetX > 0) ShiftHud(0, LevelInfoOffsetX);
            }
            HudCommand[] hud = _hud.ToArray();
            _hud.Clear();
            return new WorldFrame(tick, "levelinfo", Array.Empty<ActorSnapshot>(), default,
                Array.Empty<AudioCommand>(), default, hud, Array.Empty<ParticleInstance>(), -1, canvasScale: _canvasScale);
        }

        // The canvas frame the player's HUD saw (Actor.PostRender), and the canvas itself —
        // for the console shrink/restore fixture.
        public (int X, int Y) LastPlayerHudFrame;
        public UEObject CanvasForTest => _canvas;

        // UCanvas::Init (Engine 0x10388f40): `mov [ecx+0x78], eax` — Canvas.Viewport = the viewport of the
        // canvas owner. In the port the canvas is created lazily, the viewport belongs to the console.
        void BindCanvasViewport()
        {
            if (_canvas.GetProp("Viewport") is null && Console?.GetProp("Viewport") is UEObject vp)
                _canvas.Props["Viewport"] = vp;
        }

        void HudPostRender(UEInterpreter interp, UEObject player)
        {
            if (interp is null || player is null)
            {
                _hudCache = Array.Empty<HudCommand>();
                return;
            }
            if (_canvas is not null) { CanvasFrame hf = FrameOf(_canvas); LastPlayerHudFrame = (hf.X, hf.Y); }
            var post = interp.FindEventFunction(player, "PostRender");
            if (post is not null) interp.Invoke(player, post, new object[] { _canvas });
            ConsolePostRender(interp);
            _hudCache = _hud.ToArray();
        }

        // UConsole::PreRender (Engine.dll.c:91467-91537): console tick on the viewport
        // clock, then the border. ConsoleLines = ROUND(min(Y·ConsolePos, Y)) when
        // ConsolePos > 0; when BorderSize > 1: s = (BorderSize−1)·0.2,
        // BorderLines = max(ROUND(min(Y·s·0.25, Y)), 0), BorderPixels =
        // ROUND(min(X·s·0.25, X)) & ~3; the frame shrinks by 2·Border on both axes.
        void ConsolePreRender(UEInterpreter interp, ref int frameX, ref int frameY)
        {
            UEObject c = Console;
            if (c is null) return;
            var tick = interp.FindEventFunction(c, "Tick");
            if (tick is not null) { interp.Invoke(c, tick, new object[] { ViewportDelta }); ConsoleTickCount++; }
            c.Props["ConsoleLines"] = 0; c.Props["BorderLines"] = 0; c.Props["BorderPixels"] = 0;
            float pos = UEValue.AsFloat(c.GetProp(PK.ConsolePos));
            if (0f < pos)
            {
                float y = frameY, v = y * pos;
                if (v <= y) y = v;
                c.Props["ConsoleLines"] = (int)Math.Round(y, MidpointRounding.ToEven);
            }
            int border = UEValue.ToInt(c.GetProp(PK.BorderSize));
            if (1 < border)
            {
                float sc = (border - 1) * 0.2f;
                float y = frameY, vy = y * sc * 0.25f;
                if (vy <= y) y = vy;
                int lines = (int)Math.Round(y, MidpointRounding.ToEven);
                if (lines < 1) lines = 0;
                c.Props["BorderLines"] = lines;
                frameY -= lines * 2;
                float x = frameX, vx = x * sc * 0.25f;
                if (vx <= x) x = vx;
                int pixels = (int)Math.Round(x, MidpointRounding.ToEven) & ~3;
                c.Props["BorderPixels"] = pixels;
                frameX -= pixels * 2;
            }
        }

        // UConsole::PostRender (Engine.dll.c:32512-32528) + Console.eventPostRender
        // (UGameEngine::Draw after Actor.PostRender): the frame is restored, FrameX/Y =
        // its size, bDrewWorld = !bNoDrawWorld; then the script PostRender(Canvas).
        void ConsolePostRender(UEInterpreter interp)
        {
            UEObject c = Console;
            if (c is null) return;
            // Console region (presentation, not shipped): see SetConsoleRegion.
            int frameX = ConsoleRegionActive ? _consoleRegionX : _viewportX;
            // UConsole::PostRender (Engine.dll.c:32520-32525) restores the frame in
            // place from the PreRender copy — Canvas.Frame points at it.
            if (_masterFrame is not null) { _masterFrame.X = frameX; _masterFrame.Y = _viewportY; }
            c.Props["FrameX"] = (float)frameX;
            c.Props["FrameY"] = (float)_viewportY;
            c.Props["bDrewWorld"] = !UEValue.ToBool(c.GetProp(PK.bNoDrawWorld));
            int first = _hud.Count;
            // In the region the console canvas is region-wide too: UWindow takes the root
            // width from Canvas.ClipX (HPConsole.uc:768), not from the frame.
            object sizeX = _canvas?.GetProp(PK.SizeX), clipX = _canvas?.GetProp(PK.ClipX);
            if (ConsoleRegionActive && _canvas is not null)
            {
                _canvas.Props["SizeX"] = frameX;
                _canvas.Props["ClipX"] = (float)frameX;
            }
            var post = interp.FindEventFunction(c, "PostRender");
            if (post is not null) { interp.Invoke(c, post, new object[] { _canvas }); ConsolePostRenderCount++; }
            if (ConsoleRegionActive)
            {
                ShiftHud(first, ConsoleRegionOffsetX);
                if (_canvas is not null) { _canvas.Props["SizeX"] = sizeX; _canvas.Props["ClipX"] = clipX; }
            }
        }

        // ── CONSOLE REGION: 4:3 MENU ON A WIDE SCREEN (author decision, not original) ──
        //
        // NOT SHIPPED. The original supported only 4:3, and HP1 UWindow scales the
        // menu by canvas width (GUIScale = 1, the 640x480 book stretches to
        // Canvas.ClipX) — on a wide screen the bottom of the menu goes off-screen. Author
        // decision: the menu is 4:3, centred with black sides. The frontend sets the region
        // width (height·4/3) while the menu is open; the console then sees a frame of that
        // width (its scripts lay out UWindow themselves), and its commands are shifted
        // to the centre. A region not narrower than the screen, or 0 — shipped behaviour unchanged.
        // ── DIRECT FRAME PATH INSTEAD OF THE Render.dll PORT (author decision: render scheduling need not copy the original) ──
        //
        // The Render.dll port over the mirror (BeginFrame/DrawWorld) decided which
        // emitters are simulated and which meshes get a pose — at ~25 ms per frame.
        // By the "what the player sees" precision boundary, Core decides: the master
        // frame visibility pyramid (UESceneNode/BoundVisible, shipped port), without
        // wall culling and in actor-list order. Order and composition no longer
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

        // Frame occlusion (UEOcclusion — URender::OccludeBsp, D-56): the walk world comes from the runtime (model,
        // zones, viewer and device flags, warp-zone Generate event; the interpreter executes it).
        public Func<UEInterpreter, UEObject, UEOcclusionWorld> OcclusionWorldFor;
        public readonly UEOcclusion Occlusion = new();
        public (UEVector Location, UERotator Rotation)? ProbeCamera;   // probes only (OpenHP1 --shot --cam)
        // Frame tree of THIS tick's OccludeBsp (master and children with sprite lists; WorldFrame.Views is its projection);
        // null when the tick ran none (DrawWorld gate closed, e.g. the menu book's bNoDrawWorld) — never the previous tick's.
        public UESceneNode LastMasterFrame;
        readonly string _systemDir;
        UETextureMetrics _spriteTextures;
        // Sprite texture USize/VSize (Texture +0x34/+0x38) — from the install packages (root above System). null — not found.
        public Func<string, (int USize, int VSize)?> SpriteTextureSize;
        (int, int)? DefaultSpriteTextureSize(string path)
        {
            _spriteTextures ??= new UETextureMetrics(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_systemDir)));
            UETextureMetrics.Row r = _spriteTextures.Find(path);
            if (r is null || !int.TryParse(r.Values[0], out int u) || !int.TryParse(r.Values[1], out int v)) return null;
            return (u, v);
        }
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
        // The corona ray of this frame. NOT ORIGINAL (Enhanced, author 2026-11-08, audit/DEFERRED_DEBT_2026-10-04.md
        // "Coronas hidden by pawns"): pawns on the ray hide the corona too; Original runs the shipped TraceFlags 6 alone.
        public Func<UEVector, UEVector, bool> CoronaClear => EnhancedVisuals
            ? _coronaEnhancedClear ??= (s, e) => CoronaLineClear(s, e) && CoronaPawnClear(s, e)
            : CoronaLineClear;
        double _viewportClock;
        public double ViewportClock => _viewportClock;   // the viewport (wall) clock: coronas, actor light fade and AmbientGlow
        CoronaInstance[] _coronas;
        // ObjectId -> view indices of the frames whose DrawFrame drew the actor (WorldFrame.ActorViews).
        Dictionary<int, List<int>> _actorViews = new();
        // WorldFrame.DrawOrder: per frame of the tree (index view + 1) the actors in DrawActorSprite order.
        int[][] _drawOrder;
        ActorViewSets _actorViewSets;   // with the LOD memberships (MeshLodPart), built by DirectRenderStep
        // MESH LOD (D-62, Original only): mesh LOD data by the Mesh reference (the DrawLodMesh request per draw, DirectRenderStep).
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
        public IReadOnlyDictionary<int, int[]> SurfDynamicLights => _surfDyn;
        public string SurfDynamicLightsRefusal;
        readonly HashSet<int> _surfDynSeen = new();
        void CollectSurfDynamicLights(UESceneNode master, UEOcclusionWorld w)
        {
            _surfDyn = new Dictionary<int, int[]>();
            SurfDynamicLightsRefusal = null;
            if (w.SpanBased?.Invoke() == true)
            {
                bool any = false;
                void Any(UESceneNode f) { any |= f.SurfLights.Count > 0; foreach (UESceneNode c in f.Children) Any(c); }
                Any(master);
                if (any) SurfDynamicLightsRefusal = "SpanBased device: dynamic lights reached surfaces, but the port keeps no draw records for it — +0x48 not assembled";
                return;
            }
            HashSet<int> seen = _surfDynSeen;   // scratch (allocation only)
            seen.Clear();
            void Visit(UESceneNode f)
            {
                foreach (UESceneNode c in f.Children) Visit(c);
                foreach (var p in f.DrawnPolys)
                    if (seen.Add(p.Surf) && f.SurfLights.TryGetValue(p.Surf, out int[] ids)) _surfDyn[p.Surf] = ids;
            }
            Visit(master);
        }
        List<string> _drawnTextures;

        // Occlusion frame tree -> WorldFrame.Views (D-61): top-down walk, M = master Coords⁻¹ ∘ frame Coords
        // (axes orthogonal: Coords⁻¹(v) = O + v.x·X + v.y·Y + v.z·Z), NearClip from the frame axes into its world.
        internal static List<ViewFrame> ProjectViews(UESceneNode master, Func<int, ViewSource> sourceOf)
            => ProjectViews(master, sourceOf, null);

        // index — tree frame -> index in Views (master — −1), for DrawFrame decal pieces. drawn — receives each frame's
        // polygons (WorldFrame.Drawn: master first, then the views in the same order).
        internal static List<ViewFrame> ProjectViews(UESceneNode master, Func<int, ViewSource> sourceOf, Dictionary<UESceneNode, int> index,
            List<DrawnWorld> drawn = null, Func<int, int, ViewSource> polyOf = null)
        {
            var views = new List<ViewFrame>();
            index?.Add(master, -1);
            drawn?.Add(DrawnOf(master, polyOf));
            BrushCoords r = master.Coords;
            void Walk(UESceneNode f, int parent)
            {
                foreach (UESceneNode c in f.Children)
                {
                    int kind = c.Kind switch
                    {
                        UESceneNode.KindSky => ViewFrame.KindSky, UESceneNode.KindMirror => ViewFrame.KindMirror,
                        UESceneNode.KindWarp => ViewFrame.KindWarp,
                        _ => throw new InvalidOperationException($"child frame without an OccludeBsp branch (Kind {c.Kind})"),
                    };
                    BrushCoords k = c.Coords;
                    double E(int i, int j)   // (X_r⊗X_k + Y_r⊗Y_k + Z_r⊗Z_k)[i][j]
                        => (double)C(r.X, i) * C(k.X, j) + (double)C(r.Y, i) * C(k.Y, j) + (double)C(r.Z, i) * C(k.Z, j);
                    var m = new Affine
                    {
                        M00 = (float)E(0, 0), M01 = (float)E(0, 1), M02 = (float)E(0, 2),
                        M10 = (float)E(1, 0), M11 = (float)E(1, 1), M12 = (float)E(1, 2),
                        M20 = (float)E(2, 0), M21 = (float)E(2, 1), M22 = (float)E(2, 2),
                    };
                    double[] t = new double[3];
                    for (int i = 0; i < 3; i++)
                        t[i] = C(r.Origin, i) - (E(i, 0) * k.Origin.x + E(i, 1) * k.Origin.y + E(i, 2) * k.Origin.z);
                    m.M03 = (float)t[0]; m.M13 = (float)t[1]; m.M23 = (float)t[2];
                    UEVector cn = default; float cd = 0f;
                    if (c.NearClipW != 0f)
                    {
                        var nv = c.NearClipN;
                        double nx = (double)k.X.x * nv.x + (double)k.Y.x * nv.y + (double)k.Z.x * nv.z;
                        double ny = (double)k.X.y * nv.x + (double)k.Y.y * nv.y + (double)k.Z.y * nv.z;
                        double nz = (double)k.X.z * nv.x + (double)k.Y.z * nv.y + (double)k.Z.z * nv.z;
                        cn = new UEVector((float)nx, (float)ny, (float)nz);
                        cd = (float)(-(nx * k.Origin.x + ny * k.Origin.y + nz * k.Origin.z) - c.NearClipW);
                    }
                    var src = new ViewSource[c.SourceSurfs.Count];
                    for (int i = 0; i < src.Length; i++) src[i] = sourceOf(c.SourceSurfs[i]);
                    int me = views.Count;
                    index?.Add(c, me);
                    views.Add(new ViewFrame(parent, kind, c.Recursion, m, cn, cd,
                        BitConverter.SingleToInt32Bits(c.Mirror) < 0, src, c.Coords, c.Zone));
                    drawn?.Add(DrawnOf(c, polyOf));
                    Walk(c, me);
                }
            }
            Walk(master, -1);
            return views;
        }

        static float C(UEVector v, int i) => i == 0 ? v.x : i == 1 ? v.y : v.z;

        // A frame's FSavedPoly list -> DrawnWorld: level nodes in OccludeBsp order, mover polygons once however many of
        // their tracker fragments were drawn (the frontend draws a mover polygon whole; its coplanar pieces are
        // UELevelRuntime.MoverCoplanarPieces), sky windows (UESceneNode.SkyWindows). polyOf: (surface, node) -> level surface or
        // the mover polygon of the fragment's owner (UEOcclusionWorld.PolyOf).
        internal static DrawnWorld DrawnOf(UESceneNode f, Func<int, int, ViewSource> polyOf)
        {
            var nodes = new List<int>(f.DrawnPolys.Count);
            List<int> back = null;
            var movers = new List<ViewSource>();
            HashSet<(int, int)> seen = null;
            foreach (var (surf, node, _, fromBack) in f.DrawnPolys)
            {
                ViewSource src = polyOf(surf, node);
                if (src.Surf >= 0) { nodes.Add(node); if (fromBack) (back ??= new List<int>()).Add(node); continue; }
                seen ??= new HashSet<(int, int)>();
                if (seen.Add((src.MoverId, src.MoverPoly))) movers.Add(src);
            }
            var sky = new int[f.SkyWindows.Count];
            for (int i = 0; i < sky.Length; i++)
            {
                if (polyOf(f.SkyWindows[i].Surf, f.SkyWindows[i].Node).Surf < 0)
                    throw new NotSupportedException($"sky window on a mover polygon (tracker surface {f.SkyWindows[i].Surf}): the frontend has no mover backdrop");
                sky[i] = f.SkyWindows[i].Node;
            }
            (int, ViewSource, int)[] reflagged = null;
            if (f.Reflagged.Count > 0)
            {
                reflagged = new (int, ViewSource, int)[f.Reflagged.Count];
                for (int i = 0; i < reflagged.Length; i++)
                {
                    var (surf, node, pf) = f.Reflagged[i];
                    ViewSource src = polyOf(surf, node);
                    reflagged[i] = (src.Surf >= 0 ? node : -1, src, pf);
                }
            }
            return new DrawnWorld(nodes.ToArray(), movers.ToArray(), sky, back?.ToArray(), reflagged);
        }

        // Surf->Texture of every polygon a frame of the tree drew, first appearance order (DrawFrame Update, see DrawnWorld);
        // a surface without a texture is not updated (Level->DefaultTexture, Render.dll 0x10b258af..0x10b258c1).
        internal static List<string> DrawnTexturesOf(UESceneNode master, UEOcclusionWorld w)
        {
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Walk(UESceneNode f)
            {
                foreach (var (surf, _, _, _) in f.DrawnPolys)
                    if (w.Bsp?.MaterialOfSurf(surf)?.Path is string p && seen.Add(p)) list.Add(p);
                foreach (UESceneNode c in f.Children) Walk(c);
            }
            Walk(master);
            return list;
        }
        public int DirectChildFrames, DirectChildEmitters, DirectChildMeshes, ChildShadowUpdates;

        // FDynamicSprite::Setup of a mesh actor (Render 0x10b30cec-0x10b30dc0): IsA AParticleFX with bit +0x3c8
        // gives the full screen without a box — not ported for meshes (loud); otherwise the GetRenderBoundingBox(1) box,
        // reject when |X|² = 0 (sum order y² + z² + x², fcomp 0x10b30d89), then frame BoundVisible.
        // Setup rejects an empty screen rectangle (Y2 <= Y1, Render 0x10b30dc0..0x10b30e08): `sb.Y2 > sb.Y1` below.
        // AParticleFX::GetRenderBoundingBox (Engine 0x103c1060, export 0x1030464c; disasm 0x103c1060..0x103c18e3). Called
        // by FDynamicSprite::Setup in the render window — x87 PC24: every operation below rounds to float.
        //   1. Shift M = Location − LastUpdateLocation; L = max(|ParticleList.Max − Min|², 10000) (sums (x²+z²)+y²);
        //      |M|² > L -> Update(0): success and a container -> the container box as-is, otherwise zero Coords.
        //   2. Otherwise box P = ParticleList (+0x54); when Age > ElapsedTime it grows:
        //      h = ½·max(SizeWidth, SizeLength) (Rand < 0 -> 0); source: Distribution 2 with Owner.Mesh — FBox(owner
        //      mesh box), Pattern — ±(0, ½DrawScale, ½DrawScale), otherwise ±½(SourceDepth, SourceWidth, SourceHeight),
        //      both via FBox(·, ToLocal()) (slot +0x98); G = source ± h; P = P valid ? P ∪ G : G.
        //   3. A resting bSteadyState with Lifetime ≤ ElapsedTime — done. Otherwise T = min(Age − ElapsedTime, Lifetime),
        //      r = (max(Speed)+Chaos/ChaosDelay)·T + size correction, ·1.1; P = P ± r; gravity offset
        //      D = (GravityModifier·ZoneGravity + Gravity)·T²·½·1.1; P += P.Min + D, P += P.Max + D.
        //   Result — FBox::GetCoords(P).
        internal BrushCoords ParticleRenderBoundingBox(UEObject fx)
        {
            FxState s = FxStateOf(fx);
            UEVector loc = UEValue.AsVector3(fx.GetProp(PK.Location));
            UEVector lu = UEValue.AsVector3(fx.GetProp(PK.LastUpdateLocation));
            float mx = loc.x - lu.x, my = loc.y - lu.y, mz = loc.z - lu.z;
            float ex = s.BoundsMax.x - s.BoundsMin.x, ey = s.BoundsMax.y - s.BoundsMin.y, ez = s.BoundsMax.z - s.BoundsMin.z;
            float lim = (ex * ex + ez * ez) + ey * ey;
            if (!(lim >= 10000f)) lim = 10000f;                                       // fcom; test ah,1
            if ((mx * mx + mz * mz) + my * my > lim)                                  // fcomp st1; test ah,0x41
            {
                bool ok;
                _grbbPath = true;
                try { ok = RenderStepFx(fx); } finally { _grbbPath = false; }
                if (ok) return BoxGetCoords(s.BoundsMin, s.BoundsMax);
                return default;
            }
            UEObject owner = fx.GetProp(PK.Owner) as UEObject;
            UEObject zone = (fx.GetProp(PK.Region) as UEObject)?.GetProp(PK.Zone) as UEObject ?? fx.GetProp(PK.Level) as UEObject;
            var input = new PfxBoxIn
            {
                Location = loc, LastUpdateLocation = lu, ListMin = s.BoundsMin, ListMax = s.BoundsMax, ListValid = s.BoundsValid,
                Age = PropF(fx, "Age"), ElapsedTime = PropF(fx, "ElapsedTime"),
                SizeWidth = Range(fx, "SizeWidth"), SizeLength = Range(fx, "SizeLength"),
                SteadyState = UEValue.ToBool(fx.GetProp(PK.bSteadyState)),
                OwnerMesh = UEValue.ToInt(fx.GetProp(PK.Distribution)) == 2 && owner is not null && owner.GetProp(PK.Mesh) is not null,
                Pattern = fx.GetProp(PK.Pattern) is not null, DrawScale = PropF(fx, "DrawScale"),
                SourceWidth = Range(fx, "SourceWidth"), SourceHeight = Range(fx, "SourceHeight"), SourceDepth = Range(fx, "SourceDepth"),
                Rotation = UEValue.AsRotator(fx.GetProp(PK.Rotation)),
                Lifetime = Range(fx, "Lifetime"), Speed = Range(fx, "Speed"), SizeEndScale = Range(fx, "SizeEndScale"),
                Chaos = PropF(fx, "Chaos"), ChaosDelay = PropF(fx, "ChaosDelay"),
                HasZone = zone is not null, ZoneGravity = zone is null ? default : UEValue.AsVector3(zone.GetProp(PK.ZoneGravity)),
                GravityModifier = PropF(fx, "GravityModifier"), Gravity = UEValue.AsVector3(fx.GetProp(PK.Gravity)),
            };
            // The closure only when the box reads it (allocation only: ParticleRenderBox calls it only for OwnerMesh).
            return ParticleRenderBox(input, input.OwnerMesh ? OwnerMeshBox(owner) : null, fx.GetProp(PK.Name)?.ToString());
        }

        Func<BrushCoords> OwnerMeshBox(UEObject owner) => () =>
        {
            if (MeshRenderBoundingBox is null) throw new InvalidOperationException("ParticleFX box: owner mesh box (UELevelRuntime.MeshRenderBoundingBox) not bound");
            return MeshRenderBoundingBox(owner);
        };

        static (float Base, float Rand) Range(UEObject o, string p) => (RangeRaw(o, p, false), RangeRaw(o, p, true));

        // Inputs of AParticleFX::GetRenderBoundingBox after the shift branch (actor fields by offset — audit/prop_offsets.py).
        public struct PfxBoxIn
        {
            public UEVector Location, LastUpdateLocation;           // +0xfc, +0x380
            public UEVector ListMin, ListMax; public bool ListValid; // ParticleList +0x54..+0x6c
            public float Age, ElapsedTime;                           // +0x3a8, +0x3ac
            public (float Base, float Rand) SizeWidth, SizeLength;   // +0x2d0, +0x2d8
            public bool SteadyState;                                 // +0x29c bit 0
            public bool OwnerMesh;                                   // Distribution (+0x334) == 2 && Owner && Owner.Mesh
            public bool Pattern; public float DrawScale;             // +0x338, +0x16c
            public (float Base, float Rand) SourceWidth, SourceHeight, SourceDepth;   // +0x264, +0x26c, +0x274
            public UERotator Rotation;                               // +0x108
            public (float Base, float Rand) Lifetime, Speed, SizeEndScale;            // +0x2a8, +0x2a0, +0x2e0
            public float Chaos, ChaosDelay;                          // +0x318, +0x31c
            public bool HasZone; public UEVector ZoneGravity;        // Region.Zone ?? Level, +0x260
            public float GravityModifier; public UEVector Gravity;   // +0x344, +0x348
        }

        // Body after the shift branch (0x103c1218..0x103c18e3). ownerMeshBox — the owner mesh box (vtable +0x70); called
        // exactly where shipped calls it (after Age > ElapsedTime and the particle size) — for a skeletal mesh this is ApplyAnim.
        public static BrushCoords ParticleRenderBox(in PfxBoxIn i, Func<BrushCoords> ownerMeshBox, string who = null)
        {
            UEVector pMin = i.ListMin, pMax = i.ListMax;
            bool pValid = i.ListValid;
            float age = i.Age, elapsed = i.ElapsedTime;
            if (!(age > elapsed)) return BoxGetCoords(pMin, pMax);
            static float NonNeg(float v) => v >= 0f ? v : 0f;                        // fcom 0; test ah,1 (NaN -> 0)
            float sl = NonNeg(i.SizeLength.Rand) + i.SizeLength.Base;
            float size = NonNeg(i.SizeWidth.Rand) + i.SizeWidth.Base;
            if (!(size >= sl)) size = sl;
            float half = size * 0.5f;
            UEVector loc = i.Location, lu = i.LastUpdateLocation;
            bool steadyAtRest = i.SteadyState && loc.x == lu.x && loc.y == lu.y && loc.z == lu.z;
            UEVector gMin, gMax;
            if (i.OwnerMesh) (gMin, gMax) = BoxFromCoords24(ownerMeshBox());
            else
            {
                UEVector v;
                if (i.Pattern)
                {
                    float d = i.DrawScale * 0.5f;
                    v = new UEVector(0f, d, d);
                }
                else
                {
                    float hx = (NonNeg(i.SourceDepth.Rand) + i.SourceDepth.Base) * 0.5f;
                    float hy = (NonNeg(i.SourceWidth.Rand) + i.SourceWidth.Base) * 0.5f;
                    float hz = (NonNeg(i.SourceHeight.Rand) + i.SourceHeight.Base) * 0.5f;
                    v = new UEVector(hx, hy, hz);
                }
                (gMin, gMax) = BoxTransform24(new UEVector(-v.x, -v.y, -v.z), v, ToLocal24(i.Location, i.Rotation));
            }
            gMin = new UEVector(gMin.x - half, gMin.y - half, gMin.z - half);
            gMax = new UEVector(gMax.x + half, gMax.y + half, gMax.z + half);
            if (!pValid) { pMin = gMin; pMax = gMax; }
            else
            {
                pMin = new UEVector(pMin.x > gMin.x ? gMin.x : pMin.x, pMin.y > gMin.y ? gMin.y : pMin.y, pMin.z > gMin.z ? gMin.z : pMin.z);
                pMax = new UEVector(!(pMax.x >= gMax.x) ? gMax.x : pMax.x, !(pMax.y >= gMax.y) ? gMax.y : pMax.y, !(pMax.z >= gMax.z) ? gMax.z : pMax.z);
            }
            float lifeBase = i.Lifetime.Base, lifeRand = i.Lifetime.Rand;
            if (steadyAtRest && !(NonNeg(lifeRand) + lifeBase > elapsed)) return BoxGetCoords(pMin, pMax);
            float life = NonNeg(lifeRand) + lifeBase;
            float span = age - elapsed;
            float t = span > life ? life : span;                                      // fcomp st1; test ah,0x41
            float speed = NonNeg(i.Speed.Rand) + i.Speed.Base;
            if (i.ChaosDelay > 0f) speed = speed + i.Chaos / i.ChaosDelay;
            float travel = speed * t;
            float endScale = NonNeg(NonNeg(i.SizeEndScale.Rand) + i.SizeEndScale.Base);
            float grow = endScale * half - 1f;
            float lifeMin = (lifeRand > 0f ? 0f : lifeRand) + lifeBase;                // test ah,0x41: Rand > 0 -> 0
            if (lifeMin > t) grow = (t / lifeMin) * grow;
            float r = (travel + grow) * 1.1f;
            pMin = new UEVector(pMin.x - r, pMin.y - r, pMin.z - r);
            pMax = new UEVector(pMax.x + r, pMax.y + r, pMax.z + r);
            if (!i.HasZone) throw new InvalidOperationException($"ParticleFX {who}: no Region.Zone and Level (GetRenderBoundingBox 0x103c179c)");
            UEVector zg = i.ZoneGravity, gr = i.Gravity;
            float gm = i.GravityModifier;
            float gx = gm * zg.x + gr.x, gy = gm * zg.y + gr.y, gz = gm * zg.z + gr.z;
            float k = (t * t) * 0.5f;
            float dx = (gx * k) * 1.1f, dy = (gy * k) * 1.1f, dz = (k * gz) * 1.1f;
            var a = new UEVector(pMin.x + dx, pMin.y + dy, pMin.z + dz);
            var b = new UEVector(pMax.x + dx, pMax.y + dy, pMax.z + dz);
            (pMin, pMax) = BoxAdd24(pMin, pMax, a);
            (pMin, pMax) = BoxAdd24(pMin, pMax, b);
            return BoxGetCoords(pMin, pMax);
        }

        // Range Rand without a clamp (the caller clamps by its own comparison rule).
        static float RangeRaw(UEObject o, string p, bool rand)
            => o.GetProp(p) is UEObject bag ? UEValue.AsFloat(bag.GetProp(rand ? PK.Rand : PK.Base)) : 0f;

        // FBox::GetCoords (Core.dll.c:54460): Origin = Min, axes = sides.
        static BrushCoords BoxGetCoords(UEVector min, UEVector max) => UERenderVisibility.BoxCoords(min, max);

        // FBox::operator+=(FVector) (Core 0x1011ba10) on a valid box: Min takes the point when Min > p; Max when Max < p
        // or unordered.
        static (UEVector, UEVector) BoxAdd24(UEVector min, UEVector max, UEVector p) => (
            new UEVector(min.x > p.x ? p.x : min.x, min.y > p.y ? p.y : min.y, min.z > p.z ? p.z : min.z),
            new UEVector(!(max.x >= p.x) ? p.x : max.x, !(max.y >= p.y) ? p.y : max.y, !(max.z >= p.z) ? p.z : max.z));

        // FBox::FBox(FCoords) (Core 0x1014f0c0): Min = Max = Origin; each component of each axis (X, Y, Z) is added
        // to Max when ≥ 0, otherwise (and for NaN) to Min.
        static (UEVector, UEVector) BoxFromCoords24(in BrushCoords c)
        {
            float nx = c.Origin.x, ny = c.Origin.y, nz = c.Origin.z, xx = nx, xy = ny, xz = nz;
            static void Add(float v, ref float mx, ref float mn) { if (v >= 0f) mx = v + mx; else mn = v + mn; }
            Add(c.X.x, ref xx, ref nx); Add(c.X.y, ref xy, ref ny); Add(c.X.z, ref xz, ref nz);
            Add(c.Y.x, ref xx, ref nx); Add(c.Y.y, ref xy, ref ny); Add(c.Y.z, ref xz, ref nz);
            Add(c.Z.x, ref xx, ref nx); Add(c.Z.y, ref xy, ref ny); Add(c.Z.z, ref xz, ref nz);
            return (new UEVector(nx, ny, nz), new UEVector(xx, xy, xz));
        }

        // FBox::FBox(FBox, FCoords) (Core 0x1014f170): axes — box sides along the C axes, origin — sum per the disasm, then
        // FBox(FCoords).
        static (UEVector, UEVector) BoxTransform24(UEVector min, UEVector max, in BrushCoords c)
        {
            float dz = max.z - min.z, dy = max.y - min.y, dx = max.x - min.x;
            // RU EA (UEEdition.RuOrder; RU Core 0x1014eaf2..0x1014eb09): Origin.x = ((min.z·Z.x + min.y·Y.x) + min.x·X.x) + O.x.
            var o = new UEVector(UEEdition.RuOrder ? ((min.z * c.Z.x + min.y * c.Y.x) + min.x * c.X.x) + c.Origin.x
                    : ((min.y * c.Y.x + min.x * c.X.x) + min.z * c.Z.x) + c.Origin.x,
                ((c.X.y * min.x + c.Z.y * min.z) + c.Y.y * min.y) + c.Origin.y,
                ((c.X.z * min.x + c.Z.z * min.z) + c.Y.z * min.y) + c.Origin.z);
            return BoxFromCoords24(new BrushCoords
            {
                Origin = o,
                X = new UEVector(dx * c.X.x, dx * c.X.y, dx * c.X.z),
                Y = new UEVector(dy * c.Y.x, dy * c.Y.y, dy * c.Y.z),
                Z = new UEVector(dz * c.Z.x, dz * c.Z.y, dz * c.Z.z),
            });
        }

        // AActor::ToLocal (Engine 0x1031bac0; vtable slot +0x98, called by GetRenderBoundingBox 0x103c13b5) in PC24:
        // unit axes, *= roll, pitch, yaw (FCoords::operator/=(FRotator) matrices, BrushCoords.DivRotator; product —
        // UEOcclusion.MulAssign24), then Origin += Location (0x1031bd15..0x1031bd56).
        static BrushCoords ToLocal24(UEVector l, UERotator r)
        {
            BrushCoords c = UEOcclusion.DivRotator24(BrushCoords.Identity, r.Pitch, r.Yaw, r.Roll);
            c.Origin = new UEVector(c.Origin.x + l.x, c.Origin.y + l.y, c.Origin.z + l.z);
            return c;
        }

        bool MeshSpriteSetup(UEObject a, UESceneNode f, out UERenderVisibility.ScreenBounds sb)
        {
            sb = default;
            if (UEInterpreter.ClassIsA(a.Class, "ParticleFX"))
                throw new NotSupportedException($"FDynamicSprite::Setup: mesh AParticleFX (bit +0x3c8, full screen) not ported actor={a.GetProp(PK.Name)}");
            if (MeshRenderBoundingBox is null) throw new InvalidOperationException("DirectRenderStep: mesh box (UELevelRuntime.MeshRenderBoundingBox) not bound to the producer");
            BrushCoords box = MeshRenderBoundingBox(a);
            float lx = box.X.y * box.X.y + box.X.z * box.X.z + box.X.x * box.X.x;
            if (lx == 0f) return false;
            SetupBoundVisibleResidue(box);
            return UERenderVisibility.BoundVisible(f, box, out sb) && sb.Y2 > sb.Y1;
        }

        // URender::DrawWorld by the direct path (Render.dll.c:2643-2660): master OccludeFrame (SetupDynamics, OccludeBsp,
        // then children's OccludeFrame, Render.dll.c:603-718), then DrawFrame — children before the parent (Render.dll.c:1011-1014),
        // frame sprites in DrawFrame order (UESpriteSort on a non-SpanBased device): mesh GetFrame and emitter Update(0). A MESH runs DrawActorSprite once per
        // DRAW — every frame whose sprite list holds it, with that frame's FDynamicSprite and FX (the DrawLodMesh LOD
        // request) — like the original: GetFrame is not idempotent across draws of one tick (ULodMesh vertex-cache count
        // and tween refill, ApplyAnim channel overlay and its tween, the weapon branch's writes). An emitter or shadow is
        // updated at most once per tick however many frames see it (a repeated Update(0) in the same tick gives dt = 0).
        // Sprites are not interleaved into surface list 2 (RenderDrawOrder): only the call order between emitters depends
        // on it (the render rand() stream). shadowsOn — _shadowBlockers is empty.
        List<UEObject> DirectRenderStep(UEObject player, UEInterpreter interp,
            int frameX, int frameY, float fov, IEnumerable<UEObject> actors, bool shadowsOn)
        {
            if (ParticleDensityNow is null) throw new InvalidOperationException("DirectRenderStep: Client.ParticleDensity not bound to the producer");
            if (DrawMesh is null) throw new InvalidOperationException("DirectRenderStep: mesh branch (UELevelRuntime.DrawMeshActor) not bound to the producer");
            if (MeshLodOf is null) throw new InvalidOperationException("DrawLodMesh LOD: mesh LOD data (UELevelRuntime) not bound to the producer");
            DrawnMeshes.Clear();
            _meshPoses = new List<MeshPose>();
            _spriteActors = new List<int>();
            _actorViews = new Dictionary<int, List<int>>();
            ParticleDraws.Clear();
            var meshDraws = new List<(int View, MeshPose Pose)>();
            DirectSprites = 0;
            int rendMap = UEValue.ToInt(player.GetProp(PK.RendMap));
            // DrawLodMesh identity gate: URender+0x3c ≠ 0 (console REND WIRE, not ported — D-76, see
            // UELevelRuntime.DrawMeshActor) or RendMap ∈ {13,14,15,1}.
            bool identity = rendMap is 13 or 14 or 15 or 1;
            DirectMeshes = DirectEmitters = 0;
            DirectChildEmitters = DirectChildMeshes = 0;
            UESceneNode master = OccludeWorld(player, interp, frameX, frameY, fov, actors, shadowsOn, sprites: true);
            var viewIndex = new Dictionary<UESceneNode, int>();
            _views = ProjectViews(master, _lastOcclusionWorld.SourceOf, viewIndex, _drawn = new List<DrawnWorld>(), _lastOcclusionWorld.PolyOf);
            var drawOrder = new List<int>[_views.Count + 1];
            _drawnTextures = DrawnTexturesOf(master, _lastOcclusionWorld);
            CollectSurfDynamicLights(master, _lastOcclusionWorld);
            var drawn = new List<UEObject>();
            var once = new HashSet<UEObject>();
            var weaponOf = new List<(int Weapon, int Pawn)>();
            WeaponPawns.Clear();
            IList<UEObject> levelActors = actors as IList<UEObject> ?? new List<UEObject>(actors);
            void DrawFrame(UESceneNode f)
            {
                foreach (UESceneNode c in f.Children) DrawFrame(c);
                // The frame's surfaces (and their decals, Render.dll.c:1455-1530) are drawn before its sprites.
                DrawDecals?.Invoke(f, viewIndex[f]);
                bool child = f.Parent is not null;
                // A device without SpanBased (D3DDrv) sorts the array by actor class and draws it in two passes
                // (UESpriteSort); a SpanBased one draws it in list order after pass 2 (no list-2 interleaving here).
                bool spanBased = _lastOcclusionWorld.SpanBased?.Invoke()
                    ?? throw new InvalidOperationException("DrawFrame: RenDev.SpanBased not bound to the occlusion world");
                IReadOnlyList<UESprite> order = spanBased ? f.Sprites : UESpriteSort.NonSpanBasedOrder(f.Sprites, x => x.Actor);
                List<int> seq = drawOrder[viewIndex[f] + 1] = new List<int>(order.Count);
                var inSeq = new HashSet<int>();
                void Drew(int id) { if (inSeq.Add(id)) seq.Add(id); }
                foreach (UESprite sp in order)
                {
                    UEObject a = sp.Actor;
                    // Every frame draws its own sprite list; the actor's membership in each frame that drew it.
                    if (!_actorViews.TryGetValue(a.ObjectId, out var inViews)) _actorViews[a.ObjectId] = inViews = new List<int>();
                    inViews.Add(viewIndex[f]);
                    int dt = UEValue.ToInt(a.GetProp(PK.DrawType));
                    bool mesh = dt is not (8 or 1 or 7);
                    bool firstDraw = once.Add(a);
                    if (!mesh) Drew(a.ObjectId);   // a mesh enters with its poses (the pawn, then its weapon)
                    // An emitter is drawn by every frame whose sprite list holds it: URender::DrawParticleSystem calls
                    // AParticleFX::Update(0) (a repeat in the same frame tree has dt = 0 and returns at once) and then its
                    // drawers, whose billboard pass writes the particles' Alpha (ParticleDrawerFade).
                    if (!mesh && !firstDraw && dt != 8) continue;
                    // An actor destroyed between Setup and DrawFrame (a shadow Update body, another actor's Setup, or its own
                    // emitter Update in an earlier frame of the tree). Shipped tests no bDeleteMe before the draw (DrawFrame
                    // 0x10b254f0, DrawActorSprite 0x10b32850): an emitter's Update returns false on its freed list and draws
                    // nothing (RenderStepFxBody). What a mesh or sprite draw reads from a destroyed actor is not established.
                    if (a.Destroyed && dt != 8)
                        throw new NotSupportedException($"DrawFrame: sprite of actor {a.GetProp(PK.Name)} destroyed after SetupDynamics is not ported");
                    if (dt == 8)
                    {
                        bool ok = RenderStepFx(a);
                        if (firstDraw) { drawn.Add(a); DirectEmitters++; if (child) DirectChildEmitters++; }
                        if (ok) ParticleDrawerFade(a, f, viewIndex[f]);
                    }
                    else if (dt is 1 or 7)
                    {
                        // Sprite DrawActorSprite (texture tile): the frontend draws it; the frame names what to draw.
                        _spriteActors.Add(a.ObjectId);
                        DirectSprites++;
                    }
                    else
                    {
                        // URender::DrawLodMesh LOD request of this draw (UEMeshLod.VertsRequested over this frame's
                        // FDynamicSprite and FX) for the actor whose Mesh is drawn — the pawn, or the third-person weapon
                        // during its swap (own mesh and LODBias, the pawn's sprite). Enhanced: no LOD (D-62 improvement).
                        int Request(UEObject owner)
                        {
                            if (EnhancedVisuals) return -1;
                            var lod = MeshLodOf(owner.GetProp(PK.Mesh));
                            return UEMeshLod.VertsRequested(lod.Lod, lod.ModelVerts, lod.SpecialVerts, lod.Skeletal,
                                UEInterpreter.AsFloat(owner.GetProp(PK.LODBias)), sp.X1, sp.Y1, sp.X2, sp.Y2, f.FX);
                        }
                        var poses = new List<MeshPose>();
                        DrawMesh(a, identity, f.Coords, DrawnMeshes, poses, Request);
                        foreach (MeshPose p in poses)
                        {
                            Drew(p.ObjectId);
                            meshDraws.Add((viewIndex[f], p));
                            if (p.Weapon && !weaponOf.Contains((p.ObjectId, a.ObjectId))) weaponOf.Add((p.ObjectId, a.ObjectId));
                        }
                        if (once.Add(a)) { DirectMeshes++; if (child) DirectChildMeshes++; }
                    }
                }
                // Corona block at the end of every DrawFrame (Render.dll 0x10b26742..0x10b26e0f); UECoronas gates it.
                if (CoronaLineClear is null || CoronaActorsByExport is null || (EnhancedVisuals && CoronaPawnClear is null))
                    throw new InvalidOperationException("DrawFrame coronas: line check / export index not bound to the producer");
                CoronaTable.Step(f, player, levelActors, RenderDeviceCoronas, CoronaLevel, CoronaActorsByExport,
                    _viewportClock, CoronaClear);
            }
            DrawFrame(master);
            _coronas = CoronaTable.Draws.ToArray();
            // The third-person weapon is drawn by its pawn's DrawActorSprite: it is in exactly the pawn's frames.
            foreach (var (weapon, pawn) in weaponOf) { _actorViews[weapon] = new List<int>(_actorViews[pawn]); WeaponPawns[weapon] = pawn; }
            _actorViewSets = MeshDrawParts(meshDraws);
            _drawOrder = Array.ConvertAll(drawOrder, l => l?.ToArray() ?? Array.Empty<int>());
            return drawn;
        }

        // The per-draw GetFrame results (pose + the level the draw's face loop used, DrawMeshActor) merged into one
        // MeshPose per (actor, weapon): draws with the same level and the same points form one part (MeshPose.LodParts;
        // a part's own Points only when they differ from the first draw's). Every draw full-mesh with the same points —
        // no parts (null).
        ActorViewSets MeshDrawParts(List<(int View, MeshPose Pose)> draws)
        {
            var keys = new List<(int Id, bool Weapon)>();
            var byKey = new Dictionary<(int, bool), List<(int View, MeshPose Pose)>>();
            foreach (var d in draws)
            {
                var k = (d.Pose.ObjectId, d.Pose.Weapon);
                if (!byKey.TryGetValue(k, out var l)) { byKey[k] = l = new(); keys.Add(k); }
                l.Add(d);
            }
            var parts = new List<(int Pose, int Verts, UEVector[] Points, List<int> Views)>();
            foreach (var k in keys)
            {
                var ds = byKey[k];
                UEVector[] basePts = ds[0].Pose.Points;
                var groups = new List<(int Verts, UEVector[] Points, List<int> Views)>();
                foreach (var d in ds)
                {
                    int lv = d.Pose.LodParts[0].Verts;
                    int g = groups.FindIndex(x => x.Verts == lv && SamePoints(x.Points, d.Pose.Points));
                    if (g < 0) groups.Add((lv, d.Pose.Points, new List<int> { d.View })); else groups[g].Views.Add(d.View);
                }
                int pi = _meshPoses.Count;
                _meshPoses.Add(new MeshPose(k.Id, k.Weapon, ds[0].Pose.MeshPath, basePts));
                if (groups.Count == 1 && groups[0].Verts < 0) continue;
                foreach (var g in groups) parts.Add((pi, g.Verts, SamePoints(g.Points, basePts) ? null : g.Points, g.Views));
            }
            var sets = ActorViewSets.From(_actorViews, parts.ConvertAll(x => x.Views.ToArray()), out int[] setIdx);
            for (int j = 0; j < parts.Count;)
            {
                int pose = parts[j].Pose;
                var list = new List<MeshLodPart>();
                for (; j < parts.Count && parts[j].Pose == pose; j++) list.Add(new MeshLodPart(parts[j].Verts, setIdx[j], parts[j].Points));
                MeshPose p = _meshPoses[pose];
                _meshPoses[pose] = new MeshPose(p.ObjectId, p.Weapon, p.MeshPath, p.Points, list);
            }
            return sets;
        }

        static bool SamePoints(UEVector[] a, UEVector[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (BitConverter.SingleToInt32Bits(a[i].x) != BitConverter.SingleToInt32Bits(b[i].x)
                    || BitConverter.SingleToInt32Bits(a[i].y) != BitConverter.SingleToInt32Bits(b[i].y)
                    || BitConverter.SingleToInt32Bits(a[i].z) != BitConverter.SingleToInt32Bits(b[i].z)) return false;
            return true;
        }

        // OccludeFrame of the master frame and all children (Render.dll.c:603-718). sprites — sprite Setup (FDynamicSprite::Setup
        // and constructor) in every frame; without them — only brushes, shadows and the walk itself (Generate, node bits, children).
        readonly UESceneNodeBanks _frameBanks = new();
        UESceneNode OccludeWorld(UEObject player, UEInterpreter interp, int frameX, int frameY, float fov,
            IEnumerable<UEObject> actors, bool shadowsOn, bool sprites)
        {
            if (UpdateMovingBrush is null) throw new InvalidOperationException("SetupDynamics: brush tracker (UELevelRuntime.UpdateBrushTracker) not bound to the producer");
            if (OcclusionWorldFor is null) throw new InvalidOperationException("OccludeBsp: traversal world (UELevelRuntime.OcclusionWorld) not bound to the producer");
            if (sprites && ParticleDensityNow is null) throw new InvalidOperationException("DirectRenderStep: Client.ParticleDensity not bound to the producer");
            IList<UEObject> list = actors as IList<UEObject> ?? new List<UEObject>(actors);
            int rendMap = UEValue.ToInt(player.GetProp(PK.RendMap));
            // URender::DrawWorld (0x10b27ba0): DAT_10b719b4++ before OccludeFrame.
            Occlusion.DrawWorldCounter++;
            _frameBanks.BeginTree(Occlusion.DrawWorldCounter);   // allocation only: the previous tree stays intact
            UESceneNode master = UESceneNode.CreateMaster(frameX, frameY, _cameraCache.Location, _cameraCache.Rotation, fov, rendMap,
                _frameBanks);
            UEOcclusionWorld w = OcclusionWorldFor(interp, player);
            master.Level = w.Level;
            // CreateMasterFrame (Render.dll.c:8113-8118): ZoneNumber = UModel::PointRegion(LevelInfo, Coords.Origin).
            master.Zone = w.Bsp is null ? 0 : w.Bsp.FindRegion(master.Coords.Origin).ZoneNumber & 0xff;
            LastMasterFrame = master;
            _lastOcclusionWorld = w;
            bool behindView = UEValue.ToBool(player.GetProp(PK.bBehindView));
            UEObject viewTarget = player.GetProp(PK.ViewTarget) as UEObject;
            bool particlesOn = sprites && rendMap is not (13 or 14 or 15) && ParticleDensityNow() != 0;
            DirectMovers = 0;
            int showFlags = UEValue.ToInt(player.GetProp(PK.ShowFlags));
            int nodeCount = w.Bsp?.LiveNodeCount ?? 0;
            var shadowed = new HashSet<UEObject>();
            UESceneNode current = master;
            void Shadow(UEObject a)
            {
                if (!shadowsOn || shadowed.Contains(a)) return;
                if (a.GetDeclared("Actor", "Shadow") is not UEObject shadow || shadow.Destroyed) return;
                shadowed.Add(a);
                ShadowUpdateEntered?.Invoke(a);
                UELifecycle.CallEvent(interp, shadow, "Update", null);
                ShadowUpdateCount++;
                if (current.Parent is not null) ChildShadowUpdates++;
            }

            // Emitter FDynamicSprite::Setup (Render thunk_FUN_10b30c70) in frame f: the AParticleFX::GetRenderBoundingBox box
            // (vtable +0xa0, call 0x10b30d61; on a shift — Update(0) regardless of the frustum: the Shutdown budget -> actor
            // Destroy depends on the step count, pilot J15), reject when |X|² = 0 (order y² + z² + x²), frame BoundVisible.
            bool EmitterSetup(UEObject a, UESceneNode f, out UERenderVisibility.ScreenBounds sb)
            {
                sb = default;
                BrushCoords box = ParticleRenderBoundingBox(a);
                float lx = box.X.y * box.X.y + box.X.z * box.X.z + box.X.x * box.X.x;
                if (lx == 0f) return false;
                SetupBoundVisibleResidue(box);
                return UERenderVisibility.BoundVisible(f, box, out sb) && sb.Y2 > sb.Y1;
            }
            bool IsEmitter(UEObject a, int dt) => dt == 8 && particlesOn && UEInterpreter.ClassIsA(a.Class, "ParticleFX");
            bool IsMesh(UEObject a, int dt) => sprites && dt == 2 && a.GetProp(PK.Mesh) is not null && (showFlags & 0x20) == 0;
            // DT_Sprite (1) and DT_SpriteAnimOnce (7) — the sprite branch of FDynamicSprite::Setup (UERenderVisibility.SpriteSetup).
            bool IsSprite(int dt) => sprites && dt is 1 or 7;
            bool SpriteSetup(UEObject a, UESceneNode f, out UERenderVisibility.ScreenBounds sb)
            {
                string tex = WorldFrameProjector.AssetPath(a.GetProp(PK.Texture));
                bool has = !string.IsNullOrEmpty(tex) && tex != "None";
                int us = 0, vs = 0;
                if (has)
                {
                    (us, vs) = SpriteTextureSize(tex)
                        ?? throw new NotSupportedException($"FDynamicSprite::Setup: texture size of {tex} for sprite {a.GetProp(PK.Name)} not read");
                }
                return UERenderVisibility.SpriteSetup(f, UEValue.AsVector3(a.GetProp(PK.Location)), UEValue.AsFloat(a.GetProp(PK.DrawScale)),
                    UEValue.AsFloat(a.GetProp(PK.SpriteProjForward)), us, vs, has, showFlags, rendMap, out sb);
            }

            void OccludeFrame(UESceneNode f)
            {
                current = f;
                // OccludeFrame (Render.dll.c:676-682): Exclude — ViewTarget ?? Actor for a master frame without bBehindView.
                UEObject exclude = f.Parent is null && !behindView ? (viewTarget ?? player) : null;
                UERenderVisibility.SetupDynamicsPass(f, list, player, exclude, HighDetailActors, rendMap,
                    a =>
                    {
                        int dt = UEValue.ToInt(a.GetProp(PK.DrawType));
                        UERenderVisibility.ScreenBounds sb;
                        // FDynamicSprite::Setup (Render 0x10b30d56-0x10b30d94): the GetRenderBoundingBox(1) box BEFORE
                        // BoundVisible — for a skeletal mesh ApplyAnim(rootOnly) runs inside (bone 0 and the root block
                        // are written even for an actor outside the frustum); a degenerate box (|X axis|² = 0) is not drawn.
                        if (IsEmitter(a, dt)) { if (EmitterSetup(a, f, out sb)) Occlusion.CreateSprite(f, a, sb, nodeCount); }
                        else if (IsMesh(a, dt)) { if (MeshSpriteSetup(a, f, out sb)) Occlusion.CreateSprite(f, a, sb, nodeCount); }
                        else if (IsSprite(dt)) { if (SpriteSetup(a, f, out sb)) Occlusion.CreateSprite(f, a, sb, nodeCount); }
                    },
                    Shadow, f.Parent is null ? a => { if (UpdateMovingBrush(a)) DirectMovers++; } : null, f.Recursion,
                    a => Occlusion.SetupLight(f, a, nodeCount));
                Occlusion.OccludeBsp(f, w);
                Occlusion.ClearCache();
                foreach (UESceneNode c in f.Children) { DirectChildFrames++; OccludeFrame(c); }
            }
            // OccludeBsp iStack_a8: SupportsFogMaps (D3DDrv: 1) && VolumetricLighting && Viewport->Actor's zone bFogZone.
            UEObject viewerZone = (player?.GetDeclared("Actor", "Region") as UEObject)?.GetProp(PK.Zone) as UEObject;
            Occlusion.LeafVolumetricLighting = RenderDeviceVolumetricLighting && viewerZone is not null
                && UEValue.ToBool(viewerZone.GetProp(PK.bFogZone));
            OccludeFrame(master);
            return master;
        }

        // ── HOR+: WIDE-SCREEN HORIZONTAL (author decision, not original) ──
        //
        // NOT SHIPPED. UE1 keeps FovAngle horizontal at any aspect
        // (URender::ComputeRenderSize), so on a screen wider than 4:3 the vertical is cut.
        // Author chose Hor+: the vertical as for 4:3 with the same FovAngle, the horizontal
        // wider. ALL frame consumers get the effective FOV — the camera snapshot, the
        // shadow-visibility master frame and the viewport actor's FovAngle in the
        // Render.dll port mirror (CreateMasterFrame). So visibility (and with it
        // particle simulation and appFrand use) follows the widened view — the author
        // accepted this divergence from 4:3. Off, or a screen not wider than 4:3 —
        // FovAngle unchanged. Registered: DEFERRED_DEBT improvements list (author 2026-10-29, incl. the logic effect).
        // Set by the host through SetWideScreen (the frontend writes no Core field).
        public bool HorPlus { get; internal set; }
        public void SetWideScreen(bool horPlus, bool levelInfo43) { HorPlus = horPlus; LevelInfo43 = levelInfo43; }
        public float HorPlusFov(float fov)
        {
            // the aspect of the world frame (WorldFrameSize: the window), not of the rounded-up D-44 canvas
            if (!HorPlus || !(fov > 0f) || !(fov < 180f) || (long)_screenX * 3 <= (long)_screenY * 4) return fov;
            double t = Math.Tan(fov * 0.5 * Math.PI / 180.0) * (_screenX * 3.0) / (_screenY * 4.0);
            return (float)(2.0 * Math.Atan(t) * 180.0 / Math.PI);
        }

        int _consoleRegionX;
        public void SetConsoleRegion(int width) => _consoleRegionX = width;
        public bool ConsoleRegionActive => _consoleRegionX > 0 && _consoleRegionX < _viewportX;
        public int ConsoleRegionOffsetX => ConsoleRegionActive ? (_viewportX - _consoleRegionX) / 2 : 0;

        void ShiftHud(int first, int dx)
        {
            for (int i = first; i < _hud.Count; i++)
            {
                HudCommand h = _hud[i];
                HudGlyph[] g = h.Glyphs;
                if (g is not null)
                {
                    var moved = new HudGlyph[g.Length];
                    for (int k = 0; k < g.Length; k++)
                        moved[k] = new HudGlyph(g[k].Page, g[k].X + dx, g[k].Y, g[k].XL, g[k].YL, g[k].U, g[k].V, g[k].UL, g[k].VL);
                    g = moved;
                }
                _hud[i] = new HudCommand(h.Kind, h.X + dx, h.Y, h.XL, h.YL, h.U, h.V, h.UL, h.VL,
                    h.TexturePath, h.Text, h.FontPath, h.Color, h.Style, h.Smooth, h.Clipped, g, h.PolyFlags);
            }
        }

        static float CanvasF(UEObject c, string p) => UEValue.AsFloat(c.GetProp(p));
        static UEVector DrawColorOf(UEObject canvas)
            => canvas.GetProp(PK.DrawColor) is UEObject c
                ? new UEVector(UEValue.AsByte(c.GetProp(PK.R)),
                               UEValue.AsByte(c.GetProp(PK.G)),
                               UEValue.AsByte(c.GetProp(PK.B)))
                : default;
        static string TexPathOf(object v)
            => v is UEResourceRef rr ? (rr.ObjectPath ?? rr.Name)
             // Texture literals (Texture'HPBase.Icons.HarryBarFull') evaluate
             // to the UELib import/export object — same identity AssetPath uses.
             : v is UELib.Core.UObject uo ? uo.GetPath()
             // Runtime texture (CreateTextureFromBMP): path "SavePics.<name>",
             // by which the frontend takes pixels from UERuntimeTextures.
             : v is UEObject rto && UERuntimeTextures.PathOf(rto) is string rtp ? rtp
             : (v as UEObject)?.GetProp(PK.Name)?.ToString();
        static string FontPathOf(object f)
            => f is UEResourceRef rr ? (rr.ObjectPath ?? rr.Name)
            // The literal `Font'HPBase.HugeInkFont'` in a function BODY is an
            // ObjectConstToken and yields a UELib object (UEInterpreter.cs:781),
            // not a UEResourceRef: a literal becomes a resource reference only on
            // the defaults path (UEObject.ParseResourceRef). Both forms reach
            // Canvas.Font — HPConsole.SetupLanguage assigns fonts as
            // literals — and without this branch the path would be null. The parse is
            // identical to the neighbouring AssetPathOf, where the same form is read
            // as GetPath(); the font catalog expects exactly `Package.Name`.
             : f is UELib.Core.UObject uo ? uo.GetPath()
             : (f as UEObject)?.GetProp(PK.Name)?.ToString();

        public void DrawTile(UEFrame frame, object[] args, bool clipped)
        {
            UEObject canvas = frame.Context ?? frame.Object;
            object tex = args is { Length: > 0 } ? args[0] : null;
            if (tex is null)
            {
                // Shipped execDrawTile: log and EARLY RETURN — no pen update.
                HP1.Core.UETrace.Warn("DrawTile: Missing Texture");
                return;
            }
            float xl = UEValue.AsFloat(args[1]);
            float yl = UEValue.AsFloat(args[2]);
            float u = UEValue.AsFloat(args[3]), v = UEValue.AsFloat(args[4]);
            float ul = UEValue.AsFloat(args[5]), vl = UEValue.AsFloat(args[6]);
            float curX = CanvasF(canvas, "CurX"), curY = CanvasF(canvas, "CurY");

            if (clipped)
            {
                // ── UCanvas::execDrawTileClipped (Engine.dll.c:32151-32200) ──
                //
                // CORE COMPUTES THE GEOMETRY, not the rasteriser.
                //
                // Shipped order: first a negative pen (shifting the texture window
                // along with it), then overflow past ClipX/ClipY.
                // Every adjustment goes PROPORTIONALLY through UV, so the command carries
                // the final rectangle and the frontend has nothing to clip.
                // 32154 (0x10389fdd: fcomp 0; test ah,0x41): XL/YL <= 0 or NaN — draw nothing, do not move the pen.
                if (!(xl > 0f) || !(yl > 0f)) return;
                // x87 chains (disasm 0x1038a000..0x1038a0b7) in the PC24 window — per operation in float (ClipTileAxis*).
                ClipTileAxisNegative(ref curX, ref u, ref ul, ref xl);
                ClipTileAxisNegative(ref curY, ref v, ref vl, ref yl);
                ClipTileAxisOverflow(CanvasF(canvas, "ClipX"), curX, ref ul, ref xl);
                ClipTileAxisOverflow(CanvasF(canvas, "ClipY"), curY, ref vl, ref yl);
                canvas.Props["CurX"] = curX;           // 32158/32167: pen clamped to zero
                canvas.Props["CurY"] = curY;
            }

            // exec (0x1038971d / 0x1038a0b7): Style == STY_None — DrawTile is not
            // called at all, the pen moves (tail below).
            int style = UEValue.ToInt(canvas.GetProp(PK.Style));
            if (style != 0)
                EmitTile(canvas, tex, curX + CanvasF(canvas, "OrgX"), curY + CanvasF(canvas, "OrgY"),
                    xl, yl, u, v, ul, vl, style, TilePolyFlags(style, UEValue.ToBool(canvas.GetProp(PK.bNoSmooth))), clipped);
            // Tail shared by both commands (32193-32197 / execDrawTile):
            // CurX += SpaceX + XL; CurYL = max(CurYL, YL). For a clipped tile XL is
            // ALREADY the clipped width, so the pen follows what was drawn.
            // Disasm: fld SpaceX; fadd CurX; fadd XL; fstp CurX (execDrawTile 0x103897c4..0x103897cd,
            // execDrawTileClipped 0x1038a15e..0x1038a167); PC24 window — each addition in float (TilePenAdvance).
            canvas.Props["CurX"] = TilePenAdvance(CanvasF(canvas, "SpaceX"), curX, xl);
            // fld CurYL; fcom YL; test ah,1 (0x103897d0 / 0x1038a16a): YL is taken when CurYL < YL
            // and on NaN ("unordered" sets C0) — MathF.Max would return NaN.
            float curYL = CanvasF(canvas, "CurYL");
            canvas.Props["CurYL"] = curYL >= yl ? curYL : yl;
        }

        // ── UCanvas::DrawTile (virtual, vtable +0x60; Engine.dll 0x10387630) ──
        //
        // Rejection and clipping by the canvas FRAME (Frame->FX/FY, +0xb8/+0xbc), then
        // RenDev->DrawTile. Reject: XL <= 0, YL <= 0, X+XL <= 0, Y+YL <= 0,
        // X >= FX, Y >= FY (0x10387679..0x10387700). Left/top clip
        // (0x10387706..0x1038776b): t = X·UL/XL (by the ORIGINAL XL), X = 0,
        // XL = X+XL, U -= t, UL += t. Right/bottom (0x1038776e..0x103877c6):
        // XL > FX-X -> UL += ((FX-X) - XL)·UL/XL, XL = FX-X. PRECISION: inside the
        // Render window (FastMathWindow, HUD/console) — PC24, every operation in float,
        // like the exec helpers ClipTileAxis*; outside the window (LoadMap loading screen)
        // — PC64, intermediates in the register (modelled as double), stored as float.
        // Rest of the body: Texture->Update/AnimCur (a texture with AnimNext is a STOP in
        // TexturePolyFlags: Update is not ported), Lock with scale USize·UScale/USize
        // (cancels against the device division) and device PolyFlags =
        // exec flags | (Texture->PolyFlags & PF_Masked) (0x1038781f-0x1038782e).
        // Core decides the flags and puts them into the command; the frontend only applies
        // the blend mode they select.
        internal void EmitTile(UEObject canvas, object tex, float x, float y, float xl, float yl,
            float u, float v, float ul, float vl, int style, int execFlags, bool clipped)
        {
            CanvasFrame fr = FrameOf(canvas);
            float fx = fr.X, fy = fr.Y;
            if (!(xl > 0f) || !(yl > 0f)) return;
            if (FastMathWindow)
            {
                if (!FrameClipTilePc24(fx, fy, ref x, ref y, ref xl, ref yl, ref u, ref v, ref ul, ref vl)) return;
                EmitClippedTile(canvas, tex, x, y, xl, yl, u, v, ul, vl, style, execFlags, clipped);
                return;
            }
            float xEnd = (float)((double)x + xl), yEnd = (float)((double)y + yl);
            if (!((double)x + xl > 0.0) || !((double)y + yl > 0.0)) return;
            if (!(x < fx) || !(y < fy)) return;
            if (x < 0f)
            {
                double t = (double)x * ul / xl;
                x = 0f; xl = xEnd;
                u = (float)(u - t); ul = (float)(t + ul);
            }
            if (y < 0f)
            {
                double t = (double)y * vl / yl;
                y = 0f; yl = yEnd;
                v = (float)(v - t); vl = (float)(t + vl);
            }
            double availX = (double)fx - x;
            if (xl > availX) { ul = (float)((availX - xl) * ul / xl + ul); xl = (float)availX; }
            double availY = (double)fy - y;
            if (yl > availY) { vl = (float)((availY - yl) * vl / yl + vl); yl = (float)availY; }
            EmitClippedTile(canvas, tex, x, y, xl, yl, u, v, ul, vl, style, execFlags, clipped);
        }

        // UCanvas::DrawTile 0x10387679..0x103877c6 in the PC24 window: each fadd/fmul/fdiv
        // rounds to 24 bits = a float operation. false — rejected (no command).
        public static bool FrameClipTilePc24(float fx, float fy, ref float x, ref float y, ref float xl, ref float yl,
            ref float u, ref float v, ref float ul, ref float vl)
        {
            float xEnd = (float)(x + xl);
            if (!(0f < xEnd)) return false;
            float yEnd = (float)(y + yl);
            if (!(0f < yEnd) || !(x < fx) || !(y < fy)) return false;
            if (x < 0f)
            {
                float t = (float)((float)(x * ul) / xl);
                x = 0f; u = (float)(u - t); ul = (float)(t + ul); xl = xEnd;
            }
            if (y < 0f)
            {
                float t = (float)((float)(y * vl) / yl);
                y = 0f; v = (float)(v - t); vl = (float)(t + vl); yl = yEnd;
            }
            float ax = (float)(fx - x);
            if (ax < xl) { ul = (float)((float)((float)((float)(ax - xl) * ul) / xl) + ul); xl = ax; }
            float ay = (float)(fy - y);
            if (ay < yl) { vl = (float)((float)((float)((float)(ay - yl) * vl) / yl) + vl); yl = ay; }
            return true;
        }

        void EmitClippedTile(UEObject canvas, object tex, float x, float y, float xl, float yl,
            float u, float v, float ul, float vl, int style, int execFlags, bool clipped)
        {
            Func<object, int> texFlags = TexturePolyFlags
                ?? throw new InvalidOperationException("UCanvas::DrawTile: TexturePolyFlags not assigned (UELevelRuntime.FrameProducer)");
            int polyFlags = (texFlags(tex) & PF_Masked) | execFlags;
            _hud.Add(new HudCommand(HudKind.Tile, x, y, xl, yl, u, v, ul, vl,
                TexPathOf(tex), null, null, DrawColorOf(canvas), style,
                !UEValue.ToBool(canvas.GetProp(PK.bNoSmooth)), clipped, polyFlags: polyFlags));
        }

        // Texture flag word (UTexture +0x80) for a HUD resource reference; set by the
        // world when the producer is handed out (UELevelRuntime.FrameProducer -> UETextureFactsReader).
        public Func<object, int> TexturePolyFlags;

        const int PF_Masked = 0x2, PF_Translucent = 0x4, PF_Modulated = 0x40,
                  PF_TwoSided = 0x100, PF_NoSmooth = 0x800;

        // Tile exec-body flags are THE SAME for execDrawTile (0x1038971d-0x10389750)
        // and execDrawTileClipped (0x1038a0b7-0x1038a0ea): Style 3 -> PF_Translucent,
        // Style 4 -> PF_Modulated, others (incl. STY_Masked) -> 0; then
        // | ((Canvas+0x5c >> 1) & 1) << 11 — bNoSmooth -> PF_NoSmooth — and
        // `or dh,1` — PF_TwoSided. The mask comes ONLY from the texture (UCanvas::DrawTile).
        // PRECISION — PC24 (Render disasm): execDrawTile/execDrawTileClipped
        // run from HUD/console scripts inside the URender::PreRender window (appEnableFastMath(1), 0x10b1eeb1) ..
        // PostRender (appEnableFastMath(0), 0x10b1efa9); _controlfp(_PC_24) — every x87 operation rounds the mantissa
        // to 24 bits, i.e. matches a float operation (the exponent range is not reached here). The PC64
        // "single rounding" model is right for game logic, not for the canvas.
        // Negative pen: fld UL; fmul Cur; fdiv XL (d in the register); U = U − d; UL = d + UL; XL = XL + Cur; Cur = 0.
        // fcomp 0; test ah,1 (0x1038a000): C0 also on NaN ("unordered") — a NaN pen is clipped too.
        public static void ClipTileAxisNegative(ref float cur, ref float uv, ref float ulen, ref float len)
        {
            if (cur >= 0f) return;
            float d = (float)((float)(ulen * cur) / len);
            uv = (float)(uv - d);
            ulen = (float)(d + ulen);
            len = (float)(len + cur);
            cur = 0f;
        }

        // Overflow: a = Clip − Cur (in the register); if XL > a: UL = ((a − XL)·UL)/XL + UL; XL = a. fcomp on NaN is
        // "unordered", the branch is skipped (test ah, 0x41). PC24 — see above.
        public static void ClipTileAxisOverflow(float clip, float cur, ref float ulen, ref float len)
        {
            float a = (float)(clip - cur);
            if (!(len > a)) return;
            float diff = (float)(a - len);
            float q = (float)((float)(diff * ulen) / len);
            ulen = (float)(q + ulen);
            len = a;
        }

        // DrawTile/DrawTileClipped pen advance: (SpaceX + CurX) + XL, PC24 — each addition rounded to float.
        public static float TilePenAdvance(float spaceX, float curX, float xl)
            => (float)((float)(spaceX + curX) + xl);

        internal static int TilePolyFlags(int style, bool noSmooth)
            => (style == 3 ? PF_Translucent : style == 4 ? PF_Modulated : 0)
               | (noSmooth ? PF_NoSmooth : 0) | PF_TwoSided;

        // ── Canvas frame: Canvas.FramePtr (+0x7c) ─────────────────────────────
        //
        // UCanvas::Update (Engine.dll.c:79722) sets Frame = the UGameEngine::Draw
        // master frame — THE SAME FSceneNode that UConsole::PreRender
        // shrinks by the border (Engine.dll.c:91467: Frame->X/Y -= 2·Border), and
        // UConsole::PostRender restores in place from a copy taken before the shrink
        // (Engine.dll.c:32512: 0x5b words from DAT_105e89d8) — BEFORE the script
        // Console.PostRender. So the player's HUD sees the shrunk frame and the console
        // the full one. The frame is read by the text bodies: WrappedPrint (Frame->Y,
        // 0x1038800c) and glyph drawing FUN_10388b60 (Frame->X/Y).
        sealed class CanvasFrame { public int X, Y; }
        readonly System.Runtime.CompilerServices.ConditionalWeakTable<UEObject, CanvasFrame> _canvasFrames = new();
        CanvasFrame _masterFrame;

        CanvasFrame FrameOf(UEObject canvas)
            => _canvasFrames.TryGetValue(canvas, out CanvasFrame f) ? f
             : throw new InvalidOperationException(
                 "Canvas.Frame not assigned: UCanvas::Update has not run for this canvas "
                 + "(shipped would dereference a NULL FramePtr)");

        // Fixture seam: a fixture canvas gets its own frame the same way the real one
        // gets the master frame in UCanvas::Update.
        public void SetCanvasFrameForTest(UEObject canvas, int x, int y)
        {
            _canvasFrames.Remove(canvas);
            _canvasFrames.Add(canvas, new CanvasFrame { X = x, Y = y });
        }
        public (int X, int Y) CanvasFrameForTest(UEObject canvas)
        {
            CanvasFrame f = FrameOf(canvas);
            return (f.X, f.Y);
        }

        // Polygon flags by canvas Style — the same table in WrappedPrint
        // (0x10387d54-0x10387d79) and execDrawTextClipped (0x1038a3f1-0x1038a417):
        // STY_None -> PF_Invisible (1), STY_Translucent -> 4, STY_Modulated -> 0x40,
        // others -> 0; always | 0x1000802 (PF_Masked | PF_NoSmooth | 0x01000000).
        // A glyph goes to RenDev->DrawTile with THESE flags unchanged: FUN_10388b60
        // passes its flags argument last (0x10388c75-0x10388c79) and adds no flags of the
        // font page texture. The same flags travel in the text command
        // (HudCommand.PolyFlags) — the frontend takes the glyph blend mode
        // from there. The canvas bNoSmooth is not read for text: 0x800 is always set.
        static int TextPolyFlags(int style)
            => (style == 0 ? 1 : style == 3 ? 4 : style == 4 ? 0x40 : 0) | 0x1000802;

        // ── FUN_10388b60 (Engine.dll 0x10388b60-0x10388d0c): glyph to the device ──
        //
        // Integer clipping by Frame->X/Y (+0xa8/+0xac) and a call to
        // RenDev->DrawTile(Frame, Info, X, Y, UL, VL, U, V, UL, VL, 0, Z, ...).
        // Shipped takes the on-screen width and height from UL/VL (0x10388c6e-0x10388cf4),
        // and the right/bottom cut reduces ONLY UL/VL — W/H are not read
        // after it. PF_Invisible (flag 1) draws nothing.
        void GlyphToDevice(List<HudGlyph> into, UEObject canvas, int flags, int page,
                           int x, int y, int w, int h, int u, int v, int ul, int vl)
        {
            if ((flags & 1) != 0) return;                                   // 0x10388b71
            CanvasFrame fr = FrameOf(canvas);                               // Canvas+0x7c, dereferenced after the flag
            int xw = x + w;
            if (xw <= 0) return;                                            // 0x10388b89
            int yh = y + h;
            if (yh <= 0) return;                                            // 0x10388b9c
            if (x >= fr.X || y >= fr.Y) return;                             // 0x10388ba8/0x10388bb4
            if (w <= 0 || h <= 0) return;                                   // 0x10388bc0/0x10388bc8
            if (x < 0)                                                      // 0x10388bd0-0x10388bf8
            {
                int d = x * ul / w;
                ul += d; u -= d; x = 0; w = xw;
            }
            if (y < 0)                                                      // 0x10388bfc-0x10388c26
            {
                int d = y * vl / h;
                v -= d; vl += d; y = 0; h = yh;
            }
            int roomX = fr.X - x;                                           // 0x10388c2a-0x10388c48
            if (w > roomX) ul += (roomX - w) * ul / w;
            int roomY = fr.Y - y;                                           // 0x10388c4c-0x10388c66
            if (h > roomY) vl += (roomY - h) * vl / h;
            into.Add(new HudGlyph(page, x, y, ul, vl, u, v, ul, vl));
        }

        // ── FUN_10388530 (Engine.dll 0x10388530-0x10388a13): string output ──
        //
        // Its native-font branch (0x10388552-0x103885ac, Viewport->DrawString) is taken by the caller before the
        // metrics are looked up (DrawTextClipped -> NativeDrawString); this body is the bitmap-font path.
        //
        // Shared text helper: called by WrappedPrint (line by line,
        // clip = 0, hotkey = 0; 0x10388113) and execDrawTextClipped (clip = 1,
        // hotkey = bCheckHotKey; 0x1038a4ad). Returns the sum of pen advances
        // (local_2ac). X/Y are RELATIVE to the canvas (without OrgX/OrgY): comparisons with
        // ClipX/ClipY are in these coordinates, Org is added only when
        // passing the glyph to the device (0x10388957/0x10388967).
        //
        // Glyph lookup (0x103885e3-0x103887bd): the character is remapped by CharRemap
        // (miss -> ' '); page = c / CharactersPerPage; no page, no page
        // texture or index beyond Characters.Num — the character is SKIPPED
        // without advancing; USize == 0 for 'a'..'z' — retry with c - 0x20 (already
        // remapped, no second remap).
        //
        // Hotkey (0x1038862b-0x103886f7): '&' followed by a non-'&' draws the
        // NEXT character without advancing and remembers its USize; on the next
        // step '_' is drawn instead of the character with width min(remembered, USize of '_'),
        // and the pen advances by the remembered width. "&" at the end of the string stops output.
        internal int DrawFontText(int flags, UEObject canvas, Hp1FontMetrics m, int x, int y,
                         string text, bool clip, bool hotkey, List<HudGlyph> glyphs)
        {
            char Ch(int k) => k < text.Length ? text[k] : '\0';
            if (Ch(0) == '\0') return 0;                                    // 0x1038853d
            float orgX = CanvasF(canvas, "OrgX"), orgY = CanvasF(canvas, "OrgY");
            float spaceX = CanvasF(canvas, "SpaceX");
            float clipX = CanvasF(canvas, "ClipX"), clipY = CanvasF(canvas, "ClipY");
            int acc = 0;                                                    // [esp+0x1c]
            bool prevAmp = false;                                           // [esp+0x18]
            int savedW = 0;                                                 // [esp+0x28]
            int pos = 0;
            do
            {
                bool amp = false;                                           // [esp+0x34]
                int c = m.Remap(Ch(pos));
                if (hotkey)
                {
                    if (prevAmp) c = m.Remap('_');                          // 0x1038863a-0x10388687
                    if (Ch(pos) == '&')                                     // 0x1038868d
                    {
                        char next = Ch(pos + 1);
                        if (next == '\0') break;                            // 0x1038869b -> 0x103889e7
                        if (next != '&')
                        {
                            amp = true;                                     // 0x103886a9
                            c = m.Remap(next);
                        }
                    }
                }
                while (true)
                {
                    int page = c / m.CharactersPerPage;                     // 0x10388703
                    if (page >= m.Pages.Length) break;                      // 0x10388705 / 0x103887b4
                    Hp1FontPage pg = m.Pages[page];
                    if (!pg.HasTexture) break;                              // 0x1038871c
                    int idx = c - m.CharactersPerPage * page;
                    if (idx >= pg.Characters.Length) break;                 // 0x1038872e
                    Hp1FontChar ch = pg.Characters[idx];
                    if (ch.USize == 0 && c >= 0x61 && c <= 0x7a)            // 0x1038878f-0x1038879e
                    {
                        c -= 0x20;                                          // 0x103887a7
                        continue;
                    }
                    // 0x103887d6-0x1038881a: W = prevAmp ? min(savedW, USize) : USize.
                    int w = prevAmp ? Math.Min(savedW, ch.USize) : ch.USize;
                    int advW = w;                                           // [esp+0x14]
                    int h = ch.VSize, u = ch.StartU, v = ch.StartV;
                    int gx = acc + x, gy = y;
                    bool draw = true;
                    if (clip)                                               // 0x10388824
                    {
                        // Comparisons — x87 over an integer (fild) and a float canvas field.
                        if (gx + w <= 0) draw = false;                      // 0x10388830
                        else if ((float)gx > clipX) draw = false;           // 0x1038884e
                        else if (gy + h <= 0) draw = false;                 // 0x1038885c
                        else if ((float)gy > clipY) draw = false;           // 0x10388872
                        else
                        {
                            if (gx < 0) { u -= gx; w = gx + w; gx = 0; }   // 0x10388880-0x103888a1
                            if (gy < 0) { v -= gy; h = gy + h; gy = 0; }   // 0x103888a5-0x103888c4
                            // Shipped render window is PC24: the difference is rounded to float.
                            if ((float)(gx + w) > clipX)                    // 0x103888c8-0x103888e8
                                w = UELevelRuntime.Ftol((float)((double)clipX - gx));
                            if ((float)(gy + h) > clipY)                    // 0x103888ea-0x1038890f
                                h = UELevelRuntime.Ftol((float)((double)clipY - gy));
                        }
                    }
                    if (draw)                                               // 0x1038891a-0x1038897e
                        GlyphToDevice(glyphs, canvas, flags, page,
                            UELevelRuntime.Ftol((float)((double)gx + orgX)),
                            UELevelRuntime.Ftol((float)((double)gy + orgY)),
                            w, h, u, v, w, h);
                    // 0x10388993-0x103889c4: pen advance.
                    if (prevAmp) advW = savedW;
                    if (!amp) acc += UELevelRuntime.Ftol((float)((double)advW + spaceX));
                    else savedW = ch.USize;
                    prevAmp = amp;
                    break;
                }
                pos++;                                                      // 0x103889cf-0x103889e1
            } while (Ch(pos) != '\0');
            return acc;
        }

        // ── UCanvas::execDrawTextClipped (0x1038a2f0; Engine.dll.c:58026) ──
        //
        // No WrappedPrint and no wrapping: "No font" -> log and return; Large/BigFont
        // with language != INT WRITE Canvas.Font = MedFont (0x1038a3ce — unlike
        // WrappedPrint, where a local parameter is substituted); check(Font)
        // UnCanvas.cpp:0x2e1; flags by Style; the helper with clip = 1 and hotkey =
        // bCheckHotKey from ftol(CurX), ftol(CurY). The pen is not touched.
        public void DrawTextClipped(UEObject canvas, string text, bool checkHotKey)
        {
            object font = canvas.GetProp(PK.Font);
            if (font is null)
            {
                HP1.Core.UETrace.Warn("DrawTextClipped: No font");   // string 0x104d5ed8
                return;
            }
            if (SameFont(font, canvas.GetProp(PK.LargeFont)) || SameFont(font, canvas.GetProp(PK.BigFont)))
            {
                if (!string.Equals(UELocalization.Language, "INT", StringComparison.OrdinalIgnoreCase))
                {
                    font = canvas.GetProp(PK.MedFont);
                    canvas.Props["Font"] = font;
                }
            }
            if (font is null)
                throw new InvalidOperationException("Assertion failed: Font [UnCanvas.cpp:737] (UCanvas::execDrawTextClipped)");
            int style = UEValue.ToInt(canvas.GetProp(PK.Style));
            int flags = TextPolyFlags(style);
            int x = UELevelRuntime.Ftol(CanvasF(canvas, "CurX"));
            int y = UELevelRuntime.Ftol(CanvasF(canvas, "CurY"));
            float orgX = CanvasF(canvas, "OrgX"), orgY = CanvasF(canvas, "OrgY");
            UEVector color = DrawColorOf(canvas);
            bool smooth = !UEValue.ToBool(canvas.GetProp(PK.bNoSmooth));
            string fontPath = FontPathOf(font);
            // FUN_10388530 0x10388552-0x103885ac: a native font (+0x54 != 0) goes to Viewport->DrawString(flags, Font,
            // X, Y, Text, Color) — clip and hotkey are not passed; the pen is not touched.
            if (UENativeFonts.Of(font) is UENativeFont nf)
            {
                NativeDrawString(canvas, flags, style, nf, ref x, ref y, text);
                return;
            }
            var glyphs = new List<HudGlyph>();
            DrawFontText(flags, canvas, _fonts.Get(fontPath), x, y, text, clip: true, hotkey: checkHotKey, glyphs);
            _hud.Add(new HudCommand(HudKind.Text, x + orgX, y + orgY,
                0f, 0f, 0f, 0f, 0f, 0f, null, text, fontPath, color, style, smooth, true,
                glyphs.ToArray(), polyFlags: flags));
        }

        // ── UCanvas::execDrawText (0x10389350; Engine.dll.c:43790) ──
        //
        // Shipped order: "No font" → log and return (0x103893cc); Style ==
        // STY_None → WrappedPrint is NOT called, XL = YL = 0 (0x10389429-0x10389434),
        // i.e. only CR moves the pen. Tail 0x10389465-0x10389498:
        // CurX += XL; CurYL = max(CurYL, YL); with CR — CurY += CurYL (already
        // the new one), CurX = 0, CurYL = 0. There is NO "CurY -= YL" compensation: CurY was
        // already moved by WrappedPrint itself for EVERY line, including the last, and CR
        // moves it once more.
        public void DrawText(UEObject canvas, string text, bool cr)
        {
            object font = canvas.GetProp(PK.Font);
            if (font is null)
            {
                HP1.Core.UETrace.Warn("DrawText: No font");   // shipped log text
                return;
            }
            int style = UEValue.ToInt(canvas.GetProp(PK.Style));
            int xl = 0, yl = 0;
            if (style != 0)                                      // 0x10389429-0x10389434
            {
                bool center = UEValue.ToBool(canvas.GetProp(PK.bCenter));
                WrappedPrint(canvas, style, ref xl, ref yl, font, center, text, emit: true);
            }
            // 0x10389465-0x10389498
            canvas.Props["CurX"] = (float)((double)xl + CanvasF(canvas, "CurX"));
            float curYL = CanvasF(canvas, "CurYL");
            // fld CurYL; fcomp st(1)=YL; test ah, 1 (0x10389471-0x1038947e): YL is taken
            // when CurYL < YL and on NaN ("unordered" sets C0).
            if (!(curYL >= yl)) curYL = yl;
            canvas.Props["CurYL"] = curYL;
            if (cr)
            {
                canvas.Props["CurY"] = curYL + CanvasF(canvas, "CurY");
                canvas.Props["CurX"] = 0f;
                canvas.Props["CurYL"] = 0f;
            }
        }

        // ── UCanvas::execTextSize (0x1038a640; Engine.dll.c:32995-33004) ──
        //
        // Without a font — log "TextSize: No font" and return WITHOUT writing XL/YL
        // (0x1038a6f5-0x1038a730: the out parameters stay as they were; the caller
        // gets false and writes nothing). ClipX/CurX/CurY are saved THROUGH
        // ftol (0x1038a75f-0x1038a77d), then CurX = CurY = 0, ClipX = 32767.0
        // (0x46fffe00), WrappedPrint with STY_None and no centring, and the fields
        // are restored FROM THE INTEGERS (0x1038a7b1-0x1038a7c9) — the fractional part
        // of the pen is lost after TextSize.
        public bool TextSize(UEObject canvas, string text, out float outXL, out float outYL)
        {
            outXL = 0f; outYL = 0f;
            object font = canvas.GetProp(PK.Font);
            if (font is null)
            {
                HP1.Core.UETrace.Warn("TextSize: No font");   // shipped log text
                return false;
            }
            int clipX = UELevelRuntime.Ftol(CanvasF(canvas, "ClipX"));
            int curX = UELevelRuntime.Ftol(CanvasF(canvas, "CurX"));
            int curY = UELevelRuntime.Ftol(CanvasF(canvas, "CurY"));
            canvas.Props["CurX"] = 0f;
            canvas.Props["CurY"] = 0f;
            canvas.Props["ClipX"] = 32767f;
            int xl = 0, yl = 0;
            WrappedPrint(canvas, 0, ref xl, ref yl, font, false, text, emit: false);
            canvas.Props["CurY"] = (float)curY;
            canvas.Props["CurX"] = (float)curX;
            canvas.Props["ClipX"] = (float)clipX;
            outXL = xl; outYL = yl;
            return true;
        }

        // ── UCanvas::execStrLen (0x10389050; Engine.dll.c:47208-47229) ──
        //
        // CurX/CurY are saved through ftol (0x1038911e-0x10389131), zeroed,
        // and WrappedStrLenf (vtbl+0x6c, 0x10388d80) = WrappedPrint(
        // STY_None, XL, YL, Font, Center = 0, Text) is called with the LIVE ClipX — StrLen
        // wraps lines at the current width. Restored from the integers
        // (0x10389161-0x10389173). StrLen has no "No font" check: a null
        // font hits the assert inside WrappedPrint.
        public (float, float) StrLen(UEObject canvas, string text)
        {
            int curX = UELevelRuntime.Ftol(CanvasF(canvas, "CurX"));
            int curY = UELevelRuntime.Ftol(CanvasF(canvas, "CurY"));
            canvas.Props["CurX"] = 0f;
            canvas.Props["CurY"] = 0f;
            int xl = 0, yl = 0;
            WrappedPrint(canvas, 0, ref xl, ref yl, canvas.GetProp(PK.Font), false, text, emit: false);
            canvas.Props["CurY"] = (float)curY;
            canvas.Props["CurX"] = (float)curX;
            return (xl, yl);
        }

        // Glyph measure inside WrappedPrint (0x10387e68-0x10387f53) — NOT the same as
        // in the draw helper: the character is remapped by CharRemap; page/index
        // by CharactersPerPage; a found glyph with USize != 0 is the answer; otherwise (no
        // page, index outside the page, USize == 0) for an ORIGINAL character
        // 'a'..'z' the glyph of (original - 0x20) is taken, again through CharRemap, and its
        // size is accepted as-is. The page texture is not checked here.
        // If a glyph is found with USize == 0 and the character is not lowercase — W = 0, H = VSize.
        static (int U, int V) MeasureGlyph(Hp1FontMetrics m, char ch)
        {
            int w = 0, h = 0;                                                // [ebp-0x18], edi
            if (m.TryChar(m.Remap(ch), out _, out Hp1FontChar g))
            {
                w = g.USize; h = g.VSize;
                if (w != 0) return (w, h);                                   // 0x10387ed0
            }
            if (ch >= 0x61 && ch <= 0x7a)                                    // 0x10387edd-0x10387ee7
            {
                w = 0; h = 0;                                                // 0x10387eef-0x10387ef7
                if (m.TryChar(m.Remap(ch - 0x20), out _, out Hp1FontChar up))
                { w = up.USize; h = up.VSize; }
            }
            return (w, h);
        }

        // ── UWindowsViewport::DrawString (WinDrv), reached from the native-font branches of WrappedPrint
        // (0x10387d8a-0x10387df5) and FUN_10388530 (0x10388552-0x103885ac) ──
        //
        // WrappedPrint: X = ftol(CurX), Y = ftol(CurY), flags | 0x100 with bCenter; after the call XL = ftol(float(X) -
        // CurX), YL = ftol(float(Y) - CurY), CurX = float(X), CurY = float(Y) — the pen moves right AND down by the
        // text box, unlike the bitmap branch. FUN_10388530 (DrawTextClipped): the same call, pen untouched.
        // WinDrv (US 0x1110a0e4-0x1110a20d; RU differs only in font creation, ANSI conversion and an error log):
        // DrawText(DT_CALCRECT|DT_WORDBREAK) in (0, 0, ftol(ClipX), ftol(ClipY)) gives the box w × h; X += w, Y += h;
        // unless flags & 1 (STY_None) the text is drawn in (ftol(X - w + OrgX), ftol(Y - h + OrgY), ftol(X + OrgX),
        // ftol(Y + OrgY)), with 0x100 shifted left by (right - left) / 2 and DT_CENTER; DrawText clips to that box
        // (no DT_NOCLIP). GDI pixels are opaque text colour: the command is PF_Masked with the font's aliased coverage.
        // Layout and glyphs come from the substitute font (UENativeFont, NOT ORIGINAL).
        void NativeDrawString(UEObject canvas, int flags, int style, UENativeFont nf, ref int x, ref int y, string text)
        {
            int nul = text.IndexOf('\0');
            if (nul >= 0) text = text.Substring(0, nul);
            List<string> lines = nf.Lines(text, UELevelRuntime.Ftol(CanvasF(canvas, "ClipX")));
            int w = 0;
            foreach (string l in lines) w = Math.Max(w, nf.Width(l));
            int h = lines.Count * nf.LineHeight;
            x += w; y += h;
            if ((flags & 1) != 0) return;
            float orgX = CanvasF(canvas, "OrgX"), orgY = CanvasF(canvas, "OrgY");
            int left = UELevelRuntime.Ftol((float)((double)(x - w) + orgX)), right = UELevelRuntime.Ftol((float)((double)x + orgX));
            int top = UELevelRuntime.Ftol((float)((double)(y - h) + orgY)), bottom = UELevelRuntime.Ftol((float)((double)y + orgY));
            bool center = (flags & 0x100) != 0;
            if (center) { int d = (right - left) / 2; left -= d; right -= d; }
            var glyphs = new List<HudGlyph>();
            for (int i = 0; i < lines.Count; i++)
            {
                int pen = center ? left + (right - left - nf.Width(lines[i])) / 2 : left;
                int lineTop = top + i * nf.LineHeight;
                foreach (char c in lines[i])
                {
                    UENativeGlyph g = nf.Glyph(c);
                    int gx = pen + g.OffX, gy = lineTop + g.OffY, u = g.U, v = g.V, gw = g.W, gh = g.H;
                    pen += g.Advance;
                    // Clipped to the DrawText box.
                    if (gx < left) { u += left - gx; gw -= left - gx; gx = left; }
                    if (gy < top) { v += top - gy; gh -= top - gy; gy = top; }
                    gw = Math.Min(gw, right - gx); gh = Math.Min(gh, bottom - gy);
                    if (gw > 0 && gh > 0) glyphs.Add(new HudGlyph(0, gx, gy, gw, gh, u, v, gw, gh));
                }
            }
            _hud.Add(new HudCommand(HudKind.Text, left, top, right - left, bottom - top, 0f, 0f, 0f, 0f, null, text,
                nf.Name, DrawColorOf(canvas), style, !UEValue.ToBool(canvas.GetProp(PK.bNoSmooth)), false,
                glyphs.ToArray(), polyFlags: 0x1000802));
        }

        static bool SameFont(object a, object b)
            => a is not null && b is not null
               && (ReferenceEquals(a, b)
                   || string.Equals(FontPathOf(a), FontPathOf(b), StringComparison.OrdinalIgnoreCase));

        // ── UCanvas::WrappedPrint (0x10387c50-0x10388359; Engine.dll.c:157936) ──
        //
        // The native-font branch (0x10387d80-0x10387e08) leaves the pen where Viewport->DrawString moved it (CurX/CurY
        // written back, XL/YL its offsets). xl/yl — shipped's output INT&. emit — whether the caller draws (in shipped
        // this is Style != STY_None, 0x10387ff5).
        internal void WrappedPrint(UEObject canvas, int style, ref int xl, ref int yl,
                          object font, bool center, string text, bool emit)
        {
            // A shipped string is a TCHAR*, ending at the first NUL.
            char Ch(int k) => k < text.Length ? text[k] : '\0';

            // 0x10387c7b-0x10387caa: ClipX < 0 || ClipY < 0 || *Text == 0 →
            // XL = YL = 0 (0x10388337) and return; the pen is not touched. Comparison —
            // fcomp 0.0; test ah, 1: C0 is also set for "unordered", so NaN
            // in ClipX/ClipY also returns.
            if (!(CanvasF(canvas, "ClipX") >= 0f) || !(CanvasF(canvas, "ClipY") >= 0f) || Ch(0) == '\0')
            {
                xl = 0; yl = 0;
                return;
            }
            // 0x10387cb0-0x10387cde: Font == LargeFont || Font == BigFont, and
            // appStricmp(GetLanguage(), "INT") != 0 → Font = MedFont. The language is
            // GLanguage (UELocalization.Language), set by appInit from
            // [Engine.Engine] Language; shipped has int.
            if (SameFont(font, canvas.GetProp(PK.LargeFont)) || SameFont(font, canvas.GetProp(PK.BigFont)))
            {
                if (!string.Equals(UELocalization.Language, "INT", StringComparison.OrdinalIgnoreCase))
                    font = canvas.GetProp(PK.MedFont);
            }
            // 0x10387ce0-0x10387cf3: check(Font), UnCanvas.cpp:356.
            if (font is null)
                throw new InvalidOperationException("Assertion failed: Font [UnCanvas.cpp:356] (UCanvas::WrappedPrint)");
            // 0x10387d80-0x10387e08: a native font (name length +0x54 != 0) goes to Viewport->DrawString.
            if (UENativeFonts.Of(font) is UENativeFont nf)
            {
                float curX = CanvasF(canvas, "CurX"), curY = CanvasF(canvas, "CurY");
                int x = UELevelRuntime.Ftol(curX), y = UELevelRuntime.Ftol(curY);
                NativeDrawString(canvas, TextPolyFlags(style) | (center ? 0x100 : 0), style, nf, ref x, ref y, text);
                xl = UELevelRuntime.Ftol((float)((double)(float)x - curX));
                yl = UELevelRuntime.Ftol((float)((double)(float)y - curY));
                canvas.Props["CurX"] = (float)x;
                canvas.Props["CurY"] = (float)y;
                return;
            }

            float orgX = CanvasF(canvas, "OrgX"), orgY = CanvasF(canvas, "OrgY");
            UEVector color = DrawColorOf(canvas);
            bool smooth = !UEValue.ToBool(canvas.GetProp(PK.bNoSmooth));
            string fontPath = FontPathOf(font);
            int flags = TextPolyFlags(style);                        // 0x10387d54-0x10387d85
            Hp1FontMetrics m = _fonts.Get(fontPath);
            xl = 0; yl = 0;                                          // 0x10387e09-0x10387e17
            float savedCurX = CanvasF(canvas, "CurX");               // 0x10387e1d-0x10387e23
            int pos = 0;
            do
            {
                // 0x10387e26-0x10387e49: line X = bCenter ? 0 : ftol(CurX).
                int x = center ? 0 : UELevelRuntime.Ftol(CanvasF(canvas, "CurX"));
                int cutX = 0, lineH = 0, cutH = 0, cutIdx = 0;
                bool boundary = false;                               // [ebp-0x1c]
                float spaceX = CanvasF(canvas, "SpaceX"), spaceY = CanvasF(canvas, "SpaceY");
                float clipX = CanvasF(canvas, "ClipX");
                int i = 0;
                while (true)
                {
                    char c = Ch(pos + i);
                    if (c == '\0' || c == '\n') break;               // 0x10387e55-0x10387e62
                    var g = MeasureGlyph(m, c);
                    // 0x10387f56-0x10387f6c: fild W; fadd SpaceX; call _ftol — no fstp,
                    // but the register is in the PC24 render window: the sum is rounded to 24 bits BEFORE
                    // truncation (the same step as the pen advance in FUN_10388530
                    // 0x103889ab). Exact via double: |W| < 2^24, the int+float sum
                    // fits in 53 bits.
                    x += UELevelRuntime.Ftol((float)((double)g.U + spaceX));
                    int h = UELevelRuntime.Ftol(spaceY) + g.V;       // 0x10387f67-0x10387f77
                    if (lineH < h) lineH = h;                        // 0x10387f79-0x10387f7d
                    if (!((double)x <= clipX)) break;                // 0x10387f80-0x10387f8b
                    i++;
                    char n = Ch(pos + i);                            // 0x10387f8d-0x10387fa4
                    bool atBoundary = n == ' ' || n == '\n' || n == '\0';
                    // 0x10387fa6-0x10387fad: until a word boundary is met,
                    // the cut point moves on EVERY character — the first
                    // over-long word is cut by characters.
                    if (!atBoundary && boundary) continue;
                    cutX = x; cutIdx = i; cutH = lineH;              // 0x10387fb3-0x10387fc4
                    boundary = boundary || atBoundary;               // 0x10387fc7-0x10387fdc
                }
                // 0x10387fe8-0x10387fef: nothing to cut → return (XL/YL as-is).
                // A '\n' at the start of a line also lands here: shipped does NOT print
                // text after a line feed.
                if (cutIdx == 0) return;
                if (emit)
                {
                    // 0x10388003-0x10388033: a line is output only if it
                    // crosses the frame vertically: OrgY+CurY < Frame->Y and
                    // OrgY+CurY+cutH > 0. The sum is x87 in the render window (PC24),
                    // i.e. rounded to float; Frame->Y is the canvas frame integer.
                    float top = (float)((double)orgY + CanvasF(canvas, "CurY"));
                    if (top < FrameOf(canvas).Y && (float)((double)cutH + top) > 0f)
                    {
                        // 0x10388094-0x103880ca: X = bCenter ? ftol(CurX - cutX/2)
                        // : ftol(CurX) — centred on the PEN, not on ClipX. cutX/2 is
                        // cdq; sub; sar (truncation toward zero); fild; fsubr CurX; _ftol —
                        // the difference in the PC24 register is rounded to 24 bits before truncation.
                        float cx = CanvasF(canvas, "CurX");
                        int drawX = center ? UELevelRuntime.Ftol((float)((double)cx - cutX / 2)) : UELevelRuntime.Ftol(cx);
                        int drawY = UELevelRuntime.Ftol(CanvasF(canvas, "CurY"));   // 0x103880ff
                        int x0 = center ? 0 : UELevelRuntime.Ftol(cx);
                        string line = text.Substring(pos, cutIdx);
                        var glyphs = new List<HudGlyph>();
                        // 0x10388113: line helper, clip = 0, hotkey = 0.
                        int drawn = DrawFontText(flags, canvas, m, drawX, drawY, line,
                            clip: false, hotkey: false, glyphs);
                        // 0x103881a7-0x103881b3: CurX = X + width of the output
                        // (overwritten below at 0x10388291 — kept for ordering).
                        canvas.Props["CurX"] = (float)(drawX + drawn);
                        _hud.Add(new HudCommand(HudKind.Text, drawX + orgX, drawY + orgY,
                            cutX - x0, cutH, 0f, 0f, 0f, 0f, null, line, fontPath,
                            color, style, smooth, false, glyphs.ToArray(), polyFlags: flags));
                    }
                }
                // 0x10388291-0x103882ca: after EVERY line, including the last.
                canvas.Props["CurX"] = center ? savedCurX : 0f;
                canvas.Props["CurY"] = (float)((double)cutH + CanvasF(canvas, "CurY"));
                yl += cutH;
                if (xl < cutX) xl = cutX;
                // 0x103882cc-0x103882e3: skip ALL spaces; continue until NUL.
                pos += cutIdx;
                while (Ch(pos) == ' ') pos++;
            } while (Ch(pos) != '\0');
        }
    }
}
