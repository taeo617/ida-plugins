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
    // A4Drawing: 선택한 객체의 탑뷰(또는 정면·우측면) Make2D → 전체·주요 치수 → A4 도곽·표제란에
    // 실제 축척(1:1, 1:2 …)으로 배치 → SVG·AI 내보내기. 원본 객체는 건드리지 않음.
    public class A4DrawingCommand : Command
    {
        public override string EnglishName => "A4Drawing" + Ver.Sfx;

        static readonly string[] ViewNames = { "Top", "Front", "Right", "Bottom", "Back", "Left" };
        static readonly string[] ViewKo = { "탑뷰", "정면", "우측면", "바닥면", "배면", "좌측면" };
        static readonly string[] ScaleNames = { "Auto", "5to1", "2to1", "1to1", "1to2", "1to5", "1to10", "1to20", "1to50" };
        static readonly double[] ScaleValues = { 0, 5, 2, 1, 0.5, 0.2, 0.1, 0.05, 0.02 };

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var st = A4DrawPlugIn.Instance.Settings;
            int view = Math.Max(0, Array.IndexOf(ViewNames, st.GetString("view", "Top")));
            int scaleIx = Math.Max(0, Array.IndexOf(ScaleNames, st.GetString("scale", "Auto")));
            var optHidden = new OptionToggle(st.GetBool("hidden", false), "Off", "On");
            var optTangent = new OptionToggle(st.GetBool("tangent", true), "Off", "On");
            var optExport = new OptionToggle(st.GetBool("export", true), "Off", "On");
            var optAi = new OptionToggle(st.GetBool("ai", true), "Off", "On");
            var optPdf = new OptionToggle(st.GetBool("pdf", true), "Off", "On");
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
                int viewOpt = go.AddOptionList("View", ViewNames, view);
                int scaleOpt = go.AddOptionList("Scale", ScaleNames, scaleIx);
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
                    if (oi == viewOpt) view = go.Option().CurrentListOptionIndex;
                    else if (oi == scaleOpt) scaleIx = go.Option().CurrentListOptionIndex;
                    else if (oi == nameOpt) name = AskString("제품명 (도면 표제란·파일 이름)", name ?? DefaultName(doc)) ?? name;
                    else if (oi == folderOpt)
                    {
                        var picked = mode == RunMode.Interactive ? Export.PickFolder(Export.DefaultFolder(doc, st)) : AskString("저장 폴더", Export.DefaultFolder(doc, st));
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
            st.SetString("view", ViewNames[view]); st.SetString("scale", ScaleNames[scaleIx]);
            st.SetBool("hidden", optHidden.CurrentValue); st.SetBool("tangent", optTangent.CurrentValue);
            st.SetBool("export", optExport.CurrentValue); st.SetBool("ai", optAi.CurrentValue); st.SetBool("pdf", optPdf.CurrentValue);

            var objs = Enumerable.Range(0, go.ObjectCount).Select(i => go.Object(i).Object()).Where(o => o != null).ToList();
            if (objs.Count == 0) return Result.Nothing;
            if (string.IsNullOrWhiteSpace(name)) name = DefaultName(doc, objs);

            double u = RhinoMath.UnitScale(UnitSystem.Millimeters, doc.ModelUnitSystem); // 1mm = 문서 단위로 몇
            var srcBox = BoundingBox.Empty;
            foreach (var o in objs) srcBox.Union(o.Geometry.GetBoundingBox(true));

            // 1) Make2D (HiddenLineDrawing)
            RhinoApp.WriteLine("A4 도면: Make2D 계산 중…");
            var lines = Make2D(objs, srcBox, view, optHidden.CurrentValue, optTangent.CurrentValue, doc.ModelAbsoluteTolerance);
            var vis = lines.Where(l => l.Layer == "외형선").ToList();
            if (vis.Count == 0) { RhinoApp.WriteLine("보이는 선이 없어요. 다른 방향(View)으로 해 보세요."); return Result.Failure; }
            var dbb = BoundingBox.Empty;
            foreach (var l in lines) dbb.Union(l.Curve.GetBoundingBox(true));
            double wMm = dbb.Max.X - dbb.Min.X, hMm = dbb.Max.Y - dbb.Min.Y; // 문서 단위
            wMm /= u; hMm /= u;

            // 2) 용지 방향·축척: 세로가 훨씬 길면 세로 A4
            bool portrait = hMm > wMm * 1.15;
            double PW = portrait ? 210 : 297, PH = portrait ? 297 : 210;
            const double M = 10;                 // 도곽 여백
            const double TB_H = 18;              // 하단 띠 표제란 높이
            // 그림 영역: 도곽 안, 표제란 위, 치수 자리(아래·오른쪽 22mm, 위·왼쪽 10mm)
            double areaX0 = M + 12, areaX1 = PW - M - 24, areaY0 = M + TB_H + 22, areaY1 = PH - M - 12;
            double s = ScaleValues[scaleIx] > 0 ? ScaleValues[scaleIx] : Page.FitScale(wMm, hMm, areaX1 - areaX0, areaY1 - areaY0);
            if (wMm * s > areaX1 - areaX0 + 1e-6 || hMm * s > areaY1 - areaY0 + 1e-6)
                RhinoApp.WriteLine($"주의: 축척 {Page.ScaleText(s)}로는 A4 그림 영역보다 커요. Scale=Auto를 권해요.");

            // 3) 놓을 자리: 원본 오른쪽(이미 도면이 있으면 그 오른쪽)
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

            // 용지(A4 크기 그대로) + 도곽
            var paper = new Rectangle3d(new Plane(P(0, 0), Vector3d.XAxis, Vector3d.YAxis), PW * u, PH * u).ToNurbsCurve();
            var pa = Page.Attrs(doc, "용지", pageId);
            pa.SetUserString(Page.KeyFrame, $"{O.X:R},{O.Y:R},{PW * u:R},{PH * u:R}");
            pa.SetUserString(Page.KeyName, name);
            pa.Name = "A4 용지 - " + name;
            Add(paper, "용지", pa);
            Add(new Rectangle3d(new Plane(P(M, M), Vector3d.XAxis, Vector3d.YAxis), (PW - 2 * M) * u, (PH - 2 * M) * u).ToNurbsCurve(), "도곽");

            // 그림: 영역 가운데
            double cx = (areaX0 + areaX1) / 2, cy = (areaY0 + areaY1) / 2;
            var dc = dbb.Center;
            var xf = Transform.Translation(P(cx, cy) - Point3d.Origin) * Transform.Scale(Point3d.Origin, s) * Transform.Translation(Point3d.Origin - new Point3d(dc.X, dc.Y, 0));
            var placed = new List<(Curve Curve, string Layer)>();
            foreach (var grp in lines.GroupBy(l => l.Layer))
            {
                var crvs = grp.Select(l => { var c = l.Curve.DuplicateCurve(); c.Transform(xf); return c; }).ToArray();
                var joined = Curve.JoinCurves(crvs, doc.ModelAbsoluteTolerance * 2);
                foreach (var c in joined.Length > 0 ? joined : crvs) { Add(c, grp.Key); placed.Add((c, grp.Key)); }
            }
            var gbb = BoundingBox.Empty;
            foreach (var p in placed) if (p.Layer != "숨은선") gbb.Union(p.Curve.GetBoundingBox(true));

            // 4) 치수 (용지 위 길이 × 1/축척 = 실제 mm)
            var ds = Page.DimStyle(doc, s, u);
            int nDims = 0;
            {
                var a1 = new Point3d(gbb.Min.X, gbb.Min.Y, 0); var a2 = new Point3d(gbb.Max.X, gbb.Min.Y, 0);
                var w = LinearDimension.Create(AnnotationType.Aligned, ds, Plane.WorldXY, Vector3d.XAxis, a1, a2, new Point3d(gbb.Center.X, gbb.Min.Y - 10 * u, 0), 0);
                if (w != null) { w.DimensionStyleId = ds.Id; Add(w, "치수"); nDims++; }
                var b1 = new Point3d(gbb.Max.X, gbb.Min.Y, 0); var b2 = new Point3d(gbb.Max.X, gbb.Max.Y, 0);
                // 세로 치수: X축이 위(+Y)인 평면에서 재야 길이가 나옴 (WorldXY에서는 0mm)
                var vpl = new Plane(b1, Vector3d.YAxis, -Vector3d.XAxis);
                var h = LinearDimension.Create(AnnotationType.Aligned, ds, vpl, vpl.XAxis, b1, b2, new Point3d(gbb.Max.X + 10 * u, gbb.Center.Y, 0), 0);
                if (h != null) { h.DimensionStyleId = ds.Id; Add(h, "치수"); nDims++; }
            }
            // 주요 치수: 원·원호 지름 (크기별 하나씩, 최대 5개, 같은 크기가 여러 개면 "4× Ø6mm")
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
            foreach (var g in bySize.Take(5))
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
                Add(rd, "치수"); nDims++;
            }

            // 5) 뷰 이름 + 표제란
            var tstyle = ds;
            void Text(string t, double x, double y, double hMmTxt, TextJustification j, string layer = "글자")
            {
                var te = TextEntity.Create(t, new Plane(P(x, y), Vector3d.XAxis, Vector3d.YAxis), tstyle, false, 0, 0);
                if (te == null) return;
                te.TextHeight = hMmTxt * u;
                te.Justification = j;
                te.DimensionStyleId = tstyle.Id;
                Add(te, layer);
            }
            Text($"{ViewKo[view]}  {Page.ScaleText(s)}", (gbb.Center.X - O.X) / u, (gbb.Min.Y - O.Y) / u - 24, 3, TextJustification.BottomCenter);

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
            string[] values = { $"{name} · {ViewKo[view]}", $"{Page.ScaleText(s)} · mm", DateTime.Now.ToString("yyyy.MM.dd"), "FD-" + DateTime.Now.ToString("yyMMdd"), "1 / 1" };
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
                int gi = doc.Groups.Add("A4 도면 - " + name, added);
                if (gi < 0) RhinoApp.WriteLine("그룹을 만들지 못했어요 (도면은 그대로 있어요)");
            }
            doc.Views.Redraw();
            RhinoApp.WriteLine($"A4 도면: {name} · {(portrait ? "세로" : "가로")} A4 · 축척 {Page.ScaleText(s)} · 실제 {Math.Round(wMm)}×{Math.Round(hMm)}mm · 치수 {nDims}개 · 레이어 '{Page.Root}'");

            if (!optExport.CurrentValue) return Result.Success;
            string folder = outDir ?? (mode == RunMode.Interactive ? Export.PickFolder(Export.DefaultFolder(doc, st)) : Export.DefaultFolder(doc, st));
            if (folder == null) { RhinoApp.WriteLine("내보내기를 취소했어요 (도면은 Rhino에 그대로 있어요). 나중에 A4DrawingExport로 내보낼 수 있어요."); return Result.Success; }
            st.SetString("folder", folder);
            Export.Run(doc, pageId.ToString(), folder, optAi.CurrentValue, optPdf.CurrentValue);
            return Result.Success;
        }

        public class Line2D { public Curve Curve; public string Layer; }

        static List<Line2D> Make2D(List<RhinoObject> objs, BoundingBox bb, int view, bool hidden, bool tangent, double tol)
        {
            Vector3d dir, up;
            switch (view)
            {
                case 1: dir = Vector3d.YAxis; up = Vector3d.ZAxis; break;         // 정면 (앞에서 뒤로)
                case 2: dir = -Vector3d.XAxis; up = Vector3d.ZAxis; break;        // 우측면
                case 3: dir = Vector3d.ZAxis; up = -Vector3d.YAxis; break;        // 바닥면
                case 4: dir = -Vector3d.YAxis; up = Vector3d.ZAxis; break;        // 배면
                case 5: dir = Vector3d.XAxis; up = Vector3d.ZAxis; break;         // 좌측면
                default: dir = -Vector3d.ZAxis; up = Vector3d.YAxis; break;       // 탑뷰
            }
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
