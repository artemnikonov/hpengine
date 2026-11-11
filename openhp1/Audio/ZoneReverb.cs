// ZONE REVERB: functions over the effects-bus buffer with a parameter struct.
//
// Reference: Galaxy.dll. THIS file is checked bit-exactly by executing setup/callback under unicorn (PC64):
// se_harness/galaxy_reverb/galaxy_openhp1_dsp_check.py (gate [reverbdsp], red mutations
// audit/galaxy_reverb_dsp_redtest.sh):
//   • zone parameters (UpdateReverb FUN_106095c4) are computed by Core (ReverbState):
//     MasterGain/255 (wet), CutoffHz clamp[0,44100], 6×(Delay = byte·0.002 s
//     clamp[0.001,0.34], Gain = byte/255 clamp[0.001,0.999]); order [s1L,s1R,s2L,s2R,s3L,s3R];
//   • shape: SSE callback FUN_106206f3 / setup FUN_1060e2a0;
//   • send: mixer send bus 1 (gains (g·0x7f)>>7); return out = dry + wet·y3;
//     music goes to the send too (D-12: AudioMixer mixes MusicVoice into _dry/_send before Process).
//     Disasm: the music branch of Update (0x10608772 sound+0x64) starts the stream with FUN_1060fbf0(..., flags 1)
//     at 0x10608843; +0x11 = flags|8 or flags&~0xa (0x1060fd50..0x1060fd7b) — bit 0x80 clear; the mixer
//     (0x10612bf0) sends such a voice to the group-B buses ca64/ca6c/ca74 with master cedc; the reverb callback
//     0x106206f3 (installed in [0x1066cfe8] at 0x1060ece0) runs on (ca64, ca6c) at 0x106120bc..0x106120e0.
//   • rate: the mixer rate (glxInit 22050; OpenHP1 default 44100, D-50). Delays are in seconds (·rate, _ftol),
//     cutoff in Hz (cos(fc·2π/rate)), setup refuses fc > rate (Galaxy.dll.c:10334); all derived from
//     DAT_1067ca78, as Galaxy does at any listed rate. Bit-exact match proven at 22050 and 44100
//     (check --rate 44100, DAT_1067ca78 = 44100). Campaign cutoffs are 5000..12000 Hz, so no refusal at 22050 or above.
//
// ZONE PARAMETERS come from the Core frame: WorldFrame.Reverb (WorldFrameProjector.ProjectReverb: UpdateReverb
// over the viewer's Region.Zone, Galaxy clamps and multipliers already applied).
// When to apply them: AudioSystem.UpdateReverb (on a change of the struct words).
using HP1.UEScript;

namespace OpenHP1.Audio
{
    public sealed class ZoneReverb
    {
        // Immutable coefficient set.
        sealed class Dsp
        {
            public readonly int[] m = new int[6];
            public readonly float[] a = new float[6], c = new float[6];
            public float lpB, lpA, wet;
        }

        readonly int _rate;
        readonly int _size;
        readonly float[][] _bL, _bR;
        int _wi;
        float _fbL, _fbR;
        readonly float[] _lp = new float[6];
        Dsp _dsp, _dspInUse;

        // Setup refusals (cutoff above rate): loud, via counter and stderr.
        public int CutoffRefused;

        public ZoneReverb(int rate)
        {
            _rate = rate;
            // Maximum delay 0.34 s plus ring headroom.
            _size = (int)MathF.Ceiling(0.35f * _rate) + 8;
            _bL = new float[3][]; _bR = new float[3][];
            for (int k = 0; k < 3; k++) { _bL[k] = new float[_size]; _bR[k] = new float[_size]; }
        }

        public bool Active => _dsp != null;
        // Filter coefficient of a stage of the current set (for tests; NaN when there is no set).
        public float LowpassBForTest => _dsp?.lpB ?? float.NaN;

