// AActor::TickAnimation — Core-owned.
//
// Logical animation is gameplay: AnimEnd switches states, the 'Cast' notify
// launches a spell. So all of the LOGIC lives here (frame count, tween, notify
// scan, finish/wrap branches); the frontend keeps only what is really its own:
// where the sequence metadata comes from (rate, frame count, notify list) —
// that is its mesh data.
//
// Source: shipped AActor::TickAnimation, disasm-checked loop
// 0x103b39e4-0x103b3ac2: at most 4 passes per tick; TweenAlpha advances at the
// head of EVERY pass and does NOT consume dt (a pure tween therefore gains up to
// 4*dt*TweenRate per tick — a shipped quirk, kept as is), while frame advance
// consumes dt and returns the remainder to the notify/finish/wrap passes. The
// animator (Velocity source, receiver of AnimEnd and notifies) is the Owner when
// AnimBone!=0 && bAnimTransient (Engine.dll.c:128388: byte +0x64 and bit 0x100 of
// word +0x28; per the offset oracle this is bAnimTransient, NOT bAnimByOwner = 0x80).
//   notify scan        128432-128465
//   non-loop finish    128467-128484
//   loop wrap          128491-128509
using System;
using System.Collections.Generic;
using UELib.Core;

namespace HP1.UEScript
{
    // Sequence metadata as the animation logic sees it. Exactly what the loop
    // needs and nothing of the mesh format (shipped UMesh AnimSeqs).
    public sealed class UEAnimSequenceInfo
    {
        public string Name;
        public float Rate;
        public int NumFrames;
        // UMesh AnimSeq.Group — read by native(293) GetAnimGroup, which the pawn
        // animation state machines read (Harry.uc PlayerWalking.AnimEnd).
        public string Group;
        public IReadOnlyList<(float Time, string Function)> Notifies
            = Array.Empty<(float, string)>();
    }

    public sealed partial class UELevelRuntime
    {
        // AActor::Tick animation advance (disasm 0x103b3ad8..0x103b3b43), one fstp dword at the end:
        // AnimRate >= 0: AnimFrame + elapsed·AnimRate; < 0: rate = −(|Velocity|·AnimRate) with |Velocity| = appSqrt of
        // (X·X + Y·Y) + Z·Z, kept in the register; AnimMinRate if not below it; then ·elapsed + AnimFrame, all at PC64.
        internal static float AnimFrameAdvance(float frame, float animRate, float minRate, UEVector vel, float elapsed)
        {
            if (!float.IsFinite(frame) || !float.IsFinite(animRate) || !float.IsFinite(minRate) || !float.IsFinite(elapsed)
                || !float.IsFinite(vel.x) || !float.IsFinite(vel.y) || !float.IsFinite(vel.z))
            {
                float r0 = animRate >= 0f ? animRate : Math.Max(minRate, -animRate * vel.magnitude);
                return frame + r0 * elapsed;
            }
            static HP1.Core.X87Ext X(float v) => HP1.Core.X87Ext.From(v);
            static HP1.Core.X87Ext R(HP1.Core.X87Ext v) => HP1.Core.X87Ext.Round64(v);
            if (animRate >= 0f) return R(X(elapsed) * X(animRate) + X(frame)).ToFloat();
            HP1.Core.X87Ext rate = HP1.Core.X87Ext.From(0) - R(UEVector.Size64Ext(vel.x, vel.y, vel.z) * X(animRate));   // fchs
            if (!(X(minRate).CompareTo(rate) < 0)) rate = X(minRate);
            return R(R(rate * X(elapsed)) + X(frame)).ToFloat();
        }

        // AActor::Tick TweenAlpha 0x103b3a47..0x103b3a5e: elapsed·TweenRate + TweenAlpha in the register, fst (the
        // stored float), and the compare with 1.0f reads the unrounded register value.
        internal static float TweenAlphaStep(float alpha, float tweenRate, float elapsed, out bool ended)
        {
            if (!float.IsFinite(alpha) || !float.IsFinite(tweenRate) || !float.IsFinite(elapsed))
            {
                float a0 = alpha + elapsed * tweenRate;
                ended = a0 >= 1f;
                return a0;
            }
            HP1.Core.X87Ext a = HP1.Core.X87Ext.Round64(HP1.Core.X87Ext.From(elapsed) * HP1.Core.X87Ext.From(tweenRate)
                + HP1.Core.X87Ext.From(alpha));
            ended = !(a.CompareTo(HP1.Core.X87Ext.From(1f)) < 0);
            return a.ToFloat();
        }

