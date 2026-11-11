// OpenHP1 AUDIO INPUTS: where sound PCM and the music stream for voices come from, and device settings.
// Interfaces let the mixer run without game files (tests substitute synthetic data); the production implementations (Core*)
// read the Core catalogs (UESoundCatalog, UEMusicCatalog) and config state. Pool and music decisions belong to Core
// (UEGalaxyAudio) and do not come here.
using HP1.UEScript;

namespace OpenHP1.Audio
{
    // Package sounds (USound), from UESoundCatalog.Shared.
    public interface ISoundSource
    {
        bool Exists(string objectPath);
        UEPcm LoadPcm(string objectPath);
        // Name leaf -> exact path (null — the leaf is ambiguous or not found).
        string PathForUniqueLeaf(string leaf);
        // A voice's own stream (USound::newSoundStream per play), read by the Galaxy feeder as it fills the ring.
        ISoundStream OpenStream(string objectPath) => LoadPcm(objectPath) is UEPcm p ? new PcmSoundStream(p) : null;
    }

    // A sound's stream: channel-interleaved samples (8-bit data as (b-128)<<8); 0 — end of the lap; Restart — the next lap.
    public interface ISoundStream
    {
        int SampleRate { get; }
        int Channels { get; }
        int SampleBytes { get; }
        int Read(short[] dst, int offset, int count);
        void Restart();
    }

    // Over decoded PCM (WAV data, test sounds): a lap is the samples, Restart goes to LoopStart.
    public sealed class PcmSoundStream : ISoundStream
    {
        public readonly UEPcm Pcm;
        long _pos;
        public PcmSoundStream(UEPcm pcm) { Pcm = pcm ?? throw new ArgumentNullException(nameof(pcm)); }
        public int SampleRate => Pcm.SampleRate;
        public int Channels => Pcm.Channels;
        public int SampleBytes => Pcm.SampleBytes;
        public int Read(short[] dst, int offset, int count)
        {
            int n = (int)Math.Min(count, Pcm.Samples.Length - _pos);
            if (n <= 0) return 0;
            Array.Copy(Pcm.Samples, _pos, dst, offset, n);
            _pos += n;
            return n;
        }
        public void Restart() => _pos = (long)Pcm.LoopStart * Pcm.Channels;
    }

    // mp2: the engine's MP2SoundStream (decoded progressively, synthesis continuous across laps).
    public sealed class Mp2SoundStream : ISoundStream
    {
        readonly UEMp2SoundStream _s;
        public Mp2SoundStream(UEMp2SoundStream s) { _s = s; }
        public int SampleRate => _s.SampleRate;
        public int Channels => _s.Channels;
        public int SampleBytes => 2;
        public int Read(short[] dst, int offset, int count) => _s.Read(dst, offset, count);
        public void Restart() => _s.Restart();
    }

    // Music stream (UEMp2SoundStream — the engine's MP2SoundStream: Read/Restart, channel-interleaved samples).
    public interface IMusicStream
    {
        int SampleRate { get; }
        int Channels { get; }
        // Reads up to count samples (channel-interleaved); 0 — end.
        int Read(short[] dst, int offset, int count);
        // A new loop lap: for mp2 — bitstream restart without resetting synthesis, plus the skip (FUN_1037e550).
        void Restart();
    }

    // Music path resolution. null — the resource is not resolved (counted; plays silence).
    public interface IMusicSource
    {
        IMusicStream Open(string songPath);
    }

    // Device settings from Core config state (master volumes, reverb).
    public interface IAudioWorld
    {
        float SoundMaster { get; }
        // [Galaxy.GalaxyAudioSubsystem] UseReverb.
        bool UseReverb { get; }
    }

    // ── Production implementations over Core ────────────────────────────────

    public sealed class CoreSoundSource : ISoundSource
    {
        public bool Exists(string objectPath) => UESoundCatalog.Shared.Exists(objectPath);
        public UEPcm LoadPcm(string objectPath) => UESoundCatalog.Shared.LoadPcm(objectPath);
        public string PathForUniqueLeaf(string leaf) => UESoundCatalog.Shared.PathForUniqueLeaf(leaf);
        public ISoundStream OpenStream(string objectPath)
        {
            var (mp2, wav) = UESoundCatalog.Shared.OpenStream(objectPath);
            return mp2 != null ? new Mp2SoundStream(mp2) : new PcmSoundStream(wav);
        }
    }

    public sealed class CoreMusicSource : IMusicSource
    {
        sealed class Stream : IMusicStream
        {
            readonly UEMp2SoundStream _s;
            public Stream(UEMp2SoundStream s) { _s = s; }
            public int SampleRate => _s.SampleRate;
            public int Channels => _s.Channels;
            public int Read(short[] dst, int offset, int count) => _s.Read(dst, offset, count);
            public void Restart() => _s.Restart();
        }

        // The Core catalog throws on an unknown path (UEMusicCatalog.Canonical). Here the refusal is
        // LOUD (stderr + counter) and turns into "clip not resolved":
        // the request is counted, silence plays.
        public int Failures;
        public IMusicStream Open(string songPath)
        {
            if (string.IsNullOrEmpty(songPath)) return null;
            try { return new Stream(UEMusicCatalog.Shared.OpenStream(songPath)); }
            catch (Exception e)
            {
                Failures++;
                Console.Error.WriteLine($"[music] '{songPath}' not resolved: {e.Message}");
                return null;
            }
        }
    }

    public sealed class CoreAudioWorld : IAudioWorld
    {
        readonly UELevelRuntime _rt;
        public CoreAudioWorld(UELevelRuntime rt) { _rt = rt; }

        // Until config state exists — shipped ini defaults
        // (Default.ini:159/160) with ONE warning.
        internal const float SoundDefault = 200f / 255f;
        bool _warned;
        float Fallback(float value, string which)
        {
            if (_warned) return value;
            _warned = true;
            Console.Error.WriteLine("[audio] " + which + " requested before the config state was raised — using the "
                + "shipped ini default. If this repeats in the game, a sound "
                + "consumer starts before the world bootstrap.");
            return value;
        }
        public float SoundMaster { get { var c = _rt?.Config; return c is null ? Fallback(SoundDefault, "SoundVolume") : c.SoundMaster; } }

        // Everything except an explicit "False" is enabled
        // (shipped Default.ini: True).
        public bool UseReverb => !string.Equals(
            _rt?.Config?.Get(UEConfig.ScopeSystem, "Galaxy.GalaxyAudioSubsystem", "UseReverb"),
            "False", StringComparison.OrdinalIgnoreCase);
    }
}
