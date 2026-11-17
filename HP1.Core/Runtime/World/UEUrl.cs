// FURL — LOCAL SUBSET (boot A10/A11).
//
// Shipped (Engine.dll.c):
//   FURL::FURL(Base, TextURL, Type) 147942-148420:
//     TRAVEL_Relative (2): Protocol/Host/Map/Portal/Port are copied from Base;
//     TRAVEL_Relative and TRAVEL_Partial (1): Base options are copied by PLAIN append, except PUSH/POP/PEER/LOAD
//       (appStricmp, LAB_1042e4aa);
//     then the text: leading spaces are skipped; from the first '?' or '#' come segments — '?' -> AddOption,
//       '#' -> Portal; a space inside a segment invalidates the URL; the remainder on the left is the map.
//   FURL::AddOption: the key up to and including '=' (without '=' — the whole string with its terminator) is
//     compared with appStrnicmp; a match replaces, otherwise appended.
//   FURL::HasOption: appStricmp of the whole option.
//   FURL::LoadURLConfig 123621: every line of the section — AddOption.
//   Options string for InitGame/Login (247220-247235): "?" + option, consecutively.
//
//   FURL::FURL(NULL) / the Base-Text ctor start (Engine.dll.c:328446-328530): Protocol, Host, Port, Map, Portal are the
//     FURL::Default* statics (FURL::StaticInit 95287: GConfig->GetStr("URL", <key>) of the system ini, from
//     UEngine::Init), so Absolute/Partial text without a map keeps Map = DefaultMap (B17).
//   TRAVEL_Absolute with a Base and an internal URL (LAB_1042eee9, Engine.dll.c:329218-329350): every Base option that
//     starts with Name=/Team=/Class=/Skin=/Face=/Voice=/OverrideClass= (appStrnicmp) and whose bare name the new URL has
//     no option starting with (GetOption(name, "") == "") is appended (Op.AddItem, not AddOption).
//
// Network forms (protocol, host, "//", a dot not before a map or save extension) are not needed by the port and give a
// loud refusal, not a plausible URL.
using System;
using System.Collections.Generic;

namespace HP1.UEScript
{
    public sealed class UEUrl
    {
        public const int Absolute = 0, Partial = 1, Relative = 2;

        // FURL::DefaultMap / DefaultMapExt (FURL::StaticInit: [URL] Map / MapExt of the system ini; a missing key is "",
        // GetStr's answer). null until StaticInit ran: a map-less Absolute/Partial URL then refuses loudly.
        public static string DefaultMap { get; private set; }
        public static string DefaultMapExt { get; private set; }
        public static string DefaultSaveExt { get; private set; }
        public static string DefaultProtocol { get; private set; }
        public static string DefaultPortal { get; private set; }
        // FURL::DefaultHost / DefaultPort ([URL] Host, appAtoi([URL] Port)). The port's URLs are all local (network forms
        // refused), so every URL keeps DefaultPort; Host is DefaultHost, "" after a "//" host form, the base's when Relative.
        public static string DefaultHost { get; private set; }
        public static int DefaultPort { get; private set; }

        // Original: Engine.dll.c:95287 FURL::StaticInit (export 0x33b9), the [URL] keys the port reads.
        public static void StaticInit(UEConfig config)
        {
            string Get(string key) => config.TryGetIni(UEConfig.ScopeSystem, "URL", key, out string v) ? v : "";
            DefaultMap = Get("Map");
            DefaultMapExt = Get("MapExt");
            DefaultSaveExt = Get("SaveExt");
            DefaultProtocol = Get("Protocol");
            DefaultPortal = Get("Portal");
            DefaultHost = Get("Host");
            DefaultPort = UEValue.LeadingAtoi(Get("Port"));
        }

        public string Map = DefaultMap ?? "";
        public string Portal = "";
        public string Host = DefaultHost ?? "";
        public readonly List<string> Ops = new();

        public UEUrl Copy()
        {
            var u = new UEUrl { Map = Map, Portal = Portal, Host = Host };
            u.Ops.AddRange(Ops);
            return u;
        }

        public string OptionsString
        {
            get
            {
                var sb = new System.Text.StringBuilder();
                foreach (string op in Ops) sb.Append('?').Append(op);
                return sb.ToString();
            }
        }

        // FURL::String(FullyQualified=0) (Engine.dll.c:78851-79026) for a
        // local URL: protocol and host are default, the port is DefaultPort,
        // so neither "proto:" nor "host:port/" is printed (jump to
        // LAB_1042fc82): then Map (if non-empty), "?"+option for each, and "#"+Portal if the portal is non-empty
        // (so neither "proto:" nor "host:port/" is printed).
        public string String()
        {
            var sb = new System.Text.StringBuilder(Map);
            foreach (string op in Ops) sb.Append('?').Append(op);
            if (Portal.Length != 0) sb.Append('#').Append(Portal);
            return sb.ToString();
        }

