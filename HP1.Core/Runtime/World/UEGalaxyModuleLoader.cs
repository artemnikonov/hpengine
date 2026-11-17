// GALAXY MODULE LOADER (UseDigitalMusic=True): glxLoadMusic from memory, as far as the result is decided.
//
// UGalaxyAudioSubsystem::Update's module block (0x10609292-0x1060945d) hands the UMusic's Data (Num grown by 0x400 bytes
// of unwritten heap, 0x10609303) to glxLoadMusic(&Mem, GLX_LOADFROMMEMORY) FUN_10616510. Every music the known data
// can give it fails to load: an MP2 song has no module signature and lands in the MOD loader, which refuses it at the
// signature check (0xF); the one IT module a mod carries stores IT 2.14 compressed samples, which the IT loader reads
// raw (sample flag 8 is not read) and refuses with 0x10 at the first sample. So this file ports the dispatch and each
// path up to its result; a module the original would LOAD (and then play through the tracker sequencer FUN_10613040)
// is a loud stop until that player is ported, as are the other loaders and every read the original takes from the
// unwritten padding or from a stale buffer (undefined).
//
// Memory reader (mode 2): FUN_106163d0 seek (0 absolute / 1 relative / 2 from the end, clamped to the size) and
// FUN_10616440 read (count = min(n, (size - pos) / elem), copies count·elem bytes, returns count); tell = pos.
using System;
using System.Text;

namespace HP1.UEScript
{
    public static class UEGalaxyModuleLoader
    {
        public const int Padding = 0x400;   // Update 0x10609303: Data.Num += 0x400 (the bytes are not written)

        // Galaxy module state the load leaves behind and later calls read: DAT_1067d0aa (last order index).
        public sealed class State
        {
            public byte OrderMax;   // DAT_1067d0aa: glxInit 0 (Galaxy.dll.c:8944), reset FUN_1060c100 0 (9090)
        }

        sealed class Mem
        {
            readonly byte[] _d; public readonly int Size; public int Pos;
            public Mem(byte[] d) { _d = d; Size = d.Length + Padding; }
            // Original: Galaxy.dll not exported: FUN_106163d0 [HP1 0x106163D0] (memory seek; set by glxLoadMusic for mode 2)
            public void Seek(long off, int whence)
            {
                long p = whence == 0 ? off : whence == 1 ? Pos + off : Size + off;
                // unsigned compares in the original: a "negative" target is huge and clamps to the size
                Pos = (int)((ulong)p > (ulong)Size ? Size : p);
            }
            // Original: Galaxy.dll not exported: FUN_10616440 [HP1 0x10616440] (memory read)
            // The bytes must lie inside the data: the padding is unwritten heap (undefined), so reading it is a stop.
            public uint Read(byte[] dst, uint elem, uint n, string what)
            {
                uint remain = (uint)(Size - Pos);
                if (remain < elem * n) n = remain / elem;   // header reads: elem·n is small
                uint bytes = elem * n;
                if (Pos + (long)bytes > _d.Length)
                    throw new NotSupportedException($"glxLoadMusic: {what} reads the 0x400 unwritten bytes past the music data (undefined, STOP B27b)");
                if (dst != null) Buffer.BlockCopy(_d, Pos, dst, 0, (int)bytes);
                Pos += (int)bytes;
                return n;
            }
            public byte[] Full(int len, string what)
            {
                var b = new byte[len];
                if (Read(b, 1, (uint)len, what) != len)
                    throw new NotSupportedException($"glxLoadMusic: {what} is short; the original keeps the previous buffer content (undefined, STOP B27b)");
                return b;
            }
        }

        static bool At(byte[] h, int off, string sig) => Encoding.Latin1.GetBytes(sig).AsSpan().SequenceEqual(h.AsSpan(off, sig.Length));

