// Tests of Core natives against shipped HP1 1.53, no game files needed.
// The native is taken from the same registration the VM calls (RegisterMathOps -> GetByIndex); expectations
// follow the shipped body and are computed here independently. Red mutations — redtest.sh.
//
// Sin (187) / Cos (188): appSin/appCos are bare x87 fsin/fcos (Core.dll.c:66604 and :19270).
//   x = ±∞            -> #IA, masked response QNaN indefinite -> 0xFFC00000 as float;
//   finite |x| ≥ 2^63 -> C2 = 1, st0 untouched -> x itself;
//   NaN              -> NaN.
using HP1.UEScript;

sealed class MathNatives : UENativesCore
{
    public MathNatives() : base(1) { RegisterMathOps(); }
}

static class Program
{
    static int _fail, _pass;

    static void Check(string name, bool ok, string detail)
    {
        if (ok) _pass++; else { _fail++; Console.WriteLine($"FAIL {name}: {detail}"); }
    }

    static uint Bits(float f) => (uint)BitConverter.SingleToInt32Bits(f);

    static void Expect(MathNatives n, int idx, string name, float x, uint expectBits)
    {
        float r = (float)n.GetByIndex(idx)(null, new UEFrame(), new object[] { x });
        Check($"{name}({x:R} / 0x{Bits(x):x8})", Bits(r) == expectBits, $"expected 0x{expectBits:x8}, got 0x{Bits(r):x8} ({r:R})");
    }

    static int Main()
    {
        var n = new MathNatives();
        const uint Indefinite = 0xFFC00000;   // x87 QNaN indefinite stored as single
        float two63 = 9.223372036854775808e18f, big = 1.8446744073709552e19f;
        foreach (var (idx, name) in new[] { (187, "Sin"), (188, "Cos") })
        {
            Expect(n, idx, name, float.PositiveInfinity, Indefinite);
            Expect(n, idx, name, float.NegativeInfinity, Indefinite);
            // Finite |x| ≥ 2^63 — the argument as is.
            Expect(n, idx, name, two63, Bits(two63));
            Expect(n, idx, name, -big, Bits(-big));
            Expect(n, idx, name, float.MaxValue, Bits(float.MaxValue));
            float nan = float.NaN;
            float rn = (float)n.GetByIndex(idx)(null, new UEFrame(), new object[] { nan });
            Check($"{name}(NaN)", float.IsNaN(rn), $"got {rn:R}");
        }
        // Tan (189): fptan on ±∞ is also #IA -> NaN (the port's |x| ≥ 2^63 branch gives NaN for ∞ too).
        Expect(n, 189, "Tan", float.PositiveInfinity, Indefinite);
        Expect(n, 189, "Tan", float.NegativeInfinity, Indefinite);
        Console.WriteLine($"CoreNativesTests: passed {_pass}, failed {_fail}");
        return _fail == 0 ? 0 : 1;
    }
}
