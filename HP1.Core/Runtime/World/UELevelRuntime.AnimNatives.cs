// Animation NATIVES — Core-owned.
//
// TickAnimation (UELevelRuntime.Animation.cs) reads the animation properties AnimSequence, AnimRate, AnimLast,
// AnimMinRate, bAnimLoop, bAnimNotify, bAnimFinished, TweenRate, TweenAlpha; the PlayAnim/LoopAnim/TweenAnim
// natives write them. With wrong defaults the loop misbehaves exactly where it matters: AnimLast=0 sends the
// first tick into the finish branch (AnimRate=0, bAnimFinished=true, AnimEnd), and bAnimNotify=false means the
// notify scan never runs ('Cast'). So the natives live here, next to the loop that reads them. The frontend only
// supplies the MESH CATALOG — rate, frame count, group and notify list of a sequence (AnimSequenceLookup); no
// gameplay decision is made there.
//
// Source: shipped AActor::PlayAnim (Engine.dll.c:73758-73810), its same-loop branch (73721-73757),
// execTweenAnim (151925-151945), execFinishAnim (110563-110573), execPollFinishAnim (61024),
// execIsAnimating (118145) / execGetAnimGroup (80095).
using System;
using HP1.Core;
using System.Collections.Generic;
using UELib.Core;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        // No AnimMoveRouteHook: the frontend learns that Core drives the root from the frame
        // (`ActorSnapshot.RootMotionDriven`) — that is the only route; do not add a hook for it.

        // ── native(259) PlayAnim(name Sequence, float Rate, float TweenTime,
        //    optional EAnimType Type, optional name RootBone) ────────────────
        public object PlayAnimNative(UEInterpreter interp, UEFrame frame, object[] args)
        {
            UEObject self = frame.Context ?? frame.Object;
            if (RefusesNoMesh(self)) return null;
            string name = AnimNameArg(args, 0);
            if (!PrepareBaseAnimationRoute(interp, args, typeIndex: 3, rootIndex: 4, ref self, name)) return null;
            bool found = ResolveAnimSequence(self, name, "PlayAnim",
                out UEAnimSequenceInfo sequence, out bool clear);
            // execPlayAnim defaults: Rate = 1.0 (0x10409e54), TweenTime = −1.0
            // (0x10409e78) — i.e. a 0.5 s tween (TweenRate = 2), not an instant one.
            float rate = AnimFloatArg(args, 1, 1f);
            float tweenTime = AnimFloatArg(args, 2, -1f);
            if (!found && !clear) return null;   // miss: shipped skips the block
            // The name 'None' GOES THROUGH THE WRITES. Shipped entry condition is
            // `(GetAnimSeq(name) != NULL) || (name == None)` (73714): for 'None' the
            // block runs with AnimLast=0 (73716-73718), i.e. the animation is STOPPED.
            SetAnimation(self, sequence, rate, tweenTime, minRate: 0f, loop: false);
            return null;
        }

        // ── native(260) LoopAnim(name Sequence, float Rate, float TweenTime,
        //    float MinRate, optional EAnimType Type, optional name RootBone) ──
        public object LoopAnimNative(UEInterpreter interp, UEFrame frame, object[] args)
        {
            UEObject self = frame.Context ?? frame.Object;
            if (RefusesNoMesh(self)) return null;
            string name = AnimNameArg(args, 0);
            if (!PrepareBaseAnimationRoute(interp, args, typeIndex: 4, rootIndex: 5, ref self, name)) return null;
            bool foundL = ResolveAnimSequence(self, name, "LoopAnim",
                out UEAnimSequenceInfo sequence, out bool clearL);
            if (!foundL && !clearL) return null; // miss: shipped skips the block
            if (sequence is null)
            {
                // Same branch as PlayAnim: the name 'None' clears the state.
                SetAnimation(self, null, AnimFloatArg(args, 1, 1f),
                    AnimFloatArg(args, 2, -1f), minRate: 0f, loop: true);
                return null;
            }
            // execLoopAnim defaults: Rate = 1.0, TweenTime = −1.0 (0x10409fb8),
            // MinRate = 0 (0x10409fdc).
            float rate = AnimFloatArg(args, 1, 1f);
            float tweenTime = AnimFloatArg(args, 2, -1f);
            float minRate = AnimFloatArg(args, 3, 0f);
            // AActor::PlayAnim same-sequence looping path (Engine.dll.c:73721-73757):
            // an already-looping same anim only refreshes AnimRate/AnimMinRate and
            // clears bAnimFinished — frame, tween state and AnimLast keep running.
            bool sameLoop = string.Equals(self.GetProp(PK.AnimSequence)?.ToString(), name,
                                StringComparison.OrdinalIgnoreCase)
                         && UEInterpreter.ToBool(self.GetProp(PK.bAnimLoop))
                         && (UEInterpreter.AsFloat(self.GetProp(PK.AnimRate)) != 0f
                             || UEInterpreter.AsFloat(self.GetProp(PK.TweenRate)) != 0f);
            if (sameLoop)
            {
                // x87 chains as in the main branch (disasm 0x10409544..0x104095ae).
                self.Props["AnimRate"] = AnimRateX87(rate, sequence);
                self.Props["bAnimFinished"] = false;
                self.Props["AnimMinRate"] = minRate == 0f ? 0f : AnimMinRateX87(minRate, sequence);
            }
            else
                SetAnimation(self, sequence, rate, tweenTime, minRate, loop: true);
            return null;
        }

        // ── native(294) TweenAnim(name Sequence, float Time) ─────────────────
        public object TweenAnimNative(UEInterpreter interp, UEFrame frame, object[] args)
        {
            UEObject self = frame.Context ?? frame.Object;
            if (RefusesNoMesh(self)) return null;
            string name = AnimNameArg(args, 0);
            // Shipped execTweenAnim delegates to AActor::PlayAnim with AT_Replace
            // and RootBone=None (Engine.dll.c:151923-151945). Besides setting the
            // tween fields below, that shared path clears bAnimMove and removes
            // auxiliary animation channels. Do not leave the previous Move route
            // active just because TweenAnim has no explicit RootBone argument.
            PrepareBaseAnimationRoute(interp, Array.Empty<object>(),
                typeIndex: 0, rootIndex: 1, ref self, name);
            bool foundT = ResolveAnimSequence(self, name, "TweenAnim",
                out UEAnimSequenceInfo sequence, out bool clearT);
            float tweenTime = AnimFloatArg(args, 1, 0f);
            if (!foundT && !clearT) return null; // miss: shipped skips the block
            // execTweenAnim (Engine.dll.c:151925-151945) delegates to
            // AActor::PlayAnim(seq, bLoop=false, Rate=0, tweenTime, MinRate=0):
            // the full path with AnimRate=0 — a pure TweenAlpha ramp; AnimLast
            // stays 1-1/NumFrames so FinishAnim arms until the tween completes.
            SetAnimation(self, sequence, rate: 0f, tweenTime, minRate: 0f, loop: false);
            Journal?.Invoke($"anim tween actor={self.GetProp(PK.Name)} sequence={name} frames={sequence?.NumFrames ?? 0}");
            return null;
        }

        // ── native(261) FinishAnim — LATENT ─────────────────────────────────
        public object FinishAnimNative(UEInterpreter interp, UEFrame frame, object[] args)
        {
            UEObject self = frame.Context ?? frame.Object;
            // Optional bone argument (execFinishAnim, Engine.dll.c:110531-110561):
            //   Mesh (+0x164) == NULL                       -> return;
            //   Mesh not IsA(USkeletalMesh)                 -> return;
            //   idx = BoneIndex(Bone); first AuxAnims channel (+0x6c, count +0x70)
            //   whose AnimBone byte (+100) == idx; none -> return
            //   (idx −1 never matches a byte — as in IsAnimatingNative);
            //   channel == this                             -> return.
            // All these exits are SILENT: no log, no bAnimLoop/bAnimFinished write,
            // no wait — disasm 0x10408290..0x1040834d: every exit jumps to the
            // epilogue 0x1040834d (or 0x104082ee), with no calls or writes on the way.
            // The mesh class is checked by walking Super up to USkeletalMesh (IsA); here, as in
            // IsAnimatingNative, by the resource class name. For a found channel shipped continues with THE SAME
            // tail but on the CHANNEL: channel flags and LatentAction = 0x181 in the CHANNEL's StateFrame
            // (+0xc), not the caller's. That branch is not ported — loud STOP (C3); all 162 campaign calls
            // pass no argument.
            string bone = AnimNameArg(args, 0);
            if (!string.IsNullOrEmpty(bone) && !bone.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                if (self?.GetProp(PK.Mesh) is not UEResourceRef meshRef) return null;
                if (!meshRef.Class.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase)) return null;
                int boneIndex = BoneLookupOf(meshRef, self, bone).Index;
                foreach (AnimChannelEntry e in AnimChannelsOf(self))
                    if ((UEInterpreter.ToInt(e.Object.GetProp(PK.AnimBone)) & 0xff) == boneIndex)
                    {
                        if (ReferenceEquals(e.Object, self)) return null;
                        throw new NotSupportedException(
                            $"FinishAnim on channel bone={bone} actor={self.GetProp(PK.Name)}: waiting on the "
                            + "channel StateFrame (Engine.dll.c:110563-110570) is not ported (STOP, C3)");
                    }
                return null;   // no channel with this bone — silent exit
            }
            // execFinishAnim (Engine.dll.c:110563-110573): a looping animation
            // drops bAnimLoop AND bAnimFinished; the latent arms ONLY while the
            // animation is running (AnimRate or TweenRate nonzero) and short of
            // AnimLast — otherwise the script continues immediately. The poll
            // (execPollFinishAnim, 61024) completes on bAnimFinished.
            if (UEInterpreter.ToBool(self?.GetProp(PK.bAnimLoop)))
            {
                self.Props["bAnimLoop"] = false;
                self.Props["bAnimFinished"] = false;
            }
            string seq = self?.GetProp(PK.AnimSequence)?.ToString();
            bool armed = !string.IsNullOrEmpty(seq)
                && !seq.Equals("None", StringComparison.OrdinalIgnoreCase)
                && (UEInterpreter.AsFloat(self.GetProp(PK.AnimRate)) != 0f
                    || UEInterpreter.AsFloat(self.GetProp(PK.TweenRate)) != 0f)
                && UEInterpreter.AsFloat(self.GetProp(PK.AnimFrame))
                   < UEInterpreter.AsFloat(self.GetProp(PK.AnimLast));
            if (armed) frame.LatentState = LatentRunState.FinishAnim;
            Journal?.Invoke($"anim_finish_wait actor={self?.GetProp(PK.Name)} armed={armed} frame={self?.GetProp(PK.AnimFrame)} last={self?.GetProp(PK.AnimLast)} rate={self?.GetProp(PK.AnimRate)}");
            return null;
        }

        // ── native(282) IsAnimating → bool ──────────────────────────────────
        // AActor::execIsAnimating (Engine.dll.c:118145; disasm
        // 0x1040a170-0x1040a282). Reads actor properties, not player state:
        // a pure tween (AnimRate == 0, TweenRate != 0) IS animating. Optional
        // argument is a bone name:
        //   no bone:   AnimSequence(+0x50) != None && (AnimRate(+0x58) != 0 ||
        //              TweenRate(+0x5c) != 0);
        //   with bone: mesh not USkeletalMesh → false; otherwise the first AuxAnims channel
        //              (+0x6c) whose AnimBone byte (+0x64) == BoneIndex(name)
        //              (0x1041dec0, −1 on a miss — never matches a byte), with
        //              the same condition on the channel; no channel → false.
        public object IsAnimatingNative(UEInterpreter interp, UEFrame frame, object[] args)
        {
            UEObject self = frame.Context ?? frame.Object;
            if (self is null) return false;
            static bool Animating(UEObject a)
            {
                string seq = a.GetProp(PK.AnimSequence)?.ToString();
                if (string.IsNullOrEmpty(seq) || seq.Equals("None", StringComparison.OrdinalIgnoreCase)) return false;
                return UEInterpreter.AsFloat(a.GetProp(PK.AnimRate)) != 0f
                    || UEInterpreter.AsFloat(a.GetProp(PK.TweenRate)) != 0f;
            }
            string bone = AnimNameArg(args, 0);
            if (string.IsNullOrEmpty(bone) || bone.Equals("None", StringComparison.OrdinalIgnoreCase))
                return Animating(self);
            if (self.GetProp(PK.Mesh) is not UEResourceRef meshRef
                || !meshRef.Class.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase)) return false;
            int boneIndex = BoneLookupOf(meshRef, self, bone).Index;
            foreach (AnimChannelEntry e in AnimChannelsOf(self))
                if ((UEInterpreter.ToInt(e.Object.GetProp(PK.AnimBone)) & 0xff) == boneIndex)
                    return Animating(e.Object);
            return false;
        }

        // ── native(263) HasAnim(name Sequence) → bool ───────────────────────
        // execHasAnim (Engine.dll.c:114338-114358): Mesh (+0x164) != NULL ->
        // result = GetAnimSeq(Sequence) != NULL (vtable +0x94); otherwise
        // Stack.Logf("HasAnim: No mesh") and the result stays 0 (false): disasm
        // 0x1040a42f..0x1040a441 — Logf(Stack, 0x104db718) and ret, no write to Result.
        // The line is logged on EVERY call, without repeat suppression.
        public object HasAnimNative(UEInterpreter interp, UEFrame frame, object[] args)
        {
            UEObject self = frame.Context ?? frame.Object;
            if (self?.GetProp(PK.Mesh) is null)
            {
                HP1.Core.UETrace.Warn($"HasAnim: No mesh (actor={self?.GetProp(PK.Name)} class={self?.Class?.Name})");
                return false;
            }
            return AnimSequenceLookup(self, AnimNameArg(args, 0)) is not null;
        }

        // ── native(293) GetAnimGroup(name Sequence) → name ──────────────────
        // AActor::execGetAnimGroup (Engine 0x1040a2e0..0x1040a39a) = GetAnimSeq(seq)->Group, None without a mesh/sequence.
        public object GetAnimGroupNative(UEInterpreter interp, UEFrame frame, object[] args)
        {
            UEAnimSequenceInfo sequence = AnimSequenceLookup(
                frame.Context ?? frame.Object, AnimNameArg(args, 0));
            return new UEName(string.IsNullOrEmpty(sequence?.Group) ? "None" : sequence.Group);
        }

        // AActor::PlayAnim full path (Engine.dll.c:73758-73810). The shipped KW
        // engine has NO negative-AnimFrame tween phase: AnimFrame starts at 0 and
        // the dedicated TweenAlpha property (actor +0x60, declared right after
        // TweenRate in Engine.u Actor) ramps 0->1 at TweenRate=1/tweenTime while
        // ApplyAnim fades the cached pose out with weight 1-TweenAlpha. A null
        // sequence (PlayAnim('None')) takes the same writes with AnimLast forced
        // to 0 (Engine.dll.c:73716-73718). SimAnim (+0x234) is replication-only
        // state with no reader in this single-player world and is not maintained;
        // OldAnimRate is likewise never read here and no longer written.
        // fild N; fdivr 1.0; fsubr 1.0; fstp.
        public static float AnimLastX87(int numFrames)
        {
            X87Ext one = X87Ext.From(1f);
            return X87Ext.Round64(one - X87Ext.Div64(one, X87Ext.From(numFrames))).ToFloat();
        }
        // fld Rate; fmul seq.Rate; fidiv N; fstp — a single rounding to float (PC64).
        public static float AnimRateX87(float rate, UEAnimSequenceInfo seq)
            => X87Ext.Div64(X87Ext.Round64(X87Ext.From(rate) * X87Ext.From(seq.Rate)), X87Ext.From(seq.NumFrames)).ToFloat();
        // fild N; fdivr seq.Rate; fmul MinRate; fstp.
        public static float AnimMinRateX87(float minRate, UEAnimSequenceInfo seq)
            => X87Ext.Round64(X87Ext.Div64(X87Ext.From(seq.Rate), X87Ext.From(seq.NumFrames)) * X87Ext.From(minRate)).ToFloat();

        void SetAnimation(UEObject self, UEAnimSequenceInfo sequence, float rate,
            float tweenTime, float minRate, bool loop)
        {
            float animLast;
            if (sequence is null)
            {
                self.Props["AnimSequence"] = new UEName("None");
                animLast = 0f;
                self.Props["AnimLast"] = 0f;
                self.Props["bAnimLoop"] = false;
            }
            else
            {
                // STOP (anim audit B6) — name SPELLING. Shipped writes the ARGUMENT's FName
                // into +0x50 (Engine.dll.c:73777), the port writes the mesh sequence name. Their
                // FName index is the same: FName::FName (Core.dll.c:57305, export 0x307b) looks the
                // entry up with appStricmp and returns the FIRST registered spelling (replaced only
                // in mode 3 when appStrcmp < 0). So shipped's string is the spelling from the global
                // name table, dependent on the registration order of all loaded packages; neither
                // "as in the argument" nor "as in the mesh" guarantees it, and the port has no global
                // table. Script comparisons (36 reads of AnimSequence in the game's .uc — name '=='
                // and GetAnimGroup) are case-blind; only the string form (save record, log) sees case.
                self.Props["AnimSequence"] = new UEName(sequence.Name);
                // x87 PRECISION (disasm AActor::PlayAnim 0x104096cc..0x10409733): all three fields
                // are computed in the x87 register (PC64, appEnableFastMath(0)) and rounded to float ONCE — fstp:
                //   AnimRate    = fld Rate; fmul seq.Rate; fidiv N            -> +0x58
                //   AnimLast    = fild N; fdivr 1.0; fsubr 1.0                -> +0x22c
                //   AnimMinRate = fild N; fdivr seq.Rate; fmul MinRate        -> +0x230 (MinRate != 0)
                // Rounding after each operation gives AnimLast 0x3f2aaaaa instead of 0x3f2aaaab at N = 3
                // (same for N = 11, 31, 251) — the end of an animation could shift by a tick.
                animLast = AnimLastX87(sequence.NumFrames);
                self.Props["AnimRate"] = AnimRateX87(rate, sequence);
                self.Props["AnimLast"] = animLast;
                self.Props["AnimMinRate"] = minRate == 0f ? 0f : AnimMinRateX87(minRate, sequence);
                self.Props["bAnimNotify"] = sequence.Notifies.Count != 0;
                self.Props["bAnimLoop"] = loop;
            }
            self.Props["bAnimFinished"] = false;
            self.Props["AnimFrame"] = 0f;
            self.Props["TweenAlpha"] = 0f;
            if (animLast == 0f)
            {
                self.Props["AnimMinRate"] = 0f;
                self.Props["AnimRate"] = 0f;
                self.Props["bAnimNotify"] = false;
            }
            if (tweenTime > 0f) self.Props["TweenRate"] = 1f / tweenTime;
            else if (tweenTime == 0f)
            {
                self.Props["TweenRate"] = 0f;
                self.Props["TweenAlpha"] = 1f;
            }
            else self.Props["TweenRate"] = 2f;
            Journal?.Invoke($"anim {(loop ? "loop" : "play")} actor={self.GetProp(PK.Name)} sequence={sequence?.Name ?? "None"} frames={sequence?.NumFrames ?? 0} rate={sequence?.Rate ?? 0f:R}");
        }

        // Returns false when the route consumes the call (stopping the channels under the bone)
        // and no base animation should play. `self` is replaced by the channel when the
        // animation goes to it: the rest of the native body is then the shipped recursion
        // PlayAnim(channel, …, AT_Replace, RootBone=None).
        // under the bone) and the base animation need not be played. `self` is replaced with
        bool PrepareBaseAnimationRoute(UEInterpreter interp, object[] args,
            int typeIndex, int rootIndex, ref UEObject self, string sequence)
        {
            int type = args.Length > typeIndex ? UEInterpreter.ToInt(args[typeIndex]) : 0;
            string root = AnimNameArg(args, rootIndex);
            if (type is < 0 or > 1)
                throw new InvalidOperationException($"animation invalid EAnimType={type}");

            bool none = string.IsNullOrEmpty(root)
                || root.Equals("None", StringComparison.OrdinalIgnoreCase);
            bool move = root.Equals("Move", StringComparison.OrdinalIgnoreCase);

            // Shipped AActor::PlayAnim first special-cases FName('Move'): it clears
            // RootBone to None and sets Actor's packed bAnimMove bit (+0x28 bit
            // 0x20), then continues through the ordinary base-animation path
            // (Engine.dll.c:73625-73635 / 307078-307087). The matching property
            // declaration is Actor.uc bAnimMove. USkeletalMesh::ApplyAnim extracts
            // the root delta and AActor::Tick moves the authoritative actor through
            // ULevel::MoveActor (Engine.dll.c:128808-128855). It is gameplay state,
            // never a frontend skinner responsibility.
            // PlayAnim does NOT touch the skeleton cache (with root state): the root is reset
            // by ApplyAnim on a sequence change (142098-142101), see
            // UELevelRuntime.SkeletalPose.cs. No separate notice goes to the frontend: it reads
            // `RootMotionDriven` from the frame.
            if (self?.GetProp(PK.Mesh) is not null)
                self.Props["bAnimMove"] = move;

            // A non-Move bone on a SKELETAL mesh is the AnimChannel route
            // (Engine.dll.c:73636-73682, call site disassembled at 0x10408fbe):
            //   sequence None  -> stop the channels rooted under the bone, return;
            //   otherwise      -> CreateAnimChannel(AnimChannel, Type, RootBone,
            //                     bAnimTransient=1) and play ON THE CHANNEL with
            //                     AT_Replace and RootBone=None; if no channel came
            //                     back (unknown bone) fall through to the base path.
            if (!none && !move
                && self?.GetProp(PK.Mesh) is UEResourceRef routedMesh
                && routedMesh.Class.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
            {
                bool sequenceNone = string.IsNullOrEmpty(sequence)
                    || sequence.Equals("None", StringComparison.OrdinalIgnoreCase);
                if (sequenceNone)
                {
                    StopAnimChannelsUnder(interp, self, root);
                    return false;
                }
                UClass channelClass = HP1.Core.UEClassRegistry.Shared.Find("AnimChannel")
                    ?? throw new InvalidOperationException("routed animation: class AnimChannel is not registered");
                UEObject channel = CreateAnimChannel(interp, self, channelClass, type, root, autoDestroy: true);
                if (channel is not null)
                {
                    self = channel;
                    type = 0;
                    if (channel.GetProp(PK.Mesh) is not null)
                        channel.Props["bAnimMove"] = false;
                }
            }

            // AT_Replace destroys every existing auxiliary channel before playing
            // the base animation (Engine.dll.c:73687-73730). AT_Combine preserves
            // them. RootBone='Move' has already become None at this point and obeys
            // the same rule.
            if (type == 0) DestroyAnimChannels(interp, self);
            return true;
        }

        // AActor::PlayAnim without a mesh (disasm 0x10408e35 -> 0x10409ace): logs
        // "PlayAnim: No mesh" to GLog and returns 0 — no bAnimMove, no channels, no
        // animation writes at all. PlayAnim/LoopAnim/TweenAnim all go through this body
        // (exec wrappers 0x10409e20/0x10409f40/0x1040a0d0), including the name 'None' (B5).
        bool RefusesNoMesh(UEObject self)
        {
            if (self?.GetProp(PK.Mesh) is not null) return false;
            if (_animMissWarned.Add($"{self?.Class?.Name}.<no mesh>"))
                HP1.Core.UETrace.Warn($"PlayAnim: No mesh (actor={self?.GetProp(PK.Name)} class={self?.Class?.Name})");
            return true;
        }

        // A CATALOG MISS IS LOUD (§8), not a silent no-op.
        //
        // The original prints `PlayAnim: Sequence '%s' not found` and continues. The port
        // The original logs `PlayAnim: Sequence '%s' not found` and continues; the port
        // logs it too, because a null from the catalog means TWO different things — "the
        // mesh has no such sequence" (original data, legitimate: skfredMesh 'breath' vs the
        // cutscene's 'Breathe') and "the actor has no player" (a frontend defect). The name
        // 'None' is a deliberate animation reset, not a miss.
        // Returns: found -> the sequence; otherwise clear=true ONLY for the name 'None',
        // and clear=false for a real miss.
        //
        // Shipped entry condition is `(GetAnimSeq(name) != NULL) || (name == None)`
        // (Engine.dll.c:73714). Hence EXACTLY three branches:
        //   found                  -> the full set of writes;
        //   not found, name=None   -> entered with AnimLast=0 (73716-73718),
        //                             i.e. the animation is STOPPED;
        //   not found, other name  -> the WHOLE block is skipped: the previous animation
        //                             keeps playing.
        // The third branch is the original's real behaviour on mismatches in its own data
        // its own data (skronMesh carries 'Breathe', while Tut1Ron.uc asks for
        // 'breath'). Clearing the state there would be invented behaviour: it changed
        // 23 731 lines of the TAS log because pawns stopped getting AnimEnd and their
        // state machines diverged.
        bool ResolveAnimSequence(UEObject self, string name, string native,
            out UEAnimSequenceInfo sequence, out bool clear)
        {
            sequence = AnimSequenceLookup(self, name);
            if (sequence is not null) { clear = false; return true; }
            bool isNone = string.IsNullOrEmpty(name)
                || name.Equals("None", StringComparison.OrdinalIgnoreCase);
            clear = isNone;
            if (isNone) return false;
            // The miss is LOUD (§8) but makes NO writes: the original also logs
            // `PlayAnim: Sequence '%s' not found` and continues.
            string key = $"{self?.Class?.Name}.{name}";
            if (_animMissWarned.Add(key))
                HP1.Core.UETrace.Warn($"{native}: Sequence '{name}' not found"
                    + $" (actor={self?.GetProp(PK.Name)} class={self?.Class?.Name})"
                    + (MeshCatalog is null ? " — mesh catalog NOT LOADED" : ""));
            return false;
        }
        readonly HashSet<string> _animMissWarned = new(UEPropNameComparer.Instance);

        // Same semantics as headless NameArg/FloatArg: a missing name argument is the NAME
        // 'None', not empty (bytecode passes it literally, and the mesh catalog must answer
        // it like any other missed name).
        static string AnimNameArg(object[] args, int index)
            => args is null || args.Length <= index || args[index] is null ? "None"
             : args[index] is UEName name ? name.Value : args[index].ToString();

        static float AnimFloatArg(object[] args, int index, float fallback)
            => args is null || args.Length <= index || args[index] is null
                ? fallback : UEInterpreter.AsFloat(args[index]);
    }
}
