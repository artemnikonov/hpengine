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
        // the gate compares byte-for-byte with an independent parser.
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
        public UEConfig Config { get; private set; }

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
        // Journal sink (headless: the SHA-pinned journal; Unity: null).
        public Action<string> Journal;
        public bool DetailedJournal;
        // Frontend-supplied journal context, same kind as the two above: some
        // physics journal lines widen for the local player specifically. Core
        // identifies players by CLASS everywhere else (ClassIsA "PlayerPawn"),
        // the shipped idiom — but that is a class test, not an identity test, so
        // it cannot stand in here without changing WHICH lines are emitted.
        // Journal verbosity only, never behaviour.
        public UEObject LocalPlayer;
        // HP1_TRACE_EVENTS diagnostic gate, mirrored from the frontend exactly
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
            get => _frameProducer;
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
        // NUMBER (headless uses the gate tick so the journal stays the same) and a label.
        public WorldFrame LatestFrame { get; private set; }
        public int EngineFrameCount { get; private set; }

        // Numbers of the frames built outside the gate tick (the post-load frame, LoadMap fade, save progress): one strictly
        // decreasing negative sequence, so no two of them and no gate tick share a number. The producer cache holds the last
        // frame keyed on its number (a post-load frame and a fade frame of the same tick must not collide); the audio gap
        // detector judges only gate ticks (AudioSystem.NoteFrameTick).
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
        // THEN draws. Live original 2026-11-10: the first frame after a travel or load has
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
        public WorldStatic LatestFrameWorld { get; private set; }
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
        public float PresentedLevelTime => ManualPresent ? _presentedLevelTime : LiveLevelTime();
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

        // NOT ORIGINAL, decided: improvement (author 2026-11-08, audit/DEFERRED_DEBT_2026-10-04.md "Coronas hidden by
        // pawns"): shipped traces the corona ray with TraceFlags 6 (Level|Movers), so Harry and other pawns between the
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
        // port Core does it from the audio report (openhp1/Audio/MusicPlayer.Update, same Update, same order).
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
        // block of the next Update: +0x708 was zeroed by UnregisterMusic on loss.
        public void NoteAudioViewportRestored()
        {
            UEObject player = LocalPlayer;
            if (player is null || player.Destroyed) return;
            if (player.GetProp("Song") is not null && UEInterpreter.AsByte(player.GetProp("Transition")) == 0)
                player.Props["Transition"] = (byte)1;
        }
        // TickAnimation slot (headless mesh/anim layer; Unity skinner bridge).
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
        // set) — the Unity frontend destroys the GameObject here, so EVERY
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
        // BEFORE the Spawned event (the Unity frontend clones its visual
        // template and attaches the collision capsule here).
        public Action<UEObject> SpawnPrepareHook;
        // Diagnostic describe for the spawn_event journal lines (headless).
        public Func<UFunction, string> DescribeCandidates;
        // Error policy for script event bodies driven from the tick skeleton:
        // default runs directly (headless: an exception is a loud stop); the
        // Unity frontend logs-and-continues.
        static readonly Action<Action, string> DefaultGuard = (body, what) => body();
        Action<Action, string> _guard = DefaultGuard;
        public Action<Action, string> Guard
        {
            get => _guard;
            // Keep the lifecycle helpers' policy in step: Touch/UnTouch/
            // EncroachedBy are dispatched from inside UELifecycle, so a frontend
            // that installs a log-and-continue policy here must get it there too
            // — otherwise connecting the Core collision orchestrator would make
            // Unity stop honouring its own declared policy.
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

        // SLOWED FIRST FRAMES AFTER A LOAD (author decision; emulates the original's
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
        // HP1_AS_PLAYER=1, so gate pins do not shift.
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
        // not bPlayersOnly (special pause). The port's gate (bAlwaysTick and PlayerPawn
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
        // (live original, audit/captures_2026-10-30c: positions and CutScene nCurAction
        // unchanged after tick 0); actors spawned during load (bTicked = 1) do tick.
        public bool LevelTicked { get; private set; }
        static readonly object BoxFalse = false, BoxTrue = true;
        public void TickActorsPass(UEInterpreter interp, float levelDelta,
            Action<Exception> exceptionSink = null)
        {
            long passStart = ProfileTicks ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            UEWorldFrameProducer.AllocNote("between");
            // Pause comes from LevelInfo.Pauser, as in shipped (262651), and only from here:
            // there is no parameter, because a parameter would be a second owner of the decision.
            bool paused = !string.IsNullOrEmpty(
                interp.GetLevelInfo()?.GetProp(PK.Pauser)?.ToString());
            bool playersOnly = UEInterpreter.ToBool(interp.GetLevelInfo()?.GetProp(PK.bPlayersOnly));
            // The bPlayersOnly gate below is the port's model, not a shipped contract:
            // shipped only turns the flag into TickType = 1 (Engine.dll.c:262645) and cuts off
            // inside AActor::Tick (LAB_103b41e5, :128546); what runs before that point with
            // TickType == 1 needs the disassembly. The only source is a cheat command; if the
            // flag comes alive, say so loudly.
            if (playersOnly && !_playersOnlyWarned)
            {
                _playersOnlyWarned = true;
                HP1.Core.UETrace.Warn("[Core] bPlayersOnly ACTIVE: per-actor gate is the "
                    + "SE model (PlayerPawn||bAlwaysTick); the shipped bit identity "
                    + "(XLevel+0x111c / flag2&2) is UNRESOLVED (§8)");
            }
            // Pass sets are reused: a fresh HashSet per tick regrew to ~2k actors
            // (array garbage on every growth).
            // The game tick is outside the render window (appEnableFastMath(0)); reset in case
            // an exception cut the previous frame short before URender::PostRender.
            UEWorldFrameProducer.FastMathWindow = false;
            var visiting = _passVisiting; visiting.Clear();
            // FATAL CONSEQUENCES ARE THE SAME FOR BOTH ERROR SLOTS: the paused-input slot and
            // the full tick share one policy (including SweepDestroyed, so an actor destroyed
            // before the exception does not stay in the list). The selftest throw case proves
            // the shared consequence runs.
            void FatalStop(Exception e)
            {
                exceptionSink(e);
                visiting.Clear();
                SweepDestroyed();
            }
            // The pass starts at iFirstDynamicActor (ULevel+0x110, Engine.dll.c:
            // 262665 and paused branch 262700): static actors that LoadMap gathered at the
            // head of the array (SortActorsForPlay) do not tick.
            for (int i = Math.Min(FirstDynamicActor, Actors.Count); i < Actors.Count; i++)
            {
                UEObject actor = Actors[i];
                if (actor is null || actor.Destroyed) continue;
                if (paused)
                {
                    // ── SHIPPED PAUSED-INPUT (Engine.dll.c:262703-262745) ────
                    //
                    // With a non-empty Pauser the local PlayerPawn (IsA(APlayerPawn)
                    // and Player != null, +0x4bc) STILL gets:
                    //   Player->ReadInput(dt)  (vtable+0x58 -> UViewport::ReadInput: UpdateInput(0), UInput::ReadInput),
                    //   then FindFunctionChecked(PlayerInput) + a direct call,
                    //   then a reset of ALL CPF_Input floats (ReadInput(-1)) —
                    // and NOTHING else: no PlayerTick, no state frame.
                    //
                    // The third step is resolved by disasm 0x103b72de-0x103b7385 (the decompile
                    // alone is not enough): class and parent fields -> UFloatProperty ->
                    // PropertyFlags & CPF_Input -> write 0.0f at the offset — exactly
                    // ReadInput(-1)/ResetPlayerInputAxes, a metadata reset of the axes.
                    //
                    // DIRECT DISPATCH: between ReadInput and the PlayerInput call the shipped
                    // fragment has NO check of its own (FindFunctionChecked + vtable call). The
                    // only gate is IsProbing inside the call, and PlayerInput is outside the
                    // probe range 0xc5..0x104: script Disable('PlayerInput') is a no-op
                    // (execDisable), nothing can suppress the event.
                    //
                    // Input is read here in the paused branch and at the player's slot of the actor tick otherwise —
                    // never by a frontend.
                    if (UEInterpreter.ClassIsA(actor.Class, "PlayerPawn")
                        && actor.GetProp(PK.Player) is not null)
                    {
                        UEObject pausedPawn = actor;
                        void PausedInput()
                        {
                            Input.ViewportReadInput(interp, ViewportConsole, pausedPawn, levelDelta);
                            UFunction inputFn = interp.FindEventFunction(pausedPawn, "PlayerInput");
                            // Direct call, as in the disasm: NO gate between
                            // ReadInput and PlayerInput.
                            if (inputFn is not null) interp.Invoke(pausedPawn, inputFn, levelDelta);
                            ResetPlayerInputAxes(pausedPawn);
                        }
                        if (exceptionSink is not null)
                        {
                            try { PausedInput(); }
                            catch (Exception e) { FatalStop(e); return; }
                        }
                        else PausedInput();
                    }
                    // The full tick is ONLY bAlwaysTick (262743-262745), and for that
                    // same actor it runs AFTER paused-input, as in shipped.
                    if (!UEInterpreter.ToBool(actor.GetProp(PK.bAlwaysTick))) continue;
                }
                else if (playersOnly
                    && !UEInterpreter.ToBool(actor.GetProp(PK.bAlwaysTick))
                    && !UEInterpreter.ClassIsA(actor.Class, "PlayerPawn")) continue;
                long t0 = ProfileTicks ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                if (exceptionSink is not null)
                {
                    // IMMEDIATE STOP: the policy must match headless, where such an
                    // exception ends the run, so the pass is aborted on the spot instead of
                    // letting later actors keep changing a world already declared stopped.
                    try { TickActor(interp, actor, levelDelta, visiting); }
                    catch (Exception e) { FatalStop(e); return; }
                }
                else TickActor(interp, actor, levelDelta, visiting);
                if (ProfileTicks)
                {
                    string cls = actor.Class?.Name?.ToString() ?? "?";
                    long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                    ProfileByClass[cls] = ProfileByClass.TryGetValue(cls, out var acc) ? (acc.ticks + dt, acc.calls + 1) : (dt, 1);
                }
            }
            // Finished transient animation channels are removed by the owner's full ApplyAnim
            // (UELevelRuntime.ApplyAnim; drawing — GetFrame), not by the tick pass.
            // Tail of ULevel::Tick (FUN_103b74cc, 0x103b74cc..0x103b7536):
            // Ticked = !Ticked (0x103b74cc..0x103b74db), before CleanupDestroyed(0) — CompactActors (SweepDestroyed), then if
            // FirstDeleted > 127 (Engine.dll.c:23760 `0x7f < count`) references of live
            // actors to destroyed ones are nulled and the list is freed.
            LevelTicked = !LevelTicked;
            SweepDestroyed();
            if (FirstDeletedCount > 0x7f)
            {
                CleanupDestroyed();
                FirstDeletedCount = 0;
            }
            if (ProfileTicks) ProfileActorsTicks += System.Diagnostics.Stopwatch.GetTimestamp() - passStart;
            UEWorldFrameProducer.AllocNote("actors");
            // ── PlayerPawn.ViewFlash — HERE, BECAUSE SHIPPED CALLS IT HERE ──
            //
            // The call site was read from the image. The eventViewFlash wrapper
            // (0x103242c0) is indeed called by nobody — the compiler INLINED it. The real
            // call is visible only through the FName global reference:
            //   Engine.dll 0x103a1946  mov  eax, [0x105e9d34]   ; FName ViewFlash
            //              0x103a194b  push eax
            //              0x103a194c  mov  ecx, esi            ; Viewports(0)->Actor
            //              0x103a194e  call [FindFunctionChecked] (Core.dll)
            // with parameter `fld [ebp+8]` (0x103a1931) — the function's own DeltaSeconds.
            // The function is 0x103a0900 = UGameEngine::Tick(float) (Engine.dll.c:109914
            // calls it from the UGameEngine::Tick stub), and ViewFlash is the ONLY inlined
            // event dispatch in its whole body (0x103a0900..0x103a1d60, 5216 bytes).
            // 0x105e9d34 is ViewFlash per the name registrar: string 0x104d6654 = "ViewFlash"
            // (neighbours 0x104d6644 "DoJump", 0x104d666c "Mount").
            //
            // ORDER: the ULevel::Tick calls (slot 0x60) are at 0x103a0b0f..0x103a0b54, i.e.
            // BEFORE 0x103a194e. So the event runs AFTER the full actor pass — here, not inside
            // the player tick — exactly once per engine frame and only on the viewport player.
            //
            // DELTA: shipped passes ITS OWN DeltaSeconds parameter, the same one that goes to
            // ULevel::Tick; ULevel applies time dilation internally, to actors. Here it is
            // `levelDelta` — the same argument.
            //
            // The REAL event bytecode runs (no hand copy of the formula).
            if (LocalPlayer is UEObject vfPlayer && !vfPlayer.Destroyed)
            {
                UFunction viewFlashFn = interp.FindEventFunction(vfPlayer, "ViewFlash");
                if (viewFlashFn is not null)
                {
                    if (exceptionSink is not null)
                    {
                        try { interp.Invoke(vfPlayer, viewFlashFn, levelDelta); }
                        catch (Exception e) { FatalStop(e); return; }
                    }
                    else interp.Invoke(vfPlayer, viewFlashFn, levelDelta);
                }
            }
        }

        // AActor::Tick head (0x103b3840..0x103b395e): Owner->Tick first, then the
        // bTicked == ULevel::Ticked guard, then bTicked = Ticked and the body.
        readonly HashSet<UEObject> _ownersOutsideWarned = new();

        public void TickActor(UEInterpreter interp, UEObject actor, float dt,
            HashSet<UEObject> visiting)
        {
            if (actor is null || actor.Destroyed) return;
            if (!visiting.Add(actor))
                throw new InvalidDataException("Owner cycle at " + actor.GetProp(PK.Name));
            if (actor.GetProp(PK.Owner) is UEObject owner && !owner.Destroyed)
            {
                // Shipped AActor::Tick calls Owner->Tick() DIRECTLY
                // (Engine.dll.c:128329-128331) — no array-membership test; the
                // owner's own bTicked flag prevents a double tick. An owner
                // outside ULevel.Actors still ticks here exactly like shipped,
                // but is a modelling gap worth one loud line (headless never hits this).
                // Membership through ActorsById (kept with Actors in the same four places) instead of a linear
                // Actors scan per owned actor per tick; the test only gates this diagnostic line.
                if (!(ActorsById.TryGetValue(owner.ObjectId, out UEObject listed) && ReferenceEquals(listed, owner))
                    && _ownersOutsideWarned.Add(owner))
                    HP1.Core.UETrace.Warn(
                        $"[Core] owner outside ULevel.Actors ticks owner-first "
                        + $"(shipped 128329): owner class={owner.Class?.Name} "
                        + $"of actor={actor.GetProp(PK.Name)}");
                TickActor(interp, owner, dt, visiting);
            }
            visiting.Remove(actor);
            if (actor.Destroyed) return;
            if (UEInterpreter.ToBool(actor.GetProp(PK.bTicked)) == LevelTicked) return;
            actor.Props.SetUnversioned(PK.bTicked, LevelTicked ? BoxTrue : BoxFalse);   // not a projected value (UEProps.Version)
            TickActorBody(interp, actor, dt);
            // AWind::Tick: wind body after AActor::Tick (Engine.dll.c:100425).
            if (!actor.Destroyed && UEInterpreter.ClassIsA(actor.Class, "Wind")) TickWind(actor, dt);
        }

        // The shipped AActor::Tick body order (Engine.dll.c:128667-128855):
        // particle prelude → animation → Tick/PlayerInput+PlayerTick events →
        // ProcessState → Timer → LifeSpan → performPhysics → root motion.
        // An expiring actor never runs physics that tick.
        public void TickActorBody(UEInterpreter interp, UEObject actor, float dt)
        {
            if (DetailedJournal)
                Log($"tick_actor name={actor.GetProp(PK.Name)} class={actor.Class?.Name} begin");
            // AParticleFX::Tick prelude (Engine.dll.c:14138) — the shipped
            // virtual runs its Age/budget-destroy body and then falls into
            // AActor::Tick; it consumes NO RNG (emission is render-side).
            if (FrameProducer is not null && UEInterpreter.ClassIsA(actor.Class, "ParticleFX"))
                TickParticleFxOf(interp, actor, dt);
            if (actor.Destroyed) return;
            // LOGICAL ANIMATION TICKS UNCONDITIONALLY: AnimEnd, the notify scan and frame
            // advance are Core's and must not depend on a frontend setting a hook.
            // The hook is only the VISUAL half of the same slot and runs AFTER the logic.
            TickAnimation(interp, actor, dt);
            if (actor.Destroyed) return;
            AnimationHook?.Invoke(interp, actor, dt);

            int role = UEInterpreter.ToInt(actor.GetProp(PK.Role));
            // ── ROLE BRANCHES OF AActor::Tick (LAB_103b3d7a, disasm 0x103b3d7a..
            // 0x103b4356) ─────────────────────────────────────────────────────
            // The fork is AFTER animation and BEFORE events/timer/physics:
            //   Pawn && Role == ROLE_SimulatedProxy (flag [ebp-0x24], 0x103b3990)
            //     -> simulated pawn branch 0x103b3d87..0x103b3f80;
            //   else RemoteRole == ROLE_AutonomousProxy (0x103b3f85: `cmp byte
            //     [esi+0x32], 3`) -> with Role == 4 a ViewTarget copy and ONLY
            //     Timer, with another role — nothing (0x103b3f93 -> 0x103b4356);
            //   else Role < ROLE_SimulatedProxy (0x103b40c6: `cmp al, 2; jb
            //     0x103b433e`) -> ONLY performPhysics and ONLY when Physics ==
            //     PHYS_Falling (0x103b433e: `cmp byte [esi+0x30], 2`);
            //   else — the common branch below (events, state, Timer, LifeSpan,
            //     physics).
            // The tail (pawn eyes/weapon, counters, root motion) is common to all
            // branches — shipped converges at 0x103b4356.
            if (role == 2 && UEInterpreter.ClassIsA(actor.Class, "Pawn"))
                WarnSimulatedPawnBranch(actor);
            else if (UEInterpreter.ToInt(actor.GetProp("RemoteRole")) == 3 || role < 2)
            {
                TickNonAuthorityRoleBranch(interp, actor, dt, role);
                goto roleBranchDone;
            }
            // The script event branch and the state slot are Core's, UNCONDITIONALLY;
            // frontends cannot replace them with their own driver.
            {
                bool localPlayer = role >= 2
                    && UEInterpreter.ClassIsA(actor.Class, "PlayerPawn")
                    && actor.GetProp(PK.Player) is not null;
                if (localPlayer)
                {
                    // Shipped AActor::Tick local-player branch (Engine.dll.c:
                    // 128667-128699, LAB_103b414a): Player->ReadInput(dt) (UViewport::ReadInput: UpdateInput(0), then
                    // UInput::ReadInput — Hold for held keys, ×20/dt), PlayerInput, then PlayerTick, then the state frame.
                    Input.ViewportReadInput(interp, ViewportConsole, actor, dt);
                    {
                        UFunction inputFn = interp.FindEventFunction(actor, "PlayerInput");
                        if (inputFn is not null)
                            GuardInvoke(interp, actor, inputFn, dt, "PlayerInput");
                    }
                    if (UEInterpreter.IsProbing(actor, "PlayerTick"))
                    {
                        UFunction playerTickFn = interp.FindEventFunction(actor, "PlayerTick");
                        if (playerTickFn is not null)
                            GuardInvoke(interp, actor, playerTickFn, dt, "PlayerTick");
                    }
                    // Shipped: Player->ReadInput(-1.0) right after PlayerTick
                    // (Engine.dll.c:128696) — zero every CPF_Input FLOAT
                    // property (metadata-driven, both frontends).
                    ResetPlayerInputAxes(actor);
                }
                // Early exit of shipped AActor::Tick (Engine.dll.c:128551): the probe mask
                // is checked BEFORE the function lookup. Same gate as in the central dispatch
                // (UELifecycle.CallEvent) — repeated here because the tick dispatches the
                // event directly, as the original does.
                else if (role >= 2 && UEInterpreter.IsProbing(actor, "Tick"))
                {
                    UFunction tickFn = interp.FindEventFunction(actor, "Tick");
                    // Empty Tick (HP1 Actor.uc) — nothing to execute; checked HERE to avoid
                    // paying the Guard closure + args array + dt boxing for each of ~2k
                    // actors per tick.
                    if (tickFn is not null && !UEInterpreter.IsEmptyScriptBody(tickFn))
                        GuardInvoke(interp, actor, tickFn, dt, "Tick");
                }
                // State slot is Core, unconditionally (stage 7D step 1.1).
                ProcessState(interp, actor, dt, role);
            }

            // Shipped order: ProcessState, then Timer, then LifeSpan, THEN
            // performPhysics (Engine.dll.c:128703-128730).
            TickActorTimer(interp, actor, dt);

            if (role >= 2 && !actor.Destroyed)
            {
                float lifeSpan = UEInterpreter.AsFloat(actor.GetProp(PK.LifeSpan));
                if (lifeSpan != 0f)
                {
                    actor.Props["LifeSpan"] = lifeSpan - dt;
                    if (lifeSpan - dt <= 0.0001f)
                    {
                        // Engine.dll.c:128719-128727: Expired, DestroyActor, return.
                        GuardEvent(interp, actor, "Expired");
                        DestroyActor(interp, actor);
                        Log($"tick_actor name={actor.GetProp(PK.Name)} expired");
                        return;
                    }
                }
            }

            int physics = UEInterpreter.ToInt(actor.GetProp(PK.Physics));
            // APawn::performPhysics saves the pre-step velocity and hands it
            // to physicsRotation for the banking dv/dt (Engine.dll.c:68640).
            // Shipped gates performPhysics on Physics!=None AND
            // Role!=ROLE_AutonomousProxy (128728: this[0x30]!=0 &&
            // this[0x31]!=3) with no bDeleteMe re-check after Timer; the hook
            // owns any further gating (the headless port keeps its
            // pre-existing tail gate). Standalone never assigns Role 3, so
            // the second gate is latent in Tut1 — carried for the full port.
            Vector3 preStepVelocity = UEInterpreter.AsVector3(actor.GetProp(PK.Velocity));
            // The physics slot is Core's, UNCONDITIONALLY: Cut* and SpellLearning rely on the
            // ordinary shipped mechanism (see the comment at ProcessState in
            // UELevelRuntime.StateFrame.cs).
            if (physics != 0 && role != 3)
                PerformPhysics(interp, actor, physics, dt, preStepVelocity);
            roleBranchDone:

            // ── PAWN EYES AND WEAPON (disasm 0x103b4451..0x103b44f4) ─────────
            //
            // Ghidra dropped this part of AActor::Tick: the site was found only through
            // the global ENGINE_UpdateEyeHeight (0x105e9e30 read at 0x103b4479), and the
            // live original 1.53 sends the event on 38 maps.
            // Offsets from the layout oracle (audit/prop_offsets.py):
            //   Pawn+0x25c bit 2 bIsPlayer, Actor+0x31 Role, Pawn+0x260 bit 1
            //   bViewTarget, Pawn+0x33c ViewRotation, Actor+0x108 Rotation,
            //   Pawn+0x318 Weapon, Actor+0xfc Location.
            //     if (bIsPlayer && Role >= ROLE_AutonomousProxy)
            //         bViewTarget ? eventUpdateEyeHeight(DeltaSeconds)
            //                     : ViewRotation = Rotation;
            //     if (Weapon) XLevel->FarMoveActor(Weapon, Location, 0, 0);
            // Before this shipped runs the ViewTarget copy block ->
            // TargetViewRotation/TargetEyeHeight/TargetWeaponViewOffset
            // (0x103b4356..0x103b444e), but only with XLevel->NetDriver having a
            // ServerConnection (+0x88 -> +0x38) — a network client; single player never
            // enters it. Shipped has no bDeleteMe check between physics and this block;
            // a destroyed actor never receives the event due to the dispatcher gate,
            // as in ProcessEvent.
            if (UEInterpreter.ClassIsA(actor.Class, "Pawn"))
            {
                if (UEInterpreter.ToBool(actor.GetProp(PK.bIsPlayer)) && role >= 3)
                {
                    if (UEInterpreter.ToBool(actor.GetProp(PK.bViewTarget)))
                        UELifecycle.CallEvent(interp, actor, "UpdateEyeHeight", dt);
                    else
                        actor.Props["ViewRotation"] = actor.GetProp(PK.Rotation);
                }
                if (actor.GetProp(PK.Weapon) is UEObject weapon)
                    FarMove(interp, weapon, UEInterpreter.AsVector3(actor.GetProp(PK.Location)), false, false);
            }

            // ── PAWN COUNTERS (Engine.dll.c:128770-128808) ──────────────────
            //
            // Placed AFTER physics and BEFORE root motion, as in the original
            // (performPhysics 128728 → these counters → tail 128808). The branch runs only
            // for a PAWN and only with Role == ROLE_Authority; the original's second
            // condition (param_2 == LEVELTICK_All) holds by construction: the port has one
            // full tick.
            //
            // WHY: PainTime is pit death. The original chain: Pawn.FootZoneChange sets
            // PainTime = 0.01 when a foot enters a pain zone (and -1 on leaving) → the
            // counter below decreases and raises PainTimer when crossing 0.001 →
            // Pawn.PainTimer reads FootRegion.Zone.DamagePerSec and calls TakeDamage with the
            // zone's DamageType → Harry.TakeDamage on 'ZonePain' or 'pit' goes to
            // hit_InstantPitDeath (Harry.uc:157). Without the counter the player falls into the
            // Lev_Tut1b pit (TWO pain zones) and survives at the bottom.
            //
            // The neighbouring SpeechTime is the same counter of the same form (128797-128802).
            //
            // Block order — disasm 0x103b450e..0x103b4682: sight
            // (SightCounter/ShowSelf/CheckEnemyVisible, PawnSightTick) ->
            // PainTime -> SpeechTime -> UpdateTactics.
            if (!actor.Destroyed && role == 4 && UEInterpreter.ClassIsA(actor.Class, "Pawn"))
            {
                PawnSightTick(interp, actor, dt);
                PawnCountdown(interp, actor, dt, "PainTime", "PainTimer");
                PawnCountdown(interp, actor, dt, UEObject.PropKey(actor.Class, "Pawn", "SpeechTime"), "SpeechTimer");
                // 0x103b4658: bAdvancedTactics (+0x25c bit 0x80000000) ->
                // eventUpdateTactics(DeltaSeconds) (FName 0x105e9d54). In the data
                // in the shipped data nobody sets the flag — the form is from the source.
                if (UEInterpreter.ToBool(actor.GetDeclared("Pawn", "bAdvancedTactics")))
                    UELifecycle.CallEvent(interp, actor, "UpdateTactics", dt);
            }

            // Shipped AActor::Tick tail (Engine.dll.c:128808-128855): authority
            // actors with bAnimMove sweep their skeletal root delta.
            if (!actor.Destroyed && role == 4
                && UEInterpreter.ToBool(actor.GetProp(PK.bAnimMove))
                && actor.GetProp(PK.Mesh) is not null)
                // Root motion is Core's (UELevelRuntime.RootMotion.cs); the pose player never
                // moves the actor through foreign collision.
                TickRootMovement(interp, actor);

            // Per-address playthrough telemetry — an observer, writes nothing into the
            // simulation (UETelemetry; enabled only together with the journal).
            if (HP1.Core.UEPlaythroughLog.Enabled && UETelemetry.Active)
                UETelemetry.AfterActorTick(this, interp, actor, dt);

            if (DetailedJournal)
                Log($"tick_actor name={actor.GetProp(PK.Name)} end");
        }

        // Actor Timer — shared by the common branch (Engine.dll.c:128703-128717) and the
        // RemoteRole == ROLE_AutonomousProxy branch (LAB_103b4054, disasm
        // 0x103b4054..0x103b40c1 — the same body: `fadd TimerCounter; fcom
        // TimerRate`, ROUND, bTimerLoop bit 0x4000 of +0x28, Timer event).
        void TickActorTimer(UEInterpreter interp, UEObject actor, float dt)
        {
            if (actor.TimerRate > 0f && !actor.Destroyed)
            {
                // Shipped fires the event ONCE per tick and subtracts
                // ROUND(counter/rate)*rate (128704-128717) — the counter
                // can go negative on a >1.5x overshoot; never a loop.
                actor.TimerCounter = TimerStep(actor.TimerCounter, actor.TimerRate, dt, out bool fired, out int n);
                if (fired)
                {
                    if (n != 0 && !actor.TimerLoop) actor.TimerRate = 0f;
                    GuardEvent(interp, actor, "Timer");
                }
            }
        }

        // AActor::Tick Timer, 0x103b4260..0x103b428d (and 0x103b4068..0x103b4097): s = dt + TimerCounter in the register,
        // stored as float (fst); it fires when the UNROUNDED s >= TimerRate (fcom before the store); n = fistp of
        // float(s / TimerRate) (the register s; nearest even; out of int range 0x80000000); TimerCounter =
        // float(float(s) − n·TimerRate), fild·fmul·fsubr with one store.
        internal static float TimerStep(float counter, float rate, float dt, out bool fired, out int n)
        {
            n = 0;
            static int Fistp(double q)
            {
                double rq = Math.Round(q, MidpointRounding.ToEven);
                return rq >= -2147483648.0 && rq < 2147483648.0 ? (int)rq : int.MinValue;
            }
            if (!float.IsFinite(counter) || !float.IsFinite(rate) || !float.IsFinite(dt))
            {
                float s0 = counter + dt;
                fired = rate <= s0;
                if (!fired) return s0;
                n = Fistp(s0 / rate);
                return (float)(s0 - (double)n * rate);
            }
            static HP1.Core.X87Ext X(float v) => HP1.Core.X87Ext.From(v);
            static HP1.Core.X87Ext R(HP1.Core.X87Ext v) => HP1.Core.X87Ext.Round64(v);
            var s = R(X(dt) + X(counter));
            float sf = s.ToFloat();
            fired = s.CompareTo(X(rate)) >= 0;
            if (!fired) return sf;
            n = Fistp(HP1.Core.X87Ext.Div64(s, X(rate)).ToFloat());
            return R(X(sf) - HP1.Core.X87Ext.From(n) * X(rate)).ToFloat();
        }

        // The two role branches of AActor::Tick that bypass the common one (see the fork in
        // TickActorBody). No Tick/PlayerTick events, no state frame and no LifeSpan here —
        // shipped has none in these branches.
        void TickNonAuthorityRoleBranch(UEInterpreter interp, UEObject actor, float dt, int role)
        {
            if (UEInterpreter.ToInt(actor.GetProp("RemoteRole")) == 3)
            {
                // 0x103b3f93: `cmp al, 4; jne 0x103b4356` — not Authority: nothing.
                if (role != 4) return;
                // 0x103b3f9b..0x103b4051 (Engine.dll.c:128617-128633): a PlayerPawn-class pawn
                // with a ViewTarget pawn copies its view:
                //   TargetViewRotation (+0x7c8) = ViewTarget.ViewRotation (+0x33c),
                //   TargetEyeHeight (+0x7d4)    = ViewTarget.EyeHeight (+0x358),
                //   TargetWeaponViewOffset (+0x7d8) = ViewTarget.Weapon
                //     .PlayerViewOffset (+0x318 -> +0x290), if there is a weapon.
                if (UEInterpreter.ClassIsA(actor.Class, "PlayerPawn")
                    && actor.GetProp(PK.ViewTarget) is UEObject viewTarget
                    && UEInterpreter.ClassIsA(viewTarget.Class, "Pawn"))
                {
                    actor.Props["TargetViewRotation"] = viewTarget.GetProp("ViewRotation");
                    actor.Props["TargetEyeHeight"] = viewTarget.GetProp("EyeHeight");
                    if (viewTarget.GetProp(PK.Weapon) is UEObject weapon)
                        actor.Props["TargetWeaponViewOffset"] = weapon.GetProp("PlayerViewOffset");
                }
                TickActorTimer(interp, actor, dt);   // LAB_103b4054
                return;
            }
            // Role < 2: 0x103b433e `cmp byte [esi+0x30], 2; jne` -> performPhysics
            // (vtable +0xa8, 0x103b434c) only with PHYS_Falling.
            if (UEInterpreter.ToInt(actor.GetProp(PK.Physics)) == 2)
                PerformPhysics(interp, actor, 2, dt, UEInterpreter.AsVector3(actor.GetProp(PK.Velocity)));
        }

        // The simulated pawn branch (Pawn && Role == ROLE_SimulatedProxy,
        // disasm 0x103b3d87..0x103b3f80: trace under the feet, zone gravity,
        // moveSmooth(Velocity·dt), Tick event) is NOT PORTED — STOP. Such a pawn ticks
        // through the common branch; the divergence is reported loudly, once per actor.
        readonly HashSet<UEObject> _simulatedPawnWarned = new();
        void WarnSimulatedPawnBranch(UEObject actor)
        {
            if (_simulatedPawnWarned.Add(actor))
                HP1.Core.UETrace.Warn(
                    $"[Core] STOP: Pawn with Role=ROLE_SimulatedProxy ticks through the common branch — "
                    + $"shipped body 0x103b3d87..0x103b3f80 not ported: actor={actor.GetProp(PK.Name)} "
                    + $"class={actor.Class?.Name}");
        }

        // Closures live in separate methods: capturing (interp, actor, dt) in the body of
        // TickActorBody made the compiler allocate on EVERY call, even when the lambda
        // did not run (~40 KB of garbage per tick).
        void TickParticleFxOf(UEInterpreter interp, UEObject actor, float dt)
        {
            ParticleTickTap?.Invoke(this, actor, dt, true);
            if (!ReferenceEquals(_fxDestroyInterp, interp)) { _fxDestroyInterp = interp; _fxDestroy = DestroyFxOf(interp); }
            FrameProducer.TickParticleFx(actor, dt, _fxDestroy);
            ParticleTickTap?.Invoke(this, actor, dt, false);
        }
        // One destroy delegate per interpreter instead of a closure per call (allocation only; called synchronously).
        UEInterpreter _fxDestroyInterp;
        Action<UEObject> _fxDestroy;
        Action<UEObject> DestroyFxOf(UEInterpreter interp) => a => DestroyActor(interp, a);   // own method: no closure on entry
        // SwARc Engine measurement taps (runtime_poc/HeadlessEngineJudgeDump): AParticleFX::Tick's prelude and
        // AParticleFX::InitExecution, observed at entry and exit; null in production.
        public static Action<UELevelRuntime, UEObject, float, bool> ParticleTickTap;
        public static Action<UEObject, bool> ParticleInitTap;
        // Default policy is to just run the body, so no delegate. The lambda lives in a
        // separate method: capturing parameters made the compiler allocate a closure object
        // on entry of EVERY call, even on the fast unguarded branch.
        void GuardInvoke(UEInterpreter interp, UEObject actor, UFunction fn, float dt, string what)
        {
            if (ReferenceEquals(_guard, DefaultGuard))
            {
                // Argument array from the call pool (UEVmPool): Invoke keeps no reference past its return.
                object[] a = interp.Pool.RentArgs(1);
                a[0] = dt;
                interp.Invoke(actor, fn, a);
                interp.Pool.ReleaseArgs(a);
            }
            else GuardInvokeGuarded(interp, actor, fn, dt, what);
        }
        void GuardInvokeGuarded(UEInterpreter interp, UEObject actor, UFunction fn, float dt, string what)
            => Guard(() => interp.Invoke(actor, fn, dt), what);
        void GuardEvent(UEInterpreter interp, UEObject actor, string ev)
        {
            if (ReferenceEquals(_guard, DefaultGuard)) UELifecycle.CallEvent(interp, actor, ev);
            else GuardEventGuarded(interp, actor, ev);
        }
        void GuardEventGuarded(UEInterpreter interp, UEObject actor, string ev)
            => Guard(() => UELifecycle.CallEvent(interp, actor, ev), ev);

        // One form for both pawn counters (Engine.dll.c:128788-128794 for PainTime and
        // 128797-128802 for SpeechTime): while the value is STRICTLY positive, subtract
        // dt; if the result drops below 0.001, zero it and raise the event. Zeroing comes
        // BEFORE the event on purpose: the handler may recharge the counter
        // (Pawn.PainTimer sets PainTime = 2 in the drowning branch), and the original order
        // allows it.
        //
        // The event goes through the common dispatcher and thus passes the probe mask —
        // exactly like ProcessEvent in the original (PainTimer is probe bit 31).
        static void PawnCountdown(UEInterpreter interp, UEObject actor, float dt,
            string field, string evt)
        {
            float value = UEInterpreter.AsFloat(actor.GetProp(field));
            if (value <= 0f) return;
            float next = value - dt;
            actor.Props[field] = next;
            if (next >= 0.001f) return;
            actor.Props[field] = 0f;
            UELifecycle.CallEvent(interp, actor, evt);
        }

        // ── ULevel::DestroyActor (Engine.dll.c:252940). Every event below runs
        // on a LIVE actor — the script-visible bDeleteMe (our Destroyed flag)
        // flips only at the END (253087); the bDeleteMe re-check after each
        // event handles a recursive Destroy completing first. ──
        // Length of the Level->FirstDeleted list (ULevel+0x100): actors destroyed since
        // the last CleanupDestroyed.
        public int FirstDeletedCount { get; private set; }

        public bool DestroyActor(UEInterpreter interp, UEObject self)
        {
            if (self is null) return false;
            if (UEInterpreter.ToBool(self.GetProp(PK.bStatic)) || UEInterpreter.ToBool(self.GetProp(PK.bNoDelete)))
                return false;
            if (self.Destroyed) return true;
            // Role refusal (FUN_103a72c0, Engine.dll.c:252973-252976; disasm
            // 0x103a7353..0x103a7377 — after `test al,5` and `test ah,2`, outside the editor):
            //   Role (+0x31) != ROLE_Authority(4) && !bNetForce && !(+0x28 & 0x4000000) -> 0.
            // Bit 0x4000000 of word +0x28 is Actor.bNetTemporary (Engine.u layout,
            // audit/coverage_2026-10-02/field_coverage.tsv; the same table gives
            // bStatic 0x1, bNoDelete 0x4, bDeleteMe 0x200 and Role +0x31 — exactly the ones
            // this body checks). bNetForce: execDestroy passes 0 (`push 0`, 0x1040cc32);
            // 1 appears in the decompile only in network UPlayer::Destroy (106470) and
            // UActorChannel::Destroy (154858), absent from the port. Other DestroyActor calls
            // in the port pass 0; that all their shipped counterparts pass 0 is not verified
            // for each — with Role = 4 the value has no effect.
            // In HP1 the role is set only by Actor (4); the branch is unreachable in the
            // campaign and is ported as is.
            if (UEInterpreter.ToInt(self.GetProp(PK.Role)) != 4
                && !UEInterpreter.ToBool(self.GetProp("bNetTemporary")))
                return false;

            // EndState notification (253001-253012): shipped gates on
            // StateFrame != 0 && StateFrame->StateNode != 0 && IsProbing(EndState).
            // Every shipped actor has a StateFrame from InitExecution, and its
            // StateNode is the class when no state is set (GotoState of an
            // unknown state also leaves StateNode = Class), so the effective
            // gate is IsProbing alone — CallEvent applies it. The port creates
            // StateFrame lazily (first GotoStateCore), so its null must NOT
            // skip the dispatch (D-13).
            UELifecycle.CallEvent(interp, self, "EndState");
            if (self.Destroyed) return true;
            // Remove from base (253015-253016, bNotifyActor=1).
            if (self.GetProp(PK.Base) is not null)
            {
                SetBase(interp, self, null, notify: true);
                if (self.Destroyed) return true;
            }
            // Detach every rider based on us (253018-253025, StandingCount gate).
            if (UEInterpreter.ToInt(self.GetProp(PK.StandingCount)) != 0)
                foreach (UEObject rider in Actors.ToArray())
                    if (rider is not null && !rider.Destroyed
                        && ReferenceEquals(rider.GetProp(PK.Base) as UEObject, self))
                        SetBase(interp, rider, null, notify: true);
            // Collision-hash removal before the Destroyed event (253027-253031).
            // Core-owned: the hash is Core's structure, so Core takes the actor
            // out of it. The frontend used to make this call, which meant a
            // frontend that forgot to (Unity does) left every destroyed actor
            // linked in the hash forever. The hook survives for frontend-only
            // teardown — stopping sounds, dropping render proxies — never for
            // the hash itself.
            HashRemoveForDestroy(self);
            DestroyCleanupHook?.Invoke(self);
            // Tell the actor it is about to go (253033-253035) — still live.
            UELifecycle.CallEvent(interp, self, "Destroyed");
            if (self.Destroyed) return true;
            // One pass over the level (253038-253059): orphan our children, and
            // unlink every touch pair FROM THE HOLDER's side — an asymmetric
            // link (they hold us, we do not hold them) is found here, not in
            // our own list. bNoNotify=1: the dying side gets no UnTouch (253052).
            foreach (UEObject a in Actors.ToArray())
            {
                if (a is null || a.Destroyed || ReferenceEquals(a, self)) continue;
                if (ReferenceEquals(a.GetProp(PK.Owner) as UEObject, self))
                {
                    SetOwner(interp, a, null);
                    if (self.Destroyed) return true;
                }
                else if (Array.IndexOf(a.TouchList(), self) >= 0)
                {
                    UELifecycle.EndTouch(interp, self, a, noNotifySelf: true);
                    if (self.Destroyed) return true;
                }
            }
            // NB the shipped pass unlinks only from the HOLDER's side; a link
            // only WE hold (they never linked back) dies with our slot — no
            // event is due on the other actor, exactly like shipped.
            // LostChild on OUR owner last (253061-253070; Owner itself is not
            // cleared — the actor is gone after this call).
            if (self.GetProp(PK.Owner) is UEObject owner)
            {
                UELifecycle.CallEvent(interp, owner, "LostChild", self);
                if (self.Destroyed) return true;
            }
            // The end: the slot dies and the script-visible flag flips
            // (253086-253087).
            self.Destroyed = true;
            self.Props["bDeleteMe"] = true;
            // Engine->Audio->NoteDestroy(Actor) (vtable +0x7c, 253088-253089): the ambient slot
            // is silenced, other channels are detached from the actor.
            FrameProducer?.Audio.NoteDestroy(self.ObjectId);
            ExitProperties(self.Class, self);   // ConditionalDestroy -> UObject::Destroy (253087)
            // Outside the editor the actor joins Level->FirstDeleted (disasm
            // 0x103a7801..0x103a7812); CleanupDestroyed clears the list.
            FirstDeletedCount++;
            DestroyTeardownHook?.Invoke(self);
            return true;
        }

        // UObject::ExitProperties (Core 0x1015bdf0), from UObject::Destroy (0x101594e0), which
        // ULevel::DestroyActor calls immediately (ConditionalDestroy, 253087): DestroyValue of every
        // ConstructorLink property — those with CPF_NeedCtorLink (UStrProperty::Link, Core.dll.c:3669; array;
        // struct with such members). ~FString zeroes Num/Max (0x10106cad-0x10106cb0): the corpse's string
        // reads as "", array<> is empty. References, numbers, names are untouched. A struct without a value
        // in Props (read from class defaults) is not materialised — a known limit.
        static void ExitProperties(UStruct def, UEObject o)
        {
            for (UStruct c = def; c is not null; c = c is UClass cls ? UEInterpreter.CanonicalSuper(cls) : c.Super as UStruct)
                foreach (UProperty p in c.EnumerateFields<UProperty>())
                {
                    string name = p.Name?.ToString();
                    if (name is null) continue;
                    switch (p)
                    {
                        case UStrProperty when p.ArrayDim > 1:
                            if (o.Props.TryGetValue(name, out object sa) && sa is System.Collections.IList sl)
                                for (int i = 0; i < sl.Count; i++) sl[i] = "";
                            else o.Props[name] = new List<object>(System.Linq.Enumerable.Repeat<object>("", p.ArrayDim));
                            break;
                        case UStrProperty:
                            o.Props[name] = "";
                            break;
                        case UArrayProperty:
                            o.Props[name] = new List<object>();
                            break;
                        case UStructProperty sp when sp.Struct is not null:
                            if (!o.Props.TryGetValue(name, out object v)) break;
                            if (v is UEObject bag) ExitProperties(sp.Struct, bag);
                            else if (v is System.Collections.IList items)
                                foreach (object e in items) if (e is UEObject eb) ExitProperties(sp.Struct, eb);
                            break;
                    }
                }
        }

        // ── AActor::SetOwner (Engine.dll.c:44726): LostChild on the old owner,
        // set Owner, GainedChild on the new. Shipped keeps no child list. ──
        public void SetOwner(UEInterpreter interp, UEObject actor, UEObject newOwner)
        {
            UEObject old = actor.GetProp(PK.Owner) as UEObject;
            if (old is not null) UELifecycle.CallEvent(interp, old, "LostChild", actor);
            actor.Props["Owner"] = newOwner;
            if (newOwner is not null) UELifecycle.CallEvent(interp, newOwner, "GainedChild", actor);
        }

        // ── AActor::SetBase (Engine.dll.c:160728): re-parent onto NewBase with
        // StandingCount + Detach/Attach/BaseChange events. Level() is a valid
        // base but is excluded from the count and the events. ──
        public void SetBase(UEInterpreter interp, UEObject actor, UEObject newBase, bool notify)
        {
            // Cycle guard: if `actor` is anywhere in NewBase's base chain,
            // abort (shipped walks the chain and returns without touching
            // anything).
            for (UEObject p = newBase; p is not null; p = p.GetProp(PK.Base) as UEObject)
                if (ReferenceEquals(p, actor)) return;
            UEObject cur = actor.GetProp(PK.Base) as UEObject;
            if (ReferenceEquals(newBase, cur)) return;
            UEObject level = actor.GetProp(PK.Level) as UEObject;
            if (cur is not null && !ReferenceEquals(cur, level))
            {
                cur.Props["StandingCount"] = UEInterpreter.ToInt(cur.GetProp(PK.StandingCount)) - 1;
                UELifecycle.CallEvent(interp, cur, "Detach", actor);
            }
            actor.Props["Base"] = newBase;
            if (newBase is not null && !ReferenceEquals(newBase, level))
            {
                newBase.Props["StandingCount"] = UEInterpreter.ToInt(newBase.GetProp(PK.StandingCount)) + 1;
                UELifecycle.CallEvent(interp, newBase, "Attach", actor);
            }
            if (notify) UELifecycle.CallEvent(interp, actor, "BaseChange");
        }

        // ── ULevel::SetActorZone (vtable 0xb0, Engine.dll.c FUN_103acdd0): the
        // full shipped lifecycle. On a zone change (old Region.Zone != new):
        // with !bTest the OLD zone gets ActorLeaving and the actor gets
        // ZoneChange(new) BEFORE the Region write, the NEW zone gets
        // ActorEntered after it (256399-256414). Same zone → silent iLeaf/
        // ZoneNumber refresh (256393). A Pawn then re-derives FootRegion
        // (Location - (0,0,CollisionHeight)) and HeadRegion (Location +
        // (0,0,EyeHeight)) with FootZoneChange/HeadZoneChange fired BEFORE
        // each write (256416-256464), and mirrors Region.Zone into
        // PlayerReplicationInfo.PlayerZone (256465-256468; the NetMode gate is
        // always true standalone). Water/pain/destructive behaviour lives in
        // the SCRIPT bodies these events reach via the VM — not here. ──
        // Selftest probe: (actor, the location SetActorZone was given, whether the actor is linked in the collision hash).
        internal Action<UEObject, Vector3, bool> SetActorZoneProbe;
        internal bool HashLinked(UEObject a) => a is not null && _hashHandle.TryGetValue(a, out int h) && (CollisionHash?.LinkCount(h) ?? 0) > 0;

        public void SetActorZone(UEInterpreter interp, UEObject actor, Vector3 location,
            bool bTest = false, bool bForceRefresh = false)
        {
            SetActorZoneProbe?.Invoke(actor, location, HashLinked(actor));
            if (actor.Destroyed) return;                        // bDeleteMe gate (256304)
            UEObject level = interp.GetLevelInfo();
            bool isPawn = UEInterpreter.ClassIsA(actor.Class, "Pawn");
            if (ReferenceEquals(actor, level))
            {
                actor.SetDeclared("Actor", "Region", UEObject.MakeRegion(level, -1, 0));   // 256322-256325
                return;
            }
            if (bForceRefresh)                                   // 256339-256370
            {
                actor.SetDeclared("Actor", "Region", UEObject.MakeRegion(level, -1, 0));
                if (isPawn)
                {
                    actor.Props["FootRegion"] = UEObject.MakeRegion(level, -1, 0);
                    actor.Props["HeadRegion"] = UEObject.MakeRegion(level, -1, 0);
                }
            }
            (UEObject zone, int leaf, byte zoneNumber) = ResolveRegion(location, level);
            UEObject oldZone = (actor.GetDeclared("Actor", "Region") as UEObject)?.GetProp(PK.Zone) as UEObject
                ?? level;                                        // shipped seed is {LevelInfo,-1,0}
            if (ReferenceEquals(zone, oldZone))
                actor.SetDeclared("Actor", "Region", UEObject.MakeRegion(zone, leaf, zoneNumber));
            else
            {
                if (!bTest)
                {
                    UELifecycle.CallEvent(interp, oldZone, "ActorLeaving", actor);
                    UELifecycle.CallEvent(interp, actor, "ZoneChange", zone);
                }
                actor.SetDeclared("Actor", "Region", UEObject.MakeRegion(zone, leaf, zoneNumber));
                if (!bTest) UELifecycle.CallEvent(interp, zone, "ActorEntered", actor);
            }
            if (!isPawn) return;
            float collisionHeight = UEInterpreter.AsFloat(actor.GetProp(PK.CollisionHeight));
            (UEObject footZone, int footLeaf, byte footNumber) =
                ResolveRegion(location - new Vector3(0f, 0f, collisionHeight), level);
            UEObject oldFoot = (actor.GetProp(PK.FootRegion) as UEObject)?.GetProp(PK.Zone) as UEObject
                ?? level;
            if (!ReferenceEquals(footZone, oldFoot) && !bTest)
                UELifecycle.CallEvent(interp, actor, "FootZoneChange", footZone);
            actor.Props["FootRegion"] = UEObject.MakeRegion(footZone, footLeaf, footNumber);
            float eyeHeight = UEInterpreter.AsFloat(actor.GetProp(PK.EyeHeight));
            (UEObject headZone, int headLeaf, byte headNumber) =
                ResolveRegion(location + new Vector3(0f, 0f, eyeHeight), level);
            UEObject oldHead = (actor.GetProp(PK.HeadRegion) as UEObject)?.GetProp(PK.Zone) as UEObject
                ?? level;
            if (!ReferenceEquals(headZone, oldHead) && !bTest)
                UELifecycle.CallEvent(interp, actor, "HeadZoneChange", headZone);
            actor.Props["HeadRegion"] = UEObject.MakeRegion(headZone, headLeaf, headNumber);
            if (actor.GetProp(PK.PlayerReplicationInfo) is UEObject pri)
                pri.Props["PlayerZone"] = (actor.GetDeclared("Actor", "Region") as UEObject)?.GetProp(PK.Zone);
        }

        // iLeaf is resolved by EXECUTION: shipped `UModel::PointRegion` was run under the
        // emulator on the real Tut1 model, and over 1259 actor positions it matched 1259/1259
        // on both zone and leaf once the leaf is taken from the SAME side as the zone
        // (UEBspCollision.cs, FindRegion).
        //
        // iLeaf is READ: it goes into WorldFrame and defines the actor's lighting candidates —
        // shipped takes them from the leaf sublist (StaticGeometry.LeafPermeating/LightExports),
        // not by radius.
        // ULevel::PointRegion is CORE: Core has its OWN loaded world model (Bsp) and actor table
        // by export (ActorsByExport), so player zones and collision zones come from one model.
        // A null zone (no Bsp) resolves to LevelInfo — as with shipped PointRegion finding
        // no zone.
        (UEObject Zone, int Leaf, byte ZoneNumber) ResolveRegion(Vector3 location, UEObject level)
        {
            if (Bsp is null) return (level, -1, 0);
            BspRegion region = Bsp.FindRegion(location);
            UEObject zone = ActorsByExport.TryGetValue(region.ZoneActorIndex, out UEObject z) ? z : null;
            return (zone ?? level, region.Leaf, (byte)region.ZoneNumber);
        }

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
        // Core's own geometry, with the hook as an OVERRIDE.
        //
        // Core answers these itself: the swept move IS MoveActor, the trace IS
        // SingleLineCheck, the teleport IS FarMove. The hooks remain for a
        // frontend that must still interpose (headless keeps LineTraceHook for
        // its journal line), but nothing depends on their being set.
        // A physics-body move into ITS FCheckResult (`hit` is the caller's local,
        // see MoveCheck): the refusal and the MoveActor fast path do not touch the record,
        // and the caller reads what was there (the previous move or trace).
        // The hook is a fixture seam: its answer is stored into the record whole.
        (float Fraction, Vector3 Normal, UEObject HitActor) WalkMove(
            UEInterpreter interp, UEObject pawn, Vector3 delta, MoveCheck hit)
        {
            if (WalkMoveHook is not null)
            {
                // The hook replaces only the full-path SWEEP; the refusal and fast path are
                // MoveActor's own exits before writing Hit (0x103aa3ed-0x103aa501) and stay
                // with Core (the physics move carries the current rotation).
                if (MoveActorRefuses(pawn) || IsTinyMoveDelta(delta)) return (hit.Time, hit.Normal, hit.Actor);
                (float f, Vector3 n, UEObject a) = WalkMoveHook(interp, pawn, delta);
                hit.Set(f, n, a, -1, Vector3.zero);
                return (f, n, a);
            }
            MoveActor(interp, pawn, delta, UEInterpreter.AsRotator(pawn.GetProp(PK.Rotation)),
                bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false, hit);
            return (hit.Time, hit.Normal, hit.Actor);
        }

        // physWalking/stepUp trace into the SAME FCheckResult as the moves: every
        // SingleLineCheck of APawn::physWalking goes into &uStack_80 (the same address as
        // MoveActor's, Engine.dll.c:135495-136341), so the next fast-path move reads the
        // trace result. Disasm (physWalking 0x103e6b60..0x103eb3e0): all 22 ULevel vtable
        // +0xc4 calls (SingleLineCheck; vtable slot holds 0x1030348b), all 7 +0x8c calls
        // (MoveActor, 0x1030404d) and the stepUp call 0x103e9008 pass `lea [ebp-0x7c]`;
        // that record is built once at 0x103e6ecb..0x103e6ef2 (Time 1.0).
        LineCheckHit WalkTrace(UEInterpreter interp, UEObject actor, MoveCheck hit,
            Vector3 start, Vector3 end, Vector3 extent, int traceFlags)
        {
            LineCheckHit h = LineTrace(interp, actor, start, end, extent, traceFlags);
            hit.FromTrace(h);
            return h;
        }

        // FVector::operator/(FLOAT) Core.dll 0x10119310: `fld 1.0; fdiv V` —
        // the reciprocal stays in x87 (not rounded to float), each component X·RV is
        // rounded at fstp dword.
        // EQUIVALENT TO FLOAT DIVISION: for normal floats the quotient X/V is at least
        // 2^-49 relative away from any midpoint between adjacent floats (X·2^k − M·V is a
        // multiple of the low bit of the 49-bit M·V), while X·RV in the register (and in
        // double) errs by less than 2^-52 — rounding to float always matches. Only the form
        // with RV ROUNDED to float differs from division (see DisplacementVelocityX87).
        public static Vector3 DivByFloat(Vector3 v, float d)
        {
            double rv = 1.0 / d;
            return new Vector3((float)(v.x * rv), (float)(v.y * rv), (float)(v.z * rv));
        }

        // Velocity from displacement in tails where the compiler kept ONE difference in an
        // x87 register (disasm):
        //   physProjectile 0x103f2e6c-0x103f2ec4, physFlying 0x103f19b3-0x103f1a16 —
        //     X in the register; AActor::performPhysics, PHYS_MovingBrush
        //     0x103e5351-0x103e539e — Z in the register.
        // Form of all three: `fld 1.0; fdiv dt; fst dword [tmp]` — 1/dt both in the register
        // and as a float-rounded copy; the register component = (L−O)·(1/dt) without
        // intermediate rounding, the other two = float(L−O) · float(1/dt). Each product is
        // rounded at fstp dword. The register (64-bit mantissa) is modelled by double, as in
        // DivByFloat.
        public static Vector3 DisplacementVelocityX87(Vector3 loc, Vector3 old, float dt, int registerAxis)
        {
            double rv = 1.0 / dt;
            float rvF = (float)rv;
            float C(int axis, float l, float o)
                => axis == registerAxis ? (float)(((double)l - o) * rv) : (l - o) * rvF;
            return new Vector3(C(0, loc.x, old.x), C(1, loc.y, old.y), C(2, loc.z, old.z));
        }

        LineCheckHit LineTrace(
            UEInterpreter interp, UEObject actor, Vector3 start, Vector3 end,
            Vector3 extent, int traceFlags)
        {
            if (LineTraceHook is not null)
                return LineTraceHook(interp, actor, start, end, extent, traceFlags);
            return SingleLineCheck(interp, actor, actor, start, end, extent, traceFlags);
        }

        bool FarMoveTo(UEInterpreter interp, UEObject actor, Vector3 newLoc)
            => FarMoveHook is not null
                ? FarMoveHook(interp, actor, newLoc)
                : FarMove(interp, actor, newLoc, bTest: false, bNoCheck: false);

        // Diagnostic gates for the walking trace lines (headless: TraceMovesNow).
        public Func<bool> TraceMovesHook;
        bool TraceMovesNow => TraceMovesHook?.Invoke() ?? false;

        // ── APawn::physWalking (Engine.dll.c:135282) — the FULL walking body,
        // one state machine for every frontend: the zone-0 head, the
        // stationary floor check with the Falling transition, and the moving
        // branch (calcVelocity → zone step velocity → the shipped substep
        // plan → sweep → stepUp on a block → step-down grounding with
        // SetBase → the displacement velocity tail). Geometry rides the
        // hooks above. ──
        public void PhysWalking(UEInterpreter interp, UEObject actor, float dt, int iteration = 0)
        {
            // APawn::physWalking is a PAWN method — a non-Pawn carrying
            // PHYS_Walking (jelly beans and other pickups) never runs it.
            if (!UEInterpreter.ClassIsA(actor.Class, "Pawn")) return;
            UEObject region = actor.GetProp(PK.Region) as UEObject;
            if (region is not null && UEInterpreter.ToInt(region.GetProp(PK.ZoneNumber)) == 0)
            {
                // APawn::physWalking zone-0 head (Engine.dll.c:290948-290959).
                FellOutOfWorld(interp, actor, falling: false);
                return;
            }

            Vector3 location = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
            Vector3 velocity = UEInterpreter.AsVector3(actor.GetProp(PK.Velocity));
            // JOURNAL FILTER ONLY — this is NOT a shipped dispatch; the shipped
            // head has none (it runs calcVelocity unconditionally and dispatches
            // per SUBSTEP at 136181-136191, which is `stationarySubstep` below).
            // It exists purely so a fully idle pawn does not emit a walking_move
            // line every tick. Its horizontal-only test is a journal-volume
            // choice, not a ported predicate — do not read physics into it.
            bool journalMoving = UEInterpreter.AsVector3(actor.GetProp(PK.Acceleration)).sqrMagnitude > 0.0001f
                || new Vector3(velocity.x, velocity.y, 0f).sqrMagnitude > 0f;
            actor.Props["OldLocation"] = location;
            actor.Props["bJustTeleported"] = false;
            // NOTE: the shipped head zeroes Acceleration.Z ONLY (135300:
            // *(this+0x144)=0 with this+0x13c = Acceleration). Velocity keeps
            // all three components — calcVelocity reads/writes 0x12c/0x130/
            // 0x134 (135320-135322), the substep planner squares all three
            // (135432) and the substep dispatch compares all three deltas
            // (136181-136191). Never zero Velocity.Z here.

            // The shipped head (135300-135319): Acceleration.z zeroed, the
            // accel DIRECTION cached, calcVelocity ALWAYS (a braking no-op
            // for an idle pawn), ONE zone-adjusted step velocity for the
            // whole call.
            UEObject zone = region?.GetProp(PK.Zone) as UEObject;
            Vector3 gravity = UEInterpreter.AsVector3(zone?.GetProp(PK.ZoneGravity));
            float friction = UEInterpreter.AsFloat(zone?.GetProp(PK.ZoneGroundFriction));
            Vector3 zoneVel = UEInterpreter.AsVector3(zone?.GetProp(PK.ZoneVelocity));
            Vector3 accel = UEInterpreter.AsVector3(actor.GetProp(PK.Acceleration));
            accel.z = 0f;
            actor.Props["Acceleration"] = accel;
            Vector3 accelDir = accel.sqrMagnitude > 0f ? accel.normalized : Vector3.zero;
            UEPawnMovement.CalcVelocity(actor, accelDir,
                dt, UEInterpreter.AsFloat(actor.GetProp(PK.GroundSpeed)), friction,
                bFluid: false, bBrake: true, bBuoyant: false);
            Vector3 moveVel = UEPawnMovement.WalkingMoveVelocity(actor, zoneVel, dt);
            bool walkerIsPlayer = UEInterpreter.ClassIsA(actor.Class, "PlayerPawn");

            var walk = new WalkState
            {
                FullDt = dt,
                Remaining = dt,
                Iteration = iteration,
                FloorNormal = new Vector3(0f, 0f, 1f),
                MoveVel = moveVel,
                AccelDir = accelDir,
                GravSign = gravity.z > 0f ? 1f : -1f,
                Extent = GetCylinderExtent(actor),
                Hit = new MoveCheck(),
            };
            UEObject level = interp.GetLevelInfo();
            while (walk.Remaining > 0f && walk.Iteration <= 7)
            {
                walk.Iteration++;
                float sub = UEPawnMovement.NextWalkSubstep(walk.Remaining, walkerIsPlayer,
                    moveVel);
                walk.Remaining -= sub;
                walk.Sub = sub;
                walk.PreSubstepLoc = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
                Vector3 delta = moveVel * sub;
                // fStack_128 = delta at the start of the substep (135441); a moving
                // substep rewrites it to adjust (LAB_103e86bb).
                walk.Intended = delta;
                // The per-substep moving/stationary dispatch (135458 + 136181-
                // 136191): shipped tests |delta.X|, then |delta.Y|, then
                // |delta.Z| against 0.0001 and takes the moving machinery on
                // the FIRST one that passes — a purely VERTICAL substep is a
                // moving substep. Otherwise the stationary path — remaining
                // zeroed (136193, ONE gate pass), bHitSlopedWall cleared
                // (136194), then the hover check (LAB_103e9861).
                bool stationarySubstep = Math.Abs(delta.x) < 0.0001f && Math.Abs(delta.y) < 0.0001f
                    && Math.Abs(delta.z) < 0.0001f;
                if (!stationarySubstep)
                {
                    if (MovingSubstep(interp, actor, delta, walkerIsPlayer, ref walk)
                        == WalkGateResult.Return)
                        return;
                }
                else
                {
                    walk.Remaining = 0f;
                    actor.Props["bHitSlopedWall"] = false;
                    // Hover check: a POINT trace (extent zero, flags 6 =
                    // Movers|Level) 20 units below the feet (Location.z −
                    // CollisionHeight); hitting the CURRENT Base inside the
                    // [4.1, 4.6] hover band means the pawn is settled — skip
                    // the step-down entirely.
                    Vector3 feet = walk.PreSubstepLoc
                        - new Vector3(0f, 0f, UEInterpreter.AsFloat(actor.GetProp(PK.CollisionHeight)));
                    (bool hBlocked, float hFraction, _, UEObject hActor) =
                        WalkTrace(interp, actor, walk.Hit, feet, feet - new Vector3(0f, 0f, 20f),
                            Vector3.zero, 6);
                    if (hBlocked
                        && ReferenceEquals(hActor ?? level, actor.GetProp(PK.Base) as UEObject)
                        && WalkHoverSettled(hFraction))
                        continue;
                }
                if (StepDownAndGate(interp, actor, region, ref walk, stationarySubstep)
                    == WalkGateResult.Return)
                    return;
                // The Physics loop head does NOT check (0x103e6f30: only
                // remainder and the iteration counter) — the willFall route and a script
                // mode change reach the velocity tail below.
            }

            // The shipped velocity tail (135377-135418): Velocity from the
            // actual displacement over the WHOLE dt — zero for a pawn that
            // never moved (early-out on zero magnitude keeps the projection
            // dormant).
            if (!UEInterpreter.ToBool(actor.GetProp(PK.bJustTeleported)))
                UEPawnMovement.FinishWalkingVelocity(actor, location, dt, walk.FloorNormal);

            if (journalMoving)
                Journal?.Invoke($"walking_move actor={actor.GetProp(PK.Name)} location={actor.GetProp(PK.Location)} velocity={actor.GetProp(PK.Velocity)}");
        }

        // Mutable per-call walking state shared between the substep loop, the
        // moving-branch look-ahead machinery and the common step-down gate.
        struct WalkState
        {
            public float FullDt;        // param_1 — the whole physWalking dt
            public float Remaining;     // fStack_18
            public int Iteration;       // param_2
            public Vector3 FloorNormal; // fStack_16c/168/164
            public Vector3 MoveVel;     // fStack_e8/e4/e0 (with zone velocity)
            public Vector3 PreSubstepLoc; // fStack_1c4/1c0/1bc
            public float Sub;           // fStack_44 — current substep
            public bool MayFallCalled;  // fStack_c0
            public Vector3 AccelDir;    // fStack_8c/88/84
            public float GravSign;      // fStack_d4 (±1 from ZoneGravity.z)
            public bool WillFall;       // fStack_f0 — the look-ahead's fall pre-approval
            public Vector3 Intended;    // fStack_128/124/120 — the delta handed to the move
            public Vector3 Extent;      // uStack_9c/98/94 — GetCylinderExtent(this), ONCE
                                        // at the head (Engine.dll.c:135353, call 0x103e6ea6)
            public MoveCheck Hit;       // uStack_80 — FCheckResult(1.0) ONCE per call
                                        // (135357-135363); all traces and body moves go into it
                                        // (disasm [ebp-0x7c], built 0x103e6ecb..0x103e6ef2)
        }

        enum WalkGateResult { Continue, Return }

        // ── physWalking x87 chains (review round 1). Shipped keeps these on the x87 stack at PC64 and rounds to float
        // only at fstp/fst dword; each helper follows its disasm range. Judge: se_harness/bsp_diff/x87_size_oracle.py
        // (exact fractions, written from the disasm), SelfTest x87_size_corpus. Helpers gated by Fin keep the former
        // float formula for non-finite inputs (their compares use CompareTo/Sign, which refuse NaN); WalkSlopeProbe
        // and the P/Q/R ends run NaN through X87Ext (nan_slope_corpus). float·float and float+float are single roundings in C# and on x87
        // (the exact value fits 64 bits), so only chains through an unrounded register value need X87Ext. ──
        static bool Fin(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);

        // fmul of a float by a register value, fstp dword (a zero product keeps the IEEE sign: X87Ext carries it).
        static float MulF87(float c, X87Ext m) => R64(X87(c) * m).ToFloat();

        // (a.x·b.x + a.y·b.y) + a.z·b.z in the register.
        static X87Ext Dot87(Vector3 a, Vector3 b)
            => R64(R64(X87(a.x) * X87(b.x) + X87(a.y) * X87(b.y)) + X87(a.z) * X87(b.z));

        // moveSize, 0x103e72b5..0x103e72da: (Y·Y + Z·Z) + X·X, fstp qword, appSqrt, fstp dword.
        internal static float WalkMoveSize(Vector3 d)
        {
            if (!Fin(d)) return (float)Math.Sqrt(((double)d.y * d.y + (double)d.z * d.z) + (double)d.x * d.x);
            return Vector3.Size64Ext(d.y, d.z, d.x).ToFloat();
        }

        // B probe depth, 0x103e72dd..0x103e7326: A = ((CH + R) + moveSize) + 4 and B = (CH + MaxStep) + 4 in the
        // register; B < A stores A, otherwise the float B (fst) — each rounded once.
        internal static float WalkProbeDepth(float ch, float r, float moveSize, float maxStep)
        {
            if (!float.IsFinite(ch) || !float.IsFinite(r) || !float.IsFinite(moveSize) || !float.IsFinite(maxStep))
                return Math.Max(ch + r + moveSize + 4f, ch + maxStep + 4f);
            X87Ext a = R64(R64(R64(X87(ch) + X87(r)) + X87(moveSize)) + X87(4f));
            X87Ext b = R64(R64(X87(ch) + X87(maxStep)) + X87(4f));
            return b.CompareTo(a) < 0 ? a.ToFloat() : b.ToFloat();
        }

        // The far-slope half of the C condition, 0x103e7437..0x103e74be: N.z > 0.7 (double 0x10479e48);
        // (CH + MaxStep) + 4 < depth·time in the register; s = appSqrt(1 − N.z²) stored as DOUBLE;
        // (CH + 4) + ((moveSize + R)·s)/N.z < depth·time in the register. A negative 1 − N.z² gives NaN: the
        // compare is unordered (C0 set), the je is not taken and C runs.
        internal static bool WalkFarSlope(float nz, float bTime, float ch, float r, float maxStep, float moveSize, float depth)
        {
            if (!float.IsFinite(nz) || !float.IsFinite(bTime) || !float.IsFinite(ch) || !float.IsFinite(r)
                || !float.IsFinite(maxStep) || !float.IsFinite(moveSize) || !float.IsFinite(depth))
                return nz > 0.7f && ch + maxStep + 4f < depth * bTime
                    && ch + 4f + ((moveSize + r) * MathF.Sqrt(1f - nz * nz)) / nz < depth * bTime;
            if (!((double)nz > 0.7)) return false;
            X87Ext d = X87(depth) * X87(bTime);
            if (!(R64(R64(X87(ch) + X87(maxStep)) + X87(4f)).CompareTo(d) < 0)) return false;
            X87Ext a = R64(X87(1f) - X87(nz) * X87(nz));
            if (a.Sign < 0) return true;
            X87Ext s = X87Ext.From(X87Ext.Sqrt64(X87Ext.From(a.ToDouble())).ToDouble());
            X87Ext q = X87Ext.Div64(R64(R64(X87(moveSize) + X87(r)) * s), X87(nz));
            return R64(R64(X87(ch) + X87(4f)) + q).CompareTo(d) < 0;
        }

        // C probe (135521-135527, 0x103e7423..0x103e75ac): a B miss (time == 1) leads to C JUST LIKE a far gently sloped
        // floor; the ledge is declared only from C's result. cTrace(start, end, extent, flags) answers "hit".
        // Judge: se_harness/bsp_diff/validate_ledge.py (the real fragment under unicorn, HeadlessSmoke --ledgesim).
        internal static bool WalkLedge(bool bHit, float bTime, Vector3 bNormal, float ch, float r, float maxStep, float moveSize,
            float probeDepth, Vector3 aEnd, Vector3 gravDir, float thick, Func<Vector3, Vector3, Vector3, int, bool> cTrace)
        {
            bool ledge = false;
            if (!bHit || WalkFarSlope(bNormal.z, bTime, ch, r, maxStep, moveSize, probeDepth))
                ledge = !cTrace(aEnd, aEnd + gravDir * (maxStep + 4f), new Vector3(thick, thick, ch), 6);
            return ledge;
        }

        // Ledge direction, 0x103e7641..0x103e769e: r = 1/moveSize stored as float, each component float(c·r).
        internal static Vector3 WalkDeltaDir(Vector3 delta, float moveSize)
        {
            float r = 1f / moveSize;
            return new Vector3(delta.x * r, delta.y * r, delta.z * r);
        }

        // D clear sidestep, 0x103e7b30..0x103e7be8: m = min(DesiredSpeed, 0.8f)·GroundSpeed·sub in the register;
        // side = (m·deltaDir.y, (deltaDir.x·−1)·m, 0).
        internal static Vector3 WalkSidestep(Vector3 deltaDir, float desiredSpeed, float groundSpeed, float sub)
        {
            if (!Fin(deltaDir) || !float.IsFinite(desiredSpeed) || !float.IsFinite(groundSpeed) || !float.IsFinite(sub))
                return new Vector3(deltaDir.y, -deltaDir.x, 0f) * (Math.Min(desiredSpeed, 0.8f) * groundSpeed * sub);
            X87Ext m = R64(R64(X87(0.8f > desiredSpeed ? desiredSpeed : 0.8f) * X87(groundSpeed)) * X87(sub));
            return new Vector3(MulF87(deltaDir.y, m), MulF87(-deltaDir.x, m), 0f);
        }

        // D hit, steer along the edge, 0x103e793b..0x103e7b2b: p = SafeNormal(N.y, −N.x, 0); the alignment
        // (p.x·A.x + A.y·p.y) + A.z·p.z stays in the register (negative: p *= −1, recomputed); wall-ish below 0.5 or,
        // for bCanJump, below 0.7 (double constants); at ≥ 0.7 s = GroundSpeed·DesiredSpeed·sub, below it
        // min(DesiredSpeed, 0.8f)·GroundSpeed·sub, each rounded once; adjust = p·s per component.
        internal static (Vector3 adjust, bool wallish) WalkAlongEdge(Vector3 n, Vector3 accelDir, float desiredSpeed,
            float groundSpeed, float sub, bool canJump)
        {
            if (!Fin(n) || !Fin(accelDir) || !float.IsFinite(desiredSpeed) || !float.IsFinite(groundSpeed) || !float.IsFinite(sub))
            {
                Vector3 perp = new Vector3(n.y, -n.x, 0f);
                float pm = perp.magnitude;
                if (pm > 0f) perp /= pm;
                if (Vector3.Dot(perp, accelDir) < 0f) perp = -perp;
                float along = Vector3.Dot(perp, accelDir);
                return (along >= 0.7f ? perp * (groundSpeed * desiredSpeed * sub)
                        : perp * (groundSpeed * Math.Min(desiredSpeed, 0.8f) * sub),
                    along < 0.5f || (canJump && along < 0.7f));
            }
            Vector3 p = new Vector3(n.y, -n.x, 0f).normalized;
            if (Dot87(p, accelDir).Sign < 0) p = -p;
            X87Ext d = Dot87(p, accelDir);
            bool below07 = d.CompareTo(X87Ext.From(0.7)) < 0;
            bool wallish = d.CompareTo(X87Ext.From(0.5)) < 0 || (canJump && below07);
            float s = !below07 ? R64(R64(X87(groundSpeed) * X87(desiredSpeed)) * X87(sub)).ToFloat()
                : R64(R64(X87(0.8f > desiredSpeed ? desiredSpeed : 0.8f) * X87(groundSpeed)) * X87(sub)).ToFloat();
            return (p * s, wallish);
        }

        // D hit with bAvoidLedges, 0x103e788a..0x103e7910: s = ((GroundSpeed·DesiredSpeed)·sub)·−1 rounded once;
        // adjust = (N.x·s, N.y·s, 0.0) — Z is the constant 0.0, not 0·s.
        internal static Vector3 WalkBackOff(Vector3 n, float desiredSpeed, float groundSpeed, float sub)
        {
            if (!float.IsFinite(desiredSpeed) || !float.IsFinite(groundSpeed) || !float.IsFinite(sub))
                return new Vector3(n.x, n.y, 0f) * (-groundSpeed * desiredSpeed * sub);
            float s = -R64(R64(X87(groundSpeed) * X87(desiredSpeed)) * X87(sub)).ToFloat();
            return new Vector3(n.x * s, n.y * s, 0f);
        }

        // Wall-ish non-jumper probes I/K. I end, 0x103e8076..0x103e80e1: moveSize + R in the register, loc +
        // float(deltaDir·it). K, 0x103e81dd..0x103e8262: X, Y float(float(deltaDir·moveSize) + loc); Z keeps
        // deltaDir.z·moveSize + loc.z in the register — the start stores it (fst), the end adds down.z to it.
        internal static (Vector3 iEnd, Vector3 kStart, Vector3 kEnd) WalkStraightProbes(Vector3 loc, Vector3 deltaDir,
            float moveSize, float r, Vector3 down)
        {
            if (!Fin(loc) || !Fin(deltaDir) || !float.IsFinite(moveSize) || !float.IsFinite(r) || !Fin(down))
            {
                Vector3 k0 = loc + deltaDir * moveSize;
                return (loc + deltaDir * (moveSize + r), k0, k0 + down);
            }
            X87Ext l = R64(X87(moveSize) + X87(r));
            Vector3 iEnd = new(MulF87(deltaDir.x, l) + loc.x, MulF87(deltaDir.y, l) + loc.y, MulF87(deltaDir.z, l) + loc.z);
            X87Ext zs = R64(X87(deltaDir.z) * X87(moveSize) + X87(loc.z));
            Vector3 kStart = new(deltaDir.x * moveSize + loc.x, deltaDir.y * moveSize + loc.y, zs.ToFloat());
            return (iEnd, kStart, new Vector3(kStart.x + down.x, kStart.y + down.y, R64(zs + X87(down.z)).ToFloat()));
        }

        // L normal against MinHitWall, 0x103e84b7..0x103e84e1: the dot product in the register < the float.
        internal static bool WalkDotBelow(Vector3 a, Vector3 b, float limit)
        {
            if (!Fin(a) || !Fin(b) || !float.IsFinite(limit)) return Vector3.Dot(a, b) < limit;
            return Dot87(a, b).CompareTo(X87(limit)) < 0;
        }

        // O probe, 0x103e86c8..0x103e87b0: s = appSqrt((Y·Y + Z·Z) + X·X) is never rounded to float; r = 1/s
        // (fdiv st, st(1)); dir = float(c·r); L = max(s + 4, 30f) in the register (`fld 30; fcomp st(1); test ah,1;
        // jne` keeps s + 4 only when 30 is below it); end = float(float(dir·L) + loc) per component.
        // A zero adjust (bHitSlopedWall set and adjust zeroed by the G/H ledge stop or the N probe) has no guard in
        // shipped: s = +0, 1/+0 = +inf (zero divide masked), 0·inf is invalid → dir = the real indefinite 0xFFC00000
        // per component, L = 30 and end = (indefinite, indefinite, indefinite). X87Ext carries that NaN bit for bit.
        internal static (Vector3 dir, Vector3 end) WalkSlopeProbe(Vector3 adjust, Vector3 loc)
        {
            X87Ext s = Vector3.Size64Ext(adjust.y, adjust.z, adjust.x);
            X87Ext rr = X87Ext.Div64(X87(1f), s);
            Vector3 dir = new(MulF87(adjust.x, rr), MulF87(adjust.y, rr), MulF87(adjust.z, rr));
            X87Ext l = R64(s + X87(4f));
            if ((X87Ext.Fcom(X87(30f), l) & X87Ext.C0) == 0) l = X87(30f);
            static float End(float d, X87Ext l, float c) => R64(X87(MulF87(d, l)) + X87(c)).ToFloat();
            return (dir, new Vector3(End(dir.x, l, loc.x), End(dir.y, l, loc.y), End(dir.z, l, loc.z)));
        }

        // The bHitSlopedWall machinery of a move with the flag set, 0x103e86c8..0x103e8e40: O from loc along adjust
        // (WalkSlopeProbe, extent cyl); O on the slope → adjust slides along its z-flattened normal (136079-136093,
        // 0x103e885a-0x103e88f3); otherwise a walking player re-probes P, Q, R from base (WalkPlayerReprobes, zero
        // extent) until one finds the slope — they only rewrite the flag, the move goes without projection
        // (0x103e88f8-0x103e8c58 → jmp 0x103e8e40). Every trace is SingleLineCheck flags 6 into the walk's record; the
        // flag is written after each test (0x103e884e, 0x103e8a82, 0x103e8c4b, 0x103e8e3a), nothing in between reads
        // it, so the caller stores the last value. Judge: se_harness/bsp_diff/nan_slope_oracle.py kind slope.
        internal static Vector3 WalkSlopedWallProbes(Vector3 adjust, Vector3 loc, Vector3 cyl, float ch, float maxStep,
            bool isPlayer, Func<Vector3, Vector3, Vector3, (bool hit, Vector3 normal)> trace, out bool sloped)
        {
            (Vector3 dir, Vector3 oEnd) = WalkSlopeProbe(adjust, loc);
            (bool oHit, Vector3 oNormal) = trace(loc, oEnd, cyl);
            sloped = WalkSlopedWall(oHit, oNormal.z);
            if (sloped) return WalkSlopeProject(adjust, oNormal);
            if (!isPlayer) return adjust;
            (Vector3 baseP, Vector3 pEnd, Vector3 qEnd, Vector3 rEnd) = WalkPlayerReprobes(loc, ch, maxStep, dir);
            foreach (Vector3 probeEnd in new[] { pEnd, qEnd, rEnd })
            {
                (bool pHit, Vector3 pNormal) = trace(baseP, probeEnd, Vector3.zero);
                sloped = WalkSlopedWall(pHit, pNormal.z);
                if (sloped) break;
            }
            return adjust;
        }

        // Walking-player re-probes P/Q/R, 0x103e8967..0x103e8d8c: base Z = ((loc.z − CH) + MaxStep) + 4 rounded once.
        // P = base + dir·100 and Q = base + SafeNormal(dir.x+dir.y, dir.y−dir.x, dir.z)·100 keep Z in the register
        // (n.z·100 + base.z); R = base + SafeNormal(dir.x−dir.y, dir.x+dir.y, dir.z)·100 is float per component.
        // The NaN dir of a zero adjust (WalkSlopeProbe): P ends in indefinites, SafeNormal of the NaN sums is zero
        // (Core 0x1014f504 `fcom 1e-8; test ah,1` takes C0 on unordered), so Q and R end at base.
        internal static (Vector3 start, Vector3 p, Vector3 q, Vector3 r) WalkPlayerReprobes(Vector3 loc, float ch,
            float maxStep, Vector3 dir)
        {
            Vector3 qn = new Vector3(dir.x + dir.y, dir.y - dir.x, dir.z).normalized;
            Vector3 rn = new Vector3(dir.x - dir.y, dir.x + dir.y, dir.z).normalized;
            if (!Fin(loc) || !float.IsFinite(ch) || !float.IsFinite(maxStep))
            {
                Vector3 b0 = new(loc.x, loc.y, loc.z - ch + maxStep + 4f);
                return (b0, b0 + dir * 100f, b0 + qn * 100f, b0 + rn * 100f);
            }
            float bz = R64(R64(R64(X87(loc.z) - X87(ch)) + X87(maxStep)) + X87(4f)).ToFloat();
            Vector3 b = new(loc.x, loc.y, bz);
            Vector3 End(Vector3 n) => new(n.x * 100f + b.x, n.y * 100f + b.y, R64(X87(n.z) * X87(100f) + X87(bz)).ToFloat());
            return (b, End(dir), End(qn), b + rn * 100f);
        }

        // Slide along O's wall, 0x103e885a..0x103e88f3: n = SafeNormal(O.x, O.y, 0); the dot
        // (n.x·a.x + a.y·n.y) + a.z·n.z stays in the register; a_i − float(dot·n_i).
        internal static Vector3 WalkSlopeProject(Vector3 adjust, Vector3 oNormal)
        {
            if (!Fin(adjust) || !Fin(oNormal))
            {
                Vector3 flat = new(oNormal.x, oNormal.y, 0f);
                float fm = flat.magnitude;
                if (fm > 0f) flat /= fm;
                return adjust - flat * Vector3.Dot(adjust, flat);
            }
            Vector3 n = new Vector3(oNormal.x, oNormal.y, 0f).normalized;
            X87Ext d = Dot87(n, adjust);
            return new Vector3(adjust.x - MulF87(n.x, d), adjust.y - MulF87(n.y, d), adjust.z - MulF87(n.z, d));
        }

        // Unused-time give-back of both falling tails, 0x103e90a5..0x103e9110 and 0x103ea0ca..0x103ea145:
        // moved2D = appSqrt(dx·dx + dy·dy) (two terms, order-free) in the register; remaining +
        // (1 − min(moved2D / intended, 1))·sub, one fstp. intended ≠ 0 (both callers branch on it first).
        internal static float WalkRemainingGiveBack(float intended, float dx, float dy, float sub, float remaining)
        {
            if (!float.IsFinite(intended) || !float.IsFinite(dx) || !float.IsFinite(dy) || !float.IsFinite(sub)
                || !float.IsFinite(remaining))
                return remaining + (1f - Math.Min(new Vector3(dx, dy, 0f).magnitude / intended, 1f)) * sub;
            X87Ext ratio = X87Ext.Div64(Vector3.Size64Ext(dx, dy, 0f), X87(intended));
            X87Ext m = X87(1f).CompareTo(ratio) > 0 ? ratio : X87(1f);
            return R64(R64(R64(X87(1f) - m) * X87(sub)) + X87(remaining)).ToFloat();
        }

        // Stationary hover band, 0x103e9952..0x103e9977: time·20 in the register, settled when it is neither above
        // 4.6f nor below 4.1f.
        internal static bool WalkHoverSettled(float time)
        {
            if (!float.IsFinite(time)) return time * 20f >= 4.1f && time * 20f <= 4.6f;
            X87Ext v = X87(time) * X87(20f);
            return !(v.CompareTo(X87(4.6f)) > 0) && !(v.CompareTo(X87(4.1f)) < 0);
        }

        // Step-down drop, 0x103e9a14..0x103e9a23: (MaxStepHeight + 2)·time, one fstp.
        internal static float WalkDropDist(float maxStep, float time)
        {
            if (!float.IsFinite(maxStep) || !float.IsFinite(time)) return (maxStep + 2f) * time;
            return R64(R64(X87(maxStep) + X87(2f)) * X87(time)).ToFloat();
        }

        // 0x103e9b27: the hover lift compares the float drop with the DOUBLE 1.9 (0x1047a5a8).
        internal static bool WalkHoverLift(float dropDist) => (double)dropDist < 1.9;

        // Slope slip, 0x103e9c26..0x103e9e25: N.z < 1 and N.z·GroundFriction < 3.3f in the register; f =
        // max(GroundFriction, 0.5f); S = float(float(float(dt·G)·(1/(f+f)))·dt); dot = (S.z·N.z + S.x·N.x) + S.y·N.y
        // in the register; NN = S − float(dot·N); move when (NN.x·S.x + NN.z·S.z) + NN.y·S.y ≥ 0 (register).
        internal static (bool slip, bool move, Vector3 nonNormal) WalkSlopeSlip(Vector3 n, float groundFriction,
            Vector3 gravity, float dt)
        {
            if (!Fin(n) || !float.IsFinite(groundFriction) || !Fin(gravity) || !float.IsFinite(dt))
            {
                if (!(n.z < 1f && n.z * groundFriction < 3.3f)) return (false, false, Vector3.zero);
                Vector3 slide = gravity * (dt * dt / (2f * Math.Max(groundFriction, 0.5f)));
                Vector3 nn0 = slide - n * Vector3.Dot(slide, n);
                return (true, Vector3.Dot(nn0, slide) >= 0f, nn0);
            }
            if (!(n.z < 1f && R64(X87(n.z) * X87(groundFriction)).CompareTo(X87(3.3f)) < 0)) return (false, false, Vector3.zero);
            float f = 0.5f < groundFriction ? groundFriction : 0.5f;
            X87Ext inv = X87Ext.Div64(X87(1f), R64(X87(f) + X87(f)));
            Vector3 s = new(MulF87(dt * gravity.x, inv) * dt, MulF87(dt * gravity.y, inv) * dt, MulF87(dt * gravity.z, inv) * dt);
            X87Ext d = Dot87(new Vector3(s.z, s.x, s.y), new Vector3(n.z, n.x, n.y));
            Vector3 nn = new(s.x - MulF87(n.x, d), s.y - MulF87(n.y, d), s.z - MulF87(n.z, d));
            return (true, Dot87(new Vector3(nn.x, nn.z, nn.y), new Vector3(s.x, s.z, s.y)).Sign >= 0, nn);
        }

        // Falling-exit velocity, 0x103ea1fb..0x103ea24d: 1/(dt − remaining) in the register, float(d·it) per component.
        // (The former double form gives the same floats unless dt − remaining needs more than 53 bits.)
        internal static Vector3 WalkFallVelocity(Vector3 d, float dt, float remaining)
        {
            if (!Fin(d) || !float.IsFinite(dt) || !float.IsFinite(remaining))
            {
                double inv0 = 1.0 / ((double)dt - remaining);
                return new Vector3((float)(inv0 * d.x), (float)(inv0 * d.y), (float)(inv0 * d.z));
            }
            X87Ext inv = X87Ext.Div64(X87(1f), R64(X87(dt) - X87(remaining)));
            return new Vector3(MulF87(d.x, inv), MulF87(d.y, inv), MulF87(d.z, inv));
        }

        // bHitSlopedWall test of the move, O and P/Q/R (0x103e8edd, 0x103e8802, 0x103e8a36, 0x103e8c01, 0x103e8df0):
        // fcomp 0.01f; test ah,0x41; jne — N.z must be ABOVE 0.01f (the decompile prints "< 0.01", losing the
        // equality); then N.z < 0.7f.
        internal static bool WalkSlopedWall(bool hit, float nz) => hit && nz > 0.01f && nz < 0.7f;

        // ── ONE moving substep (Engine.dll.c:135442-136195): the shipped
        // NPC/walking-player ledge look-ahead, the adjusted move with the
        // bHitSlopedWall machinery, stepUp on a block with the falling/flying
        // handoffs, the swim check and the walking-player auto-jump gate.
        //
        // Look-ahead (LAB_103e71d1, NPCs + bIsWalking players, non-flyers):
        //   A: point probe to (Loc + delta + accelDir·r·(bAvoidLedges?1:0.5))
        //      — a blocked A skips the whole machinery (plain move).
        //   B: point probe DOWN from A's end, depth max(CH+r+|delta|+4,
        //      CH+MaxStep+4); a walkable floor within reach cancels the ledge
        //      handling. A B MISS or the far-floor slope allowance re-probes C:
        //      thick (min(r/2,14), ·, CH) box down MaxStep+4 — the ledge is
        //      declared only when C misses too (0x103e7423-0x103e7431).
        //   Everything below (D…N, the player G/H, MayFall) runs ONLY on a
        //      ledge: no ledge jumps straight to the move (0x103e75ac →
        //      0x103e86a0).
        //   Ledge found → D: point probe from the destination edge
        //      (edge + g·(CH+6)) to Location + g·(CH+6) − accelDir·2r
        //      (0x103e7764-0x103e77fc):
        //      - D clear (open air both sides): perpendicular SIDESTEP
        //        (deltaDir.y, −deltaDir.x, 0)·min(DesiredSpeed,0.8)·
        //        GroundSpeed·sub, written into the adjust BEFORE the E probe;
        //        E forward + F floor cylinder probes — both clear flips the
        //        sidestep sign blindly, a hit on either keeps the sidestep
        //        (135598-135641, 0x103e7be8-0x103e7c0c).
        //      - D hit, !bAvoidLedges: steer ALONG the edge — the
        //        horizontal perpendicular of the edge normal signed toward
        //        accelDir, wall-ish when the alignment < 0.5 (or < 0.7 for
        //        a bCanJump pawn), speed clamped to min(DesiredSpeed,0.8)
        //        below 0.7 alignment (135643-135684).
        //      - D hit, bAvoidLedges: back off along the horizontal normal,
        //        MoveTimer −0.25 (−1 with bStopAtLedges) (135686-135703).
        //   Walking player (LAB_103e7d25): forward+floor cylinder probes of
        //      the adjust; both clear → Acceleration and the adjust zeroed
        //      (the walking player stops at the ledge) (135705-135751).
        //   MayFall pre-approval (LAB_103e7f53): a bCanJump pawn on a
        //      wall-ish edge consults the probed MayFall ONCE; if bCanJump
        //      survives, willFall=1 and the move reverts to accelDir·|delta|
        //      — the gate below then falls WITHOUT the displacement-velocity
        //      correction (135755-135787).
        //   Non-jumper (bCanJump re-read LIVE at 0x103e805f, so a MayFall
        //      veto lands here too), wall-ish only (135789-135851): I/J/K
        //      probes — an I HIT (wall ahead) or a J+K floor both walk
        //      straight at deltaDir·|delta| (0x103e8123-0x103e8131,
        //      0x103e82ce-0x103e8323); otherwise the appFrand MoveTimer
        //      jitter (r ≥ 2·sub → −0.1, else −1). Then, wall-ish OR NOT
        //      (0x103e806e je 0x103e835e), unless the sidestep was probed:
        //      the L/M adjust probes (a head-on wall below the MinHitWall
        //      alignment aborts via MoveTimer=−1) and the N self-floor probe
        //      → Acceleration zeroed, remaining zeroed, back-off or freeze
        //      (135853-135918).
        // Move (LAB_103e86bb): bHitSlopedWall clear → plain move, the flag
        //   recomputed from the hit (0.01 ≤ n.z < 0.7). Flag set → the O
        //   probe (dir·max(dist+4,30)); a slope wall found BY O projects the
        //   delta off O's z-flattened normal (0x103e88c0-0x103e88f3,
        //   136079-136093). O finding none: players re-probe P/Q/R (100-unit
        //   point probes at feet+MaxStep+4 along dir and both diagonals),
        //   which only re-set the flag — the move goes unprojected
        //   (0x103e88f8-0x103e8c58 → jmp 0x103e8e40).
        // Post (136100-136195): blocked → stepUp; a stepUp that left the
        //   pawn falling gives back the unmoved 2D fraction, fires Falling
        //   and hands the remainder to physFalling THIS tick; a flying exit
        //   sets Velocity=(0,0,AirSpeed)/Acceleration=(0,0,AccelRate) and
        //   hands the remainder to physFlying (136141-136158, a branch added by
        //   KnowWonder); PHYS_Projectile returns; PHYS_Swimming is a loud
        //   STOP; ANY OTHER mode (Walking, Flying set by a HitWall handler,
        //   None…) continues into the auto-jump and step-down below —
        //   shipped has no «Physics≠Walking → return» here (0x103e9222 →
        //   0x103e9293); the bAutoJump walking-player ledge jump is ported
        //   (UELevelRuntime.Trajectory.cs). ──
        WalkGateResult MovingSubstep(UEInterpreter interp, UEObject actor, Vector3 delta,
            bool isPlayer, ref WalkState walk)
        {
            float r = UEInterpreter.AsFloat(actor.GetProp(PK.CollisionRadius));
            float ch = UEInterpreter.AsFloat(actor.GetProp(PK.CollisionHeight));
            float maxStep = UEInterpreter.AsFloat(actor.GetProp(PK.MaxStepHeight));
            float groundSpeed = UEInterpreter.AsFloat(actor.GetProp(PK.GroundSpeed));
            float desiredSpeed = UEInterpreter.AsFloat(actor.GetProp(PK.DesiredSpeed));
            bool avoidLedges = UEInterpreter.ToBool(actor.GetProp(PK.bAvoidLedges));
            bool canJump = UEInterpreter.ToBool(actor.GetProp(PK.bCanJump));
            Vector3 gravDir = new(0f, 0f, walk.GravSign);
            // Shipped runs probes E/F/G/I/J/K/L/M/O with extent uStack_9c —
            // GetCylinderExtent (135624…135965), not (R, R, H).
            Vector3 cyl = walk.Extent;
            Vector3 loc = walk.PreSubstepLoc;
            Vector3 accelDir = walk.AccelDir;
            Vector3 adjust = delta;
            Vector3 lookAhead = accelDir * (r * (avoidLedges ? 1f : 0.5f));
            Vector3 downStep = gravDir * (maxStep + 6f);
            bool wallish = false, probedSide = false;
            // A moving substep has a component of at least 1e-4 (0x103e706c..0x103e70d0), so moveSize > 0: shipped has
            // no guard (0x103e7641), the one here is never taken.
            float moveSize = WalkMoveSize(delta);
            Vector3 deltaDir = moveSize > 0f ? WalkDeltaDir(delta, moveSize) : Vector3.zero;
            bool useMachinery = !isPlayer || UEInterpreter.ToBool(actor.GetProp(PK.bIsWalking));
            bool runMayFallBlock = false;

            if (useMachinery && !UEInterpreter.ToBool(actor.GetProp(PK.bCanFly)))
            {
                (bool aHit, _, _, _) = WalkTrace(interp, actor, walk.Hit, loc,
                    loc + delta + lookAhead, Vector3.zero, 6);
                if (!aHit)
                {
                    Vector3 aEnd = loc + delta + lookAhead;
                    float probeDepth = WalkProbeDepth(ch, r, moveSize, maxStep);
                    (bool bHit, float bTime, Vector3 bNormal, _) = LineTrace(interp, actor,
                        aEnd, aEnd + gravDir * probeDepth, Vector3.zero, 6);
                    float thick = Math.Min(r * 0.5f, 14f);
                    MoveCheck hitRecord = walk.Hit;
                    bool ledge = WalkLedge(bHit, bTime, bNormal, ch, r, maxStep, moveSize, probeDepth, aEnd, gravDir, thick,
                        (s, e, x, f) => { (bool h, _, _, _) = WalkTrace(interp, actor, hitRecord, s, e, x, f); return h; });
                    // No ledge — straight to the move (0x103e75ac je 0x103e86a0): no
                    // D…N, no player G/H, no MayFall.
                    if (ledge)
                    {
                        runMayFallBlock = true;
                        Vector3 edgeBase = loc + accelDir * moveSize + lookAhead;
                        Vector3 dStart = edgeBase + gravDir * (ch + 6f);
                        // D's end is from Location, not from the edge (135565-135592,
                        // 0x103e7764-0x103e77fc: [edi] = this+0xfc).
                        Vector3 dEnd = loc + gravDir * (ch + 6f) - accelDir * (2f * r);
                        (bool dHit, _, Vector3 dNormal, _) = LineTrace(interp, actor,
                            dStart, dEnd, Vector3.zero, 6);
                        if (!dHit)
                        {
                            // Open air on the near side too — blind sidestep.
                            wallish = true;
                            Vector3 side = WalkSidestep(deltaDir, desiredSpeed, groundSpeed, walk.Sub);
                            // The sidestep is written into adjust BEFORE the E probe
                            // (0x103e7ba8-0x103e7be8) and stays when E/F hit.
                            adjust = side;
                            (bool eHit, _, _, _) = WalkTrace(interp, actor, walk.Hit, loc,
                                loc + side, cyl, 6);
                            if (!eHit)
                            {
                                (bool fHit, _, _, _) = WalkTrace(interp, actor, walk.Hit, loc + side,
                                    loc + side + downStep, cyl, 6);
                                if (!fHit) { adjust = -side; goto ApplyAdjust; }
                            }
                            probedSide = true;
                        }
                        else if (!avoidLedges)
                        {
                            // Steer along the edge.
                            (adjust, wallish) = WalkAlongEdge(dNormal, accelDir, desiredSpeed, groundSpeed, walk.Sub, canJump);
                        }
                        else
                        {
                            // bAvoidLedges: back off along the horizontal normal.
                            wallish = false;
                            adjust = WalkBackOff(dNormal, desiredSpeed, groundSpeed, walk.Sub);
                            if (UEInterpreter.ToBool(actor.GetProp(PK.bStopAtLedges)))
                                actor.Props["MoveTimer"] = -1f;
                            else
                                actor.Props["MoveTimer"] =
                                    UEInterpreter.AsFloat(actor.GetProp(PK.MoveTimer)) - 0.25f;
                        }
                    }
                ApplyAdjust:
                    // Player G/H — only at a ledge (block 135705-135751 inside the
                    // 0x103e75ac branch); the goto reaches here only from the ledge branch.
                    if (ledge && isPlayer)
                    {
                        // The walking player stops accelerating into open air
                        // (135705-135751).
                        wallish = false;
                        if (!probedSide)
                        {
                            (bool gHit, _, _, _) = WalkTrace(interp, actor, walk.Hit, loc,
                                loc + adjust + lookAhead, cyl, 6);
                            if (!gHit)
                            {
                                Vector3 gEnd = loc + adjust + lookAhead;
                                (bool hHit, _, _, _) = WalkTrace(interp, actor, walk.Hit, gEnd,
                                    gEnd + downStep, new Vector3(thick, thick, ch), 6);
                                if (!hHit)
                                {
                                    actor.Props["Acceleration"] = Vector3.zero;
                                    adjust = Vector3.zero;
                                }
                            }
                        }
                    }
                }
            }

            if (runMayFallBlock)
            {
                // LAB_103e7f53 — MayFall pre-approval / the non-jumper wall
                // machinery. Only reached on a ledge (see above).
                if (canJump && wallish)
                {
                    // UObject::IsProbing(NAME_MayFall) (0x103e7f70-0x103e7f90: FName::Names[230], probe bit 33), not
                    // "a MayFall function exists": a state whose probe mask lacks MayFall walks straight (the else arm).
                    bool probed = UEInterpreter.IsProbing(actor, "MayFall");
                    if (!probed)
                    {
                        adjust = accelDir * moveSize;
                    }
                    else if (!walk.MayFallCalled)
                    {
                        walk.MayFallCalled = true;
                        wallish = false;   // 0x103e7fe8: xor edi, edi — before the call
                        Guard(() => UELifecycle.CallEvent(interp, actor, "MayFall"), "MayFall");
                        if (UEInterpreter.ToBool(actor.GetProp(PK.bCanJump)))
                        {
                            walk.WillFall = true;
                            adjust = accelDir * moveSize;
                        }
                    }
                }
                // 0x103e805f: bCanJump is re-read LIVE — a MayFall veto
                // (0x103e8009-0x103e8012 → 0x103e806e) lands here too.
                if (!UEInterpreter.ToBool(actor.GetProp(PK.bCanJump)))
                {
                    bool walkStraight = false;
                    if (wallish)   // 0x103e806e: test edi; je 0x103e835e
                    {
                        // I/J/K: an I hit (wall ahead) OR floor at J and K → walk
                        // straight (0x103e8123-0x103e8131 → 0x103e82ce-0x103e8323);
                        // stepUp will climb it.
                        (Vector3 iEnd, Vector3 kStart, Vector3 kEnd) = WalkStraightProbes(loc, deltaDir, moveSize, r, downStep);
                        (bool iHit, _, _, _) = WalkTrace(interp, actor, walk.Hit, loc,
                            iEnd, cyl, 6);
                        if (iHit) walkStraight = true;
                        else
                        {
                            (bool jHit, _, _, _) = WalkTrace(interp, actor, walk.Hit, iEnd,
                                iEnd + downStep, cyl, 6);
                            if (jHit)
                            {
                                (bool kHit, _, _, _) = WalkTrace(interp, actor, walk.Hit, kStart,
                                    kEnd, cyl, 6);
                                walkStraight = kHit;
                            }
                        }
                        if (walkStraight)
                            adjust = deltaDir * moveSize;
                        else
                        {
                            // The shipped MoveTimer jitter (135844-135851) — the
                            // ONE shared appFrand stream.
                            if (FrandHook is null)
                                throw new NotSupportedException(
                                    $"physWalking wall jitter reached (Engine.dll.c:135844) actor={actor.GetProp(PK.Name)} without FrandHook");
                            // 0x103e832e: fstp dword — the comparison uses the float store.
                            actor.Props["MoveTimer"] = FrandHook().ToFloat() >= walk.Sub + walk.Sub
                                ? UEInterpreter.AsFloat(actor.GetProp(PK.MoveTimer)) - 0.1f
                                : -1f;
                        }
                    }
                    // L/M/N — with any wallish (0x103e835e), except "walk straight"
                    // and an already probed sidestep (fStack_90).
                    if (!walkStraight && !probedSide)
                    {
                        (bool lHit, _, Vector3 lNormal, _) = WalkTrace(interp, actor, walk.Hit, loc,
                            loc + adjust + lookAhead, cyl, 6);
                        bool clear = !lHit;
                        if (!lHit)
                        {
                            Vector3 lEnd = loc + adjust + lookAhead;
                            (bool mHit, _, _, _) = WalkTrace(interp, actor, walk.Hit, lEnd,
                                lEnd + downStep, cyl, 6);
                            clear = !mHit;
                        }
                        else if (WalkDotBelow(deltaDir, lNormal, UEInterpreter.AsFloat(actor.GetProp(PK.MinHitWall))))
                            actor.Props["MoveTimer"] = -1f;
                        if (clear)
                        {
                            float thickN = Math.Min(r * 0.5f, 14f);
                            (bool nHit, _, _, _) = WalkTrace(interp, actor, walk.Hit, loc,
                                loc + downStep, new Vector3(thickN, thickN, ch), 6);
                            actor.Props["Acceleration"] = Vector3.zero;
                            walk.Remaining = 0f;
                            actor.Props["MoveTimer"] = -1f;
                            adjust = nHit ? Vector3.zero
                                : deltaDir * (-groundSpeed * walk.Sub);
                        }
                    }
                }
            }

            // ── The move (LAB_103e86bb) with the bHitSlopedWall machinery. ──
            walk.Intended = adjust;
            float fraction; Vector3 normal; UEObject hitActor;
            if (!UEInterpreter.ToBool(actor.GetProp(PK.bHitSlopedWall)))
            {
                (fraction, normal, hitActor) = WalkMove(interp, actor, adjust, walk.Hit);
                actor.Props["bHitSlopedWall"] = WalkSlopedWall(fraction < 1f, normal.z);
            }
            else
            {
                MoveCheck record = walk.Hit;
                adjust = WalkSlopedWallProbes(adjust, loc, cyl, ch, maxStep, isPlayer, (start, end, extent) =>
                {
                    (bool hit, _, Vector3 n, _) = WalkTrace(interp, actor, record, start, end, extent, 6);
                    return (hit, n);
                }, out bool sloped);
                actor.Props["bHitSlopedWall"] = sloped;
                (fraction, normal, hitActor) = WalkMove(interp, actor, adjust, walk.Hit);
            }
            if (TraceMovesNow)
                Log($"walking_sweep actor={actor.GetProp(PK.Name)} from={walk.PreSubstepLoc}"
                    + $" delta={adjust} to={actor.GetProp(PK.Location)} fraction={fraction:R} normal={normal}"
                    + $" hit={hitActor?.GetProp(PK.Name)}");

            // ── Post (136100-136195). ──
            if (fraction < 1f)
            {
                if (normal.z > 0.7f) walk.FloorNormal = normal;
                Vector3 slideDir = adjust.sqrMagnitude > 0f ? adjust.normalized : Vector3.zero;
                StepUp(interp, actor, gravDir, slideDir, adjust * (1f - fraction), walk.Hit);
                int phys = UEInterpreter.ToInt(actor.GetProp(PK.Physics));
                if (phys == 2)
                {
                    // A script turned the pawn falling inside the step: give
                    // back the unmoved 2D fraction, fire Falling and hand the
                    // remainder to physFalling THIS tick (136114-136140).
                    // A zero |Intended| gives inf or NaN in shipped (0x103e90da), both replaced by 1: remaining unchanged.
                    float intended = walk.Intended.magnitude;
                    Vector3 nowLoc = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
                    if (intended > 0f)
                        walk.Remaining = WalkRemainingGiveBack(intended, nowLoc.x - walk.PreSubstepLoc.x,
                            nowLoc.y - walk.PreSubstepLoc.y, walk.Sub, walk.Remaining);
                    Guard(() => UELifecycle.CallEvent(interp, actor, "Falling"), "Falling");
                    if (UEInterpreter.ToInt(actor.GetProp(PK.Physics)) == 2)
                    {
                        if (walk.Remaining > 0.01f)
                            PhysFalling(interp, actor, walk.Remaining, walk.Iteration);
                        return WalkGateResult.Return;
                    }
                    // ── WALKING TO FLYING HANDOFF (Engine.dll.c:136141-136158) ──
                    //
                    // A KnowWonder addition — in stock UE1 physWalking simply returns after
                    // Falling. Body read byte by byte, offsets resolved by the layout oracle
                    // (audit/prop_offsets.py over the Pawn dump):
                    //
                    //   +0x12c/+0x130/+0x134 = Actor.Velocity     (x/y/z)
                    //   +0x13c/+0x140/+0x144 = Actor.Acceleration (x/y/z)
                    //   +0x2c4 = Pawn.AirSpeed,  +0x2c8 = Pawn.AccelRate
                    //
                    //   Velocity     = (0, 0, AirSpeed);
                    //   Acceleration = (0, 0, AccelRate);
                    //   if (remaining <= 0.01) return;
                    //   physFlying(remaining, Iterations);
                    //
                    // The 0.01 threshold and its place match the falling branch just above;
                    // both come AFTER the Falling event, and the order matters: the script
                    // may change physics in the handler, and its choice is what these two
                    // comparisons read.
                    if (UEInterpreter.ToInt(actor.GetProp(PK.Physics)) == 4)
                    {
                        actor.Props["Velocity"] = new Vector3(0f, 0f,
                            UEInterpreter.AsFloat(actor.GetProp(PK.AirSpeed)));
                        actor.Props["Acceleration"] = new Vector3(0f, 0f,
                            UEInterpreter.AsFloat(actor.GetProp(PK.AccelRate)));
                        // STATUS: PORTED only — no activating fixture, and reachability in
                        // the campaign is NOT ESTABLISHED (a `Falling` handler cannot be found
                        // by searching extracted/Scripts — Engine classes there are import
                        // stubs). So the branch is LOUD: the first live hit shows in the
                        // journal, like the neighbouring `walking_fall`.
                        Log($"walking_fly actor={actor.GetProp(PK.Name)} "
                            + $"velocity={actor.GetProp(PK.Velocity)} "
                            + $"acceleration={actor.GetProp(PK.Acceleration)} "
                            + $"remaining={walk.Remaining}");
                        if (walk.Remaining > 0.01f)
                            PhysFlying(interp, actor, walk.Remaining);
                        return WalkGateResult.Return;
                    }
                    return WalkGateResult.Return;
                }
                if (phys == 6) return WalkGateResult.Return;
                // Other modes (except 2/6 above and 3 below) do NOT exit: shipped goes on
                // to auto-jump and step-down (0x103e9222 → 0x103e9293, 136159-136176).
            }
            if (UEInterpreter.ToInt(actor.GetProp(PK.Physics)) == 3)
                throw new NotSupportedException(
                    $"physWalking startSwimming reached (Engine.dll.c:136170) actor={actor.GetProp(PK.Name)} — port the shipped swim entry before using it");
            // bAutoJump BRANCH (Engine.dll.c:136196-136305) — UELevelRuntime.Trajectory.cs,
            // matched bit for bit against the real physWalking fragment (validate_autojump.py).
            // Armed by the "Auto Jump" options menu checkbox (PlayerPawn.AutoJump); the flag
            // defaults to false, the ini does not set it.
            if (isPlayer && UEInterpreter.ToBool(actor.GetProp(PK.bAutoJump)))
            {
                LineCheckHit last = default;
                // Traces of the branch and the trajectory simulator go into uStack_80
                // (136272-136291, FUN_103e6310(&uStack_80, ...)).
                MoveCheck walkHit = walk.Hit;
                UEObject zone = (actor.GetProp(PK.Region) as UEObject)?.GetProp(PK.Zone) as UEObject ?? interp.GetLevelInfo();
                AutoJumpResult jump = AutoJumpGate(true, true, false,
                    UEInterpreter.AsVector3(actor.GetProp(PK.Location)), UEInterpreter.AsVector3(actor.GetProp(PK.Velocity)),
                    UEInterpreter.AsRotator(actor.GetProp(PK.Rotation)), UEInterpreter.AsFloat(actor.GetProp(PK.CollisionRadius)),
                    UEInterpreter.AsFloat(actor.GetProp(PK.CollisionHeight)), walk.Extent, UEInterpreter.AsFloat(actor.GetProp(PK.JumpZ)),
                    UEInterpreter.AsFloat(actor.GetProp(PK.MaxMountHeight)), UEInterpreter.AsVector3(zone.GetProp(PK.ZoneGravity)),
                    walk.AccelDir, new Vector3(0f, 0f, (maxStep + 2f) * walk.GravSign), walk.Sub, walk.Remaining,
                    (from, to, ext) =>
                    {
                        last = SingleLineCheck(interp, actor, null, from, to, ext, 7);
                        walkHit.FromTrace(last);
                        return (!last.Blocked, last.Time, last.Normal);
                    },
                    maxExtent =>
                    {
                        UEObject hitActor = last.Actor;
                        if (hitActor is null || ReferenceEquals(hitActor, interp.GetLevelInfo()))
                            return Bsp?.HitSurfaceFlags(last.Node, last.Location, last.Normal, maxExtent, null);
                        if (hitActor.GetProp(PK.Brush) is not null || UEInterpreter.ClassIsA(hitActor.Class, "Mover"))
                            return BrushModelOf(hitActor)?.HitSurfaceFlags(last.Node, last.Location, last.Normal, maxExtent,
                                BrushToLocalCoords(hitActor));
                        return null;                       // FCheckResult.Primitive is not UModel
                    });
                if (jump.Exit == AutoJumpExit.Jump)
                {
                    MoveActor(interp, actor, jump.MoveDelta, UEInterpreter.AsRotator(actor.GetProp(PK.Rotation)),
                        false, false, false, false);
                    Guard(() => UELifecycle.CallEvent(interp, actor, "DoJump", 1f), "DoJump");
                    walk.Remaining = jump.Remaining;
                    // LAB_103ea28d: physFalling(Remaining, Iterations); return.
                    PhysFalling(interp, actor, walk.Remaining, walk.Iteration);
                    return WalkGateResult.Return;
                }
            }
            return WalkGateResult.Continue;
        }

        // ── The COMMON physWalking step-down + no-floor gate, shared by the
        // stationary and moving substeps (Engine.dll.c:136333-136544).
        //  1. TRACE (not a move) (MaxStepHeight+2) along the gravity sign
        //     with the cylinder extent, flags 7 (136333-136341).
        //  2. A hit on the CURRENT Base with a drop distance ≤ 2.4 is the
        //     hover band (136367→LAB_103e9b27): below 1.9 the pawn is lifted
        //     by (2.1 − drop) and the hit time zeroed — pawns HOVER ~2 units
        //     above the hull contact; inside [1.9, 2.4] nothing moves.
        //  3. Any other hit COMMITS the drop (MoveActor 136368) and re-bases
        //     onto the move's hit actor (SetBase 136371-136373) — even a
        //     steep one; the gate below then takes it away via setPhysics.
        //  4. The gate (LAB_103e9bf0, 136383-136414): no hit OR Normal.z<0.7
        //     → the pawn may not stand here. bCanJump pawns consult MayFall
        //     ONCE per call if the event is probed (the script may clear
        //     bCanJump); then !bCanJump || bIsWalking = FULL STOP (Velocity/
        //     Acceleration zeroed, FarMoveActor back to OldLocation,
        //     MoveTimer=-1 — HP1 pawns default bCanJump=False, so NPCs stop
        //     at ledges; only jump-capable pawns fall off). Otherwise the
        //     Falling exit (LAB_103ea025, 136472-136529): bHitSlopedWall on
        //     a steep hit, unused substep time given back by the 2D travel
        //     over |Intended| (the post-look-ahead delta), Velocity.z=0, the
        //     Falling event, setPhysics(PHYS_Falling, NULL), xy velocity from the actual displacement, then
        //     physFalling with the REMAINING time — the SAME tick (136528).
        //  5. A walkable floor runs the slope-slip (136415-136454): on a
        //     non-flat floor with Normal.z·GroundFriction < 3.3 the pawn
        //     slides by Gravity·dt²/(2·max(GroundFriction, 0.5)) minus the
        //     floor-normal component.
        //  6. The willFall route (fStack_f0, set by the moving branch's
        //     MayFall pre-approval): the gate is BYPASSED — the falling exit
        //     runs without the displacement-velocity correction and without
        //     the same-tick physFalling; a final move consumes the remaining
        //     delta and the loop ends into the velocity tail (136383 +
        //     136502/136532-136542). ──
        WalkGateResult StepDownAndGate(UEInterpreter interp, UEObject actor, UEObject region,
            ref WalkState walk, bool stationary)
        {
            UEObject zone = region?.GetProp(PK.Zone) as UEObject;
            Vector3 gravity = UEInterpreter.AsVector3(zone?.GetProp(PK.ZoneGravity));
            float downSign = gravity.z > 0f ? 1f : -1f;
            float maxStep = UEInterpreter.AsFloat(actor.GetProp(PK.MaxStepHeight));
            float radius = UEInterpreter.AsFloat(actor.GetProp(PK.CollisionRadius));
            float height = UEInterpreter.AsFloat(actor.GetProp(PK.CollisionHeight));
            Vector3 stepDown = new(0f, 0f, downSign * (maxStep + 2f));
            UEObject level = interp.GetLevelInfo();

            Vector3 loc = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
            // Extent — uStack_9c (GetCylinderExtent, 136341).
            (bool blocked, float time, Vector3 normal, UEObject hitActor) =
                WalkTrace(interp, actor, walk.Hit, loc, loc + stepDown, walk.Extent, 7);
            if (!blocked) time = 1f;
            float dropDist = WalkDropDist(maxStep, time);
            if (TraceMovesNow)
                Log($"walking_step_down actor={actor.GetProp(PK.Name)} location={loc}"
                    + $" fraction={time:R} drop={dropDist:R} normal={normal}"
                    + $" hit={hitActor?.GetProp(PK.Name)}");
            if (time < 1f)
            {
                if (normal.z > 0.7f) walk.FloorNormal = normal;
                UEObject curBase = actor.GetProp(PK.Base) as UEObject;
                if (ReferenceEquals(hitActor ?? level, curBase) && dropDist <= 2.4f)
                {
                    // Hover band (LAB_103e9b27): lift by (2.1 − drop) when the
                    // gap shrank below 1.9; the shipped code saves/restores the
                    // trace normal around the lift and zeroes the hit time.
                    if (WalkHoverLift(dropDist))
                    {
                        WalkMove(interp, actor, new Vector3(0f, 0f, 2.1f - dropDist), walk.Hit);
                        time = 0f;
                        // The FCheckResult itself too (0x103e9b27..: fStack_5c = 0,
                        // trace normal returned into the record; Actor from the move).
                        walk.Hit.Time = 0f;
                        walk.Hit.Normal = normal;
                    }
                }
                else
                {
                    // Commit the drop; the MOVE's hit feeds the gate and the
                    // re-base, exactly like shipped's shared FCheckResult —
                    // including SetBase(NULL) when the move found nothing
                    // (136371 compares the move's Actor, NULL on a clean
                    // move). The hook reports null for BOTH world hits and
                    // clean moves; the fraction disambiguates. The Hit is the
                    // one the floor trace just filled (0xc4 then 0x8c on the
                    // same &uStack_80, 136343/136367): a REFUSED or fast-path
                    // move leaves the trace result for the gate — walk.Hit
                    // carries it (WalkTrace above wrote it).
                    (time, normal, hitActor) = WalkMove(interp, actor, stepDown, walk.Hit);
                    UEObject floorActor = time < 1f ? hitActor ?? level : null;
                    if (!ReferenceEquals(floorActor, actor.GetProp(PK.Base) as UEObject))
                        SetBase(interp, actor, floorActor, notify: true);
                    if (UEInterpreter.ToInt(actor.GetProp(PK.Physics)) == 3)
                        throw new NotSupportedException(
                            $"physWalking startSwimming reached (Engine.dll.c:136377) actor={actor.GetProp(PK.Name)} — port the shipped swim entry before using it");
                }
            }

            // LAB_103e9bf0 — the no-floor gate. The willFall route (the
            // moving branch's MayFall pre-approval) bypasses it and goes
            // STRAIGHT to the falling exit (136383-136384).
            if (walk.WillFall || time >= 1f || normal.z < 0.7f)
            {
                if (!walk.WillFall)
                {
                    bool canJump = UEInterpreter.ToBool(actor.GetProp(PK.bCanJump));
                    if (canJump && !walk.MayFallCalled
                        && UEInterpreter.IsProbing(actor, "MayFall"))   // UObject::IsProbing, 0x103e9f02-0x103e9f22
                    {
                        walk.MayFallCalled = true;
                        Guard(() => UELifecycle.CallEvent(interp, actor, "MayFall"), "MayFall");
                        canJump = UEInterpreter.ToBool(actor.GetProp(PK.bCanJump));
                    }
                    if (!canJump || UEInterpreter.ToBool(actor.GetProp(PK.bIsWalking)))
                    {
                        // FULL STOP at the ledge (136396-136411).
                        actor.Props["Velocity"] = Vector3.zero;
                        actor.Props["Acceleration"] = Vector3.zero;
                        FarMoveTo(interp, actor, UEInterpreter.AsVector3(actor.GetProp(PK.OldLocation)));
                        actor.Props["MoveTimer"] = -1f;
                        Log($"walking_ledge_stop actor={actor.GetProp(PK.Name)} location={actor.GetProp(PK.Location)}");
                        return WalkGateResult.Return;
                    }
                }
                // The Falling exit (LAB_103ea025).
                if (time < 1f) actor.Props["bHitSlopedWall"] = true;
                // Divisor is |Intended| (fStack_128 = delta AFTER the ledge
                // machinery, 136476-136490, 0x103ea043-0x103ea07a).
                float speed = walk.Intended.magnitude;
                Vector3 nowLoc = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
                if (speed == 0f) walk.Remaining = 0f;
                else walk.Remaining = WalkRemainingGiveBack(speed, nowLoc.x - walk.PreSubstepLoc.x,
                    nowLoc.y - walk.PreSubstepLoc.y, walk.Sub, walk.Remaining);
                Vector3 v = UEInterpreter.AsVector3(actor.GetProp(PK.Velocity));
                v.z = 0f;
                actor.Props["Velocity"] = v;
                Guard(() => UELifecycle.CallEvent(interp, actor, "Falling"), "Falling");
                if (UEInterpreter.ToInt(actor.GetProp(PK.Physics)) == 1)
                    SetPhysics(interp, actor, 2, null);
                Log(stationary
                    ? $"walking_fall actor={actor.GetProp(PK.Name)}"
                    : $"walking_falloff actor={actor.GetProp(PK.Name)}");
                if (!walk.WillFall && UEInterpreter.ToInt(actor.GetProp(PK.Physics)) == 2)
                {
                    // 136502-136529: xy velocity from the ACTUAL displacement
                    // over the consumed time (z keeps the post-event value —
                    // shipped saves it at 136503 and restores at 136522),
                    // then hand the remaining time to physFalling this tick.
                    Vector3 vel = UEInterpreter.AsVector3(actor.GetProp(PK.Velocity));
                    float zAfterEvent = vel.z;
                    if (!UEInterpreter.ToBool(actor.GetProp(PK.bJustTeleported))
                        && walk.Remaining < walk.FullDt)
                    {
                        // Location is read HERE, after the Falling event (Engine.dll.c:
                        // 136507-136510): the handler may have moved the pawn. Differences
                        // are float (FVector arguments), 1/(dt − rem) stays in the register.
                        Vector3 afterLoc = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
                        Vector3 oldLoc = UEInterpreter.AsVector3(actor.GetProp(PK.OldLocation));
                        vel = WalkFallVelocity(afterLoc - oldLoc, walk.FullDt, walk.Remaining);
                    }
                    vel.z = zAfterEvent;
                    actor.Props["Velocity"] = vel;
                    if (walk.Remaining > 0.01f)
                        PhysFalling(interp, actor, walk.Remaining, walk.Iteration);
                    return WalkGateResult.Return;
                }
                // The willFall route, or the Falling event moved the pawn OFF
                // falling (136532-136542): one final move with the whole
                // remaining delta, then the loop ends into the velocity tail
                // (the loop head does not test Physics).
                WalkMove(interp, actor, walk.MoveVel * walk.Remaining, walk.Hit);
                walk.Remaining = 0f;
                return WalkGateResult.Continue;
            }

            // Walkable floor: the slope-slip (136415-136454). The zone is the CURRENT one
            // (this+0xa0 at substep time, 136416 / 0x103e9c3a), not the one taken at the
            // call head: the substep move may have changed Region.
            UEObject slipZone = (actor.GetProp(PK.Region) as UEObject)?.GetProp(PK.Zone) as UEObject;
            float groundFriction = UEInterpreter.AsFloat(slipZone?.GetProp(PK.ZoneGroundFriction));
            (bool slip, bool slipMove, Vector3 nonNormal) = WalkSlopeSlip(normal, groundFriction,
                UEInterpreter.AsVector3(slipZone?.GetProp(PK.ZoneGravity)), walk.FullDt);
            if (slip)
            {
                if (slipMove)
                    WalkMove(interp, actor, nonNormal, walk.Hit);
                if (UEInterpreter.ToInt(actor.GetProp(PK.Physics)) == 3)
                    throw new NotSupportedException(
                        $"physWalking startSwimming reached (Engine.dll.c:136458) actor={actor.GetProp(PK.Name)} — port the shipped swim entry before using it");
            }
            return WalkGateResult.Continue;
        }

        // ── APawn::stepUp (Engine.dll.c:66559): the up→forward→down walk step
        // used when the flat forward move hit something. Mount first (shipped
        // first operation; hook — geometry-heavy), then up MaxStepHeight,
        // forward, and either slide along a wall (with the SHARED
        // ProcessHitWall on the first AND second wall + TwoWallAdjust) or
        // recurse onto a shallow floor, then step back down (sliding on a
        // steep slope). ──
        // `hit` is the caller's FCheckResult (param_11, Engine.dll.c:66564):
        // stepUp runs all its moves and Mount into the SAME record.
        // Test seam: a record built from the _lastMove* publication of the last full move.
        public void StepUp(UEInterpreter interp, UEObject pawn, Vector3 gravDir, Vector3 desiredDir,
            Vector3 delta)
            => StepUp(interp, pawn, gravDir, desiredDir, delta, MoveCheck.FromActorProps(pawn));

        public void StepUp(UEInterpreter interp, UEObject pawn, Vector3 gravDir, Vector3 desiredDir,
            Vector3 delta, MoveCheck hit)
        {
            if (TryMount(interp, pawn, -gravDir, hit)) return;

            float maxStep = UEInterpreter.AsFloat(pawn.GetProp(PK.MaxStepHeight));
            Vector3 up = -gravDir * maxStep;
            Vector3 down = gravDir * maxStep;
            if (TraceMovesNow)
                Log($"step_up begin actor={pawn.GetProp(PK.Name)} location={pawn.GetProp(PK.Location)}"
                    + $" grav={gravDir} desired={desiredDir} delta={delta} max={maxStep:R}");
            (float upFraction, Vector3 upNormal, _) = WalkMove(interp, pawn, up, hit);
            if (TraceMovesNow)
                Log($"step_up up actor={pawn.GetProp(PK.Name)} location={pawn.GetProp(PK.Location)}"
                    + $" fraction={upFraction} normal={upNormal}");
            (float time, Vector3 normal, UEObject hitActor) = WalkMove(interp, pawn, delta, hit);
            if (TraceMovesNow)
                Log($"step_up forward actor={pawn.GetProp(PK.Name)} location={pawn.GetProp(PK.Location)}"
                    + $" fraction={time:R} normal={normal} hit={hitActor?.GetProp(PK.Name)}");
            if (time < 1f)
            {
                // Player pushing a Decoration (66724-66745, disasm 0x103ec806-
                // 0x103ec8ff): player, Hit.Actor is a Decoration with bPushable (0x260&1)
                // and DesiredDir·HitNormal < −0.9 (double 0x1047a5d8) →
                // bJustTeleported, Velocity *= Mass/(Mass_deco+Mass),
                // processHitWall, exit on Falling, otherwise straight to the step down. Other
                // cases (not a player, not a Decoration, !bPushable, dot ≥ −0.9)
                // take the ordinary wall branch (0x103ec904).
                if (UEInterpreter.ClassIsA(pawn.Class, "PlayerPawn") && hitActor is not null
                    && UEInterpreter.ClassIsA(hitActor.Class, "Decoration")
                    && UEInterpreter.ToBool(hitActor.GetDeclared("Decoration", "bPushable"))
                    && ((double)desiredDir.z * normal.z + (double)desiredDir.y * normal.y
                        + (double)desiredDir.x * normal.x) < -0.9)
                {
                    pawn.Props["bJustTeleported"] = true;
                    double mass = UEInterpreter.AsFloat(pawn.GetProp(PK.Mass));
                    double ratio = mass / (UEInterpreter.AsFloat(hitActor.GetProp(PK.Mass)) + mass);
                    Vector3 v = UEInterpreter.AsVector3(pawn.GetProp(PK.Velocity));
                    pawn.Props["Velocity"] = new Vector3((float)(ratio * v.x), (float)(ratio * v.y),
                        (float)(ratio * v.z));
                    ProcessHitWall(interp, pawn, normal, hitActor);
                    if (UEInterpreter.ToInt(pawn.GetProp(PK.Physics)) == 2) return;
                }
                else
                {
                    // 0x103ec904-0x103ec94b: |N.z| against 0.2f; else |Delta|² =
                    // (y² + z²) + x² and ·Time in the register against 144f, `test ah,0x41;
                    // jne` — a wall both on equality and on NaN.
                    double deltaSq = ((double)delta.y * delta.y + (double)delta.z * delta.z)
                                     + (double)delta.x * delta.x;
                    bool wall = Math.Abs(normal.z) >= 0.2f || !(deltaSq * time > 144.0);
                    if (wall)
                    {
                        ProcessHitWall(interp, pawn, normal, hitActor);
                        if (StepUpSlide(delta, normal, time, out Vector3 slide))
                        {
                            (float sFraction, Vector3 sNormal, UEObject sHitActor) =
                                WalkMove(interp, pawn, slide, hit);
                            if (sFraction < 1f)
                            {
                                ProcessHitWall(interp, pawn, sNormal, sHitActor);
                                if (UEInterpreter.ToInt(pawn.GetProp(PK.Physics)) == 2) return; // Falling
                                Vector3 adjusted = slide;
                                TwoWallAdjust(desiredDir, ref adjusted, sNormal, normal, sFraction);
                                WalkMove(interp, pawn, adjusted, hit);
                            }
                        }
                    }
                    else
                    {
                        // Shallow floor ahead → recurse with the leftover delta.
                        StepUp(interp, pawn, gravDir, desiredDir, delta * (1f - time), hit);
                        if (UEInterpreter.ToInt(pawn.GetProp(PK.Physics)) == 2) return; // Falling
                    }
                }
            }
            // Step back down; slide if we landed on a steep slope.
            (float dTime, Vector3 dDownNormal, _) = WalkMove(interp, pawn, down, hit);
            if (TraceMovesNow)
                Log($"step_up down actor={pawn.GetProp(PK.Name)} location={pawn.GetProp(PK.Location)}"
                    + $" fraction={dTime:R} normal={dDownNormal}");
            // 0x103ecbff-0x103eccc4: the same slide computation, and the step down HAS
            // its own "not backwards" check (Down·slide < 0 or NaN -> exit without a move).
            if (dTime < 1f && dDownNormal.z < 0.5f
                && StepUpSlide(down, dDownNormal, dTime, out Vector3 downSlide))
                WalkMove(interp, pawn, downSlide, hit);
        }

        // stepUp slide along the hit normal — the same machine code for the wall
        // (0x103ec9f6-0x103ecab3) and the step down (0x103ecc27-0x103eccc4):
        //   dn = (D.y·N.y + D.z·N.z) + D.x·N.x                      — in the register;
        //   z' = float(D.z − float(dn·N.z)), y' = float(D.y − float(dn·N.y));
        //   x' = D.x − dn·N.x                                         — in the register;
        //   s = 1 − Time (register); S.z = float(z'·s), S.y = float(y'·s),
        //   x'·s stays in the register (`fst`), S.x is its float;
        //   move only if ((D.y·S.y + D.z·S.z) + (x'·s)·D.x) ≥ 0 — `fcomp 0.0;
        //   test ah,1; jne`, i.e. "less than zero" and NaN skip the move.
        // The x87 register has a 64-bit mantissa, modelled here by double (53): the same
        // convention as TwoWallAdjust; float·float products are exact in both.
        static bool StepUpSlide(Vector3 d, Vector3 n, float time, out Vector3 slide)
        {
            double dn = ((double)d.y * n.y + (double)d.z * n.z) + (double)d.x * n.x;
            float zp = d.z - (float)(dn * n.z);
            float yp = d.y - (float)(dn * n.y);
            double xp = d.x - dn * n.x;
            double s = 1.0 - time;
            float sz = (float)(zp * s), sy = (float)(yp * s);
            double xs = xp * s;
            slide = new Vector3((float)xs, sy, sz);
            double back = ((double)d.y * sy + (double)d.z * sz) + xs * d.x;
            return back >= 0.0;
        }

        // ── AActor::TwoWallAdjust (Engine.dll.c): fold the slide delta when a
        // second wall is hit — slide along the newer wall's tangent when the
        // walls face the same way, else follow the crease between them (never
        // backwards vs desired). ──
        // Body at 0x1031c3e0. Retranscribed from the machine code after the jump
        // oracle caught this port disagreeing with it, and the disagreement was
        // NOT a rounding detail: shipped runs the crease through
        // FVector::SafeNormal (0x1031c47b) before scaling by it, this port did
        // not. An unnormalised crease scales the whole adjusted delta by
        // |n x o|² = sin²θ, which is invisible while the two walls are near
        // perpendicular and wrong everywhere else — exactly the shape of bug a
        // "looks right in game" check never finds.
        //
        // The float32/extended split below is register allocation, read at the
        // same address: dot products never leave the register file, the Y and Z
        // components round before being scaled, and X does not.
        public static void TwoWallAdjust(Vector3 desiredDir, ref Vector3 delta, Vector3 hitNormal,
            Vector3 oldNormal, float time)
        {
            double f = 1.0 - time;
            double facing = ((double)hitNormal.y * oldNormal.y
                             + (double)oldNormal.z * hitNormal.z)
                          + (double)hitNormal.x * oldNormal.x;
            if (facing > 0.0)
            {
                // Both walls face the same way: slide along the newer one.
                double d = ((double)hitNormal.y * delta.y + (double)delta.x * hitNormal.x)
                         + (double)delta.z * hitNormal.z;
                float az = (float)(delta.z - (float)(d * hitNormal.z));
                float ay = (float)(delta.y - (float)(d * hitNormal.y));
                double ax = delta.x - d * hitNormal.x;
                // RU EA (UEEdition.RuOrder; RU Engine 0x1031c558..0x1031c56a): x' and 1 − Time are stored as float before
                // the three products (US keeps both in the register).
                if (UEEdition.RuOrder)
                {
                    float axf = (float)ax, ff = (float)f;
                    delta = new Vector3(axf * ff, ay * ff, az * ff);
                }
                else delta = new Vector3((float)(ax * f), (float)(ay * f), (float)(az * f));
                // This branch has its own "never backwards" test (0x1031c5ca)
                // and it does NOT mirror the crease branch's: where that one
                // FLIPS the delta, this one ZEROES it, and it keeps the slide
                // only on a strictly positive dot. The port had no test here at
                // all, so a pawn kept sliding along a wall in a direction the
                // shipped engine stops dead. Found by the jump oracle.
                if (!(((double)desiredDir.z * delta.z + (double)desiredDir.y * delta.y)
                      + (double)desiredDir.x * delta.x > 0.0))
                    delta = Vector3.zero;
                return;
            }

            // Facing each other: follow the crease, and never backwards.
            var crease = new Vector3(
                (float)((double)hitNormal.y * oldNormal.z - (double)hitNormal.z * oldNormal.y),
                (float)((double)oldNormal.x * hitNormal.z - (double)hitNormal.x * oldNormal.z),
                (float)((double)hitNormal.x * oldNormal.y - (double)hitNormal.y * oldNormal.x));
            crease = crease.normalized;
            // RU EA (UEEdition.RuOrder; RU Engine 0x1031c465..0x1031c478): (c.z·D.z + c.y·D.y) + c.x·D.x.
            double along = UEEdition.RuOrder
                ? ((double)crease.z * delta.z + (double)crease.y * delta.y) + (double)crease.x * delta.x
                : ((double)crease.x * delta.x + (double)crease.z * delta.z) + (double)crease.y * delta.y;
            double scale = along * f;
            delta = new Vector3((float)(crease.x * scale), (float)(crease.y * scale),
                                (float)(crease.z * scale));
            double forward = ((double)desiredDir.z * delta.z + (double)desiredDir.y * delta.y)
                           + (double)desiredDir.x * delta.x;
            if (forward < 0.0)
                delta = new Vector3(delta.x * -1f, delta.y * -1f, delta.z * -1f);
        }

        // AActor::physProjectile (shipped Engine.dll 0x103f2ab0 / c:56153).
        // This physics mode is not projectile-class-only: Harry's authored
        // Mounting state deliberately sets PHYS_Projectile with zero velocity.
        // Water drag, acceleration, Projectile.MaxSpeed, MoveActor, HitWall and
        // bounce-to-falling order therefore belong to the generic actor runtime.
        public void PhysProjectile(UEInterpreter interp, UEObject actor, float dt, int iteration = 0)
        {
            UEObject region = actor.GetProp(PK.Region) as UEObject;
            UEObject zone = region?.GetProp(PK.Zone) as UEObject;
            // AActor::physProjectile zone-0 head (Engine.dll.c:295433-295437):
            // ZoneNumber==0 → Level->DestroyActor(this), no event.
            if (zone is null || UEInterpreter.ToInt(region.GetProp(PK.ZoneNumber)) == 0)
            {
                DestroyActor(interp, actor);
                return;
            }

            Vector3 oldLocation = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
            actor.Props["OldLocation"] = oldLocation;
            actor.Props["bJustTeleported"] = false;
            float remaining = dt;
            int bounceCount = 0;

            // Body FCheckResult: FCheckResult(1.0) before the loop (Engine.dll.c:
            // 56213-56220), and before EACH move shipped resets only Time
            // (56225: fStack_64 = 1.0) — normal and actor remain from the previous one.
            var projHit = new MoveCheck();
            // Iteration counter is a call parameter (param_2), loop head
            // `0 < remaining && param_2 < 8` (56231).
            while (remaining > 0f && iteration < 8)
            {
                iteration++;
                // The zone is read LIVE on every iteration (this+0xa0, 56233).
                zone = (actor.GetProp(PK.Region) as UEObject)?.GetProp(PK.Zone) as UEObject ?? zone;
                Vector3 velocity = UEInterpreter.AsVector3(actor.GetProp(PK.Velocity));
                if (UEInterpreter.ToBool(zone.GetProp(PK.bWaterZone)))
                {
                    float drag = 1f - remaining
                        * UEInterpreter.AsFloat(zone.GetProp(PK.ZoneFluidFriction)) * 0.2f;
                    velocity *= drag;
                }
                velocity += UEInterpreter.AsVector3(actor.GetProp(PK.Acceleration)) * remaining;

                if (UEInterpreter.ClassIsA(actor.Class, "Projectile"))
                {
                    float maxSpeed = UEInterpreter.AsFloat(actor.GetProp(PK.MaxSpeed));
                    if (velocity.sqrMagnitude > maxSpeed * maxSpeed)
                        velocity = velocity.normalized * maxSpeed;
                }
                actor.Props["Velocity"] = velocity;

                float thisSlice = remaining;
                remaining = 0f;
                // Shipped uses the function's original DeltaSeconds here, even
                // after a bounce leaves a smaller `remaining` slice.
                Vector3 delta = velocity * dt;
                projHit.Time = 1f;
                MoveActor(interp, actor, delta, UEInterpreter.AsRotator(actor.GetProp(PK.Rotation)),
                    bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false, projHit);
                float fraction = projHit.Time;
                if (fraction < 1f && !actor.Destroyed
                    && !UEInterpreter.ToBool(actor.GetProp(PK.bJustTeleported)))
                {
                    Vector3 normal = projHit.Normal;
                    UEObject hitActor = projHit.Actor ?? interp.GetLevelInfo();
                    UELifecycle.CallEvent(interp, actor, "HitWall", normal, hitActor);
                    if (UEInterpreter.ToBool(actor.GetProp(PK.bBounce)))
                    {
                        if (bounceCount < 2) remaining = (1f - fraction) * thisSlice;
                        bounceCount++;
                        // 56275-56283, disasm 0x103f2e3c-0x103f2e5a: physFalling(
                        // remaining, param_2) WITHOUT a remaining>0 condition (when exhausted —
                        // with zero: the physFalling head still runs), then `jmp 0x103f2b6e` —
                        // the head of THIS loop; the remainder is not zeroed, and the projectile
                        // integrates it once more with its own formula.
                        if (UEInterpreter.ToInt(actor.GetProp(PK.Physics)) == 2)
                            PhysFalling(interp, actor, remaining, iteration);
                    }
                }
            }

            // Tail (disasm 0x103f2e5f-0x103f2ec4): X in the x87 register, Y and Z
            // through float (see DisplacementVelocityX87).
            if (!UEInterpreter.ToBool(actor.GetProp(PK.bBounce))
                && !UEInterpreter.ToBool(actor.GetProp(PK.bJustTeleported)) && dt != 0f)
                actor.Props["Velocity"] = DisplacementVelocityX87(
                    UEInterpreter.AsVector3(actor.GetProp(PK.Location)), oldLocation, dt, 0);
        }

        // AActor::physTrailer (shipped Engine.dll.c:149807-150025). A trailer is
        // repositioned from its authoritative Owner every physics tick via
        // FarMove(..., bNoCheck=1), then (for non-sprites) rotated with one
        // zero-delta MoveActor. The reached PeevesTrail path is non-sprite,
        // AnimBone=0. Keep the skeletal-bone branch loud until the native mesh
        // GetBoneCoords path is available in Core; using a rendered Unity bone
        // would invert the architecture boundary.
        public void PhysTrailer(UEInterpreter interp, UEObject actor)
        {
            UEObject owner = actor.GetProp(PK.Owner) as UEObject;
            if (owner is null || owner.Destroyed) return;

            bool prePivot = UEInterpreter.ToBool(actor.GetProp(PK.bTrailerPrePivot));
            bool sameRotation = UEInterpreter.ToBool(actor.GetProp(PK.bTrailerSameRotation));
            int drawType = UEInterpreter.ToInt(actor.GetProp(PK.DrawType));
            Vector3 ownerLocation = UEInterpreter.AsVector3(owner.GetProp(PK.Location));
            Vector3 trailerPrePivot = UEInterpreter.AsVector3(actor.GetProp(PK.PrePivot));

            // DT_Sprite — a separate shipped branch (Engine.dll.c:149862-149915):
            // bTrailerPrePivot → Owner.Location + PrePivot; else with
            // bTrailerSameRotation → Owner.Location − X·(this+0x200), where X is the
            // X axis of UnitCoords/Owner.Rotation, and field +0x200 per the prop_offsets
            // oracle is Actor.MASS (there is no "SpriteProjForward" field). Live user:
            // Halo (HPParticle/Halo.uc: SetPhysics(11), bTrailerSameRotation=true,
            // Mass=−11 — i.e. the sprite is pushed 11 units FORWARD along the owner's view);
            // Quidditch: Bludger_Halo/Snitch_Halo. Without flags — Owner.Location.
            // The sprite gets no rotation: only FarMove(bNoCheck).
            if (drawType == 1)
            {
                Vector3 spriteLocation;
                if (prePivot) spriteLocation = ownerLocation + trailerPrePivot;
                else if (sameRotation)
                {
                    UERotator ownerRotation = UEInterpreter.AsRotator(owner.GetProp(PK.Rotation));
                    BrushCoords coords = BrushCoords.Identity;
                    coords.DivRotator(ownerRotation.Pitch, ownerRotation.Yaw, ownerRotation.Roll);
                    float k = UEInterpreter.AsFloat(actor.GetProp(PK.Mass));
                    spriteLocation = new Vector3(ownerLocation.x - coords.X.x * k,
                        ownerLocation.y - coords.X.y * k, ownerLocation.z - coords.X.z * k);
                }
                else spriteLocation = ownerLocation;
                FarMove(interp, actor, spriteLocation, bTest: false, bNoCheck: true);
                return;
            }

            int animBone = UEInterpreter.ToInt(actor.GetProp(PK.AnimBone));
            if (animBone != 0 && owner.GetProp(PK.Mesh) is not null)
            {
                // BONE BRANCH (physTrailer, Engine.dll.c:150000-150020): coordinates of the
                // owner's bone AnimBone−1 via Mesh->GetBoneCoords (vtable slot +0x9c,
                // resolved from the image: 0x1047b4d8), position — the coords origin,
                // rotation — FCoords::OrthoRotation; then FarMove (slot +0x90, bNoCheck) and
                // MoveActor with a zero delta and this rotation (slot +0x8c). The pose is
                // Core's (UELevelRuntime.SkeletalPose).
                BoneWorld bone = BoneWorldOf(owner, animBone - 1);
                UERotator boneRotation = OrthoRotation(bone);
                FarMove(interp, actor, bone.Origin, bTest: false, bNoCheck: true);
                MoveActor(interp, actor, Vector3.zero, boneRotation,
                    bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false);
                if (DetailedJournal)
                    Journal?.Invoke($"trailer_bone actor={actor.GetProp(PK.Name)} owner={owner.GetProp(PK.Name)} "
                        + $"bone={animBone - 1} location={actor.GetProp(PK.Location)} rotation={actor.GetProp(PK.Rotation)}");
                return;
            }

            Vector3 newLocation = ownerLocation;
            if (prePivot || sameRotation)
            {
                UERotator ownerRotation = UEInterpreter.AsRotator(owner.GetProp(PK.Rotation));
                BrushCoords coords = BrushCoords.Identity;
                coords.MulRotator(ownerRotation.Pitch, ownerRotation.Yaw, ownerRotation.Roll);
                newLocation += coords.TransformVector(trailerPrePivot);
            }

            UERotator newRotation;
            if (sameRotation)
            {
                newRotation = UEInterpreter.AsRotator(owner.GetProp(PK.Rotation));
            }
            else
            {
                Vector3 ownerVelocity = UEInterpreter.AsVector3(owner.GetProp(PK.Velocity));
                if (Math.Abs(ownerVelocity.x) < 0.0001f
                    && Math.Abs(ownerVelocity.y) < 0.0001f
                    && Math.Abs(ownerVelocity.z) < 0.0001f)
                    newRotation = new UERotator(0x4000, 0, 0);
                else
                    newRotation = UEValue.RotatorFromVector(-ownerVelocity);
            }

            FarMove(interp, actor, newLocation, bTest: false, bNoCheck: true);
            MoveActor(interp, actor, Vector3.zero, newRotation,
                bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false);
            if (DetailedJournal)
                Journal?.Invoke($"trailer actor={actor.GetProp(PK.Name)} owner={owner.GetProp(PK.Name)} "
                    + $"location={actor.GetProp(PK.Location)} rotation={actor.GetProp(PK.Rotation)}");
        }

        // APawn::physFlying (Engine.dll.c:45551): fluid-friction calcVelocity,
        // optional zone flow, one swept MoveActor, then the same wall-slide /
        // two-wall / step-up split used by the shipped native. Velocity is finally
        // rederived from actual displacement unless the move teleported the pawn.
        public void PhysFlying(UEInterpreter interp, UEObject actor, float dt)
        {
            UEObject region = actor.GetProp(PK.Region) as UEObject;
            UEObject zone = region?.GetProp(PK.Zone) as UEObject;
            if (zone is null)
                throw new InvalidDataException($"physFlying missing zone actor={actor.GetProp(PK.Name)}");
            // APawn::physFlying zone-0 head (Engine.dll.c:45739-45749, disasm
            // 0x103f13c2-0x103f142d): bCollideWorld (0x1dc&2) && ZoneNumber==0 →
            // exit; a NON-player (bIsPlayer, Pawn+0x25c&2, cleared) first logs
            // "%s flew out of the world!" and is destroyed — XLevel vtable+0x98
            // (this, 0), the same DestroyActor slot as in physProjectile.
            // FellOutOfWorld is not sent (the decompile at 294778 shows only one branch).
            if (UEInterpreter.ToBool(actor.GetProp(PK.bCollideWorld))
                && UEInterpreter.ToInt(region.GetProp(PK.ZoneNumber)) == 0)
            {
                if (!UEInterpreter.ToBool(actor.GetProp(PK.bIsPlayer)))
                {
                    Log($"flew_out_of_world actor={actor.GetProp(PK.Name)}");
                    DestroyActor(interp, actor);
                }
                return;
            }
            Vector3 acceleration = UEInterpreter.AsVector3(actor.GetProp(PK.Acceleration));
            Vector3 zoneVelocity = UEInterpreter.AsVector3(zone.GetProp(PK.ZoneVelocity));
            Vector3 accelDir = acceleration.sqrMagnitude > 0f ? acceleration.normalized : Vector3.zero;
            UEPawnMovement.CalcVelocity(actor, accelDir, dt,
                UEInterpreter.AsFloat(actor.GetProp(PK.AirSpeed)),
                UEInterpreter.AsFloat(zone.GetProp(PK.ZoneFluidFriction)),
                bFluid: true, bBrake: false, bBuoyant: false);

            Vector3 location = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
            actor.Props["OldLocation"] = location;
            actor.Props["bJustTeleported"] = false;

            // PlayerPawns always inherit ZoneVelocity. NPCs do so only above the
            // shipped 300 uu/s threshold (strictly SizeSquared > 90000).
            Vector3 flow = UEInterpreter.ClassIsA(actor.Class, "PlayerPawn")
                || zoneVelocity.sqrMagnitude > 90000f ? zoneVelocity : Vector3.zero;
            Vector3 velocity = UEInterpreter.AsVector3(actor.GetProp(PK.Velocity));
            Vector3 delta = (velocity + flow) * dt;
            // Body FCheckResult — FCheckResult(1.0) right before the first move
            // (Engine.dll.c:45659-45661); the second and third moves and stepUp use it too.
            var flyHit = new MoveCheck();
            MoveActor(interp, actor, delta, UEInterpreter.AsRotator(actor.GetProp(PK.Rotation)),
                bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false, flyHit);

            float fraction = flyHit.Time;
            if (fraction < 1f)
            {
                Vector3 hitNormal = flyHit.Normal;
                UEObject hitActor = flyHit.Actor;
                Vector3 gravity = UEInterpreter.AsVector3(zone.GetProp(PK.ZoneGravity));
                Vector3 gravityDir = new(0f, 0f, gravity.z > 0f ? 1f : -1f);
                Vector3 desiredDir = delta.sqrMagnitude > 0f ? delta.normalized : Vector3.zero;
                Vector3 velocityDir = velocity.sqrMagnitude > 0f ? velocity.normalized : Vector3.zero;
                float verticalMotion = Vector3.Dot(velocityDir, gravityDir);

                if (Math.Abs(hitNormal.z) >= 0.2f || verticalMotion >= 0.5f || verticalMotion <= -0.2f)
                {
                    ProcessHitWall(interp, actor, hitNormal, hitActor);
                    float projection = Vector3.Dot(delta, hitNormal);
                    Vector3 slide = (delta - hitNormal * projection) * (1f - fraction);
                    if (Vector3.Dot(slide, delta) >= 0f)
                    {
                        MoveActor(interp, actor, slide, UEInterpreter.AsRotator(actor.GetProp(PK.Rotation)),
                            bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false, flyHit);
                        float secondFraction = flyHit.Time;
                        if (secondFraction < 1f)
                        {
                            Vector3 secondNormal = flyHit.Normal;
                            ProcessHitWall(interp, actor, secondNormal, flyHit.Actor);
                            TwoWallAdjust(desiredDir, ref slide, secondNormal, hitNormal, secondFraction);
                            MoveActor(interp, actor, slide, UEInterpreter.AsRotator(actor.GetProp(PK.Rotation)),
                                bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false, flyHit);
                        }
                    }
                }
                else
                {
                    float beforeStepZ = UEInterpreter.AsVector3(actor.GetProp(PK.Location)).z;
                    StepUp(interp, actor, gravityDir, desiredDir, delta * (1f - fraction), flyHit);
                    Vector3 oldLocation = UEInterpreter.AsVector3(actor.GetProp(PK.OldLocation));
                    oldLocation.z = (oldLocation.z - beforeStepZ)
                        + UEInterpreter.AsVector3(actor.GetProp(PK.Location)).z;
                    actor.Props["OldLocation"] = oldLocation;
                }
            }

            // Tail (disasm 0x103f19b3-0x103f1a16): X in the x87 register, Y and Z
            // through float (see DisplacementVelocityX87).
            if (!UEInterpreter.ToBool(actor.GetProp(PK.bJustTeleported)))
                actor.Props["Velocity"] = DisplacementVelocityX87(
                    UEInterpreter.AsVector3(actor.GetProp(PK.Location)),
                    UEInterpreter.AsVector3(actor.GetProp(PK.OldLocation)), dt, 0);
            if (DetailedJournal || ReferenceEquals(actor, LocalPlayer))
                Journal?.Invoke($"flying_move actor={actor.GetProp(PK.Name)} from={location}"
                    + $" to={actor.GetProp(PK.Location)} velocity={actor.GetProp(PK.Velocity)}"
                    + $" acceleration={actor.GetProp(PK.Acceleration)} flow={flow}"
                    + $" blocked={(fraction < 1f).ToString().ToLowerInvariant()} fraction={fraction:R}");
        }

        // SwARc Engine measurement tap (tools/swarc_oracle/ENGINE_MEASURE.md): PerformPhysics entry (true) and exit
        // (false) of one actor. Observes only; null in production.
        public static Action<UELevelRuntime, UEObject, int, float, bool> PerformPhysicsTap;
        readonly struct PhysicsTapEnd : IDisposable
        {
            readonly UELevelRuntime _rt; readonly UEObject _a; readonly int _p; readonly float _dt;
            public PhysicsTapEnd(UELevelRuntime rt, UEObject a, int p, float dt) { _rt = rt; _a = a; _p = p; _dt = dt; }
            public void Dispose() => PerformPhysicsTap?.Invoke(_rt, _a, _p, _dt, false);
        }

        // The performPhysics body — Core-owned (was Runtime.PhysicsHook): the shipped mode
        // dispatch plus the performPhysics tail (rotation toward DesiredRotation,
        // MoveTimer decrement, PostTouch). Runs only for Physics != PHYS_None.
        public void PerformPhysics(UEInterpreter interp, UEObject actor, int physics,
            float dt, Vector3 preStepVelocity)
        {
            PerformPhysicsTap?.Invoke(this, actor, physics, dt, true);
            using var _tap = new PhysicsTapEnd(this, actor, physics, dt);
            if (physics == 1)
                PhysWalking(interp, actor, dt);
            else if (physics == 2)
                PhysFalling(interp, actor, dt);
            else if (physics == 4 && UEInterpreter.ClassIsA(actor.Class, "Pawn"))
                PhysFlying(interp, actor, dt);
            else if (physics == 4)
            {
                // PHYS_Flying on a NON-pawn: base AActor::performPhysics
                // (Engine.dll.c:92695, switch this[0x30]) has NO case 4 — the mode falls
                // through the switch without translation; of all physics only the common
                // tail runs (physicsRotation with non-zero RotationRate + PostTouch).
                // physFlying exists ONLY in APawn (the only performPhysics overrides are
                // APawn:68659 and AInterpolationManager:38731). Live user —
                // FlipendoVaseBronzeShard shards (Lev_Tut1b): their scatter comes from the
                // rotation tail, not positional physics.
            }
            else if (physics == 5)
            {
                // PHYS_Rotating has NO case in either shipped performPhysics
                // switch (APawn Engine.dll.c:68660, AActor 92697): the mode only
                // keeps the actor physics-active so the rotation tail below runs.
            }
            else if (physics == 6)
                PhysProjectile(interp, actor, dt);
            else if (physics == 9 && UEInterpreter.ClassIsA(actor.Class, "Mover"))
                PhysMovingBrush(interp, actor, dt);
            else if (physics == 9 && UEInterpreter.ClassIsA(actor.Class, "Pawn"))
            {
                // PHYS_MovingBrush on a PAWN: the APawn::performPhysics switch
                // (Engine.dll.c:68671-68708) has no case 9 — neither OldLocation nor
                // Velocity; only the common pawn tail.
            }
            else if (physics == 9)
            {
                // PHYS_MovingBrush on a NON-mover (live user: 5 spellTrigger
                // on Lev2_Inc_A). Shipped AActor::performPhysics, case 9
                // (Engine.dll.c:92709-92719): OldLocation = Location, call
                // physMovingBrush, then Velocity = (Location − OldLocation)/dt.
                // physMovingBrush (92398) first walks the class chain to AMover and,
                // not finding it, RETURNS — so for a non-mover the mode reduces to
                // OldLocation = Location and zero velocity; then the common tail
                // (physicsRotation).
                Vector3 loc = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
                actor.Props["OldLocation"] = loc;
                actor.Props["Velocity"] = Vector3.zero;
            }
            else if (physics == 8 && UEInterpreter.ClassIsA(actor.Class, "InterpolationManager"))
                PhysInterpolating(interp, actor, dt);
            else if (physics == 8)
            {
                // PHYS_Interpolating on a NON-manager: the AActor::performPhysics switch
                // (Engine.dll.c:92695) has no case 8 — only the common tail
                // (physicsRotation). The only override is
                // AInterpolationManager::performPhysics (UELevelRuntime.Interpolation.cs).
            }
            else if (physics == 11)
                PhysTrailer(interp, actor);
            else if ((physics == 3 || physics == 10) && !UEInterpreter.ClassIsA(actor.Class, "Pawn"))
            {
                // PHYS_Swimming/PHYS_Spider on a NON-pawn: the AActor::performPhysics
                // switch (92687-92718) has no cases 3 and 10 — only the tail.
            }
            else
            {
                // STOP: physSwimming (3), physRolling (7), physSpider (10) are not
                // ported. physSwimming is UNREACHABLE in the campaign (measured):
                // Physics=3 is set only by SetPhysics(3) under a water-zone check
                // (Harry.uc:1880/2689, Engine.u PlayerPawn.uc:4628/5109/5391, Pawn.uc:667)
                // or in state PlayerSwimming, entered only from there (PlayerPawn.uc:
                // 5484); Engine.dll has no native write of Physics=3; all 226 zones
                // of 41 maps have effective bWaterZone=False (see PhysFalling).
                // SetPhysics(7|10) appears neither in Engine.u nor in package scripts.
                Journal?.Invoke($"physics_boundary actor={actor.GetProp(PK.Name)} mode={physics} location={actor.GetProp(PK.Location)} velocity={actor.GetProp(PK.Velocity)} acceleration={actor.GetProp(PK.Acceleration)} radius={actor.GetProp(PK.CollisionRadius)} height={actor.GetProp(PK.CollisionHeight)} maxstep={actor.GetProp(PK.MaxStepHeight)} region={actor.GetDeclared("Actor", "Region")}");
                throw new NotSupportedException(
                    $"TickPhysics mode={physics} actor={actor.GetProp(PK.Name)} class={actor.Class?.Name}");
            }

            // APawn::performPhysics tail (Engine.dll.c:68713-68731): after ANY
            // active physics mode a pawn turns toward DesiredRotation (players
            // always; others when Rotation differs or RotationRate.Roll > 0) and
            // MoveTimer decrements. PHYS_None pawns never reach performPhysics.
            // (PostTouch dispatch lives here too; PendingTouch is not tracked.)
            if (!actor.Destroyed)
            {
                if (UEInterpreter.ClassIsA(actor.Class, "Pawn"))
                {
                    UERotator rot = UEInterpreter.AsRotator(actor.GetProp(PK.Rotation));
                    UERotator desired = UEInterpreter.AsRotator(actor.GetProp(PK.DesiredRotation));
                    if (UEInterpreter.ClassIsA(actor.Class, "PlayerPawn")
                        || rot.Pitch != desired.Pitch || rot.Yaw != desired.Yaw || rot.Roll != desired.Roll
                        || UEInterpreter.AsRotator(actor.GetProp(PK.RotationRate)).Roll > 0)
                        PhysicsRotation(actor, dt, preStepVelocity);
                    UEPawnMovement.DecrementMoveTimer(actor, dt);
                }
                else
                {
                    // AActor::performPhysics (Engine.dll.c:92723-92726): any actor
                    // whose RotationRate has a non-zero uint16 component runs
                    // AActor::physicsRotation.
                    UERotator rate = UEInterpreter.AsRotator(actor.GetProp(PK.RotationRate));
                    if ((rate.Pitch & 0xffff) != 0 || (rate.Yaw & 0xffff) != 0 || (rate.Roll & 0xffff) != 0)
                        ActorPhysicsRotation(interp, actor, dt);
                }
                // PostTouch chain (APawn::performPhysics Engine.dll.c:68726-68737 /
                // AActor 92727-92735): fire PostTouch on the pending actor with us
                // as the toucher, then pop our link off its chain.
                if (actor.GetProp(PK.PendingTouch) is UEObject pending)
                {
                    UELifecycle.CallEvent(interp, pending, "PostTouch", actor);
                    if (actor.GetProp(PK.PendingTouch) is UEObject still)
                    {
                        actor.Props["PendingTouch"] = still.GetProp(PK.PendingTouch);
                        still.Props["PendingTouch"] = null;
                    }
                }
            }
        }

        // AActor::fixedTurn (Engine.dll.c:120438): step a 16-bit angle from cur toward
        // desired by at most ±maxDelta, shortest arc (physicsRotation always uses the
        // shortest-turn mode). maxDelta==0 → no turn this frame.
        // AActor::physicsRotation (Engine.dll.c:158362, ?physicsRotation@AActor@@QAEXM@Z):
        // gate on bRotateToDesired (0x1f8&4) OR bFixedRotationDir (&2), and when
        // rotating-to-desired skip once Rotation == DesiredRotation. Steps are
        // ROUND(RotationRate.{Pitch,Yaw,Roll} * dt); each component turns via the
        // flag-aware fixedTurn when its step is non-zero and (when bRTD) its
        // desired differs; a changed rotator commits through MoveActor(delta=0,
        // newRot) (vtable 0x8c); with bRTD and Rotation fully at Desired the
        // EndedRotation event fires when probed. Reached by rotating non-pawn
        // decor/pickups (the stage-7 wand: its pickup state spins via
        // bFixedRotationDir).
        void ActorPhysicsRotation(UEInterpreter interp, UEObject actor, float dt)
        {
            bool rtd = UEInterpreter.ToBool(actor.GetProp(PK.bRotateToDesired));
            bool fixedDir = UEInterpreter.ToBool(actor.GetProp(PK.bFixedRotationDir));
            if (!rtd && !fixedDir) return;
            UERotator rot = UEInterpreter.AsRotator(actor.GetProp(PK.Rotation));
            UERotator desired = UEInterpreter.AsRotator(actor.GetProp(PK.DesiredRotation));
            if (rtd && rot.Pitch == desired.Pitch && rot.Yaw == desired.Yaw && rot.Roll == desired.Roll)
                return;
            UERotator rate = UEInterpreter.AsRotator(actor.GetProp(PK.RotationRate));
            int rollStep = (int)Math.Round(rate.Roll * dt, MidpointRounding.ToEven);
            int yawStep = (int)Math.Round(rate.Yaw * dt, MidpointRounding.ToEven);
            int pitchStep = (int)Math.Round(rate.Pitch * dt, MidpointRounding.ToEven);
            int pitch = rot.Pitch, yaw = rot.Yaw, roll = rot.Roll;
            if (yawStep != 0 && (!rtd || desired.Yaw != yaw))
                yaw = ActorFixedTurn(fixedDir, rtd, yaw, desired.Yaw, yawStep);
            if (pitchStep != 0 && (!rtd || desired.Pitch != pitch))
                pitch = ActorFixedTurn(fixedDir, rtd, pitch, desired.Pitch, pitchStep);
            if (rollStep != 0 && (!rtd || desired.Roll != roll))
                roll = ActorFixedTurn(fixedDir, rtd, roll, desired.Roll, rollStep);
            if (pitch != rot.Pitch || yaw != rot.Yaw || roll != rot.Roll)
                MoveActor(interp, actor, Vector3.zero, new UERotator(pitch, yaw, roll),
                    bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false);
            rot = UEInterpreter.AsRotator(actor.GetProp(PK.Rotation));
            if (rtd && rot.Pitch == desired.Pitch && rot.Yaw == desired.Yaw && rot.Roll == desired.Roll)
                UELifecycle.CallEvent(interp, actor, "EndedRotation");   // via ProcessEvent (wave B3)
        }

        // AActor::fixedTurn (Engine.dll.c:120438) — FLAG-AWARE:
        //   bFixedRotationDir && !bRotateToDesired → raw signed step (the
        //     constant pickup spin: (cur + step) & 0xffff);
        //   bFixedRotationDir && bRotateToDesired → one-DIRECTIONAL approach
        //     (the step's sign picks the direction, clamped at desired);
        //   bRotateToDesired only → the shortest-arc turn (the static FixedTurn
        //     below — the pawn path lands here after physicsRotation forces the
        //     flag state);
        //   neither → no turn (unreachable from the physicsRotation gate).
        private static int ActorFixedTurn(bool fixedDir, bool rotateToDesired,
            int cur, int desired, int step)
        {
            if (step == 0) return cur & 0xffff;
            int c = cur & 0xffff, d = desired & 0xffff;
            if (fixedDir)
            {
                if (!rotateToDesired) return (c + step) & 0xffff;
                if (step < 1)
                {
                    int cc = c < d ? c + 0x10000 : c;
                    int delta = d - cc;
                    if (delta <= step) delta = step;
                    return (c + delta) & 0xffff;
                }
                int dd = d < c ? d + 0x10000 : d;
                int gap = dd - c;
                if (step <= gap) return (c + step) & 0xffff;
                return (c + gap) & 0xffff;
            }
            if (rotateToDesired) return FixedTurn(cur, desired, step);
            return c;
        }

        private static int FixedTurn(int cur, int desired, int maxDelta)
        {
            cur &= 0xffff; desired &= 0xffff;
            if (maxDelta == 0) return cur;
            int m = Math.Abs(maxDelta);
            if (desired < cur)
            {
                int gap = cur - desired;
                if (gap > 0x7fff) { int d = (desired - cur) + 0x10000; return (cur + (m < d ? m : d)) & 0xffff; }
                if (m < gap) return (cur - m) & 0xffff;
                return (cur - gap) & 0xffff;
            }
            else
            {
                int gap = desired - cur;
                if (gap < 0x8000) return (cur + Math.Min(m, gap)) & 0xffff;
                int step = Math.Min(m, (cur - desired) + 0x10000);
                return (cur - step) & 0xffff;
            }
        }

        // APawn::physicsRotation (Engine.dll.c:64031): turn Rotation toward
        // DesiredRotation by RotationRate*dt (Yaw/Pitch via fixedTurn). Roll banking
        // is a loud boundary unless RotationRate.Roll<1 (then Roll=0).
        // APawn::physicsRotation (Engine.dll.c:64034): turn Pitch/Yaw toward
        // DesiredRotation at RotationRate*dt (raw-int inequality gates, 64094/
        // 64099), then bank Roll (64104-64201): rate<1 zeroes it; a slow walker or
        // small dv/dt decays it toward 0 with t=min(8dt,1); otherwise the target
        // bank is lateral(dv/dt)*maxBank/AccelRate (maxBank 4096 walking, 28000
        // else), clamped to ±RotationRate.Roll, blended at t=min(5dt,1) after
        // normalizing the current roll for continuity. Lateral = dv/dt projected
        // on the new-rotation world Y basis (the shipped GMath /= FRotator coords).
        public void PhysicsRotation(UEObject pawn, float dt, Vector3 oldVelocity)
        {
            UERotator rate = UEInterpreter.AsRotator(pawn.GetProp(PK.RotationRate));
            UERotator desired = UEInterpreter.AsRotator(pawn.GetProp(PK.DesiredRotation));
            UERotator rot = UEInterpreter.AsRotator(pawn.GetProp(PK.Rotation));
            pawn.Props["bFixedRotationDir"] = false; // 0x1f8 &~2 |4 (64092)
            pawn.Props["bRotateToDesired"] = true;
            int yaw = rot.Yaw, pitch = rot.Pitch, roll = rot.Roll;
            int yawStep = (int)Math.Round(rate.Yaw * dt, MidpointRounding.ToEven);
            if (desired.Yaw != rot.Yaw) yaw = FixedTurn(rot.Yaw, desired.Yaw, yawStep);
            int pitchStep = (int)Math.Round(rate.Pitch * dt, MidpointRounding.ToEven);
            if (desired.Pitch != rot.Pitch) pitch = FixedTurn(rot.Pitch, desired.Pitch, pitchStep);

            if (rate.Roll < 1)
            {
                roll = 0;
            }
            else
            {
                // x87 chains per disasm 0x103e5a17-0x103e5d5e (PC64; the register is
                // double, the same convention as in neighbouring bodies).
                Vector3 velocity = UEInterpreter.AsVector3(pawn.GetProp(PK.Velocity));
                int physics = UEInterpreter.ToInt(pawn.GetProp(PK.Physics));
                // Walking pawn: (x² + y²) + z² in the register against 40000f; `test cl,ah`
                // (C0) — damping on "less" and on NaN (0x103e5a23-0x103e5a55).
                double vsq = ((double)velocity.x * velocity.x + (double)velocity.y * velocity.y)
                             + (double)velocity.z * velocity.z;
                bool decay = physics == 1 && !(vsq >= 40000.0);
                float dvx = 0f, dvy = 0f, dvz = 0f;
                if (!decay)
                {
                    // dv (0x103e5ac4-0x103e5b3f): 1/dt in the register plus its float copy;
                    // Z — difference and product in the register, X/Y — float differences
                    // multiplied by float(1/dt); |dv|² from register values against
                    // 10000f, damping on ≤ and NaN.
                    double inv = 1.0 / dt;
                    float invf = (float)inv;
                    double rz = ((double)velocity.z - oldVelocity.z) * inv;
                    double ry = (double)invf * (velocity.y - oldVelocity.y);
                    double rx = (double)invf * (velocity.x - oldVelocity.x);
                    dvx = (float)rx; dvy = (float)ry; dvz = (float)rz;
                    decay = !((rx * rx + ry * ry) + rz * rz > 10000.0);
                }
                if (decay)
                {
                    // t = dt·8 in the register, 1 when t ≥ 1 and on NaN; stored to float and fistp.
                    double t = (double)dt * 8.0;
                    if (!(1.0 > t)) t = 1.0;
                    roll = rot.Roll < 0x8000
                        ? (int)Math.Round((float)((1.0 - t) * rot.Roll), MidpointRounding.ToEven)
                        : (int)Math.Round((float)((0x10000 - rot.Roll) * t + rot.Roll), MidpointRounding.ToEven);
                }
                else
                {
                    float maxBank = physics == 1 ? 4096f : 28000f;
                    // Shipped frame: GMath identity /= FRotator(newPitch, newYaw,
                    // 0) — iStack_18 is zeroed right before the coords build
                    // (Engine.dll.c:64147). lateral is the Y component of the inlined
                    // TransformVectorBy (0x103e5ba7-0x103e5bfb) over the float copies of dv:
                    // (dv.x·Y.x + dv.y·Y.y) + dv.z·Y.z, stored to float.
                    BrushCoords frame = BrushCoords.Identity;
                    frame.DivRotator(pitch, yaw, 0);
                    float lateral = (float)(((double)dvx * frame.Y.x + (double)dvy * frame.Y.y)
                        + (double)dvz * frame.Y.z);
                    float accelRate = UEInterpreter.AsFloat(pawn.GetProp(PK.AccelRate));
                    // lateral·maxBank / AccelRate in the register (0x103e5c0d-0x103e5c21).
                    double target = (double)lateral * maxBank / accelRate;
                    int clamped;
                    if (!(lateral > 0f))
                    {
                        // `fcomp 0.0; test ah,0x41; jne` — ≤ 0 and NaN; +65536 in the register.
                        int tv = (int)Math.Round((float)(target + 65536.0), MidpointRounding.ToEven);
                        int floor = 0x10000 - rate.Roll;
                        clamped = floor < tv ? tv : floor;
                    }
                    else
                    {
                        int tv = (int)Math.Round((float)target, MidpointRounding.ToEven);
                        clamped = tv < rate.Roll ? tv : rate.Roll;
                    }
                    // 0x103e5c69-0x103e5ca5: Rotation.Roll IS WRITTEN to the actor — &0xffff,
                    // then ±0x10000 toward the target side — before blending and before the
                    // "rotation changed" comparison.
                    int cur = rot.Roll & 0xffff;
                    if (clamped <= 0x8000) { if (cur > 0x8000) cur -= 0x10000; }
                    else if (cur < 0x8000) cur += 0x10000;
                    rot = new UERotator(rot.Pitch, rot.Yaw, cur);
                    pawn.Props["Rotation"] = rot;
                    // (1 − t)·cur + clamped·t in the register, t = dt·5 (1 when ≥ 1/NaN).
                    double t = (double)dt * 5.0;
                    if (!(1.0 > t)) t = 1.0;
                    roll = (int)Math.Round((float)((1.0 - t) * cur + clamped * t), MidpointRounding.ToEven);
                }
            }
            var newRot = new UERotator(pitch, yaw, roll);
            if (newRot.Pitch != rot.Pitch || newRot.Yaw != rot.Yaw || newRot.Roll != rot.Roll)
                MoveActor(pawn.Interp, pawn, Vector3.zero, newRot, bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false);
        }

        // Mover key arrays are ArrayDim=8 static arrays: an UNSET slot is a
        // faithful UE1 zero, but a mover with no key property at all (class or
        // loader defect) must be loud, never a silent zero pose.
        private static Vector3 KeyVector(UEObject mover, string prop, int idx)
        {
            if (mover.GetProp(prop) is not System.Collections.IList list)
                throw new NotSupportedException(
                    $"mover key property {prop} missing on {mover.GetProp(PK.Name)}");
            return idx >= 0 && idx < list.Count ? UEInterpreter.AsVector3(list[idx]) : Vector3.zero;
        }

        private static UERotator KeyRotator(UEObject mover, string prop, int idx)
        {
            if (mover.GetProp(prop) is not System.Collections.IList list)
                throw new NotSupportedException(
                    $"mover key property {prop} missing on {mover.GetProp(PK.Name)}");
            return idx >= 0 && idx < list.Count
                ? UEInterpreter.AsRotator(list[idx]) : new UERotator(0, 0, 0);
        }

        // ── APawn::physMovingBrush integrator (Engine.dll.c:92396) — advance
        // PhysAlpha at PhysRate, ease with MoverGlideType (1 = smoothstep
        // 3t²−2t³, else linear), interpolate the pose from OldPos/OldRot toward
        // BasePos+KeyPos[KeyNum] / BaseRot+KeyRot[KeyNum], MoveActor to it.
        // Clear bInterpolating + fire KeyFrameReached on arrival; a blocked
        // partial move also stops.
        //
        // Core-owned. This lived in the headless frontend, which meant a
        // frontend adopting HP1.Core got the collision math but NOT the code
        // that moves doors and lifts — Core could only project a mover's state,
        // never advance it. Moved verbatim (the body was already plain C# over
        // Core objects, ported from the decompile); only the journal sink and
        // the event call were rebound to Core's own. ──
        public void PhysMovingBrush(UEInterpreter interp, UEObject actor, float dt)
        {
            Vector3 startLoc = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
            actor.Props["OldLocation"] = startLoc;

            if (!UEInterpreter.ToBool(actor.GetProp(PK.bInterpolating)))
            {
                actor.Props["Velocity"] = Vector3.zero;
                if (DetailedJournal)
                    Journal?.Invoke($"moving_brush_idle actor={actor.GetProp(PK.Name)} location={startLoc}");
                return;
            }

            // Shipped has NO PhysRate gate (disasm 0x10406261-0x104065c3): the loop head is
            // only bInterpolating (0x1f8&8) and dt > 0; PhysRate is read once, in the alpha
            // formula (0x104065bd). With PhysRate <= 0 alpha does not grow, but the mover
            // is posed at the current alpha (bCollideWorld fall too), and with
            // PhysAlpha >= 1 KeyFrameReached fires.
            float physRate = UEInterpreter.AsFloat(actor.GetProp(PK.PhysRate));
            // KeyNum — ONCE before the loop (this[699], 92481-92495); BasePos,
            // OldPos, BaseRot, OldRot, KeyPos/KeyRot[k] and the smoothing type
            // shipped reads LIVE on every iteration (92590-92620): a KeyFrameReached
            // script calling InterpolateTo rewrites OldPos/OldRot/PhysAlpha, and the
            // rest of the tick runs from the new OldPos — but toward the key whose
            // index was taken before the loop.
            int keyNum = Math.Clamp(UEInterpreter.ToInt(actor.GetProp(PK.KeyNum)), 0, 7);
            if (HP1.Core.UETrace.Enabled)
                HP1.Core.UETrace.Log($"mover_interp actor={actor.GetProp(PK.Name)}"
                    + $" keynum={keyNum} prevkeynum={actor.GetProp(PK.PrevKeyNum)}"
                    + $" alpha={actor.GetProp(PK.PhysAlpha)} loc={startLoc}");

            float remaining = dt;
            int iter = 0;
            while (UEInterpreter.ToBool(actor.GetProp(PK.bInterpolating)) && remaining > 0f && iter < 64)
            {
                iter++;
                // MOVER FALL WITH bCollideWorld (Engine.dll.c:92481-92525, HP1):
                // before the interpolation step the mover falls in its zone's ZoneGravity —
                // offset (V·g)/(g·g)·g·dt + ½·g·dt², Velocity += g·dt, MoveActor
                // with the current rotation. Blocked (Time <= 0) — FindBase; otherwise
                // the travelled distance is added to KeyPos[KeyNum] and OldPos, and
                // interpolation does not finish on this iteration (cStack_19). Live
                // original (hp1gridpush.c, Lev3_Dungeon): a GridMover pushed to the
                // chasm drops by 192 at interp=1 until it touches the bottom.
                bool fell = false;
                if (UEInterpreter.ToBool(actor.GetProp(PK.bCollideWorld))
                    && (actor.GetDeclared("Actor", "Region") as UEObject)?.GetProp(PK.Zone) is UEObject fallZone)
                {
                    Vector3 g = UEInterpreter.AsVector3(fallZone.GetProp(PK.ZoneGravity));
                    Vector3 v = UEInterpreter.AsVector3(actor.GetProp(PK.Velocity));
                    float along = g.x * v.x + g.y * v.y + g.z * v.z;
                    float inv = 1f / (g.z * g.z + g.y * g.y + g.x * g.x);
                    float half = remaining * remaining * 0.5f;
                    Vector3 fall = new(inv * along * g.x * remaining + half * g.x,
                                       inv * along * g.y * remaining + half * g.y,
                                       inv * along * g.z * remaining + half * g.z);
                    actor.Props["Velocity"] = new Vector3(remaining * g.x + v.x, remaining * g.y + v.y, remaining * g.z + v.z);
                    Vector3 before = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
                    // Its own FCheckResult(1.0) for EACH move (Engine.dll.c:92526-92531).
                    var fallHit = new MoveCheck();
                    MoveActor(interp, actor, fall, UEInterpreter.AsRotator(actor.GetProp(PK.Rotation)),
                        bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false, fallHit);
                    if (fallHit.Time <= 0f)
                        FindBase(interp, actor);
                    else
                    {
                        Vector3 moved = UEInterpreter.AsVector3(actor.GetProp(PK.Location)) - before;
                        if (actor.GetProp(PK.KeyPos) is System.Collections.IList kl && keyNum < kl.Count)
                            kl[keyNum] = KeyVector(actor, "KeyPos", keyNum) + moved;
                        actor.Props["OldPos"] = UEInterpreter.AsVector3(actor.GetProp(PK.OldPos)) + moved;
                        fell = true;
                    }
                }
                float physAlpha = UEInterpreter.AsFloat(actor.GetProp(PK.PhysAlpha));
                float newAlpha = remaining * physRate + physAlpha;
                if (newAlpha <= 1f) remaining = 0f;
                else if (physAlpha >= 1f) { remaining = 0f; newAlpha = 1f; }
                else { remaining = (newAlpha - 1f) / (newAlpha - physAlpha) * remaining; newAlpha = 1f; }
                bool glide = UEInterpreter.ToInt(actor.GetProp(PK.MoverGlideType)) == 1;
                float t = glide ? 3f * newAlpha * newAlpha - 2f * newAlpha * newAlpha * newAlpha : newAlpha;

                Vector3 basePos = UEInterpreter.AsVector3(actor.GetProp(PK.BasePos));
                Vector3 oldPos = UEInterpreter.AsVector3(actor.GetProp(PK.OldPos));
                UERotator baseRot = UEInterpreter.AsRotator(actor.GetProp(PK.BaseRot));
                UERotator oldRot = UEInterpreter.AsRotator(actor.GetProp(PK.OldRot));
                Vector3 keyPos = KeyVector(actor, "KeyPos", keyNum);
                UERotator keyRot = KeyRotator(actor, "KeyRot", keyNum);

                Vector3 targetPos = oldPos + (basePos + keyPos - oldPos) * t;
                var targetRot = new UERotator(
                    oldRot.Pitch + (int)Math.Round((baseRot.Pitch + keyRot.Pitch - oldRot.Pitch) * t, MidpointRounding.ToEven),
                    oldRot.Yaw + (int)Math.Round((baseRot.Yaw + keyRot.Yaw - oldRot.Yaw) * t, MidpointRounding.ToEven),
                    oldRot.Roll + (int)Math.Round((baseRot.Roll + keyRot.Roll - oldRot.Roll) * t, MidpointRounding.ToEven));

                Vector3 curLoc = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
                // Its own FCheckResult(1.0) for EACH move (Engine.dll.c:92586-92591):
                // a standing mover (fast path) and a refusal read Time = 1. PhysAlpha
                // grows only if MoveActor RETURNED 1 (92638-92640): an encroachment
                // refusal and bStatic/!bMovable do not advance alpha.
                var interpHit = new MoveCheck();
                bool moveOk = MoveActor(interp, actor, targetPos - curLoc, targetRot,
                    bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false, interpHit);
                float frac = interpHit.Time;
                if (moveOk) actor.Props["PhysAlpha"] = physAlpha + (newAlpha - physAlpha) * frac;
                if (fell) continue;
                if (frac >= 1f)
                {
                    if (UEInterpreter.AsFloat(actor.GetProp(PK.PhysAlpha)) >= 1f)
                    {
                        actor.Props["bInterpolating"] = false;
                        if (HP1.Core.UETrace.Enabled)
                            HP1.Core.UETrace.Log($"mover_key_reached actor={actor.GetProp(PK.Name)}"
                                + $" keynum={keyNum} loc={actor.GetProp(PK.Location)}");
                        if (HP1.Core.UEPlaythroughLog.Enabled)
                            HP1.Core.UEPlaythroughLog.Note("mover",
                                $"reached:{actor.GetProp(PK.Name)}:{keyNum}",
                                $"key_reached actor={actor.GetProp(PK.Name)} keynum={keyNum}");
                        UELifecycle.CallEvent(interp, actor, "KeyFrameReached");
                    }
                }
                else
                {
                    actor.Props["bInterpolating"] = false; // blocked mid-move
                    if (HP1.Core.UETrace.Enabled)
                        HP1.Core.UETrace.Log($"mover_blocked actor={actor.GetProp(PK.Name)}"
                            + $" keynum={keyNum} frac={frac} loc={actor.GetProp(PK.Location)}");
                    if (HP1.Core.UEPlaythroughLog.Enabled)
                        HP1.Core.UEPlaythroughLog.Note("mover",
                            $"blocked:{actor.GetProp(PK.Name)}:{keyNum}",
                            $"BLOCKED actor={actor.GetProp(PK.Name)} keynum={keyNum} frac={frac}");
                }
            }
            // AActor::performPhysics, PHYS_MovingBrush (disasm 0x103e5351-0x103e539e):
            // Z in the x87 register, X and Y through float (DisplacementVelocityX87).
            actor.Props["Velocity"] = dt > 0f
                ? DisplacementVelocityX87(UEInterpreter.AsVector3(actor.GetProp(PK.Location)), startLoc, dt, 2)
                : Vector3.zero;
            Journal?.Invoke($"moving_brush actor={actor.GetProp(PK.Name)} location={actor.GetProp(PK.Location)} alpha={actor.GetProp(PK.PhysAlpha)} interp={actor.GetProp(PK.bInterpolating)}");
        }

        // ── AActor::physFalling (Engine.dll.c:84522-85423) — the FULL shipped
        // body, one state machine for every frontend (lifted from the
        // validated headless port; geometry rides WalkMoveHook/LineTraceHook).
        //  - zone-0 → FellOutOfWorld with the falling-mode Logf filter
        //    (85230-85251), stop.
        //  - Pawn air-control prepass (84759-84833): AirControl>0.15 runs a
        //    cylinder clearance trace along the predicted lateral travel
        //    (flags 6) — ANY block clamps AirControl to 0.05; the lateral
        //    acceleration budget is AirControl·AccelRate, raised by
        //    (10−speed2D)/dt below 10 u/s, collapsed to 1 at/above
        //    GroundSpeed with clamped AirControl; at/above GroundSpeed with
        //    LIVE AirControl the 2D speed is capped at its current value
        //    (the fVar1 dance, 84806-84816). Acceleration.z zeroed.
        //  - Substeps (84846-84863): remaining<=0.1 whole, else
        //    min(remaining/2, 0.1), 8 iterations; per substep the midpoint
        //    velocity V + (Gravity+Accel)·sub/2 (water: fluid drag + the
        //    buoyancy-scaled gravity; bBobbing Decoration: half gravity;
        //    player with wet feet: the FootRegion drag), the apex split
        //    (84981-85005), the 2D cap, ZoneVelocity (skipped for a non-
        //    player pawn unless |ZoneVel|²>40000, 85175-85192), ONE
        //    committed move.
        //  - A floor hit (Normal.z>0.7) gives the unused time back,
        //    re-derives Velocity from the actual displacement and lands
        //    through the full processLanded (85265-85285). A wall runs
        //    APawn::Mount + processHitWall + the slide; a second wall runs
        //    processHitWall + TwoWallAdjust; slide/adjust landings call
        //    processLanded with ZERO remaining time (shipped 85320/85345 —
        //    no walking continuation after a slide landing).
        //  - The tail (85380-85410) re-derives the velocity from the
        //    displacement minus ZoneVelocity, doubles it against the
        //    pre-substep velocity when the z-gain fell short (the midpoint
        //    integrator's second half), and clamps to ZoneTerminalVelocity.
        //  - bBounce (fork at 85264, body 85370-85379): the hit is NOT treated as a
        //    landing or a wall — the engine sends the script HitWall, exits on
        //    PHYS_None, gives the unused time back for the first two hits per call
        //    and leaves the velocity alone in the tail (85381 `& 0x11`).
        //    The bounce is set by script, not by the engine.
        // Un-reached shipped branches stay LOUD STOPs (§8), checked on EVERY
        // substep: the water-zone formula, the bBobbing Decoration formula, the
        // PlayerPawn wet-feet formula, and the swim entry (startSwimming).
        // ZoneVelocity and bBounce are ported. ──
        // MoveActor 0x103aa58c..0x103aa65d: every |component| below 0.0001f (a NaN counts as below) -> dist 0, direction =
        // Delta; otherwise dist = appSqrt((Z·Z + Y·Y) + X·X) stored as float, r = float(1/dist), direction = float(c·r).
        internal static (float dist, Vector3 dir) MoveActorDirection(Vector3 d)
        {
            static bool Small(float c) => !(Math.Abs(c) >= 0.0001f);
            if (Small(d.x) && Small(d.y) && Small(d.z)) return (0f, d);
            if (!float.IsFinite(d.x) || !float.IsFinite(d.y) || !float.IsFinite(d.z)) { float m = d.magnitude; return (m, d / m); }
            float dist = HP1.Core.X87Ext.Sqrt64(HP1.Core.X87Ext.From(Vector3.SizeSquared64(d.z, d.y, d.x).ToDouble())).ToFloat();
            float r = 1f / dist;   // fld 1.0; fdiv dword; fstp dword: one rounding of the quotient
            return (dist, new Vector3(d.x * r, d.y * r, d.z * r));
        }

        // MoveActor 0x103aaafe..0x103aab5e (Time < 1, not bNoFail): t' = ((pad + dist)·T − pad) / dist in the register;
        // t' <= 0.0001f (C0|C3: also NaN, and dist 0) refuses the move; Time = float(t'); the delta is scaled by the
        // register t' per component. Returns false on refusal.
        internal static bool MoveActorRebase(float pad, float dist, float time, Vector3 delta, out float stored, out Vector3 scaled)
        {
            stored = 0f; scaled = delta;
            if (dist == 0f) return false;   // (pad·T − pad) <= 0 over 0: −inf or NaN, both refused
            if (!float.IsFinite(pad) || !float.IsFinite(dist) || !float.IsFinite(time)
                || !float.IsFinite(delta.x) || !float.IsFinite(delta.y) || !float.IsFinite(delta.z))
            {
                float t0 = ((pad + dist) * time - pad) / dist;
                if (!(t0 > 0.0001f)) return false;
                stored = t0; scaled = delta * t0; return true;
            }
            static HP1.Core.X87Ext X(float v) => HP1.Core.X87Ext.From(v);
            static HP1.Core.X87Ext R(HP1.Core.X87Ext v) => HP1.Core.X87Ext.Round64(v);
            HP1.Core.X87Ext t = HP1.Core.X87Ext.Div64(R(R(R(X(pad) + X(dist)) * X(time)) - X(pad)), X(dist));
            if (!(t.CompareTo(X(0.0001f)) > 0)) return false;
            stored = t.ToFloat();
            scaled = new Vector3(R(t * X(delta.x)).ToFloat(), R(t * X(delta.y)).ToFloat(), R(t * X(delta.z)).ToFloat());
            return true;
        }

        // physFalling 0x103eed2a..0x103eed5e and 0x103eee88: hs = appSqrt(Vx·Vx + Vy·Vy) in the register; `fcom 10.0` and
        // `fcom GroundSpeed` test C0 (`test ah, 1`): below 10 and "not at GroundSpeed" are C0, so an unordered hs (NaN)
        // counts as below both; the cap is hs stored as float (0x103eeeb7). NaN / ±inf run the same chain (X87Ext; judge
        // nan_slope_oracle kind fall).
        internal static (float hs, bool below10, bool atGroundSpeed) FallingHorizontalSpeed(float vx, float vy, float groundSpeed)
        {
            HP1.Core.X87Ext e = Vector3.Size64Ext(vx, vy, 0f);
            return (e.ToFloat(), (HP1.Core.X87Ext.Fcom(e, HP1.Core.X87Ext.From(10f)) & HP1.Core.X87Ext.C0) != 0,
                (HP1.Core.X87Ext.Fcom(e, HP1.Core.X87Ext.From(groundSpeed)) & HP1.Core.X87Ext.C0) == 0);
        }

        // 0x103eee99..0x103eeea7: `fld AirControl; fcomp 0.05f; test ah, 0x41; je` keeps the cap only when AirControl > 0.05
        // (ordered); otherwise (also NaN) the budget collapses to 1.
        internal static bool FallingCapDropped(float airControl) => !(airControl > 0.05f);

        // 0x103eed64..0x103eed72: budget = ((10 − hs) / dt + budget) in the register, one fstp dword (dt = 0 divides to
        // ±inf or the indefinite, as the FPU with exceptions masked).
        internal static float FallingLowSpeedBudget(float vx, float vy, float budget, float dt)
        {
            var R = (Func<HP1.Core.X87Ext, HP1.Core.X87Ext>)HP1.Core.X87Ext.Round64;
            HP1.Core.X87Ext hs = Vector3.Size64Ext(vx, vy, 0f);
            return R(HP1.Core.X87Ext.Div64(R(HP1.Core.X87Ext.From(10f) - hs), HP1.Core.X87Ext.From(dt)) + HP1.Core.X87Ext.From(budget)).ToFloat();
        }

        // 0x103efc15..0x103efccc: Vx·Vx + Vy·Vy against cap·cap in the register (no sqrt); over it,
        // (Vx, Vy) = SafeNormal(Vx, Vy, 0) · cap, per-component fstp.
        internal static (float x, float y) FallingHorizontalCap(float vx, float vy, float cap)
        {
            if (!RegisterGreater(new Vector3(vx, vy, 0f), cap)) return (vx, vy);
            Vector3 c = new Vector3(vx, vy, 0f).normalized * cap;
            return (c.x, c.y);
        }

        // (X·X + Y·Y) + Z·Z > limit·limit with both sides in the register at PC64, as physFalling compares (0x103eed95,
        // 0x103efc57): st0 = limit·limit, st1 = the sum, `fcompp; test ah, 1` — C0, so an unordered side (NaN) counts as greater.
        internal static bool RegisterGreater(Vector3 v, float limit)
        {
            var l = HP1.Core.X87Ext.From(limit);
            return (HP1.Core.X87Ext.Fcom(l * l, Vector3.SizeSquared64(v.x, v.y, v.z)) & HP1.Core.X87Ext.C0) != 0;
        }

        public void PhysFalling(UEInterpreter interp, UEObject actor, float dt, int iteration = 0)
        {
            // (No hook requirement: like PhysWalking, this answers its own
            // geometry through WalkMove/LineTrace, which fall back to Core's
            // MoveActor/SingleLineCheck when no frontend override is set.)
            UEObject region = actor.GetProp(PK.Region) as UEObject;
            UEObject zone = region?.GetProp(PK.Zone) as UEObject;
            if (zone is null)
                throw new InvalidDataException(
                    $"Core PhysFalling missing zone actor={actor.GetProp(PK.Name)}");
            if (UEInterpreter.ToInt(region.GetProp(PK.ZoneNumber)) == 0)
            {
                FellOutOfWorld(interp, actor, falling: true);
                return;
            }

            Vector3 originalAcceleration = UEInterpreter.AsVector3(actor.GetProp(PK.Acceleration));
            Vector3 acceleration = originalAcceleration;
            bool pawn = UEInterpreter.ClassIsA(actor.Class, "Pawn");
            UEObject level = interp.GetLevelInfo();

            float airControl = UEInterpreter.AsFloat(actor.GetProp(PK.AirControl));
            // bBounce is ported (85374-85379).
            //
            // THIS BIT MUST NOT BE CACHED. Shipped reads it LIVE from actor memory at
            // EVERY point: at the hit-handling fork (85264, inside the substep loop) and
            // in the tail (85381) — AFTER the script HitWall has run. The event may clear
            // or set bBounce, change physics and velocity, and real campaign classes
            // have their own handlers (spellTrollRock, spellFireCracker, Jellybean,
            // WizzardCardIcon). A bool saved before the loop would pick the next
            // substep's branch from a stale value and wrongly skip the tail velocity
            // recomputation.
            // The STOP branches (water, bBobbing, player feet in water) are checked on
            // EVERY substep — where shipped picks the velocity formula (84873-84910),
            // see the loop below.

            float horizontalSpeedCap = 0f;
            if (pawn)
            {
                float effectiveAirControl = airControl;
                float accelRate = UEInterpreter.AsFloat(actor.GetProp(PK.AccelRate));
                if (effectiveAirControl > 0.15f)
                {
                    Vector3 predictedVelocity = UEInterpreter.AsVector3(actor.GetProp(PK.Velocity));
                    if (acceleration.sqrMagnitude > 0f)
                        predictedVelocity += acceleration.normalized
                            * (effectiveAirControl * accelRate);
                    Vector3 start = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
                    Vector3 end = start
                        + new Vector3(predictedVelocity.x * dt, predictedVelocity.y * dt, 0f);
                    float r = UEInterpreter.AsFloat(actor.GetProp(PK.CollisionRadius));
                    float h = UEInterpreter.AsFloat(actor.GetProp(PK.CollisionHeight));
                    (bool clearanceHit, _, _, _) = LineTrace(interp, actor, start, end,
                        new Vector3(r, r, h), 6);
                    if (clearanceHit) effectiveAirControl = 0.05f;
                }

                float maxAcceleration = effectiveAirControl * accelRate;
                acceleration.z = 0f;
                Vector3 velocity = UEInterpreter.AsVector3(actor.GetProp(PK.Velocity));
                // Disasm 0x103eed2a..0x103eed72: hs = appSqrt(Vx·Vx + Vy·Vy) stays in the register (PC64) for the
                // compares and for the budget (10 − hs) / dt + budget, rounded to float once.
                (float horizontalSpeed, bool below10, bool atGroundSpeed) = FallingHorizontalSpeed(velocity.x, velocity.y,
                    UEInterpreter.AsFloat(actor.GetProp(PK.GroundSpeed)));
                if (below10)
                    maxAcceleration = FallingLowSpeedBudget(velocity.x, velocity.y, maxAcceleration, dt);
                else if (atGroundSpeed)
                {
                    // Shipped 84806-84816: the cap survives only with LIVE
                    // AirControl; the clamped case collapses the budget to 1
                    // and drops the cap.
                    if (FallingCapDropped(effectiveAirControl)) maxAcceleration = 1f;
                    else horizontalSpeedCap = horizontalSpeed;
                }
                // 0x103eed77..0x103eeda2: (X·X + Y·Y) + Z·Z against budget·budget, both in the register.
                if (RegisterGreater(acceleration, maxAcceleration))
                    acceleration = acceleration.normalized * maxAcceleration;
                actor.Props["Acceleration"] = acceleration;
            }

            float remaining = dt;
            // bBounce hit counter. In shipped it is a stack slot (fStack_34) zeroed at
            // 84837 — BEFORE the substep loop — and incremented ONLY in the bounce branch
            // (85378). It lives for the whole PhysFalling call, not per substep: it limits
            // the time give-back to the first two hits per call (85375). The decompile
            // reuses the slot (float arithmetic before the loop), so it is counted from
            // here, not from the function start.
            int bounceHits = 0;
            // Body FCheckResult (uStack_84): FCheckResult(1.0) ONCE before the substep
            // loop (84837-84842: Time=1, Item=-1, Actor=NULL); all moves and Mount go into
            // it; the AirControl probe above uses ITS OWN local (uStack_27c). Disasm
            // (physFalling 0x103eea20): the record is [ebp-0x80], built at 0x103eee01..
            // 0x103eee23 before the loop head 0x103eee2d; MoveActor 0x103efe88/0x103f02ef/
            // 0x103f0474 and Mount 0x103f0122 pass `lea [ebp-0x80]`; the AirControl
            // SingleLineCheck 0x103eece6 passes [ebp-0x278]. The bounce counter is
            // [ebp-0x30]: zeroed 0x103eedfe, `cmp 2` 0x103effc0, `inc` 0x103effd8. A small move
            // and a MoveActor refusal leave it untouched, and the substep then handles the
            // PREVIOUS hit. A head-on wall hit gives almost zero slide: shipped sees the same
            // wall again — a second processHitWall and TwoWallAdjust (85318-85336).
            var fallHit = new MoveCheck();
            // fStack_38 (84845, 85007-85013, LAB_103efc0a): "apex already handled" flag.
            // Set as soon as the |vz|>5 thresholds pass (even if the halves are not longer
            // than 15 ms), and disables the apex check on the NEXT substep, which also
            // resets it. Form taken from the source: no distinguishing fixture input found.
            bool apexSeen = false;
            // The loop head is ONLY the remainder and the counter (disasm 0x103eee2d-
            // 0x103eee4b; three back edges 0x103f056e/0x103f0690/0x103f06d3).
            // Neither destruction nor a Physics change breaks the loop: a script that took
            // physics away from HitWall/processHitWall gets the remaining falling substeps
            // (exit on PHYS_None exists only in the bBounce branch, 85373, and on
            // bDeleteMe — after the move, 85197).
            while (remaining > 0f && iteration <= 7)
            {
                iteration++;
                float timeTick = remaining <= 0.1f ? remaining : Math.Min(remaining * 0.5f, 0.1f);
                remaining -= timeTick;

                Vector3 location = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
                Vector3 oldVelocity = UEInterpreter.AsVector3(actor.GetProp(PK.Velocity));
                actor.Props["OldLocation"] = location;
                actor.Props["bJustTeleported"] = false;

                // Zone, gravity and acceleration are read LIVE on every substep
                // (this+0xa0, +0x13c in each formula 84873-85144): a zone change by the move
                // or by script in HitWall changes them for the next substep.
                zone = (actor.GetProp(PK.Region) as UEObject)?.GetProp(PK.Zone) as UEObject
                    ?? throw new InvalidDataException(
                        $"Core PhysFalling missing zone actor={actor.GetProp(PK.Name)}");
                Vector3 gravity = UEInterpreter.AsVector3(zone.GetProp(PK.ZoneGravity));
                Vector3 liveAcceleration = UEInterpreter.AsVector3(actor.GetProp(PK.Acceleration));
                // Velocity formula choice (84873-84910; repeated at the apex 85029-85081):
                // zone water (0x2cc&1) → buoyancy and fluid friction; else
                // Decoration.bBobbing (0x260&8) → half gravity; else PlayerPawn with
                // FootRegion.Zone.bWaterZone and oldVz<0 → foot-zone friction. These three
                // branches are NOT ported — a loud STOP on every substep. Unreachable in the
                // campaign (measured): all 226 zones of 41 maps have effective
                // bWaterZone=False (classes ZoneInfo/SkyZoneInfo/WarpZoneInfo/LevelInfo
                // without a default, no map overrides; control — bPainZone=True on 46 zones
                // is visible in the same measurement); there are no bWaterZone writes in the
                // Engine.u decompile (reads only) or in package scripts, in Engine.dll only the
                // copy constructor (12391). Decoration.bBobbing is set only by
                // Decoration.ZoneChange in a water zone (Engine.u Decoration.uc:92).
                bool water = UEInterpreter.ToBool(zone.GetProp(PK.bWaterZone));
                bool bobbing = !water && UEInterpreter.ClassIsA(actor.Class, "Decoration")
                    && UEInterpreter.ToBool(actor.GetDeclared("Decoration", "bBobbing"));
                bool wetFeet = !water && !bobbing && UEInterpreter.ClassIsA(actor.Class, "PlayerPawn")
                    && oldVelocity.z < 0f
                    && UEInterpreter.ToBool(((actor.GetProp(PK.FootRegion) as UEObject)?.GetProp(PK.Zone) as UEObject)
                        ?.GetProp(PK.bWaterZone));
                if (water || bobbing || wetFeet)
                    throw new NotSupportedException(
                        $"Core PhysFalling STOP branch (Engine.dll.c:84873-84910) actor={actor.GetProp(PK.Name)} water={water} bobbing={bobbing} wetFeet={wetFeet}");

                Vector3 force = gravity + liveAcceleration;
                Vector3 midpointVelocity = oldVelocity + force * (0.5f * timeTick);

                // Apex split (Engine.dll.c:84981-85005): both halves must
                // exceed 15 ms.
                if (!apexSeen && (0f < oldVelocity.z) != (0f < midpointVelocity.z)
                    && Math.Abs(oldVelocity.z) > 5f && Math.Abs(midpointVelocity.z) > 5f)
                {
                    apexSeen = true;
                    float first = Math.Abs(oldVelocity.z)
                        / (Math.Abs(oldVelocity.z) + Math.Abs(midpointVelocity.z)) * timeTick;
                    float second = timeTick - first;
                    if (first > 0.015f && second > 0.015f)
                    {
                        remaining += second;
                        timeTick = first;
                        midpointVelocity = oldVelocity + force * (0.5f * timeTick);
                    }
                }
                else apexSeen = false;

                // Disasm 0x103efc15..0x103efccc: Vx·Vx + Vy·Vy against cap·cap in the register (no sqrt); over it,
                // (Vx, Vy) = SafeNormal(Vx, Vy, 0) · cap (per-component fstp), Z kept.
                if (horizontalSpeedCap != 0f)
                    (midpointVelocity.x, midpointVelocity.y) = FallingHorizontalCap(midpointVelocity.x, midpointVelocity.y, horizontalSpeedCap);

                actor.Props["Velocity"] = midpointVelocity;
                // Move ZoneVelocity (85175-85192, disasm 0x103efcd9-0x103efd92):
                // taken if the bIsPawn bit (Actor+0x28 & 0x2000000) is clear, or the actor
                // is a PlayerPawn, or |ZoneVelocity|² > 40000; otherwise zero.
                // The same vector is subtracted in the tail ([ebp-0x8c], 0x103f05e1).
                Vector3 zoneVelocity = UEInterpreter.AsVector3(zone.GetProp(PK.ZoneVelocity));
                if (UEInterpreter.ToBool(actor.GetProp(PK.bIsPawn))
                    && !UEInterpreter.ClassIsA(actor.Class, "PlayerPawn")
                    && !(zoneVelocity.sqrMagnitude > 40000f))
                    zoneVelocity = Vector3.zero;
                Vector3 delta = (zoneVelocity + midpointVelocity) * timeTick;
                (float hitFraction, Vector3 hitNormal, UEObject hitActor) =
                    WalkMove(interp, actor, delta, fallHit);
                Vector3 moved = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
                Log($"falling_move actor={actor.GetProp(PK.Name)} from={location} to={moved} velocity={actor.GetProp(PK.Velocity)} gravity={gravity} blocked={(hitFraction < 1f).ToString().ToLowerInvariant()} fraction={hitFraction:R} normal={hitNormal} hit={hitActor?.GetProp(PK.Name) ?? (object)"Level"}");
                if (actor.Destroyed) return;
                // 85210-85214: a pawn that moved into water (Physics==3) —
                // APawn::startSwimming with the remainder (1−Time)·dt, before hit handling.
                if (pawn && UEInterpreter.ToInt(actor.GetProp(PK.Physics)) == 3)
                    throw new NotSupportedException(
                        $"physFalling startSwimming reached (Engine.dll.c:85211) actor={actor.GetProp(PK.Name)} — port the shipped swim entry before using it");
                // 85215-85257: a DECORATION hitting a PlayerPawn decrements numLandings
                // (max(n−1, 0), bit trick 85255) — before the bBounce fork.
                if (hitFraction < 1f && hitActor is not null
                    && UEInterpreter.ClassIsA(hitActor.Class, "PlayerPawn")
                    && UEInterpreter.ClassIsA(actor.Class, "Decoration"))
                    actor.Props["numLandings"] = Math.Max(UEInterpreter.ToInt(actor.GetProp(PK.numLandings)) - 1, 0);

                if (hitFraction < 1f && UEInterpreter.ToBool(actor.GetProp(PK.bBounce)))
                {
                    // ── bBounce BRANCH (85370-85379) ─────────────────────────
                    //
                    // Shipped calls the SCRIPT HitWall event directly —
                    // FindFunctionChecked(_ENGINE_HitWall) + ProcessEvent — NOT
                    // processHitWall. The gates of the latter (exit on a pawn hitActor,
                    // the IsProbing check, the MoveTimer=-1 branch for pawns) do not run
                    // in this branch at all. Arguments as in the neighbour: normal, then
                    // the hit actor (85366-85369).
                    //
                    // There is NO delta reflection here, and that is not an omission:
                    // shipped does not reflect the move vector off the normal — the script
                    // sets the bounce from its own HitWall.
                    //
                    // All landing and wall handling is skipped under bBounce:
                    // no Mount, processHitWall, slide, TwoWallAdjust or processLanded
                    // (85264 is the fork).
                    Guard(() => UELifecycle.CallEvent(interp, actor, "HitWall",
                        hitNormal, hitActor ?? level), "HitWall");
                    // 85373: exit if the script moved physics to PHYS_None. Exactly
                    // PHYS_None (field Actor+0x30 == 0), not "anything but PHYS_Falling":
                    // for other values shipped continues the falling substeps — the loop
                    // head does not check Physics.
                    if (UEInterpreter.ToInt(actor.GetProp(PK.Physics)) == 0) return;
                    // 85375-85378: only the first two hits per call give the unused time
                    // back, but the counter always grows.
                    // `fld 1.0; fsub T; fmul dt; fadd rem; fstp` (0x103effc6-0x103effd5) —
                    // one rounding; the register is modelled by double.
                    if (bounceHits < 2) remaining = (float)((1.0 - hitFraction) * timeTick + remaining);
                    bounceHits++;
                }
                else if (hitFraction < 1f)
                {
                    if (hitNormal.z > 0.7f)
                    {
                        // Landed (85265-85285): the unused time comes back
                        // HERE (shipped gives it back only on the landed/
                        // swim/bounce branches, never for a wall slide).
                        // x87 (disasm 0x103efff4-0x103f00ce): remainder —
                        // `fld 1.0; fsub T; fmul dt; fadd rem; fstp`, one rounding; the 0.003f
                        // threshold is compared with T·dt from the register; velocity is
                        // d·(1/(T·dt)), where T·dt and the reciprocal never leave the register,
                        // d is float (FVector). The register is modelled by double.
                        remaining = (float)((1.0 - hitFraction) * timeTick + remaining);
                        if (!UEInterpreter.ToBool(actor.GetProp(PK.bJustTeleported))
                            && hitFraction > 0.1f && (double)hitFraction * timeTick > 0.003f)
                        {
                            Vector3 d = moved - location;
                            double rv = 1.0 / ((double)hitFraction * timeTick);
                            actor.Props["Velocity"] =
                                new Vector3((float)(d.x * rv), (float)(d.y * rv), (float)(d.z * rv));
                        }
                        ProcessLanded(interp, actor, hitNormal, hitActor ?? level,
                            remaining, iteration);
                        return;
                    }

                    // Wall: shipped calls APawn::Mount(Up, Hit) for every
                    // non-floor pawn contact and ignores its bool result,
                    // then continues through processHitWall/slide (85286).
                    if (pawn) TryMount(interp, actor, new Vector3(0f, 0f, 1f), fallHit);
                    ProcessHitWall(interp, actor, hitNormal, hitActor);
                    Vector3 slide = (delta - hitNormal * Vector3.Dot(delta, hitNormal))
                        * (1f - hitFraction);
                    if (Vector3.Dot(delta, slide) >= 0f)
                    {
                        Vector3 oldNormal = hitNormal;
                        (float secondFraction, Vector3 secondNormal, UEObject secondActor) =
                            WalkMove(interp, actor, slide, fallHit);
                        if (secondFraction < 1f)
                        {
                            if (secondNormal.z > 0.7f)
                            {
                                // Slide landing: ZERO remaining time (85320).
                                ProcessLanded(interp, actor, secondNormal,
                                    secondActor ?? level, 0f, iteration);
                                return;
                            }
                            ProcessHitWall(interp, actor, secondNormal, secondActor);
                            Vector3 adjusted = slide;
                            TwoWallAdjust(delta.normalized, ref adjusted, secondNormal,
                                oldNormal, secondFraction);
                            bool opposingUpSlopes = oldNormal.z > 0f && secondNormal.z > 0f
                                && adjusted.z == 0f && Vector3.Dot(oldNormal, secondNormal) < 0f;
                            (float thirdFraction, Vector3 finalNormal, UEObject thirdActor) =
                                WalkMove(interp, actor, adjusted, fallHit);
                            // 85336-85345: `bVar4 || Hit.Normal.z > 0.7` — WITHOUT Time < 1.
                            // A clean third move resets Hit (Time=1, Normal=0,
                            // Actor=NULL, 0x103aa504-0x103aa552), and opposing slopes
                            // still land — with a zero normal and Actor=NULL
                            // (setPhysics then calls FindBase).
                            if (opposingUpSlopes || finalNormal.z > 0.7f)
                            {
                                // Adjust landing: ZERO remaining time (85345).
                                ProcessLanded(interp, actor, finalNormal,
                                    thirdActor, 0f, iteration);
                                return;
                            }
                        }
                    }
                    // 85349-85362: a wall substep replaces the pre-substep
                    // velocity's xy with the ACTUAL travel over the substep
                    // (z untouched) before the tail's midpoint doubling.
                    Vector3 afterWalls = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
                    oldVelocity = new Vector3((afterWalls.x - location.x) / timeTick,
                        (afterWalls.y - location.y) / timeTick, oldVelocity.z);
                }

                // The tail (85380-85410): velocity from the actual movement
                // minus ZoneVelocity; the midpoint's second half doubles it
                // against the pre-substep velocity; ZoneTerminalVelocity
                // clamps the magnitude (an |V|² above TV² — shipped has no
                // zero guard).
                // 85381 checks BOTH bits at once (`& 0x11`): bBounce (0x1) and
                // bJustTeleported (0x10). A bounced actor must NOT get the velocity
                // recomputed from displacement, otherwise the engine would overwrite the
                // velocity the script HitWall just set.
                if (!UEInterpreter.ToBool(actor.GetProp(PK.bBounce))
                    && !UEInterpreter.ToBool(actor.GetProp(PK.bJustTeleported)))
                {
                    // FVector::operator/(dt) (0x103f05db → Core.dll 0x10119310): RV = 1/dt
                    // in x87, X·RV is rounded to float.
                    Vector3 derived = DivByFloat(UEInterpreter.AsVector3(actor.GetProp(PK.Location)) - location,
                        timeTick) - zoneVelocity;
                    if (derived.z < oldVelocity.z || oldVelocity.z >= 0f)
                        derived = derived * 2f - oldVelocity;
                    // The zone is the CURRENT one, after the move (0x103f0671: [esi+0xa0] is
                    // re-read in the tail; the move may have re-zoned the actor).
                    UEObject tailZone = (actor.GetProp(PK.Region) as UEObject)?.GetProp(PK.Zone) as UEObject ?? zone;
                    float terminal = UEInterpreter.AsFloat(tailZone.GetProp(PK.ZoneTerminalVelocity));
                    if (derived.sqrMagnitude > terminal * terminal)
                        derived = derived.normalized * terminal;
                    actor.Props["Velocity"] = derived;
                }
            }
            actor.Props["Acceleration"] = originalAcceleration;
        }

        // ── UInput::ReadInput(-1.0) (Engine.dll.c:52558-52613): the input
        // layer walks the viewport actor's properties once (cached per class),
        // collecting the CPF_Input entries (+0x3c bit 4 = 0x4), and on the
        // -1.0 reset multiplies every FLOAT one by zero (52607-52608 with
        // fVar2=0). Only floats — the property's class chain is tested
        // against UFloatProperty (52601-52605); input BYTES/BOOLS survive.
        // Shipped calls this right after the PlayerTick event (128696). ──
        static readonly Dictionary<UClass, List<string>> _inputFloatProps = new(UERefComparer<UClass>.Instance);

        // PUBLIC: the Cut* legacy half must apply the SAME reset at the SAME
        // shipped boundary — between the player events and the state frame
        // (128696 precedes ProcessState at 128703). The Unity bridge calls
        // this from TickCutEventState; the skeleton's own localPlayer branch
        // calls it below.
        public static void ResetPlayerInputAxes(UEObject actor)
        {
            if (actor.Class is null) return;
            if (!_inputFloatProps.TryGetValue(actor.Class, out List<string> names))
            {
                names = new List<string>();
                var seen = new HashSet<string>(UEPropNameComparer.Instance);
                for (var c = actor.Class; c != null; c = HP1.Core.UEClassRegistry.Shared.CanonicalSuper(c))
                    foreach (var p in c.EnumerateFields<UELib.Core.UProperty>())
                        if (p?.Name != null
                            && p.PropertyFlags.HasFlag(UELib.Flags.PropertyFlag.Input)
                            && p is UELib.Core.UFloatProperty
                            && seen.Add(p.Name.ToString()))
                            names.Add(p.Name.ToString());
                _inputFloatProps[actor.Class] = names;
            }
            foreach (string n in names) actor.Props[n] = 0f;
        }

        // ── AActor::setPhysics (Engine.dll.c:134160) — the shipped helper,
        // richer than SE's bare Physics()=newPhysics (UActor.cpp:1242): for
        // the based physics modes it (re)finds/sets the Base, for the rest it
        // clears the Base, and it zeroes Velocity/Acceleration when the LIVE
        // Physics after the base change is None/Rotating. No-op if unchanged.
        // The base probe is Core's own FindBase. ──
        public void SetPhysics(UEInterpreter interp, UEObject actor, int newPhysics, UEObject newFloor)
        {
            if (UEInterpreter.ToInt(actor.GetProp(PK.Physics)) == newPhysics) return;
            actor.Props["Physics"] = (byte)newPhysics;
            // Based set {None(0), Walking(1), Rotating(5), Rolling(7), Spider(10)}.
            bool based = newPhysics is 0 or 1 or 5 or 7 or 10;
            if (based)
            {
                if (newFloor is null) FindBase(interp, actor);
                else if (!ReferenceEquals(actor.GetProp(PK.Base) as UEObject, newFloor))
                    SetBase(interp, actor, newFloor, true);
            }
            else if (actor.GetProp(PK.Base) is UEObject)
                SetBase(interp, actor, null, true);
            // Zeroing follows the LIVE Physics, re-read after SetBase/FindBase
            // (disasm 0x103e51c8: `mov al, byte [esi+0x30]`; 0 or 5 -> Velocity
            // +0x12c and Acceleration +0x13c = 0, else exit at 0x103e522b).
            // A BaseChange event inside SetBase may change Physics from script —
            // then the new value decides, not the argument.
            if (UEInterpreter.ToInt(actor.GetProp(PK.Physics)) is 0 or 5)
            {
                actor.Props["Velocity"] = Vector3.zero;
                actor.Props["Acceleration"] = Vector3.zero;
            }
        }

        // The shared MSVCRT stream (shipped appFrand) for the physics bodies
        // that consume RNG (processLanded's Carcass slide, 34122). Frontends
        // wire it to the ONE global stream; a reached consumer without the
        // hook is a loud modelling error, never a silent skip.
        // The VALUE is st0 of appFrand(): rand()·C in a 64-bit mantissa, NOT rounded
        // to float. Shipped gameplay bodies (PC64) compare it with `fcomp` and build
        // 2f−1 from it before any memory store; only a consumer whose disasm has
        // `fstp dword` rounds it.
        public Func<HP1.Core.X87Ext> FrandHook;

        // ── AActor::processLanded (Engine.dll.c:33689-34199) — the FULL
        // shipped body:
        //  1. A NON-pawn landing in a bBounceVelocity zone with a non-zero
        //     ZoneVelocity is kicked out with ZoneVelocity + (0,0,80) and
        //     stays falling (33874-33896).
        //  2. A Decoration with numLandings<5 runs four corner probes
        //     (±radius/2 offsets, 8 down, extent (r/2, r/2, h)); support on
        //     both diagonals with ≥2 corners re-launches it with the
        //     imbalance-vector tumble velocity and numLandings++ (33909-34110).
        //     A Carcass on a slope (Normal.z<0.9) with bSlidingCarcass slides
        //     off with Normal·120 (z=70), numLandings++ on appFrand<0.2
        //     (34112-34136). Otherwise numLandings resets.
        //  3. The Landed(HitNormal) event; if the script left the actor
        //     falling, a Pawn returns to Walking and anything else to None
        //     with Velocity zeroed — through the full shipped setPhysics with
        //     the landing floor actor (34150-34175).
        //  4. A Pawn that IS walking normalizes Acceleration and continues
        //     physWalking with the remaining substep time when it exceeds
        //     10 ms — the same tick (34177-34195). Callers without remaining
        //     time (the legacy player bridge) default to 0 → no continuation.
        // ──
        public void ProcessLanded(UEInterpreter interp, UEObject actor, Vector3 normal,
            UEObject floorActor, float remainingTime = 0f, int iteration = 0)
        {
            bool pawn = UEInterpreter.ClassIsA(actor.Class, "Pawn");
            UEObject zone = (actor.GetProp(PK.Region) as UEObject)?.GetProp(PK.Zone) as UEObject;
            if (!pawn && zone is not null && UEInterpreter.ToBool(zone.GetProp(PK.bBounceVelocity)))
            {
                Vector3 zoneVel = UEInterpreter.AsVector3(zone.GetProp(PK.ZoneVelocity));
                if (zoneVel != Vector3.zero)
                {
                    actor.Props["Velocity"] = zoneVel + new Vector3(0f, 0f, 80f);
                    return;
                }
            }
            // Carcass sliding is nested in `numLandings < 5` (34112-34146): with
            // numLandings >= 5 only the counter is reset.
            if (UEInterpreter.ClassIsA(actor.Class, "Decoration")
                && UEInterpreter.ToInt(actor.GetProp(PK.numLandings)) < 5)
            {
                if (LandedDecorationTumble(interp, actor))
                    return;
                if (UEInterpreter.ClassIsA(actor.Class, "Carcass") && normal.z < 0.9f
                    && UEInterpreter.ToBool(actor.GetProp(PK.bSlidingCarcass)))
                {
                    if (FrandHook is null)
                        throw new NotSupportedException(
                            $"processLanded Carcass slide reached (Engine.dll.c:34122) actor={actor.GetProp(PK.Name)} without FrandHook — wire the shared appFrand stream");
                    // 0x103ee1f2: fcomp dword 0.2 on st0 — exact comparison.
                    if (FrandHook().CompareTo(HP1.Core.X87Ext.From(0.2f)) < 0)
                        actor.Props["numLandings"] =
                            UEInterpreter.ToInt(actor.GetProp(PK.numLandings)) + 1;
                    actor.Props["Velocity"] = new Vector3(normal.x * 120f, normal.y * 120f, 70f);
                    return;
                }
                actor.Props["numLandings"] = 0;
            }
            else if (UEInterpreter.ClassIsA(actor.Class, "Decoration"))
                actor.Props["numLandings"] = 0;
            Guard(() => UELifecycle.CallEvent(interp, actor, "Landed", normal), "Landed");
            if (UEInterpreter.ToInt(actor.GetProp(PK.Physics)) == 2)
            {
                SetPhysics(interp, actor, pawn ? 1 : 0, floorActor);
                if (!pawn) actor.Props["Velocity"] = Vector3.zero;
            }
            // The falling-body call sites carry the substep bookkeeping; the
            // journal line sits between the physics fallout and the walking
            // continuation, exactly where the pre-lift headless host put it.
            if (remainingTime > 0f || iteration > 0)
                Log($"falling_landed actor={actor.GetProp(PK.Name)} physics={actor.GetProp(PK.Physics)}");
            if (pawn && UEInterpreter.ToInt(actor.GetProp(PK.Physics)) == 1)
            {
                Vector3 accel = UEInterpreter.AsVector3(actor.GetProp(PK.Acceleration));
                actor.Props["Acceleration"] = accel.sqrMagnitude > 0f ? accel.normalized : Vector3.zero;
                if (remainingTime > 0.01f)
                    PhysWalking(interp, actor, remainingTime, iteration);
            }
        }

        // The Decoration landing wobble (Engine.dll.c:33909-34110): a centre
        // POINT probe first (from 0.8·CollisionHeight below the centre, down
        // CollisionHeight+CollisionRadius+8) — a supported centre is stable
        // and skips the wobble. An unsupported centre runs four corner
        // probes (±CollisionRadius/2 offsets, 8 down, extent (r/2, r/2, h));
        // the corner counters are ULevel::SingleLineCheck answers (ULevel vtable +0xc4,
        // disasm 0x103ed964/0x103edb74/0x103edd82/0x103edf90), which returns
        // `Hit == NULL` (97953-98015): 1 when the path is CLEAR. So shipped counts
        // UNsupported corners: ≥2 clear corners on BOTH diagonals → numLandings++ and a
        // tumble velocity towards the clear corners (off the edge), scale
        // 2·clamp(−Velocity.z, 30, CollisionRadius+30). Returns true when the tumble
        // restarted the decoration.
        internal bool LandedDecorationTumble(UEInterpreter interp, UEObject actor)
        {
            // (The corner probes go through LineTrace, which answers from Core's
            // own SingleLineCheck when no frontend override is set — this used to
            // demand a hook and threw the moment a frontend stopped supplying one.)
            Vector3 loc = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
            float r = UEInterpreter.AsFloat(actor.GetProp(PK.CollisionRadius));
            float h = UEInterpreter.AsFloat(actor.GetProp(PK.CollisionHeight));
            Vector3 centreStart = new(loc.x, loc.y, loc.z - h * 0.8f);
            Vector3 centreEnd = new(loc.x, loc.y, loc.z - (h + r + 8f));
            (bool centreHit, _, _, _) =
                LineTrace(interp, actor, centreStart, centreEnd, Vector3.zero, 7);
            if (centreHit) return false;
            // Corner probe extent (Engine.dll.c:33952-33956, call 0x103ed6a9):
            // e = GetCylinderExtent(this); (e.x·0.5, e.y·0.5, e.z·0.5 + e.z·0.5).
            // Corner offsets use the RAW CollisionRadius·0.5 (33957-33958).
            Vector3 cylExt = GetCylinderExtent(actor);
            float halfZ = cylExt.z * 0.5f;
            Vector3 extent = new(cylExt.x * 0.5f, cylExt.y * 0.5f, halfZ + halfZ);
            int Probe(float dx, float dy)
            {
                Vector3 start = new(loc.x + dx, loc.y + dy, loc.z);
                Vector3 end = new(start.x, start.y, start.z - 8f);
                (bool blocked, _, _, _) = LineTrace(interp, actor, start, end, extent, 7);
                return blocked ? 0 : 1;                  // SingleLineCheck: 1 = free
            }
            int t1 = Probe(r * 0.5f, r * 0.5f);
            int t2 = Probe(-r * 0.5f, r * 0.5f);
            int t3 = Probe(-r * 0.5f, -r * 0.5f);
            int t4 = Probe(r * 0.5f, -r * 0.5f);
            if (t1 + t2 + t3 + t4 <= 1 || t1 + t3 == 0 || t2 + t4 == 0) return false;
            actor.Props["numLandings"] = UEInterpreter.ToInt(actor.GetProp(PK.numLandings)) + 1;
            float imbX = (t1 + t4) - t2 - t3;
            float imbY = (t1 + t2) - t3 - t4;
            float k = 2f * Math.Clamp(-UEInterpreter.AsVector3(actor.GetProp(PK.Velocity)).z,
                30f, r + 30f);
            actor.Props["Velocity"] = new Vector3(imbX * k, imbY * k, k * 0.5f);
            return true;
        }

        // ── AActor::processHitWall (Engine.dll.c): a wall hit against a Pawn
        // fires nothing. A Pawn with no HitWall probe (and not falling) aborts
        // its move by setting MoveTimer=-1 so the next moveToward poll
        // completes; otherwise the HitWall(Normal, HitActor) event fires. ──
        public void ProcessHitWall(UEInterpreter interp, UEObject actor, Vector3 normal,
            UEObject hitActor)
        {
            // AActor::processHitWall (Engine.dll.c:161677-161770, disasm
            // 0x103ecf20-0x103ed13d):
            //  * hitting a pawn — nothing;
            //  * pawn: Acceleration == 0 → exit without event and MoveTimer;
            //    dir = SafeNormal(Destination − Location); for a walking pawn
            //    (Physics==1) the z of the normal AND of dir are zeroed — the event normal too
            //    (0x103ed04e writes the argument); dir·N > MinHitWall → exit;
            //    not IsProbing(HitWall) and not falling → MoveTimer=-1, bFromWall=1;
            //  * others: not IsProbing(HitWall) → exit;
            //  * HitWall(N, Wall) event through ProcessEvent.
            if (hitActor is not null && UEInterpreter.ClassIsA(hitActor.Class, "Pawn")) return;
            bool probing = UEInterpreter.IsProbing(actor, "HitWall");
            if (UEInterpreter.ClassIsA(actor.Class, "Pawn"))
            {
                if (UEInterpreter.AsVector3(actor.GetProp(PK.Acceleration)) == Vector3.zero) return;
                Vector3 dir = (UEInterpreter.AsVector3(actor.GetProp(PK.Destination))
                    - UEInterpreter.AsVector3(actor.GetProp(PK.Location))).normalized;
                int physics = UEInterpreter.ToInt(actor.GetProp(PK.Physics));
                if (physics == 1) { normal = new Vector3(normal.x, normal.y, 0f); dir = new Vector3(dir.x, dir.y, 0f); }
                double dot = (double)dir.z * normal.z + (double)normal.x * dir.x + (double)dir.y * normal.y;
                if (UEInterpreter.AsFloat(actor.GetProp(PK.MinHitWall)) < dot) return;
                if (!probing && physics != 2)
                {
                    actor.Props["MoveTimer"] = -1f;
                    actor.Props["bFromWall"] = true;
                    return;
                }
            }
            else if (!probing) return;
            Vector3 eventNormal = normal;
            Guard(() => UELifecycle.CallEvent(interp, actor, "HitWall", eventNormal, hitActor), "HitWall");
        }

        // ── MoveActor's actor-block Bump pair (Engine.dll.c:124148-124166):
        // both parties get Bump, hit actor first. The shipped gates live IN
        // the primitive so every frontend call site shares them: no pair for
        // the world sentinel (Actors[0]/LevelInfo, 124157) and none when the
        // mover is based — transitively — on the blocker (AActor::IsBasedOn,
        // 124158; a pawn must not Bump its own lift). ──
        public void DispatchBump(UEInterpreter interp, UEObject actor, UEObject blockActor)
        {
            if (actor is null || blockActor is null) return;
            if (ReferenceEquals(blockActor, interp.GetLevelInfo())) return;
            if (IsBasedOn(actor, blockActor)) return;
            Guard(() => UELifecycle.CallEvent(interp, blockActor, "Bump", actor), "Bump");
            Guard(() => UELifecycle.CallEvent(interp, actor, "Bump", blockActor), "Bump");
        }

        // AActor::IsBasedOn: walk the actor's Base chain (self included)
        // looking for the target.
        public static bool IsBasedOn(UEObject actor, UEObject target)
        {
            for (UEObject p = actor; p is not null; p = p.GetProp(PK.Base) as UEObject)
                if (ReferenceEquals(p, target)) return true;
            return false;
        }

        // The Owner-chain twin of IsBasedOn: ULevel::SingleLineCheck skips any
        // hit actor reachable from the source through Owner (98017-98021 walks
        // param_2 by +0x38 before the class branches), and SpawnActor's owner
        // filtering uses the same walk. Core-owned so the collision consumers
        // cannot drift apart on it.
        public static bool IsInOwnerChain(UEObject self, UEObject candidate)
        {
            for (UEObject current = self; current is not null;
                 current = current.GetProp(PK.Owner) as UEObject)
                if (ReferenceEquals(current, candidate)) return true;
            return false;
        }

        // Component-wise rotator identity — the shipped MoveActor rotation
        // comparison (123791-123819 tests Pitch/Yaw/Roll against the current
        // ones), NOT a tolerance compare.
        public static bool SameRotator(UERotator a, UERotator b)
            => a.Pitch == b.Pitch && a.Yaw == b.Yaw && a.Roll == b.Roll;

        // ── The zone-0 heads of the shipped pawn physics bodies — the actor
        // left the world. physWalking (Engine.dll.c:290948-290959): Logf when
        // Role==ROLE_Authority, then eventFellOutOfWorld, stop. physFalling
        // (85230-85251): the Logf additionally filters Inventory/Decoration/
        // Pawn; the event fires for EVERY actor, stop. (physFlying zone-0
        // destroys a non-player with a "flew out of the world" Logf and is a
        // silent exit for bIsPlayer; physProjectile destroys — those
        // policies stay at their shipped physics call sites.) ──
        public void FellOutOfWorld(UEInterpreter interp, UEObject actor, bool falling)
        {
            if (UEInterpreter.ToInt(actor.GetProp(PK.Role)) == 4
                && (!falling
                    || UEInterpreter.ClassIsA(actor.Class, "Inventory")
                    || UEInterpreter.ClassIsA(actor.Class, "Decoration")
                    || UEInterpreter.ClassIsA(actor.Class, "Pawn")))
                Log($"fell_out_of_world actor={actor.GetProp(PK.Name)} mode={(falling ? "falling" : "walking")}");
            Guard(() => UELifecycle.CallEvent(interp, actor, "FellOutOfWorld"), "FellOutOfWorld");
        }

        // ── ULevel::SpawnActor (Engine.dll.c:20085-20306): FindSpot before
        // construction, class+counter name, {LevelInfo,-1,0} region seed, hash
        // residency before Spawned, the Spawned/PreBeginPlay/BeginPlay →
        // SetActorZone(force) → PostBeginPlay → encroachment re-check →
        // SetInitialState order, then the base probe. ──
        public UEObject SpawnActor(UEInterpreter interp, UEObject self, UClass cls, UEObject owner,
            string tag, Vector3 location, UERotator rotation,
            Func<UEObject> premadeFactory = null, UEObject instigator = null,
            bool noCollisionFail = false)
        {
            // ── CLASS CANONICALISATION ───────────────────────────────────────
            //
            // A cross-package class literal arrives as an IMPORT STUB: same name, no body.
            // UE1 links it to the real class of that name, so spawn must take the same step
            // HERE — before anyone reads defaults or looks up functions. Otherwise e.g.
            // `HUDType` (an import) spawns the engine HPHud as a stub whose PostRender does
            // not resolve, and the HUD is empty. This step has one place — here.
            if (HP1.Core.UEClassRegistry.IsImportStub(cls) && HP1.Core.UEClassRegistry.Shared is not null)
            {
                UClass canonical = HP1.Core.UEClassRegistry.Shared.FindForStub(cls);
                if (canonical is not null && !HP1.Core.UEClassRegistry.IsImportStub(canonical))
                    cls = canonical;
            }
            // ── ENTRY REFUSALS OF ULevel::SpawnActor (disasm 0x103a65ef..0x103a66fc) ──
            // in order: abstract class (UClass+0x488 & CLASS_Abstract), class not derived
            // from AActor, and outside the editor — bStatic (default +0x28 bit 1)
            // or bNoDelete (bit 4) of the default object: "SpawnActor failed because
            // class %s has bStatic or bNoDelete", NULL without side effects.
            // The same refusal makes AMover::Spawned (0x103be430) unreachable:
            // bNoDelete=True on Mover and all eleven of its campaign subclasses, and the
            // campaign never spawns a mover (Spawn census: no literals, no mover class among
            // Class<Actor> values in .unr and defaults; Summon under bCheatsEnabled also
            // stops here).
            {
                string refuse = (UEObjectConfig.ClassFlags(cls) & 0x1u) != 0 ? "abstract"
                    : !UEInterpreter.ClassIsA(cls, "Actor") ? "not_actor"
                    : UEInterpreter.ToBool(UEObject.ClassDefaultScalar(cls, "bStatic"))
                      || UEInterpreter.ToBool(UEObject.ClassDefaultScalar(cls, "bNoDelete")) ? "static_nodelete"
                    : null;
                if (refuse is not null)
                {
                    Log($"spawn_fail class={cls.Name} reason={refuse} location={location}");
                    return null;
                }
            }
            // Shipped SpawnActor reads the placement gate off the CLASS
            // DEFAULT object and runs FindSpot BEFORE any actor exists
            // (Engine.dll.c:20085-20113) — a refused spawn has NO observable
            // side effects: no ObjectId, no name-counter tick, no list entry,
            // no frontend proxy. The scalar class-default reads below are the
            // template reads (identical values to a LoadDefaults instance).
            object Def(string n) => UEObject.ClassDefaultScalar(cls, n);
            Log($"spawn_request class={cls.Name} self={self?.GetProp(PK.Name)} owner={owner?.GetProp(PK.Name)} location={location} rotation={rotation} collideWorld={Def("bCollideWorld")} collideWhenPlacing={Def("bCollideWhenPlacing")} collideActors={Def("bCollideActors")} blockActors={Def("bBlockActors")} blockPlayers={Def("bBlockPlayers")} radius={Def("CollisionRadius")} height={Def("CollisionHeight")}");
            // FindSpot (ULevel vtable 0xb4), gated on the template's
            // bCollideWorld or bCollideWhenPlacing. A spot that cannot be
            // adjusted fails the spawn → NULL. Actor-vs-actor encroachment is
            // NOT checked here: it runs after PostBeginPlay.
            // bNoCollisionFail (param_13) skips the probe entirely (20100):
            // travel-inventory respawn passes 1 (Engine 0x103a8a1d).
            if (!noCollisionFail
                && (UEInterpreter.ToBool(Def("bCollideWorld"))
                    || UEInterpreter.ToBool(Def("bCollideWhenPlacing"))))
            {
                // Extent = GetCylinderExtent(template) (Engine.dll.c:20102,
                // call 0x103a67d8), not (R, R, H).
                Vector3 spot = location;
                Vector3 spawnExtent = GetCylinderExtentOfTemplate(cls);
                // No Bsp means no world geometry is loaded: no probe runs and
                // no journal line is emitted (fits stays null).
                bool? fits = SpawnFindSpotHook is not null
                    ? SpawnFindSpotHook(spawnExtent, ref spot, true)
                    : Bsp is not null ? (bool?)FindSpot(spawnExtent, ref spot, true) : null;
                if (fits is not null)
                {
                    Log($"spawn_probe class={cls.Name} fit={(fits.Value ? "true" : "false")} "
                        + $"location={location} spot={spot}");
                    if (!fits.Value)
                    {
                        Log($"spawn_fail class={cls.Name} reason=findspot location={location}");
                        return null;
                    }
                    location = spot;
                }
            }

            // Construction happens only AFTER the placement probe passed
            // (shipped 20115+). A frontend factory builds its proxy here (the
            // Unity GameObject + component Awake → UEObject + defaults); a
            // factory refusal (class unresolvable) fails the spawn.
            UEObject actor = premadeFactory is not null
                ? premadeFactory()
                : new UEObject { Class = cls, Interp = interp };
            if (actor is null)
            {
                Log($"spawn_fail class={cls.Name} reason=frontend_construct location={location}");
                return null;
            }
            if (premadeFactory is null) actor.LoadDefaults();

            actor.Props["Name"] = new UEName(MakeUniqueSpawnName(interp, cls));
            // StaticConstructObject(Class, Level->GetOuter(), ...) — the outer of a
            // spawned actor is the level package (Engine.dll 0x103a6863-0x103a6881).
            actor.OuterPath = LevelPackageName;
            actor.Props["Location"] = location;
            actor.Props["OldLocation"] = location;
            actor.Props["Rotation"] = rotation;
            // ULevel::SpawnActor sets Tag = class FName (Engine.dll.c:20129,
            // +0x84 <- Class+0x20). execSpawn overwrites it with its SpawnTag
            // argument only AFTER SpawnActor returns (0x1040cb87, +0x84), so
            // Spawned/PreBeginPlay/BeginPlay/PostBeginPlay/SetInitialState see
            // the class name — see the tail of this method.
            actor.Props["Tag"] = new UEName(cls.Name);
            actor.Props["Level"] = self?.GetProp(PK.Level) ?? interp.GetLevelInfo();
            // bTicked = !XLevel->Ticked (Engine.dll.c:20191-20193): the spawn ticks in
            // the current or next actor pass.
            actor.Props.Set(PK.bTicked, LevelTicked ? BoxFalse : BoxTrue);
            // Shipped SpawnActor initializes Region to {LevelInfo, -1, zone 0}
            // (Engine.dll.c:20228-20232); the REAL zone comes from SetActorZone
            // only after BeginPlay. Spawned/PreBeginPlay/BeginPlay see LevelInfo.
            actor.SetDeclared("Actor", "Region", UEObject.MakeRegion(interp.GetLevelInfo(), -1, 0));
            // Owner and Instigator are the caller's explicit arguments — the
            // spawner is NOT an implicit owner (execSpawn 0x1040ca54 defaults
            // SpawnOwner to NULL and passes its own Instigator, 0x1040cb55).
            if (owner is not null)
            {
                actor.Props["Owner"] = owner;
                UFunction gained = UELifecycle.ActorEventsBlocked(owner) ? null : interp.FindEventFunction(owner, "GainedChild");
                if (gained is not null) Guard(() => interp.Invoke(owner, gained, actor), "GainedChild");
            }
            if (instigator is not null) actor.Props["Instigator"] = instigator;

            Actors.Add(actor);
            ActorsById[actor.ObjectId] = actor;
            interp.SpawnLog.Add(actor);
            // Shipped SpawnActor hashes the new actor right after the transform
            // fields are set, BEFORE Spawned/PreBeginPlay (Engine.dll.c:20216).
            if (UEInterpreter.ToBool(actor.GetProp(PK.bCollideActors))) HashAddActor(actor);
            Log($"spawn class={cls.Name} name={actor.GetProp(PK.Name)} owner={owner?.GetProp(PK.Name)}");
            SpawnPrepareHook?.Invoke(actor);
            void RunEvent(string evt)
            {
                if (actor.Destroyed) return;
                if (UELifecycle.ActorEventsBlocked(actor)) return;   // AActor::ProcessEvent: before bBegunPlay
                UFunction fn = interp.FindEventFunction(actor, evt);
                if (fn is null) return;
                if (Journal is not null && DescribeCandidates is not null)
                {
                    Log($"spawn_event class={cls.Name} event={evt} begin candidates={DescribeCandidates(fn)}");
                    if (evt == "PostBeginPlay")
                    {
                        UFunction timer = interp.FindEventFunction(actor, "Timer");
                        if (timer is not null) Log($"spawn_event class={cls.Name} Timer candidates={DescribeCandidates(timer)}");
                    }
                }
                Guard(() => interp.Invoke(actor, fn), evt);
                Log($"spawn_event class={cls.Name} event={evt} end");
            }

            // Shipped event order (Engine.dll.c:20260-20291): Spawned,
            // PreBeginPlay, BeginPlay, THEN the real zone via SetActorZone,
            // then PostBeginPlay and SetInitialState.
            // Virtual InitExecution — before the first event (20258).
            InitExecutionActor(actor);
            // AActor::Spawned (vtable +0x84, 20259) — between InitExecution (+0x38)
            // and the Spawned event: CT3 -> ValidateActor.
            ValidateActorCollision(actor, postLoad: false);
            RunEvent("Spawned");
            RunEvent("PreBeginPlay");
            RunEvent("BeginPlay");
            // bDeleteMe after BeginPlay (+0x28 bit 0x200, `test ah,2; jne 0x103a665b`, 0x103a6bee..0x103a6bf4): return
            // NULL at once — no zone, no PostBeginPlay, no encroachment check.
            if (actor.Destroyed) return null;
            // Shipped SpawnActor: SetActorZone(Actor, iTest, bForceRefresh=1) — it reads the live Location, which
            // Spawned/PreBeginPlay/BeginPlay may have changed (SetLocation).
            SetActorZone(interp, actor, UEInterpreter.AsVector3(actor.GetProp(PK.Location)), bTest: false, bForceRefresh: true);
            Log($"spawn_zone class={cls.Name} region={actor.GetDeclared("Actor", "Region")}");
            RunEvent("PostBeginPlay");
            // Shipped re-checks encroachment AFTER PostBeginPlay (vtable 0xbc,
            // Engine.dll.c:20272-20290; bTouchActors=0 per asm 0x103a6c57) and
            // destroys the spawn on a hit. Disasm 0x103a6c2b..0x103a6c30: the whole block
            // (hash remove, CheckEncroachment, hash re-add) is skipped when bNoCollisionFail
            // (param_13, [ebp+0x34]) is set. The test reads the LIVE Location [esi+0xfc] and
            // Rotation [esi+0x108] (0x103a6c5e..0x103a6c8f): PostBeginPlay may have moved the
            // actor. There is no bDeleteMe test between PostBeginPlay and the call: an actor
            // PostBeginPlay destroyed is still checked (FCollisionHash Remove/AddActor exit on
            // bDeleteMe, CheckEncroachment 0x103ab620 does not test it, DestroyActor
            // FUN_103a72c0 returns 1 at once for bDeleteMe).
            // Core does the encroachment check ITSELF: its own CheckEncroachment walks its own
            // collision hash in shipped candidate order.
            if (!noCollisionFail)
            {
                // The actor leaves the hash for its own encroachment test
                // (shipped 20275) so it cannot appear among its own candidates.
                if (UEInterpreter.ToBool(actor.GetProp(PK.bCollideActors))) HashRemoveActor(actor);
                if (CheckEncroachment(interp, actor, UEInterpreter.AsVector3(actor.GetProp(PK.Location)),
                        UEInterpreter.AsRotator(actor.GetProp(PK.Rotation)), bTouchActors: false))
                {
                    DestroyActor(interp, actor);
                    return null;
                }
                if (UEInterpreter.ToBool(actor.GetProp(PK.bCollideActors))) HashAddActor(actor);
            }
            RunEvent("SetInitialState");
            // Base-less colliding Decoration/Inventory/Pawn spawns probe for a
            // base when their physics is None or Rotating (Engine.dll.c:
            // 20169-20172, 20292-20306). No bDeleteMe test after PostBeginPlay (0x103a6cce..0x103a6d70): an actor
            // PostBeginPlay destroyed still gets the base probe from its live Base/bCollideWorld/Physics, and
            // SpawnActor returns its (deleted) pointer — script sees a non-None reference with bDeleteMe set.
            if (actor.GetProp(PK.Base) is null
                && UEInterpreter.ToBool(actor.GetProp(PK.bCollideWorld))
                && (UEInterpreter.ClassIsA(actor.Class, "Decoration")
                    || UEInterpreter.ClassIsA(actor.Class, "Inventory")
                    || UEInterpreter.ClassIsA(actor.Class, "Pawn")))
            {
                int spawnPhysics = UEInterpreter.ToInt(actor.GetProp(PK.Physics));
                if (spawnPhysics == 0 || spawnPhysics == 5)
                    FindBase(interp, actor);
            }
            // execSpawn: `if (Spawned) Spawned->Tag = SpawnTag` (Engine.dll.c:8582).
            // tag == null — not called from execSpawn (engine spawn), no tag.
            if (tag is not null) actor.Props["Tag"] = new UEName(tag);
            return actor;
        }

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
        // on each other. This was spread across three frontend calls plus a hook
        // assignment, which meant every new frontend had to re-derive both the
        // steps and their order from the working one — and Unity, which never
        // did, ends up with a null Bsp and an InitCollisionHash that throws on
        // the first brush or mesh actor.
        //
        // Actor population stays with the frontend (each builds actors its own
        // way); call this BEFORE it, then InitCollisionHash() after.
        // Diagnostic only: the frontend's rendering of a function's native
        // candidates for the lifecycle journal. Never affects what runs.
        public Func<UFunction, string> LifecycleCandidatesHook;

        // The four loaded-level phases, in order, over the FULL actor graph
        // (Engine.dll.c:247428-247464: PreBeginPlay, BeginPlay, SetActorZone, PostBeginPlay, SetInitialState). Spawned actors use the synchronous
        // path in SpawnActor instead.
        //
        // Core-owned because the answer to "which actors get a lifecycle" is the
        // level's, not the frontend's: a frontend driving these off its own proxy list
        // would silently skip BeginPlay for actors it did not build.
        //
        // The LOADED set is captured ONCE, before the phases — not per phase.
        // A PreBeginPlay spawns (PlayerReplicationInfo, Narrator, the dialog and
        // emote singletons), and a spawned actor already ran its own
        // Spawned/PreBeginPlay/BeginPlay/PostBeginPlay synchronously inside
        // SpawnActor. Re-snapshotting per phase would sweep those into the loaded pass
        // and give them a SECOND BeginPlay (78 extra invocations on Tut1, caught
        // by the pinned lifecycle journal).
        //
        // Order WITHIN the phase per shipped (Engine.dll.c:247428..247464): between
        // BeginPlay and PostBeginPlay there is a separate SetActorZone pass over ALL loaded
        // actors (247444, level vtable +0xb0); without it the Region of placed actors stays
        // the seed {LevelInfo, iLeaf=-1, zone 0} until their first move.
        public void RunLoadedLevelLifecycle(UEInterpreter interp)
        {
            if (interp is null) throw new ArgumentNullException(nameof(interp));
            // FIVE INDEX LOOPS OVER THE LIVE ARRAY, AS IN SHIPPED
            // (Engine.dll.c:247428-247464): each loop re-reads `Actors.Num()` every step and
            // takes the actor if the slot is non-empty and `!bScriptInitialized` (bit 0x10000
            // of word AActor+0x14c — prop_offsets oracle). This guard is why PreBeginPlay
            // spawns get no second BeginPlay: SpawnActor ran SetInitialState for them
            // synchronously, and Actor.uc:SetInitialState sets bScriptInitialized. A one-time
            // snapshot would differ on maps where ULevel::Actors holds one actor in two slots
            // (see UEActorGraphLoader): shipped gives such an actor
            // PreBeginPlay/BeginPlay/PostBeginPlay TWICE and SetInitialState once (the second
            // pass already sees the bit).
            static bool Eligible(UEObject actor) =>
                actor is not null && !actor.Destroyed
                && !UEInterpreter.ToBool(actor.GetProp(PK.bScriptInitialized));
            void EventSweep(string phase)
            {
                Log($"phase {phase} begin");
                for (int i = 0; i < Actors.Count; i++)
                {
                    LoadProgress?.Invoke();
                    UEObject actor = Actors[i];
                    if (!Eligible(actor)) continue;
                    UFunction fn = interp.FindEventFunction(actor, phase);
                    if (fn is null) continue;
                    int export = UEInterpreter.ToInt(actor.GetProp(PK.ExportIndex));
                    Log($"event {phase} actor={export}:{actor.GetProp(PK.Name)}:{actor.Class?.Name} "
                        + $"begin candidates={LifecycleCandidatesHook?.Invoke(fn)}");
                    interp.Invoke(actor, fn);
                    Log($"event {phase} actor={export} end "
                        + $"destroyed={actor.Destroyed.ToString().ToLowerInvariant()}");
                }
                Log($"phase {phase} end");
            }
            EventSweep("PreBeginPlay");
            EventSweep("BeginPlay");
            LoadedLevelZonePass(interp);
            EventSweep("PostBeginPlay");
            EventSweep("SetInitialState");
        }

        // Zone pass between BeginPlay and PostBeginPlay (Engine.dll.c:247444-247448).
        //
        // Arguments come from the disassembly (Ghidra lost them at this call site,
        // 0x1039df3f):
        //     mov edx,[ecx]; push 1; push 1; push eax; call [edx+0xb0]
        // i.e. SetActorZone(Actor, bTest=1, bForceRefresh=1) — exactly as in
        // SpawnActor (0x103a6c57), where Ghidra did show them.
        // bForceRefresh=1 resets Region/FootRegion/HeadRegion to {Actors(0), -1, 0} and
        // recomputes them from scratch, and bTest=1 suppresses zone events during load.
        // With (false, false) the new zone would be compared to the SERIALIZED one and
        // ActorLeaving/ZoneChange/ActorEntered would fire during bootstrap.
        //
        // Filter — the bScriptInitialized guard (`test [eax+0x14c],0x10000`,
        // 0x1039df33; field Actor+0x14c bit 0x10000 —
        // audit/coverage_2026-10-02/field_coverage.tsv), the same as the neighbouring
        // event passes. bDeleteMe is a different field (+0x28 bit 0x200).
        void LoadedLevelZonePass(UEInterpreter interp)
        {
            Log("phase SetActorZone begin");
            // Same live walk and same bScriptInitialized guard as the event passes
            // (the third shipped loop, Engine.dll.c:247444).
            for (int i = 0; i < Actors.Count; i++)
            {
                UEObject actor = Actors[i];
                if (actor is null || actor.Destroyed
                    || UEInterpreter.ToBool(actor.GetProp(PK.bScriptInitialized))) continue;
                SetActorZone(interp, actor, UEInterpreter.AsVector3(actor.GetProp(PK.Location)),
                    bTest: true, bForceRefresh: true);
            }
            Log("phase SetActorZone end");
        }

        // ULevel::SpawnActor and level load both call the virtual InitExecution before
        // the first event (Engine.dll.c:20258 and 247376-247381).
        //
        // For AActor it reduces to UObject::InitExecution — creating the state frame,
        // which in this port lives in UEObject itself and exists from load — plus four
        // asserts. Exactly one specialisation does observable work,
        // AParticleFX::InitExecution (75566), and it runs here. Without this pass emitters
        // would initialise lazily on first render, and a run without rendering would start
        // from a different world state than a run with it.
        public void RunInitExecutionPass(UEInterpreter interp)
        {
            int fx = 0;
            foreach (UEObject actor in Actors)
            {
                if (actor is null || actor.Destroyed) continue;
                if (!InitExecutionActor(actor)) continue;
                fx++;
            }
            Log($"phase init_execution particlefx={fx}");
        }

        // Returns true if the class has an OBSERVABLE specialisation.
        public static bool InitExecutionActor(UEObject actor)
        {
            if (!UEInterpreter.ClassIsA(actor.Class, "ParticleFX")) return false;
            ParticleInitTap?.Invoke(actor, true);
            UEWorldFrameProducer.ParticleFxInitExecution(actor);
            ParticleInitTap?.Invoke(actor, false);
            return true;
        }

        // Which map the loaded world came from. A frontend that outlives the
        // level (a Unity runtime is a process-long singleton) must be able to ASK
        // rather than remember, or a scene change silently keeps the old world.
        public string LoadedMapPath => _brushMapPath;

        // The level bound by the LOAD OF THIS world. Written next to the binding itself
        // (UEActorGraphLoader) so "bound" and "owned" cannot diverge; read only by teardown.
        UEObject _boundLevel;

        // And WHICH interpreter holds the binding: teardown must know not only WHAT to
        // release but from WHOM; releasing via a foreign interpreter would waste the token
        // and leave the real owner bound.
        UEInterpreter _boundInterp;

        internal void NoteBoundWorldLevelInfo(UEInterpreter interp, UEObject level)
        {
            _boundInterp = interp;
            _boundLevel = level;
        }

        // For ownership checks: what this world considers itself obliged to release on teardown.
        public UEObject BoundWorldLevelInfo => _boundLevel;

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
        string TakeLevelPackageName(string mapPath)
        {
            string pkg = _pendingLevelPackage ?? System.IO.Path.GetFileNameWithoutExtension(mapPath);
            _pendingLevelPackage = null;
            return pkg;
        }

        public List<UEActorGraphLoader.ActorEntry> LoadWorld(string mapPath, UEInterpreter interp,
            IEnumerable<UEObject> extraActors = null)
        {
            if (interp is null) throw new ArgumentNullException(nameof(interp));
            LevelPackageName = TakeLevelPackageName(mapPath);
            try
            {
                // INSIDE the try. It was outside, which left the one failure the
                // rollback exists for uncovered: LoadWorldGeometry publishes the
                // BSP and then reads the brush bounds, so a failure between those
                // two left a world with geometry and nothing else, and the catch
                // below never ran.
                LoadWorldGeometry(mapPath);      // refuses over a live hash
                List<UEActorGraphLoader.ActorEntry> entries = UEActorGraphLoader.Load(mapPath, interp, this);

                // The actor list and the hash are part of the SAME transaction.
                // Leaving them to the caller is how a "loaded" world ended up
                // with geometry from one step and no hash from another — each
                // call looked fine on its own. InitCollisionHash is itself
                // atomic, so a mesh or brush dependency missing here unwinds the
                // whole world rather than publishing a partial one.
                var actors = new List<UEObject>(entries.Count);
                foreach (UEActorGraphLoader.ActorEntry e in entries) actors.Add(e.Runtime);
                if (extraActors is not null)
                    foreach (UEObject extra in extraActors)
                        if (extra is not null && !actors.Contains(extra)) actors.Add(extra);
                SetActors(actors);
                InitCollisionHash();
                // UGameEngine::LoadMap: an empty LevelEnterText of the new LevelInfo0 =
                // URL.Map (UELevelRuntime.LoadMapScreens). Transition URL — captured by
                // TryTakeLevelTravel; without it (first map, Entry) — the file name.
                ApplyLoadMapLevelEnterText(_capturedUrl?.Map ?? System.IO.Path.GetFileName(mapPath));
                return entries;
            }
            catch
            {
                // Do not leave a half-world: geometry from a map whose actors or
                // hash failed is exactly what this method exists to prevent.
                ResetWorld(interp);
                throw;
            }
        }

        // Tear the world down so another map can be loaded. Everything keyed to
        // the level goes: geometry, the collision hash AND its identity maps
        // (handles are only meaningful against the actors that minted them), the
        // per-export brush/mesh caches, the actor list and the level clock.
        //
        // Without this a scene change kept the previous map's actors, BSP and
        // hash — and silently, because every individual structure still looked
        // populated and valid.
        public void ResetWorld(UEInterpreter interp = null)
        {
            // Whom this world BOUND — from the record made at binding time. Ownership is a
            // fact of the transaction, not a property of the list: Actors[0] is absent for an
            // empty world and for a world whose load failed after binding but before
            // SetActors.
            //
            // THE TOKEN IS NOT CONSUMED WITHOUT SOMEONE TO RETURN IT TO: teardown without
            // interp would zero _boundLevel without releasing the same object in the
            // interpreter, leaving the binding with no way to release it. A world that bound
            // nothing is torn down freely.
            if (_boundLevel is not null && interp is null)
                throw new ArgumentNullException(nameof(interp),
                    "this world bound LevelInfo to the interpreter — teardown without "
                    + "the interpreter would lose ownership without removing the binding");
            // And NOT by a foreign interpreter: with a different non-null interpreter the
            // release would go where this level is not, and the real owner stays bound.
            // Checked BEFORE consuming the token.
            if (_boundLevel is not null && !ReferenceEquals(interp, _boundInterp))
                throw new ArgumentException(
                    "teardown by a foreign interpreter: this world bound LevelInfo to "
                    + "ANOTHER one — the release would go to a non-owner, and the binding "
                    + "would remain forever", nameof(interp));
            UEObject ownedLevel = _boundLevel;
            UEInterpreter ownedInterp = _boundInterp;
            // Whether THIS teardown owns INTERPRETER state. Everything living in the
            // interpreter rather than this object is cleared only under this flag: a world
            // that bound nothing must not touch someone else's state.
            bool ownsInterpState = ownedLevel is not null
                && interp is not null && ReferenceEquals(interp, ownedInterp);
            _boundLevel = null;
            _boundInterp = null;
            Actors.Clear();
            ActorsByExport.Clear();
            ActorsById.Clear();
            // Name occupancy is a property of the map PACKAGE and goes with it. Class
            // counters (ClassUnique) do not: that is a UClass field and the class survives a
            // map change (live original 1.53: GameInfo1 on every map).
            _occupiedObjectNames.Clear();
            LevelPackageName = null;   // the package goes with the level
            // SpawnLog holds every actor ever spawned, so after a teardown it is
            // a list of the PREVIOUS world's destroyed actors — and it is walked
            // for iteration order and by the placed-actor registry, so leaving it
            // is not merely a leak.
            //
            // But it is INTERPRETER state, not this world's: tearing down an unrelated empty
            // world with a foreign interpreter must not wipe the ACTIVE world's SpawnLog.
            if (ownsInterpState) interp.SpawnLog.Clear();
            // Frame state is LEVEL state too: particles, ambient, audio/HUD queues and the
            // frame cache are keyed on the torn-down actors. It is not left to the frontend
            // for the same reason the collision hash is not.
            FrameProducer?.ResetForNewLevel();
            Bsp = null;
            // Static geometry is LEVEL state too: an artefact of the previous map surviving
            // teardown would make the frontend build the previous level's mesh unknowingly.
            PublishWorldStatic(null);
            CollisionHash = null;
            _hashHandle.Clear();
            _hashActor.Clear();
            _hashColLocation.Clear();
            _nextHashHandle = 0;
            BrushBoundsByExport = new Dictionary<int, BrushBounds>();
            _brushMapPath = null;
            _brushModels.Clear();
            // Tracker brush polygons are keyed by the same map-package export index: the next map's mover with an equal
            // index would otherwise take this map's polygons (Lev_Tut1b -> Lev2_HogFront Mover17/47/49/64).
            _trackedBrushModels.Clear();
            _meshBounds = null;
            // ── THE NAV TABLE AND TEXTURE STATE ARE LEVEL STATE TOO ──────────
            //
            // The load reads ReachSpecs and the texture source in its FIRST step
            // (LoadWorldGeometry -> SetReachSpecs, SetBrushSource -> TextureFacts.SetSource);
            // a failure in any later step calls teardown, which must clear them, otherwise
            // the nav table and texture packages of the FAILED map would survive the rollback.
            // In the window between rollback and the next map the world must be empty.
            //
            // ReachSpecs is assigned to the field, not via SetReachSpecs: that one refuses
            // null, rightly — "no table" and "empty table" differ, and after teardown the
            // second is correct.
            ReachSpecs = System.Array.Empty<LevelReachSpecRef>();
            TextureFacts.Reset();
            _traceTextureObjects.Clear();
            // Channel actors are keyed by owner objects that this reset just
            // dropped — keeping the map would leak the previous world's actors
            // into the next one and let the dedup scan read destroyed channels.
            _animChannels.Clear();
            // The skeleton cache (pose, tween, root motion) is keyed on actors of the torn-down
            // world — same reason as the channels on the line above.
            _skelCache.Clear();
            _clockLevel = null;
            _clockFixed = 0;
            LevelTicked = false;   // a new ULevel: +0x10c is 0 (not serialized)
            // The level is released TOGETHER with the world: otherwise the next map would take
            // the previous LevelInfo as bootstrap and pull Game from it.
            interp?.ReleaseWorldLevelInfo(ownedLevel);
        }

        public void LoadWorldGeometry(string mapPath)
        {
            if (string.IsNullOrEmpty(mapPath))
                throw new ArgumentException("map path required", nameof(mapPath));
            // A second load over a live hash would give a NEW world model with
            // the OLD actor hash and identity maps still attached — cells keyed
            // to geometry that no longer exists, handles pointing at actors from
            // the previous level. Headless is one level per process, but a Unity
            // runtime is a process-long singleton that loads levels repeatedly,
            // so this is reachable there and must not be silent (§8).
            if (CollisionHash is not null)
                throw new InvalidOperationException(
                    "LoadWorldGeometry after InitCollisionHash: the existing collision "
                    + "hash and its handle maps belong to the previous world. Tear the "
                    + "world down before loading another one — reloading geometry "
                    + "underneath a live hash is not a supported transition.");
            Bsp = HeadlessBspModel.Load(mapPath);
            SetBrushSource(mapPath, HeadlessBspModel.LoadModelBounds(mapPath));
            // ── THE NAV TABLE TRAVELS WITH THE GEOMETRY ──────────────────────
            //
            // Without it Core's FindPath (native 553) sees an empty list on the first
            // NavigationPoint.Paths[0] and throws
            //     FindPath actor HPath_A1 Paths[0]=191 outside ReachSpecs[0]
            // and with the fatal tick-error policy that stops the world.
            //
            // WHY HERE, NOT IN THE CALLER: this is ONE transaction "load the world from this
            // map": BSP, brush bounds and the navigation graph are read from ONE .unr by one
            // owner. Leaving the nav table to the caller means a frontend that "almost" loaded
            // the world. The granular half (SetReachSpecs) remains: headless and fixtures that
            // substitute the graph for one case use it.
            SetReachSpecs(HeadlessBspModel.LoadReachSpecs(mapPath));
            PublishWorldStatic(mapPath);
        }

        // The granular half, for a frontend that already holds the parsed level
        // model and only needs the brush side wired.
        public void SetBrushSource(string mapPath, Dictionary<int, BrushBounds> bounds)
        {
            _brushMapPath = mapPath ?? throw new ArgumentNullException(nameof(mapPath));
            BrushBoundsByExport = bounds ?? throw new ArgumentNullException(nameof(bounds));
            _brushModels.Clear();
            // The TEXTURE data source for native 285 travels with the map. HERE, not in
            // LoadWorldGeometry, because frontends enter the world through DIFFERENT doors
            // (LoadWorldGeometry vs assembling the world piecewise) and both pass through this one.
            // Install root = parent of the maps directory: packages are searched along the
            // game's own [Core.System] Paths list. A miss is not silent: Package() names the
            // root and the package.
            TextureFacts.SetSource(
                Path.GetFullPath(Path.Combine(Path.GetDirectoryName(mapPath) ?? ".", "..")),
                mapPath);
            // Texture objects belong to the LOADED world: they must not survive a map change,
            // just like its collision hash or actor list.
            _traceTextureObjects.Clear();
        }

        UEMeshBounds _meshBounds;

        // ---- AActor::CreateAnimChannel (native 265) --------------------------
        //
        // Auxiliary animation channels: a channel is a REAL spawned actor rooted
        // at a bone, so the whole thing is world state (spawn order, encroach,
        // hash residency, destroy path) and belongs here, not in a frontend.
        // It lived headless-only, which is why Harry's PostBeginPlay hit an
        // unknown native under Unity and his intro animation never played.
        public sealed class AnimChannelEntry
        {
            public UEObject Object;
            public int BoneIndex;
            public bool AutoDestroy;
        }

        readonly Dictionary<UEObject, List<AnimChannelEntry>> _animChannels =
            new Dictionary<UEObject, List<AnimChannelEntry>>();

        // AActor.AuxAnims (+0x6c) is a real actor property; the frame reads it from here.
        // Updated on every change of the owner's channel list.
        void SyncAuxAnims(UEObject owner)
        {
            if (owner is null) return;
            var list = new List<object>();
            if (_animChannels.TryGetValue(owner, out var channels))
                foreach (AnimChannelEntry e in channels) list.Add(e.Object);
            owner.Props["AuxAnims"] = list;
        }

        // Save load. In shipped the channel list IS the AuxAnims array (+0x6c/+0x70): CreateAnimChannel's dedup
        // and AT_Replace, PlayAnim's channel pass and ApplyAnim's overlay all walk it and read each channel's own
        // AnimBone (byte +0x64) and bAnimTransient (+0x28 bit 8) (Engine.dll.c:27782-27787, 27823-27824,
        // 73690-73691, 142947-142975). AuxAnims is not CPF_Transient (flags 0x00400000), so the save package
        // carries it and a load restores the channels with it; CreateAnimChannel (PostBeginPlay) does not run.
        // The port's side registry is rebuilt here from the loaded property, in array order.
        void RebuildAnimChannelsFromAuxAnims()
        {
            _animChannels.Clear();
            foreach (UEObject a in Actors)
            {
                if (a is null || a.Destroyed || a.GetProp(PK.AuxAnims) is not System.Collections.IList aux || aux.Count == 0)
                    continue;
                var channels = new List<AnimChannelEntry>(aux.Count);
                for (int i = 0; i < aux.Count; i++)
                {
                    if (aux[i] is not UEObject ch || ch.IsStruct || ch.Destroyed)
                        throw new InvalidDataException(
                            $"load: {a.GetProp(PK.Name)}.AuxAnims[{i}] is {(aux[i] is UEObject d && d.Destroyed ? "a destroyed actor" : aux[i]?.GetType().Name ?? "None")}"
                            + " — shipped ApplyAnim dereferences every entry; the outcome is not established (§4)");
                    channels.Add(new AnimChannelEntry
                    {
                        Object = ch,
                        BoneIndex = UEInterpreter.ToInt(ch.GetProp(PK.AnimBone)),
                        AutoDestroy = UEInterpreter.ToBool(ch.GetProp(PK.bAnimTransient)),
                    });
                }
                _animChannels[a] = channels;
            }
        }

        // Save/load journal: every owner's channels in Actors order — registry entry (channel name, bone, auto-destroy,
        // AnimSequence) and whether the registry equals the owner's AuxAnims array element for element.
        public string AnimChannelDigest()
        {
            var sb = new System.Text.StringBuilder();
            int owners = 0;
            foreach (UEObject a in Actors)
            {
                if (a is null || a.Destroyed || !_animChannels.TryGetValue(a, out var channels) || channels.Count == 0) continue;
                var aux = a.GetProp(PK.AuxAnims) as System.Collections.IList;
                bool same = aux is not null && aux.Count == channels.Count;
                for (int i = 0; same && i < channels.Count; i++) same = ReferenceEquals(aux[i], channels[i].Object);
                sb.Append(' ').Append(a.GetProp(PK.Name)).Append(same ? "=aux[" : "!=aux[");
                for (int i = 0; i < channels.Count; i++)
                    sb.Append(i == 0 ? "" : ",").Append(channels[i].Object.GetProp(PK.Name)).Append(':').Append(channels[i].BoneIndex)
                      .Append(':').Append(channels[i].AutoDestroy ? 1 : 0).Append(':').Append(channels[i].Object.GetProp(PK.AnimSequence));
                sb.Append(']');
                owners++;
            }
            return $"owners={owners}{sb}";
        }

        public IReadOnlyList<AnimChannelEntry> AnimChannelsOf(UEObject owner)
            => owner is not null && _animChannels.TryGetValue(owner, out var c)
                ? (IReadOnlyList<AnimChannelEntry>)c : System.Array.Empty<AnimChannelEntry>();

        // AT_Replace's pass over the auxiliary channels (Engine.dll.c:73688-73711).
        // It does NOT destroy them all — that was a headless-era simplification
        // this port inherited. The shipped loop branches on the channel's own
        // auto-destroy bit (+0x28 bit 8):
        //   bit clear -> the channel SURVIVES; shipped only zeroes its +0x50
        //   bit set   -> ULevel::DestroyActor (vtable +0x98) and remove from the array
        public void DestroyAnimChannels(UEInterpreter interp, UEObject owner)
        {
            if (owner is null || !_animChannels.TryGetValue(owner, out var channels)) return;
            for (int i = channels.Count - 1; i >= 0; i--)
            {
                if (!channels[i].AutoDestroy)
                {
                    // The surviving branch clears the channel's own animation.
                    // actor+0x50 IS identifiable, from this very function: 73720
                    // compares it against the requested sequence name and 73774
                    // assigns the chosen one to it — so it is the actor's current
                    // AnimSequence, which this port already models by that name
                    // (SetAnimation clears it the same way, with 'None').
                    // I had left this as a loud STOP; the offset was provable
                    // after all, and an unimplemented side effect is a gap even
                    // when it is honestly logged.
                    channels[i].Object.Props["AnimSequence"] = new UEName("None");
                    continue;
                }
                DestroyActor(interp, channels[i].Object);
                channels.RemoveAt(i);
            }
            SyncAuxAnims(owner);
        }

        public UEObject CreateAnimChannel(UEInterpreter interp, UEObject owner, UClass cls,
            int animType, string boneName, bool autoDestroy)
        {
            // Shipped order (Engine.dll.c:27768-27813): mesh and bone, look up an existing channel and ONLY
            // then SpawnActor(class) — a None class is not checked before the lookup (SpawnActor returns NULL);
            // the type branches only on `== 1` (Combine), anything else is Replace.
            if (owner is null) return null;
            if (cls is not null && HP1.Core.UEClassRegistry.IsImportStub(cls))
            {
                UClass canonical = HP1.Core.UEClassRegistry.Shared.FindForStub(cls);
                if (canonical is not null && !HP1.Core.UEClassRegistry.IsImportStub(canonical))
                    cls = canonical;
            }
            if (string.IsNullOrEmpty(boneName)
                || boneName.Equals("None", StringComparison.OrdinalIgnoreCase)) return null;

            if (owner.GetProp(PK.Mesh) is not UEResourceRef meshRef
                || !meshRef.Class.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase)) return null;
            (int boneIndex, int numChildren) = BoneLookupOf(meshRef, owner, boneName);
            if (boneIndex < 0) return null;

            if (!_animChannels.TryGetValue(owner, out var channels))
                _animChannels[owner] = channels = new List<AnimChannelEntry>();
            // Dedup like the shipped array walk (27782-27787): compare the CHANNEL
            // ACTORS' own AnimBone (+0x64) and bAnimTransient (+0x28 bit 0x100).
            // The hit RETURNS THE EXISTING CHANNEL — disassembly 0x10408727
            // `mov eax,[ebp-0x24]`; Ghidra shows only the shared epilogue call
            // and the port used to return None here (V11).
            foreach (AnimChannelEntry e in channels)
                if (UEInterpreter.ToInt(e.Object.GetProp(PK.AnimBone)) == boneIndex
                    && UEInterpreter.ToBool(e.Object.GetProp(PK.bAnimTransient)) == autoDestroy)
                    return e.Object;

            // Shipped spawns through ULevel::SpawnActor at the OWNER's
            // location/rotation with Owner=this (27790-27802) — the full spawn
            // order applies — and only AFTER the spawn returns copies the owner's
            // Mesh and the bone/auto-destroy fields onto the channel (27804-27808).
            // Instigator = NULL there (27798), Tag = the class name set inside
            // SpawnActor.
            if (cls is null) return null;   // ULevel::SpawnActor(NULL class) -> NULL
            UEObject channel = SpawnActor(interp, owner, cls, owner, null,
                UEInterpreter.AsVector3(owner.GetProp(PK.Location)),
                UEInterpreter.AsRotator(owner.GetProp(PK.Rotation)));
            if (channel is null) return null;
            // After the spawn shipped copies onto the channel (27804-27808; names
            // by the property-offset oracle): Mesh (+0x164), SkelAnim (+0x68),
            // AnimBone (+0x64, the bone index as a byte) and bAnimTransient
            // (+0x28 bit 0x100) from the 5th argument.
            channel.Props["Mesh"] = owner.GetProp(PK.Mesh);
            channel.Props["SkelAnim"] = owner.GetProp(PK.SkelAnim);
            channel.Props["AnimBone"] = (byte)boneIndex;
            channel.Props["bAnimTransient"] = autoDestroy;

            // AT_Combine (1): the channel goes to the HEAD of AuxAnims and
            // nothing is removed (27809-27812). AT_Replace (0): channels whose
            // root lies inside the new root's contiguous RefSkeleton subtree are
            // destroyed through ULevel::DestroyActor (vtable +0x98), then the
            // new one is appended (27813-27852).
            if (animType == 1)
            {
                channels.Insert(0, new AnimChannelEntry
                    { Object = channel, BoneIndex = boneIndex, AutoDestroy = autoDestroy });
                SyncAuxAnims(owner);
                Journal?.Invoke($"anim_channel class={cls.Name} owner={owner.GetProp(PK.Name)} "
                    + $"mesh={meshRef.Name} bone={boneName}:{boneIndex} "
                    + $"autodestroy={autoDestroy.ToString().ToLowerInvariant()}");
                return channel;
            }
            int subtreeEnd = checked(boneIndex + numChildren);
            for (int i = channels.Count - 1; i >= 0; i--)
            {
                int existingRoot = channels[i].BoneIndex;
                if (existingRoot >= boneIndex && existingRoot < subtreeEnd)
                {
                    DestroyActor(interp, channels[i].Object);
                    channels.RemoveAt(i);
                }
            }
            channels.Add(new AnimChannelEntry
            {
                Object = channel, BoneIndex = boneIndex, AutoDestroy = autoDestroy
            });
            SyncAuxAnims(owner);
            Journal?.Invoke($"anim_channel class={cls.Name} owner={owner.GetProp(PK.Name)} "
                + $"mesh={meshRef.Name} bone={boneName}:{boneIndex} "
                + $"autodestroy={autoDestroy.ToString().ToLowerInvariant()}");
            return channel;
        }

        // AActor::PlayAnim(None, …, RootBone) (Engine.dll.c:73637-73672): every
        // channel rooted inside the bone's subtree is stopped — a persistent one
        // keeps living with AnimSequence=None, a transient one is destroyed.
        // Returns false when the bone does not exist (shipped returns 0).
        public bool StopAnimChannelsUnder(UEInterpreter interp, UEObject owner, string boneName)
        {
            if (owner?.GetProp(PK.Mesh) is not UEResourceRef meshRef) return false;
            (int boneIndex, int numChildren) = BoneLookupOf(meshRef, owner, boneName);
            if (boneIndex < 0) return false;
            if (!_animChannels.TryGetValue(owner, out var channels)) return true;
            for (int i = channels.Count - 1; i >= 0; i--)
            {
                int root = channels[i].BoneIndex;
                if (root < boneIndex || boneIndex + numChildren <= root) continue;
                if (!channels[i].AutoDestroy)
                {
                    channels[i].Object.Props["AnimSequence"] = new UEName("None");
                    continue;
                }
                DestroyActor(interp, channels[i].Object);
                channels.RemoveAt(i);
            }
            SyncAuxAnims(owner);
            return true;
        }

        // Opens the shipped package set on first use. Both mesh readers need it,
        // and both need the same "the world has to be loaded first" diagnostic.
        void EnsureMeshBounds(UEResourceRef meshRef, UEObject actor, string what)
        {
            if (_meshBounds is not null) return;
            if (_brushMapPath is null)
                throw new InvalidOperationException(
                    $"{what} before the world was loaded "
                    + $"actor={actor?.GetProp(PK.Name)} mesh={meshRef.Name} — call "
                    + "LoadWorldGeometry (or SetBrushSource) first, or set the matching hook");
            // <install>/Maps/<level>.unr -> <install>/System, the layout the
            // shipped game ships in and both frontends already assume.
            string maps = Path.GetDirectoryName(_brushMapPath);
            string system = Path.Combine(Path.GetDirectoryName(maps) ?? "", "System");
            _meshBounds = new UEMeshBounds(system);
        }

        // Bone index + subtree span for a skeletal mesh actor. Resolved from the
        // shipped packages by default, exactly like MeshHashFieldsOf, so neither
        // frontend has to grow its own RefSkeleton reader — Unity has bone NAMES
        // baked but no subtree span, and reconstructing that span would be a
        // guess about serialized data we can simply read.
        (int Index, int NumChildren) BoneLookupOf(UEResourceRef meshRef, UEObject actor, string boneName)
        {
            if (BoneLookupHook is not null) return BoneLookupHook(meshRef, boneName);
            UERefBone[] bones = RefSkeletonOf(meshRef, actor);
            for (int i = 0; i < bones.Length; i++)
                if (bones[i].Name.Equals(boneName, StringComparison.OrdinalIgnoreCase))
                    return (i, bones[i].NumChildren);
            return (-1, 0);
        }

        UERefBone[] RefSkeletonOf(UEResourceRef meshRef, UEObject actor)
        {
            EnsureMeshBounds(meshRef, actor, "skeletal RefSkeleton");
            return _meshBounds.GetRefSkeleton(meshRef.Name, meshRef.ObjectPath);
        }

        // The collision-hash extent fields for a CollideType=3 mesh actor.
        //
        // Core resolves these itself, from the shipped .u packages next to the
        // map it loaded. MeshHashFieldsHook remains an OVERRIDE for a frontend
        // that already has the mesh loaded and would rather not re-read it.
        // Local box of the mesh primitive — GetCollisionBoundingBox(Owner, 0) of its
        // slot +0x60 — for the UBox::LineCheck/PointCheck bodies.
        public (Vector3 Lo, Vector3 Hi) MeshPrimitiveLocalBox(UEObject actor)
        {
            var meshRef = (UEResourceRef)actor.GetProp(PK.Mesh);
            Hp1MeshHashFields m = MeshHashFieldsOf(meshRef, actor);
            EnsureMeshBounds(meshRef, actor, "UBox body: CollideType=3 mesh actor");
            bool skeletal = _meshBounds.IsSkeletal(meshRef.Name, meshRef.ObjectPath);
            object ds = actor.GetProp(PK.DrawScale);
            return CylinderExtent.MeshCollisionLocalBox(m.BoundMin, m.BoundMax, m.Scale, m.Origin,
                ds is null ? 1f : UEInterpreter.AsFloat(ds), skeletal);
        }

        public Hp1MeshHashFields MeshHashFieldsOf(UEResourceRef meshRef, UEObject actor)
        {
            if (MeshHashFieldsHook is not null) return MeshHashFieldsHook(meshRef);
            EnsureMeshBounds(meshRef, actor, "collision hash: CollideType=3 mesh actor");
            return _meshBounds.GetHashFields(meshRef.Name, meshRef.ObjectPath);
        }

        // AActor::SetCollision (Engine.dll.c:30526): remove from the collision
        // hash under the OLD bCollideActors, write the three flags, re-add under
        // the NEW one. Each optional argument defaults to the current value
        // (NActor.cpp:554-560).
        //
        // Core-owned because the ORDER is the contract: the removal has to see
        // the old flag or the actor is never unlinked, and the re-add has to see
        // the new one. A frontend that writes the flags and updates its own
        // colliders — which is what Unity did — leaves Core's hash holding
        // membership that no longer matches the actor.
        public void SetCollision(UEObject self, bool? collideActors,
            bool? blockActors, bool? blockPlayers)
        {
            if (self is null) return;
            HashRemoveActor(self);
            self.Props["bCollideActors"] = collideActors ?? UEInterpreter.ToBool(self.GetProp(PK.bCollideActors));
            self.Props["bBlockActors"] = blockActors ?? UEInterpreter.ToBool(self.GetProp(PK.bBlockActors));
            self.Props["bBlockPlayers"] = blockPlayers ?? UEInterpreter.ToBool(self.GetProp(PK.bBlockPlayers));
            HashAddActor(self);
        }

        // THE THIRD ARGUMENT WRITES CollisionWidth — PER THE DISASM, NOT THE PSEUDOCODE.
        //
        // Ghidra's pseudocode (Engine.dll.c:61914-61935) loses the write: it renders
        // `*(undefined4 **)(this + 0x1d0) = &uStack_4` as an assignment of a local's
        // ADDRESS. The machine code of `AActor::execSetCollisionSize` (0x1040a870, VA from
        // PE export ordinal 2339):
        //
        //   0x1040a8c3  mov ecx,[edi+0x1d0]   3rd parameter buffer := current
        //   0x1040a8ce  mov [esp+0x14],ecx    CollisionWidth (optional default)
        //   0x1040a8e6  call GNatives[b]      optional evaluated INTO this buffer
        //   0x1040a8ec  mov ecx,[esp+0x14]
        //   0x1040a8fc  mov [edi+0x1d0],ecx   this->CollisionWidth = NewWidth
        //   0x1040a906  call 0x103041a1       and ONLY THEN the body (R,H)
        //   0x1040a911  mov [ecx],1           Result = true unconditionally
        //
        // Declaration from the package (`--dumpfunc Actor SetCollisionSize`):
        //   native(283) final function bool SetCollisionSize(
        //       float NewRadius, float NewHeight, optional float NewWidth);
        // Offset 0x1d0 = Actor.CollisionWidth (audit/prop_offsets.py --find).
        //
        // ORDER IS PART OF THE CONTRACT. The width write comes BEFORE the body, i.e. BEFORE
        // the actor is removed from the hash, and HashRemoveActor computes the box from the
        // CURRENT properties. For a plain cylinder (CollideType 0) CollisionWidth is the
        // Z offset of the hash box centre (see HashBoxOf), so moving the write after the
        // removal changes WHICH cells the actor is removed from and leaves dangling
        // references — exactly as in shipped.
        //
        // Live in the campaign: only Harry.uc:1482/1490 pass three arguments (state Mounting
        // BeginState/EndState — the cylinder is halved and the box centre rises by half the
        // height, so the bottom stays put), and both calls belong to the PLAYER.
        //
        // The AActor::SetCollisionSize body (0x10379c80) itself does no fit probe: under the
        // OLD cylinder it removes a bCollideActors actor from FCollisionHash, writes
        // CollisionRadius/CollisionHeight and re-adds it with the new extent.
        public void SetCollisionSize(UEObject self, float radius, float height,
            float? width = null)
        {
            if (self is null) return;
            // The optional default is the CURRENT property value, because the argument
            // buffer is seeded with it (0x1040a8c3). A write without the argument writes
            // the same number, as in shipped.
            self.Props["CollisionWidth"] =
                width ?? UEInterpreter.AsFloat(self.GetProp(PK.CollisionWidth));
            HashRemoveActor(self);
            self.Props["CollisionRadius"] = radius;
            self.Props["CollisionHeight"] = height;
            HashAddActor(self);
        }

        // A brush actor's own UModel (its serialized BSP), parsed once per Model
        // export. Core-owned: it needs nothing but the map path it was loaded
        // from, so making each frontend supply it was duplicated work.
        // BrushModelHook remains an override for a frontend that resolves brush
        // geometry some other way.
        public HeadlessBspModel BrushModelOf(UEObject brushActor)
        {
            if (BrushModelHook is not null) return BrushModelHook(brushActor);
            if (brushActor.GetProp(PK.Brush) is not UEModelRef brush)
                throw new NotSupportedException(
                    $"brush model missing actor={brushActor.GetProp(PK.Name)} "
                    + $"class={brushActor.Class?.Name}");
            if (_brushMapPath is null)
                throw new InvalidOperationException(
                    "brush model requested before LoadWorldGeometry — the frontend "
                    + "must load the world geometry before any brush trace");
            if (!_brushModels.TryGetValue(brush.ExportIndex, out HeadlessBspModel model))
                _brushModels[brush.ExportIndex] = model =
                    HeadlessBspModel.LoadModelExport(_brushMapPath, brush.ExportIndex);
            return model;
        }

        // Candidate handle → actor (the encroachment walk resolves the
        // replica's handles back to actors through the owner of the identity).
        // A handle the replica just returned MUST resolve: an unknown one is a
        // maintenance defect, so this throws rather than skipping silently.
        public UEObject HashActor(int handle)
        {
            if (!_hashActor.TryGetValue(handle, out UEObject actor))
                throw new KeyNotFoundException(
                    $"collision hash: handle {handle} has no actor (a hash maintenance point is missing)");
            return actor;
        }

        public int HashLinkCount(UEObject actor)
            => actor is not null && CollisionHash is not null
                && _hashHandle.TryGetValue(actor, out int handle)
                ? CollisionHash.LinkCount(handle) : 0;

        // ULevel::SetActorCollision(1) (Engine.dll.c:161885): create the hash
        // and add every non-null bCollideActors actor IN ACTORS-ARRAY ORDER —
        // the base insertion order every later chain position builds on. Runs
        // at the shipped LoadMap moment: after the level actors are
        // registered, before any lifecycle event or spawn.
        // ATOMIC on purpose. HashAddActor throws for an actor whose dependency
        // is missing — a CollideType-3 mesh with no MeshHashFieldsHook, a brush
        // with no entry in BrushBoundsByExport. Publishing CollisionHash first
        // and populating afterwards left a HALF-BUILT hash on that throw, and
        // because the entry guard returns early when the field is non-null, the
        // retry after fixing the dependency reported success without ever
        // finishing the walk. Every later query then ran against a hash missing
        // whatever came after the failure — silently, which is the worst
        // possible shape for a collision structure.
        //
        // So: build into a local, snapshot the identity maps, and publish only
        // once the whole walk survived. On failure everything goes back exactly
        // as it was and the exception propagates, so a retry is a real retry.
        public void InitCollisionHash()
        {
            if (CollisionHash is not null) return;
            // AActor::PostLoad CT3 -> ValidateActor (see ValidateActorCollision).
            // Shipped runs it at package load, i.e. BEFORE ULevel::SetActorCollision(1);
            // InitCollisionHash is the single Core point that ALL world-load paths (map,
            // transition, save) and every frontend pass through at that moment.
            foreach (UEObject actor in Actors)
                if (actor is not null && !actor.Destroyed)
                    ValidateActorCollision(actor, postLoad: true);
            var handleSnapshot = new Dictionary<UEObject, int>(_hashHandle);
            var actorSnapshot = new Dictionary<int, UEObject>(_hashActor);
            var colLocSnapshot = new Dictionary<int, Vector3>(_hashColLocation);
            int handleCounter = _nextHashHandle;
            CollisionHash = new CollisionHashReplica();
            try
            {
                foreach (UEObject actor in Actors)
                    if (actor is not null && !actor.Destroyed
                        && UEInterpreter.ToBool(actor.GetProp(PK.bCollideActors)))
                        HashAddActor(actor);
            }
            catch
            {
                CollisionHash = null;
                _hashHandle.Clear();
                foreach (KeyValuePair<UEObject, int> e in handleSnapshot) _hashHandle[e.Key] = e.Value;
                _hashActor.Clear();
                foreach (KeyValuePair<int, UEObject> e in actorSnapshot) _hashActor[e.Key] = e.Value;
                _hashColLocation.Clear();
                foreach (KeyValuePair<int, Vector3> e in colLocSnapshot) _hashColLocation[e.Key] = e.Value;
                _nextHashHandle = handleCounter;
                throw;
            }
        }

        // GetActorExtent's box: actor->GetPrimitive() (vtable +0xa4, dispatch
        // on CollideType +0x1d8, Engine.dll.c:113085) ->GetCollisionBoundingBox
        // (vtable +0x60), per shipped override:
        //   CT 3 + Mesh  -> UMesh (33473): scaled mesh bound through the mesh
        //                   RotOrigin/actor Rotation coords.
        //   CT 3 + Brush -> UModel/"UBoxPrim" (14613): the serialized model FBox
        //                   corners through ABrush::ToWorld (validate_brush L1).
        //   CT 1         -> UOrientedCylinder (body 0x103fb190): local box
        //                   ±(R, R, H) through the base AActor::ToWorld coords
        //                   (full-rotation, no PrePivot; body 0x1031be20).
        //   CT 2         -> UBox (body 0x103fe130): local box ±(R, W, H), the Y
        //                   half-extent = CollisionWidth (+0x1d0) or R when zero,
        //                   through the same base ToWorld coords.
        //   else (CT 0)  -> UPrimitive (92136): the axis-aligned cylinder AABB;
        //                   +0x1d0 (CollisionWidth) acts as the Z-center offset.
        public (Vector3 Min, Vector3 Max) HashBoxOf(UEObject actor)
        {
            Vector3 loc = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
            float r = UEInterpreter.AsFloat(actor.GetProp(PK.CollisionRadius));
            float h = UEInterpreter.AsFloat(actor.GetProp(PK.CollisionHeight));
            float w = UEInterpreter.AsFloat(actor.GetProp(PK.CollisionWidth));
            UERotator rot = UEInterpreter.AsRotator(actor.GetProp(PK.Rotation));
            switch (UEPrimitive.Resolve(actor))
            {
                case UEPrimitiveKind.Mesh when actor.GetProp(PK.Mesh) is UEResourceRef meshRef:
                    return MeshPrimitiveBox(actor, meshRef);
                case UEPrimitiveKind.Brush:
                    return BrushPrimitiveBox(actor);
                case UEPrimitiveKind.OrientedCylinder:
                    return HashBoxes.OrientedLocalBox(r, r, h, loc, rot.Pitch, rot.Yaw, rot.Roll);
                case UEPrimitiveKind.Box:
                    return HashBoxes.OrientedLocalBox(r, w != 0f ? w : r, h, loc,
                        rot.Pitch, rot.Yaw, rot.Roll);
                default:
                    return HashBoxes.Cylinder(loc, r, h, w);
            }
        }

        // AActor::GetCylinderExtent (body 0x1037a900; analysis — CylinderExtent in
        // UEBspCollision.cs). Every shipped site that calls it takes the extent FROM HERE
        // rather than building (R, R, H) itself: for CollideType 2/3 these differ.
        public Vector3 GetCylinderExtent(UEObject actor)
            => CylinderExtentOf(actor.GetProp, actor, actor.GetProp(PK.Name));

        // The same answer for a class TEMPLATE (ULevel::SpawnActor calls
        // GetCylinderExtent(param_12) — the class default object, 0x103a67d8) —
        // without creating an instance: a spawn refusal must not consume an ObjectId.
        public Vector3 GetCylinderExtentOfTemplate(UClass cls)
            => CylinderExtentOf(n => n == "Mesh" || n == "Brush"
                    ? UEObject.ClassDefaultResource(cls, n)
                    : UEObject.ClassDefaultScalar(cls, n), null, $"default {cls?.Name}");

        Vector3 CylinderExtentOf(Func<string, object> get, UEObject actor, object who)
        {
            float r = UEInterpreter.AsFloat(get("CollisionRadius"));
            float h = UEInterpreter.AsFloat(get("CollisionHeight"));
            int collideType = UEInterpreter.ToInt(get("CollideType"));
            if (collideType == 3)
            {
                if (get("Mesh") is UEResourceRef meshRef)
                {
                    // UMesh::GetCollisionExtent 0x103b8820.
                    Hp1MeshHashFields m = MeshHashFieldsOf(meshRef, actor);
                    (Vector3 min, Vector3 max) = CylinderExtent.MeshLocalBox(m.BoundMin, m.BoundMax,
                        m.Scale, m.Origin, UEInterpreter.AsFloat(get("DrawScale")));
                    return CylinderExtent.FromLocalBox(min, max);
                }
                object brush = get("Brush");
                if (brush is not null)
                {
                    // "UBoxPrim"(UModel)::GetCollisionExtent 0x103fea90: the model's raw
                    // BoundingBox. A brush whose box the port does not know (not from the map)
                    // is a loud refusal, not a cylinder.
                    if (brush is not UEModelRef model
                        || !BrushBoundsByExport.TryGetValue(model.ExportIndex, out BrushBounds b))
                        throw new NotSupportedException(
                            $"GetCylinderExtent: CollideType 3 brush without model bounds actor={who}");
                    return CylinderExtent.FromLocalBox(b.BoxMin, b.BoxMax);
                }
            }
            if (collideType == 2)
            {
                float w = UEInterpreter.AsFloat(get("CollisionWidth"));
                return new Vector3(r, w != 0f ? w : r, h);
            }
            return new Vector3(r, r, h);
        }

        // AActor::PostLoad (body 0x10379910; Engine.dll.c:34407-34411) and
        // AActor::Spawned (body 0x10379a30; 96652-96655) with CollideType 3 call
        // GetPrimitive()->ValidateActor(this) (primitive vtable +0x6c). PostLoad
        // additionally requires Brush or Mesh; Spawned does not (for the cylinder
        // UPrimitive::ValidateActor 0x103fa260 is a bare `ret 4`).
        // For UMesh/ULodMesh/USkeletalMesh/UModel slot +0x6c is "UBoxPrim"::
        // ValidateActor 0x103feb50 (Engine.dll.c:128891-128910):
        //     e = this->GetCollisionExtent(Owner)   (+0x64 — the same extent
        //                                            as GetCylinderExtent)
        //     W = R = (e.y <= e.x ? e.x : e.y);  H = e.z.
        // So a CT3 actor with a mesh/brush has its cylinder REWRITTEN from the primitive box
        // at load and at spawn. Campaign data confirms it independently: for 1416 of 1420
        // placed CT3 actors on 41 maps the editor-saved R/W/H equal this formula bit for bit
        // (--extentcensus).
        public void ValidateActorCollision(UEObject actor, bool postLoad)
        {
            if (UEInterpreter.ToInt(actor.GetProp(PK.CollideType)) != 3) return;
            if (postLoad && actor.GetProp(PK.Brush) is null && actor.GetProp(PK.Mesh) is null) return;
            Vector3 e;
            switch (UEPrimitive.Resolve(actor))
            {
                case UEPrimitiveKind.Mesh when actor.GetProp(PK.Mesh) is UEResourceRef meshRef:
                {
                    Hp1MeshHashFields m = MeshHashFieldsOf(meshRef, actor);
                    (Vector3 min, Vector3 max) = CylinderExtent.MeshLocalBox(m.BoundMin, m.BoundMax,
                        m.Scale, m.Origin, UEInterpreter.AsFloat(actor.GetProp(PK.DrawScale)));
                    e = CylinderExtent.FromLocalBox(min, max);
                    break;
                }
                case UEPrimitiveKind.Brush:
                {
                    BrushBounds b = BrushBoundsOf(actor);
                    e = CylinderExtent.FromLocalBox(b.BoxMin, b.BoxMax);
                    break;
                }
                default:
                    return;                          // UPrimitive::ValidateActor — ret 4
            }
            float rw = e.y <= e.x ? e.x : e.y;
            actor.Props["CollisionWidth"] = rw;
            actor.Props["CollisionRadius"] = rw;
            actor.Props["CollisionHeight"] = e.z;
        }

        // Both visual formulas are separate because they have TWO callers: HashBoxOf
        // (primitive by CollideType) and WorldCollisionBox (bVisual takes Mesh/Brush
        // regardless of CollideType). Two copies would drift silently.
        (Vector3 Min, Vector3 Max) MeshPrimitiveBox(UEObject actor, UEResourceRef meshRef)
        {
            Hp1MeshHashFields m = MeshHashFieldsOf(meshRef, actor);
            UERotator rot = UEInterpreter.AsRotator(actor.GetProp(PK.Rotation));
            return HashBoxes.Mesh(m.BoundMin, m.BoundMax, m.Scale, m.Origin,
                m.RotPitch, m.RotYaw, m.RotRoll,
                UEInterpreter.AsFloat(actor.GetProp(PK.DrawScale)),
                UEInterpreter.AsVector3(actor.GetProp(PK.Location)),
                UEInterpreter.AsVector3(actor.GetProp(PK.PrePivot)),
                rot.Pitch, rot.Yaw, rot.Roll);
        }

        (Vector3 Min, Vector3 Max) BrushPrimitiveBox(UEObject actor)
        {
            BrushBounds b = BrushBoundsOf(actor);
            return HashBoxes.TransformedBox(b.BoxMin, b.BoxMax, BrushToWorldCoords(actor));
        }

        // AActor::execGetWorldCollisionBox (Engine.dll.c:134732) — native 286.
        //
        // THE BRANCHING IS SHIPPED'S:
        //     if (bVisual == 0)  primitive = this->vtable[0xa4]()      // :134750
        //     else {             primitive = this[0x164];              // Mesh
        //           if (!primitive) primitive = this[0x168];           // Brush
        //           if (!primitive) primitive = this->vtable[0xa4](); }
        //     result = primitive->vtable[0x60](&ret, this, 1);
        // Offsets 0x164/0x168 checked against the shipped layout (--dumpprops Actor):
        // exactly Actor.Mesh and Actor.Brush. Virtual slot 0xa4 picks the primitive by
        // CollideType, i.e. HashBoxOf; slot 0x60 is GetCollisionBoundingBox, already
        // accounted for inside its formulas.
        //
        // KEY DIFFERENCE FROM HashBoxOf: with bVisual mesh and brush are taken REGARDLESS
        // of CollideType. An actor with CollideType=0 and a mesh gives the mesh box here and
        // a cylinder in HashBoxOf — different questions, not a contradiction.
        //
        // OWNER IS CORE, because the answer must follow from the actor's DATA (the authored
        // mesh box via RotOrigin and Rotation), not from scene state such as a deformed
        // render mesh, which would drift with animation.
        public (Vector3 Min, Vector3 Max) WorldCollisionBox(UEObject actor, bool bVisual)
        {
            if (actor is null) return (default, default);
            if (bVisual)
            {
                if (actor.GetProp(PK.Mesh) is UEResourceRef meshRef)
                    return MeshPrimitiveBox(actor, meshRef);
                if (actor.GetProp(PK.Brush) is UEModelRef)
                    return BrushPrimitiveBox(actor);
            }
            return HashBoxOf(actor);
        }

        // AActor::execTraceTexture (Engine.dll.c:55849) — native 285 IN FULL.
        //
        // DECLARATION FROM THE PACKAGE (`--dumpfunc Actor TraceTexture`):
        //   native(285) final function Texture TraceTexture(
        //       Vector TraceEnd, Vector TraceStart, out int Flags,
        //       optional bool bTraceDecals);
        //
        // BODY RECONSTRUCTED FROM THE DISASM (0x1037aba0), not the pseudocode: Ghidra
        // merges the loop counter and the decal flag into one `iVar17` here, which makes
        // the branching read wrong.
        //   1. Hit = FCheckResult(Time=1, Item=-1);
        //      XLevel->SingleLineCheck(Hit, this, End, Start, TRACE_Level=4,
        //      Extent=(0,0,0))                                   (0x1037acf4)
        //   2. *Flags = 0 BEFORE parsing the hit                  (0x1037ad0e)
        //   3. Surf = Hit.GetSurf(): Item==-1 or Primitive not UModel -> None
        //   4. Tex = Surf->Texture; *Flags = Surf->PolyFlags | Tex->PolyFlags
        //      (0x1037ad2c-0x1037ad3b; `Tex->PolyFlags` is the word at +0x80)
        //   5. bTraceDecals==0 -> return Tex                      (0x1037ad3e)
        //   6. else walk Surf->Decals (4 edges, PlaneDot of one sign) and
        //      replace Tex with the decal actor's texture          (0x1037af06)
        //
        // PORT BOUNDARY (loud). Step 6 is NOT ported, and bTraceDecals=true is a STOP, not
        // "return the surface texture". Core does keep the Surf->Decals list
        // (UELevelRuntime.DecalStore.cs, FDecal records with vertices relative to pBase and
        // Actor) (D-40); the branch is not ported because it is unreachable. Body per disasm
        // 0x1037ad4a-0x1037af06 — for porting if a caller appears:
        //   Surf->Decals.Num == 0 -> Tex; N = Vectors[vNormal];
        //   P = Hit.Location - Points[pBase] (float);
        //   for each record in order: prev = 0f; for k = 0..3:
        //     E = N ^ (V[k] - V[k-1 cyclic]) (float components),
        //     d = PlaneDot(FPlane(E, V[k-1] | E), P) (st0, PC64);
        //     d * prev < 0 -> record skipped; else prev = (float)d;
        //   all 4 edges passed and Record.Actor.Texture (+0x15c) != None ->
        //   Tex = it (the LAST matching one wins). Flags unchanged.
        //   Porting also needs a texture object for the decal resource
        //   (TextureObject is built from SurfMaterialRef).
        // Unreachable in the campaign by MEASUREMENT: all 4 call sites (Harry.PlayFootStep,
        // Harry.PlayLandedSound, baseChar.PlayFootStep, InvisibleHarry.PlayFootStep) pass
        // THREE arguments; nobody passes the optional fourth. UELib decompilation of ALL 31
        // System/*.u packages (6124 functions and states, 0 failures): no other native 285 calls.
        //
        // Also measured: none of these four calls READS the out parameter Flags — the local is
        // declared, passed and never used. So Flags correctness cannot be proven by play; a
        // fixture proves it.
        //
        // OWNER IS CORE ENTIRELY: every frontend calls this body, and texture data is read
        // by the single UETextureFactsReader.
        public object TraceTextureNative(UEInterpreter interp, UEFrame frame, object[] args)
        {
            UEObject self = frame?.Context ?? frame?.Object;
            Vector3 end = args is { Length: > 0 }
                ? UEInterpreter.AsVector3(args[0]) : Vector3.zero;
            Vector3 start = args is { Length: > 1 } && args[1] is not null
                ? UEInterpreter.AsVector3(args[1])
                : UEInterpreter.AsVector3(self?.GetProp(PK.Location));

            if (args is { Length: > 3 } && UEInterpreter.ToBool(args[3]))
                throw new NotSupportedException(
                    "STOP: TraceTexture(bTraceDecals=true) — shipped walks "
                    + "Surf->Decals (Engine.dll 0x1037ad4a-0x1037af06), and this branch "
                    + "is not ported (D-40). Across the campaign none of the 4 calls "
                    + "asks for it; if it was asked for, this is a new route, and "
                    + "answering with the surface texture would be a silent substitution");

            if (Bsp is null)
                throw new InvalidOperationException("TraceTexture before the world BSP is loaded");

            // Shipped zeroes the out parameter BEFORE parsing the hit and leaves it zero on a
            // miss — it does not "leave it untouched".
            if (args is { Length: > 2 }) args[2] = 0;

            BspHit hit = Bsp.TraceWorldLine(start, end);
            if (!hit.Blocked) return null;          // no surface -> None -> stone

            // Hit.GetSurf() = FCheckResult::BspNode(0) (0x103ff1b0 -> 0x103febd0): from the
            // trace node along the coplanar iPlane chain to the polygon that CONTAINS
            // Hit.Location (normal >= 0.99, edge test); if none is found — the last co-directed
            // one. The trace node is only the first in the plane: taking its surface meant
            // hearing the floor under a carpet (live original comparison: 108 of 518 campaign
            // steps). Hit.Location is the LineCheck tail's (BspHit.Location).
            Vector3 hitLoc = hit.Location;
            int surfNode = Bsp.BspNode(hit.Node, hitLoc, hit.Normal, 0f, null);
            if (surfNode < 0) return null;
            SurfMaterialRef material = Bsp.SurfaceMaterial(surfNode);
            UETextureFacts facts = material is null ? null : TextureFacts.Read(material);
            if (args is { Length: > 2 })
                args[2] = Bsp.SurfFlags(surfNode) | (facts?.PolyFlags ?? 0);
            // A surface without a texture: shipped returns Surf->Texture, i.e. None (not an
            // object with FootstepSound=0, which would mean "stone").
            if (material is null) return null;
            return TextureObject(interp, material, facts);
        }

        // Texture object the native hands to script. The cache is not an optimisation:
        // shipped returns THE SAME UTexture for one texture, and a fresh object per step
        // would break reference comparison in script.
        private readonly Dictionary<string, UEObject> _traceTextureObjects =
            new(UEPropNameComparer.Instance);

        // How many texture objects the native has issued. Public for the same reason as
        // RootMotionStateCount: "teardown removed this too" is an assertion, not an intent.
        public int TraceTextureObjectCount => _traceTextureObjects.Count;

        private UEObject TextureObject(UEInterpreter interp, SurfMaterialRef material,
            UETextureFacts facts)
        {
            // The key is the declared identity of the reference, not its printed path:
            // `Path` loses both the class and — for a texture embedded in the map — the export
            // number ("export:Leaf"), so it would hand ONE object to two different textures
            // with the same leaf name.
            string key = material.IdentityKey;
            if (_traceTextureObjects.TryGetValue(key, out UEObject cached)) return cached;
            var tex = new UEObject
            {
                Interp = interp,
                Class = HP1.Core.UEClassRegistry.Shared.Find("Texture"),
            };
            tex.Props["Name"] = new UEName(material.Name);
            tex.Props["FootstepSound"] = facts.FootstepSound;
            _traceTextureObjects[key] = tex;
            return tex;
        }

        // The single texture-data reader for all frontends.
        public UETextureFactsReader TextureFacts { get; } = new UETextureFactsReader();

        // FCollisionHash::AddActor (Engine.dll.c:49171): skip bDeleteMe, link
        // the extent cells, remember ColLocation (+0x120) for the un-rehashed-
        // move warning RemoveActor prints.
        public void HashAddActor(UEObject actor)
        {
            // FCollisionHash::AddActor: the body is under `if (!bDeleteMe)` (disasm
            // 0x10364b9f-0x10364ba5) — MoveActor/FarMove of the carcass do NOT return it to the hash.
            if (CollisionHash is null || actor is null || actor.Destroyed) return;
            if (!UEInterpreter.ToBool(actor.GetProp(PK.bCollideActors))) return;
            if (!_hashHandle.TryGetValue(actor, out int handle))
            {
                handle = ++_nextHashHandle;
                _hashHandle[actor] = handle;
                _hashActor[handle] = actor;
            }
            (Vector3 min, Vector3 max) = HashBoxOf(actor);
            CollisionHash.Add(handle, min, max);
            _hashColLocation[handle] = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
        }

        // FCollisionHash::RemoveActor(actor, false) (Engine.dll.c:7832): unlink
        // from the extent cells at the CURRENT location; a location that moved
        // since AddActor is the shipped "moved without proper hashing" warning
        // (stale links would survive — that would be a maintenance defect in
        // our wiring, so it is loud here).
        public void HashRemoveActor(UEObject actor)
        {
            // FCollisionHash::RemoveActor: the whole body is under `if (!bDeleteMe)`
            // (disasm 0x10364e8d-0x10364e93: `test ah,2` on +0x28 -> exit) — a corpse gets neither
            // removal, nor the warning "moved without proper hashing".
            if (CollisionHash is null || actor is null || actor.Destroyed) return;
            if (!UEInterpreter.ToBool(actor.GetProp(PK.bCollideActors))) return;
            if (!_hashHandle.TryGetValue(actor, out int handle)) return;
            Vector3 loc = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
            if (_hashColLocation.TryGetValue(handle, out Vector3 colLoc) && loc != colLoc)
                Log($"collision_hash_warn moved_without_proper_hashing "
                    + $"actor={actor.GetProp(PK.Name)} loc={loc} colLoc={colLoc}");
            (Vector3 min, Vector3 max) = HashBoxOf(actor);
            CollisionHash.Remove(handle, min, max);
        }

        // This used to claim the destroy path must leave no surviving link,
        // citing shipped CheckActorNotReferenced as the invariant. Both halves
        // were wrong: that function is EMPTY in the shipped build (a bare ret,
        // Engine.dll.c:27503-27509), and the removal itself sweeps rather than
        // asserting — see below.
        //
        // ULevel::DestroyActor's hash removal (0x103a75ca): gated on
        // bCollideActors, and it passes bSweepAll=TRUE (0x103a75d5 `push 1`) —
        // the all-buckets variant, NOT the extent walk MoveActor/FarMove use.
        //
        // Established by disassembling the shipped binary, because the Ghidra
        // output renders this call with NO arguments at all (Engine.dll.c:252935)
        // — the flag is invisible in the decompile. Argument order was settled
        // from the CALLEE's frame (RemoveActor 0x10364e40 dereferences [ebp+8]
        // as AActor* and tests [ebp+0xc] as the bool), not from convention.
        //
        // Consequence, and why this no longer throws: shipped CANNOT leave a
        // stale link here — the sweep unlinks the actor wherever it is hashed,
        // including links an un-rehashed move left behind, and it does so
        // silently. The previous extent-remove + throw was a real deviation: it
        // would abort where shipped recovers. (The "moved without proper
        // hashing" warning still belongs on the extent path, which shipped logs
        // BEFORE branching on the flag; that path is unchanged.)
        public void HashRemoveForDestroy(UEObject actor)
        {
            if (CollisionHash is null || actor is null) return;
            if (!UEInterpreter.ToBool(actor.GetProp(PK.bCollideActors))) return;
            if (!_hashHandle.TryGetValue(actor, out int handle)) return;
            // Shipped emits this BEFORE branching on bSweepAll, so the destroy
            // path warns exactly like the extent path does.
            Vector3 loc = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
            if (_hashColLocation.TryGetValue(handle, out Vector3 colLoc) && loc != colLoc)
                Log($"collision_hash_warn moved_without_proper_hashing "
                    + $"actor={actor.GetProp(PK.Name)} loc={loc} colLoc={colLoc}");
            CollisionHash.RemoveAll(handle);
        }

        // FCollisionHash::ActorEncroachmentCheck (Engine.dll.c:12460): the
        // overlap set at the test pose. Swaps the actor's Location AND
        // Rotation to the test values, walks the collision-hash cells of the
        // actor's extent box in the shipped order (CollisionHashReplica:
        // x/y/z nested cells, chain order, first-visit CollisionTag dedup),
        // filters non-static brush candidates, and tests the rest with the
        // moving actor's OWN primitive PointCheck — the brush UModel
        // (oracle-validated, validate_brush.py layer 3) or the shipped
        // cylinder point check (UPrimitive::PointCheck, Engine.dll.c:24173) —
        // then restores the pose. The returned order IS the shipped candidate
        // order (validate_collisionhash.py).
        public List<UEObject> ActorEncroachmentOverlaps(UEObject actor,
            Vector3 testLoc, UERotator testRot)
        {
            if (CollisionHash is null)
                throw new InvalidOperationException("encroachment before InitCollisionHash");
            var result = new List<UEObject>();
            Vector3 oldLoc = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
            UERotator oldRot = UEInterpreter.AsRotator(actor.GetProp(PK.Rotation));
            actor.Props["Location"] = testLoc;
            actor.Props["Rotation"] = testRot;
            try
            {
                // Shipped tests each candidate with the MOVING actor's own
                // primitive: GetPrimitive (vtable +0xa4) -> PointCheck (+0x58,
                // 12546-12550). Dispatch through the one resolver, not through a
                // Brush-pointer test — that ignored Mesh priority at CollideType
                // 3, ABrush's fallback when Brush is NULL, and both UBox and
                // UOrientedCylinder.
                UEPrimitiveKind actorPrim = UEPrimitive.Resolve(actor);
                bool actorBrush = actorPrim == UEPrimitiveKind.Brush;
                HeadlessBspModel brushModel = actorBrush ? BrushModelOf(actor) : null;
                BrushCoords? coords = actorBrush ? BrushToWorldCoords(actor) : null;
                float r = UEInterpreter.AsFloat(actor.GetProp(PK.CollisionRadius));
                float h = UEInterpreter.AsFloat(actor.GetProp(PK.CollisionHeight));
                // THE MOVING ACTOR'S WIDTH, NOT THE CANDIDATE'S — this follows from the call
                // roles, which are inverted relative to intuition.
                // Disasm of the call site (10365a88):
                //   GetCylinderExtent(candidate)      -> puVar9   (the PROBE EXTENT)
                //   param_2->GetPrimitive() (+0xa4)   -> primitive of the MOVING actor
                //   primitive->PointCheck(+0x58)( Result,
                //       param_2,                       Owner = MOVING ACTOR
                //       candidate+0xfc/0x100/0x104,    probe point = CANDIDATE Location
                //       puVar9[0..2], 0 )              probe extent = candidate extent
                // So Owner (whose 0x1cc/0x1d0/0x1d4 and Location the body reads) is the moving
                // actor, and the candidate comes in as a POINT. The same value is needed by both
                // branches below (cylinder and box), so it is read once: two copies would drift
                // silently.
                float w = UEInterpreter.AsFloat(actor.GetProp(PK.CollisionWidth));
                (Vector3 qmin, Vector3 qmax) = HashBoxOf(actor);
                foreach (int handle in CollisionHash.EncroachmentCandidates(qmin, qmax))
                {
                    UEObject candidate = HashActor(handle);
                    if (candidate is null || candidate.Destroyed || ReferenceEquals(candidate, actor)) continue;
                    if (!UEInterpreter.ToBool(candidate.GetProp(PK.bCollideActors))) continue;
                    // Non-static brush candidates (other movers) are skipped by
                    // the shipped hash walk (12581-12590): Brush != NULL AND a
                    // class-chain walk to ABrush AND !bStatic. All three are
                    // required — a Brush model on a NON-ABrush class exits that
                    // chain walk normally and falls through to the candidate
                    // processing at LAB_10365a88, so testing the Brush pointer
                    // alone would silently drop a real overlap.
                    if (UEBlocking.IsBrush(candidate)
                        && !UEInterpreter.ToBool(candidate.GetProp(PK.bStatic))) continue;
                    Vector3 cloc = UEInterpreter.AsVector3(candidate.GetProp(PK.Location));
                    // Probe extent — AActor::GetCylinderExtent(candidate)
                    // (Engine.dll.c:12591, call 0x10365a99), not (R, R, H).
                    Vector3 cext = GetCylinderExtent(candidate);
                    bool hit;
                    if (actorBrush)
                    {
                        hit = brushModel.PointCheck(cloc, cext, coords).Hit;
                    }
                    else if (actorPrim == UEPrimitiveKind.Cylinder)
                    {
                        // UPrimitive::PointCheck: z slab then XY circle, both with
                        // the <= clear edges (Engine.dll.c:24204-24210).
                        //
                        // THE Z SLAB IS MEASURED FROM THE SHIFTED CENTRE. Disasm (24204), with its
                        // operand order:
                        //   fVar1 = (Owner+0x1d0 + Owner+0x104) - param_6
                        // Owner is the moving actor (role analysis above), 0x1d0 its
                        // CollisionWidth, 0x104 Location.Z, param_6 the probe point Z. So for a
                        // PLAIN CYLINDER the width shifts the volume itself, not only the hash box:
                        // the same `0x1d0 + 0x104` appears in UPrimitive::LineCheck (35190) and
                        // UPrimitive::GetCollisionBoundingBox (92160).
                        //
                        // THIS IS A LIVE PATH: the only campaign actor with a non-zero width is the
                        // player in state Mounting (Harry.uc:1482), and CheckEncroachment is called
                        // exactly on its moves.
                        float dz = (w + testLoc.z) - cloc.z;
                        float hz = cext.z + h;
                        float dx = testLoc.x - cloc.x, dy = testLoc.y - cloc.y;
                        float rr = cext.x + r;
                        hit = hz * hz > dz * dz && rr * rr > dx * dx + dy * dy;
                    }
                    else if (actorPrim == UEPrimitiveKind.Box || actorPrim == UEPrimitiveKind.Mesh)
                    {
                        // UBox::PointCheck (body 0x103FE590), validated against
                        // the real shipped body by box_point_oracle.py.
                        //
                        // MESH IS THE SAME BOX, BY VTABLE: slots +0x58/+0x5c of the vtables of
                        // UMesh (0x10471ebc), ULodMesh and USkeletalMesh (0x1047b43c) point to the
                        // bodies UBox::PointCheck 0x103fe590 and UBox::LineCheck 0x103fe620; the mesh
                        // has only its own GetCollisionBoundingBox/GetCollisionExtent (+0x60/+0x64).
                        // Verified by se_harness/bsp_diff/validate_primitive_vtables.py.
                        if (actorPrim == UEPrimitiveKind.Mesh)
                        {
                            (Vector3 lo, Vector3 hi) = MeshPrimitiveLocalBox(actor);
                            hit = ActorBoxTrace.PointCheck(cloc, cext, testLoc, lo, hi, testRot).Hit;
                        }
                        else
                            hit = ActorBoxTrace.PointCheck(cloc, cext, testLoc, r, w, h,
                                testRot).Hit;
                    }
                    else
                    {
                        // UOrientedCylinder::PointCheck (body 0x103FB7A0),
                        // validated against the real shipped body by
                        // ocyl_point_oracle.py. Only the hit decision is
                        // claimed — which is all this call site consumes.
                        hit = ActorOrientedCylinder.PointCheckHit(
                            cloc, cext, testLoc, r, w, h, testRot);
                    }
                    if (hit) result.Add(candidate);
                }
            }
            finally
            {
                actor.Props["Location"] = oldLoc;
                actor.Props["Rotation"] = oldRot;
            }
            return result;
        }

        // ── Brush actor transform ───────────────────────────────────────────
        // ABrush::ToWorld/ToLocal from the actor's serialized transform fields
        // (validated against the real Engine.dll ToWorld on all Tut1 movers
        // plus synthetic rotations — se_harness/bsp_diff/validate_brush.py
        // layer 1). Pure property math on Core types: it moved with the
        // collision world it serves.
        public BrushCoords BrushToWorldCoords(UEObject brushActor) => BrushToWorld(brushActor);

        public static BrushCoords BrushToWorld(UEObject brushActor)
        {
            Vector3 location = UEInterpreter.AsVector3(brushActor.GetProp(PK.Location));
            UERotator rot = UEInterpreter.AsRotator(brushActor.GetProp(PK.Rotation));
            Vector3 pivot = UEInterpreter.AsVector3(brushActor.GetProp(PK.PrePivot));
            return BrushCoords.ToWorld(location, rot.Pitch, rot.Yaw, rot.Roll, pivot,
                BrushScaleOf(brushActor, "MainScale"), BrushSheerOf(brushActor, "MainScale"),
                BrushScaleOf(brushActor, "PostScale"), BrushSheerOf(brushActor, "PostScale"));
        }

        public BrushCoords BrushToLocalCoords(UEObject brushActor)
        {
            Vector3 location = UEInterpreter.AsVector3(brushActor.GetProp(PK.Location));
            UERotator rot = UEInterpreter.AsRotator(brushActor.GetProp(PK.Rotation));
            Vector3 pivot = UEInterpreter.AsVector3(brushActor.GetProp(PK.PrePivot));
            return BrushCoords.ToLocal(location, rot.Pitch, rot.Yaw, rot.Roll, pivot,
                BrushScaleOf(brushActor, "MainScale"), BrushSheerOf(brushActor, "MainScale"),
                BrushScaleOf(brushActor, "PostScale"), BrushSheerOf(brushActor, "PostScale"));
        }

        public BrushBounds BrushBoundsOf(UEObject brushActor)
        {
            if (brushActor.GetProp(PK.Brush) is not UEModelRef brush
                || !BrushBoundsByExport.TryGetValue(brush.ExportIndex, out BrushBounds b))
                throw new NotSupportedException(
                    $"headless brush bounds missing actor={brushActor.GetProp(PK.Name)} class={brushActor.Class?.Name}");
            return b;
        }

        static Vector3 BrushScaleOf(UEObject brushActor, string prop)
        {
            if (brushActor.GetProp(prop) is not UEObject sc) return Vector3.one;
            Vector3 v = UEInterpreter.AsVector3(sc.GetProp(PK.Scale));
            return v == Vector3.zero ? Vector3.one : v; // missing decodes as identity
        }

        static float BrushSheerOf(UEObject brushActor, string prop)
            => brushActor.GetProp(prop) is UEObject sc
                ? UEInterpreter.AsFloat(sc.GetProp(PK.SheerRate)) : 0f;

        // Shipped brush-actor trace: the brush's own UModel::LineCheck with the
        // actor's ToWorld coords (both trace branches validated 4800/4800
        // against the Engine.dll emulator, validate_brush.py layer 2). Runs the
        // exact trace unconditionally: the earlier corner-transform envelope
        // cull used unvalidated math (continuous trig, its own AABB formula)
        // and could silently drop a real contact; the shipped collision-hash
        // broadphase is a separate unit to port and validate on its own.
        public BspHit TraceBrushActor(UEObject brushActor, Vector3 from, Vector3 to, Vector3 extent)
        {
            HeadlessBspModel model = BrushModelOf(brushActor);
            BrushCoords coords = BrushToWorldCoords(brushActor);
            return extent.x == 0f && extent.y == 0f && extent.z == 0f
                ? model.TraceWorldLine(from, to, coords)
                : model.TraceWorldBox(from, to, extent, coords);
        }

        // Non-brush actor primitive for a trace: the CollideType dispatch —
        // UBox (2), UOrientedCylinder (1, tilt unsupported and loud), else the
        // shipped cylinder. Both trace bodies are oracle-validated
        // (cylinder_oracle 2000, box_oracle 2000).
        public ActorHit TraceNonBrushActor(UEObject candidate, Vector3 start,
            Vector3 end, Vector3 extent)
        {
            Vector3 location = UEInterpreter.AsVector3(candidate.GetProp(PK.Location));
            float radius = UEInterpreter.AsFloat(candidate.GetProp(PK.CollisionRadius));
            float width = UEInterpreter.AsFloat(candidate.GetProp(PK.CollisionWidth));
            float height = UEInterpreter.AsFloat(candidate.GetProp(PK.CollisionHeight));
            UERotator rotation = UEInterpreter.AsRotator(candidate.GetProp(PK.Rotation));
            // The body is chosen by the PRIMITIVE (AActor::GetPrimitive -> vtable +0x5c), not
            // the raw CollideType: CT 3 with a mesh is a UMesh, whose vtable LineCheck is the
            // body UBox::LineCheck 0x103fe620 (see PointCheck in ActorEncroachmentOverlaps);
            // CT 3 without mesh or brush is a cylinder.
            UEPrimitiveKind prim = UEPrimitive.Resolve(candidate);
            if (prim == UEPrimitiveKind.Mesh)
            {
                // UBox body, box from the mesh (its slot +0x60), not (R, W, H).
                (Vector3 lo, Vector3 hi) = MeshPrimitiveLocalBox(candidate);
                return ActorBoxTrace.LineCheck(start, end, extent, location, lo, hi, rotation);
            }
            if (prim == UEPrimitiveKind.Box)
                return ActorBoxTrace.LineCheck(start, end, extent, location,
                    radius, width, height, rotation);
            if (prim == UEPrimitiveKind.OrientedCylinder)
            {
                // UOrientedCylinder::LineCheck (Engine.dll.c:18845-18930, thunk
                // 0x1030155f): Start/End are transformed into the owner's LOCAL frame
                // (AActor::ToLocal, vtable +0x98 — UnitCoords/Rotation axes with origin at
                // Location) and IMMEDIATELY re-based onto Location (`+ param_2[0x3f..0x41]`),
                // extent = FBox(−e,e).TransformBy (sum of absolute axes), then THE SAME
                // UPrimitive::LineCheck with the same Owner; on a hit Location/Normal are returned
                // via FCoords::Transpose (origin −Origin·M, axes as columns). Both tilted
                // (Pitch/Roll ≠ 0) and yaw-only go through this shipped body. Live carrier of a
                // tilt: the BlockPlayer rings on the Quidditch maps (Pitch 16288, CT1, R 60..88, H 5).
                BrushCoords local = BrushCoords.Identity;
                local.DivRotator(rotation.Pitch, rotation.Yaw, rotation.Roll);
                Vector3 s0 = local.TransformVector(start - location);
                Vector3 e0 = local.TransformVector(end - location);
                Vector3 ls = new(s0.x + location.x, s0.y + location.y, s0.z + location.z);
                Vector3 le = new(e0.x + location.x, e0.y + location.y, e0.z + location.z);
                Vector3 lext = new(
                    Math.Abs(local.X.x) * extent.x + Math.Abs(local.X.y) * extent.y + Math.Abs(local.X.z) * extent.z,
                    Math.Abs(local.Y.x) * extent.x + Math.Abs(local.Y.y) * extent.y + Math.Abs(local.Y.z) * extent.z,
                    Math.Abs(local.Z.x) * extent.x + Math.Abs(local.Z.y) * extent.y + Math.Abs(local.Z.z) * extent.z);
                ActorHit lh = ActorCylinderTrace.LineCheck(ls, le, lext,
                    location.x, location.y, location.z, width, radius, height);
                if (!lh.Blocked) return lh;
                BrushCoords inv = new()
                {
                    Origin = -local.TransformVector(location),
                    X = new Vector3(local.X.x, local.Y.x, local.Z.x),
                    Y = new Vector3(local.X.y, local.Y.y, local.Z.y),
                    Z = new Vector3(local.X.z, local.Y.z, local.Z.z),
                };
                Vector3 d = new((lh.Location.x - location.x) - inv.Origin.x,
                    (lh.Location.y - location.y) - inv.Origin.y, (lh.Location.z - location.z) - inv.Origin.z);
                return new ActorHit(true, lh.Time, inv.TransformVector(d), inv.TransformVector(lh.Normal));
            }
            // THE Z SHIFT BELONGS TO THE BODY, NOT TO CollideType. UPrimitive::LineCheck reads
            // `Owner+0x1d0 + Owner+0x104` UNCONDITIONALLY (Engine.dll.c:35190), and exactly the
            // primitives that run this body arrive here:
            //   CT 0        — UPrimitive itself;
            //   CT 3 without mesh or brush — also a UPrimitive cylinder (3 is neither 1 nor 2);
            //                 CT 3 WITH A MESH does NOT come here — that is the UBox body above;
            //   CT 1        — UOrientedCylinder::LineCheck (18845) is handled by the branch above
            //                 (any rotation, including tilt) and does not come here.
            // This is live: dozens of actors on the TAS route carry a non-zero width
            // (CollisionWidth is serialized in 40 of 41 maps; note `grep` needs `-a` on .unr, and
            // `--actorpropsall` Props is a LIST, not a dictionary). Mesh actors such as
            // TransTrestleTable0 (48.6), HogTrestleBench22 (49.93), TransBlackboard0 (35.84) go
            // through the UBox body (UMesh vtable, see above), not this cylinder — shifting them by
            // 0x1d0 made the Lev_Tut1b gnomes pass under a sofa whose cylinder hung
            // CollisionWidth=73 above the floor.
            return ActorCylinderTrace.LineCheck(start, end, extent,
                location.x, location.y, location.z, width, radius, height);
        }

        // The world sentinel the shipped blocking rule compares against
        // (Actors[0] is asserted to be the LevelInfo, ../Inc/UnLevel.h:0xf7).
        UEObject LevelSentinel => Actors.Count > 0 ? Actors[0] : null;

        bool IsBlockedByActor(UEObject actor, UEObject other)
            => UEBlocking.IsBlockedBy(actor, other, LevelSentinel);

        // The rider orbit: build the yaw-only coords from the MASKED yaw delta
        // through the shipped rotator path — NOT continuous trig — then
        // TransformVectorBy(rel, coords).
        static Vector3 OrbitTransform(Vector3 rel, int maskedYawDelta)
        {
            BrushCoords c = BrushCoords.Identity;
            c.MulRotator(0, maskedYawDelta, 0);
            return c.TransformVector(rel);
        }

        // Shipped AActor::BeginTouch (Engine.dll.c:225598) — sequential
        // asymmetric TouchTo with slot eviction; events interleaved with the
        // bookkeeping (see UELifecycle).
        public void TouchActor(UEInterpreter interp, UEObject a, UEObject b)
        {
            UELifecycle.BeginTouch(interp, a, b);
            if (TraceEvents && (a.GetProp(PK.Player) is not null || b.GetProp(PK.Player) is not null)
                && (Array.IndexOf(a.TouchList(), b) >= 0 || Array.IndexOf(b.TouchList(), a) >= 0))
                Log($"touch_begin a={a.GetProp(PK.Name)} b={b.GetProp(PK.Name)}");
        }

        // Shipped AActor::EndTouch (Engine.dll.c FUN_1037a3e0).
        public void UnTouchActor(UEInterpreter interp, UEObject a, UEObject b)
        {
            bool linked = Array.IndexOf(a.TouchList(), b) >= 0 || Array.IndexOf(b.TouchList(), a) >= 0;
            UELifecycle.EndTouch(interp, a, b, noNotifySelf: false);
            if (linked && TraceEvents && (a.GetProp(PK.Player) is not null || b.GetProp(PK.Player) is not null))
                Log($"touch_end a={a.GetProp(PK.Name)} b={b.GetProp(PK.Name)}");
        }

        // The CALLER's FCheckResult of ULevel::MoveActor / SingleLineCheck.
        // Shipped fields: Actor +4, Location +8, Normal +0x14, Time +0x24, Item
        // +0x28; constructor FCheckResult(1.0): Time 1, Item −1, rest zero.
        //   * MoveActor writes it ONLY on the full path (0x103aa530-0x103aa552:
        //     a copy of FCheckResult(1.0), then the move result). The fast path
        //     (0x103aa48c-0x103aa501) and the bStatic/!bMovable refusal (0x103aa3ed-
        //     0x103aa41b) do NOT touch it — the caller reads what was there.
        //   * SingleLineCheck on a miss writes only Time=1 and Actor=NULL
        //     (0x103ac4af-0x103ac4b9), on a hit — a copy of the whole record.
        // It is the caller's LOCAL, so in the port it lives with the caller too: each
        // physics body creates it where shipped constructs its own and passes it to
        // MoveActor/traces. MoveActor still publishes the actor's _lastMove* properties on
        // the full path — for fixtures and diagnostics; production bodies do not read them.
        public sealed class MoveCheck
        {
            public float Time = 1f;
            public Vector3 Normal;
            public UEObject Actor;
            public int Item = -1;
            public Vector3 Location;

            public void Set(float time, Vector3 normal, UEObject actor, int item, Vector3 location)
            {
                Time = time; Normal = normal; Actor = actor; Item = item; Location = location;
            }

            // ULevel::SingleLineCheck into the same record (0x103ac297-0x103ac4c0).
            public void FromTrace(in LineCheckHit h)
            {
                if (!h.Blocked) { Time = 1f; Actor = null; return; }
                Set(h.Time, h.Normal, h.Actor, h.Node, h.Location);
            }

            // Test seam: the record from the published _lastMove* of the actor's last full
            // move (fixtures that feed Mount by hand).
            public static MoveCheck FromActorProps(UEObject a) => new MoveCheck
            {
                Time = UEInterpreter.AsFloat(a.GetProp(PK._lastMoveFraction)),
                Normal = UEInterpreter.AsVector3(a.GetProp(PK._lastMoveNormal)),
                Actor = a.GetProp(PK._lastMoveActor) as UEObject,
                Item = a.GetProp(PK._lastMoveNode) is null ? -1 : UEInterpreter.ToInt(a.GetProp(PK._lastMoveNode)),
                Location = UEInterpreter.AsVector3(a.GetProp(PK._lastMoveHitLocation)),
            };
        }

        // A swept MoveActor hit. Plain struct, not a positional record struct
        // (C# 10) — Core builds on the netstandard2.1 + C#9 profile.
        public readonly struct MoveHit
        {
            public float Fraction { get; }
            public Vector3 Normal { get; }
            public UEObject Actor { get; }
            public bool Blocking { get; }
            public int Node { get; }
            public Vector3 Location { get; }

            public MoveHit(float Fraction, Vector3 Normal, UEObject Actor,
                bool Blocking, int Node, Vector3 Location)
            {
                this.Fraction = Fraction;
                this.Normal = Normal;
                this.Actor = Actor;
                this.Blocking = Blocking;
                this.Node = Node;
                this.Location = Location;
            }
        }

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
        // (UEBspCollision.cs:2337), so ANY non-zero-extent box trace lands off the
        // ray by the support distance (they coincide only for a ZERO extent against a
        // cylinder). Pinned end-to-end by trace_copies_result_location_box_extent.
        // Execution counter of the coincident-face branch (fixture --moverspot).
        public int CoincidentFaceHits;

        // ── MultiLineCheck x87 chains. The body sets no control word: PC64 in game logic, PC24 (every operation
        // rounded to float) inside the render window. Judge: x87_size_oracle.py kinds mlcworld, mlcnear, mlcpull. ──

        // World-hit shortening 0x103ac70a..0x103ac805. PC64: d = Hit.Location − Start in the register, (dx²+dy²)+dz²,
        // fstp qword, appSqrt — dist stays in the register (the fst qword is a spill); ratio = (dist + 5f)·Time /
        // (dist + 0.0001f) in the register; `fld 1.0; fcomp st(1); test ah,0x41; je` stores the ratio only when 1 is
        // above it (NaN and >= 1 give 1.0f). ActorEnd: ex·f and ey·f stored, ez = End.Z − Start.Z and ez·f kept in the
        // register, + Start, fstp dword. Very close: `fld Time; fcomp 0.01f; test ah,1` (C0: below or NaN), then
        // `fcomp 30f` on the register dist, C0 again.
        internal static float MultiLineWorldFactor(Vector3 start, Vector3 end, Vector3 worldLoc, float time, bool pc24,
            out Vector3 actorEnd, out bool veryClose)
        {
            if (pc24)
            {
                Vector3 w24 = worldLoc - start;
                float d24 = UEVector.Size24(w24.x, w24.y, w24.z);
                float r24 = ((d24 + 5f) * time) / (d24 + 0.0001f);
                float f24 = 1f > r24 ? r24 : 1f;
                actorEnd = start + (end - start) * f24;
                veryClose = !(time >= 0.01f) && !(d24 >= 30f);
                return f24;
            }
            static HP1.Core.X87Ext X(float v) => HP1.Core.X87Ext.From(v);
            static HP1.Core.X87Ext R(HP1.Core.X87Ext v) => HP1.Core.X87Ext.Round64(v);
            HP1.Core.X87Ext dist = HP1.Core.X87Ext.Sqrt64(HP1.Core.X87Ext.From(DistSq87(worldLoc, start).ToDouble()));
            HP1.Core.X87Ext ratio = HP1.Core.X87Ext.Div64(R(R(dist + X(5f)) * X(time)), R(dist + X(0.0001f)));
            float factor = (HP1.Core.X87Ext.Fcom(X(1f), ratio) & 0x41) == 0 ? ratio.ToFloat() : 1f;
            HP1.Core.X87Ext ez = R(X(end.z) - X(start.z));
            actorEnd = new Vector3((end.x - start.x) * factor + start.x, (end.y - start.y) * factor + start.y,
                R(R(ez * X(factor)) + X(start.z)).ToFloat());
            veryClose = (HP1.Core.X87Ext.Fcom(X(time), X(0.01f)) & HP1.Core.X87Ext.C0) != 0
                && (HP1.Core.X87Ext.Fcom(dist, X(30f)) & HP1.Core.X87Ext.C0) != 0;
            return factor;
        }

        // Coincident-face distance test 0x103ac99a..0x103ac9d8: d = hit − world in the register, (dx²+dy²)+dz²,
        // `fcomp 4.0; test ah,1; je skip` — the pull-back runs on C0: below 4 or unordered (NaN).
        internal static bool MultiLineNearWorldHit(Vector3 loc, Vector3 worldLoc, bool pc24)
            => pc24 ? !((loc - worldLoc).sqrMagnitude >= 4f)
                : (HP1.Core.X87Ext.Fcom(DistSq87(loc, worldLoc), HP1.Core.X87Ext.From(4f)) & HP1.Core.X87Ext.C0) != 0;

        // Pull-back 0x103ac9de..0x103acaee. PC64: span² = dxr·dxf + dy² + dz² — dx = ActorEnd.X − Start.X stays in the
        // register and multiplies its own float copy (fst dword), dy and dz are stored; appSqrt, span stored as float;
        // inv = 1.0f / span in the register; dir = f32(inv · d) per component (the X product in the register, 2·dir
        // exact), Location = world − 2·dir; Time = appSqrt((vz² + vy²) + vx²) / span (v = Location − Start, stored
        // floats), fstp dword.
        internal static (Vector3 loc, float time) MultiLinePullBack(Vector3 start, Vector3 actorEnd, Vector3 worldLoc, bool pc24)
        {
            if (pc24)
            {
                Vector3 d = actorEnd - start;
                float s24 = UEVector.Size24(d.x, d.y, d.z);
                float i24 = 1f / s24;
                Vector3 l24 = worldLoc - new Vector3(i24 * d.x, i24 * d.y, i24 * d.z) * 2f;
                Vector3 v24 = l24 - start;
                return (l24, UEVector.Size24(v24.z, v24.y, v24.x) / s24);
            }
            static HP1.Core.X87Ext X(float v) => HP1.Core.X87Ext.From(v);
            static HP1.Core.X87Ext R(HP1.Core.X87Ext v) => HP1.Core.X87Ext.Round64(v);
            HP1.Core.X87Ext dxr = R(X(actorEnd.x) - X(start.x));
            float dxf = dxr.ToFloat(), dyf = actorEnd.y - start.y, dzf = actorEnd.z - start.z;
            HP1.Core.X87Ext s2 = R(R(R(dxr * X(dxf)) + X(dyf) * X(dyf)) + X(dzf) * X(dzf));
            float span = HP1.Core.X87Ext.Sqrt64(HP1.Core.X87Ext.From(s2.ToDouble())).ToFloat();
            HP1.Core.X87Ext inv = HP1.Core.X87Ext.Div64(X(1f), X(span));
            var dir = new Vector3(R(inv * X(dxf)).ToFloat(), R(inv * X(dyf)).ToFloat(), R(inv * X(dzf)).ToFloat());
            Vector3 loc = worldLoc - dir * 2f;
            Vector3 v = loc - start;
            float time = HP1.Core.X87Ext.Div64(UEVector.Size64Ext(v.z, v.y, v.x), X(span)).ToFloat();
            return (loc, time);
        }

        public List<MoveHit> MultiLineCheck(Vector3 start, Vector3 end, Vector3 extent,
            bool checkActors, UEObject levelInfo)
        {
            var results = new List<MoveHit>();
            bool haveWorld = false;
            Vector3 worldNormal = Vector3.zero, worldLoc = Vector3.zero;
            // Disasm (MultiLineCheck 0x103ac620): factor is [ebp-0x18] (1.0 at 0x103ac666,
            // clamp 0x103ac779, times the actor hit Time 0x103acaf7); worldVeryClose is
            // [ebp-0x30] (0 at 0x103ac66f, 1 at 0x103ac7f8, tested 0x103ac8fa).
            float factor = 1f;            // local_1c
            bool worldVeryClose = false;  // local_34
            Vector3 actorEnd = end;

            if (levelInfo is not null && Bsp is not null)
            {
                // Shipped passes the ExtraNodeFlags byte of its call here
                // (0x103ac694-0x103ac69e: `and eax,0xff; push eax` — the last
                // argument of UModel::LineCheck, the decompile lost it). The port passes 0, and
                // that is equivalent: a census of all calls of ULevel vtable +0xc4
                // (SingleLineCheck, forwards its NodeFlags) and +0xcc (MultiLineCheck) in the
                // Engine.dll (88 sites) and Render.dll (2) disasm — every game caller passes 0:
                // a literal `push 0` or a register zeroed by `xor`. If a non-zero caller
                // appears, the flags must be threaded into the world-trace worker.
                BspHit w = extent.x == 0f && extent.y == 0f && extent.z == 0f
                    ? Bsp.TraceWorldLine(start, end) : Bsp.TraceWorldBox(start, end, extent);
                if (w.Blocked)
                {
                    worldLoc = w.Location;
                    worldNormal = w.Normal;
                    haveWorld = true;
                    results.Add(new MoveHit(w.Fraction, w.Normal, levelInfo, false, w.Node, worldLoc));
                    // 256040-256050: shorten the actor ray past the world hit; 256063-256072: a world hit
                    // THIS close leaves only movers.
                    factor = MultiLineWorldFactor(start, end, worldLoc, w.Fraction, UEWorldFrameProducer.FastMathWindow,
                        out actorEnd, out worldVeryClose);
                }
            }

            if ((checkActors || levelInfo is not null) && CollisionHash is not null)
            {
                foreach (MoveHit hit in ActorLineCheck(start, actorEnd, extent))
                {
                    if (results.Count >= 0x40) break;     // shipped uVar12 < 0x40 cap
                    bool isMover = UEInterpreter.ClassIsA(hit.Actor.Class, "Mover");
                    // 256051 / 256074: without actor collision — and when the
                    // world hit is very close — only movers get through.
                    if (!checkActors && !isMover) continue;
                    if (worldVeryClose && !isMover) continue;

                    float time = hit.Fraction;
                    Vector3 loc = hit.Location;
                    // 256054-256062: coincident-face dedup. A mover face whose normal
                    // EQUALS the world normal (fcomp per component — float equality,
                    // −0 == +0; disasm 0x103ac95e-0x103ac9c7) and lands within 2 units
                    // of the world hit is pulled 2 units back along the ray. The world
                    // hit here is a FMovingBrushTracker fragment of the same mover
                    // (UEMovingBrushTracker.cs) — that is what makes the branch live.
                    // Branch execution is proven (--moverspot, 4 cases from the original's
                    // journal); "the world's, not the mover's" is the form from the disasm: on these
                    // inputs both hits match bit-for-bit (mutation retired in completeness_mutations.txt).
                    if (haveWorld && isMover
                        && hit.Normal == worldNormal
                        && MultiLineNearWorldHit(loc, worldLoc, UEWorldFrameProducer.FastMathWindow))
                    {
                        CoincidentFaceHits++;
                        // 256110-256112: the new location is built from the
                        // WORLD hit (local_b58/b5c/b60 = Location of the first
                        // result record [ebp-0xb64], read at 0x103aca54..0x103aca66
                        // as in the distance test 0x103ac99d..0x103ac9b1), NOT from the mover's
                        // own hit — `world - 2*dir`, not `mover - 2*dir`.
                        // They can sit up to 2 units apart, and the recomputed
                        // Time inherits the difference. No zero-span guard: shipped has none.
                        (loc, time) = MultiLinePullBack(start, actorEnd, worldLoc, UEWorldFrameProducer.FastMathWindow);
                    }
                    // 256103: rescale back to the FULL span, AFTER the dedup.
                    results.Add(new MoveHit(time * factor, hit.Normal, hit.Actor,
                        false, hit.Node, loc));
                }
            }

            // 256153: shipped sorts the collected array with appQsort by time —
            // appQsort (Core.dll.c:18592) is MSVCRT qsort, comparator 0x1030385a ->
            // 0x103ad500: A.Time < B.Time -> -1, A.Time > B.Time -> +1, else 0.
            //
            // THE ORDER OF EQUAL TIMES IS PORTED (MsvcQsort): on Lev5_fluffy the Mover7/Mover9
            // tie at fraction 0.196 decides fluffy0's Base, and the live original picks Mover9.
            // MsvcQsort is verified against the real msvcrt.dll qsort (original's bottle) under
            // unicorn: se_harness/bsp_diff/validate_msvcrt_qsort.py.
            if (CaptureLineCheckPreSort) LastLineCheckPreSort = new List<MoveHit>(results);
            HP1.Core.MsvcQsort.Sort(results, (a, b) =>
                a.Fraction < b.Fraction ? -1 : a.Fraction > b.Fraction ? 1 : 0);
            if (TraceTies)
                for (int i = 1; i < results.Count; i++)
                    if (results[i].Fraction == results[i - 1].Fraction)
                        HP1.Core.UETrace.Info($"trace_tie frac={results[i].Fraction:R} "
                            + $"a={results[i - 1].Actor?.GetProp(PK.Name)} "
                            + $"b={results[i].Actor?.GetProp(PK.Name)}");
            return results;
        }

        // The swept box of ActorLineCheck: an empty FBox += Start, then += End (Core FBox::operator+= 0x1011ba10), then
        // Min − Extent and Max + Extent. Min keeps the old value unless it is strictly above the new one (`test ah,0x41;
        // je`: unordered keeps it), Max takes the new one unless the old is not below it (`test ah,1; jne`: unordered
        // takes it). A NaN End (physWalking's O/P probes of a zero adjust) gives Min = Start, Max = NaN; the cell of a
        // NaN is ROUND → the integer indefinite (negative) → 0 (CellIndex), so the walk is empty unless Min lies in
        // cell 0. Judge: se_harness/bsp_diff/nan_slope_oracle.py kind aline (the real ActorLineCheck 0x10365cc0).
        internal static (Vector3 min, Vector3 max) ActorLineCheckBox(Vector3 start, Vector3 end, Vector3 extent)
        {
            static float Lo(float old, float v) => old > v ? v : old;
            static float Hi(float old, float v) => old >= v ? old : v;
            return (new Vector3(Lo(start.x, end.x) - extent.x, Lo(start.y, end.y) - extent.y, Lo(start.z, end.z) - extent.z),
                new Vector3(Hi(start.x, end.x) + extent.x, Hi(start.y, end.y) + extent.y, Hi(start.z, end.z) + extent.z));
        }

        // FCollisionHash::ActorLineCheck (Engine.dll.c:161416): candidates come
        // from the swept AABB's hash CELLS (the shipped CollisionTag first-visit
        // dedup and the replica's oracle-validated walk order), each tested with
        // its OWN primitive — GetPrimitive (+0xa4) -> LineCheck (+0x5c). Shipped
        // PREPENDS every hit, so the returned order is reverse discovery and
        // MultiLineCheck sorts afterwards; kept in that shape.
        List<MoveHit> ActorLineCheck(Vector3 start, Vector3 end, Vector3 extent)
        {
            var found = new List<MoveHit>();
            (Vector3 qmin, Vector3 qmax) = ActorLineCheckBox(start, end, extent);
            foreach (int handle in CollisionHash.EncroachmentCandidates(qmin, qmax))
            {
                UEObject candidate = HashActor(handle);
                if (candidate is null || candidate.Destroyed) continue;
                if (!UEInterpreter.ToBool(candidate.GetProp(PK.bCollideActors))) continue;
                if (UEPrimitive.Resolve(candidate) == UEPrimitiveKind.Brush)
                {
                    BspHit b = TraceBrushActor(candidate, start, end, extent);
                    if (!b.Blocked) continue;
                    found.Insert(0, new MoveHit(b.Fraction, b.Normal, candidate, false, b.Node, b.Location));
                    continue;
                }
                ActorHit a = TraceNonBrushActor(candidate, start, end, extent);
                if (!a.Blocked) continue;
                found.Insert(0, new MoveHit(a.Time, a.Normal, candidate, false, -1, a.Location));
            }
            return found;
        }

        // The MoveActor sweep's hit list: shipped MultiLineCheck, then Blocking
        // filled in the way the caller does — one IsBlockedBy against the moving
        // actor, which resolves the world hit through its own Level leg.
        public void CollectMoveHits(UEObject actor, Vector3 from, Vector3 to, Vector3 extent,
            bool traceWorld, bool traceActors, List<MoveHit> hits)
        {
            UEObject levelInfo = LevelSentinel;
            foreach (MoveHit hit in MultiLineCheck(from, to, extent, traceActors,
                         traceWorld ? levelInfo : null))
            {
                // Shipped drops the mover itself through the Owner/CollisionTag
                // walk; the hash can still hand it back, so drop it here.
                if (ReferenceEquals(hit.Actor, actor)) continue;
                hits.Add(new MoveHit(hit.Fraction, hit.Normal, hit.Actor,
                    UEBlocking.IsBlockedBy(actor, hit.Actor, levelInfo), hit.Node, hit.Location));
            }
        }

        // ULevel::FarMoveActor (Engine.dll.c:159147) — the SetLocation(267)
        // path: teleport the actor to a new location. The shipped gate (asm
        // 0x103a9f61 block) is bCollideWorld (0x1dc bit 1) or
        // bCollideWhenPlacing (0x198 bit 15, non-client): those run FindSpot
        // with bTryCurrent=0 — the nudge loop ALWAYS runs, there is no direct
        // accept — and a failed FindSpot refuses the teleport. Then
        // CheckEncroachment at the (possibly nudged) spot. A successful move
        // commits Location + OldLocation, marks bJustTeleported, drops based
        // riders, and re-zones.
        public bool FarMove(UEInterpreter interp, UEObject actor, Vector3 newLoc,
            bool bTest, bool bNoCheck)
        {
            // Guard 0x103a9e29..0x103a9e45: (bStatic || !bMovable) && !GIsEditor → 0.
            // The head does NOT check bDeleteMe: a destroyed actor moves, and the hash refuses
            // it on its own — FCollisionHash::RemoveActor/AddActor exit on bDeleteMe
            // (0x10364e93, 0x10364ba5), see HashRemoveActor/HashAddActor.
            if (UEInterpreter.ToBool(actor.GetProp(PK.bStatic))
                || !UEInterpreter.ToBool(actor.GetProp(PK.bMovable))) return false;
            HashRemoveActor(actor);
            bool rejoined = false;
            try
            {
                if (!bNoCheck && (UEInterpreter.ToBool(actor.GetProp(PK.bCollideWorld))
                    || UEInterpreter.ToBool(actor.GetProp(PK.bCollideWhenPlacing))))
                {
                    if (Bsp is null)
                        throw new NotSupportedException(
                            $"FarMoveActor FindSpot without a world model actor={actor.GetProp(PK.Name)}");
                    // Extent — AActor::GetCylinderExtent(this) (Engine.dll.c:159191,
                    // call 0x103a9f24), not (R, R, H): they differ for CollideType 2/3.
                    Vector3 spot = newLoc;
                    bool fits = FindSpot(GetCylinderExtent(actor), ref spot, tryCurrent: false);
                    if (!fits)
                    {
                        Log($"far_move_findspot_fail actor={actor.GetProp(PK.Name)} "
                            + $"class={actor.Class?.Name} location={newLoc}");
                        return false;
                    }
                    if (spot != newLoc)
                        Log($"far_move_findspot_adjust actor={actor.GetProp(PK.Name)} "
                            + $"from={newLoc} to={spot}");
                    newLoc = spot;
                }
                // CheckEncroachment (Engine.dll.c:62906) at the new location; a
                // blocking encroachment refuses the move (bTouchActors=1 per asm
                // 0x103a9f6c).
                if (!bTest && !bNoCheck && CheckEncroachment(interp, actor, newLoc,
                        UEInterpreter.AsRotator(actor.GetProp(PK.Rotation)), bTouchActors: true))
                    return false;

                if (!bTest && UEInterpreter.ToInt(actor.GetProp(PK.StandingCount)) != 0)
                    foreach (UEObject rider in Actors.ToArray())
                        if (rider is not null && ReferenceEquals(rider.GetProp(PK.Base) as UEObject, actor))
                            SetBase(interp, rider, null, notify: true);
                if (!bTest) actor.Props["bJustTeleported"] = true;
                actor.Props["Location"] = newLoc;
                actor.Props["OldLocation"] = newLoc;
                // 0x103aa09f..0x103aa0b5: Hash->AddActor BEFORE SetActorZone (0x103aa0b8..0x103aa0ca): the zone event
                // handlers see the actor in the collision hash. SetActorZone(Actor, bTest, 0) reads the live Location;
                // a test move refreshes the region silently, no zone events.
                HashAddActor(actor); rejoined = true;
                SetActorZone(interp, actor, UEInterpreter.AsVector3(actor.GetProp(PK.Location)), bTest);
                return true;
            }
            finally
            {
                // LAB_103aa09f: the actor rejoins the hash on every exit, at
                // whatever Location the path left it with.
                if (!rejoined) HashAddActor(actor);
            }
        }

        // ULevel::CheckEncroachment (Engine.dll.c:62906, body 0x103ab600): does
        // `actor` at (testLoc, testRot) encroach a blocking actor? Returns true =
        // blocked (the caller refuses the move). Full shipped flow: overlap set at
        // the test pose (ActorEncroachmentOverlaps), per-overlap block-bit
        // discrimination, the moving-brush push (moveSmooth by the move delta,
        // re-query, second push with the NEGATED delta from the test pose), the
        // EncroachingOn event whose nonzero return blocks (asm 0x103aba72), then
        // touch maintenance and the EncroachedBy pass.
        public bool CheckEncroachment(UEInterpreter interp, UEObject actor, Vector3 testLoc,
            UERotator testRot, bool bTouchActors)
        {
            // Gate (0x103ab63a `test byte [esi+0x1dc], 0xd`): bit0|bit2|bit3 =
            // bCollideActors|bBlockActors|bBlockPlayers (AActor::SetCollision
            // writes them at 0x1dc bits 0/2/3; bProjTarget is bit4, NOT in the
            // mask), else only a NON-static brush enters (0x103ab643).
            bool hasBits = UEInterpreter.ToBool(actor.GetProp(PK.bCollideActors))
                || UEInterpreter.ToBool(actor.GetProp(PK.bBlockActors))
                || UEInterpreter.ToBool(actor.GetProp(PK.bBlockPlayers));
            // Same three shipped tests as the candidate walk (62942-62952): the
            // gate lets a brush in ONLY when it is a real ABrush and NOT static.
            bool actorMovingBrush = UEBlocking.IsBrush(actor)
                && !UEInterpreter.ToBool(actor.GetProp(PK.bStatic));
            if (!hasBits && !actorMovingBrush) return false;

            List<UEObject> overlaps = ActorEncroachmentOverlaps(actor, testLoc, testRot);
            UEObject level = interp.GetLevelInfo();
            if (overlaps.Count > 0)
            {
                var names = new List<string>();
                foreach (UEObject o in overlaps) names.Add($"{o.GetProp(PK.Name)}");
                Log($"encroach_candidates actor={actor.GetProp(PK.Name)} "
                    + $"order=[{string.Join(",", names)}]");
            }
            foreach (UEObject other in overlaps)
            {
                if (ReferenceEquals(other, actor) || ReferenceEquals(other, level)) continue;
                if (!IsBlockedByActor(actor, other)) continue;
                bool otherMovingBrush = UEBlocking.IsBrush(other)
                    && !UEInterpreter.ToBool(other.GetProp(PK.bStatic));
                if (actorMovingBrush && !otherMovingBrush)
                {
                    // Push the encroached actor by the move delta (0x103ab7d7);
                    // if it still overlaps the test pose, push it again with the
                    // NEGATED delta while the brush sits at the test location
                    // (0x103ab99b), then fall through to the event.
                    Vector3 loc = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
                    var delta = new Vector3(testLoc.x - loc.x, testLoc.y - loc.y, testLoc.z - loc.z);
                    MoveSmooth(interp, other, delta);
                    List<UEObject> again = ActorEncroachmentOverlaps(actor, testLoc, testRot);
                    if (!again.Contains(other)) continue; // pushed clear
                    actor.Props["Location"] = testLoc;
                    MoveSmooth(interp, other, new Vector3(-delta.x, -delta.y, -delta.z));
                    actor.Props["Location"] = loc;
                }
                // Actor.EncroachingOn(Other): a nonzero return blocks the move
                // (return 1 path at 0x103aba72 that the decompiler dropped).
                // Through ProcessEvent: an actor destroyed during the move and the probe mask
                // cut off the body, ReturnValue stays 0 — the move is not blocked.
                if (GuardResult(() => UEInterpreter.ToBool(
                        UELifecycle.CallEventResult(interp, actor, "EncroachingOn", other)), "EncroachingOn"))
                    return true;
            }

            if (bTouchActors)
                foreach (UEObject t in actor.TouchList())
                    if (t is not null && !IsOverlapping(actor, t))
                        UnTouchActor(interp, actor, t);

            // Second pass over the ORIGINAL overlap list (0x103abb30): the real
            // IsBlockedBy test — non-blocking overlaps BeginTouch (if asked),
            // blocking ones fire Other.EncroachedBy(Actor).
            foreach (UEObject other in overlaps)
            {
                if (ReferenceEquals(other, actor) || ReferenceEquals(other, level)) continue;
                if (!IsBlockedByActor(actor, other))
                {
                    if (bTouchActors) TouchActor(interp, actor, other);
                    continue;
                }
                UELifecycle.CallEvent(interp, other, "EncroachedBy", actor);
            }
            return false;
        }

        // Shipped ULevel::MoveActor (Engine.dll.c:123689, 0x103aa390..0x103ab231)
        // (UActor.cpp:1367): the atomic sweep-move-resolve primitive behind the
        // Move native. Orchestration follows the shipped control flow: tiny-delta
        // fast path, 2-unit padded sweep with the hit time re-based to the
        // unpadded span, rider carry (with yaw orbit) BEFORE the commit,
        // unconditional rotation commit, then Bump/Touch/UnTouch/re-zone. Returns
        // "moved at all" (Result.Time > 0), not "unblocked" — a partial move is
        // TRUE.
        // The shipped head guard alone (0x103aa3ed): bStatic || !bMovable.
        static bool MoveActorRefuses(UEObject actor)
            => UEInterpreter.ToBool(actor.GetProp(PK.bStatic))
               || !UEInterpreter.ToBool(actor.GetProp(PK.bMovable));

        // Fast-path threshold: |dx|,|dy|,|dz| < 1e-4 (0x1047381c, 0x103aa41e-0x103aa48a).
        static bool IsTinyMoveDelta(Vector3 delta)
            => Math.Abs(delta.x) < 1e-4f && Math.Abs(delta.y) < 1e-4f && Math.Abs(delta.z) < 1e-4f;

        // Sweep end: 0x103aa696..0x103aa6dc stores paddedDelta = delta + dir·pad per component as float, then
        // 0x103aa8ac..0x103aa8f0 End = centre + paddedDelta (one rounding each), not (centre + delta) + dir·pad.
        internal static Vector3 MoveSweepEnd(Vector3 centre, Vector3 delta, Vector3 dir, float pad)
            => centre + (delta + dir * pad);

        public bool MoveActor(UEInterpreter interp, UEObject actor, Vector3 delta, UERotator newRot,
            bool bTest, bool bIgnorePawns, bool bIgnoreBases, bool bNoFail, MoveCheck callerHit = null)
        {
            // A. Guard: bStatic || !bMovable → refuse (Engine.dll.c:123784;
            // shipped tests bMovable via 0x198&0x20000). The head does NOT check bDeleteMe
            // (disasm 0x103aa3ed/0x103aa3f3): a destroyed actor moves; the hash refuses it on
            // its own (FCollisionHash::RemoveActor/AddActor exit on bDeleteMe —
            // HashRemoveActor/HashAddActor), and events on the corpse are suppressed by
            // ProcessEvent (!IsPendingKill).
            if (MoveActorRefuses(actor)) return false;

            UERotator curRot = UEInterpreter.AsRotator(actor.GetProp(PK.Rotation));
            int standingCount = UEInterpreter.ToInt(actor.GetProp(PK.StandingCount));
            // "A live brush" per shipped, in ALL THREE places that ask (the fast
            // path 123810-123812, the sweep padding 103aa663, the 0.51 extent
            // shrink 103aa8c4): Brush != NULL AND the class chain reaches ABrush
            // AND !bStatic. bStatic is already refused by guard A above, so this
            // reduces to IsBrush in practice; kept in the shipped form.
            bool movingBrush = UEBlocking.IsBrush(actor)
                && !UEInterpreter.ToBool(actor.GetProp(PK.bStatic));

            // Tiny-delta fast path (Engine.dll.c:123791-123819): identical rotation
            // is a no-op; a rotation-only change commits directly UNLESS someone
            // stands on us or we are a live brush — those fall THROUGH to the full
            // path so riders orbit and encroachment stays honest.
            // The fast path does NOT TOUCH the caller's FCheckResult (0x103aa48c-
            // 0x103aa501: returns 1 before copying FCheckResult(1.0) into Hit at
            // 0x103aa530) — neither the callerHit record nor the _lastMove* publication.
            bool tiny = IsTinyMoveDelta(delta);
            if (tiny)
            {
                if (SameRotator(curRot, newRot) || (standingCount == 0 && !movingBrush))
                {
                    if (!SameRotator(curRot, newRot)) actor.Props["Rotation"] = newRot;
                    return true;
                }
            }

            // B. Sweep. The shipped full path normalizes the delta and pads the
            // sweep end by 2 units of direction (Engine.dll.c:123865-123884);
            // hits therefore carry PADDED-span times. Brush identity does NOT
            // skip the sweep — at 103aa663 it only ZEROES that padding for a
            // real non-static ABrush (the constant loaded otherwise is
            // 0x40000000 = 2.0f). A moving mover must sweep like anything else,
            // or it tunnels through the world and through actors on the way and
            // only ever gets the destination encroachment check.
            Vector3 location = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
            (float dist, Vector3 dir) = MoveActorDirection(delta);
            float pad = movingBrush ? 0f : 2f;
            var hits = new List<MoveHit>();
            float time = 1f;             // FCheckResult.Time
            Vector3 blockNormal = Vector3.zero;
            UEObject blockActor = null;  // null = world hit or no block
            int blockNode = -1;
            Vector3 blockLocation = Vector3.zero;
            bool blocked = false;
            bool anyActorConsidered = false;

            bool collideWorld = UEInterpreter.ToBool(actor.GetProp(PK.bCollideWorld));
            bool collideActors = UEInterpreter.ToBool(actor.GetProp(PK.bCollideActors));
            // Shipped sweep gate (103aa69d): (0x1dc & 3) — bCollideActors or
            // bCollideWorld — AND a non-zero delta. Brush-ness is not in it.
            if ((collideActors || collideWorld)
                && (delta.x != 0f || delta.y != 0f || delta.z != 0f))
            {
                // The sweep runs in the primitive's COLLISION BOX space:
                // GetPrimitive (vtable +0xa4) -> GetCollisionBoundingBox
                // (+0x60), then centre = (min+max)/2 and the half-extents.
                // For an ordinary cylinder with CollisionWidth 0 the centre is
                // the Location and the half-extents are (R, R, H), i.e. exactly
                // the old form; for a brush it is the model box through ToWorld.
                (Vector3 boxMin, Vector3 boxMax) = HashBoxOf(actor);
                var centre = new Vector3((boxMin.x + boxMax.x) * 0.5f,
                    (boxMin.y + boxMax.y) * 0.5f, (boxMin.z + boxMax.z) * 0.5f);
                var extent = new Vector3((boxMax.x - boxMin.x) * 0.5f,
                    (boxMax.y - boxMin.y) * 0.5f, (boxMax.z - boxMin.z) * 0.5f);
                // A moving brush sweeps 0.51 tighter on every axis (103aa8c4).
                if (movingBrush)
                    extent = new Vector3(extent.x - 0.51f, extent.y - 0.51f, extent.z - 0.51f);
                Vector3 to = MoveSweepEnd(centre, delta, dir, pad);
                CollectMoveHits(actor, centre, to, extent, collideWorld, collideActors, hits);
                // ASCENDING SORT BY TIME — READ, NOT INFERRED.
                // Shipped ULevel::MultiLineCheck (0x103ac620) collects candidates into an
                // array of 0x2c-byte records capped at 0x40 and calls appQsort with comparator
                // 0x1030385a. That address is ONLY a thunk (`jmp 0x103ad500`); the body at
                // 0x103ad500 compares the float at record offset +0x24 (FCheckResult.Time):
                //     A.Time < B.Time -> -1 ; A.Time > B.Time -> +1 ; else 0.
                // Ghidra did not recover this body (the decompile has a bare label
                // LAB_1030385a), so it is taken from the disasm.
                //
                // Consequence: the candidate collection order does NOT affect the outcome — the
                // sort fully overrides it.
                // THERE IS NO SECOND SORT HERE AND THERE MUST NOT BE. Shipped sorts ONCE —
                // appQsort in ULevel::MultiLineCheck (0x103ac620, Engine.dll.c:256153), and the
                // port does it in the same place: `results.Sort` in MultiLineCheck. A repeated
                // UNSTABLE sort may reorder equal-time records, i.e. change a tie resolution that
                // shipped does not. The list arrives already sorted by MultiLineCheck, so a
                // red mutation must target `results.Sort`, not this site.

                // Result selection walk (Engine.dll.c:123938-124015), run only when
                // bCollideWorld|bBlockActors|bBlockPlayers (0x1dc & 0xe).
                if (collideWorld || UEInterpreter.ToBool(actor.GetProp(PK.bBlockActors))
                    || UEInterpreter.ToBool(actor.GetProp(PK.bBlockPlayers)))
                {
                    foreach (MoveHit hit in hits)
                    {
                        // No special "world" arm: shipped walks ONE list and lets
                        // the inlined IsBlockedBy resolve the sentinel — 123958
                        // tests `hit.Actor != this->Level` and, when it IS the
                        // Level, takes 0x1dc>>1 = bCollideWorld. Hit.Blocking
                        // already carries exactly that.
                        // bIgnorePawns skips Pawn and Decoration hits — only NON-static
                        // ones: `(hit+0x28 & 1) == 0` (bStatic) gates both IsA tests
                        // (123942-123952).
                        if (bIgnorePawns && !UEInterpreter.ToBool(hit.Actor.GetProp(PK.bStatic))
                            && (UEInterpreter.ClassIsA(hit.Actor.Class, "Pawn")
                                || UEInterpreter.ClassIsA(hit.Actor.Class, "Decoration"))) continue;
                        // Skips: our own riders unconditionally; our base chain only
                        // under bIgnoreBases (123954-123957).
                        if (bIgnoreBases && IsBasedOn(actor, hit.Actor)) continue;
                        if (IsBasedOn(hit.Actor, actor)) continue;
                        anyActorConsidered = true;
                        if (hit.Blocking)
                        {
                            time = hit.Fraction; blockNormal = hit.Normal; blockActor = hit.Actor;
                            blockNode = hit.Node; blockLocation = hit.Location; blocked = true; break;
                        }
                    }
                }
            }

            Vector3 rebasedDelta = delta;
            // C. Re-base the hit time to the unpadded span and refuse micro-moves:
            // t' = ((dist+pad)*T - pad)/dist, whole move refused at t' <= 0.0001
            // (Engine.dll.c:124021-124034). bNoFail skips the adjustment entirely
            // (the move commits in full).
            if (time < 1f && !bNoFail)
            {
                bool refused = !MoveActorRebase(pad, dist, time, delta, out time, out rebasedDelta);
                if (refused)
                {
                    actor.Props["_lastMoveNormal"] = blockNormal;
                    actor.Props["_lastMoveFraction"] = 0f;
                    actor.Props["_lastMoveActor"] = blockActor;
                    actor.Props["_lastMoveNode"] = blockNode;
                    actor.Props["_lastMoveHitLocation"] = blockLocation;
                    callerHit?.Set(0f, blockNormal, blockActor, blockNode, blockLocation);
                    return false;
                }
            }

            // Expose the block result so walking physics can read the hit after a
            // committed move (fraction/normal/actor), not only in a dry-run probe.
            actor.Props["_lastMoveNormal"] = blockNormal;
            actor.Props["_lastMoveFraction"] = blocked ? time : 1f;
            actor.Props["_lastMoveActor"] = blockActor;
            actor.Props["_lastMoveNode"] = blocked ? blockNode : -1;
            actor.Props["_lastMoveHitLocation"] = blocked ? blockLocation : Vector3.zero;
            callerHit?.Set(blocked ? time : 1f, blockNormal, blockActor,
                blocked ? blockNode : -1, blocked ? blockLocation : Vector3.zero);
            // bNoFail (param_13) commits the WHOLE delta: the shipped rebase block
            // at 124021 owns BOTH the time re-base and the delta scaling
            // (fStack_24/20/1c), so with bNoFail set the unscaled delta assigned at
            // 124018-124020 survives to the commit even through a blocker.
            Vector3 actuallyMoved = blocked && !bNoFail ? rebasedDelta : delta;

            // D. Based riders move BEFORE the base commits (Engine.dll.c:124035-
            // 124105): translated by the committed delta, orbited around the base by
            // the masked yaw delta, rotated by the raw yaw delta; pawn riders also
            // carry ViewRotation.Yaw. The shipped rider call's trailing flags are
            // ambiguous in the decompile; bIgnoreBases=true matches both the skip
            // semantics above and SE's rider carry.
            if (standingCount > 0 && !bTest)
            {
                int rawYawDelta = newRot.Yaw - curRot.Yaw;
                int maskedYawDelta = (newRot.Yaw & 0xfffc) - (curRot.Yaw & 0xfffc);
                bool rotChanged = !SameRotator(curRot, newRot);
                foreach (UEObject rider in Actors.ToArray())
                {
                    if (rider is null || rider.Destroyed
                        || !ReferenceEquals(rider.GetProp(PK.Base) as UEObject, actor)) continue;
                    Vector3 riderDelta = actuallyMoved;
                    UERotator riderRot = UEInterpreter.AsRotator(rider.GetProp(PK.Rotation));
                    UERotator newRiderRot = riderRot;
                    if (rotChanged)
                    {
                        Vector3 rel = location - UEInterpreter.AsVector3(rider.GetProp(PK.Location));
                        riderDelta += rel - OrbitTransform(rel, maskedYawDelta);
                        newRiderRot = new UERotator(riderRot.Pitch, riderRot.Yaw + rawYawDelta, riderRot.Roll);
                    }
                    MoveActor(interp, rider, riderDelta, newRiderRot,
                        bTest: false, bIgnorePawns: false, bIgnoreBases: true, bNoFail: false);
                    if (rotChanged && UEInterpreter.ClassIsA(rider.Class, "Pawn"))
                    {
                        UERotator view = UEInterpreter.AsRotator(rider.GetProp(PK.ViewRotation));
                        rider.Props["ViewRotation"] = new UERotator(view.Pitch, view.Yaw + rawYawDelta, view.Roll);
                    }
                }
            }

            // E. Non-pawn actors verify encroachment at the destination before the
            // commit; a hit refuses the whole move — riders were already carried,
            // exactly like the shipped order (asm 0x103aaf20-0x103aafcf: riders,
            // hash-remove, CheckEncroachment(loc+delta, newRot, bTouchActors=0),
            // blocked -> return 0 without committing).
            // The test location and the commit read the LIVE Location, each at its own point (Engine.dll.c:124112,
            // 0x103aaf38; commit 0x103aafd2..0x103ab005 `fld delta; fadd [ebx+0xfc]; fstp` AFTER CheckEncroachment,
            // whose EncroachingOn script event may move the actor): a rider carry or a handler may have moved it.
            HashRemoveActor(actor);
            if (!bTest && !bNoFail && !UEInterpreter.ClassIsA(actor.Class, "Pawn")
                && CheckEncroachment(interp, actor, UEInterpreter.AsVector3(actor.GetProp(PK.Location)) + actuallyMoved, newRot, bTouchActors: false))
            {
                // Shipped 124124-124136: hash re-add and `return 0` — Hit keeps the
                // sweep result (time, normal, actor) written above.
                HashAddActor(actor);
                return false;
            }

            // F. Commit: Location AND Rotation only (Engine.dll.c:124139-124144,
            // 0x103aafd2..): OldLocation (+0x114) is not written anywhere in the
            // body 0x103aa390..0x103ab231 — physics writes it at its own start.
            actor.Props["Location"] = UEInterpreter.AsVector3(actor.GetProp(PK.Location)) + actuallyMoved;
            actor.Props["Rotation"] = newRot;
            HashAddActor(actor);

            // G+H. The whole notification block (Bump, Touch, UnTouch) is gated on
            // bTest (param_10) — shipped commits the pose above regardless and only
            // skips the events here (124145-124189). The zone update below is NOT
            // gated: shipped forwards the flag into it (vtable +0xb0, param_10).
            if (!bTest)
            {
                // Bump both parties on an actor block, hit actor first
                // (Engine.dll.c:124148-124166) — pair dispatch AND the shipped gates
                // (world sentinel + IsBasedOn).
                DispatchBump(interp, actor, blockActor);

                // Touch pass (Engine.dll.c:124169-124181), gated the shipped way:
                // run when an actor candidate was actually considered OR the mover
                // does not fully block (either block bit clear). Raw padded-span hit
                // times compare against the re-based Result.Time — the shipped quirk.
                if (anyActorConsidered
                    || !UEInterpreter.ToBool(actor.GetProp(PK.bBlockActors))
                    || !UEInterpreter.ToBool(actor.GetProp(PK.bBlockPlayers)))
                {
                    foreach (MoveHit hit in hits)
                    {
                        if (blocked && hit.Fraction >= time) break;
                        if (hit.Actor is not null && !hit.Blocking
                            && !IsBasedOn(hit.Actor, actor)
                            && (!bIgnoreBases || !IsBasedOn(actor, hit.Actor)))
                            TouchActor(interp, actor, hit.Actor);
                    }
                }
                // UnTouch anything no longer overlapping (Engine.dll.c:124182-124188).
                foreach (UEObject t in actor.TouchList())
                    if (t is not null && !IsOverlapping(actor, t))
                        UnTouchActor(interp, actor, t);
            }

            // The zone update takes bTest AS AN ARGUMENT (124190: vtable +0xb0 is
            // called as (this, param_10, 0)) — SetActorZone gates ActorLeaving /
            // ZoneChange / ActorEntered / FootZoneChange / HeadZoneChange behind it,
            // so dropping the argument let a TEST move fire real zone lifecycle
            // events.
            // SetActorZone (vtable +0xb0, FUN_103acdd0) has no location argument: it reads Actor+0xfc at the call
            // (Engine.dll.c:256385-256387) — after Bump/Touch/UnTouch, which may have relocated the actor.
            SetActorZone(interp, actor, UEInterpreter.AsVector3(actor.GetProp(PK.Location)), bTest);
            // Shipped return (124194-124199), with NO bTest branch of its own:
            //     if (Result.Time <= 0.0) return 0; return 1;
            // Result.Time is the re-based hit time when blocked and 1.0 otherwise,
            // so "moved at all" — a move blocked PART WAY still returns true.
            return time > 0f;
        }

        // Native MoveSmooth (3969): execMoveSmooth (Engine.dll.c:62637) clears
        // bJustTeleported and returns the moveSmooth result. The body is Core's.
        public bool ExecMoveSmooth(UEInterpreter interp, UEObject actor, Vector3 delta)
        {
            actor.Props["bJustTeleported"] = false;
            return MoveSmooth(interp, actor, delta);
        }

        // Shipped AActor::moveSmooth (Engine.dll 0x103e4c30): one move at the
        // current rotation; on a block, slide along the wall by the remaining
        // fraction (never backwards), dispatching the HitWall(Normal, Hit.Actor)
        // event BEFORE the slide; a second block folds the slide with TwoWallAdjust and
        // moves once more. Returns the FIRST move's result (moved at all), not
        // the slide's — the shipped encroachment push and the MoveSmooth native
        // both consume that value.
        public bool MoveSmooth(UEInterpreter interp, UEObject actor, Vector3 delta)
        {
            // Rotation is re-read for EACH move: shipped keeps the field ADDRESS in edi
            // (`lea edi, [esi+0x108]`, 0x103e4c83) and copies three words before each
            // MoveActor (0x103e4c92, 0x103e4e1c, 0x103e4e7b). HitWall between the first and
            // second move may rotate the actor — the slide carries the new rotation, not a
            // snapshot from before the hit.
            // The body's FCheckResult is FCheckResult(1.0) once (Engine.dll.c:
            // 14563-14568); all three moves go into it.
            var smoothHit = new MoveCheck();
            bool moved = MoveActor(interp, actor, delta, UEInterpreter.AsRotator(actor.GetProp(PK.Rotation)),
                bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false, smoothHit);
            float fraction = smoothHit.Time;
            if (fraction < 1f)
            {
                Vector3 normal = smoothHit.Normal;
                float dot = normal.x * delta.x + delta.y * normal.y + delta.z * normal.z;
                Vector3 slide = (delta - normal * dot) * (1f - fraction);
                if (delta.x * slide.x + delta.z * slide.z + delta.y * slide.y >= 0f)
                {
                    Vector3 desiredDir = delta.normalized; // FVector::SafeNormal
                    // Through UELifecycle, whose Guard now carries the frontend
                    // policy (the slide below must still run either way).
                    // Wall = Hit.Actor (disasm 0x103e4dcc/0x103e4dde: [ebp-0x80] —
                    // FCheckResult.Actor), for BSP — the level sentinel.
                    UELifecycle.CallEvent(interp, actor, "HitWall", normal, smoothHit.Actor);
                    MoveActor(interp, actor, slide, UEInterpreter.AsRotator(actor.GetProp(PK.Rotation)),
                        bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false, smoothHit);
                    if (smoothHit.Time < 1f)
                    {
                        Vector3 n2 = smoothHit.Normal;
                        TwoWallAdjust(desiredDir, ref slide, n2, normal, smoothHit.Time);
                        MoveActor(interp, actor, slide, UEInterpreter.AsRotator(actor.GetProp(PK.Rotation)),
                            bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false, smoothHit);
                    }
                }
            }
            return moved;
        }

        // AActor::IsOverlapping (Engine thunk 0x103029d7 -> 0x10379d90): cylinder-cylinder overlap; a brush on either
        // side, or B == A.Level (+0x7c), never overlaps.
        public static bool IsOverlapping(UEObject a, UEObject b)
        {
            // Shipped AActor::IsOverlapping (Engine.dll.c:71052-71067) bails on a
            // brush side only when BOTH tests hold: Brush != NULL and the class
            // chain reaches ABrush. A non-ABrush actor that merely carries a
            // Brush reference must continue into the cylinder math — a
            // pointer-only test produces false UnTouch/encroachment results.
            if (UEBlocking.IsBrush(a) || UEBlocking.IsBrush(b)) return false;
            if (ReferenceEquals(b, a.GetProp(PK.Level))) return false;
            return CylindersOverlap(UEInterpreter.AsVector3(a.GetProp(PK.Location)),
                UEInterpreter.AsFloat(a.GetProp(PK.CollisionRadius)), UEInterpreter.AsFloat(a.GetProp(PK.CollisionHeight)),
                UEInterpreter.AsVector3(b.GetProp(PK.Location)),
                UEInterpreter.AsFloat(b.GetProp(PK.CollisionRadius)), UEInterpreter.AsFloat(b.GetProp(PK.CollisionHeight)));
        }

        // The cylinder test of the body (disasm 0x10379e04..0x10379e70, PC64): dx = A.x − B.x, dy, r = B.R + A.R, all in
        // st0; overlap when dy·dy + dx·dx < r·r (fcompp, test ah,0x41) and dz·dz < (B.H + A.H)². Every operation rounds
        // at 64 bits (X87Ext); a float model disagrees near the boundary (corpus is_overlapping_matches_engine).
        public static bool CylindersOverlap(Vector3 la, float ra, float ha, Vector3 lb, float rb, float hb)
        {
            static X87Ext Sub(float x, float y) => X87Ext.Round64(X87Ext.From(x) - X87Ext.From(y));
            static X87Ext Sq(X87Ext v) => X87Ext.Round64(v * v);
            X87Ext dx = Sub(la.x, lb.x), dy = Sub(la.y, lb.y);
            X87Ext r = X87Ext.Round64(X87Ext.From(rb) + X87Ext.From(ra));
            if (X87Ext.Round64(Sq(dy) + Sq(dx)).CompareTo(Sq(r)) >= 0) return false;
            X87Ext dz = Sub(la.z, lb.z), h = X87Ext.Round64(X87Ext.From(hb) + X87Ext.From(ha));
            return Sq(dz).CompareTo(Sq(h)) < 0;
        }

        // ── SingleLineCheck ────────────────────────────────────────────────
        // Plain struct, not a positional record struct: those are C# 10 and
        // Core must compile on the netstandard2.1 + C#9 frontend profile.
        public readonly struct LineCheckHit
        {
            public bool Blocked { get; }
            public float Time { get; }
            public Vector3 Location { get; }
            public Vector3 Normal { get; }
            public UEObject Actor { get; }
            public int Node { get; }

            public LineCheckHit(bool Blocked, float Time, Vector3 Location,
                Vector3 Normal, UEObject Actor, int Node)
            {
                this.Blocked = Blocked;
                this.Time = Time;
                this.Location = Location;
                this.Normal = Normal;
                this.Actor = Actor;
                this.Node = Node;
            }

            public static LineCheckHit None
                => new LineCheckHit(false, 1f, Vector3.zero, Vector3.zero, null, -1);

            public static implicit operator LineCheckHit(
                (bool Blocked, float Time, Vector3 Normal, UEObject Actor) t)
                => new LineCheckHit(t.Blocked, t.Time, Vector3.zero, t.Normal, t.Actor, -1);

            public void Deconstruct(out bool blocked, out float time, out Vector3 normal, out UEObject actor)
            {
                blocked = Blocked; time = Time; normal = Normal; actor = Actor;
            }
        }

        // ULevel::SingleLineCheck (Engine.dll.c:97953): the closest hit of the
        // world+actor extent trace (MultiLineCheck, vtable 0xcc), skipping the
        // owner chain of `ownerToSkip`. Returns the hit actor (LevelInfo for a
        // world/BSP hit, per the shipped Actors[0] sentinel), or null on a
        // clean miss. World uses the validated BSP sweep; actors use the
        // validated shipped cylinder trace. Brush actors run their own
        // coords-form UModel::LineCheck — a separate primitive, never the
        // actor cylinder.
        public UEObject SingleLineCheckActor(UEInterpreter interp, UEObject self,
            UEObject ownerToSkip, Vector3 start, Vector3 end, Vector3 extent, int traceFlags)
            => SingleLineCheck(interp, self, ownerToSkip, start, end, extent, traceFlags).Actor;

        // DIAGNOSTIC CAPTURE OF TRACE QUERIES. Needed by exactly one consumer — the
        // comparison with the shipped body under unicorn (`validate_findstairrotation.py`):
        // the oracle must be fed THE SAME trace answers and the port must present the query
        // SEQUENCE, otherwise only the result would be compared and a wrongly built probe
        // would pass unnoticed (check the order, not just the return). Always null in production.
        public Action<Vector3, Vector3, Vector3, int, float> LineCheckProbe;
        // SwARc Engine measurement tap (tools/swarc_oracle/ENGINE_MEASURE.md, runtime_poc/HeadlessEngineJudgeDump.cs):
        // the query and the port's answer of every SingleLineCheck. Observes only; null in production.
        public static Action<UELevelRuntime, UEObject, UEObject, Vector3, Vector3, Vector3, int, LineCheckHit> SingleLineCheckTap;

        public LineCheckHit SingleLineCheck(UEInterpreter interp, UEObject self,
            UEObject ownerToSkip, Vector3 start, Vector3 end, Vector3 extent, int traceFlags)
        {
            LineCheckHit probed = SingleLineCheckBody(interp, self, ownerToSkip,
                start, end, extent, traceFlags);
            LineCheckProbe?.Invoke(start, end, extent, traceFlags, probed.Time);
            SingleLineCheckTap?.Invoke(this, self, ownerToSkip, start, end, extent, traceFlags, probed);
            return probed;
        }

        private LineCheckHit SingleLineCheckBody(UEInterpreter interp, UEObject self,
            UEObject ownerToSkip, Vector3 start, Vector3 end, Vector3 extent, int traceFlags)
        {
            // Shipped ETraceFlags: Pawns=1, Movers=2, Level=4, ZoneChanges=8,
            // Others=16, OnlyProjActor=32. The physFalling air-control probe
            // uses 6 and MUST ignore Pawns.
            //
            // The query itself is MultiLineCheck (97999): checkActors is
            // `flags & 0x17`, and the Level bit decides whether the world phase
            // runs at all. The world sentinel is Actors[0] — the object
            // ../Inc/UnLevel.h:0xf8 asserts IsA(ALevelInfo) and that MoveActor
            // compares identity against — NOT interp.GetLevelInfo(), which
            // lazily fabricates a stand-in LevelInfo when none was registered
            // and so is a DIFFERENT object once a map is loaded.
            UEObject levelInfo = LevelSentinel ?? interp.GetLevelInfo();
            List<MoveHit> hits = MultiLineCheck(start, end, extent,
                (traceFlags & 0x17) != 0, (traceFlags & 4) != 0 ? levelInfo : null);

            // The list is time-sorted, so the first hit that passes the filter
            // IS the closest one (98046-98104).
            foreach (MoveHit hit in hits)
            {
                UEObject candidate = hit.Actor;
                if (candidate is null || candidate.Destroyed || ReferenceEquals(candidate, self)) continue;
                if (IsInOwnerChain(ownerToSkip, candidate)) continue;
                // Shipped filter: the LevelInfo(4)/Mover(2)/ZoneInfo(8) CLASS
                // branches run first, and everything left is split by the inline
                // IsBlockedBy test against the SOURCE actor — blocked → bit 1,
                // not blocked → bit 0x10 (LAB_103ac3e2). Bit 1 is therefore
                // "actors that block me", NOT "Pawns": a blocking Decoration is
                // bit 1 and a non-blocking Pawn is 0x10.
                bool selected;
                if (UEInterpreter.ClassIsA(candidate.Class, "LevelInfo"))
                    selected = (traceFlags & 4) != 0;
                else if (UEInterpreter.ClassIsA(candidate.Class, "Mover"))
                    selected = (traceFlags & 2) != 0;
                else if (UEInterpreter.ClassIsA(candidate.Class, "ZoneInfo"))
                    selected = (traceFlags & 8) != 0;
                else if (self is null)
                    selected = (traceFlags & 16) != 0;   // no source → no blocking test
                else
                    selected = UEBlocking.IsBlockedBy(self, candidate, levelInfo)
                        ? (traceFlags & 1) != 0
                        : (traceFlags & 16) != 0;
                if (!selected) continue;
                return new LineCheckHit(true, hit.Fraction, hit.Location, hit.Normal,
                    candidate, hit.Node);
            }
            return LineCheckHit.None;
        }

        // AActor::FindBase (Engine.dll.c:21086): floor probe (Location → Z−8,
        // the actor's cylinder extent, flags=7) via ULevel::SingleLineCheck,
        // then SetBase to whatever it lands on. A world/BSP floor bases the
        // actor on the LevelInfo.
        public void FindBase(UEInterpreter interp, UEObject actor)
        {
            Vector3 loc = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
            // Extent — GetCylinderExtent(this) (Engine.dll.c:21141, call 0x103e5064).
            Vector3 extent = GetCylinderExtent(actor);
            var end = new Vector3(loc.x, loc.y, loc.z - 8f);
            UEObject found = SingleLineCheckActor(interp, actor, actor, loc, end, extent,
                traceFlags: 7);
            Log($"find_base actor={actor.GetProp(PK.Name)} class={actor.Class?.Name} "
                + $"location={loc} base={found?.GetProp(PK.Name) ?? (object)"None"}");
            if (!ReferenceEquals(actor.GetProp(PK.Base) as UEObject, found))
                SetBase(interp, actor, found, notify: true);
        }

        // ── FindSpot / AdjustSpot ──────────────────────────────────────────
        // ULevel::AdjustSpot probe (body 0x103a9570): a zero-extent
        // SingleLineCheck(Hit, NULL, End=try, Start=spot, TraceFlags=6) — flags
        // 6 accepts ONLY LevelInfo (world) and Mover-class hits; pawns and
        // other actors are filtered out, and Source=NULL means no owner-chain
        // skipping. Returns the closest accepted hit or null.
        //
        // This is the SHARED SingleLineCheck -> MultiLineCheck (FUN_103ac620), with no
        // assembly of its own: the world trace sees mover brush fragments (tracker,
        // UEMovingBrushTracker.cs), the actor ray is shortened behind the world hit, and the
        // coincident-face branch (0x103ac95e-0x103aca31) puts the mover hit 2 units closer
        // than the world one — as in the original (Lev_Tut3b, Lev3_Dungeon/DungeonB: the shift
        // at the grate lands 0.86-2 units closer).
        public (float Time, Vector3 Normal)? AdjustSpotProbe(Vector3 start, Vector3 end)
        {
            if (LevelSentinel is null)
                throw new InvalidOperationException("AdjustSpot: the level has no LevelInfo (Actors[0]) — the MultiLineCheck world phase has nothing to go with");
            LineCheckHit h = SingleLineCheck(null, null, null, start, end, Vector3.zero, traceFlags: 6);
            return h.Blocked ? (h.Time, h.Normal) : null;
        }

        // ULevel::AdjustSpot (vtable 0xb8): probe spot→try, push the spot out
        // along the hit normal by Adjust*(1.05f-Time) when Time < 1.0f.
        public void AdjustSpot(ref Vector3 spot, Vector3 tryPoint, float adjust)
        {
            (float Time, Vector3 Normal)? hit = AdjustSpotProbe(spot, tryPoint);
            if (hit is null || !(hit.Value.Time < 1f)) return;
            Vector3 before = spot;
            spot = FindSpotMath.AdjustSpotPush(spot, hit.Value.Time, hit.Value.Normal, adjust);
            Log($"find_spot_adjust from={before} to={spot} time={hit.Value.Time} "
                + $"normal={hit.Value.Normal} adjust={adjust}");
        }

        // ULevel::FindSpot (vtable 0xb4, body 0x103a9690; oracle
        // se_harness/bsp_diff/validate_findspot.py).
        // Both shipped call sites pass bCheckActors=0 (SpawnActor
        // Engine.dll.c:20104 with bTryCurrent=1, FarMoveActor 159193 with
        // bTryCurrent=0), so the point probes test the world BSP only, while
        // the AdjustSpot nudge rays also see mover brushes — a shipped
        // asymmetry, kept as-is.
        // Hence FCollisionHash::ActorPointCheck (0x103650b0, hash slot +0x14) is unreachable:
        // only ULevel::MultiPointCheck calls it with bActors != 0 (0x103abf9d..0x103abfdf),
        // MultiPointCheck is called only by SinglePointCheck (0x103abde4, bActors passed
        // through), and that only by FindSpot (0x103a97b5/0x103a9870/0x103a9a62/0x103a9c03,
        // bActors = FindSpot's bCheckActors). Disasm of all game DLLs: calls of ULevel slots
        // +0xb4/+0xc0/+0xc8 on the level are exactly SpawnActor (push 0 — 0x103a67e4) and
        // FarMoveActor (push 0 — 0x103a9f2b); only Editor.dll imports the exports. So the
        // port has no ActorPointCheck and needs none.
        public bool FindSpot(Vector3 extent, ref Vector3 location, bool tryCurrent)
            => FindSpotMath.FindSpot(extent, ref location, tryCurrent,
                p => !Bsp.PointCheck(p, extent, null).Hit,
                AdjustSpot);
    }
}
