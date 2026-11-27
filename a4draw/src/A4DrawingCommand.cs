using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace IDA.A4Draw
{
    // A4 도면 설정 (창·명령줄 공용)
    public class DrawOpts
    {
        public bool[] Views = new bool[A4DrawingCommand.ViewCount];   // 탑뷰·정면·우측면·바닥면·배면·좌측면·3D
        public bool OneSheet = true;      // true = 고른 뷰를 한 장에, false = 뷰마다 한 장씩
        public int Angle;                 // 3D 뷰: 0 = 등각, 1 = 지금 Perspective 뷰 각도
        public int ScaleIx;
        public bool Hidden, Tangent = true, Export = true, Ai = true, Pdf = true;
        public string Name, Folder;

        public static DrawOpts Load(Rhino.PersistentSettings st)
        {
            var o = new DrawOpts
            {
                OneSheet = st.GetBool("onesheet", true),
                Angle = st.GetInteger("angle", 0),
                ScaleIx = Math.Max(0, Array.IndexOf(A4DrawingCommand.ScaleNames, st.GetString("scale", "Auto"))),
                Hidden = st.GetBool("hidden", false),
                Tangent = st.GetBool("tangent", true),
                Export = st.GetBool("export", true),
                Ai = st.GetBool("ai", true),
                Pdf = st.GetBool("pdf", true),
            };
            var vs = st.GetString("views", "");
            if (vs.Length == A4DrawingCommand.ViewCount) for (int i = 0; i < vs.Length; i++) o.Views[i] = vs[i] == '1';
            else o.Views[Math.Max(0, Array.IndexOf(A4DrawingCommand.ViewNames, st.GetString("view", "Top")))] = true;
            if (!o.Views.Any(v => v)) o.Views[0] = true;
            return o;
        }

        public void Save(Rhino.PersistentSettings st)
        {
            st.SetString("views", new string(Views.Select(v => v ? '1' : '0').ToArray()));
            st.SetBool("onesheet", OneSheet); st.SetInteger("angle", Angle);
            st.SetString("scale", A4DrawingCommand.ScaleNames[ScaleIx]);
            st.SetBool("hidden", Hidden); st.SetBool("tangent", Tangent);
            st.SetBool("export", Export); st.SetBool("ai", Ai); st.SetBool("pdf", Pdf);
        }
    }

    // A4Drawing: 선택한 객체를 고른 뷰(탑뷰·정면·우측면 … 3D)로 Make2D → 전체·주요 치수 → A4 도곽·표제란에
    // 실제 축척(1:1, 1:2 …)으로 배치 → SVG·AI·PDF 내보내기. 원본 객체는 건드리지 않음.
    // A4Drawing = 설정 창, -A4Drawing = 명령줄 옵션(에이전트·매크로용)
    public class A4DrawingCommand : Command
    {
        public override string EnglishName => "A4Drawing" + Ver.Sfx;

        public const int ViewCount = 7, View3D = 6;
        public static readonly string[] ViewNames = { "Top", "Front", "Right", "Bottom", "Back", "Left", "3D" };
        public static readonly string[] ViewKo = { "탑뷰", "정면", "우측면", "바닥면", "배면", "좌측면", "3D" };
        public static readonly string[] ScaleNames = { "Auto", "5to1", "2to1", "1to1", "1to2", "1to5", "1to10", "1to20", "1to50" };
        public static readonly double[] ScaleValues = { 0, 5, 2, 1, 0.5, 0.2, 0.1, 0.05, 0.02 };
        static readonly string[] LayoutNames = { "Single", "Three", "Three3D", "Custom" };

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var st = A4DrawPlugIn.Instance.Settings;
            var opt = DrawOpts.Load(st);
            List<RhinoObject> objs;

            if (mode == RunMode.Interactive)
            {
                // 설정 창: 객체를 먼저 고르고 창에서 뷰·장·축척·내보내기를 정함
                var go = new GetObject();
                go.SetCommandPrompt("A4 도면으로 만들 객체를 고르세요 (고른 뒤 Enter → 설정 창)");
                go.GeometryFilter = ObjectType.Brep | ObjectType.Extrusion | ObjectType.Mesh | ObjectType.SubD;
                go.SubObjectSelect = false;
                go.GroupSelect = true;
                go.EnablePreSelect(true, true);
                if (go.GetMultiple(1, 0) != GetResult.Object) return go.CommandResult();
                objs = Enumerable.Range(0, go.ObjectCount).Select(i => go.Object(i).Object()).Where(o => o != null).ToList();
                if (objs.Count == 0) return Result.Nothing;
                opt.Name = DefaultName(doc, objs);
                opt.Folder = Export.DefaultFolder(doc, st);
                if (!DrawDialog.Show(opt)) return Result.Cancel;
            }
            else
            {
                int view = Array.FindIndex(opt.Views, v => v);
                int layout = 0, sheets = opt.OneSheet ? 0 : 1;
                var optHidden = new OptionToggle(opt.Hidden, "Off", "On");
                var optTangent = new OptionToggle(opt.Tangent, "Off", "On");
                var optExport = new OptionToggle(opt.Export, "Off", "On");
                var optAi = new OptionToggle(opt.Ai, "Off", "On");
                var optPdf = new OptionToggle(opt.Pdf, "Off", "On");
                string name = null, outDir = null;

                var go = new GetObject();
                go.SetCommandPrompt("A4 도면으로 만들 객체를 고르세요");
                go.GeometryFilter = ObjectType.Brep | ObjectType.Extrusion | ObjectType.Mesh | ObjectType.SubD;
                go.SubObjectSelect = false;
                go.GroupSelect = true;
                go.EnablePreSelect(true, true);
                bool havePre = false;
                while (true)
                {
                    go.ClearCommandOptions();
                    int layoutOpt = go.AddOptionList("Layout", LayoutNames.Take(3), layout);
                    int viewOpt = go.AddOptionList("View", ViewNames, view);
                    int sheetOpt = go.AddOptionList("Sheets", new[] { "One", "PerView" }, sheets);
                    int angleOpt = go.AddOptionList("Angle", new[] { "Iso", "Perspective" }, opt.Angle);
                    int scaleOpt = go.AddOptionList("Scale", ScaleNames, opt.ScaleIx);
                    go.AddOptionToggle("Hidden", ref optHidden);
                    go.AddOptionToggle("Tangent", ref optTangent);
                    go.AddOptionToggle("Export", ref optExport);
                    go.AddOptionToggle("AI", ref optAi);
                    go.AddOptionToggle("PDF", ref optPdf);
                    int nameOpt = go.AddOption("Name");
                    int folderOpt = go.AddOption("Folder");
                    var r = go.GetMultiple(1, 0);
                    if (r == GetResult.Option)
                    {
                        go.EnablePreSelect(false, true);
                        int oi = go.OptionIndex();
                        if (oi == layoutOpt) layout = go.Option().CurrentListOptionIndex;
                        else if (oi == viewOpt) { view = go.Option().CurrentListOptionIndex; layout = 0; }
                        else if (oi == sheetOpt) sheets = go.Option().CurrentListOptionIndex;
                        else if (oi == angleOpt) opt.Angle = go.Option().CurrentListOptionIndex;
                        else if (oi == scaleOpt) opt.ScaleIx = go.Option().CurrentListOptionIndex;
                        else if (oi == nameOpt) name = AskString("제품명 (도면 표제란·파일 이름)", name ?? DefaultName(doc)) ?? name;
                        else if (oi == folderOpt)
                        {
                            var picked = AskString("저장 폴더", Export.DefaultFolder(doc, st));
                            if (picked != null) { outDir = picked.Trim().Trim('"'); RhinoApp.WriteLine("저장 폴더: " + outDir); }
                        }
                        continue;
                    }
                    if (r == GetResult.Nothing && havePre) break;
                    if (r != GetResult.Object) return go.CommandResult();
                    if (go.ObjectsWerePreselected && !havePre)
                    {
                        havePre = true;
                        go.EnablePreSelect(false, true);
                        go.AcceptNothing(true);
                        go.EnableClearObjectsOnEntry(false);
                        go.DeselectAllBeforePostSelect = false;
                        go.EnableUnselectObjectsOnExit(false);
                        go.SetCommandPrompt("옵션을 바꾸거나 Enter");
                        continue;
                    }
                    break;
                }
                objs = Enumerable.Range(0, go.ObjectCount).Select(i => go.Object(i).Object()).Where(o => o != null).ToList();
                if (objs.Count == 0) return Result.Nothing;
                opt.Views = new bool[ViewCount];
                if (layout == 0) opt.Views[Math.Max(0, view)] = true;
                else { opt.Views[0] = opt.Views[1] = opt.Views[2] = true; opt.Views[View3D] = layout == 2; }
                opt.OneSheet = sheets == 0;
                opt.Hidden = optHidden.CurrentValue; opt.Tangent = optTangent.CurrentValue;
                opt.Export = optExport.CurrentValue; opt.Ai = optAi.CurrentValue; opt.Pdf = optPdf.CurrentValue;
                opt.Name = string.IsNullOrWhiteSpace(name) ? DefaultName(doc, objs) : name;
                opt.Folder = outDir ?? Export.DefaultFolder(doc, st);
            }
            opt.Save(st);
            return Build(doc, objs, opt, st) ? Result.Success : Result.Failure;
        }

        // ---------- 만들기 ----------
        class ViewData { public int View; public List<Line2D> Lines; public BoundingBox BB; public double W, H; public int Col, Row; }

        static bool Build(RhinoDoc doc, List<RhinoObject> objs, DrawOpts opt, Rhino.PersistentSettings st)
        {
            double u = RhinoMath.UnitScale(UnitSystem.Millimeters, doc.ModelUnitSystem); // 1mm = 문서 단위로 몇
            var srcBox = BoundingBox.Empty;
            foreach (var o in objs) srcBox.Union(o.Geometry.GetBoundingBox(true));

            var views = Enumerable.Range(0, ViewCount).Where(i => opt.Views[i]).ToList();
            if (views.Count == 0) views.Add(0);
            RhinoApp.WriteLine($"A4 도면: Make2D 계산 중… ({string.Join("·", views.Select(v => ViewKo[v]))})");
            var data = new List<ViewData>();
            foreach (var v in views)
            {
                var (dir, up) = ViewDir(doc, v, opt.Angle);
                var lines = Make2D(objs, srcBox, dir, up, opt.Hidden && v != View3D, opt.Tangent, doc.ModelAbsoluteTolerance);
                if (!lines.Any(l => l.Layer == "외형선")) { RhinoApp.WriteLine($"{ViewKo[v]}: 보이는 선이 없어 건너뛰어요."); continue; }
                var bb = BoundingBox.Empty;
                foreach (var l in lines) bb.Union(l.Curve.GetBoundingBox(true));
                data.Add(new ViewData { View = v, Lines = lines, BB = bb, W = (bb.Max.X - bb.Min.X) / u, H = (bb.Max.Y - bb.Min.Y) / u });
            }
            if (data.Count == 0) { RhinoApp.WriteLine("보이는 선이 없어요. 다른 뷰로 해 보세요."); return false; }

            var sheetsList = opt.OneSheet ? new List<List<ViewData>> { data } : data.Select(d => new List<ViewData> { d }).ToList();
            string folder = null;
            if (opt.Export)
            {
                folder = string.IsNullOrWhiteSpace(opt.Folder) ? Export.DefaultFolder(doc, st) : opt.Folder.Trim().Trim('"');
                st.SetString("folder", folder);
            }
            for (int i = 0; i < sheetsList.Count; i++)
            {
                var pageId = Sheet(doc, sheetsList[i], opt, srcBox, u, i + 1, sheetsList.Count, sheetsList.Count > 1 || data.Count > 1);
                if (pageId == Guid.Empty) continue;
                if (folder != null) Export.Run(doc, pageId.ToString(), folder, opt.Ai, opt.Pdf);
            }
            doc.Views.Redraw();
            return true;
        }

        static string Title(List<ViewData> vs)
        {
            var set = vs.Select(v => v.View).OrderBy(v => v).ToList();
            if (set.SequenceEqual(new[] { 0, 1, 2 })) return "삼면도";
            if (set.SequenceEqual(new[] { 0, 1, 2, View3D })) return "삼면도 + 3D";
            return string.Join("·", set.Select(v => ViewKo[v]));
        }

        // 3각법 자리 (열, 행): 정면 가운데, 탑뷰 위, 바닥면 아래, 좌측면 왼쪽, 우측면 오른쪽, 배면 그 오른쪽, 3D 오른쪽 위
        static readonly (int C, int R)[] Cell = { (1, 0), (1, 1), (2, 1), (1, 2), (3, 1), (0, 1), (2, 0) };

        static Guid Sheet(RhinoDoc doc, List<ViewData> vs, DrawOpts opt, BoundingBox srcBox, double u, int sheetNo, int sheetCount, bool multi)
        {
            const double M = 10;                 // 도곽 여백
            const double TB_H = 18;              // 하단 띠 표제란 높이
            const double GapX = 30, GapY = 36;   // 뷰 사이 (치수·뷰 이름 자리)
            bool single = vs.Count == 1;
            int maxCircles = single ? 5 : 4;

            // 격자: 쓰는 열·행만 남겨서 촘촘히
            var cols = vs.Select(v => Cell[v.View].C).Distinct().OrderBy(c => c).ToList();
            var rows = vs.Select(v => Cell[v.View].R).Distinct().OrderBy(r => r).ToList();
            foreach (var v in vs) { v.Col = cols.IndexOf(Cell[v.View].C); v.Row = rows.IndexOf(Cell[v.View].R); }
            var colW = cols.Select((_, c) => vs.Where(v => v.Col == c).Max(v => v.W)).ToArray();
            var rowH = rows.Select((_, r) => vs.Where(v => v.Row == r).Max(v => v.H)).ToArray();
            double sumW = colW.Sum(), sumH = rowH.Sum();

            // 용지 방향·축척: 더 크게 들어가는 쪽 (같으면 세로로 긴 그림만 세로)
            double fixedS = ScaleValues[opt.ScaleIx];
            (double s, double x0, double x1, double y0, double y1) Fit(double pw, double ph)
            {
                double ax0 = M + 12, ax1 = pw - M - 24, ay0 = M + TB_H + 28, ay1 = ph - M - 12;
                double aw = ax1 - ax0 - GapX * (cols.Count - 1), ah = ay1 - ay0 - GapY * (rows.Count - 1);
                return (fixedS > 0 ? fixedS : Page.FitScale(sumW, sumH, aw, ah), ax0, ax1, ay0, ay1);
            }
            var land = Fit(297, 210); var port = Fit(210, 297);
            bool portrait = port.s > land.s || (Math.Abs(port.s - land.s) < 1e-9 && sumH > sumW * 1.15);
            double PW = portrait ? 210 : 297, PH = portrait ? 297 : 210;
            var (s, areaX0, areaX1, areaY0, areaY1) = portrait ? port : land;
            double totalW = s * sumW + GapX * (cols.Count - 1), totalH = s * sumH + GapY * (rows.Count - 1);
            if (totalW > areaX1 - areaX0 + 1e-6 || totalH > areaY1 - areaY0 + 1e-6)
                RhinoApp.WriteLine($"주의: 축척 {Page.ScaleText(s)}로는 A4 그림 영역보다 커요. 축척 Auto나 '뷰마다 한 장씩'을 권해요.");

            // 놓을 자리: 원본 오른쪽(이미 도면이 있으면 그 오른쪽)
            double ox = Math.Max(srcBox.Max.X + 100 * u, Page.RightmostPage(doc) + 40 * u);
            var O = new Point3d(ox, srcBox.Min.Y, 0);
            Point3d P(double x, double y) => new Point3d(O.X + x * u, O.Y + y * u, 0);
            var pageId = Guid.NewGuid();
            var added = new List<Guid>();
            Guid Add(GeometryBase g, string layer, ObjectAttributes a = null)
            {
                a = a ?? Page.Attrs(doc, layer, pageId);
                Guid id = Guid.Empty;
                switch (g)
                {
                    case Curve c: id = doc.Objects.AddCurve(c, a); break;
                    case LinearDimension ld: id = doc.Objects.AddLinearDimension(ld, a); break;
                    case RadialDimension rd: id = doc.Objects.AddRadialDimension(rd, a); break;
                    case TextEntity te: id = doc.Objects.AddText(te, a); break;
                    case Hatch ht: id = doc.Objects.AddHatch(ht, a); break;
                }
                if (id != Guid.Empty) added.Add(id);
                return id;
            }

            string title = single ? ViewKo[vs[0].View] : Title(vs);
            string fileBase = !multi ? opt.Name : $"{opt.Name}_{title.Replace(" + ", "+")}";

            // 용지(A4 크기 그대로) + 도곽
            var paper = new Rectangle3d(new Plane(P(0, 0), Vector3d.XAxis, Vector3d.YAxis), PW * u, PH * u).ToNurbsCurve();
            var pa = Page.Attrs(doc, "용지", pageId);
            pa.SetUserString(Page.KeyFrame, $"{O.X:R},{O.Y:R},{PW * u:R},{PH * u:R}");
            pa.SetUserString(Page.KeyName, fileBase);
            pa.Name = "A4 용지 - " + fileBase;
            Add(paper, "용지", pa);
            Add(new Rectangle3d(new Plane(P(M, M), Vector3d.XAxis, Vector3d.YAxis), (PW - 2 * M) * u, (PH - 2 * M) * u).ToNurbsCurve(), "도곽");

            var ds = Page.DimStyle(doc, s, u);
            void Text(string t, double x, double y, double hMmTxt, TextJustification j, string layer = "글자")
            {
                var te = TextEntity.Create(t, new Plane(P(x, y), Vector3d.XAxis, Vector3d.YAxis), ds, false, 0, 0);
                if (te == null) return;
                te.TextHeight = hMmTxt * u;
                te.Justification = j;
                te.DimensionStyleId = ds.Id;
                Add(te, layer);
            }

            // 뷰마다: 칸 가운데에 놓고 치수·뷰 이름
            double cx0 = (areaX0 + areaX1) / 2 - totalW / 2, top = (areaY0 + areaY1) / 2 + totalH / 2;
            int nDims = 0;
            foreach (var v in vs)
            {
                double cellX = cx0 + Enumerable.Range(0, v.Col).Sum(c => s * colW[c] + GapX) + s * colW[v.Col] / 2;
                double cellY = top - Enumerable.Range(0, v.Row).Sum(r => s * rowH[r] + GapY) - s * rowH[v.Row] / 2;
                var dc = v.BB.Center;
                var xf = Transform.Translation(P(cellX, cellY) - Point3d.Origin) * Transform.Scale(Point3d.Origin, s) * Transform.Translation(Point3d.Origin - new Point3d(dc.X, dc.Y, 0));
                var placed = new List<(Curve Curve, string Layer)>();
                foreach (var grp in v.Lines.GroupBy(l => l.Layer))
                {
                    var crvs = grp.Select(l => { var c = l.Curve.DuplicateCurve(); c.Transform(xf); return c; }).ToArray();
                    var joined = Curve.JoinCurves(crvs, doc.ModelAbsoluteTolerance * 2);
                    foreach (var c in joined.Length > 0 ? joined : crvs) { Add(c, grp.Key); placed.Add((c, grp.Key)); }
                }
                var gbb = BoundingBox.Empty;
                foreach (var p in placed) if (p.Layer != "숨은선") gbb.Union(p.Curve.GetBoundingBox(true));
                if (v.View != View3D) nDims += Dims(doc, placed, gbb, ds, s, u, v.W, v.H, maxCircles, g => Add(g, "치수"));
                string label = v.View == View3D ? (opt.Angle == 0 ? "3D (등각)" : "3D") : single ? $"{ViewKo[v.View]}  {Page.ScaleText(s)}" : ViewKo[v.View];
                Text(label, (gbb.Center.X - O.X) / u, (gbb.Min.Y - O.Y) / u - (v.View == View3D ? 8 : 24), 3, TextJustification.BottomCenter);
            }

            // 하단 띠 표제란 (found/Founded): 도곽 안 아래쪽 전체 폭 검은 띠 + 흰 로고 + 칸마다 이름(회색)·값(흰색)
            double bx0 = M, bx1 = PW - M, by0 = M, by1 = M + TB_H;
            int solid = Page.SolidPattern(doc);
            var band = new Rectangle3d(new Plane(P(bx0, by0), Vector3d.XAxis, Vector3d.YAxis), (bx1 - bx0) * u, TB_H * u).ToNurbsCurve();
            var bandHatch = Hatch.Create(band, solid, 0, 1, doc.ModelAbsoluteTolerance);
            if (bandHatch != null && bandHatch.Length > 0)
            {
                var ba = Page.Attrs(doc, "표제란", pageId); ba.DisplayOrder = -1;
                Add(bandHatch[0], "표제란", ba);
            }
            double logoH = portrait ? 3.6 : 4.4;                  // 로고 글자 높이(mm)
            double lx = bx0 + 6, ly = by0 + (TB_H - logoH) / 2;
            foreach (var letter in Page.LogoCurves(P(lx, ly).X, P(lx, ly).Y, logoH * u))
            {
                var hs = Hatch.Create(letter, solid, 0, 1, doc.ModelAbsoluteTolerance * 0.1);
                if (hs == null) continue;
                foreach (var h in hs) Add(h, "로고");
            }
            // 칸: 도면명 · 축척 · 날짜 · 도면번호 · 시트 (가로 비율로 나눔)
            double fx0 = lx + Logo.Width * logoH + 8;
            string[] labels = { "도면명", "축척 · 단위", "날짜", "도면번호", "시트" };
            string[] values = { $"{opt.Name} · {title}", $"{Page.ScaleText(s)} · mm", DateTime.Now.ToString("yyyy.MM.dd"), "FD-" + DateTime.Now.ToString("yyMMdd"), $"{sheetNo} / {sheetCount}" };
            double[] wts = { 2.2, 1.1, 1.2, 1.1, 0.7 };
            double span = bx1 - 4 - fx0, sum = wts.Sum(), fx = fx0;
            double valH = portrait ? 2.8 : 3.2, labH = 1.8;
            for (int f = 0; f < labels.Length; f++)
            {
                Add(new LineCurve(P(fx, by0 + 3.5), P(fx, by1 - 3.5)), "표제란 글자");
                Text(labels[f], fx + 2.5, by1 - 4.2 - labH, labH, TextJustification.BottomLeft, "표제란 보조");
                Text(values[f], fx + 2.5, by0 + 4, valH, TextJustification.BottomLeft, "표제란 글자");
                fx += span * wts[f] / sum;
            }

            if (added.Count > 0)
            {
                int gi = doc.Groups.Add("A4 도면 - " + fileBase, added);
                if (gi < 0) RhinoApp.WriteLine("그룹을 만들지 못했어요 (도면은 그대로 있어요)");
            }
            var size = string.Join(" · ", vs.Where(v => v.View != View3D).Select(v => $"{ViewKo[v.View]} {Math.Round(v.W)}×{Math.Round(v.H)}"));
            RhinoApp.WriteLine($"A4 도면 {sheetNo}/{sheetCount}: {opt.Name} · {title} · {(portrait ? "세로" : "가로")} A4 · 축척 {Page.ScaleText(s)} · 실제(mm) {size} · 치수 {nDims}개 · 레이어 '{Page.Root}'");
            return pageId;
        }

        // 한 뷰의 치수: 전체 가로·세로 + 원·원호 지름 (용지 위 길이 × 1/축척 = 실제 mm)
        static int Dims(RhinoDoc doc, List<(Curve Curve, string Layer)> placed, BoundingBox gbb, DimensionStyle ds, double s, double u, double wMm, double hMm, int maxCircles, Action<GeometryBase> add)
        {
            int n = 0;
            var a1 = new Point3d(gbb.Min.X, gbb.Min.Y, 0); var a2 = new Point3d(gbb.Max.X, gbb.Min.Y, 0);
            var w = LinearDimension.Create(AnnotationType.Aligned, ds, Plane.WorldXY, Vector3d.XAxis, a1, a2, new Point3d(gbb.Center.X, gbb.Min.Y - 10 * u, 0), 0);
            if (w != null) { w.DimensionStyleId = ds.Id; add(w); n++; }
            var b1 = new Point3d(gbb.Max.X, gbb.Min.Y, 0); var b2 = new Point3d(gbb.Max.X, gbb.Max.Y, 0);
            // 세로 치수: X축이 위(+Y)인 평면에서 재야 길이가 나옴 (WorldXY에서는 0mm)
            var vpl = new Plane(b1, Vector3d.YAxis, -Vector3d.XAxis);
            var h = LinearDimension.Create(AnnotationType.Aligned, ds, vpl, vpl.XAxis, b1, b2, new Point3d(gbb.Max.X + 10 * u, gbb.Center.Y, 0), 0);
            if (h != null) { h.DimensionStyleId = ds.Id; add(h); n++; }

            // 주요 치수: 원·원호 지름 (크기별 하나씩, 같은 크기가 여러 개면 "4× Ø6mm")
            var circles = FindCircles(placed.Where(p => p.Layer == "외형선").Select(p => p.Curve), doc.ModelAbsoluteTolerance * 5);
            var bySize = new List<List<Circle>>();
            foreach (var c in circles.OrderByDescending(c => c.Radius))
            {
                var g = bySize.FirstOrDefault(l => Math.Abs(l[0].Radius - c.Radius) < 0.05 * u * s);
                if (g == null) bySize.Add(new List<Circle> { c }); else g.Add(c);
            }
            // 지시선 방향을 치수마다 바꾸고(오른쪽 위 → 왼쪽 위 → 왼쪽 아래 → 오른쪽 아래 …), 그림 바깥까지 빼서 글자가 안 겹치게
            double[] dirs = { 45, 135, 225, 315, 70, 110 };
            int k = 0;
            foreach (var g in bySize.Take(maxCircles))
            {
                double dia = 2 * g[0].Radius / s / u;
                if (Math.Abs(dia - wMm) < 0.5 || Math.Abs(dia - hMm) < 0.5) continue; // 외곽과 같은 원은 전체 치수로 충분
                double angd = dirs[k++ % dirs.Length] * Math.PI / 180;
                var dir = new Vector3d(Math.Cos(angd), Math.Sin(angd), 0);
                var c = g.OrderByDescending(x => (x.Center - gbb.Center) * dir).First(); // 그 방향 바깥쪽에 있는 원
                var onC = c.Center + dir * c.Radius;
                double exit = ExitDistance(c.Center, dir, gbb);
                double len = Math.Max(c.Radius + 8 * u, exit + 6 * u);
                var rd = RadialDimension.Create(ds, AnnotationType.Diameter, Plane.WorldXY, c.Center, onC, c.Center + dir * len);
                if (rd == null) continue;
                rd.DimensionStyleId = ds.Id;
                if (g.Count > 1) rd.PlainText = $"{g.Count}× Ø<>";
                add(rd); n++;
            }
            return n;
        }

        // 뷰 방향(카메라가 보는 방향)과 위쪽
        static (Vector3d Dir, Vector3d Up) ViewDir(RhinoDoc doc, int view, int angle)
        {
            switch (view)
            {
                case 1: return (Vector3d.YAxis, Vector3d.ZAxis);         // 정면 (앞에서 뒤로)
                case 2: return (-Vector3d.XAxis, Vector3d.ZAxis);        // 우측면
                case 3: return (Vector3d.ZAxis, -Vector3d.YAxis);        // 바닥면
                case 4: return (-Vector3d.YAxis, Vector3d.ZAxis);        // 배면
                case 5: return (Vector3d.XAxis, Vector3d.ZAxis);         // 좌측면
                case View3D:
                {
                    var dir = new Vector3d(1, 1, -1);                      // 등각: 왼쪽 앞 위에서 (Rhino 남서 등각)
                    if (angle == 1)
                    {
                        // 지금 Perspective 뷰 카메라 방향 (평행 투영으로, 길이가 일그러지지 않게)
                        var vp = doc.Views.Select(x => x.ActiveViewport).FirstOrDefault(x => x.IsPerspectiveProjection && x.Name == "Perspective")
                              ?? doc.Views.Select(x => x.ActiveViewport).FirstOrDefault(x => x.IsPerspectiveProjection);
                        if (vp != null) dir = vp.CameraDirection;
                        else RhinoApp.WriteLine("Perspective 뷰가 없어 3D는 등각으로 그려요.");
                    }
                    dir.Unitize();
                    var up = Vector3d.ZAxis - (Vector3d.ZAxis * dir) * dir;
                    if (up.Length < 1e-6) up = Vector3d.YAxis;
                    up.Unitize();
                    return (dir, up);
                }
                default: return (-Vector3d.ZAxis, Vector3d.YAxis);       // 탑뷰
            }
        }

        public class Line2D { public Curve Curve; public string Layer; }

        static List<Line2D> Make2D(List<RhinoObject> objs, BoundingBox bb, Vector3d dir, Vector3d up, bool hidden, bool tangent, double tol)
        {
            double r = Math.Max(bb.Diagonal.Length, 1.0);
            var vp = new ViewportInfo();
            vp.ChangeToParallelProjection(true);
            vp.SetCameraLocation(bb.Center - dir * r * 2);
            vp.SetCameraDirection(dir);
            vp.SetCameraUp(up);
            vp.SetFrustum(-r, r, -r, r, r * 0.5, r * 4);

            var prm = new HiddenLineDrawingParameters
            {
                AbsoluteTolerance = tol,
                Flatten = true,
                IncludeHiddenCurves = hidden,
                IncludeTangentEdges = tangent,
                IncludeTangentSeams = false,
            };
            prm.SetViewport(vp);
            foreach (var o in objs) prm.AddGeometry(o.Geometry, Transform.Identity, o.Id);
            var hld = HiddenLineDrawing.Compute(prm, true);
            var outl = new List<Line2D>();
            if (hld == null) return outl;
            var flat = Transform.PlanarProjection(Plane.WorldXY);
            foreach (var seg in hld.Segments)
            {
                if (seg?.ParentCurve == null || seg.CurveGeometry == null) continue;
                var v = seg.SegmentVisibility;
                if (v != HiddenLineDrawingSegment.Visibility.Visible && v != HiddenLineDrawingSegment.Visibility.Hidden) continue;
                var t = seg.ParentCurve.SilhouetteType;
                // Tangent = 둥근 면의 실제 외곽선(외형선). NonSilhouetteTangent = 필렛 등 면이 매끈하게 이어지는 경계(탄젠트선)
                bool isTan = t == SilhouetteType.NonSilhouetteTangent;
                if (t == SilhouetteType.NonSilhouetteSeam) continue;
                string layer = v == HiddenLineDrawingSegment.Visibility.Hidden ? "숨은선" : isTan ? "탄젠트선" : "외형선";
                if (layer == "숨은선" && !hidden) continue;
                if (layer == "탄젠트선" && !tangent) continue;
                var c = seg.CurveGeometry.DuplicateCurve();
                c.Transform(flat);
                if (c.GetLength() < tol) continue;
                outl.Add(new Line2D { Curve = c, Layer = layer });
            }
            return outl;
        }

        // 점에서 dir 방향으로 상자(XY)를 빠져나가는 거리
        static double ExitDistance(Point3d p, Vector3d dir, BoundingBox bb)
        {
            double t = double.MaxValue;
            if (Math.Abs(dir.X) > 1e-9) t = Math.Min(t, ((dir.X > 0 ? bb.Max.X : bb.Min.X) - p.X) / dir.X);
            if (Math.Abs(dir.Y) > 1e-9) t = Math.Min(t, ((dir.Y > 0 ? bb.Max.Y : bb.Min.Y) - p.Y) / dir.Y);
            return t == double.MaxValue ? 0 : Math.Max(0, t);
        }

        // 원·원호 찾기: 다른 선에 잘린 원호도 중심·반지름이 같으면 모아서, 합쳐 180° 이상이면 원으로
        static List<Circle> FindCircles(IEnumerable<Curve> curves, double tol)
        {
            var arcs = new List<Arc>();
            foreach (var c in curves)
            {
                var segs = c is PolyCurve pc ? pc.Explode() : new[] { c };
                foreach (var sgm in segs)
                {
                    if (sgm.TryGetCircle(out var ci, tol)) { arcs.Add(new Arc(ci, Math.PI * 2)); continue; }
                    if (sgm.TryGetArc(out var ar, tol) && ar.AngleDegrees >= 5) arcs.Add(ar);
                }
            }
            var groups = new List<(Point3d C, double R, double Ang)>();
            foreach (var ar in arcs)
            {
                int i = groups.FindIndex(g => g.C.DistanceTo(ar.Center) < tol * 4 && Math.Abs(g.R - ar.Radius) < tol * 4);
                if (i < 0) groups.Add((ar.Center, ar.Radius, ar.Angle));
                else groups[i] = (groups[i].C, groups[i].R, groups[i].Ang + ar.Angle);
            }
            return groups.Where(g => g.Ang >= Math.PI - 1e-3).Select(g => new Circle(new Plane(g.C, Vector3d.ZAxis), g.R)).ToList();
        }

        static string DefaultName(RhinoDoc doc, List<RhinoObject> objs = null)
        {
            var n = objs?.Select(o => o.Attributes.Name).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            if (!string.IsNullOrWhiteSpace(n)) return n.Trim();
            return string.IsNullOrEmpty(doc.Name) ? "제품" : Path.GetFileNameWithoutExtension(doc.Name).Replace(" 예시", "").Trim();
        }

        internal static string AskString(string prompt, string def)
        {
            var gs = new GetString();
            gs.SetCommandPrompt(prompt);
            if (!string.IsNullOrEmpty(def)) gs.SetDefaultString(def);
            gs.AcceptNothing(true);
            var r = gs.GetLiteralString();
            if (r == GetResult.Nothing) return def;
            if (r != GetResult.String) return null;
            var s = gs.StringResult().Trim().Trim('"');
            return string.IsNullOrEmpty(s) ? def : s;
        }
    }
}
