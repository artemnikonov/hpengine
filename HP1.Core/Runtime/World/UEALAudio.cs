// HP2 AUDIO DEVICE — UALAudioSubsystem (HP2 ALAudio.dll), the source table and the script-visible decisions.
//
// HP2's audio device is ALAudio (OpenAL), not Galaxy: a different UAudioSubsystem implementation, so it is its own
// class beside UEGalaxyAudio (HP1), as the two DLLs are. Core owns the table (which source a sound gets, its priority,
// volume, radius, pitch, fades, which plays are stopped, when a play ends); the frontend only executes the OpenAL calls
// this class logs (Commands), CLAUDE.md §2.
//
// Ported and judged under unicorn on the HP2 ALAudio.dll bodies (tools/hp2/alaudio_judge.py): PlaySound (source choice,
// clamps, the source fields and the OpenAL parameter calls), SoundPriority, FindLeastImportantSound, both StopSound
// overloads, ModifySound, PlayMusic (name, the PlaySound it makes, its answer), StopMusic, StopAllMusic, NoteDestroy,
// StopSoundSource -> UnregisterSound -> OldStopSound (the source clearing), SetViewport, Update (end of play, ambient
// rescan, actor following, fades, radius ramp, pause, listener), PauseSounds, UnpauseSounds.
// The end of a play is Core's (DECISIONS.md "HP2 ALAudio Update: option (a)"): the original asks OpenAL for the source
// state (the mixer's clock); here every logged call also drives a model of that OpenAL source on Core's clock (AlModel).
// Lip sync: FLipSyncData::GetMouthPos and the ALAudio player (LipSyncPlayer, Host: the actor's mouth channel). Not ported: buffer and stream resource bookkeeping (RegisterSound
// handles: a buffer's Sound.HandleFlags is an input; the stream refill is the frontend's).
//
// Source layout (0x5c bytes at UALAudioSubsystem+0x70, count +0x74): +0 Sound, +4 OpenAL source, +8 Actor,
// +0xc Location, +0x18 Priority, +0x1c Radius (10.0 for 0), +0x20/+0x24/+0x28 Radius, +0x2c timestamp, +0x34 Volume,
// +0x38 RawVolume, +0x3c FadeTime, +0x40 FadeElapsed, +0x44 FadeState (1 in, 2 out), +0x48 Flags, +0x4c Id, +0x50 started,
// +0x54 paused, +0x58 lip sync.
using System;
using System.Collections.Generic;
using HP1.Core;

namespace HP1.UEScript
{
    public sealed class UEALAudio : IUEAudioDevice
    {
        public sealed class Sound
        {
            public readonly string Path;
            public readonly uint DataFlags;   // USound+0xb4 (header flags)
            public uint Flags;                // USound+0xb8: +0xb4 | the PlaySound flags of the last play
            // A buffer's handle flags RegisterSound stores for the sound (+4: 2 when the WAV has smpl loops) — an input here.
            // A stream's are per play (Source.HandleFlags).
            public uint HandleFlags;
            // What one play gives OpenAL, in seconds at pitch 1 (DataLength); NaN for an OGG stream (only music, which loops).
            public double Length = double.NaN;
            public bool Ogg;                  // FileType "ogg" (an FFileStream file stream; music)
            public LipSyncData LipSync;       // +0x44 FLipSyncData (DataFlags & 0x80), from the caller's registry
            // The order the ambient rescan groups sounds in: the original sorts by the USound* address, which the port does
            // not have; the creation order stands in for it (STOP, DECISIONS.md "HP2 ALAudio ambient group order").
            public readonly int Order;
            public Sound(string path, uint dataFlags, int order = 0) { Path = path; DataFlags = dataFlags; Order = order; }
        }

        // Played length of one lap (Sound.Length). A WAV buffer plays its data chunk. An XA stream plays whole chunks of
        // 0x8000 bytes: RegisterSound [HP2 0x100037f4..0x1000381b] cuts (Bits/8)·NumSamples·Channels into
        // ((bytes - 1) >> 15) + 1 chunks and alBufferData-s every one at 0x8000 bytes (0x10003a68, 0x10003af2..0x10003b15;
        // refills 0x10005fed..0x10006004), the type-3 FFileStream read zero-filling the last [HP2 Core 0x10178390].
        public static double DataLength(bool streamed, int frames, int rate, int channels)
        {
            if (!streamed) return frames / (double)rate;
            long bytes = 2L * frames * channels;
            long chunks = bytes == 0 ? 0 : ((bytes - 1) >> 15) + 1;
            return chunks * (0x8000 / (2 * channels)) / (double)rate;
        }

        public sealed class Source
        {
            public Sound Sound;
            public int Actor;
            public UEVector Location;
            public float Priority, Radius, Radius20, Radius24, Radius28, Volume, RawVolume, FadeTime, FadeElapsed;
            public long Timestamp;            // +0x2c: FTime of the play, then of the last radius change (Update)
            public int FadeState, Id, F50, F54;   // +0x50 started, +0x54 paused
            public bool HeldForFocus;               // NOT ORIGINAL (D-51 for HP2): a song paused over a focus loss (SetViewport)
            public uint Flags;
            // The flags of the play's handle: a buffer's (Sound.HandleFlags); a stream's +0x10, (+0xb8 | +0xb4) at the
            // registration this play made (see PlaySound).
            public uint HandleFlags;
            // The play's stream (+0x88 table): +0x20 the decoder still delivers, +0x24 a restart request.
            public bool StreamAlive, StreamRestart;
            // The OpenAL source (AlModel).
            public int AlState = AlInitial;
            public double AlPos, AlLength = double.NaN;
            public long AlMark;
            public float AlPitch = 1f;
            public bool AlLooping, AlDataLoops;
            public LipSyncPlayer Lip;         // +0x58
        }

        // FLipSyncData (HP2 Engine) as the device reads it: the amplitudes (TLazyArray<BYTE> +0xc/+0x10), the owner sound's
        // duration (+0x18 -> USound +0x68) and the cached maximum (+0x1c, -1 until computed).
        public sealed class LipSyncData
        {
            public readonly byte[] Amplitudes;
            public readonly float Duration;
            int _max = -1;
            public LipSyncData(byte[] amplitudes, float duration) { Amplitudes = amplitudes; Duration = duration; }
            const float Interval = 0.02f;   // FLipSyncData::fAmpSampleInterval [HP2 Engine .rdata 0x1047abfc]
            const float Half = 0.5f;        // [HP2 Engine 0x104d6524]

            // Original: Engine.dll ?GetMouthPos@FLipSyncData@@QAEMM@Z [HP2 0x10388eb0] (thunk 0x103020ef)
            // fild amp; fidiv max; fmul 0.9f; fmul 0.7f — left in st0 (PC64), the caller stores it as float.
            public float GetMouthPos(float t)
            {
                int amp = GetInterpAmplitude(t), max = GetMaxAmplitude();
                if (max == 0) throw new InvalidOperationException("FLipSyncData::GetMouthPos: maximum amplitude 0 (0/0 in the original; no shipped carrier)");
                return R(R(X87Ext.Div64(I(amp), I(max)) * F(0.9f)) * F(0.7f)).ToFloat();
            }

            // Original: Engine.dll ?GetMaxAmplitude@FLipSyncData@@IAEEXZ [HP2 0x10389140] (thunk 0x103023dd)
            // Original: Engine.dll ?CalcMaxAmplitude@FLipSyncData@@IAEXXZ [HP2 0x10389170] (thunk 0x10304c73)
            int GetMaxAmplitude()
            {
                if (_max < 0) { _max = 0; for (int i = Amplitudes.Length - 1; i >= 0; i--) if (Amplitudes[i] > _max) _max = Amplitudes[i]; }
                return _max & 0xff;
            }

