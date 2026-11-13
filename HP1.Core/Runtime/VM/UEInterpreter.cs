// UnrealScript VM.
//
// Runtime object model (UEObject), execution frame (UEFrame), and the
// interpreter (UEInterpreter). Token handlers are verified against the exec*
// bodies of Core.dll HP1 1.53. Bytecode parsing is UELib
// (UELib/Core/Tokens/*.cs). The source of truth is the original binaries and scripts.
//
// Behaviour is documented at the handlers below; Location/Rotation
// reads through the frontend hook are at InstanceVariableToken.

using System;
using System.Collections.Generic;
using UELib;
using UELib.Core;
using Tok = UELib.Core.UStruct.UByteCodeDecompiler;
using UELib.Tokens;  // CastToken enum (UnrealTokens.cs)
using HP1.Core;      // UETrace sinks + UEClassRegistry
// Script vectors ARE the Core value type: prop-bag storage, struct decode,
// literals and the As*-surface all use HP1.UEScript.UEVector.
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{


    public delegate UEEvalResult UETokenHandler(UEInterpreter interp, UEFrame frame, Tok.Token tok);

    public partial class UEInterpreter
    {
        // The frontend's native table (UENatives / HeadlessNatives). Assigned
        // at bootstrap (UEScriptRuntime ctor / headless smoke): registration
        // and every handler body live with the frontend, the Core only
        // resolves by index/name and reports unknowns.
        public IUENativeRegistry Natives;

        // The Core-owned ULevel.Actors list: the world's
        // actor set in its ORIGINAL stable order, filled by the level loader.
        // Frame building enumerates THIS — never a frontend scene graph (a
        // Core actor without a frontend proxy must still be in the frame,
        // and scene order is not the authoritative actor order).
        public System.Collections.Generic.List<UEObject> LevelActors;

        // In the headless core a compiled call that resolves to
        // NOTHING (no state/class function, no registered native) or a token
        // with no handler must STOP the scenario — logging and returning null would hide
        // missing subtitles/warnings.
        // UEGameEngine.Boot sets true; false only for fixtures that build the interpreter by hand.
        public bool ThrowOnMissingScript;
        // Diagnostics-only: native-dispatch coverage hook (headless native log and test fixtures
        // proving which native ran; null in a normal run).
        public static System.Action<string> NativeCoverageHook;


        // execIterator (Core.dll.c:33534) is empty in shipped: the loop lives in the
        // iterator native itself. Port-specific form: the native (AllActors etc.)
        // builds an iterator and parks it here, IteratorToken moves it to the
        // frame's iterator stack. Also here — the loop variable lvalue (an
        // out-param), taken while collecting the native's arguments: in shipped
        // its address is GPropAddr inside the native.
        public UEIterator PendingIterator;
        public UELValueRef PendingIteratorLoopVar;
        public List<UELValueRef?> PendingIteratorOutRefs;
        // Log of actors created by SpawnActor (one ref per spawn). The AllActors
        // walk does NOT read it (it uses the live Actors list, as execAllActors
        // 0x1040d847, UELevelRuntime.VisibilityNatives.cs). In HP1.Core the log
        // is only written and cleared (test fixtures read it).
        //
        // ONE LOG PER WORLD. Shipped keeps two live levels — the game level and the
        // resident Entry (UGameEngine::Init, Engine.dll.c:38221-38243), and each one's
        // spawns go to ITS OWN ULevel::Actors. While the Entry world runs, the log is
        // swapped for its own (UELevelRuntime.InEntryWorld), otherwise Entry's
        // GameInfo/Mutator/GRI would leak into the game map's iterations and registries.
        public List<UEObject> SpawnLog => ActiveSpawnLog ?? _spawnLogMain;
        readonly List<UEObject> _spawnLogMain = new();
        internal List<UEObject> ActiveSpawnLog;

        // UClass::ClassUnique — the MakeUniqueObjectName counter (Core.dll.c:41313).
        // It lives ON THE CLASS, and classes survive map changes, so the counter is
        // session state, not level state: the live 1.53 original on all 39 maps
        // gives GameInfo1/Mutator1/GameReplicationInfo1 (the zeros went to Entry).
        public readonly Dictionary<UClass, int> ClassUnique = new(ClassIdentity.Instance);
        // Object names in the Transient package — the outer of `new` without Outer
        // (UObject::execNew -> GetTransientPackage, Core.dll.c:25126-25128):
        // StaticFindObject(NULL, Transient, name) in MakeUniqueObjectName.
        // The port has no garbage collection, so names are never released.
        internal readonly HashSet<string> TransientObjectNames = new(StringComparer.OrdinalIgnoreCase);
        readonly System.Runtime.CompilerServices.ConditionalWeakTable<UEObject, HashSet<string>> _namesInOuter = new();
        internal sealed class ClassIdentity : IEqualityComparer<UClass>
        {
            public static readonly ClassIdentity Instance = new();
            public bool Equals(UClass a, UClass b) => ReferenceEquals(a, b);
            public int GetHashCode(UClass c) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(c);
        }
        // Dedup for the per-frame "Accessed None" diagnostic: UE1 logs Accessed None and
        // continues, and faithful unguarded reads (e.g. BaseCam.CheckPosition's debug-string
        // PossibleVictim.Name with a clear view) repeat every frame. Log each site once.
        static readonly HashSet<string> _loggedAccessedNone = new();

        // Player singleton as a VM object. HP1 reads `PlayerHarry` bare (a baseHarry
        // instance var) from many classes; UE1 assigns it at runtime, which we never
        // run, so the InstanceVariableToken handler falls back to this. Set by
        // HP1PlayerHarryBridge (thin bridge UEObject: BridgeMethods + myHUD prop).
        public UEObject PlayerHarry;

        // DEBUG: when set (from execute_script), TickPawnMove + HP1UE1Physics.TickWalkingSubstep
        // log their internals for THIS one pawn only (so a stuck NPC can be traced without spam).
        public UEObject DebugWalkTarget;

        // The world LevelInfo — what UE1's engine assigns to every actor's `Level`
        // property at load. We never run that, so a bare `Level` read resolves here
        // (see InstanceVariableToken). Bootstrapped lazily with the minimal world the
        // VM bytecode expects to exist: LevelInfo(NetMode=NM_Standalone).Game =
        // GameInfo(Difficulty=1).BaseMutator = Mutator. This is the FAITHFUL fix for
        // Actor.PreBeginPlay's relevance cull — Engine.Actor.PreBeginPlay does
        //   if(!bGameRelevant && Level.NetMode!=NM_Client && !Level.Game.IsRelevant(self)) Destroy();
        // With a real Level.Game, GameInfo.IsRelevant (run through the VM) returns
        // true for placed SP actors (bSinglePlayer/bDifficulty* default true,
        // Mutator.CheckReplacement returns true) → the branch is false → no spurious
        // self-destruct. We instantiate these as bare VM objects (no GameObject):
        // IsRelevant only reads props + calls Mutator methods, never Spawns.
        UEObject _levelInfo;
        public UEObject GetLevelInfo()
        {
            if (_levelInfo != null) return _levelInfo;
            var rt = UEClassRegistry.Shared;
            var liClass = rt.Find("LevelInfo");
            if (liClass == null) return null;           // Engine not loaded — give up
            _levelInfo = new UEObject { Class = liClass, Interp = this };
            _levelInfo.LoadDefaults();
            _levelInfo.Props["NetMode"] = (byte)0;       // ENetMode.NM_Standalone
            _levelInfo.Props["Level"] = _levelInfo;       // LevelInfo.Level == self
            var giClass = rt.Find("GameInfo");
            if (giClass != null) {
                var gi = new UEObject { Class = giClass, Interp = this };
                gi.LoadDefaults();                        // Difficulty=1, MutatorClass=Mutator
                gi.Props["Level"] = _levelInfo;           // GameInfo.IsRelevant reads Level.NetMode
                var mutClass = rt.Find("Mutator");
                if (mutClass != null) {
                    var mut = new UEObject { Class = mutClass, Interp = this };
                    mut.LoadDefaults();
                    mut.Props["Level"] = _levelInfo;
                    gi.Props["BaseMutator"] = mut;        // GameInfo normally Spawns this in InitGame
                }
                _levelInfo.Props["Game"] = gi;
            }
            return _levelInfo;
        }

        // ── LIFECYCLE OF THE SINGLE LevelInfo ───────────────────────────────
        //
        // In shipped there is ONE LevelInfo and it is ULevel::Actors(0): `GetLevelInfo()`
        // in the decompile is Actors(0), guarded by
        // `Actors(0)->IsA(ALevelInfo::StaticClass())` (UnLevel.h:0xf7-0xf8).
        //
        // The object GetLevelInfo() above creates is a pre-world STUB: before the
        // map is read there is no level yet, but pre-world code (class
        // registration, GameInfo.IsRelevant) already needs one. Once the map is
        // loaded the stub must give way to the real actor, otherwise TWO
        // LevelInfos live in the world and any identity-based "is this the world?"
        // check fails (e.g. APawn::Mount rejecting ALL wall hits).
        //
        // A transaction, not an assignment: binding carries over what the stub
        // accumulated and the map does not serialize, zeroes time exactly where
        // shipped does, and fixes back-references. Release is mandatory on world
        // teardown — otherwise the next map would take the previous LevelInfo as
        // bootstrap and drag its Game along.
        // The world's ULevel for Actor.XLevel: one object per world LevelInfo (new map — new ULevel).
        // The port does not model ULevel fields in it — nothing reads them (see the XLevel read above).
        UEObject _xLevel, _xLevelOf;
        public UEObject XLevel
        {
            get
            {
                UEObject li = GetLevelInfo();
                if (!ReferenceEquals(li, _xLevelOf))
                {
                    _xLevelOf = li;
                    // Engine.Level is a native class without an export in Engine.u; the witness is the map's import (export MyLevel).
                    _xLevel = new UEObject { Class = UEClassRegistry.Shared?.Find("Level") ?? UEClassRegistry.Shared?.FindImportedClass("Level")
                        ?? throw new InvalidOperationException("Actor.XLevel: class Engine.Level is absent (neither export nor import)"), Interp = this };
                }
                return _xLevel;
            }
        }

        public void BindWorldLevelInfo(UEObject placed)
        {
            if (placed is null || ReferenceEquals(placed, _levelInfo)) return;
            UEObject stub = _levelInfo;
            if (stub is not null)
            {
                // The map does not serialize the net mode, and pre-world code has already read it.
                if (placed.GetProp(PK.NetMode) is null && stub.GetProp(PK.NetMode) is not null)
                    placed.Props["NetMode"] = stub.GetProp(PK.NetMode);
                // Game is carried over UNCONDITIONALLY: on a placed LevelInfo this field
                // is serialized by the map and means nothing before InitGame, while
                // pre-world code already holds a reference to the stub GameInfo.
                // The real GameInfo registers itself here (BootstrapPlayWorld).
                if (stub.GetProp(PK.Game) is not null)
                    placed.Props["Game"] = stub.GetProp(PK.Game);
            }
            // TimeSeconds=0 — shipped does this in LoadMap (Engine.dll.c:247341),
            // BEFORE the InitExecution pass over all actors (247376-247381). On a
            // placed LevelInfo the field is SERIALIZED by the map (Tut1: 118.3 s —
            // how long the level lived in the editor), and without zeroing the world
            // would start at a foreign time. The load-time prime of steady-state
            // emitters (ParticleFxInitExecution) is gated on this same zero.
            placed.Props["TimeSeconds"] = 0f;
            placed.Props["Level"] = placed;               // LevelInfo.Level == self
            if (placed.GetProp(PK.Game) is UEObject game)
            {
                game.Props["Level"] = placed;
                if (game.GetProp(PK.BaseMutator) is UEObject mutator)
                    mutator.Props["Level"] = placed;
            }
            _levelInfo = placed;
        }

        // World teardown. The next GetLevelInfo() builds a FRESH stub instead of
        // returning the torn-down map's level.
        //
        // Release BY OWNERSHIP: the tearing-down world releases the level only if
        // the interpreter holds EXACTLY it. Otherwise tearing down one world would
        // take identity from another — and not only in fixtures: any code that
        // brings up a second UELevelRuntime must be able to tear it down without
        // touching the current one.
        //
        // NULL MEANS "I BOUND NOTHING", NOT "I OWN EVERYTHING". If null cleared ANY
        // active level, tearing down an empty or failed foreign world would take
        // identity from the current one — exactly what this method must forbid.
        // Whoever bound nothing has nothing to release.
        public void ReleaseWorldLevelInfo(UEObject owned)
        {
            if (owned is not null && ReferenceEquals(_levelInfo, owned)) _levelInfo = null;
        }

        // Restoring an identity snapshot (harness isolation): put the level back
        // as is, without the binding transaction.
        public void RestoreWorldLevelInfo(UEObject level) => _levelInfo = level;

        // Identity without side effects: GetLevelInfo() builds a stub, while world
        // switching needs to know what is there NOW (including "nothing").
        public UEObject PeekLevelInfo() => _levelInfo;

        readonly Dictionary<Type, UETokenHandler> _handlers = new();

        public UEInterpreter()
        {
            RegisterTokenHandlers();
        }

        // A render-resource ref (UELib Texture UObject or UEResourceRef) used as a
        // member-access context needs USize/VSize readable — the HUD sizes its tiles
        // from Tex.USize/VSize. Resolve the ref to a Texture2D and cache a proxy
        // UEObject exposing USize/VSize. Returns null if the ref isn't a resolvable
        // texture (non-texture object refs fall through to the Accessed-None path).
        //
        // ONE PROXY PER TEXTURE OBJECT, keyed by the resolved export itself (physical package file + export
        // index), never by the leaf name: 94 leaf names in the shipped install name textures of DIFFERENT size
        // (Engine.MedFont.Texture1 256x128 vs HPBase.HugeInkFont.Texture1 256x64, GenFX.LensFlar.flare4 64x64 vs
        // HP_FX.Particles.flare4 128x128 — measured over 127 packages). Misses are not cached: an
        // unresolvable reference throws every time. `_proxyByRef`/`_proxyByRefSpelling` only skip re-resolving
        // the SAME reference (object or exact spelling) once it has a hit.
        static readonly Dictionary<string, UEObject> _texProxies = new(StringComparer.OrdinalIgnoreCase);   // file names: as the catalogue
        static readonly Dictionary<object, UEObject> _proxyByRef = new(ReferenceEqualityComparer.Instance);
        static readonly Dictionary<string, UEObject> _proxyByRefSpelling = new(StringComparer.Ordinal);

        static readonly Dictionary<string, UELib.UnrealPackage> _resourcePackagesByPath =
            new(StringComparer.OrdinalIgnoreCase);

        // Shared arg-collection loop for FunctionToken descendants. Consumes
        // sub-expressions via EvalNext until the EndFunctionParmsToken marker
        // (UELib FunctionTokens.cs:35-39).
        //
        // For each positional parameter on `targetFn`: if the parameter has
        // PropertyFlag.OutParm, we read the corresponding arg expression as
        // an LValue (capturing where to write back) AND its current value;
        // otherwise we eval as a normal RValue. Shipped UObject::CallFunction
        // (Core.dll.c:7715) records the argument's GPropAddr for a parameter with
        // the OutParm flag (0x100); our streaming model needs the explicit split
        // (lvalue + value).
        //
        // If `targetFn` is null (we don't have a UFunction for the native
        // we're calling), every arg is treated as RValue — the writeback
        // path is simply skipped.
        // ── DECLARATION BEHIND AN IMPORT STUB ────────────────────────────────
        //
        // A cross-package SCRIPT function (e.g. Object.AddEqual_PlanePlane called
        // from Engine.u) arrives in the token as an IMPORT STUB: it has no body, no
        // parameters, no flags. Invoke can resolve the body, but reading the `out`
        // flags from the stub before the call would mean "no out-params": the argument
        // would be collected by value, with no writeback, and `FlashFog += ...`
        // in PlayerPawn.ViewFlash would change only the callee's local copy. Here the
        // stub is resolved to the real declaration by THE SAME rule (canonical
        // DECLARING class chain, name match, body with tokens; a state stub via
        // the same-named state); the result is cached per stub.
        readonly Dictionary<UFunction, UFunction> _stubDecl = new(UERefComparer<UFunction>.Instance);

        static readonly List<UELValueRef?> NoOutRefs = new List<UELValueRef?>(0);
        // Collection list — one per call nesting depth (an argument may itself be
        // a call); returned as an array right away. A new List per call with its
        // array growing would be a notable share of tick garbage.
        readonly List<List<object>> _argLists = new List<List<object>>();
        // Frames, argument arrays and out-ref lists of returned calls (UEVmPool).
        internal readonly UEVmPool Pool = new UEVmPool();
        int _argDepth;

        // Handlers by token index — per function token stream (shared by all its
        // frames in this interpreter). The _handlers table is filled only by the
        // constructor, so the cache never goes stale.
        readonly Dictionary<List<Tok.Token>, UETokenHandler[]> _handlerCache =
            new(UERefComparer<List<Tok.Token>>.Instance);
        static readonly Dictionary<List<Tok.Token>, TokenKind[]> _tokenKinds = new(UERefComparer<List<Tok.Token>>.Instance);

        // Read the next token in the stream as an LValue (storage location)
        // rather than its current value. Used by LetToken/LetBoolToken to
        // resolve the LHS. In shipped the lvalue is GPropAddr/GProperty, set by the
        // variable tokens (execLocalVariable / execInstanceVariable /
        // execBoolVariable) and consumed by execLet/execLetBool; we recover the
        // same information by peeking the token type and extracting its
        // name/owner.
        // AllowValueLocalStructWrites: the flag is only saved/set/restored in
        // Invoke; nothing in the tree reads it to change struct member writes.
        public bool AllowValueLocalStructWrites;

        // UObject::CallFunction (Core.dll.c:7715): locals are zeroed, arguments
        // are written to parameters, the body is ProcessInternal (or a native),
        // then out-params are copied back starting from the LAST. Here — out locals
        // are copied into args[]; the caller-side LValue writeback happens in the
        // FinalFunction/NativeFunction handlers using outRefs we recorded
        // during CollectArgsWithOuts.
        // Observation hook FOR FIXTURES ONLY — at the FUNCTION CALL level, not the
        // engine event level. Production never sets it.
        //
        // WHY SEPARATE FROM UELifecycle.EventProbe (measured, not assumed):
        // `TriggerEvent` is neither an engine event nor a native but a SCRIPT
        // function (`foreach AllActors(...) A.Trigger(Other, Instig)`), so its
        // target arrives here, in Invoke, and never reaches CallEvent. A hook on
        // CallEvent is blind to that whole class of calls (a KidPath2 fixture
        // counted zero spawner triggers with two pawns actually spawned).
        public static Action<UEObject, UFunction, object[]> CallProbe;

        // Diagnostic-only (HP1_TRACE=1): trace trigger-chain calls. Read-only,
        // no simulation effect; disabled builds pay one bool check in Invoke.
        private static void TraceWatchedCall(UEObject obj, UFunction fn, object[] args)
        {
            string name = fn.Name?.ToString();
            if (name == null) return;
            bool interpolateTo = name.Equals("InterpolateTo", StringComparison.OrdinalIgnoreCase);
            if (!interpolateTo
                && !name.Equals("Trigger", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("UnTrigger", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("TriggerEvent", StringComparison.OrdinalIgnoreCase)) return;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(args[i] is UEObject u
                    ? (u.GetProp(PK.Name)?.ToString() ?? u.Class?.Name ?? "obj")
                    : (args[i]?.ToString() ?? "None"));
            }
            string keys = interpolateTo
                ? $" keynum={obj?.GetProp(PK.KeyNum)} prevkeynum={obj?.GetProp(PK.PrevKeyNum)}"
                : "";
            HP1.Core.UETrace.Log(
                $"call={name}"
                + $" target={obj?.GetProp(PK.Name)} class={obj?.Class?.Name}"
                + $" state={obj?.CurrentStateName ?? "None"}{keys} args=[{sb}]");
        }

        // ROUTE LOG: NAMES TAKEN FROM .uc, NOT INVENTED.
        //
        // Each name below is declared in the game scripts and lies on the mandatory
        // Tut1/Tut1b route: the cutscene (CutScene.uc), its actor commands
        // (baseChar/baseHarry Cut*), lesson HUD commands (baseHud), granting and
        // selecting a spell (baseWand), a line (theNarrator.FindDialog), house
        // points and the level transition itself (baseConsole.ChangeLevel — CUT_CHANGELEVEL=14).
        //
        // WHY A LIST, NOT "ALL CALLS". A level playthrough is millions of calls; a
        // log that writes everything answers "what did the frame do", while the
        // question is "how far did the route get".
        static readonly System.Collections.Generic.Dictionary<string, string> RouteCalls =
            new(UEPropNameComparer.Instance)
            {
                ["StartPlaying"] = "cutscene", ["FinishPlaying"] = "cutscene",
                ["CutSkip"] = "cutscene", ["CutCue"] = "cutscene", ["Reset"] = "cutscene",
                ["CutCapture"] = "cutact", ["CutRelease"] = "cutact",
                ["CutMoveTo"] = "cutact", ["CutTurnTo"] = "cutact",
                ["CutAnimate"] = "cutact", ["CutDoTalk"] = "cutact",
                ["ShowPopup"] = "hud", ["DestroyPopup"] = "hud",
                ["StartCountdown"] = "hud", ["StopCountdown"] = "hud",
                ["ReceiveIconMessage"] = "hud",
                ["addSpell"] = "spell", ["SelectSpell"] = "spell",
                ["FindDialog"] = "dialog",
                // A cutscene line is delivered NOT by FindDialog but by DeliverDialog:
                // CutScene.uc case 20 (CUT_TALK) and case 6 (CUT_SAY) call
                // `theNarrator.DeliverDialog(var1)` and take the delay until the
                // next action FROM IT. Without this name "zero dialogs" in the log
                // would mean a probe gap, not a silent game.
                ["DeliverDialog"] = "dialog",
                ["CutDoTalk"] = "cutact",
                ["AddHousePoints"] = "score",
                ["ChangeLevel"] = "travel",
                ["TriggerNext"] = "lesson",
                // Original camera bootstrap: Harry.PostBeginPlay calls
                // ViewClass(BaseCam,true) and makeCamTarget() (Harry.uc:1238-1239).
                // Observing them distinguishes "the port does not run them" from
                // "runs them while the bridge duplicates".
                // Camera mode and its entry: BaseCam picks stationary or moving
                // mode by the DIFFERENCE of player positions, and "not standing
                // still" requires a non-zero difference on ALL THREE axes
                // (BaseCam.uc, operator 181 = NotEqual_FloatFloat). On a flat
                // floor the vertical difference is zero — so a walking player
                // counts as standing. The observation prints both the mode and the
                // exact position, because the breadcrumb trail rounds to integers
                // and `dz` cannot be proven from it.
                ["GeneralStationaryModeCamera"] = "cammode",
                ["GeneralMoveModeCamera"] = "cammode",
                ["makeCamTarget"] = "camboot",
                ["ViewClass"] = "camboot",
                ["PostBeginPlay"] = "lifecycle",
                ["PreBeginPlay"] = "lifecycle",
                ["Trigger"] = "trigger", ["UnTrigger"] = "trigger",
                ["TriggerEvent"] = "trigger",
                ["InterpolateTo"] = "mover",
            };

        private static void NoteRouteCall(UEObject obj, UFunction fn, object[] args)
        {
            string name = fn.Name?.ToString();
            if (name == null || !RouteCalls.TryGetValue(name, out string kind)) return;
            // `Reset` is a name not only the cutscene carries (e.g.
            // `Reset on=HeadlessCanvas(Canvas)`); the function name does NOT
            // prove the owner, so the object type is checked.
            if (kind == "cutscene" && !ClassIsA(obj?.Class, "CutScene")) return;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(args[i] is UEObject u
                    ? (u.GetProp(PK.Name)?.ToString() ?? u.Class?.Name?.ToString() ?? "obj")
                    : (args[i]?.ToString() ?? "None"));
            }
            string self = obj?.GetProp(PK.Name)?.ToString() ?? obj?.Class?.Name?.ToString() ?? "?";
            string line = $"{name} on={self}({obj?.Class?.Name}) args=[{sb}]";
            if (kind == "cammode" && obj?.GetProp(PK.P) is UEObject camP)
            {
                var pl = AsVector3(camP.GetProp(PK.Location));
                // Print THE VERY DIFFERENCE the source compares:
                // `HarrysPreviousPosition - P.Location`, per component. Otherwise
                // one would subtract neighbouring log lines, which the repeat cap
                // collapses — and which component breaks `&&` would stay a guess.
                var prev = AsVector3(obj.GetProp(PK.HarrysPreviousPosition));
                var camLoc = AsVector3(obj.GetProp(PK.Location));
                var track = AsVector3(obj.GetProp(PK.TrackingPoint));
                line += $" bStationary={ToBool(camP.GetProp(PK.bStationary))}"
                      + $" dif=({prev.x - pl.x:F4},{prev.y - pl.y:F4},{prev.z - pl.z:F4})"
                      // Pinning terms: the camera teleports to
                      // Location + TrackingPoint (SetLocation, step <= 300*dt).
                      // A small TrackingPoint with a moving player means the
                      // target is considered where the camera already is; a large
                      // one with a stationary camera means SetLocation itself refuses.
                      + $" cam=({camLoc.x:F1},{camLoc.y:F1},{camLoc.z:F1})"
                      + $" track=({track.x:F3},{track.y:F3},{track.z:F3})"
                      + $" |track|={track.magnitude:F3}";
            }
            // travel NEVER collapses: it is the end of the route.
            if (kind == "travel") HP1.Core.UEPlaythroughLog.Always(kind, line);
            // THE DEDUP KEY INCLUDES THE FIRST ARGUMENT, AND THIS MATTERS.
            //
            // For cues and triggers the identity IS the argument: `CutCue(DumEnter)`
            // and `CutCue(DumbTalkOnWalk)` are different events of one cutscene.
            // Keyed by `name:actor`, all cues would collapse into a few lines and
            // the chain of lines could not be reconstructed from the log.
            string first = args.Length > 0
                ? (args[0] is UEObject a0
                    ? (a0.GetProp(PK.Name)?.ToString() ?? a0.Class?.Name?.ToString() ?? "obj")
                    : (args[0]?.ToString() ?? "None"))
                : "";
            HP1.Core.UEPlaythroughLog.Note(kind, $"{name}:{self}:{first}", line);
            HP1.Core.UEPlaythroughLog.MarkIfCheckpoint(self, name);
        }

        // Original body of a function replaced by the port (UEPortScriptOverrides) —
        // re-entry with this flag runs the bytecode without the override.
        [ThreadStatic] static UFunction _portBypass;

        // The override closure lives in its own method: inside Invoke the capture
        // (obj, fn, args) would be allocated on EVERY call.
        object InvokePortOverride(UEObject obj, UFunction fn, object[] args, UEPortScriptOverrides.Handler portBody)
            => portBody(this, obj, args, () =>
            {
                UFunction prev = _portBypass;
                _portBypass = fn;
                try { return Invoke(obj, fn, args); }
                finally { _portBypass = prev; }
            });

        // Shipped probe check — IsProbing in UObject::ProcessInternal (Core.dll.c:21624)
        // before ANY script body, including an explicit script call
        // `A.Trigger(...)` (Core 0x101431df, disasm); the mask bit is
        // FStateFrame.ProbeMask, edited by natives Disable/Enable (execDisable
        // Core.dll.c:31010, execEnable :54226). The check sits in Invoke before
        // Run. (Example: Engine.Inventory.Sleeping has no Touch stub at all,
        // only a cleared IgnoreMask bit.) There are no other checks: shipped stores
        // the disable only in the mask (D-20); there is no separate name-based disable map.
        public object Invoke(UEObject obj, UFunction fn, params object[] args)
        {
            if (fn == null) return null;
            if (UEVmPool.Verify) UEVmPool.CheckArgs(args);
            if (HP1.Core.UETrace.Enabled) TraceWatchedCall(obj, fn, args);
            if (HP1.Core.UEPlaythroughLog.Enabled) NoteRouteCall(obj, fn, args);
            CallProbe?.Invoke(obj, fn, args);
            // Empty body (bare `return;`) — execution has no effects: no property
            // writes, no out-params (none to write), no RNG. All dispatch semantics
            // (trace, probe hooks) already ran above, so no frame/locals/token loop
            // is built. Reason: HP1 Actor.uc defines an EMPTY event Tick dispatched
            // to every actor every tick (2147 on Lev_Tut1b); without the fast path
            // an empty call cost ~6 µs of interpreter overhead (HP1_TICK_PROF).
            // Zero tokens never get here: that is an import stub with its own
            // fallback path below.
            if (!fn.FunctionFlags.HasFlag(UELib.Flags.FunctionFlag.Native) && IsEmptyBody(fn))
                return null;
            if (!ReferenceEquals(_portBypass, fn) && UEPortScriptOverrides.TryGet(fn, out var portBody))
                return InvokePortOverride(obj, fn, args, portBody);
            // Pooled (UEVmPool): released below only on the two normal returns (native body, script body); the
            // stub/import paths and exceptions leave the frame to the GC.
            var frame = Pool.RentFrame(obj, fn);
            BindParams(frame, fn, args);

            // Native fast path — UObject::CallFunction (Core.dll.c:7715): iNative
            // != 0 → Func(Stack, Result) bypassing ProcessInternal (no probe gate).
            // Native callbacks mutate args[] directly for out params (in shipped —
            // the write goes through GPropAddr inside the native).
            if (fn.FunctionFlags.HasFlag(UELib.Flags.FunctionFlag.Native))
            {
                int idx = fn.NativeToken;
                var cb = idx > 0
                       ? Natives.GetByIndex(idx)
                       : Natives.GetByName(fn.Name?.ToString());
                if (cb == null) {
                    Natives.OnUnknownNative(idx, fn.Name?.ToString(),
                        $"{frame.Object?.Class?.Name}.{fn.Name}");
                    return null;
                }
                NativeCoverageHook?.Invoke(fn.Name?.ToString());
                object nret = cb(this, frame, args);
                Pool.ReleaseFrame(frame);
                return nret;
            }

            // Bytecode path.
            frame.Tokens = LoadTokens(fn);
            // Import-stub native fallback. A native function referenced across
            // packages (e.g. Object.DynamicLoadObject called from UWindow.u) can be
            // resolved to an IMPORT-STUB UFunction that lost its Native flag — so the
            // native fast-path above skipped it and we arrive here with an empty
            // body. If a native is registered under this name, dispatch to it. (A
            // real script function always has tokens, so this only catches stubs.)
            if (frame.Tokens.Count == 0)
            {
                // ── VM-13: DECLARATION BEFORE NATIVE-BY-NAME ─────────────────
                //
                // The shipped linker binds an import to the REAL export of the same
                // FName in the declaring class (ULinkerLoad::CreateImport), not to
                // "anything with that name". So the stub is first resolved to its
                // declaration: a script with a body runs as script, a native runs by
                // its index (by name only for a native without an index). A by-name
                // native lookup first would hijack shipped scripts with matching names:
                // Object.VSize2D (static final, body `A.Z=0; return VSize(A)`; imported
                // by HPBase/HarryPotter/Hog2/Hub2/Hub5) would go to Pair(271) — HP1 has
                // no index 271; UWindowWindow.TextSize/GetEntryLevel (imports in
                // HPMenu/HPBase) would go to Canvas.TextSize(470) / PlayerPawn.GetEntryLevel.
                UFunction decl = ResolveStubDeclaration(fn);
                if (!ReferenceEquals(decl, fn))
                {
                    if (!decl.FunctionFlags.HasFlag(UELib.Flags.FunctionFlag.Native))
                        { Pool.ReleaseFrame(frame); return Invoke(obj, decl, args); }
                    string dname = decl.Name?.ToString();
                    var dcb = decl.NativeToken > 0 ? Natives.GetByIndex(decl.NativeToken) : Natives.GetByName(dname);
                    if (dcb != null) { NativeCoverageHook?.Invoke(dname); return dcb(this, frame, args); }
                    Natives.OnUnknownNative(decl.NativeToken, dname, $"{obj?.Class?.Name}.{dname} (import stub)");
                    return null;
                }
                // Declaration not found at all (declaring class not loaded by the
                // registry): fall back to native by name, otherwise loud.
                var ncb = Natives.GetByName(fn.Name?.ToString());
                if (ncb != null) { NativeCoverageHook?.Invoke(fn.Name?.ToString()); return ncb(this, frame, args); }
                // Cross-package SCRIPT function resolved to an IMPORT STUB (empty
                // body, flags lost) — e.g. Object.Vec called from HPBase bytecode.
                // Previously this ran 0 tokens and silently returned null (every
                // `Vec(d,0,0) >> Rotation` placement in the game collapsed to
                // zero). Re-resolve the real declaration starting at the stub's
                // DECLARING class (canonicalized) — NOT the object's most-derived
                // class: a `super.Tick()` stub re-resolved by dynamic type binds
                // back to the override itself (CutScene.Tick/HPHud.Tick infinite
                // recursion, stack-overflowed the editor).
                // STATE-function stub (fn.Outer is the UState, not the class) —
                // e.g. HPConsole.state UWindow `super.KeyEvent` → HPBase
                // WindowConsole.state UWindow.KeyEvent. Re-resolve via the
                // canonical declaring class, matching the same-named STATE down
                // the super chain first (UnrealScript state inheritance), then
                // that state's function. Starting at the DECLARING class keeps
                // the no-recursion guarantee above.
                if (fn.Outer is UState stubState && !(fn.Outer is UClass))
                {
                    string stName = stubState.Name?.ToString();
                    string ownerName = (stubState.Outer as UClass)?.Name
                                     ?? stubState.Outer?.Name?.ToString();
                    var ownerCanon = UEClassRegistry.Shared.Find(ownerName);
                    if (ownerCanon != null)
                    {
                        try { ownerCanon.BeginDeserializing(); } catch { }
                        string sfName = fn.Name?.ToString();
                        for (var c = ownerCanon; c != null; c = CanonicalSuper(c))
                            foreach (var st in c.EnumerateFields<UState>())
                            {
                                if (st is UClass) continue;
                                if (!string.Equals(st.Name?.ToString(), stName,
                                                   StringComparison.OrdinalIgnoreCase)) continue;
                                foreach (var f in st.EnumerateFields<UFunction>())
                                    if (string.Equals(f.Name?.ToString(), sfName,
                                                      StringComparison.OrdinalIgnoreCase)
                                     && !ReferenceEquals(f, fn) && LoadTokens(f).Count > 0)
                                        { Pool.ReleaseFrame(frame); return Invoke(obj, f, args); }
                            }
                    }
                }
                string declName = (fn.Outer as UClass)?.Name ?? fn.Outer?.Name?.ToString();
                var declCanon = UEClassRegistry.Shared.Find(declName);
                if (declCanon != null)
                {
                    // UELib materializes a class's Children (functions) lazily —
                    // canonical Core.Object is typically still raw here, making
                    // EnumerateFields empty. Idempotent, cheap after first call.
                    try { declCanon.BeginDeserializing(); } catch { }
                    string fname = fn.Name?.ToString();
                    for (var c = declCanon; c != null; c = CanonicalSuper(c))
                        foreach (var f in c.EnumerateFields<UFunction>())
                            if (string.Equals(f.Name?.ToString(), fname, StringComparison.OrdinalIgnoreCase))
                            {
                                if (!ReferenceEquals(f, fn) && LoadTokens(f).Count > 0)
                                    { Pool.ReleaseFrame(frame); return Invoke(obj, f, args); }
                                // The real declaration is a NATIVE: run its handler
                                // by index; no handler — loud, not an empty
                                // body returning null.
                                if (f.FunctionFlags.HasFlag(UELib.Flags.FunctionFlag.Native))
                                {
                                    var hcb = f.NativeToken > 0 ? Natives.GetByIndex(f.NativeToken) : null;
                                    if (hcb != null) { NativeCoverageHook?.Invoke(fname); return hcb(this, frame, args); }
                                    Natives.OnUnknownNative(f.NativeToken, fname, $"{obj?.Class?.Name}.{fname} (import stub)");
                                    return null;
                                }
                                goto stubUnresolved;
                            }
                }
                // Neither a handler nor a declaration with a body: unresolved import.
                // Measured over 8 maps × 1500 ticks: zero hits; the path is loud.
                Natives.OnUnknownNative(0, fn.Name?.ToString(), $"{obj?.Class?.Name}.{fn.Name} (unresolved import stub, decl={declName})");
                return null;
                stubUnresolved:
                Natives.OnUnknownNative(0, fn.Name?.ToString(), $"{obj?.Class?.Name}.{fn.Name} (import stub resolves to an empty declaration)");
                return null;
            }
            frame.OffsetToIndex = OffsetMapFor(frame.Tokens);
            // AllowValueLocalStructWrites below is set for STATIC functions and
            // restored after the body, but the flag has no reader: writing a member
            // of a value struct is always enabled (EvalLValue, StructMemberToken), as
            // in execStructMember.
            // UObject::ProcessInternal (Core 0x101431a0) — BEFORE ANY script body,
            // not only before an event: (1) IsProbing(FriendlyName) (0x101431df) —
            // a function disabled by ignores/Disable does not run on a direct call
            // `A.Trigger(...)`; (2) singular (FunctionFlags & 0x20, 0x101431bc):
            // if bit 0x20 is set in ObjectFlags the body does not run, otherwise it
            // is set for the body's duration. A skip yields a zero result.
            UEObject self = frame.Object;
            if (self is not null && !IsProbing(self, fn.Name?.ToString())) { Pool.ReleaseFrame(frame); return null; }
            bool singular = self is not null && ((ulong)fn.FunctionFlags & 0x20UL) != 0;
            if (singular)
            {
                if (self.InSingularFunction) { Pool.ReleaseFrame(frame); return null; }
                self.InSingularFunction = true;
            }
            bool savedGate = AllowValueLocalStructWrites;
            if (fn.FunctionFlags.HasFlag(UELib.Flags.FunctionFlag.Static))
                AllowValueLocalStructWrites = true;
            try { Run(frame); }
            finally
            {
                AllowValueLocalStructWrites = savedGate;
                if (singular) self.InSingularFunction = false;
            }
            // CallFunction (Core.dll.c:7715): after the body, out-params are copied
            // to the argument addresses starting from the LAST. Here — copy any OutParm locals
            // back into the caller's args[] slot so the
            // FinalFunction/NativeFunction handler can write through to the
            // caller's lvalue.
            WriteOutParamsBack(frame, fn, args);
            object ret = frame.ReturnValue;
            Pool.ReleaseFrame(frame);
            return ret;
        }

        // Out-param post-call writeback (CallFunction, Core.dll.c:7715: copies the
        // local into the argument's GPropAddr; the "from last" order is kept by
        // the calling handler). For each
        // PropertyFlag.Parm | OutParm field on fn, copy callee's local back
        // into the caller's args[] slot. ReturnParm and non-out params are
        // skipped.
        static void WriteOutParamsBack(UEFrame frame, UFunction fn, object[] args)
        {
            if (args == null || args.Length == 0) return;
            ParamDesc d = ParamsOf(fn);
            int n = Math.Min(d.Names.Length, args.Length);
            for (int i = 0; i < n; i++)
                if (d.IsOut[i])
                    args[i] = frame.Variables.Get(d.KeyIds[i]);
        }

        // The offset map is a pure function of the token LIST (cached per function
        // in ByteCodeManager and immutable after deserialization), so it is built
        // ONCE per function/state, not on every Invoke: building an O(tokens)
        // dictionary per call would be a notable share of hot-script overhead.
        static readonly Dictionary<List<Tok.Token>, Dictionary<int, int>> _offsetMapCache
            = new Dictionary<List<Tok.Token>, Dictionary<int, int>>(UERefComparer<List<Tok.Token>>.Instance);
        static Dictionary<int, int> OffsetMapFor(List<Tok.Token> toks)
        {
            if (!_offsetMapCache.TryGetValue(toks, out var m))
                _offsetMapCache[toks] = m = BuildOffsetMap(toks);
            return m;
        }

        // Build a byte-position → token-index lookup. execJump (Core.dll.c:37072)
        // sets Code = Script + offset; the port stores tokens, so this map translates
        // the offset into a token index (equivalent to the direct address).
        static Dictionary<int, int> BuildOffsetMap(List<Tok.Token> toks)
        {
            var map = new Dictionary<int, int>(toks.Count);
            for (int i = 0; i < toks.Count; i++) map[toks[i].Position] = i;
            return map;
        }

        // Decode the function body via UELib if not already cached. UELib's
        // ByteCodeManager.Deserialize fills DeserializedTokens with the flat
        // pre-order token stream.
        internal static List<Tok.Token> LoadTokens(UStruct node)
        {
            if (node?.ByteCodeManager == null) return new List<Tok.Token>();
            if (node.ByteCodeManager.DeserializedTokens == null
             || node.ByteCodeManager.DeserializedTokens.Count == 0)
                node.ByteCodeManager.Deserialize();
            return node.ByteCodeManager.DeserializedTokens ?? new List<Tok.Token>();
        }

        // A "bare return" body: a non-empty token stream consisting entirely of
        // Return/Nothing/terminator. Zero tokens is NOT empty in this sense —
        // that is an import stub with its own fallback route in Invoke.
        // Internal: the hot Tick dispatch site in UELevelRuntime asks this BEFORE
        // building the closure/argument array — an empty tick must cost zero
        // allocations (2147 actors per tick, Lev_Tut1b).
        static readonly Dictionary<UFunction, bool> _emptyBody = new(UERefComparer<UFunction>.Instance);
        internal static bool IsEmptyScriptBody(UFunction fn)
            => fn is not null
               && !fn.FunctionFlags.HasFlag(UELib.Flags.FunctionFlag.Native)
               && IsEmptyBody(fn);
        static bool IsEmptyBody(UFunction fn)
        {
            if (_emptyBody.TryGetValue(fn, out bool cached)) return cached;
            List<Tok.Token> toks = LoadTokens(fn);
            bool empty = toks.Count > 0;
            foreach (Tok.Token t in toks)
            {
                string n = t.GetType().Name;
                if (n != "ReturnToken" && n != "NothingToken"
                    && n != "EndOfScriptToken" && n != "EndFunctionToken")
                { empty = false; break; }
            }
            _emptyBody[fn] = empty;
            return empty;
        }

        // UObject::ProcessInternal (Core.dll.c:21624): Step until EX_Return; state
        // code — the AActor::ProcessState loop (Engine.dll.c:45801). Drives
        // statements until Return/Stop or end-of-body. Per-statement evaluation
        // comes from EvalNext (≙ FFrame::Step).
        // Runaway counter (DAT_101e9588). Shipped increments it in execJump and
        // execJumpIfNot (disasm 0x10132f30..0x10132f76, Core.dll.c:37072 / 45428):
        // ++ on EVERY jump, > 10 000 000 (cmp 0x989680) -> Stack.Logf(0x88, ...);
        // FFrame::Serialize sends event 0x88 (NAME_Critical) to GError — fatal
        // (-norunaway, never on the shipped command line, only skips the Logf).
        // GInitRunaway (Core.dll.c:31964) zeroes it at the start of ULevel::Tick
        // (Engine.dll.c:262584) — AdvanceLevelClock here. One global counter, as in
        // shipped (fatal like the original, D-13).
        const int RunawayLimit = 10_000_000;
        static int _runawayJumps;
        public static void InitRunaway() => _runawayJumps = 0;
        public static int RunawayJumpsForTest => _runawayJumps;
        internal void CountRunawayJump(UEFrame frame)
        {
            if (++_runawayJumps > RunawayLimit)
                throw new InvalidOperationException(
                    $"Runaway loop detected (over {RunawayLimit} iterations) in {frame.Object?.Class?.Name}.{frame.Func?.Name}"
                    + $" (state '{frame.Object?.CurrentStateName}'); chain: {FrameStackText()} — "
                    + "shipped: FFrame::Logf(NAME_Critical) -> GError, fatal");
        }
        // UObject::ProcessInternal (Core.dll.c:21655-21657): ++DAT_101e958c per script call,
        // > 250 (0xfa) -> Stack.Logf(0x88) -> GError, fatal. Must trip BEFORE the C# stack
        // overflows (each script call is dozens of interpreter frames deep).
        const int RecurseLimit = 250;
        int _runDepth;

        // ── CALL CHAIN AT THE MOMENT OF A TRIP ──────────────────────────────
        //
        // WHY. The runaway message names the whole chain, not only the innermost frame — the one
        // where the counter hit the limit. That is not the culprit: the 10M
        // operations may have accumulated further up the chain (e.g. a report of
        // "Runaway loop in GameInfo.SetGameSpeed", an Engine.u function that loops
        // over nothing). The chain identifies the real caller.
        //
        // COST. One reference store per script entry; formatting happens only at
        // the trip. Depth is bounded by the same RecurseLimit, beyond which
        // execution never goes.
        readonly UEFrame[] _frameStack = new UEFrame[RecurseLimit + 8];

        string FrameStackText()
        {
            var sb = new System.Text.StringBuilder();
            int depth = _runDepth < _frameStack.Length ? _runDepth : _frameStack.Length;
            for (int i = 0; i < depth; i++)
            {
                UEFrame f = _frameStack[i];
                if (f is null) continue;
                if (sb.Length > 0) sb.Append(" <- ");
                sb.Append(f.Object?.Class?.Name).Append('.').Append(f.Func?.Name);
                string st = f.Object?.CurrentStateName;
                if (!string.IsNullOrEmpty(st)) sb.Append('[').Append(st).Append(']');
            }
            return sb.Length > 0 ? sb.ToString() : "(chain empty)";
        }

        // Judge tap: sees every top-level statement before and after it runs; observes only,
        // never changes VM state. Null outside the SwARc corpus dump.
        public interface IStatementTap
        {
            object Before(UEFrame frame);
            void After(UEFrame frame, object state, UEEvalResult result);
        }
        public static IStatementTap StatementTap;

        public void Run(UEFrame frame) => Run(frame, frame.Object?.CurrentStateName);

        // stateNodeBeforePoll: the StateNode baseline of the state-code 5-change cap.
        // AActor::ProcessState takes it BEFORE the latent poll (0x1040ef77
        // `mov ebx,[eax+0x1c]`, poll at 0x1040efb7), so a GotoState fired from inside
        // the poll counts as a change after the first statement. Unused for function frames.
        public void Run(UEFrame frame, string stateNodeBeforePoll)
        {
            if (UEVmPool.Verify && frame.PoolReleased) UEVmPool.LateRead(frame, "Run");
            _runDepth++;
            if (_runDepth <= _frameStack.Length) _frameStack[_runDepth - 1] = frame;
            if (_runDepth > RecurseLimit)
            {
                _runDepth--;
                throw new InvalidOperationException(
                    $"Infinite script recursion ({RecurseLimit} calls) detected in " +
                    $"{frame.Object?.Class?.Name}.{frame.Func?.Name}" +
                    $" (state '{frame.Object?.CurrentStateName}') — shipped: FFrame::Logf(NAME_Critical) -> GError, fatal");
            }
            bool prevInRun = frame.InRun;
            frame.InRun = true;
            try
            {
            // STATE code: the AActor::ProcessState loop (Engine 0x1040efba..0x1040f00e)
            // checks bDeleteMe (+0x28 & 0x200) before each statement and exits;
            // after a statement it counts StateNode changes and aborts on the fifth.
            UEObject stateOwner = ReferenceEquals(frame, frame.Object?.StateFrame) ? frame.Object : null;
            string stateNode = stateNodeBeforePoll;
            int stateChanges = 0;
            while (frame.Cursor < frame.Tokens.Count)
            {
                if (stateOwner is not null && stateOwner.Destroyed) return;
                var stmtTok = frame.Tokens[frame.Cursor];
                var tap = StatementTap;
                object tapState = tap?.Before(frame);
                var result = EvalNext(frame);
                if (tapState != null) tap.After(frame, tapState, result);
                // GotoState(None) (Core.dll.c:58247): StateNode = class, Code =
                // NULL — the ProcessState loop halts; here a statement may have
                // dropped all states (Func = null) — the body aborts immediately.
                if (frame.Func == null) return;
                // A latent native (Sleep/MoveToward/...) sets LatentState on
                // this frame and we suspend immediately, preserving Cursor.
                // TickStateFrame resumes once the latent completes. ProcessState
                // (Engine.dll.c:45801) steps only while LatentAction == 0.
                if (frame.LatentState != LatentRunState.Continue) return;
                if (stateOwner is not null
                 && !string.Equals(stateOwner.CurrentStateName, stateNode, StringComparison.OrdinalIgnoreCase))
                {
                    stateNode = stateOwner.CurrentStateName;
                    if (++stateChanges > 4) return;
                }
                switch (result.Result)
                {
                    case UEStmtResult.Next:
                        continue;
                    case UEStmtResult.Return:
                        frame.ReturnValue = result.Value;
                        return;
                    case UEStmtResult.Stop:
                        frame.LatentState = LatentRunState.Stop;
                        return;
                    case UEStmtResult.Jump:
                        if (frame.OffsetToIndex != null
                         && frame.OffsetToIndex.TryGetValue(result.JumpAddress, out var idx)) {
                            frame.Cursor = idx;
                            continue;
                        }
                        UETrace.Error(
                            $"[VM] Jump to unknown offset 0x{result.JumpAddress:X} in " +
                            $"{frame.Object?.Class?.Name}.{frame.Func?.Name}");
                        return;
                    case UEStmtResult.Switch:
                        ProcessSwitch(frame, result.Value);
                        if (UECoverage.On && stmtTok is Tok.SwitchToken && frame.Cursor < frame.Tokens.Count)
                            UECoverage.Hit(frame.Func, stmtTok.Position, frame.Tokens[frame.Cursor].Position);
                        continue;
                    default:
                        // Switch/GotoLabel/Iterator land in subsequent commits.
                        UETrace.Error($"[VM] Run: unhandled StmtResult={result.Result} " +
                                       $"in {frame.Object?.Class?.Name}.{frame.Func?.Name}");
                        return;
                }
            }
            }
            finally
            {
                if (_runDepth <= _frameStack.Length) _frameStack[_runDepth - 1] = null;
                _runDepth--;
                frame.InRun = prevInRun;
            }
        }

        // Evaluate one expression starting at the cursor. FFrame::Step
        // (Core.dll.c:39376): GNatives[*Code++]; we dispatch on UELib's token type.
        // DebugInfoTokens are skipped (UELib emits them between statements;
        // Token.cs:55 skips them too). The skip is NOT verified against shipped:
        // Core.dll has no execDebugInfo export; if opcode 0x41 occurs in HP1
        // bytecode, the GNatives[0x41] body must be checked in the GNatives table disasm.
        public UEEvalResult EvalNext(UEFrame frame)
        {
            if (UEVmPool.Verify && frame.PoolReleased) UEVmPool.LateRead(frame, "EvalNext");
            while (frame.Cursor < frame.Tokens.Count && IsDebugInfoAt(frame, frame.Cursor))
                frame.Cursor++;

            if (frame.Cursor >= frame.Tokens.Count) {
                UETrace.Error($"[VM] EvalNext: unexpected end of bytecode in " +
                               $"{frame.Object?.Class?.Name}.{frame.Func?.Name}");
                return new UEEvalResult { Result = UEStmtResult.Return };
            }

            int idx = frame.Cursor++;
            var tok = frame.Tokens[idx];
            var hc = frame.HandlerCache;
            if (hc is null || !ReferenceEquals(frame.HandlerOwner, this))
            {
                hc = HandlersFor(frame.Tokens);
                frame.HandlerCache = hc;
                frame.HandlerOwner = this;
            }
            var handler = hc[idx];
            if (handler is null && _handlers.TryGetValue(tok.GetType(), out handler)) hc[idx] = handler;
            if (handler is not null)
                return handler(this, frame, tok);

            string noHandler = $"[VM] No handler for token {tok.GetType().Name} " +
                               $"in {frame.Object?.Class?.Name}.{frame.Func?.Name} " +
                               $"at pos {tok.Position}";
            if (ThrowOnMissingScript) throw new NotSupportedException(noHandler);
            UETrace.Error(noHandler);
            return new UEEvalResult { Result = UEStmtResult.Return };
        }

        // Function PARAMETER DESCRIPTION — names and the out flag, in declaration
        // order, with the same filter (Parm and not ReturnParm) as the three
        // consumers below. Computed ONCE per function.
        //
        // Why: otherwise EVERY call enumerated the declaration fields three times
        // (argument collection, parameter binding, out writeback) and called
        // Name.ToString() three times — the bulk of tick-event allocations (GC
        // stalls under Mono). Declaration data is immutable after package load,
        // so the cache never goes stale.
        sealed class ParamDesc
        {
            public readonly string[] Names;
            public readonly int[] KeyIds;     // UEPropKeys.Id(Names[i])
            public readonly bool[] IsOut;
            public readonly bool AnyOut;      // Array.IndexOf(IsOut, true) >= 0
            public ParamDesc(string[] names, bool[] isOut)
            {
                Names = names; IsOut = isOut; AnyOut = Array.IndexOf(isOut, true) >= 0;
                KeyIds = Array.ConvertAll(names, UEPropKeys.Id);
            }
        }
        static readonly Dictionary<UFunction, ParamDesc> _paramDescs = new(UERefComparer<UFunction>.Instance);

        static ParamDesc ParamsOf(UFunction fn)
        {
            if (_paramDescs.TryGetValue(fn, out ParamDesc d)) return d;
            var names = new List<string>();
            var outs = new List<bool>();
            foreach (var p in fn.EnumerateFields<UProperty>())
            {
                if (!p.PropertyFlags.HasFlag(UELib.Flags.PropertyFlag.Parm)) continue;
                if (p.PropertyFlags.HasFlag(UELib.Flags.PropertyFlag.ReturnParm)) continue;
                names.Add(p.Name.ToString());
                outs.Add(p.PropertyFlags.HasFlag(UELib.Flags.PropertyFlag.OutParm));
            }
            d = new ParamDesc(names.ToArray(), outs.ToArray());
            _paramDescs[fn] = d;
            return d;
        }

        // CallFunction (Core.dll.c:7715): parameters along the Children/Next chain
        // are written to locals at Offset. Here — picks UProperty fields flagged Parm (excluding
        // ReturnParm), writes the corresponding args[] slot into Variables.
        void BindParams(UEFrame frame, UFunction fn, object[] args)
        {
            int[] keys = ParamsOf(fn).KeyIds;
            int n = Math.Min(keys.Length, args.Length);
            var vars = frame.Variables;
            // CallFunction copies the parameter value into frame memory (CopyCompleteValue): a struct or array
            // passed by value is the callee's OWN copy (an out-param is written back separately).
            for (int i = 0; i < n; i++)
                vars.Set(keys[i], UEValue.CopyValue(args[i]));
        }

        // UELib parses a function/state's LabelTable (name → code offset) into
        // a private `_Labels` list on the ByteCodeManager but does not expose
        // it. We read that already-computed data via reflection (not faking
        // behaviour — surfacing parsed metadata) and cache per UStruct.
        // ULabelEntry = { string Name; int Position }.
        static readonly Dictionary<UStruct, Dictionary<string, int>> _labelCache = new(UERefComparer<UStruct>.Instance);
        static System.Reflection.FieldInfo _labelsField, _labelNameField, _labelPosField;

        // Resolved property access for token `tok` that EvalNext/EvalLValue just
        // took at index frame.Cursor-1. The cache lives on the function's token
        // stream (shared by all its frames).
        static readonly Dictionary<List<Tok.Token>, UEPropRef[]> _propRefs =
            new(UERefComparer<List<Tok.Token>>.Instance);
        readonly Dictionary<EventFnKey, UFunction> _eventFnCache = new();
        readonly Dictionary<EventFnKey, UFunction> _globalFnCache = new();

        // `default.X` lookup — the default value of property X for a class.
        // Cached per class via a throwaway defaults object (LoadDefaults applies
        // the class's defaultproperties + static-array zero-init exactly like a
        // real instance's init). Used by DefaultVariableToken.
        readonly Dictionary<UClass, UEObject> _classDefaults = new(UERefComparer<UClass>.Instance);
    }
}