        // Original: Galaxy.dll not exported: FUN_10616510 [HP1 0x10616510] (glxLoadMusic; called from ?Update@UGalaxyAudioSubsystem@@UAEXUFPointRegion@@AAVFCoords@@@Z at 0x10609374)
        // Returns the glx code (0 = loaded). Only mode 2 (memory) is reachable: Update passes 2 (0x1060934b).
        public static int LoadMusic(State st, byte[] data, string path)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            // DAT_1067d0a8 != 0 returns 1 and FUN_1060c100 returns 2 while a module plays: no module ever loads here
            // (a loadable one stops below), so both are 0 and the reset runs.
            st.OrderMax = 0;   // FUN_1060c100 (Galaxy.dll.c:9090)
            var m = new Mem(data);
            byte[] h = m.Full(0x30, $"{path}: the 0x30-byte signature probe");
            m.Seek(-0x30, 1);
            if (At(h, 0, "MThd")) throw Unported(path, "MIDI (FUN_10628af0)");
            if (At(h, 0, "MTM")) throw Unported(path, "MTM (FUN_10628450)");
            if (At(h, 0, "FAR\xfe")) throw Unported(path, "FAR (FUN_10627ed0)");
            if (At(h, 0, "IMPM")) return LoadIt(st, m, path);
            if (At(h, 8, "AM  ")) throw Unported(path, "AM (FUN_106259c0)");
            if (At(h, 0x2c, "SCRM")) throw Unported(path, "S3M (FUN_10624e50)");
            if (At(h, 0x2c, "PTMF")) throw Unported(path, "PTM (FUN_10624660)");
            if (At(h, 0, "MAS_UTrack_V00")) throw Unported(path, "ULT (FUN_10623d90)");
            if (At(h, 0, "Extended Module: ")) throw Unported(path, "XM (FUN_10623300)");
            if (At(h, 0, "if") || At(h, 0, "JN")) throw Unported(path, "669 (FUN_10622170)");
            if (At(h, 0x14, "!Scream!") || At(h, 0x14, "BMOD2STM")) throw Unported(path, "STM (FUN_106226d0)");
            return LoadMod(m, path);
        }

        static NotSupportedException Unported(string path, string what)
            => new NotSupportedException($"glxLoadMusic: {path}: a {what} module — that loader is not ported (STOP B27b)");

        static NotSupportedException Loads(string path, string what)
            => new NotSupportedException($"glxLoadMusic: {path}: the original LOADS this {what} module and plays it through the tracker sequencer FUN_10613040, which is not ported (STOP B27b)");

        // Original: Galaxy.dll not exported: FUN_10622cb0 [HP1 0x10622CB0] (MOD loader, the fallback of glxLoadMusic)
        // Reads the 0x14-byte title, 31 sample headers of 0x1e bytes and the 0x86-byte order block; the tag at
        // +0x82 of that block ("M.K.", "M!K!", "FLT4", "CD81", "?CHN", "??CH") decides; none: 0xF before any global
        // is written.
        static int LoadMod(Mem m, string path)
        {
            m.Full(0x14, $"{path}: MOD title");
            m.Full(0x1e * 0x1f, $"{path}: MOD sample headers");
            byte[] o = m.Full(0x86, $"{path}: MOD order block");
            bool tag = At(o, 0x82, "M.K.") || At(o, 0x82, "M!K!") || At(o, 0x82, "FLT4") || At(o, 0x82, "CD81") || At(o, 0x83, "CHN");
            if (At(o, 0x84, "CH")) tag = true;
            if (!tag) return 0xF;
            throw Loads(path, "MOD");
        }

