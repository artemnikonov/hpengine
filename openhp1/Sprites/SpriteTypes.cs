// Shared sprite-subsystem types: vertex, blend mode, draw, refusal report.
//
// Pure C# in UE world space (X forward, Y right, Z up). No GPU calls:
// builders emit vertex lists, the renderer decides how to fill them.
using System;
using System.Collections.Generic;

namespace OpenHP1.Sprites
{
    // Sprite/particle/shadow vertex. Position is UE world space (screen pixels for coronas,
    // see SpriteDraw.ScreenSpace). UV are texture fractions, V grows DOWN: (0,0) is the
    // top-left texel, matching UE texture rows.
    // RGBA are UE gamma values 0..1, NOT converted to linear space (the pow(·,2.2) and ×2.2974 of the
    // former Unity frontend were corrections for its linear project, not original behaviour).
    public struct SpriteVertex
    {
        public float X, Y, Z, U, V, R, G, B, A;
        public SpriteVertex(float x, float y, float z, float u, float v, float r, float g, float b, float a)
        { X = x; Y = y; Z = z; U = u; V = v; R = r; G = g; B = b; A = a; }
    }

    // Blend modes — SAME names and order as actor meshes
    // (OpenHP1.ActorBlend in SdlActors.cs:24), so the renderer uses the same pipeline:
    //   Opaque      — ONE, ZERO; depth write;
    //   Masked      — same + alpha cutoff < 0.5;
    //   Translucent — ONE, 1−SRC_COLOR, no depth write;
    //   Modulated   — DST_COLOR, SRC_COLOR (2·src·dst), no depth write.
    // None cull faces. Coronas additionally need the depth test DISABLED and screen
    // coordinates — see SpriteDraw.ScreenSpace.
    public enum SpriteBlend { Opaque, Masked, Translucent, Modulated }

    public enum SpriteKind { Sprite, Particle, Corona, Decal }

    // One draw: texture (full UE path), mode, vertices and triangles.
    public sealed class SpriteDraw
    {
        public SpriteKind Kind;
        public int ActorId;               // ActorSnapshot.ObjectId (sprite, emitter, lamp, decal actor)
        public string Texture;            // full UE path
        public SpriteBlend Blend;
        // true for coronas only: vertex X/Y in screen PIXELS (Y down), Z = 0;
        // drawn after the world and before the HUD, without depth test.
        public bool ScreenSpace;
        // For frame grouping (RenderRules in the renderer): Core's DrawFrame pass of the sprite or emitter actor
        // (ActorSnapshot.LatePass); for a decal, Surf — the level model surface of its FDecal record.
        public bool LatePass;
        public int Surf = -1;
        public int SurfPolyFlags, SurfTexturePolyFlags;   // a decal's surface flags from Core (DecalInstance)
        // View that draws the decal piece: WorldFrame.Views index, −1 = master; AnyView = all views.
        public const int AnyView = int.MinValue;
        public int FrameView = AnyView;
        public bool NoSmooth;   // sprite actor PF_NoSmooth (bNoSmooth; particles do not get the actor flag)
        public readonly List<SpriteVertex> Vertices = new();
        public readonly List<int> Indices = new();
    }

    // Loud accounting: every refusal is counted by reason name.
    // An empty report means no refusals in the frame.
    public sealed class SpriteReport
    {
        public readonly SortedDictionary<string, int> Counts = new(StringComparer.Ordinal);
        public void Add(string reason) { Counts.TryGetValue(reason, out int n); Counts[reason] = n + 1; }
        public int Get(string reason) => Counts.TryGetValue(reason, out int n) ? n : 0;
        public void Clear() => Counts.Clear();
        public override string ToString()
        {
            var parts = new List<string>();
            foreach (var kv in Counts) parts.Add(kv.Key + "=" + kv.Value);
            return string.Join(" ", parts);
        }
    }

    // Texture size: USize/VSize of WHAT is drawn (in game UETextureMetrics.TryGet; the logical
    // size of mip 0). false means the texture is unresolved.
    public delegate bool TextureSizeFn(string path, out int uSize, out int vSize);


    static class SpriteMath
    {
        // WORLD batches only (SdlRenderer level batches, merged per material: their surface order is already lost; the
        // world groups of the dynamic list — movers, decals — in SdlRenderer.SetActors): opaque first, blended after,
        // without reordering within groups. Actor draws never go through it — they are drawn in Core's DrawActorSprite
        // order (InCoreOrder; runtime_poc/verify_architecture.py locks it).
        public static void OpaqueFirst<T>(List<T> items, Func<T, bool> blended, List<T> scratch)
        {
            scratch.Clear();
            int w = 0;
            for (int i = 0; i < items.Count; i++) if (blended(items[i])) scratch.Add(items[i]); else items[w++] = items[i];
            for (int i = 0; i < scratch.Count; i++) items[w++] = scratch[i];
        }

