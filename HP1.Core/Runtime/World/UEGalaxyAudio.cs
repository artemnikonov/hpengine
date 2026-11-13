// UGalaxyAudioSubsystem IN CORE — the 16-channel table, PlaySoundW / StopSound / ModifySound / NoteDestroy /
// SetViewport and UGalaxyAudioSubsystem::Update (channel loop, bSongFinished loop, music block).
//
// Channels visible to script (ModifySound, bNoOverride rejection, bSongFinished, Transition) are owned by Core
// (the frontend only mixes). Galaxy logic and addresses are those of the ported bodies; the only change is that a voice was a
// mixer object:
//
//   * END OF VOICE. Every sound streams (USound::Serialize sets +0x70/+0x74 outside the editor, Engine.dll.c:71560-
//     71589; RegisterSound takes the stream branch, Galaxy.dll.c:5765): the voice is created by the first Update's
//     third loop (FUN_1060fbf0) on an empty 0x4000-frame ring and fed by the Galaxy stream thread (the FEEDER below).
//     Galaxy frees the channel when the voice goes off (FUN_1060a760(voice, 2) == 0, 0x1060872f-0x1060874b): when its
//     play position reaches the end the feeder armed after the data. Here Core runs the same feeder on its audio clock
//     (FeedVoice): the voice's first message sets the pre-roll lead (data starts lead frames into the ring, the
//     frontend plays that silence: Playback.PreRoll), the arming rule of FeederVoiceOff, the position advancing by the
//     voice step of each Update's Frequency.
//   * MUSIC. "Song stream exhausted" (SoundStream+8 IsFinished, vfunc +0xc) is set by the Galaxy FEEDER thread at the
//     short FillBuffer read, ahead of playback — modelled on Core's audio clock (FeederShortRead, D-72). The next
//     Update's music block stops the song (StopSound); a loop never ends. AUTHOR IMPROVEMENT (D-72, not original):
//     the stopped song's buffered audio plays out (Playback.TailPlays) until new music starts.
//
// The frontend receives a snapshot (Snapshot: 16 entries with Serial, sound, Volume/Pan/Frequency, Held) and only
// plays voices from it; it writes nothing into Core (window focus loss is input: SetViewport).
using System;
using System.Collections.Generic;

namespace HP1.UEScript
{
    // World state read by Update, and Galaxy writes into the view actor. Production implementation: UELevelRuntime.
    public interface IUEGalaxyWorld
    {
        UEAudioListener Listener { get; }
        UEGalaxyConfig Galaxy { get; }
        bool TryGetActor(int actorId, out UEVector location, out UEVector velocity);
        float MusicMaster { get; }
        // Sound: data frames, rate, channels (null — not resolved).
        UEGalaxyAudio.SoundInfo? Sound(string path);
        // Song: rate, channels and loop length in frames (null — stream failed to open).
        UEGalaxyAudio.SoundInfo? Song(string path);
        // Galaxy writes into the view actor (Galaxy.dll.c:7284-7311, 0x1060948b, 0x1060626a).
        bool WriteMusicFinished();
        void WriteSongFinishedFlag(bool finished);
        void WriteMusicStarted();
        void WriteViewportRestored();
        // Core ambients: channels dropped by viewport loss (UEWorldFrameProducer.NoteAudioChannelsLost).
        void NoteAudioChannelsLost();
    }

    public sealed class UEGalaxyAudio
    {
        // Default.ini:157 EffectsChannels=16.
        public const int EffectsChannels = 16;

        public readonly struct SoundInfo
        {
            public readonly long Frames; public readonly int SampleRate, Channels;
            public SoundInfo(long frames, int sampleRate, int channels) { Frames = frames; SampleRate = sampleRate; Channels = channels; }
        }

        // CHANNEL VOICE — what the frontend plays. Serial is the voice identity: a new voice gets a new number.
        public sealed class Playback
        {
            public readonly int Serial;
            public readonly string Sound;
            public readonly bool Loop, IsMusic;
            public readonly long Frames;
            public readonly int SampleRate, Channels;
            public double Pos { get; internal set; }            // data frames played as of the last Update
            public bool Playing { get; internal set; } = true;   // isPlaying: cleared by StopSound or end of data
            public bool Started { get; internal set; }          // survived at least one Update (the Miles voice is already sounding)
            public bool Ended { get; internal set; }            // end of data (not StopSound) — the tail plays out; music: IsFinished (short read)
            // Music: the Galaxy voice has played out to its armed end (+0xf == 0, FeederVoiceOff) — the third loop of the
            // next Update frees the channel. Only reached without StopSound (MusicVolume 0).
            public bool VoiceDone { get; internal set; }
            internal long VoiceOffMix = long.MaxValue;   // mixer frame at which the voice reaches the armed end
            public long VoiceOffMixForTest => VoiceOffMix;
            public bool Held { get; internal set; }             // D-52 (author improvement): held by menu pause
            // Feeder state of a sound's voice (FeedVoice): ring offset 16.16 and laps at mixer frame FeedMix, the lead (first message's
            // play + 0x400), the message that found the data end (-1), the armed end (-1), the previous message's play.
            internal long Pos16, Laps, FeedMix, Lead = -1, K0 = -1, ArmedEnd = -1, PrevL;
            // Music: channel dropped by eviction; voice created by the third Update loop (D-28).
            public bool Evicted { get; internal set; }
            public bool Voiced { get; internal set; }
            // Applied by Update: volume 0..0x7fff and Galaxy pan, playback rate (Hz).
            public int Volume { get; internal set; }
            public int Pan { get; internal set; }
            public int Frequency { get; internal set; }
            // NOT ORIGINAL (D-72): the song was stopped by the music block after its
            // natural end (IsFinished) and its remaining audio plays out. Core clears it when new music starts or the
            // viewport is lost. Script-visible state (channel, Song, bSongFinished) is as in the original: stopped.
            public bool TailPlays { get; internal set; }
            // Music feeder model: mixer frame at which the voice was created (third Update loop) and the song frame its
            // data starts from (0; D-51 resume — the data played at viewport loss).
            internal long VoiceMix0 = -1, DataBase;
            // Music: the Galaxy voice was (re)created Voicings times; the latest plays PreRoll frames of the empty ring
            // before its data (0x400 + p1, the feeder's first message — FeederPreRoll). The frontend executes it.
            public int Voicings { get; internal set; }
            public int PreRoll { get; internal set; }
            // D-51 resume (improvement, not original — the original restarts the song): the re-created voice continues
            // the stream seamlessly, without the feeder pre-roll.
            internal bool Resumed;
            // The Galaxy voices (pool index 0..19, FUN_1060fbf0) the channel plays through: the head and, for a stereo
            // sample, the linked second voice (+0x9c); -1 none. The mixer runs voices in index order (VoicePool).
            public int VoiceSlot { get; internal set; } = -1;
            public int VoiceSlot2 { get; internal set; } = -1;
            // S1: the voice the channel record points at (+0x0; may be another chain's linked voice after a steal) and its
            // chain's second voice, for the judge; the per-voice values the Updates wrote last (FUN_1060aae0 writes the
            // record's chain: a record pointing at another chain's linked voice writes that voice), for the frontend.
            public int RecordSlot { get; internal set; } = -1;
            public int RecordLinked { get; internal set; } = -1;
            public readonly int[] SlotVolume = new int[2], SlotPan = new int[2], SlotFrequency = new int[2];
            // the Galaxy voice was freed by another allocation (FUN_10615d40 on its head): its feeder stops
            internal bool VoiceLost;
            internal Playback(int serial, string sound, bool loop, bool music, SoundInfo info)
            {
                Serial = serial; Sound = sound; Loop = loop; IsMusic = music;
                Frames = info.Frames; SampleRate = info.SampleRate; Channels = info.Channels;
                Frequency = info.SampleRate;
                if (info.Frames <= 0) Playing = false;   // stream without data (MusicVoice: Refill(0) empty)
            }
            internal void Stop() => Playing = false;
        }

        struct Entry
        {
            public Playback Src;
            public long Id;              // pool id with the bNoOverride bit (native +0x88)
            public float VolUE;          // +0xa0
            public float RadiusUU;       // +0xa4
            public int ActorId;          // +0x84 channel.Actor
            public string Sound;         // +0x10 channel.Sound (exact UE path)
            public UEVector LocUE;       // +0x14
            public float PitchUE;        // +0x28
            public float Priority;       // +0x2c
            public bool Stereo;          // USound+0x50
            public int SampleRate;
            public int VoiceRec;         // +0x0 the record's voice: pool slot + 1 (0: none)
        }

        readonly Entry[] _ch = new Entry[EffectsChannels];
        long _freeSlot;
        int _serial;
        readonly IUEGalaxyWorld _world;

