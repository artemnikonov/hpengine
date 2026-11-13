// Comparison of the Core canvas with shipped HP1 1.53, no game files needed.
//
// Input: natives from the same registration the VM calls (BindFrameProducer -> RegisterCanvasOps ->
// GetByIndex: 464 StrLen, 465 DrawText, 469 DrawTextClipped); the canvas is a bare UEObject, the font is a
// synthetic Hp1FontMetrics placed in the producer's catalogue (shipped fonts are not
// required). Expectations follow the Engine.dll 1.53 disasm and are computed here INDEPENDENTLY: an exact
// binary fraction (BigInteger) rounded to nearest even at 24 bits — a model of the x87
// register under PC24 (the URender::PreRender..PostRender window, appEnableFastMath(1) = _controlfp(_PC_24),
// Core.dll.c:57998; HUD and console run inside it, Engine.dll.c:117933..117993) — and
// _ftol truncation. Neither X87Ext nor ported code is used for the expectations.
// Red mutations — redtest.sh.
using System.Numerics;
using System.Reflection;
using HP1.UEScript;

sealed class CanvasNatives : UENativesCore
{
    public CanvasNatives() : base(1) { }
}

static class Program
{
    static int _fail, _pass;

    static void Check(string name, bool ok, string detail)
    {
        if (ok) _pass++; else { _fail++; Console.WriteLine($"FAIL {name}: {detail}"); }
    }

    // ── Independent x87 PC24 model ───────────────────────────────────────
    // Exact a + b (int + float) as m·2^e; round to nearest even at 24
    // significant bits (PC24 changes only the result mantissa; the exponent is extended); then
    // _ftol — truncation toward zero.
    static (BigInteger M, int E) Exact(float f)
    {
        int bits = BitConverter.SingleToInt32Bits(f);
        int exp = (bits >> 23) & 0xff, man = bits & 0x7fffff;
        if (exp == 0) exp = 1; else man |= 0x800000;
        return (bits < 0 ? -man : man, exp - 150);
    }

    static (BigInteger M, int E) Add(int a, float b)
    {
        var (m, e) = Exact(b);
        if (e >= 0) return (new BigInteger(a) + (m << e), 0);
        return ((new BigInteger(a) << -e) + m, e);
    }

    static (BigInteger M, int E) Round24((BigInteger M, int E) v)
    {
        BigInteger m = BigInteger.Abs(v.M);
        if (m.IsZero) return (0, 0);
        int len = (int)m.GetBitLength();
        if (len <= 24) return v;
        int drop = len - 24;
        BigInteger q = m >> drop, r = m - (q << drop), half = BigInteger.One << (drop - 1);
        if (r > half || (r == half && !q.IsEven)) q += 1;
        return (v.M.Sign < 0 ? -q : q, v.E + drop);
    }

    static int Trunc((BigInteger M, int E) v)
    {
        if (v.E >= 0) return (int)(v.M << v.E);
        BigInteger d = BigInteger.One << -v.E;
        return (int)BigInteger.Divide(v.M, d);   // BigInteger.Divide truncates toward zero, like _ftol
    }

    static int FtolPc24(int a, float b) => Trunc(Round24(Add(a, b)));

    // ── Synthetic font ───────────────────────────────────────────────────
    // CharactersPerPage = 256, one page with a texture. Glyph (StartU, StartV, USize, VSize).
    const int WA = 8, WB = 7, WUnder = 6, WSpace = 4, H = 10;
    static Hp1FontMetrics MakeFont()
    {
        var chars = new Hp1FontChar[256];
        chars['A'] = new Hp1FontChar(10, 0, WA, H);
        chars['B'] = new Hp1FontChar(20, 0, WB, H);
        chars['_'] = new Hp1FontChar(30, 0, WUnder, H);
        chars['a'] = new Hp1FontChar(40, 0, 0, H);     // USize 0 for a lowercase letter -> glyph 'A'
        chars[' '] = new Hp1FontChar(50, 0, WSpace, H);
        return new Hp1FontMetrics
        {
            Path = FontPath, CharactersPerPage = 256,
            Pages = new[] { new Hp1FontPage { HasTexture = true, Characters = chars } },
            CharRemap = new Dictionary<int, int>(), IsRemapped = false,
        };
    }
    const string FontPath = "Synth.TestFont";

    static UEWorldFrameProducer _producer;
    static CanvasNatives _natives;

    static UEObject NewCanvas()
    {
        var c = new UEObject();
        c.Props["Font"] = new UEResourceRef("Font", "TestFont", FontPath);
        c.Props["Style"] = (byte)1;
        c.Props["ClipX"] = 640f; c.Props["ClipY"] = 480f;
        c.Props["CurX"] = 0f; c.Props["CurY"] = 0f; c.Props["CurYL"] = 0f;
        c.Props["SpaceX"] = 0f; c.Props["SpaceY"] = 0f;
        c.Props["OrgX"] = 0f; c.Props["OrgY"] = 0f;
        c.Props["bCenter"] = false; c.Props["bNoSmooth"] = false;
        _producer.SetCanvasFrameForTest(c, 640, 480);
        return c;
    }

