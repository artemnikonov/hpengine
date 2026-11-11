// Execution frame + iterator/lvalue plumbing, extracted with UEObject.
using System;
using System.Collections.Generic;
using UELib.Core;
using Tok = UELib.Core.UStruct.UByteCodeDecompiler;

namespace HP1.UEScript
{
    // Execution frame, analogue of FFrame (Core.dll.c:9617: Node +4, Object +8,
    // Code +0xc, Locals +0x10). One frame per active function call.
    // LatentState marks why a coroutine yielded so the next Tick can resume.
    //
    // Cursor is FFrame.Code: UELib parses bytecode into a flat (pre-order) token list, handlers consume
    // subexpressions via EvalNext, like FFrame::Step (Core.dll.c:39376).
    public class UEFrame
    {
        public UEObject Object;   // Self ('this') — invariant for the frame
        public UEObject Context;  // current member-access context (defaults
                                  // to Object; swapped by ContextToken for
                                  // A.B chains, restored after)
        public UClass   ClassContext; // class for `default.X` lookup, set by
                                      // ClassContextToken (`SomeClass.default.X`);
                                      // null → use Context/Object's class.
        public UStruct  Func;
        // Parameters and locals: cells of the function layout (UStruct Func), like UE1's LocalVariables memory.
        // Created on first access.
        public UEProps Variables
        {
            get
            {
                if (UEVmPool.Verify && PoolReleased) UEVmPool.LateRead(this, "Variables");
                return _vars ??= NewVariables();
            }
        }
        UEProps NewVariables()
        {
            UEProps v = _spareVars;
            _spareVars = null;
            if (v is null) v = new UEProps(UEPropLayout.ForStruct(Func));
            else v.ResetForReuse(UEPropLayout.ForStruct(Func));
            v.MaterializeStaticArrays();
            return v;
        }
        UEProps _vars;
        // Pooled frame (UEVmPool): the previous call's locals bag, emptied on first access (NewVariables). Not _vars, so
        // a pooled frame reads as fresh to every reader of _vars (the vmjudge tap reads it by reflection).
        UEProps _spareVars;
        // HP1_POOL_VERIFY=1: set when the frame went back to the pool, cleared when the pool hands it out again; a reader
        // in between fails loudly.
        internal bool PoolReleased;

        // Back to the state of `new UEFrame()` for the next Invoke; keeps the locals bag and the iterator list (both
        // emptied). UEVmPool.Verify checks every field against a fresh frame on each release and each reuse.
        internal void ResetForPool()
        {
            Object = null; Context = null; ClassContext = null; Func = null;
            if (_vars is not null) { _vars.ResetForReuse(_vars.Layout); _spareVars = _vars; _vars = null; }
            Tokens = null; HandlerOwner = null; OffsetToIndex = null; Cursor = 0;
            ProbeMask = UEProbe.All; LatentState = LatentRunState.Continue; ReturnValue = null; InRun = false;
            _iterators?.Clear();
            SleepTimeLeft = 0f;
        }
        // Fresh set of locals (the frame moved to another state/function).
        public void ResetVariables() => _vars = null;
        // flat pre-order token stream
        public List<Tok.Token> Tokens
        {
            get => _tokens;
            set { _tokens = value; PropRefs = null; HandlerCache = null; TokenKinds = null; }
        }
        List<Tok.Token> _tokens;
        // Resolved property accesses by token index (UEInterpreter.PropRefAt): bytecode addresses an object slot
        // by UProperty number, without a string.
        internal UEPropRef[] PropRefs;
        // Token handler by token index (UEInterpreter.EvalNext), instead of a lookup by token type on every
        // execution; cache of the owning interpreter.
        internal UETokenHandler[] HandlerCache;
        // Token kinds by token index (UEInterpreter.TokenKindAt), shared by the frames of one stream.
        internal UEInterpreter.TokenKind[] TokenKinds;
        internal object HandlerOwner;
        public Dictionary<int, int> OffsetToIndex; // byte position → Tokens idx (for Jump targets)
        public int Cursor;                        // index into Tokens
        // State frame PROBE MASK (Core.dll.c: InitExecution sets all ones, GotoState recomputes, Disable/Enable
        // flip one bit). Default is all ones, like a freshly created frame in the original.
        public ulong ProbeMask = UEProbe.All;
        public LatentRunState LatentState = LatentRunState.Continue;
        public object ReturnValue;

