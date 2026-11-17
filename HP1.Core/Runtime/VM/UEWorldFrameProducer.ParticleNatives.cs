// AParticleFX script natives 431-435 (Engine.u ParticleFX: NumParticles, AddParticle, GetParticleParams,
// SetParticleParams, RecomputeDeltas). No shipped script calls them; a mod may. They run in the caller's x87 window
// (FastMathWindow: PC24 inside the render window, e.g. a HUD PostRender; PC64 in the game tick) and AddParticle draws
// from the GAME rand() stream (script is game code). The container is the emitter's FxState (UParticleList +0x378); a
// destroyed emitter has none (AParticleFX::Destroy frees it, 0x103c1cdd), as RenderStepFxBody reads it.
using System;
using HP1.Core;

namespace HP1.UEScript
{
    public sealed partial class UEWorldFrameProducer
    {
        // UParticleList vtable +0x90 (0x103c75f0): the first particle from the head whose Id (+0x84) matches; no list: NULL.
        Particle FindParticle(UEObject fx, int id)
        {
            if (fx.Destroyed) return null;
            foreach (Particle p in FxStateOf(fx).Particles) if (p.Id == id) return p;
            return null;
        }

        // Original: Engine.dll ?execNumParticles@AParticleFX@@QAEXAAUFFrame@@QAX@Z [HP1 0x103C6720]
        // The list count (vtable +0x88, 0x103c75d0: +0x78), -1 without a list (0x103c67d8).
        public int NativeNumParticles(UEObject fx) => fx.Destroyed ? -1 : FxStateOf(fx).Particles.Count;

        // Original: Engine.dll ?execAddParticle@AParticleFX@@QAEXAAUFFrame@@QAX@Z [HP1 0x103C6840]
        // After P_FINISH: bSteadyState (+0x29c bit 0) cleared (0x103c68ae..0x103c68b7), GetSysParams into a 0x3cc-byte
        // buffer, GetParams, AddParticle. The script's return value is never written (no store to Result up to
        // 0x103c68f9). gameRand: the game stream's rand().
        public void NativeAddParticle(UEObject fx, int id, UEVector loc, Func<int> gameRand)
        {
            fx.Props["bSteadyState"] = false;
            if (FastMathWindow) AddParticle<FpuPc24>(fx, id, loc, gameRand, true);
            else AddParticle<FpuPc64>(fx, id, loc, gameRand, false);
        }