        // New struct words (glxSetSampleReverb FUN_1060ec80). As in the reference, ANY new set
        // resets the state on the next Process (setup FUN_1060e2a0): the tail is cut.
        // MasterGain == 0.0 means the callback is removed (FUN_1060ec80: param_1[3] == 0.0).
        public void Set(in ReverbState r)
        {
            _dsp = r.MasterGain != 0f ? Build(r) : null;
        }

        // Seconds/gains already per UpdateReverb (Core).
        Dsp Build(in ReverbState r)
        {
            var d = new Dsp();
            for (int k = 0; k < 6; k++)
            {
                d.m[k] = Math.Clamp((int)(r.Delay(k) * _rate), 1, _size - 1);   // trunc, as FUN_1060e2a0 (_ftol)
                // FUN_1060e2a0 0x1060e38d-0x1060e413 (x87, PC64: setup is called from Galaxy Update
                // before Render PreRender): a = 1 - Gain stays UNROUNDED on the x87 stack;
                // +0x38 = fst dword a, and C (+0x50) = 1 - a·a from the UNROUNDED a.
                // (1 - Gain is exact in double; a·a is one rounding, then to float.)
                double ae = 1.0 - r.Gain(k);
                d.a[k] = (float)ae;
                d.c[k] = (float)(1.0 - ae * ae);
            }
            float fc = r.CutoffHz;   // already clamp [0, 0xac44] (UpdateReverb)
            if (fc > _rate)
            {
                CutoffRefused++;
                Console.Error.WriteLine($"[Reverb] CutoffHz {fc} above the output rate {_rate} — Galaxy setup would refuse, reverb disabled");
                return null;
            }
            double cc = 1.0 - Math.Cos(fc * (double)6.282f / _rate);   // 2π Galaxy = fmul dword 6.282f (0x1063b9c4)
            d.lpB = (float)(Math.Sqrt((cc + 2.0) * cc) - cc);
            d.lpA = 1f - d.lpB;
            d.wet = Math.Clamp(r.MasterGain, 0f, 1f);
            return d;
        }

        // Dry group-B bus (stereo, interleaved) in place: out = dry + wet·y3;
        // stage input is send bus 1, already filled by the mixer at level
        // (g·0x7f)>>7 (callback DAT_1066cfe8 receives (ca64, ca6c), 0x106120bc-0x106120e1).
        public void Process(float[] data, float[] send, int frames)
        {
            var d = _dsp;
            if (!ReferenceEquals(d, _dspInUse))
            {
                _dspInUse = d;
                for (int k = 0; k < 3; k++) { Array.Clear(_bL[k], 0, _size); Array.Clear(_bR[k], 0, _size); }
                Array.Clear(_lp, 0, 6); _fbL = _fbR = 0f; _wi = 0;
            }
            if (d == null) return;
            int size = _size;
            for (int i = 0; i < frames; i++)
            {
                int j = i << 1;
                float inL = data[j], inR = data[j + 1];
                // Send: send bus 1 (level 0x7f/128, already in the voice's integer gains).
                float sL = send[j], sR = send[j + 1];
                // Cross feedback (DAT_10648d60 = 0.5,0.5).
                float xL = (_fbR + sL) * 0.5f, xR = (_fbL + sR) * 0.5f;
                for (int st = 0; st < 3; st++)
                {
                    xL = Stage(_bL[st], size, d, 2 * st, xL);
                    xR = Stage(_bR[st], size, d, 2 * st + 1, xR);
                }
                _fbL = xL; _fbR = xR;
                data[j] = inL + d.wet * xL;
                data[j + 1] = inR + d.wet * xR;
                if (++_wi >= size) _wi = 0;
            }
        }

        // Stage FUN_106206f3: t = buf[idx-M]; lp = b·t + (1-b)·lp;
        // buf[idx] = x + a·lp; y = -a·x + C·t.
        float Stage(float[] buf, int size, Dsp d, int k, float x)
        {
            int r = _wi - d.m[k]; if (r < 0) r += size;
            float t = buf[r];
            float lp = t * d.lpB + _lp[k] * d.lpA;
            _lp[k] = lp;
            buf[_wi] = x + lp * d.a[k];
            return x * -d.a[k] + t * d.c[k];
        }
    }
}
