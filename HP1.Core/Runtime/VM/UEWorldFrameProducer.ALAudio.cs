// HP2 AUDIO FRAME PATH — what the HP2 engine frame hands the ALAudio device (UEALAudio): the clock, the world it reads
// and the master frame's Coords, plus the USound -> UEALAudio.Sound registry the device's callers use. HP2 only (HP1's
// device is Galaxy, driven by Build). Fields of the producer live in UEWorldFrameProducer.cs.
//
// Two call points, as in the original:
//   AdvanceAudioClock(DeltaSeconds) — the top of UGameEngine::Tick: the main loop's appSeconds() (Launch: NewTime, then
//     GEngine->Tick(NewTime - OldTime)). The device reads appSeconds itself (PlaySound's +0x2c stamp [HP2 ALAudio
//     0x1000475c..0x100047b9], Update's +0x34 [0x100055f5..0x10005665]); in the port that is ALAudio.Clock (DECISIONS.md
//     "HP2 ALAudio Update ported under decision (a)": a play started during the level tick has advanced 0 at that frame's
//     Update), so the clock moves once per engine frame, before the level tick.
//   UpdateAudio(level, coords) — UGameEngine::Draw [HP2 0x103a8aa0]: `Audio->Update(Frame)` (vtable +0x64, 0x103a8e2c..)
//     right after Render->CreateMasterFrame (+0x68) and before Render->PreRender (+0x60), Console->PreRender,
//     Canvas->Update, Actor.PreRender, DrawWorld and the PostRenders: sounds played by the HUD/console of this Draw are
//     seen by the next frame's Update.
using System;
using System.Collections.Generic;
using HP1.Core;
using UELib.Core;

namespace HP1.UEScript
{
    public sealed partial class UEWorldFrameProducer
    {
        UEALAudio Al => ALAudio ?? throw new InvalidOperationException("ALAudio: no Engine->Audio (InitAudio has not run or failed)");

        // FTime of a float delta: exact for every delta >= 2^-9 s (a float times 2^32 is an integer there).
        public void AdvanceAudioClock(float deltaSeconds) => Al.Clock += (long)((double)deltaSeconds * 4294967296.0);
        // UGameEngine::Tick advanced the clock for the Draw that follows it (UEGameEngineNatives.TickWorld).
        public void AdvanceAudioClockForTick(float deltaSeconds) { AdvanceAudioClock(deltaSeconds); _audioClockAdvancedByTick = true; }
        bool _audioClockAdvancedByTick;

        // Original: ALAudio.dll ?Update@UALAudioSubsystem@@UAEXPAUFSceneNode@@@Z [HP2 0x100052d0] (the call site:
        // Engine.dll ?Draw@UGameEngine@@UAEXPAVUViewport@@HPAEPAH@Z [HP2 0x103a8aa0])
        // coords: the master frame's Coords (FSceneNode +0x34, UERenderVisibility.MasterCoords of the PlayerCalcView camera).
        public void UpdateAudio(UELevelRuntime level, UEInterpreter interp, in BrushCoords coords)
        {
            BindAudioLevel(level, interp);
            Al.Update(new UEALAudio.Scene(coords.Origin, coords.X, coords.Y, coords.Z));
        }

        // The level the device reads (World) and the lip-sync player acts on (LipHost): before any device call of a level
        // (PlaySound of its first tick reads the listener). Rebinding the same level keeps the adapter.
        public void BindAudioLevel(UELevelRuntime level, UEInterpreter interp)
        {
            UEALAudio al = Al;
            if (al.World is ALWorld w && ReferenceEquals(w.Runtime, level) && ReferenceEquals(w.Interp, interp)) return;
            var nw = new ALWorld(this, level ?? throw new ArgumentNullException(nameof(level)), interp);
            al.World = nw; al.LipHost = nw;
        }

        // The OpenAL calls since the last published frame (WorldFrame.ALCommands), drained, and the device time the clock
        // advanced over that span (WorldFrame.AudioMixDt): the frontend mixes that much audio, then executes the calls.
        // Build publishes them on every frame it builds; a frame built elsewhere carries none and they wait for the next.
        long _alMixedClock;
        (UEALAudio.Command[] Calls, float MixDt) DrainALCommands()
        {
            UEALAudio al = Al;
            UEALAudio.Command[] calls = al.Commands.ToArray();
            al.Commands.Clear();
            float dt = (float)((al.Clock - _alMixedClock) * (1.0 / 4294967296.0));
            _alMixedClock = al.Clock;
            return (calls, dt);
        }

        // ── USound -> UEALAudio.Sound ─────────────────────────────────────────
        // One device record per USound object (by object path, case-insensitive as FName paths are), made the first time a
        // caller hands the device that sound. What RegisterSound [HP2 ALAudio 0x10003400] reads of it: +0xb4 (DataFlags),
        // FileType, the data (a WAV's data chunk: Length; smpl loops: buffer handle flags 2), NumSamples/Rate/Channels of a
        // streamed XA sound (Length in whole 0x8000-byte chunks, UEALAudio.DataLength).
        readonly Dictionary<string, UEALAudio.Sound> _alSounds = new(StringComparer.OrdinalIgnoreCase);