            // Original: Engine.dll ?GetInterpAmplitude@FLipSyncData@@IAEEM@Z [HP2 0x10388f30] (thunk 0x10304a9d)
            // t clamped to [0, Duration] (NaN -> 0); t + Interval > Duration: the nearest sample, index clamped; else the
            // linear blend of the samples at round(t/I - .5) and round((t+I)/I - .5) by (float)fmod(t, I) (msvcrt _CIfmod:
            // exact), truncated (_ftol) to its low byte. The blend's indices are not clamped (0x10389016..0x103890b7).
            int GetInterpAmplitude(float t)
            {
                float c = Lt(t, 0f) ? 0f : Lt(t, Duration) ? t : Duration;
                int n = Amplitudes.Length;
                if ((X87Ext.Fcom(R(F(c) + F(Interval)), F(Duration)) & (X87Ext.C0 | X87Ext.C3)) == 0)
                {
                    int i = Fistp(R(F(R(X87Ext.Div64(F(c), F(Interval))).ToFloat()) - F(Half)));
                    i = i < 0 ? 0 : i < n - 1 ? i : n - 1;
                    return Amplitudes[i];
                }
                int i0 = Fistp(R(F(R(X87Ext.Div64(F(c), F(Interval))).ToFloat()) - F(Half)));
                int i1 = Fistp(R(F(R(X87Ext.Div64(R(F(c) + F(Interval)), F(Interval))).ToFloat()) - F(Half)));
                if (i0 < 0 || i0 >= n || i1 < 0 || i1 >= n)
                    throw new InvalidOperationException($"FLipSyncData::GetInterpAmplitude: index {i0}/{i1} outside {n} amplitudes (the original reads past the array)");
                float frac = (float)((double)c % (double)Interval);
                X87Ext v = R(R(I(Amplitudes[i0]) * R(F(Interval) - F(frac))) + R(I(Amplitudes[i1]) * F(frac)));
                return (int)Math.Truncate(X87Ext.Div64(v, F(Interval)).ToDouble()) & 0xff;
            }

            // fistp dword at the default rounding (nearest even); out of range: the integer indefinite 0x80000000.
            static int Fistp(X87Ext v)
            {
                double d = Math.Round(v.ToDouble(), MidpointRounding.ToEven);
                return d >= int.MinValue && d <= int.MaxValue ? (int)d : int.MinValue;
            }
        }

        // The ALAudio lip-sync player of a source (+0x58, 0x18 bytes: +0 Actor, +4 Data, +8 start time, +0xc last update,
        // +0x10 a byte, +0x14 the mouth channel), driving the actor's mouth channel through Host.
        public sealed class LipSyncPlayer
        {
            public int Actor, Channel;
            public LipSyncData Data;
            public float Start, Last;
        }

        // What the lip-sync player does to the level: the actor's mouth channel and its animation (Core: UELevelRuntime).
        public interface ILipSyncHost
        {
            // AActor::CreateAnimChannel(AAnimChannel, AT_Replace, 'mouth_root', bAutoDestroy 0, bNotReplaceable 1): 0 = none.
            int CreateMouthChannel(int actor);
            // AActor::PlayAnim(Sequence, bLoop 0, Rate, TweenTime, MinRate, AT_Replace, None) on the channel.
            void PlayAnim(int channel, string sequence, float rate, float tweenTime, float minRate);
            void SetAnimFrame(int channel, float frame);   // channel AnimFrame (+0x68)
        }
        public ILipSyncHost LipHost;

        // Original: not exported: FUN_100075d0 [HP2 ALAudio 0x100075d0] (the player's start; called by PlaySound 0x100047d3)
        // StaticLoadObject(UClass, AActor, "AnimChannel") first: its result is not used (a class load, nothing kept).
        void LipStart(LipSyncPlayer p, float time)
        {
            p.Start = time; p.Last = 0f;
            ILipSyncHost h = LipHost ?? throw new InvalidOperationException("ALAudio lip sync: no LipHost");
            p.Channel = h.CreateMouthChannel(p.Actor);
            if (p.Channel != 0) h.PlayAnim(p.Channel, "mouth_anim", 0f, -1f, 0f);
        }

        // Original: not exported: FUN_10007580 [HP2 ALAudio 0x10007580] (the player's stop; called by OldStopSound 0x10006e96)
        void LipStop(LipSyncPlayer p)
        {
            if (p.Channel != 0) LipHost.PlayAnim(p.Channel, "mouth_rest", 1f, 1f, 1f);
            p.Actor = 0; p.Data = null;
        }

        // Original: not exported: FUN_10007670 [HP2 ALAudio 0x10007670] (the player's tick; Update 0x10006171)
        // t - Start kept in st0 (fst stores the float), compared unrounded with Last + 0.05; the float is the time used.
        void LipTick(LipSyncPlayer p, float time)
        {
            if (p.Channel != 0 && LipTickValue(p, time, out float frame)) LipHost.SetAnimFrame(p.Channel, frame);
        }

        // The tick's arithmetic (a player with a channel): false when under 0.05 s since the last mouth update.
        public static bool LipTickValue(LipSyncPlayer p, float time, out float frame)
        {
            frame = 0f;
            X87Ext since = R(F(time) - F(p.Start));
            float t = since.ToFloat();
            if ((X87Ext.Fcom(R(since - F(p.Last)), F(0.05f)) & X87Ext.C0) != 0) return false;
            p.Last = t;
            frame = p.Data.GetMouthPos(t);
            return true;
        }

        // An OpenAL call. Source -1: the listener. ListenerOrientation carries six values. Buffer (A != 0) and QueueBuffers
        // also name the data for the frontend: the sound's object path (music: the "..\music\<name>.ogg" file), whether it is
        // an OGG file stream, and whether the data loops by itself (a stream's type, AlModel) — the values Core's model uses.
        public readonly record struct Command(int Source, string Op, float A = 0, float B = 0, float C = 0,
            float D = 0, float E = 0, float F = 0, string Sound = null, bool Ogg = false, bool DataLoops = false);

        // FSceneNode Coords (+0x34) of the frame Update is given: the viewport's master frame (HP2 UGameEngine::Draw calls
        // Audio->Update right after CreateMasterFrame, before Render->PreRender: game logic precision, PC64).
        public readonly record struct Scene(UEVector Origin, UEVector XAxis, UEVector YAxis, UEVector ZAxis);

        // The actor fields the device reads (HP2 Actor: +0x38 Owner, +0xa8 AmbientSound, +0x11c Location, +0x14c Velocity,
        // +0x1e0 SoundRadius, +0x1e1 SoundVolume, +0x1e2 SoundPitch, +0x204 LightType, +0x206 LightBrightness).
        public sealed class Actor
        {
            public int Owner;
            public UEVector Location, Velocity;
            public Sound AmbientSound;
            public byte SoundRadius, SoundVolume, SoundPitch, LightType, LightBrightness;
        }

        // What the device reads of the world. Actor ids are the actors' object indices (UObject+4, the ambient Id is built
        // from it; 0 is None). ViewActor: Viewport->Actor (a PlayerPawn: ShowFlags +0x610, ViewTarget +0x620, Pauser +0x470
        // of its Level); LevelActors: XLevel->Actors of the listener actor (ViewTarget, else ViewActor).
        public interface IWorld
        {
            Actor Actor(int id);                   // null: not a valid actor
            int ViewActor { get; }
            int ViewTarget { get; }
            int ShowFlags { get; }
            bool Paused { get; }                   // Level.Pauser != ""
            IReadOnlyList<int> LevelActors { get; }
        }

        public readonly Source[] Sources;
        public float SoundVolume;   // +0xac
        public float MusicVolume;   // +0xa8
        public float Rolloff;       // +0xb0 (AL_ROLLOFF_FACTOR)
        public bool ReverseStereo;  // +0xb8
        public bool UseEAX;         // +0xc0
        public bool CompatibilityMode;   // +0xc4
        public float DopplerFactor; // +0xa4 (alDopplerFactor at Init: the frontend's device setup)
        public bool Muted;          // +0x5c (gain 0 on play)
        public bool RestartRequest; // +0x58 (Exec RESTARTSOUNDS sets it; Update clears it and unpauses)
        public bool HasViewport;    // +0x2c
        public IWorld World;
        // Core's clock, FTime (32.32 fixed seconds): the level's time. The original reads rdtsc / appSeconds (decision (a)).
        public long Clock;
        public readonly List<Command> Commands = new();
        int _counter;               // DAT_1000f8d4 (static, starts 0)
        int _soundOrder;
        long _time;                 // +0x34 FTime of the last Update (the constructor's clock until the first)
        UEVector _lastOrigin;       // +0x3c the listener location of the last Update
        int _prevGameplay, _prevPaused;   // +0x4c, +0x50
        float _rescan;              // +0x54 time since the last ambient rescan

