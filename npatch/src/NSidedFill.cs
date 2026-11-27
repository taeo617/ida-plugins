using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Rhino.Geometry;

namespace NPatch
{
  public enum Continuity { G0 = 0, G1 = 1, G2 = 2 }

  /// <summary>Settings for one fill.</summary>
  public class FillOptions
  {
    public int Degree = 3;
    /// <summary>Spans per direction of the base NURBS patch.</summary>
    public int SpanCount = 10;
    /// <summary>Raise spans automatically until every edge's gap is within tolerance.</summary>
    public bool AutoRefine = true;
    /// <summary>0 = smoothest (min. bending), 1 = taut (adds membrane / area energy).</summary>
    public double Tautness = 0;
    public int SamplesPerEdge = 40;
    public double PositionWeight = 1e7;
    public double TangentWeight = 1e3;
    public double CurvatureWeight = 10;
    /// <summary>G2 re-linearisation passes after the initial G1 solve.</summary>
    public int CurvatureIterations = 3;
    public bool Trim = true;
    /// <summary>Fill 4-sided holes with an untrimmed surface whose border is the boundary itself.</summary>
    public bool PreferUntrimmed = true;
    /// <summary>Extra points the patch must pass through (G0).</summary>
    public List<Point3d> InteriorPoints = new List<Point3d>();

    // Pass/fail thresholds for the per-edge report.
    public double AngleToleranceDeg = 0.1;
    public double CurvatureTolerancePct = 5;

    public FillOptions Clone()
    {
      var o = (FillOptions)MemberwiseClone();
      o.InteriorPoints = new List<Point3d>(InteriorPoints);
      return o;
    }
  }

  /// <summary>One boundary piece: a curve, optionally with the face it must stay continuous to.</summary>
  public class BoundaryInput
  {
    public Curve Curve;
    public BrepFace AdjacentFace; // null => position only on this piece
    public Continuity Continuity = Continuity.G1;
    /// <summary>Document object the edge came from (Guid.Empty for curves). Used by the preview only.</summary>
    public Guid SourceObjectId;

    /// <summary>What can actually be honoured: no neighbour face means G0.</summary>
    public Continuity Effective => AdjacentFace == null ? Continuity.G0 : Continuity;

    /// <summary>
    /// Optional exact surface normals wanted at the curve's start / end (Unset = none). The face's normal is
    /// corrected toward them with a smooth falloff along the curve. Used for inner cuts, whose ends sit on
    /// the hole's edges: there the normal is fixed by the hole, not by the guide face the cut follows.
    /// </summary>
    internal Vector3d StartNormal = Vector3d.Unset, EndNormal = Vector3d.Unset;

    /// <summary>An inner seam between two pieces of one fill (not an edge of the hole).</summary>
    internal bool Inner;

    /// <summary>The face normal at <paramref name="pt"/> (a point on the curve), with the end corrections.</summary>
    internal Vector3d TargetNormal(Point3d pt, Vector3d faceNormal)
    {
      var n = faceNormal; n.Unitize();
      if (!StartNormal.IsValid && !EndNormal.IsValid) return n;
      var dom = Curve.Domain;
      if (!Curve.ClosestPoint(pt, out double t)) return n;
      double f = Curve.GetLength(new Interval(dom.Min, t)) / Math.Max(Curve.GetLength(), 1e-12);
      f = Math.Max(0, Math.Min(1, f));
      double w0 = (1 - f) * (1 - f) * (1 + 2 * f), w1 = 1 - w0;   // Hermite: flat at both ends
      Vector3d Delta(Vector3d want, Point3d at)
      {
        if (!want.IsValid || !AdjacentFace.ClosestPoint(at, out double u, out double v)) return Vector3d.Zero;
        var g = AdjacentFace.NormalAt(u, v); g.Unitize();
        var w = want; w.Unitize();
        if (w * g < 0) w = -w;
        if (g * n < 0) g = -g;
        return w - g;
      }
      var r = n + w0 * Delta(StartNormal, Curve.PointAtStart) + w1 * Delta(EndNormal, Curve.PointAtEnd);
      return r.Unitize() ? r : n;
    }
  }

  /// <summary>Measured quality on one boundary piece. "Corner" values are the ends of the edge.</summary>
  public class EdgeReport
  {
    public int Index;
    public Continuity Requested, Effective;
    public double Gap, CornerGap;                       // model units
    public double AngleDeg, CornerAngleDeg;             // G1+
    public double CurvaturePct, CornerCurvaturePct;     // G2: |k_patch - k_nbr| as % of neighbour curvature
    public Point3d LabelPoint;
    public bool Ok;
  }

