// Particle update: UpdateFxParticles, StepParticle/StepParticleBody, ramps, newborn catch-up,
// the render-step FX update (RenderStepFx) and the particle draw fade/billboard keep.
// Fields live in UEWorldFrameProducer.cs.
using System;
using System.Collections.Generic;
using System.IO;
using HP1.Core;
using UELib;

namespace HP1.UEScript
{
    public sealed partial class UEWorldFrameProducer
    {
        // URender::BoundVisible of FDynamicSprite::Setup: Core FCoords::Inverse(box) runs at the depth of the emitter
        // slot (see above) and leaves its result's YAxis.Y/Z there.
        internal void SetupBoundVisibleResidue(in BrushCoords box)
        {
            BrushCoords inv = UERenderVisibility.Inverse(box);
            _setupResidueKnown = true; _setupResY = inv.Y.y; _setupResZ = inv.Y.z;
        }

        // Damping factor of a substep (Engine 0x103c1ff4..0x103c200e, render window PC24): fld dword step; fmul dword
        // Damping (rounded to float); fchs; fstp qword; call appExp; fst qword; fstp dword.
        internal static float DampFactor(float step, float damping) => UEX87Transcendental.AppExp(-(step * damping), pc24: true).ToFloat();

        void UpdateFxParticles(UEObject fx, FxState s, float dt)
        {
            (_slotKind, _slotChaos, _slotSpin) = EntrySlot();
            if (_slotKind == SlotKind.SessionStart) NoSetupResidueYet++;
            float lifeCap = RangeBase(fx, "Lifetime") + RangeRandPos(fx, "Lifetime");
            if (lifeCap > 0f && dt > lifeCap) { s.Particles.Clear(); return; }
            for (int i = s.Particles.Count - 1; i >= 0; i--)
                if (s.Particles[i].Lifetime > 0f && s.Particles[i].Lifetime <= dt + s.Particles[i].Age)
                    s.Particles.RemoveAt(i);
            if (s.Particles.Count == 0) return;
            FxStepCtx ctx = BuildStepCtx(fx);
            // Engine.dll .rdata 0x10478410 = 0x3d888a48 = 0.06667f (fcomp at 0x103c400c) — NOT 1/15 (0x3d888889).
            const float SubstepMax = 0.06667f;
            for (float rem = dt; rem > 0f; )
            {
                float step = (rem <= SubstepMax || !ctx.NeedSub) ? rem : SubstepMax;
                float dampF = ctx.Damping <= 0f ? 1f : DampFactor(step, ctx.Damping);
                // The list iterator visits the FIRST particle twice (Engine UParticleList First 0x103c7330: cur =
                // sentinel.next, the advance flag +0x80 left as it is; Next 0x103c7360: advances only when the flag is
                // set, then sets it). Every iteration of the list runs to its end, which clears the flag (0x103c7383),
                // so each First() starts with it clear: particle 0 is updated twice per substep (live batch 9: its Age
                // grows by 2·dt, the others' by dt).
                for (int v = -1; v < s.Particles.Count; v++)
                {
                    int i = v < 0 ? 0 : v;
                    var p = s.Particles[i];
                    if (p.Lifetime > 0f && p.Lifetime <= step + p.Age) continue;
                    StepParticle(p, step, dampF, ctx, fx);
                    // The bSystemRelative branch ran (DripTimer 0 after the drip hold, 93717).
                    if (ctx.SysRel && p.DripTimer == 0f && _slotKind != SlotKind.SysRel)
                    {
                        (_slotChaos, _slotSpin) = UnitCoordsMulRotYAxisYZPc24(UEValue.AsRotator(fx.GetProp(PK.Rotation)));
                        _slotKind = SlotKind.SysRel;
                        if (!_grbbPath) { RenderSlotChaos = _slotChaos; RenderSlotSpin = _slotSpin; }
                    }
                }
                rem -= step;
            }
        }

        // The slot pair (ChaosTimer, SpinPos) as AParticleFX::Update finds it at entry, per caller path (see above).
        (SlotKind Kind, float Chaos, float Spin) EntrySlot()
            => _grbbPath && _setupResidueKnown ? (SlotKind.SetupBoundVisible, _setupResY, _setupResZ)
             : _grbbPath ? (SlotKind.SessionStart, 0f, 0f)
             : (SlotKind.Chain, RenderSlotChaos, RenderSlotSpin);
        // Validation seam (engine judge): the entry slot values and whether this is the GetRenderBoundingBox path.
        internal (float Chaos, float Spin, bool Grbb) EntrySlotForTest() { var e = EntrySlot(); return (e.Chaos, e.Spin, _grbbPath); }

