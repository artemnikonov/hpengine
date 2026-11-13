// HPEngine MIXER: N effect voices + music streams -> int16 stereo (SDL output — s16/32768f).
//
// The core is a port of the GENERATED mixer of the Galaxy SSE path (GalaxyMixKernel: 16.16,
// interpolation, float32 buses ×2^-30, cvtps2pi + 16-bit saturation). Here is what
// FUN_10612250 does around the kernel call (Galaxy.dll.c:12440-12990, disassembly
// 0x10612bf0-0x10612eb5): splitting into calls, voice positions, end of data and looping.
// Volume/pan/frequency are a Core body (UEGalaxyVoiceMix.Update), integer gains —
// GalaxyMixGain; the Core Update applies them (UEGalaxyAudio).
//
//   voice:  mono — one Galaxy voice; stereo — TWO mono voices (flag 0x40, WAV with
//           2 channels, Galaxy.dll.c:15845; FUN_1060fbf0 :10936-10937 uVar5 = 2,
//           link +0x9c :11114; loader FUN_10617130 splits the channels into two
//           buffers), Update FUN_1060aae0 writes volume and pan to ALL voices of the
//           chain (:8354) — a stereo sound has Pan 0x4000 (USound+0x50, :7110). Here:
//           data channel 0 is the head voice, channel 1 the linked one, with the same gains;
//   reverb: send bus 1 (gains (g·0x7f)>>7) -> ZoneReverb, the return is added
//           to the dry bus of group B;
//   music:  a GROUP B voice, like game sounds (D-12, Galaxy disassembly):
//           the Update branch "Sound+0x64 != 0" (0x10608772-0x10608851) starts a voice
//           FUN_1060fbf0(0, sample, -1, vol, -1, 1), which writes byte +0x11 =
//           1|8 (0x1060fd70-0x1060fd7b), no 0x80 bit; the mixer by bit 0x80
//           (0x10612bf0-0x10612c3e) routes it to buses ca64/ca6c/ca74 and master
//           DAT_1067cedc (SoundVolume), and the reverb callback DAT_1066cfe8 receives
//           ca64/ca6c (0x106120bc-0x106120e1). So music goes into the same buses as
//           game sounds, gains — the same GalaxyMixGain chain under master SoundVolume;
//           the voice volume is computed by Core (UEGalaxyAudio).
//   order:  the voice loop runs over the 20-voice pool in index order (0x10612b8a-0x10612ef6): the order
//           of the float additions into the buses. Which voice a channel gets is Core's (Playback.VoiceSlot,
//           FUN_1060fbf0); the mixer adds the voices in that order.
//
// SPLITTING INTO KERNEL CALLS (FUN_10612250): a block is at most 0x200 frames (:12486-12488);
// within a block frames are cut by envelope ticks (DAT_1067cf64 += DAT_1067ca80 =
// (22050<<14)/250 = 0x160ccc, 1 kHz) and sequencer ticks (DAT_1067d0ac += DAT_1067ceec =
// 0xe5b000 at tempo 0x78 and 0x30 ticks per beat, :8938-8939, :8968-8971, :12235-12237);
// a voice — also by its end of data (GalaxyMixKernel.FramesToEnd). Splitting matters: the kernel
// drops the low bit of step and fraction only within a call. In shipped the block length is also
// set by the DirectSound/WinMM write cursor (FUN_10611fd0) — that is the platform; here the SDL
// callback request plays that role.
//
// OUTPUT RATE — setting [Galaxy.GalaxyAudioSubsystem] OutputRate (ConfiguredRate). In Galaxy it is a
// UByteProperty over UEnum OutputRates (StaticConstructor, Galaxy.dll.c:1276-1323, 1409-1415): 8000Hz,
// 11025Hz, 16000Hz, 22050Hz, 32000Hz, 44100Hz, 48000Hz; Init maps the index to Hz via a table
// (Galaxy.dll.c:5232-5238) and passes it to glxStartOutput (FUN_10610c80), which accepts 8000..48000
// (`7999 < r < 0xbb81`, :12050) and recomputes voice steps and envelope/sequencer ticks from the rate
// (:12052-12073). glxInit — 22050 (:8961 `0x5622`), shipped ini — 22050Hz (Default.ini:156).
// NOT ORIGINAL (D-50): HPEngine defaults to 44100; the OutputRate key in the
// HPEngine user ini restores any value of the Galaxy list, 22050Hz — as shipped.
//
// Threading: Mix is called under the AudioSystem.Gate lock — on the main thread for every Core frame
// (AudioSystem.ApplyFrame), and by the SDL device thread (AudioSystem.Fill) only before the first frame;
// after it the device thread only drains the frame queue. Mix itself is a pure function of voice state:
// tests run it without a device.
using HP1.UEScript;

