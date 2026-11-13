// RECONCILIATION A6 TESTS (Fire.dll). Expectations are NOT from the port:
//   * ice-x87-*: execution vectors of the real RenderIce/MoveIcePosition/Blit* (IceVectors.cs);
//     input is IceTexture.Update with TimeMethod != 0 (shipped RenderIce(dt): Tick 0x1050a4b0 when
//     +0xe1 != 0), as the frame calls it (hpengine/SdlProcTex.cs). Distinguishability (V9): the former float form,
//     rewritten here from scratch, must DIVERGE from the vectors (ice-x87-old-form-differs).
//   * ice-local-*: the +0x114 rule of UIceTexture::PostLoad and the blit early exit (disasm 0x10509fca-0x1050a0bb,
//     0x10505ec4, 0x10506256), written here as a truth table, and the static frame.
//   * fire-limit-* / fire-trim-*: UFireTexture::Serialize (array grows to SparksLimit) + PostLoad
//     (disasm 0x10508089-0x1050812b): the tag limit is kept unclamped if there are no more sparks than the limit;
//     otherwise clamp to [4, 0x2000] and drop sparks of type >= 0x1f from the end. Expectations worked out by hand.
using HP1.UEScript;

static partial class Program
{
    static readonly List<string> Failed = new();
    static int _checks;

    static void Check(string name, bool ok, string detail = "")
    {
        _checks++;
        if (!ok) { Failed.Add(name); Console.WriteLine($"FAIL {name} {detail}"); }
    }

    static void Run(string name, Action test)
    {
        try { test(); }
        catch (Exception ex) { Check("crash:" + name, false, ex.GetType().Name + ": " + ex.Message); }
    }

    static int Main()
    {
        Run("IceX87Vectors", IceX87Vectors);
        Run("IceX87OldFormDiffers", IceX87OldFormDiffers);
        Run("IceLocalRule", IceLocalRule);
        Run("IceLocalStatic", IceLocalStatic);
        Run("FireSparksLimit", FireSparksLimit);
        Run("FireTrim", FireTrim);
        Console.WriteLine($"checks={_checks} failed={Failed.Count}");
        if (Failed.Count == 0) { Console.WriteLine("WAVE A6 TESTS PASS"); return 0; }
        Console.WriteLine("WAVE A6 TESTS FAIL: " + string.Join(", ", Failed));
        return 1;
    }

    static float F(uint bits) => BitConverter.Int32BitsToSingle(unchecked((int)bits));
    static string H(float f) => BitConverter.SingleToInt32Bits(f).ToString("x8");

    static IceTexture Make(IceVec v, bool local = false)
        => new IceTexture(v.W, v.H, Convert.FromHexString(v.Src), Convert.FromHexString(v.Glass), v.Style, v.Freq, v.Amp,
                          v.Hs, v.Vs, F(v.U), F(v.V), F(v.M), v.MoveIce, 1, 0f, 0f, 0, local);

    // ── shipped execution vectors ─────────────────────────────────────────────
    static void IceX87Vectors()
    {
        foreach (var v in IceVectors)
        {
            var ice = Make(v);
            double now = 0; int frame = 0, bad = -1; string got = "";
            for (int k = 0; k < v.Dts.Length; k++)
            {
                float dt = F(v.Dts[k]);
                now += dt; frame++;
                if ((float)(now - ice.LastUpdateTime) != dt) throw new InvalidOperationException($"dt of step {k} is not exact");
                ice.Update(now, frame);
                got = $"{H(ice.MasterCount)} {H(ice.UPosition)} {H(ice.VPosition)} {H(ice.UDisplace)} {H(ice.VDisplace)} {ice.RoundedU} {ice.RoundedV} {Convert.ToHexString(ice.Mip0).ToLowerInvariant()}";
                if (got != v.Expect[k]) { bad = k; break; }
            }
            Check($"ice-x87-{v.Name}", bad < 0, bad < 0 ? "" : $"step {bad}: expected \"{v.Expect[bad][..60]}…\", got \"{got[..60]}…\"");
        }
    }

