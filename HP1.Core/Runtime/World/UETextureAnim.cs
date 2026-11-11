// TEXTURE ANIMATION CHAINS FROM THE USER'S PACKAGES.
//
// Data: a texture's AnimNext / MinFrameRate / MaxFrameRate. `BspDiffRunner --exportanim` serializes this Core result
// (texture_anim.tsv format). Export rules: packages Textures/System/Maps in file-name order, the class is a Texture
// descendant by the System/*.u tree, the first path wins, AnimNext is the raw object index (compact index) with an
// export or import path, Min/Max are tag strings ("" without a tag).
//
// Frame stepping — UTexture::Tick (Engine.dll.c:70100): with MaxFrameRate==0 the frame changes every engine frame;
// otherwise Min/Max are clamped to [0.1,100], accumulator += dt, at accumulator ≥ 1/Max — a step (if accumulator
// < 1/Min — reset to 0, else −1/Min capped at 1/Min). Presentation only: does not affect game state.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UELib;
using UELib.Core;

namespace HP1.UEScript
{
    public sealed class UETextureAnim
    {
        public sealed class Entry
        {
            public string Path, Next, MinS, MaxS;   // strings as in the export
            public float Min, Max;                   // parsed values (non-number — 0)
        }

        readonly string _root;
        List<string> _packages;
        HashSet<string> _textureClasses;
        readonly Dictionary<string, UnrealPackage> _open = new(StringComparer.Ordinal);
        readonly Dictionary<string, Entry> _byPath = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> SkippedPackages = new();

        public UETextureAnim(string installRoot)
        {
            _root = installRoot ?? throw new ArgumentNullException(nameof(installRoot));
        }

        List<string> Packages
        {
            get
            {
                if (_packages is not null) return _packages;
                var list = new List<string>();
                foreach (string sub in new[] { "Textures", "System", "Maps" })
                {
                    string dir = System.IO.Path.Combine(_root, sub);
                    if (!Directory.Exists(dir)) continue;
                    foreach (string pat in new[] { "*.utx", "*.u", "*.unr" })
                        list.AddRange(Directory.GetFiles(dir, pat));
                }
                return _packages = list.Distinct(StringComparer.Ordinal)
                    .OrderBy(x => System.IO.Path.GetFileName(x), StringComparer.Ordinal).ToList();
            }
        }

        HashSet<string> TextureClasses
        {
            get
            {
                if (_textureClasses is not null) return _textureClasses;
                var super = new Dictionary<string, string>(StringComparer.Ordinal);
                string sysDir = System.IO.Path.Combine(_root, "System");
                foreach (string pkgPath in Directory.GetFiles(sysDir, "*.u").OrderBy(x => System.IO.Path.GetFileName(x), StringComparer.Ordinal))
                {
                    UnrealPackage pkg = Open(pkgPath);
                    if (pkg is null) continue;
                    foreach (var exp in pkg.Exports)
                    {
                        if (exp.Object is not UClass c) continue;
                        string sup = c.Super?.Name;
                        if (!string.IsNullOrEmpty(sup)) super[exp.ObjectName.ToString()] = sup;
                    }
                }
                var r = new HashSet<string>(StringComparer.Ordinal);
                foreach (string name in super.Keys)
                {
                    string cur = name;
                    for (int hops = 0; hops <= super.Count; hops++)
                    {
                        if (cur == "Texture") { r.Add(name); break; }
                        if (!super.TryGetValue(cur, out string next)) break;
                        cur = next;
                    }
                }
                // The root is included if it is in the inheritance table (export rule DerivedFromCore).
                if (super.ContainsKey("Texture") || super.ContainsValue("Texture")) r.Add("Texture");
                return _textureClasses = r;
            }
        }

        UnrealPackage Open(string pkgPath)
        {
            if (_open.TryGetValue(pkgPath, out var pkg)) return pkg;
            try
            {
                pkg = UnrealLoader.LoadPackage(pkgPath, FileAccess.Read);
                pkg.InitializePackage(UnrealPackage.InitFlags.All);
            }
            catch (Exception e)
            {
                SkippedPackages.Add($"{System.IO.Path.GetFileName(pkgPath)}: {e.Message}");
                pkg = null;
            }
            _open[pkgPath] = pkg;
            return pkg;
        }