namespace HPEngine.Audio
{
    // Effect voice: the Galaxy voice chain of one channel (one voice per sample channel: the head and, for a stereo
    // sample, the linked +0x9c), each playing its own 0x8000-byte ring that the Galaxy stream thread (LAB_1060bba0)
    // fills from the voice's SoundStream through the SAMPLE's 0x8000-byte staging buffer (sample +0x44, one per USound:
    // every voice of the same sound reads and refills the same buffer). Voice identity (the object) matters: the
    // (actor, slot) and ambient maps hold a REFERENCE to the voice, and eviction by another sound must not replace it.
    public sealed class Voice
    {
        public const int RingBytes = 0x8000;   // sample +0x2e (FUN_10617270 :15848): ring and staging size
        public UEPcm Pcm => (_src as PcmSoundStream)?.Pcm;   // decoded data behind the stream (WAV, tests); null for mp2
        readonly ISoundStream _src;
        public readonly bool Loop;
        // Playing flag (+0xf of the head voice). Cleared by Stop or when the voice reaches the end the feeder armed after
        // the data (in Mix: main thread, or the device thread before the first frame; under the lock).
        public bool Playing;
        // Head voice position on its ring: integer frame (+0x46 - ring base) and 16-bit fraction (+0x4a).
        public int Pos { get => _pos[0]; set => _pos[0] = value; }
        public int Frac { get => _frac[0]; set => _frac[0] = value; }
        // Applied by MixAll: playback frequency (Hz, +0x5e — the 16.16 step derives from it)
        // and integer dry gains L/R (arguments +0x24/+0x28 of the kernel call, 0..0x7fff).
        public int Frequency;
        public int GainL, GainR;
        public int Frequency1, GainL1, GainR1;   // the linked Galaxy voice of a stereo sound (S1: may differ from the head)
        // NOT ORIGINAL (D-52): a voice that was playing when the menu pause began
        // (Level.Pauser) holds its position until the pause ends (see UEGalaxyAudio).
        public bool Held;

        readonly int _ch, _bps;                    // sample channels; bytes per sample (16-bit: 2, 8-bit: 1)
        internal readonly short[][] Ring;          // per Galaxy voice: RingBytes / bps samples, zeroed (FUN_1060fbf0 :11024-11035)
        readonly int[] _pos = new int[2], _frac = new int[2];
        readonly bool[] _active = new bool[2];
        // Voice end (+0x5a) and the loop flags 0x88 of +0x4c: a stream voice loops over its ring (target +0x56 = 0x3fff,
        // loop start +0x52 = the ring base) until the feeder arms the end (0x1060bf0c-0x1060bf5a).
        readonly int[] _end = { Target, Target };
        readonly bool[] _loops = { true, true };
        public const int Target = (RingBytes >> 1) - 1;   // +0x56 = ((sample+0x2e) >> 1) - 1 (FUN_1060fbf0 :11058)
        // Feeder state: write offset +0x88 (bytes; = ring size until the first message), staging read offset +0x90 and
        // valid end +0x94 (FUN_1060fbf0 :11061-11064); the voice's own stream position (samples returned).
        int _w = RingBytes, _sOff = RingBytes, _sEnd = RingBytes;
        readonly short[] _staging;

