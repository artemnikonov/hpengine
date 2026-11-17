// HP2 XA SOUND STREAM — the PCM ALAudio hands OpenAL for a streamed (USound flags & 4) non-OGG sound.
//
// UALAudioSubsystem::RegisterSound [HP2 ALAudio 0x10003400] opens such a sound with
// FFileStream::CreateStream(TLazyArray*, NumSamples (+0x6c), 0x8000, buffers, ..., type 3, or 4 when the sound's
// flags carry 2 (loop)); FFileStream::Read [HP2 Core body 0x10178390] fills each 0x8000-byte OpenAL buffer from the
// Core XA decoder below. Shipped data (tools/hp2/xa_decode_judge.py census): every streamed sound is this format,
// mono 16-bit, payload = ceil(NumSamples / 28) blocks of 15 bytes.
//
// The decoder object (constructor not exported: FUN_1017acd0 [HP2 Core 0x1017acd0], called from
// ?Create@FFileStream@@QAEHHPAV?$TLazyArray@E@@H@Z): +0 samples left in the tail buffer, +4 samples remaining in the
// lap, +0xc tail-buffer read pointer, +0x14 tail buffer (28 shorts), +0x4c block-decoder count, +0x50/+0x52 the two
// predictor samples (most recent first), +0x54 source pointer, +0x58 destination pointer.
using System;
using System.IO;

namespace HP1.UEScript
{
    public sealed class UEXaStream
    {
        // Filter pairs at Core.dll .data 0x101d44d8 [HP2], 16 entries indexed by the header byte's high nibble
        // (movsx [4*edx + 0x101d44d8] / [4*edx + 0x101d44da]). Entries 4..15 are the bytes that follow the four
        // XA filters in the image; the decoder indexes them for a high nibble > 3, so they are kept as the image has them.
        static readonly short[] Coef =
        {
            0, 0, 240, 0, 460, -208, 392, -220, 0, 0, 0, 0, -31696, 4120, 0, 0,
            16174, 22081, 31092, 25968, 26975, 26222, 16495, 64, 0, 0, 0, 0, 0, 0, 0, 0,
        };

        readonly byte[] _data;
        readonly int _total;
        readonly string _what;
        public bool Looping { get; }
        // FStream +0x24: set when a non-looping lap ends (the rest of that buffer is zero).
        public bool Ended { get; private set; }

        int _left;          // +0x00
        int _remaining;     // +0x04
        int _leftPos;       // +0x0c (index into _tail)
        readonly short[] _tail = new short[28];   // +0x14
        short _s1, _s2;     // +0x50, +0x52
        int _src;           // +0x54 (index into _data)

        // Format the stream is played in (RegisterSound: rate +0x78; +0x74 == 1 mono, else stereo). Set by the catalog.
        public int SampleRate { get; init; }
        public int Channels { get; init; } = 1;

        public UEXaStream(byte[] data, int numSamples, bool looping, string what)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            _total = numSamples;
            Looping = looping;
            _what = what;
            // FUN_1017acd0 zeroes +0/+4/+8 and the predictor (FUN_1017ae90 with 0); Create then calls Reset.
            if (!Reset())
                throw new NotSupportedException($"{what}: XA stream with no data (the decoder refuses Reset; a looping " +
                    "stream would spin in FFileStream::Read)");
        }

        // Original: not exported: FUN_1017acf0 [HP2 Core 0x1017acf0] (callee of ?Create@FFileStream@@QAEHHPAV?$TLazyArray@E@@H@Z and of FFileStream::Read body 0x10178390)
        // Restarts the lap only when the previous one is used up; the predictor and the tail buffer are not touched.
        bool Reset()
        {
            if (_data.Length == 0 || _total <= 0) return false;   // data pointer 0 -> -1 (a zero total never decodes)
            if (_remaining != 0) return false;
            _src = 0;
            _remaining = _total;
            return true;
        }

