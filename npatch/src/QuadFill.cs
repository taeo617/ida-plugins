using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;

namespace NPatch
{
  /// <summary>
  /// 4-sided holes: an untrimmed NURBS surface whose four borders are the boundary itself.
  /// The border rows of control points are fitted to the boundary (exact G0 up to the fit), and
  /// continuity is set row by row: the next row in from each border makes the cross-boundary
  /// derivative lie in the neighbour's tangent plane (G1), the row after that matches the
  /// neighbour's normal curvature across the border (G2). The remaining control points minimise
  /// bending energy. Because nothing is trimmed, isocurves follow the boundary and the patch joins
  /// edge to edge with its neighbours.
  /// A hole counts as 4-sided when its boundary has exactly four real corners; several edges that
  /// meet smoothly form one side.
  /// </summary>
  static class QuadFill
  {
    // Pieces join into one side only when they meet (almost) tangentially; any visible bend is a corner.
    // (A side can't bend inside: its border row is one smooth curve.)
    const double CornerTurnDeg = 3;
    internal static bool ExactTangentRows = true;

    internal class Piece { public Curve C; public int Input; public double Len; }

    internal class Side
    {
      public readonly List<Piece> Pieces = new List<Piece>();
      public double Len;
      /// <summary>Piece and curve parameter at arc-length fraction s (0..1) of the side.</summary>
      public (Piece piece, double t) Locate(double s)
      {
        double L = Math.Max(0, Math.Min(1, s)) * Len;
        for (int i = 0; i < Pieces.Count; i++)
        {
          var p = Pieces[i];
          if (L <= p.Len || i == Pieces.Count - 1)
          {
            p.C.LengthParameter(Math.Max(0, Math.Min(p.Len, L)), out double t);
            return (p, t);
          }
          L -= p.Len;
        }
        return (Pieces[0], Pieces[0].C.Domain.Min);
      }
      public Point3d At(double s) { var (p, t) = Locate(s); return p.C.PointAt(t); }
    }

    public class Quad
    {
      internal Side[] Sides;
      /// <summary>Corner k (start of side k) eases G2 to 0 there. Off for corners that are not real corners of the hole.</summary>
      internal bool[] Soft = { true, true, true, true };
      /// <summary>
      /// Span counts along sides 0/2 (u) and 1/3 (v); 0 = automatic. Pieces that share a seam use the same
      /// count along it, so both fit the seam curve with identical control points.
      /// </summary>
      internal int[] Spans = { 0, 0 };
    }

    /// <summary>Returns the 4 sides if the boundary is a 4-cornered loop of smooth pieces, else null.</summary>
    public static Quad Prepare(IList<BoundaryInput> inputs, double tol)
    {
      var sides = FindSides(inputs, tol);
      if (sides == null || sides.Count != 4) return null;
      // Very unequal opposite sides make a badly stretched surface; leave those to the trimmed method.
      double r0 = Math.Min(sides[0].Len, sides[2].Len) / Math.Max(sides[0].Len, sides[2].Len);
      double r1 = Math.Min(sides[1].Len, sides[3].Len) / Math.Max(sides[1].Len, sides[3].Len);
      if (r0 < 0.2 || r1 < 0.2) return null;
      return new Quad { Sides = sides.ToArray() };
    }

    /// <summary>
    /// Chains the boundary head to tail and groups it into sides at real corners
    /// (any number of sides). Null if the boundary isn't one loop of smooth pieces.
    /// </summary>
    internal static List<Side> FindSides(IList<BoundaryInput> inputs, double tol)
    {
      int n = inputs.Count;
      if (n < 1) return null;
      double jt = Math.Max(tol * 10, 1e-6);

      // Chain the pieces head to tail.
      var chain = new List<Piece> { new Piece { C = inputs[0].Curve.DuplicateCurve(), Input = 0 } };
      var used = new bool[n]; used[0] = true;
      for (int k = 1; k < n; k++)
      {
        var end = chain[chain.Count - 1].C.PointAtEnd;
        int found = -1; bool flip = false;
        for (int i = 0; i < n && found < 0; i++)
        {
          if (used[i]) continue;
          var c = inputs[i].Curve;
          if (c.PointAtStart.DistanceTo(end) <= jt) { found = i; flip = false; }
          else if (c.PointAtEnd.DistanceTo(end) <= jt) { found = i; flip = true; }
        }
        if (found < 0) return null;
        used[found] = true;
        var cc = inputs[found].Curve.DuplicateCurve();
        if (flip) cc.Reverse();
        chain.Add(new Piece { C = cc, Input = found });
      }
      if (chain[n - 1].C.PointAtEnd.DistanceTo(chain[0].C.PointAtStart) > jt) return null;

      foreach (var p in chain)
      {
        if (p.C.IsClosed) return null;
        // A kink inside a piece would be a hidden corner.
        var d = p.C.Domain;
        double eps = 1e-6 * Math.Max(1, d.Length);
        if (p.C.GetNextDiscontinuity(Rhino.Geometry.Continuity.G1_continuous, d.Min, d.Max, out double tk) && tk < d.Max - eps && tk > d.Min + eps) return null;
        p.Len = p.C.GetLength();
        if (p.Len < jt) return null;
      }

      // Corners: turn angle at the start of each piece.
      var corner = new bool[n];
      for (int k = 0; k < n; k++)
      {
        var tin = chain[(k - 1 + n) % n].C.TangentAtEnd;
        var tout = chain[k].C.TangentAtStart;
        double a = Vector3d.VectorAngle(tin, tout) * 180 / Math.PI;
        if (a > 170) return null;          // cusp
        corner[k] = a > CornerTurnDeg;
      }
      if (corner.Count(c => c) < 3) return null;

      int start = Array.IndexOf(corner, true);
      var sides = new List<Side>();
      for (int m = 0; m < n; m++)
      {
        int k = (start + m) % n;
        if (corner[k]) sides.Add(new Side());
        sides[sides.Count - 1].Pieces.Add(chain[k]);
      }
      foreach (var s in sides) s.Len = s.Pieces.Sum(p => p.Len);
      return sides;
    }

