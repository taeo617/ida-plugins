using Rhino.PlugIns;

namespace NPatch
{
  public class NPatchPlugin : PlugIn
  {
    public NPatchPlugin() { Instance = this; }
    public static NPatchPlugin Instance { get; private set; }
  }
}