        public Voice(ISoundStream stream, bool loop, short[] staging)
        {
            _src = stream ?? throw new ArgumentNullException(nameof(stream));
            if (stream.SampleRate <= 0 || (stream.Channels != 1 && stream.Channels != 2))
                throw new ArgumentException($"PCM {stream.SampleRate} Hz × {stream.Channels} channels — the mixer does not play this");
            int bytesPerSample = stream.SampleBytes;
            if (bytesPerSample != 1 && bytesPerSample != 2) throw new ArgumentException($"{bytesPerSample} bytes per sample");
            Loop = loop; Playing = true;
            _ch = stream.Channels; _bps = bytesPerSample;
            _staging = staging ?? throw new ArgumentNullException(nameof(staging));
            if (_staging.Length != RingBytes / _bps) throw new ArgumentException("staging buffer of another sample size");
            Ring = new short[_ch][];
            for (int c = 0; c < _ch; c++) { Ring[c] = new short[RingBytes / _bps + 1]; _active[c] = true; }
            Frequency = Frequency1 = stream.SampleRate;   // until the first MixAll (which runs in the same Update)
        }

        public int Channels => _ch;
        // Ring frame the data starts at (the first message's play + 0x400: the pre-roll this voice plays); -1 before it.
        public int Lead { get; private set; } = -1;
        public bool VoiceActive(int c) => Playing && _active[c];
        public void StopVoice(int c) => _active[c] = false;
        public void Stop() => Playing = false;

        // THE STREAM MESSAGE (LAB_1060bba0, 0x1060bc74-0x1060c08b): lParam = the head voice's ring frame at the sub-block
        // start. The feeder copies into the ring everything from its write offset up to that play position (a full ring
        // ahead of playback), from the staging buffer; when the staging runs dry it moves the unread rest to the start and
        // reads as many bytes from the voice's stream as were consumed (callback DAT_1067d0ec, 0x1060be52-0x1060bea0).
        // Two empty attempts in a row end the copy: the data is over, the rest is zero-filled and, if the play position
        // is behind the write offset, the voice's end is armed there.
        public void Message(int lParam)
        {
            int P = lParam * _bps, R = RingBytes;
            if (_w == R) { _w = (P + 0x800) % R; Lead = _w / _bps; }   // 0x1060bd1e-0x1060bd32
            int A = P - _w; if (A < 0) A += R; if (A >= R) A = R;  // 0x1060bd34-0x1060bd4e
            int off = _sOff, valid = _sEnd;
            for (int j = 0; j < 2 && A != 0;)
            {
                int chunk = Math.Min(A, R - _w), avail = valid - off;
                // FUN_10617130: frames = min(avail / (bps·ch), chunk / bps); channel c into voice c's ring
                int fr = Math.Min(avail / (_bps * _ch), chunk / _bps);
                if (fr > 0)
                {
                    int w0 = _w / _bps, s0 = off / _bps;
                    for (int f = 0; f < fr; f++)
                        for (int c = 0; c < _ch; c++) Ring[c][w0 + f] = _staging[s0 + f * _ch + c];
                    j = 0;
                    _w = (_w + fr * _bps) % R; A -= fr * _bps; off += fr * _bps * _ch;   // 0x1060bf28-0x1060bf4c
                    continue;
                }
                if (off != 0)   // 0x1060be4e
                {
                    Array.Copy(_staging, off / _bps, _staging, 0, avail / _bps);
                    valid = avail + ReadStream(avail / _bps, off / _bps) * _bps;
                    off = 0;
                }
                j++;
            }
            _sOff = off; _sEnd = valid;
            if (A == 0) return;
            for (int c = 0; c < _ch; c++)
            {
                if (P < _w) { _loops[c] = false; _end[c] = _w / _bps; }   // flags &= 0xff77, +0x56 = +0x5a = W (0x1060bf06-0x1060bf5a)
                int first = Math.Min(A, R - _w);                         // zero fill (0x1060bf5d-0x1060bfc3)
                Array.Clear(Ring[c], _w / _bps, first / _bps);
                Array.Clear(Ring[c], 0, (A - first) / _bps);
            }
            _w = (A + _w) % R;
        }

        // The voice's SoundStream FillBuffer: n samples into the staging at sample `at`, decoded as they are read; a looping
        // stream starts its next lap (WAV: the data chunk again, mp2: MP2SoundStream's restart), otherwise it reads short.
        int ReadStream(int at, int n)
        {
            int got = 0;
            bool restarted = false;
            while (got < n)
            {
                int k = _src.Read(_staging, at + got, n - got);
                if (k > 0) { got += k; restarted = false; continue; }
                if (!Loop || restarted) break;
                _src.Restart(); restarted = true;
            }
            return got;
        }