        // True while this frame's Run loop is on the call stack. EnterState uses
        // it to detect "GotoState from inside the object's own state body" and
        // redirect THIS frame in place (in shipped the object holds one
        // FStateFrame and GotoState edits its Node/Code, Core.dll.c:58247).
        public bool InRun;

        // Active foreach iterators. Pushed by IteratorToken, driven by
        // IteratorNextToken, popped by IteratorPopToken. A stack so nested
        // foreach works. Shipped has no stack: the loop lives in the iterator native
        // (execIterator has an empty body, Core.dll.c:33534). Latent behaviour inside a foreach body is
        // NOT VERIFIED (no carriers in the scripts).
        public List<UEIterator> IteratorStack => _iterators ??= new List<UEIterator>();
        List<UEIterator> _iterators;

        // Latent suspension params (set by latent natives, consumed by
        // TickStateFrame). Sleep uses remaining actor-tick time; movement latents add
        // their targets here as they're implemented.
        public float SleepTimeLeft;
    }

    // Call-object pool of one interpreter (audit/perf_core_ceiling_2026-11-03.md, "perf/vm-alloc"): the UEFrame of a
    // function call (Invoke), the argument arrays and out-ref lists of a bytecode call (CollectArgsWithOuts). An object
    // goes back only at a call return where no holder keeps it (holder audit in that section); every other path leaves
    // it to the GC. Allocation only: a pooled object is reset to the state of a new one before it is handed out.
    // HP1_POOL_VERIFY=1: a released object is reset, checked against a new one, poisoned and never reused, so a late
    // reader fails loudly (frame: Variables/EvalNext/Run/lvalue access; args: Invoke entry and lvalue writes; out
    // refs: lvalue access).
    public sealed class UEVmPool
    {
        internal static readonly bool Verify = Environment.GetEnvironmentVariable("HP1_POOL_VERIFY") == "1";
        // Verify mode only. Released objects are reset and poisoned, then go back to the pool like in a normal run; a
        // reuse first checks the object is still poisoned (no late write) and, un-poisoned, equals a new one.
        internal static long FramesReleased, ArgsReleased, OutRefsReleased, FramesReused, ArgsReused, OutRefsReused;
        // Verify mode: counts of released and of checked reused objects (null otherwise); a zero count = that reuse path
        // did not execute, no verdict.
        public static string VerifyReport => !Verify ? null
            : FramesReused == 0 || ArgsReused == 0 || OutRefsReused == 0
                ? $"pool_verify NO VERDICT: a reuse path did not execute (frames_reused={FramesReused} args_reused={ArgsReused} outrefs_reused={OutRefsReused})"
            : $"pool_verify frames_released={FramesReleased} args_released={ArgsReleased} outrefs_released={OutRefsReleased} "
              + $"frames_reused={FramesReused} args_reused={ArgsReused} outrefs_reused={OutRefsReused} late_reads=0 scene_nodes_reused={UESceneNodeBanks.NodesReused}";
        const int MaxArgs = 16;
        internal const UELValueKind PoisonKind = (UELValueKind)0x7f;
        internal sealed class PoisonValue { public override string ToString() => "<pool poison>"; }
        internal static readonly PoisonValue Poison = new PoisonValue();

        readonly Stack<UEFrame> _frames = new Stack<UEFrame>();
        readonly Stack<object[]>[] _args = new Stack<object[]>[MaxArgs + 1];
        readonly Stack<List<UELValueRef?>> _outRefs = new Stack<List<UELValueRef?>>();

        internal UEFrame RentFrame(UEObject obj, UStruct fn)
        {
            UEFrame f;
            if (_frames.Count > 0)
            {
                f = _frames.Pop();
                if (Verify) { f.PoolReleased = false; CheckFresh(f); System.Threading.Interlocked.Increment(ref FramesReused); }
            }
            else f = new UEFrame();
            f.Object = obj; f.Context = obj; f.Func = fn;
            return f;
        }
        internal void ReleaseFrame(UEFrame f)
        {
            if (Verify && f.PoolReleased) throw new InvalidOperationException("HP1_POOL_VERIFY: call frame released twice");
            f.ResetForPool();
            if (Verify) { CheckFresh(f); f.PoolReleased = true; System.Threading.Interlocked.Increment(ref FramesReleased); }
            _frames.Push(f);
        }

