// RENDER VISIBILITY THAT SHIPPED DECIDES BEFORE OCCLUSION — Core-owned.
//
// Why. Part of the game state is written by the shipped RENDERER: `ActorShadow.Update`
// runs from URender::SetupDynamics (Render.dll FUN_10b2fb30), not from the tick
// (the live original 1.53 sends it on 38 maps, up to 1413 calls/s). Shipped decides
// whom to update here, in Level.Actors order and BEFORE OccludeBsp:
// `BoundVisible(Frame, Box, SpanBuffer = NULL, …)` (Render.dll 0x10b304a4:
// `push 0`) — i.e. without occlusion rasterisation, only the frame frustum.
//
// Sources (all shipped disasm/decompile, not SurrealEngine):
//   FSceneNode::ComputeRenderCoords  Engine.dll.c:28367 (RendMap branches 13/14/15
//                                    are editor ortho views, absent in game);
//   FSceneNode::ComputeRenderSize    Engine.dll.c:75870 (FX/FY/FX2/FY2, Proj,
//                                    RProj, PrjXM/PrjYM/PrjXP/PrjYP);
//   URender::CreateMasterFrame       Render.dll.c:29390-29426 (X/Y = viewport
//                                    size, XB = YB = 0, no parent);
//   FCoords::Inverse                 Core.dll.c:3446;
//   FBox::GetCoords                  Core.dll.c:54460;
//   URender::BoundVisible            Render.dll 0x10b21030 (disasm; the decompile
//                                    reads an uninitialised local_220);
//   URender::SetupDynamics           Render.dll 0x10b2fb30..0x10b304e9.
using System;
using System.Collections.Generic;
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    // Master-frame FSceneNode — exactly the fields read by SetupDynamics
    // visibility, plus the frustum side planes (+0x12c) its light cull reads.
    public sealed class UESceneNode
    {
        public BrushCoords Coords;          // +0x34 (Origin, X, Y, Z)
        public BrushCoords Uncoords;        // +0x64 = FCoords::Transpose(Coords) (Engine.dll.c:28499; D-56)
        public int X, Y;                    // +0xa8 / +0xac
        public float FX, FY, FX2, FY2;      // +0xb8 / +0xbc / +0xc8 / +0xcc
        public float FX15, FY15;            // +0xc0 / +0xc4 — raster projection centre (ClipBspSurf, D-56)
        public Vector3 NearClipN;           // +0x24 NearClip (FPlane) — zero for the master frame (W = 0)
        public float NearClipW;             // +0x30
        public float ProjZ, RProjZ;         // +0xdc / +0xe8
        public float PrjXM, PrjYM, PrjXP, PrjYP;   // +0xec / +0xf0 / +0xf4 / +0xf8
        public readonly Vector3[] ViewSides = new Vector3[4];   // +0xfc: frustum side rays in world space (occlusion, D-56)
        public readonly (Vector3 N, float W)[] ViewPlanes = new (Vector3, float)[4];   // +0x12c: side planes (SetupDynamics light cull)
        // Frame tree (occlusion, D-56): +4 Level, +8 Parent, +0x10 Child (newest first), +0x14 iSurf, +0x18 ZoneNumber,
        // +0x1c Recursion, +0x20 Mirror, +0x94 SpanBuffer, +0xa4 Sprite (by descending key).
        public object Level;
        public UESceneNode Parent;
        public readonly List<UESceneNode> Children = new();
        public int ISurf = -1, Zone, Recursion;
        public float Mirror = 1f;
        public UESpanBuffer SpanBuffer;
        public readonly List<UESprite> Sprites = new();
        // Not FSceneNode fields (port, D-61): which OccludeBsp branch created the frame and whose surfaces merged into
        // it — the frontend uses them to map world batches to child views (for mirror and sky CreateChildFrame gets iSurf = 0).
        public const int KindMaster = 0, KindSky = 1, KindMirror = 2, KindWarp = 3;
        public int Kind;
        public readonly List<int> SourceSurfs = new();
        // The frame's FSavedPoly (OccludeBsp, Render.dll.c:6602-6616 and 6641-6656; only for a non-SpanBased device): surface,
        // node and ClipBspSurf points in frame axes, in raster order (after reversal). ClipDecal clips them in DrawFrame.
        // Back: the eye is on the node's back side (side 0, Render.dll.c:5872-5874): the polygon is drawn with iZone[0]
        // (pFVar15[uStack_40 + 0x34]: lightmap zone, zone actor, fog).
        public readonly List<(int Surf, int Node, Vector3[] View, bool Back)> DrawnPolys = new();
        // Per DrawnPolys entry (same index): the draw record's volumetric input (+0x40 list, OccludeBsp 0x10b2471b..0x10b24836,
        // 0x10b2490b..0x10b249d2 new record, 0x10b24bfb..0x10b24cd3 merged): the walk's volumetric list as it stood at the
        // polygon's registration (VolumetricLeaves.Count) and the polygon's eye-pyramid edge planes (UEOcclusion.EdgePlanes).
        public readonly List<(int LeafMark, Vector3[] Edges)> DrawnPolyVolumetrics = new();
        // Port (frame export, not an FSceneNode field): PF_FakeBackdrop polygons whose sky branch spawned a sky child of this
        // frame. The branch returns before FSavedPoly (UEOcclusion.Surface), so they are not in DrawnPolys; the frontend
        // shows the sky frame through them.
        public readonly List<(int Surf, int Node)> SkyWindows = new();
        // Port (frame export): polygons whose draw record PolyFlags OccludeBsp changed from Surf | Texture flags — a
        // translucent mirror without ShinySurfaces (UEOcclusion.MirrorWithoutShine, pass 1).
        public readonly List<(int Surf, int Node, int PolyFlags)> Reflagged = new();
        // URender::SurfLights of this frame's walk (iSurf -> light ObjectIds, list order): the draw record's +0x48 list.
        public readonly Dictionary<int, int[]> SurfLights = new();
        // Dynamic entries of this frame's volumetric list (URender+0x30): LeafVolumetricLighting appends, after a leaf's static
        // chain, the +0x14-flagged links of LeafLights[leaf] (Render.dll.c:4642-4680) — (index in VolumetricLeaves of the
        // leaf, light ObjectId), walk order, before the +0xec Stamp (applied by LevelFogMaps.FrameStaticVolumetric).
        public readonly List<(int Leaf, int Id)> VolumetricDynamic = new();
        // Leaves OccludeBsp handed to LeafVolumetricLighting in this frame's walk (0x10b2302a near, 0x10b24dae far), in walk
        // order; empty when the walk's volumetric gate is off (UEOcclusion.LeafVolumetricLighting).
        public readonly List<int> VolumetricLeaves = new();

        // GMath.ViewCoords (Core FGlobalMath +0x5c; all unicorn oracles put the same values
        // into GMath): X = (0,1,0), Y = (0,0,-1), Z = (1,0,0).
        static BrushCoords ViewCoords => new()
        {
            Origin = Vector3.zero, X = new Vector3(0, 1, 0),
            Y = new Vector3(0, 0, -1), Z = new Vector3(1, 0, 0),
        };

        // URender::CreateMasterFrame -> FSceneNode::ComputeRenderCoords ->
        // ComputeRenderSize. rendMap 13/14/15 are editor ortho views: they never
        // occur in game, and porting them without a consumer would be unverifiable —
        // loud refusal.
        public static UESceneNode CreateMaster(int x, int y, Vector3 location, UERotator rotation,
            float fovAngle, int rendMap, UESceneNodeBanks banks = null)
        {
            if (rendMap == 13 || rendMap == 14 || rendMap == 15)
                throw new NotSupportedException(
                    $"FSceneNode: RendMap {rendMap} — editor ortho view (Engine.dll.c:28389-28470) not ported");
            UESceneNode f = banks?.Take() ?? new UESceneNode();
            f.X = x; f.Y = y; f.Fov = fovAngle;
            f.Coords = MasterCoords(location, rotation);
            f.Uncoords = HashBoxes.Transpose(f.Coords);
            f.ComputeRenderSize(fovAngle, pc24: false);
            // URender::CreateMasterFrame (Render.dll.c:8085-8100): span buffer covers the whole screen.
            f.SpanBuffer = banks?.Spans.Buffer() ?? new UESpanBuffer();
            f.SpanBuffer.AllocIndexForScreen(x, y);
            return f;
        }

        // A master frame for projection only, without the span buffer: the HPEngine display frame between two logic ticks
        // projects the corona tiles at its interpolated camera (CoronaBuilder; presentation, nothing in Core reads it).
        public static UESceneNode ProjectionMaster(int x, int y, Vector3 location, UERotator rotation, float fovAngle)
        {
            var f = new UESceneNode { X = x, Y = y, Fov = fovAngle };
            f.Coords = MasterCoords(location, rotation);
            f.Uncoords = HashBoxes.Transpose(f.Coords);
            f.ComputeRenderSize(fovAngle, pc24: false);
            return f;
        }

        public float Fov;   // Viewport->Actor->FovAngle (+0x370), read by the child's ComputeRenderSize

        // Port (allocation only): the pool this node came from (its children come from it too); null — plain allocation.
        internal UESceneNodeBanks Banks;

        // The state `new UESceneNode()` gives (UESceneNodeBanks; HP1_POOL_VERIFY compares every field with a new node).
        internal void ResetForPool()
        {
            Coords = default; Uncoords = default; X = Y = 0; FX = FY = FX2 = FY2 = 0f; FX15 = FY15 = 0f;
            NearClipN = default; NearClipW = 0f; ProjZ = RProjZ = 0f; PrjXM = PrjYM = PrjXP = PrjYP = 0f;
            Array.Clear(ViewSides); Array.Clear(ViewPlanes);
            Level = null; Parent = null; Children.Clear(); ISurf = -1; Zone = 0; Recursion = 0; Mirror = 1f; SpanBuffer = null;
            Sprites.Clear(); Kind = 0; SourceSurfs.Clear(); DrawnPolys.Clear(); DrawnPolyVolumetrics.Clear(); SkyWindows.Clear();
            Reflagged.Clear(); SurfLights.Clear(); VolumetricDynamic.Clear(); VolumetricLeaves.Clear(); Fov = 0f; Banks = null;
        }

        // URender::CreateChildFrame, new frame (FUN_10b20790): copy of the parent, then Level, Zone, iSurf, Recursion+1,
        // Mirror, NearClip, Coords and Uncoords (inlined transpose), Parent; pushed at the head of the parent's children;
        // ComputeRenderSize in the render window (PC24). Span buffer and bounds are set by the caller.
        internal UESceneNode CreateChild(in BrushCoords coords, object level, int iSurf, int zone, float mirror,
            Vector3 clipN, float clipW)
        {
            UESceneNode f = Banks?.Take() ?? new UESceneNode();
            f.X = X; f.Y = Y; f.Fov = Fov; f.Level = level; f.ISurf = iSurf; f.Zone = zone; f.Recursion = Recursion + 1;
            f.Mirror = mirror; f.NearClipN = clipN; f.NearClipW = clipW; f.Coords = coords; f.Parent = this;
            // Uncoords inlined (0x10b20c2f..0x10b20c95) in PC24: −((O.x·A.x + O.y·A.y) + O.z·A.z), axes transposed.
            BrushCoords c = coords;
            float ox = (c.Origin.x * c.X.x + c.Origin.y * c.X.y) + c.Origin.z * c.X.z;
            float oy = (c.Origin.x * c.Y.x + c.Origin.y * c.Y.y) + c.Origin.z * c.Y.z;
            float oz = (c.Origin.x * c.Z.x + c.Origin.y * c.Z.y) + c.Origin.z * c.Z.z;
            f.Uncoords = new BrushCoords
            {
                Origin = new Vector3(-ox, -oy, -oz),
                X = new Vector3(c.X.x, c.Y.x, c.Z.x), Y = new Vector3(c.X.y, c.Y.y, c.Z.y), Z = new Vector3(c.X.z, c.Y.z, c.Z.z),
            };
            Children.Insert(0, f);
            f.ComputeRenderSize(Fov, pc24: true);
            return f;
        }

        // Engine.dll.c:28477-28500: ViewCoords /= Rotation, then Origin += Location.
        // UGameEngine::Draw hands the same axes to audio (`Audio->Update(..., Frame->Coords)`,
        // Engine.dll.c:117938) — see UEGalaxyVoiceMix.Listener.
        // PRECISION: CreateMasterFrame (Render vtable +0x68, UGameEngine::Draw 0x1039fdd2) and the Audio->Update that
        // reads its Coords run BEFORE Render->PreRender (+0x60, 0x1039fe40, appEnableFastMath(1)): PC64, even though the
        // port builds the master frame inside its render window (OccludeWorld). BrushCoords' FCoords bodies follow the
        // window (D-74), so it is closed here for the duration.
        public static BrushCoords MasterCoords(Vector3 location, UERotator rotation)
        {
            bool window = UEWorldFrameProducer.FastMathWindow;
            UEWorldFrameProducer.FastMathWindow = false;
            try
            {
                BrushCoords c = ViewCoords;
                c.DivRotator(rotation.Pitch, rotation.Yaw, rotation.Roll);
                c.Origin = new Vector3(c.Origin.x + location.x, c.Origin.y + location.y, c.Origin.z + location.z);
                return c;
            }
            finally { UEWorldFrameProducer.FastMathWindow = window; }
        }

        // FSceneNode +0xdc Proj.Z = FX2 / tan(FovAngle·π/360) (Engine.dll.c:75891-75963): the one owner of the projection
        // distance — the frame, and every frontend projection of the same camera (HPEngine Camera, CoronaBuilder, fog,
        // draw log). FovAngle·(π/360 as float, 0x10474588) in x87 and straight to double (fstp qword); pc24 — the render
        // window (child frames), every operation rounds to float.
        // appTan (0x101260d0: fptan; fstp st(0)) = UEX87Transcendental.AppTan (correctly rounded like fsin, D-71); the
        // quotient is one fdiv in the register at the window's precision, then fstp dword.
        public static float ProjZOf(int x, float fovAngle, bool pc24)
        {
            float fx2 = x * 0.5f;
            if (pc24)
                return HP1.Core.X87Ext.Div24(HP1.Core.X87Ext.From(fx2), HP1.Core.UEX87Transcendental.AppTan(HP1.Core.X87Ext.From((double)(fovAngle * 0.008726646f)))).ToFloat();
            return HP1.Core.X87Ext.Div64(HP1.Core.X87Ext.From(fx2), HP1.Core.UEX87Transcendental.AppTan(HP1.Core.X87Ext.From((double)fovAngle * (double)0.008726646f))).ToFloat();
        }

        // Engine.dll.c:75891-75963. Storage is float, as in shipped. pc24 — call from the render window (child, CreateChildFrame):
        // every x87 operation rounds to float; the master frame is built before PreRender — PC64 (modelled as double).
        void ComputeRenderSize(float fovAngle, bool pc24)
        {
            FX = X;
            FY = Y;
            FX2 = FX * 0.5f;
            FY2 = FY * 0.5f;
            // 0x103833fd..0x1038341e: FX in the register + 1.0001f (0x1047458c), ·0.5f, stored as float (the sum is exact in double).
            FX15 = (float)(((double)FX + (double)1.0001f) * 0.5);
            FY15 = (float)(((double)FY + (double)1.0001f) * 0.5);
            ProjZ = ProjZOf(X, fovAngle, pc24);
            RProjZ = 1.0f / ProjZ;
            PrjXM = (-FX2) * (-RProjZ);
            PrjXP = (FX - FX2) * RProjZ;
            PrjYM = (-FY2) * (-RProjZ);
            PrjYP = (FY - FY2) * RProjZ;
            // ViewSides (0x10383569..0x10383696): ray (±FX2, ±FY2, ProjZ)/|·| (CRT sqrt, 1.0 double), then
            // TransformVectorBy(Uncoords) inlined ((v.x·A.x + v.y·A.y) + v.z·A.z); slot (2i + j), i — sign of X.
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                {
                    float a = (i == 0 ? -1f : 1f) * FX2, b = (j == 0 ? -1f : 1f) * FY2, c = ProjZ;
                    float vx, vy, vz;
                    if (pc24)
                    {
                        float l2 = (a * a + b * b) + c * c;
                        float r = 1f / MathF.Sqrt(l2);
                        vx = r * a; vy = r * b; vz = r * c;
                    }
                    else
                    {
                        double l2 = ((double)a * a + (double)b * b) + (double)c * c;
                        double r = 1.0 / Math.Sqrt(l2);
                        vx = (float)(r * a); vy = (float)(r * b); vz = (float)(r * c);
                    }
                    BrushCoords u = Uncoords;
                    ViewSides[i * 2 + j] = pc24
                        ? new Vector3((vx * u.X.x + vy * u.X.y) + vz * u.X.z, (vx * u.Y.x + vy * u.Y.y) + vz * u.Y.z,
                            (vx * u.Z.x + vy * u.Z.y) + vz * u.Z.z)
                        : new Vector3((float)(((double)vx * u.X.x + (double)vy * u.X.y) + (double)vz * u.X.z),
                            (float)(((double)vx * u.Y.x + (double)vy * u.Y.y) + (double)vz * u.Y.z),
                            (float)(((double)vx * u.Z.x + (double)vy * u.Z.y) + (double)vz * u.Z.z));
                }
            // ViewPlanes +0x12c (0x1038369b..0x1038394a; the decompile lost the TransformVectorBy calls): for s = −1, +1 the
            // plane through the Y edge at +0x12c + i·0x10, then through the X edge at +0x14c + i·0x10. Local normal Y edge
            // (0, rf·a, r·b) with a = s/FY2, b = 1/ProjZ, r = 1.0/sqrt(a²+b²) and rf = r stored as float (fst [ebp-0x1c]);
            // X edge (r·a, 0, r·b) with a = s/FX2. Normal = TransformVectorBy(Uncoords), W = (N.x·O.x + N.y·O.y) + N.z·O.z.
            for (int i = 0; i < 2; i++)
            {
                float s = i == 0 ? -1f : 1f;
                ViewPlanes[i] = EdgePlane(s / FY2, false, pc24);
                ViewPlanes[2 + i] = EdgePlane(s / FX2, true, pc24);
            }
        }

        (Vector3 N, float W) EdgePlane(float a, bool xEdge, bool pc24)
        {
            float b = 1f / ProjZ;
            float vx, vy, vz;
            if (pc24)
            {
                float r = 1f / MathF.Sqrt(a * a + b * b);
                vx = xEdge ? r * a : 0f; vy = xEdge ? 0f : r * a; vz = r * b;
            }
            else
            {
                double r = 1.0 / Math.Sqrt((double)a * a + (double)b * b);
                float rf = (float)r;
                vx = xEdge ? (float)(r * a) : 0f; vy = xEdge ? 0f : rf * a; vz = (float)(r * b);
            }
            BrushCoords u = Uncoords;
            Vector3 o = Coords.Origin;
            if (pc24)
            {
                var n = new Vector3((vx * u.X.x + vy * u.X.y) + vz * u.X.z, (vx * u.Y.x + vy * u.Y.y) + vz * u.Y.z,
                    (vx * u.Z.x + vy * u.Z.y) + vz * u.Z.z);
                return (n, (n.x * o.x + n.y * o.y) + n.z * o.z);
            }
            var nd = new Vector3((float)(((double)vx * u.X.x + (double)vy * u.X.y) + (double)vz * u.X.z),
                (float)(((double)vx * u.Y.x + (double)vy * u.Y.y) + (double)vz * u.Y.z),
                (float)(((double)vx * u.Z.x + (double)vy * u.Z.y) + (double)vz * u.Z.z));
            return (nd, (float)(((double)nd.x * o.x + (double)nd.y * o.y) + (double)nd.z * o.z));
        }
    }

    // Port (allocation only, render scheduling): frame-tree nodes and the master's screen spans pooled per DrawWorld in two
    // banks by DrawWorldCounter parity, like the sprites (UEOcclusion.NewSprite) — the previous tree's nodes and its master
    // SpanBuffer stay intact while the next is built. The zone and child-frame span buffers are not banked: they come from
    // UEOcclusion._arena (UESpanBuffer.Sibling), reset at the start of every master OccludeBsp pass, so a previous tree's
    // child SpanBuffer is dead once the next pass starts (only OccludeBsp reads span buffers, within its pass). Holders of a node: LastMasterFrame, UEActorLights/ActorFogFrame (rebuilt each tick from it), the
    // tick's index maps; the frontend reads no node (WorldFrame copies). A reused node is in the state `new` gives.
    public sealed class UESceneNodeBanks
    {
        readonly List<UESceneNode>[] _nodes = { new(), new() };
        readonly UESpanArena[] _spans = { new(), new() };
        List<UESceneNode> _bank;
        int _n;
        internal UESpanArena Spans { get; private set; }
        internal static long NodesReused;   // verify mode only

        // CreateMasterFrame of DrawWorld `counter`: the nodes and spans of the tree two DrawWorlds back are free again.
        public void BeginTree(int counter)
        {
            _bank = _nodes[counter & 1]; _n = 0;
            Spans = _spans[counter & 1];
            Spans.Reset();
        }

        internal UESceneNode Take()
        {
            if (_bank is null) throw new InvalidOperationException("UESceneNodeBanks: Take before BeginTree");
            UESceneNode f;
            if (_n < _bank.Count)
            {
                f = _bank[_n];
                f.ResetForPool();
                if (UEVmPool.Verify) { CheckFresh(f); System.Threading.Interlocked.Increment(ref NodesReused); }
            }
            else _bank.Add(f = new UESceneNode());
            _n++;
            f.Banks = this;
            return f;
        }

        static readonly System.Reflection.FieldInfo[] Fields = typeof(UESceneNode).GetFields(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        static readonly UESceneNode Fresh = new UESceneNode();
        static void CheckFresh(UESceneNode f)
        {
            foreach (var fi in Fields)
            {
                object got = fi.GetValue(f), want = fi.GetValue(Fresh);
                bool same = got is Array arr
                    ? System.Linq.Enumerable.SequenceEqual(System.Linq.Enumerable.Cast<object>(arr), System.Linq.Enumerable.Cast<object>((Array)want))
                    : got is System.Collections.ICollection c ? c.Count == 0 : Equals(got, want);
                if (!same) throw new InvalidOperationException($"HP1_POOL_VERIFY: UESceneNode.ResetForPool leaves {fi.Name} different from a new node");
            }
        }
    }

    public static class UERenderVisibility
    {
        // FBox::GetCoords (Core.dll.c:54460): Origin = Min, axes = box sides.
        public static BrushCoords BoxCoords(Vector3 min, Vector3 max) => new()
        {
            Origin = min,
            X = new Vector3(max.x - min.x, 0f, 0f),
            Y = new Vector3(0f, max.y - min.y, 0f),
            Z = new Vector3(0f, 0f, max.z - min.z),
        };

        // FCoords::Inverse (Core body 0x1014e700). Called FROM THE RENDERER, i.e. in
        // the PreRender..PostRender window, where x87 runs with a 24-bit mantissa
        // (URender::PreRender -> appEnableFastMath(1)): every arithmetic result
        // rounds to single precision — this is float arithmetic.
        // Operation order follows the disasm (the decompile reordered it).
        public static BrushCoords Inverse(in BrushCoords c)
        {
            float xx = c.X.x, xy = c.X.y, xz = c.X.z;
            float yx = c.Y.x, yy = c.Y.y, yz = c.Y.z;
            float zx = c.Z.x, zy = c.Z.y, zz = c.Z.z;
            float det = (((yz * zx - yx * zz) * xy) + ((zy * yx - yy * zx) * xz)) + ((yy * zz - zy * yz) * xx);
            float d = 1f / det;
            float a1 = zy * yx - yy * zx, b1 = zx * xy - xx * zy, c1 = xx * yy - yx * xy;
            float a2 = yz * zx - zz * yx, b2 = zz * xx - xz * zx, c2 = xz * yx - yz * xx;
            float a3 = zz * yy - yz * zy, b3 = xz * zy - zz * xy, c3 = yz * xy - xz * yy;
            var rX = new Vector3(a3 * d, b3 * d, c3 * d);
            var rY = new Vector3(a2 * d, b2 * d, c2 * d);
            var rZ = new Vector3(a1 * d, b1 * d, c1 * d);
            float ox = -c.Origin.x, oy = -c.Origin.y, oz = -c.Origin.z;
            return new BrushCoords
            {
                Origin = new Vector3(
                    (oy * rY.x + ox * rX.x) + oz * rZ.x,
                    (oy * rY.y + oz * rZ.y) + rX.y * ox,
                    (oy * rY.z + oz * rZ.z) + rX.z * ox),
                X = rX, Y = rY, Z = rZ,
            };
        }

        // URender::BoundVisible (Render.dll 0x10b21030), branch SpanBuffer == NULL
        // and RendMap outside 13..15 — the only one SetupDynamics calls for the
        // shadow. Returns 1/0 as shipped; screen bounds (FScreenBounds) do not affect
        // the result with a NULL span buffer and are not computed here.
        //
        //  1. The frame is moved into box space: Origin via TransformPointBy,
        //     X/Y axes via inlined TransformVectorBy, Z axis via a Core call
        //     (0x10b211bf..0x10b21393).
        //  2. Camera in normalised box coordinates p = Inverse(B)·Frame.Origin
        //     (0x10b213ad..0x10b213fe; p.x and p.y are stored as float, p.z stays
        //     on the x87 stack). All three in [0,1] — camera inside: visible (0x10b21466).
        //  3. n = −p; two depth terms per box axis: Z'_i·n_i and
        //     Z'_i + Z'_i·n_i (float slots). All six < 0 — the box is entirely behind
        //     the camera: invisible (0x10b2154f..0x10b215ed).
        //  4. The same terms for screen X and Y; the eight corners are sums of one term
        //     per axis in shipped order; corner outcode:
        //       1: D·PrjXM + SX < 0,  2: D·PrjXP − SX < 0,
        //       4: D·PrjYM + SY < 0,  8: D·PrjYP − SY < 0.
        //     A bit common to all eight — invisible; otherwise visible (with a NULL span
        //     buffer BoxIsVisible is not called).
        public static bool BoundVisible(UESceneNode frame, in BrushCoords box) => BoundVisible(frame, box, out _, out _);

        // FScreenBounds output in Y (Y1 +4, Y2 +0xc) — read by FDynamicSprite::Setup: rejects when Y2 <= Y1
        // (Render 0x10b30dc0..0x10b30e08). Disasm 0x10b21e6b..0x10b21fd1 (PC24, all float): corner 0 —
        // r = ProjZ / D, Y = fistp(float(r·SY + FY2) − 0.5); bit 4 of any corner -> Y1 = 0, bit 8 -> Y2 = frame Y;
        // corners 1..7 in the same order: y < Y1 -> Y1 = y, else y > Y2 -> Y2 = y; then Y1 = max(Y1, 0),
        // Y2 = min(Y2, Y). Camera inside the box — the whole frame (Y1 = 0, Y2 = FY). Invisible — bounds not written.
        public static bool BoundVisible(UESceneNode frame, in BrushCoords box, out int y1, out int y2)
        {
            bool v = BoundVisible(frame, box, out ScreenBounds sb);
            y1 = (int)sb.Y1; y2 = (int)sb.Y2;
            return v;
        }

        // Shipped FScreenBounds: +0 MinX, +4 MinY, +8 MaxX, +0xc MaxY (integers, written by fild/fstp), +0x10 MinZ, +0x14 MaxZ.
        public struct ScreenBounds { public float X1, Y1, X2, Y2, Z1, Z2; }

        // ── URender::Project (Render.dll 0x10b2b200, export ?Project@URender@@UAEHPAUFSceneNode@@ABVFVector@@AAM2PAM@Z) ──
        // Render window (PC24): d = V − Coords.Origin (each difference is float); x/y/z = ((d.x·A.x + d.y·A.y) + d.z·A.z)
        // along frame axes X/Y/Z (0x10b2b2e1..0x10b2b335); |z| < 0.01 (.rdata 0x10b38fc4) -> z + 0.02 (0x10b39190);
        // persp = ProjZ / z; screen = x·persp + FX2, y·persp + FY2; success only if z > 1.0 (fcomp, test ah 0x41).
        // Editor ortho views (RendMap 13/14/15) never occur in game — loud refusal.
        public static bool Project(UESceneNode f, Vector3 v, int rendMap, out float sx, out float sy, out float persp)
        {
            if (rendMap is 13 or 14 or 15) throw new NotSupportedException("URender::Project: editor ortho view (RendMap 13/14/15) not ported");
            BrushCoords c = f.Coords;
            float dx = v.x - c.Origin.x, dy = v.y - c.Origin.y, dz = v.z - c.Origin.z;
            float x = (dx * c.X.x + dy * c.X.y) + dz * c.X.z;
            float y = (dx * c.Y.x + dy * c.Y.y) + dz * c.Y.z;
            float z = (dx * c.Z.x + dy * c.Z.y) + dz * c.Z.z;
            float az = z < 0f ? -z : z;
            if (!(az >= 0.01f)) z = z + 0.02f;                  // C0: less or unordered
            persp = f.ProjZ / z;
            sx = x * persp + f.FX2;
            sy = y * persp + f.FY2;
            return z > 1f;
        }

        // appCeil (Core 0x10126200): ceil over double (MSVCRT), then _ftol truncation; NaN and out-of-int -> 0x80000000.
        public static int AppCeil(float a)
        {
            double c = Math.Ceiling((double)a);
            return double.IsNaN(c) || c >= 2147483648.0 || c < -2147483648.0 ? int.MinValue : (int)c;
        }

        // fild int -> fstp float -> fld -> fistp: integer through float (out-of-int -> 0x80000000).
        static int ViaFloat(int i)
        {
            float f = i;
            return f >= 2147483648f || f < -2147483648f ? int.MinValue : (int)f;
        }

        // ── FDynamicSprite::Setup, sprite branch (Render.dll 0x10b30e4b..0x10b3111d; DrawType 1 and 7) ─────────────────
        // No texture — reject before depth. Z = Zc = ((dz·Z.z + dy·Z.y) + dx·Z.x) − SpriteProjForward (+0x190), d = Location −
        // Coords.Origin; Z < SpriteProjForward·(−2.0) (.rdata 0x10b395fc) — reject (not for ortho views). Screen point —
        // URender::Project (GRender vtable +0x7c); half size = ((USize·persp)·DrawScale)·0.5 (0x10b385ec); X1/X2 =
        // appCeil(sx ∓ half) through float, swap, clamp to [0, frame X], reject if X2 ≤ 0 or X1 ≥ X − 1; Y likewise by
        // VSize and frame Y, success: Y2 > 0, Y1 < Y, Y1 < Y2. ShowFlags & 0x20 (editor icons) — loud refusal.
        public static bool SpriteSetup(UESceneNode f, Vector3 loc, float drawScale, float spriteProjForward, int uSize, int vSize,
            bool hasTexture, int showFlags, int rendMap, out ScreenBounds sb)
        {
            sb = default;
            if ((showFlags & 0x20) != 0) throw new NotSupportedException("FDynamicSprite::Setup: ShowFlags & 0x20 (editor icon sprites) not ported");
            if (!hasTexture) return false;
            BrushCoords c = f.Coords;
            float dx = loc.x - c.Origin.x, dy = loc.y - c.Origin.y, dz = loc.z - c.Origin.z;
            float z = ((dz * c.Z.z + dy * c.Z.y) + dx * c.Z.x) - spriteProjForward;
            sb.Z1 = sb.Z2 = z;
            if (!(z >= spriteProjForward * -2f) && rendMap is not (13 or 14 or 15)) return false;
            if (!Project(f, loc, rendMap, out float sx, out float sy, out float persp)) return false;
            float hx = (((float)uSize * persp) * drawScale) * 0.5f;
            int x1 = ViaFloat(AppCeil(sx - hx)), x2 = ViaFloat(AppCeil(hx + sx));
            if (x1 > x2) (x1, x2) = (x2, x1);
            if (x1 < 0) { x1 = 0; if (x2 < 0) x2 = 0; }
            if (x2 > f.X) { x2 = f.X; if (x1 > f.X) x1 = f.X; }
            if (!(x2 > 0) || !(x1 < f.X - 1)) return false;
            float hy = (((float)vSize * persp) * drawScale) * 0.5f;
            int y1 = ViaFloat(AppCeil(sy - hy)), y2 = ViaFloat(AppCeil(hy + sy));
            if (y1 > y2) (y1, y2) = (y2, y1);
            if (y1 < 0) { y1 = 0; if (y2 < 0) y2 = 0; }
            if (y2 > f.Y) { y2 = f.Y; if (y1 > f.Y) y1 = f.Y; }
            sb.X1 = x1; sb.X2 = x2; sb.Y1 = y1; sb.Y2 = y2;
            return y2 > 0 && y1 < f.Y && y1 < y2;
        }

        // Full screen bounds (disasm 0x10b21e6b..0x10b21ff5): X like Y with ScreenX = fistp(r·SX + FX2 − 0.5)
        // with bits 1/2 and clamp to [0, frame X]; MinZ = max(0, min corner depth) (NaN -> 0), MaxZ = max depth as-is.
        // Camera inside the box: the whole frame, MinZ = 0, MaxZ = 0x60ad78ec (1e20).
        public static bool BoundVisible(UESceneNode frame, in BrushCoords box, out ScreenBounds sb)
            => BoundVisible(frame, box, null, out sb);

        // span — the caller's FSpanBuffer (OccludeBsp, map node): a visible box outside free spans is invisible
        // (0x10b21ff8..0x10b2201d: BoxIsVisible on UNclamped bounds). Camera inside the box — visible without the test.
        public static bool BoundVisible(UESceneNode frame, in BrushCoords box, UESpanBuffer span, out ScreenBounds sb)
        {
            sb = default;
            int y1, y2;
            BrushCoords f = frame.Coords;
            // Render window: x87 in PC24 — all arithmetic below is single precision (float).
            // 1. Frame axes in box coordinates.
            Vector3 xa = Rot(f.X, box), ya = Rot(f.Y, box), za = Rot(f.Z, box);
            // 2. Camera inside the box? (x87 comparisons with unordered — as in the disasm)
            BrushCoords inv = Inverse(box);
            Vector3 fo = f.Origin;
            float px, py, pz;
            if (UEEdition.RuOrder)
            {   // RU EA (RU Render 0x10b20ae3..0x10b20b3e): ((Y·fo.y + Z·fo.z) + X·fo.x) + O.
                px = ((inv.Y.x * fo.y + inv.Z.x * fo.z) + inv.X.x * fo.x) + inv.Origin.x;
                py = ((inv.Y.y * fo.y + inv.Z.y * fo.z) + inv.X.y * fo.x) + inv.Origin.y;
                pz = ((inv.Y.z * fo.y + inv.Z.z * fo.z) + inv.X.z * fo.x) + inv.Origin.z;
            }
            else
            {
                px = ((inv.X.x * fo.x + inv.Z.x * fo.z) + inv.Y.x * fo.y) + inv.Origin.x;
                py = ((inv.X.y * fo.x + inv.Z.y * fo.z) + inv.Y.y * fo.y) + inv.Origin.y;
                pz = ((inv.X.z * fo.x + inv.Z.z * fo.z) + inv.Y.z * fo.y) + inv.Origin.z;
            }
            if (px >= 0f && !(px > 1f) && py >= 0f && !(py > 1f) && pz >= 0f && !(pz > 1f))
            {
                sb = new ScreenBounds { X1 = 0f, Y1 = 0f, X2 = frame.FX, Y2 = frame.FY, Z1 = 0f, Z2 = BitConverter.Int32BitsToSingle(0x60ad78ec) };
                return true;
            }
            float nx = -px, ny = -py, nz = -pz;
            // 3. Depth along the axes.
            float zx0 = za.x * nx, zy0 = za.y * ny, zz0 = za.z * nz;
            float zx1 = za.x + zx0, zy1 = za.y + zy0, zz1 = za.z + zz0;
            if (Lt0(zx0) && Lt0(zy0) && Lt0(zz0) && Lt0(zx1) && Lt0(zy1) && Lt0(zz1))
                return false;
            // 4. Screen terms and the eight corners (addition order as shipped).
            float xx0 = xa.x * nx, xy0 = xa.y * ny, xz0 = xa.z * nz;
            float xx1 = xa.x + xx0, xy1 = xa.y + xy0, xz1 = xa.z + xz0;
            float yx0 = ya.x * nx, yy0 = ya.y * ny, yz0 = ya.z * nz;
            float yx1 = yx0 + ya.x, yy1 = ya.y + yy0, yz1 = ya.z + yz0;
            float t = zy1 + zz1, u = xy1 + xz1, v = yy1 + yz1;
            Span<float> d = stackalloc float[8], sy = stackalloc float[8];
            Span<float> sx = stackalloc float[8];
            d[0] = (zx0 + zy0) + zz0; sx[0] = (xx0 + xy0) + xz0; sy[0] = (yx0 + yy0) + yz0;
            d[1] = (zx1 + zy0) + zz0; sx[1] = (xx1 + xy0) + xz0; sy[1] = (yx1 + yy0) + yz0;
            d[2] = (zy1 + zx0) + zz0; sx[2] = (xy1 + xx0) + xz0; sy[2] = (yy1 + yx0) + yz0;
            d[3] = (zx1 + zy1) + zz0; sx[3] = (xx1 + xy1) + xz0; sy[3] = (yx1 + yy1) + yz0;
            d[4] = (zz1 + zx0) + zy0; sx[4] = (xz1 + xx0) + xy0; sy[4] = (yz1 + yx0) + yy0;
            d[5] = (zx1 + zz1) + zy0; sx[5] = (xx1 + xz1) + xy0; sy[5] = (yx1 + yz1) + yy0;
            d[6] = zx0 + t; sx[6] = xx0 + u; sy[6] = yx0 + v;
            d[7] = zx1 + t; sx[7] = xx1 + u; sy[7] = yx1 + v;
            int and = 0xF, or = 0;
            for (int c = 0; c < 8; c++) { int code = Outcode(frame, d[c], sx[c], sy[c]); and &= code; or |= code; }
            if (and != 0) return false;
            float zMin = d[0], zMax = d[0];
            for (int c = 1; c < 8; c++)
            {
                if (d[c] < zMin) zMin = d[c];
                if (zMax < d[c]) zMax = d[c];
            }
            int x1 = ScreenX(frame, d[0], sx[0]), x2 = x1;
            y1 = ScreenY(frame, d[0], sy[0]); y2 = y1;
            if ((or & 1) != 0) x1 = 0;
            if ((or & 2) != 0) x2 = frame.X;
            if ((or & 4) != 0) y1 = 0;
            if ((or & 8) != 0) y2 = frame.Y;
            for (int c = 1; c < 8; c++)
            {
                int x = ScreenX(frame, d[c], sx[c]);
                if (x < x1) x1 = x;
                else if (x > x2) x2 = x;
                int y = ScreenY(frame, d[c], sy[c]);
                if (y < y1) y1 = y;
                else if (y > y2) y2 = y;
            }
            int rx1 = x1, ry1 = y1, rx2 = x2, ry2 = y2;
            if (x1 < 0) x1 = 0;
            if (y1 < 0) y1 = 0;
            if (x2 > frame.X) x2 = frame.X;
            if (y2 > frame.Y) y2 = frame.Y;
            sb = new ScreenBounds { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Z1 = zMin < 0f || float.IsNaN(zMin) ? 0f : zMin, Z2 = zMax };
            if (span is not null && !span.BoxIsVisible(rx1, ry1, rx2, ry2)) return false;
            return true;
        }

        static int ScreenX(UESceneNode f, float depth, float sx)
        {
            float r = f.ProjZ / depth;
            float x = r * sx + f.FX2;
            return RoundEven(x - 0.5f);
        }

        static int ScreenY(UESceneNode f, float depth, float sy)
        {
            float r = f.ProjZ / depth;
            float y = r * sy + f.FY2;
            return RoundEven(y - 0.5f);
        }

        // fistp with default rounding (round-to-nearest-even); the value is float (PC24 window).
        // x87 fistp with RC=00: nearest even; NaN and out-of-int32 -> "integer indefinite" 0x80000000.
        static int RoundEven(float v)
        {
            double d = Math.Round((double)v, MidpointRounding.ToEven);
            return double.IsNaN(d) || d < int.MinValue || d > int.MaxValue ? int.MinValue : (int)d;
        }

        // `fcomp 0; test ah,1`: C0 is set both for "less" and for "unordered".
        static bool Lt0(float v) => !(v >= 0f);

        static Vector3 Rot(Vector3 v, in BrushCoords b) => new(
            (v.x * b.X.x + v.y * b.X.y) + v.z * b.X.z,
            (v.x * b.Y.x + v.y * b.Y.y) + v.z * b.Y.z,
            (v.x * b.Z.x + v.y * b.Z.y) + v.z * b.Z.z);

        static int Outcode(UESceneNode f, float depth, float sx, float sy)
        {
            int code = 0;
            // 0x10b21748..0x10b217d8: bit on C0 (less OR unordered).
            if (Lt0(depth * f.PrjXM + sx)) code |= 1;
            if (Lt0(depth * f.PrjXP - sx)) code |= 2;
            if (Lt0(depth * f.PrjYM + sy)) code |= 4;
            if (Lt0(depth * f.PrjYP - sy)) code |= 8;
            return code;
        }

        // ── URender::SetupDynamics: WHOM SHIPPED UPDATES WITH A SHADOW ─────────────
        //
        // Disasm 0x10b2fb30..0x10b304e9, in order for each Level.Actors(i):
        //   * precondition of the whole pass: Model->Nodes.Num != 0 and
        //     ShowFlags & SHOW_Actors (8) on the viewport actor (0x10b2fb62..7d);
        //   * empty -> next; bHighDetail with HighDetailActors off
        //     (RenDev+0x64) -> next (0x10b2fbbf);
        //   * not an ortho child frame, not bBehindView and actor == ViewTarget ->
        //     next (0x10b2fbd4..ef);
        //   * actor == Exclude -> no dynamics and no shadow (0x10b2fbf5);
        //   * bHidden -> same (0x10b2fc0a..1d, game branch);
        //   * viewport actor IsA PlayerPawn -> APlayerPawn::IsActorVisible —
        //     in the shipped data `mov al,1; ret 4` (0x10379720), no other native
        //     overrides (no other native overrides exist): the gate is empty but kept in place;
        //   * VisibilityRadius != 0 and !(VisibilityRadius > |XY to camera|) ->
        //     no shadow; VisibilityHeight != 0 and !(|dZ| < VisibilityHeight) ->
        //     no shadow (0x10b2fc83..0x10b2fd24);
        //   * bOnlyOwnerSee: the Owner chain (starting at the actor itself) must
        //     reach the viewport actor, which must not be bBehindView; bOwnerNoSee:
        //     if the chain reaches the viewport actor — only with bBehindView
        //     (0x10b2fd2a..89);
        //   * Brush != None, IsA Brush and not bStatic -> Level->BrushTracker->Update(actor),
        //     no shadow (0x10b2fd8f..b8, call 0x10b30501..0x10b30511);
        //   * Shadow != None (+0x258), RendMap outside 13..15: box
        //     [Loc − (R, R, 8192), Loc + (R, R, 0)] (R = CollisionRadius),
        //     centre in front of the view plane accounting for half sides, then
        //     BoundVisible(…, NULL) -> Shadow.Update(None) (0x10b302b8..0x10b304e9).
        // RenderIterator/RenderInterface and the dynamics element between the last two
        // steps are not called by script and do not affect the selection.
        public static List<UEObject> ShadowUpdateSet(UESceneNode frame, IList<UEObject> levelActors,
            UEObject viewer, UEObject exclude, bool highDetailActors, int rendMap)
        {
            var result = new List<UEObject>();
            ShadowUpdatePass(frame, levelActors, viewer, exclude, highDetailActors, rendMap, result.Add);
            return result;
        }

        // Pass in shipped form: decision and the Update call in THE SAME loop, actor
        // by actor, re-reading Level.Actors.Num on every iteration
        // (0x10b2fba2..ab): the Update body may change the world before the next actor.
        public static void ShadowUpdatePass(UESceneNode frame, IList<UEObject> levelActors,
            UEObject viewer, UEObject exclude, bool highDetailActors, int rendMap, Action<UEObject> update)
            => ShadowUpdatePass(frame, levelActors, viewer, exclude, highDetailActors, rendMap, update, null);

        // brushUpdate — Level->BrushTracker->Update(Actor) of the brush branch (0x10b2fd8f..0x10b2fdb8 ->
        // 0x10b30501..0x10b30511): with the same actor and at the same point of the loop as shipped — the next
        // actor's shadow already sees the moved fragments.
        // recursion — Frame.Recursion (+0x1c): for a child frame (mirror, warp portal) the ViewTarget skip is off
        // (0x10b2fbd4..ef: `Recursion != 0 || bBehindView || actor != ViewTarget`), and its Exclude is NULL.
        public static void ShadowUpdatePass(UESceneNode frame, IList<UEObject> levelActors,
            UEObject viewer, UEObject exclude, bool highDetailActors, int rendMap, Action<UEObject> update,
            Action<UEObject> brushUpdate, int recursion = 0)
            => SetupDynamicsPass(frame, levelActors, viewer, exclude, highDetailActors, rendMap, null, update, brushUpdate, recursion);

        // Whole URender::SetupDynamics, actor by actor (order D-58): gate -> brush branch -> FDynamicSprite
        // (sprite: sprite Setup — the GetRenderBoundingBox frame and BoundVisible are decided by the caller) -> actor shadow.
        // In shipped the sprite Setup sits between the brush branch and the shadow (0x10b2fdb8..0x10b302b2), and the shadow
        // Update body (ActorShadow.Update -> Owner.BonePos -> ApplyAnim(rootOnly)) sees the cache header AFTER the frame and
        // BEFORE the frame's GetFrame (DrawFrame runs after all SetupDynamics, Render.dll.c:2659-2660).
        public static void SetupDynamicsPass(UESceneNode frame, IList<UEObject> levelActors,
            UEObject viewer, UEObject exclude, bool highDetailActors, int rendMap, Action<UEObject> sprite,
            Action<UEObject> update, Action<UEObject> brushUpdate, int recursion = 0, Action<UEObject> light = null)
        {
            if (viewer is null) return;
            if ((UEValue.ToInt(viewer.GetProp(PK.ShowFlags)) & 8) == 0) return;
            for (int i = 0; i < levelActors.Count; i++)
            {
                UEObject a = levelActors[i];
                if (!DynamicsHeadPasses(a, viewer, highDetailActors, recursion)) continue;
                if (DynamicsActorPassesAfterHead(frame, a, viewer, exclude))
                {
                    Vector3 loc = UEValue.AsVector3(a.GetProp(PK.Location));
                    if (a.GetProp(PK.Brush) is not null && UEInterpreter.ClassIsA(a.Class, "Brush")
                        && !UEValue.ToBool(a.GetProp(PK.bStatic)))
                        brushUpdate?.Invoke(a);
                    else
                    {
                        sprite?.Invoke(a);
                        if (a.GetDeclared("Actor", "Shadow") is UEObject shadow && !shadow.Destroyed
                            && rendMap is not (13 or 14 or 15)
                            && ShadowBoxVisible(frame, loc, UEValue.AsFloat(a.GetProp(PK.CollisionRadius))))
                            update(a);
                    }
                }
                // Every actor past the head gates reaches the light gate 0x10b3051a (14 jumps there from the sprite,
                // brush and shadow branches and their rejections, incl. bHidden / Exclude / not a PlayerPawn viewer).
                light?.Invoke(a);
            }
        }

        // The three rejections of the URender::SetupDynamics loop that skip the light gate too (jumps to 0x10b3075b):
        // empty slot, bHighDetail (+0x28 & 0x400000) without HighDetailActors, and the ViewTarget of a frame with
        // Recursion 0 (+0x1c) seen without bBehindView (Viewport->Actor +0x25c bit 0, ViewTarget +0x4f4).
        public static bool DynamicsHeadPasses(UEObject a, UEObject viewer, bool highDetailActors, int recursion)
        {
            if (a is null || a.Destroyed) return false;
            if (UEValue.ToBool(a.GetProp(PK.bHighDetail)) && !highDetailActors) return false;
            bool behind = UEValue.ToBool(viewer.GetProp(PK.bBehindView));
            return !(recursion == 0 && !behind && ReferenceEquals(a, viewer.GetProp(PK.ViewTarget)));
        }

        // Actor selection in the URender::SetupDynamics loop (0x10b2fba2..0x10b2fd8f) — before the brush branch,
        // shared by shadows (ShadowUpdatePass) and the frame's direct path (UEWorldFrameProducer.DirectRenderStep).
        // recursion — Frame.Recursion (+0x1c): for a child frame the ViewTarget skip is off.
        public static bool DynamicsActorPasses(UESceneNode frame, UEObject a, UEObject viewer, UEObject exclude,
            bool highDetailActors, int recursion)
        {
            return DynamicsHeadPasses(a, viewer, highDetailActors, recursion)
                && DynamicsActorPassesAfterHead(frame, a, viewer, exclude);
        }

        // The gates after DynamicsHeadPasses (which only reads properties): SetupDynamicsPass has already run the head.
        static bool DynamicsActorPassesAfterHead(UESceneNode frame, UEObject a, UEObject viewer, UEObject exclude)
        {
            bool behind = UEValue.ToBool(viewer.GetProp(PK.bBehindView));
            if (ReferenceEquals(a, exclude)) return false;
            if (UEValue.ToBool(a.GetProp(PK.bHidden))) return false;
            if (!UEInterpreter.ClassIsA(viewer.Class, "PlayerPawn")) return false;   // IsA APlayerPawn failed -> 0x10b3051a
            // APlayerPawn::IsActorVisible is always true (0x10379720).
            Vector3 loc = UEValue.AsVector3(a.GetProp(PK.Location));
            Vector3 org = frame.Coords.Origin;
            float visR = UEValue.AsFloat(a.GetProp(PK.VisibilityRadius));
            if (visR != 0f)
            {
                // PC24: sum of squares and fsqrt (appSqrt -> MSVCRT sqrt) in single precision.
                float dy = loc.y - org.y, dx = loc.x - org.x;
                float dist = (float)Math.Sqrt(dy * dy + dx * dx);
                if (!(visR > dist)) return false;
            }
            float visH = UEValue.AsFloat(a.GetProp(PK.VisibilityHeight));
            if (visH != 0f && !(Math.Abs(loc.z - org.z) < visH)) return false;
            if (UEValue.ToBool(a.GetProp(PK.bOnlyOwnerSee)) && (!OwnedBy(a, viewer) || behind)) return false;
            if (UEValue.ToBool(a.GetProp(PK.bOwnerNoSee)) && OwnedBy(a, viewer) && !behind) return false;
            return true;
        }

        // Owner chain, starting at the actor itself (0x10b2fd33..44).
        static bool OwnedBy(UEObject a, UEObject viewer)
        {
            for (UEObject o = a; o is not null; o = o.GetProp(PK.Owner) as UEObject)
                if (ReferenceEquals(o, viewer)) return true;
            return false;
        }

        // Shadow box and its tests (0x10b30303..0x10b304bc). Storage is float.
        public static bool ShadowBoxVisible(UESceneNode frame, Vector3 loc, float radius)
        {
            // PC24: all arithmetic is single precision.
            float maxX = radius + loc.x, maxY = radius + loc.y, maxZ = loc.z;
            float minZ = loc.z - 8192f, minY = loc.y - radius, minX = loc.x - radius;
            float cz = (minZ + maxZ) * 0.5f, cy = (minY + maxY) * 0.5f, cx = (minX + maxX) * 0.5f;
            float ex = (maxX - minX) * 0.5f, ey = (maxY - minY) * 0.5f, ez = (maxZ - minZ) * 0.5f;
            BrushCoords f = frame.Coords;
            float push = (Math.Abs(ex * f.Z.x) + Math.Abs(ez * f.Z.z)) + Math.Abs(ey * f.Z.y);
            float dot = ((cy - f.Origin.y) * f.Z.y + (cz - f.Origin.z) * f.Z.z) + (cx - f.Origin.x) * f.Z.x;
            if (dot < -push) return false;
            return BoundVisible(frame, BoxCoords(new Vector3(minX, minY, minZ), new Vector3(maxX, maxY, maxZ)));
        }
    }
}
