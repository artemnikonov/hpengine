// InstallShield cabinets (data1.hdr + data1.cab, data2.cab, ...; InstallShield 5 and later): writes the files of every
// file group as <out>/<group>/<directory>/<name>, the layout of `unshield x` (src/unshield.c extract_file: in group and
// directory names ' ' < > [ ] and unprintable characters become '_', in file names unprintable ones). A port of the
// reading part of unshield 1.6.2 (MIT, https://github.com/twogood/unshield; lib/libunshield.c, lib/file.c,
// lib/file_group.c, lib/helper.c): headers, file groups, both file descriptor layouts (major version <= 5 and >= 6),
// volumes and files split across them, obfuscation, 64 KiB raw-deflate chunks, the MD5 check (version >= 6, as
// unshield). Not ported: unshield's "old" compression and files stored outside the cabinet (both fail loudly here).
// Unlike unshield, two different files for one output path are refused instead of the later one overwriting.
// Checked against unshield itself on the US CD's setup/data1.cab (launcher/test_setup.sh, is_cab_vs_unshield).
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

static class InstallShield
{
    const uint Signature = 0x28635349;   // "ISc("
    const ushort Split = 1, Obfuscated = 2, Compressed = 4, Invalid = 8;
    const byte LinkPrev = 1;

    sealed class Fd
    {
        public uint NameOffset, DirIndex, LinkPrevious;
        public ushort Flags;
        public ulong Expanded, CompressedSize, DataOffset;
        public byte[] Md5 = new byte[16];
        public int Volume;
        public byte LinkFlags;
    }