        public UEALAudio(int sources, long clock = 0)
        {
            Sources = new Source[sources];
            for (int i = 0; i < sources; i++) Sources[i] = new Source();
            Clock = _time = clock;   // UALAudioSubsystem() [HP2 0x10002709..0x10002779]: +0x34 = appSeconds()
        }

        // Original: ALAudio.dll ?Init@UALAudioSubsystem@@UAEHXZ [HP2 0x10002f40]
        // The config properties (StaticConstructor 0x10002800: ReverseStereo +0xb8, Channels +0xb4, MusicVolume +0xa8,
        // SoundVolume +0xac, DopplerFactor +0xa4, RollOff +0xb0, UseEAX +0xc0, CompatibilityMode +0xc4) from
        // [ALAudio.ALAudioSubsystem] as GConfig answers LoadConfig (the port's settings overlay, else the install's Default.ini:
        // UEConfig.TryGetIni); a missing key is a loud failure, not a default. Init makes
        // min(Channels, 32) sources (alGenSources until an error: the model's device makes them all), fails with none
        // (Channels 0: "MaxChannels == 0", 0x10003056; below 0: no source, 0x1000316f): null, no device. RollOff <= 0 becomes
        // 1. Its alDistanceModel(AL_INVERSE_DISTANCE_CLAMPED) and alDopplerFactor are the frontend's device setup;
        // UsePrecache (+0xbc) is read by none of the ported bodies.
        public const int MaxSources = 32;   // Init's cap [HP2 0x10002f40]
        public static UEALAudio FromConfig(UEConfig config)
        {
            string Key(string k) => config.TryGetIni(UEConfig.ScopeSystem, "ALAudio.ALAudioSubsystem", k, out string s) ? s
                : throw new System.IO.InvalidDataException($"{config.InstallSystemDir}: [ALAudio.ALAudioSubsystem] has no {k} (nor the settings overlay)");
            float Float(string k) => float.Parse(Key(k), System.Globalization.CultureInfo.InvariantCulture);
            int channels = int.Parse(Key("Channels"), System.Globalization.CultureInfo.InvariantCulture);
            if (channels <= 0) return null;
            float rolloff = Float("RollOff");
            return new UEALAudio(Math.Min(channels, MaxSources))
            {
                ReverseStereo = bool.Parse(Key("ReverseStereo")),
                UseEAX = bool.Parse(Key("UseEAX")),
                CompatibilityMode = bool.Parse(Key("CompatibilityMode")),
                MusicVolume = Float("MusicVolume"),
                SoundVolume = Float("SoundVolume"),
                DopplerFactor = Float("DopplerFactor"),
                Rolloff = rolloff <= 0f ? 1f : rolloff,
            };
        }

        // A new USound seen by the device (Order: its creation order).
        public Sound NewSound(string path, uint dataFlags) => new Sound(path, dataFlags, _soundOrder++);

        public long LastUpdateTime => _time;
        public UEVector LastOrigin => _lastOrigin;
        public int PrevGameplay => _prevGameplay;
        public int PrevPaused => _prevPaused;
        public float RescanTime => _rescan;

        static X87Ext R(X87Ext v) => X87Ext.Round64(v);
        static X87Ext F(float v) => X87Ext.From(v);
        static X87Ext I(int v) => X87Ext.From(v);
        static float Mul(float a, float b) => (float)((double)a * b);   // fld; fmul; fstp dword (the product is exact)
        static bool Lt(float a, float b) => !(a >= b);                   // fcomp; test ah, 1 (C0: below or unordered)
        const float Scale = 0.0127f;          // [0x10009f18] UU to OpenAL units
        const float Inv255 = 1f / 255f;       // [0x10009fa4] 0x3b808081
        const float Inv64 = 0.015625f;        // [0x10009fa0]
        const float Pow2m32 = 1f / 4294967296f; // [0x10009f14] 0x2f800000: FTime to seconds
        const float Multiplier = 100f;        // GAudioMaxRadiusMultiplier: Core .data 100.0, never written
        Actor ActorOf(int id) => World.Actor(id)
            ?? throw new InvalidOperationException($"ALAudio: actor {id} is not a valid actor");
        UEVector Listener
        {
            get
            {
                int v = World.ViewTarget != 0 ? World.ViewTarget : World.ViewActor;
                return ActorOf(v).Location;
            }
        }

        // Original: ALAudio.dll ?SoundPriority@UALAudioSubsystem@@QAEMPAVUViewport@@VFVector@@MMH@Z [HP2 0x10006f10]
        // The x87 result stays in st0 (PC64): callers compare it unrounded. The listener: Viewport->Actor, or its
        // ViewTarget (+0x620), Location.
        public X87Ext SoundPriority(UEVector loc, float volume, float radius, uint flags)
        {
            X87Ext p = F(1f);
            if (radius != 0f)
            {
                UEVector l = Listener;
                X87Ext dx = R(F(l.x) - F(loc.x)), dy = R(F(l.y) - F(loc.y)), dz = R(F(l.z) - F(loc.z));
                X87Ext rm = R(F(radius) * F(Multiplier));
                X87Ext s = R(R(R(dz * dz) + R(dy * dy)) + R(dx * dx));
                p = R(F(1f) - X87Ext.Div64(s, R(rm * rm)));
                if ((X87Ext.Fcom(p, F(0.01f)) & X87Ext.C0) != 0) p = F(0.01f);
                else if ((X87Ext.Fcom(p, F(1f)) & X87Ext.C0) == 0) p = F(1f);
            }
            int ints = (int)((flags >> 5) & 4) + (int)((flags >> 4) & 1) + (int)((flags >> 2) & 2);
            return R(R(p * F(volume)) + X87Ext.From(ints));
        }

        // ════════════════ the OpenAL source model (decision (a)) ═════════════
        // OpenAL 1.1 source semantics on Core's clock, fed by every call this class logs (Al): alSourcePlay resumes a paused
        // source and (re)starts any other at 0; Pause freezes a playing one; Stop stops; Rewind is AL_INITIAL at 0; AL_PITCH
        // takes a positive value (0 is an invalid value, ignored); the data is attached by AL_BUFFER / alSourceQueueBuffers.
        // A playing source advances (Clock - mark)·2^-32·pitch seconds of data; it stops at the end of the data unless the
        // data loops: AL_LOOPING for a buffer, the stream type for a stream (XA type 4 when the registration flags have 2,
        // RegisterSound 0x1000381c..0x100038cd; OGG type 2 when they have 8, 0x10003524..0x10003536; both loop in
        // FFileStream::Read [HP2 Core 0x10178390]). The decoder is modelled as never late (no underrun): a stream's data
        // is all delivered by its first tick (IsStreamAlive false there).
        public const int AlInitial = 0x1011, AlPlaying = 0x1012, AlPaused = 0x1013, AlStopped = 0x1014;

        void Al(int slot, string op, float a = 0, float b = 0, float c = 0, float d = 0, float e = 0, float f = 0)
        {
            if (slot < 0) { Commands.Add(new Command(slot, op, a, b, c, d, e, f)); return; }
            Source s = Sources[slot];
            Sound data = op == "QueueBuffers" || (op == "Buffer" && a != 0f) ? s.Sound : null;
            Commands.Add(new Command(slot, op, a, b, c, d, e, f, data?.Path, data?.Ogg ?? false,
                op == "QueueBuffers" && (s.HandleFlags & (data.Ogg ? 8u : 2u)) != 0));
            switch (op)
            {
                case "Play":
                    AlAdvance(s);
                    if (s.AlState != AlPaused) s.AlPos = 0;
                    s.AlState = AlPlaying; s.AlMark = Clock;
                    break;
                case "Pause": AlAdvance(s); if (s.AlState == AlPlaying) s.AlState = AlPaused; break;
                case "Stop": s.AlState = AlStopped; break;
                case "Rewind": s.AlState = AlInitial; s.AlPos = 0; break;
                case "Pitch": AlAdvance(s); if (a > 0f) s.AlPitch = a; break;
                case "Looping": s.AlLooping = a != 0f; break;
                case "Buffer":
                    s.AlLength = a != 0f ? s.Sound.Length : double.NaN;
                    s.AlDataLoops = false;
                    break;
                case "QueueBuffers":
                    s.AlLength = s.Sound.Length;
                    s.AlDataLoops = (s.HandleFlags & (s.Sound.Ogg ? 8u : 2u)) != 0;
                    break;
            }
        }

