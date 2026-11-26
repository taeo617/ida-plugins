using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;

namespace NPatch
{
  /// <summary>
  /// Holes that aren't 4-sided, filled with untrimmed 4-sided pieces.
  ///
  /// A smooth guide surface over the whole hole is solved first (the trimmed method, untrimmed). Cut
  /// curves drawn on the guide divide the hole into 4-sided regions, in one of these layouts:
  /// <list type="bullet">
  /// <item>5 sides: one cut from a corner to the opposite side → 2 pieces (best for long strips and
  /// holes with one shallow corner).</item>
  /// <item>6 sides: one cut between opposite corners → 2 pieces.</item>
  /// <item>any 3–8 sides: a fan from each side's midpoint to a centre point → one piece per corner.</item>
  /// </list>
  /// Each piece is a <see cref="QuadFill"/> surface matched to the real neighbours along the hole's edges
  /// and, along the cuts, first to the guide and then to the neighbouring piece itself (a few passes),
  /// so the seams converge to G2. Every layout that applies is built; the one whose edges pass (fewest
  /// pieces first), or else misses by the least, is used.
  /// </summary>
  static class SplitFill
  {
    public const int MaxSides = 8;
    internal static int SeamPasses = 3;
    internal static bool SeamG2 = false;  // G2 seams even when the hole edges are only G1

    /// <summary>Part of a region's loop: a piece of an original boundary input, or a cut curve.</summary>
    class Seg { public Curve C; public int Input = -1; public int Cut = -1; }

    class Layout
    {
      public string Name;
      public List<Curve> Cuts = new List<Curve>();
      public List<(Vector3d n0, Vector3d n1)> CutN = new List<(Vector3d, Vector3d)>();  // wanted normals at cut ends (Unset = none)
      public List<List<Seg>> Regions = new List<List<Seg>>();
      public double CutFrac;  // spans along every cut = SpanCount × this (same on both sides of a seam)
    }

    public static FillResult Solve(IList<BoundaryInput> inputs, List<QuadFill.Side> sides, NurbsSurface guide, FillOptions opt, double tol)
    {
      var results = new List<FillResult>();
      string why = "";
      foreach (var lay in Layouts(inputs, sides, guide, opt, tol))
      {
        try
        {
          // More spans until the hole's edges fit (long, strongly curved edges need them).
          var r = NSidedFill.Refine(o => SolveLayout(inputs, sides, lay, guide, o, tol), opt, tol);
          if (r.Brep != null) results.Add(r); else why += (why.Length > 0 ? "; " : "") + r.Message;
        }
        catch (Exception ex) { why += (why.Length > 0 ? "; " : "") + lay.Name + ": " + ex.Message; }
      }
      if (results.Count == 0) return new FillResult { Message = "Split failed: " + why };
      return results.FirstOrDefault(r => r.AllOk) ?? results.OrderBy(r => NSidedFill.Badness(r, opt, tol)).First();
    }

    // ---------- layouts ----------

    static IEnumerable<Layout> Layouts(IList<BoundaryInput> inputs, List<QuadFill.Side> sides, NurbsSurface guide, FillOptions opt, double tol)
    {
      int m = sides.Count;
      double longest = sides.Max(s => s.Len);
      var turn = new double[m];
      for (int k = 0; k < m; k++)
        turn[k] = Vector3d.VectorAngle(EndTangent(sides[(k - 1 + m) % m]), StartTangent(sides[k])) * 180 / Math.PI;

      // One cut → 2 pieces (fewest pieces first: they're tried first and win ties).
      if (m == 5)
      {
        // From the shallowest corner (it splits into two good corners) to the opposite side.
        int k = Enumerable.Range(0, 5).OrderBy(i => turn[i]).First();
        var lay = CornerToSide(inputs, sides, k, guide, opt, longest, tol);
        if (lay != null) yield return lay;
      }
      if (m == 6)
      {
        int k = Enumerable.Range(0, 3).OrderBy(i => turn[i] + turn[i + 3]).First();
        var lay = CornerToCorner(sides, k, guide, opt, longest, tol);
        if (lay != null) yield return lay;
      }
      var fan = Fan(inputs, sides, guide, opt, tol);
      if (fan != null) yield return fan;
    }

