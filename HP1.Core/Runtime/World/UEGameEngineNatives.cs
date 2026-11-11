// PLATFORM NATIVE TABLE AND ENGINE GAME LOOP.
//
// The bodies below were moved from `runtime_poc/HeadlessSmoke.cs` (class HeadlessNatives)
// without changing behaviour or the journal, so the own engine (openhp1) boots and ticks
// the world through Core without referencing the test harness.
//
// HeadlessNatives (runtime_poc) derives from this class: it adds probes, comparison
// journals and fixture seams through explicit hooks (TickBegin, InputHook,
// BeforeActorsPass, BeforeFrame, AfterFrame, TickWorldEnd, JournalStream,
// SetLocationProbe, ActorTickBoundaryProbe) and by overriding OnUnknownNative.
// The hook order in TickWorld is exactly the order those pieces had in the former
// body, so the run journal is byte-identical.
//
// Class members keep the HeadlessSmoke.cs indentation (the class is not indented inside
// the namespace block) so `git diff --color-moved` reads the move as moved lines.
// Block namespace rather than file-scoped (netstandard2.1 + C# 9 profile).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HP1.Core;
using UELib.Core;
// Script vectors are the Core value type.
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
// HP1 shipped Core.dll routes appRand/appFrand/appRandRange through the MSVC CRT
// rand() (Core.dll.c:3578, 28148, 59731, 64932, 65436). The deterministic smoke
// fixture uses the MSVCRT recurrence with an explicit seed of 1; replay code in the
// shipped Engine also resets srand(1) (Engine.dll.c:129546). Production gameplay
// must seed this state from the same startup/replay source as the native runtime.
public class UEGameEngineNatives : UENativesCore
{
    // The Core-owned lifecycle: actor list, tick skeleton, spawn/
    // destroy, owner/base, zones and the level clock all live in
    // UELevelRuntime; this frontend supplies the BSP/anim/physics hooks.
    // Main session runtime; a native asks for the CURRENT world — the game world or,
    // while Entry code runs, the resident Entry (UELevelRuntime.Entry.cs).
    internal readonly UELevelRuntime MainRuntime = new();
    internal UELevelRuntime Runtime => MainRuntime.CurrentWorld;

    // UGameEngine::Init -> LoadMap(FURL("Entry")) before the first game map.
    internal void EnsureEntryWorld(UEInterpreter interp) => MainRuntime.EnsureEntryWorld(interp, _systemDir);
    // The release the install holds (UEEdition.OfSystem, the one owner): selects per-edition script overrides (UEPortScriptOverrides).
    internal UEEdition.DataSet DataSet => UEEdition.OfSystem(_systemDir).Data;
    internal List<UEObject> _actors => Runtime.Actors;
    // Install root: level travel must turn the map name from the URL into a path,
    // and the frontend owns paths.
    internal string _gameRoot;
    // The install root also sets the Core sound catalog (UESoundCatalog: one
    // duration formula for every frontend).
    public void SetGameRoot(string root)
    {
        _gameRoot = root;
        UESoundCatalog.Shared.SetSource(root);
        UEMusicCatalog.Shared.SetSource(root);   // Galaxy pool songs (UEGalaxyAudio) — Core, even without a window
    }

    // ── Saves (D1): Save directory under the game root, like shipped `..\Save` ──
    // Fixture seam: saves go to a temp folder so the GameSaveInfo native check
    // does not write into the install's Save.
    internal string SaveDirOverrideForTest;
    string SaveDir => SaveDirOverrideForTest ?? Path.Combine(_gameRoot, "Save");
    string SaveFilePath(string stem) => UELevelRuntime.SaveBodyPath(SaveDir, stem);

    // Reads and validates a save body before the old world is touched; false (with the reason) when it cannot be loaded.
    static bool TryReadSave(string path, out byte[] data, out string map, out string reason)
    {
        data = null; map = null; reason = null;
        try { data = File.ReadAllBytes(path); map = UELevelRuntime.ValidateSavedGame(data); return true; }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { reason = e.Message; return false; }
    }

    public void InstallSaveGameHandlers(UEInterpreter interp)
    {
        UEConsoleCommand.SaveGameHandler = slot =>
        {
            Directory.CreateDirectory(SaveDir);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            byte[] data = Runtime.SaveGame(interp);
            double serMs = sw.Elapsed.TotalMilliseconds;
            string path = SaveFilePath($"save{slot}");
            File.WriteAllBytes(path, data);
            _journal.Add($"savegame slot={slot} file={Path.GetFileName(path)} bytes={data.Length} "
                + $"ser_ms={serMs:F1} write_ms={sw.Elapsed.TotalMilliseconds - serMs:F1}");
            _journal.Add($"savegame_channels {Runtime.AnimChannelDigest()}");
            return "";
        };
        UEConsoleCommand.OpenHandler = url => { Runtime.RequestOpen(interp, url); return ""; };
        UEConsoleCommand.ExitHandler = () => _journal.Add("exit_request");
        UEConsoleCommand.FlushHandler = () => Runtime.FlushGCache();
    }
    private readonly Dictionary<int, (string Url, bool Items)> _travelProbe = new();
    // appSeconds for the LoadMap fade (UELevelRuntime.RunLoadMapFade); a test may replace it.
    public Func<double> WallSeconds = () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
    public void SetTravelProbe(int tick, string url, bool items) => _travelProbe[tick] = (url, items);
    internal readonly List<string> _journal;
    // The player host (UEGameEngine.Boot without a harness factory) keeps no comparison journal: nothing reads it there
    // (UEGameEngine.Tick cleared it unread), and its per-actor lines (tick_actor, walking_floor, walking_move, decal_*)
    // cost ~70 KiB of strings and ~0.4 ms per tick. Journal text only, never behaviour; the harness keeps every line.
    bool _journalOff;
    internal void DisableJournal() { _journalOff = true; Runtime.Journal = null; Runtime.DetailedJournal = false; }
    private readonly IniFile _ini;
    private readonly IniFile _userIni;
    private readonly UEIniFile _iniCore;
    internal readonly Hp1SkeletalMeshCatalog _skeletalMeshes;
    internal UEObject _localPlayer;
    private static readonly bool DetailedJournal =
        Environment.GetEnvironmentVariable("HP1_COMPACT_JOURNAL") != "1";
    // CFSkelHeader's root-motion cache (+0x6c..+0x94) is kept in the gameplay
    // runtime; the frontend only displays the actor location produced by this state.
    // The collision WORLD lives in Core (UELevelRuntime.Bsp) —
    // headless keeps only this proxy so the ~28 existing `_bsp` uses stay put
    // while the state itself is Core-owned.
    internal HeadlessBspModel _bsp
    {
        get => Runtime.Bsp;
        set => Runtime.Bsp = value;
    }
    internal int _currentTick = -1;

    // Provenance of every registered handler: the registry starts EMPTY
    // (base(false) skips the frontend-backed set). Only the
    // proven-pure intrinsic groups are imported from the shared VM, everything
    // else must be a headless handler, and an unregistered native THROWS via
    // OnUnknownNative instead of logging and continuing.
    internal readonly Dictionary<string, string> _provider = new(StringComparer.OrdinalIgnoreCase);
    internal readonly HashSet<string> _journaledNatives = new(StringComparer.OrdinalIgnoreCase);
    private string _registering = "headless";

    protected override void Pair(int idx, string name, UENativeHandler handler)
    {
        string provider = _registering;
        if (!string.IsNullOrEmpty(name)) _provider[name] = provider;
        base.Pair(idx, name, (interp, frame, args) =>
        {
            if (!string.IsNullOrEmpty(name) && _journaledNatives.Add(name))
                _journal.Add($"native {name} provider={provider}");
            return handler(interp, frame, args);
        });
    }

