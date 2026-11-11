// UnrealScript VM.
//
// Runtime object model (UEObject), execution frame (UEFrame), and the
// interpreter (UEInterpreter). Token handlers are verified against the exec*
// bodies of Core.dll HP1 1.53. Bytecode parsing is UELib
// (UELib/Core/Tokens/*.cs). SurrealEngine is not a source: only the original binaries and scripts are.
//
// Current behaviour is documented at the handlers below; Location/Rotation
// reads through the frontend hook are at InstanceVariableToken.

using System;
using System.Collections.Generic;
using UELib;
using UELib.Core;
using Tok = UELib.Core.UStruct.UByteCodeDecompiler;
using UELib.Tokens;  // CastToken enum (UnrealTokens.cs)
using HP1.Core;      // UETrace sinks + UEClassRegistry
// Script vectors ARE the Core value type: prop-bag storage, struct decode,
// literals and the As*-surface all use HP1.UEScript.UEVector (bit-parity
// mirror of UnityEngine.Vector3 — probed member-for-member). Frontend/bridge
// code below the movement-driver boundary stays on UnityEngine.Vector3 and
// converts via UEVectorInterop exactly at prop reads/writes.
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{



    // AActor::execAllActors (Engine.dll.c:109111, body 0x1040d780): re-reads
    // Actors.Num() on every step (0x1040d847/0x1040d8a8) — an actor spawned in the
    // loop body is yielded once, when the walk reaches the end of the array; an
    // empty slot (destroyed actor) is skipped; class and tag filters apply at the
    // moment of yielding. Port: _items is the level's live Actors list (not the
    // spawn log, UELevelRuntime.VisibilityNatives.cs), filters in MoveNext.
    public sealed class UEAllActorsIterator : UEIterator
    {
        readonly List<UEObject> _items;
        readonly UClass _baseClass;           // null → filter off
        readonly string _tag;                 // null → filter off (Tag: NameString.IsNone())
        int _index;
        public UEAllActorsIterator(List<UEObject> items, UClass baseClass = null, string tag = null)
        {
            _items = items; _baseClass = baseClass; _tag = tag;
        }
        // execAllActors filter (Engine.dll.c:109178): class along the SuperField chain
        // (class identity), `MatchTag == None || Tag == MatchTag` (FName,
        // case-insensitive); an empty slot / Destroyed is not yielded.
        internal static bool Matches(UEObject o, UClass baseClass, string tag)
        {
            if (o?.Class == null || o.Destroyed) return false;
            if (baseClass != null && !UEClassRegistry.Shared.IsChildOfClass(o.Class, baseClass)) return false;
            if (tag != null) {
                var at = o.GetProp(PK.Tag);
                string atName = at is UEName un ? un.Value : at?.ToString();
                if (string.IsNullOrEmpty(atName)
                 || !string.Equals(atName, tag, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }
        public override bool MoveNext(UEInterpreter interp, UEFrame frame)
        {
            if (_items != null)
                while (_index < _items.Count) {
                    var o = _items[_index++];
                    if (!Matches(o, _baseClass, _tag)) continue;
                    interp.WriteLValue(frame, LoopVar, o);
                    return true;
                }
            interp.WriteLValue(frame, LoopVar, null);   // SE: *ReturnValue = nullptr
            return false;
        }
    }

    // AActor::execVisibleCollidingActors (Engine 0x1040e960): the candidate list is
    // prepared upfront, filtering happens on each step, destroyed ones are not culled.
    public sealed class UEVisibleCollidingIterator : UEIterator
    {
        readonly List<UEObject> _items;
        readonly Func<UEObject, bool> _accept;
        int _index;
        public UEVisibleCollidingIterator(List<UEObject> items, Func<UEObject, bool> accept)
        { _items = items; _accept = accept; }
        public override bool MoveNext(UEInterpreter interp, UEFrame frame)
        {
            while (_index < _items.Count)
            {
                var o = _items[_index++];
                if (o is null || !_accept(o)) continue;
                interp.WriteLValue(frame, LoopVar, o);
                return true;
            }
            interp.WriteLValue(frame, LoopVar, null);
            return false;
        }
    }

    // AActor::execRadiusActors (Engine.dll.c:20960-21089). The shipped iterator
    // does NOT snapshot its matches the way SE's RadiusActorsIterator does: it
    // keeps the Level.Actors scan index across passes and re-reads Actors.Num()
    // and each candidate's current Location, so an actor spawned or moved by the
    // loop body is seen when the walk reaches it. TheLocation and Radius are read
    // once, before the loop. A Destroyed actor is skipped because shipped
    // ULevel::DestroyActor nulls the Actors slot synchronously (Engine.dll.c:
    // 253086) whereas this port compacts the list at the end of the tick pass.
    public sealed class UERadiusActorsIterator : UEIterator
    {
        readonly List<UEObject> _actors;
        readonly UClass _baseClass;          // null → shipped substitutes AActor
        readonly Vector3 _location;
        readonly float _radius;
        int _index;
        public UERadiusActorsIterator(List<UEObject> actors, UClass baseClass,
                                      float radius, Vector3 location)
        {
            _actors = actors; _baseClass = baseClass;
            _radius = radius; _location = location;
        }
        public static bool RadiusTest(Vector3 loc, Vector3 at, float radius, float collisionRadius)
        {
            static HP1.Core.X87Ext Sub(float a, float b) => HP1.Core.X87Ext.Round64(HP1.Core.X87Ext.From(a) - HP1.Core.X87Ext.From(b));
            static HP1.Core.X87Ext Sq(HP1.Core.X87Ext v) => HP1.Core.X87Ext.Round64(v * v);
            // NaN and ±inf flow through X87Ext as through the FPU; `fcompp` (st0 = r², st1 = sum), `test ah,0x41; jne skip`.
            var dx = Sub(loc.x, at.x); var dy = Sub(loc.y, at.y); var dz = Sub(loc.z, at.z);
            var r = HP1.Core.X87Ext.Round64(HP1.Core.X87Ext.From(radius) + HP1.Core.X87Ext.From(collisionRadius));
            var sum = HP1.Core.X87Ext.Round64(HP1.Core.X87Ext.Round64(Sq(dz) + Sq(dy)) + Sq(dx));
            return (HP1.Core.X87Ext.Fcom(Sq(r), sum) & 0x41) == 0;
        }
        public override bool MoveNext(UEInterpreter interp, UEFrame frame)
        {
            while (_index < _actors.Count)
            {
                UEObject actor = _actors[_index++];
                if (actor?.Class == null || actor.Destroyed) continue;
                if (_baseClass != null
                 && !UEClassRegistry.Shared.IsChildOfClass(actor.Class, _baseClass)) continue;
                // Distance test, disasm 0x1040e4a3..0x1040e4ee (PC64, all in st0): dx/dy/dz = Loc − TheLocation,
                // r = Radius + CollisionRadius, kept iff (dz² + dy²) + dx² < r² (fcompp; test ah,0x41 skips <= and NaN).
                Vector3 loc = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
                if (!RadiusTest(loc, _location, _radius, UEInterpreter.AsFloat(actor.GetProp(PK.CollisionRadius)))) continue;
                interp.WriteLValue(frame, LoopVar, actor);
                return true;
            }
            interp.WriteLValue(frame, LoopVar, null);
            return false;
        }
    }

    // AActor::execVisibleActors (Engine.dll.c:75415-75530). Same lazy walk of the
    // live Level.Actors as RadiusActors — the scan index survives each pass and
    // Actors.Num() is re-read — with three additions from the shipped body:
    // hidden actors are skipped, the radius is OPTIONAL in the strong sense (a
    // zero Radius performs no distance test at all, it does not mean "radius
    // zero"), and each survivor must be reachable by a pure BSP line check from
    // the query location — from Loc itself, NOT from an eye height.
    public sealed class UEVisibleActorsIterator : UEIterator
    {
        readonly List<UEObject> _actors;
        readonly UClass _baseClass;
        readonly Vector3 _location;
        readonly float _radius;
        readonly Func<Vector3, Vector3, bool> _lineClear;
        int _index;
        public UEVisibleActorsIterator(List<UEObject> actors, UClass baseClass,
            float radius, Vector3 location, Func<Vector3, Vector3, bool> lineClear)
        {
            _actors = actors; _baseClass = baseClass;
            _radius = radius; _location = location; _lineClear = lineClear;
        }
        public override bool MoveNext(UEInterpreter interp, UEFrame frame)
        {
            while (_index < _actors.Count)
            {
                UEObject actor = _actors[_index++];
                if (actor?.Class == null || actor.Destroyed) continue;
                if (UEInterpreter.ToBool(actor.GetProp(PK.bHidden))) continue;
                if (_baseClass != null
                 && !UEClassRegistry.Shared.IsChildOfClass(actor.Class, _baseClass)) continue;
                Vector3 actorLocation = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
                // execVisibleActors 0x1040e76f..0x1040e7be: Radius != 0 -> (dz² + dy²) + dx² < Radius² in st0 (PC64).
                if (_radius != 0f && !UERadiusActorsIterator.RadiusTest(actorLocation, _location, _radius, 0f)) continue;
                if (!_lineClear(actorLocation, _location)) continue;
                interp.WriteLValue(frame, LoopVar, actor);
                return true;
            }
            interp.WriteLValue(frame, LoopVar, null);
            return false;
        }
    }

    // AActor::execTraceActors (Engine.dll.c:122076, loop 0x1040e192..0x1040e226):
    // MultiLineCheck results in list order; there is no IsA(BaseClass) check and no
    // Self exclusion in the loop — disasm 0x1040e192..0x1040e224: writes
    // Actor/HitLocation/HitNormal from FCheckResult, then only Steps the body via
    // GNatives up to 0x30/0x31 (TraceActorsNative does not apply them either).
    // Each pass writes three out-params — Actor, HitLocation, HitNormal (OutRefs
    // in argument order, non-null slots). Exhaustion — Actor = None, false. Zero-length
    // ray — not established.
    public sealed class UETraceActorsIterator : UEIterator
    {
        public struct Hit { public UEObject Actor; public Vector3 HitLoc; public Vector3 HitNorm; }
        readonly List<Hit> _hits;
        int _index;
        public UETraceActorsIterator(List<Hit> hits) { _hits = hits; }
        // Number of matches. The frontend needs it for a log line: Core
        // (TraceActorsNative) builds the list itself, and recounting it in the
        // frontend would create a second count of the same query.
        public int Count => _hits?.Count ?? 0;
        public override bool MoveNext(UEInterpreter interp, UEFrame frame)
        {
            // Out-params in arg order: 1st=Actor, 2nd=HitLocation, 3rd=HitNormal.
            UELValueRef? aRef = LoopVar, locRef = null, normRef = null;
            if (OutRefs != null) {
                int seen = 0;
                for (int i = 0; i < OutRefs.Count; i++) {
                    if (!OutRefs[i].HasValue) continue;
                    if (seen == 0) aRef = OutRefs[i];
                    else if (seen == 1) locRef = OutRefs[i];
                    else if (seen == 2) normRef = OutRefs[i];
                    seen++;
                }
            }
            if (_hits != null && _index < _hits.Count) {
                var h = _hits[_index++];
                if (aRef.HasValue)    interp.WriteLValue(frame, aRef.Value,    h.Actor);
                if (locRef.HasValue)  interp.WriteLValue(frame, locRef.Value,  h.HitLoc);
                if (normRef.HasValue) interp.WriteLValue(frame, normRef.Value, h.HitNorm);
                return true;
            }
            if (aRef.HasValue) interp.WriteLValue(frame, aRef.Value, null);
            return false;
        }
    }


    public delegate UEEvalResult UETokenHandler(UEInterpreter interp, UEFrame frame, Tok.Token tok);

    public class UEInterpreter
    {
        // The frontend's native table (UENatives / HeadlessNatives). Assigned
        // at bootstrap (UEScriptRuntime ctor / headless smoke): registration
        // and every handler body live with the frontend, the Core only
        // resolves by index/name and reports unknowns.
        public IUENativeRegistry Natives;

        // The Core-owned ULevel.Actors list (review #12 P1): the world's
        // actor set in its ORIGINAL stable order, filled by the level loader.
        // Frame building enumerates THIS — never a frontend scene graph (a
        // Core actor without a frontend proxy must still be in the frame,
        // and scene order is not the authoritative actor order).
        public System.Collections.Generic.List<UEObject> LevelActors;

        // Review #5 P0: in the headless core a compiled call that resolves to
        // NOTHING (no state/class function, no registered native) or a token
        // with no handler must STOP the scenario — logging + null made a
        // byte-identical route compatible with missing subtitles/warnings.
        // UEGameEngine.Boot sets true; false only for fixtures that build the interpreter by hand.
        public bool ThrowOnMissingScript;
        // Diagnostics-only: native-dispatch coverage hook (headless native log and SelfTest fixtures
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
        // is only written and cleared; the runtime_poc/SelfTest.cs fixtures read it.
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
        // check fails (e.g. APawn::Mount rejecting ALL wall hits at the third gate).
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

        // Handlers by UELib token type; semantics follow the Core.dll exec* bodies.
        void RegisterTokenHandlers()
        {
            // EX_Return 0x04: ProcessInternal (Core.dll.c:21624) steps statements
            // until opcode 0x04, then the Step of the return expression writes Result.
            // Here — evaluate the child expression (HP1 packages always have one) and
            // return Result=Return.
            Register<Tok.ReturnToken>((interp, frame, tok) => {
                var inner = interp.EvalNext(frame);
                return new UEEvalResult { Value = inner.Value, Result = UEStmtResult.Return };
            });

            // execEatString (Core.dll.c:42021): evaluate a string expression and
            // discard the value (a call whose string
            // return is unused, e.g. FEBook.OpenBook's ConsoleCommand("Snap 3")).
            // Missing handler aborted OpenBook at pos 0 → the menu page never
            // changed and curBackground stayed the zero bag.
            Register<UELib.Core.EatStringToken>((interp, frame, tok) => {
                interp.EvalNext(frame);
                return new UEEvalResult { Result = UEStmtResult.Next };
            });

            // execNothing (Core.dll.c:31278): empty body. Here — value null,
            // Result=Next. Child expression of `return;` (void) and optional slots.
            Register<Tok.NothingToken>((interp, frame, tok) =>
                new UEEvalResult { Value = null, Result = UEStmtResult.Next });

            // A skipped optional argument. UObject::CallFunction (Core.dll.c:7715)
            // zeroes Locals before stepping arguments, so in shipped such a parameter
            // is the ZERO of its type, not a "default value". UELib emits
            // EmptyParmToken (OtherTokens.cs:31) for an explicitly skipped middle
            // optional; the value is the same as NothingToken (null). HP1 v76 usually
            // omits trailing arguments; registered so that a skip in the middle
            // does not give "No handler".
            Register<Tok.EmptyParmToken>((interp, frame, tok) =>
                new UEEvalResult { Value = null, Result = UEStmtResult.Next });

            // execFinalFunction (Core.dll.c:52837): a non-virtual call of a known
            // UFunction → this->CallFunction, where this is the current context
            // object; arguments in stream order (CallFunction maps to Invoke here).
            //
            // Layout (UELib FunctionTokens.cs:166-176): UFunction reference, then
            // argument expressions up to EndFunctionParmsToken (0x16) — consumed by
            // EvalNext up to that terminator.
            //
            // && / || (130/132) — natives with EX_Skip (execAndAnd_BoolBool /
            // execOrOr_BoolBool), no separate path needed here.
            // execLocalVariable (Core.dll.c:65110): GPropAddr = Locals + Offset.
            // The port stores locals and parameters in the frame layout (slots,
            // BindParams from the caller's arguments); reads go by slot; writes go
            // through EvalLValue.
            //
            // UELib FieldTokens.cs:48-51 — LocalVariableToken inherits
            // FieldToken (cs:10-26); its Object is the resolved UProperty.
            Register<Tok.LocalVariableToken>((interp, frame, tok) => {
                var ft = (Tok.LocalVariableToken)tok;
                var pr = PropRefAt(frame, tok, ft.Object);
                string name = pr.Name;
                if (string.IsNullOrEmpty(name)) {
                    UETrace.Error(
                        $"[VM] LocalVariableToken with unresolved Object in " +
                        $"{frame.Object?.Class?.Name}.{frame.Func?.Name} at pos {tok.Position}");
                    return new UEEvalResult { Result = UEStmtResult.Next };
                }
                var vars = frame.Variables;
                object val = pr.PropId >= 0 ? vars.GetSlot(vars.Layout.SlotForProp(pr.PropId)) : vars.Get(pr.NameKeyId);
                return new UEEvalResult { Value = val, Result = UEStmtResult.Next };
            });

            // execInstanceVariable (Core.dll.c:61661): GPropAddr = this + Offset,
            // where this is the current context object (not Self: in a chain A.B the
            // context while evaluating B is A). The port stores properties in the
            // UEObject layout slots (the analogue of the UProperty offset).
            //
            // UELib FieldTokens.cs:43-46 — InstanceVariableToken : FieldToken;
            // Object is the resolved UProperty.
            Register<Tok.InstanceVariableToken>((interp, frame, tok) => {
                var ft = (Tok.InstanceVariableToken)tok;
                var pr = PropRefAt(frame, tok, ft.Object);
                string name = pr.Name;
                if (string.IsNullOrEmpty(name)) {
                    UETrace.Error(
                        $"[VM] InstanceVariableToken with unresolved Object in " +
                        $"{frame.Object?.Class?.Name}.{frame.Func?.Name} at pos {tok.Position}");
                    return new UEEvalResult { Result = UEStmtResult.Next };
                }
                object val = null;
                var ctx = frame.Context;
                if (ctx != null)
                {
                    // Property slot in the object layout — like the UProperty offset.
                    var props = ctx.Props;
                    val = pr.PropId >= 0
                        ? props.GetSlot(props.Layout.SlotForProp(pr.PropId))
                        : props.Get(UEObject.PropKey(ctx.Class, PropDeclClass(ft.Object), name));
                }
                if (val == null && pr.Special != UEPropRef.None)
                {
                    // Global singleton fallback: HP1 reads `PlayerHarry` bare as an
                    // instance var on many classes; UE1 assigns it at runtime. We never
                    // run that assignment, so resolve it to the registered player bridge
                    // object (set by HP1PlayerHarryBridge) when the prop isn't present.
                    if (pr.Special == UEPropRef.PlayerHarry)
                        val = interp.PlayerHarry;
                    // UE1's engine assigns `Level` (the world LevelInfo) to every actor
                    // at load — we never run that, so resolve a bare `Level` read to the
                    // bootstrapped world (carries NetMode + Game→GameInfo→BaseMutator so
                    // Actor.PreBeginPlay's relevance cull runs faithfully). Same pattern
                    // as PlayerHarry above.
                    else if (pr.Special == UEPropRef.Level)
                        val = interp.GetLevelInfo();
                    // Object.Class is a UObject property (+0x24), not stored in the
                    // property bag: reads as the object's class. Without
                    // this, `X.Class == Class'Pkg.Y'` read null and every spell-type
                    // check (tut1Peeves.TakeSpellEffect etc.) silently failed.
                    else if (pr.Special == UEPropRef.ClassProp)
                        val = ctx?.Class;
                    // Actor.XLevel (+0x80, const transient) — the actor's ULevel; set by the engine at spawn/load,
                    // script only reads it. The only reader in campaign bytecode is WarpZoneInfo.ForceGenerate
                    // (`OtherSideLevel = XLevel`), and OtherSideLevel is only compared with None (Generate,
                    // OccludeBsp 0x10b23e69). So a non-null Level-class object per world (XLevel) suffices.
                    else if (pr.Special == UEPropRef.XLevel)
                        val = interp.XLevel;
                }
                return new UEEvalResult { Value = val, Result = UEStmtResult.Next };
            });

            // UELib FieldTokens.cs:64 DefaultVariableToken : FieldToken — `default.X`
            // reads the CLASS default value of property X (not the instance value).
            // Class = frame.ClassContext when inside `SomeClass.default.X`
            // (ClassContextToken), else the current Context/Object's class.
            // E.g. SpellLearnTrigger: `Spell.default.Gesture`; baseHarry.SetMesh.
            Register<Tok.DefaultVariableToken>((interp, frame, tok) => {
                var ft = (Tok.DefaultVariableToken)tok;
                string name = ft.Object?.Name?.ToString();
                var cls = frame.ClassContext ?? frame.Context?.Class ?? frame.Object?.Class;
                // `<class>.default.Class` — the default object's intrinsic Class IS the class
                // itself. GetClassDefault has no "Class" entry (it's not a serialized default),
                // so it returned null and `Spell.default.Class == Class'HPBase.spellFlip'`
                // (SpellLearnTrigger.TeacherDrawsSpell) was always false → Quirrell never played
                // the demo anim (bug: teacher doesn't animate when drawing the symbol).
                if (string.Equals(name, "Class", StringComparison.OrdinalIgnoreCase))
                    return new UEEvalResult { Value = cls, Result = UEStmtResult.Next };
                return new UEEvalResult { Value = interp.GetClassDefault(cls,
                    UEObject.PropKey(cls, PropDeclClass(ft.Object), name)), Result = UEStmtResult.Next };
            });

            // UELib ContextTokens.cs:77 ClassContextToken : ContextToken —
            // `<classExpr>.member` where the context is a CLASS (e.g.
            // `Spell.default.Gesture`, `class'X'.SomeConst`). Set frame.ClassContext
            // for the inner expr (consumed by DefaultVariableToken/const reads),
            // then restore. The inner expression also runs with the class's DEFAULT
            // OBJECT as Context: execClassContext 0x10132117 loads ecx =
            // [class+0x4dc] (Defaults.GetData()) as `this` for the GNatives call, so
            // `Message.static.ClientReceive(...)` (PlayerPawn.ReceiveLocalizedMessage)
            // resolves on the message class, not on the caller.
            Register<Tok.ClassContextToken>((interp, frame, tok) => {
                var objR = interp.EvalNext(frame);
                // execClassContext (Core 0x10132141): class None — "Accessed None",
                // the inner expression is SKIPPED by SkipSize, result zero (as in
                // ContextToken); it must not run with `default.Y` read from the
                // caller's class.
                if (objR.Value is null)
                {
                    string anKey = $"{frame.Object?.Class?.Name}.{frame.Func?.Name} at pos {tok.Position}";
                    if (_loggedAccessedNone.Add(anKey))
                        UETrace.Info($"[VM] Accessed None in {anKey} (repeats suppressed)");
                    var cct = (Tok.ContextToken)tok;
                    if (frame.Cursor < frame.Tokens.Count)
                    {
                        int targetByte = frame.Tokens[frame.Cursor].Position + cct.SkipSize;
                        while (frame.Cursor < frame.Tokens.Count
                            && frame.Tokens[frame.Cursor].Position < targetByte)
                            frame.Cursor++;
                    }
                    return new UEEvalResult { Value = null, Result = UEStmtResult.Next };
                }
                var saved = frame.ClassContext;
                var savedContext = frame.Context;
                frame.ClassContext = objR.Value as UClass
                                   ?? (objR.Value as UEObject)?.Class;
                frame.Context = interp.ClassContextObject(frame.ClassContext);
                UEEvalResult rhsR;
                try { rhsR = interp.EvalNext(frame); }
                finally { frame.ClassContext = saved; frame.Context = savedContext; }
                return new UEEvalResult { Value = rhsR.Value, Result = UEStmtResult.Next };
            });

            // execBoolVariable (Core.dll.c:45463): the inner variable token runs
            // with Result=NULL and sets GPropAddr, then Result =
            // (*GPropAddr & BitMask) != 0; no address → false. The port stores bool
            // as bool, so the read is delegated to the child token
            // (InstanceVariableToken / LocalVariableToken).
            //
            // UELib FieldTokens.cs:124-136 — BoolVariableToken consumes one
            // child token via DeserializeNext.
            Register<Tok.BoolVariableToken>((interp, frame, tok) => {
                return interp.EvalNext(frame);
            });

            // execGotoLabel -> UObject::GotoLabel (Core 0x10131df0): the label is
            // looked up from the CURRENT state node (StateNode) along the SuperField
            // chain; not found or None — Code = NULL: state code STOPS (not an error
            // followed by the next statement).
            Register<Tok.GotoLabelToken>((interp, frame, tok) => {
                var nameR = interp.EvalNext(frame);
                string label = nameR.Value?.ToString();
                if (!string.IsNullOrEmpty(label)
                 && !string.Equals(label, "None", StringComparison.OrdinalIgnoreCase)) {
                    int fidx = interp.ResolveStateLabel(frame, label);
                    if (fidx >= 0) {
                        frame.Cursor = fidx;
                        return new UEEvalResult { Result = UEStmtResult.Next };
                    }
                }
                UETrace.Error(
                    $"[VM] GotoLabel '{label}' not found in " +
                    $"{frame.Object?.Class?.Name}.{frame.Func?.Name} — state code stops");
                frame.LatentState = LatentRunState.Stop;
                frame.Cursor = frame.Tokens.Count;
                return new UEEvalResult { Result = UEStmtResult.Next };
            });

            // execJump (Core.dll.c:37072): Code = Script + offset. The Run loop
            // resolves that offset to a token index via OffsetToIndex. The runaway
            // counter is incremented here and in execJumpIfNot, as in shipped.
            //
            // UELib JumpTokens.cs:83-101 — reads CodeOffset (ushort).
            Register<Tok.JumpToken>((interp, frame, tok) => {
                var jt = (Tok.JumpToken)tok;
                interp.CountRunawayJump(frame);
                return new UEEvalResult {
                    Result = UEStmtResult.Jump,
                    JumpAddress = jt.CodeOffset,
                };
            });

            // execJumpIfNot (Core.dll.c:45428): offset word, then the condition;
            // false — Code = Script + offset. Otherwise
            // continue with Result=Next (the implicit fall-through into the
            // if-body).
            //
            // UELib JumpTokens.cs:335-363 — JumpIfNotToken : JumpToken; reads
            // CodeOffset (from base) and one child condition token. The child
            // token follows immediately in the flat stream so we read it with
            // EvalNext.
            // UObject::execAssert (Core.dll thunk 0x10101e38 -> 0x10133100): line
            // number word, then one boolean expression; false ->
            // FFrame::Logf(NAME_Critical 0x88, "Assertion failed, line %i"), and
            // FFrame::Serialize on 0x88 goes to GError — a fatal error.
            // True — nothing. Without a handler ANY assert (including passing ones:
            // UWindowList.InsertItemBefore, GraftLeft...) would stop execution as an
            // "unknown token".
            Register<Tok.AssertToken>((interp, frame, tok) => {
                var at = (Tok.AssertToken)tok;
                var cond = interp.EvalNext(frame);
                if (!ToBool(cond.Value))
                    throw new InvalidOperationException(
                        $"Assertion failed, line {at.Line} ({frame.Object?.Class?.Name}.{frame.Func?.Name}) — "
                        + "shipped: FFrame::Logf(NAME_Critical) -> GError, fatal");
                return new UEEvalResult { Result = UEStmtResult.Next };
            });

            Register<Tok.JumpIfNotToken>((interp, frame, tok) => {
                var jt = (Tok.JumpIfNotToken)tok;
                interp.CountRunawayJump(frame);
                var cond = interp.EvalNext(frame);
                if (!ToBool(cond.Value)) {
                    if (UECoverage.On) UECoverage.Hit(frame.Func, jt.Position, -1);
                    return new UEEvalResult {
                        Result = UEStmtResult.Jump,
                        JumpAddress = jt.CodeOffset,
                    };
                }
                // Empty if body: the target equals the point after the condition, and
                // the original's probe (comparing Code with the target) records J here — same here.
                if (UECoverage.On) UECoverage.Hit(frame.Func, jt.Position,
                    frame.Cursor < frame.Tokens.Count && frame.Tokens[frame.Cursor].Position == jt.CodeOffset ? -1 : -2);
                return new UEEvalResult { Result = UEStmtResult.Next };
            });

            Register<Tok.FinalFunctionToken>((interp, frame, tok) => {
                var ft = (Tok.FinalFunctionToken)tok;
                string lbl = ft.Function?.Name?.ToString() ?? "?";
                var (args, outRefs) = CollectArgsWithOuts(interp, frame, ft.Function, lbl);
                if (ft.Function == null) {
                    UETrace.Error(
                        $"[VM] FinalFunction with null target in " +
                        $"{frame.Object?.Class?.Name}.{frame.Func?.Name}");
                    return new UEEvalResult { Result = UEStmtResult.Next };
                }
                // execFinalFunction (Core.dll.c:52837) → CallFunction on the context
                // object: a final call runs on the CONTEXT object, not Self. For a bare /
                // Super call Context == Object (Self); for `A.FinalFunc()` Context
                // == A. (Previously invoked on frame.Object → ran the body against
                // the caller for member-targeted final calls.) None → Accessed-None.
                var target = frame.Context;
                if (target == null) {
                    UETrace.Info(
                        $"[VM] Accessed None calling final '{lbl}' in " +
                        $"{frame.Object?.Class?.Name}.{frame.Func?.Name}");
                    return new UEEvalResult { Result = UEStmtResult.Next };
                }
                var argsArr = args;
                // Bridge-method intercept (as in VirtualFunctionToken): the bridge
                // wins over the class bytecode. Out-params still flow back below.
                //
                // Canvas.SetPos/SetOrigin/SetClip/DrawIcon all have bodies in Engine.u
                // and run as bytecode; intercepting a name that has a body would
                // suppress virtual dispatch — forbidden structurally
                // (verify_architecture) and checked live.
                if (target.BridgeMethods != null && target.BridgeMethods.TryGetValue(lbl, out var bridgeFn)) {
                    var bret = bridgeFn(argsArr);
                    for (int i = 0; i < outRefs.Count; i++)
                        if (outRefs[i].HasValue && i < argsArr.Length)
                            interp.WriteLValue(frame, outRefs[i].Value, argsArr[i]);
                    return new UEEvalResult { Value = bret, Result = UEStmtResult.Next };
                }
                var ret = interp.Invoke(target, ft.Function, argsArr);
                // UObject::CallFunction (Core.dll.c:7715): out-params are copied back
                // starting from the LAST — observable under aliasing `F(x, x)`.
                for (int i = outRefs.Count - 1; i >= 0; i--) {
                    if (outRefs[i].HasValue && i < argsArr.Length)
                        interp.WriteLValue(frame, outRefs[i].Value, argsArr[i]);
                }
                interp.Pool.ReleaseOutRefs(outRefs, NoOutRefs);
                interp.Pool.ReleaseArgs(argsArr);
                return new UEEvalResult { Value = ret, Result = UEStmtResult.Next };
            });

            // Constants: execIntConst / IntConstByte / ByteConst / FloatConst /
            // StringConst / NameConst / ObjectConst (Core.dll.c:33925, 1998, 22652,
            // 30583, 43785, 30151, 55595) — the literal value from the stream. The
            // parsed value comes from UELib ConstTokens.cs.
            Register<Tok.TrueToken>((interp, frame, tok) =>
                new UEEvalResult { Value = true, Result = UEStmtResult.Next });
            Register<Tok.FalseToken>((interp, frame, tok) =>
                new UEEvalResult { Value = false, Result = UEStmtResult.Next });
            Register<Tok.IntZeroToken>((interp, frame, tok) =>
                new UEEvalResult { Value = 0, Result = UEStmtResult.Next });
            Register<Tok.IntOneToken>((interp, frame, tok) =>
                new UEEvalResult { Value = 1, Result = UEStmtResult.Next });
            Register<Tok.IntConstToken>((interp, frame, tok) =>
                new UEEvalResult { Value = ((Tok.IntConstToken)tok).Value, Result = UEStmtResult.Next });
            Register<Tok.IntConstByteToken>((interp, frame, tok) =>
                new UEEvalResult { Value = (int)((Tok.IntConstByteToken)tok).Value, Result = UEStmtResult.Next });
            Register<Tok.ByteConstToken>((interp, frame, tok) =>
                new UEEvalResult { Value = ((Tok.ByteConstToken)tok).Value, Result = UEStmtResult.Next });
            Register<Tok.FloatConstToken>((interp, frame, tok) =>
                new UEEvalResult { Value = ((Tok.FloatConstToken)tok).Value, Result = UEStmtResult.Next });
            Register<Tok.StringConstToken>((interp, frame, tok) =>
                new UEEvalResult { Value = UEValue.AnsiStringConst(((Tok.StringConstToken)tok).Value), Result = UEStmtResult.Next });
            Register<Tok.UnicodeStringConstToken>((interp, frame, tok) =>
                new UEEvalResult { Value = ((Tok.UnicodeStringConstToken)tok).Value, Result = UEStmtResult.Next });
            // NameConstToken — wrap in UEName so name comparisons stay
            // distinct from string at runtime (UE1 `name` vs `string`).
            Register<Tok.NameConstToken>((interp, frame, tok) =>
                new UEEvalResult { Value = new UEName(((Tok.NameConstToken)tok).Name), Result = UEStmtResult.Next });
            // ObjectConstToken yields the parsed UObject ref. Our runtime
            // doesn't wrap arbitrary UObjects yet; pass through and let
            // downstream choke loud if it matters. A mesh takes the same form as
            // in defaults (UEObject.MeshLiteralRef).
            Register<Tok.ObjectConstToken>((interp, frame, tok) =>
                new UEEvalResult { Value = UEObject.MeshLiteralRef(((Tok.ObjectConstToken)tok).ObjectRef), Result = UEStmtResult.Next });
            // NoneToken (ConstTokens.cs:49-56) — `none` literal for object /
            // name refs; execNoObject (Core.dll.c:16867): *Result = 0. UELib also
            // emits the NoObjectToken / EmptyDelegateToken subclasses (both
            // `none`); dispatch is by exact type so register all three.
            Register<Tok.NoneToken>((interp, frame, tok) =>
                new UEEvalResult { Value = null, Result = UEStmtResult.Next });
            Register<Tok.NoObjectToken>((interp, frame, tok) =>
                new UEEvalResult { Value = null, Result = UEStmtResult.Next });
            Register<Tok.EmptyDelegateToken>((interp, frame, tok) =>
                new UEEvalResult { Value = null, Result = UEStmtResult.Next });
            // SelfToken (ConstTokens.cs:58-65) — refers to the active object.
            // execSelf (Core.dll.c:7696): *Result = this.
            Register<Tok.SelfToken>((interp, frame, tok) =>
                new UEEvalResult { Value = frame.Object, Result = UEStmtResult.Next });

            // VectorConstToken (ConstTokens.cs:269-286) — `vect(x,y,z)` literal.
            Register<Tok.VectorConstToken>((interp, frame, tok) => {
                var vc = (Tok.VectorConstToken)tok;
                return new UEEvalResult {
                    Value = new Vector3(vc.Vector.X, vc.Vector.Y, vc.Vector.Z),
                    Result = UEStmtResult.Next,
                };
            });
            // RotationConstToken (ConstTokens.cs:250-267) — `rot(p,y,r)` literal.
            Register<Tok.RotationConstToken>((interp, frame, tok) => {
                var rc = (Tok.RotationConstToken)tok;
                return new UEEvalResult {
                    Value = new UERotator(rc.Rotation.Pitch, rc.Rotation.Yaw, rc.Rotation.Roll),
                    Result = UEStmtResult.Next,
                };
            });

            // Primitive type-conversion casts: bodies of Core.dll exec*To*, verified
            // against the disasm (open item: StringToFloat/StringToInt edge cases).
            // Each converts the child expression's value to the target type. UELib folds the whole family
            // into a single PrimitiveCastToken with a CastOpCode byte
            // (UELib UnrealTokens.cs:486 — the CastToken enum).
            //
            // PrimitiveInlineCastToken (CastTokens.cs:275-282) is the same
            // token packed inline into the opcode byte; same handler.
            UETokenHandler castHandler = (interp, frame, tok) => {
                var pc = (Tok.PrimitiveCastToken)tok;
                var inner = interp.EvalNext(frame);
                object v = inner.Value;
                object converted;
                switch (pc.CastOpCode)
                {
                    case CastToken.ByteToInt:    converted = (int)((byte?)AsByte(v) ?? 0); break;
                    case CastToken.IntToByte:    converted = (byte)(ToInt(v) & 0xFF); break;
                    case CastToken.IntToFloat:   converted = (float)ToInt(v); break;
                    case CastToken.FloatToInt:   converted = UEValue.FloatToInt(AsFloat(v)); break;
                    case CastToken.FloatToByte:  converted = UEValue.FloatToByte(AsFloat(v)); break;
                    case CastToken.ByteToFloat:  converted = (float)AsByte(v); break;
                    case CastToken.BoolToByte:   converted = (byte)(ToBool(v) ? 1 : 0); break;
                    case CastToken.BoolToInt:    converted = ToBool(v) ? 1 : 0; break;
                    case CastToken.BoolToFloat:  converted = ToBool(v) ? 1f : 0f; break;
                    case CastToken.ByteToBool:   converted = AsByte(v) != 0; break;
                    case CastToken.IntToBool:    converted = ToInt(v) != 0; break;
                    case CastToken.FloatToBool:  { float fb = AsFloat(v); converted = !(fb == 0f || float.IsNaN(fb)); break; }   // Core 0x10135100: NaN -> false
                    case CastToken.ObjectToBool: converted = v != null; break;
                    // Shipped: true if the FName index != 0 (NAME_None). FName is
                    // interned case-insensitively, and the empty string is also
                    // NAME_None, so 'none' and '' give false.
                    case CastToken.NameToBool:
                        converted = !(v == null
                                      || (v is UEName n && (string.IsNullOrEmpty(n.Value)
                                          || string.Equals(n.Value, "None", StringComparison.OrdinalIgnoreCase)))); break;
                    // String→number: UE1 appAtoi/appAtof parse the LEADING number and stop at
                    // the first non-numeric char ("12abc"→12); a whole-string TryParse gave 0.
                    // (Core.dll.c:12188/34471 appAtoi/appAtof.)
                    case CastToken.StringToByte:
                        converted = (byte)(LeadingAtoi(v?.ToString()) & 0xFF); break;
                    case CastToken.StringToInt:
                        converted = LeadingAtoi(v?.ToString()); break;
                    case CastToken.StringToFloat:
                        converted = LeadingAtof(v?.ToString()); break;
                    case CastToken.StringToBool: {
                        // Native execStringToBool (Core.dll.c:66327-66352): "True"/"False"
                        // (case-insensitive, incl. localized GTrue/GFalse) → true/false, else
                        // appAtoi(str)!=0. The old heuristic made "hello"/"00" → true.
                        string sbv = v as string ?? "";
                        if ("true".Equals(sbv, StringComparison.OrdinalIgnoreCase)) converted = true;
                        else if ("false".Equals(sbv, StringComparison.OrdinalIgnoreCase)) converted = false;
                        else converted = LeadingAtoi(sbv) != 0;
                        break; }
                    // `%i` (appSprintf) is locale-independent; .NET ToString() without
                    // InvariantCulture uses the culture's minus sign (U+2212 in some locales).
                    case CastToken.ByteToString:    converted = AsByte(v).ToString(System.Globalization.CultureInfo.InvariantCulture); break;
                    case CastToken.IntToString:     converted = ToInt(v).ToString(System.Globalization.CultureInfo.InvariantCulture); break;
                    case CastToken.BoolToString:    converted = ToBool(v) ? "True" : "False"; break;
                    case CastToken.FloatToString:
                        // UE1 FString::Printf("%f") = 6 fixed decimals
                        // (Core.dll.c:35345). Was default ToString ("1.5"), inconsistent with the
                        // port's own VectorToString ("F6") and the comment below. → "1.500000".
                        converted = PercentF(AsFloat(v)); break;
                    case CastToken.NameToString:    converted = v?.ToString() ?? "None"; break;
                    // string → name (UE1 opcode 0x5A; UELib emits it as
                    // StringToName after the dll patch). HP1 baseScript.
                    // BroadcastTrigger does `name(Event)`; wrap as UEName so it
                    // compares against name literals ('None', tags). Empty/null
                    // string → 'None' (UE1 name-of-empty convention).
                    case CastToken.StringToName:
                        converted = new UEName(string.IsNullOrEmpty(v as string) ? "None" : (string)v); break;
                    // Vector/Rotator: FVector::Rotation (0x1014e460) and
                    // (UnitCoords/R).XAxis (0x1013ab10) — shipped bodies, see UEValue.
                    case CastToken.VectorToRotator: converted = UEValue.RotatorFromVector(AsVector3(v)); break;
                    case CastToken.RotatorToVector: converted = UEValue.RotatorToVector(AsRotator(v)); break;
                    case CastToken.ObjectToString:
                        converted = ObjectPathName(v); break;
                    // Vector/Rotator string + bool conversions: execVectorToBool
                    // (Core.dll.c:15688), execRotatorToBool (:65309), execVectorToString
                    // (:4283, "%f,%f,%f"), execRotatorToString (:5094, "%i,%i,%i" by & 0xFFFF),
                    // execStringToVector (:824), execStringToRotator (:6422).
                    // These previously fell through to the error default — `if (Velocity)`
                    // style truth-tests errored, and string(Location)/string(rot) leaked
                    // Unity's "(x, y, z)" format instead of UE1's bare "x,y,z". Vectors are
                    // held in UE space (string(Location) reads UnityToUEPos), so we print
                    // the UE-space components. Float format = %f (6 dp).
                    case CastToken.VectorToBool: {
                        converted = UEValue.VectorToBool(AsVector3(v)); break; }
                    case CastToken.RotatorToBool: {
                        // execRotatorToBool: components & 0xFFFF before the check — a
                        // component == a nonzero multiple of 65536 is zero rotation.
                        var rb = AsRotator(v);
                        converted = (rb.Pitch & 0xffff) != 0 || (rb.Yaw & 0xffff) != 0 || (rb.Roll & 0xffff) != 0; break; }
                    case CastToken.VectorToString: {
                        var vs = AsVector3(v);
                        converted = PercentF(vs.x) + "," + PercentF(vs.y) + "," + PercentF(vs.z); break; }
                    case CastToken.RotatorToString: {
                        var rs = AsRotator(v);
                        converted = (rs.Pitch & 0xffff) + "," + (rs.Yaw & 0xffff)
                                  + "," + (rs.Roll & 0xffff); break; }
                    // execStringToVector (Core.dll.c:824) / execStringToRotator (:6422):
                    // X = appAtof(s); after the FIRST ',' — Y; after the next ',' — Z
                    // (appStrstr). Missing components are 0: "5,6" -> (5,6,0).
                    case CastToken.StringToVector:  converted = UEValue.StringToVector((v as string) ?? ""); break;
                    case CastToken.StringToRotator: converted = UEValue.StringToRotator((v as string) ?? ""); break;
                    default:
                        UETrace.Error(
                            $"[VM] Unsupported PrimitiveCast {pc.CastOpCode} in " +
                            $"{frame.Object?.Class?.Name}.{frame.Func?.Name}");
                        converted = v;
                        break;
                }
                return new UEEvalResult { Value = converted, Result = UEStmtResult.Next };
            };
            Register<Tok.PrimitiveCastToken>(castHandler);
            Register<Tok.PrimitiveInlineCastToken>(castHandler);

            // execDynamicCast (Core.dll.c:28229, body 0x10134330): class from the stream,
            // then the value; IsA along the SuperField chain by class IDENTITY
            // (+0x24 / +0x28); failure → None.
            // i.e. a checked downcast: returns the object if it IsA the target
            // class, else None. Drives HP1's `if (BaseCam(x) != none)` /
            // `baseChar(x).CutMoveTo(...)` dispatch in handleCast.
            //
            // UELib CastTokens.cs:284-301 — DynamicCastToken reads the target
            // UClass then one child value expression.
            Register<Tok.DynamicCastToken>((interp, frame, tok) => {
                var dc = (Tok.DynamicCastToken)tok;
                var v = interp.EvalNext(frame);
                string targetName = dc.CastClass?.Name;
                // Class identity, not name (execDynamicCast 0x1013436c):
                // a same-named class from another package is not the same class.
                if (v.Value is UEObject obj)
                    return new UEEvalResult {
                        Value = UEClassRegistry.Shared.IsChildOfClass(obj.Class, dc.CastClass) ? obj : null,
                        Result = UEStmtResult.Next };
                object passthrough = null;
                // UEResourceRef markers (DynamicLoadObject / struct defaults)
                // represent a real UE1 resource of marker.Class — the cast to
                // that class succeeds in UE1, so the marker passes through.
                // Without this, FEBook.Created's `Texture(DynamicLoadObject(
                // "MenuArt.MoonTitle1", ...))` returned null and the menu
                // background fields were written as None.
                // The cast is IsA (UObject::IsA along the superclass chain), not name
                // equality: Texture(DynamicLoadObject(..., Class'Fire.FireTexture'))
                // succeeds in shipped (FireTexture -> FractalTexture -> Texture).
                // Exact comparison gave None to the BroomHoopStage1-5 rings
                // (164 actors; checked on the live 1.53 original, Skin=...BTStageN).
                if (v.Value is UEResourceRef rr
                 && (string.Equals(rr.Class, targetName, StringComparison.OrdinalIgnoreCase)
                  || ClassIsA(UEClassRegistry.Shared?.Find(rr.Class), targetName)))
                    passthrough = v.Value;
                return new UEEvalResult { Value = passthrough, Result = UEStmtResult.Next };
            });

            // UE1 metaclass cast `Class<T>(expr)` — MetaClassCastToken
            // (CastTokens.cs:312, a subclass of DynamicCastToken; exact-type
            // dispatch needs its own handler). execMetaCast (Core.dll.c:63121): the value
            // is a class; IsChildOf the target metaclass T by identity, otherwise None.
            // WindowConsole.CreateRootWindow casts
            // DynamicLoadObject(...) → Class<UWindowRootWindow>; the `new` operator
            // then instantiates the result. Without this the class operand was null.
            Register<Tok.MetaClassCastToken>((interp, frame, tok) => {
                var mc = (Tok.MetaClassCastToken)tok;
                var v = interp.EvalNext(frame);
                var cls = v.Value as UClass;
                string targetName = mc.CastClass?.Name;
                if (cls != null && mc.CastClass != null && !UEClassRegistry.Shared.IsChildOfClass(cls, mc.CastClass))
                    cls = null;
                return new UEEvalResult { Value = cls, Result = UEStmtResult.Next };
            });

            // UE1 `new` operator. UELib NewToken (OtherTokens.cs:226) always emits
            // four child expressions in order: Outer, Name, Flags, Class (no
            // Template before stream version 300; HP1 is v76). Evaluate all four
            // (Outer/Name are usually `none`, Flags 0) and construct a UObject of
            // the resolved class via NewObject. This is the window-tree
            // instantiation gate: UWindowWindow.CreateWindow does `new (none)
            // WndClass` and WindowConsole.CreateRootWindow does
            // `new (none) Class<UWindowRootWindow>(DynamicLoadObject(...))`.
            Register<Tok.NewToken>((interp, frame, tok) => {
                // `new Class'X'` without parentheses: the compiler emits EX_Nothing in
                // the Outer slot; execNew pre-filled Outer = Stack.Object (Core
                // 0x10141923), and EX_Nothing (0x10132aa0, bare ret) leaves it alone.
                int oc = frame.Cursor;
                while (oc < frame.Tokens.Count && frame.Tokens[oc] is Tok.DebugInfoToken) oc++;
                bool outerOmitted = oc < frame.Tokens.Count && frame.Tokens[oc] is Tok.NothingToken;
                var outerR = interp.EvalNext(frame);   // Outer
                if (outerOmitted) outerR = new UEEvalResult { Value = frame.Object, Result = UEStmtResult.Next };
                var nameR  = interp.EvalNext(frame);   // Name
                interp.EvalNext(frame);                // Flags — unused at runtime
                var classR = interp.EvalNext(frame);   // Class<...>
                var cls = classR.Value as UClass;
                if (cls == null) {
                    string cn = classR.Value is UEName un ? un.Value : classR.Value?.ToString();
                    if (!string.IsNullOrEmpty(cn)) cls = UEClassRegistry.Shared.Find(cn);
                }
                string nm = nameR.Value is UEName nn ? nn.Value : nameR.Value as string;
                var obj = interp.NewObject(cls, outerR.Value as UEObject, nm);
                if (obj == null)
                    UETrace.Warn(
                        $"[VM] new: unresolved class ({classR.Value?.GetType().Name ?? "null"}) in " +
                        $"{frame.Object?.Class?.Name}.{frame.Func?.Name}");
                return new UEEvalResult { Value = obj, Result = UEStmtResult.Next };
            });

            // execContext (Core.dll.c:47177): evaluate the object part; object
            // NULL → Logf, GPropAddr = 0, Code += wSkip + 3, result zeroed
            // (the inner expression does not run). Otherwise the inner expression
            // runs with this = object; then the context is restored.
            //
            // UELib ContextTokens.cs:12-74 — ContextToken parses inline
            // SkipSize (ushort) and PropertyType byte for newer versions, but
            // emits exactly two child tokens (ObjectExpr, then ContextExpr).
            // The inline bytes are pure stream state — no token impact.
            Register<Tok.ContextToken>((interp, frame, tok) => {
                var objR = interp.EvalNext(frame);
                var savedContext = frame.Context;
                var asObj = objR.Value as UEObject;
                if (asObj != null) {
                    frame.Context = asObj;
                } else if (objR.Value == null) {
                    // ONLY null. Shipped execContext (Core.dll.c:47194) checks the
                    // object part's result with a single comparison against zero.
                    // A corpse (bDeleteMe) is a valid pointer: member access on it
                    // RUNS. (Auto-nulling of references to destroyed actors applies
                    // to property SLOTS at cleanup, not to a live value on the call
                    // stack; treating Destroyed as None broke
                    // spellTrigger.IsRelevant(projectile corpse) at position 293 — the
                    // Lev_Tut1b flip plate did not open the door.) Null, however, MUST
                    // skip the whole expression (warn, Code += wSkip, null result) —
                    // otherwise a native inside dispatches with fallback Self
                    // (None-DrawFX `DrawFX.Destroy()` destroyed the CALLER).
                    string anKey = $"{frame.Object?.Class?.Name}.{frame.Func?.Name} at pos {tok.Position}";
                    if (_loggedAccessedNone.Add(anKey))
                        UETrace.Info($"[VM] Accessed None in {anKey} (repeats suppressed)");
                    var ct = (Tok.ContextToken)tok;
                    if (frame.Cursor < frame.Tokens.Count)
                    {
                        int targetByte = frame.Tokens[frame.Cursor].Position + ct.SkipSize;
                        while (frame.Cursor < frame.Tokens.Count
                            && frame.Tokens[frame.Cursor].Position < targetByte)
                            frame.Cursor++;
                    }
                    return new UEEvalResult { Value = null, Result = UEStmtResult.Next };
                } else if (objR.Value is UClass ctxCls) {
                    // A CLASS as the member-access context — UE1 EX_Context on
                    // the class UObject (Pawn.TakeDamage `Class.Name` chains);
                    // members resolve against the class-defaults bag + Name.
                    frame.Context = interp.ClassContextObject(ctxCls);
                } else if (TextureProxyFor(objR.Value) is UEObject texProxy) {
                    // A render-resource ref (Texture / UEResourceRef) used as a
                    // member-access context — e.g. HealthBar.USize in the HUD.
                    // Substitute a proxy UEObject exposing USize/VSize from the
                    // resolved Texture2D so the HUD's tile-size math reads real dims.
                    frame.Context = texProxy;
                } else {
                    UETrace.Warn(
                        $"[VM] ContextToken got non-UEObject value " +
                        $"({objR.Value.GetType().Name}) in " +
                        $"{frame.Object?.Class?.Name}.{frame.Func?.Name}");
                    frame.Context = null;
                }
                UEEvalResult rhsR;
                try { rhsR = interp.EvalNext(frame); }
                finally { frame.Context = savedContext; }
                return new UEEvalResult { Value = rhsR.Value, Result = UEStmtResult.Next };
            });

            // execSwitch (Core.dll.c:44827): size byte (0 — string), the expression
            // value into a buffer; the case walk is ProcessSwitch (Run catches
            // Result=Switch and calls it).
            //
            // UELib JumpTokens.cs:472-543 — SwitchToken parses an inline
            // PropertyType byte (HP1 v220) then a single child expression
            // (the switch value).
            Register<Tok.SwitchToken>((interp, frame, tok) => {
                var cond = interp.EvalNext(frame);
                return new UEEvalResult {
                    Value = cond.Value,
                    Result = UEStmtResult.Switch,
                };
            });

            // execCase (Core.dll.c:25983): on fall-through (previous case body
            // without break/return) the NextOffset word is read, and the case
            // expression (unless default, 0xFFFF) is evaluated into a buffer and
            // discarded. Here Result=Next: Run executes the case expression as the
            // next statement — same outcome. Normally ProcessSwitch consumes CaseToken.
            Register<Tok.CaseToken>((interp, frame, tok) =>
                new UEEvalResult { Result = UEStmtResult.Next });

            // EX_Skip has no separate exec body among Core.dll exports: its word
            // is read by && / || themselves (execAndAnd_BoolBool / execOrOr_BoolBool,
            // Core.dll.c:67281/34421). Here — a transparent pass to the RHS when
            // there is no short-circuit: `Result = Eval(expr->Value)`. UELib SkipToken
            // (JumpTokens.cs:707-723) reads the inline Size byte (used by
            // optional-arg default-skip routing in the parser) then a child
            // expression. At eval time we just forward through.
            Register<Tok.SkipToken>((interp, frame, tok) => interp.EvalNext(frame));

            // execStop (Core.dll.c:39733): Stack.Code = NULL; the
            // AActor::ProcessState loop (Engine.dll.c:45801) halts on Code == NULL.
            // Here Result=Stop, which Run maps to LatentState=Stop — state code
            // stops without proceeding further.
            //
            // UELib JumpTokens.cs:725-733 — StopToken is leaf with no
            // children.
            Register<Tok.StopToken>((interp, frame, tok) =>
                new UEEvalResult { Result = UEStmtResult.Stop });

            // execIterator (Core.dll.c:33534) — empty body: the foreach loop lives in
            // the iterator native (AllActors, Engine.dll.c:109111 / 0x1040d780):
            // find an element, body up to 0x30/0x31, on exhaustion Code = Script +
            // wEnd + 1. The port keeps a per-frame iterator stack instead of the
            // native's C stack:
            //
            // The child native (AllActors/AllObjects/...) parks its iterator in
            // interp.PendingIterator and captures the loop-var LValue (its out
            // param). We finalize the iterator with the foreach's end offset +
            // body-top cursor, push it (frame.IteratorStack), then MoveNext: if it
            // yields an item, fall into the body; else jump past the loop.
            //
            // UELib JumpTokens.cs:577-595 — IteratorToken.Deserialize reads
            // the expression first, then CodeOffset via base.JumpToken.
            Register<Tok.IteratorToken>((interp, frame, tok) => {
                var it = (Tok.IteratorToken)tok;
                interp.PendingIterator = null;
                interp.EvalNext(frame);              // runs the iterator native
                var iter = interp.PendingIterator;
                interp.PendingIterator = null;
                if (iter == null) {
                    // Native didn't yield an iterator (unimplemented / errored —
                    // already logged loud). Treat as empty: skip the body.
                    return new UEEvalResult { Result = UEStmtResult.Jump, JumpAddress = it.CodeOffset };
                }
                iter.LoopVar = interp.PendingIteratorLoopVar;
                iter.OutRefs = interp.PendingIteratorOutRefs;
                interp.PendingIteratorOutRefs = null;
                iter.EndOffset = it.CodeOffset;
                iter.BodyTopCursor = frame.Cursor;   // first token after IteratorToken
                frame.IteratorStack.Add(iter);
                if (iter.MoveNext(interp, frame))
                    return new UEEvalResult { Result = UEStmtResult.Next };          // enter body
                // Empty on entry → jump to the IteratorPop (EndOffset) WITHOUT popping here;
                // IteratorPop does the single pop. In shipped exhaustion is Code = Script[wEnd+1],
                // a single exit from the native (via IteratorPop), like the exhaustion path of
                // IteratorNext below. Popping here too was a
                // DOUBLE pop: in a NESTED foreach whose inner collection is empty on entry, the
                // IteratorPop then removed the OUTER iterator, corrupting the outer loop.
                return new UEEvalResult { Result = UEStmtResult.Jump, JumpAddress = it.CodeOffset };
            });

            // 0x31 (IteratorNext) in shipped: the iterator native sets Code = StartCode
            // and finds the next element; found → body from the start, exhausted → exit
            // past the loop. Here IteratorNext ALWAYS jumps, never falls through: another
            // item → BodyTopCursor; exhausted → EndOffset (the IteratorPop past the
            // loop). The old code fell through
            // on exhaustion. For the TAIL IteratorNext (right before IteratorPop) that was
            // accidentally equivalent, but `continue` compiles to an EX_IteratorNext in the
            // MIDDLE of the body (UELib IteratorNextToken.Decompile → "continue") — so on the
            // last element it ran the REST of the body with the loop-var already reset to None.
            // Reachable: baseChar.uc:571 FindClosestPatrolPoint, Harry.uc aim-target selection.
            // UELib JumpTokens.cs:643-656.
            Register<Tok.IteratorNextToken>((interp, frame, tok) => {
                if (frame.IteratorStack.Count == 0) {
                    UETrace.Error($"[VM] IteratorNext with no active iterator in " +
                                   $"{frame.Object?.Class?.Name}.{frame.Func?.Name}");
                    return new UEEvalResult { Result = UEStmtResult.Next };
                }
                var iter = frame.IteratorStack[frame.IteratorStack.Count - 1];
                if (iter.MoveNext(interp, frame)) {
                    frame.Cursor = iter.BodyTopCursor;   // another item → loop back to body top
                    return new UEEvalResult { Result = UEStmtResult.Next };
                }
                // Exhausted → jump to the IteratorPop (EndOffset), which pops the iterator.
                return new UEEvalResult { Result = UEStmtResult.Jump, JumpAddress = iter.EndOffset };
            });

            // 0x30 (IteratorPop): the iterator native returns in shipped. End of a
            // foreach (also emitted by `break` out of one): pop the active iterator.
            // UELib JumpTokens.cs:659-672.
            Register<Tok.IteratorPopToken>((interp, frame, tok) => {
                if (frame.IteratorStack.Count > 0)
                    frame.IteratorStack.RemoveAt(frame.IteratorStack.Count - 1);
                return new UEEvalResult { Result = UEStmtResult.Next };
            });

            // execArrayElement (Core.dll.c:46481): first the index, then the array
            // (a variable, sets GPropAddr/GProperty); index outside [0, ArrayDim)
            // — Logf and clamp to the bound; GPropAddr += ElementSize·idx.
            //
            // UELib ArrayTokens.cs:12-31 emits children in order
            //     [ArrayElementToken, KeyExpr, ArrayExpr]
            // so we consume Key first, then Array, matching the stream layout.
            // Our runtime model: arrays are IList (List<object>) populated
            // either by LoadDefaults (static defaults) or by Let-assignments
            // at runtime. Index outside [0, ArrayDim) — StaticArrayIndex (log +
            // clamp to the bound, as execArrayElement).
            Register<Tok.ArrayElementToken>((interp, frame, tok) => {
                var keyR = interp.EvalNext(frame);
                int dim = StaticArrayDimAt(frame);
                var arrR = interp.EvalNext(frame);
                var list = arrR.Value as System.Collections.IList;
                int idx = StaticArrayIndex(frame, ToInt(keyR.Value), dim, list);
                object val = null;
                if (list != null && idx >= 0 && idx < list.Count)
                    val = list[idx];
                return new UEEvalResult { Value = val, Result = UEStmtResult.Next };
            });

            // DynamicArrayElementToken : ArrayElementToken (UELib ArrayTokens.cs:34 —
            // no override, so the same [token, Key, Array] layout). Token dispatch is
            // by EXACT type, so the static handler above does NOT cover it (that's why
            // `DrawnPoints[N]` reads logged "No handler for token DynamicArrayElementToken").
            // A read grows the array THE SAME way as a write: execDynArrayElement (Core
            // 0x10132522..0x101325ab) does not distinguish read and write — index >= Num
            // grows the TArray to idx+1 zero-filled, a negative one is logged and
            // becomes 0 (no growth). Hence the array is taken as an lvalue.
            Register<Tok.DynamicArrayElementToken>((interp, frame, tok) => {
                var keyR = interp.EvalNext(frame);
                var list = interp.DynArrayForElement(frame, ToInt(keyR.Value), out int idx);
                object val = null;
                if (list != null && idx < list.Count)
                    val = list[idx];
                return new UEEvalResult { Value = val, Result = UEStmtResult.Next };
            });

            // execDynArrayCount (Core.dll.c:47297): Num = *(int*)(GPropAddr + 4);
            // without an address Result is not written (stays the caller's zero) — the
            // port returns 0. Stream layout [token, ArrayExpr]. This body has no
            // lvalue form (`A.Length = n`); the port keeps it loud (EvalLValue
            // default), no carriers.
            Register<Tok.DynamicArrayLengthToken>((interp, frame, tok) => {
                var arrR = interp.EvalNext(frame);
                int len = (arrR.Value is System.Collections.IList list) ? list.Count : 0;
                return new UEEvalResult { Value = len, Result = UEStmtResult.Next };
            });

            // execStructMember (Core.dll.c:4845): struct into a zeroed buffer,
            // GPropAddr += member Offset, Result — the member value.
            //
            // UELib ContextTokens.cs:102-158 — Property field is parsed first,
            // then DeserializeNext consumes the parent struct expression.
            // Flat stream layout:
            //     [StructMemberToken{Property}, ParentExpr, ...]
            //
            // Runtime model: UE1 structs are represented as UEObject bags
            // (Props dict) or as IDictionary<string,object>. Look up by field
            // name. If parent is null/wrong type, return null (loud at next
            // unmodelled token if it matters).
            Register<Tok.StructMemberToken>((interp, frame, tok) => {
                var sm = (Tok.StructMemberToken)tok;
                var pr = PropRefAt(frame, tok, sm.Property);
                string fieldName = pr.Name;
                var parent = interp.EvalNext(frame);
                if (string.IsNullOrEmpty(fieldName)) {
                    UETrace.Error(
                        $"[VM] StructMember with no field name in " +
                        $"{frame.Object?.Class?.Name}.{frame.Func?.Name} at pos {tok.Position}");
                    return new UEEvalResult { Result = UEStmtResult.Next };
                }
                object val = null;
                switch (parent.Value)
                {
                    case UEObject ueo:
                        val = ueo.Props.Get(pr.NameKeyId);
                        break;
                    case System.Collections.Generic.IDictionary<string, object> d:
                        d.TryGetValue(fieldName, out val);
                        break;
                    case Vector3 vec:
                        // UE1 Vector intrinsic component access (X/Y/Z).
                        if (string.Equals(fieldName, "X", StringComparison.OrdinalIgnoreCase)) val = vec.x;
                        else if (string.Equals(fieldName, "Y", StringComparison.OrdinalIgnoreCase)) val = vec.y;
                        else if (string.Equals(fieldName, "Z", StringComparison.OrdinalIgnoreCase)) val = vec.z;
                        break;
                    case UERotator rot:
                        // UE1 Rotator intrinsic component access.
                        if (string.Equals(fieldName, "Pitch", StringComparison.OrdinalIgnoreCase)) val = rot.Pitch;
                        else if (string.Equals(fieldName, "Yaw", StringComparison.OrdinalIgnoreCase)) val = rot.Yaw;
                        else if (string.Equals(fieldName, "Roll", StringComparison.OrdinalIgnoreCase)) val = rot.Roll;
                        break;
                    case UEPlane pl:
                        // Core.Object.Plane — X/Y/Z/W (otherwise a plane member reads
                        // as null → 0 and all plane operators of Object.u return a zero plane).
                        if (string.Equals(fieldName, "X", StringComparison.OrdinalIgnoreCase)) val = pl.X;
                        else if (string.Equals(fieldName, "Y", StringComparison.OrdinalIgnoreCase)) val = pl.Y;
                        else if (string.Equals(fieldName, "Z", StringComparison.OrdinalIgnoreCase)) val = pl.Z;
                        else if (string.Equals(fieldName, "W", StringComparison.OrdinalIgnoreCase)) val = pl.W;
                        break;
                }
                return new UEEvalResult { Value = val, Result = UEStmtResult.Next };
            });

            // execLet (Core.dll.c:59462): the LHS sets GPropAddr, then the RHS is
            // written straight to that address; no address → Logf and write to
            // scratch (the RHS is still evaluated).
            //
            // execLetBool (Core.dll.c:4101): LHS → GPropAddr/GProperty, RHS, then
            // bit mask (Or / AndNot). The port stores bool as bool — same outcome,
            // hence one handler (LetBool is a typed alias UELib promotes to
            // a subclass — LetTokens.cs:28). We register both with the same
            // handler.
            //
            // UELib LetTokens.cs:10-25 — LetToken consumes two children
            // (LHS, RHS) in order.
            UETokenHandler letHandler = (interp, frame, tok) => {
                var lref = interp.EvalLValue(frame);
                var rhs = interp.EvalNext(frame);
                // UE1 value-struct semantics: assigning a struct copies it, so a saved
                // struct local isn't aliased to the source (e.g. the HUD's
                // OldColor = DrawColor; DrawColor.R = 0; DrawColor = OldColor restore).
                // A whole array<> is a value too (UArrayProperty::CopySingleValue).
                var val = UEValue.CopyValue(rhs.Value);
                interp.WriteLValue(frame, lref, val);
                return new UEEvalResult { Value = val, Result = UEStmtResult.Next };
            };
            Register<Tok.LetToken>(letHandler);
            Register<Tok.LetBoolToken>(letHandler);

            // EX_StructCmpEq / EX_StructCmpNe (UELib ComparisonToken: the struct-type UObject
            // is consumed at DESERIALIZE, leaving two operand sub-exprs at eval time). UE1
            // compares two struct instances member-by-member. Were unregistered → per-frame
            // "[VM] No handler for token StructCmpEqToken" spam in FEBook.Paint (the menu book)
            // and any `someStruct == otherStruct` guard. (#audit gap 12)
            Register<Tok.StructCmpEqToken>((interp, frame, tok) => {
                var a = interp.EvalNext(frame); var b = interp.EvalNext(frame);
                return new UEEvalResult { Value = StructEquals(a.Value, b.Value), Result = UEStmtResult.Next };
            });
            Register<Tok.StructCmpNeToken>((interp, frame, tok) => {
                var a = interp.EvalNext(frame); var b = interp.EvalNext(frame);
                return new UEEvalResult { Value = !StructEquals(a.Value, b.Value), Result = UEStmtResult.Next };
            });

            // execVirtualFunction (Core.dll.c:53121): FindFunctionChecked(this =
            // context object, name, Global=0) → CallFunction on it. Lookup
            // "state first, then class" — FindEventFunction (below,
            // UObject::FindObjectField).
            //
            // UELib FunctionTokens.cs:237-258 — VirtualFunctionToken; reads
            // FunctionName (UName) then DeserializeCall consumes args.
            Register<Tok.VirtualFunctionToken>((interp, frame, tok) => {
                var vt = (Tok.VirtualFunctionToken)tok;
                string name = vt.FunctionName.ToString();
                // Virtual calls resolve on the current Context's class
                // (for `A.B()` Context is A); the invoked frame's Self
                // becomes that Context — in shipped execContext sets this = A,
                // and execVirtualFunction looks the function up on A. We resolve the target function BEFORE
                // collecting args so CollectArgsWithOuts knows the
                // PropertyFlags layout.
                var target = frame.Context;
                // Bridge-method intercept (checked BEFORE bytecode): only for
                // functions intentionally owned by the Unity engine bridge (for
                // example PlayerHarry ClientMessage/Cut* cues). HUD/dialog drawing
                // is now VM baseHud/HPHud bytecode, not a C# overlay.
                // Args are still collected from the stream so it doesn't desync.
                if (target?.BridgeMethods != null
                 && target.BridgeMethods.TryGetValue(name, out var bridge)) {
                    var (bargs, _) = CollectArgsWithOuts(interp, frame, null, name);
                    return new UEEvalResult { Value = bridge(bargs), Result = UEStmtResult.Next };
                }
                // UEPlane is a value struct with members, so PlaneV/PlaneC/PlaneF
                // run their own bytecode, as in shipped (no MakePlane hook).
                var fn = interp.FindEventFunction(target, name);
                var (args, outRefs) = CollectArgsWithOuts(interp, frame, fn, name);
                // Diagnostic only, the call runs as usual: ClientMessage text in the log with the level time and the
                // receiver's location. CutScene.DebugPrint lands here ("CutScene start:", "CutTrigger:Event name="), so
                // a playthrough log shows when each cutscene started and where Harry was; the shipped console shows none.
                if (UETrace.Playthrough is not null && target != null && args is { Length: > 0 } && string.Equals(name, "ClientMessage", StringComparison.OrdinalIgnoreCase))
                {
                    var at = AsVector3(target.GetProp("Location"));
                    UETrace.Playthrough($"[ClientMessage] t={interp.PeekLevelInfo()?.GetProp("TimeSeconds")} '{args[0]}' at ({at.x:F0}, {at.y:F0}, {at.z:F0})");
                }
                if (target == null) {
                    // Accessed None: a virtual call through a None object. UE1
                    // treats this as a no-op that returns the default value and
                    // continues (the enclosing ContextExpression already logged
                    // "Accessed None"). Common + benign in HP1 — e.g.
                    // theNarrator.DeliverDialog when no baseNarrator actor exists,
                    // or Level.Game.SetGameSpeed (GameInfo not modelled). Quiet
                    // Info log (matches the ContextToken None path), NOT a loud
                    // error. Args were already consumed above so the stream stays
                    // in sync. (Distinct from the next branch: target present but
                    // the function genuinely missing → still loud.)
                    // §8 loudness exception: the level-travel chain (CutScene.uc:745
                    // CHANGELEVEL → Console.ChangeLevel → Level.ServerTravel) dies on
                    // this path because the console bridge has no Viewport and
                    // ServerTravel is not implemented — the END-OF-LEVEL transition
                    // must not fail as a quiet Info line.
                    if (string.Equals(name, "ChangeLevel", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(name, "ServerTravel", StringComparison.OrdinalIgnoreCase))
                        UETrace.Error(
                            $"[VM] LEVEL TRAVEL NOT PORTED: '{name}' hit Accessed-None in " +
                            $"{frame.Object?.Class?.Name}.{frame.Func?.Name} — Tut1→Tut1b transition blocked");
                    else
                        UETrace.Info(
                            $"[VM] Accessed None calling '{name}' in " +
                            $"{frame.Object?.Class?.Name}.{frame.Func?.Name}");
                    return new UEEvalResult { Result = UEStmtResult.Next };
                }
                if (fn == null) {
                    // Fallback: a NATIVE function not present as a UFunction in the
                    // loaded class hierarchy (e.g. Object.Localize, NativeToken 0 —
                    // resolved by name). If a name-native exists, run it with the
                    // collected args. (Native side effects / out-params handled by
                    // the native + the writeback below.)
                    var ncb = interp.Natives.GetByName(name);
                    if (ncb != null) {
                        var na = args;
                        NativeCoverageHook?.Invoke(name);
                        var nret = ncb(interp, frame, na);
                        for (int i = 0; i < outRefs.Count; i++)
                            if (outRefs[i].HasValue && i < na.Length)
                                interp.WriteLValue(frame, outRefs[i].Value, na[i]);
                        interp.Pool.ReleaseOutRefs(outRefs, NoOutRefs);
                        interp.Pool.ReleaseArgs(na);
                        return new UEEvalResult { Value = nret, Result = UEStmtResult.Next };
                    }
                    string missing =
                        $"[VM] Virtual function '{name}' not found on " +
                        $"{target.Class?.Name} in " +
                        $"{frame.Object?.Class?.Name}.{frame.Func?.Name}";
                    if (interp.ThrowOnMissingScript) throw new NotSupportedException(missing);
                    UETrace.Error(missing);
                    return new UEEvalResult { Result = UEStmtResult.Next };
                }
                var argsArr = args;
                var ret = interp.Invoke(target, fn, argsArr);
                // UObject::CallFunction (Core.dll.c:7715): out-params are copied back
                // starting from the LAST — observable under aliasing `F(x, x)`.
                for (int i = outRefs.Count - 1; i >= 0; i--) {
                    if (outRefs[i].HasValue && i < argsArr.Length)
                        interp.WriteLValue(frame, outRefs[i].Value, argsArr[i]);
                }
                interp.Pool.ReleaseOutRefs(outRefs, NoOutRefs);
                interp.Pool.ReleaseArgs(argsArr);
                return new UEEvalResult { Value = ret, Result = UEStmtResult.Next };
            });

            // execGlobalFunction (Core.dll.c:47853): FindFunctionChecked(name,
            // Global=1) — only the class VfHash (`global.Foo(...)`). Resolves Foo on the Context's class
            // hierarchy SKIPPING state overrides, then calls it. Used by state
            // Tick overrides that chain to the base tick, e.g.
            // baseChar.CutMovingTo.Tick begins with `global.Tick(fDeltaTime)`.
            Register<Tok.GlobalFunctionToken>((interp, frame, tok) => {
                var gt = (Tok.GlobalFunctionToken)tok;
                string name = gt.FunctionName.ToString();
                var target = frame.Context;
                var fn = interp.FindGlobalFunction(target, name);
                var (args, outRefs) = CollectArgsWithOuts(interp, frame, fn, name);
                if (target == null) {
                    UETrace.Info(
                        $"[VM] Accessed None calling global.'{name}' in " +
                        $"{frame.Object?.Class?.Name}.{frame.Func?.Name}");
                    return new UEEvalResult { Result = UEStmtResult.Next };
                }
                if (fn == null) {
                    string missing =
                        $"[VM] Global function '{name}' not found on " +
                        $"{target.Class?.Name} in " +
                        $"{frame.Object?.Class?.Name}.{frame.Func?.Name}";
                    if (interp.ThrowOnMissingScript) throw new NotSupportedException(missing);
                    UETrace.Error(missing);
                    return new UEEvalResult { Result = UEStmtResult.Next };
                }
                var argsArr = args;
                var ret = interp.Invoke(target, fn, argsArr);
                // UObject::CallFunction (Core.dll.c:7715): out-params are copied back
                // starting from the LAST — observable under aliasing `F(x, x)`.
                for (int i = outRefs.Count - 1; i >= 0; i--) {
                    if (outRefs[i].HasValue && i < argsArr.Length)
                        interp.WriteLValue(frame, outRefs[i].Value, argsArr[i]);
                }
                interp.Pool.ReleaseOutRefs(outRefs, NoOutRefs);
                interp.Pool.ReleaseArgs(argsArr);
                return new UEEvalResult { Value = ret, Result = UEStmtResult.Next };
            });

            // FFrame::Step (Core.dll.c:39376): GNatives[*Code++] — a native call's
            // opcode is its index (extended ones — FirstNative + ExtendedNative).
            // UELib resolves the NativeTableItem ({Name, Type, ByteToken}) for us
            // as tok.NativeItem. The by-name fallback (GetByName) is port-only;
            // shipped uses the index only.
            //
            // execAndAnd_BoolBool / execOrOr_BoolBool (Core.dll.c:67281/34421) —
            // && (idx 130) and || (idx 132): A, word wSkip; result decided — Code +=
            // wSkip (including EndFunctionParms). The RHS is wrapped in an EX_Skip and evaluated lazily, so
            // guards like `X==None || X.foo()` / `X!=None && X.foo()` never deref None.
            // EvalShortCircuit honours that (was a known deviation — eager eval of both
            // args fired the RHS even when the LHS decided it → spurious Accessed-None).
            Register<Tok.NativeFunctionToken>((interp, frame, tok) => {
                var nt = (Tok.NativeFunctionToken)tok;
                int nativeIdx = nt.NativeItem?.ByteToken ?? 0;
                if (nativeIdx == 130 || nativeIdx == 132)
                    return interp.EvalShortCircuit(frame, nativeIdx);
                string nativeName = nt.NativeItem?.Name;
                // Look up the matching UFunction declaration so we can read
                // PropertyFlags.OutParm for out-param dispatch. Natives
                // declared in HP1's .u scripts are recorded by
                // UEScriptRuntime.RegisterPackage. If the native isn't in
                // any loaded script (very rare; would be an engine-only
                // intrinsic), we fall through to a no-out signature.
                var nativeFn = UEClassRegistry.Shared.FindByNativeIndex(nativeIdx);
                var (args, outRefs) = CollectArgsWithOuts(interp, frame, nativeFn, nativeName ?? "?");
                var cb = (nativeIdx > 0 ? interp.Natives.GetByIndex(nativeIdx) : null)
                      ?? interp.Natives.GetByName(nativeName);
                if (cb == null) {
                    interp.Natives.OnUnknownNative(nativeIdx, nativeName,
                        $"{frame.Object?.Class?.Name}.{frame.Func?.Name}");
                    return new UEEvalResult { Result = UEStmtResult.Next };
                }
                var argsArr = args;
                if (NativeCoverageHook != null)
                {
                    // Intrinsics not declared in loaded .uc surface as "__NFUN_<idx>__"
                    // stubs — prefer the registry's human name for the coverage log.
                    string covName = interp.Natives.NameOfIndex(nativeIdx) ?? nativeName ?? ("idx" + nativeIdx);
                    NativeCoverageHook(covName);
                }
                var ret = cb(interp, frame, argsArr);
                // Iterator natives (AllActors/...) park an iterator in
                // PendingIterator and need the loop-var LValue — that's the
                // single `out` param. Hand it over; the enclosing IteratorToken
                // pushes the iterator and MoveNext writes each item. We skip the
                // normal writeback for that param (the iterator owns it).
                if (interp.PendingIterator != null) {
                    for (int i = 0; i < outRefs.Count; i++)
                        if (outRefs[i].HasValue) { interp.PendingIteratorLoopVar = outRefs[i].Value; break; }
                    interp.PendingIteratorOutRefs = outRefs;   // all out-params (TraceActors needs 3)
                    return new UEEvalResult { Value = ret, Result = UEStmtResult.Next };
                }
                // Native callbacks mutate argsArr[i] for out params. In shipped the native
                // writes out through the GPropAddr obtained in P_GET (iNative branch of
                // CallFunction, Core.dll.c:7741); the port writes after the native in
                // forward order — same result absent aliasing (no carriers).
                // We then write the updated values through to the caller-side
                // LValues we captured in CollectArgsWithOuts.
                for (int i = 0; i < outRefs.Count; i++) {
                    if (outRefs[i].HasValue && i < argsArr.Length)
                        interp.WriteLValue(frame, outRefs[i].Value, argsArr[i]);
                }
                // Not on the iterator branch above: the iterator keeps the out-ref list (UEIterator.OutRefs).
                interp.Pool.ReleaseOutRefs(outRefs, NoOutRefs);
                interp.Pool.ReleaseArgs(argsArr);
                return new UEEvalResult { Value = ret, Result = UEStmtResult.Next };
            });
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
        // HP_FX.Particles.flare4 128x128 — census 2026-10-16 over 127 packages). Misses are not cached: an
        // unresolvable reference throws every time. `_proxyByRef`/`_proxyByRefSpelling` only skip re-resolving
        // the SAME reference (object or exact spelling) once it has a hit.
        static readonly Dictionary<string, UEObject> _texProxies = new(StringComparer.OrdinalIgnoreCase);   // file names: as the catalogue
        static readonly Dictionary<object, UEObject> _proxyByRef = new(ReferenceEqualityComparer.Instance);
        static readonly Dictionary<string, UEObject> _proxyByRefSpelling = new(StringComparer.Ordinal);

        static string TextureIdentity(UELib.Core.UObject exp)
            => HP1.Core.UEClassRegistry.PhysicalPath(exp.Package.FullPackageName) + "#" + exp.ExportTable.Index;

        // A reference that names its full object path ("Package.Group.Name") resolves by that path only.
        static bool HasFullPath(UEResourceRef r)
            => r.ObjectPath is { } p && p.Contains('.') && !p.StartsWith("export:", StringComparison.Ordinal);

        static string RefSpelling(object refVal) => refVal switch
        {
            string s => "s|" + s,
            UEResourceRef r => "r|" + r.Class + "|" + r.ObjectPath + "|" + r.Name + "|" + r.IdentityKey,
            _ => null,
        };

        static bool TryCachedProxy(object refVal, out UEObject proxy)
        {
            proxy = null;
            if (refVal is UELib.Core.UObject) return _proxyByRef.TryGetValue(refVal, out proxy);
            return RefSpelling(refVal) is string k && _proxyByRefSpelling.TryGetValue(k, out proxy);
        }

        static void RememberProxy(object refVal, UEObject proxy)
        {
            if (refVal is UELib.Core.UObject) _proxyByRef[refVal] = proxy;
            else if (RefSpelling(refVal) is string k) _proxyByRefSpelling[k] = proxy;
        }
        // The UE name of a resource reference (string / UEResourceRef / UELib
        // export). Verbatim HP1TextureBank.NameOf — inlined so the Core needs
        // no frontend bank for the pure name extraction.
        static string NameOfResourceRef(object texRef)
        {
            if (texRef == null) return null;
            if (texRef is string s) return s;
            if (texRef is UEResourceRef rr) return rr.Name;
            if (texRef is UELib.Core.UObject uo) return uo.Name?.ToString();
            return null;
        }

        // Texture size for Core owners that need it as a NUMBER, not as a
        // member-access context. The first such owner is AttachDecal: the shipped
        // decal radius is sqrt(DrawScale^2 * USize^2 * 0.5) (Engine.dll 0x10410f2c,
        // `Texture->USize` at offset +0x34). Goes through the same resolver as
        // `Tex.USize` from bytecode, so no second source of sizes appears in the tree.
        public static bool TryTextureSize(object refVal, out int uSize, out int vSize)
        {
            uSize = vSize = 0;
            if (TextureProxyFor(refVal) is not UEObject proxy) return false;
            uSize = ToInt(proxy.GetProp(PK.USize));
            vSize = ToInt(proxy.GetProp(PK.VSize));
            return uSize > 0 && vSize > 0;
        }

        static readonly Dictionary<string, UELib.UnrealPackage> _resourcePackagesByPath =
            new(StringComparer.OrdinalIgnoreCase);

        static UELib.Core.UObject ResourceExportFromDisk(string objectPath, string className)
        {
            var at = UEClassRegistry.Shared?.ResolveResourceExport(objectPath, className);
            if (at is null || at.Value.PackagePath is null) return null;
            // Keyed by the PHYSICAL file: the same package through a symlinked directory is one parse.
            string key = UEClassRegistry.PhysicalPath(at.Value.PackagePath);
            if (!_resourcePackagesByPath.TryGetValue(key, out UELib.UnrealPackage pkg))
            {
                pkg = UELib.UnrealLoader.LoadPackage(at.Value.PackagePath, System.IO.FileAccess.Read);
                pkg.InitializePackage(UELib.UnrealPackage.InitFlags.All);
                _resourcePackagesByPath[key] = pkg;
            }
            return pkg.Exports[at.Value.ExportIndex - 1].Object;
        }

        // The proxy bytecode has already written to (`Tex.bMasked = true` in
        // FEBook.Created) — WITHOUT creating a new one. Shipped holds one UTexture:
        // a script write changes a bit in the same PolyFlags word (+0x80) that
        // UCanvas::DrawTile reads, so the tile flag reader must see the write
        // (UETextureFactsReader.PolyFlagsOfResource).
        public static UEObject ExistingTextureProxy(object refVal)
        {
            if (refVal is null || refVal is UEObject) return null;
            if (TryCachedProxy(refVal, out UEObject hit)) return hit;
            string name = NameOfResourceRef(refVal);
            if (string.IsNullOrEmpty(name) || name == "None") return null;
            var exp = TextureExportOf(refVal, name);
            if (exp is null || exp is UClass || exp.ExportTable is null) return null;
            return _texProxies.TryGetValue(TextureIdentity(exp), out UEObject p) ? p : null;
        }

        // Texture export by reference — shared path of TextureProxyFor and TryTextureDrawScale:
        // import -> export by full path (leaf as fallback), a path-bearing reference -> by that path only
        // (registry, otherwise the resource package from disk); a leaf-only reference -> by leaf.
        static UELib.Core.UObject TextureExportOf(object refVal, string name)
        {
            if (refVal is UEResourceRef full && HasFullPath(full))
                return UEClassRegistry.Shared.FindExport(full.ObjectPath)
                    ?? ResourceExportFromDisk(full.ObjectPath, full.Class);
            var exp = refVal as UELib.Core.UObject;
            if (exp is not null && exp.Table is UELib.UImportTableItem)
            {
                // A reference through another package's IMPORT table (HPMenu.BroomHud →
                // Texture'HPBase.Icons.TimerBarEmpty'): an import has neither tags nor
                // mips, they must be read from the EXPORT — found by full path, leaf
                // as fallback (otherwise all 9 Quidditch maps crash headless on the
                // first BroomHud frame).
                exp = UEClassRegistry.Shared.FindExport(exp.GetPath()) ?? UEClassRegistry.Shared.FindExport(name);
            }
            exp ??= UEClassRegistry.Shared.FindExport(name);
            // A resource package outside the script registry (DynamicLoadObject(
            // "MenuArt.MoonTitle1") in FEBook): ResolveDynamicLoad returned a ref
            // with the FULL path from the on-disk export table — the export is read
            // by the same exact rule (package, outer chain, name, class) as HUD
            // flags (UETextureFacts) and the render pass. Without it the startup
            // menu crashed headless on the first USize read.
            if (exp is null && refVal is UEResourceRef pathRef && pathRef.ObjectPath is not null)
                exp = ResourceExportFromDisk(pathRef.ObjectPath, pathRef.Class);
            return exp;
        }

        // Texture.DrawScale (+0x6c): the export tag, otherwise the class default (no object
        // creation — no ObjectId spent). Needed for decal U/V normalization (D3DDrv SetTexture).
        public static bool TryTextureDrawScale(object refVal, out float drawScale)
        {
            drawScale = 0f;
            string name = NameOfResourceRef(refVal);
            if (string.IsNullOrEmpty(name) || name == "None") return false;
            var exp = TextureExportOf(refVal, name);
            if (exp is null || exp is UClass) return false;
            exp.BeginDeserializing();
            if (exp.Properties != null)
                foreach (var dp in exp.Properties)
                    if (dp?.Name != null && dp.ArrayIndex == 0 && dp.Type == UELib.Types.PropertyType.FloatProperty
                        && dp.Name.ToString().Equals("DrawScale", StringComparison.OrdinalIgnoreCase)
                        && float.TryParse(dp.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v))
                    { drawScale = v; return true; }
            UClass cls = HP1.Core.UEClassRegistry.Shared.Find(exp.Class?.Name ?? "Texture") ?? HP1.Core.UEClassRegistry.Shared.Find("Texture");
            object d = cls is null ? null : UEObject.ClassDefaultScalar(cls, "DrawScale");
            if (d is null) return false;
            drawScale = AsFloat(d);
            return true;
        }

        static UEObject TextureProxyFor(object refVal)
        {
            if (refVal == null || refVal is UEObject) return null;
            // Only texture-family refs get a proxy. Every UE1 texture class name
            // ends in "Texture" (Texture/Fire/Ice/Water/Wave/Wet/Scripted);
            // class refs and other resource kinds (e.g. Class'Tut1Fred', sounds)
            // must fall through to the Accessed-None path exactly as before.
            if (refVal is UClass) return null;   // a class ref is never a texture
            string kind = refVal is UEResourceRef rr ? rr.Class
                        : (refVal as UELib.Core.UObject)?.Class?.Name;
            if (kind != null && !kind.EndsWith("Texture", StringComparison.OrdinalIgnoreCase))
                return null;
            string name = NameOfResourceRef(refVal);
            if (string.IsNullOrEmpty(name) || name == "None") return null;
            if (TryCachedProxy(refVal, out UEObject known)) return known;
            UEObject proxy = null;
            // Origin-of-truth path first: the texture EXPORT's serialized
            // USize/VSize int tags from the original package. Works with no
            // render backend (headless); the extracted PNG the Unity bank
            // loads is derived data of these same ints.
            var exp = TextureExportOf(refVal, name);
            if (exp is UClass) return null;  // name resolved to a class
            string identity = exp?.ExportTable is null ? null : TextureIdentity(exp);
            if (identity is not null && _texProxies.TryGetValue(identity, out UEObject same))
            {
                RememberProxy(refVal, same);
                return same;
            }
            if (exp != null)
            {
                exp.BeginDeserializing();
                int u = 0, v = 0;
                if (exp.Properties != null)
                    foreach (var dp in exp.Properties)
                    {
                        if (dp?.Name == null || dp.Type != UELib.Types.PropertyType.IntProperty) continue;
                        string pn = dp.Name.ToString();
                        if (pn.Equals("USize", StringComparison.OrdinalIgnoreCase)) int.TryParse(dp.Value, out u);
                        else if (pn.Equals("VSize", StringComparison.OrdinalIgnoreCase)) int.TryParse(dp.Value, out v);
                    }
                // The export may have no USize/VSize tags at all
                // (HPBase.Icons.TimerBarEmpty — BroomHud/baseHud): shipped
                // UTexture::Serialize takes the size from the FIRST mip (FMipmap
                // USize/VSize, UELib LegacyMipMap) — the same source as the
                // texture exporter (BspDiffProgram.DumpTextureMips).
                if ((u <= 0 || v <= 0) && exp is UELib.Engine.UTexture ut
                    && ut.Mips is not null && ut.Mips.Count > 0)
                {
                    u = ut.Mips[0].USize;
                    v = ut.Mips[0].VSize;
                }
                if (u > 0 && v > 0)
                {
                    proxy = new UEObject();
                    proxy.Props["USize"] = u;
                    proxy.Props["VSize"] = v;
                    proxy.Props["Name"]  = new UEName(name);
                }
            }
            if (proxy == null || identity is null)
                throw new NotSupportedException(
                    $"texture '{RefSpelling(refVal) ?? name}' unresolvable headless: no USize/VSize tags among registered package exports");
            _texProxies[identity] = proxy;
            RememberProxy(refVal, proxy);
            return proxy;
        }

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
        // flags from the stub before the call meant "no out-params": the argument
        // was collected by value, there was no writeback, and `FlashFog += ...`
        // in PlayerPawn.ViewFlash changed only the callee's local copy. Here the
        // stub is resolved to the real declaration by THE SAME rule (canonical
        // DECLARING class chain, name match, body with tokens; a state stub via
        // the same-named state); the result is cached per stub.
        readonly Dictionary<UFunction, UFunction> _stubDecl = new(UERefComparer<UFunction>.Instance);

        internal UFunction ResolveStubDeclaration(UFunction fn)
        {
            if (fn == null) return null;
            if (fn.FunctionFlags.HasFlag(UELib.Flags.FunctionFlag.Native)) return fn;
            if (_stubDecl.TryGetValue(fn, out var cached)) return cached;
            UFunction real = fn;
            // VM-13: resolution does NOT depend on whether a native with the same
            // name is registered. A `GetByName(name) == null` guard would leave the
            // stub unresolved exactly where the name matches a native name
            // (UWindowWindow.TextSize(Canvas,…, out, out) vs Canvas.TextSize,
            // Object.VSize2D vs Pair(271)), and `out` flags would be read from the
            // stub — as "no out-params".
            if (LoadTokens(fn).Count == 0)
            {
                string fname = fn.Name?.ToString();
                if (fn.Outer is UState stubState && !(fn.Outer is UClass))
                {
                    string stName = stubState.Name?.ToString();
                    string ownerName = (stubState.Outer as UClass)?.Name ?? stubState.Outer?.Name?.ToString();
                    var ownerCanon = UEClassRegistry.Shared.Find(ownerName);
                    if (ownerCanon != null)
                    {
                        try { ownerCanon.BeginDeserializing(); } catch { }
                        for (var c = ownerCanon; c != null && ReferenceEquals(real, fn); c = CanonicalSuper(c))
                            foreach (var st in c.EnumerateFields<UState>())
                            {
                                if (st is UClass) continue;
                                if (!string.Equals(st.Name?.ToString(), stName, StringComparison.OrdinalIgnoreCase)) continue;
                                foreach (var f in st.EnumerateFields<UFunction>())
                                    if (string.Equals(f.Name?.ToString(), fname, StringComparison.OrdinalIgnoreCase)
                                     && !ReferenceEquals(f, fn) && LoadTokens(f).Count > 0)
                                    { real = f; break; }
                                if (!ReferenceEquals(real, fn)) break;
                            }
                    }
                }
                if (ReferenceEquals(real, fn))
                {
                    string declName = (fn.Outer as UClass)?.Name ?? fn.Outer?.Name?.ToString();
                    var declCanon = UEClassRegistry.Shared.Find(declName);
                    if (declCanon != null)
                    {
                        try { declCanon.BeginDeserializing(); } catch { }
                        for (var c = declCanon; c != null && ReferenceEquals(real, fn); c = CanonicalSuper(c))
                            foreach (var f in c.EnumerateFields<UFunction>())
                                if (string.Equals(f.Name?.ToString(), fname, StringComparison.OrdinalIgnoreCase))
                                {
                                    // The real declaration is a script with a body OR a native
                                    // (a native has no body but has parameter flags).
                                    if (!ReferenceEquals(f, fn)
                                        && (LoadTokens(f).Count > 0
                                            || f.FunctionFlags.HasFlag(UELib.Flags.FunctionFlag.Native)))
                                        real = f;
                                    break;
                                }
                    }
                }
            }
            _stubDecl[fn] = real;
            return real;
        }

        static readonly List<UELValueRef?> NoOutRefs = new List<UELValueRef?>(0);
        // Collection list — one per call nesting depth (an argument may itself be
        // a call); returned as an array right away. A new List per call with its
        // array growing was a notable share of tick garbage.
        readonly List<List<object>> _argLists = new List<List<object>>();
        // Frames, argument arrays and out-ref lists of returned calls (UEVmPool).
        internal readonly UEVmPool Pool = new UEVmPool();
        int _argDepth;
        static (object[] args, List<UELValueRef?> outRefs)
            CollectArgsWithOuts(UEInterpreter interp, UEFrame frame, UFunction targetFn, string label)
        {
            // out flags come from the REAL declaration, not from the import stub.
            if (targetFn != null) targetFn = interp.ResolveStubDeclaration(targetFn);
            ParamDesc pd = targetFn != null ? ParamsOf(targetFn) : null;
            bool[] paramOutFlags = pd != null ? pd.IsOut : Array.Empty<bool>();
            int depth = interp._argDepth++;
            if (depth == interp._argLists.Count) interp._argLists.Add(new List<object>(8));
            var args = interp._argLists[depth];
            args.Clear();
            // Without out-params the ref list would be all nulls, and consumers
            // only look at HasValue — use the shared empty list.
            bool anyOut = pd != null && pd.AnyOut;
            var outRefs = anyOut ? interp.Pool.RentOutRefs(paramOutFlags.Length) : NoOutRefs;
            int idx = 0;
            // UObject::CallFunction (Core.dll.c:7715): arguments are evaluated by
            // GNatives in the caller's frame, with object Stack.Object, i.e. the
            // context is reset to Self (the caller). For a context call `A.B(C)` the RHS
            // runs with Context=A (so the function resolves on A), but the
            // ARGUMENT expressions still resolve against the caller — so `C`
            // (e.g. the camera's CutCueString in CutNotifyScript.CutCue(
            // CutCueString)) reads the caller's instance var, not A's. Mirror
            // that by pinning Context to Self (frame.Object) while collecting.
            var savedContext = frame.Context;
            frame.Context = frame.Object;
            try
            {
            while (true)
            {
                while (frame.Cursor < frame.Tokens.Count && IsDebugInfoAt(frame, frame.Cursor))
                    frame.Cursor++;
                if (frame.Cursor >= frame.Tokens.Count) {
                    UETrace.Error(
                        $"[VM] {label}: unexpected EOF before EndFunctionParms in " +
                        $"{frame.Object?.Class?.Name}.{frame.Func?.Name}");
                    break;
                }
                if (TokenKindAt(frame, frame.Cursor) == TokenKind.EndFunctionParms) {
                    frame.Cursor++;
                    break;
                }

                bool isOut = idx < paramOutFlags.Length && paramOutFlags[idx];
                if (isOut)
                {
                    var lref = interp.EvalLValue(frame);
                    args.Add(ReadLValue(frame, lref));
                    outRefs.Add(lref);
                }
                else
                {
                    var r = interp.EvalNext(frame);
                    args.Add(r.Value);
                    if (anyOut) outRefs.Add(null);
                }
                idx++;
            }
            }
            finally { frame.Context = savedContext; interp._argDepth--; }
            // Pooled (UEVmPool): the call site hands the array and the out-ref list back after its writeback.
            object[] result = interp.Pool.RentArgs(args.Count);
            args.CopyTo(result);
            args.Clear();
            return (result, outRefs);
        }

        // Lazy && (130) / || (132) — execAndAnd_BoolBool / execOrOr_BoolBool
        // (Core.dll.c:67281/34421): A; word wSkip; decided — Code += wSkip
        // (includes EndFunctionParms). Stream layout:
        // <A> EX_Skip(<RHS>) EndFunctionParms. Eval A; if it already decides the result
        // (&& on false / || on true) SKIP the EX_Skip-wrapped RHS without evaluating it
        // (so a None-guarding RHS — `X==None || X.foo()` — never derefs None); else eval
        // the RHS. Context pinned to Self while evaluating operands (as CollectArgsWithOuts).
        public UEEvalResult EvalShortCircuit(UEFrame frame, int idx)
        {
            var savedContext = frame.Context;
            frame.Context = frame.Object;
            bool result;
            bool skipped = false;
            try
            {
                bool lhs = ToBool(EvalNext(frame).Value);
                if ((idx == 130) ? !lhs : lhs)   // && decided by false, || by true
                {
                    result = idx == 132;          // && → false, || → true
                    skipped = SkipSkipToken(frame);
                }
                else
                {
                    result = ToBool(EvalNext(frame).Value);   // eval RHS through the SkipToken
                }
            }
            finally { frame.Context = savedContext; }
            // THE TERMINATOR IS READ ONLY WITHOUT A JUMP. The EX_Skip size already
            // includes this native's EndFunctionParms (measured — see SkipSkipToken),
            // so after a taken short-circuit the cursor is PAST it. An unconditional
            // read took the terminator of the ENCLOSING expression, and its argument
            // collection swallowed the rest of the body: in `baseHarry.stateDead` this
            // gave an endless loop (Sleep/FinishAnim stopped suspending the frame,
            // because the latent check in Run comes AFTER EvalNext, which never
            // returned) — player death hung the world.
            if (!skipped)
            {
                while (frame.Cursor < frame.Tokens.Count && frame.Tokens[frame.Cursor] is Tok.DebugInfoToken)
                    frame.Cursor++;
                if (frame.Cursor < frame.Tokens.Count && frame.Tokens[frame.Cursor] is Tok.EndFunctionParmsToken)
                    frame.Cursor++;
            }
            return new UEEvalResult { Value = result, Result = UEStmtResult.Next };
        }

        // Advance past an EX_Skip + its wrapped child WITHOUT evaluating it.
        //
        // WHAT `Size` COVERS — MEASURED ON SHIPPED BYTECODE, not inferred.
        // `baseHarry.stateDead`, condition `!(BossTarget != none &&
        // BossTarget.IsA('BossQuirrel'))`:
        //     EX_Skip header         pos 13   (opcode 1 + word 2 → body from 16)
        //     Size                   18
        //     RHS body               pos 16..32   — 17 bytes
        //     EndFunctionParms "&&"  pos 33       — 1 byte
        //     EndFunctionParms "!"   pos 34
        // 17 + 1 = 18 = Size. So the shipped `wSkip` INCLUDES the parameter
        // terminator of the native that owns the skip, and the jump lands
        // RIGHT AFTER it (pos 34).
        //
        // Consequence: EndFunctionParms must NOT be consumed after a taken jump —
        // it is already passed, and an extra read takes the terminator of the
        // ENCLOSING expression.
        //
        // Returns true if the jump happened. If the target cannot be mapped, the
        // body is evaluated the usual way (the value is correct, no short-circuit
        // this time) — the terminator is still ahead, returns false.
        bool SkipSkipToken(UEFrame frame)
        {
            while (frame.Cursor < frame.Tokens.Count && frame.Tokens[frame.Cursor] is Tok.DebugInfoToken)
                frame.Cursor++;
            if (frame.Cursor < frame.Tokens.Count && frame.Tokens[frame.Cursor] is Tok.SkipToken sk)
            {
                frame.Cursor++;   // past the SkipToken header
                if (frame.Cursor < frame.Tokens.Count
                 && frame.OffsetToIndex.TryGetValue(frame.Tokens[frame.Cursor].Position + sk.Size, out var ti))
                {
                    frame.Cursor = ti;
                    return true;
                }
                EvalNext(frame);   // couldn't map the target → evaluate the child instead
            }
            else EvalNext(frame);  // no SkipToken (unexpected) → evaluate the RHS normally
            return false;
        }

        void Register<T>(UETokenHandler h) where T : Tok.Token
            => _handlers[typeof(T)] = h;

        // Handlers by token index — per function token stream (shared by all its
        // frames in this interpreter). The _handlers table is filled only by the
        // constructor, so the cache never goes stale.
        readonly Dictionary<List<Tok.Token>, UETokenHandler[]> _handlerCache =
            new(UERefComparer<List<Tok.Token>>.Instance);
        UETokenHandler[] HandlersFor(List<Tok.Token> toks)
        {
            lock (_handlerCache)
            {
                if (!_handlerCache.TryGetValue(toks, out var arr))
                    _handlerCache[toks] = arr = new UETokenHandler[toks.Count];
                return arr;
            }
        }

        // Token kind by index, per token stream (shared by all its frames): the `is` tests against UELib's token
        // hierarchy ran on every EvalNext and every call argument. A pure function of the stream, which is immutable
        // after deserialization; an index outside the cached array falls back to the type test.
        internal enum TokenKind : byte { Other, DebugInfo, EndFunctionParms }
        static readonly Dictionary<List<Tok.Token>, TokenKind[]> _tokenKinds = new(UERefComparer<List<Tok.Token>>.Instance);
        static TokenKind KindOf(Tok.Token t)
            => t is Tok.DebugInfoToken ? TokenKind.DebugInfo
             : t is Tok.EndFunctionParmsToken ? TokenKind.EndFunctionParms : TokenKind.Other;
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static TokenKind TokenKindAt(UEFrame frame, int i)
        {
            var k = frame.TokenKinds ?? TokenKindsOf(frame);
            return (uint)i < (uint)k.Length ? k[i] : KindOf(frame.Tokens[i]);
        }
        static TokenKind[] TokenKindsOf(UEFrame frame)
        {
            var toks = frame.Tokens;
            TokenKind[] k;
            lock (_tokenKinds)
            {
                if (!_tokenKinds.TryGetValue(toks, out k))
                {
                    k = new TokenKind[toks.Count];
                    for (int j = 0; j < k.Length; j++) k[j] = KindOf(toks[j]);
                    _tokenKinds[toks] = k;
                }
            }
            return frame.TokenKinds = k;
        }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static bool IsDebugInfoAt(UEFrame frame, int i) => TokenKindAt(frame, i) == TokenKind.DebugInfo;

        // Read the next token in the stream as an LValue (storage location)
        // rather than its current value. Used by LetToken/LetBoolToken to
        // resolve the LHS. In shipped the lvalue is GPropAddr/GProperty, set by the
        // variable tokens (execLocalVariable / execInstanceVariable /
        // execBoolVariable) and consumed by execLet/execLetBool; we recover the
        // same information by peeking the token type and extracting its
        // name/owner.
        // AllowValueLocalStructWrites: the flag is only saved/set/restored in
        // Invoke; nothing in the tree reads it to change struct member writes.
        // Removing the flag is an author decision.
        public bool AllowValueLocalStructWrites;

        // MSVCRT `%f` (appSprintf -> _vsnwprintf): finite values — 6 digits;
        // special values — "1.#INF00", "-1.#INF00", indefinite (0/0: quiet NaN
        // without payload) "-1.#IND00", other NaNs "1.#QNAN0" with their sign.
        // .NET would print "∞"/"NaN".
        internal static string PercentF(float f)
        {
            if (float.IsInfinity(f)) return f > 0 ? "1.#INF00" : "-1.#INF00";
            if (float.IsNaN(f))
            {
                uint bits = (uint)BitConverter.SingleToInt32Bits(f);
                // The x87 indefinite is 0xFFC00000; an ARM host produces it with a
                // POSITIVE sign (0x7FC00000), so the sign does not decide here.
                if ((bits & 0x7FFFFFFFu) == 0x7FC00000u) return "-1.#IND00";
                return ((bits & 0x80000000u) != 0 ? "-" : "") + "1.#QNAN0";
            }
            return f.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
        }

        // ArrayDim of a static array from the array-expression token that comes
        // next in the stream ([ArrayElement, Key, Array]). 0 — a form where the
        // property is not visible from the token (then the bound is the list length).
        static int StaticArrayDimAt(UEFrame frame)
        {
            int c = frame.Cursor;
            while (c < frame.Tokens.Count && frame.Tokens[c] is Tok.DebugInfoToken) c++;
            if (c >= frame.Tokens.Count) return 0;
            UObject p = frame.Tokens[c] switch
            {
                Tok.LocalVariableToken lv => lv.Object,
                Tok.InstanceVariableToken iv => iv.Object,
                Tok.DefaultVariableToken dv => dv.Object,
                Tok.StructMemberToken sm => sm.Property,
                _ => null,
            };
            return (p as UProperty)?.ArrayDim ?? 0;
        }

        // execArrayElement (Core 0x101322f2..0x1013232b): an index outside
        // [0, ArrayDim) is logged "out of bounds" and CLAMPED to the bound
        // (<0 -> 0, >=dim -> dim-1); reads and writes go to that element.
        static int StaticArrayIndex(UEFrame frame, int idx, int dim, System.Collections.IList list)
        {
            if (dim <= 0) dim = list?.Count ?? 0;
            if (idx >= 0 && idx < dim) return idx;
            UETrace.Warn($"[VM] Accessed array out of bounds ({idx}/{dim}) in "
                       + $"{frame.Object?.Class?.Name}.{frame.Func?.Name}");
            if (dim <= 0) return idx;
            return idx < 0 ? 0 : dim - 1;
        }

        // execDynArrayElement (Core 0x10132522..0x101325ab), shared by read and
        // write: idx < 0 — log and 0 WITHOUT growth; idx >= Num — grow to idx+1
        // zero-filled. The array is resolved as an lvalue so that a missing one
        // is created in its slot.
        List<object> DynArrayForElement(UEFrame frame, int idx, out int outIdx)
        {
            var arrRef = EvalLValue(frame);
            var list = ReadLValue(frame, arrRef) as List<object>;
            if (list == null) {
                list = new List<object>();
                WriteLValue(frame, arrRef, list);
            }
            if (idx < 0) {
                UETrace.Warn($"[VM] Accessed array out of bounds ({idx}/{list.Count}) in "
                           + $"{frame.Object?.Class?.Name}.{frame.Func?.Name}");
                idx = 0;
            } else {
                while (list.Count <= idx) list.Add(null);   // zeros: null reads as the type's zero
            }
            outIdx = idx;
            return list;
        }

        public UELValueRef EvalLValue(UEFrame frame)
        {
            while (frame.Cursor < frame.Tokens.Count && IsDebugInfoAt(frame, frame.Cursor))
                frame.Cursor++;
            if (frame.Cursor >= frame.Tokens.Count) {
                UETrace.Error($"[VM] EvalLValue: unexpected EOF in " +
                               $"{frame.Object?.Class?.Name}.{frame.Func?.Name}");
                return default;
            }
            var tok = frame.Tokens[frame.Cursor++];
            switch (tok)
            {
                case Tok.LocalVariableToken lv:
                {
                    var pr = PropRefAt(frame, tok, lv.Object);
                    if (pr.PropId >= 0)
                    {
                        var layout = frame.Variables.Layout;
                        int slot = layout.SlotForProp(pr.PropId);
                        return new UELValueRef {
                            Kind = UELValueKind.Local,
                            Name = layout.KeyAt(slot),
                            SlotLayout = layout,
                            Slot = slot,
                        };
                    }
                    return new UELValueRef {
                        Kind = UELValueKind.Local,
                        Name = pr.Name,
                    };
                }
                case Tok.InstanceVariableToken iv:
                {
                    var pr = PropRefAt(frame, tok, iv.Object);
                    var ctx = frame.Context;
                    if (ctx != null && pr.PropId >= 0)
                    {
                        var layout = ctx.Props.Layout;
                        int slot = layout.SlotForProp(pr.PropId);
                        return new UELValueRef {
                            Kind = UELValueKind.Instance,
                            Name = layout.KeyAt(slot),
                            Target = ctx,
                            SlotLayout = layout,
                            Slot = slot,
                        };
                    }
                    return new UELValueRef {
                        Kind = UELValueKind.Instance,
                        Name = UEObject.PropKey(ctx?.Class, PropDeclClass(iv.Object),
                                                iv.Object?.Name?.ToString()),
                        Target = ctx,
                    };
                }
                case Tok.BoolVariableToken:
                    // execBoolVariable (Core.dll.c:45463): the inner token gives the
                    // address, GProperty = the bool property. The child is the real
                    // lvalue.
                    return EvalLValue(frame);

                case Tok.StructMemberToken sm:
                    // execStructMember (Core.dll.c:4845): GPropAddr += Offset — a member
                    // write always lands in the parent's storage. Two cases:
                    //  - parent is a UEObject bag (HUD curIconMessage/DrawColor,
                    //    Cast[i]/Locs[i]) → reference-shared, write the member on the
                    //    bag directly (always works).
                    //  - parent is a VALUE struct (Vector/Rotator local or instance
                    //    var) → read-modify-write the parent lvalue.
                    // ALWAYS ON: gating this behind AllowValueLocalStructWrites dropped
                    // member writes everywhere else, while shipped always writes to the
                    // parent (execStructMember); the drop broke every `local.Z = ...`,
                    // e.g. BaseCam.GeneralStationaryModeCamera line 704
                    // `TargetPoint.Z = eye+CameraHeight` (the gameplay camera sat BELOW Harry).
                    // (SettleOntoFloor / de-graze, 2026-06-08/09) — re-verified after enable.
                    {
                        var pr = PropRefAt(frame, tok, sm.Property);
                        string fieldName = pr.Name;
                        var pref = EvalLValue(frame);              // parent as lvalue
                        var pval = ReadLValue(frame, pref);
                        if (pval is UEObject bag)
                        {
                            if (fieldName is null)
                                return new UELValueRef { Kind = UELValueKind.Instance, Name = fieldName, Target = bag };
                            var layout = bag.Props.Layout;
                            return new UELValueRef { Kind = UELValueKind.Instance, Name = fieldName, Target = bag,
                                                     SlotLayout = layout, Slot = layout.SlotForKey(pr.NameKeyId) };
                        }
                        return new UELValueRef { Kind = UELValueKind.StructMember, Name = fieldName, ParentRef = pref };
                    }

                case Tok.DynamicArrayElementToken:
                    // UnrealScript dynamic arrays AUTO-GROW on indexed assignment:
                    // `A[i] = x` with i >= Length resizes the array to i+1 (the
                    // engine's dynamic-array element store). Static ArrayElementToken
                    // (below) never grows. Must come BEFORE the ArrayElementToken case
                    // because it derives from it (a type-pattern would otherwise swallow
                    // it). Resolve the array itself as an lvalue so a null/short array
                    // can be created and extended in place. Layout [token, Key, Array].
                    {
                        var keyR = EvalNext(frame);
                        var list = DynArrayForElement(frame, ToInt(keyR.Value), out int idx);
                        return new UELValueRef {
                            Kind = UELValueKind.ArrayIndex,
                            ListTarget = list,
                            IntIndex = idx,
                        };
                    }

                case Tok.ArrayElementToken:
                    // execArrayElement (Core.dll.c:46481): GPropAddr +=
                    // ElementSize·idx after clamping the index. Our runtime stores arrays
                    // as IList, which supports indexed assignment directly.
                    // UELib emits children in order [ArrayElementToken,
                    // KeyExpr, ArrayExpr].
                    {
                        var keyR = EvalNext(frame);
                        int dim = StaticArrayDimAt(frame);
                        var arrR = EvalNext(frame);
                        var list = arrR.Value as System.Collections.IList;
                        return new UELValueRef {
                            Kind = UELValueKind.ArrayIndex,
                            ListTarget = list,
                            IntIndex = StaticArrayIndex(frame, ToInt(keyR.Value), dim, list),
                        };
                    }

                case Tok.ContextToken:
                    // Assignment through a context chain: `A.B = X`. Mirrors
                    // the read-side execContext (Core.dll.c:47177) but the
                    // member side is resolved as an lvalue. Evaluate the
                    // object side, switch Context to it, resolve the member
                    // lvalue against that Context, then restore. (For HP1:
                    // `me.actorName.DesiredRotation = ...` etc.)
                    {
                        var objR = EvalNext(frame);
                        var savedCtx = frame.Context;
                        // A texture reference uses the same proxy as reads
                        // (ContextToken above): `LogoTexture1.bMasked = true`
                        // (FEBook.uc:495) and `Background.bTransparent = true`
                        // (baseWarning.uc:34) would otherwise lose the write (Target=null).
                        frame.Context = objR.Value as UEObject ?? TextureProxyFor(objR.Value);
                        UELValueRef inner;
                        try { inner = EvalLValue(frame); }
                        finally { frame.Context = savedCtx; }
                        return inner;
                    }

                default:
                    UETrace.Error(
                        $"[VM] EvalLValue: unsupported LHS token {tok.GetType().Name} in " +
                        $"{frame.Object?.Class?.Name}.{frame.Func?.Name} at pos {tok.Position}");
                    return default;
            }
        }

        // LValue plumbing lives in the Core (UEValue); instance/static
        // forwarders keep every call site unchanged.
        public void WriteLValue(UEFrame frame, UELValueRef lref, object value)
            => UEValue.WriteLValue(frame, lref, value);
        public static object ReadLValue(UEFrame frame, UELValueRef lref)
            => UEValue.ReadLValue(frame, lref);
        static object GetStructMember(object s, string m) => UEValue.GetStructMember(s, m);
        static object SetStructMember(object s, string m, object value)
            => UEValue.SetStructMember(s, m, value);

        // execSwitch (Core.dll.c:44827): case loop until NextOffset == 0xFFFF
        // (default); a match exits into the body; a miss — Code = Script + 1 +
        // NextOffset. Walk the sequence of CaseTokens; for each:
        //   - default case (CodeOffset == 0xFFFF, no Condition) — break,
        //     fall through to its body (the cursor is positioned after it).
        //   - regular case — evaluate its Condition, compare with the
        //     switch value. On match, break (fall through). On mismatch,
        //     jump to NextOffset (the next case's start byte).
        // The case body executes via the normal Run loop after this method
        // returns. CaseToken hit standalone (fall-through between cases) is
        // a no-op (Tok.CaseToken handler below).
        void ProcessSwitch(UEFrame frame, object condVal)
        {
            while (true)
            {
                while (frame.Cursor < frame.Tokens.Count
                       && frame.Tokens[frame.Cursor] is Tok.DebugInfoToken)
                    frame.Cursor++;
                if (frame.Cursor >= frame.Tokens.Count) {
                    UETrace.Error(
                        $"[VM] Switch: ran out of tokens looking for a case in " +
                        $"{frame.Object?.Class?.Name}.{frame.Func?.Name}");
                    return;
                }
                var tok = frame.Tokens[frame.Cursor++];
                if (!(tok is Tok.CaseToken ct)) {
                    UETrace.Error(
                        $"[VM] Switch: expected CaseToken, got {tok.GetType().Name} " +
                        $"at pos {tok.Position} in {frame.Object?.Class?.Name}.{frame.Func?.Name}");
                    return;
                }
                if (ct.CodeOffset == ushort.MaxValue) {
                    // Default case (UELib JumpTokens.cs:548 `IsDefault`).
                    return;
                }
                var caseVal = EvalNext(frame).Value;
                if (UEValue.SwitchCaseMatches(condVal, caseVal)) return;  // matched, fall through to body
                // Mismatch: jump to NextOffset (start of the next case).
                if (frame.OffsetToIndex.TryGetValue(ct.CodeOffset, out var idx)) {
                    frame.Cursor = idx;
                } else {
                    UETrace.Error(
                        $"[VM] Switch: NextOffset 0x{ct.CodeOffset:X} not found in " +
                        $"{frame.Object?.Class?.Name}.{frame.Func?.Name}");
                    return;
                }
            }
        }

        // Comparisons: execSwitch — appStricmp for strings (size 0), otherwise
        // appMemcmp by size (UEValue.SwitchCaseMatches); StructCmp — property
        // Identical (e.g. UStrProperty::Identical, Core.dll.c:36474).
        // EX_StructCmpEq semantics: two struct instances are equal iff EVERY member is equal.
        // Struct values here are UEObject(IsStruct) (custom structs, Props dict) or intrinsic
        // value-structs (Vector3/UERotator/Color). Field-by-field via ValueEquals, which coerces
        // null↔type-zero — so a struct compares equal to a default/zero struct (the VM's
        // absent-property-reads-as-zero convention). (#audit gap 12)
        // The VM value semantics live in the Core (HP1.UEScript.UEValue);
        // these forwarders keep every existing call site unchanged.
        public static bool StructEquals(object a, object b) => UEValue.StructEquals(a, b);
        public static bool ValueEquals(object a, object b) => UEValue.ValueEquals(a, b);
        public static byte AsByte(object v) => UEValue.AsByte(v);
        public static Vector3 AsVector3(object v) => UEValue.AsVector3(v);
        public static UERotator AsRotator(object v) => UEValue.AsRotator(v);
        public static float AsFloat(object v) => UEValue.AsFloat(v);
        static int LeadingAtoi(string s) => UEValue.LeadingAtoi(s);
        static float LeadingAtof(string s) => UEValue.LeadingAtof(s);
        public static int ToInt(object v) => UEValue.ToInt(v);
        public static bool ToBool(object v) => UEValue.ToBool(v);

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
        // (obj, fn, args) was allocated on EVERY call.
        object InvokePortOverride(UEObject obj, UFunction fn, object[] args, UEPortScriptOverrides.Handler portBody)
            => portBody(this, obj, args, () =>
            {
                UFunction prev = _portBypass;
                _portBypass = fn;
                try { return Invoke(obj, fn, args); }
                finally { _portBypass = prev; }
            });

        // Shipped gate — IsProbing in UObject::ProcessInternal (Core.dll.c:21624)
        // before ANY script body, including an explicit script call
        // `A.Trigger(...)` (Core 0x101431df, disasm); the mask bit is
        // FStateFrame.ProbeMask, edited by natives Disable/Enable (execDisable
        // Core.dll.c:31010, execEnable :54226). The check sits in Invoke before
        // Run. (Example: Engine.Inventory.Sleeping has no Touch stub at all,
        // only a cleared IgnoreMask bit.) There are no other gates: shipped stores
        // the disable only in the mask (D-20, C7). runtime_poc/verify_architecture.py
        // ("dual store") guards against a name-based disable map coming back.
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
                                // by index; no handler — loud (§8), not an empty
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
        // dictionary per call was a notable share of hot-script overhead.
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
        // shipped (author decision 2026-10-15: fatal like the original, D-13).
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
        // WHY. The runaway message used to name ONLY the innermost frame — the one
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

        // Judge tap (tools/swarc_oracle): sees every top-level statement before and after it runs; observes only,
        // never changes VM state. Null outside the SwARc corpus dump (runtime_poc/HeadlessVmJudgeDump.cs).
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

        // ── State machine ────────────────────────────────────────────────
        // GotoState is documented at its body below, per UObject::GotoState
        // (Core.dll.c:58247). The body starts only when the actor's next Tick
        // drives StateFrame.
        // State frame FROM A SAVE (UELevelRuntime.SaveGame): built the same way as
        // in EnterState (Func/Tokens/OffsetToIndex/ProbeMask), but without
        // BeginState/EndState events and without resetting position — Cursor, the
        // latent wait and variables come from the file. stateName/ownerClassName name the CODE node (FStateFrame.Node);
        // the state node (StateNode = CurrentStateName) is a separate saved field the caller has already restored —
        // UObject::Serialize writes Node (+0x4) and StateNode (+0x1c) separately (Core.dll.c:14441-14442), and a
        // frame running a super-state label (ResolveStateLabel) has Node != StateNode.
        internal void RestoreStateFrame(UEObject obj, string stateName, string ownerClassName, int cursor, ulong probeMask,
            LatentRunState latent, float sleepTimeLeft, Dictionary<string, object> variables, bool cursorIsCodeOffset = false)
        {
            // The state owner is the class whose code was running (see UELevelRuntime.SaveGame):
            // look up the state among THIS class's own fields.
            UState state = null;
            UClass owner = string.IsNullOrEmpty(ownerClassName) ? null : UEClassRegistry.Shared.Find(ownerClassName);
            if (owner is not null)
                foreach (var st in owner.EnumerateFields<UState>())
                    if (st is not UClass && string.Equals(st.Name?.ToString(), stateName, StringComparison.OrdinalIgnoreCase))
                    { state = st; break; }
            state ??= FindState(obj.Class, stateName)
                ?? throw new System.IO.InvalidDataException(
                    $"RestoreStateFrame: {obj.Class?.Name} has no state {stateName} (owner {ownerClassName})");
            var sf = new UEFrame { Object = obj, Context = obj, Func = state };
            sf.Tokens = LoadTokens(state);
            sf.OffsetToIndex = OffsetMapFor(sf.Tokens);
            // Save format 3: the bytecode offset of the token (-1 = past the last one), not the port's token index.
            if (cursorIsCodeOffset)
                cursor = cursor == -1 ? sf.Tokens.Count
                    : sf.OffsetToIndex.TryGetValue(cursor, out int idx) ? idx
                    : throw new System.IO.InvalidDataException(
                        $"RestoreStateFrame: code offset {cursor} is not a token start in state {stateName}");
            if (cursor < 0 || cursor > sf.Tokens.Count)
                throw new System.IO.InvalidDataException(
                    $"RestoreStateFrame: position {cursor} outside the code of state {stateName} ({sf.Tokens.Count} tokens)");
            sf.Cursor = cursor;
            sf.ProbeMask = probeMask;
            sf.LatentState = latent;
            sf.SleepTimeLeft = sleepTimeLeft;
            sf.ResetVariables();
            if (variables is not null)
                foreach (var kv in variables) sf.Variables[kv.Key] = kv.Value;
            obj.StateFrame = sf;
        }

        // Full "state + label" transition for C# callers (frontends, probes):
        // UObject::GotoState, then GotoLabel on success — exactly like
        // execGotoState, but without its skip for the same name.
        public void EnterState(UEObject obj, string stateName, string label)
        {
            if (GotoStateCore(obj, stateName, label) == GotoStateResult.Success)
                GotoLabel(obj, label);
        }

        public enum GotoStateResult { NotFound = 0, Success = 1, Preempted = 2 }

        // UObject::GotoState (Core.dll.c:58247-58388):
        //   * LatentAction = 0 at entry, Code = NULL after EndState — state code does
        //     not run until the caller sets a label (GotoLabel);
        //   * EndState/BeginState go through ProcessEvent (vtbl+0x10) with its gates;
        //   * "same state" compares NAMES (FName), not nodes: the frame's node
        //     may be an ancestor state a label led to;
        //   * EndState — only if IsProbing(EndState) under the OLD mask and not
        //     inside a running EndState (RF 0x2000); if the object changed state
        //     during EndState (RF 0x1000), the outer transition is preempted (2);
        //   * the probe mask is always recomputed: (Class|State)&Ignore;
        //   * BeginState — on a name change and IsProbing(BeginState) under the NEW
        //     mask; a nested state change again preempts (2);
        //   * success sets RF 0x1000; state not found — node = class, 0.
        public GotoStateResult GotoStateCore(UEObject obj, string stateName, string label = null)
        {
            if (obj?.Class == null) return GotoStateResult.NotFound;

            // "Auto" → the class's auto-state: UObject::GotoState (Core.dll.c:58247)
            // with name == _Names[0x68] takes the first UState with StateFlags & 2
            // while iterating the class fields (TFieldIterator).
            if (string.Equals(stateName, "Auto", StringComparison.OrdinalIgnoreCase))
                stateName = FindAutoState(obj.Class)?.Name?.ToString();

            UState newState = string.IsNullOrEmpty(stateName) ? null
                            : FindState(obj.Class, stateName);
            string oldStateName = obj.CurrentStateName;
            string newStateName = newState?.Name?.ToString();
            bool changing = !string.Equals(oldStateName, newStateName, StringComparison.OrdinalIgnoreCase);

            UEFrame sf = obj.StateFrame;
            if (sf == null)
                sf = obj.StateFrame = new UEFrame { Object = obj, Context = obj };
            // LatentAction = 0 at ENTRY, before EndState (0x10131bf7 `mov [eax+0x28], edi`,
            // Core.dll.c:58290): EndState already sees no latent (StopWaiting reads it).
            // Stop is the port's Code = NULL, not a latent — it is left alone.
            if (sf.LatentState != LatentRunState.Stop) sf.LatentState = LatentRunState.Continue;

            if (HP1.Core.UETrace.Enabled)
                HP1.Core.UETrace.Log(
                    $"goto_state actor={obj.GetProp(PK.Name)} class={obj.Class?.Name}"
                    + $" from={oldStateName ?? "None"} to={newState?.Name?.ToString() ?? "None"}"
                    + $" label={(string.IsNullOrEmpty(label) ? "None" : label)} changing={changing}");

            // Route log: a state change is the main visible sign that a route
            // participant progressed. The dedup key includes BOTH ends of the
            // transition, otherwise Walking->Falling and Falling->Walking of one
            // pawn would collapse into one line and lose direction.
            if (HP1.Core.UEPlaythroughLog.Enabled && changing)
            {
                string who = obj.GetProp(PK.Name)?.ToString() ?? obj.Class?.Name?.ToString() ?? "?";
                string to = newState?.Name?.ToString() ?? "None";
                HP1.Core.UEPlaythroughLog.Note("state", $"{who}:{oldStateName}->{to}",
                    $"{who}({obj.Class?.Name}) {oldStateName ?? "None"} -> {to}"
                    + (string.IsNullOrEmpty(label) ? "" : $" label={label}"));
                // A CHECKPOINT IS AN ACTION, NOT INITIALIZATION. Entering the auto
                // state from None (e.g. `None -> NormalTrigger` of SpellLearnTrigger0
                // at tick 0) is actor bootstrap and would falsely claim route progress,
                // so the marker requires a non-empty previous state.
                if (!string.IsNullOrEmpty(oldStateName))
                    HP1.Core.UEPlaythroughLog.MarkIfCheckpoint(who, $"state {oldStateName}->{to}");
            }

            if (!string.IsNullOrEmpty(oldStateName) && changing
                && IsProbing(obj, "EndState") && !obj.InEndState)
            {
                obj.StateChangedFlag = false;
                obj.InEndState = true;
                // Dispatched via vtbl+0x10 = ProcessEvent (0x10131cdd): the AActor
                // (bBegunPlay) and UObject (IsProbing, !IsPendingKill) gates apply —
                // the one owner of those gates is UELifecycle.CallEvent.
                UELifecycle.CallEvent(this, obj, "EndState");
                obj.InEndState = false;
                if (obj.StateChangedFlag) return GotoStateResult.Preempted;
            }

            obj.CurrentStateName = newStateName;
            sf.ProbeMask = UEProbe.MaskFor(obj.Class, newState);
            sf.Func = newState;
            sf.Tokens = newState is null ? null : LoadTokens(newState);
            sf.OffsetToIndex = newState is null ? null : OffsetMapFor(sf.Tokens);
            sf.ResetVariables();
            sf.LatentState = LatentRunState.Stop;          // Code = NULL
            sf.Cursor = sf.Tokens?.Count ?? 0;
            if (newState == null) return GotoStateResult.NotFound;

            if (changing && IsProbing(obj, "BeginState"))
            {
                obj.StateChangedFlag = false;
                UELifecycle.CallEvent(this, obj, "BeginState");   // ProcessEvent, 0x10131d73
                if (obj.StateChangedFlag) return GotoStateResult.Preempted;
            }
            obj.StateChangedFlag = true;
            return GotoStateResult.Success;
        }

        // UObject::GotoLabel: LatentAction = 0; label None/empty — 'Begin';
        // not found — Code = NULL (frame halts).
        public bool GotoLabel(UEObject obj, string label)
        {
            UEFrame sf = obj?.StateFrame;
            if (sf?.Func == null || sf.Tokens == null) return false;
            int start = ResolveStateLabel(sf, label);
            if (start < 0)
            {
                sf.LatentState = LatentRunState.Stop;
                sf.Cursor = sf.Tokens.Count;
                return false;
            }
            sf.Cursor = start;
            sf.LatentState = LatentRunState.Continue;
            return true;
        }



        // UObject::GotoLabel (Core.dll.c:45497): LatentAction = 0; walks StateNode
        // → SuperField over LabelTable; miss or None → Code = NULL. The None → Begin
        // substitution comes from execGotoState (Core.dll.c:33347, _Names[0x20]),
        // so callers (GotoState and EnterState) own it here, while the opcode
        // goto 'X' handles None itself (Stop, GotoLabelToken). Returns -1 when the
        // label is missing, and then LatentState = Stop: a state with NO "Begin" label (e.g. Mover's
        // TriggerToggle, whose only labels are Open:/Close:) runs NO latent code
        // on entry; it idles until a function (Trigger) jumps to a label.
        // Returns -1 to signal "no entry point" so RunStateBody stops the frame
        // instead of falling through to statement 0 (which opened every door at load).
        int ResolveStateLabel(UEFrame frame, string label)
        {
            string target = (string.IsNullOrEmpty(label)
                          || string.Equals(label, "None", StringComparison.OrdinalIgnoreCase))
                ? "Begin" : label;
            // UObject::GotoLabel (Core 0x10131e08..0x10131e3c): walk from StateNode
            // (the most-derived state with the current name — FindState, as for
            // state function lookup) along SuperField: this covers both `extends`
            // and the same-named ancestor state (the compiler puts it in Super).
            // The code node (frame.Func) may already be an ancestor after a prior
            // transition — the search still starts at StateNode. Found in another
            // node — the frame switches to that node's code.
            var obj = frame.Object;
            UState node = (obj != null && !string.IsNullOrEmpty(obj.CurrentStateName)
                           ? FindState(obj.Class, obj.CurrentStateName) : null)
                          ?? frame.Func as UState;
            for (UState st = node; st != null; st = CanonicalSuperState(st))
            {
                var pm = GetLabelMap(st);
                if (pm == null || !pm.TryGetValue(target, out int ppos)) continue;
                if (!ReferenceEquals(st, frame.Func))
                {
                    frame.Func = st;
                    frame.Tokens = LoadTokens(st);
                    frame.OffsetToIndex = OffsetMapFor(frame.Tokens);
                }
                if (frame.OffsetToIndex.TryGetValue(ppos, out int pidx)) return pidx;
            }
            return -1;   // label not found in the chain → Frame::GotoLabel sets LatentState = Stop
        }

        // UELib parses a function/state's LabelTable (name → code offset) into
        // a private `_Labels` list on the ByteCodeManager but does not expose
        // it. We read that already-computed data via reflection (not faking
        // behaviour — surfacing parsed metadata) and cache per UStruct.
        // ULabelEntry = { string Name; int Position }.
        static readonly Dictionary<UStruct, Dictionary<string, int>> _labelCache = new(UERefComparer<UStruct>.Instance);
        static System.Reflection.FieldInfo _labelsField, _labelNameField, _labelPosField;

        static Dictionary<string, int> GetLabelMap(UStruct node)
        {
            if (node == null) return null;
            if (_labelCache.TryGetValue(node, out var cached)) return cached;

            var map = new Dictionary<string, int>(UEPropNameComparer.Instance);
            var bcm = node.ByteCodeManager;
            if (bcm != null)
            {
                if (_labelsField == null)
                    _labelsField = bcm.GetType().GetField("_Labels",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (_labelsField?.GetValue(bcm) is System.Collections.IEnumerable labels)
                    foreach (var e in labels)
                    {
                        if (_labelNameField == null) {
                            _labelNameField = e.GetType().GetField("Name");
                            _labelPosField  = e.GetType().GetField("Position");
                        }
                        string nm = _labelNameField?.GetValue(e) as string;
                        int pos = (_labelPosField?.GetValue(e) as int?) ?? -1;
                        if (!string.IsNullOrEmpty(nm) && pos >= 0
                         && !string.Equals(nm, "None", StringComparison.OrdinalIgnoreCase))
                            map[nm] = pos;
                    }
            }
            _labelCache[node] = map;
            return map;
        }

        // HP1's packages re-export base classes as empty IMPORT STUBS — e.g.
        // HarryPotter.BaseCam and Tut1.baseChar each have 0 children and a
        // null Super. UELib links a subclass's Super to that local stub rather
        // than to the real export (HPBase.BaseCam, 32 functions), so the
        // inheritance chain dead-ends at every package boundary. CanonicalSuper
        // redirects a stub super to the registered export of the same name
        // (the version that actually carries the script content), stitching
        // the chain back together. This is load-bearing for ALL cross-package
        // method/state resolution, not just cameras.
        // Forwarding shims to the Core registry (HP1.Core.UEClassRegistry),
        // which now OWNS class-chain resolution. Kept static so the 130+
        // existing call sites are unchanged.
        public static UClass CanonicalSuper(UClass c)
            => UEClassRegistry.Shared.CanonicalSuper(c);

        // Declaring class of a property from a bytecode token (UProperty.Outer; for
        // a property from another package this is an import stub — same name).
        static string PropDeclClass(UObject prop) => UEObject.PropDeclClassName(prop);

        // Resolved property access for token `tok` that EvalNext/EvalLValue just
        // took at index frame.Cursor-1. The cache lives on the function's token
        // stream (shared by all its frames).
        static readonly Dictionary<List<Tok.Token>, UEPropRef[]> _propRefs =
            new(UERefComparer<List<Tok.Token>>.Instance);
        internal static UEPropRef PropRefAt(UEFrame frame, Tok.Token tok, UObject prop)
        {
            int idx = frame.Cursor - 1;
            var arr = frame.PropRefs;
            if (arr is null)
            {
                var toks = frame.Tokens;
                lock (_propRefs)
                {
                    if (!_propRefs.TryGetValue(toks, out arr))
                        _propRefs[toks] = arr = new UEPropRef[toks.Count];
                }
                frame.PropRefs = arr;
            }
            if ((uint)idx < (uint)arr.Length)
            {
                var r = System.Threading.Volatile.Read(ref arr[idx]);
                if (r is not null && ReferenceEquals(r.Tok, tok)) return r;
                r = new UEPropRef(tok, prop);
                if (ReferenceEquals(frame.Tokens[idx], tok)) System.Threading.Volatile.Write(ref arr[idx], r);
                return r;
            }
            return new UEPropRef(tok, prop);
        }

        // UObject::IsProbing (Core.dll.c:40251). True if the name is OUTSIDE the
        // probe range (the 0xc4/0x105 comparison precedes any mask work) OR the
        // object has no state frame; otherwise the frame's mask bit.
        //
        // This is where UnrealScript `ignores` lives: a dormant Inventory has no
        // Touch stub function — only a cleared bit in the state's IgnoreMask,
        // and the original does not deliver Touch to it at all.
        public static bool IsProbing(UEObject obj, string eventName)
        {
            if (!UEProbe.TryBit(eventName, out int bit)) return true;
            UEFrame sf = obj?.StateFrame;
            if (sf is null) return true;
            return (sf.ProbeMask & (1UL << bit)) != 0;
        }

        // UObject::IsA — true if cls or any ancestor matches targetName
        // (case-insensitive). Name-based IsA — UObject::execIsA (native 303,
        // Core.dll.c:44651): FName comparison along the SuperField chain. DynamicCast/MetaCast go by
        // class identity (IsChildOfClass). Owned by the Core registry.
        public static bool ClassIsA(UClass cls, string targetName)
            => UEClassRegistry.Shared.ClassIsA(cls, targetName);

        // A state's SuperField (UState::Link, Core.dll.c:45270) in canonical
        // form. Super may be an import stub — a class state from ANOTHER package
        // without Children (Harry.PlayerWalking -> Engine
        // PlayerPawn.PlayerWalking), so the same-named state declared in the
        // canonical owner class is taken.
        UState CanonicalSuperState(UState st)
        {
            if (st?.Super is not UState sup || sup is UClass) return null;
            string owner = (sup.Outer as UClass)?.Name ?? sup.Outer?.Name?.ToString();
            UClass canon = owner is null ? null : UEClassRegistry.Shared.Find(owner);
            if (canon != null)
            {
                try { canon.BeginDeserializing(); } catch { }
                string supName = sup.Name?.ToString();
                foreach (var s in canon.EnumerateFields<UState>())
                    if (s is not UClass && string.Equals(s.Name?.ToString(), supName,
                                                         StringComparison.OrdinalIgnoreCase))
                        return s;
            }
            return sup;
        }

        // Walk the class hierarchy for a state by name.
        public UState FindState(UClass cls, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            for (var c = cls; c != null; c = CanonicalSuper(c))
                foreach (var st in c.EnumerateFields<UState>())
                {
                    if (st is UClass) continue;
                    if (string.Equals(st.Name?.ToString(), name, StringComparison.OrdinalIgnoreCase))
                        return st;
                }
            return null;
        }

        // Find the class's auto-state (StateFlag.Auto, bit 0x2 in the UE1
        // state flags bitfield).
        public UState FindAutoState(UClass cls)
        {
            for (var c = cls; c != null; c = CanonicalSuper(c))
                foreach (var st in c.EnumerateFields<UState>())
                {
                    if (st is UClass) continue;
                    if (((uint)st.StateFlags & 0x2) != 0) return st;
                }
            return null;
        }

        // UObject::FindObjectField (Core.dll.c:64356): the state node's VfHash
        // (unless Global), then the class VfHash — state-first priority, then
        // the class hierarchy. Used by Bump/Touch/Tick/PostBeginPlay dispatch.
        //
        // MEMOIZATION: resolution is a pure function of (class, state name, event
        // name); the walk on a miss is unchanged byte for byte. Without the cache
        // the walk ran on EVERY dispatch (85% of tick cost on Tut1b, HP1_TICK_PROF).
        // The class registry is filled only at bootstrap (all GetOrLoadPackage
        // calls are startup calls), so the cache never goes stale.
        // The cache key compares strings case-insensitively WITHOUT
        // ToLowerInvariant, so no strings are allocated per call.
        readonly struct EventFnKey : IEquatable<EventFnKey>
        {
            public readonly UClass Cls; public readonly string State, Name;
            public EventFnKey(UClass cls, string state, string name) { Cls = cls; State = state ?? ""; Name = name; }
            public bool Equals(EventFnKey o) => ReferenceEquals(Cls, o.Cls)
                && string.Equals(State, o.State, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Name, o.Name, StringComparison.OrdinalIgnoreCase);
            public override bool Equals(object o) => o is EventFnKey k && Equals(k);
            public override int GetHashCode() => HashCode.Combine(
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Cls),
                StringComparer.OrdinalIgnoreCase.GetHashCode(State),
                StringComparer.OrdinalIgnoreCase.GetHashCode(Name));
        }
        readonly Dictionary<EventFnKey, UFunction> _eventFnCache = new();

        public UFunction FindEventFunction(UEObject obj, string name)
        {
            if (obj?.Class == null || string.IsNullOrEmpty(name)) return null;

            string stateName = obj.CurrentStateName;
            var key = new EventFnKey(obj.Class, stateName, name);
            if (_eventFnCache.TryGetValue(key, out UFunction cached)) return cached;
            UFunction result = null;

            // Shipped UObject::FindObjectField (Core.dll.c:64356) searches the
            // state node's VfHash, and UState::Link (45270) builds it as a COPY of
            // the SuperField (+0x28) hash plus its own functions. A state's
            // SuperField is both an explicit `extends` (QuidPlayer.BEATER extends
            // Fly) and the same-named parent-class state (Harry.PlayerWalking ->
            // PlayerPawn.PlayerWalking). So the walk follows the state Super chain,
            // not only same-named class states: otherwise inherited
            // Fly.BeginState was not found for BEATER/Chaser.
            if (!string.IsNullOrEmpty(stateName))
            {
                for (UState st = FindState(obj.Class, stateName); st != null && result is null;
                     st = CanonicalSuperState(st))
                    foreach (var fn in st.EnumerateFields<UFunction>())
                        if (string.Equals(fn.Name?.ToString(), name,
                                          StringComparison.OrdinalIgnoreCase))
                        { result = fn; break; }
            }

            if (result is null)
                for (var c = obj.Class; c != null && result is null; c = CanonicalSuper(c))
                    foreach (var fn in c.EnumerateFields<UFunction>())
                        if (string.Equals(fn.Name?.ToString(), name,
                                          StringComparison.OrdinalIgnoreCase))
                        { result = fn; break; }
            _eventFnCache[key] = result;
            return result;
        }

        // FindObjectField(name, Global=1) (Core.dll.c:64356): only the class
        // VfHash (`global.Foo()`). Like FindEventFunction but SKIPS state functions:
        // searches only class-level member functions down the hierarchy, so a
        // state's Tick override can call `global.Tick()` to run the base
        // (non-state) implementation.
        // Memoised like FindEventFunction (same key type, empty state: the class hierarchy only): a pure
        // function of (class, name), the walk on a miss is unchanged. The walk (EnumerateFields type tests + a
        // string compare per field, every class up the chain) ran on every `global.Foo()` call.
        public UFunction FindGlobalFunction(UEObject obj, string name)
        {
            if (obj?.Class == null || string.IsNullOrEmpty(name)) return null;
            var key = new EventFnKey(obj.Class, null, name);
            if (_globalFnCache.TryGetValue(key, out UFunction cached)) return cached;
            UFunction result = null;
            for (var c = obj.Class; c != null && result is null; c = CanonicalSuper(c))
                foreach (var fn in c.EnumerateFields<UFunction>())
                    if (string.Equals(fn.Name?.ToString(), name,
                                      StringComparison.OrdinalIgnoreCase))
                    { result = fn; break; }
            _globalFnCache[key] = result;
            return result;
        }
        readonly Dictionary<EventFnKey, UFunction> _globalFnCache = new();

        // `default.X` lookup — the default value of property X for a class.
        // Cached per class via a throwaway defaults object (LoadDefaults applies
        // the class's defaultproperties + static-array zero-init exactly like a
        // real instance's init). Used by DefaultVariableToken.
        readonly Dictionary<UClass, UEObject> _classDefaults = new(UERefComparer<UClass>.Instance);
        UEObject ClassDefaultsBag(UClass cls)
        {
            if (!_classDefaults.TryGetValue(cls, out var d))
            {
                d = new UEObject { Class = cls, Interp = this };
                d.LoadDefaults();
                _classDefaults[cls] = d;
            }
            return d;
        }
        // Tail of UObject::SaveConfig (Core.dll.c:63720-63760): for each loaded
        // class with IsChildOf(BaseClass) — LoadConfig(Defaults, 1).
        // The port's default objects are the `default.X` cache; those already
        // built are re-read (not-yet-built ones read the ini when built).
        public void ReloadConfigOfClassDefaults(UClass baseClass, UEConfig config)
        {
            foreach (var kv in _classDefaults)
                if (UEClassRegistry.Shared.IsChildOfClass(kv.Key, baseClass))
                    UEObjectConfig.LoadConfig(config, kv.Value, kv.Key, kv.Key, propagate: true, isInstance: false);
        }

        // Class default WITHOUT building the bag: a built bag holds the live
        // defaults (script may have written default.X; in shipped this is the same
        // class Defaults array), otherwise static parsing of class data.
        public object ClassDefaultVectorNoAlloc(UClass cls, string name)
        {
            if (cls == null || string.IsNullOrEmpty(name)) return null;
            return _classDefaults.TryGetValue(cls, out var d) ? d.GetProp(name) : UEObject.ClassDefaultVector(cls, name);
        }

        public object GetClassDefault(UClass cls, string name)
        {
            if (cls == null || string.IsNullOrEmpty(name)) return null;
            return ClassDefaultsBag(cls).GetProp(name);
        }

        // UE1 EX_Context on a CLASS value (UnScript.cpp EX_Context works on any
        // UObject, and a UClass's property block IS its instance-default block
        // plus the UObject intrinsics). Reached by Engine Pawn.TakeDamage —
        // `InstigatedBy.IsA(Class.Name) || self.IsA(InstigatedBy.Class.Name)`
        // (decompiled bytecode; was a loud "non-UEObject value (UClass)" warn
        // with Context=null, review #5 P0). Model: the cached class-defaults
        // bag, seeded once with the intrinsic Name (the class object's own
        // FName — reads as the class name).
        public UEObject ClassContextObject(UClass cls)
        {
            if (cls == null) return null;
            var d = ClassDefaultsBag(cls);
            if (!d.Props.ContainsKey("Name")) d.Props["Name"] = new UEName(cls.Name);
            return d;
        }


        // ── UObject::execObjectToString (Core.dll.c:59379, body 0x10135470) ──
        //
        // None -> "None" (0x101b29a8); otherwise UObject::GetPathName(NULL)
        // (disasm 0x101596c0..0x1015975c): if Outer (+0x18) is not NULL —
        // Outer->GetPathName, then "." (0x101bf708), then GetName() (FName
        // +0x20). I.e. the full path along the Outer chain, with no special
        // status for the root package: a package has Outer = NULL and its name is
        // the first segment.
        //
        // Outer is a VM object (Props["Outer"], `new(Outer)`) or a package
        // (UEObject.OuterPath: the map package for an actor, "Transient" for `new`
        // without Outer). An object whose outer is NOT MODELLED (engine objects:
        // viewport, console, canvas — created by C++ with outers the port lacks)
        // gets no invented path: a loud warning and a string tagged STOP (§8)
        // that makes the divergence visible in the log.
        public static string ObjectPathName(object v)
        {
            switch (v)
            {
                case null: return "None";
                case UEObject o:
                {
                    object n = o.GetProp(PK.Name);
                    string name = n?.ToString();
                    string outer = o.GetProp(PK.Outer) is UEObject ou && !ReferenceEquals(ou, o)
                        ? ObjectPathName(ou) : o.OuterPath;
                    if (string.IsNullOrEmpty(name) || name == "None" || outer is null)
                    {
                        HP1.Core.UETrace.Warn($"ObjectToString: object path {o.Class?.Name}:{name ?? "?"} "
                            + "not modelled (no name or outer) — STOP, §8");
                        return $"<STOP:path:{o.Class?.Name}:{name ?? "?"}>";
                    }
                    return outer + "." + name;
                }
                case UELib.Core.UObject uo: return HP1.UEScript.Render.ShippedLayout.FullPath(uo);
                case UEResourceRef rr:
                    if (rr.ObjectPath is null || rr.ObjectPath.StartsWith("export:", StringComparison.Ordinal))
                    {
                        HP1.Core.UETrace.Warn($"ObjectToString: resource {rr} has no full path — STOP, §8");
                        return $"<STOP:path:{rr.Class}:{rr.Name}>";
                    }
                    return rr.ObjectPath;
                default: return v.ToString();
            }
        }

        // UE1 `new` operator (NewToken). Constructs a non-actor UObject of the
        // given class. Unlike SpawnActor this creates NO Unity GameObject and runs
        // NO actor spawn lifecycle — UWindow windows extend Object (not Actor), are
        // plain VM objects, and the framework calls BeginPlay()/Created() on them
        // itself (UWindowWindow.CreateWindow). Just instantiate + apply class
        // defaults, like the bare construction the HUD/cutscene drivers already do.
        public UEObject NewObject(UClass cls, UEObject outer, string objName)
        {
            if (cls == null) return null;
            // Cross-package declarations bind to import stubs. The original
            // linker resolves those to the real same-FName export even when the
            // real class is data-only and declares no functions.
            if (UEClassRegistry.IsImportStub(cls))
            {
                var real = UEClassRegistry.Shared.FindForStub(cls);
                if (real != null && !UEClassRegistry.IsImportStub(real)) cls = real;
            }
            // UObject::StaticAllocateObject (Core.dll.c:57713-57720): a class with
            // CLASS_Abstract (UClass+0x488 & 1) — LocalizeError("Abstract") to the
            // log and NULL, no object is created; StaticConstructObject on NULL
            // does not call the constructor and returns NULL.
            if ((UEObjectConfig.ClassFlags(cls) & 0x1u) != 0)
            {
                UETrace.Warn($"[VM] new: class {cls.Name} is abstract — None (StaticAllocateObject)");
                return null;
            }
            var obj = new UEObject { Class = cls, Interp = this };
            obj.LoadDefaults();
            if (outer != null) obj.Props["Outer"] = outer;
            // execNew (Core.dll.c:25126-25131): Outer == None -> package Transient.
            else obj.OuterPath = "Transient";
            if (!string.IsNullOrEmpty(objName) && objName != "None")
                obj.Props["Name"] = new UEName(objName);
            // Name None -> StaticConstructObject -> MakeUniqueObjectName(Outer,
            // Class) (Core.dll.c:41313): class-name base + ClassUnique++,
            // while StaticFindObject(NULL, Outer, name) finds an object in THIS
            // outer (Transient or a VM outer object).
            else
                obj.Props["Name"] = new UEName(UELevelRuntime.MintObjectName(cls.Name, () =>
                {
                    int number = ClassUnique.TryGetValue(cls, out int old) ? old : 0;
                    ClassUnique[cls] = number + 1;
                    return number;
                }, outer == null ? TransientObjectNames : _namesInOuter.GetValue(outer, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase))));
            if (obj.GetProp(PK.Name) is UEName minted)
                (outer == null ? TransientObjectNames
                    : _namesInOuter.GetValue(outer, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase))).Add(minted.ToString());
            // StaticConstructObject (Core.dll.c:57880-57883): an instance of a class
            // with CLASS_PerObjectConfig immediately reads ITS OWN section (object
            // name) from the Outer's package file, then the instance's LoadLocalized.
            if ((UEObjectConfig.ClassFlags(cls) & UEObjectConfig.CLASS_PerObjectConfig) != 0)
            {
                if (UEConfig.Global is { } gconfig)
                    UEObjectConfig.LoadConfig(gconfig, obj, cls, cls, propagate: false, isInstance: true);
                UEObjectConfig.LoadLocalized(obj, cls, propagate: false, isInstance: true,
                    outerName: outer?.GetProp(PK.Name)?.ToString(), objName: objName);
            }
            return obj;
        }
    }
}