  public class FillResult
  {
    public Brep Brep;
    public NurbsSurface Surface;
    public List<EdgeReport> Edges = new List<EdgeReport>();
    public double MaxPositionError;
    public int SpanCount;
    public int Degree;
    public long Milliseconds;
    public string Message;
    /// <summary>True when the fill is made of untrimmed surface(s) built on the boundary itself.</summary>
    public bool Untrimmed;
    /// <summary>All surfaces of the fill (one, or one per 4-sided piece when an N-sided hole was split).</summary>
    public List<NurbsSurface> Surfaces = new List<NurbsSurface>();
    /// <summary>Largest angle between neighbouring pieces along the inner seams (split fills only).</summary>
    public double InnerSeamAngleDeg;
    /// <summary>Same, within the corner zones at the seams' ends (split fills only).</summary>
    public double InnerSeamCornerAngleDeg;
    /// <summary>Largest normal-curvature difference across the inner seams, % of the curvature there (split fills only).</summary>
    public double InnerSeamCurvaturePct;
    public bool AllOk => Brep != null && Edges.All(e => e.Ok);
  }

  /// <summary>
  /// N-sided hole filler. Builds one NURBS patch over the boundary's best-fit plane and solves for
  /// its control points by least squares: minimal thin-plate bending energy (optionally blended with
  /// membrane energy for a tauter result), subject to heavily weighted boundary position (G0),
  /// tangency (G1) and normal curvature (G2) to the neighbour faces, per edge. G2 is non-linear and
  /// is linearised around the previous pass. The patch is then trimmed by the boundary loop.
  /// </summary>
  public static class NSidedFill
  {
    /// <summary>Max spans the adaptive refinement will go to.</summary>
    public const int MaxSpans = 28;
    internal static bool ReuseFactor = true;
    internal const double CornerFraction = 0.12; // first/last 12% of each edge counts as "corner"

    public static FillResult Solve(IList<BoundaryInput> inputs, FillOptions opt, double tol)
    {
      var sw = Stopwatch.StartNew();
      bool untrimmedOk = opt.PreferUntrimmed && opt.InteriorPoints.Count == 0;
      // Candidates in order of preference: untrimmed 4-sided, untrimmed pieces, trimmed.
      var cands = new List<FillResult>();
      FillResult Done(FillResult r) { r.Milliseconds = sw.ElapsedMilliseconds; return r; }

      // 4-sided holes: untrimmed surface built on the boundary itself.
      if (untrimmedOk)
      {
        var q = QuadFill.Prepare(inputs, tol);
        if (q != null)
        {
          var rq = Refine(o => QuadFill.Solve(q, inputs, o, tol), opt, tol);
          if (rq.Brep != null)
          {
            if (rq.AllOk) return Done(rq);
            cands.Add(rq);
          }
        }
      }
      var r0 = Refine(o => SolveOnce(inputs, o, tol), opt, tol);
      // 3- or 5+-sided holes: untrimmed 4-sided pieces, guided by the trimmed solution.
      if (untrimmedOk && r0.Brep != null && r0.Surface != null)
      {
        var rs = TrySplit(inputs, r0.Surface, opt, tol);
        if (rs != null && rs.Brep != null) cands.Add(rs);
      }
      cands.Add(r0);

      // First one whose every edge passes; if none does, the one that misses by the least
      // (so an untrimmed result is never kept when the trimmed one is clearly better).
      var ok = cands.FirstOrDefault(c => c.AllOk);
      if (ok != null) return Done(ok);
      var valid = cands.Where(c => c.Brep != null).ToList();
      if (valid.Count == 0) return Done(r0);
      return Done(valid.OrderBy(c => Badness(c, opt, tol)).First());
    }

    /// <summary>How far the worst edge is from passing (≤ 1 = passes), measured like <see cref="EdgeReport.Ok"/>.</summary>
    internal static double Badness(FillResult r, FillOptions opt, double tol)
    {
      double worst = 0;
      foreach (var e in r.Edges)
      {
        worst = Math.Max(worst, Math.Max(e.Gap, e.CornerGap) / Math.Max(tol, 1e-9));
        if (e.Effective != Continuity.G0) worst = Math.Max(worst, e.AngleDeg / Math.Max(opt.AngleToleranceDeg, 1e-9));
        if (e.Effective == Continuity.G2) worst = Math.Max(worst, e.CurvaturePct / Math.Max(opt.CurvatureTolerancePct, 1e-9));
      }
      return worst;
    }

    internal static FillResult TrySplit(IList<BoundaryInput> inputs, NurbsSurface guide, FillOptions opt, double tol)
    {
      try
      {
        var sides = QuadFill.FindSides(inputs, tol);
        if (sides == null || sides.Count == 4 || sides.Count < 3 || sides.Count > SplitFill.MaxSides) return null;
        return SplitFill.Solve(inputs, sides, guide, opt, tol);
      }
      catch { return null; }
    }

