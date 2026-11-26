using Eto.Drawing;
using Eto.Forms;

namespace IDA.Export3
{
    // 제품명 + 저장 폴더를 받는 작은 창
    public class ExportDialog : Dialog<bool>
    {
        readonly TextBox nameBox = new TextBox();
        readonly TextBox folderBox = new TextBox();
        readonly Label preview = new Label { TextColor = Colors.Gray };

        public string ProductName => nameBox.Text;
        public string Folder => folderBox.Text;

        public ExportDialog(string name, string folder)
        {
            Title = "3export · 전시사 전달 파일";
            Padding = new Padding(16);
            Resizable = false;
            MinimumSize = new Size(460, 0);
            nameBox.Text = name ?? "";
            folderBox.Text = folder ?? "";
            nameBox.TextChanged += (s, e) => UpdatePreview();
            folderBox.TextChanged += (s, e) => UpdatePreview();
            UpdatePreview();

            var browse = new Button { Text = "찾아보기…" };
            browse.Click += (s, e) =>
            {
                var d = new SelectFolderDialog { Title = "저장할 폴더", Directory = folderBox.Text };
                if (d.ShowDialog(this) == DialogResult.Ok) folderBox.Text = d.Directory;
            };
            var ok = new Button { Text = "내보내기" };
            ok.Click += (s, e) => { if (!string.IsNullOrWhiteSpace(nameBox.Text)) Close(true); else nameBox.Focus(); };
            var cancel = new Button { Text = "취소" };
            cancel.Click += (s, e) => Close(false);
            DefaultButton = ok;
            AbortButton = cancel;

            Content = new TableLayout
            {
                Spacing = new Size(8, 8),
                Rows =
                {
                    new Label { Text = "제품명" },
                    nameBox,
                    new Label { Text = "저장 폴더" },
                    new TableLayout { Spacing = new Size(6, 0), Rows = { new TableRow(new TableCell(folderBox, true), browse) } },
                    preview,
                    new TableLayout { Spacing = new Size(6, 0), Rows = { new TableRow(null, cancel, ok) } },
                },
            };
        }

        void UpdatePreview()
        {
            var n = (nameBox.Text ?? "").Trim();
            preview.Text = n.Length == 0 ? "제품명을 입력하세요"
                : "→ " + System.IO.Path.Combine(folderBox.Text ?? "", n + Exporter.Suffix) + "\n   (stp)·(3ds)·(fbx)" + n + Exporter.Suffix + " + 이미지 소스";
        }
    }
}
