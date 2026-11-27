using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace ImagePerf
{
  /// <summary>One picked object and the faces of it that get holes.</summary>
  public class PerfTarget
  {
    public Guid Id;
    public Brep Brep;
    public List<int> Faces = new List<int>();
    internal List<FaceInfo> Infos;
    public bool IsSolid => Brep.IsSolid;
  }

  class FaceInfo
  {
    public int Target, FaceIndex;
    public BrepFace Face;
    public Surface Srf;
    public double U0, U1, V0, V1, Lu, Lv;
    public List<Curve> Edges = new List<Curve>();
    public List<Point3d> EdgePts = new List<Point3d>();
    public Vector3d AvgNormal;
    public double Area;
    public Mesh Mesh;
  }

  public class Hole
  {
    public int Target, Face;
    public double U, V;
    public Point3d P;
    public Vector3d N, X;        // normal and in-plane x direction
    public double Size, Len;     // width and (slot) length
    public HoleShape Shape;
    public double R => Shape == HoleShape.Slot ? Len / 2 : Size / 2;
    /// <summary>Segment the capsule runs along (degenerate for round holes).</summary>
    public Point3d A => Shape == HoleShape.Slot ? P - X * ((Len - Size) / 2) : P;
    public Point3d B => Shape == HoleShape.Slot ? P + X * ((Len - Size) / 2) : P;
    public double CapR => Size / 2;
  }

  public class PerfResult
  {
    public List<Hole> Holes = new List<Hole>();
    public int Candidates, RemovedOverlap, RemovedMargin;
    public long Ms;
    public string Note;
    public double RegionW, RegionH;
  }

  public static class PerfEngine
  {
    const int MaxCandidates = 80000;

    // ───────────────────────── preparation ─────────────────────────

    internal static void Prepare(IList<PerfTarget> targets, double tol)
    {
      for (int ti = 0; ti < targets.Count; ti++)
      {
        var t = targets[ti];
        if (t.Infos != null) continue;
        var list = new List<FaceInfo>();
        foreach (var fi in t.Faces)
        {
          var face = t.Brep.Faces[fi];
          var info = new FaceInfo { Target = ti, FaceIndex = fi, Face = face, Srf = face.UnderlyingSurface() };
          var outer = face.OuterLoop?.To2dCurve();
          var bb = outer != null ? outer.GetBoundingBox(true) : BoundingBox.Empty;
          var du = face.Domain(0); var dv = face.Domain(1);
          info.U0 = bb.IsValid ? Math.Max(du.Min, bb.Min.X) : du.Min;
          info.U1 = bb.IsValid ? Math.Min(du.Max, bb.Max.X) : du.Max;
          info.V0 = bb.IsValid ? Math.Max(dv.Min, bb.Min.Y) : dv.Min;
          info.V1 = bb.IsValid ? Math.Min(dv.Max, bb.Max.Y) : dv.Max;
          info.Lu = IsoLength(info.Srf, 0, (info.V0 + info.V1) / 2, info.U0, info.U1);
          info.Lv = IsoLength(info.Srf, 1, (info.U0 + info.U1) / 2, info.V0, info.V1);
          foreach (var loop in face.Loops)
            foreach (var trim in loop.Trims)
            {
              var e = trim.Edge;
              if (e == null) continue;
              var c = e.DuplicateCurve();
              if (c == null) continue;
              info.Edges.Add(c);
              var ts = c.DivideByCount(40, true);
              if (ts != null) info.EdgePts.AddRange(ts.Select(c.PointAt));
            }
          // Area-weighted average normal from a small grid of points on the face.
          var n = Vector3d.Zero;
          for (int i = 0; i <= 6; i++)
            for (int j = 0; j <= 6; j++)
            {
              double u = info.U0 + (info.U1 - info.U0) * (i + 0.5) / 7.0, v = info.V0 + (info.V1 - info.V0) * (j + 0.5) / 7.0;
              if (face.IsPointOnFace(u, v) == PointFaceRelation.Exterior) continue;
              n += face.NormalAt(u, v);
            }
          info.AvgNormal = n.IsZero ? face.NormalAt((info.U0 + info.U1) / 2, (info.V0 + info.V1) / 2) : n;
          var amp = AreaMassProperties.Compute(face.DuplicateFace(false), true, false, false, false);
          info.Area = amp?.Area ?? info.Lu * info.Lv;
          list.Add(info);
        }
        t.Infos = list;
      }
    }

    static double IsoLength(Surface s, int dir, double c, double a, double b)
    {
      var crv = s.IsoCurve(dir, c);
      if (crv == null) return 0;
      var tr = crv.Trim(a, b) ?? crv;
      return tr.GetLength();
    }

    // ───────────────────────── layout ─────────────────────────

    static IEnumerable<(double u, double v)> GridUV(FaceInfo f, double spacing, bool hex)
    {
      var vcurve = f.Srf.IsoCurve(1, (f.U0 + f.U1) / 2);
      vcurve = vcurve?.Trim(f.V0, f.V1) ?? vcurve;
      if (vcurve == null) yield break;
      double lv = vcurve.GetLength();
      if (lv <= 0) yield break;
      double rowStep = hex ? spacing * Math.Sqrt(3) / 2 : spacing;
      int nr = (int)Math.Floor(lv / 2 / rowStep) + 1;
      for (int r = -nr; r <= nr; r++)
      {
        double s = lv / 2 + r * rowStep;
        if (s < 0 || s > lv) continue;
        if (!vcurve.LengthParameter(s, out double v)) continue;
        var ucurve = f.Srf.IsoCurve(0, v);
        ucurve = ucurve?.Trim(f.U0, f.U1) ?? ucurve;
        if (ucurve == null) continue;
        double lu = ucurve.GetLength();
        if (lu <= 1e-9) { yield return ((f.U0 + f.U1) / 2, v); continue; }
        double shift = hex && ((r % 2) + 2) % 2 == 1 ? 0.5 : 0;
        int nk = (int)Math.Floor(lu / 2 / spacing) + 1;
        for (int k = -nk; k <= nk; k++)
        {
          double s2 = lu / 2 + (k + shift) * spacing;
          if (s2 < 0 || s2 > lu) continue;
          if (!ucurve.LengthParameter(s2, out double u)) continue;
          yield return (u, v);
        }
      }
    }

    /// <summary>Even (blue-noise) spread over the real surface: dart throwing on the face mesh.</summary>
    static IEnumerable<(double u, double v)> EvenUV(FaceInfo f, double spacing)
    {
      if (f.Mesh == null)
      {
        var ms = Mesh.CreateFromBrep(f.Face.DuplicateFace(false), MeshingParameters.QualityRenderMesh);
        var m = new Mesh();
        if (ms != null) foreach (var x in ms) m.Append(x);
        m.Faces.ConvertQuadsToTriangles();
        f.Mesh = m;
      }
      var mesh = f.Mesh;
      if (mesh.Faces.Count == 0) yield break;
      var areas = new double[mesh.Faces.Count];
      double total = 0;
      for (int i = 0; i < mesh.Faces.Count; i++)
      {
        var fc = mesh.Faces[i];
        Point3d a = mesh.Vertices[fc.A], b = mesh.Vertices[fc.B], c = mesh.Vertices[fc.C];
        total += Vector3d.CrossProduct(b - a, c - a).Length / 2;
        areas[i] = total;
      }
      int tries = (int)Math.Min(MaxCandidates * 4, Math.Max(200, 10 * total / (spacing * spacing)));
      var rnd = new Random(7);
      var grid = new Dictionary<(long, long, long), List<Point3d>>();
      var accepted = new List<Point3d>();
      double s2 = spacing * spacing;
      for (int n = 0; n < tries; n++)
      {
        double pick = rnd.NextDouble() * total;
        int idx = Array.BinarySearch(areas, pick);
        if (idx < 0) idx = ~idx;
        idx = Math.Min(idx, areas.Length - 1);
        var fc = mesh.Faces[idx];
        Point3d a = mesh.Vertices[fc.A], b = mesh.Vertices[fc.B], c = mesh.Vertices[fc.C];
        double r1 = Math.Sqrt(rnd.NextDouble()), r2 = rnd.NextDouble();
        var p = a * (1 - r1) + b * (r1 * (1 - r2)) + c * (r1 * r2);
        var key = (Cell(p.X, spacing), Cell(p.Y, spacing), Cell(p.Z, spacing));
        bool ok = true;
        for (long dx = -1; dx <= 1 && ok; dx++)
          for (long dy = -1; dy <= 1 && ok; dy++)
            for (long dz = -1; dz <= 1 && ok; dz++)
              if (grid.TryGetValue((key.Item1 + dx, key.Item2 + dy, key.Item3 + dz), out var bucket))
                foreach (var q in bucket) if (q.DistanceToSquared(p) < s2) { ok = false; break; }
        if (!ok) continue;
        if (!grid.TryGetValue(key, out var bl)) grid[key] = bl = new List<Point3d>();
        bl.Add(p);
        accepted.Add(p);
      }
      foreach (var p in accepted)
        if (f.Face.ClosestPoint(p, out double u, out double v)) yield return (u, v);
    }

    static long Cell(double x, double s) => (long)Math.Floor(x / s);

    // ───────────────────────── image mapping ─────────────────────────

    /// <summary>Plane the picture is projected from: fitted to the faces, facing out, x along world X.</summary>
    static Plane ProjectionPlane(List<FaceInfo> infos)
    {
      var pts = infos.SelectMany(f => f.EdgePts).ToList();
      var avgN = Vector3d.Zero;
      foreach (var f in infos) { var n = f.AvgNormal; n.Unitize(); avgN += n * Math.Max(f.Area, 1e-6); }
      if (!avgN.Unitize()) avgN = Vector3d.ZAxis;
      Vector3d z = avgN;
      if (pts.Count >= 3 && Plane.FitPlaneToPoints(pts, out Plane fit) == PlaneFitResult.Success)
      {
        z = fit.ZAxis;
        if (z * avgN < 0) z = -z;
        // A strongly curved part (e.g. a half cylinder) fits badly; trust the average normal then.
        if (z * avgN < 0.5) z = avgN;
      }
      var origin = pts.Count > 0 ? new BoundingBox(pts).Center : Point3d.Origin;
      var x = Vector3d.XAxis - z * (Vector3d.XAxis * z);
      if (x.Length < 0.2) x = Vector3d.YAxis - z * (Vector3d.YAxis * z);
      x.Unitize();
      var y = Vector3d.CrossProduct(z, x);
      return new Plane(origin, x, y);
    }

    /// <summary>Region coords (0..1) → picture coords (0..1), or false when outside the picture.</summary>
    static bool ToImage(double nx, double ny, double regionAspect, GrayImage img, PerfOptions o, out double ix, out double iy)
    {
      int rot = ((o.Rotate % 4) + 4) % 4;
      double iw = rot % 2 == 1 ? img.H : img.W, ih = rot % 2 == 1 ? img.W : img.H;
      double ia = iw / ih, ra = regionAspect;
      double x = nx, y = ny;
      switch (o.Fit)
      {
        case FitMode.Contain:
          if (ia > ra) y = 0.5 + (ny - 0.5) * ia / ra; else x = 0.5 + (nx - 0.5) * ra / ia;
          break;
        case FitMode.Cover:
          if (ia > ra) x = 0.5 + (nx - 0.5) * ra / ia; else y = 0.5 + (ny - 0.5) * ia / ra;
          break;
      }
      ix = x; iy = y;
      if (x < -1e-9 || x > 1 + 1e-9 || y < -1e-9 || y > 1 + 1e-9) return false;
      if (o.FlipX) x = 1 - x;
      switch (rot)
      {
        case 1: ix = y; iy = 1 - x; break;
        case 2: ix = 1 - x; iy = 1 - y; break;
        case 3: ix = 1 - y; iy = x; break;
        default: ix = x; iy = y; break;
      }
      return true;
    }

    // ───────────────────────── compute ─────────────────────────

    public static PerfResult Compute(IList<PerfTarget> targets, PerfOptions o, GrayImage img, double tol, CancellationToken ct)
    {
      var sw = Stopwatch.StartNew();
      var res = new PerfResult();
      Prepare(targets, tol);
      var infos = targets.SelectMany(t => t.Infos).ToList();
      if (infos.Count == 0) { res.Note = "구멍을 낼 면이 없어요."; return res; }

      double spacing = Math.Max(o.Spacing, tol * 20);
      double maxS = o.EffectiveMax, minS = o.EffectiveMin;
      double ratio = Math.Max(1.0, o.SlotRatio);

      // Projection frame shared by all faces, so the picture runs continuously across seams.
      Plane proj = Plane.WorldXY;
      double s0 = 0, s1 = 1, t0 = 0, t1 = 1;
      if (o.Mapping == Mapping.Projection)
      {
        proj = ProjectionPlane(infos);
        s0 = t0 = double.MaxValue; s1 = t1 = double.MinValue;
        foreach (var p in infos.SelectMany(f => f.EdgePts))
        {
          proj.ClosestParameter(p, out double s, out double t);
          s0 = Math.Min(s0, s); s1 = Math.Max(s1, s); t0 = Math.Min(t0, t); t1 = Math.Max(t1, t);
        }
        if (s1 - s0 < tol) { s0 -= 1; s1 += 1; }
        if (t1 - t0 < tol) { t0 -= 1; t1 += 1; }
        res.RegionW = s1 - s0; res.RegionH = t1 - t0;
      }

      var holes = new List<Hole>();
      int cand = 0;
      foreach (var f in infos)
      {
        if (ct.IsCancellationRequested) return null;
        var uvs = o.Layout == Layout.Even ? EvenUV(f, spacing) : GridUV(f, spacing, o.Layout == Layout.Hex);
        double regionAspect, rW, rH;
        if (o.Mapping == Mapping.Projection) { rW = s1 - s0; rH = t1 - t0; }
        else { rW = Math.Max(f.Lu, tol); rH = Math.Max(f.Lv, tol); }
        regionAspect = rW / rH;
        if (o.Mapping == Mapping.UV && res.RegionW == 0) { res.RegionW = rW; res.RegionH = rH; }
        // Averaging radius: about half a cell, in region units.
        double rx = 0.5 * spacing / rW, ry = 0.5 * spacing / rH;

        foreach (var (u, v) in uvs)
        {
          if (++cand > MaxCandidates)
          {
            res.Note = $"구멍 자리가 {MaxCandidates:N0}개를 넘어서 멈췄어요. 간격을 늘려 주세요.";
            goto done;
          }
          if ((cand & 255) == 0 && ct.IsCancellationRequested) return null;
          if (f.Face.IsPointOnFace(u, v) != PointFaceRelation.Interior) continue;
          if (!f.Srf.Evaluate(u, v, 1, out Point3d P, out Vector3d[] d) || d == null || d.Length < 2) continue;
          var N = f.Face.NormalAt(u, v);
          if (!N.Unitize()) continue;

          double nx, ny;
          if (o.Mapping == Mapping.Projection)
          {
            proj.ClosestParameter(P, out double s, out double t);
            nx = (s - s0) / (s1 - s0); ny = (t - t0) / (t1 - t0);
          }
          else
          {
            nx = (u - f.U0) / (f.U1 - f.U0); ny = (v - f.V0) / (f.V1 - f.V0);
          }

          double dark = 1;
          if (img != null)
          {
            if (!ToImage(nx, ny, regionAspect, img, o, out double ix, out double iy)) continue;
            double b = img.SampleArea(ix, iy, rx, ry);
            dark = o.Invert ? b : 1 - b;
          }
          dark = Math.Pow(Math.Max(0, Math.Min(1, dark)), Math.Max(0.05, o.Gamma));

          double size;
          if (o.Tone == ToneMode.Threshold || img == null)
          {
            if (img != null && dark < o.Threshold) continue;
            size = maxS;
          }
          else
          {
            if (dark < o.Cutoff) continue;
            double k = o.Cutoff >= 1 ? 1 : (dark - o.Cutoff) / (1 - o.Cutoff);
            size = Math.Sqrt(minS * minS + (maxS * maxS - minS * minS) * k);   // hole area ∝ darkness
          }
          if (size < tol * 10) continue;

          var X = d[0] - N * (d[0] * N);
          if (!X.Unitize()) { X = d[1] - N * (d[1] * N); X = Vector3d.CrossProduct(X, N); if (!X.Unitize()) continue; }
          if (Math.Abs(o.Angle) > 1e-9) X.Rotate(o.Angle * Math.PI / 180, N);
          var h = new Hole { Target = f.Target, Face = f.FaceIndex, U = u, V = v, P = P, N = N, X = X, Size = size, Shape = o.Shape };
          h.Len = o.Shape == HoleShape.Slot ? size * ratio : size;

          // Edge margin, measured from the hole's outline.
          double need = o.Margin + h.R;
          bool near = false;
          foreach (var e in f.Edges)
          {
            if (!e.ClosestPoint(P, out double et, need + tol)) continue;
            near = true; break;
          }
          if (near) { res.RemovedMargin++; continue; }
          holes.Add(h);
        }
      }
    done:
      res.Candidates = cand;
      if (ct.IsCancellationRequested) return null;
      res.Holes = RemoveOverlaps(holes, Math.Max(0, o.MinWall), out int removed);
      res.RemovedOverlap = removed;
      res.Ms = sw.ElapsedMilliseconds;
      return res;
    }

    /// <summary>Keeps bigger holes first and drops any that would leave less than <paramref name="wall"/> to a kept one.</summary>
    static List<Hole> RemoveOverlaps(List<Hole> holes, double wall, out int removed)
    {
      removed = 0;
      if (holes.Count == 0) return holes;
      double maxR = holes.Max(h => h.R);
      double cell = Math.Max(2 * maxR + wall, 1e-6);
      var grid = new Dictionary<(long, long, long), List<Hole>>();
      var kept = new List<Hole>(holes.Count);
      foreach (var h in holes.OrderByDescending(h => h.Size))
      {
        var key = (Cell(h.P.X, cell), Cell(h.P.Y, cell), Cell(h.P.Z, cell));
        bool ok = true;
        for (long dx = -1; dx <= 1 && ok; dx++)
          for (long dy = -1; dy <= 1 && ok; dy++)
            for (long dz = -1; dz <= 1 && ok; dz++)
              if (grid.TryGetValue((key.Item1 + dx, key.Item2 + dy, key.Item3 + dz), out var bucket))
                foreach (var q in bucket)
                  if (SegSegDistance(h.A, h.B, q.A, q.B) < h.CapR + q.CapR + wall - 1e-9) { ok = false; break; }
        if (!ok) { removed++; continue; }
        if (!grid.TryGetValue(key, out var bl)) grid[key] = bl = new List<Hole>();
        bl.Add(h);
        kept.Add(h);
      }
      // Back to a stable, row-like order (nicer for previews and booleans).
      var order = holes.Select((h, i) => (h, i)).ToDictionary(x => x.h, x => x.i);
      kept.Sort((a, b) => order[a].CompareTo(order[b]));
      return kept;
    }

    static double SegSegDistance(Point3d p1, Point3d q1, Point3d p2, Point3d q2)
    {
      Vector3d d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
      double a = d1 * d1, e = d2 * d2, f = d2 * r;
      double s, t;
      if (a <= 1e-12 && e <= 1e-12) return p1.DistanceTo(p2);
      if (a <= 1e-12) { s = 0; t = Clamp01(f / e); }
      else
      {
        double c = d1 * r;
        if (e <= 1e-12) { t = 0; s = Clamp01(-c / a); }
        else
        {
          double b = d1 * d2, den = a * e - b * b;
          s = den > 1e-12 ? Clamp01((b * f - c * e) / den) : 0;
          t = (b * s + f) / e;
          if (t < 0) { t = 0; s = Clamp01(-c / a); }
          else if (t > 1) { t = 1; s = Clamp01((b - c) / a); }
        }
      }
      return (p1 + d1 * s).DistanceTo(p2 + d2 * t);
    }

    static double Clamp01(double x) => x < 0 ? 0 : x > 1 ? 1 : x;

    // ───────────────────────── hole outlines ─────────────────────────

    /// <summary>Outline points in the hole's tangent plane, lifted by <paramref name="lift"/> along the normal.</summary>
    public static List<Point3d> Outline(Hole h, double lift = 0, int circleSegs = 24)
    {
      var o = h.P + h.N * lift;
      var y = Vector3d.CrossProduct(h.N, h.X);
      var pts = new List<Point3d>();
      double r = h.Size / 2;
      switch (h.Shape)
      {
        case HoleShape.Hexagon:
          for (int k = 0; k < 6; k++)
          {
            double a0 = Math.PI / 6 + k * Math.PI / 3, a1 = a0 + Math.PI / 3;
            var c0 = o + h.X * (r * Math.Cos(a0)) + y * (r * Math.Sin(a0));
            var c1 = o + h.X * (r * Math.Cos(a1)) + y * (r * Math.Sin(a1));
            for (int s = 0; s < 3; s++) pts.Add(c0 + (c1 - c0) * (s / 3.0));
          }
          break;
        case HoleShape.Slot:
          {
            double a = (h.Len - h.Size) / 2;
            int half = Math.Max(6, circleSegs / 2);
            for (int i = 0; i <= half; i++)
            {
              double t = -Math.PI / 2 + Math.PI * i / half;
              pts.Add(o + h.X * (a + r * Math.Cos(t)) + y * (r * Math.Sin(t)));
            }
            for (int i = 1; i < 4; i++) pts.Add(o + h.X * (a - 2 * a * i / 4) + y * r);
            for (int i = 0; i <= half; i++)
            {
              double t = Math.PI / 2 + Math.PI * i / half;
              pts.Add(o + h.X * (-a + r * Math.Cos(t)) + y * (r * Math.Sin(t)));
            }
            for (int i = 1; i < 4; i++) pts.Add(o + h.X * (-a + 2 * a * i / 4) - y * r);
          }
          break;
        default:
          for (int i = 0; i < circleSegs; i++)
          {
            double t = 2 * Math.PI * i / circleSegs;
            pts.Add(o + h.X * (r * Math.Cos(t)) + y * (r * Math.Sin(t)));
          }
          break;
      }
      return pts;
    }

    /// <summary>Exact planar outline curve in the tangent plane (used for boolean cutters).</summary>
    public static Curve PlanarCurve(Hole h, double lift)
    {
      var o = h.P + h.N * lift;
      var y = Vector3d.CrossProduct(h.N, h.X);
      var pl = new Plane(o, h.X, y);
      double r = h.Size / 2;
      switch (h.Shape)
      {
        case HoleShape.Hexagon:
          {
            var pts = new List<Point3d>();
            for (int k = 0; k <= 6; k++) { double a = Math.PI / 6 + k * Math.PI / 3; pts.Add(pl.PointAt(r * Math.Cos(a), r * Math.Sin(a))); }
            return new PolylineCurve(pts);
          }
        case HoleShape.Slot:
          {
            double a = (h.Len - h.Size) / 2;
            if (a < 1e-6) return new Circle(pl, r).ToNurbsCurve();
            var pc = new PolyCurve();
            pc.Append(new Arc(pl.PointAt(a, -r), pl.PointAt(a + r, 0), pl.PointAt(a, r)));
            pc.Append(new Line(pl.PointAt(a, r), pl.PointAt(-a, r)));
            pc.Append(new Arc(pl.PointAt(-a, r), pl.PointAt(-a - r, 0), pl.PointAt(-a, -r)));
            pc.Append(new Line(pl.PointAt(-a, -r), pl.PointAt(a, -r)));
            return pc;
          }
        default:
          return new Circle(pl, r).ToNurbsCurve();
      }
    }

    /// <summary>Surface point → (u, v) by Newton steps from a nearby start; fast and local.</summary>
    static bool ToUV(Surface s, Point3d q, double u0, double v0, out double u, out double v)
    {
      u = u0; v = v0;
      var du0 = s.Domain(0); var dv0 = s.Domain(1);
      for (int i = 0; i < 8; i++)
      {
        if (!s.Evaluate(u, v, 1, out Point3d p, out Vector3d[] d) || d == null || d.Length < 2) return false;
        var r = q - p;
        double a = d[0] * d[0], b = d[0] * d[1], c = d[1] * d[1], e = r * d[0], f = r * d[1];
        double det = a * c - b * b;
        if (Math.Abs(det) < 1e-20) return false;
        double du = (c * e - b * f) / det, dv = (a * f - b * e) / det;
        u = Math.Max(du0.Min, Math.Min(du0.Max, u + du));
        v = Math.Max(dv0.Min, Math.Min(dv0.Max, v + dv));
        if (Math.Abs(du) < 1e-12 * (1 + Math.Abs(u)) && Math.Abs(dv) < 1e-12 * (1 + Math.Abs(v))) break;
      }
      return true;
    }

    /// <summary>The hole outline as a closed curve in the face's (u, v) parameter space.</summary>
    static Curve UVCurve(Hole h, Surface s)
    {
      var pts = Outline(h, 0, 32);
      var uv = new List<Point3d>(pts.Count + 1);
      foreach (var p in pts)
      {
        if (!ToUV(s, p, h.U, h.V, out double u, out double v)) return null;
        uv.Add(new Point3d(u, v, 0));
      }
      uv.Add(uv[0]);
      Curve c = h.Shape == HoleShape.Hexagon
        ? (Curve)new PolylineCurve(FilterCorners(uv))
        : Curve.CreateInterpolatedCurve(uv, 3, CurveKnotStyle.ChordPeriodic);
      return c;
    }

    /// <summary>Hexagon: keep only the six corners (every third point) so edges stay straight in UV.</summary>
    static List<Point3d> FilterCorners(List<Point3d> uv)
    {
      var r = new List<Point3d>();
      for (int i = 0; i < uv.Count - 1; i += 3) r.Add(uv[i]);
      r.Add(r[0]);
      return r;
    }

    // ───────────────────────── cutting ─────────────────────────

    public static CutMode Resolve(CutMode m, PerfTarget t) => m == CutMode.Auto ? (t.IsSolid ? CutMode.Boolean : CutMode.Trim) : m;

    /// <summary>Adds each hole as an inner trim loop of its face. No intersections, so it is fast even for thousands of holes.</summary>
    public static Brep TrimHoles(PerfTarget t, IEnumerable<Hole> holes, double tol, out int failed)
    {
      failed = 0;
      var brep = t.Brep.DuplicateBrep();
      foreach (var g in holes.GroupBy(h => h.Face))
      {
        var face = brep.Faces[g.Key];
        var srf = face.UnderlyingSurface();
        foreach (var h in g)
        {
          var c2 = UVCurve(h, srf);
          if (c2 == null || !c2.IsClosed) { failed++; continue; }
          if (c2.ClosedCurveOrientation(Plane.WorldXY) != CurveOrientation.Clockwise) c2.Reverse();
          var c3 = srf.Pushup(c2, tol * 0.5);
          if (c3 == null || !c3.IsClosed) { failed++; continue; }
          int i2 = brep.AddTrimCurve(c2);
          int i3 = brep.AddEdgeCurve(c3);
          var vtx = brep.Vertices.Add(c3.PointAtStart, tol);
          var edge = brep.Edges.Add(vtx.VertexIndex, vtx.VertexIndex, i3, tol);
          var loop = brep.Loops.Add(BrepLoopType.Inner, face);
          var trim = brep.Trims.Add(edge, false, loop, i2);
          trim.IsoStatus = IsoStatus.None;
          trim.TrimType = BrepTrimType.Boundary;
          trim.SetTolerances(tol, tol);
        }
      }
      brep.SetTrimIsoFlags();
      brep.Compact();
      if (!brep.IsValid) brep.Repair(tol);
      return brep;
    }

    /// <summary>Hole curves lying on the surface (for laser cutting, or to trim by hand).</summary>
    public static List<Curve> SurfaceCurves(PerfTarget t, IEnumerable<Hole> holes, double tol)
    {
      var list = new List<Curve>();
      foreach (var h in holes)
      {
        var srf = t.Brep.Faces[h.Face].UnderlyingSurface();
        var c2 = UVCurve(h, srf);
        var c3 = c2 == null ? null : srf.Pushup(c2, tol * 0.5);
        list.Add(c3 ?? PlanarCurve(h, 0));
      }
      return list;
    }

    /// <summary>
    /// Wall thickness under each hole: a line along −normal intersected with the exact Brep; the first hit
    /// past the hole's own face is the far side of the wall. (A render mesh is not good enough: on convex
    /// faces its chords sit just under the surface and read as a near-zero wall.)
    /// </summary>
    static double Depth(PerfTarget t, Hole h, double tol, double fallback)
    {
      double len = t.Brep.GetBoundingBox(false).Diagonal.Length + 1;
      var line = new LineCurve(h.P + h.N * (10 * tol), h.P - h.N * len);
      if (!Intersection.CurveBrep(line, t.Brep, tol, out Curve[] _, out Point3d[] pts) || pts == null) return fallback;
      double best = double.MaxValue, skip = Math.Max(20 * tol, 0.02);
      foreach (var p in pts)
      {
        double d = (h.P - p) * h.N;          // distance below the hole centre
        if (d > skip && d < best) best = d;
      }
      return best == double.MaxValue ? fallback : best;
    }

    public static List<Brep> Cutters(PerfTarget t, IEnumerable<Hole> holes, double tol)
    {
      var list = new List<Brep>();
      foreach (var h in holes)
      {
        double lift = h.R + 0.5;                                    // clears the surface bulge over the hole
        double depth = Depth(t, h, tol, 6 * h.R + 5);
        double through = depth + Math.Max(0.5, 0.3 * depth);        // stops before the wall behind
        var crv = PlanarCurve(h, lift);
        var srf = Surface.CreateExtrusion(crv, -h.N * (lift + through));
        var b = srf?.ToBrep()?.CapPlanarHoles(tol);
        if (b == null) continue;
        if (b.SolidOrientation == BrepSolidOrientation.Inward) b.Flip();
        list.Add(b);
      }
      return list;
    }

    public static Brep BooleanHoles(PerfTarget t, IList<Hole> holes, double tol, Action<string> progress, out int failed)
    {
      failed = 0;
      var cutters = Cutters(t, holes, tol);
      Brep cur = t.Brep.DuplicateBrep();
      // All at once is fastest; fall back to batches, then to single cutters, when a batch fails.
      var all = Brep.CreateBooleanDifference(new[] { cur }, cutters, tol, false);
      if (all != null && all.Length == 1) return all[0];
      const int batch = 120;
      for (int i = 0; i < cutters.Count; i += batch)
      {
        progress?.Invoke($"불리언 {i}/{cutters.Count}");
        var part = cutters.Skip(i).Take(batch).ToList();
        var r = Brep.CreateBooleanDifference(new[] { cur }, part, tol, false);
        if (r != null && r.Length == 1) { cur = r[0]; continue; }
        foreach (var c in part)
        {
          var r1 = Brep.CreateBooleanDifference(cur, c, tol, false);
          if (r1 != null && r1.Length == 1) cur = r1[0]; else failed++;
        }
      }
      return cur;
    }
  }
}