        // Original: Engine.dll ?AddParticle@AParticleFX@@QAE_NHAAVFVector@@PAUFParams@@@Z [HP1 0x103C5B00]
        // The stack particle (ebp-0xcc) from UParticle(Loc, 0, NULL) (0x103c5b6e: OldPos = Pos = Loc, Vel 0, Age 0), Id
        // +0x84 (0x103c5b85), Priority +0x88 = 0 (0x103c5b79). NOT written: +0x70, ChaosTimer +0x78, SpinPos +0x7c — they
        // hold what GetParams' last appFrand -> msvcrt rand -> __getptd left on the stack (platform frames, no game value):
        // Particle.NativeResidue. With ColorPalette (0x103c5f2c) AlphaDelta and ColorDelta are not written either: STOP.
        void AddParticle<V>(UEObject fx, int id, UEVector loc, Func<int> gameRand, bool pc24) where V : struct, IFpuMode<V>
        {
            if (fx.Destroyed)
                throw new NotSupportedException($"ParticleFX.AddParticle on {fx.GetProp(PK.Name)}: the ParticleList is freed "
                    + "(AParticleFX::Destroy) — shipped calls its vtable +0x78 through NULL (0x103c6088..0x103c6097)");
            if (fx.GetProp(PK.ColorPalette) is not null)
                throw new NotSupportedException($"ParticleFX.AddParticle on {fx.GetProp(PK.Name)} with ColorPalette: AddParticle "
                    + "leaves AlphaDelta/ColorDelta to its stack residue (0x103c5f3d..0x103c5fa6 skips 0x103c5fab..0x103c6088) — STOP");
            V Frand() => V.L(gameRand()) * V.L(MsvcRandom.FrandScale);
            FParams fp = GetParams(GetSysParams<V>(fx), (Func<V>)Frand);
            var p = new Particle { OldPos = loc, Pos = loc, Id = id, NativeResidue = true };
            // Width/Length and their Max fields; SizeGrowPeriod > 0 zeroes Width/Length (0x103c5b73..0x103c5ba8).
            p.Width = p.WidthMax = fp.Width;
            p.Length = p.LengthMax = fp.Length;
            if (PropF(fx, "SizeGrowPeriod") > 0f) { p.Width = 0f; p.Length = 0f; }
            p.Lifetime = fp.Lifetime;
            // Lifetime > 0.01 (fcomp 0.01f; test ah,0x41; jne, 0x103c5bb8..0x103c5bc9): (SizeEndScale·W − W) / Lifetime
            // from the sampled W/L (0x103c5bcb..0x103c5bec), else 0.
            if (fp.Lifetime > 0.01f)
            {
                p.WidthDelta = ((V.L(fp.SizeEndScale) * V.L(fp.Width) - V.L(fp.Width)) / V.L(fp.Lifetime)).F;
                p.LengthDelta = ((V.L(fp.SizeEndScale) * V.L(fp.Length) - V.L(fp.Length)) / V.L(fp.Lifetime)).F;
            }
            // Direction (0x103c5bff..0x103c5e1b): UnitCoords *= Rotation (Core FCoords::operator*=, import [0x10608cd0]; the
            // Origin -= LastUpdateLocation before it is not read here); two appFrand(-a, a) half-angles in radians with
            // 0.01745 (.rdata 0x10478864, not EmitParticles' 0.01745329): width first, stored to a float (0x103c5ca1),
            // height chained from its register (0x103c5ccc); units = fistp of the float (a·65536)·0.15915494; local
            // (cosW·cosH, sinW·cosH, sinH) by TransformVectorBy on the axes, times Speed.
            UERotator rot = UEValue.AsRotator(fx.GetProp(PK.Rotation));
            float[] ax = { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f };
            HeadlessBspModel.MovingBrushTracker.MulRotator<V>(ax, rot.Pitch, rot.Yaw, rot.Roll);
            V FrandRange(float min, float max) => V.L(gameRand()) * V.L(MsvcRandom.FrandScale) * (V.L(max) - V.L(min)) + V.L(min);
            float a = (V.L(fp.SpreadW) * V.L(0.01745f)).F;
            float yaw = FrandRange(-a, a).F;
            float b = (V.L(fp.SpreadH) * V.L(0.01745f)).F;
            V pitch = FrandRange(-b, b);
            int pu = Fistp((pitch * V.L(65536f) * V.L(0.15915494f)).F);
            int yu = Fistp((V.L(yaw) * V.L(65536f) * V.L(0.15915494f)).F);
            float cosP = GTabSin(pu + 0x4000);
            float lx = GTabSin(yu + 0x4000) * cosP, ly = GTabSin(yu) * cosP, lz = GTabSin(pu);
            float Row(int r) => ((V.L(lx) * V.L(ax[r]) + V.L(ly) * V.L(ax[r + 1])) + V.L(lz) * V.L(ax[r + 2])).F;
            float dx = Row(0), dy = Row(3), dz = Row(6);
            p.Vel = new UEVector(dx * fp.Speed, dy * fp.Speed, dz * fp.Speed);
            p.Color = new UEVector(fp.ColorStart.x * ColorByteScale, fp.ColorStart.y * ColorByteScale, fp.ColorStart.z * ColorByteScale);
            p.AlphaMax = fp.AlphaStart;
            p.Alpha = PropF(fx, "AlphaGrowPeriod") > 0f ? 0f : fp.AlphaStart;
            p.DripTimer = fp.DripTime;
            p.SpinRate = fp.SpinRate;
            // DripTime > 0 only (0x103c5efc..0x103c5f29, EmitParticles tests != 0): the deltas spread the current size.
            if (fp.DripTime > 0f)
            {
                p.WidthDelta = (V.L(p.Width) / V.L(fp.DripTime)).F; p.Width = 0f;
                p.LengthDelta = (V.L(p.Length) / V.L(fp.DripTime)).F; p.Length = 0f;
            }
            // Lifetime > ColorDelay (0x103c5fab..0x103c6020): k = (1 / (Lifetime − ColorDelay))·0.003921569 in the register,
            // ColorDelta = (end − start bytes as an int)·k; else 0.
            float colorDelay = PropF(fx, "ColorDelay");
            if (fp.Lifetime > colorDelay)
            {
                V k = V.L(1f) / (V.L(fp.Lifetime) - V.L(colorDelay)) * V.L(ColorByteScale);
                float D(float end, float start) => (V.L((int)end - (int)start) * k).F;
                p.ColorDelta = new UEVector(D(fp.ColorEnd.x, fp.ColorStart.x), D(fp.ColorEnd.y, fp.ColorStart.y), D(fp.ColorEnd.z, fp.ColorStart.z));
            }
            // (AlphaDelay + 0.001) in the register below Lifetime (fcomp; test ah,1; je, 0x103c603a..0x103c6058; unordered
            // counts as below): (AlphaEnd − AlphaStart)·(1 / (Lifetime − AlphaDelay)), else 0.
            float alphaDelay = PropF(fx, "AlphaDelay");
            X87Ext lim = X87Ext.From(alphaDelay) + X87Ext.From(0.001f);
            lim = pc24 ? X87Ext.Round24(lim) : X87Ext.Round64(lim);
            if ((X87Ext.Fcom(lim, X87Ext.From(fp.Lifetime)) & X87Ext.C0) != 0)
                p.AlphaDelta = ((V.L(fp.AlphaEnd) - V.L(fp.AlphaStart)) * (V.L(1f) / (V.L(fp.Lifetime) - V.L(alphaDelay)))).F;
            FxStateOf(fx).Particles.Add(p);   // vtable +0x78 (0x103c73b0): appended at the tail
        }

