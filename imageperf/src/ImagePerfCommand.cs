using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;

namespace ImagePerf
{
  /// <summary>
  /// ImagePerf: image-driven perforation. Pick surfaces/polysurfaces (or single faces with Ctrl+Shift),
  /// then tune everything in the panel while the viewport previews the holes.
  /// -ImagePerf runs without the panel; all settings are command-line options (for scripts and the agent).
  /// </summary>
  public class ImagePerfCommand : Command
  {
    public override string EnglishName => "ImagePerf";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var opt = PerfOptions.Load();
      var targets = PickTargets(doc, opt, mode == RunMode.Scripted, out var res);
      if (targets == null) return res;

      double tol = doc.ModelAbsoluteTolerance;
      var session = new PerfSession(targets, opt, tol);
      if (opt.HasImage)
      {
        var err = session.LoadImage(opt.ImagePath);
        if (err != null) RhinoApp.WriteLine(err);
      }
      else if (!string.IsNullOrWhiteSpace(opt.ImagePath))
      {
        RhinoApp.WriteLine($"이미지 파일을 찾지 못했어요: {opt.ImagePath}");
        if (mode == RunMode.Scripted) return Result.Failure;
        opt.ImagePath = "";
      }

      if (mode == RunMode.Scripted)
      {
        opt.Save();
        var r = session.ComputeNow();
        return Commit(doc, session, r);
      }

      var conduit = new PerfConduit(session);
      var dlg = new PerfDialog(session);
      bool ok;
      try
      {
        conduit.Enabled = true;
        session.ComputeAsync();
        doc.Views.Redraw();
        ok = dlg.ShowSemiModal(doc, RhinoEtoApp.MainWindow);
      }
      finally
      {
        conduit.Enabled = false;
        doc.Views.Redraw();
      }
      if (!ok) return Result.Cancel;
      var result = session.Busy || session.Result == null ? session.ComputeNow() : session.Result;
      return Commit(doc, session, result);
    }

    // ───────────────────────── picking ─────────────────────────

    static List<PerfTarget> PickTargets(RhinoDoc doc, PerfOptions opt, bool scripted, out Result res)
    {
      res = Result.Cancel;
      var go = new GetObject();
      go.SetCommandPrompt("구멍을 낼 면이나 폴리서피스를 선택하세요 (면 하나만: Ctrl+Shift+클릭)");
      go.GeometryFilter = ObjectType.Surface | ObjectType.PolysrfFilter | ObjectType.Brep | ObjectType.Extrusion;
      go.SubObjectSelect = true;
      go.GroupSelect = true;

      var pick = go.GetMultiple(1, 0);
      if (pick != GetResult.Object) { res = go.CommandResult(); return null; }
      if (scripted && !ScriptOptions(opt)) { res = Result.Cancel; return null; }

      var map = new Dictionary<Guid, PerfTarget>();
      var wholeObjects = new HashSet<Guid>();
      for (int i = 0; i < go.ObjectCount; i++)
      {
        var oref = go.Object(i);
        var obj = oref.Object();
        if (obj == null) continue;
        if (!map.TryGetValue(obj.Id, out var t))
        {
          var brep = obj.Geometry switch
          {
            Brep b => b.DuplicateBrep(),
            Extrusion ex => ex.ToBrep(true),
            Surface s => s.ToBrep(),
            _ => null
          };
          if (brep == null) continue;
          t = new PerfTarget { Id = obj.Id, Brep = brep };
          map[obj.Id] = t;
        }
        var ci = oref.GeometryComponentIndex;
        if (ci.ComponentIndexType == ComponentIndexType.BrepFace && ci.Index >= 0 && ci.Index < t.Brep.Faces.Count)
        {
          if (!t.Faces.Contains(ci.Index)) t.Faces.Add(ci.Index);
        }
        else wholeObjects.Add(obj.Id);
      }
      foreach (var t in map.Values)
      {
        if (!wholeObjects.Contains(t.Id) || t.Faces.Count > 0) continue;
        if (t.Brep.IsSolid && t.Brep.Faces.Count > 1)
        {
          // A closed solid: holes go through it, so pattern only the biggest face (e.g. the front of a plate).
          // Faces of about the same size (top and bottom of a plate): take the one facing the viewer.
          var view = doc.Views.ActiveView?.ActiveViewport;
          var toEye = view != null ? -view.CameraDirection : Vector3d.ZAxis;
          toEye.Unitize();
          var areas = new double[t.Brep.Faces.Count];
          for (int f = 0; f < areas.Length; f++)
            areas[f] = AreaMassProperties.Compute(t.Brep.Faces[f].DuplicateFace(false), true, false, false, false)?.Area ?? 0;
          double maxA = areas.Max();
          int best = -1; double bestScore = double.MinValue;
          for (int f = 0; f < areas.Length; f++)
          {
            if (areas[f] < maxA * 0.97) continue;
            var face = t.Brep.Faces[f];
            var d = face.Domain(0).Mid; var e = face.Domain(1).Mid;
            var n = face.NormalAt(d, e); n.Unitize();
            double score = n * toEye;
            if (score > bestScore) { bestScore = score; best = f; }
          }
          t.Faces.Add(best < 0 ? Array.IndexOf(areas, maxA) : best);
          RhinoApp.WriteLine("닫힌 솔리드라서 가장 넓은 면 하나에만 구멍을 배치해요. 다른 면을 쓰려면 Ctrl+Shift+클릭으로 면을 고르세요.");
        }
        else t.Faces.AddRange(Enumerable.Range(0, t.Brep.Faces.Count));
      }
      var list = map.Values.Where(t => t.Faces.Count > 0).ToList();
      if (list.Count == 0) { res = Result.Nothing; return null; }
      doc.Objects.UnselectAll();
      res = Result.Success;
      return list;
    }

