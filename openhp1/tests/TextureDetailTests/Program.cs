// Texture Detail (author decision: as in original HP1 1.53).
// Expectations are literals taken from the disasm (addresses in HP1.Core UETextureLod), not from the port:
//   UTexture::Lock 0x1042132e..0x10421371, mesh skins in Render 0x10b0f243..0x10b0f302,
//   UByteProperty::ImportText Core.dll.c:65128 + ReadToken FUN_1016c5e0.
// Exit 0: all green; 1: a FAIL (each named on its own line).
using HP1.UEScript;
using OpenHP1;
using OpenHP1.Sprites;

static class Program
{
    static int _pass, _fail;
    static void Eq<T>(string name, T got, T want)
    {
        if (EqualityComparer<T>.Default.Equals(got, want)) _pass++;
        else { _fail++; Console.WriteLine($"FAIL {name}: got {got}, expected {want}"); }
    }

    static int Main()
    {
        // ── A. parsing the `set ini:` value (ImportText: token + FName of enum Details, no numbers) ──
        Eq("parse High", Parse("High"), 0);
        Eq("parse medium (FName case-insensitive)", Parse("medium"), 1);
        Eq("parse LOW", Parse("LOW"), 2);
        Eq("parse \"Ultra Low\" in quotes", Parse("\"Ultra Low\""), 3);
        Eq("parse Ultra Low without quotes (token Ultra)", Parse("Ultra Low"), -1);
        Eq("parse a number is rejected", Parse("2"), -1);
        Eq("parse empty", Parse(""), -1);
        Eq("parse Lowest (whole token)", Parse("Lowest"), -1);
        Eq("parse Low trailing (token up to the space)", Parse("Low x"), 2);

        // ── B. UTexture::Lock, LOD -1: Client.TextureLODSet[LODSet], clamp min(mips-1, 3) ──
        int[] world9 = { 0, 1, 2, 3 };
        for (int td = 0; td < 4; td++)
            Eq($"lock world 9 mips td={td}", UETextureLod.LockFirstMip(-1, 1, 9, td, 0), world9[td]);
        Eq("lock world 2 mips Low", UETextureLod.LockFirstMip(-1, 1, 2, 2, 0), 1);
        Eq("lock world 1 mip Low", UETextureLod.LockFirstMip(-1, 1, 1, 2, 0), 0);
        Eq("lock world 4 mips Ultra Low", UETextureLod.LockFirstMip(-1, 1, 4, 3, 0), 3);
        Eq("lock LODSet 0 Low (TextureLODSet[0] = 0)", UETextureLod.LockFirstMip(-1, 0, 9, 2, 0), 0);
        Eq("lock LODSet 2 = SkinDetail", UETextureLod.LockFirstMip(-1, 2, 9, 0, 2), 2);
        Eq("lock LODSet 2 is not TextureDetail", UETextureLod.LockFirstMip(-1, 2, 9, 3, 0), 0);
        Eq("lock explicit LOD 0 (particles) Low", UETextureLod.LockFirstMip(0, 1, 9, 2, 0), 0);
        Eq("lock explicit LOD 5 -> 3", UETextureLod.LockFirstMip(5, 1, 9, 0, 0), 3);
        Eq("lock RecommendedLOD raises -1", UETextureLod.LockFirstMip(-1, 1, 9, 0, 0, 2), 2);
        Eq("lock RecommendedLOD leaves an explicit one alone", UETextureLod.LockFirstMip(0, 1, 9, 0, 0, 2), 0);

        // ── C. mesh skins: min(max(TD-1, RecLOD), TextureLODSet[LODSet]) ──
        int[] skin = { 0, 0, 1, 2 };
        for (int td = 0; td < 4; td++)
            Eq($"mesh LODSet1 td={td}", UETextureLod.MeshSkinRequest(1, td, 0), skin[td]);
        Eq("mesh LODSet0 Ultra Low", UETextureLod.MeshSkinRequest(0, 3, 0), 0);
        Eq("mesh LODSet2 Ultra Low, SkinDetail High", UETextureLod.MeshSkinRequest(2, 3, 0), 0);
        Eq("mesh LODSet2 Ultra Low, SkinDetail Low", UETextureLod.MeshSkinRequest(2, 3, 2), 2);
        Eq("mesh RecommendedLOD", UETextureLod.MeshSkinRequest(1, 0, 0, 1), 0);   // min(max(-1,1), 0)

        // ── D. LODSet past the TextureLODSet[8] array: loud ──
        bool threw = false;
        try { UETextureLod.ClientLodSet(8, 0, 0); } catch (ArgumentOutOfRangeException) { threw = true; }
        Eq("LODSet 8 outside the array — exception", threw, true);

        // ── E. Resolve: null -> class default High; refusal -> previous level ──
        Eq("resolve null", UETextureLod.Resolve(null, 2, out bool r0), 0); Eq("resolve null is not a refusal", r0, false);
        Eq("resolve a number is rejected (keeps the previous)", UETextureLod.Resolve("2", 0, out bool r2), 0); Eq("resolve a number — refusal", r2, true);
        Eq("resolve garbage keeps the previous", UETextureLod.Resolve("bogus", 2, out bool r1), 2); Eq("resolve garbage — refusal", r1, true);

        // ── F. menu route: ConsoleCommand -> Core config -> level ──
        string dir = Path.Combine(Path.GetTempPath(), "td_tests_" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        string sys = Path.Combine(dir, "Default.ini"), usr = Path.Combine(dir, "DefUser.ini");
        File.WriteAllText(sys, "[Engine.Engine]\r\nViewportManager=WinDrv.WindowsClient\r\n[WinDrv.WindowsClient]\r\nBrightness=0.400000\r\n");
        File.WriteAllText(usr, "[DefaultPlayer]\r\nName=Player\r\n");
        var cfg = new UEConfig(new UEIniFile(sys), new UEIniFile(usr));
        Eq("config: TextureDetail without a key = class default", cfg.TextureDetail, "High");
        Eq("config: SkinDetail without a key = null", cfg.SkinDetail, null);
        UEConsoleCommand.Execute(cfg, "set ini:Engine.Engine.ViewportManager TextureDetail Low");
        Eq("config: after set ini", cfg.TextureDetail, "Low");
        Eq("route: level Low", UETextureLod.Resolve(cfg.TextureDetail, 0, out _), 2);
        Eq("get ini answers the same", UEConsoleCommand.Execute(cfg, "get ini:Engine.Engine.ViewportManager TextureDetail"), "Low");
        Directory.Delete(dir, true);

        // ── G. frontend: draw path -> LOD Lock -> first mip ──
        var tex = new UETextureSource.TextureData { State = "EXPORTED", LodSet = 1, MipCount = 9 };
        Eq("front Client Low", TextureDetail.FirstMip(tex, TexLod.Client, 2, 0), 2);
        Eq("front MeshSkin Low", TextureDetail.FirstMip(tex, TexLod.MeshSkin, 2, 0), 1);
        Eq("front Zero Low", TextureDetail.FirstMip(tex, TexLod.Zero, 2, 0), 0);
        Eq("front Client High", TextureDetail.FirstMip(tex, TexLod.Client, 0, 0), 0);
        var proc = new UETextureSource.TextureData { State = "PROCEDURAL_NO_PIXELS", LodSet = 1, MipCount = 1 };
        Eq("front procedural", TextureDetail.FirstMip(proc, TexLod.Client, 3, 0), 0);
        Eq("front particle -> Zero", TextureDetail.OfSprite(SpriteKind.Particle), TexLod.Zero);
        Eq("front sprite -> Client", TextureDetail.OfSprite(SpriteKind.Sprite), TexLod.Client);
        Eq("front corona -> Client", TextureDetail.OfSprite(SpriteKind.Corona), TexLod.Client);
        Eq("front decal -> Client", TextureDetail.OfSprite(SpriteKind.Decal), TexLod.Client);

        // ── H. D3DDrv 256 limit after Lock (SetTexture, formats 0x1000ad6d..0x1000ad8e) ──
        Eq("d3d 512x512 High -> mip 1", UETextureLod.RenDevFirstMip(0, 512, 512, 10), 1);
        Eq("d3d 512x256 High -> mip 1", UETextureLod.RenDevFirstMip(0, 512, 256, 10), 1);
        Eq("d3d 256x512 High -> mip 1 (by V)", UETextureLod.RenDevFirstMip(0, 256, 512, 10), 1);
        Eq("d3d 256x256 High leaves it alone", UETextureLod.RenDevFirstMip(0, 256, 256, 9), 0);
        Eq("d3d 512 Low (Lock 2) leaves it alone", UETextureLod.RenDevFirstMip(2, 512, 512, 10), 2);
        Eq("d3d 1024 High -> mip 2", UETextureLod.RenDevFirstMip(0, 1024, 1024, 11), 2);
        bool gerr = false;
        try { UETextureLod.RenDevFirstMip(0, 512, 512, 1); } catch (InvalidOperationException) { gerr = true; }
        Eq("d3d mips ran out — GError", gerr, true);
        var sky = new UETextureSource.TextureData { State = "EXPORTED", LodSet = 1, MipCount = 10, Width = 512, Height = 512 };
        Eq("front sky 512 High", TextureDetail.FirstMip(sky, TexLod.Client, 0, 0), 1);
        Eq("front sky 512 Medium", TextureDetail.FirstMip(sky, TexLod.Client, 1, 0), 1);
        Eq("front sky 512 Low", TextureDetail.FirstMip(sky, TexLod.Client, 2, 0), 2);
        Eq("front particle 512 (LOD 0) is cut too", TextureDetail.FirstMip(sky, TexLod.Zero, 2, 0), 1);

        Console.WriteLine($"TextureDetailTests: {_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    static int Parse(string s) => UETextureLod.TryParseDetail(s, out int l) ? l : -1;
}