    /// <summary>5 sides: cut from corner k to the point of side k+2 nearest to it.</summary>
    static Layout CornerToSide(IList<BoundaryInput> inputs, List<QuadFill.Side> S, int k, NurbsSurface g, FillOptions opt, double longest, double tol)
    {
      var V = S[k].At(0);
      var opp = S[(k + 2) % 5];
      double best = double.MaxValue, f = 0.5;
      for (int i = 0; i <= 60; i++)
      {
        double s = 0.15 + 0.7 * i / 60.0;
        double d = opp.At(s).DistanceTo(V);
        if (d < best) { best = d; f = s; }
      }
      var (oa, ob, P, tanP, inP) = SplitSide(opp, f);
      // Arrive square to the side, flat in its neighbour's tangent plane, so the piece corner there matches it.
      var nP = FaceNormal(inputs, inP, P);
      var bis = Bisector(S[(k + 4) % 5], S[k], P - V);
      var nV = CornerNormal(S[(k + 4) % 5], S[k]);
      var wP = Inward(g, P, tanP, V, nP);
      var cut = CutCurve(g, V, bis, P, -wP, tol, nV, nP);
      if (cut == null) return null;
      var lay = new Layout { Name = "cut", CutFrac = cut.GetLength() / Math.Max(longest, 1e-9) };
      lay.Cuts.Add(cut); lay.CutN.Add((nV ?? Vector3d.Unset, nP ?? Vector3d.Unset));
      var back = cut.DuplicateCurve(); back.Reverse();
      var A = new List<Seg>();
      A.AddRange(Segs(S[k])); A.AddRange(Segs(S[(k + 1) % 5])); A.AddRange(oa); A.Add(new Seg { C = back, Cut = 0 });
      var B = new List<Seg>();
      B.AddRange(ob); B.AddRange(Segs(S[(k + 3) % 5])); B.AddRange(Segs(S[(k + 4) % 5])); B.Add(new Seg { C = cut.DuplicateCurve(), Cut = 0 });
      lay.Regions.Add(A); lay.Regions.Add(B);
      return lay;
    }

    /// <summary>6 sides: cut from corner k to corner k+3.</summary>
    static Layout CornerToCorner(List<QuadFill.Side> S, int k, NurbsSurface g, FillOptions opt, double longest, double tol)
    {
      var Va = S[k].At(0); var Vb = S[(k + 3) % 6].At(0);
      var ba = Bisector(S[(k + 5) % 6], S[k], Vb - Va);
      var bb = Bisector(S[(k + 2) % 6], S[(k + 3) % 6], Va - Vb);
      var na = CornerNormal(S[(k + 5) % 6], S[k]); var nb = CornerNormal(S[(k + 2) % 6], S[(k + 3) % 6]);
      var cut = CutCurve(g, Va, ba, Vb, -bb, tol, na, nb);
      if (cut == null) return null;
      var lay = new Layout { Name = "cut", CutFrac = cut.GetLength() / Math.Max(longest, 1e-9) };
      lay.Cuts.Add(cut); lay.CutN.Add((na ?? Vector3d.Unset, nb ?? Vector3d.Unset));
      var back = cut.DuplicateCurve(); back.Reverse();
      var A = new List<Seg>();
      for (int i = 0; i < 3; i++) A.AddRange(Segs(S[(k + i) % 6]));
      A.Add(new Seg { C = back, Cut = 0 });
      var B = new List<Seg>();
      for (int i = 3; i < 6; i++) B.AddRange(Segs(S[(k + i) % 6]));
      B.Add(new Seg { C = cut.DuplicateCurve(), Cut = 0 });
      lay.Regions.Add(A); lay.Regions.Add(B);
      return lay;
    }

