// TEXTURE ANIMATION: Core owns every AnimNext chain's state (UETextureAnim: AnimCur, Accumulator, LastUpdateTime per
// texture). Shipped steps a chain in UTexture::Update(Viewport->CurrentTime) (Engine 0x10421070: an Update at the time it
// last ran returns at once), and every use of a texture in a frame reaches it: DrawFrame for each drawn surface's texture
// (WorldFrame.DrawnTextures), DrawMesh/DrawLodMesh for every slot texture and, through UTexture::Lock (Engine.dll.c:
// 97675-97678), the empty-slot texture, DrawActorSprite (UTexture::Get), the particle system's Textures[0]
// (FUN_10b15090), the decal texture, UCanvas::DrawTile. Core runs this frame's uses and publishes the frame each chain
// head shows (WorldFrame.TextureFrames); the frontend only looks it up. One viewport time, one Update per texture: the
// order of the uses does not matter. The clock is the viewport clock at the 60 Hz cadence (D-35, UETextureAnim).
using System;
using System.Collections.Generic;

namespace HP1.UEScript
{
    public sealed partial class UEWorldFrameProducer
    {
        const int DT_Sprite = 1, DT_Mesh = 2;
        UETextureAnim _textureAnim;
        Dictionary<string, string> _textureFrames = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<int, int> _snapshotOf = new();   // scratch: actor id -> index in the frame's snapshots
        // UEMeshSource.SlotTextures (UELevelRuntime.FrameProducer): the mesh's Textures, one entry per slot.
        public Func<string, string[]> MeshSlotTextures;

