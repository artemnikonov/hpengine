// MPEG-1/2 LAYER II DECODER — PORT OF THE ENGINE.DLL DECODER (CMpegBase, integer MMX path).
// Layer I (FUN_10453630, e.g. the Dutch retail AllDialog.dut_uax) shares the header, the MMX dequantisation table and
// the synthesis below; its frame body is UEMpegAudio.DecodeLayer1.
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

    // Sound PCM of a non-mp2 sound: the WAV data chunk as is (unsigned 8-bit becomes b - 0x80 in Galaxy's PCM reader
    // FUN_10617130, mixed as b << 8 — the same as (b-128)<<8 here, GalaxyMixKernel). mp2 does not come here: its
    // PCM is an engine stream loop (UEMp2SoundStream.ShippedPcm, needs iEndPosn from the package tail).
    public static class UESoundPcm
    {
        public static UEPcm Decode(byte[] payload, string format, string what)
        {
            // Galaxy does not read FileType for a non-mp2 sound: FUN_10616790 sniffs the first 0x50 bytes — "AS  ", ".snd",
            // "WAVE" at +8, "SCRS" — and hands anything else to its MPEG decoder FUN_1061e4e0. Only the WAVE loader is ported.
            if (payload.Length >= 12 && !(payload[8] == 'W' && payload[9] == 'A' && payload[10] == 'V' && payload[11] == 'E'))
                throw new NotSupportedException($"{what}: sound format {format} without WAVE at +8 — Galaxy's other loaders (FUN_10616790) are not ported (STOP)");
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
                    // Original: Galaxy.dll not exported: FUN_10617270 (the WAVE loader of FUN_10616790, called by ?RegisterSound@UGalaxyAudioSubsystem@@UAEXPAVUSound@@@Z) [HP1 0x10617270]
                    // The loader knows tag 1 (PCM), 0x11 (IMA ADPCM: its own decoder FUN_10617b20) and 0x55 (MPEG: Galaxy's
                    // decoder FUN_1061e4e0); any other tag returns no sample and RegisterSound logs "Invalid sound format in %s"
                    // to GError (Galaxy.dll.c:105-109): a fatal error.
                    int tag = BitConverter.ToUInt16(payload, pos + 8);
                    if (tag == 0x11 || tag == 0x55)
                        throw new NotSupportedException($"{what}: WAV tag 0x{tag:x} — Galaxy's {(tag == 0x11 ? "IMA ADPCM" : "MPEG")} decoder is not ported (STOP)");
                    if (tag != 1)
                        throw new InvalidDataException($"Invalid sound format in {what} (WAV tag 0x{tag:x}: Galaxy's loader returns no sample, GError — fatal)");
                    channels = BitConverter.ToUInt16(payload, pos + 10);
                    rate = BitConverter.ToInt32(payload, pos + 12);
                    bits = BitConverter.ToUInt16(payload, pos + 22);
                }
                else if (payload[pos] == 'd' && payload[pos + 1] == 'a' && payload[pos + 2] == 't' && payload[pos + 3] == 'a')
                { dataAt = pos + 8; dataLen = size; }
                pos += 8 + size + (size & 1);
            }
            if (dataAt < 0 || channels <= 0 || rate <= 0) throw new InvalidDataException($"{what}: WAV without fmt/data");
            if (channels > 2) throw new NotSupportedException($"{what}: WAV with {channels} channels — Galaxy reads it as mono, not ported (STOP)");
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
            // Galaxy keys only on 16 bits (flag 4) and 2 channels (flag 0x40): any other size reads as 8-bit bytes and any
            // other channel count as mono (FUN_10617270 :15838-15841) with frames = data / block align — not ported (STOP).
            else throw new NotSupportedException($"{what}: WAV {bits} bits — Galaxy reads it as 8-bit, not ported (STOP)");
            return new UEPcm { SampleRate = rate, Channels = channels, Samples = samples, SampleBytes = bits / 8 };
        }
    }

    // mp2 stream — the CMpegBase decoder: frames are indexed up front (offsets, format), PCM is produced
    // per frame (a frame is decoded when its sample is first needed — as FUN_104531e0). Synthesis state
    // (V and t) lives as long as the stream; Rewind moves the bitstream to the start WITHOUT resetting
    // synthesis (FUN_10453150). Full decoding is the same read (UEMpegAudio.Decode).
    public sealed class UEMpegStream
    {
        // Samples per channel of one decoded frame: Open FUN_10452bc0 sets the output frame size +0x248c to
        // channels·0x300 bytes for layer I and channels·0x900 otherwise.
        public static int FrameSamplesOf(int layer) => layer == 1 ? 384 : 1152;
        readonly byte[] _data;
        readonly string _what;
        readonly int[] _offsets;
        readonly UEMpegAudio.EngineSynth _synth = new UEMpegAudio.EngineSynth();
        readonly short[] _sub = new short[3 * 2 * 32 * 12];
        readonly int _layer;
        readonly short[] _buf;
        int _bufPos, _bufLen, _next;

        public int SampleRate { get; }
        public int Channels { get; }
        public int FrameSamples { get; }
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
                // Original: Engine.dll not exported: CMpegFilePlayer Open, frame decode, frame read [HP1 0x10453030, 0x10452C80, 0x104531E0]
                // The stream's format is the first frame's: Open fixes the output frame size (+0x249c) and the rate Galaxy
                // plays at; every frame is then decoded by its OWN header (FUN_10452c80 -> layer I FUN_10453630 / layer II FUN_10456190). A later
                // frame with another rate or MPEG version but the same channel count decodes exactly as it does alone
                // (unicorn on the real bodies: Common_Room 8 frames + Green_Cauldron_mx 8 frames, 22050 MPEG-2 -> 44100
                // MPEG-1: frames 1-7 after the change byte-equal to Green_Cauldron decoded alone) and plays at the first rate.
                if (offsets.Count == 0) { SampleRate = hd.Rate; Channels = hd.Channels; _layer = hd.Layer; FrameSamples = FrameSamplesOf(hd.Layer); }
                else if (hd.Layer != _layer)
                    // The output frame size (+0x249c) stays the first frame's while the decoder writes the new layer's
                    // 384 or 1152 samples: what FUN_104531e0 then delivers is not read — STOP.
                    throw new NotSupportedException($"{what}: frame {offsets.Count} changes the layer ({hd.Layer} versus {_layer}) — not ported (STOP)");
                else if (hd.Channels != Channels)
                    // A mono frame in a stereo stream yields a full stereo-size frame (4608 bytes) that is neither its mono
                    // decode nor stale data (same unicorn run with Invisible's mono frames): the layer II rule for a mode
                    // change inside FUN_10456190 is not read — STOP.
                    throw new NotSupportedException($"{what}: frame {offsets.Count} changes the channel count ({hd.Channels} versus {Channels}) — the decoder's mode change is not ported (STOP)");
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
    //     data (for a loop — assert "!(iEndPosn==0 && IsLooping)", 0x1037e5dc);
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
        readonly long _lapBytes;     // bytes per lap (iEndPosn, or the decoder output when it ends first)
        readonly short[] _scratch;
        readonly short[] _one = new short[1];
        long _pos;                   // bytes delivered in this lap (mPosn +0x28)
        int _decHi = -1;             // high byte of a decoder sample whose low byte ended the previous read
        int _outLo = -1;             // low byte of an output sample whose high byte comes from the next lap

        public int SampleRate => _dec.SampleRate;
        public int Channels => _dec.Channels;
        // Whole int16 samples of one lap (interleaved channels); an odd byte lap's half sample is not counted.
        public long LoopSamples => _lapBytes / 2;
        // The stream finishes after lap 1 whatever the loop flag (FillBuffer's short read, 0x1037e661: +8 = 1, then
        // every call returns 0 at 0x1037e594): iEndPosn past the decoder output, or iEndPosn == 0 without a loop.
        public bool Finishes { get; }

        // FUN_10452a80: skip bytes by layer.
        public static int SkipBytes(int layer) => layer == 1 ? 0x442 : layer == 2 ? 0x3c2 : 0;

        // Original: Engine.dll not exported: MP2SoundStream::FillBuffer FUN_1037e550 [HP1 0x1037E550]
        public UEMp2SoundStream(byte[] data, int endBytes, string what)
        {
            _dec = new UEMpegStream(data, what);   // accepts layers I and II
            int layer = 4 - ((data[1] >> 1) & 3);
            _skip = SkipBytes(layer) / 2;
            if (endBytes < 0)
                throw new InvalidDataException($"{what}: iEndPosn {endBytes} < 0");
            long avail = 2 * (_dec.Frames * _dec.Channels - _skip);
            // iEndPosn == 0 (0x1037e641-0x1037e64c): every read takes the whole request, so the stream delivers up to the
            // end of decoder data and finishes there (the same short read as below). With the loop flag the engine asserts
            // "!(iEndPosn==0 && IsLooping)" (0x1037e5dc-0x1037e5f8, appFailAssert: fatal) — ShippedPcm refuses that loudly.
            // Decoder shorter than the lap (RU AllDialog: 1366 voice lines): the frame past the data fails in the
            // header read (FUN_104529c0: FSoundData::Buffer 0x1037ef20 clamps to Data.Num, returns != 4 ->
            // "waiting for cd access"), FUN_104531e0 does not count it ("mpeg fail 2"), so FillBuffer's read comes back
            // short (0x1037e65d) and the stream is finished (+8 = 1, 0x1037e664) after exactly the decoded bytes, looping
            // or not: it never restarts.
            Finishes = endBytes == 0 || endBytes > avail;
            _lapBytes = Finishes ? avail : endBytes;
            _scratch = new short[_skip];
            Skip();
        }

        void Skip()
        {
            if (_dec.Read(_scratch, 0, _skip) != _skip) throw new InvalidOperationException("mp2: skip past the end of data");
            _pos = 0;
        }

        // Decoder output as bytes (FUN_10452630 copies bytes of the int16 frame buffer): an odd count leaves the high
        // byte of the last sample for the next call; Rewind drops it with the frame buffer position (FUN_10453150).
        int DecoderBytes(byte[] dst, int off, int n)
        {
            int done = 0;
            if (n > 0 && _decHi >= 0) { dst[off + done++] = (byte)_decHi; _decHi = -1; }
            int whole = (n - done) / 2;
            if (whole > 0)
            {
                var tmp = new short[whole];
                int got = _dec.Read(tmp, 0, whole);
                Buffer.BlockCopy(tmp, 0, dst, off + done, 2 * got);
                done += 2 * got;
                if (got < whole) return done;
            }
            if (done < n && _dec.Read(_one, 0, 1) == 1) { dst[off + done++] = (byte)_one[0]; _decHi = (byte)(_one[0] >> 8); }
            return done;
        }

        // One lap as the bytes FillBuffer delivers (0x1037e641-0x1037e65f / 0x1037e714-0x1037e729).
        byte[] LapBytes(string what)
        {
            var b = new byte[_lapBytes - _pos];
            int got = DecoderBytes(b, 0, b.Length);
            _pos += got;
            if (got != b.Length) throw new InvalidOperationException($"{what}: mp2 lap not read completely ({got}/{b.Length})");
            return b;
        }

        // New lap (FUN_1037e550 0x1037e6c0): bitstream restart without synthesis reset (FUN_10452650 -> FUN_10453150),
        // then the skip. A finished stream never comes here in the engine (FillBuffer returns 0 at 0x1037e594).
        public void Restart()
        {
            if (Finishes) throw new InvalidOperationException("mp2 Restart of a finished stream (FillBuffer returns 0, no lap follows)");
            _dec.Rewind();
            _decHi = -1;
            Skip();
        }

        // Up to count int16 samples of the byte stream; 0 = end of lap (the caller loops via Restart). An odd byte lap ends
        // with the low byte of a sample whose high byte is the first byte of the next lap (the ring takes bytes).
        public int Read(short[] dst, int offset, int count)
        {
            int want = (int)Math.Min(2L * count - (_outLo >= 0 ? 1 : 0), _lapBytes - _pos);
            if (want <= 0) return 0;
            var b = new byte[want + 1];
            int k = 0;
            if (_outLo >= 0) { b[k++] = (byte)_outLo; _outLo = -1; }
            int got = DecoderBytes(b, k, want);
            _pos += got; k += got;
            int n = k / 2;
            Buffer.BlockCopy(b, 0, dst, 2 * offset, 2 * n);
            if ((k & 1) != 0) _outLo = b[k - 1];
            return n;
        }

        // Sound PCM for a voice that repeats data itself (HPEngine mixer). The FillBuffer byte stream is lap 1, then lap 2
        // repeated (loops k >= 3 equal loop 2: the loop ends on the same last frame); a lap carries the synthesis tail of
        // the previous one (stereo: up to 1414 units at the AMB_pre_dungeon seam). A lap of B bytes keeps the frame
        // layout only every m laps, m the smallest with m·B a multiple of the frame size 2·ch (an odd byte lap shifts the
        // byte phase, an odd stereo sample lap the channel parity), so the start is lap 1 + (m-1)·lap 2 and the repeat
        // m·lap 2; if it equals the start (mono), one lap remains. Without a loop, or when the stream finishes (the voice then
        // ends: Core plays it as not looping, SoundInfo.Finishes), lap 1 in
        // whole frames: the Galaxy voice end is the ring byte position >> 1 (0x1060bf1d-0x1060bf24), so the half sample is
        // not played (ASSUMPTION carried over: how the ring consumes a stereo stream's last unpaired sample is not read).
        public static UEPcm ShippedPcm(byte[] data, int endBytes, bool looping, string what)
        {
            if (endBytes == 0 && looping)
                throw new NotSupportedException($"{what}: iEndPosn == 0 with the loop flag — the engine asserts \"!(iEndPosn==0 && IsLooping)\" (0x1037e5dc, fatal)");
            var s = new UEMp2SoundStream(data, endBytes, what);
            int ch = s.Channels, frame = 2 * ch;
            byte[] b1 = s.LapBytes(what);
            var pcm = new UEPcm { SampleRate = s.SampleRate, Channels = ch };
            if (!looping || s.Finishes)
            {
                pcm.Samples = Samples(b1, b1.Length / frame * frame);
                return pcm;
            }
            s.Restart();
            byte[] b2 = s.LapBytes(what);
            int m = 1;
            while (m * (long)b2.Length % frame != 0) m++;
            byte[] head = Join(b1, Repeat(b2, m - 1)), body = Repeat(b2, m);
            if (head.AsSpan().SequenceEqual(body)) { pcm.Samples = Samples(body, body.Length); return pcm; }
            pcm.Samples = Samples(Join(head, body), head.Length + body.Length);
            pcm.LoopStart = head.Length / frame;
            return pcm;
        }

        static short[] Samples(byte[] b, int bytes)
        {
            var r = new short[bytes / 2];
            Buffer.BlockCopy(b, 0, r, 0, r.Length * 2);
            return r;
        }

        static byte[] Repeat(byte[] a, int n)
        {
            var r = new byte[a.Length * n];
            for (int i = 0; i < n; i++) a.CopyTo(r, i * a.Length);
            return r;
        }

        static byte[] Join(byte[] a, byte[] b)
        {
            var r = new byte[a.Length + b.Length];
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
            public int Layer, Rate, Channels, Bytes, Mode, ModeExt, Table;
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
            // CMpegBase::ProcessHeader (FUN_10452760): a free bitrate prints "bad bit rate" through the AUDIO ASSERT
            // FUN_104533c0, which ends in int3 (0x104528c9-0x104528f1, 0x104533ce): the game dies — loud here too.
            if (brIndex == 0)
                throw new InvalidDataException($"{what}: frame {frame} has a free bitrate — the engine's AUDIO ASSERT \"bad bit rate\" ends in int3 (0x104528f1), fatal (0x{h:x8})");
            // Not read (STOP): bit 20 clear (MPEG-2.5/reserved: ProcessHeader returns -1, 0x10452770, the caller's
            // handling is not read), layer III and the reserved layer (FUN_10452c80 calls no decoder and returns 0: what
            // the frame buffer then holds is not read), bitrate 15 (table 0x10482aa8 gives 0: a frame body of -4 bytes)
            // and sample-rate index 3.
            if (version == 1 || version == 0 || (layer != 1 && layer != 2) || brIndex == 15 || srIndex == 3)
                throw new NotSupportedException($"{what}: frame {frame} is not MPEG-1/2 Layer I or II with a listed bitrate and rate (0x{h:x8}) — not ported (STOP)");
            bool lsf = version != 3;
            int fs = srIndex == 0 ? 44100 : srIndex == 1 ? 48000 : 32000;
            if (version == 2) fs >>= 1; else if (version == 0) fs >>= 2;
            int mode = (int)(h >> 6) & 3;
            int nch = mode == 3 ? 1 : 2;               // FUN_10452760: (mode != 3) + 1 — all stereo modes alike
            // FUN_10452760: kbit/s = table 0x10482aa8 row (lsf·3 + layer); body bytes +0x2498 = layer I
            // ((kbit·12000)/fs + padding)·4 - 4 (a slot is 4 bytes), otherwise (kbit·144000)/fs + padding - 4.
            int pad = (int)(h >> 9) & 1;
            int bitrate = (layer == 1 ? Layer1Bitrate : Layer2Bitrate)[(lsf ? 16 : 0) + brIndex] * 1000;
            return new Header
            {
                Layer = layer,
                Rate = fs,
                Channels = nch,
                Mode = mode,
                ModeExt = (int)(h >> 4) & 3,
                Bytes = layer == 1 ? (12 * bitrate / fs + pad) * 4 : 144 * bitrate / fs + pad,
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
            // The engine stores the protection bit (+0x24c2, FUN_10452760) but does not skip the CRC: bit allocation is
            // read right after the header, so a CRC frame decodes its CRC word as allocation bits (mp2 engine oracle,
            // HP1_MP2_EDGE crc).
            int nch = hd.Channels;
            var br = new BitReader(data, pos + 4, pos + hd.Bytes);
            if (hd.Layer == 1) { DecodeLayer1(br, nch, st, sub, output); return; }
            short[] alloc = Tables[hd.Table];
            int sblimit = SbLimit[hd.Table];

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

        // Original: Engine.dll not exported: FUN_10453630 (layer I frame; dispatcher FUN_10452c80 calls it for layer byte +0x2490 == 1) [HP1 0x10453630]
        // Original: Engine.dll not exported: FUN_10453520 (layer I stereo subband dequantisation, called by FUN_10453630) [HP1 0x10453520]
        // Layer I frame, MMX branch (0x10453b37-0x10454f84; DAT_10601e9c = 1 as for layer II) -> 384 samples per channel.
        // Like layer II the CRC word is not skipped and there is no joint-stereo bound: a stereo frame reads 32 allocation
        // bytes (high nibble channel 0, low nibble channel 1), a mono frame 16 bytes (high nibble = even subband); then
        // 6-bit scale factors of the subbands with bits (subband order, channel 0 then 1); then 12 slices x 32 subbands x
        // channels. Allocation a != 0 reads a + 1 bits c (mask 0x10482bac) and gives the int16 (truncated)
        // ((c + offset[a]) * Q[a + 1][sf]) >> 12, offset 0x10482c48 = 1 - 2^a, Q the DAT_10480fc8 row a + 1 (the stereo
        // pair FUN_10453520 and the inlined mono path index the same row); a = 0 gives 0. Each slice then goes through the
        // same synthesis FUN_104579f0 as layer II. Allocation 15 (reserved in the standard) decodes as 16 bits.
        static void DecodeLayer1(BitReader br, int nch, EngineSynth st, short[] sub, short[] output)
        {
            Span<int> ba = stackalloc int[2 * 32];
            Span<int> scale = stackalloc int[2 * 32];
            for (int sb = 0; sb < 32; sb++)
                for (int ch = 0; ch < nch; ch++) ba[ch * 32 + sb] = br.Read(4);
            for (int sb = 0; sb < 32; sb++)
                for (int ch = 0; ch < nch; ch++)
                    if (ba[ch * 32 + sb] != 0) scale[ch * 32 + sb] = br.Read(6);
            for (int slice = 0; slice < 12; slice++)
                for (int sb = 0; sb < 32; sb++)
                    for (int ch = 0; ch < nch; ch++)
                    {
                        int i = ch * 32 + sb, a = ba[i];
                        sub[slice * 64 + i] = a == 0 ? (short)0
                            : (short)(((br.Read(a + 1) + 1 - (1 << a)) * QuantUngrouped[(a + 1) * 64 + scale[i]]) >> 12);
                    }
            for (int slice = 0; slice < 12; slice++)
                SynthSlot(st, sub, slice * 64, nch, output, slice * 32 * nch);
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
        static readonly int[] QuantUngrouped;       // [bits·64 + sf], bits 2..16 (DAT_10480fc8 rows 2..16; row 2 is the 3-level digit 2 = 2/3)
        static readonly int[][] QuantGrouped;       // [L][digit·64 + sf], L = 3/5/9
        static readonly int[] DctA, DctB;           // 17×16 and 16×16 (0x1060f200)
        static readonly int[] Window;               // 32×16 by slice age
        static readonly short[][] Tables;
        static UEMpegAudio()
        {
            QuantUngrouped = new int[17 * 64];
            for (int bits = 2; bits <= 16; bits++)
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

        // 0x10482aa8 rows 1 (MPEG-1) and 4 (LSF): layer I.
        static readonly int[] Layer1Bitrate =
        {
            0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448, 0,
            0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256, 0,
        };

        static readonly int[] Layer2Bitrate =
        {
            0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 0,
            0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0,
        };

    }
}
