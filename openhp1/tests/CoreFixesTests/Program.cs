// Core fixes against shipped HP1 1.53, no game files needed.
// Input is the same function production calls: the UPolys reader on a synthetic package, the
// Localize native from RegisterLocalizationOps -> GetByName, FinishAnim/HasAnim — the bodies the
// frontend registers (Pair(261/263, Runtime.*Native)), Destroy — UELevelRuntime.DestroyActor
// (behind native 279), mode-3 mover pose — MoverMode3.MakePose/WorldPoint/WorldVector.
// Expectations are computed here from the shipped bodies, independently of the port. Red mutations — redtest.sh.
using HP1.UEScript;

sealed class LocNatives : UENativesCore
{
    public LocNatives(string saveDir) : base(1) { RegisterLocalizationOps(() => saveDir); }
}

static class Program
{
    static int _fail, _pass;

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) _pass++; else { _fail++; Console.WriteLine($"FAIL {name}: {detail}"); }
    }

    static int Main()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "corefixes_" + Environment.ProcessId);
        Directory.CreateDirectory(tmp);
        try
        {
            Run("1 FPoly.PolyFlags", () => PolyFlagsMask(tmp));
            Run("2 Localize", () => LocalizeTruncation(tmp));
            Run("3 MoverMode3", MoverPose);
            Run("4 FinishAnim(bone)", FinishAnimBone);
            Run("5 HasAnim", HasAnimNoMesh);
            Run("6 Destroy(role)", DestroyRole);
            Run("7 install path case", () => InstallPathCase(tmp));
        }
        finally { Directory.Delete(tmp, true); }
        Console.WriteLine($"CoreFixesTests: passed {_pass}, failed {_fail}");
        return _fail == 0 ? 0 : 1;
    }

    static void Run(string name, Action body)
    {
        int before = _fail;
        try { body(); }
        catch (Exception e) { _fail++; Console.WriteLine($"FAIL {name}: exception {e.GetType().Name}: {e.Message}"); }
        Console.WriteLine($"{(before == _fail ? "ok  " : "FAIL")} {name}");
    }

    // ── 1. operator<<(FArchive&, FPoly&) on load: PolyFlags &= 0xefffffff
    //    (Engine.dll.c:115984-115986). A synthetic v76 package with one UPolys export,
    //    read by the production reader HeadlessBspModel.ReadPolys (the one called by
    //    MoverGeometry/the brush tracker/--probe-moverflags).
    static void Ci(List<byte> b, int v)
    {
        uint a = (uint)Math.Abs(v);
        b.Add((byte)((v < 0 ? 0x80u : 0u) | (a & 0x3fu) | (a >= 0x40 ? 0x40u : 0u)));
        a >>= 6;
        while (a != 0) { byte nb = (byte)(a & 0x7f); a >>= 7; if (a != 0) nb |= 0x80; b.Add(nb); }
    }
    static void I32(List<byte> b, int v) => b.AddRange(BitConverter.GetBytes(v));
    static void F32(List<byte> b, float v) => b.AddRange(BitConverter.GetBytes(v));

    static void PolyFlagsMask(string tmp)
    {
        int[] flagsIn = { 0x10000001, 0x00000040, unchecked((int)0xFFFFFFFF), 0x10000000 };
        int[] flagsWant = { 0x00000001, 0x00000040, unchecked((int)0xEFFFFFFF), 0x00000000 };
        string[] names = { "None", "Core", "Engine", "Package", "Class", "Polys", "TestPolys" };
        var body = new List<byte>();
        Ci(body, 0);                                   // properties: None
        I32(body, flagsIn.Length); I32(body, flagsIn.Length);   // Num, Max
        for (int k = 0; k < flagsIn.Length; k++)
        {
            Ci(body, 3);                               // NumVertices
            for (int v = 0; v < 4 + 3; v++) { F32(body, k + 0.5f); F32(body, v); F32(body, -v); }
            I32(body, flagsIn[k]);
            Ci(body, 0); Ci(body, 0); Ci(body, 0);     // Actor, Texture, ItemName (None)
            Ci(body, k); Ci(body, 100 + k);            // iLink, iBrushPoly
            body.AddRange(BitConverter.GetBytes((short)(7 * k + 1)));
            body.AddRange(BitConverter.GetBytes((short)(-3 * k - 2)));
        }
        var pkg = new List<byte>();
        I32(pkg, unchecked((int)0x9E2A83C1));
        pkg.AddRange(BitConverter.GetBytes((ushort)76)); pkg.AddRange(BitConverter.GetBytes((ushort)0));
        I32(pkg, 1);                                   // PackageFlags
        int hdr = pkg.Count; for (int i = 0; i < 6; i++) I32(pkg, 0);
        pkg.AddRange(new byte[16]); I32(pkg, 1); I32(pkg, 1); I32(pkg, names.Length);   // GUID, generations
        int nameOff = pkg.Count;
        foreach (string n in names)
        { Ci(pkg, n.Length + 1); pkg.AddRange(System.Text.Encoding.ASCII.GetBytes(n)); pkg.Add(0); I32(pkg, 0); }
        int impOff = pkg.Count;
        Ci(pkg, 1); Ci(pkg, 3); I32(pkg, 0); Ci(pkg, 2);    // Core.Package Engine
        Ci(pkg, 1); Ci(pkg, 4); I32(pkg, -1); Ci(pkg, 5);   // Core.Class Engine.Polys
        int bodyOff = pkg.Count; pkg.AddRange(body);
        int expOff = pkg.Count;
        Ci(pkg, -2); Ci(pkg, 0); I32(pkg, 0); Ci(pkg, 6); I32(pkg, 0x00070004); Ci(pkg, body.Count); Ci(pkg, bodyOff);
        byte[] arr = pkg.ToArray();
        int[] h = { names.Length, nameOff, 1, expOff, 2, impOff };
        for (int i = 0; i < h.Length; i++) BitConverter.GetBytes(h[i]).CopyTo(arr, hdr + 4 * i);
        string path = Path.Combine(tmp, "CoreFixesPolys.unr");
        File.WriteAllBytes(path, arr);

        List<HeadlessBspModel.PolyRecord> polys = HeadlessBspModel.ReadPolys(path, 1);
        Check("polys.count", polys.Count == flagsIn.Length, $"{polys.Count}");
        for (int k = 0; k < polys.Count; k++)
        {
            var p = polys[k];
            Check($"polys[{k}].PolyFlags 0x{flagsIn[k]:x8}", p.PolyFlags == flagsWant[k],
                $"expected 0x{flagsWant[k]:x8}, got 0x{p.PolyFlags:x8}");
            // The rest of the record is intact (the mask does not shift parsing).
            Check($"polys[{k}].fields", p.Vertices.Length == 3 && p.Base.x == k + 0.5f && p.Vertices[2].y == 6f
                && p.iLink == k && p.iBrushPoly == 100 + k && p.PanU == 7 * k + 1 && p.PanV == -3 * k - 2
                && p.Texture is null && p.ItemName == "None",
                $"base={p.Base.x} iLink={p.iLink} iBrushPoly={p.iBrushPoly} pan={p.PanU},{p.PanV}");
        }
    }

    // ── 2. Localize: GetString copies the match with appStrncpy(Value, found, 0x400)
    //    (Engine.dll.c:277541; Size 0x400 — Core.dll.c:11186; appStrncpy Core.dll.c:42661:
    //    wcsncpy + Dest[Max-1] = 0) -> at most 1023 characters.
    static void LocalizeTruncation(string tmp)
    {
        string dir = Path.Combine(tmp, "System");
        Directory.CreateDirectory(dir);
        string a1500 = new string('a', 1500), b1023 = new string('b', 1023), c1024 = new string('c', 1024);
        string q1100 = new string('q', 1100);
        File.WriteAllText(Path.Combine(dir, "T.int"),
            "[S]\r\nLong=" + a1500 + "\r\nExact=" + b1023 + "\r\nOver=" + c1024 + "\r\nQuoted=\"" + q1100
            + "\"\r\nShort=hi\r\n", System.Text.Encoding.Latin1);
        UELocalization.SystemDir = dir;   // static cache: Localize has not been called in this process yet
        var n = new LocNatives(tmp);
        UENativeHandler loc = n.GetByName("Localize") ?? throw new InvalidOperationException("Localize not registered");
        string L(string key) => (string)loc(null, new UEFrame(), new object[] { "S", key, "T" });
        // Expectation: the first min(len, 1023) characters of the value.
        void Expect(string key, string value)
        {
            string want = value.Length > 1023 ? value.Substring(0, 1023) : value;
            string got = L(key);
            Check($"Localize S.{key} (length {value.Length})", got == want, $"expected length {want.Length}, got {got?.Length}");
        }
        Expect("Long", a1500);
        Expect("Exact", b1023);
        Expect("Over", c1024);
        Expect("Quoted", q1100);   // quotes removed by parsing, then truncation
        Expect("Short", "hi");
        // A miss gives the marker (truncation applies only to a hit).
        string miss = L("Missing");
        Check("Localize miss", miss == "<?int?T.S.Missing?>", miss);
    }

    // ── 3. ABrush::ToWorld (Engine.dll.c:60413): Origin −= Location; *= PostScale; *= Rotation;
    //    *= MainScale (+ quantised shear); Origin += PrePivot; point = TransformPointBy.
    //    For a brush this is world = PostScale·(R·(MainScale·(p − PrePivot))) + Location.
    static void MoverPose()
    {
        var L = new UEVector(100f, -50f, 25f);
        var P = new UEVector(8f, 4f, -2f);
        var S = new UEVector(2f, 0.5f, 4f);
        var Q = new UEVector(2f, 1f, 0.5f);
        var v = new UEVector(10f, 20f, 30f);
        // No rotation: everything is a power of two, the expectation is exact.
        var pose = MoverMode3.MakePose(L, new UERotator(0, 0, 0), P, S, 0f, Q, 0f);
        var want = new UEVector(Q.x * S.x * (v.x - P.x) + L.x, Q.y * S.y * (v.y - P.y) + L.y, Q.z * S.z * (v.z - P.z) + L.z);
        var got = MoverMode3.WorldPoint(in pose, v);
        Check("WorldPoint (PostScale, MainScale, PrePivot)", got.x == want.x && got.y == want.y && got.z == want.z, $"expected {want}, got {got}");
        var dv = new UEVector(1f, 2f, 3f);
        var wantV = new UEVector(Q.x * S.x * dv.x, Q.y * S.y * dv.y, Q.z * S.z * dv.z);
        var gotV = MoverMode3.WorldVector(in pose, dv);
        Check("WorldVector (PostScale, MainScale)", gotV.x == wantV.x && gotV.y == wantV.y && gotV.z == wantV.z, $"expected {wantV}, got {gotV}");

        // Rotation yaw = 16384 (90°): X -> Y. PostScale is in world axes AFTER rotation, MainScale before.
        // cos/sin from the FGlobalMath table: sin(i·step) in float, step = 0.0003834952f (Core.dll _DAT_1017fa38).
        float step = 0.0003834952076431364f;
        float s90 = (float)Math.Sin(4096 * (double)step), c90 = (float)Math.Sin(8192 * (double)step);
        var S2 = new UEVector(2f, 1f, 1f); var Q2 = new UEVector(1f, 3f, 1f);
        var pose2 = MoverMode3.MakePose(L, new UERotator(0, 16384, 0), P, S2, 0f, Q2, 0f);
        var v2 = new UEVector(P.x + 1f, P.y + 2f, P.z + 5f);
        double lx = S2.x * 1.0, ly = S2.y * 2.0, lz = S2.z * 5.0;              // MainScale·(p − PrePivot)
        double rx = c90 * lx - s90 * ly, ry = s90 * lx + c90 * ly, rz = lz;    // yaw: X=(c,s,0), Y=(−s,c,0)
        var want2 = new UEVector((float)(Q2.x * rx + L.x), (float)(Q2.y * ry + L.y), (float)(Q2.z * rz + L.z));
        var got2 = MoverMode3.WorldPoint(in pose2, v2);
        bool near = Math.Abs(got2.x - want2.x) < 1e-3 && Math.Abs(got2.y - want2.y) < 1e-3 && Math.Abs(got2.z - want2.z) < 1e-3;
        Check("WorldPoint yaw 90° (PostScale after rotation)", near, $"expected {want2}, got {got2}");

        // Shear: quantum |s| ≤ 0.05 -> 0 (same result), otherwise the shear matrix is not ported -> loud refusal.
        var pose3 = MoverMode3.MakePose(L, new UERotator(0, 0, 0), P, S, 0.04f, Q, -0.05f);
        var got3 = MoverMode3.WorldPoint(in pose3, v);
        Check("offset 0.04/−0.05 quantises to 0", got3.x == want.x && got3.y == want.y && got3.z == want.z, $"{got3}");
        foreach ((float ms, float ps) in new[] { (0.3f, 0f), (0f, 0.3f), (-0.7f, 0f) })
        {
            bool loud = false;
            try { MoverMode3.MakePose(L, new UERotator(0, 0, 0), P, S, ms, Q, ps); }
            catch (NotSupportedException) { loud = true; }
            Check($"shift Main={ms} Post={ps} — loud refusal", loud, "no refusal — shift silently ignored");
        }
    }

    // ── 4. execFinishAnim (Engine.dll.c:110512-110570), bone argument:
    //    no mesh / mesh not skeletal / no channel with this bone -> silent return
    //    (no write of bAnimLoop/bAnimFinished, no LatentAction).
    static UEObject AnimActor(object mesh)
    {
        var a = new UEObject();
        a.Props["Name"] = new UEName("FixActor");
        a.Props["AnimSequence"] = new UEName("Walk");
        a.Props["AnimRate"] = 1f;
        a.Props["TweenRate"] = 0f;
        a.Props["AnimFrame"] = 0.25f;
        a.Props["AnimLast"] = 0.75f;
        a.Props["bAnimLoop"] = true;
        a.Props["bAnimFinished"] = true;
        if (mesh is not null) a.Props["Mesh"] = mesh;
        return a;
    }

    static void FinishAnimBone()
    {
        var rt = new UELevelRuntime();
        int lookups = 0;
        rt.BoneLookupHook = (m, bone) => { lookups++; return (5, 0); };
        var cases = new (string Name, object Mesh)[]
        {
            ("no mesh", null),
            ("vertex mesh", new UEResourceRef("LodMesh", "FixLod")),
            ("skeletal, no channel", new UEResourceRef("SkeletalMesh", "FixSkel")),
        };
        foreach (var (name, mesh) in cases)
        {
            UEObject a = AnimActor(mesh);
            var frame = new UEFrame { Object = a };
            object r = rt.FinishAnimNative(null, frame, new object[] { new UEName("Bip01 Head") });
            Check($"FinishAnim(bone) {name}: no wait", frame.LatentState == LatentRunState.Continue, $"{frame.LatentState}");
            Check($"FinishAnim(bone) {name}: bAnimLoop/bAnimFinished untouched",
                Equals(a.GetProp("bAnimLoop"), true) && Equals(a.GetProp("bAnimFinished"), true),
                $"bAnimLoop={a.GetProp("bAnimLoop")} bAnimFinished={a.GetProp("bAnimFinished")}");
            Check($"FinishAnim(bone) {name}: result", r is null);
        }
        Check("FinishAnim(bone) skeletal: BoneIndex asked once", lookups == 1, $"{lookups}");
        // Distinguishability control: the same actor WITHOUT a bone sets the wait and clears the loop (110563-110569).
        UEObject c = AnimActor(null);
        var cf = new UEFrame { Object = c };
        rt.FinishAnimNative(null, cf, Array.Empty<object>());
        Check("FinishAnim without a bone: wait (control)", cf.LatentState == LatentRunState.FinishAnim, $"{cf.LatentState}");
        Check("FinishAnim without a bone: bAnimLoop=false, bAnimFinished=false (control)",
            Equals(c.GetProp("bAnimLoop"), false) && Equals(c.GetProp("bAnimFinished"), false));
    }

    // ── 5. execHasAnim (Engine.dll.c:114338-114358): Mesh == NULL -> Stack.Logf("HasAnim: No mesh"),
    //    result 0. With a mesh — GetAnimSeq != NULL, no line.
    static void HasAnimNoMesh()
    {
        var rt = new UELevelRuntime();
        var warns = new List<string>();
        Action<string> saved = HP1.Core.UETrace.Warn;
        HP1.Core.UETrace.Warn = warns.Add;
        try
        {
            UEObject a = AnimActor(null);
            object r1 = rt.HasAnimNative(null, new UEFrame { Object = a }, new object[] { new UEName("Walk") });
            object r2 = rt.HasAnimNative(null, new UEFrame { Object = a }, new object[] { new UEName("Walk") });
            Check("HasAnim without a mesh: false", Equals(r1, false) && Equals(r2, false), $"{r1}/{r2}");
            int n = warns.Count(w => w.StartsWith("HasAnim: No mesh", StringComparison.Ordinal));
            Check("HasAnim without a mesh: line \"HasAnim: No mesh\" on every call", n == 2 && warns.Count == 2,
                $"lines {n}, total {warns.Count}: {string.Join(" | ", warns)}");
            warns.Clear();
            UEObject m = AnimActor(new UEResourceRef("LodMesh", "FixLod"));
            object r3 = rt.HasAnimNative(null, new UEFrame { Object = m }, new object[] { new UEName("Walk") });
            Check("HasAnim with a mesh: no \"No mesh\" line", !warns.Any(w => w.StartsWith("HasAnim: No mesh", StringComparison.Ordinal)),
                string.Join(" | ", warns));
            Check("HasAnim with a mesh without a catalogue: false", Equals(r3, false), $"{r3}");
        }
        finally { HP1.Core.UETrace.Warn = saved; }
    }

    // ── 6. ULevel::DestroyActor -> FUN_103a72c0 (Engine.dll.c:252963-252976), outside the editor:
    //    (flags & 5) -> 0; (flags & 0x200) -> 1; Role != 4 && !bNetForce && !(flags & 0x4000000) -> 0.
    //    bNetForce of execDestroy = 0; 0x4000000 is bNetTemporary.
    static void DestroyRole()
    {
        var rt = new UELevelRuntime();
        var interp = new UEInterpreter();
        var events = new List<string>();
        var savedProbe = UELifecycle.EventProbe;
        UELifecycle.EventProbe = (o, ev, args) => events.Add($"{o.GetProp("Name")}.{ev}");
        try
        {
            UEObject Make(string name, int role, params (string, object)[] props)
            {
                var a = new UEObject();
                a.Props["Name"] = new UEName(name);
                a.Props["Role"] = (byte)role;
                foreach (var (k, v) in props) a.Props[k] = v;
                rt.Actors.Add(a);
                return a;
            }
            void Expect(UEObject a, bool result, bool destroyed, bool anyEvents)
            {
                string n = a.GetProp("Name").ToString();
                events.Clear();
                bool r = rt.DestroyActor(interp, a);
                Check($"Destroy {n}: result {result}", r == result, $"got {r}");
                Check($"Destroy {n}: destroyed={destroyed}", a.Destroyed == destroyed && Equals(a.GetProp("bDeleteMe") ?? false, destroyed),
                    $"Destroyed={a.Destroyed} bDeleteMe={a.GetProp("bDeleteMe")}");
                Check($"Destroy {n}: events {(anyEvents ? "present" : "none")}", (events.Count > 0) == anyEvents, string.Join(",", events));
            }
            // Refused by role: no events, the actor is alive.
            Expect(Make("SimProxy", 2), result: false, destroyed: false, anyEvents: false);
            Expect(Make("AutonomousProxy", 3), result: false, destroyed: false, anyEvents: false);
            Expect(Make("NoneRole", 0), result: false, destroyed: false, anyEvents: false);
            // bNetTemporary lifts the refusal.
            Expect(Make("NetTemporary", 3, ("bNetTemporary", true)), result: true, destroyed: true, anyEvents: true);
            // ROLE_Authority — the normal path (control).
            Expect(Make("Authority", 4), result: true, destroyed: true, anyEvents: true);
            // Check order: bStatic/bNoDelete -> 0 and bDeleteMe -> 1 come BEFORE the role.
            Expect(Make("StaticSim", 3, ("bStatic", true)), result: false, destroyed: false, anyEvents: false);
            var dead = Make("DeadSim", 3);
            dead.Destroyed = true; dead.Props["bDeleteMe"] = true;
            events.Clear();
            Check("Destroy DeadSim (bDeleteMe before role): result true", rt.DestroyActor(interp, dead), "got false");
            Check("Destroy DeadSim: no events", events.Count == 0, string.Join(",", events));
        }
        finally { UELifecycle.EventProbe = savedProbe; }
    }

    // ── 7. Install file lookup like Windows (UEInstallPath.File, D-22): shipped ran on a case-insensitive file system,
    //    references name `SkyBox`/`hp_fx` for Textures/Skybox.utx, HP_FX.utx. Layout as in the install, names as in the
    //    references. The case fallback runs only on a case-sensitive file system (Linux); on macOS/Windows the exact stat
    //    already finds the file, and the fallback is reported as not executed (no verdict on it there).
    static void InstallPathCase(string tmp)
    {
        string tex = Path.Combine(tmp, "Textures");
        Directory.CreateDirectory(tex);
        File.WriteAllText(Path.Combine(tex, "Skybox.utx"), "x");
        Directory.CreateDirectory(Path.Combine(tmp, "Save"));
        File.WriteAllText(Path.Combine(tmp, "Save", "SGS lev_tut1b.bmp"), "x");
        bool caseSensitive = !File.Exists(Path.Combine(tex, "SKYBOX.UTX"));
        foreach (var (asked, onDisk) in new[] {
            (Path.Combine(tmp, "Textures", "SkyBox.utx"), Path.Combine(tex, "Skybox.utx")),
            (Path.Combine(tmp, "save", "SGS Lev_Tut1b.bmp"), Path.Combine(tmp, "Save", "SGS lev_tut1b.bmp")) })
        {
            string got = UEInstallPath.File(asked);
            Check("7 found " + Path.GetFileName(asked), got is not null, "null");
            if (caseSensitive) Check("7 on-disk name " + Path.GetFileName(asked), got == onDisk, got ?? "null");
        }
        Check("7 missing file", UEInstallPath.File(Path.Combine(tex, "NoSuch.utx")) is null, "not null");
        Console.WriteLine(caseSensitive ? "     case fallback EXECUTED (case-sensitive file system)"
            : "     case fallback NOT EXECUTED (case-insensitive file system): verdict only on the exact branch here");
    }
}