    static float F(UEObject c, string p) => UEValue.AsFloat(c.GetProp(p));

    static object[] Call(int idx, UEObject canvas, params object[] args)
    {
        _natives.GetByIndex(idx)(null, new UEFrame { Object = canvas, Context = canvas }, args);
        return args;
    }

    static int Main()
    {
        string sys = Path.Combine(Path.GetTempPath(), "auditcanvas_" + Environment.ProcessId);
        Directory.CreateDirectory(Path.Combine(sys, "0"));
        File.WriteAllText(Path.Combine(sys, "0", "Default.ini"),
            "[Galaxy.GalaxyAudioSubsystem]\nUseSurround=False\nReverseStereo=False\nAmbientFactor=0.7\nDopplerSpeed=9000.0\nLatency=40\nOutputRate=22050Hz\n");
        _producer = new UEWorldFrameProducer(sys, 640, 480);
        var catalog = typeof(UEWorldFrameProducer).GetField("_fonts", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_producer);
        var cache = (Dictionary<string, Hp1FontMetrics>)catalog!.GetType().GetField("_cache", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(catalog)!;
        cache[FontPath] = MakeFont();
        _natives = new CanvasNatives();
        _natives.BindFrameProducer(_producer);
        // Shipped HUD/console run in the PC24 window (see header); the port holds the same flag.
        UEWorldFrameProducer.FastMathWindow = true;
        try { Run(); }
        finally { UEWorldFrameProducer.FastMathWindow = false; Directory.Delete(sys, true); }
        Console.WriteLine($"AuditCanvasTests: passed {_pass}, failed {_fail}");
        return _fail == 0 ? 0 : 1;
    }

    static void Run()
    {
        // Model control: the distinguishing inputs really distinguish.
        Check("model-control", FtolPc24(WA, -1e-7f) == 8 && (int)Math.Truncate(WA + (double)-1e-7f) == 7,
            "8 - 1e-7: PC24 -> 8, exact sum -> 7");

        // [A] Glyph width in WrappedPrint (0x10387f56: fild W; fadd SpaceX; call _ftol — no fstp,
        // the PC24 register rounds the sum to 24 bits BEFORE truncation). SpaceX = -1e-7: shipped gives 8 per glyph.
        {
            var c = NewCanvas();
            c.Props["SpaceX"] = -1e-7f;
            object[] a = Call(464, c, "AAA", 0f, 0f);
            int want = 3 * FtolPc24(WA, -1e-7f);
            Check("A strlen-advance-pc24", UEValue.AsFloat(a[1]) == want && UEValue.AsFloat(a[2]) == H,
                $"StrLen(AAA) = {a[1]}x{a[2]}, expected {want}x{H}");
            // The same step in drawing (FUN_10388530 0x103889ab) — measure and output agree.
            c = NewCanvas();
            c.Props["SpaceX"] = -1e-7f;
            _producer.ClearHudForTest();
            Call(465, c, "AAA", false);
            var g = _producer.HudForTest[0].Glyphs;
            Check("A draw-advance-pc24", g.Length == 3 && g[1].X == want / 3 && g[2].X == 2 * (want / 3),
                $"glyphs X = {string.Join(",", g.Select(x => x.X))}");
            Check("A drawtext-curx", F(c, "CurX") == want, $"CurX = {F(c, "CurX")}, expected {want}");
        }

        // [B] Centring (0x103880ad: fild cutX/2; fsubr CurX; call _ftol — no fstp, PC24).
        // CurX = 2^-30, string "AA": cutX = 16, X = ftol(round24(2^-30 - 8)) = -8; the glyph goes
        // to the device at ftol(round24(X + OrgX)) (0x10388957) = 92 with OrgX = 100.
        {
            var c = NewCanvas();
            float curX = MathF.ScaleB(1f, -30);
            c.Props["CurX"] = curX; c.Props["OrgX"] = 100f; c.Props["bCenter"] = true;
            _producer.ClearHudForTest();
            Call(465, c, "AA", false);
            int drawX = Trunc(Round24(Add(-(2 * WA) / 2, curX)));
            int wantGlyph = FtolPc24(drawX, 100f);
            var cmd = _producer.HudForTest[0];
            Check("B center-pc24", drawX == -8 && cmd.Glyphs[0].X == wantGlyph && cmd.X == drawX + 100f,
                $"center: command X = {cmd.X}, glyph X = {cmd.Glyphs[0].X}, expected {drawX + 100}/{wantGlyph}");
            // bCenter: after the string the pen returns to the saved CurX (0x10388296), then
            // execDrawText adds XL (0x10389465).
            Check("B center-pen", F(c, "CurX") == (float)(curX + 2 * WA), $"CurX = {F(c, "CurX"):R}");
        }

        // [C] WrappedPrint entry (0x10387c8f: fld ClipY; fcomp 0.0; test ah, 1; jne exit):
        // NaN gives "unordered" (C0 = 1) -> exit with XL = YL = 0 (0x10388337).
        {
            var c = NewCanvas();
            c.Props["ClipY"] = float.NaN;
            object[] a = Call(464, c, "AAA", 5f, 5f);
            Check("C cliply-nan-exit", UEValue.AsFloat(a[1]) == 0f && UEValue.AsFloat(a[2]) == 0f,
                $"StrLen with ClipY = NaN = {a[1]}x{a[2]}, expected 0x0");
            c = NewCanvas();
            c.Props["ClipX"] = float.NaN;
            a = Call(464, c, "AAA", 5f, 5f);
            Check("C clipx-nan-exit", UEValue.AsFloat(a[1]) == 0f && UEValue.AsFloat(a[2]) == 0f,
                $"StrLen with ClipX = NaN = {a[1]}x{a[2]}, expected 0x0");
        }

        // [D] execDrawText, CurYL (0x10389471: fld CurYL; fcomp st(1)=YL; test ah, 1; jne -> YL):
        // C0 is set when CurYL < YL and on NaN — the new CurYL = YL.
        {
            var c = NewCanvas();
            c.Props["CurYL"] = float.NaN;
            Call(465, c, "A", false);
            Check("D curyl-nan", F(c, "CurYL") == H, $"CurYL = {F(c, "CurYL")}, expected {H}");
            c = NewCanvas();
            c.Props["CurYL"] = 25f;
            Call(465, c, "A", false);
            Check("D curyl-keep", F(c, "CurYL") == 25f, $"CurYL = {F(c, "CurYL")}, expected 25");
        }

        // [E] DrawTextClipped hotkey (FUN_10388530 0x1038862b..0x103886f7, 0x103887d6..0x1038881a,
        // 0x10388993..0x103889c4): "&AB" -> 'A' without advancing; then '_' of width min(USize 'A', USize '_')
        // at the same place; the pen moves by USize 'A'; then 'B'. DrawTextClipped does not move the canvas pen.
        {
            var c = NewCanvas();
            c.Props["CurX"] = 5f; c.Props["CurY"] = 7f;
            _producer.ClearHudForTest();
            Call(469, c, "&AB", true);
            var g = _producer.HudForTest[0].Glyphs;
            bool ok = g.Length == 3
                && g[0].U == 10 && g[0].X == 5 && g[0].XL == WA
                && g[1].U == 30 && g[1].X == 5 && g[1].XL == Math.Min(WA, WUnder)
                && g[2].U == 20 && g[2].X == 5 + WA;
            Check("E hotkey", ok, $"glyphs (U@X/XL) = {string.Join(" ", g.Select(x => $"{x.U}@{x.X}/{x.XL}"))}");
            Check("E pen-untouched", F(c, "CurX") == 5f && F(c, "CurY") == 7f, $"pen {F(c, "CurX")},{F(c, "CurY")}");
        }

        // [F] Lowercase with USize 0 (WrappedPrint 0x10387edd..0x10387f53; FUN_10388530 0x1038878f..0x103887b7):
        // measure and glyph are taken from character − 0x20.
        {
            var c = NewCanvas();
            object[] a = Call(464, c, "aa", 0f, 0f);
            Check("F lower-measure", UEValue.AsFloat(a[1]) == 2 * WA, $"StrLen(aa) = {a[1]}, expected {2 * WA}");
            _producer.ClearHudForTest();
            Call(465, NewCanvas(), "a", false);
            var g = _producer.HudForTest[0].Glyphs;
            Check("F lower-glyph", g.Length == 1 && g[0].U == 10, $"glyph U = {(g.Length > 0 ? g[0].U : -1)}");
        }

        // [G] Text flags (WrappedPrint 0x10387d54..0x10387d79): Style 3 -> 4, 4 -> 0x40, others -> 0;
        // always | 0x1000802.
        foreach (var (style, want) in new[] { (1, 0x1000802), (2, 0x1000802), (3, 0x1000806), (4, 0x1000842) })
        {
            var c = NewCanvas();
            c.Props["Style"] = (byte)style;
            _producer.ClearHudForTest();
            Call(465, c, "A", false);
            int got = _producer.HudForTest[0].PolyFlags;
            Check($"G flags style {style}", got == want, $"0x{got:x}, expected 0x{want:x}");
        }

        // [H] Wrapping: line width limited by ClipX; split at a space, spaces after the split
        // are skipped (0x10387f80..0x10387fdc, 0x103882cc..0x103882e3). "AA AA" with ClipX = 24:
        // first line "AA" (16), second "AA" -> XL = 16, YL = 2·H.
        {
            var c = NewCanvas();
            c.Props["ClipX"] = 24f;
            object[] a = Call(464, c, "AA AA", 0f, 0f);
            Check("H wrap", UEValue.AsFloat(a[1]) == 2 * WA && UEValue.AsFloat(a[2]) == 2 * H,
                $"StrLen(AA AA, ClipX 24) = {a[1]}x{a[2]}, expected {2 * WA}x{2 * H}");
        }
    }
}
