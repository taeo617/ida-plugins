using System;
using System.Collections.Generic;
using System.Linq;
using Eto.Drawing;
using Eto.Forms;
using NPatch.Ui;
using ZebraSettings = Rhino.ApplicationSettings.ZebraAnalysisSettings;

namespace NPatch
{
  /// <summary>
  /// Live settings panel. Every change re-solves in the background and updates the viewport;
  /// edge labels in the viewport can also be clicked to cycle G0 → G1 → G2.
  /// Every setting is saved the moment it changes, so the next run starts the same way.
  /// Zebra direction, stripe size and colour are Rhino's own Zebra settings (shared with the Zebra command).
  /// </summary>
  public class PatchDialog : Dialog<bool>
  {
    const int LabelCol = 78;

    readonly PatchSession _s;
    readonly PatchConduit _conduit;
    readonly UITimer _debounce = new UITimer { Interval = 0.12 };

    // Header
    Label _subtitle, _notes;
    Pill _status;

    // Edges
    Segmented _all;
    readonly List<Segmented> _edgeSegs = new List<Segmented>();
    readonly List<Label> _edgeStats = new List<Label>();
    readonly List<Dot> _edgeDots = new List<Dot>();

    // Shape
    Segmented _degree;
    Label _spanValue, _cvCount;
    Toggle _auto;
    Slide _taut;

    // Display
    Toggle _zebra, _combs, _labels, _cvs, _join;
    Control _zebraBox, _combBox;   // option groups that open when their switch is on
    Segmented _zebraDir;
    Slide _stripe, _combLen;
    Swatch _stripeColor;
    readonly List<Control> _zebraSub = new List<Control>(), _combSub = new List<Control>();
    readonly List<Label> _zebraSubLabels = new List<Label>(), _combSubLabels = new List<Label>();

    public bool JoinToSource => _join.Checked;

    public PatchDialog(PatchSession session, PatchConduit conduit)
    {
      _s = session;
      _conduit = conduit;
      Theme.Init();
      Title = "NPatch";
      BackgroundColor = Theme.Bg;
      Padding = new Padding(16, 14, 16, 14);
      Resizable = false;

      Content = new StackLayout
      {
        Spacing = 12,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Items =
        {
          BuildHeader(),
          new Card(BuildEdges()),
          new Card(BuildShape()),
          new Card(BuildDisplay()),
          BuildFooter(),
        }
      };

      KeyDown += (_, e) =>
      {
        if (e.Key == Keys.Escape) { e.Handled = true; Close(false); }
        else if (e.Key == Keys.Enter) { e.Handled = true; Apply(); }
      };
      _debounce.Elapsed += (_, __) => { _debounce.Stop(); _s.SolveAsync(); };
      _s.Updated += Refresh;
      Closed += (_, __) => { _s.Updated -= Refresh; _debounce.Stop(); SaveSettings(); _conduit.HighlightEdge = -1; };
      UpdateEnabled();
      Refresh();
    }

    // ───────────────────────── layout helpers ─────────────────────────

    static Control HRow(int spacing, params Control[] items)
    {
      var s = new StackLayout { Orientation = Orientation.Horizontal, Spacing = spacing, VerticalContentAlignment = VerticalAlignment.Center };
      foreach (var c in items) s.Items.Add(c);
      return s;
    }

    /// <summary>Left item, flexible gap, right item.</summary>
    static Control Split(Control left, Control right)
    {
      var t = new TableLayout { Spacing = new Size(8, 0) };
      t.Rows.Add(new TableRow(new TableCell(left, true), right));
      return t;
    }

    static Control Caption(string text, string aside = null)
    {
      var l = Theme.L(text, Theme.BodyBold);
      if (aside == null) return l;
      return HRow(8, l, Theme.L(aside, Theme.Small, Theme.Faint));
    }

    static Control Divider() => new Drawable { Height = 1, BackgroundColor = Theme.Border };