    public static FillResult Solve(Quad q, IList<BoundaryInput> inputs, FillOptions opt, double tol)
    {
      var sys = Sys.Build(q, inputs, opt, null, 0, out string err);
      if (sys == null) return new FillResult { Degree = opt.Degree, SpanCount = opt.SpanCount, Untrimmed = true, Message = err };
      var srf = SolveJoint(new[] { sys }, Array.Empty<Seam>());
      if (srf == null) return new FillResult { Degree = sys.P, SpanCount = Math.Max(sys.SpU, sys.SpV), Untrimmed = true, Message = "Solver failed." };
      return sys.Finish(srf[0], tol);
    }

    /// <summary>
    /// Two pieces sharing a side: piece A's side KA and piece B's side KB run along the same curve in opposite
    /// directions, with the same number of control points. In a joint solve the shared border is free (only its
    /// 3 end control points stay on the cut), and the pieces are tied across it: same border, same cross
    /// tangent (C1) and same cross curvature (C2). They then bend like one surface, and the seam settles where
    /// that surface is fairest instead of where the guide put it.
    /// </summary>
    internal struct Seam { public int A, KA, B, KB; public bool Same; }   // Same: both sides run the same way

    /// <summary>Solves several pieces together (one piece is the plain case). One surface per piece, or null.</summary>
    internal static NurbsSurface[] SolveJoint(IList<Sys> ps, IList<Seam> seams)
    {
      bool joint = seams.Count > 0;
      foreach (var s in seams) { ps[s.A].FreeSide(s.KA); ps[s.B].FreeSide(s.KB); }
      int N = 0;
      foreach (var p in ps) { p.Off = N; N += p.Count; }
      // One piece: banded. Joint: small enough to factor densely.
      int bw = joint ? N - 1 : ps[0].Bandwidth;
      var srfs = new NurbsSurface[ps.Count];
      bool Run()
      {
        int passes = ps.Max(p => p.Passes);
        for (int pass = 0; pass < passes; pass++)
        {
          var M = new BandMatrix(N, bw);
          var rhs = new double[N];
          for (int k = 0; k < ps.Count; k++) ps[k].Assemble(srfs[k], M, rhs, N, joint);
          foreach (var s in seams) Couple(ps[s.A], s.KA, ps[s.B], s.KB, s.Same, M, N);
          if (!M.Solve(rhs)) return false;
          for (int k = 0; k < ps.Count; k++) srfs[k] = ps[k].Make(rhs);
        }
        return true;
      }
      if (!Run()) return null;
      // Exact tangency along the hole's edges without a crease (see Sys.PinTangentRows), then the inside again.
      if (ExactTangentRows)
      {
        bool again = false;
        for (int k = 0; k < ps.Count; k++) again |= ps[k].PinTangentRows(srfs[k], joint);
        if (again && !Run()) return null;
      }
      return srfs;
    }

    internal static double CoupleC1 = 1e5, CoupleC2 = 1e2;
    internal static bool FreeSeam = true;
    internal static int SeamKeepEnd = 2;   // end position + direction stay on the cut; its bend there is free

