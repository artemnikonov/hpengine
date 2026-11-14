using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using HPEngine.Launcher;
using HPEngine.Packaging;
using System.Security.Cryptography;

static partial class T
{
    static void Languages()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "hp1-language-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string game = Path.Combine(tmp, "game");
            void Put(string rel, string text)
            { string p = Path.Combine(game, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)); File.WriteAllText(p, text); }
            Put(".hp1-installed", "edition=European release: unverified\n");
            Put("Maps/Startup.unr", "map"); Put("Save/save0.hp1sav", "save");
            Put("System/0/Default.ini", "[Engine.Engine]\nLanguage=eng\n[Core.System]\nPaths=../System/*.u\n");
            // A different folder number and mixed-case filename prove that Finnish is discovered from the ini.
            Put("System/7/default.INI", "[Engine.Engine]\nLanguage=fin\n");
            Put("System/7/HPMenu.fin", "Finnish text"); Put("Sounds/7/AllDialog.fin_uax", "Finnish voices");
            Put("Sounds/0/AllDialog.eng_uax", "English voices"); Put("Sounds/AllDialog.uax", "original voices");
            Put("Textures/7/MenuArt.fin_utx", "Finnish art"); Put("Help/7/splashfin.bmp", "Finnish splash");
            Put("Textures/MenuArt.utx", "original art");
            string readOnly = Path.Combine(game, "Sounds", "7", "AllDialog.fin_uax");
            File.SetAttributes(readOnly, File.GetAttributes(readOnly) | FileAttributes.ReadOnly);
            Put("HPEngineData/Default.ini", "; settings\r\n[Engine.Engine]\r\nLanguage=eng\r\nLanguage=eng\r\n[Other]\r\nKeep=42\r\n");
            Put("System/not-a-language/Default.ini", "[Engine.Engine]\nLanguage=../bad\n");
            string data = Path.Combine(game, "HPEngineData");
            var originals = Directory.GetFiles(game, "*", SearchOption.AllDirectories)
                .Where(p => !p.StartsWith(data)).ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
            Seq("language codes read from data", new[] { "eng", "fin" }, GameLanguage.Available(game).Select(l => l.Code));
            Eq("no selection: unchanged game root", game, ModOverlay.Prepare(game, data).GameRoot);
            GameLanguage.Save(game, "fin");
            string saved = File.ReadAllText(Path.Combine(data, "Default.ini"));
            Check("language preserves comments, CRLF and other settings", saved.StartsWith("; settings\r\n") && saved.Contains("Keep=42\r\n"));
            Eq("one engine language key", 1, saved.Split('\n').Count(l => l.Trim() == "Language=fin"));
            Check("narrator selection saved", saved.Contains("CurLanguageName=FINNISH"));
            var prepared = ModOverlay.Prepare(game, data);
            Check("selected language creates a working root", prepared.GameRoot != game);
            Eq("Finnish engine configuration", "[Engine.Engine]\nLanguage=fin\n", File.ReadAllText(Path.Combine(prepared.GameRoot, "System", "Default.ini")));
            Eq("Finnish text", "Finnish text", File.ReadAllText(Path.Combine(prepared.GameRoot, "System", "HPMenu.fin")));
            Eq("Finnish dialogue canonical package", "Finnish voices", File.ReadAllText(Path.Combine(prepared.GameRoot, "Sounds", "AllDialog.uax")));
            Eq("Finnish menu art canonical package", "Finnish art", File.ReadAllText(Path.Combine(prepared.GameRoot, "Textures", "MenuArt.utx")));
            Check("Finnish splash present", File.Exists(Path.Combine(prepared.GameRoot, "Help", "splashfin.bmp")));
            Eq("language is not a script mod", 0, prepared.Active.Count);
            Eq("language files are not counted as mod replacements", 0, prepared.Replaced);
            fired.Add("language:overlay");
            Put("Mods/Translation/Sounds/AllDialog.uax", "mod voices");
            Put("HPEngineData/Mods.ini", "[Mods]\nEnabled=Translation\n");
            prepared = ModOverlay.Prepare(game, data);
            Eq("community mod wins over language", "mod voices", File.ReadAllText(Path.Combine(prepared.GameRoot, "Sounds", "AllDialog.uax")));
            Seq("mod identity unchanged by language", new[] { "Translation" }, prepared.Active);
            Eq("only the mod's own replacement is counted", 1, prepared.Replaced);
            Put("HPEngineData/Mods.ini", "[Mods]\n");
            GameLanguage.Save(game, "eng");
            prepared = ModOverlay.Prepare(game, data);
            Eq("switch back to English voices", "English voices", File.ReadAllText(Path.Combine(prepared.GameRoot, "Sounds", "AllDialog.uax")));
            Check("old Finnish root text removed on switch", !File.Exists(Path.Combine(prepared.GameRoot, "System", "HPMenu.fin")));
            GameLanguage.Save(game, null);
            Eq("default returns original root", game, ModOverlay.Prepare(game, data).GameRoot);
            Check("default removes engine/narrator overrides", !File.ReadAllText(Path.Combine(data, "Default.ini")).Contains("Language="));
            Check("every source file and save unchanged", originals.All(kv => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(kv.Key))) == kv.Value));
            Check("read-only source attribute unchanged", (File.GetAttributes(readOnly) & FileAttributes.ReadOnly) != 0);
            fired.Add("language:default");
            var window = new MainWindow(game, tmp, "0.2.0-beta", false); window.Show(); Dispatcher.UIThread.RunJobs();
            var box = Find<ComboBox>(window, "GameLanguage");
            Seq("chooser only installed languages", new[] { "Game default", "English", "Finnish" }, box.Items.Cast<string>());
            box.SelectedIndex = 2; Dispatcher.UIThread.RunJobs();
            Eq("chooser writes Finnish selection", "fin", GameLanguage.Selected(game));
            window.Close();
            window = new MainWindow(game, tmp, "0.2.0-beta", false); window.Show(); Dispatcher.UIThread.RunJobs();
            Eq("chooser restores selection after restart", 2, Find<ComboBox>(window, "GameLanguage").SelectedIndex);
            window.Close(); fired.Add("language:window");
            Directory.Move(Path.Combine(game, "System", "7"), Path.Combine(game, "System", "removed"));
            bool rejected = false;
            try { ModOverlay.Prepare(game, data); } catch (ModOverlayException e) { rejected = e.Message.Contains("no longer installed"); }
            Check("missing selected language fails visibly", rejected);
            rejected = false;
            try { GameLanguage.Save(game, "../bad"); } catch (InvalidDataException) { rejected = true; }
            Check("unknown language cannot be saved", rejected);
            window = new MainWindow(game, tmp, "0.2.0-beta", false); window.Show(); Dispatcher.UIThread.RunJobs();
            box = Find<ComboBox>(window, "GameLanguage");
            Eq("removed selection is not silently reset", "Unavailable (fin)", box.SelectedItem);
            box.SelectedIndex = 0; Dispatcher.UIThread.RunJobs();
            Eq("default recovers from removed language", null, GameLanguage.Selected(game));
            window.Close(); fired.Add("language:missing");
            var nordic = new[] { "dan", "eng", "fin", "nor", "swe" }
                .Select(c => new LanguageChoice(c, c, "0")).ToList();
            var display = EditionLabel.Describe("UK 1.1: modified", nordic);
            Eq("Nordic label describes data rather than claiming a UK disc", "Nordic multilingual data", display.Name);
            Eq("manifest difference is not called a modified disc", "different data", display.Tier);
            Check("engine family retained as separate information", display.Detail.StartsWith("Engine family: UK 1.1."));
            Eq("other multilingual sets are not labelled Nordic", "Multilingual game data", EditionLabel.Describe("UK 1.1: modified", nordic.Take(2).ToList()).Name);
            Eq("single-language edition keeps original name", "US 1.53", EditionLabel.Describe("US 1.53: exact", nordic.Take(1).ToList()).Name);
            Eq("single-language exact support is retained", "exact", EditionLabel.Describe("US 1.53: exact", nordic.Take(1).ToList()).Tier);
            Eq("known modified releases keep the modified warning", "modified", EditionLabel.Describe("Modded re-release: modified", nordic).Tier);
            foreach (var (code, folder) in new[] { ("dan", "1"), ("fin", "2"), ("nor", "4"), ("swe", "3") })
                Put("System/" + folder + "/Default.ini", "[Engine.Engine]\nLanguage=" + code + "\n");
            Put(".hp1-installed", "edition=UK 1.1: modified\n");
            window = new MainWindow(game, tmp, "0.2.0-beta", false); window.Show(); Dispatcher.UIThread.RunJobs();
            Eq("window shows Nordic data label", "Nordic multilingual data", Find<TextBlock>(window, "EditionText").Text);
            Eq("window uses neutral manifest-difference chip", "different data", Find<TextBlock>(window, "EditionChip").Text);
            Check("window exposes engine family separately", Find<TextBlock>(window, "EditionDetail").IsVisible && Find<TextBlock>(window, "EditionDetail").Text.Contains("UK 1.1"));
            window.Close(); fired.Add("language:edition");
        }
        finally
        {
            foreach (string file in Directory.GetFiles(tmp, "*", SearchOption.AllDirectories))
                if ((File.GetAttributes(file) & FileAttributes.ReadOnly) != 0) File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            Directory.Delete(tmp, true);
        }
    }

    static void LanguageShot(string game, string path)
    {
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        var window = new MainWindow(game, Path.GetDirectoryName(game), "0.2.0-beta (language preview)", false);
        window.Show(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame().Save(path); window.Close();
    }
}
