using System;
using System.Linq;
using Eto.Drawing;
using Eto.Forms;
using IDA.A4Draw.Ui;

namespace IDA.A4Draw
{
    // A4Drawing 설정 창 (NPatch와 같은 모양): 뷰 칩 · 한 장에/뷰마다 · 3D 각도 · 축척 · 선 · 내보내기
    class DrawDialog : Dialog<bool>
    {
        const int LabelCol = 70;
        readonly DrawOpts _o;
        readonly Chip[] _views;
        Segmented _angle, _sheets;
        Toggle _hidden, _tangent, _export, _ai, _pdf;
        TextBox _name, _folder;
        DropDown _scale;
        FlatButton _browse;
        Label _summary;

        public static bool Show(DrawOpts o, int count = 0)
        {
            var d = new DrawDialog(o, count);
            return d.ShowModal(Rhino.UI.RhinoEtoApp.MainWindow);
        }

        DrawDialog(DrawOpts o, int count)
        {
            _o = o;
            Theme.Init();
            Title = "A4 도면";
            BackgroundColor = Theme.Bg;
            Padding = new Padding(16, 14, 16, 14);
            Resizable = false;
            Width = 820;

            string[] names = { "탑뷰", "정면", "우측면", "바닥면", "배면", "좌측면", "3D 뷰" };
            _views = names.Select((t, i) => new Chip(t, 92) { Checked = o.Views[i] }).ToArray();
            foreach (var c in _views) c.Changed += _ => Update();

            Content = new StackLayout
            {
                Spacing = 12,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    Header(count),
                    Columns(new Card(Views()), new StackLayout { Spacing = 12, HorizontalContentAlignment = HorizontalAlignment.Stretch, Items = { new Card(Sheet()), new Card(Files()) } }),
                    Footer(),
                }
            };
            KeyDown += (_, e) =>
            {
                if (e.Key == Keys.Escape) { e.Handled = true; Close(false); }
                else if (e.Key == Keys.Enter && !(_name.HasFocus || _folder.HasFocus)) { e.Handled = true; Apply(); }
            };
            Update();
        }

        // ───────── 모양 도우미 (NPatch와 같은 규칙) ─────────
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
        static Control Columns(Control left, Control right)
        {
            var t = new TableLayout { Spacing = new Size(12, 0) };
            t.Rows.Add(new TableRow(new TableCell(left, true), new TableCell(right, true)));
            t.Rows.Add(new TableRow { ScaleHeight = false });
            return t;
        }
        static Control Field(string label, Control control)
        {
            var l = Theme.L(label, Theme.Small, Theme.Sub); l.Width = LabelCol;
            var t = new TableLayout { Spacing = new Size(6, 0) };
            t.Rows.Add(new TableRow(l, new TableCell(control, true)));
            return t;
        }
        static Control ToggleRow(Toggle toggle, string title, string sub = null)
        {
            var titleL = Theme.L(title, Theme.BodyBold);
            Control left = titleL;
            if (sub != null) left = new StackLayout { Spacing = 1, Items = { titleL, Theme.L(sub, Theme.Small, Theme.Sub) } };
            titleL.MouseDown += (_, e) => { toggle.Flip(); e.Handled = true; };
            titleL.Cursor = Cursors.Pointer;
            return Split(left, toggle);
        }
        static TextBox Box(string text) => new TextBox { Text = text ?? "", Width = 120, BackgroundColor = Theme.Card, TextColor = Theme.Text, Font = Theme.Body };

        // ───────── 구역 ─────────
        Control Header(int count)
        {
            _summary = Theme.L("", Theme.Small, Theme.Sub);
            var title = new StackLayout { Spacing = 2, Items = { Theme.L("A4 도면 만들기", Theme.Title), _summary } };
            _name = Box(_o.Name);
            return new StackLayout
            {
                Spacing = 8, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items = { title, Field("제품명", _name) }
            };
        }

