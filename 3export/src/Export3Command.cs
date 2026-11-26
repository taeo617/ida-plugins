using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;

namespace IDA.Export3
{
    // Export3: 선택한 객체 → <폴더>\<제품명>_FOUNDFOUNDED\ 에 (stp)(3ds)(fbx) + 이미지 소스
    //   대화상자 없이 쓰려면:  -Export3 "제품명" "C:\저장\폴더"
    public class Export3Command : Command
    {
        public override string EnglishName => "Export3";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var go = new GetObject();
            go.SetCommandPrompt("전시사용으로 내보낼 객체를 선택하세요");
            go.GroupSelect = true;
            go.SubObjectSelect = false;
            go.EnablePreSelect(true, true);
            go.GetMultiple(1, 0);
            if (go.CommandResult() != Result.Success) return go.CommandResult();
            var objs = go.Objects().Select(r => r.Object()).Where(o => o != null).ToList();

            var settings = Export3PlugIn.Instance.Settings;
            string desktop = System.Environment.GetFolderPath(System.Environment.SpecialFolder.DesktopDirectory);
            string folder = settings.GetString("folder", desktop);
            if (!Directory.Exists(folder)) folder = desktop;
            string name = DefaultName(doc);

            if (mode == RunMode.Interactive)
            {
                var dlg = new ExportDialog(name, folder);
                if (!dlg.ShowModal(Rhino.UI.RhinoEtoApp.MainWindow)) return Result.Cancel;
                name = dlg.ProductName; folder = dlg.Folder;
            }
            else
            {
                var gs = new GetString();
                gs.SetCommandPrompt("제품명");
                if (!string.IsNullOrEmpty(name)) gs.SetDefaultString(name);
                gs.AcceptNothing(true);
                if (gs.GetLiteralString() == GetResult.Cancel) return Result.Cancel;
                if (gs.CommandResult() == Result.Success && !string.IsNullOrWhiteSpace(gs.StringResult())) name = gs.StringResult();
                var gf = new GetString();
                gf.SetCommandPrompt("저장 폴더");
                gf.SetDefaultString(folder);
                gf.AcceptNothing(true);
                if (gf.GetLiteralString() == GetResult.Cancel) return Result.Cancel;
                if (gf.CommandResult() == Result.Success && !string.IsNullOrWhiteSpace(gf.StringResult())) folder = gf.StringResult();
            }

            name = Sanitize(name);
            folder = (folder ?? "").Trim().Trim('"');
            if (string.IsNullOrEmpty(name)) { RhinoApp.WriteLine("제품명이 비어 있어요."); return Result.Failure; }
            if (string.IsNullOrEmpty(folder)) { RhinoApp.WriteLine("저장 폴더가 비어 있어요."); return Result.Failure; }
            try { Directory.CreateDirectory(folder); }
            catch (Exception e) { RhinoApp.WriteLine("폴더를 만들 수 없어요: " + e.Message); return Result.Failure; }
            settings.SetString("folder", folder);

            try
            {
                RhinoApp.WriteLine("3export: 내보내는 중… (객체 {0}개)", objs.Count);
                var r = Exporter.Run(doc, objs, name, folder, s => { RhinoApp.WriteLine("  " + s); RhinoApp.Wait(); });
                RhinoApp.WriteLine("3export 완료: " + r.Folder);
                foreach (var line in r.Summary) RhinoApp.WriteLine("  " + line);
                if (mode == RunMode.Interactive)
                {
                    try { Process.Start("explorer.exe", "\"" + r.Folder + "\""); } catch { /* 폴더 열기 실패는 무시 */ }
                }
                return Result.Success;
            }
            catch (Exception e)
            {
                RhinoApp.WriteLine("3export 실패: " + e.Message);
                return Result.Failure;
            }
        }

        // "New best_1_TY.3dm" → "New best_1" (끝의 담당자 이니셜 제거)
        static string DefaultName(RhinoDoc doc)
        {
            var n = Path.GetFileNameWithoutExtension(doc.Name ?? "") ?? "";
            n = Regex.Replace(n, "_[A-Z]{2,3}$", "");
            return n.Trim();
        }

        static string Sanitize(string s)
        {
            s = (s ?? "").Trim().Trim('"');
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c.ToString(), "");
            return s.Trim();
        }
    }
}