        // Counters (measurement only, no effect on behaviour).
        public int Rejected, Evicted, NoListenerRejected, Mixed, MixMissingListener;
        public int PlayedCommands, StoppedCommands, UnresolvedClips;
        public int AmbientStarted, AmbientUpdated, AmbientStopped;
        public int AmbientUpdateWithoutVoice, UnknownKinds, UnknownSoundParams;
        public int AmbientRestartedWhilePlaying;
        public int ActorVoiceStopCalls, VoicesUnbound;
        public int RadiusApplied, PitchApplied;

        public UEGalaxyAudio(IUEGalaxyWorld world) { _world = world ?? throw new ArgumentNullException(nameof(world)); }

        // Judge tap (diagnostics only): observes the
        // device calls after they ran ("cmd" AudioCommand, "destroy" actor id, "viewport" bool, "viewport-same") and the
        // frame's Update ("update-begin" / "update-end" from UEWorldFrameProducer); null in production.
        public static Action<UEGalaxyAudio, string, object> JudgeTap;

        // ── SNAPSHOT FOR THE FRONTEND ───────────────────────────────────────
        // Channel voices by index (null — free) and voices whose data ended in this Update (the frontend lets them
        // play out the tail). ResumeSerial — the song held across viewport loss (D-51).
        public Playback[] Snapshot()
        {
            var s = new Playback[EffectsChannels];
            for (int i = 0; i < EffectsChannels; i++) s[i] = _ch[i].Src;
            return s;
        }
        public int ResumeSerial => _resume?.Serial ?? 0;

        // ════════════════ POOL ════════════════════════════════════════════════

        // Fresh SLOT_None id: 16 * --FreeSlot (Galaxy.dll.c:1780-1785).
        public long FreshId() => (--_freeSlot) << 4;

        public static long ActorSlotId(int actorId, int slot, bool noOverride)
            => UEGalaxyChannels.ChannelId(actorId, slot, noOverride);

        // A channel entry stays occupied until StopSound removes it (D-10): ModifySound (0x10607ae0-0x10607b67),
        // StopSound (0x10607ba0-0x10607c13) and the PlaySoundW selection loop (0x1060783e-0x10607883) read only the
        // entry's Id, Sound and priority; a finished non-looping voice is removed by the next Update.
        bool Occupied(int i) => _ch[i].Src != null;
        float CurrentPriority(int i) => Occupied(i) ? _ch[i].Priority : 0f;   // :2130

        // PlaySoundW selection loop. -1 — rejected.
        public int TryAcquire(in UEAudioListener listener, long id, UEVector locUE, float volUE,
                              float radiusUU, out float newPri, Playback reuseSrc = null, bool musicSound = false)
        {
            // PlaySoundW gates only on the viewport (+0x7c, 0x10607786-0x1060778b; viewport loss: Execute) and then
            // dereferences Viewport->Actor for the priority (0x106077d5-0x106077e2) — it has no case without that actor,
            // so a call without one is refused loudly rather than given a viewer.
            if (!listener.HasViewer)
            {
                newPri = 0f;
                NoListenerRejected++;
                if (NoListenerRejected == 1)
                    HP1.Core.UETrace.Warn("[audio] PlaySound without a viewport actor — the sound is not started (shipped dereferences Viewport->Actor here)");
                return -1;
            }
            // USound+0x64 != 0 (music sound): priority = Volume without distance (Galaxy.dll.c:1788).
            newPri = musicSound ? volUE : UEGalaxyVoiceMix.Priority(in listener, locUE, volUE, radiusUU);
            int victim = -1;
            float best = newPri;
            for (int i = 0; i < EffectsChannels; i++)
            {
                if (reuseSrc != null && _ch[i].Src == reuseSrc) { victim = i; break; }   // Id match of the slot (:1810)
                if (Occupied(i) && ((_ch[i].Id ^ id) & ~1L) == 0)
                {
                    if ((id & 1) != 0) { Rejected++; return -1; }   // :1805-1808
                    victim = i;
                    break;
                }
                float p = CurrentPriority(i);
                if (p <= best) { best = p; victim = i; }
            }
            if (victim == -1) { Rejected++; return -1; }   // :1816
            StopChannel(victim);   // :1817
            return victim;
        }

        // Occupy a channel (PlaySoundW :1837-1852).
        void Bind(int i, Playback src, long id, float volUE, float radiusUU, int actorId,
                  string sound, UEVector locUE, float pitchUE, float priority)
        {
            _ch[i] = new Entry
            {
                Src = src, Id = id, VolUE = volUE, RadiusUU = radiusUU, ActorId = actorId, Sound = sound,
                LocUE = locUE, PitchUE = pitchUE, Priority = priority, Stereo = src.Channels == 2, SampleRate = src.SampleRate,
            };
        }

        // MUSIC IN THE SHARED POOL (D-28): the Update music block starts the song with the same PlaySoundW (vtable
        // +0x78, 0x1060928f): Actor 0, Id 0 (-> fresh Id), Volume 100.0, Radius 1.0, Pitch 0; entry priority = Volume.
        public const float MusicVolumeArg = 100f;
        int AcquireMusic(in UEAudioListener listener, Playback m, string soundPath)
        {
            long id = FreshId();
            int i = TryAcquire(in listener, id, default, MusicVolumeArg, 1f, out float pri, musicSound: true);
            if (i < 0) return -1;
            _ch[i] = new Entry { Src = m, Id = id, VolUE = MusicVolumeArg, RadiusUU = 1f, Sound = soundPath, Priority = pri,
                                 Stereo = m.Channels == 2, SampleRate = m.SampleRate };
            return i;
        }

        // The song is removed by the music block itself (song change, viewport loss) — not eviction.
        void ReleaseMusic(Playback m)
        {
            if (m == null) return;
            for (int i = 0; i < EffectsChannels; i++)
                if (_ch[i].Src == m) { Clear(i); return; }
        }

        public bool HoldsMusic(Playback m)
        {
            for (int i = 0; i < EffectsChannels; i++) if (m != null && _ch[i].Src == m) return true;
            return false;
        }

        // Galaxy StopSound / ModifySound: the FIRST matching channel under the Core rule.
        public int FindFirstMatch(int id, string sound)
        {
            var view = new (int Id, string Sound)[EffectsChannels];
            for (int i = 0; i < EffectsChannels; i++)
                view[i] = Occupied(i) ? ((int)_ch[i].Id, _ch[i].Sound) : (0, null);
            return UEGalaxyChannels.FindFirst(view, id, sound);
        }

        // execModifySound result: channel found (Galaxy ModifySound 0x10607ae0 — 1 even for an unknown parameter).
        public bool HasMatchingChannel(int id, string sound) => !_viewportLost && FindFirstMatch(id, sound) >= 0;

        // Fixture seam: occupy a free channel with entry (id, sound) without a listener — ModifySound/StopSound table.
        public Playback BindForTest(long id, string sound)
        {
            for (int i = 0; i < EffectsChannels; i++)
                if (!Occupied(i))
                {
                    var v = NewPlayback(sound, false, false, new SoundInfo(1 << 20, 22050, 1));
                    Bind(i, v, id, 1f, 1600f, 0, sound, default, 1f, 0f);
                    return v;
                }
            throw new InvalidOperationException("BindForTest: no free channel");
        }
        public void ResetForTest() { for (int i = 0; i < EffectsChannels; i++) Clear(i); _slotChannels.Clear(); _ambient.Clear(); }

        public Playback SourceAt(int i) => _ch[i].Src;
        public void StopIndex(int i) => StopChannel(i);
        public UEVector LocationOf(int i) => _ch[i].LocUE;

        // UGalaxyAudioSubsystem::NoteDestroy (Galaxy.dll.c:1715): ambient slot (Id & 0xe == 8) — StopSound;
        // the rest — channel.Actor = 0 (the voice plays out from its last position).
        void NoteDestroyPool(int actorId, out int ambientStopped, out int unbound)
        {
            ambientStopped = 0; unbound = 0;
            if (actorId == 0) return;
            for (int i = 0; i < EffectsChannels; i++)
            {
                if (_ch[i].ActorId != actorId) continue;
                if ((_ch[i].Id & 0xe) == 8) { StopChannel(i); ambientStopped++; continue; }
                _ch[i].ActorId = 0;
                unbound++;
            }
        }

        // Live channel update by the ambient scan (Galaxy.dll.c:7017).
        void UpdateLive(Playback src, float volUE, float radiusUU, float pitchUE, UEVector locUE)
        {
            if (src == null) return;
            for (int i = 0; i < EffectsChannels; i++)
                if (_ch[i].Src == src)
                {
                    _ch[i].VolUE = volUE; _ch[i].RadiusUU = radiusUU; _ch[i].PitchUE = pitchUE;
                    if (_ch[i].ActorId == 0) _ch[i].LocUE = locUE;
                    return;
                }
        }

