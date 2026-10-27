namespace PoolWatch.Core.Correlation;

public static class Stats
{
    /// <summary>Spearman rank correlation; NaN when either series is constant or shorter than 4.</summary>
    public static double Spearman(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        int n = Math.Min(x.Count, y.Count);
        if (n < 4) return double.NaN;
        var rx = Ranks(x, n);
        var ry = Ranks(y, n);
        return Pearson(rx, ry);
    }

    /// <summary>Spearman of x[t] against y[t - lag] (y leads x by <paramref name="lag"/> samples).</summary>
    public static double SpearmanLagged(IReadOnlyList<double> x, IReadOnlyList<double> y, int lag)
    {
        int n = Math.Min(x.Count, y.Count);
        if (lag <= 0) return Spearman(x, y);
        if (n - lag < 4) return double.NaN;
        var xs = new double[n - lag];
        var ys = new double[n - lag];
        for (int i = lag; i < n; i++) { xs[i - lag] = x[i]; ys[i - lag] = y[i - lag]; }
        return Spearman(xs, ys);
    }

    public static double Pearson(double[] a, double[] b)
    {
        int n = a.Length;
        double ma = a.Average(), mb = b.Average();
        double num = 0, da = 0, db = 0;
        for (int i = 0; i < n; i++)
        {
            double x = a[i] - ma, y = b[i] - mb;
            num += x * y; da += x * x; db += y * y;
        }
        if (da <= 0 || db <= 0) return double.NaN;
        return num / Math.Sqrt(da * db);
    }

    /// <summary>Average ranks (ties share the mean rank).</summary>
    public static double[] Ranks(IReadOnlyList<double> v, int n)
    {
        var idx = Enumerable.Range(0, n).OrderBy(i => v[i]).ToArray();
        var ranks = new double[n];
        int k = 0;
        while (k < n)
        {
            int j = k;
            while (j + 1 < n && v[idx[j + 1]] == v[idx[k]]) j++;
            double r = (k + j) / 2.0 + 1;
            for (int m = k; m <= j; m++) ranks[idx[m]] = r;
            k = j + 1;
        }
        return ranks;
    }

    /// <summary>First differences, clamped at zero for counters that reset.</summary>
    public static double[] Deltas(IReadOnlyList<double> v)
    {
        var d = new double[Math.Max(0, v.Count - 1)];
        for (int i = 1; i < v.Count; i++) d[i - 1] = v[i] - v[i - 1];
        return d;
    }
}