        // Voice loop of FUN_10612250 for Galaxy voice c (0x10612d64-0x10612eb5): kernel calls up to the target
        // (+0x56 while looping, else the armed end +0x5a), position advanced by the full step; past the target a looping
        // voice wraps to the ring base plus the overshoot (each lap skips one frame: target 0x3fff of a 0x4000 ring),
        // otherwise it goes off.
        internal void Mix(float[] dry, float[] send, int off, int n, int c, int step, int gl, int gr, int sl, int sr)
        {
            short[] ring = Ring[c];
            while (n > 0 && _active[c])
            {
                int target = _loops[c] ? Target : _end[c];
                int k = GalaxyMixKernel.FramesToEnd(target, _pos[c], _frac[c], step);
                if (k <= 0) throw new InvalidOperationException($"voice: position {_pos[c]} past its target {target}");
                if (k > n) k = n;
                GalaxyMixKernel.Mix(dry, send, off, ring, 1, 0, ring.Length, _pos[c], _frac[c], step, k, gl, gr, sl, sr);
                GalaxyMixKernel.Advance(ref _pos[c], ref _frac[c], step, k);
                int over = _pos[c] - target;
                if (over >= 0)
                {
                    if (_loops[c]) _pos[c] = over;
                    else { _active[c] = false; if (c == 0) Playing = false; }
                }
                n -= k; off += k;
            }
        }
    }

    // Music stream — the voice of a pool channel record (VoiceTable.MusicAt). Usually there is one, but
    // Galaxy removes the previous song channel only on a change with bVar26 (UEGalaxyAudio), so the
    // previous stream may play alongside the new one.
    //
    // In Galaxy the music stream is group B voice(s) (see the header): a stereo sample is
    // TWO voices; FUN_1060fbf0 with pan = -1 and two voices writes pan 0 to the first and
    // 0x7fff to the second (`-(local_8 & 1) & 0x7fff`, 0x1060fedf-0x1060ff02),
    // and Update (FUN_1060aae0 with pan = -1) does not
    // touch it unless the voice has flag 0x20 (Galaxy.dll.c:8335-8344) — data
    // channel 0 plays only on the left, 1 only on the right, with gains
    // GalaxyMixGain(VoiceVolume, 0 | 0x7fff, master SoundVolume).
    // Sample flags of an mp2 stream are set by glxLoadSample mode 10 (FUN_10616790 -> on 'WAVE'
    // FUN_10617270, data chunk, Galaxy.dll.c:15841-15852): 2 channels — 0x40
    // (two voices), otherwise 0x20; mode & 8 — | 0x8000 (stream); sample pan +0x28 = 0x4000.
    // Data channel 0 is the first int16 of a stream frame (FUN_10617130 in the stream thread 0x1060be24: the
    // owner voice's buffer gets offset 0, the linked one (+0x9c) offset 2), and chain voice index 0
    // gets pan 0 (0x1060fedf-0x1060ff02). After skipping 0x3c2 bytes the mp2 stream
    // starts with the decoder's RIGHT sample (UEMp2SoundStream) — in the game the song's right channel plays
    // on the left and the left on the right (one-sample shift); the same here: buffer channel 0 — left.
    // A mono stream is one voice; with pan = -1 the start writes the pan only when the
    // sample flag 0x20 is set (0x1060fef4-0x1060ff02; a mono mp2 stream has it — sample pan 0x4000),
    // Update with pan = -1 likewise only when +0x4c & 0x20 (Galaxy.dll.c:8335-8344; voice +0x4c is a copy
    // of sample flags +0x2c, 0x1060fe11-0x1060fe15) and likewise the sample pan. Result for a mono stream — 0x4000
    // (confirmed by reading FUN_10617270).
    //
    // The mixing kernel is the same as for sounds (GalaxyMixKernel). Stream data lives in a
    // sliding buffer; end of data only at the end of the stream (loop — Restart in
    // Refill). The stream is an MP2SoundStream lap (UEMp2SoundStream: skip 0x3c2 bytes, lap iEndPosn;
    // Restart — skip again, the synthesis filter is continuous), as in Galaxy playing a song via a temporary USound(UMusic*).
    // The shipped stream buffer (ring LAB_1060bba0) is not ported, except its pre-roll: a new voice first plays the
    // empty ring for the frames Core reports (Playback.PreRoll, LeadIn, D-72). Otherwise only the sample past the end
    // of data depends on it, which is 0 here — a one-sample shift, inaudible (D-28).
    public sealed class MusicVoice
    {
        const int Chunk = 2048;   // frames per stream read
        // Pan of a mono music voice — the stream sample's pan (+0x28 = 0x4000, FUN_10617270; see above).
        public const int MonoPan = 0x4000;
        public readonly IMusicStream Stream;
        public readonly bool Loop;
        public bool Playing;
        // The song channel was removed by eviction (StopSound from the PlaySoundW victim choice), not by the
        // music block: shipped then does not reset Song — only bSongFinished (Galaxy.dll.c:7311).
        public bool Evicted;
        // Voice created (third Update loop, FUN_1060fbf0): the channel record starts the music block,
        // the voice — the next Update (D-28). Until then the record is silent.
        public bool Voiced;
        // Galaxy voice volume 0..0x7fff (argument of FUN_1060aae0/FUN_1060fbf0),
        // written by the Core Update (UEGalaxyAudio), applied by VoiceTable.Sync.
        public int VoiceVolume { get; private set; }
        // Integer gains of data channel c into output L/R: voice 0 and voice 1.
        int _gL0, _gR0, _gL1, _gR1;
        public int GainL0 => _gL0;
        public int GainR0 => _gR0;
        public int GainL1 => _gL1;
        public int GainR1 => _gR1;