        Control Views()
        {
            void Pick(params int[] on) { for (int i = 0; i < _views.Length; i++) _views[i].Checked = on.Contains(i); Update(); }
            var bTop = new FlatButton("탑뷰만", 74, 26); bTop.Click += () => Pick(0);
            var bThree = new FlatButton("삼면도", 74, 26); bThree.Click += () => Pick(0, 1, 2);
            var bThree3D = new FlatButton("삼면도 + 3D", 96, 26); bThree3D.Click += () => Pick(0, 1, 2, A4DrawingCommand.View3D);

            _angle = new Segmented(240, 26, "등각 (아이소)", "Perspective 뷰 각도") { SelectedIndex = _o.Angle };
            _angle.ToolTip = "등각: 왼쪽 앞 위에서 본 표준 3D 그림. Perspective 뷰 각도: 지금 Perspective 뷰에서 보이는 방향 그대로";

            var grid = new TableLayout { Spacing = new Size(6, 6) };
            grid.Rows.Add(new TableRow(_views[0], _views[1], _views[2], new TableCell(null, true)));
            grid.Rows.Add(new TableRow(_views[3], _views[4], _views[5], null));
            grid.Rows.Add(new TableRow(_views[6], null, null, null));

            return new StackLayout
            {
                Spacing = 8, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    Split(Theme.L("뷰", Theme.BodyBold), HRow(6, bTop, bThree, bThree3D)),
                    Theme.L("눌러서 켜고 끄기 · 여러 개 골라도 돼요", Theme.Small, Theme.Sub),
                    grid,
                    Field("3D 각도", _angle),
                    Theme.L("3D 뷰는 치수 없이 외형선만, 길이가 일그러지지 않는 평행 투영이에요", Theme.Small, Theme.Faint),
                }
            };
        }

        Control Sheet()
        {
            _sheets = new Segmented(240, 26, "한 장에 모두", "뷰마다 한 장씩") { SelectedIndex = _o.OneSheet ? 0 : 1 };
            _sheets.Changed += _ => Update();
            _scale = new DropDown { Width = 150 };
            foreach (var n in new[] { "자동 (A4에 맞게)", "5:1", "2:1", "1:1", "1:2", "1:5", "1:10", "1:20", "1:50" }) _scale.Items.Add(n);
            _scale.SelectedIndex = _o.ScaleIx;
            _hidden = new Toggle { Checked = _o.Hidden };
            _tangent = new Toggle { Checked = _o.Tangent };
            return new StackLayout
            {
                Spacing = 8, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    Theme.L("장 · 축척 · 선", Theme.BodyBold),
                    Field("장", _sheets),
                    Theme.L("한 장에 모두: 3각법 배치, 모든 뷰 같은 축척", Theme.Small, Theme.Faint),
                    Field("축척", _scale),
                    ToggleRow(_hidden, "숨은선", "안 보이는 모서리를 점선으로"),
                    ToggleRow(_tangent, "탄젠트선", "필렛처럼 매끈하게 이어지는 경계"),
                }
            };
        }

        Control Files()
        {
            _export = new Toggle { Checked = _o.Export };
            _ai = new Toggle { Checked = _o.Ai };
            _pdf = new Toggle { Checked = _o.Pdf };
            _export.Changed += _ => Update();
            _folder = Box(_o.Folder);
            _browse = new FlatButton("찾기", 56, 26);
            _browse.Click += () => { var p = Export.PickFolder(_folder.Text); if (p != null) _folder.Text = p; };
            var folderRow = new TableLayout { Spacing = new Size(6, 0) };
            folderRow.Rows.Add(new TableRow(new TableCell(_folder, true), _browse));
            return new StackLayout
            {
                Spacing = 8, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    ToggleRow(_export, "파일로 저장", "SVG는 항상 · 아래에서 AI·PDF 추가"),
                    ToggleRow(_ai, "AI (일러스트)", "레이어·선 두께·글자 그대로, 고칠 수 있게"),
                    ToggleRow(_pdf, "PDF (바로 인쇄)", "실제 크기(100%)로 인쇄하면 1:1"),
                    Field("폴더", folderRow),
                }
            };
        }

        Control Footer()
        {
            var cancel = new FlatButton("취소", 76, 34);
            var ok = new FlatButton("도면 만들기", 110, 34, primary: true);
            cancel.Click += () => Close(false);
            ok.Click += Apply;
            return Split(new Panel(), HRow(8, cancel, ok));
        }

        void Update()
        {
            int n = _views.Count(c => c.Checked);
            bool one = _sheets == null || _sheets.SelectedIndex == 0;
            _summary.Text = n == 0 ? "뷰를 하나 이상 골라 주세요" : one || n == 1 ? $"뷰 {n}개 → A4 1장" : $"뷰 {n}개 → A4 {n}장";
            _summary.TextColor = n == 0 ? Theme.Warn : Theme.Sub;
            if (_angle != null) _angle.Enabled = _views[A4DrawingCommand.View3D].Checked;
            if (_export != null) { bool on = _export.Checked; _ai.Enabled = _pdf.Enabled = _folder.Enabled = _browse.Enabled = on; }
        }

        void Apply()
        {
            if (!_views.Any(c => c.Checked)) { Update(); return; }
            for (int i = 0; i < _views.Length; i++) _o.Views[i] = _views[i].Checked;
            _o.Name = string.IsNullOrWhiteSpace(_name.Text) ? _o.Name : _name.Text.Trim();
            _o.Angle = Math.Max(0, _angle.SelectedIndex);
            _o.OneSheet = _sheets.SelectedIndex != 1;
            _o.ScaleIx = Math.Max(0, _scale.SelectedIndex);
            _o.Hidden = _hidden.Checked; _o.Tangent = _tangent.Checked;
            _o.Export = _export.Checked; _o.Ai = _ai.Checked; _o.Pdf = _pdf.Checked;
            _o.Folder = _folder.Text.Trim().Trim('"');
            Close(true);
        }
    }
}
