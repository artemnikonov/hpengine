// MPEG-1/2 LAYER II DECODER — PORT OF THE ENGINE.DLL DECODER (CMpegBase, integer MMX path).
//
// Shipped audio is mp2: all voice (AllDialog.uax), music (Music/*.umx) and almost all
// effects (HPSounds etc.); the rest is PCM WAV. The build ships no original files and decodes mp2
// from the user's packages itself.
//
// FRAME PARSING follows ISO/IEC 11172-3 / 13818-3 (LSF), layer II: header, bit allocation, scfsi,
// scale factors, 3/5/9-level grouping. The tables (alloc_0..4, sblims, translate,
// intwinbase = D·65536, tabsel) are standard data taken from libmpg123; they match the Engine.dll
// tables byte-for-byte (alloc PTR_DAT_104df164[], sblimit DAT_10484a48, translate 0x104849e8).
//
// DEQUANTISATION AND SYNTHESIS follow Engine.dll, not the standard. The Engine static initialiser
// (0x104d2ac0 -> 0x10452cd0 -> FUN_10452ce0) sets DAT_10601e9c = cpuid.1:EDX bit 23 (MMX); on any
// CPU that runs the game it is 1, so layer II takes the integer path FUN_10456190: dequantisation
// FUN_10455780, synthesis FUN_104579f0 (window) + FUN_10459070 (matrix).
// Semantics come from symbolic execution of these bodies (disasm) and are confirmed by running the
// real code under unicorn — the MP2SoundStream
// (FUN_1037e550) stream against Core byte-for-byte, over every shipped mp2, two loops in a row.
// This path has no x87 instruction at all — the FPU control word does not affect the output.
//   * int16 subband sample: ungrouped ((c + off)·Q(2/(2^b-1), sf)) >> 12, grouped
//     Q((2d-(L-1))/L, sf) >> 12 (group table 0x104df13c: codes beyond L^3 are zeros); truncation to
//     int16 without saturation. Q(F, sf) = (int)(float)(F·2^(27 - sf/3)) — table DAT_10480fc8
//     (27×64 int32), sf = 63 -> 0;
//   * matrix (FUN_10459070): u_i = x_i ± x_{31-i} (int16 with wraparound), rows
//     (Σ C·u) >> 14 -> int16 (truncation), C = (int)(16384·cos(πK(2i+1)/64) + 0.5) (table
//     0x1060f200); 17 rows (K = 16 - r) into half p = t&1, column (t+p)&15, and 16 rows
//     (K = 16 + r) into half p^1, column t + (p^1); an even row is a sum, an odd row a difference;
//   * window (FUN_104579f0): reads half R = 1 - (t&1), age column a — (t + R - a)&15;
//     out_j = sat16((Σ_a W[j][a]·V[R][row_j][col]) >> 13), row_j = j (j ≤ 16) or 32 - j;
//     W[j][a] = s·trunc(D[(32a - j) & 511]·16384) (= intwinbase/4 toward zero, sign per 64-blocks),
//     s = -1 for 1 ≤ j ≤ 15 and even a; out_16 — only odd a and the term
//     ((W·V) >> 16) << 16 (pmulhw + pslld). Sums are int32 with wraparound (paddd);
//   * t — byte +0x24bc: 1 after Open (FUN_10452bc0), decremented by 1 before each 32-sample slice
//     (& 15). V is zeroed by Open (FUN_10452730); a loop restart (FUN_10453150) does NOT touch them.
using System;
using System.Collections.Generic;
using System.IO;

namespace HP1.UEScript
{
    public sealed class UEPcm
    {
        public int SampleRate;
        public int Channels;
        public short[] Samples;            // interleaved channels
        // Frame the loop repeats from (0 = start). For mp2, loops after the first continue the
        // synthesis filter (UEMp2SoundStream.ShippedPcm) — the repeated loop is not the first one.
        public int LoopStart;
        // Bytes per sample of the stream Galaxy reads (WAV 8-bit: 1; 16-bit and mp2: 2).
        public int SampleBytes = 2;
        public int Frames => Channels == 0 ? 0 : Samples.Length / Channels;
    }

    // Sound PCM by package format (USound.FileType): "WAV" — the data chunk as is (unsigned 8-bit
    // is converted to signed 16-bit as any WAV player does: (b-128)<<8). mp2 does not come here: its
    // PCM is an engine stream loop (UEMp2SoundStream.ShippedPcm, needs iEndPosn from the package
    // tail). The shipped data has no other formats — refusal.
    public static class UESoundPcm
    {
        public static UEPcm Decode(byte[] payload, string format, string what)
        {
            if (!string.Equals(format, "WAV", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"{what}: sound format {format}");
            if (payload.Length < 12 || payload[0] != 'R' || payload[1] != 'I' || payload[2] != 'F' || payload[3] != 'F'
                || payload[8] != 'W' || payload[9] != 'A' || payload[10] != 'V' || payload[11] != 'E')
                throw new InvalidDataException($"{what}: WAV without a RIFF/WAVE header");
            int channels = 0, rate = 0, bits = 0, dataAt = -1, dataLen = 0;
            for (int pos = 12; pos + 8 <= payload.Length;)
            {
                int size = BitConverter.ToInt32(payload, pos + 4);
                if (size < 0 || pos + 8 + size > payload.Length) throw new InvalidDataException($"{what}: truncated WAV chunk");
                if (payload[pos] == 'f' && payload[pos + 1] == 'm' && payload[pos + 2] == 't' && payload[pos + 3] == ' ')
                {
                    if (BitConverter.ToUInt16(payload, pos + 8) != 1)
                        throw new NotSupportedException($"{what}: WAV is not PCM (tag {BitConverter.ToUInt16(payload, pos + 8)})");
                    channels = BitConverter.ToUInt16(payload, pos + 10);
                    rate = BitConverter.ToInt32(payload, pos + 12);
                    bits = BitConverter.ToUInt16(payload, pos + 22);
                }
                else if (payload[pos] == 'd' && payload[pos + 1] == 'a' && payload[pos + 2] == 't' && payload[pos + 3] == 'a')
                { dataAt = pos + 8; dataLen = size; }
                pos += 8 + size + (size & 1);
            }
            if (dataAt < 0 || channels <= 0 || rate <= 0) throw new InvalidDataException($"{what}: WAV without fmt/data");
            short[] samples;
            if (bits == 16)
            {
                samples = new short[dataLen / 2 / channels * channels];
                Buffer.BlockCopy(payload, dataAt, samples, 0, samples.Length * 2);
            }
            else if (bits == 8)
            {
                samples = new short[dataLen / channels * channels];
                for (int i = 0; i < samples.Length; i++) samples[i] = (short)((payload[dataAt + i] - 128) << 8);
            }
            else throw new NotSupportedException($"{what}: WAV {bits} bits");
            return new UEPcm { SampleRate = rate, Channels = channels, Samples = samples, SampleBytes = bits / 8 };
        }
    }