        readonly short[] _buf;
        readonly int _ch;
        readonly int _step;
        int _frames;          // frames in the buffer
        int _pos, _frac;      // position in the buffer (16.16)
        long _ring16;         // position on the voice's ring (16.16, 0 .. 0x3fff)
        const long RingTarget16 = (long)Voice.Target << 16;
        bool _eof;
        // Feeder pre-roll (Core Playback.PreRoll, D-72): silent frames still to be played before the held data.
        int _lead;
        short[] _held; int _heldPos;   // frames that were buffered when the pre-roll was inserted

        // Core created the Galaxy voice (Playback.Voicings changed): the voice first plays `frames` frames of the empty
        // ring (the feeder's first message wrote at play + 0x800 bytes, 0x1060bd1e), then the stream from where it is.
        public void LeadIn(int frames)
        {
            if (frames <= 0 || !Playing) return;
            int keep = Math.Max(0, _frames - _pos);
            _held = new short[keep * _ch];
            Array.Copy(_buf, _pos * _ch, _held, 0, keep * _ch);
            _heldPos = 0; _lead = frames; _pos = 0; _eof = false; _ring16 = 0;
            Refill(0);
        }

        public MusicVoice(IMusicStream stream, bool loop, int voiceVolume, int masterLevel, int outputRate)
        {
            Stream = stream ?? throw new ArgumentNullException(nameof(stream));
            if (stream.SampleRate <= 0 || (stream.Channels != 1 && stream.Channels != 2))
                throw new ArgumentException($"music {stream.SampleRate} Hz × {stream.Channels} channels — the mixer does not play this");
            _ch = stream.Channels;
            _step = GalaxyMixKernel.Step(stream.SampleRate, outputRate);
            _buf = new short[(Chunk + 1) * _ch];
            Loop = loop; Playing = true;
            Apply(voiceVolume, masterLevel);
            Refill(0);
            if (_frames == 0) Playing = false;
        }

        // Voice volume(s) per Update: the group B mixer chain (master m —
        // SoundVolume in Galaxy; improvement: AudioSystem.MusicMasterLevel) with the voice pan.
        public void Apply(int voiceVolume, int masterLevel)
        {
            VoiceVolume = voiceVolume;
            if (_ch == 2)
            {
                GalaxyMixGain.Gains(voiceVolume, 0, masterLevel, out _gL0, out _gR0);
                GalaxyMixGain.Gains(voiceVolume, 0x7fff, masterLevel, out _gL1, out _gR1);
            }
            else
            {
                GalaxyMixGain.Gains(voiceVolume, MonoPan, masterLevel, out _gL0, out _gR0);
                _gL1 = 0; _gR1 = 0;
            }
        }

