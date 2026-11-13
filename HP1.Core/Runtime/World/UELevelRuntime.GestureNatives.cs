// UGesture spell-pattern scoring — the spell-lesson judge.
//
// Source of truth: the shipped Engine.dll MACHINE CODE, read by disassembly.
// Ghidra loses both exec bodies (each reserves a ~16 KB stack frame and the
// decompile degenerates into `unaff_ESI` / `register0x00000010` / dropped call
// arguments), so every number below was read from the instruction stream:
//
//   UGesture::execCompareGesture       body 0x103a3f60 (thunk 0x103035ee, ord 2226)
//   UGesture::execCompareGesturePoint  body 0x103a3cc0 (thunk 0x10301244, ord 2227)
//   shared scorer FUN_103a39c0         body 0x103a39c0 (thunk 0x1030342c)
//
// Constants read out of .rdata, not retyped from the decompile:
//   0x10476584 float  5.0     penalty divisor scale (pass 1)
//   0x1046dd9c float  1.0     penalty bias (pass 1) / upper clamp return
//   0x1046d598 float  0.0     lower clamp return
//   0x10474578 double 1.0     `1.0 - penalty/total` and the upper clamp
//   0x10471b98 double 0.0     the lower clamp comparand
//   0x104765f8 double 0.001   drawn-point dedup epsilon — SHIPPED ONLY: this
//                             port has no branch using it, see GestureDedupDrawn
//   0x104765e8..0x10476588 double 0.125 .. 0.875  densify fractions
//   0x104765e4 float  0.125   the ONE float32 fraction, in the Point body only
//
// Two shipped details that reading the C decompile would have missed and that
// no amount of "looks right" would recover:
//   * the densified template count handed to the scorer is `m*8 - 7`: the LAST
//     template point contributes only itself, not 8 entries (0x103a41ce
//     `lea eax,[edi*8-7]`, and the write loop skips the 7 lerps on the last i);
//   * CompareGesturePoint EVALUATES its `accuracy` argument and then never
//     reads it — the returned value is the raw minimum distance (0x103a3e44
//     onwards has no reference to the accuracy slot).
using System;
using System.Collections;
using System.Collections.Generic;
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        // Both exec bodies clamp their point counts to 0x400 BEFORE looping
        // (0x103a3fed / 0x103a409d / 0x103a3d2d): the buffers are fixed-size
        // stack arrays, and a longer array is truncated, not rejected.
        const int GestureMaxPoints = 0x400;

        // ...BUT THE 0x400 CLAMP DOES NOT PROTECT EVERYTHING. The densified template
        // buffer occupies [esp+0x28, esp+0x2028) in the frame — 1024 slots of 8 bytes —
        // and the densify loop (0x103a40b7 `lea ecx,[esp+0x2c]`, `add ecx,8` per point)
        // writes m*8-7 slots without any bound check. Exactly m <= 128 fits;
        // measured on shipped: 128 template points give 0.4377880096, while 129
        // give 0.6610058546, because the extra slot lands on the first DRAWN point,
        // which the scorer then reads. Beyond that the write leaves the frame
        // entirely and corrupts the caller's stack.
        //
        // Campaign data never gets there: the longest shipped pattern is
        // AlohoPattern, 32 points (checked with `--gesturepatterns` over all six).
        // So the port does not reproduce the buffer overlap but STOPS LOUDLY:
        // silently returning a "reasonable" number would pass off as original
        // something the original does not do.
        const int GestureTemplateFitsFrame = 128;

        // 0x103a40ce onward: seven lerps per source point, each computed as
        // (next - cur) * f + cur with the fraction taken from .rdata as a
        // double. Order matters — this is the array (CompareGesture) body.
        static readonly double[] GestureDensifyFractions =
            { 0.125, 0.25, 0.375, 0.5, 0.625, 0.75, 0.875 };

        static IList GesturePointsOf(UEObject gesture)
        {
            var pts = gesture?.GetProp(PK.Points) as IList;
            if (pts != null && pts.Count > GestureTemplateFitsFrame)
                throw new NotSupportedException(
                    $"CompareGesture: pattern has {pts.Count} points; the shipped densify " +
                    $"overruns its own {GestureTemplateFitsFrame}-point stack buffer past this " +
                    "and corrupts the drawn set (and beyond ~500 the caller's stack). " +
                    "No shipped pattern exceeds 32 — this input is outside what the original does.");
            return pts;
        }

        // execCompareGesture 0x103a401b-0x103a4097 — building the set of
        // drawn points.
        //
        // The first filter is unambiguous: z == -1.0f exactly (0x103a401b, compare with
        // 0xbf800000) — the slot is unused; the script marks empty places with it.
        //
        // THE SECOND FILTER IS AN ORIGINAL DEFECT: it reads UNINITIALISED memory.
        // The inner loop (0x103a402c-0x103a4077) runs j from 0 to the number of
        // points already saved, but both comparisons read `[edx-4]` and `[edx]`,
        // where edx is the WRITE CURSOR; the `add edx,8` increment is only on the
        // save branch (0x103a4088), and the pointer is not indexed by j at all.
        // So a candidate is compared not with a saved point but with the slot
        // 8 bytes AHEAD of the last written one — whatever the process history left
        // in that memory. In general the result is not a function of the arguments.
        //
        // WHAT THIS MEANS FOR THE PORT — AND WHAT IS NOT PROVEN HERE (STOP).
        //
        // The port DECLARES an assumption: the slot under the write cursor matches no
        // candidate, i.e. the discard branch never fires. This is an ASSUMPTION, not a
        // measured fact: the port does not and cannot have the second input shipped
        // really depends on.
        //
        // NOT PROVEN: what frame state the REAL game process leaves before the first
        // CompareGesture. There is no live capture (shipped is a 32-bit Windows
        // binary that does not run on this machine), and stack contents cannot be
        // derived statically. Call order in the lesson: Draw.Tick does not call
        // native(427) at all, it only writes DrawnPoints and does GotoState('Judge')
        // (SpellLearnTrigger.uc:635, :643); label Judge:Begin calls native(426) ONCE
        // (:757), and only then Judge.Tick starts calling native(427) (:705). The first
        // 426 of an attempt has no 427 before it.
        //
        // MEASURED (against the original body, 144 cases, none excluded): under a
        // CONSTRUCTED IMMEDIATE-REPEAT RESIDUE — a second call of the same body on an
        // uncleared frame — shipped's answer changes in 48 cases, 23 of them real
        // campaign patterns.
        //
        // This is a SENSITIVITY PROBE, NOT a proven game state: a retried lesson does
        // call 426 again, but between the calls Judge.Tick, 427, Sleep, sound, spawns
        // and the return through Draw happen, and any of them may overwrite those ~4 KB.
        // Exactly one thing is proven, and it is enough: not one artificial point but a
        // WHOLE CLASS of inputs is disputed, whose answer is not a function of the
        // input alone. "No thinning happens" is the port's declared boundary of
        // applicability, not a proven fact.
        //
        // WHY "DO NOT DISCARD" NONETHELESS: any other model needs the same unknown
        // memory AND ALSO invents a concrete value for it (a zero-fill model diverges
        // from any other frame in 48 cases). No discard rule is the only option that
        // does not add invention to the unknown.
        //
        // Comparing a candidate against ALL saved points is wrong unconditionally: it
        // discards by a rule absent from the machine code for any frame. On sets shorter
        // than ten points that changes the penalty by 500 and flips the lesson outcome.
        static List<Vector3> GestureDedupDrawn(IList drawn)
        {
            var kept = new List<Vector3>();
            if (drawn is null) return kept;
            int n = Math.Min(drawn.Count, GestureMaxPoints);
            for (int i = 0; i < n; i++)
            {
                Vector3 p = UEInterpreter.AsVector3(drawn[i]);
                if (p.z == -1f) continue;
                kept.Add(new Vector3(p.x, p.y, 0f));
            }
            return kept;
        }

        // execCompareGesture 0x103a40bb-0x103a41c4 — densify the template 8x.
        //
        // Each source point emits itself, then (unless it is the last one) the
        // seven interpolated points toward its successor. The shipped store is
        // `fstp dword` — float32 — while the arithmetic runs in the x87 stack,
        // so the lerp is computed wide and rounded once, on write.
        static List<Vector3> GestureDensify(IList template)
        {
            var outp = new List<Vector3>();
            if (template is null) return outp;
            int m = Math.Min(template.Count, GestureMaxPoints);
            for (int i = 0; i < m; i++)
            {
                Vector3 cur = UEInterpreter.AsVector3(template[i]);
                outp.Add(new Vector3(cur.x, cur.y, 0f));
                if (i + 1 >= m) break;              // 0x103a40c8: last point, base only
                Vector3 next = UEInterpreter.AsVector3(template[i + 1]);
                double dx = (double)next.x - cur.x;
                double dy = (double)next.y - cur.y;
                foreach (double f in GestureDensifyFractions)
                    outp.Add(new Vector3(
                        (float)(dx * f + cur.x),
                        (float)(dy * f + cur.y), 0f));
            }
            return outp;
        }

        // The x87 distance of FUN_103a39c0 (0x103a39fa / 0x103a3ac0) and of the
        // Point body (0x103a3e5d): the squared sum is built on the register
        // stack, stored to a QWORD, and handed to Core.dll's appSqrt(double).
        // The result therefore stays double — it is compared against a float32
        // running minimum, which is what makes the widths asymmetric.
        static double GestureDistance(in Vector3 a, in Vector3 b)
        {
            double dx = (double)a.x - b.x;
            double dy = (double)a.y - b.y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // The running minimum starts at 1.0f and is stored back as float32
        // (`fstp dword [esp+0x10]`), so a distance only survives at float
        // precision even though it arrives as a double.
        static float GestureNearest(List<Vector3> points, in Vector3 probe)
        {
            float best = 1.0f;
            for (int i = 0; i < points.Count; i++)
            {
                double d = GestureDistance(probe, points[i]);
                if (d < best) best = (float)d;
            }
            return best;
        }

        // FUN_103a39c0 — the coverage score.
        //
        //   pass 1, over the TEMPLATE: a template point with a drawn point
        //     within `accuracy` scores 2 toward the total and nothing toward
        //     the penalty; otherwise it costs p = trunc(d/(accuracy*5) + 1),
        //     capped at 2, added to BOTH sums;
        //   pass 2, over the DRAWN points: one farther than `accuracy` from
        //     every template point costs p = trunc(d/accuracy), capped at 3,
        //     added to both sums;
        //   fewer than 10 drawn points adds a flat 500 to both, which alone
        //     drives the score to (near) zero;
        //   score = clamp(1 - penalty/total, 0, 1).
        //
        // `total == 0` (an empty template with >= 10 drawn points) divides by
        // zero on the x87: the masked result is +-Inf or NaN, `1.0 - that` is
        // never >= 0.0, and the lower clamp returns 0.0f. Written explicitly
        // here rather than relying on .NET reproducing the same NaN ordering.
        static float GestureScore(List<Vector3> drawn, List<Vector3> template, float accuracy)
        {
            int penalty = 0, total = 0;
            for (int t = 0; t < template.Count; t++)
            {
                float nearest = GestureNearest(drawn, template[t]);
                if (nearest <= accuracy) { total += 2; continue; }
                // 0x103a3a48: `fld accuracy; fmul 5.0f; fdivr nearest; fadd 1.0f`
                // — the multiplier and quotient live in a REGISTER and are not rounded
                // to float32. Folding them to float32 gives 1.99999994f at the boundary,
                // which rounds to even as 2.0f and yields penalty 2 where shipped
                // truncates 1.999999925 to 1.
                int p = Ftol((double)nearest / ((double)accuracy * 5.0) + 1.0);
                if (p > 2) p = 2;
                penalty += p; total += p;
            }
            for (int d = 0; d < drawn.Count; d++)
            {
                float nearest = GestureNearest(template, drawn[d]);
                if (nearest <= accuracy) continue;
                int p = Ftol((double)nearest / accuracy);   // 0x103a3b0e
                if (p > 3) p = 3;
                penalty += p; total += p;
            }
            if (drawn.Count < 10) { penalty += 500; total += 500; }
            if (total == 0) return 0f;
            double score = 1.0 - (double)penalty / total;
            if (!(score >= 0.0)) return 0f;
            if (score > 1.0) return 1.0f;
            return (float)score;
        }

        // msvcrt (VC6) `_ftol`: save CW, RC=truncate, `fistp QWORD`, return
        // edx:eax — and the caller (0x103a3a60 `cmp eax,2`, 0x103a3b1a
        // `cmp eax,3`) reads ONLY eax. Hence two properties a naive `(int)`
        // lacks:
        //   * NaN, ±inf and |v| >= 2^63 give the 64-bit "integer indefinite"
        //     0x8000000000000000, whose LOW dword is ZERO. Not int.MinValue:
        //     adding INT_MIN to both accumulators matches shipped only for an
        //     EVEN number of such terms (two INT_MIN cancel in int32, e.g.
        //     tri-exact-tightacc: penalty=0, total=30); an ODD count
        //     (tri-exact-one-miss-tightacc: shipped 1.0) separates the models;
        //   * 2^31 <= |v| < 2^63 truncates to int64, and eax is its low 32
        //     bits (wraps modulo 2^32), not saturation.
        //
        // The branch is reachable, not theoretical: with accuracy == 0 the penalty
        // denominator becomes zero, division gives inf, and EVERY missed template point
        // adds ZERO to both accumulators — i.e. is not penalised at all.
        internal static int Ftol(double v)
        {
            if (double.IsNaN(v) || v >= 9223372036854775808.0 || v < -9223372036854775808.0)
                return 0;                                   // low dword of 0x8000000000000000
            return unchecked((int)(long)Math.Truncate(v));  // eax of the int64
        }

        /// <summary>
        /// Engine.Gesture native(426) CompareGesture(array&lt;vector&gt; drawn,
        /// float accuracy) -> float. The call CONTEXT is the pattern.
        /// </summary>
        public float CompareGestureNative(UEObject gesture, object drawn, float accuracy) =>
            GestureScore(GestureDedupDrawn(drawn as IList),
                         GestureDensify(GesturePointsOf(gesture)), accuracy);

        // execCompareGesturePoint 0x103a3d4f-0x103a3e3e — the same densify, but
        // NOT the same code, and the difference is visible only in the disassembly.
        //
        // The array body (CompareGesture) recomputes the difference `next - cur`
        // for EACH of the seven steps. The point body computes it once and keeps it
        // on the register stack, but unevenly across axes:
        //   * dx stays wide for all seven steps (0x103a3d8a `fld st(0)`);
        //   * dy is stored as float32 (0x103a3d7a `fst dword [esp+8]`), and
        //     steps 0.25..0.875 reread THAT narrowed copy
        //     (0x103a3d97 `fld dword [esp+8]`), while the first step 0.125
        //     still uses the wide one.
        // The asymmetry has no meaning — it is register allocation — which is exactly
        // why it cannot be guessed, but measurement sees it: on the Zig template a
        // probe in the middle of the first segment catches a one-ulp float32 divergence.
        static List<Vector3> GestureDensifyForPoint(IList template)
        {
            var outp = new List<Vector3>();
            if (template is null) return outp;
            int m = Math.Min(template.Count, GestureMaxPoints);
            for (int i = 0; i < m; i++)
            {
                Vector3 cur = UEInterpreter.AsVector3(template[i]);
                outp.Add(new Vector3(cur.x, cur.y, 0f));
                if (i + 1 >= m) break;
                Vector3 next = UEInterpreter.AsVector3(template[i + 1]);
                double dx = (double)next.x - cur.x;
                double dyWide = (double)next.y - cur.y;
                float dyNarrow = (float)dyWide;
                for (int k = 0; k < GestureDensifyFractions.Length; k++)
                {
                    double f = GestureDensifyFractions[k];
                    double dy = k == 0 ? dyWide : dyNarrow;
                    outp.Add(new Vector3(
                        (float)(dx * f + cur.x),
                        (float)(dy * f + cur.y), 0f));
                }
            }
            return outp;
        }

        /// <summary>
        /// Engine.Gesture native(427) CompareGesturePoint(vector p, float
        /// accuracy) -> float. Returns the distance from p to the nearest point
        /// of the densified pattern, 1.0 when nothing is nearer. `accuracy` is
        /// evaluated by the shipped exec and then unused — see the file header.
        /// </summary>
        public float CompareGesturePointNative(UEObject gesture, Vector3 probe) =>
            GestureNearest(GestureDensifyForPoint(GesturePointsOf(gesture)), probe);
    }
}
