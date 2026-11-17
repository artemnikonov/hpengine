// HP2 OGG MUSIC DECODER — the PCM that HP2's shipped Ogg Vorbis DLLs (System/ogg.dll, vorbis.dll — vendor
// "Xiphophorus libVorbis I 20011217" — and vorbisfile.dll) hand ALAudio for a streamed music USound (FileType "ogg").
//
// Ported from the disassembly of those DLLs (HP2 EN System; the same bytes in every HP2 release). The caller is HP2
// Core.dll: FFileStream::CreateStream opens the file with ov_open(FILE*, vf, NULL, 0); FFileStream::Read
// [HP2 Core 0x10178390, stream types 1/2] fills each 0x10000-byte OpenAL buffer with ov_read(vf, buf, n, bigendian 0,
// word 2, signed 1, &bitstream) and, for a looping stream (type 2, USound flags & 8 — every music USound,
// UALAudioSubsystem::PlayMusic flags 0xc), answers an ov_read 0 with ov_time_seek(vf, 0.0) and reads on.
//
// Exactness (DECISIONS.md, "HP2 OGG music"): discrete behaviour (stream length, packets and pages consumed, the lap
// end and the restart) and every 16-bit sample exact against the DLL at PC64. The float pipeline follows the
// DLL's x87 code: values the DLL keeps in x87 registers are C# doubles, values it stores are floats (the MDCT butterflies
// are a mechanical translation of the vorbis.dll instruction stream, tools/hp2/x87cs.py). Judge:
// tools/hp2/ogg_vorbis_judge.py (the shipped DLLs under unicorn, every shipped music file, lap + restart).
//
// Only what shipped music uses is ported: floor type 1, residue type 2, mapping type 0, window type 0, transform 0
// (census of all 141 Music/*.ogg: stereo 44100 Hz, blocksizes 256/2048, one link). Anything else is refused loudly.
using System;
using System.Collections.Generic;

namespace HP1.UEScript
{
    public sealed class UEOggVorbis
    {
        // ── Ogg layer (ogg.dll: ogg_sync_pageseek CRC check, ogg_stream_pagein / packetout) ──────────────────────────
        readonly struct Packet
        {
            public readonly int Start, Bytes; public readonly long GranulePos; public readonly bool Eos;
            public Packet(int start, int bytes, long gp, bool eos) { Start = start; Bytes = bytes; GranulePos = gp; Eos = eos; }
        }