    public UEGameEngineNatives(uint seed, List<string> journal, string defaultIni,
        Hp1SkeletalMeshCatalog skeletalMeshes)
        : base(seed)
    {
        _journal = journal;
        _ini = new IniFile(defaultIni);
        _iniCore = new UEIniFile(defaultIni);
        // Shipped SpawnPlayActor reads "usr:DefaultPlayer.Class" FIRST
        // (Engine.dll.c:51548) and only then "ini:URL.Class" (51556). The first is the
        // user ini, whose template in the install is DefUser.ini
        // ([DefaultPlayer] Class=Harrypotter.harry). Without it the second
        // step, Engine.Playerpawn, would give the wrong SpawnClass.
        string userIni = Path.Combine(UELevelRuntime.SystemDirOfDefaultIni(defaultIni), "DefUser.ini");
        _userIni = File.Exists(userIni) ? new IniFile(userIni) : null;
        _skeletalMeshes = skeletalMeshes;
        // system dir (fonts live in the .u packages there); the shipped
        // viewport resolution feeds the canvas dims (retail Default.ini
        // [WinDrv.WindowsClient] WindowedViewportX/Y = 640x480).
        _systemDir = UELevelRuntime.SystemDirOfDefaultIni(defaultIni);
        _viewportX = int.Parse(_ini.Get("WinDrv.WindowsClient", "WindowedViewportX")
            ?? throw new InvalidDataException("WindowedViewportX missing from Default.ini"));
        _viewportY = int.Parse(_ini.Get("WinDrv.WindowsClient", "WindowedViewportY")
            ?? throw new InvalidDataException("WindowedViewportY missing from Default.ini"));
        BindFrameProducer(new UEWorldFrameProducer(_systemDir, _viewportX, _viewportY));
        // ACTUAL viewport state of this frontend. The window opens at the
        // saved SIZE (shipped substitutes Client->WindowedViewportX/Y
        // when NewX == -1, WinDrv.dll.c:15133-15144); after that the answer to
        // GetCurrentRes lives HERE and only here — not in the config that
        // SetRes writes.
        //
        // Colour DEPTH is NOT taken from the ini. Shipped:
        //   :5810-5825   the constructor sets ColorBytes from the desktop;
        //   :4907-4919   opening the window passes NewColorBytes = -1;
        //   :15062-15069 on -1 the ALREADY SET Viewport->ColorBytes is used.
        // The shipped `WindowedColorBits=16` is a SAVED value, not live viewport
        // state. The value is single and lives in Core with its source (C5).
        _viewport = new HeadlessViewportDevice(FrameProducer, _viewportX, _viewportY,
            UEViewport.DesktopColorBytes);
        FrameProducer.DestroyActor = fx => DestroyActorCore(fx.Interp ?? _localPlayer?.Interp, fx);

        // Wire the Core lifecycle skeleton to this frontend's BSP/anim/physics
        // layers. The skeleton (UELevelRuntime) is the ONE owner of the tick
        // order, spawn/destroy, owner/base and zones; everything below is the
        // headless implementation of the genuinely frontend-specific parts.
        Runtime.Journal = _journal.Add;
        Runtime.DetailedJournal = DetailedJournal;
        UELevelRuntime.ProfileTicks = Environment.GetEnvironmentVariable("HP1_TICK_PROF") == "1";
        // The walking/falling GEOMETRY hooks for the Core physics bodies:
        // the shipped SingleLineCheck trace (unpadded,
        // actor-aware, ETraceFlags-selected — the hover check's stationary
        // floor state also owns the walking_floor journal line, whose BSP
        // node/surfFlags detail only this host knows), FarMoveActor for the
        // ledge-stop restore, the shipped stepUp Mount probe, the shared
        // appFrand stream, and the trace gate.
        // No WalkMoveHook (PF-7): the caller reads its OWN FCheckResult (MoveCheck),
        // which the Core WalkMove path maintains itself; the fast path and a
        // MoveActor refusal publish no _lastMove* answer.
        Runtime.LineTraceHook = (i2, actor, start, end, extent, traceFlags) =>
        {
            UELevelRuntime.LineCheckHit hit = SingleLineCheckResult(i2, actor, ownerToSkip: actor,
                start, end, extent, traceFlags);
            // The stationary hover check (point trace, flags 6, straight
            // down 20) reports the standing pawn's floor state each tick —
            // the successor of the old stationary-probe journal line.
            if (traceFlags == 6 && extent == Vector3.zero && end.z == start.z - 20f
                && end.x == start.x && end.y == start.y
                && !_journalOff && (DetailedJournal || ReferenceEquals(actor, _localPlayer)))
                _journal.Add($"walking_floor actor={actor.GetProp("Name")} from={start} blocked={hit.Blocked.ToString().ToLowerInvariant()} fraction={hit.Time:R} normal={hit.Normal} node={hit.Node} surfFlags={_bsp.SurfFlags(hit.Node)} hit={hit.Actor?.GetProp("Name") ?? (object)"None"}");
            return hit;
        };
        Runtime.FarMoveHook = (i2, actor, newLoc) =>
            FarMoveActor(i2, actor, newLoc, bTest: false, bNoCheck: false);
        Runtime.FrandHook = NextRandomFrandSt0;
        Runtime.FrameProducer = FrameProducer;
        FrameProducer.AudioRuntime = Runtime;   // Galaxy pool world — this level's runtime
        Runtime.EmulateLoadStutter = AsPlayer;
        // The mesh catalog is Core: it also resolves
        // sequences and root tracks by the exact Mesh+AnimSequence pair.
        Runtime.LoadMeshCatalogFromInstall(_systemDir);
        // bAnimMove is written by Core (actor gameplay flag); root-motion state is Core's
        // and headless has no track representation of its own.
        // Core ticks logical animation itself (TickActorBody); headless needs no
        // AnimationHook — it has no visual half.
        // No PhysicsHook: the dispatcher, its rotation tail and PostTouch are
        // Core-owned now (UELevelRuntime.PerformPhysics). Headless deliberately
        // leaves the override unset so its physics runs through bare Core —
        // that is what makes the self-sufficiency claim testable.
        // Root motion is entirely Core: the decision, track data and the
        // mesh coordinate system.
        // (No PointRegionHook: Core finds zone and leaf itself from its own loaded model.)
        Runtime.DestroyCleanupHook = self =>
        {
            // The collision-hash removal moved into Core's own DestroyActor at
            // the same shipped position (Engine.dll.c:253027-253031) — the hash
            // is Core's structure. Only the frontend's own caches die here.
        };
        // (No SpawnEncroachHook: Core calls Runtime.CheckEncroachment itself.)
        // The one frontend part of the extent box: the UMesh package read.
        //
        // ObjectPath IS PASSED, AND THIS IS NOT COSMETIC: ambiguous names are
        // resolved by path — the same mesh name ships in two packages, and without
        // the package segment the query must stay loud (UEMeshBounds.Resolve).
        // E.g. `skbarrelMesh` has 2 exports; losing the path failed resolution with path=''.
        Runtime.MeshHashFieldsHook = meshRef =>
            _skeletalMeshes.GetHashFields(meshRef.Name, meshRef.ObjectPath);
        Runtime.DescribeCandidates = NativeCandidates;

        // Proven-pure intrinsic groups from the shared VM (verified: no
        // UnityActor/transform/GameObject/bridge access). Random intrinsics use
        // the shared HP1.Core MSVCRT stream seeded above.
        _registering = "base";
        RegisterBoolOps();
        RegisterIntOps();
        RegisterByteOps();
        RegisterFloatOps();
        RegisterStringOpsPure();
        RegisterTypeOpsPure();
        RegisterStateOpsPure();
        RegisterEntryLevelOps(() => MainRuntime);   // Core-owned GetEntryLevel
        RegisterLocalizationOps(() => SaveDir);   // Localize/GetLanguage/SaveConfig/GameSaveInfo — Core bodies
        RegisterMathOps();
        RegisterVectorOps();
        _registering = "headless";

        Pair(231, "Log", (interp, frame, args) =>
        {
            journal.Add("log " + (args.Length > 0 ? args[0]?.ToString() ?? "" : ""));
            return null;
        });
        // UObject::execDynamicLoadObject (Core.dll.c:63931): StaticLoadObject
        // with LOAD_NoWarn only when MayFail is true. The current gameplay path
        // requests Engine.Sound, so resolve the exact package/export path from
        // the shipped .uax; an arbitrary non-existent resource must not become
        // a truthy marker.
        Pair(0, "DynamicLoadObject", (interp, frame, args) =>
        {
            string path = args.Length > 0 ? NameArg(args, 0) : null;
            UClass requestedClass = args.Length > 1 ? args[1] as UClass : null;
            if (requestedClass is null)
                throw new InvalidDataException($"DynamicLoadObject missing class for '{path}'");
            // NON-SOUND RESOURCE — the shared Core resolver (one body for all
            // frontends, see UENativesCore.ResolveDynamicLoad).
            if (!string.Equals(requestedClass.Name?.ToString(), "Sound", StringComparison.OrdinalIgnoreCase))
            {
                object resolved = ResolveDynamicLoad(path, requestedClass.Name?.ToString(),
                    pkg => _journal.Add($"dynamic_load_unverifiable package={pkg} path={path}"
                        + " (the script registry does not load this package — existence cannot be checked)"));
                _journal.Add($"dynamic_load class={requestedClass.Name} path={path}"
                    + $" found={(resolved is not null ? "true" : "false")}");
                return resolved;
            }
            // StaticLoadObject flags are LOAD_NoWarn | (MayFail ? LOAD_Quiet : 0) (Core.dll.c:63984);
            // without -STRICT SafeLoadError (14713-14763) only logs and returns None,
            // so a miss is NOT fatal for any MayFail (baseDialog.FindDialog expects None).
            UESoundRef sound = UESoundCatalog.Shared.Load(path, mayFail: true);
            _journal.Add($"dynamic_load class=Sound path={path} found={(sound is not null).ToString().ToLowerInvariant()}"
                + (sound is null ? "" : $" duration={sound.Duration:R}"));
            return sound;
        });
        // AActor::execGetSoundDuration — body in Core (UELevelRuntime.
        // GetSoundDurationNative + UESoundCatalog).
        Pair(0, "GetSoundDuration", (interp, frame, args) =>
            Runtime.GetSoundDurationNative(interp, frame, args));
        // Bodies of these natives live in Core (UELevelRuntime.AnimNatives.cs): they write
        // exactly the engine properties Core TickAnimation reads. Only the mesh
        // catalog (LookupAnimSequence below) stays here.
        Pair(259, "PlayAnim", Runtime.PlayAnimNative);
        Pair(260, "LoopAnim", Runtime.LoopAnimNative);
        Pair(294, "TweenAnim", Runtime.TweenAnimNative);
        Pair(261, "FinishAnim", Runtime.FinishAnimNative);
        Pair(282, "IsAnimating", Runtime.IsAnimatingNative);
        Pair(263, "HasAnim", Runtime.HasAnimNative);
        Pair(293, "GetAnimGroup", Runtime.GetAnimGroupNative);
        Pair(312, "VisibleCollidingActors",
            (interp, frame, args) =>
                Runtime.VisibleCollidingActorsNative(interp, frame, args));
        // HP1 native(286) GetWorldCollisionBox(bool bVisual) — shipped
        // AActor::execGetWorldCollisionBox (Engine.dll.c:134732): bVisual →
        // the MESH primitive's bounding box (Mesh ?: Brush ?: collision
        // primitive), else the collision cylinder box Location ± (R,R,H).
        // The mesh/brush boxes reuse the oracle-validated HashBoxes formulas
        // (validate_collisionhash layer 3). Returns the BoundingBox struct
        // bag {Min,Max,IsValid}. Reached by baseSpell.Timer's homing aim
        // (TargetArea center, stage-7 hit fixture).
        Pair(286, "GetWorldCollisionBox", (interp, frame, args) =>
        {
            UEObject self = frame.Context ?? frame.Object;
            if (self is null) return null;
            bool bVisual = args is { Length: > 0 } && UEInterpreter.ToBool(args[0]);
            // Core owns the answer (UELevelRuntime.WorldCollisionBox): with neither mesh nor
            // brush shipped picks the primitive by CollideType (virtual slot 0xa4), and an
            // oriented cylinder, a box and a nonzero CollisionWidth give DIFFERENT answers
            // than a plain loc±(r,r,h).
            var (min, max) = Runtime.WorldCollisionBox(self, bVisual);
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                { { "Min", min }, { "Max", max }, { "IsValid", (byte)1 } };
        });
        // Core owns the candidate set (UELevelRuntime.AllActorsNative) — the single
        // answer to "what is Level.Actors"; see the comment in Core.
        Pair(304, "AllActors", (interp, frame, args) =>
            Runtime.AllActorsNative(interp, frame, args));
        Pair(262, "SetCollision", (interp, frame, args) =>
        {
            UEObject self = frame.Context ?? frame.Object;
            if (self is null) return null;
            // AActor::SetCollision (Engine.dll.c:30526): remove from the collision
            // hash under the OLD bCollideActors, set bCollideActors/bBlockActors/
            // bBlockPlayers, re-add under the NEW one. Each optional arg defaults
            // to the current value (NActor.cpp:554-560).
            bool? Flag(int i) => i < args.Length && args[i] is not null
                ? UEInterpreter.ToBool(args[i]) : (bool?)null;
            Runtime.SetCollision(self, Flag(0), Flag(1), Flag(2));
            return null;
        });
        Pair(283, "SetCollisionSize", (interp, frame, args) =>
        {
            UEObject self = frame.Context ?? frame.Object;
            if (self is null) return true;

            // Body entirely in Core (UELevelRuntime.SetCollisionSize): the exec thunk
            // (disasm 0x1040a870) writes the third optional argument to
            // CollisionWidth BEFORE calling the body and returns true unconditionally.
            // The Ghidra pseudocode loses this write.
            //
            // An absent optional differs from a passed zero: `null` means
            // "bytecode gave no value" and Core keeps the current one — like
            // shipped, which seeds the argument buffer with the current property.
            Runtime.SetCollisionSize(self,
                UEInterpreter.AsFloat(args is { Length: > 0 } ? args[0] : null),
                UEInterpreter.AsFloat(args is { Length: > 1 } ? args[1] : null),
                args is { Length: > 2 } && args[2] is not null
                    ? UEInterpreter.AsFloat(args[2]) : null);
            return true;
        });
        // TraceTexture (285) — body ENTIRELY in Core (UELevelRuntime.
        // TraceTextureNative), including resource resolution: two same-shaped
        // implementations are not delegation (C5).
        Pair(285, "TraceTexture", (interp, frame, args) =>
            Runtime.TraceTextureNative(interp, frame, args));
        Pair(266, "Move", (interp, frame, args) =>
        {
            UEObject self = frame.Context ?? frame.Object;
            if (self is null) return false;
            // AActor::execMove (Engine.dll.c:119068) passes the actor's CURRENT
            // rotation to MoveActor (Move never rotates) and all trailing flags 0.
            return MoveActor(interp, self, UEInterpreter.AsVector3(args.Length > 0 ? args[0] : null),
                UEInterpreter.AsRotator(self.GetProp("Rotation")),
                bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false);
        });
        Pair(267, "SetLocation", (interp, frame, args) =>
        {
            UEObject self = frame.Context ?? frame.Object;
            if (self is null) return false;
            // AActor::execSetLocation (Engine.dll.c:73485) is
            // FarMoveActor(this, NewLocation, 0, 0) — not a bare field write.
            // HP1_TRACE_SETLOC diagnostics — probe hook (runtime_poc), no body here.
            SetLocationProbe?.Invoke(self, args);
            return FarMoveActor(interp, self, UEInterpreter.AsVector3(args.Length > 0 ? args[0] : null),
                bTest: false, bNoCheck: false);
        });
        Pair(299, "SetRotation", (interp, frame, args) =>
        {
            UEObject self = frame.Context ?? frame.Object;
            if (self is null) return false;
            // AActor::execSetRotation (Engine.dll.c:15944) is MoveActor(this,
            // delta=(0,0,0), NewRotation, ...) — not a bare field write. The
            // zero delta hits the tiny-delta guard; a same-rotation SetRotation
            // returns true immediately, a real rotation goes the full path.
            return MoveActor(interp, self, Vector3.zero,
                UEInterpreter.AsRotator(args.Length > 0 ? args[0] : null),
                bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false);
        });
        // Sound natives — BODIES IN CORE (UELevelRuntime.SoundNatives.cs): the
        // PlaySound/PlayOwnedSound listener walk, AActor::CheckHearSound, the
        // ClientHearSound event and its native (APawn::execClientHearSound, by name),
        // StopSound, ModifySound.
        Pair(264, "PlaySound", (interp, frame, args) =>
            Runtime.PlaySoundNative(interp, frame, args));
        Pair(0, "PlayOwnedSound", (interp, frame, args) =>
            Runtime.PlayOwnedSoundNative(interp, frame, args));
        Pair(0, "ClientHearSound", (interp, frame, args) =>
            Runtime.ClientHearSoundNative(interp, frame, args));
        Pair(568, "StopSound", (interp, frame, args) =>
            Runtime.StopSoundNative(interp, frame, args));
        Pair(567, "ModifySound", (interp, frame, args) =>
            Runtime.ModifySoundNative(interp, frame, args));
        // Noise records land on the instigator's slots, the delivery walks the
        // Core PawnList and CanHear writes the listener's Stimulus — all Core
        // world state (AActor::CheckNoiseHearing, Engine.dll.c:90601).
        Pair(512, "MakeNoise", (interp, frame, args) =>
            Runtime.MakeNoiseNative(interp, frame, args));
        Pair(298, "SetBase", (interp, frame, args) =>
        {
            UEObject self = frame.Context ?? frame.Object;
            if (self is null) return null;
            SetBaseActor(interp, self, args.Length > 0 ? args[0] as UEObject : null, notify: true);
            return null;
        });
        // Pawn.uc native(553) FindPath(startPoint, dest). The shipped HP1 body
        // (Engine.dll.c APawn::findPath, 0x10402560) is not UE1's weighted
        // FindPathTo: it walks startPoint.Paths[16] with a fixed 100-entry DFS
        // stack/visited set and returns the first hop toward the first actor
        // whose FName equals dest. ReachSpecs come directly from ULevel in the
        // shipped .unr; no Unity scene graph, JSON export, Dijkstra weighting,
        // pruning or collision-size filter participates in this native.
        Pair(553, "FindPath", (interp, frame, args) => Runtime.FindPath(
            args.Length > 0 ? args[0] as UEObject : null,
            NameArg(args, 1)));
        // Pawn.uc native(521) final function bool pointReachable(Vector aPoint).
        // APawn::execpointReachable (Engine.dll.c:99183) is the whole wrapper:
        // read the vector, call the mode dispatcher with bKnowVisible = 0, and
        // store what came back. It stores the MODE MASK, not a normalised 0/1 —
        // the declaration says bool but the exec body writes the int straight
        // into the result slot, so a caller can still learn HOW the point is
        // reachable. Nothing here belongs to a frontend: the query walks the
        // Core world and moves the Core pawn.
        // Pawn.uc native(520) final function bool actorReachable(Actor anActor).
        // The whole exec body lives in Core (ActorReachableNative) because it is
        // gameplay: an Inventory unwrap, a proximity shortcut over the level's
        // NavigationPointList and the reachability query itself. Nothing about
        // it is frontend-shaped.
        // Actor.uc native(257) final function vector BonePos(name Bone) —
        // AActor::execBonePos (Engine.dll.c:91802); body is Core
        // (UELevelRuntime.SkeletalPose.BonePosNative). Callers in the campaign:
        // Hog2/SausageRollKidOnAStick, Hog2/DevilsSnareNew, Hub5/HarryL502, Hub5/Fluffy.
        Pair(257, "BonePos", (interp, frame, args) =>
        {
            UEObject self = frame.Context ?? frame.Object;
            if (self is null) return UEVector.zero;
            return Runtime.BonePosNative(self, args.Length > 0 ? args[0]?.ToString() : null);
        });
        Pair(520, "actorReachable", (interp, frame, args) =>
        {
            UEObject self = frame.Context ?? frame.Object;
            if (self is null) return 0;
            return Runtime.ActorReachableNative(interp, self,
                args.Length > 0 ? args[0] as UEObject : null);
        });
        Pair(521, "pointReachable", (interp, frame, args) =>
        {
            UEObject self = frame.Context ?? frame.Object;
            if (self is null) return 0;
            return Runtime.PointReachable(interp, self,
                UEInterpreter.AsVector3(args.Length > 0 ? args[0] : null),
                bKnowVisible: false);
        });
        // Pawn.uc native(524) final function int FindStairRotation(float DeltaTime).
        // The ONLY unregistered native statically reachable from the player's normal
        // Tut1 state: `Engine.PlayerWalking.PlayerMove` calls it on itself. Body
        // entirely in Core — three or four world traces and an actor-field
        // interpolation, nothing frontend-shaped.
        Pair(524, "FindStairRotation", (interp, frame, args) =>
        {
            UEObject self = frame.Context ?? frame.Object;
            if (self is null) return 0;
            return Runtime.FindStairRotation(interp, self,
                UEInterpreter.AsFloat(args.Length > 0 ? args[0] : null));
        });
        // Gesture.uc native(426) final function float CompareGesture(
        //     array<vector> Drawn, float Accuracy)
        // and native(427) final function float CompareGesturePoint(
        //     vector Point, float Accuracy).
        // Both are gameplay: they are the spell-lesson judge
        // (HPBase.SpellLearnTrigger.Judge :757 and HPBase.Judge.Tick :705), and
        // the whole body — dedup, densify, score — is Core's.
        Pair(426, "CompareGesture", (interp, frame, args) =>
        {
            UEObject self = frame.Context ?? frame.Object;
            if (self is null) return 0f;
            return Runtime.CompareGestureNative(self,
                args.Length > 0 ? args[0] : null,
                UEValue.AsFloat(args.Length > 1 ? args[1] : null));
        });
        Pair(427, "CompareGesturePoint", (interp, frame, args) =>
        {
            UEObject self = frame.Context ?? frame.Object;
            if (self is null) return 0f;
            return Runtime.CompareGesturePointNative(self,
                UEInterpreter.AsVector3(args.Length > 0 ? args[0] : null));
        });
        Pair(272, "SetOwner", (interp, frame, args) =>
        {
            UEObject self = frame.Context ?? frame.Object;
            if (self is null) return null;
            SetOwnerActor(interp, self, args.Length > 0 ? args[0] as UEObject : null);
            return null;
        });
        // SetPhysics: Actor.SetPhysics native=3970 in HP1 Engine.u (--dumpclass Actor).
        // Single arg (NewPhysics); the shipped floor arg is null here.
        Pair(3970, "SetPhysics", (interp, frame, args) =>
        {
            UEObject self = frame.Context ?? frame.Object;
            if (self is null) return null;
            SetPhysicsActor(interp, self, UEInterpreter.ToInt(args.Length > 0 ? args[0] : null), newFloor: null);
            return null;
        });
        // MoveTo (500, latent): APawn::execMoveTo (Engine.dll.c:85918). Set
        // Destination/Focus, DesiredSpeed = clamp(speed, 0, MaxDesiredSpeed),
        // setMoveTimer(dist), install the latent poll and prime one rotate+move.
        Pair(500, "MoveTo", (interp, frame, args) =>
        {
            UEObject self = frame.Context ?? frame.Object;
            if (self is null) return null;
            Vector3 dest = UEInterpreter.AsVector3(args.Length > 0 ? args[0] : null);
            float speed = args.Length > 1 && args[1] is not null ? UEInterpreter.AsFloat(args[1]) : 1f;
            UEPawnMovement.EnterMoveTo(self, frame, dest, speed, interp);
            return null;
        });
        // MoveToward (502): shipped APawn::execMoveToward (Engine.dll.c:80783).
        // Latent move toward a live ACTOR: bReducedSpeed=false, DesiredSpeed =
        // clamp(min(speed, MaxDesiredSpeed), >=0); a PAWN target (disasm
        // 0x103d8880) uses the fixed MoveTimer=1.2f instead of setMoveTimer(dist); MoveTarget is
        // kept and Destination/Focus start at its current Location (the poll
        // re-tracks it each tick). A null target starts nothing.
        Pair(502, "MoveToward", (interp, frame, args) =>
        {
            UEObject self = frame.Context ?? frame.Object;
            if (self is null) return null;
            if (args.Length == 0 || args[0] is not UEObject target) return null;
            float speed = args.Length > 1 && args[1] is not null ? UEInterpreter.AsFloat(args[1]) : 1f;
            UEPawnMovement.EnterMoveToward(self, frame, target, speed, interp);
            return null;
        });
        // MoveSmooth (3969, bound by name): shipped execMoveSmooth
        // (Engine.dll.c:62637) clears bJustTeleported, then returns
        // AActor::moveSmooth's first-move result verbatim.
        Pair(3969, "MoveSmooth", (interp, frame, args) =>
        {
            UEObject self = frame.Context ?? frame.Object;
            if (self is null) return false;
            return Runtime.ExecMoveSmooth(interp, self, UEInterpreter.AsVector3(args.Length > 0 ? args[0] : null));
        });
        // Destroy (279): execDestroy -> ULevel::DestroyActor (Engine.dll.c:252940..253089).
        // Mark deleted (the VM then reads it as Accessed-None), drop the base,
        // fire Destroyed, UnTouch everything, orphan owner and children. The
        // collision/light/bsp de-registration is implicit in the headless model.
        Pair(279, "Destroy", (interp, frame, args) =>
            DestroyActorCore(interp, frame.Context ?? frame.Object));
        Pair(278, "Spawn", Spawn);
        Pair(268, "BoneNumber", (interp, frame, args) =>
            Runtime.BoneNumberNative(interp, frame, args));
        Pair(269, "BoneName", (interp, frame, args) =>
            Runtime.BoneNameNative(interp, frame, args));
        Pair(274, "GetRenderExtent", (interp, frame, args) =>
            Runtime.GetRenderExtentNative(interp, frame, args));
        Pair(314, "Warp", (interp, frame, args) =>
            Runtime.WarpNative(interp, frame, args));
        Pair(315, "UnWarp", (interp, frame, args) =>
            Runtime.UnWarpNative(interp, frame, args));
        Pair(265, "CreateAnimChannel", CreateAnimChannel);
        Pair(277, "Trace", HeadlessTrace);
        Pair(309, "TraceActors", HeadlessTraceActors);
        Pair(310, "RadiusActors", (interp, frame, args) =>
            Runtime.RadiusActorsNative(interp, frame, args));
        Pair(311, "VisibleActors", (interp, frame, args) =>
            Runtime.VisibleActorsNative(interp, frame, args));
        Pair(548, "FastTrace", (interp, frame, args) =>
            Runtime.FastTraceNative(interp, frame, args));
        Pair(514, "LineOfSightTo", (interp, frame, args) =>
            Runtime.LineOfSightToNative(interp, frame, args));
        Pair(533, "CanSee", (interp, frame, args) =>
            Runtime.CanSeeNative(interp, frame, args));
        Pair(532, "PlayerCanSeeMe", (interp, frame, args) =>
            Runtime.PlayerCanSeeMeNative(interp, frame, args));
        Pair(534, "PickAnyTarget", (interp, frame, args) =>
            Runtime.PickAnyTargetNative(interp, frame, args));
        // ALevelInfo::execGetLocalURL — FURL::String of the level URL (UELevelRuntime.LevelUrl.cs).
        Pair(0, "GetLocalURL", (interp, frame, args) => Runtime.LocalUrlNative());
        // 321 CreateTextureFromBMP — Core body (RegisterLocalizationOps ->
        // UETextureImport). 322 CreateTextureFromScreenShot: a raster frame
        // snapshot is a frontend boundary; headless has no frame. LOUD debt: None
        // instead of a texture.
        Pair(322, "CreateTextureFromScreenShot", (interp, frame, args) =>
        { _journal.Add($"stop_texture_from_screenshot name={args[0]}"); return null; });
        // Actor.uc:1059 native(280) SetTimer(float NewTimerRate, bool bLoop):
        // schedule the Timer() event after NewTimerRate seconds, repeating if
        // bLoop; NewTimerRate<=0 cancels. Pure field writes; the count-up and
        // Timer() dispatch live in the deterministic actor tick below.
        // Decal.AttachDecal / Decal.DetachDecal — name natives (declared
        // `native simulated function` WITHOUT an index, `--dumpclass Decal`).
        // BOTH legs go to Core: the shadow builds the request itself from the decal
        // actor properties set by the `ActorShadow.Update` bytecode.
        Pair(0, "AttachDecal", (interp, frame, args) =>
        {
            object result = Runtime.AttachDecalNative(interp, frame, args);
            if (DetailedJournal && !_journalOff)
            {
                UEObject d = frame.Context ?? frame.Object;
                string verdict = result is not null && d?.GetProp("SurfList") is System.Collections.IList sl
                    ? $"surfs=[{string.Join(",", System.Linq.Enumerable.Cast<object>(sl))}]"
                    : "refused";
                _journal.Add($"decal_attach actor={d?.GetProp("Name")} {verdict}");
            }
            return result;
        });
        Pair(0, "DetachDecal", (interp, frame, args) =>
        {
            if (DetailedJournal && !_journalOff)
                _journal.Add($"decal_detach actor={(frame.Context ?? frame.Object)?.GetProp("Name")}");
            return Runtime.DetachDecalNative(interp, frame, args);
        });
        // APawn::execTurnTo / execPollTurnTo (Engine.dll.c:71384, 48528):
        // clear MoveTarget, store the fixed Focus, install the latent action,
        // update flying/swimming acceleration, and call rotateToward once. The
        // initial return value is deliberately ignored — even an already-facing
        // pawn resumes only when the poll runs.
        Pair(508, "TurnTo", (interp, frame, args) =>
        {
            UEObject self = frame.Context ?? frame.Object;
            if (self is null) return null;
            UEPawnMovement.EnterTurnTo(self, frame,
                UEInterpreter.AsVector3(args.Length > 0 ? args[0] : null));
            return null;
        });
        // APawn::execTurnToward (Engine.dll.c:40203): a null target is a true
        // no-op. Otherwise store FaceTarget, install the latent action, and aim
        // at its current Location; the poll refreshes that Location every tick.
        Pair(510, "TurnToward", (interp, frame, args) =>
        {
            UEObject self = frame.Context ?? frame.Object;
            if (self is null) return null;
            UEPawnMovement.EnterTurnToward(self, frame,
                args.Length > 0 ? args[0] as UEObject : null);
            return null;
        });
        // FinishInterpolation (301, latent): AActor::execFinishInterpolation
        // (disasm 0x104083b0-0x104083c5) — P_FINISH and LatentAction = 302 UNCONDITIONALLY.
        // Only the poll (0x104084c0) checks bInterpolating on the next
        // ProcessState, so an already stopped mover continues its code one tick later.
        Pair(301, "FinishInterpolation", (interp, frame, args) =>
        {
            frame.LatentState = LatentRunState.FinishInterpolation;
            return null;
        });
        // Sleep/SetTimer/AddPawn/RemovePawn — Core bodies (C7).
        RegisterActorTimerAndPawnListOps();
        // PickTarget (531) and the whole sight family are Core world queries:
        // they walk Core-owned actor/pawn lists, mutate Pawn sight state and
        // consume the shared appFrand stream.
        Pair(531, "PickTarget", (interp, frame, args) =>
            Runtime.PickTargetNative(interp, frame, args));
        Pair(0, "ConsoleCommand", (interp, frame, args) =>
        {
            // Core-owned (UEConsoleCommand.Execute): the RESULT is gameplay —
            // Harry.PostBeginPlay derives b3DSound from it — so it cannot be a
            // per-frontend stub.
            // Key bindings are owned by the runtime's UEInput (the bootstrap loads it
            // from [Engine.Input]); KEYBINDING needs it. The viewport device is the
            // frontend's: GetCurrentRes/SetRes must see the ACTUAL canvas, not what the ini saved.
            return UEConsoleCommand.Execute(Runtime?.Config, args.Length > 0 ? args[0]?.ToString() : null,
                Runtime?.Input, _viewport);
        });
    }