        // Read on into the buffer from position keep (frames before keep are already at the start).
        void Refill(int keep)
        {
            _frames = keep;
            while (_frames < Chunk + 1 && !_eof)
            {
                int space = Chunk + 1 - _frames;
                if (_lead > 0)
                {
                    int z = Math.Min(_lead, space);
                    Array.Clear(_buf, _frames * _ch, z * _ch);
                    _frames += z; _lead -= z;
                    continue;
                }
                if (_held != null)
                {
                    int h = Math.Min(_held.Length / _ch - _heldPos, space);
                    Array.Copy(_held, _heldPos * _ch, _buf, _frames * _ch, h * _ch);
                    _frames += h; _heldPos += h;
                    if (_heldPos * _ch >= _held.Length) _held = null;
                    continue;
                }
                int got = Stream.Read(_buf, _frames * _ch, (Chunk + 1 - _frames) * _ch) / _ch;
                if (got > 0) { _frames += got; continue; }
                // End of data. Loop — a new stream lap (FUN_1037e550: restart without resetting synthesis, with
                // the skip, UEMp2SoundStream.Restart); otherwise — end of stream.
                if (Loop) { Stream.Restart(); got = Stream.Read(_buf, _frames * _ch, (Chunk + 1 - _frames) * _ch) / _ch;
                            if (got == 0) _eof = true; else _frames += got; }
                else _eof = true;
            }
        }

        // n frames from bus frame off (this voice's kernel calls for one split segment), planned once for both data
        // channels (two Galaxy voices with the same position): Plan, then MixPlanned per channel in pool order.
        readonly List<(int Off, int Pos, int Frac, int K)> _plan = new();
        public void Plan(int off, int n)
        {
            _plan.Clear();
            if (!Playing) return;
            // Last index read by the sub-block's calls — p + ((f&~1) + (step&~1)·(n-1) >> 16) + 1, one more per lap.
            int need = _pos + (int)(((long)(_frac & 0xfffe) + (long)(_step & ~1) * (n - 1)) >> 16) + 3;
            if (need > _frames && !_eof)
            {
                // Shift: the unread tail to the start, read on.
                int keep = Math.Max(0, _frames - _pos), drop = _frames - keep;
                if (keep > 0) Array.Copy(_buf, drop * _ch, _buf, 0, keep * _ch);
                _pos -= drop; Refill(keep);
                need -= drop;
                if (need > _frames && !_eof)
                    throw new InvalidOperationException($"music: the kernel call reads {need} frames, buffer {_frames}");
            }
            while (n > 0 && Playing)
            {
                int k = n;
                if (_eof)
                {
                    k = Math.Min(n, GalaxyMixKernel.FramesToEnd(_frames, _pos, _frac, _step));
                    if (k <= 0) { Playing = false; break; }
                }
                // the ring lap (as Voice.Mix): a call ends past the target 0x3fff or is cut on it; the position wraps to
                // pos - target, so each lap skips one frame of the data
                int kLap = (int)(((long)RingTarget16 - _ring16) / _step + 1);
                if (kLap < k) k = kLap;
                _plan.Add((off, _pos, _frac, k));
                GalaxyMixKernel.Advance(ref _pos, ref _frac, _step, k);
                _ring16 += (long)_step * k;
                if (_ring16 >= RingTarget16) { _pos += 1; _ring16 -= RingTarget16; }
                n -= k; off += k;
                if (_eof && _pos >= _frames) Playing = false;
            }
        }

        public void MixPlanned(float[] dry, float[] send, int c)
        {
            if (c >= _ch) return;
            int gl = c == 0 ? _gL0 : _gL1, gr = c == 0 ? _gR0 : _gR1;
            foreach (var q in _plan)
                GalaxyMixKernel.Mix(dry, send, q.Off, _buf, _ch, c, _frames, q.Pos, q.Frac, _step, q.K, gl, gr,
                                     GalaxyMixKernel.Send(gl), GalaxyMixKernel.Send(gr));
        }