        void AlAdvance(Source s)
        {
            if (s.AlState != AlPlaying) return;
            s.AlPos += (Clock - s.AlMark) * (1.0 / 4294967296.0) * s.AlPitch;
            s.AlMark = Clock;
            if (!s.AlLooping && !s.AlDataLoops && s.AlPos >= s.AlLength) s.AlState = AlStopped;
        }

        // Original: ALAudio.dll ?PlaySound@UALAudioSubsystem@@UAEHPAVAActor@@HPAVUSound@@VFVector@@MMMHMM@Z [HP2 0x100040f0]
        public int PlaySound(int actor, int id, Sound sound, UEVector loc, float volume, float radius, float pitch,
            uint flags, float fadeTime, float priority)
        {
            if (radius == 0f) throw new InvalidOperationException("ALAudio PlaySound: assertion Radius failed (ALAudio.cpp:716)");
            if (sound is not null) sound.Flags = sound.DataFlags | flags;
            if (!HasViewport || sound is null) return -1;
            float v = volume * SoundVolume;
            v = v < 0f ? 0f : v < 1f ? v : 1f;
            int kind = id & 0xe;
            X87Ext p = kind == 10 ? F(100f)
                : priority != 0f && kind == 8 ? F(priority)
                : SoundPriority(loc, v, radius, flags);
            float stored = p.ToFloat();
            if (kind == 0) { _counter--; id = _counter * 16; }
            int slot = -1;
            X87Ext cur = p;
            for (int i = 0; i < Sources.Length; i++)
            {
                Source s = Sources[i];
                if (((s.Id ^ id) & ~1) == 0)
                {
                    if ((id & 1) != 0) return -1;
                    slot = i;
                    break;
                }
                if ((X87Ext.Fcom(F(s.Priority), cur) & X87Ext.C0) != 0) { cur = F(s.Priority); slot = i; }
            }
            if (slot == -1)
            {
                slot = FindLeastImportantSound(sound, id, stored);
                if (slot == -1) return -1;
            }
            StopSoundSource(slot);
            // RegisterSound (vtable +0x68, its only caller [HP2 0x10004340]): a buffer is registered once per sound; a stream
            // registers anew unless USound::IsRegistered [HP2 Engine 0x10304778] finds a free stream-handle slot
            // (0x1000345d). Slots are added only there (AddStreamHandle 0x10003680 / 0x100039b3), bound right after by this
            // PlaySound (BindStream 0x10004354) and deleted with their play (UnregisterSound: UnbindStream +
            // DeleteStreamHandle 0x1000403c / 0x10004048), so no slot is ever free here: every streamed play, a second live
            // play of the same sound included, gets its own stream with +0x10 = (+0xb8 | +0xb4) of this play
            // (0x1000346b..0x10003485, 0x100036a8 OGG, 0x100039d6 XA), and its stream starts delivering (+0x20 = 1).
            pitch = pitch < 0.1f ? 0.1f : pitch < 2f ? pitch : 2f;
            float gv = v < 0.01f ? 0.01f : v < 1f ? v : 1f;
            Source src = Sources[slot];
            src.Radius20 = src.Radius24 = src.Radius28 = radius;
            src.Sound = sound;
            src.Id = id;
            src.Actor = actor;
            src.Priority = stored;
            src.Location = (flags & 0x10) == 0 ? loc : default;
            src.Radius = radius == 0f ? 10f : radius;
            src.Timestamp = Clock;
            src.FadeTime = fadeTime;
            src.FadeElapsed = 0f;
            src.FadeState = 0f < fadeTime ? 1 : 0;
            src.Volume = (flags & 8) == 0 ? gv : MusicVolume;
            src.RawVolume = (flags & 8) == 0 ? volume : MusicVolume;
            src.F50 = 1; src.F54 = 0;
            bool streamed = (sound.Flags & 4) != 0;
            src.HandleFlags = streamed ? sound.Flags : sound.HandleFlags;
            src.Flags = src.HandleFlags | flags;
            src.StreamAlive = streamed; src.StreamRestart = false;
            // +0xb8 & 0x80 with an actor: a FLipSyncData player on the actor's anim channel (0x10004562..0x100045a4,
            // 0x100075d0: CreateAnimChannel, PlayAnim, then the mouth position every Update).
            if ((sound.Flags & 0x80) != 0 && actor != 0)
                src.Lip = new LipSyncPlayer
                {
                    Actor = actor,
                    Data = sound.LipSync
                    ?? throw new InvalidOperationException($"ALAudio PlaySound: {sound.Path} has +0xb8 & 0x80 but no FLipSyncData")
                };
            Al(slot, "ReferenceDistance", src.Radius24 * 0.0127f);
            Al(slot, "Gain", Muted ? 0f : src.FadeState == 1 ? 0f : src.Volume);
            Al(slot, "Pitch", pitch);
            Al(slot, "Looping", streamed ? 0 : (flags >> 1) & 1);
            Al(slot, "Position", src.Location.x * 0.0127f, src.Location.y * 0.0127f, src.Location.z * 0.0127f);
            Al(slot, "Velocity");
            Al(slot, "Rolloff", Rolloff);
            Al(slot, "Relative", (flags & 0x10) != 0 ? 1 : 0);
            Al(slot, streamed ? "QueueBuffers" : "Buffer", 1);
            Al(slot, "Stop");
            Al(slot, "Rewind");
            Al(slot, "Play");
            // The player starts at appSeconds as float seconds (0x10004750..0x100047d3: fild FTime; fmul 2^-32; fstp dword).
            if (src.Lip is not null) LipStart(src.Lip, (X87Ext.From(Clock) * F(Pow2m32)).ToFloat());
            return slot;
        }

        // Original: ALAudio.dll ?FindLeastImportantSound@UALAudioSubsystem@@QAEHPAVUSound@@HM@Z [HP2 0x10007180]
        public int FindLeastImportantSound(Sound sound, int id, float priority)
        {
            var counts = new Dictionary<Sound, int>(ReferenceEqualityComparer.Instance);
            int most = 0; Sound crowded = null;
            foreach (Source s in Sources)
            {
                if (s.Id == 0 || (s.Id & 0xe) != 8) continue;
                int c = (s.Sound is not null && counts.TryGetValue(s.Sound, out int n) ? n : 0) + 1;
                if (s.Sound is not null) counts[s.Sound] = c;
                if (most < c) { most = c; crowded = s.Sound; }
            }
            int best = -1;
            X87Ext bestP = F(1000f);
            for (int i = 0; i < Sources.Length; i++)
            {
                Source s = Sources[i];
                if (s.Id == 0) continue;
                if (crowded is not null ? !ReferenceEquals(crowded, s.Sound) : (id & 0xe) == 8 && (s.Id & 0xe) != 8) continue;
                if (!(s.Priority < priority)) continue;
                X87Ext q = SoundPriority(s.Location, s.Volume, s.Radius, s.Sound.Flags);
                if ((X87Ext.Fcom(q, bestP) & X87Ext.C0) != 0) { bestP = F(q.ToFloat()); best = i; }
            }
            return best;
        }

        // Original: ALAudio.dll ?StopSoundSource@UALAudioSubsystem@@QAEXH@Z [HP2 0x10006e00]
        public void StopSoundSource(int slot)
        {
            Source s = Sources[slot];
            if (s.Sound is not null) UnregisterSound(s.Sound, s.Id);
        }

        // Original: ALAudio.dll ?UnregisterSound@UALAudioSubsystem@@UAEXPAVUSound@@H@Z [HP2 0x10003dc0]
        // The source loop only; the buffer / stream release after it is resource bookkeeping (not ported).
        void UnregisterSound(Sound sound, int id)
        {
            for (int i = 0; i < Sources.Length; i++)
            {
                Source s = Sources[i];
                if (id == -1 ? s.Sound is not null && ReferenceEquals(s.Sound, sound) : s.Sound is not null && s.Id == id)
                    OldStopSound(i);
            }
        }

        // Original: ALAudio.dll ?OldStopSound@UALAudioSubsystem@@QAEXH@Z [HP2 0x10006e30]
        void OldStopSound(int slot)
        {
            Source s = Sources[slot];
            if (s.Id == 0) return;
            Al(slot, "Stop");
            Al(slot, "Buffer", 0);
            if (s.Lip is not null) { LipStop(s.Lip); s.Lip = null; }   // 0x10006e8d..0x10006ea9
            s.Sound = null; s.Actor = 0; s.Flags = 0; s.Priority = 0f; s.Id = 0; s.F50 = 0; s.F54 = 0;
        }

