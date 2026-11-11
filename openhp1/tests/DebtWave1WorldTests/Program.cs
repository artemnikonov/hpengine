// D-14, WORLD part: package-parsing fixes found by re-checking 23 "not verified" functions,
// and pinning of the verified GameSaveInfo format. No game files needed.
//
// Input is the same Core functions the runtime calls:
//   LevelEnterTextForLoad — LoadMap step 4 (loading screen), public entry; reads the LevelEnterText tag
//     with the UEPackageReader walker and the string with UEActorGraphLoader.ReadString (same as actor Str tags);
//   DecodeWalkerValue     — placed-actor tag decoder (ApplyInstanceProperties), private: called via
//     reflection, the same body map loading runs (Str -> ReadString, Struct -> DecodeStruct);
//   WriteGameSaveInfo/ReadGameSaveInfo — natives 325/326 (AActor::execSaveGameSaveInfo / execLoadGameSaveInfo).
//
// Expectations are computed here independently, from the shipped bodies:
//   operator<<(FArchive&, FString&)  Core 0x10150830: compact SaveNum; SaveNum >= 0 — |SaveNum| bytes,
//     each `movzx`-ed into TCHAR (ISO-8859-1); < 0 — |SaveNum| UTF-16 words; the string runs to the first zero; on write
//     SaveNum = all chars <= 0xFF ? Num : -Num, Num = length + 1 (0 for empty);
//   FCompactIndex                    Core 0x10163d40: b0 = sign 0x80 | "more" 0x40 | 6 bits; then 7 bits each
//     with flag 0x80;
//   FPlane in UStruct::SerializeBin  Core.dll.c:45567-45628: X, Y, Z, W — 16 bytes.
// Red mutations — audit/debt_wave1_redtest_world.sh.
using HP1.UEScript;
using System.Reflection;
using System.Text;

static class Program
{
    static int _fail, _pass;

    static void Check(string name, bool ok, string detail)
    {
        if (ok) _pass++; else { _fail++; Console.WriteLine($"FAIL {name}: {detail}"); }
    }

    static int Main()
    {
        HP1.Core.UEClassRegistry.Shared ??= new HP1.Core.UEClassRegistry();
        LevelEnterTextLatin1();
        LevelEnterTextUtf16();
        LevelEnterTextEmptyFallsBackToMap();
        WalkerStrAdvancesPastAllElements();
        WalkerPlaneKeepsW();
        GameSaveInfoBytes();
        Console.WriteLine($"DebtWave1WorldTests: passed {_pass}, failed {_fail}");
        return _fail == 0 ? 0 : 1;
    }

    // ── independent format writer (not port code) ────────────────────────────────────────────────────
    static void Compact(List<byte> o, int v)
    {
        uint a = (uint)Math.Abs((long)v);
        byte b0 = (byte)(a & 0x3F);
        if (v < 0) b0 |= 0x80;
        if (a >= 0x40) b0 |= 0x40;
        o.Add(b0);
        a >>= 6;
        if ((b0 & 0x40) == 0) return;
        while (true)
        {
            byte b = (byte)(a & 0x7F);
            a >>= 7;
            if (a != 0) b |= 0x80;
            o.Add(b);
            if ((b & 0x80) == 0) return;
        }
    }

    // FString on disk: ANSI — bytes + 0; otherwise -Num and UTF-16 words + 0.
    static byte[] FStr(string s, bool unicode)
    {
        var o = new List<byte>();
        int num = s.Length + 1;
        Compact(o, unicode ? -num : num);
        foreach (char c in s)
        {
            if (unicode) { o.Add((byte)c); o.Add((byte)(c >> 8)); }
            else o.Add((byte)c);
        }
        if (unicode) { o.Add(0); o.Add(0); } else o.Add(0);
        return o.ToArray();
    }

