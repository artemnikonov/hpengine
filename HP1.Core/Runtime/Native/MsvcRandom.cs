using System;

namespace HP1.Core
{
    // HP1's shipped Core.dll routes appRand/appFrand/appRandRange through the
    // Visual C++ CRT rand() (Core.dll.c:28148, 59731, 64932, 65436).  The game
    // seeds that process-wide stream with time(NULL) during Core startup
    // (Core.dll.c:55850) and resets it to 1 for replay capture/playback
    // (Engine.dll.c:129546).
    //
    // This is the Visual C++ 6/7 CRT recurrence used by the shipped Win32 game.
    // Keeping it in the platform-free core gives the frontend and deterministic
    // headless validation the same authored random stream.
    public sealed class MsvcRandom
    {
        uint _state;

        public MsvcRandom(uint seed)
        {
            _state = seed;
        }

        public static uint UnixTimeSeed()
            => unchecked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        public void Seed(uint seed)
        {
            _state = seed;
        }

        // Number of rand() calls, for comparison with the oracle (how many draws the bodies consumed).
        public long Draws { get; private set; }
        // Generator state, for validation only (to replay the same sequence).
        internal uint StateForTest => _state;

        public int NextInt15()
        {
            Draws++;
            _state = unchecked(_state * 214013u + 2531011u);
            return (int)((_state >> 16) & 0x7fffu);
        }

        // appFrand MULTIPLIER: float at Core address 0x1017ddb0, bits 0x38000100 = 3.0518509e-05 (1/32767, i.e.
        // rand()/RAND_MAX), NOT 1/32768. All four bodies read it: appFrand() (0x10126150: fild rand; fmul),
        // appFrand(min,max) (0x10126170), appRandRange(float,float) (0x101261a0), and via appFrand
        // execFRand/execVRand, particle emission and FGlobalRandoms::Tick. rand() = 0x7fff gives exactly 1.0.
        public static readonly float FrandScale = BitConverter.Int32BitsToSingle(0x38000100);

        // appFrand(): the product of a 15-bit integer and a float is exact at any x87 precision and is rounded
        // once, on the store to float. Same single rounding as a C# float multiply.
        public float NextFloat01()
            => NextInt15() * FrandScale;

        // appFrand() as the calling x87 code sees it BEFORE the store to memory: st0 = rand()*C, exact in a 64-bit
        // mantissa (15 + 24 bits); at PC24 (render window) the fmul rounds it to 24 bits. execVRand builds its 2f-1
        // component from it.
        public X87Ext NextFrandSt0(bool pc24)
        {
            X87Ext p = X87Ext.From(NextInt15()) * X87Ext.From(FrandScale);
            return pc24 ? X87Ext.Round24(p) : X87Ext.Round64(p);
        }

        // appFrand(float,float) (Core 0x10126170) at 24-bit precision, as particle emission from
        // DrawParticleSystem calls it (render after appEnableFastMath(1)): every x87 op rounds to float.
        //   fild rand; fmul C; fld max; fsub min; fmulp; fadd min
        public float NextFrandRangePc24(float min, float max)
        {
            float f = NextFloat01();
            float d = max - min;
            float p = f * d;
            return p + min;
        }

        // appRandRange(float,float) (Core 0x101261a0) at the caller's precision, as execRandRange calls it (PC64 in the
        // game tick, PC24 inside the render window):
        //   fild rand; fld max; fsub min; fmulp; fmul C; fadd min; -> float
        // Every op rounds to nearest-even at 64 (24) mantissa bits; the result rounds once more on the store to float.
        public float NextRandRange(float min, float max, bool pc24)
        {
            X87Ext R(X87Ext v) => pc24 ? X87Ext.Round24(v) : X87Ext.Round64(v);
            int r = NextInt15();
            X87Ext d = R(X87Ext.From(max) - X87Ext.From(min));
            X87Ext p = R(X87Ext.From(r) * d);
            X87Ext q = R(p * X87Ext.From(FrandScale));
            return R(q + X87Ext.From(min)).ToFloat();
        }
    }
}