    /// <summary>Ties piece A's side ka to piece B's side kb (opposite directions, same frame): same points, C1, C2.</summary>
    static void Couple(Sys a, int ka, Sys b, int kb, bool same, BandMatrix M, int N)
    {
      int n = a.AlongCount(ka);
      if (b.AlongCount(kb) != n) return;
      // Cross derivatives per model length, so pieces of different width meet with the same slope and bend.
      double ha = a.CrossStep(ka), hb = b.CrossStep(kb);
      const double wPos = 1e8;
      var idx = new int[6]; var val = new double[6];
      void Row(int cnt, double w)
      {
        for (int x = 0; x < cnt; x++)
        {
          double wx = w * val[x];
          for (int y = 0; y < cnt; y++) M.Add(idx[x], idx[y], wx * val[y]);
        }
      }
      for (int e = 0; e < n; e++)
      {
        var (a0, a1, a2) = a.Rows(ka, e);
        var (b0, b1, b2) = b.Rows(kb, same ? e : n - 1 - e);
        bool freeB = !a.Pin[a0.i, a0.j].HasValue || !b.Pin[b0.i, b0.j].HasValue;
        bool free1 = freeB || !a.Pin[a1.i, a1.j].HasValue || !b.Pin[b1.i, b1.j].HasValue;
        bool free2 = free1 || !a.Pin[a2.i, a2.j].HasValue || !b.Pin[b2.i, b2.j].HasValue;
        for (int c = 0; c < 3; c++)
        {
          if (freeB)
          {
            idx[0] = a.Idx(a0.i, a0.j, c); val[0] = 1;
            idx[1] = b.Idx(b0.i, b0.j, c); val[1] = -1;
            Row(2, wPos);
          }
          if (free1)
          {
            // C1: (a1 − a0)/ha = −(b1 − b0)/hb.
            idx[0] = a.Idx(a1.i, a1.j, c); val[0] = 1 / ha;
            idx[1] = a.Idx(a0.i, a0.j, c); val[1] = -1 / ha;
            idx[2] = b.Idx(b1.i, b1.j, c); val[2] = 1 / hb;
            idx[3] = b.Idx(b0.i, b0.j, c); val[3] = -1 / hb;
            Row(4, CoupleC1);
          }
          if (free2)
          {
            // C2: (a2 − 3a1 + 2a0)/ha² = (b2 − 3b1 + 2b0)/hb² (clamped uniform knots, 2+ spans across).
            double qa = 1 / (ha * ha), qb = 1 / (hb * hb);
            idx[0] = a.Idx(a2.i, a2.j, c); val[0] = qa;
            idx[1] = a.Idx(a1.i, a1.j, c); val[1] = -3 * qa;
            idx[2] = a.Idx(a0.i, a0.j, c); val[2] = 2 * qa;
            idx[3] = b.Idx(b2.i, b2.j, c); val[3] = -qb;
            idx[4] = b.Idx(b1.i, b1.j, c); val[4] = 3 * qb;
            idx[5] = b.Idx(b0.i, b0.j, c); val[5] = -2 * qb;
            Row(6, CoupleC2);
          }
        }
      }
    }

    /// <summary>One 4-sided piece: knots, pinned borders, continuity samples and its normal equations.</summary>
    internal class Sys
    {
      public Quad Q; public IList<BoundaryInput> Inputs; public FillOptions Opt;
      public int P, SpU, SpV, NU, NV, IE, JE, Passes;
      public double[] U, V;
      public Point3d O; public double D, Lu, Lv;
      public Point3d?[,] Pin; Point3d[,] coons;
      bool[] clash;
      List<(double u, double v, bool crossV, Continuity c, Vector3d nrm, BrepFace face, Point2d fuv, double ramp, double tramp, bool inner)> cons;
      public int Off;
      public readonly HashSet<(int i, int j)> Coupled = new HashSet<(int i, int j)>();
      public int Count => NU * NV * 3;
      public int Bandwidth => 3 * (P * NV + P + 1);
      public int Idx(int i, int j, int c) => Off + (i * NV + j) * 3 + c;

      public Point3d L(Point3d w) => new Point3d((w.X - O.X) / D, (w.Y - O.Y) / D, (w.Z - O.Z) / D);
      public Point3d W(Point3d l) => new Point3d(O.X + l.X * D, O.Y + l.Y * D, O.Z + l.Z * D);

      public int AlongCount(int k) => k % 2 == 0 ? NU : NV;
      /// <summary>First knot interval across side k, in local length (piece width there ÷ spans across).</summary>
      public double CrossStep(int k)
      {
        var S = Q.Sides;
        double width = k % 2 == 0 ? 0.5 * (S[1].Len + S[3].Len) : 0.5 * (S[0].Len + S[2].Len);
        return width / D / (k % 2 == 0 ? SpV : SpU);
      }

      /// <summary>Control points of side k at along-index e (in the side's direction): border, row 1, row 2.</summary>
      public ((int i, int j), (int i, int j), (int i, int j)) Rows(int k, int e)
      {
        switch (k)
        {
          case 0: return ((e, 0), (e, 1), (e, 2));
          case 1: return ((IE, e), (IE - 1, e), (IE - 2, e));
          case 2: return ((IE - e, JE), (IE - e, JE - 1), (IE - e, JE - 2));
          default: return ((0, JE - e), (1, JE - e), (2, JE - e));
        }
      }

      /// <summary>Unpins a shared side's border except SeamKeepEnd control points at each end (they carry the cut's ends).</summary>
      public void FreeSide(int k)
      {
        int n = AlongCount(k);
        if (FreeSeam) for (int e = SeamKeepEnd; e < n - SeamKeepEnd; e++) { var (b, _, _) = Rows(k, e); Pin[b.i, b.j] = null; }
        // Rows 1-2 along the shared side belong to the coupling; the edge rows must not pin them per piece.
        for (int e = 1; e < n - 1; e++) { var (_, r1, r2) = Rows(k, e); Coupled.Add(r1); Coupled.Add(r2); }
      }