    // Former port form (2026-10-30: the PC64 register chain — the texture ticks inside the render window, PC24), rewritten
    // here from scratch: it must diverge from the shipped execution in every vector and change the drawn offset in at
    // least one (otherwise the vectors do not separate the windows, V9). The float-per-step form of the Unity port is no
    // longer a control: at PC24 shipped rounds every step to 24 bits too.
    static void IceX87OldFormDiffers()
    {
        static HP1.Core.X87Ext X(double d) => HP1.Core.X87Ext.From(d);
        static HP1.Core.X87Ext R(HP1.Core.X87Ext x) => HP1.Core.X87Ext.Round64(x);
        static double Sin(double a) => HP1.Core.UEX87Transcendental.Fsin(X(a)).ToDouble();
        static double Cos(double a) => HP1.Core.UEX87Transcendental.Fcos(X(a)).ToDouble();
        int differs = 0, roundDiffers = 0;
        foreach (var v in IceVectors)
        {
            float m = F(v.M), u = F(v.U), vv = F(v.V);
            bool any = false, anyRound = false;
            for (int k = 0; k < v.Dts.Length; k++)
            {
                float dt = F(v.Dts[k]);
                m = R(R(X(dt) * X(120f)) + X(m)).ToFloat();
                HP1.Core.X87Ext du = R(X(v.Hs - 128) * X(dt)), dv = R(X(v.Vs - 128) * X(dt));
                u = R(X(u) - R(du + du)).ToFloat();
                vv = R(R(dv + dv) + X(vv)).ToFloat();
                int A = v.Amp + 1, Fq = v.Freq + 1;
                double Arg(float c) => R(R(X(Fq) * X(m)) * X(c)).ToDouble();
                float Shift(bool half, double trig, float pos)
                {
                    HP1.Core.X87Ext kk = X(A);
                    if (half) kk = R(kk * X(0.5f));
                    int n = (int)Math.Round(R(kk * X(trig)).ToFloat(), MidpointRounding.ToEven);
                    return R(X(n) + X(pos)).ToFloat();
                }
                float ud = u, vd = vv;
                switch (v.Style)
                {
                    case 1: ud = Shift(false, Sin(Arg(0.0012f)), u); vd = Shift(false, Cos(Arg(0.0012f)), vv); break;
                    case 2: ud = Shift(false, Sin(Arg(0.0012f)), u); vd = Shift(false, Cos(Arg(0.0011f)), vv); break;
                    case 3: ud = Shift(true, Sin(Arg(0.0012f)), u); break;
                    case 4: vd = Shift(true, Cos(Arg(0.0012f)), vv); break;
                }
                var e = v.Expect[k].Split(' ');
                string old = $"{H(m)} {H(u)} {H(vv)} {H(ud)} {H(vd)}";
                if (old != string.Join(' ', e[..5])) any = true;
                if ((int)Math.Round(ud, MidpointRounding.ToEven) != int.Parse(e[5]) || (int)Math.Round(vd, MidpointRounding.ToEven) != int.Parse(e[6])) anyRound = true;
            }
            if (any) differs++;
            if (anyRound) roundDiffers++;
        }
        Check("ice-x87-old-form-differs", differs == IceVectors.Length, $"the PC64 chain diverged in {differs} of {IceVectors.Length}");
        Check("ice-x87-old-form-differs-in-frame", roundDiffers >= 1, $"in the rounded offset — {roundDiffers}");
    }

    // ── LocalSource (+0x114) ──────────────────────────────────────────────────
    // Shipped rule (UIceTexture::PostLoad, disasm): +0x114 is set if the source has a DIFFERENT size
    // (0x10509fca-0x10509fe3) or there is no glass / it has a different size (0x10509fea-0x1050a00c); otherwise 0.
    static void IceLocalRule()
    {
        // (ice, source, glass valid) -> expected
        var table = new (int iw, int ih, int sw, int sh, bool glass, bool expect)[]
        {
            (64, 64, 64, 64, true, false),    // same size, glass present: animated
            (64, 64, 32, 64, true, true),     // source narrower in U: stretched, static
            (64, 64, 64, 16, true, true),     // source shorter in V
            (64, 64, 64, 64, false, true),    // no glass: source copied into the mip, static
            (32, 128, 32, 128, true, false),
            (32, 128, 32, 128, false, true),
        };
        foreach (var t in table)
            Check($"ice-local-rule-{t.iw}x{t.ih}-{t.sw}x{t.sh}-{(t.glass ? "g" : "nog")}",
                  UEProceduralTextureData.IceLocalSource(t.iw, t.ih, t.sw, t.sh, t.glass) == t.expect);
    }

    // +0x114 != 0: BlitTexIce/BlitIceTex exit immediately (0x10505ec4 / 0x10506256); frame = source
    // however the shifts move; no glass needed.
    static void IceLocalStatic()
    {
        var v = IceVectors[0];
        byte[] src = Convert.FromHexString(v.Src);
        foreach (bool moveIce in new[] { false, true })
        {
            var ice = new IceTexture(v.W, v.H, src, null, 1, 200, 200, 255, 0, 0f, 0f, 0f, moveIce, 1, 0f, 0f, 0, localSource: true);
            bool same = true, changed = false;
            for (int k = 1; k <= 30; k++)
            {
                changed |= ice.Update(k / 30.0, k);
                same &= ice.Mip0.AsSpan().SequenceEqual(src);
            }
            Check($"ice-local-static-{(moveIce ? "texice" : "icetex")}", same && ice.Blits == 0 && !changed,
                  $"frame=source {same}, blits {ice.Blits}, change flag {changed}");
        }
        // Without localSource the same motion must redraw the frame (otherwise the check above is blind).
        var anim = new IceTexture(v.W, v.H, src, Convert.FromHexString(v.Glass), 1, 200, 200, 255, 0, 0f, 0f, 0f, true, 1, 0f, 0f, 0);
        for (int k = 1; k <= 30; k++) anim.Update(k / 30.0, k);
        Check("ice-local-static-control-animates", anim.Blits > 1, $"blits {anim.Blits}");
        // Without glass and without localSource: loud constructor refusal.
        bool refused = false;
        try { _ = new IceTexture(v.W, v.H, src, null, 1, 1, 1, 128, 128, 0f, 0f, 0f, true, 1, 0f, 0f, 0); }
        catch (ArgumentException) { refused = true; }
        Check("ice-local-glass-required-otherwise", refused);
    }

