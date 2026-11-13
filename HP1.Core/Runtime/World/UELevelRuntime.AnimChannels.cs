// Auxiliary animation channels (AnimChannelEntry): sync from AuxAnims, create, stop and destroy.
// Fields live in UELevelRuntime.cs.
using System;
using System.Collections.Generic;
using System.IO;
using UELib.Core;
using Vector3 = HP1.UEScript.UEVector;
using X87Ext = HP1.Core.X87Ext;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        // ---- AActor::CreateAnimChannel (native 265) --------------------------
        //
        // Auxiliary animation channels: a channel is a REAL spawned actor rooted
        // at a bone, so the whole thing is world state (spawn order, encroach,
        // hash residency, destroy path) and belongs here, not in a frontend.
        public sealed class AnimChannelEntry
        {
            public UEObject Object;
            public int BoneIndex;
            public bool AutoDestroy;
        }

        // AActor.AuxAnims (+0x6c) is a real actor property; the frame reads it from here.
        // Updated on every change of the owner's channel list.
        void SyncAuxAnims(UEObject owner)
        {
            if (owner is null) return;
            var list = new List<object>();
            if (_animChannels.TryGetValue(owner, out var channels))
                foreach (AnimChannelEntry e in channels) list.Add(e.Object);
            owner.Props["AuxAnims"] = list;
        }

        // Save load. In shipped the channel list IS the AuxAnims array (+0x6c/+0x70): CreateAnimChannel's dedup
        // and AT_Replace, PlayAnim's channel pass and ApplyAnim's overlay all walk it and read each channel's own
        // AnimBone (byte +0x64) and bAnimTransient (+0x28 bit 8) (Engine.dll.c:27782-27787, 27823-27824,
        // 73690-73691, 142947-142975). AuxAnims is not CPF_Transient (flags 0x00400000), so the save package
        // carries it and a load restores the channels with it; CreateAnimChannel (PostBeginPlay) does not run.
        // The port's side registry is rebuilt here from the loaded property, in array order.
        void RebuildAnimChannelsFromAuxAnims()
        {
            _animChannels.Clear();
            foreach (UEObject a in Actors)
            {
                if (a is null || a.Destroyed || a.GetProp(PK.AuxAnims) is not System.Collections.IList aux || aux.Count == 0)
                    continue;
                var channels = new List<AnimChannelEntry>(aux.Count);
                for (int i = 0; i < aux.Count; i++)
                {
                    if (aux[i] is not UEObject ch || ch.IsStruct || ch.Destroyed)
                        throw new InvalidDataException(
                            $"load: {a.GetProp(PK.Name)}.AuxAnims[{i}] is {(aux[i] is UEObject d && d.Destroyed ? "a destroyed actor" : aux[i]?.GetType().Name ?? "None")}"
                            + " — shipped ApplyAnim dereferences every entry; the outcome is not established (§4)");
                    channels.Add(new AnimChannelEntry
                    {
                        Object = ch,
                        BoneIndex = UEInterpreter.ToInt(ch.GetProp(PK.AnimBone)),
                        AutoDestroy = UEInterpreter.ToBool(ch.GetProp(PK.bAnimTransient)),
                    });
                }
                _animChannels[a] = channels;
            }
        }

        // Save/load journal: every owner's channels in Actors order — registry entry (channel name, bone, auto-destroy,
        // AnimSequence) and whether the registry equals the owner's AuxAnims array element for element.
        public string AnimChannelDigest()
        {
            var sb = new System.Text.StringBuilder();
            int owners = 0;
            foreach (UEObject a in Actors)
            {
                if (a is null || a.Destroyed || !_animChannels.TryGetValue(a, out var channels) || channels.Count == 0) continue;
                var aux = a.GetProp(PK.AuxAnims) as System.Collections.IList;
                bool same = aux is not null && aux.Count == channels.Count;
                for (int i = 0; same && i < channels.Count; i++) same = ReferenceEquals(aux[i], channels[i].Object);
                sb.Append(' ').Append(a.GetProp(PK.Name)).Append(same ? "=aux[" : "!=aux[");
                for (int i = 0; i < channels.Count; i++)
                    sb.Append(i == 0 ? "" : ",").Append(channels[i].Object.GetProp(PK.Name)).Append(':').Append(channels[i].BoneIndex)
                      .Append(':').Append(channels[i].AutoDestroy ? 1 : 0).Append(':').Append(channels[i].Object.GetProp(PK.AnimSequence));
                sb.Append(']');
                owners++;
            }
            return $"owners={owners}{sb}";
        }

        public IReadOnlyList<AnimChannelEntry> AnimChannelsOf(UEObject owner)
            => owner is not null && _animChannels.TryGetValue(owner, out var c)
                ? (IReadOnlyList<AnimChannelEntry>)c : System.Array.Empty<AnimChannelEntry>();

        // AT_Replace's pass over the auxiliary channels (Engine.dll.c:73688-73711).
        // It does NOT destroy them all. The shipped loop branches on the channel's own
        // auto-destroy bit (+0x28 bit 8):
        //   bit clear -> the channel SURVIVES; shipped only zeroes its +0x50
        //   bit set   -> ULevel::DestroyActor (vtable +0x98) and remove from the array
        public void DestroyAnimChannels(UEInterpreter interp, UEObject owner)
        {
            if (owner is null || !_animChannels.TryGetValue(owner, out var channels)) return;
            for (int i = channels.Count - 1; i >= 0; i--)
            {
                if (!channels[i].AutoDestroy)
                {
                    // The surviving branch clears the channel's own animation.
                    // actor+0x50 IS identifiable, from this very function: 73720
                    // compares it against the requested sequence name and 73774
                    // assigns the chosen one to it — so it is the actor's current
                    // AnimSequence, which this port already models by that name
                    // (SetAnimation clears it the same way, with 'None').
                    channels[i].Object.Props["AnimSequence"] = new UEName("None");
                    continue;
                }
                DestroyActor(interp, channels[i].Object);
                channels.RemoveAt(i);
            }
            SyncAuxAnims(owner);
        }

        public UEObject CreateAnimChannel(UEInterpreter interp, UEObject owner, UClass cls,
            int animType, string boneName, bool autoDestroy)
        {
            // Shipped order (Engine.dll.c:27768-27813): mesh and bone, look up an existing channel and ONLY
            // then SpawnActor(class) — a None class is not checked before the lookup (SpawnActor returns NULL);
            // the type branches only on `== 1` (Combine), anything else is Replace.
            if (owner is null) return null;
            if (cls is not null && HP1.Core.UEClassRegistry.IsImportStub(cls))
            {
                UClass canonical = HP1.Core.UEClassRegistry.Shared.FindForStub(cls);
                if (canonical is not null && !HP1.Core.UEClassRegistry.IsImportStub(canonical))
                    cls = canonical;
            }
            if (string.IsNullOrEmpty(boneName)
                || boneName.Equals("None", StringComparison.OrdinalIgnoreCase)) return null;

            if (owner.GetProp(PK.Mesh) is not UEResourceRef meshRef
                || !meshRef.Class.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase)) return null;
            (int boneIndex, int numChildren) = BoneLookupOf(meshRef, owner, boneName);
            if (boneIndex < 0) return null;

            if (!_animChannels.TryGetValue(owner, out var channels))
                _animChannels[owner] = channels = new List<AnimChannelEntry>();
            // Dedup like the shipped array walk (27782-27787): compare the CHANNEL
            // ACTORS' own AnimBone (+0x64) and bAnimTransient (+0x28 bit 0x100).
            // The hit RETURNS THE EXISTING CHANNEL — disassembly 0x10408727
            // `mov eax,[ebp-0x24]`; Ghidra shows only the shared epilogue call
            // (returning None here would be wrong).
            foreach (AnimChannelEntry e in channels)
                if (UEInterpreter.ToInt(e.Object.GetProp(PK.AnimBone)) == boneIndex
                    && UEInterpreter.ToBool(e.Object.GetProp(PK.bAnimTransient)) == autoDestroy)
                    return e.Object;

            // Shipped spawns through ULevel::SpawnActor at the OWNER's
            // location/rotation with Owner=this (27790-27802) — the full spawn
            // order applies — and only AFTER the spawn returns copies the owner's
            // Mesh and the bone/auto-destroy fields onto the channel (27804-27808).
            // Instigator = NULL there (27798), Tag = the class name set inside
            // SpawnActor.
            if (cls is null) return null;   // ULevel::SpawnActor(NULL class) -> NULL
            UEObject channel = SpawnActor(interp, owner, cls, owner, null,
                UEInterpreter.AsVector3(owner.GetProp(PK.Location)),
                UEInterpreter.AsRotator(owner.GetProp(PK.Rotation)));
            if (channel is null) return null;
            // After the spawn shipped copies onto the channel (27804-27808; names
            // by the property-offset oracle): Mesh (+0x164), SkelAnim (+0x68),
            // AnimBone (+0x64, the bone index as a byte) and bAnimTransient
            // (+0x28 bit 0x100) from the 5th argument.
            channel.Props["Mesh"] = owner.GetProp(PK.Mesh);
            channel.Props["SkelAnim"] = owner.GetProp(PK.SkelAnim);
            channel.Props["AnimBone"] = (byte)boneIndex;
            channel.Props["bAnimTransient"] = autoDestroy;

            // AT_Combine (1): the channel goes to the HEAD of AuxAnims and
            // nothing is removed (27809-27812). AT_Replace (0): channels whose
            // root lies inside the new root's contiguous RefSkeleton subtree are
            // destroyed through ULevel::DestroyActor (vtable +0x98), then the
            // new one is appended (27813-27852).
            if (animType == 1)
            {
                channels.Insert(0, new AnimChannelEntry
                    { Object = channel, BoneIndex = boneIndex, AutoDestroy = autoDestroy });
                SyncAuxAnims(owner);
                Journal?.Invoke($"anim_channel class={cls.Name} owner={owner.GetProp(PK.Name)} "
                    + $"mesh={meshRef.Name} bone={boneName}:{boneIndex} "
                    + $"autodestroy={autoDestroy.ToString().ToLowerInvariant()}");
                return channel;
            }
            int subtreeEnd = checked(boneIndex + numChildren);
            for (int i = channels.Count - 1; i >= 0; i--)
            {
                int existingRoot = channels[i].BoneIndex;
                if (existingRoot >= boneIndex && existingRoot < subtreeEnd)
                {
                    DestroyActor(interp, channels[i].Object);
                    channels.RemoveAt(i);
                }
            }
            channels.Add(new AnimChannelEntry
            {
                Object = channel, BoneIndex = boneIndex, AutoDestroy = autoDestroy
            });
            SyncAuxAnims(owner);
            Journal?.Invoke($"anim_channel class={cls.Name} owner={owner.GetProp(PK.Name)} "
                + $"mesh={meshRef.Name} bone={boneName}:{boneIndex} "
                + $"autodestroy={autoDestroy.ToString().ToLowerInvariant()}");
            return channel;
        }

        // AActor::PlayAnim(None, …, RootBone) (Engine.dll.c:73637-73672): every
        // channel rooted inside the bone's subtree is stopped — a persistent one
        // keeps living with AnimSequence=None, a transient one is destroyed.
        // Returns false when the bone does not exist (shipped returns 0).
        public bool StopAnimChannelsUnder(UEInterpreter interp, UEObject owner, string boneName)
        {
            if (owner?.GetProp(PK.Mesh) is not UEResourceRef meshRef) return false;
            (int boneIndex, int numChildren) = BoneLookupOf(meshRef, owner, boneName);
            if (boneIndex < 0) return false;
            if (!_animChannels.TryGetValue(owner, out var channels)) return true;
            for (int i = channels.Count - 1; i >= 0; i--)
            {
                int root = channels[i].BoneIndex;
                if (root < boneIndex || boneIndex + numChildren <= root) continue;
                if (!channels[i].AutoDestroy)
                {
                    channels[i].Object.Props["AnimSequence"] = new UEName("None");
                    continue;
                }
                DestroyActor(interp, channels[i].Object);
                channels.RemoveAt(i);
            }
            SyncAuxAnims(owner);
            return true;
        }
    }
}