    /// <summary>
    /// Raises the span count until every edge passes (gap, angle and curvature, see <see cref="Badness"/>),
    /// or until more spans stop helping.
    /// </summary>
    internal static FillResult Refine(Func<FillOptions, FillResult> once, FillOptions opt, double tol)
    {
      int spans = Math.Max(2, opt.SpanCount);
      FillResult best = null;
      double bestBad = double.MaxValue;
      while (true)
      {
        var o = opt.Clone();
        o.SpanCount = spans;
        var r = once(o);
        if (r.Brep == null) { best = best ?? r; break; }
        double bad = Badness(r, opt, tol);
        if (best != null && bad > 0.85 * bestBad)
        {
          // Refining no longer helps (error limited by e.g. sharp corners, not resolution).
          if (bad < bestBad) best = r;
          break;
        }
        best = r; bestBad = bad;
        if (!opt.AutoRefine || bad <= 1 || spans >= MaxSpans) break;
        spans = Math.Min(MaxSpans, (int)Math.Ceiling(spans * 1.4));
      }
      return best;
    }

    internal class Sample
    {
      public Point3d P;
      public int Edge;
      public bool Corner;
      public double F;                     // arc-length fraction along its edge, 0..1
      public Continuity C;
      public Vector3d N = Vector3d.Unset;  // neighbour normal
      public Vector3d T;                   // boundary tangent
      public BrepFace Face;
      public Point2d FaceUV;
      public Point3d Local;
    }

    /// <summary>
    /// G2 weight factor near corners: 0 at the corner, easing to 1 by 15% along the edge.
    /// At a corner the two neighbours usually ask for different curvature; forcing both there
    /// makes the patch bulge, so G2 fades in smoothly instead.
    /// </summary>
    internal static double CornerRamp(double f)
    {
      double d = Math.Min(f, 1 - f) / 0.15;
      if (d >= 1) return 1;
      d = Math.Max(0, d);
      return d * d * (3 - 2 * d);
    }

    internal static List<Sample> BuildSamples(IList<BoundaryInput> inputs, int perEdge)
    {
      var samples = new List<Sample>();
      for (int e = 0; e < inputs.Count; e++)
      {
        var b = inputs[e];
        int count = Math.Max(4, perEdge);
        var ts = b.Curve.DivideByCount(count, true);
        if (ts == null) continue;
        var dom = b.Curve.Domain;
        double len = Math.Max(b.Curve.GetLength(), 1e-12);
        foreach (double t in ts)
        {
          double f = b.Curve.GetLength(new Interval(dom.Min, t)) / len;
          var s = new Sample
          {
            P = b.Curve.PointAt(t), T = b.Curve.TangentAt(t), Edge = e, F = f,
            Corner = f < CornerFraction || f > 1 - CornerFraction, C = b.Effective
          };
          if (s.C != Continuity.G0 && b.AdjacentFace.ClosestPoint(s.P, out double fu, out double fv))
          {
            s.N = b.AdjacentFace.NormalAt(fu, fv); s.N.Unitize();
            s.Face = b.AdjacentFace; s.FaceUV = new Point2d(fu, fv);
          }
          else s.C = Continuity.G0;
          samples.Add(s);
        }
      }
      return samples;
    }

