// Adapted from Microsoft .NET Framework ArraySortHelper<T> (MIT).
// Sort-Object 5.1 uses List<T>.Sort; completely equal keys are NOT made stable.
using System;
using System.Collections.Generic;
namespace Beta29.SearchBackend.Compatibility;
internal static class FrameworkSort<T>
{
    internal static void Sort(T[] keys, int count, bool targetsDesktop45, IComparer<T> comparer)
    {
        if (count < 2) return;
        if (targetsDesktop45) IntrospectiveSort(keys, 0, count, comparer);
        else DepthLimitedQuickSort(keys, 0, count - 1, comparer, 32);
    }
        private static void SwapIfGreater(T[] keys, IComparer<T> comparer, int a, int b)
        {
            if (a != b)
            {
                if (comparer.Compare(keys[a], keys[b]) > 0)
                {
                    T key = keys[a];
                    keys[a] = keys[b];
                    keys[b] = key;
                }
            }
        }

        private static void Swap(T[] a, int i, int j)
        {
            if(i != j)
            {
                T t = a[i];
                a[i] = a[j];
                a[j] = t;
            }
        }

        internal static void DepthLimitedQuickSort(T[] keys, int left, int right, IComparer<T> comparer, int depthLimit)
        {
            do
            {
                if (depthLimit == 0)
                {
                    Heapsort(keys, left, right, comparer);
                    return;
                }

                int i = left;
                int j = right;

                // pre-sort the low, middle (pivot), and high values in place.
                // this improves performance in the face of already sorted data, or 
                // data that is made up of multiple sorted runs appended together.
                int middle = i + ((j - i) >> 1);
                SwapIfGreater(keys, comparer, i, middle);  // swap the low with the mid point
                SwapIfGreater(keys, comparer, i, j);   // swap the low with the high
                SwapIfGreater(keys, comparer, middle, j); // swap the middle with the high

                T x = keys[middle];
                do
                {
                    while (comparer.Compare(keys[i], x) < 0) i++;
                    while (comparer.Compare(x, keys[j]) < 0) j--;

                    if (i > j) break;
                    if (i < j)
                    {
                        T key = keys[i];
                        keys[i] = keys[j];
                        keys[j] = key;
                    }
                    i++;
                    j--;
                } while (i <= j);

                // The next iteration of the while loop is to "recursively" sort the larger half of the array and the
                // following calls recrusively sort the smaller half.  So we subtrack one from depthLimit here so
                // both sorts see the new value.
                depthLimit--;

                if (j - left <= right - i)
                {
                    if (left < j) DepthLimitedQuickSort(keys, left, j, comparer, depthLimit);
                    left = i;
                }
                else
                {
                    if (i < right) DepthLimitedQuickSort(keys, i, right, comparer, depthLimit);
                    right = j;
                }
            } while (left < right);
        }

        internal static void IntrospectiveSort(T[] keys, int left, int length, IComparer<T> comparer)
        {







            if (length < 2)
                return;

            IntroSort(keys, left, length + left - 1, 2 * IntrospectiveSortUtilities.FloorLog2(keys.Length), comparer);
        }

        private static void IntroSort(T[] keys, int lo, int hi, int depthLimit, IComparer<T> comparer)
        {





            while (hi > lo)
            {
                int partitionSize = hi - lo + 1;
                if (partitionSize <= IntrospectiveSortUtilities.IntrosortSizeThreshold)
                {
                    if (partitionSize == 1)
                    {
                        return;
                    }
                    if (partitionSize == 2)
                    {
                        SwapIfGreater(keys, comparer, lo, hi);
                        return;
                    }
                    if (partitionSize == 3)
                    {
                        SwapIfGreater(keys, comparer, lo, hi-1);
                        SwapIfGreater(keys, comparer, lo, hi);
                        SwapIfGreater(keys, comparer, hi-1, hi);
                        return;
                    }

                    InsertionSort(keys, lo, hi, comparer);
                    return;
                }

                if (depthLimit == 0)
                {
                    Heapsort(keys, lo, hi, comparer);
                    return;
                }
                depthLimit--;

                int p = PickPivotAndPartition(keys, lo, hi, comparer);
                // Note we've already partitioned around the pivot and do not have to move the pivot again.
                IntroSort(keys, p + 1, hi, depthLimit, comparer);
                hi = p - 1;
            }
        }

        private static int PickPivotAndPartition(T[] keys, int lo, int hi, IComparer<T> comparer)
        {







            // Compute median-of-three.  But also partition them, since we've done the comparison.
            int middle = lo + ((hi - lo) / 2);

            // Sort lo, mid and hi appropriately, then pick mid as the pivot.
            SwapIfGreater(keys, comparer, lo, middle);  // swap the low with the mid point
            SwapIfGreater(keys, comparer, lo, hi);   // swap the low with the high
            SwapIfGreater(keys, comparer, middle, hi); // swap the middle with the high

            T pivot = keys[middle];
            Swap(keys, middle, hi - 1);
            int left = lo, right = hi - 1;  // We already partitioned lo and hi and put the pivot in hi - 1.  And we pre-increment & decrement below.

            while (left < right)
            {
                while (comparer.Compare(keys[++left], pivot) < 0) ;
                while (comparer.Compare(pivot, keys[--right]) < 0) ;

                if (left >= right)
                    break;

                Swap(keys, left, right);
            }

            // Put pivot in the right location.
            Swap(keys, left, (hi - 1));
            return left;
        }

        private static void Heapsort(T[] keys, int lo, int hi, IComparer<T> comparer)
        {






            int n = hi - lo + 1;
            for (int i = n / 2; i >= 1; i = i - 1)
            {
                DownHeap(keys, i, n, lo, comparer);
            }
            for (int i = n; i > 1; i = i - 1)
            {
                Swap(keys, lo, lo + i - 1);
                DownHeap(keys, 1, i - 1, lo, comparer);
            }
        }

        private static void DownHeap(T[] keys, int i, int n, int lo, IComparer<T> comparer)
        {





            T d = keys[lo + i - 1];
            int child;
            while (i <= n / 2)
            {
                child = 2 * i;
                if (child < n && comparer.Compare(keys[lo + child - 1], keys[lo + child]) < 0)
                {
                    child++;
                }
                if (!(comparer.Compare(d, keys[lo + child - 1]) < 0))
                    break;
                keys[lo + i - 1] = keys[lo + child - 1];
                i = child;
            }
            keys[lo + i - 1] = d;
        }

        private static void InsertionSort(T[] keys, int lo, int hi, IComparer<T> comparer)
        {





            int i, j;
            T t;
            for (i = lo; i < hi; i++)
            {
                j = i;
                t = keys[i + 1];
                while (j >= lo && comparer.Compare(t, keys[j]) < 0)
                {
                    keys[j + 1] = keys[j];
                    j--;
                }
                keys[j + 1] = t;
            }
        }
}
internal static class IntrospectiveSortUtilities
{
    internal const int IntrosortSizeThreshold = 16;
    internal static int FloorLog2(int n)
    {
        int result = 0;
        while (n >= 1) { result++; n /= 2; }
        return result;
    }
}