    private static string NameArg(object[] args, int index)
        => args.Length <= index || args[index] is null ? "None"
         : args[index] is UEName name ? name.Value : args[index].ToString();

    // Arg parsing only — AActor::CreateAnimChannel itself is Core
    // (UELevelRuntime.CreateAnimChannel): a channel is a spawned actor, so the
    // spawn order, encroachment, hash residency and destroy path are world
    // state.
    private object CreateAnimChannel(UEInterpreter interp, UEFrame frame, object[] args)
    {
        UEObject owner = frame.Context ?? frame.Object;
        UClass cls = args.Length > 0 ? args[0] as UClass : null;
        int animType = args.Length > 1 ? UEInterpreter.ToInt(args[1]) : 0;
        string boneName = args.Length > 2 && args[2] is UEName ueName
            ? ueName.Value : args.Length > 2 ? args[2]?.ToString() : null;
        bool autoDestroy = args.Length > 3 && UEInterpreter.ToBool(args[3]);
        return Runtime.CreateAnimChannel(interp, owner, cls, animType, boneName, autoDestroy);
    }

    private object Spawn(UEInterpreter interp, UEFrame frame, object[] args)
    {
        UClass cls = args.Length > 0 ? args[0] as UClass : null;
        if (cls is null) return null;
        // (Import-stub canonicalisation lives in UELevelRuntime.SpawnActor, one step
        //  for all frontends. The stub and the real class share a name, so the
        //  tag computed below does not change.)
        UEObject self = frame.Context ?? frame.Object;
        Vector3 location = args.Length > 3 && args[3] is not null
            ? UEInterpreter.AsVector3(args[3])
            : UEInterpreter.AsVector3(self?.GetProp("Location"));
        UERotator rotation = args.Length > 4 && args[4] is not null
            ? UEInterpreter.AsRotator(args[4])
            : UEInterpreter.AsRotator(self?.GetProp("Rotation"));
        UEObject owner = args.Length > 1 ? args[1] as UEObject : null;
        string tag = (args.Length > 2) switch
        {
            true when args[2] is UEName name => name.Value,
            true => args[2]?.ToString(),
            false => null
        };
        // execSpawn defaults SpawnTag to NAME_None and writes it unconditionally
        // (Engine 0x1040ca74, 0x1040cb87) — no class-name fallback here.
        if (string.IsNullOrEmpty(tag)) tag = "None";
        return SpawnCore(interp, self, cls, owner, tag, location, rotation);
    }

