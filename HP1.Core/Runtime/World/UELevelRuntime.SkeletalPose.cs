// SKELETON POSE IN CORE — port of USkeletalMesh::ApplyAnim + USkeletalMesh::GetFrame
// (Engine.dll.c:141767-142900 and 29125-29300).
//
// Why in Core and not the frontend: a bone position is a GAME value. Native 257
// BonePos (Hog2/Hub5), PHYS_Trailer attachment by AnimBone (BroomTrail on all 11
// broom maps), the particle DIST_OwnerMesh branch and the shadow route depend on it.
//
// WHAT WAS READ IN SHIPPED (ApplyAnim):
//  * actor animation: +0x68 Animation (if null — the mesh DefaultAnimation,
//    +0x24c); the sequence is looked up by AnimSequence name (+0x50) —
//    tracks are taken by the mesh bone NAME among the animation RefBones (loop
//    141928-141960, "Warning: missing bone animation" on a miss);
//  * track target time t = min(AnimFrame, 1.0)·TrackTime (142090);
//  * key search: accumulate KeyTimes[k]·TimeScale while the sum ≤ t;
//    exact match — the key as is; past the last key — WRAP to
//    key 0 with upper bound TrackTime; else alpha = (t−lo)/(hi−lo),
//    quaternion — SlerpQuat (Core.dll.c:30634), position — linear blend
//    (FUN_1041f5c0: (1−a)·lo + a·hi);
//  * a bone without a track takes the RefSkeleton reference pose (bone+8: quat, pos);
//  * key quaternion — FAnimVec::Quat: sin(k·C) on three axes, w = sqrt(1−…)
//    (in Hp1AnimTrack.DecodeQuaternion), position —
//    key·PositionScale·(1/32767);
//  * tween: with nonzero TweenRate and a previous pose in the cache the new pose
//    is blended with the previous one: quat = SlerpQuat(new, old, 1−TweenAlpha),
//    pos = new·TweenAlpha + old·(1−TweenAlpha) (142856-142876).
//
// CHAIN COMPOSITION (GetFrame 29225-29260): a bone FCoords from its place
// (quat, pos), divided by the parent coords down the hierarchy. The same
// composition is proven on shipped data for the reference pose
// (Hp1SkeletalMesh.ComputeReferencePosePoints — conjugate quaternion and
// order M_local·M_parent, 492/492 vertices). Here it is applied to the
// animated bone places.
//
// WHAT IS NOT HERE, STATED OUT LOUD:
//  * the pose of a CHANNEL actor itself (AnimBone ≠ 0 && Physics ≠ Trailer) — LOUD
//    STOP: a channel has no pose of its own. Overlaying channels onto the OWNER's pose
//    is ported (OverlayChannel);
//  * the animation StartBone offset (walking parents of the animation root above
//    the mesh root, 142380-142600) — STOP when parent ≠ self at the root;
//  * bAnimByOwner in GetFrame — STOP (no carriers in the campaign);
//  * GetFrame of a drawn mesh is ported (RenderMeshFrame): a full ApplyAnim of
//    every DT_Mesh actor drawn by DrawActorSprite, in DrawFrame order
//    (RenderLevelPass.DrawMeshSprite).
//
// ORACLE: the real ApplyAnim/GetRootMovement/AdjustRootMovement/
// GetBoneCoords/GetMeshCoords under unicorn.
using System;
using System.Collections.Generic;
using System.IO;
using HP1.Core;
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        // Bone place: quaternion and position (UE basis, mesh space).
        public struct BonePlace
        {
            public float QX, QY, QZ, QW;
            public Vector3 Pos;
        }

        // World bone coords: origin and axes (basis columns in the world).
        public struct BoneWorld
        {
            public Vector3 Origin;
            public Vector3 X, Y, Z;
        }

        // ── SKELETON CACHE HEADER (CFSkelHeader) ───────────────────────────
        //
        // ONE object per actor — like the shipped FMemCache item (key
        // Index·0x100+0x34). Pose cache (tween, memoisation) and root motion
        // state are fields of one header, written by ALL ApplyAnim calls — the
        // renderer (GetFrame, full), GetRootMovement/GetRenderBoundingBox (root
        // only), GetBoneCoords. Layout (Engine.dll.c:142025-142064,
        // disasm 0x1041bb8c-0x1041bc33):
        //   +0x08 Frame, +0x0c Alpha  — memoisation, −1.0 on creation;
        //   +0x10 Seq                 — FName, 0 (= None) on creation;
        //   +0x14 HasPose             — "pose exists" (tween condition);
        //   +0x1c Places              — LOCAL bone places, a single buffer:
        //                               base after tween AND channel overlay;
        //   +0x20 MeshCoords          — the actor's GetMeshCoords on this call;
        //   +0x6c RootLocation, +0x78 RootFrame, +0x7c RootDelta,
        //   +0x88 Adjust, +0x94 RootInit — root motion.
        // ASSUMPTION, STATED OUT LOUD: the body does NOT write +0x94 on creation
        // (disasm above) — it holds FMemCache leftover memory. The port takes false.
        // The comparison with the original measures sensitivity: it exists ONLY for
        // the first GetRootMovement of a fresh cache with no full call before it.
        internal sealed class SkelCache
        {
            public string Mesh;
            public float Frame = -1f, Alpha = -1f;
            public string Seq = "None";
            public bool HasPose;
            public BonePlace[] Places;
            public BrushCoords MeshCoords;
            public Vector3 RootLocation;
            public float RootFrame;
            public Vector3 RootDelta;
            public Vector3 Adjust;
            public bool RootInit;
            // Render box (+0x50..+0x68, FUN_1041b500): empty and invalid on creation.
            public Hp1Box RenderBox;
            // Diagnostics (not a shipped field): number of full ApplyAnim calls — the "one GetFrame per frame" fixture.
            public int FullApplies;
            // MeshCoordsOf memo (not a shipped field): the input property values it was computed from (by
            // reference — values are immutable boxes, the same reference is the same value) and the precision window.
            public object[] CoordsInputs;
            public bool CoordsPc24;
            public object CoordsHook;
        }
        readonly Dictionary<UEObject, SkelCache> _skelCache = new();

        // For fixtures and teardown checks: state keyed by world actors.
        internal SkelCache SkelCacheOf(UEObject actor)
            => _skelCache.TryGetValue(actor, out SkelCache c) ? c : null;

        static bool SameName(string a, string b)
            => string.Equals(a ?? "None", b ?? "None", StringComparison.OrdinalIgnoreCase);

        static string SequenceNameOf(UEObject actor)
        {
            string s = actor.GetProp(PK.AnimSequence)?.ToString();
            return string.IsNullOrEmpty(s) ? "None" : s;
        }

        // ── ACTOR ANIMATION OBJECT ─────────────────────────────────────────
        // AActor::GetAnim (Engine.dll.c:161844-161880) and ApplyAnim (142118-142131):
        // sequences come from Actor.SkelAnim (+0x68), and from the mesh
        // (DefaultAnimation, +0x24c) only when SkelAnim is empty. Needed for animation
        // swaps by TriggerChangeActorMesh (Lev5_Final: Quirrell -> skvoldemortAnims;
        // Lev2_fire1: CutHarry -> skhagridAnims) to reach poses and PlayAnim.
        IReadOnlyDictionary<string, Hp1AnimSequence> AnimationOf(UEObject actor, Hp1SkeletalMesh mesh)
        {
            if (actor.GetProp(PK.SkelAnim) is UEResourceRef anim)
            {
                if (!anim.Class.Equals("Animation", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Actor.SkelAnim is {anim.Class} actor={actor.GetProp(PK.Name)}");
                return MeshCatalog.GetAnimation(anim);
            }
            return mesh.AnimObject is null ? null : mesh.Sequences;
        }

        // Reference to the mesh DefaultAnimation — ApplyAnim WRITES it into an empty SkelAnim
        // (142119-142121). The mesh animation object lives in the mesh package (the reader
        // stops loudly on a cross-package reference).
        internal static UEResourceRef DefaultAnimationRef(UEResourceRef meshRef, Hp1SkeletalMesh mesh)
        {
            string path = meshRef.ObjectPath;
            int dot = path is null ? -1 : path.IndexOf('.');
            return new UEResourceRef("Animation", mesh.AnimObject,
                dot > 0 ? path.Substring(0, dot) + "." + mesh.AnimObject : mesh.AnimObject);
        }

        static BonePlace RefPlace(Hp1Bone b)
            => new BonePlace { QX = b.OrientX, QY = b.OrientY, QZ = b.OrientZ, QW = b.OrientW, Pos = b.Position };

        static int TrackOf(Hp1AnimSequence sequence, string boneName)
        {
            Hp1AnimBone[] refBones = sequence.Movement.RefBones;
            for (int k = 0; k < refBones.Length; k++)
                if (refBones[k].Name.Equals(boneName, StringComparison.OrdinalIgnoreCase))
                    return k < sequence.Movement.Tracks.Length ? k : -1;
            return -1;
        }

        // ── USkeletalMesh::ApplyAnim (Engine.dll.c:141771-142980) ──────────
        //
        // rootOnly = shipped third argument (param_3): GetRootMovement,
        // GetRenderBoundingBox and GetBoneCoords(0) pass true — ONLY bone 0 is
        // computed, the header (+0x08..+0x14) is not updated, channels are not
        // overlaid; GetFrame and GetBoneCoords(i≠0) — full call.
        // Body order (disasm 0x1041bc35-0x1041d748):
        //  1. sequence change and NOT rootOnly -> RootInit=0, RootFrame=0;
        //  2. MeshCoords = GetMeshCoords(actor);
        //  3. memoisation: same sequence, AnimFrame and TweenAlpha — computation
        //     is skipped, the place buffer stays AS IS (with previous channels);
        //  4. an empty SkelAnim gets the mesh DefaultAnimation;
        //  5. no animation/sequence -> reference pose WITHOUT tween (0x1041d625);
        //  6. else per bone: track or reference pose, bone 0 gets the root
        //     block, then tween with the previous buffer (weight 1−TweenAlpha if a pose
        //     already existed and TweenRate≠0);
        //  7. full call: header = (Seq, Frame, Alpha), HasPose, channels.
        internal SkelCache ApplyAnim(UEObject actor, bool rootOnly)
        {
            if (actor is null) throw new ArgumentNullException(nameof(actor));
            if (MeshCatalog is null)
                throw new InvalidOperationException("skeletal pose without a mesh catalog — LoadMeshCatalogFromInstall not called");
            if (actor.GetProp(PK.Mesh) is not UEResourceRef meshRef
                || !meshRef.Class.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException(
                    $"skeletal pose requires a SkeletalMesh actor={actor.GetProp(PK.Name)}");
            // Channel animation (AnimBone on a non-trailer) — the owner's pose
            // is overlaid FROM the channel (OverlayChannel); a channel has no pose of its own.
            // Unreachable: campaign channels are AnimChannel (DrawType=0), cHarryAnimChannel
            // and fluffyHead* without DrawType/Physics/BonePos in scripts: DrawActorSprite does
            // not draw them and nothing asks their BonePos (7 calls of 257 in .uc — all on
            // self, on non-channels).
            if (UEInterpreter.ToInt(actor.GetProp(PK.AnimBone)) != 0
                && UEInterpreter.ToInt(actor.GetProp(PK.Physics)) != 11)
                throw new NotSupportedException(
                    $"skeletal pose: AnimChannel routing (AnimBone={actor.GetProp(PK.AnimBone)}) not ported actor={actor.GetProp(PK.Name)}");

            Hp1SkeletalMesh mesh = MeshCatalog.Get(meshRef);
            int n = mesh.Bones.Length;
            if (n == 0) throw new InvalidDataException($"skeletal pose: mesh {mesh.Name} has no bones");

            // A cache of another mesh is dropped and recreated (FMemCache::Flush).
            if (!_skelCache.TryGetValue(actor, out SkelCache cache)
                || !string.Equals(cache.Mesh, mesh.Name, StringComparison.OrdinalIgnoreCase))
                _skelCache[actor] = cache = new SkelCache { Mesh = mesh.Name, Places = new BonePlace[n] };

            string seq = SequenceNameOf(actor);
            if (!SameName(cache.Seq, seq) && !rootOnly)
            {
                cache.RootInit = false;
                cache.RootFrame = 0f;
            }
            cache.MeshCoords = MeshCoordsMemo(actor, cache);

            float frame = UEInterpreter.AsFloat(actor.GetProp(PK.AnimFrame));
            float alpha = UEInterpreter.AsFloat(actor.GetProp(PK.TweenAlpha));
            bool memo = SameName(cache.Seq, seq) && cache.Frame == frame && cache.Alpha == alpha;
            if (!memo)
            {
                if (actor.GetProp(PK.SkelAnim) is not UEResourceRef && mesh.AnimObject is not null)
                    actor.Props["SkelAnim"] = DefaultAnimationRef(meshRef, mesh);
                IReadOnlyDictionary<string, Hp1AnimSequence> anim = AnimationOf(actor, mesh);
                Hp1AnimSequence sequence = anim is not null && anim.TryGetValue(seq, out Hp1AnimSequence found) ? found : null;
                int count = rootOnly ? 1 : n;
                if (sequence is null)
                {
                    for (int i = 0; i < count; i++) cache.Places[i] = RefPlace(mesh.Bones[i]);
                    if (rootOnly) return cache;
                }
                else
                {
                    if (sequence.Movement.Tracks.Length != 0)
                        SampleBones(actor, mesh, sequence, cache, seq, frame, alpha, count);
                    if (rootOnly) return cache;
                }
            }
            else if (rootOnly) return cache;

            cache.FullApplies++;
            cache.HasPose = true;
            cache.Seq = seq;
            cache.Frame = frame;
            cache.Alpha = alpha;
            // CHANNELS (Engine.dll.c:142947-142975, disasm ApplyAnim tail up to
            // 0x1041d748): after the base, every AuxAnims entry is overlaid into THE SAME
            // buffer, in array order; right after the overlay a channel with
            // bAnimTransient (+0x28 bit 0x100), without bAnimLoop (bit 0x10) and with
            // AnimLast (+0x22c) <= AnimFrame (+0x54) is destroyed by
            // ULevel::DestroyActor (XLevel vtable +0x98) and removed from the array.
            // ONLY a full call does this — i.e. drawing the owner (GetFrame),
            // GetBoneCoords(bone ≠ 0) and pose points; not a per-tick sweep of all owners.
            if (_animChannels.TryGetValue(actor, out List<AnimChannelEntry> channels))
            {
                bool reaped = false;
                for (int i = 0; i < channels.Count; i++)
                {
                    UEObject ch = channels[i].Object;
                    OverlayChannel(ch, meshRef, mesh, cache.Places);
                    if (!UEInterpreter.ToBool(ch.GetProp(PK.bAnimTransient)) || UEInterpreter.ToBool(ch.GetProp(PK.bAnimLoop))
                        || UEInterpreter.AsFloat(ch.GetProp(PK.AnimLast)) > UEInterpreter.AsFloat(ch.GetProp(PK.AnimFrame)))
                        continue;
                    DestroyActor(actor.Interp ?? throw new InvalidOperationException(
                        $"ApplyAnim: removing channel {ch.GetProp(PK.Name)} — owner {actor.GetProp(PK.Name)} has no interpreter"), ch);
                    channels.RemoveAt(i);
                    i--;
                    reaped = true;
                }
                if (reaped)
                {
                    if (channels.Count == 0) _animChannels.Remove(actor);
                    SyncAuxAnims(actor);
                }
            }
            return cache;
        }

        // GetMeshCoords (MeshCoordsOf) reads exactly these actor properties, the shipped mesh scalars of the Mesh
        // reference (through MeshHashFieldsHook: a pure lookup in production; the delegate is part of the key) and
        // the caller's precision window; nothing else.
        // HP1_FRAME_CACHE_VERIFY=1 recomputes every hit.
        static readonly int[] CoordsInputKeys =
        {
            PK.Mesh, PK.DrawScale, PK.Wideness, PK.PrePivot, PK.Rotation, PK.Location,
            PK.bAlignBottom, PK.bCollideWorld, PK.Physics, PK.CollideType, PK.CollisionHeight,
        };

        BrushCoords MeshCoordsMemo(UEObject actor, SkelCache cache)
        {
            bool pc24 = UEWorldFrameProducer.FastMathWindow;
            object[] inputs = cache.CoordsInputs;
            bool same = inputs is not null && cache.CoordsPc24 == pc24 && ReferenceEquals(cache.CoordsHook, MeshHashFieldsHook);
            for (int i = 0; same && i < CoordsInputKeys.Length; i++)
                same = ReferenceEquals(inputs[i], actor.GetProp(CoordsInputKeys[i]));
            if (same)
            {
                if (FrameCacheVerify.Enabled)
                {
                    BrushCoords fresh = MeshCoordsOf(actor), memo = cache.MeshCoords;
                    FrameCacheVerify.Hit("mesh_coords", SameCoords(fresh, memo) ? null
                        : $"{actor.GetProp(PK.Name)} memo={memo.Origin} fresh={fresh.Origin}");
                }
                return cache.MeshCoords;
            }
            if (FrameCacheVerify.Enabled) FrameCacheVerify.Miss("mesh_coords");
            BrushCoords c = MeshCoordsOf(actor);
            inputs ??= cache.CoordsInputs = new object[CoordsInputKeys.Length];
            for (int i = 0; i < CoordsInputKeys.Length; i++) inputs[i] = actor.GetProp(CoordsInputKeys[i]);
            cache.CoordsPc24 = pc24;
            cache.CoordsHook = MeshHashFieldsHook;
            return c;
        }

        static bool SameCoords(in BrushCoords a, in BrushCoords b)
        {
            static bool V(Vector3 x, Vector3 y) => BitConverter.SingleToInt32Bits(x.x) == BitConverter.SingleToInt32Bits(y.x)
                && BitConverter.SingleToInt32Bits(x.y) == BitConverter.SingleToInt32Bits(y.y)
                && BitConverter.SingleToInt32Bits(x.z) == BitConverter.SingleToInt32Bits(y.z);
            return V(a.Origin, b.Origin) && V(a.X, b.X) && V(a.Y, b.Y) && V(a.Z, b.Z);
        }

        void SampleBones(UEObject actor, Hp1SkeletalMesh mesh, Hp1AnimSequence sequence, SkelCache cache,
            string seq, float frame, float alpha, int count)
        {
            float trackTime = sequence.Movement.TrackTime;
            float t = Math.Min(frame, 1f) * trackTime;
            float tweenRate = UEInterpreter.AsFloat(actor.GetProp(PK.TweenRate));
            // fStack_38 = (+0x14 && TweenRate≠0) ? 1 − TweenAlpha : 0 (142186-142191).
            float wOld = cache.HasPose && tweenRate != 0f ? 1f - alpha : 0f;
            bool animMove = UEInterpreter.ToBool(actor.GetProp(PK.bAnimMove));
            for (int i = 0; i < count; i++)
            {
                int track = TrackOf(sequence, mesh.Bones[i].Name);
                BonePlace place;
                if (track < 0) place = RefPlace(mesh.Bones[i]);
                else
                {
                    Hp1AnimTrack tr = sequence.Movement.Tracks[track];
                    place = SampleTrack(tr, t, trackTime);
                    if (i == 0)
                    {
                        // The animation root must be self-parented — otherwise it
                        // needs the StartBone walk (142380-142560), not ported.
                        if (sequence.Movement.RefBones[track].ParentIndex != track)
                            throw new NotSupportedException(
                                $"skeletal pose: animation root parent chain unsupported {mesh.Name}.{sequence.Name}");
                        if (animMove) RootBlock(mesh, tr, cache, seq, frame, ref place);
                    }
                }
                if (wOld != 0f) place = BlendPlace(place, cache.Places[i], wOld);
                cache.Places[i] = place;
            }
        }

        // ── ROOT BLOCK (bAnimMove, Engine.dll.c:142567-142858; disasm
        //    0x1041cabf-0x1041d408) ────────────────────────────────────────
        //  * RootInit=0 -> RootLocation = RAW position key 0 of the root
        //    track, Adjust = 0, RootInit = 1;
        //  * delta = bone place − RootLocation; RootLocation = bone place;
        //  * bone 0's place is REPLACED by the RefSkeleton[0] reference position
        //    (0x1041d149-0x1041d178): the actor carries the movement, not the pose;
        //  * RootDelta += delta (x — sum in extended precision, y/z — from
        //    rounded differences: 0x1041d11c-0x1041d18d);
        //  * same sequence in the header and AnimFrame ≠ RootFrame: if Adjust ≠ 0,
        //    per-component return within |last − first|·(AnimFrame|1 −
        //    RootFrame), where first/last are the RAW keys 0 and PositionCount−1
        //    (0x1041d1ef-0x1041d402); then RootFrame = AnimFrame.
        static void RootBlock(Hp1SkeletalMesh mesh, Hp1AnimTrack track, SkelCache cache,
            string seq, float frame, ref BonePlace place)
        {
            if (track.PositionCount <= 0)
                throw new InvalidDataException("bAnimMove root track has no position keys");
            if (!cache.RootInit)
            {
                cache.RootLocation = track.DecodePosition(0);
                cache.Adjust = Vector3.zero;
                cache.RootInit = true;
            }
            Vector3 cur = place.Pos;
            float dy = cur.y - cache.RootLocation.y, dz = cur.z - cache.RootLocation.z;
            // The body sets no control word: GetFrame's full ApplyAnim runs it inside the render window, where the x sum
            // rounds to 24 bits at each step.
            bool pc24 = UEWorldFrameProducer.FastMathWindow;
            float rdx = pc24 ? (cur.x - cache.RootLocation.x) + cache.RootDelta.x
                : (float)((double)cur.x - cache.RootLocation.x + cache.RootDelta.x);
            cache.RootLocation = cur;
            place.Pos = mesh.Bones[0].Position;
            cache.RootDelta = new Vector3(rdx, dy + cache.RootDelta.y, dz + cache.RootDelta.z);

            if (!SameName(cache.Seq, seq) || frame == cache.RootFrame) return;
            if (cache.Adjust.x != 0f || cache.Adjust.y != 0f || cache.Adjust.z != 0f)
            {
                Vector3 first = track.DecodePosition(0);
                Vector3 last = track.DecodePosition(track.PositionCount - 1);
                float ax = Math.Abs(last.x - first.x), ay = Math.Abs(last.y - first.y), az = Math.Abs(last.z - first.z);
                // fld AnimFrame (or 1.0); fsub RootFrame; each fmul |last − first| stored (0x1041d30a..0x1041d33b).
                double advance = pc24 ? (frame == 0f ? 1f : frame) - cache.RootFrame : (double)(frame == 0f ? 1f : frame) - cache.RootFrame;
                float lx = (float)(advance * ax), ly = (float)(advance * ay), lz = (float)(advance * az);
                static float Clamp(float v, float lim) => v < -lim ? -lim : (v > lim ? lim : v);
                float cx = Clamp(cache.Adjust.x, lx), cy = Clamp(cache.Adjust.y, ly), cz = Clamp(cache.Adjust.z, lz);
                cache.RootDelta = new Vector3(cx + cache.RootDelta.x, cy + cache.RootDelta.y, cz + cache.RootDelta.z);
                cache.Adjust = new Vector3(cache.Adjust.x - cx, cache.Adjust.y - cy, cache.Adjust.z - cz);
            }
            cache.RootFrame = frame;
        }

        // ── POSE IN MESH SPACE ───────────────────────────────────────────────
        //
        // Full ApplyAnim (as in GetFrame) and chain composition: bone places
        // in mesh space (before GetMeshCoords). Input of the DIST_OwnerMesh branch and
        // of pose fixtures.
        public BonePlace[] EvaluatePose(UEObject actor)
        {
            SkelCache cache = ApplyAnim(actor, rootOnly: false);
            return ComposePose(cache, MeshCatalog.Get(((UEResourceRef)actor.GetProp(PK.Mesh)).Name,
                ((UEResourceRef)actor.GetProp(PK.Mesh)).ObjectPath));
        }

        // Chain composition over the header's bone places — without ApplyAnim.
        static BonePlace[] ComposePose(SkelCache cache, Hp1SkeletalMesh mesh)
        {
            int n = mesh.Bones.Length;
            var world = new BonePlace[n];
            for (int i = 0; i < n; i++)
            {
                BonePlace l = cache.Places[i];
                int parent = mesh.Bones[i].ParentIndex;
                if (i == 0 || parent == i || parent < 0)
                {
                    world[i] = l;
                    continue;
                }
                BonePlace p = world[parent];
                // Position: parent.pos + R(parent)·local.pos, where R is the basis
                // of the parent's conjugate quaternion (same rule as in
                // ComputeReferencePosePoints: row vector times QuatBasis(−q)).
                float[] pb = QuatBasis(-p.QX, -p.QY, -p.QZ, p.QW);
                Vector3 pos = Transform(pb, p.Pos, l.Pos);
                // Quaternion: child ⊗ parent (M_local·M_parent).
                QMul(l.QX, l.QY, l.QZ, l.QW, p.QX, p.QY, p.QZ, p.QW,
                    out float qx, out float qy, out float qz, out float qw);
                world[i] = new BonePlace { QX = qx, QY = qy, QZ = qz, QW = qw, Pos = pos };
            }
            return world;
        }

        // Tween blend 0x1041d444..0x1041d51a: quat = SlerpQuat(new, old, wOld) with wOld the stored float
        // 1 − TweenAlpha (0x1041bf15); the position weight of the new place is D = 1.0f − wOld (register), not
        // TweenAlpha: pos = float(new·D) + wOld·old, where wOld·old.x stays in the register and y, z are stored.
        static BonePlace BlendPlace(BonePlace fresh, BonePlace old, float wOld)
        {
            BonePlace r = fresh;
            SlerpQuat(fresh.QX, fresh.QY, fresh.QZ, fresh.QW, old.QX, old.QY, old.QZ, old.QW, wOld,
                out r.QX, out r.QY, out r.QZ, out r.QW);
            r.Pos = BlendPosition(fresh.Pos, old.Pos, wOld, UEWorldFrameProducer.FastMathWindow);
            return r;
        }

        // The body sets no control word: pc24 = the caller's render window, every operation in float.
        internal static Vector3 BlendPosition(Vector3 fresh, Vector3 old, float wOld, bool pc24)
        {
            if (pc24 || !float.IsFinite(wOld) || !float.IsFinite(fresh.x) || !float.IsFinite(fresh.y) || !float.IsFinite(fresh.z)
                || !float.IsFinite(old.x) || !float.IsFinite(old.y) || !float.IsFinite(old.z))
            {
                float d = 1f - wOld;
                return new Vector3(fresh.x * d + wOld * old.x, fresh.y * d + wOld * old.y, fresh.z * d + wOld * old.z);
            }
            HP1.Core.X87Ext w = HP1.Core.X87Ext.From(wOld), dr = R64(HP1.Core.X87Ext.From(1f) - w);
            HP1.Core.X87Ext New(float c) => HP1.Core.X87Ext.From(R64(HP1.Core.X87Ext.From(c) * dr).ToFloat());
            return new Vector3(R64(New(fresh.x) + R64(w * HP1.Core.X87Ext.From(old.x))).ToFloat(),
                R64(New(fresh.y) + HP1.Core.X87Ext.From(R64(w * HP1.Core.X87Ext.From(old.y)).ToFloat())).ToFloat(),
                R64(New(fresh.z) + HP1.Core.X87Ext.From(R64(w * HP1.Core.X87Ext.From(old.z)).ToFloat())).ToFloat());
        }

        // CHANNEL OVERLAY — the cStack_1d branch of ApplyAnim (Engine.dll.c:142006,
        // 142138-142141, 142199, 142862-142900). A channel is an actor with AnimBone ≠ 0 and
        // Physics ≠ PHYS_Trailer; its animation writes only bones
        // [AnimBone, AnimBone + NumChildren] (upper bound INCLUSIVE —
        // `iVar14 <= NumChildren + AnimBone`), and only those that have a track in its
        // sequence: a bone without a track does NOT get the reference pose from the
        // channel but stays as the owner's (142199). The channel tween uses ITS
        // TweenRate/TweenAlpha against ITS previous places (the channel's own cache
        // +0x1c), then the places are copied to the owner.
        void OverlayChannel(UEObject channel, UEResourceRef meshRef, Hp1SkeletalMesh mesh, BonePlace[] ownerLocal)
        {
            if (channel is null || channel.Destroyed) return;
            int root = UEInterpreter.ToInt(channel.GetProp(PK.AnimBone));
            // Unreachable: campaign channel roots are
            // 'Harry Spine1', 'bone_head{L,M,R}01' (CreateAnimChannel) and
            // 'MTROLL Spine1' (PlayAnim with a bone); bone 0 of their meshes is 'Harry',
            // 'lock_dummy', 'Bip01', 'MTROLL' (--meshspecials, root=).
            if (root == 0)
                throw new NotSupportedException(
                    $"skeletal pose: channel rooted at bone 0 is a full (non-routed) ApplyAnim — not ported channel={channel.GetProp(PK.Name)}");
            // 0x1041bcb5..0x1041bcfc, the same body as the owner's: an empty channel SkelAnim gets the mesh
            // DefaultAnimation (0x1041bcc7), the sequence comes from it, and only a missing sequence (GetAnimSeq null)
            // returns without writing; TrackTime 0 still samples key 0.
            if (channel.GetProp(PK.SkelAnim) is not UEResourceRef && mesh.AnimObject is not null)
                channel.Props["SkelAnim"] = DefaultAnimationRef(meshRef, mesh);
            string seqName = SequenceNameOf(channel);
            IReadOnlyDictionary<string, Hp1AnimSequence> anim = AnimationOf(channel, mesh);
            if (anim is null || !anim.TryGetValue(seqName, out Hp1AnimSequence sequence)) return;

            int n = ownerLocal.Length;
            if (!_skelCache.TryGetValue(channel, out SkelCache cache) || cache.Places.Length != n)
                _skelCache[channel] = cache = new SkelCache { Mesh = mesh.Name, Places = new BonePlace[n] };
            float tweenRate = UEInterpreter.AsFloat(channel.GetProp(PK.TweenRate));
            float tweenAlpha = UEInterpreter.AsFloat(channel.GetProp(PK.TweenAlpha));
            float wOld = cache.HasPose && tweenRate != 0f ? 1f - tweenAlpha : 0f;   // 0x1041bf0c..0x1041bf1a
            BonePlace[] written = cache.Places;

            float t = Math.Min(UEInterpreter.AsFloat(channel.GetProp(PK.AnimFrame)), 1f);
            int last = Math.Min(n - 1, root + mesh.Bones[root].NumChildren);
            Hp1AnimBone[] refBones = sequence.Movement.RefBones;
            for (int i = root; i <= last; i++)
            {
                int track = -1;
                for (int k = 0; k < refBones.Length; k++)
                    if (refBones[k].Name.Equals(mesh.Bones[i].Name, StringComparison.OrdinalIgnoreCase)) { track = k; break; }
                if (track < 0 || track >= sequence.Movement.Tracks.Length) continue;
                BonePlace place = SampleTrack(sequence.Movement.Tracks[track], t * sequence.Movement.TrackTime,
                    sequence.Movement.TrackTime);
                if (wOld != 0f) place = BlendPlace(place, written[i], wOld);
                written[i] = place;
                ownerLocal[i] = place;
            }
            cache.HasPose = true; cache.Seq = seqName;
        }

        // ── POSED VERTICES, IN THE WORLD: USkeletalMesh::GetFrame with UnitCoords ──
        //
        // Input of the DIST_OwnerMesh branch (AParticleFX::Update passes GMath UnitCoords) and of the frame's mesh poses.
        // IDENTITY COORDS DO NOT MEAN "MESH SPACE": ApplyAnim stores GetMeshCoords(actor) in the header +0x20 (Engine
        // 0x1041bc65 -> 0x1041bc79), and GetFrame's root is (+0x20) /= Inverse(input coords) — the actor's mesh coords —
        // so the vertices come out IN THE WORLD.
        public Vector3[] EvaluatePosePoints(UEObject actor) => EvaluatePosePoints(actor, int.MaxValue);

        // lodRequest — GetFrame's LODRequest (the AActor* overload, AParticleFX's DIST_OwnerMesh call, passes FrameVerts:
        // Engine 0x10404470 copies +0xf0 into it).
        internal Vector3[] EvaluatePosePoints(UEObject actor, int lodRequest)
        {
            if (actor.GetProp(PK.Mesh) is not UEResourceRef meshRef)
                throw new NotSupportedException("pose points: actor without Mesh");
            RefuseAnimByOwner(actor, "pose points");
            Hp1SkeletalMesh mesh = MeshCatalog.Get(meshRef);
            return PosePointsFromCache(mesh, ApplyAnim(actor, rootOnly: false), lodRequest);
        }

        // World vertices from the header a full ApplyAnim (GetFrame) has just filled (a second ApplyAnim in the frame would
        // reapply the tween and channels). The body of USkeletalMesh::GetFrame (Engine 0x1041df50) after ApplyAnim, with
        // Coords = UnitCoords, from the disasm:
        //   * bone chain 0x1041dfff..0x1041e140: root = header MeshCoords /= Inverse(Coords); bone 0 = FCoords(place 0)
        //     /= root; bone i = FCoords(place i) /= bone[parent] in index order (Core FCoords(FPlace), operator/=, Inverse —
        //     float steps or window-following, see CoordsOfPlace / DivCoords);
        //   * one group (+0x1d8 == 1, 0x1041e35b..0x1041e40f): for j < min(group 0 count, verts) vertex j = bone 0 applied to
        //     LocalPoints[j] — ((X.x·p.x + Y.x·p.y) + Z.x·p.z) + O.x per component in the register, one fstp dword;
        //   * otherwise (0x1041e414..0x1041e55e) a zeroed buffer and, for each group i (bone i) and its records k (a point
        //     >= verts leaves the group): w = fild(weight)·1.5259022e-05f (0x1047bce4); s = ((Z·p.z + Y·p.y) + X·p.x) + O
        //     with p = LocalPoints[k]: s.x stays in the register, s.y and s.z are stored; acc.x += float(s.x·w),
        //     acc.y += float(s.y·w), acc.z += w·s.z (the product in the register), each sum stored.
        // The body sets no control word: inside the render window (DrawMeshActor, EmitParticles) every step rounds to 24
        // bits (float steps); outside it the PC64 register chain. Checked against the real
        // GetFrame at FPCW 0x037F and 0x007F.
        // Vertex count n = min(LODRequest, FrameVerts) (0x1041df93..0x1041dfa5); the result has n vertices. Core's frame
        // poses are built once per tick at full LOD (lodRequest = FrameVerts) while DrawLodMesh asks for the frame's LOD
        // count; on the shipped data the two agree on every vertex below that count (every group's records are in
        // ascending point order and FrameVerts == Points).
        [ThreadStatic] static BrushCoords[] _poseBones;
        internal Vector3[] PosePointsFromCache(Hp1SkeletalMesh mesh, SkelCache cache, int lodRequest = int.MaxValue)
        {
            int nb = mesh.Bones.Length;
            // Bone frames are scratch (every entry written before it is read, none escapes): one buffer per thread.
            if (_poseBones is null || _poseBones.Length < nb) _poseBones = new BrushCoords[nb];
            BrushCoords[] bones = nb == 0 ? Array.Empty<BrushCoords>() : _poseBones;   // nb == 0: bones[0] throws, as before
            BrushCoords root = DivCoords(cache.MeshCoords, UERenderVisibility.Inverse(BrushCoords.Identity));
            for (int i = 0; i < nb; i++)
            {
                BoneWorld p = CoordsOfPlace(cache.Places[i]);
                var c = new BrushCoords { Origin = p.Origin, X = p.X, Y = p.Y, Z = p.Z };
                if (i == 0) { bones[0] = DivCoords(c, root); continue; }
                int parent = mesh.Bones[i].ParentIndex;
                // 0x1041e0c3: parent == i fails an assertion in shipped; a later parent reads a bone not yet computed.
                if (parent < 0 || parent >= i)
                    throw new InvalidDataException($"GetFrame: bone {i} of {mesh.Name} has parent {parent} (not an earlier bone)");
                bones[i] = DivCoords(c, bones[parent]);
            }
            int frameVerts = mesh.Lod?.FrameVerts ?? throw new InvalidDataException($"GetFrame: mesh {mesh.Name} has no FrameVerts (+0xf0)");
            int n = Math.Min(lodRequest, frameVerts), groups = mesh.WeightStart.Length;
            var result = new Vector3[n];
            static X87Ext X(float v) => X87Ext.From(v);
            bool pc24 = UEWorldFrameProducer.FastMathWindow;
            static X87Ext M(float a, float b) => X87Ext.From(a) * X87Ext.From(b);   // a float product is exact in 64 bits
            if (groups == 1)
            {
                int count = Math.Min(mesh.WeightNumber.Length > 0 ? mesh.WeightNumber[0] : 0, n);
                if (count > mesh.LocalPoints.Length)
                    throw new InvalidDataException($"GetFrame: {count} points of group 0 beyond LocalPoints[{mesh.LocalPoints.Length}] of {mesh.Name}");
                BrushCoords b0 = bones[0];
                for (int j = 0; j < count; j++)
                {
                    Vector3 v = mesh.LocalPoints[j];
                    if (pc24)
                        result[j] = new Vector3(((b0.X.x * v.x + b0.Y.x * v.y) + b0.Z.x * v.z) + b0.Origin.x,
                            ((b0.X.y * v.x + b0.Y.y * v.y) + b0.Z.y * v.z) + b0.Origin.y,
                            ((b0.X.z * v.x + b0.Y.z * v.y) + b0.Z.z * v.z) + b0.Origin.z);
                    else
                    {
                        float S(float xa, float ya, float za, float o)
                            => R64(R64(R64(M(xa, v.x) + M(ya, v.y)) + M(za, v.z)) + X(o)).ToFloat();
                        result[j] = new Vector3(S(b0.X.x, b0.Y.x, b0.Z.x, b0.Origin.x), S(b0.X.y, b0.Y.y, b0.Z.y, b0.Origin.y),
                            S(b0.X.z, b0.Y.z, b0.Z.z, b0.Origin.z));
                    }
                }
                return result;
            }
            if (groups > nb)
                throw new InvalidDataException($"GetFrame: {groups} weight groups but {nb} bones in {mesh.Name}");
            for (int i = 0; i < groups; i++)
            {
                BrushCoords b = bones[i];
                int start = mesh.WeightStart[i], end = start + mesh.WeightNumber[i];
                for (int k = start; k < end; k++)
                {
                    int point = mesh.WeightPoint[k];
                    if (point >= n) break;
                    Vector3 v = mesh.LocalPoints[k];
                    Vector3 acc = result[point];
                    if (pc24)
                    {
                        float w = mesh.WeightValue[k] * 1.5259022e-05f;
                        float sx = ((b.Z.x * v.z + b.Y.x * v.y) + b.X.x * v.x) + b.Origin.x;
                        float sy = ((b.Z.y * v.z + b.Y.y * v.y) + b.X.y * v.x) + b.Origin.y;
                        float sz = ((b.Z.z * v.z + b.Y.z * v.y) + b.X.z * v.x) + b.Origin.z;
                        result[point] = new Vector3(sx * w + acc.x, sy * w + acc.y, w * sz + acc.z);
                    }
                    else
                    {
                        X87Ext w = X87Ext.From(mesh.WeightValue[k]) * X(1.5259022e-05f);   // 16 x 24 bits: exact
                        X87Ext S(float za, float ya, float xa, float o) => R64(R64(R64(M(za, v.z) + M(ya, v.y)) + M(xa, v.x)) + X(o));
                        X87Ext sx = S(b.Z.x, b.Y.x, b.X.x, b.Origin.x);
                        float sy = S(b.Z.y, b.Y.y, b.X.y, b.Origin.y).ToFloat(), sz = S(b.Z.z, b.Y.z, b.X.z, b.Origin.z).ToFloat();
                        float px = R64(sx * w).ToFloat(), py = R64(X(sy) * w).ToFloat();
                        result[point] = new Vector3(px + acc.x, py + acc.y, R64(R64(w * X(sz)) + X(acc.z)).ToFloat());
                    }
                }
            }
            return result;
        }

        // USkeletalMesh::GetFrame (Engine.dll.c:29203-29207): with bAnimByOwner and a
        // live Owner the pose is taken from the OWNER (ApplyAnim(this mesh, Owner) — the
        // drawn actor's mesh with the owner's cache and coords). No carriers in the
        // campaign: no class defaults bAnimByOwner=True (--dumpdefaults over 812
        // classes), no write in .uc, no name in .unr (only the declaration in
        // Engine.u). So the branch is a LOUD STOP, not the actor's own pose, which
        // would be plausible and wrong.
        static void RefuseAnimByOwner(UEObject actor, string what)
        {
            if (UEInterpreter.ToBool(actor.GetProp(PK.bAnimByOwner)) && actor.GetProp(PK.Owner) is UEObject)
                throw new NotSupportedException(
                    $"{what}: GetFrame bAnimByOwner route (pose of Owner) not ported actor={actor.GetProp(PK.Name)}");
        }

        // ── BONE IN THE WORLD: USkeletalMesh::GetBoneCoords (Engine.dll.c:77972-78050)
        //
        // ApplyAnim(actor, rootOnly = bone == 0); an index outside [0, NumBones) —
        // GetMeshCoords as a whole. Else FCoords(bone place) and up through the
        // parents: C /= FCoords(parent place) until bone 0, then C /= the mesh
        // coords from the header (+0x20). (Converting a composed pose to the world
        // with DOT products against the mesh axes and origin Location + PrePivot
        // without rotation gives other axes for a tilted actor.)
        public BoneWorld BoneWorldOf(UEObject actor, int boneIndex)
        {
            SkelCache cache = ApplyAnim(actor, rootOnly: boneIndex == 0);
            UEResourceRef meshRef = (UEResourceRef)actor.GetProp(PK.Mesh);
            Hp1SkeletalMesh mesh = MeshCatalog.Get(meshRef);
            if (boneIndex < 0 || boneIndex >= mesh.Bones.Length) return AsBoneWorld(MeshCoordsOf(actor));
            return BoneCoordsFromCache(cache, mesh, boneIndex);
        }

        static BoneWorld BoneCoordsFromCache(SkelCache cache, Hp1SkeletalMesh mesh, int bone)
        {
            BoneWorld c = CoordsOfPlace(cache.Places[bone]);
            for (int i = bone; i != 0; )
            {
                int parent = mesh.Bones[i].ParentIndex;
                if (parent == i || parent < 0)
                    throw new InvalidDataException($"GetBoneCoords: bone {i} of {mesh.Name} has no parent chain to bone 0");
                i = parent;
                c = DivCoords(c, CoordsOfPlace(cache.Places[i]));
            }
            return DivCoords(c, AsBoneWorld(cache.MeshCoords));
        }

        static BoneWorld AsBoneWorld(in BrushCoords b) => new BoneWorld { Origin = b.Origin, X = b.X, Y = b.Y, Z = b.Z };

        // FCoords::FCoords(const FPlace&) — Core 0x1014f7a0..0x1014f890, operation order from the disasm: the doublings
        // and six products are stored to float, the three products with w stay on the x87 stack. The body sets no
        // control word: PC64 outside the render window (the w products and the sums wider than float), PC24 inside it
        // (UEWorldFrameProducer.FastMathWindow: every operation rounds to float).
        static BoneWorld CoordsOfPlace(in BonePlace p)
        {
            float x2 = p.QX + p.QX, y2 = p.QY + p.QY, z2 = p.QZ + p.QZ;
            float xx = x2 * p.QX, xy = y2 * p.QX, xz = z2 * p.QX;
            float yy = y2 * p.QY, yz = z2 * p.QY, zz = z2 * p.QZ;
            if (UEWorldFrameProducer.FastMathWindow)
            {
                float fx = x2 * p.QW, fy = y2 * p.QW, fz = z2 * p.QW;
                return new BoneWorld
                {
                    Origin = p.Pos,
                    X = new Vector3(1f - (zz + yy), xy - fz, fy + xz),
                    Y = new Vector3(fz + xy, 1f - (zz + xx), yz - fx),
                    Z = new Vector3(xz - fy, fx + yz, 1f - (yy + xx)),
                };
            }
            double wx = (double)x2 * p.QW, wy = (double)y2 * p.QW, wz = (double)z2 * p.QW;
            return new BoneWorld
            {
                Origin = p.Pos,
                X = new Vector3((float)(1.0 - ((double)zz + yy)), (float)(xy - wz), (float)(wy + xz)),
                Y = new Vector3((float)(wz + xy), (float)(1.0 - ((double)zz + xx)), (float)(yz - wx)),
                Z = new Vector3((float)(xz - wy), (float)(wx + yz), (float)(1.0 - ((double)yy + xx))),
            };
        }

        // FCoords::operator/=(const FCoords& T) — Core 0x1014e900..0x1014ed09. Every product is stored to float
        // (fmul; fstp dword into the FVector constructor arguments) and every sum is fld dword + fadd dword + fstp dword:
        // per component axis' = (a.x·T.X + a.y·T.Y) + a.z·T.Z, origin' = ((o.x·T.X + o.y·T.Y) + o.z·T.Z) + T.Origin
        // (FVector::operator* / operator+, 0x1014eca2..0x1014ece9). A float*float product is exact before its one
        // rounding and a two-term sum rounded to 64 then 24 bits equals one rounding to 24, so the float steps are the
        // shipped numbers in both windows.
        static BoneWorld DivCoords(in BoneWorld c, in BoneWorld t)
        {
            BrushCoords r = DivCoords(new BrushCoords { Origin = c.Origin, X = c.X, Y = c.Y, Z = c.Z },
                                      new BrushCoords { Origin = t.Origin, X = t.X, Y = t.Y, Z = t.Z });
            return new BoneWorld { Origin = r.Origin, X = r.X, Y = r.Y, Z = r.Z };
        }

        internal static BrushCoords DivCoords(in BrushCoords c, in BrushCoords t)
        {
            static Vector3 Sum(in Vector3 v, in BrushCoords t) => new Vector3(
                (v.x * t.X.x + v.y * t.Y.x) + v.z * t.Z.x,
                (v.x * t.X.y + v.y * t.Y.y) + v.z * t.Z.y,
                (v.x * t.X.z + v.y * t.Y.z) + v.z * t.Z.z);
            Vector3 o = Sum(c.Origin, t);
            return new BrushCoords
            {
                Origin = new Vector3(o.x + t.Origin.x, o.y + t.Origin.y, o.z + t.Origin.z),
                X = Sum(c.X, t), Y = Sum(c.Y, t), Z = Sum(c.Z, t),
            };
        }

        // FCoords::OrthoRotation (export 0x1c1c -> Core 0x1014ed10) of a bone: the one PC64 body (WarpNatives.cs).
        public static UERotator OrthoRotation(in BoneWorld c)
            => OrthoRotation(new BrushCoords { Origin = c.Origin, X = c.X, Y = c.Y, Z = c.Z });

        // The same body at PC24 (the render window), disasm 0x1014ed21..0x1014ee14. appAtan2 is a bare fpatan (Core
        // 0x101260f0: fld qword y; fld qword x; fpatan), whose result the precision control does not round; it is multiplied
        // by the qword 65536/2π (0x1017fa50) with ONE rounding to 24 bits and truncated by _ftol. |X.xy| = appSqrt
        // (bare fsqrt, 0x10126100) of X.y·X.y + X.x·X.x in float steps. Roll: the identity coords /= (pitch, yaw, 0)
        // (operator/=(FRotator) at PC24, UEOcclusion.DivRotator24), then (S.z·A.z + S.y·A.y) + S.x·A.x for A = Z and Y.
        // TRANSCENDENTAL: fpatan = UEX87Transcendental.Fpatan (correctly rounded to 64 bits, D-71).
        internal static UERotator OrthoRotation24(in BrushCoords c)
        {
            static int Angle(HP1.Core.X87Ext atan) => Ftol(HP1.Core.X87Ext.Round24(atan * HP1.Core.X87Ext.From(OrthoRadToUnr)).ToDouble());
            int yaw = Angle(AppAtan2(c.X.y, c.X.x));
            float h = (float)Math.Sqrt(c.X.y * c.X.y + c.X.x * c.X.x);
            int pitch = Angle(AppAtan2(c.X.z, h));
            BrushCoords u = UEOcclusion.DivRotator24(BrushCoords.Identity, pitch, yaw, 0);
            float b = (u.Y.z * c.Y.z + u.Y.y * c.Y.y) + u.Y.x * c.Y.x;
            float a = (u.Y.z * c.Z.z + u.Y.y * c.Z.y) + u.Y.x * c.Z.x;
            return new UERotator(pitch, yaw, Angle(AppAtan2(a, b)));
        }

        // ── PAWN WEAPON POINT: WeaponLoc / WeaponRot ────────────────────────
        //
        // Shipped writes them IN THE RENDERER, in the camera frame of the drawing FSceneNode, at PC24 (the render window;
        // every operation rounds to float). Checked bit for bit against the real fragment and
        // the real Core bodies at FPCW 0x007F.
        //  1. USkeletalMesh::GetFrame (Engine 0x1041df50) with Coords = Frame->Coords: root = header MeshCoords (+0x20)
        //     /= Inverse(Coords) (0x1041dfff..0x1041e02c); bone 0 = FCoords(place 0) /= root, bone i = FCoords(place i)
        //     /= bone[parent] (0x1041e05f..0x1041e10e), i.e. the bones in the CAMERA frame;
        //  2. weapon fragment 0x1041e610..0x1041e9d2: W = WeaponAdjust /= bone[WeaponBoneIndex]; X = SafeNormal(W.X);
        //     n = SafeNormal(X × W.Z); Z = X × n; Y = −n; then UnitCoords (GMath +0x18) transformed into W (inline
        //     TransformPointBy / TransformVectorBy, = UEOcclusion.MulAssign24) -> mesh +0x27c;
        //  3. URender::DrawActorSprite (Render 0x10b32ef0..0x10b32f82): M = Inverse(+0x27c) /= Frame->Coords;
        //     WeaponLoc = M.Origin, WeaponRot = OrthoRotation(M).
        // Exact except the fpatan of OrthoRotation (OrthoRotation24).
        public static void WeaponCoordsFromCameraBone(in BrushCoords bone, Vector3 adjustOrigin, float[] adjustAxis,
            in BrushCoords frame, out Vector3 loc, out UERotator rot)
        {
            var adjust = new BrushCoords
            {
                Origin = adjustOrigin,
                X = new Vector3(adjustAxis[0], adjustAxis[1], adjustAxis[2]),
                Y = new Vector3(adjustAxis[3], adjustAxis[4], adjustAxis[5]),
                Z = new Vector3(adjustAxis[6], adjustAxis[7], adjustAxis[8]),
            };
            BrushCoords w = DivCoords(adjust, bone);
            Vector3 x = Vector3.SafeNormal24(w.X.x, w.X.y, w.X.z), z = w.Z;
            Vector3 n = Vector3.SafeNormal24(x.y * z.z - x.z * z.y, x.z * z.x - z.z * x.x, z.y * x.x - x.y * z.x);
            w.Z = new Vector3(x.y * n.z - x.z * n.y, x.z * n.x - n.z * x.x, n.y * x.x - x.y * n.x);
            w.Y = new Vector3(-n.x, -n.y, -n.z);
            w.X = x;
            BrushCoords c = UEOcclusion.MulAssign24(BrushCoords.Identity, w);
            BrushCoords m = DivCoords(UERenderVisibility.Inverse(c), frame);
            loc = m.Origin;
            rot = OrthoRotation24(m);
        }

        // Step 1 for the weapon bone: its ancestors from bone 0 down, each in the camera frame, at PC24.
        static BrushCoords CameraBoneCoords(SkelCache cache, Hp1SkeletalMesh mesh, int bone, in BrushCoords frame)
        {
            var chain = new List<int>();
            for (int i = bone; ; )
            {
                chain.Add(i);
                if (i == 0) break;
                int parent = mesh.Bones[i].ParentIndex;
                if (parent == i || parent < 0)
                    throw new InvalidDataException($"GetFrame: bone {i} of {mesh.Name} has no parent chain to bone 0");
                i = parent;
            }
            bool saved = UEWorldFrameProducer.FastMathWindow;
            UEWorldFrameProducer.FastMathWindow = true;
            try
            {
                BrushCoords c = DivCoords(cache.MeshCoords, UERenderVisibility.Inverse(frame));
                for (int k = chain.Count - 1; k >= 0; k--)
                {
                    BoneWorld p = CoordsOfPlace(cache.Places[chain[k]]);
                    c = DivCoords(new BrushCoords { Origin = p.Origin, X = p.X, Y = p.Y, Z = p.Z }, c);
                }
                return c;
            }
            finally { UEWorldFrameProducer.FastMathWindow = saved; }
        }

        // A pawn with a weapon bone (WeaponBoneIndex >= 0 on its skeletal mesh), drawn with frame coords `frame`;
        // false — the mesh has none, shipped does not write the point (DAT_10b65a08 = 0).
        public bool TryWeaponCoords(UEObject pawn, in BrushCoords frame, out Vector3 loc, out UERotator rot)
        {
            loc = default; rot = default;
            if (MeshCatalog is null || pawn.GetProp(PK.Mesh) is not UEResourceRef meshRef
                || !meshRef.Class.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase)) return false;
            Hp1SkeletalMesh mesh = MeshCatalog.Get(meshRef);
            if (mesh.WeaponBoneIndex < 0) return false;
            // GetFrame: ONE full ApplyAnim, the bone coords come from the same
            // header (a repeated ApplyAnim would reapply channels with the tween).
            RefuseAnimByOwner(pawn, "weapon point");
            SkelCache cache = ApplyAnim(pawn, rootOnly: false);
            WeaponCoordsFromCache(cache, mesh, frame, out loc, out rot);
            return true;
        }

        static void WeaponCoordsFromCache(SkelCache cache, Hp1SkeletalMesh mesh, in BrushCoords frame, out Vector3 loc, out UERotator rot)
        {
            if (mesh.WeaponBoneIndex >= mesh.Bones.Length)
                throw new InvalidDataException($"WeaponBoneIndex {mesh.WeaponBoneIndex} of {mesh.Name} out of range");
            WeaponCoordsFromCameraBone(CameraBoneCoords(cache, mesh, mesh.WeaponBoneIndex, frame), mesh.WeaponAdjustOrigin,
                mesh.WeaponAdjustAxis, frame, out loc, out rot);
        }

        // ── GetFrame OF A DRAWN MESH: URender::DrawMesh ─────────────────────
        //
        // Shipped calls the mesh GetFrame EVERY TIME DrawActorSprite draws a
        // DT_Mesh actor (Render.dll.c:12047-12057, disasm 0x10b32e4a-0x10b32ea1),
        // i.e. every drawn frame, in DrawFrame order, after the world tick. The
        // tween's "old" pose (+0x1c), the cache header (Seq/Frame/Alpha) and the
        // root reset on sequence change depend on that call.
        //
        // URender::DrawMesh (0x10b0e920): mesh IsA ULodMesh (import 0x10b74740) ->
        // DrawLodMesh (0x10b0ff00); else the UMesh vertex branch (GetFrame +0x7c,
        // weapon triangle Render.dll.c:9130-9512) — the campaign has no export of
        // class Mesh (--meshspecials: 381 skeletal, 4 LodMesh); loud failure here.
        //
        // URender::DrawLodMesh:
        //  1. fStack_78 = URender+0x3c ≠ 0 or RendMap ∈ {13,14,15,1}
        //     (0x10b1012e-0x10b10167) — the frame coords are replaced by identity;
        //  2. GetFrame via vtable +0xa4 (0x10b10270): for USkeletalMesh —
        //     Engine 0x1041df50 (Engine.dll.c:29128): ApplyAnim(actor, NULL,
        //     false) — a FULL call, then the weapon point into mesh +0x27c when
        //     WeaponBoneIndex ≥ 0; for ULodMesh — Engine 0x1035e460: writes only the
        //     vertex cache (all stores of body 0x1035e460-0x1035eb36 are the cache
        //     header ≤ +0x10, the vertex buffer and stats globals; no actor
        //     fields), i.e. no side effects for Core;
        //  3. DAT_10b65a08 = 0 (0x10b104fa); weapon triangle from SpecialVerts
        //     (+0x184) — runs only when SpecialVerts > 0: 0 on all 385 shipped meshes
        //     (parse control: ModelVerts == point count on 381/381); loud failure here;
        //  4. fStack_78 ≠ 0 — return without a weapon point (ret 0x10b11089);
        //  5. a skeletal mesh with WeaponBoneIndex ≥ 0 — DAT_10b65a08 = 1, coords
        //     from mesh +0x27c (0x10b11096-0x10b110b9).
        // Returns DAT_10b65a08; cache — the skeleton cache header (for the weapon
        // point) or null for a vertex mesh. request — this draw's DrawLodMesh level (UEMeshLod.VertsRequested,
        // -1 = no LOD: [ebp-0x14] = ModelVerts, 0x10b0ffcd); level — the level the draw's face loop uses after GetFrame
        // (-1 = the full mesh). The skeletal GetFrame does not write the request back (Engine.dll.c:29208-29210).
        internal bool RenderMeshFrame(UEObject actor, bool identityCoords, int request, out SkelCache cache, out Vector3[] lodPoints, out int level)
        {
            cache = null; lodPoints = null; level = request;
            if (actor.GetProp(PK.Mesh) is not UEResourceRef meshRef)
                throw new InvalidOperationException($"DrawMesh: actor {actor.GetProp(PK.Name)} without Mesh");
            if (MeshCatalog is null)
                throw new InvalidOperationException("DrawMesh without a mesh catalog — LoadMeshCatalogFromInstall not called");
            if (meshRef.Class.Equals("LodMesh", StringComparison.OrdinalIgnoreCase))
            {
                Hp1VertexMesh vm = MeshCatalog.GetVertexMesh(meshRef.Name, meshRef.ObjectPath);
                if (vm.SpecialVerts > 0)
                    throw new NotSupportedException(
                        $"DrawLodMesh: weapon triangle via SpecialVerts={vm.SpecialVerts} (Render.dll.c:14256-14323) not ported mesh={meshRef.Name}");
                // ULodMesh::GetFrame (vtable +0xa4, 0x10b10270): Core keeps the vertex cache (UELevelRuntime.VertexMeshFrame).
                int n = request < 0 ? vm.ModelVerts : request;
                lodPoints = EvaluateVertexMeshPoints(actor, ref n);
                // A draw without LOD (flag [ebp-0xf4] = 0) whose tween GetFrame capped the level below ModelVerts: the
                // face loop (0x10b11270) keeps faces with FaceLevel <= n but does not collapse their wedges (0x10b112e0),
                // and wedges at or above n read vertices the transform loop (0x10b103c3, i < n) never wrote.
                if (request < 0 && n < vm.ModelVerts)
                    throw new NotSupportedException(
                        $"DrawLodMesh: no-LOD draw of {actor.GetProp(PK.Name)} capped by the tween GetFrame to {n} of {vm.ModelVerts} — uncollapsed wedges read untransformed vertices (Render 0x10b112e0) mesh={meshRef.Name}");
                level = request < 0 ? -1 : n;
                return false;
            }
            if (!meshRef.Class.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException(
                    $"DrawMesh: mesh of class {meshRef.Class} (UMesh vertex branch, Render.dll.c:9030-9512) not ported mesh={meshRef.Name}");
            Hp1SkeletalMesh mesh = MeshCatalog.Get(meshRef);
            RefuseAnimByOwner(actor, "GetFrame of a drawn mesh");
            cache = ApplyAnim(actor, rootOnly: false);
            if (mesh.SpecialVerts > 0)
                throw new NotSupportedException(
                    $"DrawLodMesh: weapon triangle via SpecialVerts={mesh.SpecialVerts} (Render.dll.c:14256-14323) not ported mesh={mesh.Name}");
            if (identityCoords) return false;
            return mesh.WeaponBoneIndex >= 0;
        }

        // ULodMesh LOD data of a drawn mesh (URender::DrawLodMesh reads it from the Mesh object; UEMeshLod).
        internal (Hp1MeshLod Lod, int ModelVerts, int SpecialVerts, bool Skeletal) MeshLodOf(object mesh)
        {
            if (mesh is not UEResourceRef r) throw new InvalidOperationException($"DrawLodMesh LOD: mesh reference {mesh} is not a resource");
            if (r.Class.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
            {
                Hp1SkeletalMesh m = MeshCatalog.Get(r);
                return (m.Lod, m.ModelVerts, m.SpecialVerts, true);
            }
            if (r.Class.Equals("LodMesh", StringComparison.OrdinalIgnoreCase))
            {
                Hp1VertexMesh v = MeshCatalog.GetVertexMesh(r.Name, r.ObjectPath);
                return (v.Lod, v.ModelVerts, v.SpecialVerts, false);
            }
            throw new NotSupportedException($"DrawLodMesh LOD: mesh of class {r.Class} is not a ULodMesh mesh={r.Name}");
        }

        // Weapon point from the header RenderMeshFrame has just filled, in the drawing frame's coords.
        internal void RenderWeaponCoords(UEObject pawn, SkelCache cache, in BrushCoords frame, out Vector3 loc, out UERotator rot)
        {
            UEResourceRef meshRef = (UEResourceRef)pawn.GetProp(PK.Mesh);
            WeaponCoordsFromCache(cache, MeshCatalog.Get(meshRef), frame, out loc, out rot);
        }

        // ── URender::DrawActorSprite, DT_Mesh BRANCH ────────────────────────
        //
        // Render.dll.c:12047-12280, disasm 0x10b32e4a-0x10b334d6. Pixels are not
        // ported; what is ported is what the renderer WRITES into the world:
        //  1. DrawMesh(actor, Frame->Coords) -> mesh GetFrame (0x10b32ea1;
        //     UELevelRuntime.RenderMeshFrame): a full skeleton ApplyAnim;
        //  2. actor IsA APawn (import 0x10b74734, 0x10b32ea6-0x10b32ed3) and
        //     DAT_10b65a08 ≠ 0 — WeaponRot (+0x328) and WeaponLoc (+0x31c) =
        //     OrthoRotation/Origin of Inverse(mesh +0x27c) /= Frame->Coords
        //     (0x10b32ef0-0x10b32f82) in the camera frame of the drawing FSceneNode
        //     (WeaponCoordsFromCameraBone, bit for bit against this chain);
        //  3. Pawn.Weapon ≠ NULL, Weapon.ThirdPersonMesh ≠ NULL and !Weapon.bHidden
        //     (0x10b32f85-0x10b32f9d) — third-person weapon:
        //       Mesh <-> ThirdPersonMesh, DrawScale <-> ThirdPersonScale,
        //       Rotation = (0,0,0) — NOT restored (0x10b32fc1-0x10b32ff3),
        //       DrawMesh(weapon) (0x10b33085) -> weapon GetFrame,
        //       swap back (0x10b3308a-0x10b330ba);
        //       bSteadyFlash3rd -> bSteadyToggle ^= 1 (0x10b330c0-0x10b330cf);
        //       muzzle flash (MuzzleFlashMesh, appFrand into AnimFrame, 0x10b330d5-
        //       0x10b332c1) — loud failure: nothing writes MuzzleFlashMesh in
        //       Engine.u (decompile of all 146 classes) or in the game scripts, and
        //       campaign weapon classes default to None;
        //       bFirstFrame = false, OldFlashCount = FlashCount (0x10b332c7-0x10b332e7);
        //  4. PlayerReplicationInfo.HasFlag ≠ NULL (0x10b332ed-0x10b33306) — CTF
        //     flag: loud failure (nothing writes HasFlag in Engine.u or scripts).
        //
        // One body for the direct frame path (UEWorldFrameProducer.DirectRenderStep) and
        // the translated pass (RenderLevelPass.DrawMeshSprite, fixtures only).
        // identity — the DrawLodMesh condition 0x10b10136-0x10b10167: URender+0x3c ≠ 0 or
        // RendMap ∈ {13,14,15,1}. URender+0x3c (allocated zeroed, no native properties) is toggled only by the console
        // command REND WIRE (URender::Exec 0x10b1f240, entered with this = the FExec base at +0x28: `xor`ed at 0x10b1f5aa..0x10b1f5b4;
        // REND BLEND toggles +0x40 (0x10b1f612), REND BONE +0x44 (0x10b1f5de)) besides the copy constructor / operator=. Core owns no
        // REND state (D-76: the console REND debug views are not ported), so the condition reduces to RendMap.
        public int WeaponThirdPersonDraws;   // how many times the third-person weapon branch ran
        //
        // poses — frame vertices (WorldFrame.MeshPoses) from the same GetFrame: a skeletal mesh from the header
        // this ApplyAnim filled (no second call), a vertex mesh from ULodMesh::GetFrame. A third-person
        // weapon — the weapon mesh vertices during the swap, placed into the pawn's weapon-bone frame
        // (Render.dll.c:12072-12120: W /= weapon Rotation, Origin += weapon Location, W is the bone frame, its
        // world form is WeaponLoc/WeaponRot): p' = WeaponLoc + axes(WeaponRot)·(p − weapon Location).
        // Frame coords are always world: identity for RendMap 13/14/15/1 is a pixel detail.
        //
        // Runs once per DRAW — every frame (master, mirror, portal) whose sprite list holds the actor, in DrawFrame order,
        // like DrawActorSprite: each run is a GetFrame (ApplyAnim with its channel overlay, the vertex cache) and the
        // weapon state writes. request — the LOD level of this draw for the actor whose Mesh is being drawn (the pawn, or
        // the weapon during the swap; -1 = no LOD). Each pose added carries the drawn level as its single LodPart
        // (ViewSet unset): UEWorldFrameProducer merges the draws into per-actor parts.
        // frame — Frame->Coords of the FSceneNode drawing the actor (the GetFrame coords and the weapon point's frame).
        public void DrawMeshActor(UEObject o, bool identity, BrushCoords frame, List<UEObject> drawn) => DrawMeshActor(o, identity, frame, drawn, null, _ => -1);
        public void DrawMeshActor(UEObject o, bool identity, BrushCoords frame, List<UEObject> drawn, List<MeshPose> poses, Func<UEObject, int> request)
        {
            drawn.Add(o);
            bool weapon = RenderMeshFrame(o, identity, request(o), out SkelCache cache, out Vector3[] lodPoints, out int level);
            if (poses is not null)
                poses.Add(new MeshPose(o.ObjectId, false, WorldFrameProjector.AssetPath(o.GetProp(PK.Mesh)),
                    lodPoints ?? PosePointsFromCache(SkeletalMeshOf(o), cache), new[] { new MeshLodPart(level, -1) }));
            if (!weapon || !UEInterpreter.ClassIsA(o.Class, "Pawn")) return;
            RenderWeaponCoords(o, cache, frame, out Vector3 wloc, out UERotator wrot);
            o.Props["WeaponRot"] = wrot;
            o.Props["WeaponLoc"] = wloc;

            if (o.GetProp(PK.Weapon) is UEObject w
                && w.GetProp(PK.ThirdPersonMesh) is UEResourceRef
                && !UEInterpreter.ToBool(w.GetProp(PK.bHidden)))
            {
                WeaponThirdPersonDraws++;
                object mesh = w.GetProp(PK.Mesh), tpMesh = w.GetProp(PK.ThirdPersonMesh);
                object scale = w.GetProp(PK.DrawScale), tpScale = w.GetProp(PK.ThirdPersonScale);
                w.Props["ThirdPersonMesh"] = mesh; w.Props["Mesh"] = tpMesh;
                w.Props["ThirdPersonScale"] = scale; w.Props["DrawScale"] = tpScale;
                w.Props["Rotation"] = new UERotator(0, 0, 0);
                // FUN_10b338f0 — polygon flags, a pure function; the weapon DrawMesh
                // with the same identity: RendMap and URender+0x3c are the same.
                drawn.Add(w);
                RenderMeshFrame(w, identity, request(w), out SkelCache wc, out Vector3[] wLod, out int wLevel);
                if (poses is not null)
                {
                    Vector3[] pts = wLod ?? PosePointsFromCache(SkeletalMeshOf(w), wc);
                    Vector3 wl = UEInterpreter.AsVector3(w.GetProp(PK.Location));
                    var ax = UEValue.GetAxes(wrot);
                    for (int i = 0; i < pts.Length; i++)
                    {
                        float dx = pts[i].x - wl.x, dy = pts[i].y - wl.y, dz = pts[i].z - wl.z;
                        pts[i] = new Vector3(wloc.x + dx * ax.X.x + dy * ax.Y.x + dz * ax.Z.x,
                                             wloc.y + dx * ax.X.y + dy * ax.Y.y + dz * ax.Z.y,
                                             wloc.z + dx * ax.X.z + dy * ax.Y.z + dz * ax.Z.z);
                    }
                    poses.Add(new MeshPose(w.ObjectId, true, WorldFrameProjector.AssetPath(w.GetProp(PK.Mesh)), pts, new[] { new MeshLodPart(wLevel, -1) }));
                }
                mesh = w.GetProp(PK.Mesh); tpMesh = w.GetProp(PK.ThirdPersonMesh);
                scale = w.GetProp(PK.DrawScale); tpScale = w.GetProp(PK.ThirdPersonScale);
                w.Props["ThirdPersonMesh"] = mesh; w.Props["Mesh"] = tpMesh;
                w.Props["ThirdPersonScale"] = scale; w.Props["DrawScale"] = tpScale;
                bool steady = UEInterpreter.ToBool(w.GetProp(PK.bSteadyFlash3rd));
                if (steady) w.Props["bSteadyToggle"] = !UEInterpreter.ToBool(w.GetProp(PK.bSteadyToggle));
                bool flash = (steady && (!UEInterpreter.ToBool(w.GetProp(PK.bToggleSteadyFlash))
                                         || UEInterpreter.ToBool(w.GetProp(PK.bSteadyToggle))))
                    || (!UEInterpreter.ToBool(w.GetProp(PK.bFirstFrame))
                        && UEInterpreter.ToInt(w.GetProp(PK.FlashCount)) != UEInterpreter.ToInt(w.GetProp(PK.OldFlashCount)));
                if (flash && w.GetProp(PK.MuzzleFlashMesh) is not null)
                    throw new NotSupportedException(
                        $"DrawActorSprite: third-person muzzle flash (MuzzleFlashMesh, Render.dll 0x10b33103-0x10b332c1) not ported weapon={w.GetProp(PK.Name)}");
                w.Props["bFirstFrame"] = false;
                w.Props["OldFlashCount"] = (byte)UEInterpreter.ToInt(w.GetProp(PK.FlashCount));
            }
            if (o.GetProp(PK.PlayerReplicationInfo) is UEObject pri && pri.GetProp(PK.HasFlag) is UEObject)
                throw new NotSupportedException(
                    $"DrawActorSprite: PlayerReplicationInfo.HasFlag flag (Render.dll 0x10b332fb-0x10b334a3) not ported pawn={o.GetProp(PK.Name)}");
        }

        // ── NATIVE 257 BonePos(name) — AActor::execBonePos (Engine.dll.c:91802) ──
        // Mesh IsA SkeletalMesh → GetBoneCoords(actor, BoneIndex(name)).Origin;
        // else Location. BoneIndex (Engine.dll.c) — linear search by name,
        // −1 on a miss; GetBoneCoords with a negative index returns
        // GetMeshCoords, whose origin is Location + rotated (PrePivot + offset)
        // + rotated −Origin·Scale of the mesh, not Location.
        public Vector3 BonePosNative(UEObject self, string boneName)
        {
            if (self.GetProp(PK.Mesh) is not UEResourceRef meshRef
                || !meshRef.Class.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
                return UEInterpreter.AsVector3(self.GetProp(PK.Location));
            if (MeshCatalog is null)
                throw new InvalidOperationException($"BonePos {self.GetProp(PK.Name)}: skeletal mesh {meshRef.ObjectPath} but the runtime has no mesh catalog");
            Hp1SkeletalMesh mesh = MeshCatalog.Get(meshRef);
            return BoneWorldOf(self, mesh.FindBone(boneName ?? "")).Origin;
        }

        // ── TRACK SAMPLING (ApplyAnim 142200-142350, disasm 0x1041bf3c-) ──────
        // The key count is the track's KeyTime.Num (+0x18; track layout in
        // UAnimation::Serialize, Engine.dll.c:45418-45431), not the quaternion
        // count: the quaternion and position index is the key index. The position
        // of a single-key (+0x10 == 1) track is key 0 AS IS, no blend.
        internal static BonePlace SampleTrack(Hp1AnimTrack track, float sampleTime, float trackTime)
        {
            int keys = track.KeyTimes.Length;
            if (keys <= 0) throw new InvalidDataException("animation track without key times");
            BonePlace Key(int q)
            {
                if (q >= track.QuaternionCount)
                    throw new InvalidDataException($"animation key {q} beyond {track.QuaternionCount} quaternions");
                var r = new BonePlace();
                track.DecodeQuaternion(q, out r.QX, out r.QY, out r.QZ, out r.QW);
                r.Pos = PositionKey(track, q);
                return r;
            }
            if (keys == 1) return Key(0);

            bool pc24 = UEWorldFrameProducer.FastMathWindow;
            if (SeekKeys(track.KeyTimes, track.TimeScale, sampleTime, trackTime, pc24, out int lower, out int upper, out float alpha))
                return Key(lower);
            BonePlace a = Key(lower), b = Key(upper);
            var res = new BonePlace();
            SlerpQuat(a.QX, a.QY, a.QZ, a.QW, b.QX, b.QY, b.QZ, b.QW, alpha,
                out res.QX, out res.QY, out res.QZ, out res.QW);
            res.Pos = track.PositionCount == 1 ? a.Pos : LerpPosition(a.Pos, b.Pos, alpha, pc24);
            return res;
        }

        // Key search 0x1041c098..0x1041c0ff and alpha 0x1041c1f5..0x1041c21b. The accumulator 0 + Σ fild(KeyTime[k])
        // ·TimeScale lives in the register: `fcom sampleTime` reads it, `fst` stores only lowerTime. Past the last key:
        // WRAP, upper key 0 and upper bound TrackTime (float). alpha = (sampleTime − lowerTime) / (upper − lowerTime)
        // with the UNROUNDED upper bound, one fstp. Returns true for "the key as is" (lowerTime == sampleTime, or lower ==
        // upper after the wrap). The body sets no control word: pc24 = the render window, every operation in float.
        internal static bool SeekKeys(byte[] keyTimes, float timeScale, float sampleTime, float trackTime, bool pc24,
            out int lower, out int upper, out float alpha)
        {
            int keys = keyTimes.Length;
            alpha = 0f;
            upper = 1;
            if (pc24 || !float.IsFinite(timeScale) || !float.IsFinite(sampleTime) || !float.IsFinite(trackTime))
            {
                float lowerTime = 0f, upperTime = 0f;
                while (upper < keys)
                {
                    upperTime = lowerTime + keyTimes[upper] * timeScale;
                    if (sampleTime < upperTime) break;
                    upper++;
                    lowerTime = upperTime;
                }
                lower = upper - 1;
                if (lowerTime == sampleTime) return true;
                if (upper >= keys) { upper = 0; upperTime = trackTime; lower = keys - 1; }
                if (upper == lower) return true;
                alpha = (sampleTime - lowerTime) / (upperTime - lowerTime);
                return false;
            }
            HP1.Core.X87Ext acc = HP1.Core.X87Ext.From(0), st = HP1.Core.X87Ext.From(sampleTime);
            float lo = 0f;
            while (upper < keys)
            {
                acc = R64(acc + HP1.Core.X87Ext.From(keyTimes[upper]) * HP1.Core.X87Ext.From(timeScale));
                if (acc.CompareTo(st) > 0) break;
                lo = acc.ToFloat();
                upper++;
            }
            lower = upper - 1;
            if (lo == sampleTime) return true;
            if (upper >= keys) { upper = 0; acc = HP1.Core.X87Ext.From(trackTime); lower = keys - 1; }
            if (upper == lower) return true;
            HP1.Core.X87Ext l = HP1.Core.X87Ext.From(lo);
            alpha = HP1.Core.X87Ext.Div64(R64(st - l), R64(acc - l)).ToFloat();
            return false;
        }

        // FUN_1041f5c0: (1 − a) in the register; each component (1 − a)·lo + a·hi, one fstp dword (pc24: float steps).
        internal static Vector3 LerpPosition(Vector3 lo, Vector3 hi, float a, bool pc24)
        {
            if (pc24 || !float.IsFinite(a) || !float.IsFinite(lo.x) || !float.IsFinite(lo.y) || !float.IsFinite(lo.z)
                || !float.IsFinite(hi.x) || !float.IsFinite(hi.y) || !float.IsFinite(hi.z))
                return new Vector3(lo.x * (1f - a) + hi.x * a, lo.y * (1f - a) + hi.y * a, lo.z * (1f - a) + hi.z * a);
            HP1.Core.X87Ext A = HP1.Core.X87Ext.From(a), om = R64(HP1.Core.X87Ext.From(1f) - A);
            float C(float l, float h) => R64(R64(om * HP1.Core.X87Ext.From(l)) + A * HP1.Core.X87Ext.From(h)).ToFloat();
            return new Vector3(C(lo.x, hi.x), C(lo.y, hi.y), C(lo.z, hi.z));
        }

        static Vector3 PositionKey(Hp1AnimTrack track, int q)
        {
            if (track.PositionCount == 1) return track.DecodePosition(0);
            if (q >= track.PositionCount)
                throw new InvalidDataException($"animation key {q} beyond {track.PositionCount} positions");
            return track.DecodePosition(q);
        }

        // ── SlerpQuat — Core 0x1014f5a0..0x1014f795 ─────────────────────────
        // The body sets no control word: PC64 in game logic, PC24 inside the render window
        // (UEWorldFrameProducer.FastMathWindow). Disasm, a = esi (first quaternion), b = edi:
        //   cos = ((a.x·b.x + a.z·b.z) + a.y·b.y) + b.w·a.w in the register (0x1014f5ad..0x1014f5c7); fst dword cosF
        //     (only its sign is read back, 0x1014f640); |cos| in the register; !(|cos| < 1.0f) -> a as is (0x1014f772);
        //   θ = _CIacos(qword |cos|) (MSVCRT); fst dword θf; sinθ = appSin(qword θ) (bare fsin);
        //   inv = 1.0 (qword 0x1017d720) / sinθ, fstp dword;
        //   wa = appSin(qword((1.0f − t)·θf))·inv, fstp dword; wb = appSin(qword(θf·t))·inv stays in the register,
        //     negated when cosF < 0;
        //   r.x/y/z = wa·a + wb·b, fstp dword each; r.w = wb·b.w + wa·a.w, fst dword, the register value goes on:
        //   n² = ((w_reg·w_f + z·z) + y·y) + x·x in the register, fst dword n²f;
        //   |n² − 1| > 9.9999997e-6f (0x1017d764; C0|C3 skips) -> n²f < tol ? (0, 0, 0.1, 0) : r·(1.5 − n²f·0.5) with the
        //     factor in the register, one fstp dword per component.
        // Every register step is rounded to the window's precision; float*float products are exact before it.
        // TRANSCENDENTALS: UEX87Transcendental (fsin correctly rounded, _CIacos = msvcrt 6.00.8337.0; D-71, checked against the x87 table
        // measured on a Steam Deck).
        public static void SlerpQuat(float ax, float ay, float az, float aw,
            float bx, float by, float bz, float bw, float t,
            out float rx, out float ry, out float rz, out float rw)
        {
            bool pc24 = UEWorldFrameProducer.FastMathWindow;
            static X87Ext X(float v) => X87Ext.From(v);
            X87Ext R(X87Ext v) => pc24 ? X87Ext.Round24(v) : X87Ext.Round64(v);
            X87Ext cos = R(R(R(R(X(ax) * X(bx)) + R(X(az) * X(bz))) + R(X(ay) * X(by))) + R(X(bw) * X(aw)));
            float cosF = cos.ToFloat();
            X87Ext zero = X(0f), one = X(1f);
            X87Ext acos = (X87Ext.Fcom(cos, zero) & X87Ext.C0) != 0 ? -cos : cos;   // fchs 0x1014f5da
            if ((X87Ext.Fcom(acos, one) & X87Ext.C0) == 0) { rx = ax; ry = ay; rz = az; rw = aw; return; }
            // θf and inv depend only on the double _CIacos reads and the window: memoised per thread on its bits (a key
            // pair keeps its cos while the sample time moves between the keys). The arbitrary-precision reference
            // (NoFastPath) bypasses the memo.
            double cosD = acos.ToDouble();
            ulong key = (ulong)BitConverter.DoubleToInt64Bits(cosD) | (pc24 ? 1UL << 63 : 0);   // cosD >= 0: bit 63 free
            SlerpThetaMemo[] memo = _slerpThetaMemo ??= new SlerpThetaMemo[1 << 10];
            ref SlerpThetaMemo slot = ref memo[(int)((key * 0x9E3779B97F4A7C15UL) >> 54)];
            float thetaF, inv;
            if (slot.Set && slot.Key == key && !UEX87Transcendental.NoFastPath) { thetaF = slot.ThetaF; inv = slot.Inv; SlerpMemoHits++; }
            else
            {
                X87Ext theta = UEX87Transcendental.CIacos(X87Ext.From(cosD), pc24);
                thetaF = theta.ToFloat();
                X87Ext sinT = UEX87Transcendental.Fsin(X87Ext.From(theta.ToDouble()));
                inv = R(X87Ext.Div64(one, sinT)).ToFloat();
                if (!UEX87Transcendental.NoFastPath) slot = new SlerpThetaMemo { Key = key, Set = true, ThetaF = thetaF, Inv = inv };
            }
            X87Ext a0 = R(R(one - X(t)) * X(thetaF));
            float wa = R(UEX87Transcendental.Fsin(X87Ext.From(a0.ToDouble())) * X(inv)).ToFloat();
            X87Ext a1 = R(X(thetaF) * X(t));
            X87Ext wb = R(UEX87Transcendental.Fsin(X87Ext.From(a1.ToDouble())) * X(inv));
            if (cosF < 0f) wb = -wb;   // fchs 0x1014f654
            rx = R(R(X(wa) * X(ax)) + R(wb * X(bx))).ToFloat();
            ry = R(R(X(wa) * X(ay)) + R(wb * X(by))).ToFloat();
            rz = R(R(X(wa) * X(az)) + R(wb * X(bz))).ToFloat();
            X87Ext w = R(R(wb * X(bw)) + R(X(wa) * X(aw)));
            rw = w.ToFloat();
            X87Ext n2 = R(R(R(R(w * X(rw)) + R(X(rz) * X(rz))) + R(X(ry) * X(ry))) + R(X(rx) * X(rx)));
            float n2F = n2.ToFloat();
            X87Ext d = R(n2 - one);
            if ((X87Ext.Fcom(d, zero) & X87Ext.C0) != 0) d = -d;   // fchs 0x1014f6d3
            if ((X87Ext.Fcom(d, X(SlerpNormTolerance)) & (X87Ext.C0 | X87Ext.C3)) != 0) return;
            if (!(n2F >= SlerpNormTolerance)) { rx = 0f; ry = 0f; rz = 0.1f; rw = 0f; return; }
            X87Ext k = R(X(1.5f) - R(X(n2F) * X(0.5f)));
            rx = R(k * X(rx)).ToFloat(); ry = R(k * X(ry)).ToFloat();
            rz = R(k * X(rz)).ToFloat(); rw = R(k * X(rw)).ToFloat();
        }
        // SlerpQuat constants READ FROM THE Core.dll IMAGE (ImageBase 0x10100000):
        // 0x1017d71c = 0.0 (sign of cos), 0x1017d714 = 1.0 (|cos| threshold),
        // 0x1017d764 = 9.9999997e-06 (normalisation threshold), 0x1017d760 = 1.5,
        // 0x1017d734 = 0.5 (normalisation q·(1.5 − 0.5·‖q‖²)).
        const float SlerpNormTolerance = 9.999999747378752e-06f;

        struct SlerpThetaMemo { public ulong Key; public bool Set; public float ThetaF, Inv; }
        [ThreadStatic] static SlerpThetaMemo[] _slerpThetaMemo;
        [ThreadStatic] internal static long SlerpMemoHits;   // differential test only

        static float[] QuatBasis(float x, float y, float z, float w) => new[]
        {
            1 - 2 * (y * y + z * z), 2 * (x * y + w * z),     2 * (x * z - w * y),
            2 * (x * y - w * z),     1 - 2 * (x * x + z * z), 2 * (y * z + w * x),
            2 * (x * z + w * y),     2 * (y * z - w * x),     1 - 2 * (x * x + y * y),
        };

        // Row vector times matrix: v·M + O (as in ComputeReferencePosePoints).
        static Vector3 Transform(float[] m, Vector3 o, Vector3 v) => new Vector3(
            m[0] * v.x + m[3] * v.y + m[6] * v.z + o.x,
            m[1] * v.x + m[4] * v.y + m[7] * v.z + o.y,
            m[2] * v.x + m[5] * v.y + m[8] * v.z + o.z);

        // Hamilton product (child on the left) — exactly the composition
        // with which ComputeReferencePosePoints is proven on shipped points
        // (M_local·M_parent for bases of conjugate quaternions).
        static void QMul(float ax, float ay, float az, float aw, float bx, float by, float bz, float bw,
            out float x, out float y, out float z, out float w)
        {
            w = aw * bw - ax * bx - ay * by - az * bz;
            x = aw * bx + ax * bw + ay * bz - az * by;
            y = aw * by - ax * bz + ay * bw + az * bx;
            z = aw * bz + ax * by - ay * bx + az * bw;
        }
    }
}
