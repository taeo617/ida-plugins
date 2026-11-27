using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;

namespace IDA.DrawPrint
{
    // DrawPrint: 선택한 객체를 FOUNDFOUNDED 인쇄 데이터 규칙으로 일러스트 .ai 저장 (전달용, mm 1:1)
    //   인쇄(면) · 가이드(치수·글자) · 도면(외곽선, 리치블랙) · 배경색(면) 레이어, CMYK
    //   파일 이름: <프로젝트명>_인쇄 데이터_FOUNDFOUNDED.ai  (같은 이름이 있으면 _2, _3 …)
    // 검토용 A4 축척 도면은 a4draw(A4Drawing)
    public class DrawPrintCommand : Command
    {
        public override string EnglishName => "DrawPrint" + Ver.Sfx;

        static readonly string[] ViewNames = { "Auto", "Top", "Front", "Right", "Back", "Left", "Bottom" };
        static readonly string[] ArtboardNames = { "Auto", "Fit", "A4" };
        static readonly double[] Beige = { 17, 16, 20, 0 }; // 문구 플레이트 배경 (샘플 기준)

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var st = DrawPrintPlugIn.Instance.Settings;
            int view = Math.Max(0, Array.IndexOf(ViewNames, st.GetString("view", "Auto")));
            int artboard = Math.Max(0, Array.IndexOf(ArtboardNames, st.GetString("artboard", "Auto")));
            var optBg = new OptionToggle(st.GetBool("background", false), "Off", "On");
            var optDim = new OptionToggle(st.GetBool("dimension", true), "Off", "On");
            string name = null, outDir = null;

            var go = new GetObject();
            go.SetCommandPrompt("인쇄 데이터로 내보낼 객체를 고르세요 (Enter = 보이는 전체)");
            go.GeometryFilter = ObjectType.Curve | ObjectType.Hatch | ObjectType.Annotation | ObjectType.TextDot |
                                ObjectType.Surface | ObjectType.PolysrfFilter | ObjectType.Brep | ObjectType.Extrusion |
                                ObjectType.Mesh | ObjectType.SubD;
            go.SubObjectSelect = false;
            go.GroupSelect = true;
            go.EnablePreSelect(true, true);
            go.AcceptNothing(true);
            bool havePre = false;
            List<RhinoObject> objs = null;
            while (true)
            {
                go.ClearCommandOptions();
                int viewOpt = go.AddOptionList("View", ViewNames, view);
                int abOpt = go.AddOptionList("Artboard", ArtboardNames, artboard);
                go.AddOptionToggle("Background", ref optBg);
                go.AddOptionToggle("Dimension", ref optDim);
                int nameOpt = go.AddOption("Name");
                int folderOpt = go.AddOption("Folder");
                var r = go.GetMultiple(1, 0);
                if (r == GetResult.Option)
                {
                    go.EnablePreSelect(false, true);
                    int oi = go.OptionIndex();
                    if (oi == viewOpt) view = go.Option().CurrentListOptionIndex;
                    else if (oi == abOpt) artboard = go.Option().CurrentListOptionIndex;
                    else if (oi == nameOpt) name = AskString("프로젝트명 (파일 이름 앞부분)", name ?? DefaultName(doc)) ?? name;
                    else if (oi == folderOpt)
                    {
                        var picked = mode == RunMode.Interactive ? PickFolder(DefaultFolder(doc, st)) : AskString("저장 폴더", DefaultFolder(doc, st));
                        if (picked != null) { outDir = picked.Trim().Trim('"'); RhinoApp.WriteLine("저장 폴더: " + outDir); }
                    }
                    continue;
                }
                if (r == GetResult.Nothing)
                {
                    if (!havePre)
                        objs = doc.Objects.GetObjectList(new ObjectEnumeratorSettings { VisibleFilter = true, HiddenObjects = false, LockedObjects = true, NormalObjects = true })
                                  .Where(o => !(o is InstanceObject)).ToList();
                    break;
                }
                if (r != GetResult.Object) return go.CommandResult();
                if (go.ObjectsWerePreselected && !havePre)
                {
                    havePre = true;
                    go.EnablePreSelect(false, true);
                    go.EnableClearObjectsOnEntry(false);
                    go.DeselectAllBeforePostSelect = false;
                    go.EnableUnselectObjectsOnExit(false);
                    go.SetCommandPrompt("옵션을 바꾸거나 Enter");
                    continue;
                }
                break;
            }
            if (objs == null) objs = Enumerable.Range(0, go.ObjectCount).Select(i => go.Object(i).Object()).Where(o => o != null).ToList();
            st.SetString("view", ViewNames[view]); st.SetString("artboard", ArtboardNames[artboard]);
            st.SetBool("background", optBg.CurrentValue); st.SetBool("dimension", optDim.CurrentValue);
            if (objs.Count == 0) { RhinoApp.WriteLine("내보낼 객체가 없어요."); return Result.Nothing; }

            if (string.IsNullOrWhiteSpace(name)) name = AskString("프로젝트명 (파일 이름 앞부분)", DefaultName(doc)) ?? DefaultName(doc);
            name = string.Join("_", name.Split(Path.GetInvalidFileNameChars())).Trim();
            if (name.Length == 0) name = "프로젝트명";