        public void MixInto(float[] dry, float[] send, int off, int n)
        {
            Plan(off, n);
            MixPlanned(dry, send, 0);
            MixPlanned(dry, send, 1);
        }
    }

    public sealed class AudioMixer
    {
        // glxInit rate and the shipped ini (Galaxy.dll.c:8961, Default.ini:156); the oracles were checked at it.
        public const int GalaxyRate = 22050;
        // NOT ORIGINAL (D-50): HPEngine default.
        public const int DefaultRate = 44100;
        // Galaxy OutputRates list: name -> Hz (Galaxy.dll.c:1288-1322, Init table :5232-5238).
        static readonly (string Name, int Hz)[] GalaxyRates =
            { ("8000Hz", 8000), ("11025Hz", 11025), ("16000Hz", 16000), ("22050Hz", 22050),
              ("32000Hz", 32000), ("44100Hz", 44100), ("48000Hz", 48000) };

        // List name (FName — case-insensitive) -> Hz; 0 — not in the list.
        public static int GalaxyRateHz(string name)
        {
            foreach (var r in GalaxyRates)
                if (string.Equals(r.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase)) return r.Hz;
            return 0;
        }

        // OutputRate from the HPEngine USER ini (UEConfig overlay); the shipped ini (22050Hz) is
        // deliberately not read — otherwise the 44100 default is unreachable. A value not in the list — loudly, default.
        public static int ConfiguredRate(UEConfig cfg)
        {
            const string Section = "Galaxy.GalaxyAudioSubsystem", Key = "OutputRate";
            if (cfg == null || !cfg.HasOverlay(UEConfig.ScopeSystem, Section, Key)) return DefaultRate;
            string raw = cfg.Get(UEConfig.ScopeSystem, Section, Key);
            int hz = GalaxyRateHz(raw);
            if (hz != 0) return hz;
            Console.Error.WriteLine($"[audio] [{Section}] {Key}={raw} — not in the Galaxy list (8000Hz..48000Hz), using {DefaultRate}");
            return DefaultRate;
        }

        // FUN_10612250: at most 0x200 frames per call (Galaxy.dll.c:12486-12488).
        public const int MaxBlock = 0x200;

        public readonly int Rate;
        // Envelope and sequencer ticks (16.16 frames) — from the rate, as glxStartOutput (:12067-12069):
        // DAT_1067ca80 = (rate<<14)/250, DAT_1067ceec = (rate·60/(0x78·0x30))<<16 + fraction;
        // initial counter values are the same (:8968-8971, at 22050).
        public readonly int EnvTickStep, SeqTickStep;

        readonly VoiceTable _channels;
        public readonly ZoneReverb Reverb;
        // Without a viewport Galaxy plays nothing — silence, voices hold.
        public bool Paused;
        // A voice with zero frequency (step 0): in Galaxy a division by zero in FUN_10612250
        // (idiv 0x10612dc5). The voice is not mixed; it is counted with one stderr message.
        public int ZeroStepVoices;

        float[] _dry = new float[0], _send = new float[0];
        int _envTick, _seqTick;   // DAT_1067cf64, DAT_1067d0ac

        public AudioMixer(VoiceTable channels, int rate = GalaxyRate)
        {
            if (rate < 8000 || rate > 48000)
                throw new ArgumentOutOfRangeException(nameof(rate), rate, "glxStartOutput accepts 8000..48000 Hz");
            _channels = channels;
            Rate = rate;
            EnvTickStep = (rate << 14) / 250;
            SeqTickStep = (int)(((long)rate * 60 / (0x78 * 0x30)) * 0x10000
                                + (((long)rate * 60 % (0x78 * 0x30)) << 16) / (0x78 * 0x30));
            _envTick = EnvTickStep; _seqTick = SeqTickStep;
            Reverb = new ZoneReverb(rate);
        }

