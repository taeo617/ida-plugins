using System.Runtime.InteropServices;
using Rhino.PlugIns;

// 플러그인 고유 ID (바꾸지 말 것)
[assembly: Guid(IDA.A4Draw.Ver.Id)]
[assembly: PlugInDescription(DescriptionType.Organization, "found/Founded")]
[assembly: PlugInDescription(DescriptionType.WebSite, "https://foundfounded.com")]

namespace IDA.A4Draw
{
    // 시험 빌드는 Sfx·Id만 바꿔서 Rhino를 끄지 않고 다시 불러옴 (배포본은 그대로)
    static class Ver
    {
        public const string Sfx = "";
        public const string Id = "ef6c574e-225d-468c-8a84-5d94c621709e";
    }

    public class A4DrawPlugIn : PlugIn
    {
        public A4DrawPlugIn() { Instance = this; }
        public static A4DrawPlugIn Instance { get; private set; }
    }
}
