using System;

namespace HP1.UEScript
{
    // SURFACE TEXTURE PAN BY LEVEL CLOCK: shipped URender::DrawFrame surface loop, Render.dll fragment
    // 0x10b258c4..0x10b25b56 (decompile Render.dll.c:1151-1210; read from the disassembly and checked by
    // executing that same fragment). For EVERY drawn FBspSurf:
    //
    //   PanU = (float)Surf.PanU                                    ; fild/fstp dword
    //   if (PolyFlags & 0x200)  PF_AutoUPan   (0x10b258d1 test ah,2)
    //       x = (float)(Zone.TexUPanSpeed * Level.TimeSeconds * 8960)   ; x87, single rounding
    //       PanU = (float)(PanU + (ROUND(x) & 0x3ffff) * (1/256))       ; fistp, and, fmul, fadd
    //   (same for V: 0x400 PF_AutoVPan, TexVPanSpeed, Surf.PanV)
    //   if (PolyFlags & 0x2000) PF_SmallWavy  (0x10b25a4f test ah,0x20)
    //       PanU = (float)(PanU + 8*appSin(t) + 4*appCos(2.3f*t))
    //       PanV = (float)(PanV + 8*appCos(t) + 4*appSin(2.3f*t))
    //
    // Constants from the Render.dll image: 8960 (0x10b38fec), 1/256 (0x10b3865c),
    // 2.3f (0x10b38fdc), 4.0 (0x10b38600, double), 8.0 (0x10b38fe0, double).
    // appSin/appCos: IAT slots 0x10b74574/0x10b74650, resolved via the PE import table to Core.dll `fsin`/`fcos`
    // (x87). TimeSeconds = LevelInfo+0x3b4, TexU/VPanSpeed = ZoneInfo+0x2e0/+0x2e4 (shipped layout).
    // Zone = draw record +0x34: ZoneActor of the node's zone on the camera side; without a ZoneActor, Actors(0)
    // (Render.dll.c:6548-6563).
    // ZoneActor — Actors(0) (Render.dll.c:6548-6563).
    //
    // Bit 0x1000 (PF_BigWavy in UE1) is PF_SpecialPoly in HP1: read only by APawn::Mount (Engine.dll 0x103ec133)
    // and a walking helper (0x103e6831); Render/D3DDrv/SoftDrv never test it on a surface (disassembly scan
    // three passes with controls). It plays no part here.
    //
    // PRECISION. DrawFrame runs inside the render window: UGameEngine::Draw (Engine
    // 0x1039fe40 / 0x1039feba / 0x103a0006) calls Render->PreRender (vtable +0x60, appEnableFastMath(1)), DrawWorld
    // (+0x74) and only then PostRender (+0x64, appEnableFastMath(0)), so this fragment executes at PC24: every fmul /
    // fadd rounds the register to a 24-bit mantissa (exponent the register's). AutoPan: fld speed; fmul time; fmul
    // 8960; fstp dword (two 24-bit roundings, then fistp to even); fild n; fmul 1/256 (exact); fadd PanU; fstp.
    // SmallWavy (0x10b25abd..0x10b25b56): sin(t) and cos(t) leave appSin/appCos by fstp qword (53 bits); t2 = t·2.3f
    // rounds at PC24; cos(t2) / sin(t2) stay in the register (fst qword), ·4.0 and ·8.0 (exact scalings, rounded at
    // PC24), faddp, fadd PanU/PanV, fstp dword. fsin/fcos are correctly rounded at any PC (D-71). Checked
    // against the fragment under unicorn at FPCW 0x007F, bit for bit. The campaign
    // has ZERO SmallWavy carriers (census of 41 maps and 37 mover maps).
    public static class SurfacePan
    {
        public const int PF_AutoUPan = 0x00000200;
        public const int PF_AutoVPan = 0x00000400;
        public const int PF_SmallWavy = 0x00002000;
        public const int PF_SpecialLit = 0x00100000;

        // MOVER SURFACE FLAGS: FMovingBrushTracker::SetupActorBrush (Engine.dll 0x1038d8a1..0x1038d8c1):
        // FPoly.PolyFlags & 0x3cffffff, plus PF_SpecialLit if the mover itself has bSpecialLit (Actor+0x1f0 bit 0x1).
        // A mover surface is a real FBspSurf in Level->Model->Surfs, drawn and lit by the same code as static ones.
        public static int MoverSurfaceFlags(int fpolyFlags, bool moverSpecialLit)
        {
            int f = fpolyFlags & 0x3cffffff;
            return moverSpecialLit ? f | PF_SpecialLit : f;
        }

        // Full DrawFrame fragment: PanU/PanV in TEXELS after the shift. A frontend whose base Pan is already baked
        // into mesh UVs calls with basePanU = basePanV = 0 and gets the animated offset.
        public static void Pan(int polyFlags, int basePanU, int basePanV, float timeSeconds,
                               float texUPanSpeed, float texVPanSpeed, out float panU, out float panV)
        {
            panU = (short)basePanU;
            panV = (short)basePanV;
            if ((polyFlags & PF_AutoUPan) != 0)
                panU = (float)((double)panU + (AutoTexels(texUPanSpeed, timeSeconds) & 0x3ffff) / 256.0);
            if ((polyFlags & PF_AutoVPan) != 0)
                panV = (float)((double)panV + (AutoTexels(texVPanSpeed, timeSeconds) & 0x3ffff) / 256.0);
            if ((polyFlags & PF_SmallWavy) != 0)
            {
                // appSin / appCos = bare fsin / fcos (UEX87Transcendental, D-71); |x| >= 2^63 leaves the operand
                // (C2), so t = 1e30 gives PanU ~ 8e30.
                static HP1.Core.X87Ext R(HP1.Core.X87Ext v) => HP1.Core.X87Ext.Round24(v);
                HP1.Core.X87Ext t = HP1.Core.X87Ext.From(timeSeconds);
                var sinT = HP1.Core.X87Ext.From(HP1.Core.UEX87Transcendental.Fsin(t).ToDouble());   // fstp qword; fld qword
                HP1.Core.X87Ext t2 = R(t * HP1.Core.X87Ext.From(2.3f));                           // fstp qword: exact
                HP1.Core.X87Ext four = HP1.Core.X87Ext.From(4.0), eight = HP1.Core.X87Ext.From(8.0);
                panU = R(R(R(HP1.Core.UEX87Transcendental.Fcos(t2) * four) + R(sinT * eight)) + HP1.Core.X87Ext.From(panU)).ToFloat();
                var cosT = HP1.Core.X87Ext.From(HP1.Core.UEX87Transcendental.Fcos(t).ToDouble());
                panV = R(R(R(HP1.Core.UEX87Transcendental.Fsin(t2) * four) + R(cosT * eight)) + HP1.Core.X87Ext.From(panV)).ToFloat();
            }
        }

        // fistp(fstp dword(speed · time · 8960)) at PC24: the integer BEFORE the 0x3ffff mask.
        static int AutoTexels(float speed, float time)
        {
            float x = (float)HP1.Core.X87Ext.Round24(HP1.Core.X87Ext.Round24((double)speed * time) * 8960.0);
            if (float.IsNaN(x) || float.IsInfinity(x)) return int.MinValue;
            double r = Math.Round((double)x, MidpointRounding.ToEven);
            if (r >= 2147483648.0 || r < -2147483648.0) return int.MinValue;
            return (int)r;
        }
    }
}