    // mp2 stream — the CMpegBase decoder: frames are indexed up front (offsets, format), PCM is produced
    // per frame (a frame is decoded when its sample is first needed — as FUN_104531e0). Synthesis state
    // (V and t) lives as long as the stream; Rewind moves the bitstream to the start WITHOUT resetting
    // synthesis (FUN_10453150). Full decoding is the same read (UEMpegAudio.Decode).
    public sealed class UEMpegStream
    {
        public const int FrameSamples = 1152;
        readonly byte[] _data;
        readonly string _what;
        readonly int[] _offsets;
        readonly UEMpegAudio.EngineSynth _synth = new UEMpegAudio.EngineSynth();
        readonly short[] _sub = new short[3 * 2 * 32 * 12];
        readonly short[] _buf;
        int _bufPos, _bufLen, _next;

        public int SampleRate { get; }
        public int Channels { get; }
        public long Frames => (long)_offsets.Length * FrameSamples;   // samples per channel

        public UEMpegStream(byte[] data, string what)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            _what = what;
            var offsets = new List<int>();
            int pos = 0;
            while (pos + 4 <= data.Length)
            {
                uint h = (uint)(data[pos] << 24 | data[pos + 1] << 16 | data[pos + 2] << 8 | data[pos + 3]);
                var hd = UEMpegAudio.ParseHeader(h, offsets.Count, pos, what);
                if (offsets.Count == 0) { SampleRate = hd.Rate; Channels = hd.Channels; }
                else if (hd.Rate != SampleRate || hd.Channels != Channels)
                    throw new InvalidDataException($"{what}: frame {offsets.Count} changes format ({hd.Rate} Hz × {hd.Channels} versus {SampleRate} × {Channels})");
                if (pos + hd.Bytes > data.Length) break;   // a truncated last frame is not decoded
                offsets.Add(pos);
                pos += hd.Bytes;
            }
            if (offsets.Count == 0) throw new InvalidDataException($"{what}: mp2 without a single whole frame");
            _offsets = offsets.ToArray();
            _buf = new short[FrameSamples * Channels];
        }

        // Bitstream back to frame 0; synthesis (V, t) untouched (FUN_10453150).
        public void Rewind()
        {
            _next = 0;
            _bufPos = _bufLen = 0;
        }

        // Reads up to count samples (interleaved channels); 0 = end of data.
        public int Read(short[] dst, int offset, int count)
        {
            int done = 0;
            while (done < count)
            {
                if (_bufPos >= _bufLen)
                {
                    if (_next >= _offsets.Length) break;
                    UEMpegAudio.DecodeFrame(_data, _offsets[_next], _next, _synth, _sub, _buf, _what);
                    _next++;
                    _bufPos = 0; _bufLen = _buf.Length;
                }
                int n = Math.Min(count - done, _bufLen - _bufPos);
                Array.Copy(_buf, _bufPos, dst, offset + done, n);
                _bufPos += n; done += n;
            }
            return done;
        }
    }

    // mp2 SOUND STREAM AS THE ENGINE DELIVERS IT: MP2SoundStream::FillBuffer (Engine FUN_1037e550,
    // disasm 0x1037e550-0x1037e7a1). ALL game mp2 goes through it: a USound with +0x6c (bIsMP2) —
    // newSoundStream (Engine.dll.c:41845); music — Galaxy Update builds a temporary USound from the
    // UMusic (USound(UMusic*,bool), Engine.dll.c:146468; Galaxy.dll.c:7407) and plays it the same way.
    // In game the decoder is CMpegFilePlayer (+0x70 = 1: USound::Serialize, Engine.dll.c:71555); it reads
    // data via FSoundData::Buffer (0x1037ef20); its synthesis is the same (FUN_10452c80 -> FUN_10456190).
    //   * at loop start (mPosn +0x28 == 0) the decoder emits skip bytes into the junk buffer DAT_105ddb18:
    //     skip = FUN_10452a80 by the layer byte +0x2490 (FUN_10452760: 4 - ((h>>17)&3)) — layer I 0x442,
    //     layer II 0x3c2, else 0 (0x10452a80-0x10452a9e). The unit is BYTES of interleaved int16 output
    //     (decoder frame +0x248c = channels·0x900), independent of channel count: mono — 481 samples,
    //     stereo — 240 frames plus ONE sample, i.e. the stereo stream is shifted by half a frame
    //     (the RIGHT sample of frame 240 comes first — confirmed by execution under unicorn);
    //   * a loop is exactly iEndPosn bytes (+0x4c: USound — tail word v76, 0x1038018e; UMusic —
    //     Data.mUncompressedLength, Engine.dll.c:80303/80311). 0 in shipped means up to the end of decoder
    //     data (for a loop — assert "!(iEndPosn==0 && IsLooping)", 0x1037e5dc); here a refusal (see below);
    //   * loop (+0x54): FUN_10452650 -> FUN_10453150 (0x10453150-0x10453176) resets ONLY the bitstream
    //     and frame-buffer positions (+0x24e8/+0x24ac/+0x24a8/+0x24e4/+0x24b4); the synthesis filter
    //     (V +4.., index +0x24bc) is zeroed only by Open (FUN_10452730) — state is CONTINUOUS across the
    //     seam, and the new loop (skipping skip bytes again) starts from the tail of the previous loop.
    //     A slice-n sample depends on slices n-15..n; the 481-sample mono skip (15 slices and 1 sample)
    //     washes the tail out — mono loop k equals loop 1; stereo skips only 240 samples per channel,
    //     and the first 240 frames of loop k ≥ 2 depend on the end of loop k-1 (oracle measurement: up to
    //     1414 units at the AMB_pre_dungeon seam). Loops k ≥ 3 equal loop 2 (the loop ends on the same
    //     last frame).
    // Restart here is FUN_10453150 (decoder Rewind without synthesis reset) plus the same skip.
    public sealed class UEMp2SoundStream
    {
        readonly UEMpegStream _dec;
        readonly int _skip;          // int16 samples skipped (skip bytes / 2)
        readonly long _loop;         // int16 samples per loop (iEndPosn / 2)
        readonly short[] _scratch;
        long _pos;                   // delivered in this loop (mPosn / 2)
        readonly bool _endsEarly;    // iEndPosn past the decoder output: the stream finishes after lap 1
        readonly bool _oddEnd;       // an odd byte lap: lap 2 would start mid-sample

