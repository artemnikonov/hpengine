// Loading a level into the runtime: the loaded-level lifecycle (InitExecution, zone pass),
// LoadWorld/ResetWorld, world geometry and brush sources. Fields live in UELevelRuntime.cs.
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
            // of word AActor+0x14c, shipped layout). This guard is why PreBeginPlay
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
        // 0x1039df33; field Actor+0x14c bit 0x10000), the same as the neighbouring
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
        // level (a process-long runtime) must be able to ASK
        // rather than remember, or a scene change silently keeps the old world.
        public string LoadedMapPath => _brushMapPath;

        internal void NoteBoundWorldLevelInfo(UEInterpreter interp, UEObject level)
        {
            _boundInterp = interp;
            _boundLevel = level;
        }

        // For ownership checks: what this world considers itself obliged to release on teardown.
        public UEObject BoundWorldLevelInfo => _boundLevel;
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
                // INSIDE the try: the rollback must cover the one failure it exists for.
                // LoadWorldGeometry publishes the
                // BSP and then reads the brush bounds, so a failure between those
                // two would leave a world with geometry and nothing else.
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
            // the previous level. Headless is one level per process, but a
            // long-lived runtime loads levels repeatedly,
            // so this is reachable there and must not be silent.
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
    }
}
