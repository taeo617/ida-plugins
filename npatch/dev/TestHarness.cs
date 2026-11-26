// Dev-only: compiled together with src/*.cs inside Rhino (see dev/run-in-rhino.csx) to test the solver
// without building the plugin. Not part of the .rhp.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace NPatch.Dev
{
  public static class TestHarness
  {
    /// <summary>Continuity levels to run (0 = G0, 1 = G1, 2 = G2).</summary>
    public static int[] Modes = { 0, 1, 2 };
    public static int Degree = 3;
    public static double Tautness = 0;

    /// <summary>
    /// 4-sided holes in a sphere (R=10) and a wavy surface: compares the untrimmed 4-sided method with
    /// the trimmed one (deviation from the original surface, per-edge continuity). Results go to layer Quad_Result.
    /// </summary>
    public static void QuadTest(RhinoDoc doc, StringBuilder output)
    {
      double tol = doc?.ModelAbsoluteTolerance ?? 0.001;   // doc == null: measure only, add nothing
      int layer = doc == null ? -1 : doc.Layers.FindByFullPath("Quad_Result", -1);
      if (doc != null && layer < 0) layer = doc.Layers.Add("Quad_Result", System.Drawing.Color.SteelBlue);
      if (doc != null) foreach (var o in doc.Objects.FindByLayer("Quad_Result")) doc.Objects.Delete(o, true);

      // (name, holed brep, distance-to-original function)
      var cases = new List<(string name, Brep holed, Func<Point3d, double> dev)>();

      var sphere = new Sphere(Point3d.Origin, 10).ToBrep();
      cases.Add(("sphere", Holed(sphere, 4.5, 0, 0, tol), p => Math.Abs(p.DistanceTo(Point3d.Origin) - 10)));

      Func<double, double, double> wave = (x, y) => 2.0 * Math.Sin(x * 0.25) * Math.Cos(y * 0.2) + 0.03 * x * y;
      var pts = new List<Point3d>();
      for (int i = 0; i <= 30; i++) for (int j = 0; j <= 30; j++) { double x = -15 + i, y = -15 + j; pts.Add(new Point3d(x, y, wave(x, y))); }
      var waveSrf = NurbsSurface.CreateThroughPoints(pts, 31, 31, 3, 3, false, false);
      cases.Add(("wave", Holed(waveSrf.ToBrep(), 5, 1.5, -1, tol), p => Math.Abs(p.Z - wave(p.X, p.Y))));

      foreach (var (name, holed, dev) in cases)
      {
        if (holed == null) { output.AppendLine(name + ": could not make the hole"); continue; }
        var inputs = new List<BoundaryInput>();
        var hc = new Point3d(name == "sphere" ? 0 : 1.5, name == "sphere" ? 0 : -1, 0);
        foreach (var e in holed.Edges)
        {
          var mp = e.PointAtNormalizedLength(0.5);
          if (e.Valence == EdgeAdjacency.Naked && new Point3d(mp.X, mp.Y, 0).DistanceTo(hc) < 8)
            inputs.Add(new BoundaryInput { Curve = e.DuplicateCurve(), AdjacentFace = holed.Faces[e.AdjacentFaces()[0]], Continuity = Continuity.G2 });
        }
        output.AppendLine($"== {name}: {inputs.Count} edges");
        foreach (bool untrimmed in new[] { true, false })
        {
          var r = NSidedFill.Solve(inputs, new FillOptions { Degree = Degree, PreferUntrimmed = untrimmed, SpanCount = untrimmed ? 6 : 10 }, tol);
          output.Append($"  {(r.Untrimmed ? "untrimmed" : "trimmed  ")}: {r.Milliseconds} ms, deg {r.Degree}, spans {r.SpanCount}, posErr {r.MaxPositionError:E1}");
          if (r.Brep == null) { output.AppendLine("  FAILED " + r.Message); continue; }
          double dmax = 0;
          foreach (var m in Mesh.CreateFromBrep(r.Brep, MeshingParameters.QualityRenderMesh)) foreach (var v in m.Vertices) dmax = Math.Max(dmax, dev(v));
          var joined = Brep.JoinBreps(new[] { holed, r.Brep }, tol * 10);
          output.AppendLine($", maxDev {dmax:F4}, join {joined?.Length}, naked after join {joined?[0].Edges.Count(x => x.Valence == EdgeAdjacency.Naked)}");
          foreach (var e in r.Edges)
            output.AppendLine($"     E{e.Index + 1}: gap {Math.Max(e.Gap, e.CornerGap):E1}  ang {e.AngleDeg:F3}/{e.CornerAngleDeg:F3}°  k {e.CurvaturePct:F1}/{e.CornerCurvaturePct:F1}%");
          var at = new ObjectAttributes { LayerIndex = layer, Name = $"{name}_{(r.Untrimmed ? "untrimmed" : "trimmed")}" };
          var xf = Transform.Translation(untrimmed ? 0 : 0, 0, 0);
          var b = r.Brep.DuplicateBrep(); b.Transform(Transform.Translation(name == "sphere" ? -80 : 80, untrimmed ? 0 : 40, 0));
          var h = holed.DuplicateBrep(); h.Transform(Transform.Translation(name == "sphere" ? -80 : 80, untrimmed ? 0 : 40, 0));
          if (doc != null) { doc.Objects.AddBrep(h, at); doc.Objects.AddBrep(b, at); }
        }
      }
      doc?.Views.Redraw();
    }

    /// <summary>Cuts a square hole (half-size a, centred at cx,cy seen from +Z) out of the top of <paramref name="b"/>.</summary>
    static Brep Holed(Brep b, double a, double cx, double cy, double tol) => Holed(b, a * Math.Sqrt(2), cx, cy, 4, Math.PI / 4, tol);

    /// <summary>3/5/6-sided holes in the sphere and the wave: split into 4-sided pieces vs trimmed.</summary>
    public static void SplitTest(RhinoDoc doc, StringBuilder output)
    {
      double tol = doc?.ModelAbsoluteTolerance ?? 0.001;   // doc == null: measure only, add nothing
      int layer = doc == null ? -1 : doc.Layers.FindByFullPath("Split_Result", -1);
      if (doc != null && layer < 0) layer = doc.Layers.Add("Split_Result", System.Drawing.Color.DarkOrange);
      if (doc != null) foreach (var o in doc.Objects.FindByLayer("Split_Result")) doc.Objects.Delete(o, true);
      Func<double, double, double> wave = (x, y) => 2.0 * Math.Sin(x * 0.25) * Math.Cos(y * 0.2) + 0.03 * x * y;
      var pts = new List<Point3d>();
      for (int i = 0; i <= 30; i++) for (int j = 0; j <= 30; j++) { double x = -15 + i, y = -15 + j; pts.Add(new Point3d(x, y, wave(x, y))); }
      var waveB = NurbsSurface.CreateThroughPoints(pts, 31, 31, 3, 3, false, false).ToBrep();
      var sphB = new Sphere(Point3d.Origin, 10).ToBrep();
      int col = 0;
      foreach (int n in new[] { 3, 5, 6 })
        foreach (var name in new[] { "sphere", "wave" })
        {
          bool sph = name == "sphere";
          double cx = sph ? 0 : 1.5, cy = sph ? 0 : -1;
          var holed = Holed(sph ? sphB : waveB, sph ? 5.5 : 6.5, cx, cy, n, 0.3, tol);
          Func<Point3d, double> dev = sph ? (Func<Point3d, double>)(p => Math.Abs(p.DistanceTo(Point3d.Origin) - 10)) : p => Math.Abs(p.Z - wave(p.X, p.Y));
          if (holed == null) { output.AppendLine($"{name} {n}: no hole"); continue; }
          var inputs = new List<BoundaryInput>();
          foreach (var e in holed.Edges)
          {
            var mp = e.PointAtNormalizedLength(0.5);
            if (e.Valence == EdgeAdjacency.Naked && new Point3d(mp.X, mp.Y, 0).DistanceTo(new Point3d(cx, cy, 0)) < 9)
              inputs.Add(new BoundaryInput { Curve = e.DuplicateCurve(), AdjacentFace = holed.Faces[e.AdjacentFaces()[0]], Continuity = Continuity.G2 });
          }
          output.AppendLine($"== {name} {n}-gon: {inputs.Count} edges");
          int row = 0;
          foreach (bool untrimmed in new[] { true, false })
          {
            var r = NSidedFill.Solve(inputs, new FillOptions { Degree = Degree, PreferUntrimmed = false, SpanCount = 10 }, tol);
            if (untrimmed) r = NSidedFill.TrySplit(inputs, r.Surface, new FillOptions { Degree = Degree, SpanCount = 10 }, tol) ?? r;
            output.Append($"  {(r.Untrimmed ? $"split x{r.Surfaces.Count}" : "trimmed ")}: {r.Milliseconds} ms, spans {r.SpanCount}, posErr {r.MaxPositionError:E1}");
            if (r.Brep == null) { output.AppendLine("  FAILED " + r.Message); continue; }
            double dmax = 0;
            foreach (var m in Mesh.CreateFromBrep(r.Brep, MeshingParameters.QualityRenderMesh)) foreach (var v in m.Vertices) dmax = Math.Max(dmax, dev(v));
            var joined = Brep.JoinBreps(new[] { holed, r.Brep }, tol * 10);
            output.AppendLine($", maxDev {dmax:F4}, seam {r.InnerSeamAngleDeg:F3}° k {r.InnerSeamCurvaturePct:F1}%, join {joined?.Length}");
            double kE = r.Edges.Max(e => e.CurvaturePct), kC = r.Edges.Max(e => e.CornerCurvaturePct), ang = r.Edges.Max(e => Math.Max(e.AngleDeg, e.CornerAngleDeg));
            output.AppendLine($"     worst edge: ang {ang:F3}°  k {kE:F1}/{kC:F1}%  ok {r.AllOk}");
            var at = new ObjectAttributes { LayerIndex = layer, Name = $"{name}{n}_{(r.Untrimmed ? "split" : "trimmed")}" };
            var xf = Transform.Translation(-60 + 40 * col, 150 + (untrimmed ? 0 : 40), 0);
            var b = r.Brep.DuplicateBrep(); b.Transform(xf); var h = holed.DuplicateBrep(); h.Transform(xf);
            if (doc != null) { doc.Objects.AddBrep(h, at); doc.Objects.AddBrep(b, at); }
            row++;
          }
          col++;
        }
      doc?.Views.Redraw();
    }

    /// <summary>Curvature error along each edge of the wave 5-gon split fill (where along the edge it goes wrong).</summary>
    public static bool ProfSphere; public static int ProfN = 5;
    public static void SplitProfile(RhinoDoc doc, StringBuilder output)
    {
      double tol = doc.ModelAbsoluteTolerance;
      Func<double, double, double> wave = (x, y) => 2.0 * Math.Sin(x * 0.25) * Math.Cos(y * 0.2) + 0.03 * x * y;
      var pts = new List<Point3d>();
      for (int i = 0; i <= 30; i++) for (int j = 0; j <= 30; j++) { double x = -15 + i, y = -15 + j; pts.Add(new Point3d(x, y, wave(x, y))); }
      double pcx = ProfSphere ? 0 : 1.5, pcy = ProfSphere ? 0 : -1;
      var holed = ProfSphere ? Holed(new Sphere(Point3d.Origin, 10).ToBrep(), 5.5, 0, 0, ProfN, 0.3, tol) : Holed(NurbsSurface.CreateThroughPoints(pts, 31, 31, 3, 3, false, false).ToBrep(), 6.5, 1.5, -1, ProfN, 0.3, tol);
      var inputs = new List<BoundaryInput>();
      foreach (var e in holed.Edges)
      {
        var mp = e.PointAtNormalizedLength(0.5);
        if (e.Valence == EdgeAdjacency.Naked && new Point3d(mp.X, mp.Y, 0).DistanceTo(new Point3d(pcx, pcy, 0)) < 9)
          inputs.Add(new BoundaryInput { Curve = e.DuplicateCurve(), AdjacentFace = holed.Faces[e.AdjacentFaces()[0]], Continuity = Continuity.G2 });
      }
      var g = NSidedFill.Solve(inputs, new FillOptions { Degree = Degree, SpanCount = 10, PreferUntrimmed = false }, tol);
      var dbg = new StringBuilder();
      var r = NSidedFill.Solve(inputs, new FillOptions { Degree = Degree, SpanCount = 10 }, tol);
      output.AppendLine($"pieces {r.Surfaces.Count}");
      foreach (var b in inputs)
      {
        var sb = new StringBuilder();
        for (int i = 0; i <= 20; i++)
        {
          double t = b.Curve.Domain.ParameterAt(i / 20.0);
          var p = b.Curve.PointAt(t);
          b.AdjacentFace.ClosestPoint(p, out double fu, out double fv);
          var n = b.AdjacentFace.NormalAt(fu, fv); n.Unitize();
          var cross = Vector3d.CrossProduct(n, b.Curve.TangentAt(t)); cross.Unitize();
          NurbsSurface best = null; double bu = 0, bv = 0, bd = 1e9; int bi = -1;
          for (int k = 0; k < r.Surfaces.Count; k++) { var s = r.Surfaces[k]; s.ClosestPoint(p, out double u, out double v); double d = s.PointAt(u, v).DistanceTo(p); if (d < bd) { bd = d; best = s; bu = u; bv = v; bi = k; } }
          NSidedFill.NormalCurvature(best, bu, bv, n, cross, out double kS);
          NSidedFill.NormalCurvature(b.AdjacentFace, fu, fv, n, cross, out double kF);
          sb.Append($" {bi}:{100 * Math.Abs(kS - kF) / Math.Max(Math.Abs(kF), 1e-9):F0}");
          if (false) { g.Surface.ClosestPoint(p, out double gu, out double gv); NSidedFill.NormalCurvature(g.Surface, gu, gv, n, cross, out double kG); dbg.AppendLine($"   e5 s{i}: kF {kF:F5} kS {kS:F5} kGuide {kG:F5}"); }
        }
        output.AppendLine(sb.ToString());
      }
      output.Append(dbg);
    }

    /// <summary>Cuts a regular n-gon hole (circumradius r, centred at cx,cy seen from +Z).</summary>
    static Brep Holed(Brep b, double r, double cx, double cy, int n, double rot, double tol)
    {
      var poly = new Polyline(Enumerable.Range(0, n + 1).Select(i => { double a = rot + 2 * Math.PI * (i % n) / n; return new Point3d(cx + r * Math.Cos(a), cy + r * Math.Sin(a), 50); }));
      var sq = poly.ToNurbsCurve();
      var proj = Curve.ProjectToBrep(new[] { sq }, new[] { b }, -Vector3d.ZAxis, tol);
      if (proj == null || proj.Length == 0) return null;
      // Keep only the upper projection (sphere has two).
      var top = Curve.JoinCurves(proj, tol * 10).OrderByDescending(c => c.PointAtStart.Z).First();
      var split = b.Split(new[] { top }, tol);
      if (split == null || split.Length == 0) split = b.Faces[0].Split(new[] { top }, tol)?.Faces.Select(f => f.DuplicateFace(false)).ToArray();
      if (split == null || split.Length < 2) return null;
      // Remove the piece that contains the hole centre.
      var probe = new Line(new Point3d(cx, cy, 50), new Point3d(cx, cy, -50));
      Brep keep = null; double best = double.MinValue;
      foreach (var piece in split)
      {
        Rhino.Geometry.Intersect.Intersection.CurveBrep(probe.ToNurbsCurve(), piece, tol, out _, out Point3d[] hits);
        double score = hits != null && hits.Length > 0 ? -hits.Max(p => p.Z) : piece.GetArea();
        // Bigger area and no hit near the top = the holed piece.
        if (hits == null || hits.Length == 0 || hits.Max(p => p.Z) < top.PointAtStart.Z - 1) score = piece.GetArea() + 1e6;
        if (score > best) { best = score; keep = piece; }
      }
      return keep;
    }

    /// <summary>Checks the double-click loop finder on the Test_Multi model.</summary>
    public static void LoopTest(RhinoDoc doc, StringBuilder output)
    {
      foreach (var o in doc.Objects.FindByLayer("Test_Multi"))
      {
        var b = o.Geometry as Brep ?? (o.Geometry as Surface)?.ToBrep();
        if (b == null) continue;
        foreach (var e in b.Edges)
        {
          if (e.Valence != EdgeAdjacency.Naked) continue;
          var mid = e.PointAtNormalizedLength(0.5);
          double r = new Point3d(mid.X, mid.Y, 0).DistanceTo(Point3d.Origin);
          string kind = r < 13 ? "hole" : (Math.Abs(mid.X) > 29.9 || Math.Abs(mid.Y) > 29.9) ? "outer" : "seam";
          var loop = BoundaryPicker.FindLoop(doc, o.Id, e.EdgeIndex);
          output.AppendLine($"{o.Id.ToString().Substring(0, 6)} e{e.EdgeIndex} {kind,-5} -> {(loop == null ? "null" : loop.Count + " edges from " + loop.Select(x => x.id).Distinct().Count() + " objects")}");
        }
      }
    }

    public static void Run(RhinoDoc doc, StringBuilder output)
    {
      var holedObj = doc.Objects.FindByLayer("Test_Holed").FirstOrDefault();
      if (holedObj == null) { output.AppendLine("No object on layer Test_Holed."); return; }
      var holed = ((BrepObject)holedObj).BrepGeometry;

      int layer = doc.Layers.FindByFullPath("Fill_Result", -1);
      if (layer < 0) layer = doc.Layers.Add("Fill_Result", System.Drawing.Color.OrangeRed);
      foreach (var o in doc.Objects.FindByLayer("Fill_Result")) doc.Objects.Delete(o, true);

      foreach (int g in Modes)
      {
        var inputs = new List<BoundaryInput>();
        foreach (var e in holed.Edges)
          if (e.Valence == EdgeAdjacency.Naked)
            inputs.Add(new BoundaryInput { Curve = e.DuplicateCurve(), AdjacentFace = holed.Faces[e.AdjacentFaces()[0]], Continuity = (Continuity)g });

        var r = NSidedFill.Solve(inputs, new FillOptions { Degree = Degree, Tautness = Tautness }, doc.ModelAbsoluteTolerance);
        output.AppendLine($"G{g}: {r.Milliseconds} ms, deg {r.Degree}, spans {r.SpanCount}, posErr {r.MaxPositionError:E2}, allOk {r.AllOk}, msg '{r.Message}'");
        foreach (var e in r.Edges)
          output.AppendLine($"   E{e.Index + 1} {e.Effective}: gap {e.Gap:E1}/{e.CornerGap:E1}  ang {e.AngleDeg:F3}/{e.CornerAngleDeg:F3}°  k {e.CurvaturePct:F1}/{e.CornerCurvaturePct:F1}%  ok {e.Ok}");
        if (r.Brep == null) continue;

        var mesh = Mesh.CreateFromBrep(r.Brep, MeshingParameters.Default);
        double dmax = 0;
        foreach (var m in mesh) foreach (var v in m.Vertices) dmax = Math.Max(dmax, Math.Abs(((Point3d)v).DistanceTo(Point3d.Origin) - 10));
        var joined = Brep.JoinBreps(new[] { holed, r.Brep }, doc.ModelAbsoluteTolerance * 10);
        output.AppendLine($"   max |r-10| = {dmax:F4}; join -> {joined?.Length} brep(s), solid={joined?[0].IsSolid}");

        if (g == Modes.Max()) doc.Objects.AddBrep(r.Brep, new ObjectAttributes { LayerIndex = layer, Name = $"NPatch_G{g}" });
      }
      doc.Views.Redraw();
    }
  }
}