        internal object[] RentArgs(int n)
        {
            if (n == 0) return Array.Empty<object>();
            if (n > MaxArgs) return new object[n];
            var s = _args[n];
            if (s is null || s.Count == 0) return new object[n];
            var a = s.Pop();
            if (Verify)
            {
                foreach (object v in a)
                    if (!ReferenceEquals(v, Poison)) throw new InvalidOperationException("HP1_POOL_VERIFY: argument array written after it went back to the pool");
                Array.Clear(a, 0, n);
                System.Threading.Interlocked.Increment(ref ArgsReused);
            }
            return a;
        }
        internal void ReleaseArgs(object[] a)
        {
            int n = a.Length;
            if (n == 0 || n > MaxArgs) return;
            if (Verify)
            {
                if (ReferenceEquals(a[0], Poison)) throw new InvalidOperationException("HP1_POOL_VERIFY: argument array released twice");
                Array.Fill(a, Poison); System.Threading.Interlocked.Increment(ref ArgsReleased);
            }
            else Array.Clear(a, 0, n);
            (_args[n] ??= new Stack<object[]>()).Push(a);
        }

        internal List<UELValueRef?> RentOutRefs(int capacity)
        {
            if (_outRefs.Count == 0) return new List<UELValueRef?>(capacity);
            var l = _outRefs.Pop();
            if (Verify)
            {
                // Released lists keep their poisoned entries (count 1 + the call's), cleared here.
                if (l.Count == 0 || l[0] is not { Kind: PoisonKind }) throw new InvalidOperationException("HP1_POOL_VERIFY: out-ref list written after it went back to the pool");
                for (int i = 1; i < l.Count; i++)
                    if (l[i] is not { Kind: PoisonKind }) throw new InvalidOperationException("HP1_POOL_VERIFY: out-ref list written after it went back to the pool");
                l.Clear();
                System.Threading.Interlocked.Increment(ref OutRefsReused);
            }
            return l;
        }
        internal void ReleaseOutRefs(List<UELValueRef?> l, List<UELValueRef?> shared)
        {
            if (ReferenceEquals(l, shared)) return;
            if (Verify)
            {
                if (l.Count > 0 && l[0] is { Kind: PoisonKind }) throw new InvalidOperationException("HP1_POOL_VERIFY: out-ref list released twice");
                for (int i = 0; i < l.Count; i++) l[i] = new UELValueRef { Kind = PoisonKind };
                l.Insert(0, new UELValueRef { Kind = PoisonKind });   // an empty list is marked too
                System.Threading.Interlocked.Increment(ref OutRefsReleased);
            }
            else l.Clear();
            _outRefs.Push(l);
        }

        internal static void LateRead(UEFrame f, string what)
            => throw new InvalidOperationException($"HP1_POOL_VERIFY: {what} of a call frame read after it went back to the pool");
        internal static void CheckArgs(object[] a)
        {
            if (a is null) return;
            for (int i = 0; i < a.Length; i++)
                if (ReferenceEquals(a[i], Poison))
                    throw new InvalidOperationException("HP1_POOL_VERIFY: argument array read after it went back to the pool");
        }
        internal static void CheckValue(object v)
        {
            if (ReferenceEquals(v, Poison))
                throw new InvalidOperationException("HP1_POOL_VERIFY: argument array read after it went back to the pool");
        }
        internal static void CheckRef(in UELValueRef r)
        {
            if (r.Kind == PoisonKind)
                throw new InvalidOperationException("HP1_POOL_VERIFY: out-ref list read after it went back to the pool");
        }

