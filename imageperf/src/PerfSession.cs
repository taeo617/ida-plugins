using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;

namespace ImagePerf
{
  /// <summary>Targets + options + picture; recomputes the hole layout in the background.</summary>
  public class PerfSession
  {
    public readonly List<PerfTarget> Targets;
    public PerfOptions Options;
    public GrayImage Image { get; private set; }
    public readonly double Tol;
    public PerfResult Result { get; private set; }
    public string Error { get; private set; }
    public bool Busy { get; private set; }
    public event Action Updated;

    CancellationTokenSource _cts;
    Task _task = Task.CompletedTask;
    int _gen;

    public PerfSession(List<PerfTarget> targets, PerfOptions options, double tol)
    {
      Targets = targets; Options = options; Tol = tol;
    }

    /// <summary>Loads the picture (UI thread). Returns an error message, or null.</summary>
    public string LoadImage(string path)
    {
      if (string.IsNullOrWhiteSpace(path)) { Image = null; Options.ImagePath = ""; return null; }
      try
      {
        Image = GrayImage.Load(path);
        Options.ImagePath = path;
        return null;
      }
      catch (Exception e)
      {
        return "이미지를 열지 못했어요: " + e.Message;
      }
    }

    public void ClearImage() { Image = null; Options.ImagePath = ""; }

    public void ComputeAsync()
    {
      _cts?.Cancel();
      var cts = new CancellationTokenSource();
      _cts = cts;
      int gen = ++_gen;
      var opt = Options.Clone();
      var img = Image;
      Busy = true;
      var prev = _task;
      _task = Task.Run(() =>
      {
        try { prev.Wait(); } catch { }
        if (cts.IsCancellationRequested) return;
        PerfResult r = null; string err = null;
        try { r = PerfEngine.Compute(Targets, opt, img, Tol, cts.Token); }
        catch (Exception e) { err = e.Message; }
        if (cts.IsCancellationRequested) return;
        RhinoApp.InvokeOnUiThread((Action)(() =>
        {
          if (gen != _gen) return;
          if (r != null) Result = r;
          Error = err;
          Busy = false;
          Updated?.Invoke();
        }));
      });
    }

    /// <summary>Synchronous compute (scripted command, or Apply while a solve is still running).</summary>
    public PerfResult ComputeNow()
    {
      _cts?.Cancel();
      try { _task.Wait(); } catch { }
      ++_gen;
      Result = PerfEngine.Compute(Targets, Options.Clone(), Image, Tol, CancellationToken.None);
      Busy = false;
      return Result;
    }
  }

  /// <summary>Viewport preview: hole outlines on the surface, and a count in the corner.</summary>
  public class PerfConduit : DisplayConduit
  {
    static readonly Color HoleColor = Color.FromArgb(235, 96, 20);
    readonly PerfSession _s;
    PerfResult _cachedFor;
    Line[] _lines = new Line[0];
    BoundingBox _bbox = BoundingBox.Empty;

    public PerfConduit(PerfSession s)
    {
      _s = s;
      foreach (var t in s.Targets) _bbox.Union(t.Brep.GetBoundingBox(false));
    }

    void Cache()
    {
      var r = _s.Result;
      if (ReferenceEquals(r, _cachedFor)) return;
      _cachedFor = r;
      var lines = new List<Line>();
      if (r != null)
        foreach (var h in r.Holes)
        {
          var pts = PerfEngine.Outline(h, Math.Max(0.02, h.Size * 0.03), h.Shape == HoleShape.Circle ? 16 : 24);
          for (int i = 0; i < pts.Count; i++) lines.Add(new Line(pts[i], pts[(i + 1) % pts.Count]));
        }
      _lines = lines.ToArray();
    }

    protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
    {
      if (_bbox.IsValid) e.IncludeBoundingBox(_bbox);
    }

    protected override void PostDrawObjects(DrawEventArgs e)
    {
      Cache();
      if (_lines.Length > 0) e.Display.DrawLines(_lines, HoleColor, 1);
    }

    protected override void DrawForeground(DrawEventArgs e)
    {
      var r = _s.Result;
      string text = _s.Busy && r == null ? "계산 중…" : r == null ? "" : $"이미지 타공 · 구멍 {r.Holes.Count:N0}개" + (_s.Busy ? " · 계산 중…" : "");
      if (text.Length == 0) return;
      var b = e.Viewport.Bounds;
      e.Display.Draw2dText(text, HoleColor, new Point2d(b.Left + 14, b.Bottom - 26), false, 14);
    }
  }
}