        // YAxis.Y, YAxis.Z of `GMath.UnitCoords *= R` (Core FCoords::operator*=(FRotator), yaw, pitch, roll elementary
        // coords, each axis = TransformVectorBy (x·C.X + y·C.Y) + z·C.Z — BrushCoords.MulRotator) at PC24, the render
        // window AParticleFX::Update runs in: every fmul/fadd rounds to float, in the body's order.
        internal static (float Y, float Z) UnitCoordsMulRotYAxisYZPc24(UERotator r)
        {
            float sy = BrushCoords.SinAngle(r.Yaw), cy = BrushCoords.CosAngle(r.Yaw);
            float sp = BrushCoords.SinAngle(r.Pitch), cp = BrushCoords.CosAngle(r.Pitch);
            float sr = BrushCoords.SinAngle(r.Roll), cr = BrushCoords.CosAngle(r.Roll);
            // Y = (0,1,0) after yaw: (sy, cy, 0).
            float y0 = (0f * cy + 1f * sy) + 0f * 0f, y1 = (0f * -sy + 1f * cy) + 0f * 0f, y2 = (0f * 0f + 1f * 0f) + 0f * 1f;
            // pitch: X=(cp,0,sp) Y=(0,1,0) Z=(-sp,0,cp).
            float p0 = (y0 * cp + y1 * 0f) + y2 * sp, p1 = (y0 * 0f + y1 * 1f) + y2 * 0f, p2 = (y0 * -sp + y1 * 0f) + y2 * cp;
            // roll: Y=(0,cr,-sr) Z=(0,sr,cr).
            return ((p0 * 0f + p1 * cr) + p2 * -sr, (p0 * 0f + p1 * sr) + p2 * cr);
        }

        // The per-step context of UParticle::Update — built once per
        // UpdateFxParticles/AdvanceNewborn call from the emitter's props.
        struct FxStepCtx
        {
            public UEVector Accel, Attraction, EmitterLoc;
            public float Damping;
            public bool NeedSub;
            public bool BUpdate, SysRel;
            public float AlphaGrow, AlphaDelay, ColorDelay, SizeGrow, SizeDelay;
            public float Chaos, ChaosDelay;
            public float Elasticity;
            public float WindModifier; public bool WindPerParticle; public UEVector Wind;
            public int Prim;
            public bool HasPalette;
        }