    static Control Field(string label, Control control, Label keep = null)
    {
      var l = keep ?? Theme.L(label, Theme.Small, Theme.Sub);
      l.Width = LabelCol;
      var t = new TableLayout { Spacing = new Size(6, 0) };
      t.Rows.Add(new TableRow(l, new TableCell(control, true)));
      return t;
    }

    Control ToggleRow(Toggle toggle, string title, string sub = null)
    {
      var titleL = Theme.L(title, Theme.BodyBold);
      Control left = titleL;
      if (sub != null)
        left = new StackLayout { Spacing = 1, Items = { titleL, Theme.L(sub, Theme.Small, Theme.Sub) } };
      titleL.MouseDown += (_, e) => { toggle.Flip(); e.Handled = true; };
      titleL.Cursor = Cursors.Pointer;
      return Split(left, toggle);
    }

    // ───────────────────────── sections ─────────────────────────

    Control BuildHeader()
    {
      _subtitle = Theme.L("", Theme.Small, Theme.Sub);
      _status = new Pill();
      _notes = Theme.L("", Theme.Small, Theme.Warn, wrap: true);
      _notes.Width = 420;
      _notes.Visible = false;
      var title = new StackLayout { Spacing = 2, Items = { Theme.L("구멍 메우기", Theme.Title), _subtitle } };
      return new StackLayout
      {
        Spacing = 6, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Items = { Split(title, _status), _notes }
      };
    }

    Control BuildEdges()
    {
      _all = new Segmented(132, 26, "G0", "G1", "G2") { SelectedIndex = -1 };
      _all.Changed += i => SetAll((Continuity)i);

      var list = new StackLayout { Spacing = 0, HorizontalContentAlignment = HorizontalAlignment.Stretch };
      for (int i = 0; i < _s.Inputs.Count; i++)
      {
        int idx = i;
        var b = _s.Inputs[i];
        var seg = new Segmented(132, 24, "G0", "G1", "G2") { SelectedIndex = b.AdjacentFace == null ? 0 : (int)b.Continuity };
        if (b.AdjacentFace == null) { seg.Enabled = false; seg.ToolTip = "옆 면이 없는 커브라서 위치(G0)만 맞춰요."; }
        seg.Changed += c => { b.Continuity = (Continuity)c; SyncAll(); Changed(); };
        var dot = new Dot(Theme.Faint);
        var stat = Theme.L("", Theme.Small, Theme.Sub);
        _edgeSegs.Add(seg); _edgeStats.Add(stat); _edgeDots.Add(dot);

        var name = Theme.L($"E{i + 1}", Theme.BodyBold); name.Width = 26;
        var t = new TableLayout { Spacing = new Size(8, 0) };
        t.Rows.Add(new TableRow(new TableCell(HRow(8, dot, name)), seg, new TableCell(stat, true)));
        var row = new HoverRow(t);
        row.HoverChanged += on => { _conduit.HighlightEdge = on ? idx : (_conduit.HighlightEdge == idx ? -1 : _conduit.HighlightEdge); Redraw(); };
        list.Items.Add(row);
      }
      Control edges = list;
      if (_s.Inputs.Count > 6)
        edges = new Scrollable { Content = list, Border = BorderType.None, Height = 6 * 32, BackgroundColor = Theme.Card, ExpandContentWidth = true };
      SyncAll();

      return new StackLayout
      {
        Spacing = 8, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Items =
        {
          Split(Caption("옆 면과 잇는 방식"), HRow(8, Theme.L("모두", Theme.Small, Theme.Sub), _all)),
          Theme.L("G0 붙이기만   ·   G1 꺾임 없이   ·   G2 휘어진 정도까지 똑같이", Theme.Small, Theme.Sub),
          edges,
          HRow(6, new Dot(Theme.Ok), Theme.L("허용오차 안", Theme.Small, Theme.Sub), new Panel { Width = 6 },
                  new Dot(Theme.Bad), Theme.L("초과", Theme.Small, Theme.Sub), new Panel { Width = 6 },
                  Theme.L("뷰포트 라벨을 클릭해도 바뀌어요", Theme.Small, Theme.Faint)),
        }
      };
    }

