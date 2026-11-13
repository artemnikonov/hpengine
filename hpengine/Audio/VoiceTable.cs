// GALAXY CHANNEL VOICES IN THE FRONTEND: playback only. Pool decisions (which channel, eviction, bNoOverride,
// end of voice, music, bSongFinished) are made by Core (UEGalaxyAudio, WorldFrame.AudioChannels); here a mixer
// voice is started for every new channel Serial, receives Volume/Pan/Frequency/Held on Update and is removed when
// Core drops the channel. A voice whose data ran out in Core (Playback.Ended) plays its tail out by itself.
// A song stopped after its natural end with Playback.TailPlays (Core, NOT ORIGINAL, D-72) plays its
// stream out to the end until Core clears TailPlays.
using HP1.UEScript;

namespace HPEngine.Audio
{
    public sealed class VoiceTable
    {
        public const int EffectsChannels = UEGalaxyAudio.EffectsChannels;

        readonly Voice[] _voice = new Voice[EffectsChannels];
        readonly MusicVoice[] _music = new MusicVoice[EffectsChannels];
        readonly int[] _serial = new int[EffectsChannels];
        readonly int[] _voicings = new int[EffectsChannels];   // Core Playback.Voicings already executed (pre-roll)
        readonly UEGalaxyAudio.Playback[] _pb = new UEGalaxyAudio.Playback[EffectsChannels];
        // Voices without a channel: tails of finished sounds and the song held while the viewport is lost (NOT ORIGINAL, D-51).
        readonly List<Voice> _tails = new();
        MusicVoice _resume; int _resumeSerial;
        readonly List<(MusicVoice Voice, UEGalaxyAudio.Playback Core)> _musicTails = new();

        public Voice VoiceAt(int i) => _voice[i];
        // The channel's Galaxy pool voices as of the last Sync (Core Playback.VoiceSlot/VoiceSlot2 then; Core moves on).
        public (int Head, int Linked) SlotsAt(int i) => (_slot[i], _slot2[i]);
        readonly int[] _slot = new int[EffectsChannels], _slot2 = new int[EffectsChannels];
        public MusicVoice MusicAt(int i) => _music[i];
        public IReadOnlyList<Voice> Tails => _tails;
        public IReadOnlyList<(MusicVoice Voice, UEGalaxyAudio.Playback Core)> MusicTails => _musicTails;
        // NOT ORIGINAL (D-72): the SwARc judge's harness turns it off to compare with the original.
        public bool PlaySongTails = true;
        public int Created, Dropped, TailsStarted, MusicTailsStarted, Unresolved;

        public int BusyCount()
        {
            int n = 0;
            for (int i = 0; i < EffectsChannels; i++) if (_voice[i] != null || _music[i] != null) n++;
            return n;
        }

        // Core snapshot after the frame Update -> voices.
        public void Sync(IReadOnlyList<UEGalaxyAudio.Playback> channels, int resumeSerial, SoundBank bank,
                         IMusicSource songs, int rate, int masterLevel, int musicMasterLevel)
        {
            for (int i = 0; i < EffectsChannels; i++)
            {
                UEGalaxyAudio.Playback p = i < channels.Count ? channels[i] : null;
                if (p == null || p.Serial != _serial[i]) Release(i, resumeSerial);
                if (p == null) continue;
                _slot[i] = p.VoiceSlot; _slot2[i] = p.VoiceSlot2;
                if (_serial[i] == 0) Start(i, p, bank, songs, rate, musicMasterLevel);
                if (p.IsMusic)
                {
                    MusicVoice m = _music[i];
                    if (m == null) continue;
                    // A new Galaxy voice (Core): it plays the feeder pre-roll Core reports before its data (D-72).
                    if (p.Voicings != _voicings[i]) { _voicings[i] = p.Voicings; if (p.Voiced) m.LeadIn(p.PreRoll); }
                    m.Voiced = p.Voiced;
                    m.Apply(p.Volume, musicMasterLevel);
                    continue;
                }
                Voice v = _voice[i];
                if (v == null) continue;
                // per Galaxy voice (S1): a voice's volume/pan/frequency are what the last Update write to it left (Core
                // Playback.SlotVolume/SlotPan/SlotFrequency: another channel's record may write this chain's linked voice)
                GalaxyMixGain.Gains(p.SlotVolume[0], p.SlotPan[0], masterLevel, out int gl, out int gr);
                v.GainL = gl; v.GainR = gr;
                v.Frequency = p.SlotFrequency[0];
                GalaxyMixGain.Gains(p.SlotVolume[1], p.SlotPan[1], masterLevel, out int gl1, out int gr1);
                v.GainL1 = gl1; v.GainR1 = gr1;
                v.Frequency1 = p.SlotFrequency[1];
                v.Held = p.Held;
            }
            _tails.RemoveAll(t => !t.Playing);
            foreach (var t in _musicTails) if (!t.Core.TailPlays) t.Voice.Playing = false;
            _musicTails.RemoveAll(t => !t.Voice.Playing);
            if (_resume != null && resumeSerial != _resumeSerial) { _resume = null; _resumeSerial = 0; }
        }

