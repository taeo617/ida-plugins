// Runs -ImagePerf on the three test objects in test_perf.3dm (a copy) and captures viewports to dev\out.
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Diagnostics;
using Rhino;
using Rhino.Geometry;
using Rhino.DocObjects;
using Rhino.Display;

var log = new StringBuilder();
var dev = @"C:\Users\User\Documents\Rhino Agent\ida-plugins\imageperf\dev";
var outDir = Path.Combine(dev, "out");
var doc = RhinoDoc.ActiveDoc;
var blob = Path.Combine(dev, "test_blob_ID.png");
var logo = @"C:\Users\User\Documents\Rhino Agent\IDA-logo.png";

RhinoObject Find(string layer, string name = null) =>
  doc.Objects.FindByLayer(layer).FirstOrDefault(o => name == null || o.Attributes.Name == name);

void Capture(string file, string viewName, BoundingBox bb, bool perspective, string mode = "Shaded")
{
  var view = doc.Views.Find(viewName, false);
  var vp = view.ActiveViewport;
  vp.DisplayMode = DisplayModeDescription.GetDisplayModes().First(m => m.EnglishName == mode);
  if (perspective)
  {
    var c = bb.Center;
    var d = bb.Diagonal.Length;
    vp.SetCameraLocations(c, c + new Vector3d(-0.55, -1.0, 0.85) * d * 1.1);
  }
  vp.ZoomBoundingBox(bb);
  view.Redraw();
  var vc = new ViewCapture { Width = 1400, Height = 950, ScaleScreenItems = false, DrawAxes = false, DrawGrid = false, DrawGridAxes = false, TransparentBackground = false };
  object bmp = vc.CaptureToBitmap(view);
  bmp.GetType().GetMethod("Save", new[] { typeof(string) }).Invoke(bmp, new object[] { Path.Combine(outDir, file) });
  ((IDisposable)bmp).Dispose();
}

string Report(string layer, string name)
{
  var o = Find(layer, name);
  var b = o?.Geometry as Brep;
  if (b == null) return $"{name}: not a brep ({o?.Geometry?.GetType().Name})";
  int inner = b.Loops.Count(l => l.LoopType == BrepLoopType.Inner);
  return $"{name ?? layer}: faces={b.Faces.Count} innerLoops={inner} solid={b.IsSolid} valid={b.IsValid}";
}

bool Run(RhinoObject o, string opts)
{
  doc.Objects.UnselectAll();
  o.Select(true);
  var sw = Stopwatch.StartNew();
  bool ok = RhinoApp.RunScript("_-ImagePerf " + opts + " _Enter", true);
  log.AppendLine($"  run ok={ok} {sw.ElapsedMilliseconds} ms");
  doc.Objects.UnselectAll();
  return ok;
}

try
{
  log.AppendLine("cmd registered: " + string.Join(",", Rhino.Commands.Command.GetCommandNames(true, false).Where(c => c.StartsWith("Image"))));

  // 1) Example plate (closed solid, 3 mm) — halftone, hex, auto → boolean through the plate.
  var plate = Find("앞판");
  log.AppendLine("T1 plate");
  Run(plate, $"Image \"{blob}\" Layout Hex Spacing 3 MinSize 0.5 MaxSize 2.5 Margin 3 MinWall 0.5 Shape Circle Angle 0 Tone Halftone Cutoff 0.06 Gamma 1 Invert=No Mapping Projection Fit Contain Rotate 0 FlipX=No Cut Auto KeepOriginal=No");
  log.AppendLine("  " + Report("앞판", null));
  var pbb = Find("앞판").Geometry.GetBoundingBox(true);
  Capture("t1_plate_top.png", "Top", pbb, false);
  Capture("t1_plate_persp.png", "Perspective", pbb, true);

  // Untouched copy of the open surface for test 4.
  var g0 = Find("TEST_freeform", "freeform_open").Geometry;
  var copyGeo = g0 is Surface s0 ? s0.ToBrep() : ((Brep)g0).DuplicateBrep();
  copyGeo.Translate(new Vector3d(140, 0, 0));

  // 2) Open double-curved NURBS surface — halftone, hex, projection → trim.
  var open = Find("TEST_freeform", "freeform_open");
  log.AppendLine("T2 freeform open");
  Run(open, $"Image \"{blob}\" Layout Hex Spacing 3.2 MinSize 0.4 MaxSize 2.7 Margin 2 MinWall 0.5 Shape Circle Tone Halftone Cutoff 0.06 Mapping Projection Fit Contain Cut Auto Invert=No FlipX=No Rotate 0 KeepOriginal=No");
  log.AppendLine("  " + Report("TEST_freeform", "freeform_open"));
  var obb = Find("TEST_freeform", "freeform_open").Geometry.GetBoundingBox(true);
  Capture("t2_open_persp.png", "Perspective", obb, true);
  Capture("t2_open_top.png", "Top", obb, false);

  // 3) Freeform shell (closed solid, 2.5 mm) — IDA logo, hexagon holes, threshold, boolean.
  var shell = Find("TEST_freeform", "freeform_shell");
  log.AppendLine("T3 freeform shell");
  Run(shell, $"Image \"{logo}\" Layout Hex Spacing 3.5 MinSize 0.5 MaxSize 2.8 Margin 3 MinWall 0.6 Shape Hexagon Tone Halftone Cutoff 0.06 Invert=No Mapping Projection Fit Contain Cut Auto Invert=No FlipX=No Rotate 0 KeepOriginal=No");
  log.AppendLine("  " + Report("TEST_freeform", "freeform_shell"));
  var sbb = Find("TEST_freeform", "freeform_shell").Geometry.GetBoundingBox(true);
  Capture("t3_shell_persp.png", "Perspective", sbb, true);
  Capture("t3_shell_top.png", "Top", sbb, false);

  // 4) Copy of the open surface — even (blue-noise) spread, slots, UV mapping, trim.
  var cid = doc.Objects.AddBrep(copyGeo, new ObjectAttributes { LayerIndex = doc.Layers.FindName("TEST_freeform").Index, Name = "freeform_even" });
  log.AppendLine("T4 even + slot + UV");
  Run(doc.Objects.FindId(cid), $"Image \"{logo}\" Layout Even Spacing 4 MinSize 0.6 MaxSize 2.2 Margin 2 MinWall 0.5 Shape Slot SlotRatio 2.2 Angle 30 Tone Threshold Threshold 0.5 Mapping UV Fit Cover Cut Trim Invert=Yes FlipX=No Rotate 0 KeepOriginal=No");
  log.AppendLine("  " + Report("TEST_freeform", "freeform_even"));
  var ebb = Find("TEST_freeform", "freeform_even").Geometry.GetBoundingBox(true);
  Capture("t4_even_persp.png", "Perspective", ebb, true);
  Capture("t4_even_top.png", "Top", ebb, false);
  Capture("t1_plate_close.png", "Perspective", new BoundingBox(new Point3d(20, 20, 0), new Point3d(50, 45, 3)), true);

  doc.WriteFile(Path.Combine(outDir, "result.3dm"), new Rhino.FileIO.FileWriteOptions());
}
catch (Exception e) { log.AppendLine("ERR " + e); }
File.WriteAllText(Path.Combine(outDir, "log.txt"), log.ToString());