    /// <summary>Any number of sides: from each side's midpoint to a centre point; one piece per corner.</summary>
    static Layout Fan(IList<BoundaryInput> inputs, List<QuadFill.Side> S, NurbsSurface g, FillOptions opt, double tol)
    {
      int m = S.Count;
      var avg = new Point3d(); int cnt = 0;
      foreach (var s in S) for (int i = 0; i < 10; i++) { avg += s.At(i / 10.0); cnt++; }
      avg /= cnt;
      if (!g.ClosestPoint(avg, out double cu, out double cv)) return null;
      var C = g.PointAt(cu, cv);
      var lay = new Layout { Name = "fan", CutFrac = 0.6 };
      var halves = new (List<Seg> a, List<Seg> b)[m];
      for (int k = 0; k < m; k++)
      {
        var (a, b, mid, tan, inM) = SplitSide(S[k], 0.5);
        var nM = FaceNormal(inputs, inM, mid);
        halves[k] = (a, b);
        var cut = CutCurve(g, mid, Inward(g, mid, tan, C, nM), C, null, tol, nM, null);
        if (cut == null) return null;
        lay.Cuts.Add(cut); lay.CutN.Add((nM ?? Vector3d.Unset, Vector3d.Unset));
      }
      for (int k = 0; k < m; k++)
      {
        int prev = (k - 1 + m) % m;
        var back = lay.Cuts[prev].DuplicateCurve(); back.Reverse();
        var R = new List<Seg>();
        R.AddRange(halves[prev].b); R.AddRange(halves[k].a);
        R.Add(new Seg { C = lay.Cuts[k].DuplicateCurve(), Cut = k });
        R.Add(new Seg { C = back, Cut = prev });
        lay.Regions.Add(R);
      }
      return lay;
    }

    // ---------- solving ----------

