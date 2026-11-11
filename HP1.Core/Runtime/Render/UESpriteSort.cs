// URender::DrawFrame sprite array on a device without SpanBased (D3DDrv, RenDev+0x48 == 0; Render.dll.c:1098-1107,
// 1547-1576). Core's: the order in which a frame's sprites reach DrawActorSprite (emitter Update order, mesh GetFrame
// order, sprite draw order).
//
//   sort   FUN_10b2afa0 (disasm 0x10b2afa0..0x10b2b17c): UE1 Sort<FDynamicSprite*> — 32-pair (Min,Max) stack; a range of
//          <= 8 by selection (max by Compare > 0, first max wins, swapped to Max, Max--); else the middle element
//          Min[Count/2] swapped to Min, `while (++i <= Max && Compare(*i,*Min) <= 0)`, `while (--j > Min &&
//          Compare(*j,*Min) >= 0)`, swap; Exchange(*Min,*j); the smaller half continues, the larger is pushed
//          ((j-1-Min)*4 < (Max-i)*4 signed, 0x10b2b0b1). Compare = (a->Actor->Class - b->Actor->Class) / 0x4fc (int32
//          subtraction, imul 0x335c49d5 / sar 8 / +sign: truncation toward zero).
//   key    the UClass object ADDRESS in the original — run-dependent heap order (audit/captures_2026-11-02_spritesort).
//          NOT ORIGINAL (author decision A, DECISIONS.md 2026-11-03): heap classes by the port's class load order
//          (UEClassRegistry.ClassLoadRank) times 0x4fc, DLL-native classes (RF_Native) after them by their static
//          UClass address in Engine.dll (UEClassRegistry.NativeClassAddress).
//   passes 0x10b2666a..0x10b26734: pass 1 draws sprites whose Actor is NULL or has Style (+0x155) != 3 (STY_Translucent)
//          and Opacity (+0x194) >= 1.0 (fcomp qword 0x10b38678, C0 clear: NaN goes to pass 2); pass 2 the rest, each
//          pass in sorted order.
using System;
using System.Collections.Generic;
using HP1.Core;

namespace HP1.UEScript
{
    public static class UESpriteSort
    {
        const int UClassSize = 0x4fc;

        static int Compare(int ka, int kb) => unchecked(ka - kb) / UClassSize;

        // FUN_10b2afa0 in place over a[0..n-1]; key = the UClass "address" of the element's actor.
        public static void Sort<T>(IList<T> a, Func<T, int> key)
        {
            int n = a.Count;
            if (n < 2) return;
            int C(int i, int j) => Compare(key(a[i]), key(a[j]));
            void Swap(int i, int j) { (a[i], a[j]) = (a[j], a[i]); }
            var stLo = new int[32]; var stHi = new int[32];
            stLo[0] = 0; stHi[0] = n - 1;
            int t = 0;
            while (t >= 0)
            {
                int lo = stLo[t], hi = stHi[t];
                t--;
                while (true)
                {
                    if (hi - lo + 1 <= 8)
                    {
                        while (hi > lo)                                   // 0x10b2b104
                        {
                            int best = lo;
                            for (int i = lo + 1; i <= hi; i++) if (C(i, best) > 0) best = i;
                            Swap(best, hi);
                            hi--;
                        }
                        break;
                    }
                    Swap(lo + (hi - lo + 1) / 2, lo);                     // 0x10b2aff6: middle to Min
                    int p = lo, q = hi + 1;
                    while (true)
                    {
                        p++;
                        while (p <= hi && C(p, lo) <= 0) p++;
                        q--;
                        while (q > lo && C(q, lo) >= 0) q--;
                        if (p > q) break;
                        Swap(p, q);
                    }
                    Swap(lo, q);
                    if (q - 1 - lo < hi - p)                              // 0x10b2b0b1 jl: left part smaller
                    {
                        if (hi > p) { t++; stLo[t] = p; stHi[t] = hi; }
                        if (lo + 1 >= q) break;
                        hi = q - 1;
                    }
                    else
                    {
                        if (lo + 1 < q) { t++; stLo[t] = lo; stHi[t] = q - 1; }
                        if (hi <= p) break;
                        lo = p;
                    }
                }
            }
        }

        // DrawFrame pass of a sprite's actor on a non-SpanBased device: 1 or 2 (bVar13 + 1). The one owner: the frame
        // carries it as ActorSnapshot.LatePass, the frontend's group.
        public static int Pass(UEObject actor)
        {
            if (actor is null) return 1;
            int style = UEValue.ToInt(actor.GetProp(PK.Style));
            float opacity = actor.GetProp(PK.Opacity) is { } op ? UEValue.AsFloat(op) : 1f;
            return style != 3 && opacity >= 1f ? 1 : 2;
        }

        // The class key of an actor (see header).
        public static int ClassKey(UEObject actor)
        {
            UEClassRegistry reg = UEClassRegistry.Shared
                ?? throw new InvalidOperationException("DrawFrame sprite sort: no class registry");
            return reg.SpriteSortKey(actor.Class);
        }

        // The frame's sprite array in DrawActorSprite order for a device without SpanBased: sorted, then pass 1, pass 2.
        public static List<T> NonSpanBasedOrder<T>(IReadOnlyList<T> sprites, Func<T, UEObject> actor)
        {
            var arr = new List<T>(sprites);
            var keys = new Dictionary<UEObject, int>();
            int Key(T s)
            {
                UEObject a = actor(s) ?? throw new NotSupportedException("DrawFrame sprite sort: a sprite without a known actor (shipped dereferences Actor->Class)");
                if (!keys.TryGetValue(a, out int k)) keys[a] = k = ClassKey(a);
                return k;
            }
            Sort(arr, Key);
            var order = new List<T>(arr.Count);
            for (int pass = 1; pass <= 2; pass++)
                foreach (T s in arr) if (Pass(actor(s)) == pass) order.Add(s);
            return order;
        }
    }
}