        // Original: ALAudio.dll ?StopSound@UALAudioSubsystem@@UAEHPAVAActor@@PAVUSound@@@Z [HP2 0x10004820]
        public bool StopSound(int actor, Sound sound)
        {
            bool any = false;
            if (actor == 0 && sound is null)
            {
                for (int i = 0; i < Sources.Length; i++) StopSoundSource(i);
                return true;
            }
            for (int i = 0; i < Sources.Length; i++)
            {
                Source s = Sources[i];
                bool hit = sound is null ? actor != 0 && actor == s.Actor
                    : (actor == 0 || actor == s.Actor) && ReferenceEquals(sound, s.Sound);
                if (hit) { StopSoundSource(i); any = true; }
            }
            return any;
        }

        bool Matches(Source s, int actor, int id, Sound sound)
            => s.Actor == actor && ((((s.Id ^ id) & ~1) == 0) || ((id & 0xe) == 0 && s.Id < 0))
               && (sound is null || ReferenceEquals(s.Sound, sound));

        // Original: ALAudio.dll ?StopSound@UALAudioSubsystem@@UAEHPAVAActor@@HPAVUSound@@M@Z [HP2 0x100048c0]
        public bool StopSound(int actor, int id, Sound sound, float fadeTime)
        {
            bool any = false;
            for (int i = 0; i < Sources.Length; i++)
            {
                Source s = Sources[i];
                if (!Matches(s, actor, id, sound)) continue;
                if (0f < fadeTime) { s.FadeElapsed = 0f; s.FadeTime = fadeTime; s.FadeState = 2; }
                else StopSoundSource(i);
                any = true;
            }
            return any;
        }

        // Original: ALAudio.dll ?ModifySound@UALAudioSubsystem@@UAEHPAVAActor@@HPAVUSound@@EM@Z [HP2 0x100049e0]
        public bool ModifySound(int actor, int id, Sound sound, byte param, float value)
        {
            bool any = false;
            for (int i = 0; i < Sources.Length; i++)
            {
                Source s = Sources[i];
                if (!Matches(s, actor, id, sound)) continue;
                if (param == 0)
                {
                    float v = value * SoundVolume;
                    v = v < 0.01f ? 0.01f : v < 1f ? v : 1f;
                    s.Volume = v;
                    Al(i, "Gain", v);
                }
                else if (param == 1)
                {
                    s.Radius20 = s.Radius24 = s.Radius28 = value;
                    Al(i, "ReferenceDistance", s.Radius24 * 0.0127f);
                }
                else if (param == 2)
                    Al(i, "Pitch", value < 0.1f ? 0.1f : value < 2f ? value : 2f);
                any = true;
            }
            return any;
        }

        // Original: ALAudio.dll ?NoteDestroy@UALAudioSubsystem@@UAEXPAVAActor@@@Z [HP2 0x10004b80]
        public void NoteDestroy(int actor)
        {
            if (actor == 0) throw new InvalidOperationException("ALAudio NoteDestroy: assertion Actor failed");
            for (int i = 0; i < Sources.Length; i++)
            {
                Source s = Sources[i];
                if (s.Actor != actor) continue;
                if ((s.Id & 0xe) == 8) StopSoundSource(i);
                else s.Actor = 0;
            }
        }

        // Original: ALAudio.dll ?Exec@UALAudioSubsystem@@UAEHPBGAAVFOutputDevice@@@Z [HP2 0x100067b0]
        // The words in the body's order: ASTAT, PAUSESOUNDS, UNPAUSESOUNDS, RESTARTSOUNDS, WEAPONRADIUS, ROLLOFF,
        // PLAYINGSOUNDSTATS, SETVOLUMES, SOUNDSOURCESTATES, MUTESOUNDS, UNMUTESOUNDS; no word -> false (0: the chain goes on).
        // Ported: SETVOLUMES [0x10006b29..0x10006c65], MUTESOUNDS [0x10006cde..0x10006d2f], UNMUTESOUNDS
        // [0x10006d31..0x10006d7f] — the ones HP2 scripts issue; the others stop loudly. Nothing is written to the output.
        // Original: ALAudio.dll ?PostEditChange@UALAudioSubsystem@@UAEXXZ [HP2 0x10002b20]
        // SoundVolume then MusicVolume clamped to [0, 1] (below 0 or NaN -> 0, not below 1 -> 1), then SetVolumes
        // [HP2 0x10006df0], an empty body: no source's gain changes. GlobalSetProperty calls it on the live device after
        // a SET (UEConsoleCommand).
        public void PostEditChange()
        {
            static float C(float v) => !(v >= 0f) ? 0f : v >= 1f ? 1f : v;
            SoundVolume = C(SoundVolume);
            MusicVolume = C(MusicVolume);
        }

        // fld RawVolume; fmul SoundVolume; fcom 0.0 / fcom 1.0 on the extended product [0x10006c1a..0x10006c4f]: compared
        // exact, stored rounded; NaN -> 0.0.
        static float Clamp01Product(double v) => !(v >= 0.0) ? 0f : v >= 1.0 ? 1f : (float)v;

        public bool Exec(string command)
        {
            static bool Word(string c, string w) => UEConsoleCommand.ParseCommand(c, w, out _);
            foreach (string w in new[] { "ASTAT", "PAUSESOUNDS", "UNPAUSESOUNDS" })
                if (Word(command, w))
                    throw new NotSupportedException($"ALAudio Exec \"{command}\": the {w} branch is not ported");
            // RESTARTSOUNDS [0x10006862..0x10006889]: +0x58 = 1 (the restart request Update reads, 0x10005733), answer 1.
            // UGameEngine::SaveGame's callers issue it after a save (HP2 Exec SAVEGAME 0x103a29a6, Tick 0x103a9cc4).
            if (Word(command, "RESTARTSOUNDS")) { RestartRequest = true; return true; }
            foreach (string w in new[] { "WEAPONRADIUS", "ROLLOFF", "PLAYINGSOUNDSTATS" })
                if (Word(command, w))
                    throw new NotSupportedException($"ALAudio Exec \"{command}\": the {w} branch is not ported");
            if (Word(command, "SETVOLUMES"))
            {
                // Parse(Cmd, "SOUND=", float&) / "MUSIC=" (appStrfind + appAtof, value untouched without the key), each
                // clamped: fcomp 0.0 / test ah,1 (below or unordered -> 0.0), then fcomp 1.0 (not below -> 1.0).
                static float Clamp01(float v) => !(v >= 0f) ? 0f : v >= 1f ? 1f : v;
                if (UEValue.AppStrfind(command, "SOUND=") is int si && si >= 0)
                    SoundVolume = Clamp01(UEValue.LeadingAtof(command.Substring(si + 6)));
                if (UEValue.AppStrfind(command, "MUSIC=") is int mi && mi >= 0)
                    MusicVolume = Clamp01(UEValue.LeadingAtof(command.Substring(mi + 6)));
                // Every source with an Id: music (Flags & 8) gets MusicVolume, else Clamp01(RawVolume * SoundVolume) (the
                // x87 product of two floats is exact, then rounded once at the fstp); alSourcef(AL_GAIN, Volume), Muted or not.
                for (int i = 0; i < Sources.Length; i++)
                {
                    Source s = Sources[i];
                    if (s.Id == 0) continue;
                    s.Volume = (s.Flags & 8) != 0 ? MusicVolume : Clamp01Product((double)s.RawVolume * SoundVolume);
                    Al(i, "Gain", s.Volume);
                }
                return true;
            }
            if (Word(command, "SOUNDSOURCESTATES"))
                throw new NotSupportedException($"ALAudio Exec \"{command}\": the SOUNDSOURCESTATES branch is not ported");
            bool mute = Word(command, "MUTESOUNDS");
            if (!mute && !Word(command, "UNMUTESOUNDS")) return false;
            Muted = mute;
            for (int i = 0; i < Sources.Length; i++)
                if (Sources[i].Id != 0) Al(i, "Gain", mute ? 0f : Sources[i].Volume);
            return true;
        }