    /// <summary>-ImagePerf: every setting as a command-line option; Enter runs.</summary>
    static bool ScriptOptions(PerfOptions opt)
    {
      // Yes/No as lists, not toggles: a toggle flips on its name alone, so "Invert No" would misfire in scripts.
      string[] yesNo = { "No", "Yes" };
      var spacing = new OptionDouble(opt.Spacing, 0.1, 1000);
      var minS = new OptionDouble(opt.MinSize, 0, 1000);
      var maxS = new OptionDouble(opt.MaxSize, 0.01, 1000);
      var margin = new OptionDouble(opt.Margin, 0, 1000);
      var wall = new OptionDouble(opt.MinWall, 0, 1000);
      var cutoff = new OptionDouble(opt.Cutoff, 0, 0.99);
      var thr = new OptionDouble(opt.Threshold, 0, 1);
      var gamma = new OptionDouble(opt.Gamma, 0.05, 10);
      var ratio = new OptionDouble(opt.SlotRatio, 1, 20);
      var angle = new OptionDouble(opt.Angle, -360, 360);
      var rot = new OptionInteger(opt.Rotate, 0, 3);
      string[] layouts = { "Hex", "Square", "Even" }, shapes = { "Circle", "Hexagon", "Slot" }, tones = { "Halftone", "Threshold" },
               maps = { "Projection", "UV" }, fits = { "Contain", "Cover", "Stretch" }, cuts = { "Auto", "Trim", "Boolean", "Curves" };
      var gp = new GetOption();
      gp.SetCommandPrompt("이미지 타공 옵션 (Enter로 실행)");
      gp.AcceptNothing(true);
      while (true)
      {
        gp.ClearCommandOptions();
        int oImage = gp.AddOption("Image", string.IsNullOrEmpty(opt.ImagePath) ? "None" : "Set");
        int oLayout = gp.AddOptionList("Layout", layouts, (int)opt.Layout);
        gp.AddOptionDouble("Spacing", ref spacing);
        gp.AddOptionDouble("MinSize", ref minS);
        gp.AddOptionDouble("MaxSize", ref maxS);
        gp.AddOptionDouble("Margin", ref margin);
        gp.AddOptionDouble("MinWall", ref wall);
        int oShape = gp.AddOptionList("Shape", shapes, (int)opt.Shape);
        gp.AddOptionDouble("SlotRatio", ref ratio);
        gp.AddOptionDouble("Angle", ref angle);
        int oTone = gp.AddOptionList("Tone", tones, (int)opt.Tone);
        gp.AddOptionDouble("Cutoff", ref cutoff);
        gp.AddOptionDouble("Threshold", ref thr);
        gp.AddOptionDouble("Gamma", ref gamma);
        int oInv = gp.AddOptionList("Invert", yesNo, opt.Invert ? 1 : 0);
        int oMap = gp.AddOptionList("Mapping", maps, (int)opt.Mapping);
        int oFit = gp.AddOptionList("Fit", fits, (int)opt.Fit);
        gp.AddOptionInteger("Rotate", ref rot);
        int oFlip = gp.AddOptionList("FlipX", yesNo, opt.FlipX ? 1 : 0);
        int oCut = gp.AddOptionList("Cut", cuts, (int)opt.Cut);
        int oKeep = gp.AddOptionList("KeepOriginal", yesNo, opt.KeepOriginal ? 1 : 0);
        var r = gp.Get();
        ReadOptions(opt, spacing, minS, maxS, margin, wall, cutoff, thr, gamma, ratio, angle);
        opt.Rotate = ((rot.CurrentValue % 4) + 4) % 4;
        if (r == GetResult.Nothing) return true;
        if (r != GetResult.Option) return false;
        var o = gp.Option();
        if (o == null) continue;
        if (o.Index == oImage)
        {
          var gs = new GetString();
          gs.SetCommandPrompt("이미지 파일 경로 (없애려면 None)");
          if (gs.GetLiteralString() != GetResult.String) continue;
          var p = gs.StringResult().Trim().Trim('"');
          opt.ImagePath = p.Equals("None", StringComparison.OrdinalIgnoreCase) ? "" : p;
        }
        else if (o.Index == oLayout) opt.Layout = (Layout)o.CurrentListOptionIndex;
        else if (o.Index == oShape) opt.Shape = (HoleShape)o.CurrentListOptionIndex;
        else if (o.Index == oTone) opt.Tone = (ToneMode)o.CurrentListOptionIndex;
        else if (o.Index == oMap) opt.Mapping = (Mapping)o.CurrentListOptionIndex;
        else if (o.Index == oFit) opt.Fit = (FitMode)o.CurrentListOptionIndex;
        else if (o.Index == oCut) opt.Cut = (CutMode)o.CurrentListOptionIndex;
        else if (o.Index == oInv) opt.Invert = o.CurrentListOptionIndex == 1;
        else if (o.Index == oFlip) opt.FlipX = o.CurrentListOptionIndex == 1;
        else if (o.Index == oKeep) opt.KeepOriginal = o.CurrentListOptionIndex == 1;
      }
    }