        FxStepCtx BuildStepCtx(UEObject fx)
        {
            var c = new FxStepCtx();
            // Acceleration (UpdateParticles head, 77441-77451): accel =
            // GravityModifier · Region.Zone->ZoneGravity + emitter Gravity —
            // the zone gravity comes from the actor's CURRENT zone, falling
            // back to Level when the Region is unset (a
            // missing Region must NOT read as zero gravity — shipped asserts
            // when BOTH are null, so an actor with neither is loud here too).
            UEObject zone = (fx.GetProp(PK.Region) as UEObject)?.GetProp(PK.Zone) as UEObject
                ?? fx.GetProp(PK.Level) as UEObject
                ?? throw new InvalidOperationException(
                    $"ParticleFX {fx.GetProp(PK.Name)}: no Region.Zone and no Level — shipped UpdateParticles asserts here (77448)");
            UEVector zoneG = UEValue.AsVector3(zone.GetProp(PK.ZoneGravity));
            float gravityMod = PropF(fx, "GravityModifier");
            UEVector gravity = UEValue.AsVector3(fx.GetProp(PK.Gravity));
            c.Accel = new UEVector(zoneG.x * gravityMod + gravity.x,
                                   zoneG.y * gravityMod + gravity.y,
                                   zoneG.z * gravityMod + gravity.z);
            c.Attraction = UEValue.AsVector3(fx.GetProp(PK.Attraction));
            c.Damping = PropF(fx, "Damping");
            c.EmitterLoc = UEValue.AsVector3(fx.GetProp(PK.Location));
            c.NeedSub = c.Attraction.x != 0f || c.Attraction.y != 0f || c.Attraction.z != 0f;
            c.BUpdate = UEValue.ToBool(fx.GetProp(PK.bUpdate));
            c.SysRel = UEValue.ToBool(fx.GetProp(PK.bSystemRelative));
            c.AlphaGrow = PropF(fx, "AlphaGrowPeriod");
            c.AlphaDelay = PropF(fx, "AlphaDelay");
            c.ColorDelay = PropF(fx, "ColorDelay");
            c.SizeGrow = PropF(fx, "SizeGrowPeriod");
            c.SizeDelay = PropF(fx, "SizeDelay");
            c.Chaos = PropF(fx, "Chaos");
            c.ChaosDelay = PropF(fx, "ChaosDelay");
            c.Prim = UEValue.ToInt(fx.GetProp(PK.RenderPrimitive));
            c.HasPalette = fx.GetProp(PK.ColorPalette) is not null;
            // WIND (Engine.dll.c:23372 / 77473; call arguments taken from disasm
            // 0x103c1f02-0x103c1f0e): when `Damping * WindModifier > 0` the emitter
            // takes GetTotalWind at ITS Location once per update; with
            // bWindPerParticle the wind is taken at each particle's position (93681).
            c.WindModifier = PropF(fx, "WindModifier");
            c.WindPerParticle = UEValue.ToBool(fx.GetProp(PK.bWindPerParticle));
            c.Wind = default;
            if (c.Damping * c.WindModifier > 0f || c.WindPerParticle)
            {
                if (TotalWind is null)
                    throw new NotSupportedException(
                        $"ParticleFX wind reached ({fx.Class?.Name}) — wind source (UELevelRuntime.TotalWind) not bound to the producer");
                if (c.Damping * c.WindModifier > 0f)
                {
                    UEVector w = TotalWind(c.EmitterLoc);
                    c.Wind = new UEVector(c.WindModifier * w.x, c.WindModifier * w.y, c.WindModifier * w.z);
                }
            }
            // Elasticity (+0x320 per prop_offsets): shipped checks `0.0 < Elasticity`
            // on every particle step (93921) — read into the context once.
            c.Elasticity = PropF(fx, "Elasticity");
            if (c.Elasticity > 0f && (ParticleFastLineCheckClear is null || ParticleSingleLineCheck is null))
                throw new NotSupportedException(
                    $"ParticleFX Elasticity reached ({fx.Class?.Name}) — level queries (FastLineCheck/SingleLineCheck) not bound to the producer");
            // Substep 0.06667 — Attraction != 0 OR 0.0001 < Elasticity (Engine.dll.c:77483-77484 and
            // 23654-23655, `this + 800` = +0x320).
            c.NeedSub |= c.Elasticity > 0.0001f;
            return c;
        }

        // One UParticle::Update step (93564-93978) — the shipped body minus
        // the DOCUMENTED STOP boundaries (Elasticity bounce, per-particle
        // wind, ColorPalette ramp — loud in BuildStepCtx/emission, not
        // silently skipped). Shared by the update pass and the newborn
        // back-integration (23644).
        // alphaColor = false — for the newborn catch-up (NOT ORIGINAL, D-54, AdvanceNewborn): it computes the alpha
        // and colour ramps itself in substeps; the size ramp stays shipped (size feeds the particle box -> visibility).
        void StepParticle(Particle p, float step, float dampF, in FxStepCtx c, UEObject fx, bool alphaColor = true)
        {
            ParticleStepTap?.Invoke(fx, p, step, dampF, c.Accel, c.Wind, true);
            StepParticleBody(p, step, dampF, c, fx, alphaColor);
            ParticleStepTap?.Invoke(fx, p, step, dampF, c.Accel, c.Wind, false);
        }

