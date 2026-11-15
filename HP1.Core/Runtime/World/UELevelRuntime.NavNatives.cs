// APawn::findPath (Engine.dll.c:99644-99749), native 553.
//
// World query, not presentation: it walks the level's own ReachSpecs table and
// the NavigationPoint Paths[16] indices into it. Core answers it itself, so the
// query always runs on the level's own table, never on a frontend copy.
//
// The shape is deliberately literal, because every part of it decides WHICH hop
// comes back first:
//   * a fixed 100-entry DFS stack, cursors initialised to -1;
//   * "already visited" is a LINEAR scan of a zero-terminated array, not a set;
//   * descent is gated on the candidate's class being the START point's class
//     (pointer equality — not IsA, not a name match). The shipped body also
//     calls appStricmp(class, "baseStation") and discards the result;
//   * an exhausted cursor toggles ONE flag shared by the whole walk (byte
//     [ebp+0xf], disasm 0x104025f3..0x10402626): flag clear -> set it and
//     restart the node; flag set -> clear it, pop, and reset the PARENT's
//     cursor as well. The flag is not per node and is not reset on descent,
//     so a child entered while it is set is popped at its FIRST -1 — "every
//     node is scanned twice" is NOT what the machine code does;
//   * on a name match the intermediate hop wins: the result is stack[1] if it
//     exists, and only otherwise the matched point.
using System;
using System.Collections.Generic;
using System.IO;
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        // APawn::calcMoveFlags (Engine.dll.c:92186). Packs seven pawn bools into
        // the reach-flag word a ReachSpec is matched against. The shipped body
        // is a chain of shifts; the bit ORDER is not the property order, so it
        // is written out here rather than derived:
        //
        //   1 walk, 2 fly, 4 swim, 8 jump, 16 door, 32 special, 64 playerOnly
        //
        // Names come from the shipped layout (bool word APawn+0x25c:
        // bit 0x2 bIsPlayer, 0x1000 bCanJump, 0x2000 bCanWalk, 0x4000 bCanSwim,
        // 0x8000 bCanFly, 0x10000 bCanOpenDoors, 0x20000 bCanDoSpecial), which
        // is what turned the shift chain into readable flags.
        static int CalcMoveFlags(UEObject pawn)
        {
            int flags = 0;
            if (UEInterpreter.ToBool(pawn.GetProp(PK.bCanWalk))) flags |= 1;
            if (UEInterpreter.ToBool(pawn.GetProp(PK.bCanFly))) flags |= 2;
            if (UEInterpreter.ToBool(pawn.GetProp(PK.bCanSwim))) flags |= 4;
            if (UEInterpreter.ToBool(pawn.GetProp(PK.bCanJump))) flags |= 8;
            if (UEInterpreter.ToBool(pawn.GetProp(PK.bCanOpenDoors))) flags |= 16;
            if (UEInterpreter.ToBool(pawn.GetProp(PK.bCanDoSpecial))) flags |= 32;
            if (UEInterpreter.ToBool(pawn.GetProp(PK.bIsPlayer))) flags |= 64;
            return flags;
        }

        // APawn::CanMoveTo (Engine.dll.c:130571). "Is there a ReachSpec from
        // `start` straight to `target` that THIS pawn fits through, with nothing
        // in the way that would stop it?"
        //
        // Two lists are scanned, not one: Paths[16] and then PrunedPaths[16]
        // (NavigationPoint+0x2a4 and +0x2e4 — resolved from the shipped layout;
        // the second list is not "upstreamPaths").
        // Each list ends at its own -1 sentinel.
        //
        // The size gates compare ROUNDED INTEGERS against the spec's integer
        // fields, so a pawn half a unit too wide still fits. The flag gate is
        // containment, not intersection: every bit the spec demands must be in
        // the pawn's own move flags.
        //
        // What can block it is only a Mover, and only conditionally: a clean
        // trace, a non-Mover hit, or a Mover the pawn is allowed through all
        // answer "yes". A pawn that cannot open doors at all does NOT get to
        // answer here — it keeps scanning the remaining specs.
        public bool CanMoveTo(UEInterpreter interp, UEObject pawn, UEObject start,
            UEObject target)
        {
            // NONE of these checks answers "unreachable". A missing ReachSpecs table,
            // Paths list, valid index or resolvable spec end means the port cannot
            // ANSWER the question, not that the answer is negative: a silent false
            // would make an unloaded or half-built Core world look like an honest
            // "no path". So: loud STOP, the same way FindPath in this file does.
            if (pawn is null || start is null || target is null)
                throw new InvalidDataException(
                    "CanMoveTo called with a null argument "
                    + $"(pawn={pawn?.GetProp(PK.Name)}, start={start?.GetProp(PK.Name)}, "
                    + $"target={target?.GetProp(PK.Name)}) — the shipped caller "
                    + "execactorReachable rejects null BEFORE the call");
            if (ReachSpecs is null)
                throw new InvalidDataException(
                    "CanMoveTo without a ReachSpecs table: the Core world is not loaded. "
                    + "Shipped reads level->ReachSpecs unconditionally, so "
                    + "\"unreachable\" here would substitute a normal game answer "
                    + "for an unloaded world");

            int radius = (int)System.Math.Round(
                UEInterpreter.AsFloat(pawn.GetProp(PK.CollisionRadius)),
                System.MidpointRounding.ToEven);
            int height = (int)System.Math.Round(
                UEInterpreter.AsFloat(pawn.GetProp(PK.CollisionHeight)),
                System.MidpointRounding.ToEven);
            int moveFlags = CalcMoveFlags(pawn);
            Vector3 startLoc = UEInterpreter.AsVector3(start.GetProp(PK.Location));
            Vector3 targetLoc = UEInterpreter.AsVector3(target.GetProp(PK.Location));

            foreach (string listName in PathListNames)
            {
                // MEASURED: a real NavigationPoint always carries BOTH lists —
                // PrunedPaths is not overridden on any Lev_Tut1 node but comes from
                // the class defaults as 16 elements of -1. A missing list means not
                // "a node without paths" but the wrong object.
                if (start.GetProp(listName) is not System.Collections.IList list)
                    throw new InvalidDataException(
                        $"CanMoveTo: {start.GetProp(PK.Name)} has no {listName}[16] — "
                        + "this is not a loaded NavigationPoint");
                // No "list without a trailing -1" check: shipped bounds the scan
                // ONLY by the counter (`while (i < 0x10)`, 130602), and -1 merely
                // ends the array EARLY. Sixteen occupied slots are valid data on
                // which the original normally answers 0. What the shape does fix is
                // the array SIZE: the body reads exactly 16 ints at fixed offsets.
                if (list.Count != 16)
                    throw new InvalidDataException(
                        $"CanMoveTo: {start.GetProp(PK.Name)}.{listName} has length {list.Count}, "
                        + "while shipped reads exactly 16 elements at fixed offsets");
                for (int i = 0; i < 16; i++)
                {
                    int specIndex = UEInterpreter.ToInt(list[i]);
                    if (specIndex == -1) break;              // array ended EARLY
                    if (specIndex < 0 || specIndex >= ReachSpecs.Count)
                        throw new InvalidDataException(
                            $"CanMoveTo: {start.GetProp(PK.Name)}.{listName}[{i}]={specIndex} "
                            + $"outside ReachSpecs[{ReachSpecs.Count}]");
                    LevelReachSpecRef spec = ReachSpecs[specIndex];
                    // End == None (a deleted path node, see LoadReachSpecs): the body compares the End POINTER with
                    // the target (Engine 0x1040144a `mov ecx,[edx+4*ecx+8]; cmp ecx,eax; jne`) and reads nothing through
                    // it: NULL is just "not this target".
                    if (spec.EndExport == 0) continue;
                    if (!ActorsByExport.TryGetValue(spec.EndExport, out UEObject specEnd))
                        throw new InvalidDataException(
                            $"CanMoveTo: ReachSpec[{specIndex}] with end {spec.EndExport} "
                            + "does not resolve to an actor — the actor graph is incomplete");
                    if (!ReferenceEquals(specEnd, target)) continue;

                    if (radius > spec.CollisionRadius || height > spec.CollisionHeight) continue;
                    // CONTAINMENT, not intersection — every bit the spec demands
                    // must be present. Measured caveat: all 5956 ReachSpecs in
                    // the whole campaign (460 of them in Lev_Tut1) carry flags
                    // == 32 and nothing else, so on shipped data the two
                    // readings are indistinguishable and no fixture can separate
                    // them. Kept as the machine code has it; do not "simplify".
                    if ((spec.ReachFlags & moveFlags) != spec.ReachFlags) continue;

                    // Zero extent, Movers+Level only (flags 6): pawns are not in
                    // the way of a reachability question.
                    UEObject blocker = SingleLineCheck(interp, pawn, pawn,
                        startLoc, targetLoc, Vector3.zero, 6).Actor;
                    if (blocker is null) return true;
                    if (!UEInterpreter.ClassIsA(blocker.Class, "Mover")) return true;
                    if (UEInterpreter.ToBool(pawn.GetProp(PK.bCanOpenDoors)))
                    {
                        if (UEInterpreter.ToBool(pawn.GetProp(PK.bIsPlayer))) return true;
                        if (!UEInterpreter.ToBool(blocker.GetProp(PK.bPlayerOnly))) return true;
                    }
                }
            }
            return false;
        }

        static readonly string[] PathListNames = { "Paths", "PrunedPaths" };

        // APawn::execactorReachable (Engine.dll.c:70360) — native 520. The exec
        // body is NOT a thin forwarder, so it is ported as a whole rather than
        // being reduced to a call to actorReachable:
        //
        //   * an Inventory target is swapped for its MyMarker (Inventory+0x2dc,
        //     offset checked against the shipped layout; it is not "Owner"), and the ORIGINAL actor is put back before the final
        //     query. The marker is used only for the proximity shortcut.
        //   * a NavigationPoint target, asked by a SMALL pawn in a level whose
        //     ReachSpecs table is NOT EMPTY, gets a proximity shortcut: a nav point within CollisionHeight
        //     vertically and max(CollisionRadius, 48) horizontally counts as
        //     "already there" when CanMoveTo says so.
        //   * if such a near point existed and none of them worked out, the
        //     answer is a flat 0 — unless the pawn is flying.
        //
        // The walk over LevelInfo.NavigationPointList is ported as a plain read
        // of that property, and that is the FAITHFUL shape, not a stub: in the
        // shipped runtime the list is empty. Measured, three ways — the only
        // writer in Engine.dll is FPathBuilder::definePaths, reached only from
        // FPathBuilder::buildPaths (the editor's Build Paths); no shipped map
        // serializes the property or nextNavigationPoint (control: the same
        // dumper does emit LevelInfo.Summary and .Level, both object refs); and
        // no script package outside Engine.u even carries the name. If anything
        // ever does fill it, this walk picks it up for free.
        public int ActorReachableNative(UEInterpreter interp, UEObject self, UEObject target)
        {
            if (self is null || target is null) return 0;

            UEObject savedInventory = null;
            if (UEInterpreter.ClassIsA(target.Class, "Inventory")
                && target.GetProp(PK.MyMarker) as UEObject is UEObject marker)
            {
                savedInventory = target;
                target = marker;
            }

            // The gate is the level's NUMBER of ReachSpecs, not the presence of a BSP:
            // `cmp dword ptr [XLevel+0x90], 0` (0x103d7a2c; ULevel+0x8c is the
            // ReachSpecs data, indexed by CanMoveTo 130609; +0x90 is Num).
            if (UEInterpreter.ClassIsA(target.Class, "NavigationPoint")
                && ReachSpecs.Count != 0
                && UEInterpreter.AsFloat(self.GetProp(PK.CollisionRadius)) <= 18f)
            {
                float limit = UEInterpreter.AsFloat(self.GetProp(PK.CollisionRadius));
                if (limit <= 48f) limit = 48f;
                float height = UEInterpreter.AsFloat(self.GetProp(PK.CollisionHeight));
                Vector3 selfLoc = UEInterpreter.AsVector3(self.GetProp(PK.Location));
                bool nearOne = false;

                bool Near(UEObject point)
                {
                    Vector3 p = UEInterpreter.AsVector3(point.GetProp(PK.Location));
                    float dz = p.z - selfLoc.z;
                    if (dz < 0f) dz = -dz;
                    float dx = p.x - selfLoc.x, dy = p.y - selfLoc.y;
                    return dz < height && dx * dx + dy * dy < limit * limit;
                }

                // The pawn's own MoveTarget is tried first, and then the walk
                // runs REGARDLESS: shipped reaches the list label from every
                // outcome of this test, including the near-but-unreachable one
                // (every arm ends in `goto LAB_103d7b4e`). Making this an `else`
                // is invisible on shipped data — the list is empty — and still
                // wrong, so it is written the way the body branches.
                if (self.GetDeclared("Pawn", "MoveTarget") as UEObject is UEObject moving
                    && UEInterpreter.ClassIsA(moving.Class, "NavigationPoint")
                    && Near(moving))
                {
                    nearOne = true;
                    if (ReferenceEquals(moving, target)
                        || CanMoveTo(interp, self, moving, target)) return 1;
                }

                for (UEObject nav = LevelSentinel?.GetProp(PK.NavigationPointList) as UEObject;
                     nav is not null;
                     nav = nav.GetProp(PK.nextNavigationPoint) as UEObject)
                {
                    if (!Near(nav)) continue;
                    nearOne = true;
                    if (ReferenceEquals(nav, target)
                        || CanMoveTo(interp, self, nav, target)) return 1;
                }

                // PHYS_Flying (4) is the one physics that keeps asking.
                if (nearOne && UEInterpreter.ToInt(self.GetProp(PK.Physics)) != 4) return 0;
            }

            return ActorReachable(interp, self, savedInventory ?? target, bKnowVisible: false);
        }

        public UEObject FindPath(UEObject start, string destination)
        {
            if (start is null) return null;

            const int MaxNodes = 100;
            var stack = new UEObject[MaxNodes];
            var cursors = new int[MaxNodes];
            var visited = new UEObject[MaxNodes];
            Array.Fill(cursors, -1);
            stack[0] = start;
            visited[0] = start;
            int visitedCount = 1;
            int depth = 0;
            bool rescan = false;

            while (depth >= 0)
            {
                UEObject current = stack[depth]
                    ?? throw new InvalidDataException($"FindPath null DFS node at depth {depth}");
                if (current.GetProp(PK.Paths) is not System.Collections.IList paths)
                    throw new InvalidDataException($"FindPath actor {current.GetProp(PK.Name)} has no Paths[16]");

                int slot = ++cursors[depth];
                if (slot >= paths.Count)
                    throw new InvalidDataException(
                        $"FindPath actor {current.GetProp(PK.Name)} Paths has no -1 sentinel (count={paths.Count})");
                int specIndex = UEInterpreter.ToInt(paths[slot]);
                if (specIndex >= 0)
                {
                    if (specIndex >= ReachSpecs.Count)
                        throw new InvalidDataException(
                            $"FindPath actor {current.GetProp(PK.Name)} Paths[{slot}]={specIndex} outside ReachSpecs[{ReachSpecs.Count}]");
                    LevelReachSpecRef spec = ReachSpecs[specIndex];
                // Shipped Logf pair (Engine.dll.c:99677-99684): BOTH endpoint
                // names of every spec the walk considers, before any decision
                // about it. Journalled rather than dropped — the port's Log is
                // where shipped Logf sites land, and losing them would make the
                // original's output and this one diverge for no stated reason.
                // ... and that Logf pair is where the original DIES on a spec with a None endpoint: Engine
                // 0x1040264e..0x10402655 (`mov eax,[eax+4*edx+4]; add eax,0x20; mov eax,[eax]`) reads Start->Name
                // with no null test, 0x1040267a..0x10402683 the same for End -> access violation at address 0x20.
                // There is no behaviour to port past this point, so the port stops here too, saying why.
                if (spec.StartExport == 0 || spec.EndExport == 0)
                    throw new InvalidDataException(
                        $"FindPath: {System.IO.Path.GetFileName(LoadedMapPath)} ReachSpec[{specIndex}] has a None endpoint "
                        + $"(Start={spec.StartExport}, End={spec.EndExport}; a path node deleted in the editor without "
                        + "rebuilding paths). The original game crashes here (access violation in APawn::findPath, "
                        + "Engine 0x1040264e): the map needs its paths rebuilt");
                ActorsByExport.TryGetValue(spec.StartExport, out UEObject logStart);
                ActorsByExport.TryGetValue(spec.EndExport, out UEObject logEnd);
                Journal?.Invoke($"find_path_spec startname={logStart?.GetProp(PK.Name) ?? "None"}"
                    + $" endname={logEnd?.GetProp(PK.Name) ?? "None"}");
                    if (!ActorsByExport.TryGetValue(spec.StartExport, out UEObject specStart)
                        || !ActorsByExport.TryGetValue(spec.EndExport, out UEObject specEnd))
                        throw new InvalidDataException(
                            $"FindPath ReachSpec[{specIndex}] unresolved endpoints {spec.StartExport}/{spec.EndExport}");

                    // Exact shipped selection: try Start unless it is already in
                    // the zero-terminated visited array, otherwise try End. The
                    // native trusts Paths to contain only incident specs; it does
                    // not independently check that current equals either endpoint.
                    UEObject candidate = null;
                    bool startVisited = false;
                    for (int i = 0; i < visitedCount; i++)
                        if (ReferenceEquals(visited[i], specStart)) { startVisited = true; break; }
                    if (!startVisited) candidate = specStart;
                    else
                    {
                        bool endVisited = false;
                        for (int i = 0; i < visitedCount; i++)
                            if (ReferenceEquals(visited[i], specEnd)) { endVisited = true; break; }
                        if (!endVisited) candidate = specEnd;
                    }

                    if (candidate is not null)
                    {
                        if (visitedCount >= MaxNodes)
                            throw new NotSupportedException("FindPath shipped 100-node visited array exhausted");
                        visited[visitedCount++] = candidate;
                        string candidateName = candidate.GetProp(PK.Name) is UEName ueName
                            ? ueName.Value : candidate.GetProp(PK.Name)?.ToString();
                        if (string.Equals(candidateName, destination, StringComparison.OrdinalIgnoreCase))
                        {
                            UEObject result = stack[1] ?? candidate;
                            Journal?.Invoke($"find_path start={start.GetProp(PK.Name)} dest={destination} "
                                + $"result={result.GetProp(PK.Name)} visited={visitedCount}");
                            return result;
                        }

                        // Offset +0x24 in the shipped UObject is the exact UClass
                        // pointer. appStricmp(class,"baseStation") is called but
                        // its return value is discarded; only pointer equality to
                        // the start actor's class controls descent. Do not replace
                        // this with IsA/subclass or name matching.
                        if (ReferenceEquals(candidate.Class, start.Class) || start.Class is null)
                        {
                            if (depth + 1 >= MaxNodes)
                                throw new NotSupportedException("FindPath shipped 100-node DFS stack exhausted");
                            stack[++depth] = candidate;
                        }
                    }
                    continue;
                }

                // One flag for the whole walk (see the header): a -1 that SETS it
                // restarts this node; a -1 that CLEARS it pops and resets the
                // parent's cursor, making the parent rescan too. Kept as a single
                // variable on purpose — a per-node flag would change the order.
                rescan = !rescan;
                if (rescan)
                {
                    cursors[depth] = -1;
                }
                else
                {
                    stack[depth] = null;
                    cursors[depth] = -1;
                    depth--;
                    if (depth >= 0) cursors[depth] = -1;
                }
            }

            Journal?.Invoke($"find_path start={start.GetProp(PK.Name)} dest={destination} result=None visited={visitedCount}");
            return null;
        }

        // APawn::clearPaths (disasm 0x104006b0..0x1040080a), native 522 and FindRandomDest's head. Walks
        // XLevel->Actors[0].NavigationPointList (+0x494) by nextNavigationPoint (+0x370); per point, in this order:
        // visitedWeight = 10000000, nextOrdered = 0, bEndPoint cleared, prevOrdered = 0, then cost = bSpecialCost
        // ? eventSpecialCost(this) (ReturnValue zeroed before ProcessEvent) : ExtraCost.
        public void ClearPaths(UEInterpreter interp, UEObject pawn)
        {
            for (UEObject nav = interp.GetLevelInfo()?.GetProp(PK.NavigationPointList) as UEObject; nav is not null;
                 nav = nav.GetProp(PK.nextNavigationPoint) as UEObject)
            {
                nav.Props["visitedWeight"] = 10000000;
                nav.Props["nextOrdered"] = null;
                nav.Props["bEndPoint"] = false;
                nav.Props["prevOrdered"] = null;
                nav.Props["cost"] = UEInterpreter.ToBool(nav.GetProp("bSpecialCost"))
                    ? UEInterpreter.ToInt(UELifecycle.CallEventResult(interp, nav, "SpecialCost", pawn))
                    : UEInterpreter.ToInt(nav.GetProp("ExtraCost"));
            }
        }

        // The shared gate of findPathTo (0x104028f4), findPathToward (0x10401b63), findPathTowardBestInventory
        // (0x1040223b) and findRandomDest (0x10402dfb): Actors[0].NavigationPointList == NULL or ReachSpecs.Num == 0
        // -> "no path" (0, or 0.0f for the inventory search). Every shipped map has an empty NavigationPointList (see
        // ActorReachableNative), so this is the answer on shipped data. A map with a built path network reaches the
        // search proper (FSortedPathList, breadthPathFrom/TraverseFrom, SetRouteCache, HandleSpecial), not ported:
        // a loud STOP instead of a guessed route.
        public void RequireNoPathNetwork(UEInterpreter interp, string who)
        {
            if (interp.GetLevelInfo()?.GetProp(PK.NavigationPointList) is null || ReachSpecs.Count == 0) return;
            throw new NotSupportedException($"{who}: this map has a path network (NavigationPointList and "
                + $"{ReachSpecs.Count} ReachSpecs); the shipped route search is not ported");
        }

        // The tail every path exec shares when the search gave no path (execFindPathTo 0x103d8040, execFindPathToward
        // 0x103d81f6, execFindBestInventoryPath 0x103d4c49 when the weight beat MinWeight): SpecialPause = 0,
        // bShootSpecial (+0x25c bit 0x2000000) cleared; HandleSpecial runs only for a non-null path and
        // `if (path == SpecialGoal) SpecialGoal = 0` changes nothing for a null one.
        public static void NoPathTail(UEObject pawn)
        {
            pawn.Props["SpecialPause"] = 0f;
            pawn.Props["bShootSpecial"] = false;
        }

        // ANavigationPoint::execdescribeSpec (disasm 0x103d62f0..0x103d6455): XLevel->ReachSpecs[iSpec] (0x1c-byte
        // FReachSpec) -> out Start (+4), End (+8), ReachFlags (+0x14), Distance (+0). No bounds test in shipped: an
        // index outside the table reads past it, which the port cannot reproduce — a loud STOP.
        public void DescribeSpecNative(object[] args)
        {
            int i = UEInterpreter.ToInt(args.Length > 0 ? args[0] : null);
            if (i < 0 || i >= ReachSpecs.Count)
                throw new NotSupportedException($"describeSpec({i}) outside ReachSpecs[{ReachSpecs.Count}]: shipped reads "
                    + "past the table (no bounds test, 0x103d640a)");
            LevelReachSpecRef spec = ReachSpecs[i];
            UEObject Resolve(int export)
            {
                if (export == 0) return null;
                return ActorsByExport.TryGetValue(export, out UEObject a) ? a : throw new InvalidDataException(
                    $"describeSpec: ReachSpec[{i}] endpoint {export} does not resolve to an actor");
            }
            if (args.Length > 1) args[1] = Resolve(spec.StartExport);
            if (args.Length > 2) args[2] = Resolve(spec.EndExport);
            if (args.Length > 3) args[3] = spec.ReachFlags;
            if (args.Length > 4) args[4] = spec.Distance;
        }
    }
}
