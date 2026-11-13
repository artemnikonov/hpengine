// Particle emitters (AParticleFX): particle and emitter state, InitExecution, the FX tick, parameter
// sampling and EmitParticles (spawn), slot bookkeeping. The per-particle step and the render-time update
// are in UEWorldFrameProducer.ParticleStep.cs. Fields live in UEWorldFrameProducer.cs.
using System;
using System.Collections.Generic;
using System.IO;
using HP1.Core;
using UELib;

namespace HP1.UEScript
{
    public sealed partial class UEWorldFrameProducer
    {
        // ── Native emitter state ─────
        // Core owns the particle system. The GAME-TICK half (below) —
        // AParticleFX::Tick (Engine.dll.c:14138) accumulates Age and fires the
        // emission-budget self-destroy; it consumes NO RNG. The RENDER-side
        // half — the deterministic RenderStep after the camera calls
        // Update(0)→EmitParticles per shipped render order, where emission
        // consumes the render-side MsvcRandom (shipped: appFrand from the one
        // CRT rand stream; the port gives presentation its own stream —
        // see UENativesCore.BindFrameProducer).
        // One live particle — the UParticle field set (offsets in the shipped
        // object; the bridge's decompile-anchored UPart). Held in the C#
        // container that models the transient ParticleList (+0x378).
        public sealed class Particle
        {
            public UEVector OldPos, Pos, Vel;
            public float Age, Lifetime;          // Lifetime 0 = immortal
            public float Alpha, AlphaMax, AlphaDelta;
            public UEVector Color, ColorDelta;
            public float Width, WidthMax, WidthDelta;
            public float Length, LengthMax, LengthDelta;
            public float DripTimer, ChaosTimer, SpinPos, SpinRate;
            // UParticle +0x88: the draw priority EmitParticles gives each born particle (PriorityTable below); read by the
            // Render.dll particle drawer's density fade.
            public int Priority;
        }

        // NATIVE-ONLY emitter state. Every DECLARED ParticleFX var — Age,
        // EmitDelay, EmissionResidue, ParticlesEmitted, ParticlesAlive,
        // ElapsedTime, LastUpdate*/LastEmitLocation — is SCRIPT-VISIBLE and
        // lives in the prop bag. What remains native is the particle
        // container itself (the transient ParticleList object's contents).
        public sealed class FxState
        {
            public readonly List<Particle> Particles = new();
            // Container bounds (UParticleList +0x54..+0x6c: Min, Max, IsValid) —
            // read by AParticleFX::GetRenderBoundingBox for emitter visibility.
            public UEVector BoundsMin, BoundsMax;
            public bool BoundsValid;
        }

        // UParticleList slot +0x94 (Engine FUN_103c7630), called at the end of
        // AParticleFX::Update after simulation: the box is reset, then for each
        // particle FBox += Location ∓ half, half = 0.5·max(Length, Width) (on
        // equality — Width; particle fields +0xc Location, +0x58 Width, +0x64
        // Length — layout of AParticleFX::AddParticle). Render arithmetic is float
        // (24-bit x87 precision); min/max do not depend on particle order.
        public static void UpdateListBounds(FxState s)
        {
            s.BoundsValid = false;
            s.BoundsMin = default; s.BoundsMax = default;
            foreach (Particle p in s.Particles)
            {
                float size = p.Length <= p.Width ? p.Width : p.Length;
                float half = size * 0.5f;
                Add(new UEVector(p.Pos.x - half, p.Pos.y - half, p.Pos.z - half));
                Add(new UEVector(p.Pos.x + half, p.Pos.y + half, p.Pos.z + half));
            }

            void Add(UEVector v)
            {
                if (!s.BoundsValid) { s.BoundsValid = true; s.BoundsMin = v; s.BoundsMax = v; return; }
                s.BoundsMin = new UEVector(v.x < s.BoundsMin.x ? v.x : s.BoundsMin.x, v.y < s.BoundsMin.y ? v.y : s.BoundsMin.y, v.z < s.BoundsMin.z ? v.z : s.BoundsMin.z);
                s.BoundsMax = new UEVector(s.BoundsMax.x < v.x ? v.x : s.BoundsMax.x, s.BoundsMax.y < v.y ? v.y : s.BoundsMax.y, s.BoundsMax.z < v.z ? v.z : s.BoundsMax.z);
            }
        }

        public FxState FxStateOf(UEObject fx)
        {
            if (!_fx.TryGetValue(fx, out var s)) _fx[fx] = s = new FxState();
            return s;
        }

        // The one-time script-init pass — two shipped
        // sources composed:
        //   the C++ ctor AParticleFX::AParticleFX (Engine.dll.c:161059)
        //   builds the class-default block: LOD(+0x300)=1.0 (NOTHING in
        //   Engine/Render ever writes it afterwards — searched, the renderer
        //   does not set it — so emission always sees the loaded value),
        //   bUpdate(+0x2f8 bit0)=true, Age/ElapsedTime/container zeroed;
        //   AParticleFX::InitExecution (Engine.dll.c:75566) then FORCES
        //   bSteadyState = (ParticlesMax==0 && Distribution != 1 (the
        //   pattern/path distribution)), and when it holds and
        //   (bPrime || Level.TimeSeconds==0) raises Age to the full lifetime
        //   (Lifetime.Base + max(Lifetime.Rand,0)) — the PRIME BURST then
        //   happens on the first render Update: dt = Age-0 passes the steady
        //   clamp (the Location==LastUpdateLocation gate fails against the
        //   ctor-zeroed LastUpdateLocation) and emission back-fills a whole
        //   lifetime window of aged particles. ElapsedTime resets to 0.
        //
        // Called from the ENGINE's InitExecution passes — the loaded-level sweep
        // (Engine.dll.c:247376-247381) and SpawnActor (20258) — never lazily off
        // the first render, so a no-render run and a rendering run start from the same actor state.
        public static void ParticleFxInitExecution(UEObject fx)
        {
            // The AParticleFX ctor (161064-161069: Age/ElapsedTime/container zeroed,
            // bUpdate|=1, LOD=1) is ported where construction lives —
            // UEObject.LoadDefaults, AFTER the defaults copy (ctor-after-defaults
            // order proven by Core.dll.c:14176-14179). The guards below exist ONLY for
            // raw fixture UEObjects that skipped LoadDefaults; after it both are always non-null.
            if (fx.GetProp(PK.LOD) is null) fx.Props["LOD"] = 1f;   // ctor 161073
            // ctor 161071: +0x2f8 bit0 (bUpdate) defaults TRUE — without it
            // every ramp (alpha/color/size) freezes (93833).
            if (fx.GetProp(PK.bUpdate) is null) fx.Props["bUpdate"] = true;
            int particlesMax = UEValue.ToInt(fx.GetProp(PK.ParticlesMax));
            int distribution = UEValue.ToInt(fx.GetProp(PK.Distribution));
            bool steady = particlesMax == 0 && distribution != 1;
            fx.Props["bSteadyState"] = steady;
            bool prime = UEValue.ToBool(fx.GetProp(PK.bPrime));
            float timeSeconds = fx.GetProp(PK.Level) is UEObject li
                ? UEValue.AsFloat(li.GetProp(PK.TimeSeconds)) : 0f;
            if (steady && (prime || timeSeconds == 0f))
            {
                float lifeMax = RangeBase(fx, "Lifetime") + RangeRandPos(fx, "Lifetime");
                if (UEValue.AsFloat(fx.GetProp(PK.Age)) < lifeMax)
                    fx.Props["Age"] = lifeMax;
            }
            fx.Props["ElapsedTime"] = 0f;
        }