        void ModifyEntry(int i, int param, float value)
        {
            if (param == 0) _ch[i].VolUE = value;
            else if (param == 1) _ch[i].RadiusUU = value;
            else if (param == 2) _ch[i].PitchUE = value;
        }

        // ── Third loop of UGalaxyAudioSubsystem::Update — over the pool ─────
        // Location from the live actor, then Priority/Volume/Pan/Doppler — UEGalaxyVoiceMix.Update.
        void MixAll(in UEAudioListener listener)
        {
            if (!listener.Valid) { MixMissingListener++; return; }
            UEGalaxyConfig cfg = _world.Galaxy
                ?? throw new InvalidOperationException("Update: the runtime has no Galaxy configuration");
            for (int i = 0; i < EffectsChannels; i++)
            {
                if (!Occupied(i)) continue;   // Id == 0 (0x1060871b-0x10608723)
                // Entry's voice ended: FUN_1060a760(voice, 2) == 0 -> StopSound(i) (0x1060872f-0x1060874b) — for every
                // channel with a voice, BEFORE the music test at 0x10608772. Natural end of the song: not an eviction.
                if (_ch[i].Src.IsMusic)
                {
                    if (_ch[i].Src.VoiceDone) Clear(i);
                    else if (!_ch[i].Src.Voiced) AllocateVoices(i, true);   // FUN_1060fbf0(.., 1) (Galaxy.dll.c:7268)
                    continue;   // music branch: volume comes from the music block, priority unchanged
                }
                // the record's voice (+0x0) decides, whoever's chain it is now (S1); a channel without one yet goes on
                int rec = _ch[i].VoiceRec - 1;
                if (rec >= 0 ? !VoiceActive(rec) : !_ch[i].Src.Playing) { StopChannel(i); continue; }
                bool hasActor = false;
                UEVector vel = default;
                if (_ch[i].ActorId != 0 && _world.TryGetActor(_ch[i].ActorId, out UEVector loc, out vel))
                {
                    _ch[i].LocUE = loc;                                     // 0x10608750
                    hasActor = true;
                }
                else vel = default;
                bool ambientSlot = (_ch[i].Id & 0xe) == 8;
                UEGalaxyVoice v = UEGalaxyVoiceMix.Update(in listener, cfg, _ch[i].LocUE, _ch[i].VolUE,
                    _ch[i].RadiusUU, _ch[i].Stereo, ambientSlot, hasActor, vel);
                _ch[i].Priority = v.Priority;
                if (v.Pan > 0x7fff)
                    throw new InvalidOperationException(
                        "Galaxy UseSurround (Pan 0xc000) — the rear channel is not played by the frontend");
                var src = _ch[i].Src;
                src.Volume = v.Volume; src.Pan = v.Pan;
                src.Frequency = UEGalaxyVoiceMix.Frequency(_ch[i].SampleRate, _ch[i].PitchUE, v.Doppler);
                // A voice created in this Update: its lead (the first message's play position + 0x400) is fixed by the step
                // set now; FeedVoice sets the same value at that message. The frontend plays it as silence (PreRoll).
                if (src.Lead < 0 && src.VoiceMix0 == MixedFrames)
                    src.PreRoll = (int)(FirstMessagePlay(src.VoiceMix0, Step16(src.Frequency, FeederOutputRate())) + PreRollFrames);
                // the channel without a voice gets one: FUN_1060fbf0(.., stereo) (Galaxy.dll.c:7201; param_6 = USound+0x50, :7109-7113)
                if (_ch[i].VoiceRec == 0) AllocateVoices(i, _ch[i].Stereo);
                // FUN_1060fbf0 / FUN_1060aae0 write volume, pan and frequency to the record's chain (Galaxy.dll.c:8354)
                int h = _ch[i].VoiceRec - 1;
                foreach (int k in new[] { h, _voiceOf[h] is Playback o && o.VoiceSlot == h ? o.VoiceSlot2 : -1 })
                    if (k >= 0) { _slotVol[k] = src.Volume; _slotPan[k] = src.Pan; _slotFreq[k] = src.Frequency; }
                Mixed++;
            }
            for (int i = 0; i < EffectsChannels; i++)
            {
                if (_ch[i].Src is not { IsMusic: false } q) continue;
                int r = _ch[i].VoiceRec - 1;
                q.RecordSlot = r;
                q.RecordLinked = r >= 0 && _voiceOf[r] is Playback o && o.VoiceSlot == r ? o.VoiceSlot2 : -1;
                for (int c = 0; c < 2; c++)
                {
                    int k = c == 0 ? q.VoiceSlot : q.VoiceSlot2;
                    if (k < 0) continue;
                    q.SlotVolume[c] = _slotVol[k]; q.SlotPan[c] = _slotPan[k]; q.SlotFrequency[c] = _slotFreq[k];
                }
            }
        }
        readonly int[] _slotVol = new int[Voices], _slotPan = new int[Voices], _slotFreq = new int[Voices];

        // Channel record cleared: StopSound frees the record's voice chain (FUN_10615d40, Galaxy.dll.c:2110), as do the
        // song's own release and the voice-off path (StopSound from the third loop).
        void Clear(int i)
        {
            int rec = _ch[i].VoiceRec - 1;   // StopSound: FUN_10615d40 on the record's voice, whoever's chain it is now (S1)
            if (rec >= 0 && _voiceOf[rec] != null)
            {
                if (!_inUpdate) _callStopSlots.Add(rec);
                FreeChain(rec);
            }
            _ch[i] = default;
        }

        void StopChannel(int i)
        {
            var src = _ch[i].Src;
            if (src != null && src.IsMusic) { Evicted++; src.Stop(); src.Evicted = true; }
            else if (src != null && src.Playing) { Evicted++; src.Stop(); }
            Clear(i);   // full clear = native StopSound :2122-2137
        }

        // AUTHOR IMPROVEMENT (D-52), not original: on entering menu pause, voices of non-ambient channels that were
        // playing before it hold their position; on exit they resume. The channel entry does not change.
        void HoldForMenuPause(bool hold)
        {
            for (int i = 0; i < EffectsChannels; i++)
            {
                Playback v = _ch[i].Src;
                if (v == null || v.IsMusic) continue;
                if (hold && !v.Started) continue;   // a sound started in the entry frame (menu) keeps playing
                v.Held = hold && (_ch[i].Id & 0xe) != 8;   // SLOT_Ambient(4): pause silences ambients, as in Galaxy
            }
        }

        // Galaxy SetViewport (Galaxy.dll.c:5085-5094): StopSound on every channel except the song.
        void StopAllButMusic()
        {
            for (int i = 0; i < EffectsChannels; i++)
                if (_ch[i].Src is { IsMusic: false }) StopChannel(i);
        }

        public int BusyCount()
        {
            int n = 0;
            for (int i = 0; i < EffectsChannels; i++) if (Occupied(i)) n++;
            return n;
        }

        public bool EntryOf(Playback src, out int index, out long id, out float volUE, out float radiusUU,
                            out int actorId, out float pitchUE, out float priority)
        {
            for (int i = 0; i < EffectsChannels; i++)
                if (src != null && _ch[i].Src == src)
                {
                    index = i; id = _ch[i].Id; volUE = _ch[i].VolUE; radiusUU = _ch[i].RadiusUU;
                    actorId = _ch[i].ActorId; pitchUE = _ch[i].PitchUE; priority = _ch[i].Priority;
                    return true;
                }
            index = -1; id = 0; volUE = radiusUU = pitchUE = priority = 0f; actorId = 0;
            return false;
        }

        // ════════════════ CALL EXECUTION ════════════════════════════════════════
        // A UE1 channel = (actor, slot).
        readonly Dictionary<long, Playback> _slotChannels = new();
        readonly Dictionary<int, Playback> _ambient = new();
        bool _viewportLost;

        Playback NewPlayback(string sound, bool loop, bool music, SoundInfo info)
            => new Playback(++_serial, sound, loop, music, info);

        // Synchronous, at call time (Audio->PlaySound/StopSound/ModifySound are synchronous device calls).
        public void Execute(in AudioCommand c)
        {
            // Without a viewport Galaxy is silent: PlaySoundW starts a sound only when this+0x7c != 0
            // (Galaxy.dll.c:1778), SetViewport(NULL) writes +0x7c = 0 (5190).
            if (_viewportLost) return;
            // the ambient scan runs inside Galaxy's Update (Galaxy.dll.c:7017), after the frame's audio is mixed: its stops
            // are not call stops (K2)
            bool ambient = c.Kind is AudioKind.AmbientStart or AudioKind.AmbientUpdate or AudioKind.AmbientStop;
            bool was = _inUpdate;
            if (ambient) _inUpdate = true;
            try { ExecuteKind(c); } finally { _inUpdate = was; }
            JudgeTap?.Invoke(this, "cmd", c);
        }