        // fistp of a register exact in a double (PC64): nearest even, outside int the integer indefinite.
        static int FistpD(double v) => v >= -2147483648.0 && v < 2147483648.0 ? (int)Math.Round(v, MidpointRounding.ToEven) : int.MinValue;

        // Original: Engine.dll ?execGetParticleParams@AParticleFX@@QAEXAAUFFrame@@QAX@Z [HP1 0x103C69B0]
        // The particle with Id: Position (+0xc), Velocity (+0x18), Lifetime, Alpha, Color as an FColor — per channel
        // fistp(float(c·256) − 0.5) with the subtraction in the caller's window (0x103c6a8f..), clamped 0..255; A from
        // +0x44 (every writer stores 0 there) — Width, Length, DripTimer, SpinRate. Not found or no list: null (false,
        // the out parameter not written).
        public UEObject NativeGetParticleParams(UEObject fx, int id)
        {
            Particle p = FindParticle(fx, id);
            if (p is null) return null;
            bool pc24 = FastMathWindow;
            byte Ch(float c)
            {
                float t = c * 256f;
                int v = pc24 ? Fistp(t - 0.5f) : FistpD((double)t - 0.5);
                return (byte)(v < 0 ? 0 : v > 254 ? 255 : v);
            }
            var pp = new UEObject(UEPropLayout.ForStructName("ParticleParams")) { IsStruct = true };
            pp.Props["Position"] = p.Pos;
            pp.Props["Velocity"] = p.Vel;
            pp.Props["Lifetime"] = p.Lifetime;
            pp.Props["Alpha"] = p.Alpha;
            pp.Props["Color"] = UEObject.MakeColorBag(Ch(p.Color.x), Ch(p.Color.y), Ch(p.Color.z), Ch(0f));
            pp.Props["Width"] = p.Width;
            pp.Props["Length"] = p.Length;
            pp.Props["DripTimer"] = p.DripTimer;
            pp.Props["SpinRate"] = p.SpinRate;
            return pp;
        }

