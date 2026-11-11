// FGlobalRandoms::Tick: Render.dll FUN_10b1d020 (vtable 0x10b38a3c slot 2, object 0x10b6716c), ported from the
// disassembly 0x10b1d020..0x10b1d1e5.
//
// Called from URender::PreRender (0x10b01375, Render.dll.c:15780-15802) on EVERY frame, right after
// appEnableFastMath(1); the argument is TimeSeconds of the frame level's LevelInfo (Actors(0)+0x3b4). All body
// arithmetic runs at 24-bit x87 precision (PC24), hence float here.
//
//   ticks = _ftol(Time * 35.0)                       (fmul qword 0x10b38a30)
//   Base[i] = appFrand()           i = 0..255, ALWAYS (table 0x10b6696c)
//   d = (ticks - Last) << 4        (unsigned)
//   if d >= 0x100 or Last == 0:                      (0x10b1d149)
//       Rand[i] = appFrand(); Delta[i] = (Base[i] - Rand[i]) * 0.0625
//   else:                                            (0x10b1d09a)
//       k from (Last<<4)&0xff to (ticks<<4)&0xff (exclusive):
//           Delta[k] = (Base[k] - Rand[k]) * 0.0625
//       n = (ticks - Last) & 0xff
//       k from (ticks<<4)&0xff wrapping to (Last<<4)&0xff (do-while, so when
//       equal, all 256): Rand[k] += n * Delta[k]
//   Rand[i] is clamped to [0, 1]: v < 0 -> 0; !(v < 1) -> 1   (0x10b385bc/0x10b385b0)
//   Last = ticks
//
// Tables: Base 0x10b6696c (read by Flicker, 0x10b06470), Delta 0x10b6656c, Rand 0x10b66d6c (spatial light
// effects). appFrand = rand() * MsvcRandom.FrandScale (Core 0x10126150); the caller supplies the RNG stream.
using System;

namespace HP1.UEScript
{
    public sealed class UEGlobalRandoms
    {
        public readonly float[] Base = new float[256];
        public readonly float[] Delta = new float[256];
        public readonly float[] Rand = new float[256];
        public uint Last { get; private set; }
        public int TickCount { get; private set; }

        public void Tick(float timeSeconds, Func<float> appFrand)
        {
            if (appFrand is null) throw new ArgumentNullException(nameof(appFrand));
            // PC24: the product rounds to float, then _ftol truncates toward zero.
            float scaled = (float)((double)timeSeconds * 35.0);
            uint ticks = FtolLow(scaled);
            for (int i = 0; i < 256; i++) Base[i] = appFrand();
            uint last = Last;
            if (((ticks - last) << 4) >= 0x100u || last == 0)
            {
                for (int i = 0; i < 256; i++)
                {
                    float r = appFrand();
                    Rand[i] = r;
                    Delta[i] = (Base[i] - r) * 0.0625f;
                }
            }
            else
            {
                uint to = (ticks << 4) & 0xff, from = (last << 4) & 0xff;
                for (uint k = from; k != to; k = (k + 1) & 0xff)
                    Delta[k] = (Base[k] - Rand[k]) * 0.0625f;
                float n = (ticks - last) & 0xff;
                uint j = to;
                do
                {
                    Rand[j] = n * Delta[j] + Rand[j];
                    j = (j + 1) & 0xff;
                } while (j != from);
            }
            for (int i = 0; i < 256; i++)
            {
                float v = Rand[i];
                if (!(v >= 0f)) Rand[i] = 0f;   // fcom 0: C0 = less OR unordered
                else if (!(v < 1f)) Rand[i] = 1f;
            }
            Last = ticks;
            TickCount++;
        }

        // MSVCRT _ftol: truncation toward zero to a 64-bit integer; eax holds the low word.
        internal static uint FtolLow(float v)
        {
            double t = Math.Truncate((double)v);
            long l = double.IsNaN(t) || t >= 9.2233720368547758e18 || t < -9.2233720368547758e18
                ? long.MinValue : (long)t;
            return (uint)l;
        }
    }
}
