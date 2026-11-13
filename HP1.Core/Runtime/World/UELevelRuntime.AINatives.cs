// Native AI sight/target queries.  They mutate Pawn sight state and consume
// the shared appFrand stream, so they belong beside the authoritative Core
// world, not in a frontend camera/physics bridge.
//
// Source of truth is the shipped Engine.dll. A simplified model (a single
// center/top/bottom probe) would drop the
// Visibility scaling, the Stimulus transform, the Enemy bookkeeping, the
// bLOSflag alternation, the RNG throttle and the corner probes:
//
//   APawn::LineOfSightTo          Engine.dll.c:65712-66070
//   APawn::execLineOfSightTo      Engine.dll.c:24921-24939   -> (Other, 0)
//   APawn::execCanSee             Engine.dll.c:92822-92840   -> (Other, 1)
//   AActor::execPlayerCanSeeMe    Engine.dll.c:97454-97519
//   AActor::TestCanSeeMe          Engine.dll.c:69518-69597
//   APawn::execPickTarget         Engine.dll.c:106333-106450
//   APawn::execPickAnyTarget      Engine.dll.c:159707-159799
//
// Field offsets read out of those bodies were named by reconstructing the
// shipped layout from the Engine.u property chain with the UE1 UStruct::Link
// rules and requiring it to reproduce the offsets the decompile already
// pins.  All of these are anchors that matched exactly:
//   Actor  0x80 XLevel, 0x9c Base, 0xd4 Touching[4], 0xfc Location,
//          0x108 Rotation, 0x1cc CollisionRadius, 0x1d4 CollisionHeight,
//          0x1dc bools (bit 0x1 bCollideActors, bit 0x10 bProjTarget)
//   Pawn   0x25c bools (bit 0x2 bIsPlayer, bit 0x80000 bLOSflag),
//          0x2e0 Visibility, 0x2e8 Stimulus, 0x2ec SightRadius,
//          0x2f0 PeripheralVision, 0x2f8 LastSeenPos, 0x304 LastSeeingPos,
//          0x314 Enemy, 0x33c ViewRotation, 0x354 BaseEyeHeight,
//          0x388 Health, 0x3b8 Skill, 0x3f8 nextPawn
//   PlayerPawn 0x4f4 ViewTarget;  LevelInfo 0x498 PawnList;
//   GameInfo 0x26c bit 0x200 bTeamGame;  PlayerReplicationInfo 0x284 Team
using System;
using System.Collections.Generic;
using UELib.Core;
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        static bool PropBool(UEObject actor, string name)
            => UEInterpreter.ToBool(actor?.GetProp(name));

        static Vector3 ActorLocation(UEObject actor)
            => UEInterpreter.AsVector3(actor?.GetProp(PK.Location));

        static bool IsPawn(UEObject actor)
            => actor?.Class is not null
               && UEInterpreter.ClassIsA(actor.Class, "Pawn");

        bool SightLineClear(Vector3 target, Vector3 eye)
        {
            if (Bsp is null)
                throw new InvalidOperationException(
                    "AI sight query reached before BSP world load");
            return Bsp.FastLineCheckClear(target, eye);
        }

        // GMath.UnitCoords /= Rotation, then the X axis (Engine.dll.c:65864-
        // 65878).  DivRotator is the shipped FCoords::operator/=.
        static Vector3 ActorForward(UEObject actor, string rotationProp)
        {
            UERotator rotation =
                UEInterpreter.AsRotator(actor.GetProp(rotationProp));
            BrushCoords coords = BrushCoords.Identity;
            coords.DivRotator(rotation.Pitch, rotation.Yaw, rotation.Roll);
            return coords.X;
        }

        // APawn::LineOfSightTo(Other, bCheckFOV).  bCheckFOV is what separates
        // the two natives: CanSee passes 1 (SightRadius + PeripheralVision +
        // Stimulus), LineOfSightTo passes 0 (fixed Visibility-scaled ranges).
        public bool PawnLineOfSightTo(UEObject self, UEObject other, bool checkFov)
        {
            if (self is null || other is null) return false;

            UEObject enemy = self.GetProp(PK.Enemy) as UEObject;
            bool againstEnemy = ReferenceEquals(other, enemy);
            // Against the Enemy the FOV half is skipped outright; otherwise a
            // CanSee call ALTERNATES bLOSflag, which later decides both the
            // far-range gate and which corner probe is skipped.
            if (againstEnemy) checkFov = false;
            else if (checkFov) self.Props["bLOSflag"] = !PropBool(self, "bLOSflag");

            Vector3 selfLocation = ActorLocation(self);
            Vector3 otherLocation = ActorLocation(other);
            Vector3 delta = otherLocation - selfLocation;
            // 0x103da0ec..0x103da122: differences, squares and (dx² + dy²) + dz² in the register, one fstp dword.
            float distSq = LosDistSq(otherLocation, selfLocation);
            bool otherIsPawn = IsPawn(other);
            bool selfIsPlayer = PropBool(self, "bIsPlayer");
            // rangeSq stays on the x87 stack until the far-range half compare (0x103da5fa); the CanSee half stores
            // it as float first (0x103da1ab), the LineOfSightTo half never does (0x103da4f2..0x103da58f).
            HP1.Core.X87Ext rangeSq = LosRangeSq(checkFov, otherIsPawn, selfIsPlayer, otherIsPawn ? VisibilityOf(other) : 0,
                UEInterpreter.AsFloat(self.GetProp(PK.SightRadius)));
            float effectiveDistSq;

            // fld distSq; fcomp rangeSq; on above: false (0x103da1d9, 0x103da58f).
            if (X87(distSq).CompareTo(rangeSq) > 0) return false;
            if (!checkFov) effectiveDistSq = distSq;
            else
            {
                Vector3 direction = SafeNormalShipped(delta);
                // Shipped writes Stimulus twice — the raw cone term first (fst, 0x103da2c4), then the transformed
                // value (0x103da2e5). Nothing observes the object between the two stores, so the port keeps the
                // final value only.
                float stimulus = LosStimulus(ActorForward(self, "Rotation"), direction,
                    UEInterpreter.AsFloat(self.GetProp(PK.PeripheralVision)));
                self.Props["Stimulus"] = stimulus;
                if (stimulus <= 0f) return false;

                // Height difference is divided by Skill (0x3b8), floored at 1,
                // and the whole distance is divided by Stimulus^2: a
                // sharper-facing, more skilled pawn sees further.
                effectiveDistSq = LosEffectiveDistSq(otherLocation.z, selfLocation.z,
                    UEInterpreter.AsFloat(self.GetProp(PK.Skill)), stimulus, distSq);
                if (rangeSq.ToFloat() < effectiveDistSq) return false;
                self.Props["Stimulus"] = 1f;
            }

            Vector3 eye = selfLocation;
            eye.z += UEInterpreter.AsFloat(self.GetProp(PK.BaseEyeHeight));

            if (againstEnemy)
            {
                if (SightLineClear(otherLocation, eye)
                    || SightLineClear(otherLocation, selfLocation))
                {
                    // Both writes happen ONLY on a successful enemy sighting.
                    self.Props["LastSeeingPos"] = selfLocation;
                    self.Props["LastSeenPos"] = ActorLocation(enemy);
                    return true;
                }
                if (effectiveDistSq > 1000000f) return false;
            }
            else if (effectiveDistSq > 1000000f)
            {
                if (otherIsPawn)
                {
                    // fmul qword 0.5 on the register rangeSq; fcompp (0x103da5fa..0x103da605).
                    if (!PropBool(self, "bLOSflag")
                        && (rangeSq * HP1.Core.X87Ext.From(0.5)).CompareTo(X87(effectiveDistSq)) < 0) return false;
                    if (!selfIsPlayer)
                    {
                        // The ONLY appFrand in this body, and only on the far
                        // non-enemy pawn branch: half of those checks are
                        // dropped outright (Engine.dll.c:65960).
                        if (FrandHook is null)
                            throw new NotSupportedException(
                                "LineOfSightTo RNG branch reached without FrandHook, actor="
                                + self.GetProp(PK.Name));
                        // 0x103da630: fcomp qword 0.5 against st0 — exact comparison.
                        if (FrandHook().CompareTo(HP1.Core.X87Ext.From(0.5)) < 0) return false;
                    }
                }
                return SightLineClear(otherLocation, eye);
            }

            // Top of the target's cylinder — skipped for a CanSee call while
            // bLOSflag is set (Engine.dll.c:65971).
            if (!checkFov || !PropBool(self, "bLOSflag"))
            {
                var top = new Vector3(otherLocation.x, otherLocation.y,
                    LosTopZ(otherLocation.z, UEInterpreter.AsFloat(other.GetProp(PK.CollisionHeight))));
                if (SightLineClear(top, eye)) return true;
            }

            if (effectiveDistSq > 250000f) return false;
            if (!otherIsPawn) return false;

            float radius = UEInterpreter.AsFloat(other.GetProp(PK.CollisionRadius));
            var corners = new Vector3[4]
            {
                new(otherLocation.x - radius, otherLocation.y + radius, otherLocation.z),
                new(otherLocation.x + radius, otherLocation.y + radius, otherLocation.z),
                new(otherLocation.x - radius, otherLocation.y - radius, otherLocation.z),
                new(otherLocation.x + radius, otherLocation.y - radius, otherLocation.z),
            };
            // The nearest and farthest corner are dropped — measured, as the
            // shipped body does, by the point's distance from the WORLD ORIGIN
            // and not from the viewer (Engine.dll.c:66019-66042).  Keep the
            // quirk: on an off-origin map it selects a different pair than a
            // viewer-relative test would.
            var (imin, imax) = LosCornerPick(corners);

            // bLOSflag makes a CanSee call skip the FIRST eligible corner, so
            // consecutive calls alternate which of the two is probed.
            bool skip = PropBool(self, "bLOSflag");
            for (int i = 0; i < 4; i++)
            {
                if (i == imin || i == imax) continue;
                if (!skip || !checkFov)
                {
                    skip = true;
                    if (SightLineClear(corners[i], eye)) return true;
                    continue;
                }
                skip = false;
            }
            return false;
        }

        // ── native(512) MakeNoise → AActor::CheckNoiseHearing ───────────────
        // execMakeNoise (Engine.dll.c:161040-161053) is a NetMode gate and
        // nothing else; the mechanism is CheckNoiseHearing (90601).
        public object MakeNoiseNative(
            UEInterpreter interp, UEFrame frame, object[] args)
        {
            UEObject self = frame.Context ?? frame.Object;
            if (self is null) return null;
            float loudness = args.Length > 0 ? UEInterpreter.AsFloat(args[0]) : 0f;
            UEObject level = interp.GetLevelInfo();
            if (UEInterpreter.ToInt(level?.GetProp(PK.NetMode)) == 3) return null;
            CheckNoiseHearing(interp, self, loudness);
            return null;
        }

        // ── x87 hearing chains (FPCW PC64, from disasm) ──────────
        // Shipped keeps differences, squares and sums on the x87 stack and rounds to
        // float only at `fstp dword`. Rounding every operation to float makes CanHear's
        // Stimulus differ by 1–2 ULP in ~40 % of calls; and CheckNoiseHearing's 0.9 is a
        // DOUBLE constant (`fmul qword [0x10478e78]`) — "0.9f·L" would suppress noise that
        // shipped lets through (L = 1, previous loudness 0.9f: 0.9f < 0.9).
        static HP1.Core.X87Ext X87(float v) => HP1.Core.X87Ext.From(v);
        static HP1.Core.X87Ext R64(HP1.Core.X87Ext v) => HP1.Core.X87Ext.Round64(v);

        // |a - b|² as one chain: d = a - b on the stack, sum (dx² + dy²) + dz²
        // (CanHear 0x103dacc9..0x103dacfd, CheckNoiseHearing 0x103db03c..0x103db06e).
        static HP1.Core.X87Ext DistSq87(Vector3 a, Vector3 b)
        {
            HP1.Core.X87Ext dx = R64(X87(a.x) - X87(b.x));
            HP1.Core.X87Ext dy = R64(X87(a.y) - X87(b.y));
            HP1.Core.X87Ext dz = R64(X87(a.z) - X87(b.z));
            return R64(R64(R64(dx * dx) + R64(dy * dy)) + R64(dz * dz));
        }

        // ── APawn::LineOfSightTo x87 chains (0x103da070, PC64). ──
        internal static float LosDistSq(Vector3 a, Vector3 b) => DistSq87(a, b).ToFloat();

        // Range² in the register. LineOfSightTo half (0x103da4c4..0x103da589), pawn target: s = min(fild(Visibility
        // + 16)·0.015f, 1); (s·5000f)² (NPC 4000f) unless not below the cap 25e6 (NPC 16e6); non-pawn 16e6 (NPC 9e6).
        // CanSee half (0x103da153..0x103da1d7), pawn: s = min(fild(Visibility)·(1/128), 1); (s·SightRadius)² stored
        // as float unless not below the cap 16e6 (NPC 12e6); non-pawn: float(SightRadius²). min(s, 1) and the caps are
        // `fld k; fcomp st(1); test ah,0x41`: the register value survives only strictly below the constant.
        internal static HP1.Core.X87Ext LosRangeSq(bool checkFov, bool otherIsPawn, bool selfIsPlayer, int visibility, float sightRadius)
        {
            if (!otherIsPawn)
                return checkFov ? X87(sightRadius * sightRadius) : X87(selfIsPlayer ? 16000000f : 9000000f);
            HP1.Core.X87Ext one = X87(1f);
            HP1.Core.X87Ext s = checkFov ? X87(visibility) * X87(0.0078125f) : HP1.Core.X87Ext.From(visibility + 16) * X87(0.015f);
            if (!(one.CompareTo(s) > 0)) s = one;
            HP1.Core.X87Ext r = R64(s * X87(checkFov ? sightRadius : selfIsPlayer ? 5000f : 4000f));
            HP1.Core.X87Ext r2 = R64(r * r);
            float cap = checkFov ? (selfIsPlayer ? 16000000f : 12000000f) : (selfIsPlayer ? 25000000f : 16000000f);
            if (!(X87(cap).CompareTo(r2) > 0)) return X87(cap);
            return checkFov ? X87(r2.ToFloat()) : r2;
        }

        // 0x103da296..0x103da2e5: s = ((F.x·D.x + F.y·D.y) + F.z·D.z) − PeripheralVision in the register; the branch
        // reads the register (s <= 0: ·0.17f, else ·0.8f), + 0.2f, one fstp dword.
        internal static float LosStimulus(Vector3 f, Vector3 d, float peripheral)
        {
            HP1.Core.X87Ext s = R64(R64(R64(X87(f.x) * X87(d.x) + X87(f.y) * X87(d.y)) + X87(f.z) * X87(d.z)) - X87(peripheral));
            return R64(R64(s * X87(s.Sign <= 0 ? 0.17f : 0.8f)) + X87(0.2f)).ToFloat();
        }

        // 0x103da313..0x103da366: dz = |other.z − self.z|, skill = Skill + 1 (1 unless above it), q = dz / skill,
        // ((q·q) + distSq) / (Stimulus·Stimulus) in the register, one fstp dword.
        internal static float LosEffectiveDistSq(float otherZ, float selfZ, float skill, float stimulus, float distSq)
        {
            HP1.Core.X87Ext dz = R64(X87(otherZ) - X87(selfZ));
            if (dz.Sign < 0) dz = HP1.Core.X87Ext.From(0) - dz;
            HP1.Core.X87Ext k = R64(X87(skill) + X87(1f));
            if (!(X87(1f).CompareTo(k) < 0)) k = X87(1f);
            HP1.Core.X87Ext q = HP1.Core.X87Ext.Div64(dz, k);
            return HP1.Core.X87Ext.Div64(R64(R64(q * q) + X87(distSq)), X87(stimulus) * X87(stimulus)).ToFloat();
        }

        // 0x103da6e1..0x103da706: fld CollisionHeight; fmul 0.8f; fadd Location.z; fstp dword.
        internal static float LosTopZ(float z, float collisionHeight) => R64(X87(collisionHeight) * X87(0.8f) + X87(z)).ToFloat();

        // 0x103da894..0x103da91d: corner 0's (x·x + y·y) + z·z stored as float into both min and max; each next corner's
        // sum stays in the register for `fcom max` (above: new max) else `fcom min` (below: new min), stored as float.
        internal static (int imin, int imax) LosCornerPick(Vector3[] c)
        {
            static HP1.Core.X87Ext Sq(Vector3 v) => UEVector.SizeSquared64(v.x, v.y, v.z);
            float min = Sq(c[0]).ToFloat(), max = min;
            int imin = 0, imax = 0;
            for (int i = 1; i < 4; i++)
            {
                HP1.Core.X87Ext s = Sq(c[i]);
                if (s.CompareTo(X87(max)) > 0) { max = s.ToFloat(); imax = i; }
                else if (s.CompareTo(X87(min)) < 0) { min = s.ToFloat(); imin = i; }
            }
            return (imin, imax);
        }

        // `fld L; fmul L; fmul dword k; fcomp dist; test ah,1` — C0: L²·k < dist
        // or an unordered comparison (NaN). Non-finite loudness (the script
        // divides by JumpZ) uses the same logic in double: with ±inf/NaN the ordering is exact.
        static bool LoudSqBelow(float loudness, float k, float dist)
        {
            if (!float.IsFinite(loudness))
                return !((double)loudness * loudness * k >= dist);
            return R64(R64(X87(loudness) * X87(loudness)) * X87(k)).CompareTo(X87(dist)) < 0;
        }

        // `fld loud; fld L; fmul qword 0.9; fcompp; test ah,0x41` — suppress
        // when L·0.9 ≤ loud or unordered (0x103db083..0x103db099).
        static bool LoudSuppresses(float slotLoudness, float loudness)
        {
            if (!float.IsFinite(loudness) || !float.IsFinite(slotLoudness))
                return !((double)loudness * 0.9 > slotLoudness);
            return R64(X87(loudness) * HP1.Core.X87Ext.From(0.9))
                .CompareTo(X87(slotLoudness)) <= 0;
        }

        public void CheckNoiseHearing(UEInterpreter interp, UEObject self, float loudness)
        {
            // The noise is recorded on the INSTIGATOR, not on the actor that
            // made it; without one the shipped body returns before any write.
            if (self.GetProp(PK.Instigator) is not UEObject noisePawn) return;
            UEObject level = interp.GetLevelInfo();
            // Clock is ULevel+0xdc EXACTLY: `fild qword [XLevel+0xdc]; fmul dword [2^-32]` (0x103db01b..0x103db021),
            // kept on the x87 stack through all comparisons; rounded to float only at the slot write `fstp dword` (D-11).
            if (!ReferenceEquals(_clockLevel, level))
                throw new InvalidOperationException("CheckNoiseHearing: level clock not up for the current LevelInfo");
            HP1.Core.X87Ext nowX = R64(HP1.Core.X87Ext.From(_clockFixed) * X87(1f / 4294967296f));
            float now = nowX.ToFloat();
            Vector3 noiseLoc = ActorLocation(self);

            // Suppressed when a slot is fresh (0.2 s), close (50 uu) AND not
            // quieter than 0.9 of the new noise — all three at once.
            // Freshness: `fsub dword 0.2f` on the stack, `fcom time; test ah,1` —
            // (now − 0.2f) < time; proximity: dist² < 2500f (0x10478e74).
            HP1.Core.X87Ext nowMinus02 = R64(nowX - X87(0.2f));
            bool Suppresses(string time, string spot, string loud)
                => nowMinus02.CompareTo(X87(UEInterpreter.AsFloat(noisePawn.GetProp(time)))) < 0
                   && DistSq87(UEInterpreter.AsVector3(noisePawn.GetProp(spot)), noiseLoc)
                        .CompareTo(X87(2500f)) < 0
                   && LoudSuppresses(UEInterpreter.AsFloat(noisePawn.GetProp(loud)), loudness);
            if (Suppresses("noise1time", "noise1spot", "noise1loudness")) return;
            if (Suppresses("noise2time", "noise2spot", "noise2loudness")) return;

            void Record(string slot)
            {
                noisePawn.Props[slot + "time"] = now;
                noisePawn.Props[slot + "spot"] = noiseLoc;
                noisePawn.Props[slot + "loudness"] = loudness;
                Journal?.Invoke($"noise source={self.GetProp(PK.Name)} "
                    + $"instigator={noisePawn.GetProp(PK.Name)} slot={slot} "
                    + $"loudness={loudness:R} time={now:R} location={noiseLoc}");
            }
            float noise1Time = UEInterpreter.AsFloat(noisePawn.GetProp(PK.noise1time));
            float noise2Time = UEInterpreter.AsFloat(noisePawn.GetProp(PK.noise2time));
            // Asymmetric on purpose (Engine.dll.c:90654-90693): slot 2 is only
            // ever overwritten when IT is the stale one; every other path lands
            // on slot 1, and one path writes nothing at all. The staleness test
            // is STRICT — shipped asks `now - 0.18 <= noiseTime` for FRESH, so a
            // slot exactly on the boundary counts as fresh and is kept.
            // `now − 0.18f` is also on the stack (`fsub dword [0x10479e40]`, 0x103db145).
            HP1.Core.X87Ext nowMinus018 = R64(nowX - X87(0.18f));
            // Loudness: `fld slot; fcomp L; test ah,0x41` — write when
            // slot ≤ L or the comparison is unordered (0x103db20e, 0x103db253).
            bool NotLouder(float slot) => !(slot > loudness);
            if (nowMinus018.CompareTo(X87(noise1Time)) > 0) Record("noise1");
            else if (nowMinus018.CompareTo(X87(noise2Time)) > 0) Record("noise2");
            else if (DistSq87(UEInterpreter.AsVector3(noisePawn.GetProp(PK.noise1spot)), noiseLoc)
                         .CompareTo(X87(2500f)) < 0
                     && NotLouder(UEInterpreter.AsFloat(noisePawn.GetProp(PK.noise1loudness))))
                Record("noise1");
            else if (NotLouder(UEInterpreter.AsFloat(noisePawn.GetProp(PK.noise2loudness))))
                Record("noise1");

            // Two delivery loops over the same PawnList. Which one runs depends
            // on the instigator (and its Enemy) being a player, and they differ
            // by MORE than the player filter — see the class gate below.
            UEObject enemy = noisePawn.GetProp(PK.Enemy) as UEObject;
            bool classGated = !PropBool(noisePawn, "bIsPlayer")
                              && (enemy is null || !PropBool(enemy, "bIsPlayer"));
            for (UEObject pawn = level?.GetProp(PK.PawnList) as UEObject;
                 pawn is not null; pawn = pawn.GetProp(PK.nextPawn) as UEObject)
            {
                if (!classGated && UEInterpreter.ClassIsA(pawn.Class, "PlayerPawn")) continue;
                if (ReferenceEquals(pawn, noisePawn)) continue;
                // Shipped UObject::IsProbing(HearNoise) (90726/90766, disasm
                // 0x103db349/0x103db4c9) — the probe-mask bit of the state frame
                // (`ignores`, Disable), not the presence of the function; then ProcessEvent
                // with its own gate.
                if (!UEInterpreter.IsProbing(pawn, "HearNoise")) continue;
                if (classGated && !ClassesRelated(pawn, self)) continue;
                if (!PawnCanHear(interp, pawn, noiseLoc, loudness, noisePawn)) continue;
                UEObject listener = pawn;
                UELifecycle.Guard(() => UELifecycle.CallEvent(interp, listener, "HearNoise", loudness, self), "HearNoise");
            }
        }

        // Engine.dll.c:90714-90723 — the listener passes only when one of the
        // two classes descends from the other. Confirmed against the real body
        // under the emulator, because it reads like a decompiler artefact and
        // is not: a non-Pawn noise source (a door, a prop) is heard by NOBODY on
        // this branch, since no such class is related to Pawn. Surprising, and
        // shipped. Do not "fix" it.
        static bool ClassesRelated(UEObject a, UEObject b)
        {
            if (a?.Class is null || b?.Class is null) return false;
            return UEInterpreter.ClassIsA(a.Class, b.Class.Name?.ToString())
                || UEInterpreter.ClassIsA(b.Class, a.Class.Name?.ToString());
        }

        // APawn::CanHear (Engine.dll.c:68748). The plain range is 2000*Loudness
        // (Loudness^2 * 4e6 against DistSquared); a range of 4000
        // exists only in the team-mate branch, which needs a team game
        // and two players and is therefore unreachable in HP1.
        public bool PawnCanHear(UEInterpreter interp, UEObject listener,
            Vector3 noiseLoc, float loudness, UEObject other)
        {
            // A single rounding to float: `fstp dword [ebp-0x14]` (0x103dacff).
            float distSq = DistSq87(ActorLocation(listener), noiseLoc).ToFloat();
            bool reject;
            if (PropBool(listener, "bIsPlayer")
                && interp.GetLevelInfo()?.GetProp(PK.Game) is UEObject game
                && PropBool(game, "bTeamGame")
                && PropBool(other, "bIsPlayer")
                && listener.GetProp(PK.PlayerReplicationInfo) is UEObject mine
                && other.GetProp(PK.PlayerReplicationInfo) is UEObject theirs
                && UEInterpreter.ToInt(mine.GetProp(PK.Team))
                   == UEInterpreter.ToInt(theirs.GetProp(PK.Team)))
            {
                // Team-mates: a far wider range, and NO Stimulus write.
                reject = LoudSqBelow(loudness, 16000000f, distSq);
            }
            else
            {
                if (LoudSqBelow(loudness, 4000000f, distSq)) return false;
                // Standing exactly on the noise is heard without a Stimulus
                // computation and without a trace.
                if (distSq == 0f) return true;
                // 0x103dae25..0x103dae7d: the quotient on the stack is compared with 2.0
                // UNrounded, but Stimulus gets its float copy (`fst dword
                // [ebp+0x18]`, then `fld dword [ebp+0x18]`); the sum
                // alert·Alertness + scale·L is one chain, `fst dword` into
                // Stimulus, while the threshold is compared with the UNrounded sum.
                float alertness = UEInterpreter.AsFloat(listener.GetProp(PK.Alertness));
                float threshold = UEInterpreter.AsFloat(listener.GetProp(PK.HearingThreshold));
                HP1.Core.X87Ext quotient = HP1.Core.X87Ext.Div64(X87(1200000f), X87(distSq));
                float scale, alert;
                if (quotient.CompareTo(X87(2f)) > 0) { scale = 2f; alert = 0.5f; }
                else
                {
                    scale = quotient.ToFloat();
                    alert = 0.5f <= scale ? 0.5f : scale;
                }
                if (!float.IsFinite(loudness) || !float.IsFinite(alertness))
                {
                    double wide = (double)alert * alertness + (double)scale * loudness;
                    listener.Props["Stimulus"] = (float)wide;
                    reject = !(wide >= threshold);
                }
                else
                {
                    HP1.Core.X87Ext stimulus = R64(R64(X87(alert) * X87(alertness))
                                                 + R64(X87(scale) * X87(loudness)));
                    // The SAME field the sight queries write.
                    listener.Props["Stimulus"] = stimulus.ToFloat();
                    // `fcomp HearingThreshold; test ah,1` (via 0x103dadc7): C0 = reject.
                    reject = stimulus.CompareTo(X87(threshold)) < 0;
                }
            }
            if (reject) return false;
            return SightLineClear(noiseLoc, ActorLocation(listener));
        }

        // ── PAWN SIGHT IN AActor::Tick: SightCounter, ShowSelf, CheckEnemyVisible ──
        //
        // Disasm 0x103b450e..0x103b45a1 (Ghidra: Engine.dll.c:128770-128785); the
        // branch runs when Role == ROLE_Authority && TickType == LEVELTICK_All
        // (0x103b44fa/0x103b4504), AFTER eyes/weapon and BEFORE PainTime (0x103b45a1):
        //     if (SightCounter < 0) SightCounter += 0.2;          // 0x104737f0
        //     SightCounter -= DeltaSeconds;
        //     if (bIsPlayer && !bHidden) ShowSelf();              // +0x25c&2, +0x28&2
        //     if (SightCounter < 0 && IsProbing(EnemyNotVisible)) // Names[0xee]
        //     {   CheckEnemyVisible(); SightCounter = 0.1; }      // 0x3dcccccd
        // The 0.1 assignment is INSIDE the probe gate (je 0x103b45a1 skips both).
        // Comparisons are `fcomp; test ah,1` (bit C0): an unordered comparison
        // (NaN) also gives C0 = 1, so "less" here means "not ≥".
        //
        // The counter is re-read AFTER ShowSelf (0x103b455b): another pawn's SeePlayer
        // may overwrite it.
        //
        // Observable in the campaign: HP data probes neither SeePlayer (bit 40) nor
        // EnemyNotVisible (bit 41) in any shipped class or state mask (Pawn 0xf8040c02;
        // a body-less `event SeePlayer(..);` declaration does not set the bit), and
        // execEnable restores only bits the class/state defines. So with a live state
        // frame both gates are false and the visible trace of the branch is SightCounter
        // itself (also saved). The branch is ported in full because its side effects
        // (bLOSflag/Stimulus/appFrand inside LineOfSightTo) occur exactly when the gate is open.
        internal void PawnSightTick(UEInterpreter interp, UEObject pawn, float dt)
        {
            string key = UEObject.PropKey(pawn.Class, "Pawn", "SightCounter");
            float counter = UEInterpreter.AsFloat(pawn.GetProp(key));
            if (X87Below(counter, 0f)) counter += 0.2f;
            counter -= dt;
            pawn.Props[key] = counter;

            if (UEInterpreter.ToBool(pawn.GetDeclared("Pawn", "bIsPlayer"))
                && !UEInterpreter.ToBool(pawn.GetDeclared("Actor", "bHidden")))
                ShowSelf(interp, pawn);

            if (X87Below(UEInterpreter.AsFloat(pawn.GetProp(key)), 0f)
                && UEInterpreter.IsProbing(pawn, "EnemyNotVisible"))
            {
                CheckEnemyVisible(interp, pawn);
                pawn.Props[key] = 0.1f;
            }
        }

        // `fcomp m32; fnstsw ax; test ah,1` — branch on C0: true for
        // a < b AND for an unordered comparison.
        static bool X87Below(float a, float b) => !(a >= b);

        // APawn::ShowSelf — Engine.dll.c:156239, disasm 0x103db929..0x103db9ac.
        // Walks XLevel->Actors(0)->PawnList (+0x498) via nextPawn (+0x3f8);
        // the next one is taken AFTER the event. For every OTHER pawn with
        // SightCounter < 0 and IsProbing(SeePlayer) (Names[0xed] = bit 40):
        // LineOfSightTo(this, 1) (push 1, thunk 0x10302842 -> 0x103da070) and
        // eventSeePlayer(this).
        void ShowSelf(UEInterpreter interp, UEObject self)
        {
            UEObject level = interp.GetLevelInfo();
            for (UEObject other = level?.GetProp(PK.PawnList) as UEObject; other is not null;
                 other = other.GetDeclared("Pawn", "nextPawn") as UEObject)
            {
                if (ReferenceEquals(other, self)) continue;
                if (!X87Below(UEInterpreter.AsFloat(other.GetDeclared("Pawn", "SightCounter")), 0f)) continue;
                if (!UEInterpreter.IsProbing(other, "SeePlayer")) continue;
                if (!PawnLineOfSightTo(other, self, checkFov: true)) continue;
                UELifecycle.CallEvent(interp, other, "SeePlayer", self);
            }
        }

        // APawn::CheckEnemyVisible — Engine.dll.c:13449-13484. With an Enemy:
        // LineOfSightTo(Enemy, 0); not visible -> eventEnemyNotVisible, visible ->
        // LastSeenTime = XLevel->TimeSeconds (FTime +0xdc, 32.32 * 2^-32 —
        // exactly the port's level clock, not a copy of LevelInfo.TimeSeconds).
        // check(Enemy->IsValid()) (UnPawn.cpp:0x736) is unreachable in the port:
        // a destroyed actor stays an object until garbage collection.
        void CheckEnemyVisible(UEInterpreter interp, UEObject self)
        {
            if (self.GetDeclared("Pawn", "Enemy") is not UEObject enemy) return;
            if (!PawnLineOfSightTo(self, enemy, checkFov: false))
                UELifecycle.CallEvent(interp, self, "EnemyNotVisible");
            else
                self.SetDeclared("Pawn", "LastSeenTime", LevelClockSeconds);
        }

        static int VisibilityOf(UEObject actor)
            => UEInterpreter.ToInt(actor.GetProp(PK.Visibility)) & 0xff;

        // FVector::SafeNormal — one authority, UEVector.normalized (a float32 copy
        // would differ: Core.dll keeps the square sum and the reciprocal
        // wider than float32, 0x1014f4e6+).
        static Vector3 SafeNormalShipped(Vector3 v) => v.normalized;

        public object LineOfSightToNative(
            UEInterpreter interp, UEFrame frame, object[] args)
            => PawnLineOfSightTo(frame.Context ?? frame.Object,
                args.Length > 0 ? args[0] as UEObject : null, checkFov: false);

        public object CanSeeNative(
            UEInterpreter interp, UEFrame frame, object[] args)
            => PawnLineOfSightTo(frame.Context ?? frame.Object,
                args.Length > 0 ? args[0] as UEObject : null, checkFov: true);

        // AActor::TestCanSeeMe(PlayerPawn) — the per-viewer half of
        // PlayerCanSeeMe.  Note the range test is NOT a squared radius: the
        // shipped comparison is DistSquared < (CollisionRadius + 3.6) * 100000.
        bool TestCanSeeMe(UEObject self, UEObject player)
        {
            if (player is null) return false;
            if (ReferenceEquals(player.GetProp(PK.ViewTarget) as UEObject, self))
                return true;

            // x87 chains per disasm 0x103d6116-0x103d6209 (PC64, exact X87Ext model;
            // double does NOT work here: (20 + 3.6)·1e5 in double is exactly 2360000, but in
            // the 64-bit register it is 2360000 + 8.9e-12):
            //  * dist² — differences on the stack, (dx² + dy²) + dz², one write to float;
            //  * limit (CollisionRadius + 3.6)·100000 — QWORD constants
            //    (0x10479de0 = 3.6, 0x10479dd0 = 1e5), in the register; continue only when
            //    limit > dist² (`test ah,0x41; jne` — equality and NaN exit);
            //  * cone — differences already in float (fstp dword), dot = (X.x·dx + X.y·dy)
            //    + X.z·dz and dot² in the register against dist²·0.25f (fmul dword 0.25 —
            //    exact); exit when 0.25·dist² > dot² (NaN passes on).
            Vector3 selfLoc = ActorLocation(self), playerLoc = ActorLocation(player);
            float radius = UEInterpreter.AsFloat(self.GetProp(PK.CollisionRadius));
            Vector3 delta = selfLoc - playerLoc;
            Vector3 f = ActorForward(player, "ViewRotation");
            bool behind = PropBool(player, "bBehindView");
            if (!float.IsFinite(radius) || !float.IsFinite(delta.x) || !float.IsFinite(delta.y)
                || !float.IsFinite(delta.z))
            {
                // Non-finite inputs: same order in double (inf/NaN behave as in x87).
                double rx = (double)selfLoc.x - playerLoc.x, ry = (double)selfLoc.y - playerLoc.y,
                       rz = (double)selfLoc.z - playerLoc.z;
                float dsq = (float)((rx * rx + ry * ry) + rz * rz);
                if (!(((double)radius + 3.6) * 100000.0 > dsq)) return false;
                double d = ((double)f.x * delta.x + (double)f.y * delta.y) + (double)f.z * delta.z;
                if (!behind && dsq * 0.25f > d * d) return false;
                return PawnLineOfSightTo(player, self, checkFov: false);
            }
            float distSq = DistSq87(selfLoc, playerLoc).ToFloat();
            HP1.Core.X87Ext limit = R64(R64(X87(radius) + HP1.Core.X87Ext.From(3.6))
                                        * HP1.Core.X87Ext.From(100000.0));
            if (limit.CompareTo(X87(distSq)) <= 0) return false;

            if (!behind)
            {
                // Cone against the player's ViewRotation, not its Rotation.
                HP1.Core.X87Ext dot = R64(R64(R64(X87(f.x) * X87(delta.x)) + R64(X87(f.y) * X87(delta.y)))
                                          + R64(X87(f.z) * X87(delta.z)));
                if (X87(distSq * 0.25f).CompareTo(R64(dot * dot)) > 0) return false;
            }
            return PawnLineOfSightTo(player, self, checkFov: false);
        }

        public object PlayerCanSeeMeNative(
            UEInterpreter interp, UEFrame frame, object[] args)
        {
            UEObject self = frame.Context ?? frame.Object;
            if (self is null) return false;
            UEObject level = interp.GetLevelInfo();
            int netMode = UEInterpreter.ToInt(level?.GetProp(PK.NetMode));
            if (netMode != 0)
                throw new NotSupportedException(
                    "PlayerCanSeeMe: shipped walks the PawnList only for NetMode!=0 "
                    + "(Engine.dll.c:97495-97510); this port runs standalone, NetMode="
                    + netMode);
            // Standalone shipped path: the client's viewports, each viewport's
            // Actor.  Core owns exactly one local viewport.
            if (Viewport is null)
                throw new InvalidOperationException(
                    "PlayerCanSeeMe reached before the local viewport exists");
            return TestCanSeeMe(self, Viewport.GetProp(PK.Actor) as UEObject);
        }

        // Shared aim/range/LOS test of both target pickers
        // (Engine.dll.c:106419-106436 and 159771-159788).
        bool ConsiderTarget(UEObject self, UEObject candidate, Vector3 fireDir,
            Vector3 projStart, ref float bestAim, ref float bestDist,
            ref UEObject best)
        {
            // x87 chains (disasm 0x103d66ed..0x103d67cb / 0x103d6a2e..0x103d6b10):
            // differences on the stack; aim = (F.z·dz + F.y·dy) + F.x·dx is compared with
            // 0 unrounded, stored to float via `fst dword`; dist² = (dz² + dy²) + dx²
            // against 4e6 on the stack, appSqrt(double) = fsqrt in PC64, `fstp dword`;
            // the quotient aim_f / dist_f is compared with bestAim UNrounded — this
            // decides the tie-break between nearly equal candidates.
            Vector3 at = ActorLocation(candidate);
            HP1.Core.X87Ext dx = R64(X87(at.x) - X87(projStart.x));
            HP1.Core.X87Ext dy = R64(X87(at.y) - X87(projStart.y));
            HP1.Core.X87Ext dz = R64(X87(at.z) - X87(projStart.z));
            HP1.Core.X87Ext aimX = R64(R64(R64(X87(fireDir.z) * dz) + R64(X87(fireDir.y) * dy))
                                       + R64(X87(fireDir.x) * dx));
            if (aimX.Sign <= 0) return false;
            float aimF = aimX.ToFloat();
            HP1.Core.X87Ext distSq = R64(R64(R64(dz * dz) + R64(dy * dy)) + R64(dx * dx));
            if (distSq.CompareTo(X87(4000000f)) >= 0) return false;     // 2000 uu
            float dist = HP1.Core.X87Ext.Sqrt64(HP1.Core.X87Ext.From(distSq.ToDouble())).ToFloat();
            HP1.Core.X87Ext aim = HP1.Core.X87Ext.Div64(X87(aimF), X87(dist));
            // `fcomp [bestAim]; test ah,0x41`: C0|C3 = not better (incl. a NaN threshold).
            bool notBetter = float.IsFinite(bestAim)
                ? aim.CompareTo(X87(bestAim)) <= 0
                : !float.IsNegativeInfinity(bestAim);
            if (notBetter) return false;
            if (!PawnLineOfSightTo(self, candidate, checkFov: false)) return false;
            bestAim = aim.ToFloat();
            bestDist = dist;
            best = candidate;
            return true;
        }

        public object PickTargetNative(
            UEInterpreter interp, UEFrame frame, object[] args)
        {
            UEObject self = frame.Context ?? frame.Object;
            if (self is null) return null;
            float bestAim = args.Length > 0 ? UEInterpreter.AsFloat(args[0]) : 0f;
            float bestDist = args.Length > 1 ? UEInterpreter.AsFloat(args[1]) : 0f;
            Vector3 fireDir = UEInterpreter.AsVector3(args.Length > 2 ? args[2] : null);
            Vector3 projStart = UEInterpreter.AsVector3(args.Length > 3 ? args[3] : null);

            UEObject level = interp.GetLevelInfo();
            UEObject best = null;
            for (UEObject pawn = level?.GetProp(PK.PawnList) as UEObject;
                 pawn is not null; pawn = pawn.GetProp(PK.nextPawn) as UEObject)
            {
                if (ReferenceEquals(pawn, self)) continue;
                if (UEInterpreter.ToInt(pawn.GetProp(PK.Health)) <= 0) continue;
                if (!PropBool(pawn, "bProjTarget")) continue;
                if (SameTeam(level, self, pawn)) continue;
                ConsiderTarget(self, pawn, fireDir, projStart,
                    ref bestAim, ref bestDist, ref best);
            }
            if (args.Length > 0) args[0] = bestAim;
            if (args.Length > 1) args[1] = bestDist;
            return best;
        }

        // Team-mates are skipped only when BOTH pawns carry a
        // PlayerReplicationInfo and the running game declares bTeamGame
        // (Engine.dll.c:106402-106417).  HP1 ships no team game, so this is
        // inert here — it is ported because the shipped branch exists, not
        // because a call site needs it.
        static bool SameTeam(UEObject level, UEObject self, UEObject other)
        {
            if (self.GetProp(PK.PlayerReplicationInfo) is not UEObject mine) return false;
            if (other.GetProp(PK.PlayerReplicationInfo) is not UEObject theirs) return false;
            if (level?.GetProp(PK.Game) is not UEObject game) return false;
            if (!PropBool(game, "bTeamGame")) return false;
            return UEInterpreter.ToInt(mine.GetProp(PK.Team))
                == UEInterpreter.ToInt(theirs.GetProp(PK.Team));
        }

        public object PickAnyTargetNative(
            UEInterpreter interp, UEFrame frame, object[] args)
        {
            UEObject self = frame.Context ?? frame.Object;
            if (self is null) return null;
            float bestAim = args.Length > 0 ? UEInterpreter.AsFloat(args[0]) : 0f;
            float bestDist = args.Length > 1 ? UEInterpreter.AsFloat(args[1]) : 0f;
            Vector3 fireDir = UEInterpreter.AsVector3(args.Length > 2 ? args[2] : null);
            Vector3 projStart = UEInterpreter.AsVector3(args.Length > 3 ? args[3] : null);

            UEObject best = null;
            // Level.Actors in index order, and Pawns are the ones SKIPPED here
            // (PickTarget already owns them).
            for (int i = 0; i < Actors.Count; i++)
            {
                UEObject actor = Actors[i];
                if (actor?.Class is null || actor.Destroyed) continue;
                if (!PropBool(actor, "bProjTarget")) continue;
                if (IsPawn(actor)) continue;
                ConsiderTarget(self, actor, fireDir, projStart,
                    ref bestAim, ref bestDist, ref best);
            }
            if (args.Length > 0) args[0] = bestAim;
            if (args.Length > 1) args[1] = bestDist;
            return best;
        }
    }
}
