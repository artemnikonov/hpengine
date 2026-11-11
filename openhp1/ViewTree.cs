// CHILD VIEWS of the display frame: Core occlusion child frames (WorldFrame.Views, D-61): sky, mirror, warp portal. The
// frontend neither chooses them nor computes cameras: a view is the frame's M (view world -> root world) and its NearClip.
// Here only which world batch of the parent view takes which child view's colour: a warp portal by its level surface
// (WorldMesh.WarpPortals), a mirror by its surface key (WorldMesh.MirrorPlaneOfSurf / MoverScene.MirrorPlaneOf: one key
// per mirrored surface, so the key names exactly the surfaces Core listed in ViewFrame.Sources), a sky window by the
// parent (all PF_FakeBackdrop batches of a view sample its sky child).
// One key, one view: a second frame claiming a key already taken would never be drawn — a loud refusal, not a silent
// first-wins.
using HP1.UEScript;

namespace OpenHP1
{
    public sealed class ViewTree
    {
        public struct View { public int Parent, Kind; public Affine M; public UEVector ClipN; public float ClipD; public bool Flip; }
        public const int KindMirror = 1, KindWarp = 2, KindSky = 3;
        public readonly List<View> Views = new();
        public readonly List<int> FrameOf = new();   // view -> frame index in WorldFrame.Views (root = −1)
        readonly Dictionary<(int Parent, int Kind, int Key), int> _childOf = new();

        public int ChildOf(int view, int kind, int key) => _childOf.TryGetValue((view, kind, key), out int vi) ? vi : -1;

        // portalOf: level surface -> warp portal batch (−1 none); mirrorKey: mirror source -> batch key (−1 none).
        // Actors of a sky frame need nothing here: shipped DrawFrame draws every frame's own sprite list with no test of the
        // frame kind (0x10b25704..0x10b2576d), and the actor pass draws each batch in the view of its frame (FrameView).
        public void Build(IReadOnlyList<ViewFrame> frames, Func<int, int> portalOf, Func<ViewSource, int> mirrorKey,
            Action<string> refuse)
        {
            Views.Clear(); _childOf.Clear(); FrameOf.Clear();
            Views.Add(new View { Parent = -1, M = Affine.Identity }); FrameOf.Add(-1);
            var at = new int[frames.Count];   // Core frame index -> view index (−1 = not drawn)
            for (int i = 0; i < frames.Count; i++)
            {
                ViewFrame fv = frames[i];
                int parent = fv.Parent < 0 ? 0 : at[fv.Parent];
                at[i] = -1;
                if (parent < 0) continue;
                if (Views[parent].Kind == KindSky) { refuse($"child frame (branch {fv.Kind}) of a sky frame — the sky view draws only the sky"); continue; }
                int kind = fv.Kind switch { ViewFrame.KindSky => KindSky, ViewFrame.KindMirror => KindMirror, _ => KindWarp };
                int vi = Views.Count;
                Views.Add(new View { Parent = parent, Kind = kind, M = fv.M, ClipN = fv.ClipN, ClipD = fv.ClipD, Flip = fv.Flip });
                FrameOf.Add(i);
                at[i] = vi;
                if (kind == KindSky) { Claim(parent, kind, 0, vi, i, refuse); continue; }
                foreach (ViewSource src in fv.Sources)
                {
                    int key = kind == KindWarp ? (src.Surf >= 0 ? portalOf(src.Surf) : -1) : mirrorKey(src);
                    if (key < 0) { refuse($"frame of branch {fv.Kind}: surface {src.Surf} (mover {src.MoverId}, polygon {src.MoverPoly}) has no mesh batch"); continue; }
                    Claim(parent, kind, key, vi, i, refuse);
                }
            }
        }

        void Claim(int parent, int kind, int key, int vi, int frame, Action<string> refuse)
        {
            if (_childOf.TryAdd((parent, kind, key), vi) || _childOf[(parent, kind, key)] == vi) return;
            refuse($"frame {frame} (kind {kind}) claims batch key {key} of view {parent}, already taken by view {_childOf[(parent, kind, key)]} — "
                + "its colour is never shown");
        }
    }
}