        // Original: Galaxy.dll not exported: FUN_10626350 [HP1 0x10626350] (IT loader)
        // Original: Galaxy.dll not exported: FUN_1062d4d0 [HP1 0x1062D4D0] (sample data read: 0xB alloc, 0x10 short read)
        static int LoadIt(State st, Mem m, string path)
        {
            int baseOff = m.Pos;   // PTR_FUN_106461bc (tell)
            byte[] h = m.Full(0xc0, $"{path}: IT header");
            ushort ordNum = BitConverter.ToUInt16(h, 0x20), insNum = BitConverter.ToUInt16(h, 0x22);
            ushort smpNum = BitConverter.ToUInt16(h, 0x24), patNum = BitConverter.ToUInt16(h, 0x26), flags = BitConverter.ToUInt16(h, 0x2c);
            byte orders = (byte)(ordNum < 0x100 ? ordNum : 0xff);
            byte[] ord = m.Full(orders, $"{path}: IT orders");
            m.Seek(ordNum - orders, 1);
            // DAT_10668fc0 / DAT_10669780 / DAT_10669300 hold 128 / 256 / 256 offsets before the next global: more
            // overwrite other Galaxy state.
            if (insNum > 128 || smpNum > 256 || patNum > 256)
                throw new NotSupportedException($"glxLoadMusic: {path}: IT counts ins {insNum} smp {smpNum} pat {patNum} overflow Galaxy's offset tables (STOP B27b)");
            uint[] ins = Offsets(m, insNum, $"{path}: IT instrument offsets");
            uint[] smp = Offsets(m, smpNum, $"{path}: IT sample offsets");
            Offsets(m, patNum, $"{path}: IT pattern offsets");
            // DAT_1067d0aa = min(orders, index of the first 0xFF order) - 1 (byte). DAT_1067cf80 beyond the orders read
            // keeps older content, but it is only reached when the read orders hold no 0xFF, and then the minimum is
            // the count read.
            int firstEnd = Array.IndexOf(ord, (byte)0xff);
            st.OrderMax = (byte)((firstEnd >= 0 ? firstEnd : orders) - 1);
            byte[] sh = new byte[0x50];
            if ((flags & 4) == 0)
            {
                for (int i = 0; i < smpNum; i++)
                {
                    m.Seek(baseOff + smp[i], 0);
                    sh = m.Full(0x50, $"{path}: IT sample header {i}");
                    if (!At(sh, 0, "IMPS")) continue;
                    m.Seek(baseOff + BitConverter.ToUInt32(sh, 0x48), 0);
                    int r = SampleData(m, sh, $"{path}: IT sample {i}");
                    if (r != 0) return r;
                }
            }
            else
            {
                for (int i = 0; i < insNum; i++)
                {
                    m.Seek(baseOff + ins[i], 0);
                    byte[] ih = m.Full(0x226, $"{path}: IT instrument {i}");
                    if (!At(ih, 0, "IMPI")) continue;
                    // the keyboard's samples in first-use order (DAT_10669200 map, 0x10626b97..)
                    var order = new System.Collections.Generic.List<int>();
                    for (int k = 0; k < 0x78; k++)
                    {
                        int s = ih[0x41 + 2 * k];
                        if (s != 0 && !order.Contains(s)) order.Add(s);
                    }
                    foreach (int s in order)
                    {
                        if (s > smpNum)
                            throw new NotSupportedException($"glxLoadMusic: {path}: IT instrument {i} uses sample {s} of {smpNum}: the offset table entry is older Galaxy state (STOP B27b)");
                        m.Seek(baseOff + smp[s - 1], 0);
                        sh = m.Full(0x50, $"{path}: IT sample header {s}");
                        if (BitConverter.ToUInt32(sh, 0x30) == 0) continue;   // no IMPS check in this mode
                        m.Seek(baseOff + BitConverter.ToUInt32(sh, 0x48), 0);
                        int r = SampleData(m, sh, $"{path}: IT sample {s}");
                        if (r != 0) return r;
                    }
                }
            }
            throw Loads(path, "IT");
        }

        static uint[] Offsets(Mem m, int n, string what)
        {
            byte[] b = m.Full(4 * n, what);
            var r = new uint[n];
            for (int i = 0; i < n; i++) r[i] = BitConverter.ToUInt32(b, 4 * i);
            return r;
        }

        // FUN_1062d4d0: width = 2 for a 16-bit sample (IMPS flag 2 -> Galaxy flag 4; flag 8 "compressed" is never read),
        // alloc (len + 0x1f)·width (32-bit; 0xB if malloc fails), read len elements of width: fewer -> 0x10. Length 0
        // reads nothing. The bytes read do not decide anything here (the padding may be among them).
        static int SampleData(Mem m, byte[] sh, string what)
        {
            uint len = BitConverter.ToUInt32(sh, 0x30);
            if (len == 0) return 0;
            uint width = (sh[0x12] & 2) != 0 ? 2u : 1u;
            uint alloc = unchecked((len + 0x1f) * width);
            // malloc's answer for a large block depends on the process's address space, not on the source.
            if (alloc > 64u << 20)
                throw new NotSupportedException($"glxLoadMusic: {what}: a {alloc}-byte sample buffer — whether malloc gives it (0xB) is not decided by the source (STOP B27b)");
            uint remain = (uint)(m.Size - m.Pos);
            uint got = remain < unchecked(width * len) ? remain / width : len;   // FUN_10616440: 32-bit product
            m.Pos += (int)unchecked(width * got);
            return got != len ? 0x10 : 0;
        }

        // Original: Galaxy.dll not exported: FUN_1060a940 [HP1 0x1060A940] (glxControlMusic; GLX_SETPOSITION = 1)
        // Section <= DAT_1067d0aa: 0 (DAT_1067cf61 = section - 1, DAT_1067ca08 = 1 and the mixer budget DAT_1067ca3c = 0,
        // which only cuts a mixer call running on the timer thread at that moment: wall clock, not modelled); else 0x11.
        public static int SetPosition(State st, int section) => section <= st.OrderMax ? 0 : 0x11;
    }
}