        void ExecuteKind(in AudioCommand c)
        {
            switch (c.Kind)
            {
                case AudioKind.Play:          ExecutePlay(c);          break;
                case AudioKind.Stop:          ExecuteStop(c);          break;
                case AudioKind.AmbientStart:  ExecuteAmbientStart(c);  break;
                case AudioKind.AmbientUpdate: ExecuteAmbientUpdate(c); break;
                case AudioKind.AmbientStop:   ExecuteAmbientStop(c);   break;
                case AudioKind.Modify:        ExecuteModify(c);        break;
                default:
                    UnknownKinds++;
                    HP1.Core.UETrace.Warn("[audio] unknown AudioKind " + c.Kind + " — command NOT executed");
                    break;
            }
        }

        static string RuleSound(in AudioCommand c)
            => c.SoundPath == null || c.SoundPath == "None" ? null : c.SoundPath;

        // Galaxy ModifySound (0x10607ae0-0x10607b67).
        void ExecuteModify(in AudioCommand c)
        {
            int idx = FindFirstMatch(UEGalaxyChannels.ChannelId(c.ActorId, c.Slot, false), RuleSound(c));
            if (idx < 0 || SourceAt(idx) == null) return;
            switch (c.Param)
            {
                case 0: ModifyEntry(idx, 0, c.Volume); break;
                case 1: ModifyEntry(idx, 1, c.Radius); break;
                case 2: ModifyEntry(idx, 2, c.Pitch); break;
                default:
                    UnknownSoundParams++;
                    HP1.Core.UETrace.Warn("[audio] ModifySound: unknown ESoundParam " + c.Param + " — channel NOT modified");
                    break;
            }
        }

        SoundInfo? ResolveClip(in AudioCommand c)
        {
            if (string.IsNullOrEmpty(c.SoundPath) || c.SoundPath == "None") return null;
            SoundInfo? info = _world.Sound(c.SoundPath);
            if (info == null) UnresolvedClips++;
            return info;
        }

        Playback PlayVoice(SoundInfo info, UEVector loc, float vol, float radiusUE, float pitch, bool loop,
                           long poolId, Playback reuse, int actorId, string soundPath)
        {
            long id = poolId != 0 ? poolId : FreshId();
            int i = TryAcquire(_world.Listener, id, loc, vol, radiusUE, out float pri, reuse);
            if (i < 0) return null;
            var v = NewPlayback(soundPath, loop, false, info);
            Bind(i, v, id, vol, radiusUE, actorId, soundPath, loc, pitch, pri);
            return v;
        }

        void ExecutePlay(in AudioCommand c)
        {
            if (ResolveClip(c) is not SoundInfo info) return;
            float vol = c.Volume < 0f ? 0f : c.Volume;
            // Shipped execPlaySound (63868): zero radius means 1600 UU.
            float radiusUE = c.Radius == 0f ? 1600f : c.Radius;
            float pitch = c.Pitch <= 0f ? 1f : c.Pitch;   // Play: pitch <= 0 -> 1
            if (c.Slot == 0)
            {
                // SLOT_None: a fresh voice per call, not tracked.
                if (PlayVoice(info, c.Location, vol, radiusUE, pitch, c.Looping, 0, null, c.ActorId, c.SoundPath) != null)
                { PlayedCommands++; RadiusApplied++; PitchApplied++; }
                return;
            }
            // Channel (actor, slot) is the entry Id: an Id match selects THIS channel; with bNoOverride on the new call
            // it is rejected (PlaySoundW :1803-1810), regardless of whether the entry is still sounding (D-10).
            long key = ((long)c.ActorId << 8) | (uint)(c.Slot & 0xFF);
            var src = PlayVoice(info, c.Location, vol, radiusUE, pitch, c.Looping,
                ActorSlotId(c.ActorId, c.Slot, c.NoOverride), null, c.ActorId, c.SoundPath);
            if (src == null)
            {
                // Rejected: the channel entry with the same Id (bNoOverride) stays with the previous voice.
                if (!(_slotChannels.TryGetValue(key, out var cur) && cur != null && EntryOf(cur, out _, out _, out _, out _, out _, out _, out _)))
                    _slotChannels.Remove(key);
                return;
            }
            _slotChannels[key] = src;
            PlayedCommands++; RadiusApplied++; PitchApplied++;
        }

        // Galaxy StopSound (0x10607ba0-0x10607c13).
        void ExecuteStop(in AudioCommand c)
        {
            StoppedCommands++;
            int idx = FindFirstMatch(UEGalaxyChannels.ChannelId(c.ActorId, c.Slot, false), RuleSound(c));
            if (idx < 0) return;
            var src = SourceAt(idx);
            StopIndex(idx);
            var gone = new List<long>();
            foreach (var kv in _slotChannels) if (kv.Value == src) gone.Add(kv.Key);
            foreach (long k in gone) _slotChannels.Remove(k);
            var goneAmb = new List<int>();
            foreach (var kv in _ambient) if (kv.Value == src) goneAmb.Add(kv.Key);
            foreach (int k in goneAmb) _ambient.Remove(k);
        }

        void Kill(Playback src)
        {
            if (src == null) return;
            if (src.Playing) src.Stop();
            for (int i = 0; i < EffectsChannels; i++)
                if (_ch[i].Src == src) { Clear(i); return; }
        }

        // ── AMBIENT: start -> update* -> stop, ONE voice per actor ───────────
        void ExecuteAmbientStart(in AudioCommand c) => ExecuteAmbientStart(c, c.Volume);
        void ExecuteAmbientStart(in AudioCommand c, float volume)
        {
            if (ResolveClip(c) is not SoundInfo info) return;
            if (_ambient.TryGetValue(c.ActorId, out var old))
            {
                if (old != null && old.Playing && string.Equals(old.Sound, c.SoundPath, StringComparison.OrdinalIgnoreCase))
                    AmbientRestartedWhilePlaying++;
                Kill(old);
            }
            float radiusUE = c.Radius <= 0f ? 1600f : c.Radius;
            float pitch = c.Pitch <= 0f ? 1f : c.Pitch;
            // Looping is a sound-data flag, as for Play; a non-looping voice ends and Update restarts it.
            var src = PlayVoice(info, c.Location, volume, radiusUE, pitch, c.Looping,
                ActorSlotId(c.ActorId, c.Slot, false), null, c.ActorId, c.SoundPath);
            if (src == null) { _ambient.Remove(c.ActorId); return; }
            _ambient[c.ActorId] = src;
            AmbientStarted++;
        }

        // Update without a voice RE-ACQUIRES it.
        void ExecuteAmbientUpdate(in AudioCommand c)
        {
            // A non-looping voice ended but its entry is still in the pool: Galaxy Update N sees channel Index*16+8 and
            // does not start (start loop, Galaxy.dll.c:6956-6976); the entry is removed by the third loop of the same
            // Update (7276-7281); the new start happens on Update N+1.
            if (_ambient.TryGetValue(c.ActorId, out var ended) && ended != null && !ended.Playing
                && EntryOf(ended, out _, out _, out _, out _, out _, out _, out _))
                return;
            if (!_ambient.TryGetValue(c.ActorId, out var src) || src == null || !src.Playing)
            {
                // Galaxy re-acquires it in the START loop: PlaySoundW with the start volume SoundVolume·AmbientFactor/255
                // (0x10608478-0x1060848c, no doubling — that is 0x106085e2 in the update loop); the victim/reject choice
                // uses that volume (0x10607830, 0x10607871-0x10607880). The update loop of the same Update then refreshes
                // the channel (0x106085cb-0x10608676).
                AmbientUpdateWithoutVoice++;
                ExecuteAmbientStart(c, c.StartVolume);
                if (!_ambient.TryGetValue(c.ActorId, out src)) return;
            }
            UpdateLive(src, c.Volume, c.Radius <= 0f ? 1600f : c.Radius, c.Pitch <= 0f ? 1f : c.Pitch, c.Location);
            RadiusApplied++; PitchApplied++;
            AmbientUpdated++;
        }

        void ExecuteAmbientStop(in AudioCommand c)
        {
            if (_ambient.TryGetValue(c.ActorId, out var src)) Kill(src);
            _ambient.Remove(c.ActorId);
            AmbientStopped++;
        }

