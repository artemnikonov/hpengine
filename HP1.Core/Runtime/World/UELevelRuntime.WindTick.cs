// AWind::Tick (Engine.dll.c:100405-100460, body 0x10430db0) — 1:1 port.
//
// Virtual wind Tick: first AActor::Tick, then, when WindFluctuation (+0x262) != 0,
// the fluctuation. Precision as in shipped (PC64, x87 extended): every `fstp dword`
// here is ToFloat, everything else is kept in X87Ext.
//   t  = dt / (max(WindFlucPeriod,1)·0.015625)            → float
//   e1 = (float)exp(−(1.0·t))
//   v  = (2·appFrand−1) on three axes (each → float), repeated while
//        (zExt·z + y²)+x² > 1 (0x10430e51..0x10430e78; z in the sum is extended)
//   n  = UnsafeNormal(v) (Core 0x10119ae0: 1/sqrt in double, the multiplier
//        is not rounded)
//   k  = ((1−e1)·WindFluctuation)·(1/255)
//   FlucVel = (e1·FlucVel.x + float(n.x·k), float(e1·FlucVel.y) + float(n.y·k),
//              float(e1·FlucVel.z) + n.z·k)
//   e2 = exp(−t) (double, in the register)
//   Fluc = float(e2·Fluc), then Fluc += dt·FlucVel (x — extended, y/z — float)
// RNG: ≥3 appFrand per tick, so the game RNG stream depends on this on maps with
// WindFluctuation ≠ 0.
// Oracle: se_harness/bsp_diff/validate_wind_tick.py (the real body under unicorn),
// corpus se_harness/bsp_diff/data/wind_tick_corpus.tsv, fixture wind_tick_matches_shipped_oracle.
using System;
using HP1.Core;
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        internal void TickWind(UEObject actor, float dt)
        {
            int fluct = UEInterpreter.ToInt(actor.GetProp(PK.WindFluctuation)) & 0xff;
            if (fluct == 0) return;
            if (FrandHook is null)
                throw new InvalidOperationException(
                    $"AWind::Tick (Engine.dll.c:100432) reached actor={actor.GetProp(PK.Name)} without FrandHook");
            static X87Ext R(X87Ext v) => X87Ext.Round64(v);
            static X87Ext F(float v) => X87Ext.From(v);
            int period = UEInterpreter.ToInt(actor.GetProp(PK.WindFlucPeriod)) & 0xff;
            if (period == 0) period = 1;
            float t = X87Ext.Div64(F(dt), R(X87Ext.From(period) * F(0.015625f))).ToFloat();
            float e1 = UEX87Transcendental.AppExp(-(double)t, pc24: false).ToFloat();   // appExp(−(1.0f·t)), fstp dword (0x10430e18)
            X87Ext one = F(1f);
            float vx, vy, vz;
            while (true)
            {
                X87Ext r = FrandHook(); vx = R(R(r + r) - one).ToFloat();
                r = FrandHook();        vy = R(R(r + r) - one).ToFloat();
                // z: `fst dword` (0x10430e51) — stored as float, st0 keeps the
                // extended value; `fmul [z]` multiplies the extended by its own float copy.
                r = FrandHook();        X87Ext zExt = R(R(r + r) - one); vz = zExt.ToFloat();
                X87Ext sum = R(R(R(zExt * F(vz)) + R(F(vy) * F(vy))) + R(F(vx) * F(vx)));
                if (sum.CompareTo(one) <= 0) break;
            }
            // FVector::UnsafeNormal (Core 0x10119ae0).
            double size2 = R(R(R(F(vx) * F(vx)) + R(F(vy) * F(vy))) + R(F(vz) * F(vz))).ToDouble();
            X87Ext scale = X87Ext.Div64(X87Ext.From(1.0), X87Ext.From(Math.Sqrt(size2)));
            float nx = R(scale * F(vx)).ToFloat();
            float ny = R(scale * F(vy)).ToFloat();
            float nz = R(scale * F(vz)).ToFloat();
            X87Ext k = R(R(R(one - F(e1)) * X87Ext.From(fluct)) * F(0.003921569f));

            Vector3 fv = UEInterpreter.AsVector3(actor.GetProp(PK.FlucVel));
            float a0 = R(F(nx) * k).ToFloat();
            float a1 = R(F(ny) * k).ToFloat();
            X87Ext a2 = R(k * F(nz));
            X87Ext ex = R(F(e1) * F(fv.x));
            float ey = R(F(e1) * F(fv.y)).ToFloat();
            float ez = R(F(e1) * F(fv.z)).ToFloat();
            fv = new Vector3(R(ex + F(a0)).ToFloat(), R(F(ey) + F(a1)).ToFloat(), R(F(ez) + a2).ToFloat());
            actor.Props["FlucVel"] = fv;

            X87Ext e2 = UEX87Transcendental.AppExp(-(double)t, pc24: false);   // appExp, st0 used in the register (0x10430f40)
            Vector3 fl = UEInterpreter.AsVector3(actor.GetProp(PK.Fluc));
            fl = new Vector3(R(e2 * F(fl.x)).ToFloat(), R(e2 * F(fl.y)).ToFloat(), R(e2 * F(fl.z)).ToFloat());
            X87Ext bx = R(F(dt) * F(fv.x));
            float by = R(F(dt) * F(fv.y)).ToFloat();
            float bz = R(F(dt) * F(fv.z)).ToFloat();
            actor.Props["Fluc"] = new Vector3(R(bx + F(fl.x)).ToFloat(), R(F(by) + F(fl.y)).ToFloat(),
                R(F(bz) + F(fl.z)).ToFloat());
        }
    }
}
