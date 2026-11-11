// ADecal::execAttachDecal (Engine.dll, body 0x10410770) — the REQUEST is OWNED BY CORE.
//
// Surface selection lives in UELevelRuntime.DecalSurface; the request arguments
// (location, distance, direction, shadow size) come from bytecode executed by the
// VM, never from the frontend (C5/C7).
//
// WHERE THE REQUEST REALLY IS. In BYTECODE, executed by the VM.
// `Engine.ActorShadow.Update` (347 bytes of bytecode; text from decompiling
// Engine.u, not paraphrase — `extracted/Scripts/*/ActorShadow.uc` are the game
// packages' IMPORT STUBS, hence empty):
//
//     event Update(Actor L)
//     {
//         if (Owner == None || Owner.bDeleteMe || int(Owner.Style) == 3) return;
//         bUpdated = true;
//         Opacity  = Owner.Opacity;
//         Loc = Owner.BonePos(Owner.BoneName(0));      // natives 257 over 269
//         Loc.Z += 1;
//         Movement = VSize(OldOwnerLocation - Loc);
//         if (Movement <= MoveThreshold)
//             if (!bOriented || OldOwnerRotation == Owner.Rotation) return;
//         DetachDecal();
//         if (L == None) ShadowDir = vec(0,0,-1);
//         else { ShadowDir = Normal(Loc - L.Location);
//                if (ShadowDir.Z > 0) ShadowDir.Z *= -1; }
//         Extent = Owner.GetRenderExtent();            // native 274
//         DrawScale = (Max(int(Extent.X), int(Extent.Y)) * ShadowSizeFactor)
//                   / Max(Texture.USize, Texture.VSize);
//         SetLocation(Loc);
//         SetRotation(Rotator(-ShadowDir));
//         AttachDecal(MaxShadowDist, Vector(Owner.Rotation));
//         OldOwnerLocation = Loc;  OldOwnerRotation = Owner.Rotation;
//     }
//
// DIRECTION. The bytecode has `Rotator(-ShadowDir)` — WITH the minus (without it
// the ray would go up), so for `L == None`:
// bytecode has `Rotator(-ShadowDir)`, so for `L == None`
// Rotation looks UP, XAxis = (0,0,+1), and the ray end
// `Location + (-XAxis)*Distance` takes the trace DOWN — under the owner's
// feet, as drawn.
//
// WHO CALLS `Update` AND WHAT `L` IS — READ FROM THE DISASSEMBLY (V11).
// No campaign `.uc` calls `Update`, and the Engine.dll decompile shows only
// TWO mentions of the name, both FName table initialisers. A negative conclusion
// from the decompile is not allowed, and it would be wrong: the call lives in
// Render.dll (0x10b304be), where the compiler INLINED the `ADecal::eventUpdate`
// wrapper, so the images contain neither an import nor a `call` to its body
// (0x1033e3a0) or export thunk (0x10304b01) — checked by scanning both addresses:
//
//     mov  eax, [ebx+0x258]      ; Actor->Shadow   (field offset proven by
//     test eax, eax              ;  the --dumpprops oracle + prop_offsets.py)
//     je   ...
//     ...
//     mov  edi, [ebx+0x258]
//     mov  dword ptr [ebp-0x18], 0   ; <- parameter L = None
//     call [FindFunctionChecked]     ;    name Update
//     call [edx+0x10]                ;    ProcessEvent
//
// So shipped calls `Shadow.Update(None)` from the actor render, and the
// "no light passed" branch is the ONLY live one. The shadow is strictly vertical
// because the argument is hard-wired to zero at the call site.
using System;
using System.Collections.Generic;
using System.Linq;
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        // ── EXECUTOR — CORE'S OWN CODE ─────────────────────────────────────
        //
        // The whole body (gates, record, FUN_104127c0 nodes, MultiDecalLevel wrap,
        // detach) is Core's own code (UELevelRuntime.DecalStore.cs), bit-checked
        // against the translated body in the PC24 window (HeadlessSmoke --decalcore).
        // Surf->Decals lives in Core. The translation remains only as the check reference.
        //
        // Shadows on mover brushes use the surfaces the FMovingBrushTracker inserted
        // into the level model (UEMovingBrushTracker.cs), with the same body.
        public const bool DecalWrapPorted = true;
        public const bool SurfDecalListPorted = true;

        // Activation counters (V8): native calls and successful attachments.
        public int DecalAttachCalls;
        public int DecalAttachOk;
        public int DecalDetachCalls;
        // The return of the last call is observable; it is used by
        // `Decal.AttachToSurface` (`if (AttachDecal(100.0) == None) Destroy();`).
        public object LastAttachDecalReturn;

        public object AttachDecalNative(UEInterpreter interp, UEFrame frame, object[] args)
        {
            UEObject self = frame.Context ?? frame.Object;
            if (self is null) throw new InvalidOperationException("AttachDecal without a decal actor");
            float distance = args.Length > 0 ? UEInterpreter.AsFloat(args[0]) : 0f;
            // `optional Vector DecalDir`: omitted — P_GET_VECTOR_OPTX with
            // default (0,0,0), and the body takes the random-direction branch.
            Vector3 dir = args.Length > 1 && args[1] is not null ? UEInterpreter.AsVector3(args[1]) : Vector3.zero;
            // The body is ported for PC24 (render window, appEnableFastMath(1)) — that is how
            // ActorShadow.Update calls it. The game tick (PC64) never calls the native in the campaign.
            if (!UEWorldFrameProducer.FastMathWindow)
                throw new NotSupportedException(
                    $"AttachDecal outside the render window ({self.GetProp(PK.Name)}): the PC64 branch (appEnableFastMath(0)) is not ported — the campaign does not call it");
            DecalAttachCalls++;
            // appFrand of the random-direction branch — the render rand() stream.
            UEResourceRef ret = AttachDecalCore(self, distance, dir, FrameProducer?.RandInt);
            if (ret is not null) DecalAttachOk++;
            LastAttachDecalReturn = ret;
            return ret;
        }

        public object DetachDecalNative(UEInterpreter interp, UEFrame frame, object[] args)
        {
            UEObject self = frame.Context ?? frame.Object;
            if (self is null) throw new InvalidOperationException("DetachDecal without a decal actor");
            DecalDetachCalls++;
            DetachDecalCore(self);
            return null;
        }

        // Decal pieces of this tick's frames: FDecal record -> pieces (DrawFrame -> surface decal loop).
        readonly Dictionary<SurfDecal, List<DecalPiece>> _drawnDecalPieces = new(ReferenceEqualityComparer.Instance);

        // New game frame (URender::DrawWorld): last tick's pieces are not drawn.
        public void BeginDecalFrame() => _drawnDecalPieces.Clear();

        // URender::DrawFrame, surface decal loop (Render.dll.c:1455-1530): on a device without SpanBased, with
        // Client.Decals and a surface without PolyFlags & 0x8000000, every Surf->Decals record is clipped by ClipDecal
        // against EACH polygon of the surface drawn this frame (FSavedPoly; empty FDecal.Nodes — against all, else
        // against its own nodes) with this frame's camera; a non-empty piece sets Actor.LastRenderedTime (+0x260) =
        // Actor.Level.TimeSeconds (+0x3b4) and is drawn. The body does not check whether the decal actor is hidden.
        // view — frame index in WorldFrame.Views (−1 = master).
        readonly Dictionary<int, List<(int Node, Vector3[] View)>> _dfPolys = new();
        readonly Stack<List<(int Node, Vector3[] View)>> _dfListPool = new();
        readonly List<int> _dfOrder = new();
        readonly List<UEDecalClip.Point> _dfClip = new();
        public void DrawFrameDecals(UESceneNode f, int view)
        {
            if (f.DrawnPolys.Count == 0 || Bsp is null || !ClientDecalsEnabled(Config)) return;
            EnsureDecalStore();
            // Scratch reused per call (allocation only): the lists go back to the pool emptied.
            var polys = _dfPolys; var order = _dfOrder; var clip = _dfClip;
            foreach (var l in polys.Values) { l.Clear(); _dfListPool.Push(l); }
            polys.Clear(); order.Clear();
            foreach (var (s, n, v, _) in f.DrawnPolys)
            {
                if (!polys.TryGetValue(s, out var l)) { polys[s] = l = _dfListPool.Count > 0 ? _dfListPool.Pop() : new List<(int, Vector3[])>(); order.Add(s); }
                l.Insert(0, (n, v));                                   // FSavedPoly — at the head of the surface list
            }
            foreach (int s in order)
            {
                if (!_surfDecals.TryGetValue(s, out var recs) || recs.Count == 0) continue;
                BspSurfRecord sr = Bsp.Surf(s);
                if ((sr.PolyFlags & SurfNoDecalsFlag) != 0) continue;
                Vector3 basePt = Bsp.Point(sr.PBase), normal = Bsp.Vector(sr.VNormal);
                foreach (SurfDecal r in recs)
                {
                    if (r.Actor is null || r.Actor.Destroyed)
                        throw new InvalidOperationException($"DrawFrame: FDecal record of surface {s} without a live actor — shipped would read a dangling pointer");
                    if (!DecalTextureScale(r.Actor, out float uDiv, out float vDiv))
                        throw new InvalidOperationException($"decal {r.Actor.GetProp(PK.Name)}: texture without size/DrawScale — nothing to normalize U/V with");
                    float ds = UEValue.AsFloat(r.Actor.GetProp(PK.DrawScale));
                    foreach (var (node, vp) in polys[s])
                    {
                        if (r.Nodes.Count > 0 && !r.Nodes.Contains(node)) continue;
                        List<Vector3> world = UEDecalClip.SavedPolyWorld(f, vp);
                        if (UEDecalClip.Clip(normal, basePt, r.Vertices[0], r.Vertices[1], r.Vertices[2], r.Vertices[3], ds, f.Coords.Origin, world, clip) == 0)
                            continue;
                        UEObject level = r.Actor.GetProp(PK.Level) as UEObject
                            ?? throw new InvalidOperationException($"decal {r.Actor.GetProp(PK.Name)}: no Level — nothing to write LastRenderedTime from");
                        r.Actor.Props["LastRenderedTime"] = UEValue.AsFloat(level.GetProp(PK.TimeSeconds));
                        var pts = new Vector3[clip.Count]; var us = new float[clip.Count]; var vs = new float[clip.Count];
                        for (int i = 0; i < clip.Count; i++) { pts[i] = clip[i].World; us[i] = clip[i].U / uDiv; vs[i] = clip[i].V / vDiv; }
                        if (!_drawnDecalPieces.TryGetValue(r, out var pl)) _drawnDecalPieces[r] = pl = new List<DecalPiece>();
                        pl.Add(new DecalPiece(node, pts, us, vs, view));
                    }
                }
            }
        }

        // Snapshot of Surf->Decals for the frame: record geometry and the pieces drawn by this tick's DrawFrame (no
        // frame — no pieces). The frontend takes texture and style from the snapshot of the decal actor ITSELF
        // (ActorShadow: Texture='ShadowT', Style=4), otherwise one value would have two owners.
        public IReadOnlyList<DecalInstance> SnapshotDecals()
        {
            if (Bsp is null) return null;
            List<DecalInstance> list = null;
            // Texture flags only for a mover brush tracker surface (the frontend reads them only there; a map surface keeps
            // the level mesh's group): reading them for a map surface would load its texture package here, a side effect.
            foreach (var (s, _, r) in AllSurfDecals())
            {
                if (r.Actor is null || r.Actor.Destroyed) continue;
                BspSurfRecord sr = Bsp.Surf(s);
                DecalPiece[] pieces = _drawnDecalPieces.TryGetValue(r, out var pl) ? pl.ToArray() : Array.Empty<DecalPiece>();
                (list ??= new List<DecalInstance>()).Add(new DecalInstance(r.Actor.ObjectId,
                    (r.Actor.GetProp(PK.Owner) as UEObject)?.ObjectId ?? 0,
                    s, Bsp.Vector(sr.VNormal), Bsp.Point(sr.PBase), r.Vertices[0], r.Vertices[1], r.Vertices[2], r.Vertices[3], r.Nodes.ToArray(), pieces,
                    sr.PolyFlags, s >= (Bsp.Tracker?.Surfs0 ?? int.MaxValue) ? SurfTexturePolyFlags(s) : 0));
            }
            return list;
        }

        // Probe and fixtures: a frame with camera eye/rot — the same path as the game (CreateMaster, OccludeBsp of
        // master and children, DrawFrameDecals over the tree), without sprites and shadows; snapshot with this frame's pieces.
        // drawnPolys (probe): receives (Surf, Node) of every FSavedPoly of every frame of the tree.
        public IReadOnlyList<DecalInstance> SnapshotDecalsFrom(UEInterpreter interp, UEVector eye, UERotator rot,
            int showFlags = 0x480c, int rendMap = 5, Func<bool> spanBased = null, List<(int Surf, int Node)> drawnPolys = null)
        {
            UEObject any = Actors.FirstOrDefault(a => a is not null)
                ?? throw new InvalidOperationException("SnapshotDecalsFrom: the level has no actors");
            UEOcclusionWorld w = OcclusionWorld(interp, any);
            w.ShowFlags = showFlags; w.RendMap = rendMap;
            if (spanBased is not null) w.SpanBased = spanBased;
            var occ = new UEOcclusion();
            UESceneNode master = UESceneNode.CreateMaster(800, 600, eye, rot, 90f, rendMap);
            master.Level = w.Level;
            master.Zone = Bsp.FindRegion(master.Coords.Origin).ZoneNumber & 0xff;
            BeginDecalFrame();
            int k = 0;
            void Walk(UESceneNode f, int view)
            {
                occ.OccludeBsp(f, w); occ.ClearCache();
                foreach (UESceneNode c in f.Children) Walk(c, k++);
                if (drawnPolys is not null) foreach (var (s, n, _, _) in f.DrawnPolys) drawnPolys.Add((s, n));
                DrawFrameDecals(f, view);
            }
            Walk(master, -1);
            return SnapshotDecals();
        }

        // Surface whose decals DrawDecals skips: PolyFlags & 0x8000000.
        public const int SurfNoDecalsFlag = 0x8000000;

        // Device U/V normalisation of a decal: D3DDrv SetTexture takes the factor
        // 1/((2^mip·USize_mip)·Info.UScale) (D3DDrv.dll.c:3421-3429), and
        // UTexture::Lock — UScale = USize/USize_mip · Texture.DrawScale (+0x6c,
        // Engine.dll.c:97624-97626); result — 1/(USize·DrawScale), VSize for V.
        readonly Dictionary<string, (int U, int V, float Ds)> _decalTexScale = new();
        bool DecalTextureScale(UEObject decal, out float uDiv, out float vDiv)
        {
            uDiv = vDiv = 0f;
            object tex = decal.GetProp(PK.Texture);
            string key = TextureKey(tex);
            if (key is null) return false;
            if (!_decalTexScale.TryGetValue(key, out var t))
            {
                if (!UEInterpreter.TryTextureSize(tex, out int us, out int vs)) return false;
                if (!UEInterpreter.TryTextureDrawScale(tex, out float ds))
                    throw new InvalidOperationException($"decal: texture DrawScale of {key} not read (neither an export tag nor a class default)");
                _decalTexScale[key] = t = (us, vs, ds);
            }
            if (t.U <= 0 || t.V <= 0 || t.Ds == 0f) return false;
            uDiv = t.U * t.Ds; vDiv = t.V * t.Ds;
            return true;
        }

        // Random decal direction for a zero DecalDir — the one body AttachDecalCore runs; the --crtrandbatch check
        // (decaldir case) runs it against the real execAttachDecal block under unicorn at PC24.
        // Disasm 0x104109ab..0x10410a2e. AttachDecalNative runs only inside the render window (PC24), where every x87
        // operation rounds to float: appFrand st0 = fild rand · C (Core 0x10126150) rounded, `fadd st0,st0; fsub 1.0;
        // fstp dword`; FVector::SizeSquared (Core 0x10119700) (x²+y²)+z², `fcomp 1.0; test ah,0x41; je` loops while
        // the sum is above 1; FVector::UnsafeNormal (Core 0x10119ae0): the same sum, fstp qword, appSqrt, fdivr qword
        // 1.0 kept in the register, fmul, fstp dword. Squares of components in [-1, 1] that are not zero are at least
        // (2·C − 1 rounded)² ~ 1e-9: no float range edge, the C# float steps are the PC24 numbers.
        internal static Vector3 RandomDecalDir(Func<int> randInt)
        {
            static float Component(int r) { float f = r * HP1.Core.MsvcRandom.FrandScale; return (f + f) - 1f; }
            float x, y, z;
            do
            {
                x = Component(randInt());
                y = Component(randInt());
                z = Component(randInt());
            } while ((x * x + y * y) + z * z > 1f);
            // fsqrt and fdivr round to 24 bits: (float)sqrt, then 1/that in double rounded to float (53 >= 2·24 + 2).
            float inv = (float)(1.0 / (float)Math.Sqrt((x * x + y * y) + z * z));
            return new Vector3(x * inv, y * inv, z * inv);
        }
    }
}
