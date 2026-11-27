using System;

namespace NPatch
{
  /// <summary>
  /// Symmetric banded matrix that stores only its lower band (n × (bw+1) values instead of n × n).
  /// Callers add both (i,j) and (j,i) of a symmetric product; the upper half is simply ignored.
  /// <see cref="Solve"/> factors in place (Cholesky; fill-in stays inside the band) and solves A x = b.
  /// </summary>
  internal sealed class BandMatrix
  {
    public readonly int N, Bw;
    readonly int _w;
    readonly double[] _a;   // row i, column j (i-Bw ≤ j ≤ i) at i*_w + (j - i + Bw)
    bool _overflow;

    public BandMatrix(int n, int bw)
    {
      N = n;
      Bw = Math.Max(0, Math.Min(bw, n - 1));
      _w = Bw + 1;
      _a = new double[(long)n * _w];
    }

    public void Add(int i, int j, double v)
    {
      if (j > i) return;
      int d = i - j;
      if (d > Bw) { if (v != 0) _overflow = true; return; }
      _a[(long)i * _w + Bw - d] += v;
    }

    public bool Solve(double[] b)
    {
      if (_overflow) return false;
      int n = N, bw = Bw, w = _w;
      var a = _a;
      for (int j = 0; j < n; j++)
      {
        long jr = (long)j * w + bw - j;           // index of (j, k) = jr + k
        int k0 = Math.Max(0, j - bw);
        double s = a[jr + j];
        for (int k = k0; k < j; k++) s -= a[jr + k] * a[jr + k];
        if (s <= 0) return false;
        double ljj = Math.Sqrt(s);
        a[jr + j] = ljj;
        int iEnd = Math.Min(n - 1, j + bw);
        for (int i = j + 1; i <= iEnd; i++)
        {
          long ir = (long)i * w + bw - i;
          int kk = Math.Max(k0, i - bw);
          double t = a[ir + j];
          for (int k = kk; k < j; k++) t -= a[ir + k] * a[jr + k];
          a[ir + j] = t / ljj;
        }
      }
      for (int i = 0; i < n; i++)
      {
        long ir = (long)i * w + bw - i;
        double t = b[i];
        for (int k = Math.Max(0, i - bw); k < i; k++) t -= a[ir + k] * b[k];
        b[i] = t / a[ir + i];
      }
      for (int i = n - 1; i >= 0; i--)
      {
        double t = b[i];
        int kEnd = Math.Min(n - 1, i + bw);
        for (int k = i + 1; k <= kEnd; k++) t -= a[(long)k * w + bw - k + i] * b[k];
        b[i] = t / a[(long)i * w + bw];
      }
      return true;
    }
  }
}
