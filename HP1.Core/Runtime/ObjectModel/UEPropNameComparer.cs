using System;
using System.Collections.Generic;

namespace HP1.UEScript
{
    // PROPERTY KEY: CASE-INSENSITIVE LIKE A UE1 FName, BUT CHEAP.
    //
    // Same semantics as StringComparer.OrdinalIgnoreCase (equality of chars after
    // ToUpperInvariant, culture-free). Only the cost differs: under Mono OrdinalIgnoreCase
    // hashes char by char through a case table (97 ns per hash, 129 ns per lookup vs 31 ns
    // for Ordinal). Frame projection does ~100 lookups per actor; on Tut1's 1262 actors that
    // was 13 of 21 ms.
    //
    // ASCII is folded in place (A-Z <=> a-z); any non-ASCII char sends EQUALITY to the real
    // OrdinalIgnoreCase, and its hash uses Char.ToUpperInvariant, the same mapping
    // OrdinalIgnoreCase uses for equality, so equal keys give equal hashes.
    // Equivalence to OrdinalIgnoreCase is checked by a self-test.
    public sealed class UEPropNameComparer : IEqualityComparer<string>
    {
        public static readonly UEPropNameComparer Instance = new UEPropNameComparer();

        public bool Equals(string a, string b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a is null || b is null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                char x = a[i], y = b[i];
                if (x == y) continue;
                if (x < 128 && y < 128)
                {
                    // Distinct ASCII chars are case-insensitively equal only as a letter pair.
                    if ((x ^ y) == 0x20 && (uint)((x | 0x20) - 'a') <= 25u) continue;
                    return false;
                }
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }

        // THE HASH IS MEMOISED BY STRING REFERENCE. Almost all lookups reuse the same
        // string objects (literals like `GetProp("Location")`, names from the package name
        // table), and per-char rehashing cost ~11% of the main thread (Tut1: ~150k lookups
        // per frame). Strings are immutable, so the memoised hash equals the recomputed one:
        // same answer, lower cost. The slot is chosen by the IDENTITY hash (cheap, no char
        // reads); a different string in the slot means recompute and replace.
        sealed class Memo { public readonly string S; public readonly int H; public Memo(string s, int h) { S = s; H = h; } }
        static readonly Memo[] _memo = new Memo[8192];

        public int GetHashCode(string s)
        {
            if (s is null) return 0;
            int slot = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(s) & (8192 - 1);
            Memo m = _memo[slot];
            if (m is not null && ReferenceEquals(m.S, s)) return m.H;
            int h = Compute(s);
            _memo[slot] = new Memo(s, h);
            return h;
        }

        internal static int Compute(string s)
        {
            uint h = 5381;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                uint u = c < 128
                    ? ((uint)(c - 'a') <= 25u ? (uint)c - 0x20u : c)
                    : char.ToUpperInvariant(c);
                h = (h * 33u) ^ u;
            }
            return (int)h;
        }
    }
}