            // 규칙(config.json) + 이번 실행 옵션
            var cfg = MiniJson.Obj(MiniJson.Parse(Resource("config.json")));
            cfg["view"] = ViewNames[view].ToLowerInvariant();
            cfg["artboard"] = ArtboardNames[artboard].ToLowerInvariant();
            cfg["auto_dimension"] = optDim.CurrentValue;
            cfg["solid_background"] = optBg.CurrentValue ? Beige.Select(x => (object)x).ToList() : null;

            RhinoApp.WriteLine($"인쇄 데이터: 객체 {objs.Count}개 정리 중…");
            var col = new Collector(doc, cfg);
            List<object> items, texts; double[] bbox;
            try { (items, texts, bbox) = col.Run(objs, (string)cfg["view"]); }
            catch (Exception e) { RhinoApp.WriteLine("인쇄 데이터: 정리하다 멈췄어요 — " + e.Message); return Result.Failure; }
            foreach (var w in col.Warn.Take(10)) RhinoApp.WriteLine("  · " + w);
            if (items.Count == 0 && texts.Count == 0) { RhinoApp.WriteLine("내보낼 선·면이 없어요."); return Result.Nothing; }
            int nShapes = items.Sum(it => ((List<object>)((Dictionary<string, object>)it)["shapes"]).Count);
            RhinoApp.WriteLine($"  · 그룹 {items.Count} / 도형 {nShapes} / 글자 {texts.Count}");

            string folder = outDir ?? DefaultFolder(doc, st);
            Directory.CreateDirectory(folder);
            st.SetString("folder", folder);
            string aiPath = Unique(Path.Combine(folder, name + MiniJson.S(cfg, "file_suffix", "") + ".ai"));

            var data = new Dictionary<string, object>
            {
                { "project", name }, { "source", doc.Path ?? doc.Name ?? "" }, { "config", cfg },
                { "bbox", bbox.Select(x => (object)x).ToList() }, { "items", items }, { "texts", texts },
                { "save_path", aiPath.Replace("\\", "/") }, { "created", DateTime.Now.ToString("s") },
            };
            // 일러스트로 넘기는 작업 파일 — 사용자 문서 폴더가 아니라 임시 폴더에 둠
            string dataDir = Path.Combine(Path.GetTempPath(), "drawprint");
            Directory.CreateDirectory(dataDir);
            string dataPath = Path.Combine(dataDir, "last_export.json");
            File.WriteAllText(dataPath, MiniJson.Write(data), new UTF8Encoding(false));

            var res = ToIllustrator(dataPath);
            RhinoApp.WriteLine("인쇄 데이터: " + res);
            return File.Exists(aiPath) ? Result.Success : Result.Failure;
        }

        // ---------- 일러스트레이터: 검증된 빌더(ai_build.jsx)에 데이터 경로만 넘겨 실행
        static string ToIllustrator(string dataPath)
        {
            var t = Type.GetTypeFromProgID("Illustrator.Application");
            if (t == null) return "이 PC에 일러스트레이터가 없어요. 데이터만 저장했어요: " + dataPath;
            RhinoApp.WriteLine("인쇄 데이터: 일러스트레이터로 보내는 중… (처음엔 일러스트가 켜지느라 조금 걸려요)");
            string js = "var DATA_PATH = \"" + dataPath.Replace("\\", "/").Replace("\"", "\\\"") + "\";\n" + Resource("ai_build.jsx");
            object app = null;
            try
            {
                app = Activator.CreateInstance(t);
                var r = t.InvokeMember("DoJavaScript", BindingFlags.InvokeMethod, null, app, new object[] { js });
                return Convert.ToString(r);
            }
            catch (Exception e)
            {
                var inner = e is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException.Message : e.Message;
                return "일러스트레이터 변환 실패 (" + inner + ")";
            }
            finally
            {
                if (app != null) try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(app); } catch { }
            }
        }

        static string Resource(string name)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            using (var r = new StreamReader(s, Encoding.UTF8))
                return r.ReadToEnd();
        }

        static string Unique(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path), b = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            int i = 2;
            while (File.Exists(Path.Combine(dir, $"{b}_{i}{ext}"))) i++;
            return Path.Combine(dir, $"{b}_{i}{ext}");
        }

        static string DefaultName(RhinoDoc doc) => string.IsNullOrEmpty(doc.Name) ? "프로젝트명" : Path.GetFileNameWithoutExtension(doc.Name);

        // 기본 저장 위치: 3dm 파일이 있는 폴더 (저장 안 된 문서는 바탕화면)
        static string DefaultFolder(RhinoDoc doc, PersistentSettings st)
        {
            if (!string.IsNullOrEmpty(doc.Path)) return Path.GetDirectoryName(doc.Path);
            var last = st.GetString("folder", "");
            if (!string.IsNullOrEmpty(last) && Directory.Exists(last)) return last;
            return System.Environment.GetFolderPath(System.Environment.SpecialFolder.DesktopDirectory);
        }

        static string PickFolder(string start)
        {
            var dlg = new Eto.Forms.SelectFolderDialog { Title = "인쇄 데이터(AI)를 저장할 폴더를 고르세요" };
            if (!string.IsNullOrEmpty(start) && Directory.Exists(start)) dlg.Directory = start;
            return dlg.ShowDialog(Rhino.UI.RhinoEtoApp.MainWindow) == Eto.Forms.DialogResult.Ok ? dlg.Directory : null;
        }

        static string AskString(string prompt, string def)
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
