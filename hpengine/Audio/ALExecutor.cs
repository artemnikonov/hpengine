// HPEngine HP2 AUDIO — the executor of the OpenAL calls Core's ALAudio device makes (WorldFrame.ALCommands, UEALAudio.Command).
// Core owns every decision (which source plays which sound, gain, pitch, position, fades, when a play ends, pause); this
// class only applies the calls in order to 32 OpenAL-like sources and mixes them, as OpenAL32.dll does for the original.
//
// The mixing is the platform's (the original's OpenAL32.dll / sound driver; not a game decision, DECISIONS.md "HP2 audio
// front: OpenAL executor"): OpenAL 1.1 source semantics (Play resumes a paused source, restarts any other at 0; Pause,
// Stop, Rewind; AL_PITCH > 0 only), AL_INVERSE_DISTANCE_CLAMPED (the model UALAudioSubsystem::Init sets) with
// AL_MAX_DISTANCE infinite, the OpenAL 1.1 Doppler shift (speed of sound 343.3, alDopplerFactor), equal-power stereo
// panning of mono data on the listener's right axis (at x up), stereo data unspatialised, linear resampling.
using HP1.UEScript;

namespace HPEngine.Audio
{
    public sealed class ALExecutor
    {
        public const int Initial = UEALAudio.AlInitial, Playing = UEALAudio.AlPlaying, Paused = UEALAudio.AlPaused,
            Stopped = UEALAudio.AlStopped;
        const float SpeedOfSound = 343.3f;

        public sealed class Source
        {
            public int State = Initial;
            public float Gain = 1f, Pitch = 1f, ReferenceDistance = 1f, Rolloff = 1f;
            public bool Looping, Relative;
            public UEVector Position, Velocity;
            public string Data;            // the attached sound (object path / music file); null: none
            public bool Stream, DataLoops;
            internal ISoundStream Reader;  // the data as read (a buffer is a PcmSoundStream over its PCM)
            internal double Frac;          // position between the two held frames
            internal short[] Two = new short[4];   // [prev L, prev R, cur L, cur R]
            internal bool Primed;
            internal readonly short[] One = new short[2];   // one data frame as read
        }

        public readonly Source[] Sources;
        public UEVector ListenerPosition, ListenerVelocity;
        public UEVector ListenerAt = new(0, 0, -1), ListenerUp = new(0, 1, 0);
        public float DopplerFactor = 1f;
        public readonly int Rate;
        readonly ISoundSource _sounds;
        readonly IMusicSource _music;
        public int Executed;

        public ALExecutor(int sources, int rate, ISoundSource sounds, IMusicSource music)
        {
            Sources = new Source[sources];
            for (int i = 0; i < sources; i++) Sources[i] = new Source();
            Rate = rate; _sounds = sounds; _music = music;
        }

        // The frame's calls, in call order. An unknown call is refused loudly (Core and the frontend disagree on the protocol).
        public void Execute(IReadOnlyList<UEALAudio.Command> commands)
        {
            foreach (UEALAudio.Command c in commands)
            {
                Executed++;
                if (c.Source < 0)
                {
                    switch (c.Op)
                    {
                        case "ListenerPosition": ListenerPosition = new UEVector(c.A, c.B, c.C); break;
                        case "ListenerVelocity": ListenerVelocity = new UEVector(c.A, c.B, c.C); break;
                        case "ListenerOrientation": ListenerAt = new UEVector(c.A, c.B, c.C); ListenerUp = new UEVector(c.D, c.E, c.F); break;
                        default: throw new InvalidDataException("ALExecutor: unknown listener call " + c.Op);
                    }
                    continue;
                }
                Source s = Sources[c.Source];
                switch (c.Op)
                {
                    case "Play":
                        if (s.State != Paused) Rewind(s);
                        s.State = Playing; break;
                    case "Pause": if (s.State == Playing) s.State = Paused; break;
                    case "Stop": s.State = Stopped; break;
                    case "Rewind": s.State = Initial; Rewind(s); break;
                    case "Gain": s.Gain = c.A; break;
                    case "Pitch": if (c.A > 0f) s.Pitch = c.A; break;
                    case "Looping": s.Looping = c.A != 0f; break;
                    case "Position": s.Position = new UEVector(c.A, c.B, c.C); break;
                    case "Velocity": s.Velocity = new UEVector(c.A, c.B, c.C); break;
                    case "ReferenceDistance": s.ReferenceDistance = c.A; break;
                    case "Rolloff": s.Rolloff = c.A; break;
                    case "Relative": s.Relative = c.A != 0f; break;
                    case "Buffer": Attach(s, c.A != 0f ? c.Sound : null, stream: false, ogg: false, loops: false); break;
                    case "QueueBuffers": Attach(s, c.Sound, stream: true, c.Ogg, c.DataLoops); break;
                    default: throw new InvalidDataException("ALExecutor: unknown source call " + c.Op);
                }
            }
        }