        public int SampleRate => _dec.SampleRate;
        public int Channels => _dec.Channels;
        public long LoopSamples => _loop;   // int16, interleaved channels

        // FUN_10452a80: skip bytes by layer.
        public static int SkipBytes(int layer) => layer == 1 ? 0x442 : layer == 2 ? 0x3c2 : 0;

        public UEMp2SoundStream(byte[] data, int endBytes, string what)
        {
            _dec = new UEMpegStream(data, what);   // accepts layer II only
            int layer = 4 - ((data[1] >> 1) & 3);
            _skip = SkipBytes(layer) / 2;
            if (endBytes < 0)
                throw new InvalidDataException($"{what}: iEndPosn {endBytes} < 0");
            long avail = _dec.Frames * _dec.Channels - _skip;
            // iEndPosn == 0: shipped delivers up to the end of decoder data, and on the frame past the end
            // reads memory beyond the buffer (FUN_10452ab0 without a bound; the "mpeg fail" frame of
            // FUN_104531e0 is not counted). In shipped data no such sound reaches a stream (the only mp2
            // with zero, HPSounds.menu_sfx.s_menu_enter, has bIsMP2 = 0 — the catalog refuses earlier) —
            // not ported, STOP.
            if (endBytes == 0)
                throw new NotSupportedException($"{what}: iEndPosn == 0 — the \"to the end of decoder data\" loop is not ported (STOP)");
            // Decoder shorter than the loop (RU AllDialog: 1366 voice lines): the frame past the data fails in the
            // header read (FUN_104529c0: FSoundData::Buffer 0x1037ef20 clamps to Data.Num, returns != 4 ->
            // "waiting for cd access"), FUN_104531e0 does not count it ("mpeg fail 2"), so FillBuffer's read comes back
            // short (0x1037e65d) and the stream is finished (+8 = 1, 0x1037e664) after exactly the decoded bytes, looping
            // or not. An odd iEndPosn (RU, all of them also past the decoder) makes the last read an odd byte count;
            // the Galaxy voice end is the ring byte position >> 1 (0x1060bf1d-0x1060bf24), so the half sample is not
            // played: the lap is floor(bytes / 2) int16 samples.
            long laps = Math.Min(endBytes, 2 * avail);
            _endsEarly = endBytes > 2 * avail;
            _oddEnd = (laps & 1) != 0;
            _loop = laps / 2;
            _scratch = new short[_skip];
            Skip();
        }

        void Skip()
        {
            if (_dec.Read(_scratch, 0, _skip) != _skip) throw new InvalidOperationException("mp2: skip past the end of data");
            _pos = 0;
        }

        // New loop (FUN_1037e550 loop 0x1037e6c0): bitstream restart without synthesis reset, then skip.
        public void Restart()
        {
            // A finished stream (early end) does not loop in shipped; an odd lap shifts the byte phase of lap 2.
            // Neither occurs for a looping sound or a song in the shipped data (US, RU) — not ported, STOP.
            if (_endsEarly || _oddEnd)
                throw new NotSupportedException($"mp2 loop restart after {(_endsEarly ? "an early end of stream" : "an odd-byte lap")} — not ported (STOP)");
            _dec.Rewind();
            Skip();
        }

        // Up to count samples of the loop; 0 = end of loop (the caller loops via Restart).
        public int Read(short[] dst, int offset, int count)
        {
            int n = (int)Math.Min(count, _loop - _pos);
            if (n <= 0) return 0;
            int got = _dec.Read(dst, offset, n);
            _pos += got;
            return got;
        }

        short[] Circle(string what)
        {
            var c = new short[_loop];
            int read = 0;
            while (read < c.Length) { int n = Read(c, read, c.Length - read); if (n == 0) break; read += n; }
            if (read != c.Length) throw new InvalidOperationException($"{what}: mp2 loop not read completely ({read}/{c.Length})");
            return c;
        }