        // Texture with a non-empty AnimNext; null — not animated (or the object was not read).
        Entry EntryOf(UnrealPackage pkg, string pkgName, UExportTableItem exp, string path)
        {
            if (exp.Object is not UObject obj) return null;
            try { obj.BeginDeserializing(); } catch { return null; }
            int refIndex = 0;
            foreach (var kv in obj.GetRawPropertyValueBytesAll("AnimNext"))
            { int p = 0; refIndex = ReadCompactIndex(kv.Value, ref p); break; }
            if (refIndex == 0) return null;
            var e = new Entry
            {
                Path = path,
                Next = ObjectPathOfRef(pkg, pkgName, refIndex),
                MinS = obj.Properties?.Find("MinFrameRate")?.Value ?? "",
                MaxS = obj.Properties?.Find("MaxFrameRate")?.Value ?? "",
            };
            float.TryParse(e.MinS, NumberStyles.Float, CultureInfo.InvariantCulture, out e.Min);
            float.TryParse(e.MaxS, NumberStyles.Float, CultureInfo.InvariantCulture, out e.Max);
            return e;
        }

        // All animated shipped textures in export order.
        public IEnumerable<Entry> All()
        {
            HashSet<string> classes = TextureClasses;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string pkgPath in Packages)
            {
                UnrealPackage pkg = Open(pkgPath);
                if (pkg is null) continue;
                string pkgName = System.IO.Path.GetFileNameWithoutExtension(pkgPath);
                foreach (var exp in pkg.Exports)
                {
                    if (!classes.Contains(exp.ClassName?.ToString() ?? "Class")) continue;
                    string path = UETextureSource.FullPathOf(pkgName, exp);
                    if (!seen.Add(path)) continue;
                    Entry e = EntryOf(pkg, pkgName, exp, path);
                    if (e is not null) yield return e;
                }
            }
        }

        public static string Format(Entry e) => $"{e.Path}\t{e.Next}\t{e.MinS}\t{e.MaxS}";

        // Texture record by full path: packages named by the first segment, in export order.
        public Entry Find(string uePath)
        {
            if (string.IsNullOrEmpty(uePath)) return null;
            if (_byPath.TryGetValue(uePath, out Entry hit)) return hit;
            int dot = uePath.IndexOf('.');
            string want = dot > 0 ? uePath.Substring(0, dot) : uePath;
            Entry found = null;
            bool matched = false;
            HashSet<string> classes = TextureClasses;
            foreach (string pkgPath in Packages)
            {
                string pkgName = System.IO.Path.GetFileNameWithoutExtension(pkgPath);
                if (!string.Equals(pkgName, want, StringComparison.OrdinalIgnoreCase)) continue;
                UnrealPackage pkg = Open(pkgPath);
                if (pkg is null) continue;
                foreach (var exp in pkg.Exports)
                {
                    if (!classes.Contains(exp.ClassName?.ToString() ?? "Class")) continue;
                    string path = UETextureSource.FullPathOf(pkgName, exp);
                    if (!string.Equals(path, uePath, StringComparison.OrdinalIgnoreCase)) continue;
                    found = EntryOf(pkg, pkgName, exp, path);
                    matched = true;
                    break;
                }
                if (matched) break;
            }
            _byPath[uePath] = found;
            return found;
        }

        static int ReadCompactIndex(byte[] b, ref int p)
        {
            int b0 = b[p++];
            bool neg = (b0 & 0x80) != 0;
            int value = b0 & 0x3F;
            if ((b0 & 0x40) != 0)
            {
                int shift = 6;
                for (int i = 0; i < 4; i++)
                {
                    int bn = b[p++];
                    value |= (bn & 0x7F) << shift;
                    shift += 7;
                    if ((bn & 0x80) == 0) break;
                }
            }
            return neg ? -value : value;
        }