    static FillResult SolveOnce(IList<BoundaryInput> inputs, FillOptions opt, double tol)
    {
      Prof.Trimmed++;
      var res = new FillResult { SpanCount = opt.SpanCount, Degree = opt.Degree };
      if (inputs == null || inputs.Count == 0) { res.Message = "No boundary curves."; return res; }

      // 1. Closed boundary loop.
      var joined = Curve.JoinCurves(inputs.Select(b => b.Curve), Math.Max(tol * 10, 1e-6));
      if (joined.Length != 1 || !joined[0].IsClosed)
      {
        res.Message = $"Boundary must form exactly one closed loop (got {joined.Length} open piece(s)).";
        return res;
      }
      Curve loop = joined[0];

      // 2. Boundary samples.
      var samples = BuildSamples(inputs, opt.SamplesPerEdge);
      if (samples.Count < 6) { res.Message = "Too few boundary samples."; return res; }

      // 3. Parameterisation: best-fit plane turned to the hole's main direction, and a rectangle around
      //    the loop. Long narrow holes get spans along their length instead of wasting most of a square.
      if (Plane.FitPlaneToPoints(samples.Select(s => s.P), out Plane plane) != PlaneFitResult.Success)
      { res.Message = "Could not fit a plane to the boundary."; return res; }
      {
        var xy = samples.Select(s => { plane.ClosestParameter(s.P, out double x, out double y); return (x, y); }).ToList();
        double mx = xy.Average(t => t.x), my = xy.Average(t => t.y), sxx = 0, syy = 0, sxy = 0;
        foreach (var (x, y) in xy) { sxx += (x - mx) * (x - mx); syy += (y - my) * (y - my); sxy += (x - mx) * (y - my); }
        plane.Rotate(0.5 * Math.Atan2(2 * sxy, sxx - syy), plane.ZAxis);
      }
      double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
      foreach (var s in samples)
      {
        plane.ClosestParameter(s.P, out double x, out double y);
        minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
        minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
      }
      double D0 = Math.Max(maxX - minX, maxY - minY);
      if (D0 < tol) { res.Message = "Boundary is degenerate."; return res; }
      double cx = 0.5 * (minX + maxX), cy = 0.5 * (minY + maxY);
      double Dx = maxX - minX + 0.16 * D0, Dy = maxY - minY + 0.16 * D0; // 8% padding each side
      double D = Math.Max(Dx, Dy);
      double ax = Dx / D, ay = Dy / D;   // rectangle size in local units (the longer side is 1)
      minX = cx - 0.5 * Dx; minY = cy - 0.5 * Dy;

      Point3d ToLocal(Point3d p)
      {
        var v = p - plane.Origin;
        return new Point3d((v * plane.XAxis - minX) / D, (v * plane.YAxis - minY) / D, (v * plane.ZAxis) / D);
      }
      Point3d ToWorld(Point3d q) =>
        plane.Origin + plane.XAxis * (q.X * D + minX) + plane.YAxis * (q.Y * D + minY) + plane.ZAxis * (q.Z * D);
      foreach (var s in samples) s.Local = ToLocal(s.P);
      var interior = opt.InteriorPoints.Select(ToLocal).ToList();

      int p_ = Math.Max(2, Math.Min(7, opt.Degree)), sp = Math.Max(2, opt.SpanCount);
      int minSp = Math.Min(sp, 4);
      int spU = Math.Max(minSp, (int)Math.Ceiling(sp * ax - 1e-9)), spV = Math.Max(minSp, (int)Math.Ceiling(sp * ay - 1e-9));
      int nU = spU + p_, nV = spV + p_;
      double[] U = ClampedUniformKnots(nU, p_), V = ClampedUniformKnots(nV, p_);
      int Idx(int i, int j, int c) => (i * nV + j) * 3 + c;

      // 4. Solve: one linear pass, plus G2 passes linearised around the previous result.
      bool anyG2 = samples.Any(s => s.C == Continuity.G2);
      int passes = anyG2 ? 1 + Math.Max(1, opt.CurvatureIterations) : 1;
      NurbsSurface srf = null;
      // From the 2nd pass on only the G2 targets change, so the factored matrix is kept and reused.
      BandMatrix kept = null; int keptG2 = -1;
      for (int pass = 0; pass < passes; pass++)
      {
        var sol = Assemble(srf, kept != null) ?? (kept != null ? Assemble(srf, false) : null);
        if (sol == null) { res.Message = "Solver failed (matrix not positive definite)."; return res; }
        srf = NurbsSurface.Create(3, false, p_ + 1, p_ + 1, nU, nV);
        for (int k = 1; k < U.Length - 1; k++) srf.KnotsU[k - 1] = U[k];
        for (int k = 1; k < V.Length - 1; k++) srf.KnotsV[k - 1] = V[k];
        for (int i = 0; i < nU; i++)
          for (int j = 0; j < nV; j++)
            srf.Points.SetPoint(i, j, ToWorld(new Point3d(sol[Idx(i, j, 0)], sol[Idx(i, j, 1)], sol[Idx(i, j, 2)])));
      }
      res.Surface = srf; res.Surfaces.Add(srf);

      // 5. Per-edge quality report.
      BuildReport(res, inputs, samples, srf, D, opt, tol);

      // 6. Trim by the boundary loop.
      var brep = srf.ToBrep();
      if (opt.Trim)
      {
        var trimmed = TrimToLoop(brep, loop, plane, Math.Max(tol, res.MaxPositionError * 1.5));
        if (trimmed != null) brep = trimmed;
        else res.Message = "Trim failed; showing the untrimmed patch.";
      }
      res.Brep = brep;
      return res;


      // Builds and solves the least-squares system. `prev` (world-space surface) linearises the
      // G2 rows; null on the first pass.
      // rhsOnly: reuse `kept` and only build the right-hand side; null if the G2 rows differ from it.
      double[] Assemble(NurbsSurface prev, bool rhsOnly)
      {
        int N = nU * nV * 3;
        int bw = 3 * (p_ * nV + p_ + 1); // half bandwidth of M with this unknown ordering
        var M = rhsOnly ? kept : new BandMatrix(N, bw);
        int g2Rows = 0;
        var rhs = new double[N];
        var rowIdx = new int[3 * (p_ + 1) * (p_ + 1)];
        var rowVal = new double[rowIdx.Length];
        void AddRow(int count, double target, double w)
        {
          for (int a = 0; a < count; a++)
          {
            double wa = w * rowVal[a];
            int ia = rowIdx[a];
            rhs[ia] += wa * target;
            if (!rhsOnly) for (int b = 0; b < count; b++) M.Add(ia, rowIdx[b], wa * rowVal[b]);
          }
        }
        // Row = sum_ab Bu[du,a] Bv[dv,b] * (n . P_ab) when n != null, else component c only.
        int FillRow(int su, double[,] Bu, int du, int sv, double[,] Bv, int dv, double[] n, int c)
        {
          int k = 0;
          for (int a = 0; a <= p_; a++)
            for (int b = 0; b <= p_; b++)
            {
              double bv = Bu[du, a] * Bv[dv, b];
              if (n == null) { rowIdx[k] = Idx(su - p_ + a, sv - p_ + b, c); rowVal[k++] = bv; }
              else for (int cc = 0; cc < 3; cc++) { rowIdx[k] = Idx(su - p_ + a, sv - p_ + b, cc); rowVal[k++] = bv * n[cc]; }
            }
          return k;
        }

        // a. Bending (thin plate) + membrane (tautness) energy by quadrature on a grid.
        //    Derivatives are taken in local length units (x = ax u, y = ay v), so a rectangle behaves
        //    like the square always did.
        int Gu = 3 * spU, Gv = 3 * spV;
        double wE = ax * ay / (Gu * Gv);
        double wM = wE * 60 * opt.Tautness * opt.Tautness;
        double a2 = ax * ax, b2 = ay * ay;
        for (int gi = 0; gi < (rhsOnly ? 0 : Gu); gi++)   // zero targets: nothing for the right-hand side
          for (int gj = 0; gj < Gv; gj++)
          {
            Basis(U, nU, p_, (gi + 0.5) / Gu, 2, out int su, out double[,] Bu);
            Basis(V, nV, p_, (gj + 0.5) / Gv, 2, out int sv, out double[,] Bv);
            for (int c = 0; c < 3; c++)
            {
              AddRow(FillRow(su, Bu, 2, sv, Bv, 0, null, c), 0, wE / (a2 * a2));
              AddRow(FillRow(su, Bu, 1, sv, Bv, 1, null, c), 0, 2 * wE / (a2 * b2));
              AddRow(FillRow(su, Bu, 0, sv, Bv, 2, null, c), 0, wE / (b2 * b2));
              if (wM > 0 && c == 2)
              {
                // Membrane on the out-of-plane part only, so tautness doesn't distort the parameterisation.
                AddRow(FillRow(su, Bu, 1, sv, Bv, 0, null, c), 0, wM / a2);
                AddRow(FillRow(su, Bu, 0, sv, Bv, 1, null, c), 0, wM / b2);
              }
            }
          }

        // b. Tiny regularisation toward the flat patch S(u,v) = (u, v, 0) so the system is SPD.
        var grevU = Greville(U, nU, p_); var grevV = Greville(V, nV, p_);
        const double wReg = 1e-8;
        for (int i = 0; i < nU; i++)
          for (int j = 0; j < nV; j++)
          {
            double[] tgt = { grevU[i] * ax, grevV[j] * ay, 0 };
            for (int c = 0; c < 3; c++) { int ix = Idx(i, j, c); if (!rhsOnly) M.Add(ix, ix, wReg); rhs[ix] += wReg * tgt[c]; }
          }

        // c. Boundary constraints.
        foreach (var s in samples)
        {
          var q = s.Local;
          double pu = Math.Max(0, Math.Min(1, q.X / ax)), pv = Math.Max(0, Math.Min(1, q.Y / ay));
          Basis(U, nU, p_, pu, 2, out int su, out double[,] Bu);
          Basis(V, nV, p_, pv, 2, out int sv, out double[,] Bv);

          double[] qc = { q.X, q.Y, q.Z };
          for (int c = 0; c < 3; c++) AddRow(FillRow(su, Bu, 0, sv, Bv, 0, null, c), qc[c], opt.PositionWeight);
          if (s.C == Continuity.G0) continue;

          double[] nl = { s.N * plane.XAxis, s.N * plane.YAxis, s.N * plane.ZAxis };
          AddRow(FillRow(su, Bu, 1, sv, Bv, 0, nl, 0), 0, opt.TangentWeight);
          AddRow(FillRow(su, Bu, 0, sv, Bv, 1, nl, 0), 0, opt.TangentWeight);

          // G2: n . S_xy = II_neighbour(S_x, S_y), using S_x from the previous pass.
          if (s.C == Continuity.G2 && prev != null &&
              prev.Evaluate(pu, pv, 1, out Point3d _, out Vector3d[] pd) &&
              SecondForm(s.Face, s.FaceUV.X, s.FaceUV.Y, s.N, out var II))
          {
            // World second derivatives = D * local ones, so local target = world target / D.
            double wk = opt.CurvatureWeight * CornerRamp(s.F);
            if (wk > 0)
            {
              g2Rows++;
              AddRow(FillRow(su, Bu, 2, sv, Bv, 0, nl, 0), II(pd[0], pd[0]) / D, wk);
              AddRow(FillRow(su, Bu, 1, sv, Bv, 1, nl, 0), II(pd[0], pd[1]) / D, wk);
              AddRow(FillRow(su, Bu, 0, sv, Bv, 2, nl, 0), II(pd[1], pd[1]) / D, wk);
            }
          }
        }

        // d. Interior pass-through points.
        foreach (var q in interior)
        {
          double pu = q.X / ax, pv = q.Y / ay;
          if (pu < 0 || pu > 1 || pv < 0 || pv > 1) continue;
          Basis(U, nU, p_, pu, 0, out int su, out double[,] Bu);
          Basis(V, nV, p_, pv, 0, out int sv, out double[,] Bv);
          double[] qc = { q.X, q.Y, q.Z };
          // Only the height is pinned: x/y stay free so the parameterisation isn't pulled around.
          AddRow(FillRow(su, Bu, 0, sv, Bv, 0, null, 2), qc[2], opt.PositionWeight);
        }

        if (rhsOnly && g2Rows != keptG2) return null;
        if (!M.Solve(rhs)) return null;
        if (!rhsOnly && prev != null && ReuseFactor) { kept = M; keptG2 = g2Rows; }
        return rhs;
      }
    }

