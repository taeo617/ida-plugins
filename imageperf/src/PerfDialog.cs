using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Eto.Drawing;
using Eto.Forms;
using ImagePerf.Ui;

namespace ImagePerf
{
  /// <summary>
  /// Live panel. Every change recomputes the hole layout in the background and updates the viewport.
  /// The picture can be opened with a file dialog or dragged onto the thumbnail.
  /// </summary>
  public class PerfDialog : Dialog<bool>
  {
    const int LabelCol = 70;
    readonly PerfSession _s;
    readonly UITimer _debounce = new UITimer { Interval = 0.15 };
    PerfOptions O => _s.Options;

    Label _subtitle, _notes, _imgName;
    Pill _status;
    Drawable _thumb;
    Bitmap _thumbBmp;
    Segmented _tone, _layout, _shape, _mapping, _fit, _cut;
    Toggle _invert, _flip, _keep;
    Control _cutoffRow, _thresholdRow, _slotRow;
    FlatButton _apply;

    public PerfDialog(PerfSession s)
    {
      _s = s;
      Theme.Init();
      Title = "이미지 타공";
      BackgroundColor = Theme.Bg;
      Padding = new Padding(16, 14, 16, 14);
      Resizable = false;

      var left = new StackLayout
      {
        Spacing = 12, Width = 360, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Items = { new Card(BuildImage()), new Card(BuildLayout()) }
      };
      var right = new StackLayout
      {
        Spacing = 12, Width = 330, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Items = { new Card(BuildShape()), new Card(BuildMapping()), new Card(BuildCut()) }
      };
      var cols = new TableLayout { Spacing = new Size(12, 0) };
      cols.Rows.Add(new TableRow(new TableCell(left), new TableCell(right)));

      Content = new StackLayout
      {
        Spacing = 12, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Items = { BuildHeader(), cols, BuildFooter() }
      };

      KeyDown += (_, e) =>
      {
        if (e.Key == Keys.Escape) { e.Handled = true; Close(false); }
      };
      _debounce.Elapsed += (_, __) => { _debounce.Stop(); _s.ComputeAsync(); };
      _s.Updated += Refresh;
      Closed += (_, __) => { _s.Updated -= Refresh; _debounce.Stop(); O.Save(); _thumbBmp?.Dispose(); };
      UpdateVisibility();
      LoadThumb();
      Refresh();
    }

    // ───────────────────────── helpers ─────────────────────────

    static Control HRow(int spacing, params Control[] items)
    {
      var s = new StackLayout { Orientation = Orientation.Horizontal, Spacing = spacing, VerticalContentAlignment = VerticalAlignment.Center };
      foreach (var c in items) s.Items.Add(c);
      return s;
    }

    static Control Split(Control left, Control right)
    {
      var t = new TableLayout { Spacing = new Size(8, 0) };
      t.Rows.Add(new TableRow(new TableCell(left, true), right));
      return t;
    }

    static Control Field(string label, Control control)
    {
      var l = Theme.L(label, Theme.Small, Theme.Sub);
      l.Width = LabelCol;
      var t = new TableLayout { Spacing = new Size(6, 0) };
      t.Rows.Add(new TableRow(l, new TableCell(control, true)));
      return t;
    }

    static Control Caption(string text, string aside = null)
    {
      var l = Theme.L(text, Theme.BodyBold);
      if (aside == null) return l;
      return HRow(8, l, Theme.L(aside, Theme.Small, Theme.Faint));
    }

