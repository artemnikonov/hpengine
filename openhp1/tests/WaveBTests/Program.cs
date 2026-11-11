using System.Linq;
// Wave B fixes against shipped HP1 1.53, without game files.
// Input: the native registration in the frontend (UEGameEngineNatives.GetByIndex), as the VM uses it.
// Expectations follow the disasm of the shipped bodies. Red mutations: audit/verify_wave_b_redtest.sh.
using HP1.UEScript;
using Vector3 = HP1.UEScript.UEVector;

static class Program
{
    static int _fail, _pass;

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) _pass++; else { _fail++; Console.WriteLine($"FAIL {name}: {detail}"); }
    }

    static int Main()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "waveb_" + Environment.ProcessId);
        string sys = Path.Combine(tmp, "0"); // _systemDir = tmp; UEGalaxyConfig reads <systemDir>/0/Default.ini
        Directory.CreateDirectory(sys);
        string ini = Path.Combine(sys, "Default.ini");
        File.WriteAllText(ini, "[WinDrv.WindowsClient]\nWindowedViewportX=640\nWindowedViewportY=480\n"
            + "[Galaxy.GalaxyAudioSubsystem]\nUseSurround=False\nReverseStereo=False\nAmbientFactor=0.7\nDopplerSpeed=9000.0\nLatency=40\nOutputRate=22050Hz\n");
        try
        {
            var natives = new UEGameEngineNatives(1, new List<string>(), ini, null);
            FinishInterpolation(natives);
            HashCellIndex();
            DynamicLoadSoundMiss(natives, tmp);
            OrthoRotationFtol();
            ActorProcessEventGate();
            DestroyExitProperties();
            UniqueNameStemCut();
            SoundPeriodShippedBranches();
            TextureAnimWallClock();
            StructValueSemantics();
            TravelTextRoundTrip();
            DynamicLoadMissingPackage();
            DefaultIniLayouts(tmp);
            SetLanguageShipped(tmp);   // last: changes the process-global GLanguage
        }
        finally { Directory.Delete(tmp, true); }
        Console.WriteLine($"WaveBTests: passed {_pass}, failed {_fail}");
        return _fail == 0 ? 0 : 1;
    }

    // ── B3: AActor::ProcessEvent (Engine 0x10379a60): `test byte [Level+0x438], 2; je ret`; before
    //    Level.bBegunPlay an actor event does not even reach UObject::ProcessEvent (probe masks,
    //    function lookup). Observer: UELifecycle.EventProbe (the first action of the UObject gate).
    //    A non-actor (Console etc.) is not gated: UObject::ProcessEvent has no such check.
    static void ActorProcessEventGate()
    {
        HP1.Core.UEClassRegistry.Shared ??= new HP1.Core.UEClassRegistry();
        var actorCls = new UELib.Core.UClass { Name = new UELib.Core.UName("Actor") };
        var objCls = new UELib.Core.UClass { Name = new UELib.Core.UName("Console") };
        var interp = new UEInterpreter();
        var seen = new List<string>();
        var prev = UELifecycle.EventProbe;
        UELifecycle.EventProbe = (a, n, args) => seen.Add($"{a.GetProp("Name")}.{n}");
        try
        {
            foreach (bool begun in new[] { false, true })
            {
                var level = new UEObject();
                level.Props["bBegunPlay"] = begun;
                var actor = new UEObject { Class = actorCls };
                actor.Props["Name"] = new UEName("A"); actor.Props["Level"] = level;
                var console = new UEObject { Class = objCls };
                console.Props["Name"] = new UEName("C"); console.Props["Level"] = level;
                seen.Clear();
                UELifecycle.CallEvent(interp, actor, "Spawned");
                UELifecycle.CallEvent(interp, actor, "Trigger", null, null);
                UELifecycle.CallEvent(interp, console, "Tick", 0.1f);
                string want = begun ? "A.Spawned A.Trigger C.Tick" : "C.Tick";
                Check($"ProcessEvent bBegunPlay={begun}", string.Join(" ", seen) == want, string.Join(" ", seen));
            }
        }
        finally { UELifecycle.EventProbe = prev; }
    }

    // ── B4-obj: ULevel::DestroyActor -> ConditionalDestroy -> UObject::Destroy -> ExitProperties
    //    (Core 0x1015bdf0): the corpse's strings = "", array<> empty, string members of structs = ""; numbers,
    //    references and other struct members untouched. Input: UELevelRuntime.DestroyActor (native 279).
    static void DestroyExitProperties()
    {
        HP1.Core.UEClassRegistry.Shared ??= new HP1.Core.UEClassRegistry();
        T Named<T>(string n) where T : UELib.Core.UField, new() => new T { Name = new UELib.Core.UName(n) };
        void Chain(UELib.Core.UStruct owner, params UELib.Core.UField[] fields)
        {
            for (int i = 0; i + 1 < fields.Length; i++) fields[i].NextField = fields[i + 1];
            typeof(UELib.Core.UStruct).GetProperty("Children", System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!.SetValue(owner, fields[0]);
        }
        var st = Named<UELib.Core.UStruct>("Entry");
        var stT = Named<UELib.Core.UStrProperty>("T"); var stN = Named<UELib.Core.UIntProperty>("N");
        Chain(st, stT, stN);
        var cls = Named<UELib.Core.UClass>("ExitPropsActor");
        var pS = Named<UELib.Core.UStrProperty>("S");
        var pSA = Named<UELib.Core.UStrProperty>("SA"); pSA.ArrayDim = 2;
        var pA = Named<UELib.Core.UArrayProperty>("A");
        var pE = Named<UELib.Core.UStructProperty>("E"); pE.Struct = st;
        var pK = Named<UELib.Core.UIntProperty>("K");
        var pO = Named<UELib.Core.UObjectProperty>("O");
        Chain(cls, pS, pSA, pA, pE, pK, pO);
        var rt = new UELevelRuntime();
        var interp = new UEInterpreter();
        var other = new UEObject();
        var a = new UEObject { Class = cls };
        a.Props["Name"] = new UEName("Corpse"); a.Props["Role"] = (byte)4;
        a.Props["S"] = "hello"; a.Props["SA"] = new List<object> { "x", "y" };
        a.Props["A"] = new List<object> { 1, 2, 3 };
        var bag = new UEObject(); bag.Props["T"] = "member"; bag.Props["N"] = 7; a.Props["E"] = bag;
        a.Props["K"] = 42; a.Props["O"] = other;
        rt.Actors.Add(a);
        Check("Destroy: destroyed", rt.DestroyActor(interp, a) && a.Destroyed);
        Check("ExitProperties: string = \"\"", Equals(a.GetProp("S"), ""), $"{a.GetProp("S")}");
        Check("ExitProperties: string[2] = \"\"", a.GetProp("SA") is List<object> sa && sa.Count == 2 && Equals(sa[0], "") && Equals(sa[1], ""));
        Check("ExitProperties: array<> empty", a.GetProp("A") is List<object> al && al.Count == 0);
        Check("ExitProperties: struct.string = \"\"", Equals(bag.GetProp("T"), ""), $"{bag.GetProp("T")}");
        Check("ExitProperties: struct.int untouched", Equals(bag.GetProp("N"), 7));
        Check("ExitProperties: int untouched", Equals(a.GetProp("K"), 42));
        Check("ExitProperties: reference untouched", ReferenceEquals(a.GetProp("O"), other));
    }

    // ── B4-obj: MakeUniqueObjectName (Core 0x101614a0): appStrncpy(dst, base, 0x3f - len(number)),
    //    0 at [n-1] (0x101262da); the stem is cut to 0x3e - len(number). Input: the internal rule
    //    UELevelRuntime.MintObjectName via reflection (the same function is called by spawn and `new`).
    static void UniqueNameStemCut()
    {
        var mint = typeof(UELevelRuntime).GetMethod("MintObjectName",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Check("MintObjectName found", mint is not null);
        string cls = new string('A', 70);
        string got = (string)mint.Invoke(null, new object[] { cls, (Func<int>)(() => 7), new HashSet<string>() });
        Check("MakeUniqueObjectName: base 0x3e-1 + \"7\"", got == new string('A', 0x3d) + "7", $"{got.Length}: {got}");
        string shortName = (string)mint.Invoke(null, new object[] { "Harry", (Func<int>)(() => 12), new HashSet<string>() });
        Check("MakeUniqueObjectName: a short name whole", shortName == "Harry12", shortName);
    }

    // ── B6: FSoundData::GetPeriod (0x1037ec80) + FWaveModInfo::ReadWaveInfo (Engine.dll.c:143355-143430)
    //    and FUN_1044acf0 (84179-84260). Expectations: the shipped formula data·8/(ch·rate·bits) (one division,
    //    the same X87Period reconciled earlier), computed here from the header.
    static byte[] Wav(bool riff, bool wave, ushort encoding, ushort ch, int rate, ushort bits, int dataDeclared, int dataPresent)
    {
        var b = new List<byte>();
        b.AddRange(System.Text.Encoding.ASCII.GetBytes(riff ? "RIFF" : "JUNK"));
        b.AddRange(BitConverter.GetBytes(0));
        b.AddRange(System.Text.Encoding.ASCII.GetBytes(wave ? "WAVE" : "WAV_"));
        b.AddRange(System.Text.Encoding.ASCII.GetBytes("fmt ")); b.AddRange(BitConverter.GetBytes(16));
        b.AddRange(BitConverter.GetBytes(encoding)); b.AddRange(BitConverter.GetBytes(ch));
        b.AddRange(BitConverter.GetBytes(rate)); b.AddRange(BitConverter.GetBytes(rate * ch * bits / 8));
        b.AddRange(BitConverter.GetBytes((ushort)(ch * bits / 8))); b.AddRange(BitConverter.GetBytes(bits));
        b.AddRange(System.Text.Encoding.ASCII.GetBytes("data")); b.AddRange(BitConverter.GetBytes(dataDeclared));
        b.AddRange(new byte[dataPresent]);
        return b.ToArray();
    }

    static void SoundPeriodShippedBranches()
    {
        float Want(int dataBytes, int den) => (float)((double)dataBytes * 8.0 / den);
        string Err(Func<float> f, out float v) { try { v = f(); return null; } catch (Exception e) { v = float.NaN; return e.GetType().Name; } }
        void Case(string name, byte[] d, string format, float want)
        {
            string err = Err(() => UESoundCatalog.SoundPeriod(d, format, "Test." + name, 0), out float got);
            Check($"GetPeriod {name}", err is null && Math.Abs(got - want) <= Math.Abs(want) * 1e-6f, err ?? $"{got:R} != {want:R}");
        }
        Case("PCM", Wav(true, true, 1, 1, 22050, 16, 44100, 44100), "WAV", Want(44100, 22050 * 16));
        Case("encoding 2 (ADPCM) — no refusal", Wav(true, true, 2, 1, 22050, 4, 1000, 1000), "WAV", Want(1000, 22050 * 4));
        Case("no RIFF, with WAVE", Wav(false, true, 1, 2, 11025, 8, 500, 500), "WAV", Want(500, 2 * 11025 * 8));
        Case("no WAVE -> 0", Wav(true, false, 1, 1, 22050, 16, 100, 100), "WAV", 0f);
        Case("format not mp2 -> WAV branch", Wav(true, true, 1, 1, 22050, 16, 2205, 2205), "ogg", Want(2205, 22050 * 16));
        Case("truncated data — declared size", Wav(true, true, 1, 1, 22050, 16, 88200, 10), "WAV", Want(88200, 22050 * 16));
        // mp2, free bitrate: MPEG-1 layer II, 44100, mono (mode 3), bitrateIndex 0.
        byte[] mp2 = { 0xFF, 0xFD, 0x00, 0xC0 };
        string e2 = Err(() => UESoundCatalog.SoundPeriod(mp2, "mp2", "Test.free", 4410), out float p2);
        Check("GetPeriod mp2 bitrateIndex 0", e2 is null && Math.Abs(p2 - Want(4410, 44100 * 16)) <= 1e-6f, e2 ?? $"{p2:R}");
    }

    // ── B7: UTexture::Update (Engine 0x10421070): dt = Time − LastUpdateTime on the viewport WALL clock and
    //    each texture's own LastUpdateTime; Update only when Time != LastUpdateTime. Chain A->B->A,
    //    Max=10, Min=1 (1/Max = 0.1, 1/Min = 1). Entries go into the path dictionary via reflection (a seam
    //    without production API); input: the same BeginFrame/CurrentFrame that SdlRenderer.AnimateWorld calls.
    static void TextureAnimWallClock()
    {
        var anim = new UETextureAnim(Path.GetTempPath());
        var byPath = (System.Collections.IDictionary)typeof(UETextureAnim).GetField("_byPath",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(anim);
        byPath["T.A"] = new UETextureAnim.Entry { Path = "T.A", Next = "T.B", Min = 1f, Max = 10f };
        byPath["T.B"] = new UETextureAnim.Entry { Path = "T.B", Next = "T.A", Min = 1f, Max = 10f };
        // First draw: shipped LastUpdateTime is 0 while Time is "since machine start"; a step and accumulator = 1/Min.
        anim.BeginFrame(100.0);
        Check("AnimNext: first draw — a step", anim.CurrentFrame("T.A") == "T.B");
        // Engine frames advance, the wall clock stands still (pause / the same Time): no step.
        anim.BeginFrame(100.0);
        Check("AnimNext: Time == LastUpdateTime — no Update", anim.CurrentFrame("T.A") == "T.B");
        // The texture was not drawn for 5 s: the next draw makes exactly ONE step (dt = time since its last draw).
        anim.BeginFrame(105.0);
        Check("AnimNext: a long skip — one step", anim.CurrentFrame("T.A") == "T.A");
        // The accumulator after a step is cut to 1/Min = 1 (>= 1/Max): Tick by hand: 105.05: 1.05 -> step, 0.05;
        // 105.15: 0.15 -> step, 0 (< 1/Min); 105.20: 0.05 < 0.1, no step; 105.30: 0.15 -> step.
        anim.BeginFrame(105.05);
        Check("AnimNext: 105.05 — step (accumulator 1.05)", anim.CurrentFrame("T.A") == "T.B");
        anim.BeginFrame(105.15);
        Check("AnimNext: 105.15 — step (0.15)", anim.CurrentFrame("T.A") == "T.A");
        anim.BeginFrame(105.20);
        Check("AnimNext: 105.20 — no step (0.05)", anim.CurrentFrame("T.A") == "T.A");
        anim.BeginFrame(105.30);
        Check("AnimNext: 105.30 — step (0.15)", anim.CurrentFrame("T.A") == "T.B");
        // D-35: a chain with MaxFrameRate=0 (a step per frame in shipped) on a 120 Hz monitor steps at 60 Hz.
        byPath["T.C"] = new UETextureAnim.Entry { Path = "T.C", Next = "T.D", Min = 0f, Max = 0f };
        byPath["T.D"] = new UETextureAnim.Entry { Path = "T.D", Next = "T.C", Min = 0f, Max = 0f };
        string prev = null; int changes = 0;
        for (int k = 1; k <= 120; k++)
        {
            anim.BeginFrame(200.0 + k / 120.0);
            string cur = anim.CurrentFrame("T.C");
            if (prev != null && cur != prev) changes++;
            prev = cur;
        }
        Check("AnimNext: Max=0 at 120 Hz — 60 Hz pace", changes >= 58 && changes <= 62, $"frame changes {changes}");
    }

    // ── B1: structs and arrays are values (UStructProperty/UArrayProperty::CopySingleValue,
    //    UStruct::StructCompare, UIntProperty::Identical). Input: UEValue.CopyValue / StructEquals / ValueEquals,
    //    used by Let, BindParams, EX_StructCmpEq/Ne and switch.
    static void StructValueSemantics()
    {
        UEObject S(params (string, object)[] kv) { var o = new UEObject { IsStruct = true }; foreach (var (k, v) in kv) o.Props[k] = v; return o; }
        // Copy: a static array member gets its own list, a nested struct its own object.
        var inner = S(("N", 1));
        var a = S(("Arr", new List<object> { 1, 2 }), ("In", inner));
        var c = (UEObject)UEValue.CopyValue(a);
        ((List<object>)c.GetProp("Arr"))[0] = 99;
        ((UEObject)c.GetProp("In")).Props["N"] = 7;
        Check("CopyValue: a member static array is not shared", Equals(((List<object>)a.GetProp("Arr"))[0], 1));
        Check("CopyValue: a nested struct is not shared", Equals(inner.GetProp("N"), 1));
        var arr = new List<object> { S(("N", 1)) };
        var arrCopy = (List<object>)UEValue.CopyValue(arr);
        ((UEObject)arrCopy[0]).Props["N"] = 5;
        Check("CopyValue: array<> of structs — own elements", Equals(((UEObject)arr[0]).GetProp("N"), 1));
        // Comparison: nested structs and arrays equal by content are equal; different ones are not.
        var x = S(("In", S(("N", 3))), ("Arr", new List<object> { 1, 2 }));
        var y = S(("In", S(("N", 3))), ("Arr", new List<object> { 1, 2 }));
        var z = S(("In", S(("N", 4))), ("Arr", new List<object> { 1, 2 }));
        var zArr = S(("In", S(("N", 3))), ("Arr", new List<object> { 1, 3 }));
        Check("StructEquals: nested struct by value", UEValue.StructEquals(x, y));
        Check("StructEquals: different nested — not equal", !UEValue.StructEquals(x, z));
        Check("StructEquals: different array element — not equal", !UEValue.StructEquals(x, zArr));
        // int vs int is exact: 16777216 and 16777217 coincide as float.
        Check("ValueEquals: int 2^24 and 2^24+1 differ", !UEValue.ValueEquals(16777216, 16777217));
        Check("StructEquals: int member 2^24+1", !UEValue.StructEquals(S(("I", 16777216)), S(("I", 16777217))));
        // execStringConst -> appFromAnsi: at most 1023 characters.
        Check("StringConst: 1500 characters -> 1023", UEValue.AnsiStringConst(new string('x', 1500)).Length == 1023);
        Check("StringConst: a short string as is", UEValue.AnsiStringConst("abc") == "abc");
        // switch: appMemcmp compares a vector bitwise (−0.0 != 0.0); a name folds case only for A..Z ('Ä' != 'ä').
        Check("switch: vector −0.0 and 0.0 differ", !UEValue.SwitchCaseMatches(new UEVector(-0f, 0f, 0f), new UEVector(0f, 0f, 0f)));
        Check("switch: equal vectors match", UEValue.SwitchCaseMatches(new UEVector(1f, 2f, 3f), new UEVector(1f, 2f, 3f)));
        Check("switch: name 'Idle'/'idle' match", UEValue.SwitchCaseMatches(new UEName("Idle"), new UEName("idle")));
        Check("switch: name 'Äb'/'äb' differ (folding only A..Z)", !UEValue.SwitchCaseMatches(new UEName("Äb"), new UEName("äb")));
    }

    // ── B2: travel is carried as TEXT (ExportTextItem "%.7g" -> appAtof; name -> ReadToken).
    static void TravelTextRoundTrip()
    {
        var m = typeof(UELevelRuntime).GetMethod("TravelText",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Check("TravelText found", m is not null);
        var fp = new UELib.Core.UFloatProperty { Name = new UELib.Core.UName("F") };
        var np = new UELib.Core.UNameProperty { Name = new UELib.Core.UName("N") };
        var ip = new UELib.Core.UIntProperty { Name = new UELib.Core.UName("I") };
        float f = BitConverter.Int32BitsToSingle(0x3F800001);   // 1 + 1 ulp = 1.00000012
        object fb = m.Invoke(null, new object[] { fp, f });
        Check("travel float: %.7g -> 1.0f (1 ulp lost)", fb is float ff && ff == 1f, $"{fb}");
        object nb = m.Invoke(null, new object[] { np, new UEName("A.B") });
        Check("travel name: ReadToken cuts at '.'", nb is UEName un && un.Value == "A", $"{nb}");
        object ib = m.Invoke(null, new object[] { ip, 123456789 });
        Check("travel int: unchanged", Equals(ib, 123456789));
    }

    // ── B1-b: execDynamicLoadObject with the package neither in the registry nor on disk -> StaticLoadObject finds no
    //    file -> None.
    static void DynamicLoadMissingPackage()
    {
        HP1.Core.UEClassRegistry.Shared ??= new HP1.Core.UEClassRegistry();
        string warned = null;
        object r = UENativesCore.ResolveDynamicLoad("NoSuchPackage.SomeTex", "Texture", pkg => warned = pkg);
        Check("DynamicLoadObject: no package -> None", r is null, $"{r}");
        Check("DynamicLoadObject: no package -> warning", warned == "NoSuchPackage", $"{warned}");
    }

    // ── B9-math: FCoords::OrthoRotation (Core 0x1014ed10): angle = _ftol(atan2 · C), MSVCRT _ftol is
    //    truncation toward zero, C = double 0x40c45f306446f9b4 (fmul qword [0x1017fa50]). Both port copies:
    //    UELevelRuntime.OrthoRotation(BoneWorld) (skeleton) and the private OrthoRotation(BrushCoords)
    //    (Warp/UnWarp and PHYS_Interpolating). Frames are built from float axes; cases where the rules
    //    differ are found by search over the exact atan2 of those same float axes.
    static void OrthoRotationFtol()
    {
        double C = BitConverter.Int64BitsToDouble(0x40c45f306446f9b4);
        double Cwrong = 32768.0 / Math.PI;
        var warp = typeof(UELevelRuntime).GetMethod("OrthoRotation",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
            new[] { typeof(BrushCoords).MakeByRefType() });
        Check("OrthoRotation(BrushCoords) found", warp is not null);
        int roundCases = 0, constCases = 0, negCases = 0;
        for (int i = 1; i < 200000 && (roundCases < 3 || constCases < 3 || negCases < 3); i++)
        {
            double t = (i % 2 == 0 ? 1 : -1) * i * 1.37e-5;
            float cx = (float)Math.Cos(t), sy = (float)Math.Sin(t);
            double a = Math.Atan2(sy, cx);
            double v = a * C;
            bool byRound = Math.Round(v, MidpointRounding.ToEven) != Math.Truncate(v);
            bool byConst = Math.Truncate(v) != Math.Truncate(a * Cwrong);
            bool neg = v < 0 && byRound;
            if (!(byRound && roundCases < 3) && !(byConst && constCases < 3) && !(neg && negCases < 3)) continue;
            if (byRound) roundCases++; if (byConst) constCases++; if (neg) negCases++;
            int want = (int)Math.Truncate(v);
            var bw = new UELevelRuntime.BoneWorld
            {
                X = new Vector3(cx, sy, 0f), Y = new Vector3(-sy, cx, 0f), Z = new Vector3(0f, 0f, 1f),
            };
            UERotator r1 = UELevelRuntime.OrthoRotation(in bw);
            Check($"OrthoRotation(skeleton) yaw t={t:R}", r1.Yaw == want, $"{r1.Yaw} != {want}");
            var bc = BrushCoords.Identity;
            bc.X = bw.X; bc.Y = bw.Y; bc.Z = bw.Z;
            var r2 = (UERotator)warp.Invoke(null, new object[] { bc });
            Check($"OrthoRotation(Warp) yaw t={t:R}", r2.Yaw == want, $"{r2.Yaw} != {want}");
        }
        Check("the sweep found rounding/constant/negative cases", roundCases >= 3 && constCases >= 3 && negCases >= 3,
            $"round={roundCases} const={constCases} neg={negCases}");
    }

    // ── B4-load: UObject::execDynamicLoadObject (Core.dll.c:63931-63985) -> StaticLoadObject with
    //    LOAD_NoWarn | (MayFail ? LOAD_Quiet : 0); SafeLoadError (14713-14763) without -STRICT does not throw
    //    and does not call GError; the native returns None for any MayFail.
    static void DynamicLoadSoundMiss(UEGameEngineNatives natives, string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "Sounds")); // empty directory: no export
        UESoundCatalog.Shared.SetSource(root);
        UENativeHandler h = natives.GetByName("DynamicLoadObject");
        var sound = new UELib.Core.UClass { Name = new UELib.Core.UName("Sound") };
        foreach (bool mayFail in new[] { false, true })
        {
            object r = null; string err = null;
            try { r = h(null, new UEFrame(), new object[] { "AllDialog.NoSuchLine", sound, mayFail }); }
            catch (Exception e) { err = $"{e.GetType().Name}: {e.Message}"; }
            Check($"DynamicLoadObject(Sound, MayFail={mayFail}) miss: no exception", err is null, err ?? "");
            Check($"DynamicLoadObject(Sound, MayFail={mayFail}) miss: None", r is null, $"{r}");
        }
    }

    // ── B8-coll: FCollisionHash::GetHashIndices (disasm 0x10364030-0x103640c7):
    //    fld v; fadd 32768f; fmul 1/256f; fstp dword; fistp (round half to even); clamp 0..0x3fff.
    //    Expectations by hand: (128.0001f+32768)/256 = 128.50000039 -> float 128.5 -> 128.
    static void HashCellIndex()
    {
        var cases = new (float V, int Want)[]
        {
            (128.0001f, 128),   // float rounding to 128.5, then to even
            (383.9999f, 130),   // 129.4999996 -> float 129.5 -> 130
            (-128.0001f, 128),  // 127.4999996 -> float 127.5 -> 128
            (128.002f, 129),    // control: above the float rounding band
            (-40000f, 0),       // clamp below
            (4300000f, 0x3fff), // clamp above
        };
        foreach (var (v, want) in cases)
        {
            int got = CollisionHashReplica.CellIndex(v);
            Check($"CellIndex({v:R})", got == want, $"{got} != {want}");
        }
    }

    // ── B8-phys: AActor::execFinishInterpolation (disasm 0x104083b0-0x104083c5):
    //    LatentAction = 302 unconditionally, without reading bInterpolating; the latent is cleared
    //    only by the poll 0x104084c0 on the next ProcessState.
    static void FinishInterpolation(UEGameEngineNatives natives)
    {
        UENativeHandler h = natives.GetByIndex(301);
        foreach (bool interpolating in new[] { false, true })
        {
            var a = new UEObject();
            a.Props["bInterpolating"] = interpolating;
            var frame = new UEFrame { Object = a };
            object r = h(null, frame, Array.Empty<object>());
            Check($"FinishInterpolation bInterpolating={interpolating}: latent 302",
                frame.LatentState == LatentRunState.FinishInterpolation, $"{frame.LatentState}");
            Check($"FinishInterpolation bInterpolating={interpolating}: result", r is null);
            Check($"FinishInterpolation bInterpolating={interpolating}: property untouched",
                Equals(a.GetProp("bInterpolating"), interpolating));
        }
    }

    // ── D-39: UObject::SetLanguage (Core 0x10164d60, disasm): stricmp, GLanguage, five LocalizeGeneral words,
    //    LanguageChange on all objects. Caller: appInit BEFORE StaticInit (no objects). Port: the language
    //    changes; a Core.<language> [General] word that differs beyond case is a STOP (the port keeps literals).
    static void SetLanguageShipped(string tmp)
    {
        string sys = Path.Combine(tmp, "lang");
        Directory.CreateDirectory(sys);
        byte[] U16(string t) => new byte[] { 0xff, 0xfe }.Concat(System.Text.Encoding.Unicode.GetBytes(t)).ToArray();
        File.WriteAllText(Path.Combine(sys, "Core.int"), "[General]\r\nTrue=True\r\nFalse=False\r\nNone=None\r\nYes=Yes\r\nNo=No\r\n");
        File.WriteAllBytes(Path.Combine(sys, "Core.rus"), U16("[General]\r\nTrue=TRUE\r\nFalse=FALSE\r\nNone=None\r\nYes=Yes\r\nNo=No\r\n"));
        File.WriteAllText(Path.Combine(sys, "Core.spa"), "[General]\r\nTrue=Verdadero\r\n");
        File.WriteAllBytes(Path.Combine(sys, "LangTest.rus"), U16("[S]\r\nK=Привет\r\n"));   // non-Latin test input
        File.WriteAllText(Path.Combine(sys, "LangTest.int"), "[S]\r\nK=Hello\r\nOnlyInt=Fallback\r\n");
        string prev = UELocalization.SystemDir;
        UELocalization.SystemDir = sys;
        try
        {
            Check("SetLanguage: no packages (as in appInit before StaticInit)",
                HP1.Core.UEClassRegistry.Shared is not { Packages.Count: > 0 });
            bool stop = false;
            try { UELocalization.SetLanguage("spa"); } catch (NotSupportedException) { stop = true; }
            Check("SetLanguage spa: True=Verdadero — STOP, the language did not change", stop && UELocalization.Language == "int");
            UELocalization.SetLanguage("INT");
            Check("SetLanguage INT: stricmp — no change", UELocalization.Language == "int");
            string why = "";
            try { UELocalization.SetLanguage("rus"); } catch (Exception e) { why = e.Message; }
            Check("SetLanguage rus: GLanguage = rus", UELocalization.Language == "rus", why == "" ? UELocalization.Language : why);
            Check("Localize rus: .rus in UTF-16 with BOM", UELocalization.Localize("S", "K", "LangTest") == "Привет");   // non-Latin test input
            Check("Localize rus: .rus miss — fallback to .int", UELocalization.Localize("S", "OnlyInt", "LangTest") == "Fallback");
        }
        finally { UELocalization.SystemDir = prev; }
    }

    // ── D-39: the system ini is `System/Default.ini` of the installed game (shipped: HP.ini <- Default.ini in System);
    //    `System/0/Default.ini` exists only in the unpacked US copy. Both layouts yield the System directory.
    static void DefaultIniLayouts(string tmp)
    {
        string sys = Path.Combine(tmp, "layout");
        Directory.CreateDirectory(Path.Combine(sys, "0"));
        File.WriteAllText(Path.Combine(sys, "0", "Default.ini"), "[x]\n");
        string extracted = UELevelRuntime.DefaultIniPath(sys);
        Check("Default.ini: only System/0 — it is taken", extracted == Path.Combine(sys, "0", "Default.ini"), extracted);
        File.WriteAllText(Path.Combine(sys, "Default.ini"), "[x]\n");
        string installed = UELevelRuntime.DefaultIniPath(sys);
        Check("Default.ini: System/Default.ini exists — it comes first", installed == Path.Combine(sys, "Default.ini"), installed);
        Check("SystemDirOfDefaultIni: both layouts -> System",
            UELevelRuntime.SystemDirOfDefaultIni(installed) == Path.GetFullPath(sys)
            && UELevelRuntime.SystemDirOfDefaultIni(extracted) == Path.GetFullPath(sys));
    }
}
