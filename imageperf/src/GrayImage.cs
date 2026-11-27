using System;

namespace ImagePerf
{
  /// <summary>
  /// The picture as brightness values (0 = black, 1 = white). Transparent pixels count as white.
  /// Loaded once on the UI thread (Eto), then read from any thread.
  /// </summary>
  public class GrayImage
  {
    public readonly int W, H;
    readonly float[] _v;
    public string Path { get; private set; }

    GrayImage(int w, int h) { W = w; H = h; _v = new float[w * h]; }

    public static GrayImage Load(string path, int maxSide = 900)
    {
      using (var src = new Eto.Drawing.Bitmap(path))
      {
        int w = src.Width, h = src.Height;
        if (w <= 0 || h <= 0) throw new Exception("이미지 크기를 읽지 못했어요.");
        double k = Math.Min(1.0, maxSide / (double)Math.Max(w, h));
        int nw = Math.Max(1, (int)Math.Round(w * k)), nh = Math.Max(1, (int)Math.Round(h * k));
        Eto.Drawing.Bitmap bmp = src;
        bool scaled = nw != w || nh != h;
        if (scaled) bmp = new Eto.Drawing.Bitmap(src, nw, nh, Eto.Drawing.ImageInterpolation.High);
        try
        {
          var img = new GrayImage(nw, nh) { Path = path };
          using (var data = bmp.Lock())
          {
            for (int y = 0; y < nh; y++)
              for (int x = 0; x < nw; x++)
              {
                var c = data.GetPixel(x, y);
                double l = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
                l = l * c.A + (1 - c.A);
                img._v[y * nw + x] = (float)Math.Max(0, Math.Min(1, l));
              }
          }
          return img;
        }
        finally { if (scaled) bmp.Dispose(); }
      }
    }

    /// <summary>Bilinear brightness at (x, y) in 0..1, origin bottom-left, y up.</summary>
    public double Sample(double x, double y)
    {
      double px = Clamp(x) * (W - 1), py = (1 - Clamp(y)) * (H - 1);
      int x0 = (int)Math.Floor(px), y0 = (int)Math.Floor(py);
      int x1 = Math.Min(W - 1, x0 + 1), y1 = Math.Min(H - 1, y0 + 1);
      double fx = px - x0, fy = py - y0;
      double a = _v[y0 * W + x0], b = _v[y0 * W + x1], c = _v[y1 * W + x0], d = _v[y1 * W + x1];
      return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy;
    }

    /// <summary>Average brightness over a small ellipse (radius rx, ry in 0..1 units): smoother halftone.</summary>
    public double SampleArea(double x, double y, double rx, double ry)
    {
      double sum = Sample(x, y) * 2, wsum = 2;
      for (int i = 0; i < 8; i++)
      {
        double a = i * Math.PI / 4;
        sum += Sample(x + Math.Cos(a) * rx * 0.7, y + Math.Sin(a) * ry * 0.7);
        wsum += 1;
      }
      return sum / wsum;
    }

    static double Clamp(double t) => t < 0 ? 0 : t > 1 ? 1 : t;
  }
}