    // The spawn body behind the Spawn native (arg parsing stays in the lambda).
    // Split out so the selftest can drive spawns without a script frame.
    // ULevel::SpawnActor — Core-owned (UELevelRuntime.SpawnActor); FindSpot,
    // encroachment, hash residency and the base probe ride the spawn hooks.
    internal UEObject SpawnCore(UEInterpreter interp, UEObject self, UClass cls, UEObject owner,
        string tag, Vector3 location, UERotator rotation)
        => Runtime.SpawnActor(interp, self, cls, owner, tag, location, rotation,
            instigator: self?.GetProp("Instigator") as UEObject);

    // ULevel::DestroyActor — Core-owned (UELevelRuntime.DestroyActor); the
    // collision-hash/root-motion teardown rides the DestroyCleanupHook.
    internal bool DestroyActorCore(UEInterpreter interp, UEObject self)
        => Runtime.DestroyActor(interp, self);

    // DELEGATE. The query body lives in Core (UELevelRuntime.TraceNative). Only the
    // headless precondition and the journal line stay here: the journal is pinned by SHA
    // and belongs to this frontend.
    private object HeadlessTrace(UEInterpreter interp, UEFrame frame, object[] args)
    {
        if (_bsp is null) throw new InvalidOperationException("headless BSP not loaded");
        UEObject self = frame.Context ?? frame.Object;
        Vector3 end = UEInterpreter.AsVector3(args.Length > 2 ? args[2] : null);
        Vector3 start = args.Length > 3 && args[3] is not null
            ? UEInterpreter.AsVector3(args[3])
            : UEInterpreter.AsVector3(self?.GetProp("Location"));
        bool traceActors = args.Length > 4 && args[4] is not null
            ? UEInterpreter.ToBool(args[4])
            : UEInterpreter.ToBool(self?.GetProp("bCollideActors"));
        Vector3 extent = args.Length > 5 && args[5] is not null
            ? UEInterpreter.AsVector3(args[5]) : Vector3.zero;