    // ── fire: SparksLimit after Serialize+PostLoad ────────────────────────────
    static byte[] Spark(byte type, byte id, byte x = 5, byte y = 5) => new byte[] { type, id, x, y, 0, 0, 0, 0 };

    static void FireSparksLimit()
    {
        // Limit 2, one emitter spark of type 0x04 (spawns 0x20 when R() < 0x80 while NumSparks < SparksLimit):
        // the spark array (1) is not above the limit (2) -> PostLoad exits before the clamp, the limit stays 2.
        var t = new FireTexture(16, 16, 128, false, 2, Spark(0x04, 0xf0), 0);
        var rng = new FractalRandom(); int k = 0; rng.Seed(() => unchecked(k++ * 1103515245 + 12345) >> 16);
        int max = 0;
        for (int i = 0; i < 200; i++) { t.ConstantTimeTick(rng); max = Math.Max(max, t.NumSparks); }
        Check("fire-limit-kept-below-4", t.SparksLimit == 2 && max == 2, $"SparksLimit={t.SparksLimit}, max sparks {max} (expected 2 and 2)");
        var big = new FireTexture(16, 16, 128, false, 0x2001, Spark(0x00, 1), 0);
        Check("fire-limit-kept-above-0x2000", big.SparksLimit == 0x2001, $"SparksLimit={big.SparksLimit}");
        // Sparks (3) above the limit (2) -> clamp to 4, nothing to drop.
        var over = new FireTexture(16, 16, 128, false, 2, Spark(0, 1).Concat(Spark(0, 2)).Concat(Spark(0, 3)).ToArray(), 0);
        Check("fire-limit-clamped-when-overfull", over.SparksLimit == 4 && over.NumSparks == 3, $"SparksLimit={over.SparksLimit}, NumSparks={over.NumSparks}");
    }

    static void FireTrim()
    {
        // Limit 5, 7 sparks (type/id): a 04/1, b 21/2, c 00/3, d 1f/4, e 04/5, f 22/6, g 00/7.
        // From the end: g (0x00 < 0x1f) skipped; f (0x22) dropped -> g takes its place, 6 sparks; e skipped; d (0x1f) dropped ->
        // the last one (g) takes its place, 5 sparks = limit. Result: a b c g e.
        byte[][] all = { Spark(0x04, 1), Spark(0x21, 2), Spark(0x00, 3), Spark(0x1f, 4), Spark(0x04, 5), Spark(0x22, 6), Spark(0x00, 7) };
        var t = new FireTexture(16, 16, 128, false, 5, all.SelectMany(b => b).ToArray(), 0);
        byte[] ids = Enumerable.Range(0, t.NumSparks).Select(i => t.Sparks[i * 8 + 1]).ToArray();
        Check("fire-trim-transient-from-end", t.SparksLimit == 5 && t.NumSparks == 5 && ids.SequenceEqual(new byte[] { 1, 2, 3, 7, 5 }),
              $"limit {t.SparksLimit}, sparks {t.NumSparks}, id {string.Join(",", ids)} (expected 1,2,3,7,5)");
        // Limit 4, 6 sparks of "persistent" types only (< 0x1f): nothing to drop -> NumSparks = min(4, 6), the first four.
        byte[][] keep = { Spark(0x04, 1), Spark(0x00, 2), Spark(0x1e, 3), Spark(0x16, 4), Spark(0x04, 5), Spark(0x00, 6) };
        var k2 = new FireTexture(16, 16, 128, false, 4, keep.SelectMany(b => b).ToArray(), 0);
        byte[] ids2 = Enumerable.Range(0, k2.NumSparks).Select(i => k2.Sparks[i * 8 + 1]).ToArray();
        Check("fire-trim-min-when-nothing-transient", k2.NumSparks == 4 && ids2.SequenceEqual(new byte[] { 1, 2, 3, 4 }),
              $"sparks {k2.NumSparks}, id {string.Join(",", ids2)} (expected 1,2,3,4)");
    }
}