        void StepParticleBody(Particle p, float step, float dampF, in FxStepCtx c, UEObject fx, bool alphaColor)
        {
            float pdt = step;
            // Drip hold (93588): the particle hangs while DripTimer runs; on
            // release the step remainder applies and the size deltas zero.
            if (p.DripTimer > 0f)
            {
                p.DripTimer -= pdt;
                if (p.DripTimer <= 0f)
                {
                    pdt = -p.DripTimer;
                    p.DripTimer = 0f;
                    p.WidthDelta = 0f;
                    p.LengthDelta = 0f;
                }
            }
            p.OldPos = p.Pos;
            if (p.DripTimer == 0f)
            {
                p.SpinPos += pdt * p.SpinRate;
                if (dampF >= 1f)
                {
                    // Undamped (93605): pos += (v + a·dt/2)·dt; v += a·dt.
                    p.Pos = new UEVector(
                        p.Pos.x + (p.Vel.x + c.Accel.x * (pdt * 0.5f)) * pdt,
                        p.Pos.y + (p.Vel.y + c.Accel.y * (pdt * 0.5f)) * pdt,
                        p.Pos.z + (p.Vel.z + c.Accel.z * (pdt * 0.5f)) * pdt);
                    p.Vel = new UEVector(p.Vel.x + c.Accel.x * pdt,
                                         p.Vel.y + c.Accel.y * pdt,
                                         p.Vel.z + c.Accel.z * pdt);
                }
                else
                {
                    // Damped analytic (93622): terminal = a/D + wind;
                    // pos += terminal·dt + (terminal−v)·(dampF−1)/D;
                    // v = terminal + (v−terminal)·dampF.
                    // terminal = Accel/Damping + wind (93676-93693): the emitter's
                    // common wind or, with bWindPerParticle, the wind at the particle position.
                    float invD = 1f / c.Damping;
                    UEVector wind = c.Wind;
                    if (c.WindPerParticle)
                    {
                        UEVector pw = TotalWind(p.Pos);
                        wind = new UEVector(c.WindModifier * pw.x, c.WindModifier * pw.y, c.WindModifier * pw.z);
                    }
                    UEVector terminal = new UEVector(invD * c.Accel.x + wind.x, invD * c.Accel.y + wind.y, invD * c.Accel.z + wind.z);
                    float k = (dampF - 1f) / c.Damping;
                    // Order (0x103bfdd1..0x103bfe0f, render window PC24 = per-op float): (t·dt + (t−v)·k) + pos.
                    p.Pos = new UEVector(
                        (terminal.x * pdt + (terminal.x - p.Vel.x) * k) + p.Pos.x,
                        (terminal.y * pdt + (terminal.y - p.Vel.y) * k) + p.Pos.y,
                        (terminal.z * pdt + (terminal.z - p.Vel.z) * k) + p.Pos.z);
                    p.Vel = new UEVector(
                        terminal.x + (p.Vel.x - terminal.x) * dampF,
                        terminal.y + (p.Vel.y - terminal.y) * dampF,
                        terminal.z + (p.Vel.z - terminal.z) * dampF);
                }
                // bSystemRelative (93717, +0x2f8 bit2): the particle FOLLOWS
                // the emitter's frame — subtract the emitter's movement since
                // the last update, then re-anchor its offset from the OLD
                // rotation's local frame into the NEW rotation's frame.
                if (c.SysRel)
                {
                    UEVector lastLoc = UEValue.AsVector3(fx.GetProp(PK.LastUpdateLocation));
                    var lastRot = UEValue.AsRotator(fx.GetProp(PK.LastUpdateRotation));
                    var curRot = UEValue.AsRotator(fx.GetProp(PK.Rotation));
                    p.Pos = new UEVector(p.Pos.x - (lastLoc.x - c.EmitterLoc.x),
                                         p.Pos.y - (lastLoc.y - c.EmitterLoc.y),
                                         p.Pos.z - (lastLoc.z - c.EmitterLoc.z));
                    UEVector rel = new UEVector(p.Pos.x - c.EmitterLoc.x,
                                                p.Pos.y - c.EmitterLoc.y,
                                                p.Pos.z - c.EmitterLoc.z);
                    var (oX, oY, oZ) = UEValue.GetAxes(lastRot);
                    float lx = rel.x * oX.x + rel.y * oX.y + rel.z * oX.z;
                    float ly = rel.x * oY.x + rel.y * oY.y + rel.z * oY.z;
                    float lz = rel.x * oZ.x + rel.y * oZ.y + rel.z * oZ.z;
                    var (nX, nY, nZ) = UEValue.GetAxes(curRot);
                    p.Pos = new UEVector(
                        c.EmitterLoc.x + nX.x * lx + nY.x * ly + nZ.x * lz,
                        c.EmitterLoc.y + nX.y * lx + nY.y * ly + nZ.y * lz,
                        c.EmitterLoc.z + nX.z * lx + nY.z * ly + nZ.z * lz);
                }
                // Attraction (93780): v += (emitter − p) ⊙ A · dt; negative
                // components repel.
                if (c.NeedSub)
                {
                    // X multiplies dt FIRST (0x103c01d3..0x103c01ee: d.x·dt·A.x), Y/Z multiply A first (0x103c01f1..).
                    p.Vel = new UEVector(
                        p.Vel.x + (c.EmitterLoc.x - p.Pos.x) * pdt * c.Attraction.x,
                        p.Vel.y + (c.EmitterLoc.y - p.Pos.y) * c.Attraction.y * pdt,
                        p.Vel.z + (c.EmitterLoc.z - p.Pos.z) * c.Attraction.z * pdt);
                }
                // Chaos (93795): the timer runs down every step; with Chaos
                // set and the timer spent, kick v by a normalized random
                // vector (THREE appFrand, components 2f−1, normalized when
                // len² ≥ 1e-8) times Chaos; timer reloads to ChaosDelay.
                if (p.ChaosTimer != 0f)
                    p.ChaosTimer = p.ChaosTimer <= pdt ? 0f : p.ChaosTimer - pdt;
                if (c.Chaos != 0f && p.ChaosTimer <= 0f)
                {
                    float r1 = Frand() * 2f - 1f;
                    float r2 = Frand() * 2f - 1f;
                    float r3 = Frand() * 2f - 1f;
                    // Sum order from the disasm (0x103c02a7..0x103c02b9): (r3² + r2²) + r1² — the third draw first.
                    float len2 = (r3 * r3 + r2 * r2) + r1 * r1;
                    if (len2 >= 1e-08f)
                    {
                        float inv = 1f / MathF.Sqrt(len2);
                        r1 *= inv; r2 *= inv; r3 *= inv;
                    }
                    // (93822): the kick FVector is built as
                    // (third, second, first) — X = r3, Y = r2, Z = r1.
                    p.Vel = new UEVector(p.Vel.x + r3 * c.Chaos,
                                         p.Vel.y + r2 * c.Chaos,
                                         p.Vel.z + r1 * c.Chaos);
                    p.ChaosTimer = c.ChaosDelay;
                }
            }
            Ramps(p, pdt, c, p.Age, p.DripTimer, alphaColor, size: true);
            // World bounce (LAB_103c064e, 93921-93975): with Elasticity > 0 and a
            // BLOCKED UModel::FastLineCheck(End=OldPos(+0), Start=Pos(+0xc))
            // comes ULevel::SingleLineCheck(Source=0, End=Pos, Start=OldPos,
            // TraceFlags=6, Extent=0). A hit on LevelInfo reflects the velocity
            // about the normal, v = (v − 2(v·N)N)·Elasticity, and the particle moves
            // to HitLocation; otherwise (a mover or nothing) the velocity is zeroed and
            // the particle returns to OldPos.
            if (c.Elasticity > 0f && !ParticleFastLineCheckClear(p.OldPos, p.Pos))
            {
                ParticleLineHit hit = ParticleSingleLineCheck(p.OldPos, p.Pos);
                if (hit.World)
                {
                    UEVector n = hit.Normal;
                    float vn = p.Vel.x * n.x + p.Vel.y * n.y + p.Vel.z * n.z;
                    // FVector((N.x+N.x)·vn, (N.y+N.y)·vn, (N.z+N.z)·vn) — as in the decompile.
                    UEVector twice = new UEVector((n.x + n.x) * vn, (n.y + n.y) * vn, (n.z + n.z) * vn);
                    p.Vel = new UEVector((p.Vel.x - twice.x) * c.Elasticity,
                                         (p.Vel.y - twice.y) * c.Elasticity,
                                         (p.Vel.z - twice.z) * c.Elasticity);
                    p.Pos = hit.Location;
                }
                else
                {
                    p.Vel = new UEVector(0f, 0f, 0f);
                    p.Pos = p.OldPos;
                }
            }
            // The shipped body's very last operation (93976).
            p.Age += pdt;
        }