        // Actor destroyed: POOL scan (NoteDestroy).
        public void NoteDestroy(int actorId)
        {
            ActorVoiceStopCalls++;
            NoteDestroyPool(actorId, out int ambStopped, out int unbound);
            JudgeTap?.Invoke(this, "destroy", actorId);
            AmbientStopped += ambStopped;
            VoicesUnbound += unbound;
            _ambient.Remove(actorId);
            var gone = new List<long>();
            foreach (var kv in _slotChannels)
                if ((int)(kv.Key >> 8) == actorId) gone.Add(kv.Key);
            foreach (long k in gone) _slotChannels.Remove(k);
        }

        public Playback SlotVoiceForTest(int actorId, int slot)
            => _slotChannels.TryGetValue(((long)actorId << 8) | (uint)(slot & 0xFF), out var v)
               && EntryOf(v, out _, out _, out _, out _, out _, out _, out _) ? v : null;
        public Playback AmbientVoiceForTest(int actorId) => _ambient.TryGetValue(actorId, out var v) ? v : null;

        // Audio->SetViewport(Audio->GetViewport()) — UGameEngine::LoadMap calls it twice: after the fade's final Draw,
        // before UObject::BeginLoad (0x1039c8af-0x1039c8c7), and at the end of its `if (Client)` block after
        // GNewBrushTracker (0x1039e5b1-0x1039e5c6). Galaxy SetViewport (0x10605f50, Galaxy.dll.c:5085-5094) StopSounds every
        // channel whose sound is not music (with the viewport lost the channels are already free), then returns: the
        // viewport is the same.
        public void SetViewportSame() { if (!_viewportLost) ClearWorld(); JudgeTap?.Invoke(this, "viewport-same", null); }

        // Level reset (UEWorldFrameProducer.ResetForNewLevel: teardown, a world built without LoadMap): the same stop;
        // the actor-keyed channel maps die with the level. On the LoadMap route the channels were already stopped by
        // SetViewportSame after the fade, so here it finds nothing.
        public void ClearWorld()
        {
            foreach (var kv in _slotChannels) Kill(kv.Value);
            foreach (var kv in _ambient) Kill(kv.Value);
            StopAllButMusic();
            _slotChannels.Clear(); _ambient.Clear();
        }

        // Window focus loss/regain is frontend INPUT: Audio->SetViewport(NULL / viewport)
        // (WinDrv.dll.c:3038-3052, Galaxy.dll.c:5093-5140).
        public void SetViewport(bool present)
        {
            JudgeTap?.Invoke(this, "viewport", present);
            if (!present)
            {
                if (_viewportLost) return;
                _viewportLost = true;
                KeepForResume();   // D-51: the song resumes from here
                // StopSound on every channel (0x10605fb7-0x10605feb, viewport NULL), a voice held by menu pause (D-52)
                // included: the improvement holds a voice's position, it does not keep a channel the original frees.
                for (int i = 0; i < EffectsChannels; i++) StopChannel(i);
                _slotChannels.Clear();
                _ambient.Clear();
                StopMusicForViewportLoss();
                _world.NoteAudioChannelsLost();
            }
            else if (_viewportLost)
            {
                _viewportLost = false;
                // SetViewport(viewport): Song != None and Transition == 0 -> Transition = 1 (Galaxy.dll.c:5222-5225).
                _world.WriteViewportRestored();
            }
        }

        // ════════════════ UPDATE (once per engine frame) ══════════════════════
        bool _menuPaused;

        // Sound timing is exact: a device call frees its voices at once
        // (StopSound -> FUN_10615d40) and the mixer's next frames run without them. The frame carries the pool voices the
        // calls since the previous Update stopped (LastCallStops, pool voices) and the dt this Update advanced the audio clock by
        // (LastMixDt); the frontend stops those voices, then mixes the frame's audio, then takes the new table. The list
        // holds pool voices (a call's StopSound frees the record's chain, which after S1 may be another channel's voice).
        readonly List<int> _callStopSlots = new();   // pool voices the calls freed (FUN_10615d40 from StopSound)
        bool _inUpdate;
        public int[] LastCallStops { get; private set; } = Array.Empty<int>();
        public float LastMixDt { get; private set; }

        public void Update(MusicState music, float dt, bool paused)
        {
            LastCallStops = _callStopSlots.ToArray(); _callStopSlots.Clear();
            LastMixDt = !_viewportLost && dt > 0f && _world.Galaxy is not null ? dt : 0f;
            if (_viewportLost) return;   // without a viewport Update returns at once (Galaxy.dll.c:6900-6902)
            _inUpdate = true;
            try { UpdateBody(music, dt, paused); } finally { _inUpdate = false; }
        }

        void UpdateBody(MusicState music, float dt, bool paused)
        {
            // Core's audio clock (mixed output frames since Galaxy start) advances by OutputRate·dt every engine frame
            // with a viewport, voices or not (the mixer FUN_10612250 runs its counter update at 0x10612aaf-0x10612b54 with
            // zero voices). Without a viewport it stands still: SetViewport(NULL) calls glxStopOutput (0x10606145 ->
            // 0x10616040: timeKillEvent 0x10616061, d0f4 = 0 0x1061606f), the timer callbacks 0x10611bd0/0x10615a30 are
            // the only way into the mixer and test d0f4 first; glxStartOutput on return does not touch the sequencer
            // counter DAT_1067d0ac, so the phase resumes where it stopped.
            if (dt > 0f && _world.Galaxy is UEGalaxyConfig g) { _mixClock += (double)g.OutputRate * dt; Partition(MixedFrames); }
            if (paused != _menuPaused) { _menuPaused = paused; HoldForMenuPause(paused); }
            Advance(dt);
            MixAll(_world.Listener);
            UpdateMusic(music, GalaxyStep(dt));
        }

        // Galaxy Update measures its own frame delta and clamps it before the music block (Galaxy.dll.c:6918-6927,
        // disasm 0x106082be..0x106082f4): below 0 (or NaN) -> 0, not below 1 -> 1.
        internal static float GalaxyStep(float dt) => !(dt >= 0f) ? 0f : dt < 1f ? dt : 1f;

        // Voice position per frame: a voice started BEFORE this Update has advanced Frequency·dt data frames.
        void Advance(float dt)
        {
            for (int i = 0; i < EffectsChannels; i++)
            {
                Playback v = _ch[i].Src;
                if (v == null) continue;
                if (!v.Started)
                {
                    v.Started = true;
                    // FUN_1060fbf0 creates the voice in this Update's third loop: the feeder starts here (music below)
                    if (!v.IsMusic) { v.VoiceMix0 = v.FeedMix = MixedFrames; v.Voicings++; }
                    continue;
                }
                // MusicVolume 0 (no StopSound by the music block): the voice plays on to the armed end; the Update after
                // it reaches it frees the channel (live: off 1.006 s after the script-visible end, freed 11.6 ms later).
                if (v.IsMusic) { if (v.Voiced && !v.VoiceDone) FeedVoice(v, MixedFrames); continue; }   // the song's feeder too
                if (!v.Playing || v.VoiceLost) continue;   // a voice freed by another allocation has no feeder (S1)
                if (v.Held) { v.FeedMix = MixedFrames; continue; }   // D-52: the held voice does not advance
                FeedVoice(v, MixedFrames);
            }
        }

        // ════════════════ MUSIC ══════════════════════════════════════════════
        int _percentVolume = 100;
        string _currentSong;           // +0x708 CurrentMusic
        int _currentCd = 255;          // +0x70c
        int _currentSection;           // +0x70d
        float _fadeCounter = 1f;       // +0x780
        Playback _sound;               // +0x784 song USound
        string _soundPath;
        readonly HashSet<string> _registered = new(StringComparer.OrdinalIgnoreCase);
        bool _fading;
        Playback _resume;              // D-51 (author improvement): song stream playing at viewport loss
        string _resumePath;
        (int, string) _reqLogged = (0, null);
        public int RequestsConsumed { get; private set; }
        public int ResolvedSongRequests { get; private set; }
        public int FinishReports { get; private set; }
        public int FinishRejected { get; private set; }
        public string CurrentSongPath => _currentSong;
        public bool Fading => _fading;
        public float FadeCounter => _fadeCounter;
        public Playback ActiveSong => _sound;
        public string ActiveSongPath => _soundPath;

        static bool SameSong(string a, string b) => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