    static FillResult SolveLayout(IList<BoundaryInput> inputs, List<QuadFill.Side> sides, Layout lay, NurbsSurface guide, FillOptions opt, double tol)
    {
      var res = new FillResult { Degree = opt.Degree, SpanCount = opt.SpanCount, Untrimmed = true };
      var gFace = guide.ToBrep().Faces[0];
      var corners = sides.Select(s => s.At(0)).ToList();
      double near = 0.01 * sides.Min(s => s.Len);
      int nr = lay.Regions.Count;
      var o = opt.Clone();
      o.SpanCount = Math.Max(3, (int)Math.Ceiling(opt.SpanCount * (nr <= 2 ? 0.8 : 0.6)));
      int cutSpans = Math.Max(3, (int)Math.Ceiling(opt.SpanCount * lay.CutFrac - 1e-9));

      var qs = new List<BoundaryInput>[nr];
      var quads = new QuadFill.Quad[nr];
      var cutAt = new List<(int region, int index)>[lay.Cuts.Count];
      for (int c = 0; c < cutAt.Length; c++) cutAt[c] = new List<(int, int)>();
      var srfs = new NurbsSurface[nr];
      var breps = new Brep[nr];

      for (int r = 0; r < nr; r++)
      {
        var q = new List<BoundaryInput>();
        foreach (var sg in lay.Regions[r])
        {
          if (sg.Cut >= 0)
          {
            cutAt[sg.Cut].Add((r, q.Count));
            var (n0, n1) = lay.CutN[sg.Cut];
            bool fwd = sg.C.PointAtStart.DistanceTo(lay.Cuts[sg.Cut].PointAtStart) < sg.C.PointAtStart.DistanceTo(lay.Cuts[sg.Cut].PointAtEnd);
            q.Add(new BoundaryInput
            {
              Curve = sg.C, AdjacentFace = gFace, Continuity = SeamG2 || inputs.Any(b => b.Effective == Continuity.G2) ? Continuity.G2 : Continuity.G1,
              StartNormal = fwd ? n0 : n1, EndNormal = fwd ? n1 : n0, Inner = true,
            });
          }
          else
          {
            var b = inputs[sg.Input];
            q.Add(new BoundaryInput { Curve = sg.C, AdjacentFace = b.AdjacentFace, Continuity = b.Continuity, SourceObjectId = b.SourceObjectId });
          }
        }
        var quad = QuadFill.Prepare(q, tol);
        if (quad == null)
        {
          var fs = QuadFill.FindSides(q, tol);
          res.Message = $"{lay.Name}: piece {r + 1} is not 4-sided ({fs?.Count} sides: {string.Join("/", q.Select(x => x.Curve.GetLength().ToString("F1")))})";
          return res;
        }
        // Only the hole's own corners ease G2; cut ends on a side and the centre are smooth points.
        for (int i = 0; i < 4; i++)
        {
          var a = quad.Sides[i].At(0);
          quad.Soft[i] = corners.Any(v => v.DistanceTo(a) < near);
          // Along a cut: the same span count in both pieces sharing it.
          if (quad.Sides[i].Pieces.Any(pc => lay.Regions[r][pc.Input].Cut >= 0 || q[pc.Input].AdjacentFace == gFace))
            quad.Spans[i % 2] = cutSpans;
        }
        qs[r] = q; quads[r] = quad;
      }

      // Two pieces, one cut: solve both at once with the seam free (see QuadFill.Seam), so they bend as one
      // surface and the seam isn't stuck on the guide's shape there.
      bool joined1 = false;
      if (JointSeam && nr == 2 && lay.Cuts.Count == 1)
      {
        // If it fails (e.g. a piece folds), fall back to solving the pieces in turn below.
        joined1 = SolveJoint(quads, qs, sides, o, tol, srfs, breps, out _, out int spans, out int deg);
        if (joined1) { res.SpanCount = spans; res.Degree = deg; }
      }

      if (!joined1)
        for (int r = 0; r < nr; r++)
        {
          var res1 = QuadFill.Solve(quads[r], qs[r], o, tol);
          if (res1.Brep == null) { res.Message = $"{lay.Name}: piece {r + 1}: {res1.Message}"; return res; }
          srfs[r] = res1.Surface; breps[r] = res1.Brep;
          res.SpanCount = Math.Max(res.SpanCount, res1.SpanCount); res.Degree = res1.Degree;
        }

      // Pieces now match the guide along the cuts, so they meet each other only as well as each one matches
      // it. Rebuild each piece against its actual neighbour pieces (G1 + G2 to them) a few times; each pass
      // pulls the pair onto one common shape, so the seams converge to G2.
      for (int pass = 0; pass < (joined1 ? 0 : SeamPasses); pass++)
        for (int r = 0; r < nr; r++)
        {
          for (int c = 0; c < cutAt.Length; c++)
            foreach (var (rr, idx) in cutAt[c])
              if (rr == r)
              {
                int other = cutAt[c].Select(t => t.region).FirstOrDefault(x => x != r);
                qs[r][idx].AdjacentFace = breps[other].Faces[0];
              }
          var rp = QuadFill.Solve(quads[r], qs[r], o, tol);
          if (rp.Brep == null) continue;                          // keep the previous version of this piece
          srfs[r] = rp.Surface; breps[r] = rp.Brep;
        }

      var joined = Brep.JoinBreps(breps, tol * 10);
      if (joined == null || joined.Length != 1) { res.Message = $"{lay.Name}: pieces didn't join."; return res; }

      res.Surfaces = srfs.ToList();
      res.Surface = srfs[0];
      res.Brep = joined[0];
      var bbox = BoundingBox.Empty;
      foreach (var s in sides) for (int i = 0; i <= 10; i++) bbox.Union(s.At(i / 10.0));
      double size = bbox.Diagonal.Length;
      NSidedFill.BuildReport(res, inputs, NSidedFill.BuildSamples(inputs, opt.SamplesPerEdge), res.Surfaces, size, opt, tol);

      // Smoothness across the seams (ends skipped: they're on the hole's edges or at the fan centre).
      double maxK = 0, dK = 0;
      for (int c = 0; c < cutAt.Length; c++)
      {
        if (cutAt[c].Count != 2) continue;
        var sa = srfs[cutAt[c][0].region]; var sb = srfs[cutAt[c][1].region];
        // The seam as built (a joint solve moves it off the cut): the border of A that lies on B.
        Curve cut = lay.Cuts[c]; double bestD = double.MaxValue;
        for (int dir = 0; dir < 2; dir++)
          foreach (var tt in new[] { sa.Domain(dir).Min, sa.Domain(dir).Max })
          {
            var iso = sa.IsoCurve(dir, tt); if (iso == null) continue;
            double dsum = 0;
            for (int i = 0; i <= 8; i++) { var p = iso.PointAtNormalizedLength(i / 8.0); sb.ClosestPoint(p, out double u2, out double v2); dsum += sb.PointAt(u2, v2).DistanceTo(p); }
            if (dsum < bestD) { bestD = dsum; cut = iso; }
          }
        for (int i = 1; i < 20; i++)
        {
          cut.NormalizedLengthParameter(i / 20.0, out double t);
          var p = cut.PointAt(t);
          if (!sa.ClosestPoint(p, out double ua, out double va) || !sb.ClosestPoint(p, out double ub, out double vb)) continue;
          var na = sa.NormalAt(ua, va); var nb = sb.NormalAt(ub, vb);
          double a = Vector3d.VectorAngle(na, nb) * 180 / Math.PI;
          // Ends of a seam sit on the hole's border, often at a corner whose neighbours disagree: reported apart,
          // like the corner zones of the hole's edges.
          bool end = i / 20.0 < NSidedFill.CornerFraction || i / 20.0 > 1 - NSidedFill.CornerFraction;
          if (end) res.InnerSeamCornerAngleDeg = Math.Max(res.InnerSeamCornerAngleDeg, Math.Min(a, 180 - a));
          else res.InnerSeamAngleDeg = Math.Max(res.InnerSeamAngleDeg, Math.Min(a, 180 - a));
          if (i < 2 || i > 17) continue;
          var cross = Vector3d.CrossProduct(na, cut.TangentAt(t)); cross.Unitize();
          if (NSidedFill.NormalCurvature(sa, ua, va, na, cross, out double ka) && NSidedFill.NormalCurvature(sb, ub, vb, na, cross, out double kb))
          {
            maxK = Math.Max(maxK, Math.Max(Math.Abs(ka), Math.Abs(kb)));
            dK = Math.Max(dK, Math.Abs(ka - kb));
          }
        }
      }
      res.InnerSeamCurvaturePct = 100 * dK / Math.Max(maxK, 0.1 / size);
      return res;
    }