        // Original: Engine.dll ?execSetParticleParams@AParticleFX@@QAEXAAUFFrame@@QAX@Z [HP1 0x103C6CB0]
        // bSteadyState cleared after P_FINISH, with or without a list (0x103c6d3a..0x103c6d43); the particle with Id takes
        // Position, Velocity, Lifetime, Alpha, Color (byte·0.003921569 each, +0x44 = 0), Width, Length, DripTimer, SpinRate.
        public bool NativeSetParticleParams(UEObject fx, int id, UEObject pp)
        {
            fx.Props["bSteadyState"] = false;
            Particle p = FindParticle(fx, id);
            if (p is null) return false;
            var c = pp?.GetProp("Color") as UEObject;
            p.Pos = UEValue.AsVector3(pp?.GetProp("Position"));
            p.Vel = UEValue.AsVector3(pp?.GetProp("Velocity"));
            p.Lifetime = UEValue.AsFloat(pp?.GetProp("Lifetime"));
            p.Alpha = UEValue.AsFloat(pp?.GetProp("Alpha"));
            p.Color = new UEVector(UEValue.AsByte(c?.GetProp(PK.R)) * ColorByteScale, UEValue.AsByte(c?.GetProp(PK.G)) * ColorByteScale,
                                   UEValue.AsByte(c?.GetProp(PK.B)) * ColorByteScale);
            p.Width = UEValue.AsFloat(pp?.GetProp("Width"));
            p.Length = UEValue.AsFloat(pp?.GetProp("Length"));
            p.DripTimer = UEValue.AsFloat(pp?.GetProp("DripTimer"));
            p.SpinRate = UEValue.AsFloat(pp?.GetProp("SpinRate"));
            return true;
        }

        // Original: Engine.dll ?execRecomputeDeltas@AParticleFX@@QAEXAAUFFrame@@QAX@Z [HP1 0x103C6F30]
        // Original: Engine.dll ?RecomputeDeltas@AParticleFX@@QAEHH@Z [HP1 0x103C6270]
        // Only without ColorPalette and with a list and the particle: span = Lifetime − Age in the register, > 0 (fcom 0.0;
        // test ah,0x41); inv = 1/span, k = inv·0.003921569 in the register; ColorDelta = (ColorEnd.Base byte − Color)·k
        // (a byte minus the 0..1 colour, as shipped), +0x54 = 0, AlphaDelta = (AlphaEnd.Base − Alpha)·inv, Width/LengthDelta
        // = (SizeEndScale.Base·W − W)·inv. The result is one byte (setne dl; mov [eax], dl).
        public bool NativeRecomputeDeltas(UEObject fx, int id)
        {
            if (fx.GetProp(PK.ColorPalette) is not null) return false;
            Particle p = FindParticle(fx, id);
            if (p is null) return false;
            bool pc24 = FastMathWindow;
            X87Ext R(X87Ext v) => pc24 ? X87Ext.Round24(v) : X87Ext.Round64(v);
            X87Ext L(float v) => X87Ext.From(v);
            X87Ext span = R(L(p.Lifetime) - L(p.Age));
            if ((X87Ext.Fcom(span, X87Ext.From(0f)) & (X87Ext.C0 | X87Ext.C3)) != 0) return false;
            X87Ext inv = pc24 ? X87Ext.Div24(L(1f), span) : X87Ext.Div64(L(1f), span);
            X87Ext k = R(inv * L(ColorByteScale));
            var end = fx.GetProp("ColorEnd") is UEObject ce ? ce.GetProp(PK.Base) as UEObject : null;
            float D(object b, float c) => R(R(X87Ext.From((int)UEValue.AsByte(b)) - L(c)) * k).ToFloat();
            p.ColorDelta = new UEVector(D(end?.GetProp(PK.R), p.Color.x), D(end?.GetProp(PK.G), p.Color.y), D(end?.GetProp(PK.B), p.Color.z));
            p.AlphaDelta = R(R(L(RangeBase(fx, "AlphaEnd")) - L(p.Alpha)) * inv).ToFloat();
            float ses = RangeBase(fx, "SizeEndScale");
            p.WidthDelta = R(R(R(L(ses) * L(p.Width)) - L(p.Width)) * inv).ToFloat();
            p.LengthDelta = R(R(R(L(ses) * L(p.Length)) - L(p.Length)) * inv).ToFloat();
            return true;
        }
    }
}
