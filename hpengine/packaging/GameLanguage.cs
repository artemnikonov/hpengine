// NOT ORIGINAL: choose data already present in a multilingual distribution. The original files stay unchanged;
// ModOverlay places this language first in its working copy, then applies community mods over it.
// Which package file the game opens stays Core's rule (UEInstallPath.Packages.cs): with [Engine.Engine] Language=<code>
// it opens <dir>/<Name>.<code>_<ext> before <dir>/<Name>.<ext>, so a language folder's packages are placed under their own
// names in the top directory (Sounds/7/AllDialog.fin_uax -> Sounds/AllDialog.fin_uax), never renamed to the plain name.
// Core reads only System/ for Default.ini and *.<code> text (DefaultIniPath, UELocalization.SystemDir) and Help/ for the
// splash, so those move from the numbered folder to the top one.
// The launcher does not reference HP1.Core: this small reader follows FConfigFile::Read (UEIniFile: no comments, no
// trimming, quotes stripped, the last duplicate wins) and writes ISO-8859-1, as Core's UEConfig.FileStore does.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace HPEngine.Packaging
{
    public sealed record LanguageChoice(string Code, string Label, string Folder)
    {
        public override string ToString() => Label;
    }

    public static class GameLanguage
    {
        public const string SelectionKey = "DataLanguage";
        public const string DefaultLabel = "Game default";
        static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
        {
            ["eng"] = "English",
            ["int"] = "English",
            ["fin"] = "Finnish",
            ["dan"] = "Danish",
            ["swe"] = "Swedish",
            ["nor"] = "Norwegian",
            ["fra"] = "French",
            ["fre"] = "French",
            ["deu"] = "German",
            ["ger"] = "German",
            ["ita"] = "Italian",
            ["esn"] = "Spanish",
            ["esp"] = "Spanish",
            ["rus"] = "Russian",
            ["pol"] = "Polish",
            ["hun"] = "Hungarian",
            ["por"] = "Portuguese",
            ["dut"] = "Dutch",
            ["nld"] = "Dutch",
            ["cze"] = "Czech",
        };

        static string Settings(string install) => Path.Combine(install, InstallRoot.DataDirName, "Default.ini");
        static bool Code(string value) => value is { Length: >= 2 and <= 4 } && value.All(c => c is >= 'a' and <= 'z');

        // appLoadFileToString: UTF-16 with a BOM, otherwise ISO-8859-1 (UELocalization.DecodeFileBytes).
        internal static string Load(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            if (b.Length > 1 && (b.Length & 1) == 0 && b[0] == 0xff && b[1] == 0xfe) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
            if (b.Length > 1 && (b.Length & 1) == 0 && b[0] == 0xfe && b[1] == 0xff) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
            return Encoding.Latin1.GetString(b);
        }

        internal static string Read(string text, string section, string key)
        {
            bool inside = false; string value = null;
            foreach (string line in text.Split('\r', '\n'))
            {
                if (line.Length == 0) continue;
                if (line[0] == '[' && line[^1] == ']') { inside = line[1..^1].Equals(section, StringComparison.OrdinalIgnoreCase); continue; }
                int eq = line.IndexOf('=');
                if (!inside || eq < 0 || !line[..eq].Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                value = line[(eq + 1)..];
                if (value.Length > 1 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
            }
            return value;
        }

        // The language code comes from the distribution's ini, never from a presumed folder-number table.
        public static List<LanguageChoice> Available(string install)
        {
            string system = ModOverlay.FindDir(install, "System");
            if (system == null) return new();
            var choices = new List<LanguageChoice>();
            foreach (string dir in new[] { system }.Concat(Directory.GetDirectories(system)
                .Where(p => Path.GetFileName(p).All(char.IsAsciiDigit)).OrderBy(p => p, StringComparer.Ordinal)))
            {
                string ini = ModOverlay.FindFile(dir, "Default.ini");
                string code = ini == null ? null : Read(Load(ini), "Engine.Engine", "Language")?.ToLowerInvariant();
                if (!Code(code) || choices.Any(c => c.Code == code)) continue;
                choices.Add(new(code, Names.GetValueOrDefault(code, code), dir == system ? null : Path.GetFileName(dir)));
            }
            return choices.OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static string Selected(string install) => File.Exists(Settings(install))
            ? Read(Load(Settings(install)), "HPEngine", SelectionKey) : null;

        // Only the writable settings overlay changes. Other settings, comments and line endings are retained.
        internal static string Set(string text, string section, string key, string value)
        {
            string nl = text.Contains("\r\n") ? "\r\n" : "\n";
            var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (lines.Count > 0 && lines[^1] == "") lines.RemoveAt(lines.Count - 1);
            var result = new List<string>(); bool inside = false, placed = false, found = false;
            void Place() { if (!placed && value != null) result.Add(key + "=" + value); placed = true; }
            foreach (string line in lines)
            {
                string t = line.Trim();
                if (t.StartsWith('['))
                {
                    if (inside) Place();
                    inside = t.Equals("[" + section + "]", StringComparison.OrdinalIgnoreCase); found |= inside;
                }
                int eq = t.IndexOf('=');
                if (inside && eq > 0 && t[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) { Place(); continue; }
                result.Add(line);
            }
            if (inside) Place();
            if (!found && value != null) { if (result.Count > 0) result.Add(""); result.Add("[" + section + "]"); result.Add(key + "=" + value); }
            return string.Join(nl, result) + nl;
        }

        public static void Save(string install, string code)
        {
            LanguageChoice choice = code == null ? null : Available(install).FirstOrDefault(c => c.Code == code)
                ?? throw new InvalidDataException("The selected language is no longer installed: " + code);
            string path = Settings(install);
            string text = File.Exists(path) ? Load(path) : "";
            text = Set(text, "HPEngine", SelectionKey, choice?.Code);
            text = Set(text, "Engine.Engine", "Language", choice?.Code);
            // baseNarrator.SetLanguage stores Caps(Name) in its globalconfig CurLanguageName (HPBase baseNarrator.uc).
            text = Set(text, "HPBase.baseNarrator", "CurLanguageName", choice != null && Names.TryGetValue(choice.Code, out string name) ? name.ToUpperInvariant() : null);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".language-new";
            File.WriteAllText(temp, text, Encoding.Latin1);   // UEConfig.FileStore's encoding (codepage 28591)
            File.Move(temp, path, true);
        }

        // Selected folder's Default.ini and *.<code> text (to System/), its <Name>.<code>_<ext> packages under their own
        // names (Core opens them for Language=<code>), and its splash<code>.bmp (to Help/). No game files are generated.
        public static List<(string Source, string Rel)> Files(string install)
        {
            string code = Selected(install);
            if (string.IsNullOrEmpty(code)) return new();
            var choice = Available(install).FirstOrDefault(c => c.Code == code)
                ?? throw new ModOverlayException("selected language '" + code + "' is no longer installed; choose another language in the Launcher");
            var files = new List<(string, string)>();
            foreach (string name in new[] { "System", "Sounds", "Textures", "Help" })
            {
                string top = ModOverlay.FindDir(install, name);
                string lang = top == null ? null : choice.Folder == null ? top : ModOverlay.FindDir(top, choice.Folder);
                if (lang == null) continue;
                foreach (string file in Directory.GetFiles(lang))
                {
                    string leaf = Path.GetFileName(file), ext = Path.GetExtension(leaf);
                    bool package = ext.StartsWith("." + code + "_", StringComparison.OrdinalIgnoreCase);
                    bool localized = ext.Equals("." + code, StringComparison.OrdinalIgnoreCase);
                    bool config = name == "System" && leaf.Equals("Default.ini", StringComparison.OrdinalIgnoreCase);
                    bool splash = name == "Help" && leaf.Equals("splash" + code + ".bmp", StringComparison.OrdinalIgnoreCase);
                    if (package || localized || config || splash) files.Add((file, Path.Combine(name, leaf)));
                }
            }
            return files;
        }
    }
}
