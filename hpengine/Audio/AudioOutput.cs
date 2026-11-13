// OUTPUT DEVICE — SDL3 audio stream (SDL_OpenAudioDeviceStream) with a callback.
//
// Format: float32 little-endian, stereo, mixer rate (AudioMixer.Rate — [Galaxy] OutputRate, NOT ORIGINAL, D-50).
// Values are an EXACT representation of the Galaxy converter's int16 (s16/32768f, saturation is
// already done in GalaxyMixKernel.ToS16); SDL converts them to the device format losslessly
// for a 16-bit device.
//
// The callback is called by the SDL device thread; it takes AudioSystem.Gate for the duration of Fill (drains the frame
// queue; mixes on demand only before the first Core frame).
// An exception in the callback cannot cross the unmanaged boundary — it is stored
// (Fault), the callback returns silence, and the main thread must check
// ThrowIfFaulted() once per frame: no silent degradation.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SDL;
using static SDL.SDL3;

namespace HPEngine.Audio
{
    public sealed unsafe class AudioOutput : IDisposable
    {
        readonly AudioSystem _system;
        SDL_AudioStream* _stream;
        GCHandle _self;
        float[] _buf = new float[0];

        public Exception Fault { get; private set; }
        public long FramesMixed;
        public int Callbacks;

        public AudioOutput(AudioSystem system) { _system = system; }

        // false — the device did not open (reason — SDL_GetError).
        public bool Open(out string error)
        {
            error = null;
            if (!BitConverter.IsLittleEndian) { error = "the mixer writes float32 LE, the platform is big-endian"; return false; }
            if (!SDL_InitSubSystem(SDL_InitFlags.SDL_INIT_AUDIO)) { error = SDL_GetError(); return false; }
            var spec = new SDL_AudioSpec { format = SDL_AudioFormat.SDL_AUDIO_F32LE, channels = 2, freq = _system.Mixer.Rate };
            _self = GCHandle.Alloc(this);
            _stream = SDL_OpenAudioDeviceStream(SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, &spec, &Callback, GCHandle.ToIntPtr(_self));
            if (_stream == null) { error = SDL_GetError(); _self.Free(); return false; }
            if (!SDL_ResumeAudioStreamDevice(_stream)) { error = SDL_GetError(); Dispose(); return false; }
            return true;
        }

        public void ThrowIfFaulted()
        {
            if (Fault != null) throw new InvalidOperationException("[audio] the device callback failed", Fault);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void Callback(IntPtr user, SDL_AudioStream* stream, int additional, int total)
        {
            var self = (AudioOutput)GCHandle.FromIntPtr(user).Target;
            int frames = additional / 8;   // 2 channels × 4 bytes
            if (frames <= 0) return;
            if (self._buf.Length < frames * 2) self._buf = new float[frames * 2];
            try
            {
                if (self.Fault == null) self._system.Fill(self._buf, frames);
                else Array.Clear(self._buf, 0, frames * 2);
            }
            catch (Exception e)
            {
                self.Fault = e;
                Array.Clear(self._buf, 0, frames * 2);
            }
            fixed (float* p = self._buf) SDL_PutAudioStreamData(stream, (IntPtr)p, frames * 8);
            self.FramesMixed += frames;
            self.Callbacks++;
        }

        public void Dispose()
        {
            if (_stream != null) { SDL_DestroyAudioStream(_stream); _stream = null; }
            if (_self.IsAllocated) _self.Free();
        }
    }
}