        static float RangeBase(UEObject o, string p)
            => o.GetProp(p) is UEObject bag ? UEValue.AsFloat(bag.GetProp(PK.Base)) : 0f;
        static float RangeRandPos(UEObject o, string p)
        {
            float r = o.GetProp(p) is UEObject bag ? UEValue.AsFloat(bag.GetProp(PK.Rand)) : 0f;
            return r < 0f ? 0f : r;
        }

        // AParticleFX::Tick prelude (Engine.dll.c:14138) — runs in the game
        // tick BEFORE the plain AActor::Tick body, exactly as the shipped
        // virtual does (it calls AActor::Tick at its end). No RNG. GIsEditor=0
        // headless, so the editor gates collapse:
        //   activity: the ParentBlend system's alive count >= 1, else
        //             ParticlesPerSec.Base + max(Rand,0) > 0;
        //   Age += dt;
        //   self-destroy: ParticlesMax>0 && pps.Base>0 &&
        //     ParticlesMax/pps.Base + Lifetime.Base + max(Lifetime.Rand,0)
        //       < Age(new)  → DestroyActor(this, 0).
        public void TickParticleFx(UEObject fx, float dt, Action<UEObject> destroy)
        {
            // Activity gate: this+0x378 in the shipped body is
            // the emitter's OWN internal live-particle container (its virtual
            // +0x88 returns the live count) — NOT the ParentBlend property.
            // An emitter with pps=0 but particles still alive keeps aging.
            bool active;
            if (FxStateOf(fx).Particles.Count >= 1)
                active = true;
            else
                active = RangeBase(fx, "ParticlesPerSec") + RangeRandPos(fx, "ParticlesPerSec") > 0f;
            if (!active) return;
            // Age (+0x3a8) is a DECLARED script-visible var — prop bag.
            float age = UEValue.AsFloat(fx.GetProp(PK.Age)) + dt;
            fx.Props["Age"] = age;
            int particlesMax = UEValue.ToInt(fx.GetProp(PK.ParticlesMax));
            float ppsBase = RangeBase(fx, "ParticlesPerSec");
            if (particlesMax > 0 && ppsBase > 0f)
            {
                // Game tick, x87 PC64 (0x103c3d43..0x103c3d59): `fild Max; fdiv ppsBase; fadd RandPos; fadd Base` in
                // the register, `fcompp` against the stored float Age — ((M/p) + R) + B, unrounded to float.
                X87Ext sum = X87Ext.Round64(X87Ext.Round64(
                    X87Ext.Div64(X87Ext.From(particlesMax), X87Ext.From(ppsBase))
                    + X87Ext.From(RangeRandPos(fx, "Lifetime"))) + X87Ext.From(RangeBase(fx, "Lifetime")));
                if (X87Ext.From(age).CompareTo(sum) > 0)
                    destroy(fx);
            }
        }
        void AnnounceNoRenderPass()
        {
            if (_noPassAnnounced) return;
            _noPassAnnounced = true;
            HP1.Core.UEPlaythroughLog.Always("render",
                "render pass not bound: particles in the frame are not simulated (shipped simulates only drawn ones)");
        }
        static int AngleUnits(float rad) => (int)MathF.Round(rad * RadToUnits);
        internal static float GTabSin(int units) => BrushCoords.SinAngle(units);

        // ── The RENDER-side particle half ───
        // Shipped: the renderer calls AParticleFX::Update(0) per drawn
        // particle system (Render.dll.c DrawParticleSystem); Update computes
        // dt = Age - ElapsedTime (the render catches up to the game tick),
        // then EmitParticles/UpdateParticles run — emission consumes the
        // render appFrand stream in the exact order of the banked RE map.
        // The port runs the same traversal: DrawFrame steps a ParticleFX
        // (RenderStepFx) when its sprite is drawn, once per actor per frame, so a
        // culled emitter is not updated, as in shipped. LOD (+0x300) keeps the
        // ctor value 1.0 or a loaded override: Render.dll has no operand at
        // +0x300 at all (disasm scan), so nothing in the renderer writes it.
        static float PropF(UEObject o, string p) => UEValue.AsFloat(o.GetProp(p));

        float SampleRange(UEObject src, string prop)
        {
            // GetParams quirk (Engine.dll.c:30726): appFrand is consumed ONLY
            // when Rand != 0; Rand == 0 returns Base with NO call.
            if (src.GetProp(prop) is not UEObject bag) return 0f;
            float b = UEValue.AsFloat(bag.GetProp(PK.Base));
            float r = UEValue.AsFloat(bag.GetProp(PK.Rand));
            if (r == 0f) return b;
            return Frand() * r + b;
        }

        UEVector SampleColor(UEObject src, string prop)
        {
            // ColorParams {Base:{R,G,B}, Rand:{R,G,B}} — one conditional
            // appFrand for the whole color (byte components / 255).
            if (src.GetProp(prop) is not UEObject bag) return default;
            var bs = bag.GetProp(PK.Base) as UEObject;
            var rn = bag.GetProp(PK.Rand) as UEObject;
            float br = UEValue.AsByte(bs?.GetProp(PK.R)), bg = UEValue.AsByte(bs?.GetProp(PK.G)), bb = UEValue.AsByte(bs?.GetProp(PK.B));
            float rr = UEValue.AsByte(rn?.GetProp(PK.R)), rg = UEValue.AsByte(rn?.GetProp(PK.G)), rb = UEValue.AsByte(rn?.GetProp(PK.B));
            // The "no random" test compares the whole FColor dword, alpha included (`cmp [esi+0x2b4], ebx` 0x103c554c,
            // `[esi+0x2bc]` 0x103c5704).
            // The sample stays an FColor (bytes) in FParams; EmitParticles converts it (ColorByteScale below).
            if (rr == 0f && rg == 0f && rb == 0f && UEValue.AsByte(rn?.GetProp("A")) == 0)
                return new UEVector(br, bg, bb);
            // AParticleFX::GetParams (Engine.dll.c:30836-30945): channel addend —
            // ROUND(Rand·f), clamped to 0..255, the sum with Base saturates at 255.
            // The original particle colour never exceeds 1.
            float f = Frand();
            static float Sat(float rand, float bas, float f)
            {
                int add = (int)MathF.Round(rand * f);   // fistp: round to nearest even
                if (add < 0) add = 0; else if (add > 254) add = 255;
                int sum = (int)bas + add;
                if (sum > 254) sum = 255;
                return sum;
            }
            return new UEVector(Sat(rr, br, f), Sat(rg, bg, f), Sat(rb, bb, f));
        }

