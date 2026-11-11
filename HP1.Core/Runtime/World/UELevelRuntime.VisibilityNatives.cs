// Collision/visibility iterator natives whose candidate world is Core-owned.
//
// Source priority matters here.  SurrealEngine's VisibleCollidingActorsIterator
// uses its own overlap system, but shipped HP1 calls
// FCollisionHash::ActorRadiusCheck and then applies the class/hidden/BSP tests
// in execVisibleCollidingActors.  The bodies below follow the shipped order:
//
//   ActorRadiusCheck (Engine.dll 0x10365500)
//     - query collision-hash cells for Location ± Radius;
//     - first-visit dedup in hash walk order;
//     - strict center-distance test: DistSquared < Radius*Radius;
//     - prepend each FCheckResult, reversing the qualifying hash order.
//
//   execVisibleCollidingActors (Engine.dll.c:19859-19984)
//     - IsA(BaseClass);
//     - bIgnoreHidden || !bHidden;
//     - UModel::FastLineCheck(candidate.Location, query Location);
//
// This is gameplay state/query logic, not presentation.  Unity and headless
// register this same method; neither frontend is allowed to substitute its
// collider set or transform positions.
using System;
using System.Collections.Generic;
using UELib.Core;
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        // AActor::execFastTrace (Engine.dll.c:52683-52716) calls the level
        // UModel::FastLineCheck directly.  It is a pure BSP query: actors and
        // movers are not candidates.  TraceStart defaults to Self.Location.
        public object FastTraceNative(
            UEInterpreter interp, UEFrame frame, object[] args)
        {
            UEObject self = frame.Context ?? frame.Object;
            if (self is null) return false;
            if (Bsp is null)
                throw new InvalidOperationException(
                    "FastTrace reached before BSP world load");
            Vector3 end = args.Length > 0
                ? UEInterpreter.AsVector3(args[0])
                : Vector3.zero;
            Vector3 start = args.Length > 1 && args[1] is not null
                ? UEInterpreter.AsVector3(args[1])
                : UEInterpreter.AsVector3(self.GetProp(PK.Location));
            return Bsp.FastLineCheckClear(end, start);
        }

        // AActor::execRadiusActors (Engine.dll.c:20960-21089) walks the live
        // Level.Actors array in index order.  Its radius test is against the
        // actor's collision cylinder envelope:
        //   DistSquared < (Radius + Actor.CollisionRadius)^2
        // The comparison is strict.  Unlike VisibleCollidingActors this query
        // neither consults the collision hash nor filters hidden/occluded actors.
        // The scan itself is lazy — see UERadiusActorsIterator.
        public object RadiusActorsNative(
            UEInterpreter interp, UEFrame frame, object[] args)
        {
            UEObject self = frame.Context ?? frame.Object;
            if (self is null) return null;
            UClass baseClass = ClassArg(args, 0);
            float radius = args.Length > 2 && args[2] is not null
                ? UEInterpreter.AsFloat(args[2])
                : 0f;
            Vector3 location = args.Length > 3 && args[3] is not null
                ? UEInterpreter.AsVector3(args[3])
                : UEInterpreter.AsVector3(self.GetProp(PK.Location));

            interp.PendingIterator =
                new UERadiusActorsIterator(Actors, baseClass, radius, location);
            return null;
        }

        // AActor::execVisibleActors (Engine.dll.c:75415-75530). Unlike
        // VisibleCollidingActors this never consults the collision hash: it is
        // the Level.Actors walk plus !bHidden, an optional radius and a pure BSP
        // line check. Radius is optional in the strong sense — the shipped body
        // performs NO distance test when it is zero (Engine.dll.c:75497), which
        // is not the same as a zero-radius query.
        public object VisibleActorsNative(
            UEInterpreter interp, UEFrame frame, object[] args)
        {
            UEObject self = frame.Context ?? frame.Object;
            if (self is null) return null;
            if (Bsp is null)
                throw new InvalidOperationException(
                    "VisibleActors reached before BSP world load");
            UClass baseClass = ClassArg(args, 0);
            float radius = args.Length > 2 && args[2] is not null
                ? UEInterpreter.AsFloat(args[2])
                : 0f;
            Vector3 location = args.Length > 3 && args[3] is not null
                ? UEInterpreter.AsVector3(args[3])
                : UEInterpreter.AsVector3(self.GetProp(PK.Location));

            interp.PendingIterator = new UEVisibleActorsIterator(
                Actors, baseClass, radius, location,
                (target, from) => Bsp.FastLineCheckClear(target, from));
            return null;
        }

        public object VisibleCollidingActorsNative(
            UEInterpreter interp, UEFrame frame, object[] args)
        {
            UEObject self = frame.Context ?? frame.Object;
            if (self is null) return null;
            if (CollisionHash is null)
                throw new InvalidOperationException(
                    "VisibleCollidingActors reached before InitCollisionHash");
            if (Bsp is null)
                throw new InvalidOperationException(
                    "VisibleCollidingActors reached before BSP world load");

            UClass baseClass = ClassArg(args, 0);
            // Shipped exec default is 1000.0f (0x447a0000), not the caller's
            // CollisionRadius used by SurrealEngine's later model.
            // 0x1040ea61-0x1040ea79: Radius == 0.0 (including an explicit 0 and
            // an omitted argument) → 1000.0f.
            float radius = args.Length > 2 && args[2] is not null
                ? UEInterpreter.AsFloat(args[2]) : 0f;
            if (radius == 0f) radius = 1000f;
            Vector3 location = args.Length > 3 && args[3] is not null
                ? UEInterpreter.AsVector3(args[3])
                : UEInterpreter.AsVector3(self.GetProp(PK.Location));
            bool ignoreHidden = args.Length > 4
                && args[4] is not null
                && UEInterpreter.ToBool(args[4]);

            Vector3 extent = new(radius, radius, radius);
            float radiusSquared = radius * radius;
            var snapshot = new List<UEObject>();
            foreach (int handle in CollisionHash.EncroachmentCandidates(
                         location - extent, location + extent))
            {
                UEObject actor = HashActor(handle);
                if (actor is null || actor.Destroyed) continue;
                Vector3 delta =
                    UEInterpreter.AsVector3(actor.GetProp(PK.Location)) - location;
                if (Vector3.Dot(delta, delta) >= radiusSquared) continue;
                snapshot.Add(actor);
            }
            // ActorRadiusCheck allocates each new FCheckResult at the list head.
            snapshot.Reverse();

            // The list is built ONCE (ActorRadiusCheck), and filtering happens on
            // EVERY step against the candidate's current state (0x1040eb05..0x1040eba5):
            // the IsA chain, 0x1040eb2d-0x1040eb39 skip only with bIgnoreHidden
            // AND bHidden, FastLineCheck (0x1040eb7f) from its CURRENT Location.
            // There is no bDeleteMe check: an actor destroyed by the loop body (chained
            // HurtRadius) is still returned.
            interp.PendingIterator = new UEVisibleCollidingIterator(snapshot, actor =>
                (baseClass is null || HP1.Core.UEClassRegistry.Shared.IsChildOfClass(actor.Class, baseClass))
                && !(ignoreHidden && UEInterpreter.ToBool(actor.GetProp(PK.bHidden)))
                && Bsp.FastLineCheckClear(
                    UEInterpreter.AsVector3(actor.GetProp(PK.Location)), location));
            return null;
        }

        // AllActors (304) — THE MOST BASIC ACTOR SEARCH, OWNED BY CORE (one world for
        // all frontends: the world's composition belongs to Core).
        //
        // It matters for the whole Tut1 start sequence: `Harry.PreBeginPlay` creates the
        // Narrator (Harry.uc:1195), and `CutScene.StartPlaying` looks it up via
        // `AllActors(baseNarrator)` (CutScene.uc:1214). Not finding it (`Cant find narrator`)
        // leaves `theNarrator` None: SAY/TALK lines are silent, their duration does not
        // hold the script, and the cutscene is "swallowed".
        //
        // Candidates are `Actors` IN CORE ORDER: that is shipped Level.Actors,
        // including runtime spawns (added by SpawnActor). Class/tag/destroyed
        // filtering is done by the iterator itself on the LIVE list — like shipped Next().
        public object AllActorsNative(
            UEInterpreter interp, UEFrame frame, object[] args)
        {
            UClass baseClass = ClassArg(args, 0);
            // execAllActors 0x1040d888 (test ebp,ebp; je: name index 0 = None skips the Tag compare) — an explicit
            // (or variable) 'None' DISABLES the filter, like an omitted argument;
            // filtering by the literal "None" would find nothing, including actors
            // with no serialized Tag at all.
            string tag = args.Length > 2 && args[2] is not null
                ? (args[2] is UEName n ? n.Value : args[2].ToString())
                : null;
            if (string.IsNullOrEmpty(tag)
                || tag.Equals("None", StringComparison.OrdinalIgnoreCase)) tag = null;

            // Live Actors: shipped AllActors (0x1040d780) rereads Actors.Num() on
            // every step (0x1040d847/0x1040d8a8), and an actor spawned by the loop
            // body (SpawnActor appends to the end) is returned ONCE.
            interp.PendingIterator = new UEAllActorsIterator(Actors, baseClass, tag);
            return null;
        }

        // ── Trace (277) and TraceActors (309): ONE WORLD FOR ALL FRONTENDS ──────
        //
        // The bodies below are the only owner (the authoritative actor set and the
        // collision hash belong to Core; the camera, BaseCam CheckPosition/CanSeeTarget,
        // must query the world the player walks in). Frontends only call them.
        //
        // AActor::execTrace (Engine.dll.c:156096). Query flags come from the
        // shipped code: the call goes to level vtable +0xc4 with
        // `(-(bTraceActors) & 0x11) + 6`, i.e. 6 (Level|Movers) for a world-only
        // trace and 0x17 (Level|Movers|Pawns|Others) for an actor trace.
        // That is exactly SingleLineCheck, not a second hand-written filter.
        //
        // An omitted optional bTraceActors comes from the tracer's OWN
        // bCollideActors bit (:156096 `pFStack_9c = *(this+0x1dc) & 1`
        // — the same bit execSetCollision :10663 reads as the default),
        // NOT a constant false: SE NActor.cpp:643 uses false, but by source
        // order shipped wins.
        //
        // The out parameter Location is COPIED from the result (:156122-156125), not
        // recomputed from the fraction: after mover/world dedup these are different floats.
        public UEObject TraceNative(UEInterpreter interp, UEFrame frame,
            object[] args, out LineCheckHit hit)
        {
            UEObject self = frame.Context ?? frame.Object;
            Vector3 end = UEInterpreter.AsVector3(args.Length > 2 ? args[2] : null);
            Vector3 start = args.Length > 3 && args[3] is not null
                ? UEInterpreter.AsVector3(args[3])
                : UEInterpreter.AsVector3(self?.GetProp(PK.Location));
            bool traceActors = args.Length > 4 && args[4] is not null
                ? UEInterpreter.ToBool(args[4])
                : UEInterpreter.ToBool(self?.GetProp(PK.bCollideActors));
            Vector3 extent = args.Length > 5 && args[5] is not null
                ? UEInterpreter.AsVector3(args[5]) : Vector3.zero;

            hit = SingleLineCheck(interp, self, self, start, end, extent,
                                  traceActors ? 0x17 : 6);
            bool blocked = hit.Blocked && hit.Actor is not null;
            // 0x1040c7bc-0x1040c894: Hit is zero-initialised, SingleLineCheck does not write
            // Location on a miss, out parameters are copied unconditionally —
            // a miss's HitLocation = (0,0,0), not the trace end.
            if (args.Length > 0) args[0] = blocked ? hit.Location : Vector3.zero;
            if (args.Length > 1) args[1] = blocked ? hit.Normal : Vector3.zero;
            return blocked ? hit.Actor : null;
        }

        // AActor::execTraceActors (Engine.dll.c:122076) reaches the level's
        // MultiLineCheck (+0xcc, :256043) and SIMPLY WALKS the resulting
        // list. So there is neither a second world-distance cut here
        // (MultiLineCheck has already shortened the actor ray) nor a re-sort: the list
        // is already in engine order, and re-sorting would destroy that very
        // hit order.
        //
        // WORLD HITS STAY IN THE LIST with Actor = LevelInfo (:256048
        // stores the passed LevelInfo). Game bytecode depends on it: BaseCam
        // CheckPosition :898/:919 and CanSeeTarget :521 test
        // HitActor.IsA('LevelInfo') — the ONLY thing that keeps the camera against a wall.
        // The SurrealEngine iterator drops world hits (with its own
        // "Is that correct?" there); by source order shipped wins.
        public object TraceActorsNative(UEInterpreter interp, UEFrame frame, object[] args)
        {
            UEObject self = frame.Context ?? frame.Object;
            // args[0] — BaseClass: shipped parses it but does not apply it in the output loop.
            Vector3 end = UEInterpreter.AsVector3(args.Length > 4 ? args[4] : null);
            Vector3 start = args.Length > 5 && args[5] is not null
                ? UEInterpreter.AsVector3(args[5])
                : UEInterpreter.AsVector3(self?.GetProp(PK.Location));
            Vector3 extent = args.Length > 6 && args[6] is not null
                ? UEInterpreter.AsVector3(args[6]) : Vector3.zero;

            var list = new List<UETraceActorsIterator.Hit>();
            // No zero-span guard: shipped has no compare between the last argument parse and the MultiLineCheck call
            // (0x1040e08b..0x1040e16a); a zero span goes through the same (ported) bodies.
            {
                UEObject levelInfo = Actors.Count > 0 ? Actors[0] : interp.GetLevelInfo();
                foreach (MoveHit h in MultiLineCheck(start, end, extent,
                                                     checkActors: true, levelInfo))
                {
                    // Shipped's output loop (0x1040e192..0x1040e226) writes Actor/HitLoc/
                    // HitNorm of each result as is: NEITHER IsA(BaseClass) NOR a Self
                    // exclusion (both filters came from SurrealEngine). Campaign scripts
                    // filter the camera themselves: BaseCam `!IsA('BaseCam')`.
                    // Shipped's hash holds no destroyed actors — the skip here is only a safeguard.
                    UEObject candidate = h.Actor;
                    if (candidate is null || candidate.Destroyed) continue;
                    // Location and Normal are COPIED from the result, not recomputed.
                    list.Add(new UETraceActorsIterator.Hit
                    {
                        Actor = candidate,
                        HitLoc = h.Location,
                        HitNorm = h.Normal,
                    });
                }
            }
            interp.PendingIterator = new UETraceActorsIterator(list);
            return null;
        }

        // The iterator's filter class is a UClass, not a name: shipped compares
        // pointers along the Class/SuperField chain (execAllActors 0x1040d780,
        // `pFVar5 == pUVar13`); HPModels.LevShadow and HProps.LevShadow are different
        // classes.
        private static UClass ClassArg(object[] args, int index)
        {
            if (args.Length <= index || args[index] is null) return null;
            if (args[index] is UClass cls) return cls;
            // A name instead of a class comes only from direct calls (fixtures):
            // resolved via the registry; bytecode always passes a UClass.
            string name = ClassNameArg(args, index);
            return string.IsNullOrEmpty(name) ? null : HP1.Core.UEClassRegistry.Shared.Find(name);
        }

        private static string ClassNameArg(object[] args, int index)
        {
            if (args.Length <= index || args[index] is null) return null;
            if (args[index] is UClass cls) return cls.Name?.ToString();
            if (args[index] is UObject obj) return obj.Name?.ToString();
            if (args[index] is UEName name) return name.Value;
            return args[index].ToString();
        }
    }
}