        // Sound PCM for a voice that repeats data itself (HPEngine mixer): no loop — loop 1; looping —
        // loop 1 plus the repeated part from frame LoopStart. Loops k ≥ 2 equal loop 2 (see above), so a
        // loop is loop 1, then loop 2 repeated; if loop 2 equals loop 1 (mono), one loop remains. Stereo
        // with an odd sample count per loop: the per-frame layout repeats every TWO loops (each loop
        // shifts the channel parity) — the start is "loop 1 + loop 2", the repeat "loop 2 + loop 2";
        // without a loop the last unpaired sample is dropped (ASSUMPTION: how the Galaxy ring consumes
        // the half frame is not read; shipped data has 6 entries of the form iEndPosn = output - 0x3c2,
        // e.g. HPSounds.Hub2_sfx.waterfall2).
        public static UEPcm ShippedPcm(byte[] data, int endBytes, bool looping, string what)
        {
            var s = new UEMp2SoundStream(data, endBytes, what);
            short[] c1 = s.Circle(what);
            int ch = s.Channels;
            bool odd = ch == 2 && (c1.Length & 1) != 0;
            var pcm = new UEPcm { SampleRate = s.SampleRate, Channels = ch };
            if (!looping)
            {
                pcm.Samples = odd ? c1[..^1] : c1;
                return pcm;
            }
            s.Restart();
            short[] c2 = s.Circle(what);
            short[] head = odd ? Join(c1, c2) : c1, body = odd ? Join(c2, c2) : c2;
            if (head.AsSpan().SequenceEqual(body)) { pcm.Samples = body; return pcm; }
            pcm.Samples = Join(head, body);
            pcm.LoopStart = head.Length / ch;
            return pcm;
        }

        static short[] Join(short[] a, short[] b)
        {
            var r = new short[a.Length + b.Length];
            a.CopyTo(r, 0); b.CopyTo(r, a.Length);
            return r;
        }
    }

    public static class UEMpegAudio
    {
        public static UEPcm Decode(byte[] data, string what)
        {
            var stream = new UEMpegStream(data, what);
            var output = new short[stream.Frames * stream.Channels];
            stream.Read(output, 0, output.Length);
            return new UEPcm { SampleRate = stream.SampleRate, Channels = stream.Channels, Samples = output };
        }

        internal struct Header
        {
            public int Rate, Channels, Bytes, Mode, ModeExt, Table;
            public bool Crc;
        }

        internal static Header ParseHeader(uint h, int frame, int pos, string what)
        {
            if ((h & 0xFFE00000u) != 0xFFE00000u)
            {
                if (frame == 0) throw new InvalidDataException($"{what}: mp2 without a sync word at the start (0x{h:x8})");
                throw new InvalidDataException($"{what}: mp2 lost the sync word at byte {pos} after {frame} frames");
            }
            int version = (int)(h >> 19) & 3;          // 3 — MPEG-1, 2 — MPEG-2, 0 — MPEG-2.5
            int layer = 4 - ((int)(h >> 17) & 3);
            int brIndex = (int)(h >> 12) & 15;
            int srIndex = (int)(h >> 10) & 3;
            if (version == 1 || layer != 2 || brIndex == 0 || brIndex == 15 || srIndex == 3)
                throw new NotSupportedException($"{what}: frame {frame} is not MPEG Layer II or has a free bitrate (0x{h:x8})");
            bool lsf = version != 3;
            int fs = srIndex == 0 ? 44100 : srIndex == 1 ? 48000 : 32000;
            if (version == 2) fs >>= 1; else if (version == 0) fs >>= 2;
            int mode = (int)(h >> 6) & 3;
            int nch = mode == 3 ? 1 : 2;               // FUN_10452760: (mode != 3) + 1 — all stereo modes alike
            int bitrate = Layer2Bitrate[(lsf ? 16 : 0) + brIndex] * 1000;
            return new Header
            {
                Rate = fs, Channels = nch, Mode = mode, ModeExt = (int)(h >> 4) & 3,
                Crc = ((h >> 16) & 1) == 0,
                Bytes = 144 * bitrate / fs + ((int)(h >> 9) & 1),
                Table = lsf ? 4 : Translate[srIndex * 32 + (2 - nch) * 16 + brIndex],
            };
        }

        // CMpegBase synthesis state: V (decoder+4, 0x480 bytes per channel: 2 halves × 18 rows × 16
        // int16 columns) and t (byte +0x24bc).
        internal sealed class EngineSynth
        {
            public readonly short[] V = new short[2 * 2 * 18 * 16];
            public int T = 1;
        }