        // AActor::Tick unconsumed time after a notify jump (0x103b3bc5..0x103b3be5), a non-looping end
        // (0x103b3cb4..0x103b3ce6) and the loop wrap (0x103b3c42..0x103b3c5d): (frame − mark)·elapsed / (frame − from),
        // one register chain, one fstp dword.
        internal static float AnimElapsedRemainder(float frame, float mark, float elapsed, float from)
        {
            if (!float.IsFinite(frame) || !float.IsFinite(mark) || !float.IsFinite(elapsed) || !float.IsFinite(from)
                || frame == from)
                return (frame - mark) * elapsed / (frame - from);   // x87 and IEEE agree on inf/NaN here
            static HP1.Core.X87Ext X(float v) => HP1.Core.X87Ext.From(v);
            static HP1.Core.X87Ext R(HP1.Core.X87Ext v) => HP1.Core.X87Ext.Round64(v);
            return HP1.Core.X87Ext.Div64(R(R(X(frame) - X(mark)) * X(elapsed)), R(X(frame) - X(from))).ToFloat();
        }

        // Notify scan 0x103b3b85..0x103b3bb9: a notify is a candidate when from < time <= AnimFrame; its distance
        // time − from (register) wins when the first one or below the stored best (fstp dword [ebp-0x30]).
        internal static bool NotifyCloser(float time, float from, bool first, ref float best)
        {
            if (!float.IsFinite(time) || !float.IsFinite(from))
            {
                float d0 = time - from;
                if (!first && !(d0 < best)) return false;
                best = d0; return true;
            }
            HP1.Core.X87Ext d = HP1.Core.X87Ext.Round64(HP1.Core.X87Ext.From(time) - HP1.Core.X87Ext.From(from));
            if (!first && !(d.CompareTo(HP1.Core.X87Ext.From(best)) < 0)) return false;
            best = d.ToFloat();
            return true;
        }

        // MESH CATALOG — Core. Filled once from the installed game; animation
        // does not resolve before that.
        public Hp1SkeletalMeshCatalog MeshCatalog;

        public void LoadMeshCatalogFromInstall(string systemDir)
            => MeshCatalog ??= new Hp1SkeletalMeshCatalog(systemDir);

        // Meshes and animations for the frontend — over the same catalog as the
        // game natives: one set of package data for Core and for drawing.
        UEMeshSource _meshSource;
        public UEMeshSource MeshSource => MeshCatalog is null ? null
            : _meshSource is not null && _meshSourceCatalog == MeshCatalog ? _meshSource
            : (_meshSource = new UEMeshSource(_meshSourceCatalog = MeshCatalog));
        Hp1SkeletalMeshCatalog _meshSourceCatalog;

