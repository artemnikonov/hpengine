// States and labels: RestoreStateFrame, EnterState/GotoStateCore, GotoLabel and label maps, the
// state super chain, probing, and event/global function lookup. Fields live in UEInterpreter.cs.
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
    public partial class UEInterpreter
    {
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
    }
}
