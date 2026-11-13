// RegisterTokenHandlers: the bytecode token handlers (the exec* bodies of UObject), one table
// built in the constructor. Fields live in UEInterpreter.cs.
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
        // Handlers by UELib token type; semantics follow the Core.dll exec* bodies.
        void RegisterTokenHandlers()
        {
            // EX_Return 0x04: ProcessInternal (Core.dll.c:21624) steps statements
            // until opcode 0x04, then the Step of the return expression writes Result.
            // Here — evaluate the child expression (HP1 packages always have one) and
            // return Result=Return.
            Register<Tok.ReturnToken>((interp, frame, tok) => {
                var inner = interp.EvalNext(frame);
                return new UEEvalResult { Value = inner.Value, Result = UEStmtResult.Return };
            });

            // execEatString (Core.dll.c:42021): evaluate a string expression and
            // discard the value (a call whose string
            // return is unused, e.g. FEBook.OpenBook's ConsoleCommand("Snap 3")).
            // Missing handler aborted OpenBook at pos 0 → the menu page never
            // changed and curBackground stayed the zero bag.
            Register<UELib.Core.EatStringToken>((interp, frame, tok) => {
                interp.EvalNext(frame);
                return new UEEvalResult { Result = UEStmtResult.Next };
            });

            // execNothing (Core.dll.c:31278): empty body. Here — value null,
            // Result=Next. Child expression of `return;` (void) and optional slots.
            Register<Tok.NothingToken>((interp, frame, tok) =>
                new UEEvalResult { Value = null, Result = UEStmtResult.Next });

            // A skipped optional argument. UObject::CallFunction (Core.dll.c:7715)
            // zeroes Locals before stepping arguments, so in shipped such a parameter
            // is the ZERO of its type, not a "default value". UELib emits
            // EmptyParmToken (OtherTokens.cs:31) for an explicitly skipped middle
            // optional; the value is the same as NothingToken (null). HP1 v76 usually
            // omits trailing arguments; registered so that a skip in the middle
            // does not give "No handler".
            Register<Tok.EmptyParmToken>((interp, frame, tok) =>
                new UEEvalResult { Value = null, Result = UEStmtResult.Next });

            // execFinalFunction (Core.dll.c:52837): a non-virtual call of a known
            // UFunction → this->CallFunction, where this is the current context
            // object; arguments in stream order (CallFunction maps to Invoke here).
            //
            // Layout (UELib FunctionTokens.cs:166-176): UFunction reference, then
            // argument expressions up to EndFunctionParmsToken (0x16) — consumed by
            // EvalNext up to that terminator.
            //
            // && / || (130/132) — natives with EX_Skip (execAndAnd_BoolBool /
            // execOrOr_BoolBool), no separate path needed here.
            // execLocalVariable (Core.dll.c:65110): GPropAddr = Locals + Offset.
            // The port stores locals and parameters in the frame layout (slots,
            // BindParams from the caller's arguments); reads go by slot; writes go
            // through EvalLValue.
            //
            // UELib FieldTokens.cs:48-51 — LocalVariableToken inherits
            // FieldToken (cs:10-26); its Object is the resolved UProperty.
            Register<Tok.LocalVariableToken>((interp, frame, tok) => {
                var ft = (Tok.LocalVariableToken)tok;
                var pr = PropRefAt(frame, tok, ft.Object);
                string name = pr.Name;
                if (string.IsNullOrEmpty(name)) {
                    UETrace.Error(
                        $"[VM] LocalVariableToken with unresolved Object in " +
                        $"{frame.Object?.Class?.Name}.{frame.Func?.Name} at pos {tok.Position}");
                    return new UEEvalResult { Result = UEStmtResult.Next };
                }
                var vars = frame.Variables;
                object val = pr.PropId >= 0 ? vars.GetSlot(vars.Layout.SlotForProp(pr.PropId)) : vars.Get(pr.NameKeyId);
                return new UEEvalResult { Value = val, Result = UEStmtResult.Next };
            });

            // execInstanceVariable (Core.dll.c:61661): GPropAddr = this + Offset,
            // where this is the current context object (not Self: in a chain A.B the
            // context while evaluating B is A). The port stores properties in the
            // UEObject layout slots (the analogue of the UProperty offset).
            //
            // UELib FieldTokens.cs:43-46 — InstanceVariableToken : FieldToken;
            // Object is the resolved UProperty.
            Register<Tok.InstanceVariableToken>((interp, frame, tok) => {
                var ft = (Tok.InstanceVariableToken)tok;
                var pr = PropRefAt(frame, tok, ft.Object);
                string name = pr.Name;
                if (string.IsNullOrEmpty(name)) {
                    UETrace.Error(
                        $"[VM] InstanceVariableToken with unresolved Object in " +
                        $"{frame.Object?.Class?.Name}.{frame.Func?.Name} at pos {tok.Position}");
                    return new UEEvalResult { Result = UEStmtResult.Next };
                }
                object val = null;
                var ctx = frame.Context;
                if (ctx != null)
                {
                    // Property slot in the object layout — like the UProperty offset.
                    var props = ctx.Props;
                    val = pr.PropId >= 0
                        ? props.GetSlot(props.Layout.SlotForProp(pr.PropId))
                        : props.Get(UEObject.PropKey(ctx.Class, PropDeclClass(ft.Object), name));
                }
                if (val == null && pr.Special != UEPropRef.None)
                {
                    // Global singleton fallback: HP1 reads `PlayerHarry` bare as an
                    // instance var on many classes; UE1 assigns it at runtime. We never
                    // run that assignment, so resolve it to the registered player bridge
                    // object (set by HP1PlayerHarryBridge) when the prop isn't present.
                    if (pr.Special == UEPropRef.PlayerHarry)
                        val = interp.PlayerHarry;
                    // UE1's engine assigns `Level` (the world LevelInfo) to every actor
                    // at load — we never run that, so resolve a bare `Level` read to the
                    // bootstrapped world (carries NetMode + Game→GameInfo→BaseMutator so
                    // Actor.PreBeginPlay's relevance cull runs faithfully). Same pattern
                    // as PlayerHarry above.
                    else if (pr.Special == UEPropRef.Level)
                        val = interp.GetLevelInfo();
                    // Object.Class is a UObject property (+0x24), not stored in the
                    // property bag: reads as the object's class. Without
                    // this, `X.Class == Class'Pkg.Y'` read null and every spell-type
                    // check (tut1Peeves.TakeSpellEffect etc.) silently failed.
                    else if (pr.Special == UEPropRef.ClassProp)
                        val = ctx?.Class;
                    // Actor.XLevel (+0x80, const transient) — the actor's ULevel; set by the engine at spawn/load,
                    // script only reads it. The only reader in campaign bytecode is WarpZoneInfo.ForceGenerate
                    // (`OtherSideLevel = XLevel`), and OtherSideLevel is only compared with None (Generate,
                    // OccludeBsp 0x10b23e69). So a non-null Level-class object per world (XLevel) suffices.
                    else if (pr.Special == UEPropRef.XLevel)
                        val = interp.XLevel;
                }
                return new UEEvalResult { Value = val, Result = UEStmtResult.Next };
            });

            // UELib FieldTokens.cs:64 DefaultVariableToken : FieldToken — `default.X`
            // reads the CLASS default value of property X (not the instance value).
            // Class = frame.ClassContext when inside `SomeClass.default.X`
            // (ClassContextToken), else the current Context/Object's class.
            // E.g. SpellLearnTrigger: `Spell.default.Gesture`; baseHarry.SetMesh.
            Register<Tok.DefaultVariableToken>((interp, frame, tok) => {
                var ft = (Tok.DefaultVariableToken)tok;
                string name = ft.Object?.Name?.ToString();
                var cls = frame.ClassContext ?? frame.Context?.Class ?? frame.Object?.Class;
                // `<class>.default.Class` — the default object's intrinsic Class IS the class
                // itself. GetClassDefault has no "Class" entry (it's not a serialized default),
                // so `Spell.default.Class == Class'HPBase.spellFlip'`
                // (SpellLearnTrigger.TeacherDrawsSpell) must be true for Quirrell to play
                // the demo anim.
                if (string.Equals(name, "Class", StringComparison.OrdinalIgnoreCase))
                    return new UEEvalResult { Value = cls, Result = UEStmtResult.Next };
                return new UEEvalResult { Value = interp.GetClassDefault(cls,
                    UEObject.PropKey(cls, PropDeclClass(ft.Object), name)), Result = UEStmtResult.Next };
            });

            // UELib ContextTokens.cs:77 ClassContextToken : ContextToken —
            // `<classExpr>.member` where the context is a CLASS (e.g.
            // `Spell.default.Gesture`, `class'X'.SomeConst`). Set frame.ClassContext
            // for the inner expr (consumed by DefaultVariableToken/const reads),
            // then restore. The inner expression also runs with the class's DEFAULT
            // OBJECT as Context: execClassContext 0x10132117 loads ecx =
            // [class+0x4dc] (Defaults.GetData()) as `this` for the GNatives call, so
            // `Message.static.ClientReceive(...)` (PlayerPawn.ReceiveLocalizedMessage)
            // resolves on the message class, not on the caller.
            Register<Tok.ClassContextToken>((interp, frame, tok) => {
                var objR = interp.EvalNext(frame);
                // execClassContext (Core 0x10132141): class None — "Accessed None",
                // the inner expression is SKIPPED by SkipSize, result zero (as in
                // ContextToken); it must not run with `default.Y` read from the
                // caller's class.
                if (objR.Value is null)
                {
                    string anKey = $"{frame.Object?.Class?.Name}.{frame.Func?.Name} at pos {tok.Position}";
                    if (_loggedAccessedNone.Add(anKey))
                        UETrace.Info($"[VM] Accessed None in {anKey} (repeats suppressed)");
                    var cct = (Tok.ContextToken)tok;
                    if (frame.Cursor < frame.Tokens.Count)
                    {
                        int targetByte = frame.Tokens[frame.Cursor].Position + cct.SkipSize;
                        while (frame.Cursor < frame.Tokens.Count
                            && frame.Tokens[frame.Cursor].Position < targetByte)
                            frame.Cursor++;
                    }
                    return new UEEvalResult { Value = null, Result = UEStmtResult.Next };
                }
                var saved = frame.ClassContext;
                var savedContext = frame.Context;
                frame.ClassContext = objR.Value as UClass
                                   ?? (objR.Value as UEObject)?.Class;
                frame.Context = interp.ClassContextObject(frame.ClassContext);
                UEEvalResult rhsR;
                try { rhsR = interp.EvalNext(frame); }
                finally { frame.ClassContext = saved; frame.Context = savedContext; }
                return new UEEvalResult { Value = rhsR.Value, Result = UEStmtResult.Next };
            });

            // execBoolVariable (Core.dll.c:45463): the inner variable token runs
            // with Result=NULL and sets GPropAddr, then Result =
            // (*GPropAddr & BitMask) != 0; no address → false. The port stores bool
            // as bool, so the read is delegated to the child token
            // (InstanceVariableToken / LocalVariableToken).
            //
            // UELib FieldTokens.cs:124-136 — BoolVariableToken consumes one
            // child token via DeserializeNext.
            Register<Tok.BoolVariableToken>((interp, frame, tok) => {
                return interp.EvalNext(frame);
            });

            // execGotoLabel -> UObject::GotoLabel (Core 0x10131df0): the label is
            // looked up from the CURRENT state node (StateNode) along the SuperField
            // chain; not found or None — Code = NULL: state code STOPS (not an error
            // followed by the next statement).
            Register<Tok.GotoLabelToken>((interp, frame, tok) => {
                var nameR = interp.EvalNext(frame);
                string label = nameR.Value?.ToString();
                if (!string.IsNullOrEmpty(label)
                 && !string.Equals(label, "None", StringComparison.OrdinalIgnoreCase)) {
                    int fidx = interp.ResolveStateLabel(frame, label);
                    if (fidx >= 0) {
                        frame.Cursor = fidx;
                        return new UEEvalResult { Result = UEStmtResult.Next };
                    }
                }
                UETrace.Error(
                    $"[VM] GotoLabel '{label}' not found in " +
                    $"{frame.Object?.Class?.Name}.{frame.Func?.Name} — state code stops");
                frame.LatentState = LatentRunState.Stop;
                frame.Cursor = frame.Tokens.Count;
                return new UEEvalResult { Result = UEStmtResult.Next };
            });

            // execJump (Core.dll.c:37072): Code = Script + offset. The Run loop
            // resolves that offset to a token index via OffsetToIndex. The runaway
            // counter is incremented here and in execJumpIfNot, as in shipped.
            //
            // UELib JumpTokens.cs:83-101 — reads CodeOffset (ushort).
            Register<Tok.JumpToken>((interp, frame, tok) => {
                var jt = (Tok.JumpToken)tok;
                interp.CountRunawayJump(frame);
                return new UEEvalResult {
                    Result = UEStmtResult.Jump,
                    JumpAddress = jt.CodeOffset,
                };
            });

            // execJumpIfNot (Core.dll.c:45428): offset word, then the condition;
            // false — Code = Script + offset. Otherwise
            // continue with Result=Next (the implicit fall-through into the
            // if-body).
            //
            // UELib JumpTokens.cs:335-363 — JumpIfNotToken : JumpToken; reads
            // CodeOffset (from base) and one child condition token. The child
            // token follows immediately in the flat stream so we read it with
            // EvalNext.
            // UObject::execAssert (Core.dll thunk 0x10101e38 -> 0x10133100): line
            // number word, then one boolean expression; false ->
            // FFrame::Logf(NAME_Critical 0x88, "Assertion failed, line %i"), and
            // FFrame::Serialize on 0x88 goes to GError — a fatal error.
            // True — nothing. Without a handler ANY assert (including passing ones:
            // UWindowList.InsertItemBefore, GraftLeft...) would stop execution as an
            // "unknown token".
            Register<Tok.AssertToken>((interp, frame, tok) => {
                var at = (Tok.AssertToken)tok;
                var cond = interp.EvalNext(frame);
                if (!ToBool(cond.Value))
                    throw new InvalidOperationException(
                        $"Assertion failed, line {at.Line} ({frame.Object?.Class?.Name}.{frame.Func?.Name}) — "
                        + "shipped: FFrame::Logf(NAME_Critical) -> GError, fatal");
                return new UEEvalResult { Result = UEStmtResult.Next };
            });

            Register<Tok.JumpIfNotToken>((interp, frame, tok) => {
                var jt = (Tok.JumpIfNotToken)tok;
                interp.CountRunawayJump(frame);
                var cond = interp.EvalNext(frame);
                if (!ToBool(cond.Value)) {
                    if (UECoverage.On) UECoverage.Hit(frame.Func, jt.Position, -1);
                    return new UEEvalResult {
                        Result = UEStmtResult.Jump,
                        JumpAddress = jt.CodeOffset,
                    };
                }
                // Empty if body: the target equals the point after the condition, and
                // the original's probe (comparing Code with the target) records J here — same here.
                if (UECoverage.On) UECoverage.Hit(frame.Func, jt.Position,
                    frame.Cursor < frame.Tokens.Count && frame.Tokens[frame.Cursor].Position == jt.CodeOffset ? -1 : -2);
                return new UEEvalResult { Result = UEStmtResult.Next };
            });

            Register<Tok.FinalFunctionToken>((interp, frame, tok) => {
                var ft = (Tok.FinalFunctionToken)tok;
                string lbl = ft.Function?.Name?.ToString() ?? "?";
                var (args, outRefs) = CollectArgsWithOuts(interp, frame, ft.Function, lbl);
                if (ft.Function == null) {
                    UETrace.Error(
                        $"[VM] FinalFunction with null target in " +
                        $"{frame.Object?.Class?.Name}.{frame.Func?.Name}");
                    return new UEEvalResult { Result = UEStmtResult.Next };
                }
                // execFinalFunction (Core.dll.c:52837) → CallFunction on the context
                // object: a final call runs on the CONTEXT object, not Self. For a bare /
                // Super call Context == Object (Self); for `A.FinalFunc()` Context
                // == A. (Previously invoked on frame.Object → ran the body against
                // the caller for member-targeted final calls.) None → Accessed-None.
                var target = frame.Context;
                if (target == null) {
                    UETrace.Info(
                        $"[VM] Accessed None calling final '{lbl}' in " +
                        $"{frame.Object?.Class?.Name}.{frame.Func?.Name}");
                    return new UEEvalResult { Result = UEStmtResult.Next };
                }
                var argsArr = args;
                // Bridge-method intercept (as in VirtualFunctionToken): the bridge
                // wins over the class bytecode. Out-params still flow back below.
                //
                // Canvas.SetPos/SetOrigin/SetClip/DrawIcon all have bodies in Engine.u
                // and run as bytecode; intercepting a name that has a body would
                // suppress virtual dispatch — forbidden structurally
                // (verify_architecture) and checked live.
                if (target.BridgeMethods != null && target.BridgeMethods.TryGetValue(lbl, out var bridgeFn)) {
                    var bret = bridgeFn(argsArr);
                    for (int i = 0; i < outRefs.Count; i++)
                        if (outRefs[i].HasValue && i < argsArr.Length)
                            interp.WriteLValue(frame, outRefs[i].Value, argsArr[i]);
                    return new UEEvalResult { Value = bret, Result = UEStmtResult.Next };
                }
                var ret = interp.Invoke(target, ft.Function, argsArr);
                // UObject::CallFunction (Core.dll.c:7715): out-params are copied back
                // starting from the LAST — observable under aliasing `F(x, x)`.
                for (int i = outRefs.Count - 1; i >= 0; i--) {
                    if (outRefs[i].HasValue && i < argsArr.Length)
                        interp.WriteLValue(frame, outRefs[i].Value, argsArr[i]);
                }
                interp.Pool.ReleaseOutRefs(outRefs, NoOutRefs);
                interp.Pool.ReleaseArgs(argsArr);
                return new UEEvalResult { Value = ret, Result = UEStmtResult.Next };
            });

            // Constants: execIntConst / IntConstByte / ByteConst / FloatConst /
            // StringConst / NameConst / ObjectConst (Core.dll.c:33925, 1998, 22652,
            // 30583, 43785, 30151, 55595) — the literal value from the stream. The
            // parsed value comes from UELib ConstTokens.cs.
            Register<Tok.TrueToken>((interp, frame, tok) =>
                new UEEvalResult { Value = true, Result = UEStmtResult.Next });
            Register<Tok.FalseToken>((interp, frame, tok) =>
                new UEEvalResult { Value = false, Result = UEStmtResult.Next });
            Register<Tok.IntZeroToken>((interp, frame, tok) =>
                new UEEvalResult { Value = 0, Result = UEStmtResult.Next });
            Register<Tok.IntOneToken>((interp, frame, tok) =>
                new UEEvalResult { Value = 1, Result = UEStmtResult.Next });
            Register<Tok.IntConstToken>((interp, frame, tok) =>
                new UEEvalResult { Value = ((Tok.IntConstToken)tok).Value, Result = UEStmtResult.Next });
            Register<Tok.IntConstByteToken>((interp, frame, tok) =>
                new UEEvalResult { Value = (int)((Tok.IntConstByteToken)tok).Value, Result = UEStmtResult.Next });
            Register<Tok.ByteConstToken>((interp, frame, tok) =>
                new UEEvalResult { Value = ((Tok.ByteConstToken)tok).Value, Result = UEStmtResult.Next });
            Register<Tok.FloatConstToken>((interp, frame, tok) =>
                new UEEvalResult { Value = ((Tok.FloatConstToken)tok).Value, Result = UEStmtResult.Next });
            Register<Tok.StringConstToken>((interp, frame, tok) =>
                new UEEvalResult { Value = UEValue.AnsiStringConst(((Tok.StringConstToken)tok).Value), Result = UEStmtResult.Next });
            Register<Tok.UnicodeStringConstToken>((interp, frame, tok) =>
                new UEEvalResult { Value = ((Tok.UnicodeStringConstToken)tok).Value, Result = UEStmtResult.Next });
            // NameConstToken — wrap in UEName so name comparisons stay
            // distinct from string at runtime (UE1 `name` vs `string`).
            Register<Tok.NameConstToken>((interp, frame, tok) =>
                new UEEvalResult { Value = new UEName(((Tok.NameConstToken)tok).Name), Result = UEStmtResult.Next });
            // ObjectConstToken yields the parsed UObject ref. Our runtime
            // doesn't wrap arbitrary UObjects yet; pass through and let
            // downstream choke loud if it matters. A mesh takes the same form as
            // in defaults (UEObject.MeshLiteralRef).
            Register<Tok.ObjectConstToken>((interp, frame, tok) =>
                new UEEvalResult { Value = UEObject.MeshLiteralRef(((Tok.ObjectConstToken)tok).ObjectRef), Result = UEStmtResult.Next });
            // NoneToken (ConstTokens.cs:49-56) — `none` literal for object /
            // name refs; execNoObject (Core.dll.c:16867): *Result = 0. UELib also
            // emits the NoObjectToken / EmptyDelegateToken subclasses (both
            // `none`); dispatch is by exact type so register all three.
            Register<Tok.NoneToken>((interp, frame, tok) =>
                new UEEvalResult { Value = null, Result = UEStmtResult.Next });
            Register<Tok.NoObjectToken>((interp, frame, tok) =>
                new UEEvalResult { Value = null, Result = UEStmtResult.Next });
            Register<Tok.EmptyDelegateToken>((interp, frame, tok) =>
                new UEEvalResult { Value = null, Result = UEStmtResult.Next });
            // SelfToken (ConstTokens.cs:58-65) — refers to the active object.
            // execSelf (Core.dll.c:7696): *Result = this.
            Register<Tok.SelfToken>((interp, frame, tok) =>
                new UEEvalResult { Value = frame.Object, Result = UEStmtResult.Next });

            // VectorConstToken (ConstTokens.cs:269-286) — `vect(x,y,z)` literal.
            Register<Tok.VectorConstToken>((interp, frame, tok) => {
                var vc = (Tok.VectorConstToken)tok;
                return new UEEvalResult {
                    Value = new Vector3(vc.Vector.X, vc.Vector.Y, vc.Vector.Z),
                    Result = UEStmtResult.Next,
                };
            });
            // RotationConstToken (ConstTokens.cs:250-267) — `rot(p,y,r)` literal.
            Register<Tok.RotationConstToken>((interp, frame, tok) => {
                var rc = (Tok.RotationConstToken)tok;
                return new UEEvalResult {
                    Value = new UERotator(rc.Rotation.Pitch, rc.Rotation.Yaw, rc.Rotation.Roll),
                    Result = UEStmtResult.Next,
                };
            });

            // Primitive type-conversion casts: bodies of Core.dll exec*To*, verified
            // against the disasm (open item: StringToFloat/StringToInt edge cases).
            // Each converts the child expression's value to the target type. UELib folds the whole family
            // into a single PrimitiveCastToken with a CastOpCode byte
            // (UELib UnrealTokens.cs:486 — the CastToken enum).
            //
            // PrimitiveInlineCastToken (CastTokens.cs:275-282) is the same
            // token packed inline into the opcode byte; same handler.
            UETokenHandler castHandler = (interp, frame, tok) => {
                var pc = (Tok.PrimitiveCastToken)tok;
                var inner = interp.EvalNext(frame);
                object v = inner.Value;
                object converted;
                switch (pc.CastOpCode)
                {
                    case CastToken.ByteToInt:    converted = (int)((byte?)AsByte(v) ?? 0); break;
                    case CastToken.IntToByte:    converted = (byte)(ToInt(v) & 0xFF); break;
                    case CastToken.IntToFloat:   converted = (float)ToInt(v); break;
                    case CastToken.FloatToInt:   converted = UEValue.FloatToInt(AsFloat(v)); break;
                    case CastToken.FloatToByte:  converted = UEValue.FloatToByte(AsFloat(v)); break;
                    case CastToken.ByteToFloat:  converted = (float)AsByte(v); break;
                    case CastToken.BoolToByte:   converted = (byte)(ToBool(v) ? 1 : 0); break;
                    case CastToken.BoolToInt:    converted = ToBool(v) ? 1 : 0; break;
                    case CastToken.BoolToFloat:  converted = ToBool(v) ? 1f : 0f; break;
                    case CastToken.ByteToBool:   converted = AsByte(v) != 0; break;
                    case CastToken.IntToBool:    converted = ToInt(v) != 0; break;
                    case CastToken.FloatToBool:  { float fb = AsFloat(v); converted = !(fb == 0f || float.IsNaN(fb)); break; }   // Core 0x10135100: NaN -> false
                    case CastToken.ObjectToBool: converted = v != null; break;
                    // Shipped: true if the FName index != 0 (NAME_None). FName is
                    // interned case-insensitively, and the empty string is also
                    // NAME_None, so 'none' and '' give false.
                    case CastToken.NameToBool:
                        converted = !(v == null
                                      || (v is UEName n && (string.IsNullOrEmpty(n.Value)
                                          || string.Equals(n.Value, "None", StringComparison.OrdinalIgnoreCase)))); break;
                    // String→number: UE1 appAtoi/appAtof parse the LEADING number and stop at
                    // the first non-numeric char ("12abc"→12); a whole-string TryParse gave 0.
                    // (Core.dll.c:12188/34471 appAtoi/appAtof.)
                    case CastToken.StringToByte:
                        converted = (byte)(LeadingAtoi(v?.ToString()) & 0xFF); break;
                    case CastToken.StringToInt:
                        converted = LeadingAtoi(v?.ToString()); break;
                    case CastToken.StringToFloat:
                        converted = LeadingAtof(v?.ToString()); break;
                    case CastToken.StringToBool: {
                        // Native execStringToBool (Core.dll.c:66327-66352): "True"/"False"
                        // (case-insensitive, incl. localized GTrue/GFalse) → true/false, else
                        // appAtoi(str)!=0. The old heuristic made "hello"/"00" → true.
                        string sbv = v as string ?? "";
                        if ("true".Equals(sbv, StringComparison.OrdinalIgnoreCase)) converted = true;
                        else if ("false".Equals(sbv, StringComparison.OrdinalIgnoreCase)) converted = false;
                        else converted = LeadingAtoi(sbv) != 0;
                        break; }
                    // `%i` (appSprintf) is locale-independent; .NET ToString() without
                    // InvariantCulture uses the culture's minus sign (U+2212 in some locales).
                    case CastToken.ByteToString:    converted = AsByte(v).ToString(System.Globalization.CultureInfo.InvariantCulture); break;
                    case CastToken.IntToString:     converted = ToInt(v).ToString(System.Globalization.CultureInfo.InvariantCulture); break;
                    case CastToken.BoolToString:    converted = ToBool(v) ? "True" : "False"; break;
                    case CastToken.FloatToString:
                        // UE1 FString::Printf("%f") = 6 fixed decimals
                        // (Core.dll.c:35345). Was default ToString ("1.5"), inconsistent with the
                        // port's own VectorToString ("F6") and the comment below. → "1.500000".
                        converted = PercentF(AsFloat(v)); break;
                    case CastToken.NameToString:    converted = v?.ToString() ?? "None"; break;
                    // string → name (UE1 opcode 0x5A; UELib emits it as
                    // StringToName after the dll patch). HP1 baseScript.
                    // BroadcastTrigger does `name(Event)`; wrap as UEName so it
                    // compares against name literals ('None', tags). Empty/null
                    // string → 'None' (UE1 name-of-empty convention).
                    case CastToken.StringToName:
                        converted = new UEName(string.IsNullOrEmpty(v as string) ? "None" : (string)v); break;
                    // Vector/Rotator: FVector::Rotation (0x1014e460) and
                    // (UnitCoords/R).XAxis (0x1013ab10) — shipped bodies, see UEValue.
                    case CastToken.VectorToRotator: converted = UEValue.RotatorFromVector(AsVector3(v)); break;
                    case CastToken.RotatorToVector: converted = UEValue.RotatorToVector(AsRotator(v)); break;
                    case CastToken.ObjectToString:
                        converted = ObjectPathName(v); break;
                    // Vector/Rotator string + bool conversions: execVectorToBool
                    // (Core.dll.c:15688), execRotatorToBool (:65309), execVectorToString
                    // (:4283, "%f,%f,%f"), execRotatorToString (:5094, "%i,%i,%i" by & 0xFFFF),
                    // execStringToVector (:824), execStringToRotator (:6422).
                    // Truth-tests like `if (Velocity)` and string(Location)/string(rot) use UE1's
                    // bare "x,y,z" format. Vectors are held in UE space, so we print the
                    // UE-space components. Float format = %f (6 dp).
                    case CastToken.VectorToBool: {
                        converted = UEValue.VectorToBool(AsVector3(v)); break; }
                    case CastToken.RotatorToBool: {
                        // execRotatorToBool: components & 0xFFFF before the check — a
                        // component == a nonzero multiple of 65536 is zero rotation.
                        var rb = AsRotator(v);
                        converted = (rb.Pitch & 0xffff) != 0 || (rb.Yaw & 0xffff) != 0 || (rb.Roll & 0xffff) != 0; break; }
                    case CastToken.VectorToString: {
                        var vs = AsVector3(v);
                        converted = PercentF(vs.x) + "," + PercentF(vs.y) + "," + PercentF(vs.z); break; }
                    case CastToken.RotatorToString: {
                        var rs = AsRotator(v);
                        converted = (rs.Pitch & 0xffff) + "," + (rs.Yaw & 0xffff)
                                  + "," + (rs.Roll & 0xffff); break; }
                    // execStringToVector (Core.dll.c:824) / execStringToRotator (:6422):
                    // X = appAtof(s); after the FIRST ',' — Y; after the next ',' — Z
                    // (appStrstr). Missing components are 0: "5,6" -> (5,6,0).
                    case CastToken.StringToVector:  converted = UEValue.StringToVector((v as string) ?? ""); break;
                    case CastToken.StringToRotator: converted = UEValue.StringToRotator((v as string) ?? ""); break;
                    default:
                        UETrace.Error(
                            $"[VM] Unsupported PrimitiveCast {pc.CastOpCode} in " +
                            $"{frame.Object?.Class?.Name}.{frame.Func?.Name}");
                        converted = v;
                        break;
                }
                return new UEEvalResult { Value = converted, Result = UEStmtResult.Next };
            };
            Register<Tok.PrimitiveCastToken>(castHandler);
            Register<Tok.PrimitiveInlineCastToken>(castHandler);

            // execDynamicCast (Core.dll.c:28229, body 0x10134330): class from the stream,
            // then the value; IsA along the SuperField chain by class IDENTITY
            // (+0x24 / +0x28); failure → None.
            // i.e. a checked downcast: returns the object if it IsA the target
            // class, else None. Drives HP1's `if (BaseCam(x) != none)` /
            // `baseChar(x).CutMoveTo(...)` dispatch in handleCast.
            //
            // UELib CastTokens.cs:284-301 — DynamicCastToken reads the target
            // UClass then one child value expression.
            Register<Tok.DynamicCastToken>((interp, frame, tok) => {
                var dc = (Tok.DynamicCastToken)tok;
                var v = interp.EvalNext(frame);
                string targetName = dc.CastClass?.Name;
                // Class identity, not name (execDynamicCast 0x1013436c):
                // a same-named class from another package is not the same class.
                if (v.Value is UEObject obj)
                    return new UEEvalResult {
                        Value = UEClassRegistry.Shared.IsChildOfClass(obj.Class, dc.CastClass) ? obj : null,
                        Result = UEStmtResult.Next };
                object passthrough = null;
                // UEResourceRef markers (DynamicLoadObject / struct defaults)
                // represent a real UE1 resource of marker.Class — the cast to
                // that class succeeds in UE1, so the marker passes through.
                // Without this, FEBook.Created's `Texture(DynamicLoadObject(
                // "MenuArt.MoonTitle1", ...))` returned null and the menu
                // background fields were written as None.
                // The cast is IsA (UObject::IsA along the superclass chain), not name
                // equality: Texture(DynamicLoadObject(..., Class'Fire.FireTexture'))
                // succeeds in shipped (FireTexture -> FractalTexture -> Texture).
                // Exact comparison gave None to the BroomHoopStage1-5 rings
                // (164 actors; checked on the live 1.53 original, Skin=...BTStageN).
                if (v.Value is UEResourceRef rr
                 && (string.Equals(rr.Class, targetName, StringComparison.OrdinalIgnoreCase)
                  || ClassIsA(UEClassRegistry.Shared?.Find(rr.Class), targetName)))
                    passthrough = v.Value;
                return new UEEvalResult { Value = passthrough, Result = UEStmtResult.Next };
            });

            // UE1 metaclass cast `Class<T>(expr)` — MetaClassCastToken
            // (CastTokens.cs:312, a subclass of DynamicCastToken; exact-type
            // dispatch needs its own handler). execMetaCast (Core.dll.c:63121): the value
            // is a class; IsChildOf the target metaclass T by identity, otherwise None.
            // WindowConsole.CreateRootWindow casts
            // DynamicLoadObject(...) → Class<UWindowRootWindow>; the `new` operator
            // then instantiates the result.
            Register<Tok.MetaClassCastToken>((interp, frame, tok) => {
                var mc = (Tok.MetaClassCastToken)tok;
                var v = interp.EvalNext(frame);
                var cls = v.Value as UClass;
                string targetName = mc.CastClass?.Name;
                if (cls != null && mc.CastClass != null && !UEClassRegistry.Shared.IsChildOfClass(cls, mc.CastClass))
                    cls = null;
                return new UEEvalResult { Value = cls, Result = UEStmtResult.Next };
            });

            // UE1 `new` operator. UELib NewToken (OtherTokens.cs:226) always emits
            // four child expressions in order: Outer, Name, Flags, Class (no
            // Template before stream version 300; HP1 is v76). Evaluate all four
            // (Outer/Name are usually `none`, Flags 0) and construct a UObject of
            // the resolved class via NewObject. This is the window-tree
            // instantiation point: UWindowWindow.CreateWindow does `new (none)
            // WndClass` and WindowConsole.CreateRootWindow does
            // `new (none) Class<UWindowRootWindow>(DynamicLoadObject(...))`.
            Register<Tok.NewToken>((interp, frame, tok) => {
                // `new Class'X'` without parentheses: the compiler emits EX_Nothing in
                // the Outer slot; execNew pre-filled Outer = Stack.Object (Core
                // 0x10141923), and EX_Nothing (0x10132aa0, bare ret) leaves it alone.
                int oc = frame.Cursor;
                while (oc < frame.Tokens.Count && frame.Tokens[oc] is Tok.DebugInfoToken) oc++;
                bool outerOmitted = oc < frame.Tokens.Count && frame.Tokens[oc] is Tok.NothingToken;
                var outerR = interp.EvalNext(frame);   // Outer
                if (outerOmitted) outerR = new UEEvalResult { Value = frame.Object, Result = UEStmtResult.Next };
                var nameR  = interp.EvalNext(frame);   // Name
                interp.EvalNext(frame);                // Flags — unused at runtime
                var classR = interp.EvalNext(frame);   // Class<...>
                var cls = classR.Value as UClass;
                if (cls == null) {
                    string cn = classR.Value is UEName un ? un.Value : classR.Value?.ToString();
                    if (!string.IsNullOrEmpty(cn)) cls = UEClassRegistry.Shared.Find(cn);
                }
                string nm = nameR.Value is UEName nn ? nn.Value : nameR.Value as string;
                var obj = interp.NewObject(cls, outerR.Value as UEObject, nm);
                if (obj == null)
                    UETrace.Warn(
                        $"[VM] new: unresolved class ({classR.Value?.GetType().Name ?? "null"}) in " +
                        $"{frame.Object?.Class?.Name}.{frame.Func?.Name}");
                return new UEEvalResult { Value = obj, Result = UEStmtResult.Next };
            });

            // execContext (Core.dll.c:47177): evaluate the object part; object
            // NULL → Logf, GPropAddr = 0, Code += wSkip + 3, result zeroed
            // (the inner expression does not run). Otherwise the inner expression
            // runs with this = object; then the context is restored.
            //
            // UELib ContextTokens.cs:12-74 — ContextToken parses inline
            // SkipSize (ushort) and PropertyType byte for newer versions, but
            // emits exactly two child tokens (ObjectExpr, then ContextExpr).
            // The inline bytes are pure stream state — no token impact.
            Register<Tok.ContextToken>((interp, frame, tok) => {
                var objR = interp.EvalNext(frame);
                var savedContext = frame.Context;
                var asObj = objR.Value as UEObject;
                if (asObj != null) {
                    frame.Context = asObj;
                } else if (objR.Value == null) {
                    // ONLY null. Shipped execContext (Core.dll.c:47194) checks the
                    // object part's result with a single comparison against zero.
                    // A corpse (bDeleteMe) is a valid pointer: member access on it
                    // RUNS. (Auto-nulling of references to destroyed actors applies
                    // to property SLOTS at cleanup, not to a live value on the call
                    // stack; treating Destroyed as None broke
                    // spellTrigger.IsRelevant(projectile corpse) at position 293 — the
                    // Lev_Tut1b flip plate did not open the door.) Null, however, MUST
                    // skip the whole expression (warn, Code += wSkip, null result) —
                    // otherwise a native inside dispatches with fallback Self
                    // (None-DrawFX `DrawFX.Destroy()` destroyed the CALLER).
                    string anKey = $"{frame.Object?.Class?.Name}.{frame.Func?.Name} at pos {tok.Position}";
                    if (_loggedAccessedNone.Add(anKey))
                        UETrace.Info($"[VM] Accessed None in {anKey} (repeats suppressed)");
                    var ct = (Tok.ContextToken)tok;
                    if (frame.Cursor < frame.Tokens.Count)
                    {
                        int targetByte = frame.Tokens[frame.Cursor].Position + ct.SkipSize;
                        while (frame.Cursor < frame.Tokens.Count
                            && frame.Tokens[frame.Cursor].Position < targetByte)
                            frame.Cursor++;
                    }
                    return new UEEvalResult { Value = null, Result = UEStmtResult.Next };
                } else if (objR.Value is UClass ctxCls) {
                    // A CLASS as the member-access context — UE1 EX_Context on
                    // the class UObject (Pawn.TakeDamage `Class.Name` chains);
                    // members resolve against the class-defaults bag + Name.
                    frame.Context = interp.ClassContextObject(ctxCls);
                } else if (TextureProxyFor(objR.Value) is UEObject texProxy) {
                    // A render-resource ref (Texture / UEResourceRef) used as a
                    // member-access context — e.g. HealthBar.USize in the HUD.
                    // Substitute a proxy UEObject exposing USize/VSize from the
                    // resolved Texture2D so the HUD's tile-size math reads real dims.
                    frame.Context = texProxy;
                } else {
                    UETrace.Warn(
                        $"[VM] ContextToken got non-UEObject value " +
                        $"({objR.Value.GetType().Name}) in " +
                        $"{frame.Object?.Class?.Name}.{frame.Func?.Name}");
                    frame.Context = null;
                }
                UEEvalResult rhsR;
                try { rhsR = interp.EvalNext(frame); }
                finally { frame.Context = savedContext; }
                return new UEEvalResult { Value = rhsR.Value, Result = UEStmtResult.Next };
            });

            // execSwitch (Core.dll.c:44827): size byte (0 — string), the expression
            // value into a buffer; the case walk is ProcessSwitch (Run catches
            // Result=Switch and calls it).
            //
            // UELib JumpTokens.cs:472-543 — SwitchToken parses an inline
            // PropertyType byte (HP1 v220) then a single child expression
            // (the switch value).
            Register<Tok.SwitchToken>((interp, frame, tok) => {
                var cond = interp.EvalNext(frame);
                return new UEEvalResult {
                    Value = cond.Value,
                    Result = UEStmtResult.Switch,
                };
            });

            // execCase (Core.dll.c:25983): on fall-through (previous case body
            // without break/return) the NextOffset word is read, and the case
            // expression (unless default, 0xFFFF) is evaluated into a buffer and
            // discarded. Here Result=Next: Run executes the case expression as the
            // next statement — same outcome. Normally ProcessSwitch consumes CaseToken.
            Register<Tok.CaseToken>((interp, frame, tok) =>
                new UEEvalResult { Result = UEStmtResult.Next });

            // EX_Skip has no separate exec body among Core.dll exports: its word
            // is read by && / || themselves (execAndAnd_BoolBool / execOrOr_BoolBool,
            // Core.dll.c:67281/34421). Here — a transparent pass to the RHS when
            // there is no short-circuit: `Result = Eval(expr->Value)`. UELib SkipToken
            // (JumpTokens.cs:707-723) reads the inline Size byte (used by
            // optional-arg default-skip routing in the parser) then a child
            // expression. At eval time we just forward through.
            Register<Tok.SkipToken>((interp, frame, tok) => interp.EvalNext(frame));

            // execStop (Core.dll.c:39733): Stack.Code = NULL; the
            // AActor::ProcessState loop (Engine.dll.c:45801) halts on Code == NULL.
            // Here Result=Stop, which Run maps to LatentState=Stop — state code
            // stops without proceeding further.
            //
            // UELib JumpTokens.cs:725-733 — StopToken is leaf with no
            // children.
            Register<Tok.StopToken>((interp, frame, tok) =>
                new UEEvalResult { Result = UEStmtResult.Stop });

            // execIterator (Core.dll.c:33534) — empty body: the foreach loop lives in
            // the iterator native (AllActors, Engine.dll.c:109111 / 0x1040d780):
            // find an element, body up to 0x30/0x31, on exhaustion Code = Script +
            // wEnd + 1. The port keeps a per-frame iterator stack instead of the
            // native's C stack:
            //
            // The child native (AllActors/AllObjects/...) parks its iterator in
            // interp.PendingIterator and captures the loop-var LValue (its out
            // param). We finalize the iterator with the foreach's end offset +
            // body-top cursor, push it (frame.IteratorStack), then MoveNext: if it
            // yields an item, fall into the body; else jump past the loop.
            //
            // UELib JumpTokens.cs:577-595 — IteratorToken.Deserialize reads
            // the expression first, then CodeOffset via base.JumpToken.
            Register<Tok.IteratorToken>((interp, frame, tok) => {
                var it = (Tok.IteratorToken)tok;
                interp.PendingIterator = null;
                interp.EvalNext(frame);              // runs the iterator native
                var iter = interp.PendingIterator;
                interp.PendingIterator = null;
                if (iter == null) {
                    // Native didn't yield an iterator (unimplemented / errored —
                    // already logged loud). Treat as empty: skip the body.
                    return new UEEvalResult { Result = UEStmtResult.Jump, JumpAddress = it.CodeOffset };
                }
                iter.LoopVar = interp.PendingIteratorLoopVar;
                iter.OutRefs = interp.PendingIteratorOutRefs;
                interp.PendingIteratorOutRefs = null;
                iter.EndOffset = it.CodeOffset;
                iter.BodyTopCursor = frame.Cursor;   // first token after IteratorToken
                frame.IteratorStack.Add(iter);
                if (iter.MoveNext(interp, frame))
                    return new UEEvalResult { Result = UEStmtResult.Next };          // enter body
                // Empty on entry → jump to the IteratorPop (EndOffset) WITHOUT popping here;
                // IteratorPop does the single pop. In shipped exhaustion is Code = Script[wEnd+1],
                // a single exit from the native (via IteratorPop), like the exhaustion path of
                // IteratorNext below. Popping here too would be a
                // DOUBLE pop: in a NESTED foreach whose inner collection is empty on entry, the
                // IteratorPop would then remove the OUTER iterator, corrupting the outer loop.
                return new UEEvalResult { Result = UEStmtResult.Jump, JumpAddress = it.CodeOffset };
            });

            // 0x31 (IteratorNext) in shipped: the iterator native sets Code = StartCode
            // and finds the next element; found → body from the start, exhausted → exit
            // past the loop. Here IteratorNext ALWAYS jumps, never falls through: another
            // item → BodyTopCursor; exhausted → EndOffset (the IteratorPop past the
            // loop). Falling through on exhaustion would be wrong:
            // for the TAIL IteratorNext (right before IteratorPop) it would be
            // accidentally equivalent, but `continue` compiles to an EX_IteratorNext in the
            // MIDDLE of the body (UELib IteratorNextToken.Decompile → "continue") — so on the
            // last element it would run the REST of the body with the loop-var already reset to None.
            // Reachable: baseChar.uc:571 FindClosestPatrolPoint, Harry.uc aim-target selection.
            // UELib JumpTokens.cs:643-656.
            Register<Tok.IteratorNextToken>((interp, frame, tok) => {
                if (frame.IteratorStack.Count == 0) {
                    UETrace.Error($"[VM] IteratorNext with no active iterator in " +
                                   $"{frame.Object?.Class?.Name}.{frame.Func?.Name}");
                    return new UEEvalResult { Result = UEStmtResult.Next };
                }
                var iter = frame.IteratorStack[frame.IteratorStack.Count - 1];
                if (iter.MoveNext(interp, frame)) {
                    frame.Cursor = iter.BodyTopCursor;   // another item → loop back to body top
                    return new UEEvalResult { Result = UEStmtResult.Next };
                }
                // Exhausted → jump to the IteratorPop (EndOffset), which pops the iterator.
                return new UEEvalResult { Result = UEStmtResult.Jump, JumpAddress = iter.EndOffset };
            });

            // 0x30 (IteratorPop): the iterator native returns in shipped. End of a
            // foreach (also emitted by `break` out of one): pop the active iterator.
            // UELib JumpTokens.cs:659-672.
            Register<Tok.IteratorPopToken>((interp, frame, tok) => {
                if (frame.IteratorStack.Count > 0)
                    frame.IteratorStack.RemoveAt(frame.IteratorStack.Count - 1);
                return new UEEvalResult { Result = UEStmtResult.Next };
            });

            // execArrayElement (Core.dll.c:46481): first the index, then the array
            // (a variable, sets GPropAddr/GProperty); index outside [0, ArrayDim)
            // — Logf and clamp to the bound; GPropAddr += ElementSize·idx.
            //
            // UELib ArrayTokens.cs:12-31 emits children in order
            //     [ArrayElementToken, KeyExpr, ArrayExpr]
            // so we consume Key first, then Array, matching the stream layout.
            // Our runtime model: arrays are IList (List<object>) populated
            // either by LoadDefaults (static defaults) or by Let-assignments
            // at runtime. Index outside [0, ArrayDim) — StaticArrayIndex (log +
            // clamp to the bound, as execArrayElement).
            Register<Tok.ArrayElementToken>((interp, frame, tok) => {
                var keyR = interp.EvalNext(frame);
                int dim = StaticArrayDimAt(frame);
                var arrR = interp.EvalNext(frame);
                var list = arrR.Value as System.Collections.IList;
                int idx = StaticArrayIndex(frame, ToInt(keyR.Value), dim, list);
                object val = null;
                if (list != null && idx >= 0 && idx < list.Count)
                    val = list[idx];
                return new UEEvalResult { Value = val, Result = UEStmtResult.Next };
            });

            // DynamicArrayElementToken : ArrayElementToken (UELib ArrayTokens.cs:34 —
            // no override, so the same [token, Key, Array] layout). Token dispatch is
            // by EXACT type, so the static handler above does NOT cover it (that's why
            // `DrawnPoints[N]` reads logged "No handler for token DynamicArrayElementToken").
            // A read grows the array THE SAME way as a write: execDynArrayElement (Core
            // 0x10132522..0x101325ab) does not distinguish read and write — index >= Num
            // grows the TArray to idx+1 zero-filled, a negative one is logged and
            // becomes 0 (no growth). Hence the array is taken as an lvalue.
            Register<Tok.DynamicArrayElementToken>((interp, frame, tok) => {
                var keyR = interp.EvalNext(frame);
                var list = interp.DynArrayForElement(frame, ToInt(keyR.Value), out int idx);
                object val = null;
                if (list != null && idx < list.Count)
                    val = list[idx];
                return new UEEvalResult { Value = val, Result = UEStmtResult.Next };
            });

            // execDynArrayCount (Core.dll.c:47297): Num = *(int*)(GPropAddr + 4);
            // without an address Result is not written (stays the caller's zero) — the
            // port returns 0. Stream layout [token, ArrayExpr]. This body has no
            // lvalue form (`A.Length = n`); the port keeps it loud (EvalLValue
            // default), no carriers.
            Register<Tok.DynamicArrayLengthToken>((interp, frame, tok) => {
                var arrR = interp.EvalNext(frame);
                int len = (arrR.Value is System.Collections.IList list) ? list.Count : 0;
                return new UEEvalResult { Value = len, Result = UEStmtResult.Next };
            });

            // execStructMember (Core.dll.c:4845): struct into a zeroed buffer,
            // GPropAddr += member Offset, Result — the member value.
            //
            // UELib ContextTokens.cs:102-158 — Property field is parsed first,
            // then DeserializeNext consumes the parent struct expression.
            // Flat stream layout:
            //     [StructMemberToken{Property}, ParentExpr, ...]
            //
            // Runtime model: UE1 structs are represented as UEObject bags
            // (Props dict) or as IDictionary<string,object>. Look up by field
            // name. If parent is null/wrong type, return null (loud at next
            // unmodelled token if it matters).
            Register<Tok.StructMemberToken>((interp, frame, tok) => {
                var sm = (Tok.StructMemberToken)tok;
                var pr = PropRefAt(frame, tok, sm.Property);
                string fieldName = pr.Name;
                var parent = interp.EvalNext(frame);
                if (string.IsNullOrEmpty(fieldName)) {
                    UETrace.Error(
                        $"[VM] StructMember with no field name in " +
                        $"{frame.Object?.Class?.Name}.{frame.Func?.Name} at pos {tok.Position}");
                    return new UEEvalResult { Result = UEStmtResult.Next };
                }
                object val = null;
                switch (parent.Value)
                {
                    case UEObject ueo:
                        val = ueo.Props.Get(pr.NameKeyId);
                        break;
                    case System.Collections.Generic.IDictionary<string, object> d:
                        d.TryGetValue(fieldName, out val);
                        break;
                    case Vector3 vec:
                        // UE1 Vector intrinsic component access (X/Y/Z).
                        if (string.Equals(fieldName, "X", StringComparison.OrdinalIgnoreCase)) val = vec.x;
                        else if (string.Equals(fieldName, "Y", StringComparison.OrdinalIgnoreCase)) val = vec.y;
                        else if (string.Equals(fieldName, "Z", StringComparison.OrdinalIgnoreCase)) val = vec.z;
                        break;
                    case UERotator rot:
                        // UE1 Rotator intrinsic component access.
                        if (string.Equals(fieldName, "Pitch", StringComparison.OrdinalIgnoreCase)) val = rot.Pitch;
                        else if (string.Equals(fieldName, "Yaw", StringComparison.OrdinalIgnoreCase)) val = rot.Yaw;
                        else if (string.Equals(fieldName, "Roll", StringComparison.OrdinalIgnoreCase)) val = rot.Roll;
                        break;
                    case UEPlane pl:
                        // Core.Object.Plane — X/Y/Z/W (otherwise a plane member reads
                        // as null → 0 and all plane operators of Object.u return a zero plane).
                        if (string.Equals(fieldName, "X", StringComparison.OrdinalIgnoreCase)) val = pl.X;
                        else if (string.Equals(fieldName, "Y", StringComparison.OrdinalIgnoreCase)) val = pl.Y;
                        else if (string.Equals(fieldName, "Z", StringComparison.OrdinalIgnoreCase)) val = pl.Z;
                        else if (string.Equals(fieldName, "W", StringComparison.OrdinalIgnoreCase)) val = pl.W;
                        break;
                }
                return new UEEvalResult { Value = val, Result = UEStmtResult.Next };
            });

            // execLet (Core.dll.c:59462): the LHS sets GPropAddr, then the RHS is
            // written straight to that address; no address → Logf and write to
            // scratch (the RHS is still evaluated).
            //
            // execLetBool (Core.dll.c:4101): LHS → GPropAddr/GProperty, RHS, then
            // bit mask (Or / AndNot). The port stores bool as bool — same outcome,
            // hence one handler (LetBool is a typed alias UELib promotes to
            // a subclass — LetTokens.cs:28). We register both with the same
            // handler.
            //
            // UELib LetTokens.cs:10-25 — LetToken consumes two children
            // (LHS, RHS) in order.
            UETokenHandler letHandler = (interp, frame, tok) => {
                var lref = interp.EvalLValue(frame);
                var rhs = interp.EvalNext(frame);
                // UE1 value-struct semantics: assigning a struct copies it, so a saved
                // struct local isn't aliased to the source (e.g. the HUD's
                // OldColor = DrawColor; DrawColor.R = 0; DrawColor = OldColor restore).
                // A whole array<> is a value too (UArrayProperty::CopySingleValue).
                var val = UEValue.CopyValue(rhs.Value);
                interp.WriteLValue(frame, lref, val);
                return new UEEvalResult { Value = val, Result = UEStmtResult.Next };
            };
            Register<Tok.LetToken>(letHandler);
            Register<Tok.LetBoolToken>(letHandler);

            // EX_StructCmpEq / EX_StructCmpNe (UELib ComparisonToken: the struct-type UObject
            // is consumed at DESERIALIZE, leaving two operand sub-exprs at eval time). UE1
            // compares two struct instances member-by-member. Were unregistered → per-frame
            // "[VM] No handler for token StructCmpEqToken" spam in FEBook.Paint (the menu book)
            // and any `someStruct == otherStruct` guard. (#audit gap 12)
            Register<Tok.StructCmpEqToken>((interp, frame, tok) => {
                var a = interp.EvalNext(frame); var b = interp.EvalNext(frame);
                return new UEEvalResult { Value = StructEquals(a.Value, b.Value), Result = UEStmtResult.Next };
            });
            Register<Tok.StructCmpNeToken>((interp, frame, tok) => {
                var a = interp.EvalNext(frame); var b = interp.EvalNext(frame);
                return new UEEvalResult { Value = !StructEquals(a.Value, b.Value), Result = UEStmtResult.Next };
            });

            // execVirtualFunction (Core.dll.c:53121): FindFunctionChecked(this =
            // context object, name, Global=0) → CallFunction on it. Lookup
            // "state first, then class" — FindEventFunction (below,
            // UObject::FindObjectField).
            //
            // UELib FunctionTokens.cs:237-258 — VirtualFunctionToken; reads
            // FunctionName (UName) then DeserializeCall consumes args.
            Register<Tok.VirtualFunctionToken>((interp, frame, tok) => {
                var vt = (Tok.VirtualFunctionToken)tok;
                string name = vt.FunctionName.ToString();
                // Virtual calls resolve on the current Context's class
                // (for `A.B()` Context is A); the invoked frame's Self
                // becomes that Context — in shipped execContext sets this = A,
                // and execVirtualFunction looks the function up on A. We resolve the target function BEFORE
                // collecting args so CollectArgsWithOuts knows the
                // PropertyFlags layout.
                var target = frame.Context;
                // Bridge-method intercept (checked BEFORE bytecode): only for
                // functions intentionally owned by the frontend bridge (for
                // example PlayerHarry ClientMessage/Cut* cues). HUD/dialog drawing
                // is VM baseHud/HPHud bytecode, not a C# overlay.
                // Args are still collected from the stream so it doesn't desync.
                if (target?.BridgeMethods != null
                 && target.BridgeMethods.TryGetValue(name, out var bridge)) {
                    var (bargs, _) = CollectArgsWithOuts(interp, frame, null, name);
                    return new UEEvalResult { Value = bridge(bargs), Result = UEStmtResult.Next };
                }
                // UEPlane is a value struct with members, so PlaneV/PlaneC/PlaneF
                // run their own bytecode, as in shipped (no MakePlane hook).
                var fn = interp.FindEventFunction(target, name);
                var (args, outRefs) = CollectArgsWithOuts(interp, frame, fn, name);
                // Diagnostic only, the call runs as usual: ClientMessage text in the log with the level time and the
                // receiver's location. CutScene.DebugPrint lands here ("CutScene start:", "CutTrigger:Event name="), so
                // a playthrough log shows when each cutscene started and where Harry was; the shipped console shows none.
                if (UETrace.Playthrough is not null && target != null && args is { Length: > 0 } && string.Equals(name, "ClientMessage", StringComparison.OrdinalIgnoreCase))
                {
                    var at = AsVector3(target.GetProp("Location"));
                    UETrace.Playthrough($"[ClientMessage] t={interp.PeekLevelInfo()?.GetProp("TimeSeconds")} '{args[0]}' at ({at.x:F0}, {at.y:F0}, {at.z:F0})");
                }
                if (target == null) {
                    // Accessed None: a virtual call through a None object. UE1
                    // treats this as a no-op that returns the default value and
                    // continues (the enclosing ContextExpression already logged
                    // "Accessed None"). Common + benign in HP1 — e.g.
                    // theNarrator.DeliverDialog when no baseNarrator actor exists,
                    // or Level.Game.SetGameSpeed (GameInfo not modelled). Quiet
                    // Info log (matches the ContextToken None path), NOT a loud
                    // error. Args were already consumed above so the stream stays
                    // in sync. (Distinct from the next branch: target present but
                    // the function genuinely missing → still loud.)
                    // Loudness exception: the level-travel chain (CutScene.uc:745
                    // CHANGELEVEL → Console.ChangeLevel → Level.ServerTravel) dies on
                    // this path because the console bridge has no Viewport and
                    // ServerTravel is not implemented — the END-OF-LEVEL transition
                    // must not fail as a quiet Info line.
                    if (string.Equals(name, "ChangeLevel", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(name, "ServerTravel", StringComparison.OrdinalIgnoreCase))
                        UETrace.Error(
                            $"[VM] LEVEL TRAVEL NOT PORTED: '{name}' hit Accessed-None in " +
                            $"{frame.Object?.Class?.Name}.{frame.Func?.Name} — Tut1→Tut1b transition blocked");
                    else
                        UETrace.Info(
                            $"[VM] Accessed None calling '{name}' in " +
                            $"{frame.Object?.Class?.Name}.{frame.Func?.Name}");
                    return new UEEvalResult { Result = UEStmtResult.Next };
                }
                if (fn == null) {
                    // Fallback: a NATIVE function not present as a UFunction in the
                    // loaded class hierarchy (e.g. Object.Localize, NativeToken 0 —
                    // resolved by name). If a name-native exists, run it with the
                    // collected args. (Native side effects / out-params handled by
                    // the native + the writeback below.)
                    var ncb = interp.Natives.GetByName(name);
                    if (ncb != null) {
                        var na = args;
                        NativeCoverageHook?.Invoke(name);
                        var nret = ncb(interp, frame, na);
                        for (int i = 0; i < outRefs.Count; i++)
                            if (outRefs[i].HasValue && i < na.Length)
                                interp.WriteLValue(frame, outRefs[i].Value, na[i]);
                        interp.Pool.ReleaseOutRefs(outRefs, NoOutRefs);
                        interp.Pool.ReleaseArgs(na);
                        return new UEEvalResult { Value = nret, Result = UEStmtResult.Next };
                    }
                    string missing =
                        $"[VM] Virtual function '{name}' not found on " +
                        $"{target.Class?.Name} in " +
                        $"{frame.Object?.Class?.Name}.{frame.Func?.Name}";
                    if (interp.ThrowOnMissingScript) throw new NotSupportedException(missing);
                    UETrace.Error(missing);
                    return new UEEvalResult { Result = UEStmtResult.Next };
                }
                var argsArr = args;
                var ret = interp.Invoke(target, fn, argsArr);
                // UObject::CallFunction (Core.dll.c:7715): out-params are copied back
                // starting from the LAST — observable under aliasing `F(x, x)`.
                for (int i = outRefs.Count - 1; i >= 0; i--) {
                    if (outRefs[i].HasValue && i < argsArr.Length)
                        interp.WriteLValue(frame, outRefs[i].Value, argsArr[i]);
                }
                interp.Pool.ReleaseOutRefs(outRefs, NoOutRefs);
                interp.Pool.ReleaseArgs(argsArr);
                return new UEEvalResult { Value = ret, Result = UEStmtResult.Next };
            });

            // execGlobalFunction (Core.dll.c:47853): FindFunctionChecked(name,
            // Global=1) — only the class VfHash (`global.Foo(...)`). Resolves Foo on the Context's class
            // hierarchy SKIPPING state overrides, then calls it. Used by state
            // Tick overrides that chain to the base tick, e.g.
            // baseChar.CutMovingTo.Tick begins with `global.Tick(fDeltaTime)`.
            Register<Tok.GlobalFunctionToken>((interp, frame, tok) => {
                var gt = (Tok.GlobalFunctionToken)tok;
                string name = gt.FunctionName.ToString();
                var target = frame.Context;
                var fn = interp.FindGlobalFunction(target, name);
                var (args, outRefs) = CollectArgsWithOuts(interp, frame, fn, name);
                if (target == null) {
                    UETrace.Info(
                        $"[VM] Accessed None calling global.'{name}' in " +
                        $"{frame.Object?.Class?.Name}.{frame.Func?.Name}");
                    return new UEEvalResult { Result = UEStmtResult.Next };
                }
                if (fn == null) {
                    string missing =
                        $"[VM] Global function '{name}' not found on " +
                        $"{target.Class?.Name} in " +
                        $"{frame.Object?.Class?.Name}.{frame.Func?.Name}";
                    if (interp.ThrowOnMissingScript) throw new NotSupportedException(missing);
                    UETrace.Error(missing);
                    return new UEEvalResult { Result = UEStmtResult.Next };
                }
                var argsArr = args;
                var ret = interp.Invoke(target, fn, argsArr);
                // UObject::CallFunction (Core.dll.c:7715): out-params are copied back
                // starting from the LAST — observable under aliasing `F(x, x)`.
                for (int i = outRefs.Count - 1; i >= 0; i--) {
                    if (outRefs[i].HasValue && i < argsArr.Length)
                        interp.WriteLValue(frame, outRefs[i].Value, argsArr[i]);
                }
                interp.Pool.ReleaseOutRefs(outRefs, NoOutRefs);
                interp.Pool.ReleaseArgs(argsArr);
                return new UEEvalResult { Value = ret, Result = UEStmtResult.Next };
            });

            // FFrame::Step (Core.dll.c:39376): GNatives[*Code++] — a native call's
            // opcode is its index (extended ones — FirstNative + ExtendedNative).
            // UELib resolves the NativeTableItem ({Name, Type, ByteToken}) for us
            // as tok.NativeItem. The by-name fallback (GetByName) is port-only;
            // shipped uses the index only.
            //
            // execAndAnd_BoolBool / execOrOr_BoolBool (Core.dll.c:67281/34421) —
            // && (idx 130) and || (idx 132): A, word wSkip; result decided — Code +=
            // wSkip (including EndFunctionParms). The RHS is wrapped in an EX_Skip and evaluated lazily, so
            // guards like `X==None || X.foo()` / `X!=None && X.foo()` never deref None.
            // EvalShortCircuit honours that (evaluating both args eagerly would fire the RHS
            // even when the LHS decided it → spurious Accessed-None).
            Register<Tok.NativeFunctionToken>((interp, frame, tok) => {
                var nt = (Tok.NativeFunctionToken)tok;
                int nativeIdx = nt.NativeItem?.ByteToken ?? 0;
                if (nativeIdx == 130 || nativeIdx == 132)
                    return interp.EvalShortCircuit(frame, nativeIdx);
                string nativeName = nt.NativeItem?.Name;
                // Look up the matching UFunction declaration so we can read
                // PropertyFlags.OutParm for out-param dispatch. Natives
                // declared in HP1's .u scripts are recorded by
                // UEScriptRuntime.RegisterPackage. If the native isn't in
                // any loaded script (very rare; would be an engine-only
                // intrinsic), we fall through to a no-out signature.
                var nativeFn = UEClassRegistry.Shared.FindByNativeIndex(nativeIdx);
                var (args, outRefs) = CollectArgsWithOuts(interp, frame, nativeFn, nativeName ?? "?");
                var cb = (nativeIdx > 0 ? interp.Natives.GetByIndex(nativeIdx) : null)
                      ?? interp.Natives.GetByName(nativeName);
                if (cb == null) {
                    interp.Natives.OnUnknownNative(nativeIdx, nativeName,
                        $"{frame.Object?.Class?.Name}.{frame.Func?.Name}");
                    return new UEEvalResult { Result = UEStmtResult.Next };
                }
                var argsArr = args;
                if (NativeCoverageHook != null)
                {
                    // Intrinsics not declared in loaded .uc surface as "__NFUN_<idx>__"
                    // stubs — prefer the registry's human name for the coverage log.
                    string covName = interp.Natives.NameOfIndex(nativeIdx) ?? nativeName ?? ("idx" + nativeIdx);
                    NativeCoverageHook(covName);
                }
                var ret = cb(interp, frame, argsArr);
                // Iterator natives (AllActors/...) park an iterator in
                // PendingIterator and need the loop-var LValue — that's the
                // single `out` param. Hand it over; the enclosing IteratorToken
                // pushes the iterator and MoveNext writes each item. We skip the
                // normal writeback for that param (the iterator owns it).
                if (interp.PendingIterator != null) {
                    for (int i = 0; i < outRefs.Count; i++)
                        if (outRefs[i].HasValue) { interp.PendingIteratorLoopVar = outRefs[i].Value; break; }
                    interp.PendingIteratorOutRefs = outRefs;   // all out-params (TraceActors needs 3)
                    return new UEEvalResult { Value = ret, Result = UEStmtResult.Next };
                }
                // Native callbacks mutate argsArr[i] for out params. In shipped the native
                // writes out through the GPropAddr obtained in P_GET (iNative branch of
                // CallFunction, Core.dll.c:7741); the port writes after the native in
                // forward order — same result absent aliasing (no carriers).
                // We then write the updated values through to the caller-side
                // LValues we captured in CollectArgsWithOuts.
                for (int i = 0; i < outRefs.Count; i++) {
                    if (outRefs[i].HasValue && i < argsArr.Length)
                        interp.WriteLValue(frame, outRefs[i].Value, argsArr[i]);
                }
                // Not on the iterator branch above: the iterator keeps the out-ref list (UEIterator.OutRefs).
                interp.Pool.ReleaseOutRefs(outRefs, NoOutRefs);
                interp.Pool.ReleaseArgs(argsArr);
                return new UEEvalResult { Value = ret, Result = UEStmtResult.Next };
            });
        }
    }
}