        // The per-particle FParams sample (GetParams, Engine.dll.c:30726). SpreadW, SpreadH, SourceW, SourceD, SourceH,
        // Speed, Lifetime, AlphaStart, AlphaEnd in the shipped order (disasm 0x103c5315..0x103c5417 reads actor
        // +0x28c, +0x294, +0x264, +0x274, +0x26c), then SpinRate (+0x2e8, 0x103c54e6) and DripTime (+0x2f0, 0x103c5519)
        // BEFORE ColorStart (+0x2b4, 0x103c554c) / ColorEnd (+0x2bc, 0x103c5704), then the size (Width/Length share one
        // appFrand, 0x103c58c1), SizeEndScale (+0x2e0, 0x103c58ea) last. The order decides which draw lands in which
        // field — the particle colour is visible (live batch 9 particle dumps: Color.y off by up to 28/255 with the
        // former Color-first order).
        struct FParams
        {
            public float SpreadW, SpreadH, SourceW, SourceD, SourceH;
            public float Speed, Lifetime, AlphaStart, AlphaEnd;
            public UEVector ColorStart, ColorEnd;
            public float Width, Length, SizeEndScale, SpinRate, DripTime;
        }

        FParams GetParams(UEObject src)
        {
            FParams p;
            p.SpreadW = SampleRange(src, "AngularSpreadWidth");
            p.SpreadH = SampleRange(src, "AngularSpreadHeight");
            p.SourceW = SampleRange(src, "SourceWidth");
            p.SourceD = SampleRange(src, "SourceDepth");
            p.SourceH = SampleRange(src, "SourceHeight");
            p.Speed = SampleRange(src, "Speed");
            p.Lifetime = SampleRange(src, "Lifetime");
            p.AlphaStart = SampleRange(src, "AlphaStart");
            p.AlphaEnd = SampleRange(src, "AlphaEnd");
            p.SpinRate = SampleRange(src, "SpinRate");
            p.DripTime = SampleRange(src, "DripTime");
            p.ColorStart = SampleColor(src, "ColorStart");
            p.ColorEnd = SampleColor(src, "ColorEnd");
            // Width/Length: ONE shared UNCONDITIONAL appFrand (30870 tail).
            float f = Frand();
            float wb = 0f, wr = 0f, lb = 0f, lr = 0f;
            if (src.GetProp(PK.SizeWidth) is UEObject wbag)
            { wb = UEValue.AsFloat(wbag.GetProp(PK.Base)); wr = UEValue.AsFloat(wbag.GetProp(PK.Rand)); }
            if (src.GetProp(PK.SizeLength) is UEObject lbag)
            { lb = UEValue.AsFloat(lbag.GetProp(PK.Base)); lr = UEValue.AsFloat(lbag.GetProp(PK.Rand)); }
            p.Width = f * wr + wb;
            p.Length = f * lr + lb;
            p.SizeEndScale = SampleRange(src, "SizeEndScale");
            return p;
        }