        // Alpha/colour/size ramps of UParticle::Update (93833-93920). age/drip — the particle's Age and DripTimer at
        // this step: in shipped the particle's own fields, in the newborn catch-up (NOT ORIGINAL, D-54) — substep counts.
        void Ramps(Particle p, float pdt, in FxStepCtx c, float age, float drip, bool alphaColor, bool size)
        {
            // Ramps — ALL gated on bUpdate (93833: bit0 of +0x2f8, ctor
            // default TRUE; without it alpha/color/size stay frozen). They
            // compare against the OLD Age — the shipped body advances Age as
            // its very LAST operation (93976): a newborn's
            // first step with AlphaDelay=0 does NOT pass the strict
            // Age > AlphaDelay yet.
            if (!c.BUpdate) return;
            if (alphaColor)
            {
                // Alpha (93834): grow toward AlphaMax over
                // AlphaGrowPeriod·Lifetime, else fade by AlphaDelta after
                // AlphaDelay; snap to 0 below 0.001.
                if (age < c.AlphaGrow * p.Lifetime)
                {
                    float a = p.Alpha + pdt * p.AlphaMax / (c.AlphaGrow * p.Lifetime);
                    p.Alpha = a < p.AlphaMax ? a : p.AlphaMax;
                }
                else if (age > c.AlphaDelay)
                    p.Alpha += p.AlphaDelta * pdt;
                if (p.Alpha < 0.001f) p.Alpha = 0f;
                // Color (93853): the linear delta applies only after
                // ColorDelay and only without a palette (palette STOPs at
                // emission).
                if (!c.HasPalette
                    && (p.ColorDelta.x != 0f || p.ColorDelta.y != 0f || p.ColorDelta.z != 0f)
                    && age > c.ColorDelay)
                    p.Color = new UEVector(p.Color.x + p.ColorDelta.x * pdt,
                                           p.Color.y + p.ColorDelta.y * pdt,
                                           p.Color.z + p.ColorDelta.z * pdt);
            }
            // Size (93899): PPRIM_Liquid(2) only sizes while dripping;
            // grow toward WidthMax/LengthMax over SizeGrowPeriod·Lifetime,
            // else the EndScale slope after SizeDelay.
            if (size && (c.Prim != 2 || drip > 0f))
            {
                if (age < c.SizeGrow * p.Lifetime)
                {
                    float scale = pdt / (c.SizeGrow * p.Lifetime);
                    float w = p.Width + scale * p.WidthMax;
                    float l = p.Length + scale * p.LengthMax;
                    p.Width = w < p.WidthMax ? w : p.WidthMax;
                    p.Length = l < p.LengthMax ? l : p.LengthMax;
                }
                else if (age > c.SizeDelay)
                {
                    p.Width += p.WidthDelta * pdt;
                    p.Length += p.LengthDelta * pdt;
                }
            }
        }