        // K2: a device call freed this pool voice (StopSound frees the record's chain at once): it is silent for the frames
        // mixed before the next table. Songs are left to the table (which holds a song across viewport loss, D-51).
        public void StopSlot(int slot)
        {
            for (int i = 0; i < EffectsChannels; i++)
            {
                if (_voice[i] == null) continue;
                if (_slot[i] == slot) _voice[i].Playing = false;                 // the head: the whole chain
                else if (_slot2[i] == slot) _voice[i].StopVoice(1);             // the linked voice alone
            }
        }

        void Start(int i, UEGalaxyAudio.Playback p, SoundBank bank, IMusicSource songs, int rate, int musicMasterLevel)
        {
            _serial[i] = p.Serial; _pb[i] = p; _voicings[i] = 0;
            if (p.IsMusic)
            {
                if (_resume != null && _resumeSerial == p.Serial) { _music[i] = _resume; _resume = null; _resumeSerial = 0; _music[i].Playing = true; return; }
                IMusicStream st = songs.Open(p.Sound);
                if (st == null) { Unresolved++; return; }
                _music[i] = new MusicVoice(st, p.Loop, p.Volume, musicMasterLevel, rate);
                Created++;
                return;
            }
            var (ss, path) = bank.Open(p.Sound);
            if (ss == null) { Unresolved++; return; }
            // the sample's staging buffer (sample +0x44, FUN_10617270 :15849-15850): one per sound, shared by its voices
            if (!_staging.TryGetValue(path, out short[] stg)) _staging[path] = stg = new short[Voice.RingBytes / ss.SampleBytes];
            _voice[i] = new Voice(ss, p.Loop, stg);
            Created++;
        }
        readonly Dictionary<string, short[]> _staging = new(StringComparer.OrdinalIgnoreCase);   // ponytail: never shrinks; ~3500 sounds × 64 KiB at most

        void Release(int i, int resumeSerial)
        {
            if (_serial[i] == 0) return;
            Voice v = _voice[i];
            if (v != null)
            {
                // End of data in Core: the mixer plays the tail out; removal (StopSound, eviction) is immediate.
                if (v.Playing && _pb[i].Ended) { _tails.Add(v); TailsStarted++; }
                else v.Playing = false;
            }
            MusicVoice m = _music[i];
            if (m != null)
            {
                if (_serial[i] == resumeSerial) { _resume = m; _resumeSerial = resumeSerial; }
                else if (_pb[i].TailPlays && m.Playing && PlaySongTails) { _musicTails.Add((m, _pb[i])); MusicTailsStarted++; }
                else { m.Playing = false; m.Evicted = _pb[i].Evicted; }
            }
            _voice[i] = null; _music[i] = null; _serial[i] = 0; _pb[i] = null;
            Dropped++;
        }
    }
}
