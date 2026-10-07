namespace WallDistance.Core
{
    /// <summary>
    /// Order statistics on caller-owned scratch arrays, without allocating. Floor self-alignment
    /// takes percentiles of thousands of heights for each of ~85 trial shifts per frame; sorting
    /// every time would dominate the frame budget.
    /// </summary>
    public static class Selection
    {
        /// <summary>The k-th smallest (0-based) of a[0..n). Reorders a[0..n).</summary>
        public static float Kth(float[] a, int n, int k)
        {
            int lo = 0, hi = n - 1;
            while (lo < hi)
            {
                // Hoare partition around the middle element; with many duplicates (integer-like
                // heights) it still splits evenly, unlike a Lomuto partition.
                float pivot = a[(lo + hi) >> 1];
                int i = lo, j = hi;
                while (i <= j)
                {
                    while (a[i] < pivot) i++;
                    while (a[j] > pivot) j--;
                    if (i <= j)
                    {
                        float tmp = a[i];
                        a[i] = a[j];
                        a[j] = tmp;
                        i++;
                        j--;
                    }
                }
                // [lo..j] ≤ pivot ≤ [i..hi]; anything strictly between equals the pivot.
                if (k <= j) hi = j;
                else if (k >= i) lo = i;
                else return a[k];
            }
            return a[k];
        }
    }
}
