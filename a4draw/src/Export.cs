using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace IDA.A4Draw
{
    // A4 도면 한 장을 SVG(실제 mm 크기, 레이어별 그룹, 선 두께, 글자는 편집 가능한 텍스트)로 쓰고,
    // 일러스트레이터가 있으면 그 SVG를 열어 레이어로 정리한 .ai로 저장
    static class Export
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        static string F(double v) => Math.Round(v, 3).ToString("0.###", IC);

        public static string DefaultFolder(RhinoDoc doc, Rhino.PersistentSettings st)
        {
            var last = st.GetString("folder", "");
            if (!string.IsNullOrEmpty(last) && Directory.Exists(last)) return last;
            string dir = !string.IsNullOrEmpty(doc.Path) ? Path.GetDirectoryName(doc.Path) : System.Environment.GetFolderPath(System.Environment.SpecialFolder.DesktopDirectory);
            return Path.Combine(dir, "도면");
        }

        public static string PickFolder(string start)
        {
            var dlg = new Eto.Forms.SelectFolderDialog { Title = "도면(SVG·AI)을 저장할 폴더를 고르세요" };
            var d = start;
            while (!string.IsNullOrEmpty(d) && !Directory.Exists(d)) d = Path.GetDirectoryName(d);
            if (!string.IsNullOrEmpty(d)) dlg.Directory = d;
            return dlg.ShowDialog(Rhino.UI.RhinoEtoApp.MainWindow) == Eto.Forms.DialogResult.Ok ? dlg.Directory : null;
        }

        public static bool Run(RhinoDoc doc, string page, string folder, bool ai)
        {
            var objs = Page.ObjectsOf(doc, page);
            var frame = objs.FirstOrDefault(o => !string.IsNullOrEmpty(o.Attributes.GetUserString(Page.KeyFrame)));
            if (frame == null) { RhinoApp.WriteLine("A4 용지를 찾지 못했어요."); return false; }
            var f = frame.Attributes.GetUserString(Page.KeyFrame).Split(',').Select(x => double.Parse(x, IC)).ToArray();
            string name = frame.Attributes.GetUserString(Page.KeyName) ?? "도면";
            double u = RhinoMath.UnitScale(UnitSystem.Millimeters, doc.ModelUnitSystem);
            double ox = f[0], oy = f[1], pw = f[2] / u, ph = f[3] / u;

            Directory.CreateDirectory(folder);
            string safe = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
            string orient = pw > ph ? "A4 가로" : "A4 세로";
            string svgPath = Path.Combine(folder, $"{safe}_도면_{orient}.svg");
            string aiPath = Path.ChangeExtension(svgPath, ".ai");

            var svg = Svg(doc, objs, ox, oy, pw, ph, u);
            File.WriteAllText(svgPath, svg, new UTF8Encoding(false));
            RhinoApp.WriteLine("SVG 저장: " + svgPath);

            if (ai)
            {
                var msg = ToIllustrator(svgPath, aiPath);
                RhinoApp.WriteLine(msg);
            }
            return true;
        }

        // ---------- SVG ----------
        class Sink { public StringBuilder Paths = new StringBuilder(); public StringBuilder Fills = new StringBuilder(); public StringBuilder Texts = new StringBuilder(); }

        static string Svg(RhinoDoc doc, List<RhinoObject> objs, double ox, double oy, double pw, double ph, double u)
        {
            Point2d Map(Point3d p) => new Point2d((p.X - ox) / u, ph - (p.Y - oy) / u);
            var groups = new Dictionary<string, Sink>();
            Sink G(string n) { if (!groups.TryGetValue(n, out var s)) groups[n] = s = new Sink(); return s; }

            foreach (var o in objs)
            {
                var layer = doc.Layers[o.Attributes.LayerIndex];
                string ln = layer.Name;
                if (ln == "용지") continue; // 용지 = SVG 대지(artboard)
                switch (o.Geometry)
                {
                    case Curve c:
                        G(ln).Paths.Append(PathTag(c, Map, false));
                        break;
                    case Dimension ab:
                        var ds = ab.DimensionStyleId != Guid.Empty ? doc.DimStyles.FindId(ab.DimensionStyleId) : null;
                        foreach (var part in ab.Explode() ?? new GeometryBase[0])
                        {
                            // 분해 결과에 글자가 섞여 나올 때가 있음 → 버리고 DimText 하나만 씀 (겹침 방지)
                            if (part is TextEntity) continue;
                            if (part is Curve pc)
                            {
                                bool arrow = pc.IsClosed && pc.GetBoundingBox(true).Diagonal.Length < 6 * u;
                                (arrow ? G(ln).Fills : G(ln).Paths).Append(PathTag(pc, Map, arrow));
                            }
                            else if (part is Hatch hh)
                                foreach (var bc in hh.Get3dCurves(true)) G(ln).Fills.Append(PathTag(bc, Map, true));
                        }
                        G(ln).Texts.Append(DimText(doc, ab, ds, Map, u));
                        break;
                    case TextEntity te:
                        G(ln).Texts.Append(TextTag(te, Map, u));
                        break;
                    case Hatch hz:
                        G(ln).Fills.Append(HatchTag(hz, Map));
                        break;
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(pw)}mm\" height=\"{F(ph)}mm\" viewBox=\"0 0 {F(pw)} {F(ph)}\">");
            sb.AppendLine("  <!-- found/Founded A4 도면 · 1 단위 = 1mm · 선 두께는 출력 기준 mm -->");
            // 아래 레이어부터: 도곽 → 그림 → 치수 → 글자
            string[] order = { "도곽", "표제란", "로고", "표제란 글자", "표제란 보조", "숨은선", "탄젠트선", "외형선", "치수", "글자" };
            foreach (var ln in order.Concat(groups.Keys.Except(order)))
            {
                if (!groups.TryGetValue(ln, out var s)) continue;
                var spec = Page.Layers.FirstOrDefault(l => l.Name == ln);
                var li = doc.Layers.FindByFullPath(Page.Root + "::" + ln, -1);
                double w = li >= 0 && doc.Layers[li].PlotWeight > 0 ? doc.Layers[li].PlotWeight : (spec.Name != null ? spec.Weight : 0.25);
                var col = li >= 0 ? doc.Layers[li].PlotColor : System.Drawing.Color.Black;
                if (spec.Name != null && Page.IsWhite(spec.Color)) col = System.Drawing.Color.White;   // 검은 띠 위 흰 로고·글자
                else if (col.A == 0 || Page.IsWhite(col)) col = System.Drawing.Color.Black;
                string hex = $"#{col.R:X2}{col.G:X2}{col.B:X2}";
                string dash = spec.Dashed ? " stroke-dasharray=\"1.5 0.8\"" : "";
                sb.AppendLine($"  <g id=\"{ln}\" fill=\"none\" stroke=\"{hex}\" stroke-width=\"{F(w)}\" stroke-linecap=\"round\" stroke-linejoin=\"round\"{dash}>");
                sb.Append(s.Paths);
                if (s.Fills.Length > 0) { sb.AppendLine($"    <g fill=\"{hex}\" stroke=\"none\">"); sb.Append(s.Fills); sb.AppendLine("    </g>"); }
                if (s.Texts.Length > 0) { sb.AppendLine($"    <g fill=\"{hex}\" stroke=\"none\" font-family=\"Pretendard, 'Pretendard Variable', sans-serif\">"); sb.Append(s.Texts); sb.AppendLine("    </g>"); }
                sb.AppendLine("  </g>");
            }
            sb.AppendLine("</svg>");
            return sb.ToString();
        }

        static string PathTag(Curve c, Func<Point3d, Point2d> map, bool fill) => $"    <path d=\"{PathD(c, map)}\"/>\n";

        // 해치(면): 바깥 + 안쪽(구멍) 경계를 한 경로로, 짝홀 규칙으로 구멍 뚫기
        static string HatchTag(Hatch h, Func<Point3d, Point2d> map)
        {
            var d = string.Join(" ", h.Get3dCurves(true).Concat(h.Get3dCurves(false)).Select(c => PathD(c, map)));
            return d.Length == 0 ? "" : $"    <path fill-rule=\"evenodd\" d=\"{d}\"/>\n";
        }

        static string PathD(Curve c, Func<Point3d, Point2d> map)
        {
            var d = new StringBuilder();
            void M(Point3d p) { var q = map(p); d.Append("M").Append(F(q.X)).Append(' ').Append(F(q.Y)); }
            void L(Point3d p) { var q = map(p); d.Append(" L").Append(F(q.X)).Append(' ').Append(F(q.Y)); }
            if (c.TryGetPolyline(out var pl))
            {
                M(pl[0]); for (int i = 1; i < pl.Count; i++) L(pl[i]);
            }
            else
            {
                var segs = c is PolyCurve pc ? pc.Explode() : new[] { c };
                M(c.PointAtStart);
                foreach (var sg in segs)
                {
                    if (sg.IsLinear(0.001)) { L(sg.PointAtEnd); continue; }
                    if (sg.TryGetPolyline(out var spl)) { for (int i = 1; i < spl.Count; i++) L(spl[i]); continue; }
                    var bz = BezierCurve.CreateCubicBeziers(sg, 0.001, 0.01);
                    if (bz == null || bz.Length == 0) { L(sg.PointAtEnd); continue; }
                    foreach (var b in bz)
                    {
                        var p1 = map(b.GetControlVertex3d(1)); var p2 = map(b.GetControlVertex3d(2)); var p3 = map(b.GetControlVertex3d(3));
                        d.Append(" C").Append(F(p1.X)).Append(' ').Append(F(p1.Y)).Append(' ').Append(F(p2.X)).Append(' ').Append(F(p2.Y)).Append(' ').Append(F(p3.X)).Append(' ').Append(F(p3.Y));
                    }
                }
            }
            if (c.IsClosed) d.Append(" Z");
            return d.ToString();
        }

        // 치수 숫자: 분해하면 글자가 빠지므로 값·위치를 직접 읽어 편집 가능한 <text>로
        static string DimText(RhinoDoc doc, Dimension dm, DimensionStyle ds, Func<Point3d, Point2d> map, double u)
        {
            ds = ds ?? doc.DimStyles.Current;
            string val = dm is LinearDimension ld ? ld.GetDistanceDisplayText(doc.ModelUnitSystem, ds)
                       : dm is RadialDimension rd ? rd.GetDistanceDisplayText(doc.ModelUnitSystem, ds) : "";
            string t = val; // 사용자가 고친 글자("4× Ø<>" 등)까지 반영된 값
            if (dm is RadialDimension rd2 && rd2.IsDiameterDimension && !t.Contains("Ø") && !t.Contains("⌀")) t = "Ø" + t;
            if (string.IsNullOrEmpty(t)) return "";
            double h = (dm.TextHeight > 0 ? dm.TextHeight : ds.TextHeight) / u;
            var pl = dm.Plane;
            var c = pl.PointAt(dm.TextPosition.X, dm.TextPosition.Y);      // 글자 가운데
            var xa = pl.XAxis; xa.Rotate(dm.TextRotation, pl.ZAxis);
            bool leftSide = false;
            if (dm is RadialDimension rdim)
            {
                xa = Vector3d.XAxis;                                         // 지름 치수 글자는 가로로
                rdim.Get3dPoints(out var cp, out _, out var dp, out _);
                leftSide = dp.X < cp.X - 1e-6;                               // 왼쪽으로 뺀 지시선은 글자를 왼쪽에
                c = dp;
            }
            if (xa.X < -1e-6 || (Math.Abs(xa.X) < 1e-6 && xa.Y < 0)) xa = -xa; // 거꾸로 읽히지 않게 (아래→위, 왼→오)
            var ya = Vector3d.CrossProduct(Vector3d.ZAxis, xa);
            // 선형 치수: TextPosition은 치수선 위 → 글자 간격만큼 띄움.
            // 지름 치수: 지시선 꺾인 곳(가로 선) 위에 올려 선이 글자를 가로지르지 않게
            bool radial = dm is RadialDimension;
            double gap = ds.TextGap > 0 ? ds.TextGap : 0.8 * u;
            var basePt = radial ? c + ya * gap + xa * (leftSide ? -0.5 * gap : 0.5 * gap) : c + ya * gap;
            var q = map(basePt);
            double ang = -Math.Atan2(xa.Y, xa.X) * 180 / Math.PI;
            string rot = Math.Abs(ang) > 0.01 ? $" transform=\"rotate({F(ang)} {F(q.X)} {F(q.Y)})\"" : "";
            return $"      <text x=\"{F(q.X)}\" y=\"{F(q.Y)}\" font-size=\"{F(h / 0.72)}\" text-anchor=\"{(radial ? (leftSide ? "end" : "start") : "middle")}\"{rot}>{Esc(t)}</text>\n";
        }

        static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

        static string TextTag(TextEntity te, Func<Point3d, Point2d> map, double u)
        {
            string t = te.PlainText ?? "";
            if (t.Length == 0) return "";
            double h = te.TextHeight / u;                // Rhino 글자 높이 ≈ 대문자 높이
            double size = h / 0.72;                       // SVG font-size (em)
            var pl = te.Plane;
            var bb = te.GetBoundingBox(pl);               // 글자 평면 기준 상자
            // 글자 평면 좌표 → 기준점: 가로는 정렬에 따라, 세로는 아래(기준선)
            var ha = te.TextHorizontalAlignment;
            double lx = ha == TextHorizontalAlignment.Left ? bb.Min.X : ha == TextHorizontalAlignment.Right ? bb.Max.X : (bb.Min.X + bb.Max.X) / 2;
            string anchor = ha == TextHorizontalAlignment.Left ? "start" : ha == TextHorizontalAlignment.Right ? "end" : "middle";
            var basePt = pl.PointAt(lx, bb.Min.Y);
            var q = map(basePt);
            // 회전: 글자 X축 방향 (SVG는 Y가 아래로)
            double ang = -Math.Atan2(pl.XAxis.Y, pl.XAxis.X) * 180 / Math.PI;
            string rot = Math.Abs(ang) > 0.01 ? $" transform=\"rotate({F(ang)} {F(q.X)} {F(q.Y)})\"" : "";
            var lines = t.Replace("\r", "").Split('\n');
            var sb = new StringBuilder();
            if (lines.Length == 1)
                sb.Append($"      <text x=\"{F(q.X)}\" y=\"{F(q.Y)}\" font-size=\"{F(size)}\" text-anchor=\"{anchor}\"{rot}>{Esc(t)}</text>\n");
            else
            {
                double lh = h * 1.6;
                double top = q.Y - lh * (lines.Length - 1);
                sb.Append($"      <text x=\"{F(q.X)}\" y=\"{F(top)}\" font-size=\"{F(size)}\" text-anchor=\"{anchor}\"{rot}>");
                for (int i = 0; i < lines.Length; i++) sb.Append($"<tspan x=\"{F(q.X)}\" dy=\"{(i == 0 ? "0" : F(lh))}\">{Esc(lines[i])}</tspan>");
                sb.Append("</text>\n");
            }
            return sb.ToString();
        }

        // ---------- 일러스트레이터 (.ai) ----------
        static string ToIllustrator(string svgPath, string aiPath)
        {
            var t = Type.GetTypeFromProgID("Illustrator.Application");
            if (t == null) return "AI: 이 PC에 일러스트레이터가 없어 SVG만 저장했어요 (SVG를 일러스트에서 열고 다른 이름으로 저장하면 .ai가 돼요).";
            bool wasRunning = Process.GetProcessesByName("Illustrator").Length > 0;
            RhinoApp.WriteLine("AI: 일러스트레이터로 변환 중… (처음엔 일러스트가 켜지느라 조금 걸려요)");
            string J(string p) => p.Replace("\\", "/").Replace("'", "\\'");
            // 맨 위 그룹(레이어별)을 일러스트 레이어로 바꾸고 .ai로 저장, 레이어별 선 두께(pt)·글자 수 보고
            string js = @"
(function(){
  var f = new File('" + J(svgPath) + @"');
  var doc = app.open(f);
  var r = [];
  try {
    var base = doc.layers[0];
    var gs = [];
    for (var i = 0; i < base.groupItems.length; i++) gs.push(base.groupItems[i]);
    if (gs.length == 1 && gs[0].groupItems.length > 1 && gs[0].name == '') { var w0 = gs[0]; gs = []; for (var i = 0; i < w0.groupItems.length; i++) gs.push(w0.groupItems[i]); }
    // gs[0] = 맨 위 그룹. 새 레이어는 항상 맨 위에 생기므로 맨 아래 그룹부터 만들어 쌓임 순서 유지 (검은 띠가 로고·글자를 덮지 않게)
    for (var i = gs.length - 1; i >= 0; i--) {
      var g = gs[i]; var L = doc.layers.add(); L.name = g.name || ('레이어 ' + (i + 1));
      g.move(L, ElementPlacement.PLACEATBEGINNING);
    }
    try { if (base.pageItems.length == 0) base.remove(); } catch (e) {}
    var o = new IllustratorSaveOptions(); o.pdfCompatible = true; o.embedLinkedFiles = true;
    doc.saveAs(new File('" + J(aiPath) + @"'), o);
    var ab = doc.artboards[0].artboardRect;
    r.push('대지 ' + Math.round((ab[2] - ab[0]) / 72 * 25.4) + 'x' + Math.round((ab[1] - ab[3]) / 72 * 25.4) + 'mm');
    var walk = function (items, a) { for (var k = 0; k < items.length; k++) { var it = items[k];
      if (it.typename == 'GroupItem') walk(it.pageItems, a);
      else if (it.typename == 'PathItem') { a.p++; if (it.stroked) { var w = (Math.round(it.strokeWidth / 72 * 25.4 * 100) / 100) + 'mm'; if (!a.seen[w]) { a.seen[w] = 1; a.w.push(w); } } }
      else if (it.typename == 'TextFrame') a.t++; } };
    for (var i = 0; i < doc.layers.length; i++) {
      var L = doc.layers[i]; var a = { p: 0, t: 0, w: [], seen: {} }; walk(L.pageItems, a);
      r.push(L.name + ' 선' + a.p + (a.w.length ? '(' + a.w.join('·') + ')' : '') + ' 글자' + a.t);
    }
  } finally {
    doc.close(SaveOptions.DONOTSAVECHANGES);
  }
  return r.join(' | ');
})();";
            object app = null;
            try
            {
                app = Activator.CreateInstance(t);
                var res = t.InvokeMember("DoJavaScript", BindingFlags.InvokeMethod, null, app, new object[] { js });
                if (!wasRunning) { try { t.InvokeMember("Quit", BindingFlags.InvokeMethod, null, app, null); } catch { } }
                return File.Exists(aiPath) ? $"AI 저장: {aiPath}\n  ({res})" : "AI: 일러스트에서 저장하지 못했어요. " + res;
            }
            catch (Exception e)
            {
                var inner = e is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException.Message : e.Message;
                if (!wasRunning && app != null) { try { t.InvokeMember("Quit", BindingFlags.InvokeMethod, null, app, null); } catch { } }
                if (File.Exists(aiPath)) return $"AI 저장: {aiPath} (보고는 못 받았어요: {inner})";
                return "AI: 일러스트레이터 변환 실패 (" + inner + "). SVG는 저장됐어요.";
            }
            finally
            {
                if (app != null) try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(app); } catch { }
            }
        }
    }

    // A4DrawingExport: Rhino에서 치수·글자를 고친 A4 도면을 다시 SVG·AI로
    public class A4DrawingExportCommand : Command
    {
        public override string EnglishName => "A4DrawingExport" + Ver.Sfx;

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var st = A4DrawPlugIn.Instance.Settings;
            var optAi = new OptionToggle(st.GetBool("ai", true), "Off", "On");
            string outDir = null, page = null;
            var go = new GetObject();
            go.SetCommandPrompt("내보낼 A4 도면의 아무 선이나 고르세요");
            go.SubObjectSelect = false;
            go.EnablePreSelect(true, true);
            go.SetCustomGeometryFilter((o, g, ci) => !string.IsNullOrEmpty(o?.Attributes.GetUserString(Page.KeyPage)));
            while (true)
            {
                go.ClearCommandOptions();
                go.AddOptionToggle("AI", ref optAi);
                int folderOpt = go.AddOption("Folder");
                var r = go.Get();
                if (r == GetResult.Option)
                {
                    go.EnablePreSelect(false, true);
                    if (go.OptionIndex() == folderOpt)
                    {
                        var picked = mode == RunMode.Interactive ? Export.PickFolder(Export.DefaultFolder(doc, st)) : A4DrawingCommand.AskString("저장 폴더", Export.DefaultFolder(doc, st));
                        if (picked != null) outDir = picked.Trim().Trim('"');
                    }
                    continue;
                }
                if (r == GetResult.Nothing && page != null) break;
                if (r != GetResult.Object) return go.CommandResult();
                page = go.Object(0).Object()?.Attributes.GetUserString(Page.KeyPage);
                if (go.ObjectsWerePreselected && page != null)
                {
                    // 미리 고른 도면: 옵션(AI·Folder)을 더 받은 뒤 Enter
                    go.EnablePreSelect(false, true);
                    go.AcceptNothing(true);
                    go.SetCommandPrompt("옵션을 바꾸거나 Enter");
                    continue;
                }
                break;
            }
            st.SetBool("ai", optAi.CurrentValue);
            if (string.IsNullOrEmpty(page)) return Result.Failure;
            string folder = outDir ?? (mode == RunMode.Interactive ? Export.PickFolder(Export.DefaultFolder(doc, st)) : Export.DefaultFolder(doc, st));
            if (folder == null) return Result.Cancel;
            st.SetString("folder", folder);
            return Export.Run(doc, page, folder, optAi.CurrentValue) ? Result.Success : Result.Failure;
        }
    }
}