        // Fill dst (frames frames, interleaved L/R). Overwrites dst.
        public void Mix(float[] dst, int frames)
        {
            int n2 = frames * 2;
            Array.Clear(dst, 0, n2);
            if (Paused) return;
            if (_dry.Length < n2) { _dry = new float[n2]; _send = new float[n2]; }
            Array.Clear(_dry, 0, n2);
            Array.Clear(_send, 0, n2);
            Order();
            for (int b = 0; b < frames;)
            {
                int block = Math.Min(frames - b, MaxBlock);
                for (int done = 0; done < block;)
                {
                    int u = (block - done) << 16;
                    if (_envTick < u) u = (int)(_envTick & 0xffff0000);
                    if (_seqTick < u) u = (int)(_seqTick & 0xffff0000);
                    _envTick -= u; if (_envTick < 0x10000) _envTick += EnvTickStep;
                    _seqTick -= u;
                    bool tick = _seqTick < 0x10000;
                    if (tick) _seqTick += SeqTickStep;
                    int n = u >> 16;
                    int off = b + done;
                    // The sequencer tick posts a stream message per active head stream voice, in pool order, BEFORE the
                    // sub-block is mixed (0x10612ae0-0x10612b3c); the feeder runs it at once (its writes lie behind playback).
                    if (tick)
                        foreach (var (v, c) in _order)
                            if (c == 0 && v is Voice sv && sv.Playing && !sv.Held) sv.Message(sv.Pos);
                    foreach (var (v, c) in _order) if (c == 0 && v is MusicVoice { Voiced: true } m) m.Plan(off, n);
                    // The voice loop of FUN_10612250 runs over the pool in index order (0x10612b8a-0x10612ed1): the order
                    // of the float additions into the buses. Music is a group B voice: the same buses, through reverb (D-12).
                    foreach (var (v, c) in _order)
                    {
                        if (v is Voice sv) { if (sv.VoiceActive(c) && !sv.Held) MixVoice(sv, c, off, n); }
                        else if (v is MusicVoice { Voiced: true } m) m.MixPlanned(_dry, _send, c);
                    }
                    // NOT ORIGINAL (D-72): song tails: the stopped song's stream plays out.
                    foreach (var t in _channels.MusicTails)
                        if (t.Voice.Playing) t.Voice.MixInto(_dry, _send, off, n);
                    // Tails: Core freed the channel, the voice plays to its own end.
                    foreach (Voice v in _channels.Tails)
                        for (int c = 0; c < v.Channels; c++)
                            if (v.VoiceActive(c)) MixVoice(v, c, off, n);
                    done += n;
                }
                b += block;
            }
            // Send 1 -> reverb, return into the group B dry bus; then the converter.
            Reverb.Process(_dry, _send, frames);
            for (int k = 0; k < n2; k++) dst[k] = GalaxyMixKernel.ToS16(_dry[k]) / 32768f;
        }

        // The channels' Galaxy voices by pool index (Core Playback.VoiceSlot / VoiceSlot2): (voice, data channel).
        readonly List<(object Voice, int Channel)> _order = new();
        readonly (object, int)[] _bySlot = new (object, int)[VoiceTable.EffectsChannels + 4];
        void Order()
        {
            Array.Clear(_bySlot);
            for (int i = 0; i < VoiceTable.EffectsChannels; i++)
            {
                object v = (object)_channels.VoiceAt(i) ?? _channels.MusicAt(i);
                if (v == null) continue;
                var (head, linked) = _channels.SlotsAt(i);
                if (head < 0) continue;   // the channel's Galaxy voice is created by the next Update (third loop): silent
                _bySlot[head] = (v, 0);
                if (linked >= 0) _bySlot[linked] = (v, 1);
            }
            _order.Clear();
            foreach (var e in _bySlot) if (e.Item1 != null) _order.Add(e);
        }

        // One Galaxy voice of a sound (Voice.Mix) at its step and gains; a stereo sound's two voices share the gains.
        void MixVoice(Voice v, int c, int off, int n)
        {
            int step = GalaxyMixKernel.Step(c == 0 ? v.Frequency : v.Frequency1, Rate);
            if (step == 0)
            {
                if (ZeroStepVoices++ == 0)
                    Console.Error.WriteLine($"[audio] voice with frequency {v.Frequency}: step 0 (Galaxy divides by zero) — not mixed");
                return;
            }
            int gl = c == 0 ? v.GainL : v.GainL1, gr = c == 0 ? v.GainR : v.GainR1;
            v.Mix(_dry, _send, off, n, c, step, gl, gr, GalaxyMixKernel.Send(gl), GalaxyMixKernel.Send(gr));
        }
    }
}
