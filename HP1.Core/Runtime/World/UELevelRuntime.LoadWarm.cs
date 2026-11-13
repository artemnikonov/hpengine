// LOAD-TIME WARM-UP OF FIRST-USE DATA CACHES.
//
// The first ticks of a level decoded shipped data lazily: skeletal meshes and their animation objects (first pose),
// the music export index (first song), sprite texture sizes (first frame), the Engine.dll
// export table (sprite sort key of a native class), and per script function the locals layout (UEPropLayout.ForStruct)
// and the port-override lookup (UEPortScriptOverrides.TryGet) — for the class chains of the level's actors. Each is a
// pure function of the install's bytes (or of the function object) cached by key; nothing here runs script, touches the
// RNG, writes an actor or emits an event, so filling the same caches at the end of world bring-up (behind the load
// screen) only moves the decode out of the visible frames. Off unless the host asks (UEGameEngine.Boot for the game): the validation harness
// keeps the lazy order, and its logs carry the UELib loader prints of the music index.
//
// A warm call that throws is ignored and leaves its cache as it was (none of these caches stores a failure), so the
// lazy call fails exactly where it failed before. NOT warmed: surface texture flags (SurfTexturePolyFlags) — they read
// the script-written texture proxy and build class-default probe objects (UEObject ids), so their moment is behaviour.
// HP1_LOAD_WARM=0 turns the warm-up off (A/B of the product path).
using System;
using System.Collections.Generic;
using UELib.Core;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        public bool WarmCachesOnLoad;
        static readonly bool LoadWarmDisabled = Environment.GetEnvironmentVariable("HP1_LOAD_WARM") == "0";
        // Diagnostic only: warm calls made, those that threw, the distinct exception kinds.
        public int LoadWarmCalls, LoadWarmExceptions;
        public readonly SortedSet<string> LoadWarmExceptionKinds = new(StringComparer.Ordinal);

        void WarmFirstUseCaches()
        {
            if (!WarmCachesOnLoad || LoadWarmDisabled) return;
            void Try(Action a)
            {
                LoadWarmCalls++;
                try { a(); }
                catch (Exception e) { LoadWarmExceptions++; LoadWarmExceptionKinds.Add(e.GetType().Name + ": " + e.Message); }
            }
            Try(UEMusicCatalog.Shared.WarmIndex);
            Func<string, (int, int)?> spriteSize = FrameProducer?.SpriteTextureSize;
            var reg = HP1.Core.UEClassRegistry.Shared;
            var classes = new HashSet<UClass>(UERefComparer<UClass>.Instance);
            var functions = new List<UFunction>();
            foreach (UEObject actor in Actors)
            {
                if (actor is null) continue;
                if (actor.Class is UClass actorClass && classes.Add(actorClass))
                {
                    Try(() => reg.SpriteSortKey(actorClass));
                    for (UClass c = reg.CanonicalSuper(actorClass); c is not null; c = reg.CanonicalSuper(c)) classes.Add(c);
                }
                if (MeshCatalog is not null && actor.GetProp(PK.Mesh) is UEResourceRef meshRef)
                {
                    if (meshRef.Class.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
                        Try(() =>
                        {
                            Hp1SkeletalMesh mesh = MeshCatalog.Get(meshRef);
                            // AnimationOf: Actor.SkelAnim, which ApplyAnim fills from the mesh DefaultAnimation when empty.
                            UEResourceRef anim = actor.GetProp(PK.SkelAnim) as UEResourceRef
                                ?? (mesh.AnimObject is null ? null : DefaultAnimationRef(meshRef, mesh));
                            if (anim is not null && anim.Class.Equals("Animation", StringComparison.OrdinalIgnoreCase))
                                MeshCatalog.GetAnimation(anim.Name, anim.ObjectPath);
                        });
                    else if (meshRef.Class.Equals("LodMesh", StringComparison.OrdinalIgnoreCase))
                        Try(() => MeshCatalog.GetVertexMesh(meshRef.Name, meshRef.ObjectPath));
                }
                if (spriteSize is not null)
                {
                    string tex = WorldFrameProjector.AssetPath(actor.GetProp(PK.Texture));
                    if (!string.IsNullOrEmpty(tex) && tex != "None") Try(() => spriteSize(tex));
                }
            }
            Try(() =>
            {
                foreach (UClass c in classes)
                {
                    functions.AddRange(c.EnumerateFields<UFunction>());
                    foreach (UState st in c.EnumerateFields<UState>())
                        if (st is not UClass) functions.AddRange(st.EnumerateFields<UFunction>());
                }
                UEPropLayout.WarmStructs(functions);
                UEPortScriptOverrides.Warm(functions);
            });
        }
    }
}