    /// <summary>Measures gap / angle / curvature difference per edge of <paramref name="srf"/>.</summary>
    internal static void BuildReport(FillResult res, IList<BoundaryInput> inputs, List<Sample> samples, NurbsSurface srf, double size, FillOptions opt, double tol)
      => BuildReport(res, inputs, samples, new[] { srf }, size, opt, tol);

    /// <summary>Per-edge report against several surfaces: each sample is measured on the nearest one.</summary>
    internal static void BuildReport(FillResult res, IList<BoundaryInput> inputs, List<Sample> samples, IList<NurbsSurface> srfs, double size, FillOptions opt, double tol)
    {
      Prof.Report.Start();
      try { BuildReportCore(res, inputs, samples, srfs, size, opt, tol); } finally { Prof.Report.Stop(); }
    }

    static void BuildReportCore(FillResult res, IList<BoundaryInput> inputs, List<Sample> samples, IList<NurbsSurface> srfs, double size, FillOptions opt, double tol)
    {
      NurbsSurface srf = srfs[0];
      bool Nearest(Point3d pt, out double u, out double v)
      {
        u = v = 0; double best = double.MaxValue; bool ok = false;
        foreach (var s in srfs)
        {
          if (!s.ClosestPoint(pt, out double su, out double sv)) continue;
          double d = s.PointAt(su, sv).DistanceTo(pt);
          if (d < best) { best = d; srf = s; u = su; v = sv; ok = true; }
        }
        return ok;
      }
      double kFloor = 0.1 / size; // curvature below this counts as "flat" when forming percentages
      var centroid = new Point3d();
      foreach (var s in samples) centroid += s.P;
      centroid /= samples.Count;
      for (int e = 0; e < inputs.Count; e++)
      {
        var rep = new EdgeReport { Index = e, Requested = inputs[e].Continuity, Effective = inputs[e].Effective };
        var c = inputs[e].Curve;
        // Label sits just inside the hole so neighbouring labels at sharp corners don't collide.
        var mid = c.PointAtNormalizedLength(0.5);
        rep.LabelPoint = mid + (centroid - mid) * 0.15;
        double maxKF = 0, dK = 0, dKc = 0;
        foreach (var s in samples.Where(s => s.Edge == e))
        {
          if (!Nearest(s.P, out double u, out double v)) continue;
          double gap = srf.PointAt(u, v).DistanceTo(s.P);
          if (s.Corner) rep.CornerGap = Math.Max(rep.CornerGap, gap); else rep.Gap = Math.Max(rep.Gap, gap);
          res.MaxPositionError = Math.Max(res.MaxPositionError, gap);
          if (s.C == Continuity.G0) continue;

          double ang = Vector3d.VectorAngle(srf.NormalAt(u, v), s.N) * 180 / Math.PI;
          ang = Math.Min(ang, 180 - ang);
          if (s.Corner) rep.CornerAngleDeg = Math.Max(rep.CornerAngleDeg, ang); else rep.AngleDeg = Math.Max(rep.AngleDeg, ang);
          if (s.C != Continuity.G2) continue;

          var cross = Vector3d.CrossProduct(s.N, s.T); cross.Unitize();
          if (NormalCurvature(srf, u, v, s.N, cross, out double kS) &&
              NormalCurvature(s.Face, s.FaceUV.X, s.FaceUV.Y, s.N, cross, out double kF))
          {
            maxKF = Math.Max(maxKF, Math.Abs(kF));
            if (s.Corner) dKc = Math.Max(dKc, Math.Abs(kS - kF)); else dK = Math.Max(dK, Math.Abs(kS - kF));
          }
        }
        double denom = Math.Max(maxKF, kFloor);
        rep.CurvaturePct = 100 * dK / denom;
        rep.CornerCurvaturePct = 100 * dKc / denom;
        rep.Ok = Math.Max(rep.Gap, rep.CornerGap) <= Math.Max(tol, 1e-9)
              && (rep.Effective == Continuity.G0 || rep.AngleDeg <= opt.AngleToleranceDeg)
              && (rep.Effective != Continuity.G2 || rep.CurvaturePct <= opt.CurvatureTolerancePct);
        res.Edges.Add(rep);
      }
    }