    // Minimal UE1 version-76 package: names, one LevelInfo0 export with one LevelEnterText tag (Str,
    // size code 5 — a length byte), then None. Read by the same UEPackageReader as campaign maps.
    static string Package(byte[] strValue)
    {
        string[] names = { "None", "LevelInfo0", "LevelEnterText" };
        var body = new List<byte>();
        Compact(body, 2);                                // LevelEnterText
        body.Add((byte)(13 | (5 << 4)));                 // StrProperty, size is the next byte
        body.Add(checked((byte)strValue.Length));
        body.AddRange(strValue);
        Compact(body, 0);                                // None

        var nameTable = new List<byte>();
        foreach (string n in names) { nameTable.AddRange(FStr(n, false)); nameTable.AddRange(new byte[4]); }

        const int header = 4 + 2 + 2 + 4 + 6 * 4;
        int nameOffset = header;
        int dataOffset = nameOffset + nameTable.Count;
        int exportOffset = dataOffset + body.Count;
        var export = new List<byte>();
        Compact(export, 0);                              // class
        Compact(export, 0);                              // super
        export.AddRange(BitConverter.GetBytes(0));       // outer
        Compact(export, 1);                              // name LevelInfo0
        export.AddRange(BitConverter.GetBytes(0u));      // flags (no RF_HasStack)
        Compact(export, body.Count);
        Compact(export, dataOffset);
        int importOffset = exportOffset + export.Count;

        var file = new List<byte>();
        file.AddRange(BitConverter.GetBytes(0x9e2a83c1u));
        file.AddRange(BitConverter.GetBytes((ushort)76));
        file.AddRange(BitConverter.GetBytes((ushort)0));
        file.AddRange(BitConverter.GetBytes(0u));
        file.AddRange(BitConverter.GetBytes(names.Length)); file.AddRange(BitConverter.GetBytes(nameOffset));
        file.AddRange(BitConverter.GetBytes(1)); file.AddRange(BitConverter.GetBytes(exportOffset));
        file.AddRange(BitConverter.GetBytes(0)); file.AddRange(BitConverter.GetBytes(importOffset));
        file.AddRange(nameTable);
        file.AddRange(body);
        file.AddRange(export);
        string path = Path.Combine(Path.GetTempPath(), $"debtwave1_{Guid.NewGuid():N}.unr");
        File.WriteAllBytes(path, file.ToArray());
        return path;
    }

    static string EnterText(byte[] strValue, string urlMap)
    {
        string path = Package(strValue);
        try { return UELevelRuntime.LevelEnterTextForLoad(path, urlMap); }
        catch (Exception e) { return "EXC " + e.GetType().Name + ": " + e.Message; }
        finally { File.Delete(path); }
    }

    static string Show(string s) => s is null ? "null" : string.Join(" ", s.Select(c => ((int)c).ToString("X4")));

    // Byte 0xE9 in shipped is TCHAR 0x00E9 ('é'), not the '?' of an ASCII decoder.
    static void LevelEnterTextLatin1()
    {
        string want = "Café © Hogwarts";
        string got = EnterText(FStr(want, false), "Lev_X.unr");
        Check("LevelEnterText ANSI (bytes >= 0x80)", got == want, $"expected \"{want}\", got \"{got}\" [{Show(got)}]");
    }

    // SaveNum < 0 is a UTF-16 string (it must not decode as "", which would show the map name on the loading screen).
    static void LevelEnterTextUtf16()
    {
        string want = "Хогвартс";   // non-Latin text forces the UTF-16 form
        string got = EnterText(FStr(want, true), "Lev_X.unr");
        Check("LevelEnterText UTF-16 (SaveNum < 0)", got == want, $"expected \"{want}\", got \"{got}\" [{Show(got)}]");
    }

    // Num == 0 (empty) and Num == 1 (terminator only) give an empty string; LoadMap takes URL.Map.
    static void LevelEnterTextEmptyFallsBackToMap()
    {
        string a = EnterText(new byte[] { 0x00 }, "Lev_Y.unr");
        string b = EnterText(new byte[] { 0x01, 0x00 }, "Lev_Y.unr");
        Check("LevelEnterText empty -> URL.Map", a == "Lev_Y.unr" && b == "Lev_Y.unr", $"Num=0 -> \"{a}\", Num=1 -> \"{b}\"");
    }

    static readonly MethodInfo Walker = typeof(UEActorGraphLoader).GetMethod("DecodeWalkerValue",
        BindingFlags.NonPublic | BindingFlags.Static) ?? throw new MissingMethodException("DecodeWalkerValue");
    static readonly MethodInfo ReadStr = typeof(UEActorGraphLoader).GetMethod("ReadString",
        BindingFlags.NonPublic | BindingFlags.Static) ?? throw new MissingMethodException("ReadString");

    static object Decode(int type, string structName, byte[] raw)
    {
        var tag = new UEPackageReader.RawPropertyTag("P", type, structName, 0, raw, false);
        try { return Walker.Invoke(null, new object[] { null, tag, null, null }); }
        catch (TargetInvocationException e) { return "EXC " + e.InnerException?.GetType().Name; }
    }