        static readonly uint[] CrcTable = BuildCrc();
        static uint[] BuildCrc()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint r = i << 24;
                for (int k = 0; k < 8; k++) r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04c11db7 : r << 1;
                t[i] = r;
            }
            return t;
        }

        // Every packet of the (single) logical stream in order; the header packets are packets 0..2. A packet's granule
        // position is its page's when it is the last packet completed on that page, else -1 (ogg_stream_packetout); EOS
        // marks the last packet of the EOS page. The port refuses what would make the DLL skip or hole (bad CRC, a page
        // gap, a second serial number, a truncated last page): no shipped file has one (the judge's census).
        static (byte[] Data, List<Packet> Packets) ReadPackets(byte[] file, string what)
        {
            var data = new List<byte>(file.Length);
            var packets = new List<Packet>();
            int pos = 0, pending = -1; long expectPage = 0; uint serial = 0;
            while (pos < file.Length)
            {
                if (file.Length - pos < 27 || file[pos] != 'O' || file[pos + 1] != 'g' || file[pos + 2] != 'g' || file[pos + 3] != 'S')
                    throw new NotSupportedException($"{what}: bytes at {pos} are not an Ogg page (the DLL would resync: STOP)");
                int segs = file[pos + 26], hdr = 27 + segs, body = 0;
                for (int i = 0; i < segs; i++) body += file[pos + 27 + i];
                if (pos + hdr + body > file.Length) throw new NotSupportedException($"{what}: truncated last page (STOP)");
                uint want = BitConverter.ToUInt32(file, pos + 22), crc = 0;
                for (int i = 0; i < hdr + body; i++)
                    crc = (crc << 8) ^ CrcTable[((crc >> 24) & 0xff) ^ (i >= 22 && i < 26 ? 0 : file[pos + i])];
                if (crc != want) throw new NotSupportedException($"{what}: page at {pos} fails its CRC (the DLL skips it: STOP)");
                uint ser = BitConverter.ToUInt32(file, pos + 14);
                long pageNo = BitConverter.ToUInt32(file, pos + 18);
                if (pos == 0) serial = ser;
                else if (ser != serial) throw new NotSupportedException($"{what}: chained/multiplexed stream (STOP)");
                if (pageNo != expectPage) throw new NotSupportedException($"{what}: page gap {expectPage}->{pageNo} (a hole: STOP)");
                expectPage = pageNo + 1;
                byte flags = file[pos + 5];
                bool continued = (flags & 1) != 0;
                if (continued != (pending >= 0)) throw new NotSupportedException($"{what}: continuation flag mismatch at {pos} (STOP)");
                long gp = BitConverter.ToInt64(file, pos + 6);
                int b = pos + hdr, lastDone = -1;
                for (int i = 0; i < segs; i++)
                {
                    int len = file[pos + 27 + i];
                    if (pending < 0) pending = data.Count;
                    for (int k = 0; k < len; k++) data.Add(file[b + k]);
                    b += len;
                    if (len < 255)
                    {
                        packets.Add(new Packet(pending, data.Count - pending, -1, false));
                        pending = -1; lastDone = packets.Count - 1;
                    }
                }
                if (lastDone >= 0)
                {
                    var p = packets[lastDone];
                    packets[lastDone] = new Packet(p.Start, p.Bytes, gp, (flags & 4) != 0);
                }
                else if ((flags & 4) != 0) throw new NotSupportedException($"{what}: EOS page completes no packet (STOP)");
                pos += hdr + body;
            }
            if (pending >= 0) throw new NotSupportedException($"{what}: the stream ends inside a packet (STOP)");
            return (data.ToArray(), packets);
        }

        // oggpack_read [ogg.dll 0x100013b0]: LSb first; a read past the end answers -1 and the position still advances.
        struct Bits
        {
            readonly byte[] _d; readonly int _start; readonly long _end; long _pos;
            public Bits(byte[] d, int start, int bytes) { _d = d; _start = start; _end = (long)bytes * 8; _pos = 0; }
            public int Read(int n)
            {
                if (_pos + n > _end) { _pos += n; return -1; }
                long v = 0;
                for (int i = 0; i < n; i++, _pos++)
                    v |= (long)((_d[_start + (int)(_pos >> 3)] >> (int)(_pos & 7)) & 1) << i;
                return (int)v;   // a 32-bit read keeps its bit pattern (as the DLL's long)
            }
        }

        static int ILog(uint v) { int r = 0; while (v != 0) { r++; v >>= 1; } return r; }

        // ── Codebooks (vorbis.dll sharedbook/codebook: unpack, _make_words, _book_unquantize 0x1000fd40,
        //    decode_packed_entry_number 0x10003b90) ─────────────────────────────────────────────────────────────────
        sealed class Codebook
        {
            public int Dim, Entries;
            public int[] Tree;        // pairs: Tree[2k + bit] = child (> 0 node index, <= 0 leaf -entry), -int.MaxValue unset
            public float[] Values;    // Entries * Dim (maptype 1/2), else null

            // decode_packed_entry_number: bit by bit from the root (the DLL's lookahead table gives the same entry while
            // the packet has the bits; at its end it falls back to single bits — the same answers). -1 when the bits run out.
            public int Decode(ref Bits b)
            {
                int node = 0;
                while (true)
                {
                    int bit = b.Read(1);
                    if (bit < 0) return -1;
                    int next = Tree[node * 2 + bit];
                    if (next == int.MinValue) throw new NotSupportedException("codeword outside the codebook tree (STOP)");
                    if (next <= 0) return -next;
                    node = next;
                }
            }
        }

        static Codebook ReadCodebook(ref Bits b, string what)
        {
            if (b.Read(24) != 0x564342) throw new NotSupportedException($"{what}: codebook sync");
            var c = new Codebook { Dim = b.Read(16), Entries = b.Read(24) };
            if (c.Dim <= 0 || c.Entries <= 0) throw new NotSupportedException($"{what}: codebook dims");
            var len = new int[c.Entries];
            if (b.Read(1) != 0)
            {
                int l = b.Read(5) + 1;
                for (int i = 0; i < c.Entries; l++)
                {
                    int num = b.Read(ILog((uint)(c.Entries - i)));
                    if (num < 0 || i + num > c.Entries) throw new NotSupportedException($"{what}: ordered codebook");
                    for (int k = 0; k < num; k++) len[i++] = l;
                }
            }
            else
            {
                bool sparse = b.Read(1) != 0;
                for (int i = 0; i < c.Entries; i++)
                    len[i] = sparse ? (b.Read(1) != 0 ? b.Read(5) + 1 : 0) : b.Read(5) + 1;
            }
            BuildTree(c, len, what);
            int maptype = b.Read(4);
            if (maptype == 1 || maptype == 2)
            {
                // _float32_unpack 0x1000f950: ldexp(sign * (v & 0x1fffff), ((v >> 21) & 0x3ff) - 788), stored as float.
                float min = Float32Unpack((uint)b.Read(32)), delta = Float32Unpack((uint)b.Read(32));
                int qbits = b.Read(4) + 1; bool seq = b.Read(1) != 0;
                int quantvals = maptype == 1 ? Maptype1Quantvals(c.Entries, c.Dim) : c.Entries * c.Dim;
                var q = new int[quantvals];
                for (int i = 0; i < quantvals; i++) q[i] = b.Read(qbits);
                c.Values = new float[c.Entries * c.Dim];
                // _book_unquantize: val = ((|q| * delta) + last) + mindel in x87 registers; stored float; last = the stored
                // float when sequencep (fst dword).
                for (int j = 0; j < c.Entries; j++)
                {
                    float last = 0; int indexdiv = 1;
                    for (int k = 0; k < c.Dim; k++)
                    {
                        int qi = maptype == 1 ? q[j / indexdiv % quantvals] : q[j * c.Dim + k];
                        double v = (double)Math.Abs(qi) * delta + last + min;
                        c.Values[j * c.Dim + k] = (float)v;
                        if (seq) last = (float)v;
                        if (maptype == 1) indexdiv *= quantvals;
                    }
                }
            }
            else if (maptype != 0) throw new NotSupportedException($"{what}: codebook maptype {maptype}");
            return c;
        }

        static float Float32Unpack(uint v)
        {
            double m = v & 0x1fffff;
            if ((v & 0x80000000) != 0) m = -m;
            return (float)Math.ScaleB(m, (int)((v >> 21) & 0x3ff) - 788);
        }

        // _book_maptype1_quantvals 0x1000fcd0: the integer search from floor(pow(entries, 1/dim)).
        static int Maptype1Quantvals(int entries, int dim)
        {
            int vals = (int)Math.Floor(Math.Pow(entries, 1.0 / dim));
            while (true)
            {
                long acc = 1, acc1 = 1;
                for (int i = 0; i < dim; i++) { acc *= vals; acc1 *= vals + 1; }
                if (acc <= entries && acc1 > entries) return vals;
                if (acc > entries) vals--; else vals++;
            }
        }

        // _make_words (the Vorbis I codeword assignment) and the decode tree over it.
        static void BuildTree(Codebook c, int[] len, string what)
        {
            var marker = new uint[33];
            var tree = new List<int> { int.MinValue, int.MinValue };
            int used = 0;
            for (int i = 0; i < c.Entries; i++)
            {
                int length = len[i];
                if (length <= 0) continue;
                uint entry = marker[length], word = entry;   // r[i] = the marker before the update
                if (length < 32 && (entry >> length) != 0) throw new NotSupportedException($"{what}: overpopulated codebook");
                for (int j = length; j > 0; j--)
                {
                    if ((marker[j] & 1) != 0) { marker[j] = j == 1 ? marker[1] + 1 : marker[j - 1] << 1; break; }
                    marker[j]++;
                }
                for (int j = length + 1; j < 33; j++)
                    if ((marker[j] >> 1) == entry) { entry = marker[j]; marker[j] = marker[j - 1] << 1; } else break;
                int node = 0;
                for (int k = length - 1; k >= 0; k--)
                {
                    int bit = (int)((word >> k) & 1), slot = node * 2 + bit;
                    if (k == 0) { tree[slot] = -i; break; }
                    if (tree[slot] == int.MinValue) { tree[slot] = tree.Count / 2; tree.Add(int.MinValue); tree.Add(int.MinValue); }
                    else if (tree[slot] <= 0) throw new NotSupportedException($"{what}: codeword prefix clash");
                    node = tree[slot];
                }
                used++;
            }
            if (used == 1) throw new NotSupportedException($"{what}: single-entry codebook (no shipped carrier: STOP)");
            c.Tree = tree.ToArray();
        }

        // ── Floor 1 (vorbis.dll floor1_unpack/floor1_look; inverse1 0x10006f60, inverse2 0x10007250) ──────────────────
        sealed class Floor1
        {
            public int[] PartClass; public int[] ClassDim, ClassSubs, ClassBook; public int[][] ClassSubbook;
            public int Mult; public int[] PostList;
            public int Posts, N, QuantQ; public int[] ForwardIndex, LoNeighbor, HiNeighbor;
        }

        static Floor1 ReadFloor1(ref Bits b, Codebook[] books, string what)
        {
            var f = new Floor1();
            int parts = b.Read(5), maxclass = -1;
            f.PartClass = new int[parts];
            for (int i = 0; i < parts; i++) { f.PartClass[i] = b.Read(4); maxclass = Math.Max(maxclass, f.PartClass[i]); }
            int nc = maxclass + 1;
            f.ClassDim = new int[nc]; f.ClassSubs = new int[nc]; f.ClassBook = new int[nc]; f.ClassSubbook = new int[nc][];
            for (int j = 0; j < nc; j++)
            {
                f.ClassDim[j] = b.Read(3) + 1;
                f.ClassSubs[j] = b.Read(2);
                if (f.ClassSubs[j] != 0) f.ClassBook[j] = b.Read(8);
                f.ClassSubbook[j] = new int[1 << f.ClassSubs[j]];
                for (int k = 0; k < (1 << f.ClassSubs[j]); k++) f.ClassSubbook[j][k] = b.Read(8) - 1;
            }
            f.Mult = b.Read(2) + 1;
            int rangebits = b.Read(4);
            var posts = new List<int> { 0, 1 << rangebits };
            for (int i = 0; i < parts; i++)
                for (int k = 0; k < f.ClassDim[f.PartClass[i]]; k++) posts.Add(b.Read(rangebits));
            f.PostList = posts.ToArray();
            f.Posts = posts.Count; f.N = f.PostList[1];
            // floor1_look: qsort of the posts by x (icomp; shipped post lists have distinct x, the judge counts ties), the
            // quantiser and the neighbours.
            var order = new int[f.Posts];
            for (int i = 0; i < f.Posts; i++) order[i] = i;
            Array.Sort(order, (x, y) => f.PostList[x].CompareTo(f.PostList[y]));
            for (int i = 1; i < f.Posts; i++)
                if (f.PostList[order[i]] == f.PostList[order[i - 1]]) throw new NotSupportedException($"{what}: floor1 posts share an x (qsort order: STOP)");
            f.ForwardIndex = order;
            f.QuantQ = f.Mult switch { 1 => 256, 2 => 128, 3 => 86, _ => 64 };
            f.LoNeighbor = new int[f.Posts - 2]; f.HiNeighbor = new int[f.Posts - 2];
            for (int i = 0; i < f.Posts - 2; i++)
            {
                int lo = 0, hi = 1, lx = 0, hx = f.N, cx = f.PostList[i + 2];
                for (int j = 0; j < i + 2; j++)
                {
                    int x = f.PostList[j];
                    if (x > lx && x < cx) { lo = j; lx = x; }
                    if (x < hx && x > cx) { hi = j; hx = x; }
                }
                f.LoNeighbor[i] = lo; f.HiNeighbor[i] = hi;
            }
            return f;
        }

        // render_point 0x100066e0
        static int RenderPoint(int x0, int x1, int y0, int y1, int x)
        {
            y0 &= 0x7fff; y1 &= 0x7fff;
            int dy = y1 - y0, adx = x1 - x0, ady = Math.Abs(dy), err = ady * (x - x0), off = err / adx;
            return dy < 0 ? y0 - off : y0 + off;
        }

        // floor1_inverse1: the fit values, or null (an unused floor, or a read that ran out of packet).
        static int[] Floor1Decode(Floor1 f, Codebook[] books, ref Bits b)
        {
            if (b.Read(1) != 1) return null;
            var fit = new int[f.Posts];
            int bits = ILog((uint)(f.QuantQ - 1));
            fit[0] = b.Read(bits);   // not checked for -1 (as the DLL)
            fit[1] = b.Read(bits);
            for (int i = 0, j = 2; i < f.PartClass.Length; i++)
            {
                int cls = f.PartClass[i], cdim = f.ClassDim[cls], csubbits = f.ClassSubs[cls], csub = (1 << csubbits) - 1, cval = 0;
                if (csubbits != 0)
                {
                    cval = books[f.ClassBook[cls]].Decode(ref b);
                    if (cval == -1) return null;
                }
                for (int k = 0; k < cdim; k++)
                {
                    int book = f.ClassSubbook[cls][cval & csub];
                    cval >>= csubbits;
                    if (book >= 0)
                    {
                        int v = books[book].Decode(ref b);
                        fit[j + k] = v;
                        if (v == -1) return null;
                    }
                    else fit[j + k] = 0;
                }
                j += cdim;
            }
            for (int i = 2; i < f.Posts; i++)
            {
                int lo = f.LoNeighbor[i - 2], hi = f.HiNeighbor[i - 2];
                int predicted = RenderPoint(f.PostList[lo], f.PostList[hi], fit[lo], fit[hi], f.PostList[i]);
                int hiroom = f.QuantQ - predicted, loroom = predicted, room = (hiroom < loroom ? hiroom : loroom) << 1;
                int val = fit[i];
                if (val != 0)
                {
                    if (val >= room) val = hiroom > loroom ? val - loroom : -1 - (val - hiroom);
                    else val = (val & 1) != 0 ? -((val + 1) >> 1) : val >> 1;
                    fit[i] = val + predicted;
                    fit[lo] &= 0x7fff;
                    fit[hi] &= 0x7fff;
                }
                else fit[i] = predicted | 0x8000;
            }
            return fit;
        }

        // floor1_inverse2: d[0..n) *= the rendered floor (FLOOR_fromdB_LOOKUP), segment by segment over the used posts;
        // past the last used post the DLL multiplies by the integer ly itself (`fild ly`, 0x1000732b), not the table —
        // unreachable in shipped data (the last post is always x = n).
        static void Floor1Apply(Floor1 f, int[] fit, float[] d, int n)
        {
            int hx = 0, lx = 0, ly = fit[0] * f.Mult;
            for (int j = 1; j < f.Posts; j++)
            {
                int current = f.ForwardIndex[j];
                int hy = fit[current] & 0x7fff;
                if (hy == fit[current])
                {
                    hy *= f.Mult;
                    hx = f.PostList[current];
                    RenderLine(lx, hx, ly, hy, d);
                    lx = hx; ly = hy;
                }
            }
            for (int j = hx; j < n; j++) d[j] = (float)((double)ly * d[j]);
        }

        // render_line 0x10007360
        static void RenderLine(int x0, int x1, int y0, int y1, float[] d)
        {
            int dy = y1 - y0, adx = x1 - x0, ady = Math.Abs(dy), bas = dy / adx, sy = dy < 0 ? bas - 1 : bas + 1;
            int x = x0, y = y0, err = 0;
            ady -= Math.Abs(bas * adx);
            d[x] = FromdB[y] * d[x];
            while (++x < x1)
            {
                err += ady;
                if (err >= adx) { err -= adx; y += sy; } else y += bas;
                d[x] = FromdB[y] * d[x];
            }
        }

        // FLOOR_fromdB_LOOKUP (vorbis.dll .rdata 0x10015128; the Vorbis I floor1_inverse_dB_table)
        static readonly float[] FromdB =
        {
            1.0649863e-07F, 1.1341951e-07F, 1.2079015e-07F, 1.2863978e-07F, 1.369995e-07F, 1.459025e-07F,
            1.5538409e-07F, 1.6548181e-07F, 1.7623574e-07F, 1.8768856e-07F, 1.998856e-07F, 2.128753e-07F,
            2.2670913e-07F, 2.4144197e-07F, 2.5713223e-07F, 2.7384212e-07F, 2.9163792e-07F, 3.1059022e-07F,
            3.307741e-07F, 3.5226967e-07F, 3.7516213e-07F, 3.995423e-07F, 4.255068e-07F, 4.5315863e-07F,
            4.8260745e-07F, 5.1397e-07F, 5.4737063e-07F, 5.829419e-07F, 6.208247e-07F, 6.611694e-07F,
            7.041359e-07F, 7.4989464e-07F, 7.98627e-07F, 8.505263e-07F, 9.057983e-07F, 9.646621e-07F,
            1.0273513e-06F, 1.0941144e-06F, 1.1652161e-06F, 1.2409384e-06F, 1.3215816e-06F, 1.4074654e-06F,
            1.4989305e-06F, 1.5963394e-06F, 1.7000785e-06F, 1.8105592e-06F, 1.9282195e-06F, 2.053526e-06F,
            2.1869757e-06F, 2.3290977e-06F, 2.4804558e-06F, 2.6416496e-06F, 2.813319e-06F, 2.9961443e-06F,
            3.1908505e-06F, 3.39821e-06F, 3.619045e-06F, 3.8542307e-06F, 4.1047006e-06F, 4.371447e-06F,
            4.6555283e-06F, 4.958071e-06F, 5.280274e-06F, 5.623416e-06F, 5.988857e-06F, 6.3780467e-06F,
            6.7925284e-06F, 7.2339453e-06F, 7.704048e-06F, 8.2047e-06F, 8.737888e-06F, 9.305725e-06F,
            9.910464e-06F, 1.0554501e-05F, 1.1240392e-05F, 1.1970856e-05F, 1.2748789e-05F, 1.3577278e-05F,
            1.4459606e-05F, 1.5399271e-05F, 1.6400005e-05F, 1.7465769e-05F, 1.8600793e-05F, 1.9809577e-05F,
            2.1096914e-05F, 2.2467912e-05F, 2.3928002e-05F, 2.5482977e-05F, 2.7139005e-05F, 2.890265e-05F,
            3.078091e-05F, 3.2781227e-05F, 3.4911533e-05F, 3.718028e-05F, 3.9596467e-05F, 4.2169668e-05F,
            4.491009e-05F, 4.7828602e-05F, 5.0936775e-05F, 5.424693e-05F, 5.7772202e-05F, 6.152657e-05F,
            6.552491e-05F, 6.9783084e-05F, 7.4317984e-05F, 7.914758e-05F, 8.429104e-05F, 8.976875e-05F,
            9.560242e-05F, 1.0181521e-04F, 1.0843174e-04F, 1.1547824e-04F, 1.2298267e-04F, 1.3097477e-04F,
            1.3948625e-04F, 1.4855085e-04F, 1.5820454e-04F, 1.6848555e-04F, 1.7943469e-04F, 1.9109536e-04F,
            2.0351382e-04F, 2.167393e-04F, 2.3082423e-04F, 2.4582449e-04F, 2.6179955e-04F, 2.7881275e-04F,
            2.9693157e-04F, 3.1622787e-04F, 3.3677815e-04F, 3.5866388e-04F, 3.8197188e-04F, 4.0679457e-04F,
            4.3323037e-04F, 4.613841e-04F, 4.913675e-04F, 5.2329927e-04F, 5.573062e-04F, 5.935231e-04F,
            6.320936e-04F, 6.731706e-04F, 7.16917e-04F, 7.635063e-04F, 8.1312325e-04F, 8.6596457e-04F,
            9.2223985e-04F, 9.821722e-04F, 1.0459992e-03F, 1.1139743e-03F, 1.1863665e-03F, 1.2634633e-03F,
            1.3455702e-03F, 1.4330129e-03F, 1.5261382e-03F, 1.6253153e-03F, 1.7309374e-03F, 1.8434235e-03F,
            1.9632196e-03F, 2.0908006e-03F, 2.2266726e-03F, 2.3713743e-03F, 2.5254795e-03F, 2.6895993e-03F,
            2.8643848e-03F, 3.0505287e-03F, 3.248769e-03F, 3.4598925e-03F, 3.6847359e-03F, 3.9241905e-03F,
            4.1792067e-03F, 4.450795e-03F, 4.740033e-03F, 5.048067e-03F, 5.3761187e-03F, 5.725489e-03F,
            6.0975635e-03F, 6.4938175e-03F, 6.9158226e-03F, 7.3652514e-03F, 7.843887e-03F, 8.353627e-03F,
            8.896492e-03F, 9.474637e-03F, 0.010090352F, 0.01074608F, 0.011444421F, 0.012188144F,
            0.012980198F, 0.013823725F, 0.014722068F, 0.015678791F, 0.016697686F, 0.017782796F,
            0.018938422F, 0.020169148F, 0.021479854F, 0.022875736F, 0.02436233F, 0.025945531F,
            0.027631618F, 0.029427277F, 0.031339627F, 0.03337625F, 0.035545226F, 0.037855156F,
            0.0403152F, 0.042935107F, 0.045725275F, 0.048696756F, 0.05186135F, 0.05523159F,
            0.05882085F, 0.062643364F, 0.06671428F, 0.07104975F, 0.075666964F, 0.08058423F,
            0.08582105F, 0.09139818F, 0.097337745F, 0.1036633F, 0.11039993F, 0.11757434F,
            0.12521498F, 0.13335215F, 0.14201812F, 0.15124726F, 0.16107617F, 0.1715438F,
            0.18269168F, 0.19456401F, 0.20720787F, 0.22067343F, 0.23501402F, 0.25028655F,
            0.26655158F, 0.28387362F, 0.3023213F, 0.32196787F, 0.34289113F, 0.36517414F,
            0.3889052F, 0.41417846F, 0.44109413F, 0.4697589F, 0.50028646F, 0.53279793F,
            0.5674221F, 0.6042964F, 0.64356697F, 0.6853896F, 0.72993004F, 0.777365F,
            0.8278826F, 0.88168305F, 0.9389798F, 1F,
        };

        // ── Residue 2 (res2_inverse 0x1000f7a0, vorbis_book_decodevv_add 0x10003ec0) ─────────────────────────────────
        sealed class Residue2
        {
            public int Begin, End, Grouping, Parts, GroupBook; public int[] SecondStages; public int[][] PartBooks;
            public int Stages; public int[][] DecodeMap;
        }

        static Residue2 ReadResidue(ref Bits b, Codebook[] books, string what)
        {
            int type = b.Read(16);
            if (type != 2) throw new NotSupportedException($"{what}: residue type {type} (only type 2 ships: STOP)");
            var r = new Residue2 { Begin = b.Read(24), End = b.Read(24), Grouping = b.Read(24) + 1, Parts = b.Read(6) + 1, GroupBook = b.Read(8) };
            r.SecondStages = new int[r.Parts];
            int acc = 0;
            for (int j = 0; j < r.Parts; j++)
            {
                int cascade = b.Read(3);
                if (b.Read(1) != 0) cascade |= b.Read(5) << 3;
                r.SecondStages[j] = cascade;
                for (int k = 0; k < 8; k++) acc += (cascade >> k) & 1;
            }
            var booklist = new int[acc];
            for (int k = 0; k < acc; k++) booklist[k] = b.Read(8);
            r.PartBooks = new int[r.Parts][];
            int a = 0, maxstage = 0;
            for (int j = 0; j < r.Parts; j++)
            {
                int stages = ILog((uint)r.SecondStages[j]);
                if (stages == 0) continue;
                maxstage = Math.Max(maxstage, stages);
                r.PartBooks[j] = new int[stages];
                for (int k = 0; k < stages; k++)
                    r.PartBooks[j][k] = (r.SecondStages[j] & (1 << k)) != 0 ? booklist[a++] : -1;
            }
            r.Stages = maxstage;
            int dim = books[r.GroupBook].Dim, partvals = 1;
            for (int k = 0; k < dim; k++) partvals *= r.Parts;
            r.DecodeMap = new int[partvals][];
            for (int j = 0; j < partvals; j++)
            {
                int val = j, mult = partvals / r.Parts;
                r.DecodeMap[j] = new int[dim];
                for (int k = 0; k < dim; k++) { int deco = val / mult; val -= deco * mult; mult /= r.Parts; r.DecodeMap[j][k] = deco; }
            }
            return r;
        }

        static void Residue2Decode(Residue2 r, Codebook[] books, ref Bits b, float[][] pcm, bool[] nonzero, int ch)
        {
            int spp = r.Grouping, ppw = books[r.GroupBook].Dim, n = r.End - r.Begin, partvals = n / spp;
            int partwords = (partvals + ppw - 1) / ppw;
            var partword = new int[partwords][];
            int i0 = 0;
            for (; i0 < ch; i0++) if (nonzero[i0]) break;
            if (i0 == ch) return;
            for (int s = 0; s < r.Stages; s++)
            {
                for (int i = 0, l = 0; i < partvals; l++)
                {
                    if (s == 0)
                    {
                        int temp = books[r.GroupBook].Decode(ref b);
                        if (temp == -1) return;
                        if (temp >= r.DecodeMap.Length) throw new NotSupportedException("residue classword past the decode map (STOP)");
                        partword[l] = r.DecodeMap[temp];
                    }
                    for (int k = 0; k < ppw && i < partvals; k++, i++)
                    {
                        int cls = partword[l][k];
                        if ((r.SecondStages[cls] & (1 << s)) == 0) continue;
                        int book = r.PartBooks[cls][s];
                        if (book < 0) continue;
                        if (DecodeVvAdd(books[book], pcm, i * spp + r.Begin, ch, ref b, spp) == -1) return;
                    }
                }
            }
        }

        static int DecodeVvAdd(Codebook book, float[][] a, int offset, int ch, ref Bits b, int n)
        {
            int chptr = 0;
            for (int i = offset / ch; i < (offset + n) / ch;)
            {
                int entry = book.Decode(ref b);
                if (entry == -1) return -1;
                if (book.Values is null) throw new NotSupportedException("residue book without values (STOP)");
                int t = entry * book.Dim;
                for (int j = 0; j < book.Dim; j++)
                {
                    a[chptr][i] = a[chptr][i] + book.Values[t + j];
                    if (++chptr == ch) { chptr = 0; i++; }
                }
            }
            return 0;
        }

        // ── MDCT (mdct_init 0x1000a3f0, mdct_backward 0x1000a610 with butterflies 0x1000ab20/0x1000afa0/0x1000b030) ──
        sealed class Mdct
        {
            public readonly int N, Log2n; public readonly float[] T; public readonly int[] BitRev;
            const float PiF = 3.14159274f;   // float M_PI (.rdata 0x10014130)

            public Mdct(int n)
            {
                N = n; Log2n = (int)Math.Floor(Math.Log((float)n) / Math.Log(2.0) + 0.5);
                int n2 = n >> 1;
                T = new float[n + n / 4]; BitRev = new int[n / 4];
                double a = (double)PiF / (float)n, b2 = PiF / (2.0 * n);
                for (int i = 0; i < n / 4; i++)
                {
                    double x = 4 * i * a, y = (2 * i + 1) * b2;
                    T[i * 2] = (float)Math.Cos(x); T[i * 2 + 1] = (float)-Math.Sin(x);
                    T[n2 + i * 2] = (float)Math.Cos(y); T[n2 + i * 2 + 1] = (float)Math.Sin(y);
                }
                for (int i = 0; i < n / 8; i++)
                {
                    double x = (4 * i + 2) * a;
                    T[n + i * 2] = (float)(Math.Cos(x) * 0.5); T[n + i * 2 + 1] = (float)(Math.Sin(x) * -0.5);
                }
                int mask = (1 << (Log2n - 1)) - 1, msb = 1 << (Log2n - 2);
                for (int i = 0; i < n / 8; i++)
                {
                    int acc = 0;
                    for (int j = 0; (msb >> j) != 0; j++) if (((msb >> j) & i) != 0) acc |= 1 << j;
                    BitRev[i * 2] = (~acc & mask) - 1; BitRev[i * 2 + 1] = acc;
                }
            }

            // In place (mapping0_inverse calls it with in == out).
            public void Backward(float[] x)
            {
                int n = N, n2 = n >> 1, n4 = n >> 2;
                // rotate 1: iX = in + n2 - 7 down by 8, oX = out + n2 + n4 down by 4, T = trig + n4 up by 4
                {
                    int iX = n2 - 7, oX = n2 + n4, t = n4;
                    do
                    {
                        oX -= 4;
                        x[oX + 0] = (float)(-((double)x[iX + 2] * T[t + 3]) - (double)x[iX + 0] * T[t + 2]);
                        x[oX + 1] = (float)((double)x[iX + 0] * T[t + 3] - (double)x[iX + 2] * T[t + 2]);
                        x[oX + 2] = (float)(-((double)T[t + 1] * x[iX + 6]) - (double)x[iX + 4] * T[t + 0]);
                        x[oX + 3] = (float)((double)T[t + 1] * x[iX + 4] - (double)T[t + 0] * x[iX + 6]);
                        iX -= 8; t += 4;
                    } while (iX >= 0);
                }
                // rotate 2: iX = in + n2 - 8 down by 8, oX = out + n2 + n4 up by 4, T = trig + n4 down by 4
                {
                    int iX = n2 - 8, oX = n2 + n4, t = n4;
                    do
                    {
                        t -= 4;
                        x[oX + 0] = (float)((double)x[iX + 4] * T[t + 3] + (double)T[t + 2] * x[iX + 6]);
                        x[oX + 1] = (float)((double)x[iX + 4] * T[t + 2] - (double)T[t + 3] * x[iX + 6]);
                        x[oX + 2] = (float)((double)x[iX + 2] * T[t + 0] + (double)x[iX + 0] * T[t + 1]);
                        x[oX + 3] = (float)((double)x[iX + 0] * T[t + 0] - (double)T[t + 1] * x[iX + 2]);
                        iX -= 8; oX += 4;
                    } while (iX >= 0);
                }
                Butterflies(x, n2, n2);
                BitReverse(x);
                // rotate + window (0x1000a9e9): T = trig + n2
                {
                    int oX1 = n2 + n4, oX2 = n2 + n4, iX = 0, t = n2;
                    do
                    {
                        oX1 -= 4;
                        x[oX1 + 3] = (float)((double)T[t + 1] * x[iX + 0] - (double)x[iX + 1] * T[t + 0]);
                        x[oX2 + 0] = (float)-((double)T[t + 1] * x[iX + 1] + (double)x[iX + 0] * T[t + 0]);
                        x[oX1 + 2] = (float)((double)x[iX + 2] * T[t + 3] - (double)x[iX + 3] * T[t + 2]);
                        x[oX2 + 1] = (float)-((double)x[iX + 3] * T[t + 3] + (double)x[iX + 2] * T[t + 2]);
                        x[oX1 + 1] = (float)((double)x[iX + 4] * T[t + 5] - (double)x[iX + 5] * T[t + 4]);
                        x[oX2 + 2] = (float)-((double)x[iX + 5] * T[t + 5] + (double)x[iX + 4] * T[t + 4]);
                        x[oX1 + 0] = (float)((double)x[iX + 6] * T[t + 7] - (double)x[iX + 7] * T[t + 6]);
                        x[oX2 + 3] = (float)-((double)x[iX + 7] * T[t + 7] + (double)x[iX + 6] * T[t + 6]);
                        oX2 += 4; iX += 8; t += 8;
                    } while (iX < oX1);
                    iX = n2 + n4; oX1 = n4; oX2 = oX1;
                    do
                    {
                        oX1 -= 4; iX -= 4;
                        x[oX2 + 0] = -(x[oX1 + 3] = x[iX + 3]);
                        x[oX2 + 1] = -(x[oX1 + 2] = x[iX + 2]);
                        x[oX2 + 2] = -(x[oX1 + 1] = x[iX + 1]);
                        x[oX2 + 3] = -(x[oX1 + 0] = x[iX + 0]);
                        oX2 += 4;
                    } while (oX2 < iX);
                    iX = n2 + n4; oX1 = n2 + n4; oX2 = n2;
                    do
                    {
                        oX1 -= 4;
                        x[oX1 + 0] = x[iX + 3]; x[oX1 + 1] = x[iX + 2]; x[oX1 + 2] = x[iX + 1]; x[oX1 + 3] = x[iX + 0];
                        iX += 4;
                    } while (oX1 > oX2);
                }
            }

            // mdct_butterflies (inlined in mdct_backward): first (log2n - 6 > 0), generic stages, then 32-point blocks.
            void Butterflies(float[] x, int o, int points)
            {
                int stages = Log2n - 5;
                if (--stages > 0) ButterflyFirst(x, o, points);
                for (int i = 1; --stages > 0; i++)
                    for (int j = 0; j < (1 << i); j++)
                        ButterflyGeneric(x, o + (points >> i) * j, points >> i, 4 << i);
                for (int j = 0; j < points; j += 32) Butterfly32(x, o + j);
            }

            void ButterflyFirst(float[] x, int o, int points)
            {
                int x1 = o + points - 8, x2 = o + (points >> 1) - 8, t = 0;
                do
                {
                    for (int k = 6, tt = 0; k >= 0; k -= 2, tt += 4)
                    {
                        double r0 = (double)x[x1 + k] - x[x2 + k], r1 = (double)x[x1 + k + 1] - x[x2 + k + 1];
                        x[x1 + k] = x[x1 + k] + x[x2 + k];
                        x[x1 + k + 1] = x[x2 + k + 1] + x[x1 + k + 1];
                        x[x2 + k] = (float)(r1 * T[t + tt + 1] + r0 * T[t + tt]);
                        x[x2 + k + 1] = (float)(r1 * T[t + tt] - r0 * T[t + tt + 1]);
                    }
                    x1 -= 8; x2 -= 8; t += 16;
                } while (x2 >= o);
            }

            void ButterflyGeneric(float[] x, int o, int points, int trigint)
            {
                int x1 = o + points - 8, x2 = o + (points >> 1) - 8, t = 0;
                do
                {
                    for (int k = 6; k >= 0; k -= 2)
                    {
                        double r0 = (double)x[x1 + k] - x[x2 + k], r1 = (double)x[x1 + k + 1] - x[x2 + k + 1];
                        x[x1 + k] = x[x1 + k] + x[x2 + k];
                        x[x1 + k + 1] = x[x2 + k + 1] + x[x1 + k + 1];
                        x[x2 + k] = (float)(r1 * T[t + 1] + r0 * T[t]);
                        x[x2 + k + 1] = (float)(r1 * T[t] - r0 * T[t + 1]);
                        t += trigint;
                    }
                    x1 -= 8; x2 -= 8;
                } while (x2 >= o);
            }

            // mdct_bitreverse (inlined, 0x1000a8d8): r2 and the second HALVE are spilled to float, r3 / the first HALVE stay in st.
            void BitReverse(float[] x)
            {
                int n = N, bit = 0, w0 = 0, w1 = n >> 1, xo = n >> 1, t = n;
                do
                {
                    for (int h = 0; h < 2; h++)
                    {
                        int x0 = xo + BitRev[bit + 2 * h], x1 = xo + BitRev[bit + 2 * h + 1];
                        double r0 = (double)x[x0 + 1] - x[x1 + 1], r1 = (double)x[x1] + x[x0];
                        float r2 = (float)(r1 * T[t + 2 * h] + r0 * T[t + 2 * h + 1]);
                        double r3 = r1 * T[t + 2 * h + 1] - r0 * T[t + 2 * h];
                        double q0 = ((double)x[x1 + 1] + x[x0 + 1]) * 0.5f;
                        float q1 = (float)(((double)x[x0] - x[x1]) * 0.5f);
                        if (h == 0)
                        {
                            w1 -= 4;
                            x[w0 + 0] = (float)(r2 + q0); x[w1 + 2] = (float)(q0 - r2);
                            x[w0 + 1] = (float)(r3 + q1); x[w1 + 3] = (float)(r3 - q1);
                        }
                        else
                        {
                            x[w0 + 2] = (float)(r2 + q0); x[w1 + 0] = (float)(q0 - r2);
                            x[w0 + 3] = (float)(r3 + q1); x[w1 + 1] = (float)(r3 - q1);
                        }
                    }
                    t += 4; bit += 4; w0 += 4;
                } while (w0 < w1);
            }

            const float C1_8 = 0.9238795042037964f, C3_8 = 0.3826834261417389f, C2_8 = 0.7071067690849304f;   // .rdata 0x10014198/94/90

            // mdct_butterfly_32 0x1000ab20 (with mdct_butterfly_16 and the first mdct_butterfly_8 of each half inlined):
            // generated from the instruction stream by tools/hp2/x87cs.py — st registers are doubles, dword stores floats.
            static void Butterfly32(float[] x, int o)
            {
                double t1 = (double)x[o + 30] - x[o + 14];
                double t2 = (double)x[o + 31] - x[o + 15];
                float s2 = (float)t2;
                double t3 = (double)x[o + 14] + x[o + 30];
                x[o + 30] = (float)t3;
                double t4 = (double)x[o + 31] + x[o + 15];
                x[o + 15] = s2;
                x[o + 31] = (float)t4;
                x[o + 14] = (float)t1;
                double t5 = (double)x[o + 28] - x[o + 12];
                double t6 = (double)x[o + 29] - x[o + 13];
                double t7 = (double)x[o + 12] + x[o + 28];
                x[o + 28] = (float)t7;
                double t8 = (double)x[o + 29] + x[o + 13];
                x[o + 29] = (float)t8;
                double t9 = t5 * C1_8;
                double t10 = t6 * C3_8;
                double t11 = t9 - t10;
                x[o + 12] = (float)t11;
                double t12 = t6 * C1_8;
                double t13 = t5 * C3_8;
                double t14 = t12 + t13;
                x[o + 13] = (float)t14;
                double t15 = (double)x[o + 26] - x[o + 10];
                double t16 = (double)x[o + 27] - x[o + 11];
                double t17 = (double)x[o + 10] + x[o + 26];
                x[o + 26] = (float)t17;
                double t18 = (double)x[o + 27] + x[o + 11];
                x[o + 27] = (float)t18;
                double t19 = t15 - t16;
                double t20 = t19 * C2_8;
                x[o + 10] = (float)t20;
                double t21 = t16 + t15;
                double t22 = t21 * C2_8;
                x[o + 11] = (float)t22;
                double t23 = (double)x[o + 24] - x[o + 8];
                double t24 = (double)x[o + 25] - x[o + 9];
                double t25 = (double)x[o + 8] + x[o + 24];
                x[o + 24] = (float)t25;
                double t26 = (double)x[o + 25] + x[o + 9];
                x[o + 25] = (float)t26;
                double t27 = t23 * C3_8;
                double t28 = t24 * C1_8;
                double t29 = t27 - t28;
                x[o + 8] = (float)t29;
                double t30 = t24 * C3_8;
                double t31 = t23 * C1_8;
                double t32 = t30 + t31;
                x[o + 9] = (float)t32;
                double t33 = (double)x[o + 22] - x[o + 6];
                double t34 = (double)x[o + 7] - x[o + 23];
                double t35 = (double)x[o + 6] + x[o + 22];
                x[o + 22] = (float)t35;
                double t36 = (double)x[o + 7] + x[o + 23];
                x[o + 23] = (float)t36;
                x[o + 6] = (float)t34;
                x[o + 7] = (float)t33;
                double t37 = (double)x[o + 4] - x[o + 20];
                double t38 = (double)x[o + 5] - x[o + 21];
                double t39 = (double)x[o + 20] + x[o + 4];
                x[o + 20] = (float)t39;
                double t40 = (double)x[o + 5] + x[o + 21];
                x[o + 21] = (float)t40;
                double t41 = t38 * C1_8;
                double t42 = t37 * C3_8;
                double t43 = t41 + t42;
                x[o + 4] = (float)t43;
                double t44 = t38 * C3_8;
                double t45 = t37 * C1_8;
                double t46 = t44 - t45;
                x[o + 5] = (float)t46;
                double t47 = (double)x[o + 2] - x[o + 18];
                double t48 = (double)x[o + 3] - x[o + 19];
                double t49 = (double)x[o + 18] + x[o + 2];
                x[o + 18] = (float)t49;
                double t50 = (double)x[o + 19] + x[o + 3];
                x[o + 19] = (float)t50;
                double t51 = t48 + t47;
                double t52 = t51 * C2_8;
                x[o + 2] = (float)t52;
                double t53 = t48 - t47;
                double t54 = t53 * C2_8;
                x[o + 3] = (float)t54;
                double t55 = (double)x[o + 0] - x[o + 16];
                double t56 = (double)x[o + 1] - x[o + 17];
                double t57 = (double)x[o + 16] + x[o + 0];
                x[o + 16] = (float)t57;
                double t58 = (double)x[o + 17] + x[o + 1];
                x[o + 17] = (float)t58;
                double t59 = t56 * C3_8;
                double t60 = t55 * C1_8;
                double t61 = t59 + t60;
                float s62 = (float)t61;
                double t62 = t56 * C1_8;
                double t63 = t55 * C3_8;
                double t64 = t62 - t63;
                double t65 = t64 - x[o + 9];
                float s67 = (float)t65;
                double t66 = (double)s62 - x[o + 8];
                float s69 = (float)t66;
                double t67 = (double)s62 + x[o + 8];
                x[o + 8] = (float)t67;
                double t68 = t64 + x[o + 9];
                x[o + 9] = (float)t68;
                double t69 = (double)s69 + s67;
                double t70 = t69 * C2_8;
                x[o + 0] = (float)t70;
                double t71 = (double)s67 - s69;
                double t72 = t71 * C2_8;
                x[o + 1] = (float)t72;
                double t73 = (double)x[o + 3] - x[o + 11];
                double t74 = (double)x[o + 10] - x[o + 2];
                float s77 = (float)t74;
                double t75 = (double)x[o + 2] + x[o + 10];
                x[o + 10] = (float)t75;
                double t76 = (double)x[o + 3] + x[o + 11];
                x[o + 3] = s77;
                x[o + 11] = (float)t76;
                x[o + 2] = (float)t73;
                double t77 = (double)x[o + 12] - x[o + 4];
                double t78 = (double)x[o + 13] - x[o + 5];
                double t79 = (double)x[o + 12] + x[o + 4];
                x[o + 12] = (float)t79;
                double t80 = (double)x[o + 5] + x[o + 13];
                x[o + 13] = (float)t80;
                double t81 = t77 - t78;
                double t82 = t81 * C2_8;
                x[o + 4] = (float)t82;
                double t83 = t78 + t77;
                double t84 = t83 * C2_8;
                x[o + 5] = (float)t84;
                double t85 = (double)x[o + 14] - x[o + 6];
                double t86 = (double)x[o + 15] - x[o + 7];
                float s89 = (float)t86;
                double t87 = (double)x[o + 14] + x[o + 6];
                x[o + 14] = (float)t87;
                double t88 = (double)x[o + 7] + x[o + 15];
                x[o + 15] = (float)t88;
                double t89 = t85 + x[o + 2];
                float s92 = (float)t89;
                double t90 = t85 - x[o + 2];
                double t91 = (double)x[o + 0] + x[o + 4];
                double t92 = (double)x[o + 4] - x[o + 0];
                float s95 = (float)t92;
                double t93 = t91 + s92;
                x[o + 6] = (float)t93;
                double t94 = (double)s92 - t91;
                x[o + 4] = (float)t94;
                double t95 = (double)x[o + 5] - x[o + 1];
                double t96 = (double)s89 - x[o + 3];
                float s99 = (float)t96;
                double t97 = t90 + t95;
                x[o + 0] = (float)t97;
                double t98 = t90 - t95;
                x[o + 2] = (float)t98;
                double t99 = (double)x[o + 5] + x[o + 1];
                double t100 = (double)s89 + x[o + 3];
                double t101 = (double)s95 + s99;
                x[o + 3] = (float)t101;
                double t102 = (double)s99 - s95;
                x[o + 1] = (float)t102;
                double t103 = t100 + t99;
                x[o + 7] = (float)t103;
                double t104 = t100 - t99;
                x[o + 5] = (float)t104;
                Butterfly8(x, o + 8);
                double t105 = (double)x[o + 17] - x[o + 25];
                double t106 = (double)x[o + 16] - x[o + 24];
                double t107 = (double)x[o + 16] + x[o + 24];
                x[o + 24] = (float)t107;
                double t108 = (double)x[o + 25] + x[o + 17];
                x[o + 25] = (float)t108;
                double t109 = t106 + t105;
                double t110 = t109 * C2_8;
                x[o + 16] = (float)t110;
                double t111 = t105 - t106;
                double t112 = t111 * C2_8;
                x[o + 17] = (float)t112;
                double t113 = (double)x[o + 19] - x[o + 27];
                double t114 = (double)x[o + 26] - x[o + 18];
                float s117 = (float)t114;
                double t115 = (double)x[o + 26] + x[o + 18];
                x[o + 26] = (float)t115;
                double t116 = (double)x[o + 27] + x[o + 19];
                x[o + 19] = s117;
                x[o + 27] = (float)t116;
                x[o + 18] = (float)t113;
                double t117 = (double)x[o + 28] - x[o + 20];
                double t118 = (double)x[o + 29] - x[o + 21];
                double t119 = (double)x[o + 20] + x[o + 28];
                x[o + 28] = (float)t119;
                double t120 = (double)x[o + 29] + x[o + 21];
                x[o + 29] = (float)t120;
                double t121 = t117 - t118;
                double t122 = t121 * C2_8;
                x[o + 20] = (float)t122;
                double t123 = t118 + t117;
                double t124 = t123 * C2_8;
                x[o + 21] = (float)t124;
                double t125 = (double)x[o + 30] - x[o + 22];
                double t126 = (double)x[o + 31] - x[o + 23];
                float s129 = (float)t126;
                double t127 = (double)x[o + 22] + x[o + 30];
                x[o + 30] = (float)t127;
                double t128 = (double)x[o + 31] + x[o + 23];
                x[o + 31] = (float)t128;
                double t129 = t125 + x[o + 18];
                float s132 = (float)t129;
                double t130 = t125 - x[o + 18];
                double t131 = (double)x[o + 16] + x[o + 20];
                double t132 = (double)x[o + 20] - x[o + 16];
                float s135 = (float)t132;
                double t133 = t131 + s132;
                x[o + 22] = (float)t133;
                double t134 = (double)s132 - t131;
                x[o + 20] = (float)t134;
                double t135 = (double)x[o + 21] - x[o + 17];
                double t136 = (double)s129 - x[o + 19];
                float s139 = (float)t136;
                double t137 = t130 + t135;
                x[o + 16] = (float)t137;
                double t138 = t130 - t135;
                x[o + 18] = (float)t138;
                double t139 = (double)x[o + 17] + x[o + 21];
                double t140 = (double)s129 + x[o + 19];
                double t141 = (double)s135 + s139;
                x[o + 19] = (float)t141;
                double t142 = (double)s139 - s135;
                x[o + 17] = (float)t142;
                double t143 = t140 + t139;
                x[o + 23] = (float)t143;
                double t144 = t140 - t139;
                x[o + 21] = (float)t144;
                Butterfly8(x, o + 24);
            }

            // mdct_butterfly_8 0x1000afa0 (generated as above)
            static void Butterfly8(float[] x, int o)
            {
                double t1 = (double)x[o + 2] + x[o + 6];
                double t2 = (double)x[o + 6] - x[o + 2];
                float s2 = (float)t2;
                double t3 = (double)x[o + 0] + x[o + 4];
                double t4 = (double)x[o + 4] - x[o + 0];
                float s5 = (float)t4;
                double t5 = t3 + t1;
                x[o + 6] = (float)t5;
                double t6 = t1 - t3;
                x[o + 4] = (float)t6;
                double t7 = (double)x[o + 5] - x[o + 1];
                double t8 = (double)x[o + 7] - x[o + 3];
                float s10 = (float)t8;
                double t9 = (double)s2 + t7;
                x[o + 0] = (float)t9;
                double t10 = (double)s2 - t7;
                x[o + 2] = (float)t10;
                double t11 = (double)x[o + 1] + x[o + 5];
                double t12 = (double)x[o + 3] + x[o + 7];
                double t13 = (double)s5 + s10;
                x[o + 3] = (float)t13;
                double t14 = (double)s10 - s5;
                x[o + 1] = (float)t14;
                double t15 = t12 + t11;
                x[o + 7] = (float)t15;
                double t16 = t12 - t11;
                x[o + 5] = (float)t16;
            }
        }

        // _vorbis_window 0x10013370 (type 0): x = sin(sin((i + .5f) / left * pi_f * .5)^2 * (float)(pi / 2)), x87 throughout.
        static float[] Window(int window, int left, int right)
        {
            const float PiF = 3.14159274f, HalfPiF = 1.57079637f;   // .rdata 0x10014130, 0x100141f8
            var w = new float[window];
            int leftbegin = window / 4 - left / 2, rightbegin = window - window / 4 - right / 2;
            for (int i = 0; i < left; i++)
            {
                double s = Math.Sin((i + 0.5) / left * PiF * 0.5);
                w[i + leftbegin] = (float)Math.Sin(s * s * HalfPiF);
            }
            for (int i = leftbegin + left; i < rightbegin; i++) w[i] = 1f;
            for (int i = 0, c = right; i < right; i++, c--)
            {
                double s = Math.Sin((c - 0.5) / right * PiF * 0.5);
                w[i + rightbegin] = (float)Math.Sin(s * s * HalfPiF);
            }
            return w;
        }

        // ── Stream setup ────────────────────────────────────────────────────────────────────────────────────────────
        readonly byte[] _data;
        readonly List<Packet> _packets;
        readonly string _what;
        public int Channels { get; }
        public int SampleRate { get; }
        // ov_pcm_total(vf, -1) for the single link: the last page's granule position (the lap length, samples per channel).
        public long PcmTotal { get; }
        readonly int[] _bs = new int[2];
        Codebook[] _books; Floor1[] _floors; Residue2[] _residues;
        int[] _mapFloor; int[] _mapResidue; int _couplingMag, _couplingAng; bool _coupled;   // mapping 0, one submap
        (bool Long, int Mapping)[] _modes;
        Mdct[] _mdct; float[][] _windows;   // index (W * 2 + lW) * 2 + nW (_vds_shared_init 0x100026c0)

        public UEOggVorbis(byte[] file, string what)
        {
            _what = what;
            (_data, _packets) = ReadPackets(file ?? throw new ArgumentNullException(nameof(file)), what);
            if (_packets.Count < 3) throw new NotSupportedException($"{what}: fewer than three Vorbis headers");
            var id = Header(0, 1);
            if (id.Read(32) != 0) throw new NotSupportedException($"{what}: Vorbis version");
            Channels = id.Read(8); SampleRate = id.Read(32); id.Read(32); id.Read(32); id.Read(32);
            _bs[0] = 1 << id.Read(4); _bs[1] = 1 << id.Read(4);
            if (Channels < 1 || SampleRate < 1 || _bs[0] < 64 || _bs[1] < _bs[0]) throw new NotSupportedException($"{what}: identification header");
            Header(1, 3);
            ReadSetup(Header(2, 5));
            long last = -1;
            foreach (var p in _packets) if (p.GranulePos != -1) last = p.GranulePos;
            PcmTotal = last;
            _mdct = new[] { new Mdct(_bs[0]), new Mdct(_bs[1]) };
            int h0 = _bs[0] / 2, h1 = _bs[1] / 2;
            var w0 = Window(_bs[0], h0, h0);
            _windows = new[] { w0, w0, w0, w0, Window(_bs[1], h0, h0), Window(_bs[1], h0, h1), Window(_bs[1], h1, h0), Window(_bs[1], h1, h1) };
            Restart();
        }

        Bits Header(int index, int type)
        {
            var p = _packets[index];
            var b = new Bits(_data, p.Start, p.Bytes);
            if (b.Read(8) != type) throw new NotSupportedException($"{_what}: header {index} type");
            foreach (char ch in "vorbis") if (b.Read(8) != ch) throw new NotSupportedException($"{_what}: header {index} magic");
            return b;
        }

        void ReadSetup(Bits b)
        {
            _books = new Codebook[b.Read(8) + 1];
            for (int i = 0; i < _books.Length; i++) _books[i] = ReadCodebook(ref b, _what);
            int times = b.Read(6) + 1;
            for (int i = 0; i < times; i++) if (b.Read(16) != 0) throw new NotSupportedException($"{_what}: time type");
            _floors = new Floor1[b.Read(6) + 1];
            for (int i = 0; i < _floors.Length; i++)
            {
                int type = b.Read(16);
                if (type != 1) throw new NotSupportedException($"{_what}: floor type {type} (only floor 1 ships: STOP)");
                _floors[i] = ReadFloor1(ref b, _books, _what);
            }
            _residues = new Residue2[b.Read(6) + 1];
            for (int i = 0; i < _residues.Length; i++) _residues[i] = ReadResidue(ref b, _books, _what);
            int maps = b.Read(6) + 1;
            _mapFloor = new int[maps]; _mapResidue = new int[maps];
            var coupling = new (int Mag, int Ang)[maps];
            for (int i = 0; i < maps; i++)
            {
                if (b.Read(16) != 0) throw new NotSupportedException($"{_what}: mapping type");
                int submaps = b.Read(1) != 0 ? b.Read(4) + 1 : 1;
                int steps = b.Read(1) != 0 ? b.Read(8) + 1 : 0;
                if (submaps != 1 || steps > 1 || (steps == 0 && Channels != 1))
                    throw new NotSupportedException($"{_what}: mapping with {submaps} submaps / {steps} coupling steps (shipped: 1 / 1: STOP)");
                coupling[i] = (-1, -1);
                if (steps == 1) coupling[i] = (b.Read(ILog((uint)(Channels - 1))), b.Read(ILog((uint)(Channels - 1))));
                if (b.Read(2) != 0) throw new NotSupportedException($"{_what}: mapping reserved bits");
                b.Read(8);
                _mapFloor[i] = b.Read(8); _mapResidue[i] = b.Read(8);
                if (i > 0 && coupling[i] != coupling[0]) throw new NotSupportedException($"{_what}: mappings couple differently (STOP)");
            }
            (_couplingMag, _couplingAng) = coupling[0]; _coupled = _couplingMag >= 0;
            _modes = new (bool, int)[b.Read(6) + 1];
            for (int i = 0; i < _modes.Length; i++)
            {
                bool blockflag = b.Read(1) != 0;
                if (b.Read(16) != 0 || b.Read(16) != 0) throw new NotSupportedException($"{_what}: window/transform type");
                _modes[i] = (blockflag, b.Read(8));
            }
            if (b.Read(1) != 1) throw new NotSupportedException($"{_what}: setup framing bit");
        }

        // ── Decode state (vorbis_dsp_state as vorbis_synthesis_init 0x10003280 leaves it) ──────────────────────────────
        float[][] _pcm; int _pcmStorage, _pcmCurrent, _pcmReturned, _centerW, _lW, _W; long _granulepos, _sequence;
        int _next;   // next packet index (ogg_stream_packetout order; packetno = index)
        readonly float[][] _block = new float[2][];

        // The new lap as ov_time_seek(vf, 0.0) gives it (the judge: identical to the first lap for every shipped file).
        public void Restart()
        {
            _pcmStorage = 8192;
            _pcm = new float[Channels][];
            for (int i = 0; i < Channels; i++) _pcm[i] = new float[_pcmStorage];
            _lW = _W = 0; _centerW = _pcmCurrent = _bs[1] / 2;
            _pcmReturned = -1; _granulepos = -1; _sequence = -1;
            _next = 0;
        }

        // ov_read [vorbisfile 0x10002e10] for 16-bit signed little-endian into dst (interleaved, count samples): reads
        // packets until PCM is ready; 0 = the end of the lap (Core then seeks to 0.0 for a looping stream).
        public int Read(short[] dst, int offset, int count)
        {
            int frames = count / Channels;
            if (frames <= 0) return 0;
            while (_pcmReturned < 0 || _centerW - _pcmReturned <= 0)
            {
                if (_next >= _packets.Count) return 0;
                DecodePacket(_next++);
            }
            int ready = Math.Min(_centerW - _pcmReturned, frames);
            for (int c = 0; c < Channels; c++)
            {
                float[] src = _pcm[c];
                for (int j = 0, d = offset + c; j < ready; j++, d += Channels)
                {
                    // fld; fmul 32768.0f; fstp qword; fld qword; fistp dword (round to nearest even); clamp. An x87 fistp
                    // out of range (or NaN) stores 0x80000000, which the clamp turns into -32768.
                    double v = src[_pcmReturned + j] * 32768.0;
                    int val = Math.Abs(v) < 2147483648.0 ? (int)Math.Round(v, MidpointRounding.ToEven) : int.MinValue;
                    dst[d] = (short)(val > 32767 ? 32767 : val < -32768 ? -32768 : val);
                }
            }
            _pcmReturned += ready;   // vorbis_synthesis_read
            return ready * Channels;
        }

        // vorbis_synthesis 0x10013160 + mapping0_inverse 0x1000a050 + vorbis_synthesis_blockin 0x100032b0. A packet the
        // DLL rejects (not an audio packet: the headers when decoding starts at offset 0) is skipped as vorbisfile does.
        void DecodePacket(int index)
        {
            var p = _packets[index];
            var b = new Bits(_data, p.Start, p.Bytes);
            if (b.Read(1) != 0) return;                       // OV_ENOTAUDIO (also a 0-byte packet: read answers -1)
            int mode = b.Read(ILog((uint)(_modes.Length - 1)));
            if (mode == -1) return;                           // OV_EBADPACKET
            if (mode >= _modes.Length) throw new NotSupportedException($"{_what}: packet {index} mode {mode} (STOP)");
            int W = _modes[mode].Long ? 1 : 0, lW = 0, nW = 0;
            if (W == 1)
            {
                lW = b.Read(1); nW = b.Read(1);
                if (nW == -1) return;
                if (lW == -1) throw new NotSupportedException($"{_what}: packet {index} lW past the end (STOP)");
            }
            long sequence = index - 3;                        // vb->sequence = op->packetno - 3
            int n = _bs[W], map = _modes[mode].Mapping;

            // mapping0_inverse
            for (int c = 0; c < 2; c++) if (_block[c] is null || _block[c].Length < _bs[1]) _block[c] = new float[_bs[1]];
            var pcm = new float[Channels][];
            for (int c = 0; c < Channels; c++) pcm[c] = c < 2 ? _block[c] : new float[_bs[1]];
            var fits = new int[Channels][];
            var nonzero = new bool[Channels];
            Floor1 floor = _floors[_mapFloor[map]];
            for (int c = 0; c < Channels; c++)
            {
                fits[c] = Floor1Decode(floor, _books, ref b);
                nonzero[c] = fits[c] != null;
                Array.Clear(pcm[c], 0, n / 2);
            }
            if (_coupled && (nonzero[_couplingMag] || nonzero[_couplingAng])) nonzero[_couplingMag] = nonzero[_couplingAng] = true;
            Residue2Decode(_residues[_mapResidue[map]], _books, ref b, pcm, nonzero, Channels);
            if (_coupled)
            {
                float[] M = pcm[_couplingMag], A = pcm[_couplingAng];
                for (int j = 0; j < n / 2; j++)
                {
                    float mag = M[j], ang = A[j];
                    if (mag > 0)
                    {
                        if (ang > 0) A[j] = mag - ang; else { A[j] = mag; M[j] = mag + ang; }
                    }
                    else
                    {
                        if (ang > 0) A[j] = mag + ang; else { A[j] = mag; M[j] = mag - ang; }
                    }
                }
            }
            for (int c = 0; c < Channels; c++)
                if (fits[c] != null) Floor1Apply(floor, fits[c], pcm[c], n / 2); else Array.Clear(pcm[c], 0, n / 2);
            for (int c = 0; c < Channels; c++) _mdct[W].Backward(pcm[c]);
            float[] window = _windows[(W * 2 + lW) * 2 + nW];
            for (int c = 0; c < Channels; c++)
                if (nonzero[c]) for (int j = 0; j < n; j++) pcm[c][j] = window[j] * pcm[c][j];
                else Array.Clear(pcm[c], 0, n);

            BlockIn(pcm, W, sequence, p.GranulePos, p.Eos);
        }

        void BlockIn(float[][] block, int vbW, long vbSequence, long vbGranulepos, bool eof)
        {
            int bs1 = _bs[1];
            if (_centerW > bs1 / 2 && _pcmReturned > 8192)
            {
                int shift = Math.Min(_pcmReturned, _centerW - bs1 / 2);
                _pcmCurrent -= shift; _centerW -= shift; _pcmReturned -= shift;
                if (shift != 0) for (int c = 0; c < Channels; c++) Array.Copy(_pcm[c], shift, _pcm[c], 0, _pcmCurrent);
            }
            _lW = _W; _W = vbW;
            if (_sequence + 1 != vbSequence) _granulepos = -1;
            _sequence = vbSequence;
            int sizeW = _bs[_W], centerW = _centerW + _bs[_lW] / 4 + sizeW / 4, beginW = centerW - sizeW / 2, endW = beginW + sizeW;
            if (endW > _pcmStorage)
            {
                _pcmStorage = endW + bs1;
                for (int c = 0; c < Channels; c++) Array.Resize(ref _pcm[c], _pcmStorage);
            }
            int beginSl, endSl;
            if (_W == 0) { beginSl = 0; endSl = _bs[0] / 2; }
            else { beginSl = bs1 / 4 - _bs[_lW] / 4; endSl = beginSl + _bs[_lW] / 2; }
            for (int c = 0; c < Channels; c++)
            {
                float[] dst = _pcm[c], src = block[c];
                int i = beginSl;
                for (; i < endSl; i++) dst[beginW + i] = src[i] + dst[beginW + i];
                for (; i < sizeW; i++) dst[beginW + i] = src[i];
            }
            if (_pcmReturned == -1) _pcmReturned = centerW;
            if (_granulepos == -1) _granulepos = vbGranulepos == -1 ? 0 : vbGranulepos;
            else
            {
                _granulepos += centerW - _centerW;
                if (vbGranulepos != -1 && _granulepos != vbGranulepos)
                {
                    if (_granulepos > vbGranulepos)
                    {
                        int extra = (int)(_granulepos - vbGranulepos);   // low dword (sub eax, [ebp+0x30])
                        if (eof) centerW -= extra;
                        else if (vbSequence == 1)
                        {
                            _pcmReturned += extra;
                            if (_pcmReturned > centerW) _pcmReturned = centerW;
                        }
                    }
                    _granulepos = vbGranulepos;
                }
            }
            _centerW = centerW;
            _pcmCurrent = endW;
        }
    }
}