    Control BuildShape()
    {
      _degree = new Segmented(260, 28, "보통 · 3차", "더 매끈 · 5차") { SelectedIndex = _s.Options.Degree >= 5 ? 1 : 0 };
      _degree.ToolTip = "숫자가 클수록 면이 더 부드럽게 휘어요. G2로 이을 땐 5차가 좋아요.";
      _degree.Changed += i => { _s.Options.Degree = i == 1 ? 5 : 3; Changed(); };

      var minus = new FlatButton("−", 28, 28);
      var plus = new FlatButton("+", 28, 28);
      _spanValue = new Label { Font = Theme.BodyBold, TextColor = Theme.Text, Width = 34, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
      _cvCount = Theme.L("", Theme.Small, Theme.Sub);
      minus.Click += () => SetSpans(_s.Options.SpanCount - 1);
      plus.Click += () => SetSpans(_s.Options.SpanCount + 1);

      _auto = new Toggle { Checked = _s.Options.AutoRefine };
      _auto.Changed += on => { _s.Options.AutoRefine = on; Changed(); };
      var autoL = Theme.L("경계가 안 맞으면 칸 수 자동으로 늘리기", Theme.Small, Theme.Text);
      autoL.MouseDown += (_, e) => { _auto.Flip(); e.Handled = true; };
      autoL.Cursor = Cursors.Pointer;

      _taut = new Slide(0, 100, 200) { Value = (int)Math.Round(_s.Options.Tautness * 100) };
      _taut.ToolTip = "왼쪽은 풍선처럼 자연스럽게 부풀고, 오른쪽은 천을 당긴 것처럼 평평해져요.";
      _taut.Changed += v => { _s.Options.Tautness = v / 100.0; Changed(); };

      return new StackLayout
      {
        Spacing = 10, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Items =
        {
          Caption("새 면의 모양"),
          Field("부드러움", HRow(0, _degree)),
          Field("칸 수", HRow(10, HRow(4, minus, _spanValue, plus), _cvCount)),
          Field("", HRow(8, _auto, autoL)),
          Field("모양", HRow(6, Theme.L("볼록", Theme.Small, Theme.Sub), _taut, Theme.L("팽팽", Theme.Small, Theme.Sub))),
        }
      };
    }

    Control BuildDisplay()
    {
      // Zebra: Rhino's own settings (same as the Zebra command's options).
      _zebra = new Toggle { Checked = _conduit.ShowZebra };
      _zebra.Changed += on => { _conduit.ShowZebra = on; UpdateEnabled(); DisplayChanged(); };
      _zebraDir = new Segmented(140, 26, "가로", "세로") { SelectedIndex = ZebraSettings.VerticalStripes ? 1 : 0 };
      _zebraDir.Changed += i => { ZebraSettings.VerticalStripes = i == 1; DisplayChanged(); };
      _stripe = new Slide(0, 6, 170) { Value = ZebraSettings.StripeThickness };
      _stripe.Changed += v => { ZebraSettings.StripeThickness = v; DisplayChanged(); };
      _stripeColor = new Swatch(Theme.FromSys(ZebraSettings.StripeColor)) { ToolTip = "줄 색 바꾸기" };
      _stripeColor.Click += PickStripeColor;

      var lDir = Theme.L("방향", Theme.Small, Theme.Sub);
      var lThick = Theme.L("줄 굵기", Theme.Small, Theme.Sub);
      var lColor = Theme.L("줄 색", Theme.Small, Theme.Sub);
      var thick = Theme.L("굵게", Theme.Small, Theme.Sub); var thin = Theme.L("얇게", Theme.Small, Theme.Sub);
      _zebraSub.AddRange(new Control[] { _zebraDir, _stripe, _stripeColor });
      _zebraSubLabels.AddRange(new[] { lDir, lThick, lColor, thick, thin });

      // Combs
      _combs = new Toggle { Checked = _conduit.ShowCombs };
      _combs.Changed += on => { _conduit.ShowCombs = on; UpdateEnabled(); DisplayChanged(); };
      _combLen = new Slide(10, 400, 170) { Value = (int)Math.Round(_conduit.CombScale * 100) };
      _combLen.Changed += v => { _conduit.CombScale = v / 100.0; _conduit.Invalidate(); DisplayChanged(); };
      var lLen = Theme.L("길이", Theme.Small, Theme.Sub);
      var shortL = Theme.L("짧게", Theme.Small, Theme.Sub); var longL = Theme.L("길게", Theme.Small, Theme.Sub);
      var newFace = Theme.L("새 면", Theme.Small, Theme.Sub); var nbrFace = Theme.L("옆 면", Theme.Small, Theme.Sub);
      var lColors = Theme.L("색", Theme.Small, Theme.Sub);
      var legend = HRow(6, new Dot(Theme.FromSys(System.Drawing.Color.FromArgb(230, 60, 160)), 9), newFace,
                           new Panel { Width = 8 }, new Dot(Theme.FromSys(System.Drawing.Color.FromArgb(40, 170, 220)), 9), nbrFace,
                           new Panel { Width = 4 }, Theme.L("두 빗살이 겹칠수록 잘 맞아요", Theme.Small, Theme.Faint));
      _combSub.Add(_combLen);
      _combSubLabels.AddRange(new[] { lLen, shortL, longL, newFace, nbrFace, lColors });

      _labels = new Toggle { Checked = _conduit.ShowLabels };
      _labels.Changed += on => { _conduit.ShowLabels = on; DisplayChanged(); };
      _cvs = new Toggle { Checked = _conduit.ShowControlPoints };
      _cvs.Changed += on => { _conduit.ShowControlPoints = on; DisplayChanged(); };

      Control Indent(Control c) => new Panel { Padding = new Padding(14, 0, 0, 0), Content = c };
      _zebraBox = Indent(new StackLayout
      {
        Spacing = 6, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Items =
        {
          Field(null, HRow(0, _zebraDir), lDir),
          Field(null, HRow(6, thick, _stripe, thin), lThick),
          Field(null, HRow(8, _stripeColor, Theme.L("라이노 Zebra 명령과 같은 설정이에요", Theme.Small, Theme.Faint)), lColor),
        }
      });
      _combBox = Indent(new StackLayout
      {
        Spacing = 6, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Items =
        {
          Field(null, legend, lColors),
          Field(null, HRow(6, shortL, _combLen, longL), lLen),
          Theme.L("길이는 화면에서 보기 좋게 맞추는 값이에요. 결과 면에는 영향이 없어요.", Theme.Small, Theme.Faint),
        }
      });

      return new StackLayout
      {
        Spacing = 10, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Items =
        {
          Caption("확인용 표시", "화면에만 보이고 결과 면은 그대로예요"),
          ToggleRow(_zebra, "제브라", "줄무늬가 이음매에서 끊기지 않고 이어지면 매끈하게 붙은 거예요"),
          _zebraBox,
          Divider(),
          ToggleRow(_combs, "곡률 콤", "경계에서 휘어진 정도를 빗살 길이로 보여줘요"),
          _combBox,
          Divider(),
          ToggleRow(_labels, "엣지 라벨", "뷰포트에서 라벨을 클릭하면 G0 → G1 → G2"),
          ToggleRow(_cvs, "조절점 보기"),
        }
      };
    }

    Control BuildFooter()
    {
      // Merging needs every edge to belong to a surface (curves picked as boundary have none).
      int nObj = Output.SourceIds(_s.Inputs).Count;
      bool canJoin = nObj > 0 && _s.Inputs.All(b => b.SourceObjectId != Guid.Empty);
      _join = new Toggle { Checked = canJoin && PatchSettings.Join, Enabled = canJoin };
      _join.Changed += _ => SaveSettings();
      var joinL = Theme.L(nObj > 1 ? $"옆 면 {nObj}개와 하나로 합치기" : "원래 면과 하나로 합치기", Theme.Body, _join.Enabled ? Theme.Text : Theme.Faint);
      if (nObj > 1) joinL.ToolTip = _join.ToolTip = $"새 면과 옆 면 {nObj}개를 폴리서피스 하나로 합쳐요.";
      if (!_join.Enabled) joinL.ToolTip = _join.ToolTip = "경계를 커브로 골랐으면 합칠 면이 없어서 새 면만 따로 추가돼요.";
      else { joinL.Cursor = Cursors.Pointer; joinL.MouseDown += (_, e) => { _join.Flip(); e.Handled = true; }; }

      var cancel = new FlatButton("취소", 76, 34);
      var ok = new FlatButton("적용", 96, 34, primary: true);
      cancel.Click += () => Close(false);
      ok.Click += Apply;
      return Split(HRow(8, _join, joinL), HRow(8, cancel, ok));
    }

    // ───────────────────────── behaviour ─────────────────────────

    void Apply()
    {
      if (_s.Busy || _s.Result?.Brep == null) return;
      Close(true);
    }

    void PickStripeColor()
    {
      var c = ZebraSettings.StripeColor;
      if (!Rhino.UI.Dialogs.ShowColorDialog(ref c)) return;
      ZebraSettings.StripeColor = c;
      _stripeColor.Color = Theme.FromSys(c);
      DisplayChanged();
    }

    void SetSpans(int n)
    {
      n = Math.Max(3, Math.Min(24, n));
      if (n == _s.Options.SpanCount) return;
      _s.Options.SpanCount = n;
      Changed();
      Refresh();
    }

    void UpdateEnabled()
    {
      bool z = _zebra.Checked, c = _combs.Checked;
      bool changed = _zebraBox.Visible != z || _combBox.Visible != c;
      _zebraBox.Visible = z;
      _combBox.Visible = c;
      if (changed && Loaded) FitToContent();
    }

    /// <summary>Grow/shrink the window when an option group opens or closes.</summary>
    void FitToContent()
    {
      try
      {
        var w = ControlObject;   // WPF Window on Windows
        var p = w.GetType().GetProperty("SizeToContent");
        if (p != null) { p.SetValue(w, Enum.Parse(p.PropertyType, "Manual")); p.SetValue(w, Enum.Parse(p.PropertyType, "WidthAndHeight")); }
      }
      catch { }
    }

    /// <summary>"All" segment shows the shared level, or nothing when edges differ.</summary>
    void SyncAll()
    {
      var lv = _s.Inputs.Where(b => b.AdjacentFace != null).Select(b => (int)b.Continuity).Distinct().ToList();
      _all.SelectedIndex = lv.Count == 1 ? lv[0] : -1;
    }

    void SetAll(Continuity c)
    {
      for (int i = 0; i < _s.Inputs.Count; i++)
      {
        if (_s.Inputs[i].AdjacentFace == null) continue;
        _s.Inputs[i].Continuity = c;
        _edgeSegs[i].SelectedIndex = (int)c;
      }
      PatchSettings.DefaultContinuity = (int)c;
      Changed();
    }

    /// <summary>Called from the viewport when an edge label is clicked.</summary>
    public void CycleEdge(int i)
    {
      var b = _s.Inputs[i];
      if (b.AdjacentFace == null) return;
      b.Continuity = (Continuity)(((int)b.Continuity + 1) % 3);
      _edgeSegs[i].SelectedIndex = (int)b.Continuity;
      SyncAll();
      Changed();
    }

    void Changed()
    {
      SaveSettings();
      _debounce.Stop();
      _debounce.Start();
      _status.Set("계산 중…", Theme.Sub);
    }

    void DisplayChanged()
    {
      SaveSettings();
      Redraw();
    }

    void Redraw() => Rhino.RhinoDoc.ActiveDoc?.Views.Redraw();

    void Refresh()
    {
      var r = _s.Result;
      int deg = _s.Options.Degree >= 5 ? 5 : 3;
      _spanValue.Text = _s.Options.SpanCount.ToString();
      int usedSpans = r?.SpanCount ?? _s.Options.SpanCount;
      int nCv = usedSpans + (r?.Degree ?? deg);
      _cvCount.Text = r != null && r.SpanCount != _s.Options.SpanCount
        ? $"자동으로 {r.SpanCount}칸 사용 · 조절점 {nCv}×{nCv}"
        : $"조절점 {nCv}×{nCv}";
      string kind = r?.Brep == null ? "" : !r.Untrimmed ? "   ·   트림한 면" : r.Surfaces.Count > 1 ? $"   ·   트림 없는 4변 조각 {r.Surfaces.Count}개" : "   ·   트림 없는 4변 면";
      _subtitle.Text = $"엣지 {_s.Inputs.Count}개   ·   {deg}차   ·   {usedSpans}칸" + kind + (r?.Brep != null ? $"   ·   {r.Milliseconds} ms" : "");
      _subtitle.ToolTip = r?.Untrimmed == true && r.Surfaces.Count > 1
        ? $"구멍을 4변 조각 {r.Surfaces.Count}개로 나눠 각각 트림 없는 면으로 만들고 하나로 붙였어요. 조각끼리 매끄럽게 이어져요(이음 각도 최대 {r.InnerSeamAngleDeg:F2}°, 구멍 모서리 근처 {r.InnerSeamCornerAngleDeg:F2}°)."
        : r?.Untrimmed == true
        ? "구멍이 4변이라 경계를 그대로 테두리로 쓰는 면을 만들었어요. 아이소커브가 경계를 따라가고 옆 면과 딱 맞게 이어져요."
        : "구멍 모양에 맞춰 큰 면을 만든 뒤 경계로 잘라냈어요. 트림 없는 면으로 만들 수 있으면 자동으로 그쪽을 써요.";

      string notes = "";
      if (_s.Busy) _status.Set("계산 중…", Theme.Sub);
      else if (r == null || r.Brep == null)
      {
        _status.Set("면을 만들 수 없어요", Theme.Bad);
        notes = r?.Message ?? "";
      }
      else
      {
        int bad = r.Edges.Count(e => !e.Ok);
        _status.Set(bad == 0 ? "✓  모든 엣지 OK" : $"⚠  {bad}개 엣지 허용오차 초과", bad == 0 ? Theme.Ok : Theme.Warn);
        notes = Explain(r);
      }
      _notes.Text = notes;
      _notes.Visible = notes.Length > 0;

      for (int i = 0; i < _edgeStats.Count; i++)
      {
        var e = r?.Edges.ElementAtOrDefault(i);
        if (e == null) { _edgeStats[i].Text = ""; _edgeDots[i].Color = Theme.Faint; continue; }
        _edgeStats[i].Text = StatText(e);
        _edgeStats[i].TextColor = e.Ok ? Theme.Sub : Theme.Bad;
        _edgeDots[i].Color = e.Ok ? Theme.Ok : Theme.Bad;
      }
      _conduit.Invalidate();
      Redraw();
    }

    static string StatText(EdgeReport e)
    {
      var parts = new List<string>();
      if (e.Effective == Continuity.G2)
        parts.Add($"곡률 {e.CurvaturePct:0.#}%" + (e.CornerCurvaturePct > e.CurvaturePct * 1.5 && e.CornerCurvaturePct > 1 ? $" (코너 {e.CornerCurvaturePct:0}%)" : ""));
      if (e.Effective >= Continuity.G1)
        parts.Add($"{e.AngleDeg:0.###}°" + (e.CornerAngleDeg > e.AngleDeg * 1.5 && e.CornerAngleDeg > 0.005 ? $" (코너 {e.CornerAngleDeg:0.##}°)" : ""));
      parts.Add($"틈 {Num(Math.Max(e.Gap, e.CornerGap))}");
      if (e.Requested != e.Effective) parts.Add("옆 면 없음");
      return string.Join("  ·  ", parts);
    }

    static string Num(double v) => v == 0 ? "0" : v < 1e-3 ? v.ToString("0.0e0") : v.ToString("0.####");

    static string Explain(FillResult r)
    {
      var notes = new List<string>();
      var g2Corners = r.Edges.Where(e => e.Effective == Continuity.G2 && e.CornerCurvaturePct > Math.Max(5, 1.5 * e.CurvaturePct)).ToList();
      if (g2Corners.Count > 0)
        notes.Add($"코너에서 옆 면끼리 휘어진 정도가 달라요 ({string.Join(", ", g2Corners.Select(e => $"E{e.Index + 1} {e.CornerCurvaturePct:0}%"))}). " +
                  "코너에서는 G2를 정확히 맞출 수 없어서, 코너 값에서 엣지 값으로 자연스럽게 이어지게 했어요.");
      if (r.Edges.Any(e => !e.Ok && Math.Max(e.Gap, e.CornerGap) > 0) && r.SpanCount >= NSidedFill.MaxSpans)
        notes.Add("칸 수를 최대로 늘려도 틈이 남아요. 경계에 날카롭게 꺾인 곳이 있는지 확인해 보세요.");
      if (!string.IsNullOrEmpty(r.Message)) notes.Add(r.Message);
      return string.Join("\n", notes);
    }

    void SaveSettings()
    {
      PatchSettings.Degree = _s.Options.Degree;
      PatchSettings.Spans = _s.Options.SpanCount;
      PatchSettings.AutoRefine = _s.Options.AutoRefine;
      PatchSettings.Tautness = _s.Options.Tautness;
      PatchSettings.Zebra = _conduit.ShowZebra;
      PatchSettings.Combs = _conduit.ShowCombs;
      PatchSettings.Labels = _conduit.ShowLabels;
      PatchSettings.ControlPoints = _conduit.ShowControlPoints;
      PatchSettings.CombScale = _conduit.CombScale;
      if (_join.Enabled) PatchSettings.Join = _join.Checked;
    }
  }

