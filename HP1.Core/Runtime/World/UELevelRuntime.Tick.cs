// The per-tick actor pass (ULevel::Tick actor loop): TickActorsPass, TickActor/TickActorBody
// (events -> state -> Timer -> LifeSpan -> physics), the actor timer, the non-authority role branch,
// the particle FX tick hooks and guarded script invocation. Fields live in UELevelRuntime.cs.
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
        public void TickActorsPass(UEInterpreter interp, float levelDelta,
            Action<Exception> exceptionSink = null)
        {
            long passStart = ProfileTicks ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            UEWorldFrameProducer.AllocNote("between");
            // Pause comes from LevelInfo.Pauser, as in shipped (262651), and only from here:
            // there is no parameter, because a parameter would be a second owner of the decision.
            bool paused = !string.IsNullOrEmpty(
                interp.GetLevelInfo()?.GetProp(PK.Pauser)?.ToString());
            bool playersOnly = UEInterpreter.ToBool(interp.GetLevelInfo()?.GetProp(PK.bPlayersOnly));
            // The bPlayersOnly gate below is the port's model, not a shipped contract:
            // shipped only turns the flag into TickType = 1 (Engine.dll.c:262645) and cuts off
            // inside AActor::Tick (LAB_103b41e5, :128546); what runs before that point with
            // TickType == 1 needs the disassembly. The only source is a cheat command; if the
            // flag comes alive, say so loudly.
            if (playersOnly && !_playersOnlyWarned)
            {
                _playersOnlyWarned = true;
                HP1.Core.UETrace.Warn("[Core] bPlayersOnly ACTIVE: per-actor gate is the "
                    + "SE model (PlayerPawn||bAlwaysTick); the shipped bit identity "
                    + "(XLevel+0x111c / flag2&2) is UNRESOLVED (§8)");
            }
            // Pass sets are reused: a fresh HashSet per tick regrew to ~2k actors
            // (array garbage on every growth).
            // The game tick is outside the render window (appEnableFastMath(0)); reset in case
            // an exception cut the previous frame short before URender::PostRender.
            UEWorldFrameProducer.FastMathWindow = false;
            var visiting = _passVisiting; visiting.Clear();
            // FATAL CONSEQUENCES ARE THE SAME FOR BOTH ERROR SLOTS: the paused-input slot and
            // the full tick share one policy (including SweepDestroyed, so an actor destroyed
            // before the exception does not stay in the list). The selftest throw case proves
            // the shared consequence runs.
            void FatalStop(Exception e)
            {
                exceptionSink(e);
                visiting.Clear();
                SweepDestroyed();
            }
            // The pass starts at iFirstDynamicActor (ULevel+0x110, Engine.dll.c:
            // 262665 and paused branch 262700): static actors that LoadMap gathered at the
            // head of the array (SortActorsForPlay) do not tick.
            for (int i = Math.Min(FirstDynamicActor, Actors.Count); i < Actors.Count; i++)
            {
                UEObject actor = Actors[i];
                if (actor is null || actor.Destroyed) continue;
                if (paused)
                {
                    // ── SHIPPED PAUSED-INPUT (Engine.dll.c:262703-262745) ────
                    //
                    // With a non-empty Pauser the local PlayerPawn (IsA(APlayerPawn)
                    // and Player != null, +0x4bc) STILL gets:
                    //   Player->ReadInput(dt)  (vtable+0x58 -> UViewport::ReadInput: UpdateInput(0), UInput::ReadInput),
                    //   then FindFunctionChecked(PlayerInput) + a direct call,
                    //   then a reset of ALL CPF_Input floats (ReadInput(-1)) —
                    // and NOTHING else: no PlayerTick, no state frame.
                    //
                    // The third step is resolved by disasm 0x103b72de-0x103b7385 (the decompile
                    // alone is not enough): class and parent fields -> UFloatProperty ->
                    // PropertyFlags & CPF_Input -> write 0.0f at the offset — exactly
                    // ReadInput(-1)/ResetPlayerInputAxes, a metadata reset of the axes.
                    //
                    // DIRECT DISPATCH: between ReadInput and the PlayerInput call the shipped
                    // fragment has NO check of its own (FindFunctionChecked + vtable call). The
                    // only gate is IsProbing inside the call, and PlayerInput is outside the
                    // probe range 0xc5..0x104: script Disable('PlayerInput') is a no-op
                    // (execDisable), nothing can suppress the event.
                    //
                    // Input is read here in the paused branch and at the player's slot of the actor tick otherwise —
                    // never by a frontend.
                    if (UEInterpreter.ClassIsA(actor.Class, "PlayerPawn")
                        && actor.GetProp(PK.Player) is not null)
                    {
                        UEObject pausedPawn = actor;
                        void PausedInput()
                        {
                            Input.ViewportReadInput(interp, ViewportConsole, pausedPawn, levelDelta);
                            UFunction inputFn = interp.FindEventFunction(pausedPawn, "PlayerInput");
                            // Direct call, as in the disasm: NO gate between
                            // ReadInput and PlayerInput.
                            if (inputFn is not null) interp.Invoke(pausedPawn, inputFn, levelDelta);
                            ResetPlayerInputAxes(pausedPawn);
                        }
                        if (exceptionSink is not null)
                        {
                            try { PausedInput(); }
                            catch (Exception e) { FatalStop(e); return; }
                        }
                        else PausedInput();
                    }
                    // The full tick is ONLY bAlwaysTick (262743-262745), and for that
                    // same actor it runs AFTER paused-input, as in shipped.
                    if (!UEInterpreter.ToBool(actor.GetProp(PK.bAlwaysTick))) continue;
                }
                else if (playersOnly
                    && !UEInterpreter.ToBool(actor.GetProp(PK.bAlwaysTick))
                    && !UEInterpreter.ClassIsA(actor.Class, "PlayerPawn")) continue;
                long t0 = ProfileTicks ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                if (exceptionSink is not null)
                {
                    // IMMEDIATE STOP: the policy must match headless, where such an
                    // exception ends the run, so the pass is aborted on the spot instead of
                    // letting later actors keep changing a world already declared stopped.
                    try { TickActor(interp, actor, levelDelta, visiting); }
                    catch (Exception e) { FatalStop(e); return; }
                }
                else TickActor(interp, actor, levelDelta, visiting);
                if (ProfileTicks)
                {
                    string cls = actor.Class?.Name?.ToString() ?? "?";
                    long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                    ProfileByClass[cls] = ProfileByClass.TryGetValue(cls, out var acc) ? (acc.ticks + dt, acc.calls + 1) : (dt, 1);
                }
            }
            // Finished transient animation channels are removed by the owner's full ApplyAnim
            // (UELevelRuntime.ApplyAnim; drawing — GetFrame), not by the tick pass.
            // Tail of ULevel::Tick (FUN_103b74cc, 0x103b74cc..0x103b7536):
            // Ticked = !Ticked (0x103b74cc..0x103b74db), before CleanupDestroyed(0) — CompactActors (SweepDestroyed), then if
            // FirstDeleted > 127 (Engine.dll.c:23760 `0x7f < count`) references of live
            // actors to destroyed ones are nulled and the list is freed.
            LevelTicked = !LevelTicked;
            SweepDestroyed();
            if (FirstDeletedCount > 0x7f)
            {
                CleanupDestroyed();
                FirstDeletedCount = 0;
            }
            if (ProfileTicks) ProfileActorsTicks += System.Diagnostics.Stopwatch.GetTimestamp() - passStart;
            UEWorldFrameProducer.AllocNote("actors");
            // ── PlayerPawn.ViewFlash — HERE, BECAUSE SHIPPED CALLS IT HERE ──
            //
            // The call site was read from the image. The eventViewFlash wrapper
            // (0x103242c0) is indeed called by nobody — the compiler INLINED it. The real
            // call is visible only through the FName global reference:
            //   Engine.dll 0x103a1946  mov  eax, [0x105e9d34]   ; FName ViewFlash
            //              0x103a194b  push eax
            //              0x103a194c  mov  ecx, esi            ; Viewports(0)->Actor
            //              0x103a194e  call [FindFunctionChecked] (Core.dll)
            // with parameter `fld [ebp+8]` (0x103a1931) — the function's own DeltaSeconds.
            // The function is 0x103a0900 = UGameEngine::Tick(float) (Engine.dll.c:109914
            // calls it from the UGameEngine::Tick stub), and ViewFlash is the ONLY inlined
            // event dispatch in its whole body (0x103a0900..0x103a1d60, 5216 bytes).
            // 0x105e9d34 is ViewFlash per the name registrar: string 0x104d6654 = "ViewFlash"
            // (neighbours 0x104d6644 "DoJump", 0x104d666c "Mount").
            //
            // ORDER: the ULevel::Tick calls (slot 0x60) are at 0x103a0b0f..0x103a0b54, i.e.
            // BEFORE 0x103a194e. So the event runs AFTER the full actor pass — here, not inside
            // the player tick — exactly once per engine frame and only on the viewport player.
            //
            // DELTA: shipped passes ITS OWN DeltaSeconds parameter, the same one that goes to
            // ULevel::Tick; ULevel applies time dilation internally, to actors. Here it is
            // `levelDelta` — the same argument.
            //
            // The REAL event bytecode runs (no hand copy of the formula).
            if (LocalPlayer is UEObject vfPlayer && !vfPlayer.Destroyed)
            {
                UFunction viewFlashFn = interp.FindEventFunction(vfPlayer, "ViewFlash");
                if (viewFlashFn is not null)
                {
                    if (exceptionSink is not null)
                    {
                        try { interp.Invoke(vfPlayer, viewFlashFn, levelDelta); }
                        catch (Exception e) { FatalStop(e); return; }
                    }
                    else interp.Invoke(vfPlayer, viewFlashFn, levelDelta);
                }
            }
        }

        public void TickActor(UEInterpreter interp, UEObject actor, float dt,
            HashSet<UEObject> visiting)
        {
            if (actor is null || actor.Destroyed) return;
            if (!visiting.Add(actor))
                throw new InvalidDataException("Owner cycle at " + actor.GetProp(PK.Name));
            if (actor.GetProp(PK.Owner) is UEObject owner && !owner.Destroyed)
            {
                // Shipped AActor::Tick calls Owner->Tick() DIRECTLY
                // (Engine.dll.c:128329-128331) — no array-membership test; the
                // owner's own bTicked flag prevents a double tick. An owner
                // outside ULevel.Actors still ticks here exactly like shipped,
                // but is a modelling gap worth one loud line (headless never hits this).
                // Membership through ActorsById (kept with Actors in the same four places) instead of a linear
                // Actors scan per owned actor per tick; the test only gates this diagnostic line.
                if (!(ActorsById.TryGetValue(owner.ObjectId, out UEObject listed) && ReferenceEquals(listed, owner))
                    && _ownersOutsideWarned.Add(owner))
                    HP1.Core.UETrace.Warn(
                        $"[Core] owner outside ULevel.Actors ticks owner-first "
                        + $"(shipped 128329): owner class={owner.Class?.Name} "
                        + $"of actor={actor.GetProp(PK.Name)}");
                TickActor(interp, owner, dt, visiting);
            }
            visiting.Remove(actor);
            if (actor.Destroyed) return;
            if (UEInterpreter.ToBool(actor.GetProp(PK.bTicked)) == LevelTicked) return;
            actor.Props.SetUnversioned(PK.bTicked, LevelTicked ? BoxTrue : BoxFalse);   // not a projected value (UEProps.Version)
            TickActorBody(interp, actor, dt);
            // AWind::Tick: wind body after AActor::Tick (Engine.dll.c:100425).
            if (!actor.Destroyed && UEInterpreter.ClassIsA(actor.Class, "Wind")) TickWind(actor, dt);
        }

        // The shipped AActor::Tick body order (Engine.dll.c:128667-128855):
        // particle prelude → animation → Tick/PlayerInput+PlayerTick events →
        // ProcessState → Timer → LifeSpan → performPhysics → root motion.
        // An expiring actor never runs physics that tick.
        public void TickActorBody(UEInterpreter interp, UEObject actor, float dt)
        {
            if (DetailedJournal)
                Log($"tick_actor name={actor.GetProp(PK.Name)} class={actor.Class?.Name} begin");
            // AParticleFX::Tick prelude (Engine.dll.c:14138) — the shipped
            // virtual runs its Age/budget-destroy body and then falls into
            // AActor::Tick; it consumes NO RNG (emission is render-side).
            if (FrameProducer is not null && UEInterpreter.ClassIsA(actor.Class, "ParticleFX"))
                TickParticleFxOf(interp, actor, dt);
            if (actor.Destroyed) return;
            // LOGICAL ANIMATION TICKS UNCONDITIONALLY: AnimEnd, the notify scan and frame
            // advance are Core's and must not depend on a frontend setting a hook.
            // The hook is only the VISUAL half of the same slot and runs AFTER the logic.
            TickAnimation(interp, actor, dt);
            if (actor.Destroyed) return;
            AnimationHook?.Invoke(interp, actor, dt);

            int role = UEInterpreter.ToInt(actor.GetProp(PK.Role));
            // ── ROLE BRANCHES OF AActor::Tick (LAB_103b3d7a, disasm 0x103b3d7a..
            // 0x103b4356) ─────────────────────────────────────────────────────
            // The fork is AFTER animation and BEFORE events/timer/physics:
            //   Pawn && Role == ROLE_SimulatedProxy (flag [ebp-0x24], 0x103b3990)
            //     -> simulated pawn branch 0x103b3d87..0x103b3f80;
            //   else RemoteRole == ROLE_AutonomousProxy (0x103b3f85: `cmp byte
            //     [esi+0x32], 3`) -> with Role == 4 a ViewTarget copy and ONLY
            //     Timer, with another role — nothing (0x103b3f93 -> 0x103b4356);
            //   else Role < ROLE_SimulatedProxy (0x103b40c6: `cmp al, 2; jb
            //     0x103b433e`) -> ONLY performPhysics and ONLY when Physics ==
            //     PHYS_Falling (0x103b433e: `cmp byte [esi+0x30], 2`);
            //   else — the common branch below (events, state, Timer, LifeSpan,
            //     physics).
            // The tail (pawn eyes/weapon, counters, root motion) is common to all
            // branches — shipped converges at 0x103b4356.
            if (role == 2 && UEInterpreter.ClassIsA(actor.Class, "Pawn"))
                WarnSimulatedPawnBranch(actor);
            else if (UEInterpreter.ToInt(actor.GetProp("RemoteRole")) == 3 || role < 2)
            {
                TickNonAuthorityRoleBranch(interp, actor, dt, role);
                goto roleBranchDone;
            }
            // The script event branch and the state slot are Core's, UNCONDITIONALLY;
            // frontends cannot replace them with their own driver.
            {
                bool localPlayer = role >= 2
                    && UEInterpreter.ClassIsA(actor.Class, "PlayerPawn")
                    && actor.GetProp(PK.Player) is not null;
                if (localPlayer)
                {
                    // Shipped AActor::Tick local-player branch (Engine.dll.c:
                    // 128667-128699, LAB_103b414a): Player->ReadInput(dt) (UViewport::ReadInput: UpdateInput(0), then
                    // UInput::ReadInput — Hold for held keys, ×20/dt), PlayerInput, then PlayerTick, then the state frame.
                    Input.ViewportReadInput(interp, ViewportConsole, actor, dt);
                    {
                        UFunction inputFn = interp.FindEventFunction(actor, "PlayerInput");
                        if (inputFn is not null)
                            GuardInvoke(interp, actor, inputFn, dt, "PlayerInput");
                    }
                    if (UEInterpreter.IsProbing(actor, "PlayerTick"))
                    {
                        UFunction playerTickFn = interp.FindEventFunction(actor, "PlayerTick");
                        if (playerTickFn is not null)
                            GuardInvoke(interp, actor, playerTickFn, dt, "PlayerTick");
                    }
                    // Shipped: Player->ReadInput(-1.0) right after PlayerTick
                    // (Engine.dll.c:128696) — zero every CPF_Input FLOAT
                    // property (metadata-driven, both frontends).
                    ResetPlayerInputAxes(actor);
                }
                // Early exit of shipped AActor::Tick (Engine.dll.c:128551): the probe mask
                // is checked BEFORE the function lookup. Same gate as in the central dispatch
                // (UELifecycle.CallEvent) — repeated here because the tick dispatches the
                // event directly, as the original does.
                else if (role >= 2 && UEInterpreter.IsProbing(actor, "Tick"))
                {
                    UFunction tickFn = interp.FindEventFunction(actor, "Tick");
                    // Empty Tick (HP1 Actor.uc) — nothing to execute; checked HERE to avoid
                    // paying the Guard closure + args array + dt boxing for each of ~2k
                    // actors per tick.
                    if (tickFn is not null && !UEInterpreter.IsEmptyScriptBody(tickFn))
                        GuardInvoke(interp, actor, tickFn, dt, "Tick");
                }
                // State slot is Core, unconditionally (stage 7D step 1.1).
                ProcessState(interp, actor, dt, role);
            }

            // Shipped order: ProcessState, then Timer, then LifeSpan, THEN
            // performPhysics (Engine.dll.c:128703-128730).
            TickActorTimer(interp, actor, dt);

            if (role >= 2 && !actor.Destroyed)
            {
                float lifeSpan = UEInterpreter.AsFloat(actor.GetProp(PK.LifeSpan));
                if (lifeSpan != 0f)
                {
                    actor.Props["LifeSpan"] = lifeSpan - dt;
                    if (lifeSpan - dt <= 0.0001f)
                    {
                        // Engine.dll.c:128719-128727: Expired, DestroyActor, return.
                        GuardEvent(interp, actor, "Expired");
                        DestroyActor(interp, actor);
                        Log($"tick_actor name={actor.GetProp(PK.Name)} expired");
                        return;
                    }
                }
            }

            int physics = UEInterpreter.ToInt(actor.GetProp(PK.Physics));
            // APawn::performPhysics saves the pre-step velocity and hands it
            // to physicsRotation for the banking dv/dt (Engine.dll.c:68640).
            // Shipped gates performPhysics on Physics!=None AND
            // Role!=ROLE_AutonomousProxy (128728: this[0x30]!=0 &&
            // this[0x31]!=3) with no bDeleteMe re-check after Timer; the hook
            // owns any further gating (the headless port keeps its
            // tail gate). Standalone never assigns Role 3, so
            // the second gate is latent in Tut1 — carried for the full port.
            Vector3 preStepVelocity = UEInterpreter.AsVector3(actor.GetProp(PK.Velocity));
            // The physics slot is Core's, UNCONDITIONALLY: Cut* and SpellLearning rely on the
            // ordinary shipped mechanism (see the comment at ProcessState in
            // UELevelRuntime.StateFrame.cs).
            if (physics != 0 && role != 3)
                PerformPhysics(interp, actor, physics, dt, preStepVelocity);
            roleBranchDone:

            // ── PAWN EYES AND WEAPON (disasm 0x103b4451..0x103b44f4) ─────────
            //
            // Ghidra dropped this part of AActor::Tick: the site was found only through
            // the global ENGINE_UpdateEyeHeight (0x105e9e30 read at 0x103b4479), and the
            // live original 1.53 sends the event on 38 maps.
            // Offsets checked against the shipped layout:
            //   Pawn+0x25c bit 2 bIsPlayer, Actor+0x31 Role, Pawn+0x260 bit 1
            //   bViewTarget, Pawn+0x33c ViewRotation, Actor+0x108 Rotation,
            //   Pawn+0x318 Weapon, Actor+0xfc Location.
            //     if (bIsPlayer && Role >= ROLE_AutonomousProxy)
            //         bViewTarget ? eventUpdateEyeHeight(DeltaSeconds)
            //                     : ViewRotation = Rotation;
            //     if (Weapon) XLevel->FarMoveActor(Weapon, Location, 0, 0);
            // Before this shipped runs the ViewTarget copy block ->
            // TargetViewRotation/TargetEyeHeight/TargetWeaponViewOffset
            // (0x103b4356..0x103b444e), but only with XLevel->NetDriver having a
            // ServerConnection (+0x88 -> +0x38) — a network client; single player never
            // enters it. Shipped has no bDeleteMe check between physics and this block;
            // a destroyed actor never receives the event due to the dispatcher gate,
            // as in ProcessEvent.
            if (UEInterpreter.ClassIsA(actor.Class, "Pawn"))
            {
                if (UEInterpreter.ToBool(actor.GetProp(PK.bIsPlayer)) && role >= 3)
                {
                    if (UEInterpreter.ToBool(actor.GetProp(PK.bViewTarget)))
                        UELifecycle.CallEvent(interp, actor, "UpdateEyeHeight", dt);
                    else
                        actor.Props["ViewRotation"] = actor.GetProp(PK.Rotation);
                }
                if (actor.GetProp(PK.Weapon) is UEObject weapon)
                    FarMove(interp, weapon, UEInterpreter.AsVector3(actor.GetProp(PK.Location)), false, false);
            }

            // ── PAWN COUNTERS (Engine.dll.c:128770-128808) ──────────────────
            //
            // Placed AFTER physics and BEFORE root motion, as in the original
            // (performPhysics 128728 → these counters → tail 128808). The branch runs only
            // for a PAWN and only with Role == ROLE_Authority; the original's second
            // condition (param_2 == LEVELTICK_All) holds by construction: the port has one
            // full tick.
            //
            // WHY: PainTime is pit death. The original chain: Pawn.FootZoneChange sets
            // PainTime = 0.01 when a foot enters a pain zone (and -1 on leaving) → the
            // counter below decreases and raises PainTimer when crossing 0.001 →
            // Pawn.PainTimer reads FootRegion.Zone.DamagePerSec and calls TakeDamage with the
            // zone's DamageType → Harry.TakeDamage on 'ZonePain' or 'pit' goes to
            // hit_InstantPitDeath (Harry.uc:157). Without the counter the player falls into the
            // Lev_Tut1b pit (TWO pain zones) and survives at the bottom.
            //
            // The neighbouring SpeechTime is the same counter of the same form (128797-128802).
            //
            // Block order — disasm 0x103b450e..0x103b4682: sight
            // (SightCounter/ShowSelf/CheckEnemyVisible, PawnSightTick) ->
            // PainTime -> SpeechTime -> UpdateTactics.
            if (!actor.Destroyed && role == 4 && UEInterpreter.ClassIsA(actor.Class, "Pawn"))
            {
                PawnSightTick(interp, actor, dt);
                PawnCountdown(interp, actor, dt, "PainTime", "PainTimer");
                PawnCountdown(interp, actor, dt, UEObject.PropKey(actor.Class, "Pawn", "SpeechTime"), "SpeechTimer");
                // 0x103b4658: bAdvancedTactics (+0x25c bit 0x80000000) ->
                // eventUpdateTactics(DeltaSeconds) (FName 0x105e9d54). In the data
                // in the shipped data nobody sets the flag — the form is from the source.
                if (UEInterpreter.ToBool(actor.GetDeclared("Pawn", "bAdvancedTactics")))
                    UELifecycle.CallEvent(interp, actor, "UpdateTactics", dt);
            }

            // Shipped AActor::Tick tail (Engine.dll.c:128808-128855): authority
            // actors with bAnimMove sweep their skeletal root delta.
            if (!actor.Destroyed && role == 4
                && UEInterpreter.ToBool(actor.GetProp(PK.bAnimMove))
                && actor.GetProp(PK.Mesh) is not null)
                // Root motion is Core's (UELevelRuntime.RootMotion.cs); the pose player never
                // moves the actor through foreign collision.
                TickRootMovement(interp, actor);

            // Per-address playthrough telemetry — an observer, writes nothing into the
            // simulation (UETelemetry; enabled only together with the journal).
            if (HP1.Core.UEPlaythroughLog.Enabled && UETelemetry.Active)
                UETelemetry.AfterActorTick(this, interp, actor, dt);

            if (DetailedJournal)
                Log($"tick_actor name={actor.GetProp(PK.Name)} end");
        }

        // Actor Timer — shared by the common branch (Engine.dll.c:128703-128717) and the
        // RemoteRole == ROLE_AutonomousProxy branch (LAB_103b4054, disasm
        // 0x103b4054..0x103b40c1 — the same body: `fadd TimerCounter; fcom
        // TimerRate`, ROUND, bTimerLoop bit 0x4000 of +0x28, Timer event).
        void TickActorTimer(UEInterpreter interp, UEObject actor, float dt)
        {
            if (actor.TimerRate > 0f && !actor.Destroyed)
            {
                // Shipped fires the event ONCE per tick and subtracts
                // ROUND(counter/rate)*rate (128704-128717) — the counter
                // can go negative on a >1.5x overshoot; never a loop.
                actor.TimerCounter = TimerStep(actor.TimerCounter, actor.TimerRate, dt, out bool fired, out int n);
                if (fired)
                {
                    if (n != 0 && !actor.TimerLoop) actor.TimerRate = 0f;
                    GuardEvent(interp, actor, "Timer");
                }
            }
        }

        // AActor::Tick Timer, 0x103b4260..0x103b428d (and 0x103b4068..0x103b4097): s = dt + TimerCounter in the register,
        // stored as float (fst); it fires when the UNROUNDED s >= TimerRate (fcom before the store); n = fistp of
        // float(s / TimerRate) (the register s; nearest even; out of int range 0x80000000); TimerCounter =
        // float(float(s) − n·TimerRate), fild·fmul·fsubr with one store.
        internal static float TimerStep(float counter, float rate, float dt, out bool fired, out int n)
        {
            n = 0;
            static int Fistp(double q)
            {
                double rq = Math.Round(q, MidpointRounding.ToEven);
                return rq >= -2147483648.0 && rq < 2147483648.0 ? (int)rq : int.MinValue;
            }
            if (!float.IsFinite(counter) || !float.IsFinite(rate) || !float.IsFinite(dt))
            {
                float s0 = counter + dt;
                fired = rate <= s0;
                if (!fired) return s0;
                n = Fistp(s0 / rate);
                return (float)(s0 - (double)n * rate);
            }
            static HP1.Core.X87Ext X(float v) => HP1.Core.X87Ext.From(v);
            static HP1.Core.X87Ext R(HP1.Core.X87Ext v) => HP1.Core.X87Ext.Round64(v);
            var s = R(X(dt) + X(counter));
            float sf = s.ToFloat();
            fired = s.CompareTo(X(rate)) >= 0;
            if (!fired) return sf;
            n = Fistp(HP1.Core.X87Ext.Div64(s, X(rate)).ToFloat());
            return R(X(sf) - HP1.Core.X87Ext.From(n) * X(rate)).ToFloat();
        }

        // The two role branches of AActor::Tick that bypass the common one (see the fork in
        // TickActorBody). No Tick/PlayerTick events, no state frame and no LifeSpan here —
        // shipped has none in these branches.
        void TickNonAuthorityRoleBranch(UEInterpreter interp, UEObject actor, float dt, int role)
        {
            if (UEInterpreter.ToInt(actor.GetProp("RemoteRole")) == 3)
            {
                // 0x103b3f93: `cmp al, 4; jne 0x103b4356` — not Authority: nothing.
                if (role != 4) return;
                // 0x103b3f9b..0x103b4051 (Engine.dll.c:128617-128633): a PlayerPawn-class pawn
                // with a ViewTarget pawn copies its view:
                //   TargetViewRotation (+0x7c8) = ViewTarget.ViewRotation (+0x33c),
                //   TargetEyeHeight (+0x7d4)    = ViewTarget.EyeHeight (+0x358),
                //   TargetWeaponViewOffset (+0x7d8) = ViewTarget.Weapon
                //     .PlayerViewOffset (+0x318 -> +0x290), if there is a weapon.
                if (UEInterpreter.ClassIsA(actor.Class, "PlayerPawn")
                    && actor.GetProp(PK.ViewTarget) is UEObject viewTarget
                    && UEInterpreter.ClassIsA(viewTarget.Class, "Pawn"))
                {
                    actor.Props["TargetViewRotation"] = viewTarget.GetProp("ViewRotation");
                    actor.Props["TargetEyeHeight"] = viewTarget.GetProp("EyeHeight");
                    if (viewTarget.GetProp(PK.Weapon) is UEObject weapon)
                        actor.Props["TargetWeaponViewOffset"] = weapon.GetProp("PlayerViewOffset");
                }
                TickActorTimer(interp, actor, dt);   // LAB_103b4054
                return;
            }
            // Role < 2: 0x103b433e `cmp byte [esi+0x30], 2; jne` -> performPhysics
            // (vtable +0xa8, 0x103b434c) only with PHYS_Falling.
            if (UEInterpreter.ToInt(actor.GetProp(PK.Physics)) == 2)
                PerformPhysics(interp, actor, 2, dt, UEInterpreter.AsVector3(actor.GetProp(PK.Velocity)));
        }
        void WarnSimulatedPawnBranch(UEObject actor)
        {
            if (_simulatedPawnWarned.Add(actor))
                HP1.Core.UETrace.Warn(
                    $"[Core] STOP: Pawn with Role=ROLE_SimulatedProxy ticks through the common branch — "
                    + $"shipped body 0x103b3d87..0x103b3f80 not ported: actor={actor.GetProp(PK.Name)} "
                    + $"class={actor.Class?.Name}");
        }

        // Closures live in separate methods: capturing (interp, actor, dt) in the body of
        // TickActorBody made the compiler allocate on EVERY call, even when the lambda
        // did not run (~40 KB of garbage per tick).
        void TickParticleFxOf(UEInterpreter interp, UEObject actor, float dt)
        {
            ParticleTickTap?.Invoke(this, actor, dt, true);
            if (!ReferenceEquals(_fxDestroyInterp, interp)) { _fxDestroyInterp = interp; _fxDestroy = DestroyFxOf(interp); }
            FrameProducer.TickParticleFx(actor, dt, _fxDestroy);
            ParticleTickTap?.Invoke(this, actor, dt, false);
        }
        Action<UEObject> DestroyFxOf(UEInterpreter interp) => a => DestroyActor(interp, a);   // own method: no closure on entry
        // Default policy is to just run the body, so no delegate. The lambda lives in a
        // separate method: capturing parameters made the compiler allocate a closure object
        // on entry of EVERY call, even on the fast unguarded branch.
        void GuardInvoke(UEInterpreter interp, UEObject actor, UFunction fn, float dt, string what)
        {
            if (ReferenceEquals(_guard, DefaultGuard))
            {
                // Argument array from the call pool (UEVmPool): Invoke keeps no reference past its return.
                object[] a = interp.Pool.RentArgs(1);
                a[0] = dt;
                interp.Invoke(actor, fn, a);
                interp.Pool.ReleaseArgs(a);
            }
            else GuardInvokeGuarded(interp, actor, fn, dt, what);
        }
        void GuardInvokeGuarded(UEInterpreter interp, UEObject actor, UFunction fn, float dt, string what)
            => Guard(() => interp.Invoke(actor, fn, dt), what);
        void GuardEvent(UEInterpreter interp, UEObject actor, string ev)
        {
            if (ReferenceEquals(_guard, DefaultGuard)) UELifecycle.CallEvent(interp, actor, ev);
            else GuardEventGuarded(interp, actor, ev);
        }
        void GuardEventGuarded(UEInterpreter interp, UEObject actor, string ev)
            => Guard(() => UELifecycle.CallEvent(interp, actor, ev), ev);

        // One form for both pawn counters (Engine.dll.c:128788-128794 for PainTime and
        // 128797-128802 for SpeechTime): while the value is STRICTLY positive, subtract
        // dt; if the result drops below 0.001, zero it and raise the event. Zeroing comes
        // BEFORE the event on purpose: the handler may recharge the counter
        // (Pawn.PainTimer sets PainTime = 2 in the drowning branch), and the original order
        // allows it.
        //
        // The event goes through the common dispatcher and thus passes the probe mask —
        // exactly like ProcessEvent in the original (PainTimer is probe bit 31).
        static void PawnCountdown(UEInterpreter interp, UEObject actor, float dt,
            string field, string evt)
        {
            float value = UEInterpreter.AsFloat(actor.GetProp(field));
            if (value <= 0f) return;
            float next = value - dt;
            actor.Props[field] = next;
            if (next >= 0.001f) return;
            actor.Props[field] = 0f;
            UELifecycle.CallEvent(interp, actor, evt);
        }
    }
}
