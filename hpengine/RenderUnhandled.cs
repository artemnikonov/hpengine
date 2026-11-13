// LOUD ACCOUNTING OF UNHANDLED FLAGS (flag × carrier census).
//
// A draw path reports a flag it RECEIVED and does NOT handle, and draws as usual. One log line per carrier × flag
// combination ("[Render] <unhandled>: <carrier> <flag> <example>"), a counter per hit. Does not change drawing.
// Defect class this exists for: PF_Mirrored on a mover brush was drawn dark because the mirror pass knew only the
// level BSP, and nothing said so.
//
// "Handled" is a bit the path reads; "no effect by rule" is a bit the original does not read on the D3D path
// (per the flag census): it is not reported, otherwise 42k surfaces with
// PF_Semisolid would drown the real ones. A new bit in data or in a rule is reported until it is triaged.
namespace HPEngine
{
    public static class RenderUnhandled
    {
        public static Action<string> Log = Console.WriteLine;
        static readonly Dictionary<string, int> _counts = new();
        static readonly object _gate = new();

        public static void Note(string carrier, string feature, string example)
        {
            string key = carrier + " " + feature;
            bool first;
            lock (_gate)
            {
                _counts.TryGetValue(key, out int n);
                _counts[key] = n + 1;
                first = n == 0;
            }
            if (first) Log($"[Render] unhandled: {key} {example}");
        }

        public static Dictionary<string, int> Snapshot() { lock (_gate) return new Dictionary<string, int>(_counts); }
        public static void Reset() { lock (_gate) _counts.Clear(); }

        // Surface PolyFlags bit names (EPolyFlags; 0..23 are Engine.Texture bool names).
        static readonly string[] Names =
        {
            "Invisible", "Masked", "Translucent", "NotSolid", "Environment", "Semisolid", "Modulated", "FakeBackdrop",
            "TwoSided", "AutoUPan", "AutoVPan", "NoSmooth", "BigWavy", "SmallWavy", "WaterWavy", "LowShadowDetail",
            "NoMerge", "CloudWavy", "DirtyShadows", "HighLedge", "SpecialLit", "Gouraud", "Unlit", "HighShadowDetail",
            "Memorized", "Selected", "Portal", "Mirrored", "Highlighted", "FlatShaded", "bit30", "bit31",
        };

        static void Bits(string carrier, int unhandled, string example)
        {
            for (int i = 0; i < 32; i++)
                if ((unhandled >> i & 1) != 0) Note(carrier, "PF_" + Names[i], example);
        }

        const int Invisible = 0x1, Masked = 0x2, Translucent = 0x4, NotSolid = 0x8, Semisolid = 0x20, Modulated = 0x40,
            FakeBackdrop = 0x80, TwoSided = 0x100, AutoUPan = 0x200, AutoVPan = 0x400, NoSmooth = 0x800, BigWavy = 0x1000,
            SmallWavy = 0x2000, LowShadowDetail = 0x8000, NoMerge = 0x10000, DirtyShadows = 0x40000, HighLedge = 0x80000,
            SpecialLit = 0x100000, Unlit = 0x400000, HighShadowDetail = 0x800000, Portal = 0x4000000, Mirrored = 0x8000000;

        // WorldMesh.Build (level BSP and mover faces): blending, mask, side, sky window, clock panning, point
        // sampling, SpecialLit, Unlit, portal (drawn as a surface), mirror.
        public const int SurfaceHandled = Invisible | Masked | Translucent | Modulated | FakeBackdrop | TwoSided | AutoUPan
            | AutoVPan | NoSmooth | SmallWavy | SpecialLit | Unlit | Portal | Mirrored;
        // No effect on the D3D path (census: BigWavy is not read by Render/D3DDrv; build/collision; editor
        // bits 24/25/28-31 are cleared by SetupActorBrush & 0x3cffffff for movers, not read for BSP).
        public const int SurfaceNoEffect = NotSolid | Semisolid | BigWavy | LowShadowDetail | NoMerge | DirtyShadows
            | HighLedge | HighShadowDetail | unchecked((int)0xf3000000);

        public static void Surface(string carrier, int eff, string example)
            => Bits(carrier, eff & ~SurfaceHandled & ~SurfaceNoEffect, example);

        // Mesh material: ActorOpacity.Resolve reads Masked/Translucent/Modulated, SdlActors reads TwoSided.
        public const int MeshMaterialHandled = Masked | Translucent | Modulated | TwoSided;

        public static void MeshMaterial(int flags, string example) => Bits("mesh-material", flags & ~MeshMaterialHandled, example);

        // Sprite and particle: SpriteMath.BlendOfFlags reads Masked/Translucent/Modulated; Unlit always (light is not
        // computed); TwoSided is meaningless for a tile. Sprite NoSmooth comes from the actor only (texture flag not read).
        public const int SpriteHandled = Masked | Translucent | Modulated | Unlit | TwoSided;

        public static void SpriteTexture(string carrier, int texFlags, string example)
            => Bits(carrier, texFlags & ~SpriteHandled & ~SurfaceNoEffect, example);
    }
}
