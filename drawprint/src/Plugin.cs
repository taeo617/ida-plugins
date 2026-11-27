using System.Runtime.InteropServices;
using Rhino.PlugIns;

// 플러그인 고유 ID (바꾸지 말 것)
[assembly: Guid(IDA.DrawPrint.Ver.Id)]
[assembly: PlugInDescription(DescriptionType.Organization, "found/Founded")]
[assembly: PlugInDescription(DescriptionType.WebSite, "https://foundfounded.com")]

namespace IDA.DrawPrint
{
    // 시험 빌드는 Sfx·Id만 바꿔서 Rhino를 끄지 않고 다시 불러옴 (배포본은 그대로)
    static class Ver
    {
        public const string Sfx = "";
        public const string Id = "3d0f6b52-9a1e-4c7d-b8e4-6f2a9c1d7e35";
    }

    public class DrawPrintPlugIn : PlugIn
    {
        public DrawPrintPlugIn() { Instance = this; }
        public static DrawPrintPlugIn Instance { get; private set; }
    }
}