  /// <summary>Remembered dialog settings (stored in the plug-in's settings file).
  /// Zebra direction/size/colour are not here: they live in Rhino's own Zebra settings.</summary>
  static class PatchSettings
  {
    static Rhino.PersistentSettings S => NPatchPlugin.Instance.Settings;
    public static int Degree { get => S.GetInteger("Degree", 3); set => S.SetInteger("Degree", value); }
    public static int Spans { get => S.GetInteger("Spans", 10); set => S.SetInteger("Spans", value); }
    public static bool AutoRefine { get => S.GetBool("AutoRefine", true); set => S.SetBool("AutoRefine", value); }
    public static double Tautness { get => S.GetDouble("Tautness", 0); set => S.SetDouble("Tautness", value); }
    public static bool Zebra { get => S.GetBool("Zebra", false); set => S.SetBool("Zebra", value); }
    public static bool Combs { get => S.GetBool("Combs", true); set => S.SetBool("Combs", value); }
    public static bool Labels { get => S.GetBool("Labels", true); set => S.SetBool("Labels", value); }
    public static bool ControlPoints { get => S.GetBool("ControlPoints", false); set => S.SetBool("ControlPoints", value); }
    public static double CombScale { get => S.GetDouble("CombScale", 1.0); set => S.SetDouble("CombScale", value); }
    public static bool Join { get => S.GetBool("Join", true); set => S.SetBool("Join", value); }
    public static int DefaultContinuity { get => S.GetInteger("Continuity", 2); set => S.SetInteger("Continuity", value); }
  }
}
