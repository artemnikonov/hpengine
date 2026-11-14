// NOT ORIGINAL: choose data already present in a multilingual distribution. The original files stay unchanged;
// ModOverlay places this language first in its working copy, then applies community mods over it.
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
            ["eng"] = "English", ["int"] = "English", ["fin"] = "Finnish", ["dan"] = "Danish",
            ["swe"] = "Swedish", ["nor"] = "Norwegian", ["fra"] = "French", ["fre"] = "French",
            ["deu"] = "German", ["ger"] = "German", ["ita"] = "Italian", ["esn"] = "Spanish",
            ["esp"] = "Spanish", ["rus"] = "Russian", ["pol"] = "Polish", ["hun"] = "Hungarian",
            ["por"] = "Portuguese", ["dut"] = "Dutch", ["nld"] = "Dutch", ["cze"] = "Czech",
        };

        static string FindDir(string parent, string name) => Directory.Exists(parent)
            ? Directory.GetDirectories(parent).FirstOrDefault(p => Path.GetFileName(p).Equals(name, StringComparison.OrdinalIgnoreCase)) : null;
        static string FindFile(string parent, string name) => parent != null && Directory.Exists(parent)
            ? Directory.GetFiles(parent).FirstOrDefault(p => Path.GetFileName(p).Equals(name, StringComparison.OrdinalIgnoreCase)) : null;
        static string Settings(string install) => Path.Combine(install, InstallRoot.DataDirName, "Default.ini");
        static bool Code(string value) => value is { Length: >= 2 and <= 4 } && value.All(c => c is >= 'a' and <= 'z');

        internal static string Read(string text, string section, string key)
        {
            bool inside = false; string value = null;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Split(';')[0].Trim();
                if (line.StartsWith('[')) { inside = line.Equals("[" + section + "]", StringComparison.OrdinalIgnoreCase); continue; }
                int eq = line.IndexOf('=');
                if (inside && eq > 0 && line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) value = line[(eq + 1)..].Trim();
            }
            return value;
        }

        // The language code comes from the distribution's ini, never from a presumed folder-number table.
        public static List<LanguageChoice> Available(string install)
        {
            string system = FindDir(install, "System");
            if (system == null) return new();
            var choices = new List<LanguageChoice>();
            foreach (string dir in new[] { system }.Concat(Directory.GetDirectories(system)
                .Where(p => Path.GetFileName(p).All(char.IsAsciiDigit)).OrderBy(p => p, StringComparer.Ordinal)))
            {
                string ini = FindFile(dir, "Default.ini");
                string code = ini == null ? null : Read(File.ReadAllText(ini), "Engine.Engine", "Language")?.ToLowerInvariant();
                if (!Code(code) || choices.Any(c => c.Code == code)) continue;
                choices.Add(new(code, Names.GetValueOrDefault(code, code), dir == system ? null : Path.GetFileName(dir)));
            }
            return choices.OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static string Selected(string install) => File.Exists(Settings(install))
            ? Read(File.ReadAllText(Settings(install)), "HPEngine", SelectionKey) : null;

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
            string text = File.Exists(path) ? File.ReadAllText(path) : "";
            text = Set(text, "HPEngine", SelectionKey, choice?.Code);
            text = Set(text, "Engine.Engine", "Language", choice?.Code);
            // baseNarrator has its own language name as well as the engine's three-letter code.
            text = Set(text, "HPBase.baseNarrator", "CurLanguageName", choice != null && Names.TryGetValue(choice.Code, out string name) ? name.ToUpperInvariant() : null);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".language-new";
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            File.Move(temp, path, true);
        }

        // Selected folder's config/localization, voice packages, menu art and splash. Package suffixes such as
        // AllDialog.fin_uax become AllDialog.uax, the installed filename the game opens. No game files are generated.
        public static List<(string Source, string Rel)> Files(string install)
        {
            string code = Selected(install);
            if (string.IsNullOrEmpty(code)) return new();
            var choice = Available(install).FirstOrDefault(c => c.Code == code)
                ?? throw new ModOverlayException("selected language '" + code + "' is no longer installed; choose another language in the Launcher");
            var files = new List<(string, string)>();
            foreach (string name in new[] { "System", "Sounds", "Textures", "Help" })
            {
                string top = FindDir(install, name);
                string lang = top == null ? null : choice.Folder == null ? top : FindDir(top, choice.Folder);
                if (lang == null) continue;
                foreach (string file in Directory.GetFiles(lang))
                {
                    string leaf = Path.GetFileName(file);
                    bool package = false;
                    foreach (string ext in new[] { "uax", "utx" })
                        if (leaf.EndsWith("." + code + "_" + ext, StringComparison.OrdinalIgnoreCase))
                        { leaf = leaf[..^(code.Length + ext.Length + 2)] + "." + ext; package = true; break; }
                    bool localized = Path.GetExtension(file).Equals("." + code, StringComparison.OrdinalIgnoreCase);
                    bool config = name == "System" && leaf.Equals("Default.ini", StringComparison.OrdinalIgnoreCase);
                    bool splash = name == "Help" && leaf.Equals("splash" + code + ".bmp", StringComparison.OrdinalIgnoreCase);
                    if (package || localized || config || splash) files.Add((file, Path.Combine(name, leaf)));
                }
            }
            return files;
        }
    }
}