        UEObject bestObject = Runtime.TraceNative(interp, frame, args, out var hit);
        bool blocked = bestObject is not null;
        float bestFraction = blocked ? hit.Time : 1f;
        int node = blocked ? hit.Node : -1;
        _journal.Add($"trace self={self?.GetProp("Name")} start={start} end={end} extent={extent} actors={traceActors.ToString().ToLowerInvariant()} blocked={blocked.ToString().ToLowerInvariant()} fraction={bestFraction:R} object={bestObject?.GetProp("Name")} node={node}");
        return bestObject;
    }

    // DELEGATE — see HeadlessTrace. Engine order, clipping the actor ray by the
    // world and recording a world hit (Actor = LevelInfo) belong to Core.
    private object HeadlessTraceActors(UEInterpreter interp, UEFrame frame, object[] args)
    {
        if (_bsp is null) throw new InvalidOperationException("headless BSP not loaded");
        UEObject self = frame.Context ?? frame.Object;
        string baseName = (args.Length > 0) switch
        {
            true when args[0] is UClass cls => cls.Name,
            true when args[0] is UEName name => name.Value,
            true => args[0]?.ToString(),
            false => null
        };
        Vector3 end = UEInterpreter.AsVector3(args.Length > 4 ? args[4] : null);
        Vector3 start = args.Length > 5 && args[5] is not null
            ? UEInterpreter.AsVector3(args[5])
            : UEInterpreter.AsVector3(self?.GetProp("Location"));
        Vector3 extent = args.Length > 6 && args[6] is not null
            ? UEInterpreter.AsVector3(args[6]) : Vector3.zero;

