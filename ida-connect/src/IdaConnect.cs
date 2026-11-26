using System;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using Rhino.PlugIns;

// 플러그인 고유 ID (바꾸지 말 것)
[assembly: Guid("b2d7c4e1-9a35-4f6b-8c02-5e1f3a7d9b64")]
[assembly: PlugInDescription(DescriptionType.Organization, "found/Founded")]

namespace IDA.Connect
{
    // Rhino가 켜지면 RhinoMCP 서버(mcpstart)를 자동으로 켭니다.
    // → 파일을 더블클릭해서 Rhino를 열어도 산업디자이너 에이전트와 바로 연결됨.
    // 이미 다른 Rhino가 MCP를 쓰고 있으면(포트 사용 중) 건드리지 않습니다.
    public class IdaConnectPlugIn : PlugIn
    {
        public const int Port = 1999;
        public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;

        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            RhinoApp.Idle += OnFirstIdle;
            return LoadReturnCode.Success;
        }

        void OnFirstIdle(object sender, EventArgs e)
        {
            RhinoApp.Idle -= OnFirstIdle;
            if (Settings.GetBool("disabled", false)) return;
            Start(quiet: true);
        }

        public static bool PortInUse()
        {
            try
            {
                using (var c = new TcpClient())
                {
                    var ar = c.BeginConnect("127.0.0.1", Port, null, null);
                    bool ok = ar.AsyncWaitHandle.WaitOne(300) && c.Connected;
                    if (ok) c.EndConnect(ar);
                    return ok;
                }
            }
            catch { return false; }
        }

        public static bool Start(bool quiet)
        {
            if (PortInUse())
            {
                if (!quiet) RhinoApp.WriteLine("산업디자이너 에이전트: 이미 연결돼 있어요 (포트 {0}).", Port);
                return true;
            }
            bool ok = RhinoApp.RunScript("_mcpstart", false);
            RhinoApp.WriteLine(ok ? "산업디자이너 에이전트: Rhino 연결을 켰어요." : "산업디자이너 에이전트: RhinoMCP를 켜지 못했어요. RhinoMCP 플러그인이 설치돼 있는지 확인해 주세요.");
            return ok;
        }
    }

    // IdaConnect: 수동으로 다시 연결 / IdaConnect Off: 자동 연결 끄기
    public class IdaConnectCommand : Command
    {
        public override string EnglishName => "IdaConnect";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var go = new Rhino.Input.Custom.GetOption();
            go.SetCommandPrompt("산업디자이너 에이전트 연결");
            var plugin = (IdaConnectPlugIn)PlugIn.Find(new Guid("b2d7c4e1-9a35-4f6b-8c02-5e1f3a7d9b64"));
            var auto = new Rhino.Input.Custom.OptionToggle(!plugin.Settings.GetBool("disabled", false), "Off", "On");
            go.AddOptionToggle("AutoConnect", ref auto);
            go.AcceptNothing(true);
            while (true)
            {
                var r = go.Get();
                if (r == Rhino.Input.GetResult.Option) continue;
                if (r == Rhino.Input.GetResult.Cancel) return Result.Cancel;
                break;
            }
            plugin.Settings.SetBool("disabled", !auto.CurrentValue);
            if (auto.CurrentValue) IdaConnectPlugIn.Start(quiet: false);
            else RhinoApp.WriteLine("자동 연결을 껐어요. 다시 켜려면 IdaConnect에서 AutoConnect=On.");
            return Result.Success;
        }
    }
}