    // A string inside a struct: the position must move past ALL elements (and the terminator), otherwise the next
    // field is read shifted. Two strings in a row — ANSI with an inner zero and UTF-16.
    static void WalkerStrAdvancesPastAllElements()
    {
        string tag = Decode(13, null, FStr("Hedwig", false)) as string;
        Check("actor Str tag (ANSI)", tag == "Hedwig", $"got \"{tag}\"");

        var buf = new List<byte>();
        buf.AddRange(FStr("ab\0cd", false));             // Num = 6, the string runs to the first zero
        buf.AddRange(FStr("ЖЖ", true));        // non-Latin, Num = -3
        buf.Add(0x5A);                                   // marker after the second string
        object[] args = { buf.ToArray(), 0 };
        string s1 = ReadStr.Invoke(null, args) as string;
        int p1 = (int)args[1];
        string s2 = ReadStr.Invoke(null, args) as string;
        int p2 = (int)args[1];
        Check("FString: string up to the first zero, offset over all bytes", s1 == "ab" && p1 == 1 + 6,
            $"\"{s1}\" pos={p1}, expected \"ab\" pos=7");
        Check("FString: UTF-16 and offset over all words", s2 == "ЖЖ" && p2 == 7 + 1 + 6   // non-Latin test input
            && buf[p2] == 0x5A, $"\"{s2}\" [{Show(s2)}] pos={p2}, expected \"ЖЖ\" pos=14");   // non-Latin test input
    }

    // FPlane is 16 bytes; W of a placed actor (ZoneInfo.ViewFog/ViewFlash) is not lost, the type is UEPlane, as
    // for class defaults.
    static void WalkerPlaneKeepsW()
    {
        var raw = new List<byte>();
        foreach (float f in new[] { 0.25f, -1.5f, 3f, 0.75f }) raw.AddRange(BitConverter.GetBytes(f));
        object v = Decode(10, "Plane", raw.ToArray());
        Check("Plane of a placed actor: X,Y,Z,W", v is UEPlane p && p.X == 0.25f && p.Y == -1.5f && p.Z == 3f && p.W == 0.75f,
            $"got {v?.GetType().Name} {v}");
        object vec = Decode(10, "Vector", raw.GetRange(0, 12).ToArray());
        Check("Vector of a placed actor unchanged", vec is UEVector u && u.x == 0.25f && u.y == -1.5f && u.z == 3f,
            $"got {vec?.GetType().Name} {vec}");
    }

    // GameSaveInfo: 4 x int32 and an FString — byte-for-byte as shipped (verified: the format is unchanged).
    static void GameSaveInfoBytes()
    {
        void Case(string name, string level)
        {
            var info = new UEObject();
            info.Props["numBeans"] = 11; info.Props["numStars"] = -2; info.Props["numPoints"] = 300; info.Props["savePointID"] = 7;
            info.Props["currentLevelString"] = level;
            string path = Path.Combine(Path.GetTempPath(), $"debtwave1_gsi_{Guid.NewGuid():N}");
            try
            {
                bool wrote = UELevelRuntime.WriteGameSaveInfo(path, info);
                byte[] got = File.ReadAllBytes(path);
                var want = new List<byte>();
                foreach (int i in new[] { 11, -2, 300, 7 }) want.AddRange(BitConverter.GetBytes(i));
                bool ansi = level.All(c => c <= 0xFF);
                if (level.Length == 0) want.Add(0x00);
                else want.AddRange(FStr(level, !ansi));
                Check($"GameSaveInfo write ({name})", wrote && got.SequenceEqual(want),
                    $"expected {BitConverter.ToString(want.ToArray())}, got {BitConverter.ToString(got)}");
                var back = new UEObject();
                bool read = UELevelRuntime.ReadGameSaveInfo(path, back);
                Check($"GameSaveInfo read ({name})", read && back.GetProp("currentLevelString") as string == level
                    && UEInterpreter.ToInt(back.GetProp("numPoints")) == 300,
                    $"got \"{back.GetProp("currentLevelString")}\"");
            }
            finally { File.Delete(path); }
        }
        Case("empty", "");
        Case("ANSI, Num = 0x40 — two-byte compact", new string('a', 63));
        Case("byte 0xE9 stays ANSI", "Lev_Café");
        Case("character > 0xFF — UTF-16", "Lev_Ж");   // non-Latin test input
        Case("long, three-byte compact", new string('b', 9000));
    }
}
