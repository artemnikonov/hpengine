// Engine actor-lifecycle helpers shared by every frontend (headless, Unity):
// the ONE owner of the touch-list semantics and of engine event dispatch.
// Ported from the shipped HP1 Engine.dll decompile — NOT from SurrealEngine:
// the shipped BeginTouch/TouchTo/EndTouch family (Engine.dll.c FUN_1037a038 /
// FUN_1037a0a0 / FUN_1037a3e0, guard names AActor::BeginTouch / TouchTo /
// AActor::EndTouch) is asymmetric and eviction-capable, unlike SE's
// symmetric UActor::Touch/UnTouch (§7: shipped wins).
using System;
using UELib.Core;

namespace HP1.UEScript
{
    public static class UELifecycle
    {
        // Test-only observation hook: fires on EVERY engine event dispatch,
        // before the body lookup (so it sees events whose class has no body).
        // Production code never assigns it; the headless selftest uses it to
        // pin event ORDER and to stand in for script bodies (a script Touch
        // may Destroy an actor — the fixture does the same from the probe).
        public static Action<UEObject, string, object[]> EventProbe;
        // SwARc Engine measurement tap (tools/swarc_oracle/ENGINE_MEASURE.md): the dispatch of an event has ended
        // (its handler, if any, returned). Observes only; null in production.
        public static Action<UEObject, string> EventDone;
        readonly struct EventEnd : IDisposable
        {
            readonly UEObject _a; readonly string _n;
            public EventEnd(UEObject a, string n) { _a = a; _n = n; }
            public void Dispose() => EventDone?.Invoke(_a, _n);
        }
        static EventEnd EventScope(UEObject actor, string name, object[] args)
        {
            EventProbe?.Invoke(actor, name, args);
            return new EventEnd(actor, name);
        }

        static readonly object[] NoArgs = Array.Empty<object>();

        // ROUTE OBSERVATION: ONE POINT, BECAUSE THERE IS ONE DISPATCHER.
        //
        // All engine events go through CallEvent, so Touch/UnTouch/Bump/HitWall/PostTouch are
        // caught here rather than by six hooks across the code: a hook "next to the call" would
        // silently miss a new dispatch path. The list is closed: the route log answers "how far
        // did the run get", not "what happened in the frame".
        static readonly string[] Watched =
            { "Touch", "UnTouch", "Bump", "HitWall", "PostTouch", "EncroachedBy" };

        static bool IsPlayer(UEObject a) =>
            a is not null && UEInterpreter.ToBool(a.GetProp(PK.bIsPlayer));

        static void NoteEvent(UEObject actor, string name, object[] args)
        {
            if (!HP1.Core.UEPlaythroughLog.Enabled) return;
            bool watched = false;
            for (int i = 0; i < Watched.Length; i++)
                if (string.Equals(Watched[i], name, StringComparison.OrdinalIgnoreCase))
                { watched = true; break; }
            if (!watched) return;
            // THE LOG FOLLOWS THE PLAYER; THIS IS A NAMED BOUNDARY.
            //
            // Over 600 ticks: 266 event lines, most of them NPCs touching an ambient actor
            // (28 Touch/UnTouch of one SpecialEvent3 by passers-by). They do not answer "how far
            // did the PLAYER get" and drown the log.
            //
            // The criterion is the pawn's own `bIsPlayer`, not class or name, as shipped decides
            // (`Pawn+0x25c` bit 1). Filtered events are COUNTED and printed in the summary:
            // silently dropped ones would read as "did not happen".
            UEObject arg0 = args is { Length: > 0 } ? args[0] as UEObject : null;
            if (!IsPlayer(actor) && !IsPlayer(arg0))
            {
                HP1.Core.UEPlaythroughLog.CountFiltered();
                return;
            }
            string self = actor?.GetProp(PK.Name)?.ToString() ?? "?";
            string cls = actor?.Class?.Name?.ToString() ?? "?";
            string other = arg0 is not null
                ? (arg0.GetProp(PK.Name)?.ToString() ?? arg0.Class?.Name?.ToString() ?? "?")
                : "-";
            HP1.Core.UEPlaythroughLog.Note("event", $"{name}:{self}:{other}",
                $"{name} actor={self}({cls}) other={other}");
            HP1.Core.UEPlaythroughLog.MarkIfCheckpoint(self, name);
        }

