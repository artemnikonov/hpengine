// HP2 LUMOS SURFACES — URender::DrawFrame's HP2 lines at the DrawComplexSurface call, owned by Core.
//
// HP2's D3DDrv DrawComplexSurface takes two more arguments than HP1's: a DWORD PolyFlags (used where HP1 read
// FSurfaceInfo.PolyFlags) and a BYTE alpha (the multitexture pass's diffuse alpha, HP1: 0xff)
// (?DrawComplexSurface@UD3DRenderDevice@@UAEXPAUFSceneNode@@AAUFSurfaceInfo@@AAUFSurfaceFacet@@KE@Z). DrawFrame passes
// FSurfaceInfo.PolyFlags and 0xff for a draw record [HP2 0x10b260a1..0x10b260c7], unless the record's surface
// (Model->Surfs[Nodes[Draw->iNode].iSurf].PolyFlags, the live word OccludeBsp's Lumos pass marks with 0x40000 when
// revealed) has a bit of 0x800040 and !GIsEditor [HP2 0x10b25da1..0x10b25dd4]. Then (ebx = that surface word, t2 = ebx &
// 0x200000, a = t2 ? 0xff : 0):
//   * Viewport->Actor not IsA APlayerPawn: the record is not drawn [0x10b25dda..0x10b25dff].
//   * SpanBased device: revealed -> ((ebx | 4) & 0xff5bffbf, a); else !bLumosOn && t2 -> not drawn; else
//     (ebx & 0xff5bffbf, a) [0x10b25e2e..0x10b25e73].
//   * otherwise (D3DDrv): not revealed -> (t2 ? ebx : ebx | 0x80000000, a) [0x10b2608b..0x10b2609f]; revealed -> the
//     smallest fast-sqrt distance from the record's FIRST FSavedPoly's points (Facet.Polys, no Next walk), back to world
//     through Frame->Uncoords, to the viewport actor's Location (start 100000.0), r = that / (fLumosRadius * 0.8),
//     rc = r unless its bits exceed 1.0's as unsigned (negative, NaN, > 1) -> 1.0; flags = ebx & 0x7fffffff;
//     t2: alpha = max(_ftol(rc * 255), 100); else a' = _ftol(255 - rc * 255): > 200 -> 200, 0 -> alpha 0 and
//     flags | 0x80000000, else a' [0x10b25e84..0x10b26095]. The render window is x87 PC24: every operation is float.
// The record: OccludeBsp merges a surface's FSavedPolys into one record per (surface, zone actor) when the record's
// PolyFlags & 0x4010047 == 0 (Render.dll.c:6455-6472, head insertion of each merged poly), so the record's first poly is
// the frame's last registered poly of that (surface, zone actor); otherwise every poly is its own record.
using System;
using System.Collections.Generic;
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    public static class UELumosSurfaces
    {
        public const int CarrierMask = 0x800040, Revealed = 0x40000, Fades = 0x200000;

        public static bool IsCarrier(int surfPolyFlags) => (surfPolyFlags & CarrierMask) != 0;

        // Original: Render.dll ?DrawFrame@URender@@QAEXPAUFSceneNode@@@Z [HP2 0x10B24F30] (lines 0x10b25da1..0x10b260ca)
        // The DrawComplexSurface arguments of a carrier record: surf — the surface's live PolyFlags; head — the record's first
        // FSavedPoly's points (camera space), null when there is none. Drawn false: DrawFrame skips the record.
        public static (bool Drawn, int PolyFlags, int Alpha) Args(int surf, bool spanBased, bool viewerIsPlayerPawn, bool lumosOn,
            float lumosRadius, Vector3 viewerLocation, in BrushCoords uncoords, Vector3[] head)
        {
            if (!viewerIsPlayerPawn) return (false, 0, 0);
            int t2 = surf & Fades;
            int a = t2 != 0 ? 0xff : 0;
            if (spanBased)
            {
                if ((surf & Revealed) != 0) return (true, (surf | 4) & unchecked((int)0xff5bffbf), a);
                if (!lumosOn && t2 != 0) return (false, 0, 0);
                return (true, surf & unchecked((int)0xff5bffbf), a);
            }
            if ((surf & Revealed) == 0) return (true, t2 != 0 ? surf : surf | int.MinValue, a);
            if (head is null) return (false, 0, 0);
            float min = 100000f;                                         // 0x47c34f80
            foreach (Vector3 p in head)
            {
                // inline TransformPointBy(Frame->Uncoords): d = P - Origin, ((d.x·X.x + d.y·X.y) + d.z·X.z) per axis
                float dx = p.x - uncoords.Origin.x, dy = p.y - uncoords.Origin.y, dz = p.z - uncoords.Origin.z;
                float wx = (dx * uncoords.X.x + dy * uncoords.X.y) + dz * uncoords.X.z;
                float wy = (dx * uncoords.Y.x + dy * uncoords.Y.y) + dz * uncoords.Y.z;
                float wz = (dx * uncoords.Z.x + dy * uncoords.Z.y) + dz * uncoords.Z.z;
                float ex = wx - viewerLocation.x, ey = wy - viewerLocation.y, ez = wz - viewerLocation.z;
                float d = FogMap.FastSqrt((ex * ex + ez * ez) + ey * ey);
                if (!(d >= min)) min = d;                                 // fcomp; test ah,1 (C0: less or unordered)
            }
            float r = min / (lumosRadius * 0.8f);
            float rc = (uint)BitConverter.SingleToInt32Bits(r) > 0x3f800000u ? 1f : r;   // cmp 1.0 bits, r bits; jae
            int flags = surf & int.MaxValue;
            if (t2 != 0)
            {
                int v = (int)(rc * 255f);                                // _ftol
                return (true, flags, v < 100 ? 100 : v);
            }
            int w = (int)(255f - rc * 255f);
            if (w > 200) return (true, flags, 200);
            if (w != 0) return (true, flags, w);
            return (true, flags | int.MinValue, 0);
        }

        // The draw record each of a frame's FSavedPolys (UESceneNode.DrawnPolys, registration order) belongs to, as the index
        // of the record's first poly (the head of its Facet.Polys list): OccludeBsp merges a poly into the frame's record of
        // the same surface and zone actor when the record's PolyFlags & 0x4010047 == 0 (Render.dll.c:6455-6472, the poly
        // inserted at the head), so that head is the last registered poly of the (surface, zone actor); otherwise a poly
        // is its own record. zoneActor(node, side): Model->Zones[iZone[side]].ZoneActor, or Level->Actors[0] when none;
        // recordFlags(surf): the draw record's PolyFlags (surface | texture, masked by ShowFlags).
        public static int[] RecordHeads(UESceneNode f, Func<int, int> recordFlags, Func<int, int, object> zoneActor)
        {
            var polys = f.DrawnPolys;
            var heads = new int[polys.Count];
            var last = new Dictionary<(int, object), int>();
            for (int i = polys.Count - 1; i >= 0; i--)
            {
                var (s, node, _, back) = polys[i];
                if ((recordFlags(s) & 0x4010047) != 0) { heads[i] = i; continue; }
                var key = (s, zoneActor(node, back ? 0 : 1));
                if (!last.TryGetValue(key, out int h)) last[key] = h = i;    // walking backwards: the first seen is the last registered
                heads[i] = h;
            }
            return heads;
        }

        // Per FSavedPoly of a frame (same index): whether its surface is a carrier and, if so, its record's arguments.
        public static (bool Carrier, bool Drawn, int PolyFlags, int Alpha)[] Frame(UESceneNode f, Func<int, int> surfFlags,
            Func<int, int> recordFlags, Func<int, int, object> zoneActor, bool spanBased, bool viewerIsPlayerPawn, bool lumosOn,
            float lumosRadius, Vector3 viewerLocation)
        {
            var polys = f.DrawnPolys;
            var o = new (bool, bool, int, int)[polys.Count];
            int[] heads = null;
            for (int i = 0; i < polys.Count; i++)
            {
                int sf = surfFlags(polys[i].Surf);
                if (!IsCarrier(sf)) continue;
                heads ??= RecordHeads(f, recordFlags, zoneActor);
                var (drawn, pf, alpha) = Args(sf, spanBased, viewerIsPlayerPawn, lumosOn, lumosRadius, viewerLocation, f.Uncoords,
                    polys[heads[i]].View);
                o[i] = (true, drawn, pf, alpha);
            }
            return o;
        }
    }
}