    // `first`: the <prefix>1.hdr, or <prefix>1.cab of a cabinet without a separate header. Returns the file count.
    public static int Extract(string first, string outDir)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(first))!, name = Path.GetFileName(first);
        int digit = name.IndexOfAny("0123456789.".ToCharArray());   // unshield_create_filename_pattern
        string prefix = digit < 0 ? name : name[..digit];
        string Volume(int i, string suffix) => FileCI(dir, $"{prefix}{i}.{suffix}");
        string hdr = Volume(1, "hdr") ?? Volume(1, "cab") ?? throw new InvalidDataException($"no {prefix}1.hdr or {prefix}1.cab");
        byte[] h = File.ReadAllBytes(hdr);
        if (h.Length < 20 || U32(h, 0) != Signature) throw new InvalidDataException($"{Path.GetFileName(hdr)} is not an InstallShield cabinet");
        uint version = U32(h, 4), descOffset = U32(h, 12), descSize = U32(h, 16);
        int major = version >> 24 == 1 ? (int)((version >> 12) & 0xf)
            : version >> 24 is 2 or 4 ? (int)(version & 0xffff) / 100 : 0;
        if (descSize == 0) throw new InvalidDataException("no cabinet descriptor");
        int d = (int)descOffset;
        uint fto = U32(h, d + 0xc), dirCount = U32(h, d + 0x1c), fileCount = U32(h, d + 0x28), fto2 = U32(h, d + 0x2c);
        int table = d + (int)fto;
        string Str(long at)
        {
            int p = checked((int)at);
            if (major >= 17)
            {
                int e = p; while (h[e] != 0 || h[e + 1] != 0) e += 2;
                return Encoding.Unicode.GetString(h, p, e - p);
            }
            int z = Array.IndexOf(h, (byte)0, p);
            return Encoding.Latin1.GetString(h, p, z - p);
        }
        uint Table(long i) => U32(h, table + (int)(i * 4));

        Fd Read(int i)
        {
            var f = new Fd();
            if (major is 0 or 5)
            {
                int p = table + (int)Table(dirCount + i);
                f.Volume = 1;
                f.NameOffset = U32(h, p); f.DirIndex = U32(h, p + 4); f.Flags = U16(h, p + 8);
                f.Expanded = U32(h, p + 10); f.CompressedSize = U32(h, p + 14); f.DataOffset = U32(h, p + 0x26);
                if (major == 5) Array.Copy(h, p + 0x2a, f.Md5, 0, 16);
            }
            else
            {
                int p = table + (int)fto2 + i * 0x57;
                f.Flags = U16(h, p); f.Expanded = U64(h, p + 2); f.CompressedSize = U64(h, p + 10);
                f.DataOffset = U64(h, p + 18); Array.Copy(h, p + 26, f.Md5, 0, 16);
                f.NameOffset = U32(h, p + 0x3a); f.DirIndex = U16(h, p + 0x3e);
                f.LinkPrevious = U32(h, p + 0x4c); f.LinkFlags = h[p + 0x54]; f.Volume = U16(h, p + 0x55);
            }
            return f;
        }

        var fds = new Fd[fileCount];
        for (int i = 0; i < fileCount; i++) fds[i] = Read(i);
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int count = 0;
        for (int g = 0; g < 71; g++)   // MAX_FILE_GROUP_COUNT offsets at descriptor + 0x3e, each a linked list
            for (uint next = U32(h, d + 0x3e + g * 4); next != 0;)
            {
                int list = d + (int)next, desc = d + (int)U32(h, list + 4);
                next = U32(h, list + 8);
                string group = Str(d + (long)U32(h, desc));
                int at = desc + 4 + (major <= 5 ? 0x48 : 0x12);
                uint firstFile = U32(h, at), lastFile = U32(h, at + 4);
                for (uint i = firstFile; i <= lastFile && i < fileCount; i++)
                {
                    var f = fds[i];
                    if ((f.Flags & Invalid) != 0 || f.NameOffset == 0 || f.DataOffset == 0) continue;   // unshield_file_is_valid
                    string rel = SafeRelative(group, Str(table + (long)Table(f.DirIndex)), Str(table + (long)f.NameOffset));
                    int src = (f.LinkFlags & LinkPrev) != 0 ? (int)f.LinkPrevious : (int)i;
                    string target = Path.Combine(outDir, rel), tmp = target + ".hp1part";
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    Save(src, fds[src], tmp);
                    if (!written.Add(rel))
                    {
                        bool same = File.ReadAllBytes(tmp).AsSpan().SequenceEqual(File.ReadAllBytes(target));
                        File.Delete(tmp);
                        if (!same) throw new InvalidDataException($"two different files for {rel}");
                        continue;
                    }
                    File.Move(tmp, target);
                    count++;
                }
            }
        return count;

        void Save(int index, Fd f, string path)
        {
            using var r = new Reader(Volume, major, (int)fileCount, index, f);
            using var o = File.Create(path);
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            ulong left = (f.Flags & Compressed) != 0 ? f.CompressedSize : f.Expanded, total = 0;
            var input = new byte[0x10000];
            var output = new byte[0x10000];
            while (left > 0)
            {
                int n;
                if ((f.Flags & Compressed) != 0)
                {
                    r.Read(input, 2);
                    int len = U16(input, 0);
                    if (len == 0) throw new InvalidDataException($"file {index}: empty chunk");
                    r.Read(input, len);
                    using var z = new DeflateStream(new MemoryStream(input, 0, len), CompressionMode.Decompress);
                    n = 0;
                    for (int k; n < output.Length && (k = z.Read(output, n, output.Length - n)) > 0;) n += (int)k;
                    left -= (ulong)(2 + len);
                }
                else
                {
                    n = (int)Math.Min(left, (ulong)output.Length);
                    r.Read(output, n);
                    left -= (ulong)n;
                }
                md5.AppendData(output, 0, n);
                o.Write(output, 0, n);
                total += (ulong)n;
            }
            if (total != f.Expanded) throw new InvalidDataException($"file {index}: {total} bytes, expected {f.Expanded}");
            if (major >= 6 && !md5.GetHashAndReset().AsSpan().SequenceEqual(f.Md5))
                throw new InvalidDataException($"file {index}: MD5 differs");
        }
    }

    // One file's data across volumes (unshield_reader_open_volume / unshield_reader_read).
    sealed class Reader : IDisposable
    {
        readonly Func<int, string, string> _volume;
        readonly int _major, _fileCount, _index;
        readonly Fd _f;
        FileStream _s;
        int _vol;
        ulong _left;
        uint _seed;

        public Reader(Func<int, string, string> volume, int major, int fileCount, int index, Fd f)
        {
            _volume = volume; _major = major; _fileCount = fileCount; _index = index; _f = f;
            for (int v = f.Volume; ; v++)
            {
                uint last = Open(v);
                if (_major <= 5 && index > last) continue;   // IS5: the file starts in a later volume
                break;
            }
            if (_s.Length == (long)f.DataOffset) throw new InvalidDataException($"file {index} is not inside the cabinet");
        }

        uint Open(int v)
        {
            _s?.Dispose();
            string p = _volume(v, "cab") ?? throw new InvalidDataException($"cabinet volume {v} is missing");
            _s = File.OpenRead(p);
            var b = new byte[20 + 64];
            int need = 20 + (_major is 0 or 5 ? 40 : 64);
            if (_s.ReadAtLeast(b, need, throwOnEndOfStream: false) < need || U32(b, 0) != Signature)
                throw new InvalidDataException($"{Path.GetFileName(p)} is not an InstallShield cabinet");
            ulong dataOffset, firstIndex, lastIndex, firstOffset, firstExp, firstComp, lastOffset, lastExp, lastComp;
            if (_major is 0 or 5)
            {
                firstIndex = U32(b, 28); lastIndex = U32(b, 32); firstOffset = U32(b, 36); firstExp = U32(b, 40);
                firstComp = U32(b, 44); lastOffset = U32(b, 48); lastExp = U32(b, 52); lastComp = U32(b, 56);
                if (lastOffset == 0) lastOffset = int.MaxValue;
            }
            else
            {
                firstIndex = U32(b, 28); lastIndex = U32(b, 32); firstOffset = U64(b, 36); firstExp = U64(b, 44);
                firstComp = U64(b, 52); lastOffset = U64(b, 60); lastExp = U64(b, 68); lastComp = U64(b, 76);
            }
            if (_major == 5)
            {
                if ((ulong)_index < (ulong)(_fileCount - 1) && (ulong)_index == lastIndex && lastComp != _f.CompressedSize) _f.Flags |= Split;
                else if (_index > 0 && (ulong)_index == firstIndex && firstComp != _f.CompressedSize) _f.Flags |= Split;
            }
            ulong exp, comp;
            if ((_f.Flags & Split) != 0)
            {
                if ((ulong)_index == lastIndex && lastOffset != 0x7FFFFFFF) { dataOffset = lastOffset; exp = lastExp; comp = lastComp; }
                else if ((ulong)_index == firstIndex) { dataOffset = firstOffset; exp = firstExp; comp = firstComp; }
                else { _vol = v; return (uint)lastIndex; }   // unshield: nothing of this file here, position kept
            }
            else { dataOffset = _f.DataOffset; exp = _f.Expanded; comp = _f.CompressedSize; }
            _left = (_f.Flags & Compressed) != 0 ? comp : exp;
            _s.Seek((long)dataOffset, SeekOrigin.Begin);
            _vol = v;
            return (uint)lastIndex;
        }

        public void Read(byte[] buf, int size)
        {
            int at = 0;
            while (true)
            {
                int n = (int)Math.Min((ulong)(size - at), _left);
                if (n == 0) throw new InvalidDataException($"file {_index}: no data left in volume {_vol}");
                if (_s.ReadAtLeast(buf.AsSpan(at, n), n, throwOnEndOfStream: false) != n)
                    throw new InvalidDataException($"file {_index}: volume {_vol} ends early");
                at += n; _left -= (ulong)n;
                if (at == size) break;
                Open(_vol + 1);
            }
            if ((_f.Flags & Obfuscated) != 0)
                for (int i = 0; i < size; i++, _seed++)   // unshield_deobfuscate
                {
                    int x = buf[i] ^ 0xd5;
                    buf[i] = (byte)((((x >> 2) | (x << 6)) & 0xff) - _seed % 0x47);
                }
        }

        public void Dispose() => _s?.Dispose();
    }

    // <group>/<directory>/<name> as unshield names it, as a relative path inside the output; anything that could
    // leave it is refused.
    static string SafeRelative(string group, string dir, string name)
    {
        static string Clean(string t, bool isDir) => new(t.Select(c => c < 0x20 || c > 0x7e ? '_'
            : isDir && c is ' ' or '<' or '>' or '[' or ']' ? '_' : c).ToArray());
        var parts = (Clean(group, true) + "/" + Clean(dir, true)).Split('\\', '/').Where(p => p.Length > 0)
            .Append(Clean(name, false)).ToArray();
        if (parts.Any(p => p is "" or "." or ".." || p.IndexOfAny(new[] { ':', '\\', '/', '"', '|', '?', '*' }) >= 0))
            throw new InvalidDataException($"unsafe path in the cabinet: {group}/{dir}/{name}");
        return Path.Combine(parts);
    }

    static string FileCI(string dir, string name) =>
        File.Exists(Path.Combine(dir, name)) ? Path.Combine(dir, name)
        : Directory.EnumerateFiles(dir).FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));

    static ushort U16(byte[] b, int at) => BitConverter.ToUInt16(b, at);
    static uint U32(byte[] b, int at) => BitConverter.ToUInt32(b, at);
    static ulong U64(byte[] b, int at) => BitConverter.ToUInt64(b, at);
}
