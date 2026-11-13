// Window audio (hpengine/Audio): startup, every Core frame as Core builds it, focus, map change, destroyed actors.
// Zone reverb comes from the Core frame (WorldFrame.Reverb, Galaxy UpdateReverb); the frontend does not read
// actor properties.
using HP1.UEScript;
using HPEngine.Audio;

namespace HPEngine
{
    sealed class GameAudio : IDisposable
    {
        public readonly AudioSystem System;
        readonly AudioOutput _out;
        readonly UEGameEngine _host;
        CoreAudioWorld _world;
        UELevelRuntime _rt;
        public bool Open;

        public GameAudio(UEGameEngine host, string root, bool device = true)
        {
            _host = host;
            UESoundCatalog.Shared.SetSource(root);
            UEMusicCatalog.Shared.SetSource(root);
            // The first frame that must reach audio is Core tick 0 (UEGameEngine.Tick): audio comes up BEFORE the
            // first tick, as Galaxy does before the first engine frame (AudioSystem.FramesMissed).
            // Mixer rate: [Galaxy.GalaxyAudioSubsystem] OutputRate from the user ini, otherwise 44100 (D-50).
            System = new AudioSystem(new CoreSoundSource(), new CoreMusicSource(), firstTick: 0,
                                     outputRate: AudioMixer.ConfiguredRate(host.Runtime?.Config));
            Bind();
            // Every Core frame reaches the mixer when Core builds it, also the frames built inside one tick (LoadMap fade,
            // post-load, save progress): UELevelRuntime.PresentAudioFrameNow.
            host.Runtime.PresentAudioFrameNow = f =>
            {
                if (!ReferenceEquals(_rt, _host.Runtime)) Bind();
                System.Update(f, _world);
            };
            host.Runtime.PresentAudioNoDrawNow = System.NoteNoDrawTick;   // Core: a browsing tick has no audio frame
            _out = new AudioOutput(System);
            if (!device) return;   // probe without a device: the caller pulls the mixer (System.Fill)
            Open = _out.Open(out string err);
            if (!Open) Console.Error.WriteLine("sound: " + err);
        }

        void Bind()
        {
            _rt = _host.Runtime;
            _world = new CoreAudioWorld(_rt);
        }

        // Right after host.Tick: the tick's frames were already mixed as Core built them (PresentAudioFrameNow); here the
        // world binding follows a map change and a device fault surfaces.
        public void Update(bool worldChanged)
        {
            if (worldChanged || !ReferenceEquals(_rt, _host.Runtime)) Bind();
            _out.ThrowIfFaulted();
        }

        // LoadMap stopped the old level's channels before loading (UELevelRuntime.PresentAudioNow): play from that snapshot now.
        public void ApplyChannels(IReadOnlyList<UEGalaxyAudio.Playback> channels, int resumeSerial)
        {
            if (!ReferenceEquals(_rt, _host.Runtime)) Bind();
            System.ApplyChannels(channels, resumeSerial, _world);
        }

        // WinDrv WM_ACTIVATE/WM_SIZE -> Audio->SetViewport(NULL / viewport): input for Core (UEGalaxyAudio.SetViewport).
        public void Focus(bool present)
        {
            _host.Runtime?.FrameProducer?.Audio.SetViewport(present);
            System.SetPaused(!present);
        }

        public void Dispose() { if (Open) _out.Dispose(); }

        // Windowless probe (--audio-probe <ticks>): Core tick -> Update -> Fill without a device, report.
        public static int Probe(UEGameEngine host, string root, int ticks)
        {
            using var a = new GameAudio(host, root, device: false);
            const float dt = 1f / 60f;
            int perTick = (int)(a.System.Mixer.Rate * dt);
            var buf = new float[perTick * 2];
            double sum = 0; long n = 0; float peak = 0; int busyMax = 0, loudTicks = 0;
            for (int t = 0; t < ticks; t++)
            {
                host.Tick(1, dt);
                a.Update(false);
                lock (a.System.Gate) a.System.Fill(buf, perTick);
                double tickSum = 0;
                foreach (float v in buf) { tickSum += v * v; peak = Math.Max(peak, Math.Abs(v)); }
                sum += tickSum; n += buf.Length;
                if (tickSum / buf.Length > 1e-6) loudTicks++;
                busyMax = Math.Max(busyMax, a.System.Channels.BusyCount());
            }
            var g = host.Runtime.FrameProducer.Audio;
            Console.WriteLine($"audio: ticks {ticks}, RMS {Math.Sqrt(sum / Math.Max(1, n)):F4}, peak {peak:F3}, ticks with sound {loudTicks}, channels max {busyMax}");
            Console.WriteLine($"  commands: play {g.PlayedCommands}, stop {g.StoppedCommands}, no clip {g.UnresolvedClips}, ambient start/upd/stop {g.AmbientStarted}/{g.AmbientUpdated}/{g.AmbientStopped}, unknown {g.UnknownKinds}/{g.UnknownSoundParams}");
            Console.WriteLine($"  channels: refused {g.Rejected}, evicted {g.Evicted}, no listener {g.NoListenerRejected}; voices started {a.System.Channels.Created}, tails {a.System.Channels.TailsStarted}, unresolved {a.System.Channels.Unresolved}");
            Console.WriteLine($"  music: requests {g.RequestsConsumed}, songs {g.ResolvedSongRequests}, now '{g.CurrentSongPath}', playing {g.ActiveSong?.Playing == true}");
            Console.WriteLine($"  frames missed by sound {a.System.FramesMissed}");
            ReverbState rv = host.Runtime.PresentedFrame?.Reverb ?? default;
            Console.WriteLine($"  reverb: changes {a.System.ReverbChanges}, now {(a.System.Mixer.Reverb.Active ? "on" : "off")}, frame Valid {rv.Valid} MasterGain {rv.MasterGain} Cutoff {rv.CutoffHz}");
            return 0;
        }
    }
}
