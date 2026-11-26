using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;

namespace NPatch
{
  /// <summary>
  /// NPatch: interactive N-sided fill. Pick the edges/curves around a hole (and optional interior
  /// curves/points), then tune everything in a live panel while the viewport previews the result.
  /// </summary>
  public class NPatchCommand : Command
  {
    public override string EnglishName => "NPatch";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      if (!BoundaryPicker.Pick(doc, "구멍 둘레의 엣지나 커브를 선택하세요 (3개 이상 또는 닫힌 커브 1개). 끝나면 Enter",
            out var inputs, out var sourceId, out var sourceBrep, out var res))
        return res;

      var interior = PickInterior(doc);
      if (interior == null) return Result.Cancel;

      var defC = (Continuity)Math.Max(0, Math.Min(2, PatchSettings.DefaultContinuity));
      foreach (var b in inputs) b.Continuity = defC;
      var opt = new FillOptions
      {
        Degree = PatchSettings.Degree,
        SpanCount = PatchSettings.Spans,
        AutoRefine = PatchSettings.AutoRefine,
        Tautness = PatchSettings.Tautness,
        InteriorPoints = interior,
      };
      var session = new PatchSession(inputs, opt, doc.ModelAbsoluteTolerance, sourceId, sourceBrep);
      session.SolveNow();

      var conduit = new PatchConduit(session)
      {
        ShowZebra = PatchSettings.Zebra,
        ShowCombs = PatchSettings.Combs,
        ShowLabels = PatchSettings.Labels,
        ShowControlPoints = PatchSettings.ControlPoints,
        CombScale = PatchSettings.CombScale,
      };
      var dlg = new PatchDialog(session, conduit);
      var clicker = new LabelClicker(conduit, dlg);
      bool ok;
      try
      {
        conduit.Enabled = true;
        clicker.Enabled = true;
        doc.Views.Redraw();
        ok = dlg.ShowSemiModal(doc, RhinoEtoApp.MainWindow);
      }
      finally
      {
        clicker.Enabled = false;
        conduit.Enabled = false;
        doc.Views.Redraw();
      }
      if (!ok || session.Result?.Brep == null) return Result.Cancel;

      // Remember the continuity the user ended on as the next default.
      var used = session.Inputs.Where(b => b.AdjacentFace != null).Select(b => (int)b.Continuity).ToList();
      if (used.Count > 0) PatchSettings.DefaultContinuity = used.Max();

      Output.Commit(doc, session.Result, dlg.JoinToSource ? Output.SourceIds(session.Inputs) : null, doc.ModelAbsoluteTolerance);
      RhinoApp.WriteLine(Output.Summary(session.Result));
      return Result.Success;
    }

    static List<Point3d> PickInterior(RhinoDoc doc)
    {
      var go = new GetObject();
      go.SetCommandPrompt("패치가 지나갈 내부 커브/점 선택 (없으면 Enter)");
      go.GeometryFilter = ObjectType.Curve | ObjectType.Point;
      go.AcceptNothing(true);
      go.EnablePreSelect(false, true);
      go.DeselectAllBeforePostSelect = false;
      var r = go.GetMultiple(1, 0);
      if (r == GetResult.Nothing) return new List<Point3d>();
      if (r != GetResult.Object) return null;
      var pts = new List<Point3d>();
      for (int i = 0; i < go.ObjectCount; i++)
      {
        var o = go.Object(i);
        if (o.Point() is Rhino.Geometry.Point p) pts.Add(p.Location);
        else if (o.Curve() is Curve c)
        {
          var ts = c.DivideByCount(30, true);
          if (ts != null) pts.AddRange(ts.Select(c.PointAt));
        }
      }
      return pts;
    }

    /// <summary>Click an edge label in the viewport to cycle its continuity.</summary>
    class LabelClicker : MouseCallback
    {
      readonly PatchConduit _conduit;
      readonly PatchDialog _dlg;
      public LabelClicker(PatchConduit conduit, PatchDialog dlg) { _conduit = conduit; _dlg = dlg; }

      protected override void OnMouseDown(MouseCallbackEventArgs e)
      {
        if (e.MouseButton != MouseButton.Left || !_conduit.ShowLabels) return;
        var vp = e.View.ActiveViewport;
        var click = new Point2d(e.ViewportPoint.X, e.ViewportPoint.Y);
        foreach (var (edge, screen) in _conduit.LabelScreenPoints(vp))
        {
          if (screen.DistanceTo(click) > 22) continue;
          _dlg.CycleEdge(edge);
          e.Cancel = true;
          return;
        }
      }
    }
  }


  static class Output
  {
    /// <summary>Object ids of the surfaces around the hole (edges picked from curves have none).</summary>
    public static List<Guid> SourceIds(IEnumerable<BoundaryInput> inputs) =>
      inputs.Select(b => b.SourceObjectId).Where(id => id != Guid.Empty).Distinct().ToList();

    /// <summary>
    /// Adds the patch. When <paramref name="joinIds"/> is given, the patch and those objects are joined
    /// into one polysurface that replaces the first object (the others are deleted). If they don't all
    /// join into one piece, nothing is merged and the patch is added on its own.
    /// </summary>
    public static void Commit(RhinoDoc doc, FillResult res, IList<Guid> joinIds, double tol)
    {
      if (joinIds != null && joinIds.Count > 0)
      {
        var breps = new List<Brep>();
        foreach (var id in joinIds)
        {
          var o = doc.Objects.FindId(id);
          var b = o?.Geometry as Brep ?? (o?.Geometry as Surface)?.ToBrep() ?? (o?.Geometry as Extrusion)?.ToBrep();
          if (b != null) breps.Add(b);
        }
        if (breps.Count == joinIds.Count)
        {
          breps.Add(res.Brep);
          var joined = Brep.JoinBreps(breps, tol * 10);
          if (joined != null && joined.Length == 1)
          {
            doc.Objects.Replace(joinIds[0], joined[0]);
            for (int i = 1; i < joinIds.Count; i++) doc.Objects.Delete(joinIds[i], true);
            doc.Views.Redraw();
            return;
          }
        }
        RhinoApp.WriteLine("옆 면들과 한 덩어리로 이어지지 않아서 새 면만 따로 추가했어요.");
      }
      doc.Objects.AddBrep(res.Brep);
      doc.Views.Redraw();
    }

    public static string Summary(FillResult r)
    {
      int bad = r.Edges.Count(e => !e.Ok);
      return $"NPatch: {r.Edges.Count}개 엣지, 차수 {r.Degree}, {r.SpanCount} 스팬, {r.Milliseconds} ms — " +
             (bad == 0 ? "모든 엣지 허용오차 이내" : $"{bad}개 엣지 허용오차 초과");
    }
  }
}