        // Music part of Galaxy Update, in shipped order:
        //   1. third channel loop on the song entry: voice (FUN_1060fbf0) and volume (0x10608797-0x10608825);
        //   2. bSongFinished loop (Galaxy.dll.c:7284-7311, disasm 0x10608ecc-0x10608f6a);
        //   3. music block, if Transition != 0 (7312-7466).
        void UpdateMusic(MusicState state, float dt)
        {
            if (state.Valid) _percentVolume = state.PercentVolume;   // +0x70e = ViewActor+0x55b (7247-7250)
            for (int i = 0; i < EffectsChannels; i++)
            {
                Playback m = _ch[i].Src;
                if (m == null || !m.IsMusic) continue;
                if (!m.Voiced)
                {
                    // FUN_1060fbf0 creates the voice: an empty ring, the feeder's pre-roll at its first message (FeedVoice)
                    m.VoiceMix0 = m.FeedMix = MixedFrames;
                    m.Pos16 = m.Laps = 0; m.Lead = m.K0 = m.ArmedEnd = -1;
                    m.PreRoll = m.Resumed ? 0
                        : (int)(FirstMessagePlay(MixedFrames, Step16(m.SampleRate, FeederOutputRate())) + PreRollFrames);
                    m.Voicings++;
                }
                m.Voiced = true;
                m.Volume = VoiceVolume(_fadeCounter);
                // IsFinished, set by the feeder thread before this Update's bSongFinished loop reads it.
                if (m.Playing && !m.Loop && FeederShortRead(m))
                { m.Playing = false; m.Ended = true; ShortReads++; }
            }
            if (!state.Valid) return;
            int transition = state.Transition;
            string song = state.SongPath;
            bool held = _sound != null && HoldsMusic(_sound);
            if (held && !_sound.Playing)
            {
                FinishReports++;
                if (!_world.WriteMusicFinished())
                {
                    FinishRejected++;
                    HP1.Core.UETrace.Info($"[Music] end of '{_soundPath}' not accepted (no view actor)");
                }
                transition = 1;
                song = null;
            }
            else _world.WriteSongFinishedFlag(!held);
            if (transition == 0) { _fading = false; return; }
            if (state.SongSection != 0 && state.SongSection != 255)
                throw new InvalidOperationException(
                    $"Music section {state.SongSection} is not represented by the decoded track '{song}'");
            if (string.IsNullOrEmpty(song) && state.CdTrack != 255)
                throw new InvalidOperationException($"CD music track {state.CdTrack} is not ported");
            if (transition < 1 || transition > 5)
                throw new InvalidOperationException($"Unknown EMusicTransition value {transition}");
            if (_reqLogged != (transition, song))
            {
                _reqLogged = (transition, song);
                HP1.Core.UETrace.Info($"[Music] request '{song ?? "None"}' transition {transition} section {state.SongSection}; current '{_currentSong ?? "—"}' (section {_currentSection}), counter {_fadeCounter}");
            }
            // bVar24 — the CURRENT song differs from the actor's; bVar26 — and MusicVolume != 0 (7316-7318).
            bool differs = !SameSong(_currentSong, song);
            bool soundOn = MusicByte() != 0 && differs;
            _fading = false;
            if (_currentSong != null || _currentCd != 255)
            {
                bool change;
                if (_currentSection == 255) change = true;          // 7320
                else if (transition >= 3)
                {
                    // 7323-7345, disasm 0x10608fcc..0x10609053: the counter is stored as float (fst) but compared
                    // UNROUNDED with fild Latency · double constant (fcompp; change iff counter < threshold).
                    // ponytail: double stands in for x87 extended; may differ only in the last bits of a tie.
                    var (rate, scale) = FadeParams(transition);
                    double counter = _fadeCounter - dt * rate;   // Fade (3): rate 1.0, the product is exact
                    double threshold = (_world.Galaxy?.Latency
                        ?? throw new InvalidOperationException("music fade: the runtime has no Galaxy configuration")) * scale;
                    _fadeCounter = (float)counter;
                    change = counter < threshold;
                }
                else change = true;                                  // Instant / Segue
                if (!change) { _fading = true; return; }             // SetVolumes (7381)
                if (_currentSong != null && soundOn)
                {
                    // StopSound of the channel whose Sound == +0x784 (7355-7364); UnregisterMusic of the current one (7367).
                    if (_sound != null && HoldsMusic(_sound))
                    {
                        _sound.Playing = false;
                        // NOT ORIGINAL (D-72): after the natural end the buffered rest
                        // of the song is heard (the original cuts it here); the channel is freed as in the original.
                        if (_sound.Ended) { StopTail(); _sound.TailPlays = true; _tail = _sound; }
                        ReleaseMusic(_sound);
                    }
                    _registered.Remove(_currentSong);
                }
                _currentSong = null;                                 // 7376-7377
                _currentCd = 255;
            }
            StartSong(song, state, soundOn);
        }

        // Galaxy .rdata doubles: rate × dt (Fade has no multiply), threshold scale × Latency.
        static (double Rate, double Scale) FadeParams(int transition) => transition switch
        {
            3 => (1.0, BitConverter.Int64BitsToDouble(unchecked((long)0xbf60624dd2f1a9fcUL))),                                  // MTRAN_Fade: -0.002 [0x1063a4d0]
            4 => (BitConverter.Int64BitsToDouble(0x4008000000000000L), BitConverter.Int64BitsToDouble(unchecked((long)0xbf789374bc6a7efaUL))),  // MTRAN_FastFade: 3.0 [0x1063a4a0], -0.006 [0x1063a490]
            _ => (BitConverter.Int64BitsToDouble(0x3fc999999999999aL), BitConverter.Int64BitsToDouble(unchecked((long)0xbf3a36e2eb1c432dUL))), // MTRAN_SlowFade: 0.2 [0x1063a4c0], -0.0004 [0x1063a4b0]
        };

        // Start block of Galaxy UpdateMusic (7385-7451): +0x780 = 1.0; at the end Transition = 0 (0x1060948b).
        void StartSong(string song, MusicState state, bool soundOn)
        {
            _fadeCounter = 1f;
            _reqLogged = (0, null);
            RequestsConsumed++;
            _currentSong = string.IsNullOrEmpty(song) ? null : song;
            _currentCd = state.CdTrack;
            _currentSection = state.SongSection;
            if (_currentSong != null && soundOn)
            {
                SoundInfo? info = _world.Song(_currentSong);
                if (info != null) ResolvedSongRequests++;
                StartNow(info, _currentSong, !state.DontLoop);
                _registered.Add(_currentSong);
            }
            else if (_currentSong != null)
                HP1.Core.UETrace.Info(MusicByte() == 0
                    ? $"[Music]   MusicVolume 0 — '{_currentSong}' is not started, the previous stream is not stopped"
                    : "[Music]   same song — not restarted");
            _resume = null;   // D-51: a stream not taken by this start block is not resumed
            _world.WriteMusicStarted();
        }

        // AUTHOR IMPROVEMENT (D-51), not original: after the viewport returns, the start block gets the PREVIOUS
        // stream with its position (same Serial — the frontend continues the voice) if it is the same song and loop.
        Playback TakeResume(string path, bool loop)
        {
            Playback r = _resume;
            _resume = null;
            if (r == null || !SameSong(_resumePath, path) || r.Loop != loop) return null;
            r.Playing = true; r.Evicted = false; r.Voiced = false; r.Ended = false; r.VoiceDone = false; r.Resumed = true; r.VoiceOffMix = long.MaxValue;
            r.Volume = VoiceVolume(1f);
            HP1.Core.UETrace.Info($"[Music]   '{path}' resumes from where focus was lost (improvement D-51)");
            return r;
        }

        void KeepForResume()
        {
            if (_sound != null && _sound.Playing && HoldsMusic(_sound))
            {
                _resume = _sound; _resumePath = _soundPath;
                _sound.DataBase = DataPlayed(_sound);
            }
        }

        void StartNow(SoundInfo? info, string path, bool loop)
        {
            HP1.Core.UETrace.Info($"[Music]   start '{path}'{(info == null ? " — NO STREAM (file did not open)" : "")}");
            if (info is not SoundInfo si) return;   // USound without data: no channel, +0x784 keeps its previous value
            StopTail();   // D-72 improvement: new music stops the previous song's tail, as the original stops the song
            Playback v = TakeResume(path, loop) ?? NewPlayback(path, loop, true, si);
            if (v.Volume == 0) v.Volume = VoiceVolume(1f);
            // Music PlaySoundW: channel from the shared pool; rejected — no voice; +0x784 is still the new USound (7411).
            _sound = v; _soundPath = path;
            if (AcquireMusic(_world.Listener, v, path) < 0)
                HP1.Core.UETrace.Info($"[Music] PlaySound of song '{path}' rejected by the channel pool — no music, as in shipped");
        }

        void StopTail()
        {
            if (_tail == null) return;
            _tail.TailPlays = false;
            _tail = null;
        }

