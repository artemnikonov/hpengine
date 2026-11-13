// Actor lifecycle: SpawnActor, DestroyActor (+ ExitProperties), SetOwner, SetBase, and zone/region
// tracking (SetActorZone, ResolveRegion). Fields live in UELevelRuntime.cs.
using System;
using System.Collections.Generic;
using System.IO;
using UELib.Core;
using Vector3 = HP1.UEScript.UEVector;
using X87Ext = HP1.Core.X87Ext;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        public bool DestroyActor(UEInterpreter interp, UEObject self)
        {
            if (self is null) return false;
            if (UEInterpreter.ToBool(self.GetProp(PK.bStatic)) || UEInterpreter.ToBool(self.GetProp(PK.bNoDelete)))
                return false;
            if (self.Destroyed) return true;
            // Role refusal (FUN_103a72c0, Engine.dll.c:252973-252976; disasm
            // 0x103a7353..0x103a7377 — after `test al,5` and `test ah,2`, outside the editor):
            //   Role (+0x31) != ROLE_Authority(4) && !bNetForce && !(+0x28 & 0x4000000) -> 0.
            // Bit 0x4000000 of word +0x28 is Actor.bNetTemporary (Engine.u layout;
            // the same layout gives
            // bStatic 0x1, bNoDelete 0x4, bDeleteMe 0x200 and Role +0x31 — exactly the ones
            // this body checks). bNetForce: execDestroy passes 0 (`push 0`, 0x1040cc32);
            // 1 appears in the decompile only in network UPlayer::Destroy (106470) and
            // UActorChannel::Destroy (154858), absent from the port. Other DestroyActor calls
            // in the port pass 0; that all their shipped counterparts pass 0 is not verified
            // for each — with Role = 4 the value has no effect.
            // In HP1 the role is set only by Actor (4); the branch is unreachable in the
            // campaign and is ported as is.
            if (UEInterpreter.ToInt(self.GetProp(PK.Role)) != 4
                && !UEInterpreter.ToBool(self.GetProp("bNetTemporary")))
                return false;

            // EndState notification (253001-253012): shipped gates on
            // StateFrame != 0 && StateFrame->StateNode != 0 && IsProbing(EndState).
            // Every shipped actor has a StateFrame from InitExecution, and its
            // StateNode is the class when no state is set (GotoState of an
            // unknown state also leaves StateNode = Class), so the effective
            // gate is IsProbing alone — CallEvent applies it. The port creates
            // StateFrame lazily (first GotoStateCore), so its null must NOT
            // skip the dispatch (D-13).
            UELifecycle.CallEvent(interp, self, "EndState");
            if (self.Destroyed) return true;
            // Remove from base (253015-253016, bNotifyActor=1).
            if (self.GetProp(PK.Base) is not null)
            {
                SetBase(interp, self, null, notify: true);
                if (self.Destroyed) return true;
            }
            // Detach every rider based on us (253018-253025, StandingCount gate).
            if (UEInterpreter.ToInt(self.GetProp(PK.StandingCount)) != 0)
                foreach (UEObject rider in Actors.ToArray())
                    if (rider is not null && !rider.Destroyed
                        && ReferenceEquals(rider.GetProp(PK.Base) as UEObject, self))
                        SetBase(interp, rider, null, notify: true);
            // Collision-hash removal before the Destroyed event (253027-253031).
            // Core-owned: the hash is Core's structure, so Core takes the actor
            // out of it; a frontend that forgot to would leave every destroyed actor
            // linked in the hash forever. The hook is for frontend-only
            // teardown — stopping sounds, dropping render proxies — never for
            // the hash itself.
            HashRemoveForDestroy(self);
            DestroyCleanupHook?.Invoke(self);
            // Tell the actor it is about to go (253033-253035) — still live.
            UELifecycle.CallEvent(interp, self, "Destroyed");
            if (self.Destroyed) return true;
            // One pass over the level (253038-253059): orphan our children, and
            // unlink every touch pair FROM THE HOLDER's side — an asymmetric
            // link (they hold us, we do not hold them) is found here, not in
            // our own list. bNoNotify=1: the dying side gets no UnTouch (253052).
            foreach (UEObject a in Actors.ToArray())
            {
                if (a is null || a.Destroyed || ReferenceEquals(a, self)) continue;
                if (ReferenceEquals(a.GetProp(PK.Owner) as UEObject, self))
                {
                    SetOwner(interp, a, null);
                    if (self.Destroyed) return true;
                }
                else if (Array.IndexOf(a.TouchList(), self) >= 0)
                {
                    UELifecycle.EndTouch(interp, self, a, noNotifySelf: true);
                    if (self.Destroyed) return true;
                }
            }
            // NB the shipped pass unlinks only from the HOLDER's side; a link
            // only WE hold (they never linked back) dies with our slot — no
            // event is due on the other actor, exactly like shipped.
            // LostChild on OUR owner last (253061-253070; Owner itself is not
            // cleared — the actor is gone after this call).
            if (self.GetProp(PK.Owner) is UEObject owner)
            {
                UELifecycle.CallEvent(interp, owner, "LostChild", self);
                if (self.Destroyed) return true;
            }
            // The end: the slot dies and the script-visible flag flips
            // (253086-253087).
            self.Destroyed = true;
            self.Props["bDeleteMe"] = true;
            // Engine->Audio->NoteDestroy(Actor) (vtable +0x7c, 253088-253089): the ambient slot
            // is silenced, other channels are detached from the actor.
            FrameProducer?.Audio.NoteDestroy(self.ObjectId);
            ExitProperties(self.Class, self);   // ConditionalDestroy -> UObject::Destroy (253087)
            // Outside the editor the actor joins Level->FirstDeleted (disasm
            // 0x103a7801..0x103a7812); CleanupDestroyed clears the list.
            FirstDeletedCount++;
            DestroyTeardownHook?.Invoke(self);
            return true;
        }

        // UObject::ExitProperties (Core 0x1015bdf0), from UObject::Destroy (0x101594e0), which
        // ULevel::DestroyActor calls immediately (ConditionalDestroy, 253087): DestroyValue of every
        // ConstructorLink property — those with CPF_NeedCtorLink (UStrProperty::Link, Core.dll.c:3669; array;
        // struct with such members). ~FString zeroes Num/Max (0x10106cad-0x10106cb0): the corpse's string
        // reads as "", array<> is empty. References, numbers, names are untouched. A struct without a value
        // in Props (read from class defaults) is not materialised — a known limit.
        static void ExitProperties(UStruct def, UEObject o)
        {
            for (UStruct c = def; c is not null; c = c is UClass cls ? UEInterpreter.CanonicalSuper(cls) : c.Super as UStruct)
                foreach (UProperty p in c.EnumerateFields<UProperty>())
                {
                    string name = p.Name?.ToString();
                    if (name is null) continue;
                    switch (p)
                    {
                        case UStrProperty when p.ArrayDim > 1:
                            if (o.Props.TryGetValue(name, out object sa) && sa is System.Collections.IList sl)
                                for (int i = 0; i < sl.Count; i++) sl[i] = "";
                            else o.Props[name] = new List<object>(System.Linq.Enumerable.Repeat<object>("", p.ArrayDim));
                            break;
                        case UStrProperty:
                            o.Props[name] = "";
                            break;
                        case UArrayProperty:
                            o.Props[name] = new List<object>();
                            break;
                        case UStructProperty sp when sp.Struct is not null:
                            if (!o.Props.TryGetValue(name, out object v)) break;
                            if (v is UEObject bag) ExitProperties(sp.Struct, bag);
                            else if (v is System.Collections.IList items)
                                foreach (object e in items) if (e is UEObject eb) ExitProperties(sp.Struct, eb);
                            break;
                    }
                }
        }

        // ── AActor::SetOwner (Engine.dll.c:44726): LostChild on the old owner,
        // set Owner, GainedChild on the new. Shipped keeps no child list. ──
        public void SetOwner(UEInterpreter interp, UEObject actor, UEObject newOwner)
        {
            UEObject old = actor.GetProp(PK.Owner) as UEObject;
            if (old is not null) UELifecycle.CallEvent(interp, old, "LostChild", actor);
            actor.Props["Owner"] = newOwner;
            if (newOwner is not null) UELifecycle.CallEvent(interp, newOwner, "GainedChild", actor);
        }

        // ── AActor::SetBase (Engine.dll.c:160728): re-parent onto NewBase with
        // StandingCount + Detach/Attach/BaseChange events. Level() is a valid
        // base but is excluded from the count and the events. ──
        public void SetBase(UEInterpreter interp, UEObject actor, UEObject newBase, bool notify)
        {
            // Cycle guard: if `actor` is anywhere in NewBase's base chain,
            // abort (shipped walks the chain and returns without touching
            // anything).
            for (UEObject p = newBase; p is not null; p = p.GetProp(PK.Base) as UEObject)
                if (ReferenceEquals(p, actor)) return;
            UEObject cur = actor.GetProp(PK.Base) as UEObject;
            if (ReferenceEquals(newBase, cur)) return;
            UEObject level = actor.GetProp(PK.Level) as UEObject;
            if (cur is not null && !ReferenceEquals(cur, level))
            {
                cur.Props["StandingCount"] = UEInterpreter.ToInt(cur.GetProp(PK.StandingCount)) - 1;
                UELifecycle.CallEvent(interp, cur, "Detach", actor);
            }
            actor.Props["Base"] = newBase;
            if (newBase is not null && !ReferenceEquals(newBase, level))
            {
                newBase.Props["StandingCount"] = UEInterpreter.ToInt(newBase.GetProp(PK.StandingCount)) + 1;
                UELifecycle.CallEvent(interp, newBase, "Attach", actor);
            }
            if (notify) UELifecycle.CallEvent(interp, actor, "BaseChange");
        }
        internal bool HashLinked(UEObject a) => a is not null && _hashHandle.TryGetValue(a, out int h) && (CollisionHash?.LinkCount(h) ?? 0) > 0;

        public void SetActorZone(UEInterpreter interp, UEObject actor, Vector3 location,
            bool bTest = false, bool bForceRefresh = false)
        {
            SetActorZoneProbe?.Invoke(actor, location, HashLinked(actor));
            if (actor.Destroyed) return;                        // bDeleteMe gate (256304)
            UEObject level = interp.GetLevelInfo();
            bool isPawn = UEInterpreter.ClassIsA(actor.Class, "Pawn");
            if (ReferenceEquals(actor, level))
            {
                actor.SetDeclared("Actor", "Region", UEObject.MakeRegion(level, -1, 0));   // 256322-256325
                return;
            }
            if (bForceRefresh)                                   // 256339-256370
            {
                actor.SetDeclared("Actor", "Region", UEObject.MakeRegion(level, -1, 0));
                if (isPawn)
                {
                    actor.Props["FootRegion"] = UEObject.MakeRegion(level, -1, 0);
                    actor.Props["HeadRegion"] = UEObject.MakeRegion(level, -1, 0);
                }
            }
            (UEObject zone, int leaf, byte zoneNumber) = ResolveRegion(location, level);
            UEObject oldZone = (actor.GetDeclared("Actor", "Region") as UEObject)?.GetProp(PK.Zone) as UEObject
                ?? level;                                        // shipped seed is {LevelInfo,-1,0}
            if (ReferenceEquals(zone, oldZone))
                actor.SetDeclared("Actor", "Region", UEObject.MakeRegion(zone, leaf, zoneNumber));
            else
            {
                if (!bTest)
                {
                    UELifecycle.CallEvent(interp, oldZone, "ActorLeaving", actor);
                    UELifecycle.CallEvent(interp, actor, "ZoneChange", zone);
                }
                actor.SetDeclared("Actor", "Region", UEObject.MakeRegion(zone, leaf, zoneNumber));
                if (!bTest) UELifecycle.CallEvent(interp, zone, "ActorEntered", actor);
            }
            if (!isPawn) return;
            float collisionHeight = UEInterpreter.AsFloat(actor.GetProp(PK.CollisionHeight));
            (UEObject footZone, int footLeaf, byte footNumber) =
                ResolveRegion(location - new Vector3(0f, 0f, collisionHeight), level);
            UEObject oldFoot = (actor.GetProp(PK.FootRegion) as UEObject)?.GetProp(PK.Zone) as UEObject
                ?? level;
            if (!ReferenceEquals(footZone, oldFoot) && !bTest)
                UELifecycle.CallEvent(interp, actor, "FootZoneChange", footZone);
            actor.Props["FootRegion"] = UEObject.MakeRegion(footZone, footLeaf, footNumber);
            float eyeHeight = UEInterpreter.AsFloat(actor.GetProp(PK.EyeHeight));
            (UEObject headZone, int headLeaf, byte headNumber) =
                ResolveRegion(location + new Vector3(0f, 0f, eyeHeight), level);
            UEObject oldHead = (actor.GetProp(PK.HeadRegion) as UEObject)?.GetProp(PK.Zone) as UEObject
                ?? level;
            if (!ReferenceEquals(headZone, oldHead) && !bTest)
                UELifecycle.CallEvent(interp, actor, "HeadZoneChange", headZone);
            actor.Props["HeadRegion"] = UEObject.MakeRegion(headZone, headLeaf, headNumber);
            if (actor.GetProp(PK.PlayerReplicationInfo) is UEObject pri)
                pri.Props["PlayerZone"] = (actor.GetDeclared("Actor", "Region") as UEObject)?.GetProp(PK.Zone);
        }

        // iLeaf is resolved by EXECUTION: shipped `UModel::PointRegion` was run under the
        // emulator on the real Tut1 model, and over 1259 actor positions it matched 1259/1259
        // on both zone and leaf once the leaf is taken from the SAME side as the zone
        // (UEBspCollision.cs, FindRegion).
        //
        // iLeaf is READ: it goes into WorldFrame and defines the actor's lighting candidates —
        // shipped takes them from the leaf sublist (StaticGeometry.LeafPermeating/LightExports),
        // not by radius.
        // ULevel::PointRegion is CORE: Core has its OWN loaded world model (Bsp) and actor table
        // by export (ActorsByExport), so player zones and collision zones come from one model.
        // A null zone (no Bsp) resolves to LevelInfo — as with shipped PointRegion finding
        // no zone.
        (UEObject Zone, int Leaf, byte ZoneNumber) ResolveRegion(Vector3 location, UEObject level)
        {
            if (Bsp is null) return (level, -1, 0);
            BspRegion region = Bsp.FindRegion(location);
            UEObject zone = ActorsByExport.TryGetValue(region.ZoneActorIndex, out UEObject z) ? z : null;
            return (zone ?? level, region.Leaf, (byte)region.ZoneNumber);
        }

        // ── ULevel::SpawnActor (Engine.dll.c:20085-20306): FindSpot before
        // construction, class+counter name, {LevelInfo,-1,0} region seed, hash
        // residency before Spawned, the Spawned/PreBeginPlay/BeginPlay →
        // SetActorZone(force) → PostBeginPlay → encroachment re-check →
        // SetInitialState order, then the base probe. ──
        public UEObject SpawnActor(UEInterpreter interp, UEObject self, UClass cls, UEObject owner,
            string tag, Vector3 location, UERotator rotation,
            Func<UEObject> premadeFactory = null, UEObject instigator = null,
            bool noCollisionFail = false)
        {
            // ── CLASS CANONICALISATION ───────────────────────────────────────
            //
            // A cross-package class literal arrives as an IMPORT STUB: same name, no body.
            // UE1 links it to the real class of that name, so spawn must take the same step
            // HERE — before anyone reads defaults or looks up functions. Otherwise e.g.
            // `HUDType` (an import) spawns the engine HPHud as a stub whose PostRender does
            // not resolve, and the HUD is empty. This step has one place — here.
            if (HP1.Core.UEClassRegistry.IsImportStub(cls) && HP1.Core.UEClassRegistry.Shared is not null)
            {
                UClass canonical = HP1.Core.UEClassRegistry.Shared.FindForStub(cls);
                if (canonical is not null && !HP1.Core.UEClassRegistry.IsImportStub(canonical))
                    cls = canonical;
            }
            // ── ENTRY REFUSALS OF ULevel::SpawnActor (disasm 0x103a65ef..0x103a66fc) ──
            // in order: abstract class (UClass+0x488 & CLASS_Abstract), class not derived
            // from AActor, and outside the editor — bStatic (default +0x28 bit 1)
            // or bNoDelete (bit 4) of the default object: "SpawnActor failed because
            // class %s has bStatic or bNoDelete", NULL without side effects.
            // The same refusal makes AMover::Spawned (0x103be430) unreachable:
            // bNoDelete=True on Mover and all eleven of its campaign subclasses, and the
            // campaign never spawns a mover (Spawn census: no literals, no mover class among
            // Class<Actor> values in .unr and defaults; Summon under bCheatsEnabled also
            // stops here).
            {
                string refuse = (UEObjectConfig.ClassFlags(cls) & 0x1u) != 0 ? "abstract"
                    : !UEInterpreter.ClassIsA(cls, "Actor") ? "not_actor"
                    : UEInterpreter.ToBool(UEObject.ClassDefaultScalar(cls, "bStatic"))
                      || UEInterpreter.ToBool(UEObject.ClassDefaultScalar(cls, "bNoDelete")) ? "static_nodelete"
                    : null;
                if (refuse is not null)
                {
                    Log($"spawn_fail class={cls.Name} reason={refuse} location={location}");
                    return null;
                }
            }
            // Shipped SpawnActor reads the placement gate off the CLASS
            // DEFAULT object and runs FindSpot BEFORE any actor exists
            // (Engine.dll.c:20085-20113) — a refused spawn has NO observable
            // side effects: no ObjectId, no name-counter tick, no list entry,
            // no frontend proxy. The scalar class-default reads below are the
            // template reads (identical values to a LoadDefaults instance).
            object Def(string n) => UEObject.ClassDefaultScalar(cls, n);
            Log($"spawn_request class={cls.Name} self={self?.GetProp(PK.Name)} owner={owner?.GetProp(PK.Name)} location={location} rotation={rotation} collideWorld={Def("bCollideWorld")} collideWhenPlacing={Def("bCollideWhenPlacing")} collideActors={Def("bCollideActors")} blockActors={Def("bBlockActors")} blockPlayers={Def("bBlockPlayers")} radius={Def("CollisionRadius")} height={Def("CollisionHeight")}");
            // FindSpot (ULevel vtable 0xb4), gated on the template's
            // bCollideWorld or bCollideWhenPlacing. A spot that cannot be
            // adjusted fails the spawn → NULL. Actor-vs-actor encroachment is
            // NOT checked here: it runs after PostBeginPlay.
            // bNoCollisionFail (param_13) skips the probe entirely (20100):
            // travel-inventory respawn passes 1 (Engine 0x103a8a1d).
            if (!noCollisionFail
                && (UEInterpreter.ToBool(Def("bCollideWorld"))
                    || UEInterpreter.ToBool(Def("bCollideWhenPlacing"))))
            {
                // Extent = GetCylinderExtent(template) (Engine.dll.c:20102,
                // call 0x103a67d8), not (R, R, H).
                Vector3 spot = location;
                Vector3 spawnExtent = GetCylinderExtentOfTemplate(cls);
                // No Bsp means no world geometry is loaded: no probe runs and
                // no journal line is emitted (fits stays null).
                bool? fits = SpawnFindSpotHook is not null
                    ? SpawnFindSpotHook(spawnExtent, ref spot, true)
                    : Bsp is not null ? (bool?)FindSpot(spawnExtent, ref spot, true) : null;
                if (fits is not null)
                {
                    Log($"spawn_probe class={cls.Name} fit={(fits.Value ? "true" : "false")} "
                        + $"location={location} spot={spot}");
                    if (!fits.Value)
                    {
                        Log($"spawn_fail class={cls.Name} reason=findspot location={location}");
                        return null;
                    }
                    location = spot;
                }
            }

            // Construction happens only AFTER the placement probe passed
            // (shipped 20115+). A frontend factory builds its proxy here
            // (UEObject + defaults); a
            // factory refusal (class unresolvable) fails the spawn.
            UEObject actor = premadeFactory is not null
                ? premadeFactory()
                : new UEObject { Class = cls, Interp = interp };
            if (actor is null)
            {
                Log($"spawn_fail class={cls.Name} reason=frontend_construct location={location}");
                return null;
            }
            if (premadeFactory is null) actor.LoadDefaults();

            actor.Props["Name"] = new UEName(MakeUniqueSpawnName(interp, cls));
            // StaticConstructObject(Class, Level->GetOuter(), ...) — the outer of a
            // spawned actor is the level package (Engine.dll 0x103a6863-0x103a6881).
            actor.OuterPath = LevelPackageName;
            actor.Props["Location"] = location;
            actor.Props["OldLocation"] = location;
            actor.Props["Rotation"] = rotation;
            // ULevel::SpawnActor sets Tag = class FName (Engine.dll.c:20129,
            // +0x84 <- Class+0x20). execSpawn overwrites it with its SpawnTag
            // argument only AFTER SpawnActor returns (0x1040cb87, +0x84), so
            // Spawned/PreBeginPlay/BeginPlay/PostBeginPlay/SetInitialState see
            // the class name — see the tail of this method.
            actor.Props["Tag"] = new UEName(cls.Name);
            actor.Props["Level"] = self?.GetProp(PK.Level) ?? interp.GetLevelInfo();
            // bTicked = !XLevel->Ticked (Engine.dll.c:20191-20193): the spawn ticks in
            // the current or next actor pass.
            actor.Props.Set(PK.bTicked, LevelTicked ? BoxFalse : BoxTrue);
            // Shipped SpawnActor initializes Region to {LevelInfo, -1, zone 0}
            // (Engine.dll.c:20228-20232); the REAL zone comes from SetActorZone
            // only after BeginPlay. Spawned/PreBeginPlay/BeginPlay see LevelInfo.
            actor.SetDeclared("Actor", "Region", UEObject.MakeRegion(interp.GetLevelInfo(), -1, 0));
            // Owner and Instigator are the caller's explicit arguments — the
            // spawner is NOT an implicit owner (execSpawn 0x1040ca54 defaults
            // SpawnOwner to NULL and passes its own Instigator, 0x1040cb55).
            if (owner is not null)
            {
                actor.Props["Owner"] = owner;
                UFunction gained = UELifecycle.ActorEventsBlocked(owner) ? null : interp.FindEventFunction(owner, "GainedChild");
                if (gained is not null) Guard(() => interp.Invoke(owner, gained, actor), "GainedChild");
            }
            if (instigator is not null) actor.Props["Instigator"] = instigator;

            Actors.Add(actor);
            ActorsById[actor.ObjectId] = actor;
            interp.SpawnLog.Add(actor);
            // Shipped SpawnActor hashes the new actor right after the transform
            // fields are set, BEFORE Spawned/PreBeginPlay (Engine.dll.c:20216).
            if (UEInterpreter.ToBool(actor.GetProp(PK.bCollideActors))) HashAddActor(actor);
            Log($"spawn class={cls.Name} name={actor.GetProp(PK.Name)} owner={owner?.GetProp(PK.Name)}");
            SpawnPrepareHook?.Invoke(actor);
            void RunEvent(string evt)
            {
                if (actor.Destroyed) return;
                if (UELifecycle.ActorEventsBlocked(actor)) return;   // AActor::ProcessEvent: before bBegunPlay
                UFunction fn = interp.FindEventFunction(actor, evt);
                if (fn is null) return;
                if (Journal is not null && DescribeCandidates is not null)
                {
                    Log($"spawn_event class={cls.Name} event={evt} begin candidates={DescribeCandidates(fn)}");
                    if (evt == "PostBeginPlay")
                    {
                        UFunction timer = interp.FindEventFunction(actor, "Timer");
                        if (timer is not null) Log($"spawn_event class={cls.Name} Timer candidates={DescribeCandidates(timer)}");
                    }
                }
                Guard(() => interp.Invoke(actor, fn), evt);
                Log($"spawn_event class={cls.Name} event={evt} end");
            }

            // Shipped event order (Engine.dll.c:20260-20291): Spawned,
            // PreBeginPlay, BeginPlay, THEN the real zone via SetActorZone,
            // then PostBeginPlay and SetInitialState.
            // Virtual InitExecution — before the first event (20258).
            InitExecutionActor(actor);
            // AActor::Spawned (vtable +0x84, 20259) — between InitExecution (+0x38)
            // and the Spawned event: CT3 -> ValidateActor.
            ValidateActorCollision(actor, postLoad: false);
            RunEvent("Spawned");
            RunEvent("PreBeginPlay");
            RunEvent("BeginPlay");
            // bDeleteMe after BeginPlay (+0x28 bit 0x200, `test ah,2; jne 0x103a665b`, 0x103a6bee..0x103a6bf4): return
            // NULL at once — no zone, no PostBeginPlay, no encroachment check.
            if (actor.Destroyed) return null;
            // Shipped SpawnActor: SetActorZone(Actor, iTest, bForceRefresh=1) — it reads the live Location, which
            // Spawned/PreBeginPlay/BeginPlay may have changed (SetLocation).
            SetActorZone(interp, actor, UEInterpreter.AsVector3(actor.GetProp(PK.Location)), bTest: false, bForceRefresh: true);
            Log($"spawn_zone class={cls.Name} region={actor.GetDeclared("Actor", "Region")}");
            RunEvent("PostBeginPlay");
            // Shipped re-checks encroachment AFTER PostBeginPlay (vtable 0xbc,
            // Engine.dll.c:20272-20290; bTouchActors=0 per asm 0x103a6c57) and
            // destroys the spawn on a hit. Disasm 0x103a6c2b..0x103a6c30: the whole block
            // (hash remove, CheckEncroachment, hash re-add) is skipped when bNoCollisionFail
            // (param_13, [ebp+0x34]) is set. The test reads the LIVE Location [esi+0xfc] and
            // Rotation [esi+0x108] (0x103a6c5e..0x103a6c8f): PostBeginPlay may have moved the
            // actor. There is no bDeleteMe test between PostBeginPlay and the call: an actor
            // PostBeginPlay destroyed is still checked (FCollisionHash Remove/AddActor exit on
            // bDeleteMe, CheckEncroachment 0x103ab620 does not test it, DestroyActor
            // FUN_103a72c0 returns 1 at once for bDeleteMe).
            // Core does the encroachment check ITSELF: its own CheckEncroachment walks its own
            // collision hash in shipped candidate order.
            if (!noCollisionFail)
            {
                // The actor leaves the hash for its own encroachment test
                // (shipped 20275) so it cannot appear among its own candidates.
                if (UEInterpreter.ToBool(actor.GetProp(PK.bCollideActors))) HashRemoveActor(actor);
                if (CheckEncroachment(interp, actor, UEInterpreter.AsVector3(actor.GetProp(PK.Location)),
                        UEInterpreter.AsRotator(actor.GetProp(PK.Rotation)), bTouchActors: false))
                {
                    DestroyActor(interp, actor);
                    return null;
                }
                if (UEInterpreter.ToBool(actor.GetProp(PK.bCollideActors))) HashAddActor(actor);
            }
            RunEvent("SetInitialState");
            // Base-less colliding Decoration/Inventory/Pawn spawns probe for a
            // base when their physics is None or Rotating (Engine.dll.c:
            // 20169-20172, 20292-20306). No bDeleteMe test after PostBeginPlay (0x103a6cce..0x103a6d70): an actor
            // PostBeginPlay destroyed still gets the base probe from its live Base/bCollideWorld/Physics, and
            // SpawnActor returns its (deleted) pointer — script sees a non-None reference with bDeleteMe set.
            if (actor.GetProp(PK.Base) is null
                && UEInterpreter.ToBool(actor.GetProp(PK.bCollideWorld))
                && (UEInterpreter.ClassIsA(actor.Class, "Decoration")
                    || UEInterpreter.ClassIsA(actor.Class, "Inventory")
                    || UEInterpreter.ClassIsA(actor.Class, "Pawn")))
            {
                int spawnPhysics = UEInterpreter.ToInt(actor.GetProp(PK.Physics));
                if (spawnPhysics == 0 || spawnPhysics == 5)
                    FindBase(interp, actor);
            }
            // execSpawn: `if (Spawned) Spawned->Tag = SpawnTag` (Engine.dll.c:8582).
            // tag == null — not called from execSpawn (engine spawn), no tag.
            if (tag is not null) actor.Props["Tag"] = new UEName(tag);
            return actor;
        }
    }
}
