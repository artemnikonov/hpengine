// HPEngine AUDIO — the per-frame entry point. The Galaxy channel pool, music and their decisions belong to Core (UEGalaxyAudio,
// executed in the tick); here — playback only: the frame's channel snapshot (WorldFrame.AudioChannels) -> mixer voices,
// zone reverb (Galaxy UpdateReverb: struct words from the Core frame), device master volumes.
//
// Everything that changes voices goes under one Gate lock: the main thread mixes each Core frame under it (ApplyFrame), the SDL
// device thread (AudioOutput -> Fill) takes it to drain the frame queue, and mixes on demand only before the first frame.
using HP1.UEScript;

namespace HPEngine.Audio
{
    public sealed class AudioSystem
    {
        public readonly object Gate = new object();
        public readonly VoiceTable Channels = new VoiceTable();
        public readonly AudioMixer Mixer;
        readonly SoundBank _bank;
        readonly IMusicSource _music;
        readonly ISoundSource _sounds;
        // HP2: the executor of Core's OpenAL calls (WorldFrame.ALCommands), made by the first frame that carries them; its
        // sources are mixed into the same frame queue as the Galaxy voices (HP1 frames carry none: AL stays null).
        public ALExecutor AL;

        // Reverb struct words last delivered (Galaxy this+0x70f), and the number of their changes (glxSetSampleReverb).
        ReverbState _reverbApplied;
        public int ReverbChanges;

        // ALL CORE FRAMES REACH AUDIO: a frame not passed here loses the voices started and stopped in it.
        // A skip is reported loudly and counted.
        int _lastFrameTick;
        public int FramesMissed;

        public AudioSystem(ISoundSource sounds, IMusicSource music, int firstTick = int.MinValue,
                           int outputRate = AudioMixer.GalaxyRate)
        {
            Mixer = new AudioMixer(Channels, outputRate);
            _bank = new SoundBank(sounds);
            _sounds = sounds;
            _music = music ?? throw new ArgumentNullException(nameof(music));
            _lastFrameTick = firstTick == int.MinValue ? int.MinValue : firstTick - 1;
        }

        void NoteFrameTick(int tick)
        {
            if (tick < 0) return;   // frames built outside the gate tick (post-load, LoadMap fade, save progress): UELevelRuntime.NextInsideTickFrameNumber
            if (_lastFrameTick != int.MinValue && tick > _lastFrameTick + 1)
            {
                int missed = tick - _lastFrameTick - 1;
                FramesMissed += missed;
                Console.Error.WriteLine($"[audio] Core frames {_lastFrameTick + 1}..{tick - 1} ({missed}) were not passed to sound — their voices are lost");
            }
            if (_lastFrameTick == int.MinValue || tick > _lastFrameTick) _lastFrameTick = tick;
        }

        // A tick Core marks as having no engine frame (the browsing frame after a travel/load, UELevelRuntime.PresentAudioNoDrawNow):
        // delivered, with nothing to mix — not a gap.
        public void NoteNoDrawTick(int tick) { lock (Gate) NoteFrameTick(tick); }

        // EVERY CORE FRAME, as Core builds it (UELevelRuntime.PresentAudioFrameNow, also the
        // frames built inside one tick: LoadMap fade, post-load, save progress): the frame's audio is mixed here, in Galaxy's
        // order — the voices the frame's calls stopped are stopped first, then the frames this Update's dt advanced the
        // clock by are mixed with the voices of the previous table, then the new table and reverb apply. The device plays
        // the queue (Fill).
        public void Update(WorldFrame frame, IAudioWorld world)
        {
            if (frame == null) return;
            // HP2: the span the device clock advanced (AudioMixDt) is mixed with the sources as they were, then the calls
            // made at the frame's end apply (Core's model: a play started in the tick advances from the next span).
            if (frame.ALCommands is not null) lock (Gate) AL ??= new ALExecutor(UEALAudio.MaxSources, Mixer.Rate, _sounds, _music);
            ApplyFrame(frame.Tick, frame.AudioCallStops, frame.AudioMixDt, frame.AudioChannels, frame.AudioResumeSerial,
                       frame.Reverb, world);
            if (frame.ALCommands is { } al) lock (Gate) AL.Execute(al);
        }