        // ── MUSIC FEEDER MODEL (D-72: deterministic, phase 0 at Galaxy start) ─────
        // Galaxy streams a song through a feeder thread (0x1060bba0, GetMessage 0x400). The mixer (FUN_10612250) cuts
        // its chunk at the sequencer counter DAT_1067d0ac (0x1061234e-0x1061235a); when the counter drops below 0x10000
        // it posts 0x400 to every stream voice with lParam = play offset +0x46 - +0x52 (0x10612acb-0x10612b25), BEFORE
        // the next chunk is mixed, and reloads the counter with DAT_1067ceec (0x10612b43-0x10612b54) = OutputRate/96
        // frames in 16.16 (FeederReload16; 0xE5B000 at 22050). So message k is posted when the mixed total
        // reaches floor(k·reload / 0x10000), reporting the voice's play position there.
        // Feeder (0x1060bc35-0x1060c083): the 0x8000-byte ring (+0x2e; 16-bit, 0x4000 frames per voice) starts with
        // write = ring size; the voice's FIRST message sets write = play + 0x800 bytes (0x1060bd1e-0x1060bd32, a pre-roll
        // of 0x400 frames of the initial ring), and every message copies the 0x8000-byte staging buffer (+0x44) into the
        // ring from write up to lParam — so the ring is full as of the message and the first one copies 0x3c00 frames.
        // FillBuffer (0x1060be83) is called only when the copy needs more data (0x1060be34-0x1060be4c): stream block j
        // (0x8000 bytes = 0x8000/(2·ch) frames) is read when the data written exceeds j blocks. The short read — block
        // floor(D / 0x8000), D = iEndPosn (Data.mUncompressedLength) — sets IsFinished (MP2SoundStream::FillBuffer,
        // Engine 0x1037e550, SoundStream+8).
        // Live original (6 natural song ends): the first message's
        // play offset is 29..205 frames (inside one message interval only with the 0x400 pre-roll); fed it and the
        // IsFinished message's lParam, the block/ring accounting gives the live bytes copied from the short block
        // (536/1416/536/540/536/196). The mixer phase there is wall clock; here it is 0 at Galaxy start.
        // Not modelled: mix-call splits (DirectSound buffer, wall clock) that move a message's lParam inside its
        // interval (measured steps 220..243 frames instead of 229/230).
        public const int FeederBlockBytes = 0x8000, RingFrames = 0x8000 / 2, PreRollFrames = 0x800 / 2;
        double _mixClock;
        Playback _tail;
        public int ShortReads { get; private set; }
        public Playback SongTail => _tail;
        public long MixedFrames => (long)Math.Floor(_mixClock);

        // DAT_1067ceec in 16.16, glxStartOutput (0x10610c80, called by SetViewport(viewport) 0x106062f3/0x10606307):
        // [0x1067ca48] & 2 is clear (0x10611353 `test cl,2; je`): ca48 = 0 from glxInit (0x1060b988) / song reset
        // (0x1060c200); only the module loaders behind glxLoadMusic set it, and that block runs only with UseDigitalMusic
        // (this+0x40, `0x106092a0 test; je`), False in the shipped ini. The taken branch 0x10611397-0x106113db is
        // (60·rate / (cee8·d0a2)) << 16 + ((60·rate mod cee8·d0a2) << 16) / (cee8·d0a2) with cee8 = 0x78 (0x1060b90f,
        // 0x1060c1bc) and d0a2 = 0x30 (0x1060b903, 0x1060c1c8): divisor 5760. For rate = 96q + m: 60·rate = 5760q + 60m,
        // so it equals the form below bit for bit at every OutputRate (glxInit's literal 0xE5B000 at 22050, 0x1060b9d3).
        public static long FeederReload16(int outputRate)
            => ((long)(outputRate / 96) << 16) + (((long)(outputRate % 96) << 16) / 96);

        // Voice step +0x62: FUN_1060aae0 :8350-8352 (unsigned division, truncation).
        static long Step16(int frequency, int rate)
        {
            uint f = (uint)frequency, r = (uint)rate;
            return (long)((f / r) * 0x10000u + ((f % r) << 16) / r);
        }

        int FeederOutputRate() => (_world.Galaxy
            ?? throw new InvalidOperationException("music feeder: the runtime has no Galaxy configuration (OutputRate)")).OutputRate;

        // Song data frames played by now (D-51 resume): the voice position past the lead (FeedVoice state at MixedFrames).
        public long DataPlayed(Playback m)
            => m.DataBase + Math.Max(0, m.Laps * RingFrames + (m.Pos16 >> 16) - Math.Max(0, m.Lead));

        // Song data frames copied into the ring by the voice's last message (the feeder keeps the ring full up to the
        // message's play position + ring, 0x1060bd34-0x1060bd4e); 0 before the first message.
        public long Written(Playback m) => m.Lead < 0 ? 0 : m.DataBase + m.PrevL - m.Lead + RingFrames;

        public bool ArmedAtDataEnd { get; private set; }   // the last arming's branch (diagnostics for the checks)

        // MIXER CALL PARTITION (FUN_10612250, as AudioMixer.Mix): the frames an Update mixes are one call (the judge's
        // and Core's schedule; the original's calls follow the device cursor), split into blocks of 0x200 and those at the
        // envelope ticks (DAT_1067cf64, every OutputRate/250·2^14 in 16.16) and sequencer ticks (DAT_1067d0ac, FeederReload16;
        // both from glxInit 0x1060b97b-0x1060b9d3). A sequencer tick posts the stream messages (0x10612b0e-0x10612b54)
        // BEFORE its sub-block is mixed: a message's lParam is the voice position at the START of that sub-block (SwARc
        // judge: the first message of a voice created at a call start carries 0), not at the tick.
        readonly List<long> _msgFrames = new();
        long _envTick = -1, _seqTick, _partitioned;
        void Partition(long to)
        {
            int rate = FeederOutputRate();
            long envStep = ((long)rate << 14) / 250, seqStep = FeederReload16(rate);
            if (_envTick < 0) { _envTick = envStep; _seqTick = seqStep; }
            _msgFrames.Clear();
            for (long x = _partitioned; x < to;)
            {
                long block = Math.Min(to - x, 0x200);
                for (long done = 0; done < block;)
                {
                    long u = (block - done) << 16;
                    if (_envTick < u) u = _envTick & ~0xFFFFL;
                    if (_seqTick < u) u = _seqTick & ~0xFFFFL;
                    _envTick -= u; if (_envTick < 0x10000) _envTick += envStep;
                    _seqTick -= u; if (_seqTick < 0x10000) { _msgFrames.Add(x + done); _seqTick += seqStep; _seqTicks++; }
                    done += u >> 16;
                }
                x += block;
            }
            _partitioned = to;
        }

        // The voice position at the first message after a voice created at mixer frame x0 (no call boundary before it).
        long FirstMessagePlay(long x0, long step)
        {
            int rate = FeederOutputRate();
            long envStep = ((long)rate << 14) / 250, seqStep = FeederReload16(rate);
            long env = _envTick < 0 ? envStep : _envTick, seq = _envTick < 0 ? seqStep : _seqTick;
            for (long x = x0, inBlock = 0; ; )
            {
                long u = (0x200 - inBlock) << 16;
                if (env < u) u = env & ~0xFFFFL;
                if (seq < u) u = seq & ~0xFFFFL;
                env -= u; if (env < 0x10000) env += envStep;
                seq -= u; if (seq < 0x10000) return ((x - x0) * step) >> 16;
                x += u >> 16; inBlock = (inBlock + (u >> 16)) % 0x200;
            }
        }

        // The feeder of a sound's voice over the mixer frames (FeedMix, mixed]: each stream message of the interval
        // (Partition: lParam = the voice's play position L at its sub-block start) sets the lead at the first one and,
        // once the copy would pass the data (L + ring > data end), arms the end as FeederVoiceOff; the voice goes off at
        // the first frame its position reaches the armed end. The step is the one the previous Update set (FUN_1060aae0
        // +0x62 from Frequency).
        void FeedVoice(Playback v, long mixed)
        {
            long step = Step16(v.IsMusic ? v.SampleRate : v.Frequency, FeederOutputRate());
            long cur = v.FeedMix;
            if (mixed <= cur || step <= 0) { v.FeedMix = Math.Max(cur, mixed); return; }
            foreach (long x in _msgFrames)
            {
                if (x < cur) continue;
                if (RingAdvance(v, x - cur, step, v.ArmedEnd, cur)) return;
                cur = x;
                long l = v.Laps * RingFrames + (v.Pos16 >> 16);   // lParam, unwrapped by the voice's laps
                // the lead: the first message writes at play + 0x800 bytes (0x1060bd1e-0x1060bd32); a D-51 resume (not original)
                // continues seamlessly
                if (v.Lead < 0) { v.Lead = v.Resumed ? 0 : l + PreRollFrames; v.PreRoll = (int)v.Lead; }
                long dataEnd = v.Frames - v.DataBase + v.Lead;   // ring position at which the data ends
                if (v.ArmedEnd < 0 && !v.Loop)
                {
                    // the copy runs out of data (would pass the end): arm at the write offset if lParam < write as ring
                    // offsets (0x1060bf06-0x1060bf5a, linked voice 0x1060bfd7-0x1060bffa), else at the first later message
                    // whose lParam wrapped the ring, at the previous lParam (write after its zero fill)
                    if (v.K0 < 0) { if (l + RingFrames > dataEnd) { v.K0 = x; if (l % RingFrames < dataEnd % RingFrames) v.ArmedEnd = dataEnd; } }
                    else if (l % RingFrames < v.PrevL % RingFrames) v.ArmedEnd = v.PrevL + RingFrames;
                    if (v.ArmedEnd >= 0) ArmedAtDataEnd = v.ArmedEnd == dataEnd;
                }
                v.PrevL = l;
            }
            if (RingAdvance(v, mixed - cur, step, v.ArmedEnd, cur)) return;
            v.FeedMix = mixed;
        }

