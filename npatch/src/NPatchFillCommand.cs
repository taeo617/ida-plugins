using Rhino;
using Rhino.Commands;
using Rhino.Input.Custom;

namespace NPatch
{
  /// <summary>
  /// NPatchFill: the same fill without the panel — command-line options only, for scripts/macros
  /// (e.g. "_NPatchFill _Continuity=G2 _Enter").
  /// </summary>
  public class NPatchFillCommand : Command
  {
    static int s_cont = 2;
    static int s_spans = 10;
    static bool s_join = true;

    public override string EnglishName => "NPatchFill";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      // Options first (so a macro can set them), then the boundary.
      var gopt = new GetOption();
      gopt.SetCommandPrompt("옵션을 고르거나 Enter로 계속");
      gopt.AcceptNothing(true);
      var spans = new OptionInteger(s_spans, 3, NSidedFill.MaxSpans);
      var join = new OptionToggle(s_join, "No", "Yes");
      string[] conts = { "G0", "G1", "G2" };
      while (true)
      {
        gopt.ClearCommandOptions();
        int iCont = gopt.AddOptionList("Continuity", conts, s_cont);
        gopt.AddOptionInteger("Spans", ref spans);
        gopt.AddOptionToggle("Join", ref join);
        var r = gopt.Get();
        if (r == Rhino.Input.GetResult.Option)
        {
          if (gopt.OptionIndex() == iCont) s_cont = gopt.Option().CurrentListOptionIndex;
          continue;
        }
        if (r == Rhino.Input.GetResult.Nothing) break;
        return gopt.CommandResult();
      }
      s_spans = spans.CurrentValue; s_join = join.CurrentValue;

      if (!BoundaryPicker.Pick(doc, "구멍 둘레의 엣지나 커브를 선택하세요. 끝나면 Enter",
            out var inputs, out var sourceId, out var sourceBrep, out var res))
        return res;
      foreach (var b in inputs) b.Continuity = (Continuity)s_cont;

      var result = NSidedFill.Solve(inputs, new FillOptions { SpanCount = s_spans }, doc.ModelAbsoluteTolerance);
      if (result.Brep == null) { RhinoApp.WriteLine("NPatchFill 실패: " + result.Message); return Result.Failure; }
      Output.Commit(doc, result, s_join ? Output.SourceIds(inputs) : null, doc.ModelAbsoluteTolerance);
      RhinoApp.WriteLine(Output.Summary(result));
      return Result.Success;
    }
  }
}