        // Frontend error policy for the event BODY only — the bookkeeping around
        // it (touch slots, base links) must still run, so this wraps just the
        // Invoke. UELevelRuntime.Guard's setter keeps this in step, so a Unity
        // log-and-continue policy also covers the events dispatched from inside
        // these helpers (Touch/UnTouch/EncroachedBy), not only the ones the tick
        // skeleton guards directly. Default runs the body straight through,
        // which is the headless "an exception is a loud stop" contract.
        public static Action<Action, string> Guard = (body, what) => body();

        // GATE of AActor::ProcessEvent (Engine 0x10379a60): `test byte [Level+0x438], 2; je ret`:
        // an actor event is delivered only when Level.bBegunPlay, then UObject::ProcessEvent.
        // LoadMap spawns GameInfo BEFORE bBegunPlay (247382), so in shipped its spawn events are
        // lost, and it gets Pre/Post/BeginPlay and SetInitialState from the common pass after
        // InitGame (wave B3). An actor without Level (synthetic fixtures) is not gated.
        public static bool ActorEventsBlocked(UEObject obj)
            => obj?.GetProp(PK.Level) is UEObject level
               && UEInterpreter.ClassIsA(obj.Class, "Actor")
               && !UEInterpreter.ToBool(level.GetProp(PK.bBegunPlay));

        // GATE of UObject::ProcessEvent (Core.dll.c:60785). In the original every event dispatch
        // passes these checks, and they live here:
        //   1. IsProbing(this, Function->Name): the probe mask of the state frame
        //      (UnrealScript `ignores` lands here too, see UEProbeMask);
        //   2. !IsPendingKill() (vtable +0x2c; for an actor it is bDeleteMe, which
        //      DestroyActor sets AFTER its own events, 253087).
        // Script Disable() clears a bit of the same mask (item 1); there is no second store (D-20).
        // No body: silent no-op, as in the original.
        //
        // Check order as in shipped: probe mask BEFORE function lookup.
        public static void CallEvent(UEInterpreter interp, UEObject actor, string name)
        {
            if (ActorEventsBlocked(actor)) return;   // AActor::ProcessEvent
            using var _ev = EventScope(actor, name, NoArgs);
            NoteEvent(actor, name, NoArgs);
            if (!UEInterpreter.IsProbing(actor, name)) return;
            if (actor.Destroyed) return;          // !IsPendingKill() (vtable +0x2c, bDeleteMe)
            UFunction fn = interp.FindEventFunction(actor, name);
            if (fn is not null) Guard(() => interp.Invoke(actor, fn), name);
        }

        public static void CallEvent(UEInterpreter interp, UEObject actor, string name, object arg)
        {
            if (ActorEventsBlocked(actor)) return;   // AActor::ProcessEvent
            using var _ev = EventScope(actor, name, new[] { arg });
            NoteEvent(actor, name, new[] { arg });
            if (!UEInterpreter.IsProbing(actor, name)) return;
            if (actor.Destroyed) return;          // !IsPendingKill() (vtable +0x2c, bDeleteMe)
            UFunction fn = interp.FindEventFunction(actor, name);
            if (fn is not null) Guard(() => interp.Invoke(actor, fn, arg), name);
        }