        // Original: not exported: FUN_1017ab80 [HP2 Core 0x1017ab80] (callee of FUN_1017ad30)
        // Whole 28-sample blocks while the count is positive: header byte (high nibble filter, low nibble shift - 8),
        // then 14 bytes, high nibble first.
        void Blocks(short[] dst, ref int pos, int count)
        {
            while (count > 0)
            {
                count -= 28;
                if (_src + 15 > _data.Length)
                    throw new InvalidDataException($"{_what}: XA block past the end of the data (the original reads foreign memory)");
                int h = _data[_src++];
                int shift = (h & 15) + 8;
                int c0 = Coef[(h >> 4) * 2], c1 = Coef[(h >> 4) * 2 + 1];
                for (int k = 0; k < 14; k++)
                {
                    int b = _data[_src];
                    int v = (((b >> 4) << 28) >> shift) + _s2 * c1 + _s1 * c0 >> 8;
                    short a = (short)(v < -0x8000 ? -0x8000 : v > 0x7fff ? 0x7fff : v);
                    dst[pos] = a;
                    _s2 = a;
                    v = ((b << 28) >> shift) + a * c0 + _s1 * c1 >> 8;
                    short z = (short)(v < -0x8000 ? -0x8000 : v > 0x7fff ? 0x7fff : v);
                    dst[pos + 1] = z;
                    _s1 = z;
                    _src++;
                    pos += 2;
                }
            }
        }

        // Original: not exported: FUN_1017ad30 [HP2 Core 0x1017ad30] (callee of FFileStream::Read body 0x10178390)
        // Up to n samples of the lap into dst[pos..]; returns how many were written.
        int Decode(short[] dst, int pos, int n)
        {
            int written = 0;
            if (_remaining == 0) return 0;
            if (_remaining <= n) n = _remaining;
            if (_left != 0)
            {
                written = _left < n ? _left : n;
                Array.Copy(_tail, _leftPos, dst, pos, written);
                pos += written; _leftPos += written;
                _remaining -= written; _left -= written; n -= written;
            }
            int full = n / 28 * 28, part = n % 28;
            if (full > 0)
            {
                Blocks(dst, ref pos, full);
                written += full;
                _remaining -= full;
            }
            if (part > 0)
            {
                int tp = 0;
                Blocks(_tail, ref tp, part);
                _remaining -= part;
                _leftPos = part;
                _left = 28 - part;
                Array.Copy(_tail, 0, dst, pos, part);
                written += part;
            }
            if (_remaining < 1) _left = 0;
            return written;
        }

        // The frontend's stream view (hpengine ISoundStream): ReadLap gives up to count samples of the lap, 0 at its end;
        // Restart begins the next lap with the predictor kept — exactly the pieces FFileStream::Read type 4 joins (Read
        // below is built from the same two, so tools/hp2/xa_decode_judge.py's looping stream judges this view too).
        public int ReadLap(short[] dst, int offset, int count) => Decode(dst, offset, count);

        public void Restart()
        {
            if (!Reset()) throw new InvalidOperationException($"{_what}: XA restart before the lap ended (the original never restarts mid-lap)");
        }

        // Original: Core.dll ?Read@FFileStream@@QAEHHH@Z [HP2 0x10178390] (thunk 0x10101cad), stream types 3 and 4
        // Fills `samples` samples of dst (bytes / 2 in the original): a non-looping stream (3) zero-fills after the lap
        // and sets Ended; a looping one (4) restarts the lap (Reset) and continues with the predictor it has.
        public void Read(short[] dst, int samples)
        {
            if (samples < 1) return;
            int i = 0;
            while (true)
            {
                int got = Decode(dst, i, samples - i);
                if (got < samples - i)
                {
                    if (!Looping)
                    {
                        Array.Clear(dst, i + got, samples - i - got);
                        Ended = true;
                        return;
                    }
                    Reset();
                }
                i += got;
                if (samples <= i) return;
            }
        }
    }
}