    /// <summary>
    /// Second fundamental form of <paramref name="s"/> at (u,v), measured against normal
    /// <paramref name="n"/>, as a function of two 3D tangent vectors.
    /// </summary>
    public static bool SecondForm(Surface s, double u, double v, Vector3d n, out Func<Vector3d, Vector3d, double> II)
    {
      II = null;
      if (s == null || !s.Evaluate(u, v, 2, out Point3d _, out Vector3d[] d) || d.Length < 5) return false;
      Vector3d Fs = d[0], Ft = d[1];
      double L = n * d[2], Mm = n * d[3], Nn = n * d[4];
      double a11 = Fs * Fs, a12 = Fs * Ft, a22 = Ft * Ft;
      double det = a11 * a22 - a12 * a12;
      if (Math.Abs(det) < 1e-30) return false;
      II = (t1, t2) =>
      {
        // Express t as a*Fs + b*Ft (least squares), then II = [a b] [L M; M N] [a b]^T.
        double r1 = t1 * Fs, r2 = t1 * Ft, s1 = t2 * Fs, s2 = t2 * Ft;
        double x1 = (a22 * r1 - a12 * r2) / det, y1 = (a11 * r2 - a12 * r1) / det;
        double x2 = (a22 * s1 - a12 * s2) / det, y2 = (a11 * s2 - a12 * s1) / det;
        return L * x1 * x2 + Mm * (x1 * y2 + x2 * y1) + Nn * y1 * y2;
      };
      return true;
    }

