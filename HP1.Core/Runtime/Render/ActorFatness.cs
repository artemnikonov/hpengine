// Actor.Fatness — offsets a mesh vertex along its normal: Fatness == 128 ? 0 : Fatness·0.0625 − 8, applied to the
// world position BEFORE lighting and clipping. Shipped: Render.dll.c:9328, 14851; 0.0625 and 8.0 are .rdata
// 0x10b387c0/0x10b38644 (WorldFrame.cs, ActorSnapshot.Fatness). The offset is in UE units (the former reference
// port's extra 0.02 factor converted to Unity metres and does not apply here). Core's body (moved from the frontend
// 2026-10-29, review plan A2); the frontend calls it per vertex like UEActorLighting.LightPoints.
using System;

namespace HP1.UEScript
{
    public static class ActorFatness
    {
        // Offset in UE units; 128 means no offset.
        public static float Offset(int fatness) => fatness == 128 ? 0f : fatness * 0.0625f - 8f;

        // posWS += N·fat, N is the normalized vertex normal. normals is the same normal that feeds
        // vertex lighting (ActorBuilder: the point's unit normal or zero). Returns the offset.
        public static float Apply(Span<HP1.UEScript.UEVector> points, ReadOnlySpan<HP1.UEScript.UEVector> normals, int fatness)
        {
            if (normals.Length < points.Length) throw new ArgumentException($"{normals.Length} normals for {points.Length} vertices");
            float d = Offset(fatness);
            if (d == 0f) return 0f;
            for (int i = 0; i < points.Length; i++)
            {
                var n = normals[i];
                points[i] = new HP1.UEScript.UEVector(points[i].x + n.x * d, points[i].y + n.y * d, points[i].z + n.z * d);
            }
            return d;
        }
    }
}