    internal static bool JointSeam = true;

    /// <summary>Both pieces of a one-cut layout in one system (shared frame, coupled along the cut).</summary>
    static bool SolveJoint(QuadFill.Quad[] quads, List<BoundaryInput>[] qs, List<QuadFill.Side> sides, FillOptions o, double tol,
                           NurbsSurface[] srfs, Brep[] breps, out string err, out int spans, out int deg)
    {
      spans = 0; deg = 0;
      var O = new Point3d();
      foreach (var s in sides) O += s.At(0);
      O /= sides.Count;
      var bb = BoundingBox.Empty;
      foreach (var s in sides) for (int i = 0; i <= 20; i++) bb.Union(s.At(i / 20.0));
      double D = Math.Max(bb.Diagonal.Length, 1e-9);

      var ps = new QuadFill.Sys[2]; var k = new int[2];
      for (int r = 0; r < 2; r++)
      {
        k[r] = Array.FindIndex(quads[r].Sides, sd => sd.Pieces.Count == 1 && qs[r][sd.Pieces[0].Input].Inner);
        if (k[r] < 0) { err = $"joint: piece {r + 1} has no single cut side"; return false; }
        ps[r] = QuadFill.Sys.Build(quads[r], qs[r], o, O, D, out err);
        if (ps[r] == null) return false;
      }
      if (ps[0].AlongCount(k[0]) != ps[1].AlongCount(k[1])) { err = "joint: cut sides differ in control points"; return false; }
      var a0 = quads[0].Sides[k[0]].At(0);
      bool same = a0.DistanceTo(quads[1].Sides[k[1]].At(0)) < a0.DistanceTo(quads[1].Sides[k[1]].At(1));
      var res = QuadFill.SolveJoint(ps, new[] { new QuadFill.Seam { A = 0, KA = k[0], B = 1, KB = k[1], Same = same } });
      if (res == null) { err = "joint: solver failed"; return false; }
      for (int r = 0; r < 2; r++)
      {
        var f = ps[r].Finish(res[r], tol);
        if (f.Brep == null) { err = $"joint: piece {r + 1}: {f.Message}"; return false; }
        srfs[r] = f.Surface; breps[r] = f.Brep;
        spans = Math.Max(spans, f.SpanCount); deg = f.Degree;
      }
      err = null;
      return true;
    }

    // ---------- geometry helpers ----------

    static Vector3d StartTangent(QuadFill.Side s) { var (p, t) = s.Locate(0); return p.C.TangentAt(t); }
    static Vector3d EndTangent(QuadFill.Side s) { var (p, t) = s.Locate(1); return p.C.TangentAt(t); }