    /// <summary>Normal curvature of <paramref name="s"/> along unit tangent <paramref name="dir"/>, signed by <paramref name="n"/>.</summary>
    public static bool NormalCurvature(Surface s, double u, double v, Vector3d n, Vector3d dir, out double k)
    {
      k = 0;
      if (!SecondForm(s, u, v, n, out var II)) return false;
      double l2 = dir.SquareLength;
      if (l2 < 1e-24) return false;
      k = II(dir, dir) / l2;
      return true;
    }

    static Brep TrimToLoop(Brep brep, Curve loop, Plane plane, double tol)
    {
      Prof.Trim.Start();
      try { return TrimToLoopCore(brep, loop, plane, tol); } finally { Prof.Trim.Stop(); }
    }

    static Brep TrimToLoopCore(Brep brep, Curve loop, Plane plane, double tol)
    {
      var face = brep.Faces[0];
      var pulled = loop.PullToBrepFace(face, tol);
      if (pulled == null || pulled.Length == 0) return null;
      var split = face.Split(pulled, tol);
      if (split == null || split.Faces.Count < 2) return null;

      // Keep the face whose interior points fall inside the loop (seen in the fit plane).
      // Centroids are unreliable here: the outer piece is a ring whose centroid lies in the hole.
      var flat = Curve.ProjectToPlane(loop, plane);
      BrepFace best = null; double bestScore = 0;
      foreach (var f in split.Faces)
      {
        int inside = 0, total = 0;
        var du = f.Domain(0); var dv = f.Domain(1);
        for (int i = 1; i < 16; i++)
          for (int j = 1; j < 16; j++)
          {
            double u = du.ParameterAt(i / 16.0), v = dv.ParameterAt(j / 16.0);
            if (f.IsPointOnFace(u, v) != PointFaceRelation.Interior) continue;
            total++;
            if (flat.Contains(plane.ClosestPoint(f.PointAt(u, v)), plane, tol) == PointContainment.Inside) inside++;
          }
        double score = total == 0 ? 0 : (double)inside / total;
        if (score > bestScore) { bestScore = score; best = f; }
      }
      if (best == null || bestScore < 0.5) return null;
      var piece = best.DuplicateFace(false);
      piece.Faces.ShrinkFaces();
      return piece;
    }

    // ---------- B-spline helpers ----------

    internal static double[] ClampedUniformKnots(int nCv, int p)
    {
      int m = nCv + p + 1;
      var U = new double[m];
      int inner = nCv - p; // number of spans
      for (int k = 0; k < m; k++)
        U[k] = k <= p ? 0 : k >= nCv ? 1 : (double)(k - p) / inner;
      return U;
    }