      /// <summary>Sets up a piece. A joint solve passes one frame (origin, scale) for all pieces.</summary>
      public static Sys Build(Quad q, IList<BoundaryInput> inputs, FillOptions opt, Point3d? frameO, double frameD, out string err)
      {
        err = null;
        var s = new Sys { Q = q, Inputs = inputs, Opt = opt };
        int p = s.P = Math.Max(2, Math.Min(7, opt.Degree));
        bool anyG2 = inputs.Any(b => b.Effective == Continuity.G2);
        // Need room for rows 0,1,2 on both opposite borders when G2 is used.
        int spMin = Math.Max(1, (anyG2 ? 6 : 4) - p);
        int sp = Math.Max(Math.Max(1, opt.SpanCount), spMin);
        var S = q.Sides;
        // Spans follow the piece's proportions: the full count along the longer direction, fewer across
        // a narrow piece (instead of the same count both ways, which wastes rows on long thin pieces).
        double lenU = 0.5 * (S[0].Len + S[2].Len), lenV = 0.5 * (S[1].Len + S[3].Len);
        int spShortMin = Math.Max(spMin, Math.Min(sp, 3));
        int spU = lenU >= lenV ? sp : Math.Max(spShortMin, (int)Math.Ceiling(sp * lenU / lenV - 1e-9));
        int spV = lenV >= lenU ? sp : Math.Max(spShortMin, (int)Math.Ceiling(sp * lenV / lenU - 1e-9));
        if (q.Spans[0] > 0) spU = Math.Max(spMin, q.Spans[0]);
        if (q.Spans[1] > 0) spV = Math.Max(spMin, q.Spans[1]);
        s.SpU = spU; s.SpV = spV;
        int nU = s.NU = spU + p, nV = s.NV = spV + p;
        s.U = NSidedFill.ClampedUniformKnots(nU, p); s.V = NSidedFill.ClampedUniformKnots(nV, p);

        // Local frame: world / D around the corner centroid keeps numbers near 1.
        if (frameO.HasValue && frameD > 0) { s.O = frameO.Value; s.D = frameD; }
        else
        {
          var O = new Point3d();
          foreach (var sd in S) O += sd.At(0);
          O /= 4;
          var bb = BoundingBox.Empty;
          foreach (var sd in S) for (int i = 0; i <= 20; i++) bb.Union(sd.At(i / 20.0));
          s.O = O; s.D = Math.Max(bb.Diagonal.Length, 1e-9);
        }

        // 1. Border rows: fit each side with the shared knot vector.
        var fit = new Point3d[4][];
        for (int k = 0; k < 4; k++)
        {
          fit[k] = k % 2 == 0 ? FitSide(S[k], s.U, nU, p, s.L, !q.Soft[k], !q.Soft[(k + 1) % 4])
                              : FitSide(S[k], s.V, nV, p, s.L, !q.Soft[k], !q.Soft[(k + 1) % 4]);
          if (fit[k] == null) { err = "Could not fit a side."; return null; }
        }
        int iE = s.IE = nU - 1, jE = s.JE = nV - 1;
        var pin = s.Pin = new Point3d?[nU, nV];
        for (int i = 0; i < nU; i++) { pin[i, 0] = fit[0][i]; pin[i, jE] = fit[2][iE - i]; }
        for (int j = 0; j < nV; j++) { pin[iE, j] = fit[1][j]; pin[0, j] = fit[3][jE - j]; }
        // Corners: both fits end exactly on the corner; average to remove round-off.
        pin[0, 0] = Mid(fit[0][0], fit[3][jE]);
        pin[iE, 0] = Mid(fit[0][iE], fit[1][0]);
        pin[iE, jE] = Mid(fit[1][jE], fit[2][0]);
        pin[0, jE] = Mid(fit[2][iE], fit[3][0]);

        // Coons patch of the border rows: starting shape and weak regulariser.
        var coons = s.coons = new Point3d[nU, nV];
        for (int i = 0; i < nU; i++)
          for (int j = 0; j < nV; j++)
          {
            double a = i / (double)iE, b = j / (double)jE;
            var P = (1 - b) * (Vector3d)pin[i, 0].Value + b * (Vector3d)pin[i, jE].Value
                  + (1 - a) * (Vector3d)pin[0, j].Value + a * (Vector3d)pin[iE, j].Value
                  - ((1 - a) * (1 - b) * (Vector3d)pin[0, 0].Value + a * (1 - b) * (Vector3d)pin[iE, 0].Value
                   + a * b * (Vector3d)pin[iE, jE].Value + (1 - a) * b * (Vector3d)pin[0, jE].Value);
            coons[i, j] = (Point3d)P;
          }

        // Corners where the hole's own neighbours disagree with each other: the surface normal there is fixed
        // by the two edge tangents, and at least one neighbour isn't square to it, so no surface can match both
        // edges right up to the corner. Let the edges' angle rows fade out near such a corner (into the
        // corner zone the report sets apart) instead of forcing the mismatch into the surface or an inner seam.
        var clash = s.clash = new bool[4];
        for (int k = 0; k < 4; k++)
        {
          if (!q.Soft[k]) continue;
          var (pa, ta) = S[(k + 3) % 4].Locate(1); var (pb, tb) = S[k].Locate(0);
          var nc = Vector3d.CrossProduct(pa.C.TangentAt(ta), pb.C.TangentAt(tb));
          if (!nc.Unitize()) continue;
          foreach (var (pc, tc) in new[] { (pa, ta), (pb, tb) })
          {
            var bi = inputs[pc.Input];
            if (bi.Inner || bi.Effective == Continuity.G0) continue;
            var pt = pc.C.PointAt(tc);
            if (!bi.AdjacentFace.ClosestPoint(pt, out double fu, out double fv)) continue;
            double a = Vector3d.VectorAngle(bi.AdjacentFace.NormalAt(fu, fv), nc) * 180 / Math.PI;
            if (Math.Min(a, 180 - a) > opt.AngleToleranceDeg) clash[k] = true;
          }
        }

        // 2. Cross-boundary samples with their neighbour data.
        var cons = s.cons = new List<(double, double, bool, Continuity, Vector3d, BrepFace, Point2d, double, double, bool)>();
        int m = Math.Max(40, 4 * Math.Max(nU, nV));
        for (int k = 0; k < 4; k++)
          for (int i = 0; i <= m; i++)
          {
            double sf = i / (double)m;
            var (piece, t) = S[k].Locate(sf);
            var b = inputs[piece.Input];
            var c = b.Effective;
            if (c == Continuity.G0) continue;
            var pt = piece.C.PointAt(t);
            if (!b.AdjacentFace.ClosestPoint(pt, out double fu, out double fv)) continue;
            var nrm = b.TargetNormal(pt, b.AdjacentFace.NormalAt(fu, fv));
            double u, v; bool crossV;
            switch (k)
            {
              case 0: u = sf; v = 0; crossV = true; break;
              case 1: u = 1; v = sf; crossV = false; break;
              case 2: u = 1 - sf; v = 1; crossV = true; break;
              default: u = 0; v = 1 - sf; crossV = false; break;
            }
            double tr = b.Inner ? 1 : Math.Min(clash[k] ? NSidedFill.CornerRamp(Math.Min(sf, 0.5)) : 1, clash[(k + 1) % 4] ? NSidedFill.CornerRamp(Math.Max(sf, 0.5)) : 1);
            cons.Add((u, v, crossV, c, nrm, b.AdjacentFace, new Point2d(fu, fv), Math.Min(q.Soft[k] ? NSidedFill.CornerRamp(Math.Min(sf, 0.5)) : 1, q.Soft[(k + 1) % 4] ? NSidedFill.CornerRamp(Math.Max(sf, 0.5)) : 1), tr, b.Inner));
          }

        s.Lu = lenU / s.D; s.Lv = lenV / s.D;
        s.Passes = anyG2 ? 1 + Math.Max(1, opt.CurvatureIterations) : 1;
        return s;
      }

