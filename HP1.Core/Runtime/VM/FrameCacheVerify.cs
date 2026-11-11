// VERIFY MODE OF THE PERFORMANCE CACHES (perf/frame-caches, 2026-11-05).
//
// Every cache that skips a recomputation (catalog resolution memo on UEResourceRef, per-actor frame projection,
// render-bound root pose) recomputes on EVERY hit when HP1_FRAME_CACHE_VERIFY=1 and compares with what it served.
// A mismatch is a stale cache: counted per cache, the first ones named. Off in production (one bool test per hit).
// Reported by tools/perf_core_bench (`frame_cache_verify ...` line); the red mutations in
// audit/completeness_mutations.txt (frame_cache_*) drop one key field and must make the mismatch count non-zero.
using System.Collections.Generic;

namespace HP1.UEScript
{
    public static class FrameCacheVerify
    {
        public static readonly bool Enabled = System.Environment.GetEnvironmentVariable("HP1_FRAME_CACHE_VERIFY") == "1";

        static readonly object _gate = new object();
        static readonly Dictionary<string, (long hits, long mismatches, long misses)> _byCache = new();
        static readonly List<string> _first = new();

        public static void Hit(string cache, string mismatch)
        {
            lock (_gate)
            {
                _byCache.TryGetValue(cache, out var c);
                c.hits++;
                if (mismatch is not null)
                {
                    c.mismatches++;
                    if (_first.Count < 20) _first.Add(cache + ": " + mismatch);
                }
                _byCache[cache] = c;
            }
        }

        // A recomputation the cache did not skip (key changed) — counted only in verify mode, for the hit rate.
        public static void Miss(string cache)
        {
            lock (_gate) { _byCache.TryGetValue(cache, out var c); c.misses++; _byCache[cache] = c; }
        }

        // First difference between a fresh recomputation and the served value, by public fields, recursively
        // (structs, lists element by element; floats bit for bit, NaN included); null when identical. Reflection:
        // verify mode only, and a field added to a snapshot type is compared without touching this code.
        public static string Diff(object fresh, object served, string label)
        {
            string d = DiffAt(fresh, served, "", 0);
            return d is null ? null : label + d;
        }

        static string DiffAt(object a, object b, string path, int depth)
        {
            if (a is null || b is null) return a is null && b is null ? null : $"{path}: {a ?? "null"} vs {b ?? "null"}";
            System.Type t = a.GetType();
            if (t != b.GetType()) return $"{path}: type {t.Name} vs {b.GetType().Name}";
            if (a is float fa) return System.BitConverter.SingleToInt32Bits(fa) == System.BitConverter.SingleToInt32Bits((float)b) ? null : $"{path}: {fa:R} vs {(float)b:R}";
            if (a is double da) return System.BitConverter.DoubleToInt64Bits(da) == System.BitConverter.DoubleToInt64Bits((double)b) ? null : $"{path}: {da:R} vs {(double)b:R}";
            if (t.IsPrimitive || t.IsEnum || a is string) return a.Equals(b) ? null : $"{path}: {a} vs {b}";
            if (depth > 8) throw new System.InvalidOperationException("FrameCacheVerify.Diff: nesting too deep at " + path);
            if (a is System.Collections.IList la)
            {
                var lb = (System.Collections.IList)b;
                if (la.Count != lb.Count) return $"{path}: count {la.Count} vs {lb.Count}";
                for (int i = 0; i < la.Count; i++)
                {
                    string d = DiffAt(la[i], lb[i], $"{path}[{i}]", depth + 1);
                    if (d is not null) return d;
                }
                return null;
            }
            if (!t.IsValueType) return ReferenceEquals(a, b) ? null : $"{path}: other {t.Name} instance";
            foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            {
                string d = DiffAt(f.GetValue(a), f.GetValue(b), path + "." + f.Name, depth + 1);
                if (d is not null) return d;
            }
            return null;
        }

        public static long Mismatches
        {
            get { lock (_gate) { long m = 0; foreach (var c in _byCache.Values) m += c.mismatches; return m; } }
        }

        public static string Report()
        {
            lock (_gate)
            {
                var sb = new System.Text.StringBuilder();
                long m = 0;
                foreach (var c in _byCache.Values) m += c.mismatches;
                sb.Append("frame_cache_verify mismatches=").Append(m);
                foreach (var kv in _byCache) sb.Append(' ').Append(kv.Key).Append(" hits=").Append(kv.Value.hits).Append(" misses=").Append(kv.Value.misses).Append(" bad=").Append(kv.Value.mismatches);
                foreach (string f in _first) sb.Append("\n  stale ").Append(f);
                return sb.ToString();
            }
        }
    }
}