        // Every field of a reset frame equals a new frame's, except the kept (emptied) locals bag and iterator list.
        static readonly System.Reflection.FieldInfo[] FrameFields = typeof(UEFrame).GetFields(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        static readonly UEFrame Fresh = new UEFrame();
        static void CheckFresh(UEFrame f)
        {
            foreach (var fi in FrameFields)
            {
                object got = fi.GetValue(f), want = fi.GetValue(Fresh);
                if (fi.Name == "_spareVars") { if (got is UEProps p && p.Count != 0) throw Bad(fi.Name); continue; }
                if (fi.Name == "_iterators") { if (got is List<UEIterator> l && l.Count != 0) throw Bad(fi.Name); continue; }
                if (!Equals(got, want)) throw Bad(fi.Name);
            }
        }
        static Exception Bad(string field)
            => new InvalidOperationException($"HP1_POOL_VERIFY: UEFrame.ResetForPool leaves {field} different from a new frame");
    }

    // Bytecode property access: UProperty number (slot in the object layout) and simple-name number
    // (a struct member is addressed by name).
    internal sealed class UEPropRef
    {
        public const byte None = 0, PlayerHarry = 2, Level = 3, ClassProp = 4, XLevel = 5;
        public readonly Tok.Token Tok;
        public readonly string Name;
        public readonly int PropId;     // -1: property is not a UProperty
        public readonly int NameKeyId;
        public readonly byte Special;
        public UEPropRef(Tok.Token tok, UObject prop)
        {
            Tok = tok;
            Name = prop?.Name?.ToString();
            PropId = prop is UProperty up && Name != null ? UEPropKeys.PropId(up) : -1;
            NameKeyId = Name is null ? -1 : UEPropKeys.Id(Name);
            Special = Name is null ? None
                : string.Equals(Name, "PlayerHarry", StringComparison.OrdinalIgnoreCase) ? PlayerHarry
                : string.Equals(Name, "Level", StringComparison.OrdinalIgnoreCase) ? Level
                : string.Equals(Name, "Class", StringComparison.OrdinalIgnoreCase) ? ClassProp
                : string.Equals(Name, "XLevel", StringComparison.OrdinalIgnoreCase) ? XLevel
                : None;
        }
    }

    // REFERENCE comparison for caches keyed by the object itself (token stream, resource). Own type rather than
    // System.Collections.Generic.ReferenceEqualityComparer: that appeared in .NET 5, while Core sources must also
    // build under netstandard2.1 + C# 9.
    internal sealed class UERefComparer<T> : IEqualityComparer<T> where T : class
    {
        public static readonly UERefComparer<T> Instance = new UERefComparer<T>();
        public bool Equals(T a, T b) => ReferenceEquals(a, b);
        public int GetHashCode(T o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
    }

    // foreach iterator. Shipped scheme: `*Out = NULL` at the start of a pass, element search, body up to
    // 0x30/0x31, `continue` -> StartCode. MoveNext writes the loop variable and returns false when exhausted.
    // EndOffset = wEndOffset, BodyTopCursor = StartCode.
    public abstract class UEIterator
    {
        public UELValueRef LoopVar;   // where each item is written (the foreach var)
        // All of the foreach's out-param LValues, parallel to the call args (null
        // for non-out slots). AllActors needs only LoopVar; TraceActors writes 3
        // out-params (Actor/HitLocation/HitNormal) per pass and reads them here.
        public List<UELValueRef?> OutRefs;
        public int EndOffset;         // byte offset just past the foreach (IteratorToken.CodeOffset)
        public int BodyTopCursor;     // token index of the loop body's first token
        public abstract bool MoveNext(UEInterpreter interp, UEFrame frame);
    }

    // Discriminated lvalue reference, used by Let/LetBool to capture where the
    // RHS value should be stored. Shipped addresses GPropAddr/GProperty
    // (execLocalVariable Core.dll.c:65110, execInstanceVariable :61661); the port
    // keeps a re-resolvable reference {kind, slot, owner}; the difference shows
    // only under aliasing.
    public enum UELValueKind { Invalid, Local, Instance, ArrayIndex, StructMember }
    public struct UELValueRef
    {
        public UELValueKind Kind;
        public string Name;
        // Instance/Local: slot of Name in the SlotLayout layout (null = addressing by Name).
        public UEPropLayout SlotLayout;
        public int Slot;
        public UEObject Target;            // owning UEObject for Instance kind
        public System.Collections.IList ListTarget;  // IList target for ArrayIndex kind
        public int IntIndex;               // element index for ArrayIndex kind
        public object ParentRef;           // boxed parent UELValueRef for StructMember kind
    }
}