        public void AddOption(string str)
        {
            int eq = str.IndexOf('=');
            int match = eq >= 0 ? eq + 1 : str.Length + 1;
            for (int i = 0; i < Ops.Count; i++)
                if (StrnicmpZero(Ops[i], str, match)) { Ops[i] = str; return; }
            Ops.Add(str);
        }

        // appStrnicmp over C strings: the terminator takes part in the comparison.
        static bool StrnicmpZero(string a, string b, int n)
        {
            for (int k = 0; k < n; k++)
            {
                char ca = k < a.Length ? a[k] : '\0', cb = k < b.Length ? b[k] : '\0';
                if (char.ToUpperInvariant(ca) != char.ToUpperInvariant(cb)) return false;
                if (ca == '\0') return true;
            }
            return true;
        }

        // FURL::GetOption (Engine.dll.c:132121, export 0x4354): the first option whose first strlen(Match) characters
        // equal Match (appStrnicmp) — the rest of it; none -> Default.
        public string GetOption(string match, string fallback)
        {
            foreach (string op in Ops)
                if (op.Length >= match.Length && StrnicmpZero(op, match, match.Length)) return op.Substring(match.Length);
            return fallback;
        }

        // FURL::SaveURLConfig (Engine.dll.c:94656): every option "Key=Value" whose Key equals Item (appStricmp) is written
        // to the section (GConfig SetString +0x30).
        public void SaveURLConfig(UEConfig config, string scope, string section, string item)
        {
            foreach (string op in Ops)
            {
                int eq = op.IndexOf('=');
                if (eq < 0) continue;
                string key = op.Substring(0, eq);
                if (string.Equals(key, item, StringComparison.OrdinalIgnoreCase)) config.Set(scope, section, key, op.Substring(eq + 1));
            }
        }