      public NurbsSurface Make(double[] sol)
      {
        var srf = NurbsSurface.Create(3, false, P + 1, P + 1, NU, NV);
        for (int k = 1; k < U.Length - 1; k++) srf.KnotsU[k - 1] = U[k];
        for (int k = 1; k < V.Length - 1; k++) srf.KnotsV[k - 1] = V[k];
        for (int i = 0; i < NU; i++)
          for (int j = 0; j < NV; j++)
            srf.Points.SetPoint(i, j, W(new Point3d(sol[Idx(i, j, 0)], sol[Idx(i, j, 1)], sol[Idx(i, j, 2)])));
        return srf;
      }

      public FillResult Finish(NurbsSurface srf, double tol)
      {
        var res = new FillResult { SpanCount = Math.Max(SpU, SpV), Degree = P, Untrimmed = true };
        if (srf == null || !srf.IsValid || Folded(srf)) { res.Message = "4-sided surface folded."; return res; }
        res.Surface = srf; res.Surfaces.Add(srf);
        NSidedFill.BuildReport(res, Inputs, NSidedFill.BuildSamples(Inputs, Opt.SamplesPerEdge), srf, D, Opt, tol);
        res.Brep = srf.ToBrep();
        return res;
      }

      /// <summary>
      /// Exact tangency without a crease. The soft angle rows leave a small angle error on long, strongly curved
      /// edges, and simply weighting them harder makes the surface bend sharply right next to the edge (visible
      /// as highlight bands). Instead, take the smooth solution and move only the control points of the row next
      /// to each G1/G2 edge, as little as possible, so that the cross tangent along the whole edge lies in the
      /// neighbour's tangent plane (least squares over dense samples). That row is then pinned and the inside
      /// solved again, so it stays fair. In a joint solve the shared sides are left to the coupling.
      /// </summary>
      public bool PinTangentRows(NurbsSurface srf, bool skipInner)
      {
        int p = P, nU = NU, nV = NV, iE = IE, jE = JE;
        var S = Q.Sides; var pin = Pin;
        var idx = new Dictionary<(int, int), int>();
        var x0 = new List<Point3d>();
        int Var(int i, int j)
        {
          if (pin[i, j].HasValue || Coupled.Contains((i, j))) return -1;
          if (idx.TryGetValue((i, j), out int k)) return k;
          srf.Points.GetPoint(i, j, out Point3d w);
          idx[(i, j)] = x0.Count; x0.Add(L(w));
          return x0.Count - 1;
        }
        Point3d Cur(int i, int j) { if (pin[i, j].HasValue) return pin[i, j].Value; srf.Points.GetPoint(i, j, out Point3d w); return L(w); }
        var rows = new List<(int[] v, double[] c, double r)>();
        for (int k = 0; k < 4; k++)
        {
          var kn = k % 2 == 0 ? U : V; int nA = k % 2 == 0 ? nU : nV;
          int m = 6 * nA;
          for (int si = 1; si < m; si++)
          {
            double s = si / (double)m;
            // Leave the angle free where the hole's own neighbours clash at a corner (see 'clash').
            if ((clash[k] && NSidedFill.CornerRamp(Math.Min(s, 0.5)) < 0.5) || (clash[(k + 1) % 4] && NSidedFill.CornerRamp(Math.Max(s, 0.5)) < 0.5)) continue;
            var (piece, t) = S[k].Locate(s);
            var b = Inputs[piece.Input];
            if (b.Effective == Continuity.G0 || (skipInner && b.Inner)) continue;
            var pt = piece.C.PointAt(t);
            if (!b.AdjacentFace.ClosestPoint(pt, out double fu, out double fv)) continue;
            var n = b.TargetNormal(pt, b.AdjacentFace.NormalAt(fu, fv));
            double prm = k < 2 ? s : 1 - s;
            NSidedFill.Basis(kn, nA, p, prm, 0, out int span, out double[,] B);
            // Cross derivative at the edge ∝ Σ N_a (row1_a − edge_a); it must be square to n.
            var vs = new List<int>(); var cs = new List<double>(); double r = 0;
            for (int a = 0; a <= p; a++)
            {
              int e = span - p + a;
              (int, int) bnd, r1;
              switch (k)
              {
                case 0: bnd = (e, 0); r1 = (e, 1); break;
                case 2: bnd = (e, jE); r1 = (e, jE - 1); break;
                case 1: bnd = (iE, e); r1 = (iE - 1, e); break;
                default: bnd = (0, e); r1 = (1, e); break;
              }
              double w = B[0, a];
              if (w == 0) continue;
              r += w * ((Vector3d)Cur(bnd.Item1, bnd.Item2) * n);
              int vi = Var(r1.Item1, r1.Item2);
              if (vi < 0) { r -= w * ((Vector3d)Cur(r1.Item1, r1.Item2) * n); continue; }
              vs.Add(3 * vi); cs.Add(w * n.X); vs.Add(3 * vi + 1); cs.Add(w * n.Y); vs.Add(3 * vi + 2); cs.Add(w * n.Z);
            }
            if (vs.Count > 0) rows.Add((vs.ToArray(), cs.ToArray(), r));
          }
        }
        int Nn = 3 * x0.Count;
        if (Nn == 0 || rows.Count == 0) return false;
        // Least squares with a small pull to the smooth solution: the row moves as little as it can.
        var A = new double[Nn * Nn]; var rhs = new double[Nn];
        foreach (var (v, c, r) in rows)
          for (int a = 0; a < v.Length; a++)
          {
            rhs[v[a]] += c[a] * r;
            for (int b2 = 0; b2 < v.Length; b2++) A[v[a] * Nn + v[b2]] += c[a] * c[b2];
          }
        const double lam = 1e-4;
        for (int q2 = 0; q2 < x0.Count; q2++)
        {
          double[] xc = { x0[q2].X, x0[q2].Y, x0[q2].Z };
          for (int c = 0; c < 3; c++) { A[(3 * q2 + c) * Nn + 3 * q2 + c] += lam; rhs[3 * q2 + c] += lam * xc[c]; }
        }
        if (!NSidedFill.BandCholeskySolve(A, rhs, Nn, Nn - 1)) return false;
        foreach (var kv in idx) pin[kv.Key.Item1, kv.Key.Item2] = new Point3d(rhs[3 * kv.Value], rhs[3 * kv.Value + 1], rhs[3 * kv.Value + 2]);
        return true;
      }

