// CORE WORLD BOOT AND TICK FOR THE FRONTEND.
//
// The single boot sequence: SmokeProgram.Main (the harness) goes through it too, with
// BootStops stop points (D-18):
// class registry -> interpreter -> AppInit -> packages -> native table -> BSP and
// ReachSpecs -> actor graph -> clock, brushes, collision hash -> Entry -> BootstrapPlayWorld. Tick is
// UEGameEngineNatives.TickWorld. HeadlessHost (runtime_poc) is a thin wrapper over this class;
// the own engine (openhp1) calls it directly.
// Frontend natives here are headless (canvas/sound without a device): enough for world rendering;
// a real player needs its own set of frontend natives.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HP1.Core;

namespace HP1.UEScript
{
    public sealed class UEGameEngine
    {
        // Native table factory: the harness (runtime_poc) supplies its own subclass with probes
        // and comparison journals; without a factory the production Core table is used.
        public delegate UEGameEngineNatives NativesFactory(uint seed, List<string> journal,
            string defaultIni, Hp1SkeletalMeshCatalog skeletalMeshes);

        readonly UEGameEngineNatives _n;
        readonly List<string> _journal;
        int _tick;
        public UEInterpreter Interp { get; }
        public UELevelRuntime Runtime => _n.Runtime;
        public UEGameEngineNatives Natives => _n;
        public HeadlessBspModel Bsp { get; }
        public string GameRoot { get; }
        public string MapName { get; }

        UEGameEngine(UEGameEngineNatives n, List<string> journal, UEInterpreter interp, HeadlessBspModel bsp, string root, string map)
        { _n = n; _journal = journal; Interp = interp; Bsp = bsp; GameRoot = root; MapName = map; }

        // selectHardwareRenderDevice — player's first run (UEScriptedActor calls
        // SelectHardwareRenderDeviceIfUnset before world boot): no device selected ->
        // D3DDrv (HighDetailActors=True). Set BEFORE BootstrapPlayWorld: boot-time bHighDetailMode
        // and ULevel::DetailChange depend on it. The harness (pins) does not request it.
        // BOOT STOP POINTS FOR THE VALIDATION HARNESS (D-18). There is ONE boot sequence — here;
        // the harness (runtime_poc SmokeProgram.Main) keeps no copy of its own, it configures and
        // stops this one at the declared points: gesture census — after packages, level dump —
        // after the actor graph, SelfTest — after the collision hash (before Entry and
        // BootstrapPlayWorld). false stops the boot, Boot returns null.
        // Boot writes the boot journal lines itself; the game sets no stop points.
        public sealed class BootStops
        {
            public Func<UEInterpreter, bool> PackagesLoaded;
            public Action<UEGameEngineNatives> NativesCreated;
            public Func<List<UEActorGraphLoader.ActorEntry>, bool> ActorGraphLoaded;
            public Func<bool> CollisionReady;
        }

        // forceSurfaceLightmapsInFrame — the game always enables per-frame surface lightmaps
        // and publishes the world static before the post-load frame (RunOwedLoadFrame). The harness passes
        // false: its own HP1_SURF_LM switch (runtime_poc) decides lightmaps and the first tick
        // (UEGameEngineNatives.TickWorld) publishes the static — its pins and surflm journals were taken that way.
        public static UEGameEngine Boot(string gameRoot, string mapName, NativesFactory createNatives = null,
            bool selectHardwareRenderDevice = false, UEConfig.IStore configStore = null,
            bool forceSurfaceLightmapsInFrame = true, BootStops stops = null)
        {
            string systemDir = Path.Combine(gameRoot, "System");
            UEClassRegistry.Shared = new UEClassRegistry();
            var interp = new UEInterpreter();
            UEObject.DefaultInterp = interp;
            interp.ThrowOnMissingScript = true;
            UELevelRuntime.AppInit(systemDir, configStore);
            foreach (string name in CorePackages)
            {
                string path = UEInstallPath.File(Path.Combine(systemDir, name + ".u"));
                if (path is not null) UEClassRegistry.Shared.GetOrLoadPackage(path);
            }
            if (stops?.PackagesLoaded is { } afterPackages && !afterPackages(interp)) return null;
            var journal = new List<string>();
            var n = (createNatives ?? ((s, j, ini, meshes) => new UEGameEngineNatives(s, j, ini, meshes)))(
                1, journal, UELevelRuntime.DefaultIniPath(systemDir),
                new Hp1SkeletalMeshCatalog(systemDir));
            // Per-frame surface lightmaps in the engine frame — the same switch as the headless measurement.
            if (forceSurfaceLightmapsInFrame) n.EnableSurfaceLightmapsInFrame();
            if (createNatives is null) n.DisableJournal();   // production table: no journal reader (harness factories keep it)
            interp.Natives = n;
            n.SelectHardwareRenderDevice = selectHardwareRenderDevice;
            n.SetGameRoot(gameRoot);
            stops?.NativesCreated?.Invoke(n);
            string mapPath = Path.Combine(gameRoot, "Maps", mapName + ".unr");
            mapPath = UEInstallPath.File(mapPath) ?? mapPath;
            HeadlessBspModel bsp = HeadlessBspModel.Load(mapPath);
            journal.Add($"bsp nodes={bsp.NodeCount} leafHullValues={bsp.LeafHullValueCount}");
            n.SetBsp(bsp);
            n.SetReachSpecs(HeadlessBspModel.LoadReachSpecs(mapPath));
            var actors = LoadActorGraph(mapPath, interp);
            journal.Add($"graph actors={actors.Count}");
            if (stops?.ActorGraphLoaded is { } afterGraph && !afterGraph(actors)) return null;
            n.InitializeLevelClock(interp);
            n.SetBrushSource(mapPath, HeadlessBspModel.LoadModelBounds(mapPath));
            n.InitCollisionHash();   // ULevel::SetActorCollision(1) — before events and spawns
            if (stops?.CollisionReady is { } afterCollision && !afterCollision()) return null;
            n.Runtime.LifecycleCandidatesHook = UEGameEngineNatives.NativeCandidates;
            n.InstallSaveGameHandlers(interp);
            n.EnsureEntryWorld(interp);
            // The game decodes first-use data at load, behind the load screen (UELevelRuntime.LoadWarm.cs); the harness
            // (forceSurfaceLightmapsInFrame: false) keeps the lazy order its pinned logs were taken with.
            n.Runtime.WarmCachesOnLoad = forceSurfaceLightmapsInFrame;
            n.BootstrapPlayWorld(interp);
            if (forceSurfaceLightmapsInFrame && n.Runtime.LatestWorldStatic is null) n.Runtime.PublishWorldStaticForDiagnostics();
            return new UEGameEngine(n, journal, interp, bsp, gameRoot, mapName);
        }