        public bool HasOption(string name)
        {
            foreach (string op in Ops)
                if (string.Equals(op, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // String "?a?b" (as PlayWorldConfig.Options holds it) — into options.
        public static UEUrl FromOptions(string map, string portal, string options)
        {
            var u = new UEUrl { Map = map ?? "", Portal = portal ?? "" };
            if (!string.IsNullOrEmpty(options))
                foreach (string part in options.Split('?'))
                    if (part.Length > 0) u.Ops.Add(part);
            return u;
        }

        public static UEUrl Parse(UEUrl baseUrl, string text, int type)
        {
            if (text is null) throw new ArgumentNullException(nameof(text));
            var u = new UEUrl();
            if (type == Relative || type == Partial)
            {
                if (baseUrl is null)
                    throw new InvalidOperationException($"FURL: check(Base) — {(type == Relative ? "Relative" : "Partial")} URL \"{text}\" without a base");
                if (type == Relative) { u.Map = baseUrl.Map; u.Portal = baseUrl.Portal; u.Host = baseUrl.Host; }
                foreach (string op in baseUrl.Ops)
                    if (!IsOneOf(op, "PUSH", "POP", "PEER", "LOAD")) u.Ops.Add(op);
            }
            string all = text.TrimStart(' ');
            string s = all;
            int cut = FirstOf(all, 0);
            if (cut >= 0)
            {
                s = all.Substring(0, cut);
                char kind = all[cut];
                int pos = cut + 1;
                while (true)
                {
                    int next = FirstOf(all, pos);
                    string seg = next < 0 ? all.Substring(pos) : all.Substring(pos, next - pos);
                    if (seg.IndexOf(' ') >= 0) throw Invalid(text, "a space in an option or portal segment");
                    if (kind == '?') u.AddOption(seg); else u.Portal = seg;
                    if (next < 0) break;
                    kind = all[next];
                    pos = next + 1;
                }
            }
            // Protocol and host (Engine.dll.c:328822-328960). Not a drive path ("C:..."): a ':' after the second character
            // with no '.' before it ends the protocol; then "//" starts a host form (Host = ""), a single '/' makes the
            // URL invalid; a '.' after the first character not followed by MapExt/SaveExt + a non-alphanumeric is a
            // network host. A drive path skips all of it (Engine.dll.c:329060-329216): Map = the whole text, Portal =
            // DefaultPortal, a host form with no map left to read.
            string protocol = DefaultProtocol ?? "";
            bool hostForm = false, drivePath = !(s.Length < 3 || s[1] != ':');
            if (!drivePath)
            {
                int colon = s.IndexOf(':'), dot = s.IndexOf('.');
                if (colon > 1 && (dot < 0 || dot > colon)) { protocol = s.Substring(0, colon); s = s.Substring(colon + 1); }
                if (s.StartsWith("/", StringComparison.Ordinal))
                {
                    if (!s.StartsWith("//", StringComparison.Ordinal)) throw Invalid(text, "a single '/' before the map");
                    hostForm = true;
                    u.Host = "";
                    s = s.Substring(2);
                }
                if (NonLocalDot(s))
                    throw new NotSupportedException($"FURL \"{text}\": network form (host) not ported");
            }
            bool internalUrl = string.Equals(protocol, DefaultProtocol ?? "", StringComparison.OrdinalIgnoreCase);   // FURL::IsInternal
            if (!internalUrl)
                throw new NotSupportedException($"FURL \"{text}\": network form (protocol {protocol}) not ported");
            // Original: Engine.dll.c:329218 (LAB_1042eee9) — before the map is taken from the text.
            if (type == Absolute && baseUrl is not null)
                foreach (string op in baseUrl.Ops)
                {
                    // The key is appStrncpy(key, op, idx('=') + 1): MaxLen counts the terminator, so the '=' is cut and
                    // GetOption matches any option that starts with the bare name ("Name" also finds "NameX=1").
                    if (IsPersistentOption(op) && u.GetOption(op.Substring(0, op.IndexOf('=')), "").Length == 0) u.Ops.Add(op);
                }
            bool mapGiven = s.Length > 0;
            if (drivePath)
            {
                u.Map = s;
                u.Portal = DefaultPortal ?? "";
                hostForm = true;
            }
            else if (mapGiven)
            {
                // Local map "Map/Portal" (Engine.dll.c:329354-329420): the first '/' separates the portal — it REPLACES
                // the portal from '#'; the second '/' truncates the portal, and a non-empty tail after it invalidates
                // the URL.
                int slash = s.IndexOf('/');
                if (slash >= 0)
                {
                    string portal = s.Substring(slash + 1);
                    s = s.Substring(0, slash);
                    int slash2 = portal.IndexOf('/');
                    if (slash2 >= 0)
                    {
                        if (slash2 + 1 < portal.Length) throw Invalid(text, "a second '/' with a tail");
                        portal = portal.Substring(0, slash2);
                    }
                    u.Portal = portal;
                }
                u.Map = s;
            }
            else if (type != Relative && DefaultMap is null)
                throw new InvalidOperationException($"FURL \"{text}\": no map in the text and FURL::StaticInit has not run (DefaultMap)");
            // Body tail (Engine.dll.c:329420-329450): a space in Protocol, Host or Portal, or neither a host form, nor a
            // map in the text, nor an option, makes the URL invalid.
            if (protocol.IndexOf(' ') >= 0 || u.Portal.IndexOf(' ') >= 0) throw Invalid(text, "a space in the protocol or portal");
            if (!hostForm && !mapGiven && u.Ops.Count == 0) throw Invalid(text, "no map and no option");
            return u;
        }

        // An invalid URL (Valid = 0, every field back to FURL(NULL)): the port's callers do not carry one — loud.
        static NotSupportedException Invalid(string text, string why) =>
            new($"FURL \"{text}\": {why} — shipped makes the URL invalid");

        static int FirstOf(string s, int from)
        {
            int q = s.IndexOf('?', from), h = s.IndexOf('#', from);
            if (q < 0) return h;
            if (h < 0) return q;
            return Math.Min(q, h);
        }

        // A dot NOT followed by DefaultMapExt or DefaultSaveExt with a non-alphanumeric character after it — marks a host.
        static bool NonLocalDot(string s)
        {
            int dot = s.IndexOf('.');
            if (dot <= 0) return false;
            foreach (string ext in new[] { DefaultMapExt ?? "", DefaultSaveExt ?? "" })
                if (s.Length >= dot + 1 + ext.Length
                    && string.Compare(s, dot + 1, ext, 0, ext.Length, StringComparison.OrdinalIgnoreCase) == 0
                    && (s.Length == dot + 1 + ext.Length || !char.IsLetterOrDigit(s[dot + 1 + ext.Length])))
                    return false;
            return true;
        }

        static bool IsPersistentOption(string op)
        {
            foreach (string p in new[] { "Name=", "Team=", "Class=", "Skin=", "Face=", "Voice=", "OverrideClass=" })
                if (op.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static bool IsOneOf(string op, params string[] names)
        {
            foreach (string n in names)
                if (string.Equals(op, n, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
