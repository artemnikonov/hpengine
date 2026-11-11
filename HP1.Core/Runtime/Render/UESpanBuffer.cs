// RENDER FSpanBuffer: port of Render.dll bodies (D-56, OccludeBsp occlusion).
//
// Shipped layout (0x20 bytes): +0 StartY, +4 EndY, +8 ValidLines (number of spans, not rows), +0xc Index
// (FSpan* per row StartY..EndY-1), +0x10 FMemStack, +0x14.. FMemMark (Release rewinds the stack to it).
// FSpan (0xc): +0 Start, +4 End, +8 Next: sorted list of free (not yet covered) spans of a row.
// FRasterSpan (8): X[0], X[1]: polygon raster per row.
//
// Span nodes are shared objects, like shared memory in shipped: CopyIndexFrom copies only row heads, and
// CopyFromRasterUpdate, by changing the source list, changes it for every buffer sharing the nodes. Memory
// (FMemStack, marks, Release) is allocation without behaviour: Release does nothing; a buffer with an Arena takes its
// nodes, row-head arrays and new buffers from it (UESpanArena — the port's stand-in for GDynMem, render scheduling
// only: the walk reuses them after the arena's Reset, values are as freshly allocated).
//
// Bodies (Render.dll addresses, disassembly):
//   AllocIndex            0x10b2e3a0 (export 0x11f9)    AllocIndexForScreen 0x11fe
//   CopyIndexFrom         export 0x119a                 CopyFromRaster      export 0x109b (0x10b2eb..)
//   CopyFromRasterUpdate  0x10b2e830..0x10b2eb44        MergeWith           export 0x11c7 (0x10b2ee..)
//   BoxIsVisible          0x10b2e7c0..0x10b2e829
// Judge: se_harness/bsp_diff/span_oracle.py (real bodies under unicorn) and SelfTest span_buffer_matches_shipped.
namespace HP1.UEScript
{
    public sealed class UESpan
    {
        public int Start, End;
        public UESpan Next;
    }

    // Per frame tree arena (port, allocation only): UEOcclusion resets it when a master frame's walk starts — nothing
    // reads the spans of an earlier tree. Every object it hands out is in the state `new` would give.
    public sealed class UESpanArena
    {
        readonly System.Collections.Generic.List<UESpan> _spans = new();
        readonly System.Collections.Generic.List<UESpanBuffer> _bufs = new();
        sealed class Bucket { public readonly System.Collections.Generic.List<UESpan[]> L = new(); public int N; }
        readonly System.Collections.Generic.Dictionary<int, Bucket> _index = new();
        int _nSpans, _nBufs;

        public void Reset()
        {
            _nSpans = 0; _nBufs = 0;
            foreach (Bucket b in _index.Values) b.N = 0;
        }

        public UESpan Span(int start, int end, UESpan next)
        {
            UESpan n;
            if (_nSpans < _spans.Count) n = _spans[_nSpans];
            else _spans.Add(n = new UESpan());
            _nSpans++;
            n.Start = start; n.End = end; n.Next = next;
            return n;
        }

        public UESpanBuffer Buffer()
        {
            UESpanBuffer b;
            if (_nBufs < _bufs.Count) b = _bufs[_nBufs];
            else _bufs.Add(b = new UESpanBuffer());
            _nBufs++;
            b.StartY = b.EndY = b.ValidLines = 0; b.Index = System.Array.Empty<UESpan>(); b.Arena = this;
            return b;
        }

        public UESpan[] Index(int length)
        {
            if (length == 0) return System.Array.Empty<UESpan>();
            if (!_index.TryGetValue(length, out Bucket k)) _index[length] = k = new Bucket();
            UESpan[] a;
            if (k.N < k.L.Count) { a = k.L[k.N]; System.Array.Clear(a); }
            else k.L.Add(a = new UESpan[length]);
            k.N++;
            return a;
        }
    }

    public sealed class UESpanBuffer
    {
        public int StartY, EndY, ValidLines;
        public UESpan[] Index = System.Array.Empty<UESpan>();
        public UESpanArena Arena;                        // port: null — plain allocation

