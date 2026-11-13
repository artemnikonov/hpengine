// Token evaluation helpers: stub declarations, argument collection with out-refs, short-circuit and
// skip tokens, handler lookup, token kinds, static/dynamic array indexing, l-values (EvalLValue,
// WriteLValue), switch, struct equality and value conversions. Fields live in UEInterpreter.cs.
using System;
using System.Collections.Generic;
using UELib;
using UELib.Core;
using Tok = UELib.Core.UStruct.UByteCodeDecompiler;
using UELib.Tokens;  // CastToken enum (UnrealTokens.cs)
using HP1.Core;      // UETrace sinks + UEClassRegistry
// Script vectors ARE the Core value type: prop-bag storage, struct decode,
// literals and the As*-surface all use HP1.UEScript.UEVector.
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    public partial class UEInterpreter
    {
        internal UFunction ResolveStubDeclaration(UFunction fn)
        {
            if (fn == null) return null;
            if (fn.FunctionFlags.HasFlag(UELib.Flags.FunctionFlag.Native)) return fn;
            if (_stubDecl.TryGetValue(fn, out var cached)) return cached;
            UFunction real = fn;
            // VM-13: resolution does NOT depend on whether a native with the same
            // name is registered. A `GetByName(name) == null` guard would leave the
            // stub unresolved exactly where the name matches a native name
            // (UWindowWindow.TextSize(Canvas,…, out, out) vs Canvas.TextSize,
            // Object.VSize2D vs Pair(271)), and `out` flags would be read from the
            // stub — as "no out-params".
            if (LoadTokens(fn).Count == 0)
            {
                string fname = fn.Name?.ToString();
                if (fn.Outer is UState stubState && !(fn.Outer is UClass))
                {
                    string stName = stubState.Name?.ToString();
                    string ownerName = (stubState.Outer as UClass)?.Name ?? stubState.Outer?.Name?.ToString();
                    var ownerCanon = UEClassRegistry.Shared.Find(ownerName);
                    if (ownerCanon != null)
                    {
                        try { ownerCanon.BeginDeserializing(); } catch { }
                        for (var c = ownerCanon; c != null && ReferenceEquals(real, fn); c = CanonicalSuper(c))
                            foreach (var st in c.EnumerateFields<UState>())
                            {
                                if (st is UClass) continue;
                                if (!string.Equals(st.Name?.ToString(), stName, StringComparison.OrdinalIgnoreCase)) continue;
                                foreach (var f in st.EnumerateFields<UFunction>())
                                    if (string.Equals(f.Name?.ToString(), fname, StringComparison.OrdinalIgnoreCase)
                                     && !ReferenceEquals(f, fn) && LoadTokens(f).Count > 0)
                                    { real = f; break; }
                                if (!ReferenceEquals(real, fn)) break;
                            }
                    }
                }
                if (ReferenceEquals(real, fn))
                {
                    string declName = (fn.Outer as UClass)?.Name ?? fn.Outer?.Name?.ToString();
                    var declCanon = UEClassRegistry.Shared.Find(declName);
                    if (declCanon != null)
                    {
                        try { declCanon.BeginDeserializing(); } catch { }
                        for (var c = declCanon; c != null && ReferenceEquals(real, fn); c = CanonicalSuper(c))
                            foreach (var f in c.EnumerateFields<UFunction>())
                                if (string.Equals(f.Name?.ToString(), fname, StringComparison.OrdinalIgnoreCase))
                                {
                                    // The real declaration is a script with a body OR a native
                                    // (a native has no body but has parameter flags).
                                    if (!ReferenceEquals(f, fn)
                                        && (LoadTokens(f).Count > 0
                                            || f.FunctionFlags.HasFlag(UELib.Flags.FunctionFlag.Native)))
                                        real = f;
                                    break;
                                }
                    }
                }
            }
            _stubDecl[fn] = real;
            return real;
        }
        static (object[] args, List<UELValueRef?> outRefs)
            CollectArgsWithOuts(UEInterpreter interp, UEFrame frame, UFunction targetFn, string label)
        {
            // out flags come from the REAL declaration, not from the import stub.
            if (targetFn != null) targetFn = interp.ResolveStubDeclaration(targetFn);
            ParamDesc pd = targetFn != null ? ParamsOf(targetFn) : null;
            bool[] paramOutFlags = pd != null ? pd.IsOut : Array.Empty<bool>();
            int depth = interp._argDepth++;
            if (depth == interp._argLists.Count) interp._argLists.Add(new List<object>(8));
            var args = interp._argLists[depth];
            args.Clear();
            // Without out-params the ref list would be all nulls, and consumers
            // only look at HasValue — use the shared empty list.
            bool anyOut = pd != null && pd.AnyOut;
            var outRefs = anyOut ? interp.Pool.RentOutRefs(paramOutFlags.Length) : NoOutRefs;
            int idx = 0;
            // UObject::CallFunction (Core.dll.c:7715): arguments are evaluated by
            // GNatives in the caller's frame, with object Stack.Object, i.e. the
            // context is reset to Self (the caller). For a context call `A.B(C)` the RHS
            // runs with Context=A (so the function resolves on A), but the
            // ARGUMENT expressions still resolve against the caller — so `C`
            // (e.g. the camera's CutCueString in CutNotifyScript.CutCue(
            // CutCueString)) reads the caller's instance var, not A's. Mirror
            // that by pinning Context to Self (frame.Object) while collecting.
            var savedContext = frame.Context;
            frame.Context = frame.Object;
            try
            {
            while (true)
            {
                while (frame.Cursor < frame.Tokens.Count && IsDebugInfoAt(frame, frame.Cursor))
                    frame.Cursor++;
                if (frame.Cursor >= frame.Tokens.Count) {
                    UETrace.Error(
                        $"[VM] {label}: unexpected EOF before EndFunctionParms in " +
                        $"{frame.Object?.Class?.Name}.{frame.Func?.Name}");
                    break;
                }
                if (TokenKindAt(frame, frame.Cursor) == TokenKind.EndFunctionParms) {
                    frame.Cursor++;
                    break;
                }

                bool isOut = idx < paramOutFlags.Length && paramOutFlags[idx];
                if (isOut)
                {
                    var lref = interp.EvalLValue(frame);
                    args.Add(ReadLValue(frame, lref));
                    outRefs.Add(lref);
                }
                else
                {
                    var r = interp.EvalNext(frame);
                    args.Add(r.Value);
                    if (anyOut) outRefs.Add(null);
                }
                idx++;
            }
            }
            finally { frame.Context = savedContext; interp._argDepth--; }
            // Pooled (UEVmPool): the call site hands the array and the out-ref list back after its writeback.
            object[] result = interp.Pool.RentArgs(args.Count);
            args.CopyTo(result);
            args.Clear();
            return (result, outRefs);
        }

        // Lazy && (130) / || (132) — execAndAnd_BoolBool / execOrOr_BoolBool
        // (Core.dll.c:67281/34421): A; word wSkip; decided — Code += wSkip
        // (includes EndFunctionParms). Stream layout:
        // <A> EX_Skip(<RHS>) EndFunctionParms. Eval A; if it already decides the result
        // (&& on false / || on true) SKIP the EX_Skip-wrapped RHS without evaluating it
        // (so a None-guarding RHS — `X==None || X.foo()` — never derefs None); else eval
        // the RHS. Context pinned to Self while evaluating operands (as CollectArgsWithOuts).
        public UEEvalResult EvalShortCircuit(UEFrame frame, int idx)
        {
            var savedContext = frame.Context;
            frame.Context = frame.Object;
            bool result;
            bool skipped = false;
            try
            {
                bool lhs = ToBool(EvalNext(frame).Value);
                if ((idx == 130) ? !lhs : lhs)   // && decided by false, || by true
                {
                    result = idx == 132;          // && → false, || → true
                    skipped = SkipSkipToken(frame);
                }
                else
                {
                    result = ToBool(EvalNext(frame).Value);   // eval RHS through the SkipToken
                }
            }
            finally { frame.Context = savedContext; }
            // THE TERMINATOR IS READ ONLY WITHOUT A JUMP. The EX_Skip size already
            // includes this native's EndFunctionParms (measured — see SkipSkipToken),
            // so after a taken short-circuit the cursor is PAST it. An unconditional
            // read took the terminator of the ENCLOSING expression, and its argument
            // collection swallowed the rest of the body: in `baseHarry.stateDead` this
            // gave an endless loop (Sleep/FinishAnim stopped suspending the frame,
            // because the latent check in Run comes AFTER EvalNext, which never
            // returned) — player death hung the world.
            if (!skipped)
            {
                while (frame.Cursor < frame.Tokens.Count && frame.Tokens[frame.Cursor] is Tok.DebugInfoToken)
                    frame.Cursor++;
                if (frame.Cursor < frame.Tokens.Count && frame.Tokens[frame.Cursor] is Tok.EndFunctionParmsToken)
                    frame.Cursor++;
            }
            return new UEEvalResult { Value = result, Result = UEStmtResult.Next };
        }

        // Advance past an EX_Skip + its wrapped child WITHOUT evaluating it.
        //
        // WHAT `Size` COVERS — MEASURED ON SHIPPED BYTECODE, not inferred.
        // `baseHarry.stateDead`, condition `!(BossTarget != none &&
        // BossTarget.IsA('BossQuirrel'))`:
        //     EX_Skip header         pos 13   (opcode 1 + word 2 → body from 16)
        //     Size                   18
        //     RHS body               pos 16..32   — 17 bytes
        //     EndFunctionParms "&&"  pos 33       — 1 byte
        //     EndFunctionParms "!"   pos 34
        // 17 + 1 = 18 = Size. So the shipped `wSkip` INCLUDES the parameter
        // terminator of the native that owns the skip, and the jump lands
        // RIGHT AFTER it (pos 34).
        //
        // Consequence: EndFunctionParms must NOT be consumed after a taken jump —
        // it is already passed, and an extra read takes the terminator of the
        // ENCLOSING expression.
        //
        // Returns true if the jump happened. If the target cannot be mapped, the
        // body is evaluated the usual way (the value is correct, no short-circuit
        // this time) — the terminator is still ahead, returns false.
        bool SkipSkipToken(UEFrame frame)
        {
            while (frame.Cursor < frame.Tokens.Count && frame.Tokens[frame.Cursor] is Tok.DebugInfoToken)
                frame.Cursor++;
            if (frame.Cursor < frame.Tokens.Count && frame.Tokens[frame.Cursor] is Tok.SkipToken sk)
            {
                frame.Cursor++;   // past the SkipToken header
                if (frame.Cursor < frame.Tokens.Count
                 && frame.OffsetToIndex.TryGetValue(frame.Tokens[frame.Cursor].Position + sk.Size, out var ti))
                {
                    frame.Cursor = ti;
                    return true;
                }
                EvalNext(frame);   // couldn't map the target → evaluate the child instead
            }
            else EvalNext(frame);  // no SkipToken (unexpected) → evaluate the RHS normally
            return false;
        }

        void Register<T>(UETokenHandler h) where T : Tok.Token
            => _handlers[typeof(T)] = h;
        UETokenHandler[] HandlersFor(List<Tok.Token> toks)
        {
            lock (_handlerCache)
            {
                if (!_handlerCache.TryGetValue(toks, out var arr))
                    _handlerCache[toks] = arr = new UETokenHandler[toks.Count];
                return arr;
            }
        }

        // Token kind by index, per token stream (shared by all its frames): the `is` tests against UELib's token
        // hierarchy ran on every EvalNext and every call argument. A pure function of the stream, which is immutable
        // after deserialization; an index outside the cached array falls back to the type test.
        internal enum TokenKind : byte { Other, DebugInfo, EndFunctionParms }
        static TokenKind KindOf(Tok.Token t)
            => t is Tok.DebugInfoToken ? TokenKind.DebugInfo
             : t is Tok.EndFunctionParmsToken ? TokenKind.EndFunctionParms : TokenKind.Other;
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static TokenKind TokenKindAt(UEFrame frame, int i)
        {
            var k = frame.TokenKinds ?? TokenKindsOf(frame);
            return (uint)i < (uint)k.Length ? k[i] : KindOf(frame.Tokens[i]);
        }
        static TokenKind[] TokenKindsOf(UEFrame frame)
        {
            var toks = frame.Tokens;
            TokenKind[] k;
            lock (_tokenKinds)
            {
                if (!_tokenKinds.TryGetValue(toks, out k))
                {
                    k = new TokenKind[toks.Count];
                    for (int j = 0; j < k.Length; j++) k[j] = KindOf(toks[j]);
                    _tokenKinds[toks] = k;
                }
            }
            return frame.TokenKinds = k;
        }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static bool IsDebugInfoAt(UEFrame frame, int i) => TokenKindAt(frame, i) == TokenKind.DebugInfo;

        // MSVCRT `%f` (appSprintf -> _vsnwprintf): finite values — 6 digits;
        // special values — "1.#INF00", "-1.#INF00", indefinite (0/0: quiet NaN
        // without payload) "-1.#IND00", other NaNs "1.#QNAN0" with their sign.
        // .NET would print "∞"/"NaN".
        internal static string PercentF(float f)
        {
            if (float.IsInfinity(f)) return f > 0 ? "1.#INF00" : "-1.#INF00";
            if (float.IsNaN(f))
            {
                uint bits = (uint)BitConverter.SingleToInt32Bits(f);
                // The x87 indefinite is 0xFFC00000; an ARM host produces it with a
                // POSITIVE sign (0x7FC00000), so the sign does not decide here.
                if ((bits & 0x7FFFFFFFu) == 0x7FC00000u) return "-1.#IND00";
                return ((bits & 0x80000000u) != 0 ? "-" : "") + "1.#QNAN0";
            }
            return f.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
        }

        // ArrayDim of a static array from the array-expression token that comes
        // next in the stream ([ArrayElement, Key, Array]). 0 — a form where the
        // property is not visible from the token (then the bound is the list length).
        static int StaticArrayDimAt(UEFrame frame)
        {
            int c = frame.Cursor;
            while (c < frame.Tokens.Count && frame.Tokens[c] is Tok.DebugInfoToken) c++;
            if (c >= frame.Tokens.Count) return 0;
            UObject p = frame.Tokens[c] switch
            {
                Tok.LocalVariableToken lv => lv.Object,
                Tok.InstanceVariableToken iv => iv.Object,
                Tok.DefaultVariableToken dv => dv.Object,
                Tok.StructMemberToken sm => sm.Property,
                _ => null,
            };
            return (p as UProperty)?.ArrayDim ?? 0;
        }

        // execArrayElement (Core 0x101322f2..0x1013232b): an index outside
        // [0, ArrayDim) is logged "out of bounds" and CLAMPED to the bound
        // (<0 -> 0, >=dim -> dim-1); reads and writes go to that element.
        static int StaticArrayIndex(UEFrame frame, int idx, int dim, System.Collections.IList list)
        {
            if (dim <= 0) dim = list?.Count ?? 0;
            if (idx >= 0 && idx < dim) return idx;
            UETrace.Warn($"[VM] Accessed array out of bounds ({idx}/{dim}) in "
                       + $"{frame.Object?.Class?.Name}.{frame.Func?.Name}");
            if (dim <= 0) return idx;
            return idx < 0 ? 0 : dim - 1;
        }

        // execDynArrayElement (Core 0x10132522..0x101325ab), shared by read and
        // write: idx < 0 — log and 0 WITHOUT growth; idx >= Num — grow to idx+1
        // zero-filled. The array is resolved as an lvalue so that a missing one
        // is created in its slot.
        List<object> DynArrayForElement(UEFrame frame, int idx, out int outIdx)
        {
            var arrRef = EvalLValue(frame);
            var list = ReadLValue(frame, arrRef) as List<object>;
            if (list == null) {
                list = new List<object>();
                WriteLValue(frame, arrRef, list);
            }
            if (idx < 0) {
                UETrace.Warn($"[VM] Accessed array out of bounds ({idx}/{list.Count}) in "
                           + $"{frame.Object?.Class?.Name}.{frame.Func?.Name}");
                idx = 0;
            } else {
                while (list.Count <= idx) list.Add(null);   // zeros: null reads as the type's zero
            }
            outIdx = idx;
            return list;
        }

        public UELValueRef EvalLValue(UEFrame frame)
        {
            while (frame.Cursor < frame.Tokens.Count && IsDebugInfoAt(frame, frame.Cursor))
                frame.Cursor++;
            if (frame.Cursor >= frame.Tokens.Count) {
                UETrace.Error($"[VM] EvalLValue: unexpected EOF in " +
                               $"{frame.Object?.Class?.Name}.{frame.Func?.Name}");
                return default;
            }
            var tok = frame.Tokens[frame.Cursor++];
            switch (tok)
            {
                case Tok.LocalVariableToken lv:
                {
                    var pr = PropRefAt(frame, tok, lv.Object);
                    if (pr.PropId >= 0)
                    {
                        var layout = frame.Variables.Layout;
                        int slot = layout.SlotForProp(pr.PropId);
                        return new UELValueRef {
                            Kind = UELValueKind.Local,
                            Name = layout.KeyAt(slot),
                            SlotLayout = layout,
                            Slot = slot,
                        };
                    }
                    return new UELValueRef {
                        Kind = UELValueKind.Local,
                        Name = pr.Name,
                    };
                }
                case Tok.InstanceVariableToken iv:
                {
                    var pr = PropRefAt(frame, tok, iv.Object);
                    var ctx = frame.Context;
                    if (ctx != null && pr.PropId >= 0)
                    {
                        var layout = ctx.Props.Layout;
                        int slot = layout.SlotForProp(pr.PropId);
                        return new UELValueRef {
                            Kind = UELValueKind.Instance,
                            Name = layout.KeyAt(slot),
                            Target = ctx,
                            SlotLayout = layout,
                            Slot = slot,
                        };
                    }
                    return new UELValueRef {
                        Kind = UELValueKind.Instance,
                        Name = UEObject.PropKey(ctx?.Class, PropDeclClass(iv.Object),
                                                iv.Object?.Name?.ToString()),
                        Target = ctx,
                    };
                }
                case Tok.BoolVariableToken:
                    // execBoolVariable (Core.dll.c:45463): the inner token gives the
                    // address, GProperty = the bool property. The child is the real
                    // lvalue.
                    return EvalLValue(frame);

                case Tok.StructMemberToken sm:
                    // execStructMember (Core.dll.c:4845): GPropAddr += Offset — a member
                    // write always lands in the parent's storage. Two cases:
                    //  - parent is a UEObject bag (HUD curIconMessage/DrawColor,
                    //    Cast[i]/Locs[i]) → reference-shared, write the member on the
                    //    bag directly (always works).
                    //  - parent is a VALUE struct (Vector/Rotator local or instance
                    //    var) → read-modify-write the parent lvalue.
                    // ALWAYS ON: gating this behind AllowValueLocalStructWrites dropped
                    // member writes everywhere else, while shipped always writes to the
                    // parent (execStructMember); the drop broke every `local.Z = ...`,
                    // e.g. BaseCam.GeneralStationaryModeCamera line 704
                    // `TargetPoint.Z = eye+CameraHeight` (the gameplay camera sat BELOW Harry).
                    {
                        var pr = PropRefAt(frame, tok, sm.Property);
                        string fieldName = pr.Name;
                        var pref = EvalLValue(frame);              // parent as lvalue
                        var pval = ReadLValue(frame, pref);
                        if (pval is UEObject bag)
                        {
                            if (fieldName is null)
                                return new UELValueRef { Kind = UELValueKind.Instance, Name = fieldName, Target = bag };
                            var layout = bag.Props.Layout;
                            return new UELValueRef { Kind = UELValueKind.Instance, Name = fieldName, Target = bag,
                                                     SlotLayout = layout, Slot = layout.SlotForKey(pr.NameKeyId) };
                        }
                        return new UELValueRef { Kind = UELValueKind.StructMember, Name = fieldName, ParentRef = pref };
                    }

                case Tok.DynamicArrayElementToken:
                    // UnrealScript dynamic arrays AUTO-GROW on indexed assignment:
                    // `A[i] = x` with i >= Length resizes the array to i+1 (the
                    // engine's dynamic-array element store). Static ArrayElementToken
                    // (below) never grows. Must come BEFORE the ArrayElementToken case
                    // because it derives from it (a type-pattern would otherwise swallow
                    // it). Resolve the array itself as an lvalue so a null/short array
                    // can be created and extended in place. Layout [token, Key, Array].
                    {
                        var keyR = EvalNext(frame);
                        var list = DynArrayForElement(frame, ToInt(keyR.Value), out int idx);
                        return new UELValueRef {
                            Kind = UELValueKind.ArrayIndex,
                            ListTarget = list,
                            IntIndex = idx,
                        };
                    }

                case Tok.ArrayElementToken:
                    // execArrayElement (Core.dll.c:46481): GPropAddr +=
                    // ElementSize·idx after clamping the index. Our runtime stores arrays
                    // as IList, which supports indexed assignment directly.
                    // UELib emits children in order [ArrayElementToken,
                    // KeyExpr, ArrayExpr].
                    {
                        var keyR = EvalNext(frame);
                        int dim = StaticArrayDimAt(frame);
                        var arrR = EvalNext(frame);
                        var list = arrR.Value as System.Collections.IList;
                        return new UELValueRef {
                            Kind = UELValueKind.ArrayIndex,
                            ListTarget = list,
                            IntIndex = StaticArrayIndex(frame, ToInt(keyR.Value), dim, list),
                        };
                    }

                case Tok.ContextToken:
                    // Assignment through a context chain: `A.B = X`. Mirrors
                    // the read-side execContext (Core.dll.c:47177) but the
                    // member side is resolved as an lvalue. Evaluate the
                    // object side, switch Context to it, resolve the member
                    // lvalue against that Context, then restore. (For HP1:
                    // `me.actorName.DesiredRotation = ...` etc.)
                    {
                        var objR = EvalNext(frame);
                        var savedCtx = frame.Context;
                        // A texture reference uses the same proxy as reads
                        // (ContextToken above): `LogoTexture1.bMasked = true`
                        // (FEBook.uc:495) and `Background.bTransparent = true`
                        // (baseWarning.uc:34) would otherwise lose the write (Target=null).
                        frame.Context = objR.Value as UEObject ?? TextureProxyFor(objR.Value);
                        UELValueRef inner;
                        try { inner = EvalLValue(frame); }
                        finally { frame.Context = savedCtx; }
                        return inner;
                    }

                default:
                    UETrace.Error(
                        $"[VM] EvalLValue: unsupported LHS token {tok.GetType().Name} in " +
                        $"{frame.Object?.Class?.Name}.{frame.Func?.Name} at pos {tok.Position}");
                    return default;
            }
        }

        // LValue plumbing lives in the Core (UEValue); instance/static
        // forwarders keep every call site unchanged.
        public void WriteLValue(UEFrame frame, UELValueRef lref, object value)
            => UEValue.WriteLValue(frame, lref, value);
        public static object ReadLValue(UEFrame frame, UELValueRef lref)
            => UEValue.ReadLValue(frame, lref);
        static object GetStructMember(object s, string m) => UEValue.GetStructMember(s, m);
        static object SetStructMember(object s, string m, object value)
            => UEValue.SetStructMember(s, m, value);

        // execSwitch (Core.dll.c:44827): case loop until NextOffset == 0xFFFF
        // (default); a match exits into the body; a miss — Code = Script + 1 +
        // NextOffset. Walk the sequence of CaseTokens; for each:
        //   - default case (CodeOffset == 0xFFFF, no Condition) — break,
        //     fall through to its body (the cursor is positioned after it).
        //   - regular case — evaluate its Condition, compare with the
        //     switch value. On match, break (fall through). On mismatch,
        //     jump to NextOffset (the next case's start byte).
        // The case body executes via the normal Run loop after this method
        // returns. CaseToken hit standalone (fall-through between cases) is
        // a no-op (Tok.CaseToken handler below).
        void ProcessSwitch(UEFrame frame, object condVal)
        {
            while (true)
            {
                while (frame.Cursor < frame.Tokens.Count
                       && frame.Tokens[frame.Cursor] is Tok.DebugInfoToken)
                    frame.Cursor++;
                if (frame.Cursor >= frame.Tokens.Count) {
                    UETrace.Error(
                        $"[VM] Switch: ran out of tokens looking for a case in " +
                        $"{frame.Object?.Class?.Name}.{frame.Func?.Name}");
                    return;
                }
                var tok = frame.Tokens[frame.Cursor++];
                if (!(tok is Tok.CaseToken ct)) {
                    UETrace.Error(
                        $"[VM] Switch: expected CaseToken, got {tok.GetType().Name} " +
                        $"at pos {tok.Position} in {frame.Object?.Class?.Name}.{frame.Func?.Name}");
                    return;
                }
                if (ct.CodeOffset == ushort.MaxValue) {
                    // Default case (UELib `IsDefault`).
                    return;
                }
                var caseVal = EvalNext(frame).Value;
                if (UEValue.SwitchCaseMatches(condVal, caseVal)) return;  // matched, fall through to body
                // Mismatch: jump to NextOffset (start of the next case).
                if (frame.OffsetToIndex.TryGetValue(ct.CodeOffset, out var idx)) {
                    frame.Cursor = idx;
                } else {
                    UETrace.Error(
                        $"[VM] Switch: NextOffset 0x{ct.CodeOffset:X} not found in " +
                        $"{frame.Object?.Class?.Name}.{frame.Func?.Name}");
                    return;
                }
            }
        }

        // Comparisons: execSwitch — appStricmp for strings (size 0), otherwise
        // appMemcmp by size (UEValue.SwitchCaseMatches); StructCmp — property
        // Identical (e.g. UStrProperty::Identical, Core.dll.c:36474).
        // EX_StructCmpEq semantics: two struct instances are equal iff EVERY member is equal.
        // Struct values here are UEObject(IsStruct) (custom structs, Props dict) or intrinsic
        // value-structs (Vector3/UERotator/Color). Field-by-field via ValueEquals, which coerces
        // null↔type-zero — so a struct compares equal to a default/zero struct (the VM's
        // absent-property-reads-as-zero convention). (#audit gap 12)
        // The VM value semantics live in the Core (HP1.UEScript.UEValue);
        // these forwarders keep every existing call site unchanged.
        public static bool StructEquals(object a, object b) => UEValue.StructEquals(a, b);
        public static bool ValueEquals(object a, object b) => UEValue.ValueEquals(a, b);
        public static byte AsByte(object v) => UEValue.AsByte(v);
        public static Vector3 AsVector3(object v) => UEValue.AsVector3(v);
        public static UERotator AsRotator(object v) => UEValue.AsRotator(v);
        public static float AsFloat(object v) => UEValue.AsFloat(v);
        static int LeadingAtoi(string s) => UEValue.LeadingAtoi(s);
        static float LeadingAtof(string s) => UEValue.LeadingAtof(s);
        public static int ToInt(object v) => UEValue.ToInt(v);
        public static bool ToBool(object v) => UEValue.ToBool(v);
    }
}
