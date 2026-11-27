using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace IDA.Bambu
{
    // Bambu: 선택한 객체를 뱀부랩 X2D에 맞게
    //   1) 서포트가 가장 적은 방향으로 눕히고  2) 판보다 크면 나누고(맞춤 핀 구멍)
    //   3) 판별로 배치하고  4) 판별 STL로 내보냄. 원본은 그대로 두고 복사본으로만 작업.
    public class BambuCommand : Command
    {
        public override string EnglishName => "Bambu";

        // Bambu Lab X2D (Bambu Studio 프린터 설정 기준, 노즐 하나)
        const double PlateW = 256, PlateD = 256, PlateH = 261;

        class Part
        {
            public string Name;
            public GeometryBase Geo;   // Brep 또는 Mesh (Xform 적용 전)
            public Mesh Mesh;          // 분석·내보내기용 메쉬 (Xform 적용 전)
            public OrientResult O;
            public PackItem P;
            public Transform Final;
        }

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var st = BambuPlugIn.Instance.Settings;
            var optSplit = new OptionToggle(st.GetBool("split", true), "Off", "On");
            var optPins = new OptionToggle(st.GetBool("pins", true), "Off", "On");
            var optExport = new OptionToggle(st.GetBool("export", true), "Off", "On");
            var optGap = new OptionDouble(st.GetDouble("gap", 5.0), 1.0, 50.0);
            // Prefer: 판보다 큰 것을 기울여 넣을지 / 나눠서 눕힐지 (Auto = 점수로 판단)
            int prefer = Math.Max(0, Array.IndexOf(PreferNames, st.GetString("prefer", "Auto")));

            var go = new GetObject();
            go.SetCommandPrompt("뱀부 X2D로 출력할 객체를 고르세요");
            go.GeometryFilter = ObjectType.Brep | ObjectType.Extrusion | ObjectType.Mesh | ObjectType.SubD;
            go.SubObjectSelect = false;
            go.GroupSelect = true;
            go.EnablePreSelect(true, true);
            string outDir = null;          // 이번에 고른 STL 폴더 (null이면 내보낼 때 물어봄)
            // 미리 고른 객체가 있어도 옵션(Split=On 등)을 받을 수 있게: 미리 고른 걸 받은 뒤 Enter까지 옵션만 더 받음
            bool havePre = false;
            while (true)
            {
                go.ClearCommandOptions();
                go.AddOptionToggle("Split", ref optSplit);
                go.AddOptionToggle("Pins", ref optPins);
                int preferOpt = go.AddOptionList("Prefer", PreferNames, prefer);
                go.AddOptionDouble("Gap", ref optGap);
                go.AddOptionToggle("ExportSTL", ref optExport);
                int folderOpt = go.AddOption("Folder");
                var r = go.GetMultiple(1, 0);
                if (r == GetResult.Option)
                {
                    go.EnablePreSelect(false, true);
                    if (go.OptionIndex() == preferOpt) prefer = go.Option().CurrentListOptionIndex;
                    else if (go.OptionIndex() == folderOpt)
                    {
                        var picked = mode == RunMode.Interactive ? PickFolder(DefaultFolder(doc, st)) : AskFolder(DefaultFolder(doc, st));
                        if (picked != null) { outDir = picked; RhinoApp.WriteLine("STL 폴더: " + outDir); }
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
            st.SetBool("split", optSplit.CurrentValue); st.SetBool("pins", optPins.CurrentValue);
            st.SetBool("export", optExport.CurrentValue); st.SetDouble("gap", optGap.CurrentValue);
            st.SetString("prefer", PreferNames[prefer]);

            double mm = RhinoMath.UnitScale(UnitSystem.Millimeters, doc.ModelUnitSystem); // 1mm = 문서 단위로 몇
            double toMm = 1.0 / mm;
            double tol = doc.ModelAbsoluteTolerance;
            double pw = PlateW * mm, pd = PlateD * mm, ph = PlateH * mm;
            var log = new List<string>();
            var parts = new List<Part>();
            var srcBox = BoundingBox.Empty;
            var mp = MeshingParameters.QualityRenderMesh;
            mp.JaggedSeams = false;

            RhinoApp.WriteLine("뱀부 X2D 출력 준비 중… (방향 찾기)");
            for (int i = 0; i < go.ObjectCount; i++)
            {
                var obj = go.Object(i).Object();
                if (obj == null) continue;
                srcBox.Union(obj.Geometry.GetBoundingBox(true));
                string name = !string.IsNullOrWhiteSpace(obj.Attributes.Name) ? obj.Attributes.Name
                            : doc.Layers[obj.Attributes.LayerIndex].Name + " " + (i + 1);
                GeometryBase geo = obj.Geometry;
                Brep brep = geo as Brep ?? (geo as Extrusion)?.ToBrep(true) ?? (geo as SubD)?.ToBrep(SubDToBrepOptions.Default);
                Mesh mesh = geo as Mesh;
                if (brep == null && mesh == null) continue;
                if (brep != null) { geo = brep; mesh = BrepMesh(brep, mp); }
                if (mesh == null || mesh.Faces.Count == 0) { log.Add($"• {name}: 메쉬를 만들지 못해 건너뜀"); continue; }

                // 1) 서포트 최소 자세(Free)가 판에 들어가면 그대로. 2) 아니면 기울여 넣기(Fit) vs 나눠서 눕히기(Free + 자르기 비용)
                var an = Orient.Analyze(mesh, mm, pw, pd, ph);
                var o = an.Fit ?? an.Free;
                bool canSplit = optSplit.CurrentValue && brep != null && brep.IsSolid;
                bool doSplit = false;
                string why = null;
                if (!an.Free.Fits)
                {
                    int pieces0 = EstimatePieces(an.Free, pw - 6 * mm, ph, mm);
                    double thick = an.Free.Height * toMm;          // 눕혔을 때 두께 = 자른 면의 폭
                    bool pinOk = thick >= 8;                       // Ø4.2 핀 + 양쪽 벽 여유
                    double splitCost = an.Free.Score + (pieces0 - 1) * (0.6 + (pinOk ? 0 : 0.8));
                    string fitDesc = an.Fit == null ? null
                        : $"바닥 폭 {an.Fit.BaseWidth * toMm:0}mm·높이 {an.Fit.Height * toMm:0}mm, 서포트 {an.Fit.OverhangArea * toMm * toMm:0}mm²";
                    if (an.Fit == null) { doSplit = true; why = "어느 방향으로도 판에 안 들어감"; }
                    else if (prefer == 1) { why = "Prefer=Tilt"; }
                    else if (prefer == 2) { doSplit = true; why = "Prefer=Split"; }
                    else if (an.Fit.Stability == 2) { doSplit = true; why = $"판에 넣으려면 세워야 하는데 모서리로만 서서 불안정({fitDesc})"; }
                    else if (splitCost < an.Fit.Score) { doSplit = true; why = $"기울이면 {fitDesc} — 나누는 쪽이 나음"; }
                    else why = null;

                    if (doSplit && canSplit)
                    {
                        log.Add($"• {name}: 눕혀서 나눔 — {why}");
                        var pieces = Split.ToFit(brep, an.Free.Xform, pw - 6 * mm, ph, optPins.CurrentValue, mm, tol, log);
                        if (!pinOk && optPins.CurrentValue) log.Add($"    두께 {thick:0.#}mm라 핀이 들어갈 자리가 없어요 → 자른 면은 접착(CA·에폭시) 권장");
                        int k = 0;
                        foreach (var pc in pieces)
                        {
                            var pm = BrepMesh(pc, mp);
                            if (pm == null || pm.Faces.Count == 0) continue;
                            k++;
                            parts.Add(new Part { Name = $"{name}-{k}", Geo = pc, Mesh = pm, O = Orient.Best(pm, mm, pw, pd, ph) });
                        }
                        log.Add($"    → 조각 {k}개");
                        continue;
                    }
                    if (an.Fit != null)
                    {
                        string tail = doSplit ? " (나누는 게 낫지만 " + NoSplitReason(brep, optSplit.CurrentValue) + " 기울여 넣음)" : "";
                        log.Add($"• {name}: 기울여 넣기 선택 — {fitDesc}, 나누면 {pieces0}조각" + (why != null && !doSplit ? $" ({why})" : "") + tail);
                    }
                    else log.Add($"• {name}: 판보다 큰데 " + NoSplitReason(brep, optSplit.CurrentValue) + " 나누지 못했어요");
                }
                parts.Add(new Part { Name = name, Geo = geo, Mesh = mesh, O = o });
            }
            if (parts.Count == 0) { RhinoApp.WriteLine("출력할 객체가 없어요."); return Result.Nothing; }

            // 판별 배치
            foreach (var p in parts) p.P = new PackItem { W = p.O.Width, D = p.O.Depth };
            int plates = Pack.Run(parts.Select(p => p.P).ToList(), pw, pd, optGap.CurrentValue * mm, 3 * mm);
            double step = pw + 40 * mm;
            var origin0 = new Point3d(srcBox.IsValid ? srcBox.Max.X + 100 * mm : 0, srcBox.IsValid ? srcBox.Min.Y : 0, 0);
            Point3d PlateOrigin(int k) => origin0 + new Vector3d(k * step, 0, 0);

            int parent = EnsureLayer(doc, "Bambu", -1, Color.FromArgb(40, 40, 40));
            var plateLayers = new int[plates];
            for (int k = 0; k < plates; k++)
            {
                plateLayers[k] = EnsureLayer(doc, $"판 {k + 1}", parent, Color.FromArgb(30, 120, 200));
                var a = new ObjectAttributes { LayerIndex = plateLayers[k] };
                var o = PlateOrigin(k);
                doc.Objects.AddPolyline(new[] { o, o + new Vector3d(pw, 0, 0), o + new Vector3d(pw, pd, 0), o + new Vector3d(0, pd, 0), o }, a);
                doc.Objects.AddTextDot($"판 {k + 1} · X2D", o + new Vector3d(pw / 2, pd + 8 * mm, 0), a);
            }
            int supLayer = EnsureLayer(doc, "서포트 필요", parent, Color.FromArgb(230, 40, 30));

            double overTotal = 0;
            foreach (var p in parts)
            {
                var x = p.O.Xform;
                // 90° 돌리면 (x,y)→(-y,x): X 범위가 [-깊이, 0]이 되므로 깊이만큼 옮김
                if (p.P.Rotated) x = Transform.Translation(p.O.Depth, 0, 0) * Transform.Rotation(Math.PI / 2, Vector3d.ZAxis, Point3d.Origin) * x;
                var o = PlateOrigin(p.P.Plate);
                p.Final = Transform.Translation(o.X + p.P.X, o.Y + p.P.Y, o.Z) * x;

                var a = new ObjectAttributes { LayerIndex = plateLayers[Math.Max(0, p.P.Plate)], Name = p.Name };
                var g = p.Geo.Duplicate(); g.Transform(p.Final);
                if (g is Brep b) doc.Objects.AddBrep(b, a); else if (g is Mesh m) doc.Objects.AddMesh(m, a);

                var placed = p.Mesh.DuplicateMesh(); placed.Transform(p.Final);
                var local = placed.DuplicateMesh(); local.Translate(new Vector3d(0, 0, -o.Z));
                var over = Orient.OverhangFaces(local, mm);
                if (over != null)
                {
                    over.Translate(new Vector3d(0, 0, o.Z));
                    over.Translate(new Vector3d(0, 0, 0.05 * mm));
                    doc.Objects.AddMesh(over, new ObjectAttributes { LayerIndex = supLayer, Name = p.Name + " 서포트", ColorSource = ObjectColorSource.ColorFromLayer });
                }
                overTotal += p.O.OverhangArea;
            }

            // STL 내보내기 (판마다 한 파일, 판 원점 = 0,0)
            string folder = null;
            if (optExport.CurrentValue)
            {
                // 폴더: 옵션에서 고른 곳 → (마우스로 실행하면) 저장 위치 묻기 → (스크립트면) 지난번 폴더 또는 Rhino 파일 옆 Bambu
                folder = outDir ?? (mode == RunMode.Interactive ? PickFolder(DefaultFolder(doc, st)) : DefaultFolder(doc, st));
                if (folder == null) RhinoApp.WriteLine("STL 저장을 취소했어요 (배치는 그대로 남아 있어요)");
            }
            if (folder != null)
            {
                string baseName = string.IsNullOrEmpty(doc.Name) ? "bambu" : Path.GetFileNameWithoutExtension(doc.Name);
                Directory.CreateDirectory(folder);
                st.SetString("folder", folder);
                for (int k = 0; k < plates; k++)
                {
                    var joined = new Mesh();
                    foreach (var p in parts.Where(q => q.P.Plate == k))
                    {
                        var m = p.Mesh.DuplicateMesh(); m.Transform(p.Final);
                        var o = PlateOrigin(k); m.Translate(new Vector3d(-o.X, -o.Y, -o.Z));
                        joined.Append(m);
                    }
                    if (joined.Faces.Count == 0) continue;
                    var file = Path.Combine(folder, plates == 1 ? $"{baseName}_bambu.stl" : $"{baseName}_판{k + 1}.stl");
                    Stl.Write(file, joined, toMm, baseName);
                }
            }
            doc.Views.Redraw();

            // 결과 정리
            RhinoApp.WriteLine("");
            RhinoApp.WriteLine($"뱀부 X2D 출력 준비 끝 — 파트 {parts.Count}개, 판 {plates}개");
            foreach (var l in log) RhinoApp.WriteLine(l);
            foreach (var p in parts)
            {
                double sup = p.O.OverhangArea * toMm * toMm;
                string s = sup < 1 ? "서포트 없음" : $"서포트 필요 {sup:0}mm² (빨간 표시)";
                string b = p.O.Stability == 1 ? ", 브림 권장" : p.O.Stability == 2 ? ", 쓰러질 수 있음(브림·서포트로 받쳐야 함)" : "";
                RhinoApp.WriteLine($"• {p.Name}: 판 {p.P.Plate + 1}, {p.O.Width * toMm:0}×{p.O.Depth * toMm:0}×{p.O.Height * toMm:0}mm, {s}{b}" + (p.O.Fits ? "" : " — 판보다 큼!"));
            }
            if (folder != null)
            {
                RhinoApp.WriteLine($"STL: {folder}");
                if (mode == RunMode.Interactive) try { System.Diagnostics.Process.Start("explorer.exe", "\"" + folder + "\""); } catch { }
            }
            return Result.Success;
        }

        static readonly string[] PreferNames = { "Auto", "Tilt", "Split" };

        // 눕힌 자세에서 판(여유 빼고)에 맞추려면 몇 조각이 되는지 (Split.ToFit과 같은 계산)
        static int EstimatePieces(OrientResult o, double limXY, double limZ, double mm)
        {
            int N(double len, double lim) => len <= lim ? 1 : (int)Math.Ceiling(len / (lim - 2 * mm));
            return N(o.Width, limXY) * N(o.Depth, limXY) * N(o.Height, limZ);
        }

        static string NoSplitReason(Brep brep, bool splitOn) =>
            brep == null ? "메쉬라서" : !brep.IsSolid ? "닫힌 솔리드가 아니라서" : !splitOn ? "나누기(Split)가 꺼져 있어서" : "";

        // 기본 STL 폴더: 지난번에 고른 곳 → Rhino 파일 옆 Bambu → 바탕화면Bambu
        static string DefaultFolder(RhinoDoc doc, Rhino.PersistentSettings st)
        {
            var last = st.GetString("folder", "");
            if (!string.IsNullOrEmpty(last) && Directory.Exists(last)) return last;
            string dir = !string.IsNullOrEmpty(doc.Path) ? Path.GetDirectoryName(doc.Path) : System.Environment.GetFolderPath(System.Environment.SpecialFolder.DesktopDirectory);
            return Path.Combine(dir, "Bambu");
        }

        // 폴더 고르기 창 (취소하면 null)
        static string PickFolder(string start)
        {
            var dlg = new Eto.Forms.SelectFolderDialog { Title = "STL을 저장할 폴더를 고르세요" };
            var d = start;
            while (!string.IsNullOrEmpty(d) && !Directory.Exists(d)) d = Path.GetDirectoryName(d);
            if (!string.IsNullOrEmpty(d)) dlg.Directory = d;
            return dlg.ShowDialog(Rhino.UI.RhinoEtoApp.MainWindow) == Eto.Forms.DialogResult.Ok ? dlg.Directory : null;
        }

        // 스크립트용: -Bambu _Folder "C:경로" (Enter만 치면 기본 폴더)
        static string AskFolder(string start)
        {
            var gs = new GetString();
            gs.SetCommandPrompt("STL 저장 폴더");
            gs.SetDefaultString(start);
            gs.AcceptNothing(true);
            var r = gs.GetLiteralString();
            if (r == GetResult.Nothing) return start;
            if (r != GetResult.String) return null;
            var p = gs.StringResult().Trim().Trim((char)34);
            return string.IsNullOrEmpty(p) ? start : p;
        }

        static Mesh BrepMesh(Brep b, MeshingParameters mp)
        {
            var arr = Mesh.CreateFromBrep(b, mp);
            if (arr == null || arr.Length == 0) return null;
            var m = new Mesh();
            foreach (var x in arr) m.Append(x);
            m.Weld(Math.PI);
            m.Normals.ComputeNormals();
            return m;
        }

        static int EnsureLayer(RhinoDoc doc, string name, int parentIndex, Color color)
        {
            string full = parentIndex >= 0 ? doc.Layers[parentIndex].FullPath + "::" + name : name;
            int idx = doc.Layers.FindByFullPath(full, -1);
            if (idx >= 0) return idx;
            var l = new Layer { Name = name, Color = color };
            if (parentIndex >= 0) l.ParentLayerId = doc.Layers[parentIndex].Id;
            return doc.Layers.Add(l);
        }
    }
}
