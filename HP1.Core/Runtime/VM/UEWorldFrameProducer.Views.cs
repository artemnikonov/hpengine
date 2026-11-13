// Frame-building stages of the 3D view: dynamic surface lights, actor view projection (ProjectViews,
// DrawnOf), particle render boxes, MeshSpriteSetup, DirectRenderStep, MeshDrawParts and OccludeWorld.
// Fields live in UEWorldFrameProducer.cs.
using System;
using System.Collections.Generic;
using System.IO;
using HP1.Core;
using UELib;

namespace HP1.UEScript
{
    public sealed partial class UEWorldFrameProducer
    {
        (int, int)? DefaultSpriteTextureSize(string path)
        {
            _spriteTextures ??= new UETextureMetrics(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_systemDir)));
            UETextureMetrics.Row r = _spriteTextures.Find(path);
            if (r is null || !int.TryParse(r.Values[0], out int u) || !int.TryParse(r.Values[1], out int v)) return null;
            return (u, v);
        }
        // The corona ray of this frame. NOT ORIGINAL (Enhanced mode): pawns on the ray hide the corona too; Original runs the shipped TraceFlags 6 alone.
        public Func<UEVector, UEVector, bool> CoronaClear => EnhancedVisuals
            ? _coronaEnhancedClear ??= (s, e) => CoronaLineClear(s, e) && CoronaPawnClear(s, e)
            : CoronaLineClear;
        public double ViewportClock => _viewportClock;   // the viewport (wall) clock: coronas, actor light fade and AmbientGlow
        public IReadOnlyDictionary<int, int[]> SurfDynamicLights => _surfDyn;
        void CollectSurfDynamicLights(UESceneNode master, UEOcclusionWorld w)
        {
            _surfDyn = new Dictionary<int, int[]>();
            SurfDynamicLightsRefusal = null;
            if (w.SpanBased?.Invoke() == true)
            {
                bool any = false;
                void Any(UESceneNode f) { any |= f.SurfLights.Count > 0; foreach (UESceneNode c in f.Children) Any(c); }
                Any(master);
                if (any) SurfDynamicLightsRefusal = "SpanBased device: dynamic lights reached surfaces, but the port keeps no draw records for it — +0x48 not assembled";
                return;
            }
            HashSet<int> seen = _surfDynSeen;   // scratch (allocation only)
            seen.Clear();
            void Visit(UESceneNode f)
            {
                foreach (UESceneNode c in f.Children) Visit(c);
                foreach (var p in f.DrawnPolys)
                    if (seen.Add(p.Surf) && f.SurfLights.TryGetValue(p.Surf, out int[] ids)) _surfDyn[p.Surf] = ids;
            }
            Visit(master);
        }

        // Occlusion frame tree -> WorldFrame.Views (D-61): top-down walk, M = master Coords⁻¹ ∘ frame Coords
        // (axes orthogonal: Coords⁻¹(v) = O + v.x·X + v.y·Y + v.z·Z), NearClip from the frame axes into its world.
        internal static List<ViewFrame> ProjectViews(UESceneNode master, Func<int, ViewSource> sourceOf)
            => ProjectViews(master, sourceOf, null);

        // index — tree frame -> index in Views (master — −1), for DrawFrame decal pieces. drawn — receives each frame's
        // polygons (WorldFrame.Drawn: master first, then the views in the same order).
        internal static List<ViewFrame> ProjectViews(UESceneNode master, Func<int, ViewSource> sourceOf, Dictionary<UESceneNode, int> index,
            List<DrawnWorld> drawn = null, Func<int, int, ViewSource> polyOf = null)
        {
            var views = new List<ViewFrame>();
            index?.Add(master, -1);
            drawn?.Add(DrawnOf(master, polyOf));
            BrushCoords r = master.Coords;
            void Walk(UESceneNode f, int parent)
            {
                foreach (UESceneNode c in f.Children)
                {
                    int kind = c.Kind switch
                    {
                        UESceneNode.KindSky => ViewFrame.KindSky, UESceneNode.KindMirror => ViewFrame.KindMirror,
                        UESceneNode.KindWarp => ViewFrame.KindWarp,
                        _ => throw new InvalidOperationException($"child frame without an OccludeBsp branch (Kind {c.Kind})"),
                    };
                    BrushCoords k = c.Coords;
                    double E(int i, int j)   // (X_r⊗X_k + Y_r⊗Y_k + Z_r⊗Z_k)[i][j]
                        => (double)C(r.X, i) * C(k.X, j) + (double)C(r.Y, i) * C(k.Y, j) + (double)C(r.Z, i) * C(k.Z, j);
                    var m = new Affine
                    {
                        M00 = (float)E(0, 0), M01 = (float)E(0, 1), M02 = (float)E(0, 2),
                        M10 = (float)E(1, 0), M11 = (float)E(1, 1), M12 = (float)E(1, 2),
                        M20 = (float)E(2, 0), M21 = (float)E(2, 1), M22 = (float)E(2, 2),
                    };
                    double[] t = new double[3];
                    for (int i = 0; i < 3; i++)
                        t[i] = C(r.Origin, i) - (E(i, 0) * k.Origin.x + E(i, 1) * k.Origin.y + E(i, 2) * k.Origin.z);
                    m.M03 = (float)t[0]; m.M13 = (float)t[1]; m.M23 = (float)t[2];
                    UEVector cn = default; float cd = 0f;
                    if (c.NearClipW != 0f)
                    {
                        var nv = c.NearClipN;
                        double nx = (double)k.X.x * nv.x + (double)k.Y.x * nv.y + (double)k.Z.x * nv.z;
                        double ny = (double)k.X.y * nv.x + (double)k.Y.y * nv.y + (double)k.Z.y * nv.z;
                        double nz = (double)k.X.z * nv.x + (double)k.Y.z * nv.y + (double)k.Z.z * nv.z;
                        cn = new UEVector((float)nx, (float)ny, (float)nz);
                        cd = (float)(-(nx * k.Origin.x + ny * k.Origin.y + nz * k.Origin.z) - c.NearClipW);
                    }
                    var src = new ViewSource[c.SourceSurfs.Count];
                    for (int i = 0; i < src.Length; i++) src[i] = sourceOf(c.SourceSurfs[i]);
                    int me = views.Count;
                    index?.Add(c, me);
                    views.Add(new ViewFrame(parent, kind, c.Recursion, m, cn, cd,
                        BitConverter.SingleToInt32Bits(c.Mirror) < 0, src, c.Coords, c.Zone));
                    drawn?.Add(DrawnOf(c, polyOf));
                    Walk(c, me);
                }
            }
            Walk(master, -1);
            return views;
        }

        static float C(UEVector v, int i) => i == 0 ? v.x : i == 1 ? v.y : v.z;

        // A frame's FSavedPoly list -> DrawnWorld: level nodes in OccludeBsp order, mover polygons once however many of
        // their tracker fragments were drawn (the frontend draws a mover polygon whole; its coplanar pieces are
        // UELevelRuntime.MoverCoplanarPieces), sky windows (UESceneNode.SkyWindows). polyOf: (surface, node) -> level surface or
        // the mover polygon of the fragment's owner (UEOcclusionWorld.PolyOf).
        internal static DrawnWorld DrawnOf(UESceneNode f, Func<int, int, ViewSource> polyOf)
        {
            var nodes = new List<int>(f.DrawnPolys.Count);
            List<int> back = null;
            var movers = new List<ViewSource>();
            HashSet<(int, int)> seen = null;
            foreach (var (surf, node, _, fromBack) in f.DrawnPolys)
            {
                ViewSource src = polyOf(surf, node);
                if (src.Surf >= 0) { nodes.Add(node); if (fromBack) (back ??= new List<int>()).Add(node); continue; }
                seen ??= new HashSet<(int, int)>();
                if (seen.Add((src.MoverId, src.MoverPoly))) movers.Add(src);
            }
            var sky = new int[f.SkyWindows.Count];
            for (int i = 0; i < sky.Length; i++)
            {
                if (polyOf(f.SkyWindows[i].Surf, f.SkyWindows[i].Node).Surf < 0)
                    throw new NotSupportedException($"sky window on a mover polygon (tracker surface {f.SkyWindows[i].Surf}): the frontend has no mover backdrop");
                sky[i] = f.SkyWindows[i].Node;
            }
            (int, ViewSource, int)[] reflagged = null;
            if (f.Reflagged.Count > 0)
            {
                reflagged = new (int, ViewSource, int)[f.Reflagged.Count];
                for (int i = 0; i < reflagged.Length; i++)
                {
                    var (surf, node, pf) = f.Reflagged[i];
                    ViewSource src = polyOf(surf, node);
                    reflagged[i] = (src.Surf >= 0 ? node : -1, src, pf);
                }
            }
            return new DrawnWorld(nodes.ToArray(), movers.ToArray(), sky, back?.ToArray(), reflagged);
        }

        // Surf->Texture of every polygon a frame of the tree drew, first appearance order (DrawFrame Update, see DrawnWorld);
        // a surface without a texture is not updated (Level->DefaultTexture, Render.dll 0x10b258af..0x10b258c1).
        internal static List<string> DrawnTexturesOf(UESceneNode master, UEOcclusionWorld w)
        {
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Walk(UESceneNode f)
            {
                foreach (var (surf, _, _, _) in f.DrawnPolys)
                    if (w.Bsp?.MaterialOfSurf(surf)?.Path is string p && seen.Add(p)) list.Add(p);
                foreach (UESceneNode c in f.Children) Walk(c);
            }
            Walk(master);
            return list;
        }

        // FDynamicSprite::Setup of a mesh actor (Render 0x10b30cec-0x10b30dc0): IsA AParticleFX with bit +0x3c8
        // gives the full screen without a box — not ported for meshes (loud); otherwise the GetRenderBoundingBox(1) box,
        // reject when |X|² = 0 (sum order y² + z² + x², fcomp 0x10b30d89), then frame BoundVisible.
        // Setup rejects an empty screen rectangle (Y2 <= Y1, Render 0x10b30dc0..0x10b30e08): `sb.Y2 > sb.Y1` below.
        // AParticleFX::GetRenderBoundingBox (Engine 0x103c1060, export 0x1030464c; disasm 0x103c1060..0x103c18e3). Called
        // by FDynamicSprite::Setup in the render window — x87 PC24: every operation below rounds to float.
        //   1. Shift M = Location − LastUpdateLocation; L = max(|ParticleList.Max − Min|², 10000) (sums (x²+z²)+y²);
        //      |M|² > L -> Update(0): success and a container -> the container box as-is, otherwise zero Coords.
        //   2. Otherwise box P = ParticleList (+0x54); when Age > ElapsedTime it grows:
        //      h = ½·max(SizeWidth, SizeLength) (Rand < 0 -> 0); source: Distribution 2 with Owner.Mesh — FBox(owner
        //      mesh box), Pattern — ±(0, ½DrawScale, ½DrawScale), otherwise ±½(SourceDepth, SourceWidth, SourceHeight),
        //      both via FBox(·, ToLocal()) (slot +0x98); G = source ± h; P = P valid ? P ∪ G : G.
        //   3. A resting bSteadyState with Lifetime ≤ ElapsedTime — done. Otherwise T = min(Age − ElapsedTime, Lifetime),
        //      r = (max(Speed)+Chaos/ChaosDelay)·T + size correction, ·1.1; P = P ± r; gravity offset
        //      D = (GravityModifier·ZoneGravity + Gravity)·T²·½·1.1; P += P.Min + D, P += P.Max + D.
        //   Result — FBox::GetCoords(P).
        internal BrushCoords ParticleRenderBoundingBox(UEObject fx)
        {
            FxState s = FxStateOf(fx);
            UEVector loc = UEValue.AsVector3(fx.GetProp(PK.Location));
            UEVector lu = UEValue.AsVector3(fx.GetProp(PK.LastUpdateLocation));
            float mx = loc.x - lu.x, my = loc.y - lu.y, mz = loc.z - lu.z;
            float ex = s.BoundsMax.x - s.BoundsMin.x, ey = s.BoundsMax.y - s.BoundsMin.y, ez = s.BoundsMax.z - s.BoundsMin.z;
            float lim = (ex * ex + ez * ez) + ey * ey;
            if (!(lim >= 10000f)) lim = 10000f;                                       // fcom; test ah,1
            if ((mx * mx + mz * mz) + my * my > lim)                                  // fcomp st1; test ah,0x41
            {
                bool ok;
                _grbbPath = true;
                try { ok = RenderStepFx(fx); } finally { _grbbPath = false; }
                if (ok) return BoxGetCoords(s.BoundsMin, s.BoundsMax);
                return default;
            }
            UEObject owner = fx.GetProp(PK.Owner) as UEObject;
            UEObject zone = (fx.GetProp(PK.Region) as UEObject)?.GetProp(PK.Zone) as UEObject ?? fx.GetProp(PK.Level) as UEObject;
            var input = new PfxBoxIn
            {
                Location = loc, LastUpdateLocation = lu, ListMin = s.BoundsMin, ListMax = s.BoundsMax, ListValid = s.BoundsValid,
                Age = PropF(fx, "Age"), ElapsedTime = PropF(fx, "ElapsedTime"),
                SizeWidth = Range(fx, "SizeWidth"), SizeLength = Range(fx, "SizeLength"),
                SteadyState = UEValue.ToBool(fx.GetProp(PK.bSteadyState)),
                OwnerMesh = UEValue.ToInt(fx.GetProp(PK.Distribution)) == 2 && owner is not null && owner.GetProp(PK.Mesh) is not null,
                Pattern = fx.GetProp(PK.Pattern) is not null, DrawScale = PropF(fx, "DrawScale"),
                SourceWidth = Range(fx, "SourceWidth"), SourceHeight = Range(fx, "SourceHeight"), SourceDepth = Range(fx, "SourceDepth"),
                Rotation = UEValue.AsRotator(fx.GetProp(PK.Rotation)),
                Lifetime = Range(fx, "Lifetime"), Speed = Range(fx, "Speed"), SizeEndScale = Range(fx, "SizeEndScale"),
                Chaos = PropF(fx, "Chaos"), ChaosDelay = PropF(fx, "ChaosDelay"),
                HasZone = zone is not null, ZoneGravity = zone is null ? default : UEValue.AsVector3(zone.GetProp(PK.ZoneGravity)),
                GravityModifier = PropF(fx, "GravityModifier"), Gravity = UEValue.AsVector3(fx.GetProp(PK.Gravity)),
            };
            // The closure only when the box reads it (allocation only: ParticleRenderBox calls it only for OwnerMesh).
            return ParticleRenderBox(input, input.OwnerMesh ? OwnerMeshBox(owner) : null, fx.GetProp(PK.Name)?.ToString());
        }

        Func<BrushCoords> OwnerMeshBox(UEObject owner) => () =>
        {
            if (MeshRenderBoundingBox is null) throw new InvalidOperationException("ParticleFX box: owner mesh box (UELevelRuntime.MeshRenderBoundingBox) not bound");
            return MeshRenderBoundingBox(owner);
        };

        static (float Base, float Rand) Range(UEObject o, string p) => (RangeRaw(o, p, false), RangeRaw(o, p, true));

        // Inputs of AParticleFX::GetRenderBoundingBox after the shift branch (actor fields by offset).
        public struct PfxBoxIn
        {
            public UEVector Location, LastUpdateLocation;           // +0xfc, +0x380
            public UEVector ListMin, ListMax; public bool ListValid; // ParticleList +0x54..+0x6c
            public float Age, ElapsedTime;                           // +0x3a8, +0x3ac
            public (float Base, float Rand) SizeWidth, SizeLength;   // +0x2d0, +0x2d8
            public bool SteadyState;                                 // +0x29c bit 0
            public bool OwnerMesh;                                   // Distribution (+0x334) == 2 && Owner && Owner.Mesh
            public bool Pattern; public float DrawScale;             // +0x338, +0x16c
            public (float Base, float Rand) SourceWidth, SourceHeight, SourceDepth;   // +0x264, +0x26c, +0x274
            public UERotator Rotation;                               // +0x108
            public (float Base, float Rand) Lifetime, Speed, SizeEndScale;            // +0x2a8, +0x2a0, +0x2e0
            public float Chaos, ChaosDelay;                          // +0x318, +0x31c
            public bool HasZone; public UEVector ZoneGravity;        // Region.Zone ?? Level, +0x260
            public float GravityModifier; public UEVector Gravity;   // +0x344, +0x348
        }

        // Body after the shift branch (0x103c1218..0x103c18e3). ownerMeshBox — the owner mesh box (vtable +0x70); called
        // exactly where shipped calls it (after Age > ElapsedTime and the particle size) — for a skeletal mesh this is ApplyAnim.
        public static BrushCoords ParticleRenderBox(in PfxBoxIn i, Func<BrushCoords> ownerMeshBox, string who = null)
        {
            UEVector pMin = i.ListMin, pMax = i.ListMax;
            bool pValid = i.ListValid;
            float age = i.Age, elapsed = i.ElapsedTime;
            if (!(age > elapsed)) return BoxGetCoords(pMin, pMax);
            static float NonNeg(float v) => v >= 0f ? v : 0f;                        // fcom 0; test ah,1 (NaN -> 0)
            float sl = NonNeg(i.SizeLength.Rand) + i.SizeLength.Base;
            float size = NonNeg(i.SizeWidth.Rand) + i.SizeWidth.Base;
            if (!(size >= sl)) size = sl;
            float half = size * 0.5f;
            UEVector loc = i.Location, lu = i.LastUpdateLocation;
            bool steadyAtRest = i.SteadyState && loc.x == lu.x && loc.y == lu.y && loc.z == lu.z;
            UEVector gMin, gMax;
            if (i.OwnerMesh) (gMin, gMax) = BoxFromCoords24(ownerMeshBox());
            else
            {
                UEVector v;
                if (i.Pattern)
                {
                    float d = i.DrawScale * 0.5f;
                    v = new UEVector(0f, d, d);
                }
                else
                {
                    float hx = (NonNeg(i.SourceDepth.Rand) + i.SourceDepth.Base) * 0.5f;
                    float hy = (NonNeg(i.SourceWidth.Rand) + i.SourceWidth.Base) * 0.5f;
                    float hz = (NonNeg(i.SourceHeight.Rand) + i.SourceHeight.Base) * 0.5f;
                    v = new UEVector(hx, hy, hz);
                }
                (gMin, gMax) = BoxTransform24(new UEVector(-v.x, -v.y, -v.z), v, ToLocal24(i.Location, i.Rotation));
            }
            gMin = new UEVector(gMin.x - half, gMin.y - half, gMin.z - half);
            gMax = new UEVector(gMax.x + half, gMax.y + half, gMax.z + half);
            if (!pValid) { pMin = gMin; pMax = gMax; }
            else
            {
                pMin = new UEVector(pMin.x > gMin.x ? gMin.x : pMin.x, pMin.y > gMin.y ? gMin.y : pMin.y, pMin.z > gMin.z ? gMin.z : pMin.z);
                pMax = new UEVector(!(pMax.x >= gMax.x) ? gMax.x : pMax.x, !(pMax.y >= gMax.y) ? gMax.y : pMax.y, !(pMax.z >= gMax.z) ? gMax.z : pMax.z);
            }
            float lifeBase = i.Lifetime.Base, lifeRand = i.Lifetime.Rand;
            if (steadyAtRest && !(NonNeg(lifeRand) + lifeBase > elapsed)) return BoxGetCoords(pMin, pMax);
            float life = NonNeg(lifeRand) + lifeBase;
            float span = age - elapsed;
            float t = span > life ? life : span;                                      // fcomp st1; test ah,0x41
            float speed = NonNeg(i.Speed.Rand) + i.Speed.Base;
            if (i.ChaosDelay > 0f) speed = speed + i.Chaos / i.ChaosDelay;
            float travel = speed * t;
            float endScale = NonNeg(NonNeg(i.SizeEndScale.Rand) + i.SizeEndScale.Base);
            float grow = endScale * half - 1f;
            float lifeMin = (lifeRand > 0f ? 0f : lifeRand) + lifeBase;                // test ah,0x41: Rand > 0 -> 0
            if (lifeMin > t) grow = (t / lifeMin) * grow;
            float r = (travel + grow) * 1.1f;
            pMin = new UEVector(pMin.x - r, pMin.y - r, pMin.z - r);
            pMax = new UEVector(pMax.x + r, pMax.y + r, pMax.z + r);
            if (!i.HasZone) throw new InvalidOperationException($"ParticleFX {who}: no Region.Zone and Level (GetRenderBoundingBox 0x103c179c)");
            UEVector zg = i.ZoneGravity, gr = i.Gravity;
            float gm = i.GravityModifier;
            float gx = gm * zg.x + gr.x, gy = gm * zg.y + gr.y, gz = gm * zg.z + gr.z;
            float k = (t * t) * 0.5f;
            float dx = (gx * k) * 1.1f, dy = (gy * k) * 1.1f, dz = (k * gz) * 1.1f;
            var a = new UEVector(pMin.x + dx, pMin.y + dy, pMin.z + dz);
            var b = new UEVector(pMax.x + dx, pMax.y + dy, pMax.z + dz);
            (pMin, pMax) = BoxAdd24(pMin, pMax, a);
            (pMin, pMax) = BoxAdd24(pMin, pMax, b);
            return BoxGetCoords(pMin, pMax);
        }

        // Range Rand without a clamp (the caller clamps by its own comparison rule).
        static float RangeRaw(UEObject o, string p, bool rand)
            => o.GetProp(p) is UEObject bag ? UEValue.AsFloat(bag.GetProp(rand ? PK.Rand : PK.Base)) : 0f;

        // FBox::GetCoords (Core.dll.c:54460): Origin = Min, axes = sides.
        static BrushCoords BoxGetCoords(UEVector min, UEVector max) => UERenderVisibility.BoxCoords(min, max);

        // FBox::operator+=(FVector) (Core 0x1011ba10) on a valid box: Min takes the point when Min > p; Max when Max < p
        // or unordered.
        static (UEVector, UEVector) BoxAdd24(UEVector min, UEVector max, UEVector p) => (
            new UEVector(min.x > p.x ? p.x : min.x, min.y > p.y ? p.y : min.y, min.z > p.z ? p.z : min.z),
            new UEVector(!(max.x >= p.x) ? p.x : max.x, !(max.y >= p.y) ? p.y : max.y, !(max.z >= p.z) ? p.z : max.z));

        // FBox::FBox(FCoords) (Core 0x1014f0c0): Min = Max = Origin; each component of each axis (X, Y, Z) is added
        // to Max when ≥ 0, otherwise (and for NaN) to Min.
        static (UEVector, UEVector) BoxFromCoords24(in BrushCoords c)
        {
            float nx = c.Origin.x, ny = c.Origin.y, nz = c.Origin.z, xx = nx, xy = ny, xz = nz;
            static void Add(float v, ref float mx, ref float mn) { if (v >= 0f) mx = v + mx; else mn = v + mn; }
            Add(c.X.x, ref xx, ref nx); Add(c.X.y, ref xy, ref ny); Add(c.X.z, ref xz, ref nz);
            Add(c.Y.x, ref xx, ref nx); Add(c.Y.y, ref xy, ref ny); Add(c.Y.z, ref xz, ref nz);
            Add(c.Z.x, ref xx, ref nx); Add(c.Z.y, ref xy, ref ny); Add(c.Z.z, ref xz, ref nz);
            return (new UEVector(nx, ny, nz), new UEVector(xx, xy, xz));
        }

        // FBox::FBox(FBox, FCoords) (Core 0x1014f170): axes — box sides along the C axes, origin — sum per the disasm, then
        // FBox(FCoords).
        static (UEVector, UEVector) BoxTransform24(UEVector min, UEVector max, in BrushCoords c)
        {
            float dz = max.z - min.z, dy = max.y - min.y, dx = max.x - min.x;
            // RU EA (UEEdition.RuOrder; RU Core 0x1014eaf2..0x1014eb09): Origin.x = ((min.z·Z.x + min.y·Y.x) + min.x·X.x) + O.x.
            var o = new UEVector(UEEdition.RuOrder ? ((min.z * c.Z.x + min.y * c.Y.x) + min.x * c.X.x) + c.Origin.x
                    : ((min.y * c.Y.x + min.x * c.X.x) + min.z * c.Z.x) + c.Origin.x,
                ((c.X.y * min.x + c.Z.y * min.z) + c.Y.y * min.y) + c.Origin.y,
                ((c.X.z * min.x + c.Z.z * min.z) + c.Y.z * min.y) + c.Origin.z);
            return BoxFromCoords24(new BrushCoords
            {
                Origin = o,
                X = new UEVector(dx * c.X.x, dx * c.X.y, dx * c.X.z),
                Y = new UEVector(dy * c.Y.x, dy * c.Y.y, dy * c.Y.z),
                Z = new UEVector(dz * c.Z.x, dz * c.Z.y, dz * c.Z.z),
            });
        }

        // AActor::ToLocal (Engine 0x1031bac0; vtable slot +0x98, called by GetRenderBoundingBox 0x103c13b5) in PC24:
        // unit axes, *= roll, pitch, yaw (FCoords::operator/=(FRotator) matrices, BrushCoords.DivRotator; product —
        // UEOcclusion.MulAssign24), then Origin += Location (0x1031bd15..0x1031bd56).
        static BrushCoords ToLocal24(UEVector l, UERotator r)
        {
            BrushCoords c = UEOcclusion.DivRotator24(BrushCoords.Identity, r.Pitch, r.Yaw, r.Roll);
            c.Origin = new UEVector(c.Origin.x + l.x, c.Origin.y + l.y, c.Origin.z + l.z);
            return c;
        }

        bool MeshSpriteSetup(UEObject a, UESceneNode f, out UERenderVisibility.ScreenBounds sb)
        {
            sb = default;
            if (UEInterpreter.ClassIsA(a.Class, "ParticleFX"))
                throw new NotSupportedException($"FDynamicSprite::Setup: mesh AParticleFX (bit +0x3c8, full screen) not ported actor={a.GetProp(PK.Name)}");
            if (MeshRenderBoundingBox is null) throw new InvalidOperationException("DirectRenderStep: mesh box (UELevelRuntime.MeshRenderBoundingBox) not bound to the producer");
            BrushCoords box = MeshRenderBoundingBox(a);
            float lx = box.X.y * box.X.y + box.X.z * box.X.z + box.X.x * box.X.x;
            if (lx == 0f) return false;
            SetupBoundVisibleResidue(box);
            return UERenderVisibility.BoundVisible(f, box, out sb) && sb.Y2 > sb.Y1;
        }

        // URender::DrawWorld by the direct path (Render.dll.c:2643-2660): master OccludeFrame (SetupDynamics, OccludeBsp,
        // then children's OccludeFrame, Render.dll.c:603-718), then DrawFrame — children before the parent (Render.dll.c:1011-1014),
        // frame sprites in DrawFrame order (UESpriteSort on a non-SpanBased device): mesh GetFrame and emitter Update(0). A MESH runs DrawActorSprite once per
        // DRAW — every frame whose sprite list holds it, with that frame's FDynamicSprite and FX (the DrawLodMesh LOD
        // request) — like the original: GetFrame is not idempotent across draws of one tick (ULodMesh vertex-cache count
        // and tween refill, ApplyAnim channel overlay and its tween, the weapon branch's writes). An emitter or shadow is
        // updated at most once per tick however many frames see it (a repeated Update(0) in the same tick gives dt = 0).
        // Sprites are not interleaved into surface list 2 (RenderDrawOrder): only the call order between emitters depends
        // on it (the render rand() stream). shadowsOn — _shadowBlockers is empty.
        List<UEObject> DirectRenderStep(UEObject player, UEInterpreter interp,
            int frameX, int frameY, float fov, IEnumerable<UEObject> actors, bool shadowsOn)
        {
            if (ParticleDensityNow is null) throw new InvalidOperationException("DirectRenderStep: Client.ParticleDensity not bound to the producer");
            if (DrawMesh is null) throw new InvalidOperationException("DirectRenderStep: mesh branch (UELevelRuntime.DrawMeshActor) not bound to the producer");
            if (MeshLodOf is null) throw new InvalidOperationException("DrawLodMesh LOD: mesh LOD data (UELevelRuntime) not bound to the producer");
            DrawnMeshes.Clear();
            _meshPoses = new List<MeshPose>();
            _spriteActors = new List<int>();
            _actorViews = new Dictionary<int, List<int>>();
            ParticleDraws.Clear();
            var meshDraws = new List<(int View, MeshPose Pose)>();
            DirectSprites = 0;
            int rendMap = UEValue.ToInt(player.GetProp(PK.RendMap));
            // DrawLodMesh identity gate: URender+0x3c ≠ 0 (console REND WIRE, not ported — D-76, see
            // UELevelRuntime.DrawMeshActor) or RendMap ∈ {13,14,15,1}.
            bool identity = rendMap is 13 or 14 or 15 or 1;
            DirectMeshes = DirectEmitters = 0;
            DirectChildEmitters = DirectChildMeshes = 0;
            UESceneNode master = OccludeWorld(player, interp, frameX, frameY, fov, actors, shadowsOn, sprites: true);
            var viewIndex = new Dictionary<UESceneNode, int>();
            _views = ProjectViews(master, _lastOcclusionWorld.SourceOf, viewIndex, _drawn = new List<DrawnWorld>(), _lastOcclusionWorld.PolyOf);
            var drawOrder = new List<int>[_views.Count + 1];
            _drawnTextures = DrawnTexturesOf(master, _lastOcclusionWorld);
            CollectSurfDynamicLights(master, _lastOcclusionWorld);
            var drawn = new List<UEObject>();
            var once = new HashSet<UEObject>();
            var weaponOf = new List<(int Weapon, int Pawn)>();
            WeaponPawns.Clear();
            IList<UEObject> levelActors = actors as IList<UEObject> ?? new List<UEObject>(actors);
            void DrawFrame(UESceneNode f)
            {
                foreach (UESceneNode c in f.Children) DrawFrame(c);
                // The frame's surfaces (and their decals, Render.dll.c:1455-1530) are drawn before its sprites.
                DrawDecals?.Invoke(f, viewIndex[f]);
                bool child = f.Parent is not null;
                // A device without SpanBased (D3DDrv) sorts the array by actor class and draws it in two passes
                // (UESpriteSort); a SpanBased one draws it in list order after pass 2 (no list-2 interleaving here).
                bool spanBased = _lastOcclusionWorld.SpanBased?.Invoke()
                    ?? throw new InvalidOperationException("DrawFrame: RenDev.SpanBased not bound to the occlusion world");
                IReadOnlyList<UESprite> order = spanBased ? f.Sprites : UESpriteSort.NonSpanBasedOrder(f.Sprites, x => x.Actor);
                List<int> seq = drawOrder[viewIndex[f] + 1] = new List<int>(order.Count);
                var inSeq = new HashSet<int>();
                void Drew(int id) { if (inSeq.Add(id)) seq.Add(id); }
                foreach (UESprite sp in order)
                {
                    UEObject a = sp.Actor;
                    // Every frame draws its own sprite list; the actor's membership in each frame that drew it.
                    if (!_actorViews.TryGetValue(a.ObjectId, out var inViews)) _actorViews[a.ObjectId] = inViews = new List<int>();
                    inViews.Add(viewIndex[f]);
                    int dt = UEValue.ToInt(a.GetProp(PK.DrawType));
                    bool mesh = dt is not (8 or 1 or 7);
                    bool firstDraw = once.Add(a);
                    if (!mesh) Drew(a.ObjectId);   // a mesh enters with its poses (the pawn, then its weapon)
                    // An emitter is drawn by every frame whose sprite list holds it: URender::DrawParticleSystem calls
                    // AParticleFX::Update(0) (a repeat in the same frame tree has dt = 0 and returns at once) and then its
                    // drawers, whose billboard pass writes the particles' Alpha (ParticleDrawerFade).
                    if (!mesh && !firstDraw && dt != 8) continue;
                    // An actor destroyed between Setup and DrawFrame (a shadow Update body, another actor's Setup, or its own
                    // emitter Update in an earlier frame of the tree). Shipped tests no bDeleteMe before the draw (DrawFrame
                    // 0x10b254f0, DrawActorSprite 0x10b32850): an emitter's Update returns false on its freed list and draws
                    // nothing (RenderStepFxBody). What a mesh or sprite draw reads from a destroyed actor is not established.
                    if (a.Destroyed && dt != 8)
                        throw new NotSupportedException($"DrawFrame: sprite of actor {a.GetProp(PK.Name)} destroyed after SetupDynamics is not ported");
                    if (dt == 8)
                    {
                        bool ok = RenderStepFx(a);
                        if (firstDraw) { drawn.Add(a); DirectEmitters++; if (child) DirectChildEmitters++; }
                        if (ok) ParticleDrawerFade(a, f, viewIndex[f]);
                    }
                    else if (dt is 1 or 7)
                    {
                        // Sprite DrawActorSprite (texture tile): the frontend draws it; the frame names what to draw.
                        _spriteActors.Add(a.ObjectId);
                        DirectSprites++;
                    }
                    else
                    {
                        // URender::DrawLodMesh LOD request of this draw (UEMeshLod.VertsRequested over this frame's
                        // FDynamicSprite and FX) for the actor whose Mesh is drawn — the pawn, or the third-person weapon
                        // during its swap (own mesh and LODBias, the pawn's sprite). Enhanced: no LOD (NOT ORIGINAL, D-62).
                        int Request(UEObject owner)
                        {
                            if (EnhancedVisuals) return -1;
                            var lod = MeshLodOf(owner.GetProp(PK.Mesh));
                            return UEMeshLod.VertsRequested(lod.Lod, lod.ModelVerts, lod.SpecialVerts, lod.Skeletal,
                                UEInterpreter.AsFloat(owner.GetProp(PK.LODBias)), sp.X1, sp.Y1, sp.X2, sp.Y2, f.FX);
                        }
                        var poses = new List<MeshPose>();
                        DrawMesh(a, identity, f.Coords, DrawnMeshes, poses, Request);
                        foreach (MeshPose p in poses)
                        {
                            Drew(p.ObjectId);
                            meshDraws.Add((viewIndex[f], p));
                            if (p.Weapon && !weaponOf.Contains((p.ObjectId, a.ObjectId))) weaponOf.Add((p.ObjectId, a.ObjectId));
                        }
                        if (once.Add(a)) { DirectMeshes++; if (child) DirectChildMeshes++; }
                    }
                }
                // Corona block at the end of every DrawFrame (Render.dll 0x10b26742..0x10b26e0f); UECoronas gates it.
                if (CoronaLineClear is null || CoronaActorsByExport is null || (EnhancedVisuals && CoronaPawnClear is null))
                    throw new InvalidOperationException("DrawFrame coronas: line check / export index not bound to the producer");
                CoronaTable.Step(f, player, levelActors, RenderDeviceCoronas, CoronaLevel, CoronaActorsByExport,
                    _viewportClock, CoronaClear);
            }
            DrawFrame(master);
            _coronas = CoronaTable.Draws.ToArray();
            // The third-person weapon is drawn by its pawn's DrawActorSprite: it is in exactly the pawn's frames.
            foreach (var (weapon, pawn) in weaponOf) { _actorViews[weapon] = new List<int>(_actorViews[pawn]); WeaponPawns[weapon] = pawn; }
            _actorViewSets = MeshDrawParts(meshDraws);
            _drawOrder = Array.ConvertAll(drawOrder, l => l?.ToArray() ?? Array.Empty<int>());
            return drawn;
        }

        // The per-draw GetFrame results (pose + the level the draw's face loop used, DrawMeshActor) merged into one
        // MeshPose per (actor, weapon): draws with the same level and the same points form one part (MeshPose.LodParts;
        // a part's own Points only when they differ from the first draw's). Every draw full-mesh with the same points —
        // no parts (null).
        ActorViewSets MeshDrawParts(List<(int View, MeshPose Pose)> draws)
        {
            var keys = new List<(int Id, bool Weapon)>();
            var byKey = new Dictionary<(int, bool), List<(int View, MeshPose Pose)>>();
            foreach (var d in draws)
            {
                var k = (d.Pose.ObjectId, d.Pose.Weapon);
                if (!byKey.TryGetValue(k, out var l)) { byKey[k] = l = new(); keys.Add(k); }
                l.Add(d);
            }
            var parts = new List<(int Pose, int Verts, UEVector[] Points, List<int> Views)>();
            foreach (var k in keys)
            {
                var ds = byKey[k];
                UEVector[] basePts = ds[0].Pose.Points;
                var groups = new List<(int Verts, UEVector[] Points, List<int> Views)>();
                foreach (var d in ds)
                {
                    int lv = d.Pose.LodParts[0].Verts;
                    int g = groups.FindIndex(x => x.Verts == lv && SamePoints(x.Points, d.Pose.Points));
                    if (g < 0) groups.Add((lv, d.Pose.Points, new List<int> { d.View })); else groups[g].Views.Add(d.View);
                }
                int pi = _meshPoses.Count;
                _meshPoses.Add(new MeshPose(k.Id, k.Weapon, ds[0].Pose.MeshPath, basePts));
                if (groups.Count == 1 && groups[0].Verts < 0) continue;
                foreach (var g in groups) parts.Add((pi, g.Verts, SamePoints(g.Points, basePts) ? null : g.Points, g.Views));
            }
            var sets = ActorViewSets.From(_actorViews, parts.ConvertAll(x => x.Views.ToArray()), out int[] setIdx);
            for (int j = 0; j < parts.Count;)
            {
                int pose = parts[j].Pose;
                var list = new List<MeshLodPart>();
                for (; j < parts.Count && parts[j].Pose == pose; j++) list.Add(new MeshLodPart(parts[j].Verts, setIdx[j], parts[j].Points));
                MeshPose p = _meshPoses[pose];
                _meshPoses[pose] = new MeshPose(p.ObjectId, p.Weapon, p.MeshPath, p.Points, list);
            }
            return sets;
        }

        static bool SamePoints(UEVector[] a, UEVector[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (BitConverter.SingleToInt32Bits(a[i].x) != BitConverter.SingleToInt32Bits(b[i].x)
                    || BitConverter.SingleToInt32Bits(a[i].y) != BitConverter.SingleToInt32Bits(b[i].y)
                    || BitConverter.SingleToInt32Bits(a[i].z) != BitConverter.SingleToInt32Bits(b[i].z)) return false;
            return true;
        }
        UESceneNode OccludeWorld(UEObject player, UEInterpreter interp, int frameX, int frameY, float fov,
            IEnumerable<UEObject> actors, bool shadowsOn, bool sprites)
        {
            if (UpdateMovingBrush is null) throw new InvalidOperationException("SetupDynamics: brush tracker (UELevelRuntime.UpdateBrushTracker) not bound to the producer");
            if (OcclusionWorldFor is null) throw new InvalidOperationException("OccludeBsp: traversal world (UELevelRuntime.OcclusionWorld) not bound to the producer");
            if (sprites && ParticleDensityNow is null) throw new InvalidOperationException("DirectRenderStep: Client.ParticleDensity not bound to the producer");
            IList<UEObject> list = actors as IList<UEObject> ?? new List<UEObject>(actors);
            int rendMap = UEValue.ToInt(player.GetProp(PK.RendMap));
            // URender::DrawWorld (0x10b27ba0): DAT_10b719b4++ before OccludeFrame.
            Occlusion.DrawWorldCounter++;
            _frameBanks.BeginTree(Occlusion.DrawWorldCounter);   // allocation only: the previous tree stays intact
            UESceneNode master = UESceneNode.CreateMaster(frameX, frameY, _cameraCache.Location, _cameraCache.Rotation, fov, rendMap,
                _frameBanks);
            UEOcclusionWorld w = OcclusionWorldFor(interp, player);
            master.Level = w.Level;
            // CreateMasterFrame (Render.dll.c:8113-8118): ZoneNumber = UModel::PointRegion(LevelInfo, Coords.Origin).
            master.Zone = w.Bsp is null ? 0 : w.Bsp.FindRegion(master.Coords.Origin).ZoneNumber & 0xff;
            LastMasterFrame = master;
            _lastOcclusionWorld = w;
            bool behindView = UEValue.ToBool(player.GetProp(PK.bBehindView));
            UEObject viewTarget = player.GetProp(PK.ViewTarget) as UEObject;
            bool particlesOn = sprites && rendMap is not (13 or 14 or 15) && ParticleDensityNow() != 0;
            DirectMovers = 0;
            int showFlags = UEValue.ToInt(player.GetProp(PK.ShowFlags));
            int nodeCount = w.Bsp?.LiveNodeCount ?? 0;
            var shadowed = new HashSet<UEObject>();
            UESceneNode current = master;
            void Shadow(UEObject a)
            {
                if (!shadowsOn || shadowed.Contains(a)) return;
                if (a.GetDeclared("Actor", "Shadow") is not UEObject shadow || shadow.Destroyed) return;
                shadowed.Add(a);
                ShadowUpdateEntered?.Invoke(a);
                UELifecycle.CallEvent(interp, shadow, "Update", null);
                ShadowUpdateCount++;
                if (current.Parent is not null) ChildShadowUpdates++;
            }

            // Emitter FDynamicSprite::Setup (Render thunk_FUN_10b30c70) in frame f: the AParticleFX::GetRenderBoundingBox box
            // (vtable +0xa0, call 0x10b30d61; on a shift — Update(0) regardless of the frustum: the Shutdown budget -> actor
            // Destroy depends on the step count, pilot J15), reject when |X|² = 0 (order y² + z² + x²), frame BoundVisible.
            bool EmitterSetup(UEObject a, UESceneNode f, out UERenderVisibility.ScreenBounds sb)
            {
                sb = default;
                BrushCoords box = ParticleRenderBoundingBox(a);
                float lx = box.X.y * box.X.y + box.X.z * box.X.z + box.X.x * box.X.x;
                if (lx == 0f) return false;
                SetupBoundVisibleResidue(box);
                return UERenderVisibility.BoundVisible(f, box, out sb) && sb.Y2 > sb.Y1;
            }
            bool IsEmitter(UEObject a, int dt) => dt == 8 && particlesOn && UEInterpreter.ClassIsA(a.Class, "ParticleFX");
            bool IsMesh(UEObject a, int dt) => sprites && dt == 2 && a.GetProp(PK.Mesh) is not null && (showFlags & 0x20) == 0;
            // DT_Sprite (1) and DT_SpriteAnimOnce (7) — the sprite branch of FDynamicSprite::Setup (UERenderVisibility.SpriteSetup).
            bool IsSprite(int dt) => sprites && dt is 1 or 7;
            bool SpriteSetup(UEObject a, UESceneNode f, out UERenderVisibility.ScreenBounds sb)
            {
                string tex = WorldFrameProjector.AssetPath(a.GetProp(PK.Texture));
                bool has = !string.IsNullOrEmpty(tex) && tex != "None";
                int us = 0, vs = 0;
                if (has)
                {
                    (us, vs) = SpriteTextureSize(tex)
                        ?? throw new NotSupportedException($"FDynamicSprite::Setup: texture size of {tex} for sprite {a.GetProp(PK.Name)} not read");
                }
                return UERenderVisibility.SpriteSetup(f, UEValue.AsVector3(a.GetProp(PK.Location)), UEValue.AsFloat(a.GetProp(PK.DrawScale)),
                    UEValue.AsFloat(a.GetProp(PK.SpriteProjForward)), us, vs, has, showFlags, rendMap, out sb);
            }

            void OccludeFrame(UESceneNode f)
            {
                current = f;
                // OccludeFrame (Render.dll.c:676-682): Exclude — ViewTarget ?? Actor for a master frame without bBehindView.
                UEObject exclude = f.Parent is null && !behindView ? (viewTarget ?? player) : null;
                UERenderVisibility.SetupDynamicsPass(f, list, player, exclude, HighDetailActors, rendMap,
                    a =>
                    {
                        int dt = UEValue.ToInt(a.GetProp(PK.DrawType));
                        UERenderVisibility.ScreenBounds sb;
                        // FDynamicSprite::Setup (Render 0x10b30d56-0x10b30d94): the GetRenderBoundingBox(1) box BEFORE
                        // BoundVisible — for a skeletal mesh ApplyAnim(rootOnly) runs inside (bone 0 and the root block
                        // are written even for an actor outside the frustum); a degenerate box (|X axis|² = 0) is not drawn.
                        if (IsEmitter(a, dt)) { if (EmitterSetup(a, f, out sb)) Occlusion.CreateSprite(f, a, sb, nodeCount); }
                        else if (IsMesh(a, dt)) { if (MeshSpriteSetup(a, f, out sb)) Occlusion.CreateSprite(f, a, sb, nodeCount); }
                        else if (IsSprite(dt)) { if (SpriteSetup(a, f, out sb)) Occlusion.CreateSprite(f, a, sb, nodeCount); }
                    },
                    Shadow, f.Parent is null ? a => { if (UpdateMovingBrush(a)) DirectMovers++; } : null, f.Recursion,
                    a => Occlusion.SetupLight(f, a, nodeCount));
                Occlusion.OccludeBsp(f, w);
                Occlusion.ClearCache();
                foreach (UESceneNode c in f.Children) { DirectChildFrames++; OccludeFrame(c); }
            }
            // OccludeBsp iStack_a8: SupportsFogMaps (D3DDrv: 1) && VolumetricLighting && Viewport->Actor's zone bFogZone.
            UEObject viewerZone = (player?.GetDeclared("Actor", "Region") as UEObject)?.GetProp(PK.Zone) as UEObject;
            Occlusion.LeafVolumetricLighting = RenderDeviceVolumetricLighting && viewerZone is not null
                && UEValue.ToBool(viewerZone.GetProp(PK.bFogZone));
            OccludeFrame(master);
            return master;
        }
    }
}
