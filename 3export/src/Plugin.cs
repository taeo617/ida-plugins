using System.Runtime.InteropServices;
using Rhino.PlugIns;

// 플러그인 고유 ID (바꾸지 말 것 — 업데이트할 때 같은 플러그인으로 인식)
[assembly: Guid("6a3f0e2c-4b1d-4c8e-9f27-3e5b8d1a7c40")]
[assembly: PlugInDescription(DescriptionType.Organization, "found/Founded")]
[assembly: PlugInDescription(DescriptionType.WebSite, "https://foundfounded.com")]

namespace IDA.Export3
{
    public class Export3PlugIn : PlugIn
    {
        public Export3PlugIn() { Instance = this; }
        public static Export3PlugIn Instance { get; private set; }
        public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;
    }
}