        // Sequence resolution by the EXACT pair (actor mesh, name), owned by Core
        // (a frontend hook would answer from its own player state, a frame late).
        // Null = no such sequence (UE1 no-op), and that is a legal case: the
        // original data is self-inconsistent (skronMesh carries 'Breathe',
        // while Tut1Ron.uc asks for 'breath').
        public UEAnimSequenceInfo AnimSequenceLookup(UEObject actor, string sequenceName)
        {
            if (MeshCatalog is null || actor is null || string.IsNullOrEmpty(sequenceName))
                return null;
            if (actor.GetProp(PK.Mesh) is not UEResourceRef meshRef) return null;
            // AActor::GetAnim (Engine.dll.c:161844-161880): for a skeletal mesh —
            // the sequences of Actor.SkelAnim, or the mesh's DefaultAnimation when empty;
            // for a vertex mesh — the mesh itself. SkelAnim is NOT written here
            // (only ApplyAnim writes it).
            Hp1AnimSequence sequence;
            if (meshRef.Class.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
            {
                IReadOnlyDictionary<string, Hp1AnimSequence> anim =
                    AnimationOf(actor, MeshCatalog.Get(meshRef));
                sequence = anim is not null && anim.TryGetValue(sequenceName, out Hp1AnimSequence s) ? s : null;
            }
            else sequence = MeshCatalog.FindSequence(meshRef, sequenceName);
            if (sequence is null) return null;
            var notifies = new List<(float, string)>(sequence.Notifies.Length);
            foreach (Hp1AnimNotify n in sequence.Notifies) notifies.Add((n.Time, n.Function));
            return new UEAnimSequenceInfo
            {
                Name = sequence.Name,
                Rate = sequence.Rate,
                NumFrames = sequence.NumFrames,
                Group = sequence.Group,
                Notifies = notifies,
            };
        }

        public void TickAnimation(UEInterpreter interp, UEObject actor, float elapsed)
        {
            UEObject animator = actor;
            if (UEInterpreter.ToInt(actor.GetProp(PK.AnimBone)) != 0
                && UEInterpreter.ToBool(actor.GetProp(PK.bAnimTransient))
                && actor.GetProp(PK.Owner) is UEObject byOwner)
                animator = byOwner;

            for (int iterations = 1; ; iterations++)
            {
                string sequenceName = actor.GetProp(PK.AnimSequence)?.ToString();
                bool hasSequence = !string.IsNullOrEmpty(sequenceName)
                    && !sequenceName.Equals("None", StringComparison.OrdinalIgnoreCase);
                float animRate = UEInterpreter.AsFloat(actor.GetProp(PK.AnimRate));
                float tweenRate = UEInterpreter.AsFloat(actor.GetProp(PK.TweenRate));
                if (!hasSequence || (animRate == 0f && tweenRate == 0f)
                    || elapsed <= 0f || iterations > 4)
                    break;

                if (tweenRate > 0f)
                {
                    float alpha = TweenAlphaStep(UEInterpreter.AsFloat(actor.GetProp(PK.TweenAlpha)), tweenRate, elapsed,
                        out bool tweenEnded);
                    actor.Props["TweenAlpha"] = alpha;
                    if (tweenEnded)
                    {
                        actor.Props["TweenAlpha"] = 1f;
                        actor.Props["TweenRate"] = 0f;
                        if (animRate == 0f)
                        {
                            actor.Props["bAnimFinished"] = true;
                            UELifecycle.CallEvent(interp, animator, "AnimEnd");
                        }
                    }
                }
                float from = UEInterpreter.AsFloat(actor.GetProp(PK.AnimFrame));
                // The do-while back edge rereads AnimRate (0x103b3aae):
                // an AnimEnd raised by the tween's end may have started a new
                // animation, and it advances its frame in this same pass.
                animRate = UEInterpreter.AsFloat(actor.GetProp(PK.AnimRate));
                if (animRate == 0f) continue;   // pure tween: back to the head checks

                float to = AnimFrameAdvance(from, animRate, UEInterpreter.AsFloat(actor.GetProp(PK.AnimMinRate)),
                    UEInterpreter.AsVector3(animator.GetProp(PK.Velocity)), elapsed);
                actor.Props["AnimFrame"] = to;

                // Notify scan: nearest within (from, to]; the frame jumps to it,
                // dt is recomputed to the unconsumed remainder, the function is sent
                // to the ANIMATOR (a silently missing handler is skipped).
                if (UEInterpreter.ToBool(actor.GetProp(PK.bAnimNotify))
                    && actor.GetProp(PK.Mesh) is not null
                    && AnimSequenceLookup(actor, sequenceName) is UEAnimSequenceInfo sequence)
                {
                    string nearestFn = null;
                    float nearestTime = 0f, best = 100000f;
                    foreach ((float time, string function) in sequence.Notifies)
                        if (time > from && time <= to && NotifyCloser(time, from, nearestFn is null, ref best))
                        { nearestFn = function; nearestTime = time; }
                    if (nearestFn is not null)
                    {
                        actor.Props["AnimFrame"] = nearestTime;
                        elapsed = AnimElapsedRemainder(to, nearestTime, elapsed, from);
                        UFunction fn = interp.FindEventFunction(animator, nearestFn);
                        if (fn is not null) interp.Invoke(animator, fn);
                        continue;
                    }
                }

                float animLast = UEInterpreter.AsFloat(actor.GetProp(PK.AnimLast));
                float frame = UEInterpreter.AsFloat(actor.GetProp(PK.AnimFrame));
                if (frame < animLast) break;    // mid-animation: exit with unconsumed dt

                if (!UEInterpreter.ToBool(actor.GetProp(PK.bAnimLoop)))
                {
                    // Non-loop finish: jump to AnimLast, recompute dt, set the
                    // finished flag, AnimEnd to the ANIMATOR. SimAnim replication is not
                    // supported here.
                    actor.Props["AnimRate"] = 0f;
                    actor.Props["AnimFrame"] = animLast;
                    elapsed = AnimElapsedRemainder(frame, animLast, elapsed, from);
                    actor.Props["bAnimFinished"] = true;
                    UELifecycle.CallEvent(interp, animator, "AnimEnd");
                    continue;
                }
                // Loop wrap: past 1.0 — wrap to 0 with recomputed dt;
                // bAnimFinished only while latently waiting in FinishAnim (shipped
                // latent action check 0x181); AnimEnd is sent to SELF here.
                if (frame >= 1f)
                {
                    actor.Props["AnimFrame"] = 0f;
                    elapsed = AnimElapsedRemainder(frame, 1f, elapsed, from);
                }
                else elapsed = 0f;
                if (from < animLast)
                {
                    if (actor.StateFrame?.LatentState == LatentRunState.FinishAnim)
                        actor.Props["bAnimFinished"] = true;
                    UELifecycle.CallEvent(interp, actor, "AnimEnd");
                }
            }
        }
    }
}