        public static void CallEvent(UEInterpreter interp, UEObject actor, string name, object a, object b)
        {
            if (ActorEventsBlocked(actor)) return;   // AActor::ProcessEvent
            using var _ev = EventScope(actor, name, new[] { a, b });
            NoteEvent(actor, name, new[] { a, b });
            if (!UEInterpreter.IsProbing(actor, name)) return;
            if (actor.Destroyed) return;          // !IsPendingKill() (vtable +0x2c, bDeleteMe)
            UFunction fn = interp.FindEventFunction(actor, name);
            if (fn is not null) Guard(() => interp.Invoke(actor, fn, a, b), name);
        }

        // Same gate (AActor::ProcessEvent + UObject::ProcessEvent) for an event whose RETURN value the
        // engine reads (eventEncroachingOn 0x1031af00: ReturnValue is zeroed before the call and read
        // after; for a gated-off event that is 0, here null). No Guard: the caller keeps its own error policy.
        public static object CallEventResult(UEInterpreter interp, UEObject actor, string name, params object[] args)
        {
            if (ActorEventsBlocked(actor)) return null;   // AActor::ProcessEvent
            using var _ev = EventScope(actor, name, args);
            NoteEvent(actor, name, args);
            if (!UEInterpreter.IsProbing(actor, name)) return null;
            if (actor.Destroyed) return null;          // !IsPendingKill() (vtable +0x2c, bDeleteMe)
            UFunction fn = interp.FindEventFunction(actor, name);
            return fn is null ? null : interp.Invoke(actor, fn, args);
        }

        // Same ProcessEvent gate for an event with an ARBITRARY number of arguments
        // (ClientHearSound carries five: AActor::CheckHearSound, disasm
        // 0x1040acb6-0x1040ad13). A separate name, not a params overload of
        // CallEvent: an array passed to the single-`object` overload would go as ONE
        // argument.
        public static void CallEventArgs(UEInterpreter interp, UEObject actor, string name, object[] args)
        {
            if (ActorEventsBlocked(actor)) return;   // AActor::ProcessEvent
            using var _ev = EventScope(actor, name, args);
            NoteEvent(actor, name, args);
            if (!UEInterpreter.IsProbing(actor, name)) return;
            if (actor.Destroyed) return;          // !IsPendingKill() (vtable +0x2c, bDeleteMe)
            UFunction fn = interp.FindEventFunction(actor, name);
            if (fn is not null) Guard(() => interp.Invoke(actor, fn, args), name);
        }

        // AActor::BeginTouch (Engine.dll.c:225598): TouchTo is SEQUENTIAL and
        // asymmetric — the second side is linked only when the first side
        // reports success (its Touch event did not unlink/destroy the pair).
        public static void BeginTouch(UEInterpreter interp, UEObject actor, UEObject other)
        {
            if (TouchTo(interp, actor, other))
                TouchTo(interp, other, actor);
        }