        static string ObjectPathOfRef(UnrealPackage pkg, string pkgName, int index)
        {
            if (index > 0) return UETextureSource.FullPathOf(pkgName, pkg.Exports[index - 1]);
            var imp = pkg.Imports[-index - 1];
            var chain = new List<string>();
            UObjectTableItem item = imp;
            while (item != null) { chain.Add(item.ObjectName.ToString()); item = item.Outer; }
            chain.Reverse();
            return string.Join(".", chain);
        }

        // ── Frame (UTexture::Tick) ───────────────────────────────────────────────────

        // UTexture::Update (Engine 0x10421070, Engine.dll.c:37954-38026): dt = Time − LastUpdateTime, where Time is
        // Viewport->CurrentTime, WALL clock (UViewport::Lock, appSecondsSlow/rdtsc, 60670-60680), and Update is called
        // only by UTexture::Lock when Time != LastUpdateTime (0x10421720) — a texture's dt is the time since ITS last
        // draw; pause and TimeDilation do not affect it. LastUpdateTime is 0 from the constructor while Time is
        // "seconds since machine start" — the first dt is huge (the accumulator clips it to 1/Min): here −∞.
        sealed class State { public string Current; public float Accumulator; public int LastFrame = -1; public double LastTime = double.NegativeInfinity; public RenderCadence Cadence; }
        readonly Dictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);
        double _now;
        int _frame;

        public bool IsAnimated(string path) => Find(path) is not null;

        public void BeginFrame(double wallSeconds)
        {
            _frame++;
            _now = wallSeconds;
        }

        public string CurrentFrame(string path)
        {
            Entry head = Find(path);
            if (head is null) return path;
            if (!_states.TryGetValue(path, out State st))
                _states[path] = st = new State { Current = path };
            if (st.LastFrame == _frame) return st.Current;
            st.LastFrame = _frame;
            if (_now == st.LastTime) return st.Current;      // Time == LastUpdateTime — Update is not called
            if (!st.Cadence.Due(_now)) return st.Current;    // 60 Hz rate at any render frame rate (D-35)
            float dt = (float)(_now - st.LastTime);
            st.LastTime = _now;
            if (head.Max == 0f) Advance(st, path);
            else
            {
                // UTexture::Tick 0x104217c0 as WaterTexture.Tick (judged by merge_check 35): the sum stays in the x87
                // register for both compares (float rounding of it flipped a step, validate_texanim.py).
                float max = head.Max < 0.1f ? 0.1f : head.Max >= 100f ? 100f : head.Max;
                float min = head.Min < 0.1f ? 0.1f : head.Min >= 100f ? 100f : head.Min;
                float minTime = (float)(1.0 / max), maxTime = (float)(1.0 / min);
                double sum = (double)dt + st.Accumulator;
                st.Accumulator = (float)sum;
                if (sum >= minTime)
                {
                    Advance(st, path);
                    if (sum < maxTime) st.Accumulator = 0f;
                    else
                    {
                        double rest = (double)st.Accumulator - maxTime;
                        st.Accumulator = (float)rest;
                        if (rest > maxTime) st.Accumulator = maxTime;
                    }
                }
            }
            return st.Current;
        }

        public int ChainLength(string head)
        {
            int n = 1;
            string cur = head;
            for (int guard = 0; guard < 256; guard++)
            {
                Entry e = Find(cur);
                if (e is null || string.IsNullOrEmpty(e.Next)) break;
                cur = e.Next;
                if (string.Equals(cur, head, StringComparison.OrdinalIgnoreCase)) break;
                n++;
            }
            return n;
        }

        public string ChainAt(string head, int k)
        {
            string cur = head;
            for (int i = 0; i < k; i++)
            {
                Entry e = Find(cur);
                if (e is null || string.IsNullOrEmpty(e.Next)) break;
                cur = e.Next;
            }
            return cur;
        }

        void Advance(State st, string head)
        {
            Entry e = Find(st.Current);
            st.Current = e is not null && !string.IsNullOrEmpty(e.Next) ? e.Next : head;
        }
    }
}