      /// <summary>Adds this piece's normal equations into the (joint) system. Joint: shared sides are left to the coupling.</summary>
      public void Assemble(NurbsSurface prev, BandMatrix M, double[] rhs, int N, bool joint)
      {
        int p = P, nU = NU, nV = NV;
        var rowIdx = new int[3 * (p + 1) * (p + 1)];
        var rowVal = new double[rowIdx.Length];
        void AddRow(int count, double target, double w)
        {
          for (int a = 0; a < count; a++)
          {
            double wa = w * rowVal[a];
            int ia = rowIdx[a];
            rhs[ia] += wa * target;
            for (int b = 0; b < count; b++) M.Add(ia, rowIdx[b], wa * rowVal[b]);
          }
        }
        int FillRow(int su, double[,] Bu, int du, int sv, double[,] Bv, int dv, Vector3d? nrm, int c)
        {
          int k = 0;
          for (int a = 0; a <= p; a++)
            for (int b = 0; b <= p; b++)
            {
              double bv = Bu[du, a] * Bv[dv, b];
              if (nrm == null) { rowIdx[k] = Idx(su - p + a, sv - p + b, c); rowVal[k++] = bv; }
              else
              {
                var nn = nrm.Value;
                rowIdx[k] = Idx(su - p + a, sv - p + b, 0); rowVal[k++] = bv * nn.X;
                rowIdx[k] = Idx(su - p + a, sv - p + b, 1); rowVal[k++] = bv * nn.Y;
                rowIdx[k] = Idx(su - p + a, sv - p + b, 2); rowVal[k++] = bv * nn.Z;
              }
            }
          return k;
        }

        // a. Bending energy in true proportions (u spans Lu, v spans Lv), plus membrane for tautness.
        //    (Bending energy doesn't depend on the frame's scale, so pieces in one frame weigh the same per area.)
        // Exact integration: Gauss points per knot span (the integrand is a polynomial on each span). Coarse
        // midpoint sampling let thin bends right at a border (one sample deep) cost almost nothing, which shows
        // as a fine highlight line along edges and seams.
        var gu = GaussEnergy ? Quadrature(U, SpU) : Midpoints(3 * Math.Max(SpU, 2) + 3);
        var gv = GaussEnergy ? Quadrature(V, SpV) : Midpoints(3 * Math.Max(SpV, 2) + 3);
        double tm = 60 * Opt.Tautness * Opt.Tautness;
        foreach (var (tu, au) in gu)
          foreach (var (tv, av) in gv)
          {
            double area = Lu * Lv * au * av;
            double wuu = area / Math.Pow(Lu, 4), wuv = 2 * area / (Lu * Lu * Lv * Lv), wvv = area / Math.Pow(Lv, 4);
            double wmu = area * tm / (Lu * Lu), wmv = area * tm / (Lv * Lv);
            NSidedFill.Basis(U, nU, p, tu, 2, out int su, out double[,] Bu);
            NSidedFill.Basis(V, nV, p, tv, 2, out int sv, out double[,] Bv);
            for (int c = 0; c < 3; c++)
            {
              AddRow(FillRow(su, Bu, 2, sv, Bv, 0, null, c), 0, wuu);
              AddRow(FillRow(su, Bu, 1, sv, Bv, 1, null, c), 0, wuv);
              AddRow(FillRow(su, Bu, 0, sv, Bv, 2, null, c), 0, wvv);
              if (tm > 0)
              {
                AddRow(FillRow(su, Bu, 1, sv, Bv, 0, null, c), 0, wmu);
                AddRow(FillRow(su, Bu, 0, sv, Bv, 1, null, c), 0, wmv);
              }
            }
          }

        // b. Border rows pinned; everything else weakly pulled toward the Coons patch.
        const double wPin = 1e9, wReg = 1e-8;
        for (int i = 0; i < nU; i++)
          for (int j = 0; j < nV; j++)
          {
            var tgt = Pin[i, j] ?? coons[i, j];
            double w = Pin[i, j].HasValue ? wPin : wReg;
            double[] tc = { tgt.X, tgt.Y, tgt.Z };
            for (int c = 0; c < 3; c++) { int ix = Idx(i, j, c); M.Add(ix, ix, w); rhs[ix] += w * tc[c]; }
          }

        // c. Continuity rows across each border.
        foreach (var k in cons)
        {
          if (joint && k.inner) continue;
          NSidedFill.Basis(U, nU, p, k.u, 2, out int su, out double[,] Bu);
          NSidedFill.Basis(V, nV, p, k.v, 2, out int sv, out double[,] Bv);
          int du1 = k.crossV ? 0 : 1, dv1 = k.crossV ? 1 : 0;
          AddRow(FillRow(su, Bu, du1, sv, Bv, dv1, k.nrm, 0), 0, Opt.TangentWeight * k.tramp);

          if (k.c == Continuity.G2 && prev != null && k.ramp > 0 &&
              prev.Evaluate(k.u, k.v, 1, out Point3d _, out Vector3d[] pd) &&
              NSidedFill.SecondForm(k.face, k.fuv.X, k.fuv.Y, k.nrm, out var II))
          {
            var dc = k.crossV ? pd[1] : pd[0];   // world cross-boundary derivative
            int du2 = k.crossV ? 0 : 2, dv2 = k.crossV ? 2 : 0;
            AddRow(FillRow(su, Bu, du2, sv, Bv, dv2, k.nrm, 0), II(dc, dc) / D, Opt.CurvatureWeight * k.ramp);
          }
        }
      }
    }

