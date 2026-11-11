// GALAXY MIXER KERNEL — port of the GENERATED code of the SSE path.
//
// Galaxy builds the mixer DAT_1067d0b4 and the output converter DAT_1067ca04 when opening
// the device (FUN_106119b0, Galaxy.dll.c:12278-12321). On a CPU with SSE (GIsKatmai and
// CPUID.1:EDX bit 25 — any modern one) with stereo output the generator
// FUN_10620b91 runs: it writes a VERBATIM copy of the templates 0x106200f0[0x603] (mixer) and
// 0x106209a4[0x1ed] (converter), without patches. Established by executing the generator
// under unicorn and comparing bytes (se_harness/galaxy_mixer/). Below is the semantics of that code:
//
// MIXER (one call: n frames of one mono voice, arguments 0x10612dd8-0x10612e10):
//   frac &= ~1, step &= ~1 (0x10620106, 0x1062010b) — only within the call: the caller
//     advances the voice position by the FULL step (Advance);
//   position is 16.16: integer p (voice +0x32) and fraction f (+0x36); the step carries into p via
//     the fraction carry (add edx, esp / adc esi, ebp) — truncation, not rounding;
//   a sample is ALWAYS linear interpolation of neighbours at ANY step: bit 1 of the call flags
//     (UseFilter at step < 1, :12939) is not read by the template at all (only bits 0
//     and 2 of [ebp+0x3c] are tested); v = s[p]·((~f & 0xffff) >> 1) + s[p+1]·(f >> 1) — pmaddwd,
//     15-bit weights, mask DAT_10648d40 = 0x7fff (read from the module); the weights sum to 0x7fff;
//   x = (float)v (cvtpi2ps); buses are float32 stereo: bus += x · ((float)g · 2^-30)
//     (cvtsi2ss + mulps on DAT_10648d50 = 2^-30, read from the module); three buses — dry,
//     send 1 (reverb), send 2 (callback DAT_1067cf28 is not set in HP1 — the bus is
//     silent, not computed here);
//   an 8-bit source (signed byte b, the loader subtracts 0x80) is read as b << 8 —
//     exactly the same as the 16-bit (b-128)<<8 of UEPcm (verified by execution).
//
// CONVERTER (16-bit stereo): cvtps2pi (default MXCSR — round to nearest, half
//   to even; no game module contains ldmxcsr) + packssdw (16-bit saturation). Group B
//   (flag 0x40) is added to group A's output with paddsw; group A is empty in HP1 (voice bit 0x80
//   is set only by the module synthesizer) — output = ToS16(bus B).
namespace OpenHP1.Audio
{
    public static class GalaxyMixKernel
    {
        // DAT_10648d50 (4 floats, read from Galaxy.dll 1.53) = 2^-30.
        public const float Scale = 1f / 1073741824f;
        // Voice reverb send level +0x1e = 0x7f (initialisation 0x1060b888),
        // send arguments (+0x2c/+0x30) = (g · 0x7f) >> 7 (0x10612d12-0x10612d51).
        public const int SendLevel = 0x7f;

        // 16.16 step from the voice frequency: FUN_1060aae0 :8350-8352 (unsigned division, truncation).
        public static int Step(int frequency, int rate)
        {
            uint f = (uint)frequency, r = (uint)rate;
            return (int)((f / r) * 0x10000u + ((f % r) << 16) / r);
        }

        // Voice send from the dry gain.
        public static int Send(int gain) => (gain * SendLevel) >> 7;

        // Caller's advance of the voice position after the call (0x10612e19-0x10612e37):
        // t = step·n + frac (32 bits), frac = t & 0xffff, pos += t >> 16 (arithmetic).
        public static void Advance(ref int pos, ref int frac, int step, int n)
        {
            int t = step * n + frac;
            frac = t & 0xffff;
            pos += t >> 16;
        }

        // Number of frames to the end of data (0x10612d93-0x10612dd2): d = clamp(end - pos, -0x7fff, 0x7fff),
        // k = (d·0x10000 - frac) / step + 1 (idiv — truncation toward zero).
        public static int FramesToEnd(int end, int pos, int frac, int step)
        {
            int d = end - pos;
            if (d < -0x7fff) d = -0x7fff; else if (d > 0x7fff) d = 0x7fff;
            return ((d << 16) - frac) / step + 1;
        }

        // One call of the generated mixer: n frames of channel c (s[i·stride + c]) from position
        // (pos, frac) into buses dry/send from frame off; gains are arguments +0x24..+0x30. A sample index >= total reads 0
        // (a sound voice's ring keeps one zero frame past its end, read only with weight 0; a song's buffer ends its data).
        public static void Mix(float[] dry, float[] send, int off, short[] s, int stride, int c, int total,
                               int pos, int frac, int step, int n, int gainL, int gainR, int sendL, int sendR)
        {
            float gl = (float)gainL * Scale, gr = (float)gainR * Scale;
            float sl = (float)sendL * Scale, sr = (float)sendR * Scale;
            // frac &= ~1 (0x10620106) — the source form; no observable consequence: weights take
            // f >> 1, and the kernel step is even, so carries are the same too (mutation indistinguishable, V9).
            int f = frac & 0xfffe;
            int st = step & ~1;
            int stInt = st >> 16, stFrac = st & 0xffff;
            int p = pos;
            for (int j = 0; j < n; j++)
            {
                int wa = (~f & 0xffff) >> 1, wb = f >> 1;
                int v = At(s, stride, c, total, p) * wa + At(s, stride, c, total, p + 1) * wb;
                float x = v;
                int k = 2 * (off + j);
                dry[k] += x * gl;
                dry[k + 1] += x * gr;
                send[k] += x * sl;
                send[k + 1] += x * sr;
                int t = f + stFrac;
                p += stInt + (t >> 16);
                f = t & 0xffff;
            }
        }

        static int At(short[] s, int stride, int c, int total, int i) => i < total ? s[i * stride + c] : 0;

        // Converter: float buses -> int16 (cvtps2pi, then packssdw).
        public static short ToS16(float x)
        {
            int v;
            // cvtps2pi out of int32 range (and NaN) gives 0x80000000 — packssdw -> -32768.
            if (!(x > -2147483904f && x < 2147483648f)) v = int.MinValue;
            else v = (int)MathF.Round(x, MidpointRounding.ToEven);
            return (short)(v < -32768 ? -32768 : v > 32767 ? 32767 : v);
        }
    }
}