        // One frame (FUN_10456190, MMX branch) -> 1152 samples per channel into output (interleaved).
        // sub — working buffer of subband samples for 12 granules (the engine uses global DAT_106021bc).
        internal static void DecodeFrame(byte[] data, int pos, int frame, EngineSynth st, short[] sub, short[] output, string what)
        {
            uint h = (uint)(data[pos] << 24 | data[pos + 1] << 16 | data[pos + 2] << 8 | data[pos + 3]);
            Header hd = ParseHeader(h, frame, pos, what);
            // The engine stores the protection bit (+0x24c2, FUN_10452760) but does not skip the CRC: bit
            // allocation is read right after the header. Shipped data has no CRC frames — not ported (STOP).
            if (hd.Crc) throw new NotSupportedException($"{what}: frame {frame} with CRC — the engine does not skip CRC, not ported (STOP)");
            int nch = hd.Channels;
            short[] alloc = Tables[hd.Table];
            int sblimit = SbLimit[hd.Table];
            var br = new BitReader(data, pos + 4, pos + hd.Bytes);

            // Bit allocation (the engine has no joint-stereo bound: both channels up to sblimit).
            Span<int> ba = stackalloc int[2 * 32];
            Span<int> allocBase = stackalloc int[32];
            int at = 0;
            for (int sb = 0; sb < sblimit; sb++)
            {
                allocBase[sb] = at;
                int nbal = alloc[at * 2];
                for (int ch = 0; ch < nch; ch++) ba[ch * 32 + sb] = br.Read(nbal);
                at += 1 << nbal;
            }
            Span<int> scfsi = stackalloc int[2 * 32];
            for (int sb = 0; sb < sblimit; sb++)
                for (int ch = 0; ch < nch; ch++)
                    if (ba[ch * 32 + sb] != 0) scfsi[ch * 32 + sb] = br.Read(2);
            Span<int> scale = stackalloc int[2 * 32 * 3];
            for (int sb = 0; sb < sblimit; sb++)
                for (int ch = 0; ch < nch; ch++)
                {
                    int x = (ch * 32 + sb) * 3;
                    if (ba[ch * 32 + sb] == 0) continue;
                    switch (scfsi[ch * 32 + sb])
                    {
                        case 0: scale[x] = br.Read(6); scale[x + 1] = br.Read(6); scale[x + 2] = br.Read(6); break;
                        case 1: scale[x] = scale[x + 1] = br.Read(6); scale[x + 2] = br.Read(6); break;
                        case 2: scale[x] = scale[x + 1] = scale[x + 2] = br.Read(6); break;
                        default: scale[x] = br.Read(6); scale[x + 1] = scale[x + 2] = br.Read(6); break;
                    }
                }

            // FUN_10455780 over 12 granules: samples [granule][slice 0..2][channel][subband]; subbands
            // without bits and beyond sblimit are zeros (jump table 0x10456088).
            Array.Clear(sub, 0, sub.Length);
            for (int gr = 0; gr < 12; gr++)
            {
                int part = gr >> 2;
                for (int sb = 0; sb < sblimit; sb++)
                    for (int ch = 0; ch < nch; ch++)
                    {
                        int b = ba[ch * 32 + sb];
                        if (b == 0) continue;
                        int e = (allocBase[sb] + b) * 2;
                        int bits = alloc[e], d = alloc[e + 1];
                        int sf = scale[(ch * 32 + sb) * 3 + part];
                        int o = (gr * 3 * 2 + ch) * 32 + sb;          // slice s: + s·64
                        if (d < 0)
                        {
                            int q = QuantUngrouped[bits * 64 + sf];
                            for (int s = 0; s < 3; s++)
                                sub[o + s * 64] = (short)(((br.Read(bits) + d) * q) >> 12);
                        }
                        else
                        {
                            int code = br.Read(bits);
                            if (code >= d * d * d) continue;          // codes beyond L^3 are zeros in the group table
                            int[] qg = QuantGrouped[d];
                            for (int s = 0; s < 3; s++, code /= d)
                                sub[o + s * 64] = (short)(qg[(code % d) * 64 + sf] >> 12);
                        }
                    }
            }

            // FUN_104579f0 over 36 slices.
            int written = 0;
            for (int slot = 0; slot < 36; slot++)
            {
                SynthSlot(st, sub, slot * 64, nch, output, written);
                written += 32 * nch;
            }
        }

        // A 32-sample slice per channel (FUN_104579f0 + FUN_10459070).
        static void SynthSlot(EngineSynth st, short[] sub, int at, int nch, short[] output, int outAt)
        {
            int t = st.T = (st.T - 1) & 15;
            int p = t & 1, r = p ^ 1;
            short[] v = st.V;
            Span<short> uSum = stackalloc short[16], uDif = stackalloc short[16];
            for (int ch = 0; ch < nch; ch++)
            {
                int x = at + ch * 32, vc = ch * 2 * 18 * 16;
                for (int i = 0; i < 16; i++)
                {
                    uSum[i] = (short)(sub[x + i] + sub[x + 31 - i]);   // paddw/psubw: int16 with wraparound
                    uDif[i] = (short)(sub[x + i] - sub[x + 31 - i]);
                }
                // Matrix: 17 rows into half p (column (t+p)&15), 16 rows into half p^1 (column t+(p^1)).
                int colA = (t + p) & 15, colB = t + r;
                for (int row = 0; row <= 16; row++)
                    v[vc + (p * 18 + row) * 16 + colA] = Matrix(DctA, row, (row & 1) == 0 ? uSum : uDif);
                for (int row = 0; row < 16; row++)
                    v[vc + (r * 18 + row) * 16 + colB] = Matrix(DctB, row, (row & 1) == 0 ? uSum : uDif);
                // Window: half R = p^1.
                int vr = vc + r * 18 * 16;
                for (int j = 0; j < 32; j++)
                {
                    int row = j <= 16 ? j : 32 - j, w = j * 16, sum = 0;
                    int vrow = vr + row * 16;
                    if (j == 16)
                    {
                        for (int a = 1; a < 16; a += 2)
                            sum += ((Window[w + a] * v[vrow + ((t + r - a) & 15)]) >> 16) << 16;
                    }
                    else
                    {
                        for (int a = 0; a < 16; a++)
                            sum += Window[w + a] * v[vrow + ((t + r - a) & 15)];
                    }
                    sum >>= 13;
                    output[outAt + j * nch + ch] = (short)(sum > 32767 ? 32767 : sum < -32768 ? -32768 : sum);
                }
            }
        }

        static short Matrix(int[] m, int row, ReadOnlySpan<short> u)
        {
            int sum = 0, o = row * 16;
            for (int i = 0; i < 16; i++) sum += m[o + i] * u[i];
            return (short)(sum >> 14);
        }

        sealed class BitReader
        {
            readonly byte[] _d; int _bit; readonly int _endBit;
            public BitReader(byte[] d, int start, int end) { _d = d; _bit = start * 8; _endBit = end * 8; }
            public int Read(int n)
            {
                if (_bit + n > _endBit) throw new InvalidDataException("mp2: frame data shorter than its bit allocation");
                int v = 0;
                for (int i = 0; i < n; i++, _bit++)
                    v = (v << 1) | ((_d[_bit >> 3] >> (7 - (_bit & 7))) & 1);
                return v;
            }
        }