        // AParticleFX::EmitParticles (Engine.dll.c:23044) — the banked RE map.
        // Default, Pattern and OwnerMesh distributions are ported; ColorPalette
        // and a fractional ParentBlend STOP loudly (no carriers in the campaign).
        void EmitParticles(UEObject fx, FxState s, float dt)
        {
            float emitDelay = PropF(fx, "EmitDelay") + dt;
            fx.Props["EmitDelay"] = emitDelay;
            UEVector loc = UEValue.AsVector3(fx.GetProp(PK.Location));
            // pps sample — UNCONDITIONAL appFrand (23245).
            float pps = Frand() * RangeRandRaw(fx, "ParticlesPerSec") + RangeBase(fx, "ParticlesPerSec");
            // ── ParentBlend (23246 + AParticleFX::GetSysParams, 17069) ─────
            //
            // The second parameter source is the PARENT CLASS DEFAULT.
            // Shipped chain: emitter->Class (UObject+0x24) -> SuperField
            // (UStruct+0x28) -> +0x4dc. The middle link is PROVEN, not inferred:
            // the UStruct::IsChildOf body (Core.dll.c) walks ancestors
            // by exactly this offset — `this = *(UStruct**)(this + 0x28)`.
            //
            // The two forks are DIFFERENT and must not be merged:
            //   * the pps counter is blended when ParentBlend != 0 (23246), and the
            //     SECOND appFrand is spent unconditionally, even with blend outside [0,1];
            //   * the particle parameter source (GetSysParams) is chosen by
            //     `<= 0` -> the emitter itself, `>= 1` -> the parent default, in between ->
            //     a blended copy of the whole property block.
            float parentBlend = PropF(fx, "ParentBlend");
            UEObject sysParams = fx;
            if (parentBlend != 0f)
            {
                UEObject parentDef = ParentClassDefaults(fx);
                float pf = Frand();
                float parentPps = pf * RangeRandRaw(parentDef, "ParticlesPerSec")
                                + RangeBase(parentDef, "ParticlesPerSec");
                pps = (parentPps - pps) * parentBlend + pps;
                if (parentBlend > 0f)
                {
                    // FRACTIONAL BLENDING IS NOT PORTED — MEASURED, NOT DEFERRED
                    // BLINDLY: across the whole campaign ParentBlend takes
                    // only 0 or 1 (SpellLearnTrigger.uc:710-723 switches
                    // two systems, ParticleFadeFX.uc:8-14 likewise, and
                    // BroomHarry.uc:273 uses Min(Speed/200, 1), an integer).
                    // Nobody sets intermediate values.
                    if (parentBlend < 1f)
                        throw new NotSupportedException(
                            $"ParticleFX ParentBlend={parentBlend} ({fx.Class?.Name}) — fractional "
                            + "blending copies the whole property block (0x3cc) and lerps ~22 fields "
                            + "plus colors (17096-17535); the campaign has no such value");
                    sysParams = parentDef;
                }
            }
            int distribution = UEValue.ToInt(fx.GetProp(PK.Distribution));
            // ── DIST_Pattern / DIST_Path (23252-23281) ────────────────────
            //
            // With Distribution==1 ParticlesPerSec is REINTERPRETED as a STEP, i.e.
            // how many length units per particle — the counter is obtained by
            // DIVIDING the length by it (23278), not by multiplying by time
            // as in the other modes.
            //
            // Pattern is a Gesture (an object with a Points array; set from
            // script: SpellLearnFX.DrawSpell "Sparkle.Pattern = Gesture",
            // SpellLearnTrigger, Target). The meaning of Period comes from there too:
            // DrawState.Tick drives Period.Base as PROGRESS along the gesture in [0,1],
            // and Period.Rand as the progress increment FOR THIS FRAME.
            //
            // THE FORK IS ON THE `Pattern` OBJECT ITSELF (23253 tests the
            // pointer), NOT on its array: an object without usable points is
            // not "a path without a pattern", and routing it to another sub-path would
            // silently diverge from shipped. That case stops out loud.
            //
            // THE PATTERN HAS TWO ROLES, AND ONLY ONE DEPENDS ON Distribution (disasm
            // EmitParticles 0x103c2170): COUNT by pattern length — with
            // Distribution == 1 (0x103c2272/0x103c22cd); SPAWN POINT — with
            // any Distribution ≠ 2 (the mesh branch) if Pattern is non-empty
            // (0x103c2819 `cmp Distribution,2; jne 0x103c2a3e` -> 0x103c2a3e
            // `mov esi,[ebx+0x338]; test esi,esi`). Target.DrawSpellFX sets
            // Pattern = the spell gesture on Les_SpellShape/SpellShape_* effects
            // (Distribution = 0): in shipped particles follow the symbol outline
            // stretched to DrawScale = target size.
            object patternRaw = fx.GetProp(PK.Pattern);
            var patternObj = patternRaw as UEObject;
            if (patternRaw is not null && patternObj is null)
                throw new NotSupportedException(
                    $"ParticleFX DIST_Pattern ({fx.Class?.Name}): Pattern holds "
                    + $"{patternRaw.GetType().Name}, not an object with a Points array");
            var patternPoints = patternObj?.GetProp(PK.Points) as System.Collections.IList;
            if (patternObj is not null && (patternPoints is null || patternPoints.Count < 2))
                throw new NotSupportedException(
                    $"ParticleFX DIST_Pattern ({fx.Class?.Name}): Pattern has "
                    + $"{patternPoints?.Count.ToString() ?? "no array"} points. The shipped body "
                    + "indexes points[idx+1] without a lower bound check and with <2 points reads foreign "
                    + "memory; the script (SpellLearnFX.DrawSpell) rejects such a gesture itself");
            // ── DIST_OwnerMesh (23386-23402): preparation once per call ──
            //
            // Entry: Distribution==2 AND an Owner exists AND it has a Mesh. Vertices
            // come from GetFrame with UNIT coords (GMath+0x18) — and
            // come out IN WORLD space: the GetFrame root divides the actor's mesh coords from
            // the ApplyAnim frame header (+0x20 = GetMeshCoords) by the
            // inverse of the input ones (UELevelRuntime.EvaluatePosePoints, disasm there).
            UEVector[] meshPoints = null;
            int[] meshTris = null;
            if (distribution == 2)
            {
                var owner = fx.GetProp(PK.Owner) as UEObject;
                var meshRef = owner?.GetProp(PK.Mesh) as UEResourceRef;
                if (owner is null || meshRef is null)
                    throw new NotSupportedException(
                        $"ParticleFX DIST_OwnerMesh ({fx.Class?.Name}): no Owner or it has no Mesh — "
                        + "shipped does not emit from the mesh in this case, and the port must see it");
                if (MeshCatalog is null)
                    throw new NotSupportedException(
                        $"ParticleFX DIST_OwnerMesh ({fx.Class?.Name}): mesh catalog not supplied to the producer");
                // FRAME POSE — from Core (UELevelRuntime.SkeletalPose, port of
                // ApplyAnim + GetFrame): GetFrame with unit coords gives
                // world-space vertices. Without the runtime hook (the producer
                // does not hold the level) — a LOUD STOP, not a silent reference pose.
                object seq = owner.GetProp(PK.AnimSequence);
                string seqName = seq?.ToString();
                Hp1SkeletalMesh sk = MeshCatalog.Get(meshRef);
                if (PosePoints is null)
                    throw new NotSupportedException(
                        $"ParticleFX DIST_OwnerMesh ({fx.Class?.Name}): owner pose not bound to the producer "
                        + $"(AnimSequence='{seqName}')");
                meshPoints = PosePoints(owner);
                OwnerMeshPointsForTest?.Invoke(fx, meshPoints);
                meshTris = sk.TriVerts;
                if (meshTris.Length == 0)
                    throw new NotSupportedException(
                        $"ParticleFX DIST_OwnerMesh ({fx.Class?.Name}): mesh {meshRef.Name} has no faces");
            }
            float count;
            if (distribution == 1)
            {
                // The LENGTH over which particles are laid out differs between
                // the two sub-paths (23253-23279). Both end with a division by pps.
                if (patternObj is null)
                {
                    // Without Pattern — the emitter's path length over the frame (23256).
                    UEVector le = UEValue.AsVector3(fx.GetProp(PK.LastEmitLocation));
                    float px = le.x - loc.x, py = le.y - loc.y, pz = le.z - loc.z;
                    count = (float)Math.Sqrt(px * px + py * py + pz * pz);
                }
                else
                {
                    // With Pattern — an estimate of the gesture arc length DRAWN this frame:
                    // the length of ONE segment (taken at the MIDDLE of the window: shipped
                    // substitutes the literal 0.5 where a particle's
                    // fraction would go — .rdata 0x1046e9c0), times the number of
                    // segments, DrawScale and the frame share Period.Rand.
                    float periodRand0 = RangeRandRaw(fx, "Period");
                    int np0 = patternPoints.Count;
                    int idx0 = PatternSegment(0.5f, RangeBase(fx, "Period"), periodRand0, np0, out _);
                    UEVector c0 = UEValue.AsVector3(patternPoints[idx0]);
                    UEVector n0 = UEValue.AsVector3(patternPoints[idx0 + 1]);
                    float sx = c0.x - n0.x, sy = c0.y - n0.y, sz = c0.z - n0.z;
                    float segLen = (float)Math.Sqrt(sx * sx + sy * sy + sz * sz);
                    // Multiplication order as in the body (0x103c2367-0x103c2372):
                    // first (np-1)·DrawScale, then by the length, then by the share.
                    count = segLen * ((np0 - 1) * PropF(fx, "DrawScale")) * periodRand0;
                }
                count /= pps;
            }
            else
            {
                // count = clamp(LOD*3, 0.1..1) * pps * EmitDelay (+ residue).
                float lod = PropF(fx, "LOD");
                float lodF = lod * 3f;
                count = lodF >= 0.1f
                    ? (lodF >= 1f ? 1f : lodF) * pps * emitDelay
                    : pps * 0.1f * emitDelay;
            }
            count += PropF(fx, "EmissionResidue");
            float windowStart = -emitDelay;
            int emitted = UEValue.ToInt(fx.GetProp(PK.ParticlesEmitted));
            int particlesMax = UEValue.ToInt(fx.GetProp(PK.ParticlesMax));
            if (particlesMax > 0)
            {
                float remaining = particlesMax - emitted;
                if (remaining <= count)
                {
                    emitDelay = (remaining / count) * emitDelay;
                    fx.Props["EmitDelay"] = emitDelay;
                    count = remaining;
                }
            }
            float lifeCap = RangeBase(fx, "Lifetime") + RangeRandPos(fx, "Lifetime");
            if (windowStart < -lifeCap)
            {
                float trimmed = lifeCap + windowStart + emitDelay;
                count = (trimmed / emitDelay) * count;
                fx.Props["EmitDelay"] = emitDelay = trimmed;
                windowStart = -lifeCap;
            }
            // ftol (23412 `iStack_6c = ftol()`) — truncation TOWARD ZERO. Inside the
            // range it is an ordinary cast; OUTSIDE it x87 yields the "integer
            // indefinite", and in either form (0 for the qword variant,
            // 0x80000000 for dword) the very next check `0 < n` is false — i.e.
            // shipped spawns NO particles. C# SATURATES the same input to
            // int.MaxValue, which would give a two-billion-iteration loop:
            // a divergence from the original, not a guard against it.
            //
            // The input is reachable because of the division by pps (with Distribution==1
            // ParticlesPerSec is the DENOMINATOR). Zero never occurs there in the campaign —
            // MEASURED: the base class default ParticlesPerSec.Base = 3, and none of the
            // five classes overrides it (the pattern fixture prints the measurement).
            int n = float.IsNaN(count) || count >= 2147483648f || count <= -2147483648f
                ? 0 : (int)count;
            fx.Props["EmissionResidue"] = count - n;
            if (n <= 0) return;
            int aliveLimit = UEValue.ToInt(fx.GetProp(PK.ParticlesAlive));
            if (aliveLimit > 0 && s.Particles.Count + n > aliveLimit)
            {
                if (aliveLimit < n) n = aliveLimit;
                int evict = s.Particles.Count - aliveLimit + n;
                // Shipped evicts the OLDEST via the container virtuals.
                if (evict > 0) s.Particles.RemoveRange(0, Math.Min(evict, s.Particles.Count));
            }
            bool velRelative = (UEValue.ToInt(fx.GetProp(PK.bVelocityRelative)) != 0)
                || UEValue.ToBool(fx.GetProp(PK.bVelocityRelative));
            UEVector ownerVel = velRelative && fx.GetProp(PK.Owner) is UEObject own
                ? UEValue.AsVector3(own.GetProp(PK.Velocity)) : default;
            int renderPrimitive = UEValue.ToInt(fx.GetProp(PK.RenderPrimitive));
            if (fx.GetProp(PK.ColorPalette) is not null)
                throw new NotSupportedException(
                    $"ParticleFX ColorPalette reached ({fx.Class?.Name}) — the palette ramp is not ported yet");
            var rot = UEValue.AsRotator(fx.GetProp(PK.Rotation));
            // The emitter coords (0x103c249a..0x103c2519: GMath.UnitCoords, Origin -= Location, FCoords::operator*=(Rotation)
            // through the Core import [0x10608cd0], render window PC24; kept at ebp-0x248): the spread direction
            // (0x103c2d6e..0x103c2dc2) and the volume offset (0x103c2e94..) are FVector::TransformVectorBy on THESE axes,
            // (x·C.X.x + y·C.X.y) + z·C.X.z per component.
            // Not GetAxes (UnitCoords / R, transposed): the same rotation by other roundings (Vel 1 ulp, Lev5_FlyKeys).
            BrushCoords emitCoords = BrushCoords.Identity;
            emitCoords.MulRotator(rot.Pitch, rot.Yaw, rot.Roll);
            // UParticle +0x78 ChaosTimer and +0x7c SpinPos of the stack particle (ebp-0x17c): neither the ctor
            // (0x103bfbb0: +0..+0x24, +0x84) nor EmitParticles writes them (SpinPos only for RenderPrimitive 3,
            // 0x103c31f6..0x103c321b), so each particle inherits the previous one's values after its catch-up
            // (gated-out particles leave the slot as it was). The first particle reads ebp-0x104/-0x100 as the caller
            // left them: EmitParticles and UpdateParticles are called from the same esp (AParticleFX::Update
            // 0x103c4098 / 0x103c411e), so the slot is what the caller path and UpdateParticles' callees left there
            // (_slot*, UpdateFxParticles): per path (D-73). On the render path every particle's values after its catch-up
            // stay at that absolute address for the next render-path call (RenderSlotChaos/Spin).
            bool carryKnown = false;
            float carryChaos = 0f, carrySpin = 0f;
            for (int i = 0; i < n; i++)
            {
                // The parameter source is the GetSysParams result, not always the
                // emitter itself: shipped calls it ONCE before the loop and feeds it to
                // GetParams for every particle. Other fields (LOD, Rotation,
                // Location) are still read from `fx`, as in the body.
                FParams fp = GetParams(sysParams);
                // fraction: with Distribution==1 it is DETERMINISTIC — (i+1)/count
                // (23407-23415), and the appFrand at the loop head is NOT SPENT AT ALL.
                // That is the only difference of this mode's RNG stream from the normal one:
                // spread and volume below consume it the same way (unlike the mesh
                // branch, which REPLACES the two spread calls). Divided by the
                // FRACTIONAL count — the same one n was taken from, before truncation.
                float fraction = distribution == 1
                    ? (count <= 0f ? 0f : (i + 1) / count)
                    : Frand();
                var p = new Particle();
                float birthOffset = -windowStart - fraction * emitDelay;
                // Birth gate (disasm 0x103c27e6..0x103c2813): `fcomp [ebp-0xc4]` (the sampled Lifetime, FParams+0x24);
                // `test ah,1; je 0x103c353b` — a birth offset NOT below its Lifetime (NaN passes) skips the particle
                // entirely: no mesh/spread/volume/SpinPos draws, no catch-up, no AddParticle (vtable+0x78 at
                // 0x103c3538). GetParams and the fraction draw above are already spent; ParticlesEmitted still counts n.
                if (birthOffset >= fp.Lifetime) continue;
                // Direction (23502): TWO appFrand(min,max) spread half-angles in
                // DEGREES→rad (·0.01745329), quantized to rotator units and
                // pushed through the GMath sine table; the local direction is
                // (cosYaw·cosPitch, sinYaw·cosPitch, sinPitch) — the rotator→
                // vector convention — rotated by the emitter's axes.
                // ── DIST_OwnerMesh: mesh sampling REPLACES the spread ─────────
                // RNG order read from the whole fork 23431-23530:
                // appRandRange(triangle) -> two appFrand (barycentric), and that is
                // INSTEAD of the two spread appFrands, not in addition. The three volume
                // calls below are the common tail of both branches.
                UEVector meshBase = default, meshNormal = default;
                if (meshPoints is not null)
                {
                    int tri = RandRange(0, meshTris.Length / 3 - 1);
                    // WHICH SLOT IS WHICH — from the disassembly, not the decompile
                    // (Ghidra's iStack_294/298/29c are ebp-0x290/-0x294/-0x298):
                    // GetTriVerts writes out[0..2] at ebp-0x298 (0x103c2859);
                    // f2*f1 multiplies out[2] (0x103c2887), (1-f2)*f1 out[1]
                    // (0x103c288d), 1-f1 out[0] (0x103c28a1); the normal is
                    // (out[1]-out[0]) x (out[2]-out[0]) (0x103c2971..0x103c29e2).
                    // So v0 below is out[2] and v2 is out[0]. Reading them the
                    // other way round flipped every face normal (the sparks flew
                    // INTO the mesh) and moved the 1-f1 weight to the wrong corner
                    // (Lev_Tut3 Lock0 cloud ~20% tighter than the original's).
                    UEVector v0 = meshPoints[meshTris[tri * 3 + 2]];
                    UEVector v1 = meshPoints[meshTris[tri * 3 + 1]];
                    UEVector v2 = meshPoints[meshTris[tri * 3]];
                    float f1 = Frand();
                    float f2 = Frand();
                    float w0 = f2 * f1, w1 = (1f - f2) * f1, w2 = 1f - f1;
                    meshBase = new UEVector(
                        w2 * v2.x + w1 * v1.x + w0 * v0.x,
                        w2 * v2.y + w1 * v1.y + w0 * v0.y,
                        w2 * v2.z + w1 * v1.z + w0 * v0.z);
                    // Face normal: cross(v1-v2, v0-v2), then SafeNormal.
                    float e0x = v0.x - v2.x, e0y = v0.y - v2.y, e0z = v0.z - v2.z;
                    float e1x = v1.x - v2.x, e1y = v1.y - v2.y, e1z = v1.z - v2.z;
                    // SafeNormal with SHIPPED semantics explicitly: below the threshold
                    // it returns ZERO, not normalised noise. Bare
                    // `v.normalized` copies in the tree are known to be wrong and must not
                    // be used here.
                    float nx = e1y * e0z - e1z * e0y;
                    float ny = e1z * e0x - e1x * e0z;
                    float nz = e1x * e0y - e1y * e0x;
                    // Core FVector::SafeNormal (0x1014f4e0): (x² + y²) + z², below [0x1017d72c] zero; otherwise
                    // scale = 1 / appSqrt(sum) (fdivr 0x1014f557) and each component is MULTIPLIED by it (0x1014f566..
                    // 0x1014f581) — not divided by the root.
                    float sq = nx * nx + ny * ny + nz * nz;
                    float invLen = sq < 1e-8f ? 0f : 1f / (float)Math.Sqrt(sq);
                    meshNormal = sq < 1e-8f ? default
                        : new UEVector(invLen * nx, invLen * ny, invLen * nz);
                }
                // ── DIST_Pattern: the base point is a GESTURE POINT (23470-23496) ──
                //
                // The branch lives in THE SAME else as the normal base, so it does NOT
                // cancel the spread below; it does not touch the RNG at all.
                // Position along the gesture: x = (fraction·Period.Rand + Period.Base)
                // ·(N−1) — frame progress plus the particle's share within the frame;
                // the integer part selects the segment, the fraction — linear interpolation
                // between its ends.
                UEVector patternLocal = default;
                if (patternPoints is not null)
                {
                    int np = patternPoints.Count;
                    int idx = PatternSegment(fraction, RangeBase(fx, "Period"),
                                             RangeRandRaw(fx, "Period"), np, out float f);
                    UEVector cur = UEValue.AsVector3(patternPoints[idx]);
                    UEVector nxt = UEValue.AsVector3(patternPoints[idx + 1]);
                    float lerpX = (1f - f) * cur.x + f * nxt.x;
                    float lerpY = (1f - f) * cur.y + f * nxt.y;
                    // The gesture is normalised to [0,1]²: it is centred by subtracting
                    // 0.5, scaled by DrawScale and placed in the Y-Z PLANE
                    // of the emitter's local axes — X exactly zero, and the second
                    // gesture coordinate inverted (screen order
                    // top to bottom): {0, S·(x−0.5), S·(0.5−y)}.
                    float scale = PropF(fx, "DrawScale");
                    patternLocal = new UEVector(0f, scale * (lerpX - 0.5f), (0.5f - lerpY) * scale);
                }
                float yawRad = meshPoints is not null ? 0f
                    : FrandRange(fp.SpreadW * -DegToRad, fp.SpreadW * DegToRad);
                float pitchRad = meshPoints is not null ? 0f
                    : FrandRange(fp.SpreadH * -DegToRad, fp.SpreadH * DegToRad);
                int yawU = AngleUnits(yawRad), pitchU = AngleUnits(pitchRad);
                float cosPitch = GTabSin(pitchU + 0x4000);
                float lx = GTabSin(yawU + 0x4000) * cosPitch;
                float ly = GTabSin(yawU) * cosPitch;
                float lz = GTabSin(pitchU);
                // In the mesh branch the direction is the FACE NORMAL (23455-23468), not
                // the rotated spread: shipped puts SafeNormal(cross) straight into the
                // same direction slot.
                UEVector dir = meshPoints is not null ? meshNormal : new UEVector(
                    (lx * emitCoords.X.x + ly * emitCoords.X.y) + lz * emitCoords.X.z,
                    (lx * emitCoords.Y.x + ly * emitCoords.Y.y) + lz * emitCoords.Y.z,
                    (lx * emitCoords.Z.x + ly * emitCoords.Z.y) + lz * emitCoords.Z.z);
                // Source volume: THREE appFrand(−size/2, +size/2) — the sampled
                // SourceW/D/H are BOX SIZES and the offsets are CENTRED. From the
                // disassembly (FParams at ebp−0xe8, GetParams 0x103c52f0 stores
                // SourceWidth +4, SourceHeight +8, SourceDepth +0xc): the draws
                // are Height (0x103c2ddd, [ebp−0xe0]), Width (0x103c2e09,
                // [ebp−0xe4]), Depth (0x103c2e35, [ebp−0xdc]); the vector at
                // ebp−0x7c is stored (Depth, Width, Height) (0x103c2e5e..
                // 0x103c2e7f) and transformed by the emitter coords, so
                // Depth→X (forward), Width→Y, Height→Z.
                float offH = FrandRange(fp.SourceH * -0.5f, fp.SourceH * 0.5f);
                float offW = FrandRange(fp.SourceW * -0.5f, fp.SourceW * 0.5f);
                float offD = FrandRange(fp.SourceD * -0.5f, fp.SourceD * 0.5f);
                // Position (23611): base + rotated box + (LastEmitLocation −
                // Location)·(1−fraction): newborns (fraction→1) at the CURRENT
                // location, window-old ones back along the emitter's path
                // (zero on the first update — the 106729 prelude seeds
                // LastEmitLocation = Location before any emission).
                UEVector lastEmit = UEValue.AsVector3(fx.GetProp(PK.LastEmitLocation));
                float back = 1f - fraction;
                // BASE POINT: normally the emitter Location (23472-23474), in the
                // mesh branch the sampled surface point (23461-23463),
                // and in the gesture branch the pattern point MOVED INTO THE WORLD. Shipped
                // does the latter via FVector::TransformPointBy over the
                // coords "GMath.UnitCoords with Origin=−Location, rotated
                // by Rotation" (23484-23496).
                //
                // WHAT THAT POINT IS WAS MEASURED BY EXECUTION, NOT DERIVED FROM
                // CONVENTIONS: the real Core.dll bodies (FCoords::operator*=
                // ord 501 and TransformPointBy ord 1365) run under unicorn with
                // FPCW=0x037F on 6 (Location, Rotation) pairs; exactly the
                // variant "Location + local vector along the emitter axes" matched
                // (worst difference 6.7e-05 = float noise vs table
                // sines), while the inverse convention misses by 10
                // units. So it is THE SAME technique as already used below for the
                // volume offsets, and the oracle confirmed that too.
                // (Not judged: no DIST_Pattern sample; keeps the GetAxes form the unicorn check above measured.)
                var (axX, axY, axZ) = patternPoints is not null ? UEValue.GetAxes(rot) : default;
                UEVector emitBase = meshPoints is not null ? meshBase
                    : patternPoints is not null
                        ? new UEVector(
                            loc.x + axY.x * patternLocal.y + axZ.x * patternLocal.z,
                            loc.y + axY.y * patternLocal.y + axZ.y * patternLocal.z,
                            loc.z + axY.z * patternLocal.y + axZ.z * patternLocal.z)
                        : loc;
                // Order (render window PC24, every op rounds to float): the volume offset (Depth, Width, Height) goes
                // through the coords FIRST — (D·a + W·b) + H·c per component (0x103c2e94..0x103c2ee8) — then
                // offset + base (0x103c2eeb..0x103c2f2f), then + (1 − fraction)·(LastEmitLocation − Location)
                // (0x103c2f35..0x103c2f7c).
                UEVector vol = new UEVector(
                    (offD * emitCoords.X.x + offW * emitCoords.X.y) + offH * emitCoords.X.z,
                    (offD * emitCoords.Y.x + offW * emitCoords.Y.y) + offH * emitCoords.Y.z,
                    (offD * emitCoords.Z.x + offW * emitCoords.Z.y) + offH * emitCoords.Z.z);
                p.Pos = new UEVector(
                    (vol.x + emitBase.x) + back * (lastEmit.x - loc.x),
                    (emitBase.y + vol.y) + back * (lastEmit.y - loc.y),
                    (emitBase.z + vol.z) + back * (lastEmit.z - loc.z));
                p.Vel = new UEVector(dir.x * fp.Speed + ownerVel.x,
                                     dir.y * fp.Speed + ownerVel.y,
                                     dir.z * fp.Speed + ownerVel.z);
                p.OldPos = p.Pos;
                p.Lifetime = fp.Lifetime;
                p.Alpha = PropF(fx, "AlphaGrowPeriod") > 0f ? 0f : fp.AlphaStart;
                p.AlphaMax = fp.AlphaStart;
                p.AlphaDelta = fp.Lifetime > PropF(fx, "AlphaDelay")
                    ? (fp.AlphaEnd - fp.AlphaStart) / (fp.Lifetime - PropF(fx, "AlphaDelay"))
                    : 0f;
                // FColor -> FPlane (Engine.dll.c:23579..23585): byte · 0.003921569 (a float multiply, not /255); the
                // delta takes the INTEGER byte difference (end − start) · 0.003921569, then / (Lifetime − ColorDelay)
                // (23614..23620).
                p.Color = new UEVector(fp.ColorStart.x * ColorByteScale, fp.ColorStart.y * ColorByteScale, fp.ColorStart.z * ColorByteScale);
                float colorSpan = fp.Lifetime - PropF(fx, "ColorDelay");
                p.ColorDelta = fp.Lifetime > PropF(fx, "ColorDelay")
                    ? new UEVector((fp.ColorEnd.x - fp.ColorStart.x) * ColorByteScale / colorSpan,
                                   (fp.ColorEnd.y - fp.ColorStart.y) * ColorByteScale / colorSpan,
                                   (fp.ColorEnd.z - fp.ColorStart.z) * ColorByteScale / colorSpan)
                    : default;
                p.Width = fp.Width; p.WidthMax = fp.Width;
                p.Length = fp.Length; p.LengthMax = fp.Length;
                // SizeGrowPeriod > 0 (0x103c2f82..0x103c2fad): Width (+0x58) and Length (+0x64) start at 0; the Max
                // fields keep the sample.
                if (PropF(fx, "SizeGrowPeriod") > 0f) { p.Width = 0f; p.Length = 0f; }
                if (fp.DripTime > 0f || fp.DripTime < 0f)
                {
                    // DripTime != 0 (`test ah,0x40; je 0x103c304f`, 0x103c304f..0x103c3081): the deltas spread the
                    // CURRENT size (already 0 after SizeGrowPeriod) over the drip, then the size zeroes.
                    p.WidthDelta = p.Width / fp.DripTime; p.Width = 0f;
                    p.LengthDelta = p.Length / fp.DripTime; p.Length = 0f;
                }
                else
                {
                    float endScale = fp.SizeEndScale;
                    float sizeSpan = fp.Lifetime - PropF(fx, "SizeDelay");
                    p.WidthDelta = sizeSpan > 0f ? (fp.Width * endScale - fp.Width) / sizeSpan : 0f;
                    p.LengthDelta = sizeSpan > 0f ? (fp.Length * endScale - fp.Length) / sizeSpan : 0f;
                }
                p.DripTimer = fp.DripTime;
                // D-73: the first particle of a call reads the slot (_slotKind): the bSystemRelative YAxis residue, the
                // Setup BoundVisible residue (GetRenderBoundingBox path) or the chain (render path).
                if (!carryKnown)
                {
                    carryChaos = _slotChaos; carrySpin = _slotSpin;
                    // Validation seam only (headless, InternalsVisibleTo): the live comparison supplies the value the
                    // original's first particle read where the original's slot did not come from game code (D-73: the
                    // render device path overwrote it — a driver value, deliberately not emulated).
                    if (FirstNewbornSlotForTest?.Invoke(s.Particles.Count, _grbbPath, (carryChaos, carrySpin)) is var (rc, rs))
                    { carryChaos = rc; carrySpin = rs; }
                    if (_slotKind == SlotKind.Chain) ChainParticleSlot++;
                    else if (_slotKind == SlotKind.SetupBoundVisible) SetupResidueParticleSlot++;
                    else if (_slotKind == SlotKind.SysRel) SysRelParticleSlot++;
                }
                p.ChaosTimer = carryChaos;
                p.SpinRate = fp.SpinRate;
                // RenderPrimitive 3: random spin phase (appFrand * 2π, 23598).
                p.SpinPos = renderPrimitive == 3 ? Frand() * 6.2831855f : carrySpin;
                // Newborn back-integration (23644) runs AFTER the particle is
                // FULLY initialized (Lifetime, ramp deltas, DripTimer,
                // SpinRate all set: calling it earlier
                // advanced position/Age on a half-built particle): a particle
                // born mid-window is caught up to "now" by its birth offset
                // through the same substep loop as the update pass.
                if (birthOffset > 0f) AdvanceNewborn(fx, p, birthOffset);
                carryKnown = true; carryChaos = p.ChaosTimer; carrySpin = p.SpinPos;
                if (!_grbbPath) { RenderSlotChaos = p.ChaosTimer; RenderSlotSpin = p.SpinPos; }
                // After the catch-up, before AddParticle (0x103c34f8..0x103c3529): CurrentPriorityTag (+0x3c4) += 1, wrapped
                // to 0 above 9 (`cmp eax, 9; jle`), and the particle takes PriorityTable[CurrentPriorityTag].
                int tag = UEValue.ToInt(fx.GetProp(PK.CurrentPriorityTag)) + 1;
                if (tag > 9) tag = 0;
                fx.Props["CurrentPriorityTag"] = tag;
                p.Priority = PriorityTable[tag];
                s.Particles.Add(p);
            }
            // ParticlesEmitted += n (0x103c3567..0x103c3575) — gated-out particles count too.
            fx.Props["ParticlesEmitted"] = emitted + n;
            fx.Props["EmitDelay"] = 0f;
            fx.Props["LastEmitLocation"] = loc;
        }

