using System.Runtime.InteropServices;
using Rhino.PlugIns;

// Rhino identifies a plug-in by this Guid. Never change it after release.
[assembly: Guid("64449869-33b7-4be6-95be-b056357cb42a")]
[assembly: PlugInDescription(DescriptionType.Organization, "found/Founded")]
[assembly: PlugInDescription(DescriptionType.WebSite, "https://foundfounded.com")]

namespace ImagePerf
{
  public class ImagePerfPlugin : PlugIn
  {
    public ImagePerfPlugin() { Instance = this; }
    public static ImagePerfPlugin Instance { get; private set; }
  }
}
