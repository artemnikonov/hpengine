using System;
using System.Collections.Generic;

namespace HP1.Core
{
    // MSVCRT qsort, which shipped uses to sort trace hits: appQsort (Core.dll.c:18592) is a direct call to
    // msvcrt.dll qsort. The order of EQUAL elements is deterministic but not stable, and it is visible to script
    // (ULevel::MultiLineCheck -> FindBase, TraceActors).
    //
    // REFERENCE: the msvcrt.dll the live 1.53 original ran with (CrossOver XP bottle, syswow64): qsort -> qsort_s
    // with a comparator thunk (0x100561c0 -> 0x100561f0). The port is checked against real execution of that DLL
    // under unicorn (se_harness/bsp_diff/validate_msvcrt_qsort.py) on random arrays with ties. Algorithm:
    // median-of-three partitioning with an explicit stack; a run shorter than 8 uses selection sort that moves
    // the maximum to the end (an equal element does not displace an earlier maximum).
    public static class MsvcQsort
    {
        public static void Sort<T>(IList<T> a, Comparison<T> cmp)
        {
            int n = a.Count;
            if (n < 2) return;
            var stackLo = new int[64];
            var stackHi = new int[64];
            int sp = 0;
            stackLo[0] = 0; stackHi[0] = n - 1;
            while (sp >= 0)
            {
                int beg = stackLo[sp], end = stackHi[sp--];
                if (end - beg < 8)
                {
                    SmallSort(a, beg, end - beg + 1, cmp);
                    continue;
                }
                int lo = beg, hi = end;
                int med = lo + (hi - lo + 1) / 2;
                SwapAscending(a, lo, med, cmp);
                SwapAscending(a, lo, hi, cmp);
                SwapAscending(a, med, hi, cmp);
                lo++; hi--;
                while (true)
                {
                    while (lo <= hi)
                    {
                        if (lo != med && cmp(a[lo], a[med]) > 0) break;
                        lo++;
                    }
                    while (med != hi)
                    {
                        if (cmp(a[hi], a[med]) <= 0) break;
                        hi--;
                    }
                    if (hi < lo) break;
                    Swap(a, lo, hi);
                    if (hi == med) med = lo;
                    lo++; hi--;
                }
                while (hi > beg)
                {
                    if (hi != med && cmp(a[hi], a[med]) != 0) break;
                    hi--;
                }
                if (hi - beg >= end - lo)
                {
                    stackLo[++sp] = beg; stackHi[sp] = hi;
                    stackLo[++sp] = lo; stackHi[sp] = end;
                }
                else
                {
                    stackLo[++sp] = lo; stackHi[sp] = end;
                    stackLo[++sp] = beg; stackHi[sp] = hi;
                }
            }
        }

        static void SmallSort<T>(IList<T> a, int beg, int count, Comparison<T> cmp)
        {
            for (int e = count; e > 1; e--)
            {
                int max = beg, p = beg;
                for (int i = 1; i < e; i++)
                {
                    p = beg + i;
                    if (cmp(a[p], a[max]) > 0) max = p;
                }
                if (p != max) Swap(a, p, max);
            }
        }

        static void SwapAscending<T>(IList<T> a, int x, int y, Comparison<T> cmp)
        {
            if (cmp(a[x], a[y]) > 0) Swap(a, x, y);
        }

        static void Swap<T>(IList<T> a, int x, int y)
        {
            T t = a[x]; a[x] = a[y]; a[y] = t;
        }
    }
}