    static void ReadOptions(PerfOptions opt, OptionDouble spacing, OptionDouble minS,
      OptionDouble maxS, OptionDouble margin, OptionDouble wall, OptionDouble cutoff, OptionDouble thr, OptionDouble gamma, OptionDouble ratio, OptionDouble angle)
    {
      opt.Spacing = spacing.CurrentValue; opt.MinSize = minS.CurrentValue; opt.MaxSize = maxS.CurrentValue;
      opt.Margin = margin.CurrentValue; opt.MinWall = wall.CurrentValue; opt.Cutoff = cutoff.CurrentValue;
      opt.Threshold = thr.CurrentValue; opt.Gamma = gamma.CurrentValue; opt.SlotRatio = ratio.CurrentValue; opt.Angle = angle.CurrentValue;
    }

    // ───────────────────────── output ─────────────────────────

    static Result Commit(RhinoDoc doc, PerfSession s, PerfResult r)
    {
      if (r == null || r.Holes.Count == 0)
      {
        RhinoApp.WriteLine("이미지 타공: 구멍이 하나도 없어서 아무것도 바꾸지 않았어요." + (r?.Note != null ? " " + r.Note : ""));
        return Result.Nothing;
      }
      var o = s.Options;
      double tol = doc.ModelAbsoluteTolerance;
      var sw = Stopwatch.StartNew();
      int total = 0, failedAll = 0;
      var notes = new List<string>();
      for (int ti = 0; ti < s.Targets.Count; ti++)
      {
        var t = s.Targets[ti];
        var holes = r.Holes.Where(h => h.Target == ti).ToList();
        if (holes.Count == 0) continue;
        var obj = doc.Objects.FindId(t.Id);
        if (obj == null) continue;
        var mode = PerfEngine.Resolve(o.Cut, t);
        total += holes.Count;

        if (mode == CutMode.Curves)
        {
          var attr = obj.Attributes.Duplicate();
          attr.ObjectId = Guid.Empty;
          var gi = doc.Groups.Add();
          attr.AddToGroup(gi);
          foreach (var c in PerfEngine.SurfaceCurves(t, holes, tol)) doc.Objects.AddCurve(c, attr);
          continue;
        }

        int failed;
        Brep result;
        if (mode == CutMode.Boolean)
        {
          RhinoApp.SetCommandPrompt($"불리언으로 구멍 {holes.Count:N0}개 뚫는 중… (잠시 걸릴 수 있어요)");
          result = PerfEngine.BooleanHoles(t, holes, tol, msg => RhinoApp.SetCommandPrompt(msg), out failed);
        }
        else
        {
          result = PerfEngine.TrimHoles(t, holes, tol, out failed);
          if (result != null && !result.IsValid)
          {
            result.IsValidWithLog(out string log);
            notes.Add("트림 결과가 유효하지 않아요: " + (log ?? "").Split('\n').FirstOrDefault());
          }
        }
        failedAll += failed;
        if (result == null) { notes.Add("구멍을 뚫지 못했어요."); continue; }

        if (o.KeepOriginal)
        {
          var attr = obj.Attributes.Duplicate();
          attr.ObjectId = Guid.Empty;
          doc.Objects.AddBrep(result, attr);
          doc.Objects.Hide(obj, true);
        }
        else doc.Objects.Replace(t.Id, result);
      }
      doc.Views.Redraw();
      string kind = o.Cut == CutMode.Curves ? "구멍 커브" : "구멍";
      RhinoApp.WriteLine($"이미지 타공: {kind} {total - failedAll:N0}개 ({sw.ElapsedMilliseconds} ms)" +
                         (failedAll > 0 ? $", {failedAll}개는 실패" : "") + (notes.Count > 0 ? " — " + string.Join(" ", notes) : ""));
      return Result.Success;
    }
  }
}
