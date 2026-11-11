// BYTECODE BRANCH COVERAGE COUNTER (diagnostics for comparison with the original, not a gate).
//
// Counterpart of the `B`/`W` records of the live-original probe tools/origprobe/hp1probe.dll: for every executed
// EX_JumpIfNot the outcome (J = jump taken, N = fall through), for every EX_Switch the offset of the matched case
// body. Positions are in script memory (UELib Token.Position, same space as CodeOffset of jumps), like
// `Code - Node->Script.Data` in the original (Core.dll.c execJumpIfNot: Script.Data = *(Node+0x48)).
//
// Function key: names of the Outer chain without the package: `Class.Func`, `Class.State.Func`, and
// `Class.State` for state code.
//
// Denominator (`S`): all JumpIfNot/Switch sites in all UStructs of the registry packages; the union over map runs
// is the campaign.
using System.Collections.Generic;
using System.IO;
using System.Text;
using UELib;
using UELib.Core;
using Tok = UELib.Core.UStruct.UByteCodeDecompiler;

namespace HP1.UEScript
{
    public static class UECoverage
    {
        public static bool On;

        static readonly Dictionary<(UStruct, int, int), long> _hits =
            new Dictionary<(UStruct, int, int), long>();

        // Counters are written by the Core thread; the snapshot (Write) may be called from another thread, hence the lock.
        static readonly object _lock = new object();

        // dest: -1 = JumpIfNot jumped, -2 = fell through; otherwise the case body offset.
        internal static void Hit(UStruct node, int pos, int dest)
        {
            var k = (node, pos, dest);
            lock (_lock) _hits[k] = _hits.TryGetValue(k, out long c) ? c + 1 : 1;
        }

        public static void Reset() { lock (_lock) { _hits.Clear(); } }

        static void AppendLine(StringBuilder sb, (UStruct, int, int) k, long count)
        {
            var (node, pos, dest) = k;
            string key = KeyOf(node);
            if (dest < 0) sb.Append("B ").Append(key).Append(' ').Append(pos).Append(dest == -1 ? " J " : " N ");
            else sb.Append("W ").Append(key).Append(' ').Append(pos).Append(' ').Append(dest).Append(' ');
            sb.Append(count).Append('\n');
        }

        public static string KeyOf(UObject o)
        {
            var parts = new List<string>();
            for (; o != null; o = o.Outer) parts.Add(o.Name.ToString());
            parts.Reverse();
            return string.Join(".", parts);
        }

        public static void Write(string path, IEnumerable<UnrealPackage> packages)
        {
            var sb = new StringBuilder();
            lock (_lock)
                foreach (var kv in _hits) AppendLine(sb, kv.Key, kv.Value);
            AppendSites(sb, packages);
            File.WriteAllText(path, sb.ToString());
        }

        static void AppendSites(StringBuilder sb, IEnumerable<UnrealPackage> packages)
        {
            if (packages != null)
                foreach (var pkg in packages)
                {
                    if (pkg?.Exports == null) continue;
                    foreach (var e in pkg.Exports)
                    {
                        if (!(e.Object is UStruct st) || st is UClass) continue;
                        if (st.ByteCodeManager == null) continue;
                        string key = null;
                        var toks = UEInterpreter.LoadTokens(st);
                        for (int i = 0; i < toks.Count; i++)
                        {
                            var t = toks[i];
                            char kind = t is Tok.JumpIfNotToken ? 'B' : t is Tok.SwitchToken ? 'W' : '\0';
                            if (kind == '\0') continue;
                            // Degenerate site (b): JumpIfNot target == point after the condition; jump and
                            // fall-through are indistinguishable in the original, one direction.
                            if (t is Tok.JumpIfNotToken jt && FallThrough(toks, i) == jt.CodeOffset) kind = 'b';
                            key ??= KeyOf(st);
                            sb.Append("S ").Append(kind).Append(' ').Append(key).Append(' ').Append(t.Position).Append('\n');
                        }
                    }
                }
        }

        // Position of the first token after the JumpIfNot condition. The condition is one expression; UELib does
        // not store subtree bytes, so the boundary is taken from the condition token's Position + Size.
        static int FallThrough(List<Tok.Token> toks, int i)
        {
            if (i + 1 >= toks.Count) return -1;
            var cond = toks[i + 1];
            return cond.Position + cond.Size;
        }
    }
}