        // The voice's ring position over n mixed frames from mixer frame x0 (FUN_10612250's voice loop): a kernel call mixes
        // k = floor(((target - pos) << 16 - frac) / step) + 1 frames (0x10612dbf-0x10612dcd, target = the ring's last frame
        // 0x3fff), so the position ends past the target and is wrapped by pos = loop start + (pos - target)
        // (0x10612e45-0x10612e80): each lap of the ring advances the play position one frame more than the frames mixed
        // (SwARc judge: a voice two laps in goes off two mixer frames before the plain count). Positions are unwrapped
        // as Laps·ring + offset (what the feeder's lParam ring offsets mean). With an armed end the voice goes off at the
        // first mixer frame its position reaches it (iso run: +0x46 == +0x56 at the frame it goes inactive): returns true.
        bool RingAdvance(Playback v, long n, long step, long end, long x0)
        {
            const long T = (long)(RingFrames - 1) << 16;
            long done = 0;
            while (n > 0)
            {
                long kWrap = (T - v.Pos16) / step + 1, seg = Math.Min(n, kWrap);
                if (end >= 0)
                {
                    long need = ((end - v.Laps * RingFrames) << 16) - v.Pos16;
                    long f = need <= 0 ? 0 : (need + step - 1) / step;
                    if (f <= seg)
                    {
                        // a song's end the script saw already (IsFinished); its channel goes with the voice (VoiceDone)
                        if (v.IsMusic) v.VoiceDone = true; else { v.Playing = false; v.Ended = true; }
                        v.VoiceOffMix = x0 + done + f;
                        v.Pos16 += f * step; v.FeedMix = x0 + done + f;
                        return true;
                    }
                }
                v.Pos16 += seg * step; done += seg; n -= seg;
                // the wrap test follows every kernel call (0x10612e45: pos - target >= 0), so a call cut at a sub-block
                // boundary exactly on the target wraps too: the voice then skips the ring's last frame, not frame 0
                if (v.Pos16 >= T) { v.Pos16 -= T; v.Laps++; }
            }
            return false;
        }

        // IsFinished: the FillBuffer read that ends short — stream block floor(D / 0x8000) (MP2SoundStream::FillBuffer,
        // Engine 0x1037e550) — happens when the copy passes that block.
        bool FeederShortRead(Playback m)
        {
            long block = FeederBlockBytes / (2 * m.Channels);
            return Written(m) > m.Frames / block * block;
        }

        // GALAXY VOICE POOL: DAT_1066cfed = EffectsChannels + 4 voices (0x10672a00, stride 0xa0), initialised with
        // +0xf = 0, +0x11 = 8, +8 = 0 (Galaxy.dll.c:8897-8915). Script sees it only through a stolen voice (the channel
        // whose voice another allocation took is stopped, :7206-7212); the frontend mixes voices in pool order.
        const int Voices = EffectsChannels + 4;
        readonly Playback[] _voiceOf = new Playback[Voices];   // the chain's channel voice (+0x98 != 0), null: freed
        readonly bool[] _voiceNoHead = new bool[Voices];       // !(+0x11 & 8): the linked second voice of a chain
        readonly uint[] _voiceStamp = new uint[Voices];        // +8
        uint _seqTicks;                                        // DAT_1067ca40: +1 per sequencer tick (FUN_10613040)

        // +0xf: the voice sounds until its play position reaches the armed end (sound: Playing, cleared with Ended there;
        // song: VoiceDone).
        bool VoiceActive(int v) => _voiceOf[v] is Playback p && (p.IsMusic ? !p.VoiceDone : p.Playing);

        // FUN_10615d40: frees the chain from voice v (a voice without a mutex, +0x98 == 0, is left as it is).
        void FreeChain(int v)
        {
            Playback p = _voiceOf[v];
            if (p == null) return;
            foreach (int k in new[] { v, v == p.VoiceSlot ? p.VoiceSlot2 : -1 })
            {
                if (k < 0 || _voiceOf[k] != p) continue;
                _voiceOf[k] = null; _voiceNoHead[k] = false; _voiceStamp[k] = 0;
            }
            if (v == p.VoiceSlot) { p.VoiceSlot = -1; p.VoiceSlot2 = -1; p.VoiceLost = true; } else p.VoiceSlot2 = -1;
        }

        // FUN_1060fbf0(0, sample, ...) for channel i (Galaxy.dll.c:10936-10995): one voice per sample channel; each is the
        // LAST free voice with +0x11 & 8 (whose +8 the scan zeroes), else the first with the lowest +8 except the chain's
        // previous voice; FUN_10615d40 frees it; +8 = 0xfffffffe for a stereo sound or a song (param_6 & 1) and for the
        // linked voice, else the sequencer tick count. A channel whose record pointed at the taken voice is stopped.
        void AllocateVoices(int i, bool param6)
        {
            Playback p = _ch[i].Src;
            int n = p.Channels == 2 ? 2 : 1, prev = -1;
            for (int k = 0; k < n; k++)
            {
                int pick = -1; uint best = 0xfffffffe;
                for (int v = 0; v < Voices; v++)
                {
                    if (!VoiceActive(v) && !_voiceNoHead[v]) { pick = v; _voiceStamp[v] = 0; best = 0; }
                    else if (v != prev && _voiceStamp[v] < best) { pick = v; best = _voiceStamp[v]; }
                }
                // no voice: FUN_1060fbf0 returns 0 and Update asserts "Playing Channel" (Galaxy.dll.c:7215)
                if (pick < 0) throw new InvalidOperationException("Galaxy voice pool: no voice for channel " + i + " (appFailAssert Playing Channel)");
                if (_voiceOf[pick] is Playback old && old != p) FreeChain(pick);
                _voiceOf[pick] = p; _voiceNoHead[pick] = k != 0;
                _voiceStamp[pick] = param6 || k != 0 ? 0xfffffffe : _seqTicks;
                if (k == 0) p.VoiceSlot = pick; else p.VoiceSlot2 = pick;
                prev = pick;
            }
            p.VoiceLost = false;
            _ch[i].VoiceRec = p.VoiceSlot + 1;
            // the other channel whose record held the new chain's HEAD (only the head is compared, Galaxy.dll.c:7206-7212)
            // loses it: record voice 0, StopSound. A record holding the new linked voice keeps it (S1).
            for (int j = 0; j < EffectsChannels; j++)
                if (j != i && _ch[j].Src != null && _ch[j].VoiceRec == p.VoiceSlot + 1) { _ch[j].VoiceRec = 0; StopChannel(j); break; }
        }

        // MUSIC VOICE VOLUME (D-12), Galaxy disasm 0x10608797-0x106087e5:
        //   PercentMusicVolume · MusicVolume · K · +0x780 · 32767, ftol, clamp 0..0x7fff; K = 1/25500 (.rdata 0x1063a528).
        const float PercentByteScale = 1f / 25500f;
        int VoiceVolume(float fade)
        {
            double x = (double)_percentVolume * MusicByte() * PercentByteScale * fade * 32767.0;
            if (!(x > 0.0)) return 0;
            return x >= 0x7fff ? 0x7fff : (int)x;
        }

        int MusicByte() => (int)MathF.Round(_world.MusicMaster * 255f);

        // Galaxy SetViewport(NULL) (Galaxy.dll.c:5093-5140): UnregisterMusic of each registered song zeroes +0x708 if
        // it is that song (0x1060727f). +0x784, +0x780, +0x70c, +0x70d are left untouched.
        void StopMusicForViewportLoss()
        {
            StopTail();
            if (_sound != null) { _sound.Playing = false; ReleaseMusic(_sound); }
            if (_currentSong != null && _registered.Contains(_currentSong)) _currentSong = null;
            _registered.Clear();
            _fading = false;
        }
    }
}
