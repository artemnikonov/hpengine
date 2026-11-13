// Line-check queries against actors and world: TraceTexture, MultiLineCheck, ActorLineCheck,
// SingleLineCheck, FindBase, AdjustSpot/FindSpot. Fields live in UELevelRuntime.cs.
using System;
using System.Collections.Generic;
using System.IO;
using UELib.Core;
using Vector3 = HP1.UEScript.UEVector;
using X87Ext = HP1.Core.X87Ext;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        // AActor::execTraceTexture (Engine.dll.c:55849) — native 285 IN FULL.
        //
        // DECLARATION FROM THE PACKAGE (`--dumpfunc Actor TraceTexture`):
        //   native(285) final function Texture TraceTexture(
        //       Vector TraceEnd, Vector TraceStart, out int Flags,
        //       optional bool bTraceDecals);
        //
        // BODY RECONSTRUCTED FROM THE DISASM (0x1037aba0), not the pseudocode: Ghidra
        // merges the loop counter and the decal flag into one `iVar17` here, which makes
        // the branching read wrong.
        //   1. Hit = FCheckResult(Time=1, Item=-1);
        //      XLevel->SingleLineCheck(Hit, this, End, Start, TRACE_Level=4,
        //      Extent=(0,0,0))                                   (0x1037acf4)
        //   2. *Flags = 0 BEFORE parsing the hit                  (0x1037ad0e)
        //   3. Surf = Hit.GetSurf(): Item==-1 or Primitive not UModel -> None
        //   4. Tex = Surf->Texture; *Flags = Surf->PolyFlags | Tex->PolyFlags
        //      (0x1037ad2c-0x1037ad3b; `Tex->PolyFlags` is the word at +0x80)
        //   5. bTraceDecals==0 -> return Tex                      (0x1037ad3e)
        //   6. else walk Surf->Decals (4 edges, PlaneDot of one sign) and
        //      replace Tex with the decal actor's texture          (0x1037af06)
        //
        // PORT BOUNDARY (loud). Step 6 is NOT ported, and bTraceDecals=true is a STOP, not
        // "return the surface texture". Core does keep the Surf->Decals list
        // (UELevelRuntime.DecalStore.cs, FDecal records with vertices relative to pBase and
        // Actor) (D-40); the branch is not ported because it is unreachable. Body per disasm
        // 0x1037ad4a-0x1037af06 — for porting if a caller appears:
        //   Surf->Decals.Num == 0 -> Tex; N = Vectors[vNormal];
        //   P = Hit.Location - Points[pBase] (float);
        //   for each record in order: prev = 0f; for k = 0..3:
        //     E = N ^ (V[k] - V[k-1 cyclic]) (float components),
        //     d = PlaneDot(FPlane(E, V[k-1] | E), P) (st0, PC64);
        //     d * prev < 0 -> record skipped; else prev = (float)d;
        //   all 4 edges passed and Record.Actor.Texture (+0x15c) != None ->
        //   Tex = it (the LAST matching one wins). Flags unchanged.
        //   Porting also needs a texture object for the decal resource
        //   (TextureObject is built from SurfMaterialRef).
        // Unreachable in the campaign by MEASUREMENT: all 4 call sites (Harry.PlayFootStep,
        // Harry.PlayLandedSound, baseChar.PlayFootStep, InvisibleHarry.PlayFootStep) pass
        // THREE arguments; nobody passes the optional fourth. UELib decompilation of ALL 31
        // System/*.u packages (6124 functions and states, 0 failures): no other native 285 calls.
        //
        // Also measured: none of these four calls READS the out parameter Flags — the local is
        // declared, passed and never used. So Flags correctness cannot be proven by play; a
        // fixture proves it.
        //
        // OWNER IS CORE ENTIRELY: every frontend calls this body, and texture data is read
        // by the single UETextureFactsReader.
        public object TraceTextureNative(UEInterpreter interp, UEFrame frame, object[] args)
        {
            UEObject self = frame?.Context ?? frame?.Object;
            Vector3 end = args is { Length: > 0 }
                ? UEInterpreter.AsVector3(args[0]) : Vector3.zero;
            Vector3 start = args is { Length: > 1 } && args[1] is not null
                ? UEInterpreter.AsVector3(args[1])
                : UEInterpreter.AsVector3(self?.GetProp(PK.Location));

            if (args is { Length: > 3 } && UEInterpreter.ToBool(args[3]))
                throw new NotSupportedException(
                    "STOP: TraceTexture(bTraceDecals=true) — shipped walks "
                    + "Surf->Decals (Engine.dll 0x1037ad4a-0x1037af06), and this branch "
                    + "is not ported (D-40). Across the campaign none of the 4 calls "
                    + "asks for it; if it was asked for, this is a new route, and "
                    + "answering with the surface texture would be a silent substitution");

            if (Bsp is null)
                throw new InvalidOperationException("TraceTexture before the world BSP is loaded");

            // Shipped zeroes the out parameter BEFORE parsing the hit and leaves it zero on a
            // miss — it does not "leave it untouched".
            if (args is { Length: > 2 }) args[2] = 0;

            BspHit hit = Bsp.TraceWorldLine(start, end);
            if (!hit.Blocked) return null;          // no surface -> None -> stone

            // Hit.GetSurf() = FCheckResult::BspNode(0) (0x103ff1b0 -> 0x103febd0): from the
            // trace node along the coplanar iPlane chain to the polygon that CONTAINS
            // Hit.Location (normal >= 0.99, edge test); if none is found — the last co-directed
            // one. The trace node is only the first in the plane: taking its surface meant
            // hearing the floor under a carpet (live original comparison: 108 of 518 campaign
            // steps). Hit.Location is the LineCheck tail's (BspHit.Location).
            Vector3 hitLoc = hit.Location;
            int surfNode = Bsp.BspNode(hit.Node, hitLoc, hit.Normal, 0f, null);
            if (surfNode < 0) return null;
            SurfMaterialRef material = Bsp.SurfaceMaterial(surfNode);
            UETextureFacts facts = material is null ? null : TextureFacts.Read(material);
            if (args is { Length: > 2 })
                args[2] = Bsp.SurfFlags(surfNode) | (facts?.PolyFlags ?? 0);
            // A surface without a texture: shipped returns Surf->Texture, i.e. None (not an
            // object with FootstepSound=0, which would mean "stone").
            if (material is null) return null;
            return TextureObject(interp, material, facts);
        }

        // How many texture objects the native has issued. Public for the same reason as
        // RootMotionStateCount: "teardown removed this too" is an assertion, not an intent.
        public int TraceTextureObjectCount => _traceTextureObjects.Count;

        private UEObject TextureObject(UEInterpreter interp, SurfMaterialRef material,
            UETextureFacts facts)
        {
            // The key is the declared identity of the reference, not its printed path:
            // `Path` loses both the class and — for a texture embedded in the map — the export
            // number ("export:Leaf"), so it would hand ONE object to two different textures
            // with the same leaf name.
            string key = material.IdentityKey;
            if (_traceTextureObjects.TryGetValue(key, out UEObject cached)) return cached;
            var tex = new UEObject
            {
                Interp = interp,
                Class = HP1.Core.UEClassRegistry.Shared.Find("Texture"),
            };
            tex.Props["Name"] = new UEName(material.Name);
            tex.Props["FootstepSound"] = facts.FootstepSound;
            _traceTextureObjects[key] = tex;
            return tex;
        }

        // ── MultiLineCheck x87 chains. The body sets no control word: PC64 in game logic, PC24 (every operation
        // rounded to float) inside the render window. Checked against the real bodies under unicorn. ──

        // World-hit shortening 0x103ac70a..0x103ac805. PC64: d = Hit.Location − Start in the register, (dx²+dy²)+dz²,
        // fstp qword, appSqrt — dist stays in the register (the fst qword is a spill); ratio = (dist + 5f)·Time /
        // (dist + 0.0001f) in the register; `fld 1.0; fcomp st(1); test ah,0x41; je` stores the ratio only when 1 is
        // above it (NaN and >= 1 give 1.0f). ActorEnd: ex·f and ey·f stored, ez = End.Z − Start.Z and ez·f kept in the
        // register, + Start, fstp dword. Very close: `fld Time; fcomp 0.01f; test ah,1` (C0: below or NaN), then
        // `fcomp 30f` on the register dist, C0 again.
        internal static float MultiLineWorldFactor(Vector3 start, Vector3 end, Vector3 worldLoc, float time, bool pc24,
            out Vector3 actorEnd, out bool veryClose)
        {
            if (pc24)
            {
                Vector3 w24 = worldLoc - start;
                float d24 = UEVector.Size24(w24.x, w24.y, w24.z);
                float r24 = ((d24 + 5f) * time) / (d24 + 0.0001f);
                float f24 = 1f > r24 ? r24 : 1f;
                actorEnd = start + (end - start) * f24;
                veryClose = !(time >= 0.01f) && !(d24 >= 30f);
                return f24;
            }
            static HP1.Core.X87Ext X(float v) => HP1.Core.X87Ext.From(v);
            static HP1.Core.X87Ext R(HP1.Core.X87Ext v) => HP1.Core.X87Ext.Round64(v);
            HP1.Core.X87Ext dist = HP1.Core.X87Ext.Sqrt64(HP1.Core.X87Ext.From(DistSq87(worldLoc, start).ToDouble()));
            HP1.Core.X87Ext ratio = HP1.Core.X87Ext.Div64(R(R(dist + X(5f)) * X(time)), R(dist + X(0.0001f)));
            float factor = (HP1.Core.X87Ext.Fcom(X(1f), ratio) & 0x41) == 0 ? ratio.ToFloat() : 1f;
            HP1.Core.X87Ext ez = R(X(end.z) - X(start.z));
            actorEnd = new Vector3((end.x - start.x) * factor + start.x, (end.y - start.y) * factor + start.y,
                R(R(ez * X(factor)) + X(start.z)).ToFloat());
            veryClose = (HP1.Core.X87Ext.Fcom(X(time), X(0.01f)) & HP1.Core.X87Ext.C0) != 0
                && (HP1.Core.X87Ext.Fcom(dist, X(30f)) & HP1.Core.X87Ext.C0) != 0;
            return factor;
        }

        // Coincident-face distance test 0x103ac99a..0x103ac9d8: d = hit − world in the register, (dx²+dy²)+dz²,
        // `fcomp 4.0; test ah,1; je skip` — the pull-back runs on C0: below 4 or unordered (NaN).
        internal static bool MultiLineNearWorldHit(Vector3 loc, Vector3 worldLoc, bool pc24)
            => pc24 ? !((loc - worldLoc).sqrMagnitude >= 4f)
                : (HP1.Core.X87Ext.Fcom(DistSq87(loc, worldLoc), HP1.Core.X87Ext.From(4f)) & HP1.Core.X87Ext.C0) != 0;

        // Pull-back 0x103ac9de..0x103acaee. PC64: span² = dxr·dxf + dy² + dz² — dx = ActorEnd.X − Start.X stays in the
        // register and multiplies its own float copy (fst dword), dy and dz are stored; appSqrt, span stored as float;
        // inv = 1.0f / span in the register; dir = f32(inv · d) per component (the X product in the register, 2·dir
        // exact), Location = world − 2·dir; Time = appSqrt((vz² + vy²) + vx²) / span (v = Location − Start, stored
        // floats), fstp dword.
        internal static (Vector3 loc, float time) MultiLinePullBack(Vector3 start, Vector3 actorEnd, Vector3 worldLoc, bool pc24)
        {
            if (pc24)
            {
                Vector3 d = actorEnd - start;
                float s24 = UEVector.Size24(d.x, d.y, d.z);
                float i24 = 1f / s24;
                Vector3 l24 = worldLoc - new Vector3(i24 * d.x, i24 * d.y, i24 * d.z) * 2f;
                Vector3 v24 = l24 - start;
                return (l24, UEVector.Size24(v24.z, v24.y, v24.x) / s24);
            }
            static HP1.Core.X87Ext X(float v) => HP1.Core.X87Ext.From(v);
            static HP1.Core.X87Ext R(HP1.Core.X87Ext v) => HP1.Core.X87Ext.Round64(v);
            HP1.Core.X87Ext dxr = R(X(actorEnd.x) - X(start.x));
            float dxf = dxr.ToFloat(), dyf = actorEnd.y - start.y, dzf = actorEnd.z - start.z;
            HP1.Core.X87Ext s2 = R(R(R(dxr * X(dxf)) + X(dyf) * X(dyf)) + X(dzf) * X(dzf));
            float span = HP1.Core.X87Ext.Sqrt64(HP1.Core.X87Ext.From(s2.ToDouble())).ToFloat();
            HP1.Core.X87Ext inv = HP1.Core.X87Ext.Div64(X(1f), X(span));
            var dir = new Vector3(R(inv * X(dxf)).ToFloat(), R(inv * X(dyf)).ToFloat(), R(inv * X(dzf)).ToFloat());
            Vector3 loc = worldLoc - dir * 2f;
            Vector3 v = loc - start;
            float time = HP1.Core.X87Ext.Div64(UEVector.Size64Ext(v.z, v.y, v.x), X(span)).ToFloat();
            return (loc, time);
        }

        public List<MoveHit> MultiLineCheck(Vector3 start, Vector3 end, Vector3 extent,
            bool checkActors, UEObject levelInfo)
        {
            var results = new List<MoveHit>();
            bool haveWorld = false;
            Vector3 worldNormal = Vector3.zero, worldLoc = Vector3.zero;
            // Disasm (MultiLineCheck 0x103ac620): factor is [ebp-0x18] (1.0 at 0x103ac666,
            // clamp 0x103ac779, times the actor hit Time 0x103acaf7); worldVeryClose is
            // [ebp-0x30] (0 at 0x103ac66f, 1 at 0x103ac7f8, tested 0x103ac8fa).
            float factor = 1f;            // local_1c
            bool worldVeryClose = false;  // local_34
            Vector3 actorEnd = end;

            if (levelInfo is not null && Bsp is not null)
            {
                // Shipped passes the ExtraNodeFlags byte of its call here
                // (0x103ac694-0x103ac69e: `and eax,0xff; push eax` — the last
                // argument of UModel::LineCheck, the decompile lost it). The port passes 0, and
                // that is equivalent: a census of all calls of ULevel vtable +0xc4
                // (SingleLineCheck, forwards its NodeFlags) and +0xcc (MultiLineCheck) in the
                // Engine.dll (88 sites) and Render.dll (2) disasm — every game caller passes 0:
                // a literal `push 0` or a register zeroed by `xor`. If a non-zero caller
                // appears, the flags must be threaded into the world-trace worker.
                BspHit w = extent.x == 0f && extent.y == 0f && extent.z == 0f
                    ? Bsp.TraceWorldLine(start, end) : Bsp.TraceWorldBox(start, end, extent);
                if (w.Blocked)
                {
                    worldLoc = w.Location;
                    worldNormal = w.Normal;
                    haveWorld = true;
                    results.Add(new MoveHit(w.Fraction, w.Normal, levelInfo, false, w.Node, worldLoc));
                    // 256040-256050: shorten the actor ray past the world hit; 256063-256072: a world hit
                    // THIS close leaves only movers.
                    factor = MultiLineWorldFactor(start, end, worldLoc, w.Fraction, UEWorldFrameProducer.FastMathWindow,
                        out actorEnd, out worldVeryClose);
                }
            }

            if ((checkActors || levelInfo is not null) && CollisionHash is not null)
            {
                foreach (MoveHit hit in ActorLineCheck(start, actorEnd, extent))
                {
                    if (results.Count >= 0x40) break;     // shipped uVar12 < 0x40 cap
                    bool isMover = UEInterpreter.ClassIsA(hit.Actor.Class, "Mover");
                    // 256051 / 256074: without actor collision — and when the
                    // world hit is very close — only movers get through.
                    if (!checkActors && !isMover) continue;
                    if (worldVeryClose && !isMover) continue;

                    float time = hit.Fraction;
                    Vector3 loc = hit.Location;
                    // 256054-256062: coincident-face dedup. A mover face whose normal
                    // EQUALS the world normal (fcomp per component — float equality,
                    // −0 == +0; disasm 0x103ac95e-0x103ac9c7) and lands within 2 units
                    // of the world hit is pulled 2 units back along the ray. The world
                    // hit here is a FMovingBrushTracker fragment of the same mover
                    // (UEMovingBrushTracker.cs) — that is what makes the branch live.
                    // Branch execution is proven (--moverspot, 4 cases from the original's
                    // journal); "the world's, not the mover's" is the form from the disasm: on these
                    // inputs both hits match bit-for-bit.
                    if (haveWorld && isMover
                        && hit.Normal == worldNormal
                        && MultiLineNearWorldHit(loc, worldLoc, UEWorldFrameProducer.FastMathWindow))
                    {
                        CoincidentFaceHits++;
                        // 256110-256112: the new location is built from the
                        // WORLD hit (local_b58/b5c/b60 = Location of the first
                        // result record [ebp-0xb64], read at 0x103aca54..0x103aca66
                        // as in the distance test 0x103ac99d..0x103ac9b1), NOT from the mover's
                        // own hit — `world - 2*dir`, not `mover - 2*dir`.
                        // They can sit up to 2 units apart, and the recomputed
                        // Time inherits the difference. No zero-span guard: shipped has none.
                        (loc, time) = MultiLinePullBack(start, actorEnd, worldLoc, UEWorldFrameProducer.FastMathWindow);
                    }
                    // 256103: rescale back to the FULL span, AFTER the dedup.
                    results.Add(new MoveHit(time * factor, hit.Normal, hit.Actor,
                        false, hit.Node, loc));
                }
            }

            // 256153: shipped sorts the collected array with appQsort by time —
            // appQsort (Core.dll.c:18592) is MSVCRT qsort, comparator 0x1030385a ->
            // 0x103ad500: A.Time < B.Time -> -1, A.Time > B.Time -> +1, else 0.
            //
            // THE ORDER OF EQUAL TIMES IS PORTED (MsvcQsort): on Lev5_fluffy the Mover7/Mover9
            // tie at fraction 0.196 decides fluffy0's Base, and the live original picks Mover9.
            // MsvcQsort is verified against the real msvcrt.dll qsort (original's bottle) under
            // unicorn.
            if (CaptureLineCheckPreSort) LastLineCheckPreSort = new List<MoveHit>(results);
            HP1.Core.MsvcQsort.Sort(results, (a, b) =>
                a.Fraction < b.Fraction ? -1 : a.Fraction > b.Fraction ? 1 : 0);
            if (TraceTies)
                for (int i = 1; i < results.Count; i++)
                    if (results[i].Fraction == results[i - 1].Fraction)
                        HP1.Core.UETrace.Info($"trace_tie frac={results[i].Fraction:R} "
                            + $"a={results[i - 1].Actor?.GetProp(PK.Name)} "
                            + $"b={results[i].Actor?.GetProp(PK.Name)}");
            return results;
        }

        // The swept box of ActorLineCheck: an empty FBox += Start, then += End (Core FBox::operator+= 0x1011ba10), then
        // Min − Extent and Max + Extent. Min keeps the old value unless it is strictly above the new one (`test ah,0x41;
        // je`: unordered keeps it), Max takes the new one unless the old is not below it (`test ah,1; jne`: unordered
        // takes it). A NaN End (physWalking's O/P probes of a zero adjust) gives Min = Start, Max = NaN; the cell of a
        // NaN is ROUND → the integer indefinite (negative) → 0 (CellIndex), so the walk is empty unless Min lies in
        // cell 0. Checked against the real ActorLineCheck (0x10365cc0) under unicorn.
        internal static (Vector3 min, Vector3 max) ActorLineCheckBox(Vector3 start, Vector3 end, Vector3 extent)
        {
            static float Lo(float old, float v) => old > v ? v : old;
            static float Hi(float old, float v) => old >= v ? old : v;
            return (new Vector3(Lo(start.x, end.x) - extent.x, Lo(start.y, end.y) - extent.y, Lo(start.z, end.z) - extent.z),
                new Vector3(Hi(start.x, end.x) + extent.x, Hi(start.y, end.y) + extent.y, Hi(start.z, end.z) + extent.z));
        }

        // FCollisionHash::ActorLineCheck (Engine.dll.c:161416): candidates come
        // from the swept AABB's hash CELLS (the shipped CollisionTag first-visit
        // dedup and the replica's oracle-validated walk order), each tested with
        // its OWN primitive — GetPrimitive (+0xa4) -> LineCheck (+0x5c). Shipped
        // PREPENDS every hit, so the returned order is reverse discovery and
        // MultiLineCheck sorts afterwards; kept in that shape.
        List<MoveHit> ActorLineCheck(Vector3 start, Vector3 end, Vector3 extent)
        {
            var found = new List<MoveHit>();
            (Vector3 qmin, Vector3 qmax) = ActorLineCheckBox(start, end, extent);
            foreach (int handle in CollisionHash.EncroachmentCandidates(qmin, qmax))
            {
                UEObject candidate = HashActor(handle);
                if (candidate is null || candidate.Destroyed) continue;
                if (!UEInterpreter.ToBool(candidate.GetProp(PK.bCollideActors))) continue;
                if (UEPrimitive.Resolve(candidate) == UEPrimitiveKind.Brush)
                {
                    BspHit b = TraceBrushActor(candidate, start, end, extent);
                    if (!b.Blocked) continue;
                    found.Insert(0, new MoveHit(b.Fraction, b.Normal, candidate, false, b.Node, b.Location));
                    continue;
                }
                ActorHit a = TraceNonBrushActor(candidate, start, end, extent);
                if (!a.Blocked) continue;
                found.Insert(0, new MoveHit(a.Time, a.Normal, candidate, false, -1, a.Location));
            }
            return found;
        }

        // ── SingleLineCheck ────────────────────────────────────────────────
        // Plain struct, not a positional record struct: those are C# 10 and
        // Core must compile on the netstandard2.1 + C#9 frontend profile.
        public readonly struct LineCheckHit
        {
            public bool Blocked { get; }
            public float Time { get; }
            public Vector3 Location { get; }
            public Vector3 Normal { get; }
            public UEObject Actor { get; }
            public int Node { get; }

            public LineCheckHit(bool Blocked, float Time, Vector3 Location,
                Vector3 Normal, UEObject Actor, int Node)
            {
                this.Blocked = Blocked;
                this.Time = Time;
                this.Location = Location;
                this.Normal = Normal;
                this.Actor = Actor;
                this.Node = Node;
            }

            public static LineCheckHit None
                => new LineCheckHit(false, 1f, Vector3.zero, Vector3.zero, null, -1);

            public static implicit operator LineCheckHit(
                (bool Blocked, float Time, Vector3 Normal, UEObject Actor) t)
                => new LineCheckHit(t.Blocked, t.Time, Vector3.zero, t.Normal, t.Actor, -1);

            public void Deconstruct(out bool blocked, out float time, out Vector3 normal, out UEObject actor)
            {
                blocked = Blocked; time = Time; normal = Normal; actor = Actor;
            }
        }

        // ULevel::SingleLineCheck (Engine.dll.c:97953): the closest hit of the
        // world+actor extent trace (MultiLineCheck, vtable 0xcc), skipping the
        // owner chain of `ownerToSkip`. Returns the hit actor (LevelInfo for a
        // world/BSP hit, per the shipped Actors[0] sentinel), or null on a
        // clean miss. World uses the validated BSP sweep; actors use the
        // validated shipped cylinder trace. Brush actors run their own
        // coords-form UModel::LineCheck — a separate primitive, never the
        // actor cylinder.
        public UEObject SingleLineCheckActor(UEInterpreter interp, UEObject self,
            UEObject ownerToSkip, Vector3 start, Vector3 end, Vector3 extent, int traceFlags)
            => SingleLineCheck(interp, self, ownerToSkip, start, end, extent, traceFlags).Actor;

        public LineCheckHit SingleLineCheck(UEInterpreter interp, UEObject self,
            UEObject ownerToSkip, Vector3 start, Vector3 end, Vector3 extent, int traceFlags)
        {
            LineCheckHit probed = SingleLineCheckBody(interp, self, ownerToSkip,
                start, end, extent, traceFlags);
            LineCheckProbe?.Invoke(start, end, extent, traceFlags, probed.Time);
            SingleLineCheckTap?.Invoke(this, self, ownerToSkip, start, end, extent, traceFlags, probed);
            return probed;
        }

        private LineCheckHit SingleLineCheckBody(UEInterpreter interp, UEObject self,
            UEObject ownerToSkip, Vector3 start, Vector3 end, Vector3 extent, int traceFlags)
        {
            // Shipped ETraceFlags: Pawns=1, Movers=2, Level=4, ZoneChanges=8,
            // Others=16, OnlyProjActor=32. The physFalling air-control probe
            // uses 6 and MUST ignore Pawns.
            //
            // The query itself is MultiLineCheck (97999): checkActors is
            // `flags & 0x17`, and the Level bit decides whether the world phase
            // runs at all. The world sentinel is Actors[0] — the object
            // ../Inc/UnLevel.h:0xf8 asserts IsA(ALevelInfo) and that MoveActor
            // compares identity against — NOT interp.GetLevelInfo(), which
            // lazily fabricates a stand-in LevelInfo when none was registered
            // and so is a DIFFERENT object once a map is loaded.
            UEObject levelInfo = LevelSentinel ?? interp.GetLevelInfo();
            List<MoveHit> hits = MultiLineCheck(start, end, extent,
                (traceFlags & 0x17) != 0, (traceFlags & 4) != 0 ? levelInfo : null);

            // The list is time-sorted, so the first hit that passes the filter
            // IS the closest one (98046-98104).
            foreach (MoveHit hit in hits)
            {
                UEObject candidate = hit.Actor;
                if (candidate is null || candidate.Destroyed || ReferenceEquals(candidate, self)) continue;
                if (IsInOwnerChain(ownerToSkip, candidate)) continue;
                // Shipped filter: the LevelInfo(4)/Mover(2)/ZoneInfo(8) CLASS
                // branches run first, and everything left is split by the inline
                // IsBlockedBy test against the SOURCE actor — blocked → bit 1,
                // not blocked → bit 0x10 (LAB_103ac3e2). Bit 1 is therefore
                // "actors that block me", NOT "Pawns": a blocking Decoration is
                // bit 1 and a non-blocking Pawn is 0x10.
                bool selected;
                if (UEInterpreter.ClassIsA(candidate.Class, "LevelInfo"))
                    selected = (traceFlags & 4) != 0;
                else if (UEInterpreter.ClassIsA(candidate.Class, "Mover"))
                    selected = (traceFlags & 2) != 0;
                else if (UEInterpreter.ClassIsA(candidate.Class, "ZoneInfo"))
                    selected = (traceFlags & 8) != 0;
                else if (self is null)
                    selected = (traceFlags & 16) != 0;   // no source → no blocking test
                else
                    selected = UEBlocking.IsBlockedBy(self, candidate, levelInfo)
                        ? (traceFlags & 1) != 0
                        : (traceFlags & 16) != 0;
                if (!selected) continue;
                return new LineCheckHit(true, hit.Fraction, hit.Location, hit.Normal,
                    candidate, hit.Node);
            }
            return LineCheckHit.None;
        }

        // AActor::FindBase (Engine.dll.c:21086): floor probe (Location → Z−8,
        // the actor's cylinder extent, flags=7) via ULevel::SingleLineCheck,
        // then SetBase to whatever it lands on. A world/BSP floor bases the
        // actor on the LevelInfo.
        public void FindBase(UEInterpreter interp, UEObject actor)
        {
            Vector3 loc = UEInterpreter.AsVector3(actor.GetProp(PK.Location));
            // Extent — GetCylinderExtent(this) (Engine.dll.c:21141, call 0x103e5064).
            Vector3 extent = GetCylinderExtent(actor);
            var end = new Vector3(loc.x, loc.y, loc.z - 8f);
            UEObject found = SingleLineCheckActor(interp, actor, actor, loc, end, extent,
                traceFlags: 7);
            Log($"find_base actor={actor.GetProp(PK.Name)} class={actor.Class?.Name} "
                + $"location={loc} base={found?.GetProp(PK.Name) ?? (object)"None"}");
            if (!ReferenceEquals(actor.GetProp(PK.Base) as UEObject, found))
                SetBase(interp, actor, found, notify: true);
        }

        // ── FindSpot / AdjustSpot ──────────────────────────────────────────
        // ULevel::AdjustSpot probe (body 0x103a9570): a zero-extent
        // SingleLineCheck(Hit, NULL, End=try, Start=spot, TraceFlags=6) — flags
        // 6 accepts ONLY LevelInfo (world) and Mover-class hits; pawns and
        // other actors are filtered out, and Source=NULL means no owner-chain
        // skipping. Returns the closest accepted hit or null.
        //
        // This is the SHARED SingleLineCheck -> MultiLineCheck (FUN_103ac620), with no
        // assembly of its own: the world trace sees mover brush fragments (tracker,
        // UEMovingBrushTracker.cs), the actor ray is shortened behind the world hit, and the
        // coincident-face branch (0x103ac95e-0x103aca31) puts the mover hit 2 units closer
        // than the world one — as in the original (Lev_Tut3b, Lev3_Dungeon/DungeonB: the shift
        // at the grate lands 0.86-2 units closer).
        public (float Time, Vector3 Normal)? AdjustSpotProbe(Vector3 start, Vector3 end)
        {
            if (LevelSentinel is null)
                throw new InvalidOperationException("AdjustSpot: the level has no LevelInfo (Actors[0]) — the MultiLineCheck world phase has nothing to go with");
            LineCheckHit h = SingleLineCheck(null, null, null, start, end, Vector3.zero, traceFlags: 6);
            return h.Blocked ? (h.Time, h.Normal) : null;
        }

        // ULevel::AdjustSpot (vtable 0xb8): probe spot→try, push the spot out
        // along the hit normal by Adjust*(1.05f-Time) when Time < 1.0f.
        public void AdjustSpot(ref Vector3 spot, Vector3 tryPoint, float adjust)
        {
            (float Time, Vector3 Normal)? hit = AdjustSpotProbe(spot, tryPoint);
            if (hit is null || !(hit.Value.Time < 1f)) return;
            Vector3 before = spot;
            spot = FindSpotMath.AdjustSpotPush(spot, hit.Value.Time, hit.Value.Normal, adjust);
            Log($"find_spot_adjust from={before} to={spot} time={hit.Value.Time} "
                + $"normal={hit.Value.Normal} adjust={adjust}");
        }

        // ULevel::FindSpot (vtable 0xb4, body 0x103a9690).
        // Both shipped call sites pass bCheckActors=0 (SpawnActor
        // Engine.dll.c:20104 with bTryCurrent=1, FarMoveActor 159193 with
        // bTryCurrent=0), so the point probes test the world BSP only, while
        // the AdjustSpot nudge rays also see mover brushes — a shipped
        // asymmetry, kept as-is.
        // Hence FCollisionHash::ActorPointCheck (0x103650b0, hash slot +0x14) is unreachable:
        // only ULevel::MultiPointCheck calls it with bActors != 0 (0x103abf9d..0x103abfdf),
        // MultiPointCheck is called only by SinglePointCheck (0x103abde4, bActors passed
        // through), and that only by FindSpot (0x103a97b5/0x103a9870/0x103a9a62/0x103a9c03,
        // bActors = FindSpot's bCheckActors). Disasm of all game DLLs: calls of ULevel slots
        // +0xb4/+0xc0/+0xc8 on the level are exactly SpawnActor (push 0 — 0x103a67e4) and
        // FarMoveActor (push 0 — 0x103a9f2b); only Editor.dll imports the exports. So the
        // port has no ActorPointCheck and needs none.
        public bool FindSpot(Vector3 extent, ref Vector3 location, bool tryCurrent)
            => FindSpotMath.FindSpot(extent, ref location, tryCurrent,
                p => !Bsp.PointCheck(p, extent, null).Hit,
                AdjustSpot);
    }
}