        // The shipped emit-tail catch-up loop (23644): same physics context as
        // the update pass, 0.06667-substep, per-step damping exponent.
        //
        // NOT ORIGINAL (D-54): improved newborn catch-up. Without Attraction/Elasticity shipped catches a newborn up
        // in ONE step, and the ramps are evaluated against the OLD Age = 0 (93834): particles seeded over a long
        // window (emitter came back into the frame, priming) are drawn as newborns — a torch flares. Here the
        // shipped step runs as-is (position, velocity, chaos and appFrand use, bSystemRelative, DripTimer,
        // Age, size — the Lifetime of later particles depends on them, removal, particle box -> emitter
        // visibility and self-destruction of a spent emitter), while the ALPHA AND COLOUR ramps run in 0.06667
        // substeps from the initial state; they affect neither the box nor the game. Substepping the whole
        // UParticle::Update instead shifts FireSeedSmoke self-destruction on Lev2_fire1 (chaos on every substep,
        // box from size and float-ulp positions). Only under Enhanced (EnhancedVisuals); Original runs the shipped
        // single step with the ramps against Age = 0.
        void AdvanceNewborn(UEObject fx, Particle p, float dt)
        {
            FxStepCtx ctx = BuildStepCtx(fx);
            bool improve = EnhancedVisuals && !ctx.NeedSub && dt > 0.06667f;
            float age0 = p.Age, drip0 = p.DripTimer;
            for (float rem = dt; rem > 0f; )
            {
                float step = (rem <= 0.06667f || !ctx.NeedSub) ? rem : 0.06667f;
                float dampF = ctx.Damping <= 0f ? 1f : DampFactor(step, ctx.Damping);
                StepParticle(p, step, dampF, ctx, fx, alphaColor: !improve);
                rem -= step;
            }
            if (!improve) return;
            for (float rem = dt, age = age0, drip = drip0; rem > 0f; )
            {
                float step = rem <= 0.06667f ? rem : 0.06667f;
                float pdt = step;
                if (drip > 0f)
                {
                    drip -= pdt;
                    if (drip <= 0f) { pdt = -drip; drip = 0f; }
                }
                Ramps(p, pdt, ctx, age, drip, alphaColor: true, size: false);
                age += pdt;
                rem -= step;
            }
        }

        public bool RenderStepFx(UEObject fx)
        {
            ParticleUpdateTap?.Invoke(this, fx, true, false);
            bool ok = RenderStepFxBody(fx);
            ParticleUpdateTap?.Invoke(this, fx, false, ok);
            return ok;
        }