        UESpan NewSpan(int start, int end, UESpan next = null)
            => Arena is not null ? Arena.Span(start, end, next) : new UESpan { Start = start, End = end, Next = next };
        UESpan[] NewIndex(int length) => Arena is not null ? Arena.Index(length) : new UESpan[length];
        // A new buffer from the same arena (or a plain one).
        public UESpanBuffer Sibling() => Arena is not null ? Arena.Buffer() : new UESpanBuffer();

        public UESpan Line(int y) => Index[y - StartY];

        // AllocIndex(StartY, EndY): rows are not filled (shipped leaves garbage; the caller writes them).
        public void AllocIndex(int startY, int endY)
        {
            StartY = startY; EndY = endY; ValidLines = 0;
            Index = endY < startY ? System.Array.Empty<UESpan>() : NewIndex(endY - startY);
        }

        // AllocIndexForScreen(SizeX, SizeY): StartY = 0, EndY = ValidLines = SizeY, one span [0, SizeX) per row.
        public void AllocIndexForScreen(int sizeX, int sizeY)
        {
            StartY = 0; EndY = sizeY; ValidLines = sizeY;
            Index = NewIndex(sizeY);
            for (int i = 0; i < sizeY; i++) Index[i] = NewSpan(0, sizeX);
        }

        // CopyIndexFrom: StartY/EndY and the source row heads; ValidLines untouched.
        public void CopyIndexFrom(UESpanBuffer src)
        {
            StartY = src.StartY; EndY = src.EndY;
            UESpan[] idx = NewIndex(src.Index.Length);
            System.Array.Copy(src.Index, idx, idx.Length);
            Index = idx;
        }

        // Rows of this outside [max(src.StartY, y0), min(src.EndY, y1)) are cleared; inside, the raster is
        // intersected with the source's free spans, source unchanged. Returns 1 if at least one span was created.
        public bool CopyFromRaster(UESpanBuffer src, int y0, int y1, (int X0, int X1)[] raster)
        {
            bool any = false;
            int from = src.StartY > y0 ? src.StartY : y0;
            int to = y1 > src.EndY ? src.EndY : y1;
            if (to <= from)
            {
                for (int i = 0; i < EndY - StartY; i++) Index[i] = null;
                return false;
            }
            for (int y = StartY; y < from; y++) Index[y - StartY] = null;
            for (int y = from; y < to; y++)
            {
                (int x0, int x1) = raster[y - y0];
                UESpan s = src.Index[y - src.StartY];
                UESpan head = null, tail = null;
                void Add(int a, int b)
                {
                    var n = NewSpan(a, b);
                    if (tail is null) head = n; else tail.Next = n;
                    tail = n; ValidLines++; any = true;
                }
                if (s is not null && x0 < x1)
                {
                    while (s is not null && !(x0 < s.End)) s = s.Next;
                    if (s is not null)
                    {
                        if (x0 > s.Start)
                        {
                            Add(x0, x1 <= s.End ? x1 : s.End);
                            s = s.Next;
                        }
                        while (s is not null)
                        {
                            if (x1 < s.End)
                            {
                                if (s.Start < x1) Add(s.Start, x1);
                                break;
                            }
                            Add(s.Start, s.End);
                            s = s.Next;
                        }
                    }
                }
                Index[y - StartY] = head;
            }
            for (int y = to; y < EndY; y++) Index[y - StartY] = null;
            return any;
        }

