using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Rhino;
using Rhino.Geometry;

namespace NPatch
{
  /// <summary>
  /// State of one interactive fill: the boundary, the settings and the latest result.
  /// Solves run on a worker thread; only the newest request's result is kept.
  /// </summary>
  public class PatchSession
  {
    public readonly List<BoundaryInput> Inputs;
    public readonly FillOptions Options;
    public readonly double Tolerance;
    public readonly Guid SourceObjectId;   // Guid.Empty when edges come from several objects / curves
    public readonly Brep SourceBrep;

    public FillResult Result { get; private set; }
    public bool Busy { get; private set; }
    public event Action Updated;

    int _version;

    public PatchSession(List<BoundaryInput> inputs, FillOptions options, double tol, Guid sourceId, Brep sourceBrep)
    {
      Inputs = OrderAlongLoop(inputs, tol);
      Options = options;
      Tolerance = tol;
      SourceObjectId = sourceId;
      SourceBrep = sourceBrep;
    }

    /// <summary>Solve synchronously (used for the first result and by scripts).</summary>
    public void SolveNow()
    {
      _version++;
      Result = NSidedFill.Solve(Snapshot(), Options.Clone(), Tolerance);
      Busy = false;
      Updated?.Invoke();
    }

    /// <summary>Solve on a worker thread; raises <see cref="Updated"/> on the UI thread.</summary>
    public void SolveAsync()
    {
      int v = ++_version;
      Busy = true;
      Updated?.Invoke();
      var inputs = Snapshot();
      var opt = Options.Clone();
      Task.Run(() =>
      {
        FillResult r;
        try { r = NSidedFill.Solve(inputs, opt, Tolerance); }
        catch (Exception ex) { r = new FillResult { Message = "Error: " + ex.Message }; }
        RhinoApp.InvokeOnUiThread(new Action(() =>
        {
          if (v != _version) return; // a newer request is on its way
          Result = r;
          Busy = false;
          Updated?.Invoke();
        }));
      });
    }

    List<BoundaryInput> Snapshot() =>
      Inputs.Select(b => new BoundaryInput { Curve = b.Curve, AdjacentFace = b.AdjacentFace, Continuity = b.Continuity, SourceObjectId = b.SourceObjectId }).ToList();

    /// <summary>Orders boundary pieces head-to-tail so E1..En run around the hole.</summary>
    static List<BoundaryInput> OrderAlongLoop(List<BoundaryInput> inputs, double tol)
    {
      if (inputs.Count < 2) return inputs;
      double gapTol = Math.Max(tol * 10, 1e-6);
      var left = new List<BoundaryInput>(inputs);
      var ordered = new List<BoundaryInput> { left[0] };
      left.RemoveAt(0);
      Point3d end = ordered[0].Curve.PointAtEnd;
      while (left.Count > 0)
      {
        int bestI = -1; bool flip = false; double bestD = double.MaxValue;
        for (int i = 0; i < left.Count; i++)
        {
          double d0 = left[i].Curve.PointAtStart.DistanceTo(end);
          double d1 = left[i].Curve.PointAtEnd.DistanceTo(end);
          if (d0 < bestD) { bestD = d0; bestI = i; flip = false; }
          if (d1 < bestD) { bestD = d1; bestI = i; flip = true; }
        }
        if (bestD > gapTol * 100) { ordered.AddRange(left); break; } // not a chain; keep the rest as given
        var b = left[bestI];
        left.RemoveAt(bestI);
        if (flip) { var c = b.Curve.DuplicateCurve(); c.Reverse(); b.Curve = c; }
        ordered.Add(b);
        end = b.Curve.PointAtEnd;
      }
      return ordered;
    }
  }
}