        // The frame's dynamic draws in Core's DrawActorSprite order: draws of the actor groups (isActor: RenderRules.Actors /
        // LateActors) per frame of the tree in WorldFrame.DrawOrder sequence, each actor's own draws in build order; the
        // other draws (movers, decals — their surface pass) first, in their own order. The frontend decides no actor order:
        // an actor draw without a place in DrawOrder is a broken Core contract (loud, §4).
        // Per-frame storage is reused (OrderStore<T>): no allocation once the pool has grown. Frame thread only.
        public static void InCoreOrder<T>(IReadOnlyList<T> draws, HP1.UEScript.WorldFrame f, Func<T, bool> isActor,
            Func<T, int> actorId, Func<T, int> frameView, List<T> into)
        {
            into.Clear();
            var byActor = OrderStore<T>.ByActor;
            OrderStore<T>.Release();   // a previous call that threw leaves its lists here
            foreach (T d in draws)
            {
                if (!isActor(d)) { into.Add(d); continue; }
                var k = (frameView(d), actorId(d));
                if (!byActor.TryGetValue(k, out var l)) byActor[k] = l = OrderStore<T>.Take();
                l.Add(d);
            }
            if (byActor.Count == 0) return;
            IReadOnlyList<int[]> order = f.DrawOrder
                ?? throw new InvalidOperationException("actor draws without WorldFrame.DrawOrder (no Core DrawFrame this tick)");
            for (int v = 0; v < order.Count; v++)
                foreach (int id in order[v])
                    if (byActor.Remove((v - 1, id), out var l)) { into.AddRange(l); OrderStore<T>.Give(l); }
            foreach (var k in byActor.Keys)
                throw new InvalidOperationException($"actor {k.Id} drawn in frame {k.View} without a place in WorldFrame.DrawOrder");
        }

        static class OrderStore<T>
        {
            public static readonly Dictionary<(int View, int Id), List<T>> ByActor = new();
            static readonly Stack<List<T>> Free = new();
            public static List<T> Take() => Free.Count > 0 ? Free.Pop() : new List<T>();
            public static void Give(List<T> l) { l.Clear(); Free.Push(l); }
            public static void Release() { foreach (var l in ByActor.Values) Give(l); ByActor.Clear(); }
        }

        // Actor style -> PolyFlags: FUN_10b338f0 (sprites, Render.dll.c:39776-39785) and FUN_10b15090 (particles,
        // :24688-24697) — 2 -> PF_Masked, 3 -> PF_Translucent, 4 -> PF_Modulated, others -> 0. Both paths OR in
        // Texture.PolyFlags (+0x80): sprite — DrawActorSprite disasm 0x10b32b83-0x10b32b8e, particle — :24697.
        public const int PF_Masked = 0x2, PF_Translucent = 0x4, PF_Modulated = 0x40;
        public static int StyleFlags(int style) => style == 2 ? PF_Masked : style == 3 ? PF_Translucent : style == 4 ? PF_Modulated : 0;

        // PolyFlags -> D3DDrv SetBlending (D3DDrv.dll.c:788-830): Translucent wins over Modulated; the mask
        // (alpha test) is dropped under Translucent. Modulated keeps the alpha test with a mask — SpriteBlend has no
        // such pair (no carriers among campaign sprites/particles).
        public static SpriteBlend BlendOfFlags(int pf)
            => (pf & PF_Translucent) != 0 ? SpriteBlend.Translucent
             : (pf & PF_Modulated) != 0 ? SpriteBlend.Modulated
             : (pf & PF_Masked) != 0 ? SpriteBlend.Masked : SpriteBlend.Opaque;

        // Vertex colour under PF_Modulated is white: D3DDrv DrawTriangles (D3DDrv.dll.c:1017-1020, diffuse 0xFFFFFFFF,
        // specular 0) and DrawTile (:5295, colour used only without 0x40). The colour computed by Render never reaches the frame.
        public static bool WhiteVertex(int pf) => (pf & PF_Modulated) != 0;

        public static bool NoTexture(string path) => string.IsNullOrEmpty(path) || path == "None";

        // Frames that drew the actor (WorldFrame.ActorViews: Views index, −1 = master). An actor Core drew always has a
        // membership; none is a broken Core contract — a loud refusal, not "every pass with the master camera" (§4).
        public static int[] ViewsOf(HP1.UEScript.WorldFrame f, int actorId)
            => f.ActorViews.SetOf.TryGetValue(actorId, out int s) ? f.ActorViews.Sets[s]
             : throw new InvalidOperationException($"actor {actorId}: drawn without frame membership (WorldFrame.ActorViews)");

        // Camera of frame `view` in THAT frame's world. FDynamicSprite::Setup (Render.dll 0x10b30e95..0x10b30ecd),
        // DrawParticleSystem (FUN_10b16a10) and DrawMesh (specular, PF_Environment) take origin and axes from the Frame they
        // are given, so in a mirror or portal the eye and axes are the child's: Core's own Coords of that frame
        // (ViewFrame.Coords, mirror flip included); the master (view −1, or AnyView) is UESceneNode.MasterCoords of the frame
        // camera. Coords: X right, Y down, Z forward. The pass draws the result back through M.
        public static void ViewCamera(HP1.UEScript.WorldFrame f, int view, out HP1.UEScript.UEVector eye,
            out HP1.UEScript.UEVector fwd, out HP1.UEScript.UEVector right, out HP1.UEScript.UEVector up)
        {
            HP1.UEScript.BrushCoords c = view < 0 ? HP1.UEScript.UESceneNode.MasterCoords(f.Camera.Location, f.Camera.Rotation) : f.Views[view].Coords;
            fwd = c.Z; right = c.X; up = -c.Y;
            eye = view < 0 ? f.Camera.Location : c.Origin;
        }

        public static void Require(object dep, string name)
        {
            if (dep == null) throw new ArgumentNullException(name, "Sprites: required input «" + name + "» is not set");
        }
    }
}