        bool RenderStepFxBody(UEObject fx)
        {
            RenderStepFxEntered?.Invoke(fx);
            // Update head (0x103c3e05..0x103c3e12): a null particle list returns false before any write. A destroyed emitter
            // has it freed (AParticleFX::Destroy, +0x378 = 0 at 0x103c1cdd) — the case of an emitter destroyed earlier in
            // the same frame tree and drawn again by a child frame (DrawActorSprite has no bDeleteMe test).
            if (fx.Destroyed) return false;
            FxState s = FxStateOf(fx);
            float age = PropF(fx, "Age");
            float elapsed = PropF(fx, "ElapsedTime");
            UEVector loc = UEValue.AsVector3(fx.GetProp(PK.Location));
            // First-update prelude (106729-106740): ElapsedTime==0 seeds Location into
            // LastUpdateLocation AND LastEmitLocation, Rotation into
            // LastUpdateRotation — so the first emit back-dates along a ZERO
            // delta and the prime burst still passes the steady clamp via the
            // (target−alive)/ppsMin==lifeMax arithmetic, not a failed gate.
            if (elapsed == 0f)
            {
                fx.Props["LastUpdateLocation"] = loc;
                fx.Props["LastEmitLocation"] = loc;
                fx.Props["LastUpdateRotation"] = UEValue.AsRotator(fx.GetProp(PK.Rotation));
            }
            // Update(0) head (106743): dt = Age − ElapsedTime, and
            // ElapsedTime += dt ALWAYS — no clamp.
            float dt = age - elapsed;
            fx.Props["ElapsedTime"] = elapsed + dt;
            bool shellOnly = UEValue.ToBool(fx.GetProp(PK.bShellOnly));
            if (UEValue.ToBool(fx.GetProp(PK.bSteadyState)) && dt > 0.06667f
                && UEValue.AsVector3(fx.GetProp(PK.LastUpdateLocation)) == loc)
            {
                float lifeMax = RangeBase(fx, "Lifetime") + RangeRandPos(fx, "Lifetime");
                float target = (RangeBase(fx, "ParticlesPerSec") + RangeRandPos(fx, "ParticlesPerSec")) * lifeMax;
                int aliveCap = UEValue.ToInt(fx.GetProp(PK.ParticlesAlive));
                if (aliveCap > 0 && aliveCap < target) target = aliveCap;
                float ppsRand = RangeRandRaw(fx, "ParticlesPerSec");
                float ppsMin = RangeBase(fx, "ParticlesPerSec") + (ppsRand < 0f ? ppsRand : 0f);
                // Unguarded division (0x103c4003..0x103c401b): ppsMin 0 gives ±inf (0/0 NaN); `fcom 0.06667;
                // test ah,1` replaces anything below OR unordered with 0.06667, +inf leaves dt uncapped.
                float catchUp = (target - s.Particles.Count) / ppsMin;
                if (!(catchUp >= 0.06667f)) catchUp = 0.06667f;
                if (lifeMax < dt) dt = lifeMax;
                if (catchUp < dt) dt = catchUp;
            }
            // (106782): dt>0 && !bShellOnly → simulate dt; dt<=0 &&
            // !bShellOnly → return WITHOUT simulating (particles freeze on
            // pause frames); ONLY bShellOnly forces the 0.1 shell quantum.
            // LOD is NOT touched here: the ctor default 1.0 (or a loaded
            // override) is what shipped emission reads — nothing in
            // Engine/Render writes +0x300 at runtime (searched;
            // ParticleFxInitExecution ports the ctor default).
            if (shellOnly) dt = 0.1f;
            else if (dt <= 0f) return true;
            UpdateFxParticles(fx, s, dt);
            int emitted = UEValue.ToInt(fx.GetProp(PK.ParticlesEmitted));
            int particlesMax = UEValue.ToInt(fx.GetProp(PK.ParticlesMax));
            if (particlesMax == 0 || emitted < particlesMax)
            {
                // bEmit gate (+0x3c8 bit1): absent -> class default (true for
                // the shipped ParticleFX defaults; scripts toggle via
                // EnableEmission).
                bool emit = fx.GetProp(PK.bEmit) is { } be ? UEValue.ToBool(be) : true;
                if (emit) EmitParticles(fx, s, dt);
                else fx.Props["LastEmitLocation"] = loc;
            }
            else if (s.Particles.Count == 0)
            {
                DestroyActor?.Invoke(fx);
                return false;
            }
            UpdateListBounds(s);      // list +0x94 (106809)
            fx.Props["LastUpdateLocation"] = loc;
            fx.Props["LastUpdateRotation"] = UEValue.AsRotator(fx.GetProp(PK.Rotation));
            return true;
        }