    internal static double[] Greville(double[] U, int nCv, int p)
    {
      var g = new double[nCv];
      for (int i = 0; i < nCv; i++)
      {
        double s = 0;
        for (int k = 1; k <= p; k++) s += U[i + k];
        g[i] = s / p;
      }
      return g;
    }

    internal static void Basis(double[] U, int nCv, int p, double t, int nd, out int span, out double[,] ders)
    {
      t = Math.Max(0, Math.Min(1, t));
      span = FindSpan(nCv, p, t, U);
      ders = DersBasis(span, t, p, nd, U);
    }

    static int FindSpan(int nCv, int p, double t, double[] U)
    {
      int n = nCv - 1;
      if (t >= U[n + 1]) return n;
      if (t <= U[p]) return p;
      int lo = p, hi = n + 1, mid = (lo + hi) / 2;
      while (t < U[mid] || t >= U[mid + 1])
      {
        if (t < U[mid]) hi = mid; else lo = mid;
        mid = (lo + hi) / 2;
      }
      return mid;
    }

    // Piegl & Tiller A2.3
    static double[,] DersBasis(int span, double t, int p, int nd, double[] U)
    {
      var ders = new double[nd + 1, p + 1];
      var ndu = new double[p + 1, p + 1];
      var left = new double[p + 1];
      var right = new double[p + 1];
      ndu[0, 0] = 1;
      for (int j = 1; j <= p; j++)
      {
        left[j] = t - U[span + 1 - j];
        right[j] = U[span + j] - t;
        double saved = 0;
        for (int r = 0; r < j; r++)
        {
          ndu[j, r] = right[r + 1] + left[j - r];
          double temp = ndu[r, j - 1] / ndu[j, r];
          ndu[r, j] = saved + right[r + 1] * temp;
          saved = left[j - r] * temp;
        }
        ndu[j, j] = saved;
      }
      for (int j = 0; j <= p; j++) ders[0, j] = ndu[j, p];

      var a = new double[2, p + 1];
      for (int r = 0; r <= p; r++)
      {
        int s1 = 0, s2 = 1;
        a[0, 0] = 1;
        for (int k = 1; k <= nd; k++)
        {
          double d = 0;
          int rk = r - k, pk = p - k;
          if (r >= k) { a[s2, 0] = a[s1, 0] / ndu[pk + 1, rk]; d = a[s2, 0] * ndu[rk, pk]; }
          int j1 = rk >= -1 ? 1 : -rk;
          int j2 = (r - 1 <= pk) ? k - 1 : p - r;
          for (int j = j1; j <= j2; j++)
          {
            a[s2, j] = (a[s1, j] - a[s1, j - 1]) / ndu[pk + 1, rk + j];
            d += a[s2, j] * ndu[rk + j, pk];
          }
          if (r <= pk) { a[s2, k] = -a[s1, k - 1] / ndu[pk + 1, r]; d += a[s2, k] * ndu[r, pk]; }
          ders[k, r] = d;
          int tmp = s1; s1 = s2; s2 = tmp;
        }
      }
      int rr = p;
      for (int k = 1; k <= nd; k++)
      {
        for (int j = 0; j <= p; j++) ders[k, j] *= rr;
        rr *= (p - k);
      }
      return ders;
    }

    // In-place Cholesky of a symmetric banded matrix (dense storage, half bandwidth bw).
    // Fill-in of a banded SPD matrix stays inside the band, so loops can be limited to it.
    internal static bool BandCholeskySolve(double[] A, double[] b, int n, int bw)
    {
      for (int j = 0; j < n; j++)
      {
        int jr = j * n, k0 = Math.Max(0, j - bw);
        double s = A[jr + j];
        for (int k = k0; k < j; k++) s -= A[jr + k] * A[jr + k];
        if (s <= 0) return false;
        double ljj = Math.Sqrt(s);
        A[jr + j] = ljj;
        int iEnd = Math.Min(n - 1, j + bw);
        for (int i = j + 1; i <= iEnd; i++)
        {
          int ir = i * n, kk = Math.Max(k0, i - bw);
          double t = A[ir + j];
          for (int k = kk; k < j; k++) t -= A[ir + k] * A[jr + k];
          A[ir + j] = t / ljj;
        }
      }
      for (int i = 0; i < n; i++)
      {
        int ir = i * n; double t = b[i];
        for (int k = Math.Max(0, i - bw); k < i; k++) t -= A[ir + k] * b[k];
        b[i] = t / A[ir + i];
      }
      for (int i = n - 1; i >= 0; i--)
      {
        double t = b[i];
        int kEnd = Math.Min(n - 1, i + bw);
        for (int k = i + 1; k <= kEnd; k++) t -= A[k * n + i] * b[k];
        b[i] = t / A[i * n + i];
      }
      return true;
    }
  }
}