        // Original: ALAudio.dll ?PlayMusic@UALAudioSubsystem@@UAEHVFString@@M@Z [HP2 0x10004c70]
        // A new transient USound per call: FileType "ogg", flags 0xc, file "..\music\<name>" (+ ".ogg" unless its
        // extension is ogg); played as PlaySound(None, 1, it, (0,0,0), MusicVolume, 1000, 1, 0x1c, fade, 0).
        // Answer: its source + 1, 0 for "" / "none" or when no source took it.
        public int PlayMusic(string name, float fadeTime, out Sound music)
        {
            music = null;
            name ??= "";
            if (name.Length == 0 || name.Equals("none", StringComparison.OrdinalIgnoreCase)) return 0;
            string file = @"..\music\" + name;
            if (!Ext(name).Equals("ogg", StringComparison.OrdinalIgnoreCase)) file += ".ogg";
            // A new sound is registered inside its PlaySound, after +0xb8 = 0xc | 0x1c: the stream's flags are 0x1c.
            music = NewSound(file, 0xc);
            music.Flags = 0xc; music.Ogg = true;
            PlaySound(0, 1, music, default, MusicVolume, 1000f, 1f, 0x1c, fadeTime, 0f);
            for (int i = 0; i < Sources.Length; i++)
                if (ReferenceEquals(Sources[i].Sound, music)) return i + 1;
            return 0;
        }

        // Original: Core.dll ?appFExt@@YAPBGPBG@Z [HP2 0x101521f0] (thunk 0x101035e4)
        // Past the first ':', then past every '/', then past every '.'; a name without '.' is its own "extension".
        static string Ext(string s)
        {
            int c = s.IndexOf(':'); if (c >= 0) s = s.Substring(c + 1);
            int p = s.LastIndexOf('/'); if (p >= 0) s = s.Substring(p + 1);
            int d = s.LastIndexOf('.'); return d < 0 ? s : s.Substring(d + 1);
        }

        // Original: ALAudio.dll ?StopMusic@UALAudioSubsystem@@UAEHHM@Z [HP2 0x100050c0]
        public int StopMusic(int sourcePlusOne, float fadeTime)
        {
            int i = sourcePlusOne - 1;
            if (i >= Sources.Length || i < 0) return 0;
            if (0f < fadeTime) { Source s = Sources[i]; s.FadeElapsed = 0f; s.FadeTime = fadeTime; s.FadeState = 2; return 1; }
            StopSoundSource(i);
            return 1;
        }

        // Original: ALAudio.dll ?StopAllMusic@UALAudioSubsystem@@UAEHM@Z [HP2 0x100051b0]
        public int StopAllMusic(float fadeTime)
        {
            for (int i = 0; i < Sources.Length; i++)
                if ((Sources[i].Flags & 8) != 0) StopMusic(i + 1, fadeTime);
            return 0;
        }

        // Original: ALAudio.dll ?SetViewport@UALAudioSubsystem@@UAEXPAVUViewport@@@Z [HP2 0x10003330]
        // A started source (+0x50) stops; the viewport is stored (and the zone region +0x64 cleared: read only by
        // SetI3DL2Listener, an empty body).
        // NOT ORIGINAL (D-51 for HP2, decided by the coordinator 2026-11-16, option (b)): on a focus loss (NULL) a started
        // music source (Flags & 8) is paused instead of stopped and is played again on return, so the song resumes where it
        // was (the original stops it and nothing re-requests it); the script's music handle stays valid. Every other source
        // stops as shipped, and a held song is not played while the game is paused (+0x54: UnpauseSounds plays it).
        public void SetViewport(bool viewport)
        {
            for (int i = 0; i < Sources.Length; i++)
            {
                Source s = Sources[i];
                if (s.HeldForFocus)
                {
                    if (!viewport) continue;
                    s.HeldForFocus = false;
                    if (s.F50 != 0 && s.F54 == 0) Al(i, "Play");   // not if script stopped it meanwhile
                    continue;
                }
                if (s.F50 == 0) continue;
                if (!viewport && (s.Flags & 8) != 0) { s.HeldForFocus = true; Al(i, "Pause"); continue; }
                StopSoundSource(i);
            }
            HasViewport = viewport;
        }

        // Original: ALAudio.dll ?PauseSounds@UALAudioSubsystem@@QAEXXZ [HP2 0x10005250]
        void PauseSounds()
        {
            for (int i = 0; i < Sources.Length; i++)
                if (Sources[i].Id != 0) { Sources[i].F54 = 1; Al(i, "Pause"); }
        }

        // Original: ALAudio.dll ?UnpauseSounds@UALAudioSubsystem@@QAEXXZ [HP2 0x10005290]
        void UnpauseSounds()
        {
            for (int i = 0; i < Sources.Length; i++)
            {
                Source s = Sources[i];
                if (s.Id == 0) continue;
                s.F54 = 0;
                if (s.F50 != 0) Al(i, "Play");
            }
        }

