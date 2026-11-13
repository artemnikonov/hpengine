// The ONE owner of the actor/world lifecycle: actor list and spawn
// order, the level clock, the per-actor tick skeleton (events → state →
// Timer → LifeSpan → physics), spawn/destroy, owner/base, zones. Ported from
// the shipped HP1 Engine.dll decompile (citations inline). Frontends supply
// what is genuinely frontend-specific through the hook fields: BSP queries
// (FindSpot/PointRegion), the animation layer, the physics mode dispatch,
// the collision hash, and the error policy.
// Every frontend (headless smoke included) drives THIS skeleton, so the lifecycle has one owner.
using System;
using System.Collections.Generic;
using System.IO;
using UELib.Core;
using Vector3 = HP1.UEScript.UEVector;
using X87Ext = HP1.Core.X87Ext;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        // ── Actor list (ULevel::Actors) ──
        public readonly List<UEObject> Actors = new();

        // Level paths are WORLD data, like the actor list and BSP: read from the .unr,
        // unrelated to presentation. Core owns them so the game runs on the same data
        // the level data matches an independent parser byte for byte.
        public IReadOnlyList<LevelReachSpecRef> ReachSpecs { get; private set; }
            = System.Array.Empty<LevelReachSpecRef>();

        public void SetReachSpecs(IReadOnlyList<LevelReachSpecRef> reachSpecs)
            => ReachSpecs = reachSpecs
               ?? throw new System.ArgumentNullException(nameof(reachSpecs));

        // Input is Core state for the same reason as the actor list and the hash: every
        // frontend must share ONE key layout, key edges and command execution order.
        // The frontend only supplies UE1 key names.
        public readonly UEInput Input = new();

        // Mutable game config state: `get ini:` and `set ini:` both work on this ONE
        // object, and consumers (music/sound volume) read values from here, not from
        // constants. Loaded together with key bindings (LoadInputBindingsFromInstall),
        // one "read the install" entry point.
        UEConfig _config;
        public UEConfig Config { get { HP1.Core.UECoreOwner.Check("UELevelRuntime.Config"); return _config; } private set => _config = value; }

        // ── Collision world ────────────────────────────────────────────────
        // The BSP model and the FCollisionHash replica are Core state: all
        // frontends must query the SAME world. The orchestration that consumes
        // them (MoveActor, SingleLineCheck, CheckEncroachment) is in the next layer.
        public HeadlessBspModel Bsp;
        public CollisionHashReplica CollisionHash;
        public readonly Dictionary<int, UEObject> ActorsByExport = new();

        // ObjectId -> ACTOR index.
        //
        // The frontend receives the world as a FRAME in which each actor is named by its
        // ObjectId and must find the live Core object by it. ExportIndex does not work:
        // only PLACED actors have one, runtime spawns have no export.
        //
        // The index is maintained in EXACTLY the four places where `Actors` changes
        // (SetActors, spawn, sweep of destroyed actors, world teardown). There is no
        // separate "update index" step, otherwise it would drift from the list.
        public readonly Dictionary<int, UEObject> ActorsById = new();

        // Live actor by ObjectId. A destroyed one is not returned: shipped removes
        // it from Level.Actors in DestroyActor, and the index must answer the same.
        public UEObject FindActorById(int objectId)
            => ActorsById.TryGetValue(objectId, out UEObject a) && a is not null && !a.Destroyed
                ? a : null;

        // NAMES OCCUPIED IN THE MAP PACKAGE — input of the MakeUniqueObjectName loop.
        //
        // Shipped (Core.dll.c:41354-41362) bumps the class counter while
        // StaticFindObject(NULL, Outer, name) finds an object. The Outer of a spawned
        // actor is the level's outer (ULevel::SpawnActor, Engine.dll.c:20124,
        // `*(this+0x18)`), i.e. the map package: it holds ALL .unr exports
        // (all have outer = 0 — measured on Lev2_Quid1, 637/637). StaticFindObject ignores
        // flags (disasm 0x1015ba1d..0x1015ba67: name +0x20, outer +0x18, class) — a
        // destroyed actor's name stays taken while the object is in the hash, i.e. until
        // garbage collection; no GC runs during level play, so names are never freed here.
        // FName comparison is case-insensitive.
        readonly HashSet<string> _occupiedObjectNames = new(StringComparer.OrdinalIgnoreCase);

        internal void SeedPackageObjectNames(IEnumerable<string> names)
        {
            foreach (string n in names)
                if (!string.IsNullOrEmpty(n)) _occupiedObjectNames.Add(n);
        }

        // UObject::MakeUniqueObjectName (Core.dll.c:41313). Base = class name without
        // TRAILING DIGITS (loop over `'0' <= c <= '9'`: Ring1 -> "Ring",
        // Spawn_flash_1 -> "Spawn_flash_"); truncated to 0x3f − number length − 1 chars
        // (appStrncpy), then ClassUnique++ is appended, repeated until the first name
        // not present in the package.
        // The counter is UClass::ClassUnique, SESSION state (interp.ClassUnique):
        // a map change does not reset it, and the Entry world, loaded first, uses up
        // the zero numbers (GameInfo0/Mutator0/GameReplicationInfo0).
        internal string MakeUniqueSpawnName(UEInterpreter interp, UClass cls)
            => MintName(cls.Name, () =>
            {
                int number = interp.ClassUnique.TryGetValue(cls, out int old) ? old : 0;
                interp.ClassUnique[cls] = number + 1;
                return number;
            });

        // The naming rule (Core.dll.c:41313-41362), separate from the counter owner.
        internal string MintName(string className, Func<int> nextNumber)
            => MintObjectName(className, nextNumber, _occupiedObjectNames);

        // Same rule over the name set of ANOTHER outer (Transient package for
        // `new` without Outer — UEInterpreter.NewObject).
        internal static string MintObjectName(string className, Func<int> nextNumber, HashSet<string> occupied)
        {
            int end = className.Length;
            while (end > 0 && className[end - 1] >= '0' && className[end - 1] <= '9') end--;
            string stem = className.Substring(0, end);
            string name;
            do
            {
                string digits = nextNumber().ToString(System.Globalization.CultureInfo.InvariantCulture);
                // appStrncpy(dst, base, 0x3f - len(number)) (0x10161575-0x1016158e) writes 0 at [n-1]
                // (0x101262da): the base is at most 0x3e - len(number) long.
                int keep = Math.Min(stem.Length, 0x3e - digits.Length);
                name = stem.Substring(0, keep) + digits;
            }
            while (!occupied.Add(name));
            return name;
        }

        public void SetActors(IEnumerable<UEObject> actors)
        {
            FirstDeletedCount = 0;   // new world = new Level, FirstDeleted empty
            Actors.Clear();
            Actors.AddRange(actors);
            ActorsByExport.Clear();
            ActorsById.Clear();
            foreach (UEObject actor in Actors)
            {
                int exportIndex = UEInterpreter.ToInt(actor.GetProp(PK.ExportIndex));
                // One actor can occupy two Actors slots (shipped data of five maps,
                // see UEActorGraphLoader) — the export index then maps to the same
                // object, not a conflict.
                if (exportIndex > 0)
                {
                    if (ActorsByExport.TryGetValue(exportIndex, out UEObject prior) && !ReferenceEquals(prior, actor))
                        throw new InvalidDataException(
                            $"ULevel::Actors: two DIFFERENT actors with ExportIndex {exportIndex}");
                    ActorsByExport[exportIndex] = actor;
                }
                if (actor is not null) ActorsById[actor.ObjectId] = actor;
            }
        }

        // Sweep of destroyed actors — ONE place for both lists, so the index never
        // keeps dead actors that the list has already dropped.
        void SweepDestroyed()
        {
            for (int i = Actors.Count - 1; i >= 0; i--)
            {
                UEObject a = Actors[i];
                if (a is not null && !a.Destroyed) continue;
                if (a is not null) ActorsById.Remove(a.ObjectId);
                Actors.RemoveAt(i);
            }
        }

        // ── Frontend hooks ──
        // Journal sink (headless: the pinned journal; otherwise null).
        public Action<string> Journal;
        public bool DetailedJournal;
        // Frontend-supplied journal context, same kind as the two above: some
        // physics journal lines widen for the local player specifically. Core
        // identifies players by CLASS everywhere else (ClassIsA "PlayerPawn"),
        // the shipped idiom — but that is a class test, not an identity test, so
        // it cannot stand in here without changing WHICH lines are emitted.
        // Journal verbosity only, never behaviour.
        public UEObject LocalPlayer;
        // HP1_TRACE_EVENTS diagnostic switch, mirrored from the frontend exactly
        // like DetailedJournal — it only widens the journal, never gameplay.
        public bool TraceEvents;
        // HP1_TRACE_TIES=1: report every equal-time pair MultiLineCheck produces
        // (diagnostic; the ported MsvcQsort decides the order of equal times).
        static readonly bool TraceTies =
            Environment.GetEnvironmentVariable("HP1_TRACE_TIES") == "1";
        // Snapshot of the MultiLineCheck array BEFORE sorting — for the fixture that
        // pins the order of equal times (enabled by flag, off by default).
        public static bool CaptureLineCheckPreSort;
        public static List<MoveHit> LastLineCheckPreSort;
        // WorldFrame producer — owns the AParticleFX::Tick prelude. The render pass
        // is bound when the producer is handed out, not on one of the frame paths:
        // Build is called by both RunEngineFrame and headless BuildWorldFrame.
        UEWorldFrameProducer _frameProducer;
        public UEWorldFrameProducer FrameProducer
        {
            get { HP1.Core.UECoreOwner.Check("UELevelRuntime.FrameProducer"); return _frameProducer; }
            set
            {
                _frameProducer = value;
                // Mesh branch of DrawActorSprite on the direct frame path — a Core body.
                if (value is not null) value.DrawMesh = DrawMeshActor;
                if (value is not null) value.MeshRenderBoundingBox = MeshRenderBoundingBox;
                if (value is not null) value.MeshLodOf = MeshLodOf;
                // Brush tracker (records mover SavedPos/SavedRot) and Client.ParticleDensity.
                if (value is not null) value.UpdateMovingBrush = UpdateBrushTracker;
                if (value is not null) value.ParticleDensityNow = () => ReadClientParticleDensity(Config);
                // Frame occlusion (URender::OccludeBsp): level model, zones, viewer and device flags, Generate.
                if (value is not null) value.OcclusionWorldFor = OcclusionWorld;
                // Texture flag word of a HUD tile (UCanvas::DrawTile) — a world reader.
                if (value is not null) value.TexturePolyFlags = TextureFacts.PolyFlagsOfResource;
            }
        }

        // ── ENGINE FRAME PHASE ───────────────────────────────────────────────
        //
        // UEWorldFrameProducer.Build ports UGameEngine::Draw: it runs the view actor's
        // PreRender/PostRender and Update(0) of DRAWN ParticleFX (render pass RenderLevelPass),
        // and both halves draw from the SHARED MSVCRT stream. Shipped has no frame in which
        // they did not run and no frame in which they ran twice.
        //
        // So the phase has one owner and lives here. The frontend only supplies the frame
        // NUMBER (headless uses the tick number so the journal stays the same) and a label.
        WorldFrame _latestFrame;
        public WorldFrame LatestFrame { get { HP1.Core.UECoreOwner.Check("UELevelRuntime.LatestFrame"); return _latestFrame; } private set => _latestFrame = value; }
        public int EngineFrameCount { get; private set; }

        // Numbers of the frames built outside a game tick (the post-load frame, LoadMap fade, save progress): one strictly
        // decreasing negative sequence, so no two of them and no game tick share a number. The producer cache holds the last
        // frame keyed on its number (a post-load frame and a fade frame of the same tick must not collide); the audio gap
        // detector judges only game ticks (AudioSystem.NoteFrameTick).
        int _insideTickFrameSeq;
        public int NextInsideTickFrameNumber() => --_insideTickFrameSeq;

        // ONLY THE FIRST WORLD IS DRAWN BEFORE ITS FIRST TICK. UGameEngine::Init browses the start
        // map (Engine.dll.c:38312) and only then opens the viewport window (38365); a NEW window ends
        // UWindowsViewport::OpenWindow with Repaint(1) (WinDrv.dll.c:4857, 4927-4929) — one Draw of
        // the start level before its first ULevel::Tick, and its PlayerPawn.PreRender lazily creates
        // myHUD (PlayerPawn.uc:2280), so intro cutscenes that freeze Harry on the first tick
        // (CutScene.uc:849, `baseHud(PlayerHarry.myHUD).bCutSceneMode = true`) find the HUD alive.
        // A later travel or save load has no window to open, and the engine frame that browses
        // returns right after Browse (UEGameEngineNatives.TickWorld): the new level ticks once,
        // THEN draws. Live original: the first frame after a travel or load has
        // TimeSeconds = start + 0.1, the slot 99 save holds 0.1, and Lumos -> Lev2_fire1 shows
        // the health bar on that first frame (myHUD was None on the first tick).
        //
        // The debt is raised by the first world bring-up of this runtime (ArmFirstWindowFrame) and
        // cleared by any RunEngineFrame.
        public bool EngineFrameOwedAfterLoad { get; private set; }
        bool _viewportWindowOpened;
        // The world LatestFrame was built from (its zones, movers, lightmap tiles); null for the held loading screen,
        // which draws no world. A frontend builds a new level's scene from the first frame of THAT level: after a travel
        // or load the browsing tick only holds the screen, the new world's first frame comes after its first tick.
        WorldStatic _latestFrameWorld;
        public WorldStatic LatestFrameWorld { get { HP1.Core.UECoreOwner.Check("UELevelRuntime.LatestFrameWorld"); return _latestFrameWorld; } private set => _latestFrameWorld = value; }
        void ArmFirstWindowFrame() { EngineFrameOwedAfterLoad = !_viewportWindowOpened; _viewportWindowOpened = true; }

        // The browsing engine frame: no Draw; what the screen keeps is LoadMap's loading screen.
        public WorldFrame HoldLoadMapScreen(int tick)
        {
            if (LastLoadMapScreen is not null) { LatestFrame = LastLoadMapScreen; LatestFrameWorld = null; }
            LastLoadMapScreen = null;
            PresentAudioNoDrawNow?.Invoke(tick);   // no Galaxy Update this tick (UELevelRuntime.PresentAudioNoDrawNow)
            return LatestFrame;
        }

        // Static level contract — what the frontend binds ONCE, not every frame.
        // Built where the model is loaded and ONLY from it: the `.unr` is never handed
        // to the frontend or read again. null until a world is loaded.
        public WorldStatic LatestWorldStatic { get; private set; }

        // PRESENTED FRAME — what the frontend reads (frame on a separate thread).
        // Until the frontend takes over publishing (ManualPresent), the presented frame
        // is the last built one. When Core steps on its own thread, the frontend calls
        // Present() in the sync window (Core stopped) and reads an unchanged frame
        // until the next Present() while Core builds a new one.
        public bool ManualPresent;
        WorldFrame _presentedFrame;
        public WorldFrame PresentedFrame => ManualPresent ? _presentedFrame : LatestFrame;
        // Level time (LevelInfo.TimeSeconds), captured in the same window as the frame.
        float _presentedLevelTime;
        public float PresentedLevelTime { get { HP1.Core.UECoreOwner.Check("UELevelRuntime.PresentedLevelTime"); return ManualPresent ? _presentedLevelTime : LiveLevelTime(); } }
        float LiveLevelTime()
        {
            UEObject lvl = Actors.Count > 0 ? Actors[0] : null;
            return lvl is null ? 0f : UEValue.AsFloat(lvl.GetProp(PK.TimeSeconds));
        }
        public void Present() => Present(LatestFrame);

        // Present a SPECIFIC frame (the save-progress frame before the step's frame).
        public void Present(WorldFrame frame)
        {
            _presentedFrame = frame;
            _presentedLevelTime = LiveLevelTime();
        }

        // The single publishing point. Called from LoadWorldGeometry and from world
        // teardown: an artefact of the previous level surviving a map change is exactly
        // the defect class the teardown contract catches.
        // Diagnostics (headless HP1_SURF_LM): the runner loads the model via SetBsp,
        // not LoadWorldGeometry, and does not publish world statics.
        public void PublishWorldStaticForDiagnostics() => PublishWorldStatic(LoadedMapPath);

        void PublishWorldStatic(string mapPath) =>
            LatestWorldStatic = mapPath is null
                ? null
                : new WorldStatic(mapPath, Bsp?.BuildStaticGeometry());

        // viewportDelta — viewport clock delta (UConsole::PreRender ticks the console
        // with it, Engine.dll.c:91467): real time between engine frames, without
        // TimeDilation and pause. The frontend knows it as the frame duration.
        public WorldFrame RunEngineFrame(UEInterpreter interp, int frameNumber, string label)
            => RunEngineFrame(interp, frameNumber, label, 0f);

        // Time spent on per-frame surface lightmaps in the last frame (player fps line).
        public double LastLightmapMs;

        // UGameEngine+0x108: set by UGameEngine::Draw for its duration (0x1039fa8a, cleared at 0x103a001c);
        // a nested Draw and PaintProgress do nothing while it is set (UELevelRuntime.PaintProgress).
        bool _inEngineFrame;

        public WorldFrame RunEngineFrame(UEInterpreter interp, int frameNumber, string label, float viewportDelta)
        {
            if (FrameProducer is null) return LatestFrame;
            FrameProducer.Console = ViewportConsole;
            FrameProducer.ViewportDelta = viewportDelta;
            EngineFrameCount++;
            EngineFrameOwedAfterLoad = false;
            // Device values are supplied HERE, not when the producer is handed out:
            // each frontend assigns FrameProducer itself, and setting this there would
            // give one decision two owners.
            BindFrameProducerHooks(interp);
            _inEngineFrame = true;
            WorldFrame prev = LatestFrame;
            try
            {
                LatestFrame = FrameProducer.Build(frameNumber, label, Actors, interp,
                    LocalPlayer ?? interp?.PlayerHarry);
                LatestFrameWorld = LatestWorldStatic;
            }
            finally { _inEngineFrame = false; }
            // A new frame ran a Galaxy Update; the producer's cached frame (same number) did not.
            if (!ReferenceEquals(LatestFrame, prev)) PresentAudioFrameNow?.Invoke(LatestFrame);
            // Per-frame surface lightmaps are part of the Core frame, not a frontend call
            // from its own Update (the frontend only uploads finished tiles). Headless
            // neither draws nor requests them.
            LastLightmapMs = 0;
            if (ComputeSurfaceLightmapsInFrame)
            {
                long lm0 = System.Diagnostics.Stopwatch.GetTimestamp();
                // The level surface lightmaps (the largest part) build on a worker while the rest runs here: disjoint
                // state, presentation only, no game RNG (BeginSurfaceLightmaps). Joined before the frame is returned.
                BeginSurfaceLightmaps(out Action level);
                System.Threading.Tasks.Task worker = level is null ? null : System.Threading.Tasks.Task.Run(level);
                try
                {
                    if (level is not null) UpdateMoverSurfaceLightmaps();
                    UpdateFogMaps();
                    UpdateActorLights(interp);
                    MoverCoplanarOfTick = MoverCoplanarPieces();
                }
                catch
                {
                    try { worker?.Wait(); } catch (AggregateException) { }
                    throw;
                }
                worker?.GetAwaiter().GetResult();
                VerifySurfaceLightmapsSerial();
                long lmDt = System.Diagnostics.Stopwatch.GetTimestamp() - lm0;
                LastLightmapMs = lmDt * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (ProfileTicks) ProfileLightmapTicks += lmDt;
            }
            UEWorldFrameProducer.AllocNote("lightmaps");
            return LatestFrame;
        }

        // Producer bindings to the world (mesh catalog, alignment, decals, particle
        // queries). Anyone who builds a frame or steps particles outside RunEngineFrame
        // (the original-comparison probe) needs the same set.
        public void BindFrameProducerHooks(UEInterpreter interp)
        {
            FrameProducer.HighDetailActors = HighDetailActors;
            FrameProducer.RenderDeviceCoronas = RenderDeviceCoronas;
            FrameProducer.RenderDeviceVolumetricLighting = RenderDeviceVolumetricLighting;
            FrameProducer.CoronaLevel = CoronaGeometry();
            FrameProducer.CoronaActorsByExport = ActorsByExport;
            FrameProducer.MeshCatalog = MeshCatalog;
            FrameProducer.DecalSnapshot = SnapshotDecals;
            FrameProducer.DrawDecals = DrawFrameDecals;
            FrameProducer.BeginDecalFrame = BeginDecalFrame;
            FrameProducer.AudioRuntime = this;
            BindParticleLineChecks(interp);
        }

        // Level queries for particle bounce (UParticle::Update 93921-93978):
        // FastLineCheck — pure BSP check of the level model; SingleLineCheck — a trace
        // without source with TraceFlags=6 (Movers|Level). Rebound every frame because
        // the interpreter is a frame argument, not a world field.
        UEInterpreter _particleHookInterp;
        void BindParticleLineChecks(UEInterpreter interp)
        {
            if (ReferenceEquals(_particleHookInterp, interp)
                && FrameProducer.ParticleFastLineCheckClear is not null) return;
            _particleHookInterp = interp;
            FrameProducer.PosePoints = EvaluatePosePoints;
            FrameProducer.TotalWind = TotalWind;
            FrameProducer.ParticleFastLineCheckClear = (end, start) =>
                Bsp is null
                    ? throw new InvalidOperationException("particle bounce: the level model has no BSP")
                    : Bsp.FastLineCheckClear(end, start);
            FrameProducer.ParticleSingleLineCheck = (start, end) =>
            {
                LineCheckHit hit = SingleLineCheck(interp, null, null, start, end, Vector3.zero, 6);
                bool world = hit.Actor is not null && !hit.Actor.Destroyed
                    && UEInterpreter.ClassIsA(hit.Actor.Class, "LevelInfo");
                return new UEWorldFrameProducer.ParticleLineHit(world, hit.Location, hit.Normal);
            };
            // Corona ray (FUN_10b26f10): XLevel->SingleLineCheck(Hit, NULL, End = lamp, Start = Frame->Coords.Origin,
            // TraceFlags 6, Extent 0) — nothing hit.
            FrameProducer.CoronaLineClear = (start, end) => !SingleLineCheck(interp, null, null, start, end, Vector3.zero, 6).Blocked;
            FrameProducer.CoronaPawnClear = CoronaPawnClear;
        }

        // NOT ORIGINAL (improvement, coronas hidden by pawns): shipped traces the corona ray with TraceFlags 6 (Level|Movers), so Harry and other pawns between the
        // camera and the lamp do not hide it. Enhanced only (UEWorldFrameProducer.DirectRenderStep): a visible, drawn, opaque pawn
        // whose collision primitive the ray crosses blocks the corona. TraceFlags 1 cannot be used here: with Source
        // NULL shipped maps every non-world actor to bit 0x10 (SingleLineCheckBody), so this is a direct pawn pass over
        // the collision hash. A pawn whose cylinder holds the ray start never blocks (the camera is inside it: first
        // person, or a camera at its view target). Pure query: no touch, no RNG, no writes (the hash visit tag only
        // dedups within one query).
        const int StyleModulated = 4;   // ERenderStyle STY_Modulated
        public bool CoronaPawnClear(Vector3 start, Vector3 end)
        {
            if (CollisionHash is null)
                throw new InvalidOperationException("corona pawn trace: the level has no collision hash");
            foreach (MoveHit hit in ActorLineCheck(start, end, Vector3.zero))
            {
                UEObject p = hit.Actor;
                if (!UEInterpreter.ClassIsA(p.Class, "Pawn") || UEInterpreter.ToBool(p.GetProp(PK.bHidden))) continue;
                // A pawn that is not drawn (DT_None: VenTent2Head, the tentacula's head hit box) hides nothing.
                if (UEValue.ToInt(p.GetProp(PK.DrawType)) == 0) continue;
                // A see-through pawn (Harry under the cloak, Opacity 0.15; the ghosts, STY_Translucent) lets the lamp show
                // through, so its corona stays: blending as the mesh draw resolves it, plus STY_Modulated.
                int style = UEValue.ToInt(p.GetProp(PK.Style));
                float opacity = p.GetProp(PK.Opacity) is { } op ? UEValue.AsFloat(op) : 1f;
                if (MeshTriangleOrder.ActorPolyFlags(style, opacity) != 0 || style == StyleModulated) continue;
                Vector3 d = start - UEInterpreter.AsVector3(p.GetProp(PK.Location));
                float r = UEInterpreter.AsFloat(p.GetProp(PK.CollisionRadius)), h = UEInterpreter.AsFloat(p.GetProp(PK.CollisionHeight));
                if (d.x * d.x + d.y * d.y <= r * r && Math.Abs(d.z) <= h) continue;
                return false;
            }
            return true;
        }

        // Model->Leaves / Model->Lights of the current level for the corona block: the published static geometry, or
        // (runners that load the model with SetBsp and publish no WorldStatic) one built from the model.
        StaticGeometry _coronaGeometry; object _coronaGeometryBsp;
        StaticGeometry CoronaGeometry()
        {
            if (LatestWorldStatic?.Geometry is StaticGeometry g) return g;
            if (Bsp is null) return null;
            if (!ReferenceEquals(_coronaGeometryBsp, Bsp)) { _coronaGeometryBsp = Bsp; _coronaGeometry = Bsp.BuildStaticGeometry(); }
            return _coronaGeometry;
        }

        // GALAXY UPDATE WRITES INTO THE VIEW ACTOR (music). Galaxy writes them itself; in the
        // port Core does it from the audio report (hpengine/Audio/MusicPlayer.Update, same Update, same order).
        //
        // bSongFinished loop (Galaxy.dll.c:7284-7311, disasm 0x10608ecc-0x10608f6a) — EVERY
        // Update, before the music block: a channel whose Sound == +0x784 (the song USound) found ->
        // bSongFinished = "stream exhausted" (0x10608f2c), and if 1 — Transition = MTRAN_Instant,
        // Song = None (0x10608f41 and 7301); no channel -> bSongFinished = 1 (0x10608f6a), Song and
        // Transition untouched. +0x564 is written nowhere else in Galaxy (disasm: only
        // 0x10608f2c and 0x10608f6a). An exhausted one-shot stream still playing under a fade
        // towards a new request erases that request: Lev2_RemChase, live original
        // (hp1ledger, D3D) — Song = JS_Rememberall_Chase_mx with Transition 3 at frame 239,
        // one-shot JS_HogwartsDark_17 ends, Song = None at frame 254, no chase music.
        //
        // Which stream is "the song" is decided by audio (+0x784 is its field, not Core's).
        // +0x784 survives a map change (LoadMap -> SetViewport(same viewport) returns before
        // UnregisterMusic, Galaxy.dll.c:5095-5105).
        public bool ReportMusicFinished(string songPath)
        {
            UEObject player = LocalPlayer;
            if (player is null || player.Destroyed) return false;
            player.Props["bSongFinished"] = true;
            player.Props["Song"] = null;
            player.Props["Transition"] = (byte)1; // MTRAN_Instant
            return true;
        }

        public void ReportSongFinishedFlag(bool finished)
        {
            UEObject player = LocalPlayer;
            if (player is null || player.Destroyed) return;
            player.Props["bSongFinished"] = finished;
        }

        // Start block of Galaxy UpdateMusic (Galaxy.dll.c:7385-7451): at its end `ViewActor+0x55a = 0`
        // (Transition = MTRAN_None, 0x1060948b — the only write of zero). Until then the request
        // lives: the fade follows the current Transition, a repeated ClientSetMusic does not reset
        // the counter. The start block does NOT write bSongFinished — the loop above does in the next Update.
        public void ReportMusicStarted()
        {
            UEObject player = LocalPlayer;
            if (player is null || player.Destroyed) return;
            player.Props["Transition"] = (byte)0;
        }

        // Galaxy SetViewport(viewport) after SetViewport(NULL) (window focus lost/regained):
        // `if (Actor.Song != None && Actor.Transition == 0) Actor.Transition = 1`
        // (Galaxy.dll.c:5222-5225, disasm 0x10606250-0x1060626a). The song is restarted by the music
        // block of the next Update: +0x708 is zeroed by UnregisterMusic on loss.
        public void NoteAudioViewportRestored()
        {
            UEObject player = LocalPlayer;
            if (player is null || player.Destroyed) return;
            if (player.GetProp("Song") is not null && UEInterpreter.AsByte(player.GetProp("Transition")) == 0)
                player.Props["Transition"] = (byte)1;
        }
        // TickAnimation slot (mesh/anim layer).
        public Action<UEInterpreter, UEObject, float> AnimationHook;
        // The state slot is executed by Core itself (ProcessState, port of AActor::ProcessState);
        // per-actor physics is run by PerformPhysics; root motion is executed by Core, the frontend
        // only supplies track DATA (RootTrackSampleHook and MeshCoordsHook in
        // UELevelRuntime.RootMotion.cs); zone and leaf come from Core's own level model
        // (ResolveRegion). The frontend gets no slot inside the actor pass: pose and render state
        // go ONLY through the frame (WorldFrame.ActorSnapshot -> HP1ActorFrameSink, after
        // RunEngineFrame); verify_architecture.py, stage frame-sink, forbids bringing one back.
        // Collision-hash + frontend bookkeeping teardown inside DestroyActor
        // (runs at the shipped hash-removal point, before the Destroyed event).
        public Action<UEObject> DestroyCleanupHook;
        // Final frontend teardown, after the actor is fully dead (bDeleteMe
        // set) — the frontend tears down its proxy here, so EVERY
        // destroy path (the Destroy native, LifeSpan expiry, cascade destroys)
        // tears the proxy down.
        public Action<UEObject> DestroyTeardownHook;
        // The spawn FindSpot probe. A frontend that owns its own world geometry
        // may supply it here and Core defers; currently no frontend sets it.
        // Left null, Core runs its OWN FindSpot against its own Bsp,
        // which is the headless path; null Bsp too means no world is loaded, so
        // no probe runs at all. Fixtures also use this slot to force a
        // deterministic accept/refuse without geometry
        // (stage3_spawn_findspot_refusal_no_side_effects, whose subject is the
        // SpawnActor construction ORDER rather than the probe itself).
        // The post-PostBeginPlay encroachment re-check is Core's own CheckEncroachment
        // over its own collision hash.
        public delegate bool? FindSpotDelegate(Vector3 extent, ref Vector3 spot, bool tryCurrent);
        public FindSpotDelegate SpawnFindSpotHook;
        // The ONLY frontend part of the hash-extent box: reading a UMesh's
        // serialized bound/scale/origin fields out of the .u package. The box
        // FORMULAS and the hash maintenance are Core — this hook is
        // the package layer, not a collision decision.
        public Func<UEResourceRef, Hp1MeshHashFields> MeshHashFieldsHook;
        // Bone lookup for a skeletal mesh: name -> (index, subtree span). Same
        // split as MeshHashFieldsHook — reading the RefSkeleton out of the .u
        // package is the frontend's, the channel decisions built on it are Core's.
        // Index < 0 means "no such bone". NumChildren is the serialized subtree
        // span the shipped range test uses (Engine.dll.c:27809-27874).
        public Func<UEResourceRef, string, (int Index, int NumChildren)> BoneLookupHook;
        // Same shape for a brush actor's own serialized UModel: the package
        // read stays with the frontend, the trace/overlap decisions are Core.
        public Func<UEObject, HeadlessBspModel> BrushModelHook;
        // Frontend visual/collision prep for a fresh spawn — runs after the
        // transform/identity fields are set and the actor joined the list,
        // BEFORE the Spawned event (the frontend attaches its visual
        // proxy here).
        public Action<UEObject> SpawnPrepareHook;
        // Diagnostic describe for the spawn_event journal lines (headless).
        public Func<UFunction, string> DescribeCandidates;
        // Error policy for script event bodies driven from the tick skeleton:
        // default runs directly (an exception is a loud stop); a frontend may
        // install a log-and-continue policy.
        static readonly Action<Action, string> DefaultGuard = (body, what) => body();
        Action<Action, string> _guard = DefaultGuard;
        public Action<Action, string> Guard
        {
            get => _guard;
            // Keep the lifecycle helpers' policy in step: Touch/UnTouch/
            // EncroachedBy are dispatched from inside UELifecycle, so a frontend
            // that installs a log-and-continue policy here must get it there too
            // — otherwise the lifecycle helpers would ignore the declared policy.
            set
            {
                _guard = value ?? DefaultGuard;
                UELifecycle.Guard = _guard;
            }
        }
        // Return-aware variant for a script event whose RESULT drives collision:
        // EncroachingOn's nonzero return is what refuses the move, so a plain
        // Action guard would swallow the decision. Default runs the body
        // directly, exactly like Guard's.
        public Func<Func<bool>, string, bool> GuardResult = (body, what) => body();
        // (Frontends have no EVENT/STATE slot: the tick skeleton runs for EVERY actor,
        //  including the player in Cut* states.)

        void Log(string line) => Journal?.Invoke(line);

        // ── Level clock (ULevel::Tick, Engine.dll.c:262612-262641): raw delta
        // clamped to 0.005..0.1, scaled by TimeDilation, accumulated in 32.32
        // fixed point; TimeSeconds published before actor ticks. ──
        UEObject _clockLevel;
        long _clockFixed;

        public void InitializeLevelClock(UEInterpreter interp)
        {
            UEObject level = interp.GetLevelInfo();
            _clockLevel = level;
            _clockFixed = 0;
            level.Props["TimeSeconds"] = 0f;
            // UGameEngine::LoadMap: GLevel->UpdateTime(Info) (vtable +0xe4,
            // call 0x1039d868) — at level bring-up, before GameInfo/BeginPlay.
            UpdateTime(level);
        }

        // ── ULevel::UpdateTime (Engine.dll 0x103b2400) ─────────────────────
        // appSystemTime(Year +0x3b8, Month +0x3bc, DayOfWeek +0x3c4, Day +0x3c0,
        // Hour +0x3c8, Minute +0x3cc, Second +0x3d0, Millisecond +0x3d4) —
        // GetLocalTime (Core.dll.c:66772..66790), SYSTEMTIME fields as is
        // (wDayOfWeek: 0 = Sunday). Called by LoadMap (0x1039d868) and every
        // ULevel::Tick right after TimeSeconds (0x103b70e4). The clock is a platform
        // boundary: the host supplies the local-time source (default: the process
        // clock, as in shipped).
        public Func<DateTime> LocalTimeSource = () => DateTime.Now;

        internal void UpdateTime(UEObject level)
        {
            if (level is null) return;
            DateTime t = LocalTimeSource();
            level.Props["Year"] = t.Year;
            level.Props["Month"] = t.Month;
            level.Props["DayOfWeek"] = (int)t.DayOfWeek;
            level.Props["Day"] = t.Day;
            level.Props["Hour"] = t.Hour;
            level.Props["Minute"] = t.Minute;
            level.Props["Second"] = t.Second;
            level.Props["Millisecond"] = t.Millisecond;
        }

        // SLOWED FIRST FRAMES AFTER A LOAD (emulates the original's
        // pacing, not an engine body; not original code).
        //
        // Shipped level time advances by real frame time clamped to 0.005..0.1
        // (Engine.dll.c:262612). The original's first frames after LoadMap are slow
        // (texture upload on first display), so by the first cutscene steps the level
        // clock has accumulated a few tenths of a second. Game data depends on it: in
        // Lev_Tut1b/Lev_Tut3 Harry is placed inside Trigger16 (Event=MU1, ReTriggerDelay
        // 0.25), his first step is tick ~7, and Trigger.Touch (Trigger.uc:265) rejects the
        // touch if Level.TimeSeconds < 0.25. A probe of the original (~18 FPS) plays the
        // music; at 60–120 FPS only ~0.18 s accumulate by that tick and the music is lost.
        //
        // Rule: the first LoadStutterTicks ticks after world bring-up get a delta of at
        // least shipped's upper clamp (0.1). Three ticks give 0.3 s at any frame rate.
        // Enabled by a host with real frame pacing; fixed-step headless only with
        // HP1_AS_PLAYER=1, so the pinned fixed-step journal does not shift.
        public const int LoadStutterTicks = 3;
        public bool EmulateLoadStutter { get; internal set; }   // UEGameEngine.SetRealFramePacing
        int _loadStutterTicksLeft;
        internal void ArmLoadStutter() => _loadStutterTicksLeft = LoadStutterTicks;

        public float AdvanceLevelClock(UEInterpreter interp, float realDelta)
        {
            UEInterpreter.InitRunaway();   // GInitRunaway at the start of ULevel::Tick (Engine.dll.c:262584)
            if (_loadStutterTicksLeft > 0)
            {
                _loadStutterTicksLeft--;
                if (EmulateLoadStutter && realDelta < 0.1f) realDelta = 0.1f;
            }
            // ULevel::Tick, disasm 0x103b7062..0x103b70dc: fcom clamp against 0.005f
            // (0x10477bcc; less OR unordered -> 0.005f), then against 0.1f
            // (0x104737fc; not less -> 0.1f); fmul TimeDilation — the product stays in
            // the register; fst into DeltaSeconds (float); fmul 2^32 and MSVCRT _ftol
            // (import 0x1060956c) — TRUNCATION toward zero of the UNrounded product.
            // With TimeDilation = 1 (the only value in the campaign) the product is integral.
            float clamped = realDelta;
            if (!(clamped >= 0.005f)) clamped = 0.005f;
            else if (!(clamped < 0.1f)) clamped = 0.1f;
            UEObject level = interp.GetLevelInfo();
            float dilation = UEInterpreter.AsFloat(level.GetProp(PK.TimeDilation));
            float levelDelta = clamped * dilation;
            const double fixedScale = 4294967296.0;
            if (!ReferenceEquals(_clockLevel, level))
                InitializeLevelClock(interp);
            _clockFixed = checked(_clockFixed + (long)((double)clamped * dilation * fixedScale));
            level.Props["TimeSeconds"] = (float)(_clockFixed / fixedScale);
            UpdateTime(level);   // ULevel::Tick 0x103b70e4, right after TimeSeconds
            return levelDelta;
        }

        // ULevel::TimeSeconds (FTime, XLevel+0xdc) in seconds: shipped reads
        // `(float)(__int64)Time * 2^-32` (CheckEnemyVisible, 13478).
        internal float LevelClockSeconds => (float)(_clockFixed / 4294967296.0);

        // FTime ULevel+0xdc as is (32.32) and its override: loading a save
        // (LoadSavedWorld) and fixtures that need a level at a given clock.
        public long LevelClockFixed => _clockFixed;
        public void RestoreLevelClock(UEObject level, long clockFixed)
        {
            _clockLevel = level;
            _clockFixed = clockFixed;
        }

        // ── One actor pass of ULevel::Tick: re-reads Actors.Count, so actors
        // appended during the tick are reached later in that same tick; dead
        // entries are dropped after the pass.
        // Gating (shipped ULevel::Tick): pause is decided HERE from LevelInfo.Pauser
        // (shipped 262651: non-empty string = paused); the menu writes Pauser through real
        // bytecode (FEBook.OpenBook -> LaunchUWindow -> SetPause(true) under NM_Standalone;
        // CloseUWindow -> SetPause(false)), so there is no second owner of the signal.
        // While paused only bAlwaysTick actors run their full tick (262744, actor flag
        // 0x200000); the shipped paused path also gives the local player
        // ReadInput+PlayerInput for menu navigation — that half lives HERE, in the pass
        // itself, as in shipped (262703-262712, details at the branch below).
        // bPlayersOnly is read from LevelInfo by the pass itself. Shipped ULevel::Tick
        // (Engine.dll.c:262645): bit LevelInfo+0x438 & 4 → TickType = 1, Tick is called on
        // ALL actors; the cut-off is inside AActor::Tick (TickType == 1 check at
        // LAB_103b41e5, Engine.dll.c:128546). XLevel+0x111c / flag2&2 (128340-128349) is
        // not bPlayersOnly (special pause). The port's filter (bAlwaysTick and PlayerPawn
        // only) is NOT shipped: it stays behind a loud warning until AActor::Tick is
        // disassembled; the only source of the flag is a console cheat command. ──
        bool _playersOnlyWarned;

        // TICK PROFILE: TickActor time per actor class and frame build time; enabled by
        // the host (HP1_TICK_PROF=1 in headless). Zero by default — zero cost (one if per actor).
        public static bool ProfileTicks;
        public static readonly Dictionary<string, (long ticks, int calls)> ProfileByClass = new(StringComparer.Ordinal);
        public static long ProfileActorsTicks, ProfileFrameTicks, ProfileProjectTicks, ProfileLightmapTicks;   // Project — the ProjectWorld share inside Frame

        readonly HashSet<UEObject> _passVisiting = new();

        // ── ULevel::Ticked (ULevel+0x10c) and AActor.bTicked (AActor+0x28 bit 11,
        // `var transient const bool bTicked`, Engine.u) ───────────────────────────
        // The once-per-tick guard of the original is a parity bit, not a per-pass set:
        //   AActor::Tick 0x103b387c..0x103b3893: after Owner->Tick, `(flags>>11)&1 ==
        //     XLevel->Ticked` -> return 1 (0x103b430c) — the actor does not tick;
        //   0x103b393c..0x103b395e: bTicked = Ticked, then the body;
        //   ULevel::Tick tail 0x103b74cc..0x103b74db: Ticked = !Ticked (every path);
        //   SpawnActor (Engine.dll.c:20191-20193): bTicked = !Ticked (ticks this pass).
        // Ticked is not serialized (ULevel::Serialize) and no constructor/LoadMap writes
        // it: a new level starts at 0, and bTicked is transient, so every PLACED actor
        // starts at 0 too — the FIRST ULevel::Tick after LoadMap skips all of them
        // (live original: positions and CutScene nCurAction
        // unchanged after tick 0); actors spawned during load (bTicked = 1) do tick.
        public bool LevelTicked { get; private set; }
        static readonly object BoxFalse = false, BoxTrue = true;

        // AActor::Tick head (0x103b3840..0x103b395e): Owner->Tick first, then the
        // bTicked == ULevel::Ticked guard, then bTicked = Ticked and the body.
        readonly HashSet<UEObject> _ownersOutsideWarned = new();

        // The simulated pawn branch (Pawn && Role == ROLE_SimulatedProxy,
        // disasm 0x103b3d87..0x103b3f80: trace under the feet, zone gravity,
        // moveSmooth(Velocity·dt), Tick event) is NOT PORTED — STOP. Such a pawn ticks
        // through the common branch; the divergence is reported loudly, once per actor.
        readonly HashSet<UEObject> _simulatedPawnWarned = new();
        // One destroy delegate per interpreter instead of a closure per call (allocation only; called synchronously).
        UEInterpreter _fxDestroyInterp;
        Action<UEObject> _fxDestroy;
        // Measurement taps: AParticleFX::Tick's prelude and
        // AParticleFX::InitExecution, observed at entry and exit; null in production.
        public static Action<UELevelRuntime, UEObject, float, bool> ParticleTickTap;
        public static Action<UEObject, bool> ParticleInitTap;

        // ── ULevel::DestroyActor (Engine.dll.c:252940). Every event below runs
        // on a LIVE actor — the script-visible bDeleteMe (our Destroyed flag)
        // flips only at the END (253087); the bDeleteMe re-check after each
        // event handles a recursive Destroy completing first. ──
        // Length of the Level->FirstDeleted list (ULevel+0x100): actors destroyed since
        // the last CleanupDestroyed.
        public int FirstDeletedCount { get; private set; }

        // ── ULevel::SetActorZone (vtable 0xb0, Engine.dll.c FUN_103acdd0): the
        // full shipped lifecycle. On a zone change (old Region.Zone != new):
        // with !bTest the OLD zone gets ActorLeaving and the actor gets
        // ZoneChange(new) BEFORE the Region write, the NEW zone gets
        // ActorEntered after it (256399-256414). Same zone → silent iLeaf/
        // ZoneNumber refresh (256393). A Pawn then re-derives FootRegion
        // (Location - (0,0,CollisionHeight)) and HeadRegion (Location +
        // (0,0,EyeHeight)) with FootZoneChange/HeadZoneChange fired BEFORE
        // each write (256416-256464), and mirrors Region.Zone into
        // PlayerReplicationInfo.PlayerZone (256465-256468; the NetMode check is
        // always true standalone). Water/pain/destructive behaviour lives in
        // the SCRIPT bodies these events reach via the VM — not here. ──
        // Test probe: (actor, the location SetActorZone was given, whether the actor is linked in the collision hash).
        internal Action<UEObject, Vector3, bool> SetActorZoneProbe;

        // ── The walking/falling GEOMETRY hooks (the physics state machines
        // below are Core and identical on every frontend). WalkMoveHook commits
        // a swept move of `delta` (rotation
        // unchanged) and reports the shipped FCheckResult essentials.
        // LineTraceHook is the NON-committing ULevel::Trace/SingleLineCheck
        // (vtable +0xc4): start→end with an explicit extent and the shipped
        // ETraceFlags (Pawns=1, Movers=2, Level=4, ...) — unpadded, unlike
        // the MoveActor sweep; a world/BSP hit reports the LevelInfo actor.
        // FarMoveHook is ULevel::FarMoveActor (vtable +0x90, bNoCheck=0) —
        // the walking ledge-stop restores OldLocation through it (136406).
        public delegate (float Fraction, Vector3 Normal, UEObject HitActor) WalkSweepDelegate(
            UEInterpreter interp, UEObject pawn, Vector3 delta);
        public WalkSweepDelegate WalkMoveHook;
        // Returns the full LineCheckHit (Location/Node too): the physWalking trace writes
        // the call's shared FCheckResult entirely (see WalkTrace). The tuple
        // (Blocked, Time, Normal, Actor) converts into it implicitly — Location zero,
        // Node −1.
        public delegate LineCheckHit
            LineTraceDelegate(UEInterpreter interp, UEObject actor, Vector3 start, Vector3 end,
                Vector3 extent, int traceFlags);
        public LineTraceDelegate LineTraceHook;
        public Func<UEInterpreter, UEObject, Vector3, bool> FarMoveHook;

        // Diagnostic gates for the walking trace lines (headless: TraceMovesNow).
        public Func<bool> TraceMovesHook;

        // Measurement tap: PerformPhysics entry (true) and exit
        // (false) of one actor. Observes only; null in production.
        public static Action<UELevelRuntime, UEObject, int, float, bool> PerformPhysicsTap;

        // ── UInput::ReadInput(-1.0) (Engine.dll.c:52558-52613): the input
        // layer walks the viewport actor's properties once (cached per class),
        // collecting the CPF_Input entries (+0x3c bit 4 = 0x4), and on the
        // -1.0 reset multiplies every FLOAT one by zero (52607-52608 with
        // fVar2=0). Only floats — the property's class chain is tested
        // against UFloatProperty (52601-52605); input BYTES/BOOLS survive.
        // Shipped calls this right after the PlayerTick event (128696). ──
        static readonly Dictionary<UClass, List<string>> _inputFloatProps = new(UERefComparer<UClass>.Instance);

        // The shared MSVCRT stream (shipped appFrand) for the physics bodies
        // that consume RNG (processLanded's Carcass slide, 34122). Frontends
        // wire it to the ONE global stream; a reached consumer without the
        // hook is a loud modelling error, never a silent skip.
        // The VALUE is st0 of appFrand(): rand()·C in a 64-bit mantissa, NOT rounded
        // to float. Shipped gameplay bodies (PC64) compare it with `fcomp` and build
        // 2f−1 from it before any memory store; only a consumer whose disasm has
        // `fstp dword` rounds it.
        public Func<HP1.Core.X87Ext> FrandHook;

        // ── Collision hash maintenance ─────────────────────────────────────
        // The hash STATE is Core state; the MAINTENANCE follows it
        // here, so the shipped call sites (SpawnActor, DestroyActor,
        // MoveActor, FarMoveActor, SetCollision) drive Core code directly
        // instead of calling out to a frontend delegate and back. The handle
        // maps are the replica's actor↔handle identity — they belong to the
        // same owner as the replica itself.
        readonly Dictionary<UEObject, int> _hashHandle = new Dictionary<UEObject, int>();
        readonly Dictionary<int, UEObject> _hashActor = new Dictionary<int, UEObject>();
        readonly Dictionary<int, Vector3> _hashColLocation = new Dictionary<int, Vector3>();
        int _nextHashHandle;

        // Serialized UPrimitive bounds (FBox + FSphere) per Model export, read
        // from the .unr by the frontend's package layer — the data source for
        // the brush extent box and the brush trace gates.
        public Dictionary<int, BrushBounds> BrushBoundsByExport = new Dictionary<int, BrushBounds>();

        string _brushMapPath;
        readonly Dictionary<int, HeadlessBspModel> _brushModels =
            new Dictionary<int, HeadlessBspModel>();

        // The whole collision world in ONE call, in the order the parts depend
        // on each other. A frontend must not re-derive the steps and their order:
        // skipping them leaves a null Bsp and an InitCollisionHash that throws on
        // the first brush or mesh actor.
        //
        // Actor population stays with the frontend (each builds actors its own
        // way); call this BEFORE it, then InitCollisionHash() after.
        // Diagnostic only: the frontend's rendering of a function's native
        // candidates for the lifecycle journal. Never affects what runs.
        public Func<UFunction, string> LifecycleCandidatesHook;

        // The level bound by the LOAD OF THIS world. Written next to the binding itself
        // (UEActorGraphLoader) so "bound" and "owned" cannot diverge; read only by teardown.
        UEObject _boundLevel;

        // And WHICH interpreter holds the binding: teardown must know not only WHAT to
        // release but from WHOM; releasing via a foreign interpreter would waste the token
        // and leave the real owner bound.
        UEInterpreter _boundInterp;

        // The whole world in one call: geometry, brush source, actor graph. Either
        // all of it is loaded from the SAME map, or none of it is.
        //
        // The pieces were reachable individually, and that is exactly how a
        // frontend ends up with one map's BSP next to another's actors — each
        // step looked fine on its own. Actor population stays the caller's (it
        // decides what to do with the entries); InitCollisionHash comes after.
        // Level package name = the Outer of its actors (UObject::GetPathName). Shipped
        // LoadMap loads the package by URL.Map, and the package name is the base of the file
        // name built from the URL (case as in the URL). A travel request puts it into
        // _pendingLevelPackage (TryTakeLevelTravel) and the load takes it; without a request
        // (first map, Entry world, fixtures) — the file name base.
        public string LevelPackageName { get; internal set; }
        string _pendingLevelPackage;

        UEMeshBounds _meshBounds;

        readonly Dictionary<UEObject, List<AnimChannelEntry>> _animChannels =
            new Dictionary<UEObject, List<AnimChannelEntry>>();

        // Texture object the native hands to script. The cache is not an optimisation:
        // shipped returns THE SAME UTexture for one texture, and a fresh object per step
        // would break reference comparison in script.
        private readonly Dictionary<string, UEObject> _traceTextureObjects =
            new(UEPropNameComparer.Instance);

        // The single texture-data reader for all frontends.
        public UETextureFactsReader TextureFacts { get; } = new UETextureFactsReader();

        // ── ULevel::MultiLineCheck (thunk 72338 -> body FUN_103ac620, 255981) ──
        // THE query every sweep and trace goes through. Ported whole, because the
        // parts are not separable: the world hit SHORTENS the actor ray, that
        // shortening rescales every actor time back to the full span, and the
        // mover/world coincident-face dedup only exists inside that composition.
        //
        //   checkActors — shipped param_11. MoveActor passes 0x1dc & 1
        //                 (bCollideActors); SingleLineCheck passes flags & 0x17.
        //                 When false, ONLY Mover hits survive the walk.
        //   levelInfo   — shipped param_12: non-null runs the world phase and
        //                 becomes the world hit's Actor. The world hit is NOT a
        //                 special "Actor == null" case: shipped puts it in the
        //                 same list and recognises it later as Actor == Level
        //                 (0x7c, 123958), whose block flag is bCollideWorld —
        //                 exactly IsBlockedBy's world leg.
        //
        // Blocking is deliberately NOT decided here: shipped leaves it to the
        // caller, which tests IsBlockedBy against the MOVING actor.
        //
        // Shipped execTrace COPIES FCheckResult.Location (156122-156125) instead
        // of rebuilding it from the fraction, and the two genuinely differ:
        // ActorBoxTrace.LineCheck sets `Location = ray(Time) - Normal*(Normal|Extent)`
        // (UEBspCollision), so ANY non-zero-extent box trace lands off the
        // ray by the support distance (they coincide only for a ZERO extent against a
        // cylinder). Pinned end-to-end by trace_copies_result_location_box_extent.
        // Execution counter of the coincident-face branch (fixture --moverspot).
        public int CoincidentFaceHits;

        // DIAGNOSTIC CAPTURE OF TRACE QUERIES. Needed by exactly one consumer — the
        // comparison with the shipped body under unicorn:
        // the oracle must be fed THE SAME trace answers and the port must present the query
        // SEQUENCE, otherwise only the result would be compared and a wrongly built probe
        // would pass unnoticed (check the order, not just the return). Always null in production.
        public Action<Vector3, Vector3, Vector3, int, float> LineCheckProbe;
        // Measurement tap:
        // the query and the port's answer of every SingleLineCheck. Observes only; null in production.
        public static Action<UELevelRuntime, UEObject, UEObject, Vector3, Vector3, Vector3, int, LineCheckHit> SingleLineCheckTap;
    }
}