    /// <summary>Direction that splits the hole's angle at the corner between sides a (in) and b (out).</summary>
    static Vector3d Bisector(QuadFill.Side a, QuadFill.Side b, Vector3d toward)
    {
      var tin = EndTangent(a); var tout = StartTangent(b);
      var d = tout - tin;
      if (d.Length < 1e-6) d = toward;
      d.Unitize();
      if (d * toward < 0) d = -d;
      return d;
    }

    /// <summary>Into the hole, square to a side with tangent <paramref name="tan"/> at p, on the guide.</summary>
    static Vector3d Inward(NurbsSurface g, Point3d p, Vector3d tan, Point3d toward, Vector3d? n = null)
    {
      g.ClosestPoint(p, out double u, out double v);
      var w = Vector3d.CrossProduct(n ?? g.NormalAt(u, v), tan);
      w.Unitize();
      if (w * (toward - p) < 0) w = -w;
      return w;
    }

    static Vector2d ToUv(NurbsSurface g, double u, double v, Vector3d d)
    {
      g.Evaluate(u, v, 1, out Point3d _, out Vector3d[] d1);
      double a11 = d1[0] * d1[0], a12 = d1[0] * d1[1], a22 = d1[1] * d1[1], b1 = d1[0] * d, b2 = d1[1] * d;
      double det = a11 * a22 - a12 * a12;
      if (Math.Abs(det) < 1e-30) return Vector2d.Zero;
      var r = new Vector2d((a22 * b1 - a12 * b2) / det, (a11 * b2 - a12 * b1) / det);
      return r.Length > 1e-30 ? r / r.Length : Vector2d.Zero;
    }

    /// <summary>
    /// A cut curve on the guide from a to b: a cubic in the guide's (u,v) that leaves a along
    /// <paramref name="startDir"/> and arrives at b travelling along <paramref name="endDir"/> (either may be
    /// null = head straight for the other end), pushed up exactly onto the guide.
    /// </summary>
    static Curve CutCurve(NurbsSurface g, Point3d a, Vector3d? startDir, Point3d b, Vector3d? endDir, double tol,
                          Vector3d? startN = null, Vector3d? endN = null)
    {
      if (!g.ClosestPoint(a, out double ua, out double va) || !g.ClosestPoint(b, out double ub, out double vb)) return null;
      var p0 = new Point2d(ua, va); var p3 = new Point2d(ub, vb);
      double L = p0.DistanceTo(p3);
      if (L < 1e-12) return null;
      var d0 = startDir.HasValue ? ToUv(g, ua, va, startDir.Value) : Vector2d.Zero;
      var d1 = endDir.HasValue ? ToUv(g, ub, vb, endDir.Value) : Vector2d.Zero;
      var p1 = d0.Length > 0 ? p0 + d0 * (L / 3) : p0 + (p3 - p0) / 3;
      var p2 = d1.Length > 0 ? p3 - d1 * (L / 3) : p3 + (p0 - p3) / 3;
      var uv = new NurbsCurve(2, false, 4, 4);
      uv.Knots.CreateUniformKnots(1);
      var cp = new[] { p0, p1, p2, p3 };
      for (int i = 0; i < 4; i++) uv.Points.SetPoint(i, new Point3d(cp[i].X, cp[i].Y, 0));
      var c = g.Pushup(uv, tol * 0.1);
      if (c == null) return null;
      var nc = EndsTo(c, a, b).ToNurbsCurve();
      if (startN.HasValue) FlattenEnd(nc, true, startN.Value);
      if (endN.HasValue) FlattenEnd(nc, false, endN.Value);
      return nc;
    }

    /// <summary>
    /// Turns the curve's end tangent (by moving only the next control point, keeping its distance) into
    /// the plane square to <paramref name="n"/>: a cut that ends on a hole edge must leave flat in that edge's
    /// neighbour, otherwise the pieces' corner there is tilted against it. The guide is only G1 to within its
    /// own error, so a cut that just follows the guide is off by that much.
    /// </summary>
    static void FlattenEnd(NurbsCurve nc, bool start, Vector3d n)
    {
      if (!n.Unitize() || nc.Points.Count < 3) return;
      int i0 = start ? 0 : nc.Points.Count - 1, i1 = start ? 1 : nc.Points.Count - 2;
      var p0 = nc.Points[i0].Location; var p1 = nc.Points[i1].Location;
      var d = p1 - p0; double len = d.Length;
      if (len < 1e-12) return;
      var f = d - (d * n) * n;
      if (!f.Unitize()) return;
      nc.Points.SetPoint(i1, p0 + f * len, nc.Points[i1].Weight);
    }

