// Mods for both games: ModInstall (hpengine/packaging/ModInstall.cs, the Add rule both Launchers use) and the HP2 tab's
// Mods page (its own folders: <HP2 game>/Mods, <HP2Data>/HPEngineData/Mods.ini, the engine's InstallRoot.Hp2DataDir).
using System.IO.Compression;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using HPEngine.Launcher;
using HPEngine.Packaging;

static partial class T
{
    static void ModAdd()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "mod-add-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var game = Path.Combine(tmp, "game"); var src = Path.Combine(tmp, "src");
            foreach (var d in new[] { "System", "Maps", "Textures" }) Directory.CreateDirectory(Path.Combine(game, d));
            string Refused(string s) { try { ModInstall.Add(game, s); return null; } catch (InvalidDataException e) { return e.Message; } }
            string Tree(string d) => string.Join("|", Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(d, f).Replace('\\', '/')).OrderBy(x => x, StringComparer.Ordinal));
            // a folder: copied, the source untouched; a link inside it is skipped
            var f1 = Path.Combine(src, "Flat Mod"); Directory.CreateDirectory(f1);
            File.WriteAllText(Path.Combine(f1, "HPBase.u"), "u"); File.WriteAllText(Path.Combine(f1, "readme.txt"), "r");
            if (!OperatingSystem.IsWindows()) File.CreateSymbolicLink(Path.Combine(f1, "outside.u"), Path.Combine(tmp, "nowhere"));
            Eq("add folder: its name", "Flat Mod", ModInstall.Add(game, f1));
            Eq("add folder: the files, no link", "HPBase.u|readme.txt", Tree(Path.Combine(game, "Mods", "Flat Mod")));
            Check("add folder: the source stays", File.Exists(Path.Combine(f1, "HPBase.u")));
            Directory.CreateDirectory(Path.Combine(src, "x", "FLAT MOD")); File.WriteAllText(Path.Combine(src, "x", "FLAT MOD", "a.u"), "a");
            Check("add: a mod of that name (any case) is refused", Refused(Path.Combine(src, "x", "FLAT MOD"))?.Contains("already a mod named Flat Mod") == true);
            // a zip with one wrapper folder (and __MACOSX): the wrapper dropped; a game folder is never dropped
            var z = Path.Combine(src, "Wrapped-v1.zip");
            using (var a = ZipFile.Open(z, ZipArchiveMode.Create))
            {
                foreach (var (n, t) in new[] { ("Wrapped-v1/inner/System/HGame.u", "g"), ("Wrapped-v1/inner/Maps/New.unr", "m"), ("__MACOSX/._x", "x") })
                    using (var w = new StreamWriter(a.CreateEntry(n).Open())) w.Write(t);
            }
            Eq("add zip: named after the archive", "Wrapped-v1", ModInstall.Add(game, z));
            Eq("add zip: two wrapper folders dropped, down to the game folders", "Maps/New.unr|System/HGame.u", Tree(Path.Combine(game, "Mods", "Wrapped-v1")));
            var z2 = Path.Combine(src, "OnlySystem.zip");
            using (var a = ZipFile.Open(z2, ZipArchiveMode.Create)) using (var w = new StreamWriter(a.CreateEntry("system/Engine.u").Open())) w.Write("e");
            ModInstall.Add(game, z2);
            Eq("add zip: a single game folder is kept (any case)", "system/Engine.u", Tree(Path.Combine(game, "Mods", "OnlySystem")));
            File.WriteAllText(Path.Combine(src, "Setup.exe"), "MZ");
            Check("add: .exe refused with the reason", Refused(Path.Combine(src, "Setup.exe"))?.Contains("never runs a mod's installer") == true);
            Directory.CreateDirectory(Path.Combine(src, "Empty", "sub"));
            Check("add: a mod without files is refused, nothing left", Refused(Path.Combine(src, "Empty"))?.Contains("holds no files") == true && !Directory.Exists(Path.Combine(game, "Mods", "Empty")));
            File.WriteAllBytes(Path.Combine(src, "Early.unr"), new byte[] { 0xC1, 0x83, 0x2A, 0x9E, 76, 0 });
            Eq("add: one loose package is a mod of its own", "Early", ModInstall.Add(game, Path.Combine(src, "Early.unr")));
            Check("add: the loose package in its folder", File.Exists(Path.Combine(game, "Mods", "Early", "Early.unr")));
            File.WriteAllText(Path.Combine(src, "notes.unr"), "text");
            Check("add: a loose file that is not a package is refused", Refused(Path.Combine(src, "notes.unr"))?.Contains("is not a mod folder") == true);
            File.WriteAllText(Path.Combine(src, "bad.zip"), "not a zip");
            Check("add: a damaged zip is refused", Refused(Path.Combine(src, "bad.zip"))?.StartsWith("Could not read bad.zip") == true && !Directory.Exists(Path.Combine(game, "Mods", "bad")));
            Check("add: no temporary folder left", !Directory.EnumerateDirectories(Path.Combine(game, "Mods"), ".adding-*").Any());
            // the Launcher's Remove: off in Mods.ini first, then the folder
            ModsIni.Save(game, new[] { new ModEntry("Flat Mod", true, true), new ModEntry("OnlySystem", true, true) });
            ModsIni.Remove(game, null, ModsIni.Load(game), "flat mod");
            Seq("remove: off in Mods.ini", new[] { "OnlySystem" }, ModsIni.ReadEnabled(File.ReadAllText(ModsIni.IniPath(game))));
            Check("remove: the folder is gone, the others stay", !Directory.Exists(Path.Combine(game, "Mods", "Flat Mod")) && Directory.Exists(Path.Combine(game, "Mods", "Wrapped-v1")));
            fired.Add("mods:add");
        }
        finally { try { Directory.Delete(tmp, true); } catch (IOException) { } }
    }

    // The HP2 tab's Mods page: list, add, switch, remove — on HP2's folders, HP1's untouched.
    static void Hp2Mods()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "hp2-mods-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var g = Path.Combine(tmp, "game"); var h2 = Path.Combine(tmp, "HP2Data", "game");
            Directory.CreateDirectory(Path.Combine(g, "System")); File.WriteAllText(Path.Combine(g, ".hp1-installed"), "edition=US 1.53: exact\n");
            Directory.CreateDirectory(Path.Combine(g, "Mods", "Hp1Mod", "Maps"));
            File.WriteAllBytes(Path.Combine(g, "Mods", "Hp1Mod", "Maps", "New.unr"), new byte[] { 0xC1, 0x83, 0x2A, 0x9E, 126, 0 });   // HP1: no limit
            foreach (var d in new[] { "System", "Maps" }) Directory.CreateDirectory(Path.Combine(h2, d));
            File.WriteAllText(Path.Combine(h2, "System", "Engine.dll"), "x"); File.WriteAllText(Path.Combine(h2, "System", "Startup.int"), "[IDDIALOG_FrontEndPageMainMenu]\nIDC_NewGame=New Game\nIDC_LoadGame=Load Game\n");
            File.WriteAllText(Path.Combine(h2, ".hp1-installed"), "edition=HP2 EN\n");
            var src = Path.Combine(tmp, "src", "Hp2Texture"); Directory.CreateDirectory(Path.Combine(src, "Textures")); File.WriteAllText(Path.Combine(src, "Textures", "X.utx"), "t");
            var w = new MainWindow(g, tmp, "0.1.0-beta", false); w.Show(); Dispatcher.UIThread.RunJobs();
            void Click(string n) { Find<Button>(w, n)?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); }
            string Rows() => string.Join("|", Find<StackPanel>(w, "ModsList").GetLogicalDescendants().OfType<ToggleSwitch>().Select(c => c.Name[4..] + (c.IsChecked == true ? "+" : "-")));
            Click("TabHp2");
            Eq("hp2 mods: the row on the main page", "None", Find<TextBlock>(w, "Hp2ModsSummary")?.Text);
            Click("Hp2ModsRow");
            Check("hp2 mods: the Mods page, HP2's (no HP1 mod listed)", Find<StackPanel>(w, "ModsPage").IsVisible && !Find<StackPanel>(w, "Hp2Panel").IsVisible && Rows() == "", Rows());
            Eq("hp2 mods: added (ModsIni.Add) into HP2's Mods folder", null, ModsIni.Add(h2, src));
            Click("RefreshMods");
            Eq("hp2 mods: listed off", "Hp2Texture-", Rows());
            Find<ToggleSwitch>(w, "Mod_Hp2Texture").IsChecked = true; Dispatcher.UIThread.RunJobs();
            var ini = Path.Combine(tmp, "HP2Data", "HPEngineData", "Mods.ini");
            Eq("hp2 mods: the data dir the engine runs HP2 with", InstallRoot.Hp2DataDir(Hp2FrontEnd.SaveDir(g)), Path.GetDirectoryName(ini));
            Seq("hp2 mods: switched on in HP2's Mods.ini", new[] { "Hp2Texture" }, File.Exists(ini) ? ModsIni.ReadEnabled(File.ReadAllText(ini)) : new List<string>());
            Check("hp2 mods: HP1's Mods.ini untouched", !File.Exists(ModsIni.IniPath(g)));
            Click("Remove_Hp2Texture");
            Window dlg = null;
            for (int i = 0; i < 100 && dlg == null; i++) { Thread.Sleep(10); Dispatcher.UIThread.RunJobs(); dlg = w.OwnedWindows.FirstOrDefault(); }
            dlg?.GetLogicalDescendants().OfType<Button>().FirstOrDefault(b => b.Content as string == "Remove")?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (int i = 0; i < 100 && Directory.Exists(Path.Combine(h2, "Mods", "Hp2Texture")); i++) { Thread.Sleep(10); Dispatcher.UIThread.RunJobs(); }
            Check("hp2 mods: Remove asked, then folder gone and switched off", dlg != null && !Directory.Exists(Path.Combine(h2, "Mods", "Hp2Texture"))
                && ModsIni.ReadEnabled(File.ReadAllText(ini)).Count == 0 && Rows() == "", $"dialog {dlg != null}, rows {Rows()}, remove {Find<Button>(w, "Remove_Hp2Texture")?.IsEffectivelyEnabled}, error {Find<TextBlock>(w, "ErrorText").Text}");
            // a mod made for a newer HP2 engine (a v93 package, retail 79): named under the mod; Play refuses it by name
            var newer = Path.Combine(h2, "Mods", "M212Map", "Maps"); Directory.CreateDirectory(newer);
            File.WriteAllBytes(Path.Combine(newer, "Adv6Goyle.unr"), new byte[] { 0xC1, 0x83, 0x2A, 0x9E, 93, 0 });
            var retail = Path.Combine(h2, "Mods", "RetailMap", "Maps"); Directory.CreateDirectory(retail);
            File.WriteAllBytes(Path.Combine(retail, "Adv6Goyle.unr"), new byte[] { 0xC1, 0x83, 0x2A, 0x9E, 79, 0 });
            Click("RefreshMods");
            Eq("hp2 newer engine: the line under the mod", "M212Map was made for a newer HP2 engine and does not run in HPEngine", Find<TextBlock>(w, "ModNewer_M212Map")?.Text);
            Check("hp2 newer engine: none under a v79 mod", Find<TextBlock>(w, "ModNewer_RetailMap") == null);
            Find<ToggleSwitch>(w, "Mod_M212Map").IsChecked = true; Dispatcher.UIThread.RunJobs();
            Click("ModsBack"); Click("Hp2NewGame"); Click("Hp2Slot1");
            Eq("hp2 newer engine: Play refused by name", "M212Map was made for a newer HP2 engine and does not run in HPEngine. Turn it off in Mods.", Find<TextBlock>(w, "ErrorText").Text);
            Click("Hp2Back");
            Check("hp2 mods: Back to HP2's menu", Find<StackPanel>(w, "Hp2Panel").IsVisible && Find<Button>(w, "Hp2NewGame") != null);
            fired.Add("hp2:newer-engine");
            Click("TabHp1");
            Check("hp2 mods: HP1's list unchanged", Find<StackPanel>(w, "ReadyPanel").IsVisible && Find<TextBlock>(w, "ModsSummary").Text == "0 of 1 on"
                && Find<TextBlock>(w, "ModNewer_Hp1Mod") == null);
            w.Close();
            fired.Add("hp2:mods");
        }
        finally { try { Directory.Delete(tmp, true); } catch (IOException) { } }
    }
}
