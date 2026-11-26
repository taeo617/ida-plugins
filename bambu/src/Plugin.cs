using System.Runtime.InteropServices;
using Rhino.PlugIns;

// 플러그인 고유 ID (바꾸지 말 것)
[assembly: Guid("4e8b2a71-c3d6-4f95-a1e0-7b9c5d2f8a36")]
[assembly: PlugInDescription(DescriptionType.Organization, "found/Founded")]
[assembly: PlugInDescription(DescriptionType.WebSite, "https://foundfounded.com")]

namespace IDA.Bambu
{
    public class BambuPlugIn : PlugIn
    {
        public BambuPlugIn() { Instance = this; }
        public static BambuPlugIn Instance { get; private set; }
    }
}