        // Shipped TouchTo (Engine.dll.c FUN_1037a0a0). Returns false ONLY when
        // the Touch event dispatched here changed the slot (unlinked or
        // destroyed the toucher) — every other outcome, including "array full,
        // could not evict", reports true so BeginTouch still links the other
        // side (the shipped asymmetry).
        public static bool TouchTo(UEInterpreter interp, UEObject actor, UEObject other)
        {
            // Shipped appFailAsserts (UnActor.cpp:0x473-0x475).
            if (actor is null) throw new InvalidOperationException("TouchTo: Actor is null");
            if (other is null) throw new InvalidOperationException("TouchTo: Other is null");
            if (ReferenceEquals(actor, other)) throw new InvalidOperationException("TouchTo: Actor == Other");

            UEObject[] touching = actor.TouchList();
            int slot = -1;
            for (int i = 0; i < UEObject.TouchingArraySize; i++)
            {
                if (touching[i] is null) slot = i;                         // LAST free slot wins (225664-225668)
                else if (ReferenceEquals(touching[i], other)) return true; // already touching (225670-225673)
            }
            if (slot == -1)
            {
                // Eviction pass A (225678-225688): drop entries whose Physics
                // is PHYS_None; the LAST evicted slot wins (no break).
                //
                // Shipped assert 0x48a is UObject::IsValid: ONLY validity in the GObjObjects
                // registry (Core.dll.c:23541), NOT bDeleteMe. A corpse (bDeleteMe, FirstDeleted
                // chain) passes it and stays in Touching. This is PROVEN shipped behaviour, not a
                // port leak: a projectile dying in its OWN Touch of the first half of the pair
                // (baseSpell.ProcessTouch -> Explode -> Destroy) keeps its one-sided reference,
                // since DestroyActor's unlink scans only the holder side (Engine.dll.c:253050),
                // and the second half of BeginTouch links the corpse; it is evicted only by this
                // pass when Physics==None. Live example: spellFlip x4 in Touching of spellTrigger9
                // (Lev_Tut1b). IsValid is not "alive" (V1); in the C# model a registry slot
                // reference is always valid, so there is nothing to assert.
                for (int i = 0; i < UEObject.TouchingArraySize; i++)
                {
                    if (UEInterpreter.ToInt(touching[i].GetProp(PK.Physics)) == 0)
                    {
                        EndTouch(interp, actor, touching[i], false);
                        slot = i;
                    }
                }
                if (slot == -1 && UEInterpreter.ClassIsA(other.Class, "Pawn"))
                {
                    // Pass B (225697-225708): an incoming Pawn evicts the FIRST
                    // non-Pawn entry.
                    for (int i = 0; i < UEObject.TouchingArraySize; i++)
                    {
                        if (!UEInterpreter.ClassIsA(touching[i].Class, "Pawn"))
                        {
                            EndTouch(interp, actor, touching[i], false);
                            slot = i;
                            break;
                        }
                    }
                    if (slot == -1)
                    {
                        // Pass C (225712-225730): a PLAYER Pawn (Pawn+0x25c
                        // bit 1 = bIsPlayer by Pawn.uc bool order) evicts the
                        // first non-player entry; a non-player gives up — and
                        // still returns true.
                        if (!UEInterpreter.ToBool(other.GetProp(PK.bIsPlayer))) return true;
                        for (int i = 0; i < UEObject.TouchingArraySize; i++)
                        {
                            if (!UEInterpreter.ClassIsA(touching[i].Class, "Pawn")
                                || !UEInterpreter.ToBool(touching[i].GetProp(PK.bIsPlayer)))
                            {
                                EndTouch(interp, actor, touching[i], false);
                                slot = i;
                                break;
                            }
                        }
                    }
                }
            }
            if (slot >= 0)
            {
                // Link BEFORE the event; the event may unlink it (225734-225742).
                touching[slot] = other;
                CallEvent(interp, actor, "Touch", other);
                if (!ReferenceEquals(touching[slot], other)) return false;
            }
            return true;
        }

        // Shipped AActor::EndTouch (Engine.dll.c FUN_1037a3e0). Asymmetric:
        // noNotifySelf suppresses UnTouch on THIS side only (DestroyActor
        // passes 1 for the dying actor — the surviving side is always
        // notified, Engine.dll.c:253052).
        public static void EndTouch(UEInterpreter interp, UEObject actor, UEObject other, bool noNotifySelf)
        {
            if (ReferenceEquals(other, actor))
                throw new InvalidOperationException("EndTouch: Other == this"); // 0x4d7 assert
            UEObject[] mine = actor.TouchList();
            for (int i = 0; i < UEObject.TouchingArraySize; i++)
            {
                if (ReferenceEquals(mine[i], other))
                {
                    mine[i] = null;
                    if (!noNotifySelf) CallEvent(interp, actor, "UnTouch", other);
                    break;
                }
            }
            UEObject[] theirs = other.TouchList();
            for (int i = 0; i < UEObject.TouchingArraySize; i++)
            {
                if (ReferenceEquals(theirs[i], actor))
                {
                    theirs[i] = null;
                    CallEvent(interp, other, "UnTouch", actor);
                    break;
                }
            }
        }
    }
}