        // ════════════════ UPDATE (once per frame) ════════════════════════════
        // Original: ALAudio.dll ?Update@UALAudioSubsystem@@UAEXPAUFSceneNode@@@Z [HP2 0x100052d0]
        // The device reads Clock where the original reads its own clock (DECISIONS.md "HP2 ALAudio Update: option (a)");
        // UModel::PointRegion(Level, Origin) -> +0x64 (0x100055a8..0x100055f2) is read only by SetI3DL2Listener, an empty
        // body [HP2 0x100070d0], and the appSqrt of the frame's X axis (0x10005506..0x1000553c) is discarded: not ported.
        public void Update(in Scene scene)
        {
            if (!HasViewport) return;
            IWorld w = World;
            UEVector o = scene.Origin;
            // Listener axes "at" and "up" (alListenerfv AL_ORIENTATION), up = at x X (0x10005306..0x100054ab).
            UEVector x = ReverseStereo ? new UEVector(-scene.XAxis.x, -scene.XAxis.y, -scene.XAxis.z) : scene.XAxis;
            float atx, aty, atz, upx, upy, upz;
            if (!UseEAX || CompatibilityMode)
            {
                atx = -scene.YAxis.x; aty = -scene.YAxis.y; atz = scene.YAxis.z;
                Cross(atx, aty, atz, x.x, -x.y, x.z, out X87Ext c1, out X87Ext c2, out X87Ext c3);
                upx = c3.ToFloat(); upy = c2.ToFloat(); upz = (-c1).ToFloat();
            }
            else
            {
                atx = scene.ZAxis.x; aty = scene.ZAxis.y; atz = scene.ZAxis.z;
                Cross(atx, aty, atz, x.x, x.y, -x.z, out X87Ext c1, out X87Ext c2, out X87Ext c3);
                upx = c3.ToFloat(); upy = c2.ToFloat(); upz = c1.ToFloat();
            }
            X87Ext s2 = R(R(F(aty) * F(aty) + F(atx) * F(atx)) + F(atz) * F(atz));
            if ((X87Ext.Fcom(s2, F(1e-8f)) & X87Ext.C0) == 0)
            {
                X87Ext inv = InvSqrt(s2);
                atx = R(F(atx) * inv).ToFloat(); aty = R(F(aty) * inv).ToFloat(); atz = R(F(atz) * inv).ToFloat();
            }
            s2 = R(R(F(upx) * F(upx) + F(upy) * F(upy)) + F(upz) * F(upz));
            if ((X87Ext.Fcom(s2, F(1e-8f)) & X87Ext.C0) == 0)
            {
                X87Ext inv = InvSqrt(s2);
                upx = R(F(upx) * inv).ToFloat(); upy = R(F(upy) * inv).ToFloat(); upz = R(F(upz) * inv).ToFloat();
            }
            // Frame time: FTime difference, clamped to [0.0001, 1] (0x100055f5..0x100056a6).
            long now = Clock;
            float dt = R(X87Ext.From(now) * F(Pow2m32) - X87Ext.From(_time) * F(Pow2m32)).ToFloat();
            _time = now;
            if (Lt(dt, 0.0001f)) dt = 0.0001f;
            else if (!Lt(dt, 1f)) dt = 1f;
            // Pause (0x100056a6..0x1000574b): the viewport actor's ShowFlags & 0x4800 (game running) and its Level's
            // Pauser. The third unpause trigger (+0x58): Exec RESTARTSOUNDS's request, cleared here (0x10005733..0x1000573a).
            if (w.ViewActor == 0)
                throw new InvalidOperationException("ALAudio Update: Viewport->Actor is None (its Level is read without a test, 0x100056c4)");
            int gameplay = (w.ShowFlags & 0x4800) != 0 ? 1 : 0;
            int paused = w.Paused ? 1 : 0;
            if ((gameplay == 0 && _prevGameplay != 0) || (paused != 0 && _prevPaused == 0)) PauseSounds();
            else if ((gameplay != 0 && _prevGameplay == 0) || (paused == 0 && _prevPaused != 0)) UnpauseSounds();
            else if (RestartRequest) { RestartRequest = false; UnpauseSounds(); }
            _prevGameplay = gameplay; _prevPaused = paused;
            // End of play (0x1000574e..0x100058f7): alGetSourcei(AL_SOURCE_STATE) == AL_STOPPED.
            for (int i = 0; i < Sources.Length; i++)
            {
                Source s = Sources[i];
                if (s.Id == 0 || s.Sound is null) continue;
                AlAdvance(s);
                if (s.AlState != AlStopped) continue;
                if ((s.Flags & 0xa) != 0)
                    // The original sets the restart request of streams[GetHandle(Sound, Id) - 1] (0x100058b2..0x100058c5): for a
                    // buffer that indexes the stream table with a buffer handle; for a stream the restart replays its queued
                    // buffers — both need the resource layer (not ported). No shipped play reaches it: a looping play of a
                    // shipped sound loops by its data (no shipped WAV has smpl loops; a stream registers with the play's flags).
                    throw new NotSupportedException($"ALAudio Update: stopped source {i} with flags 0x{s.Flags:x} & 0xa (restart of a non-looping buffer/stream) is not ported (STOP, DECISIONS.md \"HP2 ALAudio Update\")");
                if ((s.Flags & 4) == 0) StopSoundSource(i);
                else if (!s.StreamAlive) UnregisterSound(s.Sound, s.Id);   // vtable +0x6c (0x100058cf..0x100058da)
                else s.StreamRestart = true;   // "WARNING: audio buffer underrun" (Logf) and the stream's +0x24
            }
            // Ambient rescan every 0.2 s (0x100058fc..0x10005e81).
            X87Ext acc = R(F(dt) + F(_rescan));
            _rescan = acc.ToFloat();
            if ((X87Ext.Fcom(acc, F(0.2f)) & (X87Ext.C0 | X87Ext.C3)) == 0)
            {
                _rescan = 0f;
                if (gameplay != 0 && paused == 0) Rescan(o, w);
                // An ambient source out of its radius, or whose actor's ambient sound changed, stops (0x10005d72..0x10005e0a).
                for (int i = 0; i < Sources.Length; i++)
                {
                    Source s = Sources[i];
                    if ((s.Id & 0xe) != 8 || s.Actor == 0) continue;
                    Actor a = ActorOf(s.Actor);
                    X87Ext rm = R(F(s.Radius20) * F(Multiplier));
                    if ((X87Ext.Fcom(Dist2(a.Location, o), R(rm * rm)) & (X87Ext.C0 | X87Ext.C3)) == 0
                        || !ReferenceEquals(a.AmbientSound, s.Sound))
                        StopSoundSource(i);
                }
            }
            // Per source (0x10005e84..0x1000653d).
            for (int i = 0; i < Sources.Length; i++)
            {
                Source s = Sources[i];
                if (s.Actor != 0 && w.Actor(s.Actor) is null)
                    throw new InvalidOperationException("ALAudio Update: assertion Sources[i].Actor->IsValid() failed (ALAudio.cpp:1588)");
                if (s.Id == 0) continue;
                if (s.F54 != 0)
                {
                    // A paused source's player starts later by the frame time (FUN_100076b0: fld dt; fadd +8; fstp +8).
                    if (s.Lip is not null) s.Lip.Start = R(F(dt) + F(s.Lip.Start)).ToFloat();
                    continue;
                }
                if ((s.Flags & 4) != 0)
                {
                    // The refill (AL_BUFFERS_PROCESSED, FFileStream chunks, 0x10005f09..0x1000607c) is the frontend's: no
                    // buffer is processed in the model, and IsStreamAlive is false from here on.
                    s.StreamAlive = false;
                    if (s.StreamRestart) { Al(i, "Play"); s.StreamRestart = false; }
                }
                // The mouth (0x10006152..0x10006171): Update's FTime as float seconds.
                if (s.Lip is not null) LipTick(s.Lip, (X87Ext.From(now) * F(Pow2m32)).ToFloat());
                float vol = s.Volume;
                float vx = 0f, vy = 0f, vz = 0f;
                if (s.Actor != 0 && (s.Flags & 0x40) == 0)
                {
                    Actor a = ActorOf(s.Actor);
                    s.Location = a.Location;
                    if ((s.Flags & 0x10) == 0) { vx = Mul(a.Velocity.x, Scale); vy = Mul(a.Velocity.y, Scale); vz = Mul(a.Velocity.z, Scale); }
                    if ((s.Flags & 0x20) != 0) Al(i, "Pitch", a.SoundPitch * Inv64);
                    if ((s.Id & 0xe) == 8) s.Volume = vol = R(I(a.SoundVolume) * F(SoundVolume) * F(Inv255)).ToFloat();
                    if (a.LightType != 0) vol = R(I(a.LightBrightness) * F(vol) * F(Inv255)).ToFloat();
                }
                if (s.FadeState == 1)
                {
                    if (Lt(dt, 1f)) s.FadeElapsed = R(F(dt) + F(s.FadeElapsed)).ToFloat();
                    if (Lt(s.FadeElapsed, s.FadeTime)) vol = R(X87Ext.Div64(F(s.FadeElapsed), F(s.FadeTime)) * F(vol)).ToFloat();
                    else s.FadeState = 0;
                }
                if (s.FadeState == 2)
                {
                    if (Lt(dt, 1f)) s.FadeElapsed = R(F(dt) + F(s.FadeElapsed)).ToFloat();
                    if (!Lt(s.FadeElapsed, s.FadeTime)) { StopSoundSource(i); continue; }
                    vol = R(R(F(1f) - X87Ext.Div64(F(s.FadeElapsed), F(s.FadeTime))) * F(vol)).ToFloat();
                }
                if (!Muted) Al(i, "Gain", vol);
                if ((s.Flags & 0x10) == 0)
                {
                    // Reference distance: a radius change ramps over 1 s from +0x24 to +0x1c (0x100063af..0x10006430).
                    float rad = s.Radius;
                    s.Radius20 = rad;
                    if (rad < s.Radius28 || rad > s.Radius28) { s.Timestamp = now; s.Radius28 = rad; }
                    long d = now - s.Timestamp;
                    X87Ext refd;
                    if ((X87Ext.Fcom(X87Ext.From(d) * F(Pow2m32), F(1f)) & X87Ext.C0) != 0)
                        refd = R(R(R(F(rad) - F(s.Radius24)) * X87Ext.From(d)) * F(Pow2m32) + F(s.Radius24));
                    else { refd = F(rad); s.Radius24 = rad; }
                    Al(i, "ReferenceDistance", R(refd * F(Scale)).ToFloat());
                }
                s.Priority = SoundPriority(s.Location, s.Volume, s.Radius20, s.Flags).ToFloat();
                if ((s.Flags & 0x10) == 0)
                {
                    Al(i, "Position", Mul(s.Location.x, Scale), Mul(s.Location.y, Scale), Mul(s.Location.z, Scale));
                    vx = Mul(vx, Scale); vy = Mul(vy, Scale); vz = Mul(vz, Scale);
                    X87Ext m2 = R(R(F(vx) * F(vx) + F(vz) * F(vz)) + F(vy) * F(vy));
                    if ((X87Ext.Fcom(X87Ext.Sqrt64(X87Ext.From(m2.ToDouble())), F(300f)) & X87Ext.C0) != 0)
                        Al(i, "Velocity", vx, vy, vz);
                }
            }
            // Listener (0x1000657b..0x100066cc): velocity from the last origin over the clamped frame time.
            X87Ext ldz = R(F(o.z) - F(_lastOrigin.z));
            float dyf = R(F(o.y) - F(_lastOrigin.y)).ToFloat();
            float dxf = R(F(o.x) - F(_lastOrigin.x)).ToFloat();
            X87Ext invDt = X87Ext.Div64(F(1f), F(dt));
            _lastOrigin = o;
            float invf = invDt.ToFloat();
            float lz = R(R(ldz * invDt) * F(Scale)).ToFloat();
            float ly = Mul(Mul(invf, dyf), Scale), lx = Mul(Mul(invf, dxf), Scale);
            Al(-1, "ListenerPosition", Mul(o.x, Scale), Mul(o.y, Scale), Mul(o.z, Scale));
            Al(-1, "ListenerOrientation", atx, aty, atz, upx, upy, upz);
            X87Ext l2 = R(R(F(lx) * F(lx) + F(lz) * F(lz)) + F(ly) * F(ly));
            if ((X87Ext.Fcom(X87Ext.Sqrt64(X87Ext.From(l2.ToDouble())), F(50f)) & X87Ext.C0) != 0)
                Al(-1, "ListenerVelocity", lx, ly, lz);
            // SetI3DL2Listener(Zone->ZoneEffect): an empty body [HP2 0x100070d0].
        }

