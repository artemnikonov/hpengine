// GALAXY MIXER GAIN CHAIN FOR A GAME SOUND VOICE.
//
// Mixer FUN_10612250 (Galaxy.dll.c:12887-12909) computes a voice's dry gains as
// integers, each multiplication truncated by `>> 15`:
//
//   a = (vol12 · m · m · 2) >> 15        vol12 = voice+0x12, m = group master
//   a = (a · v18) >> 15;  a = (a · v1a) >> 15;  a = (a · (short)v24) >> 15;
//   a = (a · v2a) >> 15;  a = (a · v66) >> 15
//   p = (current pan & 0x7fff) + ((0x4000-|p-0x4000|)·(short)v36 >> 15)·v3c >> 15
//   R = (Table[p] · a) >> 15,  L = (Table[0x7fff - p] · a) >> 15
//
// Field values for a game sound voice (2D path, FUN_1060fbf0 :10992-11093,
// FUN_1060aae0 :8318-8348):
//   * v18 = v1a = v24 = v2a = 0x7fff, v36 = v3c = 0 (pan offset 0),
//     v20 = 0 (the voice has no instrument — envelopes :12562 do not run);
//   * v66 = Update volume (0..0x7fff, argument of FUN_1060aae0);
//   * v12 = 0x7e02 (= 127²·2) — initialisation of all voices (:8913). FUN_1060fbf0
//     and FUN_1060aae0 do not write it — from the disassembly: 0x1060fbf0..0x1060fffe and
//     0x1060aae0..0x1060ac70 have no store to +0x10..+0x12 wider than the bytes +0x10 (0x1060fd6a)
//     and +0x11 (0x1060fd7b); the only rep stos (0x1060fdef/0x1060fdfb) fills a fresh malloc buffer;
//     the direct callees 0x10610000..0x10610100 and 0x10615d40..0x10615eba write only +0x11 (0x10615e73);
//   * current pan = target: FUN_1060fbf0 and FUN_1060aae0 write +0x14 and
//     +0x16 with one value, so the step ≤ 0x200 (:12887-12897) of a 2D voice
//     is zero — no smoothing, the pan jumps per Update;
//   * m — the group B master (all game sounds): DAT_1067cedc, set by
//     glxSetSampleVolume FUN_1060c4b0 (:9242) from SetVolumes
//     (`fild byte; fmul 1/255; fmul 127.0; ftol` — the clamp in between
//     belongs to MusicVolume, disassembly 0x10606664-0x106066c9). So the master law is QUADRATIC in the
//     integer m = ftol(SoundVolume/255·127), not linear byte/255.
//
// The kernel scale (how the generated mixer multiplies a sample by L/R) was established by
// executing the generator: float32 x·(g·2^-30), DAT_10648d50 = 2^-30 read from the
// module — GalaxyMixKernel. Gains from here go into the kernel as integers.
using System;
using HP1.Core;
using HP1.UEScript;

namespace HPEngine.Audio
{
    public static class GalaxyMixGain
    {
        const int InitVolume12 = 0x7e02;   // Galaxy.dll.c:8913
        const int Unity15 = 0x7fff;        // +0x18/+0x1a/+0x24/+0x2a for a game sound

        // m = ftol(SoundVolume·(1/255)·127.0) — the glxSetSampleVolume argument, SetVolumes 0x10606664-0x106066c9:
        // `fild byte [+0x6a]; fmul qword 1/255` stays UNROUNDED in st(1) (x87, 64-bit mantissa); the clamp to [0, 1]
        // (0x1060668c-0x106066b8) and `fstp dword` apply to MusicVolume (+0x69), not to SoundVolume; then
        // `fmul dword 127.0; _ftol` (truncation). At SoundVolume 255 this is 126.999… -> 126.
        public static int MasterLevel(float soundMaster)
        {
            int b = (int)MathF.Round(soundMaster * 255f);   // the byte property (+0x6a)
            X87Ext x = X87Ext.Round64(X87Ext.From(b) * X87Ext.From(1.0 / 255.0));   // 0x1063a400
            return (int)X87Ext.Round64(x * X87Ext.From(127f)).TruncateToInteger();  // 0x1063a3e8
        }

        // Integer L/R gains (0..0x7fff) of a voice with volume vol and pan pan.
        public static void Gains(int vol, int pan, int master, out int left, out int right)
        {
            int a = (InitVolume12 * master * master * 2) >> 15;
            a = (a * Unity15) >> 15;   // +0x18
            a = (a * Unity15) >> 15;   // +0x1a
            a = (a * Unity15) >> 15;   // +0x24
            a = (a * Unity15) >> 15;   // +0x2a
            a = (a * vol) >> 15;       // +0x66
            int p = pan & 0x7fff;
            right = (UEGalaxyVoiceMix.PanTable(p) * a) >> 15;
            left = (UEGalaxyVoiceMix.PanTable(0x7fff - p) * a) >> 15;
        }
    }
}
