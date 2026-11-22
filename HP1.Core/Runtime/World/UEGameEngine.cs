// CORE WORLD BOOT AND TICK FOR THE FRONTEND.
//
// The single boot sequence; test harnesses go through it too, with
// BootStops stop points:
// class registry -> interpreter -> AppInit -> packages -> native table -> BSP and
// ReachSpecs -> actor graph -> clock, brushes, collision hash -> Entry -> BootstrapPlayWorld. Tick is
// UEGameEngineNatives.TickWorld. The own engine (hpengine) calls this class directly.
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
        // Native table factory: a test harness supplies its own subclass with probes
        // and comparison journals; without a factory the production Core table is used.
        public delegate UEGameEngineNatives NativesFactory(uint seed, List<string> journal,
            string defaultIni, Hp1SkeletalMeshCatalog skeletalMeshes);

        readonly UEGameEngineNatives _n;
        readonly List<string> _journal;
        int _tick;
        public UEInterpreter Interp { get; }
        public UELevelRuntime Runtime => _n.Runtime;
        public UEGameEngineNatives Natives => _n;
        // HP2 Game.exe's command line as the engine reads it (the launcher passes "PrivetDr.unr  -SAVESLOT=n" or
        // " -LOADGAME=0  -SAVESLOT=n"): the URL is the first token unless it starts with '-' (none: [URL] LocalMap);
        // SAVESLOT= is appInit's Parse (Core [HP2 0x101571a2..]) and LOADGAME= UGameEngine::Init's (0x103a15cf..0x103a17f8):
        // Parse(appCmdLine, "KEY=", int) = the key found case-insensitively anywhere, then appAtoi of what follows.
        public static (string Map, int? SaveSlot, int? LoadGame) ParseHp2CommandLine(string cmd)
        {
            cmd ??= "";
            string first = cmd.TrimStart(' ', '\t').Split(' ', '\t')[0];
            string map = first.Length == 0 || first[0] == '-' ? null : Path.GetFileNameWithoutExtension(first);
            static int? Int(string c, string key)
                => UEValue.AppStrfind(c, key) is int i && i >= 0 ? UEValue.LeadingAtoi(c.Substring(i + key.Length)) : null;
            return (map, Int(cmd, "SAVESLOT="), Int(cmd, "LOADGAME="));
        }

        // UGameEngine::Init's LOADGAME=n [HP2 0x103a15cf..0x103a17f8], after the first map and the player: Exec LOADGAME's
        // ServerTravel to the slot's save, without LoadPersistentActorCache (appInit restored the cache already).
        public void LoadGameAtInit(int slot) => _n.LoadGameTravel(Interp, slot);

        // The frontend calls it once before the process ends (appPreExit's game part: HP2 clears the PA cache).
        public void PreExit() => _n.PreExit();
        public HeadlessBspModel Bsp { get; }
        public string GameRoot { get; }
        public string MapName { get; }

        UEGameEngine(UEGameEngineNatives n, List<string> journal, UEInterpreter interp, HeadlessBspModel bsp, string root, string map)
        { _n = n; _journal = journal; Interp = interp; Bsp = bsp; GameRoot = root; MapName = map; }

        // selectHardwareRenderDevice — player's first run (UEScriptedActor calls
        // SelectHardwareRenderDeviceIfUnset before world boot): no device selected ->
        // D3DDrv (HighDetailActors=True). Set BEFORE BootstrapPlayWorld: boot-time bHighDetailMode
        // and ULevel::DetailChange depend on it. A test harness does not request it.
        // BOOT STOP POINTS FOR VALIDATION HARNESSES. There is ONE boot sequence — here;
        // a harness keeps no copy of its own, it configures and stops this one at the declared
        // points: after packages, after the actor graph, after the collision hash (before Entry
        // and BootstrapPlayWorld). false stops the boot, Boot returns null.
        // Boot writes the boot journal lines itself; the game sets no stop points.
        public sealed class BootStops
        {
            public Func<UEInterpreter, bool> PackagesLoaded;
            public Action<UEGameEngineNatives> NativesCreated;
            public Func<List<UEActorGraphLoader.ActorEntry>, bool> ActorGraphLoaded;
            public Func<bool> CollisionReady;
        }

        // Lines for the game's log: the port's script overrides whose function the loaded scripts (a mod's) do not have,
        // so the override does not apply there (UEPortScriptOverrides.AbsentTargets), and those whose function in the
        // install's scripts is not the shipped one, so the override hides what a mod did to it (ChangedTargets).
        // Text only, no behaviour; a package that cannot be read is named, not hashed.
        public static List<string> ScriptOverrideWarnings()
        {
            var unhashed = new List<string>();
            return HP1.UEScript.UEPortScriptOverrides.AbsentTargets()
                .Select(t => $"{t.Class}.{t.Function} is not in the loaded scripts: the port's override of it does not apply")
                .Concat(HP1.UEScript.UEPortScriptOverrides.ChangedTargets(null, unhashed)
                    .Select(t => $"{t.Class}.{t.Function} differs from the shipped game's function (a mod's scripts): the port's override of it still applies, so the mod's change to it may not take effect"))
                .Concat(unhashed.Select(u => $"{u} (the package could not be read, its functions were not compared with the shipped game's)"))
                .Concat(HP1.UEScript.UEPortScriptOverrides.ReleaseChangedTargets() is { Count: > 0 } release
                    ? new[] { $"this modified re-release changes {release.Count} functions the port overrides ({string.Join(", ", release)}): the port's overrides of them apply, so the re-release's changes to them do not take effect" }
                    : Array.Empty<string>())
                .ToList();
        }

        // forceSurfaceLightmapsInFrame — the game always enables per-frame surface lightmaps
        // and publishes the world static before the post-load frame (RunOwedLoadFrame). A test harness passes
        // false: it decides lightmaps itself and the first tick
        // (UEGameEngineNatives.TickWorld) publishes the static.
        public static UEGameEngine Boot(string gameRoot, string mapName, NativesFactory createNatives = null,
            bool selectHardwareRenderDevice = false, UEConfig.IStore configStore = null,
            bool forceSurfaceLightmapsInFrame = true, BootStops stops = null, string saveDir = null,
            IReadOnlyList<string> mods = null, int? saveSlot = null)
        {
            string systemDir = Path.Combine(gameRoot, "System");
            UEClassRegistry.Shared = new UEClassRegistry();
            var interp = new UEInterpreter();
            UEObject.DefaultInterp = interp;
            interp.ThrowOnMissingScript = true;
            UELevelRuntime.AppInit(systemDir, configStore);
            // A game whose port is partial (HP2) boots up to its first unported seam, which stops loudly by name
            // (UEGameProfile.Stop, the native gate in UENativesCore.Pair); a player frontend refuses it before Boot.
            foreach (string name in UEGameProfile.Known(UEGameProfile.Current.CorePackages, "the script package load set"))
            {
                string path = UEInstallPath.ScriptPackageFile(gameRoot, name);
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
            if (saveDir is not null) n.SaveDirOverride = saveDir;   // gameRoot is the mod working copy (hpengine ModOverlay)
            if (mods is not null) n.ActiveMods = mods;               // the enabled mods: recorded in saves, compared on load
            n.SaveSlot = saveSlot;                                    // HP2 appInit's SAVESLOT= (GSys+0x40)
            n.AppInitSaveSlot();
            stops?.NativesCreated?.Invoke(n);
            string mapPath = Path.Combine(gameRoot, "Maps", mapName + ".unr");
            mapPath = UEInstallPath.File(mapPath) ?? mapPath;
            if (File.Exists(mapPath) && UEPackageVerify.FirstMissingPackage(gameRoot, mapPath) is string missingPackage)
                throw new UEFailedBrowseException(mapName, missingPackage);   // UGameEngine::Init's FailedBrowse GError
            HeadlessBspModel bsp = HeadlessBspModel.Load(mapPath);
            journal.Add($"bsp nodes={bsp.NodeCount} leafHullValues={bsp.LeafHullValueCount}");
            n.SetBsp(bsp);
            n.SetReachSpecs(HeadlessBspModel.LoadReachSpecs(mapPath));
            // UGameEngine::Init [HP2 Engine 0x103a0fa0, thunk 0x10301c5d] browses FURL("Entry") before the startup map: the
            // Entry map's linker, so its names and its imports' (hgame...), are in the name table before the map's
            // (UENameTable; the port loads Entry's world after the map's graph). Live HP2 Quidditch: the map spells
            // 'PostFanFare_Win', hgame.u 'PostFanfare_Win'; the original prints hgame's.
            UENameTable.Linker(UEInstallPath.File(Path.Combine(gameRoot, "Maps", "Entry.unr")));
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
        // Death probe (snapshot --kill, HP1_KILL_AT): the player's baseHarry.KillHarry(true) between ticks,
        // the script's own death route (stateDead -> LoadSelectedSlot). No console route exists (KillHarry is not exec).
        // Returns the killed pawn's name, or null without a player pawn.
        public string KillPlayerProbe()
        {
            if (Runtime.PlayerPawn is not UEObject victim) return null;
            Interp.Invoke(victim, Interp.FindEventFunction(victim, "KillHarry")
                ?? throw new System.IO.InvalidDataException("KillPlayerProbe: the player has no KillHarry"), true);
            return victim.GetProp(PK.Name)?.ToString();
        }
        // Menu page probe (snapshot --open-book-at): FEBook.OpenBook(page) on the console's menu book, the call the menu's own
        // buttons make (e.g. the report page's folio card -> "FOLIO"). "<page>/<array property>/<index>" then clicks that
        // control of the page (Notify(DE_Click = 2), as MenuClickProbe), e.g. "FOLIO/SmallCardBmp/3" selects a card.
        // Returns the class of the page then current.
        public string OpenBookProbe(string spec)
        {
            string[] p = spec.Split('/');
            UEObject book = Runtime.ViewportConsole?.GetProp("menuBook") as UEObject
                ?? throw new System.IO.InvalidDataException("OpenBookProbe: the console has no menuBook (open the menu first)");
            Interp.Invoke(book, Interp.FindGlobalFunction(book, "OpenBook"), p[0]);
            UEObject cur = book.GetProp("curPage") as UEObject;
            if (p.Length == 3 && (cur?.GetProp(p[1]) as System.Collections.IList)?[int.Parse(p[2])] is UEObject c)
                Interp.Invoke(c, Interp.FindGlobalFunction(c, "Notify"), (byte)2);
            return cur?.Class?.Name;
        }
        // Menu click probe (window script verb `click <text>` or `click @<Property>`): the control of the open menu book page
        // whose Text is the given one (or held in that page property) gets Notify(DE_Click = 2), the call UWindowButton.Click makes for a mouse click (no cursor position
        // involved). Returns "<page class>.<control name>"; when the page has no such control, "NO SUCH CONTROL" and the
        // texts the page does have.
        public string MenuClickProbe(string text)
        {
            UEObject page = (Runtime.ViewportConsole?.GetProp("menuBook") as UEObject)?.GetProp("curPage") as UEObject;
            var texts = new List<string>();
            UEObject Find(UEObject w, int depth)
            {
                for (UEObject c = w?.GetProp("FirstChildWindow") as UEObject; c is not null && depth < 6; c = c.GetProp("NextSiblingWindow") as UEObject)
                {
                    if (UEInterpreter.ClassIsA(c.Class, "UWindowDialogControl"))
                    {
                        if (string.Equals(c.GetProp("Text")?.ToString(), text, StringComparison.OrdinalIgnoreCase)) return c;
                        texts.Add(c.GetProp("Text")?.ToString());
                    }
                    if (Find(c, depth + 1) is UEObject deeper) return deeper;
                }
                return null;
            }
            // "@<Property>": the page's control held in that property (HP2's bitmap buttons have no Text).
            UEObject control = text.StartsWith('@') ? page?.GetProp(text[1..]) as UEObject : Find(page, 0);
            if (control is null)
                return $"NO SUCH CONTROL on {page?.Class?.Name.ToString() ?? "no page"}; it has: {string.Join(" | ", texts)}";
            Interp.Invoke(control, Interp.FindGlobalFunction(control, "Notify"), (byte)2);
            return $"{page.Class?.Name}.{control.GetProp(PK.Name)}";
        }
        // Console probe (the live original probe's `exec=` / `pexec=`, run through UViewport::Exec): the command through the
        // ConsoleCommand native's own route (UEConsoleCommand.Execute with the runtime's config, input, live actors and
        // class defaults; no viewport device, no audio device).
        public string ConsoleProbe(string command)
            => UEConsoleCommand.Execute(Runtime.Config, command, Runtime.Input, null, Interp, null, _n.LiveActors());
        // Snapshot probe (the live original probe's `causeevent <Tag>` exec): Trigger(player, player)
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
        // Snapshot probe (the live original probe's `pcall=<label> 1 <actor> Trigger player player`): Trigger(player, player) on
        // the one actor of that Name. Returns its name, or "" when there is none.
        public string TriggerProbe(string name)
        {
            if (Runtime.PlayerPawn is not UEObject p) return "";
            foreach (UEObject a in new List<UEObject>(Runtime.Actors))
            {
                if (a is null || a.Destroyed || !string.Equals(a.GetProp(PK.Name)?.ToString(), name, StringComparison.OrdinalIgnoreCase)) continue;
                Interp.Invoke(a, Interp.FindEventFunction(a, "Trigger")
                    ?? throw new System.IO.InvalidDataException($"TriggerProbe: {name} has no Trigger"), p, p);
                return name;
            }
            return "";
        }
        // Reach probe (window script `exec reach <actor>`, the headless HP2_BOOT_EVENT verb Reach): the player is FarMoveActor'd
        // onto the actor of that Name and Touches it (a pickup, a bump-started cut); `exec bump <actor>`: Bump instead (a vendor,
        // Characters.Bump). Not a route: one event.
        public string ReachProbe(string name, string ev = "Touch")
        {
            if (Runtime.PlayerPawn is not UEObject p) return "";
            foreach (UEObject a in new List<UEObject>(Runtime.Actors))
            {
                if (a is null || a.Destroyed || !string.Equals(a.GetProp(PK.Name)?.ToString(), name, StringComparison.OrdinalIgnoreCase)) continue;
                _n.FarMoveActor(Interp, p, UEInterpreter.AsVector3(a.GetProp(PK.Location)), bTest: false, bNoCheck: false);
                UELifecycle.CallEvent(Interp, a, ev, p);
                return $"{name} state={a.CurrentStateName}";
            }
            return "";
        }
        // Peek probe (window script `exec peek <actor> <property>`): the live value of one property of the actor of that
        // Name (HP2 has no GET). Read-only.
        public string PeekProbe(string name, string prop)
        {
            foreach (UEObject a in Runtime.Actors)
                if (a is not null && !a.Destroyed && string.Equals(a.GetProp(PK.Name)?.ToString(), name, StringComparison.OrdinalIgnoreCase))
                    return $"{name}.{prop}={a.GetProp(prop)} state={a.CurrentStateName}";
            return "";
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
                // The Core interpreter owns the authoritative
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