        // The first boot's frame — before the start level's first tick, as in UEGameEngineNatives.TickWorld
        // (UELevelRuntime.EngineFrameOwedAfterLoad). Separate so the frontend can take its
        // output (static lightmap tiles) before the tick frame replaces it.
        public void RunOwedLoadFrame()
        {
            if (Runtime.EngineFrameOwedAfterLoad && Runtime.FrameProducer is not null)
                Runtime.RunEngineFrame(Interp, Runtime.NextInsideTickFrameNumber(), "load", 0f);
        }

        // Travel probe: at tick `tick` (this host's numbering) Core performs travel to `url` — by the
        // same route as the scripted ClientTravel (UEGameEngineNatives.SetTravelProbe).
        public void SetTravelProbe(int tick, string url, bool items = true) => _n.SetTravelProbe(tick, url, items);
        // Death probe (snapshot --kill, HeadlessSmoke HP1_KILL_AT): the player's baseHarry.KillHarry(true) between ticks,
        // the script's own death route (stateDead -> LoadSelectedSlot). No console route exists (KillHarry is not exec).
        // Returns the killed pawn's name, or null without a player pawn.
        public string KillPlayerProbe()
        {
            if (Runtime.PlayerPawn is not UEObject victim) return null;
            Interp.Invoke(victim, Interp.FindEventFunction(victim, "KillHarry")
                ?? throw new System.IO.InvalidDataException("KillPlayerProbe: the player has no KillHarry"), true);
            return victim.GetProp(PK.Name)?.ToString();
        }
        // Snapshot probe (the live probe's `causeevent <Tag>` exec, tools/origprobe hp1drawlog EXEC): Trigger(player, player)
        // on every actor whose Tag is the name, in actor-list order. Not a console command of the port (UEConsoleCommand).
        public int CauseEventProbe(string tag)
        {
            if (Runtime.PlayerPawn is not UEObject p) return 0;
            int n = 0;
            foreach (UEObject a in new List<UEObject>(Runtime.Actors))
            {
                if (a is null || a.Destroyed || !string.Equals(a.GetProp(PK.Tag)?.ToString(), tag, StringComparison.OrdinalIgnoreCase)) continue;
                Interp.Invoke(a, Interp.FindEventFunction(a, "Trigger")
                    ?? throw new System.IO.InvalidDataException($"CauseEventProbe: {a.GetProp(PK.Name)} has no Trigger"), p, p);
                n++;
            }
            return n;
        }
        // A host with real frame pacing (the windowed game): the original's load-time delta (UELevelRuntime.EmulateLoadStutter).
        public void SetRealFramePacing(bool on) => _n.Runtime.EmulateLoadStutter = on;

        public void Tick(int count, float deltaSeconds)
        {
            _n.TickWorld(Interp, count, deltaSeconds, streamJournal: false, firstTick: _tick);
            _tick += count;
            _journal.Clear();   // the comparison journal is not needed for play, and it grows every tick
        }

        public WorldFrame Frame(string label) => _n.BuildWorldFrame(_tick, label);

        // Script packages loaded by a game run.
        internal static readonly string[] CorePackages =
        {
            "Core", "Engine", "HPParticle", "UWindow", "HPBase", "HarryPotter",
            "HPMenu", "HProps", "HPDialog", "Tut1", "Tut3", "Hub2"
        };

        // Core-owned (UEActorGraphLoader.Load). The placed-actor graph IS actor
        // identity — export order, ExportIndex, the mover's Brush UModelRef, every
        // instance tag and inter-actor reference — so it belongs to Core, not to
        // whichever frontend happens to load a level. Only this runner's own wiring
        // of the result stays here.
        // Binds the graph to the native table's runtime.
        public static List<UEActorGraphLoader.ActorEntry> LoadActorGraph(string mapPath, UEInterpreter interp)
        {
            // The level owner is the same UELevelRuntime this runner later ticks and
            // tears down; otherwise nobody would bind it and teardown would release nothing.
            var result = UEActorGraphLoader.Load(mapPath, interp,
                (interp.Natives as UEGameEngineNatives)?.Runtime);
            if (interp.Natives is UEGameEngineNatives headless)
            {
                headless.SetActors(result.Select(x => x.Runtime));
                // Review #12 P1: the Core interpreter owns the authoritative
                // ULevel.Actors list (the SAME live list — later spawns appear).
                interp.LevelActors = headless.ActorsLive;
                // UGameEngine::LoadMap: empty LevelEnterText = URL.Map (the same step as in
                // UELevelRuntime.LoadWorld; the runner's first map is loaded here).
                headless.Runtime?.ApplyLoadMapLevelEnterText(Path.GetFileName(mapPath));
            }
            return result;
        }
    }
}