    /// <summary>Slider + number box for a value in model units (or a plain number).</summary>
    Control Number(string label, double min, double max, double step, Func<double> get, Action<double> set, string unit = "mm", int sliderWidth = 150)
    {
      var slide = new Slide((int)Math.Round(min / step), (int)Math.Round(max / step), sliderWidth);
      var box = new TextBox { Width = 52, Font = Theme.Small, TextAlignment = TextAlignment.Right };
      bool sync = false;
      Action show = () =>
      {
        sync = true;
        double v = get();
        slide.Value = (int)Math.Round(v / step);
        box.Text = v.ToString(step < 0.1 ? "0.00" : step < 1 ? "0.0" : "0", CultureInfo.InvariantCulture);
        sync = false;
      };
      slide.Changed += i => { if (sync) return; set(i * step); show(); Changed(); };
      Action commit = () =>
      {
        if (sync) return;
        if (double.TryParse(box.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
        {
          set(Math.Max(0, v)); show(); Changed();
        }
        else show();
      };
      box.KeyDown += (_, e) => { if (e.Key == Keys.Enter) { e.Handled = true; commit(); } };
      box.LostFocus += (_, __) => commit();
      show();
      return Field(label, HRow(6, slide, box, Theme.L(unit, Theme.Small, Theme.Faint)));
    }

    Control ToggleRow(Toggle toggle, string title)
    {
      var l = Theme.L(title, Theme.Small, Theme.Text);
      l.MouseDown += (_, e) => { toggle.Flip(); e.Handled = true; };
      l.Cursor = Cursors.Pointer;
      return HRow(8, toggle, l);
    }

    // ───────────────────────── sections ─────────────────────────

    Control BuildHeader()
    {
      _subtitle = Theme.L("", Theme.Small, Theme.Sub);
      _status = new Pill();
      _notes = Theme.L("", Theme.Small, Theme.Warn, wrap: true);
      _notes.Width = 700;
      _notes.Visible = false;
      var title = new StackLayout { Spacing = 2, Items = { Theme.L("이미지 타공", Theme.Title), _subtitle } };
      return new StackLayout
      {
        Spacing = 6, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Items = { Split(title, _status), _notes }
      };
    }

    Control BuildImage()
    {
      _thumb = new Drawable { Size = new Size(120, 90), AllowDrop = true, Cursor = Cursors.Pointer, ToolTip = "클릭해서 이미지를 고르거나, 파일을 여기로 끌어다 놓으세요." };
      _thumb.Paint += (_, e) =>
      {
        var g = e.Graphics;
        var r = new RectangleF(0, 0, _thumb.Width - 1, _thumb.Height - 1);
        g.FillRectangle(Theme.SegBg, r);
        if (_thumbBmp != null)
        {
          float k = Math.Min(r.Width / _thumbBmp.Width, r.Height / _thumbBmp.Height);
          float w = _thumbBmp.Width * k, h = _thumbBmp.Height * k;
          g.DrawImage(_thumbBmp, (r.Width - w) / 2, (r.Height - h) / 2, w, h);
        }
        else
        {
          var f = Theme.Small;
          var txt = "이미지를\n끌어다 놓기";
          var sz = g.MeasureString(f, txt);
          g.DrawText(f, Theme.Sub, (r.Width - sz.Width) / 2, (r.Height - sz.Height) / 2, txt);
        }
        g.DrawRectangle(Theme.Border, r);
      };
      _thumb.MouseDown += (_, e) => { e.Handled = true; PickImage(); };
      _thumb.DragEnter += (_, e) => { if (e.Data.ContainsUris) e.Effects = DragEffects.Copy; };
      _thumb.DragOver += (_, e) => { if (e.Data.ContainsUris) e.Effects = DragEffects.Copy; };
      _thumb.DragDrop += (_, e) =>
      {
        var uri = e.Data.Uris?.FirstOrDefault();
        if (uri != null && uri.IsFile) SetImage(uri.LocalPath);
      };
      // Drops anywhere on the panel count too.
      AllowDrop = true;
      DragEnter += (_, e) => { if (e.Data.ContainsUris) e.Effects = DragEffects.Copy; };
      DragOver += (_, e) => { if (e.Data.ContainsUris) e.Effects = DragEffects.Copy; };
      DragDrop += (_, e) =>
      {
        var uri = e.Data.Uris?.FirstOrDefault();
        if (uri != null && uri.IsFile) SetImage(uri.LocalPath);
      };

      var open = new FlatButton("이미지 열기…", 110, 28);
      open.Click += PickImage;
      var clear = new FlatButton("빼기", 50, 28);
      clear.Click += () => { _s.ClearImage(); LoadThumb(); Changed(); };
      _imgName = Theme.L("", Theme.Small, Theme.Sub, wrap: true);
      _imgName.Width = 200;

      _tone = new Segmented(200, 26, "하프톤 (크기)", "있음 · 없음") { SelectedIndex = (int)O.Tone };
      _tone.ToolTip = "하프톤: 어두울수록 큰 구멍. 있음·없음: 기준보다 어두운 곳에만 같은 크기 구멍.";
      _tone.Changed += i => { O.Tone = (ToneMode)i; UpdateVisibility(); Changed(); };
      _invert = new Toggle { Checked = O.Invert };
      _invert.Changed += on => { O.Invert = on; Changed(); };

      _cutoffRow = Number("시작 밝기", 0, 0.95, 0.01, () => O.Cutoff, v => O.Cutoff = Math.Min(0.95, v), "", 150);
      _thresholdRow = Number("기준", 0.02, 0.98, 0.01, () => O.Threshold, v => O.Threshold = Math.Min(0.98, v), "", 150);

      return new StackLayout
      {
        Spacing = 9, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Items =
        {
          Caption("이미지", "PNG · JPG"),
          HRow(12, _thumb, new StackLayout { Spacing = 6, Items = { HRow(6, open, clear), _imgName } }),
          Field("밝기 → 구멍", HRow(0, _tone)),
          _cutoffRow,
          _thresholdRow,
          Number("대비", 0.3, 3, 0.05, () => O.Gamma, v => O.Gamma = Math.Max(0.3, v), "", 150),
          Field("", ToggleRow(_invert, "반전 (밝은 곳에 구멍)")),
        }
      };
    }

    Control BuildLayout()
    {
      _layout = new Segmented(240, 26, "육각 엇갈림", "격자", "균일 랜덤") { SelectedIndex = (int)O.Layout };
      _layout.ToolTip = "육각·격자는 면의 UV 방향을 따라 길이 기준으로 줄을 세우고, 균일 랜덤은 곡면 위에 고르게 흩뿌려요.";
      _layout.Changed += i => { O.Layout = (Layout)i; Changed(); };
      return new StackLayout
      {
        Spacing = 9, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Items =
        {
          Caption("배치"),
          Field("방식", HRow(0, _layout)),
          Number("간격", 0.5, 40, 0.1, () => O.Spacing, v => O.Spacing = Math.Max(0.5, v)),
          Number("최소 크기", 0, 30, 0.1, () => O.MinSize, v => O.MinSize = v),
          Number("최대 크기", 0.1, 30, 0.1, () => O.MaxSize, v => O.MaxSize = Math.Max(0.1, v)),
          Number("가장자리", 0, 30, 0.1, () => O.Margin, v => O.Margin = v),
          Number("최소 살", 0, 5, 0.05, () => O.MinWall, v => O.MinWall = v),
        }
      };
    }

    Control BuildShape()
    {
      _shape = new Segmented(200, 26, "원", "육각", "슬롯") { SelectedIndex = (int)O.Shape };
      _shape.Changed += i => { O.Shape = (HoleShape)i; UpdateVisibility(); Changed(); };
      _slotRow = Number("슬롯 길이", 1, 8, 0.1, () => O.SlotRatio, v => O.SlotRatio = Math.Max(1, v), "× 폭", 120);
      return new StackLayout
      {
        Spacing = 9, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Items =
        {
          Caption("구멍 모양"),
          Field("모양", HRow(0, _shape)),
          _slotRow,
          Number("회전", 0, 180, 1, () => O.Angle, v => O.Angle = Math.Min(180, v), "°", 120),
        }
      };
    }

    Control BuildMapping()
    {
      _mapping = new Segmented(200, 26, "평면 투영", "UV 펼침") { SelectedIndex = (int)O.Mapping };
      _mapping.ToolTip = "평면 투영: 면을 앞에서 본 모습 그대로 이미지가 비쳐요. UV 펼침: 면의 UV를 따라 이미지를 펴서 감싸요(면마다 따로).";
      _mapping.Changed += i => { O.Mapping = (Mapping)i; Changed(); };
      _fit = new Segmented(200, 26, "맞춤", "채움", "늘림") { SelectedIndex = (int)O.Fit };
      _fit.Changed += i => { O.Fit = (FitMode)i; Changed(); };
      var rot = new FlatButton("90° 돌리기", 90, 26);
      rot.Click += () => { O.Rotate = (O.Rotate + 1) % 4; Changed(); };
      _flip = new Toggle { Checked = O.FlipX };
      _flip.Changed += on => { O.FlipX = on; Changed(); };
      return new StackLayout
      {
        Spacing = 9, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Items =
        {
          Caption("이미지 씌우기"),
          Field("방식", HRow(0, _mapping)),
          Field("비율", HRow(0, _fit)),
          Field("", HRow(12, rot, ToggleRow(_flip, "좌우 뒤집기"))),
        }
      };
    }

    Control BuildCut()
    {
      _cut = new Segmented(222, 26, "자동", "트림", "불리언", "커브만") { SelectedIndex = (int)O.Cut };
      _cut.ToolTip = "자동: 닫힌 솔리드는 불리언(두께 관통), 열린 면은 트림. 커브만: 면 위에 구멍 커브만 그려요(레이저용).";
      _cut.Changed += i => { O.Cut = (CutMode)i; Refresh(); };
      _keep = new Toggle { Checked = O.KeepOriginal };
      _keep.Changed += on => O.KeepOriginal = on;
      return new StackLayout
      {
        Spacing = 9, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Items =
        {
          Caption("뚫는 방법"),
          Field("방법", HRow(0, _cut)),
          Field("", ToggleRow(_keep, "원본 남기기 (숨김)")),
        }
      };
    }

    Control BuildFooter()
    {
      var cancel = new FlatButton("취소", 90, 32);
      cancel.Click += () => Close(false);
      _apply = new FlatButton("구멍 뚫기", 120, 32, primary: true);
      _apply.Click += () => { O.Save(); Close(true); };
      var hint = Theme.L("값을 바꾸면 뷰포트 미리보기가 바로 바뀌어요.", Theme.Small, Theme.Faint);
      return Split(hint, HRow(8, cancel, _apply));
    }

    // ───────────────────────── behaviour ─────────────────────────

    void PickImage()
    {
      var dlg = new OpenFileDialog { Title = "타공에 쓸 이미지", MultiSelect = false };
      dlg.Filters.Add(new FileFilter("이미지", ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"));
      if (File.Exists(O.ImagePath)) dlg.Directory = new Uri(Path.GetDirectoryName(O.ImagePath) + Path.DirectorySeparatorChar);
      if (dlg.ShowDialog(this) == DialogResult.Ok) SetImage(dlg.FileName);
    }

    void SetImage(string path)
    {
      var err = _s.LoadImage(path);
      if (err != null) { MessageBox.Show(this, err, "이미지 타공", MessageBoxType.Warning); return; }
      LoadThumb();
      Changed();
    }

    void LoadThumb()
    {
      _thumbBmp?.Dispose();
      _thumbBmp = null;
      if (_s.Image != null && File.Exists(O.ImagePath))
      {
        try { _thumbBmp = new Bitmap(O.ImagePath); } catch { _thumbBmp = null; }
        _imgName.Text = Path.GetFileName(O.ImagePath) + $"\n{_s.Image.W}×{_s.Image.H} 으로 읽음";
      }
      else _imgName.Text = "이미지가 없으면 모든 구멍이 최대 크기로 같은 패턴이 돼요.";
      _thumb.Invalidate();
    }

    void UpdateVisibility()
    {
      _cutoffRow.Visible = O.Tone == ToneMode.Halftone;
      _thresholdRow.Visible = O.Tone == ToneMode.Threshold;
      _slotRow.Visible = O.Shape == HoleShape.Slot;
    }

    void Changed()
    {
      _status.Set("계산 중…", Theme.Faint);
      _debounce.Stop();
      _debounce.Start();
    }

    void Refresh()
    {
      var r = _s.Result;
      int faces = _s.Targets.Sum(t => t.Faces.Count);
      string kinds = string.Join(", ", _s.Targets.Select(t => PerfEngine.Resolve(O.Cut, t) switch
      {
        CutMode.Boolean => "불리언",
        CutMode.Curves => "커브만",
        _ => "트림"
      }).Distinct());
      _subtitle.Text = $"{_s.Targets.Count}개 객체 · {faces}개 면 · {kinds}";

      if (_s.Error != null) _status.Set("오류", Theme.Bad);
      else if (_s.Busy || r == null) _status.Set("계산 중…", Theme.Faint);
      else _status.Set($"구멍 {r.Holes.Count:N0}개 · {r.Ms} ms", r.Holes.Count > 0 ? Theme.Ok : Theme.Warn);

      var notes = new System.Collections.Generic.List<string>();
      if (_s.Error != null) notes.Add(_s.Error);
      if (r?.Note != null) notes.Add(r.Note);
      if (O.MaxSize > O.MaxPossible + 1e-9)
        notes.Add($"최대 크기가 간격 − 최소 살({O.MaxPossible:0.##} mm)보다 커서 그 크기로 줄였어요.");
      if (r != null && r.RemovedOverlap > 0)
        notes.Add($"겹치는 구멍 {r.RemovedOverlap:N0}개를 뺐어요 (최소 살 {O.MinWall:0.##} mm).");
      if (r != null && r.Holes.Count == 0 && _s.Error == null)
        notes.Add("구멍이 하나도 없어요. 간격·가장자리 여백을 줄이거나 시작 밝기를 낮춰 보세요.");
      if (r != null && r.Holes.Count > 4000 && _s.Targets.Any(t => PerfEngine.Resolve(O.Cut, t) == CutMode.Boolean))
        notes.Add("구멍이 많아서 불리언이 오래 걸릴 수 있어요 (트림이 훨씬 빨라요).");
      _notes.Text = string.Join("\n", notes);
      _notes.Visible = notes.Count > 0;
      _apply.Enabled = r != null && r.Holes.Count > 0;
      _apply.Invalidate();
      Rhino.RhinoDoc.ActiveDoc?.Views.Redraw();
    }
  }
}