    internal static bool GaussEnergy = true;
    static List<(double t, double w)> Midpoints(int g) => Enumerable.Range(0, g).Select(i => ((i + 0.5) / g, 1.0 / g)).ToList();

    /// <summary>4-point Gauss–Legendre nodes and weights on every span of a uniform clamped knot vector on [0,1].</summary>
    static List<(double t, double w)> Quadrature(double[] knots, int spans)
    {
      double[] x = { -0.8611363115940526, -0.3399810435848563, 0.3399810435848563, 0.8611363115940526 };
      double[] w = { 0.3478548451374538, 0.6521451548625461, 0.6521451548625461, 0.3478548451374538 };
      var res = new List<(double, double)>();
      var ks = knots.Distinct().OrderBy(k => k).ToList();
      for (int s = 0; s + 1 < ks.Count; s++)
      {
        double a = ks[s], b = ks[s + 1], h = 0.5 * (b - a);
        for (int g = 0; g < 4; g++) res.Add((a + h * (1 + x[g]), h * w[g]));
      }
      return res;
    }

    static Point3d Mid(Point3d a, Point3d b) => new Point3d(0.5 * (a.X + b.X), 0.5 * (a.Y + b.Y), 0.5 * (a.Z + b.Z));

    /// <summary>
    /// Least-squares fit of one side with the given knots. Local coordinates.
    /// The first and last control points carry the side's exact position, tangent and curvature at its
    /// ends (3 each when there are enough), so the curvature where two sides meet is the real one.
    /// </summary>
    static Point3d[] FitSide(Side side, double[] U, int n, int p, Func<Point3d, Point3d> toLocal, bool exactStart, bool exactEnd)
    {
      int m = Math.Max(60, 8 * n);
      int full = p >= 2 && n >= 6 ? 3 : n >= 4 ? 2 : 1;
      int e0 = exactStart ? full : 1, e1 = exactEnd ? full : 1;   // control points fixed at each end
      var res = new Point3d[n];
      var head = EndPoints(side, 0, U, p, e0, toLocal);
      var Ur = new double[U.Length]; for (int i = 0; i < U.Length; i++) Ur[i] = U[U.Length - 1] - U[U.Length - 1 - i];
      var tail = EndPoints(side, 1, Ur, p, e1, toLocal);
      for (int i = 0; i < e0; i++) res[i] = head[i];
      for (int i = 0; i < e1; i++) res[n - 1 - i] = tail[i];
      int k = n - e0 - e1;                   // free interior control points
      if (k <= 0) return res;
      var A = new double[k * k];
      var rhs = new double[3][]; for (int c = 0; c < 3; c++) rhs[c] = new double[k];
      var row = new double[n];
      for (int s = 0; s <= m; s++)
      {
        double t = s / (double)m;
        var q = toLocal(side.At(t));
        NSidedFill.Basis(U, n, p, t, 0, out int span, out double[,] B);
        Array.Clear(row, 0, n);
        for (int a = 0; a <= p; a++) row[span - p + a] = B[0, a];
        double[] r = { q.X, q.Y, q.Z };
        for (int a = 0; a < n; a++)
          if (a < e0 || a >= n - e1) { r[0] -= row[a] * res[a].X; r[1] -= row[a] * res[a].Y; r[2] -= row[a] * res[a].Z; }
        for (int a = e0; a < n - e1; a++)
        {
          if (row[a] == 0) continue;
          for (int b = e0; b < n - e1; b++) A[(a - e0) * k + (b - e0)] += row[a] * row[b];
          for (int c = 0; c < 3; c++) rhs[c][a - e0] += row[a] * r[c];
        }
      }
      for (int i = 0; i < k; i++) A[i * k + i] += 1e-12;
      for (int c = 0; c < 3; c++)
      {
        var Ac = (double[])A.Clone();
        if (!NSidedFill.BandCholeskySolve(Ac, rhs[c], k, k)) return null;
      }
      for (int i = 0; i < k; i++) res[i + e0] = new Point3d(rhs[0][i], rhs[1][i], rhs[2][i]);
      return res;
    }