        static float RangeRandRaw(UEObject o, string p)
            => o.GetProp(p) is UEObject bag ? UEValue.AsFloat(bag.GetProp(PK.Rand)) : 0f;

        internal UEObject ParentClassDefaultsForTest(UEObject fx) => ParentClassDefaults(fx);

        UEObject ParentClassDefaults(UEObject fx)
        {
            UELib.Core.UClass cls = fx.Class
                ?? throw new NotSupportedException("ParticleFX ParentBlend: the emitter has no class");
            if (_parentDefaults.TryGetValue(cls, out UEObject cached)) return cached;
            UELib.Core.UClass super = HP1.Core.UEClassRegistry.Shared.CanonicalSuper(cls)
                ?? throw new NotSupportedException(
                    $"ParticleFX ParentBlend ({cls.Name}): the class has no parent, while shipped "
                    + "dereferences SuperField unconditionally");
            var def = new UEObject { Class = super, Interp = fx.Interp };
            def.LoadDefaults();
            _parentDefaults[cls] = def;
            return def;
        }

        // Gesture segment selection for DIST_Pattern — the same expression in two
        // places of the shipped body: in the particle count (0x103c22de, with the literal
        // 0.5 in place of fraction) and per particle (0x103c2a4c).
        //
        // Truncation is `ftol`, i.e. TOWARD ZERO, like a C# int cast. Shipped has an
        // upper bound (min with N−2) but NO LOWER one: with negative
        // progress the body indexes memory before the array. The port stops
        // loudly — silently returning point 0 would replace the original's
        // undefined behaviour with something plausible.
        static int PatternSegment(float fraction, float periodBase, float periodRand,
                                  int numPoints, out float frac)
        {
            float x = (fraction * periodRand + periodBase) * (numPoints - 1);
            int idx = (int)x;
            if (idx > numPoints - 2) idx = numPoints - 2;
            if (idx < 0)
                throw new NotSupportedException(
                    $"ParticleFX DIST_Pattern: gesture segment {idx} < 0 (Period progress "
                    + $"{periodBase}+{fraction}·{periodRand}, points {numPoints}). Shipped sets no lower "
                    + "bound and reads memory before the array — the port does not substitute this");
            frac = x - idx;
            return idx;
        }