        Runtime.TraceActorsNative(interp, frame, args);
        int hits = interp.PendingIterator is UETraceActorsIterator it ? it.Count : 0;
        _journal.Add($"trace_actors self={self?.GetProp("Name")} base={baseName} start={start} end={end} extent={extent} hits={hits}");
        return null;
    }

    // ── PROBE HOOKS ────────────────────────────────────────────────────────────
    //
    // Probes, comparison journals and route checkpoints live in runtime_poc
    // (HeadlessNatives) and plug in here, at the SAME loop positions their bodies
    // occupied before the move. The engine (openhp1) needs no hooks: an empty hook is
    // exactly the behaviour with no HP1_* variables set.
    //   TickBegin         — tick start, after _currentTick (HP1_TRACE, sampler);
    //   InputHook         — after the "tick N begin" line, before the travel probe;
    //   BeforeActorsPass  — after the travel probe, before the actor pass;
    //   BeforeFrame       — before the engine frame (HP1_SURF_LM_POKE);
    //   AfterFrame        — after the frame, before the "tick N end" line;
    //   JournalStream     — journal flush when streamJournal;
    //   TickWorldEnd      — after the last tick of the call;
    //   SetLocationProbe  — in native 267 before FarMoveActor (HP1_TRACE_SETLOC);
    //   ActorTickBoundaryProbe — sink for actor-pass exceptions (HP1_SURVEY).
    public Action<int> TickBegin;
    public Action<UEInterpreter, int, float> InputHook;
    public Action<UEInterpreter, int> BeforeActorsPass;
    public Action<int> BeforeFrame;
    public Action<UEInterpreter, int, WorldFrame, float> AfterFrame;
    public Action JournalStream;
    public Action<UEInterpreter> TickWorldEnd;
    public Action<UEObject, object[]> SetLocationProbe;
    public Action<Exception> ActorTickBoundaryProbe;

    // `firstTick` — number of this call's first tick. The engine frame is cached BY
    // TICK NUMBER (UEWorldFrameProducer.Build): calling TickWorld(1) in a loop
    // without it would build "tick 0" every time and get the same frame —
    // no new audio commands, particles or HUD PostRender. Regular callers make one
    // call from zero and pass 0.
    public void TickWorld(UEInterpreter interp, int tickCount, float deltaSeconds,
        bool streamJournal = false, int firstTick = 0)
    {
        for (int tick = firstTick; tick < firstTick + tickCount; tick++)
        {
            _currentTick = tick;
            TickBegin?.Invoke(tick);   // probe hook: sampler tick 50, HP1_TRACE
            if (HP1.Core.UEPlaythroughLog.Enabled) HP1.Core.UEPlaythroughLog.Tick = tick;
            // The first boot's frame — before the start level's first tick (window opening;
            // (UELevelRuntime.EngineFrameOwedAfterLoad). It takes its number from the inside-tick
            // sequence (UELevelRuntime.NextInsideTickFrameNumber): the producer cache is keyed by number, so neither the
            // tick frame nor a fade / progress frame of the same tick may share it.
            if (Runtime.EngineFrameOwedAfterLoad && Runtime.FrameProducer is not null)
                Runtime.RunEngineFrame(interp, Runtime.NextInsideTickFrameNumber(), "load", deltaSeconds);
            float levelDelta = AdvanceLevelClock(interp, deltaSeconds);
            _journal.Add($"tick {tick} begin dt={levelDelta:R} actors={_actors.Count}");
            // Input — hook point (harness: HP1_TRACE_SETLOC and the fixture input
            // scenario; the engine feeds Core.Input before Tick itself).
            InputHook?.Invoke(interp, tick, levelDelta);
            // Travel probe: HP1_TRAVEL_AT=<tick>:<URL>[:0] calls the REAL shipped
            // function LevelInfo.ServerTravel — the same one a cutscene CHANGELEVEL
            // reaches. Only the call moment differs. This checks the ENGINE half,
            // it does not replace the scripted one.
            //
            // bItems defaults to TRUE because that is the game route:
            // CutScene.uc:745, CutScriptII.uc:360, TriggerChangeLevel.uc:34,
            // FEBook.uc:411 and BroomPracticeReferee.uc:769 all call
            // ChangeLevel(..., true). The ":0" suffix keeps the rare no-carry branch available.
            if (_travelProbe.TryGetValue(tick, out (string Url, bool Items) probe))
            {
                UEObject lvl = interp.GetLevelInfo();
                UFunction st = interp.FindEventFunction(lvl, "ServerTravel")
                    ?? throw new InvalidDataException("LevelInfo.ServerTravel missing");
                _journal.Add($"travel_probe tick={tick} url={probe.Url} items={probe.Items}");
                interp.Invoke(lvl, st, probe.Url, probe.Items);
            }
            // Probes before the actor pass (runtime_poc: HP1_CHANGELEVEL_AT,
            // HP1_TELEPORT, HP1_KILL_AT, HP1_SAVE_AT, HP1_LOAD_AT).
            BeforeActorsPass?.Invoke(interp, tick);
            TickActorsPass(interp, levelDelta);
            // GEntry->Tick right after the game level (FUN_103a0900,
            // Engine.dll.c:249090-249096) — with the same raw DeltaSeconds.
            MainRuntime.TickEntryWorld(interp, deltaSeconds);
            // Engine half of level travel: the script has already put the request
            // in LevelInfo (ServerTravel -> ProcessServerTravel), Core takes it, and
            // this runner turns the map name into a path — the file system belongs
            // to the frontend, not Core.
            // bItems is kept: it is the main game route (CutScene.uc:745 and all
            // ChangeLevel calls pass true). The carry gate is read from LevelInfo.bNextItems
            // inside Runtime.GetTravelInfo — exactly where shipped reads it
            // (Engine.dll.c:249177) — so here it is only journalled, not passed by a
            // second path (one source of truth).
            bool browsed = Runtime.TryTakeLevelTravel(interp, deltaSeconds, out string travelUrl, out bool travelItems);
            if (browsed)
            {
                _journal.Add($"travel_request url={travelUrl} items={travelItems}");
                // UGameEngine::LoadMap before loading: fade frames without a world tick (UELevelRuntime.RunLoadMapFade).
                int fadeFrames = Runtime.RunLoadMapFade(interp, deltaSeconds, Runtime.NextInsideTickFrameNumber, WallSeconds);
                _journal.Add($"loadmap_fade frames={fadeFrames}");
                (string map, string options) = UELevelRuntime.SplitTravelUrl(travelUrl);
                var cfg = PlayWorldConfigFromIni();
                cfg.Options = options;
                bool isSave = map.EndsWith(".usa", StringComparison.OrdinalIgnoreCase);
                byte[] data = null; string savedMap = null, badReason = null;
                string targetPath = isSave ? SaveFilePath(Path.GetFileNameWithoutExtension(map))
                    : UEInstallPath.File(Path.Combine(_gameRoot, "Maps", map.EndsWith(".unr", StringComparison.OrdinalIgnoreCase) ? map : map + ".unr"))
                      ?? Path.Combine(_gameRoot, "Maps", map.EndsWith(".unr", StringComparison.OrdinalIgnoreCase) ? map : map + ".unr");
                if (!File.Exists(targetPath))
                {
                    // LoadMap's catch: the old level plays on (UELevelRuntime.LoadMapMissingFile). E.g. the death
                    // reload `open save99.usa` with no level-start save.
                    Runtime.LoadMapMissingFile(interp, map);
                    _journal.Add($"loadmap_failed url_map={map} file={Path.GetFileName(targetPath)}");
                }
                else if (isSave && !TryReadSave(targetPath, out data, out savedMap, out badReason))
                {
                    // Damaged or newer-build save: LoadMap's catch, the old level plays on (UELevelRuntime.LoadMapBadSave).
                    Runtime.LoadMapBadSave(interp, map, badReason);
                    _journal.Add($"loadmap_failed url_map={map} file={Path.GetFileName(targetPath)} reason={badReason}");
                }
                else if (isSave)
                {
                    // `open saveN.usa` — world from a save (UELevelRuntime.SaveGame).
                    string savePath = targetPath;
                    string savedMapPath = Path.Combine(_gameRoot, "Maps", savedMap + ".unr");
                    savedMapPath = UEInstallPath.File(savedMapPath) ?? savedMapPath;
                    _journal.Add($"load_savegame file={Path.GetFileName(savePath)} map={savedMap} bytes={data.Length}");
                    _localPlayer = Runtime.LoadSavedWorldAndBootstrap(interp, savedMapPath, data, cfg);
                    _journal.Add($"load_savegame_done actors={Runtime.Actors.Count} player={_localPlayer?.GetProp("Name")}");
                    _journal.Add($"load_savegame_channels {Runtime.AnimChannelDigest()}");
                    _journal.Add($"load_savegame_level_action saved={Runtime.LoadedSaveLevelAction} "
                        + $"after_spawn_play_actor={UEInterpreter.ToInt(interp.GetLevelInfo()?.GetProp("LevelAction"))}");
                    // Music request seen by the first sound Update (original: PostLogin's None with MTRAN_Fade, D-43).
                    _journal.Add($"load_savegame_music song={(_localPlayer?.GetProp("Song") as UEResourceRef)?.ObjectPath ?? "None"} "
                        + $"transition={UEInterpreter.AsByte(_localPlayer?.GetProp("Transition"))}");
                }
                else
                {
                    _localPlayer = Runtime.Travel(interp, targetPath, cfg);
                }
                WorldFrame screen = Runtime.LastLoadMapScreen;
                _journal.Add($"loadmap_levelinfo hud={screen?.Hud.Count ?? -1}");
            }
            // EVERY tick, not only when a frontend asks for the snapshot.
            //
            // UEWorldFrameProducer.Build IS the port of UGameEngine::Draw: it
            // runs the view actor's PreRender/PostRender bytecode (which lazily
            // spawns myHUD and drives myHUD.PostRender) and Update(0) on every
            // live ParticleFX. Both draw from the SHARED MSVCRT stream, exactly
            // as shipped — the original has no frame in which they do not run.
            //
            // Gating that on a `--frames` flag invented a mode the original
            // does not have, and it showed up as the review's P0: building a
            // frame "perturbed gameplay spawns", because the plain mode was the
            // one running a world where the HUD never drew. The frame is now
            // always executed; the flag only decides whether the SNAPSHOT is
            // journalled.
            // Engine frame phase — one Core call for all frontends. The frame number
            // here is the gate tick, because the journal prints it.
            long frameT0 = UELevelRuntime.ProfileTicks ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            if (_publishWorldStaticForLightmaps && Runtime.LatestWorldStatic is null) Runtime.PublishWorldStaticForDiagnostics();
            BeforeFrame?.Invoke(tick);   // HP1_SURF_LM_POKE probe
            // A frame that browsed draws nothing more: UGameEngine::Tick returns right after Browse — server travel
            // 0x103a11ea -> ret 0x103a1313, client travel 0x103a1622 -> jmp 0x103a12fe -> ret — so neither
            // Client->Tick (Draw, Console.Tick: the slot 99 save) nor the pending level runs; the loading screen
            // stays up and the new level is first drawn after its first ULevel::Tick, in the next frame.
            WorldFrame frame = browsed ? Runtime.HoldLoadMapScreen(tick) : Runtime.RunEngineFrame(interp, tick, "gate", deltaSeconds);
            if (UELevelRuntime.ProfileTicks) UELevelRuntime.ProfileFrameTicks += System.Diagnostics.Stopwatch.GetTimestamp() - frameT0;
            // Comparison journals and route checkpoints (runtime_poc).
            AfterFrame?.Invoke(interp, tick, frame, deltaSeconds);
            _journal.Add($"tick {tick} end actors={_actors.Count}");
            if (streamJournal) JournalStream?.Invoke();
        }
        TickWorldEnd?.Invoke(interp);   // segment acceptance, ledger journal flush
    }