        public UEALAudio.Sound ALSound(string objectPath)
        {
            if (string.IsNullOrEmpty(objectPath)) return null;
            if (_alSounds.TryGetValue(objectPath, out UEALAudio.Sound s)) return s;
            var r = UESoundCatalog.Shared.HP2Registration(objectPath);
            s = Al.NewSound(objectPath, r.Flags);
            s.Length = UEALAudio.DataLength((r.Flags & 4) != 0, r.Frames, r.Rate, r.Channels);
            s.HandleFlags = (r.Flags & 4) == 0 && r.SampleLoops ? 2u : 0u;
            if ((r.Flags & 0x80) != 0)
                s.LipSync = new UEALAudio.LipSyncData(UESoundCatalog.Shared.HP2LipSync(objectPath),
                    UESoundCatalog.Shared.HP2HeaderOf(objectPath).Duration);
            _alSounds.Add(objectPath, s);
            return s;
        }

        // A sound property value (UEResourceRef) as the device's record; None -> null.
        public UEALAudio.Sound ALSound(object soundRef)
            => soundRef is UEResourceRef rr ? ALSound(rr.ObjectPath ?? rr.Name) : soundRef is null ? null
                : throw new InvalidOperationException($"ALAudio: {soundRef.GetType().Name} is not a sound reference");

        // What the device reads of the level (UEALAudio.IWorld): actors by object index, Viewport->Actor (the level's local
        // player), its ViewTarget (+0x620), ShowFlags (+0x610), its Level's Pauser (+0x470), XLevel->Actors.
        public UEALAudio.IWorld ALWorldOf(UELevelRuntime level, UEInterpreter interp = null) => new ALWorld(this, level, interp);
        sealed class ALWorld : UEALAudio.IWorld, UEALAudio.ILipSyncHost
        {
            readonly UEWorldFrameProducer _p;
            readonly UELevelRuntime _rt;
            public readonly UEInterpreter Interp;
            public UELevelRuntime Runtime => _rt;
            public ALWorld(UEWorldFrameProducer p, UELevelRuntime rt, UEInterpreter interp) { _p = p; _rt = rt; Interp = interp; }

            UEObject Live(int id) => _rt.FindActorById(id) ?? throw new InvalidOperationException($"ALAudio lip sync: actor {id} is not live");
            UEInterpreter In => Interp ?? throw new InvalidOperationException("ALAudio lip sync: the level is bound without an interpreter");
            public int CreateMouthChannel(int actor)
            {
                UClass cls = HP1.Core.UEClassRegistry.Shared.Find("Engine", "AnimChannel")
                    ?? throw new InvalidOperationException("ALAudio lip sync: class Engine.AnimChannel not loaded");
                return _rt.CreateAnimChannel(In, Live(actor), cls, 0, "mouth_root", autoDestroy: false, notReplaceable: true)?.ObjectId ?? 0;
            }
            public void PlayAnim(int channel, string sequence, float rate, float tweenTime, float minRate)
            {
                // The channel may be gone (its owner destroyed): the original calls into the actor memory regardless.
                if (_rt.FindActorById(channel) is UEObject c) _rt.PlayAnimFromCode(In, c, sequence, rate, tweenTime, minRate);
            }
            public void SetAnimFrame(int channel, float frame)
            {
                if (_rt.FindActorById(channel) is UEObject c) c.Props["AnimFrame"] = frame;
            }
            UEObject View => _rt.LocalPlayer;

            public UEALAudio.Actor Actor(int id)
            {
                UEObject a = id != 0 ? _rt.FindActorById(id) : null;
                if (a is null) return null;
                return new UEALAudio.Actor
                {
                    Owner = (a.GetProp(PK.Owner) as UEObject)?.ObjectId ?? 0,
                    Location = UEValue.AsVector3(a.GetProp(PK.Location)),
                    Velocity = UEValue.AsVector3(a.GetProp(PK.Velocity)),
                    AmbientSound = _p.ALSound(a.GetProp(PK.AmbientSound)),
                    SoundRadius = UEValue.AsByte(a.GetProp(PK.SoundRadius)),
                    SoundVolume = UEValue.AsByte(a.GetProp(PK.SoundVolume)),
                    SoundPitch = UEValue.AsByte(a.GetProp(PK.SoundPitch)),
                    LightType = UEValue.AsByte(a.GetProp(PK.LightType)),
                    LightBrightness = UEValue.AsByte(a.GetProp(PK.LightBrightness)),
                };
            }
            public int ViewActor => View?.ObjectId ?? 0;
            public int ViewTarget => (View?.GetProp(PK.ViewTarget) as UEObject)?.ObjectId ?? 0;
            public int ShowFlags => UEValue.ToInt(View?.GetProp(PK.ShowFlags));
            public bool Paused => !string.IsNullOrEmpty((View?.GetProp(PK.Level) as UEObject)?.GetProp(PK.Pauser)?.ToString());
            public IReadOnlyList<int> LevelActors
            {
                get
                {
                    var ids = new List<int>(_rt.Actors.Count);
                    foreach (UEObject a in _rt.Actors) ids.Add(a is null || a.Destroyed ? 0 : a.ObjectId);
                    return ids;
                }
            }
        }
    }
}