        // AParticleFX::UpdateParticles (77407) + UParticle::Update (93564) —
        // integration per the bridge's decompile port; the chaos kick's THREE
        // sequential appFrand components (93564+) STOP loudly until verified
        // against the shipped body in context.
        // The stack slot EmitParticles reads as the first particle's +0x78/+0x7c (ebp-0x104/-0x100, the same address in
        // UpdateParticles' frame). The only callee of UpdateParticles that writes it deterministically is UParticle::Update
        // in its bSystemRelative branch: rep movsd of `UnitCoords *= Rotation` to its ebp-0xac (0x103bffa7..0x103bffb2;
        // its ebp is the caller's ebp-0x74), so the slot pair = YAxis.Y, YAxis.Z. appExp (Core 0x10126060, called per
        // substep when Damping > 0, 0x103c2005) is register-only (fldl2e .. fscale, ret): it writes nothing there; no
        // other callee changed the slot in the live probes (hp1slot3 call sites, hp1slot4 CHG records).
        //
        // What the slot holds at AParticleFX::Update entry depends on the CALLER PATH (live original, D-73 probe
        // hp1slot2: dead-stack snapshot at Update entry + ring of BoundVisible calls):
        //   * GetRenderBoundingBox path (FDynamicSprite::Setup -> AParticleFX::GetRenderBoundingBox 0x103c116c ->
        //     Update(0), emitter shifted): the slot lies in the frame Core FCoords::Inverse used under the PREVIOUS
        //     Setup's URender::BoundVisible (Render 0x10b213ad, esp = slot + 564): (Inverse(box).YAxis.Y, .Z) of the box
        //     that Setup passed — game code, 33/33 first particles bit-exact (Lev2_Fire2 SnailTrailFX, Lev_Tut2
        //     BroomTrail_02). Modelled: _setupResidue, written at every Setup BoundVisible call.
        //   * render path (DrawParticleSystem -> Update(0), live original probe): the slot is
        //     DrawParticleSystem esp - 0x1c0, the same absolute address for every render-path call, and holds the last
        //     value written there. Game code writes it in EmitParticles (every born particle's ChaosTimer/SpinPos after its
        //     catch-up, 0x103c34e5 / AddParticle 0x103c3538; a call with no born particle writes nothing) and in
        //     UpdateParticles' bSystemRelative branch (below) — so a call chains from the previous render-path call,
        //     across emitters and frames (Lev_Tut3 186, Lev_Tut1 243 first particles bit-exact). Between calls the render
        //     device path (D3DDrv/wined3d under DrawDecals / DrawComplexSurface) may overwrite it with a wined3d return
        //     address or heap pointers: platform code, deliberately NOT emulated: a call always chains (D-73).
        //     appExp (Core 0x10126060, Damping > 0) is register-only; its return address lands at UpdateParticles'
        //     ebp-0x60, not at the slot. Session start: 0, 0 (no game write yet; the original holds a loader/platform
        //     residue there — D-73).
        enum SlotKind { Chain, SetupBoundVisible, SysRel, SessionStart }
        internal bool GrbbPath { set => _grbbPath = value; }
        internal (bool Known, float Y, float Z) SetupResidue => (_setupResidueKnown, _setupResY, _setupResZ);
    }
}