    /// <summary>
    /// The first <paramref name="e"/> control points of a clamped B-spline that reproduce the side's
    /// position, first and second derivative at end <paramref name="at"/> (0 = start, 1 = end, then
    /// <paramref name="U"/> is the reversed knot vector and the curve is walked backwards).
    /// </summary>
    static Point3d[] EndPoints(Side side, double at, double[] U, int p, int e, Func<Point3d, Point3d> toLocal)
    {
      var (piece, t) = side.Locate(at);
      var P = side.At(at);
      var d1 = piece.C.TangentAt(t) * side.Len;            // d/ds with s = arc-length fraction
      var d2 = piece.C.CurvatureAt(t) * (side.Len * side.Len);
      if (at > 0.5) d1 = -d1;                                // walking backwards
      var L0 = toLocal(P);
      Vector3d Lv(Vector3d v) => toLocal(P + v) - L0;        // toLocal is affine
      var o = new Point3d[e];
      o[0] = L0;
      if (e > 1) o[1] = L0 + Lv(d1) * (U[p + 1] / p);
      if (e > 2)
      {
        double h1 = U[p + 1], h2 = U[p + 2];
        var v1 = (o[1] - o[0]) / h1;
        o[2] = o[1] + (Lv(d2) * (h1 / (p * (p - 1.0))) + v1) * h2;
      }
      return o;
    }

    /// <summary>True if the surface normal flips anywhere (a fold) — then the trimmed method is safer.</summary>
    static bool Folded(NurbsSurface s)
    {
      // Neighbouring normals on a fine grid must agree (a smooth but strongly curved patch is fine).
      const int G = 24;
      var nm = new Vector3d[G + 1, G + 1];
      for (int i = 0; i <= G; i++)
        for (int j = 0; j <= G; j++)
        {
          var nrm = s.NormalAt(s.Domain(0).ParameterAt(i / (double)G), s.Domain(1).ParameterAt(j / (double)G));
          if (!nrm.IsValid || nrm.IsZero) return true;
          nrm.Unitize();
          nm[i, j] = nrm;
          if (i > 0 && nm[i - 1, j] * nrm < 0.5) return true;
          if (j > 0 && nm[i, j - 1] * nrm < 0.5) return true;
        }
      return false;
    }
  }
}