        // Same, and the raster-covered part is subtracted from the source (the polygon covers part of the zone screen).
        public bool CopyFromRasterUpdate(UESpanBuffer src, int y0, int y1, (int X0, int X1)[] raster)
        {
            if (y0 < StartY || EndY < y1)
                throw new System.InvalidOperationException($"FSpanBuffer::CopyFromRasterUpdate: Illegal span range <{StartY},{EndY}> <{y0},{y1}>");
            bool any = false;
            int from = src.StartY > y0 ? src.StartY : y0;
            int to = y1 > src.EndY ? src.EndY : y1;
            if (to <= from)
            {
                for (int i = 0; i < EndY - StartY; i++) Index[i] = null;
                return false;
            }
            for (int y = StartY; y < from; y++) Index[y - StartY] = null;
            for (int y = from; y < to; y++)
            {
                (int x0, int x1) = raster[y - y0];
                int li = y - src.StartY;
                UESpan head = null, tail = null;
                void Add(int a, int b)
                {
                    var n = NewSpan(a, b);
                    if (tail is null) head = n; else tail.Next = n;
                    tail = n; ValidLines++; any = true;
                }
                // link: source row head or Next of the previous node (pointer-to-pointer in shipped).
                UESpan prev = null;
                UESpan s = src.Index[li];
                void Unlink(UESpan node) { if (prev is null) src.Index[li] = node.Next; else prev.Next = node.Next; }
                if (s is not null && x0 < x1)
                {
                    while (s is not null && s.End <= x0) { prev = s; s = s.Next; }
                    if (s is not null)
                    {
                        bool done = false;
                        if (s.Start < x0)
                        {
                            Add(x0, x1 < s.End ? x1 : s.End);
                            if (s.End > x1)
                            {
                                var m = NewSpan(x1, s.End, s.Next);
                                s.Next = m; s.End = x0; src.ValidLines++;
                                done = true;
                            }
                            else
                            {
                                s.End = x0; prev = s; s = s.Next;
                                if (s is null) done = true;
                            }
                        }
                        while (!done)
                        {
                            if (s.End <= x1)
                            {
                                Add(s.Start, s.End);
                                Unlink(s); src.ValidLines--;
                                s = s.Next;
                                if (s is null) break;
                            }
                            else
                            {
                                if (s.Start < x1) { Add(s.Start, x1); s.Start = x1; }
                                break;
                            }
                        }
                    }
                }
                Index[y - StartY] = head;
            }
            for (int y = to; y < EndY; y++) Index[y - StartY] = null;
            return any;
        }

        // Union: the row range widens to the union (new rows empty), source spans merge into this's lists
        // joining overlaps/touches; ValidLines += new - absorbed.
        public void MergeWith(UESpanBuffer src)
        {
            if (src.StartY < StartY || src.EndY > EndY)
            {
                int ns = src.StartY < StartY ? src.StartY : StartY;
                int ne = EndY < src.EndY ? src.EndY : EndY;
                var idx = NewIndex(ne - ns);
                for (int y = StartY; y < EndY; y++) idx[y - ns] = Index[y - StartY];
                StartY = ns; EndY = ne; Index = idx;
            }
            for (int y = src.StartY; y < src.EndY; y++)
            {
                UESpan b = src.Index[y - src.StartY];
                UESpan a = Index[y - StartY];
                UESpan head = null, tail = null;
                void Link(UESpan n) { if (tail is null) head = n; else tail.Next = n; tail = n; }
                while (a is not null)
                {
                    if (b is null) break;
                    if (b.End < a.Start)
                    {
                        Link(NewSpan(b.Start, b.End));
                        b = b.Next; ValidLines++;
                    }
                    else if (a.End < b.Start)
                    {
                        Link(a); a = a.Next;
                    }
                    else
                    {
                        Link(a);
                        if (b.Start < a.Start) a.Start = b.Start;
                        if (a.End < b.End) a.End = b.End;
                        UESpan nxt = a.Next;
                        while (true)
                        {
                            b = b.Next;
                            while (nxt is not null && nxt.Start <= a.End)
                            {
                                if (a.End < nxt.End) a.End = nxt.End;
                                nxt = nxt.Next; ValidLines--;
                            }
                            if (b is null || a.End < b.Start) break;
                            if (a.End < b.End) a.End = b.End;
                        }
                        a = nxt;
                    }
                }
                if (a is not null) { Link(a); }
                else
                    while (b is not null)
                    {
                        Link(NewSpan(b.Start, b.End));
                        b = b.Next; ValidLines++;
                    }
                if (tail is not null && a is null) tail.Next = null;
                Index[y - StartY] = head;
            }
        }

        // Rectangle [x1, x2) x [y1, y2) touches at least one free span.
        public bool BoxIsVisible(int x1, int y1, int x2, int y2)
        {
            if (y1 >= EndY || y2 <= StartY) return false;
            if (y1 < StartY) y1 = StartY;
            if (y2 > EndY) y2 = EndY;
            for (int y = y1; y < y2; y++)
                for (UESpan s = Index[y - StartY]; s is not null; s = s.Next)
                {
                    if (x2 <= s.Start) break;
                    if (x1 < s.End) return true;
                }
            return false;
        }
    }
}
