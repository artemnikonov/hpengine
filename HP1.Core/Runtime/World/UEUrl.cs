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
// Network forms (protocol, host, "//", a dot not before a map or save extension) and Absolute/Partial without a map
// in the text are not needed by the port and give a loud refusal, not a plausible URL.
using System;
using System.Collections.Generic;

namespace HP1.UEScript
{
    public sealed class UEUrl
    {
        public const int Absolute = 0, Partial = 1, Relative = 2;

        public string Map = "";
        public string Portal = "";
        public readonly List<string> Ops = new();

        public UEUrl Copy()
        {
            var u = new UEUrl { Map = Map, Portal = Portal };
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
                if (type == Relative) { u.Map = baseUrl.Map; u.Portal = baseUrl.Portal; }
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
                    if (seg.IndexOf(' ') >= 0)
                        throw new NotSupportedException($"FURL \"{text}\": space in an option — shipped makes the URL invalid");
                    if (kind == '?') u.AddOption(seg); else u.Portal = seg;
                    if (next < 0) break;
                    kind = all[next];
                    pos = next + 1;
                }
            }
            if (s.IndexOf(':') >= 0 || s.StartsWith("/", StringComparison.Ordinal) || NonLocalDot(s))
                throw new NotSupportedException($"FURL \"{text}\": network form (protocol/host) not ported");
            // Local map "Map/Portal" (Engine.dll.c 328403+951..1016, FURL::FURL 0x1042e100): the first '/' separates
            // the portal — it REPLACES the portal from '#'; the second '/' truncates the portal, and a non-empty tail
            // after it invalidates the URL. A space in the portal does too (body tail).
            int slash = s.IndexOf('/');
            if (slash >= 0)
            {
                string portal = s.Substring(slash + 1);
                s = s.Substring(0, slash);
                int slash2 = portal.IndexOf('/');
                if (slash2 >= 0)
                {
                    if (slash2 + 1 < portal.Length)
                        throw new NotSupportedException($"FURL \"{text}\": second '/' with a tail — shipped makes the URL invalid");
                    portal = portal.Substring(0, slash2);
                }
                if (portal.IndexOf(' ') >= 0)
                    throw new NotSupportedException($"FURL \"{text}\": space in the portal — shipped makes the URL invalid");
                u.Portal = portal;
            }
            if (s.Length > 0) u.Map = s;
            else if (type != Relative)
                throw new NotSupportedException($"FURL \"{text}\": default map for Absolute/Partial not ported");
            return u;
        }

        static int FirstOf(string s, int from)
        {
            int q = s.IndexOf('?', from), h = s.IndexOf('#', from);
            if (q < 0) return h;
            if (h < 0) return q;
            return Math.Min(q, h);
        }

        // A dot NOT followed by a map (unr) or save (usa) extension with a non-alphanumeric character after it —
        // marks a host.
        static bool NonLocalDot(string s)
        {
            int dot = s.IndexOf('.');
            if (dot <= 0) return false;
            foreach (string ext in new[] { "unr", "usa" })
                if (s.Length >= dot + 1 + ext.Length
                    && string.Compare(s, dot + 1, ext, 0, ext.Length, StringComparison.OrdinalIgnoreCase) == 0
                    && (s.Length == dot + 1 + ext.Length || !char.IsLetterOrDigit(s[dot + 1 + ext.Length])))
                    return false;
            return true;
        }

        static bool IsOneOf(string op, params string[] names)
        {
            foreach (string n in names)
                if (string.Equals(op, n, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
