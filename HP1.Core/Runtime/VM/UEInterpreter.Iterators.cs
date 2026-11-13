// The native actor iterators behind ForEach (AllActors, VisibleCollidingActors, RadiusActors,
// VisibleActors, TraceActors).
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
            interp.WriteLValue(frame, LoopVar, null);   // SurrealEngine: *ReturnValue = nullptr
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
    // does NOT snapshot its matches the way SurrealEngine's RadiusActorsIterator does: it
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
}
