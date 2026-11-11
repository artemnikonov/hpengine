// Binding a decal to a world surface — Core-owned.
//
// Decal-to-world-surface binding: the actor shadow finds its floor through the shipped
// surface path below, not by its own ray against collision hulls (a hull is the convex
// shell of a CSG leaf; its plane may match no visible polygon).
//
// WHAT SHIPPED DOES (ADecal::execAttachDecal, body 0x10410770; read from the
// DISASSEMBLY, not the pseudocode — the pseudocode confuses stack slots here):
//
//   1. `XAxis = (GMath.UnitCoords / Rotation).XAxis`, the ray is
//      Start = Location, End = Location + (-XAxis)*Distance (0x10410a30-a7b);
//   2. `UModel::LineCheck(Result, NULL, End, Start, Extent=0, ExtraNodeFlags=6)`
//      — a POLYGON trace over the model (vtable +0x5c), `push 6` at 0x10410a7b;
//   3. reject if the trace missed or `Item == -1`;
//   4. node surface `surf1 = Surfs[Nodes[Item].iSurf]`, and the point is
//      RECOMPUTED as the intersection of the ray with that surface's PLANE
//      (0x10410b45-bd5) — shipped does not use the LineCheck result (its
//      Location/Time) AT ALL;
//   5. `iSurf = UModel::FindCoplanarSurface(Item, Point1, 0)` (0x10410bfa);
//      reject on -1;
//   6. the point is recomputed again, with the plane of `surf2 = Surfs[iSurf]`
//      (0x10410c82-ce8);
//   7. reject if `(XAxis | N2) == 0.0` (0x10410d47);
//   8. reject if `|(Base2 - Point2) | N2| > 0.001` (0x10410dbc);
//   9. reject if `(surf2.PolyFlags & 0x600) != 0` (`test ah, 6` at
//      0x10410dd0).
//
// The LineCheck tail pulling the point back by 0.5 units does not matter: the pulled
// point never enters the decision, since shipped rebuilds the point from the ray-plane
// intersection. All gates, including 5 and 8, are ported below.
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        // PF_AutoUPan (0x200) | PF_AutoVPan (0x400) — literal 0x600 from
        // ADecal::execAttachDecal (`test ah, 6` at 0x10410dd0).
        //
        // The name is PROVEN BY HP1's OWN DATA, not taken from SurrealEngine. The
        // PolyFlags word is the bool properties of class Engine.Texture in
        // declaration order (the same layout is pinned in UETextureFacts.PolyBits),
        // and `--dumpprops Texture` prints them: bInvisible(0), bMasked(1),
        // bTransparent(2), bNotSolid(3), bEnvironment(4), bSemisolid(5),
        // bModulate(6), bFakeBackdrop(7), bTwoSided(8), bAutoUPan(9),
        // bAutoVPan(10). So 0x600 is the TWO texture auto-panning bits,
        // not PF_Invisible|PF_FakeBackdrop (0x1|0x80). The meaning fits: a decal
        // cannot stick to a surface whose texture moves by itself.
        public const int PfDecalReject = 0x600;

        // ExtraNodeFlags of the call: NF_ShootThrough (0x2) | NF_NotVisBlocking (0x4).
        public const int DecalExtraNodeFlags = 6;

        public readonly struct DecalSurfaceHit
        {
            public bool Found { get; }
            public Vector3 Location { get; }
            public Vector3 Normal { get; }
            public int PolyFlags { get; }
            public int Node { get; }
            // The surface CHOSEN by FindCoplanarSurface. Not the same as
            // Surfs[Nodes[Node].iSurf]: the chain of coplanar nodes can lead to a
            // neighbouring node of the same plane, and shipped reads PolyFlags from
            // THIS surface.
            public int Surf { get; }
            public string Reject { get; }

            public DecalSurfaceHit(bool Found, Vector3 Location, Vector3 Normal,
                int PolyFlags, int Node, int Surf, string Reject)
            {
                this.Found = Found; this.Location = Location; this.Normal = Normal;
                this.PolyFlags = PolyFlags; this.Node = Node; this.Surf = Surf;
                this.Reject = Reject;
            }

            public static DecalSurfaceHit None(string why)
                => new DecalSurfaceHit(false, Vector3.zero, Vector3.zero, 0, -1, -1, why);
        }

        // THE REJECT REASON IS RETURNED, NOT LOST: a shadow has several distinct
        // "not drawn" causes that look identical from outside.
        //
        // THE ARGUMENTS ARE THE SAME THREE AS SHIPPED: decal location, its rotation
        // axis and the trace distance. A (start, end) pair would be one quantity
        // instead of two, and gate 7 compares EXACTLY the axis — recovering it from
        // the ray ends would substitute arithmetic for the source.
        public DecalSurfaceHit TraceDecalSurface(Vector3 location, Vector3 axis, float distance)
        {
            if (Bsp is null) return DecalSurfaceHit.None("bsp_not_loaded");

            // End = Location + (-XAxis)*Distance (0x10410a30-a7b).
            Vector3 end = new Vector3(location.x + -axis.x * distance,
                                      location.y + -axis.y * distance,
                                      location.z + -axis.z * distance);
            BspHit hit = Bsp.TraceWorldLine(location, end, null, DecalExtraNodeFlags);
            if (!hit.Blocked) return DecalSurfaceHit.None("no_world_hit");
            if (hit.Node < 0) return DecalSurfaceHit.None("no_bsp_node");

            Vector3 first = SurfacePlanePoint(Bsp.NodeSurf(hit.Node), location, end);
            int surf = Bsp.FindCoplanarSurface(hit.Node, first);
            if (surf < 0) return DecalSurfaceHit.None("no_coplanar_polygon_under_point");

            BspSurfRecord record = Bsp.Surf(surf);
            Vector3 normal = Bsp.Vector(record.VNormal);
            Vector3 point = SurfacePlanePoint(surf, location, end);

            // Gate 7. The dot product here is INLINED BY THE COMPILER in
            // x, z, y order (0x10410d29-d45) — not via FVector::operator|,
            // whose order differs.
            if (InlineDot(axis, normal) == 0.0) return DecalSurfaceHit.None("axis_parallel_to_surface");

            // Gate 8. The difference is computed per component and STORED to float32
            // by the FVector constructor (0x10410d5e-d81), and only then reduced
            // in the same x, z, y order.
            Vector3 basePoint = Bsp.Point(record.PBase);
            var offset = new Vector3(basePoint.x - point.x, basePoint.y - point.y,
                                     basePoint.z - point.z);
            double flat = InlineDot(offset, normal);
            if (flat < 0.0) flat = -flat;
            if (flat > 0.001f) return DecalSurfaceHit.None("point_off_surface_plane");

            if ((record.PolyFlags & PfDecalReject) != 0)
                return DecalSurfaceHit.None("surface_autopan");

            // The normal comes from the trace, not record.VNormal: the iPlane chain
            // nodes are COPLANAR, i.e. they share one plane, and the trace has already
            // flipped it against the ray direction. The consumer needs the flipped one.
            return new DecalSurfaceHit(true, point, hit.Normal, record.PolyFlags,
                hit.Node, surf, null);
        }

        // Intersection of the ray Location->End with the surface PLANE (0x10410b45-bd5
        // and 0x10410c82-ce8 — two identical pieces). Each step goes through
        // Core.dll FVector operators, which store the result to float32:
        // differences per component (0x10119230), the scalar in extended precision, rounded
        // once on store (0x10119190, order x + (y + z)).
        private Vector3 SurfacePlanePoint(int surf, Vector3 location, Vector3 end)
        {
            BspSurfRecord record = Bsp.Surf(surf);
            Vector3 normal = Bsp.Vector(record.VNormal);
            Vector3 basePoint = Bsp.Point(record.PBase);

            var toBase = new Vector3(basePoint.x - location.x, basePoint.y - location.y,
                                     basePoint.z - location.z);
            var along = new Vector3(end.x - location.x, end.y - location.y,
                                    end.z - location.z);
            float numerator = (float)VectorDot(toBase, normal);
            float denominator = (float)VectorDot(along, normal);
            float scale = numerator / denominator;
            return new Vector3(location.x + along.x * scale,
                               location.y + along.y * scale,
                               location.z + along.z * scale);
        }

        // FVector::operator| (Core.dll 0x10119190): x*x + (y*y + z*z).
        private static double VectorDot(Vector3 a, Vector3 b)
            => (double)a.x * b.x + ((double)a.y * b.y + (double)a.z * b.z);

        // The reduction the compiler built INLINE in execAttachDecal:
        // (x*x + z*z) + y*y (0x10410d29-d45 and 0x10410d92-dab).
        private static double InlineDot(Vector3 a, Vector3 b)
            => ((double)a.x * b.x + (double)a.z * b.z) + (double)a.y * b.y;
    }
}
