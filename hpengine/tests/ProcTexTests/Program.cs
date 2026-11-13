// ProcTex TESTS. Expected values are computed HERE, independently of the port:
//   * ice: the reference formulas (HP1IceTextures.cs:118-173) and the decompile texel address from the reference header
//     (BlitIceTex/BlitTexIce, :49-54), rewritten from scratch (modulo via ((v % n) + n) % n, not a mask);
//     the UTexture::Tick accumulator in both forms (float of the ice reference and double of Core); the class must
//     follow the Core form;
//   * fire/water: an independent instance of the Core simulation (FireTexture/WaterTexture) on a COPY of the ring,
//     run directly by the test, plus the reference palette rule (HP1FireTexture.cs:53, HP1WetTextures.cs:142)
//     written here independently; cadence step counts come from the formula in the test and a hand constant.
// Synthetic data: no game files in the cloud.
using HP1.UEScript;
using HPEngine.ProcTex;

static class Program
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
        Run("FireCadence60", FireCadence60);
        Run("IceMovePosition", IceMovePosition);
        Run("IcePanAnchor", IcePanAnchor);
        Run("IceBlitBranches", IceBlitBranches);
        Run("IceRoundingToEven", IceRoundingToEven);
        Run("IceChangedFlag", IceChangedFlag);
        Run("IceCadence", IceCadence);
        Run("IceAccumulatorForm", IceAccumulatorForm);
        Run("IceRealTimeScroll", IceRealTimeScroll);
        Run("IcePrime", IcePrime);
        Run("IceRgba", IceRgba);
        Run("IceRefusals", IceRefusals);
        Run("FireRgbaAndSteps", FireRgbaAndSteps);
        Run("FireCadence", FireCadence);
        Run("FireResolve", FireResolve);
        Run("WetRgbaAndSteps", WetRgbaAndSteps);
        Run("WetGatingAndStops", WetGatingAndStops);
        Run("RingUnseeded", RingUnseeded);
        Run("DefaultRingShared", DefaultRingShared);
        Run("VersionFlag", VersionFlag);
        Console.WriteLine($"checks={_checks} failed={Failed.Count}");
        if (Failed.Count == 0) { Console.WriteLine("PROCTEX TESTS PASS"); return 0; }
        Console.WriteLine("PROCTEX TESTS FAIL: " + string.Join(", ", Failed));
        return 1;
    }

    // ── ice: independent formulas ─────────────────────────────────────────────

    static int Mod(int v, int n) => ((v % n) + n) % n;
    static float RoundEven(float x) => (float)Math.Round((double)x, MidpointRounding.ToEven);

    // Reference MoveIcePosition (HP1IceTextures.cs:118-146), rewritten here.
    struct IceRef
    {
        public float M, U, V, UD, VD;
        public void Move(float dt, int style, int amp, int freq, int h, int v)
        {
            M += dt * 120f;
            U -= 2f * (h - 128) * dt;
            V += 2f * (v - 128) * dt;
            float A = amp + 1, F = freq + 1;
            if (style == 1) { UD = RoundEven(A * (float)Math.Sin(F * M * 0.0012f)) + U; VD = RoundEven(A * (float)Math.Cos(F * M * 0.0012f)) + V; }
            else if (style == 2) { UD = RoundEven(A * (float)Math.Sin(F * M * 0.0012f)) + U; VD = RoundEven(A * (float)Math.Cos(F * M * 0.0011f)) + V; }
            else if (style == 3) { UD = RoundEven(A * 0.5f * (float)Math.Sin(F * M * 0.0012f)) + U; VD = V; }
            else if (style == 4) { UD = U; VD = RoundEven(A * 0.5f * (float)Math.Cos(F * M * 0.0012f)) + V; }
            else { UD = U; VD = V; }
        }
    }

    static byte[] Pattern(int n, int mul, int add)
    {
        var b = new byte[n];
        uint s = (uint)(mul * 2654435761u + (uint)add);
        for (int i = 0; i < n; i++) { s = s * 1103515245u + 12345u; b[i] = (byte)(s >> 16); }
        return b;
    }

    static IceTexture Ice(int w, int h, int style, int amp, int freq, int hs, int vs, float u, float v, float m,
                          bool moveIce, int timeMethod, float minFr, float maxFr, byte prime = 0,
                          byte[] src = null, byte[] glass = null)
        => new IceTexture(w, h, src ?? Pattern(w * h, 3, 1), glass ?? Pattern(w * h, 7, 2), style, freq, amp, hs, vs,
                          u, v, m, moveIce, timeMethod, minFr, maxFr, prime);

    static void IceMovePosition()
    {
        // TIME_FrameRateSync, MaxFrameRate=0: every drawn frame is exactly one
        // ConstantTimeTick = MoveIcePosition(1/120) (reference :149-158).
        for (int style = 0; style <= 4; style++)
        {
            var ice = Ice(16, 16, style, 44, 11, 131, 120, 23f, -38f, 3528f, true, 0, 0f, 0f);
            var r = new IceRef { M = 3528f, U = 23f, V = -38f, UD = 23f, VD = -38f };
            bool ok = true; string det = "";
            for (int k = 1; k <= 400; k++)
            {
                ice.Update(k / 60.0, k);
                r.Move(1f / 120f, style, 44, 11, 131, 120);
                if (ice.UDisplace != r.UD || ice.VDisplace != r.VD || ice.MasterCount != r.M
                    || ice.RoundedU != (int)Math.Round(r.UD, MidpointRounding.ToEven)
                    || ice.RoundedV != (int)Math.Round(r.VD, MidpointRounding.ToEven))
                { ok = false; det = $"k={k} UD {ice.UDisplace} vs {r.UD}, VD {ice.VDisplace} vs {r.VD}"; break; }
            }
            Check($"ice-move-style{style}", ok, det);
        }
        // style 2 differs from style 1 (0.0011 vs 0.0012 for VD); otherwise the check above is blind
        var a = new IceRef { M = 3528f }; var b = new IceRef { M = 3528f };
        bool differ = false;
        for (int k = 0; k < 400 && !differ; k++) { a.Move(1f / 120f, 1, 44, 11, 128, 128); b.Move(1f / 120f, 2, 44, 11, 128, 128); differ = a.VD != b.VD; }
        Check("ice-move-style2-distinguishable", differ);
    }

    static void IcePanAnchor()
    {
        // Hand anchor: HorizPanSpeed=188 (speed 2·60 = 120 texels/s, negative sign),
        // VertPanSpeed=98 (2·(−30) = −60 texels/s, positive sign); 120 steps of 1/120 s = 1 s.
        var ice = Ice(16, 16, 0, 0, 0, 188, 98, 0f, 0f, 0f, false, 0, 0f, 0f);
        for (int k = 1; k <= 120; k++) ice.Update(k / 60.0, k);
        Check("ice-pan-anchor-u", ice.RoundedU == -120, $"U={ice.UDisplace}");
        Check("ice-pan-anchor-v", ice.RoundedV == -60, $"V={ice.VDisplace}");
        Check("ice-pan-anchor-master", Math.Abs(ice.MasterCount - 120f) < 1e-3f, $"M={ice.MasterCount}");
    }

    // Decompile texel address (reference header :49-54), modulo without masks.
    static byte[] ExpectedBlit(byte[] src, byte[] glass, int w, int h, int ud, int vd, bool moveIce)
    {
        var d = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (moveIce)
                {
                    int g = glass[Mod(y + vd, h) * w + Mod(x + ud, w)];
                    d[y * w + x] = src[y * w + Mod(x + g, w)];
                }
                else
                {
                    int g = glass[y * w + x];
                    d[y * w + x] = src[Mod(y + vd, h) * w + Mod(x + ud + g, w)];
                }
            }
        return d;
    }

    static void IceBlitBranches()
    {
        // Non-square 16×8 catches swapped U/V masks; both negative and positive shifts.
        const int W = 16, H = 8;
        var src = Pattern(W * H, 5, 9); var glass = Pattern(W * H, 11, 4);
        foreach (bool moveIce in new[] { false, true })
            foreach (var (u, v) in new[] { (-3f, 5f), (7f, -2f), (0f, 0f), (21f, 13f) })
            {
                string name = $"ice-blit-{(moveIce ? "TexIce" : "IceTex")}-{u}_{v}";
                try
                {
                    var ice = Ice(W, H, 0, 0, 0, 128, 128, u, v, 0f, moveIce, 0, 0f, 0f, 0, src, glass);
                    bool changed = ice.Update(1.0, 1);
                    var exp = ExpectedBlit(src, glass, W, H, (int)u, (int)v, moveIce);
                    Check(name, changed && ice.Mip0.AsSpan().SequenceEqual(exp));
                }
                catch (Exception ex) { Check(name, false, ex.GetType().Name + ": " + ex.Message); }
            }
        // the branches are distinguishable on this data
        var e0 = ExpectedBlit(src, glass, W, H, -3, 5, false); var e1 = ExpectedBlit(src, glass, W, H, -3, 5, true);
        Check("ice-blit-branches-distinguishable", !e0.AsSpan().SequenceEqual(e1));
    }

    static void IceRoundingToEven()
    {
        // fistp/Mathf.RoundToInt: round half to even. 2.5 -> 2, -2.5 -> -2 (away from zero would give 3 and -3).
        var src = Pattern(64, 5, 9); var glass = Pattern(64, 11, 4);
        var ice = Ice(8, 8, 0, 0, 0, 128, 128, 2.5f, -2.5f, 0f, false, 0, 0f, 0f, 0, src, glass);
        ice.Update(1.0, 1);
        Check("ice-round-even-u", ice.RoundedU == 2, $"U={ice.RoundedU}");
        Check("ice-round-even-v", ice.RoundedV == -2, $"V={ice.RoundedV}");
        Check("ice-round-even-blit", ice.Mip0.AsSpan().SequenceEqual(ExpectedBlit(src, glass, 8, 8, 2, -2, false)));
    }

    static void IceChangedFlag()
    {
        // HorizPanSpeed=129: U decreases by 2·1/120 = 1/60 per frame; redraw only when the
        // rounded shift changes (RenderIce +0x104/+0x108).
        var ice = Ice(8, 8, 0, 0, 0, 129, 128, 0f, 0f, 0f, false, 0, 0f, 0f);
        var r = new IceRef();
        int prev = int.MinValue, expectedChanges = 0, gotChanges = 0; bool ok = true;
        for (int k = 1; k <= 300; k++)
        {
            bool ch = ice.Update(k / 60.0, k);
            r.Move(1f / 120f, 0, 0, 0, 129, 128);
            int ru = (int)Math.Round(r.UD, MidpointRounding.ToEven);
            bool exp = ru != prev; prev = ru;
            if (exp) expectedChanges++;
            if (ch) gotChanges++;
            if (ch != exp) { ok = false; break; }
            // a repeat in the same frame at the same time is neither a redraw nor an Update
            int upd = ice.Updates;
            if (ice.Update(k / 60.0, k) || ice.Updates != upd) { ok = false; break; }
        }
        Check("ice-changed-only-on-rounded-shift", ok && expectedChanges > 3 && expectedChanges < 300,
              $"exp={expectedChanges} got={gotChanges}");
        // the same Time in a new frame: Update is not called (Time == LastUpdateTime)
        var ice2 = Ice(8, 8, 0, 0, 0, 129, 128, 0f, 0f, 0f, false, 0, 0f, 0f);
        ice2.Update(1.0, 1); ice2.Update(1.0, 2);
        Check("ice-same-time-no-update", ice2.Updates == 1 && ice2.ConstantTicks == 1, $"upd={ice2.Updates}");
    }

    // UTexture::Tick, Core form (double sum), written independently by the test.
    static int StepsDouble(float minFr, float maxFr, float[] dts)
    {
        float max = Math.Clamp(maxFr, 0.1f, 100f), min = Math.Clamp(minFr, 0.1f, 100f);
        float minT = (float)(1.0 / max), maxT = (float)(1.0 / min);
        float acc = 0f; int n = 0;
        foreach (float dt in dts)
        {
            if (maxFr == 0f) { n++; continue; }
            double s = (double)dt + acc; acc = (float)s;
            if (s < minT) continue;
            n++;
            if (s < maxT) { acc = 0f; continue; }
            double rest = (double)acc - maxT; acc = (float)rest; if (rest > maxT) acc = maxT;
        }
        return n;
    }

    // UTexture::Tick, ice reference form (HP1IceTextures.cs:156-173, float).
    static int StepsFloat(float minFr, float maxFr, float[] dts)
    {
        float max = Math.Clamp(maxFr, 0.1f, 100f), min = Math.Clamp(minFr, 0.1f, 100f);
        float minT = 1f / max, maxT = 1f / min;
        float acc = 0f; int n = 0;
        foreach (float dt in dts)
        {
            if (maxFr == 0f) { n++; continue; }
            acc += dt;
            if (minT <= acc)
            {
                n++;
                if (acc < maxT) acc = 0f;
                else { acc -= maxT; if (maxT < acc) acc = maxT; }
            }
        }
        return n;
    }

    static void IceCadence()
    {
        // MaxFrameRate=30 at 60 frames/s for 1 s: 30 steps (by hand: a step every second frame).
        var ice = Ice(8, 8, 1, 10, 3, 128, 128, 0f, 0f, 0f, false, 0, 0f, 30f);
        var dts = new float[60];
        double last = 0;
        for (int k = 1; k <= 60; k++) { double now = k / 60.0; dts[k - 1] = (float)(now - last); last = now; ice.Update(now, k); }
        Check("ice-cadence-30-of-60", ice.ConstantTicks == 30 && StepsDouble(0f, 30f, dts) == 30, $"got={ice.ConstantTicks}");
        // MaxFrameRate=0: a step on every drawn frame
        var ice0 = Ice(8, 8, 1, 10, 3, 128, 128, 0f, 0f, 0f, false, 0, 0f, 0f);
        for (int k = 1; k <= 60; k++) ice0.Update(k / 60.0, k);
        Check("ice-cadence-every-frame", ice0.ConstantTicks == 60, $"got={ice0.ConstantTicks}");
        // D-35: the same ice on a 120 Hz monitor steps at 60 Hz, not once per drawn frame.
        var ice120 = Ice(8, 8, 1, 10, 3, 128, 128, 0f, 0f, 0f, false, 0, 0f, 0f);
        for (int k = 1; k <= 120; k++) ice120.Update(k / 120.0, k);
        Check("ice-cadence-60-at-120hz", ice120.ConstantTicks >= 58 && ice120.ConstantTicks <= 62, $"got={ice120.ConstantTicks}");
        // MinFrameRate=50 (maxTime 0.02 s) with 0.1 s frames: a step every frame, the accumulator
        // is clamped to maxTime; step count = frame count (by hand).
        var iceSlow = Ice(8, 8, 1, 10, 3, 128, 128, 0f, 0f, 0f, false, 0, 50f, 100f);
        for (int k = 1; k <= 10; k++) iceSlow.Update(k * 0.1, k);
        Check("ice-cadence-slow-frames", iceSlow.ConstantTicks == 10 && Math.Abs(iceSlow.Accumulator - 0.02f) < 1e-7f,
              $"got={iceSlow.ConstantTicks} acc={iceSlow.Accumulator}");
    }

    static void IceAccumulatorForm()
    {
        // Input where the reference float form and the Core double form diverge: MaxFrameRate=60,
        // dt1 = 0.01f, dt2 = 0x3bda740f: the exact sum is < 1/60f, the float sum rounds to >= 1/60f.
        float dt2 = BitConverter.Int32BitsToSingle(0x3bda740f);
        double now1 = 0.01, now2 = now1 + dt2;
        float d1 = (float)now1, d2 = (float)(now2 - now1);
        var dts = new[] { d1, d2 };
        int sd = StepsDouble(0f, 60f, dts), sf = StepsFloat(0f, 60f, dts);
        Check("ice-acc-forms-distinguishable", d1 == 0.01f && d2 == dt2 && sd == 0 && sf == 1, $"double={sd} float={sf}");
        var ice = Ice(8, 8, 0, 0, 0, 128, 128, 0f, 0f, 0f, false, 0, 0f, 60f);
        ice.Update(now1, 1); ice.Update(now2, 2);
        Check("ice-acc-core-form", ice.ConstantTicks == sd, $"got={ice.ConstantTicks}");
    }

    static void IceRealTimeScroll()
    {
        // TIME_RealTimeScroll: MoveIcePosition(real delta); the first delta = CurrentTime
        // (LastUpdateTime = 0 at creation, reference :184-190).
        var ice = Ice(8, 8, 0, 0, 0, 138, 128, 0f, 0f, 100f, false, 1, 0f, 30f);
        ice.Update(5.0, 1);
        Check("ice-realtime-first-delta", ice.MasterCount == 100f + 5f * 120f && ice.RealTicks == 1 && ice.ConstantTicks == 0,
              $"M={ice.MasterCount}");
        ice.Update(5.25, 2);
        var r = new IceRef { M = 100f };
        r.Move(5f, 0, 0, 0, 138, 128); r.Move((float)(5.25 - 5.0), 0, 0, 0, 138, 128);
        Check("ice-realtime-second-delta", ice.MasterCount == r.M && ice.UDisplace == r.UD, $"M={ice.MasterCount} vs {r.M}");
    }

    static void IcePrime()
    {
        // PrimeCount=3: the first Update is 3 Prime steps + 1 step (MaxFrameRate=0); then one at a time.
        var ice = Ice(8, 8, 0, 0, 0, 128, 128, 0f, 0f, 0f, false, 0, 0f, 0f, prime: 3);
        ice.Update(1.0, 1);
        Check("ice-prime-first", ice.ConstantTicks == 4, $"got={ice.ConstantTicks}");
        ice.Update(2.0, 2);
        Check("ice-prime-once", ice.ConstantTicks == 5, $"got={ice.ConstantTicks}");
    }

    static void IceRgba()
    {
        var src = Pattern(64, 5, 9); var glass = Pattern(64, 11, 4);
        var pal = Pattern(768, 13, 6);
        var ice = Ice(8, 8, 0, 0, 0, 128, 128, 1f, 2f, 0f, true, 0, 0f, 0f, 0, src, glass);
        ice.Update(1.0, 1);
        var rgba = new byte[256];
        ice.WriteRgba(pal, rgba);
        var exp = ExpectedBlit(src, glass, 8, 8, 1, 2, true);
        bool ok = true;
        for (int i = 0; i < 64 && ok; i++)
            ok = rgba[i * 4] == pal[exp[i] * 3] && rgba[i * 4 + 1] == pal[exp[i] * 3 + 1]
              && rgba[i * 4 + 2] == pal[exp[i] * 3 + 2] && rgba[i * 4 + 3] == 255;
        Check("ice-rgba-palette-alpha255", ok);
    }

    static void IceRefusals()
    {
        bool T(Action a) { try { a(); return false; } catch (ArgumentException) { return true; } }
        Check("ice-refuse-style5", T(() => Ice(8, 8, 5, 0, 0, 128, 128, 0, 0, 0, false, 0, 0, 0)));
        Check("ice-refuse-npot", T(() => Ice(12, 8, 0, 0, 0, 128, 128, 0, 0, 0, false, 0, 0, 0, 0, new byte[96], new byte[96])));
        Check("ice-refuse-glass-size", T(() => Ice(8, 8, 0, 0, 0, 128, 128, 0, 0, 0, false, 0, 0, 0, 0, new byte[64], new byte[32])));
    }

    // ── fire / water ─────────────────────────────────────────────────────────

    static FractalRandom Ring()
    {
        var r = new FractalRandom();
        uint s = 12345;
        r.Seed(() => { s = s * 214013u + 2531011u; return (int)((s >> 16) & 0x7fff); });
        return r;
    }

    static byte[] FirePalette()
    {
        // max(r,g,b) in different channels: alpha = r / g / b depending on the index
        var p = new byte[768];
        for (int i = 0; i < 256; i++)
        {
            p[i * 3] = (byte)(i % 3 == 0 ? 250 - i / 4 : i / 3);
            p[i * 3 + 1] = (byte)(i % 3 == 1 ? 240 - i / 5 : i / 4);
            p[i * 3 + 2] = (byte)(i % 3 == 2 ? 230 - i / 6 : i / 5);
        }
        return p;
    }

    static UEProceduralTextureData.Fire FireRec(string path, float maxFr = 0f, int prime = 2, int heat = 200)
    {
        // sparks: [type, heat, X, Y, 4 parameters]; types 0/1/3 with high heat
        var sparks = new byte[] { 0, 255, 3, 10, 40, 20, 0, 0,
                                  1, 250, 8, 12, 30, 50, 0, 0,
                                  3, 240, 12, 6, 10, 90, 0, 0 };
        int dot = path.LastIndexOf('.');
        return new UEProceduralTextureData.Fire
        {
            Path = path, Name = path.Substring(dot + 1), USize = 16, VSize = 16, RenderHeat = heat, SparksLimit = 32,
            PrimeCount = prime, FxFrequency = 7, MinFrameRate = 0f, MaxFrameRate = maxFr, Rising = true,
            SparkCount = 3, Sparks = sparks, Palette = FirePalette(),
        };
    }

    static FireTexture FireRef(UEProceduralTextureData.Fire d)
        => new FireTexture(d.USize, d.VSize, (byte)d.RenderHeat, d.Rising, d.SparksLimit, d.Sparks, (byte)d.FxFrequency)
           { MinFrameRate = d.MinFrameRate, MaxFrameRate = d.MaxFrameRate, PrimeCount = (byte)d.PrimeCount };

    static bool FireRgbaMatches(byte[] rgba, byte[] mip, byte[] pal)
    {
        for (int i = 0; i < mip.Length; i++)
        {
            byte r = pal[mip[i] * 3], g = pal[mip[i] * 3 + 1], b = pal[mip[i] * 3 + 2];
            byte a = r > g ? (r > b ? r : b) : (g > b ? g : b);
            if (rgba[i * 4] != r || rgba[i * 4 + 1] != g || rgba[i * 4 + 2] != b || rgba[i * 4 + 3] != a) return false;
        }
        return true;
    }

    static void FireRgbaAndSteps()
    {
        var rec = FireRec("Pkg.Grp.Flame");
        var ringA = Ring(); var ringB = Ring();
        var pt = new ProcTextures(new[] { rec }, null, ringA);
        var e = pt.Fire("Pkg.Grp.Flame", 1, 1 / 60.0);
        // independently: creation = Tick(0) (Prime 2 + a step, MaxFrameRate=0)
        var refSim = FireRef(rec);
        refSim.Tick(0f, ringB);
        Check("fire-created", e != null && e.Width == 16 && e.Ticks == 1);
        Check("fire-rgba-after-create", e != null && FireRgbaMatches(e.Rgba, refSim.Mip0, rec.Palette));
        // data is not degenerate: the mip has more than one index and alpha does not equal one channel everywhere
        Check("fire-data-nontrivial", refSim.Mip0.Distinct().Count() > 4);
        bool ok = true;
        for (int k = 2; k <= 20 && e != null; k++)
        {
            double now = k / 60.0;
            pt.MarkFireUsed("Pkg.Grp.Flame", k);
            pt.TickAll(k, now);
            refSim.Tick((float)(now - (k - 1) / 60.0), ringB);
            if (!FireRgbaMatches(e.Rgba, refSim.Mip0, rec.Palette)) { ok = false; break; }
        }
        Check("fire-rgba-per-tick", ok && e.Ticks == 20, $"ticks={e?.Ticks}");
        Check("fire-ring-shared-order", ringA.Index == ringB.Index && ringA.Ring.AsSpan().SequenceEqual(ringB.Ring));
        // the alpha rule is distinguishable from "alpha = R": the palette has indices with max in G and B
        var pal = rec.Palette; bool alphaNotR = false;
        foreach (byte m in refSim.Mip0) if (Math.Max(pal[m * 3], Math.Max(pal[m * 3 + 1], pal[m * 3 + 2])) != pal[m * 3]) { alphaNotR = true; break; }
        Check("fire-alpha-rule-distinguishable", alphaNotR);
    }

    static void FireCadence()
    {
        // MaxFrameRate=20 at 60 frames/s: created in frame 1 (Tick(0): Prime, no step since the accumulator
        // 0 < 1/20), then 60 frames of 1/60 s -> 20 steps (by hand: every third frame).
        var rec = FireRec("Pkg.Grp.Slow", maxFr: 20f, prime: 1);
        var ringA = Ring();
        var pt = new ProcTextures(new[] { rec }, null, ringA);
        var e = pt.Fire("Pkg.Grp.Slow", 1, 1 / 60.0);
        var dts = new List<float> { 0f };
        for (int k = 2; k <= 61; k++)
        {
            double now = k / 60.0;
            pt.MarkFireUsed("Pkg.Grp.Slow", k);
            pt.TickAll(k, now);
            dts.Add((float)(now - (k - 1) / 60.0));
        }
        int steps = StepsDouble(0f, 20f, dts.ToArray());
        Check("fire-cadence-formula-20", steps == 20, $"formula={steps}");
        // independent instance: Prime + exactly `steps` ConstantTimeTick steps
        FireTexture Run(int n) { var s = FireRef(rec); var rg = Ring(); s.PrimeCurrent = 0; for (int i = 0; i < rec.PrimeCount + n; i++) s.ConstantTimeTick(rg); return s; }
        var exact = Run(steps); var less = Run(steps - 1); var more = Run(steps + 1);
        Check("fire-cadence-steps-distinguishable", !exact.Mip0.AsSpan().SequenceEqual(less.Mip0) && !exact.Mip0.AsSpan().SequenceEqual(more.Mip0));
        Check("fire-cadence-steps", e != null && FireRgbaMatches(e.Rgba, exact.Mip0, rec.Palette) && e.Ticks == 61, $"ticks={e?.Ticks}");
    }

    static void FireResolve()
    {
        var a = FireRec("PkgA.G.Spark"); var b = FireRec("PkgB.G.Spark");                 // same content
        var c = FireRec("PkgA.G.Torch"); var d = FireRec("PkgB.G.Torch", heat: 150);      // different content
        var pt = new ProcTextures(new[] { a, b, c, d }, null, Ring());
        var byLeaf = pt.Fire("Spark", 1, 0.5);
        Check("fire-leaf-identical-resolves", byLeaf != null && byLeaf.Key == "PkgA.G.Spark");
        Check("fire-leaf-same-entry", pt.Fire("Other.Spark", 1, 0.5) == byLeaf && pt.FireLive == 1);
        Check("fire-full-path", pt.Fire("PkgB.G.Torch", 1, 0.5)?.Key == "PkgB.G.Torch");
        Check("fire-leaf-ambiguous-stop", pt.Fire("Torch", 1, 0.5) == null && pt.Problems.Any(p => p.Contains("leaf 'Torch'")));
        Check("fire-unknown-null", pt.Fire("Nope.X", 1, 0.5) == null);
        // the path is case-sensitive (the reference Dictionary has no comparer)
        Check("fire-path-case-sensitive", pt.Fire("pkgb.g.torch", 1, 0.5) == null);
    }

    static UEProceduralTextureData.Wet WetRec(string path, int w = 16, int srcW = 16, bool source = true, float maxFr = 0f)
    {
        int bits(int v) { int b = 0; while ((1 << b) < v) b++; return b; }
        var src = new byte[srcW * srcW];
        for (int i = 0; i < src.Length; i++) src[i] = (byte)((i * 37 + i / srcW * 11) % 9);   // indices 0..8, zero included
        var pal = new byte[768];
        for (int i = 0; i < 768; i++) pal[i] = (byte)(i * 29 + 7);
        var drops = new byte[] { 0, 200, 5, 5, 0, 0, 0, 0,  0, 180, 10, 3, 0, 0, 0, 0 };
        return new UEProceduralTextureData.Wet
        {
            Path = path, W = w, H = w, UBits = bits(w), VBits = bits(w),
            DropType = 1, WaveAmp = 128, FxFrequency = 8, FxPhase = 8, FxAmplitude = 255, FxSpeed = 255, FxRadius = 128,
            FxSize = 30, FxDepth = 255, FxTime = 30, NumDrops = 2, Drops = drops, MinFrameRate = 0f, MaxFrameRate = maxFr,
            SourcePath = source ? "Src.Pkg.Water" : "", SourceW = srcW, SourceH = srcW, SourceUBits = bits(srcW), SourceVBits = bits(srcW),
            SourcePalette = source ? pal : Array.Empty<byte>(), SourceMip0 = source ? src : null,
            SourceMissing = source ? "" : "genfluid.Water",
        };
    }

    static WaterTexture WetRef(UEProceduralTextureData.Wet r)
    {
        var w = new WaterTexture(r.W, r.H)
        {
            DropType = (byte)r.DropType, WaveAmp = (byte)r.WaveAmp, FX_Frequency = (byte)r.FxFrequency, FX_Phase = (byte)r.FxPhase,
            FX_Amplitude = (byte)r.FxAmplitude, FX_Speed = (byte)r.FxSpeed, FX_Radius = (byte)r.FxRadius, FX_Size = (byte)r.FxSize,
            FX_Depth = (byte)r.FxDepth, FX_Time = (byte)r.FxTime, NumDrops = r.NumDrops,
            MinFrameRate = r.MinFrameRate, MaxFrameRate = r.MaxFrameRate, PrimeCount = (byte)r.PrimeCount,
            SourceMip0 = r.SourceMip0, SourceUBits = (byte)r.SourceUBits, SourceVBits = (byte)r.SourceVBits,
        };
        Array.Copy(r.Drops, w.Drops, r.Drops.Length);
        w.PostLoad();
        return w;
    }

    static bool WetRgbaMatches(byte[] rgba, byte[] mip, byte[] pal)
    {
        for (int i = 0; i < mip.Length; i++)
        {
            int c = mip[i] * 3;
            byte a = mip[i] == 0 ? (byte)0 : (byte)255;
            if (rgba[i * 4] != pal[c] || rgba[i * 4 + 1] != pal[c + 1] || rgba[i * 4 + 2] != pal[c + 2] || rgba[i * 4 + 3] != a) return false;
        }
        return true;
    }

    static void WetRgbaAndSteps()
    {
        var rec = WetRec("Lev.Water.Pool");
        var ringA = Ring(); var ringB = Ring();
        var pt = new ProcTextures(null, new[] { rec }, ringA);
        var e = pt.Wet("Lev.Water.Pool", 1, 1 / 60.0);
        var refSim = WetRef(rec);
        refSim.Tick(0f, ringB);
        Check("wet-created", e != null && e.Ticks == 1 && e.Water.WaterParity == 1);
        Check("wet-rgba-after-create", e != null && WetRgbaMatches(e.Rgba, refSim.Mip0, rec.SourcePalette));
        Check("wet-index0-present", refSim.Mip0.Contains((byte)0) && refSim.Mip0.Any(m => m != 0));
        bool ok = true;
        for (int k = 2; k <= 30 && e != null; k++)
        {
            double now = k / 60.0;
            pt.MarkWetUsed("Lev.Water.Pool", k);
            pt.TickAll(k, now);
            refSim.Tick((float)(now - (k - 1) / 60.0), ringB);
            if (!WetRgbaMatches(e.Rgba, refSim.Mip0, rec.SourcePalette)) { ok = false; break; }
        }
        // MaxFrameRate=0: a step on every drawn frame; 30 frames, 30 steps (WaterParity++ per step)
        Check("wet-rgba-per-tick", ok);
        Check("wet-step-per-frame", e != null && e.Water.WaterParity == 30 && e.Ticks == 30, $"parity={e?.Water.WaterParity}");
        Check("wet-case-insensitive", pt.Wet("lev.water.pool", 31, 31 / 60.0) == e);
    }

    static void WetGatingAndStops()
    {
        var rec = WetRec("Lev.Water.Pool");
        var pt = new ProcTextures(null, new[] { rec, WetRec("Other.Water.Pool"), WetRec("Lev.Water.Lake"),
                                                WetRec("Lev.Water.Gone", source: false), WetRec("Lev.Water.Big", w: 16, srcW: 32) }, Ring());
        var e = pt.Wet("Lev.Water.Pool", 1, 1 / 60.0);
        // creation + TickAll in the same frame is one tick (Time == LastUpdateTime)
        pt.TickAll(1, 1 / 60.0);
        Check("wet-no-double-tick-on-create", e.Ticks == 1, $"ticks={e.Ticks}");
        // new frame but the viewport clock did not move (Time == LastUpdateTime): Update is not called
        var still = new ProcTextures(null, new[] { WetRec("Lev.Water.Still") }, Ring());
        var es = still.Wet("Lev.Water.Still", 1, 0.5);
        still.MarkWetUsed("Lev.Water.Still", 2); still.TickAll(2, 0.5);
        still.MarkWetUsed("Lev.Water.Still", 3); still.TickAll(3, 0.6);
        Check("wet-same-time-new-frame-no-tick", es.Ticks == 2, $"ticks={es.Ticks}");
        // marked up to frame 10; frame 11 still ticks (previous frame), 12..19 do not
        for (int k = 2; k <= 19; k++)
        {
            if (k <= 10) pt.MarkWetUsed("Lev.Water.Pool", k);
            pt.TickAll(k, k / 60.0);
        }
        Check("wet-gating-used-frames", e.Ticks == 11, $"ticks={e.Ticks}");
        pt.MarkWetUsed("Lev.Water.Pool", 20); pt.TickAll(20, 20 / 60.0);
        Check("wet-gating-resume", e.Ticks == 12, $"ticks={e.Ticks}");
        // TickAll twice in a frame runs once. The second call comes 1.5 periods of the 60 Hz cadence later: the cadence
        // gate lets it through, only the frame guard holds it (otherwise a guard mutation would be indistinguishable).
        pt.MarkWetUsed("Lev.Water.Pool", 21); pt.TickAll(21, 21 / 60.0); pt.TickAll(21, 22.5 / 60.0);
        Check("wet-tickall-once-per-frame", e.Ticks == 13, $"ticks={e.Ticks}");

        Check("wet-carrier", pt.IsWetCarrier("Lev.Water.Gone") && !pt.IsWetCarrier("Lev.Water.None"));
        Check("wet-size", pt.WetSize("Lev.Water.Pool", out int ww, out int hh) && ww == 16 && hh == 16);
        Check("wet-leaf-unique", pt.WetCarrierByLeaf("Lake") == "Lev.Water.Lake");
        Check("wet-leaf-ambiguous-stop", pt.WetCarrierByLeaf("Pool") == null && pt.Problems.Any(p => p.Contains("leaf 'Pool'")));
        Check("wet-leaf-no-source-not-indexed", pt.WetCarrierByLeaf("Gone") == null);
        Check("wet-missing-source-stop", pt.Wet("Lev.Water.Gone", 22, 1.0) == null && pt.Problems.Any(p => p.Contains("genfluid.Water") && p.Contains("is missing")));
        Check("wet-big-source-stop", pt.Wet("Lev.Water.Big", 22, 1.0) == null && pt.Problems.Any(p => p.Contains("is larger")));
        int n = pt.Problems.Count; pt.Wet("Lev.Water.Gone", 23, 1.1);
        Check("wet-stop-reported-once", pt.Problems.Count == n);
    }

    static void RingUnseeded()
    {
        var pt = new ProcTextures(new[] { FireRec("P.G.F") }, new[] { WetRec("P.G.W") }, new FractalRandom());
        pt.Fire("P.G.F", 1, 0.1);
        Check("ring-unseeded-stop-fire", pt.Problems.Count(p => p.Contains("not seeded")) == 1);
        pt.Wet("P.G.W", 1, 0.1);
        Check("ring-unseeded-stop-once", pt.Problems.Count(p => p.Contains("not seeded")) == 1);
        var wetOnly = new ProcTextures(null, new[] { WetRec("P.G.W") }, new FractalRandom());
        wetOnly.Wet("P.G.W", 1, 0.1);
        Check("ring-unseeded-stop-wet", wetOnly.Problems.Count(p => p.Contains("not seeded")) == 1);
        var seeded = new ProcTextures(new[] { FireRec("P.G.F") }, null, Ring());
        seeded.Fire("P.G.F", 1, 0.1);
        Check("ring-seeded-no-stop", seeded.Problems.Count == 0);
    }

    static void DefaultRingShared()
    {
        // Without an explicit ring the manager takes FractalRandom.Shared, the ring shared by fire and water (reference
        // HP1FireTexture.cs:64, HP1WetTextures.cs:149/195). The test process is separate; it seeds here.
        if (!FractalRandom.Shared.Seeded) FractalRandom.Shared.Set(Ring().Ring, 0);
        var before = (byte[])FractalRandom.Shared.Ring.Clone(); int idx = FractalRandom.Shared.Index;
        var pt = new ProcTextures(new[] { FireRec("P.G.S") }, null);
        pt.Fire("P.G.S", 1, 0.1);
        Check("fire-default-ring-shared", FractalRandom.Shared.Index != idx || !FractalRandom.Shared.Ring.AsSpan().SequenceEqual(before));
    }

    static void VersionFlag()
    {
        // "Changed" flag: the version grows only when RGBA content differs; the renderer clears it with MarkUploaded.
        var rec = FireRec("P.G.V", maxFr: 10f, prime: 0);
        var pt = new ProcTextures(new[] { rec }, null, Ring());
        var e = pt.Fire("P.G.V", 1, 1.0);
        Check("version-new-is-changed", e.Changed && e.Version == 1);
        e.MarkUploaded();
        // MaxFrameRate=10: a frame 1/60 s later has no step, content unchanged
        pt.MarkFireUsed("P.G.V", 2); pt.TickAll(2, 1.0 + 1 / 60.0);
        Check("version-no-step-no-change", !e.Changed && e.Ticks == 2, $"ticks={e.Ticks} v={e.Version}");
        // after 0.2 s: a step; sparks change the mip
        pt.MarkFireUsed("P.G.V", 3); pt.TickAll(3, 1.2);
        Check("version-step-changes", e.Changed && e.Version == 2, $"v={e.Version}");
    }

    // D-35 (author decision): procedural texture steps run at a 60 Hz cadence at any draw rate.
    // One second of frames at 60/120/144 Hz and at 60 Hz with ±3 ms jitter gives ~60 fire steps, not one per frame.
    static void FireCadence60()
    {
        foreach (var (hz, jitter) in new[] { (60.0, 0.0), (60.0, 0.003), (120.0, 0.0), (144.0, 0.0) })
        {
            var pt = new ProcTextures(new[] { FireRec("P.G.C") }, null, Ring());
            var rnd = new Random(7);
            ProcTextures.Entry e = null;
            int start = 0;
            for (int f = 1; f <= (int)hz * 2; f++)
            {
                double t = f / hz + (jitter > 0 ? (rnd.NextDouble() * 2 - 1) * jitter : 0);
                e = pt.Fire("P.G.C", f, t);
                pt.TickAll(f, t);
                if (f == (int)hz) start = e.Ticks;   // second second: no warm-up
            }
            int steps = e.Ticks - start;
            Check($"fire-cadence-60-at-{hz:F0}hz{(jitter > 0 ? "-jitter" : "")}", steps >= 58 && steps <= 62, $"steps per second {steps}");
        }
    }
}
