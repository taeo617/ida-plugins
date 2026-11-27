// Test setup: freeform test geometry next to the example plate, saved as test_perf.3dm (copy, never the user's file).
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using Rhino;
using Rhino.Geometry;
using Rhino.DocObjects;

var log = new StringBuilder();
var dev = @"C:\Users\User\Documents\Rhino Agent\ida-plugins\imageperf\dev";
var doc = RhinoDoc.ActiveDoc;
try
{
  var g = new Guid("64449869-33b7-4be6-95be-b056357cb42a");
  log.AppendLine("installed: " + string.Join(",", Rhino.PlugIns.PlugIn.GetInstalledPlugIns().Where(kv => kv.Key == g).Select(kv => kv.Value)));
  log.AppendLine("cmd: " + string.Join(",", Rhino.Commands.Command.GetCommandNames(true, false).Where(c => c.StartsWith("Image"))));

  if (doc.Objects.FindByLayer("TEST_freeform") == null || doc.Objects.FindByLayer("TEST_freeform").Length == 0)
  {
    var pts = new List<Point3d>();
    int n = 6;
    for (int i = 0; i < n; i++) for (int j = 0; j < n; j++)
    {
      double x = 150 + i * 24, y = -10 + j * 18;
      double cx = (i - 2.5) / 2.5, cy = (j - 2.5) / 2.5;
      double z = 28 * (1 - 0.55 * cx * cx - 0.75 * cy * cy) + 6 * Math.Sin(i * 1.3) * cx;
      pts.Add(new Point3d(x, y, z));
    }
    var srf = NurbsSurface.CreateFromPoints(pts, n, n, 3, 3);
    int li = doc.Layers.Add(new Layer { Name = "TEST_freeform", Color = System.Drawing.Color.FromArgb(70, 110, 160) });
    doc.Objects.AddSurface(srf, new ObjectAttributes { LayerIndex = li, Name = "freeform_open" });
    var srf2 = (NurbsSurface)srf.Duplicate();
    srf2.Translate(new Vector3d(0, 120, 0));
    var solids = Brep.CreateOffsetBrep(srf2.ToBrep(), -2.5, true, false, doc.ModelAbsoluteTolerance, out _, out _);
    doc.Objects.AddBrep(solids[0], new ObjectAttributes { LayerIndex = li, Name = "freeform_shell" });
    log.AppendLine($"shell solid={solids[0].IsSolid} faces={solids[0].Faces.Count}");
  }
  doc.Views.Redraw();
  var path = Path.Combine(dev, "test_perf.3dm");
  bool ok = doc.WriteFile(path, new Rhino.FileIO.FileWriteOptions());
  log.AppendLine("saved copy " + ok);
}
catch (Exception e) { log.AppendLine("ERR " + e); }
File.WriteAllText(Path.Combine(dev, "out", "log.txt"), log.ToString());
