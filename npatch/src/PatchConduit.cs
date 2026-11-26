using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Rhino.Display;
using Rhino.Geometry;

namespace NPatch
{
  /// <summary>Viewport preview: patch, zebra, curvature combs, edge labels and control points.</summary>
  public class PatchConduit : DisplayConduit
  {
    public static readonly Color Ok = Color.FromArgb(46, 160, 67);
    public static readonly Color Warn = Color.FromArgb(214, 140, 20);
    public static readonly Color Bad = Color.FromArgb(207, 34, 46);
    static readonly Color Boundary = Color.FromArgb(240, 200, 40);
    static readonly Color PatchComb = Color.FromArgb(230, 60, 160);
    static readonly Color NbrComb = Color.FromArgb(40, 170, 220);
    static readonly Color Cv = Color.FromArgb(235, 90, 170);

    readonly PatchSession _s;
    readonly DisplayMaterial _mat = new DisplayMaterial(Color.FromArgb(120, 160, 230), 0.15);

    public bool ShowZebra, ShowCombs = true, ShowLabels = true, ShowControlPoints;
    public double CombScale = 1.0;   // user multiplier on the auto scale
    /// <summary>Second zebra colour; the stripe colour, direction and size come from Rhino's own Zebra settings.</summary>
    public static readonly Color ZebraBase = Color.White;
    public int HighlightEdge = -1;

    // Cached geometry for the current result.
    FillResult _cachedFor;
    Mesh[] _patchMeshes;
    readonly List<Mesh> _nbrMeshes = new List<Mesh>();
    readonly List<(Line[] patch, Polyline patchEnv, Line[] nbr, Polyline nbrEnv)> _combs = new List<(Line[], Polyline, Line[], Polyline)>();
    BoundingBox _bbox = BoundingBox.Empty;

    public PatchConduit(PatchSession session)
    {
      _s = session;
      // Neighbour objects get meshed once, for zebra across the seam. While zebra is on, the real
      // objects are culled and these meshes drawn instead (drawing both z-fights).
      var mp = MeshingParameters.QualityRenderMesh;
      var seen = new HashSet<Guid>();
      foreach (var b in session.Inputs.Where(b => b.AdjacentFace != null))
      {
        if (b.SourceObjectId != Guid.Empty && !seen.Add(b.SourceObjectId)) continue;
        // Whole object when we know it (so culling doesn't hide faces we don't redraw), else the face.
        Brep geo = b.SourceObjectId != Guid.Empty ? b.AdjacentFace.Brep : b.AdjacentFace.DuplicateFace(false);
        if (b.SourceObjectId != Guid.Empty) _culled.Add(b.SourceObjectId);
        // Per-face meshes keep the exact surface normals from meshing, which Rhino's zebra shader
        // needs for clean stripes (re-averaging normals or merging faces gives jagged bands).
        var m = Mesh.CreateFromBrep(geo, Dense(mp, geo.GetBoundingBox(true).Diagonal.Length));
        if (m != null) _nbrMeshes.AddRange(m.Where(x => x != null));
      }
    }

    readonly HashSet<Guid> _culled = new HashSet<Guid>();

    protected override void ObjectCulling(CullObjectEventArgs e)
    {
      if (ShowZebra && e.RhinoObject != null && _culled.Contains(e.RhinoObject.Id)) e.CullObject = true;
    }

    static MeshingParameters Dense(MeshingParameters mp, double size)
    {
      return new MeshingParameters
      {
        MaximumEdgeLength = size / 150, GridMinCount = 64, RefineGrid = true,
        SimplePlanes = false, JaggedSeams = false, GridAngle = mp.GridAngle
      };
    }

    public void Invalidate() => _cachedFor = null;

    void Rebuild()
    {
      var r = _s.Result;
      _cachedFor = r;
      _patchMeshes = null;
      _combs.Clear();
      _bbox = BoundingBox.Empty;
      foreach (var b in _s.Inputs) _bbox.Union(b.Curve.GetBoundingBox(false));
      if (r?.Brep == null) return;
      _bbox.Union(r.Brep.GetBoundingBox(false));
      foreach (var s in r.Surfaces) _bbox.Union(s.GetBoundingBox(false));

      double size = _bbox.Diagonal.Length;
      _patchMeshes = Mesh.CreateFromBrep(r.Brep, Dense(MeshingParameters.QualityRenderMesh, size))?.Where(m => m != null).ToArray();
      BuildCombs(r, size);
    }

    static NurbsSurface Nearest(FillResult r, Point3d p, out double u, out double v)
    {
      NurbsSurface best = null; u = v = 0; double dBest = double.MaxValue;
      foreach (var s in r.Surfaces)
      {
        if (!s.ClosestPoint(p, out double a, out double b)) continue;
        double d = s.PointAt(a, b).DistanceTo(p);
        if (d < dBest) { dBest = d; best = s; u = a; v = b; }
      }
      return best;
    }