        public void ApplyFrame(int tick, IReadOnlyList<int> callStops, float mixDt, IReadOnlyList<UEGalaxyAudio.Playback> channels,
                               int resumeSerial, ReverbState reverb, IAudioWorld world)
        {
            lock (Gate)
            {
                _frameClocked = true;
                foreach (int slot in callStops) Channels.StopSlot(slot);
                CallStopsApplied += callStops.Count;
                if (mixDt > 0f) { _clock += (double)Mixer.Rate * mixDt; MixedSeconds += mixDt; }
                int n = (int)((long)_clock - _clocked);
                if (n > 0)
                {
                    _clocked += n;
                    if (_frame.Length < 2 * n) _frame = new float[2 * n];
                    Mixer.Mix(_frame, n);
                    AL?.MixAdd(_frame, n);
                    MixedFrames += n;
                    for (int k = 0; k < 2 * n; k += 2)
                    {
                        if (_frame[k] != 0f || _frame[k + 1] != 0f) SoundingFrames++;
                        _queue.Enqueue(_frame[k]); _queue.Enqueue(_frame[k + 1]);
                    }
                    // The queue holds a fixed lead, never a backlog (Galaxy mixes Latency ahead of the play cursor; OpenAL
                    // its device buffer): audio the frames deliver beyond MaxLeadSeconds while the device keeps playing (a
                    // frame clock running ahead of the device after a load, clock drift) is dropped from the head at once,
                    // so it cannot sit there delaying every later sound (sound audit S1, 2026-11-20).
                    int max = 2 * (int)(Mixer.Rate * MaxLeadSeconds);
                    while (_queue.Count > max) { _queue.Dequeue(); _queue.Dequeue(); QueueDropped++; }
                }
                NoteFrameTick(tick: tick);
                Sync(channels, resumeSerial, world);
                UpdateReverb(reverb, world.UseReverb);
            }
        }
        bool _frameClocked, _primed;
        // ponytail: fixed 60 ms device lead over the frame clock (Galaxy mixes Latency = 40 ms ahead); tune if hitches
        // longer than it are common
        public const float PrebufferSeconds = 0.06f;
        // ponytail: the steady-state ceiling = the prebuffer + 50 ms of engine-frame jitter (a 66.7 ms late frame after a
        // drained prebuffer stays under it); raise it if frame jitter on slow machines drops audio
        public const float MaxLeadSeconds = PrebufferSeconds + 0.05f;
        double _clock; long _clocked;
        float[] _frame = new float[0];
        readonly Queue<float> _queue = new();
        public int QueueDropped, QueueUnderruns;
        // What the frames delivered so far carried (AudioTests [loadmap]: equals the sum over Core's Galaxy Updates).
        public double MixedSeconds; public int CallStopsApplied;
        // What the frame clock mixed into the queue: stereo frames, and those not silent (audit/hp2_audio_check.sh).
        public long MixedFrames, SoundingFrames;

        public void Apply(int tick, IReadOnlyList<UEGalaxyAudio.Playback> channels, int resumeSerial, ReverbState reverb, IAudioWorld world)
        {
            lock (Gate)
            {
                NoteFrameTick(tick);
                Sync(channels, resumeSerial, world);
                UpdateReverb(reverb, world.UseReverb);
            }
        }

        void Sync(IReadOnlyList<UEGalaxyAudio.Playback> channels, int resumeSerial, IAudioWorld world)
            => Channels.Sync(channels, resumeSerial, _bank, _music, Mixer.Rate,
                    GalaxyMixGain.MasterLevel(world.SoundMaster), MusicMasterLevel);