    // One actor pass of ULevel::Tick (also the stage-3 fixtures' entry) —
    // Core-owned; Survey mode swallows per-actor boundaries to enumerate them.
    internal void TickActorsPass(UEInterpreter interp, float levelDelta)
        => Runtime.TickActorsPass(interp, levelDelta, ActorTickBoundaryProbe);

    // Shipped ULevel::Tick (Engine.dll.c:262612-262641) clamps the real frame
    // delta, applies LevelInfo.TimeDilation, converts it to 32.32 fixed point,
    // and publishes the accumulated high-precision time as a float BEFORE any
    // actor is ticked. Repeated float addition is observably different on long
    // routes and breaks TimeSeconds-based script guards.
    // The shipped fixed-point level clock — Core-owned (UELevelRuntime).
    internal void InitializeLevelClock(UEInterpreter interp)
        => Runtime.InitializeLevelClock(interp);

    internal float AdvanceLevelClock(UEInterpreter interp, float realDelta)
        => Runtime.AdvanceLevelClock(interp, realDelta);

    public void SetBsp(HeadlessBspModel bsp) => _bsp = bsp;
    public void SetReachSpecs(IReadOnlyList<LevelReachSpecRef> reachSpecs)
        => Runtime.SetReachSpecs(reachSpecs);
    // Layout and alias table come from the user ini, exactly the
    // [Engine.Input] section shipped UInput reads. Nothing is invented:
    // no file means a loud error, not a "default layout".
    internal void LoadInputBindings()
    {
        Runtime.Input.Journal = _journalOff ? null : _journal.Add;
        Runtime.LoadInputBindingsFromInstall(_systemDir);
        // "AS PLAYER" MODE for comparison with the original (HP1_AS_PLAYER=1); the headless
        // gate skips it so pins do not shift: first run selects the hardware device
        // (D3DDrv, HighDetailActors=True — bHighDetail trees and shadows live) and the
        // first ticks after load stutter (EmulateLoadStutter).
        if (AsPlayer || SelectHardwareRenderDevice) Runtime.SelectHardwareRenderDeviceIfUnset();
    }
    // The player (UEGameEngine.Boot(selectHardwareRenderDevice: true)) always selects it.
    internal bool SelectHardwareRenderDevice;
    internal static readonly bool AsPlayer = Environment.GetEnvironmentVariable("HP1_AS_PLAYER") == "1";

    // Config is read by CORE (UELevelRuntime.ConfigFromInstall) — the ini key set and
    // their priority order live there, once for all frontends.
    internal UELevelRuntime.PlayWorldConfig PlayWorldConfigFromIni()
        => UELevelRuntime.ConfigFromInstall(_systemDir);

    // Player login events (SpawnPlayActor 0x103a86cc..0x103a9005), recorded by
    // EXECUTION through the VM on this world boot (login_events journal).
    internal readonly List<string> LoginEventsForTest = new();
    static readonly HashSet<string> LoginEventNames = new(StringComparer.OrdinalIgnoreCase)
        { "Login", "TravelPreAccept", "AcceptInventory", "TravelPostAccept", "PostLogin" };

    public void BootstrapPlayWorld(UEInterpreter interp)
    {
        LoadInputBindings();
        var prevProbe = UEInterpreter.CallProbe;
        UEInterpreter.CallProbe = (o, f, a) =>
        {
            prevProbe?.Invoke(o, f, a);
            string n = f?.Name?.ToString();
            if (n is not null && LoginEventNames.Contains(n))
                LoginEventsForTest.Add($"{n}@{o?.Class?.Name}");
        };
        UEObject playerPawn;
        try { playerPawn = Runtime.BootstrapPlayWorld(interp, PlayWorldConfigFromIni()); }
        finally { UEInterpreter.CallProbe = prevProbe; }
        _journal.Add("login_events " + string.Join(",", LoginEventsForTest));
        _localPlayer = playerPawn;
        _journal.Add($"local_player actor={playerPawn.GetProp("Name")}:{playerPawn.Class?.Name}"
            + $" player={Runtime.Viewport?.Class?.Name} console={Runtime.ViewportConsole?.Class?.Name}");
    }

    internal bool _publishWorldStaticForLightmaps;
    // Per-frame surface lightmaps; the first tick publishes the world static (TickWorld). Off by default:
    // the game turns it on via UEGameEngine.Boot(forceSurfaceLightmapsInFrame), the harness via its HP1_SURF_LM.
    internal void EnableSurfaceLightmapsInFrame()
    {
        Runtime.ComputeSurfaceLightmapsInFrame = true;
        _publishWorldStaticForLightmaps = true;
    }
    internal readonly string _systemDir;
    private readonly int _viewportX;
    private readonly int _viewportY;

    // ── HEADLESS FRONTEND VIEWPORT DEVICE ─────────────────────────────────────
    //
    // Headless has no window but has a VIEWPORT: a canvas whose size
    // UEWorldFrameProducer holds and puts into Canvas.SizeX/SizeY — exactly what the
    // script sees. So resize here is REAL: it changes that canvas, not a number in a
    // side field.
    //
    // This is the frontend half of the boundary: Core parses the command, decides
    // order and saves; the actual values live here.
    internal sealed class HeadlessViewportDevice : HP1.UEScript.IViewportDevice
    {
        private readonly UEWorldFrameProducer _producer;

        public HeadlessViewportDevice(UEWorldFrameProducer producer,
            int sizeX, int sizeY, int colorBytes)
        {
            _producer = producer;
            SizeX = sizeX;
            SizeY = sizeY;
            ColorBytes = colorBytes;
        }

