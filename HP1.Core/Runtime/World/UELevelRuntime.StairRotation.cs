// APawn::FindStairRotation — native 524, Core-owned.
//
// REACHABILITY. Measured:
//
//   * the call is in the ENGINE body `PlayerPawn.PlayerWalking.PlayerMove`, and HP1
//     OVERRIDES this state in `Harry` (HarryPotter/Harry.uc) with no `__NFUN_524__`
//     in the override. The only direct PlayerPawn subclass, `baseHarry`, does not
//     override the state;
//   * the call is additionally gated by `bLookUpStairs`, which is in neither
//     (the call also sits under `bLookUpStairs`, which is in neither PlayerPawn
//     defaultproperties nor any shipped ini, so it is false by default). Direct
//     measurement: 3000 ticks of a Tut1 segment — ZERO hits.
//
// So it is reachable for PlayerPawn, not for the HP1 player. It must still be
// registered: the branch is off, not bodiless, and the first case reaching it
// would otherwise be a fatal STOP via OnUnknownNative.
//
// SOURCE — SHIPPED ONLY: the body is read from the shipped binary.
//
// BODY ADDRESS FROM THE THUNK, NOT THE EXPORT. Export 2262
// (`?execFindStairRotation@APawn@@QAEXAAUFFrame@@QAX@Z`) points to
// `0x10303111`, which is a THUNK: `jmp 0x103d7020`. The body is
// `0x103d7020..0x103d7709` (Engine.dll ImageBase = 0x10300000). Taking the export
// VA for the body yields "the function does nothing".
//
// CHECKED BY DISASM, NOT PSEUDOCODE (V11 — negative and structural claims
// from a decompile alone are not allowed):
//
//   * early exit on `DeltaTime > 0.33f` — constant 0x3ea8f5c3 is EXACTLY 0.33
//     (0x103d7059); the pitch is then returned UNCHANGED;
//   * pitch normalisation is WRITTEN BACK to `ViewRotation.Pitch`
//     (`mov [edi], eax`, 0x103d709e) — a side effect of the native, not a local,
//     and the pseudocode does not read as a store;
//   * the basis is `GMath.UnitCoords /= FRotator(0, Yaw, Roll)` (copy of 12
//     dwords from `GMath+0x18` to `[ebp-0xc8]`, then `FCoords::operator/=`), and
//     the traces follow its X axis — the triple `[ebp-0xbc]/[ebp-0xb8]/[ebp-0xb4]`
//     is X (Origin takes the first three slots from `[ebp-0xc8]`);
//   * the `else` belongs to the `drop >= dist*0.7 - 6.0` check, NOT the outer
//     condition: `jne 0x103d7481` with (dist*0.7-6) <= drop goes to the DESCENT
//     branch, fall-through is ASCENT. The pseudocode nesting read ambiguously;
//   * `8000 / |diff|` is a 32-bit SIGNED `idiv` (0x103d76b6), not the 64-bit
//     division Ghidra draws;
//   * the `0.8` threshold is a QWORD, i.e. double (`fmul qword ptr [0x10479df0]`,
//     value 0.8), while 0.7/0.9/3.0/6.0/8.0/10.0/1.0 are float.
//
// FIELD OFFSETS MEASURED against the shipped Pawn layout:
// 0x33c/0x340/0x344 = `Pawn.ViewRotation` (Pitch/Yaw/Roll), 0x354 =
// `Pawn.BaseEyeHeight` (not MaxStepHeight), 0x1cc = `Actor.CollisionRadius`,
// 0x1d4 = `Actor.CollisionHeight`, 0xfc = `Actor.Location`.
using System;
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        // Target view tilt pitches: 0x1518 = +5400 (ascent), 0xffffec78 =
        // -5000 (descent). Both are literals of the shipped body (0x103d7475, 0x103d7682).
        private const int StairPitchUp = 5400;
        private const int StairPitchDown = -5000;

        public int FindStairRotation(UEInterpreter interp, UEObject pawn, float deltaTime)
        {
            if (pawn is null) return 0;
            UERotator view = UEInterpreter.AsRotator(pawn.GetProp(PK.ViewRotation));

            // 0x103d7059: a frame longer than a third of a second does not recompute
            // the tilt at all. Not an optimisation: with a large step the interpolation
            // below would overshoot the target, and shipped prefers NOT to touch the view.
            if (deltaTime > 0.33f) return view.Pitch;

            // 0x103d707e-0x103d709e: pitch brought to the signed range, WRITTEN
            // BACK to the actor. The comparison is signed (`cmp eax, 0x8000`
            // + `jle`), so negative values pass through.
            int pitch = view.Pitch;
            if (pitch > 0x8000)
            {
                pitch = (pitch & 0xffff) - 0x10000;
                pawn.Props["ViewRotation"] = new UERotator(pitch, view.Yaw, view.Roll);
            }

            // 0x103d70cc-0x103d7110: the basis is built WITHOUT pitch — probes are
            // horizontal, otherwise the view tilt would feed itself.
            BrushCoords coords = BrushCoords.Identity;
            coords.DivRotator(0, view.Yaw, view.Roll);
            Vector3 forward = coords.X;

            float eye = UEInterpreter.AsFloat(pawn.GetProp(PK.BaseEyeHeight));
            float radius = UEInterpreter.AsFloat(pawn.GetProp(PK.CollisionRadius));
            float height = UEInterpreter.AsFloat(pawn.GetProp(PK.CollisionHeight));
            Vector3 location = UEInterpreter.AsVector3(pawn.GetProp(PK.Location));

            // 0x103d7152-0x103d717e: dist = CollisionHeight + BaseEyeHeight,
            // extent = (Radius, Radius, 1.0) — Z is deliberately one, not the
            // height: the probe feels the step, not the pawn's size.
            float dist = height + eye;
            Vector3 extent = new Vector3(radius, radius, 1f);
            Vector3 eyePoint = new Vector3(location.x, location.y, location.z + eye);

            // Probe 1 (0x103d7235): forward by two dist from the eye point.
            float t1 = SingleLineCheck(interp, pawn, pawn,
                eyePoint, eyePoint + forward * (dist + dist), extent, 6).Time;

            int target = 0;
            float ahead = t1 * dist + t1 * dist;          // 0x103d7248: fadd st,st

            // 0x103d7250-0x103d7260: the 0.8 factor is a DOUBLE and the comparison is
            // in x87 extended precision. Do not rewrite it in float: shipped runs a
            // 64-bit mantissa (appEnableFastMath(0)).
            if ((double)dist * 0.8 < ahead)
            {
                float drop3 = dist * 3f;

                // Probe 2 (0x103d732d): down from the middle of the covered segment.
                Vector3 mid = eyePoint + forward * (ahead * 0.5f);
                float t2 = SingleLineCheck(interp, pawn, pawn,
                    mid, new Vector3(mid.x, mid.y, mid.z - drop3), extent, 6).Time;

                // 0x103d7341: no floor ahead at all — the tilt stays zero.
                if (t2 < 1f)
                {
                    float drop = drop3 * t2;
                    Vector3 far = eyePoint + forward * ahead;
                    Vector3 farDown = new Vector3(far.x, far.y, far.z - drop3);

                    if (dist * 0.7f - 6f > drop)
                    {
                        // ASCENT (0x103d736f): the floor ahead is HIGHER than expected.
                        float t3 = SingleLineCheck(interp, pawn, pawn,
                            far, farDown, extent, 6).Time;
                        target = pitch > 0 ? pitch : 0;          // setle/dec/and
                        if (drop3 * t3 < drop - 10f) target = StairPitchUp;
                    }
                    else if (dist * 0.7f + 6f < drop)
                    {
                        // DESCENT (0x103d7481). First a CLEARANCE trace with zero extent
                        // and FROM Location itself, not the eye point (0x103d7562:
                        // the start is esi = &Location, set at 0x103d7131 and not
                        // reassigned on this path).
                        Vector3 clearEnd = location + forward * (ahead * 0.9f);
                        float clear = SingleLineCheck(interp, pawn, pawn,
                            location, clearEnd, Vector3.zero, 6).Time;
                        // 0x103d7576: the downward tilt is taken only with a CLEAR
                        // clearance — when blocked, the view is not lowered.
                        if (clear == 1f)
                        {
                            float t4 = SingleLineCheck(interp, pawn, pawn,
                                far, farDown, extent, 6).Time;
                            target = pitch < 0 ? pitch : 0;      // setge/dec/and
                            if (drop + 10f < drop3 * t4) target = StairPitchDown;
                        }
                    }
                    // Between -6 and +6 around dist*0.7 the target stays zero: no
                    // step, the view levels out.
                }
            }

            // Tail 0x103d7689: interpolation toward the target. The rate is higher the
            // SMALLER the remainder — but only below 1000; above that, 8.0.
            int diff = pitch - target;
            if (diff < 0) diff = -diff;
            if (diff > 0)
            {
                float rate = diff < 1000 ? (int)(8000 / diff) : 8f;
                rate *= deltaTime;
                if (rate >= 1f) rate = 1f;
            // `fistp` — round to NEAREST (default x87 mode),
            // i.e. MidpointRounding.ToEven, not truncation.
                target = (int)MathF.Round(target * rate + (1f - rate) * pitch,
                    MidpointRounding.ToEven);
            }
            return target;
        }
    }
}
