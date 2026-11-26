using System.Runtime.InteropServices;
using Rhino;
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

        // Rhino 명령어는 숫자로 시작할 수 없어서 실제 명령어는 Export3.
        // 플러그인 이름 그대로 '3export'로도 칠 수 있게 Rhino 별칭(Alias)을 만들어 둠.
        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            try
            {
                if (!Rhino.ApplicationSettings.CommandAliasList.IsAlias("3export"))
                    Rhino.ApplicationSettings.CommandAliasList.Add("3export", "! _Export3");
            }
            catch { /* 별칭을 못 만들어도 Export3은 그대로 동작 */ }
            return LoadReturnCode.Success;
        }
    }
}