    void BuildCombs(FillResult r, double size)
    {
      var raw = new List<(Point3d p, Vector3d n, double kS, double kF)[]>();
      double kMax = 0;
      foreach (var b in _s.Inputs)
      {
        var list = new List<(Point3d, Vector3d, double, double)>();
        if (b.AdjacentFace != null)
        {
          var ts = b.Curve.DivideByCount(48, true);
          foreach (double t in ts ?? new double[0])
          {
            var p = b.Curve.PointAt(t);
            if (!b.AdjacentFace.ClosestPoint(p, out double fu, out double fv)) continue;
            var ps = Nearest(r, p, out double u, out double v);
            if (ps == null) continue;
            var n = b.AdjacentFace.NormalAt(fu, fv); n.Unitize();
            var cross = Vector3d.CrossProduct(n, b.Curve.TangentAt(t)); cross.Unitize();
            if (!NSidedFill.NormalCurvature(ps, u, v, n, cross, out double kS)) continue;
            if (!NSidedFill.NormalCurvature(b.AdjacentFace, fu, fv, n, cross, out double kF)) continue;
            list.Add((p, n, kS, kF));
            kMax = Math.Max(kMax, Math.Max(Math.Abs(kS), Math.Abs(kF)));
          }
        }
        raw.Add(list.ToArray());
      }
      double scale = kMax > 1e-12 ? 0.05 * size / kMax : 0;
      foreach (var edge in raw)
      {
        Line[] Teeth(Func<(Point3d p, Vector3d n, double kS, double kF), double> k) =>
          edge.Select(e => new Line(e.p, e.p - e.n * k(e) * scale * CombScale)).ToArray();
        var pt = Teeth(e => e.kS); var nt = Teeth(e => e.kF);
        _combs.Add((pt, new Polyline(pt.Select(l => l.To)), nt, new Polyline(nt.Select(l => l.To))));
      }
    }

    protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
    {
      if (_cachedFor != _s.Result) Rebuild();
      if (_bbox.IsValid) e.IncludeBoundingBox(_bbox);
    }

    protected override void PostDrawObjects(DrawEventArgs e)
    {
      if (_cachedFor != _s.Result) Rebuild();
      var r = _s.Result;
      var d = e.Display;

      if (r?.Brep != null)
      {
        bool zebra = ShowZebra && _patchMeshes != null && _patchMeshes.Length > 0;
        if (zebra)
        {
          // Rhino's own zebra shader, shaded per pixel: same look and same settings as the Zebra command.
          foreach (var m in _patchMeshes) d.DrawZebraPreview(m, ZebraBase);
          foreach (var m in _nbrMeshes) d.DrawZebraPreview(m, ZebraBase);
        }
        else d.DrawBrepShaded(r.Brep, _mat);
        if (!zebra || Rhino.ApplicationSettings.ZebraAnalysisSettings.ShowIsoCurves)
          d.DrawBrepWires(r.Brep, Color.FromArgb(40, 60, 110), 1);

        if (ShowControlPoints)
        foreach (var s in r.Surfaces)
        {
          int nu = s.Points.CountU, nv = s.Points.CountV;
          for (int i = 0; i < nu; i++)
            for (int j = 0; j < nv; j++)
            {
              s.Points.GetPoint(i, j, out Point3d p);
              if (i + 1 < nu) { s.Points.GetPoint(i + 1, j, out Point3d q); d.DrawLine(p, q, Color.FromArgb(120, Cv)); }
              if (j + 1 < nv) { s.Points.GetPoint(i, j + 1, out Point3d q); d.DrawLine(p, q, Color.FromArgb(120, Cv)); }
              d.DrawPoint(p, PointStyle.RoundSimple, 3, Cv);
            }
        }
      }

      // Boundary, coloured per edge state.
      for (int i = 0; i < _s.Inputs.Count; i++)
      {
        var rep = r?.Edges.ElementAtOrDefault(i);
        var col = rep == null ? Boundary : rep.Ok ? Boundary : Bad;
        d.DrawCurve(_s.Inputs[i].Curve, col, i == HighlightEdge ? 5 : 2);
      }

      if (ShowCombs && r?.Brep != null)
        for (int i = 0; i < _combs.Count; i++)
        {
          var c = _combs[i];
          if (c.patch.Length == 0) continue;
          d.DrawLines(c.nbr, Color.FromArgb(90, NbrComb));
          d.DrawPolyline(c.nbrEnv, NbrComb, 2);
          d.DrawLines(c.patch, Color.FromArgb(90, PatchComb));
          d.DrawPolyline(c.patchEnv, PatchComb, 2);
        }
    }

    protected override void DrawForeground(DrawEventArgs e)
    {
      if (!ShowLabels) return;
      var r = _s.Result;
      for (int i = 0; i < _s.Inputs.Count; i++)
      {
        var rep = r?.Edges.ElementAtOrDefault(i);
        var pt = rep?.LabelPoint ?? _s.Inputs[i].Curve.PointAtNormalizedLength(0.5);
        e.Display.DrawDot(pt, LabelText(i, rep), rep == null ? Color.Gray : rep.Ok ? Ok : Bad, Color.White);
      }
    }

    /// <summary>Screen positions of the edge labels (for click-to-cycle).</summary>
    public IEnumerable<(int edge, Point2d screen)> LabelScreenPoints(RhinoViewport vp)
    {
      var r = _s.Result;
      for (int i = 0; i < _s.Inputs.Count; i++)
      {
        var pt = r?.Edges.ElementAtOrDefault(i)?.LabelPoint ?? _s.Inputs[i].Curve.PointAtNormalizedLength(0.5);
        yield return (i, vp.WorldToClient(pt));
      }
    }

    public string LabelText(int i, EdgeReport rep)
    {
      var b = _s.Inputs[i];
      string c = b.AdjacentFace == null ? "G0" : b.Continuity.ToString();
      if (rep == null) return $"E{i + 1} {c}";
      string v = rep.Effective == Continuity.G2 ? $"{rep.CurvaturePct:0.#}%"
               : rep.Effective == Continuity.G1 ? $"{rep.AngleDeg:0.###}°"
               : $"{rep.Gap:0.####}";
      return $"E{i + 1} {c} {v}";
    }

  }
}