        static void Cross(float atx, float aty, float atz, float xx, float xy, float xz, out X87Ext c1, out X87Ext c2, out X87Ext c3)
        {
            c1 = R(F(aty) * F(xx) - F(atx) * F(xy));
            c2 = R(F(atx) * F(xz) - F(xx) * F(atz));
            c3 = R(F(xy) * F(atz) - F(aty) * F(xz));
        }

        // 1 / appSqrt((double)s): fstp qword, appSqrt [HP2 Core 0x10125d20: fld qword; fsqrt], fstp qword, fld qword 1.0, fdiv.
        static X87Ext InvSqrt(X87Ext s)
            => X87Ext.Div64(X87Ext.From(1.0), X87Ext.From(X87Ext.Sqrt64(X87Ext.From(s.ToDouble())).ToDouble()));

        // (dz² + dy²) + dx² of loc - origin in the register (0x10005a41..0x10005a85, 0x10005da7..0x10005dd5).
        static X87Ext Dist2(UEVector loc, UEVector o)
        {
            X87Ext dx = R(F(loc.x) - F(o.x)), dy = R(F(loc.y) - F(o.y)), dz = R(F(loc.z) - F(o.z));
            return R(R(R(dz * dz) + R(dy * dy)) + R(dx * dx));
        }

        struct Entry { public int Actor; public float Priority; public uint Flags; public int Id, Src; }

        // The rescan (0x1000592b..0x10005d70): every level actor with an ambient sound and no source on its ambient Id
        // (Index·16 + 8) that is owned by the listener actor (flags 0x32) or within SoundRadius·100 of the frame origin
        // (0x22), with every playing ambient source; grouped by sound, each group sorted by priority: past the third
        // stops, the first three start.
        void Rescan(UEVector o, IWorld w)
        {
            int listener = w.ViewTarget != 0 ? w.ViewTarget : w.ViewActor;
            var list = new List<Entry>();
            foreach (int id in w.LevelActors)
            {
                if (id == 0) continue;
                Actor a = ActorOf(id);
                if (a.AmbientSound is null) continue;
                int amb = id * 16 + 8;
                bool playing = false;
                foreach (Source s in Sources) if (s.Id == amb) { playing = true; break; }
                if (playing) continue;
                bool owned = false;
                for (int k = id; k != 0; k = ActorOf(k).Owner) if (k == listener) { owned = true; break; }
                uint flags;
                if (owned) flags = 0x32;
                else
                {
                    X87Ext rr = R(I(a.SoundRadius) * F(Multiplier));
                    if ((X87Ext.Fcom(R(rr * rr), Dist2(a.Location, o)) & X87Ext.C0) != 0) continue;
                    flags = 0x22;
                }
                float vol = R(I(a.SoundVolume) * F(Inv255) * F(SoundVolume)).ToFloat();
                list.Add(new Entry
                {
                    Actor = id,
                    Priority = SoundPriority(a.Location, vol, a.SoundRadius, flags).ToFloat(),
                    Flags = flags,
                    Id = amb,
                    Src = -1
                });
            }
            for (int i = 0; i < Sources.Length; i++)
            {
                Source s = Sources[i];
                if (s.Id == 0 || (s.Id & 0xe) != 8) continue;
                list.Add(new Entry { Actor = s.Actor, Priority = s.Priority, Flags = s.Sound.Flags, Id = s.Id, Src = i });
            }
            if (list.Count == 0) return;
            Entry[] e = list.ToArray();
            Sort(e, 0, e.Length, BySound);
            for (int i0 = 0; i0 < e.Length;)
            {
                Sound snd = AmbientOf(e[i0].Actor);
                int j = i0;
                while (j < e.Length && ReferenceEquals(AmbientOf(e[j].Actor), snd)) j++;
                int n = j - i0;
                if (n > 1) Sort(e, i0, n, ByPriority);
                if (n > 3)
                    for (int k = i0 + 3; k <= j - 1; k++)
                        if (e[k].Src != -1) StopSoundSource(e[k].Src);
                for (int k = i0; k <= Math.Min(i0 + 2, j - 1); k++)
                {
                    if (e[k].Src != -1) continue;
                    Actor a = ActorOf(e[k].Actor);
                    float vol = (float)(a.SoundVolume * (double)Inv255);
                    if (PlaySound(e[k].Actor, e[k].Id, a.AmbientSound, a.Location, vol, a.SoundRadius, a.SoundPitch * Inv64,
                            e[k].Flags, 0f, e[k].Priority) == -1)
                        break;
                }
                i0 = j;
            }
        }

        Sound AmbientOf(int actor) => actor != 0 ? ActorOf(actor).AmbientSound
            : throw new InvalidOperationException("ALAudio rescan: an ambient source without an actor (the original reads None->AmbientSound)");

        // Original: not exported: FUN_100076c0 [HP2 ALAudio 0x100076c0] (the comparator of FUN_100080d0; mode DAT_1000e0c4).
        // Mode 0: the actors' AmbientSound pointers, larger first (-1 when B's is below A's, else 1).
        int BySound(Entry a, Entry b) => OrderOf(AmbientOf(b.Actor)) < OrderOf(AmbientOf(a.Actor)) ? -1 : 1;
        static int OrderOf(Sound s) => s is null ? -1 : s.Order;
        // Mode 1: fld B.Priority; fsub A.Priority; fcomp 0.0 — higher priority first (never 0: equal is 1).
        static int ByPriority(Entry a, Entry b) => b.Priority >= a.Priority ? 1 : -1;

        // Original: not exported: FUN_100080d0 [HP2 ALAudio 0x100080d0] (called only by Update, 0x10005bea and 0x10005c49)
        // The engine's in-place quicksort: up to 8 items a selection pass (the largest by the comparator to the end),
        // above the middle item as the pivot, the bigger half saved on a stack of 32, the smaller looped on.
        static void Sort(Entry[] a, int first, int num, Func<Entry, Entry, int> cmp)
        {
            if (num < 2) return;
            var stack = new (int Min, int Max)[32];
            stack[0] = (first, first + num - 1);
            for (int top = 0; top >= 0; top--)
            {
                (int min, int max) = stack[top];
                for (; ; )
                {
                    int count = max - min + 1;
                    if (count <= 8)
                    {
                        for (; max > min; max--)
                        {
                            int best = min;
                            for (int item = min + 1; item <= max; item++) if (cmp(a[item], a[best]) > 0) best = item;
                            (a[best], a[max]) = (a[max], a[best]);
                        }
                        break;
                    }
                    int mid = min + count / 2;
                    (a[mid], a[min]) = (a[min], a[mid]);
                    int inner = min, outer = max + 1;
                    for (; ; )
                    {
                        while (++inner <= max && cmp(a[inner], a[min]) <= 0) { }
                        while (--outer > min && cmp(a[outer], a[min]) >= 0) { }
                        if (inner > outer) break;
                        (a[inner], a[outer]) = (a[outer], a[inner]);
                    }
                    (a[min], a[outer]) = (a[outer], a[min]);
                    if (outer - 1 - min >= max - inner)
                    {
                        if (min + 1 < outer) stack[top++] = (min, outer - 1);
                        if (max > inner) { min = inner; continue; }
                    }
                    else
                    {
                        if (max > inner) stack[top++] = (inner, max);
                        if (min + 1 < outer) { max = outer - 1; continue; }
                    }
                    break;
                }
            }
        }
    }
}
