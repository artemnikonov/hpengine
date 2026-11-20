// HPEngine HP2 AUDIO — the executor of the OpenAL calls Core's ALAudio device makes (WorldFrame.ALCommands, UEALAudio.Command).
// Core owns every decision (which source plays which sound, gain, pitch, position, fades, when a play ends, pause); this
// class only applies the calls in order to 32 OpenAL-like sources and mixes them, as OpenAL32.dll does for the original.
//
// The mixing is the platform's (the original's OpenAL32.dll / sound driver; not a game decision, DECISIONS.md "HP2 audio
// front: OpenAL executor"): OpenAL 1.1 source semantics (Play resumes a paused source, restarts any other at 0; Pause,
// Stop, Rewind; AL_PITCH > 0 only), AL_INVERSE_DISTANCE_CLAMPED (the model UALAudioSubsystem::Init sets) with
// AL_MAX_DISTANCE infinite, the shipped OpenAL32.dll's Doppler pitch (DopplerPitch, alDopplerFactor), the stereo pan of mono
// data as the shipped OpenAL32.dll's software device computes it (Spatial; NOT the OpenAL 1.1 at x up right axis), stereo
// data unspatialised, linear resampling.
using HP1.UEScript;

namespace HPEngine.Audio
{
    public sealed class ALExecutor
    {
        public const int Initial = UEALAudio.AlInitial, Playing = UEALAudio.AlPlaying, Paused = UEALAudio.AlPaused,
            Stopped = UEALAudio.AlStopped;

        public sealed class Source
        {
            public int State = Initial;
            public float Gain = 1f, Pitch = 1f, ReferenceDistance = 1f, Rolloff = 1f;
            public bool Looping, Relative;
            public UEVector Position, Velocity;
            public string Data;            // the attached sound (object path / music file); null: none
            public bool Stream, DataLoops;
            internal bool Ogg, Read;       // Read: the reader was read from since it was opened
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
        public int Executed, Plays;

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
                        // alDopplerFactor [HP2 OpenAL32 0x10005bf0]: below 0 (or NaN) is AL_INVALID_VALUE, the factor stays
                        case "DopplerFactor": if (!(c.A < 0f) && !float.IsNaN(c.A)) DopplerFactor = c.A; break;
                        default: throw new InvalidDataException("ALExecutor: unknown listener call " + c.Op);
                    }
                    continue;
                }
                Source s = Sources[c.Source];
                switch (c.Op)
                {
                    case "Play":
                        if (s.State != Paused) Rewind(s);
                        s.State = Playing; Plays++; break;
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
            s.Data = data; s.Stream = stream; s.DataLoops = loops; s.Ogg = ogg; Open(s);
            if (data is not null && s.Reader is null) Console.Error.WriteLine($"[al] source data '{data}' not resolved: silence");
        }

        void Open(Source s)
        {
            string data = s.Data;
            s.Reader = data is null ? null
                : s.Ogg ? (_music.Open(data) is IMusicStream m ? new MusicReader(m) : null)
                : s.Stream ? _sounds.OpenStream(data)
                : _sounds.LoadPcm(data) is UEPcm p ? new PcmSoundStream(p) : null;
            s.Read = false; s.Primed = false; s.Frac = 0;
        }

        // Back to the start of the attached data: a reader not read since it was opened is already there; one read from is
        // opened again (a reader's Restart is the next loop lap — XA refuses it mid-lap, a buffer's goes to its LoopStart).
        void Rewind(Source s)
        {
            if (s.Read) Open(s);
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
            s.Read = true;
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

        // Original: not exported: FUN_100112c0 [HP2 OpenAL32 0x100112c0] (the software mixer's per-source gain/pan; called at
        // 0x10011bcc with mode 2; reads the listener through alGetListenerfv 0x10003810) — the pan; the gain part as before.
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
            // The pan of HP2's device: ALAudio Init opens "DirectSound" (UseEAX False), OpenAL32.dll's own software mixer,
            // whose panner (stereo mode 2) builds r1 = up x at', r2 = at' x r1, r3 = at' (at' = at with z negated, each
            // normalised) and multiplies the source offset by the matrix with ROWS (r1.x, r2.x, r3.x), (r1.y, ..), (r1.z, ..)
            // — the basis transposed — then pans by x of the normalised result: p = 0.5 + 0.4 sign(x) sqrt|x|,
            // L = sqrt(1 - p), R = sqrt(p). Relative sources are panned too (only the listener offset is skipped).
            // ALAudio's listener frame (Update, UseEAX False) is built for this panner: an OpenAL 1.1 (at x up) panner
            // swaps left and right for a camera facing world +-X. Judge: tools/hp2/openal_pan_oracle.py (AudioTests [al]).
            UEVector atp = Norm(new UEVector(ListenerAt.x, ListenerAt.y, -ListenerAt.z));
            UEVector r1 = Norm(Cross(ListenerUp, atp)), r2 = Norm(Cross(atp, r1));
            UEVector v = new(r1.x * rel.x + r2.x * rel.y + atp.x * rel.z, r1.y * rel.x + r2.y * rel.y + atp.y * rel.z,
                r1.z * rel.x + r2.z * rel.y + atp.z * rel.z);
            float vl = Len(v), x = vl > 0f ? v.x / vl : 0f;
            float p = 0.5f + (x >= 0f ? 0.4f * MathF.Sqrt(x) : -0.4f * MathF.Sqrt(-x));
            return (gain * MathF.Sqrt(1f - p), gain * MathF.Sqrt(p), DopplerPitch(s, rel, d));
        }

        // Original: not exported: FUN_100112c0 [HP2 OpenAL32 0x100112c0] — its pitch block (0x100117b0..0x1001182f), not the
        // OpenAL 1.1 formula: w = (v_source − v_listener)·rel, rel the offset the pan uses (a relative source's position as
        // is), L the distance clamped to AL_REFERENCE_DISTANCE. With alDopplerFactor DF != 0 and L != 0:
        // pitch = P·DF / (1 + w / (L·343)) — DF scales the PITCH (Creative OpenAL 1.0), relative sources included, no
        // clamp; otherwise pitch = P / (1 + w·(1/343) [float 0x1001a2b8]), w not normalised. Judge: the shipped body under
        // unicorn (HP1_archive/night_2026-11-18/sound_audit_doppler_probe.py; AudioTests [al] doppler).
        float DopplerPitch(Source s, UEVector rel, float clampedDistance)
        {
            UEVector dv = Sub(s.Velocity, ListenerVelocity);
            double w = (double)dv.x * rel.x + (double)dv.y * rel.y + (double)dv.z * rel.z;
            if (DopplerFactor != 0f && clampedDistance != 0f)
                return (float)((double)s.Pitch * DopplerFactor / (w / ((double)clampedDistance * 343f) + 1.0));
            return (float)(s.Pitch / (w * (double)(1f / 343f) + 1.0));
        }

        static UEVector Sub(UEVector a, UEVector b) => new(a.x - b.x, a.y - b.y, a.z - b.z);
        static float Dot(UEVector a, UEVector b) => a.x * b.x + a.y * b.y + a.z * b.z;
        static float Len(UEVector a) => MathF.Sqrt(Dot(a, a));
        static UEVector Norm(UEVector a) { float l = Len(a); return l > 0f ? new UEVector(a.x / l, a.y / l, a.z / l) : a; }
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