        // Derived tables are built in the static constructor: field initialisers run in text order, and
        // the standard tables are declared below. Checked against Engine.dll by a self-check
        // (module bytes from the install) and the stream oracle.
        static readonly int[] QuantUngrouped;       // [bits·64 + sf], bits 3..16 (DAT_10480fc8 rows 3..16)
        static readonly int[][] QuantGrouped;       // [L][digit·64 + sf], L = 3/5/9
        static readonly int[] DctA, DctB;           // 17×16 and 16×16 (0x1060f200)
        static readonly int[] Window;               // 32×16 by slice age
        static readonly short[][] Tables;
        static UEMpegAudio()
        {
            QuantUngrouped = new int[17 * 64];
            for (int bits = 3; bits <= 16; bits++)
                for (int sf = 0; sf < 64; sf++) QuantUngrouped[bits * 64 + sf] = Quant(2.0 / ((1 << bits) - 1), sf);
            QuantGrouped = new int[10][];
            foreach (int l in new[] { 3, 5, 9 })
            {
                QuantGrouped[l] = new int[l * 64];
                for (int dgt = 0; dgt < l; dgt++)
                    for (int sf = 0; sf < 64; sf++) QuantGrouped[l][dgt * 64 + sf] = Quant((double)(2 * dgt - (l - 1)) / l, sf);
            }
            DctA = new int[17 * 16];
            DctB = new int[16 * 16];
            for (int row = 0; row < 17; row++)
                for (int i = 0; i < 16; i++)
                {
                    DctA[row * 16 + i] = Cos16384(16 - row, i);
                    if (row < 16) DctB[row * 16 + i] = Cos16384(16 + row, i);
                }
            Window = new int[32 * 16];
            for (int j = 0; j < 32; j++)
                for (int a = 0; a < 16; a++)
                {
                    int k = (32 * a - j) & 511;
                    int d = k <= 256 ? WindowBase[k] : WindowBase[512 - k];
                    if (((k >> 6) & 1) != 0) d = -d;
                    if (j >= 1 && j <= 15 && (a & 1) == 0) d = -d;
                    Window[j * 16 + a] = d / 4;              // C: toward zero
                }
            Tables = new[] { Alloc0, Alloc1, Alloc2, Alloc3, Alloc4 };
        }

        // DAT_10480fc8: (int)(float)(F·2^(27 - sf/3)); sf = 63 — 0.
        internal static int Quant(double f, int sf) => sf == 63 ? 0 : (int)(float)(f * Math.Pow(2.0, 27 - sf / 3.0));

        // 0x1060f200: (int)(16384·cos(πK(2i+1)/64) + 0.5).
        internal static int Cos16384(int k, int i) => (int)(16384.0 * Math.Cos(Math.PI * k * (2 * i + 1) / 64) + 0.5);

        // Tables for comparison with the module (selftest mp2_engine_tables).
        internal static int QuantTable(int bitsOrLevels, int digit, int sf) =>
            digit < 0 ? QuantUngrouped[bitsOrLevels * 64 + sf] : QuantGrouped[bitsOrLevels][digit * 64 + sf];

        static readonly int[] WindowBase =
        {
            0, -1, -1, -1, -1, -1, -1, -2, -2, -2, -2, -3, -3, -4, -4, -5,
            -5, -6, -7, -7, -8, -9, -10, -11, -13, -14, -16, -17, -19, -21, -24, -26,
            -29, -31, -35, -38, -41, -45, -49, -53, -58, -63, -68, -73, -79, -85, -91, -97,
            -104, -111, -117, -125, -132, -139, -147, -154, -161, -169, -176, -183, -190, -196, -202, -208,
            -213, -218, -222, -225, -227, -228, -228, -227, -224, -221, -215, -208, -200, -189, -177, -163,
            -146, -127, -106, -83, -57, -29, 2, 36, 72, 111, 153, 197, 244, 294, 347, 401,
            459, 519, 581, 645, 711, 779, 848, 919, 991, 1064, 1137, 1210, 1283, 1356, 1428, 1498,
            1567, 1634, 1698, 1759, 1817, 1870, 1919, 1962, 2001, 2032, 2057, 2075, 2085, 2087, 2080, 2063,
            2037, 2000, 1952, 1893, 1822, 1739, 1644, 1535, 1414, 1280, 1131, 970, 794, 605, 402, 185,
            -45, -288, -545, -814, -1095, -1388, -1692, -2006, -2330, -2663, -3004, -3351, -3705, -4063, -4425, -4788,
            -5153, -5517, -5879, -6237, -6589, -6935, -7271, -7597, -7910, -8209, -8491, -8755, -8998, -9219, -9416, -9585,
            -9727, -9838, -9916, -9959, -9966, -9935, -9863, -9750, -9592, -9389, -9139, -8840, -8492, -8092, -7640, -7134,
            -6574, -5959, -5288, -4561, -3776, -2935, -2037, -1082, -70, 998, 2122, 3300, 4533, 5818, 7154, 8540,
            9975, 11455, 12980, 14548, 16155, 17799, 19478, 21189, 22929, 24694, 26482, 28289, 30112, 31947, 33791, 35640,
            37489, 39336, 41176, 43006, 44821, 46617, 48390, 50137, 51853, 53534, 55178, 56778, 58333, 59838, 61289, 62684,
            64019, 65290, 66494, 67629, 68692, 69679, 70590, 71420, 72169, 72835, 73415, 73908, 74313, 74630, 74856, 74992,
            75038,
        };