        // Channels changed by Core outside a frame (LoadMap's Audio->SetViewport after the fade, UELevelRuntime.PresentAudioNow):
        // no Galaxy Update ran, so the reverb words stay.
        public void ApplyChannels(IReadOnlyList<UEGalaxyAudio.Playback> channels, int resumeSerial, IAudioWorld world)
        {
            lock (Gate) Sync(channels, resumeSerial, world);
        }

        // Group B master for the song voice (DAT_1067cedc = ftol(SoundVolume/255·127)). Galaxy scales the streamed
        // song by the common SoundVolume (D-12). NOT ORIGINAL (improvement, always on, no setting —
        // audio improvements are not switchable): the song master is fixed at the shipped default
        // SoundVolume 200 (Default.ini:160) — music is controlled only by MusicVolume.
        public static int MusicMasterLevel => GalaxyMixGain.MasterLevel(CoreAudioWorld.SoundDefault);

        // Tail of Galaxy Update — UpdateReverb (FUN_106095c4, 0x106095c4-0x1060976d): words are built EVERY frame,
        // zeros when UseReverb == 0; glxSetSampleReverb is called only if the words CHANGED.
        void UpdateReverb(ReverbState reverb, bool useReverb)
        {
            ReverbState want = useReverb ? reverb : default;
            if (want.SameWords(_reverbApplied)) return;
            _reverbApplied = want;
            ReverbChanges++;
            Mixer.Reverb.Set(want);
        }

        // Window focus loss/return: input for Core (Audio->SetViewport(NULL / viewport), UEGalaxyAudio.SetViewport);
        // without a viewport Galaxy plays nothing — silence, voices hold.
        public void SetPaused(bool paused)
        {
            lock (Gate) Mixer.Paused = paused;
        }

        // The frame queue as mixed, without the device's prebuffer (the judge's PCM harness: frame k's audio right after it).
        public void TakeQueued(float[] dst, int frames)
        {
            lock (Gate)
            {
                int k = 0;
                for (; k < 2 * frames && _queue.Count > 0; k++) dst[k] = _queue.Dequeue();
                System.Array.Clear(dst, k, 2 * frames - k);
            }
        }

        // Device thread: fill the buffer under the lock — from the frame queue once frames drive the mixer (Update),
        // otherwise (tests, Apply) by mixing on demand.
        public void Fill(float[] dst, int frames)
        {
            lock (Gate)
            {
                if (!_frameClocked) { Mixer.Mix(dst, frames); return; }
                // the device starts (and restarts after running dry) only with PrebufferSeconds queued: a late engine frame
                // (hitch) then drains the queue instead of cutting the sound. A hitch that ran it dry delivers the whole gap's
                // audio in one frame; kept, everything after it played that much late (up to MaxLeadSeconds) for the rest of
                // the session (bug "music starts late", 2026-11-20). Galaxy and OpenAL mix a fixed lead ahead of the play
                // position, never a backlog, and the gap's audio is not heard either way: such a backlog is cut to the lead.
                // A normal start overshoots the lead by under a frame and keeps it (ponytail: over two 60 Hz frames = a gap's).
                int lead = 2 * (int)(Mixer.Rate * PrebufferSeconds);
                if (!_primed && _queue.Count >= lead)
                {
                    _primed = true;
                    if (_queue.Count > lead + 2 * (Mixer.Rate / 30))
                        while (_queue.Count > lead) { _queue.Dequeue(); _queue.Dequeue(); QueueDropped++; }
                }
                int k = 0;
                if (_primed) for (; k < 2 * frames && _queue.Count > 0; k++) dst[k] = _queue.Dequeue();
                if (k < 2 * frames)
                {
                    if (_primed) { QueueUnderruns++; _primed = false; }
                    System.Array.Clear(dst, k, 2 * frames - k);
                }
            }
        }
    }
}