    /// <summary>
    /// The only surface normal possible at the hole corner between sides a (in) and b (out): square to both
    /// edge tangents. Null when the corner is too straight for that to say anything.
    /// </summary>
    static Vector3d? CornerNormal(QuadFill.Side a, QuadFill.Side b)
    {
      var tin = EndTangent(a); var tout = StartTangent(b);
      if (Vector3d.VectorAngle(tin, tout) < 5 * Math.PI / 180) return null;
      var n = Vector3d.CrossProduct(tin, tout);
      return n.Unitize() ? n : (Vector3d?)null;
    }

    /// <summary>Neighbour surface normal of an input at p (null for a curve-only or G0 edge).</summary>
    static Vector3d? FaceNormal(IList<BoundaryInput> inputs, int input, Point3d p)
    {
      if (input < 0 || input >= inputs.Count) return null;
      var b = inputs[input];
      if (b.AdjacentFace == null || b.Continuity == Continuity.G0) return null;
      if (!b.AdjacentFace.ClosestPoint(p, out double u, out double v)) return null;
      var n = b.AdjacentFace.NormalAt(u, v);
      return n.Unitize() ? n : (Vector3d?)null;
    }

    /// <summary>
    /// Moves the curve's ends exactly onto a and b with a smooth cubic falloff (instead of moving only
    /// the end control point, which would bend the curvature right at the end).
    /// </summary>
    static Curve EndsTo(Curve c, Point3d a, Point3d b)
    {
      var nc = c.ToNurbsCurve();
      var da = a - nc.PointAtStart; var db = b - nc.PointAtEnd;
      var g = nc.GrevilleParameters();
      var d = nc.Domain;
      for (int i = 0; i < nc.Points.Count; i++)
      {
        double s = d.NormalizedParameterAt(g[i]), r = 1 - s;
        var w = nc.Points[i].Weight;
        nc.Points.SetPoint(i, nc.Points[i].Location + da * (r * r * r) + db * (s * s * s), w);
      }
      return nc;
    }

    static List<Seg> Segs(QuadFill.Side side) =>
      side.Pieces.Select(p => new Seg { C = p.C.DuplicateCurve(), Input = p.Input }).ToList();

    /// <summary>Splits a side at arc-length fraction f into (start→f) and (f→end) segments.</summary>
    static (List<Seg> a, List<Seg> b, Point3d at, Vector3d tan, int input) SplitSide(QuadFill.Side side, double f)
    {
      var (piece, t) = side.Locate(f);
      var at = piece.C.PointAt(t);
      var tan = piece.C.TangentAt(t);
      var a = new List<Seg>(); var b = new List<Seg>();
      bool after = false;
      foreach (var p in side.Pieces)
      {
        if (p == piece)
        {
          // A split right next to where two edges of this side meet would leave a sliver edge: split
          // exactly at the joint instead.
          var d = p.C.Domain; double near = 0.01 * side.Len;
          bool atStart = p.C.GetLength(new Interval(d.Min, Math.Max(d.Min, t))) < near;
          bool atEnd = p.C.GetLength(new Interval(Math.Min(t, d.Max), d.Max)) < near;
          if (atStart) b.Add(new Seg { C = p.C.DuplicateCurve(), Input = p.Input });
          else if (atEnd) a.Add(new Seg { C = p.C.DuplicateCurve(), Input = p.Input });
          else
          {
            a.Add(new Seg { C = p.C.Trim(d.Min, t), Input = p.Input });
            b.Add(new Seg { C = p.C.Trim(t, d.Max), Input = p.Input });
          }
          if (atStart) { at = p.C.PointAtStart; tan = p.C.TangentAtStart; }
          if (atEnd) { at = p.C.PointAtEnd; tan = p.C.TangentAtEnd; }
          after = true;
        }
        else if (!after) a.Add(new Seg { C = p.C.DuplicateCurve(), Input = p.Input });
        else b.Add(new Seg { C = p.C.DuplicateCurve(), Input = p.Input });
      }
      return (a, b, at, tan, piece.Input);
    }
  }
}
