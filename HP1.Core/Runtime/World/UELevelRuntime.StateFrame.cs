// AActor::ProcessState — the state slot of the tick skeleton, Core-owned (single body
// for all frontends). Port of shipped AActor::ProcessState (Engine.dll.c:45801).
using System;
using UELib.Core;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        // Diagnostic probe for movement-latent completion: (interp, actor, kind).
        // An observation, not a decision; a frontend may print a journal line
        // from it. Optional; Core does not depend on it.
        public Action<UEInterpreter, UEObject, string> LatentCompletedProbe;

        // One-time warning about an UNPORTED member of the shipped gate.
        bool _simulatedStateRoleWarned;

        // ── AActor::ProcessState (Engine.dll.c:45801-45855) ──────────────────
        //
        // Shipped entry condition:
        //   StateFrame != NULL                          (45824: this+0xc)
        //   && StateFrame->Code != NULL                 (45824: frame+0xc)
        //   && (Role > 3 || (frame->StateNode->StateFlags & 4))   (45825-45826)
        //   && vtable[0x2c]() == 0                      (45826)
        // then GNatives[frame->LatentAction](frame, &DeltaSeconds) — poll of the
        // active latent — and the token execution loop.
        //
        // Role > 3 is ROLE_Authority(4); the second member is STATE_Simulated
        // (StateFlags & 4, UELib StateFlags.Simulated=0x4), which lets a
        // simulated state run at ANY role.
        //
        // vtable[0x2c] IDENTIFIED:
        //   AActor::IsPendingKill (Engine.dll.c:53480-53486, ordinal 1489,
        //   ?IsPendingKill@AActor@@UAEHXZ), body —
        //       return *(uint *)(this + 0x28) >> 9 & 1;
        //   i.e. bit 0x200 of ActorFlags(+0x28).
        // Who sets it (checked): ULevel::DestroyActor (253087) clears the actor's
        // slot in Level.Actors and IMMEDIATELY sets
        //       *(uint *)(actor + 0x28) |= 0x200;
        // before ConditionalDestroy. The port uses the same point: DestroyActor
        // sets `Destroyed` and the script-visible `bDeleteMe`
        // (see DestroyActor).
        //
        // So the entry gate reads literally: a destroyed actor's state does NOT
        // run — no latent poll, not a single token.
        public void ProcessState(UEInterpreter interp, UEObject actor, float dt, int role)
        {
            UEFrame frame = actor.StateFrame;
            if (frame?.Func is null) return;
            // StateFrame->Code != NULL (45824, frame+0xc). A latent set on a frame
            // whose code has STOPPED (SpellLearnTrigger.Template:
            // `Player.TurnToward(TempActor)` — execTurnToward writes
            // LatentAction into Harry's frame, whose SpellLearning state has already
            // executed `stop`) is not polled at all in shipped.
            if (frame.Tokens is null || frame.Cursor >= frame.Tokens.Count) return;
            // (Role > 3 || STATE_Simulated). HasFlags compares the RAW value,
            // as shipped compares the byte frame->StateNode+0x80 with 4.
            // The flag is read from frame->StateNode (+0x1c, 45825), not Node: a frame
            // running a super-state's label has Node = that ancestor. StateNode = the
            // state found by name (GotoState), the class when there is none.
            if (role <= 3)
            {
                WarnLowRoleStateFrame(actor, role);
                UState stateNode = string.IsNullOrEmpty(actor.CurrentStateName)
                    ? actor.Class : interp.FindState(actor.Class, actor.CurrentStateName);
                if (!(stateNode is not null && stateNode.StateFlags.HasFlags(0x00000004U)))
                    return;
            }
            // IsPendingKill() == 0 is the LAST member of the shipped condition, and the
            // order is kept: the low-role warning above belongs to its own member and
            // fires at the same point as in shipped.
            if (actor.Destroyed) return;

            // StateNode baseline of the 5-change cap — BEFORE the poll (0x1040ef77).
            string stateNodeBeforePoll = actor.CurrentStateName;
            LatentRunState polled = frame.LatentState;
            switch (polled)
            {
                case LatentRunState.Continue:
                    break;
                case LatentRunState.Sleep:
                    // NActor::Sleep — the remaining time runs on the actor's OWN tick,
                    // so pause and tick gates are exact without a separate condition.
                    // execPollSleep (Engine.dll.c:66982-66986): LatentFloat -= dt;
                    // sleep ends when the remainder < 0.5·dt — i.e. on the tick
                    // nearest to the deadline, not when the remainder reaches zero.
                    frame.SleepTimeLeft -= dt;
                    if (!(frame.SleepTimeLeft < dt * 0.5f)) goto PollPending;
                    break;
                case LatentRunState.FinishAnim:
                    // execPollFinishAnim (Engine.dll.c:61024-61033): the latent is
                    // ended by the bAnimFinished bit — set by UpdateAnimation
                    // at tween end, end of a non-looping animation, or a loop
                    // wrap while waiting. Core owns the bit (TickAnimation), so
                    // there is no player to ask.
                    if (!UEInterpreter.ToBool(actor.GetProp(PK.bAnimFinished))) goto PollPending;
                    break;
                case LatentRunState.FinishInterpolation:
                    // execPollFinishInterpolation (Engine.dll.c:143153): wait until
                    // the physMovingBrush integrator clears bInterpolating.
                    if (UEInterpreter.ToBool(actor.GetProp(PK.bInterpolating))) goto PollPending;
                    break;
                case LatentRunState.MoveTo:
                    // execPollMoveTo (Engine.dll.c:38490) — only steering and the
                    // arrival check; the movement itself is done by the physics slot.
                    if (!UEPawnMovement.PollMoveTo(actor, interp)) goto PollPending;
                    LatentCompletedProbe?.Invoke(interp, actor, "MoveTo");
                    break;
                case LatentRunState.MoveToward:
                    // execPollMoveToward (Engine.dll.c:88839).
                    if (!UEPawnMovement.PollMoveToward(interp, actor)) goto PollPending;
                    LatentCompletedProbe?.Invoke(interp, actor, "MoveToward");
                    break;
                case LatentRunState.TurnTo:
                    // execPollTurnTo (Engine.dll.c:48528): DesiredRotation plus
                    // the shipped completion band ±100.
                    if (!UEPawnMovement.PollTurnTo(actor)) goto PollPending;
                    break;
                case LatentRunState.TurnToward:
                    // execPollTurnToward (Engine.dll.c:53949). The turn itself happens
                    // ONLY in physicsRotation, at the tail of performPhysics.
                    if (!UEPawnMovement.PollTurnToward(actor)) goto PollPending;
                    break;
                case LatentRunState.Stop:
                    return;
                default:
                    // A latent whose poll is not ported
                    // (StrafeTo/StrafeFacing/WaitForLanding). Silently switching the
                    // frame to Stop would silently kill the actor's state.
                    throw new NotSupportedException(
                        $"latent poll not ported: {frame.LatentState} actor="
                        + actor.GetProp(PK.Name) + " state=" + actor.CurrentStateName);
            }
            // A completed poll clears its own LatentAction — unless an event it fired
            // already moved the frame (GotoState: LatentAction = 0, then GotoLabel or
            // Code = NULL), which then stands as written.
            if (frame.LatentState == polled) frame.LatentState = LatentRunState.Continue;
        PollPending:
            // Loop entry (0x1040efba): bDeleteMe, Code != NULL, LatentAction == 0. A
            // GotoState+GotoLabel fired from inside a pending poll runs the new state's
            // code in THIS ProcessState, not a tick later.
            if (frame.LatentState != LatentRunState.Continue
                || frame.Tokens is null || frame.Cursor >= frame.Tokens.Count) return;
            interp.Run(frame, stateNodeBeforePoll);
            // execStop (EX_Stop) clears Code: the state has ended but the FRAME
            // stays — with its probe mask and latent (shipped does not delete StateFrame
            // until the object is destroyed; InitExecution is the only one that
            // creates it). In the port Code == NULL is a position past the end of code.
            if (frame.LatentState == LatentRunState.Stop && frame.Tokens is not null)
                frame.Cursor = frame.Tokens.Count;
        }

        // Role < 4 on an actor with a live state frame means the second member
        // of the shipped condition (STATE_Simulated) has started to decide something.
        // In HP1 no class except Actor sets Role (default 4) and no script writes
        // it — if that ever changes, we must hear about it rather than find out
        // from diverging journals.
        void WarnLowRoleStateFrame(UEObject actor, int role)
        {
            if (_simulatedStateRoleWarned) return;
            _simulatedStateRoleWarned = true;
            HP1.Core.UETrace.Warn($"[Core] ProcessState: Role={role} (<4) with a live state "
                + $"frame on {actor.GetProp(PK.Name)} — entry is now decided by "
                + "STATE_Simulated (shipped 45825); earlier ports lacked this member");
        }
    }
}