        public int SizeX { get; private set; }
        public int SizeY { get; private set; }
        public int ColorBytes { get; private set; }
        // Headless has no screen: modes are the shipped windowed list.
        public string DisplayModes => HP1.UEScript.UEConsoleCommand.WindowedResolutions;

        // FULLSCREEN HERE IS STATE, NOT A WINDOW. Headless has no window, so the
        // "mode" means exactly what it is in shipped: the BlitFlags bit that picks
        // the key pair on save (WinDrv.dll.c:1619 -> :15366-15379). This frontend does
        // not run the startup chain (UEViewportStartup) — it opens no window — so the
        // bit stays false and saving goes to Windowed*.
        public bool Fullscreen { get; private set; }

        // Shipped windowed resize rejects nothing: the driver mode is enumerated
        // only in fullscreen (WinDrv.dll.c:5126), and the window gets the requested
        // size. Zero width/height is filtered by Core itself — where shipped does it.
        //
        // It is also SYNCHRONOUS here, like shipped: behind the headless viewport there
        // is no window or swapchain, only two numbers in the frame producer that change
        // within this call. So the outcome is always Applied and the device never has an
        // open transaction.
        public ViewportResizeOutcome Resize(int sizeX, int sizeY, int colorBytes, bool fullscreen)
        {
            // Width rounds up to even — shipped does this INSIDE
            // ResizeViewport, before applying and on every path
            // (WinDrv.dll.c:15152 -> Viewport->SizeX :15297). Height is untouched.
            sizeX = UEViewport.NormalizeSizeX(sizeX);
            SizeX = sizeX;
            SizeY = sizeY;
            Fullscreen = fullscreen;
            // Zero means "keep depth" — that is how `SetRes 800x600` without a
            // third part arrives.
            if (colorBytes != 0) ColorBytes = colorBytes;
            _producer?.SetViewport(sizeX, sizeY);
            return ViewportResizeOutcome.Applied;
        }
    }

    internal HP1.UEScript.IViewportDevice _viewport;
    // Window device of a frontend with a real window (OpenHP1): size, mode and mode list come from the
    // window and monitor, not the headless canvas. The frontend still sets the script canvas itself (UEWorldFrameProducer.SetViewport).
    public void UseViewportDevice(HP1.UEScript.IViewportDevice device)
        => _viewport = device ?? throw new ArgumentNullException(nameof(device));

    // Frame building is owned by the SHARED Core producer (UEWorldFrameProducer).
    // Headless supplies its actor list and local player.
    // Same producer bindings as RunEngineFrame (corona line check, particle queries, ...): a frame built here must not
    // depend on an earlier engine frame having bound them.
    public WorldFrame BuildWorldFrame(int tick, string label)
    {
        Runtime.BindFrameProducerHooks(_localPlayer?.Interp);
        return FrameProducer.Build(tick, label, _actors, _localPlayer?.Interp, _localPlayer);
    }

    internal List<UEObject> ActorsLive => _actors;
    // Serialized UPrimitive bounds (FBox + FSphere) per Model export, read from
    // the .unr — the data source for the conservative brush gates below. The
    // MAP is Core state (UELevelRuntime.BrushBoundsByExport, layer C); the
    // package read that fills it stays here.
    // Core-owned (UELevelRuntime.SetBrushSource): the map path, the per-export
    // model cache and the parse all live there now, so a frontend gets brush
    // geometry by loading the world rather than by re-implementing the lookup.
    public void SetBrushSource(string mapPath, Dictionary<int, BrushBounds> bounds)
    {
        _brushMapPath = mapPath;          // kept: BuildWorldStatic still reports it
        Runtime.SetBrushSource(mapPath, bounds);
    }

    internal string _brushMapPath;

    // Shipped ULevel::MoveActor (Engine.dll.c:123689, 0x103aa390..0x103ab231)
    // (UActor.cpp:1367): the atomic sweep-move-resolve primitive behind the Move
    // native. Orchestration follows the shipped control flow: tiny-delta fast
    // path, 2-unit padded sweep with the hit time re-based to the unpadded span,
    // rider carry (with yaw orbit) BEFORE the commit, unconditional rotation
    // commit, then Bump/Touch/UnTouch/re-zone. Returns "moved at all"
    // (Result.Time > 0), not "unblocked" — a partial move is TRUE.
    // Core-owned (UELevelRuntime.MoveActor) — layer C. MoveActor,
    // MoveSmooth and CheckEncroachment are MUTUALLY RECURSIVE (MoveActor's
    // pre-commit encroachment check pushes riders with moveSmooth, which moves
    // again), so they moved as ONE unit — splitting them would have needed a
    // hook back into the frontend for each leg of the cycle.
    internal bool MoveActor(UEInterpreter interp, UEObject actor, Vector3 delta, UERotator newRot,
        bool bTest, bool bIgnorePawns, bool bIgnoreBases, bool bNoFail)
        => Runtime.MoveActor(interp, actor, delta, newRot, bTest, bIgnorePawns, bIgnoreBases, bNoFail);

    // Core-owned (UELevelRuntime.FarMove) — layer C. Runtime.FarMoveHook
    // remains: it is part of the required PhysWalking hook set
    // (WalkMoveHook/LineTraceHook/FarMoveHook) and an active test seam
    // (fixtures count and intercept far moves).
    internal bool FarMoveActor(UEInterpreter interp, UEObject actor, Vector3 newLoc, bool bTest, bool bNoCheck)
        => Runtime.FarMove(interp, actor, newLoc, bTest, bNoCheck);

    // AActor::SetBase — Core-owned (UELevelRuntime.SetBase).
    private void SetBaseActor(UEInterpreter interp, UEObject actor, UEObject newBase, bool notify)
        => Runtime.SetBase(interp, actor, newBase, notify);

    // AActor::SetOwner — Core-owned (UELevelRuntime.SetOwner).
    private void SetOwnerActor(UEInterpreter interp, UEObject actor, UEObject newOwner)
    {
        Runtime.SetOwner(interp, actor, newOwner);
    }

    // AActor::setPhysics — Core-owned (UELevelRuntime.SetPhysics, the shipped
    // Engine.dll.c:134160 helper). Its base (re)probe is Core's own FindBase.
    private void SetPhysicsActor(UEInterpreter interp, UEObject actor, int newPhysics, UEObject newFloor)
        => Runtime.SetPhysics(interp, actor, newPhysics, newFloor);

    private UELevelRuntime.LineCheckHit SingleLineCheckResult(UEInterpreter interp, UEObject self,
        UEObject ownerToSkip, Vector3 start, Vector3 end, Vector3 extent, int traceFlags)
        => Runtime.SingleLineCheck(interp, self, ownerToSkip, start, end, extent, traceFlags);

    // Core-owned (UELevelRuntime.InitCollisionHash) — layer C.
    internal void InitCollisionHash() => Runtime.InitCollisionHash();

    internal static string NativeCandidates(UFunction fn)
    {
        var method = typeof(UEInterpreter).GetMethod("LoadTokens",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        if (method?.Invoke(null, new object[] { fn }) is not System.Collections.IEnumerable tokens) return "?";
        var names = new List<string>();
        foreach (object token in tokens)
        {
            var type = token.GetType();
            object item = type.GetField("NativeItem")?.GetValue(token);
            if (item is not null)
            {
                string name = item.GetType().GetField("Name")?.GetValue(item)?.ToString()
                           ?? item.GetType().GetProperty("Name")?.GetValue(item)?.ToString();
                string index = item.GetType().GetField("ByteToken")?.GetValue(item)?.ToString()
                            ?? item.GetType().GetProperty("ByteToken")?.GetValue(item)?.ToString();
                names.Add($"native:{name ?? "?"}/{index ?? "?"}");
            }
            object virtualName = type.GetField("FunctionName")?.GetValue(token);
            if (virtualName is not null) names.Add("virtual:" + virtualName);
            if (type.GetField("Function")?.GetValue(token) is UFunction final)
                names.Add("final:" + final.Name);
        }
        return string.Join(",", names.Distinct());
    }

    public void SetActors(IEnumerable<UEObject> actors) => Runtime.SetActors(actors);
}

internal sealed class IniFile
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections =
        new(StringComparer.OrdinalIgnoreCase);

    public IniFile(string path)
    {
        string section = "";
        foreach (string sourceLine in File.ReadLines(path))
        {
            string line = sourceLine.Trim();
            if (line.Length == 0 || line.StartsWith(';')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1];
                if (!_sections.ContainsKey(section))
                    _sections[section] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            int equals = line.IndexOf('=');
            if (equals <= 0) continue;
            if (!_sections.TryGetValue(section, out var values))
                _sections[section] = values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            values[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }
    }

    public string Get(string section, string key)
        => _sections.TryGetValue(section, out var values) && values.TryGetValue(key, out string value)
            ? value : null;

    // Whole section in file order — needed for [Engine.Input]: both the alias table
    // and the layout are read as a list of pairs, not by key name.
    public IEnumerable<KeyValuePair<string, string>> Section(string section)
        => _sections.TryGetValue(section, out var values)
            ? values : Enumerable.Empty<KeyValuePair<string, string>>();
}
}