        internal UETextureAnim TextureAnim
            => _textureAnim ??= new UETextureAnim(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_systemDir)));

        // Viewport->Lock of a frame: the CurrentTime every Update of the frame gets.
        void BeginTextureFrame()
        {
            TextureAnim.BeginFrame(_viewportClock);
            _textureFrames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        // Fixture seam (runtime_poc SelfTest): a frame's Viewport->Lock without the rest of BuildFrame.
        public void BeginTextureFrameForTest(float viewportDelta) { _viewportClock += viewportDelta; BeginTextureFrame(); }
        public IReadOnlyDictionary<string, string> TextureFramesForTest => _textureFrames;

        // One use: Update(Viewport->CurrentTime), then the texture the use draws when it takes AnimCur (AnimCur ? AnimCur : this).
        // Original: Engine.dll ?Update@UTexture@@UAEXVFTime@@@Z [HP1 0x10421070]
        string TextureUse(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "None") return path;
            string cur = TextureAnim.CurrentFrame(path);
            if (TextureAnim.IsAnimated(path)) _textureFrames[path] = cur;
            return cur;
        }

        // The frame's world and actor uses, from the frame Core assembled (the snapshots name the textures the frontend draws).
        void TextureUses(WorldFrame f)
        {
            foreach (string t in f.DrawnTextures) TextureUse(t);
            _snapshotOf.Clear();
            for (int i = 0; i < f.Actors.Count; i++) if (!f.Actors[i].Destroyed) _snapshotOf[f.Actors[i].ObjectId] = i;
            // DrawActorSprite: Texture->Get(Viewport->CurrentTime) for DT_Sprite (Render.dll.c:12024). DT_SpriteAnimOnce draws the
            // life-fraction frame with its AnimNext/AnimCur zeroed for the Get (Render.dll.c:12002-12005): not stepped here.
            foreach (int id in f.SpriteActors)
                if (_snapshotOf.TryGetValue(id, out int i) && f.Actors[i].DrawType == DT_Sprite) TextureUse(f.Actors[i].TexturePath);
            // FUN_10b15090: Textures[0]->Update(CurrentTime) for each drawn emitter (Render.dll.c:9703-9713).
            string emitted = null;   // an emitter's particles share its Textures[0]: one use per run (allocation-free skip)
            foreach (ParticleInstance p in f.Particles)
                if (!ReferenceEquals(p.TexturePath, emitted)) TextureUse(emitted = p.TexturePath);
            foreach (DecalInstance d in f.Decals)
                if (_snapshotOf.TryGetValue(d.DecalId, out int i)) TextureUse(f.Actors[i].TexturePath);
            // DrawMesh/DrawLodMesh: every slot's GetTexture(i, Owner) is Updated (Render.dll.c:9241-9262), the empty-slot
            // texture is Locked (UTexture::Lock -> Update when the time changed).
            foreach (MeshPose mp in f.MeshPoses)
            {
                if (!_snapshotOf.TryGetValue(mp.ObjectId, out int i)) continue;
                ActorSnapshot a = f.Actors[i];
                if (!mp.Weapon && a.DrawType != DT_Mesh) continue;
                string[] slots = MeshSlotTextures?.Invoke(mp.MeshPath);
                if (slots is null) continue;
                for (int k = 0; k < slots.Length; k++) TextureUse(MeshSlotTexture(a, string.IsNullOrEmpty(slots[k]) ? null : slots[k], k));
                TextureUse(MeshFallbackTexture(a, slots, p => p));
            }
        }

        // UMesh::GetTexture(Count, Owner) as DrawMesh/DrawLodMesh call it per slot (vtable +0x88): Owner.MultiSkins[Count] if set,
        // then the mesh's own texture for a slot other than 0, then Owner.Skin, then the mesh texture; null — an empty slot.
        // Original: Engine.dll ?GetTexture@UMesh@@UAEPAVUTexture@@HPAVAActor@@@Z [HP1 0x1035baf0]
        public static string MeshSlotTexture(in ActorSnapshot a, string meshTexture, int count)
        {
            if (a.MultiSkins != null && count >= 0 && count < a.MultiSkins.Count
                && !string.IsNullOrEmpty(a.MultiSkins[count]) && a.MultiSkins[count] != "None")
                return a.MultiSkins[count];
            bool meshHas = !string.IsNullOrEmpty(meshTexture);
            if (count != 0 && meshHas) return meshTexture;
            if (!string.IsNullOrEmpty(a.Skin) && a.Skin != "None") return a.Skin;
            return meshHas ? meshTexture : null;
        }

        // Texture of an empty slot / an environment-mapped triangle (Render.dll 0x10b1174b-0x10b1183f; DrawMesh
        // Render.dll.c:9240-9268): the last non-null slot texture as the slot loop left it (after its AnimCur substitution:
        // slotFrame), overridden by Actor.Texture, else Region.Zone.EnvironmentMap, else Level.EnvironmentMap, as they are
        // (no AnimCur). null — the mesh is not drawn.
        // Original: Render.dll ?DrawLodMesh@URender@@QAEXPAUFSceneNode@@PAUFDynamicSprite@@PAVAActor@@ABVFCoords@@K@Z [HP1 0x10b0ff00] (block 0x10b1174b-0x10b1183f)
        // Original: Render.dll ?DrawMesh@URender@@QAEXPAUFSceneNode@@PAUFDynamicSprite@@PAVAActor@@ABVFCoords@@K@Z [HP1 0x10b0e920] (Render.dll.c:9240-9268)
        public static string MeshFallbackTexture(in ActorSnapshot a, string[] slotTable, Func<string, string> slotFrame)
        {
            string last = null;
            if (slotTable != null)
                for (int i = 0; i < slotTable.Length; i++)
                    last = MeshSlotTexture(a, string.IsNullOrEmpty(slotTable[i]) ? null : slotTable[i], i) ?? last;
            return MeshFallbackTexture(a.TexturePath, a.ZoneEnvironmentMap, a.LevelEnvironmentMap, last is null ? null : slotFrame(last));
        }
        public static string MeshFallbackTexture(string actorTexture, string zoneEnvMap, string levelEnvMap, string lastSlotTexture)
            => Tex(actorTexture) ?? Tex(zoneEnvMap) ?? Tex(levelEnvMap) ?? Tex(lastSlotTexture);
        static string Tex(string path) => string.IsNullOrEmpty(path) || path == "None" ? null : path;
    }
}