        static readonly short[] Alloc0 =
        {
            4, 0, 5, 3, 3, -3, 4, -7, 5, -15, 6, -31, 7, -63, 8, -127,
            9, -255, 10, -511, 11, -1023, 12, -2047, 13, -4095, 14, -8191, 15, -16383, 16, -32767,
            4, 0, 5, 3, 3, -3, 4, -7, 5, -15, 6, -31, 7, -63, 8, -127,
            9, -255, 10, -511, 11, -1023, 12, -2047, 13, -4095, 14, -8191, 15, -16383, 16, -32767,
            4, 0, 5, 3, 3, -3, 4, -7, 5, -15, 6, -31, 7, -63, 8, -127,
            9, -255, 10, -511, 11, -1023, 12, -2047, 13, -4095, 14, -8191, 15, -16383, 16, -32767,
            4, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 6, -31,
            7, -63, 8, -127, 9, -255, 10, -511, 11, -1023, 12, -2047, 13, -4095, 16, -32767,
            4, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 6, -31,
            7, -63, 8, -127, 9, -255, 10, -511, 11, -1023, 12, -2047, 13, -4095, 16, -32767,
            4, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 6, -31,
            7, -63, 8, -127, 9, -255, 10, -511, 11, -1023, 12, -2047, 13, -4095, 16, -32767,
            4, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 6, -31,
            7, -63, 8, -127, 9, -255, 10, -511, 11, -1023, 12, -2047, 13, -4095, 16, -32767,
            4, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 6, -31,
            7, -63, 8, -127, 9, -255, 10, -511, 11, -1023, 12, -2047, 13, -4095, 16, -32767,
            4, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 6, -31,
            7, -63, 8, -127, 9, -255, 10, -511, 11, -1023, 12, -2047, 13, -4095, 16, -32767,
            4, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 6, -31,
            7, -63, 8, -127, 9, -255, 10, -511, 11, -1023, 12, -2047, 13, -4095, 16, -32767,
            4, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 6, -31,
            7, -63, 8, -127, 9, -255, 10, -511, 11, -1023, 12, -2047, 13, -4095, 16, -32767,
            3, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 16, -32767,
            3, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 16, -32767,
            3, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 16, -32767,
            3, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 16, -32767,
            3, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 16, -32767,
            3, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 16, -32767,
            3, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 16, -32767,
            3, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 16, -32767,
            3, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 16, -32767,
            3, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 16, -32767,
            3, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 16, -32767,
            3, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 16, -32767,
            2, 0, 5, 3, 7, 5, 16, -32767, 2, 0, 5, 3, 7, 5, 16, -32767,
            2, 0, 5, 3, 7, 5, 16, -32767, 2, 0, 5, 3, 7, 5, 16, -32767,
        };

        static readonly short[] Alloc1 =
        {
            4, 0, 5, 3, 3, -3, 4, -7, 5, -15, 6, -31, 7, -63, 8, -127,
            9, -255, 10, -511, 11, -1023, 12, -2047, 13, -4095, 14, -8191, 15, -16383, 16, -32767,
            4, 0, 5, 3, 3, -3, 4, -7, 5, -15, 6, -31, 7, -63, 8, -127,
            9, -255, 10, -511, 11, -1023, 12, -2047, 13, -4095, 14, -8191, 15, -16383, 16, -32767,
            4, 0, 5, 3, 3, -3, 4, -7, 5, -15, 6, -31, 7, -63, 8, -127,
            9, -255, 10, -511, 11, -1023, 12, -2047, 13, -4095, 14, -8191, 15, -16383, 16, -32767,
            4, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 6, -31,
            7, -63, 8, -127, 9, -255, 10, -511, 11, -1023, 12, -2047, 13, -4095, 16, -32767,
            4, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 6, -31,
            7, -63, 8, -127, 9, -255, 10, -511, 11, -1023, 12, -2047, 13, -4095, 16, -32767,
            4, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 6, -31,
            7, -63, 8, -127, 9, -255, 10, -511, 11, -1023, 12, -2047, 13, -4095, 16, -32767,
            4, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 6, -31,
            7, -63, 8, -127, 9, -255, 10, -511, 11, -1023, 12, -2047, 13, -4095, 16, -32767,
            4, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 6, -31,
            7, -63, 8, -127, 9, -255, 10, -511, 11, -1023, 12, -2047, 13, -4095, 16, -32767,
            4, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 6, -31,
            7, -63, 8, -127, 9, -255, 10, -511, 11, -1023, 12, -2047, 13, -4095, 16, -32767,
            4, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 6, -31,
            7, -63, 8, -127, 9, -255, 10, -511, 11, -1023, 12, -2047, 13, -4095, 16, -32767,
            4, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 6, -31,
            7, -63, 8, -127, 9, -255, 10, -511, 11, -1023, 12, -2047, 13, -4095, 16, -32767,
            3, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 16, -32767,
            3, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 16, -32767,
            3, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 16, -32767,
            3, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 16, -32767,
            3, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 16, -32767,
            3, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 16, -32767,
            3, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 16, -32767,
            3, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 16, -32767,
            3, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 16, -32767,
            3, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 16, -32767,
            3, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 16, -32767,
            3, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 16, -32767,
            2, 0, 5, 3, 7, 5, 16, -32767, 2, 0, 5, 3, 7, 5, 16, -32767,
            2, 0, 5, 3, 7, 5, 16, -32767, 2, 0, 5, 3, 7, 5, 16, -32767,
            2, 0, 5, 3, 7, 5, 16, -32767, 2, 0, 5, 3, 7, 5, 16, -32767,
            2, 0, 5, 3, 7, 5, 16, -32767,
        };

        static readonly short[] Alloc2 =
        {
            4, 0, 5, 3, 7, 5, 10, 9, 4, -7, 5, -15, 6, -31, 7, -63,
            8, -127, 9, -255, 10, -511, 11, -1023, 12, -2047, 13, -4095, 14, -8191, 15, -16383,
            4, 0, 5, 3, 7, 5, 10, 9, 4, -7, 5, -15, 6, -31, 7, -63,
            8, -127, 9, -255, 10, -511, 11, -1023, 12, -2047, 13, -4095, 14, -8191, 15, -16383,
            3, 0, 5, 3, 7, 5, 10, 9, 4, -7, 5, -15, 6, -31, 7, -63,
            3, 0, 5, 3, 7, 5, 10, 9, 4, -7, 5, -15, 6, -31, 7, -63,
            3, 0, 5, 3, 7, 5, 10, 9, 4, -7, 5, -15, 6, -31, 7, -63,
            3, 0, 5, 3, 7, 5, 10, 9, 4, -7, 5, -15, 6, -31, 7, -63,
            3, 0, 5, 3, 7, 5, 10, 9, 4, -7, 5, -15, 6, -31, 7, -63,
            3, 0, 5, 3, 7, 5, 10, 9, 4, -7, 5, -15, 6, -31, 7, -63,
        };