        void Attach(Source s, string data, bool stream, bool ogg, bool loops)
        {
            s.Data = data; s.Stream = stream; s.DataLoops = loops;
            s.Reader = data is null ? null
                : ogg ? (_music.Open(data) is IMusicStream m ? new MusicReader(m) : null)
                : stream ? _sounds.OpenStream(data)
                : _sounds.LoadPcm(data) is UEPcm p ? new PcmSoundStream(p) : null;
            if (data is not null && s.Reader is null) Console.Error.WriteLine($"[al] source data '{data}' not resolved: silence");
            s.Primed = false; s.Frac = 0;
        }

        static void Rewind(Source s)
        {
            s.Reader?.Restart();
            s.Primed = false; s.Frac = 0;
        }

        // Adds `frames` stereo frames of every playing source to dst (interleaved L/R, fractions of full scale).
        public void MixAdd(float[] dst, int frames)
        {
            foreach (Source s in Sources)
            {
                if (s.State != Playing || s.Reader is null) continue;
                (float gl, float gr, float pitch) = Spatial(s);
                bool mono = s.Reader.Channels == 1;
                double step = s.Reader.SampleRate * (double)pitch / Rate;
                for (int k = 0; k < frames; k++)
                {
                    if (!s.Primed) { if (!Next(s) || !Next(s)) { s.State = Stopped; break; } s.Primed = true; }
                    float l = (float)(s.Two[0] + (s.Two[2] - s.Two[0]) * s.Frac) / 32768f;
                    float r = (float)(s.Two[1] + (s.Two[3] - s.Two[1]) * s.Frac) / 32768f;
                    if (mono) { dst[2 * k] += l * gl; dst[2 * k + 1] += l * gr; }
                    else { dst[2 * k] += l * gl; dst[2 * k + 1] += r * gr; }
                    s.Frac += step;
                    bool ended = false;
                    while (s.Frac >= 1.0 && !ended) { s.Frac -= 1.0; ended = !Next(s); }
                    if (ended) { s.State = Stopped; break; }
                }
            }
        }

        // The next data frame into Two (shift cur to prev). End of data: a looping buffer or self-looping stream restarts.
        static bool Next(Source s)
        {
            int ch = s.Reader.Channels;
            short[] tmp = s.One;
            int got = s.Reader.Read(tmp, 0, ch);
            if (got < ch && ((!s.Stream && s.Looping) || (s.Stream && s.DataLoops)))
            {
                s.Reader.Restart();
                got = s.Reader.Read(tmp, 0, ch);
            }
            if (got < ch) return false;
            s.Two[0] = s.Two[2]; s.Two[1] = s.Two[3];
            s.Two[2] = tmp[0]; s.Two[3] = ch == 1 ? tmp[0] : tmp[1];
            return true;
        }

        // Gains (L, R) and the pitch after Doppler of one source, from the listener as Core last set it.
        public (float L, float R, float Pitch) Spatial(Source s)
        {
            float g = Math.Clamp(s.Gain, 0f, 1f);
            if (s.Reader is { Channels: 2 }) return (g, g, s.Pitch);
            UEVector rel = s.Relative ? s.Position : Sub(s.Position, ListenerPosition);
            float dist = Len(rel);
            float rd = s.ReferenceDistance;
            float d = Math.Max(dist, rd);
            float ad = rd + s.Rolloff * (d - rd);
            float att = ad > 0f ? rd / ad : 1f;
            float gain = g * Math.Clamp(att, 0f, 1f);
            float pan = 0f;
            if (dist > 0f && !s.Relative)
            {
                UEVector right = Cross(ListenerAt, ListenerUp);
                float rl = Len(right);
                if (rl > 0f) pan = Math.Clamp(Dot(rel, right) / (rl * dist), -1f, 1f);
            }
            float pitch = s.Pitch;
            if (DopplerFactor > 0f && dist > 0f && !s.Relative)
            {
                UEVector sl = new(-rel.x, -rel.y, -rel.z);   // source to listener
                float lim = SpeedOfSound / DopplerFactor;
                float vls = Math.Min(Dot(sl, ListenerVelocity) / dist, lim), vss = Math.Min(Dot(sl, s.Velocity) / dist, lim);
                float den = SpeedOfSound - DopplerFactor * vss;
                if (den > 0f) pitch *= (SpeedOfSound - DopplerFactor * vls) / den;
            }
            return (gain * MathF.Sqrt((1f - pan) * 0.5f), gain * MathF.Sqrt((1f + pan) * 0.5f), pitch);
        }

        static UEVector Sub(UEVector a, UEVector b) => new(a.x - b.x, a.y - b.y, a.z - b.z);
        static float Dot(UEVector a, UEVector b) => a.x * b.x + a.y * b.y + a.z * b.z;
        static float Len(UEVector a) => MathF.Sqrt(Dot(a, a));
        static UEVector Cross(UEVector a, UEVector b) => new(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);

        sealed class MusicReader : ISoundStream
        {
            readonly IMusicStream _m;
            public MusicReader(IMusicStream m) { _m = m; }
            public int SampleRate => _m.SampleRate;
            public int Channels => _m.Channels;
            public int SampleBytes => 2;
            public int Read(short[] dst, int offset, int count) => _m.Read(dst, offset, count);
            public void Restart() => _m.Restart();
        }
    }
}