        void ParticleDrawerFade(UEObject fx, UESceneNode f, int view)
        {
            if (fx.GetProp(PK.Textures) is not System.Collections.IList tl || tl.Count == 0 || tl[0] is null) return;
            ParticleDrawTap?.Invoke(this, fx, f, true);
            FxState s = FxStateOf(fx);
            var kept = new List<(Particle, float)>();
            int prim = UEValue.ToInt(fx.GetProp(PK.RenderPrimitive));
            bool billboard = prim <= 3 && !UEValue.ToBool(fx.GetProp(PK.bShellOnly));
            float budget = ((f.FY * f.FX) * 1f) * 0.5f;
            bool uk = UkParticleWalk;
            int distribution = uk ? UEValue.ToInt(fx.GetProp(PK.Distribution)) : 0;
            float lod = uk ? PropF(fx, "LOD") : 0f;
            for (int v = s.Particles.Count > 0 ? -1 : 0; v < s.Particles.Count; v++)
            {
                Particle p = s.Particles[v < 0 ? 0 : v];
                if (uk && UkWalkCut(distribution, lod, p.Priority)) continue;
                if (!billboard || BillboardKeep(f, p, budget, fade: !uk)) kept.Add((p, p.Alpha));
            }
            ParticleDraws.Add((fx, view, kept));
            ParticleDrawTap?.Invoke(this, fx, f, false);
        }

        // UK11 Render.dll walk FUN_10b15000 (US FUN_10b15090), 0x10b1531c..0x10b15341, before the drawer is called: unless
        // Distribution (+0x334) == 1, `fld LOD(+0x300); fmul 9.0 (0x10b388d0); fild Priority(+0x88); fcompp; test ah,
        // 0x41; je next` — the particle is not drawn when Priority > LOD·9 (ordered; NaN draws). US 1.53 has no such cut.
        // Judged against those Fix bytes under unicorn.
        internal static bool UkWalkCut(int distribution, float lod, int priority) => distribution != 1 && (float)priority > lod * 9f;

        // fade: the US 1.53 density fade (below). The UK11 drawer FUN_10b18390 ends at the outcode return 0x10b18a7f: the
        // same culling, no budget, no Alpha write (its drawer object has no budget field: DrawParticleSystem 0x10b157c0).
        internal static bool BillboardKeep(UESceneNode f, Particle p, float budget, bool fade = true)
        {
            BrushCoords c = f.Coords;
            float dx = p.Pos.x - c.Origin.x, dy = p.Pos.y - c.Origin.y, dz = p.Pos.z - c.Origin.z;
            float cx = (dx * c.X.x + dy * c.X.y) + dz * c.X.z;
            float cy = (dx * c.Y.x + dy * c.Y.y) + dz * c.Y.z;
            float cz = (dx * c.Z.x + dy * c.Z.y) + dz * c.Z.z;
            if (!(cz >= 1f)) return false;
            float sc = f.ProjZ / cz;
            float hw = p.Width * 0.5f, hl = p.Length * 0.5f;
            float ex = hw, ey = hl;
            if (p.SpinRate < 0f || p.SpinRate > 0f)   // `fcomp 0.0; test ah, 0x40; jne` (0x10b18603..0x10b18614): equal or NaN unrotated
            {
                float t = p.SpinPos * 65536f * 0.15915494f;
                int a = t >= -2147483648f && t < 2147483648f ? (int)MathF.Round(t, MidpointRounding.ToEven) : int.MinValue;
                float co = BrushCoords.CosAngle(a), si = BrushCoords.SinAngle(a);
                if (co < 0f) co = -co;
                if (si < 0f) si = -si;
                ex = co * hw + si * hl;
                ey = si * hw + co * hl;
            }
            static bool Neg(float v) => BitConverter.SingleToInt32Bits(v) < 0;
            if (Neg((f.PrjYP * cz - cy) + ey) || Neg((f.PrjYM * cz + cy) + ey) || Neg((f.PrjXP * cz - cx) + ex) || Neg((f.PrjXM * cz + cx) + ex))
                return false;
            if (!fade) return true;
            float area = ((p.Length * p.Width) * sc) * sc;
            float x = ((float)(p.Priority + 1) * area) * 0.25f;
            if (x > budget)
            {
                float fa = ((budget + budget) - x) / budget;
                if (p.Alpha > fa) p.Alpha = fa;
                if (!(p.Alpha > 0f)) return false;
            }
            return true;
        }
    }
}