        static readonly short[] Alloc3 =
        {
            4, 0, 5, 3, 7, 5, 10, 9, 4, -7, 5, -15, 6, -31, 7, -63,
            8, -127, 9, -255, 10, -511, 11, -1023, 12, -2047, 13, -4095, 14, -8191, 15, -16383,
            4, 0, 5, 3, 7, 5, 10, 9, 4, -7, 5, -15, 6, -31, 7, -63,
            8, -127, 9, -255, 10, -511, 11, -1023, 12, -2047, 13, -4095, 14, -8191, 15, -16383,
            3, 0, 5, 3, 7, 5, 10, 9, 4, -7, 5, -15, 6, -31, 7, -63,
            3, 0, 5, 3, 7, 5, 10, 9, 4, -7, 5, -15, 6, -31, 7, -63,
            3, 0, 5, 3, 7, 5, 10, 9, 4, -7, 5, -15, 6, -31, 7, -63,
            3, 0, 5, 3, 7, 5, 10, 9, 4, -7, 5, -15, 6, -31, 7, -63,
            3, 0, 5, 3, 7, 5, 10, 9, 4, -7, 5, -15, 6, -31, 7, -63,
            3, 0, 5, 3, 7, 5, 10, 9, 4, -7, 5, -15, 6, -31, 7, -63,
            3, 0, 5, 3, 7, 5, 10, 9, 4, -7, 5, -15, 6, -31, 7, -63,
            3, 0, 5, 3, 7, 5, 10, 9, 4, -7, 5, -15, 6, -31, 7, -63,
            3, 0, 5, 3, 7, 5, 10, 9, 4, -7, 5, -15, 6, -31, 7, -63,
            3, 0, 5, 3, 7, 5, 10, 9, 4, -7, 5, -15, 6, -31, 7, -63,
        };

        static readonly short[] Alloc4 =
        {
            4, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 6, -31,
            7, -63, 8, -127, 9, -255, 10, -511, 11, -1023, 12, -2047, 13, -4095, 14, -8191,
            4, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 6, -31,
            7, -63, 8, -127, 9, -255, 10, -511, 11, -1023, 12, -2047, 13, -4095, 14, -8191,
            4, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 6, -31,
            7, -63, 8, -127, 9, -255, 10, -511, 11, -1023, 12, -2047, 13, -4095, 14, -8191,
            4, 0, 5, 3, 7, 5, 3, -3, 10, 9, 4, -7, 5, -15, 6, -31,
            7, -63, 8, -127, 9, -255, 10, -511, 11, -1023, 12, -2047, 13, -4095, 14, -8191,
            3, 0, 5, 3, 7, 5, 10, 9, 4, -7, 5, -15, 6, -31, 7, -63,
            3, 0, 5, 3, 7, 5, 10, 9, 4, -7, 5, -15, 6, -31, 7, -63,
            3, 0, 5, 3, 7, 5, 10, 9, 4, -7, 5, -15, 6, -31, 7, -63,
            3, 0, 5, 3, 7, 5, 10, 9, 4, -7, 5, -15, 6, -31, 7, -63,
            3, 0, 5, 3, 7, 5, 10, 9, 4, -7, 5, -15, 6, -31, 7, -63,
            3, 0, 5, 3, 7, 5, 10, 9, 4, -7, 5, -15, 6, -31, 7, -63,
            3, 0, 5, 3, 7, 5, 10, 9, 4, -7, 5, -15, 6, -31, 7, -63,
            2, 0, 5, 3, 7, 5, 10, 9, 2, 0, 5, 3, 7, 5, 10, 9,
            2, 0, 5, 3, 7, 5, 10, 9, 2, 0, 5, 3, 7, 5, 10, 9,
            2, 0, 5, 3, 7, 5, 10, 9, 2, 0, 5, 3, 7, 5, 10, 9,
            2, 0, 5, 3, 7, 5, 10, 9, 2, 0, 5, 3, 7, 5, 10, 9,
            2, 0, 5, 3, 7, 5, 10, 9, 2, 0, 5, 3, 7, 5, 10, 9,
            2, 0, 5, 3, 7, 5, 10, 9, 2, 0, 5, 3, 7, 5, 10, 9,
            2, 0, 5, 3, 7, 5, 10, 9, 2, 0, 5, 3, 7, 5, 10, 9,
            2, 0, 5, 3, 7, 5, 10, 9, 2, 0, 5, 3, 7, 5, 10, 9,
            2, 0, 5, 3, 7, 5, 10, 9, 2, 0, 5, 3, 7, 5, 10, 9,
            2, 0, 5, 3, 7, 5, 10, 9,
        };

        static readonly int[] SbLimit =
        {
            27, 30, 8, 12, 30,
        };

        static readonly int[] Translate =
        {
            0, 2, 2, 2, 2, 2, 2, 0, 0, 0, 1, 1, 1, 1, 1, 0,
            0, 2, 2, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0,
            0, 2, 2, 2, 2, 2, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 2, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 3, 3, 3, 3, 3, 3, 0, 0, 0, 1, 1, 1, 1, 1, 0,
            0, 3, 3, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0,
        };

        static readonly int[] Layer2Bitrate =
        {
            0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 0,
            0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0,
        };

    }
}
